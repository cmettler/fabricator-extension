using Apache.Arrow;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge.Conversion;

/// <summary>
/// Wraps a concrete Apache.Arrow array builder behind a uniform
/// append-object / append-null / build interface, with the CLR→Arrow value
/// conversion for each supported type. One appender per result column.
/// </summary>
public sealed class ColumnAppender
{
    private readonly Action<object> _append;
    private readonly Action _appendNull;
    private readonly Func<IArrowArray> _build;

    private ColumnAppender(Action<object> append, Action appendNull, Func<IArrowArray> build)
    {
        _append = append;
        _appendNull = appendNull;
        _build = build;
    }

    public void Append(object value) => _append(value);
    public void AppendNull() => _appendNull();
    public IArrowArray Build() => _build();

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static ColumnAppender Create(IArrowType type)
    {
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean:
            {
                var b = new BooleanArray.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToBoolean(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.UInt8:
            {
                var b = new UInt8Array.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToByte(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Int16:
            {
                var b = new Int16Array.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToInt16(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Int32:
            {
                var b = new Int32Array.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToInt32(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Int64:
            {
                var b = new Int64Array.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToInt64(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Float:
            {
                var b = new FloatArray.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToSingle(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Double:
            {
                var b = new DoubleArray.Builder();
                return new ColumnAppender(o => b.Append(Convert.ToDouble(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Decimal128:
            {
                var b = new Decimal128Array.Builder((Decimal128Type)type);
                return new ColumnAppender(o => b.Append(Convert.ToDecimal(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Date32:
            {
                var b = new Date32Array.Builder();
                return new ColumnAppender(o => b.Append(DateOnly.FromDateTime((DateTime)o)), () => b.AppendNull(),
                                          () => b.Build());
            }
            case ArrowTypeId.Time64:
            {
                var b = new Time64Array.Builder((Time64Type)type);
                return new ColumnAppender(o => b.Append(((TimeSpan)o).Ticks / 10L), // 100ns ticks -> microseconds
                                          () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Timestamp:
            {
                var b = new TimestampArray.Builder((TimestampType)type);
                return new ColumnAppender(o => b.Append(ToTimestamp(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.String:
            {
                var b = new StringArray.Builder();
                return new ColumnAppender(o => b.Append(ToStringValue(o)), () => b.AppendNull(), () => b.Build());
            }
            case ArrowTypeId.Binary:
            {
                var b = new BinaryArray.Builder();
                return new ColumnAppender(o => b.Append(((byte[])o).AsSpan()), () => b.AppendNull(), () => b.Build());
            }
            default:
                throw new NotSupportedException($"ArrowNet: no column appender for Arrow type {type.TypeId} ({type.Name})");
        }
    }

    private static DateTimeOffset ToTimestamp(object o) => o switch
    {
        DateTimeOffset dto => dto,
        // Treat naive SQL datetimes as UTC so the wall-clock value is preserved.
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => new DateTimeOffset(Convert.ToDateTime(o).ToUniversalTime()),
    };

    private static string ToStringValue(object o) => o switch
    {
        string s => s,
        Guid g => g.ToString("D"),
        _ => o.ToString() ?? string.Empty,
    };
}
