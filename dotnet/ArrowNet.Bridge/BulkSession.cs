using System.Collections.Generic;
using System.Threading.Channels;
using Apache.Arrow;

namespace ArrowNet.Bridge;

/// <summary>
/// A streaming bulk-load session: the C++ host pushes Arrow record batches one at
/// a time (<c>push_batch</c>) while a background task drains them into the backend
/// via <see cref="IBackendCatalog.BulkInsert"/> (e.g. SqlBulkCopy). A bounded
/// channel provides backpressure — <see cref="Push"/> blocks while the channel is
/// full so peak memory stays bounded regardless of dataset size. This offloads the
/// bulk-copy concurrency to the .NET thread pool: the host's sink thread only fills
/// the channel; the consumer task pulls and writes.
/// </summary>
internal sealed class BulkSession
{
    // Batches in flight before the host's push blocks. Each batch is one sink chunk
    // (<= a DuckDB vector). A handful is enough to overlap encoding with the write
    // while keeping memory bounded.
    private const int ChannelCapacity = 8;

    private readonly Channel<RecordBatch> _channel;
    private readonly Task<long> _consumer;
    // Cancelled when the consumer task exits (done or faulted) so a blocked Push
    // never deadlocks on a full channel whose reader is gone.
    private readonly CancellationTokenSource _consumerExited = new();

    public Schema Schema { get; }

    public BulkSession(IBackendCatalog catalog, string schemaName, string tableName, Schema schema, bool createTable,
                       bool replace, bool checkConstraints, long txnId, nint opener = 0,
                       IReadOnlyList<string>? partitionColumns = null, IReadOnlyList<string>? sortColumns = null,
                       string? schemaMode = null)
    {
        Schema = schema;
        _channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var reader = _channel.Reader;
        _consumer = Task.Run(() =>
        {
            // Re-establish the per-thread ambients on the consumer thread (it's a different thread than
            // begin_bulk): the transaction id (so a SQL provider keys its per-transaction connection) and the
            // host-FS opener (so a host-FS provider — the Delta catalog — resolves DuckDB secrets while writing
            // through DuckDB's FileSystem). The opener's ClientContext stays valid until complete_bulk returns.
            AmbientTransaction.Current = txnId;
            AmbientOpener.Current = opener;
            try
            {
                return catalog.BulkInsert(schemaName, tableName, new ChannelArrowStream(schema, reader), createTable,
                                          replace, checkConstraints, txnId, partitionColumns, sortColumns, schemaMode);
            }
            finally
            {
                // On ANY exit (success, fault, or abort): close the channel so further
                // pushes fail fast, unblock a pending push, and dispose any batches still
                // queued so nothing leaks.
                _channel.Writer.TryComplete();
                _consumerExited.Cancel();
                while (reader.TryRead(out var leftover))
                {
                    leftover.Dispose();
                }
            }
        });
    }

    /// <summary>
    /// Enqueues one batch, blocking for backpressure while the channel is full. If
    /// the consumer has already exited (typically because the bulk-copy faulted),
    /// the batch is dropped and disposed — the real error surfaces from
    /// <see cref="Complete"/>. Takes ownership of <paramref name="batch"/>.
    /// </summary>
    public void Push(RecordBatch batch)
    {
        try
        {
            _channel.Writer.WriteAsync(batch, _consumerExited.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            batch.Dispose();
        }
    }

    /// <summary>
    /// Signals end-of-stream, waits for the background load to finish, and returns
    /// rows written. Rethrows the bulk-copy error if it faulted. When
    /// <paramref name="abort"/> is set the load is cancelled and errors are swallowed
    /// (cleanup path for a failed/cancelled query).
    /// </summary>
    public long Complete(bool abort)
    {
        if (abort)
        {
            // Fault the stream so the in-flight WriteToServer stops and (inside a
            // transaction) rolls back, rather than committing a partial load.
            _channel.Writer.TryComplete(new OperationCanceledException("bulk load aborted"));
            _consumerExited.Cancel();
            try
            {
                return _consumer.GetAwaiter().GetResult();
            }
            catch
            {
                return 0;
            }
            finally
            {
                _consumerExited.Dispose();
            }
        }

        _channel.Writer.TryComplete();
        try
        {
            return _consumer.GetAwaiter().GetResult();
        }
        finally
        {
            _consumerExited.Dispose();
        }
    }
}
