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

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("fabricator_delta_scan: path must not be NULL");
        // The opener (this operator's ClientContext) is valid for the duration of this synchronous bind —
        // read the Delta table's schema now (no data read). This is a connection-free global reader with no
        // Fabric credential, so clear any left on this (reused) execution thread by a prior catalog op → the FS
        // factory uses the host-FS (duckdb-azure) path, not the direct-SDK OneLake filesystem.
        AmbientAdlsCredential.Current = null;
        var schema = DeltaReader.GetSchema(AmbientOpener.Current, path);
        return new DeltaBinding(path, schema);
    }

    private sealed class DeltaBinding : ITableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public DeltaBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;

        // ⚠ THE FILTER IS PUSHED AND STILL NOT CLAIMED, and that is the distinction the two flags exist to
        // express. engineered-wood prunes FILES and ROW GROUPS by the predicate and then never re-checks per
        // row, so the result is a SUPERSET — claiming it would tell DuckDB not to re-apply, which is a wrong
        // answer rather than a missed optimisation.
        public bool SupportsFilterPushdown => false;

        // Claimed since 2026-08-13: engineered-wood reads ONLY the requested columns, and BindingBoundTableFunction
        // now declares the projected schema (it resolves it with the same ProjectionPlan used below, so the
        // batches and the declaration cannot disagree). Exact by nature — there is no "superset of columns".
        public bool SupportsProjectionPushdown => true;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Capture the opener (this operator's ClientContext) NOW — it stays valid for the whole execution,
            // so the lazy stream below can read files through it as the host pulls batches (no materialization).
            // Connection-free global reader → clear any stale Fabric credential (host-FS path, see Bind).
            AmbientAdlsCredential.Current = null;
            var opener = AmbientOpener.Current;
            var spec = scan.Spec;
            // Push the FILTER for file + row-group skipping (doesn't change the result schema). Read the filter
            // constants + map the predicate eagerly (the constants batch is in-memory; this consumes + disposes
            // scan.FilterValues). A node we can't safely push is dropped (superset-safe); DuckDB re-applies.
            var filter = spec?.Filter is { } node
                ? new DeltaFilterBuilder(ReadValues(scan.FilterValues)).Build(node)
                : null;
            // Column PROJECTION: engineered-wood reads only these from the Parquet. ProjectionPlan returns
            // null when everything must be read — nothing pushed, an EMPTY list (the COUNT(*) shape: a
            // zero-field schema is not expressible across the Arrow C interface), or a name this binding does
            // not declare — and null means "all columns", which is what `columns: null` already meant.
            var columns = ProjectionPlan.Columns(_schema, spec?.Columns);
            return DeltaReader.Stream(opener, _path, columns, filter, ct);
        }

        private static IReadOnlyList<object?> ReadValues(IArrowArrayStream? filterValues)
            => FilterConstants.Read(filterValues);

        public void Dispose() { }
    }
}

/// <summary>
/// Reads a pushdown filter's typed constants out of the host's side stream. Shared by the two global Delta
/// readers — <c>fabricator_delta_scan</c> and <c>fabricator_delta_native_scan</c> — which both push the
/// FILTER (file / row-group skipping) and both leave the projection to DuckDB.
/// </summary>
internal static class FilterConstants
{
    public static IReadOnlyList<object?> Read(IArrowArrayStream? filterValues)
        => ReadAsync(filterValues).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<object?>> ReadAsync(IArrowArrayStream? filterValues)
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
}

/// <summary>
/// <c>fabricator_delta_native_scan(path)</c> — read a Delta table by path with <b>DuckDB's own parquet
/// reader</b>, so the read gets its tuned reader + <c>ExternalFileCache</c> (over the <c>onelake://</c>
/// subsystem for OneLake). The exact counterpart of <see cref="DeltaGlobalTableFunction"/>
/// (<c>fabricator_delta_scan</c>), which reads the same table with engineered-wood's C# parquet reader:
/// same argument, same result, different engine below.
///
/// <para>⚠ IT WAS A PHASE-A SPIKE UNTIL 2026-08-13 AND IT SERVED DELETED ROWS. It ran
/// <c>SELECT * FROM read_parquet([&lt;active files&gt;])</c> over a file list resolved at bind, which is
/// enough to read a plain table and nothing else: a deletion vector records the deletion in the LOG and
/// leaves the parquet untouched, so every deleted row came back. MEASURED on the DEFAULT table shape —
/// 10 rows, a DV delete of 3, and this returned all ten while the catalog and <c>fabricator_delta_scan</c>
/// both returned 7. It now delegates to <see cref="DeltaNativeReader"/>, the reader an ATTACH catalog uses
/// under <c>native_read</c>, which resolves files AND their deletion vectors from one snapshot and handles
/// partition columns, column mapping and schema evolution.</para>
///
/// <para>⚠ Pushdown: the FILTER is pushed (file / row-group skipping), the PROJECTION is not —
/// <see cref="BindingBoundTableFunction"/> declares the binding's full <c>OutputSchema</c> at bind, so a projected
/// subset would mismatch it. <c>fabricator_delta_scan</c> carries the identical limitation for the identical
/// reason; lifting it needs a bound table that declares the projected schema.</para>
///
/// <para>Credential-free where the log lives on a local/host-FS path; OneLake needs the ambient credential.</para>
/// </summary>
public sealed class DeltaNativeScanFunction : ITableFunction
{
    public string Name => "fabricator_delta_native_scan";
    public Schema Parameters => new Schema(new[] { new Field("path", StringType.Default, nullable: false) }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var path = ((StringArray)args.Column(0)).GetString(0)
                   ?? throw new System.ArgumentException("fabricator_delta_native_scan: path must not be NULL");
        // Connection-free global reader → clear any stale Fabric credential (host-FS path for the log read).
        AmbientAdlsCredential.Current = null;
        var opener = AmbientOpener.Current;
        var schema = DeltaReader.GetSchema(opener, path);              // engineered-wood: the log → schema
        // ⚠ The active-file LIST is no longer resolved here. It used to be, and reading it at bind was part
        // of what made this serve deleted rows: a list of file URIs carries no deletion vector, so whatever
        // consumed it could only read whole files. DeltaNativeReader resolves the files AND their DVs
        // together at execute time, from one snapshot.
        return new NativeBinding(path, schema);
    }

