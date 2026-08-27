// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Fabricator.Bridge;

/// <summary>
/// The native reader's STABLE-ID fast path: file + row-group skipping for filters on the row-tracking
/// virtual columns <c>__delta_row_id</c> / <c>__delta_row_commit_version</c> (point lookups, dedup
/// DELETEs, "changed since version X" incremental extracts). Unlike the transient rowid (a locator that
/// decodes positionally), the stable id is IDENTITY — so the strategy is per-file, split by how the file
/// serves the column:
/// <list type="bullet">
///   <item><b>Derived file</b> (no materialized physical column — every plain append): the id is exactly
///     <c>baseRowId + file_row_number</c>, so the log alone bounds the file to
///     <c>[baseRowId, baseRowId + numRecords)</c> — file SKIPPED when the constraint can't intersect,
///     else the constraint translates to a <c>file_row_number</c> predicate (exact per-row-group min/max
///     synthesized by the parquet reader ⇒ ROW-GROUP skipping). The commit version is a per-file
///     CONSTANT (<c>defaultRowCommitVersion</c>) — the file is skipped outright when it fails the
///     predicate.</item>
///   <item><b>Materialized file</b> (rewrites: merge-on-read post-images, compaction, CoW — carries the
///     ORIGINAL ids, decoupled from its fresh baseRowId): the constraint is pushed onto the PHYSICAL
///     column inside the per-file query as the superset conjunct <c>(pred(col) OR col IS NULL)</c> —
///     single-column, so parquet zone maps prune row groups (NULL rows — pre-tracking sources whose id
///     derives — are never wrongly excluded; files with no NULLs, the normal case, prune fully).</item>
/// </list>
/// Superset-safe by construction: every emitted condition is implied by the exact predicate, which the
/// rendered SQL WHERE still applies over the COALESCE alias (and DuckDB re-applies above the scan in
/// non-exact mode). Extraction is conjunct-level like <see cref="DeltaRowIdFilter"/> — OR shapes and
/// unsupported ops contribute nothing.
/// </summary>
internal sealed class DeltaRowTrackingFilter
{
    /// <summary>One column's accumulated conjunctive constraint: an exact value set (=/IN, intersected)
    /// and/or inclusive range bounds.</summary>
    private sealed class Constraint
    {
        public HashSet<long>? Exact;      // null = no equality/IN conjunct
        public long Lo = long.MinValue;   // inclusive
        public long Hi = long.MaxValue;   // inclusive

        public bool Any => Exact is not null || Lo != long.MinValue || Hi != long.MaxValue;

        /// <summary>The effective candidate set clipped to the range; null = range-only.</summary>
        public IReadOnlyList<long>? Values()
            => Exact?.Where(v => v >= Lo && v <= Hi).OrderBy(v => v).ToList();

        public bool Contradictory => Lo > Hi || (Exact is not null && Values()!.Count == 0);

        /// <summary>May any value in [lo, hi] (hi null = unbounded) satisfy this constraint?</summary>
        public bool Intersects(long lo, long? hi)
        {
            if (Contradictory)
            {
                return false;
            }
            if (Exact is not null)
            {
                return Values()!.Any(v => v >= lo && (hi is null || v < hi));
            }
            return (hi is null || Lo < hi) && Hi >= lo;
        }

        /// <summary>May the single value satisfy this constraint?</summary>
        public bool Matches(long v)
            => !Contradictory && v >= Lo && v <= Hi && (Exact is null || Exact.Contains(v));

        /// <summary>Renders the constraint as SQL over <paramref name="expr"/> (values inlined — BIGINTs,
        /// injection-free). Null when the constraint is empty.</summary>
        public string? Render(string expr)
        {
            if (Exact is not null)
            {
                var vs = Values()!;
                return vs.Count == 0
                    ? $"{expr} IN (NULL)" // contradictory (defensive; the file is skipped upstream)
                    : $"{expr} IN ({string.Join(",", vs.Select(I))})";
            }
            var conds = new List<string>(2);
            if (Lo != long.MinValue)
            {
                conds.Add($"{expr} >= {I(Lo)}");
            }
            if (Hi != long.MaxValue)
            {
                conds.Add($"{expr} <= {I(Hi)}");
            }
            return conds.Count == 0 ? null : string.Join(" AND ", conds);
        }

        private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
    }

    private readonly Constraint? _id;
    private readonly Constraint? _version;
    private readonly string _idColumn;
    private readonly string _versionColumn;

