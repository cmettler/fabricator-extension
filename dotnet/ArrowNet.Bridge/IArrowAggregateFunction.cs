using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom aggregate function (UDAF), implemented in C# over Arrow — the aggregate
/// analog of <see cref="IArrowScalarFunction"/> (4e) / <see cref="IArrowTableFunction"/> (4f) /
/// <see cref="IArrowTableInOutFunction"/> (4g). Surfaced into every attached catalog and usable wherever
/// DuckDB allows an aggregate: <c>SELECT db.SchemaName.Name(args) FROM t</c>, with <c>GROUP BY</c>,
/// parallel aggregation, and window (<c>OVER(...)</c>) contexts.
///
/// DuckDB's aggregate model is state-vectorized: DuckDB owns a contiguous array of fixed-size state
/// blobs and drives reduction through initialize/update/combine/finalize callbacks. The bridge keeps each
/// blob as a mere <c>int64</c> id; the real per-group accumulator lives here in C#, behind that id (see
/// <see cref="IAggregateSession"/>). DuckDB chooses a fresh accumulator per group via
/// <see cref="CreateState"/>; partial states from parallel threads are merged via
/// <see cref="IArrowAggregateState.Combine"/>.
/// </summary>
public interface IArrowAggregateFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }

    /// <summary>Function name, as called: <c>db.SchemaName.Name(args)</c>.</summary>
    string Name { get; }

    /// <summary>The argument fields, in positional order (names + Arrow types) — the call signature.</summary>
    Schema Parameters { get; }

    /// <summary>The single result field (name + Arrow type) the aggregate produces per group.</summary>
    Field Result { get; }

    /// <summary>
    /// Creates a fresh accumulator for one group. DuckDB calls this lazily, once per distinct group state
    /// (and once per partial state during parallel/windowed aggregation). The returned object must be
    /// independent (no shared mutable state across groups).
    /// </summary>
    IArrowAggregateState CreateState();
}

/// <summary>
/// A single per-group accumulator created by <see cref="IArrowAggregateFunction.CreateState"/>. A given
/// instance is only ever touched by one thread at a time (DuckDB partitions work per thread), so it needs
/// no internal locking. A brand-new instance must finalize to the "empty group" value (e.g. NULL or 0) —
/// DuckDB may finalize a state that was never updated.
/// </summary>
public interface IArrowAggregateState
{
    /// <summary>
    /// Folds one batch of argument rows into this accumulator. <paramref name="args"/> carries the columns
    /// described by <see cref="IArrowAggregateFunction.Parameters"/> (positional, same order); every row
    /// belongs to this group. The accumulator sees NULL argument rows and decides how to treat them
    /// (standard SQL aggregates skip NULLs).
    /// </summary>
    void Update(RecordBatch args);

    /// <summary>Merges another partial accumulator (the same function's state) into this one.</summary>
    void Combine(IArrowAggregateState source);

    /// <summary>This group's single result value (boxed; <c>null</c> = SQL NULL), typed per
    /// <see cref="IArrowAggregateFunction.Result"/>.</summary>
    object? Finalize();
}
