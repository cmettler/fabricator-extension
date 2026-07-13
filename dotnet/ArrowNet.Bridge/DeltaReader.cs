using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake;
using EngineeredWood.DeltaLake.Actions;
using EngineeredWood.DeltaLake.DeletionVectors;
using EngineeredWood.DeltaLake.Table;
using EngineeredWood.IO;
using EngineeredWood.Expressions;
using EngineeredWood.Parquet;
using Microsoft.Extensions.Logging;

namespace ArrowNet.Bridge;

/// <summary>
/// Reads a Delta Lake table (Curt Hagenlocher's engineered-wood, pure C#) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via <see cref="DuckDbTableFileSystem"/> — so Delta tables on local/az://-s3://-https://
/// paths read with DuckDB's secrets + backends. Surfaced to DuckDB as the <c>arrownet_delta_scan(path)</c>
/// connection-free GLOBAL host-FS table function (see <see cref="DeltaGlobalTableFunction"/>); the opener is
/// the calling operator's ClientContext, threaded via <see cref="AmbientOpener"/>.
/// </summary>
internal static class DeltaReader
{
    private static readonly ILogger DmlLog = ArrowNetLog.CreateLogger("ArrowNet.Delta.Write");

    /// <summary>The EXACT active data-file URIs of the current snapshot (the `add` set, NOT a glob — a glob would
    /// include tombstoned files). Relative `add.path`s are resolved against the table root; an abfss-OneLake root
    /// is rewritten to the <c>onelake://</c> scheme so DuckDB's native reader routes them to our FileSystem
    /// subsystem (+ ExternalFileCache). Used by the native-read path (docs/multifile-delta.md Phase A pre-spike):
    /// engineered-wood lists the files, DuckDB's native parquet reader reads them via read_parquet.</summary>
    public static IReadOnlyList<string> GetActiveFileUris(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var root = ToReadableRoot(path);
            var uris = new List<string>();
            foreach (var add in table.CurrentSnapshot.ActiveFiles.Values)
            {
                uris.Add(root + "/" + EngineeredWood.DeltaLake.DeltaPath.Decode(add.Path).Replace('\\', '/').TrimStart('/'));
            }
            return uris;
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>One active data file for the native reader: its global path-sorted <paramref name="Ordinal"/>
    /// (matching engineered-wood's <c>OrderedActiveFiles</c> so a `(Ordinal&lt;&lt;40)|file_row_number` rowid
    /// round-trips to <c>DeleteByRowIdsAsync</c>), the readable <paramref name="Uri"/> (onelake:// for OneLake),
    /// and the sorted deleted row positions <paramref name="Dv"/> (empty = no DV).</summary>
    /// <summary><paramref name="BaseRowId"/>/<paramref name="CommitVersion"/> = the add action's row-tracking
    /// fields (null when the table doesn't track rows, or for a transaction's PENDING files — ids are assigned
    /// at commit): they drive the <c>__delta_row_id</c>/<c>__delta_row_commit_version</c> virtual columns
    /// (stable id = baseRowId + position unless a materialized column overrides).</summary>
    public sealed record NativeScanFile(int Ordinal, string Uri, long[] Dv,
                                        IReadOnlyDictionary<string, string>? PartitionValues = null,
                                        long? BaseRowId = null, long? CommitVersion = null);

    /// <summary>The result of <see cref="ListNativeScanFiles"/>: the resolved snapshot <see cref="Version"/>, the
    /// surviving (post-prune) <see cref="Files"/> in path-sorted global-ordinal order, and <see cref="AnyUri"/> =
    /// any active file's URI (pre-prune) for a schema probe when everything was pruned.</summary>
    public sealed class NativeScanList
    {
        /// <summary>The table's partition columns (LOGICAL names — metaData.partitionColumns). The
        /// per-file SQL emits each file's partitionValues as typed literals for them: a partition
        /// column is ABSENT from the data files, and the log is the authoritative source (paths are
        /// opaque — never parsed).</summary>
        public IReadOnlyList<string> PartitionColumns { get; init; } = System.Array.Empty<string>();

        public long Version { get; init; }
        public IReadOnlyList<NativeScanFile> Files { get; init; } = System.Array.Empty<NativeScanFile>();
        public string? AnyUri { get; init; }

        /// <summary>For a <b>name-mode</b> column-mapping table: logical column name → the PHYSICAL name the
        /// column is stored under in the parquet files. Null when column mapping is off (the common case) OR in id
        /// mode (see <see cref="LogicalToFieldId"/>). The native reader aliases <c>"physical" AS "logical"</c> so the
        /// scan output uses logical names — mirroring how engineered-wood's own reader maps by physical name.</summary>
        public IReadOnlyDictionary<string, string>? LogicalToPhysical { get; init; }

        /// <summary>For an <b>id-mode</b> column-mapping table: logical column name → its Delta
        /// <c>delta.columnMapping.id</c> (== the parquet <c>field_id</c>). Null unless id mode. The native reader
        /// resolves each file's parquet <c>field_id → physical parquet name</c> (via <c>parquet_schema</c>) and
        /// composes logical → field_id → physical name, then aliases as for name mode — so it reads BOTH an
        /// engineered-wood id-mode table (logical names in the files) AND an external Spark/Databricks id-mode table
        /// (col-&lt;guid&gt; physical names), and survives a column RENAME (field_id is stable).</summary>
        public IReadOnlyDictionary<string, int>? LogicalToFieldId { get; init; }

        /// <summary>The snapshot's mapped Delta schema, set ONLY for a column-mapping table with NESTED
        /// (struct-carrying) fields: nested children arrive from <c>read_parquet</c> under their PHYSICAL names
        /// (a flat SELECT alias can't rename struct children), so the native reader applies
        /// <see cref="ArrowColumnMappingRename"/> to each batch (a zero-copy recursive type-tree rename back to
        /// the logical names). Null for flat tables (fully handled by the top-level alias).</summary>
        public EngineeredWood.DeltaLake.Schema.StructType? MappedSchema { get; init; }

        /// <summary>The snapshot's FULL Delta schema (always set — mapping or not). Drives the native
        /// reader's per-file presence handling: stored names, field ids and typed NULL backfill for
        /// columns/members a file predates (schema evolution).</summary>
        public EngineeredWood.DeltaLake.Schema.StructType? TableSchema { get; init; }
    }

    /// <summary>Resolves the version whose commit is at/just-before <paramref name="instantUtc"/> (via the
    /// always-written <c>commitInfo.timestamp</c>); falls back to the latest version if the timestamp can't be
    /// resolved (e.g. an external table with no commit timestamps). Used for per-transaction snapshot pinning.</summary>
    public static long ResolveVersionAsOf(nint opener, string path, DateTime instantUtc, ILogger log)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            try
            {
                var snap = table.GetSnapshotAtTimestampAsync(new DateTimeOffset(instantUtc, TimeSpan.Zero), default)
                    .GetAwaiter().GetResult();
                log.LogDebug("delta snapshot pin: {Path} as-of {Instant:o} -> v{Version}", path, instantUtc, snap.Version);
                return snap.Version;
            }
            catch (Exception ex)
            {
                long latest = table.CurrentSnapshot.Version;
                log.LogDebug("delta snapshot pin: {Path} as-of {Instant:o} unresolved ({Err}); pinning latest v{Version}",
                    path, instantUtc, ex.Message, latest);
                return latest;
            }
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Lists the active data files for a native read: resolves the snapshot (latest / at
    /// <paramref name="unit"/>+<paramref name="value"/>), assigns each file its GLOBAL path-sorted ordinal
    /// (rowid parity), applies best-effort Delta-log FILE pruning via <paramref name="prune"/> (skip a file whose
    /// stats/partitions can't match), and resolves each surviving file's deletion-vector positions.</summary>
    public static NativeScanList ListNativeScanFiles(
        nint opener, string path, string? unit, string? value, Predicate? prune, ILogger log,
        EngineeredWood.DeltaLake.Schema.StructType? schemaOverride = null)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var snap = unit is null
                ? table.CurrentSnapshot
                : ResolveSnapshotAsync(table, unit, value ?? "", default).AsTask().GetAwaiter().GetResult();
            // schemaOverride: a buffered transaction's PENDING (ALTERed) schema — presence handling, mapping
            // maps and pruning key off it so a pending-added column reads as typed NULL from every committed
            // file (the same machinery as committed schema evolution; no stats => pruning stays superset-safe).
            var schemaForMaps = schemaOverride ?? snap.Schema;
            var root = ToReadableRoot(path);
            // GLOBAL path-sorted ordinal over ALL active files (matches engineered-wood OrderedActiveFiles), then prune.
            var ordered = new List<AddFile>(snap.ActiveFiles.Values);
            ordered.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var pruner = prune is null ? null : new DeltaFilePruner(schemaForMaps, snap.Metadata.PartitionColumns);
            var dvReader = new DeletionVectorReader(fs);
            var files = new List<NativeScanFile>();
            string? anyUri = null;
            int pruned = 0;
            for (int ordinal = 0; ordinal < ordered.Count; ordinal++)
            {
                var add = ordered[ordinal];
                var uri = root + "/" + EngineeredWood.DeltaLake.DeltaPath.Decode(add.Path).Replace('\\', '/').TrimStart('/');
                anyUri ??= uri;
                if (pruner is not null && !pruner.ShouldInclude(add, prune!))
                {
                    pruned++;
                    continue;
                }
                long[] dv = System.Array.Empty<long>();
                if (add.DeletionVector is not null)
                {
                    var deleted = dvReader.ReadAsync(add.DeletionVector).GetAwaiter().GetResult();
                    dv = deleted.ToArray();
                    System.Array.Sort(dv);
                }
                files.Add(new NativeScanFile(ordinal, uri, dv,
                    add.PartitionValues is { Count: > 0 } ? add.PartitionValues : null,
                    add.BaseRowId, add.DefaultRowCommitVersion));
            }
            // Column-mapping tables store columns decoupled from the logical name — capture the mapping (from THIS
            // snapshot's schema, so time travel to a pre-rename version maps correctly) so the native reader can
            // alias physical→logical. Two mechanisms, one per mode:
            //   • NAME mode → logical → PHYSICAL name (read `delta.columnMapping.physicalName` directly; Spark writes
            //     the parquet columns under it, and the physical name is STABLE across renames → probe-free).
            //   • ID mode → logical → field_id (`delta.columnMapping.id`); the reader resolves each file's parquet
            //     field_id → physical name (via parquet_schema) and composes logical → physical. Field-id (not
            //     physicalName) because engineered-wood's id-mode writer keeps LOGICAL names in the files (matching
            //     by field-id) while declaring a col-<guid> physicalName — so a physicalName alias would fail there;
            //     field-id reads BOTH the EW-created and the external-Spark (col-<guid>) layout, and survives rename.
            // (Top-level columns only; a nested mapped column needs recursive field-id remapping — a later slice.)
            var mode = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(snap.Metadata.Configuration);
            IReadOnlyDictionary<string, string>? logicalToPhysical = null;
            IReadOnlyDictionary<string, int>? logicalToFieldId = null;
            if (mode == EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Name)
            {
                var map = new Dictionary<string, string>();
                foreach (var field in schemaForMaps.Fields)
                {
                    if (field.Metadata is { } md
                        && md.TryGetValue(EngineeredWood.DeltaLake.Schema.ColumnMapping.PhysicalNameKey, out var phys)
                        && !string.IsNullOrEmpty(phys) && phys != field.Name)
                    {
                        map[field.Name] = phys;
                    }
                }
                logicalToPhysical = map.Count > 0 ? map : null;
            }
            else if (mode == EngineeredWood.DeltaLake.Schema.ColumnMappingMode.Id)
            {
                var map = EngineeredWood.DeltaLake.Schema.ColumnMapping.BuildLogicalToFieldIdMap(schemaForMaps);
                logicalToFieldId = map.Count > 0 ? map : null;
            }
            // Nested mapped fields (struct children under physical names in the files) need the recursive
            // batch rename — carry the mapped schema so the reader can apply it.
            var mappedSchema = mode != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None
                               && ArrowColumnMappingRename.HasNestedFields(schemaForMaps)
                ? schemaForMaps : null;
            log.LogDebug("delta native list: {Path} v{Version} active={Active} scanned={Scanned} pruned={Pruned} colmap={Map}",
                path, snap.Version, ordered.Count, files.Count, pruned, mode);
            return new NativeScanList
            {
                Version = snap.Version,
                PartitionColumns = snap.Metadata.PartitionColumns, Files = files, AnyUri = anyUri,
                LogicalToPhysical = logicalToPhysical, LogicalToFieldId = logicalToFieldId,
                MappedSchema = mappedSchema, TableSchema = schemaForMaps,
            };
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>The active data files of the Delta table as a JSON array of objects <c>[{"path":"&lt;uri&gt;"}]</c>
    /// for the C++ MultiFileReader (docs/multifile-delta.md Phase A). Slice 1a: paths only (the exact `add` set,
    /// NOT a glob). Partition values + deletion vectors (later slices) become extra keys on each object; the
    /// <paramref name="pushJson"/> pushed-filter arg is accepted now and applied for file pruning in a later
    /// slice. Paths are absolute URIs (onelake:// for OneLake → native + cached).</summary>
    public static string ListScanFilesJson(nint opener, string path, string? pushJson)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var root = ToReadableRoot(path);
            var dvReader = new EngineeredWood.DeltaLake.DeletionVectors.DeletionVectorReader(fs);
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var add in table.CurrentSnapshot.ActiveFiles.Values)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                var uri = root + "/" + EngineeredWood.DeltaLake.DeltaPath.Decode(add.Path).Replace('\\', '/').TrimStart('/');
                sb.Append("{\"path\":\"").Append(uri.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                // Slice 1b — deletion vectors: resolve the file's DV to the deleted ROW POSITIONS (sorted), so the
                // C++ MultiFileReader attaches a DeleteFilter and DuckDB's native read excludes them. Positions are
                // relative to the file (0-based physical order), matching read_parquet's row order.
                if (add.DeletionVector is not null)
                {
                    var deleted = dvReader.ReadAsync(add.DeletionVector).GetAwaiter().GetResult();
                    if (deleted.Count > 0)
                    {
                        var sorted = deleted.ToArray();
                        System.Array.Sort(sorted);
                        sb.Append(",\"dv\":[");
                        for (int i = 0; i < sorted.Length; i++)
                        {
                            if (i > 0)
                            {
                                sb.Append(',');
                            }
                            sb.Append(sorted[i]);
                        }
                        sb.Append(']');
                    }
                }
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // The table root as a URI DuckDB's native reader can open: an abfss-OneLake root → onelake:// (our VFS
    // subsystem, cached); everything else (local / s3 / already onelake://) passes through unchanged.
    internal static string ToReadableRoot(string path)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        if (p.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase)
            && p.Contains("onelake", StringComparison.OrdinalIgnoreCase))
        {
            var (_, fsName, under) = OneLakeDataLakeFileSystem.ParseAbfss(p);
            return "onelake://" + fsName + "/" + under;
        }
        return p;
    }

