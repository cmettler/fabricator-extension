using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fabricator.Bridge.Conversion;
using Fluid;

namespace Fabricator.SqlServer;

/// <summary>
/// Provider-authored custom functions — scalar, table, table-in-out, and aggregate — surfaced into every
/// attached catalog alongside the discovered SQL Server functions (resolved as <c>db.schema.name(args)</c>).
/// To add one, implement the matching Bridge interface (<see cref="ICatalogScalarFunction"/>,
/// <see cref="ICatalogTableFunction"/>, <see cref="ICatalogInOutFunction"/> — or its fixed-schema convenience base
/// <see cref="StaticInOutFunction"/> — or <see cref="ICatalogAggregateFunction"/>) and list it in the
/// corresponding array below. These run entirely in C# — there need be no corresponding SQL Server object.
/// </summary>
internal static class CustomFunctions
{
    // Connection-free GLOBAL scalar functions — registered at extension load as bare fn(...), no ATTACH
    // (see docs/global-functions.md). Provider-agnostic utilities; surfaced via SqlServerBackend (the always
    // -present default provider). Implement the base IScalarFunction (no SchemaName).
    public static readonly IReadOnlyList<IScalarFunction> GlobalScalar = new IScalarFunction[]
    {
        new CfRenderFunction(),
    };

    // Connection-free GLOBAL table-in-out (streaming exchange) functions — bare fn(<input>), no ATTACH.
    // Implement the base IInOutFunction (no SchemaName).
    public static readonly IReadOnlyList<IInOutFunction> GlobalInOut = new IInOutFunction[]
    {
        new GfTagFunction(),
    };

    // Connection-free GLOBAL collector (pipeline-breaker) functions — bare fn(<input>), no ATTACH.
    // Implement the base ICollectorTableFunction (no SchemaName).
    public static readonly IReadOnlyList<ICollectorTableFunction> GlobalCollector = new ICollectorTableFunction[]
    {
        new GfCollectSumFunction(),
        // HOST-FS WRITE: fabricator_delta_write(<input>, path := '…') writes any input table to a Delta table
        // (Overwrite) on OneLake/ADLS/local, returning (version, rows_written). Buffers + writes one commit.
        new Fabricator.Bridge.DeltaWriteCollectorFunction(),
    };

    // Connection-free GLOBAL table functions — bare fn(args), no ATTACH. Implement the base ITableFunction
    // (no SchemaName); output schema resolved per-call from the args via the v29 table session.
    public static readonly IReadOnlyList<ITableFunction> GlobalTable = new ITableFunction[]
    {
        new GfSeqFunction(),
        new GfColumnsFunction(),
        // Reference HOST-FS reader: fabricator_delta_scan(path) reads a Delta Lake table via engineered-wood,
        // IO through DuckDB's FileSystem + secrets. A provider-agnostic core reader (lives in the Bridge);
        // declared here because SqlServer is the always-present default backend (same as fabricator_render).
        new Fabricator.Bridge.DeltaGlobalTableFunction(),
        // NATIVE-READ pre-spike: fabricator_delta_native_scan(path) — engineered-wood lists the exact active files,
        // DuckDB's native parquet reader reads them via read_parquet (cached, over onelake:// for OneLake).
        // Plain tables only (no DV/partition/pushdown yet). See docs/multifile-delta.md Phase A.
        new Fabricator.Bridge.DeltaNativeScanFunction(),
        // HOST-FS WRITE spike: fabricator_delta_write_demo(path) writes a fixed 5-row Delta table via the host-FS
        // write callbacks (put-if-absent commit), returning (version, rows_written). Proves the write bridge.
        new Fabricator.Bridge.DeltaWriteDemoFunction(),
    };

    // Connection-free GLOBAL aggregate functions (UDAF) — bare fn(args), no ATTACH, usable in GROUP BY / OVER /
    // parallel. Implement the base IAggregateFunction (no SchemaName).
    public static readonly IReadOnlyList<IAggregateFunction> GlobalAggregate = new IAggregateFunction[]
    {
        new GfProductFunction(),
    };

