using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// The Delta Lake provider backed by <b>engineered-wood</b> (the 3rd <see cref="IBackend"/>, after SQL Server
/// and DAX): a Delta <b>folder</b> is an ATTACH-able catalog root —
/// <c>ATTACH '/lake' AS lake (TYPE fabricator, PROVIDER 'engineeredwooddelta')</c> (or an <c>abfss://…</c>
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

    // `delta` is an alias for RESOLUTION only — the two names select different DEFAULT PROFILES (see
    // NativeDefaultsFor). The primary name distinguishes this engineered-wood-backed provider from a future
    // delta-rs production provider.
    public IEnumerable<string> Aliases => new[] { "delta" };

    /// <summary>
    /// The name → DEFAULT PROFILE table. Both names reach the same catalog; they differ only in what
    /// <c>native_read</c> / <c>native_write</c> default to when the ATTACH does not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PROVIDER 'delta'</c> ⇒ <b>native on</b>: DuckDB's parquet reader and writer move the bytes while
    /// engineered-wood owns the <c>_delta_log</c>. This is the production path — it is the one with bounded-memory
    /// streaming writes, and it is the ONLY one that can recluster a liquid-clustered table, because that rewrite's
    /// global ORDER BY relies on DuckDB's SPILLING sort (engineered-wood has no external sort).
    /// </para>
    /// <para>
    /// <c>PROVIDER 'engineeredwooddelta'</c> ⇒ <b>native off</b>: pure engineered-wood, its own parquet codec on
    /// both sides. Keeps a single-dependency path for driver-level testing and for exercising the codec itself.
    /// </para>
    /// <para>
    /// An explicit ATTACH option always wins, in either direction — <c>PROVIDER 'delta', native_write false</c>
    /// and <c>PROVIDER 'engineeredwooddelta', native_read true</c> both do what they say. An ATTACH that names
    /// NO provider (the default backend) gets the conservative profile, since nothing asked for the hybrid.
    /// </para>
    /// </remarks>
    internal static (bool Read, bool Write) NativeDefaultsFor(string? requestedProvider) =>
        string.Equals(requestedProvider?.Trim(), "delta", System.StringComparison.OrdinalIgnoreCase)
            ? (true, true)
            : (false, false);

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

    // CATALOG-BOUND macros: bound into every discovered schema of an attached catalog, so they resolve as
    // db.<schema>.fab_*(…). Static because the declaration is a compile-time property of the provider, and
    // read by DeltaCatalog.GetMetadata(CatalogMacros) — never assembled into SQL, so a Delta catalog (which has
    // no SQL engine at all) can declare them, and declaring them touches no storage.
    //
    // Every body here is deliberately SELF-CONTAINED — pure expressions, or a query over a DuckDB built-in.
    // That is not decoration: DuckDB captures no search path when it expands a macro, so an unqualified table
    // reference would resolve against the CALLER's catalog/schema rather than this one. A body that must read
    // its own catalog's tables belongs in an ISqlTableFunction, which is handed the ATTACH alias.
    internal static readonly IReadOnlyList<CatalogMacroDefinition> DeclaredCatalogMacros = new[]
    {
        // Plain scalar. The case nothing else in the stack serves cheaply: a catalog-scoped SCALAR helper.
        // sqlgen (ISqlTableFunction) is table-valued only, and a catalog scalar function (ICatalogScalarFunction)
        // marshals every call across the ABI — this crosses NOTHING at runtime, the binder just expands it.
        new CatalogMacroDefinition("__all__", "fab_pct",
            "CREATE MACRO fab_pct(part, whole) AS "
            + "CASE WHEN whole IS NULL OR whole = 0 THEN NULL ELSE round(100.0 * part / whole, 2) END"),
        // Named parameter with a DEFAULT — proves the full CREATE MACRO grammar survives the catalog path, not
        // just the simple form (and per §1.3, a defaulted parameter also accepts a POSITIONAL argument).
        new CatalogMacroDefinition("__all__", "fab_clamp",
            "CREATE MACRO fab_clamp(v, lo := 0, hi := 100) AS least(greatest(v, lo), hi)"),
        // TABLE macro (AS TABLE …) — a different DuckDB catalog type (TABLE_MACRO_ENTRY) reached through the
        // TABLE_FUNCTION_ENTRY lookup, so it exercises the other half of the host's dispatch.
        new CatalogMacroDefinition("__all__", "fab_numbers",
            "CREATE MACRO fab_numbers(n) AS TABLE SELECT i AS n FROM range(n) t(i)"),
    };

    /// <summary>
    /// The provider's catalog macros. The declarations use the sentinel schema <c>__all__</c>, which
    /// <see cref="DeltaCatalog"/> expands to every schema it discovered — a Delta root's schema names are not
    /// known until ATTACH (they are folder names), so a static declaration cannot name them.
    /// </summary>
    public IEnumerable<CatalogMacroDefinition> CatalogMacros => DeclaredCatalogMacros;

    // Catalog-bound custom FUNCTIONS (as opposed to the macros above): real C# that runs per call, marshaled
    // over the ABI. Same `__all__` sentinel, same reason. Unlike the SQL-Server catalog these are not
    // discovered from a server — the provider declares them — so the kind-6 stream is built in memory
    // (see FunctionsMetadata).
    //
    // Deliberately NOT a static list: every function here exists BECAUSE it needs catalog context (the
    // attach root, and on OneLake the workspace/item + credential resolved at ATTACH). A static registry
    // would force that context into every call's arguments, which is precisely the ergonomics the
    // catalog-bound form buys us. Built per catalog by DeltaCatalog.BuildFunctionSet.

    // The connstr IS the folder root. Data-file IO is via DuckDB FS secrets (the opener). An azure SP secret on
    // a OneLake ATTACH additionally authenticates the Fabric REST API used to list tables (the glob bug
    // workaround) — carry its fields to the catalog as a credential marker on the root (mirrors the DAX provider).
    public string BuildConnectionString(
        string secretType, IReadOnlyDictionary<string, string> fields, string baseConnString)
    {
        // ANY abfss:// root, not just OneLake. The fields carry the STORAGE credential, which is what selects
        // the direct-SDK filesystem — and with it the only commit primitive that is actually atomic on ADLS
        // (duckdb-azure's ExclusiveCreate is a client-side existence check; measured losing 7 of 48 concurrent
        // commits, mostly silently). On a OneLake root the same fields additionally authenticate the Fabric
        // REST + Unity-Catalog endpoints used to enumerate tables.
        if (secretType.Equals("azure", System.StringComparison.OrdinalIgnoreCase)
            && AdlsPath.IsAdlsGen2(baseConnString))
        {
            return FabricLakehouse.AppendCredMarker(baseConnString, fields);
        }
        // An s3 secret on an s3:// ATTACH: carry its fields so the commit rename runs a REAL
        // conditional PUT through the AWS SDK (httpfs never passes If-None-Match — without this,
        // S3 catalogs are single-writer). Data IO stays on the host-FS/opener path.
        if (secretType.Equals("s3", System.StringComparison.OrdinalIgnoreCase)
            && baseConnString.TrimStart().StartsWith("s3://", System.StringComparison.OrdinalIgnoreCase))
        {
            return S3CommitCredential.AppendMarker(baseConnString, fields);
        }
        return baseConnString;
    }

    // The two-argument form cannot know which name was written, so it takes the conservative profile. Reached
    // only by a caller that predates the three-argument overload (the host always passes the name).
    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        new DeltaCatalog(connectionString, optionsJson, (false, false));

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson, string requestedProvider) =>
        new DeltaCatalog(connectionString, optionsJson, NativeDefaultsFor(requestedProvider));
}

/// <summary>An ATTACH'd Delta folder catalog. Lazy: holds the root path; all FS access happens during metadata
/// discovery / scan, using the active host-FS opener (<see cref="AmbientOpener"/>, set by the host before each
/// catalog metadata + scan + bulk-write call).</summary>
public sealed class DeltaCatalog : IBackendCatalog
{
    internal const string MainSchema = "main";
    // The stable row-tracking id surfaced as the DuckDB rowid for UPDATE/DELETE (a VIRTUAL column — not part
    // of the user schema). Matches EngineeredWood.DeltaLake.RowTracking.RowTrackingConfig.VirtualRowIdColumn.
    // OUR DuckDB-facing virtual rowid column. Deliberately NOT engineered-wood's
    // TransientRowAddress.ColumnName ("_ew_row_address"): the two were the same string until upstream
    // separated them, because EW's trailing column carried a snapshot-scoped ADDRESS under Spark's name for
    // the STABLE row id. This name is the one DuckDB binds (and the one _metadata.row_id means to Spark, which
    // is what our virtual columns surface); EW's is an internal encoding we decode on this side.
    internal const string RowIdColumn = "_metadata.row_id";
    // Transient rowid packing. We do NOT define the split: engineered-wood's TransientRowAddress owns it, and
    // we must agree with it rather than merely match a literal — the codec read path renames EW's
    // `_ew_row_address` to RowIdColumn and passes ITS packed value straight through to DuckDB, so decoding
    // with our own copy of "40" would silently mis-read if the split ever moved. Our own pending-file
    // ordinals (PendingOrdinalBase and up) live in the same space for the same reason.
    private readonly string _root; // normalized (forward slashes), no trailing slash
    // For a OneLake root: the Fabric REST API credential (from the ATTACH'd azure SP secret) used to list
    // tables (and, for a schema-enabled lakehouse, an Entra SQL token). Null for local/S3/ADLS (glob discovery)
    // or when no secret was supplied.
    private readonly Azure.Core.TokenCredential? _fabricCredential;
    // For an abfss:// root ATTACHed with an azure SECRET: the STORAGE credential (Entra token OR shared key).
    // Distinct from _fabricCredential above, which is the Entra-only REST/Unity-Catalog credential and is null
    // for a shared-key account. Non-null => IO takes the direct-SDK AdlsGen2TableFileSystem, whose commit
    // rename is a real conditional PUT; null => the host-FS path, where the commit guard does NOT hold
    // (measured — see WarnIfUnguardedRemoteWrite).
    private readonly AdlsCredential? _adlsCredential;
    // For an s3:// root ATTACHed with an s3 SECRET: the commit-rename conditional-PUT credential
    // (multi-writer safety). Null => host-FS rename (single-writer, the documented httpfs caveat).
    private readonly S3CommitCredential? _s3Credential;
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
    // write _change_data files and fabricator_delta_changes(...) returns a correct row-level change feed.
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
    private readonly long? _defaultRowGroupSizeBytes;
    private readonly long? _defaultRowGroupsPerFile;
    private readonly long? _defaultDictionarySizeLimit;
    private readonly long? _defaultFileSizeBytes;
    private readonly DeltaParquetVersion _defaultParquetVersion;
    private readonly int? _defaultCompressionLevel;
    private readonly double? _defaultBloomFilterFpp;
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
    // ATTACH option `column_mapping 'name'|'id'|'none'` (default NAME — Fabric-T-SQL-endpoint-compatible;
    // the endpoint rejects id-mode tables): tables CREATED in this catalog enable Delta
    // column mapping — physical column names (col-<guid>) decoupled from logical names, so a later RENAME/DROP
    // COLUMN is a metadata-only commit. engineered-wood's CreateAsync assigns the physical names + bumps the
    // protocol (reader v2 / writer v5). Our read (physical→logical alias / EW RenameByFieldId) + write (EW codec
    // RenameToPhysical / SetParquetFieldIds) already handle mapped tables. Only affects table CREATION.
    private readonly EngineeredWood.DeltaLake.Schema.ColumnMappingMode _columnMappingMode;

    // ATTACH option `pushdown_filters 'none'|'static'|'dynamic'|'all'` (duckdb-delta parity) — how much of
    // the query's filters this catalog's scans consume:
    //   none    — no pushdown at all (pure fallback: DuckDB filters everything above the scan).
    //   static  — the bind-time filter is pushed BEST-EFFORT for engineered-wood file/row-group/bloom
    //             pruning (superset-safe; DuckDB re-applies). The codec default.
    //   dynamic/all — EXACT mode: the scan declares filter_pushdown=true, DuckDB hands the live
    //             TableFilterSet (static + dynamic JOIN filters) and ERASES the statics from the plan; the
    //             host renders it 1:1 to SQL (spec.native_filter). native_read applies it inside
    //             read_parquet; the codec path applies it EXACTLY per batch via HostBatchFilter. 'dynamic'
    //             is accepted as an alias of 'all' on this provider (the erasure contract is all-or-nothing).
    //             The native_read default. Downgrades to 'static' when host queries are unavailable.
    private enum PushdownMode { None, Static, Exact }
    private readonly PushdownMode _pushdownMode;

    // Explicit-transaction APPEND buffering (see DeltaTxnBuffer): plain appends park here per DuckDB
    // transaction and flush as ONE Delta commit per table at CommitTransaction; RollbackTransaction
    // discards. In autocommit the flush fires at statement end = today's per-statement commit.
    private readonly DeltaTxnBuffer _txnBuffer = new();

    // Append-only transactions (slice 1): any non-append operation on a table that already has buffered
    // inserts in the CURRENT transaction is rejected — its snapshot-coupled actions (rowid DML, replace,
    // DDL, maintenance) would not see / would misorder against the pending rows.
    private void ThrowIfPendingAppends(string tablePath, string operation)
    {
        if (_txnBuffer.HasPending(AmbientTransaction.Current, tablePath))
        {
            throw new System.NotSupportedException(
                $"delta: {operation} on a table with uncommitted buffered changes in this transaction is "
                + "not supported — COMMIT first.");
        }
    }

    private static readonly Microsoft.Extensions.Logging.ILogger _log = FabricatorLog.CreateLogger("Fabricator.Delta");

    public DeltaCatalog(string root) : this(root, "{}") { }

    public DeltaCatalog(string root, string? optionsJson) : this(root, optionsJson, (false, false)) { }

    /// <param name="nativeDefaults">What <c>native_read</c>/<c>native_write</c> default to when the ATTACH does
    /// not say — chosen by the PROVIDER NAME the user wrote (see <see cref="DeltaBackend.NativeDefaultsFor"/>).
    /// An explicit ATTACH option overrides either way.</param>
    public DeltaCatalog(string root, string? optionsJson, (bool Read, bool Write) nativeDefaults)
    {
        var (clean, credential, storage) = FabricLakehouse.Extract(root);
        (clean, _s3Credential) = S3CommitCredential.Extract(clean);
        _root = Normalize(clean).TrimEnd('/');
        _fabricCredential = credential;
        _adlsCredential = storage;
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
        _defaultRowGroupSizeBytes = ParseLongOption(optionsJson, "row_group_size_bytes");
        _defaultRowGroupsPerFile = ParseLongOption(optionsJson, "row_groups_per_file");
        _defaultDictionarySizeLimit = ParseLongOption(optionsJson, "dictionary_size_limit");
        _defaultFileSizeBytes = ParseLongOption(optionsJson, "file_size_bytes");
        _defaultCompressionLevel = (int?)ParseLongOption(optionsJson, "compression_level");
        _defaultBloomFilterFpp = ParseDoubleOption(optionsJson, "bloom_filter_false_positive_ratio");
        _defaultParquetVersion = ParseParquetVersion(ParseStringOption(optionsJson, "parquet_version"))
                                 ?? DeltaParquetVersion.Default;
        _mergeSchemaOnWrite = ParseBoolOption(optionsJson, "merge_schema");
        _nativeRead = ParseBoolOption(optionsJson, "native_read", nativeDefaults.Read);
        _nativeWrite = ParseBoolOption(optionsJson, "native_write", nativeDefaults.Write);
        _columnMappingMode = ParseColumnMappingOption(optionsJson);
        _pushdownMode = ParsePushdownFiltersOption(optionsJson, _nativeRead);
        _copyDisposition = ParseStringOption(optionsJson, "copy_disposition");
        var isolation = ParseStringOption(optionsJson, "isolation_level");
        // DEFAULT = serializable (2026-08-01, behaviour-breaking). It used to be write_serializable on the
        // belief that this matched Spark; MEASURED FALSE — Fabric Spark commits at Serializable, so the old
        // default made US the weaker writer on any table that does not declare a level, and which engine
        // wrote last silently decided the guarantee. Aligning removes that. Explicit options still win, and a
        // table's own delta.isolationLevel still overrides the catalog (PendingSerializable).
        _serializable = isolation?.Replace("_", "").ToLowerInvariant() switch
        {
            null or "" or "serializable" => true,
            "writeserializable" => false,
            _ => throw new System.ArgumentException(
                $"delta: unknown isolation_level '{isolation}' — expected 'serializable' or 'write_serializable'."),
        };
        WarnIfUnguardedRemoteWrite(ParseStringOption(optionsJson, "access_mode"));
    }

