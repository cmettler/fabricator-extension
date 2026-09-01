// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Fabricator.Bridge;
using Fabricator.Bridge.Conversion;

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
        // fabricator_parse(text, type_name) — parses `text` as the named type, and RETURNS that type: its
        // result type is resolved at BIND from the constant `type_name`, which is the ABI v80 scalar bind
        // session's whole point (docs/abi-history.md §v80). The shipped demonstration of the capability, and
        // the only in-tree scalar whose return type is not fixed at registration.
        new CfParseFunction(),
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
        // fabricator_va_concat(sep, …) — the VARIADIC tail (Params.VarArgs): minimum arity 1, then any number
        // of further arguments of ANY type. See the varargs section at the bottom of this file.
        new GfVaConcatFunction(),
    };

    // Connection-free GLOBAL table-in-out (streaming exchange) functions — bare fn(<input>), no ATTACH.
    // Implement the base IInOutFunction (no SchemaName).
    public static readonly IReadOnlyList<IInOutFunction> GlobalInOut = new IInOutFunction[]
    {
        new GfTagFunction(),
        // Mixed signature: table input + POSITIONAL + NAMED in one declaration (see GfMixFunction).
        new GfMixFunction(),
        // Table input + POSITIONAL + a VARIADIC TAIL of bind-time cost args (see GfInOutVaFunction).
        new GfInOutVaFunction(),
    };

    // Connection-free GLOBAL row-mapped (correlated LATERAL) functions — bare fn(a, b), callable BOTH with
    // literal args and correlated against an outer relation (FROM t, fn(t.a, t.b)). Implement the base
    // ILateralFunction (no SchemaName). See ILateralFunction / catalog/fabricator_lateral.hpp.
    public static readonly IReadOnlyList<ILateralFunction> GlobalLateral = new ILateralFunction[]
    {
        new GfLatRepeatFunction(),
        new GfLatScaleFunction(),
        new GfLatBadOriginFunction(),
        new GfLatFieldsFunction(),
        // A lateral whose bind-time CONSTANT is the FIRST parameter -- see GfLatFrontFunction.
        new GfLatFrontFunction(),
        // [CONSTANT][POSITIONAL][VARIADIC TAIL] in one declaration -- see GfLatSpanFunction.
        new GfLatSpanFunction(),
    };

    // Connection-free GLOBAL collector (pipeline-breaker) functions — bare fn(<input>), no ATTACH.
    // Implement the base ICollectorFunction (no SchemaName).
    public static readonly IReadOnlyList<ICollectorFunction> GlobalCollector = new ICollectorFunction[]
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
        // declared here because SqlServer is the always-present default backend.
        new Fabricator.Bridge.DeltaGlobalTableFunction(),
        // NATIVE-READ pre-spike: fabricator_delta_native_scan(path) — engineered-wood lists the exact active files,
        // DuckDB's native parquet reader reads them via read_parquet (cached, over onelake:// for OneLake).
        // Plain tables only (no DV/partition/pushdown yet). See docs/multifile-delta.md Phase A.
        new Fabricator.Bridge.DeltaNativeScanFunction(),
        // HOST-FS WRITE spike: fabricator_delta_write_demo(path) writes a fixed 5-row Delta table via the host-FS
        // write callbacks (put-if-absent commit), returning (version, rows_written). Proves the write bridge.
        new Fabricator.Bridge.DeltaWriteDemoFunction(),
        // fabricator_va_args(label, …) — a VARIADIC table function: one row per tail argument.
        new GfVaArgsFunction(),
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
        // fabricator_va_values(…) — a VARIADIC generator: the SQL's COLUMN COUNT is the argument count.
        new GfVaValuesFunction(),
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
        // cf_va_sum(…) — the CATALOG-BOUND half of the variadic surface. A declaration form that only ever
        // ships GLOBAL looks covered while the second registration path (GetOrCreateScalarFunction) is
        // untested; this closes that, the same gap the catalog-VIEW work had to close for providers.
        new CfVaSumFunction(),
    };

    public static readonly IReadOnlyList<ICatalogTableFunction> Table = new ICatalogTableFunction[]
    {
        new CfRangeFunction(),
        new CfColumnsFunction(),
        new CfVaRowsFunction(),      // VARIADIC tail on the attach-time TABLE path
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
        new CfVaSelectFunction(),    // VARIADIC tail on the attach-time SQLGEN path
    };

    public static readonly IReadOnlyList<ICatalogInOutFunction> InOut = new ICatalogInOutFunction[]
    {
        new CfTagFunction(),
        new CfRunningSumFunction(),
        new CfExchangeFunction(),
        // ⚠ The FIRST catalog-bound in-out to declare a cost argument, and it did not work when it was
        // written: CatalogFunctionSet.ParamSchema omitted in-out/collector, so the host fell back to the bare
        // {TABLE} signature and SILENTLY dropped both the positional and the tail. Fixed at that site the
        // same day; this demo is what found it and is the gate that keeps it fixed.
        new CfVaTagFunction(),
    };

    // Catalog-bound row-mapped (correlated LATERAL) functions (ICatalogLateralFunction), singletons —
    // surfaced as `kind='lateral'` and resolved by SqlServerCatalog.LateralBind. Pure C#, no SQL object.
    public static readonly IReadOnlyList<ICatalogLateralFunction> Lateral = new ICatalogLateralFunction[]
    {
        new CfLatSplitFunction(),
        new CfVaSpanFunction(),      // VARIADIC tail on the attach-time LATERAL path
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

    // Collector table-in-out functions (ICatalogCollectorFunction), singletons — surfaced as
    // `kind='collector'` and resolved by SqlServerCatalog.InOutBind on the Sink+Source pipeline-breaker
    // operator (NOT the streaming exchange). A collector sees ALL input before emitting any output (whole-table
    // semantics), so it can take arbitrarily many input chunks — unlike the streaming in-out. Pure C#.
    public static readonly IReadOnlyList<ICatalogCollectorFunction> Collector = new ICatalogCollectorFunction[]
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
    public IInOutFunctionBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : IInOutFunctionBinding
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

    public IInOutFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
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

    private sealed class Binding : IInOutFunctionBinding
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
// is seen. The global analog of cf_collect; implements the base ICollectorFunction, registered at load on
// the Sink+Source collector operator. No ATTACH. See docs/global-functions.md.
internal sealed class GfCollectSumFunction : ICollectorFunction
{
    public string Name => "fabricator_collect_sum";

    public Schema Parameters => new(new[]
    {
        Params.TableInput("input", new Field("n", Int32Type.Default, nullable: true)),
    }, metadata: null);
    public ICollectorFunctionBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ICollectorFunctionBinding
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
internal sealed class GfLatRepeatFunction : ILateralFunction
{
    public string Name => "fabricator_lat_repeat";

    // Both POSITIONAL — i.e. these two ARE the per-row input columns, and their types are what DuckDB
    // registers as the function's argument types. Nothing here is a table input.
    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Positional("times", Int32Type.Default),
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralFunctionBinding
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
// GLOBAL lateral with a BIND-TIME CONSTANT parameter (Params.Constant — the host capture channel):
// fabricator_lat_fields(n, fields) has an OUTPUT SCHEMA that depends on `fields`, which the v79 registration
// cannot express directly (a positional lateral parameter carries no bind-time value). The captured constant
// may be a VARCHAR of comma-separated column names, an integer count (columns c1..cN), a LIST of names, or
// a STRUCT (its field names become the columns) — pinning that the channel is type-generic. Column i = n * (i+1), 1:1 with the input rows. Callers:
//   SELECT * FROM fabricator_lat_fields(7, 'x,y');                 -- literal shape
//   SELECT * FROM t, fabricator_lat_fields(t.n, 'x,y');             -- correlated: bare constants work too
internal sealed class GfLatFieldsFunction : ILateralFunction
{
    public string Name => "fabricator_lat_fields";

    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int64Type.Default),
        Params.Constant("fields"), // bind-time constant: arrives in Bind's args, never in the rows
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        int idx = args?.Schema.GetFieldIndex("fields") ?? -1;
        var col = idx >= 0 ? args!.Column(idx) : null;
        var names = col switch
        {
            StringArray s when !s.IsNull(0) =>
                s.GetString(0).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Int32Array i when !i.IsNull(0) && i.Values[0] is > 0 and <= 99 =>
                Enumerable.Range(1, i.Values[0]).Select(k => "c" + k).ToArray(),
            Int64Array l when !l.IsNull(0) && l.Values[0] is > 0 and <= 99 =>
                Enumerable.Range(1, (int)l.Values[0]).Select(k => "c" + k).ToArray(),
            ListArray list when !list.IsNull(0) && list.GetSlicedValues(0) is StringArray items =>
                Enumerable.Range(0, items.Length).Select(k => items.GetString(k)).ToArray(),
            // A STRUCT works as a structured config: its FIELD NAMES become the columns — which is also the
            // end-to-end proof that a captured struct arrives with its fields intact, not merely as a type.
            StructArray st when !st.IsNull(0) =>
                ((StructType)st.Data.DataType).Fields.Select(f => f.Name).ToArray(),
            _ => throw new NotSupportedException(
                "fabricator_lat_fields: `fields` must be a VARCHAR of comma-separated column names, an integer "
                + "count between 1 and 99, a LIST of names, or a STRUCT (its field names become the columns) "
                + $"— got {col?.Data.DataType.Name ?? "nothing"}."),
        };
        if (names.Length == 0)
        {
            throw new NotSupportedException("fabricator_lat_fields: `fields` names no columns.");
        }
        return new Binding(names);
    }

    private sealed class Binding : ILateralFunctionBinding
    {
        private readonly string[] _names;

        public Binding(string[] names) => _names = names;

        public Schema OutputSchema =>
            new(_names.Select(n => new Field(n, Int64Type.Default, nullable: true)).ToArray(), metadata: null);

        public ILateralSession Open() => new Session(OutputSchema, _names.Length);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;
        private readonly int _cols;

        public Session(Schema output, int cols)
        {
            _output = output;
            _cols = cols;
        }

        public LateralResult Call(RecordBatch input)
        {
            // ONE per-row input column: the `fields` slot never reaches the rows (the host strips it).
            var n = (Int64Array)input.Column(0);
            var builders = new Int64Array.Builder[_cols];
            for (int c = 0; c < _cols; c++) { builders[c] = new Int64Array.Builder().Reserve(input.Length); }
            for (int r = 0; r < input.Length; r++)
            {
                for (int c = 0; c < _cols; c++)
                {
                    if (n.IsNull(r)) { builders[c].AppendNull(); } else { builders[c].Append(n.Values[r] * (c + 1)); }
                }
            }
            return new LateralResult(new RecordBatch(
                _output, builders.Select(b => (IArrowArray)b.Build()).ToArray(), input.Length));
        }

        public void Dispose() { }
    }
}

internal sealed class GfLatScaleFunction : ILateralFunction
{
    public string Name => "fabricator_lat_scale";

    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Named("factor", Int32Type.Default),
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
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

    private sealed class Binding : ILateralFunctionBinding
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
internal sealed class GfLatBadOriginFunction : ILateralFunction
{
    public string Name => "fabricator_lat_badorigin";

    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int32Type.Default),
        Params.Positional("mode", StringType.Default),
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralFunctionBinding
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

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralFunctionBinding
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

// GLOBAL scalar (connection-free, no ATTACH): fabricator_parse(text, type_name) -> `text` parsed as the named
// type. The RESULT TYPE follows the constant `type_name`, resolved per call site in Bind — so
// `fabricator_parse('42','bigint')` is a BIGINT expression (arithmetic works on it) while
// `fabricator_parse('42','double')` is a DOUBLE, from ONE registered function. This is the shipped
// demonstration of the ABI v80 scalar bind session; a build that fixed the return type at registration
// cannot make those two calls differ.
//
// It also shows the two rules a bind-resolving provider must respect: the type argument is only readable
// when it is CONSTANT (a scalar may be called as f(t.col), unlike a table function), and the parsed type is
// resolved ONCE per call site rather than per chunk.
internal sealed class CfParseFunction : IScalarFunction
{
    public string Name => "fabricator_parse";

    public Schema Parameters => new(new[]
    {
        new Field("text", StringType.Default, nullable: true),
        new Field("type_name", StringType.Default, nullable: true),
    }, metadata: null);

    // No fixed return type: it is whatever `type_name` names, so the host registers this function as ANY and
    // requires Bind to resolve a type per call site.
    public Field? Result => null;

    // Never called: Bind always returns a CfParseBinding, which carries the resolved type. The interface still
    // demands it (a fixed-return function implements only Invoke), so it fails loudly rather than silently
    // computing something untyped.
    public IArrowArray Invoke(RecordBatch args) =>
        throw new InvalidOperationException(
            "fabricator_parse must be executed through its binding (its result type is resolved at bind)");

    public IScalarFunctionBinding Bind(ScalarBindArgs args)
    {
        // ⚠ IsConstant, not just "is there a value": a non-constant slot carries a NULL PLACEHOLDER that is
        // indistinguishable from an explicit NULL literal by looking at the value. Refusing by name here is
        // the honest answer — the return type is part of the PLAN, so it cannot depend on row data.
        if (!args.IsConstant(1))
        {
            throw new InvalidOperationException(
                "fabricator_parse: the type_name argument must be a constant (the result type is resolved at " +
                "bind, so it cannot depend on row values)");
        }
        var name = (args.ConstantArray(1) as StringArray)?.GetString(0);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("fabricator_parse: type_name must not be NULL or empty");
        }
        IArrowType type = name.Trim().ToLowerInvariant() switch
        {
            "bigint" or "int64" => Int64Type.Default,
            "double" or "float8" => DoubleType.Default,
            "varchar" or "text" => StringType.Default,
            _ => throw new InvalidOperationException(
                $"fabricator_parse: unsupported type_name '{name}' (expected bigint, double or varchar)"),
        };
        return new CfParseBinding(type);
    }
}

// The per-call-site binding: it holds the resolved result type, which is exactly the kind of once-per-call-site
// state the stateless execute_scalar had nowhere to put.
internal sealed class CfParseBinding : IScalarFunctionBinding
{
    private readonly IArrowType _type;

    public CfParseBinding(IArrowType type)
    {
        _type = type;
        Result = new Field("result", type, nullable: true);
    }

    // A CONCRETE field, not null: this call's result genuinely differs from the (absent) declaration.
    public Field? Result { get; }

    public IArrowArray Invoke(RecordBatch args)
    {
        var text = (StringArray)args.Column(0);
        // ⚠ Column 1 (type_name) is present and fully materialised here even though the binding already knows
        // it — see IScalarFunctionBinding.Invoke: which arguments are constant is a call-site property, so the
        // batch always carries every parameter in declared order.
        if (_type == Int64Type.Default)
        {
            var b = new Int64Array.Builder().Reserve(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                var v = text.GetString(i);
                if (v is not null && long.TryParse(v, System.Globalization.NumberStyles.Integer,
                                                   System.Globalization.CultureInfo.InvariantCulture, out var l))
                {
                    b.Append(l);
                }
                else
                {
                    b.AppendNull();
                }
            }
            return b.Build();
        }
        if (_type == DoubleType.Default)
        {
            var b = new DoubleArray.Builder().Reserve(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                var v = text.GetString(i);
                if (v is not null && double.TryParse(v, System.Globalization.NumberStyles.Float,
                                                     System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    b.Append(d);
                }
                else
                {
                    b.AppendNull();
                }
            }
            return b.Build();
        }
        var sb = new StringArray.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            sb.Append(text.GetString(i));
        }
        return sb.Build();
    }

    public void Dispose()
    {
    }
}

// =============================================================================
// VARIADIC (varargs) demonstrations — one per kind that supports a variadic tail.
//
// A tail is declared with `Params.VarArgs(name[, type])`: the fields BEFORE it are the function's MINIMUM
// arity, and DuckDB then accepts any number of further arguments, each implicitly cast to the tail's type
// (the ANY overload => no cast, so each argument keeps its own runtime type). The args batch of a variadic
// call is WIDER than `Parameters` — the tail columns follow the prefix in call order, named `<tail>_0`,
// `<tail>_1`, … — so an implementation reads the COUNT from the batch, never from its own declaration.
//
// ⚠ A LIST parameter already covers the homogeneous case (`f(['a','b'])`) and needs none of this machinery.
// What a tail buys is HETEROGENEOUS, individually-typed arguments, which is why every one of these demos
// mixes types at the call site.
// =============================================================================

/// <summary>
/// Renders one bound argument for display. ⚠ INVARIANT culture, deliberately: the default
/// <c>object.ToString()</c> formats dates and numbers per the machine's locale, so a gate asserting the
/// output would pass here and fail on a runner set to another culture — the kind of environment dependency
/// this tree treats as a defect rather than a quirk.
/// </summary>
internal static class VaRender
{
    internal static string? Text(object? v) => v switch
    {
        null => null,
        string s => s,
        System.IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}

/// <summary>
/// <c>fabricator_va_concat(sep, …)</c> — the SCALAR tail: renders every argument after the separator and
/// joins them. Min arity 1; <c>ANY</c> tail, so <c>fabricator_va_concat('-', 1, 'x', DATE '2020-01-01')</c>
/// works and each value arrives as its own Arrow type.
/// </summary>
internal sealed class GfVaConcatFunction : IScalarFunction
{
    public string Name => "fabricator_va_concat";

    public Schema Parameters => new(new[]
    {
        Params.Positional("sep", StringType.Default),
        // No type => the ANY tail. DuckDB inserts no cast for ANY, so a heterogeneous call arrives verbatim.
        Params.VarArgs("value"),
    }, metadata: null);

    public Field Result => new("result", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var sep = (StringArray)args.Column(0);
        var b = new StringArray.Builder().Reserve(args.Length);
        for (int row = 0; row < args.Length; row++)
        {
            var parts = new List<string>();
            // The COUNT comes from the batch: the declaration says only "a tail follows".
            for (int c = 1; c < args.ColumnCount; c++)
            {
                parts.Add(VaRender.Text(ArrowValueReader.ReadScalar(args.Column(c), row)) ?? "NULL");
            }
            b.Append(string.Join(sep.IsNull(row) ? string.Empty : sep.GetString(row), parts));
        }
        return b.Build();
    }
}

/// <summary>
/// <c>fabricator_va_args(label, …)</c> — the TABLE tail: one output row per tail argument, reporting its
/// ordinal, the column NAME the host gave it, its Arrow type and its rendered value. The shipped proof that
/// a variadic call's args batch reaches a table function intact.
/// </summary>
/// <remarks>
/// It keeps a fixed prefix deliberately: a table function whose tail is its ONLY parameter has minimum arity
/// 0, and a zero-argument call crosses with NO args stream at all (a zero-field Arrow schema cannot be
/// represented), which is a different code path from the one being demonstrated here.
/// </remarks>
internal sealed class GfVaArgsFunction : ITableFunction
{
    public string Name => "fabricator_va_args";

    public Schema Parameters => new(new[]
    {
        Params.Positional("label", StringType.Default),
        Params.VarArgs("arg"),
        // ⚠ A NAMED parameter AFTER the tail — legal, and the ordering under test. The tail must be the last
        // POSITIONAL parameter; named parameters are a separate namespace, so they may follow. The marshal
        // has to interleave them correctly: the tail consumes every remaining POSITIONAL value, and the
        // named one is then matched BY NAME rather than by the position it would otherwise have had.
        Params.Named("note", StringType.Default),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var label = args.ColumnCount > 0 && args.Column(0) is StringArray l && !l.IsNull(0)
            ? l.GetString(0)
            : string.Empty;
        var rows = new List<(int Ordinal, string Name, string Type, string? Value)>();
        string? note = null;
        for (int c = 1; c < args.ColumnCount; c++)
        {
            var col = args.Column(c);
            var name = args.Schema.FieldsList[c].Name;
            // The NAMED parameter arrives under its own name; the tail columns under `<tail>_<k>`. Reading
            // by name is what keeps the two apart without counting positions.
            if (string.Equals(name, "note", System.StringComparison.OrdinalIgnoreCase))
            {
                note = VaRender.Text(ArrowValueReader.ReadScalar(col, 0));
                continue;
            }
            rows.Add((rows.Count, name, col.Data.DataType.Name,
                      VaRender.Text(ArrowValueReader.ReadScalar(col, 0))));
        }
        return new Binding(note is null ? label : label + "/" + note, rows);
    }

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly string _label;
        private readonly List<(int Ordinal, string Name, string Type, string? Value)> _rows;

        public Binding(string label, List<(int Ordinal, string Name, string Type, string? Value)> rows)
        {
            _label = label;
            _rows = rows;
        }

        public Schema OutputSchema => new(new[]
        {
            new Field("label", StringType.Default, nullable: false),
            new Field("ordinal", Int32Type.Default, nullable: false),
            new Field("name", StringType.Default, nullable: false),
            new Field("type", StringType.Default, nullable: false),
            new Field("value", StringType.Default, nullable: true),
        }, metadata: null);

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
            var label = new StringArray.Builder().Reserve(_rows.Count);
            var ordinal = new Int32Array.Builder().Reserve(_rows.Count);
            var name = new StringArray.Builder().Reserve(_rows.Count);
            var type = new StringArray.Builder().Reserve(_rows.Count);
            var value = new StringArray.Builder().Reserve(_rows.Count);
            foreach (var r in _rows)
            {
                label.Append(_label);
                ordinal.Append(r.Ordinal);
                name.Append(r.Name);
                type.Append(r.Type);
                if (r.Value is null) { value.AppendNull(); } else { value.Append(r.Value); }
            }
            yield return new RecordBatch(
                OutputSchema,
                new IArrowArray[] { label.Build(), ordinal.Build(), name.Build(), type.Build(), value.Build() },
                _rows.Count);
        }

        public void Dispose() { }
    }
}

/// <summary>
/// <c>db.dbo.cf_va_sum(…)</c> — the CATALOG-BOUND variadic scalar: sums any number of arguments, NULLs
/// skipped, and reports 0 for a call with none. Its whole job is to exercise the ATTACH-time registration
/// path (<c>GetOrCreateScalarFunction</c>), which is a different site from the load-time global one.
/// </summary>
/// <remarks>
/// A CONCRETE tail type (<c>BIGINT</c>) rather than the ANY used by the global demos, which is the other
/// half of the mechanism: DuckDB applies its ordinary IMPLICIT-CAST rules per tail argument, so
/// <c>cf_va_sum(1, 2::SMALLINT, 3::TINYINT)</c> arrives as three <c>int64</c> columns.
/// <para>⚠ MEASURED, and it corrects the obvious reading: a concrete tail is not "anything, coerced". A cast
/// DuckDB will not make implicitly is refused at BIND, exactly as for a declared parameter —
/// <c>cf_va_sum(1, 2::SMALLINT, 3.0)</c> fails with <i>"No function matches … cf_va_sum(INTEGER_LITERAL,
/// SMALLINT, DECIMAL(2,1))"</i> because DECIMAL→BIGINT is lossy. Declare an ANY tail when the point is to
/// accept anything.</para>
/// </remarks>
internal sealed class CfVaSumFunction : ICatalogScalarFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_va_sum";

    public Schema Parameters => new(new[] { Params.VarArgs("n", Int64Type.Default) }, metadata: null);

    public Field Result => new("result", Int64Type.Default, nullable: false);

    public IArrowArray Invoke(RecordBatch args)
    {
        var b = new Int64Array.Builder().Reserve(args.Length);
        for (int row = 0; row < args.Length; row++)
        {
            long sum = 0;
            for (int c = 0; c < args.ColumnCount; c++)
            {
                if (args.Column(c) is Int64Array v && !v.IsNull(row))
                {
                    sum += v.Values[row];
                }
            }
            b.Append(sum);
        }
        return b.Build();
    }
}

