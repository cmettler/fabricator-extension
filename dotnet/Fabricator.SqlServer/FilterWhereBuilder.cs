// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Text;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

/// <summary>
/// Renders a pushed-down <see cref="FilterNode"/> tree into a parameterized
/// T-SQL <c>WHERE</c> clause. Constants come from the filter value batch (by
/// index) and become <see cref="SqlParameter"/>s — so there is no literal
/// escaping, and SQL Server infers types from the CLR values.
///
/// The C++ host only emits superset-safe predicates, so this builder does not
/// re-check safety; it throws on anything it can't render, and the caller falls
/// back to no WHERE (DuckDB still applies every filter).
/// </summary>
internal sealed class FilterWhereBuilder
{
    private readonly IReadOnlyList<object?> _values;
    private readonly bool _openJson;

    /// <summary>More values than this go as ONE parameter, a JSON array read with <c>OPENJSON</c>; up to it, one
    /// parameter each. 50 is DuckDB's default <c>dynamic_or_filter_threshold</c>, so a join's key list goes as before
    /// unless the session raised it (docs/duckdb-2.0-remote-execution.md §5.3).</summary>
    internal const int JsonListThreshold = 50;

    /// <summary>SQL Server takes at most 2100 parameters per statement; a list that cannot go as JSON and would pass
    /// this is refused here rather than by the server.</summary>
    internal const int MaxListParameters = 2000;

    public FilterWhereBuilder(IReadOnlyList<object?> values, bool openJson = false)
    {
        _values = values;
        _openJson = openJson;
    }

    public List<SqlParameter> Parameters { get; } = new();

    public string Build(FilterNode node) => node.Op switch
    {
        "and" => Conjunction(node, " AND "),
        "or" => Conjunction(node, " OR "),
        "compare" => Compare(node),
        "is_null" => $"{Col(node)} IS NULL",
        "is_not_null" => $"{Col(node)} IS NOT NULL",
        "in" => In(node),
        _ => throw new NotSupportedException($"fabricator: unsupported filter op '{node.Op}'"),
    };

    /// <summary>
    /// Renders one SET value of a predicate UPDATE (ABI v94). Exact, never best-effort: an unknown node throws,
    /// and the statement fails rather than writing a different value.
    /// </summary>
    /// <remarks>Arithmetic is parenthesised so the tree's own grouping is what SQL Server evaluates; the host
    /// only sends INTEGER operands, whose results agree between the engines whenever both succeed.</remarks>
    public string BuildValue(ValueNode node) => node.Op switch
    {
        "const" => Param(node.Val ?? throw new InvalidOperationException("const without val")),
        "col" => "[" + (node.Col ?? throw new InvalidOperationException("col without name")).Replace("]", "]]") + "]",
        "+" or "-" or "*" => Arithmetic(node),
        _ => throw new NotSupportedException($"fabricator: unsupported SET value op '{node.Op}'"),
    };

    private string Arithmetic(ValueNode node)
    {
        var children = node.Children;
        if (children is not { Count: 2 })
        {
            throw new InvalidOperationException($"arithmetic '{node.Op}' needs two operands");
        }
        return $"({BuildValue(children[0])} {node.Op} {BuildValue(children[1])})";
    }