    public static readonly IReadOnlyList<ICatalogScalarFunction> Scalar = new ICatalogScalarFunction[]
    {
        new CfAddFunction(),
        new CfHostAnswerFunction(),
        new CfHostSumFunction(),
        new CfHostParamFunction(),
    };

    public static readonly IReadOnlyList<ICatalogTableFunction> Table = new ICatalogTableFunction[]
    {
        new CfRangeFunction(),
        new CfColumnsFunction(),
    };

    // Custom table-in-out functions (ICatalogInOutFunction), singletons — surfaced as `kind='inout'` and
    // resolved by SqlServerCatalog.InOutBind on the streaming exchange; Bind() mints the per-call binding. All
    // three use the fixed-schema convenience base StaticInOutFunction (override OutputSchema + DoExchange; the
    // author owns the loop + sentinel, cross-chunk state in DoExchange locals — stateless cf_tag, cumulative
    // cf_running_sum, row-index cf_exchange).
    public static readonly IReadOnlyList<ICatalogInOutFunction> InOut = new ICatalogInOutFunction[]
    {
        new CfTagFunction(),
        new CfRunningSumFunction(),
        new CfExchangeFunction(),
    };

    // Aggregate functions (UDAF). The function object is a singleton; CreateState() mints the per-group
    // accumulator. These reduce in C# (no SQL Server equivalent) and work in GROUP BY / parallel / OVER(...).
    public static readonly IReadOnlyList<ICatalogAggregateFunction> Aggregate = new ICatalogAggregateFunction[]
    {
        new CfProductFunction(),
        new CfBitOrFunction(),
        new CfSumSpillFunction(),
        new CfMedianFunction(),
    };

    // Collector table-in-out functions (ICatalogCollectorTableFunction), singletons — surfaced as
    // `kind='collector'` and resolved by SqlServerCatalog.InOutBind on the Sink+Source pipeline-breaker
    // operator (NOT the streaming exchange). A collector sees ALL input before emitting any output (whole-table
    // semantics), so it can take arbitrarily many input chunks — unlike the streaming in-out. Pure C#.
    public static readonly IReadOnlyList<ICatalogCollectorTableFunction> Collector = new ICatalogCollectorTableFunction[]
    {
        new CfCollectFunction(),
    };
}

// Demo (COLLECTOR table-in-out): dbo.cf_collect(<table of n>) -> (n, total) — emits every input row paired
// with the GLOBAL sum across ALL input rows. The total is only knowable after the whole input is seen, so this
// proves the pipeline-breaker contract: Collect reads all input to EOF, THEN emits. It also emits one output
// row per input row across as many chunks as needed (no single-chunk cap — the operator buffers all input).
// FIXED output schema → derives from StaticCollectorFunction (just OutputSchema + Collect). Pure C#, no SQL.
internal sealed class CfCollectFunction : StaticCollectorFunction
{
    public override string SchemaName => "dbo";
    public override string Name => "cf_collect";

    public override Schema InputSchema =>
        new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public override Schema OutputSchema => new(new[]
    {
        new Field("n", Int32Type.Default, nullable: true),
        new Field("total", Int64Type.Default, nullable: false),
    }, metadata: null);

    public override async IAsyncEnumerable<RecordBatch> Collect(
        IAsyncEnumerable<RecordBatch> allInput, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Phase 1 — collect EVERY input row (copy values out; the batch buffers are freed after we consume it).
        var values = new List<int?>();
        long total = 0;
        await foreach (var chunk in allInput.WithCancellation(ct))
        {
            using (chunk)
            {
                var n = (Int32Array)chunk.Column(0);
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (n.IsNull(i))
                    {
                        values.Add(null);
                    }
                    else
                    {
                        values.Add(n.Values[i]);
                        total += n.Values[i];
                    }
                }
            }
        }