/// <summary>
/// <c>fabricator_va_values(…)</c> — the SQL-GENERATING tail: emits a one-row SELECT whose COLUMN COUNT is the
/// number of arguments (<c>SELECT 1 AS v0, 'x' AS v1</c>). The shape sqlgen exists for — the SQL TEXT, not
/// just the values, depends on the call.
/// </summary>
internal sealed class GfVaValuesFunction : ISqlTableFunction
{
    public string Name => "fabricator_va_values";

    public Schema Parameters => new(new[] { Params.VarArgs("v") }, metadata: null);

    public string GenerateSql(RecordBatch args)
    {
        // ⚠ `args` can be an EMPTY batch, not just a short one: a variadic generator's minimum arity is its
        // (here empty) prefix, so `fabricator_va_values()` binds and the host hands the crossing no args
        // stream at all. The generator's own rule is what refuses it.
        if (args is null || args.ColumnCount == 0)
        {
            throw new ArgumentException("fabricator_va_values: pass at least one value");
        }
        var cols = new List<string>();
        for (int c = 0; c < args.ColumnCount; c++)
        {
            cols.Add(Literal(ArrowValueReader.ReadScalar(args.Column(c), 0)) + " AS v" + c);
        }
        return "SELECT " + string.Join(", ", cols);
    }

