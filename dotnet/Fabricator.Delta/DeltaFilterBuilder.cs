// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using EngineeredWood.Expressions;

namespace Fabricator.Bridge;

/// <summary>
/// Maps a pushed-down <see cref="FilterNode"/> tree (from a scan's <c>spec_json</c>) into an
/// engineered-wood <see cref="Predicate"/>, for Delta <b>file + row-group skipping</b> (the predicate is
/// evaluated against partition values + Parquet column statistics; a file/row-group whose stats prove no row
/// can match is skipped before any data pages are read). engineered-wood does NOT re-apply the predicate per
/// row — and DuckDB re-applies every predicate above the scan anyway — so this must be <b>superset-safe</b>:
/// only skip when CERTAIN nothing matches.
///
/// <para>This builder maps faithfully whatever the C++ encoder emits — the superset-safety policy lives
/// UPSTREAM in the C++ <c>FilterSerializer</c> (fabricator_table_entry.cpp). For a Delta reader the encoder is
/// told the source is byte-ordered (<c>string_order_pushable=true</c>, since Parquet min/max stats are
/// byte-ordered like DuckDB's default binary string comparison), so it emits ALL comparisons + <c>IN</c> for
/// every type including strings; DuckDB re-applies regardless, so any mismatch only forfeits pruning.</para>
/// <list type="bullet">
/// <item>All comparisons (<c>=</c> <c>&lt;&gt;</c> <c>&lt;</c> <c>&lt;=</c> <c>&gt;</c> <c>&gt;=</c>) + <c>IN</c>
/// map to the matching predicate; <c>is_null</c> / <c>is_not_null</c> map.</item>
/// <item><c>and</c> keeps its pushable children (dropping one still yields a superset); <c>or</c> is
/// all-or-nothing (dropping a branch would narrow the result).</item>
/// </list>
/// A node (or value type) that can't be mapped renders to <c>null</c> = "don't push it". Temporal /
/// GUID / binary literals are not pushed in this iteration (a future extension — the literal-vs-stat type
/// pairing needs the column's physical type to stay sound).
/// </summary>
internal sealed class DeltaFilterBuilder
{
    private readonly IReadOnlyList<object?> _values;
    private readonly bool _exact;

    public DeltaFilterBuilder(IReadOnlyList<object?> values) : this(values, exact: false) { }

    private DeltaFilterBuilder(IReadOnlyList<object?> values, bool exact)
    {
        _values = values;
        _exact = exact;
    }

    /// <summary>
    /// The EXACT twin, for a predicate DELETE (<see cref="DeltaCatalog.DeleteWhere"/>): the predicate engineered-wood
    /// evaluates IS the user's — nothing re-checks a delete — so no node may be dropped, an <c>and</c> keeps every
    /// child or fails, and whatever cannot be rendered THROWS rather than widening. <c>null</c> = every row.
    /// </summary>
    /// <remarks>The host sends only what its strict serializer rendered exactly (one shape per node, constants of
    /// the column's own type), so a throw here means the two sides disagree about a shape — a loud failure of the
    /// statement, never a different set of rows.</remarks>
    public static Predicate BuildExact(FilterNode? node, IReadOnlyList<object?> values)
    {
        if (node is null)
        {
            return Expressions.True;
        }
        return new DeltaFilterBuilder(values, exact: true).Build(node)
            ?? throw new System.NotSupportedException(
                "fabricator: this DELETE predicate has no exact Delta form (node '" + node.Op + "')");
    }

    /// <summary>Maps the node to a predicate, or null if it can't be safely pushed.</summary>
    public Predicate? Build(FilterNode? node) => node?.Op switch
    {
        "and" => BuildAnd(node),
        "or" => BuildOr(node),
        "compare" => BuildCompare(node),
        "is_null" => RefName(node) is { } c ? Expressions.IsNull(c) : null,
        "is_not_null" => RefName(node) is { } c ? Expressions.IsNotNull(c) : null,
        "in" => BuildIn(node),
        _ => null,
    };

    // A STRUCT-member reference arrives as `path` (["s","a"]); engineered-wood's evaluators resolve the
    // dotted form at every layer — flattened Delta file stats ("s.a"), Parquet row-group leaf paths
    // (ColumnDescriptor.DottedPath) and bloom probing. A plain column stays `col`. Ambiguity (a literal
    // dotted column name colliding with a struct path) is poisoned engineered-wood-side, never guessed.
    private static string? RefName(FilterNode node) =>
        node.Path is { Count: > 0 } p ? string.Join(".", p) : node.Col;