    private sealed class NativeBinding : ITableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public NativeBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;

        // The filter is pushed for file / row-group skipping but the result is a superset, so it stays
        // unclaimed (see fabricator_delta_scan above for the full argument). The PROJECTION is claimed:
        // DeltaNativeReader names exactly the requested columns in its generated SQL, so DuckDB prunes them
        // inside the Parquet read.
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => true;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Connection-free global reader → clear any stale Fabric credential left on this (reused)
            // execution thread by a prior catalog op, exactly as Bind does: the FS factory must take the
            // host-FS (duckdb-azure) path, not the direct-SDK OneLake filesystem.
            AmbientAdlsCredential.Current = null;
            // ⚠ THIS USED TO BE `Host.Query("SELECT * FROM read_parquet([<active files>])")`, WHICH SERVED
            // DELETED ROWS. MEASURED 2026-08-13 on the DEFAULT table shape (deletion_vectors is on by
            // default): 10 rows, DELETE of 3 via a deletion vector, and this function returned all ten —
            // ids [1..10], sum 55, where the catalog and fabricator_delta_scan both returned 7 / 49. Silent,
            // no error. A DV records the deletion in the LOG and leaves the parquet file untouched, so any
            // reader that goes straight to the bytes reports rows the table no longer contains.
            //
            // It now routes through DeltaNativeReader — the same reader the ATTACH catalog uses under
            // native_read — which applies the deletion vector, reconstructs partition columns from the log
            // (rather than relying on DuckDB's hive auto-detection of the directory layout) and handles
            // column mapping and schema evolution. That is also the honest fix for the "plain tables only"
            // caveat this spike shipped with: the follow-up slices all went into that class.
            //
            // ⚠ THE PROJECTION IS NOT PUSHED, and that is a constraint of this seam rather than of the
            // reader. BindingBoundTableFunction wraps the stream with the binding's FULL OutputSchema, fixed at bind
            // before DuckDB knows what it wants, so emitting a projected subset mismatches the declared
            // schema (arrow_ingest reads past the end — SIGSEGV). Passing no Columns makes the reader
            // resolve the full schema, which is what OutputSchema promises. fabricator_delta_scan carries
            // the identical limitation for the identical reason; lifting it needs a bound table that
            // declares the PROJECTED schema.
            var spec = scan.Spec;
            // ⚠ Columns are re-resolved through ProjectionPlan rather than forwarded verbatim: it drops the
            // shapes that must read everything and, crucially, fixes the ORDER to the declared schema's —
            // DeltaNativeReader emits in the order it is handed, and BindingBoundTableFunction declares in that same
            // order. Forwarding spec.Columns as given would let the two disagree whenever DuckDB asks for
            // columns out of schema order.
            var columns = spec is null ? null : ProjectionPlan.Columns(_schema, spec.Columns);
            var pushed = spec is null && columns is null
                ? null
                : new ScanSpec
                {
                    Columns = columns is null ? null : new System.Collections.Generic.List<string>(columns),
                    Filter = spec?.Filter,
                    NativeFilter = spec?.NativeFilter,
                    At = spec?.At,
                };
            var stream = DeltaNativeReader.Read(
                AmbientOpener.Current, _path, _schema, pushed, FilterConstants.Read(scan.FilterValues),
                unit: null, value: null);
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

    public ITableFunctionBinding Bind(RecordBatch args)
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

    private sealed class WriteBinding : ITableFunctionBinding
    {
        private readonly string _path;
        private readonly Schema _schema;

        public WriteBinding(string path, Schema schema)
        {
            _path = path;
            _schema = schema;
        }

