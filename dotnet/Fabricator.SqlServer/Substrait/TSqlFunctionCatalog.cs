// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using F = Clast.Drivers.SqlServer.Substrait.SubstraitFields;

namespace Clast.Drivers.SqlServer.Substrait;

internal enum TSqlFunctionKind
{
    Scalar,
    Aggregate,
}

/// <summary>
/// A set of implementations of a function that share the same option
/// behavior. Each variant is one entry in the dialect file's
/// supported_scalar_functions or supported_aggregate_functions list.
/// </summary>
internal sealed class TSqlFunctionVariant
{
    public TSqlFunctionVariant(string[] impls, params (string Name, string Value)[] options)
    {
        Impls = impls;
        Options = options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);
    }

    /// <summary>Supported argument signatures, e.g. "i64_i64".</summary>
    public IReadOnlyList<string> Impls { get; }

    /// <summary>For each option, the behavior the translation delivers.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>Minimum argument count when the last argument is variadic; null if not variadic.</summary>
    public int? VariadicMin { get; init; }

    /// <summary>Maximum argument count for variadic functions, if limited.</summary>
    public int? VariadicMax { get; init; }
}

/// <summary>A Substrait function the driver can translate, and how.</summary>
internal sealed class TSqlFunction
{
    public TSqlFunction(string urn, string name, TSqlFunctionKind kind, string systemName, string notation,
        TSqlFunctionVariant[] variants, Func<SType[], SType> returnType, Func<TSqlCall, string> write, bool predicate)
    {
        Urn = urn;
        Name = name;
        Kind = kind;
        SystemName = systemName;
        Notation = notation;
        Variants = variants;
        ReturnType = returnType;
        Write = write;
        Predicate = predicate;
    }

    public string Urn { get; }
    public string Name { get; }
    public TSqlFunctionKind Kind { get; }
    /// <summary>The T-SQL function or operator this maps to (dialect system_metadata.name).</summary>
    public string SystemName { get; }
    /// <summary>INFIX, PREFIX or FUNCTION (dialect system_metadata.notation).</summary>
    public string Notation { get; }
    public IReadOnlyList<TSqlFunctionVariant> Variants { get; }
    public Func<SType[], SType> ReturnType { get; }
    /// <summary>Writes the call; the text is a condition when <see cref="Predicate"/> is set, else a value.</summary>
    public Func<TSqlCall, string> Write { get; }
    public bool Predicate { get; }
    /// <summary>Whether DISTINCT invocations are supported (aggregates).</summary>
    public bool AllowsDistinct { get; init; }
    /// <summary>Whether the aggregate's sort fields (an ordered aggregate) are supported.</summary>
    public bool AllowsSorts { get; init; }
}

/// <summary>A resolved call being written: its arguments, their types and the chosen options.</summary>
internal sealed class TSqlCall
{
    private readonly TSqlEmitter _e;
    private readonly IReadOnlyList<SqlColumn> _scope;
    private readonly SqlExpr?[] _rendered;
    private string? _filter;

    public TSqlCall(TSqlEmitter emitter, IReadOnlyList<SqlColumn> scope, string displayName, List<FunctionArg> args, SqlExpr?[] rendered,
        SType[] argTypes, IReadOnlyDictionary<string, string> options, HashSet<string> requested, SType resultType)
    {
        _e = emitter;
        _scope = scope;
        _rendered = rendered;
        DisplayName = displayName;
        Args = args;
        ArgTypes = argTypes;
        Options = options;
        Requested = requested;
        ResultType = resultType;
    }

    public string DisplayName { get; }
    public List<FunctionArg> Args { get; }
    public SType[] ArgTypes { get; }
    /// <summary>The behavior the chosen variant delivers, for each of its options.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }
    /// <summary>The options the plan asked for (others are the consumer's choice).</summary>
    public HashSet<string> Requested { get; }
    public SType ResultType { get; }
    /// <summary>What the SQL written so far contains.</summary>
    public SqlProps Props { get; private set; }

    /// <summary>The measure's filter, for aggregate calls.</summary>
    public Expr? Filter { get; init; }
    /// <summary>True for DISTINCT aggregate invocations.</summary>
    public bool Distinct { get; init; }
    /// <summary>The order of values, for ordered aggregates.</summary>
    public List<SortField> Sorts { get; init; } = new();

    public TSqlEmitter Emitter => _e;

    public int Count => Args.Count;

    public SqlExpr Arg(int index)
    {
        var value = index < Args.Count ? Args[index].Value : null;
        if (value == null)
        {
            throw SubstraitTranslationException.MalformedPlan($"Argument {index} of '{DisplayName}' must be a value.");
        }
        var e = _rendered[index] ??= _e.Render(value, _scope);
        Props |= e.Props;
        return e;
    }

    /// <summary>Argument <paramref name="index"/> as a value (a bit for booleans).</summary>
    public string V(int index) => Arg(index).Value;

    /// <summary>Argument <paramref name="index"/> as a condition.</summary>
    public string P(int index) => Arg(index).Predicate;

    public SType T(int index) => index < ArgTypes.Length ? ArgTypes[index] : SType.Unknown;

    public bool MayBeNull(int index) => TSqlEmitter.MayBeNull(Arg(index));

    public string Enum(int index)
    {
        var value = index < Args.Count ? Args[index].Enum : null;
        return value ?? throw SubstraitTranslationException.MalformedPlan($"Argument {index} of '{DisplayName}' must be an enumeration.");
    }

    public LiteralExpr? Literal(int index) => index < Args.Count && Args[index].Value is LiteralExpr { IsNull: false } lit ? lit : null;

    public string? StringLiteral(int index) => Literal(index) is { } lit && lit.Type.IsStringLike ? lit.StringValue : null;

    public string Join(string separator, int from = 0) => string.Join(separator, Enumerable.Range(from, Count - from).Select(V));

    /// <summary>Converts to the result type, for T-SQL functions that return a wider type.</summary>
    public string Cast(string text) => $"CAST({text} AS {TSql.RequireTypeName(ResultType)})";

    /// <summary>Casts only when the result type is one T-SQL widens (smallint to int, real to float).</summary>
    public string Narrow(string text) => ResultType.Kind is TypeKind.I16 or TypeKind.Fp32 ? Cast(text) : text;

    /// <summary>
    /// The collation that gives an explicitly requested case sensitivity, or
    /// null when values keep the query's collation (when none was requested,
    /// the database's own case sensitivity is the consumer's choice).
    /// </summary>
    public string? CaseCollation()
    {
        if (!Requested.Contains("case_sensitivity"))
        {
            return null;
        }
        string target = Options["case_sensitivity"] == "CASE_INSENSITIVE" ? TSqlEmitter.CaseInsensitiveCollation : TSqlEmitter.BinaryCollation;
        return target == _e.Collation ? null : target;
    }

    /// <summary>Argument <paramref name="index"/>, a string, with the requested case sensitivity.</summary>
    public string S(int index)
    {
        string? collation = CaseCollation();
        return collation == null ? V(index) : $"({V(index)}) COLLATE {collation}";
    }

    /// <summary>A string argument as nvarchar(max), so that functions don't truncate it at 8000 bytes.</summary>
    public string MaxString(int index)
    {
        string? collation = CaseCollation() ?? _e.Collation;
        string text = $"CAST({V(index)} AS nvarchar(max))";
        return collation == null ? text : $"{text} COLLATE {collation}";
    }

    /// <summary>The measure's filter as a condition.</summary>
    public string FilterPredicate()
    {
        if (Filter == null)
        {
            throw new InvalidOperationException("Call has no filter.");
        }
        if (_filter == null)
        {
            var e = _e.Render(Filter, _scope);
            Props |= e.Props;
            _filter = e.Predicate;
        }
        return _filter;
    }

    /// <summary>An aggregate's argument: null for rows the measure's filter excludes, which aggregates skip.</summary>
    public string AggArg(int index) => Filter == null ? V(index) : $"CASE WHEN {FilterPredicate()} THEN {V(index)} END";

    public string DistinctPrefix => Distinct ? "DISTINCT " : "";

    public string OrderBy()
    {
        var items = _e.OrderItems(Sorts, _scope);
        return string.Join(", ", items);
    }

    public SubstraitTranslationException Unsupported(string detail)
        => SubstraitTranslationException.Unsupported($"{DisplayName}: {detail}");
}