    /// <summary>
    /// Renders one bound argument as a SQL literal. ⚠ An ALLOW-LIST, not an escaper: this text is parsed as
    /// SQL, so a type whose rendering is not provably safe is refused by name rather than interpolated.
    /// </summary>
    private static string Literal(object? v) => v switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        sbyte or byte or short or ushort or int or uint or long or ulong => v.ToString()!,
        float or double or decimal => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!,
        string s => "'" + s.Replace("'", "''") + "'",
        _ => throw new ArgumentException(
            "fabricator_va_values: cannot render a " + v.GetType().Name + " as a SQL literal — pass a number, "
            + "a boolean, a string or NULL"),
    };
}

// GLOBAL lateral whose bind-time CONSTANT is the FIRST parameter, not the last:
// fabricator_lat_front(fields, n) -> one INT64 column per name in `fields`, column k = n * (k+1), 1:1.
//
// ⚠ It exists to settle a claim this tree carried WITHOUT CHECKING IT. The design note said a lateral's
// constant slots "are also trailing", and used that to argue a variadic tail could not compose with them.
// Nothing in the code says trailing: `LateralBind` builds `declared_constant` in slot order and tests
// `declared_constant[i]` per ACTUAL slot, `wire_slots` is simply the non-constant indices,
// `LateralConstants.Validate` matches by NAME, and `Params.Validate` gives Constant no ordering rule at all.
// The convention was the demos', not the mechanism's — and once constants may sit at the FRONT, the layout
// [constants][positionals][tail] leaves a variadic tail nothing to contend with.
internal sealed class GfLatFrontFunction : ILateralFunction
{
    public string Name => "fabricator_lat_front";

