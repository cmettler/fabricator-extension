using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using EngineeredWood.Parquet;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// <c>fabricator_delta_scan(path)</c> — a connection-free GLOBAL <b>host-FS</b> table function: reads a Delta
/// Lake table (Curt Hagenlocher's engineered-wood, pure C#) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via <see cref="DuckDbTableFileSystem"/>, so local / az:// / s3:// / https:// paths and
/// DuckDB secrets all work (one auth config shared with native reads).
///
/// This is the reference <b>host-FS reader</b>: it needs the calling operator's <c>ClientContext</c> (for
/// secret resolution) at both bind (read the Delta log → schema) and execute (read data). That opener isn't an
/// argument of the generic table-function path, so it's read from <see cref="AmbientOpener.Current"/>, which
/// the host sets via <c>set_active_opener</c> immediately before each bind/execute. A new lakehouse format
/// (Iceberg/Lance/…) is added the same way — a pure-C# <see cref="ITableFunction"/> declared as a global, no
/// bespoke C++/ABI. See docs/global-functions.md §host-FS + docs/filesystem-bridge.md.
/// </summary>
public sealed class DeltaGlobalTableFunction : ITableFunction
{
    public string Name => "fabricator_delta_scan";

    public Schema Parameters { get; } =
        new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);

    // Delta/Parquet statistics are byte-ordered (UTF-8 binary), matching DuckDB's default binary string
    // comparison — so string ordering comparisons + BETWEEN are superset-safe to push into file/row-group
    // skipping (the C++ FilterSerializer honors this; DuckDB re-applies regardless).
    public bool StringOrderPushable => true;

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("fabricator_delta_scan: path must not be NULL");
        // The opener (this operator's ClientContext) is valid for the duration of this synchronous bind —
        // read the Delta table's schema now (no data read). This is a connection-free global reader with no
        // Fabric credential, so clear any left on this (reused) execution thread by a prior catalog op → the FS
        // factory uses the host-FS (duckdb-azure) path, not the direct-SDK OneLake filesystem.
        AmbientOneLakeCredential.Current = null;
        var schema = DeltaReader.GetSchema(AmbientOpener.Current, path);
        return new DeltaBinding(path, schema);
    }

    private sealed class DeltaBinding : IArrowTableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public DeltaBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;

        // engineered-wood honors the projection (reads only the requested columns) and pushes the filter into
        // file + row-group skipping; it does NOT re-apply the predicate per row, so the result is a SUPERSET —
        // DuckDB re-applies the projection (by name) + every filter above the scan. (The host already maps the
        // result columns by name regardless of this flag for a global table function.)
        public bool SupportsPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Capture the opener (this operator's ClientContext) NOW — it stays valid for the whole execution,
            // so the lazy stream below can read files through it as the host pulls batches (no materialization).
            // Connection-free global reader → clear any stale Fabric credential (host-FS path, see Bind).
            AmbientOneLakeCredential.Current = null;
            var opener = AmbientOpener.Current;
            var spec = scan.Spec;
            // Push the FILTER for file + row-group skipping (doesn't change the result schema). Read the filter
            // constants + map the predicate eagerly (the constants batch is in-memory; this consumes + disposes
            // scan.FilterValues). A node we can't safely push is dropped (superset-safe); DuckDB re-applies.
            var filter = spec?.Filter is { } node
                ? new DeltaFilterBuilder(ReadValues(scan.FilterValues)).Build(node)
                : null;
            // Column PROJECTION is intentionally NOT pushed into engineered-wood here: the shared
            // BindingBoundTable wraps this stream with the binding's FULL OutputSchema, so returning a
            // projected column subset would mismatch the declared schema (arrow_ingest SIGSEGV). DuckDB still
            // projects columns above the scan (by name). True column-pruning into the Parquet read would need
            // a pushdown-native bound table that declares the projected schema — see docs/filesystem-bridge.md.
            return DeltaReader.Stream(opener, _path, columns: null, filter, ct);
        }

        private static IReadOnlyList<object?> ReadValues(IArrowArrayStream? filterValues)
            => ReadValuesAsync(filterValues).GetAwaiter().GetResult();

        private static async Task<IReadOnlyList<object?>> ReadValuesAsync(IArrowArrayStream? filterValues)
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
                    try
                    {
                        values[i] = ArrowValueReader.ReadScalar(batch.Column(i), 0);
                    }
                    catch (System.NotSupportedException)
                    {
                        values[i] = null; // unmappable Arrow type → that predicate node won't push (DuckDB re-applies)
                    }
                }
                return values;
            }
        }

        public void Dispose() { }
    }
}

/// <summary>
/// <c>fabricator_delta_native_scan(path)</c> — the native-read pre-spike (docs/multifile-delta.md Phase A):
/// engineered-wood supplies the EXACT active data-file list + schema (the log/snapshot layer), and DuckDB's
/// <b>native parquet reader</b> reads the files via <c>read_parquet([...])</c> run on the host engine
/// (<see cref="Host.Query"/>) — so the read gets DuckDB's tuned reader + <c>ExternalFileCache</c> (over the
/// <c>onelake://</c> subsystem for OneLake) instead of engineered-wood's C# parquet reader. Contrast
/// <see cref="DeltaGlobalTableFunction"/> (<c>fabricator_delta_scan</c>), which reads the data in C#.
///
/// <para>First slice — plain tables: no deletion vectors, no partition columns, no pushdown (DuckDB projects +
/// filters above the scan). DV/partition/pushdown + folding into the ATTACH catalog are follow-up slices.
/// Credential-free where the log lives on a local/host-FS path; OneLake needs the ambient credential for the
/// log read (works from the ATTACH catalog path — a later slice).</para>
/// </summary>
public sealed class DeltaNativeScanFunction : ITableFunction
{
    public string Name => "fabricator_delta_native_scan";
    public Schema Parameters => new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("fabricator_delta_native_scan: path must not be NULL");
        // Connection-free global reader → clear any stale Fabric credential (host-FS path for the log read).
        AmbientOneLakeCredential.Current = null;
        var opener = AmbientOpener.Current;
        var schema = DeltaReader.GetSchema(opener, path);              // engineered-wood: the log → schema
        var files = DeltaReader.GetActiveFileUris(opener, path);      // engineered-wood: the exact active file set
        return new NativeBinding(schema, files);
    }

    private sealed class NativeBinding : IArrowTableFunctionBinding
    {
        private readonly Schema _schema;
        private readonly IReadOnlyList<string> _files;

        public NativeBinding(Schema schema, IReadOnlyList<string> files)
        {
            _schema = schema;
            _files = files;
        }

        public Schema OutputSchema => _schema;
        public bool SupportsPushdown => false; // DuckDB projects + filters above the read_parquet scan

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            if (_files.Count == 0)
            {
                return EmptyStream();
            }
            // Read the EXACT active files through the host's native parquet reader (cached; over onelake:// for
            // OneLake). A fresh host connection runs this (Host.Query) — reentrancy-safe by design.
            var list = string.Join(",", _files.Select(f => "'" + f.Replace("'", "''") + "'"));
            var stream = Host.Query($"SELECT * FROM read_parquet([{list}])");
            return Drain(stream, ct);
        }

        private static async IAsyncEnumerable<RecordBatch> EmptyStream()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        private static async IAsyncEnumerable<RecordBatch> Drain(
            IArrowArrayStream stream, [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var b = await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                    if (b is null)
                    {
                        break;
                    }
                    yield return b;
                }
            }
            finally
            {
                stream.Dispose();
            }
        }

        public void Dispose() { }
    }
}

/// <summary>
/// <c>fabricator_delta_write_demo(path)</c> — a connection-free GLOBAL host-FS table function that WRITES a small
/// fixed Delta table (5 rows: <c>id BIGINT</c>, <c>name VARCHAR</c>) at <paramref name="path"/> via
/// engineered-wood through the host FileSystem write callbacks, and returns one row <c>(version, rows_written)</c>.
/// A spike that proves the write bridge end-to-end (round-trips with <c>fabricator_delta_scan</c>); the write goes
/// through <see cref="DuckDbTableFileSystem"/>, whose commit uses the put-if-absent EXCLUSIVE_CREATE primitive
/// (validated on OneLake). The opener (this operator's ClientContext) is read from <see cref="AmbientOpener"/>.
/// </summary>
public sealed class DeltaWriteDemoFunction : ITableFunction
{
    public string Name => "fabricator_delta_write_demo";

