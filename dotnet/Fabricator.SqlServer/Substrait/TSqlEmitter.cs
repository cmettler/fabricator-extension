// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Numerics;
using System.Text;
using F = Clast.Drivers.SqlServer.Substrait.SubstraitFields;

namespace Clast.Drivers.SqlServer.Substrait;

/// <summary>
/// Writes T-SQL for a decoded Substrait plan.
/// </summary>
/// <remarks>
/// Each relation becomes part of a <see cref="SelectBlock"/>. Substrait refers
/// to columns by ordinal, so every block keeps the SQL expression of each of
/// its output fields; a relation on top refers to those expressions directly
/// (a projection over a filter is one SELECT), and only wraps the block in a
/// derived table when T-SQL's clause order requires it (a filter over a TOP,
/// say). Table aliases are numbered across the whole query, so expressions
/// stay valid inside correlated subqueries.
/// </remarks>
internal sealed class TSqlEmitter
{
    /// <summary>The collation closest to Substrait's string semantics: binary and case-sensitive, by UTF-16 code unit.</summary>
    public const string BinaryCollation = "Latin1_General_100_BIN2_UTF8";

    /// <summary>The collation used for functions asked to ignore case (and nothing else: accents, kana and width count).</summary>
    public const string CaseInsensitiveCollation = "Latin1_General_100_CI_AS_KS_WS_SC_UTF8";

    /// <summary>Expressions longer than this are not repeated inline; the relation below becomes a derived table.</summary>
    private const int MaxInlineLength = 16 * 1024;

    private readonly bool _perRecordEvaluation;
    private readonly string? _collation;
    private readonly Func<IReadOnlyList<string>, string>? _tableName;
    private readonly List<IReadOnlyList<SqlColumn>> _outerScopes = new();
    private int _aliasCounter;
    private int _nameCounter;

    /// <param name="perRecordEvaluation">The plan asks for execution context variables to be evaluated per record.</param>
    /// <param name="collation">The collation given to every string value, or null to use the database's collations.</param>
    /// <param name="tableName">Writes a named table's name, in place of its quoted name parts.</param>
    public TSqlEmitter(bool perRecordEvaluation, string? collation, Func<IReadOnlyList<string>, string>? tableName = null)
    {
        _perRecordEvaluation = perRecordEvaluation;
        _collation = collation;
        _tableName = tableName;
    }

    /// <summary>The collation given to string values, or null.</summary>
    public string? Collation => _collation;

    /// <summary>
    /// Writes the query. With <paramref name="nested"/>, the query is meant
    /// to be used as a derived table, so a final ORDER BY (which T-SQL does not
    /// allow there, and which would not order the outer query anyway) is left out.
    /// </summary>
    public string EmitRoot(RootRel root, bool nested, out List<SqlColumn> columns)
    {
        var block = EmitRel(root.Input);
        if (block.Columns.Count == 0)
        {
            throw SubstraitTranslationException.Unsupported("The plan's result has no columns.");
        }
        List<string> names;
        if (root.Names == null || root.Names.Count == 0)
        {
            names = UniqueNames(block.Columns.Select(c => c.Name));
        }
        else if (root.Names.Count != block.Columns.Count)
        {
            throw SubstraitTranslationException.MalformedPlan($"RelRoot has {root.Names.Count} names but its input has {block.Columns.Count} columns.");
        }
        else
        {
            // A result set may have duplicate column names.
            names = root.Names;
        }

        columns = block.Columns.Select((c, i) => new SqlColumn(names[i], c.Expr, c.IsBare, c.Raw)).ToList();
        return Render(block, SelectList(block, names), keepOrder: !nested);
    }

    #region Relations

    private SelectBlock EmitRel(Rel rel)
    {
        var block = rel switch
        {
            ReadRel r => EmitRead(r),
            FilterRel r => EmitFilter(r),
            ProjectRel r => EmitProject(r),
            FetchRel r => EmitFetch(r),
            SortRel r => EmitSort(r),
            AggregateRel r => EmitAggregate(r),
            JoinRel r => EmitJoin(r),
            CrossRel r => EmitJoin(new JoinRel { Left = r.Left, Right = r.Right, Type = F.JoinType_Inner }),
            SetRel r => EmitSet(r),
            TopNRel r => EmitTopN(r),
            WindowRel r => EmitWindow(r), // [FABRICATOR-PATCH: OURS-BY-DESIGN] window functions
            _ => throw SubstraitTranslationException.Unsupported($"relation {rel.GetType().Name}"),
        };

        if (rel.Emit != null)
        {
            if (block.Distinct && !rel.Emit.SequenceEqual(Enumerable.Range(0, block.Columns.Count)))
            {
                // DISTINCT applies to the relation's own columns, before the emit drops any.
                block = Wrap(block, keepOrder: true);
            }
            var emitted = new List<SqlColumn>(rel.Emit.Count);
            foreach (int index in rel.Emit)
            {
                if (index < 0 || index >= block.Columns.Count)
                {
                    throw SubstraitTranslationException.MalformedPlan($"Emit output mapping references field {index}, but the relation has {block.Columns.Count} fields.");
                }
                emitted.Add(block.Columns[index]);
            }
            block.Columns = emitted;
        }
        return block;
    }

    private SelectBlock EmitRead(ReadRel rel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in rel.ColumnNames)
        {
            if (!seen.Add(name))
            {
                throw SubstraitTranslationException.MalformedPlan($"base_schema has duplicate column name '{name}'.");
            }
        }

        string alias = NewAlias();
        var block = new SelectBlock();
        if (rel.VirtualRows != null)
        {
            block.From = VirtualTable(rel, alias);
        }
        else
        {
            block.From = (_tableName?.Invoke(rel.TableNames) ?? string.Join(".", rel.TableNames.Select(TSql.Identifier))) + " AS " + alias;
        }
        for (int i = 0; i < rel.ColumnNames.Count; i++)
        {
            block.Columns.Add(ColumnOf(alias, rel.ColumnNames[i], rel.ColumnTypes[i]));
        }

