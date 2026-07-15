using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// Minimal <see cref="IArrowArrayStream"/> over a fixed set of in-memory record
/// batches. Used for the Phase 0 round-trip and for small metadata messages.
/// Backends that stream from a <see cref="System.Data.Common.DbDataReader"/>
/// implement their own lazy <see cref="IArrowArrayStream"/> instead.
/// </summary>
public sealed class InMemoryArrayStream : IArrowArrayStream
{
    private readonly Queue<RecordBatch> _batches;

    public InMemoryArrayStream(Schema schema, IEnumerable<RecordBatch> batches)
    {
        Schema = schema;
        _batches = new Queue<RecordBatch>(batches);
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        => new(_batches.Count > 0 ? _batches.Dequeue() : null);

    public void Dispose()
    {
        while (_batches.Count > 0)
        {
            _batches.Dequeue().Dispose();
        }
    }
}
