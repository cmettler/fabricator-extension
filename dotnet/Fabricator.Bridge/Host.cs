// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Reuse the HOST's own DuckDB engine from a managed provider/function, over Arrow. <see cref="Query"/> runs
/// SQL on a FRESH host connection (its own ClientContext/transaction — never the in-flight one, which is
/// non-reentrant) and returns the result as an Arrow stream. This is the safe way to run DuckDB queries from
/// inside the extension (call a DuckDB function, read a table, use an extension) instead of opening a second
/// database or going out to ADBC. Separate transaction ⇒ committed-reads semantics. See docs/host-query.md.
/// </summary>
public static class Host
{
    /// <summary>True once the host registered the host_query callback (it boots with the extension).</summary>
    public static bool CanQuery => HostFs.CanQuery;

    /// <summary>Runs <paramref name="sql"/> on a fresh host connection; the caller owns + disposes the stream.</summary>
    public static IArrowArrayStream Query(string sql) => HostFs.Query(sql);

    /// <summary>
    /// Runs <paramref name="sql"/> binding a 1-row <paramref name="parameters"/> batch to the statement's
    /// parameters via a prepared statement on a fresh host connection.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The STATEMENT decides which binding is used</b>: BY NAME when the batch's column names are all
    /// parameter names the statement declares (<c>$a</c>), POSITIONALLY otherwise — which is what a
    /// <c>?</c> / <c>$1</c> statement gets, since it names its parameters "1", "2", … Only ROW 0 is read,
    /// an empty batch binds all-NULL, and a parameterised call is limited to ONE statement (Prepare, not
    /// SendQuery).
    /// </remarks>
    public static IArrowArrayStream Query(string sql, RecordBatch parameters) => HostFs.Query(sql, parameters);

    /// <summary>
    /// Runs <paramref name="sql"/> on a fresh host connection with C#-provided named Arrow <paramref name="inputs"/>
    /// registered as connection-scoped views first (data-in) — the SQL references them by name. The host
    /// consumes the input streams during the query. Lets a managed component push data into the host engine
    /// (join/filter/aggregate it with DuckDB) over Arrow. See docs/host-query.md.
    /// </summary>
    public static IArrowArrayStream Query(string sql, IReadOnlyList<(string Name, IArrowArrayStream Stream)> inputs)
        => HostFs.Query(sql, inputs: inputs);

    /// <summary>Runs <paramref name="sql"/> with both positional <paramref name="parameters"/> and named Arrow
    /// <paramref name="inputs"/> on a fresh host connection.</summary>
    public static IArrowArrayStream Query(string sql, RecordBatch? parameters,
                                          IReadOnlyList<(string Name, IArrowArrayStream Stream)>? inputs)
        => HostFs.Query(sql, parameters, inputs);

    /// <summary>
    /// Runs <paramref name="sql"/> on a fresh host connection, optionally AS THE CALLER'S SESSION WOULD:
    /// pass the calling operator's <c>ClientContext</c> handle as <paramref name="clientSession"/> and the
    /// fresh connection inherits its <c>TimeZone</c> and catalog SEARCH PATH before the statement runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>The search path is one thing, not three.</b> <c>current_catalog()</c>, <c>current_schema()</c>
    /// and <c>search_path</c> all read the same <c>CatalogSearchPath</c>, so copying it carries all three —
    /// which is why an unqualified <c>FROM t</c> resolves the way the caller's statement would.
    /// </para>
    /// <para>
    /// ⚠ <b>It is opt-in, and that is the design rather than caution.</b> Inheriting is a CHOICE: a template
    /// rendering the caller's statement wants it, while a provider doing its own bookkeeping wants a session
    /// that cannot depend on who called it. 0 (the default) is exactly the behaviour every caller had before
    /// ABI v83. Inside an ABI crossing the handle to pass is <c>AmbientOpener.Current</c>; a plugin gets it
    /// automatically through <see cref="IHostQuery"/>, whose implementation passes the ambient for you.
    /// </para>
    /// <para>
    /// ⚠ <b>It does NOT make the query part of the caller's transaction.</b> The connection is still fresh
    /// and still reads COMMITTED state, so a caller inside <c>BEGIN; INSERT …;</c> does not see its own
    /// uncommitted rows — that is a property of opening a separate connection and is unchanged. What is
    /// copied is name and time RESOLUTION, nothing else; "copy the session" has no principled boundary, so
    /// each addition is deliberate.
    /// </para>
    /// <para>
    /// ⚠ The handle is valid only for the duration of this synchronous call — the host captures the session
    /// immediately and does not keep the pointer.
    /// </para>
    /// </remarks>
    public static IArrowArrayStream Query(string sql, RecordBatch? parameters,
                                          IReadOnlyList<(string Name, IArrowArrayStream Stream)>? inputs,
                                          nint clientSession)
        => HostFs.Query(sql, parameters, inputs, clientSession: clientSession);