        // Phase 2 — only now (all input seen) emit each input n with the global total, in output-sized batches.
        const int batchRows = 2048;
        for (int off = 0; off < values.Count; off += batchRows)
        {
            int rows = Math.Min(batchRows, values.Count - off);
            var nb = new Int32Array.Builder().Reserve(rows);
            var tb = new Int64Array.Builder().Reserve(rows);
            for (int i = 0; i < rows; i++)
            {
                if (values[off + i].HasValue)
                {
                    nb.Append(values[off + i]!.Value);
                }
                else
                {
                    nb.AppendNull();
                }
                tb.Append(total);
            }
            yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), tb.Build() }, rows);
        }
    }
}

// Demo (aggregate, NON-ADDITIVE / holistic): dbo.cf_median(x DOUBLE) -> DOUBLE. A holistic aggregate can't be
// reduced to a small running value — the state IS the full collection of values: Update collects, Combine
// MERGES the two collections (the answer to "what about combine?": it's concatenation, not arithmetic), and
// Finalize sorts + picks the median. Order-independent, so it's correct under parallel partial-state merging.
// SupportsSpill stays false (the default): an unbounded collection can't fit the fixed spill blob, so holistic
// aggregates run in the fast in-memory mode (bounded by the group's cardinality), like DuckDB's own median.
internal sealed class CfMedianFunction : ICatalogAggregateFunction
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
internal sealed class CfProductFunction : ICatalogAggregateFunction
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
internal sealed class CfBitOrFunction : ICatalogAggregateFunction
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
internal sealed class CfSumSpillFunction : ICatalogAggregateFunction
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

    public override async IAsyncEnumerable<RecordBatch> Invoke(
        RecordBatch args, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // synchronous generation; satisfies the async-iterator signature
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
// depends on the constant argument n — only expressible because ICatalogTableFunction.Bind sees the args. The
// binding resolves the schema once at bind and reuses it for the row.
internal sealed class CfColumnsFunction : ICatalogTableFunction
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

        public async IAsyncEnumerable<RecordBatch> Execute(
            TableFunctionScan scan, [EnumeratorCancellation] CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            await Task.CompletedTask; // synchronous generation; satisfies the async-iterator signature
            var arrays = new IArrowArray[_n];
            for (int i = 1; i <= _n; i++)
            {
                var b = new Int32Array.Builder();
                b.Append(i);
                arrays[i - 1] = b.Build();
            }
            yield return new RecordBatch(OutputSchema, arrays, 1);
        }

        public void Dispose() { }
    }
}

// Demo: dbo.cf_host_answer(x) -> runs a query on the HOST DuckDB engine (a fresh connection) via
// Host.Query and returns its scalar result for every input row. Proves the C#->host_query round-trip:
// DuckDB -> this C# scalar -> host_query -> a fresh host connection -> Arrow -> back. The nested run is on a
// FRESH connection, so the outer query's context is untouched (reentrancy-safe). See docs/host-query.md.
internal sealed class CfHostAnswerFunction : ICatalogScalarFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_host_answer";
    public Schema Parameters => new(new[] { new Field("x", Int64Type.Default, nullable: true) }, metadata: null);
    public Field Result => new("answer", Int64Type.Default, nullable: false);

    public IArrowArray Invoke(RecordBatch args)
    {
        long answer;
        using (var stream = Host.Query("SELECT 40::BIGINT + 2 AS answer"))
        {
            var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()
                        ?? throw new InvalidOperationException("host_query returned no result");
            answer = ((Int64Array)batch.Column(0)).GetValue(0)!.Value;
        }
        var builder = new Int64Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            builder.Append(answer);
        }
        return builder.Build();
    }
}