/// <summary>
/// The Substrait functions this driver translates to T-SQL. This catalog and
/// the dialect file (Dialect/sqlserver_dialect.yaml) describe the same set of
/// functions, implementations and options; a unit test keeps them in sync.
/// </summary>
internal static class TSqlFunctionCatalog
{
    public const string ArithmeticUrn = "extension:io.substrait:functions_arithmetic";
    public const string ArithmeticDecimalUrn = "extension:io.substrait:functions_arithmetic_decimal";
    public const string ComparisonUrn = "extension:io.substrait:functions_comparison";
    public const string BooleanUrn = "extension:io.substrait:functions_boolean";
    public const string StringUrn = "extension:io.substrait:functions_string";
    public const string DatetimeUrn = "extension:io.substrait:functions_datetime";
    public const string RoundingUrn = "extension:io.substrait:functions_rounding";
    public const string RoundingDecimalUrn = "extension:io.substrait:functions_rounding_decimal";
    public const string LogarithmicUrn = "extension:io.substrait:functions_logarithmic";
    public const string AggregateGenericUrn = "extension:io.substrait:functions_aggregate_generic";
    public const string AggregateApproxUrn = "extension:io.substrait:functions_aggregate_approx";
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] Fabricator's own extension: string min/max.</summary>
    public const string FabricatorStringUrn = "extension:fabricator:functions_string";
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] A function of the attached catalog, named by a JSON array of its schema
    /// path and name — never resolved against the built-ins.</summary>
    public const string FabricatorCatalogFunctionUrn = "extension:fabricator:catalog_function";
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] Window functions with IGNORE NULLS, which Substrait cannot say.</summary>
    public const string FabricatorWindowUrn = "extension:fabricator:functions_window";
    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] DuckDB date functions with no Substrait counterpart.</summary>
    public const string FabricatorDatetimeUrn = "extension:fabricator:functions_datetime";

    /// <summary>T-SQL functions take at most 254 arguments; CONCAT's first is an empty nvarchar(max).</summary>
    private const int MaxConcatArguments = 253;

    private const int MaxArguments = 254;

    public static IReadOnlyList<TSqlFunction> All { get; } = Build();

    private static readonly Dictionary<string, List<TSqlFunction>> s_byName = All
        .GroupBy(f => f.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    #region Resolution

    public static SqlExpr WriteScalar(TSqlEmitter e, ScalarFunctionExpr sf, IReadOnlyList<SqlColumn> scope)
    {
        if (sf.Function.Urn == FabricatorCatalogFunctionUrn)
        {
            return WriteCatalogFunction(e, sf, scope);
        }
        var (rendered, argTypes) = RenderArgs(e, sf.Args, scope);
        var (fn, options, requested) = Resolve(sf.Function, TSqlFunctionKind.Scalar, sf.Args, argTypes, sf.Options);
        var resultType = sf.OutputType ?? fn.ReturnType(argTypes);
        var call = new TSqlCall(e, scope, sf.Function.DisplayName, sf.Args, rendered, argTypes, options, requested, resultType);
        string text = fn.Write(call);
        return fn.Predicate ? SqlExpr.Condition(text, call.Props) : SqlExpr.Of(text, resultType, call.Props);
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A call of the attached catalog's own function (a SQL Server UDF), by the name
    /// the provider gives it. Its arguments arrive cast to the declared parameter types and its result is the server's
    /// own value, so the call is exactly what the host would have run per row.
    /// </summary>
    private static SqlExpr WriteCatalogFunction(TSqlEmitter e, ScalarFunctionExpr sf, IReadOnlyList<SqlColumn> scope)
    {
        string[] path;
        try
        {
            path = System.Text.Json.JsonSerializer.Deserialize<string[]>(sf.Function.RawName) ?? Array.Empty<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            throw SubstraitTranslationException.MalformedPlan($"Catalog function name is not a JSON array: {sf.Function.RawName}");
        }
        string display = string.Join(".", path);
        string? name = path.Length >= 2 ? e.CatalogFunctions?.Invoke(path) : null;
        if (name == null)
        {
            throw SubstraitTranslationException.Unsupported($"{display}() is not a function of the server");
        }
        if (sf.OutputType is not { } resultType)
        {
            throw SubstraitTranslationException.Unsupported($"{display}(): the plan does not name its result type");
        }
        var (rendered, argTypes) = RenderArgs(e, sf.Args, scope);
        var call = new TSqlCall(e, scope, display, sf.Args, rendered, argTypes,
                                new Dictionary<string, string>(), new HashSet<string>(), resultType);
        var args = new List<string>();
        for (int i = 0; i < sf.Args.Count; i++)
        {
            args.Add(call.V(i));
        }
        return SqlExpr.Of($"{name}({string.Join(", ", args)})", resultType, call.Props);
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A window function. An aggregate is written as it would be in an
    /// aggregate relation (so exact avg/sum and string min/max carry over), with the OVER clause after each of its
    /// aggregate calls; ranking and offset functions are written here. Declines what T-SQL computes differently:
    /// DISTINCT, frames other than unbounded/current row under RANGE, ROWS offsets that are not constants, and
    /// functions that need an order (ranks, lag/lead, first/last value) without one.
    /// IGNORE NULLS (lag, lead, first_value, last_value) needs SQL Server 2022 or later.
    /// </summary>
    public static SqlExpr WriteWindow(TSqlEmitter e, WindowCall call, IReadOnlyList<SqlColumn> scope, List<string> partitions, List<OrderItem> order)
    {
        string name = call.Function.Name;
        bool ignoreNulls = false;
        if (call.Function.Urn == FabricatorWindowUrn && name.EndsWith("_ignore_nulls", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "_ignore_nulls".Length);
            ignoreNulls = true;
            if (!e.SupportsIgnoreNulls)
                throw SubstraitTranslationException.Unsupported($"{name} IGNORE NULLS needs SQL Server 2022 or later.");
            if (name is not ("lag" or "lead" or "first_value" or "last_value"))
                throw SubstraitTranslationException.UnsupportedFunction(call.Function.DisplayName);
        }
        else if (call.Function.Urn != ArithmeticUrn && call.Function.Urn != AggregateGenericUrn
            && call.Function.Urn != ArithmeticDecimalUrn && call.Function.Urn != FabricatorStringUrn
            && call.Function.Urn != DatetimeUrn && call.Function.Urn != BooleanUrn)
        {
            throw SubstraitTranslationException.UnsupportedFunction(call.Function.DisplayName);
        }
        if (call.Invocation == F.AggregationInvocation_Distinct)
            throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName}: DISTINCT window functions are not supported; T-SQL does not allow them.");
        var resultType = call.OutputType ?? throw SubstraitTranslationException.MalformedPlan($"Window function {call.Function.DisplayName} has no output type.");

        string Over(bool frame)
        {
            var parts = new List<string>();
            if (partitions.Count > 0) parts.Add("PARTITION BY " + string.Join(", ", partitions));
            if (order.Count > 0) parts.Add("ORDER BY " + string.Join(", ", order));
            if (frame) parts.Add(Frame(call, order.Count > 0, out _) ?? "");
            return "(" + string.Join(" ", parts.Where(p => p.Length > 0)) + ")";
        }
        string Value(int index)
        {
            var arg = index < call.Args.Count ? call.Args[index].Value : null;
            if (arg == null) throw SubstraitTranslationException.MalformedPlan($"Argument {index} of window function {call.Function.DisplayName} must be a value.");
            var v = e.Render(arg, scope);
            if ((v.Props & (SqlProps.Subquery | SqlProps.Outer | SqlProps.Window | SqlProps.Aggregate)) != 0)
                throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName}: arguments with a subquery, an outer reference or an aggregate are not supported.");
            return v.Value;
        }
        long Constant(int index, long min)
        {
            if (index >= call.Args.Count || call.Args[index].Value is not LiteralExpr { IsNull: false } lit || !lit.Type.IsInteger || lit.IntValue < min)
                throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName}: argument {index} must be a constant integer of at least {min}.");
            return lit.IntValue;
        }
        void NeedsOrder()
        {
            if (order.Count == 0)
                throw SubstraitTranslationException.Unsupported($"{name} without ORDER BY is not supported; its result would depend on an arbitrary order.");
        }
        string Nulls() => ignoreNulls ? " IGNORE NULLS" : "";
        const SqlProps props = SqlProps.Column | SqlProps.Window;

        if (call.Function.Urn is ArithmeticUrn or FabricatorWindowUrn)
        {
            switch (name)
            {
                case "row_number": NeedsOrder(); return SqlExpr.Of("ROW_NUMBER() OVER " + Over(false), resultType, props);
                case "rank": NeedsOrder(); return SqlExpr.Of("RANK() OVER " + Over(false), resultType, props);
                case "dense_rank": NeedsOrder(); return SqlExpr.Of("DENSE_RANK() OVER " + Over(false), resultType, props);
                case "percent_rank": NeedsOrder(); return SqlExpr.Of("PERCENT_RANK() OVER " + Over(false), resultType, props);
                case "cume_dist": NeedsOrder(); return SqlExpr.Of("CUME_DIST() OVER " + Over(false), resultType, props);
                case "ntile": NeedsOrder(); return SqlExpr.Of($"NTILE({TSql.Int(Constant(0, 1))}) OVER " + Over(false), resultType, props);
                case "lag":
                case "lead":
                {
                    NeedsOrder();
                    var args = new List<string> { Value(0) };
                    long offset = call.Args.Count > 1 ? Constant(1, 0) : 1;
                    if (ignoreNulls && offset != 1)
                    {
                        // measured: T-SQL moves `offset` rows and then on to the next non-null one, DuckDB counts
                        // non-null rows only; the two agree for an offset of 1
                        throw SubstraitTranslationException.Unsupported($"{name} IGNORE NULLS with an offset other than 1 is not supported; SQL Server counts the offset differently.");
                    }
                    if (ignoreNulls && call.Args.Count > 2 && !IsNullValue(call.Args[2].Value))
                    {
                        // measured: when only nulls follow, DuckDB returns the default and T-SQL null (T-SQL's
                        // default applies only where the offset leaves the partition)
                        throw SubstraitTranslationException.Unsupported($"{name} IGNORE NULLS with a default is not supported; SQL Server applies the default differently.");
                    }
                    if (call.Args.Count > 1) args.Add(TSql.Int(offset));
                    if (call.Args.Count > 2) args.Add(Value(2));
                    if (call.Args.Count > 3) throw SubstraitTranslationException.Unsupported($"{name} with {call.Args.Count} arguments is not supported.");
                    return SqlExpr.Of($"{name.ToUpperInvariant()}({string.Join(", ", args)}){Nulls()} OVER " + Over(false), resultType, props);
                }
                case "first_value":
                case "last_value":
                    NeedsOrder();
                    return SqlExpr.Of($"{name.ToUpperInvariant()}({Value(0)}){Nulls()} OVER " + Over(true), resultType, props);
            }
            if (call.Function.Urn == FabricatorWindowUrn)
                throw SubstraitTranslationException.UnsupportedFunction(call.Function.DisplayName);
        }

        // an aggregate over the window
        if (name is not ("count" or "sum" or "avg" or "min" or "max" or "bool_and" or "bool_or" or "std_dev" or "variance"))
            throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName} is not supported as a window function.");
        var measure = new Measure
        {
            Call = new AggregateCall
            {
                Function = call.Function,
                Args = call.Args,
                Options = call.Options,
                OutputType = call.OutputType,
                Invocation = call.Invocation,
            },
        };
        var aggregate = WriteAggregate(e, measure, scope);
        string text = InsertOver(aggregate.Value, "OVER " + Over(true), out int calls);
        if (calls == 0)
            throw SubstraitTranslationException.Unsupported($"{call.Function.DisplayName}: no aggregate call to apply the window to.");
        return SqlExpr.Of(text, aggregate.Type, props);
    }

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] DuckDB's lpad / rpad, counted in code points.</summary>
    private static string Pad(TSqlCall c, bool left)
    {
        string? fill = c.StringLiteral(2);
        if (!IsNonNegativeConstant(c, 1) || string.IsNullOrEmpty(fill))
        {
            throw c.Unsupported("the length must be a constant of at least 0 and the fill a non-empty constant.");
        }
        string cp = TSqlEmitter.CodePointCollation;
        string s = $"(({c.V(0)}) COLLATE {cp})";
        string n = c.V(1);
        string length = $"(LEN({s} + N'.') - 1)";
        string padding = $"LEFT(REPLICATE(CAST({TSql.Unicode(fill)} AS nvarchar(max)) COLLATE {cp}, {n}), {n} - {length})";
        string padded = left ? $"{padding} + {s}" : $"{s} + {padding}";
        return c.Emitter.Collate($"(CASE WHEN {length} >= {n} THEN LEFT({s}, {n}) ELSE {padded} END)");
    }

    private static bool IsNonNegativeConstant(TSqlCall c, int index)
    {
        var e = index < c.Args.Count ? c.Args[index].Value : null;
        while (e is CastExpr cast)
        {
            e = cast.Input;
        }
        return e is LiteralExpr { IsNull: false } lit && lit.Type.IsInteger && lit.IntValue >= 0;
    }

    private static bool IsNonZeroConstant(Expr? e) => e switch
    {
        LiteralExpr { IsNull: false } lit => lit.Type.IsFloat ? lit.FloatValue != 0 : lit.Type.IsInteger ? lit.IntValue != 0 : false,
        CastExpr cast => IsNonZeroConstant(cast.Input),
        _ => false,
    };

    private static bool IsNullValue(Expr? e) => e is LiteralExpr { IsNull: true } || e is CastExpr { Input: LiteralExpr { IsNull: true } };

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] The frame clause, or null when the frame is the whole partition.</summary>
    private static string? Frame(WindowCall call, bool ordered, out bool wholePartition)
    {
        var lower = call.Lower ?? throw SubstraitTranslationException.MalformedPlan("A window function has no lower bound.");
        var upper = call.Upper ?? throw SubstraitTranslationException.MalformedPlan("A window function has no upper bound.");
        bool range = call.BoundsType == F.BoundsType_Range;
        if (call.BoundsType is not (F.BoundsType_Rows or F.BoundsType_Range))
            throw SubstraitTranslationException.Unsupported($"window bounds type {call.BoundsType} is not supported.");
        bool unboundedBoth = lower.Kind == F.WindowBound_Unbounded && upper.Kind == F.WindowBound_Unbounded;
        // with all rows peers (no ORDER BY), RANGE ... CURRENT ROW reaches the last peer, i.e. the partition's end
        wholePartition = unboundedBoth || (!ordered && range && lower.Kind == F.WindowBound_Unbounded && upper.Kind == F.WindowBound_CurrentRow);
        if (!ordered)
        {
            if (!wholePartition)
                throw SubstraitTranslationException.Unsupported("a window frame without ORDER BY that is not the whole partition is not supported; it depends on an arbitrary order.");
            return null;
        }
        string Bound(WindowBound b, bool isLower)
        {
            switch (b.Kind)
            {
                case F.WindowBound_Unbounded: return isLower ? "UNBOUNDED PRECEDING" : "UNBOUNDED FOLLOWING";
                case F.WindowBound_CurrentRow: return "CURRENT ROW";
                case F.WindowBound_Preceding:
                case F.WindowBound_Following:
                    if (range)
                        throw SubstraitTranslationException.Unsupported("RANGE frames with an offset are not supported; T-SQL's RANGE has none.");
                    if (b.Offset is not LiteralExpr { IsNull: false } lit || !lit.Type.IsInteger || lit.IntValue < 0)
                        throw SubstraitTranslationException.Unsupported("a ROWS frame offset must be a constant, non-negative integer.");
                    return TSql.Int(lit.IntValue) + (b.Kind == F.WindowBound_Preceding ? " PRECEDING" : " FOLLOWING");
                default: throw SubstraitTranslationException.MalformedPlan($"Window bound kind {b.Kind} is not valid.");
            }
        }
        return $"{(range ? "RANGE" : "ROWS")} BETWEEN {Bound(lower, true)} AND {Bound(upper, false)}";
    }

    private static readonly HashSet<string> s_windowAggregates = new(StringComparer.Ordinal)
    {
        "SUM", "COUNT_BIG", "COUNT", "MIN", "MAX", "AVG", "STDEV", "STDEVP", "VAR", "VARP",
    };

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] Puts <paramref name="over"/> after each aggregate call in written SQL,
    /// skipping string literals and bracketed identifiers.
    /// </summary>
    internal static string InsertOver(string text, string over, out int calls)
    {
        calls = 0;
        var sb = new StringBuilder(text.Length + over.Length + 2);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch is '\'' or '[')
            {
                int end = SkipQuoted(text, i);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                bool wordStart = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '@' or '#');
                if (wordStart && j < text.Length && text[j] == '(' && s_windowAggregates.Contains(text.Substring(i, j - i)))
                {
                    int close = MatchingParen(text, j);
                    sb.Append(text, i, close + 1 - i).Append(' ').Append(over);
                    calls++;
                    i = close + 1;
                    continue;
                }
                sb.Append(text, i, j - i);
                i = j;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static int SkipQuoted(string text, int start)
    {
        char close = text[start] == '[' ? ']' : '\'';
        for (int k = start + 1; k < text.Length; k++)
        {
            if (text[k] != close) continue;
            if (k + 1 < text.Length && text[k + 1] == close) { k++; continue; }
            return k + 1;
        }
        throw SubstraitTranslationException.MalformedPlan("Unterminated literal in written SQL.");
    }

    private static int MatchingParen(string text, int open)
    {
        int depth = 0;
        for (int k = open; k < text.Length; k++)
        {
            char ch = text[k];
            if (ch is '\'' or '[') { k = SkipQuoted(text, k) - 1; continue; }
            if (ch == '(') depth++;
            else if (ch == ')' && --depth == 0) return k;
        }
        throw SubstraitTranslationException.MalformedPlan("Unbalanced parentheses in written SQL.");
    }

    public static SqlExpr WriteAggregate(TSqlEmitter e, Measure measure, IReadOnlyList<SqlColumn> scope)
    {
        var agg = measure.Call;
        var (rendered, argTypes) = RenderArgs(e, agg.Args, scope);
        var (fn, options, requested) = Resolve(agg.Function, TSqlFunctionKind.Aggregate, agg.Args, argTypes, agg.Options);
        var resultType = agg.OutputType ?? fn.ReturnType(argTypes);
        bool distinct = agg.Invocation == F.AggregationInvocation_Distinct;
        if (distinct && !fn.AllowsDistinct)
        {
            throw SubstraitTranslationException.Unsupported($"{agg.Function.DisplayName}: DISTINCT is not supported.");
        }
        if (agg.Sorts.Count > 0 && !fn.AllowsSorts)
        {
            throw SubstraitTranslationException.Unsupported($"{agg.Function.DisplayName}: ordered aggregation (aggregate sort fields) is not supported.");
        }
        var call = new TSqlCall(e, scope, agg.Function.DisplayName, agg.Args, rendered, argTypes, options, requested, resultType)
        {
            Filter = measure.Filter,
            Distinct = distinct,
            Sorts = agg.Sorts,
        };
        string text = fn.Write(call);
        if ((call.Props & SqlProps.Subquery) != 0)
        {
            throw SubstraitTranslationException.Unsupported($"{agg.Function.DisplayName}: subqueries in aggregate arguments are not supported; T-SQL does not allow them.");
        }
        return SqlExpr.Of(text, resultType, call.Props | SqlProps.Aggregate | SqlProps.Column);
    }

    private static (SqlExpr?[] Rendered, SType[] Types) RenderArgs(TSqlEmitter e, List<FunctionArg> args, IReadOnlyList<SqlColumn> scope)
    {
        var rendered = new SqlExpr?[args.Count];
        var types = new SType[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            var value = args[i].Value;
            if (value == null)
            {
                types[i] = SType.Unknown;
            }
            else if (value is LiteralExpr lit)
            {
                // Literals are rendered by the function that needs them (interval literals can't be rendered alone).
                types[i] = lit.Type;
            }
            else
            {
                rendered[i] = e.Render(value, scope);
                types[i] = rendered[i]!.Type;
            }
        }
        return (rendered, types);
    }

    private static (TSqlFunction Function, IReadOnlyDictionary<string, string> Options, HashSet<string> Requested) Resolve(
        FunctionDecl decl, TSqlFunctionKind kind, List<FunctionArg> args, SType[] argTypes, List<FunctionOptionValue> options)
    {
        if (!s_byName.TryGetValue(decl.Name, out var named))
        {
            throw SubstraitTranslationException.UnsupportedFunction(decl.DisplayName);
        }

        // Plans that don't name the extension (some producers omit URNs) are matched by name and signature.
        var functions = named.Where(f => f.Kind == kind && (decl.Urn == null || f.Urn == decl.Urn)).ToList();
        if (functions.Count == 0)
        {
            throw SubstraitTranslationException.UnsupportedFunction(decl.Urn == null ? decl.DisplayName : $"{decl.DisplayName} ({decl.Urn})");
        }

        string[]? callTokens = decl.Signature != null ? SplitSignature(decl.Signature) : InferTokens(args, argTypes);
        var candidates = new List<(TSqlFunction Fn, TSqlFunctionVariant Variant)>();
        foreach (var fn in functions)
        {
            foreach (var variant in fn.Variants)
            {
                if (callTokens == null || variant.Impls.Any(impl => SignatureMatches(callTokens, SplitSignature(impl), variant.VariadicMin.HasValue)))
                {
                    candidates.Add((fn, variant));
                }
            }
        }
        if (candidates.Count == 0)
        {
            string sig = callTokens == null ? "" : ":" + string.Join("_", callTokens);
            throw SubstraitTranslationException.UnsupportedFunction($"{decl.Name}{sig}");
        }

        // A requested option must be honored with the first preference the driver can deliver.
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (option.Preference.Count == 0)
            {
                continue;
            }
            string? chosen = option.Preference.FirstOrDefault(p =>
                candidates.Any(c => c.Variant.Options.TryGetValue(option.Name, out var v) && v == p));
            if (chosen == null)
            {
                throw SubstraitTranslationException.UnsupportedOption(decl.DisplayName, option.Name, option.Preference);
            }
            candidates = candidates.Where(c => c.Variant.Options.TryGetValue(option.Name, out var v) && v == chosen).ToList();
            requested.Add(option.Name);
        }

        var (function, chosenVariant) = candidates[0];
        if (chosenVariant.VariadicMax.HasValue && args.Count > chosenVariant.VariadicMax.Value)
        {
            throw SubstraitTranslationException.Unsupported($"{decl.DisplayName}: at most {chosenVariant.VariadicMax} arguments are supported.");
        }
        return (function, chosenVariant.Options, requested);
    }

    private static string[] SplitSignature(string signature)
        => signature.Length == 0 ? Array.Empty<string>() : signature.Split('_');

    private static string[]? InferTokens(List<FunctionArg> args, SType[] argTypes)
    {
        var tokens = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Enum != null)
            {
                tokens[i] = "req";
                continue;
            }
            var token = argTypes[i].SignatureToken;
            if (token == null)
            {
                return null;
            }
            tokens[i] = token;
        }
        return tokens;
    }

    private static bool SignatureMatches(string[] call, string[] impl, bool variadic)
    {
        if (variadic)
        {
            if (impl.Length == 0 || call.Length < impl.Length - 1)
            {
                return false;
            }
        }
        else if (call.Length != impl.Length)
        {
            return false;
        }

        for (int i = 0; i < call.Length; i++)
        {
            string expected = i < impl.Length ? impl[i] : impl[impl.Length - 1];
            if (expected != "any" && expected != call[i])
            {
                return false;
            }
        }
        return true;
    }

    #endregion

    #region Catalog

    private static SType SameAsFirst(SType[] t) => t.Length > 0 ? t[0] : SType.Unknown;

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] String equality with an indexable superset in front of the exact key
    /// comparison, when one side is a plain column: <c>col = 'x' AND key(col) = key('x')</c>. The first conjunct
    /// lets SQL Server seek an index or eliminate segments; the second makes the answer exact.
    /// </summary>
    private static string Indexable(TSqlCall c, string exact)
    {
        if (!c.T(0).IsStringLike || !c.T(1).IsStringLike)
        {
            return exact;
        }
        string? left = TSqlEmitter.IndexableString(c.V(0));
        string? right = TSqlEmitter.IndexableString(c.V(1));
        if (left == null || right == null || (!TSqlEmitter.IsColumnReference(left) && !TSqlEmitter.IsColumnReference(right)))
        {
            return exact;
        }
        return $"(({left} = {right}) AND {exact})";
    }

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] Argument <paramref name="i"/>, a string as its exact key.</summary>
    private static string K(TSqlCall c, int i) => c.T(i).IsStringLike ? TSqlEmitter.StringKey(c.V(i)) : TSqlEmitter.Comparable(c.T(i), c.V(i));

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] Argument <paramref name="i"/> for an EQUALITY, a string as its equality
    /// key (cheaper than the order key <see cref="K"/>).</summary>
    private static string KE(TSqlCall c, int i) => c.T(i).IsStringLike ? c.Emitter.EqualityKey(c.V(i)) : TSqlEmitter.Comparable(c.T(i), c.V(i));

    /// <summary>
    /// [FABRICATOR-PATCH: OFFER-READY] An average computed as its exact sum divided by its count, in float; null
    /// when no value counts (SUM is null then, and the count is nulled so nothing divides by zero).
    /// </summary>
    private static string ExactAverage(TSqlCall c, string exactValue)
        => $"(CAST(SUM({c.DistinctPrefix}{exactValue}) AS float) / CAST(NULLIF(COUNT_BIG({c.DistinctPrefix}{c.AggArg(0)}), 0) AS float))";
    private static SType Bool(SType[] _) => SType.Bool;
    private static SType Real(SType[] _) => SType.Fp64;
    private static SType Long(SType[] _) => SType.I64;
    private static SType Str(SType[] _) => SType.String;

    /// <summary>fp32 functions of fp32 return fp32; integer and decimal inputs give fp64.</summary>
    private static SType FloatOf(SType[] t) => t.Length > 0 && t[0].Kind == TypeKind.Fp32 ? new SType(TypeKind.Fp32) : SType.Fp64;

    private static TSqlFunctionVariant V(params string[] impls) => new(impls);

    private static List<TSqlFunction> Build()
    {
        // Locals rather than static fields: Build() runs during static initialization.
        string[] ints = ["i16_i16", "i32_i32", "i64_i64"];
        string[] floats = ["fp32_fp32", "fp64_fp64"];
        string[] unaryInts = ["i16", "i32", "i64"];
        string[] unaryFloats = ["fp32", "fp64"];
        string[] unaryNumbers = ["i16", "i32", "i64", "fp32", "fp64"];
        var error = ("overflow", "ERROR");
        var ieee = ("rounding", "TIE_TO_EVEN");
        var list = new List<TSqlFunction>();

        void Scalar(string urn, string name, string sql, string notation, Func<SType[], SType> ret, Func<TSqlCall, string> write, params TSqlFunctionVariant[] variants)
            => list.Add(new TSqlFunction(urn, name, TSqlFunctionKind.Scalar, sql, notation, variants, ret, write, predicate: false));

        void Condition(string urn, string name, string sql, string notation, Func<TSqlCall, string> write, params TSqlFunctionVariant[] variants)
            => list.Add(new TSqlFunction(urn, name, TSqlFunctionKind.Scalar, sql, notation, variants, Bool, write, predicate: true));

        void Aggregate(string urn, string name, string sql, Func<SType[], SType> ret, Func<TSqlCall, string> write, bool distinct, params TSqlFunctionVariant[] variants)
            => list.Add(new TSqlFunction(urn, name, TSqlFunctionKind.Aggregate, sql, "FUNCTION", variants, ret, write, predicate: false) { AllowsDistinct = distinct });

        // ── functions_arithmetic: scalar ────────────────────────────────
        // SQL Server raises an error on integer overflow, on division by zero (integer or float)
        // and on float domain errors (sqrt(-1), log(0), asin(2)); float arithmetic is IEEE 754.
        foreach (var (name, op) in new[] { ("add", "+"), ("subtract", "-"), ("multiply", "*") })
        {
            Scalar(ArithmeticUrn, name, op, "INFIX", SameAsFirst, c => $"({c.V(0)} {op} {c.V(1)})",
                new TSqlFunctionVariant(ints, error), new TSqlFunctionVariant(floats, ieee));
        }
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] a float divided by zero is inf / nan in DuckDB and an error in T-SQL, which has neither value:
        // a float division declines unless the divisor is a non-zero constant (measured: f / (t - t))
        Scalar(ArithmeticUrn, "divide", "/", "INFIX", SameAsFirst,
            c => (c.T(0).IsFloat || c.T(1).IsFloat) && !IsNonZeroConstant(c.Args[1].Value)
                ? throw c.Unsupported("a float division by a value that may be zero is not supported; DuckDB returns inf, SQL Server fails.")
                : Divide(c),
            new TSqlFunctionVariant(ints, ("on_division_by_zero", "ERROR"), ("on_domain_error", "ERROR"), error),
            new TSqlFunctionVariant(ints, ("on_division_by_zero", "NULL"), ("on_domain_error", "NULL"), error),
            new TSqlFunctionVariant(floats, ("on_division_by_zero", "ERROR"), ("on_domain_error", "ERROR"), ieee),
            new TSqlFunctionVariant(floats, ("on_division_by_zero", "NULL"), ("on_domain_error", "NULL"), ieee));
        Scalar(ArithmeticUrn, "modulus", "%", "INFIX", SameAsFirst, Modulus,
            new TSqlFunctionVariant(ints, ("division_type", "TRUNCATE"), ("on_domain_error", "ERROR"), error),
            new TSqlFunctionVariant(ints, ("division_type", "TRUNCATE"), ("on_domain_error", "NULL"), error),
            new TSqlFunctionVariant(ints, ("division_type", "FLOOR"), ("on_domain_error", "ERROR"), error),
            new TSqlFunctionVariant(ints, ("division_type", "FLOOR"), ("on_domain_error", "NULL"), error));
        Scalar(ArithmeticUrn, "negate", "-", "PREFIX", SameAsFirst, c => $"(- {c.V(0)})",
            new TSqlFunctionVariant(unaryInts, error), V(unaryFloats));
        Scalar(ArithmeticUrn, "abs", "ABS", "FUNCTION", SameAsFirst, c => c.Narrow($"ABS({c.V(0)})"),
            new TSqlFunctionVariant(unaryInts, error), V(unaryFloats));
        Scalar(ArithmeticUrn, "sign", "SIGN", "FUNCTION", SameAsFirst, c => c.Narrow($"SIGN({c.V(0)})"), V(unaryNumbers));
        // POWER computes in float, so an integer power is not exact; only the float ones are listed.
        Scalar(ArithmeticUrn, "power", "POWER", "FUNCTION", SameAsFirst, c => c.Narrow($"POWER({c.V(0)}, {c.V(1)})"), V(floats));
        Scalar(ArithmeticUrn, "sqrt", "SQRT", "FUNCTION", FloatOf, c => c.Narrow($"SQRT({c.V(0)})"),
            new TSqlFunctionVariant(["i64", "fp32", "fp64"], ("on_domain_error", "ERROR")));
        Scalar(ArithmeticUrn, "exp", "EXP", "FUNCTION", FloatOf, c => c.Narrow($"EXP({c.V(0)})"), V("i64", "fp32", "fp64"));
        foreach (var (name, sql) in new[] { ("sin", "SIN"), ("cos", "COS"), ("tan", "TAN"), ("atan", "ATAN"), ("radians", "RADIANS"), ("degrees", "DEGREES") })
        {
            Scalar(ArithmeticUrn, name, sql, "FUNCTION", FloatOf, c => c.Narrow($"{sql}({c.V(0)})"), V(unaryFloats));
        }
        foreach (var (name, sql) in new[] { ("asin", "ASIN"), ("acos", "ACOS") })
        {
            Scalar(ArithmeticUrn, name, sql, "FUNCTION", FloatOf, c => c.Narrow($"{sql}({c.V(0)})"),
                new TSqlFunctionVariant(unaryFloats, ("on_domain_error", "ERROR")));
        }
        foreach (var (name, op) in new[] { ("bitwise_and", "&"), ("bitwise_or", "|"), ("bitwise_xor", "^") })
        {
            Scalar(ArithmeticUrn, name, op, "INFIX", SameAsFirst, c => $"({c.V(0)} {op} {c.V(1)})", V(ints));
        }
        Scalar(ArithmeticUrn, "bitwise_not", "~", "PREFIX", SameAsFirst, c => $"(~ {c.V(0)})", V(unaryInts));
        // RIGHT_SHIFT is a logical shift; an arithmetic shift of a negative number shifts its complement.
        Scalar(ArithmeticUrn, "shift_left", "LEFT_SHIFT", "FUNCTION", SameAsFirst, c => $"LEFT_SHIFT({c.V(0)}, {c.V(1)})", V("i32_i32", "i64_i32"));
        Scalar(ArithmeticUrn, "shift_right", "RIGHT_SHIFT", "FUNCTION", SameAsFirst,
            c => $"CASE WHEN {c.V(0)} < 0 THEN ~RIGHT_SHIFT(~{c.V(0)}, {c.V(1)}) ELSE RIGHT_SHIFT({c.V(0)}, {c.V(1)}) END", V("i32_i32", "i64_i32"));
        Scalar(ArithmeticUrn, "shift_right_unsigned", "RIGHT_SHIFT", "FUNCTION", SameAsFirst, c => $"RIGHT_SHIFT({c.V(0)}, {c.V(1)})", V("i32_i32", "i64_i32"));

        // ── functions_arithmetic: aggregate ─────────────────────────────
        static SType SumType(SType[] t) => t.Length > 0 && t[0].IsFloat ? SType.Fp64 : SType.I64;
        Aggregate(ArithmeticUrn, "sum", "SUM", SumType, c => $"SUM({c.DistinctPrefix}{Widened(c, c.AggArg(0))})", distinct: true,
            new TSqlFunctionVariant(unaryNumbers, error));
        Aggregate(ArithmeticUrn, "sum0", "SUM", t => SumType(t).WithNullable(false),
            c => $"COALESCE(SUM({c.DistinctPrefix}{Widened(c, c.AggArg(0))}), {(c.T(0).IsFloat ? "0E0" : "CAST(0 AS bigint)")})", distinct: true,
            new TSqlFunctionVariant(unaryNumbers, error));
        // AVG truncates integer averages, as Substrait specifies; the sum is taken as bigint.
        // [FABRICATOR-PATCH: OFFER-READY] unless the plan declares a floating result for an integer average (DuckDB
        // does): then the T-SQL AVG over bigint both truncated and overflowed. Average as DuckDB does — the exact
        // sum (decimal(38,0)) divided by the count, in float.
        Aggregate(ArithmeticUrn, "avg", "AVG", SameAsFirst, c => c.T(0).IsInteger && c.ResultType.IsFloat
            ? ExactAverage(c, $"CAST({c.AggArg(0)} AS decimal(38,0))")
            : c.T(0).IsInteger || c.T(0).Kind == TypeKind.Fp32
            ? c.Cast($"AVG({c.DistinctPrefix}{Widened(c, c.AggArg(0))})")
            : $"AVG({c.DistinctPrefix}{c.AggArg(0)})", distinct: true,
            new TSqlFunctionVariant(unaryInts, error), new TSqlFunctionVariant(unaryFloats, error));
        Aggregate(ArithmeticUrn, "min", "MIN", SameAsFirst, c => $"MIN({c.AggArg(0)})", distinct: true, V(unaryNumbers));
        Aggregate(ArithmeticUrn, "max", "MAX", SameAsFirst, c => $"MAX({c.AggArg(0)})", distinct: true, V(unaryNumbers));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] string min/max in DuckDB's order: aggregate the exact key, read the
        // string back from it (TSqlEmitter.StringKey).
        Aggregate(FabricatorStringUrn, "min", "MIN", SameAsFirst, c => TSqlEmitter.StringFromKey($"MIN({TSqlEmitter.StringKey(c.AggArg(0))})"), distinct: true, V("str"));
        Aggregate(FabricatorStringUrn, "max", "MAX", SameAsFirst, c => TSqlEmitter.StringFromKey($"MAX({TSqlEmitter.StringKey(c.AggArg(0))})"), distinct: true, V("str"));
        Aggregate(ArithmeticUrn, "product", "PRODUCT", SameAsFirst,
            c => $"COALESCE({c.Narrow($"PRODUCT({c.AggArg(0)})")}, {(c.T(0).IsFloat ? "1E0" : "1")})", distinct: false,
            new TSqlFunctionVariant(unaryInts, error), V("fp64"));
        Aggregate(ArithmeticUrn, "std_dev", "STDEV", FloatOfLast, c => Dispersion(c, "STDEV", "STDEVP"), distinct: true,
            V("req_fp32", "req_fp64"),
            new TSqlFunctionVariant(unaryFloats, ("distribution", "SAMPLE")),
            new TSqlFunctionVariant(unaryFloats, ("distribution", "POPULATION")));
        Aggregate(ArithmeticUrn, "variance", "VAR", FloatOfLast, c => Dispersion(c, "VAR", "VARP"), distinct: true,
            V("req_fp32", "req_fp64"),
            new TSqlFunctionVariant(unaryFloats, ("distribution", "SAMPLE")),
            new TSqlFunctionVariant(unaryFloats, ("distribution", "POPULATION")));
        Aggregate(ArithmeticUrn, "median", "APPROX_PERCENTILE_CONT", Real, Median, distinct: false, V("req_fp64"));

        // ── functions_arithmetic_decimal ────────────────────────────────
        // The result is cast to the type Substrait derives, which for division differs from SQL Server's.
        foreach (var (name, op) in new[] { ("add", "+"), ("subtract", "-"), ("multiply", "*"), ("divide", "/") })
        {
            // [FABRICATOR-PATCH: OURS-BY-DESIGN] SQL Server reduces the scale of a result whose precision exceeds 38 and rounds (measured:
            // decimal(10,2) * decimal(38,10)); where its scale falls below the plan's, the answer differs: decline
            Scalar(ArithmeticDecimalUrn, name, op, "INFIX", t => DecimalResult(name, t), c =>
            {
                var server = DecimalResult(name, c.ArgTypes);
                if (c.ResultType.Kind == TypeKind.Decimal && server.Kind == TypeKind.Decimal && server.Scale < c.ResultType.Scale)
                {
                    throw c.Unsupported($"SQL Server computes this at scale {server.Scale}, below the result's {c.ResultType.Scale}.");
                }
                return c.Cast($"({c.V(0)} {op} {c.V(1)})");
            },
                new TSqlFunctionVariant(["dec_dec"], error));
        }
        Scalar(ArithmeticDecimalUrn, "negate", "-", "PREFIX", SameAsFirst, c => $"(- {c.V(0)})", V("dec"));
        Scalar(ArithmeticDecimalUrn, "abs", "ABS", "FUNCTION", SameAsFirst, c => $"ABS({c.V(0)})", V("dec"));
        Aggregate(ArithmeticDecimalUrn, "sum", "SUM", t => DecimalOf(38, t), c => $"SUM({c.DistinctPrefix}{c.AggArg(0)})", distinct: true,
            new TSqlFunctionVariant(["dec"], error));
        Aggregate(ArithmeticDecimalUrn, "sum0", "SUM", t => DecimalOf(38, t).WithNullable(false), c => $"COALESCE(SUM({c.DistinctPrefix}{c.AggArg(0)}), 0)", distinct: true,
            new TSqlFunctionVariant(["dec"], error));
        // [FABRICATOR-PATCH: OFFER-READY] a floating result declared for a decimal average: the exact decimal sum
        // divided by the count, in float, instead of T-SQL's AVG at a fixed scale of 6.
        Aggregate(ArithmeticDecimalUrn, "avg", "AVG", t => DecimalOf(38, t), c => c.ResultType.IsFloat
            ? ExactAverage(c, c.AggArg(0))
            : c.Cast($"AVG({c.DistinctPrefix}{c.AggArg(0)})"), distinct: true,
            new TSqlFunctionVariant(["dec"], error));
        Aggregate(ArithmeticDecimalUrn, "min", "MIN", SameAsFirst, c => $"MIN({c.AggArg(0)})", distinct: true, V("dec"));
        Aggregate(ArithmeticDecimalUrn, "max", "MAX", SameAsFirst, c => $"MAX({c.AggArg(0)})", distinct: true, V("dec"));

        // ── functions_comparison ────────────────────────────────────────
        // Strings compare with the query's collation (binary by default), but SQL Server pads the
        // shorter string with spaces, and binary values with zeros, before comparing.
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] ...except that a string compares by its exact key (K), so neither padding nor
        // case nor UTF-16 order changes the answer; see TSqlEmitter.StringKey.
        Condition(ComparisonUrn, "equal", "=", "INFIX", c => Indexable(c, $"({KE(c, 0)} = {KE(c, 1)})"), V("any_any"));
        Condition(ComparisonUrn, "not_equal", "<>", "INFIX", c => $"({KE(c, 0)} <> {KE(c, 1)})", V("any_any"));
        foreach (var (name, op) in new[] { ("lt", "<"), ("lte", "<="), ("gt", ">"), ("gte", ">=") })
        {
            Condition(ComparisonUrn, name, op, "INFIX", c => Ordered(c, $"({K(c, 0)} {op} {K(c, 1)})"), V("any_any"));
        }
        Condition(ComparisonUrn, "between", "BETWEEN", "INFIX", c => Ordered(c, $"({K(c, 0)} BETWEEN {K(c, 1)} AND {K(c, 2)})"), V("any_any_any"));
        Condition(ComparisonUrn, "is_null", "IS NULL", "POSTFIX", c => $"({c.V(0)} IS NULL)", V("any"));
        Condition(ComparisonUrn, "is_not_null", "IS NOT NULL", "POSTFIX", c => $"({c.V(0)} IS NOT NULL)", V("any"));
        // SQL Server's float cannot hold NaN or infinity.
        Scalar(ComparisonUrn, "is_nan", "CASE", "FUNCTION", Bool, c => $"CASE WHEN {c.V(0)} IS NULL THEN NULL ELSE CAST(0 AS bit) END", V(unaryFloats));
        Scalar(ComparisonUrn, "is_finite", "CASE", "FUNCTION", Bool, c => $"CASE WHEN {c.V(0)} IS NULL THEN NULL ELSE CAST(1 AS bit) END", V(unaryFloats));
        Scalar(ComparisonUrn, "is_infinite", "CASE", "FUNCTION", Bool, c => $"CASE WHEN {c.V(0)} IS NULL THEN NULL ELSE CAST(0 AS bit) END", V(unaryFloats));
        Scalar(ComparisonUrn, "is_true", "CASE", "FUNCTION", Bool, c => $"CASE WHEN {c.P(0)} THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END", V("bool"));
        Scalar(ComparisonUrn, "is_not_true", "CASE", "FUNCTION", Bool, c => $"CASE WHEN {c.P(0)} THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END", V("bool"));
        Scalar(ComparisonUrn, "is_false", "CASE", "FUNCTION", Bool, c => $"CASE WHEN NOT {c.P(0)} THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END", V("bool"));
        Scalar(ComparisonUrn, "is_not_false", "CASE", "FUNCTION", Bool, c => $"CASE WHEN NOT {c.P(0)} THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END", V("bool"));
        Scalar(ComparisonUrn, "nullif", "NULLIF", "FUNCTION", SameAsFirst, c => $"NULLIF({c.V(0)}, {c.V(1)})", V("any_any"));
        Scalar(ComparisonUrn, "coalesce", "COALESCE", "FUNCTION", SameAsFirst, c => $"COALESCE({c.Join(", ")})",
            new TSqlFunctionVariant(["any"]) { VariadicMin = 2 });
        Condition(ComparisonUrn, "is_distinct_from", "IS DISTINCT FROM", "INFIX", c => $"({KE(c, 0)} IS DISTINCT FROM {KE(c, 1)})", V("any_any"));
        Condition(ComparisonUrn, "is_not_distinct_from", "IS NOT DISTINCT FROM", "INFIX", c => $"({KE(c, 0)} IS NOT DISTINCT FROM {KE(c, 1)})", V("any_any"));
        // LEAST and GREATEST skip nulls; Substrait's least and greatest return null if any argument is null.
        foreach (var (name, sql) in new[] { ("least", "LEAST"), ("greatest", "GREATEST") })
        {
            Scalar(ComparisonUrn, name, sql, "FUNCTION", SameAsFirst, c => NullIfAnyNull(c, $"{sql}({c.Join(", ")})"),
                new TSqlFunctionVariant(["any"]) { VariadicMin = 2, VariadicMax = MaxArguments });
            Scalar(ComparisonUrn, name + "_skip_null", sql, "FUNCTION", SameAsFirst, c => $"{sql}({c.Join(", ")})",
                new TSqlFunctionVariant(["any"]) { VariadicMin = 2, VariadicMax = MaxArguments });
        }

        // ── functions_boolean ───────────────────────────────────────────
        // T-SQL's AND, OR and NOT are three-valued, like Substrait's.
        Condition(BooleanUrn, "and", "AND", "INFIX", c => Logical(c, "AND", "(1 = 1)"), new TSqlFunctionVariant(["bool"]) { VariadicMin = 0 });
        Condition(BooleanUrn, "or", "OR", "INFIX", c => Logical(c, "OR", "(1 = 0)"), new TSqlFunctionVariant(["bool"]) { VariadicMin = 0 });
        Condition(BooleanUrn, "not", "NOT", "PREFIX", c => $"(NOT {c.P(0)})", V("bool"));
        Condition(BooleanUrn, "and_not", "AND NOT", "INFIX", c => $"({c.P(0)} AND NOT {c.P(1)})", V("bool_bool"));
        Condition(BooleanUrn, "xor", "<>", "INFIX", c => $"({c.V(0)} <> {c.V(1)})", V("bool_bool"));
        Aggregate(BooleanUrn, "bool_and", "MIN", Bool, c => $"CAST(MIN(CAST({c.AggArg(0)} AS tinyint)) AS bit)", distinct: false, V("bool"));
        Aggregate(BooleanUrn, "bool_or", "MAX", Bool, c => $"CAST(MAX(CAST({c.AggArg(0)} AS tinyint)) AS bit)", distinct: false, V("bool"));

        // ── functions_string ────────────────────────────────────────────
        var caseSensitive = ("case_sensitivity", "CASE_SENSITIVE");
        var caseInsensitive = ("case_sensitivity", "CASE_INSENSITIVE");
        Scalar(StringUrn, "concat", "CONCAT", "FUNCTION", Str, c => $"CONCAT({EmptyMax(c)}, {c.Join(", ")})",
            new TSqlFunctionVariant(["str"], ("null_handling", "IGNORE_NULLS")) { VariadicMin = 1, VariadicMax = MaxConcatArguments });
        Scalar(StringUrn, "concat", "+", "INFIX", Str, c => $"({EmptyMax(c)} + {c.Join(" + ")})",
            new TSqlFunctionVariant(["str"], ("null_handling", "ACCEPT_NULLS")) { VariadicMin = 1, VariadicMax = MaxArguments });
        Condition(StringUrn, "like", "LIKE", "INFIX", c => $"({c.S(0)} LIKE {LikePattern(c, 1, "", "", escapeWildcards: false)})",
            new TSqlFunctionVariant(["str_str"], caseSensitive), new TSqlFunctionVariant(["str_str"], caseInsensitive));
        Condition(StringUrn, "starts_with", "LIKE", "INFIX", c => $"({c.S(0)} LIKE {LikePattern(c, 1, "", "%", escapeWildcards: true)})",
            new TSqlFunctionVariant(["str_str"], caseSensitive), new TSqlFunctionVariant(["str_str"], caseInsensitive));
        Condition(StringUrn, "ends_with", "LIKE", "INFIX", c => $"({c.S(0)} LIKE {LikePattern(c, 1, "%", "", escapeWildcards: true)})",
            new TSqlFunctionVariant(["str_str"], caseSensitive), new TSqlFunctionVariant(["str_str"], caseInsensitive));
        Condition(StringUrn, "contains", "LIKE", "INFIX", c => $"({c.S(0)} LIKE {LikePattern(c, 1, "%", "%", escapeWildcards: true)})",
            new TSqlFunctionVariant(["str_str"], caseSensitive), new TSqlFunctionVariant(["str_str"], caseInsensitive));
        // SUBSTRING counts positions before the first character, as LEFT_OF_BEGINNING does.
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] positions and lengths decline: SQL Server counts UTF-16 code units where DuckDB counts code points
        // (measured on U+1D11E: LEN 2, SUBSTRING splits the surrogate pair, CHARINDEX one too far), and REVERSE reverses
        // code units where DuckDB keeps a base character and its combining marks together
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] a NEGATIVE start counts from the end in DuckDB and lies before the
        // beginning in T-SQL (measured), and a negative length errors in T-SQL: both must be constants >= 0
        Scalar(StringUrn, "substring", "SUBSTRING", "FUNCTION", Str,
            c => !IsNonNegativeConstant(c, 1) || (c.Count >= 3 && !IsNonNegativeConstant(c, 2))
                ? throw c.Unsupported("start and length must be constants of at least 0; DuckDB counts a negative start from the end.")
                : c.Emitter.Collate(c.Count >= 3
                ? $"SUBSTRING(({c.V(0)}) COLLATE {TSqlEmitter.CodePointCollation}, {c.V(1)}, {c.V(2)})"
                : $"SUBSTRING(({c.V(0)}) COLLATE {TSqlEmitter.CodePointCollation}, {c.V(1)}, 2147483647)"),
            new TSqlFunctionVariant(["str_i32_i32", "str_i32"], ("negative_start", "LEFT_OF_BEGINNING")));
        Scalar(StringUrn, "strpos", "CHARINDEX", "FUNCTION", Long, StrPos,
            new TSqlFunctionVariant(["str_str"], caseSensitive), new TSqlFunctionVariant(["str_str"], caseInsensitive));
        Scalar(StringUrn, "replace", "REPLACE", "FUNCTION", Str, c => $"REPLACE({c.MaxString(0)}, {c.S(1)}, {c.S(2)})",
            new TSqlFunctionVariant(["str_str_str"], caseSensitive), new TSqlFunctionVariant(["str_str_str"], caseInsensitive));
        Scalar(StringUrn, "repeat", "REPLICATE", "FUNCTION", Str, c => $"REPLICATE({c.MaxString(0)}, {c.V(1)})", V("str_i64"));
        Scalar(StringUrn, "reverse", "REVERSE", "FUNCTION", Str, c => throw c.Unsupported("SQL Server reverses UTF-16 code units."), V("str"));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] LOWER / UPPER are pushed although they are NOT exact — the one ACCEPTED
        // divergence (user decision 2026-10-25, documented in the README): measured over every BMP code point, LOWER
        // maps 298 of them differently from DuckDB and UPPER 308 (SQL Server's _100 collations carry Unicode 5.0 case
        // tables: ß, µ, ſ, Greek symbol forms, IPA letters, newer scripts). Latin letters, umlauts and accents agree.
        Scalar(StringUrn, "lower", "LOWER", "FUNCTION", Str, c => $"LOWER({c.V(0)})", new TSqlFunctionVariant(["str"], ("char_set", "UTF8")));
        Scalar(StringUrn, "upper", "UPPER", "FUNCTION", Str, c => $"UPPER({c.V(0)})", new TSqlFunctionVariant(["str"], ("char_set", "UTF8")));
        // LEN ignores trailing spaces; replacing spaces (with one character each) counts them.
        // a character appended keeps LEN from dropping trailing spaces; the code-point collation makes it count
        // code points, not UTF-16 code units
        Scalar(StringUrn, "char_length", "LEN", "FUNCTION", Long,
            c => $"CAST(LEN(({c.V(0)}) COLLATE {TSqlEmitter.CodePointCollation} + N'.') - 1 AS bigint)", V("str"));
        Scalar(StringUrn, "octet_length", "DATALENGTH", "FUNCTION", Long, c => $"CAST(DATALENGTH({Utf8(c)}) AS bigint)", V("str"));
        Scalar(StringUrn, "bit_length", "DATALENGTH", "FUNCTION", Long, c => $"(CAST(DATALENGTH({Utf8(c)}) AS bigint) * 8)", V("str"));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] LTRIM / RTRIM take their characters from SQL Server 2022 on
        Scalar(StringUrn, "ltrim", "LTRIM", "FUNCTION", Str, c => $"LTRIM({c.V(0)}, {OneSidedTrimCharacters(c)})", V("str_str"));
        Scalar(StringUrn, "rtrim", "RTRIM", "FUNCTION", Str, c => $"RTRIM({c.V(0)}, {OneSidedTrimCharacters(c)})", V("str_str"));
        Scalar(StringUrn, "trim", "TRIM", "FUNCTION", Str, c => $"TRIM({TrimCharacters(c)} FROM {c.V(0)})", V("str_str"));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] lpad / rpad as DuckDB defines them (measured): the fill repeats from its
        // start, a longer input is cut to the length from the RIGHT end, positions are code points. Length and fill
        // must be constants (length >= 0, fill non-empty — DuckDB fails on an empty one).
        Scalar(StringUrn, "lpad", "LPAD", "FUNCTION", Str, c => Pad(c, left: true), V("str_i32_str"));
        Scalar(StringUrn, "rpad", "RPAD", "FUNCTION", Str, c => Pad(c, left: false), V("str_i32_str"));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] LEFT / RIGHT count code points as SUBSTRING does (the code-point
        // collation); a negative count keeps all but that many in DuckDB and errors in T-SQL, so it must be a
        // constant >= 0
        Scalar(StringUrn, "left", "LEFT", "FUNCTION", Str, c => LeftOrRight(c, "LEFT"), V("str_i32"));
        Scalar(StringUrn, "right", "RIGHT", "FUNCTION", Str, c => LeftOrRight(c, "RIGHT"), V("str_i32"));
        var regexDefaults = new[] { ("multiline", "MULTILINE_DISABLED"), ("dotall", "DOTALL_DISABLED") };
        TSqlFunctionVariant RegexVariant(string[] impls, (string, string) caseOption) => new(impls, regexDefaults.Append(caseOption).ToArray());
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] regular expressions decline: measured, REGEXP_REPLACE replaces every match where DuckDB's
        // regexp_replace replaces the first, and positions are UTF-16 code units
        Scalar(StringUrn, "regexp_replace", "REGEXP_REPLACE", "FUNCTION", Str, c => throw c.Unsupported("SQL Server's regular expressions differ from DuckDB's."),
            RegexVariant(["str_str_str", "str_str_str_i64_i64"], caseSensitive), RegexVariant(["str_str_str", "str_str_str_i64_i64"], caseInsensitive));
        Scalar(StringUrn, "regexp_match_substring", "REGEXP_SUBSTR", "FUNCTION", Str,
            c => throw c.Unsupported("SQL Server's regular expressions differ from DuckDB's."),
            RegexVariant(["str_str", "str_str_i64_i64_i64"], caseSensitive), RegexVariant(["str_str", "str_str_i64_i64_i64"], caseInsensitive));
        Scalar(StringUrn, "regexp_count_substring", "REGEXP_COUNT", "FUNCTION", Long,
            c => throw c.Unsupported("SQL Server's regular expressions differ from DuckDB's."),
            RegexVariant(["str_str", "str_str_i64"], caseSensitive), RegexVariant(["str_str", "str_str_i64"], caseInsensitive));
        Scalar(StringUrn, "regexp_strpos", "REGEXP_INSTR", "FUNCTION", Long,
            c => throw c.Unsupported("SQL Server's regular expressions differ from DuckDB's."),
            RegexVariant(["str_str_i64_i64"], caseSensitive), RegexVariant(["str_str_i64_i64"], caseInsensitive));
        list.Add(new TSqlFunction(StringUrn, "string_agg", TSqlFunctionKind.Aggregate, "STRING_AGG", "FUNCTION", [V("str_str")], Str, StringAgg, predicate: false)
        {
            AllowsSorts = true,
        });

        // ── functions_datetime ──────────────────────────────────────────
        Scalar(DatetimeUrn, "extract", "DATEPART", "FUNCTION", Long, Extract,
            V("req_pts", "req_ptstz_str", "req_date", "req_pt", "req_req_pts", "req_req_ptstz_str", "req_req_date"));
        Condition(DatetimeUrn, "extract_boolean", "DATEPART", "FUNCTION", ExtractBoolean, V("req_pts", "req_ptstz_str", "req_date"));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] DuckDB's date_diff counts unit boundaries like DATEDIFF_BIG — measured on
        // 2000 pairs, timestamps and dates, for every unit but the week, which DuckDB counts as whole days / 7
        // (truncated toward zero, also measured); last_day is EOMONTH, make_date DATEFROMPARTS (a year past 9999 is
        // an error there and an answer in DuckDB).
        Scalar(FabricatorDatetimeUrn, "date_diff", "DATEDIFF_BIG", "FUNCTION", Long, DateDiff, V("req_pts_pts", "req_date_date"));
        Scalar(FabricatorDatetimeUrn, "last_day", "EOMONTH", "FUNCTION", t => new SType(TypeKind.Date), c => $"EOMONTH({c.V(0)})", V("date", "pts"));
        Scalar(FabricatorDatetimeUrn, "make_date", "DATEFROMPARTS", "FUNCTION", t => new SType(TypeKind.Date),
            c => $"DATEFROMPARTS({c.V(0)}, {c.V(1)}, {c.V(2)})", V("i64_i64_i64"));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] round_calendar rounding DOWN to one unit (DuckDB's date_trunc) as DATETRUNC
        Scalar(DatetimeUrn, "round_calendar", "DATETRUNC", "FUNCTION", SameAsFirst, RoundCalendar, V("pts_req_req_req_i64"));
        Scalar(DatetimeUrn, "add", "DATEADD", "FUNCTION", DatetimeArithmeticType, c => DatetimeArithmetic(c, negate: false),
            V("pts_iday", "ptstz_iday", "date_iday", "pts_iyear", "ptstz_iyear_str", "date_iyear"));
        Scalar(DatetimeUrn, "subtract", "DATEADD", "FUNCTION", DatetimeArithmeticType, c => DatetimeArithmetic(c, negate: true),
            V("pts_iday", "ptstz_iday", "date_iday", "pts_iyear", "ptstz_iyear_str", "date_iyear"));
        foreach (var (name, op) in new[] { ("lt", "<"), ("lte", "<="), ("gt", ">"), ("gte", ">=") })
        {
            Condition(DatetimeUrn, name, op, "INFIX", c => $"({c.V(0)} {op} {c.V(1)})", V("pts_pts", "ptstz_ptstz", "date_date"));
        }
        Scalar(DatetimeUrn, "assume_timezone", "TODATETIMEOFFSET", "FUNCTION", t => new SType(TypeKind.PrecisionTimestampTz, precision: SameAsFirst(t).Precision),
            c => $"TODATETIMEOFFSET({c.V(0)}, '{Offset(c, 1)}')", V("pts_str"));
        Scalar(DatetimeUrn, "local_timestamp", "SWITCHOFFSET", "FUNCTION", t => new SType(TypeKind.PrecisionTimestamp, precision: SameAsFirst(t).Precision),
            c => $"CAST(SWITCHOFFSET({c.V(0)}, '{Offset(c, 1)}') AS datetime2({Math.Min(c.T(0).Precision, TSql.MaxTimePrecision)}))", V("ptstz_str"));
        Aggregate(DatetimeUrn, "min", "MIN", SameAsFirst, c => $"MIN({c.AggArg(0)})", distinct: true, V("date", "pt", "pts", "ptstz"));
        Aggregate(DatetimeUrn, "max", "MAX", SameAsFirst, c => $"MAX({c.AggArg(0)})", distinct: true, V("date", "pt", "pts", "ptstz"));

        // ── functions_rounding ──────────────────────────────────────────
        Scalar(RoundingUrn, "ceil", "CEILING", "FUNCTION", SameAsFirst, c => c.Narrow($"CEILING({c.V(0)})"), V(unaryFloats));
        Scalar(RoundingUrn, "floor", "FLOOR", "FUNCTION", SameAsFirst, c => c.Narrow($"FLOOR({c.V(0)})"), V(unaryFloats));
        // ROUND breaks ties away from zero (measured, also for float); its third argument truncates.
        string[] roundImpls = ["i16_i32", "i32_i32", "i64_i32", "fp32_i32", "fp64_i32"];
        Scalar(RoundingUrn, "round", "ROUND", "FUNCTION", SameAsFirst, c => c.Narrow(Round(c, c.V(0))),
            new TSqlFunctionVariant(roundImpls, ("rounding", "TIE_AWAY_FROM_ZERO")), new TSqlFunctionVariant(roundImpls, ("rounding", "TRUNCATE")));

        // ── functions_rounding_decimal ──────────────────────────────────
        foreach (var (name, sql) in new[] { ("ceil", "CEILING"), ("floor", "FLOOR") })
        {
            Scalar(RoundingDecimalUrn, name, sql, "FUNCTION", t => new SType(TypeKind.Decimal, precision: Math.Min(SameAsFirst(t).Precision - SameAsFirst(t).Scale + 1, 38)),
                c => c.Cast($"{sql}({c.V(0)})"), V("dec"));
        }
        static SType RoundedDecimal(SType[] t) => new(TypeKind.Decimal, precision: Math.Min(SameAsFirst(t).Precision + 1, 38), scale: SameAsFirst(t).Scale);
        Scalar(RoundingDecimalUrn, "round", "ROUND", "FUNCTION", RoundedDecimal, c => Round(c, $"CAST({c.V(0)} AS {TSql.RequireTypeName(RoundedDecimal(c.ArgTypes))})"),
            new TSqlFunctionVariant(["dec_i32"], ("rounding", "TIE_AWAY_FROM_ZERO")), new TSqlFunctionVariant(["dec_i32"], ("rounding", "TRUNCATE")));

        // ── functions_logarithmic ───────────────────────────────────────
        var logErrors = new[] { ("on_domain_error", "ERROR"), ("on_log_zero", "ERROR") };
        string[] logImpls = ["i64", "fp32", "fp64", "dec"];
        Scalar(LogarithmicUrn, "ln", "LOG", "FUNCTION", FloatOf, c => c.Narrow($"LOG({c.V(0)})"), new TSqlFunctionVariant(logImpls, logErrors));
        Scalar(LogarithmicUrn, "log10", "LOG10", "FUNCTION", FloatOf, c => c.Narrow($"LOG10({c.V(0)})"), new TSqlFunctionVariant(logImpls, logErrors));
        Scalar(LogarithmicUrn, "log2", "LOG", "FUNCTION", FloatOf, c => c.Narrow($"LOG({c.V(0)}, 2)"), new TSqlFunctionVariant(logImpls, logErrors));
        Scalar(LogarithmicUrn, "logb", "LOG", "FUNCTION", FloatOf, c => c.Narrow($"LOG({c.V(0)}, {c.V(1)})"),
            new TSqlFunctionVariant(["i64_i64", "fp32_fp32", "fp64_fp64", "dec_dec"], logErrors));

        // ── functions_aggregate_generic / functions_aggregate_approx ────
        Aggregate(AggregateGenericUrn, "count", "COUNT_BIG", t => SType.I64.WithNullable(false), Count, distinct: true,
            new TSqlFunctionVariant(["any", ""], error));
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] any_value declines: DuckDB returns ONE of the values (first() in practice) and MAX a different one
        Aggregate(AggregateGenericUrn, "any_value", "MAX", SameAsFirst,
            c => throw c.Unsupported("any_value picks a value other than DuckDB's."), distinct: false,
            new TSqlFunctionVariant(["any"], ("ignore_nulls", "TRUE")), new TSqlFunctionVariant(["any"], ("ignore_nulls", "FALSE")));
        Aggregate(AggregateApproxUrn, "approx_count_distinct", "APPROX_COUNT_DISTINCT", t => SType.I64.WithNullable(false),
            c => $"APPROX_COUNT_DISTINCT({c.AggArg(0)})", distinct: false, V("any"));

        return list;
    }

    private static SType FloatOfLast(SType[] t) => t.Length > 0 && t[t.Length - 1].Kind == TypeKind.Fp32 ? new SType(TypeKind.Fp32) : SType.Fp64;

    private static SType DecimalOf(int precision, SType[] t) => new(TypeKind.Decimal, precision: precision, scale: SameAsFirst(t).Scale);

    /// <summary>The result type Substrait derives for decimal arithmetic (functions_arithmetic_decimal.yaml).</summary>
    private static SType DecimalResult(string function, SType[] t)
    {
        if (t.Length < 2)
        {
            return SType.Unknown;
        }
        int p1 = t[0].Precision, s1 = t[0].Scale, p2 = t[1].Precision, s2 = t[1].Scale;
        int initScale, initPrecision;
        switch (function)
        {
            case "multiply":
                initScale = s1 + s2;
                initPrecision = p1 + p2 + 1;
                break;
            case "divide":
                initScale = Math.Max(6, s1 + p2 + 1);
                initPrecision = p1 - s1 + p2 + initScale;
                break;
            default:
                initScale = Math.Max(s1, s2);
                initPrecision = initScale + Math.Max(p1 - s1, p2 - s2) + 1;
                break;
        }
        int minScale = Math.Min(initScale, 6);
        int delta = initPrecision - 38;
        int precision = Math.Min(initPrecision, 38);
        int scale = initPrecision > 38 ? Math.Max(initScale - delta, minScale) : initScale;
        return new SType(TypeKind.Decimal, precision: precision, scale: scale);
    }

    #endregion

    #region Writers

    /// <summary>Widens an aggregate's argument so that sums are taken as bigint (integers) or float (fp32).</summary>
    private static string Widened(TSqlCall c, string value) => c.T(0).Kind switch
    {
        TypeKind.I16 or TypeKind.I32 => $"CAST({value} AS bigint)",
        TypeKind.Fp32 => $"CAST({value} AS float)",
        _ => value,
    };

    private static string Divide(TSqlCall c)
    {
        string divisor = c.Options["on_division_by_zero"] == "NULL" ? $"NULLIF({c.V(1)}, 0)" : c.V(1);
        return $"({c.V(0)} / {divisor})";
    }

    private static string Modulus(TSqlCall c)
    {
        string divisor = c.Options["on_domain_error"] == "NULL" ? $"NULLIF({c.V(1)}, 0)" : c.V(1);
        string remainder = $"({c.V(0)} % {divisor})";
        if (c.Options["division_type"] == "TRUNCATE")
        {
            return remainder;
        }
        // A floored remainder has the divisor's sign: move a remainder of the other sign by one divisor.
        return $"CASE WHEN SIGN({remainder}) * SIGN({c.V(1)}) < 0 THEN {remainder} + {c.V(1)} ELSE {remainder} END";
    }

    private static string Ordered(TSqlCall c, string comparison)
    {
        // SQL Server orders uniqueidentifier by its last bytes first.
        if (c.ArgTypes.Any(t => t.Kind == TypeKind.Uuid))
        {
            throw c.Unsupported("ordering comparisons on uuid values are not supported.");
        }
        return comparison;
    }

    private static string NullIfAnyNull(TSqlCall c, string value)
    {
        var nullable = Enumerable.Range(0, c.Count).Where(c.MayBeNull).ToList();
        if (nullable.Count == 0)
        {
            return value;
        }
        return $"CASE WHEN {string.Join(" OR ", nullable.Select(i => $"{c.V(i)} IS NULL"))} THEN NULL ELSE {value} END";
    }

    private static string Logical(TSqlCall c, string op, string identity)
    {
        if (c.Count == 0)
        {
            return identity;
        }
        if (c.Count == 1)
        {
            return c.P(0);
        }
        return "(" + string.Join($" {op} ", Enumerable.Range(0, c.Count).Select(c.P)) + ")";
    }

    private static string Dispersion(TSqlCall c, string sample, string population)
    {
        // The distribution is an enumeration argument (current signature) or an option (deprecated one).
        string distribution;
        int valueIndex;
        if (c.Args.Count > 0 && c.Args[0].Enum != null)
        {
            distribution = c.Args[0].Enum!;
            valueIndex = 1;
        }
        else
        {
            distribution = c.Options.TryGetValue("distribution", out var d) ? d : "SAMPLE";
            valueIndex = 0;
        }
        string function = distribution switch
        {
            "SAMPLE" => sample,
            "POPULATION" => population,
            _ => throw c.Unsupported($"unknown distribution '{distribution}'."),
        };
        return c.Narrow($"{function}({c.DistinctPrefix}{c.AggArg(valueIndex)})");
    }

    private static string Median(TSqlCall c)
    {
        // APPROX_PERCENTILE_CONT is an estimate; T-SQL's exact PERCENTILE_CONT is only a window function.
        if (c.Enum(0) != "APPROXIMATE")
        {
            throw c.Unsupported("only the APPROXIMATE median is supported.");
        }
        return $"APPROX_PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY {c.AggArg(1)})";
    }

    private static string Count(TSqlCall c)
    {
        if (c.Count == 0)
        {
            return c.Filter == null ? "COUNT_BIG(*)" : $"COUNT_BIG(CASE WHEN {c.FilterPredicate()} THEN 1 END)";
        }
        if (c.Distinct && c.T(0).IsBinaryLike)
        {
            TSqlEmitter.Comparable(c.T(0), "");
        }
        if (c.Distinct && c.T(0).IsStringLike) // [FABRICATOR-PATCH: OURS-BY-DESIGN] distinct strings by their exact key
        {
            return $"COUNT_BIG(DISTINCT {c.Emitter.EqualityKey(c.AggArg(0))})";
        }
        return $"COUNT_BIG({c.DistinctPrefix}{c.AggArg(0)})";
    }

    private static string AnyValue(TSqlCall c)
    {
        // Any value will do; MAX returns one that is not null when there is one.
        if (c.T(0).Kind == TypeKind.Bool)
        {
            return $"CAST(MAX(CAST({c.AggArg(0)} AS tinyint)) AS bit)";
        }
        return $"MAX({c.AggArg(0)})";
    }

    private static string StringAgg(TSqlCall c)
    {
        // The separator must be a literal (or a variable) in T-SQL.
        string? separator = c.StringLiteral(1);
        if (separator == null)
        {
            throw c.Unsupported("the separator must be a string literal.");
        }
        string value = c.Filter == null ? c.MaxString(0) : $"CASE WHEN {c.FilterPredicate()} THEN {c.MaxString(0)} END";
        string sql = $"STRING_AGG({value}, {TSql.Unicode(separator)})";
        if (c.Sorts.Count > 0)
        {
            string order = c.OrderBy();
            if (order.Length > 0)
            {
                sql += $" WITHIN GROUP (ORDER BY {order})";
            }
        }
        return sql;
    }

    /// <summary>An empty nvarchar(max), so that concatenation is not truncated at 8000 bytes.</summary>
    private static string EmptyMax(TSqlCall c) => c.Emitter.Collate("CAST(N'' AS nvarchar(max))");

    /// <summary>The characters to trim, which T-SQL refuses as nvarchar(max) (error 536).</summary>
    private static string TrimCharacters(TSqlCall c)
        => c.StringLiteral(1) is { Length: <= 4000 } ? c.V(1) : c.Emitter.Collate($"CAST({c.V(1)} AS nvarchar(4000))");

    private static string OneSidedTrimCharacters(TSqlCall c)
        => c.Emitter.SupportsTrimCharacters ? TrimCharacters(c) : throw c.Unsupported("LTRIM and RTRIM take characters from SQL Server 2022 on.");

    private static string LeftOrRight(TSqlCall c, string function)
        => !IsNonNegativeConstant(c, 1)
            ? throw c.Unsupported("the count must be a constant of at least 0; DuckDB keeps all but that many for a negative one.")
            : c.Emitter.Collate($"{function}(({c.V(0)}) COLLATE {TSqlEmitter.CodePointCollation}, {c.V(1)})");

    /// <summary>The string as UTF-8 bytes (varchar in a UTF-8 collation).</summary>
    private static string Utf8(TSqlCall c) => $"CAST(({c.V(0)}) COLLATE {TSqlEmitter.BinaryCollation} AS varchar(max))";

    /// <summary>
    /// A LIKE pattern for argument <paramref name="index"/>. T-SQL's LIKE also
    /// treats [ as a wildcard, which standard LIKE patterns don't have, so it
    /// is escaped; with <paramref name="escapeWildcards"/>, % and _ too, for
    /// functions that match a plain substring.
    /// </summary>
    private static string LikePattern(TSqlCall c, int index, string prefix, string suffix, bool escapeWildcards)
    {
        string? collation = c.CaseCollation();

        string? literal = c.StringLiteral(index);
        if (!escapeWildcards && (literal == null || literal.Contains('_')))
        {
            // [FABRICATOR-PATCH: OURS-BY-DESIGN] LIKE's `_` matches one UTF-16 code unit on the server and one code point in DuckDB
            // (measured: N'𝄞' LIKE N'_' is false on the server); a pattern that is not a constant may hold one
            throw c.Unsupported("a LIKE pattern with `_`, or one that is not a constant, is not supported; SQL Server's `_` matches a UTF-16 code unit.");
        }
        if (literal != null)
        {
            string escaped = literal.Replace("[", "[[]");
            if (escapeWildcards)
            {
                escaped = escaped.Replace("%", "[%]").Replace("_", "[_]");
            }
            // An nvarchar pattern makes LIKE compare as Unicode, where trailing spaces count.
            string text = TSql.Unicode(prefix + escaped + suffix);
            return collation == null ? c.Emitter.Collate(text) : $"{text} COLLATE {collation}";
        }

        string pattern = $"REPLACE({c.V(index)}, N'[', N'[[]')";
        if (escapeWildcards)
        {
            pattern = $"REPLACE(REPLACE({pattern}, N'%', N'[%]'), N'_', N'[_]')";
        }
        if (prefix.Length > 0)
        {
            pattern = $"{TSql.Unicode(prefix)} + {pattern}";
        }
        if (suffix.Length > 0)
        {
            pattern = $"{pattern} + {TSql.Unicode(suffix)}";
        }
        return collation == null ? "(" + pattern + ")" : $"({pattern}) COLLATE {collation}";
    }

    private static string StrPos(TSqlCall c)
    {
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] CHARINDEX searches under the query's binary collation and answers in
        // UTF-16 code units; the code points before the match are counted under the code-point collation
        string unit = $"CHARINDEX({c.S(1)}, {c.S(0)})";
        string position = $"CAST(CASE WHEN {unit} = 0 THEN 0 ELSE LEN((LEFT({c.S(0)}, {unit} - 1)) COLLATE {TSqlEmitter.CodePointCollation} + N'.') END AS bigint)";
        if (c.StringLiteral(1) is { Length: > 0 })
        {
            return position;
        }
        // An empty string is found at position 1, where CHARINDEX says 0.
        return $"CASE WHEN {c.V(0)} IS NULL THEN NULL WHEN DATALENGTH({c.V(1)}) = 0 THEN CAST(1 AS bigint) ELSE {position} END";
    }

    #region Regular expressions

    // SQL Server's regular expressions are RE2; Substrait's are ICU. Most syntax is shared; the
    // constructs below are ICU-only (RE2 rejects them or reads them differently), so patterns
    // using them are refused rather than run with another meaning.
    private static readonly Regex s_icuOnly = new(@"\(\?[=!>#]|\(\?<[=!]|\\[1-9kGZRXhHVvN]|[*+?}]\+", RegexOptions.CultureInvariant);

    private static string RegexPattern(TSqlCall c)
    {
        string? pattern = c.StringLiteral(1);
        if (pattern == null)
        {
            throw c.Unsupported("the pattern must be a string literal, so that it can be checked for ICU-only syntax.");
        }
        var match = s_icuOnly.Match(pattern);
        if (match.Success)
        {
            throw c.Unsupported($"the pattern uses '{match.Value}', which SQL Server's RE2 regular expressions do not support with ICU's meaning.");
        }
        return TSql.Unicode(pattern);
    }

    private static string RegexInt(TSqlCall c, int index, string missing) => index < c.Count ? c.V(index) : missing;

    private static string RegexFlags(TSqlCall c)
        => c.Options["case_sensitivity"] == "CASE_INSENSITIVE" ? "'i'" : "'c'";

    private static string RegexpReplace(TSqlCall c)
    {
        string? replacement = c.StringLiteral(2);
        if (replacement == null)
        {
            throw c.Unsupported("the replacement must be a string literal.");
        }
        // ICU refers to groups as $n and escapes with \; RE2 refers to them as \n.
        var sb = new StringBuilder();
        for (int i = 0; i < replacement.Length; i++)
        {
            char ch = replacement[i];
            if (ch == '\\' && i + 1 < replacement.Length)
            {
                char next = replacement[++i];
                sb.Append(next == '\\' ? "\\\\" : next.ToString());
            }
            else if (ch == '$' && i + 1 < replacement.Length && char.IsDigit(replacement[i + 1]))
            {
                sb.Append('\\').Append(replacement[++i]);
            }
            else if (ch == '$' || ch == '\\')
            {
                throw c.Unsupported($"the replacement uses '{ch}' in a way that cannot be translated.");
            }
            else
            {
                sb.Append(ch);
            }
        }
        return $"REGEXP_REPLACE({c.V(0)}, {RegexPattern(c)}, {TSql.Unicode(sb.ToString())}, {RegexInt(c, 3, "1")}, {RegexInt(c, 4, "0")}, {RegexFlags(c)})";
    }

    #endregion

    #region Date and time

    private static readonly HashSet<string> s_utcNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UTC", "Etc/UTC", "Etc/UCT", "UCT", "GMT", "Etc/GMT", "Etc/GMT0", "Etc/GMT+0", "Etc/GMT-0", "GMT0", "Universal", "Etc/Universal", "Zulu", "Etc/Zulu", "Z",
    };

    private static readonly Regex s_offset = new(@"^([+-])(\d{2}):?(\d{2})$", RegexOptions.CultureInvariant);
    private static readonly Regex s_etcGmt = new(@"^Etc/GMT([+-])(\d{1,2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The UTC offset for a time zone argument, which must be a literal. SQL
    /// Server knows Windows time zone names only, so only zones with a fixed
    /// offset are supported: UTC, Etc/GMT±n and ±hh:mm.
    /// </summary>
    private static string Offset(TSqlCall c, int index)
    {
        string? zone = c.StringLiteral(index);
        if (zone == null)
        {
            throw c.Unsupported("the time zone must be a string literal.");
        }
        if (s_utcNames.Contains(zone))
        {
            return "+00:00";
        }
        var m = s_offset.Match(zone);
        if (m.Success && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) <= 14 && int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) < 60)
        {
            return $"{m.Groups[1].Value}{m.Groups[2].Value}:{m.Groups[3].Value}";
        }
        m = s_etcGmt.Match(zone);
        if (m.Success && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) <= 14)
        {
            // POSIX-style names invert the sign: Etc/GMT+5 is five hours behind UTC.
            return $"{(m.Groups[1].Value == "+" ? "-" : "+")}{int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture):00}:00";
        }
        throw c.Unsupported($"time zone '{zone}' is not supported; only zones with a fixed UTC offset are (SQL Server knows Windows time zone names only).");
    }

    private static readonly HashSet<string> s_timeComponents = new(StringComparer.Ordinal)
    {
        "HOUR", "MINUTE", "SECOND", "MILLISECOND", "MICROSECOND", "SUBSECOND",
    };

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] round_calendar(x, FLOOR, unit, origin, multiple) for the forms date_trunc
    /// produces, as DATETRUNC (SQL Server 2022+). The week is the ISO week (Monday), never DATEFIRST's.
    /// </summary>
    private static string RoundCalendar(TSqlCall c)
    {
        if (!c.Emitter.SupportsDateTrunc)
        {
            throw c.Unsupported("DATETRUNC needs SQL Server 2022 or later.");
        }
        long? multiple = c.Literal(4) is { } lit && lit.Type.IsInteger ? lit.IntValue : null;
        string? part = (c.Enum(1), c.Enum(2), c.Enum(3), multiple) switch
        {
            ("FLOOR", "YEAR", "YEAR", 1) => "year",
            ("FLOOR", "MONTH", "YEAR", 3) => "quarter",
            ("FLOOR", "MONTH", "YEAR", 1) => "month",
            ("FLOOR", "WEEK", "ISO_WEEK", 1) => "iso_week",
            ("FLOOR", "DAY", _, 1) => "day",
            ("FLOOR", "HOUR", "DAY", 1) => "hour",
            ("FLOOR", "MINUTE", "HOUR", 1) => "minute",
            ("FLOOR", "SECOND", "MINUTE", 1) => "second",
            _ => null,
        };
        return part == null
            ? throw c.Unsupported("only rounding down to one calendar unit is supported.")
            : $"DATETRUNC({part}, {c.V(0)})";
    }

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] DuckDB's date_diff (see the registration for the measurement).</summary>
    private static string DateDiff(TSqlCall c)
    {
        string unit = c.Enum(0) switch
        {
            "YEAR" => "year",
            "QUARTER" => "quarter",
            "MONTH" => "month",
            "WEEK" => "week",
            "DAY" => "day",
            "HOUR" => "hour",
            "MINUTE" => "minute",
            "SECOND" => "second",
            "MILLISECOND" => "millisecond",
            "MICROSECOND" => "microsecond",
            _ => throw c.Unsupported($"date_diff unit '{c.Enum(0)}' is not supported."),
        };
        return unit == "week"
            ? $"(DATEDIFF_BIG(day, {c.V(1)}, {c.V(2)}) / 7)"
            : $"DATEDIFF_BIG({unit}, {c.V(1)}, {c.V(2)})";
    }

    private static string Extract(TSqlCall c)
    {
        string component = c.Enum(0);
        int valueIndex = 1;
        int offset = 0;
        if (c.Args.Count > 1 && c.Args[1].Enum != null)
        {
            offset = c.Args[1].Enum switch
            {
                "ONE" => 0,
                "ZERO" => -1,
                _ => throw c.Unsupported($"unknown indexing '{c.Args[1].Enum}'."),
            };
            valueIndex = 2;
        }

        var type = c.T(valueIndex);
        string value = c.V(valueIndex);
        if (type.Kind == TypeKind.PrecisionTimestampTz)
        {
            if (component == "UNIX_TIME")
            {
                return $"DATEDIFF_BIG(second, CAST('1970-01-01T00:00:00+00:00' AS datetimeoffset), {value})";
            }
            // Components are those of the local time in the given zone.
            value = $"CAST(SWITCHOFFSET({value}, '{Offset(c, valueIndex + 1)}') AS datetime2(7))";
        }
        if (type.Kind == TypeKind.PrecisionTime && !s_timeComponents.Contains(component))
        {
            throw c.Unsupported($"extracting '{component}' from a time is not valid.");
        }

        // Day of the week from 0 (Monday) to 6, whatever DATEFIRST is: 1900-01-01 was a Monday.
        string monday = $"((DATEDIFF(day, '19000101', {value}) % 7 + 7) % 7)";
        string sunday = $"(({monday} + 1) % 7)";
        string Part(string part) => $"DATEPART({part}, {value})";
        string Indexed(string sql) => offset == 0 ? sql : $"({sql} - 1)";

        string result = component switch
        {
            "YEAR" => Part("year"),
            // ISO years and weeks are those of the week's Thursday; US ones those of the week's Wednesday.
            "ISO_YEAR" => $"DATEPART(year, DATEADD(day, 3 - {monday}, {value}))",
            "US_YEAR" => $"DATEPART(year, DATEADD(day, 3 - {sunday}, {value}))",
            "QUARTER" => Indexed(Part("quarter")),
            "MONTH" => Indexed(Part("month")),
            "DAY" => Indexed(Part("day")),
            "DAY_OF_YEAR" => Indexed(Part("dayofyear")),
            "MONDAY_DAY_OF_WEEK" => offset == 0 ? $"({monday} + 1)" : monday,
            "SUNDAY_DAY_OF_WEEK" => offset == 0 ? $"({sunday} + 1)" : sunday,
            "ISO_WEEK" => Indexed(Part("iso_week")),
            "US_WEEK" => Indexed($"((DATEPART(dayofyear, DATEADD(day, 3 - {sunday}, {value})) - 1) / 7 + 1)"),
            "HOUR" => Part("hour"),
            "MINUTE" => Part("minute"),
            "SECOND" => Part("second"),
            // DATEPART gives the whole fraction of a second in the unit asked for.
            "MILLISECOND" => Part("millisecond"),
            "MICROSECOND" => $"({Part("microsecond")} % 1000)",
            "NANOSECOND" => $"({Part("nanosecond")} % 1000)",
            "SUBSECOND" => Part("microsecond"),
            "UNIX_TIME" => $"DATEDIFF_BIG(second, '19700101', {value})",
            _ => throw c.Unsupported($"extracting '{component}' is not supported."),
        };
        return $"CAST({result} AS bigint)";
    }

    private static string ExtractBoolean(TSqlCall c)
    {
        if (c.Enum(0) != "IS_LEAP_YEAR")
        {
            throw c.Unsupported($"extracting '{c.Enum(0)}' is not supported.");
        }
        string value = c.V(1);
        if (c.T(1).Kind == TypeKind.PrecisionTimestampTz)
        {
            value = $"SWITCHOFFSET({value}, '{Offset(c, 2)}')";
        }
        string year = $"DATEPART(year, {value})";
        return $"(({year} % 4 = 0 AND {year} % 100 <> 0) OR {year} % 400 = 0)";
    }

    private static SType DatetimeArithmeticType(SType[] t)
    {
        var first = SameAsFirst(t);
        // A date plus a day-time interval is a timestamp with the interval's precision.
        if (first.Kind == TypeKind.Date && t.Length > 1 && t[1].Kind == TypeKind.IntervalDay)
        {
            return new SType(TypeKind.PrecisionTimestamp, precision: t[1].Precision);
        }
        return first;
    }

    private static string DatetimeArithmetic(TSqlCall c, bool negate)
    {
        // SQL Server has no interval type: the interval must be a literal, applied with DATEADD.
        var interval = c.Literal(1);
        if (interval == null || !(interval.Type.Kind is TypeKind.IntervalDay or TypeKind.IntervalYear))
        {
            throw c.Unsupported("the interval must be a literal; SQL Server has no interval type.");
        }
        string value = c.V(0);
        if (interval.Type.Kind == TypeKind.IntervalYear)
        {
            long months = interval.Years * 12 + interval.Months;
            if (negate)
            {
                months = -months;
            }
            if (c.T(0).Kind == TypeKind.PrecisionTimestampTz)
            {
                // Calendar arithmetic happens in the given time zone.
                value = $"SWITCHOFFSET({value}, '{Offset(c, 2)}')";
            }
            return months == 0 ? value : $"DATEADD(month, {CheckedInt(c, months)}, {value})";
        }

        if (c.T(0).Kind == TypeKind.Date)
        {
            value = $"CAST({value} AS {TSql.RequireTypeName(c.ResultType)})";
        }
        const long nanosPerSecond = 1_000_000_000;
        int precision = interval.SubsecondPrecision;
        if (precision < 0 || precision > 9)
        {
            throw c.Unsupported($"interval precision {precision} is not supported.");
        }
        var total = (new System.Numerics.BigInteger(interval.Days) * 86_400 + interval.Seconds) * nanosPerSecond
            + new System.Numerics.BigInteger(interval.Subseconds) * TSqlEmitter.Pow10(9 - precision);
        if (negate)
        {
            total = -total;
        }
        int sign = total.Sign;
        var magnitude = System.Numerics.BigInteger.Abs(total);
        var days = magnitude / (86_400 * nanosPerSecond);
        var rest = magnitude % (86_400 * nanosPerSecond);
        long seconds = (long)(rest / nanosPerSecond);
        long nanos = (long)(rest % nanosPerSecond);

        if (nanos % 100 != 0)
        {
            // DATEADD would round to datetime2's 100 ns.
            throw c.Unsupported("the interval has sub-100ns precision, which SQL Server cannot represent.");
        }
        if (nanos != 0)
        {
            value = nanos % 1000 == 0
                ? $"DATEADD(microsecond, {sign * (nanos / 1000)}, {value})"
                : $"DATEADD(nanosecond, {sign * nanos}, {value})";
        }
        if (seconds != 0)
        {
            value = $"DATEADD(second, {sign * seconds}, {value})";
        }
        if (!days.IsZero)
        {
            if (days > 3_652_059)
            {
                throw c.Unsupported("the interval is longer than SQL Server's date range.");
            }
            value = $"DATEADD(day, {sign * (long)days}, {value})";
        }
        return value;
    }

    private static string CheckedInt(TSqlCall c, long value)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw c.Unsupported("the interval is too long.");
        }
        return TSql.Int(value);
    }

    #endregion

    private static string Round(TSqlCall c, string value)
    {
        string function = c.Options.TryGetValue("rounding", out var mode) && mode == "TRUNCATE" ? ", 1" : "";
        return $"ROUND({value}, {c.V(1)}{function})";
    }

    #endregion
}
