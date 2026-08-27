// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Globalization;
using Fabricator.Bridge;

namespace Fabricator.AnalysisServices;

/// <summary>
/// Renders a pushed-down <see cref="FilterNode"/> tree into a DAX boolean predicate for use inside
/// <c>FILTER('T', &lt;pred&gt;)</c>. The VertiPaq engine pushes the storage-engine-friendly parts down and
/// iterates the rest in the formula engine. DAX has no parameters, so constants are inlined as DAX literals.
///
/// <para><b>Safety.</b> Pushdown is best-effort and DuckDB re-applies every predicate above the scan, so a
/// <em>superset</em> is correct but a <em>subset</em> (dropping a truly-matching row) is not. DAX differs
/// from SQL in two ways that matter:</para>
/// <list type="bullet">
/// <item>String comparison is <b>case-insensitive</b> by default. So <c>=</c> / <c>IN</c> on strings returns
/// a <em>superset</em> of DuckDB's case-sensitive match (safe), but <c>&lt;&gt;</c> on strings returns a
/// <em>subset</em> (it also excludes case-variants DuckDB keeps) — so string <c>&lt;&gt;</c> is NOT pushed.
/// String ordering (<c>&lt;</c>/<c>&gt;</c>) can differ by collation — also NOT pushed.</item>
/// <item><c>BLANK</c> coercion errs toward <em>including</em> rows — a superset — so it's safe.</item>
/// </list>
/// <para>So: <c>=</c> and <c>IN</c> push for any type; <c>&lt;&gt;</c>/<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/
/// <c>&gt;=</c> push only for non-string values. A node that can't be safely pushed renders to <c>null</c>;
/// for an <c>and</c> the unpushable children are simply dropped (still a superset), but an <c>or</c> with any
/// unpushable child can't be pushed at all (dropping a branch would narrow the result). The whole filter is
/// optional — if nothing is pushable the scan runs unfiltered and DuckDB filters.</para>
/// </summary>
internal sealed class DaxFilterBuilder
{
    private readonly IReadOnlyList<object?> _values;
    private readonly string _tableRef; // e.g. 'pai vwPAIFlat'

    public DaxFilterBuilder(IReadOnlyList<object?> values, string tableRef)
    {
        _values = values;
        _tableRef = tableRef;
    }

    /// <summary>Renders the node to a DAX predicate, or null if it can't be safely pushed.</summary>
    public string? Render(FilterNode node) => node.Op switch
    {
        "and" => RenderAnd(node),
        "or" => RenderOr(node),
        "compare" => RenderCompare(node),
        "is_null" => $"ISBLANK({Col(node)})",
        "is_not_null" => $"NOT(ISBLANK({Col(node)}))",
        "in" => RenderIn(node),
        _ => null,
    };

    // AND: keep the pushable children (dropping unpushable ones still yields a superset).
    private string? RenderAnd(FilterNode node)
    {
        var parts = (node.Children ?? new()).Select(Render).Where(p => p is not null).ToList();
        return parts.Count == 0 ? null : "(" + string.Join(" && ", parts) + ")";
    }

    // OR: every child must be pushable (dropping a branch would narrow the result → unsafe).
    private string? RenderOr(FilterNode node)
    {
        var children = node.Children ?? new();
        var parts = new List<string>(children.Count);
        foreach (var child in children)
        {
            var p = Render(child);
            if (p is null)
            {
                return null;
            }
            parts.Add(p);
        }
        return parts.Count == 0 ? null : "(" + string.Join(" || ", parts) + ")";
    }

    private string? RenderCompare(FilterNode node)
    {
        if (node.Val is not int idx || idx < 0 || idx >= _values.Count)
        {
            return null;
        }
        var value = _values[idx];
        if (value is null)
        {
            return null; // a null constant — the host emits is_null separately; skip
        }
        var lit = Lit(value);
        if (lit is null)
        {
            return null;
        }
        bool isString = value is string;
        switch (node.Cmp)
        {
            case "=":
                // string: case-insensitive => superset (safe); numeric/date: exact.
                return $"{Col(node)} = {lit}";
            case "<>":
            case "<":
            case "<=":
            case ">":
            case ">=":
                // Safe only for non-string values (string ordering/case differs from DuckDB → could drop rows).
                return isString ? null : $"{Col(node)} {node.Cmp} {lit}";
            default:
                return null; // is_distinct / is_not_distinct — no DAX equivalent
        }
    }

    private string? RenderIn(FilterNode node)
    {
        if (node.Vals is not { Count: > 0 } idxs)
        {
            return null;
        }
        var lits = new List<string>(idxs.Count);
        foreach (var i in idxs)
        {
            if (i < 0 || i >= _values.Count)
            {
                return null;
            }
            var value = _values[i];
            if (value is null)
            {
                return null;
            }
            var lit = Lit(value);
            if (lit is null)
            {
                return null;
            }
            lits.Add(lit);
        }
        // string IN: case-insensitive => superset (safe); numeric: exact.
        return $"{Col(node)} IN {{ {string.Join(", ", lits)} }}";
    }

    private string Col(FilterNode node)
    {
        var name = node.Col ?? throw new InvalidOperationException("filter node without col");
        return $"{_tableRef}[{name}]";
    }

    /// <summary>Formats a CLR constant as a DAX literal, or null if the type has no safe DAX literal form.</summary>
    private static string? Lit(object value) => value switch
    {
        string s => "\"" + s.Replace("\"", "\"\"") + "\"",
        bool b => b ? "TRUE()" : "FALSE()",
        DateTime dt => DateLit(dt),
        DateTimeOffset dto => DateLit(dto.DateTime),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null, // binary / unknown — don't push
    };

    private static string DateLit(DateTime dt)
        => $"(DATE({dt.Year},{dt.Month},{dt.Day})+TIME({dt.Hour},{dt.Minute},{dt.Second}))";
}