    /// <summary>
    /// Runs a non-query statement (DDL / DML) on a fresh host connection and returns the affected-row count
    /// when the engine reports one. A thin helper over <see cref="Query"/> (the ABI has one primitive —
    /// host_query subsumes exec).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>THE RULE IS "the first column of the first batch, when it is an Int64" — an INFERENCE from the
    /// RESULT SHAPE, not a question put to the statement.</b> This doc used to say "DML → a 1-row BIGINT
    /// Count; DDL → 0", which is right for DML and for PURE DDL and WRONG for a CTAS: MEASURED 2026-09-02,
    /// <c>CREATE TABLE c AS SELECT * FROM range(7)</c> returns <b>7</b> here.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>SO THIS DIVERGES FROM THE SQL SURFACE, and the divergence is REAL rather than cosmetic:</b> the
    /// C++ <c>fabricator_host_exec</c> asks DuckDB's own <c>StatementReturnType::CHANGED_ROWS</c> and answers
    /// <b>0</b> for that same CTAS (measured side by side). Managed code cannot reach that classification, so
    /// this cannot be made to agree without one — do not "fix" it by matching on a leading keyword, which is
    /// the prefix-check anti-pattern §9.2 of docs/fluid-templating.md measures as broken.
    /// </para>
    /// <para>
    /// ⚠ It follows that a <b>SELECT</b> returning a single BIGINT reports that VALUE as though it were a
    /// count. Callers for whom that matters must refuse a SELECT first — the Fluid <c>exec()</c> does, using
    /// DuckDB's parser rather than a prefix test.
    /// </para>
    /// <para>For several statements the count is the LAST one's (<c>SendQuery</c> returns the last result).</para>
    /// </remarks>

    /// <summary>
    /// Opens a host connection that OUTLIVES a single call, so several statements share one DuckDB
    /// Connection — and therefore one TEMPORARY catalog, one set of session settings and one transaction
    /// context (ABI v84). Dispose it when the unit of work ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ <b>What it is FOR: read-your-writes inside one logical unit of work.</b> Every
    /// <see cref="Query"/> overload opens its own connection, so a statement cannot see what an earlier one
    /// created. On a pinned connection <c>CREATE TEMP TABLE t …</c> followed by <c>SELECT … FROM t</c>
    /// works, which is the shape a template or a multi-step generator wants: a scratch space needing no
    /// name in the shared catalog and no cleanup, because disposing the connection destroys its temporary
    /// catalog with it.
    /// </para>
    /// <para>
    /// ⚠⚠ <b>ONE RESULT STREAM AT A TIME, and the host REFUSES a second query rather than truncating the
    /// first.</b> DuckDB closes a connection's active streaming result when the next statement starts on
    /// it, and MEASURED it does so SILENTLY — the abandoned stream reports end-of-stream, so the first
    /// query's remaining rows would be LOST with no error anywhere. Read (or dispose) each stream before
    /// the next call on the same connection.
    /// </para>
    /// <para>
    /// ⚠ <b>NOT thread-safe</b>, like any DuckDB connection: one call at a time. A caller working on
    /// several threads opens one connection per thread.
    /// </para>
    /// <para>
    /// ⚠ <paramref name="clientSession"/> is applied ONCE at open rather than per query, so a <c>SET</c>
    /// performed THROUGH the pin sticks — which is part of the point. It still does NOT join the caller's
    /// transaction: reads are of COMMITTED state, exactly as for a fresh connection.
    /// </para>
    /// <para>
    /// ⚠ Named Arrow <c>inputs</c> are REFUSED on a pinned connection: an arrow_scan view is
    /// connection-scoped, so it would outlive the call and collide with the next one using that name.
    /// </para>
    /// </remarks>
    public static HostConnection OpenConnection(nint clientSession = 0) => new(HostFs.OpenConnection(clientSession));

    /// <summary>
    /// A pinned host DuckDB connection (see <see cref="Host.OpenConnection"/>): several statements on ONE
    /// connection, so its temporary catalog and session settings persist for the object's life.
    /// </summary>
    public sealed class HostConnection : IDisposable
    {
        private nint _handle;

        internal HostConnection(nint handle)
        {
            _handle = handle;
        }

