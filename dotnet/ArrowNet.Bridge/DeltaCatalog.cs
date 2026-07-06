using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace ArrowNet.Bridge;

/// <summary>
/// The Delta Lake provider backed by <b>engineered-wood</b> (the 3rd <see cref="IBackend"/>, after SQL Server
/// and DAX): a Delta <b>folder</b> is an ATTACH-able catalog root —
/// <c>ATTACH '/lake' AS lake (TYPE arrownet, PROVIDER 'engineeredwooddelta')</c> (or an <c>abfss://…</c>
/// OneLake/ADLS prefix). The provider name is <c>engineeredwooddelta</c> to distinguish it from a future
/// delta-rs/delta-kernel-backed provider; <c>delta</c> and <c>deltalake</c> remain aliases. Tables = subdirs
/// with a <c>_delta_log/</c> (flat <c>main</c> schema, or per-lakehouse schemas on a schema-enabled OneLake
/// lakehouse). Connection-free: all IO goes through DuckDB's FileSystem via the host callbacks (local / az:// /
/// s3:// + DuckDB secrets), reusing <see cref="DeltaReader"/>. Full read + write DML — CREATE/INSERT/CTAS/COPY/
/// DROP, DELETE (copy-on-write or opt-in deletion vectors), UPDATE (copy-on-write), OCC retry for concurrent
/// writers — all reuse the provider-agnostic C++ catalog machinery. See docs/delta-catalog.md.
/// </summary>
public sealed class DeltaBackend : IBackend
{
    public string Name => "engineeredwooddelta";

    // `delta`/`deltalake` stay as aliases so existing ATTACHes keep working; the primary name distinguishes
    // this engineered-wood-backed provider from a future delta-rs production provider.
    public IEnumerable<string> Aliases => new[] { "delta", "deltalake" };

    // Session-level write tuning applied just before each CREATE/INSERT/CTAS/COPY, modeled as a JSON object so a
    // single setting carries several knobs (and is easy to extend). Keys: "compression" (snappy|zstd|gzip|brotli|
    // lz4|uncompressed|…), "row_group_size" (int rows), "bloom_filter_columns" (string[] dotted paths),
    // "partition_by" (string[] — applied at CREATE/CTAS; a native PARTITIONED BY clause overrides it),
    // "replace_where" ({partcol:val,…} — an INSERT becomes an ATOMIC partition-overwrite of the matching
    // partition(s)), "schema_mode" ("merge"|"overwrite" — append+union / replace+adopt-schema; also the COPY
    // SCHEMA_MODE option) / "merge_schema" (bool, legacy alias for schema_mode=merge on append).
    // Overlays the per-catalog ATTACH defaults (compression / row_group_size / bloom_filter_columns / merge_schema).
    // Read by DeltaCatalog from ProviderSettingsStore at write time — see docs/delta-catalog.md "Write tuning".
    public const string WriteOptionsSetting = "delta_write_options";

    public IEnumerable<ProviderSetting> Settings => new[]
    {
        new ProviderSetting(WriteOptionsSetting, ProviderSettingType.Varchar, Default: null,
            Description: "Delta write options as JSON (compression, row_group_size, bloom_filter_columns, partition_by, replace_where, merge_schema), applied to CREATE/INSERT/CTAS."),
    };

    // The connstr IS the folder root. Data-file IO is via DuckDB FS secrets (the opener). An azure SP secret on
    // a OneLake ATTACH additionally authenticates the Fabric REST API used to list tables (the glob bug
    // workaround) — carry its fields to the catalog as a credential marker on the root (mirrors the DAX provider).
    public string BuildConnectionString(
        string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString)
    {
        if (secretType.Equals("azure", System.StringComparison.OrdinalIgnoreCase)
            && FabricLakehouse.IsOneLake(baseConnString))
        {
            return FabricLakehouse.AppendCredMarker(baseConnString, fields);
        }
        return baseConnString;
    }

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        new DeltaCatalog(connectionString, optionsJson);
}

/// <summary>An ATTACH'd Delta folder catalog. Lazy: holds the root path; all FS access happens during metadata
/// discovery / scan, using the active host-FS opener (<see cref="AmbientOpener"/>, set by the host before each
/// catalog metadata + scan + bulk-write call).</summary>
public sealed class DeltaCatalog : IBackendCatalog
{
    internal const string MainSchema = "main";
    // The stable row-tracking id surfaced as the DuckDB rowid for UPDATE/DELETE (a VIRTUAL column — not part
    // of the user schema). Matches EngineeredWood.DeltaLake.RowTracking.RowTrackingConfig.VirtualRowIdColumn.
    private const string RowIdColumn = "_metadata.row_id";
    // Transient rowid packing — MUST match engineered-wood's DeltaTable.RowIdPositionBits: (fileOrdinal << 40) |
    // rowPositionInFile. Used to recompute a row's rowid during the per-file UPDATE rewrite.
    private const int RowIdPositionBits = 40;
    private readonly string _root; // normalized (forward slashes), no trailing slash
    // For a OneLake root: the Fabric REST API credential (from the ATTACH'd azure SP secret) used to list
    // tables (and, for a schema-enabled lakehouse, an Entra SQL token). Null for local/S3/ADLS (glob discovery)
    // or when no secret was supplied.
    private readonly Azure.Core.TokenCredential? _fabricCredential;
    // Lazily-resolved OneLake shape (schema-enabled flag + discovered tables); null for non-OneLake roots.
    private FabricLakehouse.OneLakeInfo? _oneLake;
    private bool _oneLakeResolved;
    // ATTACH option `deletion_vectors true`: tables CREATED in this catalog enable DV + row tracking (so their
    // DELETEs use deletion vectors). DELETE on ANY table still follows that table's own delta.enableDeletionVectors
    // config, so external DV tables are honored regardless of this flag.
    private readonly bool _deletionVectorsOnCreate;
    // ATTACH option `row_tracking true`: tables CREATED here enable the Delta delta.enableRowTracking WRITER
    // feature standalone (independent of deletion_vectors). Stable row ids + row_commit_version for external
    // consumers (Spark/Fabric). NOTE: our own DELETE/UPDATE still use the transient (file,position) rowid — the
    // physical locator the DV/copy-on-write delete needs — NOT the stable id (see docs/delta-catalog.md).
    private readonly bool _rowTrackingOnCreate;
    // ATTACH option `in_commit_timestamps true`: tables CREATED in this catalog enable the Delta
    // delta.enableInCommitTimestamps WRITER feature, so AT (TIMESTAMP => ts) time travel can resolve a timestamp
    // to a version (engineered-wood reads inCommitTimestamp, not commit-file mtime). VERSION travel works without it.
    private readonly bool _inCommitTimestampsOnCreate;
    // ATTACH option `change_data_feed true`: tables CREATED here enable delta.enableChangeDataFeed, so DELETE/UPDATE
    // write _change_data files and arrownet_delta_changes(...) returns a correct row-level change feed.
    private readonly bool _changeDataFeedOnCreate;
    // ATTACH option `schemas true`: a NON-OneLake root (local/S3/plain-ADLS) uses a two-level
    // <root>/<schema>/<table> layout so DuckDB schemas other than "main" map to subfolders (discovery, CREATE,
    // DROP all schema-aware). Default false = the flat <root>/<table>, "main"-only layout. Ignored for OneLake
    // (its layout is driven by the lakehouse's schema-enabled flag, not this option).
    private readonly bool _schemas;
    // Per-catalog write-tuning DEFAULTS from the ATTACH options (overlaid by the session delta_write_options
    // setting at write time). Null => engineered-wood's default. See ResolveWriteSpec.
    private readonly string? _defaultCompression;
    private readonly int? _defaultRowGroupSize;
    private readonly IReadOnlyList<string>? _defaultBloomColumns;
    // ATTACH option `merge_schema true`: an append whose incoming data has columns absent from the table
    // auto-evolves the schema (nullable AddColumn) before writing. Overridable per statement via the
    // delta_write_options setting's "merge_schema". (replace_where is per-statement only — the setting.)
    private readonly bool _mergeSchemaOnWrite;
    // ATTACH option `native_read true` (docs/multifile-delta.md slice 1e): a plain SELECT reads the table's data
    // files through DuckDB's NATIVE parquet reader (read_parquet over the exact active file set, run on the host
    // engine via Host.Query — tuned decode + cross-file parallelism + ExternalFileCache, over onelake:// for
    // OneLake) instead of engineered-wood's C# parquet reader. Opt-in (default off). Read-only: a scan that needs
    // the transient rowid (UPDATE/DELETE), a time-travel scan, or a table carrying deletion vectors transparently
    // falls back to the C# reader (the native path has no DeleteFilter / rowid / snapshot logic — those are
    // follow-ups). Purely a byte-source switch inside ScanTable — no C++/ABI change.
    private readonly bool _nativeRead;
    // ATTACH option `native_write true` (docs/native-delta-write.md): INSERT/CTAS/append data files are produced
    // by DuckDB's native parquet writer (COPY … TO … FORMAT parquet) instead of engineered-wood's codec; the
    // _delta_log commit stays in engineered-wood. Opt-in (default off); DELETE/UPDATE rewrites are a later slice.
    private readonly bool _nativeWrite;
    // ATTACH option `materialize_row_tracking true` (opt-in; default off): a row-tracking table declares the Delta
    // `delta.rowTracking.materializedRowIdColumnName` so a spec reader (Spark) exposes `_metadata.row_id`, AND a
    // copy-on-write/merge-on-read rewrite materializes each rewritten row's ORIGINAL stable id (instead of a fresh
    // base_row_id) so row ids are preserved across UPDATE (and, later, compaction). Validated via the Fabric Spark
    // Livy harness. Default OFF keeps the validated DV-default path untouched (no new feature declaration).
    private readonly bool _materializeRowTracking;

