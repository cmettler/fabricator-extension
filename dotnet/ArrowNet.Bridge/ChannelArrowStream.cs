using System.Threading.Channels;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace ArrowNet.Bridge;

/// <summary>
/// Adapts a bounded <see cref="ChannelReader{RecordBatch}"/> to an
/// <see cref="IArrowArrayStream"/> so the streaming bulk-load consumer (an
/// <see cref="ArrowDataReader"/> driving SqlBulkCopy) can pull record batches as
/// the host pushes them. The schema is fixed up front (from <c>begin_bulk</c>);
/// batches arrive one at a time and are disposed by the reader as they are
/// consumed. Reading blocks until a batch is available or the channel completes
/// (returns <c>null</c> = end of stream). If the channel was completed with an
/// exception (abort), it propagates here.
/// </summary>
internal sealed class ChannelArrowStream : IArrowArrayStream
{
    private readonly ChannelReader<RecordBatch> _reader;

    public ChannelArrowStream(Schema schema, ChannelReader<RecordBatch> reader)
    {
        Schema = schema;
        _reader = reader;
    }

    public Schema Schema { get; }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        // WaitToReadAsync returns false once the channel is completed and drained,
        // or throws if it was completed with an exception (abort).
        if (await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
            _reader.TryRead(out var batch))
        {
            return batch;
        }
        return null;
    }

    // Batches are owned/disposed by the ArrowDataReader as it reads them; any left
    // unread on completion are drained by the producing BulkSession. Nothing to do here.
    public void Dispose()
    {
    }
}
