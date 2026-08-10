using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fabricator.Bridge;

/// <summary>
/// The projection + filter (+ time-travel) pushdown specification the C++ host sends with a table or
/// table-function scan, as a small JSON document:
/// <c>{ "columns": ["a","b"], "filter": &lt;predicate-tree&gt;, "top": n, "order_by": [...], "at": {...} }</c>.
///
/// <para>This is the provider-agnostic mirror of the host's <c>spec_json</c> contract (defined by the C++
/// fabricator core), so it lives in the Bridge: every backend (SQL Server, future DAX) and any custom C#
/// table function (which receives the raw <see cref="TableFunctionScan.SpecJson"/>) can parse it. Rendering
/// a parsed spec into provider SQL (e.g. a T-SQL WHERE) is provider-specific and stays in the backend.</para>
///
/// <para><c>columns</c> is the projection (absent/empty =&gt; <c>SELECT *</c>). <c>filter</c> is an optional
/// predicate tree (see <see cref="FilterNode"/>) whose constants are referenced by index into the separate
/// Arrow value batch, so the final predicate can be built with parameters rather than inlined literals.</para>
/// </summary>
public sealed class ScanSpec
{
    [JsonPropertyName("columns")]
    public List<string>? Columns { get; set; }

    [JsonPropertyName("filter")]
    public FilterNode? Filter { get; set; }

    /// <summary>A 1:1 SQL rendering of the same predicates as <see cref="Filter"/>, with literals inlined —
    /// emitted by the host only for a scan whose target is DuckDB itself (the native Delta <c>read_parquet</c>
    /// path). A provider that renders its own dialect (SQL Server, DAX) ignores this and uses
    /// <see cref="Filter"/> + the value batch. Null/absent =&gt; no filter (or the host didn't render one).</summary>
    [JsonPropertyName("native_filter")]
    public string? NativeFilter { get; set; }

    [JsonPropertyName("top")]
    public long? Top { get; set; }

    [JsonPropertyName("order_by")]
    public List<OrderKey>? OrderBy { get; set; }

    /// <summary>Time travel (DuckDB <c>AT (...)</c>): a base-table snapshot context. Only set for catalog
    /// table scans (the AT clause is a base-table feature). <see cref="AtSpec.Unit"/> is "timestamp" or
    /// "version".</summary>
    [JsonPropertyName("at")]
    public AtSpec? At { get; set; }

    /// <summary>
    /// Present when this scan reads the SAME catalog a sink in the same plan writes to, and it NAMES that
    /// sink (<c>FabricatorCatalog::MarkSinkOnOwnScans</c>). Absent on an ordinary scan.
    ///
    /// <para><b>The host states a fact, not a verdict.</b> Whether the scan must be produced in full before
    /// the stream is returned depends on how the backend will WRITE, which only the backend knows — so the
    /// decision belongs here, on the provider side. A provider that holds no connection (Delta) ignores it
    /// entirely. A provider that pins one connection per transaction cannot hold an open reader while a bulk
    /// load runs on it — on SQL Server that is error 595, and it is size-dependent, so a small result never
    /// trips it.</para>
    ///
    /// <para>The second effect matters as much as the first: with the reader fully drained there is no
    /// outstanding result set, so the scan may run on the PINNED connection even where MARS is
    /// unavailable — which is what gives read-your-writes back on Fabric/Synapse.</para>
    ///
    /// <para>⚠ This replaced a bare <c>"materialize": true</c> on 2026-08-10, because that spelling put a
    /// provider decision in the host and got it wrong in a measurable way: an
    /// <c>INSERT INTO &lt;external table&gt; SELECT … FROM &lt;same catalog&gt;</c> drained 200 000 rows into
    /// memory to protect against a <c>SqlBulkCopy</c> that never runs, since a SQL Server external table's
    /// INSERT routes to STORAGE. <see cref="HasSink"/> reproduces the old boolean exactly for any consumer
    /// that only wants it.</para>
    /// </summary>
    [JsonPropertyName("sink")]
    public SinkInfo? Sink { get; set; }

    /// <summary>Exactly the old <c>materialize</c> boolean: "a sink of this catalog is in this plan".</summary>
    [JsonIgnore]
    public bool HasSink => Sink is not null && !string.IsNullOrEmpty(Sink.Table);

    /// <summary>
    /// The sink a marked scan shares its plan with. SINGULAR by construction — DuckDB gives a plan one sink,
    /// and MERGE INTO's several operators all address one target. The read set needs no transport: each scan
    /// already knows the table it reads.
    /// </summary>
    public sealed class SinkInfo
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = "";

        [JsonPropertyName("table")]
        public string Table { get; set; } = "";

        /// <summary><c>insert</c> or <c>create</c> (CTAS / CREATE OR REPLACE). The kind is load-bearing for at
        /// least one provider decision: a SQL Server external table accepts a routed INSERT, but a CTAS over
        /// one is not a storage-write shape.</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";
    }

    /// <summary>
    /// DESCRIBE ONLY: the caller wants this scan's Arrow schema and no rows (the bind-time probe behind
    /// <c>PopulateReturnSchema</c>). A backend that can answer more cheaply should — SQL Server appends
    /// <c>WHERE 1 = 0</c> so the server produces no rows instead of starting a full table read that the
    /// bind then cancels.
    /// </summary>
    /// <remarks>
    /// ⚠ Ignoring it is always CORRECT — the schema of a scan that returns no rows is the schema of the
    /// same scan returning rows — so a provider need not implement it. That is deliberate: the request rides
    /// the ordinary scan path precisely so provider routing (native vs codec), credential resolution and
    /// snapshot-pin seeding stay identical to a real scan. Answering it from a separate metadata route was
    /// measured to break exactly that on Delta (see FabricatorTableEntry's schema_factory).
    /// </remarks>
    [JsonPropertyName("schema_only")]
    public bool SchemaOnly { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static ScanSpec? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ScanSpec>(json, Options);
}

/// <summary>One ORDER BY key: a column name + direction.</summary>
public sealed class OrderKey
{
    [JsonPropertyName("col")]
    public string Col { get; set; } = "";

    [JsonPropertyName("desc")]
    public bool Desc { get; set; }
}

/// <summary>A DuckDB <c>AT (...)</c> time-travel clause: a unit ("timestamp" / "version") + the constant
/// value (rendered as a string). A backend maps it to its own time-travel facility (SQL Server maps
/// "timestamp" to <c>FOR SYSTEM_TIME AS OF</c>; "version" has no equivalent and is rejected).</summary>
public sealed class AtSpec
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