        // The filters apply to the base schema, before the projection.
        if (rel.Filter != null)
        {
            block.Where.Add(Predicate(rel.Filter, block.Columns));
        }
        if (rel.BestEffortFilter != null)
        {
            block.Where.Add(Predicate(rel.BestEffortFilter, block.Columns));
        }
        if (rel.Projection != null)
        {
            var projected = new List<SqlColumn>(rel.Projection.Count);
            foreach (int index in rel.Projection)
            {
                projected.Add(Column(block.Columns, index));
            }
            block.Columns = projected;
        }
        return block;
    }

    private string VirtualTable(ReadRel rel, string alias)
    {
        var none = Array.Empty<SqlColumn>();
        if (rel.ColumnNames.Count == 0)
        {
            // Rows without columns (as for a SELECT without FROM); VALUES needs a column.
            return rel.VirtualRows!.Count == 0
                ? $"(SELECT 0 AS [_] WHERE 1 = 0) AS {alias}"
                : $"(VALUES {string.Join(", ", rel.VirtualRows.Select(_ => "(0)"))}) AS {alias} ([_])";
        }
        if (rel.VirtualRows!.Count == 0)
        {
            // VALUES needs at least one row; an empty table is a typed SELECT that returns none.
            var nulls = rel.ColumnNames.Select((n, i) => $"CAST(NULL AS {TSql.RequireTypeName(rel.ColumnTypes[i])}) AS {TSql.Identifier(n)}");
            return $"(SELECT {string.Join(", ", nulls)} WHERE 1 = 0) AS {alias}";
        }

        var rows = new StringBuilder();
        foreach (var row in rel.VirtualRows)
        {
            rows.Append(rows.Length == 0 ? "(VALUES\n(" : ",\n(");
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    rows.Append(", ");
                }
                var value = Render(row[i], none);
                // A null's type comes from the schema, so that VALUES gives the column that type.
                rows.Append(row[i] is LiteralExpr { IsNull: true } && rel.ColumnTypes[i].IsKnown
                    ? $"CAST(NULL AS {TSql.RequireTypeName(rel.ColumnTypes[i])})"
                    : value.Value);
            }
            rows.Append(')');
        }
        rows.Append("\n) AS ").Append(alias).Append(" (").Append(string.Join(", ", rel.ColumnNames.Select(TSql.Identifier))).Append(')');
        return rows.ToString();
    }

    private SelectBlock EmitFilter(FilterRel rel)
    {
        var block = EmitRel(rel.Input);
        if (!block.CanFilter)
        {
            block = Wrap(block, keepOrder: true);
        }
        block.Where.Add(Predicate(rel.Condition, block.Columns));
        return block;
    }

    private SelectBlock EmitProject(ProjectRel rel)
    {
        var block = EmitRel(rel.Input);
        if (block.Distinct)
        {
            block = Wrap(block, keepOrder: true);
        }

        var added = ProjectExpressions(rel, block.Columns);
        if (added.Any(c => c.Expr.Value.Length > MaxInlineLength) && block.Columns.Any(c => !c.IsBare))
        {
            // Long expressions over computed columns would repeat them; refer to them by name instead.
            block = Wrap(block, keepOrder: true);
            added = ProjectExpressions(rel, block.Columns);
        }
        block.Columns = block.Columns.Concat(added).ToList();
        return block;
    }

    private List<SqlColumn> ProjectExpressions(ProjectRel rel, IReadOnlyList<SqlColumn> scope)
    {
        var added = new List<SqlColumn>(rel.Expressions.Count);
        foreach (var expr in rel.Expressions)
        {
            if (expr is FieldRefExpr { StepsOut: 0 } fr)
            {
                // A plain column reference keeps the column as it is.
                added.Add(Column(scope, fr.Index));
                continue;
            }
            added.Add(new SqlColumn(NewName("_e"), Render(expr, scope), isBare: false));
        }
        return added;
    }

    private SelectBlock EmitFetch(FetchRel rel)
    {
        var block = EmitRel(rel.Input);
        return ApplyFetch(block, rel.Offset, rel.Count, withTies: false);
    }

    private SelectBlock ApplyFetch(SelectBlock block, Expr? offsetExpr, Expr? countExpr, bool withTies)
    {
        string? offset = RowCount(offsetExpr, "offset", "0");
        string? count = RowCount(countExpr, "count", TSql.Int(long.MaxValue));
        if (offset == null && count == null)
        {
            return block;
        }
        if (block.HasFetch)
        {
            block = Wrap(block, keepOrder: true);
        }

        if (offset == null)
        {
            // Without sort keys every row ties, so WITH TIES returns them all.
            if (!withTies || block.OrderBy.Count > 0)
            {
                block.Top = count;
                block.WithTies = withTies;
            }
            return block;
        }
        if (withTies)
        {
            throw SubstraitTranslationException.Unsupported("TopNRel WITH TIES with an offset is not supported; T-SQL's WITH TIES works only with TOP.");
        }
        if (count == "0")
        {
            // FETCH NEXT needs a positive count; no rows is TOP (0) whatever the offset.
            block.Top = count;
            return block;
        }
        if (block.Distinct && block.OrderBy.Count == 0)
        {
            // OFFSET needs an ORDER BY, and SELECT DISTINCT allows only selected columns there.
            block = Wrap(block, keepOrder: false);
        }
        block.Offset = offset;
        if (count != null && countExpr is not LiteralExpr)
        {
            // A computed count may be 0, which FETCH NEXT refuses; apply it as a TOP over the offset rows.
            block = Wrap(block, keepOrder: true);
            block.Top = count;
            return block;
        }
        block.Fetch = count;
        return block;
    }

    /// <summary>
    /// The SQL for an offset or count, or null when it has no effect. A null
    /// offset means none and a null count means all rows.
    /// </summary>
    private string? RowCount(Expr? expr, string what, string nullValue)
    {
        if (expr == null || expr is LiteralExpr { IsNull: true })
        {
            return null;
        }
        if (expr is LiteralExpr { IsNull: false } lit)
        {
            if (!lit.Type.IsInteger)
            {
                throw SubstraitTranslationException.MalformedPlan($"Fetch {what} must be an integer.");
            }
            if (lit.IntValue < 0)
            {
                throw SubstraitTranslationException.MalformedPlan($"Fetch {what} must not be negative.");
            }
            return what == "offset" && lit.IntValue == 0 ? null : TSql.Int(lit.IntValue);
        }

        var value = Render(expr, Array.Empty<SqlColumn>());
        if (!value.Type.IsInteger && value.Type.IsKnown)
        {
            throw SubstraitTranslationException.MalformedPlan($"Fetch {what} must be an integer.");
        }
        return $"COALESCE({value.Value}, {nullValue})";
    }

    private SelectBlock EmitSort(SortRel rel)
    {
        var block = EmitRel(rel.Input);
        if (block.HasFetch || block.Distinct)
        {
            // A new sort replaces any earlier order.
            block = Wrap(block, keepOrder: false);
        }
        block.OrderBy = OrderItems(rel.Sorts, block.Columns);
        return block;
    }

    private SelectBlock EmitTopN(TopNRel rel)
    {
        var block = EmitRel(rel.Input);
        if (block.HasFetch || block.Distinct)
        {
            block = Wrap(block, keepOrder: false);
        }
        block.OrderBy = OrderItems(rel.Sorts, block.Columns);
        return ApplyFetch(block, rel.Offset, rel.Count, rel.WithTies);
    }

    internal List<OrderItem> OrderItems(IEnumerable<SortField> sorts, IReadOnlyList<SqlColumn> scope)
    {
        var items = new List<OrderItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string sql, bool descending)
        {
            // T-SQL rejects a repeated ORDER BY item; a repeated key doesn't change the order anyway.
            if (seen.Add(sql))
            {
                items.Add(new OrderItem(sql, descending));
            }
        }

        foreach (var sort in sorts)
        {
            var key = Render(sort.Expr, scope);
            if ((key.Props & (SqlProps.Column | SqlProps.Subquery)) == 0)
            {
                // T-SQL rejects constant ORDER BY items, and they don't change the order.
                continue;
            }
            if (key.Type.Kind == TypeKind.Uuid)
            {
                throw SubstraitTranslationException.Unsupported("Sorting by uuid values is not supported; SQL Server orders uniqueidentifier by its last bytes first.");
            }
            string value = key.Type.IsStringLike ? StringKey(key.Value) : key.Value; // [FABRICATOR-PATCH: OURS-BY-DESIGN] exact string order
            bool nullable = MayBeNull(key);
            // SQL Server sorts nulls as the lowest values.
            switch (sort.Direction)
            {
                case F.SortDirection_AscNullsFirst:
                case F.SortDirection_Clustered:
                    Add(value, descending: false);
                    break;
                case F.SortDirection_AscNullsLast:
                    if (nullable)
                    {
                        Add($"CASE WHEN {value} IS NULL THEN 1 ELSE 0 END", descending: false);
                    }
                    Add(value, descending: false);
                    break;
                case F.SortDirection_DescNullsFirst:
                    if (nullable)
                    {
                        Add($"CASE WHEN {value} IS NULL THEN 0 ELSE 1 END", descending: false);
                    }
                    Add(value, descending: true);
                    break;
                case F.SortDirection_DescNullsLast:
                    Add(value, descending: true);
                    break;
                default:
                    throw SubstraitTranslationException.MalformedPlan($"Sort direction {sort.Direction} is not valid.");
            }
        }
        return items;
    }

    private SelectBlock EmitAggregate(AggregateRel rel)
    {
        var block = EmitRel(rel.Input);
        if (!block.IsPlain)
        {
            block = Wrap(block, keepOrder: false);
        }
        // An aggregate's output has no order.
        block.OrderBy.Clear();

        // T-SQL can't group by a constant, a subquery or an outer column, and takes an aggregate
        // whose argument is an outer column as one of the outer query. Such keys, measure arguments
        // and measure filters are computed as columns of a derived table first.
        int inputCount = block.Columns.Count;
        var scope = block.Columns;
        var extra = new List<SqlExpr>();
        Expr Materialize(Expr e, bool key)
        {
            if (!key && e is LiteralExpr)
            {
                return e;
            }
            var rendered = Render(e, scope);
            bool needed = key ? !IsGroupable(rendered) : (rendered.Props & (SqlProps.Subquery | SqlProps.Outer)) != 0;
            if (!needed)
            {
                return e;
            }
            extra.Add(rendered);
            return new FieldRefExpr { Index = inputCount + extra.Count - 1 };
        }
        var groupingExpressions = rel.GroupingExpressions.Select(e => Materialize(e, key: true)).ToList();
        var measureList = rel.Measures.Select(m => new Measure
        {
            Call = new AggregateCall
            {
                Function = m.Call.Function,
                Args = m.Call.Args.Select(a => a.Value == null ? a : new FunctionArg { Value = Materialize(a.Value, key: false), Enum = a.Enum, TypeArg = a.TypeArg }).ToList(),
                Options = m.Call.Options,
                OutputType = m.Call.OutputType,
                Invocation = m.Call.Invocation,
                Sorts = m.Call.Sorts.Select(sf => new SortField { Expr = Materialize(sf.Expr, key: false), Direction = sf.Direction }).ToList(),
            },
            Filter = m.Filter == null ? null : Materialize(m.Filter, key: false),
        }).ToList();
        if (extra.Count > 0)
        {
            block.Columns = block.Columns.Concat(extra.Select(e => new SqlColumn(NewName("_g"), e, isBare: false))).ToList();
            block = Wrap(block, keepOrder: false);
        }

        var keys = groupingExpressions.Select(e => Render(e, block.Columns)).ToList();
        // [FABRICATOR-PATCH: OURS-BY-DESIGN] a string groups by its exact key and is read back from it
        keys = keys.Select(k => k.Type.IsStringLike ? new SqlExpr(StringKey(k.Value), null, k.Type, k.Props) : k).ToList();
        string Out(SqlExpr key, string value) => key.Type.IsStringLike ? StringFromKey(value) : value;
        var measures = measureList.Select(m => TSqlFunctionCatalog.WriteAggregate(this, m, block.Columns)).ToList();

        var output = new List<SqlColumn>(keys.Count + measures.Count + 1);
        var sets = rel.GroupingSets;
        if (sets.Count <= 1)
        {
            var distinctKeys = keys.Select(k => k.Value).Distinct(StringComparer.Ordinal).ToList();
            block.GroupBy = distinctKeys.Count == 0 ? null : string.Join(", ", distinctKeys);
            for (int i = 0; i < keys.Count; i++)
            {
                output.Add(new SqlColumn(KeyName(rel.GroupingExpressions[i], scope), new SqlExpr(Out(keys[i], keys[i].Value), null, keys[i].Type, keys[i].Props), isBare: false));
            }
        }
        else
        {
            // GROUPING_ID tells the sets apart, by the grouping keys each one leaves out.
            var distinctKeys = keys.Select(k => k.Value).Distinct(StringComparer.Ordinal).ToList();
            var masks = new List<int>(sets.Count);
            var setTexts = new List<string>(sets.Count);
            foreach (var set in sets)
            {
                var members = set.Select(i => keys[i].Value).Distinct(StringComparer.Ordinal).ToList();
                int mask = 0;
                for (int k = 0; k < distinctKeys.Count; k++)
                {
                    if (!members.Contains(distinctKeys[k]))
                    {
                        mask |= 1 << (distinctKeys.Count - 1 - k);
                    }
                }
                if (masks.Contains(mask))
                {
                    throw SubstraitTranslationException.Unsupported("Repeated grouping sets are not supported; SQL Server's GROUPING_ID cannot tell them apart.");
                }
                masks.Add(mask);
                setTexts.Add("(" + string.Join(", ", members) + ")");
            }
            if (distinctKeys.Count > 31)
            {
                throw SubstraitTranslationException.Unsupported("Grouping sets over more than 31 grouping expressions are not supported.");
            }
            block.GroupBy = "GROUPING SETS (" + string.Join(", ", setTexts) + ")";
            var index = new StringBuilder("CAST(CASE GROUPING_ID(").Append(string.Join(", ", distinctKeys)).Append(')');
            for (int s = 0; s < masks.Count; s++)
            {
                index.Append(" WHEN ").Append(masks[s]).Append(" THEN ").Append(s);
            }
            index.Append(" END AS int)");
            for (int i = 0; i < keys.Count; i++)
            {
                bool inAll = sets.All(set => set.Contains(i));
                var type = inAll ? keys[i].Type : keys[i].Type.WithNullable(true);
                string value = keys[i].Value;
                // A key repeated under another index is grouped by in that one's sets too; it is null
                // in the rows of sets that don't name it.
                var named = Enumerable.Range(0, sets.Count).Where(s => sets[s].Contains(i)).ToList();
                var grouped = Enumerable.Range(0, sets.Count).Where(s => sets[s].Any(j => keys[j].Value == value)).ToList();
                if (!named.SequenceEqual(grouped))
                {
                    value = $"CASE WHEN {index} IN ({string.Join(", ", named)}) THEN {value} END";
                }
                output.Add(new SqlColumn(KeyName(rel.GroupingExpressions[i], scope), new SqlExpr(Out(keys[i], value), null, type, keys[i].Props | SqlProps.Aggregate), isBare: false));
            }
            measures.Add(SqlExpr.Of(index.ToString(), new SType(TypeKind.I32, nullable: false), SqlProps.Column | SqlProps.Aggregate));
        }

        output.AddRange(measures.Select(m => new SqlColumn(NewName("_a"), m, isBare: false)));
        block.Columns = output;
        block.Aggregated = true;
        return block;
    }

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A window relation: the input's columns, then one column per window
    /// function. Partitions and ORDER BY items use the exact string key (§3.6), so partitions, peers and ranks are
    /// DuckDB's; the ORDER BY items carry DuckDB's null order. The input is made plain first: T-SQL evaluates
    /// window functions after WHERE and GROUP BY, and does not nest them.
    /// </summary>
    private SelectBlock EmitWindow(WindowRel rel)
    {
        var block = EmitRel(rel.Input);
        if (!block.IsPlain)
        {
            block = Wrap(block, keepOrder: true);
        }
        var scope = block.Columns;
        void Check(SqlExpr e, string what)
        {
            if ((e.Props & (SqlProps.Subquery | SqlProps.Outer | SqlProps.Window | SqlProps.Aggregate)) != 0)
            {
                throw SubstraitTranslationException.Unsupported($"a window {what} with a subquery, an outer reference or an aggregate is not supported.");
            }
        }
        var partitions = new List<string>();
        foreach (var expr in rel.Partitions)
        {
            var e = Render(expr, scope);
            Check(e, "partition");
            if ((e.Props & SqlProps.Column) == 0)
            {
                continue; // a constant puts every row in one partition, as no partition does
            }
            partitions.Add(e.Type.IsStringLike ? StringKey(e.Value) : e.Value);
        }
        foreach (var sort in rel.Sorts)
        {
            Check(Render(sort.Expr, scope), "order");
        }
        var order = OrderItems(rel.Sorts, scope);
        var added = rel.Functions
            .Select(f => new SqlColumn(NewName("_w"), TSqlFunctionCatalog.WriteWindow(this, f, scope, partitions, order), isBare: false))
            .ToList();
        block.Columns = block.Columns.Concat(added).ToList();
        block.Windowed = true;
        return block;
    }

    /// <summary>A grouping key keeps the name of the column it groups by.</summary>
    private string KeyName(Expr key, IReadOnlyList<SqlColumn> scope)
        => key is FieldRefExpr { StepsOut: 0 } fr && fr.Index >= 0 && fr.Index < scope.Count ? scope[fr.Index].Name : NewName("_g");

    private static bool IsGroupable(SqlExpr key)
        => (key.Props & SqlProps.Column) != 0 && (key.Props & (SqlProps.Subquery | SqlProps.Aggregate | SqlProps.Outer)) == 0;

    private SelectBlock EmitJoin(JoinRel rel)
    {
        switch (rel.Type)
        {
            case F.JoinType_Inner:
            case F.JoinType_Left:
            case F.JoinType_Right:
            case F.JoinType_Outer:
                return EmitOuterJoin(rel);
            case F.JoinType_LeftSemi:
            case F.JoinType_LeftAnti:
            case F.JoinType_RightSemi:
            case F.JoinType_RightAnti:
                return EmitSemiJoin(rel);
            case F.JoinType_LeftMark:
            case F.JoinType_RightMark:
                return EmitMarkJoin(rel);
            case F.JoinType_LeftSingle:
            case F.JoinType_RightSingle:
                throw SubstraitTranslationException.Unsupported("Single joins are not supported.");
            default:
                throw SubstraitTranslationException.MalformedPlan($"Join type {rel.Type} is not valid.");
        }
    }

    /// <summary>Inner, left, right and full outer joins, and cross joins.</summary>
    private SelectBlock EmitOuterJoin(JoinRel rel)
    {
        int type = rel.Type;
        bool leftNullable = type is F.JoinType_Right or F.JoinType_Outer;
        bool rightNullable = type is F.JoinType_Left or F.JoinType_Outer;

        var left = JoinInput(EmitRel(rel.Left), leftNullable, keepWhere: type != F.JoinType_Outer);
        var right = JoinInput(EmitRel(rel.Right), rightNullable, keepWhere: type != F.JoinType_Outer);

        // A filter on a row-preserving input applies after the join as well as before it; a filter
        // on a null-supplying input belongs in the ON clause, which decides what matches.
        var where = new List<string>();
        var on = new List<string>();
        (type == F.JoinType_Right ? on : where).AddRange(left.Where);
        (type == F.JoinType_Left ? on : where).AddRange(right.Where);

        var scope = left.Columns.Select(c => leftNullable ? c.WithType(c.Type.WithNullable(true)) : c)
            .Concat(right.Columns.Select(c => rightNullable ? c.WithType(c.Type.WithNullable(true)) : c))
            .ToList();
        if (rel.Expression != null && !IsTrue(rel.Expression))
        {
            on.Insert(0, Predicate(rel.Expression, left.Columns.Concat(right.Columns).ToList()));
        }

        string rightFrom = right.FromIsJoin ? "(" + right.From + ")" : right.From;
        string kind = type switch
        {
            F.JoinType_Inner => "INNER JOIN",
            F.JoinType_Left => "LEFT JOIN",
            F.JoinType_Right => "RIGHT JOIN",
            _ => "FULL JOIN",
        };
        string from = on.Count == 0 && type == F.JoinType_Inner
            ? $"{left.From}\nCROSS JOIN {rightFrom}"
            : $"{left.From}\n{kind} {rightFrom} ON {(on.Count == 0 ? "1 = 1" : string.Join(" AND ", on))}";

        var block = new SelectBlock { From = from, FromIsJoin = true, Columns = scope, Where = where };
        if (rel.PostJoinFilter != null)
        {
            block.Where.Add(Predicate(rel.PostJoinFilter, scope));
        }
        return block;
    }

    /// <summary>Prepares a block to be one side of a join, wrapping it when it can't be joined as it stands.</summary>
    private SelectBlock JoinInput(SelectBlock block, bool nullSupplying, bool keepWhere)
    {
        // A computed column of a null-supplying input would keep its value for a missing row.
        if (!block.IsPlain || (!keepWhere && block.Where.Count > 0) || (nullSupplying && block.Columns.Any(c => !c.IsBare)))
        {
            block = Wrap(block, keepOrder: false);
        }
        block.OrderBy.Clear();
        return block;
    }

    private static bool IsTrue(Expr expr) => expr is LiteralExpr { IsNull: false, Type.Kind: TypeKind.Bool, BoolValue: true };

    private SelectBlock EmitSemiJoin(JoinRel rel)
    {
        bool leftOuter = rel.Type is F.JoinType_LeftSemi or F.JoinType_LeftAnti;
        bool anti = rel.Type is F.JoinType_LeftAnti or F.JoinType_RightAnti;
        var left = EmitRel(rel.Left);
        var right = EmitRel(rel.Right);
        PrepareExistsSides(ref left, ref right, leftOuter);
        var outer = leftOuter ? left : right;
        var inner = leftOuter ? right : left;

        string exists = Exists(inner, rel.Expression, left.Columns.Concat(right.Columns).ToList(), null);
        // Substrait's anti join keeps rows without a match, which is NOT EXISTS.
        outer.Where.Add(anti ? $"(NOT {exists})" : $"({exists})");
        if (rel.PostJoinFilter != null)
        {
            outer.Where.Add(Predicate(rel.PostJoinFilter, outer.Columns));
        }
        return outer;
    }

    private SelectBlock EmitMarkJoin(JoinRel rel)
    {
        bool leftOuter = rel.Type == F.JoinType_LeftMark;
        var left = EmitRel(rel.Left);
        var right = EmitRel(rel.Right);
        PrepareExistsSides(ref left, ref right, leftOuter);
        var outer = leftOuter ? left : right;
        var inner = leftOuter ? right : left;
        var scope = left.Columns.Concat(right.Columns).ToList();

        // The mark is true when the condition holds for some row, else null when it is null
        // for some row, else false.
        string matched = Exists(inner, rel.Expression, scope, null);
        string mark;
        if (rel.Expression == null || IsTrue(rel.Expression))
        {
            mark = $"CASE WHEN {matched} THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END";
        }
        else
        {
            string unknown = Exists(inner, rel.Expression, scope, c => $"CASE WHEN {c} THEN 1 WHEN NOT {c} THEN 0 END IS NULL");
            mark = $"CASE WHEN {matched} THEN CAST(1 AS bit) WHEN {unknown} THEN NULL ELSE CAST(0 AS bit) END";
        }
        outer.Columns = outer.Columns.Concat(new[]
        {
            new SqlColumn(NewName("_mark"), SqlExpr.Of(mark, SType.Bool, SqlProps.Column | SqlProps.Subquery), isBare: false),
        }).ToList();
        if (rel.PostJoinFilter != null)
        {
            outer.Where.Add(Predicate(rel.PostJoinFilter, outer.Columns));
        }
        return outer;
    }

    /// <summary>
    /// Readies the inputs of a semi, anti or mark join, which keeps the outer
    /// input's rows and tests the inner input with EXISTS.
    /// </summary>
    private void PrepareExistsSides(ref SelectBlock left, ref SelectBlock right, bool leftOuter)
    {
        ref SelectBlock outer = ref leftOuter ? ref left : ref right;
        ref SelectBlock inner = ref leftOuter ? ref right : ref left;
        if (!outer.IsPlain)
        {
            outer = Wrap(outer, keepOrder: true);
        }
        if (!inner.IsPlain)
        {
            inner = Wrap(inner, keepOrder: false);
        }
        inner.OrderBy.Clear();
    }

    /// <summary>An EXISTS test of <paramref name="inner"/> rows for which the join condition (as transformed) holds.</summary>
    private string Exists(SelectBlock inner, Expr? condition, IReadOnlyList<SqlColumn> scope, Func<string, string>? transform)
    {
        var where = new List<string>(inner.Where);
        if (condition != null && !IsTrue(condition))
        {
            string predicate = Predicate(condition, scope);
            where.Add(transform == null ? predicate : $"({transform(predicate)})");
        }
        string sql = "EXISTS (SELECT 1 FROM " + inner.From;
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }
        return sql + ")";
    }

    private SelectBlock EmitSet(SetRel rel)
    {
        var inputs = rel.Inputs.Select(EmitRel).ToList();
        int width = inputs[0].Columns.Count;
        if (inputs.Any(b => b.Columns.Count != width))
        {
            throw SubstraitTranslationException.MalformedPlan("SetRel inputs have different numbers of columns.");
        }
        var names = UniqueNames(inputs[0].Columns.Select(c => c.Name));
        var types = Enumerable.Range(0, width)
            .Select(i => inputs[0].Columns[i].Type.WithNullable(inputs.Any(b => !b.Columns[i].Type.IsKnown || b.Columns[i].Type.Nullable)))
            .ToList();

        // [FABRICATOR-PATCH: OURS-BY-DESIGN] a set operation that compares rows compares strings by collation: decline those
        // over strings (UNION ALL compares nothing).
        if (rel.Op != F.SetOp_UnionAll && types.Any(t => t.IsStringLike))
        {
            throw SubstraitTranslationException.Unsupported("set operations that compare string columns are not supported (collation semantics).");
        }
        string sql;
        if (inputs.Count == 1)
        {
            // A set operation over one input keeps it, or its distinct rows.
            var only = inputs[0];
            if (rel.Op is F.SetOp_UnionAll or F.SetOp_MinusPrimaryAll or F.SetOp_IntersectionMultisetAll)
            {
                return only;
            }
            if (!only.IsPlain)
            {
                only = Wrap(only, keepOrder: false);
            }
            only.OrderBy.Clear();
            only.Distinct = true;
            return only;
        }

        var branches = inputs.Select(b => Branch(b, names)).ToList();
        switch (rel.Op)
        {
            case F.SetOp_UnionAll:
                sql = string.Join("\nUNION ALL\n", branches);
                break;
            case F.SetOp_UnionDistinct:
                sql = string.Join("\nUNION\n", branches);
                break;
            case F.SetOp_MinusPrimary:
                sql = string.Join("\nEXCEPT\n", branches);
                break;
            case F.SetOp_IntersectionMultiset:
                sql = string.Join("\nINTERSECT\n", branches);
                break;
            case F.SetOp_IntersectionPrimary:
                // Rows of the primary input found in any other input.
                sql = branches[0] + "\nINTERSECT\n" + (branches.Count == 2
                    ? branches[1]
                    : $"SELECT {string.Join(", ", names.Select(TSql.Identifier))} FROM {TSql.Parenthesize(string.Join("\nUNION ALL\n", branches.Skip(1)))} AS {NewAlias()}");
                break;
            case F.SetOp_MinusPrimaryAll:
            {
                // EXCEPT ALL: number each row among its duplicates, so that m copies minus n copies leave m - n.
                string rn = UniqueName("_rn", names);
                sql = Numbered(branches[0], names, rn) + "\nEXCEPT\n" + Numbered(string.Join("\nUNION ALL\n", branches.Skip(1)), names, rn);
                sql = $"SELECT {string.Join(", ", names.Select(TSql.Identifier))} FROM {TSql.Parenthesize(sql)} AS {NewAlias()}";
                break;
            }
            case F.SetOp_IntersectionMultisetAll:
            {
                string rn = UniqueName("_rn", names);
                sql = string.Join("\nINTERSECT\n", branches.Select(b => Numbered(b, names, rn)));
                sql = $"SELECT {string.Join(", ", names.Select(TSql.Identifier))} FROM {TSql.Parenthesize(sql)} AS {NewAlias()}";
                break;
            }
            case F.SetOp_MinusMultiset:
                throw SubstraitTranslationException.Unsupported("MINUS MULTISET is not supported.");
            default:
                throw SubstraitTranslationException.MalformedPlan($"Set operation {rel.Op} is not valid.");
        }

        string alias = NewAlias();
        return new SelectBlock
        {
            From = TSql.Parenthesize(sql) + " AS " + alias,
            Columns = names.Select((n, i) => ColumnOf(alias, n, types[i])).ToList(),
        };
    }

    /// <summary>One input of a set operation, as a SELECT with the given column names.</summary>
    private string Branch(SelectBlock block, List<string> names)
    {
        if (block.HasFetch)
        {
            // A TOP or OFFSET input needs its ORDER BY, which a set operation's inputs can't have.
            block = Wrap(block, keepOrder: false);
        }
        // Set operations compare rows, so strings keep their collation.
        return Render(block, block.Columns.Select((c, i) => (c.Expr.Value, names[i])), keepOrder: false);
    }

    /// <summary>Rows of a query with a row number among equal rows, for the ALL set operations.</summary>
    private string Numbered(string query, List<string> names, string rowNumber)
    {
        // The query's string columns already have the query's collation, which PARTITION BY uses.
        string alias = NewAlias();
        var columns = names.Select(n => alias + "." + TSql.Identifier(n)).ToList();
        string list = string.Join(", ", names.Select((n, i) => $"{columns[i]} AS {TSql.Identifier(n)}"));
        return $"SELECT {list}, ROW_NUMBER() OVER (PARTITION BY {string.Join(", ", columns)} ORDER BY (SELECT NULL)) AS {TSql.Identifier(rowNumber)}\nFROM {TSql.Parenthesize(query)} AS {alias}";
    }

    /// <summary>A column of a table or derived table; strings are given the query's collation.</summary>
    private SqlColumn ColumnOf(string alias, string name, SType type)
    {
        string reference = alias + "." + TSql.Identifier(name);
        if (_collation != null && type.IsStringLike)
        {
            return new SqlColumn(name, SqlExpr.Of(reference + " COLLATE " + _collation, type, SqlProps.Column), isBare: true, raw: reference);
        }
        return new SqlColumn(name, SqlExpr.Of(reference, type, SqlProps.Column), isBare: true);
    }

    /// <summary>
    /// Turns a block into a derived table in the FROM clause of a new block.
    /// With <paramref name="keepOrder"/>, the block's sort carries over to the
    /// new block, through hidden columns holding the sort keys (a derived table
    /// has no order of its own).
    /// </summary>
    private SelectBlock Wrap(SelectBlock block, bool keepOrder)
    {
        var names = UniqueNames(block.Columns.Select(c => c.Name));
        var items = SelectList(block, names);
        var hidden = new List<(string Name, bool Descending)>();
        if (keepOrder)
        {
            foreach (var item in block.OrderBy)
            {
                string name = UniqueName("_o" + hidden.Count.ToString(CultureInfo.InvariantCulture), names.Concat(hidden.Select(h => h.Name)));
                items.Add((item.Sql, name));
                hidden.Add((name, item.Descending));
            }
        }

        string alias = NewAlias();
        string inner = Render(block, items, keepOrder: false);
        var wrapped = new SelectBlock
        {
            From = TSql.Parenthesize(inner) + " AS " + alias,
            Columns = names.Select((n, i) => ColumnOf(alias, n, block.Columns[i].Type)).ToList(),
        };
        foreach (var (name, descending) in hidden)
        {
            wrapped.OrderBy.Add(new OrderItem(alias + "." + TSql.Identifier(name), descending));
        }
        return wrapped;
    }

    /// <summary>
    /// The select list for a block's columns. A bare string column is selected
    /// without its added collation, unless the block deduplicates rows: so
    /// that columns such as xml, which have no collation, can still be read.
    /// </summary>
    private static List<(string Sql, string Name)> SelectList(SelectBlock block, IReadOnlyList<string> names)
        => block.Columns.Select((c, i) => (!block.Distinct && c.Raw != null ? c.Raw : c.Expr.Value, names[i])).ToList();

    private static string Render(SelectBlock block, IEnumerable<(string Sql, string Name)> selectList, bool keepOrder)
    {
        var sb = new StringBuilder("SELECT ");
        if (block.Distinct)
        {
            sb.Append("DISTINCT ");
        }
        if (block.Top != null)
        {
            sb.Append("TOP (").Append(block.Top).Append(") ");
            if (block.WithTies)
            {
                sb.Append("WITH TIES ");
            }
        }
        bool first = true;
        foreach (var (sql, name) in selectList)
        {
            if (!first)
            {
                sb.Append(", ");
            }
            first = false;
            sb.Append(sql);
            if (name.Length > 0)
            {
                sb.Append(" AS ").Append(TSql.Identifier(name));
            }
        }
        if (first)
        {
            // A relation without columns (only its row count matters, as in EXISTS).
            sb.Append('1');
        }
        if (block.From.Length > 0)
        {
            sb.Append("\nFROM ").Append(block.From);
        }
        if (block.Where.Count > 0)
        {
            sb.Append("\nWHERE ").Append(string.Join("\n  AND ", block.Where));
        }
        if (block.GroupBy != null)
        {
            sb.Append("\nGROUP BY ").Append(block.GroupBy);
        }
        if ((keepOrder || block.HasFetch) && block.OrderBy.Count > 0)
        {
            sb.Append("\nORDER BY ").Append(string.Join(", ", block.OrderBy));
        }
        else if (block.Offset != null)
        {
            sb.Append("\nORDER BY (SELECT NULL)");
        }
        if (block.Offset != null)
        {
            sb.Append("\nOFFSET ").Append(block.Offset).Append(" ROWS");
            if (block.Fetch != null)
            {
                sb.Append(" FETCH NEXT ").Append(block.Fetch).Append(" ROWS ONLY");
            }
        }
        return sb.ToString();
    }

    private List<string> UniqueNames(IEnumerable<string> names)
    {
        // Column names of a derived table must differ, ignoring case; they are also kept short.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string n in names)
        {
            string name = n.Length == 0 ? "_c" : n.Length > 100 ? n.Substring(0, 100) : n;
            if (!used.Add(name))
            {
                name = UniqueName(name + "_", used);
                used.Add(name);
            }
            result.Add(name);
        }
        return result;
    }

    private string UniqueName(string prefix, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(prefix) && !prefix.EndsWith("_", StringComparison.Ordinal))
        {
            return prefix;
        }
        string name;
        do
        {
            name = prefix + (_nameCounter++).ToString(CultureInfo.InvariantCulture);
        }
        while (set.Contains(name));
        return name;
    }

    private string NewAlias() => "[t" + (_aliasCounter++).ToString(CultureInfo.InvariantCulture) + "]";

    private string NewName(string prefix) => prefix + (_nameCounter++).ToString(CultureInfo.InvariantCulture);

    private static SqlColumn Column(IReadOnlyList<SqlColumn> scope, int index)
    {
        if (index < 0 || index >= scope.Count)
        {
            throw SubstraitTranslationException.MalformedPlan($"Field reference {index} is out of range; the input has {scope.Count} fields.");
        }
        return scope[index];
    }

    #endregion

    #region Expressions

    /// <summary>Renders an expression over the columns in <paramref name="scope"/>.</summary>
    internal SqlExpr Render(Expr expr, IReadOnlyList<SqlColumn> scope) => expr switch
    {
        LiteralExpr lit => Literal(lit),
        FieldRefExpr fr => FieldReference(fr, scope),
        ScalarFunctionExpr sf => TSqlFunctionCatalog.WriteScalar(this, sf, scope),
        IfThenExpr it => IfThen(it, scope),
        SwitchExpr sw => Switch(sw, scope),
        SingularOrListExpr sl => SingularOrList(sl, scope),
        MultiOrListExpr ml => MultiOrList(ml, scope),
        CastExpr c => Cast(c, scope),
        ContextVariableExpr cv => ContextVariable(cv),
        SubqueryExpr sq => Subquery(sq, scope),
        _ => throw SubstraitTranslationException.UnsupportedExpression(expr.GetType().Name),
    };

    /// <summary>Renders a boolean expression as a condition.</summary>
    internal string Predicate(Expr expr, IReadOnlyList<SqlColumn> scope)
    {
        var e = Render(expr, scope);
        if (e.Type.IsKnown && e.Type.Kind != TypeKind.Bool)
        {
            throw SubstraitTranslationException.MalformedPlan($"A condition has type '{e.Type}', not boolean.");
        }
        return e.Predicate;
    }

    /// <summary>The type of an expression; literals are not rendered, so interval literals don't fail here.</summary>
    internal SType TypeOf(Expr expr, IReadOnlyList<SqlColumn> scope) => expr is LiteralExpr lit ? lit.Type : Render(expr, scope).Type;

    internal static bool MayBeNull(SqlExpr e) => !e.Type.IsKnown || e.Type.Nullable;

    /// <summary>Gives a string value the query's collation (string literals and casts, which have none of their own).</summary>
    internal string Collate(string text) => _collation == null ? text : text + " COLLATE " + _collation;

    // [FABRICATOR-PATCH: OURS-BY-DESIGN] Exact string semantics (DuckDB's: byte equality, code point order),
    // where the collation the original relies on ignores trailing spaces and orders UTF-16 code units. The key
    // is UTF-8 under a binary collation (byte order = code point order), with CHAR(1) and CHAR(0) escaped and a
    // CHAR(0) appended, so that a prefix sorts first and no two different strings compare equal despite padding.
    // One key serves equality, grouping, distinct and order. Measured: docs/duckdb-2.0-remote-execution.md §3.6.
    internal static string StringKey(string value)
        => $"(REPLACE(REPLACE(CONVERT(varchar(max), ({value}) COLLATE Latin1_General_100_BIN2_UTF8), CHAR(1), CHAR(1) + CHAR(2)), CHAR(0), CHAR(1) + CHAR(1)) + CHAR(0))";

    /// <summary>
    /// [FABRICATOR-PATCH: OURS-BY-DESIGN] A rendered string column or literal in the form an index can use: the
    /// column without the query collation (so it compares under its own, which its indexes are built in), a
    /// literal of printable ASCII as a non-Unicode literal (so a varchar column is not converted, which would
    /// rule out a seek). Null for anything else. Compared with <c>=</c>, two of these give a SUPERSET of exact
    /// equality — byte-equal strings are equal under every collation — so it may only ever be ANDed with the
    /// exact key comparison, never stand alone.
    /// </summary>
    internal static string? IndexableString(string rendered)
    {
        const string suffix = " COLLATE " + BinaryCollation;
        if (!rendered.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        string raw = rendered.Substring(0, rendered.Length - suffix.Length);
        // a cast to nvarchar(max) does not change a string's value: look through it
        const string castHead = "CAST(", castTail = " AS nvarchar(max))";
        if (raw.StartsWith(castHead, StringComparison.Ordinal) && raw.EndsWith(castTail, StringComparison.Ordinal))
        {
            return IndexableString(raw.Substring(castHead.Length, raw.Length - castHead.Length - castTail.Length));
        }
        if (s_unicodeLiteral.IsMatch(raw))
        {
            string body = raw.Substring(2, raw.Length - 3);
            return body.All(ch => ch >= 0x20 && ch < 0x7F) ? "'" + body + "'" : raw;
        }
        return s_columnReference.IsMatch(raw) ? raw : null;
    }

    private static readonly System.Text.RegularExpressions.Regex s_unicodeLiteral =
        new(@"^N'(?:[^']|'')*'$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex s_columnReference =
        new(@"^\[[^\]]+\]\.\[(?:[^\]]|\]\])*\]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] Whether a rendered value is a plain column reference.</summary>
    internal static bool IsColumnReference(string? raw) => raw != null && s_columnReference.IsMatch(raw);

    /// <summary>[FABRICATOR-PATCH: OURS-BY-DESIGN] The string a <see cref="StringKey"/> was made from.</summary>
    internal static string StringFromKey(string key)
        => $"CONVERT(nvarchar(max), REPLACE(REPLACE(LEFT({key}, LEN({key}) - 1), CHAR(1) + CHAR(1), CHAR(0)), CHAR(1) + CHAR(2), CHAR(1)))";

    private SqlExpr FieldReference(FieldRefExpr fr, IReadOnlyList<SqlColumn> scope)
    {
        if (fr.StepsOut == 0)
        {
            return Column(scope, fr.Index).Expr;
        }
        if (fr.StepsOut > _outerScopes.Count)
        {
            throw SubstraitTranslationException.MalformedPlan($"An outer reference steps out {fr.StepsOut} subqueries, but it is inside {_outerScopes.Count}.");
        }
        var outer = Column(_outerScopes[_outerScopes.Count - fr.StepsOut], fr.Index).Expr;
        return outer.With(outer.Type, outer.Props | SqlProps.Outer);
    }

    private SqlExpr IfThen(IfThenExpr expr, IReadOnlyList<SqlColumn> scope)
    {
        var sb = new StringBuilder("CASE");
        var results = new List<SqlExpr>();
        var props = SqlProps.None;
        foreach (var (cond, then) in expr.Clauses)
        {
            var c = Render(cond, scope);
            var t = Render(then, scope);
            props |= c.Props | t.Props;
            results.Add(t);
            sb.Append(" WHEN ").Append(c.Predicate).Append(" THEN ").Append(t.Value);
        }
        if (expr.Else != null)
        {
            var e = Render(expr.Else, scope);
            props |= e.Props;
            results.Add(e);
            sb.Append(" ELSE ").Append(e.Value);
        }
        return SqlExpr.Of(sb.Append(" END").ToString(), CaseType(results, expr.Else != null), props);
    }

    /// <summary>The type of a CASE: its first known result type, nullable if any result can be null or none applies.</summary>
    private static SType CaseType(List<SqlExpr> results, bool hasElse)
    {
        var type = results.Select(r => r.Type).FirstOrDefault(t => t.IsKnown);
        return type.WithNullable(!hasElse || results.Any(MayBeNull));
    }

    private SqlExpr Switch(SwitchExpr expr, IReadOnlyList<SqlColumn> scope)
    {
        var match = Render(expr.Match, scope);
        var sb = new StringBuilder("CASE ").Append(match.Value);
        var results = new List<SqlExpr>();
        var props = match.Props;
        foreach (var (value, then) in expr.Cases)
        {
            var t = Render(then, scope);
            props |= t.Props;
            results.Add(t);
            sb.Append(" WHEN ").Append(Literal(value).Value).Append(" THEN ").Append(t.Value);
        }
        if (expr.Else != null)
        {
            var e = Render(expr.Else, scope);
            props |= e.Props;
            results.Add(e);
            sb.Append(" ELSE ").Append(e.Value);
        }
        return SqlExpr.Of(sb.Append(" END").ToString(), CaseType(results, expr.Else != null), props);
    }

    private SqlExpr SingularOrList(SingularOrListExpr expr, IReadOnlyList<SqlColumn> scope)
    {
        if (expr.Options.Count == 0)
        {
            return SqlExpr.Condition("(1 = 0)", SqlProps.None);
        }
        var value = Render(expr.Value, scope);
        var options = expr.Options.Select(o => Render(o, scope)).ToList();
        var props = options.Aggregate(value.Props, (p, o) => p | o.Props);
        // T-SQL's IN is three-valued, like Substrait's.
        if (value.Type.IsStringLike) // [FABRICATOR-PATCH: OURS-BY-DESIGN] strings compare by their exact key
        {
            string exact = $"({StringKey(value.Value)} IN ({string.Join(", ", options.Select(o => StringKey(o.Value)))}))";
            // [FABRICATOR-PATCH: OURS-BY-DESIGN] an indexable superset first (see IndexableString)
            string? column = IndexableString(value.Value);
            var literals = options.Select(o => IndexableString(o.Value)).ToList();
            if (IsColumnReference(column) && literals.All(l => l != null && !IsColumnReference(l)))
            {
                exact = $"({column} IN ({string.Join(", ", literals)}) AND {exact})";
            }
            return SqlExpr.Condition(exact, props);
        }
        return SqlExpr.Condition($"({value.Value} IN ({string.Join(", ", options.Select(o => o.Value))}))", props);
    }

    private SqlExpr MultiOrList(MultiOrListExpr expr, IReadOnlyList<SqlColumn> scope)
    {
        if (expr.Options.Count == 0)
        {
            return SqlExpr.Condition("(1 = 0)", SqlProps.None);
        }
        var values = expr.Values.Select(v => Render(v, scope)).ToList();
        var props = values.Aggregate(SqlProps.None, (p, v) => p | v.Props);
        var records = new List<string>(expr.Options.Count);
        foreach (var record in expr.Options)
        {
            var terms = new List<string>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                var o = Render(record[i], scope);
                props |= o.Props;
                terms.Add(values[i].Type.IsStringLike ? $"{StringKey(values[i].Value)} = {StringKey(o.Value)}" : $"{values[i].Value} = {o.Value}"); // [FABRICATOR-PATCH: OURS-BY-DESIGN] exact string equality
            }
            records.Add("(" + string.Join(" AND ", terms) + ")");
        }
        return SqlExpr.Condition("(" + string.Join(" OR ", records) + ")", props);
    }

    private SqlExpr Subquery(SubqueryExpr expr, IReadOnlyList<SqlColumn> scope)
    {
        SqlExpr? needle = expr.Needle == null ? null : Render(expr.Needle, scope);
        SelectBlock block;
        _outerScopes.Add(scope);
        try
        {
            block = EmitRel(expr.Input);
        }
        finally
        {
            _outerScopes.RemoveAt(_outerScopes.Count - 1);
        }

        var props = SqlProps.Subquery | SqlProps.Column | (needle?.Props ?? SqlProps.None);
        if (expr.Kind != SubqueryKind.Exists && block.Columns.Count != 1)
        {
            throw SubstraitTranslationException.MalformedPlan($"A {expr.Kind} subquery must return one column; it returns {block.Columns.Count}.");
        }
        string query = Render(block, block.Columns.Select((c, i) => (c.Expr.Value, "c" + i.ToString(CultureInfo.InvariantCulture))), keepOrder: false);
        switch (expr.Kind)
        {
            case SubqueryKind.Scalar:
                return SqlExpr.Of(TSql.Parenthesize(query), block.Columns[0].Type.WithNullable(true), props);
            case SubqueryKind.In:
                return SqlExpr.Condition($"({needle!.Value} IN {TSql.Parenthesize(query)})", props);
            case SubqueryKind.Exists:
                return SqlExpr.Condition($"(EXISTS {TSql.Parenthesize(query)})", props);
            default:
            {
                string op = expr.ComparisonOp switch
                {
                    F.ComparisonOp_Eq => "=",
                    F.ComparisonOp_Ne => "<>",
                    F.ComparisonOp_Lt => "<",
                    F.ComparisonOp_Gt => ">",
                    F.ComparisonOp_Le => "<=",
                    _ => ">=",
                };
                string reduction = expr.ReductionOp == F.ReductionOp_All ? "ALL" : "ANY";
                return SqlExpr.Condition($"({needle!.Value} {op} {reduction} {TSql.Parenthesize(query)})", props);
            }
        }
    }

    private SqlExpr ContextVariable(ContextVariableExpr expr)
    {
        // SQL Server evaluates SYSUTCDATETIME() once per query, which is per-plan evaluation.
        if (_perRecordEvaluation && expr.Variable != F.ContextVariable_CurrentTimezone)
        {
            throw SubstraitTranslationException.Unsupported("Per-record evaluation of execution context variables is not supported; SQL Server evaluates SYSUTCDATETIME() once per query.");
        }
        // The session time zone is UTC: the server's own time zone is not the client's.
        return expr.Variable switch
        {
            F.ContextVariable_CurrentTimestamp => SqlExpr.Of("CAST(SYSUTCDATETIME() AS datetimeoffset(7))", new SType(TypeKind.PrecisionTimestampTz, false, 7), SqlProps.None),
            F.ContextVariable_CurrentDate => SqlExpr.Of("CAST(SYSUTCDATETIME() AS date)", new SType(TypeKind.Date, false), SqlProps.None),
            _ => SqlExpr.Of(Collate("N'UTC'"), new SType(TypeKind.String, false), SqlProps.None),
        };
    }

    #endregion

    #region Casts

    private SqlExpr Cast(CastExpr expr, IReadOnlyList<SqlColumn> scope)
    {
        var input = Render(expr.Input, scope);
        var source = input.Type;
        var target = expr.Type;
        string op = expr.FailureBehavior == F.FailureBehavior_ReturnNull ? "TRY_CAST" : "CAST";

        string Convert(string value, string typeName) => $"{op}({value} AS {typeName})";

        string text;
        switch (target.Kind)
        {
            case TypeKind.String:
                text = CastToString(input, op);
                break;
            case TypeKind.VarChar:
            case TypeKind.FixedChar:
                throw SubstraitTranslationException.Unsupported("Casts to varchar or fixedchar are not supported; SQL Server truncates strings to the length without an error.");
            case TypeKind.Binary:
            case TypeKind.FixedBinary:
                if (!(source.Kind is TypeKind.Binary or TypeKind.FixedBinary))
                {
                    throw SubstraitTranslationException.Unsupported($"Cast from '{source}' to binary is not supported; SQL Server converts the internal representation.");
                }
                text = Convert(input.Value, TSql.RequireTypeName(target));
                break;
            case TypeKind.PrecisionTimestamp:
            case TypeKind.Date:
            case TypeKind.PrecisionTime:
                // A timestamp with a time zone is an instant; its UTC time is the timestamp.
                text = Convert(source.Kind == TypeKind.PrecisionTimestampTz ? $"SWITCHOFFSET({input.Value}, '+00:00')" : input.Value, TSql.RequireTypeName(target));
                break;
            case TypeKind.PrecisionTimestampTz:
                text = Convert(source.Kind == TypeKind.Date ? $"CAST({input.Value} AS datetime2(0))" : input.Value, TSql.RequireTypeName(target));
                break;
            case TypeKind.I8:
                throw SubstraitTranslationException.Unsupported("Casts to i8 are not supported; SQL Server's tinyint is unsigned.");
            default:
                if (source.IsStringLike && target.Kind == TypeKind.Uuid)
                {
                    text = Convert(input.Value, "uniqueidentifier");
                    break;
                }
                text = Convert(input.Value, TSql.RequireTypeName(target));
                break;
        }
        return SqlExpr.Of(text, target, input.Props);
    }

    private string CastToString(SqlExpr input, string op)
    {
        var source = input.Type;
        switch (source.Kind)
        {
            case TypeKind.Bool:
                return Collate($"CASE WHEN {input.Predicate} THEN N'true' WHEN NOT {input.Predicate} THEN N'false' END");
            case TypeKind.Uuid:
                // SQL Server writes uniqueidentifier in upper case; Substrait (RFC 4122) in lower case.
                return Collate($"LOWER(CAST({input.Value} AS nvarchar(36)))");
            case TypeKind.I16:
            case TypeKind.I32:
            case TypeKind.I64:
            case TypeKind.Decimal:
            case TypeKind.String:
            case TypeKind.VarChar:
            case TypeKind.FixedChar:
                return Collate($"{op}({input.Value} AS nvarchar(max))");
            default:
                throw SubstraitTranslationException.Unsupported(
                    $"Cast from '{source}' to string is not supported; SQL Server formats floating-point, binary and date/time values differently.");
        }
    }

    #endregion

    #region Literals

    internal SqlExpr Literal(LiteralExpr lit)
    {
        if (lit.IsNull)
        {
            string? typeName = lit.Type.IsKnown ? TSql.RequireTypeName(lit.Type) : null;
            return SqlExpr.Of(typeName == null ? "NULL" : $"CAST(NULL AS {typeName})", lit.Type, SqlProps.None);
        }

        var t = lit.Type;
        switch (t.Kind)
        {
            case TypeKind.Bool:
                return new SqlExpr(lit.BoolValue ? "CAST(1 AS bit)" : "CAST(0 AS bit)", lit.BoolValue ? "(1 = 1)" : "(1 = 0)", t, SqlProps.None);
            case TypeKind.I16:
                return SqlExpr.Of($"CAST({TSql.Int(lit.IntValue)} AS smallint)", t, SqlProps.None);
            case TypeKind.I32:
                // Integer literals in the int range are ints.
                return SqlExpr.Of(lit.IntValue < 0 ? $"({TSql.Int(lit.IntValue)})" : TSql.Int(lit.IntValue), t, SqlProps.None);
            case TypeKind.I64:
                return SqlExpr.Of($"CAST({TSql.Int(lit.IntValue)} AS bigint)", t, SqlProps.None);
            case TypeKind.Fp32:
                return SqlExpr.Of($"CAST({FloatText(lit.FloatValue, (float)lit.FloatValue)} AS real)", t, SqlProps.None);
            case TypeKind.Fp64:
                return SqlExpr.Of(FloatText(lit.FloatValue, null), t, SqlProps.None);
            case TypeKind.String:
            case TypeKind.VarChar:
            case TypeKind.FixedChar:
                return SqlExpr.Of(Collate(TSql.Unicode(lit.StringValue ?? "")), t, SqlProps.None);
            case TypeKind.Binary:
            case TypeKind.FixedBinary:
                return SqlExpr.Of(TSql.Binary(lit.BytesValue!), t, SqlProps.None);
            case TypeKind.Date:
            {
                // Days beyond TimeSpan's range would overflow in ticks (and could wrap to a date in range).
                long ticks = lit.IntValue is >= -TimeSpanMaxDays and <= TimeSpanMaxDays ? lit.IntValue * TimeSpan.TicksPerDay : long.MaxValue;
                return SqlExpr.Of($"CAST('{DateTimeOf(ticks).ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture)}' AS date)", t, SqlProps.None);
            }
            case TypeKind.PrecisionTimestamp:
            case TypeKind.PrecisionTimestampTz:
            {
                long ticks = ToTicks(lit.IntValue, t.Precision, "timestamp");
                int precision = Math.Min(t.Precision, TSql.MaxTimePrecision);
                string text = DateTimeOf(ticks).ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss" + Fraction(precision), CultureInfo.InvariantCulture);
                return t.Kind == TypeKind.PrecisionTimestamp
                    ? SqlExpr.Of($"CAST('{text}' AS datetime2({precision}))", t, SqlProps.None)
                    : SqlExpr.Of($"CAST('{text}+00:00' AS datetimeoffset({precision}))", t, SqlProps.None);
            }
            case TypeKind.PrecisionTime:
            {
                long ticks = ToTicks(lit.IntValue, t.Precision, "time");
                if (ticks < 0 || ticks >= TimeSpan.TicksPerDay)
                {
                    throw SubstraitTranslationException.Unsupported("The time literal is outside a day.");
                }
                int precision = Math.Min(t.Precision, TSql.MaxTimePrecision);
                string text = new DateTime(ticks, DateTimeKind.Unspecified).ToString("HH':'mm':'ss" + Fraction(precision), CultureInfo.InvariantCulture);
                return SqlExpr.Of($"CAST('{text}' AS time({precision}))", t, SqlProps.None);
            }
            case TypeKind.Decimal:
                return SqlExpr.Of($"CAST({DecimalText(lit)} AS decimal({t.Precision},{t.Scale}))", t, SqlProps.None);
            case TypeKind.Uuid:
                return SqlExpr.Of($"CAST('{GuidText(lit.BytesValue!)}' AS uniqueidentifier)", t, SqlProps.None);
            case TypeKind.IntervalDay:
            case TypeKind.IntervalYear:
                throw SubstraitTranslationException.Unsupported("Interval literals are only supported as the interval argument of datetime add and subtract; SQL Server has no interval type.");
            case TypeKind.I8:
                throw SubstraitTranslationException.Unsupported("i8 literals are not supported; SQL Server's tinyint is unsigned.");
            default:
                throw SubstraitTranslationException.Unsupported($"Literals of type '{t}' are not supported.");
        }
    }

    private static string Fraction(int precision) => precision == 0 ? "" : "'.'" + new string('f', precision);

    private static string FloatText(double value, float? single)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw SubstraitTranslationException.Unsupported("NaN and infinite floating-point literals are not supported; SQL Server's float cannot hold them.");
        }
        string text = single.HasValue
            ? single.Value.ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
        // A literal with an exponent is a float in T-SQL; without one it is a decimal.
        if (text.IndexOf('E') < 0)
        {
            text += "E0";
        }
        return text.StartsWith("-", StringComparison.Ordinal) ? "(" + text + ")" : text;
    }

    /// <summary>Converts a value in units of 10^-precision seconds to 100ns ticks.</summary>
    internal static long ToTicks(long value, int precision, string what)
    {
        if (precision < 0 || precision > 12)
        {
            throw SubstraitTranslationException.MalformedPlan($"Invalid {what} precision {precision}.");
        }
        if (precision <= TSql.MaxTimePrecision)
        {
            long factor = Pow10(TSql.MaxTimePrecision - precision);
            if (value > long.MaxValue / factor || value < long.MinValue / factor)
            {
                throw SubstraitTranslationException.Unsupported($"The {what} literal is outside the range SQL Server can represent.");
            }
            return value * factor;
        }
        long divisor = Pow10(precision - TSql.MaxTimePrecision);
        if (value % divisor != 0)
        {
            throw SubstraitTranslationException.Unsupported($"The {what} literal has sub-100ns precision, which SQL Server cannot represent.");
        }
        return value / divisor;
    }

    internal static long Pow10(int exponent)
    {
        long result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }
        return result;
    }

    private static readonly long s_unixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>TimeSpan.MaxValue.Days: the most days whose ticks fit in a long.</summary>
    private const long TimeSpanMaxDays = 10_675_199;

    private static DateTime DateTimeOf(long ticksSinceEpoch)
    {
        if (ticksSinceEpoch < DateTime.MinValue.Ticks - s_unixEpochTicks || ticksSinceEpoch > DateTime.MaxValue.Ticks - s_unixEpochTicks)
        {
            throw SubstraitTranslationException.Unsupported("The date or timestamp literal is outside the range SQL Server can represent (years 1 to 9999).");
        }
        return new DateTime(s_unixEpochTicks + ticksSinceEpoch, DateTimeKind.Utc);
    }

    private static string DecimalText(LiteralExpr lit)
    {
        var unscaled = new BigInteger(lit.BytesValue!);
        int scale = lit.Type.Scale;
        if (lit.Type.Precision is < 1 or > TSql.MaxDecimalPrecision || scale < 0 || scale > lit.Type.Precision)
        {
            throw SubstraitTranslationException.Unsupported($"Decimal literals of type decimal({lit.Type.Precision},{scale}) are not supported.");
        }
        string digits = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
        if (digits.Length <= scale)
        {
            digits = new string('0', scale - digits.Length + 1) + digits;
        }
        string text = scale == 0 ? digits : digits.Substring(0, digits.Length - scale) + "." + digits.Substring(digits.Length - scale);
        return unscaled.Sign < 0 ? "-" + text : text;
    }

    private static string GuidText(byte[] bytes)
    {
        // Substrait UUIDs are the 16 bytes in RFC 4122 (big-endian) order, as is the text form.
        var hex = new StringBuilder(36);
        for (int i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10)
            {
                hex.Append('-');
            }
            hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return hex.ToString();
    }

    #endregion
}
