using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// A pull-based <see cref="IArrowArrayStream"/> over an <see cref="IAsyncEnumerable{T}"/> of record batches.
/// The host pulls synchronously (the Arrow C-stream exporter blocks on <see cref="ReadNextRecordBatchAsync"/>)
/// and each pull advances the async enumerator — sync-over-async at the boundary, which can't deadlock in the
/// hostfxr CLR (no <c>SynchronizationContext</c>). Streams lazily (no buffering). An optional
/// <c>owner</c> (e.g. the producing binding) is disposed when the stream closes, so resources the enumerable
/// depends on outlive the pulls.
/// </summary>
public sealed class AsyncEnumerableArrowStream : IArrowArrayStream
{
    private readonly IAsyncEnumerator<RecordBatch> _enumerator;
    private readonly IDisposable? _owner;
    private bool _disposed;

    public AsyncEnumerableArrowStream(Schema schema, IAsyncEnumerable<RecordBatch> batches, IDisposable? owner = null)
    {
        Schema = schema;
        _enumerator = batches.GetAsyncEnumerator();
        _owner = owner;
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        bool has = _enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        return new ValueTask<RecordBatch?>(has ? _enumerator.Current : null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); // runs the enumerable's finally
        }
        catch
        {
            // best-effort teardown
        }
        _owner?.Dispose();
    }
}