// Demo: dbo.cf_host_sum(x) pushes a C#-built Arrow table INTO a host query (data-in) and sums it on the
// host DuckDB engine: Host.Query registers the input as a connection-scoped view `in0` (via duckdb_arrow_scan)
// and runs `SELECT sum(v) FROM in0`. Proves C#-provided Arrow streaming into the host. See docs/host-query.md.
internal sealed class CfHostSumFunction : ICatalogScalarFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_host_sum";
    public Schema Parameters => new(new[] { new Field("x", Int64Type.Default, nullable: true) }, metadata: null);
    public Field Result => new("host_sum", Int64Type.Default, nullable: false);

    public IArrowArray Invoke(RecordBatch args)
    {
        var schema = new Schema(new[] { new Field("v", Int64Type.Default, nullable: false) }, metadata: null);
        var col = new Int64Array.Builder().Append(1).Append(2).Append(3).Append(4).Build();
        var inputBatch = new RecordBatch(schema, new IArrowArray[] { col }, 4);
        var inputStream = new InMemoryArrayStream(schema, new[] { inputBatch }); // host consumes + disposes it

        long sum;
        using (var result = Host.Query("SELECT sum(v)::BIGINT AS s FROM in0",
                                       new[] { ("in0", (IArrowArrayStream)inputStream) }))
        {
            var b = result.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("host_query returned no result");
            sum = ((Int64Array)b.Column(0)).GetValue(0)!.Value; // 1+2+3+4 = 10
        }
        var outb = new Int64Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            outb.Append(sum);
        }
        return outb.Build();
    }
}

// Demo: dbo.cf_host_param(x) binds a 1-row Arrow params batch [40, 2] POSITIONALLY into a host query
// (SELECT (?::BIGINT)+(?::BIGINT)) via a prepared statement on a fresh host connection -> 42. Proves
// host_query parameter binding. host-query.md.
internal sealed class CfHostParamFunction : ICatalogScalarFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_host_param";
    public Schema Parameters => new(new[] { new Field("x", Int64Type.Default, nullable: true) }, metadata: null);
    public Field Result => new("host_param", Int64Type.Default, nullable: false);

    public IArrowArray Invoke(RecordBatch args)
    {
        var pschema = new Schema(new[]
        {
            new Field("p0", Int64Type.Default, nullable: false),
            new Field("p1", Int64Type.Default, nullable: false),
        }, metadata: null);
        var p0 = new Int64Array.Builder().Append(40).Build();
        var p1 = new Int64Array.Builder().Append(2).Build();
        var paramBatch = new RecordBatch(pschema, new IArrowArray[] { p0, p1 }, 1);

        long answer;
        using (var result = Host.Query("SELECT (?::BIGINT) + (?::BIGINT) AS s", paramBatch))
        {
            var b = result.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("host_query returned no result");
            answer = ((Int64Array)b.Column(0)).GetValue(0)!.Value; // 40 + 2 = 42
        }
        var outb = new Int64Array.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            outb.Append(answer);
        }
        return outb.Build();
    }
}

// GLOBAL scalar (connection-free, no ATTACH): fabricator_render(template, params) -> the Liquid template rendered
// with the params bag, where `params` is EITHER a DuckDB STRUCT ({'name':'x','n':3}, type-safe, no quoting) OR a
// JSON string ('{"name":"x"}'). A template engine (Fluid / Liquid — secure-by-default, parse-once cached).
// Registered at extension load via SqlServerBackend.GlobalScalarFunctions; resolved as a bare fn(...) with no
// catalog. Implements the base IScalarFunction (no SchemaName). See docs/global-functions.md.
internal sealed class CfRenderFunction : IScalarFunction
{
    private static readonly FluidParser Parser = new();
    // Parse-once / render-many: templates are usually a constant literal across a batch, so cache the parsed,
    // thread-safe IFluidTemplate keyed by the template string.
    private static readonly ConcurrentDictionary<string, IFluidTemplate> Cache = new();

    public string Name => "fabricator_render";

    public Schema Parameters => new(new[]
    {
        new Field("template", StringType.Default, nullable: true),
        // The SQLNULL sentinel => the host registers this param as LogicalType::ANY, so a caller may pass a
        // STRUCT (preferred) OR a JSON string; Invoke reads the column's runtime type.
        new Field("params", NullType.Default, nullable: true),
    }, metadata: null);