    private DeltaRowTrackingFilter(Constraint? id, Constraint? version, string idColumn, string versionColumn)
    {
        _id = id;
        _version = version;
        _idColumn = idColumn;
        _versionColumn = versionColumn;
    }

    /// <summary>Extracts the row-tracking constraints from a pushed filter tree, or null when the tree
    /// carries no AND-reachable conjunct on either column.</summary>
    public static DeltaRowTrackingFilter? Extract(
        FilterNode? node, IReadOnlyList<object?> values, string idColumn, string versionColumn)
    {
        if (node is null)
        {
            return null;
        }
        var id = new Constraint();
        var version = new Constraint();
        Walk(node, values, idColumn, id, versionColumn, version);
        return id.Any || version.Any
            ? new DeltaRowTrackingFilter(id.Any ? id : null, version.Any ? version : null, idColumn, versionColumn)
            : null;
    }

    /// <summary>Removes AND-reachable conjuncts on either row-tracking column (for the engineered-wood
    /// prune tree — the Delta log has no stats for them; dropping a conjunct only widens).</summary>
    public static FilterNode? Strip(FilterNode? node, string idColumn, string versionColumn)
        => DeltaRowIdFilter.Strip(DeltaRowIdFilter.Strip(node, idColumn), versionColumn);

    private static void Walk(FilterNode node, IReadOnlyList<object?> values,
                             string idCol, Constraint id, string verCol, Constraint version)
    {
        switch (node.Op)
        {
            case "and":
                foreach (var c in node.Children ?? new List<FilterNode>())
                {
                    Walk(c, values, idCol, id, verCol, version);
                }
                return;
            case "compare":
            case "in":
            {
                var target = string.Equals(node.Col, idCol, StringComparison.Ordinal) ? id
                    : string.Equals(node.Col, verCol, StringComparison.Ordinal) ? version
                    : null;
                if (target is null)
                {
                    return;
                }
                Apply(node, values, target);
                return;
            }
            case "or":
                // A SINGLE-COLUMN OR of equalities/INs is a value-set conjunct — the live serializer
                // renders erased/dynamic IN filters as OR-of-equals. Mixed-column/other ORs contribute
                // nothing (superset).
                TryApplyValueOr(node, values, idCol, id);
                TryApplyValueOr(node, values, verCol, version);
                return;
            default:
                return; // other shapes: no constraint from here (superset)
        }
    }

    // An OR whose every child is `col = v` / `col IN (…)` on ONE column ⇒ the union of the values is a
    // conjunct-level value set for that column. Anything else in the OR ⇒ no constraint (superset).
    private static void TryApplyValueOr(FilterNode node, IReadOnlyList<object?> values,
                                        string col, Constraint c)
    {
        if (node.Children is not { Count: > 0 } children)
        {
            return;
        }
        var union = new HashSet<long>();
        foreach (var child in children)
        {
            if (!string.Equals(child.Col, col, StringComparison.Ordinal))
            {
                return;
            }
            if (string.Equals(child.Op, "compare", StringComparison.Ordinal)
                && string.Equals(child.Cmp, "=", StringComparison.Ordinal)
                && child.Val is int idx && TryToInt64(values, idx, out long v))
            {
                union.Add(v);
            }
            else if (string.Equals(child.Op, "in", StringComparison.Ordinal) && child.Vals is { Count: > 0 } idxs)
            {
                foreach (var i in idxs)
                {
                    if (!TryToInt64(values, i, out long iv))
                    {
                        return;
                    }
                    union.Add(iv);
                }
            }
            else
            {
                return;
            }
        }
        if (c.Exact is null)
        {
            c.Exact = union;
        }
        else
        {
            c.Exact.IntersectWith(union);
        }
    }