    public Schema Parameters => new(new[]
    {
        Params.Constant("fields"), // FIRST — the point of this demo
        Params.Positional("n", Int64Type.Default),
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        // By NAME, not by position — which is what makes the declaration order a free choice.
        int idx = args?.Schema.GetFieldIndex("fields") ?? -1;
        var col = idx >= 0 ? args!.Column(idx) : null;
        if (col is not StringArray s || s.IsNull(0))
        {
            throw new ArgumentException(
                "fabricator_lat_front: 'fields' must be a non-NULL VARCHAR of comma-separated column names");
        }
        var names = s.GetString(0).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            throw new ArgumentException("fabricator_lat_front: 'fields' named no columns");
        }
        return new Binding(names);
    }

    private sealed class Binding : ILateralFunctionBinding
    {
        private readonly string[] _names;

        public Binding(string[] names) => _names = names;

        public Schema OutputSchema =>
            new(_names.Select(n => new Field(n, Int64Type.Default, nullable: true)).ToList(), metadata: null);

        public ILateralSession Open() => new Session(OutputSchema, _names.Length);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;
        private readonly int _cols;

        public Session(Schema output, int cols)
        {
            _output = output;
            _cols = cols;
        }

        public LateralResult Call(RecordBatch input)
        {
            // The constant is NOT on the wire, so column 0 is `n` even though it is declared SECOND — the
            // assertion that the slot was stripped by index rather than by position-from-the-end.
            var n = (Int64Array)input.Column(0);
            var cols = new IArrowArray[_cols];
            for (int c = 0; c < _cols; c++)
            {
                var b = new Int64Array.Builder().Reserve(input.Length);
                for (int r = 0; r < input.Length; r++)
                {
                    if (n.IsNull(r)) { b.AppendNull(); } else { b.Append(n.Values[r] * (c + 1)); }
                }
                cols[c] = b.Build();
            }
            return new LateralResult(new RecordBatch(_output, cols, input.Length)); // 1:1, no provenance
        }