    public Schema Parameters { get; } =
        new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);

    public IArrowTableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("fabricator_delta_write_demo: path must not be NULL");
        var outSchema = new Schema(new[]
        {
            new Field("version", Int64Type.Default, nullable: false),
            new Field("rows_written", Int64Type.Default, nullable: false),
        }, metadata: null);
        return new WriteBinding(path, outSchema);
    }

    private sealed class WriteBinding : IArrowTableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public WriteBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;
        public bool SupportsPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Write synchronously while the opener (captured now) is valid, then yield the result row.
            AmbientOneLakeCredential.Current = null; // connection-free global writer → host-FS path
            var (version, rows) = WriteDemoTable(AmbientOpener.Current, _path, ct);
            var batch = new RecordBatch(_schema, new IArrowArray[]
            {
                new Int64Array.Builder().Append(version).Build(),
                new Int64Array.Builder().Append(rows).Build(),
            }, length: 1);
            return Single(batch);
        }

        private static async IAsyncEnumerable<RecordBatch> Single(RecordBatch batch)
        {
            yield return batch;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose() { }
    }

    private static (long Version, long Rows) WriteDemoTable(nint opener, string path, CancellationToken ct)
    {
        var schema = new Schema(new[]
        {
            new Field("id", Int64Type.Default, nullable: false),
            new Field("name", StringType.Default, nullable: false),
        }, metadata: null);

        const int rows = 5;
        var ids = new Int64Array.Builder();
        var names = new StringArray.Builder();
        for (long i = 1; i <= rows; i++)
        {
            ids.Append(i);
            names.Append($"row_{i}");
        }
        var batch = new RecordBatch(schema, new IArrowArray[] { ids.Build(), names.Build() }, rows);
        long version = DeltaWriter.WriteOverwrite(opener, path, schema, new[] { batch }, ct);
        return (version, rows);
    }
}

/// <summary>Resolved per-write tuning for a Delta write, assembled by <see cref="DeltaCatalog"/> from the ATTACH
/// options (catalog defaults) overlaid with the session <c>delta_write_options</c> JSON setting, plus the
/// partition columns (native <c>PARTITIONED BY</c> clause, else the setting's <c>partition_by</c>). Any null
/// member keeps engineered-wood's default. Applied at CREATE/INSERT/CTAS/COPY (partition columns take effect at
/// table creation and are thereafter preserved from the table metadata for all writes, incl. UPDATE/DELETE).</summary>
/// <summary>How a Delta write reconciles the incoming source schema with the table schema.</summary>
internal enum DeltaSchemaMode
{
    /// <summary>Default: honor the write mode's normal schema handling (Overwrite adopts the incoming schema —
    /// a true replace; Append is strict — extra source columns are dropped, missing ones read NULL).</summary>
    None,
    /// <summary>Append + UNION: add any incoming column not in the table (nullable) before appending. The
    /// delta-rs <c>schema_mode="merge"</c>. Reaches the provider via COPY (INSERT is binder-checked).</summary>
    Merge,
    /// <summary>Replace data AND schema: the table adopts exactly the incoming source schema (add/drop/retype).
    /// The delta-rs <c>schema_mode="overwrite"</c>.</summary>
    Overwrite,
}

internal sealed record DeltaWriteSpec(
    EngineeredWood.Compression.CompressionCodec? Compression,
    int? RowGroupSize,
    IReadOnlyList<string>? BloomFilterColumns,
    IReadOnlyList<string>? PartitionColumns,
    // replace_where: a partition column→value map. When set on an INSERT, the write becomes an ATOMIC
    // partition-overwrite (remove the matching partition's files + add the new data, one commit) instead of an
    // append. Keys must be partition columns of the table (engineered-wood enforces this).
    IReadOnlyDictionary<string, string>? ReplaceWhere = null,
    // schema_mode (SCHEMA_MODE COPY option / delta_write_options): Merge = append+union new columns;
    // Overwrite = replace data + adopt the incoming schema. None = write-mode default.
    DeltaSchemaMode SchemaMode = DeltaSchemaMode.None,
    // PARTITION_OVERWRITE COPY option: DYNAMIC partition overwrite (Spark partitionOverwriteMode=dynamic) —
    // the partitions PRESENT IN THE INPUT are atomically replaced in one commit (their current files removed +
    // the new files added); untouched partitions kept. Append-shaped only; requires a partitioned table.
    // Unlike ReplaceWhere (a STATIC, user-supplied partition filter) the target set is derived from the data.
    bool DynamicPartitionOverwrite = false,
    // CREATE TABLE ... WITH (...) delta.*/fabricator.* property keys (original case), merged into the
    // CREATE's table configuration LAST (a WITH property wins over a derived key). Create-time only —
    // ignored when the write doesn't create the table.
    IReadOnlyDictionary<string, string>? CreateProperties = null);

/// <summary>
/// Writes a Delta Lake table via engineered-wood through the host FileSystem (the <see cref="DuckDbTableFileSystem"/>
/// write side + the put-if-absent EXCLUSIVE_CREATE commit). Forces <c>OmitPathInSchema=false</c> so the parquet
/// footer carries the REQUIRED <c>path_in_schema</c> field — otherwise standard readers (DuckDB/arrow-rs/Fabric)
/// reject the file. The opener is the calling operator's ClientContext (valid for the write call).
/// </summary>
internal static class DeltaWriter
{
    private static readonly Microsoft.Extensions.Logging.ILogger Log =
        FabricatorLog.CreateLogger("Fabricator.Delta.Write");

    // Compact one-line summary of the write's feature flags + tuning for the log.
    // The table's display name for constraint errors: the last path segment.
    private static string TableNameFromPath(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
    }

    private static string DescribeSpec(DeltaWriteSpec? spec, bool dv, bool rowTracking, bool ict, bool cdf)
    {
        var parts = new List<string>();
        if (dv) { parts.Add("deletion_vectors"); }
        if (rowTracking) { parts.Add("row_tracking"); }
        if (ict) { parts.Add("in_commit_timestamps"); }
        if (cdf) { parts.Add("change_data_feed"); }
        if (spec?.PartitionColumns is { Count: > 0 } pc) { parts.Add("partition_by=" + string.Join("/", pc)); }
        if (spec?.SchemaMode is { } sm && sm != DeltaSchemaMode.None) { parts.Add("schema_mode=" + sm); }
        if (spec?.ReplaceWhere is { Count: > 0 }) { parts.Add("replace_where"); }
        if (spec?.DynamicPartitionOverwrite == true) { parts.Add("partition_overwrite"); }
        return parts.Count == 0 ? "plain" : string.Join(",", parts);
    }

    /// <summary>Delta table options for ALL engineered-wood writes (initial write AND the copy-on-write DELETE
    /// rewrite): the parquet writer MUST emit <c>path_in_schema</c> (OmitPathInSchema=false) or standard readers
    /// (delta-kernel / Spark / Fabric) reject the footer with <c>TProtocolException: Invalid data</c>.
    /// <paramref name="spec"/> (null =&gt; defaults) carries the resolved per-write tuning (compression /
    /// row-group size / bloom-filter columns) from the ATTACH options + the <c>delta_write_options</c> setting.</summary>
    /// <summary>Read options for ALL engineered-wood data-file reads: decimals widened to the classic
    /// Decimal128/256 (EW master defaults to the physical-width Decimal32/64, which are mishandled crossing
    /// the Arrow C data interface to DuckDB — read as 128-bit over the 4/8-byte buffer ⇒ corruption; the
    /// widening is lossless, EW's decoders sign-extend). An optional row-group <paramref name="filter"/>
    /// (+ bloom probing) composes on top.</summary>
    internal static ParquetReadOptions ReadOptions(EngineeredWood.Expressions.Predicate? filter = null) =>
        filter is null
            ? ParquetReadOptions.Default with { DecimalOutput = DecimalOutputKind.Decimal128 }
            : new ParquetReadOptions
            {
                Filter = filter,
                FilterUseBloomFilters = true,
                DecimalOutput = DecimalOutputKind.Decimal128,
            };

