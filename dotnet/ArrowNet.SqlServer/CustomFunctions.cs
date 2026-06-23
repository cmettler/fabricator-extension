using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using ArrowNet.Bridge;

namespace ArrowNet.SqlServer;

/// <summary>
/// Provider-authored custom functions — scalar, table, table-in-out, and aggregate — surfaced into every
/// attached catalog alongside the discovered SQL Server functions (resolved as <c>db.schema.name(args)</c>).
/// To add one, implement the matching Bridge interface (<see cref="IArrowScalarFunction"/>,
/// <see cref="IArrowTableFunction"/>, <see cref="IArrowInOutFunction"/> — or its fixed-schema convenience base
/// <see cref="StaticInOutFunction"/> — or <see cref="IArrowAggregateFunction"/>) and list it in the
/// corresponding array below. These run entirely in C# — there need be no corresponding SQL Server object.
/// </summary>
internal static class CustomFunctions
{
    public static readonly IReadOnlyList<IArrowScalarFunction> Scalar = new IArrowScalarFunction[]
    {
        new CfAddFunction(),
    };

    public static readonly IReadOnlyList<IArrowTableFunction> Table = new IArrowTableFunction[]
    {
        new CfRangeFunction(),
        new CfColumnsFunction(),
    };

    // Custom table-in-out functions (IArrowInOutFunction), singletons — surfaced as `kind='inout'` and
    // resolved by SqlServerCatalog.InOutBind on the streaming exchange; Bind() mints the per-call binding. All
    // three use the fixed-schema convenience base StaticInOutFunction (override OutputSchema + DoExchange; the
    // author owns the loop + sentinel, cross-chunk state in DoExchange locals — stateless cf_tag, cumulative
    // cf_running_sum, row-index cf_exchange).
    public static readonly IReadOnlyList<IArrowInOutFunction> InOut = new IArrowInOutFunction[]
    {
        new CfTagFunction(),
        new CfRunningSumFunction(),
        new CfExchangeFunction(),
    };

    // Aggregate functions (UDAF). The function object is a singleton; CreateState() mints the per-group
    // accumulator. These reduce in C# (no SQL Server equivalent) and work in GROUP BY / parallel / OVER(...).
    public static readonly IReadOnlyList<IArrowAggregateFunction> Aggregate = new IArrowAggregateFunction[]
    {
        new CfProductFunction(),
        new CfBitOrFunction(),
        new CfSumSpillFunction(),
        new CfMedianFunction(),
    };
}

// Demo (aggregate, NON-ADDITIVE / holistic): dbo.cf_median(x DOUBLE) -> DOUBLE. A holistic aggregate can't be
// reduced to a small running value — the state IS the full collection of values: Update collects, Combine
// MERGES the two collections (the answer to "what about combine?": it's concatenation, not arithmetic), and
// Finalize sorts + picks the median. Order-independent, so it's correct under parallel partial-state merging.
// SupportsSpill stays false (the default): an unbounded collection can't fit the fixed spill blob, so holistic
// aggregates run in the fast in-memory mode (bounded by the group's cardinality), like DuckDB's own median.
internal sealed class CfMedianFunction : IArrowAggregateFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_median";
    public Schema Parameters => new(new[] { new Field("x", DoubleType.Default, nullable: true) }, metadata: null);
    public Field Result => new("median", DoubleType.Default, nullable: true);
    public IArrowAggregateState CreateState() => new State();

    private sealed class State : IArrowAggregateState
    {
        private readonly List<double> _values = new();

        public void Update(RecordBatch args)
        {
            var x = (DoubleArray)args.Column(0);
            for (int i = 0; i < args.Length; i++)
            {
                if (!x.IsNull(i))
                {
                    _values.Add(x.Values[i]); // copy the value out — the batch's buffers are freed after this returns
                }
            }
        }

        // Combine for a holistic aggregate = merge the collections (NOT an arithmetic fold).
        public void Combine(IArrowAggregateState source) => _values.AddRange(((State)source)._values);

        public object? Finalize()
        {
            if (_values.Count == 0)
            {
                return null;
            }
            _values.Sort();
            int n = _values.Count;
            // Matches DuckDB's median (quantile_cont(0.5)): average the two middles for an even count.
            return n % 2 == 1 ? _values[n / 2] : (_values[n / 2 - 1] + _values[n / 2]) / 2.0;
        }
    }
}