    public Field Result => new("result", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var templates = (StringArray)args.Column(0);
        var paramsCol = args.Column(1); // a StructArray (preferred), a StringArray (JSON), or a NullArray
        var structType = (paramsCol as StructArray)?.Data.DataType as StructType;
        var b = new StringArray.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (templates.IsNull(i))
            {
                b.AppendNull();
                continue;
            }
            var template = Cache.GetOrAdd(templates.GetString(i), src =>
            {
                if (!Parser.TryParse(src, out var parsed, out var error))
                {
                    throw new ArgumentException($"fabricator_render: template parse error: {error}");
                }
                return parsed;
            });
            var ctx = new TemplateContext();
            if (paramsCol is StructArray sa && structType is not null)
            {
                // STRUCT params: each field becomes a template variable (the field's value at this row).
                for (int k = 0; k < structType.Fields.Count; k++)
                {
                    ctx.SetValue(structType.Fields[k].Name, ArrowValueReader.ReadScalar(sa.Fields[k], i));
                }
            }
            else if (paramsCol is StringArray jsonStrs && !jsonStrs.IsNull(i))
            {
                // JSON-string params (programmatic callers).
                using var doc = JsonDocument.Parse(jsonStrs.GetString(i));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        ctx.SetValue(p.Name, JsonToClr(p.Value));
                    }
                }
            }
            b.Append(template.Render(ctx));
        }
        return b.Build();
    }

    private static object? JsonToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => e.EnumerateArray().Select(JsonToClr).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => JsonToClr(p.Value)),
        _ => e.ToString(),
    };
}

// GLOBAL in-out (streaming, connection-free): fabricator_tag(<table of n>) -> (n, sq=n*n) per input row, no
// ATTACH. The global analog of cf_tag — implements the base IInOutFunction (no SchemaName); registered at load
// on the streaming-exchange operator. The author writes DoExchange (one output batch per input chunk + a
// length-0 sentinel). See docs/global-functions.md.
internal sealed class GfTagFunction : IInOutFunction
{
    public string Name => "fabricator_tag";
    public Schema InputSchema => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);
    public IArrowInOutBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : IArrowInOutBinding
    {
        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int32Type.Default, nullable: true),
            new Field("sq", Int32Type.Default, nullable: true),
        }, metadata: null);

        public async IAsyncEnumerable<RecordBatch> DoExchange(
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
                        if (n.IsNull(i)) { nb.AppendNull(); sq.AppendNull(); }
                        else { nb.Append(n.Values[i]); sq.Append(n.Values[i] * n.Values[i]); }
                    }
                    yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), sq.Build() }, rows);
                }
                yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel (NEED_MORE_INPUT)
            }
        }

        public void Dispose() { }
    }
}

// GLOBAL collector (pipeline-breaker, connection-free): fabricator_collect_sum(<table of n>) -> (n, total),
// emitting every input row paired with the GLOBAL sum across ALL input — only knowable after the whole input
// is seen. The global analog of cf_collect; implements the base ICollectorTableFunction, registered at load on
// the Sink+Source collector operator. No ATTACH. See docs/global-functions.md.
internal sealed class GfCollectSumFunction : ICollectorTableFunction
{
    public string Name => "fabricator_collect_sum";
    public Schema InputSchema => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);
    public IArrowCollectorBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : IArrowCollectorBinding
    {
        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int32Type.Default, nullable: true),
            new Field("total", Int64Type.Default, nullable: false),
        }, metadata: null);

        public async IAsyncEnumerable<RecordBatch> Collect(
            IAsyncEnumerable<RecordBatch> allInput, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var values = new List<int?>();
            long total = 0;
            await foreach (var chunk in allInput.WithCancellation(ct))
            {
                using (chunk)
                {
                    var n = (Int32Array)chunk.Column(0);
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        if (n.IsNull(i)) { values.Add(null); }
                        else { values.Add(n.Values[i]); total += n.Values[i]; }
                    }
                }
            }
            const int batchRows = 2048;
            for (int off = 0; off < values.Count; off += batchRows)
            {
                int rows = Math.Min(batchRows, values.Count - off);
                var nb = new Int32Array.Builder().Reserve(rows);
                var tb = new Int64Array.Builder().Reserve(rows);
                for (int i = 0; i < rows; i++)
                {
                    if (values[off + i].HasValue) { nb.Append(values[off + i]!.Value); } else { nb.AppendNull(); }
                    tb.Append(total);
                }
                yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), tb.Build() }, rows);
            }
        }

        public void Dispose() { }
    }
}

