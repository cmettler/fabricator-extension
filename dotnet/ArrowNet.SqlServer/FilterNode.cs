using System.Text.Json.Serialization;

namespace ArrowNet.SqlServer;

/// <summary>
/// A node in the pushed-down predicate tree (the <c>"filter"</c> of a
/// <see cref="ScanSpec"/>). The C++ host only emits predicates that are
/// <em>superset-safe</em> (a row that truly matches always passes the emitted
/// SQL), because pushdown is best-effort: DuckDB re-applies every predicate
/// above the scan, so an over-approximation is correct and an under-approximation
/// is not. The C# side just renders whatever tree it is given.
///
/// <para>Constants are referenced by <c>val</c>/<c>vals</c> = a column index into
/// the separate Arrow value batch (column i, row 0). They become SQL parameters,
/// so no literal escaping/collation pitfalls.</para>
///
/// Discriminated by <c>op</c>:
/// <list type="bullet">
/// <item><c>and</c>/<c>or</c> — <c>children</c></item>
/// <item><c>compare</c> — <c>cmp</c> (= &lt;&gt; &lt; &lt;= &gt; &gt;= is_distinct is_not_distinct), <c>col</c>, <c>val</c></item>
/// <item><c>is_null</c>/<c>is_not_null</c> — <c>col</c></item>
/// <item><c>in</c> — <c>col</c>, <c>vals</c></item>
/// </list>
/// </summary>
internal sealed class FilterNode
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("children")]
    public List<FilterNode>? Children { get; set; }

    [JsonPropertyName("cmp")]
    public string? Cmp { get; set; }

    [JsonPropertyName("col")]
    public string? Col { get; set; }

    [JsonPropertyName("val")]
    public int? Val { get; set; }

    [JsonPropertyName("vals")]
    public List<int>? Vals { get; set; }
}