// Demo (aggregate): dbo.cf_product(x BIGINT) -> BIGINT, the product of all non-NULL inputs (SQL Server has
// no PRODUCT aggregate). Empty group / all-NULL => NULL (SUM-like). Order-independent => safe under parallel
// combine and windowing.
internal sealed class CfProductFunction : IArrowAggregateFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_product";
    public Schema Parameters => new(new[] { new Field("x", Int64Type.Default, nullable: true) }, metadata: null);
    public Field Result => new("product", Int64Type.Default, nullable: true);
    public IArrowAggregateState CreateState() => new State();

    private sealed class State : IArrowAggregateState
    {
        private bool _any;
        private long _product = 1;

        public void Update(RecordBatch args)
        {
            var x = (Int64Array)args.Column(0);
            for (int i = 0; i < args.Length; i++)
            {
                if (x.IsNull(i))
                {
                    continue; // NULLs are skipped (standard aggregate semantics)
                }
                _product *= x.Values[i];
                _any = true;
            }
        }

        public void Combine(IArrowAggregateState source)
        {
            var s = (State)source;
            if (s._any)
            {
                _product *= s._product;
                _any = true;
            }
        }

        public object? Finalize() => _any ? _product : null;
    }
}

// Demo (aggregate): dbo.cf_bit_or(x BIGINT) -> BIGINT, the bitwise OR of all non-NULL inputs. Associative +
// commutative => a clean parallel/combine test. Empty group / all-NULL => NULL.
internal sealed class CfBitOrFunction : IArrowAggregateFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_bit_or";
    public Schema Parameters => new(new[] { new Field("x", Int64Type.Default, nullable: true) }, metadata: null);
    public Field Result => new("bit_or", Int64Type.Default, nullable: true);
    public IArrowAggregateState CreateState() => new State();

    private sealed class State : IArrowAggregateState
    {
        private bool _any;
        private long _acc;

        public void Update(RecordBatch args)
        {
            var x = (Int64Array)args.Column(0);
            for (int i = 0; i < args.Length; i++)
            {
                if (x.IsNull(i))
                {
                    continue;
                }
                _acc |= x.Values[i];
                _any = true;
            }
        }

        public void Combine(IArrowAggregateState source)
        {
            var s = (State)source;
            if (s._any)
            {
                _acc |= s._acc;
                _any = true;
            }
        }

        public object? Finalize() => _any ? _acc : null;
    }
}

// Demo (aggregate, SPILLABLE): dbo.cf_sum_spill(x BIGINT) -> BIGINT, sum of non-NULL inputs. Identical in
// behaviour to SUM, but opts into spillable mode (SupportsSpill + Serialize/Load): its state is serialized
// into DuckDB's fixed state blob so a huge-cardinality GROUP BY can spill to disk. State = {any:bool, sum:long}
// => 9 bytes, well under the 1 KB cap. Empty group / all-NULL => NULL.
internal sealed class CfSumSpillFunction : IArrowAggregateFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_sum_spill";
    public Schema Parameters => new(new[] { new Field("x", Int64Type.Default, nullable: true) }, metadata: null);
    public Field Result => new("sum", Int64Type.Default, nullable: true);
    public bool SupportsSpill => true;
    public IArrowAggregateState CreateState() => new State();

    private sealed class State : IArrowAggregateState
    {
        private bool _any;
        private long _sum;

        public void Update(RecordBatch args)
        {
            var x = (Int64Array)args.Column(0);
            for (int i = 0; i < args.Length; i++)
            {
                if (x.IsNull(i))
                {
                    continue;
                }
                _sum += x.Values[i];
                _any = true;
            }
        }

        public void Combine(IArrowAggregateState source)
        {
            var s = (State)source;
            if (s._any)
            {
                _sum += s._sum;
                _any = true;
            }
        }

        public object? Finalize() => _any ? _sum : null;

        // Spillable: 1 byte flag + 8 byte little-endian sum.
        public byte[] Serialize()
        {
            var buf = new byte[9];
            buf[0] = _any ? (byte)1 : (byte)0;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(1), _sum);
            return buf;
        }

        public void Load(ReadOnlySpan<byte> state)
        {
            _any = state[0] != 0;
            _sum = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(state.Slice(1));
        }
    }
}

