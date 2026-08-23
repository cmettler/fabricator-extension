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
        // hilbert_index(coords BIGINT[], bits) — n-dimensional Hilbert-curve position, for liquid-clustering-
        // style ordered writes (ORDER BY hilbert_index(...) rides DuckDB's spilling sort; the write pipeline
        // stays streaming). Provider-agnostic core (lives in the Bridge, like DeltaGlobalTableFunction).
        new Fabricator.Bridge.HilbertIndexFunction(),
        // bucket(n, value) — the Iceberg/DuckLake Murmur3 bucket transform, for bucket PARTITIONING of
        // high-cardinality keys: materialize `bucket(8, col)` as a column, PARTITION BY it, and prune with
        // `WHERE bucket_col = bucket(8, <literal>)` (CONSISTENT => the constant side folds at plan time).
        new Fabricator.Bridge.BucketFunction(),
        // fabricator_batch_seq() — takes NO ARGUMENTS, which is the point: it is the gate for zero-argument
        // scalars, previously recorded as impossible. See BatchSeqFunction and verify_global_functions §.
        new Fabricator.Bridge.BatchSeqFunction(),
    };

    // Connection-free GLOBAL table-in-out (streaming exchange) functions — bare fn(<input>), no ATTACH.
    // Implement the base IInOutFunction (no SchemaName).
    public static readonly IReadOnlyList<IInOutFunction> GlobalInOut = new IInOutFunction[]
    {
        new GfTagFunction(),
        // Mixed signature: table input + POSITIONAL + NAMED in one declaration (see GfMixFunction).
        new GfMixFunction(),
    };

    // Connection-free GLOBAL row-mapped (correlated LATERAL) functions — bare fn(a, b), callable BOTH with
    // literal args and correlated against an outer relation (FROM t, fn(t.a, t.b)). Implement the base
    // ILateralTableFunction (no SchemaName). See ILateralTableFunction / catalog/fabricator_lateral.hpp.
    public static readonly IReadOnlyList<ILateralTableFunction> GlobalLateral = new ILateralTableFunction[]
    {
        new GfLatRepeatFunction(),
        new GfLatScaleFunction(),
        new GfLatBadOriginFunction(),
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

    // Connection-free GLOBAL SQL-GENERATING table functions — bare fn(args), no ATTACH. The call is REPLACED
    // at bind time by the SQL the generator builds from the constant args (DuckDB's bind_replace), so the
    // output schema is arg-dependent for free and NO data crosses the bridge at execution — each referenced
    // scan keeps its own pushdown. Use when the SQL TEXT must depend on the args (a macro covers fixed text
    // with varying values). See docs/macros-and-sqlgen-functions.md §2.
    public static readonly IReadOnlyList<ISqlTableFunction> GlobalSqlTable = new ISqlTableFunction[]
    {
        new GfSqlSeqFunction(),
        new GfDeltaUnionFunction(),
    };

    // Provider MACROs — SQL templates registered into DuckDB's system catalog at extension load, so they
    // resolve as a bare fn(...) / FROM fn(...) in every database with no ATTACH. Each is one complete CREATE
    // MACRO statement parsed by DuckDB itself (named-parameter defaults + overload sets + AS TABLE all work).
    // A macro is expanded by the BINDER — parameters substitute as expressions, nothing crosses the bridge at
    // runtime — so this is the cheapest way to ship sugar over our functions. When the SQL TEXT itself must
    // depend on the arguments, use a SQL-generating table function instead.
    // See docs/macros-and-sqlgen-functions.md.
    public static readonly IReadOnlyList<MacroDefinition> GlobalMacros = new MacroDefinition[]
    {
        // Split fabricator's TRANSIENT Delta rowid — (fileOrdinal << 40) | rowPositionInFile, the DML locator
        // exposed as _metadata.row_id — into its two halves. Diagnostics for the rowid/DV paths
        // (docs/rowid-concepts.md); NOT the stable row-tracking id (that is __delta_row_id).
        new MacroDefinition("fabricator_rowid_parts",
            "CREATE MACRO fabricator_rowid_parts(rid) AS "
            + "{'file_ordinal': rid >> 40, 'row_position': rid & ((1::BIGINT << 40) - 1)}"),
        // The inverse — compose a transient rowid. An OVERLOAD SET (dispatch by arity): from the two halves,
        // or round-tripping a fabricator_rowid_parts struct.
        new MacroDefinition("fabricator_rowid_of",
            "CREATE MACRO fabricator_rowid_of(file_ordinal, row_position) AS "
            + "(file_ordinal::BIGINT << 40) | row_position, "
            + "(parts) AS (parts.file_ordinal::BIGINT << 40) | parts.row_position"),
        // Sugar over the bucket() global scalar with the DuckLake/Iceberg default bucket count — shows a
        // NAMED PARAMETER WITH A DEFAULT crossing intact: fabricator_bucket_of(x), ..., n := 16, or
        // positionally (..., 16 — DuckDB accepts either for a defaulted macro parameter).
        new MacroDefinition("fabricator_bucket_of",
            "CREATE MACRO fabricator_bucket_of(v, n := 8) AS bucket(n, v)"),
        // A TABLE macro over the global Delta reader: peek at any Delta table by path, no ATTACH.
        new MacroDefinition("fabricator_delta_head",
            "CREATE MACRO fabricator_delta_head(path, n := 100) AS TABLE "
            + "SELECT * FROM fabricator_delta_scan(path) LIMIT n"),
    };

    // CATALOG-BOUND macros: bound into the attached catalog's schemas, so they resolve as db.dbo.cm_*(…) rather
    // than under a bare global name. The value is NAMESPACING — two attached servers can expose differently
    // shaped helpers under the same short name — and it is the one shape nothing else here serves cheaply: a
    // catalog SCALAR helper. ICatalogSqlTableFunction is table-valued only, and ICatalogScalarFunction marshals
    // every call over the ABI; a macro is expanded by the binder and crosses nothing at runtime.
    //
    // Bodies are SELF-CONTAINED on purpose. DuckDB captures no search path when expanding a macro, so an
    // unqualified table reference here would resolve against the CALLER's catalog/schema, not this one — a
    // silently-wrong-table hazard. A body that must read this catalog's tables belongs in an
    // ICatalogSqlTableFunction, which is handed the ATTACH alias (the only way to qualify such a reference,
    // since the alias is chosen at ATTACH time).
    public static readonly IReadOnlyList<CatalogMacroDefinition> CatalogMacros = new CatalogMacroDefinition[]
    {
        // Scalar, in the discovered `dbo` schema: db.dbo.cm_pct(part, whole).
        new CatalogMacroDefinition("dbo", "cm_pct",
            "CREATE MACRO cm_pct(part, whole) AS "
            + "CASE WHEN whole IS NULL OR whole = 0 THEN NULL ELSE round(100.0 * part / whole, 2) END"),
    };

    // Catalog-bound VIEWS (ABI v77) — and note the contrast with the macro comment directly above: a VIEW
    // body IS anchored to this catalog + schema by DuckDB's view binder, so an unqualified table reference
    // here would resolve against THIS catalog. That is what makes a view, not a macro, the declaration form
    // for a body that names provider tables. The one below stays self-contained anyway, because a shipped
    // declaration cannot know which tables a given server has.
    public static readonly IReadOnlyList<ViewDefinition> CatalogViews = new ViewDefinition[]
    {
        // In the discovered `dbo` schema: SELECT * FROM db.dbo.cv_info.
        new ViewDefinition("dbo", "cv_info",
            "CREATE VIEW cv_info AS SELECT 'fabricator' AS provider, 'sqlserver' AS kind"),
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
    // SQL-GENERATING table functions (ICatalogSqlTableFunction), singletons — surfaced as `kind='table_sql'`.
    // The call `db.dbo.fn(args)` is REPLACED at bind time by SQL this generator builds, so nothing crosses the
    // bridge at execution and every scan it references keeps its own pushdown. The generator gets the catalog's
    // ATTACH alias + the live catalog (bind-time lookups). See docs/macros-and-sqlgen-functions.md §2.
    public static readonly IReadOnlyList<ICatalogSqlTableFunction> SqlTable = new ICatalogSqlTableFunction[]
    {
        new CfUnionByPatternFunction(),
    };

    public static readonly IReadOnlyList<ICatalogInOutFunction> InOut = new ICatalogInOutFunction[]
    {
        new CfTagFunction(),
        new CfRunningSumFunction(),
        new CfExchangeFunction(),
    };

    // Catalog-bound row-mapped (correlated LATERAL) functions (ICatalogLateralFunction), singletons —
    // surfaced as `kind='lateral'` and resolved by SqlServerCatalog.LateralBind. Pure C#, no SQL object.
    public static readonly IReadOnlyList<ICatalogLateralFunction> Lateral = new ICatalogLateralFunction[]
    {
        new CfLatSplitFunction(),
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
    public IAggregateState CreateState() => new State();

    private sealed class State : IAggregateState
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
        public void Combine(IAggregateState source) => _values.AddRange(((State)source)._values);

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
    public IAggregateState CreateState() => new State();

    private sealed class State : IAggregateState
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

        public void Combine(IAggregateState source)
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
    public IAggregateState CreateState() => new State();

    private sealed class State : IAggregateState
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

        public void Combine(IAggregateState source)
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
    public IAggregateState CreateState() => new State();

    private sealed class State : IAggregateState
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

        public void Combine(IAggregateState source)
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

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var a = (Int32Array)args.Column(0);
        int n = args.Length > 0 && !a.IsNull(0) ? a.Values[0] : 0;
        return new Binding(n);
    }

    private sealed class Binding : ITableFunctionBinding
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
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        // Dispose eagerly in a plain method, then delegate — see the lifetime note in StaticTableFunction.Execute.
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows([EnumeratorCancellation] CancellationToken ct)
        {
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

    // The declared signature, in the target authoring shape: the input table IS a parameter, so it is one
    // field flagged as the table input rather than a schema of its own.
    public Schema Parameters => new(new[]
    {
        Params.TableInput("input", new Field("n", Int32Type.Default, nullable: true)),
    }, metadata: null);
    public IInOutBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : IInOutBinding
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

// GLOBAL in-out proving a MIXED signature: fabricator_mix(<table of n>, factor, bias := k) -> (n, value)
// where value = n * factor + bias. The point is the SIGNATURE, not the arithmetic: a table input, a
// POSITIONAL constant arg and a NAMED one in a single declaration.
//
// ⚠ Positional constant args on an in-out were previously impossible by omission — the bind marshalled ONLY
// named parameters, so a positional one would have been accepted by the binder and then silently dropped
// before reaching C#. The unified protocol makes it declarable and FabricatorMarshalInOutArgs makes it
// arrive; this function is what keeps both true.
internal sealed class GfMixFunction : IInOutFunction
{
    public string Name => "fabricator_mix";

    public Schema Parameters => new(new[]
    {
        Params.TableInput("input", new Field("n", Int32Type.Default, nullable: true)),
        Params.Positional("factor", Int32Type.Default),
        // NOT "offset": a named parameter whose name is a DuckDB reserved word cannot be written
        // unquoted at the call site (`offset := 10` is a parser error), which makes the function
        // look broken rather than mis-named.
        Params.Named("bias", Int32Type.Default),
    }, metadata: null);

    public IInOutBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        // Read BY POSITION over the declared order, skipping the table input (it carries no value). An
        // omitted named argument arrives as a typed NULL, which is the documented "omitted == explicit NULL".
        int factor = ReadInt(args, "factor") ?? 1;
        int bias = ReadInt(args, "bias") ?? 0;
        return new Binding(factor, bias);
    }

    private static int? ReadInt(RecordBatch? args, string name)
    {
        if (args is null) { return null; }
        for (int i = 0; i < args.ColumnCount; i++)
        {
            if (string.Equals(args.Schema.FieldsList[i].Name, name, System.StringComparison.OrdinalIgnoreCase)
                && args.Column(i) is Int32Array a && a.Length > 0 && !a.IsNull(0))
            {
                return a.Values[0];
            }
        }
        return null;
    }

    private sealed class Binding : IInOutBinding
    {
        private readonly int _factor;
        private readonly int _offset;

        public Binding(int factor, int offset) { _factor = factor; _offset = offset; }

        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int32Type.Default, nullable: true),
            new Field("value", Int32Type.Default, nullable: true),
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
                    var vb = new Int32Array.Builder().Reserve(rows);
                    for (int i = 0; i < rows; i++)
                    {
                        if (n.IsNull(i)) { nb.AppendNull(); vb.AppendNull(); }
                        else { nb.Append(n.Values[i]); vb.Append(n.Values[i] * _factor + _offset); }
                    }
                    yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), vb.Build() }, rows);
                }
                yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel
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

    public Schema Parameters => new(new[]
    {
        Params.TableInput("input", new Field("n", Int32Type.Default, nullable: true)),
    }, metadata: null);
    public ICollectorBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ICollectorBinding
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

