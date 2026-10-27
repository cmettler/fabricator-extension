// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Data.SqlTypes;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Turns one Parquet-variant value (the <c>value</c> bytes of an <c>arrow.parquet.variant</c> row) into the
/// CLR value a SQL Server <c>sql_variant</c> column accepts through SqlBulkCopy — exactly, or not at all.
/// SqlBulkCopy picks the stored base type from the CLR type, MEASURED on SQL Server 2025:
/// <list type="bullet">
/// <item><see cref="DateTime"/> becomes the legacy <c>datetime</c>, which ROUNDS silently to 1/300 s and fails
/// before 1753. So a timestamp without time zone (and a date) is written only when <c>datetime</c> holds it
/// exactly; otherwise it is refused, naming the cast that does store it (TIMESTAMPTZ → datetimeoffset(7)).</item>
/// <item>A string above 4000 UTF-16 code units becomes <c>varchar</c> — a code page, so non-ASCII characters
/// would be lost silently. Refused.</item>
/// <item><see cref="DateTimeOffset"/> → datetimeoffset(7) and <see cref="TimeSpan"/> → time(7) are exact;
/// <see cref="DateOnly"/> breaks the TDS stream and <see cref="sbyte"/> is refused, so neither is used.</item>
/// </list>
/// An object or an array has no sql_variant form and is refused, pointing at <c>v::JSON</c>.
/// </summary>
public static class VariantClrValue
{
    /// <summary>The <c>arrow.parquet.variant</c> extension name, as field metadata or as <see cref="VariantType"/>.</summary>
    public static bool IsVariantField(Field field) =>
        field.DataType is VariantType
        || (field.Metadata is { } md
            && md.TryGetValue("ARROW:extension:name", out var ext)
            && string.Equals(ext, "arrow.parquet.variant", StringComparison.Ordinal));

    /// <summary>The value bytes of row <paramref name="index"/> of a variant column (struct storage).</summary>
    public static ReadOnlySpan<byte> ValueBytes(IArrowArray array, int index)
    {
        var storage = array as StructArray ?? (array as VariantArray)?.Storage as StructArray
            ?? throw new NotSupportedException($"a variant column arrived as {array.GetType().Name}");
        var type = (StructType)storage.Data.DataType;
        int valueField = type.GetFieldIndex("value");
        if (valueField < 0)
        {
            throw new NotSupportedException("a variant column has no 'value' field");
        }
        return ((BinaryArray)storage.Fields[valueField]).GetBytes(index);
    }

    private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>The CLR value for a sql_variant column, or <see cref="DBNull.Value"/> for a variant null.</summary>
    public static object ToSqlVariant(ReadOnlySpan<byte> value, string column)
    {
        if (value.IsEmpty)
        {
            throw Refuse(column, "an empty variant value");
        }
        byte header = value[0];
        int basic = header & 0x03;
        int type = header >> 2;
        switch (basic)
        {
            case 1: // short string: the length is in the header
                return String(value.Slice(1, type), column);
            case 2:
                throw Refuse(column, "a variant OBJECT, which sql_variant cannot store; cast it to JSON (v::JSON)");
            case 3:
                throw Refuse(column, "a variant ARRAY, which sql_variant cannot store; cast it to JSON (v::JSON)");
        }
        ReadOnlySpan<byte> p = value.Slice(1);
        switch (type)
        {
            case 0: return DBNull.Value;
            case 1: return true;
            case 2: return false;
            case 3: return (short)(sbyte)p[0]; // int8: sql_variant has no signed tinyint
            case 4: return BinaryPrimitives.ReadInt16LittleEndian(p);
            case 5: return BinaryPrimitives.ReadInt32LittleEndian(p);
            case 6: return BinaryPrimitives.ReadInt64LittleEndian(p);
            case 7: return BinaryPrimitives.ReadDoubleLittleEndian(p);
            case 8: return Decimal(p[0], BinaryPrimitives.ReadInt32LittleEndian(p.Slice(1)), column);
            case 9: return Decimal(p[0], BinaryPrimitives.ReadInt64LittleEndian(p.Slice(1)), column);
            case 10: return Decimal(p[0], BinaryPrimitives.ReadInt128LittleEndian(p.Slice(1)), column);
            case 11: // date: days since 1970
                return Datetime(BinaryPrimitives.ReadInt32LittleEndian(p) * TimeSpan.TicksPerDay, column, "date");
            case 12: // timestamp with time zone, microseconds since 1970 UTC
                return Offset(BinaryPrimitives.ReadInt64LittleEndian(p) * 10, column);
            case 13: // timestamp without time zone, microseconds
                return Datetime(BinaryPrimitives.ReadInt64LittleEndian(p) * 10, column, "timestamp");
            case 14: return BinaryPrimitives.ReadSingleLittleEndian(p);
            case 15:
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(p);
                if (length > 8000)
                {
                    throw Refuse(column, $"a binary value of {length} bytes; sql_variant holds at most 8000");
                }
                return p.Slice(4, length).ToArray();
            }
            case 16:
                return String(p.Slice(4, BinaryPrimitives.ReadInt32LittleEndian(p)), column);
            case 17: // time, microseconds
                return TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(p) * 10);
            case 18: // timestamp with time zone, nanoseconds
                return Offset(FloorDiv(BinaryPrimitives.ReadInt64LittleEndian(p), 100), column);
            case 19: // timestamp without time zone, nanoseconds
                return Datetime(FloorDiv(BinaryPrimitives.ReadInt64LittleEndian(p), 100), column, "timestamp");
            case 20: return new Guid(p.Slice(0, 16), bigEndian: true);
            default:
                throw Refuse(column, $"a variant primitive of type {type}, which has no sql_variant form");
        }
    }

    private static string String(ReadOnlySpan<byte> utf8, string column)
    {
        string s = Encoding.UTF8.GetString(utf8);
        if (s.Length > 4000)
        {
            // SqlBulkCopy writes a longer string as varchar, i.e. in a code page: non-ASCII text would be lost.
            throw Refuse(column, $"a string of {s.Length} characters; sql_variant holds at most 4000 as nvarchar");
        }
        return s;
    }

    private static decimal Decimal(byte scale, Int128 unscaled, string column)
    {
        UInt128 magnitude = unscaled < 0 ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (scale > 28 || magnitude >> 96 != 0)
        {
            throw Refuse(column, "a decimal beyond 28 digits of scale or 96 bits, which SqlClient cannot carry");
        }
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64),
                           unscaled < 0, scale);
    }

    /// <summary>datetime only when it holds the value exactly (1/300 s, from 1753).</summary>
    private static DateTime Datetime(long ticksSinceEpoch, string column, string what)
    {
        var value = new DateTime(UnixEpochTicks + ticksSinceEpoch, DateTimeKind.Unspecified);
        bool exact = value >= SqlDateTime.MinValue.Value && value <= SqlDateTime.MaxValue.Value
                     && new SqlDateTime(value).Value == value;
        if (!exact)
        {
            throw Refuse(column,
                $"the {what} {value:yyyy-MM-dd HH:mm:ss.fffffff}, which sql_variant would store as datetime "
                + "(1/300 s precision, from 1753) and so round or reject; cast it to TIMESTAMPTZ to store it "
                + "exactly as datetimeoffset(7)");
        }
        return value;
    }

    private static DateTimeOffset Offset(long ticksSinceEpoch, string column) =>
        new(UnixEpochTicks + ticksSinceEpoch, TimeSpan.Zero);

    private static long FloorDiv(long a, long b) => a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0);

    private static NotSupportedException Refuse(string column, string what) =>
        new($"Column '{column}' holds {what}.");
}