// Demo (table-in-out): dbo.cf_tag(<table of n>) -> (n, sq=n*n) per input row. STATELESS. Uses the
// StaticInOutFunction base (fixed schema + Bind wiring); the author writes DoExchange — one output batch per
// input chunk + a sentinel. Pure C#, no SQL object.
internal sealed class CfTagFunction : StaticInOutFunction
{
    public override string SchemaName => "dbo";
    public override string Name => "cf_tag";

    public override Schema InputSchema =>
        new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public override Schema OutputSchema => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
        new Field("sq", Int32Type.Default, nullable: true),
    }, metadata: null);

    public override async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in input.WithCancellation(ct))
        {
            using (chunk)
            {
                var n = (Int32Array)chunk.Column(0);
                int rows = chunk.Length;
                var nb = new Int32Array.Builder().Reserve(rows);
                var sq = new Int32Array.Builder().Reserve(rows);
                for (int i = 0; i < rows; i++)
                {
                    if (n.IsNull(i))
                    {
                        nb.AppendNull();
                        sq.AppendNull();
                    }
                    else
                    {
                        nb.Append(n.Values[i]);
                        sq.Append(n.Values[i] * n.Values[i]);
                    }
                }
                yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), sq.Build() }, rows);
            }
            yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel (NEED_MORE_INPUT)
        }
    }
}

// Demo (table-in-out, STATEFUL): dbo.cf_running_sum(<table of n>) -> (n, running) — cumulative sum across the
// input stream, held in a DoExchange LOCAL (`running`, fresh per exchange, so re-executions never share state).
// The cumulative VALUE is order-dependent, but max(running) == total is order-independent (the last row holds
// the full sum), which is what the test asserts. Pure C#, no SQL object.
internal sealed class CfRunningSumFunction : StaticInOutFunction
{
    public override string SchemaName => "dbo";
    public override string Name => "cf_running_sum";

    public override Schema InputSchema =>
        new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public override Schema OutputSchema => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
        new Field("running", Int64Type.Default, nullable: false),
    }, metadata: null);

    public override async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        long running = 0; // cumulative sum across the whole input stream, in a local (fresh per exchange)
        await foreach (var chunk in input.WithCancellation(ct))
        {
            using (chunk)
            {
                var n = (Int32Array)chunk.Column(0);
                int rows = chunk.Length;
                var nb = new Int32Array.Builder().Reserve(rows);
                var rb = new Int64Array.Builder().Reserve(rows);
                for (int i = 0; i < rows; i++)
                {
                    if (n.IsNull(i))
                    {
                        nb.AppendNull();
                    }
                    else
                    {
                        running += n.Values[i];
                        nb.Append(n.Values[i]);
                    }
                    rb.Append(running);
                }
                yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), rb.Build() }, rows);
            }
            yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel (NEED_MORE_INPUT)
        }
    }
}

// Demo (table-in-out, STATEFUL): dbo.cf_exchange(<table of n>) -> (n, rownum) — rownum is a 1-based index
// across the WHOLE input stream, held in a DoExchange LOCAL. Same StaticInOutFunction shape as
// cf_tag/cf_running_sum (the author owns the streaming loop + sentinel). Pure C#, no SQL object.
internal sealed class CfExchangeFunction : StaticInOutFunction
{
    public override string SchemaName => "dbo";
    public override string Name => "cf_exchange";