// CATALOG-BOUND SQL-GENERATING table function: db.dbo.cf_union_by_pattern('sales_%') unions every table in
// the catalog whose name matches the LIKE pattern into one relation. The member list is discovered at BIND time
// on this catalog's own connection (sys.tables), then spliced into the generated SQL as quoted three-part
// names — so the SQL TEXT depends on the argument, which no macro can express, and each member scan is a normal
// catalog scan that keeps its projection/filter/TopN pushdown into SQL Server (which a marshaled table function
// would forfeit by streaming every row through C#). See docs/macros-and-sqlgen-functions.md §2.
internal sealed class CfUnionByPatternFunction : ICatalogSqlTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ICatalogSqlTableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    public string SchemaName => "dbo";
    public string Name => "cf_union_by_pattern";

    public Schema Parameters => new(new[]
    {
        new Field("pattern", StringType.Default, nullable: false),
    }, metadata: null);

    // UNION ALL BY NAME (the member column sets differ) vs positional UNION ALL. Also `schema_filter`, so the
    // sweep can be narrowed to one SQL schema.
    public Schema NamedParameters => new(new[]
    {
        new Field("by_name", BooleanType.Default, nullable: true),
        new Field("schema_name", StringType.Default, nullable: true),
    }, metadata: null);

    public string GenerateSql(SqlGenContext ctx, RecordBatch args)
    {
        string pattern = ((StringArray)args.Column("pattern")).GetString(0)!;
        bool byName = ReadBool(args, "by_name") ?? false;
        string? schemaFilter = ReadString(args, "schema_name");

        // BIND-TIME LOOKUP on this catalog's own connection: which tables match? (T-SQL string literals —
        // apostrophes doubled. LIKE metacharacters in the pattern are the caller's intent, not escaped.)
        var sql = "SELECT s.name AS schema_name, t.name AS table_name FROM sys.tables t "
                  + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                  + $"WHERE t.name LIKE '{Tsql(pattern)}'"
                  + (schemaFilter is null ? string.Empty : $" AND s.name = '{Tsql(schemaFilter)}'")
                  + " ORDER BY s.name, t.name";
        var members = new List<string>();
        using (var stream = ctx.Catalog.ExecuteQuery(sql))
        {
            RecordBatch? batch;
            while ((batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()) is not null)
            {
                using (batch)
                {
                    var schemas = (StringArray)batch.Column(0);
                    var tables = (StringArray)batch.Column(1);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        // Fully qualified with the ATTACH alias so each member binds as a catalog scan.
                        members.Add("SELECT * FROM "
                                    + DuckSql.QuoteName(ctx.CatalogName, schemas.GetString(i), tables.GetString(i)));
                    }
                }
            }
        }
        if (members.Count == 0)
        {
            throw new ArgumentException(
                $"cf_union_by_pattern: no table matches '{pattern}' in catalog '{ctx.CatalogName}'");
        }
        return string.Join(byName ? " UNION ALL BY NAME " : " UNION ALL ", members);
    }

    private static string Tsql(string s) => s.Replace("'", "''");

    private static bool? ReadBool(RecordBatch args, string field)
    {
        int i = args.Schema.GetFieldIndex(field);
        return i >= 0 && args.Column(i) is BooleanArray a && !a.IsNull(0) ? a.GetValue(0) : null;
    }

    private static string? ReadString(RecordBatch args, string field)
    {
        int i = args.Schema.GetFieldIndex(field);
        return i >= 0 && args.Column(i) is StringArray a && !a.IsNull(0) ? a.GetString(0) : null;
    }
}

