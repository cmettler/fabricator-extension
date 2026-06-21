using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A table-in-out execution session (Phase 4). One session per in-out call streams a TABLE in and a
/// TABLE out. The parallel input branches all push into the <em>one</em> session (thread-safe); the host
/// signals the single all-input-done point via <see cref="Finish"/> (driven by an injected
/// <c>OperatorFinalize</c>, since <c>in_out_function_final</c> fires once per branch), and the
/// operator-state destructor calls <see cref="Abort"/> on LIMIT/error/cancel.
/// </summary>
public interface IInOutSession
{
    /// <summary>The Arrow schema of the input table (its columns are the function's positional params).</summary>
    Schema InputSchema { get; }

    /// <summary>Enqueue one input chunk (thread-safe across parallel branches; backpressured).</summary>
    void Push(RecordBatch chunk);

    /// <summary>Output rows available so far (may be empty). Non-blocking.</summary>
    IArrowArrayStream DrainReady();

    /// <summary>Input exhausted: complete the session and return all remaining output. Idempotent.</summary>
    IArrowArrayStream Finish();

    /// <summary>Release/cancel (LIMIT/error/cancel backstop). Idempotent.</summary>
    void Abort();
}