    /// <summary>
    /// Warns when a remote root — <c>s3://</c> or <c>abfss://</c> — is attached READ_WRITE with no NAMED
    /// secret, the configuration that loses concurrent commits SILENTLY. Both are MEASURED, not inferred:
    /// s3 at 6 writers × 8 commits landed 8 of 48 with zero errors (§8.3); abfss at the same shape landed 41
    /// of 48, six of the seven losses silent and one surfacing as an unrelated-looking Azure
    /// <c>InvalidFlushPosition</c> (§8.4). Naming the secret ⇒ 48/48 on both.
    ///
    /// <para><b>Why a warning is worth its noise here.</b> The unsafe configuration authenticates, writes,
    /// reads and passes every single-writer test — the host filesystem (httpfs / duckdb-azure) uses the
    /// ambient secret for DATA IO, and only the COMMIT guard is missing, because the credential marker rides
    /// on the secret the ATTACH NAMES. So nothing about the setup looks wrong, the remedy is one option, and
    /// the failure is invisible. That asymmetry — silent, severe, one-line fix — is the case a warning exists
    /// for.</para>
    ///
    /// <para>The two backends fail for DIFFERENT reasons and it is worth keeping both in mind: httpfs cannot
    /// send <c>If-None-Match</c> at all, while duckdb-azure's <c>ExclusiveCreate</c> looks correct
    /// single-threadedly (<c>fabricator_fs_write_probe</c> reports it throwing on an existing file) and is a
    /// client-side existence CHECK, so it races. A capability probe cannot distinguish those; only a
    /// concurrent run can.</para>
    ///
    /// <para>Gated on READ_WRITE specifically, not on "not read-only": a remote root under
    /// <c>AUTOMATIC</c> may be bumped to read-only by DuckDB, and warning about a catalog that will never
    /// write is how a real warning gets trained away. Asking for write access is the deliberate act.</para>
    /// </summary>
    private void WarnIfUnguardedRemoteWrite(string? accessMode)
    {
        if (!string.Equals(accessMode, "read_write", System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (_s3Credential is null && S3CommitFileSystem.IsS3(_root))
        {
            _log.LogWarning(
                "delta attach {Root}: s3 root attached READ_WRITE with no named SECRET — the commit guard is "
                + "OFF, so concurrent writers LOSE COMMITS SILENTLY (httpfs cannot send If-None-Match; a "
                + "measured 6-writer run landed 8 of 48 commits with no error). Single-writer use is unaffected. "
                + "Add SECRET <name> to the ATTACH to route commits through the conditional-PUT path — an s3 "
                + "secret merely being in scope is NOT enough, since only the NAMED one reaches the commit path.",
                _root);
        }
        else if (_adlsCredential is null && AdlsPath.IsAdlsGen2(_root))
        {
            _log.LogWarning(
                "delta attach {Root}: abfss root attached READ_WRITE with no named SECRET — the commit guard is "
                + "OFF, so concurrent writers LOSE COMMITS SILENTLY (duckdb-azure's ExclusiveCreate is a "
                + "client-side existence check, not a conditional PUT; a measured 6-writer run landed 41 of 48 "
                + "commits, six losses with no error). Table RENAME and DROP are also unavailable on that path "
                + "(MoveFile/RemoveDirectory are unimplemented there). Single-writer append-only use is "
                + "unaffected. Add SECRET <name> to the ATTACH to route IO through the Azure DataLake SDK — an "
                + "azure secret merely being in scope is NOT enough, since only the NAMED one reaches us.",
                _root);
        }
    }

    // ATTACH option `isolation_level 'write_serializable'|'serializable'` (Spark's delta.isolationLevel):
    // how explicit transactions treat CONCURRENT BLIND APPENDS at COMMIT. write_serializable (the default)
    // lets the COMMIT logically reorder before them (they pass the rebase even when
    // they match the transaction's reads); serializable makes commit order the logical order — a concurrent
    // append matching the transaction's read predicates conflict-aborts. All other checks (metadata /
    // protocol / delete-delete / delete-read) are identical at both levels.
    //
    // ⚠ write_serializable is DATABRICKS' default, NOT Spark's — this comment used to claim "Spark's default
    // too" and that is MEASURED FALSE (2026-07-31, Fabric Spark 4.1.1): Fabric/OSS Spark records Serializable
    // for its own commits, and its DDL validator REJECTS the value outright ("delta.isolationLevel must be
    // Serializable") at CREATE and at ALTER SET TBLPROPERTIES. So on a shared table with the property ABSENT,
    // we apply WriteSerializable while Fabric Spark applies Serializable — we are the more permissive of the
    // two. ATTACH with isolation_level 'serializable' to match Fabric Spark. (A property we STAMP is still
    // honored by Spark on read/write — it just cannot set it itself.) docs/delta-transactions.md §10.6.
    //
    // NOTE: this ATTACH option is now only the CREATE-TIME DEFAULT semantics reference. The EFFECTIVE
    // isolation for an EXISTING table's conflict check is the table's OWN delta.isolationLevel property
    // (see PendingSerializable) — so our writer conforms to the guarantee the table advertises, uniform with
    // Spark/other writers (the whole reason Delta makes isolation a TABLE property). Change a table's level
    // with fabricator_delta_set_tblproperties. Autocommit single-statement DML still uses this catalog default
    // for its row-level-retry resilience knob (a documented minor divergence; multi-statement serializability
    // — where it matters — is honored per-table below).
    private readonly bool _serializable;

    /// <summary>
    /// The effective isolation for <paramref name="path"/>: the TABLE's <c>delta.isolationLevel</c> property
    /// WINS, and the catalog's ATTACH <c>isolation_level</c> is the DEFAULT used only when the table declares
    /// nothing.
    /// </summary>
    /// <remarks>
    /// That precedence is the whole point of Delta making isolation a TABLE property: the guarantee has to
    /// hold whoever attaches the table and with whatever options, or it is worthless as a cross-engine
    /// contract. This is the ONE place the rule is expressed — every isolation decision goes through here, so
    /// there is no path on which an attach-time flag can outrank a table's declaration.
    /// </remarks>
    private bool EffectiveSerializable(string path)
        => EffectiveSerializable(DeltaReader.GetTableProperties(Opener(), path));

    /// <summary>As above, against an already-read configuration (one table open serving several properties).
    /// The rule itself lives in <see cref="DeltaReader.EffectiveSerializable"/> so that the merge-on-read UPDATE
    /// — which resolves it from the configuration it already holds, inside the reader — cannot express it
    /// differently. Property absent => the catalog's ATTACH <c>isolation_level</c> default.</summary>
    private bool EffectiveSerializable(IReadOnlyDictionary<string, string> config)
        => DeltaReader.EffectiveSerializable(config, _serializable);

    // As EffectiveSerializable, read once and cached on the buffer (isolation is stable within a
    // transaction). Used by the flush's OCC check + row-level relaxation.
    private bool PendingSerializable(DeltaTxnBuffer.PendingAppends pending, string path)
    {
        if (pending.Serializable is { } cached)
        {
            return cached;
        }
        bool ser = EffectiveSerializable(path);
        pending.Serializable = ser;
        return ser;
    }

    // COPY (FORMAT delta) MODE 'error'|'ignore' — set only on the COPY's TRANSIENT catalog (which serves
    // exactly one statement, so a per-statement disposition may ride the catalog options): a create-shaped
    // bulk onto an EXISTING target fails ('error', Spark's default save mode) or silently no-ops ('ignore').
    private readonly string? _copyDisposition;

    /// <summary>Returns the host-FS opener for this thread and, in the same breath, publishes this catalog's
    /// storage credential to <see cref="AmbientAdlsCredential"/> so the filesystem factory
    /// (<see cref="TableFileSystems.Create"/>) picks the direct-SDK <see cref="AdlsGen2TableFileSystem"/> for
    /// <c>abfss://</c> roots (bypassing duckdb-azure). Without a credential — no secret NAMED on the ATTACH —
    /// it is null and the factory falls back to <see cref="DuckDbTableFileSystem"/>. Setting it every time also
    /// clears any stale credential left on a reused execution thread by another catalog. The bulk write path runs
    /// on a background thread, so <c>BulkSession</c> re-establishes both ambients there.</summary>
    private nint Opener()
    {
        AmbientAdlsCredential.Current = _adlsCredential;
        AmbientS3Credential.Current = _s3Credential;
        return AmbientOpener.Current;
    }

    /// <summary>True when DIRECTORY operations (recursive DROP, table RENAME) go through the ADLS Gen2 DFS SDK
    /// instead of the host filesystem. Requires an <c>abfss://</c> root AND a storage credential.</summary>
    /// <remarks>
    /// Not an optimization — DuckDB's azure FileSystem implements NEITHER of these
    /// (<c>AzureDfsStorageFileSystem: MoveFile is not implemented!</c>, and no <c>RemoveDirectory</c> at all),
    /// so without this a plain ADLS catalog cannot rename a table, which is what a dbt table model does on
    /// every re-deploy. Previously gated on <c>IsOneLake</c>, which is why only Fabric had working DDL.
    /// </remarks>
    private bool UseAdlsDirectoryOps => _adlsCredential is not null && AdlsPath.IsAdlsGen2(_root);

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
    /// <c>'name'</c> / <c>'none'</c>, case-insensitive; default <c>name</c>). <c>'id'</c> maps columns by the Delta
    /// <c>delta.columnMapping.id</c> (== the parquet <c>field_id</c>) — the standard identity, stable across a
    /// RENAME, and what the native reader resolves via <c>parquet_schema</c> (see
    /// <see cref="DeltaNativeReader"/>). <c>'name'</c> maps by <c>physicalName</c> (col-&lt;guid&gt;). READING an
    /// external (Spark/Databricks) id- or name-mode table works regardless of this option (the mode is a table
    /// property). Throws on any other value.</summary>
    private static EngineeredWood.DeltaLake.Schema.ColumnMappingMode ParseColumnMappingOption(string? optionsJson)
    {
        var s = ParseStringOption(optionsJson, "column_mapping");
        // Default (option absent) = NAME mode: a later RENAME/DROP COLUMN is a metadata-only commit (no
        // rewrite) out of the box, AND the table stays readable by the Fabric T-SQL endpoint — which REJECTS
        // id-mode tables outright ("Unsupported column mapping mode: id"; validated live 2026-07-06) while
        // Spark/kernel/DuckDB read both modes. Id mode remains opt-in (`column_mapping 'id'`) for its
        // field-id-based resolution; `column_mapping 'none'` gives a plain table (logical == physical name,
        // no protocol bump — e.g. a consumer that can't read a writer-v7 table).
        if (string.IsNullOrWhiteSpace(s)) { return EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name; }
        return s.Trim().ToLowerInvariant() switch
        {
            "none" or "" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None,
            "name" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name,
            "id" => EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Id,
            _ => throw new System.ArgumentException(
                $"column_mapping: unknown mode '{s}' (expected 'id', 'name', or 'none')."),
        };
    }

    private static PushdownMode ParsePushdownFiltersOption(string? optionsJson, bool nativeRead)
    {
        var s = ParseStringOption(optionsJson, "pushdown_filters");
        PushdownMode mode;
        if (string.IsNullOrWhiteSpace(s))
        {
            // Defaults preserve prior behavior: exact under native_read, best-effort static otherwise.
            mode = nativeRead ? PushdownMode.Exact : PushdownMode.Static;
        }
        else
        {
            mode = s.Trim().ToLowerInvariant() switch
            {
                "none" => PushdownMode.None,
                "static" => PushdownMode.Static,
                "dynamic" or "all" => PushdownMode.Exact,
                _ => throw new System.ArgumentException(
                    $"pushdown_filters: unknown mode '{s}' (expected 'none', 'static', 'dynamic', or 'all')."),
            };
        }
        // Exact application needs the host engine (read_parquet WHERE / HostBatchFilter).
        if (mode == PushdownMode.Exact && !Host.CanQuery)
        {
            mode = PushdownMode.Static;
        }
        return mode;
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

    /// <summary>Refuses a write option the CONFIGURED ENGINE cannot honour. ⚠ The engine must be read from
    /// the catalog's own <c>native_write</c>, NOT inferred from whether an <c>IDataFileWriter</c> was passed:
    /// a native CTAS writes its files through DuckDB's COPY on a separate route and still opens
    /// engineered-wood with no writer, so that inference refuses valid options on the DEFAULT provider
    /// (measured — it did exactly that). Refusing beats accepting-and-dropping: a write option that silently
    /// does nothing leaves the user believing the file was written as asked.</summary>
    private DeltaWriteSpec? ValidateSpecForEngine(DeltaWriteSpec? spec)
    {
        if (spec is null)
        {
            return spec;
        }
        // ⚠ FILE-ROTATING OPTIONS ARE REFUSED ON EVERY PATH, and each path has its OWN measured reason —
        // this is not one guess applied three times:
        //   • native, NOT partitioned — DuckDB treats the target as a DIRECTORY and writes
        //     `<name>.parquet/data_0.parquet` into it while the Delta `add` records `<name>.parquet`. The
        //     commit SUCCEEDS and the table's data file is a directory: SILENT CORRUPTION.
        //   • native, PARTITIONED — DuckDB refuses outright: "Can't combine file rotation (e.g.,
        //     ROW_GROUPS_PER_FILE) and PARTITION_BY for COPY". (Worth stating because the partitioned write
        //     ALREADY targets a directory and ReadFileStats already registers one `add` per RETURN_STATS row,
        //     so our side would have coped — the limit is upstream's, not ours.)
        //   • engineered-wood codec — no equivalent at all.
        // ⇒ there is no path on which these can be honoured today. Refusing beats accepting-and-dropping: a
        // write option that silently does nothing leaves the user believing the file was written as asked.
        if (spec.RowGroupsPerFile is not null || spec.FileSizeBytes is not null)
        {
            throw new System.ArgumentException(
                (spec.RowGroupsPerFile is not null ? "parquet_row_groups_per_file" : "parquet_file_size_bytes")
                + " is not supported: DuckDB cannot rotate files together with PARTITION_BY, and without "
                + "partitioning it writes a DIRECTORY where a Delta `add` action must name one file. Use "
                + "parquet_row_group_size / parquet_row_group_size_bytes to control row-group size instead.");
        }
        if (_nativeWrite)
        {
            return spec;
        }
        // Engine-specific: no engineered-wood equivalent, so refuse rather than accept-and-drop — a write
        // option that silently does nothing leaves the user believing the file was written as asked.
        if (spec.DictionarySizeLimit is not null)
        {
            throw new System.ArgumentException(
                "parquet_dictionary_size_limit is only supported with native_write (DuckDB's parquet "
                + "writer); this catalog uses the engineered-wood codec, which has no equivalent (its "
                + "DictionaryPageSizeLimit is a BYTE limit, not a distinct-value cap). Attach with "
                + "PROVIDER 'delta' (native_write is its default), or drop the option.");
        }
        return spec;
    }

    /// <summary>Reads a DOUBLE option (the bloom false-positive rate). ⚠ Parsed with the invariant culture
    /// like every other option here — a machine-readable JSON/ATTACH value must not depend on the host's
    /// decimal separator.</summary>
    private static double? ParseDoubleOption(string? optionsJson, string key)
    {
        var s = ParseStringOption(optionsJson, key);
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var v)
               && v > 0 && v < 1
            ? v
            : (double?)null;
    }

    private static long? ParseLongOption(string? optionsJson, string key)
    {
        var s = ParseStringOption(optionsJson, key);
        return long.TryParse(s, out var v) ? v : (long?)null;
    }

    /// <summary>Parses a PARQUET_VERSION option value; null when absent so the caller keeps its fallback.
    /// ⚠ An unrecognised value THROWS rather than falling back to a default — a silently ignored write option
    /// is the failure mode this whole surface exists to avoid.</summary>
    private static DeltaParquetVersion? ParseParquetVersion(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant() switch
            {
                "v1" or "1" => DeltaParquetVersion.V1,
                "v2" or "2" => DeltaParquetVersion.V2,
                _ => throw new System.ArgumentException(
                    $"parquet_version: unknown version '{value}' (expected 'V1' or 'V2')."),
            };

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

    /// <summary>Resolves the effective write tuning for one write against ONE TABLE. Precedence, lowest first:
    /// <b>ATTACH defaults &lt; <c>SET delta_write_options</c> &lt; the table's persisted
    /// <c>fabricator.parquet.*</c> properties &lt; the statement's <c>WITH</c></b> (the last applied by
    /// <c>ApplyWithOptions</c> on the returned spec). Partition columns come from
    /// <paramref name="nativePartitionColumns"/> (a native <c>PARTITIONED BY</c> clause) when present, else the
    /// setting's <c>partition_by</c>. Returns null only when nothing is specified (=> engineered-wood defaults).</summary>
    /// <param name="tablePath">The table being written, or null where there genuinely is no table yet (a CREATE
    /// resolving the spec that will CREATE it). ⚠ REQUIRED — not optional — deliberately: this method used to be
    /// table-AGNOSTIC, and the persisted layer is invisible from its result, so a call site that forgot to say
    /// which table would silently write at the wrong settings. Making the compiler ask the question is the whole
    /// mechanism; the same omission on the rewrite paths is what let OPTIMIZE undo its own configuration for
    /// months.</param>
    private DeltaWriteSpec? ResolveWriteSpec(IReadOnlyList<string>? nativePartitionColumns, string? schemaModeArg,
                                             string? tablePath)
    {
        var sessionJson = ProviderSettingsStore.Instance.GetString(
            DeltaBackendName, DeltaBackend.WriteOptionsSetting);

        string? compression = _defaultCompression;
        int? rowGroup = _defaultRowGroupSize;
        IReadOnlyList<string>? bloom = _defaultBloomColumns;
        // The further parquet knobs, same precedence chain as the three above: ATTACH default <
        // delta_write_options < the per-table WITH (applied by ApplyWithOptions).
        long? rowGroupBytes = _defaultRowGroupSizeBytes;
        long? rowGroupsPerFile = _defaultRowGroupsPerFile;
        long? dictLimit = _defaultDictionarySizeLimit;
        long? fileBytes = _defaultFileSizeBytes;
        var parquetVersion = _defaultParquetVersion;
        int? compressionLevel = _defaultCompressionLevel;
        double? bloomFpp = _defaultBloomFilterFpp;
        IReadOnlyList<string>? settingPartition = null;
        IReadOnlyDictionary<string, string>? replaceWhere = null;
        // schema_mode precedence: per-catalog merge_schema default < delta_write_options (merge_schema / schema_mode)
        // < the per-statement COPY SCHEMA_MODE arg.
        var schemaMode = _mergeSchemaOnWrite ? DeltaSchemaMode.Merge : DeltaSchemaMode.None;
        // (the engine check runs on the assembled spec at the end — see ValidateSpecForEngine)

        if (!string.IsNullOrWhiteSpace(sessionJson))
        {
            compression = ParseStringOption(sessionJson, "compression") ?? compression;
            rowGroup = ParseIntOption(sessionJson, "row_group_size") ?? rowGroup;
            rowGroupBytes = ParseLongOption(sessionJson, "row_group_size_bytes") ?? rowGroupBytes;
            rowGroupsPerFile = ParseLongOption(sessionJson, "row_groups_per_file") ?? rowGroupsPerFile;
            dictLimit = ParseLongOption(sessionJson, "dictionary_size_limit") ?? dictLimit;
            fileBytes = ParseLongOption(sessionJson, "file_size_bytes") ?? fileBytes;
            compressionLevel = (int?)ParseLongOption(sessionJson, "compression_level") ?? compressionLevel;
            bloomFpp = ParseDoubleOption(sessionJson, "bloom_filter_false_positive_ratio") ?? bloomFpp;
            parquetVersion = ParseParquetVersion(ParseStringOption(sessionJson, "parquet_version"))
                             ?? parquetVersion;
            bloom = ParseListOption(sessionJson, "bloom_filter_columns") ?? bloom;
            settingPartition = ParseListOption(sessionJson, "partition_by");
            replaceWhere = ParseMapOption(sessionJson, "replace_where"); // partition col -> value (per-statement)
            if (ParseStringOption(sessionJson, "schema_mode") is { } sm) { schemaMode = ParseSchemaMode(sm); }
            else if (ParseBoolOption(sessionJson, "merge_schema")) { schemaMode = DeltaSchemaMode.Merge; }
        }
        if (!string.IsNullOrWhiteSpace(schemaModeArg)) { schemaMode = ParseSchemaMode(schemaModeArg); }

        // ── The PERSISTED layer: the table's own fabricator.parquet.* declaration, which OUTRANKS the session
        // setting. That ordering is deliberate rather than incidental: the property is a property OF THE TABLE,
        // so a stray `SET delta_write_options` in someone's session must not silently change a table's storage
        // format; the per-statement WITH stays the escape hatch above it. Only the FILE-FORMAT knobs are read —
        // partitioning/replace_where/schema_mode are statement semantics and are never persisted.
        if (tablePath is not null)
        {
            var persisted = ParquetTuning.Parse(TableConfig(Opener(), tablePath));
            if (!persisted.IsEmpty)
            {
                // ⚠ APPLY WHAT FITS, IGNORE THE REST — do NOT route this through ValidateSpecForEngine, which
                // THROWS. A declaration made once by whoever created the table may name a knob THIS engine
                // cannot honour, and failing would make the table unwritable by the codec engine because
                // someone once set a native-only option. (An unparseable value is different and still throws —
                // ParquetTuning.Parse raises it above.) See DeltaParquetProperties for the full distinction.
                var dropped = new List<string>();
                compression = persisted.Compression ?? compression;
                rowGroup = persisted.RowGroupSize ?? rowGroup;
                rowGroupBytes = persisted.RowGroupSizeBytes ?? rowGroupBytes;
                parquetVersion = ParseParquetVersion(persisted.ParquetVersion) ?? parquetVersion;
                bloom = persisted.BloomFilterColumns ?? bloom;
                compressionLevel = persisted.CompressionLevel ?? compressionLevel;
                bloomFpp = persisted.BloomFilterFpp ?? bloomFpp;
                // Native-only, and unhonourable by the codec: DuckDB's DICTIONARY_SIZE_LIMIT is a cap on
                // DISTINCT VALUES while engineered-wood's DictionaryPageSizeLimit is BYTES, so there is nothing
                // to map it onto.
                if (persisted.DictionarySizeLimit is { } dsl)
                {
                    if (_nativeWrite) { dictLimit = dsl; }
                    else { dropped.Add(ParquetTuning.DictionarySizeLimitKey); }
                }
                // The two file-ROTATING knobs cannot be honoured on ANY path today (DuckDB refuses them with
                // PARTITION_BY, and without it writes a directory where a Delta `add` must name one file), so a
                // persisted one is always dropped. Persisting it is still right: the declaration outlives the
                // limitation, and when upstream lifts it the property starts being honoured with no migration.
                if (persisted.RowGroupsPerFile is not null) { dropped.Add(ParquetTuning.RowGroupsPerFileKey); }
                if (persisted.FileSizeBytes is not null) { dropped.Add(ParquetTuning.FileSizeBytesKey); }
                if (dropped.Count > 0)
                {
                    // Debug, not Warning: ignoring here is CORRECT rather than degraded. It is still logged
                    // because the choice is otherwise invisible from SQL.
                    _log.LogDebug(
                        "delta write {Path}: table declares {Count} parquet propert(ies) this engine cannot "
                        + "honour ({Keys}) — ignored (engine: {Engine})",
                        tablePath, dropped.Count, string.Join(", ", dropped),
                        _nativeWrite ? "native_write" : "engineered-wood codec");
                }
            }
        }

        var partition = nativePartitionColumns is { Count: > 0 } ? nativePartitionColumns : settingPartition;
        var codec = ParseCompression(compression);

        if (codec is null && rowGroup is null && bloom is null && (partition is null || partition.Count == 0)
            && (replaceWhere is null || replaceWhere.Count == 0) && schemaMode == DeltaSchemaMode.None
            && rowGroupBytes is null && rowGroupsPerFile is null && dictLimit is null && fileBytes is null
            && parquetVersion == DeltaParquetVersion.Default && compressionLevel is null
            && bloomFpp is null)
        {
            return null;
        }
        return ValidateSpecForEngine(new DeltaWriteSpec(codec, rowGroup, bloom, partition, replaceWhere,
                                                        schemaMode)
        {
            RowGroupSizeBytes = rowGroupBytes,
            RowGroupsPerFile = rowGroupsPerFile,
            DictionarySizeLimit = dictLimit,
            FileSizeBytes = fileBytes,
            ParquetVersion = parquetVersion,
            CompressionLevel = compressionLevel,
            BloomFilterFpp = bloomFpp,
        });
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
        // CatalogSchemaNames, not SchemaNames: the advertised set includes the `fabric` function namespace on a
        // OneLake root. See CatalogSchemaNames for why the two lists must stay separate.
        MetadataKind.Schemas => SingleColumn("schema_name", CatalogSchemaNames()),
        MetadataKind.Tables => DiscoverTables(),
        // Columns = a zero-row stream whose SCHEMA describes the table's columns (engineered-wood's Delta schema).
        MetadataKind.Columns => new InMemoryArrayStream(
            ColumnsSchema(TablePath(schema!, table!)), System.Array.Empty<RecordBatch>()),
        // RowId: always surface the virtual _metadata.row_id — a TRANSIENT (file, position) rowid computed at
        // scan time (no row-tracking feature needed; works on ANY Delta table). Enables UPDATE/DELETE
        // (rowid-based, mirrors the SQL Server backend); DELETE is copy-on-write (plain add/remove).
        MetadataKind.RowId => SingleColumn("name", new[] { RowIdColumn }),
        // Provider virtual columns: the STABLE row-tracking id + commit version as queryable-by-name virtual
        // columns (__delta_row_id / __delta_row_commit_version — the Delta materialized-column names; excluded
        // from SELECT *). native_read + delta.enableRowTracking tables only — the native reader derives them
        // per file (COALESCE(materialized, baseRowId + file_row_number) / defaultRowCommitVersion).
        MetadataKind.VirtualColumns => VirtualColumnsStream(TablePath(schema!, table!)),
        // Snapshots/history (fabricator_delta_snapshots): arg1=schema, arg2=table. Schema is required on a
        // schema-enabled lakehouse; defaults to "main" on a flat catalog.
        MetadataKind.Snapshots => SnapshotsStream(schema, table),
        MetadataKind.TxnVersion => TxnVersionStream(schema, table),
        MetadataKind.SetTxnVersion => SetTxnVersionStream(schema, table),
        // fabricator_delta_tblproperties / _set_tblproperties: read / set the table's delta.* properties.
        MetadataKind.TblProperties => TblPropertiesStream(schema),
        MetadataKind.SetTblProperties => SetTblPropertiesStream(schema, table),
        // Change Data Feed (fabricator_delta_changes): arg1 = 'schema.table' ref, arg2 = "from:to" (to empty => latest).
        MetadataKind.Changes => ChangesStream(schema, table),
        // Capability profile (property, value). `exact_filter_pushdown` = whether the host may set
        // filter_pushdown=true on this catalog's scans — governed by the pushdown_filters mode: EXACT mode
        // applies the erased TableFilterSet 1:1 (read_parquet WHERE under native_read; HostBatchFilter per
        // batch on the codec path); None/Static keep filter_pushdown=false so DuckDB re-applies everything.
        MetadataKind.ServerInfo => TwoColumn(
            "property", new[] { "exact_filter_pushdown" },
            "value", new[] { _pushdownMode == PushdownMode.Exact ? "true" : "false" }),
        // Provider-declared CATALOG-BOUND macros (schema, name, create_sql), bound by the host into this
        // catalog's schemas as db.schema.m(...). Local declarations — nothing here touches storage, which is
        // exactly why they ride their own metadata kind instead of a SQL discovery stream.
        MetadataKind.CatalogMacros => CatalogMacroMetadata.Stream(ExpandCatalogMacroSchemas()),
        // Provider-declared CATALOG-BOUND custom functions (schema_name, name, kind). Built IN MEMORY —
        // unlike SqlServerCatalog, this provider has no SQL engine to assemble a discovery query with, and
        // nothing here is discovered anyway: the set is what the provider declares. `__all__` expands across
        // discovered schemas (lazily — an empty set costs no schema enumeration, which on OneLake is I/O).
        MetadataKind.Functions => FunctionsMetadata.Stream(Functions.Declarations(SchemaNames)),
        // No row-count/NDV stats surfaced.
        _ => EmptyStringTable("name"),
    };

    // rowTracking flag per table path, filled by ColumnsSchema (the column fetch that ALWAYS precedes the
    // virtual-columns fetch in the host's entry materialization) — so VirtualColumnsStream normally costs no
    // extra _delta_log read (the OneLake enumeration concern).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _rowTrackingByPath = new();

    // The Columns metadata schema: a buffered transaction's pending (CREATE/ALTER) shape wins; otherwise one
    // table open that also caches the row-tracking flag for VirtualColumnsStream.
    private Schema ColumnsSchema(string path)
    {
        if (_txnBuffer.Get(AmbientTransaction.Current, path)?.PendingArrowSchema is { } pendingSchema)
        {
            return pendingSchema;
        }
        Schema schema;
        bool rowTracking;
        try
        {
            schema = DeltaReader.GetSchemaAndRowTracking(Opener(), path, out rowTracking);
        }
        catch (System.Exception ex)
        {
            // CLASSIFY the failure rather than let the host assume absence. It converts any column-fetch
            // failure into "the table does not exist" — dropping the entry AND removing the name from
            // enumeration — which is right after an out-of-band DROP and catastrophic otherwise: an
            // incomplete log, an expired credential or a brief outage would make a table whose data is
            // entirely intact disappear from the catalog. Absence is ESTABLISHED here (no commit in
            // _delta_log, the engine's own definition); anything else keeps its real error, which for a
            // holed log is engineered-wood naming the exact version it could not cover.
            if (DeltaReader.TableExists(Opener(), path))
            {
                throw;
            }
            throw new ObjectNotFoundException("table", path, ex);
        }
        _rowTrackingByPath[path] = rowTracking;
        return schema;
    }

    // Provider virtual columns for one table: __delta_row_id + __delta_row_commit_version (BIGINT), advertised
    // only when the catalog reads natively (the per-file SQL derives them) AND the table tracks rows. A real
    // user column with the same name shadows the virtual one at bind (DuckDB's TableBinding prefers real names).
    private IArrowArrayStream VirtualColumnsStream(string path)
    {
        bool rowTracking = false;
        if (_nativeRead && !_rowTrackingByPath.TryGetValue(path, out rowTracking))
        {
            DeltaReader.GetSchemaAndRowTracking(Opener(), path, out rowTracking);
            _rowTrackingByPath[path] = rowTracking;
        }
        return _nativeRead && rowTracking
            ? TwoColumn(
                "name", new[] { DeltaNativeReader.RowTrackingIdColumn, DeltaNativeReader.RowTrackingVersionColumn },
                "type", new[] { "BIGINT", "BIGINT" })
            : TwoColumn("name", System.Array.Empty<string>(), "type", System.Array.Empty<string>());
    }

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

    // Resolve a '<schema>.<table>' reference (schema mandatory on a schema-enabled lakehouse, defaults to
    // "main" on a flat catalog) to the table's folder path. Shared by the txn-version functions.
    private string ResolveTableRefPath(string? tableRef, string context)
    {
        if (string.IsNullOrEmpty(tableRef))
        {
            throw new System.ArgumentException($"{context}: a table is required ('schema.table').");
        }
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
                $"{context}: a schema is required on a schema-enabled lakehouse — use 'schema.table'.");
        }
        else
        {
            resolvedSchema = MainSchema;
        }
        return TablePath(resolvedSchema, table);
    }

    // fabricator_delta_get_transaction_version: the latest `txn`-action version for an app id — the Delta
    // idempotent-append high-water mark. 1 row: (app_id, version — NULL when the app never committed one).
    private IArrowArrayStream TxnVersionStream(string? tableRef, string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            throw new System.ArgumentException("delta transaction version: an app_id is required.");
        }
        var path = ResolveTableRefPath(tableRef, "delta transaction version");
        long? version = DeltaReader.GetAppTransactionVersion(Opener(), path, appId!);
        return AppTxnRow(appId!, version);
    }