// GLOBAL SQL-GENERATING table function (connection-free): fabricator_delta_union(paths[, by_name := …])
// unions several Delta tables BY PATH in one relation — the SQL text (one fabricator_delta_scan per path)
// depends on the argument, which is exactly what a macro cannot express. Since the call is REPLACED by that
// SQL at bind time, each member scan keeps its own filter/projection pushdown and nothing streams through C#.
// See docs/macros-and-sqlgen-functions.md §2.
internal sealed class GfDeltaUnionFunction : ISqlTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ISqlTableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    public string Name => "fabricator_delta_union";

    public Schema Parameters => new(new[]
    {
        new Field("paths", new ListType(new Field("item", StringType.Default, nullable: true)), nullable: false),
    }, metadata: null);

    // UNION ALL BY NAME (column sets differ) vs positional UNION ALL — the query_table() option, named so it
    // stays optional.
    public Schema NamedParameters => new(new[]
    {
        new Field("by_name", BooleanType.Default, nullable: true),
    }, metadata: null);

    public string GenerateSql(RecordBatch args)
    {
        var list = (ListArray)args.Column("paths");
        var items = (StringArray)list.GetSlicedValues(0);
        if (items.Length == 0)
        {
            throw new ArgumentException("fabricator_delta_union: the path list is empty");
        }
        bool byName = false;
        var byNameCol = args.Schema.GetFieldIndex("by_name");
        if (byNameCol >= 0 && args.Column(byNameCol) is BooleanArray b && !b.IsNull(0))
        {
            byName = b.GetValue(0)!.Value;
        }
        var union = byName ? " UNION ALL BY NAME " : " UNION ALL ";
        var parts = new List<string>(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            var path = items.GetString(i)
                       ?? throw new ArgumentException("fabricator_delta_union: NULL path in the list");
            parts.Add($"SELECT * FROM fabricator_delta_scan({DuckSql.QuoteString(path)})");
        }
        return string.Join(union, parts);
    }
}