    // ATTACH option `column_mapping 'name'|'id'` (default none): tables CREATED in this catalog enable Delta
    // column mapping — physical column names (col-<guid>) decoupled from logical names, so a later RENAME/DROP
    // COLUMN is a metadata-only commit. engineered-wood's CreateAsync assigns the physical names + bumps the
    // protocol (reader v2 / writer v5). Our read (physical→logical alias / EW RenameByFieldId) + write (EW codec
    // RenameToPhysical / SetParquetFieldIds) already handle mapped tables. Only affects table CREATION.
    private readonly EngineeredWood.DeltaLake.Schema.ColumnMappingMode _columnMappingMode;

    private static readonly Microsoft.Extensions.Logging.ILogger _log = ArrowNetLog.CreateLogger("ArrowNet.Delta");

    public DeltaCatalog(string root) : this(root, "{}") { }

    public DeltaCatalog(string root, string? optionsJson)
    {
        var (clean, credential) = FabricLakehouse.Extract(root);
        _root = Normalize(clean).TrimEnd('/');
        _fabricCredential = credential;
        // Deletion vectors are the DEFAULT DML mode (the modern Delta standard: DELETE marks rows in a DV bitmap
        // instead of rewriting the whole file — cheap, and it preserves row-tracking ids/versions for free).
        // Opt OUT with `deletion_vectors false` for the maximally-compatible plain copy-on-write table (minReader
        // 1, no reader-v3 bump) — e.g. a consumer that can't read reader-v3 DV tables. DV enables reader-v3 +
        // the deletionVectors + rowTracking features; validated live on Fabric (SQL-endpoint-queryable).
        _deletionVectorsOnCreate = ParseBoolOption(optionsJson, "deletion_vectors", defaultValue: true);
        _rowTrackingOnCreate = ParseBoolOption(optionsJson, "row_tracking");
        _inCommitTimestampsOnCreate = ParseBoolOption(optionsJson, "in_commit_timestamps");
        _changeDataFeedOnCreate = ParseBoolOption(optionsJson, "change_data_feed");
        _schemas = ParseBoolOption(optionsJson, "schemas");
        _defaultCompression = ParseStringOption(optionsJson, "compression");
        _defaultRowGroupSize = ParseIntOption(optionsJson, "row_group_size");
        _defaultBloomColumns = ParseListOption(optionsJson, "bloom_filter_columns");
        _mergeSchemaOnWrite = ParseBoolOption(optionsJson, "merge_schema");
        _nativeRead = ParseBoolOption(optionsJson, "native_read");
        _nativeWrite = ParseBoolOption(optionsJson, "native_write");
        _materializeRowTracking = ParseBoolOption(optionsJson, "materialize_row_tracking");
        _columnMappingMode = ParseColumnMappingOption(optionsJson);
    }

    /// <summary>Returns the host-FS opener for this thread and, in the same breath, publishes this catalog's
    /// Fabric credential to <see cref="AmbientOneLakeCredential"/> so the filesystem factory
    /// (<see cref="TableFileSystems.Create"/>) picks the direct-SDK <see cref="OneLakeDataLakeFileSystem"/> for
    /// OneLake roots (bypassing duckdb-azure). For a non-OneLake catalog <c>_fabricCredential</c> is null → the
    /// factory falls back to <see cref="DuckDbTableFileSystem"/> (unchanged behavior). Setting it every time also
    /// clears any stale credential left on a reused execution thread by another catalog. The bulk write path runs
    /// on a background thread, so <c>BulkSession</c> re-establishes both ambients there.</summary>
    private nint Opener()
    {
        AmbientOneLakeCredential.Current = _fabricCredential;
        return AmbientOpener.Current;
    }

    /// <summary>True when this catalog uses the two-level <c>&lt;root&gt;/&lt;schema&gt;/&lt;table&gt;</c> layout:
    /// a schema-enabled OneLake lakehouse, OR a non-OneLake root with the <c>schemas true</c> ATTACH option.
    /// (<see cref="OneLake"/> is null for non-OneLake roots, so the two arms are mutually exclusive.)</summary>
    private bool SchemaLayout => OneLake()?.SchemaEnabled == true || (OneLake() is null && _schemas);

    private static bool ParseBoolOption(string? optionsJson, string key) =>
        ParseBoolOption(optionsJson, key, defaultValue: false);

