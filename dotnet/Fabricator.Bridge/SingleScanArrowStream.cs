// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Microsoft.Extensions.Logging;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Wraps a bound host-query input so a SECOND scan of it is LOUD instead of empty.
/// <para>Why it exists: an input registered with <see cref="Host.Query(string, System.Collections.Generic.IReadOnlyList{System.ValueTuple{string, IArrowArrayStream}})"/>
/// is a connection-scoped view over a single-use stream. <see cref="InMemoryArrayStream"/> returns
/// <c>null</c> once drained, so if a plan ever scans that view twice the second scan sees ZERO ROWS and
/// succeeds. For a deletion-vector anti-join that is not a degraded result, it is DELETED ROWS COMING BACK —
/// silently. A <c>WITH … AS MATERIALIZED</c> CTE is what makes the single scan true today; this makes a future
/// planner change that breaks that assumption fail instead of corrupting an answer.</para>
/// <para>A read after end-of-stream is the signal, not the read count: a stream legitimately reports
/// end-of-stream once per scan, so throwing on the SECOND end-of-stream read is what distinguishes "drained"
/// from "scanned again".</para>
/// </summary>
internal sealed class SingleScanArrowStream : IArrowArrayStream
{
    private readonly IArrowArrayStream _inner;
    private readonly string _name;
    private bool _drained;

    internal SingleScanArrowStream(IArrowArrayStream inner, string name)
    {
        _inner = inner;
        _name = name;
    }

    public Schema Schema => _inner.Schema;

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        if (_drained)
        {
            throw new System.InvalidOperationException(
                $"fabricator: bound input '{_name}' was scanned more than once. It is a single-use stream, so a "
                + "second scan would silently contribute NO rows — for a deletion-vector anti-join that resurrects "
                + "deleted rows. The query must reference it exactly once (a WITH … AS MATERIALIZED CTE).");
        }
        var batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            _drained = true;
        }
        return batch;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Naming and cleanup for the connection-scoped Arrow inputs handed to <see cref="Host.Query(string,
/// System.Collections.Generic.IReadOnlyList{System.ValueTuple{string, Apache.Arrow.Ipc.IArrowArrayStream}})"/>.
/// <para>⚠ Both halves are load-bearing. DuckDB's <c>duckdb_arrow_scan</c> registers an input with
/// <c>CreateView(name, replace: true, temporary: FALSE)</c> — a CATALOG-level view shared by every connection
/// on the database, silently replacing any existing one, and outliving the connection that made it. So a
/// FIXED name is (a) a race between any two concurrent host queries and (b) a view left behind in the user's
/// <c>duckdb_views()</c>. MEASURED on the write path: six concurrent Delta writers in one process — the
/// <c>dbt run --threads N</c> shape — and <b>five of the six failed</b> with
/// <c>failed to register input view '__fabricator_delta_write_src'</c>.</para>
/// </summary>
internal static class BoundInput
{
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Bridge");
    private static long _seq;

    /// <summary>Wraps a LAZY host-query result so its bound-input views are dropped when the CALLER disposes
    /// it — the only moment a lazy producer can know draining is over. Sites whose query materializes before
    /// returning should just call <see cref="Drop"/> in a <c>finally</c> instead.
    /// <para>⚠ Not merely tidiness: <c>duckdb_arrow_scan</c> creates a CATALOG-level (non-temporary) view, so
    /// it outlives the connection AND the stream that owns the input's storage. Left behind, it is a view
    /// pointing at freed memory that any later query naming it would scan.</para></summary>
    internal static IArrowArrayStream WrapDrop(IArrowArrayStream inner, params string[] names)
        => new DropViewsOnDisposeStream(inner, names);

    private sealed class DropViewsOnDisposeStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private readonly string[] _names;
        private bool _dropped;

        internal DropViewsOnDisposeStream(IArrowArrayStream inner, string[] names)
        {
            _inner = inner;
            _names = names;
        }

        public Schema Schema => _inner.Schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
            => _inner.ReadNextRecordBatchAsync(cancellationToken);

        public void Dispose()
        {
            // Inner FIRST: that releases the host-side result and with it the adopted input streams, so the
            // view is dead before it is dropped rather than after.
            _inner.Dispose();
            if (!_dropped)
            {
                _dropped = true;
                Drop(_names);
            }
        }
    }

    /// <summary>A name no concurrent host query can be using.</summary>
    internal static string NextName(string prefix)
        => prefix + "_" + System.Threading.Interlocked.Increment(ref _seq)
                              .ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Drops the views once their query has been consumed — what makes unique names affordable, since
    /// otherwise the catalog accumulates one per call instead of one stale entry. Best-effort: the query has
    /// already produced its result, so a failure here must never surface.</summary>
    internal static void Drop(params string[] names)
    {
        if (names.Length == 0)
        {
            return;
        }
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var n in names)
            {
                sb.Append("DROP VIEW IF EXISTS \"").Append(n.Replace("\"", "\"\"")).Append("\"; ");
            }
            using var _ = Host.Query(sb.ToString());
        }
        catch (System.Exception ex)
        {
            Log.LogDebug("dropping bound-input view failed ({Msg})", ex.Message);
        }
    }
}