        public void Dispose() { }
    }
}

// GLOBAL lateral combining BOTH ends of the parameter protocol:
// fabricator_lat_span(fields, n, ...extra) -> one INT64 column per name in `fields` (column k = n * (k+1))
// plus `tail_sum` = the sum of the tail arguments for that row. 1:1 with the input rows.
//
// The layout is [CONSTANT][POSITIONAL][VARIADIC TAIL], which is the whole point: a lateral's positional
// slots ARE its per-row input columns, so the tail is a variable-width WIRE rather than a wider args batch.
// That works because LateralBind is written against the ACTUAL call — `arg_width` is the call's width and
// every declaration lookup is guarded — and because CONSTANT slots are stripped BY INDEX, so a front
// constant leaves the tail nothing to contend with.
//
// ⚠ The tail is CONCRETE (BIGINT) rather than ANY, deliberately: that is the case where the host must
// normalize the slots PAST the declaration to the tail's declared type (an ANY tail needs no normalization,
// so it would not exercise it). It also means DuckDB applies its ordinary implicit-cast rules per tail
// argument — `2::SMALLINT` is taken, a DECIMAL is refused at bind.
internal sealed class GfLatSpanFunction : ILateralFunction
{
    public string Name => "fabricator_lat_span";

    public Schema Parameters => new(new[]
    {
        Params.Constant("fields"),                       // bind-time, FIRST
        Params.Positional("n", Int64Type.Default),       // per-row
        Params.VarArgs("extra", Int64Type.Default),      // per-row, any number, LAST
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        int idx = args?.Schema.GetFieldIndex("fields") ?? -1;
        if ((idx >= 0 ? args!.Column(idx) : null) is not StringArray s || s.IsNull(0))
        {
            throw new ArgumentException(
                "fabricator_lat_span: 'fields' must be a non-NULL VARCHAR of comma-separated column names");
        }
        var names = s.GetString(0).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            throw new ArgumentException("fabricator_lat_span: 'fields' named no columns");
        }
        // The tail's WIDTH is known here from the input schema, not from the declaration — `inputSchema` is
        // the wire (the constant already stripped), so column 0 is `n` and the rest are the tail.
        return new Binding(names, System.Math.Max(0, inputSchema.FieldsList.Count - 1));
    }