        /// <summary>Runs <paramref name="sql"/> on this connection; the caller owns and disposes the stream.</summary>
        /// <remarks>
        /// <para>
        /// ⚠ Dispose the returned stream before calling again on this connection — a second statement would
        /// otherwise be REFUSED, which is deliberately better than DuckDB silently closing the first
        /// result. See <see cref="Host.OpenConnection"/>.
        /// </para>
        /// <para>
        /// ⚠ <paramref name="batchRows"/> (ABI v85) is how many rows to accumulate into each exported Arrow
        /// batch; 0 keeps the historical default of ONE DuckDB DataChunk (2048 rows). Ask for a big batch
        /// when the rows become SCAN MORSELS (measured ~2.4x on 100M rows), and leave it at 0 when they
        /// become FILES — engineered-wood writes one parquet file per input batch, which is why this cannot
        /// simply be a better default. See <c>FabricatorHostServices::host_query</c> in abi.h.
        /// </para>
        /// </remarks>
        public IArrowArrayStream Query(string sql, RecordBatch? parameters = null, long batchRows = 0)
        {
            ThrowIfDisposed();
            return HostFs.Query(sql, parameters, connection: _handle, batchRows: batchRows);
        }

        /// <summary>
        /// Runs a non-query statement (DDL / DML) on this connection and returns the affected-row count when
        /// the engine reports one — the same inference <see cref="Host.ExecuteNonQuery"/> documents.
        /// </summary>
        public long ExecuteNonQuery(string sql)
        {
            ThrowIfDisposed();
            using var stream = Query(sql);
            return ReadCount(stream);
        }

        /// <summary>Closes the connection, destroying its temporary catalog.</summary>
        /// <remarks>
        /// ⚠ Safe with result streams still outstanding: each holds its own reference to the underlying
        /// connection, so it dies with the last of them rather than under a live stream. Idempotent.
        /// </remarks>
        public void Dispose()
        {
            var h = _handle;
            _handle = 0;
            HostFs.CloseConnection(h);
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
            {
                throw new ObjectDisposedException(nameof(HostConnection));
            }
        }
    }

    public static long ExecuteNonQuery(string sql)
    {
        using var stream = Query(sql);
        return ReadCount(stream);
    }

    /// <summary>
    /// The affected-row inference both exec surfaces use: the first column of the first batch when it is an
    /// Int64, else 0.
    /// </summary>
    /// <remarks>
    /// ⚠ ONE copy on purpose. <see cref="ExecuteNonQuery"/> and
    /// <see cref="HostConnection.ExecuteNonQuery"/> answer the same question about the same primitive, and
    /// the rule is subtle enough (a CTAS reports its rows; a SELECT of one BIGINT reports its VALUE) that two
    /// copies would drift and only one of them would be documented.
    /// </remarks>
    private static long ReadCount(IArrowArrayStream stream)
    {
        var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
        if (batch is null || batch.ColumnCount == 0 || batch.Length == 0)
        {
            return 0;
        }
        return batch.Column(0) is Int64Array c && c.GetValue(0) is long v ? v : 0;
    }

    // ---- ambient named-source registry (data-in by name) -------------------------------------------------
    // A managed component registers `name -> a factory producing a FRESH Arrow stream`; any host query (and,
    // with the replacement-scan layer, any query) referencing that name resolves to it via fabricator_scan.
    // The factory must yield a fresh stream per call (a stream is read once). Names are case-insensitive.
    private readonly record struct NamedSource(Func<IArrowArrayStream> Factory, Schema? Schema);

