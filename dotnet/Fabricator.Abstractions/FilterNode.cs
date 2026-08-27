// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Fabricator.Bridge;

/// <summary>
/// A node in the pushed-down predicate tree (the <c>"filter"</c> of a <see cref="ScanSpec"/>). The C++ host
/// only emits predicates that are <em>superset-safe</em> (a row that truly matches always passes the emitted
/// predicate), because pushdown is best-effort: DuckDB re-applies every predicate above the scan, so an
/// over-approximation is correct and an under-approximation is not. A backend just renders whatever tree it
/// is given (rendering is provider-specific — see the SQL Server FilterWhereBuilder).
///
/// <para>Provider-agnostic (part of the host's <c>spec_json</c> contract), so it lives in the Bridge.
/// Constants are referenced by <c>val</c>/<c>vals</c> = a column index into the separate Arrow value batch
/// (column i, row 0); a renderer turns them into parameters, so no literal escaping/collation pitfalls.</para>
///
/// Discriminated by <c>op</c>:
/// <list type="bullet">
/// <item><c>and</c>/<c>or</c> — <c>children</c></item>
/// <item><c>compare</c> — <c>cmp</c> (= &lt;&gt; &lt; &lt;= &gt; &gt;= is_distinct is_not_distinct), <c>col</c>, <c>val</c></item>
/// <item><c>is_null</c>/<c>is_not_null</c> — <c>col</c></item>
/// <item><c>in</c> — <c>col</c>, <c>vals</c></item>
/// </list>
///
/// <para>A STRUCT-member predicate (<c>WHERE (s).a = 5</c>) carries <c>path</c> — the full member path
/// including the top-level column (<c>["s","a"]</c>) — and <c>col</c> is null. A renderer that doesn't
/// understand paths throws on the missing <c>col</c> and its caller falls back to pushing nothing
/// (superset-safe); only providers with nested columns (Delta) resolve it, for stats-based pruning.</para>
/// </summary>
public sealed class FilterNode
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("children")]
    public List<FilterNode>? Children { get; set; }

    [JsonPropertyName("cmp")]
    public string? Cmp { get; set; }

    [JsonPropertyName("col")]
    public string? Col { get; set; }

    [JsonPropertyName("path")]
    public List<string>? Path { get; set; }

    [JsonPropertyName("val")]
    public int? Val { get; set; }

    [JsonPropertyName("vals")]
    public List<int>? Vals { get; set; }
}