    private sealed class Binding : ILateralFunctionBinding
    {
        private readonly string[] _names;
        private readonly int _tailWidth;

        public Binding(string[] names, int tailWidth)
        {
            _names = names;
            _tailWidth = tailWidth;
        }

        public Schema OutputSchema
        {
            get
            {
                var fields = _names.Select(n => new Field(n, Int64Type.Default, nullable: true)).ToList();
                fields.Add(new Field("tail_sum", Int64Type.Default, nullable: false));
                return new Schema(fields, metadata: null);
            }
        }

        public ILateralSession Open() => new Session(OutputSchema, _names.Length, _tailWidth);

        public void Dispose() { }
    }

    private sealed class Session : ILateralSession
    {
        private readonly Schema _output;
        private readonly int _cols;
        private readonly int _tailWidth;

        public Session(Schema output, int cols, int tailWidth)
        {
            _output = output;
            _cols = cols;
            _tailWidth = tailWidth;
        }

        public LateralResult Call(RecordBatch input)
        {
            var n = (Int64Array)input.Column(0);
            var arrays = new IArrowArray[_cols + 1];
            for (int c = 0; c < _cols; c++)
            {
                var b = new Int64Array.Builder().Reserve(input.Length);
                for (int r = 0; r < input.Length; r++)
                {
                    if (n.IsNull(r)) { b.AppendNull(); } else { b.Append(n.Values[r] * (c + 1)); }
                }
                arrays[c] = b.Build();
            }
            var sum = new Int64Array.Builder().Reserve(input.Length);
            for (int r = 0; r < input.Length; r++)
            {
                long acc = 0;
                // 1 .. ColumnCount-1 are the tail columns. Read the count from the BATCH, never from the
                // declaration — that is the whole contract of a variadic tail.
                for (int c = 1; c < input.ColumnCount; c++)
                {
                    if (input.Column(c) is Int64Array v && !v.IsNull(r))
                    {
                        acc += v.Values[r];
                    }
                }
                sum.Append(acc);
            }
            arrays[_cols] = sum.Build();
            return new LateralResult(new RecordBatch(_output, arrays, input.Length)); // 1:1, no provenance
        }

        public void Dispose() { }
    }
}

// GLOBAL table-in-out with a VARIADIC TAIL of BIND-TIME cost arguments:
// fabricator_inout_va(<table of n>, label, ...extra) -> (n, tag) where tag is `label` followed by the tail
// values joined with '+'.
//
// ⚠ THE POINT IS WHICH SIDE THE TAIL LANDS ON. An in-out's per-row input is its {TABLE} argument ALONE; its
// positional and named parameters are CONSTANTS resolved at bind and marshaled into the 1-row args batch the
// author reads in `Bind`. So a tail here widens the ARGS BATCH — the scalar/table/sqlgen mechanism — and the
// input STREAM is untouched. The two cannot mix: the subquery slot binds to a BoundStatement while the tail
// arguments are constants in `input.inputs`.
//
// (A LATERAL tail is the other mechanism — there the positional slots really ARE the per-row input columns,
// so its tail widens the WIRE. Same declaration, different half of the call.)
internal sealed class GfInOutVaFunction : IInOutFunction
{
    public string Name => "fabricator_inout_va";

