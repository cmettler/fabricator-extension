// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// <c>db.dbo.fabricator_session_tag(key, value)</c> — sets a session-context key on the transaction's provider
/// connection and returns the identifiers Fabric's monitoring records for it, so a run or a model can be
/// correlated to its statements afterwards.
/// </summary>
/// <remarks>
/// <para><b>Why this must be an extension function and cannot be user SQL.</b> The obvious spelling —
/// <c>SELECT fabricator_exec('db', 'EXEC sp_set_session_context …')</c> — does not work, and fails SILENTLY.
/// <c>fabricator_exec</c> runs <see cref="AmbientTransaction.JoinOnly"/>: it joins the transaction's pinned
/// connection only if one ALREADY exists, else it takes a fresh connection it deliberately does not retain
/// (nothing would ever commit it). A provider connection is pinned lazily on the first WRITE, so at the moment a
/// dbt <c>pre_hook</c> runs there is nothing to join — the tag lands on a pooled connection that is handed back
/// immediately, and every later statement runs somewhere else. Measured: two consecutive calls inside an explicit
/// <c>BEGIN</c> reported different <c>@@SPID</c>s and the value read back as NULL.</para>
/// <para>This function instead goes through <c>BeginWrite()</c> WITHOUT the join-only restriction, which pins the
/// transaction's connection. Everything the transaction does afterwards reuses that same connection, so the tag
/// actually applies to the work. That pinning is the whole reason the function exists; it is not sugar over
/// <c>fabricator_exec</c>.</para>
/// <para><b>It only makes sense inside an EXPLICIT transaction</b>, and it says so rather than misleading you.
/// In autocommit each statement is its own transaction, so the connection this pins is committed and released
/// before the next statement runs — the tag would be set and immediately discarded. The function therefore
/// raises when it is called outside an explicit transaction, because a tag that silently fails to stick is worse
/// than no tag at all.</para>
/// <para><b>What it returns, and why those columns.</b> <c>connection_id</c> is the key that joins
/// <c>queryinsights.exec_sessions_history</c> to <c>exec_requests_history</c> — and unlike <c>session_id</c>
/// (a reused spid) it is a GUID, so correlation is unambiguous across a day of history.
/// <c>dist_statement_id</c> is the Capacity Metrics app's <c>Operation Id</c>, i.e. the hop from a statement to
/// its CU seconds. Returning them makes the correlation verifiable at the point of tagging instead of requiring
/// a <c>LIKE</c> scan over 30 days of command text — and it makes the failure mode observable: two calls that
/// report different <c>connection_id</c>s did not share a session.</para>
/// <para>Full measurements, the three tagging vectors and their trade-offs:
/// docs/consumption-monitoring.md §2.4a.</para>
/// </remarks>
internal sealed class SqlServerSessionTagFunction : ICatalogTableFunction
{
    /// <summary>The catalog-bound name. Declared in <c>dbo</c>, like the other provider-authored functions.</summary>
    internal const string FunctionName = "fabricator_session_tag";

    internal static bool Is(string name) =>
        string.Equals(name, FunctionName, StringComparison.OrdinalIgnoreCase);

    private readonly SqlServerCatalog _catalog;

    internal SqlServerSessionTagFunction(SqlServerCatalog catalog) => _catalog = catalog;

    public string SchemaName => "dbo";

    public string Name => FunctionName;

    /// <summary>
    /// The session-context key and its value. Both positional and required: a tag with a defaulted key would
    /// invite two callers to overwrite each other's correlation id without noticing.
    /// </summary>
    public Schema Parameters { get; } = new(new[]
    {
        new Field("key", StringType.Default, nullable: false),
        new Field("value", StringType.Default, nullable: false),
    }, null);

    internal static Schema Columns { get; } = new(new[]
    {
        new Field("connection_id", StringType.Default, nullable: true),
        new Field("session_id", Int32Type.Default, nullable: true),
        new Field("dist_statement_id", StringType.Default, nullable: true),
        new Field("tag_key", StringType.Default, nullable: true),
        new Field("tag_value", StringType.Default, nullable: true),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        // Read the arguments HERE: the stream they were imported from is disposed when tablefn_bind returns, so a
        // binding that kept the batch would read freed Arrow buffers at execution time.
        var key = Arg(args, 0);
        var value = Arg(args, 1);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException(
                $"{FunctionName}: a non-empty key is required, e.g. {FunctionName}('dbt_run_id', '<uuid>').");
        }
        // sp_set_session_context caps the key at 128 bytes and the value at 8000; reject locally so the failure
        // names the limit instead of arriving as a generic T-SQL error.
        if (System.Text.Encoding.UTF8.GetByteCount(key!) > 128)
        {
            throw new ArgumentException($"{FunctionName}: key exceeds the 128-byte limit.");
        }
        if (value is not null && System.Text.Encoding.UTF8.GetByteCount(value) > 8000)
        {
            throw new ArgumentException($"{FunctionName}: value exceeds the 8000-byte limit.");
        }
        // Capture the ambient transaction id HERE, at BIND. Bind runs on the thread the host set it on;
        // EXECUTE may not. AmbientTransaction is an AsyncLocal, so a scan that runs on a DuckDB worker thread
        // sees 0 — measured as intermittent failure (roughly one call in six wrongly reported "not in an
        // explicit transaction", and with the id absent the tag would have gone to a throwaway connection).
        // Capturing at bind and re-establishing at execute is the same fix begin_bulk uses for its background
        // consumer thread.
        return new Binding(_catalog, key!, value, AmbientTransaction.Current);
    }

    // A 1-row args batch column as a string. Tolerant about the arrival type for the same reason the Fabric
    // functions are: a DuckDB literal can reach us at a different width than declared.
    private static string? Arg(RecordBatch? args, int col) =>
        args is null || col >= args.ColumnCount || args.Length == 0
            ? null
            : ArrowValueReader.ReadScalar(args.Column(col), 0)?.ToString();

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly SqlServerCatalog _catalog;
        private readonly string _key;
        private readonly string? _value;
        private readonly long _txnId;

        internal Binding(SqlServerCatalog catalog, string key, string? value, long txnId)
        {
            _catalog = catalog;
            _key = key;
            _value = value;
            _txnId = txnId;
        }

        public Schema OutputSchema => Columns;

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Dispose the pushed filter values in this PLAIN method — an async-iterator body does not begin
            // until the host's first get_next, long after InitGlobal returned, and the producer is owned by the
            // scan's global state (the documented late-release use-after-free).
            scan.FilterValues?.Dispose();
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            // Re-establish the bind-time transaction id: this may be a worker thread, where the AsyncLocal is 0.
            if (AmbientTransaction.Current == 0 && _txnId != 0)
            {
                AmbientTransaction.Current = _txnId;
            }
            yield return _catalog.SetSessionTag(_key, _value);
        }

        public void Dispose()
        {
        }
    }
}
