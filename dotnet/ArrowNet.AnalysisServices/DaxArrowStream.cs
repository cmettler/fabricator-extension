using System.Data;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using ArrowNet.Bridge.Conversion;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// Lazy streaming of an ADOMD/DAX result as Arrow — one batch per host pull. Holds + disposes the
/// connection / command / open <see cref="IDataReader"/>.
///
/// IMPORTANT: an <c>AdomdDataReader.Read()</c> called AFTER it has already returned <c>false</c> (i.e. past
/// end-of-data) does NOT return <c>false</c> again — it throws <c>AdomdUnknownResponseException</c> ("the
/// server sent an unrecognizable response", trying to parse the rowset's closing XML). DuckDB pulls one more
/// batch after the final (partial) one, so we MUST remember end-of-data and never call <c>Read()</c> again.
/// </summary>
internal sealed class DaxArrowStream : IArrowArrayStream
{
    private readonly IDbConnection _connection;
    private readonly IDbCommand _command;
    private readonly IDataReader _reader;
    private readonly IArrowType[] _columnTypes;
    private readonly int _batchSize;
    private bool _done;

    public DaxArrowStream(IDbConnection connection, IDbCommand command, IDataReader reader, Schema schema,
                          int batchSize = 2048)
    {
        _connection = connection;
        _command = command;
        _reader = reader;
        _batchSize = batchSize;
        Schema = schema;
        _columnTypes = new IArrowType[schema.FieldsList.Count];
        for (int i = 0; i < _columnTypes.Length; i++)
        {
            _columnTypes[i] = schema.FieldsList[i].DataType;
        }
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
        while (rows < _batchSize)
        {
            if (!_reader.Read())
            {
                // End of data. Mark done so we NEVER call Read() again (a read past end throws on ADOMD).
                _done = true;
                break;
            }
            for (int i = 0; i < appenders.Length; i++)
            {
                var value = _reader.GetValue(i);
                if (value is null or DBNull) { appenders[i].AppendNull(); } else { appenders[i].Append(value); }
            }
            rows++;
        }
        if (rows == 0)
        {
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
        try { _reader.Dispose(); } catch { }
        try { _command.Dispose(); } catch { }
        try { _connection.Dispose(); } catch { }
    }
}
