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
        IReadOnlyDictionary<string, string>? metadata = (col.DataTypeName ?? string.Empty).ToLowerInvariant() switch
        {
            // Canonical Arrow extensions DuckDB's import recognizes: json (Utf8 storage) -> JSON,
            // uuid (FixedSizeBinary(16) storage) -> UUID. Unregistered => graceful fallback to storage type.
            "json" => new Dictionary<string, string> { ["ARROW:extension:name"] = "arrow.json" },
            "uniqueidentifier" => new Dictionary<string, string> { ["ARROW:extension:name"] = "arrow.uuid" },
            _ => null,
        };
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
                return new Time64Type(FractionalUnit(col));
            case "datetime":
            case "datetime2":
            case "smalldatetime":
                return new TimestampType(FractionalUnit(col), (string?)null);
            case "datetimeoffset":
                // Keep microsecond: DuckDB TIMESTAMPTZ is microsecond-only (no ns+tz type), so a tz instant
                // stays TIMESTAMPTZ (the 7th digit is dropped — the same minor tradeoff as before, but the
                // type is correct). Only naive datetime2/time go scale-aware ns above.
                return new TimestampType(TimeUnit.Microsecond, "UTC");
            case "binary":
            case "varbinary":
            case "image":
            case "timestamp":   // SQL rowversion (8 bytes)
            case "rowversion":
                return BinaryType.Default;
            case "uniqueidentifier":
                // FixedSizeBinary(16) + the arrow.uuid extension (set in ToArrowField) -> DuckDB UUID.
                // The value appender writes the 16 bytes in canonical RFC-4122 (big-endian) order.
                return new FixedSizeBinaryType(16);
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

    // SQL Server fractional-seconds scale -> Arrow time unit, for naive time/datetime2 (datetimeoffset
    // stays microsecond TIMESTAMPTZ — see above). Scale 7 (time(7)/datetime2(7) — incl. the datetime2/time
    // DEFAULT of 7) carries 100ns resolution, so map it to Nanosecond (DuckDB TIME_NS / TIMESTAMP_NS) to
    // preserve the 7th fractional digit; scale <= 6 fits Microsecond (DuckDB TIME / TIMESTAMP — the common
    // types, full date range). NumericScale is the fractional scale for these types (datetime/smalldatetime
    // report <= 3 -> Microsecond); a null (older driver) falls back to Microsecond.
    //   CAVEAT: DuckDB TIMESTAMP_NS spans only ~1677..2262, so a datetime2(7) value outside that range
    //   errors LOUDLY on read (a Conversion Error, never silent corruption) — an extreme edge for
    //   100ns-precision timestamps. time(7) (time-of-day) has no range concern.
    private static TimeUnit FractionalUnit(DbColumn col) =>
        (col.NumericScale ?? 0) >= 7 ? TimeUnit.Nanosecond : TimeUnit.Microsecond;

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