// GLOBAL SQL-generating demo with an ARG-DEPENDENT OUTPUT SCHEMA — the capability the design calls out:
// fabricator_sql_seq(n) generates `SELECT i, i*i AS sq FROM range(...)`, and fabricator_sql_seq(n, cols := 3)
// widens the projection, so two calls of the same function bind to different column sets with no schema
// declaration anywhere (the plan decides). Also the cheapest end-to-end pin of the bind_replace path.
internal sealed class GfSqlSeqFunction : ISqlTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ISqlTableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    public string Name => "fabricator_sql_seq";

    public Schema Parameters => new(new[]
    {
        new Field("n", Int64Type.Default, nullable: false),
    }, metadata: null);

    public Schema NamedParameters => new(new[]
    {
        new Field("cols", Int64Type.Default, nullable: true),
    }, metadata: null);

    public string GenerateSql(RecordBatch args)
    {
        long n = ((Int64Array)args.Column("n")).GetValue(0)!.Value;
        long cols = 1;
        int idx = args.Schema.GetFieldIndex("cols");
        if (idx >= 0 && args.Column(idx) is Int64Array c && !c.IsNull(0))
        {
            cols = c.GetValue(0)!.Value;
        }
        if (n < 0 || cols < 1)
        {
            throw new ArgumentException("fabricator_sql_seq: n must be >= 0 and cols >= 1");
        }
        var projection = new List<string> { "i" };
        for (long k = 1; k <= cols; k++)
        {
            projection.Add($"i * {DuckSql.Literal(k)} AS {DuckSql.QuoteIdent("c" + k)}");
        }
        return $"SELECT {string.Join(", ", projection)} FROM range(1, {DuckSql.Literal(n + 1)}) t(i)";
    }
}