    internal static DeltaTableOptions Options(DeltaWriteSpec? spec = null,
                                              IDataFileWriter? dataFileWriter = null,
                                              IDataFileReader? dataFileReader = null) => DeltaTableOptions.Default with
    {
        ParquetReadOptions = ReadOptions(),
        ParquetWriteOptions = new ParquetWriteOptions
        {
            OmitPathInSchema = false, // REQUIRED field — standard readers (DuckDB/arrow-rs/Fabric) reject without it
            RowGroupMaxRows = spec?.RowGroupSize ?? 122880, // default = DuckDB's row-group size
            Compression = spec?.Compression ?? EngineeredWood.Compression.CompressionCodec.Snappy,
            BloomFilterColumns = spec?.BloomFilterColumns is { Count: > 0 } bloom
                ? new HashSet<string>(bloom, System.StringComparer.Ordinal)
                : null,
        },
        // native_write: DuckDB's parquet writer produces the data files; engineered-wood keeps the _delta_log.
        // (The former IDataFileRewriter — DuckDB applying the DELETE/UPDATE transform in SQL — was dropped
        // upstream: EW master owns the rewrite semantics via its rowid DML; only the encoding seams remain.)
        DataFileWriter = dataFileWriter,
        // native_read: DuckDB's read_parquet decodes data files for the rewrite/compaction READ halves (raw
        // physical batches in file order) — with the writer, the full host codec pair (variant-preserving).
        DataFileReader = dataFileReader,
    };

    // Catalog tables are written as PLAIN Delta — NO table features (no row tracking, no deletion vectors).
    // The DuckDB rowid for UPDATE/DELETE is a TRANSIENT (file, position) rowid computed at scan time
    // (engineered-wood ReadAllWithRowIdsAsync), and DELETE is copy-on-write (rewrite the file, plain add/remove).
    // Plain Delta (minReader 1 / minWriter 2) is maximally reader-compatible — Fabric OneLake conversion + Spark
    // can't read our row-tracking/deletion-vector commits (engineered-wood's DV format isn't Spark-compatible).

    // Opt-in table features for the deletion-vector fast-delete mode (DELETE marks rows in a DV instead of
    // rewriting the file). Row tracking is enabled alongside (per the chosen design); both are declared in the
    // protocol by CreateAsync (reader v3 + deletionVectors/rowTracking/domainMetadata). A table created with
    // this config is recognized by DeltaReader.IsDeletionVectorsEnabled so DELETE picks the DV path.
    /// <summary>Builds the create-time table config for the opt-in features, or null when none are enabled.
    /// <paramref name="deletionVectors"/> => DV + row-tracking fast-delete; <paramref name="inCommitTimestamps"/>
    /// => <c>delta.enableInCommitTimestamps</c> (a WRITER-only feature) so AT (TIMESTAMP =&gt; ...) time travel
    /// can resolve a timestamp to a version.</summary>
    // Delta materialized row-tracking column names (opt-in). Declaring these makes a spec reader (Spark) expose
    // `_metadata.row_id`/`_metadata.row_commit_version`; a rewrite that materializes these physical columns
    // preserves the original stable id across UPDATE/compaction. The row-id column name matches what
    // engineered-wood's RowTrackingWriter writes (`__delta_row_id`).
    internal const string MaterializedRowIdColumn = "__delta_row_id";
    internal const string MaterializedRowCommitVersionColumn = "__delta_row_commit_version";

    /// <summary>Table config key persisting the CREATE-time <c>SORTED BY</c> columns (a JSON string array) —
    /// our own key (not a delta.* feature; readable/changeable via fabricator_delta_(set_)tblproperties).
    /// Appends without an explicit clause read it back and re-apply the ORDER BY, so the table's files KEEP
    /// the clustered layout across INSERTs.</summary>
    internal const string SortedByKey = "fabricator.sortedBy";

    internal static string SerializeSortedBy(IReadOnlyList<string> cols)
        => System.Text.Json.JsonSerializer.Serialize(cols);

