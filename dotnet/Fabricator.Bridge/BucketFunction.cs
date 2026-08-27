// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Numerics;
using Apache.Arrow;
using Apache.Arrow.Arrays; // FixedSizeBinaryArray
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// GLOBAL scalar (connection-free, no ATTACH): <c>bucket(num_buckets BIGINT, value ANY) -&gt; INTEGER</c> —
/// the Iceberg/DuckLake bucket transform: Murmur3 (x86, 32-bit, seed 0) over the value's Iceberg canonical
/// byte encoding, then <c>(hash &amp; Int32.MaxValue) % num_buckets</c>. Arg order matches DuckLake's
/// <c>bucket(8, user_name)</c>.
///
/// <para><b>Purpose: bucket partitioning for high-cardinality keys.</b> Delta has no transform partitioning
/// (a table partitions only by a real column), so the DuckLake <c>PARTITIONED BY (bucket(8, col))</c>
/// equivalent here is a MATERIALIZED bucket column:
/// <c>CREATE TABLE lake.s.t PARTITIONED BY (user_bucket) AS SELECT *, bucket(8, user_name) AS user_bucket …</c>
/// Queries then prune with <c>WHERE user_name = 'alice' AND user_bucket = bucket(8, 'alice')</c> — the
/// function is registered CONSISTENT (<see cref="IsVolatile"/> = false), so the constant side folds at plan
/// time and reaches the scan as an ordinary partition filter.</para>
///
/// <para><b>Cross-engine agreement (Iceberg spec, Appendix B):</b> integers/dates hash as the little-endian
/// 8-byte long (ints promoted, dates as days-from-epoch), timestamps/times as microseconds, decimals as the
/// minimal big-endian two's-complement of the UNSCALED value (so the declared scale matters), strings as
/// UTF-8 bytes, blobs raw — identical values bucket identically in DuckLake / Iceberg / Spark. NULL value
/// =&gt; NULL. float/double/boolean are rejected (not bucketable in Iceberg either — cast first). Unsigned
/// ints hash as the same little-endian bytes (values above <c>long.MaxValue</c> have no Iceberg analog).</para>
/// </summary>
public sealed class BucketFunction : IScalarFunction
{
    public string Name => "bucket";

    public bool IsVolatile => false; // pure — constant-foldable (partition pruning depends on it)