    private string Conjunction(FilterNode node, string sep)
    {
        var children = node.Children ?? throw new InvalidOperationException("conjunction without children");
        var sb = new StringBuilder("(");
        for (int i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(sep);
            }
            sb.Append(Build(children[i]));
        }
        return sb.Append(')').ToString();
    }

    private string Compare(FilterNode node)
    {
        var op = node.Cmp switch
        {
            "=" => "=",
            "<>" => "<>",
            "<" => "<",
            "<=" => "<=",
            ">" => ">",
            ">=" => ">=",
            "is_distinct" => "IS DISTINCT FROM",         // SQL Server 2022+
            "is_not_distinct" => "IS NOT DISTINCT FROM", // SQL Server 2022+
            _ => throw new NotSupportedException($"fabricator: unsupported comparison '{node.Cmp}'"),
        };
        var idx = node.Val ?? throw new InvalidOperationException("compare without val");
        return $"{Col(node)} {op} {Param(idx, equality: op is "=" or "IS NOT DISTINCT FROM")}";
    }

    private string In(FilterNode node)
    {
        var idxs = node.Vals ?? throw new InvalidOperationException("in without vals");
        if (idxs.Count > JsonListThreshold && _openJson && JsonIntegerType(idxs) is { } sqlType)
        {
            // A long key list (a hash join's keys under a raised dynamic_or_filter_threshold) as one JSON parameter:
            // no 2100-parameter cap, one plan for any list length, and MEASURED as fast as the parameter list or
            // faster — 411 keys over 6M rows 0.17 s vs 0.22 s, 10 664 keys 0.46 s where the range alone took 7.8 s.
            // Integers only: their JSON form is exact, and the type matches the column's.
            var name = "@p" + Parameters.Count;
            var json = new StringBuilder("[");
            for (int i = 0; i < idxs.Count; i++)
            {
                json.Append(i == 0 ? "" : ",").Append(Convert.ToString(_values[idxs[i]], System.Globalization.CultureInfo.InvariantCulture));
            }
            json.Append(']');
            Parameters.Add(new SqlParameter(name, System.Data.SqlDbType.NVarChar, -1) { Value = json.ToString() });
            return $"{Col(node)} IN (SELECT [v] FROM OPENJSON({name}) WITH ([v] {sqlType} '$'))";
        }
        if (idxs.Count > MaxListParameters)
        {
            throw new NotSupportedException($"fabricator: an IN list of {idxs.Count} values exceeds the parameter limit");
        }
        return $"{Col(node)} IN ({string.Join(", ", idxs.Select(i => Param(i, equality: true)))})";
    }

    /// <summary>The T-SQL type of an IN list whose values are all integers of one CLR type, else null.</summary>
    private string? JsonIntegerType(IReadOnlyList<int> idxs)
    {
        string? type = null;
        foreach (var i in idxs)
        {
            string? t = _values[i] switch
            {
                long => "bigint",
                int => "int",
                short => "smallint",
                byte => "tinyint",
                _ => null,
            };
            if (t is null || (type is not null && t != type))
            {
                return null;
            }
            type = t;
        }
        return type;
    }

    private static string Col(FilterNode node)
    {
        var name = node.Col ?? throw new InvalidOperationException("node without col");
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// Binds one pushed filter value as a parameter and returns its placeholder.
    /// </summary>
    /// <remarks>
    /// <para><b>⚠⚠ A <see cref="DateTime"/> IS PINNED TO <c>datetime2</c>, AND LETTING SqlClient INFER IT IS
    /// A SILENT DATA-LOSS BUG.</b> Inference maps a CLR <c>DateTime</c> to <c>SqlDbType.DateTime</c> — the
    /// LEGACY type, whose resolution is ~3.33 ms — so the value is ROUNDED TO THE NEAREST TICK before the
    /// server ever sees it. MEASURED over 3400 consecutive microsecond offsets: 1732 round DOWN and
    /// <b>1667 round UP</b>. Rounding down is harmless (the pushed predicate admits a superset and DuckDB
    /// re-applies it), but rounding UP makes a <c>&gt;</c> predicate STRICTER, so matching rows are dropped
    /// on the SERVER and never reach the re-application that would have saved them. That breaks the
    /// never-erases rule this whole pushdown rests on.</para>
    /// <para><b>⚠ It was NOT reachable before 2026-08-25</b>, and only by accident:
    /// <c>ArrowValueReader.ReadTimestamp</c> had a ternary-unification bug that made every timestamp arrive
    /// as a <c>DateTimeOffset</c>, which SqlClient maps to <c>datetimeoffset</c> — full precision, so no
    /// loss. Fixing that bug is what made the inference reachable, which is why the two changes belong in
    /// one commit.</para>
    /// <para><b>⚠ <c>datetime2</c> rather than <c>datetime</c> or <c>datetimeoffset</c>, and each rejection
    /// is measured.</b> Against an indexed <c>datetime2</c> column, a <c>datetime2</c> parameter gives a
    /// direct <c>Index Seek(SEEK: dt &gt; @p)</c> that parallelises, where a <c>datetimeoffset</c> one goes
    /// through <c>GetRangeWithMismatchedTypes</c> — a constant scan and a nested loop computing an
    /// equivalent range first — and did not parallelise. Against a legacy <c>datetime</c> or a <c>date</c>
    /// column it is the mismatched-types seek instead, which is what <c>datetimeoffset</c> already was for
    /// BOTH, and the widening conversion is lossless so no row is dropped.</para>
    /// <para>⚠ A <see cref="DateTimeOffset"/> is left to inference deliberately: it maps to
    /// <c>datetimeoffset</c>, which is the MATCHING type for the only column type that produces one.</para>
    /// <para><b>An ASCII string compared for EQUALITY is bound as <c>varchar</c>, not inferred
    /// <c>nvarchar</c>.</b> Against a <c>varchar</c> column under a SQL_ collation an <c>nvarchar</c> value
    /// converts the COLUMN, so the index is scanned instead of sought — measured on 1M rows, a join's three keys:
    /// 650 ms CPU against 60 ms. ASCII has the same bytes in every code page, so the row that equals the value
    /// still equals it under any collation: the server's WHERE stays a superset of what the host re-applies.
    /// Not for ranges or <c>&lt;&gt;</c>, where the comparison's ORDER would change with the type.</para>
    /// </remarks>
    private string Param(int valueIndex, bool equality = false)
    {
        var name = "@p" + Parameters.Count;
        var value = _values[valueIndex];
        // ⚠ ONE rule, in ONE place. SqlServerCatalog.MakeParameter applies the datetime2 pin above; the
        // caller-supplied `params` bag (ABI v88) goes through the same helper, so a fix to the inference
        // rule cannot reach one surface and miss the other.
        Parameters.Add(equality && value is string text && IsAscii(text)
            ? new SqlParameter(name, System.Data.SqlDbType.VarChar, text.Length <= 8000 ? 8000 : -1) { Value = text }
            : SqlServerCatalog.MakeParameter(name, value));
        return name;
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > '\u007f')
            {
                return false;
            }
        }
        return true;
    }
}