    internal static IReadOnlyList<string>? ParseSortedBy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var cols = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json!);
            return cols is { Count: > 0 } ? cols : null;
        }
        catch
        {
            return null; // a hand-edited/unparseable property never fails a write — ordering is advisory
        }
    }

    private static Dictionary<string, string>? CreateConfig(
        bool deletionVectors, bool rowTracking, bool inCommitTimestamps, bool changeDataFeed,
        bool serializable = false, IReadOnlyList<string>? sortedBy = null,
        IReadOnlyDictionary<string, string>? extraProperties = null)
    {
        if (!deletionVectors && !rowTracking && !inCommitTimestamps && !changeDataFeed && !serializable
            && sortedBy is not { Count: > 0 } && extraProperties is not { Count: > 0 })
        {
            return null;
        }
        var config = new Dictionary<string, string>(System.StringComparer.Ordinal);
        if (sortedBy is { Count: > 0 })
        {
            // CREATE ... SORTED BY (cols): persist the ordered-write spec so later appends re-apply it.
            config[SortedByKey] = SerializeSortedBy(sortedBy);
        }
        if (serializable)
        {
            // Stamp the ATTACH isolation_level 'serializable' onto CREATEd tables so the table SELF-DECLARES
            // its guarantee (all writers, us + Spark, then honor it uniformly). write_serializable is the
            // Spark default => left ABSENT (no stamp), matching Spark's minimal metadata.
            config["delta.isolationLevel"] = "Serializable";
        }
        if (deletionVectors)
        {
            config["delta.enableDeletionVectors"] = "true";
            config["delta.enableRowTracking"] = "true"; // DV mode keeps row tracking (unchanged behavior)
        }
        if (rowTracking)
        {
            // Standalone row tracking (writer feature), independent of deletion vectors.
            config["delta.enableRowTracking"] = "true";
        }
        if (inCommitTimestamps)
        {
            config["delta.enableInCommitTimestamps"] = "true";
        }
        if (changeDataFeed)
        {
            config["delta.enableChangeDataFeed"] = "true";
        }
        if (deletionVectors || rowTracking)
        {
            // Row tracking IMPLIES materialization (Spark parity: enableRowTracking promises ids stable
            // across rewrites, implemented via the materialized columns — Spark auto-declares them at
            // enablement). Rewrites (merge-on-read post-images, compaction, buffered UPDATE post-images)
            // bake each row's ORIGINAL id into the declared column; plain appends never materialize
            // (readers derive baseRowId + position). The old opt-in `materialize_row_tracking` ATTACH
            // option is gone — the declaration always rides row tracking.
            config["delta.rowTracking.materializedRowIdColumnName"] = MaterializedRowIdColumn;
            config["delta.rowTracking.materializedRowCommitVersionColumnName"] = MaterializedRowCommitVersionColumn;
        }
        if (extraProperties is not null)
        {
            // CREATE TABLE ... WITH (...) delta.*/fabricator.* properties — merged LAST so a WITH property
            // wins over a derived key (e.g. delta.isolationLevel). Feature-enabling spellings were rejected
            // at parse (DeltaWithOptions.GuardPropertyKey), so nothing here changes the protocol demands.
            foreach (var kv in extraProperties)
            {
                config[kv.Key] = kv.Value;
            }
        }
        return config;
    }

    // Optimistic-concurrency retry bound for commits. A concurrent writer that commits our target version
    // first makes engineered-wood throw DeltaConflictException; we reopen (picking up the new latest version)
    // and retry. Safe for append/overwrite/create — the data doesn't depend on the conflicting commit. Rowid
    // DELETE/UPDATE do NOT retry (their absolute positions are tied to the scanned snapshot — a concurrent
    // change invalidates them; DeltaReader surfaces a clear conflict error instead).
    internal const int MaxCommitAttempts = 16;

    /// <summary>Opens-or-creates the Delta table at <paramref name="path"/> and writes <paramref name="batches"/>
    /// in <paramref name="mode"/> (Overwrite for CTAS/REPLACE, Append for INSERT). Returns the committed version.
    /// <paramref name="deletionVectors"/> enables the DV+rowTracking features on a NEW table (opt-in fast-delete).
    /// Retries on a commit conflict (concurrent writer) by reopening at the new latest version (OCC).</summary>
    public static long Write(nint opener, string path, Schema schema, IReadOnlyList<RecordBatch> batches,
                             DeltaWriteMode mode, CancellationToken ct, bool deletionVectors = false,
                             bool inCommitTimestamps = false, bool changeDataFeed = false,
                             bool rowTracking = false, DeltaWriteSpec? spec = null, bool nativeWrite = false,
                             EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping =
                                 EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None,
                             bool serializable = false, IReadOnlyList<string>? sortedBy = null)
        => WriteAsync(opener, path, schema, batches, mode, ct, deletionVectors, inCommitTimestamps,
                      changeDataFeed, rowTracking, spec, nativeWrite, columnMapping, serializable, sortedBy)
            .GetAwaiter().GetResult();

    private static async Task<long> WriteAsync(nint opener, string path, Schema schema,
                             IReadOnlyList<RecordBatch> batches,
                             DeltaWriteMode mode, CancellationToken ct, bool deletionVectors,
                             bool inCommitTimestamps, bool changeDataFeed,
                             bool rowTracking, DeltaWriteSpec? spec, bool nativeWrite,
                             EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
                             bool serializable, IReadOnlyList<string>? sortedBy = null)
    {
        // native_write: DuckDB's parquet writer produces the data-file bytes (via COPY on a fresh host
        // connection); engineered-wood keeps the _delta_log commit. Falls back to EW's codec if host_query is
        // unavailable. Only the data write is affected — partitioning/stats/row-tracking/commit are unchanged.
        // The resolved tuning (compression/row-group size) rides into the writer's COPY options.
        var dataFileWriter = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path, spec)
            : null;
        long totalRows = 0;
        foreach (var b in batches) { totalRows += b.Length; }
        Log.LogInformation(
            "delta write {Path}: mode={Mode} rows={Rows} batches={Batches} writer={Writer} spec=[{Spec}]",
            path, mode, totalRows, batches.Count, dataFileWriter is null ? "engineered-wood" : "native-duckdb",
            DescribeSpec(spec, deletionVectors, rowTracking, inCommitTimestamps, changeDataFeed));
        for (int attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                Log.LogWarning("delta write {Path}: commit conflict — reopening at latest (attempt {Attempt}/{Max})",
                    path, attempt, MaxCommitAttempts);
            }
            var fs = TableFileSystems.Create(opener, path);
            var table = await DeltaTable.OpenOrCreateAsync(fs, schema, Options(spec, dataFileWriter),
                                                     partitionColumns: spec?.PartitionColumns,
                                                     configuration: CreateConfig(deletionVectors, rowTracking, inCommitTimestamps, changeDataFeed, serializable, sortedBy, spec?.CreateProperties),
                                                     columnMappingMode: columnMapping,
                                                     cancellationToken: ct,
                                                     clusteringColumns: spec?.PartitionColumns is { Count: > 0 } ? null : sortedBy).ConfigureAwait(false);
            try
            {
                // NOT NULL enforcement: an APPEND into an existing table must honor its declared nullability
                // (incl. nested struct/list/map constraints on external tables). Overwrite/replace adopts the
                // INPUT schema (nullable), matching drop+recreate semantics — nothing to enforce there.
                if (mode == DeltaWriteMode.Append)
                {
                    DeltaNullability.ValidateBatches(
                        batches, table.CurrentSnapshot.Schema, TableNameFromPath(path));
                }
                // NESTED columns + column mapping are handled by engineered-wood's RECURSIVE physical-rename
                // + field-id stamping (ColumnMappingRecursive.ToPhysical in WriteCoreAsync), so this collect
                // path writes the spec nested layout too — the old top-level-only gate is lifted.
                if (mode == DeltaWriteMode.Overwrite)
                {
                    // A true replace (CREATE OR REPLACE / CTAS-replace / COPY REPLACE / schema_mode=overwrite):
                    // the table adopts EXACTLY the incoming schema (add/drop/retype) — a metadata-only commit,
                    // no-op if identical or a freshly-created table — then the Overwrite removes the old files.
                    // On a column-mapping table EW re-assigns fresh field ids for the new schema (sound because the
                    // Overwrite drops the old-schema files); a fresh CTAS is a no-op (schema already matches).
                    await table.SetSchemaAsync(schema, ct).ConfigureAwait(false);
                }
                else if (spec?.SchemaMode == DeltaSchemaMode.Merge)
                {
                    // Append + UNION: add any incoming column absent from the table (nullable) before appending.
                    await MergeSchemaAsync(table, schema, ct).ConfigureAwait(false);
                }
                // Repartition-on-overwrite: PARTITION_COLUMNS on a FULL overwrite of an EXISTING table whose
                // partitioning differs → the new partitionColumns commit atomically with the file swap
                // (protocol-legal only then; Spark's overwriteSchema + new partitionBy). A fresh create
                // already matches (OpenOrCreate applied them) → no-op.
                System.Collections.Generic.IReadOnlyList<string>? repartitionTo = null;
                if (mode == DeltaWriteMode.Overwrite
                    && spec?.ReplaceWhere is not { Count: > 0 }
                    && spec?.DynamicPartitionOverwrite != true
                    && spec?.PartitionColumns is { Count: > 0 } reqCols
                    && !System.Linq.Enumerable.SequenceEqual(
                           reqCols, table.CurrentSnapshot.Metadata.PartitionColumns))
                {
                    repartitionTo = reqCols;
                }
                // replace_where => STATIC partition-overwrite; PARTITION_OVERWRITE => DYNAMIC (partitions present
                // in the input); otherwise the requested append/overwrite. Each is one atomic commit.
                long version = spec?.ReplaceWhere is { Count: > 0 } parts
                    ? await table.OverwritePartitionsAsync(batches, parts, ct).ConfigureAwait(false)
                    : spec?.DynamicPartitionOverwrite == true
                        ? await table.DynamicOverwriteAsync(batches, ct).ConfigureAwait(false)
                        : await table.WriteAsync(batches, mode, ct, repartitionTo: repartitionTo)
                            .ConfigureAwait(false);
                Log.LogInformation("delta write {Path}: committed v{Version}", path, version);
                return version;
            }
            catch (EngineeredWood.DeltaLake.DeltaConflictException) when (attempt < MaxCommitAttempts)
            {
                // Concurrent writer took our version — reopen + retry (append/overwrite is snapshot-independent).
            }
            finally
            {
                await table.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// STREAMING native write (bounded memory — for a Fabric notebook where RAM is limited): streams
    /// <paramref name="data"/> straight into ONE parquet file via DuckDB's native <c>COPY</c> (the stream is
    /// pull-based, so the whole dataset never lands in C# memory), then commits the single <c>add</c> via
    /// engineered-wood's commit-only <see cref="DeltaTable.CommitDataFilesAsync"/>. This is the <b>native_write</b>
    /// counterpart of <see cref="Write"/> that avoids the <see cref="Materialize"/> full-collect.
    /// <para>Returns the committed version, or <c>null</c> when streaming does NOT apply and the caller must fall
    /// back to the collect path (<see cref="Materialize"/> + <see cref="Write"/>): a partitioned target (native or
    /// existing), <c>replace_where</c>, <c>schema_mode=merge</c>, or a table needing engineered-wood's own writer
    /// (column mapping / identity / IcebergCompat). On <c>null</c> NO file was written (checked before COPY), so
    /// there is no orphan.</para>
    /// </summary>
    /// <summary>
    /// Eager-write plan, slice B: stream a buffered-transaction CTAS's data files for a table that does
    /// NOT exist yet — nothing touches the <c>_delta_log</c> (the flush's CREATE with
    /// <paramref name="assignedSchema"/> + one commit references them; ROLLBACK leaves orphan parquet in
    /// a log-less folder, invisible to every reader). Column-mapping physical names/ids are assigned
    /// HERE (they are random GUIDs — the create must reuse them, never re-assign). Non-partitioned only
    /// (the partitioned pending-create keeps the collect path); returns null when streaming is
    /// unavailable, leaving <paramref name="data"/> unconsumed for the collect path.
    /// </summary>
    public static List<WrittenDataFile>? TryStreamCreateFiles(
        nint opener, string path, IArrowArrayStream data,
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
        out long rowsWritten, out EngineeredWood.DeltaLake.Schema.StructType? assignedSchema,
        IReadOnlyList<string>? partitionColumns = null, DeltaWriteSpec? spec = null)
    {
        rowsWritten = 0;
        assignedSchema = null;
        if (!NativeParquetDataFileWriter.Available)
        {
            return null;
        }
        var writableRoot = DeltaReader.ToReadableRoot(path);
        string? fieldIdsSpec = null;
        Schema statsSchema = data.Schema;
        IArrowArrayStream copySource = data;
        IReadOnlyList<string> copyPartCols = partitionColumns ?? System.Array.Empty<string>();
        if (columnMapping != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None)
        {
            var delta = EngineeredWood.DeltaLake.Schema.SchemaConverter.FromArrowSchema(data.Schema);
            var (mapped, _) = EngineeredWood.DeltaLake.Schema.ColumnMapping.AssignColumnMapping(delta);
            assignedSchema = mapped;
            // Same physical layout the open-table streaming path emits: recursive physical rename +
            // FIELD_IDS + physical-keyed stats, all driven by the just-assigned schema. Partitioned:
            // PARTITION_BY + partitionValues keys use the PHYSICAL names, partition columns excluded
            // from the files/FIELD_IDS (the Delta/Spark convention — see TryWriteStreaming).
            copySource = ArrowColumnMappingRename.Wrap(data, mapped, toPhysical: true);
            var renameToPhysical = EngineeredWood.DeltaLake.Schema.ColumnMapping.BuildLogicalToPhysicalMap(
                mapped, columnMapping);
            fieldIdsSpec = BuildFieldIdsSpec(mapped,
                copyPartCols.Count > 0
                    ? new HashSet<string>(copyPartCols, System.StringComparer.Ordinal)
                    : null);
            var physFields = new List<Field>(data.Schema.FieldsList.Count);
            foreach (var f in data.Schema.FieldsList)
            {
                physFields.Add(renameToPhysical.TryGetValue(f.Name, out var pn) && pn != f.Name
                    ? new Field(pn, f.DataType, f.IsNullable)
                    : f);
            }
            statsSchema = new Schema(physFields, null);
            if (copyPartCols.Count > 0)
            {
                var phys = new List<string>(copyPartCols.Count);
                foreach (var pc in copyPartCols)
                {
                    phys.Add(renameToPhysical.TryGetValue(pc, out var pp) ? pp : pc);
                }
                copyPartCols = phys;
            }
        }
        // The table FOLDER does not exist yet (no create ran) — make it (recursive, best-effort: object
        // stores have implicit dirs and the blob write creates the path anyway).
        try { HostFs.CreateDir(AmbientOpener.Current, writableRoot); }
        catch { /* implicit dirs / unimplemented CreateDirectory */ }
        if (copyPartCols.Count > 0)
        {
            // Partitioned pending CTAS: one COPY PARTITION_BY streams the Hive layout; each file's
            // partitionValues (physical-keyed under mapping) ride the WrittenDataFile into the flush's
            // commit — and into the pending-file read-back's typed literals.
            var copied = NativeParquetDataFileWriter.RunCopyPartitioned(
                writableRoot, copyPartCols, copySource, default, statsSchema: statsSchema,
                fieldIdsSpec: fieldIdsSpec, spec: spec);
            var pfiles = new List<WrittenDataFile>(copied.Count);
            long ptotal = 0;
            foreach (var cf in copied)
            {
                if (cf.Rows == 0) { continue; }
                ptotal += cf.Rows;
                pfiles.Add(new WrittenDataFile(cf.RelativePath, cf.Size, cf.Rows, cf.PartitionValues, cf.Stats));
            }
            rowsWritten = ptotal;
            return pfiles;
        }
        string fileRel = $"{System.Guid.NewGuid():N}.parquet";
        var (rows, size, stats) = NativeParquetDataFileWriter.RunCopy(
            writableRoot, fileRel, copySource, default, statsSchema: statsSchema, fieldIdsSpec: fieldIdsSpec,
            spec: spec);
        rowsWritten = rows;
        return rows > 0
            ? new List<WrittenDataFile> { new(fileRel, size, rows, null, stats) }
            : new List<WrittenDataFile>();
    }

    public static long? TryWriteStreaming(
        nint opener, string path, IArrowArrayStream data, DeltaWriteMode mode,
        bool deletionVectors, bool inCommitTimestamps, bool changeDataFeed, bool rowTracking,
        DeltaWriteSpec? spec, out long rowsWritten,
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping =
            EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None,
        EngineeredWood.DeltaLake.Schema.StructType? pendingSchema = null,
        List<WrittenDataFile>? deferCommitTo = null,
        bool serializable = false, IReadOnlyList<string>? sortedBy = null)
    {
        var (result, rows) = TryWriteStreamingCoreAsync(opener, path, data, mode, deletionVectors,
            inCommitTimestamps, changeDataFeed, rowTracking, spec, columnMapping, pendingSchema,
            deferCommitTo, serializable, sortedBy).GetAwaiter().GetResult();
        rowsWritten = rows;
        return result;
    }

    private static async Task<(long? Result, long RowsWritten)> TryWriteStreamingCoreAsync(
        nint opener, string path, IArrowArrayStream data, DeltaWriteMode mode,
        bool deletionVectors, bool inCommitTimestamps, bool changeDataFeed, bool rowTracking,
        DeltaWriteSpec? spec,
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
        EngineeredWood.DeltaLake.Schema.StructType? pendingSchema,
        List<WrittenDataFile>? deferCommitTo,
        bool serializable, IReadOnlyList<string>? sortedBy = null)
    {
        // Transaction-deferred commit: the caller (an explicit-transaction append) wants the files WRITTEN
        // but the Delta commit PARKED — CommitTransaction flushes everything as one atomic commit. Only a
        // plain Append can defer (an Overwrite's removes are snapshot-coupled).
        if (deferCommitTo is not null && (mode != DeltaWriteMode.Append || spec?.DynamicPartitionOverwrite == true))
        {
            throw new System.InvalidOperationException(
                "TryWriteStreaming: only a plain Append can defer its commit.");
        }
        // pendingSchema = a buffered transaction's PENDING (ALTERed) schema, deferred appends only: the
        // input carries columns the committed snapshot doesn't know yet, so the pending schema drives the
        // NOT NULL wrap + the mapping rename/FIELD_IDS/stats keying below; the paired metaData action
        // joins the fused commit at flush.
        if (pendingSchema is not null && deferCommitTo is null)
        {
            throw new System.InvalidOperationException(
                "TryWriteStreaming: pendingSchema requires a deferred (transaction-buffered) append.");
        }
        long rowsWritten = 0;
        // Cases the streaming commit can't represent → fall back to the batch path.
        if (spec?.ReplaceWhere is { Count: > 0 }) { return (null, rowsWritten); }
        if (spec?.SchemaMode == DeltaSchemaMode.Merge) { return (null, rowsWritten); }

        var writableRoot = DeltaReader.ToReadableRoot(path);
        var fs = TableFileSystems.Create(opener, path);
        // Pass columnMapping so a NEW table is created WITH the mode (an existing table keeps its own mode). Only
        // id mode reaches here as a mapping table (name mode stays on the codec path, gated by the caller) — an
        // id-mode table's data files carry field_ids, which the external commit + native reader both handle.
        var table = await DeltaTable.OpenOrCreateAsync(
            fs, data.Schema, Options(spec),
            partitionColumns: spec?.PartitionColumns,   // set on a partitioned CTAS create; ignored for an INSERT
            configuration: CreateConfig(deletionVectors, rowTracking, inCommitTimestamps, changeDataFeed, serializable, sortedBy, spec?.CreateProperties),
            columnMappingMode: columnMapping,
            cancellationToken: default,
            clusteringColumns: spec?.PartitionColumns is { Count: > 0 } ? null : sortedBy).ConfigureAwait(false);
        try
        {
            // Fall back BEFORE writing any file / touching the log (so a fallback leaves no orphan): a table
            // needing engineered-wood's own writer (identity / iceberg) can't use external commit. A
            // column-mapping table (BOTH modes) CAN stream: the COPY renames the columns to their PHYSICAL names
            // (Delta spec: data files use physical names in both modes) and stamps the field_ids (FIELD_IDS).
            if (!table.SupportsExternalDataFileCommit)
            {
                return (null, rowsWritten);
            }
            // Repartition-on-overwrite (PARTITION_COLUMNS differing from an EXISTING table's partitioning):
            // the streaming commit has no metaData-swap support → collect path, whose WriteAsync(repartitionTo:)
            // folds the new partitionColumns into the overwrite commit. A fresh create already matches
            // (OpenOrCreate above applied them) → streams as usual. Checked BEFORE SetSchemaAsync so a
            // fallback leaves no half-done metadata commit behind.
            if (mode == DeltaWriteMode.Overwrite
                && spec?.PartitionColumns is { Count: > 0 } reqPartCols
                && !System.Linq.Enumerable.SequenceEqual(
                       reqPartCols, table.CurrentSnapshot.Metadata.PartitionColumns))
            {
                return (null, rowsWritten);
            }
            // A REPLACE that CHANGES a mapping table's schema: adopt the new schema FIRST (SetSchemaAsync's
            // mapping branch assigns fresh field ids — sound because the paired Overwrite removes the old
            // files), so the maps / FIELD_IDS / COPY below are built from the NEW schema and the write can
            // STREAM (previously this shape fell back to the collect path). Metadata-only commit; the same
            // two-commit shape the collect path used.
            if (mode == DeltaWriteMode.Overwrite
                && EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(
                       table.CurrentSnapshot.Metadata.Configuration)
                   != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None
                && !SameLogicalColumns(table.ArrowSchema, data.Schema))
            {
                await table.SetSchemaAsync(data.Schema, default).ConfigureAwait(false);
            }
            // NOT NULL enforcement on the streamed APPEND: wrap the input with the per-batch validator
            // (lazy — a later fallback `return null` leaves the stream unconsumed for the collect path,
            // which validates on its own). Overwrite adopts the input schema — nothing to enforce.
            if (mode == DeltaWriteMode.Append)
            {
                data = DeltaNullability.Wrap(data, pendingSchema ?? table.CurrentSnapshot.Schema,
                                             TableNameFromPath(path));
            }
            var partCols = table.CurrentSnapshot.Metadata.PartitionColumns;
            var mappingMode = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(
                table.CurrentSnapshot.Metadata.Configuration);
            string? fieldIdsSpec = null;
            Schema statsSchema = data.Schema;
            IReadOnlyList<string> copyPartCols = partCols;
            IArrowArrayStream copySource = data;
            if (mappingMode != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None)
            {
                var snapSchema = pendingSchema ?? table.CurrentSnapshot.Schema;
                var renameToPhysical = EngineeredWood.DeltaLake.Schema.ColumnMapping.BuildLogicalToPhysicalMap(
                    snapSchema, mappingMode);
                // The COPY must emit the PHYSICAL layout at EVERY level (Delta spec: data files use physical
                // names in both modes — nested struct fields included, which a flat SELECT alias cannot rename).
                // Rename the input stream itself (a zero-copy Arrow type-tree rewrap); the COPY then writes
                // SELECT * of the already-physical stream. Lazy — nothing is consumed until the COPY pulls, so
                // the fallback `return null` paths below leave `data` intact for the collect path.
                copySource = ArrowColumnMappingRename.Wrap(data, snapSchema, toPhysical: true);
                // Partitioned mapping table: PARTITION_BY uses the PHYSICAL column names (matching the renamed
                // stream), so RETURN_STATS.partition_keys — and therefore the committed partitionValues — come
                // back keyed PHYSICAL, the Delta-spec convention Spark uses (physical keys survive a
                // partition-column RENAME). Directory names follow; readers treat paths as opaque.
                if (partCols.Count > 0)
                {
                    var phys = new List<string>(partCols.Count);
                    foreach (var pc in partCols)
                    {
                        phys.Add(renameToPhysical.TryGetValue(pc, out var p) ? p : pc);
                    }
                    copyPartCols = phys;
                }
                // FIELD_IDS keyed by the COPY's OUTPUT (physical) names — RECURSIVE for struct fields (the
                // __duckdb_field_id sentinel), so nested columns carry their delta.columnMapping.id in the
                // parquet. Partition columns are excluded (COPY PARTITION_BY leaves them out of the files).
                fieldIdsSpec = BuildFieldIdsSpec(snapSchema,
                    partCols.Count > 0 ? new HashSet<string>(partCols, System.StringComparer.Ordinal) : null);
                // Stats in the Delta log are keyed by the PHYSICAL column names (spec) — type them from a
                // physical-renamed copy of the write schema so BuildDeltaStats emits physical keys.
                var physFields = new List<Field>(data.Schema.FieldsList.Count);
                foreach (var f in data.Schema.FieldsList)
                {
                    physFields.Add(renameToPhysical.TryGetValue(f.Name, out var p) && p != f.Name
                        ? new Field(p, f.DataType, f.IsNullable)
                        : f);
                }
                statsSchema = new Schema(physFields, null);
            }
            // PARTITION_OVERWRITE requires a partitioned target — with none there is no partition to scope the
            // overwrite to (an unpartitioned "dynamic overwrite" would be a disguised full replace; error instead).
            if (spec?.DynamicPartitionOverwrite == true && partCols.Count == 0)
            {
                throw new System.ArgumentException(
                    "PARTITION_OVERWRITE requires a partitioned table (the target has no partition columns).");
            }
            if (mode == DeltaWriteMode.Overwrite)
            {
                if (mappingMode == EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None)
                {
                    // CREATE OR REPLACE / CTAS-replace / schema_mode=overwrite: adopt the incoming schema
                    // (metadata-only, no-op if identical), then the commit's removes drop the old files.
                    await table.SetSchemaAsync(data.Schema, default).ConfigureAwait(false);
                }
                // (a schema-changing mapping REPLACE already adopted the new schema above, so the maps
                // match; nothing further to do here)
            }

            // RETURN_STATS gives per-file row count + byte size + the Delta stats JSON (min/max/nullCount, typed by
            // data.Schema) — so streamed files get FULL data-skipping stats. The whole input is pulled through
            // DuckDB's COPY, never materialized in C# (bounded memory).
            List<WrittenDataFile> files;
            if (partCols.Count > 0)
            {
                // Partitioned: DuckDB COPY PARTITION_BY streams the Hive col=val/ layout in one pass — one+ files
                // per partition, each with its partition values (from RETURN_STATS.partition_keys) + stats. Under
                // mapping the stream was renamed to physical names (all levels) and partitions by the PHYSICAL
                // names, so dirs + partitionValues keys are physical (Delta spec) and FIELD_IDS stamp the files.
                var copied = NativeParquetDataFileWriter.RunCopyPartitioned(
                    writableRoot, copyPartCols, copySource, default, statsSchema: statsSchema,
                    fieldIdsSpec: fieldIdsSpec, spec: spec);
                files = new List<WrittenDataFile>(copied.Count);
                long total = 0;
                foreach (var cf in copied)
                {
                    if (cf.Rows == 0) { continue; }
                    total += cf.Rows;
                    files.Add(new WrittenDataFile(cf.RelativePath, cf.Size, cf.Rows, cf.PartitionValues, cf.Stats));
                }
                rowsWritten = total;
            }
            else
            {
                // Non-partitioned: stream into ONE parquet file (for a mapping table the stream was renamed to
                // physical names at every level + FIELD_IDS stamps the ids recursively; stats keyed physical).
                string fileRel = $"{System.Guid.NewGuid():N}.parquet";
                var (rows, size, stats) = NativeParquetDataFileWriter.RunCopy(
                    writableRoot, fileRel, copySource, default, statsSchema: statsSchema,
                    fieldIdsSpec: fieldIdsSpec, spec: spec);
                rowsWritten = rows;
                // 0 rows → reference no file (an empty COPY output would be an unreferenced orphan); an Overwrite
                // still commits its removes (clears the table), an Append commits an empty version.
                files = rows > 0
                    ? new List<WrittenDataFile> { new(fileRel, size, rows, null, stats) }
                    : new List<WrittenDataFile>();
            }

            if (deferCommitTo is not null)
            {
                // Explicit transaction: files are on storage, the commit is parked — CommitTransaction
                // flushes the whole buffer as ONE Delta commit; ROLLBACK leaves them as invisible orphans.
                deferCommitTo.AddRange(files);
                Log.LogInformation(
                    "delta stream-write {Path}: deferred {Files} file(s) rows={Rows} to the transaction commit",
                    path, files.Count, rowsWritten);
                return (-1L, rowsWritten);
            }
            long version = await table.CommitDataFilesAsync(
                files, mode, dynamicPartitionOverwrite: spec?.DynamicPartitionOverwrite == true,
                cancellationToken: default).ConfigureAwait(false);
            Log.LogInformation(
                "delta stream-write {Path}: committed v{Version} rows={Rows} files={Files} (native COPY, bounded memory)",
                path, version, rowsWritten, files.Count);
            return (version, rowsWritten);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Renders the COPY <c>FIELD_IDS</c> spec from a column-mapping Delta schema: keys are the PHYSICAL
    /// column names (matching the physically-renamed COPY input stream), values the <c>delta.columnMapping.id</c>;
    /// a STRUCT field renders recursively via DuckDB's <c>__duckdb_field_id</c> sentinel so nested columns carry
    /// their ids in the parquet. Struct fields INSIDE a list/map render under DuckDB's structural child names
    /// (<c>'element'</c> / <c>'key'</c>+<c>'value'</c> — those inner nodes carry no Delta id of their own);
    /// plain lists/maps of primitives emit the field's own id as a leaf.
    /// <paramref name="excludeTopLogical"/> drops partition columns (excluded from the data files). Returns null
    /// when no field carries an id (a non-mapping table).</summary>
    internal static string? BuildFieldIdsSpec(
        EngineeredWood.DeltaLake.Schema.StructType schema, ISet<string>? excludeTopLogical)
    {
        var sb = new System.Text.StringBuilder("{");
        bool any = false;
        foreach (var f in schema.Fields)
        {
            if (excludeTopLogical?.Contains(f.Name) == true) { continue; }
            AppendFieldIdEntry(sb, f, ref any);
        }
        sb.Append('}');
        return any ? sb.ToString() : null;
    }

    private static void AppendFieldIdEntry(
        System.Text.StringBuilder sb, EngineeredWood.DeltaLake.Schema.StructField f, ref bool any)
    {
        int? id = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetFieldId(f);
        if (id is null) { return; }
        if (any) { sb.Append(", "); }
        any = true;
        string phys = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(
            f, EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name); // mapping-on ⇒ returns physicalName
        sb.Append('\'').Append(phys.Replace("'", "''")).Append("': ");
        AppendFieldIdValue(sb, f.Type, id.Value);
    }

    // The FIELD_IDS VALUE for one field: a bare id for a leaf; for a struct the __duckdb_field_id sentinel plus
    // the children; for a list/map of structs, the id plus the inner struct's children under DuckDB's structural
    // child names ('element' / 'key'+'value' — those inner nodes carry no Delta id of their own, which DuckDB
    // accepts: a nested dict without the sentinel assigns ids only to its children).
    private static void AppendFieldIdValue(
        System.Text.StringBuilder sb, EngineeredWood.DeltaLake.Schema.DeltaDataType type, int id)
    {
        switch (type)
        {
            case EngineeredWood.DeltaLake.Schema.StructType st:
            {
                sb.Append("{__duckdb_field_id: ").Append(id);
                bool childAny = true; // the sentinel counts as the first entry — children append with ", "
                foreach (var child in st.Fields)
                {
                    AppendFieldIdEntry(sb, child, ref childAny);
                }
                sb.Append('}');
                return;
            }
            case EngineeredWood.DeltaLake.Schema.ArrayType at when ContainsStructDelta(at.ElementType):
            {
                sb.Append("{__duckdb_field_id: ").Append(id).Append(", 'element': ");
                AppendInnerNode(sb, at.ElementType);
                sb.Append('}');
                return;
            }
            case EngineeredWood.DeltaLake.Schema.MapType mt
                when ContainsStructDelta(mt.KeyType) || ContainsStructDelta(mt.ValueType):
            {
                sb.Append("{__duckdb_field_id: ").Append(id);
                if (ContainsStructDelta(mt.KeyType))
                {
                    sb.Append(", 'key': ");
                    AppendInnerNode(sb, mt.KeyType);
                }
                if (ContainsStructDelta(mt.ValueType))
                {
                    sb.Append(", 'value': ");
                    AppendInnerNode(sb, mt.ValueType);
                }
                sb.Append('}');
                return;
            }
            default:
                sb.Append(id); // primitive / list-of-primitive / map-of-primitive — the field's own id only
                return;
        }
    }

    // An INNER node of a list/map (the 'element' / 'key' / 'value' slot): it has no Delta id of its own —
    // a struct renders its children as entries, a nested list/map recurses one level deeper.
    private static void AppendInnerNode(
        System.Text.StringBuilder sb, EngineeredWood.DeltaLake.Schema.DeltaDataType type)
    {
        switch (type)
        {
            case EngineeredWood.DeltaLake.Schema.StructType st:
            {
                sb.Append('{');
                bool childAny = false;
                foreach (var child in st.Fields)
                {
                    AppendFieldIdEntry(sb, child, ref childAny);
                }
                sb.Append('}');
                return;
            }
            case EngineeredWood.DeltaLake.Schema.ArrayType at:
                sb.Append("{'element': ");
                AppendInnerNode(sb, at.ElementType);
                sb.Append('}');
                return;
            case EngineeredWood.DeltaLake.Schema.MapType mt:
            {
                sb.Append('{');
                bool first = true;
                if (ContainsStructDelta(mt.KeyType))
                {
                    sb.Append("'key': ");
                    AppendInnerNode(sb, mt.KeyType);
                    first = false;
                }
                if (ContainsStructDelta(mt.ValueType))
                {
                    if (!first) { sb.Append(", "); }
                    sb.Append("'value': ");
                    AppendInnerNode(sb, mt.ValueType);
                }
                sb.Append('}');
                return;
            }
            default:
                sb.Append("{}"); // primitive inner slot — nothing to stamp
                return;
        }
    }

    private static bool ContainsStructDelta(EngineeredWood.DeltaLake.Schema.DeltaDataType t) => t switch
    {
        EngineeredWood.DeltaLake.Schema.StructType => true,
        EngineeredWood.DeltaLake.Schema.ArrayType at => ContainsStructDelta(at.ElementType),
        EngineeredWood.DeltaLake.Schema.MapType mt =>
            ContainsStructDelta(mt.KeyType) || ContainsStructDelta(mt.ValueType),
        _ => false,
    };

    /// <summary>True when any field (at any depth) contains a STRUCT — the gate for nested column-mapping
    /// handling (the EW-codec writer maps names/ids at the top level only, so nested + mapping must go through
    /// the native streaming COPY).</summary>
    internal static bool HasNestedColumns(Schema schema)
    {
        foreach (var f in schema.FieldsList)
        {
            if (ContainsStruct(f.DataType)) { return true; }
        }
        return false;
    }

    private static bool ContainsStruct(Apache.Arrow.Types.IArrowType t) => t switch
    {
        Apache.Arrow.Types.StructType => true,
        Apache.Arrow.Types.ListType lt => ContainsStruct(lt.ValueField.DataType),
        Apache.Arrow.Types.LargeListType llt => ContainsStruct(llt.ValueField.DataType),
        Apache.Arrow.Types.MapType mt => ContainsStruct(mt.KeyField.DataType) || ContainsStruct(mt.ValueField.DataType),
        _ => false,
    };

    /// <summary>True when the two schemas declare the same column names in the same order (logical shape; types
    /// not compared — a same-name different-type replace falls back via the streamed COPY's own bind).</summary>
    private static bool SameLogicalColumns(Schema a, Schema b)
    {
        if (a.FieldsList.Count != b.FieldsList.Count) { return false; }
        for (int i = 0; i < a.FieldsList.Count; i++)
        {
            if (!string.Equals(a.FieldsList[i].Name, b.FieldsList[i].Name, System.StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Evolves the table schema for merge_schema: for each field in <paramref name="incoming"/> whose
    /// name is absent from the table's current schema, adds it as a NULLABLE column (engineered-wood
    /// <c>AddColumnAsync</c> — a metadata-only commit; old files read the new column back as NULL). Case-insensitive
    /// name match. Columns the incoming data lacks are left as-is (they read/append as NULL).</summary>
    private static async Task MergeSchemaAsync(DeltaTable table, Schema incoming, CancellationToken ct)
    {
        var existing = new HashSet<string>(
            table.ArrowSchema.FieldsList.Select(f => f.Name), System.StringComparer.OrdinalIgnoreCase);
        foreach (var field in incoming.FieldsList)
        {
            if (existing.Contains(field.Name))
            {
                continue;
            }
            // Add as nullable regardless of the incoming field's flag (a newly-added column must be nullable so
            // pre-existing rows can back-fill NULL).
            var nullableField = new Field(field.Name, field.DataType, nullable: true);
            await table.AddColumnAsync(nullableField, ct).ConfigureAwait(false);
            existing.Add(field.Name);
        }
    }

    public static long WriteOverwrite(nint opener, string path, Schema schema, IReadOnlyList<RecordBatch> batches,
                                      CancellationToken ct) =>
        Write(opener, path, schema, batches, DeltaWriteMode.Overwrite, ct);

    /// <summary>Creates an empty Delta table (commit 0 with the schema, no data) at <paramref name="path"/>.
    /// <paramref name="deletionVectors"/> enables the DV+rowTracking features (opt-in fast-delete).</summary>
    public static void Create(nint opener, string path, Schema schema, CancellationToken ct,
                              bool deletionVectors = false, bool inCommitTimestamps = false,
                              bool changeDataFeed = false, bool rowTracking = false, DeltaWriteSpec? spec = null,
                              EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping =
                                  EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None,
                              EngineeredWood.DeltaLake.Schema.StructType? preAssignedSchema = null,
                              bool serializable = false, IReadOnlyList<string>? sortedBy = null)
        => CreateAsync(opener, path, schema, ct, deletionVectors, inCommitTimestamps, changeDataFeed,
                       rowTracking, spec, columnMapping, preAssignedSchema, serializable, sortedBy)
            .GetAwaiter().GetResult();

    private static async Task CreateAsync(nint opener, string path, Schema schema, CancellationToken ct,
                              bool deletionVectors, bool inCommitTimestamps,
                              bool changeDataFeed, bool rowTracking, DeltaWriteSpec? spec,
                              EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
                              EngineeredWood.DeltaLake.Schema.StructType? preAssignedSchema,
                              bool serializable, IReadOnlyList<string>? sortedBy = null)
    {
        Log.LogInformation("delta create {Path}: cols={Cols} spec=[{Spec}]", path, schema.FieldsList.Count,
            DescribeSpec(spec, deletionVectors, rowTracking, inCommitTimestamps, changeDataFeed));
        for (int attempt = 1; ; attempt++)
        {
            var fs = TableFileSystems.Create(opener, path);
            try
            {
                // OpenOrCreate writes commit-0 for a new table (or opens an existing one — no commit, no conflict).
                var table = await DeltaTable.OpenOrCreateAsync(fs, schema, Options(spec),
                                                         partitionColumns: spec?.PartitionColumns,
                                                         configuration: CreateConfig(deletionVectors, rowTracking, inCommitTimestamps, changeDataFeed, serializable, sortedBy, spec?.CreateProperties),
                                                         columnMappingMode: columnMapping,
                                                         preAssignedSchema: preAssignedSchema,
                                                         cancellationToken: ct,
                                                         clusteringColumns: spec?.PartitionColumns is { Count: > 0 } ? null : sortedBy).ConfigureAwait(false);
                await table.DisposeAsync().ConfigureAwait(false);
                Log.LogDebug("delta create {Path}: opened/created (commit-0 if new)", path);
                return;
            }
            catch (EngineeredWood.DeltaLake.DeltaConflictException) when (attempt < MaxCommitAttempts)
            {
                // A concurrent creator won commit-0 — retry (the next OpenOrCreate will just open it).
            }
        }
    }

    /// <summary>Materializes a (possibly streamed) Arrow stream into independent in-memory batches via an Arrow
    /// IPC round-trip — the source batches may be freed after consumption, and engineered-wood's WriteAsync
    /// needs them retained for one commit. Returns the schema, batches, and total row count.</summary>
    public static (Schema Schema, List<RecordBatch> Batches, long Rows) Materialize(
        IArrowArrayStream stream, CancellationToken ct)
        => MaterializeAsync(stream, ct).GetAwaiter().GetResult();

    private static async Task<(Schema Schema, List<RecordBatch> Batches, long Rows)> MaterializeAsync(
        IArrowArrayStream stream, CancellationToken ct)
    {
        var schema = stream.Schema;
        var ms = new MemoryStream();
        long rows = 0;
        using (var w = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            RecordBatch? b;
            while ((b = await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (b.Length == 0)
                {
                    continue;
                }
                await w.WriteRecordBatchAsync(b, ct).ConfigureAwait(false);
                rows += b.Length;
            }
            await w.WriteEndAsync(ct).ConfigureAwait(false);
        }
        var batches = new List<RecordBatch>();
        ms.Position = 0;
        using (var r = new ArrowStreamReader(ms))
        {
            RecordBatch? b;
            while ((b = await r.ReadNextRecordBatchAsync(ct).ConfigureAwait(false)) is not null)
            {
                batches.Add(b);
            }
        }
        return (schema, batches, rows);
    }
}

/// <summary>
/// <c>fabricator_delta_write(&lt;input&gt;, path := '…')</c> — a connection-free GLOBAL host-FS <b>collector</b>
/// that writes ANY input table (a DuckDB query result) to a Delta Lake table at <c>path</c> on OneLake/ADLS/
/// local (Overwrite), returning one row <c>(version, rows_written)</c>. The collector buffers all input, copies
/// it (via an Arrow IPC round-trip — the input batches are freed after consumption), and commits one Delta
/// version through <see cref="DeltaWriter"/>. The opener is threaded via <see cref="AmbientOpener"/> (set by the
/// host before the collector runs). Single-writer; the commit is put-if-absent (EXCLUSIVE_CREATE). The written
/// table is standard-/Fabric-readable.
/// </summary>
public sealed class DeltaWriteCollectorFunction : ICollectorTableFunction
{
    public string Name => "fabricator_delta_write";

    // The actual input schema is supplied to Bind; this declared schema is only discovery metadata (the operator
    // registers a {TABLE} param that accepts any input).
    public Schema InputSchema { get; } = new Schema(System.Array.Empty<Field>(), metadata: null);

    public Schema Parameters { get; } =
        new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, metadata: null);

    public IArrowCollectorBinding Bind(RecordBatch? args, Schema inputSchema)
    {
        var path = ReadPath(args);
        return new WriteCollectorBinding(path, inputSchema);
    }

    private static string ReadPath(RecordBatch? args)
    {
        if (args is not null)
        {
            for (int i = 0; i < args.ColumnCount; i++)
            {
                if (string.Equals(args.Schema.FieldsList[i].Name, "path", System.StringComparison.OrdinalIgnoreCase)
                    && args.Column(i) is StringArray s && s.Length > 0 && s.GetString(0) is { } p)
                {
                    return p;
                }
            }
        }
        throw new System.ArgumentException("fabricator_delta_write: the 'path' argument is required");
    }

    private sealed class WriteCollectorBinding : IArrowCollectorBinding
    {
        private readonly string _path;
        private readonly Schema _inputSchema;
        private readonly Schema _outputSchema;

        public WriteCollectorBinding(string path, Schema inputSchema)
        {
            _path = path;
            _inputSchema = inputSchema;
            _outputSchema = new Schema(new[]
            {
                new Field("version", Int64Type.Default, nullable: false),
                new Field("rows_written", Int64Type.Default, nullable: false),
            }, metadata: null);
        }

        public Schema OutputSchema => _outputSchema;

        public async IAsyncEnumerable<RecordBatch> Collect(
            IAsyncEnumerable<RecordBatch> allInput, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Capture the opener at entry (the operator set it before pulling); valid for the whole write.
            AmbientOneLakeCredential.Current = null; // connection-free global collector → host-FS path
            var opener = AmbientOpener.Current;

            // Copy the input out of its (transient) Arrow buffers via an IPC round-trip, since the operator frees
            // each batch after it's consumed and we must retain ALL rows to write one Delta commit.
            var ms = new MemoryStream();
            long rows = 0;
            using (var w = new ArrowStreamWriter(ms, _inputSchema, leaveOpen: true))
            {
                await foreach (var b in allInput.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (b.Length == 0)
                    {
                        continue;
                    }
                    await w.WriteRecordBatchAsync(b, ct).ConfigureAwait(false);
                    rows += b.Length;
                }
                await w.WriteEndAsync(ct).ConfigureAwait(false);
            }

            var batches = new List<RecordBatch>();
            ms.Position = 0;
            using (var r = new ArrowStreamReader(ms))
            {
                RecordBatch? b;
                while ((b = await r.ReadNextRecordBatchAsync(ct).ConfigureAwait(false)) is not null)
                {
                    batches.Add(b);
                }
            }

            long version = DeltaWriter.WriteOverwrite(opener, _path, _inputSchema, batches, ct);
            yield return new RecordBatch(_outputSchema, new IArrowArray[]
            {
                new Int64Array.Builder().Append(version).Build(),
                new Int64Array.Builder().Append(rows).Build(),
            }, length: 1);
        }

        public void Dispose() { }
    }
}
