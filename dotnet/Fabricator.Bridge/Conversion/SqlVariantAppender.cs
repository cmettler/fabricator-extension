// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge.Conversion;

/// <summary>
/// Builds a <see cref="VariantArray"/> (Arrow <c>arrow.parquet.variant</c>, unshredded) from the values a
/// <c>sql_variant</c> column returns through <c>DbDataReader.GetValue</c>. DuckDB 2.0 imports that as VARIANT.
/// Each value is encoded as the variant primitive of its own base type, after the mapping in mssql-adbc
/// (<c>VariantDecoders.cs</c>), which reads the same values off the TDS wire:
/// <list type="bullet">
/// <item>tinyint, smallint → int16; int → int32; bigint → int64; bit → boolean; real → float;
/// float → double.</item>
/// <item>decimal, numeric, money, smallmoney → decimal4/8/16, the narrowest that holds the value's digits and
/// scale (the variant format records width and scale, not the declared precision).</item>
/// <item>date, datetime, smalldatetime, datetime2 → timestamp without time zone. ⚠ SqlClient returns all four
/// as <see cref="DateTime"/>, so a <c>date</c> is not distinguishable from a <c>datetime2</c> at midnight and
/// arrives as a timestamp: the value is kept, the type is widened.</item>
/// <item>datetimeoffset → timestamp with time zone (UTC); the offset is not kept, the instant is.</item>
/// <item>A timestamp with a sub-microsecond part (datetime2(7) / datetimeoffset(7)) uses the nanosecond
/// encoding while it fits (1677 .. 2262); otherwise microseconds, truncating the last digit.</item>
/// <item>time → time (microseconds; the variant format has no nanosecond time, so time(7) loses its last
/// digit).</item>
/// <item>uniqueidentifier → uuid; character types → string; binary types → binary.</item>
/// </list>
/// </summary>
internal sealed class SqlVariantAppender
{
    // Parquet variant spec: value header = (primitive type << 2) | basic type (0 = primitive, 1 = short string).
    private const byte TypeTrue = 1;
    private const byte TypeFalse = 2;
    private const byte TypeInt16 = 4;
    private const byte TypeInt32 = 5;
    private const byte TypeInt64 = 6;
    private const byte TypeDouble = 7;
    private const byte TypeDecimal4 = 8;
    private const byte TypeDecimal8 = 9;
    private const byte TypeDecimal16 = 10;
    private const byte TypeTimestampMicros = 12;
    private const byte TypeTimestampNtzMicros = 13;
    private const byte TypeFloat = 14;
    private const byte TypeBinary = 15;
    private const byte TypeString = 16;
    private const byte TypeTimeNtzMicros = 17;
    private const byte TypeTimestampNanos = 18;
    private const byte TypeTimestampNtzNanos = 19;
    private const byte TypeUuid = 20;
    private const byte BasicShortString = 1;

    /// <summary>Metadata for values without objects: version 1, an empty dictionary.</summary>
    private static readonly byte[] EmptyMetadata = [0x01, 0x00, 0x00];

    private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private readonly BinaryArray.Builder _metadata = new();
    private readonly BinaryArray.Builder _value = new();
    private readonly ArrowBuffer.BitmapBuilder _validity = new();
    private readonly List<byte> _bytes = new(32);
    private int _length;
    private int _nullCount;

