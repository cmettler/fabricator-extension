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
/// command, and connection. Reads synchronously so the Arrow C-stream exporter
/// (which blocks on the returned task) never deadlocks.
/// </summary>
public sealed class DbDataReaderArrowStream : IArrowArrayStream
{
    private readonly DbConnection _connection;
    private readonly DbCommand _command;
    private readonly DbDataReader _reader;
    private readonly IArrowType[] _columnTypes;
    private readonly int _batchSize;
    private readonly bool _ownsConnection;
    private bool _done;

    public DbDataReaderArrowStream(DbConnection connection, DbCommand command, DbDataReader reader,
                                   int batchSize = 2048, bool ownsConnection = true)
    {
        _connection = connection;
        _command = command;
        _reader = reader;
        _batchSize = batchSize;
        _ownsConnection = ownsConnection;

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
        => new(ReadNextBatch());

    private RecordBatch? ReadNextBatch()
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
        while (rows < _batchSize && _reader.Read())
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
    }
}
