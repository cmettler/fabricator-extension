using Apache.Arrow;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge;

/// <summary>
/// Reads a single scalar (row <paramref name="index"/>) out of an Arrow array as a CLR value — used to turn
/// a pushed-down filter value batch into CLR values (SQL parameters, DAX literals, …). Provider-agnostic, so
/// it lives in the Bridge. Unhandled types throw; callers treat that as "don't push this predicate" (DuckDB
/// re-applies it), so coverage gaps cost performance, never correctness.
/// </summary>
public static class ArrowValueReader
{
    public static object? ReadScalar(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        return array switch
        {
            BooleanArray a => a.GetValue(index),
            Int8Array a => a.GetValue(index),
            Int16Array a => a.GetValue(index),
            Int32Array a => a.GetValue(index),
            Int64Array a => a.GetValue(index),
            UInt8Array a => a.GetValue(index),
            UInt16Array a => a.GetValue(index),
            UInt32Array a => a.GetValue(index),
            UInt64Array a => a.GetValue(index),
            FloatArray a => a.GetValue(index),
            DoubleArray a => a.GetValue(index),
            Decimal128Array a => a.GetValue(index),
            Decimal256Array a => a.GetValue(index),
            StringArray a => a.GetString(index),
            LargeStringArray a => a.GetString(index),
            BinaryArray a => a.GetBytes(index).ToArray(),
            Date32Array a => a.GetDateTime(index),
            Date64Array a => a.GetDateTime(index),
            TimestampArray a => ReadTimestamp(a, index),
            _ => throw new NotSupportedException($"arrownet: unsupported filter value type {array.Data.DataType.TypeId}"),
        };
    }

    private static object ReadTimestamp(TimestampArray a, int index)
    {
        var ts = a.GetTimestamp(index)!.Value; // DateTimeOffset (stored as UTC when no tz)
        var type = (TimestampType)a.Data.DataType;
        // No timezone => a wall-clock value (SQL datetime2): hand back a DateTime.
        // With timezone (SQL datetimeoffset): hand back the DateTimeOffset.
        return string.IsNullOrEmpty(type.Timezone) ? ts.UtcDateTime : ts;
    }
}
