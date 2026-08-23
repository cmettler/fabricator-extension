using System;
using System.Collections.Generic;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// A provider-agnostic holder + dispatcher for CATALOG-BOUND custom (C#-authored) functions, across all six
/// kinds the host can register: scalar, table, SQL-generating table, table-in-out, collector and aggregate.
///
/// <para>Every backend that hosts catalog functions has to answer the same ABI calls by looking a
/// <c>"schema.name"</c> key up in a dictionary and delegating to Bridge helpers. The SQL-Server catalog used to
/// do that inline in six separate dictionaries, interleaved with its discovered-routine fallbacks (which ARE
/// genuinely SqlServer-specific and stay there); Delta and DAX need only the lookup half. So it lives here once,
/// and the interleaving providers call into it rather than re-deriving it.</para>
///
/// <para><b>Why the KIND strings matter more than they look.</b> The host's registration switch silently ignores
/// a kind it does not know (see <see cref="FunctionsMetadata"/>), so a typo there does not fail — it makes a
/// function quietly not exist. Emitting every declaration from <see cref="Declarations"/> means those strings are
/// written once, and the <c>aggregate</c> vs <c>aggregate_spill</c> choice is made in one place too.</para>
///
/// <para><b>The <c>__all__</c> schema sentinel.</b> A Delta root's schema names are folder names, unknown until
/// ATTACH, so a static declaration cannot name them (the same problem <see cref="CatalogMacroDefinition"/> has).
/// A function declared in schema <c>__all__</c> is therefore advertised once per discovered schema, and resolves
/// in any of them. Lookup tries the exact schema first so a schema-specific declaration always wins over a
/// sentinel one of the same name.</para>
/// </summary>
public sealed class CatalogFunctionSet
{
    /// <summary>Declare a function in every discovered schema of the catalog.</summary>
    public const string AllSchemas = "__all__";

    private readonly Dictionary<string, ICatalogScalarFunction> _scalars;
    private readonly Dictionary<string, ICatalogTableFunction> _tables;
    private readonly Dictionary<string, ICatalogSqlTableFunction> _sqlTables;
    private readonly Dictionary<string, ICatalogInOutFunction> _inOut;
    private readonly Dictionary<string, ICatalogLateralFunction> _laterals;
    private readonly Dictionary<string, ICatalogCollectorTableFunction> _collectors;
    private readonly Dictionary<string, ICatalogAggregateFunction> _aggregates;

    public CatalogFunctionSet(
        IEnumerable<ICatalogScalarFunction>? scalars = null,
        IEnumerable<ICatalogTableFunction>? tables = null,
        IEnumerable<ICatalogSqlTableFunction>? sqlTables = null,
        IEnumerable<ICatalogInOutFunction>? inOut = null,
        IEnumerable<ICatalogLateralFunction>? laterals = null,
        IEnumerable<ICatalogCollectorTableFunction>? collectors = null,
        IEnumerable<ICatalogAggregateFunction>? aggregates = null)
    {
        _scalars = Index(scalars);
        _tables = Index(tables);
        _sqlTables = Index(sqlTables);
        _inOut = Index(inOut);
        _laterals = Index(laterals);
        _collectors = Index(collectors);
        _aggregates = Index(aggregates);
    }

