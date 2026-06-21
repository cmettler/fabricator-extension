using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A custom-aggregate execution session (Phase 4h). One session per bound aggregate holds the map from
/// DuckDB's per-group <c>int64</c> state ids to the live C# accumulators (see
/// <see cref="IArrowAggregateFunction"/>). The C++ aggregate callbacks marshal the state id(s) + argument
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
    /// function's <see cref="IArrowAggregateFunction.Parameters"/> (used to import the batch).
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
}