    public Schema Parameters => new(new[]
    {
        Params.TableInput("input", new Field("n", Int32Type.Default, nullable: true)),
        Params.Positional("label", StringType.Default),
        Params.VarArgs("extra"), // ANY tail: heterogeneous bind-time arguments
        Params.Named("note", StringType.Default), // NAMED after the tail — a separate namespace, so legal
    }, metadata: null);

    public IInOutFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        // Read the tail from the BATCH, by the host's `<tail>_<k>` naming — the count is a property of the
        // call, not of the declaration.
        string label = "";
        string? note = null;
        var extras = new List<string>();
        for (int c = 0; args is not null && c < args.ColumnCount; c++)
        {
            var name = args.Schema.FieldsList[c].Name;
            var v = ArrowValueReader.ReadScalar(args.Column(c), 0);
            if (string.Equals(name, "label", System.StringComparison.OrdinalIgnoreCase))
            {
                label = VaRender.Text(v) ?? "";
            }
            else if (string.Equals(name, "note", System.StringComparison.OrdinalIgnoreCase))
            {
                note = VaRender.Text(v);
            }
            else if (name.StartsWith("extra_", System.StringComparison.Ordinal))
            {
                extras.Add(VaRender.Text(v) ?? "NULL");
            }
        }
        var tag = extras.Count == 0 ? label : label + ":" + string.Join("+", extras);
        return new Binding(note is null ? tag : tag + "/" + note);
    }

    private sealed class Binding : IInOutFunctionBinding
    {
        private readonly string _tag;

        public Binding(string tag) => _tag = tag;

        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int64Type.Default, nullable: true),
            new Field("tag", StringType.Default, nullable: false),
        }, metadata: null);

        public async IAsyncEnumerable<RecordBatch> DoExchange(
            IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in input.WithCancellation(ct))
            {
                using (chunk)
                {
                    // ⚠ Read the input column GENERICALLY. A TableInput's declared columns are recorded,
                    // not enforced (see Params.TableInput), so the subquery may hand over any type DuckDB
                    // produced -- `range(2)` is BIGINT where the declaration says INTEGER. A hard cast here
                    // is the shape that makes an in-out demo look broken on the most obvious call a user
                    // writes.
                    var col = chunk.Column(0);
                    int rows = chunk.Length;
                    var nb = new Int64Array.Builder().Reserve(rows);
                    var tb = new StringArray.Builder().Reserve(rows);
                    for (int i = 0; i < rows; i++)
                    {
                        var v = ArrowValueReader.ReadScalar(col, i);
                        if (v is null) { nb.AppendNull(); } else { nb.Append(System.Convert.ToInt64(v)); }
                        tb.Append(_tag);
                    }
                    yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), tb.Build() }, rows);
                }
                yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel
            }
        }

        public void Dispose() { }
    }
}

// =============================================================================
// VARIADIC tails on the ATTACH-TIME (catalog-bound) registration sites.
//
// The four demos below exist for coverage, not capability: every one mirrors a load-time global that is
// already gated, and they run through the SAME helpers. What they exercise is the OTHER registration site.
// Each catalog path has its own plumbing to fetch parameter styles and hand the tail's position to its
// marshal, so each is an independent chance to have made the mistake the sqlgen path actually made — there
// the info stored a FILTERED declaration, so the varargs index did not correspond to the names it indexed,
// and it surfaced three layers away as `ArgumentNullException: 'fields'`. A static audit cannot see that.
//
// With these, all five catalog kinds that take a tail are covered: scalar (cf_va_sum), table, sqlgen,
// table-in-out and lateral.
// =============================================================================

/// <summary><c>db.dbo.cf_va_rows(label, …)</c> — the catalog TABLE path: one row per tail argument.</summary>
internal sealed class CfVaRowsFunction : ICatalogTableFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_va_rows";

    public Schema Parameters => new(new[]
    {
        Params.Positional("label", StringType.Default),
        Params.VarArgs("arg"),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var label = args.ColumnCount > 0 && args.Column(0) is StringArray l && !l.IsNull(0)
            ? l.GetString(0)
            : string.Empty;
        var rows = new List<(string Name, string? Value)>();
        for (int c = 1; c < args.ColumnCount; c++)
        {
            rows.Add((args.Schema.FieldsList[c].Name,
                      VaRender.Text(ArrowValueReader.ReadScalar(args.Column(c), 0))));
        }
        return new Binding(label, rows);
    }

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly string _label;
        private readonly List<(string Name, string? Value)> _rows;

        public Binding(string label, List<(string Name, string? Value)> rows)
        {
            _label = label;
            _rows = rows;
        }

        public Schema OutputSchema => new(new[]
        {
            new Field("label", StringType.Default, nullable: false),
            new Field("name", StringType.Default, nullable: false),
            new Field("value", StringType.Default, nullable: true),
        }, metadata: null);

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose(); // eager dispose, then delegate — see StaticTableFunction.Execute
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var label = new StringArray.Builder().Reserve(_rows.Count);
            var name = new StringArray.Builder().Reserve(_rows.Count);
            var value = new StringArray.Builder().Reserve(_rows.Count);
            foreach (var r in _rows)
            {
                label.Append(_label);
                name.Append(r.Name);
                if (r.Value is null) { value.AppendNull(); } else { value.Append(r.Value); }
            }
            yield return new RecordBatch(OutputSchema,
                                         new IArrowArray[] { label.Build(), name.Build(), value.Build() },
                                         _rows.Count);
        }

        public void Dispose() { }
    }
}