    private static readonly ConcurrentDictionary<string, NamedSource> Sources =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) a named Arrow source. Reference it as <c>fabricator_scan('name')</c>
    /// (or bare, with the replacement scan).</summary>
    /// <param name="name">The name to resolve it under; case-insensitive.</param>
    /// <param name="factory">Produces a FRESH stream per call; a stream is read once.</param>
    /// <remarks>
    /// <b>⚠⚠ THE FACTORY IS INVOKED TWICE PER BOUND SCAN, not once.</b> A table function must declare its
    /// output columns at BIND, and with no declared schema the only way to learn them is to open a stream and
    /// read its schema — so the bind invokes the factory, reads the schema and releases WITHOUT pulling any
    /// data, and the scan then invokes it again for the rows. Binds also REPEAT: every use of a view over the
    /// source, and every <c>EXECUTE</c> of a prepared statement, binds again.
    /// <para>This doc used to say "invoked per scan", which was wrong, and is exactly the sentence an author
    /// would write an expensive or side-effecting factory against. A factory must be cheap and
    /// side-effect-free — or declare its schema, using the overload below.</para>
    /// </remarks>
    public static void RegisterSource(string name, Func<IArrowArrayStream> factory) =>
        Sources[name] = new NamedSource(factory, null);

    /// <summary>
    /// Registers a named Arrow source that DECLARES its schema, so the bind never invokes
    /// <paramref name="factory"/> at all — it is invoked exactly once, by the scan.
    /// </summary>
    /// <remarks>
    /// Use this whenever producing a stream costs anything: opening a connection, running a query, buffering.
    /// The declaration answers the bind directly and the factory is deferred to the first batch pull.
    /// <para>⚠ The declaration must MATCH what the factory's stream produces, because the host builds its
    /// Arrow→DuckDB converters from the DECLARED schema and reads the delivered batches through them. A
    /// mismatch is refused on the first pull rather than read as data — see <see cref="DeclaredSchemaStream"/>
    /// for exactly which differences that check catches.</para>
    /// </remarks>
    public static void RegisterSource(string name, Func<IArrowArrayStream> factory, Schema schema) =>
        Sources[name] = new NamedSource(factory ?? throw new ArgumentNullException(nameof(factory)),
                                        schema ?? throw new ArgumentNullException(nameof(schema)));

    /// <summary>Removes a named source. Returns true if it was registered.</summary>
    public static bool UnregisterSource(string name) => Sources.TryRemove(name, out _);

    internal static bool SourceExists(string name) => Sources.ContainsKey(name);

    internal static IArrowArrayStream? OpenSource(string name)
    {
        if (!Sources.TryGetValue(name, out var source))
        {
            return null;
        }
        // A declared schema makes the BIND's open free: the wrapper answers from the declaration and defers
        // the factory to the first pull, which the bind never performs.
        return source.Schema is null ? source.Factory() : new DeclaredSchemaStream(source.Schema, source.Factory);
    }
}

/// <summary>
/// A named source's stream when its schema was DECLARED at registration: it answers the schema without
/// invoking the factory, and opens the real stream on the first batch pull.
/// </summary>
/// <remarks>
/// ⚠ It exists so a BIND costs nothing. <c>PopulateReturnSchema</c> opens a stream, reads its schema and
/// releases it without ever pulling a batch — so with this wrapper the bind's open never reaches the author's
/// factory, and the factory is invoked exactly once, by the scan.
/// </remarks>
internal sealed class DeclaredSchemaStream : IArrowArrayStream
{
    private readonly Func<IArrowArrayStream> _factory;
    private IArrowArrayStream? _inner;
    private bool _disposed;

    internal DeclaredSchemaStream(Schema schema, Func<IArrowArrayStream> factory)
    {
        Schema = schema;
        _factory = factory;
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DeclaredSchemaStream));
        }
        if (_inner is null)
        {
            _inner = _factory() ?? throw new InvalidOperationException(
                "fabricator: a named source's factory returned null");
            Verify(Schema, _inner.Schema);
        }
        return _inner.ReadNextRecordBatchAsync(cancellationToken);
    }

    /// <summary>Refuses, on the first pull, a stream whose schema disagrees with the declaration.</summary>
    /// <remarks>
    /// ⚠ WHAT THIS CATCHES AND WHAT IT DOES NOT, said plainly rather than implied: the column COUNT, the
    /// column NAMES and each column's Arrow <c>TypeId</c> — which covers the authoring mistakes that are
    /// actually likely. It does NOT compare a type's PARAMETERS, so a declared <c>decimal(18,4)</c> against a
    /// produced <c>decimal(9,2)</c> passes here.
    /// <para>⚠ Deeper equality is not free: <c>IArrowType.Equals</c> is REFERENCE equality in Apache.Arrow, so
    /// a structural comparer has to be hand-written. <c>SqlServerCdcReader.SameType</c> is one, but it is
    /// private to another assembly. Consolidating the two into the bridge is the right follow-up; adding a
    /// second copy here as part of an unrelated change is not.</para>
    /// </remarks>
    private static void Verify(Schema declared, Schema actual)
    {
        var bad = declared.FieldsList.Count != actual.FieldsList.Count;
        for (int i = 0; !bad && i < declared.FieldsList.Count; i++)
        {
            var d = declared.FieldsList[i];
            var a = actual.FieldsList[i];
            bad = !string.Equals(d.Name, a.Name, StringComparison.Ordinal)
                  || d.DataType.TypeId != a.DataType.TypeId;
        }
        if (bad)
        {
            throw new InvalidOperationException(
                "fabricator: a named source's declared schema does not match the stream it produced — "
                + $"declared [{Render(declared)}], produced [{Render(actual)}]. The host builds its converters "
                + "from the DECLARED schema, so this would be read as data rather than reported.");
        }
    }

    private static string Render(Schema s) =>
        string.Join(", ", s.FieldsList.Select(f => f.Name + " " + f.DataType.TypeId));

    public void Dispose()
    {
        _disposed = true;
        _inner?.Dispose();
        _inner = null;
    }
}
