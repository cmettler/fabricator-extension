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

    [JsonPropertyName("order_by")]
    public List<OrderKey>? OrderBy { get; set; }

    /// <summary>Time travel (DuckDB <c>AT (...)</c>): a base-table snapshot context. Only set for catalog
    /// table scans (the AT clause is a base-table feature). <see cref="AtSpec.Unit"/> is "timestamp" or
    /// "version".</summary>
    [JsonPropertyName("at")]
    public AtSpec? At { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static ScanSpec? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ScanSpec>(json, Options);
}

/// <summary>One ORDER BY key: a column name + direction.</summary>
internal sealed class OrderKey
{
    [JsonPropertyName("col")]
    public string Col { get; set; } = "";

    [JsonPropertyName("desc")]
    public bool Desc { get; set; }
}

/// <summary>A DuckDB <c>AT (...)</c> time-travel clause: a unit ("timestamp" / "version") + the constant
/// value (rendered as a string). The SQL Server provider maps "timestamp" to <c>FOR SYSTEM_TIME AS OF</c>;
/// "version" has no equivalent and is rejected.</summary>
internal sealed class AtSpec
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
