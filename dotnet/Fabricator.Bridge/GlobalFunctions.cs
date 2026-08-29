// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace Fabricator.Bridge;

/// <summary>
/// The process-wide registry of connection-free GLOBAL functions — the union, across all registered providers,
/// of <c>IBackend.GlobalScalarFunctions</c>. Built once (lazily) and keyed by name (case-insensitive). Used for
/// the <b>handle-0</b> path of the scalar ABI entries (<c>get_function_param_schema</c> /
/// <c>get_function_return_schema</c> / <c>execute_scalar</c>, where a 0 handle means "global, by name") and
/// enumerated by <c>list_global_functions</c> at extension load. A duplicate name across providers is a fatal
/// config error. See docs/global-functions.md.
/// </summary>
public static class GlobalFunctions
{
    private static readonly Lazy<IReadOnlyDictionary<string, IScalarFunction>> ScalarMap =
        new(BuildScalars, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, IInOutFunction>> InOutMap =
        new(() => Build<IInOutFunction>(b => b.GlobalInOutFunctions, f => f.Name, "in-out"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, ILateralFunction>> LateralMap =
        new(() => Build<ILateralFunction>(b => b.GlobalLateralFunctions, f => f.Name, "lateral"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, ICollectorFunction>> CollectorMap =
        new(() => Build<ICollectorFunction>(b => b.GlobalCollectorFunctions, f => f.Name, "collector"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, ITableFunction>> TableMap =
        new(() => Build<ITableFunction>(b => b.GlobalTableFunctions, f => f.Name, "table",
                                        HostGlobalFunctions.TableFunctions),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, IAggregateFunction>> AggregateMap =
        new(() => Build<IAggregateFunction>(b => b.GlobalAggregateFunctions, f => f.Name, "aggregate"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, MacroDefinition>> MacroMap =
        new(() => Build<MacroDefinition>(b => b.GlobalMacros, m => m.Name, "macro"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, ISqlTableFunction>> SqlTableMap =
        new(() => Build<ISqlTableFunction>(b => b.GlobalSqlTableFunctions, f => f.Name, "table_sql"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>All declared global scalar functions (the provider union), for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<IScalarFunction> AllScalars() => (IReadOnlyCollection<IScalarFunction>)ScalarMap.Value.Values;

    /// <summary>All declared global in-out functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<IInOutFunction> AllInOut() => (IReadOnlyCollection<IInOutFunction>)InOutMap.Value.Values;

    /// <summary>All declared global row-mapped (correlated LATERAL) functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<ILateralFunction> AllLaterals() => (IReadOnlyCollection<ILateralFunction>)LateralMap.Value.Values;

    /// <summary>All declared global collector functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<ICollectorFunction> AllCollectors() => (IReadOnlyCollection<ICollectorFunction>)CollectorMap.Value.Values;

    /// <summary>All declared global table functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<ITableFunction> AllTables() => (IReadOnlyCollection<ITableFunction>)TableMap.Value.Values;

    /// <summary>Bind a global table function by name (the handle-0 tablefn_bind path) → an
    /// ITableFunctionSession. Wraps the arg-dependent binding in a <see cref="TableFunctionBindingAdapter"/>
    /// (by-name projection mapping, like a custom table function); DuckDB re-applies filters above the
    /// scan.</summary>
    public static ITableFunctionSession ResolveTable(string name, RecordBatch? args) =>
        TableMap.Value.TryGetValue(name, out var t)
            ? new TableFunctionBindingAdapter(t.Bind(args!), supportsPushdown: true)
            : throw new ArgumentException($"fabricator: no global table function '{name}'");

    /// <summary>All declared global aggregate functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<IAggregateFunction> AllAggregates() => (IReadOnlyCollection<IAggregateFunction>)AggregateMap.Value.Values;

    /// <summary>All declared provider MACROs (the provider union), for <c>list_global_functions</c>. Each carries
    /// its full <c>CREATE MACRO</c> statement, which the host parses + registers at load — no execution path
    /// (a macro is expanded by DuckDB's binder, so nothing ever crosses back).</summary>
    public static IReadOnlyCollection<MacroDefinition> AllMacros() => (IReadOnlyCollection<MacroDefinition>)MacroMap.Value.Values;

    /// <summary>All declared global SQL-generating table functions, for <c>list_global_functions</c>.</summary>
    public static IReadOnlyCollection<ISqlTableFunction> AllSqlTables() => (IReadOnlyCollection<ISqlTableFunction>)SqlTableMap.Value.Values;

    /// <summary>True iff <paramref name="name"/> is a global SQL-generating table function (used by
    /// <see cref="ParamSchema"/> to return the positional++named-tagged parameter schema for that kind).</summary>
    public static bool IsSqlTable(string name) => SqlTableMap.Value.ContainsKey(name);

    /// <summary>Generate the replacement SQL for a global <c>table_sql</c> call (the handle-0
    /// <c>generate_table_sql</c> path). Bind-time only; the returned statement replaces the call in the plan.</summary>
    public static string GenerateTableSql(string name, RecordBatch? args) =>
        SqlTableMap.Value.TryGetValue(name, out var fn)
            ? SqlGen.Generate(fn, args)
            : throw new ArgumentException($"fabricator: no global SQL-generating table function '{name}'");

    /// <summary>Open a session for a global aggregate by name (the handle-0 agg_open path). Throws if none.</summary>
    public static IAggregateSession ResolveAggregate(string name) =>
        AggregateMap.Value.TryGetValue(name, out var a)
            ? new AggregateSession(a)
            : throw new ArgumentException($"fabricator: no global aggregate function '{name}'");

    /// <summary>The positional/cost parameter schema for ANY global function by name (the handle-0
    /// get_function_param_schema path): a scalar's, table's, or aggregate's <c>Parameters</c>; an in-out/collector
    /// declares no cost args here (the input table is the <c>{TABLE}</c> param), so it returns an empty schema.</summary>
    public static Schema ParamSchema(string name)
    {
        if (ScalarMap.Value.TryGetValue(name, out var s)) { return s.Parameters; }
        // Every kind declares ONE schema whose fields already carry their style, so there is nothing to
        // combine: positional, named and table-input all arrive tagged and the host splits on the tag.
        if (TableMap.Value.TryGetValue(name, out var t)) { return t.Parameters; }
        if (SqlTableMap.Value.TryGetValue(name, out var sq)) { return sq.Parameters; }
        if (AggregateMap.Value.TryGetValue(name, out var a)) { return a.Parameters; }
        // A lateral function's schema carries BOTH halves: its positional fields become the per-row input
        // columns (and hence the DuckDB argument types) while its named fields are the constant cost args.
        if (LateralMap.Value.TryGetValue(name, out var lat)) { return lat.Parameters; }
        // In-out / collector cost args are declared as NAMED parameters (e.g. path := '…'); default empty.
        if (InOutMap.Value.TryGetValue(name, out var io)) { return io.Parameters; }
        if (CollectorMap.Value.TryGetValue(name, out var co)) { return co.Parameters; }
        throw new ArgumentException($"fabricator: no global function '{name}'");
    }

    /// <summary>The single return field for a global scalar OR aggregate by name (the handle-0
    /// get_function_return_schema path). Throws for kinds without a scalar return (table/in-out/collector).</summary>
    public static Field ReturnField(string name)
    {
        // A pure scalar's field carries the CONSISTENT tag (fabricator.volatile = "0") so the C++
        // registration folds constants — see ScalarFunctionMetadata.
        if (ScalarMap.Value.TryGetValue(name, out var s)) { return ScalarFunctionMetadata.DeclaredReturnField(s); }
        if (AggregateMap.Value.TryGetValue(name, out var a)) { return a.Result; }
        throw new ArgumentException($"fabricator: global function '{name}' has no scalar return type");
    }

    /// <summary>Resolve a global scalar by name (case-insensitive); throws if none is registered.</summary>
    public static IScalarFunction ResolveScalar(string name) =>
        ScalarMap.Value.TryGetValue(name, out var fn)
            ? fn
            : throw new ArgumentException($"fabricator: no global scalar function '{name}'");

    /// <summary>Bind a global row-mapped (correlated LATERAL) function by name (the handle-0 lateral_bind
    /// path); throws if none is registered.</summary>
    public static ILateralFunctionBinding ResolveLateral(string name, RecordBatch? args, Schema inputSchema)
    {
        if (!LateralMap.Value.TryGetValue(name, out var fn))
        {
            throw new ArgumentException($"fabricator: no global lateral function '{name}'");
        }
        // Checked at BIND (once per plan, so free) rather than at registration: a table-input declaration
        // would build a {TABLE} signature the correlated spelling cannot bind against, i.e. a function nobody
        // could call the way it was declared. The host refuses it too; this is where the message is best.
        Params.Validate(fn.Name, fn.Parameters, allowNamed: true, allowTableInput: false, allowConstant: true);
        args = LateralConstants.Validate(fn.Name, fn.Parameters, args);
        return fn.Bind(args, inputSchema);
    }

    /// <summary>Bind a global in-out OR collector by name (the handle-0 inout_bind path). A collector is wrapped
    /// in a <see cref="CollectorInOutBinding"/> so it flows through the shared exchange marshaling.</summary>
    public static IInOutFunctionBinding ResolveInOut(string name, RecordBatch? args, Schema inputSchema)
    {
        if (InOutMap.Value.TryGetValue(name, out var io))
        {
            return io.Bind(args, inputSchema);
        }
        if (CollectorMap.Value.TryGetValue(name, out var co))
        {
            return new CollectorInOutBinding(co.Bind(args, inputSchema));
        }
        throw new ArgumentException($"fabricator: no global in-out/collector function '{name}'");
    }

    private static IReadOnlyDictionary<string, T> Build<T>(Func<IBackend, IEnumerable<T>> select,
                                                           Func<T, string> nameOf, string kind,
                                                           IEnumerable<T>? host = null)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        // HOST functions first (see HostGlobalFunctions): they are diagnostics ABOUT the provider machinery,
        // so they must exist even when no provider loaded. Going first also means a provider that declares a
        // colliding name trips the duplicate check below instead of silently shadowing one.
        foreach (var fn in host ?? Enumerable.Empty<T>())
        {
            if (!map.TryAdd(nameOf(fn), fn))
            {
                throw new InvalidOperationException(
                    $"fabricator: duplicate global {kind} function name '{nameOf(fn)}' among host functions");
            }
        }
        foreach (var backend in BackendRegistry.All())
        {
            foreach (var fn in select(backend))
            {
                if (!map.TryAdd(nameOf(fn), fn))
                {
                    throw new InvalidOperationException(
                        $"fabricator: duplicate global {kind} function name '{nameOf(fn)}' across providers");
                }
            }
        }
        return map;
    }

    private static IReadOnlyDictionary<string, IScalarFunction> BuildScalars() =>
        Build<IScalarFunction>(b => b.GlobalScalarFunctions, f => f.Name, "scalar");

}
