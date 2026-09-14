// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

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
/// <see cref="IAggregateState.Combine"/>.
/// </summary>
public interface IAggregateFunction
{
    /// <summary>Function name. Catalog: <c>db.schema.Name(args)</c>; global: the bare registered name.</summary>
    string Name { get; }

    /// <summary>The argument fields, in positional order (names + Arrow types) — the call signature.</summary>
    Schema Parameters { get; }

    /// <summary>
    /// The single result field (name + Arrow type) the aggregate produces per group, or <c>null</c> when the
    /// type is resolved PER CALL SITE by <see cref="Bind"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>null</c> registers the aggregate as <c>ANY</c> and makes <see cref="Bind"/> obliged to supply a
    /// type — the same sentinel <see cref="IScalarFunction.Result"/> uses, and for the same reason: a
    /// function whose result depends on a constant argument (a rendered template, a declared shape) cannot
    /// state it at registration. An aggregate declaring a fixed type leaves <see cref="Bind"/> alone.
    /// </remarks>
    Field? Result { get; }

    /// <summary>
    /// Creates a fresh accumulator for one group. DuckDB calls this lazily, once per distinct group state
    /// (and once per partial state during parallel/windowed aggregation). The returned object must be
    /// independent (no shared mutable state across groups).
    /// </summary>
    IAggregateState CreateState();

    /// <summary>
    /// Opt-in: when <c>true</c>, the aggregate runs in <em>spillable</em> mode — the per-group state is
    /// serialized into DuckDB's fixed-size, pointer-free state blob (via <see cref="IAggregateState.Serialize"/>
    /// / <see cref="IAggregateState.Load"/>) so DuckDB's out-of-core <c>GROUP BY</c> can spill it to disk.
    /// This trades per-call (de)serialization cost for bounded memory at high group cardinality, and requires
    /// the serialized state to fit a fixed cap (1&nbsp;KB). Leave <c>false</c> (the default) for the fast
    /// in-memory path, which keeps a live accumulator per group and cannot spill.
    /// </summary>
    bool SupportsSpill => false;

    /// <summary>
    /// Binds ONE call site: <paramref name="args"/> carries the call's constant arguments (a 1-row batch
    /// plus an IsConstant mask, exactly as a scalar's bind receives them). Returns the per-call-site binding
    /// that supplies the RESULT TYPE and creates this call's accumulators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default binds NOTHING: it reports <see cref="Result"/> and delegates <see cref="CreateState"/>, so
    /// an aggregate with a fixed return type implements no extra member. Override it only to resolve a type
    /// from the arguments — and then <see cref="Result"/> must be <c>null</c>, or the declaration silently
    /// wins at registration and the two disagree.
    /// </para>
    /// <para>
    /// ⚠⚠ ONE SESSION PER CALL SITE, which is what makes this possible at all: <c>agg_open</c> is called from
    /// DuckDB's aggregate BIND, so two call sites of the same function never share a binding — and DuckDB
    /// passes the bound <c>AggregateFunction</c> BY VALUE into that bind and moves the mutated copy into the
    /// bound expression, so a type resolved here really does become the expression's type.
    /// </para>
    /// <para>
    /// ⚠ A NON-CONSTANT argument's slot holds a NULL PLACEHOLDER, not a value. Read
    /// <see cref="ScalarBindArgs.IsConstant"/> first; a per-row argument cannot contribute to a type that is
    /// part of the PLAN.
    /// </para>
    /// </remarks>
    IAggregateBinding Bind(ScalarBindArgs args) => new StaticAggregateBinding(this);
}

/// <summary>One bound aggregate call site: the resolved result type plus this call's accumulator factory.</summary>
public interface IAggregateBinding
{
    /// <summary>The result field for THIS call site. Never null — a binding that cannot resolve one throws.</summary>
    Field Result { get; }

    /// <summary>Creates a fresh accumulator for one group of this call. See <see cref="IAggregateFunction.CreateState"/>.</summary>
    IAggregateState CreateState();

    /// <summary>
    /// Optionally builds the finished result COLUMN for ONE finalize call — a whole vector of groups, in
    /// state order — from the values their <see cref="IAggregateState.Finalize"/> returned. Return
    /// <c>null</c> to use the host's default, which boxes each value into a column of
    /// <see cref="Result"/>'s type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things the default cannot do, and both are why this exists. It converts through a hand-written
    /// per-type ladder, so a STRUCT, LIST or MAP result is refused by name; and it works value-by-value,
    /// where a binding whose states produce an INTERMEDIATE (text, say) can convert the whole vector in ONE
    /// step. A binding that already HAS the column — because DuckDB produced it — should hand it over rather
    /// than round-tripping it through boxed objects.
    /// </para>
    /// <para>
    /// ⚠⚠ It must have EXACTLY <c>values.Length</c> entries, in the same order, and its Arrow type must be
    /// <see cref="Result"/>'s: the host declares the result schema from <see cref="Result"/> and the column
    /// is read through converters built from it, so a mismatch is read as DATA rather than caught.
    /// </para>
    /// </remarks>
    IArrowArray? FinalizeColumn(object?[] values) => null;
}

/// <summary>The default binding: the function's DECLARED result and its own accumulators.</summary>
/// <remarks>
/// ⚠ It throws when the function declared no result, which is the honest failure for
/// "<see cref="IAggregateFunction.Result"/> is null and Bind was not overridden" — the alternative is an
/// unresolved ANY flowing into the plan and failing far from its cause.
/// </remarks>
public sealed class StaticAggregateBinding : IAggregateBinding
{
    private readonly IAggregateFunction _fn;

    public StaticAggregateBinding(IAggregateFunction fn) => _fn = fn;

    public Field Result => _fn.Result ?? throw new NotSupportedException(
        $"fabricator: aggregate '{_fn.Name}' declares no return type and does not override Bind to resolve one");

    public IAggregateState CreateState() => _fn.CreateState();
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
public interface IAggregateState
{
    /// <summary>
    /// Folds one batch of argument rows into this accumulator. <paramref name="args"/> carries the columns
    /// described by <see cref="IAggregateFunction.Parameters"/> (positional, same order); every row
    /// belongs to this group. The accumulator sees NULL argument rows and decides how to treat them
    /// (standard SQL aggregates skip NULLs).
    /// </summary>
    void Update(RecordBatch args);

    /// <summary>Merges another partial accumulator (the same function's state) into this one.</summary>
    void Combine(IAggregateState source);

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
