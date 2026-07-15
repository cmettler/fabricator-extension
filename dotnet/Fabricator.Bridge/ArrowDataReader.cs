using System.Collections;
using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Adapts an <see cref="IArrowArrayStream"/> to a forward-only
/// <see cref="DbDataReader"/> so Arrow record batches can be bulk-loaded into a
/// target (e.g. SqlBulkCopy). Backend-agnostic (lives in the bridge). Reads
/// synchronously and owns the underlying stream.
/// </summary>
public sealed class ArrowDataReader : DbDataReader
{
    private readonly IArrowArrayStream _stream;
    private readonly Schema _schema;
    private RecordBatch? _batch;
    private int _rowInBatch = -1;
    private bool _closed;

    public ArrowDataReader(IArrowArrayStream stream)
    {
        _stream = stream;
        _schema = stream.Schema;
    }

    public override int FieldCount => _schema.FieldsList.Count;
    public override bool HasRows => true; // unknown up-front; SqlBulkCopy doesn't require accuracy
    public override int Depth => 0;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;

    public override string GetName(int ordinal) => _schema.FieldsList[ordinal].Name;
    public override Type GetFieldType(int ordinal) => ClrType(_schema.FieldsList[ordinal].DataType);
    public override string GetDataTypeName(int ordinal) => _schema.FieldsList[ordinal].DataType.Name;

    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < _schema.FieldsList.Count; i++)
        {
            if (string.Equals(_schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        throw new IndexOutOfRangeException(name);
    }

    public override bool Read()
    {
        while (true)
        {
            if (_batch == null)
            {
                _batch = _stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                _rowInBatch = -1;
                if (_batch == null)
                {
                    return false; // end of stream
                }
            }
            _rowInBatch++;
            if (_rowInBatch < _batch.Length)
            {
                return true;
            }
            _batch.Dispose();
            _batch = null;
        }
    }

    public override bool IsDBNull(int ordinal)
    {
        var array = _batch!.Column(ordinal);
        return array.IsNull(_rowInBatch);
    }

    public override object GetValue(int ordinal)
    {
        var array = _batch!.Column(ordinal);
        return ValueAt(array, _rowInBatch) ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++)
        {
            values[i] = GetValue(i);
        }
        return n;
    }

    // Extracts a CLR value (suited to SqlBulkCopy) from an Arrow array element.
    private static object? ValueAt(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        switch (array)
        {
            case BooleanArray a: return a.GetValue(index);
            case Int8Array a: return a.GetValue(index);
            case UInt8Array a: return a.GetValue(index);
            case Int16Array a: return a.GetValue(index);
            case UInt16Array a: return a.GetValue(index);
            case Int32Array a: return a.GetValue(index);
            case UInt32Array a: return a.GetValue(index);
            case Int64Array a: return a.GetValue(index);
            case UInt64Array a: return a.GetValue(index);
            case FloatArray a: return a.GetValue(index);
            case DoubleArray a: return a.GetValue(index);
            case Decimal128Array a: return a.GetValue(index);
            case Decimal256Array a: return a.GetString(index);
            case Date32Array a: return a.GetDateTime(index);
            case Date64Array a: return a.GetDateTime(index);
            case Time32Array a: return Time32ToTimeSpan(a, index);
            case Time64Array a: return Time64ToTimeSpan(a, index);
            case TimestampArray a: return a.GetTimestamp(index)?.UtcDateTime;
            case StringArray a: return a.GetString(index);
            case BinaryArray a: return a.GetBytes(index).ToArray();
            default: return array.GetType().Name; // last-resort: should not happen for our producers
        }
    }

    private static object? Time32ToTimeSpan(Time32Array array, int index)
    {
        int? value = array.GetValue(index);
        if (value is null)
        {
            return null;
        }
        var unit = ((Time32Type)array.Data.DataType).Unit;
        long ticks = unit == TimeUnit.Second ? value.Value * 10_000_000L : value.Value * 10_000L; // ms -> ticks
        return TimeSpan.FromTicks(ticks);
    }

    private static object? Time64ToTimeSpan(Time64Array array, int index)
    {
        long? value = array.GetValue(index);
        if (value is null)
        {
            return null;
        }
        var unit = ((Time64Type)array.Data.DataType).Unit;
        long ticks = unit == TimeUnit.Nanosecond ? value.Value / 100 : value.Value * 10; // microseconds -> ticks
        return TimeSpan.FromTicks(ticks);
    }

    private static Type ClrType(IArrowType type) => type.TypeId switch
    {
        ArrowTypeId.Boolean => typeof(bool),
        ArrowTypeId.Int8 => typeof(sbyte),
        ArrowTypeId.UInt8 => typeof(byte),
        ArrowTypeId.Int16 => typeof(short),
        ArrowTypeId.UInt16 => typeof(ushort),
        ArrowTypeId.Int32 => typeof(int),
        ArrowTypeId.UInt32 => typeof(uint),
        ArrowTypeId.Int64 => typeof(long),
        ArrowTypeId.UInt64 => typeof(ulong),
        ArrowTypeId.Float => typeof(float),
        ArrowTypeId.Double => typeof(double),
        ArrowTypeId.Decimal128 => typeof(decimal),
        ArrowTypeId.Date32 or ArrowTypeId.Date64 => typeof(DateTime),
        ArrowTypeId.Time64 or ArrowTypeId.Time32 => typeof(TimeSpan),
        ArrowTypeId.Timestamp => typeof(DateTime),
        ArrowTypeId.Binary => typeof(byte[]),
        _ => typeof(string),
    };

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_closed)
        {
            _closed = true;
            _batch?.Dispose();
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