    // fabricator_delta_set_transaction_version: PARK an application-transaction version on the current
    // explicit transaction. At COMMIT the flush compares-and-swaps it against the LATEST snapshot's
    // AppTransactions (expected null = "must not exist yet") and emits the `txn` action ATOMICALLY with the
    // transaction's fused commit — a retried batch whose first attempt landed then FAILS the CAS instead of
    // duplicating data (Delta's exactly-once mechanism; duckdb-delta / Spark txnAppId parity).
    private IArrowArrayStream SetTxnVersionStream(string? tableRef, string? payload)
    {
        var path = ResolveTableRefPath(tableRef, "delta set transaction version");
        var parts = (payload ?? string.Empty).Split('\n');
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || !long.TryParse(parts[1], out var version))
        {
            throw new System.ArgumentException(
                "delta set transaction version: app_id and version are required.");
        }
        string appId = parts[0];
        long? expected = parts.Length > 2 && parts[2].Length > 0 && long.TryParse(parts[2], out var e)
            ? e
            : null;
        long txnId = AmbientTransaction.Current;
        if (txnId == 0 || !_txnBuffer.IsExplicit(txnId))
        {
            throw new System.InvalidOperationException(
                "delta set transaction version: requires an explicit transaction (BEGIN … COMMIT) — the "
                + "version is compared-and-swapped atomically with the transaction's commit.");
        }
        var pending = _txnBuffer.GetOrCreate(txnId, path);
        // Pin the transaction's base version (like any read) so the flush has a rebase base even for an
        // otherwise append-only transaction.
        pending.PinnedVersion ??= SnapshotPinning.TryGetPinned(txnId, path)
            ?? SnapshotPinning.PinVersion(txnId, path,
                inst => DeltaReader.ResolveVersionAsOf(Opener(), path, inst, _log), System.DateTime.UtcNow);
        pending.AppTxnVersions[appId] = (version, expected);
        _log.LogInformation(
            "delta txn {Txn} set app-transaction {Path}: app='{App}' version={Version} expected={Expected}",
            txnId, path, appId, version, expected?.ToString() ?? "<none>");
        return AppTxnRow(appId, version);
    }

    private static IArrowArrayStream AppTxnRow(string appId, long? version)
    {
        var schema = new Schema(new[]
        {
            new Field("app_id", StringType.Default, nullable: false),
            new Field("version", Int64Type.Default, nullable: true),
        }, null);
        var apps = new StringArray.Builder();
        apps.Append(appId);
        var versions = new Int64Array.Builder();
        if (version is { } v)
        {
            versions.Append(v);
        }
        else
        {
            versions.AppendNull();
        }
        return new InMemoryArrayStream(schema,
            new[] { new RecordBatch(schema, new IArrowArray[] { apps.Build(), versions.Build() }, 1) });
    }

    // fabricator_delta_tblproperties(catalog, 'schema.table'): the table's delta.* properties as (property,
    // value) rows, sorted by key.
    private IArrowArrayStream TblPropertiesStream(string? tableRef)
    {
        var path = ResolveTableRefPath(tableRef, "delta tblproperties");
        var props = DeltaReader.GetTableProperties(Opener(), path);
        var keys = props.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray();
        var vals = keys.Select(k => props[k]).ToArray();
        return TwoColumn("property", keys, "value", vals);
    }

    // Table-FEATURE properties: enabling these requires a protocol upgrade (reader/writer feature + supporting
    // metadata), so they can't be flipped by a plain metaData commit on an existing table — they're set at
    // CREATE via the ATTACH option. A set_tblproperties attempt on one is rejected with a clear pointer.
    private static readonly System.Collections.Generic.HashSet<string> FeatureProperties = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "delta.enableDeletionVectors", "delta.enableChangeDataFeed", "delta.enableRowTracking",
        "delta.enableInCommitTimestamps", "delta.columnMapping.mode",
    };

    // fabricator_delta_set_tblproperties(catalog, 'schema.table', properties): SET/UNSET delta.* properties via
    // ONE metaData commit. `properties` is a JSON object {"delta.isolationLevel":"Serializable", …} (a null
    // value UNSETs). Commits IMMEDIATELY (like OPTIMIZE/VACUUM) — an administrative metadata change, not part
    // of a surrounding DuckDB transaction. Feature-enabling keys are rejected (set at CREATE).
    private IArrowArrayStream SetTblPropertiesStream(string? tableRef, string? propsJson)
    {
        var path = ResolveTableRefPath(tableRef, "delta set tblproperties");
        if (string.IsNullOrWhiteSpace(propsJson))
        {
            throw new System.ArgumentException(
                "delta set tblproperties: a JSON object of property->value is required, e.g. "
                + "'{\"delta.isolationLevel\":\"Serializable\"}'.");
        }
        var updates = new List<KeyValuePair<string, string?>>();
        using (var doc = System.Text.Json.JsonDocument.Parse(propsJson!))
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new System.ArgumentException("delta set tblproperties: properties must be a JSON object.");
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (FeatureProperties.Contains(p.Name))
                {
                    throw new System.NotSupportedException(
                        $"delta set tblproperties: '{p.Name}' enables a table FEATURE that needs a protocol "
                        + "upgrade — set it at CREATE via the ATTACH option (deletion_vectors / row_tracking / "
                        + "change_data_feed / column_mapping), not on an existing table.");
                }
                string? val = p.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Null => null,
                    System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                    _ => p.Value.GetRawText(), // numbers/booleans -> their literal text (Delta config is string-typed)
                };
                updates.Add(new KeyValuePair<string, string?>(p.Name, val));
            }
        }
        if (updates.Count == 0)
        {
            throw new System.ArgumentException("delta set tblproperties: no properties given.");
        }
        long version = DeltaReader.SetTableProperties(Opener(), path, updates);
        // Any config-derived property may have changed — fabricator.sortedBy AND the fabricator.parquet.*
        // tuning both come from here, so one eviction covers both.
        _tableConfigCache.TryRemove(path, out _);
        _log.LogInformation("delta set tblproperties {Path}: {Count} propertie(s) -> v{Version}",
            path, updates.Count, version);
        var keys = updates.Select(u => u.Key).ToArray();
        var vals = updates.Select(u => u.Value ?? "<unset>").ToArray();
        return TwoColumn("property", keys, "value", vals);
    }

    // The table's WHOLE Delta configuration per table path, read ONCE per catalog instance (every read is a
    // `_delta_log` LIST — an extra table open per append otherwise); invalidated on set_tblproperties / DROP /
    // RENAME through this catalog. ⚠ ONE cache for every config-derived property (fabricator.sortedBy AND the
    // fabricator.parquet.* tuning) on purpose: two caches would mean two opens per append and two sets of
    // invalidation sites to keep in sync. A property changed by ANOTHER writer takes effect here on re-attach —
    // acceptable for BOTH consumers, and for the same reason: each governs advisory LAYOUT / storage format,
    // never correctness. Nothing keyed on this may ever decide what a query RETURNS.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, IReadOnlyDictionary<string, string>?> _tableConfigCache = new();

    /// <summary>The table's Delta configuration, cached per path. ⚠ A MISS IS NEVER CACHED, and that is
    /// correctness rather than thrift: the spec for a write is resolved BEFORE the write runs, so a CTAS asks
    /// for the configuration of a table that does not exist yet. Caching that "absent" would make the CREATE's
    /// own persisted declaration invisible to every later statement in the session — measured exactly that way
    /// (the property landed in the table, and the next plain INSERT still wrote SNAPPY). Note it would have
    /// silently broken <c>fabricator.sortedBy</c> too, which shares this cache and never used to be read on a
    /// create path: an ordered table would have quietly stopped ordering its appends.</summary>
    private IReadOnlyDictionary<string, string>? TableConfig(nint opener, string path)
    {
        if (_tableConfigCache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var cfg = DeltaReader.GetTableConfigAll(opener, path);
        if (cfg is not null)
        {
            _tableConfigCache[path] = cfg;
        }
        return cfg;
    }

    private IReadOnlyList<string>? SortedByFromConfig(nint opener, string path, Schema schema)
    {
        var cols = DeltaWriter.ParseSortedBy(
            TableConfig(opener, path) is { } cfg && cfg.TryGetValue(DeltaWriter.SortedByKey, out var v) ? v : null);
        if (cols is not { Count: > 0 })
        {
            return null;
        }
        // Tolerate schema drift: order only by the persisted columns PRESENT in this write's schema.
        var present = new List<string>(cols.Count);
        foreach (var c in cols)
        {
            foreach (var f in schema.FieldsList)
            {
                if (string.Equals(f.Name, c, System.StringComparison.OrdinalIgnoreCase))
                {
                    present.Add(c);
                    break;
                }
            }
        }
        return present.Count > 0 ? present : null;
    }

    // The ordered-write wrap: run the live input stream through the HOST engine's ORDER BY — DuckDB's
    // EXTERNAL (disk-spilling) sort absorbs the global reorder, so the bridge pipeline stays streaming
    // (channel backpressure feeds the sort; the sorted stream feeds whichever write path runs next).
    private static IArrowArrayStream SortStream(IArrowArrayStream data, IReadOnlyList<string> cols)
    {
        // Unique per call — see BoundInput (a fixed name races concurrent host queries). This returns a LAZY
        // stream, so the view must outlive the call: BoundInput.WrapDrop defers the DROP to the caller's
        // Dispose, the only point that knows draining is over. (The COPY/filter sites instead drop in a
        // finally, because their queries materialize before returning.)
        string sortInput = BoundInput.NextName("__fabricator_sort_input");
        var sb = new System.Text.StringBuilder("SELECT * FROM \"" + sortInput + "\" ORDER BY ");
        for (int i = 0; i < cols.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append('"').Append(cols[i].Replace("\"", "\"\"")).Append('"');
        }
        return BoundInput.WrapDrop(
            Host.Query(sb.ToString(), new (string, IArrowArrayStream)[] { (sortInput, data) }),
            sortInput);
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

    /// <summary>
    /// The schemas this catalog ADVERTISES: the discovered DATA schemas plus, on a OneLake root, the
    /// <c>fabric</c> function namespace.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately distinct from <see cref="SchemaNames"/>, and the split is load-bearing in BOTH
    /// directions:</para>
    /// <list type="bullet">
    ///   <item>The host drops a declared function whose schema it did not register
    ///   (<c>FabricatorCatalog::LoadCatalog</c>), so WITHOUT this the entire Fabric function set would silently
    ///   cease to exist — no error, just ~50 missing functions.</item>
    ///   <item>Conversely <see cref="SchemaNames"/> must NOT include it, because that list is what the
    ///   <c>__all__</c> sentinel expands over: adding <c>fabric</c> there would re-declare the provider's
    ///   macros and <c>fab_delta_info</c> inside it, which is the per-schema duplication that moving the Fabric
    ///   functions out of <c>__all__</c> exists to remove.</item>
    /// </list>
    /// <para>Gated on the SAME condition as the registration in <see cref="BuildFunctionSet"/> — a local or S3
    /// Delta attach registers no Fabric functions and so must advertise no <c>fabric</c> schema, or it would
    /// gain a permanently empty one.</para>
    /// </remarks>
    /// <summary>
    /// Refuses DDL that would put a TABLE into the <c>fabric</c> function namespace.
    /// </summary>
    /// <remarks>
    /// <para>The schema is synthetic — declared by this provider to host functions, backed by no storage — but
    /// the host cannot know that and will happily route <c>CREATE TABLE cat.fabric.t</c> here, which would
    /// create a real Delta table in a <c>fabric/</c> folder. Nothing would break immediately; the damage is that
    /// on the next ATTACH <c>fabric</c> is ALSO discovered as a data schema, so a namespace deliberately
    /// separated from the user's tables quietly stops being separate, and the folder now has to be cleaned up by
    /// hand.</para>
    /// <para>Refusing costs one comparison and names the fix. It applies only where the synthetic schema exists
    /// (a OneLake root) — elsewhere <c>fabric</c> is an ordinary name a user is entitled to use for their own
    /// data, and forbidding it there would be inventing a reserved word.</para>
    /// </remarks>
    private void RejectFunctionSchemaDdl(string schemaName, string what)
    {
        if (FabricLakehouse.IsOneLake(_root)
            && string.Equals(schemaName, FabricApiFunctions.SchemaName, System.StringComparison.OrdinalIgnoreCase))
        {
            throw new System.NotSupportedException(
                $"{what}: '{FabricApiFunctions.SchemaName}' is this catalog's Fabric FUNCTION namespace, not a "
                + "storage schema — it holds no tables and is backed by no folder. Create the table in a data "
                + "schema instead.");
        }
    }

    private IReadOnlyList<string> CatalogSchemaNames()
    {
        var data = SchemaNames();
        if (!FabricLakehouse.IsOneLake(_root))
        {
            return data;
        }
        var all = new List<string>(data.Count + 1);
        all.AddRange(data);
        // Defensive: a DATA schema literally called "fabric" would otherwise be listed twice, and a duplicate
        // schema name is a host-side ensure_schema collision rather than a merge. Case-INSENSITIVE on purpose:
        // DuckDB resolves schema names that way, so a lakehouse schema named "Fabric" collides just as hard
        // (and this matches RejectFunctionSchemaDdl and the SqlServer side, which must agree on the answer).
        if (!all.Contains(FabricApiFunctions.SchemaName, System.StringComparer.OrdinalIgnoreCase))
        {
            all.Add(FabricApiFunctions.SchemaName);
        }
        return all;
    }

    /// <summary>
    /// Resolves the provider's catalog-macro declarations against THIS catalog's discovered schemas, expanding
    /// the <c>__all__</c> sentinel to one declaration per schema.
    /// </summary>
    /// <remarks>
    /// A Delta root's schema names are folder names, not known until ATTACH, so a static declaration cannot
    /// name them — hence the sentinel. A declaration naming a real schema is passed through untouched (and the
    /// host drops any whose schema it did not register, which is what makes an ATTACH <c>schema_filter</c> gate
    /// macros too).
    /// </remarks>
    private IReadOnlyList<CatalogMacroDefinition> ExpandCatalogMacroSchemas()
    {
        var declared = DeltaBackend.DeclaredCatalogMacros;
        var outList = new List<CatalogMacroDefinition>();
        IReadOnlyList<string>? schemas = null;
        foreach (var m in declared)
        {
            if (!string.Equals(m.SchemaName, "__all__", System.StringComparison.Ordinal))
            {
                outList.Add(m);
                continue;
            }
            schemas ??= SchemaNames();
            foreach (var s in schemas)
            {
                outList.Add(m with { SchemaName = s });
            }
        }
        return outList;
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

        // Plain (non-OneLake) ADLS Gen2 with a credential: walk DFS DIRECTORIES through the SDK. There is no
        // Unity Catalog to ask — that is a Fabric service — so the list comes from the filesystem either way;
        // this shape is O(tables) where the glob below is O(commit files). See AdlsTableDiscovery.
        if (_adlsCredential is not null && AdlsPath.IsAdlsGen2(_root))
        {
            return AdlsTableDiscovery.Discover(_root, _adlsCredential, _schemas, MainSchema);
        }

        // PLAIN LOCAL root (incl. the Fabric notebook fuse mount /lakehouse/default/Tables): discover via
        // direct System.IO enumeration — schema dirs → table dirs → Directory.Exists(_delta_log). O(dirs)
        // syscalls instead of the host glob, whose commit-file matching + per-match stat is minutes over a
        // fuse mount (measured: 258 s ATTACH on a populated lakehouse → ~1 s with this path).
        if (System.IO.Directory.Exists(_root))
        {
            foreach (var schemaDir in _schemas
                         ? System.IO.Directory.EnumerateDirectories(_root)
                         : new[] { _root })
            {
                string schemaName2 = _schemas ? System.IO.Path.GetFileName(schemaDir) : MainSchema;
                foreach (var tableDir in System.IO.Directory.EnumerateDirectories(schemaDir))
                {
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(tableDir, "_delta_log")))
                    {
                        pairs.Add((schemaName2, System.IO.Path.GetFileName(tableDir)));
                    }
                }
            }
            return pairs;
        }

        // Object stores (S3 / plain ADLS): glob the commit files. schemas mode = two levels deep, else one.
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
        // the global fabricator_delta_scan; column-pruning into parquet would need a projected-schema stream.
        var spec = ScanSpec.Parse(specJson);
        _log.LogDebug("delta scan {Schema}.{Table}: mode={Mode} native_filter={NF} spec={Spec}",
            schemaName, tableName, _pushdownMode, spec?.NativeFilter ?? "<none>",
            spec is null ? "<null>" : (spec.Columns is null ? "no-cols" : $"cols={spec.Columns.Count}"));
        var filterVals = ReadFilterValues(filterValues);
        if (_pushdownMode == PushdownMode.None && spec is not null)
        {
            // Pure fallback: consume no filters at all — DuckDB applies everything above the scan.
            spec.Filter = null;
            spec.NativeFilter = null;
        }
        EngineeredWood.Expressions.Predicate? filter = spec?.Filter is { } node
            ? new DeltaFilterBuilder(filterVals).Build(node)
            : null;

        // READ-SET recording (explicit transactions; Spark ConflictChecker parity): the pushed predicate —
        // a superset of the rows this scan consumes (unpushed residue filters ABOVE the scan, so the files
        // it can touch are exactly those the pushed part matches) — or whole-table when nothing pushed.
        // Feeds the logical rebase's concurrentAppend/concurrentDeleteRead checks at COMMIT. Explicit AT
        // scans read committed history, not the transaction's snapshot — excluded. Under SERIALIZABLE the
        // first read also pins the transaction's base version (the rebase walks pin+1..latest; without a
        // pin an append-only transaction's reads would have no base to check against).
        // spec == null is the BIND-TIME schema probe (no projection, no filter — not a data read);
        // every real scan carries a spec with at least its projected columns.
        long scanTxn = AmbientTransaction.Current;
        if (spec is not null && scanTxn != 0 && spec.At is null && _txnBuffer.IsExplicit(scanTxn))
        {
            var readPending = _txnBuffer.GetOrCreate(scanTxn, path);
            if (filter is null)
            {
                readPending.ReadWholeTable = true;
            }
            else
            {
                readPending.ReadPredicates.Add(filter);
            }
            // SNAPSHOT ISOLATION for reads (default): the transaction's FIRST scan captures one UTC
            // instant (SnapshotPinning, per txn) and each table resolves it to a version on first touch —
            // every scan in the transaction then reads that consistent cut (the codec branch below routes
            // through the AT-version streams; the native path already did). Also the rebase base for the
            // COMMIT conflict check. Since hoist slice 5 a table created in
            // this transaction is on storage like any other, so there is no create case to exclude.
            {
                readPending.PinnedVersion ??= SnapshotPinning.TryGetPinned(scanTxn, path)
                    ?? SnapshotPinning.PinVersion(scanTxn, path,
                        inst => DeltaReader.ResolveVersionAsOf(opener, path, inst, _log), System.DateTime.UtcNow);
            }
        }

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
            var (atProjCols, atProjected) = ProjectFor(atSchema, spec);
            // DuckDB may still request the virtual rowid for a time-travel scan (its count(*)-via-rowid
            // optimization). Produce it (version-aware transient rowid) so the stream matches what DuckDB asked
            // for; otherwise the rowid (BIGINT) it expects collides with the first user column (the
            // "BIGINT referenced INTEGER" internal error). No DML against a past snapshot, so it's read-only.
            bool wantRowIdAt = spec.Columns is { } atCols && atCols.Contains(RowIdColumn);
            if (wantRowIdAt)
            {
                var atSchemaWithRowId = new Schema(
                    new List<Field>(atProjected.FieldsList)
                    {
                        new Field(RowIdColumn, Int64Type.Default, nullable: false),
                    }, atProjected.Metadata);
                return new AsyncEnumerableArrowStream(atSchemaWithRowId, WithExactFilter(atSchemaWithRowId,
                    DeltaReader.StreamWithRowIdsAt(opener, path, atProjCols, filter, at.Unit, at.Value, default), spec));
            }
            // Explicit AT = committed history: buffered (uncommitted) appends are deliberately excluded.
            return new AsyncEnumerableArrowStream(atProjected, WithExactFilter(atProjected,
                DeltaReader.StreamAt(opener, path, atProjCols, filter, at.Unit, at.Value, default), spec));
        }

        // Read-your-writes: overlay this transaction's buffered changes onto the scan.
        var pendingScan = _txnBuffer.Get(AmbientTransaction.Current, path);
        if (pendingScan is { Files.Count: > 0 })
        {
            // native_write catalog read mid-transaction: the buffered appends are STREAMED FILES (not
            // batches), invisible to the codec reader (they're not in the _delta_log yet) — route the scan
            // through the native per-file reader, which overlays them uniformly (host_query is available on
            // a native_write catalog by construction; after COMMIT scans return to the codec path).
            return ScanNative(opener, path, spec, filterVals);
        }
        // SNAPSHOT ISOLATION (default): inside an explicit transaction, plain codec reads run AT the
        // transaction's pinned version — the instant captured at the transaction's FIRST scan, resolved to
        // a version per table (SnapshotPinning; recorded above, but also consulted directly since a
        // read-only buffer entry is invisible through Get()). A concurrent writer's commits are therefore
        // NOT visible mid-transaction; autocommit statements keep reading latest (a single codec statement
        // is one snapshot anyway). The buffer pin (DML/ALTER) has priority — same source, same value.
        long? pinnedRead = pendingScan?.PinnedVersion;
        if (pinnedRead is null && scanTxn != 0)
        {
            // Deliberately NOT gated on IsExplicit: this only READS an existing pin, and in AUTOCOMMIT the
            // pin is now seeded by the first codec scan's own schema open (ScanCodec) — so a SECOND reference
            // to the same table in ONE statement (self-join, t UNION ALL t, a correlated re-scan,
            // INSERT INTO t … FROM t JOIN t) reads the version the first one read instead of opening at
            // latest independently and possibly straddling a concurrent commit. Reading the pin BEFORE the
            // schema fetch matters: it makes the schema and the data come from the same version, which
            // seeding alone would not guarantee if a concurrent ALTER landed in between.
            pinnedRead = SnapshotPinning.TryGetPinned(scanTxn, path);
        }
        string? pinnedReadValue = pinnedRead?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ScanCodec(opener, path, spec, filter, pendingScan, pinnedReadValue);
    }

    // The codec-path VIRTUAL-TABLE read: the pinned base stream overlaid with this transaction's
    // buffered changes — pending DELETEs excluded (the base is forced onto the rowid stream so
    // positions can be matched), a pending ALTER's schema advertised (projection translated to the
    // committed names, batches reconciled), pending in-memory batches concatenated. One composition
    // serves plain reads, rowid (DML-plan) reads, and buffered-DML read-your-writes; no-pending scans
    // pass straight through it (every overlay step is conditional).
    //
    // Deliberately NOT an engineered-wood synthetic Snapshot (the "pinned ⊕ pending actions" form):
    // EW's OrderedActiveFiles path-sorts the WHOLE active set, so uuid-named pending files would
    // interleave into the committed ordinal range and break the transient-rowid contract that scans,
    // position parking, the flush's DV resolution and the same-txn-DML routing all share (committed
    // ordinals < 0x700000, pending files at 0x780000+idx). The overlay composition IS the virtual
    // table, with the ordinal spaces kept disjoint by construction.
    private IArrowArrayStream ScanCodec(nint opener, string path, ScanSpec? spec,
                                        EngineeredWood.Expressions.Predicate? filter,
                                        DeltaTxnBuffer.PendingAppends? pending, string? pinnedReadValue)
    {
        // Buffered DML forces the rowid stream: positions decode against the PINNED version's ordinals
        // (BufferDeleteRows guarantees PinnedVersion is set whenever DeletedByOrdinal is non-empty).
        bool hasDeletes = pending is { DeletedByOrdinal.Count: > 0 };
        // Pending buffered ALTER: advertise the PENDING schema. The engineered-wood read below knows only
        // the COMMITTED columns, so pending-only names are stripped from its projection and each batch is
        // RECONCILED to the advertised shape afterwards (added columns backfilled as typed NULLs).
        Schema userSchema;
        if (pending?.PendingArrowSchema is { } pendingSchema)
        {
            userSchema = pendingSchema;
        }
        else if (pinnedReadValue is null)
        {
            // ZERO-IO SNAPSHOT PIN. This open reads the latest version anyway, so recording which version it
            // saw costs nothing and every LATER reference to this table in the same statement/transaction then
            // reads AT it (ScanTable consults the pin before calling us). Without it each reference opened at
            // latest independently, so two references in ONE autocommit statement could straddle a concurrent
            // commit — the codec path never pinned in autocommit because the pin was written inside the
            // explicit-only read-set block (see docs + CLAUDE.md). Seeding from THIS open rather than from
            // ResolveVersionAsOf is what keeps it free; that helper would open the log again.
            // The explicit-transaction pin is instant-resolved (so a multi-table query gets ONE cut) and is
            // already set by the time we are called — PinVersion's GetOrAdd therefore never overwrites it,
            // and a concurrent seeder wins the race harmlessly (we then read at ITS version).
            userSchema = DeltaReader.GetSchemaAndVersion(opener, path, out long latest);
            long pinTxn = AmbientTransaction.Current;
            if (pinTxn != 0)
            {
                pinnedReadValue = SnapshotPinning
                    .PinVersion(pinTxn, path, _ => latest, System.DateTime.UtcNow)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                // Logged because this branch runs at most ONCE per (txn, table) — ScanTable consults the pin
                // before calling us — so the line count IS the sharing assertion: one line however many times
                // the statement references the table (verify_delta_autocommit_pin).
                _log.LogDebug("delta codec pin {Path} -> v{Version}", path, pinnedReadValue);
            }
        }
        else
        {
            userSchema = DeltaReader.GetSchemaAt(opener, path, "version", pinnedReadValue);
        }
        var (projCols, projected) = ProjectFor(userSchema, spec);
        bool reconcile = pending?.PendingMetadata is not null;
        var renameRev = reconcile ? CommittedToPending(pending!) : null;
        var ewProjCols = reconcile
            ? TranslateProjectionToCommitted(projCols, opener, path, pinnedReadValue, pending!)
            : projCols;
        // When the scan requests the virtual rowid (UPDATE/DELETE plans), advertise it in the schema;
        // DuckDB maps the requested output by name. Pending deletes need the rowid internally even when
        // the scan didn't ask (dropped again after the exclusion).
        bool wantRowId = spec?.Columns is { } cols && cols.Contains(RowIdColumn);
        bool needRowId = wantRowId || hasDeletes;
        var outSchema = wantRowId ? SchemaWithRowId(projected) : projected;
        System.Collections.Generic.IAsyncEnumerable<RecordBatch> stream;
        if (needRowId)
        {
            stream = pinnedReadValue is null
                ? DeltaReader.StreamWithRowIds(opener, path, ewProjCols, filter, default)
                : DeltaReader.StreamWithRowIdsAt(opener, path, ewProjCols, filter, "version", pinnedReadValue, default);
            if (hasDeletes)
            {
                stream = DeltaTxnBuffer.ExcludeDeleted(
                    stream, pending!.DeletedByOrdinal, dropRowId: !wantRowId, TransientRowAddress.PositionBits);
            }
        }
        else
        {
            stream = pinnedReadValue is null
                ? DeltaReader.Stream(opener, path, ewProjCols, filter, default)
                : DeltaReader.StreamAt(opener, path, ewProjCols, filter, "version", pinnedReadValue, default);
        }
        if (reconcile)
        {
            stream = ReconcileToSchema(stream, outSchema, renameRev);
        }
        if (pending is { Batches.Count: > 0 })
        {
            stream = DeltaTxnBuffer.Concat(stream,
                DeltaTxnBuffer.ProjectPending(pending, outSchema, RowIdColumn, PendingRowIdOrdinal));
        }
        return new AsyncEnumerableArrowStream(outSchema, WithExactFilter(outSchema, stream, spec));
    }

    // The projected schema with the trailing virtual _metadata.row_id column appended.
    private static Schema SchemaWithRowId(Schema projected) =>
        new Schema(new List<Field>(projected.FieldsList)
        {
            new Field(RowIdColumn, Int64Type.Default, nullable: false),
        }, projected.Metadata);

    // Translate a pending-ALTER projection to the COMMITTED names the data is stored under (renamed
    // columns), dropping pending-only columns (added — nothing to read; the reconcile backfills NULLs).
    private IReadOnlyList<string>? TranslateProjectionToCommitted(
        IReadOnlyList<string>? projCols, nint opener, string path, string? pinnedReadValue,
        DeltaTxnBuffer.PendingAppends pending)
    {
        if (projCols is null)
        {
            return null;
        }
        var committed = pinnedReadValue is null
            ? DeltaReader.GetSchema(opener, path)
            : DeltaReader.GetSchemaAt(opener, path, "version", pinnedReadValue);
        var keep = new List<string>();
        foreach (var pc in projCols)
        {
            var src = pending.RenameMap.TryGetValue(pc, out var orig) ? orig : pc;
            foreach (var fl in committed.FieldsList)
            {
                if (string.Equals(fl.Name, src, System.StringComparison.OrdinalIgnoreCase))
                {
                    keep.Add(src);
                    break;
                }
            }
        }
        return keep.Count > 0 ? keep : null;
    }

    // A table created in THIS transaction exists only in the buffer (no _delta_log) — serve the scan
    // from the pending batches plus any eagerly-STREAMED pending files (slice B: a native_write CTAS's
    // data is already on storage, read back via the host's read_parquet and renamed physical->logical
    // through the pre-assigned mapping schema). Synthetic rowids for the count(*)/DML-plan paths; DML
    // against pending rows is rejected with its own clear error.
    private bool TableExists(string path)
    {
        try
        {
            DeltaReader.GetTxnDmlProfile(Opener(), path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The synthetic rowid ordinal for buffered (uncommitted) rows — far above any real file ordinal, and
    // DML against buffered rows is rejected, so the ids only need scan-local uniqueness.
    private const long PendingRowIdOrdinal = 0x700000;

    // EXACT mode on the codec path: the host rendered the erased TableFilterSet to SQL
    // (spec.native_filter) — apply it per batch via the host engine (see HostBatchFilter). A no-op when
    // exact mode is off or nothing was rendered (then DuckDB re-applies as usual).
    private System.Collections.Generic.IAsyncEnumerable<RecordBatch> WithExactFilter(
        Schema schema, System.Collections.Generic.IAsyncEnumerable<RecordBatch> source, ScanSpec? spec)
        => _pushdownMode == PushdownMode.Exact && !string.IsNullOrWhiteSpace(spec?.NativeFilter)
            ? HostBatchFilter.Apply(schema, source, spec!.NativeFilter!)
            : source;

    /// <summary>
    /// PROJECTION into the engineered-wood decode: maps the scan's requested columns to the column list the
    /// EW reader should decode (row groups are read column-selectively) plus the matching stream schema —
    /// the TABLE-ORDER subset of the full schema (engineered-wood's BackfillMissingColumns reconciles each
    /// batch to exactly that set, so schema and batches agree; arrow_ingest maps by name). The virtual rowid
    /// is excluded here (the dedicated streams append it). Falls back to the full schema when nothing (or
    /// everything) is projected, or when the requested set resolves to no user column (a bare count(*)).
    /// </summary>
    private static (IReadOnlyList<string>? Columns, Schema Schema) ProjectFor(Schema fullSchema, ScanSpec? spec)
    {
        if (spec?.Columns is not { } requested)
        {
            return (null, fullSchema);
        }
        var set = new HashSet<string>(requested, System.StringComparer.Ordinal);
        set.Remove(RowIdColumn);
        if (set.Count == 0 || set.Count >= fullSchema.FieldsList.Count)
        {
            return (null, fullSchema);
        }
        var fields = new List<Field>(set.Count);
        var names = new List<string>(set.Count);
        foreach (var f in fullSchema.FieldsList)
        {
            if (set.Contains(f.Name))
            {
                fields.Add(f);
                names.Add(f.Name);
            }
        }
        if (fields.Count == 0 || fields.Count == fullSchema.FieldsList.Count)
        {
            return (null, fullSchema);
        }
        return (names, new Schema(fields, fullSchema.Metadata));
    }

    // Native read (native_read true): resolve the snapshot (explicit AT > per-transaction pinned version >
    // latest), then let DuckDB's read_parquet decode the files via DeltaNativeReader. Pinning gives a consistent
    // cut across a multi-table query (all implicit-AT scans in one DuckDB transaction pin to the same instant).
    private IArrowArrayStream ScanNative(nint opener, string path, ScanSpec? spec, IReadOnlyList<object?> filterVals)
    {
        string? unit = null, value = null;
        bool seedPinFromSchemaOpen = false;
        var pendingNative = spec?.At is null ? _txnBuffer.Get(AmbientTransaction.Current, path) : null;
        if (spec?.At is { } at)
        {
            unit = at.Unit;
            value = at.Value; // explicit AT overrides the per-transaction pin
        }
        else if (pendingNative?.PinnedVersion is { } bufPin)
        {
            // Buffered DML pinned the transaction's base version — scans MUST read exactly that version so
            // rowids/ordinals stay consistent with the buffered deletion positions.
            unit = "version";
            value = bufPin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            long txn = AmbientTransaction.Current;
            if (txn != 0)
            {
                // An EXISTING pin (a previous reference to this table in the same statement/transaction, or
                // the instant-resolved explicit-transaction pin) is consulted FIRST — reading it before the
                // schema fetch below is what makes schema and data come from ONE version, which seeding
                // alone would not guarantee if a concurrent ALTER landed in between.
                if (SnapshotPinning.TryGetPinned(txn, path) is { } already)
                {
                    unit = "version";
                    value = already.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    // No pin yet: DEFER to the schema fetch, which opens the table at latest anyway and can
                    // report the version it saw. Resolving here instead (ResolveVersionAsOf) costs a WHOLE
                    // extra DeltaTable open — its own _delta_log LIST, plus a timestamp->version scan of the
                    // commit timestamps — and that redundant open was this path's measured "+1 snapshot
                    // construction per statement" versus the codec (docs/ew-master-migration.md §Appendix).
                    // The listing open below already resolves a version; this makes the pin FREE, exactly as
                    // ScanCodec's GetSchemaAndVersion seeding does.
                    seedPinFromSchemaOpen = true;
                }
            }
        }
        Schema userSchema;
        if (spec?.At is not null)
        {
            userSchema = DeltaReader.GetSchemaAt(opener, path, unit!, value!); // committed history
        }
        else if (pendingNative?.PendingArrowSchema is { } pendingArrow)
        {
            userSchema = pendingArrow; // pending buffered ALTER wins
        }
        else if (seedPinFromSchemaOpen)
        {
            // ZERO-IO SNAPSHOT PIN (native path). Same trick, same reason as the codec path's: record the
            // version THIS open saw so every later reference in the statement/transaction reads AT it
            // instead of opening at latest independently and possibly straddling a concurrent commit.
            // PinVersion's GetOrAdd never overwrites, so a concurrent seeder wins harmlessly (we then read
            // at ITS version, which is equally consistent).
            userSchema = DeltaReader.GetSchemaAndVersion(opener, path, out long latest);
            long pinTxn = AmbientTransaction.Current;
            long pinned = SnapshotPinning.PinVersion(pinTxn, path, _ => latest, System.DateTime.UtcNow);
            unit = "version";
            value = pinned.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _log.LogDebug("delta native pin {Path} -> v{Version}", path, value);
        }
        else
        {
            userSchema = unit is null
                ? DeltaReader.GetSchema(opener, path)
                : DeltaReader.GetSchemaAt(opener, path, unit, value!);
        }
        // Read-your-writes: overlay this transaction's buffered appends. Streamed pending FILES join the
        // per-file loop (presence probe + WHERE apply per file); collect-path pending BATCHES concatenate
        // onto the stream (exact-mode WHERE applied via the host, like the codec overlay). An explicit AT
        // excludes pending (committed history).
        var native = DeltaNativeReader.Read(opener, path, userSchema, spec, filterVals, unit, value,
                                            pendingFiles: pendingNative?.Files,
                                            pendingDeletes: pendingNative?.DeletedByOrdinal,
                                            pendingSchema: pendingNative?.PendingDeltaSchema);
        if (pendingNative is not { Batches.Count: > 0 })
        {
            return native;
        }
        // The rowid, when the scan requests it, is synthesized inside ProjectPending (schema-driven).
        var overlay = DeltaTxnBuffer.ProjectPending(pendingNative, native.Schema, RowIdColumn, PendingRowIdOrdinal);
        if (_pushdownMode == PushdownMode.Exact && !string.IsNullOrWhiteSpace(spec?.NativeFilter))
        {
            overlay = HostBatchFilter.Apply(native.Schema, overlay, spec!.NativeFilter!);
        }
        return new AsyncEnumerableArrowStream(native.Schema,
            DeltaTxnBuffer.Concat(DeltaTxnBuffer.AsEnumerable(native), overlay));
    }

    private static IReadOnlyList<object?> ReadFilterValues(IArrowArrayStream? filterValues)
        => ReadFilterValuesAsync(filterValues).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<object?>> ReadFilterValuesAsync(IArrowArrayStream? filterValues)
    {
        if (filterValues is null)
        {
            return System.Array.Empty<object?>();
        }
        using (filterValues)
        {
            var batch = await filterValues.ReadNextRecordBatchAsync().ConfigureAwait(false);
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
    // A VARIANT column crosses the C ABI as the ew.variant_transport LEAF-binary transport (one
    // metadata||value blob per row); EW models it canonically (arrow.parquet.variant) and converts at its
    // host boundary (VariantTransport, selected by DeltaTableOptions.VariantTransportBlob). Both byte
    // paths work — the native seams AND the EW codec, incl. codec rewrites. Only the placement/CDF
    // gates below remain.

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
        if (VariantMarker.IsVariantArrowField(field))
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
        ListType lt => VariantMarker.IsVariantArrowField(lt.ValueField)
                       || TypeHasVariant(lt.ValueDataType),
        MapType mt => VariantMarker.IsVariantArrowField(mt.KeyField)
                      || VariantMarker.IsVariantArrowField(mt.ValueField)
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
        // The codec paths handle variant fully (EW's VariantTransport converts the transport blob at its
        // write sites and read pipeline) — CREATE/INSERT/SELECT AND rewrites (UPDATE/OPTIMIZE) work with
        // no native-path requirement. Only the CDF combination remains gated.
        if (_changeDataFeedOnCreate)
        {
            throw new System.NotSupportedException(
                "VARIANT columns cannot be combined with change_data_feed true (the CDC change files are "
                + "written by the built-in codec, which does not emit the variant layout there).");
        }
    }

    // ---- write surface (INSERT / CTAS / COPY via the streaming bulk path) ----

    /// <summary>Streaming bulk write (INSERT / CTAS / COPY). Runs on the bulk consumer thread; the host-FS
    /// opener was re-established on it by BulkSession. createTable/replace => Overwrite (CTAS/REPLACE: the table
    /// becomes exactly these rows); otherwise Append (INSERT). One Delta commit. Returns rows written.</summary>
    public long BulkInsert(string schemaName, string tableName, IArrowArrayStream data, bool createTable,
                           bool replace, bool checkConstraints, long txnId, IReadOnlyList<string>? partitionColumns,
                           IReadOnlyList<string>? sortColumns, string? schemaMode, bool partitionOverwrite,
                           string? optionsJson)
    {
        var opener = Opener();
        _log.LogInformation("delta bulk {Schema}.{Table}: create={Create} replace={Replace} native_write={Native} partition_overwrite={PartOw}",
            schemaName, tableName, createTable, replace, _nativeWrite, partitionOverwrite);
        EnsureVariantWritable(data.Schema);
        // Nanosecond / second Arrow timestamps have no faithful Delta encoding. EW rejects them at its own
        // write sites, but the native_write path hands DuckDB the bytes and EW never sees the batches — so
        // check here too, where EVERY write (INSERT / CTAS / COPY, codec or native) passes exactly once.
        DeltaWriter.EnsureTimestampUnitsWritable(data.Schema);
        // CREATE TABLE AS ... WITH (key='value', ...): per-statement write tuning + per-table create-flag
        // overrides + delta.*/fabricator.* properties (Parse rejects unknown/guarded keys). Only a
        // create-shaped bulk carries options (the host passes them from the CTAS operator only).
        var withOpts = DeltaWithOptions.Parse(optionsJson);
        if (withOpts is not null && !createTable && !replace)
        {
            throw new System.ArgumentException(
                "WITH options apply to CREATE TABLE [AS] — a plain append carries none.");
        }
        var tablePath = TablePath(schemaName, tableName);
        // SORTED BY -> an ORDERED (clustered) write: an explicit CREATE/CTAS clause orders THIS write and
        // is persisted (fabricator.sortedBy) at create; an append with no clause re-applies the persisted
        // spec. The ORDER BY runs on the HOST engine (DuckDB's EXTERNAL, disk-spilling sort) over the live
        // input stream — the bridge pipeline stays streaming, and EVERY downstream path (native COPY,
        // codec collect, buffered CTAS, eager writes) consumes the already-SORTED stream from this one
        // interception point. Ordered files carry tight min/max on the sort keys -> stats file skipping
        // (pair with hilbert_index for multi-key clustering; see docs/global-functions.md).
        var effectiveSort = sortColumns is { Count: > 0 }
            ? sortColumns
            : !createTable && !replace ? SortedByFromConfig(opener, tablePath, data.Schema) : null;
        if (effectiveSort is { Count: > 0 } && HostFs.CanQuery)
        {
            _log.LogInformation("delta bulk {Schema}.{Table}: ordered write — ORDER BY [{Cols}] (host spillable sort)",
                schemaName, tableName, string.Join(",", effectiveSort));
            data = SortStream(data, effectiveSort);
        }
        // Partition columns are ALWAYS forwarded but take effect only when the write actually CREATES the
        // table (explicit CREATE/REPLACE, or the append shape's implicit create-if-missing) — engineered-wood's
        // OpenOrCreateAsync applies them at creation and an existing table keeps its metadata partitioning.
        // WITH keys overlay the resolved spec LAST (precedence: WITH > delta_write_options > ATTACH defaults),
        // and the create-flag overrides resolve against the catalog defaults.
        // ⚠ THE PERSISTED DECLARATION IS READ ON EVERY WRITE, INCLUDING A CREATE OR REPLACE — measured, and
        // the opposite (treat a replace as "redefines the table, inherit nothing") is WORSE in exactly the way
        // this feature exists to fix. A `CREATE OR REPLACE` does NOT re-create the Delta table: the log
        // continues and the metaData commit copies the configuration forward, so the table still DECLARES its
        // tuning afterwards. Inheriting nothing would therefore write the replace's files at the engine default
        // INTO a table declared zstd, and the next plain INSERT — which does read the declaration — would flip
        // back: mixed compression produced by one statement.
        //
        // ⚠ The corollary is a real limitation, pinned in verify_with_options and documented in the README: a
        // REPLACE cannot CHANGE the declaration (its WITH applies to that statement's write only), because
        // create-time configuration is applied at v0 and engineered-wood's OpenOrCreateAsync returns early for
        // an existing table. That is not specific to the parquet keys — it is equally true of every create flag
        // (deletion_vectors / column_mapping / row_tracking / change_data_feed) and of the `delta.*` WITH
        // properties beside them. `fabricator_delta_set_tblproperties` is what changes a declaration.
        //
        // A brand-new table needs no special case: it does not exist, so the read finds nothing — and a MISS IS
        // NOT CACHED (see TableConfig), which is what makes the CREATE's own declaration visible to the very
        // next statement.
        var spec = ApplyWithOptions(ResolveWriteSpec(partitionColumns, schemaMode, tablePath), withOpts);
        var flags = EffectiveCreateFlags(withOpts);
        // Data mode: schema_mode=overwrite forces a full replace (adopt the source schema); CREATE/CTAS/REPLACE
        // also overwrite; otherwise it's an append (INSERT / COPY create_table=false / schema_mode=merge).
        //
        // ⚠ HOIST SLICE 5 — THE ONE PLACE THIS CHANGE COULD HAVE CORRUPTED SILENTLY, and the place a WRONG
        // ASSUMPTION was caught. A CTAS arrives here with createTable=true, which used to mean "the table is
        // not there yet". Since the create is now IMMEDIATE the table IS there (empty, at v0), and an
        // Overwrite mode would make the write NON-BUFFERABLE below (`bufferable` requires Append) — so
        // `BEGIN; CREATE TABLE t AS SELECT …; …; ROLLBACK` would COMMIT its data immediately, breaking the
        // transaction's atomicity while every existing suite still passed (they assert the ROWS, and the rows
        // are right). A table this transaction just created is empty, so there is nothing for an overwrite to
        // replace: it is the append it actually is.
        //
        // ⚠ AND THE CREATE FOR A **CTAS** HAPPENS HERE, NOT IN CreateTable — measured, after assuming
        // otherwise. A bare `CREATE TABLE t (cols)` goes through DeltaCatalog.CreateTable; a CTAS reaches this
        // method with create=true and lets `OpenOrCreateAsync` do the creating, so CreateTable's ownership
        // mark never fires for it. The symptom was three versions instead of two
        // (verify_delta_catalog_transactions:1687): create + an IMMEDIATE overwrite commit + the later
        // INSERT's flush. So the immediate create is performed here too — schema only — and the data then
        // buffers like any append.
        bool createdHere = _txnBuffer.Get(txnId, tablePath) is { CreatedInTxn: true };
        if (!createdHere && createTable && !replace && !partitionOverwrite
            && _txnBuffer.IsExplicit(txnId) && !TableExists(tablePath))
        {
            DeltaWriter.Create(opener, tablePath, data.Schema, default,
                               deletionVectors: flags.DeletionVectors,
                               inCommitTimestamps: flags.InCommitTimestamps,
                               changeDataFeed: flags.ChangeDataFeed,
                               rowTracking: flags.RowTracking,
                               spec: spec, columnMapping: flags.ColumnMapping,
                               serializable: _serializable, sortedBy: sortColumns);
            _txnBuffer.GetOrCreate(txnId, tablePath).CreatedInTxn = true;
            createdHere = true;
            _log.LogInformation(
                "delta txn {Txn} created {Schema}.{Table} for CTAS (rollback will drop it)",
                txnId, schemaName, tableName);
        }
        bool overwrite = (createTable && !createdHere) || replace
                         || spec?.SchemaMode == DeltaSchemaMode.Overwrite;
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

        // Dynamic partition overwrite is append-SHAPED, so a missing target is implicitly created. With
        // PARTITION_COLUMNS supplied it is created PARTITIONED and the first write is a plain append
        // (dynamic overwrite matches no pre-existing files) — the idempotent first-run shape. WITHOUT them
        // the implicit create would be unpartitioned and only THEN fail the partitioned-target guard,
        // leaving an empty commit-0 behind — reject that up front, before anything touches storage.
        if (partitionOverwrite && !TableExists(tablePath) && partitionColumns is not { Count: > 0 })
        {
            throw new System.InvalidOperationException(
                $"PARTITION_OVERWRITE: target table '{schemaName}.{tableName}' does not exist — supply "
                + "PARTITION_COLUMNS to create it partitioned, or create it first.");
        }

        // COPY (FORMAT delta) MODE dispositions (transient-catalog-only option; the transient catalog serves
        // exactly one statement). 'error'/'ignore' are create-shaped: an EXISTING target fails / silently
        // no-ops (Spark save-mode semantics; a missing target is created by either). 'error_if_not_exists'
        // is append-shaped: a MISSING target fails instead of being implicitly provisioned (strict append —
        // the inverse of 'error'; no Spark equivalent). Returning without consuming `data` is safe —
        // BulkSession's finally drains.
        if (_copyDisposition is not null)
        {
            if (createTable && TableExists(tablePath))
            {
                if (_copyDisposition == "error")
                {
                    throw new System.InvalidOperationException(
                        $"delta COPY MODE 'error': target table '{schemaName}.{tableName}' already exists "
                        + "(use MODE 'overwrite', 'append' or 'ignore').");
                }
                if (_copyDisposition == "ignore")
                {
                    _log.LogInformation("delta bulk {Schema}.{Table}: MODE 'ignore' — target exists, no-op",
                        schemaName, tableName);
                    return 0;
                }
            }
            if (_copyDisposition == "error_if_not_exists" && !TableExists(tablePath))
            {
                throw new System.InvalidOperationException(
                    $"delta COPY MODE 'error_if_not_exists': target table '{schemaName}.{tableName}' does "
                    + "not exist (use MODE 'append' to create it implicitly).");
            }
        }

        // SORTED_COLUMNS declarative convergence (the COPY surface): when the target EXISTS and its
        // persisted fabricator.sortedBy differs from the declared columns, RE-KEY first — one metadata
        // commit updating the property AND (unpartitioned) the delta.clustering domain, the SetSortedBy
        // machinery — so repeated runs converge the table to the declared spec (dbt-style; old ZCubes go
        // stale and the next OPTIMIZE reclusters incrementally). An ABSENT option keeps the persisted
        // spec (the PARTITION_COLUMNS precedent); removal is DDL (ALTER TABLE … RESET SORTED BY). Runs
        // AFTER the dispositions so MODE 'error'/'ignore' never take metadata side effects.
        if (sortColumns is { Count: > 0 } && TableExists(tablePath))
        {
            var persisted = DeltaWriter.ParseSortedBy(
                DeltaReader.GetTableConfig(opener, tablePath, DeltaWriter.SortedByKey));
            bool same = persisted is not null && persisted.Count == sortColumns.Count;
            for (int i = 0; same && i < persisted!.Count; i++)
            {
                same = string.Equals(persisted[i], sortColumns[i], System.StringComparison.OrdinalIgnoreCase);
            }
            if (!same)
            {
                if (_txnBuffer.IsExplicit(txnId))
                {
                    throw new System.NotSupportedException(
                        "delta: changing SORTED_COLUMNS inside an explicit transaction is not supported "
                        + "(the re-key is an immediate metadata commit) — COMMIT first, or align the "
                        + "declared columns with the table's fabricator.sortedBy.");
                }
                _log.LogInformation(
                    "delta bulk {Schema}.{Table}: SORTED_COLUMNS changed ([{Old}] -> [{New}]) — re-keying",
                    schemaName, tableName,
                    persisted is null ? "" : string.Join(",", persisted), string.Join(",", sortColumns));
                DeltaReader.SetSortedBy(opener, tablePath, sortColumns, default);
                _tableConfigCache.TryRemove(tablePath, out _);
            }
        }

        // ⚠ HOIST SLICE 5: the buffered-CTAS branch that stood here is GONE. It parked the CREATE on
        // the buffer and had the flush create the table and write the rows at COMMIT. The create is now
        // immediate (DeltaCatalog.CreateTable), so by the time a CTAS reaches this method the table
        // already exists at v0 and its data is an ordinary buffered APPEND — handled by the block below,
        // which is why this one had nothing left to do. `createdHere` above is what keeps it an append.

        // Explicit-transaction append buffering: a PLAIN append parks its data (files on the streaming
        // path, materialized batches on the collect path) and the Delta commit happens at COMMIT — one
        // atomic commit per table for the whole transaction; ROLLBACK discards. Autocommit is unchanged
        // (the transaction commits at statement end). Non-append shapes stay immediate and are guarded
        // against mixing with pending appends.
        bool bufferable = mode == DeltaWriteMode.Append
            && !partitionOverwrite
            && spec?.ReplaceWhere is not { Count: > 0 }
            && spec?.SchemaMode != DeltaSchemaMode.Merge;
        if (!bufferable)
        {
            ThrowIfPendingAppends(tablePath, "a non-append write");
        }
        else
        {
            var pending = _txnBuffer.GetOrCreate(txnId, tablePath);
            pending.HasAppend = true;
            // slice C2: on a CDF table EVERY buffered statement writes its cdc counterpart (a commit
            // carrying any cdc action is read cdc-ONLY — inference is disabled — so appends fused with
            // DML would otherwise vanish from the feed). Probed once per (txn, table); partitioned CDF
            // stays on inference (its appends commit cdc-less and its DML is rejected, so commits are
            // never mixed). Insert-cdc needs the batches in hand -> CDF appends skip the streamed path.
            if (_txnBuffer.IsExplicit(txnId) && pending.CdfEnabled is null)
            {
                var prof = DeltaReader.GetTxnDmlProfile(opener, tablePath);
                pending.CdfEnabled = prof.CdfEnabled && prof.SupportsExternalCommit;
                if (pending.CdfEnabled == true)
                {
                    pending.PinnedVersion ??= SnapshotPinning.TryGetPinned(txnId, tablePath) ?? prof.Version;
                }
            }
            bool cdfAppend = pending.CdfEnabled == true;
            bool tryStream = _nativeWrite && !cdfAppend;
            if (tryStream)
            {
                // Eager-write plan, slice A: a PENDING BUFFERED ALTER streams too — the pending schema
                // (whose added columns already carry their column-mapping ids/physical names) drives the
                // NOT NULL wrap + rename maps + FIELD_IDS inside TryWriteStreaming, and the streamed
                // files fuse with the metaData action into the one commit at COMMIT.
                var deferred = DeltaWriter.TryWriteStreaming(
                    opener, tablePath, data, mode,
                    deletionVectors: flags.DeletionVectors,
                    inCommitTimestamps: flags.InCommitTimestamps,
                    changeDataFeed: flags.ChangeDataFeed,
                    rowTracking: flags.RowTracking,
                    spec: spec,                    out var deferredRows,
                    columnMapping: flags.ColumnMapping,
                    pendingSchema: pending.PendingDeltaSchema,
                    deferCommitTo: pending.Files, serializable: _serializable);
                if (deferred is not null)
                {
                    pending.Rows += deferredRows;
                    MemoryProbe.Mark("delta bulk: streamed to files, actions parked", pending.Rows);
                    _log.LogInformation("delta bulk {Schema}.{Table}: buffered {Rows} row(s) for txn {Txn} (streamed files)",
                        schemaName, tableName, deferredRows, txnId);
                    return deferredRows;
                }
                // Streaming not applicable (identity/iceberg fall back to the collect writer). What the
                // fallback may do depends on the MODE, and the distinction is the one the old comment here
                // got wrong: it justified committing immediately with "append+append commute", which is an
                // argument about CONCURRENCY and says nothing about ROLLBACK.
                //   * AUTOCOMMIT — committing now is fine and is the long-validated shape: the DuckDB
                //     transaction commits at statement end regardless, so it is byte-identical.
                //   * EXPLICIT BEGIN..COMMIT — it must NOT commit. A committed append cannot be undone, so
                //     ROLLBACK would silently keep the rows. Collect under the buffer instead (below).
                //     This was unreachable until `PROVIDER 'delta'` began defaulting native_write ON: with
                //     it off, tryStream was false and every identity append already took the buffered path.
                //     Caught by verify_delta_catalog_transactions' identity section (3 log commits mid-txn
                //     where 2 are correct) and pinned there by a ROLLBACK assertion.
                // A pending ALTER also forces collection, for a different reason: the data must not commit
                // before the schema it was written against.
            }
            if (!tryStream || pending.PendingMetadata is not null || _txnBuffer.IsExplicit(txnId))
            {
                var (bschema, bbatches, brows) = DeltaWriter.Materialize(data, default);
                // ⚠ HOIST SLICE 5 deleted the pending-created IDENTITY pre-generation that stood here. It
                // generated values from the PARKED create schema and chained the marks so the flush could
                // bake the final high-water marks into commit-0 — only possible while the create waited for
                // the whole transaction. With an immediate create the table's own schema is authoritative and
                // an identity table created in this transaction takes the SAME path as one that already
                // existed: engineered-wood generates the values on the write and the flush's metaData action
                // carries the marks (BuildIdentityMetadataAction). This is "stop special-casing", not a lost
                // guarantee — but it IS observable: v0 of such a table no longer shows the final mark.
                if (cdfAppend)
                {
                    WriteCdcFiles(opener, tablePath, pending, bbatches, "insert");
                }
                // slice C1: in an EXPLICIT transaction the statement's batches become a data file NOW
                // (memory caps at one statement); autocommit keeps the batch park (flushes at statement
                // end anyway — byte-identical to per-statement commits).
                if (_txnBuffer.IsExplicit(txnId)
                    && TryEagerWriteBatches(opener, tablePath, pending, bbatches, tableName))
                {
                    foreach (var b in bbatches)
                    {
                        b.Dispose();
                    }
                    pending.Rows += brows;
                    _log.LogInformation(
                        "delta bulk {Schema}.{Table}: buffered {Rows} row(s) for txn {Txn} (eager file)",
                        schemaName, tableName, brows, txnId);
                    return brows;
                }
                pending.BatchSchema ??= bschema;
                pending.Batches.AddRange(bbatches);
                pending.Rows += brows;
                // The one buffered-INSERT branch that RETAINS batches until COMMIT (identity/iceberg, or a
                // pending ALTER). Every other branch parks actions only, so this mark is where an explicit
                // transaction's memory actually grows with rows.
                MemoryProbe.Mark("delta bulk: batches PARKED until commit", pending.Rows);
                _log.LogInformation("delta bulk {Schema}.{Table}: buffered {Rows} row(s) for txn {Txn} (collected batches)",
                    schemaName, tableName, brows, txnId);
                return brows;
            }
        }

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
                deletionVectors: flags.DeletionVectors,
                inCommitTimestamps: flags.InCommitTimestamps,
                changeDataFeed: flags.ChangeDataFeed,
                rowTracking: flags.RowTracking,
                spec: spec,                out var streamedRows,
                columnMapping: flags.ColumnMapping, serializable: _serializable, sortedBy: sortColumns);
            if (streamedVersion is not null)
            {
                return streamedRows;
            }
        }

        // Collect path: materialize the whole stream in C# (bounded by RAM), then write via engineered-wood's
        // codec OR DuckDB's per-file writer (native_write, non-streamable case: partitioned/merge/…).
        var (schema, batches, rows) = DeltaWriter.Materialize(data, default);
        DeltaWriter.Write(opener, tablePath, schema, batches, mode, default,
                          deletionVectors: flags.DeletionVectors,
                          inCommitTimestamps: flags.InCommitTimestamps,
                          changeDataFeed: flags.ChangeDataFeed,
                          rowTracking: flags.RowTracking,
                          spec: spec, nativeWrite: _nativeWrite,
                          columnMapping: flags.ColumnMapping, serializable: _serializable,
                          sortedBy: sortColumns);
        return rows;
    }

    /// <summary>The create-time feature flags for one statement: the WITH override where given, else the
    /// catalog's ATTACH default — so a single table opts in/out (e.g. the protocol-1.0 recipe
    /// <c>WITH (deletion_vectors=false, column_mapping='none')</c>) without a dedicated ATTACH.</summary>
    private readonly record struct CreateFlags(
        bool DeletionVectors, bool InCommitTimestamps, bool ChangeDataFeed, bool RowTracking,
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode ColumnMapping);

    private CreateFlags EffectiveCreateFlags(DeltaWithOptions? w) => new(
        w?.DeletionVectors ?? _deletionVectorsOnCreate,
        w?.InCommitTimestamps ?? _inCommitTimestampsOnCreate,
        w?.ChangeDataFeed ?? _changeDataFeedOnCreate,
        w?.RowTracking ?? _rowTrackingOnCreate,
        w?.ColumnMapping ?? _columnMappingMode);

    /// <summary>Overlays the WITH clause's write tuning + delta.*/fabricator.* properties onto the resolved
    /// write spec (WITH wins per key — the strongest layer above delta_write_options and the ATTACH defaults).</summary>
    private DeltaWriteSpec? ApplyWithOptions(DeltaWriteSpec? spec, DeltaWithOptions? w)
    {
        if (w is null || (!w.HasWriteTuning && w.Properties is not { Count: > 0 }))
        {
            return spec;
        }
        spec ??= new DeltaWriteSpec(null, null, null, null);
        if (w.Compression is { } comp)
        {
            spec = spec with
            {
                Compression = ParseCompression(comp)
                    ?? throw new System.ArgumentException(
                        $"WITH parquet_compression: unknown codec '{comp}' "
                        + "(expected snappy/zstd/gzip/brotli/lz4/uncompressed)."),
            };
        }
        if (w.RowGroupSize is { } rg)
        {
            spec = spec with { RowGroupSize = rg };
        }
        if (w.BloomFilterColumns is { } bloom)
        {
            // ⚠ REFUSE UNDER COLUMN MAPPING — this option SILENTLY DID NOTHING on the DEFAULT table shape
            // until 2026-08-07, and nothing caught it because no suite ever asserted that a bloom filter was
            // written at all. engineered-wood matches BloomFilterColumns against the PARQUET path, and on a
            // column-mapped table that path is the PHYSICAL name (`col-e090d9ee…`), never the logical one —
            // measured: 0 of 10 column chunks got a filter with mapping on, 10 of 10 with it off.
            //
            // Refusing rather than fixing is deliberate and bounded: the physical names are assigned by the
            // CREATE itself, and engineered-wood takes ParquetWriteOptions AT OPEN, so translating them needs
            // either a two-phase open or an EW-side resolution against the Delta schema. Until then a loud
            // error with the one-word workaround beats a knob that writes nothing.
            //
            // Only the WITH layer is refused. A `SET delta_write_options` bloom list spans every table in the
            // session (some mapped, some not), and a PERSISTED one is a declaration a different engine may
            // read — failing either would punish writes that never asked for anything here. Those two are
            // logged instead, at Debug, by the persisted layer's drop reporting.
            if (EffectiveCreateFlags(w).ColumnMapping
                != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None)
            {
                throw new System.ArgumentException(
                    "parquet_bloom_filter_columns names LOGICAL columns, but this table uses column mapping, "
                    + "where the parquet files store PHYSICAL names — the filter would never be built. Add "
                    + "column_mapping='none' to the same WITH clause, or drop the option.");
            }
            spec = spec with { BloomFilterColumns = bloom };
        }
        if (w.RowGroupSizeBytes is { } rgb)
        {
            spec = spec with { RowGroupSizeBytes = rgb };
        }
        if (w.RowGroupsPerFile is { } rgpf)
        {
            spec = spec with { RowGroupsPerFile = rgpf };
        }
        if (w.DictionarySizeLimit is { } dsl)
        {
            spec = spec with { DictionarySizeLimit = dsl };
        }
        if (w.FileSizeBytes is { } fsb)
        {
            spec = spec with { FileSizeBytes = fsb };
        }
        if (w.ParquetVersion != DeltaParquetVersion.Default)
        {
            spec = spec with { ParquetVersion = w.ParquetVersion };
        }
        if (w.CompressionLevel is { } clevel)
        {
            spec = spec with { CompressionLevel = clevel };
        }
        if (w.BloomFilterFpp is { } wfpp)
        {
            spec = spec with { BloomFilterFpp = wfpp };
        }
        // ── PERSIST the statement's own parquet tuning as table properties, so a later plain INSERT, a
        // merge-on-read post-image, a copy-on-write rewrite and OPTIMIZE's compaction all keep writing the
        // format the table was created with — whichever catalog happens to run them.
        //
        // ⚠ ONLY WHAT THIS STATEMENT'S `WITH` DECLARED, never the fully-resolved spec. Persisting the resolved
        // value would turn a session `SET delta_write_options` into a permanent property of the table, which is
        // exactly what the precedence rule (property OUTRANKS setting) exists to prevent — a stray SET must not
        // silently change a table's storage format, still less durably. The WITH is the explicit, user-authored,
        // per-table layer, so it is the one that persists.
        //
        // ⚠ EVERY declared key persists, INCLUDING ones this engine cannot honour (see DeltaParquetProperties):
        // the reading engine intersects with its own capabilities. Note the keys ride `CreateProperties`, whose
        // only consumer is `CreateConfig` at creation — engineered-wood's OpenOrCreateAsync returns early on an
        // existing table and ignores the configuration entirely — so this is create-only by construction, the
        // same lifecycle as the `delta.*` properties it sits beside, and a plain INSERT cannot rewrite it.
        var tuning = new ParquetTuning(
            Compression: w.Compression,
            RowGroupSize: w.RowGroupSize,
            RowGroupSizeBytes: w.RowGroupSizeBytes,
            ParquetVersion: w.ParquetVersion switch
            {
                DeltaParquetVersion.V1 => "V1",
                DeltaParquetVersion.V2 => "V2",
                _ => null,
            },
            DictionarySizeLimit: w.DictionarySizeLimit,
            CompressionLevel: w.CompressionLevel,
            BloomFilterFpp: w.BloomFilterFpp,
            RowGroupsPerFile: w.RowGroupsPerFile,
            FileSizeBytes: w.FileSizeBytes,
            BloomFilterColumns: w.BloomFilterColumns);
        if (w.Properties is { Count: > 0 } || !tuning.IsEmpty)
        {
            var props = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var kv in tuning.Render()) { props[kv.Key] = kv.Value; }
            // An explicit `WITH ("fabricator.parquet.compression"='…')` wins over the same value derived from
            // `WITH (parquet_compression='…')` — the raw property spelling is the more specific request, and
            // this way the two spellings cannot disagree about which one landed.
            if (w.Properties is { Count: > 0 } declared)
            {
                foreach (var kv in declared) { props[kv.Key] = kv.Value; }
            }
            spec = spec with { CreateProperties = props };
        }
        return ValidateSpecForEngine(spec);
    }

    /// <summary>Creates an empty Delta table (commit 0 with the schema). Idempotent (OpenOrCreate), so
    /// <paramref name="ifNotExists"/> is satisfied; PK/UNIQUE/DEFAULT are ignored (Delta has no such constraints).</summary>
    // identityColumns (the DuckDB `AS (0)` generated-column marker, v53): Delta has NATIVE identity —
    // attach delta.identity.* metadata (start 1, step 1, GENERATED ALWAYS) to the marked fields.
    // Keep nullable=true on the DuckDB-facing schema: the INSERT stream carries NULLs for the
    // engine-assigned column (generation replaces them); files never actually hold nulls.
    private static Schema WithIdentityMetadata(Schema columns, IReadOnlyList<string>? identityColumns)
    {
        if (identityColumns is not { Count: > 0 })
        {
            return columns;
        }
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
                withIdentity.Add(new Field(f.Name, f.DataType, nullable: true, meta));
            }
            else
            {
                withIdentity.Add(f);
            }
        }
        return new Schema(withIdentity, columns.Metadata);
    }

    // Bakes a buffered transaction's final identity high-water marks into the parked ARROW schema's
    // field metadata — commit-0 then carries the marks the transaction's pre-generated values consumed
    // (no separate metaData action; nobody can have consumed ids from a never-committed table).
    private static Schema BakeIdentityMarks(Schema columns, IReadOnlyDictionary<string, long> marks)
    {
        var fields = new List<Field>(columns.FieldsList.Count);
        foreach (var f in columns.FieldsList)
        {
            if (marks.TryGetValue(f.Name, out var hwm))
            {
                var meta = new Dictionary<string, string>();
                if (f.Metadata is { } src)
                {
                    foreach (var kv in src) { meta[kv.Key] = kv.Value; }
                }
                meta[EngineeredWood.DeltaLake.Schema.IdentityColumn.HighWaterMarkKey] =
                    hwm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                fields.Add(new Field(f.Name, f.DataType, f.IsNullable, meta));
            }
            else
            {
                fields.Add(f);
            }
        }
        return new Schema(fields, columns.Metadata);
    }

    public void CreateTable(string schemaName, string tableName, Schema columns, bool ifNotExists,
                            string? primaryKey, string? uniques, string? defaults,
                            IReadOnlyList<string>? partitionColumns, IReadOnlyList<string>? sortColumns,
                            IReadOnlyList<string>? identityColumns, string? optionsJson)
    {
        RejectFunctionSchemaDdl(schemaName, $"CREATE TABLE {tableName}");
        // Commit 0 itself is metadata-only, but a variant table is unusable without the native paths — fail
        // the CREATE up front with the actionable ATTACH-option error rather than at the first INSERT/SELECT.
        EnsureVariantWritable(columns);
        // CREATE TABLE ... WITH (...): per-table create-flag overrides + delta.*/fabricator.* properties.
        // Write-TUNING keys are per-statement and an empty CREATE writes no data — reject them here rather
        // than silently not applying them (use CTAS, or SET delta_write_options for later INSERTs).
        var withOpts = DeltaWithOptions.Parse(optionsJson);
        if (withOpts is { HasWriteTuning: true })
        {
            throw new System.ArgumentException(
                "WITH write-tuning options (parquet_compression / parquet_row_group_size / "
                + "parquet_bloom_filter_columns) apply to the statement's write — use them on "
                + "CREATE TABLE AS, or SET delta_write_options for later INSERTs.");
        }
        var flags = EffectiveCreateFlags(withOpts);
        columns = WithIdentityMetadata(columns, identityColumns);
        // ⚠ HOIST SLICE 5: a CREATE inside an explicit transaction is now IMMEDIATE — it takes the very
        // same path an autocommit CREATE takes, below. The buffered form is gone (it used to park the
        // schema here and have the flush create the table at COMMIT). The transaction records only that it
        // OWNS the table, so ROLLBACK may drop it; see DeltaTxnBuffer.PendingAppends.CreatedInTxn for the
        // trade this accepts and what it buys.
        //
        // ⚠ THE ORDER IS LOAD-BEARING: the mark is set only AFTER the create below has actually landed.
        // Setting it first would let a FAILED create leave a transaction believing it owns a table it never
        // made, and ROLLBACK would then drop whatever happens to be at that path — someone else's table.
        long createTxn = AmbientTransaction.Current;
        bool markCreated = _txnBuffer.IsExplicit(createTxn)
                           && !TableExists(TablePath(schemaName, tableName));
        ThrowIfPendingAppends(TablePath(schemaName, tableName), "CREATE (OR REPLACE) TABLE");
        // sortColumns (native SORTED BY): persisted as fabricator.sortedBy — every append then re-applies
        // the ORDER BY (see BulkInsert), keeping the table's files in the clustered layout.
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
            // (metadata already attached above — WithIdentityMetadata)
        }
        DeltaWriter.Create(Opener(), TablePath(schemaName, tableName), columns, default,
                              deletionVectors: flags.DeletionVectors,
                              inCommitTimestamps: flags.InCommitTimestamps,
                              changeDataFeed: flags.ChangeDataFeed,
                              rowTracking: flags.RowTracking,
                              // tablePath: null — the table does not exist yet, so there is no persisted
                              // declaration to read. A CREATE OR REPLACE deliberately does NOT inherit the
                              // replaced table's tuning either: the statement defines the new table.
                              spec: ApplyWithOptions(
                                  ResolveWriteSpec(partitionColumns, schemaModeArg: null, tablePath: null),
                                  withOpts),
                              columnMapping: flags.ColumnMapping, serializable: _serializable,
                              sortedBy: sortColumns);
        if (markCreated)
        {
            // The create landed, so the transaction now owns this table and ROLLBACK may drop it.
            _txnBuffer.GetOrCreate(createTxn, TablePath(schemaName, tableName)).CreatedInTxn = true;
            _log.LogInformation("delta txn {Txn} created {Schema}.{Table} (rollback will drop it)",
                createTxn, schemaName, tableName);
        }
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
    // Buffers are created lazily on the first buffered append; the explicit flag (v60) is recorded so
    // DELETE/UPDATE know whether to BUFFER (explicit BEGIN..COMMIT — atomic, snapshot-isolated) or run the
    // direct per-statement paths (autocommit — unchanged behavior, incl. CDF capture + copy-on-write).
    public void BeginTransaction(bool isExplicit)
    {
        if (isExplicit)
        {
            _txnBuffer.MarkExplicit(AmbientTransaction.Current);
        }
    }

    // Flushes the transaction's buffered appends: ONE atomic Delta commit per table (Delta has no
    // cross-table transaction — a multi-table COMMIT commits per table, sequentially). The host set the
    // ambient opener + txn id before this call.
    public void CommitTransaction()
    {
        long txnId = AmbientTransaction.Current;
        // Release the transaction's snapshot pins FIRST, and unconditionally — a READ-ONLY transaction has
        // no buffered tables and returns just below, yet it pinned a version for every table it scanned.
        // Nothing called Release before this, so the ONLY reclamation was InstantFor's panic
        // `Txns.Clear()` at 4096 entries; since one autocommit statement is one transaction id, that
        // threshold is reached routinely and the clear wipes the pins of transactions still IN FLIGHT.
        // An explicit transaction whose pin vanished then re-captures a NEW instant on its next scan and
        // starts seeing a concurrent writer's commits mid-transaction — snapshot isolation silently broken.
        // Releasing per transaction makes the panic path unreachable in normal operation.
        SnapshotPinning.Release(txnId);
        var tables = _txnBuffer.Remove(txnId);
        if (tables is null)
        {
            return;
        }
        var opener = Opener();
        foreach (var kv in tables)
        {
            var pending = kv.Value;
            try
            {
                // ⚠ `pending.HeldTxn is not null` REPLACED `pending.PendingCdc.Count > 0` (hoist 1b): CDF
                // actions now go straight into the transaction at statement time, so the parked list is
                // always empty and a CDF-ONLY statement (a buffered INSERT on a CDF table, which writes a
                // cdc counterpart and nothing else) would fall through to the plain-append path below and
                // LOSE its cdc actions with no error. The held transaction is the honest signal: it exists
                // exactly when something staged into one.
                //
                // The `PendingCreate` branch that used to precede this one is gone with hoist slice 5: a
                // created table is on storage from its CREATE, so its data is ordinary buffered work.
                if (pending.DeletedByOrdinal.Count > 0 || pending.PendingMetadata is not null
                         || pending.AppTxnVersions.Count > 0 || pending.HeldTxn is not null
                         || pending.PendingIdentityHwm.Count > 0
                         || (pending.HasReads && pending.PinnedVersion is not null
                             && (pending.Files.Count > 0 || pending.Batches.Count > 0)
                             && PendingSerializable(pending, kv.Key)))
                {
                    // Buffered DML and/or a buffered schema change: everything (metaData + protocol upgrade
                    // + DV deletes + appends + post-images) fuses into ONE atomic commit, rebase-checked
                    // against the pinned base version. Under SERIALIZABLE an append-only transaction that
                    // READ the table routes here too (its reads must be checked against concurrent commits
                    // — commit order is logical order); under write_serializable it stays on the blind
                    // fast path below (a deliberate, documented divergence from Spark's deleteRead check
                    // for append-only transactions).
                    FlushDmlTransaction(opener, kv.Key, txnId, pending);
                }
                else if (pending.Files.Count > 0)
                {
                    long v = FlushDeferredFiles(opener, kv.Key, pending.Files);
                    _log.LogInformation("delta txn {Txn} commit {Path}: v{Version} ({Files} file(s), {Rows} row(s))",
                        txnId, kv.Key, v, pending.Files.Count, pending.Rows);
                }
                else if (pending.Batches.Count > 0)
                {
                    long v = DeltaWriter.Write(
                        opener, kv.Key, pending.BatchSchema!, pending.Batches, DeltaWriteMode.Append, default,
                        deletionVectors: _deletionVectorsOnCreate,
                        inCommitTimestamps: _inCommitTimestampsOnCreate,
                        changeDataFeed: _changeDataFeedOnCreate,
                        rowTracking: _rowTrackingOnCreate,
                        spec: ResolveWriteSpec(null, null, kv.Key), nativeWrite: _nativeWrite,
                        columnMapping: _columnMappingMode, serializable: _serializable);
                    _log.LogInformation("delta txn {Txn} commit {Path}: v{Version} ({Rows} buffered row(s))",
                        txnId, kv.Key, v, pending.Rows);
                }
            }
            finally
            {
                // Held table/transaction first: disposing the transaction is the ABORT that reclaims what
                // EW staged during a flush that did not commit, and this finally is the only path that runs
                // when the flush THREW — which is exactly what the flush's `await using` used to cover.
                DisposeHeld(pending);
                DeltaTxnBuffer.DisposeBatches(pending);
            }
        }
    }

    // Discards the transaction's buffers AND reclaims the eagerly-written data files they named.
    //
    // Not committing was always the rollback — a file no version references changes nothing a reader sees —
    // so this was never a correctness gap, only a reclamation one: those bytes used to sit on storage until
    // VACUUM's retention horizon, billed the whole time. EW #52's DiscardDataFilesAsync is the verb for it,
    // and it has to be a verb rather than a disposal: WriteDataFilesAsync hands back a plain list and keeps
    // no handle, because a write is ALLOWED to outlive its call and be committed by a later one. Only the
    // host knows the commit is not coming, and here it does.
    public void RollbackTransaction()
    {
        long txnId = AmbientTransaction.Current;
        SnapshotPinning.Release(txnId); // see CommitTransaction — unconditional, before the early return
        var tables = _txnBuffer.Remove(txnId);
        if (tables is null)
        {
            return;
        }
        foreach (var kv in tables)
        {
            // Abort + release the held EW transaction/table before reclaiming our own eagerly-written files:
            // the abort takes back what EW's OWN writers staged (a deletion vector, a CDF file), which is a
            // disjoint set from the host-written data files DiscardBufferedFiles names. In slice 1a nothing
            // is held on this path — the flush is the only creator and it never ran — so this is inert here
            // and becomes load-bearing at 1b.
            DisposeHeld(kv.Value);
            int reclaimed = DiscardBufferedFiles(kv.Key, kv.Value);
            _log.LogInformation(
                "delta txn {Txn} rollback {Path}: discarded {Rows} buffered row(s), reclaimed {Reclaimed} of "
                + "{Files} written file(s)",
                txnId, kv.Key, kv.Value.Rows, reclaimed, kv.Value.Files.Count);
            DeltaTxnBuffer.DisposeBatches(kv.Value);
            // ⚠ HOIST SLICE 5's OTHER HALF, and it must not ship without it: the create is immediate now, so
            // a rolled-back CREATE has already put a table on storage. Dropping it is BEST EFFORT by design
            // — rollback is the failure path, and a throw here would replace the user's real error with a
            // cleanup error. A failure therefore leaves an EMPTY table behind and says so, loudly enough to
            // be actionable, because "the rollback silently left a table" is the one outcome nobody could
            // diagnose. This is the accepted regression in docs/delta-transaction-hoist.md §3.
            //
            // ⚠ Ordering: AFTER the file reclamation above, because DiscardBufferedFiles OPENS the table to
            // do its work — dropping first would make it fail on every rolled-back created table.
            if (kv.Value.CreatedInTxn)
            {
                DropCreatedOnRollback(txnId, kv.Key);
            }
        }
    }

    /// <summary>
    /// Drops a table this transaction CREATED, on ROLLBACK. Best effort: never throws.
    ///
    /// <para>Unlike the autocommit-CTAS orphan refused in
    /// <c>docs/delta-transactions.md</c> §7.1, the authority to destroy is present here — the user typed
    /// ROLLBACK, and the table exists only because this same transaction created it moments ago. That is the
    /// distinction, not atomicity: <c>DROP TABLE</c> is the same unconditional recursive folder delete and we
    /// ship it.</para>
    ///
    /// <para>⚠ It can still lose a concurrent writer's rows — another session may have INSERTed into the
    /// table while it was visible. That window is the accepted price of the immediate create (§3), and it is
    /// the reason this logs what it did rather than staying quiet.</para>
    /// </summary>
    private void DropCreatedOnRollback(long txnId, string path)
    {
        try
        {
            RemoveTableFolder(path);
            _log.LogInformation("delta txn {Txn} rollback {Path}: dropped the table this transaction created",
                txnId, path);
        }
        catch (System.Exception ex)
        {
            // Name the residue explicitly. An empty committed table nobody asked for is confusing precisely
            // because nothing else in the session mentions it.
            _log.LogWarning(ex,
                "delta txn {Txn} rollback {Path}: could not drop the table this transaction created — an "
                + "EMPTY table is left at that path; drop it manually",
                txnId, path);
        }
    }

    /// <summary>
    /// Deletes the data files a rolled-back table buffer wrote. Returns how many were handed to EW for
    /// deletion — 0 when there was nothing to reclaim, or when the attempt failed.
    ///
    /// <para><b>Never throws.</b> Rollback is already the failure path; an exception here would replace the
    /// user's real error with a cleanup error, and leaving an orphan behind is exactly the status quo this
    /// improves on — strictly no worse than before. That is also EW's own posture in
    /// <c>DeltaTransaction.AbortAsync</c>.</para>
    ///
    /// <para><b>⚠ The throw it most plausibly swallows is a REFERENCED file.</b> DiscardDataFilesAsync
    /// refuses a file the table names, read from a FRESH log rather than a cached snapshot, and validates
    /// the whole list before deleting any of it — so a mistake costs nothing and reclaims nothing. Ours are
    /// uncommitted by construction, but the immediate-by-design operations (identity creates, CREATE OR
    /// REPLACE, partition overwrite) commit inside the transaction, so treating "referenced" as impossible
    /// would be an assumption about a list we do not fully own. Let EW decide and log what it says.</para>
    /// </summary>
    private int DiscardBufferedFiles(string tablePath, DeltaTxnBuffer.PendingAppends pending)
    {
        if (pending.Files.Count == 0)
        {
            return 0;
        }
        try
        {
            var fs = TableFileSystems.Create(Opener(), tablePath);
            var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options()).GetAwaiter().GetResult();
            try
            {
                table.DiscardDataFilesAsync(pending.Files).GetAwaiter().GetResult();
                return pending.Files.Count;
            }
            finally
            {
                table.DisposeAsync().GetAwaiter().GetResult();
            }
        }
        catch (System.Exception ex)
        {
            _log.LogWarning(
                "delta rollback {Path}: could not reclaim {Files} written file(s) ({Reason}) — they remain as "
                + "invisible orphans for VACUUM, which is the behaviour that predates this cleanup",
                tablePath, pending.Files.Count, ex.Message);
            return 0;
        }
    }

    // Commits transaction-deferred streamed files as ONE Delta commit, with the standard OCC retry
    // (appends are snapshot-independent, so reopening at the new latest and re-committing is safe).
    private long FlushDeferredFiles(nint opener, string tablePath,
                                    System.Collections.Generic.IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile> files)
        => FlushDeferredFilesAsync(opener, tablePath, files).GetAwaiter().GetResult();

    private static async Task<long> FlushDeferredFilesAsync(nint opener, string tablePath,
                                    System.Collections.Generic.IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile> files)
    {
        // Cancel the COMMIT phase on query interrupt (Ctrl+C during a slow COMMIT / a spinning OCC retry loop) —
        // safe: a cancel before the log commit lands leaves invisible orphan files (VACUUM reclaims them), i.e.
        // it degrades to a rollback. Opener is fresh (CommitTransaction set it). See docs/cancellation.md.
        using var interrupt = new InterruptScope(opener);
        var token = interrupt.Token;
        const int maxAttempts = 16;
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested(); // break out of the retry loop on interrupt
            var fs = TableFileSystems.Create(opener, tablePath);
            var table = await EngineeredWood.DeltaLake.Table.DeltaTable.OpenAsync(fs, DeltaWriter.Options(), token)
                .ConfigureAwait(false);
            try
            {
                return await table.CommitDataFilesAsync(files, DeltaWriteMode.Append, cancellationToken: token)
                    .ConfigureAwait(false);
            }
            catch (EngineeredWood.DeltaLake.DeltaConflictException) when (attempt < maxAttempts)
            {
                // Concurrent writer took the version — reopen + retry. LOGGED (like the sibling retry in
                // DeltaGlobalTableFunction.WriteAsync) because a silent retry makes multi-writer behaviour
                // unobservable: a successful concurrent run and a run whose writers merely serialized look
                // identical from the outside, so there is no way to tell whether the commit guard was ever
                // exercised. This is the one signal that says the put-if-absent actually rejected a commit.
                _log.LogWarning("delta flush {Path}: commit conflict — reopening at latest (attempt {Attempt}/{Max})",
                    tablePath, attempt, maxAttempts);
            }
            finally
            {
                await table.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Buffers an ALTER ADD COLUMN (explicit transactions): the compute-only EW step yields the metaData
    // (+ protocol upgrade) actions and the new schema; nothing is committed until the transaction's fused
    // flush. Requires NO buffered data changes yet (add columns BEFORE the data statements — writes then
    // run schema-overridden; changing the schema under already-buffered rows/post-images is unsupported).
    // Shared start of every buffered schema change: guard the order rule (ALTERs before the transaction's
    // data statements — buffered rows/post-images were built under the pre-ALTER schema) and pin the base.
    private DeltaTxnBuffer.PendingAppends BeginSchemaChange(long txnId, string path,
                                                            in DeltaReader.TxnDmlProfile profile)
    {
        var pending = _txnBuffer.GetOrCreate(txnId, path);
        if (pending.Rows > 0 || pending.DeletedByOrdinal.Count > 0)
        {
            throw new System.NotSupportedException(
                "delta: ALTER TABLE after buffered data changes in this transaction is not supported — "
                + "COMMIT first (or run the schema changes BEFORE the data statements).");
        }
        pending.PinnedVersion ??= SnapshotPinning.TryGetPinned(txnId, path) ?? profile.Version;
        return pending;
    }

    private void StoreSchemaChange(DeltaTxnBuffer.PendingAppends pending,
                                   in EngineeredWood.DeltaLake.Table.DeltaTable.DeferredSchemaChange change,
                                   string op)
    {
        pending.PendingMetadata = change.Metadata;
        pending.PendingProtocol = MergeProtocol(pending.PendingProtocol, change.ProtocolUpgrade);
        pending.PendingDeltaSchema = change.NewSchema;
        // Transport form: the overlay serves bind schemas, which cross the C ABI (variant = tagged binary).
        pending.PendingArrowSchema = VariantMarker.ToTransportSchema(
            EngineeredWood.DeltaLake.Schema.SchemaConverter.ToArrowSchema(change.NewSchema));
        pending.HasAlter = true;
        pending.AlterOps.Add(op);
    }

    private void BufferAddColumn(long txnId, string path, string schemaName, string tableName,
                                 Field column, int flags)
    {
        var opener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(opener, path);
        var pending = BeginSchemaChange(txnId, path, profile);
        // IF NOT EXISTS against the effective (pending ?? committed) schema.
        var baseArrow = pending.PendingArrowSchema ?? DeltaReader.GetSchema(opener, path);
        foreach (var existing in baseArrow.FieldsList)
        {
            if (string.Equals(existing.Name, column.Name, System.StringComparison.Ordinal))
            {
                if ((flags & 1) != 0)
                {
                    return; // IF NOT EXISTS — no-op
                }
                throw new System.InvalidOperationException($"Column '{column.Name}' already exists.");
            }
        }
        var change = DeltaReader.ComputeSchemaChange(opener, path,
            tbl => tbl.ComputeAddColumn(column, pending.PendingMetadata, pending.PendingProtocol));
        StoreSchemaChange(pending, change, "ADD COLUMNS");
        _log.LogInformation("delta txn {Txn} buffer add-column {Schema}.{Table}: {Column} pinned=v{Pin}",
            txnId, schemaName, tableName, column.Name, pending.PinnedVersion);
    }

    private void BufferRenameColumn(long txnId, string path, string schemaName, string tableName,
                                    string oldName, string newName)
    {
        var opener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(opener, path);
        var pending = BeginSchemaChange(txnId, path, profile);
        var change = DeltaReader.ComputeSchemaChange(opener, path,
            tbl => tbl.ComputeRenameColumn(oldName, newName, pending.PendingMetadata));
        // A renamed PARTITION column would break the flush (the partition split runs against the committed
        // partition columns while the batches carry the new name) — keep it on the immediate path.
        foreach (var pc in change.Metadata.PartitionColumns)
        {
            if (string.Equals(pc, newName, System.StringComparison.Ordinal))
            {
                throw new System.NotSupportedException(
                    "delta: RENAME of a partition column inside an explicit transaction is not supported "
                    + "— run it in autocommit.");
            }
        }
        // Rename-map composition (pending name -> committed name), only when the origin is a real
        // committed column (a renamed pending-ADDed column reads as NULL backfill either way).
        var committed = DeltaReader.GetSchema(opener, path);
        string origin = pending.RenameMap.TryGetValue(oldName, out var o) ? o : oldName;
        pending.RenameMap.Remove(oldName);
        foreach (var fl in committed.FieldsList)
        {
            if (string.Equals(fl.Name, origin, System.StringComparison.Ordinal))
            {
                pending.RenameMap[newName] = origin;
                break;
            }
        }
        StoreSchemaChange(pending, change, "RENAME COLUMN");
        _log.LogInformation("delta txn {Txn} buffer rename-column {Schema}.{Table}: {Old} -> {New} pinned=v{Pin}",
            txnId, schemaName, tableName, oldName, newName, pending.PinnedVersion);
    }

    private void BufferDropColumn(long txnId, string path, string schemaName, string tableName, string name)
    {
        var opener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(opener, path);
        var pending = BeginSchemaChange(txnId, path, profile);
        var change = DeltaReader.ComputeSchemaChange(opener, path,
            tbl => tbl.ComputeDropColumn(name, pending.PendingMetadata));
        pending.RenameMap.Remove(name);
        StoreSchemaChange(pending, change, "DROP COLUMNS");
        _log.LogInformation("delta txn {Txn} buffer drop-column {Schema}.{Table}: {Column} pinned=v{Pin}",
            txnId, schemaName, tableName, name, pending.PinnedVersion);
    }

    private void BufferAddField(long txnId, string path, string schemaName, string tableName,
                                System.Collections.Generic.IReadOnlyList<string> containerPath, Field field)
    {
        var opener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(opener, path);
        var pending = BeginSchemaChange(txnId, path, profile);
        var change = DeltaReader.ComputeSchemaChange(opener, path,
            tbl => tbl.ComputeAddField(containerPath, field, pending.PendingMetadata, pending.PendingProtocol));
        StoreSchemaChange(pending, change, "ADD COLUMNS");
        _log.LogInformation("delta txn {Txn} buffer add-field {Schema}.{Table}: {Path}.{Field} pinned=v{Pin}",
            txnId, schemaName, tableName, string.Join(".", containerPath), field.Name, pending.PinnedVersion);
    }

    private void BufferDropField(long txnId, string path, string schemaName, string tableName,
                                 System.Collections.Generic.IReadOnlyList<string> fieldPath)
    {
        var opener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(opener, path);
        var pending = BeginSchemaChange(txnId, path, profile);
        var change = DeltaReader.ComputeSchemaChange(opener, path,
            tbl => tbl.ComputeDropField(fieldPath, pending.PendingMetadata));
        StoreSchemaChange(pending, change, "DROP COLUMNS");
        _log.LogInformation("delta txn {Txn} buffer drop-field {Schema}.{Table}: {Path} pinned=v{Pin}",
            txnId, schemaName, tableName, string.Join(".", fieldPath), pending.PinnedVersion);
    }

    // Chained adds in one transaction can each carry a protocol upgrade computed against the same committed
    // base — merge them (max versions, union feature lists) so the single committed protocol action covers
    // every added column's required features.
    private static EngineeredWood.DeltaLake.Actions.ProtocolAction? MergeProtocol(
        EngineeredWood.DeltaLake.Actions.ProtocolAction? a,
        EngineeredWood.DeltaLake.Actions.ProtocolAction? b)
    {
        if (a is null)
        {
            return b;
        }
        if (b is null)
        {
            return a;
        }
        static IReadOnlyList<string>? Union(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (x is null)
            {
                return y;
            }
            if (y is null)
            {
                return x;
            }
            var set = new HashSet<string>(x, System.StringComparer.Ordinal);
            set.UnionWith(y);
            return new List<string>(set);
        }
        return new EngineeredWood.DeltaLake.Actions.ProtocolAction
        {
            MinReaderVersion = System.Math.Max(a.MinReaderVersion, b.MinReaderVersion),
            MinWriterVersion = System.Math.Max(a.MinWriterVersion, b.MinWriterVersion),
            ReaderFeatures = Union(a.ReaderFeatures, b.ReaderFeatures),
            WriterFeatures = Union(a.WriterFeatures, b.WriterFeatures),
        };
    }

    // Reconciles a committed-shape batch to the transaction's pending schema: (1) RENAMEd top-level columns
    // are re-labeled committed->pending (zero copy — else the recursive reconcile would NULL them out);
    // (2) engineered-wood's RECURSIVE schema-evolution reconcile backfills pending-ADDed columns/struct
    // members as typed NULLs and drops pending-DROPped ones at any depth. Used for the codec
    // read-your-writes overlay and the buffered UPDATE's read-back alignment.
    private static RecordBatch ReconcileBatch(RecordBatch batch, Schema target,
                                              IReadOnlyDictionary<string, string>? committedToPending)
    {
        var src = batch;
        if (committedToPending is { Count: > 0 })
        {
            bool any = false;
            var fields = new List<Field>(batch.Schema.FieldsList.Count);
            foreach (var bf in batch.Schema.FieldsList)
            {
                if (committedToPending.TryGetValue(bf.Name, out var pendingName))
                {
                    fields.Add(new Field(pendingName, bf.DataType, bf.IsNullable, bf.Metadata));
                    any = true;
                }
                else
                {
                    fields.Add(bf);
                }
            }
            if (any)
            {
                var cols = new List<IArrowArray>(batch.ColumnCount);
                for (int i = 0; i < batch.ColumnCount; i++)
                {
                    cols.Add(batch.Column(i));
                }
                src = new RecordBatch(new Schema(fields, batch.Schema.Metadata), cols, batch.Length);
            }
        }
        return EngineeredWood.DeltaLake.Table.DeltaTable.ReconcileBatchToFields(src, target.FieldsList);
    }

    // The rename map inverted for the read direction (committed name -> pending name).
    private static IReadOnlyDictionary<string, string>? CommittedToPending(DeltaTxnBuffer.PendingAppends pending)
    {
        if (pending.RenameMap.Count == 0)
        {
            return null;
        }
        var rev = new Dictionary<string, string>(pending.RenameMap.Count, System.StringComparer.Ordinal);
        foreach (var kv in pending.RenameMap)
        {
            rev[kv.Value] = kv.Key;
        }
        return rev;
    }

    private static async System.Collections.Generic.IAsyncEnumerable<RecordBatch> ReconcileToSchema(
        System.Collections.Generic.IAsyncEnumerable<RecordBatch> source, Schema target,
        IReadOnlyDictionary<string, string>? committedToPending,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in source.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return ReconcileBatch(batch, target, committedToPending);
        }
    }

    // Any rowid ordinal at/above this is a synthetic pending-row id (buffered batches overlay at 0x700000,
    // buffered streamed files at 0x780000) — buffered DML can only target COMMITTED rows (modifying rows
    // inserted in the same transaction is a later slice).
    private const long PendingOrdinalBase = 0x700000;
    // Pending eagerly-written FILES enter the native per-file scan with ordinals 0x780000+idx (idx =
    // index into pending.Files) — same-transaction DELETEs of their rows key DeletedByOrdinal there.
    private const long PendingFileOrdinalBase = 0x780000;
    private const long RowIdPosMask = (1L << TransientRowAddress.PositionBits) - 1;

    /// <summary>
    /// Ordinal → log <c>add.path</c> for one snapshot: the decode half of the transient row id, so that what
    /// crosses into engineered-wood is a self-describing <see cref="FileRowSelection"/> rather than an integer
    /// whose meaning depends on both sides reproducing the same sort against the same version.
    /// <see cref="DeltaTable.PlanFiles"/> is deliberately the single source of the ordering — it is the same
    /// planner the reader that MINTED these ordinals goes through (<c>BuildNativeScanListAsync</c>), so the
    /// encode and decode cannot drift. Called unfiltered, so ordinals are dense and every active file appears.
    /// </summary>
    private static Dictionary<int, string> PathsByOrdinal(
        DeltaTable table, EngineeredWood.DeltaLake.Snapshot.Snapshot snapshot)
    {
        var plan = table.PlanFiles(snapshot: snapshot);
        var map = new Dictionary<int, string>(plan.Count);
        foreach (var planned in plan)
        {
            map[planned.FileOrdinal] = planned.File.Path;
        }
        return map;
    }

    // Eligibility for buffered (explicit-transaction) DML. Never silently non-atomic: an ineligible shape
    // ERRORS with the autocommit escape hatch instead of falling back to an immediate commit.
    private void EnsureBufferedDmlEligible(in DeltaReader.TxnDmlProfile p, string op, bool forUpdate)
    {
        if (!p.DvEnabled)
        {
            // ⚠ Do NOT say "run it in autocommit" here. Since MERGE landed, buffering is ALSO forced for a
            // merge carrying two or more UPDATE/DELETE actions EVEN IN AUTOCOMMIT — because those actions share
            // one scan's row addresses and a copy-on-write delete would renumber the rows the other action
            // already addressed. So this message is reachable on a statement that IS in autocommit, where
            // advising autocommit is both wrong and baffling. State the requirement; name both ways out.
            throw new System.NotSupportedException(
                $"delta: {op} requires deletion vectors on the table when it is buffered (this table has them "
                + "disabled). It is buffered inside an explicit transaction, and for a MERGE carrying more than "
                + "one UPDATE/DELETE action — two such actions cannot be applied one at a time without risking "
                + "the wrong row. Enable deletion vectors on the table, or use at most one UPDATE/DELETE action "
                + "per merge outside a transaction.");
        }
        if (p.CdfEnabled && !p.SupportsExternalCommit)
        {
            throw new System.NotSupportedException(
                $"delta: {op} inside an explicit transaction is not supported on an identity/IcebergCompat "
                + "Change-Data-Feed table yet — run it in autocommit (full CDF capture applies there).");
        }
        if (forUpdate && !p.SupportsExternalCommit)
        {
            throw new System.NotSupportedException(
                "delta: UPDATE inside an explicit transaction is not supported on identity/IcebergCompat "
                + "tables — run it in autocommit.");
        }
        // Materialized row tracking (implied by row tracking) × partitioned UPDATE: supported since the
        // WriteDataFilesAsync partition
        // split learned to carry materialized ids (the id column rides the split).
    }

    // Buffers a DELETE (or an UPDATE's old-row half): decode each transient rowid into (pinned-snapshot file
    // ordinal, absolute position) and accumulate per file. The pin is the version the DML's scan read
    // (SnapshotPinning on native_read; else the current version — the flush conflict-aborts if it moved).
    private long BufferDeleteRows(long txnId, string path, string schemaName, string tableName,
                                  IReadOnlyCollection<long> ids, bool forUpdate)
    {
        var delOpener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(delOpener, path);
        EnsureBufferedDmlEligible(profile, forUpdate ? "UPDATE" : "DELETE", forUpdate);
        var pending = _txnBuffer.GetOrCreate(txnId, path);
        if (profile.CdfEnabled && pending.PendingMetadata is not null)
        {
            throw new System.NotSupportedException(
                "delta: DML on a Change-Data-Feed table cannot follow a buffered ALTER in the same "
                + "transaction — COMMIT the ALTER first.");
        }
        pending.PinnedVersion ??= SnapshotPinning.TryGetPinned(txnId, path) ?? profile.Version;
        if (profile.CdfEnabled && !forUpdate && ids.Count > 0)
        {
            // slice C2: the deleted rows' CONTENT goes into an eager _change_data file (the position set
            // parked below has no row values; this is the one extra read CDF costs a buffered DELETE).
            // Fully STREAMING: read-back -> per-batch cdc write, one batch in flight — a huge CDF DELETE
            // never materializes its matched rows. (EW master's read-back already yields USER columns
            // only — no trailing rowid to drop.)
            WriteCdcFiles(delOpener, path, pending,
                DisposeAfterUse(DeltaReader.ReadRowsByRowIds(delOpener, path, ids, default,
                    atVersion: pending.PinnedVersion, nativeRead: _nativeRead)),
                "delete");
        }
        long added = 0;
        foreach (var rid in ids)
        {
            long ordinal = TransientRowAddress.FileOrdinal(rid);
            if (ordinal >= PendingOrdinalBase && ordinal < PendingFileOrdinalBase)
            {
                // In-memory pending BATCH rows (identity-under-ALTER / iceberg fallbacks) — practically
                // unreachable on DML-eligible tables now that appends eager-write to files.
                throw new System.NotSupportedException(
                    $"delta: {(forUpdate ? "UPDATE" : "DELETE")} of rows inserted in the same transaction "
                    + "is not supported yet — COMMIT the inserts first.");
            }
            if (ordinal >= PendingFileOrdinalBase)
            {
                // Same-transaction DML lift (C3): the row lives in one of THIS transaction's eagerly
                // written pending files — its add will be born with an inline deletion vector at flush.
                // The 0x780000+idx ordinal keys slot straight into DeletedByOrdinal: the native reader's
                // pending-file exclusion matches them, and the flush splits them off to
                // CommitDataFilesAsync(deletedPositionsByFileIndex). CDF is guarded (the insert-cdc
                // file was already written with the row); UPDATE of pending rows stays guarded.
                if (forUpdate)
                {
                    throw new System.NotSupportedException(
                        "delta: UPDATE of rows inserted in the same transaction is not supported yet — "
                        + "COMMIT the inserts first.");
                }
                if (pending.CdfEnabled == true || profile.CdfEnabled)
                {
                    throw new System.NotSupportedException(
                        "delta: DELETE of rows inserted in the same transaction is not supported on a "
                        + "Change-Data-Feed table (their insert was already captured in the feed) — "
                        + "COMMIT the inserts first.");
                }
            }
            if (!pending.DeletedByOrdinal.TryGetValue((int)ordinal, out var set))
            {
                set = new HashSet<long>();
                pending.DeletedByOrdinal[(int)ordinal] = set;
            }
            if (set.Add(TransientRowAddress.Position(rid)))
            {
                added++;
            }
        }
        if (forUpdate)
        {
            pending.HasUpdate = true;
        }
        else
        {
            pending.HasDelete = true;
        }
        _log.LogInformation("delta txn {Txn} buffer {Op} {Schema}.{Table}: rows={Rows} pinned=v{Pin}",
            txnId, forUpdate ? "update(old rows)" : "delete", schemaName, tableName, added, pending.PinnedVersion);
        return added;
    }

    // Buffers an UPDATE: the old rows join the pending deletion positions; the post-image rows (matched rows
    // read back with the SET values substituted) join the pending append batches — both flush in
    // the ONE fused commit. Rows inserted in the same transaction are rejected (later slice).
    private long BufferUpdateRows(long txnId, string path, string schemaName, string tableName,
                                  UpdateInput updates, int[] setSlotByColumn, Schema userSchema)
    {
        var opener = Opener();
        var profile = DeltaReader.GetTxnDmlProfile(opener, path);
        EnsureBufferedDmlEligible(profile, "UPDATE", forUpdate: true);
        foreach (var rid in updates.RowByRid.Keys)
        {
            if ((TransientRowAddress.FileOrdinal(rid)) >= PendingOrdinalBase)
            {
                throw new System.NotSupportedException(
                    "delta: UPDATE of rows inserted in the same transaction is not supported yet — COMMIT "
                    + "the inserts first.");
            }
        }
        var pending = _txnBuffer.GetOrCreate(txnId, path);
        pending.PinnedVersion ??= SnapshotPinning.TryGetPinned(txnId, path) ?? profile.Version;

        var fields = userSchema.FieldsList;
        // Pending ALTER: the read-back batches carry only the COMMITTED columns — reconcile each to the
        // pending shape so the substitution loop's positional indexing aligns. (EW master's read-back
        // yields USER columns only; the rowid correlation key rides the rowIdsOut out-param.)
        Schema? readTarget = pending.PendingMetadata is not null ? userSchema : null;
        if (profile.CdfEnabled && pending.PendingMetadata is not null)
        {
            throw new System.NotSupportedException(
                "delta: DML on a Change-Data-Feed table cannot follow a buffered ALTER in the same "
                + "transaction — COMMIT the ALTER first.");
        }
        long matched = 0;
        // ── GROUPED FLUSH: the read-back streams, it is not accumulated ────────────────────────────────
        // Post-images used to pile up for the WHOLE statement before anything was written (~474 MB for a
        // 1M-row UPDATE, and the pre-images on top of that on a CDF table). Now a group's worth becomes data
        // files — and its CDF pair change files — as soon as it is big enough, and only the WrittenDataFile /
        // CdcFile actions park on the buffer. Still exactly ONE commit at flush.
        //
        // ⚠ FILE LAYOUT IS UNCHANGED BY CONSTRUCTION: WriteDataFilesAsync writes one parquet file per (input
        // batch × partition), so N read-back batches yield N data files however they are grouped.
        //
        // ⚠ THE ALL-OR-NOTHING ROW-ID RULE MUST BE DECIDED BEFORE READING, because a group is written before
        // the later groups' ids are known. TxnDmlProfile.AllFilesRowTracked answers it for free in the probe
        // this method already does — and it is trusted ONLY when the read-back's pinned version is the one it
        // describes, since a different version has a different file set. Where it cannot be established the
        // threshold is DISABLED and the statement buffers whole, exactly as it did before: a legacy table
        // (files predating row tracking) keeps its old behaviour rather than acquiring new semantics from a
        // memory fix.
        bool idsPreResolved = profile.MaterializeRowIds && profile.AllFilesRowTracked
                              && pending.PinnedVersion == profile.Version;
        long groupBytes = !profile.MaterializeRowIds || idsPreResolved
            ? DeltaReader.UpdateGroupBytes
            : long.MaxValue;

        var postGroup = new List<RecordBatch>();
        var preGroup = profile.CdfEnabled ? new List<RecordBatch>() : null;
        // Materialized row tracking (implied by row tracking — the table declares the materialized
        // columns): the post-image rows must carry their ORIGINAL stable ids in the declared
        // __delta_row_id column (identity preserved across the UPDATE — Spark's reference behavior).
        // Resolved BY THE READ-BACK per row: the source file's materialized value where present (a
        // compacted / CoW-rewritten source — the post-OPTIMIZE case) else baseRowId + position.
        List<long?>? idGroup = null;
        List<(long?[] Ids, long?[] Versions)>? srcTracking = null;
        if (profile.MaterializeRowIds)
        {
            idGroup = new List<long?>();
            srcTracking = new List<(long?[] Ids, long?[] Versions)>();
        }
        long groupAccum = 0;
        bool idsUnresolvable = false;
        bool wroteWithIds = false;
        // Null until the first flush decides it; false pins the whole statement to the batch park (the
        // verdict depends on table capabilities, not on the batches, so it cannot differ between groups).
        bool? eager = null;
        pending.BatchSchema ??= userSchema;

        void FlushGroup()
        {
            if (postGroup.Count == 0)
            {
                return;
            }
            // Every id resolvable => bake the originals; ANY unresolvable row (a pre-row-tracking source
            // file) => write the post-images WITHOUT materialized ids (fresh ids for the whole statement —
            // never a wrong or colliding id).
            // The seam's parameter is nullable per element (an entry it cannot derive is written as null, and
            // the reader then falls back to baseRowId + position for that row alone). We deliberately do NOT
            // use that: a partially-materialised statement would leave identity depending on which rows
            // happened to resolve, so the gate is all-or-nothing and the list handed over never has a null.
            List<long?>? stableIds = null;
            if (idGroup is not null && !idsUnresolvable)
            {
                stableIds = new List<long?>(idGroup);
                wroteWithIds = true;
            }
            else if (wroteWithIds)
            {
                // Unreachable unless AllFilesRowTracked lied about the pinned version's files. Loud, because
                // the alternative is a statement whose earlier rows kept their identity and whose later rows
                // silently did not.
                throw new System.InvalidOperationException(
                    "delta: buffered UPDATE hit an unresolvable row id after post-images had already been "
                    + "written with their original ids — the pinned snapshot's files disagree about row "
                    + "tracking.");
            }

            if (preGroup is not null)
            {
                // slice C2: eager CDC capture — pre-images (committed values, read back above) + the
                // post-images built from the SET substitution, exactly the autocommit merge-on-read pair.
                // Both go through the pair's HELD table and transaction, so a group costs no extra open.
                WriteCdcFiles(opener, path, pending, preGroup, "update_preimage");
                WriteCdcFiles(opener, path, pending, postGroup, "update_postimage");
            }
            // Eager post-image write (eager-write plan, slices A + C1): the post-image rows become a
            // parquet file NOW — the merge-on-read shape with a deferred commit — and only the
            // WrittenDataFile action parks on the buffer. Native_write AND codec catalogs both (the codec
            // writes via EW's own writer). The pending-FILES read overlay (ScanNative routing) serves
            // read-your-writes; ROLLBACK reclaims the bytes via DiscardDataFilesAsync. NOT NULL is
            // validated inside the helper (the flush only validates Batches).
            // ⚠ This OPENS THE TABLE per call, so grouping costs one extra _delta_log LIST per group. Cheap
            // next to the read-back it bounds, and removable: the pair already holds an open table
            // (EnsureHeldTableAsync) that this helper predates and could use instead.
            eager = TryEagerWriteBatches(opener, path, pending, postGroup, tableName,
                                         materializedRowIds: stableIds);
            if (eager == true)
            {
                foreach (var b in postGroup)
                {
                    b.Dispose();
                }
            }
            else if (stableIds is not null)
            {
                // A materialize post-image MUST carry its original ids — the batch-park flush path cannot
                // bake them, so an eager-write failure here is a hard error, not a fallback.
                throw new System.NotSupportedException(
                    "delta: UPDATE inside an explicit transaction on a materialized-row-tracking table "
                    + "could not write its post-images eagerly — run it in autocommit.");
            }
            else
            {
                // Parked, not written: memory is then unbounded for this table shape exactly as before.
                pending.Batches.AddRange(postGroup);
            }
            postGroup.Clear();
            preGroup?.Clear();
            idGroup?.Clear();
            groupAccum = 0;
            // Should be FLAT across groups. A climbing heap here means something is still retaining them —
            // and note the park branch above retains by design, so check `eager` before calling it a leak.
            MemoryProbe.Mark("delta buffered update: group flushed", matched);
        }

        // The rowids' ordinals are PINNED-version path-sort positions — read back AT that version so a
        // concurrent commuting append (which shifts the ordering) can never make us read the wrong files.
        // Both out-lists are drained per batch (their producer only ever appends, never reads them back), so
        // the rowids and source tracking do not accumulate across the statement either.
        var ridsPerBatch = new List<long[]>();
        foreach (var raw in DeltaReader.ReadRowsByRowIds(opener, path, updates.RowByRid.Keys, default,
                     atVersion: pending.PinnedVersion, sourceTrackingOut: srcTracking,
                     rowIdsOut: ridsPerBatch, nativeRead: _nativeRead))
        {
            var batch = readTarget is null ? raw : ReconcileBatch(raw, readTarget, CommittedToPending(pending));
            var rids = ridsPerBatch[ridsPerBatch.Count - 1];
            ridsPerBatch.Clear();
            var srcIds = srcTracking is { Count: > 0 } ? srcTracking[srcTracking.Count - 1].Ids : null;
            srcTracking?.Clear();
            preGroup?.Add(batch);
            var newCols = new IArrowArray[fields.Count];
            for (int c = 0; c < fields.Count; c++)
            {
                int slot = setSlotByColumn[c];
                if (slot < 0)
                {
                    newCols[c] = batch.Column(c); // unchanged column (batch owns its buffers — safe to alias)
                    continue;
                }
                var values = new List<object?>(batch.Length);
                for (int i = 0; i < batch.Length; i++)
                {
                    long rid = i < rids.Length ? rids[i] : -1;
                    // The SET value now comes out of the parsed Arrow batch rather than a retained box; the
                    // read is the same ReadScalarDeep call, one row at a time, nothing held.
                    values.Add(updates.RowByRid.TryGetValue(rid, out int ur)
                        ? updates.Value(ur, slot)
                        : ArrowValueReader.ReadScalarDeep(batch.Column(c), i));
                }
                newCols[c] = BuildArray(fields[c].DataType, values);
            }
            var post = new RecordBatch(userSchema, newCols, batch.Length);
            postGroup.Add(post);
            matched += batch.Length;
            groupAccum += DeltaReader.ApproxBatchBytes(batch) + DeltaReader.ApproxBatchBytes(post);
            for (int i = 0; i < batch.Length; i++)
            {
                // One entry per row of this batch by the seam's contract; the bounds check is the only guard
                // needed now that the ids arrive as a plain long[] rather than a nullable Arrow array.
                if (i < rids.Length)
                {
                    long rid = rids[i];
                    long ordinal = TransientRowAddress.FileOrdinal(rid);
                    if (!pending.DeletedByOrdinal.TryGetValue((int)ordinal, out var set))
                    {
                        set = new HashSet<long>();
                        pending.DeletedByOrdinal[(int)ordinal] = set;
                    }
                    set.Add(TransientRowAddress.Position(rid));
                    if (idGroup is not null)
                    {
                        long? id = srcIds is not null && i < srcIds.Length ? srcIds[i] : null;
                        if (id is null) { idsUnresolvable = true; }
                        idGroup.Add(id);
                    }
                }
            }
            // Once a group has parked rather than written, grouping buys nothing — keep accumulating so the
            // park path stays byte-identical to what it always did.
            if (groupAccum >= groupBytes && eager != false)
            {
                FlushGroup();
            }
        }
        FlushGroup();
        pending.Rows += matched;
        pending.HasUpdate = true;
        _log.LogInformation("delta txn {Txn} buffer update {Schema}.{Table}: rows={Rows} pinned=v{Pin}",
            txnId, schemaName, tableName, matched, pending.PinnedVersion);
        return matched;
    }

    // Eager CDC capture (slice C2): write the _change_data file(s) for a buffered statement NOW — the
    // rows are in hand exactly here — and park only the CdcFile actions (they fuse into the
    // transaction's single commit; ROLLBACK leaves them as invisible orphans). The plural
    // WriteChangeDataFilesAsync splits per partition (data-file convention), so partitioned CDF tables
    // work too.
    private void WriteCdcFiles(nint opener, string tablePath, DeltaTxnBuffer.PendingAppends pending,
                               IEnumerable<RecordBatch> rows, string changeType)
        => WriteCdcFilesAsync(opener, tablePath, pending, rows, changeType).GetAwaiter().GetResult();

    private async Task WriteCdcFilesAsync(nint opener, string tablePath, DeltaTxnBuffer.PendingAppends pending,
                               IEnumerable<RecordBatch> rows, string changeType)
    {
        // Cancel a slow buffered-statement CDC write on interrupt (opener fresh from the DML operator).
        using var interrupt = new InterruptScope(opener);
        var token = interrupt.Token;
        // rows may be a STREAMING source (the DELETE read-back) — one batch in flight; the table and the
        // transaction are obtained lazily on the first non-empty batch, so an empty statement still never
        // touches storage. This is where the hoist pays for itself: the table used to be OPENED AND DISPOSED
        // here, per buffered CDF statement, which cost one _delta_log LIST each time; now it is the pair's
        // held table.
        //
        // StageChangeDataAsync writes the _change_data parquet IMMEDIATELY (eager capture is unchanged — the
        // rows are in hand exactly here) and files the cdc actions into the transaction, replacing the parked
        // pending.PendingCdc list. Sharing the held table is safe even though it carries the native
        // data-file writer and this path used to pass DeltaWriter.Options(): CdfWriter.WriteAsync writes via
        // fs.CreateAsync and never consults _options.DataFileWriter, so change files are EW-codec written
        // either way.
        EngineeredWood.DeltaLake.Table.DeltaTransaction? txn = null;
        foreach (var b in rows)
        {
            if (b.Length == 0)
            {
                continue;
            }
            if (txn is null)
            {
                var table = await EnsureHeldTableAsync(opener, tablePath, pending, token).ConfigureAwait(false);
                pending.PinnedVersion ??= table.CurrentSnapshot.Version;
                var pinnedSnap = await ResolvePinnedSnapshotAsync(
                    table, pending.PinnedVersion.Value, token).ConfigureAwait(false);
                txn = EnsureHeldTxn(pending, table, pinnedSnap, PendingSerializable(pending, tablePath));
            }
            await txn.StageChangeDataAsync(VariantTransport.ToCanonical(b), changeType, token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The snapshot a buffered transaction's work was computed against: deletion-vector positions are keyed by
    /// ITS path-sorted file ordinals and ALTERs are chained against ITS metadata. Resolved explicitly rather
    /// than taken from <c>CurrentSnapshot</c>, which a concurrent writer may have advanced — and against which
    /// the commit's validation would be vacuous.
    /// </summary>
    private static async Task<EngineeredWood.DeltaLake.Snapshot.Snapshot> ResolvePinnedSnapshotAsync(
        EngineeredWood.DeltaLake.Table.DeltaTable table, long pinned,
        System.Threading.CancellationToken token)
        => table.CurrentSnapshot.Version == pinned
            ? table.CurrentSnapshot
            : await table.GetSnapshotAtVersionAsync(pinned, token).ConfigureAwait(false);

    // Streams the read-back, disposing each source batch once the consumer has moved past it
    // (the finally runs when the consumer pulls the next item; enumerator disposal covers early
    // termination) — keeps a huge streaming read at one batch in flight.
    private static IEnumerable<RecordBatch> DisposeAfterUse(IEnumerable<RecordBatch> source)
    {
        foreach (var raw in source)
        {
            try
            {
                yield return raw;
            }
            finally
            {
                raw.Dispose();
            }
        }
    }

    // Eager-write plan, slice C1: write a statement's collected batches to data file(s) NOW (EW's codec
    // writer — or DuckDB's under native_write via the IDataFileWriter seam) and park only the
    // WrittenDataFile actions, so a codec-catalog transaction caps at ONE statement's memory (the
    // codec's own autocommit profile). Mid-transaction reads route through ScanNative once
    // pending.Files is non-empty — the same read_parquet path native_write-without-native_read
    // catalogs already use (and native_read validates broadly against codec-written files). Explicit
    // transactions only (autocommit keeps the byte-identical park-batches → DeltaWriter.Write shape).
    // Returns false (nothing written, batches untouched) for: a pending-created table (nothing on
    // storage to open) or identity/iceberg (no external-commit support). Materialized row tracking
    // catalogs eager-write like everyone: a FRESH append needs no physical __delta_row_id column —
    // readers derive ids from the commit-assigned baseRowId + position (the validated streamed-native
    // behavior, and what the flush's own WriteDataFilesAsync batch path already produces); the
    // materialized column is an override for rows whose ORIGINAL ids must survive a rewrite
    // (compaction / merge-on-read post-images), not a requirement for new rows.
    private bool TryEagerWriteBatches(nint opener, string tablePath, DeltaTxnBuffer.PendingAppends pending,
                                      IReadOnlyList<RecordBatch> batches, string tableName,
                                      IReadOnlyList<long?>? materializedRowIds = null)
        => TryEagerWriteBatchesAsync(opener, tablePath, pending, batches, tableName, materializedRowIds)
            .GetAwaiter().GetResult();

    private async Task<bool> TryEagerWriteBatchesAsync(nint opener, string tablePath, DeltaTxnBuffer.PendingAppends pending,
                                      IReadOnlyList<RecordBatch> batches, string tableName,
                                      IReadOnlyList<long?>? materializedRowIds)
    {
        if (batches.Count == 0)
        {
            return false;
        }
        // Cancel a slow buffered-statement eager data-file write on interrupt (opener fresh from the DML operator).
        using var interrupt = new InterruptScope(opener);
        var token = interrupt.Token;
        // The (DuckDB txn, table) pair's HELD table, not a fresh open. This used to open — and dispose — its
        // own, i.e. one extra `_delta_log` LIST per eager write, which the grouped UPDATE turned into one per
        // GROUP (cheap locally, not on OneLake/S3, and the reason UpdateGroupBytes defaults to 64 MiB rather
        // than 16). Reusing the held table is only correct because EnsureHeldTableAsync now opens with the
        // SAME write spec this call site used to pass; before that fix the two differed in compression /
        // row-group size / bloom filters, so the swap would have silently dropped the user's write tuning
        // from the eager path instead of adding it to the held one.
        var table = await EnsureHeldTableAsync(opener, tablePath, pending, token).ConfigureAwait(false);
        {
            IReadOnlyList<RecordBatch> toWrite = batches;
            bool identity = false;
            if (!table.SupportsExternalDataFileCommit)
            {
                // Identity appends eager-write too: values are generated NOW from the pinned/chained
                // high-water mark (read-your-writes shows the REAL ids, which the batch park never
                // could) and the flush fuses the HWM metaData into the one commit. Concurrent identity
                // consumers abort via the rebase's metadata check (their commit carries metaData) —
                // Spark's own policy. Iceberg and pending-ALTER-on-identity stay on the batch park.
                if (table.IsIcebergCompat || !table.HasIdentityColumns
                    || pending.PendingMetadata is not null)
                {
                    return false;
                }
                identity = true;
                var (gen, marks) = table.GenerateIdentityValues(batches,
                    pending.PendingIdentityHwm.Count > 0 ? pending.PendingIdentityHwm : null);
                toWrite = gen;
                foreach (var kv in marks)
                {
                    pending.PendingIdentityHwm[kv.Key] = kv.Value;
                }
                pending.PinnedVersion ??= table.CurrentSnapshot.Version; // the flush's rebase base
            }
            DeltaNullability.ValidateBatches(toWrite,
                pending.PendingDeltaSchema ?? table.CurrentSnapshot.Schema, tableName);
            pending.Files.AddRange(await table.WriteDataFilesAsync(
                    VariantTransport.ToCanonical(toWrite), token,
                    schemaOverride: pending.PendingDeltaSchema,
                    identityValuesPreGenerated: identity,
                    materializedRowIds: materializedRowIds)
                .ConfigureAwait(false));
            return true;
        }
        // No dispose: the held table belongs to the buffer entry and is released with it (commit, rollback,
        // or any other exit from the pair's life) — disposing it here would pull the table out from under the
        // held transaction and every later statement of this DuckDB transaction.
    }

    // COMMIT flush for a transaction-CREATED table: nothing touched the _delta_log before now. Uses
    // today's autocommit CTAS commit shape (v0 CREATE TABLE + ONE WRITE for all buffered rows; single-
    // commit CTAS = an engineered-wood follow-up). A concurrent same-name create conflict-aborts (commit
    // 0's put-if-absent is the arbiter — the pre-check just gives the clear error).
    // COMMIT flush for a transaction with buffered DML: validate the pinned base version (conflict-ABORT —
    // first-committer-wins snapshot isolation), write the buffered batches as data files (no commit), compute
    // the deletion-vector actions, and commit EVERYTHING as one atomic Delta commit. No retry — the DV
    // positions are snapshot-coupled.
    private void FlushDmlTransaction(nint opener, string tablePath, long txnId,
                                     DeltaTxnBuffer.PendingAppends pending)
        => FlushDmlTransactionAsync(opener, tablePath, txnId, pending).GetAwaiter().GetResult();

    private async Task FlushDmlTransactionAsync(nint opener, string tablePath, long txnId,
                                     DeltaTxnBuffer.PendingAppends pending)
    {
        // Cancel the DML-commit phase on interrupt (a slow buffered write over OneLake/S3, or a spinning
        // OCC/rebase retry loop against a busy concurrent writer). SAFE: a cancel before the log commit lands
        // leaves invisible orphan files (VACUUM reclaims), i.e. degrades to rollback; a cancel is not a
        // DeltaConflictException so it breaks OUT of the retry loop rather than being swallowed as a conflict.
        // Opener fresh from CommitTransaction. See docs/cancellation.md.
        using var interrupt = new InterruptScope(opener);
        var token = interrupt.Token;
        // The high-water mark of a whole explicit transaction: everything every statement parked is still
        // held here. pending.Batches is the term to watch — the eager-write paths leave only ACTIONS behind,
        // so a large heap at this mark means some statement fell back to parking its batches.
        MemoryProbe.Mark("delta flush: begin", pending.Rows);
        // Effective isolation = the TABLE's delta.isolationLevel property (cached on the buffer), NOT the
        // catalog-wide flag — so our OCC check + row-level relaxation conform to the guarantee the table
        // advertises, uniform with Spark. Absent property => WriteSerializable (Spark's default).
        bool tableSer = PendingSerializable(pending, tablePath);
        // The table is now owned by the BUFFER ENTRY, not by this method's scope (hoist slice 1a) — so it is
        // NOT disposed in a finally here; CommitTransaction's per-table finally does it, which is also what
        // preserves the abort-on-exception this method used to get from `await using`.
        var table = await EnsureHeldTableAsync(opener, tablePath, pending, token).ConfigureAwait(false);
        // The bare block is what used to be `try { … } finally { table.DisposeAsync(); }`. Kept as a block
        // ON PURPOSE: removing it would re-indent ~200 lines and bury a 6-line behavioural change in an
        // unreviewable diff. It carries no semantics and can be flattened in a later cosmetic pass.
        {
            long pinned = pending.PinnedVersion!.Value;
            // The transaction's changes were computed against the PINNED snapshot: DV positions are keyed
            // by ITS path-sorted file ordinals, ALTERs chained against ITS metadata. Resolve it explicitly
            // (a concurrent writer may have advanced the table) — the rebase check below decides whether
            // committing on top of the newer snapshot is safe.
            var pinnedSnap = await ResolvePinnedSnapshotAsync(table, pinned, token).ConfigureAwait(false);
            var files = new List<EngineeredWood.DeltaLake.Table.WrittenDataFile>(pending.Files);
            if (pending.Batches.Count > 0)
            {
                DeltaNullability.ValidateBatches(pending.Batches,
                    pending.PendingDeltaSchema ?? pinnedSnap.Schema,
                    tablePath.Substring(tablePath.LastIndexOf('/') + 1));
                files.AddRange(await table.WriteDataFilesAsync(
                        VariantTransport.ToCanonical(pending.Batches), token,
                        schemaOverride: pending.PendingDeltaSchema)
                    .ConfigureAwait(false));
            }
            // Split the delete set: committed-file rows are handed to EW keyed BY PATH — the transient
            // rowid's file ordinal is OUR encoding, so WE own the decode, and PlanFiles is the ONE
            // ordinal->path dictionary, the same planner that minted those ordinals for the reader. That
            // also makes a rowid that does not resolve a loud error here instead of a delete EW silently
            // drops. Pending-file ordinals (0x780000+idx, this transaction's own eager files) stay INDEX
            // keyed: those files are not in any snapshot, so no path can name them — they become inline DVs
            // BORN ON their adds at commit, and their rows never reach a committed version.
            var deletes = new Dictionary<string, IReadOnlyCollection<long>>(
                pending.DeletedByOrdinal.Count, StringComparer.Ordinal);
            Dictionary<int, IReadOnlyCollection<long>>? pendingFileDeletes = null;
            long pendingRowsDeleted = 0;
            Dictionary<int, string>? pinnedPaths = null;
            foreach (var kv in pending.DeletedByOrdinal)
            {
                if (kv.Key >= PendingFileOrdinalBase)
                {
                    pendingFileDeletes ??= new Dictionary<int, IReadOnlyCollection<long>>();
                    pendingFileDeletes[kv.Key - (int)PendingFileOrdinalBase] = kv.Value;
                    pendingRowsDeleted += kv.Value.Count;
                }
                else
                {
                    pinnedPaths ??= PathsByOrdinal(table, pinnedSnap);
                    if (!pinnedPaths.TryGetValue(kv.Key, out var delPath))
                    {
                        throw new System.InvalidOperationException(
                            $"delta buffered DML on '{tablePath}': row-id file ordinal {kv.Key} does not name "
                            + $"an active file of the transaction's pinned version {pinned} "
                            + $"({pinnedPaths.Count} active) — the row identifiers were captured against a "
                            + "different snapshot.");
                    }
                    deletes[delPath] = kv.Value;
                }
            }
            // The transaction is pinned to OUR snapshot, not the table's current one: the DV positions above
            // are keyed by the pinned version's file ordinals and the ALTERs below are chained against its
            // metadata, and what the commit has to be validated against is every commit that landed SINCE
            // that version. From CurrentSnapshot that set would be empty and the validation vacuous.
            //
            // `await using`: a flush that does NOT commit takes back what EW's own writers put on storage
            // during it. MEASURED: a buffered DELETE whose commit is then refused left a
            // `deletion_vector_*.bin` behind — StageRowDeletesAsync writes the vector at STAGING time, so it
            // is on disk before the precondition is judged. Our own eagerly-written DATA files are NOT
            // affected and must not be: they are written before this transaction exists and EW's provenance
            // rule never collects a host-written file, so "rollback leaves invisible orphans for VACUUM"
            // still describes them exactly.
            // ⚠ Safe only from EW #49 onward. #46 introduced the ledger, but CommitOccAsync refreshed the
            // snapshot AFTER the commit json was durable and inside the same try, so a commit that LANDED
            // and then threw still named its live files — disposal would have deleted committed data. #49
            // empties the ledger the instant WriteCommitAsync returns. Do not backport this line.
            // Disposal order: declared inside the try, so it runs BEFORE the finally disposes the table —
            // the cleanup needs the table's filesystem. (EW deliberately tolerates the reverse order too.)
            // Created HERE, at exactly the point the `await using` used to be. Moving it earlier LOOKS free —
            // StartTransaction(pinnedSnap, isolation) is only `new DeltaTransaction(this, snapshot, level)`,
            // it registers nothing on the table and installs no ambient state, and both arguments are already
            // resolved above — but "looks free" is not "is free", and slice 1a's whole claim is byte-identity.
            // Moving it is slice 1b's business, with its own gate.
            //
            // ⚠ Do NOT expect the hoist to reclaim our eagerly-written data files on abort. EW's abort ledger
            // is passed EXPLICITLY (`written:`, as StageChangeDataAsync does) and WriteDataFilesAsync has no
            // such parameter, so a live transaction never collects a file written straight through the table.
            // That is exactly why DiscardDataFilesAsync is a separate verb and why RollbackTransaction calls
            // it independently: reclaiming OUR files stays OUR job at every slice.
            var txn = EnsureHeldTxn(pending, table, pinnedSnap, tableSer);

            // Appends. identityValuesPreGenerated: our eager identity path already put the values in the
            // files, which is what lets an identity table accept externally written ones at all.
            // deletedPositionsByFileIndex: rows this transaction inserted and then deleted — the add is born
            // with an inline DV, so they never reach a committed version.
            if (files.Count > 0)
            {
                // EW keys bornDeleted BY PATH now, so translate our pending-file INDEX here — where `files`
                // exists — rather than keeping an index-keyed surface alive. An index that names no file in
                // this call is a loud error: it would otherwise silently un-delete rows the statement
                // deleted, which reads as data appearing from nowhere.
                RowSelection? bornDeleted = null;
                if (pendingFileDeletes is not null)
                {
                    var byPath = new Dictionary<string, IReadOnlyCollection<long>>(
                        pendingFileDeletes.Count, StringComparer.Ordinal);
                    foreach (var kv in pendingFileDeletes)
                    {
                        if (kv.Key < 0 || kv.Key >= files.Count)
                        {
                            throw new System.InvalidOperationException(
                                $"delta flush: pending-file index {kv.Key} names no file of this commit "
                                + $"({files.Count} written) — the born-deleted rows cannot be placed.");
                        }
                        byPath[files[kv.Key].RelativePath] = kv.Value;
                    }
                    bornDeleted = RowSelection.ByPath(byPath);
                }
                await txn.StageDataFilesAsync(files, bornDeleted,
                        identityValuesPreGenerated: pending.PendingIdentityHwm.Count > 0,
                        cancellationToken: token)
                    .ConfigureAwait(false);
            }

            // Committed-file deletes. Staging (rather than computing the actions ourselves) is what hands the
            // commit loop the per-file row edits it needs to reconcile row-by-row against a concurrent delete
            // of DIFFERENT rows, and to relocate ours by stable id across a concurrent rewrite.
            long rowsDeleted = pendingRowsDeleted;
            if (deletes.Count > 0)
            {
                rowsDeleted += await txn.StageRowDeletesAsync(RowSelection.ByPath(deletes), token)
                    .ConfigureAwait(false);
            }
            // The buffered schema change (metaData + merged protocol upgrade) joins the SAME commit.
            // Eagerly-generated identity high-water marks compose INTO that metaData action (a commit
            // must not carry two metaData actions) — or form their own when there is no buffered ALTER.
            var baseExtra = new List<EngineeredWood.DeltaLake.Actions.DeltaAction>();
            if (pending.PendingProtocol is { } proto)
            {
                baseExtra.Add(proto);
            }
            var metaAction = pending.PendingMetadata;
            if (pending.PendingIdentityHwm.Count > 0)
            {
                metaAction = table.BuildIdentityMetadataAction(pending.PendingIdentityHwm, metaAction);
            }
            if (metaAction is { } meta)
            {
                baseExtra.Add(meta);
            }
            // slice C2 + hoist 1b: the eagerly-written _change_data files are ALREADY in this transaction —
            // WriteCdcFilesAsync staged them at statement time via StageChangeDataAsync, so there is no
            // parked list to append here. (cdc actions carry DataChange=false — concurrent readers'
            // dataChange checks ignore them; rebase safety: if the rebase passes, delete-delete/deleteRead
            // already guaranteed our touched files are unchanged, so the captured CDC content stays exact.)
            if (baseExtra.Count > 0)
            {
                txn.StageActions(baseExtra);
            }
            // Application-transaction versions (idempotent appends): staged as a PAIR (version, expected
            // previous) rather than as a hand-built `txn` action, because the compare-and-set has to be
            // re-validated on every commit attempt. A twin producer running the same batch takes our version;
            // the retry's read-set check then passes — a concurrent append invalidates nothing we read — so
            // without the guard inside the loop the batch would commit a SECOND time.
            foreach (var kv in pending.AppTxnVersions)
            {
                // BEHAVIOUR-PRESERVING mapping onto EW's precondition union. Our documented contract for
                // fabricator_delta_set_transaction_version is "no expected version" == "this producer must
                // have recorded NOTHING yet" — which is Absent, NOT the union's None. Getting that wrong is
                // silent and costly: None writes UNCONDITIONALLY, so a replayed first batch would commit a
                // second time and rewrite the recorded version with the same value, leaving nothing in the
                // table to say it happened. (NotApplied — Delta-Spark's monotonic rule — is a plausible
                // third mode to expose later, but it is a CONTRACT change and does not belong in a bump.)
                txn.RequireAppTransaction(kv.Key, kv.Value.Version,
                    kv.Value.Expected is { } expected
                        ? EngineeredWood.DeltaLake.Table.AppTransactionPrecondition.Exactly(expected)
                        : EngineeredWood.DeltaLake.Table.AppTransactionPrecondition.Absent);
            }
            int kinds = (pending.HasAppend ? 1 : 0) + (pending.HasDelete ? 1 : 0) + (pending.HasUpdate ? 1 : 0)
                        + (pending.HasAlter ? 1 : 0);
            string operation = kinds > 1 ? "TRANSACTION"
                : pending.HasUpdate ? "UPDATE"
                : pending.HasDelete ? "DELETE"
                : pending.HasAlter
                    ? (pending.AlterOps.Count == 1 ? pending.AlterOps.First() : "ALTER TABLE")
                    : "WRITE";
            txn.Operation = operation;

            // Read set: the predicates our in-transaction scans pushed (or a whole-table flag when nothing
            // pushed). The commit loop uses them for the concurrentAppend check — under serializable always,
            // under write_serializable only from non-blind-append commits.
            foreach (var pred in pending.ReadPredicates)
            {
                txn.DeclareRead(pred);
            }
            if (pending.ReadWholeTable)
            {
                txn.DeclareWholeTableRead();
            }

            // ROW-LEVEL DML is exempted from the whole-table read declaration — the opt-in that preserves
            // what we shipped before the bump. A statement whose predicate did not push declares the whole
            // table, and under write_serializable that declaration would meet a concurrent blind append and
            // abort a DELETE/UPDATE that touches disjoint ROWS — the very composition the row-level path
            // exists to allow. It is deliberately not the library default: only a host that resolved its own
            // rows knows the declaration was a scan artefact rather than a real dependency. Under
            // serializable the exemption does not apply at all (EW gates it on the level), which is why the
            // isolation default flip does not silently widen it.
            txn.ExemptRowLevelFromWholeTableRead = true;

            // ONE call replaces what used to be a hand-rolled OCC loop here: conflict-check the read set
            // against every commit landed since the pin, rebase the deletion-vector pairs onto the latest
            // snapshot (re-unioning a concurrent delete of DIFFERENT rows, relocating ours by stable id
            // across a concurrent rewrite), re-derive row-id high-water marks, retry on a lost race, and
            // re-check the idempotent-producer versions on each attempt. Those invariants — re-rebase from
            // the ORIGINAL staged actions, never from a prior attempt's — are engineered-wood's to keep now.
            try
            {
                long v = await txn.CommitAsync(token).ConfigureAwait(false);
                _log.LogInformation(
                    "delta txn {Txn} commit {Path}: v{Version} op={Op} (files={Files}, rows+={Rows}, rows-={Deleted})",
                    txnId, tablePath, v, operation, files.Count, pending.Rows, rowsDeleted);
            }
            catch (EngineeredWood.DeltaLake.Table.AppTransactionPreconditionException ex)
            {
                // The idempotent-producer CAS refused: this batch is already in the table. Reported in OUR
                // documented vocabulary (fabricator_delta_set_transaction_version's contract) with EW's
                // explanation kept, because the two say different useful things — ours names the mechanism
                // the user invoked, EW's says why retrying is the wrong response. Identified by TYPE, not by
                // message text: a string match here would relabel unrelated commit failures.
                throw new System.InvalidOperationException(
                    $"transaction version conflict on '{tablePath}' for app '{ex.AppId}': {ex.Message}");
            }
            catch (EngineeredWood.DeltaLake.DeltaConflictException ex)
            {
                throw new System.InvalidOperationException(
                    $"delta transaction conflict on '{tablePath}': the table moved from version {pinned} "
                    + $"while the transaction was open and the concurrent changes do not commute "
                    + $"({ex.Message}) — the transaction is rolled back; retry it.");
            }
        }
    }

    /// <summary>
    /// Opens this (DuckDB txn, table) pair's <see cref="EngineeredWood.DeltaLake.Table.DeltaTable"/> once and
    /// parks it on the buffer entry (hoist slice 1a). Idempotent: a second call returns the held one.
    /// </summary>
    private async Task<EngineeredWood.DeltaLake.Table.DeltaTable> EnsureHeldTableAsync(
        nint opener, string tablePath, DeltaTxnBuffer.PendingAppends pending,
        System.Threading.CancellationToken token)
    {
        if (pending.HeldTable is { } held)
        {
            return held;
        }
        var fs = TableFileSystems.Create(opener, tablePath);
        var dataFileWriter = _nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(tablePath)
            : null;
        // ⚠ THE WRITE SPEC IS LOAD-BEARING HERE AND USED TO BE OMITTED, which made every file this table
        // writes — the CDF change files, and the parked batches the flush writes — silently ignore the user's
        // `delta_write_options` (compression / row_group_size / bloom_filter_columns) and fall back to
        // snappy / 122880 / none. MEASURED on the codec engine with compression 'zstd': the CTAS data files
        // came out ZSTD and the change files SNAPPY, in the same table.
        pending.HeldTable = await EngineeredWood.DeltaLake.Table.DeltaTable
            .OpenAsync(fs, DeltaWriter.Options(ResolveWriteSpec(null, null, tablePath), dataFileWriter), token)
            .ConfigureAwait(false);
        return pending.HeldTable;
    }

    /// <summary>
    /// Starts this (DuckDB txn, table) pair's EW transaction once and parks it on the buffer entry
    /// (hoist slice 1a). Idempotent. The isolation and base snapshot are fixed at the FIRST call, which is
    /// the point of the hoist: every later statement stages into the same transaction against the same base.
    /// </summary>
    private static EngineeredWood.DeltaLake.Table.DeltaTransaction EnsureHeldTxn(
        DeltaTxnBuffer.PendingAppends pending, EngineeredWood.DeltaLake.Table.DeltaTable table,
        EngineeredWood.DeltaLake.Snapshot.Snapshot pinnedSnap, bool tableSer)
        => pending.HeldTxn ??= table.StartTransaction(pinnedSnap,
            tableSer
                ? EngineeredWood.DeltaLake.Table.IsolationLevel.Serializable
                : EngineeredWood.DeltaLake.Table.IsolationLevel.WriteSerializable);

    /// <summary>
    /// Disposes the buffer entry's held EW transaction and table, <b>transaction first</b> — its cleanup needs
    /// the table's filesystem. Runs on EVERY exit from a (DuckDB txn, table)'s life: commit, rollback, and an
    /// exception out of the flush.
    ///
    /// <para>Disposing the transaction is what ABORTS it, which is the reclamation the flush used to get from
    /// <c>await using</c>: a flush that does not commit takes back what EW's own writers staged during it —
    /// measured, a buffered DELETE whose commit is refused otherwise leaves a <c>deletion_vector_*.bin</c>,
    /// because <c>StageRowDeletesAsync</c> writes the vector at STAGING time, before the precondition is
    /// judged. After a SUCCESSFUL commit the abort is a no-op (EW #49 empties the ledger the instant the
    /// commit json is durable) — which is also why this is only safe from #49 onward.</para>
    ///
    /// <para><b>Never throws.</b> This runs in a finally, on the path where the caller may already be
    /// carrying the user's real error; a cleanup failure must not replace it.</para>
    /// </summary>
    private void DisposeHeld(DeltaTxnBuffer.PendingAppends pending)
    {
        var txn = pending.HeldTxn;
        var table = pending.HeldTable;
        pending.HeldTxn = null;
        pending.HeldTable = null;
        if (txn is not null)
        {
            try { txn.DisposeAsync().GetAwaiter().GetResult(); }
            catch (System.Exception ex)
            {
                _log.LogWarning("delta held transaction dispose failed ({Reason}) — staged files may remain "
                                + "as orphans for VACUUM", ex.Message);
            }
        }
        if (table is not null)
        {
            try { table.DisposeAsync().GetAwaiter().GetResult(); }
            catch (System.Exception ex)
            {
                _log.LogWarning("delta held table dispose failed ({Reason})", ex.Message);
            }
        }
    }

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
        _tableConfigCache.TryRemove(TablePath(schemaName, tableName), out _);
        long dropTxn = AmbientTransaction.Current;
        // CREATE + DROP inside one transaction still CANCELS OUT for the buffer — but the table IS on storage
        // now (hoist slice 5), so unlike before, the real drop below must actually run. Discard this
        // transaction's buffered work for it (that work targeted a table that is going away; its eagerly
        // written files live inside the folder the drop removes) and clear the ownership mark so ROLLBACK does
        // not try to drop it a second time.
        //
        // ⚠ THE ORDER AND THE CONDITION ARE BOTH LOAD-BEARING, and getting either wrong is silent. A first
        // version removed the entry UNCONDITIONALLY and BEFORE the guard, which made
        // `BEGIN; DELETE …; DROP TABLE …;` SUCCEED — the guard found nothing left to complain about. Caught by
        // verify_delta_catalog_transactions:339, which exists for exactly that shape.
        if (_txnBuffer.Get(dropTxn, TablePath(schemaName, tableName)) is { CreatedInTxn: true } created)
        {
            DisposeHeld(created);
            DeltaTxnBuffer.DisposeBatches(created);
            _txnBuffer.RemoveTable(dropTxn, TablePath(schemaName, tableName));
        }
        ThrowIfPendingAppends(TablePath(schemaName, tableName), "DROP TABLE");
        _log.LogInformation("delta drop table {Schema}.{Table} (adls={Adls})",
            schemaName, tableName, UseAdlsDirectoryOps);
        // Any credentialed abfss:// root (OneLake included) takes the DFS-native recursive delete: DuckDB's
        // azure FileSystem implements no RemoveDirectory at all, so the host-FS path below cannot serve it.
        RemoveTableFolder(TablePath(schemaName, tableName));
    }

    // The backend-specific recursive delete of a table folder, SHARED by DROP TABLE and by the rollback of a
    // table this transaction created (hoist slice 5). Extracted rather than duplicated because two of the
    // three branches are the ones that matter remotely: without them a rolled-back created table would fail
    // to drop on precisely OneLake/abfss (DuckDB's azure FileSystem implements no RemoveDirectory at all) and
    // on S3 (httpfs' RemoveDirectory re-lists keys WITHOUT the scheme prefix and fails its own per-file
    // remove). A hand-rolled HostFs.RemoveDir in the rollback path would have "worked" on a local root and
    // left an orphan table on every remote one.
    private void RemoveTableFolder(string path)
    {
        if (UseAdlsDirectoryOps)
        {
            FabricLakehouse.DeleteDirectory(path, _adlsCredential);
            return;
        }
        if (!HostFs.CanRemoveDir)
        {
            throw Unsupported("DROP TABLE (host does not provide a recursive directory-delete callback)");
        }
        try
        {
            HostFs.RemoveDir(Opener(), path);
        }
        catch (System.Exception ex)
        {
            // Fall back to a provider-side recursive delete: glob every object under the table prefix and
            // remove them file-by-file (object-store directories are implicit, and RemoveFile IS implemented
            // for s3).
            _log.LogInformation("delta drop {Path}: RemoveDirectory failed ({Err}) — per-file fallback",
                path, ex.Message);
            RemoveDirByFiles(path);
        }
    }

    // Recursive object-store delete without RemoveDirectory: remove every globbed object under the
    // prefix, then the zero-byte directory-marker keys CreateDirectory may have left (best-effort).
    private void RemoveDirByFiles(string dir)
    {
        var opener = Opener();
        var json = HostFs.Glob(opener, dir + "/**");
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var path = el.GetProperty("path").GetString();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                try
                {
                    HostFs.Remove(opener, path);
                }
                catch
                {
                    // best-effort per object (a marker key or an already-deleted object)
                }
            }
        }
        try { HostFs.Remove(opener, dir + "/_delta_log/"); } catch { }
        try { HostFs.Remove(opener, dir + "/"); } catch { }
    }

    /// <summary>DELETE = rowid-based via Delta row tracking: <paramref name="keys"/> is a stream whose single
    /// <c>_metadata.row_id</c> Int64 column holds the stable ids of the rows to delete (DuckDB's scan produced
    /// them, applying the WHERE). Collected and applied via deletion vectors (<see cref="DeltaReader.DeleteByRowIds"/>).</summary>
    // Drains a rowid stream (a single Int64 _metadata.row_id column) to a list. The stream consumption is
    // the sole async step of ExecuteDelete, isolated here so the delete's txn/DV branching stays synchronous.
    private static List<long> CollectRowIds(IArrowArrayStream keys)
        => CollectRowIdsAsync(keys).GetAwaiter().GetResult();

    private static async Task<List<long>> CollectRowIdsAsync(IArrowArrayStream keys)
    {
        var ids = new List<long>();
        using (keys)
        {
            RecordBatch? batch;
            while ((batch = await keys.ReadNextRecordBatchAsync().ConfigureAwait(false)) is not null)
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
        return ids;
    }

    public long ExecuteDelete(string schemaName, string tableName, IArrowArrayStream keys)
    {
        var opener = Opener();
        var ids = CollectRowIds(keys);
        if (ids.Count == 0)
        {
            return 0;
        }
        var path = TablePath(schemaName, tableName);
        // Explicit transaction (v60): BUFFER the delete — deletion-vector positions against the pinned base
        // version — and commit it fused with the transaction's other changes at COMMIT (atomic, rollback-able,
        // conflict-aborted if a concurrent writer moves the table). Autocommit keeps the direct per-statement
        // paths below (incl. copy-on-write and CDF capture).
        if (_txnBuffer.IsExplicit(AmbientTransaction.Current))
        {
            return BufferDeleteRows(AmbientTransaction.Current, path, schemaName, tableName, ids, forUpdate: false);
        }
        // Follow the TABLE's config: deletion-vector tables get the no-rewrite DV delete; everything else is
        // copy-on-write. (Honors external DV tables regardless of this catalog's create-time flag.)
        // DV-mode delete writes no data file (just a new DV + remove/add) → native writer N/A; copy-on-write
        // rewrite honors native_write (DuckDB writes the survivor file).
        // ONE table open serving both properties. Reading them separately would open the table twice — on
        // OneLake/S3 that is a second _delta_log LIST per DELETE, and adding the isolation read below is what
        // made the difference visible.
        var tableConfig = DeltaReader.GetTableProperties(opener, path);
        bool dvMode = DeltaReader.IsDeletionVectorsEnabled(tableConfig);
        _log.LogInformation("delta delete {Schema}.{Table}: rows={Rows} mode={Mode} native_write={Native}",
            schemaName, tableName, ids.Count, dvMode ? "deletion-vector" : "copy-on-write",
            !dvMode && _nativeWrite);
        // rowLevelRetry: Databricks-style ROW-LEVEL CONCURRENCY (write_serializable only) — a concurrent
        // DV swap of the same file re-unions instead of conflicting when the touched rows are disjoint.
        //
        // Reads the TABLE's level (EffectiveSerializable), not the catalog flag. It used to read the catalog
        // flag directly, which made this the ONE path where an attach-time option outranked a table's own
        // declaration: `delta.isolationLevel = Serializable` + ATTACH write_serializable meant an explicit
        // transaction was strict while a bare autocommit DELETE on the SAME table quietly took the row-level
        // relaxation. The old justification — that a single autocommit statement has no cross-statement reads
        // to serialize, so this is "only a resilience knob" — is true about the isolation semantics and beside
        // the point about the contract: once a table has DECLARED Serializable, no local option should weaken
        // it. Costs one property read per autocommit DELETE, on the DV path only.
        return dvMode
            ? DeltaReader.DeleteByRowIdsViaVectors(opener, path, ids, default,
                                                   rowLevelRetry: !EffectiveSerializable(tableConfig))
            : DeltaReader.DeleteByRowIds(opener, path, ids, default, _nativeWrite, _nativeRead,
                                         ResolveWriteSpec(null, null, path));
    }

    public IArrowArrayStream ExecuteQuery(string sql) => throw Unsupported("raw query");

    // Maintenance command dialect, invoked via fabricator_exec('<catalog>', '<cmd>'):
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
        ThrowIfPendingAppends(path, $"{tokens[0].ToUpperInvariant()} (maintenance)");
        switch (tokens[0].ToUpperInvariant())
        {
            case "OPTIMIZE":
            {
                // OPTIMIZE <table> [FULL] — FULL forces a full recluster on a clustering-declared table
                // (ignores ZCube identities; the Databricks `OPTIMIZE tbl FULL` dialect). No effect on
                // plain bin-pack tables.
                bool fullRecluster = tokens.Length > 2
                    && string.Equals(tokens[2], "FULL", System.StringComparison.OrdinalIgnoreCase);
                _log.LogInformation("delta exec OPTIMIZE {Schema}.{Table} native_write={Native} full={Full}",
                    schema, table, _nativeWrite, fullRecluster);
                return DeltaReader.Optimize(opener, path, default, _nativeWrite, _nativeRead, fullRecluster,
                                            ResolveWriteSpec(null, null, path));
            }
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
    // Drains the UPDATE stream — SET-column values (cols 0..setColumnCount-1, named by the target column) plus
    // the transient _metadata.row_id (last column) — into (SET column names, rowid -> new SET values). The stream
    // consumption is the sole async step of ExecuteUpdate, isolated here so the rewrite logic stays synchronous.
    /// <summary>
    /// The drained UPDATE stream, held in ARROW form: one batch of <c>[SET columns …, rowid]</c> carrying each
    /// surviving row exactly once, plus the rowid → row map both consumers correlate by.
    /// </summary>
    /// <remarks>
    /// <para><b>This used to be a <c>Dictionary&lt;long, object?[]&gt;</c> of BOXED values, and that was the
    /// UPDATE's single largest memory term.</b> Measured at 1M rows on one table: a DELETE, which carries
    /// rowids and no SET values, peaks at 204 MB; the same rows through a one-SET-column UPDATE peaked at
    /// 454 MB and a three-column one at 651 MB — i.e. ~98 MB per additional SET column per 1M rows, or
    /// ~98 BYTES to carry an 8-byte value. That is a boxed <c>long</c> (24 B) plus an <c>object?[]</c> per row
    /// (~32 B) plus a dictionary entry plus a second copy into a <c>List&lt;object?&gt;</c> before
    /// <c>BuildArray</c> rebuilt it as Arrow anyway.</para>
    /// <para><b>⚠ The incoming chunks CANNOT simply be retained</b> — <c>DeltaWriter.Materialize</c> does a full
    /// Arrow IPC round-trip for exactly this reason ("the source batches may be freed after consumption"), and
    /// the old <c>ReadScalarDeep</c> here was documented as deep-copying because the batch is disposed right
    /// after. So each chunk is copied ONCE into independent buffers with
    /// <see cref="EngineeredWood.Arrow.ArrowCompute.Take(RecordBatch, Schema, System.Collections.Generic.List{int})"/>,
    /// and the copies are joined with <see cref="ArrowArrayConcatenator"/> at the end.</para>
    /// <para><b>Dedupe is preserved and is not cosmetic:</b> the old dictionary assignment was last-write-wins,
    /// reachable through <c>UPDATE … FROM other</c> whose join matches one target row twice, and it also sets
    /// the statement's REPORTED row count. Appends cannot overwrite, so every row is appended, the map keeps
    /// each rowid's LAST ordinal, and one final <c>Take</c> compacts to the survivors.</para>
    /// </remarks>
    private sealed class UpdateInput : System.IDisposable
    {
        internal List<string> SetColNames = new();
        /// <summary>SET columns 0..n-1 then the rowid — the incoming layout, one row per surviving rowid.</summary>
        internal RecordBatch? Values;
        /// <summary>rowid → its row in <see cref="Values"/>.</summary>
        internal Dictionary<long, int> RowByRid = new();
        internal int Count => RowByRid.Count;

        /// <summary>One SET value, boxed on demand. Same call the old parse made eagerly for every value of
        /// every row — the difference is that nothing retains the result.</summary>
        internal object? Value(int row, int setSlot)
            => ArrowValueReader.ReadScalarDeep(Values!.Column(setSlot), row);

        public void Dispose() => Values?.Dispose();
    }

    private static UpdateInput ParseUpdateStream(IArrowArrayStream data, int setColumnCount)
        => ParseUpdateStreamAsync(data, setColumnCount).GetAwaiter().GetResult();

    private static async Task<UpdateInput> ParseUpdateStreamAsync(
        IArrowArrayStream data, int setColumnCount)
    {
        var input = new UpdateInput();
        var copies = new List<RecordBatch>();
        var rids = new List<long>();
        // rowid -> its ordinal across the concatenated copies; last occurrence wins, as the old dictionary did.
        var ordinalByRid = new Dictionary<long, int>();
        Schema? schema = null;
        try
        {
            using (data)
            {
                RecordBatch? b;
                while ((b = await data.ReadNextRecordBatchAsync().ConfigureAwait(false)) is not null)
                {
                    using (b)
                    {
                        if (b.Length == 0)
                        {
                            continue;
                        }
                        if (input.SetColNames.Count == 0)
                        {
                            for (int j = 0; j < setColumnCount; j++)
                            {
                                input.SetColNames.Add(b.Schema.FieldsList[j].Name);
                            }
                            schema = b.Schema;
                        }
                        var ridArr = (Int64Array)b.Column(setColumnCount);
                        // Rows whose rowid is NULL are dropped, exactly as the old `continue` did — so the copy
                        // is a Take of the surviving positions rather than of the whole chunk.
                        var keep = new List<int>(b.Length);
                        for (int i = 0; i < b.Length; i++)
                        {
                            if (ridArr.GetValue(i) is not { } rid)
                            {
                                continue;
                            }
                            ordinalByRid[rid] = rids.Count;
                            rids.Add(rid);
                            keep.Add(i);
                        }
                        if (keep.Count > 0)
                        {
                            // The independent copy. Take allocates new buffers, so this survives the chunk's
                            // disposal on the next line — which retaining `b` itself would not.
                            copies.Add(EngineeredWood.Arrow.ArrowCompute.Take(b, b.Schema, keep));
                        }
                    }
                }
            }
            if (copies.Count == 0 || schema is null)
            {
                return input;
            }

            // Compact to the surviving rows: the ordinals of the LAST occurrence of each rowid, and the map
            // the consumers correlate by is built over the compacted positions.
            var survivors = new List<int>(ordinalByRid.Count);
            foreach (var kv in ordinalByRid)
            {
                input.RowByRid[kv.Key] = survivors.Count;
                survivors.Add(kv.Value);
            }

            var columns = new IArrowArray[schema.FieldsList.Count];
            for (int c = 0; c < columns.Length; c++)
            {
                var slices = new IArrowArray[copies.Count];
                for (int k = 0; k < copies.Count; k++)
                {
                    slices[k] = copies[k].Column(c);
                }
                var joined = copies.Count == 1 ? slices[0] : ArrowArrayConcatenator.Concatenate(slices);
                columns[c] = EngineeredWood.Arrow.ArrowCompute.Take(joined, survivors);
            }
            input.Values = new RecordBatch(schema, columns, survivors.Count);
            return input;
        }
        finally
        {
            foreach (var c in copies)
            {
                c.Dispose();
            }
        }
    }

    public long ExecuteUpdate(string schemaName, string tableName, int setColumnCount, IArrowArrayStream data)
    {
        var opener = Opener();
        var path = TablePath(schemaName, tableName);

        // 1. Parse the update stream into ARROW: one compacted batch of [SET columns…, rowid] plus the
        // rowid -> row map. The stream drain is the sole async step, isolated in the helper so the rewrite
        // logic below stays synchronous. Disposed at the end of the statement — see UpdateInput for why the
        // incoming chunks are copied rather than retained.
        using var updates = ParseUpdateStream(data, setColumnCount);
        var setColNames = updates.SetColNames;
        if (updates.Count == 0)
        {
            return 0;
        }

        // 2. Map SET column names -> user-schema column indices (case-insensitive). A pending buffered
        // ALTER's schema wins (the post-image rows must carry the added columns; autocommit has no pending).
        var userSchema = _txnBuffer.Get(AmbientTransaction.Current, path)?.PendingArrowSchema
            ?? DeltaReader.GetSchema(opener, path);
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

        // NOT NULL enforcement on the SET values: a Delta writer must honor the declared nullability
        // (top-level + struct-field — a struct SET value is the ReadScalarDeep dictionary, walked
        // recursively; a key missing from the dictionary is an implicit NULL).
        for (int j = 0; j < setColNames.Count; j++)
        {
            if (setSlotField[j] is not { } targetField)
            {
                continue;
            }
            for (int r = 0; r < updates.Count; r++)
            {
                // Boxed for the duration of the check only — the value is not retained anywhere.
                DeltaNullability.ValidateSetValue(updates.Value(r, j), targetField, tableName);
            }
        }

        // Explicit transaction (v60): BUFFER the update — the old rows become pending deletion-vector
        // positions, the post-image rows (read back at the pinned base, SET values substituted) become
        // pending appends; both flush fused into ONE commit at COMMIT. Autocommit keeps the direct
        // copy-on-write / merge-on-read paths below.
        if (_txnBuffer.IsExplicit(AmbientTransaction.Current))
        {
            return BufferUpdateRows(AmbientTransaction.Current, path, schemaName, tableName,
                                    updates, setSlotByColumn, userSchema);
        }

        // 3. Per-file copy-on-write via EW master's host-join UPDATE shape (pr4-to-master guide §1):
        //    build ONE `updates` batch — the transient rowid column + one column per SET column (LOGICAL
        //    table-column names, table-schema types via BuildArray) — and hand it to
        //    UpdateByRowIdsAsync(updates). EW rewrites only the files containing a matched row,
        //    substituting the SET columns keyed by rowid (type-agnostic concat+take — structs included);
        //    pass-through rows/columns move by reference. Struct SET on COLUMN-MAPPING tables still works:
        //    the rewrite applies EW's recursive ToPhysical, so the logical-named substituted struct lands
        //    in the spec nested layout.
        MemoryProbe.Mark("delta update: set values parsed", updates.Count);
        var updFields = new List<Field>(setColNames.Count + 1)
        {
            new Field(RowIdColumn, Int64Type.Default, nullable: false),
        };
        // The rowid column IS the parsed batch's last column, already compacted to the surviving rows and in
        // the order RowByRid indexes — so it is reused rather than rebuilt. Only its FIELD is restated (the
        // name the consumers look it up by); an array carries no name of its own.
        var updArrays = new List<IArrowArray>(setColNames.Count + 1)
        {
            updates.Values!.Column(setColNames.Count),
        };
        for (int j = 0; j < setColNames.Count; j++)
        {
            var field = setSlotField[j];
            // ⚠ ONE column's values are boxed at a time and dropped before the next, rather than every value
            // of every row being boxed up front and held. BuildArray's TARGET-TYPE conversion is preserved
            // exactly — an incoming array of a different width or unit is still converted through the boxed
            // value — which is why the incoming Arrow column is not simply handed through here.
            var vals = new List<object?>(updates.Count);
            for (int r = 0; r < updates.Count; r++)
            {
                vals.Add(updates.Value(r, j));
            }
            updArrays.Add(BuildArray(field.DataType, vals));
            // Keep the field metadata: the ew.variant_transport transport marker must ride the SET column.
            updFields.Add(new Field(field.Name, field.DataType, nullable: true, field.Metadata));
        }
        var updatesBatch = new RecordBatch(
            new Apache.Arrow.Schema(updFields, null), updArrays, updates.Count);
        // The catalog's ATTACH default only — the TABLE's own delta.isolationLevel outranks it, resolved inside
        // from the configuration the update path already reads (no extra _delta_log LIST).
        MemoryProbe.Mark("delta update: arrow batch rebuilt", updates.Count);
        DeltaReader.UpdateByRowIds(opener, path, updatesBatch, default, _nativeWrite, _nativeRead,
                                   catalogSerializable: _serializable,
                                   spec: ResolveWriteSpec(null, null, path));

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
            // BLOB — incl. the ew.variant_transport transport (a VARIANT SET value crosses as its blob form).
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
    // RENAME TABLE of a PENDING-CREATED table: nothing is on storage under the old name except this
    // transaction's eagerly-streamed files (slice B) — move them to the new folder (atomic dir move
    // where the FS supports it, per-file copy+delete otherwise: object stores) and re-key the buffer.
    // The flush then creates the table at the FINAL path. Codec pending-creates park batches (no files)
    // — pure re-key.
    // The backend-specific rename of a table folder, SHARED by ALTER TABLE RENAME and by the rename of a
    // table this transaction CREATED (hoist slice 5) — extracted for the same reason as RemoveTableFolder:
    // duplicating it would have given the created-table path the local-only branch and silently failed to
    // move anything on OneLake/abfss (Azure MoveFile is unimplemented) or S3 (httpfs has no MoveFile).
    private void RenameTableFolder(string oldPath, string newPath)
    {
        if (UseAdlsDirectoryOps)
        {
            FabricLakehouse.RenameDirectory(oldPath, newPath, _adlsCredential);
        }
        else if (_s3Credential is not null && S3CommitFileSystem.IsS3(_root))
        {
            S3CommitFileSystem.RenameDirectory(oldPath, newPath, _s3Credential);
        }
        else
        {
            HostFs.MoveDir(Opener(), oldPath, newPath);
        }
        _tableConfigCache.TryRemove(newPath, out _);
    }

    // Object-store fallback for a directory move (no MoveDir on S3): copy each of the transaction's eager
    // files to the new path and delete the source. The per-file IO is the sole async step.
    private static void MoveFilesByCopy(nint opener, string oldPath, string newPath,
                                        IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile> files)
        => MoveFilesByCopyAsync(opener, oldPath, newPath, files).GetAwaiter().GetResult();

    private static async Task MoveFilesByCopyAsync(nint opener, string oldPath, string newPath,
                                        IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile> files)
    {
        var src = new DuckDbTableFileSystem(opener, oldPath);
        var dst = new DuckDbTableFileSystem(opener, newPath);
        foreach (var wf in files)
        {
            var bytes = await src.ReadAllBytesAsync(wf.RelativePath).ConfigureAwait(false);
            await dst.WriteAllBytesAsync(wf.RelativePath, bytes).ConfigureAwait(false);
            await src.DeleteAsync(wf.RelativePath).ConfigureAwait(false);
        }
    }

    public void AlterTable(int k, string s, string t, string? a1, string? a2, Field? c, int f)
    {
        // VARIANT: `c` arrives from the C ABI in TRANSPORT form (a BINARY field carrying the
        // ew.variant_transport marker), and every consumer of it below hands it to engineered-wood. Convert
        // once, here, at the boundary. This is the path the variant suite pins for ADD COLUMN: the marker is
        // the ONLY discriminator (the transport is a leaf binary, so the storage type cannot carry it), and a
        // field that reaches EW unconverted records Delta `binary` FOREVER — a metaData commit is not
        // revisable, and the failure would surface far away as an insert that cannot convert VARIANT to BLOB.
        c = c is null ? null : VariantMarker.ToCanonicalField(c);
        // Explicit transaction (slice 3): schema-evolution ALTERs buffer — the metaData (+ protocol)
        // action fuses into the transaction's ONE commit; reads/binds overlay the pending schema; ROLLBACK
        // discards it. Buffered kinds: ADD/RENAME/DROP COLUMN + nested ADD/DROP FIELD. Nested RENAME FIELD
        // and RENAME TABLE stay immediate (a nested rename would need a per-level name map in the read
        // overlay; a table rename is a physical folder move).
        long alterTxn = AmbientTransaction.Current;
        if (_txnBuffer.IsExplicit(alterTxn)
            && (k == AlterKind.AddColumn || k == AlterKind.RenameColumn || k == AlterKind.DropColumn
                || k == AlterKind.AddField || k == AlterKind.DropField))
        {
            var alterPath = TablePath(s, t);
            switch (k)
            {
                case AlterKind.AddColumn:
                {
                    var col0 = c ?? throw new System.InvalidOperationException(
                        "delta ADD COLUMN requires a column definition.");
                    string name0 = a1 ?? col0.Name;
                    var field0 = string.Equals(name0, col0.Name, System.StringComparison.Ordinal)
                        ? col0
                        : new Field(name0, col0.DataType, col0.IsNullable);
                    BufferAddColumn(alterTxn, alterPath, s, t, field0, f);
                    return;
                }
                case AlterKind.RenameColumn:
                    BufferRenameColumn(alterTxn, alterPath, s, t,
                        a1 ?? throw new System.InvalidOperationException(
                            "delta RENAME COLUMN requires the old column name."),
                        a2 ?? throw new System.InvalidOperationException(
                            "delta RENAME COLUMN requires the new column name."));
                    return;
                case AlterKind.DropColumn:
                    BufferDropColumn(alterTxn, alterPath, s, t,
                        a1 ?? throw new System.InvalidOperationException(
                            "delta DROP COLUMN requires the column name."));
                    return;
                case AlterKind.AddField:
                    BufferAddField(alterTxn, alterPath, s, t,
                        ParseJsonPath(a1, "ADD COLUMN (nested field)"),
                        c ?? throw new System.InvalidOperationException(
                            "delta ADD COLUMN (nested field) requires a field definition."));
                    return;
                case AlterKind.DropField:
                    BufferDropField(alterTxn, alterPath, s, t,
                        ParseJsonPath(a1, "DROP COLUMN (nested field)"));
                    return;
            }
        }
        // RENAME TABLE of a table CREATED in this transaction — dbt's table materialization, and the shape
        // that caught this slice's last gap (verify_delta_catalog_transactions:2872):
        //   BEGIN; CREATE <model>__dbt_tmp AS …;
        //          ALTER <model> RENAME TO <model>__dbt_backup;
        //          ALTER <model>__dbt_tmp RENAME TO <model>; COMMIT
        // The dedicated buffer-only path this replaces re-keyed the entry and moved the eagerly-written
        // files, because the table was not on storage. It IS on storage now, so the rename below is the
        // ORDINARY folder rename — but two things still have to happen here, and both are silent if missed:
        //   1. the buffer entry must be RE-KEYED, since it is keyed by path and holds this statement's
        //      buffered rows plus the CreatedInTxn ownership mark. Lose it and the flush writes nothing and
        //      the rollback drops nothing;
        //   2. the held EW table/transaction must be DISPOSED first — they were opened against the OLD path
        //      and their filesystem would keep writing there after the folder moved.
        // The pending-appends guard must therefore be SKIPPED for this case (buffered rows are expected);
        // it still applies to every other ALTER and to a table this transaction did not create.
        if (k == AlterKind.RenameTable
            && _txnBuffer.Get(alterTxn, TablePath(s, t)) is { CreatedInTxn: true } renCreated)
        {
            string renTo = a1 ?? throw new System.InvalidOperationException(
                "delta RENAME TABLE requires a new table name.");
            string renNew = TablePath(s, renTo);
            if (_txnBuffer.HasPending(alterTxn, renNew) || TableExists(renNew))
            {
                throw new System.InvalidOperationException(
                    $"delta RENAME TABLE: '{renTo}' already exists.");
            }
            DisposeHeld(renCreated);
            RenameTableFolder(TablePath(s, t), renNew);
            if (!_txnBuffer.RenameTable(alterTxn, TablePath(s, t), renNew))
            {
                throw new System.InvalidOperationException(
                    $"delta RENAME TABLE: the transaction's buffer entry for '{t}' could not be re-keyed.");
            }
            _tableConfigCache.TryRemove(TablePath(s, t), out _);
            _log.LogInformation("delta txn {Txn} rename created table {Old} -> {New}",
                alterTxn, TablePath(s, t), renNew);
            return;
        }
        ThrowIfPendingAppends(TablePath(s, t), "ALTER TABLE");
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
            case AlterKind.AddField:
            {
                // a1 = JSON path of the CONTAINING struct; `c` = the new field (metadata-only commit;
                // old files backfill NULL on read via the recursive schema-evolution reconcile).
                var col = c ?? throw new System.InvalidOperationException(
                    "delta ADD COLUMN (nested field) requires a field definition.");
                var container = ParseJsonPath(a1, "ADD COLUMN (nested field)");
                DeltaReader.AddField(Opener(), TablePath(s, t), container, col, default);
                return;
            }
            case AlterKind.RenameField:
            {
                // a1 = JSON full path of the field; a2 = the new name (requires column mapping).
                var fieldPath = ParseJsonPath(a1, "RENAME COLUMN (nested field)");
                string newFieldName = a2 ?? throw new System.InvalidOperationException(
                    "delta RENAME COLUMN (nested field) requires the new name.");
                DeltaReader.RenameField(Opener(), TablePath(s, t), fieldPath, newFieldName, default);
                return;
            }
            case AlterKind.DropField:
            {
                // a1 = JSON full path of the field (requires column mapping; readers reconcile old files).
                var fieldPath = ParseJsonPath(a1, "DROP COLUMN (nested field)");
                DeltaReader.DropField(Opener(), TablePath(s, t), fieldPath, default);
                return;
            }
            case AlterKind.RenameTable:
            {
                string newName = a1 ?? throw new System.InvalidOperationException(
                    "delta RENAME TABLE requires a new table name.");
                // The table folder (incl. _delta_log) is moved; the schema is unchanged (RENAME TABLE renames
                // within the same schema). SECRET-routed abfss:// (OneLake or a plain ADLS Gen2 account) → DFS
                // atomic rename (Azure MoveFile is unimplemented); SECRET-routed s3:// → SDK server-side
                // CopyObject rename (httpfs has no MoveFile; no data crosses the client); local → the host FS
                // move (FileSystem::MoveFile, atomic). A secretless abfss:// / s3:// still throws cleanly
                // (attach with a SECRET for rename support).
                RenameTableFolder(TablePath(s, t), TablePath(s, newName));
                _tableConfigCache.TryRemove(TablePath(s, t), out _);
                _tableConfigCache.TryRemove(TablePath(s, newName), out _);
                return;
            }
            case AlterKind.SetSortedBy:
            {
                // ALTER TABLE t SET SORTED BY (a, b) / RESET SORTED BY — a1 = JSON array ([] = RESET).
                // ONE metadata commit updates the fabricator.sortedBy ordered-write property AND
                // (unpartitioned) the delta.clustering declaration — the ALTER CLUSTER BY analog; a
                // partitioned table takes the property only (clustering + partitioning are mutually
                // exclusive). Immediate/administrative, like set_tblproperties.
                IReadOnlyList<string> sortCols = string.IsNullOrEmpty(a1)
                    ? []
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(a1!) ?? [];
                DeltaReader.SetSortedBy(Opener(), TablePath(s, t), sortCols, default);
                _tableConfigCache.TryRemove(TablePath(s, t), out _); // the config changed — re-read on next write
                return;
            }
            case AlterKind.SetPartitionedBy:
                throw Unsupported(
                    "SET/RESET PARTITIONED BY — changing a Delta table's partitioning requires a full "
                    + "rewrite: COPY the data to the table's path with (FORMAT delta, MODE 'overwrite', "
                    + "PARTITION_COLUMNS '…')");
            default:
                throw Unsupported("ALTER TABLE (only ADD/RENAME/DROP COLUMN — top-level or nested struct field — "
                                  + "SET/RESET SORTED BY, and RENAME TABLE are supported on Delta)");
        }
    }

    // A nested-field path from the host: a JSON array of segments (["s","inner","f"] — names may contain dots).
    private static IReadOnlyList<string> ParseJsonPath(string? json, string operation)
    {
        if (string.IsNullOrEmpty(json))
        {
            throw new System.InvalidOperationException($"delta {operation} requires a field path.");
        }
        var segments = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json!);
        if (segments is not { Count: > 0 })
        {
            throw new System.InvalidOperationException($"delta {operation}: invalid field path '{json}'.");
        }
        return segments;
    }

    // ---- catalog-bound custom functions -------------------------------------------------------------
    // The scalar + table kinds are hosted (see Functions); in-out and aggregates deliberately are not — no
    // demand, and each would add a session lifetime to reason about. GenerateTableSql keeps the default
    // interface throw: a sqlgen function must name tables in a SQL dialect this provider does not have.

    /// <summary>
    /// This catalog's catalog-bound custom functions. Lazily built so an ATTACH that never touches a function
    /// pays nothing, and per CATALOG (not static) because a function may capture catalog context — the OneLake
    /// workspace/item and credential resolved at ATTACH, which is what lets a Fabric-API function be called
    /// with no arguments. See docs/fabric-api-functions.md.
    /// </summary>
    private CatalogFunctionSet Functions => _functions ??= BuildFunctionSet();
    private CatalogFunctionSet? _functions;

    private CatalogFunctionSet BuildFunctionSet()
    {
        var scalars = new List<ICatalogScalarFunction>();
        var tables = new List<ICatalogTableFunction>
        {
            new DeltaCatalogInfoFunction(new[]
            {
                new KeyValuePair<string, string>("root", _root),
                new KeyValuePair<string, string>("native_read", _nativeRead ? "true" : "false"),
                new KeyValuePair<string, string>("native_write", _nativeWrite ? "true" : "false"),
                new KeyValuePair<string, string>("onelake", FabricLakehouse.IsOneLake(_root) ? "true" : "false"),
            }),
        };
        // Fabric REST API functions are registered ONLY on a OneLake root: off Fabric they have no workspace,
        // no item and no REST credential, so advertising them would put functions in the catalog that can only
        // fail. A local/S3 Delta attach therefore shows none of them — asserted as a negative control in
        // test/verify_delta_catalog_functions.test.
        if (FabricLakehouse.IsOneLake(_root))
        {
            // The root names both defaults; parsed once here rather than inside the client, so the Fabric
            // function set stays provider-agnostic and a SQL Server attach can supply the same pair from its
            // `workspace`/`item` ATTACH options (docs/fabric-api-functions.md §9h).
            var (workspace, lakehouse) = FabricLakehouse.ParseOneLake(_root);
            FabricApiFunctions.Register(scalars, tables,
                                        new FabricApiContext(workspace, lakehouse, _fabricCredential));
        }
        return new CatalogFunctionSet(scalars, tables);
    }

    public Schema GetFunctionParamSchema(string s, string f) =>
        Functions.ParamSchema(s, f) ?? throw NoFunction(s, f);

    public Schema GetFunctionReturnSchema(string s, string f) =>
        Functions.ReturnSchema(s, f) ?? throw NoFunction(s, f);

    public IArrowArrayStream ExecuteScalar(string s, string f, IArrowArrayStream a) =>
        Functions.ExecuteScalar(s, f, a) ?? throw NoFunction(s, f);

    public Schema GetFunctionOutputSchema(string s, string f, RecordBatch? a = null) =>
        Functions.OutputSchema(s, f, a) ?? throw NoFunction(s, f);

    public IBoundTable TableBind(string s, string f, RecordBatch? a) =>
        Functions.TableBind(s, f, a) ?? throw NoFunction(s, f);

    public IArrowInOutBinding InOutBind(string s, string f, RecordBatch? a, Schema input) => throw NoFunctions();
    public IAggregateSession AggOpen(string s, string f) => throw NoFunctions();

    private static NotSupportedException NoFunctions() => new("delta provider: no catalog functions.");

    // Names the function, because a lookup miss here means the host registered a declaration this catalog
    // cannot serve — i.e. the declaration list and the registry disagree, which is a bug in OUR wiring, not
    // user error. A bare "no catalog functions" would send the reader looking in the wrong place.
    private static NotSupportedException NoFunction(string schema, string func) =>
        new($"delta provider: no catalog function '{schema}.{func}'.");

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