    /// <summary>Opens the Delta table at <paramref name="path"/> and returns its Arrow schema only (no data
    /// read). Used at table-function bind. <paramref name="opener"/> = the calling operator's ClientContext.</summary>
    public static Schema GetSchema(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            return table.ArrowSchema;
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>Like <see cref="GetSchema"/> but also reports whether <c>delta.enableRowTracking</c> is set —
    /// in the SAME table open, so the catalog's column fetch can cache the flag for the (immediately
    /// following) virtual-columns metadata fetch without a second <c>_delta_log</c> read (OneLake cost).</summary>
    public static Schema GetSchemaAndRowTracking(nint opener, string path, out bool rowTracking)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            rowTracking = cfg is not null
                && cfg.TryGetValue("delta.enableRowTracking", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
            return table.ArrowSchema;
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>
    /// Streams the Delta table at <paramref name="path"/> lazily (one <see cref="RecordBatch"/> at a time, no
    /// materialization). <paramref name="columns"/> (null =&gt; all) is the projection — engineered-wood reads
    /// only those columns. <paramref name="filter"/> (null =&gt; none) is pushed for <b>file + row-group
    /// skipping</b>: it drives both the Delta file pruner (<c>ReadAllAsync(columns, filter)</c>) and the
    /// per-file Parquet row-group/stats pruner (via <c>ParquetReadOptions.Filter</c>). engineered-wood does not
    /// re-apply the predicate per row, so the result is a superset — DuckDB re-applies above the scan.
    /// <paramref name="opener"/> (the operator's ClientContext) must stay valid for the whole scan; it does —
    /// the ClientContext lives for the whole table-function execution.
    /// </summary>
    public static IAsyncEnumerable<RecordBatch> Stream(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var parquet = filter is null
            ? ParquetReadOptions.Default
            // Bloom probing refines row-group pruning when min/max stats are inconclusive (point lookups
            // on high-cardinality columns) — natively-written files carry blooms on dict-encoded columns.
            : new ParquetReadOptions { Filter = filter, FilterUseBloomFilters = true };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamImpl(fs, options, columns, filter, ct);
    }

    // Returns the snapshot's mapped Delta schema when the table has column mapping AND nested (struct-carrying)
    // fields — the engineered-wood reader renames only TOP-LEVEL columns back to logical (RenameColumns /
    // RenameByFieldId), so nested struct children still carry their physical names and need the recursive
    // batch rename. Null (no transform) for flat or unmapped tables.
    private static EngineeredWood.DeltaLake.Schema.StructType? NestedMappedSchema(
        EngineeredWood.DeltaLake.Snapshot.Snapshot snap)
        => EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(snap.Metadata.Configuration)
               != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None
           && ArrowColumnMappingRename.HasNestedFields(snap.Schema)
            ? snap.Schema : null;

    private static async IAsyncEnumerable<RecordBatch> StreamImpl(
        ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            var nested = NestedMappedSchema(table.CurrentSnapshot);
            await foreach (var batch in table.ReadAllAsync(columns, filter, ct).ConfigureAwait(false))
            {
                yield return nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Like <see cref="Stream"/> but each batch carries a trailing non-null Int64
    /// <c>_metadata.row_id</c> column (the stable row-tracking id) — used to surface the DuckDB rowid for
    /// UPDATE/DELETE. Requires the table to have row tracking enabled (see <see cref="IsRowTrackingEnabled"/>).</summary>
    public static IAsyncEnumerable<RecordBatch> StreamWithRowIds(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var parquet = filter is null
            ? ParquetReadOptions.Default
            // Bloom probing refines row-group pruning when min/max stats are inconclusive (point lookups
            // on high-cardinality columns) — natively-written files carry blooms on dict-encoded columns.
            : new ParquetReadOptions { Filter = filter, FilterUseBloomFilters = true };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamWithRowIdsImpl(fs, options, columns, filter, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamWithRowIdsImpl(
        ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            var nested = NestedMappedSchema(table.CurrentSnapshot);
            await foreach (var batch in table.ReadAllWithRowIdsAsync(columns, filter, ct).ConfigureAwait(false))
            {
                yield return nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the rows whose transient <c>_metadata.row_id</c> is in <paramref name="rowIds"/>
    /// (deletion vectors). Returns the number of rows deleted.</summary>
    public static long DeleteByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                      CancellationToken ct, bool nativeWrite = false, bool nativeRead = false)
    {
        var fs = TableFileSystems.Create(opener, path);
        // Open with the standard WRITE options (OmitPathInSchema=false) so the copy-on-write rewrite emits
        // standard-readable parquet — DeltaTableOptions.Default would drop path_in_schema (TProtocolException).
        // native_write => DuckDB's parquet writer produces the rewritten survivor file (bloom/stats/footer) AND
        // DuckDB's read_parquet reads the source + drops the deleted positions (the rewriter, retiring the
        // engineered-wood reader for the clean shape). native_read => the FALLBACK read half (shapes the rewriter
        // is gated off for) also decodes through read_parquet (the IDataFileReader seam — variant-preserving).
        // engineered-wood still selects the affected files, computes stats, and commits remove(old)+add(new).
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path)
            : null;
        var rewriter = nativeWrite && NativeParquetDataFileRewriter.Available
            ? new NativeParquetDataFileRewriter(path, GetSchema(opener, path))
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(dataFileWriter: writer, dataFileRewriter: rewriter,
                                                                 dataFileReader: fileReader), ct)
            .AsTask().GetAwaiter().GetResult();
        try
        {
            long deleted = table.DeleteByRowIdsAsync(rowIds, ct).AsTask().GetAwaiter().GetResult().RowsDeleted;
            DmlLog.LogInformation("delta delete-rewrite {Path}: deleted={Deleted} writer={Writer} rewriter={Rewriter}",
                path, deleted, writer is null ? "engineered-wood" : "native-duckdb",
                rewriter is null ? "engineered-wood" : "native-duckdb");
            return deleted;
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DELETE");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // A rowid DELETE/UPDATE cannot be safely retried on a commit conflict: its absolute positions were computed
    // against the scanned snapshot, which a concurrent writer has changed. Surface a clear, retryable-by-the-user
    // error instead (re-running re-scans and recomputes the rowids).
    private static System.InvalidOperationException ConcurrentModification(string op) =>
        new($"delta: concurrent modification during {op} — another writer committed; the row positions are no "
            + "longer valid. Retry the statement.");

    /// <summary>True if the Delta table at <paramref name="path"/> has <c>delta.enableDeletionVectors=true</c>
    /// — DELETE then uses deletion vectors (no file rewrite) instead of copy-on-write.</summary>
    public static bool IsDeletionVectorsEnabled(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            return cfg is not null
                && cfg.TryGetValue("delta.enableDeletionVectors", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>The table properties buffered (explicit-transaction) DML needs, probed in ONE table open,
    /// plus the current version (the pin fallback when no scan pinned the transaction yet).
    /// <paramref name="MaterializeRowIds"/> = the table declares
    /// <c>delta.rowTracking.materializedRowIdColumnName</c> (implied by row tracking on our created tables) —
    /// UPDATE post-images then bake each row's ORIGINAL stable id into that column.</summary>
    public readonly record struct TxnDmlProfile(
        bool DvEnabled, bool CdfEnabled, bool SupportsExternalCommit, long Version, bool Partitioned,
        bool MaterializeRowIds);

    /// <summary>The active files' baseRowIds in transient-rowid ordinal order (see
    /// <see cref="DeltaTable.OrderedActiveBaseRowIds"/>) — resolves a matched row's ORIGINAL stable id
    /// for the buffered UPDATE's materialized post-images.</summary>
    public static IReadOnlyList<long?> GetOrderedActiveBaseRowIds(nint opener, string path,
                                                                  long? atVersion = null)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            return table.OrderedActiveBaseRowIdsAsync(atVersion).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            table.Dispose();
        }
    }

    public static TxnDmlProfile GetTxnDmlProfile(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            bool dv = cfg is not null
                && cfg.TryGetValue("delta.enableDeletionVectors", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
            bool cdf = cfg is not null
                && cfg.TryGetValue("delta.enableChangeDataFeed", out var c)
                && string.Equals(c, "true", System.StringComparison.OrdinalIgnoreCase);
            bool matIds = cfg is not null
                && cfg.TryGetValue("delta.rowTracking.materializedRowIdColumnName", out var m)
                && !string.IsNullOrEmpty(m);
            return new TxnDmlProfile(dv, cdf, table.SupportsExternalDataFileCommit,
                                     table.CurrentSnapshot.Version,
                                     table.CurrentSnapshot.Metadata.PartitionColumns.Count > 0,
                                     matIds);
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>Runs a compute-only schema change (the <c>Compute*</c> family — ADD/RENAME/DROP COLUMN,
    /// nested ADD/DROP FIELD) against the table WITHOUT committing: the buffered transaction parks the
    /// returned actions and fuses them into its ONE commit. Chained changes pass the previous pending
    /// metadata/protocol as the base via the closure.</summary>
    public static DeltaTable.DeferredSchemaChange ComputeSchemaChange(
        nint opener, string path, Func<DeltaTable, DeltaTable.DeferredSchemaChange> compute)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            return compute(table);
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>Reads exactly the rows identified by the given transient rowids, as DEEP-COPIED batches
    /// (Arrow-IPC round-trip — engineered-wood batch buffers do not outlive the open table) WITH the
    /// trailing virtual <c>_metadata.row_id</c> column. The read-back step of a buffered UPDATE.</summary>
    public static List<RecordBatch> ReadRowsByRowIds(
        nint opener, string path, IReadOnlyCollection<long> rowIds, CancellationToken ct,
        long? atVersion = null)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options()).GetAwaiter().GetResult();
        try
        {
            var ms = new System.IO.MemoryStream();
            Apache.Arrow.Ipc.ArrowStreamWriter? w = null;
            var e = table.ReadRowsByRowIdsAsync(rowIds, ct, atVersion).GetAsyncEnumerator(ct);
            try
            {
                while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    var b = e.Current;
                    w ??= new Apache.Arrow.Ipc.ArrowStreamWriter(ms, b.Schema, leaveOpen: true);
                    w.WriteRecordBatchAsync(b, ct).GetAwaiter().GetResult();
                }
            }
            finally
            {
                e.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            var result = new List<RecordBatch>();
            if (w is not null)
            {
                w.WriteEndAsync(ct).GetAwaiter().GetResult();
                w.Dispose();
                ms.Position = 0;
                using var r = new Apache.Arrow.Ipc.ArrowStreamReader(ms);
                RecordBatch? rb;
                while ((rb = r.ReadNextRecordBatchAsync(ct).AsTask().GetAwaiter().GetResult()) is not null)
                {
                    result.Add(rb);
                }
            }
            return result;
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>DELETE via deletion vectors (no file rewrite) — for tables with deletion vectors enabled.
    /// <paramref name="rowIds"/> are ABSOLUTE transient rowids. Returns rows deleted.</summary>
    public static long DeleteByRowIdsViaVectors(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                                CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            return table.DeleteByRowIdsViaVectorsAsync(rowIds, ct).AsTask().GetAwaiter().GetResult().RowsDeleted;
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DELETE");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Time travel — the Arrow schema of the table AS OF a version/timestamp (the schema can differ from
    /// the latest, e.g. before an ADD COLUMN). <paramref name="unit"/> is "version" or "timestamp" (the DuckDB
    /// <c>AT</c> clause unit); <paramref name="value"/> is the BIGINT version or a parseable timestamp.</summary>
    public static Schema GetSchemaAt(nint opener, string path, string unit, string value)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var snap = ResolveSnapshotAsync(table, unit, value, default).AsTask().GetAwaiter().GetResult();
            return snap.ArrowSchema;
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>Time travel — streams the table AS OF a version/timestamp. A timestamp is resolved to its version
    /// first, so the version read path (with file/row-group filter pushdown) serves both. Read-only (a past
    /// snapshot is never written), so there is no rowid variant.</summary>
    public static IAsyncEnumerable<RecordBatch> StreamAt(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, string unit, string value,
        CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var parquet = filter is null
            ? ParquetReadOptions.Default
            // Bloom probing refines row-group pruning when min/max stats are inconclusive (point lookups
            // on high-cardinality columns) — natively-written files carry blooms on dict-encoded columns.
            : new ParquetReadOptions { Filter = filter, FilterUseBloomFilters = true };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamAtImpl(fs, options, columns, filter, unit, value, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamAtImpl(
        ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns, Predicate? filter,
        string unit, string value, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            var snap = await ResolveSnapshotAsync(table, unit, value, ct).ConfigureAwait(false);
            var nested = NestedMappedSchema(snap); // the AS-OF snapshot names the columns
            await foreach (var batch in table.ReadAtVersionAsync(snap.Version, columns, filter, ct)
                               .ConfigureAwait(false))
            {
                yield return nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The latest APPLICATION TRANSACTION version recorded for <paramref name="appId"/> (the Delta
    /// <c>txn</c> action's per-app high-water mark — the idempotent-append mechanism), or null when the app
    /// never committed one. Reads the Delta log only.</summary>
    public static long? GetAppTransactionVersion(nint opener, string path, string appId)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).AsTask().GetAwaiter().GetResult();
        try
        {
            return table.CurrentSnapshot.AppTransactions.TryGetValue(appId, out var txn) ? txn.Version : null;
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>The table's commit history (the snapshots/versions view) as an Arrow stream:
    /// <c>(version BIGINT, timestamp TIMESTAMP, operation VARCHAR, operation_parameters VARCHAR)</c>, oldest
    /// first. <c>timestamp</c> is non-null only on tables that record it (inCommitTimestamps or a commitInfo
    /// timestamp). Reads the Delta log only (no data files).</summary>
    public static IArrowArrayStream GetSnapshots(nint opener, string path)
    {
        var tsType = new TimestampType(TimeUnit.Microsecond, (string?)null);
        var versions = new Int64Array.Builder();
        var timestamps = new TimestampArray.Builder(tsType);
        var operations = new StringArray.Builder();
        var operationParams = new StringArray.Builder();
        int rows = CollectHistory(opener, path, versions, timestamps, operations, operationParams)
            .GetAwaiter().GetResult();

        var schema = new Schema(new[]
        {
            new Field("version", Int64Type.Default, nullable: false),
            new Field("timestamp", tsType, nullable: true),
            new Field("operation", StringType.Default, nullable: true),
            new Field("operation_parameters", StringType.Default, nullable: true),
        }, metadata: null);
        var batch = new RecordBatch(schema, new IArrowArray[]
        {
            versions.Build(), timestamps.Build(), operations.Build(), operationParams.Build(),
        }, rows);
        return new InMemoryArrayStream(schema, new[] { batch });
    }

    /// <summary>The Change Data Feed of the table between <paramref name="fromVersion"/> and
    /// <paramref name="toVersion"/> (inclusive; -1 =&gt; latest) as an Arrow stream — the table's columns plus
    /// <c>_change_type</c> ("insert"/"delete"/"update_preimage"/"update_postimage"), <c>_commit_version</c>,
    /// <c>_commit_timestamp</c>. Requires the table to have <c>delta.enableChangeDataFeed</c> (else
    /// engineered-wood errors). Streams lazily.</summary>
    public static IArrowArrayStream GetChanges(nint opener, string path, long fromVersion, long toVersion)
    {
        // Stream lazily (the table stays open for the whole enumeration — materializing then disposing frees the
        // batches' Arrow buffers = use-after-free), and advertise the ACTUAL schema by peeking the first batch
        // (hand-building it risks a column/type mismatch that SIGSEGVs arrow_ingest).
        var enumerator = StreamChanges(opener, path, fromVersion, toVersion, default).GetAsyncEnumerator(default);
        bool hasFirst = enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        var schema = hasFirst ? enumerator.Current.Schema : EmptyChangeSchema(opener, path);
        return new AsyncEnumerableArrowStream(schema, ReplayThenRest(hasFirst, enumerator));
    }

    private static async IAsyncEnumerable<RecordBatch> ReplayThenRest(
        bool hasFirst, IAsyncEnumerator<RecordBatch> enumerator)
    {
        try
        {
            if (hasFirst)
            {
                yield return enumerator.Current;
            }
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<RecordBatch> StreamChanges(
        nint opener, string path, long fromVersion, long toVersion, [EnumeratorCancellation] CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaTableOptions.Default, ct).ConfigureAwait(false);
        try
        {
            // engineered-wood's CdfReader silently INFERS changes from add/remove when a version has no cdc
            // files — misleading for copy-on-write DELETE/UPDATE (the rewritten file's survivors look like
            // inserts, the removed file like deletes). So require the table to actually have CDF enabled,
            // matching Spark/Delta (DELTA_CHANGE_DATA_FEED_NOT_ENABLED) rather than returning a bogus feed.
            if (!EngineeredWood.DeltaLake.ChangeDataFeed.CdfConfig.IsEnabled(table.CurrentSnapshot.Metadata.Configuration))
                throw new InvalidOperationException(
                    "Change Data Feed is not enabled on this Delta table (delta.enableChangeDataFeed). " +
                    "ATTACH with 'change_data_feed true' and create the table so future commits capture changes.");

            long end = toVersion < 0 ? table.CurrentSnapshot.Version : toVersion;
            // Nested mapped fields: change rows inferred from data files carry PHYSICAL struct-child names
            // (CdfReader's rename is top-level) — apply the recursive rename; the CDF metadata columns
            // (_change_type/...) are not table columns and pass through untouched.
            var nested = NestedMappedSchema(table.CurrentSnapshot);
            await foreach (var batch in table.ReadChangesAsync(fromVersion, end, ct).ConfigureAwait(false))
            {
                yield return nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Schema for an empty change feed (no changes in range): the table columns ++ the 3 CDF columns.</summary>
    private static Schema EmptyChangeSchema(nint opener, string path)
    {
        var us = GetSchema(opener, path);
        var fields = new List<Field>(us.FieldsList)
        {
            new Field("_change_type", StringType.Default, nullable: false),
            new Field("_commit_version", Int64Type.Default, nullable: false),
            new Field("_commit_timestamp", Int64Type.Default, nullable: true),
        };
        return new Schema(fields, us.Metadata);
    }

    private static async System.Threading.Tasks.Task<int> CollectHistory(
        nint opener, string path, Int64Array.Builder versions, TimestampArray.Builder timestamps,
        StringArray.Builder operations, StringArray.Builder operationParams)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        int rows = 0;
        try
        {
            await foreach (var h in table.GetHistoryAsync().ConfigureAwait(false))
            {
                versions.Append(h.Version);
                if (h.TimestampMs is { } ms)
                {
                    timestamps.Append(System.DateTimeOffset.FromUnixTimeMilliseconds(ms));
                }
                else
                {
                    timestamps.AppendNull();
                }
                if (h.Operation is { } op) { operations.Append(op); } else { operations.AppendNull(); }
                if (h.OperationParameters is { } p) { operationParams.Append(p); } else { operationParams.AppendNull(); }
                rows++;
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
        return rows;
    }

    /// <summary>Time travel WITH the trailing <c>_metadata.row_id</c> column — used when a time-travel scan
    /// requests the rowid (e.g. DuckDB's <c>count(*)</c>-via-rowid optimization). The version analog of
    /// <see cref="StreamWithRowIds"/>; a timestamp is resolved to its version first.</summary>
    public static IAsyncEnumerable<RecordBatch> StreamWithRowIdsAt(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, string unit, string value,
        CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var parquet = filter is null
            ? ParquetReadOptions.Default
            // Bloom probing refines row-group pruning when min/max stats are inconclusive (point lookups
            // on high-cardinality columns) — natively-written files carry blooms on dict-encoded columns.
            : new ParquetReadOptions { Filter = filter, FilterUseBloomFilters = true };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamWithRowIdsAtImpl(fs, options, columns, filter, unit, value, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamWithRowIdsAtImpl(
        ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns, Predicate? filter,
        string unit, string value, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            var snap = await ResolveSnapshotAsync(table, unit, value, ct).ConfigureAwait(false);
            var nested = NestedMappedSchema(snap); // the AS-OF snapshot names the columns
            await foreach (var batch in table.ReadAtVersionWithRowIdsAsync(snap.Version, columns, filter, ct)
                               .ConfigureAwait(false))
            {
                yield return nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Resolves the DuckDB <c>AT (unit =&gt; value)</c> clause to a Delta snapshot. "version" =&gt; that
    /// commit version; "timestamp" =&gt; the latest version at/just-before that instant. Any other unit errors.</summary>
    private static async System.Threading.Tasks.ValueTask<EngineeredWood.DeltaLake.Snapshot.Snapshot>
        ResolveSnapshotAsync(DeltaTable table, string unit, string value, CancellationToken ct)
    {
        if (string.Equals(unit, "version", System.StringComparison.OrdinalIgnoreCase))
        {
            long version = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            return await table.GetSnapshotAtVersionAsync(version, ct).ConfigureAwait(false);
        }
        if (string.Equals(unit, "timestamp", System.StringComparison.OrdinalIgnoreCase))
        {
            var ts = System.DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            return await table.GetSnapshotAtTimestampAsync(ts, ct).ConfigureAwait(false);
        }
        throw new System.NotSupportedException(
            $"delta: AT ({unit} => …) time travel is not supported — use AT (VERSION => n) or AT (TIMESTAMP => ts).");
    }

    /// <summary>Schema evolution — appends a nullable <paramref name="column"/> to the Delta table at
    /// <paramref name="path"/> as a metadata-only commit (no file rewrite); old files' missing values read back
    /// as NULL (engineered-wood backfills them). Opens with the standard write options (path_in_schema).</summary>
    public static void AddColumn(nint opener, string path, Field column, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.AddColumnAsync(column, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("ADD COLUMN");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Adds a field INSIDE a nested struct (metadata-only; <paramref name="containerPath"/> names
    /// the containing struct). Old files backfill the new member as NULL on read.</summary>
    public static void AddField(
        nint opener, string path, IReadOnlyList<string> containerPath, Field field, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.AddFieldAsync(containerPath, field, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("ADD COLUMN (nested field)");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Renames a field INSIDE a nested struct (metadata-only; requires column mapping).</summary>
    public static void RenameField(
        nint opener, string path, IReadOnlyList<string> fieldPath, string newName, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.RenameFieldAsync(fieldPath, newName, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("RENAME COLUMN (nested field)");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Drops a field INSIDE a nested struct (metadata-only; requires column mapping).</summary>
    public static void DropField(
        nint opener, string path, IReadOnlyList<string> fieldPath, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.DropFieldAsync(fieldPath, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DROP COLUMN (nested field)");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>True when the table at <paramref name="path"/> has column mapping enabled (either mode) — a
    /// cheap log-only open used to gate operations whose rewrite path can't produce the mapped layout.</summary>
    public static bool IsColumnMapped(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            return EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(table.CurrentSnapshot.Metadata.Configuration)
                   != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None;
        }
        finally
        {
            table.Dispose();
        }
    }

    /// <summary>Renames a column as a metadata-only commit (no file rewrite) — engineered-wood
    /// <see cref="DeltaTable.RenameColumnAsync"/>. Requires a column-mapping table (the field keeps its
    /// physicalName + columnMapping.id, so old files read unchanged under the new logical name); a plain table is
    /// rejected there.</summary>
    public static void RenameColumn(nint opener, string path, string oldName, string newName, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.RenameColumnAsync(oldName, newName, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("RENAME COLUMN");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Drops a column as a metadata-only commit (no file rewrite) — engineered-wood
    /// <see cref="DeltaTable.DropColumnAsync"/>. Requires a column-mapping table (old files keep the physical
    /// column; readers reconcile it away against the current schema); a plain table is rejected there.</summary>
    public static void DropColumn(nint opener, string path, string name, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.DropColumnAsync(name, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DROP COLUMN");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Maintenance: bin-pack COMPACTION (OPTIMIZE) — consolidates small files into larger ones,
    /// EXCLUDING deletion-vector-deleted rows (so it also materializes DV deletions). Returns 0 (not row-affecting).
    /// Compaction re-assigns row-tracking baseRowIds (stable-id preservation across compaction needs materialized
    /// row-id columns — a separate slice); the DATA is correct.</summary>
    public static long Optimize(nint opener, string path, CancellationToken ct, bool nativeWrite = false,
                                bool nativeRead = false)
    {
        var fs = TableFileSystems.Create(opener, path);
        // native_write => DuckDB's parquet writer produces the compacted files (bloom/stats/footer), so an
        // OPTIMIZE keeps the native-write quality instead of reverting to the engineered-wood codec.
        // native_read => the compaction READ half decodes the candidate files through read_parquet too
        // (the IDataFileReader seam) — with the writer, compaction preserves the variant annotation.
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(dataFileWriter: writer, dataFileReader: fileReader), ct)
            .AsTask().GetAwaiter().GetResult();
        try
        {
            var v = table.CompactAsync(null, ct).AsTask().GetAwaiter().GetResult();
            DmlLog.LogInformation("delta optimize {Path}: {Result} writer={Writer}", path,
                v.HasValue ? $"compacted → v{v.Value}" : "nothing to compact",
                writer is null ? "engineered-wood" : "native-duckdb");
            return 0;
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Maintenance: VACUUM — deletes data files no longer referenced by the log and older than the
    /// retention period (default the table's <c>VacuumRetention</c>). <paramref name="dryRun"/> lists without
    /// deleting. Returns the number of files deleted (0 on a dry run).</summary>
    public static long Vacuum(nint opener, string path, bool dryRun, double? retentionHours, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            System.TimeSpan? retention = retentionHours is { } h ? System.TimeSpan.FromHours(h) : null;
            var r = table.VacuumAsync(retention, dryRun, ct).AsTask().GetAwaiter().GetResult();
            DmlLog.LogInformation("delta vacuum {Path}: files_deleted={Files} dry_run={Dry}",
                path, r.FilesDeleted, dryRun);
            return r.FilesDeleted;
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Per-file copy-on-write UPDATE: only files containing a target <paramref name="rowIds"/> are
    /// rewritten. <paramref name="rewriteFile"/> (ordinal, the file's batches) returns the same rows with the SET
    /// columns modified on matched positions (the caller owns the typed substitution); engineered-wood re-writes
    /// them as plain remove+add with a clean schema. Opens with the standard write options (path_in_schema).</summary>
    public static void UpdateByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds,
        System.Func<long, IReadOnlyList<RecordBatch>, IReadOnlyList<RecordBatch>> rewriteFile, CancellationToken ct,
        bool nativeWrite = false, IDataFileRewriter? rewriter = null, bool nativeRead = false)
    {
        var fs = TableFileSystems.Create(opener, path);
        // native_write => DuckDB's parquet writer produces the rewritten file (bloom/stats/footer) AND, when the
        // caller supplied a rewriter, DuckDB's read_parquet reads the source + applies the SET substitution via a
        // LEFT JOIN (retiring the in-process BuildArray). EW still selects the affected files, computes stats, and
        // commits remove(old)+add(new). Without a rewriter (unsupported shape) EW reads + the rewriteFile callback
        // applies the substitution in-process — native_read routes THAT read through read_parquet too (the
        // IDataFileReader seam), which also feeds the merge-on-read matched-row reads.
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = DeltaTable.OpenAsync(fs,
                DeltaWriter.Options(dataFileWriter: writer, dataFileRewriter: rewriter, dataFileReader: fileReader), ct)
            .AsTask().GetAwaiter().GetResult();
        try
        {
            // On a nested column-mapping table the source batches engineered-wood hands the callback carry
            // PHYSICAL nested child names (EW's read rename is top-level only) — rename them to LOGICAL first
            // so the typed substitution (BuildArray carries over unmatched rows by logical name) sees the
            // table schema; EW's recursive ToPhysical converts the returned batches back for the rewrite.
            var snapSchema = table.CurrentSnapshot.Schema;
            if (EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(
                    table.CurrentSnapshot.Metadata.Configuration)
                    != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None
                && ArrowColumnMappingRename.HasNestedFields(snapSchema))
            {
                var inner = rewriteFile;
                rewriteFile = (ordinal, batches) =>
                {
                    var logical = new List<RecordBatch>(batches.Count);
                    foreach (var b in batches)
                        logical.Add(ArrowColumnMappingRename.RenameBatch(b, snapSchema, toPhysical: false));
                    return inner(ordinal, logical);
                };
            }
            table.UpdateByRowIdsAsync(rowIds, rewriteFile, ct).AsTask().GetAwaiter().GetResult();
            DmlLog.LogInformation("delta update-rewrite {Path}: rowids={RowIds} writer={Writer} rewriter={Rewriter}",
                path, rowIds.Count, writer is null ? "engineered-wood" : "native-duckdb",
                rewriter is null ? "engineered-wood" : "native-duckdb");
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("UPDATE");
        }
        finally
        {
            table.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