        public Schema OutputSchema => _schema;
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // Write synchronously while the opener (captured now) is valid, then yield the result row.
            AmbientAdlsCredential.Current = null; // connection-free global writer → host-FS path
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
    IReadOnlyDictionary<string, string>? CreateProperties = null,
    // ---- further parquet write tuning (2026-08-07) ----
    // ⚠ Only the first two are expressible on BOTH engines. The rest are DuckDB COPY options with no
    // engineered-wood equivalent, so DeltaWriter.Options REFUSES them on the codec engine rather than
    // accepting and dropping them — an option that silently does nothing is worse than one that errors.
    long? RowGroupSizeBytes = null,      // native ROW_GROUP_SIZE_BYTES  <-> EW RowGroupMaxBytes
    DeltaParquetVersion ParquetVersion = DeltaParquetVersion.Default, // native PARQUET_VERSION <-> EW DataPageVersion
    long? RowGroupsPerFile = null,       // native ROW_GROUPS_PER_FILE — native only
    long? DictionarySizeLimit = null,    // native DICTIONARY_SIZE_LIMIT — native only, see the note below
    long? FileSizeBytes = null,          // native FILE_SIZE_BYTES — native only
    // ⚠ These two are expressible on BOTH engines and the engines DISAGREE on the bloom default
    // (DuckDB 0.01, engineered-wood 0.05), so leaving one unset does NOT make the two write equivalent
    // files. Each engine keeps its own default when unset — normalising would silently change the codec
    // engine's behaviour for a user who never asked for anything.
    int? CompressionLevel = null,        // native COMPRESSION_LEVEL <-> EW CustomCompressionLevel
    double? BloomFilterFpp = null);      // native BLOOM_FILTER_FALSE_POSITIVE_RATIO <-> EW BloomFilterFpp

/// <summary>Parquet format version for the data files (DuckDB's <c>PARQUET_VERSION</c>; engineered-wood's
/// <c>DataPageVersion</c>). <see cref="Default"/> means "leave each engine on its own default", which is NOT
/// the same value on both — DuckDB writes V1 pages, engineered-wood writes V2 — so an unset option must not be
/// rendered rather than being normalised to one of them.</summary>
internal enum DeltaParquetVersion
{
    Default = 0,
    V1,
    V2,
}

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
        // NOTE: VariantTransportBlob is deliberately NOT set. The C++↔C# Arrow boundary does speak the
        // ew.variant_transport LEAF-binary transport (one self-delimiting metadata++value blob per row — the
        // canonical arrow.parquet.variant struct extension crashes DuckDB's ArrowAppender::FinalizeChild),
        // but that flattening is now OURS: engineered-wood's read pipeline emits its canonical
        // VariantArray via the UNPATCHED VariantColumnCoercion, and Fabricator.Bridge.VariantTransport
        // converts at each boundary (read exits, the native-read seam, the write entries). Keeping the
        // conversion on this side is what removes ~392 lines of patch from engineered-wood.
        ParquetWriteOptions = new ParquetWriteOptions
        {
            OmitPathInSchema = false, // REQUIRED field — standard readers (DuckDB/arrow-rs/Fabric) reject without it
            RowGroupMaxRows = spec?.RowGroupSize ?? 122880, // default = DuckDB's row-group size
            // ⚠ EW's own default is 128 MiB; DuckDB's ROW_GROUP_SIZE_BYTES default is row_group_size * 1024.
            // Leave EW on its default when unset rather than importing DuckDB's — an unset option must not
            // silently change the codec engine's behaviour.
            RowGroupMaxBytes = spec?.RowGroupSizeBytes ?? (128L * 1024 * 1024),
            DataPageVersion = spec?.ParquetVersion switch
            {
                DeltaParquetVersion.V1 => EngineeredWood.Parquet.DataPageVersion.V1,
                DeltaParquetVersion.V2 => EngineeredWood.Parquet.DataPageVersion.V2,
                _ => EngineeredWood.Parquet.DataPageVersion.V2, // EW's own default
            },
            Compression = spec?.Compression ?? EngineeredWood.Compression.CompressionCodec.Snappy,
            // CustomCompressionLevel, not CompressionLevel: DuckDB's COMPRESSION_LEVEL is a NATIVE codec
            // level (zstd 1-22, gzip 1-9) and so is this one, which the options record documents as
            // overriding the coarse BlockCompressionLevel enum. Mapping to the enum instead would silently
            // reinterpret the number as one of a handful of presets.
            CustomCompressionLevel = spec?.CompressionLevel,
            // Unset keeps EW's own 0.05 — see the spec's note on the engines disagreeing here.
            BloomFilterFpp = spec?.BloomFilterFpp ?? 0.05,
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

    /// <param name="serializable">
    /// The catalog's ATTACH isolation level. **No longer affects the created table's configuration** — see the
    /// "NO AUTOMATIC delta.isolationLevel STAMP" note below. Still threaded through the write entry points so
    /// removing it is a separate, purely mechanical change across ~6 signatures rather than a behaviour edit
    /// buried in this one; it is inert until then.
    /// </param>
    private static Dictionary<string, string>? CreateConfig(
        bool deletionVectors, bool rowTracking, bool inCommitTimestamps, bool changeDataFeed,
        bool serializable = false, IReadOnlyList<string>? sortedBy = null,
        IReadOnlyDictionary<string, string>? extraProperties = null)
    {
        _ = serializable; // inert: the isolation level is no longer stamped into the table config
        if (!deletionVectors && !rowTracking && !inCommitTimestamps && !changeDataFeed
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
        // NO AUTOMATIC delta.isolationLevel STAMP (removed 2026-08-01, together with the default flip).
        //
        // A CREATE used to bake the catalog's ATTACH isolation_level into the table as a property. That
        // conflates two different things — the ATTACH option is a per-catalog BEHAVIOUR knob, the property is
        // a DURABLE DECLARATION about the table — and the conflation has a sharp edge: because the table's
        // property WINS over any catalog (PendingSerializable), a stamp makes an ephemeral attach-time choice
        // permanent AND silently overrides a different catalog's explicit setting later. Measured directly:
        // with the stamp in place, attaching the same path twice at two levels stopped honoring the second
        // one, which is exactly the composition our own level-contrast suites rely on.
        //
        // ⚠ THE ORIGINAL JUSTIFICATION FOR NOT STAMPING IS NOW STALE, AND THE REASON STILL HOLDS. It read:
        // "not stamping costs nothing, because the DEFAULT now matches Fabric Spark (Serializable) — silence
        // already means cross-engine agreement". Since the catalog default went back to write_serializable
        // (2026-08-11) SILENCE NO LONGER MEANS AGREEMENT: a table created here and declaring nothing is read
        // as Serializable by Spark and as WriteSerializable by us, so a user relying on ROW-LEVEL CONCURRENCY
        // across engines does not get it from the catalog default alone.
        //
        // Stamping is still the wrong fix, for the reason above rather than for that one: the property
        // outranks EVERY catalog, so an ephemeral attach-time choice would become permanent and would
        // silently override a later, explicit setting on another attach. MEASURED when the stamp existed.
        //
        // The durable, per-table, visible-in-the-SQL route is unchanged:
        // CREATE TABLE ... WITH ("delta.isolationLevel"='WriteSerializable') or
        // delta.set_tblproperties. Spark HONORS such a value even though its own DDL refuses to
        // set it (measured 2026-07-31). docs/delta-transactions.md §10.6a.
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
    // and retry. Safe for create, for overwrite, and for an append THAT READ NOTHING — the data doesn't
    // depend on the conflicting commit. ⚠ NOT safe for an append that declared `isBlindAppend: false`: its
    // rows were computed FROM the table, so replaying them re-commits values derived from a snapshot that
    // has moved (the guard on the catch). Rowid DELETE/UPDATE do NOT retry either (their absolute positions
    // are tied to the scanned snapshot — a concurrent change invalidates them; DeltaReader surfaces a clear
    // conflict error instead), which is the same rule: retry only what does not depend on what it read.
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
                             bool serializable = false, IReadOnlyList<string>? sortedBy = null,
                             bool? isBlindAppend = null)
    {
        // Liveness probe markers (level 2 only): the write is what READS the retained batches, so a release
        // printed between these two lines would be the use-after-free the IPC copy used to guard against.
        LivenessMark($"write BEGIN reading {batches.Count} retained batch(es)");
        long version = WriteAsync(opener, path, schema, batches, mode, ct, deletionVectors, inCommitTimestamps,
                      changeDataFeed, rowTracking, spec, nativeWrite, columnMapping, serializable, sortedBy,
                      isBlindAppend)
            .GetAwaiter().GetResult();
        LivenessMark("write END");
        return version;
    }

    private static async Task<long> WriteAsync(nint opener, string path, Schema schema,
                             IReadOnlyList<RecordBatch> batches,
                             DeltaWriteMode mode, CancellationToken ct, bool deletionVectors,
                             bool inCommitTimestamps, bool changeDataFeed,
                             bool rowTracking, DeltaWriteSpec? spec, bool nativeWrite,
                             EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
                             bool serializable, IReadOnlyList<string>? sortedBy = null,
                             bool? isBlindAppend = null)
    {
        // native_write: DuckDB's parquet writer produces the data-file bytes (via COPY on a fresh host
        // connection); engineered-wood keeps the _delta_log commit. Falls back to EW's codec if host_query is
        // unavailable. Only the data write is affected — partitioning/stats/row-tracking/commit are unchanged.
        // The resolved tuning (compression/row-group size) rides into the writer's COPY options.
        var dataFileWriter = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path, spec)
            : null;
        // VARIANT: everything below this line is engineered-wood's, so hand it the CANONICAL form and keep the
        // transport entirely on our side of the seam. One funnel is safe here — unlike at bulk ingest, which
        // has two sinks with opposite needs — because the native_write path reaches DuckDB's COPY THROUGH EW
        // (via NativeParquetDataFileWriter, which flattens back to transport itself), so there is no second
        // consumer of these batches expecting the blob form. No-op unless a column is actually a variant.
        schema = VariantMarker.ToCanonicalSchema(schema);
        batches = VariantTransport.ToCanonical(batches);
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
                        // ⚠ isBlindAppend is ONLY meaningful on the plain-append branch — engineered-wood
                        // ignores it for the overwrite family, which declares FALSE from its own read of
                        // the active-file set. Passing our claim here is what keeps the CODEC engine's
                        // autocommit appends honest. ⚠ The MECHANISM changed on 2026-08-12: EW used to claim
                        // `true` on our behalf (#125's hardcode), and our #137 landed upstream replacing it
                        // with this parameter, defaulting to NULL — so silence is now merely silent, not a
                        // lie. Passing it still matters, for #143's half: a declared-false append stops
                        // being rebase-safe, so a collision surfaces instead of replaying stale rows.
                        : await table.WriteAsync(batches, mode, ct, repartitionTo: repartitionTo,
                                                 isBlindAppend: isBlindAppend)
                            .ConfigureAwait(false);
                Log.LogInformation("delta write {Path}: committed v{Version}", path, version);
                return version;
            }
            // ⚠ `isBlindAppend != false` IS A CORRECTNESS GUARD — see the twin in
            // DeltaCatalog.FlushDeferredFilesAsync for the full argument. Short form: retrying re-writes
            // the SAME in-memory batches, and those rows were computed by DuckDB before this call, so a
            // retry replays values derived from a snapshot that has since moved. engineered-wood #143
            // stopped rebasing a declared-false append for that reason; the conflict has to reach the
            // statement, which is the only thing that can recompute. Null keeps retrying (the caller said
            // nothing, which is not the same as "read something"), and so does the overwrite family, whose
            // claim EW derives from its own read of the active-file set.
            catch (EngineeredWood.DeltaLake.DeltaConflictException)
                when (attempt < MaxCommitAttempts && isBlindAppend != false)
            {
                // Concurrent writer took our version — reopen + retry.
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
            // VARIANT: only the SCHEMA is canonicalised here. The STREAM stays in transport form because it
            // feeds DuckDB's COPY (which is what maps ew.variant_transport to a real parquet VARIANT) — this
            // is exactly the split that a single funnel at ingest could not express.
            var delta = EngineeredWood.DeltaLake.Schema.SchemaConverter.FromArrowSchema(
                VariantMarker.ToCanonicalSchema(data.Schema));
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

    /// <summary>
    /// AUTOCOMMIT CTAS, REORDERED: write the data files FIRST into the not-yet-existing table folder, then
    /// create and commit. Returns the committed version, or <c>null</c> — with <paramref name="data"/>
    /// UNCONSUMED — when the reorder does not apply and the caller must take the ordinary create-then-write
    /// path (<see cref="TryWriteStreaming"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>What it buys.</b> A CREATE-plus-data lands as TWO versions either way (v0 = protocol+metaData,
    /// v1 = the data) — engineered-wood has no API that publishes both at once for a host holding its own data
    /// plane, so this does NOT reduce the version count. What it moves is the FAILURE: today the long,
    /// failure-prone part (DuckDB's COPY over the network, disk, permissions) runs AFTER v0 is committed, so a
    /// data-write failure leaves **an empty committed table behind a statement the user saw fail** — the
    /// inverse of every other write path, where a failure leaves nothing. Reordered, the COPY precedes any log
    /// write, and the residual window is between two ADJACENT log writes with no data movement in between.
    /// See docs/known-limitations.md 1.5 and docs/delta-transactions.md §7.1.</para>
    /// <para><b>⚠ THE PREDICTED ENGINE DIVERGENCE DOES NOT EXIST, AND IT POINTS THE OTHER WAY — MEASURED.</b>
    /// The plan for this change (docs/delta-transactions.md §7.1) warned that being native_write-only would make
    /// the engines diverge on failure semantics "where today they agree", because the codec provider has no
    /// DuckDB writer to stage files with. That assumed the codec path creates first; it does not. It
    /// MATERIALIZES the whole stream (<see cref="Materialize"/>) and only then calls <see cref="Write"/>, so a
    /// mid-stream source failure there fails before any create. Measured on a 2M-row CTAS failing at row 1.9M,
    /// 3 runs each: codec leaves NO FOLDER AT ALL, native-before leaves <c>_delta_log/…0.json</c> (an empty
    /// COMMITTED table), native-after leaves an unreferenced parquet and no log. So this makes native CONVERGE
    /// on the codec's behaviour rather than diverge from it. The residue that does differ: native leaves orphan
    /// BYTES (they were already on storage) where the codec leaves none — and on a STORAGE failure the order
    /// reverses again, since the codec's create precedes its file writes while ours now follows them.</para>
    /// <para><b>⚠ A failed COPY leaves orphan parquet in a folder with NO <c>_delta_log</c></b>, which is not a
    /// table to any reader (nothing is discoverable there) and is strictly better than the empty committed
    /// table it replaces: a retry writes fresh GUID-named files and references only its own. Deleting them by
    /// name is the natural follow-on and is deliberately NOT done here — it is a separate decision from
    /// reordering, and the shape that is NOT acceptable (a version-checked delete of v0) is argued out in
    /// docs/delta-transactions.md §7.1.</para>
    /// <para><b>⚠ Only for a table that does not exist yet.</b> The caller establishes that; this method then
    /// assigns the column mapping ITSELF (physical names are random GUIDs — see
    /// <see cref="TryStreamCreateFiles"/>) and hands the same schema to the create as
    /// <c>preAssignedSchema</c>, which is the case that parameter exists for. If another writer creates the
    /// table in the window between the caller's check and our create, <c>OpenOrCreateAsync</c> OPENS theirs and
    /// ignores our pre-assigned schema — so the layout is re-checked before committing and a mismatch THROWS
    /// rather than committing files whose columns would read all-NULL.</para>
    /// </remarks>
    public static long? TryCreateFilesFirst(
        nint opener, string path, IArrowArrayStream data, DeltaWriteMode mode,
        bool deletionVectors, bool inCommitTimestamps, bool changeDataFeed, bool rowTracking,
        DeltaWriteSpec? spec, out long rowsWritten,
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
        bool serializable, IReadOnlyList<string>? sortedBy)
    {
        rowsWritten = 0;
        if (!NativeParquetDataFileWriter.Available)
        {
            return null;
        }
        // VARIANT: the SCHEMA crosses into engineered-wood and must be canonical (a transport marker reaching
        // EW maps to Delta `binary`, durably and silently — the worst failure in the variant surface). The
        // STREAM stays transport all the way to DuckDB's COPY inside TryStreamCreateFiles.
        var ewSchema = VariantMarker.ToCanonicalSchema(data.Schema);
        var config = CreateConfig(deletionVectors, rowTracking, inCommitTimestamps, changeDataFeed,
                                  serializable, sortedBy, spec?.CreateProperties);
        // The table this create WOULD produce must be one engineered-wood lets an outside writer commit files
        // into. The open-table path answers that from `table.SupportsExternalDataFileCommit` AFTER opening;
        // here there is no table yet, so the same three conditions are evaluated on the exact inputs the create
        // is about to be given. Declining is always safe — it is today's path, with the stream untouched.
        if (NeedsOwnWriterOnCreate(ewSchema, config))
        {
            return null;
        }
        var files = TryStreamCreateFiles(opener, path, data, columnMapping, out rowsWritten,
                                         out var assigned, spec?.PartitionColumns, spec);
        if (files is null)
        {
            return null;   // streaming unavailable — `data` untouched for the caller's fallback
        }
        // ⚠ FROM HERE `data` IS CONSUMED AND THE BYTES ARE ON STORAGE: every remaining path must commit or
        // throw, never return null (a null would send the caller down a second write path over an exhausted
        // stream, silently producing an empty table).
        return CreateAndCommitFilesAsync(opener, path, ewSchema, config, files, assigned, mode, spec,
                                         columnMapping, sortedBy).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The create+commit half of <see cref="TryCreateFilesFirst"/>: two adjacent log writes over files that
    /// are already on storage.
    /// </summary>
    private static async Task<long> CreateAndCommitFilesAsync(
        nint opener, string path, Schema ewSchema, IReadOnlyDictionary<string, string>? config,
        List<WrittenDataFile> files, EngineeredWood.DeltaLake.Schema.StructType? assigned,
        DeltaWriteMode mode, DeltaWriteSpec? spec,
        EngineeredWood.DeltaLake.Schema.ColumnMappingMode columnMapping,
        IReadOnlyList<string>? sortedBy)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenOrCreateAsync(
            fs, ewSchema, Options(spec),
            partitionColumns: spec?.PartitionColumns,
            clusteringColumns: spec?.PartitionColumns is { Count: > 0 } ? null : sortedBy,
            columnMappingMode: columnMapping,
            configuration: config,
            cancellationToken: default,
            preAssignedSchema: assigned).ConfigureAwait(false);
        try
        {
            EnsurePreAssignedLayoutAdopted(table, assigned, path);
            // The SAME mode the ordinary path would have committed, so the commitInfo this statement records is
            // unchanged by the reorder. On a table this call just created an Overwrite removes nothing (there
            // are no active files), so the two readings agree.
            long version = await table.CommitDataFilesAsync(
                files, mode, dynamicPartitionOverwrite: false,
                cancellationToken: default).ConfigureAwait(false);
            Log.LogInformation(
                "delta create-then-commit {Path}: committed v{Version} files={Files} (files written BEFORE the log)",
                path, version, files.Count);
            return version;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the table a create with these inputs would produce needs engineered-wood's own writer, i.e.
    /// would report <c>SupportsExternalDataFileCommit == false</c>. Mirrors that property's three conditions
    /// (<c>IsIcebergCompat</c>, an identity column, <c>WriteTimeExpressions.Declares</c>) read from the
    /// configuration and schema the create is about to be handed instead of from a snapshot that does not
    /// exist yet. Erring toward TRUE only costs the reorder.
    /// </summary>
    private static bool NeedsOwnWriterOnCreate(Schema arrowSchema, IReadOnlyDictionary<string, string>? config)
    {
        if (EngineeredWood.DeltaLake.Schema.IcebergCompat.GetVersion(config)
            != EngineeredWood.DeltaLake.Schema.IcebergCompatVersion.None)
        {
            return true;
        }
        if (config is not null)
        {
            foreach (var key in config.Keys)
            {
                // DeltaConstraintEnforcer.Declares' own prefix test.
                if (key.StartsWith("delta.constraints.", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        // Invariants / generated columns ride FIELD metadata, which the Arrow schema carries verbatim into the
        // Delta one — the same top-level fields DeltaConstraintEnforcer/DeltaGeneratedColumns walk.
        foreach (var f in arrowSchema.FieldsList)
        {
            if (f.Metadata is { } md
                && (md.ContainsKey("delta.invariants") || md.ContainsKey("delta.generationExpression")))
            {
                return true;
            }
        }
        var delta = EngineeredWood.DeltaLake.Schema.SchemaConverter.FromArrowSchema(arrowSchema);
        foreach (var f in delta.Fields)
        {
            if (EngineeredWood.DeltaLake.Schema.IdentityColumn.GetConfig(f) is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Guards the one way the reorder can corrupt: our files were written under physical names WE assigned, so
    /// if <c>OpenOrCreateAsync</c> opened a table someone else created concurrently (which ignores
    /// <c>preAssignedSchema</c> by design, so a crashed CTAS can be retried) those names may name nothing in
    /// its schema and every column would read NULL. Refuse instead — the bytes stay as invisible orphans.
    /// </summary>
    private static void EnsurePreAssignedLayoutAdopted(
        DeltaTable table, EngineeredWood.DeltaLake.Schema.StructType? assigned, string path)
    {
        if (assigned is null)
        {
            return;   // no mapping was assigned ⇒ the files carry logical names, which any create reproduces
        }
        var live = table.CurrentSnapshot.Schema;
        bool same = live.Fields.Count == assigned.Fields.Count;
        for (int i = 0; same && i < assigned.Fields.Count; i++)
        {
            same = string.Equals(
                EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(
                    live.Fields[i], EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name),
                EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(
                    assigned.Fields[i], EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name),
                System.StringComparison.Ordinal);
        }
        if (!same)
        {
            throw new System.InvalidOperationException(
                $"delta: the table at '{path}' was created concurrently while this CREATE ... AS SELECT was "
                + "writing its data files, and its column-mapping physical names differ from the ones those "
                + "files were written under — refusing to commit them (they would read as all-NULL columns). "
                + "The written files are unreferenced; re-run the statement.");
        }
    }

    /// <summary>
    /// Rejects Arrow timestamp units that have no faithful Delta encoding. Delta timestamps are MICROSECOND,
    /// and parquet has only MILLIS/MICROS/NANOS — so a SECOND-unit column would be stored unchanged under a
    /// micros annotation and read back a million times too small, and a NANOSECOND column cannot be stored
    /// without discarding its sub-microsecond digits. Millisecond is fine (MILLIS exists).
    /// </summary>
    /// <remarks>
    /// engineered-wood refuses both at ITS write sites, but that guard CANNOT fire on our native_write path:
    /// there the data files are produced by DuckDB's COPY and EW only ever sees the finished files
    /// (CommitDataFilesAsync), never the Arrow batches. DuckDB's parquet writer DOES support NANOS, so
    /// without this check a native_write would emit a NANOS-annotated file inside a table whose Delta schema
    /// declares micros — readable by DuckDB, wrong for every other reader. Hence we check the schema on our
    /// side of the boundary.
    /// <para>
    /// This is reachable, not theoretical: the SQL Server provider maps datetime2(7)/time(7) to Arrow
    /// NANOSECOND (SqlArrowMapping), so `CREATE TABLE lake.s.t AS SELECT * FROM sqldb.dbo.&lt;datetime2(7)&gt;`
    /// lands here. Like EW we REFUSE rather than round: which rounding is correct is the caller's decision.
    /// </para>
    /// </remarks>
    internal static void EnsureTimestampUnitsWritable(Schema schema)
    {
        foreach (var f in schema.FieldsList)
        {
            var unit = FindUnsupportedTimestampUnit(f.DataType);
            if (unit is null)
            {
                continue;
            }
            var why = unit == Apache.Arrow.Types.TimeUnit.Second
                ? "parquet has no second-precision timestamp unit, so the values would be stored unchanged "
                  + "under a microsecond annotation and read back a million times too small"
                : "Delta timestamps are microsecond precision, so the sub-microsecond digits would be lost";
            throw new System.NotSupportedException(
                $"Column \"{f.Name}\" is an Arrow {unit} timestamp, which cannot be written to Delta: {why}. "
                + "CAST it to a microsecond TIMESTAMP first, choosing the rounding yourself — e.g. "
                + "CAST(ts AS TIMESTAMP). (SQL Server datetime2(7)/time(7) arrive as NANOSECOND.)");
        }
    }

    // Recurses into struct/list/map: a nested nanosecond timestamp is just as unwritable as a top-level one.
    private static Apache.Arrow.Types.TimeUnit? FindUnsupportedTimestampUnit(
        Apache.Arrow.Types.IArrowType type) => type switch
    {
        Apache.Arrow.Types.TimestampType ts =>
            ts.Unit is Apache.Arrow.Types.TimeUnit.Nanosecond or Apache.Arrow.Types.TimeUnit.Second
                ? ts.Unit
                : null,
        StructType st => st.Fields.Select(x => FindUnsupportedTimestampUnit(x.DataType))
                                  .FirstOrDefault(u => u is not null),
        ListType lt => FindUnsupportedTimestampUnit(lt.ValueDataType),
        MapType mt => FindUnsupportedTimestampUnit(mt.KeyField.DataType)
                      ?? FindUnsupportedTimestampUnit(mt.ValueField.DataType),
        _ => null,
    };

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
        // The native path bypasses EW's own timestamp-unit guard entirely (DuckDB writes the files), so check
        // here — before anything is written — rather than committing a spec-invalid file. See the helper.
        EnsureTimestampUnitsWritable(data.Schema);
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
        // VARIANT: this path splits the two dialects, so the schema and the stream go separate ways. Every
        // EW-facing SCHEMA below is `ewSchema` (canonical VariantType — what its SchemaConverter understands
        // and what makes the metaData record `variant`); the STREAM stays in transport form all the way to
        // DuckDB's COPY, which is what turns ew.variant_transport into a real parquet VARIANT. `data` is
        // rewrapped below but only by pass-throughs that preserve the fields, so computing this once is safe;
        // the PHYSICAL-renamed copies (copySource/statsSchema) are COPY's business and stay transport.
        var ewSchema = VariantMarker.ToCanonicalSchema(data.Schema);
        // Pass columnMapping so a NEW table is created WITH the mode (an existing table keeps its own mode). Only
        // id mode reaches here as a mapping table (name mode stays on the codec path, gated by the caller) — an
        // id-mode table's data files carry field_ids, which the external commit + native reader both handle.
        var table = await DeltaTable.OpenOrCreateAsync(
            fs, ewSchema, Options(spec),
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
                await table.SetSchemaAsync(ewSchema, default).ConfigureAwait(false);
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
                //
                // ...and so is any table column the STREAM does not carry. The COPY writes SELECT * of the
                // stream, so FIELD_IDS must describe the STREAM, not the table: a partial-column
                // `INSERT INTO t (a) VALUES (…)`, or an INSERT whose column list omits a column a buffered
                // ALTER added, supplies fewer columns than the schema. Naming an absent one made the COPY fail
                // to BIND ("Column name \"col-…\" specified in FIELD_IDS not found"), which also MASKED the real
                // diagnostic — binding fails before the first batch is pulled, so the lazy NOT NULL validator
                // above never ran and a partial INSERT omitting a NOT NULL column reported this instead of the
                // constraint violation. Note statsSchema below already derives from data.Schema; this is the
                // same rule applied to the other consumer.
                var fieldIdsExclude = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var pc in partCols)
                {
                    fieldIdsExclude.Add(pc);
                }
                var streamCols = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var f in data.Schema.FieldsList)
                {
                    streamCols.Add(f.Name);
                }
                foreach (var f in snapSchema.Fields)
                {
                    if (!streamCols.Contains(f.Name))
                    {
                        fieldIdsExclude.Add(f.Name);
                    }
                }
                fieldIdsSpec = BuildFieldIdsSpec(snapSchema, fieldIdsExclude.Count > 0 ? fieldIdsExclude : null);
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
                    await table.SetSchemaAsync(ewSchema, default).ConfigureAwait(false);
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
        // VARIANT: the schema below crosses into engineered-wood, whose SchemaConverter recognises the
        // canonical VariantType and knows nothing of our transport marker. Getting this wrong is the WORST
        // failure in the whole variant surface: a marker that reaches EW unconverted maps to Delta `binary`,
        // and a metaData commit is not revisable — the table would record the wrong type durably and silently.
        schema = VariantMarker.ToCanonicalSchema(schema);
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

    /// <summary>Collects a (possibly streamed) Arrow stream into in-memory batches that stay valid after the
    /// stream is released — engineered-wood's <c>WriteAsync</c> needs them all retained for one commit.
    /// Returns the schema, batches, and total row count.</summary>
    /// <remarks>
    /// <para><b>⚠ THE COPY IS OPT-IN AND OFF BY DEFAULT SINCE 2026-08-07.</b> The batches are now RETAINED
    /// as they arrive; set <c>FABRICATOR_MATERIALIZE_COPY=1</c> to restore the old Arrow IPC round trip.</para>
    ///
    /// <para><b>Why the copy was there, and why it is not needed.</b> It was documented as necessary because
    /// "the source batches may be freed after consumption". That is not true of any stream that reaches
    /// here: the Arrow C data interface makes a consumed <c>ArrowArray</c> the CONSUMER's property, and our
    /// own producer implements exactly that — <c>ArrowProducer::GetNext</c> moves the batch out of its queue
    /// ("ownership transfers to the consumer") and <c>Release</c> frees only what is STILL QUEUED. So the
    /// import owns its buffers and they live until it is disposed.</para>
    ///
    /// <para><b>⚠ It was NOT settled by reading that code, deliberately.</b> A use-after-free here is SILENT
    /// on Windows and Linux — which is how the <c>ArrowProducer::Release</c> mutex bug hid until macOS CI ran
    /// it — so green suites prove nothing. It was settled by an out-of-band liveness registry
    /// (<c>ArrowLiveness</c>, <c>FABRICATOR_ARROW_LIVENESS=1</c>) that interposes every handed-out batch's
    /// release callback and ATTRIBUTES the free: producer-side (the bug) versus consumer-side (correct).
    /// Measured across the collect-path suites: every handed-out batch released exactly once, zero
    /// producer-side releases, zero double releases.</para>
    ///
    /// <para>Cost removed: the IPC form was TWO copies plus serialization, with the serialized
    /// <c>MemoryStream</c> and the decoded batches alive simultaneously — on the buffered-INSERT path, where
    /// memory already grows with rows.</para>
    ///
    /// <para><b>⚠ Empty batches are still SKIPPED and that is load-bearing</b> — engineered-wood writes one
    /// parquet file per input batch, so passing a zero-row batch through would commit an empty data file.</para>
    /// </remarks>
    public static (Schema Schema, List<RecordBatch> Batches, long Rows) Materialize(
        IArrowArrayStream stream, CancellationToken ct)
        => MaterializeAsync(stream, ct).GetAwaiter().GetResult();

    /// <summary>Set <c>FABRICATOR_MATERIALIZE_COPY=1</c> to restore the pre-2026-08-07 Arrow IPC round trip.
    /// Kept as an escape hatch rather than deleted: the removal rests on a liveness measurement taken on ONE
    /// platform, and a macOS/other-producer surprise should be one environment variable away from being
    /// isolated, not a rebuild.</summary>
    private static readonly bool MaterializeCopy =
        Environment.GetEnvironmentVariable("FABRICATOR_MATERIALIZE_COPY") == "1";

    /// <summary>Level 2 of the liveness probe (<c>FABRICATOR_ARROW_LIVENESS=2</c>): the consumer prints its
    /// own markers to the SAME stderr the C++ registry prints handouts/releases to, so the INTERLEAVING
    /// answers the only question that matters — is a batch released before or after the write reads it?
    /// Counting alone cannot tell those apart.</summary>
    internal static readonly bool LivenessVerbose =
        Environment.GetEnvironmentVariable("FABRICATOR_ARROW_LIVENESS") == "2";

    internal static void LivenessMark(string what)
    {
        if (LivenessVerbose)
        {
            Console.Error.WriteLine("FABRICATOR-LIVENESS: consumer " + what);
            Console.Error.Flush();
        }
    }

    private static async Task<(Schema Schema, List<RecordBatch> Batches, long Rows)> MaterializeAsync(
        IArrowArrayStream stream, CancellationToken ct)
    {
        var schema = stream.Schema;
        if (!MaterializeCopy)
        {
            var retained = new List<RecordBatch>();
            long retainedRows = 0;
            RecordBatch? batch;
            while ((batch = await stream.ReadNextRecordBatchAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (batch.Length == 0)
                {
                    batch.Dispose(); // see the remark: an empty batch would become an empty parquet file
                    continue;
                }
                retained.Add(batch);
                retainedRows += batch.Length;
            }
            LivenessMark($"materialize retained {retained.Count} batch(es), {retainedRows} row(s)");
            return (schema, retained, retainedRows);
        }

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

    /// <summary>
    /// The call signature: the input table (any columns — the operator accepts anything, so none are declared)
    /// followed by the destination path as a NAMED parameter, which is how it is written:
    /// <c>fabricator_delta_write(&lt;input&gt;, path := '…')</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>path</c> MUST carry <see cref="Params.Named"/>. Before the unified protocol, a collector's
    /// <c>Parameters</c> meant "named cost args" by convention and the host tagged them all as named; now an
    /// unflagged field means POSITIONAL, so leaving it bare silently turns <c>path := '…'</c> into a binder
    /// error. `verify_delta_write` line 44 is what catches it.
    /// </remarks>
    public Schema Parameters { get; } = new Schema(new[]
    {
        Params.TableInput("input"),
        Params.Named("path", StringType.Default),
    }, metadata: null);

    public ICollectorBinding Bind(RecordBatch? args, Schema inputSchema)
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

    private sealed class WriteCollectorBinding : ICollectorBinding
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
            AmbientAdlsCredential.Current = null; // connection-free global collector → host-FS path
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
