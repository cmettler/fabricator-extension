using System;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

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
    /// Runs a non-query statement (DDL / DML) on a fresh host connection and returns the affected-row count
    /// when the engine reports one (DML → a 1-row BIGINT "Count"; DDL → 0). A thin helper over
    /// <see cref="Query"/> (the ABI has one primitive — host_query subsumes exec).
    /// </summary>
    public static long ExecuteNonQuery(string sql)
    {
        using var stream = Query(sql);
        var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
        if (batch is null || batch.ColumnCount == 0 || batch.Length == 0)
        {
            return 0;
        }
        return batch.Column(0) is Int64Array c && c.GetValue(0) is long v ? v : 0;
    }
}
