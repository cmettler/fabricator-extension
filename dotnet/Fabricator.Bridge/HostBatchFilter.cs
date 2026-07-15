using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// EXACT per-batch filter application through the host engine: each source batch is bound as an Arrow view
/// and run through <c>SELECT * FROM &lt;batch&gt; WHERE &lt;sql&gt;</c> on the host DuckDB. Used by the Delta
/// codec path under <c>pushdown_filters 'all'</c>/<c>'dynamic'</c>, where the scan declared
/// <c>filter_pushdown=true</c>: DuckDB ERASES the pushed static filters from the plan (the scan MUST apply
/// them exactly — pruning alone would return surplus rows), and the host-rendered
/// <see cref="ScanSpec.NativeFilter"/> SQL is the 1:1 rendering of that TableFilterSet (statics exact,
/// dynamic join filters resolved as of scan init, exotic filters via their SQL form) — evaluating it on the
/// host gives full expression coverage with no type-mapping gaps. Column-typed values (variant transport
/// blobs, structs, the trailing rowid) round-trip through the host's registered Arrow extensions.
/// </summary>
internal static class HostBatchFilter
{
    private const string InputName = "__fabricator_scan_batch";

    internal static async IAsyncEnumerable<RecordBatch> Apply(
        Schema schema, IAsyncEnumerable<RecordBatch> source, string whereSql,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The MATERIALIZED CTE is a predicate-pushdown BARRIER, and it is load-bearing: DuckDB's arrow scan
        // (which backs the bound input view) declares filter_pushdown=true, so a bare
        // `SELECT * FROM <input> WHERE ...` gets its WHERE erased INTO the arrow scan — where a plain
        // C-stream input cannot apply it — and silently returns every row. Materializing first forces the
        // filter to run above the scan.
        string sql = $"WITH b AS MATERIALIZED (SELECT * FROM {InputName}) SELECT * FROM b WHERE {whereSql}";
        await foreach (var batch in source.ConfigureAwait(false))
        {
            if (batch.Length == 0)
            {
                continue;
            }
            using var input = new InMemoryArrayStream(schema, new[] { batch });
            using var filtered = Host.Query(sql, new (string, IArrowArrayStream)[] { (InputName, input) });
            while (true)
            {
                var rb = await filtered.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                if (rb is null)
                {
                    break;
                }
                if (rb.Length > 0)
                {
                    yield return rb;
                }
            }
        }
    }
}
