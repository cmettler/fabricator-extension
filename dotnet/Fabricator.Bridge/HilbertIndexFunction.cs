using System;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// GLOBAL scalar (connection-free, no ATTACH): <c>hilbert_index(coords BIGINT[], bits INTEGER) -&gt; BIGINT</c> —
/// the position of an n-dimensional point on the Hilbert space-filling curve with <c>bits</c> bits per
/// dimension (n = the list length, per row; n * bits &lt;= 63 so the index fits a non-negative BIGINT).
///
/// <para><b>Purpose: liquid-clustering-style writes.</b> Ordering a write by the curve position gives every
/// file/row-group TIGHT min/max stats on ALL clustering keys at once (a lexicographic ORDER BY only bounds
/// the leading key), so stats-based file skipping works on any predicate subset — partitioning's pruning
/// benefit without its rigid split. Used inside a plain <c>ORDER BY</c>, DuckDB's EXTERNAL (spilling) sort
/// does the global reorder — the write pipeline stays streaming:
/// <c>CREATE TABLE lake.s.t AS SELECT … ORDER BY hilbert_index([wb_a, wb_b], 16)</c>.
/// Coordinates should be pre-bucketed to [0, 2^bits) — e.g. DuckDB's
/// <c>width_bucket(x, min, max, 2^bits - 2)</c>, or any integer id; values outside the range are CLAMPED
/// (layout quality degrades, correctness never — the curve position is advisory ordering only).</para>
///
/// <para>Algorithm: Skilling's transpose method ("Programming the Hilbert Curve", AIP 2004) — axes →
/// transposed Gray-code form, then MSB-first bit interleave. n = 1 degenerates to the identity (a plain
/// range sort); NULL bits / list / element =&gt; NULL. The 2D order at bits=1 is the classic U:
/// (0,0)=0, (0,1)=1, (1,1)=2, (1,0)=3.</para>
/// </summary>
public sealed class HilbertIndexFunction : IScalarFunction
{
    public string Name => "hilbert_index";

    public Schema Parameters => new(new[]
    {
        new Field("coords", new ListType(new Field("item", Int64Type.Default, nullable: true)), nullable: true),
        new Field("bits", Int32Type.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("index", Int64Type.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var coords = (ListArray)args.Column(0);
        var values = (Int64Array)coords.Values;
        var bitsCol = (Int32Array)args.Column(1);
        var b = new Int64Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (coords.IsNull(i) || bitsCol.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            int offset = coords.ValueOffsets[i];
            int n = coords.ValueOffsets[i + 1] - offset;
            int bits = bitsCol.GetValue(i)!.Value;
            if (n == 0)
            {
                throw new ArgumentException("hilbert_index: the coordinate list must not be empty.");
            }
            if (bits < 1 || (long)n * bits > 63)
            {
                throw new ArgumentException(
                    $"hilbert_index: need bits >= 1 and dimensions * bits <= 63 (got {n} x {bits}).");
            }
            bool hasNull = false;
            var x = new ulong[n];
            ulong max = (1UL << bits) - 1;
            for (int k = 0; k < n; k++)
            {
                if (values.IsNull(offset + k))
                {
                    hasNull = true;
                    break;
                }
                long v = values.GetValue(offset + k)!.Value;
                x[k] = v <= 0 ? 0UL : (ulong)v > max ? max : (ulong)v; // clamp into the bit space
            }
            if (hasNull)
            {
                b.AppendNull();
                continue;
            }
            b.Append(n == 1 ? (long)x[0] : (long)Index(x, n, bits));
        }
        return b.Build();
    }

    // Skilling's AxesToTranspose + Gray encode, then MSB-first interleave of the transposed axes.
    private static ulong Index(ulong[] x, int n, int bits)
    {
        // Inverse undo excess work
        for (ulong q = 1UL << (bits - 1); q > 1; q >>= 1)
        {
            ulong p = q - 1;
            for (int i = 0; i < n; i++)
            {
                if ((x[i] & q) != 0)
                {
                    x[0] ^= p;
                }
                else
                {
                    ulong t = (x[0] ^ x[i]) & p;
                    x[0] ^= t;
                    x[i] ^= t;
                }
            }
        }
        // Gray encode
        for (int i = 1; i < n; i++)
        {
            x[i] ^= x[i - 1];
        }
        ulong t2 = 0;
        for (ulong q = 1UL << (bits - 1); q > 1; q >>= 1)
        {
            if ((x[n - 1] & q) != 0)
            {
                t2 ^= q - 1;
            }
        }
        for (int i = 0; i < n; i++)
        {
            x[i] ^= t2;
        }
        // Transpose -> index: bit (bitpos) of dimension i lands at position ((bitpos * n) + (n-1-i)) from
        // the LSB end — i.e. MSB-first interleave across dimensions.
        ulong index = 0;
        for (int bitpos = bits - 1; bitpos >= 0; bitpos--)
        {
            for (int i = 0; i < n; i++)
            {
                index = (index << 1) | ((x[i] >> bitpos) & 1UL);
            }
        }
        return index;
    }
}
