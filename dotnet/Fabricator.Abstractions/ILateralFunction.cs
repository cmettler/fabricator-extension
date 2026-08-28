// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// The result of one batched lateral call: the output rows plus, for each output row, which INPUT row
/// produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance is what makes batching possible at all.</b> When N input rows produce M output rows the HOST
/// has to stamp the correlated columns (`t.x` in <c>SELECT t.x, f.* FROM t, f(t.a)</c>) onto each output row,
/// and it can only do that if it knows which input row each output row belongs to. Without it, 1→N (fan-out)
/// and 1→0 (filtering) are inexpressible.
/// </para>
/// <para>
/// <see cref="Origin"/> may be null for a strict 1:1 map, where the identity mapping is implied — and that
/// case is STRICT: absent provenance with <c>Rows.Length != input.Length</c> is an error, never a guess.
/// </para>
/// </remarks>
public readonly struct LateralResult
{
    /// <summary>The output rows (the function's own columns). Null or empty = this whole input chunk produced
    /// nothing, which is a legitimate answer (1→0), NOT end-of-stream.</summary>
    public RecordBatch? Rows { get; }

    /// <summary>Per output row, the 0-based index of the input row that produced it. Null = identity (1:1).</summary>
    public int[]? Origin { get; }

    public LateralResult(RecordBatch? rows, int[]? origin = null)
    {
        Rows = rows;
        Origin = origin;
    }

    /// <summary>Every input row filtered out — no output rows for this call.</summary>
    public static LateralResult Empty => new(null, null);
}

/// <summary>
/// One per-thread session on a bound lateral function. The framework opens SEVERAL of these concurrently — the
/// batched operator declares itself parallel, so every pipeline thread gets its own session and there is no
/// shared mutable state to guard. Hold per-thread resources (a connection, an HTTP client, a model handle)
/// here; the framework disposes the session when its thread's operator state is torn down.
/// </summary>
public interface ILateralSession : IDisposable
{
    /// <summary>
    /// One call over a whole chunk of input rows (up to a DuckDB vector, 2048). Returns the output rows plus
    /// their provenance. This is the method the whole design exists to make batched: N outer rows cost
    /// ceil(N / 2048) calls here instead of N.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A map owes exactly ONE response per request: returning no rows means "these inputs produced nothing",
    /// never "I am finished". The framework calls again with the next chunk.
    /// </para>
    /// <para>
    /// ⚠ OWNERSHIP: the framework DISPOSES <paramref name="input"/> when this returns, and takes ownership of
    /// the batch in the returned <see cref="LateralResult"/>. So a value carried from input to output must be
    /// COPIED, not referenced — the same convention the in-out exchange uses (its author disposes each input
    /// chunk inside the loop).
    /// </para>
    /// </remarks>
    LateralResult Call(RecordBatch input);
}

/// <summary>
/// A bound lateral call — the per-plan object produced by <see cref="ILateralFunction.Bind"/>. It holds
/// whatever the call's constant arguments resolved to and hands out per-thread sessions.
/// </summary>
public interface ILateralFunctionBinding : IDisposable
{
    /// <summary>
    /// The function's OWN output columns. Note what is NOT here: the correlated passthrough columns. Those are
    /// the host's business — DuckDB projects them on the row-by-row path and the batched operator stamps them
    /// from <see cref="LateralResult.Origin"/> — so a lateral function never echoes its input, unlike an
    /// <see cref="IInOutFunctionBinding"/>.
    /// </summary>
    Schema OutputSchema { get; }

    /// <summary>Open one per-thread session. Called once per pipeline thread, not once per chunk.</summary>
    ILateralSession Open();
}

/// <summary>
/// A provider-authored ROW-MAPPED (correlated LATERAL) table function: the shape a table-in-out cannot
/// express.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IInOutFunction"/> declares its input as a <see cref="Params.TableInput"/> parameter, so it is
/// called on a relation the caller can NAME — <c>f(&lt;table&gt;)</c>. A lateral function declares its
/// positional parameters as REAL VALUE TYPES and no table input, so DuckDB's binder synthesises the input
/// relation from whichever arguments are expressions, and the idiomatic correlated spelling binds:
/// </para>
/// <code>
/// SELECT t.id, f.* FROM t, my_fn(t.a, t.b);        -- implicitly correlated (LATERAL)
/// SELECT * FROM my_fn(1, 2);                       -- the same bind, literal args
/// </code>
/// <para>
/// <b>Consequences of that registration, all forced by DuckDB:</b> a positional parameter carries no
/// bind-time value (it is runtime data), so <b>bind-time configuration must use NAMED parameters</b> — those
/// are what arrive in <see cref="Bind"/>'s <c>args</c>. Overload resolution goes by input-column COUNT, not
/// by argument values.
/// </para>
/// <para>
/// <b>Reach for this only when the per-call cost dominates the per-row work</b> — a network round trip, a
/// process boundary, a model invocation. A 1→1 in-process computation wants a scalar function (already
/// vectorized, no correlation); a bounded 1→N wants a scalar returning <c>LIST&lt;STRUCT&gt;</c> plus
/// <c>UNNEST</c>. Both are batched by construction and need none of this machinery.
/// </para>
/// </remarks>
public interface ILateralFunction
{
    /// <summary>Function name. Catalog: <c>SELECT * FROM t, db.schema.Name(t.a)</c>; global: the bare name.</summary>
    string Name { get; }

    /// <summary>
    /// The call signature. POSITIONAL fields (<see cref="Params.Positional"/>) are the PER-ROW INPUT COLUMNS —
    /// their types become the function's DuckDB argument types. NAMED fields (<see cref="Params.Named"/>) are
    /// the constant "cost" args, and are the only ones whose values reach <see cref="Bind"/>.
    /// </summary>
    /// <remarks>
    /// Declaring a <see cref="Params.TableInput"/> here is REFUSED at registration: a lateral function has no
    /// input table, and accepting one would build a <c>{TABLE}</c> signature that the correlated spelling
    /// cannot bind against — i.e. a function nobody could call the way it was meant to be called.
    /// </remarks>
    Schema Parameters { get; }

    /// <summary>Binds one call: <paramref name="args"/> (nullable) is a 1-row batch of the constant NAMED args,
    /// <paramref name="inputSchema"/> the actual per-row input columns as DuckDB resolved them.</summary>
    /// <remarks>
    /// ⚠ Read <paramref name="args"/> DURING this call and do not retain it — the framework owns the batch and
    /// its lifetime ends with the bind, exactly as on the in-out path. Capture the plain values you need.
    /// </remarks>
    ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema);
}

/// <summary>A catalog-bound lateral function (attach-time scope) — <see cref="ILateralFunction"/> plus the
/// <see cref="SchemaName"/>. For a connection-free, ATTACH-free one, implement the base interface and declare
/// it as a global instead.</summary>
public interface ICatalogLateralFunction : ILateralFunction
{
    /// <summary>Target catalog schema (e.g. "dbo").</summary>
    string SchemaName { get; }
}