    public void Append(object o)
    {
        _bytes.Clear();
        Encode(o);
        _metadata.Append(EmptyMetadata.AsSpan());
        _value.Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes));
        _validity.Append(true);
        _length++;
    }

    public void AppendNull()
    {
        // The children carry a valid (variant null) value under the struct's null, so a reader that looks
        // past the validity still finds well-formed bytes.
        _metadata.Append(EmptyMetadata.AsSpan());
        _value.Append([0x00]);
        _validity.Append(false);
        _length++;
        _nullCount++;
    }

    public IArrowArray Build()
    {
        var type = VariantType.Default;
        IArrowArray metadata = _metadata.Build();
        IArrowArray value = _value.Build();
        var storage = new StructArray(type.StorageType, _length, [metadata, value], _validity.Build(), _nullCount);
        return new VariantArray(type, storage);
    }

    private void Encode(object o)
    {
        switch (o)
        {
            case bool b:
                _bytes.Add(Header(b ? TypeTrue : TypeFalse));
                break;
            case byte v:
                Fixed(TypeInt16, BitConverter.GetBytes((short)v));
                break;
            case short v:
                Fixed(TypeInt16, BitConverter.GetBytes(v));
                break;
            case int v:
                Fixed(TypeInt32, BitConverter.GetBytes(v));
                break;
            case long v:
                Fixed(TypeInt64, BitConverter.GetBytes(v));
                break;
            case float v:
                Fixed(TypeFloat, BitConverter.GetBytes(v));
                break;
            case double v:
                Fixed(TypeDouble, BitConverter.GetBytes(v));
                break;
            case decimal v:
                Decimal(v);
                break;
            case DateTime v:
                Timestamp(v.Ticks - UnixEpochTicks, TypeTimestampNtzMicros, TypeTimestampNtzNanos);
                break;
            case DateTimeOffset v:
                Timestamp(v.UtcTicks - UnixEpochTicks, TypeTimestampMicros, TypeTimestampNanos);
                break;
            case TimeSpan v:
                Fixed(TypeTimeNtzMicros, BitConverter.GetBytes(v.Ticks / 10));
                break;
            case Guid v:
                Fixed(TypeUuid, v.ToByteArray(bigEndian: true));
                break;
            case string v:
                String(Encoding.UTF8.GetBytes(v));
                break;
            case byte[] v:
                LengthPrefixed(TypeBinary, v);
                break;
            default:
                throw new NotSupportedException(
                    $"Fabricator: a sql_variant value of CLR type {o.GetType().Name} has no variant encoding");
        }
    }

    private static byte Header(byte primitiveType) => (byte)(primitiveType << 2);

    private void Fixed(byte type, byte[] littleEndian)
    {
        _bytes.Add(Header(type));
        _bytes.AddRange(littleEndian);
    }

    private void LengthPrefixed(byte type, byte[] payload)
    {
        _bytes.Add(Header(type));
        _bytes.AddRange(BitConverter.GetBytes(payload.Length));
        _bytes.AddRange(payload);
    }

    /// <summary>A short string (one header byte) when under 64 bytes, else a length-prefixed string.</summary>
    private void String(byte[] utf8)
    {
        if (utf8.Length < 64)
        {
            _bytes.Add((byte)((utf8.Length << 2) | BasicShortString));
            _bytes.AddRange(utf8);
        }
        else
        {
            LengthPrefixed(TypeString, utf8);
        }
    }

    /// <summary>
    /// The narrowest decimal width whose precision holds both the value's digits and its scale: 9 digits for
    /// decimal4, 18 for decimal8, 38 for decimal16 (a System.Decimal has at most 29).
    /// </summary>
    private void Decimal(decimal v)
    {
        int[] bits = decimal.GetBits(v);
        byte scale = (byte)((bits[3] >> 16) & 0xFF);
        bool negative = bits[3] < 0;
        UInt128 magnitude = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        Int128 unscaled = negative ? -(Int128)magnitude : (Int128)magnitude;
        int digits = magnitude == 0 ? 1 : magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        int precision = Math.Max(digits, scale);
        (byte type, int size) = precision <= 9 ? (TypeDecimal4, 4) : precision <= 18 ? (TypeDecimal8, 8) : (TypeDecimal16, 16);
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(buffer, unscaled);
        _bytes.Add(Header(type));
        _bytes.Add(scale);
        for (int i = 0; i < size; i++)
        {
            _bytes.Add(buffer[i]);
        }
    }

    /// <summary>
    /// Microseconds unless the value has a sub-microsecond part and fits the nanosecond range, so a
    /// datetime2(7) keeps its seventh digit wherever the format can carry it.
    /// </summary>
    private void Timestamp(long ticksSinceEpoch, byte microsType, byte nanosType)
    {
        if (ticksSinceEpoch % 10 != 0 && ticksSinceEpoch is > long.MinValue / 100 and < long.MaxValue / 100)
        {
            Fixed(nanosType, BitConverter.GetBytes(ticksSinceEpoch * 100));
            return;
        }
        Fixed(microsType, BitConverter.GetBytes(FloorDiv(ticksSinceEpoch, 10)));
    }

    private static long FloorDiv(long a, long b) => a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0);
}
