using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Runs <paramref name="sql"/> binding a 1-row <paramref name="parameters"/> batch positionally to the
    /// statement's parameters (?, $1, …) via a prepared statement on a fresh host connection.
    /// </summary>
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

    // ---- ambient named-source registry (data-in by name) -------------------------------------------------
    // A managed component registers `name -> a factory producing a FRESH Arrow stream`; any host query (and,
    // with the replacement-scan layer, any query) referencing that name resolves to it via arrownet_scan.
    // The factory must yield a fresh stream per call (a stream is read once). Names are case-insensitive.
    private static readonly ConcurrentDictionary<string, Func<IArrowArrayStream>> Sources =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) a named Arrow source. <paramref name="factory"/> is invoked per scan to
    /// produce a fresh stream. Reference it as <c>arrownet_scan('name')</c> (or bare, with the replacement scan).</summary>
    public static void RegisterSource(string name, Func<IArrowArrayStream> factory) => Sources[name] = factory;

    /// <summary>Removes a named source. Returns true if it was registered.</summary>
    public static bool UnregisterSource(string name) => Sources.TryRemove(name, out _);

    internal static bool SourceExists(string name) => Sources.ContainsKey(name);

    internal static IArrowArrayStream? OpenSource(string name) =>
        Sources.TryGetValue(name, out var factory) ? factory() : null;
}