    public Schema Parameters => new(new[]
    {
        new Field("num_buckets", Int64Type.Default, nullable: true),
        // NullType = the "accept any value" sentinel: registered as ANY, the exec chunk carries the
        // argument's RUNTIME type (see BuildFabricatorScalarFunction) — Invoke dispatches on the array type.
        new Field("value", NullType.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("bucket", Int32Type.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var nCol = (Int64Array)args.Column(0);
        var values = args.Column(1);
        var b = new Int32Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (nCol.IsNull(i))
            {
                throw new ArgumentException("bucket: num_buckets must not be NULL.");
            }
            long n = nCol.GetValue(i)!.Value;
            if (n < 1 || n > int.MaxValue)
            {
                throw new ArgumentException($"bucket: num_buckets must be in [1, {int.MaxValue}] (got {n}).");
            }
            if (values.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            b.Append((int)((HashValue(values, i) & int.MaxValue) % n));
        }
        return b.Build();
    }

    // The Iceberg 32-bit hash of row i, dispatched on the value column's runtime Arrow type.
    private static int HashValue(IArrowArray values, int i) => values switch
    {
        Int8Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        Int16Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        Int32Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        Int64Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        // Unsigned: same little-endian byte encoding (bit-cast; value-preserving up to long.MaxValue).
        UInt8Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        UInt16Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        UInt32Array a => Murmur3.HashLong(a.GetValue(i)!.Value),
        UInt64Array a => Murmur3.HashLong(unchecked((long)a.GetValue(i)!.Value)),
        StringArray a => Murmur3.Hash(a.GetBytes(i)),
        LargeStringArray a => Murmur3.Hash(a.GetBytes(i)),
        BinaryArray a => Murmur3.Hash(a.GetBytes(i)),
        LargeBinaryArray a => Murmur3.Hash(a.GetBytes(i)),
        // NOTE: DecimalNNNArray derives from FixedSizeBinaryArray — the decimal cases MUST precede it
        // (Iceberg hashes the minimal big-endian unscaled value, not the raw fixed-width buffer).
        Decimal128Array a => HashUnscaled(a.ValueBuffer.Span.Slice(checked((a.Offset + i) * 16), 16)),
        Decimal256Array a => HashUnscaled(a.ValueBuffer.Span.Slice(checked((a.Offset + i) * 32), 32)),
        FixedSizeBinaryArray a => Murmur3.Hash(a.GetBytes(i)),
        Date32Array a => Murmur3.HashLong(a.GetValue(i)!.Value), // days from epoch
        Date64Array a => Murmur3.HashLong(a.GetValue(i)!.Value / 86_400_000L),
        TimestampArray a => Murmur3.HashLong(ToMicros(a.GetValue(i)!.Value, ((TimestampType)a.Data.DataType).Unit)),
        Time64Array a => Murmur3.HashLong(ToMicros(a.GetValue(i)!.Value, ((Time64Type)a.Data.DataType).Unit)),
        Time32Array a => Murmur3.HashLong(ToMicros(a.GetValue(i)!.Value, ((Time32Type)a.Data.DataType).Unit)),
        _ => throw new ArgumentException(
            $"bucket: unsupported value type '{values.Data.DataType.Name}' — supported: integers, string, " +
            "decimal, date, time, timestamp, blob (float/double/boolean are not bucketable; cast first)."),
    };

    // Iceberg decimal hash: minimal big-endian two's-complement bytes of the unscaled value. The Arrow
    // buffer holds the unscaled value little-endian two's-complement; BigInteger round-trips it minimally
    // (matching Java BigInteger.toByteArray()).
    private static int HashUnscaled(ReadOnlySpan<byte> littleEndian)
    {
        var unscaled = new BigInteger(littleEndian, isUnsigned: false, isBigEndian: false);
        return Murmur3.Hash(unscaled.ToByteArray(isUnsigned: false, isBigEndian: true));
    }

    private static long ToMicros(long value, TimeUnit unit) => unit switch
    {
        TimeUnit.Second => value * 1_000_000L,
        TimeUnit.Millisecond => value * 1_000L,
        TimeUnit.Microsecond => value,
        TimeUnit.Nanosecond => value / 1_000L,
        _ => throw new ArgumentException($"bucket: unsupported time unit {unit}."),
    };
}

/// <summary>
/// Murmur3 x86 32-bit, seed 0 — the hash the Iceberg spec (Appendix B) mandates for the bucket transform
/// (and which DuckLake uses "for full compatibility with Iceberg"). <see cref="HashLong"/> hashes the
/// little-endian 8-byte encoding (Iceberg hashes int/long/date/time/timestamp through this one shape).
/// </summary>
internal static class Murmur3
{
    public static int HashLong(long v)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, v);
        return Hash(bytes);
    }

    public static int Hash(ReadOnlySpan<byte> data)
    {
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        uint h = 0; // seed 0
        int len = data.Length;
        int i = 0;
        for (; i + 4 <= len; i += 4)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
            k *= c1;
            k = uint.RotateLeft(k, 15);
            k *= c2;
            h ^= k;
            h = uint.RotateLeft(h, 13);
            h = h * 5 + 0xe6546b64;
        }
        uint tail = 0;
        switch (len & 3)
        {
            case 3: tail ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: tail ^= (uint)data[i + 1] << 8; goto case 1;
            case 1:
                tail ^= data[i];
                tail *= c1;
                tail = uint.RotateLeft(tail, 15);
                tail *= c2;
                h ^= tail;
                break;
        }
        h ^= (uint)len;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return (int)h;
    }
}