    private static void Apply(FilterNode node, IReadOnlyList<object?> values, Constraint c)
    {
        if (string.Equals(node.Op, "in", StringComparison.Ordinal))
        {
            if (node.Vals is not { Count: > 0 } idxs)
            {
                return;
            }
            var vs = new List<long>(idxs.Count);
            foreach (var i in idxs)
            {
                if (!TryToInt64(values, i, out long v))
                {
                    return; // any unresolvable member ⇒ don't constrain from this IN
                }
                vs.Add(v);
            }
            if (c.Exact is null)
            {
                c.Exact = new HashSet<long>(vs);
            }
            else
            {
                c.Exact.IntersectWith(vs);
            }
            return;
        }
        if (node.Val is not int idx || !TryToInt64(values, idx, out long val))
        {
            return;
        }
        switch (node.Cmp)
        {
            case "=":
                if (c.Exact is null)
                {
                    c.Exact = new HashSet<long> { val };
                }
                else
                {
                    c.Exact.IntersectWith(new[] { val });
                }
                break;
            case ">=":
                c.Lo = Math.Max(c.Lo, val);
                break;
            case ">":
                c.Lo = val == long.MaxValue ? long.MaxValue : Math.Max(c.Lo, val + 1);
                c.Hi = val == long.MaxValue ? long.MinValue : c.Hi; // > MAX ⇒ empty
                break;
            case "<=":
                c.Hi = Math.Min(c.Hi, val);
                break;
            case "<":
                c.Hi = val == long.MinValue ? long.MinValue : Math.Min(c.Hi, val - 1);
                c.Lo = val == long.MinValue ? long.MaxValue : c.Lo; // < MIN ⇒ empty
                break;
            // "<>": ignored (superset)
        }
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

    /// <summary>Per-file verdict, decided AFTER the footer probe (materialized presence is per file).
    /// <paramref name="skip"/> = the file cannot contain a matching row (no data query at all);
    /// otherwise <paramref name="innerCondition"/> is an optional superset conjunct for the per-file
    /// query's INNER WHERE (references raw file columns / file_row_number ⇒ parquet zone maps prune).</summary>
    public void FileVerdict(DeltaReader.NativeScanFile f, bool idMaterialized, bool versionMaterialized,
                            out bool skip, out string? innerCondition)
    {
        skip = false;
        innerCondition = null;
        var conds = new List<string>(2);

        if (_id is { } id)
        {
            if (idMaterialized)
            {
                // Original ids, decoupled from this file's fresh baseRowId — push the constraint onto the
                // physical column; OR IS NULL keeps derived-fallback rows (pre-tracking sources) visible.
                // Single-column ⇒ zone-map prunable; files with nullCount 0 prune fully.
                if (id.Render(Quote(_idColumn)) is { } cond)
                {
                    conds.Add($"({cond} OR {Quote(_idColumn)} IS NULL)");
                }
            }
            else if (f.BaseRowId is { } b)
            {
                // Derived: id == baseRowId + file_row_number, bounded by [b, b + numRecords).
                if (!id.Intersects(b, f.NumRecords is { } n ? b + n : null))
                {
                    skip = true;
                    return;
                }
                if (Shift(id, -b) is { } shifted && shifted.Render("file_row_number") is { } cond)
                {
                    conds.Add(cond);
                }
            }
            else if (id.Any)
            {
                // Pending (uncommitted) file with no materialized ids: the column reads NULL — a value
                // constraint (=/IN/range) can never match NULL.
                skip = true;
                return;
            }
        }

        if (_version is { } ver)
        {
            if (versionMaterialized)
            {
                if (ver.Render(Quote(_versionColumn)) is { } cond)
                {
                    conds.Add($"({cond} OR {Quote(_versionColumn)} IS NULL)");
                }
            }
            else if (f.CommitVersion is { } v)
            {
                // Derived: a per-file CONSTANT — the whole file matches or none of it does.
                if (!ver.Matches(v))
                {
                    skip = true;
                    return;
                }
            }
            else if (ver.Any)
            {
                skip = true; // pending file: version reads NULL
                return;
            }
        }

        innerCondition = conds.Count switch
        {
            0 => null,
            1 => conds[0],
            _ => string.Join(" AND ", conds),
        };
    }

    // The constraint over (id - base) — i.e. translated onto file_row_number. Null when the shift
    // over/underflows (then no position condition; the range check above already admitted the file).
    private static Constraint? Shift(Constraint c, long offset)
    {
        var s = new Constraint();
        if (c.Exact is not null)
        {
            s.Exact = new HashSet<long>();
            foreach (var v in c.Values()!)
            {
                long p = v + offset;
                if (p >= 0)
                {
                    s.Exact.Add(p);
                }
            }
        }
        if (c.Lo != long.MinValue)
        {
            s.Lo = Math.Max(0, c.Lo + offset);
        }
        if (c.Hi != long.MaxValue)
        {
            long h = c.Hi + offset;
            if (h < 0)
            {
                return null; // entirely below this file (defensive; Intersects filtered already)
            }
            s.Hi = h;
        }
        return s;
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
