using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom <em>table-in-out</em> function, implemented in C# over Arrow — the in-out
/// analog of <see cref="IArrowScalarFunction"/> (4e) / <see cref="IArrowTableFunction"/> (4f). It consumes
/// a TABLE and produces a TABLE, so it can be <b>stateful across the whole input stream</b> (a running
/// aggregate, a windowed transform, a whole-table summary) — something a per-call scalar/table function
/// cannot express. It is surfaced into every attached catalog and resolves as
/// <c>SELECT * FROM db.SchemaName.Name(&lt;input table&gt;)</c>, running through the very same in-out operator
/// path as a discovered TVF's <c>_each</c> alias — but the session dispatches to this object (see
/// <c>SqlServerCatalog.InOutOpen</c>) instead of generating a CROSS APPLY. No extra ABI (reuses
/// <c>inout_open</c>/<c>inout_push</c>/<c>inout_finish</c>/<c>inout_abort</c>, ABI v23).
///
/// Unlike a discovered TVF's <c>_each</c> (whose output echoes the input columns ++ the TVF output), a
/// custom in-out declares its <see cref="OutputSchema"/> in full — it need not echo its input.
///
/// Threading: <see cref="Process"/>/<see cref="Finish"/> are invoked serially by the session (one input
/// chunk at a time, then one Finish), so an implementation may keep mutable state without locking.
/// </summary>
public interface IArrowTableInOutFunction
{
    /// <summary>Target catalog schema (e.g. "dbo"); created on attach if it isn't already present.</summary>
    string SchemaName { get; }

    /// <summary>Function name, as called: <c>SELECT * FROM db.SchemaName.Name(&lt;input table&gt;)</c>.</summary>
    string Name { get; }

    /// <summary>The expected input-table columns (names + Arrow types). Declarative (drives discovery).</summary>
    Schema InputSchema { get; }

    /// <summary>The result columns (names + Arrow types). Fixed (known at bind time).</summary>
    Schema OutputSchema { get; }

    /// <summary>
    /// Process one input chunk and return zero or more output batches (streamed). Called once per input
    /// chunk, in arrival order. Each returned batch must conform to <see cref="OutputSchema"/>. The chunk
    /// is disposed after this returns, so do not retain views into it (copy what you keep).
    /// </summary>
    IEnumerable<RecordBatch> Process(RecordBatch inputChunk);

    /// <summary>
    /// Input exhausted — return any final output (e.g. a running aggregate's result, a whole-table
    /// summary). May be empty. Each returned batch must conform to <see cref="OutputSchema"/>.
    /// </summary>
    IEnumerable<RecordBatch> Finish();
}
