using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArrowNet.SqlServer;

/// <summary>
/// Pushdown specification the C++ host sends with a table scan, as a small JSON
/// document: <c>{ "columns": ["a","b"], "filter": &lt;predicate-tree&gt; }</c>.
///
/// <para><c>columns</c> is the projection (absent/empty =&gt; <c>SELECT *</c>).</para>
/// <para><c>filter</c> is an optional predicate tree (see <see cref="FilterNode"/>);
/// its constants are referenced by index into the separate Arrow value batch, so
/// the final WHERE can be built with parameters rather than inlined literals.</para>
/// </summary>
internal sealed class ScanSpec
{
    [JsonPropertyName("columns")]
    public List<string>? Columns { get; set; }

    [JsonPropertyName("filter")]
    public FilterNode? Filter { get; set; }

    [JsonPropertyName("top")]
    public long? Top { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static ScanSpec? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ScanSpec>(json, Options);
}