    /// <summary>Parses a boolean ATTACH option. Returns <paramref name="defaultValue"/> when the key is ABSENT
    /// (so a default-on option like <c>deletion_vectors</c> can be opted OUT with an explicit <c>false</c>).</summary>
    private static bool ParseBoolOption(string? optionsJson, string key, bool defaultValue)
    {
        if (string.IsNullOrEmpty(optionsJson))
        {
            return defaultValue;
        }
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var el))
            {
                var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                return string.Equals(s, "true", System.StringComparison.OrdinalIgnoreCase) || s == "1";
            }
        }
        catch (JsonException)
        {
        }
        return defaultValue;
    }

    /// <summary>Parses the <c>column_mapping</c> ATTACH option for tables CREATED in this catalog (<c>'id'</c> /
    /// <c>'name'</c> / <c>'none'</c>, case-insensitive; default None). <c>'id'</c> maps columns by the Delta
    /// <c>delta.columnMapping.id</c> (== the parquet <c>field_id</c>) — the standard identity, stable across a
    /// RENAME, and what the native reader resolves via <c>parquet_schema</c> (see
    /// <see cref="DeltaNativeReader"/>). <c>'name'</c> maps by <c>physicalName</c> (col-&lt;guid&gt;). READING an
    /// external (Spark/Databricks) id- or name-mode table works regardless of this option (the mode is a table
    /// property). Throws on any other value.</summary>
    private static EngineeredWood.DeltaLake.Schema.ColumnMappingMode ParseColumnMappingOption(string? optionsJson)
    {
        var s = ParseStringOption(optionsJson, "column_mapping");
        // Default (option absent) = ID mode: the standard column-mapping identity, so a later RENAME COLUMN is a
        // metadata-only commit (no rewrite) out of the box. Opt out with `column_mapping 'none'` for a plain table
        // (logical == physical name, no protocol bump — e.g. a consumer that can't read a writer-v7 table).
        if (string.IsNullOrWhiteSpace(s)) { return EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Id; }
        return s.Trim().ToLowerInvariant() switch
        {
            "none" or "" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None,
            "name" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name,
            "id" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Id,
            _ => throw new System.ArgumentException(
                $"column_mapping: unknown mode '{s}' (expected 'id', 'name', or 'none')."),
        };
    }

    /// <summary>Reads a string ATTACH option (null if absent/blank/unparseable).</summary>
    private static string? ParseStringOption(string? optionsJson, string key)
    {
        if (string.IsNullOrEmpty(optionsJson)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var el))
            {
                var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Reads an int ATTACH option (accepts a JSON number or numeric string; null if absent/unparseable).</summary>
    private static int? ParseIntOption(string? optionsJson, string key)
    {
        var s = ParseStringOption(optionsJson, key);
        return int.TryParse(s, out var v) ? v : (int?)null;
    }

    /// <summary>Reads a list ATTACH option — a JSON array OR a comma-separated string (null if absent/empty).</summary>
    private static IReadOnlyList<string>? ParseListOption(string? optionsJson, string key)
    {
        if (string.IsNullOrEmpty(optionsJson)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var el))
            {
                return ReadStringList(el);
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>A JSON array of strings, or a single comma-separated string, → a trimmed non-empty list (null if none).</summary>
    private static IReadOnlyList<string>? ReadStringList(JsonElement el)
    {
        var list = new List<string>();
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(s)) { list.Add(s!.Trim()); }
            }
        }
        else
        {
            var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            foreach (var part in (raw ?? string.Empty).Split(','))
            {
                if (!string.IsNullOrWhiteSpace(part)) { list.Add(part.Trim()); }
            }
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Maps a compression name (case-insensitive) to engineered-wood's codec; null if unset/unknown
    /// (=> engineered-wood's Snappy default).</summary>
    private static EngineeredWood.Compression.CompressionCodec? ParseCompression(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        return name.Trim().ToLowerInvariant() switch
        {
            "snappy" => EngineeredWood.Compression.CompressionCodec.Snappy,
            "zstd" => EngineeredWood.Compression.CompressionCodec.Zstd,
            "gzip" => EngineeredWood.Compression.CompressionCodec.Gzip,
            "brotli" => EngineeredWood.Compression.CompressionCodec.Brotli,
            "lz4" => EngineeredWood.Compression.CompressionCodec.Lz4,
            "lz4_hadoop" or "lz4hadoop" => EngineeredWood.Compression.CompressionCodec.Lz4Hadoop,
            "deflate" => EngineeredWood.Compression.CompressionCodec.Deflate,
            "lzo" => EngineeredWood.Compression.CompressionCodec.Lzo,
            "none" or "uncompressed" => EngineeredWood.Compression.CompressionCodec.Uncompressed,
            _ => null,
        };
    }

    /// <summary>Resolves the effective write tuning for one write: the per-catalog ATTACH defaults overlaid with the
    /// session <c>delta_write_options</c> JSON setting (setting wins per key). Partition columns come from
    /// <paramref name="nativePartitionColumns"/> (a native <c>PARTITIONED BY</c> clause) when present, else the
    /// setting's <c>partition_by</c>. Returns null only when nothing is specified (=> engineered-wood defaults).</summary>
    private DeltaWriteSpec? ResolveWriteSpec(IReadOnlyList<string>? nativePartitionColumns, string? schemaModeArg)
    {
        var sessionJson = ProviderSettingsStore.Instance.GetString(
            DeltaBackendName, DeltaBackend.WriteOptionsSetting);

        string? compression = _defaultCompression;
        int? rowGroup = _defaultRowGroupSize;
        IReadOnlyList<string>? bloom = _defaultBloomColumns;
        IReadOnlyList<string>? settingPartition = null;
        IReadOnlyDictionary<string, string>? replaceWhere = null;
        // schema_mode precedence: per-catalog merge_schema default < delta_write_options (merge_schema / schema_mode)
        // < the per-statement COPY SCHEMA_MODE arg.
        var schemaMode = _mergeSchemaOnWrite ? DeltaSchemaMode.Merge : DeltaSchemaMode.None;

        if (!string.IsNullOrWhiteSpace(sessionJson))
        {
            compression = ParseStringOption(sessionJson, "compression") ?? compression;
            rowGroup = ParseIntOption(sessionJson, "row_group_size") ?? rowGroup;
            bloom = ParseListOption(sessionJson, "bloom_filter_columns") ?? bloom;
            settingPartition = ParseListOption(sessionJson, "partition_by");
            replaceWhere = ParseMapOption(sessionJson, "replace_where"); // partition col -> value (per-statement)
            if (ParseStringOption(sessionJson, "schema_mode") is { } sm) { schemaMode = ParseSchemaMode(sm); }
            else if (ParseBoolOption(sessionJson, "merge_schema")) { schemaMode = DeltaSchemaMode.Merge; }
        }
        if (!string.IsNullOrWhiteSpace(schemaModeArg)) { schemaMode = ParseSchemaMode(schemaModeArg); }

        var partition = nativePartitionColumns is { Count: > 0 } ? nativePartitionColumns : settingPartition;
        var codec = ParseCompression(compression);

        if (codec is null && rowGroup is null && bloom is null && (partition is null || partition.Count == 0)
            && (replaceWhere is null || replaceWhere.Count == 0) && schemaMode == DeltaSchemaMode.None)
        {
            return null;
        }
        return new DeltaWriteSpec(codec, rowGroup, bloom, partition, replaceWhere, schemaMode);
    }

    /// <summary>Maps a SCHEMA_MODE string (case-insensitive) to <see cref="DeltaSchemaMode"/>; unknown => None.</summary>
    private static DeltaSchemaMode ParseSchemaMode(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "merge" => DeltaSchemaMode.Merge,
        "overwrite" => DeltaSchemaMode.Overwrite,
        _ => DeltaSchemaMode.None,
    };

    /// <summary>Reads a JSON OBJECT option (e.g. <c>replace_where</c>) as a string→string map — a partition
    /// column→value equality set. A JSON string value is stringified. Null when absent/empty/not an object.</summary>
    private static IReadOnlyDictionary<string, string>? ParseMapOption(string? optionsJson, string key)
    {
        if (string.IsNullOrEmpty(optionsJson)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject())
                {
                    map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.ToString();
                }
                return map.Count > 0 ? map : null;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private const string DeltaBackendName = "engineeredwooddelta";

    private static string Normalize(string p) => p.Replace('\\', '/');

    /// <summary>Resolves (once) the OneLake lakehouse shape via the Fabric API + (schema-enabled) SQL endpoint.
    /// Null for non-OneLake roots. Network calls; cached for the catalog's lifetime (refreshed on re-ATTACH).</summary>
    private FabricLakehouse.OneLakeInfo? OneLake()
    {
        if (!_oneLakeResolved)
        {
            _oneLake = FabricLakehouse.IsOneLake(_root) ? FabricLakehouse.Resolve(_root, _fabricCredential) : null;
            _oneLakeResolved = true;
        }
        return _oneLake;
    }

    /// <summary>The Delta table folder for a (schema, table). A schema-enabled OneLake lakehouse stores tables at
    /// <c>&lt;root&gt;/&lt;schema&gt;/&lt;table&gt;</c>; everything else is flat <c>&lt;root&gt;/&lt;table&gt;</c>
    /// (the DuckDB schema is then the single "main", ignored).</summary>
    private string TablePath(string schema, string table) =>
        SchemaLayout ? _root + "/" + schema + "/" + table : _root + "/" + table;

    public IArrowArrayStream GetMetadata(int kind, string? schema, string? table) => kind switch
    {
        MetadataKind.Schemas => SingleColumn("schema_name", SchemaNames()),
        MetadataKind.Tables => DiscoverTables(),
        // Columns = a zero-row stream whose SCHEMA describes the table's columns (engineered-wood's Delta schema).
        MetadataKind.Columns => new InMemoryArrayStream(
            DeltaReader.GetSchema(Opener(), TablePath(schema!, table!)), System.Array.Empty<RecordBatch>()),
        // RowId: always surface the virtual _metadata.row_id — a TRANSIENT (file, position) rowid computed at
        // scan time (no row-tracking feature needed; works on ANY Delta table). Enables UPDATE/DELETE
        // (rowid-based, mirrors the SQL Server backend); DELETE is copy-on-write (plain add/remove).
        MetadataKind.RowId => SingleColumn("name", new[] { RowIdColumn }),
        // Snapshots/history (arrownet_delta_snapshots): arg1=schema, arg2=table. Schema is required on a
        // schema-enabled lakehouse; defaults to "main" on a flat catalog.
        MetadataKind.Snapshots => SnapshotsStream(schema, table),
        // Change Data Feed (arrownet_delta_changes): arg1 = 'schema.table' ref, arg2 = "from:to" (to empty => latest).
        MetadataKind.Changes => ChangesStream(schema, table),
        // Capability profile (property, value). `exact_filter_pushdown` = whether the host may set
        // filter_pushdown=true on this catalog's scan: TRUE only under native_read, where every scan routes
        // through read_parquet on the host DuckDB and applies the pushed WHERE EXACTLY (1:1, same engine).
        // With native_read off the engineered-wood reader only PRUNES (superset), so DuckDB must keep
        // re-applying — filter_pushdown stays false. See docs/multifile-delta.md §"Batch 2 slice 2".
        MetadataKind.ServerInfo => TwoColumn(
            "property", new[] { "exact_filter_pushdown" },
            "value", new[] { _nativeRead ? "true" : "false" }),
        // No row-count/NDV stats surfaced, no functions.
        _ => EmptyStringTable("name"),
    };

    /// <summary>The commit history of <paramref name="schema"/>.<paramref name="table"/> as an Arrow stream
    /// (version, timestamp, operation, operation_parameters). <paramref name="schema"/> is required on a
    /// schema-enabled lakehouse (the table path needs it); on a flat catalog an empty schema defaults to "main".</summary>
    private IArrowArrayStream SnapshotsStream(string? schema, string? table)
    {
        if (string.IsNullOrEmpty(table))
        {
            throw new System.ArgumentException("delta snapshots: a table name is required (catalog, 'schema.table').");
        }
        string resolvedSchema;
        if (!string.IsNullOrEmpty(schema))
        {
            resolvedSchema = schema!;
        }
        else if (SchemaLayout)
        {
            throw new System.InvalidOperationException(
                "delta snapshots: a schema is required on a schema-enabled lakehouse — use 'schema.table'.");
        }
        else
        {
            resolvedSchema = MainSchema;
        }
        return DeltaReader.GetSnapshots(Opener(), TablePath(resolvedSchema, table!));
    }

    /// <summary>Change Data Feed of a table. <paramref name="tableRef"/> = '&lt;schema.&gt;table' (schema required
    /// on a schema-enabled lakehouse, default "main" on a flat catalog); <paramref name="range"/> = "from:to"
    /// (empty "to" =&gt; latest). Returns the row-level change feed for [from, to].</summary>
    private IArrowArrayStream ChangesStream(string? tableRef, string? range)
    {
        if (string.IsNullOrEmpty(tableRef))
        {
            throw new System.ArgumentException("delta changes: a table is required (catalog, 'schema.table', from, to).");
        }
        // Split '<schema>.<table>' (first dot). A bare name => no schema (resolved below).
        string? schema = null;
        string table = tableRef!;
        int dot = tableRef!.IndexOf('.');
        if (dot >= 0)
        {
            schema = tableRef.Substring(0, dot);
            table = tableRef.Substring(dot + 1);
        }
        string resolvedSchema;
        if (!string.IsNullOrEmpty(schema))
        {
            resolvedSchema = schema!;
        }
        else if (SchemaLayout)
        {
            throw new System.InvalidOperationException(
                "delta changes: a schema is required on a schema-enabled lakehouse — use 'schema.table'.");
        }
        else
        {
            resolvedSchema = MainSchema;
        }

        // Parse "from:to" — to empty/absent => latest (-1).
        long from = 0, to = -1;
        if (!string.IsNullOrEmpty(range))
        {
            var parts = range!.Split(':');
            if (parts.Length > 0 && long.TryParse(parts[0], out var f)) { from = f; }
            if (parts.Length > 1 && long.TryParse(parts[1], out var t)) { to = t; }
        }
        return DeltaReader.GetChanges(Opener(), TablePath(resolvedSchema, table), from, to);
    }

    /// <summary>The catalog's schemas: the lakehouse schemas for a schema-enabled OneLake lakehouse; for a
    /// non-OneLake <c>schemas true</c> catalog the distinct subfolders discovered as schemas (+ always "main", the
    /// default); else the single flat "main".</summary>
    private IReadOnlyList<string> SchemaNames()
    {
        var ol = OneLake();
        if (ol?.SchemaEnabled == true)
        {
            var schemas = new SortedSet<string>(System.StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(ol.DefaultSchema))
            {
                schemas.Add(ol.DefaultSchema!); // always expose the default schema (so CREATE works when empty)
            }
            foreach (var (s, _) in ol.Tables)
            {
                schemas.Add(s);
            }
            if (schemas.Count == 0)
            {
                schemas.Add(MainSchema);
            }
            return new List<string>(schemas);
        }
        if (ol is null && _schemas)
        {
            // schemas-mode local/S3: schemas = the <root>/<schema>/ subfolders that contain a table, plus "main"
            // (the default, so the catalog always has a schema). An EMPTY created schema with no tables yet does
            // not survive a re-attach (it has no _delta_log to glob) — a documented limitation.
            var schemas = new SortedSet<string>(System.StringComparer.Ordinal) { MainSchema };
            foreach (var (s, _) in DiscoverTablePairs())
            {
                schemas.Add(s);
            }
            return new List<string>(schemas);
        }
        return new[] { MainSchema };
    }

    /// <summary>Discovers (schema, table) pairs. OneLake → the DFS-resolved list. Non-OneLake → globs the Delta
    /// commit files: flat <c>&lt;root&gt;/*/_delta_log/*.json</c> (schema "main") or, in <c>schemas</c> mode, the
    /// two-level <c>&lt;root&gt;/*/*/_delta_log/*.json</c> (the segment before <c>_delta_log</c> = table, the one
    /// before that = schema).</summary>
    private SortedSet<(string Schema, string Table)> DiscoverTablePairs()
    {
        var pairs = new SortedSet<(string Schema, string Table)>();
        var ol = OneLake();
        if (ol is not null)
        {
            // OneLake: DuckDB's azure glob can't recurse a _delta_log tree (PR #174), so tables are listed via the
            // OneLake DFS endpoint directly (GetPaths) — flat (Tables/<table>, schema "main") or schema-enabled
            // (Tables/<schema>/<table>); the schema-enabled flag is from the Fabric API. Resolved in OneLake().
            foreach (var (s, t) in ol.Tables)
            {
                pairs.Add((s, t));
            }
            return pairs;
        }

        // Local / S3 / plain ADLS: glob the commit files. schemas mode = two levels deep, else one.
        var glob = _schemas ? _root + "/*/*/_delta_log/*.json" : _root + "/*/_delta_log/*.json";
        var json = HostFs.Glob(Opener(), glob);
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = Normalize(el.GetProperty("path").GetString() ?? string.Empty);
            int marker = path.IndexOf("/_delta_log/", System.StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }
            // …/<table>/_delta_log/…  → the segment before "/_delta_log/" is the table.
            int tblSlash = path.LastIndexOf('/', marker - 1);
            var table = tblSlash < 0 ? path.Substring(0, marker) : path.Substring(tblSlash + 1, marker - tblSlash - 1);
            if (table.Length == 0)
            {
                continue;
            }
            string schema = MainSchema;
            if (_schemas && tblSlash > 0)
            {
                // …/<schema>/<table>/_delta_log/…  → the segment before <table> is the schema.
                int schSlash = path.LastIndexOf('/', tblSlash - 1);
                if (schSlash >= 0)
                {
                    schema = path.Substring(schSlash + 1, tblSlash - schSlash - 1);
                }
            }
            pairs.Add((schema, table));
        }
        return pairs;
    }

    /// <summary>Discovers tables as an Arrow metadata stream (schema_name, table_name, table_type).</summary>
    private IArrowArrayStream DiscoverTables()
    {
        var pairs = DiscoverTablePairs();
        var schemaCol = new List<string>();
        var nameCol = new List<string>();
        var typeCol = new List<string>();
        foreach (var (s, t) in pairs)
        {
            schemaCol.Add(s);
            nameCol.Add(t);
            typeCol.Add("BASE TABLE");
        }
        return ThreeColumn("schema_name", schemaCol, "table_name", nameCol, "table_type", typeCol);
    }

    public IArrowArrayStream ScanTable(string schemaName, string tableName, string? specJson,
                                       IArrowArrayStream? filterValues)
    {
        var opener = Opener();
        var path = TablePath(schemaName, tableName);
        // Push the FILTER into engineered-wood file/row-group skipping (superset-safe; DuckDB re-applies).
        // Projection is left to DuckDB above the scan (the full schema is returned, mapped by name) — same as
        // the global arrownet_delta_scan; column-pruning into parquet would need a projected-schema stream.
        var spec = ScanSpec.Parse(specJson);
        var filterVals = ReadFilterValues(filterValues);
        EngineeredWood.Expressions.Predicate? filter = spec?.Filter is { } node
            ? new DeltaFilterBuilder(filterVals).Build(node)
            : null;

        // Opt-in native read (native_read true): DuckDB's own read_parquet decodes the files. A per-file loop
        // pushes projection + the static filter + Delta-log FILE pruning, excludes each file's deletion vector,
        // and computes the transient _metadata.row_id — so plain SELECT, DELETE/UPDATE (rowid) and time travel
        // ALL run natively (no fallback). Snapshot pinning gives a consistent cut across a multi-table query.
        // See DeltaNativeReader + docs/multifile-delta.md §"Concrete plan".
        if (_nativeRead)
        {
            return ScanNative(opener, path, spec, filterVals);
        }

        // ---- engineered-wood C# reader (default: native_read off) ----
        // Time travel: `FROM t AT (VERSION => n)` / `AT (TIMESTAMP => ts)` — a read-only snapshot, so it uses
        // the plain stream (no rowid) and advertises the schema AS OF that version (which can differ from the
        // latest, e.g. before an ADD COLUMN). Delta supports BOTH version and timestamp (unlike the SQL provider,
        // which only does timestamp via FOR SYSTEM_TIME AS OF).
        if (spec?.At is { } at)
        {
            var atSchema = DeltaReader.GetSchemaAt(opener, path, at.Unit, at.Value);
            ThrowIfVariantCodecRead(atSchema);
            // DuckDB may still request the virtual rowid for a time-travel scan (its count(*)-via-rowid
            // optimization). Produce it (version-aware transient rowid) so the stream matches what DuckDB asked
            // for; otherwise the rowid (BIGINT) it expects collides with the first user column (the
            // "BIGINT referenced INTEGER" internal error). No DML against a past snapshot, so it's read-only.
            bool wantRowIdAt = spec.Columns is { } atCols && atCols.Contains(RowIdColumn);
            if (wantRowIdAt)
            {
                var atFields = new List<Field>(atSchema.FieldsList)
                {
                    new Field(RowIdColumn, Int64Type.Default, nullable: false),
                };
                return new AsyncEnumerableArrowStream(
                    new Schema(atFields, atSchema.Metadata),
                    DeltaReader.StreamWithRowIdsAt(opener, path, columns: null, filter, at.Unit, at.Value, default));
            }
            return new AsyncEnumerableArrowStream(
                atSchema, DeltaReader.StreamAt(opener, path, columns: null, filter, at.Unit, at.Value, default));
        }

        var userSchema = DeltaReader.GetSchema(opener, path);
        ThrowIfVariantCodecRead(userSchema);

        // When the scan requests the virtual rowid (UPDATE/DELETE plans), stream WITH the trailing
        // _metadata.row_id column and advertise it in the schema; DuckDB maps the requested output by name.
        bool wantRowId = spec?.Columns is { } cols && cols.Contains(RowIdColumn);
        if (wantRowId)
        {
            var fields = new List<Field>(userSchema.FieldsList)
            {
                new Field(RowIdColumn, Int64Type.Default, nullable: false),
            };
            var schemaWithRowId = new Schema(fields, userSchema.Metadata);
            return new AsyncEnumerableArrowStream(
                schemaWithRowId, DeltaReader.StreamWithRowIds(opener, path, columns: null, filter, default));
        }

        return new AsyncEnumerableArrowStream(userSchema, DeltaReader.Stream(opener, path, columns: null, filter, default));
    }

    // Native read (native_read true): resolve the snapshot (explicit AT > per-transaction pinned version >
    // latest), then let DuckDB's read_parquet decode the files via DeltaNativeReader. Pinning gives a consistent
    // cut across a multi-table query (all implicit-AT scans in one DuckDB transaction pin to the same instant).
    private IArrowArrayStream ScanNative(nint opener, string path, ScanSpec? spec, IReadOnlyList<object?> filterVals)
    {
        string? unit = null, value = null;
        if (spec?.At is { } at)
        {
            unit = at.Unit;
            value = at.Value; // explicit AT overrides the per-transaction pin
        }
        else
        {
            long txn = AmbientTransaction.Current;
            if (txn != 0)
            {
                long v = SnapshotPinning.PinVersion(txn, path,
                    inst => DeltaReader.ResolveVersionAsOf(opener, path, inst, _log), System.DateTime.UtcNow);
                unit = "version";
                value = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        var userSchema = unit is null
            ? DeltaReader.GetSchema(opener, path)
            : DeltaReader.GetSchemaAt(opener, path, unit, value!);
        return DeltaNativeReader.Read(opener, path, userSchema, spec, filterVals, unit, value);
    }

    private static IReadOnlyList<object?> ReadFilterValues(IArrowArrayStream? filterValues)
    {
        if (filterValues is null)
        {
            return System.Array.Empty<object?>();
        }
        using (filterValues)
        {
            var batch = filterValues.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                return System.Array.Empty<object?>();
            }
            var values = new object?[batch.ColumnCount];
            for (int i = 0; i < batch.ColumnCount; i++)
            {
                try { values[i] = ArrowValueReader.ReadScalar(batch.Column(i), 0); }
                catch (System.NotSupportedException) { values[i] = null; }
            }
            return values;
        }
    }

    // ---- VARIANT gates ----
    // A VARIANT column crosses the boundary as the tagged transport struct (arrow.parquet.variant) and its
    // parquet layout is the annotated variant group — only DuckDB's own parquet reader/writer produce/consume
    // that layout, so variant tables REQUIRE the native paths. The EW backstops (DeltaTable) also reject codec
    // writes/rewrites, but gating here gives the actionable ATTACH-option error up front.

    private static bool SchemaHasVariant(Schema schema)
    {
        foreach (var f in schema.FieldsList)
        {
            if (FieldHasVariant(f))
            {
                return true;
            }
        }
        return false;
    }

    private static bool FieldHasVariant(Field field)
    {
        if (EngineeredWood.DeltaLake.Schema.SchemaConverter.IsVariantArrowField(field))
        {
            return true;
        }
        if (field.DataType is StructType st)
        {
            foreach (var child in st.Fields)
            {
                if (FieldHasVariant(child))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // True when a variant marker sits BELOW the top level (struct/list/map, any depth).
    private static bool HasNestedVariant(Schema schema)
    {
        foreach (var f in schema.FieldsList)
        {
            if (TypeHasVariant(f.DataType))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TypeHasVariant(Apache.Arrow.Types.IArrowType type) => type switch
    {
        StructType st => st.Fields.Any(FieldHasVariant),
        ListType lt => EngineeredWood.DeltaLake.Schema.SchemaConverter.IsVariantArrowField(lt.ValueField)
                       || TypeHasVariant(lt.ValueDataType),
        MapType mt => EngineeredWood.DeltaLake.Schema.SchemaConverter.IsVariantArrowField(mt.KeyField)
                      || EngineeredWood.DeltaLake.Schema.SchemaConverter.IsVariantArrowField(mt.ValueField)
                      || TypeHasVariant(mt.KeyField.DataType) || TypeHasVariant(mt.ValueField.DataType),
        _ => false,
    };

    private void EnsureVariantWritable(Schema schema)
    {
        // Nested variant (struct/list/map, any depth) cannot be WRITTEN: DuckDB's parquet writer rejects a
        // non-root VARIANT ("requires a transform, but is not a root column"), so the table would be
        // unusable — fail the CREATE/INSERT with the reason instead.
        if (HasNestedVariant(schema))
        {
            throw new System.NotSupportedException(
                "VARIANT is only supported as a TOP-LEVEL column (DuckDB's parquet writer cannot write a "
                + "VARIANT nested inside a struct/list/map).");
        }
        if (!SchemaHasVariant(schema))
        {
            return;
        }
        if (!_nativeWrite || !_nativeRead)
        {
            throw new System.NotSupportedException(
                "VARIANT columns require the native paths — ATTACH the Delta catalog with "
                + "native_write true AND native_read true (DuckDB's own parquet writer/reader handle the "
                + "variant layout; the built-in codec cannot).");
        }
        if (_changeDataFeedOnCreate)
        {
            throw new System.NotSupportedException(
                "VARIANT columns cannot be combined with change_data_feed true (the CDC change files are "
                + "written by the built-in codec, which cannot emit the variant layout).");
        }
    }

    // Called on the codec-read branch only (the native path handles variant).
    private static void ThrowIfVariantCodecRead(Schema schema)
    {
        if (SchemaHasVariant(schema))
        {
            throw new System.NotSupportedException(
                "Reading a table with VARIANT columns requires the native_read true ATTACH option "
                + "(DuckDB's own parquet reader decodes the variant layout; the built-in codec cannot).");
        }
    }

    // ---- write surface (INSERT / CTAS / COPY via the streaming bulk path) ----

    /// <summary>Streaming bulk write (INSERT / CTAS / COPY). Runs on the bulk consumer thread; the host-FS
    /// opener was re-established on it by BulkSession. createTable/replace => Overwrite (CTAS/REPLACE: the table
    /// becomes exactly these rows); otherwise Append (INSERT). One Delta commit. Returns rows written.</summary>
    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId, IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode, bool partitionOverwrite)
    {
        // sortColumns (native SORTED BY) is a SQL-Server-warehouse CLUSTER BY concept; Delta doesn't cluster — ignored.
        var opener = Opener();
        _log.LogInformation("delta bulk {Schema}.{Table}: create={Create} replace={Replace} native_write={Native} partition_overwrite={PartOw}",
            schemaName, tableName, createTable, replace, _nativeWrite, partitionOverwrite);
        EnsureVariantWritable(data.Schema);
        // Partition columns take effect only at table creation; an INSERT (Append) into an existing table
        // preserves the table's declared partitioning (engineered-wood reads it from the metadata).
        var spec = ResolveWriteSpec(createTable || replace ? partitionColumns : null, schemaMode);
        // Data mode: schema_mode=overwrite forces a full replace (adopt the source schema); CREATE/CTAS/REPLACE
        // also overwrite; otherwise it's an append (INSERT / COPY create_table=false / schema_mode=merge).
        bool overwrite = createTable || replace || spec?.SchemaMode == DeltaSchemaMode.Overwrite;
        var mode = overwrite ? DeltaWriteMode.Overwrite : DeltaWriteMode.Append;
        // replace_where (atomic partition-overwrite) is an APPEND-time concept — for a full (re)write, drop it.
        if (overwrite && spec?.ReplaceWhere is not null)
        {
            spec = spec with { ReplaceWhere = null };
        }
        // PARTITION_OVERWRITE (dynamic partition overwrite): append-shaped only (the C++ COPY already rejects
        // CREATE_TABLE/REPLACE; SCHEMA_MODE 'overwrite' can still force a full replace → reject the combo), and
        // mutually exclusive with the STATIC replace_where filter (one target-set source, not two).
        if (partitionOverwrite)
        {
            if (overwrite)
            {
                throw new System.ArgumentException(
                    "PARTITION_OVERWRITE cannot be combined with a full replace (CREATE_TABLE/REPLACE/"
                    + "SCHEMA_MODE 'overwrite') — it appends into an existing partitioned table, atomically "
                    + "replacing only the partitions present in the input.");
            }
            if (spec?.ReplaceWhere is { Count: > 0 })
            {
                throw new System.ArgumentException(
                    "PARTITION_OVERWRITE (dynamic — target partitions derived from the input) cannot be combined "
                    + "with replace_where (static — an explicit partition filter); use one or the other.");
            }
            spec = (spec ?? new DeltaWriteSpec(null, null, null, null)) with { DynamicPartitionOverwrite = true };
        }
        var tablePath = TablePath(schemaName, tableName);

        // native_write: STREAM straight to DuckDB's parquet writer (bounded memory — important for a Fabric
        // notebook). TryWriteStreaming returns null (WITHOUT consuming `data`) for cases the single-file streaming
        // commit can't represent (partitioned+mapping / replace_where / schema_mode=merge / mapping-replace with a
        // schema change / identity / iceberg) → fall through to the collect path with `data` intact. Column-mapping
        // tables (BOTH modes) stream: TryWriteStreaming creates the table WITH the mode, and the COPY renames the
        // columns to their PHYSICAL names + stamps FIELD_IDS (the Delta-spec file layout for both modes).
        if (_nativeWrite)
        {
            var streamedVersion = DeltaWriter.TryWriteStreaming(
                opener, tablePath, data, mode,
                deletionVectors: _deletionVectorsOnCreate,
                inCommitTimestamps: _inCommitTimestampsOnCreate,
                changeDataFeed: _changeDataFeedOnCreate,
                rowTracking: _rowTrackingOnCreate,
                spec: spec, materializeRowTracking: _materializeRowTracking,
                out var streamedRows,
                columnMapping: _columnMappingMode);
            if (streamedVersion is not null)
            {
                return streamedRows;
            }
        }

        // Collect path: materialize the whole stream in C# (bounded by RAM), then write via engineered-wood's
        // codec OR DuckDB's per-file writer (native_write, non-streamable case: partitioned/merge/…).
        var (schema, batches, rows) = DeltaWriter.Materialize(data, default);
        DeltaWriter.Write(opener, tablePath, schema, batches, mode, default,
                          deletionVectors: _deletionVectorsOnCreate,
                          inCommitTimestamps: _inCommitTimestampsOnCreate,
                          changeDataFeed: _changeDataFeedOnCreate,
                          rowTracking: _rowTrackingOnCreate,
                          spec: spec, nativeWrite: _nativeWrite,
                          materializeRowTracking: _materializeRowTracking,
                          columnMapping: _columnMappingMode);
        return rows;
    }

    /// <summary>Creates an empty Delta table (commit 0 with the schema). Idempotent (OpenOrCreate), so
    /// <paramref name="ifNotExists"/> is satisfied; PK/UNIQUE/DEFAULT are ignored (Delta has no such constraints).</summary>
    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                            string? primaryKey, string? uniques, string? defaults,
                            IReadOnlyList<string>? partitionColumns, IReadOnlyList<string>? sortColumns,
                            IReadOnlyList<string>? identityColumns)
    {
        // Commit 0 itself is metadata-only, but a variant table is unusable without the native paths — fail
        // the CREATE up front with the actionable ATTACH-option error rather than at the first INSERT/SELECT.
        EnsureVariantWritable(columns);
        // sortColumns (CLUSTER BY) is a SQL-Server-warehouse concept — Delta ignores it.
        // identityColumns (the DuckDB `AS (0)` generated-column marker, v53): Delta has NATIVE identity —
        // attach delta.identity.* metadata (start 1, step 1, GENERATED ALWAYS) to the marked fields.
        // engineered-wood does the rest: the identityColumns writer feature at create, per-batch value
        // generation on every write (IdentityColumnWriter.ProcessBatch), and the highWaterMark update in the
        // SAME commit. Streaming falls back to the collect path for identity tables
        // (SupportsExternalDataFileCommit=false), which under native_write still emits native parquet via the
        // IDataFileWriter seam. An OCC-retried append REGENERATES values from the fresh snapshot's high-water
        // mark (the input batches are not mutated), so concurrent identity appends are safe — unlike Spark,
        // which rejects concurrent transactions on identity tables outright.
        if (identityColumns is { Count: > 0 })
        {
            var idSet = new HashSet<string>(identityColumns, System.StringComparer.OrdinalIgnoreCase);
            var withIdentity = new List<Field>(columns.FieldsList.Count);
            foreach (var f in columns.FieldsList)
            {
                if (idSet.Contains(f.Name))
                {
                    var meta = new Dictionary<string, string>(
                        EngineeredWood.DeltaLake.Schema.IdentityColumn.CreateMetadata(
                            start: 1, step: 1, allowExplicitInsert: false));
                    if (f.Metadata is { } src)
                    {
                        foreach (var kv in src) { meta[kv.Key] = kv.Value; }
                    }
                    // Keep nullable=true on the DuckDB-facing schema: the INSERT stream carries NULLs for the
                    // engine-assigned column (ProcessBatch replaces them); files never actually hold nulls.
                    withIdentity.Add(new Field(f.Name, f.DataType, nullable: true, meta));
                }
                else
                {
                    withIdentity.Add(f);
                }
            }
            columns = new Schema(withIdentity, columns.Metadata);
        }
        DeltaWriter.Create(Opener(), TablePath(schemaName, tableName), columns, default,
                              deletionVectors: _deletionVectorsOnCreate,
                              inCommitTimestamps: _inCommitTimestampsOnCreate,
                              changeDataFeed: _changeDataFeedOnCreate,
                              rowTracking: _rowTrackingOnCreate,
                              spec: ResolveWriteSpec(partitionColumns, schemaModeArg: null),
                              materializeRowTracking: _materializeRowTracking,
                              columnMapping: _columnMappingMode);
    }

    /// <summary>CREATE SCHEMA. In <c>schemas</c> mode (non-OneLake) it materializes the <c>&lt;root&gt;/&lt;schema&gt;/</c>
    /// subfolder so a subsequent CREATE TABLE lands there (and the schema is rediscovered once it holds a table).
    /// Otherwise a no-op: OneLake schemas mirror the lakehouse, and the flat layout has only "main".</summary>
    public void CreateSchema(string s, bool ie)
    {
        if (_schemas && OneLake() is null && !string.Equals(s, MainSchema, System.StringComparison.Ordinal))
        {
            HostFs.CreateDir(Opener(), _root + "/" + s); // recursive mkdir; idempotent
        }
    }
    public void BeginTransaction() { }              // Delta is per-commit (no cross-statement transaction)
    public void CommitTransaction() { }
    public void RollbackTransaction() { }

    // ---- still unsupported in this slice ----
    private static NotSupportedException Unsupported(string what) =>
        new($"delta provider: {what} not supported yet.");

    /// <summary>DROP TABLE = recursively delete the table's <c>&lt;root&gt;/&lt;table&gt;/</c> folder (its _delta_log
    /// + all data files). OneLake goes through the <b>DFS endpoint directly</b>
    /// (<see cref="FabricLakehouse.DeleteDirectory"/>) — DuckDB's azure FileSystem has no RemoveDirectory; local/S3
    /// use the host's recursive directory-delete callback. Idempotent (no error if missing), so
    /// <paramref name="ifExists"/> is satisfied either way.</summary>
    public void DropTable(string schemaName, string tableName, bool ifExists)
    {
        _log.LogInformation("delta drop table {Schema}.{Table} (onelake={OneLake})",
            schemaName, tableName, FabricLakehouse.IsOneLake(_root));
        if (FabricLakehouse.IsOneLake(_root))
        {
            FabricLakehouse.DeleteDirectory(TablePath(schemaName, tableName), _fabricCredential);
            return;
        }
        if (!HostFs.CanRemoveDir)
        {
            throw Unsupported("DROP TABLE (host does not provide a recursive directory-delete callback)");
        }
        HostFs.RemoveDir(Opener(), TablePath(schemaName, tableName));
    }

    /// <summary>DELETE = rowid-based via Delta row tracking: <paramref name="keys"/> is a stream whose single
    /// <c>_metadata.row_id</c> Int64 column holds the stable ids of the rows to delete (DuckDB's scan produced
    /// them, applying the WHERE). Collected and applied via deletion vectors (<see cref="DeltaReader.DeleteByRowIds"/>).</summary>
    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys)
    {
        var opener = Opener();
        var ids = new List<long>();
        using (keys)
        {
            while (keys.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
            {
                using (batch)
                {
                    if (batch.Length == 0)
                    {
                        continue;
                    }
                    // The keys batch has exactly the rowid column(s); a virtual rowid is the single Int64
                    // _metadata.row_id (column 0).
                    if (batch.Column(0) is Int64Array idArray)
                    {
                        for (int i = 0; i < idArray.Length; i++)
                        {
                            if (idArray.GetValue(i) is { } id)
                            {
                                ids.Add(id);
                            }
                        }
                    }
                }
            }
        }
        if (ids.Count == 0)
        {
            return 0;
        }
        var path = TablePath(schemaName, tableName);
        // Follow the TABLE's config: deletion-vector tables get the no-rewrite DV delete; everything else is
        // copy-on-write. (Honors external DV tables regardless of this catalog's create-time flag.)
        // DV-mode delete writes no data file (just a new DV + remove/add) → native writer N/A; copy-on-write
        // rewrite honors native_write (DuckDB writes the survivor file).
        bool dvMode = DeltaReader.IsDeletionVectorsEnabled(opener, path);
        _log.LogInformation("delta delete {Schema}.{Table}: rows={Rows} mode={Mode} native_write={Native}",
            schemaName, tableName, ids.Count, dvMode ? "deletion-vector" : "copy-on-write",
            !dvMode && _nativeWrite);
        return dvMode
            ? DeltaReader.DeleteByRowIdsViaVectors(opener, path, ids, default)
            : DeltaReader.DeleteByRowIds(opener, path, ids, default, _nativeWrite, _nativeRead);
    }

    public IArrowArrayStream ExecuteQuery(string sql) => throw Unsupported("raw query");

    // Maintenance command dialect, invoked via mssql_net_exec('<catalog>', '<cmd>'):
    //   OPTIMIZE <table>                      -- bin-pack small files (excludes DV-deleted rows)
    //   VACUUM   <table> [RETAIN <hours> HOURS] [DRY RUN]
    // <table> is '<schema>.<table>' (schema defaults to 'main'; qualify on a schema-enabled lakehouse). Returns
    // the affected count (VACUUM = files deleted; OPTIMIZE = 0). Important under DV-default: DVs + merge-on-read
    // append small files accumulate, so OPTIMIZE consolidates them (and materializes DV deletions).
    public long ExecuteNonQuery(string sql)
    {
        var text = (sql ?? string.Empty).Trim();
        var tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw Unsupported($"exec '{text}' — expected OPTIMIZE|VACUUM <table> …");
        }
        var opener = Opener();
        int dot = tokens[1].IndexOf('.');
        var (schema, table) = dot >= 0
            ? (tokens[1].Substring(0, dot), tokens[1].Substring(dot + 1))
            : ("main", tokens[1]);
        var path = TablePath(schema, table);
        switch (tokens[0].ToUpperInvariant())
        {
            case "OPTIMIZE":
                _log.LogInformation("delta exec OPTIMIZE {Schema}.{Table} native_write={Native}", schema, table, _nativeWrite);
                return DeltaReader.Optimize(opener, path, default, _nativeWrite, _nativeRead);
            case "VACUUM":
            {
                bool dryRun = HasToken(tokens, "DRY");
                double? retentionHours = null;
                int r = TokenIndex(tokens, "RETAIN");
                if (r >= 0 && r + 1 < tokens.Length && double.TryParse(tokens[r + 1],
                        System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var h))
                {
                    retentionHours = h;
                }
                _log.LogInformation("delta exec VACUUM {Schema}.{Table} dry_run={Dry}", schema, table, dryRun);
                return DeltaReader.Vacuum(opener, path, dryRun, retentionHours, default);
            }
            default:
                throw Unsupported($"exec verb '{tokens[0]}' — supported: OPTIMIZE, VACUUM");
        }
    }

    private static bool HasToken(string[] tokens, string token) => TokenIndex(tokens, token) >= 0;

    private static int TokenIndex(string[] tokens, string token)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], token, System.StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>UPDATE = rowid-based copy-on-write: <paramref name="data"/> carries the new SET-column values
    /// (columns 0..<paramref name="setColumnCount"/>-1, named by the target column) + the transient
    /// <c>_metadata.row_id</c> (last column). We re-scan the table with rowids, replace the SET columns on the
    /// matched rows (rebuilt as clean Apache.Arrow batches), and OVERWRITE via the proven write path — so the
    /// output is plain Delta + standard-readable (delta-kernel/Spark/Fabric). Returns rows updated.</summary>
    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
    {
        var opener = Opener();
        var path = TablePath(schemaName, tableName);

        // 1. Parse the update stream: rowid -> new SET values (aligned to the SET column order).
        var setColNames = new List<string>();
        var updates = new Dictionary<long, object?[]>();
        using (data)
        {
            while (data.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } b)
            {
                using (b)
                {
                    if (b.Length == 0)
                    {
                        continue;
                    }
                    if (setColNames.Count == 0)
                    {
                        for (int j = 0; j < setColumnCount; j++)
                        {
                            setColNames.Add(b.Schema.FieldsList[j].Name);
                        }
                    }
                    var ridArr = (Int64Array)b.Column(setColumnCount);
                    for (int i = 0; i < b.Length; i++)
                    {
                        if (ridArr.GetValue(i) is not { } rid)
                        {
                            continue;
                        }
                        var vals = new object?[setColumnCount];
                        for (int j = 0; j < setColumnCount; j++)
                        {
                            // Deep variant: a STRUCT SET value becomes a Dictionary (deep-copied — the batch
                            // is disposed after this loop).
                            vals[j] = ArrowValueReader.ReadScalarDeep(b.Column(j), i);
                        }
                        updates[rid] = vals;
                    }
                }
            }
        }
        if (updates.Count == 0)
        {
            return 0;
        }

        // 2. Map SET column names -> user-schema column indices (case-insensitive).
        var userSchema = DeltaReader.GetSchema(opener, path);
        var fields = userSchema.FieldsList;
        var setSlotByColumn = new int[fields.Count];
        var setSlotField = new Field[setColNames.Count]; // the canonical user field for each SET slot
        for (int c = 0; c < fields.Count; c++)
        {
            setSlotByColumn[c] = -1;
            for (int j = 0; j < setColNames.Count; j++)
            {
                if (string.Equals(fields[c].Name, setColNames[j], System.StringComparison.OrdinalIgnoreCase))
                {
                    setSlotByColumn[c] = j;
                    setSlotField[j] = fields[c];
                    break;
                }
            }
        }

        // STRUCT SET values work on COLUMN-MAPPING tables too: the copy-on-write rewrite applies
        // engineered-wood's RECURSIVE physical-rename + field-id stamping (ColumnMappingRecursive.ToPhysical),
        // so a substituted struct column (rebuilt logical-named from the table schema by BuildArray) lands in
        // the spec nested layout; pass-through columns read from data files are already physical and just get
        // their ids stamped.

        // 2b. native_write: build the per-file-ordinal (position -> new SET values) Arrow view the native rewriter
        //     LEFT JOINs against — so DuckDB applies the substitution in SQL and BuildArray is retired for the
        //     supported shape. Keyed [__arrownet_pos:int64 ++ <set columns, canonical name+type>]. Built here (this
        //     is where the boxed new values live); the rewriter binds it. Fallback (unsupported shape / no host
        //     query) leaves rewriter null → engineered-wood reads + the rewriteFile callback substitutes in-process.
        IDataFileRewriter? rewriter = null;
        if (_nativeWrite && NativeParquetDataFileRewriter.Available)
        {
            var rowsByOrdinal = new Dictionary<int, List<KeyValuePair<long, object?[]>>>();
            foreach (var kv in updates)
            {
                int ord = (int)(kv.Key >> RowIdPositionBits);
                if (!rowsByOrdinal.TryGetValue(ord, out var list))
                {
                    list = new List<KeyValuePair<long, object?[]>>();
                    rowsByOrdinal[ord] = list;
                }
                list.Add(kv);
            }
            var updatesByOrdinal = new Dictionary<int, RecordBatch>(rowsByOrdinal.Count);
            long posMask = (1L << RowIdPositionBits) - 1;
            foreach (var (ord, rows) in rowsByOrdinal)
            {
                var posBuilder = new Int64Array.Builder();
                var colVals = new List<object?>[setColNames.Count];
                for (int j = 0; j < setColNames.Count; j++)
                {
                    colVals[j] = new List<object?>(rows.Count);
                }
                foreach (var kv in rows)
                {
                    posBuilder.Append(kv.Key & posMask);
                    for (int j = 0; j < setColNames.Count; j++)
                    {
                        colVals[j].Add(kv.Value[j]);
                    }
                }
                var batchFields = new List<Field>(setColNames.Count + 1)
                {
                    new Field("__arrownet_pos", Int64Type.Default, nullable: false),
                };
                var batchArrays = new List<IArrowArray>(setColNames.Count + 1) { posBuilder.Build() };
                for (int j = 0; j < setColNames.Count; j++)
                {
                    var field = setSlotField[j];
                    batchArrays.Add(BuildArray(field.DataType, colVals[j]));
                    // Keep the field metadata: the arrownet.variant transport marker types the bound view's
                    // column as VARIANT in the host engine (else the CASE substitution mismatches BLOB/VARIANT).
                    batchFields.Add(new Field(field.Name, field.DataType, nullable: true, field.Metadata));
                }
                var batchSchema = new Apache.Arrow.Schema(batchFields, null);
                updatesByOrdinal[ord] = new RecordBatch(batchSchema, batchArrays, rows.Count);
            }
            var setColCanonical = setSlotField.Select(f => f.Name).ToList();
            rewriter = new NativeParquetDataFileRewriter(path, userSchema, setColCanonical, updatesByOrdinal);
        }

        // 3. Per-file copy-on-write: engineered-wood rewrites ONLY the files containing a matched row. For each
        //    such file it hands us (fileOrdinal, the file's batches in read order); we rebuild the SET columns
        //    on the matched positions (rowid = (ordinal << RowIdPositionBits) | positionInFile — same encoding
        //    the scan emitted) and return the modified batches. Unaffected files are left untouched.
        DeltaReader.UpdateByRowIds(opener, path, updates.Keys, (ordinal, batches) =>
        {
            // Each batch is the file's USER columns (0..fields.Count-1) + a trailing _metadata.row_id (last) =
            // the ABSOLUTE rowid. Match each row by its rowid (robust even when the file has a deletion vector).
            var outBatches = new List<RecordBatch>(batches.Count);
            foreach (var batch in batches)
            {
                var rids = (Int64Array)batch.Column(batch.ColumnCount - 1);
                var newCols = new IArrowArray[fields.Count];
                for (int c = 0; c < fields.Count; c++)
                {
                    int slot = setSlotByColumn[c];
                    if (slot < 0)
                    {
                        newCols[c] = batch.Column(c); // unchanged column
                        continue;
                    }
                    var values = new List<object?>(batch.Length);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        long rid = rids.GetValue(i) ?? -1;
                        values.Add(updates.TryGetValue(rid, out var nv)
                            ? nv[slot]
                            : ArrowValueReader.ReadScalarDeep(batch.Column(c), i));
                    }
                    newCols[c] = BuildArray(fields[c].DataType, values);
                }
                outBatches.Add(new RecordBatch(userSchema, newCols, batch.Length));
            }
            return outBatches;
        }, default, _nativeWrite, rewriter, _nativeRead);

        _log.LogInformation("delta update {Schema}.{Table}: rows={Rows} set_cols={SetCols} native_write={Native}",
            schemaName, tableName, updates.Count, setColNames.Count, _nativeWrite);
        return updates.Count; // each distinct rowid is one updated row
    }

    /// <summary>Builds an Arrow array of <paramref name="type"/> from boxed CLR values (the inverse of
    /// <see cref="ArrowValueReader.ReadScalar"/>) — used to rebuild a SET column during UPDATE. Covers the types
    /// DuckDB↔Delta exchanges; an unsupported SET-column type throws (the UPDATE fails cleanly).</summary>
    private static IArrowArray BuildArray(Apache.Arrow.Types.IArrowType type, List<object?> values)
    {
        switch (type)
        {
            case BooleanType: { var b = new BooleanArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((bool)v); } return b.Build(); }
            case Int8Type: { var b = new Int8Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((sbyte)v); } return b.Build(); }
            case Int16Type: { var b = new Int16Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((short)v); } return b.Build(); }
            case Int32Type: { var b = new Int32Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((int)v); } return b.Build(); }
            case Int64Type: { var b = new Int64Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((long)v); } return b.Build(); }
            case UInt8Type: { var b = new UInt8Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((byte)v); } return b.Build(); }
            case UInt16Type: { var b = new UInt16Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((ushort)v); } return b.Build(); }
            case UInt32Type: { var b = new UInt32Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((uint)v); } return b.Build(); }
            case UInt64Type: { var b = new UInt64Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((ulong)v); } return b.Build(); }
            case FloatType: { var b = new FloatArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((float)v); } return b.Build(); }
            case DoubleType: { var b = new DoubleArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((double)v); } return b.Build(); }
            case Decimal128Type d: { var b = new Decimal128Array.Builder(d); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((decimal)v); } return b.Build(); }
            case StringType: { var b = new StringArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((string)v); } return b.Build(); }
            // BLOB — incl. the arrownet.variant transport (a VARIANT SET value crosses as its blob form).
            case BinaryType: { var b = new BinaryArray.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(((byte[])v).AsSpan()); } return b.Build(); }
            case Date32Type: { var b = new Date32Array.Builder(); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(System.DateOnly.FromDateTime((System.DateTime)v)); } return b.Build(); }
            case TimestampType ts: { var b = new TimestampArray.Builder(ts); foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(v is System.DateTimeOffset dto ? dto : new System.DateTimeOffset(System.DateTime.SpecifyKind((System.DateTime)v, System.DateTimeKind.Utc))); } return b.Build(); }
            case Apache.Arrow.Types.StructType st:
            {
                // A struct value is a Dictionary<string, object?> from ReadScalarDeep (or null for a NULL row).
                // Build each child column recursively from the extracted member values; rebuild the struct's own
                // validity bitmap.
                int n = values.Count;
                var validity = new ArrowBuffer.BitmapBuilder(n);
                int nulls = 0;
                var childVals = new List<object?>[st.Fields.Count];
                for (int c = 0; c < st.Fields.Count; c++)
                {
                    childVals[c] = new List<object?>(n);
                }
                foreach (var v in values)
                {
                    bool isNull = v is null;
                    validity.Append(!isNull);
                    if (isNull) { nulls++; }
                    var dict = v as System.Collections.Generic.IReadOnlyDictionary<string, object?>;
                    for (int c = 0; c < st.Fields.Count; c++)
                    {
                        childVals[c].Add(dict is not null && dict.TryGetValue(st.Fields[c].Name, out var cv)
                            ? cv : null);
                    }
                }
                var childData = new ArrayData[st.Fields.Count];
                for (int c = 0; c < st.Fields.Count; c++)
                {
                    childData[c] = BuildArray(st.Fields[c].DataType, childVals[c]).Data;
                }
                var data = new ArrayData(st, n, nulls, 0, new[] { validity.Build() }, childData);
                return Apache.Arrow.ArrowArrayFactory.BuildArray(data);
            }
            default: throw new NotSupportedException($"delta UPDATE: unsupported SET column type {type.TypeId}");
        }
    }

    public IArrowArrayStream InsertReturning(string s, string t, IArrowArrayStream r) => throw Unsupported("INSERT ... RETURNING");
    /// <summary>DROP SCHEMA. In <c>schemas</c> mode (non-OneLake) it recursively removes the
    /// <c>&lt;root&gt;/&lt;schema&gt;/</c> subfolder (and every table under it). Unsupported otherwise (OneLake
    /// schemas mirror the lakehouse; the flat layout has only "main").</summary>
    public void DropSchema(string s, bool ie)
    {
        if (_schemas && OneLake() is null)
        {
            if (string.Equals(s, MainSchema, System.StringComparison.Ordinal))
            {
                throw Unsupported("DROP SCHEMA main (the default schema)");
            }
            HostFs.RemoveDir(Opener(), _root + "/" + s); // recursive; idempotent
            return;
        }
        throw Unsupported("DROP SCHEMA");
    }
    /// <summary>Schema evolution. Supported on Delta: <c>ADD COLUMN</c> (a metadata-only commit appending a
    /// nullable column — no file rewrite; old rows read back NULL) and <c>RENAME TABLE</c> (a folder move — the
    /// <c>_delta_log</c> uses table-relative paths, so moving the whole folder preserves the table; OneLake uses
    /// the DFS endpoint's atomic native rename). RENAME/DROP COLUMN + ALTER COLUMN TYPE need column mapping or a
    /// full rewrite (clean error). For ADD COLUMN <paramref name="a1"/> = the new column's name and
    /// <paramref name="c"/> carries its Arrow type + nullability; for RENAME TABLE <paramref name="a1"/> = the new
    /// table name.</summary>
    public void AlterTable(int k, string s, string t, string? a1, string? a2, Field? c, int f)
    {
        _log.LogInformation("delta alter {Schema}.{Table}: kind={Kind} arg={Arg}", s, t, k, a1);
        switch (k)
        {
            case AlterKind.AddColumn:
            {
                var col = c ?? throw new System.InvalidOperationException(
                    "delta ADD COLUMN requires a column definition.");
                string name = a1 ?? col.Name;
                var field = string.Equals(name, col.Name, System.StringComparison.Ordinal)
                    ? col
                    : new Field(name, col.DataType, col.IsNullable);
                DeltaReader.AddColumn(Opener(), TablePath(s, t), field, default);
                return;
            }
            case AlterKind.RenameColumn:
            {
                // a1 = old column name, a2 = new column name (C++ RenameColumnInfo). Metadata-only commit on a
                // column-mapping table (the field keeps its physicalName + columnMapping.id); EW rejects a plain
                // table (which would need a full rewrite). Opener threaded for the host-FS log write.
                string oldCol = a1 ?? throw new System.InvalidOperationException(
                    "delta RENAME COLUMN requires the old column name.");
                string newCol = a2 ?? throw new System.InvalidOperationException(
                    "delta RENAME COLUMN requires the new column name.");
                DeltaReader.RenameColumn(Opener(), TablePath(s, t), oldCol, newCol, default);
                return;
            }
            case AlterKind.DropColumn:
            {
                // a1 = column name. Metadata-only commit on a column-mapping table (old files keep the physical
                // column; readers reconcile it away); EW rejects a plain table (would need a full rewrite).
                string dropCol = a1 ?? throw new System.InvalidOperationException(
                    "delta DROP COLUMN requires the column name.");
                DeltaReader.DropColumn(Opener(), TablePath(s, t), dropCol, default);
                return;
            }
            case AlterKind.RenameTable:
            {
                string newName = a1 ?? throw new System.InvalidOperationException(
                    "delta RENAME TABLE requires a new table name.");
                // The table folder (incl. _delta_log) is moved; the schema is unchanged (RENAME TABLE renames
                // within the same schema). OneLake → DFS atomic rename (Azure MoveFile is unimplemented); local/S3
                // → the host FS move (FileSystem::MoveFile — atomic on local; an object store throws cleanly).
                if (FabricLakehouse.IsOneLake(_root))
                {
                    FabricLakehouse.RenameDirectory(TablePath(s, t), TablePath(s, newName), _fabricCredential);
                }
                else
                {
                    HostFs.MoveDir(Opener(), TablePath(s, t), TablePath(s, newName));
                }
                return;
            }
            default:
                throw Unsupported("ALTER TABLE (only ADD/RENAME/DROP COLUMN and RENAME TABLE are supported on Delta)");
        }
    }

    public Schema GetFunctionParamSchema(string s, string f) => throw NoFunctions();
    public Schema GetFunctionReturnSchema(string s, string f) => throw NoFunctions();
    public IArrowArrayStream ExecuteScalar(string s, string f, IArrowArrayStream a) => throw NoFunctions();
    public Schema GetFunctionOutputSchema(string s, string f, RecordBatch? a = null) => throw NoFunctions();
    public IBoundTable TableBind(string s, string f, RecordBatch? a) => throw NoFunctions();
    public IArrowInOutBinding InOutBind(string s, string f, RecordBatch? a, Schema input) => throw NoFunctions();
    public IAggregateSession AggOpen(string s, string f) => throw NoFunctions();
    private static NotSupportedException NoFunctions() => new("delta provider: no catalog functions.");

    public void Dispose() { }

    // ---- Arrow metadata-stream helpers (mirror DaxCatalog) ----
    private static IArrowArrayStream SingleColumn(string name, IReadOnlyList<string> values)
    {
        var schema = new Schema(new[] { new Field(name, StringType.Default, nullable: true) }, null);
        var b = new StringArray.Builder();
        foreach (var v in values) { b.Append(v); }
        return new InMemoryArrayStream(schema, new[] { new RecordBatch(schema, new IArrowArray[] { b.Build() }, values.Count) });
    }

    private static IArrowArrayStream TwoColumn(string n0, IReadOnlyList<string> c0, string n1, IReadOnlyList<string> c1)
    {
        var schema = new Schema(new[]
        {
            new Field(n0, StringType.Default, nullable: true),
            new Field(n1, StringType.Default, nullable: true),
        }, null);
        static IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) { b.Append(v); }
            return b.Build();
        }
        return new InMemoryArrayStream(schema,
            new[] { new RecordBatch(schema, new[] { Build(c0), Build(c1) }, c0.Count) });
    }

    private static IArrowArrayStream ThreeColumn(string n0, IReadOnlyList<string> c0, string n1,
                                                 IReadOnlyList<string> c1, string n2, IReadOnlyList<string> c2)
    {
        var schema = new Schema(new[]
        {
            new Field(n0, StringType.Default, nullable: true),
            new Field(n1, StringType.Default, nullable: true),
            new Field(n2, StringType.Default, nullable: true),
        }, null);
        static IArrowArray Build(IReadOnlyList<string> vals)
        {
            var b = new StringArray.Builder();
            foreach (var v in vals) { b.Append(v); }
            return b.Build();
        }
        return new InMemoryArrayStream(schema,
            new[] { new RecordBatch(schema, new[] { Build(c0), Build(c1), Build(c2) }, c0.Count) });
    }

    private static IArrowArrayStream EmptyStringTable(params string[] columns)
    {
        var builder = new Schema.Builder();
        foreach (var c in columns) { builder.Field(new Field(c, StringType.Default, nullable: true)); }
        return new InMemoryArrayStream(builder.Build(), System.Array.Empty<RecordBatch>());
    }
}
