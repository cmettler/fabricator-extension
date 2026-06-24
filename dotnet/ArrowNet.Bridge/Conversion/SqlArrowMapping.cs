using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge.Conversion;

/// <summary>
/// Maps a <see cref="DbColumn"/> (as reported by a SQL Server
/// <see cref="DbDataReader.GetColumnSchema"/>) to a standard Apache.Arrow type.
/// Ported/adapted from SqlServerFlights' FlightField mapping, but built against
/// the standard Apache.Arrow package (no custom fork).
/// </summary>
public static class SqlArrowMapping
{
    public static Field ToArrowField(DbColumn col)
    {
        var name = col.ColumnName ?? $"column{col.ColumnOrdinal ?? 0}";
        var nullable = col.AllowDBNull ?? true;
        // A SQL Server 2025+ native `json` column is tagged with the canonical Arrow `arrow.json`
        // extension so DuckDB's import lands it as the JSON logical type (when the json extension is
        // available; an unregistered extension falls back to the Utf8 storage type = VARCHAR, so the
        // value round-trips either way). Storage type is Utf8 in both cases.
        IReadOnlyDictionary<string, string>? metadata = null;
        if ((col.DataTypeName ?? string.Empty).Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            metadata = new Dictionary<string, string> { ["ARROW:extension:name"] = "arrow.json" };
        }
        return new Field(name, MapType(col), nullable, metadata);
    }

    public static IArrowType MapType(DbColumn col)
    {
        var sql = (col.DataTypeName ?? string.Empty).ToLowerInvariant();
        switch (sql)
        {
            case "bit":
                return BooleanType.Default;
            case "tinyint":
                return UInt8Type.Default; // SQL TINYINT is unsigned 0..255
            case "smallint":
                return Int16Type.Default;
            case "int":
                return Int32Type.Default;
            case "bigint":
                return Int64Type.Default;
            case "real":
                return FloatType.Default;
            case "float":
                return DoubleType.Default;
            case "decimal":
            case "numeric":
                return MakeDecimal(col);
            case "money":
                return new Decimal128Type(19, 4);
            case "smallmoney":
                return new Decimal128Type(10, 4);
            case "date":
                return Date32Type.Default;
            case "time":
                return new Time64Type(TimeUnit.Microsecond);
            case "datetime":
            case "datetime2":
            case "smalldatetime":
                return new TimestampType(TimeUnit.Microsecond, (string?)null);
            case "datetimeoffset":
                return new TimestampType(TimeUnit.Microsecond, "UTC");
            case "binary":
            case "varbinary":
            case "image":
            case "timestamp":   // SQL rowversion (8 bytes)
            case "rowversion":
                return BinaryType.Default;
            case "uniqueidentifier":
                // Surface as text for Phase 1 (readable, lossless round-trip of the value).
                return StringType.Default;
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            case "text":
            case "ntext":
            case "xml":
            case "sysname":
            case "json": // SQL Server 2025 native json; ToArrowField tags it arrow.json (Utf8 storage)
                return StringType.Default;
            default:
                // Fall back on the CLR type for anything unmapped.
                return MapClrType(col.DataType) ?? StringType.Default;
        }
    }

    private static IArrowType MakeDecimal(DbColumn col)
    {
        int precision = col.NumericPrecision is { } p && p > 0 ? p : 38;
        int scale = col.NumericScale ?? 0;
        if (precision > 38)
        {
            precision = 38;
        }
        if (scale > precision)
        {
            scale = precision;
        }
        return new Decimal128Type(precision, scale);
    }

    private static IArrowType? MapClrType(Type? t)
    {
        if (t == null)
        {
            return null;
        }
        if (t == typeof(bool))
        {
            return BooleanType.Default;
        }
        if (t == typeof(byte))
        {
            return UInt8Type.Default;
        }
        if (t == typeof(short))
        {
            return Int16Type.Default;
        }
        if (t == typeof(int))
        {
            return Int32Type.Default;
        }
        if (t == typeof(long))
        {
            return Int64Type.Default;
        }
        if (t == typeof(float))
        {
            return FloatType.Default;
        }
        if (t == typeof(double))
        {
            return DoubleType.Default;
        }
        if (t == typeof(decimal))
        {
            return new Decimal128Type(38, 9);
        }
        if (t == typeof(DateTime))
        {
            return new TimestampType(TimeUnit.Microsecond, (string?)null);
        }
        if (t == typeof(DateTimeOffset))
        {
            return new TimestampType(TimeUnit.Microsecond, "UTC");
        }
        if (t == typeof(TimeSpan))
        {
            return new Time64Type(TimeUnit.Microsecond);
        }
        if (t == typeof(byte[]))
        {
            return BinaryType.Default;
        }
        if (t == typeof(Guid))
        {
            return StringType.Default;
        }
        return StringType.Default;
    }
}