// GLOBAL table (connection-free): fabricator_seq(n) -> rows (value, squared) for value = 1..n, no ATTACH.
// Fixed output schema. Implements the base ITableFunction (no SchemaName); registered at load on the v29
// table-session path (handle-0 tablefn_bind). See docs/global-functions.md.
internal sealed class GfSeqFunction : ITableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    public string Name => "fabricator_seq";
    public Schema Parameters => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    /// <summary>
    /// <c>fabricator_seq(5, start := 10)</c> — an OPTIONAL argument, so this function has a MIXED signature
    /// (one positional + one named). Named because DuckDB positional table arguments have no defaults.
    /// </summary>
    /// <remarks>
    /// The mixed shape is the one worth demonstrating: the binding reads BY POSITION
    /// (<see cref="Parameters"/> ++ <see cref="NamedParameters"/>), and an omitted named argument arrives as
    /// NULL, so a bug in the host's NULL substitution would shift the POSITIONAL values too.
    /// </remarks>
    public Schema NamedParameters =>
        new(new[] { new Field("start", Int32Type.Default, nullable: true) }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var a = (Int32Array)args.Column(0);
        int n = args.Length > 0 && !a.IsNull(0) ? a.Values[0] : 0;
        // Position 1 is `start` (the first named parameter); NULL when the caller omitted it.
        int start = 1;
        if (args.ColumnCount > 1 && args.Column(1) is Int32Array s2 && args.Length > 0 && !s2.IsNull(0))
        {
            start = s2.Values[0];
        }
        return new Binding(n, start);
    }

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly int _n;
        private readonly int _start;

        public Binding(int n, int start)
        {
            _n = n;
            _start = start;
        }

        public Schema OutputSchema => new(new[]
        {
            new Field("value", Int32Type.Default, nullable: false),
            new Field("squared", Int32Type.Default, nullable: false),
        }, metadata: null);

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        // Dispose eagerly in a plain method, then delegate — see the lifetime note in StaticTableFunction.Execute.
        // (This is the exact shape that aborted on macOS: a deferred dispose released the host's filter-values
        // producer during get_next, after InitGlobal had destroyed it.)
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var value = new Int32Array.Builder().Reserve(_n);
            var squared = new Int32Array.Builder().Reserve(_n);
            for (int k = 0; k < _n; k++)
            {
                int i = _start + k;
                value.Append(i);
                squared.Append(i * i);
            }
            yield return new RecordBatch(OutputSchema, new IArrowArray[] { value.Build(), squared.Build() }, _n);
        }

        public void Dispose() { }
    }
}

