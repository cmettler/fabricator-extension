using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge.Conversion;

namespace Fabricator.Bridge;

/// <summary>
/// Streams a <see cref="DbDataReader"/> result set to the C++ host as Arrow
/// record batches, one batch per <see cref="ReadNextRecordBatchAsync"/> call.
/// Backend-agnostic (any ADO.NET provider); owns and disposes the reader,
/// command, and connection. Rows are fetched with <c>ReadAsync</c> so a query
/// interrupt (Ctrl+C / timeout) can cancel a blocking network fetch via the
/// optional <see cref="InterruptScope"/>; the Arrow C-stream exporter still
/// blocks once on the returned task (safe — the hostfxr CLR has no
/// SynchronizationContext, so sync-over-async can't deadlock). See
/// docs/cancellation.md. When no scope is supplied the token is <c>default</c>
/// and behavior is identical to a plain synchronous read.
/// </summary>
public sealed class DbDataReaderArrowStream : IArrowArrayStream
{
    private readonly DbConnection _connection;
    private readonly DbCommand _command;
    private readonly DbDataReader _reader;
    private readonly IArrowType[] _columnTypes;
    private readonly int _batchSize;
    private readonly bool _ownsConnection;
    private readonly InterruptScope? _interrupt;
    private readonly CancellationToken _token;
    private bool _done;

    public DbDataReaderArrowStream(DbConnection connection, DbCommand command, DbDataReader reader,
                                   int batchSize = 2048, bool ownsConnection = true,
                                   InterruptScope? interrupt = null)
    {
        _connection = connection;
        _command = command;
        _reader = reader;
        _batchSize = batchSize;
        _ownsConnection = ownsConnection;
        _interrupt = interrupt;
        _token = interrupt?.Token ?? default;

        var columns = reader.GetColumnSchema();
        var fields = new Field[columns.Count];
        _columnTypes = new IArrowType[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            fields[i] = SqlArrowMapping.ToArrowField(columns[i]);
            _columnTypes[i] = fields[i].DataType;
        }
        Schema = new Schema(fields, metadata: null);
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        => ReadNextBatchAsync();

    private async ValueTask<RecordBatch?> ReadNextBatchAsync()
    {
        if (_done)
        {
            return null;
        }

        var appenders = new ColumnAppender[_columnTypes.Length];
        for (int i = 0; i < appenders.Length; i++)
        {
            appenders[i] = ColumnAppender.Create(_columnTypes[i]);
        }

        int rows = 0;
        // ReadAsync honors the interrupt token — a blocking network packet fetch cancels on Ctrl+C/timeout.
        // For a provider without true async (e.g. ADOMD) DbDataReader.ReadAsync falls back to a synchronous
        // read, so this is safe everywhere; for buffered rows it completes synchronously (no overhead spike).
        while (rows < _batchSize && await _reader.ReadAsync(_token).ConfigureAwait(false))
        {
            for (int i = 0; i < appenders.Length; i++)
            {
                var value = _reader[i];
                if (value is null or DBNull)
                {
                    appenders[i].AppendNull();
                }
                else
                {
                    appenders[i].Append(value);
                }
            }
            rows++;
        }

        if (rows == 0)
        {
            _done = true;
            return null;
        }

        var arrays = new IArrowArray[appenders.Length];
        for (int i = 0; i < appenders.Length; i++)
        {
            arrays[i] = appenders[i].Build();
        }
        return new RecordBatch(Schema, arrays, rows);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _command.Dispose();
        // A borrowed (pinned-transaction) connection is owned by the catalog.
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
        // Stop the interrupt poller before the ClientContext it polls is freed.
        _interrupt?.Dispose();
    }
}