/// <summary><c>db.dbo.cf_va_select(…)</c> — the catalog SQLGEN path: the generated SQL's column count is the
/// argument count. ⚠ The catalog generator's `GenerateSql` also receives the ATTACH alias; this one does not
/// need it, which keeps the demo about the tail.</summary>
internal sealed class CfVaSelectFunction : ICatalogSqlTableFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_va_select";

    public Schema Parameters => new(new[] { Params.VarArgs("v", Int64Type.Default) }, metadata: null);

    public string GenerateSql(SqlGenContext ctx, RecordBatch args)
    {
        if (args is null || args.ColumnCount == 0)
        {
            throw new ArgumentException("cf_va_select: pass at least one value");
        }
        var cols = new List<string>();
        for (int c = 0; c < args.ColumnCount; c++)
        {
            // A CONCRETE BIGINT tail, so every value is an int64 by the time it arrives — no literal
            // rendering allow-list is needed here (the global fabricator_va_values covers that question).
            var v = ArrowValueReader.ReadScalar(args.Column(c), 0);
            cols.Add((v is null ? "NULL" : System.Convert.ToInt64(v).ToString(
                          System.Globalization.CultureInfo.InvariantCulture)) + " AS v" + c);
        }
        return "SELECT " + string.Join(", ", cols);
    }
}

/// <summary><c>db.dbo.cf_va_span(n, …extra)</c> — the catalog LATERAL path: the tail is per-row WIRE data
/// here, not bind-time arguments. 1:1 with the input rows.</summary>
internal sealed class CfVaSpanFunction : ICatalogLateralFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_va_span";

    public Schema Parameters => new(new[]
    {
        Params.Positional("n", Int64Type.Default),
        Params.VarArgs("extra", Int64Type.Default),
    }, metadata: null);

    public ILateralFunctionBinding Bind(RecordBatch? args, Schema inputSchema) => new Binding();

    private sealed class Binding : ILateralFunctionBinding
    {
        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int64Type.Default, nullable: true),
            new Field("tail_sum", Int64Type.Default, nullable: false),
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
            var n = (Int64Array)input.Column(0);
            var nb = new Int64Array.Builder().Reserve(input.Length);
            var sb = new Int64Array.Builder().Reserve(input.Length);
            for (int r = 0; r < input.Length; r++)
            {
                if (n.IsNull(r)) { nb.AppendNull(); } else { nb.Append(n.Values[r]); }
                long acc = 0;
                // Columns 1.. are the tail — its width is a property of the CALL, read from the batch.
                for (int c = 1; c < input.ColumnCount; c++)
                {
                    if (input.Column(c) is Int64Array v && !v.IsNull(r)) { acc += v.Values[r]; }
                }
                sb.Append(acc);
            }
            return new LateralResult(new RecordBatch(_output, new IArrowArray[] { nb.Build(), sb.Build() },
                                                     input.Length)); // 1:1, no provenance
        }

        public void Dispose() { }
    }
}

/// <summary><c>db.dbo.cf_va_tag(&lt;table&gt;, label, …)</c> — the catalog TABLE-IN-OUT path. Implements
/// <see cref="ICatalogInOutFunction"/> directly rather than the <c>StaticInOutFunction</c> base, because that
/// base composes <c>Parameters</c> itself (table input ++ named) and so cannot carry a tail.</summary>
internal sealed class CfVaTagFunction : ICatalogInOutFunction
{
    public string SchemaName => "dbo";
    public string Name => "cf_va_tag";

    public Schema Parameters => new(new[]
    {
        Params.TableInput("input", new Field("n", Int32Type.Default, nullable: true)),
        Params.Positional("label", StringType.Default),
        Params.VarArgs("extra"),
    }, metadata: null);

    public IInOutFunctionBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        string label = "";
        var extras = new List<string>();
        for (int c = 0; args is not null && c < args.ColumnCount; c++)
        {
            var name = args.Schema.FieldsList[c].Name;
            var v = VaRender.Text(ArrowValueReader.ReadScalar(args.Column(c), 0));
            if (string.Equals(name, "label", System.StringComparison.OrdinalIgnoreCase)) { label = v ?? ""; }
            else if (name.StartsWith("extra_", System.StringComparison.Ordinal)) { extras.Add(v ?? "NULL"); }
        }
        return new Binding(extras.Count == 0 ? label : label + ":" + string.Join("+", extras));
    }

    private sealed class Binding : IInOutFunctionBinding
    {
        private readonly string _tag;

        public Binding(string tag) => _tag = tag;

        public Schema OutputSchema => new(new[]
        {
            new Field("n", Int64Type.Default, nullable: true),
            new Field("tag", StringType.Default, nullable: false),
        }, metadata: null);

        public async IAsyncEnumerable<RecordBatch> DoExchange(
            IAsyncEnumerable<RecordBatch> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in input.WithCancellation(ct))
            {
                using (chunk)
                {
                    // Generic read: a TableInput's declared columns are recorded, not enforced.
                    var col = chunk.Column(0);
                    int rows = chunk.Length;
                    var nb = new Int64Array.Builder().Reserve(rows);
                    var tb = new StringArray.Builder().Reserve(rows);
                    for (int i = 0; i < rows; i++)
                    {
                        var v = ArrowValueReader.ReadScalar(col, i);
                        if (v is null) { nb.AppendNull(); } else { nb.Append(System.Convert.ToInt64(v)); }
                        tb.Append(_tag);
                    }
                    yield return new RecordBatch(OutputSchema, new IArrowArray[] { nb.Build(), tb.Build() }, rows);
                }
                yield return InOutExchange.EmptyBatch(OutputSchema); // per-input sentinel
            }
        }

        public void Dispose() { }
    }
}