// GLOBAL table with ARG-DEPENDENT output schema (connection-free): fabricator_columns(n) -> a single row with n
// INT columns c1..cn (c_i = i). The output COLUMN SET depends on the constant arg n — resolved at bind via the
// handle-0 tablefn_bind (the v29 session), proving arg-dependent global table schemas. No ATTACH. The global
// analog of cf_columns.
internal sealed class GfColumnsFunction : ITableFunction
{
    public string Name => "fabricator_columns";
    public Schema Parameters => new(new[] { new Field("n", Int32Type.Default, nullable: true) }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var a = (Int32Array)args.Column(0);
        int n = args.Length > 0 && !a.IsNull(0) ? a.Values[0] : 0;
        return new Binding(n);
    }

    private sealed class Binding : ITableFunctionBinding
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
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        // Dispose eagerly in a plain method, then delegate — see the lifetime note in StaticTableFunction.Execute.
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows([EnumeratorCancellation] CancellationToken ct)
        {
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
    public IAggregateState CreateState() => new State();

    private sealed class State : IAggregateState
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

        public void Combine(IAggregateState source)
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

// GLOBAL row-mapped (correlated LATERAL) demo, 1->N: fabricator_lat_repeat(n, times) emits `times` rows
// (n, i) for i = 0 .. times-1. It is the FAN-OUT shape, so it MUST return provenance — without it the host
// could not tell which outer row each emitted row belongs to, and the correlated columns of
// `SELECT t.id, r.* FROM t, fabricator_lat_repeat(t.n, 2) r` would be stamped wrong.
//
// It also covers 1->0 (times <= 0 or NULL emits nothing for that row) and, with a large `times`, the case
// where ONE input chunk produces more than a DuckDB vector of output — which is what exercises the host's
// multi-slice drain.
internal sealed class GfLatRepeatFunction : ILateralTableFunction
{
    public string Name => "fabricator_lat_repeat";

    // Both POSITIONAL — i.e. these two ARE the per-row input columns, and their types are what DuckDB
    // registers as the function's argument types. Nothing here is a table input.
    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Positional("times", Int32Type.Default),
    }, metadata: null);

    public ILateralBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralBinding
    {
        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int32Type.Default, nullable: true),
            new Field("i", Int32Type.Default, nullable: true),
        }, metadata: null);

        public ILateralSession Open() => new Session(OutputSchema);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;

        public Session(Schema output) => _output = output;

        public LateralResult Call(RecordBatch input)
        {
            var n = (Int32Array)input.Column(0);
            var times = (Int32Array)input.Column(1);
            var nb = new Int32Array.Builder();
            var ib = new Int32Array.Builder();
            var origin = new List<int>();
            for (int r = 0; r < input.Length; r++)
            {
                int count = times.IsNull(r) ? 0 : times.Values[r];
                for (int k = 0; k < count; k++)
                {
                    if (n.IsNull(r)) { nb.AppendNull(); } else { nb.Append(n.Values[r]); }
                    ib.Append(k);
                    origin.Add(r); // THE point: which input row produced this output row
                }
            }
            int m = origin.Count;
            if (m == 0)
            {
                return LateralResult.Empty; // every input row filtered out — a legitimate answer, not EOS
            }
            return new LateralResult(
                new RecordBatch(_output, new IArrowArray[] { nb.Build(), ib.Build() }, m), origin.ToArray());
        }

        public void Dispose() { }
    }
}

