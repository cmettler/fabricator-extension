using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Fabricator.Bridge;

/// <summary>
/// The native reader's ROWID fast path. A filter on the transient rowid column
/// (<c>_metadata.row_id</c> = <c>(fileOrdinal &lt;&lt; 40) | positionInFile</c>) arrives from two sources:
/// DuckDB's <b>late-materialization</b> rewrite (ORDER BY … LIMIT n → TopN on a narrow scan + SEMI-join
/// back on rowid, whose dynamic join filter is a min/max range on the rowid) and user-written
/// <c>WHERE rowid = / IN (…)</c>. Because the rowid is a <b>locator</b>, such a filter decodes exactly:
/// the ordinal half selects the files (no stats needed), the position half becomes a per-file
/// <c>file_row_number</c> predicate — which DuckDB's parquet reader prunes ROW GROUPS with (it
/// synthesizes exact per-row-group min/max for <c>file_row_number</c>). So a rowid-filtered scan is
/// O(matched files), never a second full scan.
///
/// <para>Extraction is conjunct-level and superset-safe: only AND-reachable <c>compare</c>/<c>in</c>
/// nodes on the rowid column tighten the constraint; anything else (OR shapes, <c>&lt;&gt;</c>) is
/// ignored — the rendered SQL WHERE still applies the full predicate exactly (the per-file SELECT
/// aliases the rowid expression to its column name, and DuckDB permits SELECT-alias references in
/// WHERE), and DuckDB's semi join re-applies above the scan regardless.</para>
/// </summary>
internal sealed class DeltaRowIdFilter
{
    private const int PositionBits = 40;
    private const long PositionMask = (1L << PositionBits) - 1;

    private long _lo = long.MinValue;
    private long _hi = long.MaxValue;
    // Exact rowid set (WHERE rowid = / IN). Null => range-only. Pruned to [_lo,_hi] + grouped per
    // ordinal after extraction.
    private Dictionary<int, List<long>>? _positionsByOrdinal;

    /// <summary>Extracts the rowid constraint from a pushed filter tree, or null when the tree carries
    /// no AND-reachable rowid conjunct.</summary>
    public static DeltaRowIdFilter? Extract(FilterNode? node, IReadOnlyList<object?> values, string rowIdColumn)
    {
        if (node is null)
        {
            return null;
        }
        var f = new DeltaRowIdFilter();
        var exact = f.Walk(node, values, rowIdColumn, exact: null);
        bool hasExact = exact is not null;
        if (hasExact)
        {
            f._positionsByOrdinal = new Dictionary<int, List<long>>();
            foreach (var r in exact!)
            {
                if (r < f._lo || r > f._hi)
                {
                    continue;
                }
                int ord = (int)(r >> PositionBits);
                if (!f._positionsByOrdinal.TryGetValue(ord, out var list))
                {
                    f._positionsByOrdinal[ord] = list = new List<long>();
                }
                list.Add(r & PositionMask);
            }
        }
        return hasExact || f._lo != long.MinValue || f._hi != long.MaxValue ? f : null;
    }

    /// <summary>Removes AND-reachable conjuncts that mention the rowid column from the tree (for the
    /// engineered-wood file-pruning predicate, which has no rowid stats). Dropping a conjunct only
    /// WIDENS the predicate — superset-safe; the decode applies the rowid half exactly.</summary>
    public static FilterNode? Strip(FilterNode? node, string rowIdColumn)
    {
        if (node is null)
        {
            return null;
        }
        if (string.Equals(node.Op, "and", StringComparison.Ordinal))
        {
            var kept = (node.Children ?? new List<FilterNode>())
                .Select(c => Strip(c, rowIdColumn)).Where(c => c is not null).Select(c => c!).ToList();
            return kept.Count switch
            {
                0 => null,
                1 => kept[0],
                _ => new FilterNode { Op = "and", Children = kept },
            };
        }
        return Mentions(node, rowIdColumn) ? null : node;
    }

    private static bool Mentions(FilterNode node, string col)
    {
        if (string.Equals(node.Col, col, StringComparison.Ordinal))
        {
            return true;
        }
        return node.Children is { } cs && cs.Any(c => Mentions(c, col));
    }

