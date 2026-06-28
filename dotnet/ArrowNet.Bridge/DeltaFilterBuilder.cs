using System.Collections.Generic;
using System.Linq;
using EngineeredWood.Expressions;

namespace ArrowNet.Bridge;

/// <summary>
/// Maps a pushed-down <see cref="FilterNode"/> tree (from a scan's <c>spec_json</c>) into an
/// engineered-wood <see cref="Predicate"/>, for Delta <b>file + row-group skipping</b> (the predicate is
/// evaluated against partition values + Parquet column statistics; a file/row-group whose stats prove no row
/// can match is skipped before any data pages are read). engineered-wood does NOT re-apply the predicate per
/// row — and DuckDB re-applies every predicate above the scan anyway — so this must be <b>superset-safe</b>:
/// only skip when CERTAIN nothing matches.
///
/// <para>Soundness rules (Parquet min/max stats are byte-ordered / binary, matching DuckDB's default binary
/// string comparison):</para>
/// <list type="bullet">
/// <item>All comparisons (<c>=</c> <c>&lt;&gt;</c> <c>&lt;</c> <c>&lt;=</c> <c>&gt;</c> <c>&gt;=</c>) and
/// <c>IN</c> push for any type, including strings — byte-order stats match DuckDB's default binary string
/// comparison. (A non-binary string collation could differ, but that risk applies equally to every operator
/// and DuckDB re-applies regardless, so a mismatch only forfeits pruning, never correctness.)</item>
/// <item><c>is_null</c> / <c>is_not_null</c> push.</item>
/// <item><c>and</c> keeps its pushable children (dropping one still yields a superset); <c>or</c> is
/// all-or-nothing (dropping a branch would narrow the result).</item>
/// </list>
/// A node (or value type) that can't be safely mapped renders to <c>null</c> = "don't push it". Temporal /
/// GUID / binary literals are not pushed in this iteration (a future extension — the literal-vs-stat type
/// pairing needs the column's physical type to stay sound).
/// </summary>
internal sealed class DeltaFilterBuilder
{
    private readonly IReadOnlyList<object?> _values;

    public DeltaFilterBuilder(IReadOnlyList<object?> values) => _values = values;

    /// <summary>Maps the node to a predicate, or null if it can't be safely pushed.</summary>
    public Predicate? Build(FilterNode? node) => node?.Op switch
    {
        "and" => BuildAnd(node),
        "or" => BuildOr(node),
        "compare" => BuildCompare(node),
        "is_null" => node.Col is { } c ? Expressions.IsNull(c) : null,
        "is_not_null" => node.Col is { } c ? Expressions.IsNotNull(c) : null,
        "in" => BuildIn(node),
        _ => null,
    };

    // AND: keep the pushable children (dropping unpushable ones still yields a superset).
    private Predicate? BuildAnd(FilterNode node)
    {
        var parts = (node.Children ?? new List<FilterNode>())
            .Select(Build).Where(p => p is not null).Select(p => p!).ToArray();
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
        if (node.Col is not { } col || node.Val is not int idx || idx < 0 || idx >= _values.Count)
        {
            return null;
        }
        var value = _values[idx];
        if (value is null || Lit(value) is not { } lit)
        {
            return null; // null constant (host emits is_null separately) or unmappable type
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
        if (node.Col is not { } col || node.Vals is not { Count: > 0 } idxs)
        {
            return null;
        }
        var lits = new List<LiteralValue>(idxs.Count);
        foreach (var i in idxs)
        {
            if (i < 0 || i >= _values.Count || _values[i] is not { } v || Lit(v) is not { } lit)
            {
                return null;
            }
            lits.Add(lit);
        }
        return Expressions.In(Expressions.Ref(col), lits);
    }

    /// <summary>Maps a CLR constant (as read by <see cref="ArrowValueReader"/>) to an engineered-wood literal,
    /// or null if its type isn't safely pushable here.</summary>
    private static LiteralValue? Lit(object value) => value switch
    {
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