// GLOBAL lateral demo, 1->1 with NO provenance and a NAMED cost arg:
// fabricator_lat_scale(n [, factor := k]) -> (scaled, batch_rows). Returning no provenance ASSERTS a 1:1 map,
// and the framework holds that assertion STRICTLY — a row count differing from the input's is an error, not a
// guess. The named arg is also the point: a lateral function's positional slots are runtime data, so a named
// parameter is the ONLY way to configure one at bind time.
//
// ⚠ `batch_rows` is what makes BATCHING checkable, and it is DATA rather than a log line: it reports the
// number of input rows the call that produced this row was handed. On the row-by-row path it is 1 for every
// row; on the batched path it is the chunk size. Counting Debug log lines was tried first and does not work —
// duckdb_logs flushes per-thread LAZILY, so a read immediately after the query saw 1 of 98 entries and a
// count assertion would have been comparing whatever happened to be visible.
internal sealed class GfLatScaleFunction : ILateralTableFunction
{
    public string Name => "fabricator_lat_scale";

    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Named("factor", Int32Type.Default),
    }, metadata: null);

    public ILateralBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        // Read the args HERE — the framework owns that batch and its lifetime ends with the bind.
        int factor = 2;
        if (args is not null)
        {
            for (int i = 0; i < args.ColumnCount; i++)
            {
                if (string.Equals(args.Schema.FieldsList[i].Name, "factor", System.StringComparison.OrdinalIgnoreCase)
                    && args.Column(i) is Int32Array a && a.Length > 0 && !a.IsNull(0))
                {
                    factor = a.Values[0];
                }
            }
        }
        return new Binding(factor);
    }

    private sealed class Binding : ILateralBinding
    {
        private readonly int _factor;

        public Binding(int factor) => _factor = factor;

        public Schema OutputSchema => new(new[]
        {
            new Field("scaled", Int32Type.Default, nullable: true),
            new Field("batch_rows", Int32Type.Default, nullable: false),
        }, metadata: null);

        public ILateralSession Open() => new Session(OutputSchema, _factor);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;
        private readonly int _factor;

        public Session(Schema output, int factor)
        {
            _output = output;
            _factor = factor;
        }

        public LateralResult Call(RecordBatch input)
        {
            var n = (Int32Array)input.Column(0);
            var b = new Int32Array.Builder().Reserve(input.Length);
            var rows = new Int32Array.Builder().Reserve(input.Length);
            for (int r = 0; r < input.Length; r++)
            {
                if (n.IsNull(r)) { b.AppendNull(); } else { b.Append(n.Values[r] * _factor); }
                rows.Append(input.Length); // how many rows THIS call was handed — see the class note
            }
            // No Origin: one output row per input row, in order. The framework fills in the identity mapping.
            return new LateralResult(
                new RecordBatch(_output, new IArrowArray[] { b.Build(), rows.Build() }, input.Length));
        }

        public void Dispose() { }
    }
}

