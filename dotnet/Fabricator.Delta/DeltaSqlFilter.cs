// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Fabricator.Bridge;

/// <summary>
/// Renders a pushed-down <see cref="FilterNode"/> tree into a DuckDB SQL <c>WHERE</c> fragment for the native
/// Delta reader, so <c>read_parquet(...)</c> prunes row-groups by the static filter. This is <b>best-effort +
/// superset-safe</b>: the native reader keeps <c>filter_pushdown = false</c> (the catalog scan uses
/// <c>pushdown_complex_filter</c>), so DuckDB re-applies every predicate ABOVE the scan — a partial or
/// over-approximate WHERE only forfeits pruning, never correctness. An unmappable node renders to <c>null</c>
/// ("don't push it"): <c>and</c> drops the child (still a superset), <c>or</c> is all-or-nothing.
///
/// <para>Constants come from the same Arrow value batch (<see cref="FilterNode.Val"/>/<see cref="FilterNode.Vals"/>
/// index) already read by <see cref="ArrowValueReader"/>; they are rendered inline as SQL literals (numbers/
/// bool/decimal/string only — temporal/GUID/binary are not pushed here, matching <see cref="DeltaFilterBuilder"/>).</para>
/// </summary>
internal static class DeltaSqlFilter
{
    /// <summary>Renders <paramref name="node"/> to a SQL WHERE fragment, or null if nothing pushable.</summary>
    public static string? ToWhere(FilterNode? node, IReadOnlyList<object?> values) => node?.Op switch
    {
        "and" => And(node, values),
        "or" => Or(node, values),
        "compare" => Compare(node, values),
        "is_null" => Ref(node) is { } c ? $"{c} IS NULL" : null,
        "is_not_null" => Ref(node) is { } c ? $"{c} IS NOT NULL" : null,
        "in" => In(node, values),
        _ => null,
    };

    // The column reference: a plain column, or a struct-member path rendered as an explicit struct_extract
    // chain — exact DuckDB SQL (the native reader rebuilds mapped structs with LOGICAL member names, so the
    // logical reference binds on mapped tables too).
    private static string? Ref(FilterNode node)
    {
        if (node.Path is { Count: > 0 } p)
        {
            var expr = Quote(p[0]);
            for (int i = 1; i < p.Count; i++)
            {
                expr = $"struct_extract({expr}, '{p[i].Replace("'", "''")}')";
            }
            return expr;
        }
        return node.Col is { } c ? Quote(c) : null;
    }

    private static string? And(FilterNode node, IReadOnlyList<object?> values)
    {
        var parts = (node.Children ?? new List<FilterNode>())
            .Select(c => ToWhere(c, values)).Where(s => s is not null).Select(s => s!).ToArray();
        return parts.Length == 0 ? null : "(" + string.Join(" AND ", parts) + ")";
    }

    private static string? Or(FilterNode node, IReadOnlyList<object?> values)
    {
        var children = node.Children ?? new List<FilterNode>();
        if (children.Count == 0)
        {
            return null;
        }
        var parts = new List<string>(children.Count);
        foreach (var child in children)
        {
            var s = ToWhere(child, values);
            if (s is null)
            {
                return null; // dropping an OR branch would narrow the result → unsafe
            }
            parts.Add(s);
        }
        return "(" + string.Join(" OR ", parts) + ")";
    }

    private static string? Compare(FilterNode node, IReadOnlyList<object?> values)
    {
        if (Ref(node) is not { } col || node.Val is not int idx || idx < 0 || idx >= values.Count)
        {
            return null;
        }
        if (values[idx] is not { } v || Lit(v) is not { } lit)
        {
            return null;
        }
        var op = node.Cmp switch
        {
            "=" => "=",
            "<>" => "<>",
            "<" => "<",
            "<=" => "<=",
            ">" => ">",
            ">=" => ">=",
            _ => null, // is_distinct / is_not_distinct: not pushed
        };
        return op is null ? null : $"{col} {op} {lit}";
    }

    private static string? In(FilterNode node, IReadOnlyList<object?> values)
    {
        if (Ref(node) is not { } col || node.Vals is not { Count: > 0 } idxs)
        {
            return null;
        }
        var lits = new List<string>(idxs.Count);
        foreach (var i in idxs)
        {
            if (i < 0 || i >= values.Count || values[i] is not { } v || Lit(v) is not { } lit)
            {
                return null;
            }
            lits.Add(lit);
        }
        return $"{col} IN ({string.Join(", ", lits)})";
    }

    // Renders a CLR constant to a SQL literal, or null if its type isn't pushed here (matches DeltaFilterBuilder).
    private static string? Lit(object value) => value switch
    {
        bool b => b ? "TRUE" : "FALSE",
        sbyte v => v.ToString(CultureInfo.InvariantCulture),
        short v => v.ToString(CultureInfo.InvariantCulture),
        int v => v.ToString(CultureInfo.InvariantCulture),
        long v => v.ToString(CultureInfo.InvariantCulture),
        byte v => v.ToString(CultureInfo.InvariantCulture),
        ushort v => v.ToString(CultureInfo.InvariantCulture),
        uint v => v.ToString(CultureInfo.InvariantCulture),
        ulong v => v.ToString(CultureInfo.InvariantCulture),
        float v => v.ToString("R", CultureInfo.InvariantCulture),
        double v => v.ToString("R", CultureInfo.InvariantCulture),
        decimal v => v.ToString(CultureInfo.InvariantCulture),
        string v => "'" + v.Replace("'", "''") + "'",
        _ => null, // temporal / GUID / binary: not pushed (DuckDB re-applies regardless)
    };

    private static string Quote(string col) => "\"" + col.Replace("\"", "\"\"") + "\"";
}