    // Walks AND-reachable conjuncts, tightening the range and intersecting exact sets. Returns the
    // exact rowid set accumulated so far (null = no equality/IN constraint yet).
    private HashSet<long>? Walk(FilterNode node, IReadOnlyList<object?> values, string col,
                                HashSet<long>? exact)
    {
        switch (node.Op)
        {
            case "and":
                foreach (var c in node.Children ?? new List<FilterNode>())
                {
                    exact = Walk(c, values, col, exact);
                }
                return exact;
            case "compare" when string.Equals(node.Col, col, StringComparison.Ordinal):
            {
                if (node.Val is not int idx || !TryToInt64(values, idx, out long v))
                {
                    return exact;
                }
                switch (node.Cmp)
                {
                    case "=":
                        exact = IntersectExact(exact, new[] { v });
                        break;
                    case ">=":
                        _lo = Math.Max(_lo, v);
                        break;
                    case ">":
                        _lo = v == long.MaxValue ? long.MaxValue : Math.Max(_lo, v + 1);
                        _hi = v == long.MaxValue ? long.MinValue : _hi; // > MAX ⇒ empty
                        break;
                    case "<=":
                        _hi = Math.Min(_hi, v);
                        break;
                    case "<":
                        _hi = v == long.MinValue ? long.MinValue : Math.Min(_hi, v - 1);
                        _lo = v == long.MinValue ? long.MaxValue : _lo; // < MIN ⇒ empty
                        break;
                    // "<>" / is_distinct: ignored (superset)
                }
                return exact;
            }
            case "in" when string.Equals(node.Col, col, StringComparison.Ordinal):
            {
                if (node.Vals is not { Count: > 0 } idxs)
                {
                    return exact;
                }
                var vs = new List<long>(idxs.Count);
                foreach (var i in idxs)
                {
                    if (!TryToInt64(values, i, out long v))
                    {
                        return exact; // any unresolvable member ⇒ don't constrain from this IN
                    }
                    vs.Add(v);
                }
                return IntersectExact(exact, vs);
            }
            default:
                return exact; // or / other shapes: no constraint from here (superset)
        }
    }

    private static HashSet<long>? IntersectExact(HashSet<long>? exact, IEnumerable<long> vs)
    {
        if (exact is null)
        {
            return new HashSet<long>(vs);
        }
        exact.IntersectWith(vs);
        return exact;
    }

    private static bool TryToInt64(IReadOnlyList<object?> values, int idx, out long v)
    {
        v = 0;
        if (idx < 0 || idx >= values.Count)
        {
            return false;
        }
        switch (values[idx])
        {
            case sbyte x: v = x; return true;
            case short x: v = x; return true;
            case int x: v = x; return true;
            case long x: v = x; return true;
            case byte x: v = x; return true;
            case ushort x: v = x; return true;
            case uint x: v = x; return true;
            case ulong x when x <= long.MaxValue: v = (long)x; return true;
            default: return false;
        }
    }

    /// <summary>May any row of the file at this ordinal satisfy the constraint?</summary>
    public bool OrdinalMayMatch(int ordinal)
    {
        if (_positionsByOrdinal is { } byOrd)
        {
            return byOrd.ContainsKey(ordinal);
        }
        if (_lo > _hi)
        {
            return false; // contradictory range ⇒ empty
        }
        long loOrd = _lo <= 0 ? 0 : _lo >> PositionBits;
        long hiOrd = _hi < 0 ? -1 : _hi >> PositionBits;
        return ordinal >= loOrd && ordinal <= hiOrd;
    }

    /// <summary>The per-file <c>file_row_number</c> predicate for this ordinal (row-group skipping), or
    /// null when the constraint puts no position bound on this file (a fully-covered middle file).</summary>
    public string? PositionCondition(int ordinal)
    {
        if (_positionsByOrdinal is { } byOrd)
        {
            if (!byOrd.TryGetValue(ordinal, out var positions) || positions.Count == 0)
            {
                return "file_row_number IN (-1)"; // unmatched ordinal (defensive; the file is pruned anyway)
            }
            return "file_row_number IN (" +
                   string.Join(",", positions.Select(p => p.ToString(CultureInfo.InvariantCulture))) + ")";
        }
        var conds = new List<string>(2);
        if (_lo > 0 && (_lo >> PositionBits) == ordinal)
        {
            conds.Add($"file_row_number >= {(_lo & PositionMask).ToString(CultureInfo.InvariantCulture)}");
        }
        if (_hi >= 0 && (_hi >> PositionBits) == ordinal)
        {
            conds.Add($"file_row_number <= {(_hi & PositionMask).ToString(CultureInfo.InvariantCulture)}");
        }
        return conds.Count == 0 ? null : string.Join(" AND ", conds);
    }
}