// GLOBAL lateral FIXTURE for malformed provenance: fabricator_lat_badorigin(n, mode := '…') deliberately
// returns provenance the framework must refuse. It exists because provenance is used directly as an ARRAY
// INDEX into the input chunk, so "the callee is adversarial" has to be a tested property rather than a
// comment — and both refusals must be reachable from SQL, or neither is gated.
//
//   mode = 'range'   an index equal to the input row count (one past the end)
//   mode = 'length'  one provenance index too many
//   mode = 'missing' a 1->N result with NO provenance at all (the STRICT absent case)
//   anything else    a correct explicit identity mapping — the POSITIVE CONTROL, without which "it errored"
//                    would pass equally on a build where the function had simply stopped working
//
// ⚠ `mode` is POSITIONAL, not named, and that is forced: a NAMED argument cannot be written in the CORRELATED
// call shape at all (DuckDB sweeps every argument expression into the input subquery before extracting named
// parameters — see docs/duckdb-upstream-issues.md), so a named selector would leave the BATCHED path's own
// validation unreachable from SQL. Positional means it arrives as a constant input column instead, which
// works in both shapes.
internal sealed class GfLatBadOriginFunction : ILateralTableFunction
{
    public string Name => "fabricator_lat_badorigin";

    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Positional("mode", StringType.Default),
    }, metadata: null);

    public ILateralBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralBinding
    {
        public Schema OutputSchema => new(new[] { new Field("v", Int32Type.Default, nullable: true) },
                                          metadata: null);

        public ILateralSession Open() => new Session(OutputSchema);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;

        public Session(Schema output) => _output = output;

        public LateralResult Call(RecordBatch input)
        {
            // The mode rides the input as a (constant) column, so it is read per call rather than at bind.
            var modes = (StringArray)input.Column(1);
            string _mode = input.Length > 0 && !modes.IsNull(0) ? modes.GetString(0) ?? "ok" : "ok";
            int rows = _mode == "missing" ? input.Length * 2 : input.Length;
            var b = new Int32Array.Builder().Reserve(rows);
            for (int r = 0; r < rows; r++)
            {
                b.Append(r);
            }
            var batch = new RecordBatch(_output, new IArrowArray[] { b.Build() }, rows);
            switch (_mode)
            {
                case "missing":
                    return new LateralResult(batch); // 2N rows, no provenance => refused
                case "length":
                {
                    var origin = new int[rows + 1];
                    return new LateralResult(batch, origin);
                }
                case "range":
                {
                    var origin = new int[rows];
                    for (int r = 0; r < rows; r++)
                    {
                        origin[r] = input.Length; // one past the last valid index
                    }
                    return new LateralResult(batch, origin);
                }
                default:
                {
                    var origin = new int[rows];
                    for (int r = 0; r < rows; r++)
                    {
                        origin[r] = r;
                    }
                    return new LateralResult(batch, origin);
                }
            }
        }

        public void Dispose() { }
    }
}

// CATALOG-BOUND lateral demo: dbo.cf_lat_split(text, sep) -> (part, idx), one output row per separated
// fragment. Resolved as `SELECT t.id, s.* FROM t, db.dbo.cf_lat_split(t.txt, ',') s` — the spelling that made
// this function kind worth building, and the one an in-out cannot offer (its input is a TABLE parameter, so
// it can only be called on a relation the caller can name).
internal sealed class CfLatSplitFunction : ICatalogLateralFunction
{
    public string SchemaName => "dbo";

    public string Name => "cf_lat_split";

    public Schema Parameters => new(new[]
    {
        Params.Positional("text", StringType.Default),
        Params.Positional("sep", StringType.Default),
    }, metadata: null);

    public ILateralBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralBinding
    {
        public Schema OutputSchema => new(new[]
        {
            new Field("part", StringType.Default, nullable: true),
            new Field("idx", Int32Type.Default, nullable: true),
        }, metadata: null);

        public ILateralSession Open() => new Session(OutputSchema);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;

        public Session(Schema output) => _output = output;

        public LateralResult Call(RecordBatch input)
        {
            var text = (StringArray)input.Column(0);
            var sep = (StringArray)input.Column(1);
            var pb = new StringArray.Builder();
            var ib = new Int32Array.Builder();
            var origin = new List<int>();
            for (int r = 0; r < input.Length; r++)
            {
                if (text.IsNull(r))
                {
                    continue; // a NULL input row contributes nothing (1->0)
                }
                var s = text.GetString(r) ?? string.Empty;
                var d = sep.IsNull(r) ? "," : sep.GetString(r) ?? ",";
                var parts = d.Length == 0 ? new[] { s } : s.Split(d);
                for (int k = 0; k < parts.Length; k++)
                {
                    pb.Append(parts[k]);
                    ib.Append(k);
                    origin.Add(r);
                }
            }
            int m = origin.Count;
            if (m == 0)
            {
                return LateralResult.Empty;
            }
            return new LateralResult(
                new RecordBatch(_output, new IArrowArray[] { pb.Build(), ib.Build() }, m), origin.ToArray());
        }

        public void Dispose() { }
    }
}