    // One indexer for all six: every catalog-bound interface carries SchemaName + Name, but they share no
    // common base that declares both, so the key selectors are passed per kind by the callers below.
    private static Dictionary<string, T> Index<T>(IEnumerable<T>? items) where T : class
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items ?? Enumerable.Empty<T>())
        {
            map[Key(SchemaOf(item), NameOf(item))] = item;
        }
        return map;
    }

    private static string SchemaOf(object fn) => fn switch
    {
        ICatalogScalarFunction f => f.SchemaName,
        ICatalogTableFunction f => f.SchemaName,
        ICatalogSqlTableFunction f => f.SchemaName,
        ICatalogInOutFunction f => f.SchemaName,
        ICatalogLateralFunction f => f.SchemaName,
        ICatalogCollectorTableFunction f => f.SchemaName,
        ICatalogAggregateFunction f => f.SchemaName,
        _ => throw new ArgumentException($"not a catalog-bound function: {fn.GetType().Name}"),
    };

    private static string NameOf(object fn) => fn switch
    {
        ICatalogScalarFunction f => f.Name,
        ICatalogTableFunction f => f.Name,
        ICatalogSqlTableFunction f => f.Name,
        ICatalogInOutFunction f => f.Name,
        ICatalogLateralFunction f => f.Name,
        ICatalogCollectorTableFunction f => f.Name,
        ICatalogAggregateFunction f => f.Name,
        _ => throw new ArgumentException($"not a catalog-bound function: {fn.GetType().Name}"),
    };

    /// <summary>True when nothing is registered — lets a caller keep its old "no functions" behaviour cheaply.</summary>
    public bool IsEmpty =>
        _scalars.Count == 0 && _tables.Count == 0 && _sqlTables.Count == 0 && _inOut.Count == 0
        && _laterals.Count == 0 && _collectors.Count == 0 && _aggregates.Count == 0;

    private static string Key(string schema, string name) => $"{schema}.{name}";

    /// <summary>
    /// The kind-6 declaration rows for this set, with <see cref="AllSchemas"/> expanded across
    /// <paramref name="schemaNames"/> (evaluated lazily — a catalog that declares nothing must not pay for a
    /// schema enumeration, which on OneLake is a storage round-trip).
    /// </summary>
    public IEnumerable<FunctionsMetadata.Declaration> Declarations(Func<IReadOnlyList<string>> schemaNames)
    {
        if (IsEmpty)
        {
            return System.Array.Empty<FunctionsMetadata.Declaration>();
        }
        var rows = new List<FunctionsMetadata.Declaration>();
        IReadOnlyList<string>? schemas = null;
        void Emit(string declaredSchema, string name, string kind, int paramCount, string returnType)
        {
            if (!string.Equals(declaredSchema, AllSchemas, StringComparison.Ordinal))
            {
                rows.Add(new FunctionsMetadata.Declaration(declaredSchema, name, kind, paramCount, returnType));
                return;
            }
            schemas ??= schemaNames();
            foreach (var s in schemas)
            {
                rows.Add(new FunctionsMetadata.Declaration(s, name, kind, paramCount, returnType));
            }
        }
        foreach (var f in _scalars.Values)
        {
            Emit(f.SchemaName, f.Name, "scalar", f.Parameters.FieldsList.Count, f.Result.DataType.Name);
        }
        foreach (var f in _tables.Values)
        {
            Emit(f.SchemaName, f.Name, "table", Params.DeclaredCount(f.Parameters), "");
        }
        foreach (var f in _sqlTables.Values)
        {
            Emit(f.SchemaName, f.Name, "table_sql", Params.DeclaredCount(f.Parameters), "");
        }
        foreach (var f in _inOut.Values)
        {
            Emit(f.SchemaName, f.Name, "inout", Params.DeclaredCount(f.Parameters), "");
        }
        foreach (var f in _laterals.Values)
        {
            // DeclaredCount counts positional + named, which for a lateral function is exactly its argument
            // count: its positional parameters ARE the per-row input columns and each occupies an arg slot.
            Emit(f.SchemaName, f.Name, "lateral", Params.DeclaredCount(f.Parameters), "");
        }
        foreach (var f in _collectors.Values)
        {
            Emit(f.SchemaName, f.Name, "collector", Params.DeclaredCount(f.Parameters), "");
        }
        foreach (var f in _aggregates.Values)
        {
            // 'aggregate' (fast in-memory id-based) vs 'aggregate_spill' (state serialized into DuckDB's blob
            // so external GROUP BY can spill it) — the host picks the callback set from this kind.
            Emit(f.SchemaName, f.Name, f.SupportsSpill ? "aggregate_spill" : "aggregate",
                 f.Parameters.FieldsList.Count, f.Result.DataType.Name);
        }
        return rows;
    }

    private bool TryGet<T>(Dictionary<string, T> map, string schema, string func, out T fn) =>
        map.TryGetValue(Key(schema, func), out fn!) || map.TryGetValue(Key(AllSchemas, func), out fn!);

    public bool TryScalar(string schema, string func, out ICatalogScalarFunction fn) =>
        TryGet(_scalars, schema, func, out fn);

    public bool TryTable(string schema, string func, out ICatalogTableFunction fn) =>
        TryGet(_tables, schema, func, out fn);

    public bool TrySqlTable(string schema, string func, out ICatalogSqlTableFunction fn) =>
        TryGet(_sqlTables, schema, func, out fn);

    public bool TryInOut(string schema, string func, out ICatalogInOutFunction fn) =>
        TryGet(_inOut, schema, func, out fn);

    public bool TryLateral(string schema, string func, out ICatalogLateralFunction fn) =>
        TryGet(_laterals, schema, func, out fn);

    public bool TryCollector(string schema, string func, out ICatalogCollectorTableFunction fn) =>
        TryGet(_collectors, schema, func, out fn);

    public bool TryAggregate(string schema, string func, out ICatalogAggregateFunction fn) =>
        TryGet(_aggregates, schema, func, out fn);

    // ---- the ABI members, as one-liners a hosting catalog can forward to ------------------------------
    // Each returns null (or false) for a name this set does not hold, so a provider WITH discovered routines
    // can fall through to them and a provider without can throw. The kind order matches what SqlServerBackend
    // did inline, which only matters if one name were registered under two kinds — itself a bug.

    /// <summary>Argument schema for any kind that has one. Returns null when the name is not ours.</summary>
    public Schema? ParamSchema(string schema, string func)
    {
        if (TryScalar(schema, func, out var s)) { return s.Parameters; }
        // ONE schema per function, each field already carrying its style — nothing to combine here any more.
        if (TryTable(schema, func, out var t)) { return t.Parameters; }
        if (TryAggregate(schema, func, out var a)) { return a.Parameters; }
        if (TrySqlTable(schema, func, out var g)) { return g.Parameters; }
        // A lateral function MUST answer here: its positional parameters become the DuckDB argument types, so
        // without this the host resolves no signature and the declaration silently never becomes a callable
        // function (measured — `fabricator_functions()` listed it and `db.dbo.fn(...)` said "does not exist").
        //
        // ⚠ In-out and collector are NOT in this list, and that is pre-existing rather than deliberate:
        // GetOrCreateCustomInOutFunction catches the resulting failure and falls back to the bare {TABLE}
        // signature, which is right for every in-out shipped today (none declares a cost arg on a CATALOG) and
        // would silently drop one that did. Left alone — adding them here would change how every existing
        // in-out's signature is built, which is not this change's business.
        if (TryLateral(schema, func, out var l)) { return l.Parameters; }
        return null;
    }

    /// <summary>
    /// Scalar return field, carrying the volatility tag the host reads to allow constant folding. An aggregate's
    /// result is returned UNTAGGED — volatility is a scalar-only notion (a folded aggregate makes no sense), and
    /// tagging it would advertise a property the host would then act on.
    /// </summary>
    public Schema? ReturnSchema(string schema, string func)
    {
        if (TryScalar(schema, func, out var fn))
        {
            return new Schema(new[] { ScalarFunctionMetadata.TagVolatility(fn.Result, fn) }, null);
        }
        if (TryAggregate(schema, func, out var agg))
        {
            return new Schema(new[] { agg.Result }, null);
        }
        return null;
    }

    /// <summary>Runs a scalar over the argument stream. Consumes/disposes <paramref name="args"/>.</summary>
    public IArrowArrayStream? ExecuteScalar(string schema, string func, IArrowArrayStream args) =>
        TryScalar(schema, func, out var fn) ? GlobalFunctions.ExecuteScalar(fn, args) : null;

    /// <summary>
    /// Output schema of a table function, resolved from its constant args — this is the whole reason the
    /// Bind→Binding model exists, so the binding is built and immediately disposed rather than cached.
    /// </summary>
    public Schema? OutputSchema(string schema, string func, RecordBatch? args)
    {
        if (!TryTable(schema, func, out var fn))
        {
            return null;
        }
        using var binding = fn.Bind(args!);
        return binding.OutputSchema;
    }

    /// <summary>
    /// Binds a table function for one execution. <c>supportsPushdown: true</c> means the host maps the full
    /// result BY NAME (the flag is projection mapping, NOT SQL pushdown — see
    /// <see cref="BindingBoundTableFunction"/>).
    /// </summary>
    public IBoundTableFunction? TableFnBind(string schema, string func, RecordBatch? args) =>
        TryTable(schema, func, out var fn)
            ? new BindingBoundTableFunction(fn.Bind(args!), supportsPushdown: true)
            : null;

    /// <summary>
    /// The replacement SQL for a SQL-generating table function — the host parses it and substitutes it for the
    /// call (bind_replace). BIND-time only and possibly repeated, so a generator must be deterministic and
    /// side-effect-free. Null when the name is not ours.
    /// </summary>
    public string? GenerateTableSql(string schema, string func, SqlGenContext ctx, RecordBatch? args) =>
        TrySqlTable(schema, func, out var fn) ? SqlGen.Generate(fn, ctx, args) : null;

    /// <summary>
    /// Binds an in-out call: a COLLECTOR first (it runs on the host's Sink+Source pipeline-breaker operator and
    /// is wrapped so it flows through the shared exchange marshaling), else a streaming in-out. Null when the
    /// name is not ours. A caller that honours isolation applies it to the returned binding itself — the level
    /// is provider state, not something this set knows.
    /// </summary>
    public IInOutBinding? InOutBind(string schema, string func, RecordBatch? args, Schema inputSchema)
    {
        if (TryCollector(schema, func, out var collector))
        {
            return new CollectorInOutBinding(collector.Bind(args, inputSchema));
        }
        return TryInOut(schema, func, out var fn) ? fn.Bind(args, inputSchema) : null;
    }

    /// <summary>
    /// Binds a ROW-MAPPED (correlated LATERAL) call. Null when the name is not ours. Kept separate from
    /// <see cref="InOutBind"/> on purpose: the two answer different ABI entries and their bindings have
    /// different contracts (an in-out echoes its input, a lateral function does not).
    /// </summary>
    public ILateralBinding? LateralBind(string schema, string func, RecordBatch? args, Schema inputSchema)
    {
        if (!TryLateral(schema, func, out var fn))
        {
            return null;
        }
        Params.Validate(fn.Name, fn.Parameters, allowNamed: true, allowTableInput: false);
        return fn.Bind(args, inputSchema);
    }

    /// <summary>
    /// Opens an aggregate session mapping DuckDB's per-group int64 state ids to live C# accumulators. Null when
    /// the name is not ours.
    /// </summary>
    public IAggregateSession? AggOpen(string schema, string func) =>
        TryAggregate(schema, func, out var fn) ? new AggregateSession(fn) : null;
}
