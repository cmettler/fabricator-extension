using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A custom-aggregate execution session (Phase 4h). One session per bound aggregate holds the map from
/// DuckDB's per-group <c>int64</c> state ids to the live C# accumulators (see
/// <see cref="IAggregateFunction"/>). The C++ aggregate callbacks marshal the state id(s) + argument
/// columns over the <c>agg_*</c> ABI; this session routes them to the right accumulator. Opened at
/// <c>bind</c> (<c>agg_open</c>), released when the bound plan is torn down (<c>agg_close</c>).
///
/// The session is hit by multiple threads concurrently during parallel aggregation, but a given id is
/// touched by only one thread at a time, so the implementation just needs a thread-safe id→accumulator
/// map (no per-accumulator lock).
/// </summary>
public interface IAggregateSession
{
    /// <summary>
    /// Arrow schema of an <see cref="Update"/> batch: an <c>int64</c> "state_id" column followed by the
    /// function's <see cref="IAggregateFunction.Parameters"/> (used to import the batch).
    /// </summary>
    Schema UpdateSchema { get; }

    /// <summary>Applies one update batch: column 0 = int64 state_id, columns 1.. = argument values.</summary>
    void Update(RecordBatch idPlusArgs);

    /// <summary>Merges partial states: column 0 = int64 target_id, column 1 = int64 source_id (per row).</summary>
    void Combine(RecordBatch targetSource);

    /// <summary>
    /// Finalizes the given states: a single int64 "state_id" column; returns a one-column Arrow stream of
    /// each group's result, in the SAME ORDER as the ids (an absent id => a fresh accumulator => the
    /// empty-group value).
    /// </summary>
    IArrowArrayStream Finalize(RecordBatch ids);

    /// <summary>Drops the given states (single int64 "state_id" column) to bound memory. Best-effort.</summary>
    void Destroy(RecordBatch ids);

    /// <summary>Releases the session (the id→accumulator map). Idempotent.</summary>
    void Close();

    // ---- Spillable mode (IAggregateFunction.SupportsSpill) — state lives as bytes in DuckDB's blob,
    // not in this session; each call round-trips bytes <-> a transient accumulator. ----

    /// <summary>
    /// Spillable update: <paramref name="groupStates"/> is a single BLOB column (one row per distinct group
    /// in this chunk, its current serialized state; NULL = fresh); <paramref name="slotPlusArgs"/> is
    /// <c>[int64 slot ++ params]</c> (slot indexes into <paramref name="groupStates"/>). Returns a single BLOB
    /// column of the new serialized state per group, in the same order as <paramref name="groupStates"/>.
    /// </summary>
    IArrowArrayStream UpdateSpill(RecordBatch groupStates, RecordBatch slotPlusArgs);

    /// <summary>
    /// Spillable combine: <paramref name="targetStates"/> is a BLOB column (one row per distinct target,
    /// NULL = fresh); <paramref name="batch"/> is <c>[int64 slot, BLOB source]</c> — each row merges its
    /// source into <c>targetStates[slot]</c> (a target may repeat, e.g. the window segment-tree merges
    /// several nodes into one frame state). Returns a BLOB column of the merged target state per target.
    /// </summary>
    IArrowArrayStream CombineSpill(RecordBatch targetStates, RecordBatch batch);

    /// <summary>
    /// Spillable finalize: <paramref name="states"/> is a single BLOB column (NULL = fresh/empty); returns one
    /// result column, same order.
    /// </summary>
    IArrowArrayStream FinalizeSpill(RecordBatch states);
}