    public override Schema InputSchema =>
        new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public override Schema OutputSchema => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
        new Field("rownum", Int64Type.Default, nullable: false),
    }, metadata: null);

    public override async IAsyncEnumerable<RecordBatch> DoExchange(
        IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        long rownum = 0; // 1-based index across the whole input stream, in a local (fresh per exchange)
        await foreach (var chunk in input.WithCancellation(ct))
        {
            using (chunk)
            {
                var n = (Int32Array)chunk.Column(0);
                int rows = chunk.Length;
                var nb = new Int32Array.Builder().Reserve(rows);
                var rb = new Int64Array.Builder().Reserve(rows);
                for (int i = 0; i < rows; i++)
                {
                    rownum++;
                    if (n.IsNull(i))
                    {
                        nb.AppendNull();
                    }
                    else
                    {
                        nb.Append(n.Values[i]);
                    }
                    rb.Append(rownum);
                }
                yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), rb.Build() }, rows);
            }
            yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel (NEED_MORE_INPUT)
        }
    }
}

// Demo: dbo.cf_range(n) -> rows (value, squared) for value = 1..n, generated in C# (no such object exists in
// SQL Server). FIXED output schema → derives from StaticTableFunction (just OutputSchema + Invoke).
internal sealed class CfRangeFunction : StaticTableFunction
{
    public override string SchemaName => "dbo";
    public override string Name => "cf_range";

    public override Schema Parameters => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
    }, metadata: null);

    public override Schema OutputSchema => new(new[]
    {
        new Field("value", Int32Type.Default, nullable: false),
        new Field("squared", Int32Type.Default, nullable: false),
    }, metadata: null);

    public override IEnumerable<RecordBatch> Invoke(RecordBatch args)
    {
        var arg = (Int32Array)args.Column(0);
        int n = args.Length > 0 && !arg.IsNull(0) ? arg.Values[0] : 0;
        var value = new Int32Array.Builder().Reserve(n);
        var squared = new Int32Array.Builder().Reserve(n);
        for (int i = 1; i <= n; i++)
        {
            value.Append(i);
            squared.Append(i * i);
        }
        yield return new RecordBatch(OutputSchema, new IArrowArray[] { value.Build(), squared.Build() }, n);
    }
}

// Demo: dbo.cf_columns(n) -> a single row with n INT columns c1..cn (c_i = i). The OUTPUT SCHEMA itself
// depends on the constant argument n — only expressible because IArrowTableFunction.Bind sees the args. The
// binding resolves the schema once at bind and reuses it for the row.
internal sealed class CfColumnsFunction : IArrowTableFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_columns";
    public Schema Parameters => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        var a = (Int32Array)args.Column(0);
        int n = args.Length > 0 && !a.IsNull(0) ? a.Values[0] : 0;
        return new Binding(n);
    }

    private sealed class Binding : IArrowTableFunctionBinding
    {
        private readonly int _n;
        public Binding(int n)
        {
            _n = n;
            var fields = new Field[_n];
            for (int i = 1; i <= _n; i++)
            {
                fields[i - 1] = new Field($"c{i}", Int32Type.Default, nullable: false);
            }
            OutputSchema = new Schema(fields, metadata: null);
        }

        public Schema OutputSchema { get; }
        public bool SupportsPushdown => false;

        public IEnumerable<RecordBatch> Execute(TableFunctionScan scan)
        {
            scan.FilterValues?.Dispose();
            var arrays = new IArrowArray[_n];
            for (int i = 1; i <= _n; i++)
            {
                var b = new Int32Array.Builder();
                b.Append(i);
                arrays[i - 1] = b.Build();
            }
            return new[] { new RecordBatch(OutputSchema, arrays, 1) };
        }

        public void Dispose() { }
    }
}

// Demo: dbo.cf_add(a, b) -> a + b, computed in C# (no such object exists in SQL Server).
internal sealed class CfAddFunction : IArrowScalarFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_add";

    public Schema Parameters => new(new[]
    {
        new Field("a", Int32Type.Default, nullable: true),
        new Field("b", Int32Type.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("result", Int32Type.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var a = (Int32Array)args.Column(0);
        var b = (Int32Array)args.Column(1);
        var builder = new Int32Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (a.IsNull(i) || b.IsNull(i))
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(a.Values[i] + b.Values[i]);
            }
        }
        return builder.Build();
    }
}
