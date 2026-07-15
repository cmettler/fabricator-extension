using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// A provider-authored custom aggregate function (UDAF), implemented in C# over Arrow — the aggregate
/// analog of <see cref="ICatalogScalarFunction"/> (4e) / <see cref="ICatalogTableFunction"/> (4f) /
/// <see cref="ICatalogInOutFunction"/> (4g). Surfaced into every attached catalog and usable wherever
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
public interface IAggregateFunction
{
    /// <summary>Function name. Catalog: <c>db.schema.Name(args)</c>; global: the bare registered name.</summary>
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

    /// <summary>
    /// Opt-in: when <c>true</c>, the aggregate runs in <em>spillable</em> mode — the per-group state is
    /// serialized into DuckDB's fixed-size, pointer-free state blob (via <see cref="IArrowAggregateState.Serialize"/>
    /// / <see cref="IArrowAggregateState.Load"/>) so DuckDB's out-of-core <c>GROUP BY</c> can spill it to disk.
    /// This trades per-call (de)serialization cost for bounded memory at high group cardinality, and requires
    /// the serialized state to fit a fixed cap (1&nbsp;KB). Leave <c>false</c> (the default) for the fast
    /// in-memory path, which keeps a live accumulator per group and cannot spill.
    /// </summary>
    bool SupportsSpill => false;
}

/// <summary>A catalog-bound custom aggregate (attach-time scope) — <see cref="IAggregateFunction"/> plus the
/// <see cref="SchemaName"/>. For a connection-free, ATTACH-free aggregate, implement the base
/// <see cref="IAggregateFunction"/> and declare it as a global instead.</summary>
public interface ICatalogAggregateFunction : IAggregateFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }
}

/// <summary>
/// A single per-group accumulator created by <see cref="IAggregateFunction.CreateState"/>. A given
/// instance is only ever touched by one thread at a time (DuckDB partitions work per thread), so it needs
/// no internal locking. A brand-new instance must finalize to the "empty group" value (e.g. NULL or 0) —
/// DuckDB may finalize a state that was never updated.
/// </summary>
public interface IArrowAggregateState
{
    /// <summary>
    /// Folds one batch of argument rows into this accumulator. <paramref name="args"/> carries the columns
    /// described by <see cref="IAggregateFunction.Parameters"/> (positional, same order); every row
    /// belongs to this group. The accumulator sees NULL argument rows and decides how to treat them
    /// (standard SQL aggregates skip NULLs).
    /// </summary>
    void Update(RecordBatch args);

    /// <summary>Merges another partial accumulator (the same function's state) into this one.</summary>
    void Combine(IArrowAggregateState source);

    /// <summary>This group's single result value (boxed; <c>null</c> = SQL NULL), typed per
    /// <see cref="IAggregateFunction.Result"/>.</summary>
    object? Finalize();

    /// <summary>
    /// Spillable mode only (<see cref="IAggregateFunction.SupportsSpill"/>): serialize this accumulator
    /// to a compact, self-contained byte form. Must fit the fixed cap (1&nbsp;KB) — throw or keep state small
    /// otherwise. The default throws (non-spillable aggregates need not implement it).
    /// </summary>
    byte[] Serialize() => throw new NotSupportedException("this aggregate does not support spilling");

    /// <summary>
    /// Spillable mode only: reset this accumulator's state from bytes previously produced by
    /// <see cref="Serialize"/>. The default throws.
    /// </summary>
    void Load(ReadOnlySpan<byte> state) => throw new NotSupportedException("this aggregate does not support spilling");
}