    // AND: keep the pushable children (dropping unpushable ones still yields a superset). EXACT: every child or
    // nothing — a dropped conjunct would delete rows the user did not name.
    private Predicate? BuildAnd(FilterNode node)
    {
        var built = (node.Children ?? new List<FilterNode>()).Select(Build).ToArray();
        if (_exact && (built.Length == 0 || built.Any(p => p is null)))
        {
            return null;
        }
        var parts = built.Where(p => p is not null).Select(p => p!).ToArray();
        return parts.Length == 0 ? null : Expressions.And(parts);
    }

    // OR: every child must be pushable (dropping a branch would narrow the result → unsafe).
    private Predicate? BuildOr(FilterNode node)
    {
        var children = node.Children ?? new List<FilterNode>();
        if (children.Count == 0)
        {
            return null;
        }
        var parts = new List<Predicate>(children.Count);
        foreach (var child in children)
        {
            var p = Build(child);
            if (p is null)
            {
                return null;
            }
            parts.Add(p);
        }
        return Expressions.Or(parts.ToArray());
    }

    private Predicate? BuildCompare(FilterNode node)
    {
        if (RefName(node) is not { } col || node.Val is not int idx || idx < 0 || idx >= _values.Count)
        {
            return null;
        }
        var value = _values[idx];
        if (value is null || Lit(value, _exact) is not { } lit)
        {
            return null; // null constant (host emits is_null separately) or unmappable type
        }
        if (_exact)
        {
            // The two null-safe comparisons, exact against a NON-null constant (the host never sends a null
            // one): NOT DISTINCT is plain equality, and DISTINCT is also TRUE for a NULL row — which `<>` alone
            // would leave unknown, i.e. not deleted.
            if (node.Cmp == "is_not_distinct")
            {
                return Expressions.Equal(col, lit);
            }
            if (node.Cmp == "is_distinct")
            {
                return Expressions.Or(Expressions.NotEqual(col, lit), Expressions.IsNull(col));
            }
        }
        // All comparisons push for any type, INCLUDING strings: Parquet min/max statistics are byte-ordered
        // (UTF-8 binary), which matches DuckDB's default binary string comparison — so byte-order pruning is a
        // correct superset. (Only a non-binary string collation could differ, and that risk applies equally to
        // '=' / IN; DuckDB re-applies every predicate above the scan, so any mismatch only forfeits pruning,
        // never correctness.)
        return node.Cmp switch
        {
            "=" => Expressions.Equal(col, lit),
            "<>" => Expressions.NotEqual(col, lit),
            "<" => Expressions.LessThan(col, lit),
            "<=" => Expressions.LessThanOrEqual(col, lit),
            ">" => Expressions.GreaterThan(col, lit),
            ">=" => Expressions.GreaterThanOrEqual(col, lit),
            _ => null,                                                               // is_distinct / is_not_distinct
        };
    }

    private Predicate? BuildIn(FilterNode node)
    {
        if (RefName(node) is not { } col || node.Vals is not { Count: > 0 } idxs)
        {
            return null;
        }
        var lits = new List<LiteralValue>(idxs.Count);
        foreach (var i in idxs)
        {
            if (i < 0 || i >= _values.Count || _values[i] is not { } v || Lit(v, _exact) is not { } lit)
            {
                return null;
            }
            lits.Add(lit);
        }
        return Expressions.In(Expressions.Ref(col), lits);
    }

    /// <summary>Maps a CLR constant (as read by <see cref="ArrowValueReader"/>) to an engineered-wood literal,
    /// or null if its type isn't safely pushable here.</summary>
    private static LiteralValue? Lit(object value, bool exact) => value switch
    {
        // A DATE constant arrives as a DateTime at midnight (ArrowValueReader's Date32). EXACT mode only: the
        // row evaluator compares a calendar date against a date column directly, while the pruning path keeps
        // temporal literals out until their stats pairing is settled (see the class remarks).
        System.DateTime d when exact && d.TimeOfDay == System.TimeSpan.Zero => LiteralValue.Of(System.DateOnly.FromDateTime(d)),
        bool b => (LiteralValue?)LiteralValue.Of(b),
        sbyte v => LiteralValue.Of((int)v),
        short v => LiteralValue.Of((int)v),
        int v => LiteralValue.Of(v),
        byte v => LiteralValue.Of((int)v),
        ushort v => LiteralValue.Of((int)v),
        uint v => LiteralValue.Of(v),
        long v => LiteralValue.Of(v),
        ulong v => LiteralValue.Of(v),
        float v => LiteralValue.Of(v),
        double v => LiteralValue.Of(v),
        decimal v => LiteralValue.Of(v),
        string v => LiteralValue.Of(v),
        _ => null, // temporal / GUID / binary: not pushed this iteration (DuckDB re-applies regardless)
    };
}