// GLOBAL table (connection-free): fabricator_seq(n) -> rows (value, squared) for value = 1..n, no ATTACH.
// Fixed output schema. Implements the base ITableFunction (no SchemaName); registered at load on the v29
// table-session path (handle-0 table_bind). See docs/global-functions.md.
internal sealed class GfSeqFunction : ITableFunction
{
    public string Name => "fabricator_seq";
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
        public Binding(int n) => _n = n;

        public Schema OutputSchema => new(new[]
        {
            new Field("value", Int32Type.Default, nullable: false),
            new Field("squared", Int32Type.Default, nullable: false),
        }, metadata: null);

        public bool SupportsPushdown => false;

        public async IAsyncEnumerable<RecordBatch> Execute(
            TableFunctionScan scan, [EnumeratorCancellation] CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            await Task.CompletedTask;
            var value = new Int32Array.Builder().Reserve(_n);
            var squared = new Int32Array.Builder().Reserve(_n);
            for (int i = 1; i <= _n; i++) { value.Append(i); squared.Append(i * i); }
            yield return new RecordBatch(OutputSchema, new IArrowArray[] { value.Build(), squared.Build() }, _n);
        }

        public void Dispose() { }
    }
}

// GLOBAL table with ARG-DEPENDENT output schema (connection-free): fabricator_columns(n) -> a single row with n
// INT columns c1..cn (c_i = i). The output COLUMN SET depends on the constant arg n — resolved at bind via the
// handle-0 table_bind (the v29 session), proving arg-dependent global table schemas. No ATTACH. The global
// analog of cf_columns.
internal sealed class GfColumnsFunction : ITableFunction
{
    public string Name => "fabricator_columns";
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
            for (int i = 1; i <= _n; i++) { fields[i - 1] = new Field($"c{i}", Int32Type.Default, nullable: false); }
            OutputSchema = new Schema(fields, metadata: null);
        }

        public Schema OutputSchema { get; }
        public bool SupportsPushdown => false;

        public async IAsyncEnumerable<RecordBatch> Execute(
            TableFunctionScan scan, [EnumeratorCancellation] CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            await Task.CompletedTask;
            var arrays = new IArrowArray[_n];
            for (int i = 1; i <= _n; i++) { var b = new Int32Array.Builder(); b.Append(i); arrays[i - 1] = b.Build(); }
            yield return new RecordBatch(OutputSchema, arrays, 1);
        }

        public void Dispose() { }
    }
}

// GLOBAL aggregate (UDAF, connection-free): fabricator_product(x BIGINT) -> BIGINT, the product of all non-NULL
// inputs, no ATTACH. The global analog of cf_product; implements the base IAggregateFunction (no SchemaName),
// registered at load. Works in GROUP BY / OVER / parallel via the shared state-vectorized session. Empty group
// / all-NULL => NULL. See docs/global-functions.md.
internal sealed class GfProductFunction : IAggregateFunction
{
    public string Name => "fabricator_product";
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
                if (x.IsNull(i)) { continue; } // NULLs skipped (standard aggregate semantics)
                _product *= x.Values[i];
                _any = true;
            }
        }

        public void Combine(IArrowAggregateState source)
        {
            var s = (State)source;
            if (s._any) { _product *= s._product; _any = true; }
        }

        public object? Finalize() => _any ? _product : null;
    }
}

// Demo: dbo.cf_add(a, b) -> a + b, computed in C# (no such object exists in SQL Server).
internal sealed class CfAddFunction : ICatalogScalarFunction
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
