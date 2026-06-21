using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A provider-authored custom table-in-out function, implemented in C# over Arrow - the in-out analog of
/// <see cref="IArrowScalarFunction"/> (4e) / <see cref="IArrowTableFunction"/> (4f). It consumes a TABLE
/// and produces a TABLE (e.g. a streaming transform or a running aggregate). It is surfaced into every
/// attached catalog and resolves as <c>SELECT * FROM db.SchemaName.Name(&lt;input table&gt;)</c>, running
/// through the very same in-out operator path as a discovered TVF's <c>_each</c> alias - but the session
/// dispatches to this object (see <c>SqlServerCatalog.InOutOpen</c>) instead of generating a CROSS APPLY.
/// No extra ABI (reuses <c>inout_open</c>/<c>inout_push</c>/<c>inout_abort</c>, ABI v23).
///
/// Unlike a discovered TVF's <c>_each</c> (whose output echoes the input columns ++ the TVF output), a
/// custom in-out declares its <see cref="OutputSchema"/> in full - it need not echo its input.
///
/// Per-chunk streaming only. Output is emitted synchronously per input chunk: each <see cref="Process"/>
/// call returns that chunk's complete output, emitted immediately. There is deliberately no "emit at end"
/// hook - a function that must consume the whole table before emitting (a non-running aggregate /
/// whole-table summary) is a pipeline breaker, not a streaming in-out, and cannot reliably emit its final
/// rows across parallel input branches (the row-emitting operator finalize fires per branch). A running
/// aggregate that emits per row works (emit during <see cref="Process"/>), and may keep mutable state
/// across calls since <see cref="Process"/> is invoked serially by the session.
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
    /// Process one input chunk and return zero or more output batches (emitted immediately). Called once
    /// per input chunk, in arrival order (serially). Each returned batch must conform to
    /// <see cref="OutputSchema"/>. The chunk is disposed after this returns, so do not retain views into
    /// it (copy what you keep).
    /// </summary>
    IEnumerable<RecordBatch> Process(RecordBatch inputChunk);
}
