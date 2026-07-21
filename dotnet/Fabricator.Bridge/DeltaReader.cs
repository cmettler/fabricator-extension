using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace Fabricator.Bridge;

/// <summary>
/// Reads a Delta Lake table (Curt Hagenlocher's engineered-wood, pure C#) whose IO is delegated to DuckDB's
/// <c>FileSystem</c> via <see cref="DuckDbTableFileSystem"/> — so Delta tables on local/az://-s3://-https://
/// paths read with DuckDB's secrets + backends. Surfaced to DuckDB as the <c>fabricator_delta_scan(path)</c>
/// connection-free GLOBAL host-FS table function (see <see cref="DeltaGlobalTableFunction"/>); the opener is
/// the calling operator's ClientContext, threaded via <see cref="AmbientOpener"/>.
/// </summary>
internal static class DeltaReader
{
    private static readonly ILogger DmlLog = FabricatorLog.CreateLogger("Fabricator.Delta.Write");

    /// <summary>The EXACT active data-file URIs of the current snapshot (the `add` set, NOT a glob — a glob would
    /// include tombstoned files). Relative `add.path`s are resolved against the table root; an abfss-OneLake root
    /// is rewritten to the <c>onelake://</c> scheme so DuckDB's native reader routes them to our FileSystem
    /// subsystem (+ ExternalFileCache). Used by the native-read path (docs/multifile-delta.md Phase A pre-spike):
    /// engineered-wood lists the files, DuckDB's native parquet reader reads them via read_parquet.</summary>
    // Sync-over-async CONVENTION (see CLAUDE.md "Sync-over-async cleanup"): the ABI-facing method is a thin
    // sync wrapper that blocks EXACTLY ONCE on a private async core, and the core uses ConfigureAwait(false)
    // at every await. This is the shape all Bridge IO should converge on (leaf-first, one seam at a time,
    // verified by verify_delta_catalog_* after each) — vs the legacy ".GetAwaiter().GetResult()"-at-every-await
    // form. Safe here because the hostfxr CLR has NO SynchronizationContext (so blocking can't deadlock) and
    // the ambients are AsyncLocal (flow across the pool-thread hops ConfigureAwait(false) may cause).
    public static IReadOnlyList<string> GetActiveFileUris(nint opener, string path)
        => GetActiveFileUrisAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<string>> GetActiveFileUrisAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
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
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The table's Delta properties (<c>metaData.configuration</c> — the <c>delta.*</c> keys), a copy
    /// (empty when none). Backs <c>fabricator_delta_tblproperties</c>.</summary>
    public static IReadOnlyDictionary<string, string> GetTableProperties(nint opener, string path)
        => GetTablePropertiesAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<IReadOnlyDictionary<string, string>> GetTablePropertiesAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            return cfg is null
                ? new Dictionary<string, string>()
                : cfg.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>SET/UNSET table properties as ONE metaData commit (merges <paramref name="updates"/> into the
    /// current <c>configuration</c>; a null value UNSETs the key). Returns the new commit version. Backs
    /// <c>fabricator_delta_set_tblproperties</c>. Pure config change — no protocol upgrade (the caller rejects
    /// feature-enabling keys); the merged metaData rides <c>extraActions</c> exactly like a buffered ALTER.</summary>
    public static long SetTableProperties(nint opener, string path, IReadOnlyList<KeyValuePair<string, string?>> updates)
        => SetTablePropertiesAsync(opener, path, updates).GetAwaiter().GetResult();

    private static async Task<long> SetTablePropertiesAsync(
        nint opener, string path, IReadOnlyList<KeyValuePair<string, string?>> updates)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            var snapshot = table.CurrentSnapshot;
            var merged = snapshot.Metadata.Configuration is null
                ? new Dictionary<string, string>()
                : snapshot.Metadata.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var kv in updates)
            {
                if (kv.Value is null)
                {
                    merged.Remove(kv.Key);
                }
                else
                {
                    merged[kv.Key] = kv.Value;
                }
            }
            var metaData = snapshot.Metadata with { Configuration = merged };
            return await table.CommitDataFilesAsync(
                System.Array.Empty<WrittenDataFile>(), DeltaWriteMode.Append,
                extraActions: new DeltaAction[] { metaData },
                expectedVersion: snapshot.Version, operation: "SET TBLPROPERTIES").ConfigureAwait(false);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>ALTER TABLE … SET SORTED BY (cols) / RESET SORTED BY (<paramref name="cols"/> empty):
    /// ONE metadata commit updating the <c>fabricator.sortedBy</c> ordered-write property AND — on an
    /// UNPARTITIONED table — the <c>delta.clustering</c> declaration (EW
    /// <c>SetClusteringColumnsAsync</c>, the ALTER CLUSTER BY analog: domain re-key/removal + the
    /// writer-only clustering/domainMetadata protocol upgrade when missing). A PARTITIONED table takes
    /// the property only (clustering and partitioning are mutually exclusive). Changing the keys makes
    /// existing ZCubes STALE (ZCUBE_ZORDER_BY mismatch) — the next OPTIMIZE reclusters them
    /// incrementally.</summary>
    public static void SetSortedBy(nint opener, string path, IReadOnlyList<string> cols, CancellationToken ct)
        => SetSortedByAsync(opener, path, cols, ct).GetAwaiter().GetResult();

    private static async Task<long> SetSortedByAsync(
        nint opener, string path, IReadOnlyList<string> cols, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            var snapshot = table.CurrentSnapshot;
            // Validate + canonicalize the columns against the schema (case-insensitive) up front — a typo
            // must not persist a dangling property/domain.
            var canonical = new List<string>(cols.Count);
            foreach (var c in cols)
            {
                var field = snapshot.Schema.Fields.FirstOrDefault(
                    x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"delta SET SORTED BY: column '{c}' is not a column of the table.");
                canonical.Add(field.Name);
            }
            var merged = snapshot.Metadata.Configuration is null
                ? new Dictionary<string, string>()
                : snapshot.Metadata.Configuration.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (canonical.Count > 0)
            {
                merged[DeltaWriter.SortedByKey] = DeltaWriter.SerializeSortedBy(canonical);
            }
            else
            {
                merged.Remove(DeltaWriter.SortedByKey);
            }
            var metaData = snapshot.Metadata with { Configuration = merged };
            if (snapshot.Metadata.PartitionColumns.Count > 0)
            {
                return await table.CommitDataFilesAsync(
                    System.Array.Empty<WrittenDataFile>(), DeltaWriteMode.Append,
                    cancellationToken: ct, extraActions: new DeltaAction[] { metaData },
                    expectedVersion: snapshot.Version,
                    operation: canonical.Count > 0 ? "SET SORTED BY" : "RESET SORTED BY")
                    .ConfigureAwait(false);
            }
            return await table.SetClusteringColumnsAsync(
                canonical.Count > 0 ? canonical : null,
                extraActions: new DeltaAction[] { metaData }, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("SET SORTED BY");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
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
    /// <summary><paramref name="NumRecords"/> = the add action's stats row count (null when the add carries
    /// no stats — external writers): with <paramref name="BaseRowId"/> it bounds the file's DERIVED stable-id
    /// range [baseRowId, baseRowId + numRecords) for the row-tracking filter fast path.</summary>
    /// <summary><paramref name="SizeBytes"/> = the add action's file size (null when unknown): with
    /// <paramref name="NumRecords"/> it estimates bytes-per-row for the clustered-OPTIMIZE file split.
    /// <paramref name="AddPath"/> = the add action's ENCODED log-relative path (identity — correlates the
    /// listing file back to its snapshot add for per-file removes). <paramref name="ZCubeId"/> /
    /// <paramref name="ZCubeBy"/> = the add's <c>tags[ZCUBE_ID]</c> / <c>tags[ZCUBE_ZORDER_BY]</c>
    /// (Spark's incremental-clustering cube identity + the keys it was clustered by).</summary>
    public sealed record NativeScanFile(int Ordinal, string Uri, long[] Dv,
                                        IReadOnlyDictionary<string, string>? PartitionValues = null,
                                        long? BaseRowId = null, long? CommitVersion = null,
                                        long? NumRecords = null, long? SizeBytes = null,
                                        string? AddPath = null, string? ZCubeId = null,
                                        string? ZCubeBy = null);

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
        => ResolveVersionAsOfAsync(opener, path, instantUtc, log).GetAwaiter().GetResult();

    private static async Task<long> ResolveVersionAsOfAsync(nint opener, string path, DateTime instantUtc, ILogger log)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            try
            {
                var snap = await table.GetSnapshotAtTimestampAsync(new DateTimeOffset(instantUtc, TimeSpan.Zero), default)
                    .ConfigureAwait(false);
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
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Lists the active data files for a native read: resolves the snapshot (latest / at
    /// <paramref name="unit"/>+<paramref name="value"/>), assigns each file its GLOBAL path-sorted ordinal
    /// (rowid parity), applies best-effort Delta-log FILE pruning via <paramref name="prune"/> (skip a file whose
    /// stats/partitions can't match), and resolves each surviving file's deletion-vector positions.</summary>
    public static NativeScanList ListNativeScanFiles(
        nint opener, string path, string? unit, string? value, Predicate? prune, ILogger log,
        EngineeredWood.DeltaLake.Schema.StructType? schemaOverride = null)
        => ListNativeScanFilesAsync(opener, path, unit, value, prune, log, schemaOverride).GetAwaiter().GetResult();

    private static async Task<NativeScanList> ListNativeScanFilesAsync(
        nint opener, string path, string? unit, string? value, Predicate? prune, ILogger log,
        EngineeredWood.DeltaLake.Schema.StructType? schemaOverride)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            var snap = unit is null
                ? table.CurrentSnapshot
                : await ResolveSnapshotAsync(table, unit, value ?? "", default).ConfigureAwait(false);
            return await BuildNativeScanListAsync(fs, path, snap, prune, log, schemaOverride).ConfigureAwait(false);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The post-open core of ListNativeScanFilesAsync, callable against an ALREADY-OPEN table's snapshot —
    // the clustered-OPTIMIZE rewrite lists against the SAME snapshot its commit pins (expectedVersion), so
    // a writer landing between two separate opens can't produce a spurious conflict.
    private static async Task<NativeScanList> BuildNativeScanListAsync(
        EngineeredWood.IO.ITableFileSystem fs, string path, EngineeredWood.DeltaLake.Snapshot.Snapshot snap,
        Predicate? prune, ILogger log, EngineeredWood.DeltaLake.Schema.StructType? schemaOverride)
    {
        {
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
                    var deleted = await dvReader.ReadAsync(add.DeletionVector).ConfigureAwait(false);
                    dv = deleted.ToArray();
                    System.Array.Sort(dv);
                }
                // numRecords from the add's stats (Parse is null/error-tolerant; 0 = absent → unknown, the
                // derived-id range then stays unbounded above — external writers may omit stats).
                var addStats = EngineeredWood.DeltaLake.Actions.ColumnStats.Parse(add.Stats);
                long? numRecords = addStats is { NumRecords: > 0 } ? addStats.NumRecords : null;
                files.Add(new NativeScanFile(ordinal, uri, dv,
                    add.PartitionValues is { Count: > 0 } ? add.PartitionValues : null,
                    add.BaseRowId, add.DefaultRowCommitVersion, numRecords,
                    add.Size > 0 ? add.Size : null,
                    add.Path,
                    add.Tags is { } tg && tg.TryGetValue("ZCUBE_ID", out var zc) ? zc : null,
                    add.Tags is { } tb && tb.TryGetValue("ZCUBE_ZORDER_BY", out var zb) ? zb : null));
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
    }

    /// <summary>The active data files of the Delta table as a JSON array of objects <c>[{"path":"&lt;uri&gt;"}]</c>
    /// for the C++ MultiFileReader (docs/multifile-delta.md Phase A). Slice 1a: paths only (the exact `add` set,
    /// NOT a glob). Partition values + deletion vectors (later slices) become extra keys on each object; the
    /// <paramref name="pushJson"/> pushed-filter arg is accepted now and applied for file pruning in a later
    /// slice. Paths are absolute URIs (onelake:// for OneLake → native + cached).</summary>
    public static string ListScanFilesJson(nint opener, string path, string? pushJson)
        => ListScanFilesJsonAsync(opener, path, pushJson).GetAwaiter().GetResult();

    private static async Task<string> ListScanFilesJsonAsync(nint opener, string path, string? pushJson)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
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
                    var deleted = await dvReader.ReadAsync(add.DeletionVector).ConfigureAwait(false);
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
            await table.DisposeAsync().ConfigureAwait(false);
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
        => GetSchemaAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<Schema> GetSchemaAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            return table.ArrowSchema;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Like <see cref="GetSchema"/> but also reports whether <c>delta.enableRowTracking</c> is set —
    /// in the SAME table open, so the catalog's column fetch can cache the flag for the (immediately
    /// following) virtual-columns metadata fetch without a second <c>_delta_log</c> read (OneLake cost).</summary>
    public static Schema GetSchemaAndRowTracking(nint opener, string path, out bool rowTracking)
    {
        var (schema, rt) = GetSchemaAndRowTrackingAsync(opener, path).GetAwaiter().GetResult();
        rowTracking = rt;
        return schema;
    }

    private static async Task<(Schema Schema, bool RowTracking)> GetSchemaAndRowTrackingAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            bool rowTracking = cfg is not null
                && cfg.TryGetValue("delta.enableRowTracking", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
            return (table.ArrowSchema, rowTracking);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
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
        return StreamImpl(opener, fs, options, columns, filter, ct);
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
        nint opener, ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        // Cancel long-running reads (a big OneLake/S3 row-group) when the query is interrupted (Ctrl+C /
        // timeout): the scope polls DuckDB's interrupt flag and trips this token, which engineered-wood honors
        // between chunks. See docs/cancellation.md.
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var table = await DeltaTable.OpenAsync(fs, options, token).ConfigureAwait(false);
        try
        {
            var nested = NestedMappedSchema(table.CurrentSnapshot);
            await foreach (var batch in table.ReadAllAsync(columns, filter, token).ConfigureAwait(false))
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
        return StreamWithRowIdsImpl(opener, fs, options, columns, filter, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamWithRowIdsImpl(
        nint opener, ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var table = await DeltaTable.OpenAsync(fs, options, token).ConfigureAwait(false);
        try
        {
            var nested = NestedMappedSchema(table.CurrentSnapshot);
            await foreach (var batch in table.ReadAllWithRowIdsAsync(columns, filter, token).ConfigureAwait(false))
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
        => DeleteByRowIdsAsync(opener, path, rowIds, ct, nativeWrite, nativeRead).GetAwaiter().GetResult();

    private static async Task<long> DeleteByRowIdsAsync(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                      CancellationToken ct, bool nativeWrite, bool nativeRead)
    {
        // Cancel a long copy-on-write/DV rewrite (OneLake/S3) on query interrupt — the opener is fresh (the
        // modify operator's Finalize set it via FabricatorSetActiveTxn). See docs/cancellation.md.
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        // Open with the standard WRITE options (OmitPathInSchema=false) so the copy-on-write rewrite emits
        // standard-readable parquet — DeltaTableOptions.Default would drop path_in_schema (TProtocolException).
        // native_write => DuckDB's parquet writer produces the rewritten survivor file (bloom/stats/footer);
        // native_read => the read half decodes through read_parquet (the IDataFileReader seam —
        // variant-preserving). EW master owns the rewrite TRANSFORM itself (the former IDataFileRewriter —
        // read + drop-positions in one host SQL — was dropped upstream); it still selects the affected files,
        // computes stats, and commits remove(old)+add(new).
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(dataFileWriter: writer,
                                                                 dataFileReader: fileReader), token)
            .ConfigureAwait(false);
        try
        {
            long deleted = (await table.DeleteByRowIdsAsync(rowIds, token).ConfigureAwait(false)).RowsDeleted;
            DmlLog.LogInformation("delta delete-rewrite {Path}: deleted={Deleted} writer={Writer}",
                path, deleted, writer is null ? "engineered-wood" : "native-duckdb");
            return deleted;
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DELETE");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    // A rowid DELETE/UPDATE cannot be safely retried on a commit conflict: its absolute positions were computed
    // against the scanned snapshot, which a concurrent writer has changed. Surface a clear, retryable-by-the-user
    // error instead (re-running re-scans and recomputes the rowids).
    private static System.InvalidOperationException ConcurrentModification(
        string op, DeltaConflictException? detail = null) =>
        new($"delta: concurrent modification during {op} — another writer committed"
            + (detail is not null ? $" ({detail.Message})" : "; the row positions are no longer valid")
            + ". Retry the statement.");

    /// <summary>True if the Delta table at <paramref name="path"/> has <c>delta.enableDeletionVectors=true</c>
    /// — DELETE then uses deletion vectors (no file rewrite) instead of copy-on-write.</summary>
    public static bool IsDeletionVectorsEnabled(nint opener, string path)
        => IsDeletionVectorsEnabledAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<bool> IsDeletionVectorsEnabledAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            return cfg is not null
                && cfg.TryGetValue("delta.enableDeletionVectors", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
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
        => GetOrderedActiveBaseRowIdsAsync(opener, path, atVersion).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<long?>> GetOrderedActiveBaseRowIdsAsync(
        nint opener, string path, long? atVersion)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            return await table.OrderedActiveBaseRowIdsAsync(atVersion).ConfigureAwait(false);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static TxnDmlProfile GetTxnDmlProfile(nint opener, string path)
        => GetTxnDmlProfileAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<TxnDmlProfile> GetTxnDmlProfileAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
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
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads ONE table-configuration value — null when the key or the TABLE is absent (an
    /// append-shaped implicit create has nothing to read). E.g. the <c>fabricator.sortedBy</c>
    /// ordered-write spec an append re-applies.</summary>
    public static string? GetTableConfig(nint opener, string path, string key)
        => GetTableConfigAsync(opener, path, key).GetAwaiter().GetResult();

    private static async Task<string?> GetTableConfigAsync(nint opener, string path, string key)
    {
        try
        {
            var fs = TableFileSystems.Create(opener, path);
            await using var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            return cfg is not null && cfg.TryGetValue(key, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs a compute-only schema change (the <c>Compute*</c> family — ADD/RENAME/DROP COLUMN,
    /// nested ADD/DROP FIELD) against the table WITHOUT committing: the buffered transaction parks the
    /// returned actions and fuses them into its ONE commit. Chained changes pass the previous pending
    /// metadata/protocol as the base via the closure.</summary>
    public static DeltaTable.DeferredSchemaChange ComputeSchemaChange(
        nint opener, string path, Func<DeltaTable, DeltaTable.DeferredSchemaChange> compute)
        => ComputeSchemaChangeAsync(opener, path, compute).GetAwaiter().GetResult();

    private static async Task<DeltaTable.DeferredSchemaChange> ComputeSchemaChangeAsync(
        nint opener, string path, Func<DeltaTable, DeltaTable.DeferredSchemaChange> compute)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            return compute(table);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads exactly the rows identified by the given transient rowids, STREAMED LAZILY (one
    /// batch in flight — the table stays open for the enumeration and is disposed when it completes or
    /// is abandoned), WITH the trailing virtual <c>_metadata.row_id</c> column. The read-back step of a
    /// buffered UPDATE / CDF DELETE. Yielded batches are SELF-OWNED (engineered-wood arrays own their
    /// buffers) and stay valid after the table is disposed — the caller may retain or dispose them freely.
    /// <paramref name="sourceTrackingOut"/> (optional): one row-aligned entry per yielded batch with each
    /// row's ORIGINAL stable id/commit version (materialized source value else baseRowId + position) —
    /// plain value arrays; each batch's entry is appended BEFORE that batch is yielded, and the list is
    /// complete only once the enumeration finishes.</summary>
    public static IEnumerable<RecordBatch> ReadRowsByRowIds(
        nint opener, string path, IReadOnlyCollection<long> rowIds, CancellationToken ct,
        long? atVersion = null,
        List<(long?[] Ids, long?[] Versions)>? sourceTrackingOut = null)
        => BlockingEnumerable(ReadRowsByRowIdsAsync(opener, path, rowIds, atVersion, sourceTrackingOut, ct));

    private static async IAsyncEnumerable<RecordBatch> ReadRowsByRowIdsAsync(
        nint opener, string path, IReadOnlyCollection<long> rowIds,
        long? atVersion, List<(long?[] Ids, long?[] Versions)>? sourceTrackingOut,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Cancel a slow buffered-UPDATE read-back of the matched rows over OneLake/S3 on interrupt.
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        await using var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), token).ConfigureAwait(false);
        await foreach (var batch in table.ReadRowsByRowIdsAsync(rowIds, atVersion, sourceTrackingOut, token)
                           .ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    // Sync facade over an async stream for the synchronous ABI callers: blocks once per pulled item
    // (the per-item analog of the sync-wrapper convention; safe — the hostfxr CLR has no
    // SynchronizationContext). Equivalent to .NET 9's ToBlockingEnumerable, which the net8.0 target lacks.
    private static IEnumerable<T> BlockingEnumerable<T>(IAsyncEnumerable<T> source)
    {
        var e = source.GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                yield return e.Current;
            }
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>DELETE via deletion vectors (no file rewrite) — for tables with deletion vectors enabled.
    /// <paramref name="rowIds"/> are ABSOLUTE transient rowids. Returns rows deleted.</summary>
    public static long DeleteByRowIdsViaVectors(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                                CancellationToken ct, bool rowLevelRetry = false)
        => DeleteByRowIdsViaVectorsAsync(opener, path, rowIds, ct, rowLevelRetry).GetAwaiter().GetResult();

    private static async Task<long> DeleteByRowIdsViaVectorsAsync(nint opener, string path,
                                                IReadOnlyCollection<long> rowIds, CancellationToken ct, bool rowLevelRetry)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), token).ConfigureAwait(false);
        try
        {
            return (await table.DeleteByRowIdsViaVectorsAsync(rowIds, token, rowLevelRetry: rowLevelRetry)
                .ConfigureAwait(false)).RowsDeleted;
        }
        catch (DeltaConflictException ex)
        {
            throw ConcurrentModification("DELETE", ex);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Time travel — the Arrow schema of the table AS OF a version/timestamp (the schema can differ from
    /// the latest, e.g. before an ADD COLUMN). <paramref name="unit"/> is "version" or "timestamp" (the DuckDB
    /// <c>AT</c> clause unit); <paramref name="value"/> is the BIGINT version or a parseable timestamp.</summary>
    public static Schema GetSchemaAt(nint opener, string path, string unit, string value)
        => GetSchemaAtAsync(opener, path, unit, value).GetAwaiter().GetResult();

    private static async Task<Schema> GetSchemaAtAsync(nint opener, string path, string unit, string value)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            var snap = await ResolveSnapshotAsync(table, unit, value, default).ConfigureAwait(false);
            return snap.ArrowSchema;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
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
        return StreamAtImpl(opener, fs, options, columns, filter, unit, value, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamAtImpl(
        nint opener, ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns, Predicate? filter,
        string unit, string value, [EnumeratorCancellation] CancellationToken ct)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var table = await DeltaTable.OpenAsync(fs, options, token).ConfigureAwait(false);
        try
        {
            var snap = await ResolveSnapshotAsync(table, unit, value, token).ConfigureAwait(false);
            var nested = NestedMappedSchema(snap); // the AS-OF snapshot names the columns
            await foreach (var batch in table.ReadAtVersionAsync(snap.Version, columns, filter, token)
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
        => GetAppTransactionVersionAsync(opener, path, appId).GetAwaiter().GetResult();

    private static async Task<long?> GetAppTransactionVersionAsync(nint opener, string path, string appId)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            return table.CurrentSnapshot.AppTransactions.TryGetValue(appId, out var txn) ? txn.Version : null;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
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
        // Stream lazily (bounded memory — the feed can span many versions; the table stays open for the whole
        // enumeration), and advertise the ACTUAL schema by peeking the first batch (hand-building it risks a
        // column/type mismatch that SIGSEGVs arrow_ingest). NOTE the old "materializing then disposing the table
        // frees the batches' Arrow buffers" rationale was DISPROVEN 2026-07-16 (EW batches are self-owned; the
        // 2026-06-30 corruption was the CDF schema-mismatch fixed in the same pass) — laziness is kept purely
        // for the memory bound.
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
        return StreamWithRowIdsAtImpl(opener, fs, options, columns, filter, unit, value, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamWithRowIdsAtImpl(
        nint opener, ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns, Predicate? filter,
        string unit, string value, [EnumeratorCancellation] CancellationToken ct)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var table = await DeltaTable.OpenAsync(fs, options, token).ConfigureAwait(false);
        try
        {
            var snap = await ResolveSnapshotAsync(table, unit, value, token).ConfigureAwait(false);
            var nested = NestedMappedSchema(snap); // the AS-OF snapshot names the columns
            await foreach (var batch in table.ReadAtVersionWithRowIdsAsync(snap.Version, columns, filter, token)
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
        => AddColumnAsync(opener, path, column, ct).GetAwaiter().GetResult();

    private static async Task AddColumnAsync(nint opener, string path, Field column, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            await table.AddColumnAsync(column, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("ADD COLUMN");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Adds a field INSIDE a nested struct (metadata-only; <paramref name="containerPath"/> names
    /// the containing struct). Old files backfill the new member as NULL on read.</summary>
    public static void AddField(
        nint opener, string path, IReadOnlyList<string> containerPath, Field field, CancellationToken ct)
        => AddFieldAsync(opener, path, containerPath, field, ct).GetAwaiter().GetResult();

    private static async Task AddFieldAsync(
        nint opener, string path, IReadOnlyList<string> containerPath, Field field, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            await table.AddFieldAsync(containerPath, field, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("ADD COLUMN (nested field)");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Renames a field INSIDE a nested struct (metadata-only; requires column mapping).</summary>
    public static void RenameField(
        nint opener, string path, IReadOnlyList<string> fieldPath, string newName, CancellationToken ct)
        => RenameFieldAsync(opener, path, fieldPath, newName, ct).GetAwaiter().GetResult();

    private static async Task RenameFieldAsync(
        nint opener, string path, IReadOnlyList<string> fieldPath, string newName, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            await table.RenameFieldAsync(fieldPath, newName, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("RENAME COLUMN (nested field)");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Drops a field INSIDE a nested struct (metadata-only; requires column mapping).</summary>
    public static void DropField(
        nint opener, string path, IReadOnlyList<string> fieldPath, CancellationToken ct)
        => DropFieldAsync(opener, path, fieldPath, ct).GetAwaiter().GetResult();

    private static async Task DropFieldAsync(
        nint opener, string path, IReadOnlyList<string> fieldPath, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            await table.DropFieldAsync(fieldPath, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DROP COLUMN (nested field)");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>True when the table at <paramref name="path"/> has column mapping enabled (either mode) — a
    /// cheap log-only open used to gate operations whose rewrite path can't produce the mapped layout.</summary>
    public static bool IsColumnMapped(nint opener, string path)
        => IsColumnMappedAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<bool> IsColumnMappedAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs).ConfigureAwait(false);
        try
        {
            return EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(table.CurrentSnapshot.Metadata.Configuration)
                   != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Renames a column as a metadata-only commit (no file rewrite) — engineered-wood
    /// <see cref="DeltaTable.RenameColumnAsync"/>. Requires a column-mapping table (the field keeps its
    /// physicalName + columnMapping.id, so old files read unchanged under the new logical name); a plain table is
    /// rejected there.</summary>
    public static void RenameColumn(nint opener, string path, string oldName, string newName, CancellationToken ct)
        => RenameColumnAsync(opener, path, oldName, newName, ct).GetAwaiter().GetResult();

    private static async Task RenameColumnAsync(
        nint opener, string path, string oldName, string newName, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            await table.RenameColumnAsync(oldName, newName, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("RENAME COLUMN");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Drops a column as a metadata-only commit (no file rewrite) — engineered-wood
    /// <see cref="DeltaTable.DropColumnAsync"/>. Requires a column-mapping table (old files keep the physical
    /// column; readers reconcile it away against the current schema); a plain table is rejected there.</summary>
    public static void DropColumn(nint opener, string path, string name, CancellationToken ct)
        => DropColumnAsync(opener, path, name, ct).GetAwaiter().GetResult();

    private static async Task DropColumnAsync(nint opener, string path, string name, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
        try
        {
            await table.DropColumnAsync(name, ct).ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("DROP COLUMN");
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Maintenance: bin-pack COMPACTION (OPTIMIZE) — consolidates small files into larger ones,
    /// EXCLUDING deletion-vector-deleted rows (so it also materializes DV deletions). Returns 0 (not row-affecting).
    /// Compaction re-assigns row-tracking baseRowIds (stable-id preservation across compaction needs materialized
    /// row-id columns — a separate slice); the DATA is correct.</summary>
    public static long Optimize(nint opener, string path, CancellationToken ct, bool nativeWrite = false,
                                bool nativeRead = false, bool full = false)
        => OptimizeAsync(opener, path, ct, nativeWrite, nativeRead, full).GetAwaiter().GetResult();

    private static async Task<long> OptimizeAsync(nint opener, string path, CancellationToken ct,
                                bool nativeWrite, bool nativeRead, bool full)
    {
        // OPTIMIZE (compaction) rewrites many files — cancel a slow one on interrupt (opener set fresh by
        // fabricator_exec). See docs/cancellation.md.
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(dataFileWriter: writer, dataFileReader: fileReader), token)
            .ConfigureAwait(false);
        try
        {
            // A clustering-declared table (the delta.clustering domain, else the fabricator.sortedBy property)
            // RECLUSTERS instead of bin-packing when the native writer is available: ONE host query reads every
            // active file (DV rows excluded), globally re-orders — hilbert_index over ntile range-buckets for
            // 2+ keys (Spark's range_partition_id shape, type-agnostic), plain ORDER BY for one — and COPYs the
            // clustered file; ONE dataChange=false commit swaps the active set, tagging add.clusteringProvider.
            // DuckDB's spilling sort does the reorder, so the rewrite streams (data never crosses the C ABI).
            var clusterCols = writer is not null ? ResolveClusteringColumns(table) : null;
            if (clusterCols is { Count: > 0 } && ClusteredRewriteEligible(table))
            {
                long? cv = await ClusteredRewriteAsync(fs, path, table, clusterCols, full, token).ConfigureAwait(false);
                DmlLog.LogInformation("delta optimize {Path}: {Result} clustered by [{Cols}] full={Full}", path,
                    cv.HasValue ? $"reclustered → v{cv.Value}" : "nothing to recluster",
                    string.Join(",", clusterCols), full);
                return 0;
            }
            var v = await table.CompactAsync(null, token).ConfigureAwait(false);
            DmlLog.LogInformation("delta optimize {Path}: {Result} writer={Writer}", path,
                v.HasValue ? $"compacted → v{v.Value}" : "nothing to compact",
                writer is null ? "engineered-wood" : "native-duckdb");
            return 0;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The table's clustering key as LOGICAL column names, or null when the table declares none (or
    /// the declaration isn't usable — nested clustering paths, an unknown column). Sources, in order: the
    /// <c>delta.clustering</c> domain (authoritative; stores PHYSICAL names — resolved back through the mapped
    /// schema), else the <c>fabricator.sortedBy</c> property (tables created by SORTED BY before the domain
    /// declaration existed).</summary>
    private static IReadOnlyList<string>? ResolveClusteringColumns(DeltaTable table)
    {
        var snap = table.CurrentSnapshot;
        var mode = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(snap.Metadata.Configuration);
        if (snap.DomainMetadata.TryGetValue("delta.clustering", out var dm)
            && dm is { Removed: false, Configuration: { Length: > 0 } cfgJson })
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(cfgJson);
                if (doc.RootElement.TryGetProperty("clusteringColumns", out var colsEl)
                    && colsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var result = new List<string>();
                    foreach (var pathEl in colsEl.EnumerateArray())
                    {
                        if (pathEl.ValueKind != System.Text.Json.JsonValueKind.Array || pathEl.GetArrayLength() != 1)
                        {
                            return null; // a NESTED clustering column — not supported, keep bin-pack
                        }
                        string stored = pathEl[0].GetString() ?? "";
                        string? logical = null;
                        foreach (var f in snap.Schema.Fields)
                        {
                            if (string.Equals(EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(f, mode),
                                              stored, StringComparison.Ordinal)
                                || string.Equals(f.Name, stored, StringComparison.Ordinal))
                            {
                                logical = f.Name;
                                break;
                            }
                        }
                        if (logical is null)
                        {
                            return null; // stale/foreign declaration — never guess
                        }
                        result.Add(logical);
                    }
                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        if (snap.Metadata.Configuration is { } cfg
            && cfg.TryGetValue(DeltaWriter.SortedByKey, out var sortedJson)
            && DeltaWriter.ParseSortedBy(sortedJson) is { Count: > 0 } sorted)
        {
            var result = new List<string>(sorted.Count);
            foreach (var c in sorted)
            {
                var f = snap.Schema.Fields.FirstOrDefault(
                    x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase));
                if (f is null)
                {
                    return null; // persisted column no longer in the schema
                }
                result.Add(f.Name);
            }
            return result;
        }
        return null;
    }

    // Shapes the clustered SQL rewrite can't serve — they fall back to bin-pack CompactAsync (which handles
    // them via the reader/writer seams): identity/IcebergCompat (need engineered-wood's committing writer),
    // variant columns (the tagged-blob transport isn't read_parquet-representable), and nested columns under
    // column mapping (the recursive physical rename has no hook inside one SQL statement). PARTITIONED tables
    // ARE served — as PER-PARTITION reclustering (the Databricks ZORDER-on-partitioned analog).
    private static bool ClusteredRewriteEligible(DeltaTable table)
    {
        var snap = table.CurrentSnapshot;
        if (!table.SupportsExternalDataFileCommit)
        {
            return false;
        }
        var mode = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(snap.Metadata.Configuration);
        foreach (var f in snap.Schema.Fields)
        {
            if (HasVariant(f.Type))
            {
                return false;
            }
            if (mode != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None
                && f.Type is not EngineeredWood.DeltaLake.Schema.PrimitiveType)
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasVariant(EngineeredWood.DeltaLake.Schema.DeltaDataType t) => t switch
    {
        EngineeredWood.DeltaLake.Schema.PrimitiveType p =>
            string.Equals(p.TypeName, "variant", StringComparison.OrdinalIgnoreCase),
        EngineeredWood.DeltaLake.Schema.StructType st => st.Fields.Any(f => HasVariant(f.Type)),
        EngineeredWood.DeltaLake.Schema.ArrayType a => HasVariant(a.ElementType),
        EngineeredWood.DeltaLake.Schema.MapType m => HasVariant(m.KeyType) || HasVariant(m.ValueType),
        _ => false,
    };

    private static async Task<long?> ClusteredRewriteAsync(
        ITableFileSystem fs, string path, DeltaTable table,
        IReadOnlyList<string> clusterCols, bool full, CancellationToken token)
    {
        var snap = table.CurrentSnapshot;
        // List against the OPEN table's own snapshot (not a second open) so the commit's expectedVersion is
        // exactly the version the rewrite read — a concurrent commit conflicts cleanly instead of racing.
        var listing = await BuildNativeScanListAsync(fs, path, snap, prune: null, DmlLog, schemaOverride: null)
            .ConfigureAwait(false);
        if (listing.Files.Count == 0)
        {
            return null;
        }
        var mode = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetMode(snap.Metadata.Configuration);
        bool partitioned = snap.Metadata.PartitionColumns.Count > 0;

        // PARTITIONED tables recluster PER PARTITION (the Databricks ZORDER-on-partitioned analog; liquid
        // clustering proper is unpartitioned, so partitioned rewrites carry NO clusteringProvider tag and
        // NO delta.clustering declaration exists for them). Files group by their add.partitionValues —
        // canonicalized with keys normalized to PHYSICAL names, so mixed logical/physical key vintages of
        // one partition group together (EW's CanonicalPartitionKey parity). Unpartitioned = one "" group.
        Dictionary<string, string>? keyToPhysical = null;
        if (mode != EngineeredWood.DeltaLake.Schema.ColumnMappingMode.None)
        {
            keyToPhysical = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in snap.Schema.Fields)
            {
                keyToPhysical[f.Name] = EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(f, mode);
            }
        }
        string KeyOf(IReadOnlyDictionary<string, string>? pv)
        {
            if (pv is null || pv.Count == 0)
            {
                return "";
            }
            var parts = new List<string>(pv.Count);
            foreach (var kv in pv)
            {
                string k = keyToPhysical is not null && keyToPhysical.TryGetValue(kv.Key, out var p) ? p : kv.Key;
                parts.Add(k + "=" + (kv.Value ?? "\u0000<null>"));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("\u0001", parts);
        }
        var groups = listing.Files.GroupBy(f => KeyOf(f.PartitionValues), StringComparer.Ordinal).ToList();
        var addsByKey = snap.ActiveFiles.Values
            .GroupBy(a => KeyOf(a.PartitionValues), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Every user column (logical names), plus the materialized row-tracking pair when the table declares
        // them — the per-file COALESCE(materialized, baseRowId + position) bakes each row's ORIGINAL stable
        // id/version into the clustered file (the compaction preservation rule). Partition columns join the
        // INNER selection (FileSql renders them as typed literals — a cluster key may be one) but are
        // EXCLUDED from the written file (Delta layout: values live in add.partitionValues).
        bool materializeIds = snap.Metadata.Configuration is { } rtCfg
            && rtCfg.TryGetValue("delta.rowTracking.materializedRowIdColumnName", out var mrc)
            && string.Equals(mrc, DeltaNativeReader.RowTrackingIdColumn, StringComparison.Ordinal)
            && rtCfg.TryGetValue("delta.rowTracking.materializedRowCommitVersionColumnName", out var mvc)
            && string.Equals(mvc, DeltaNativeReader.RowTrackingVersionColumn, StringComparison.Ordinal);
        var dataCols = new List<string>(snap.Schema.Fields.Count + 2);
        foreach (var f in snap.Schema.Fields)
        {
            dataCols.Add(f.Name);
        }
        if (materializeIds)
        {
            dataCols.Add(DeltaNativeReader.RowTrackingIdColumn);
            dataCols.Add(DeltaNativeReader.RowTrackingVersionColumn);
        }
        var partSet = new HashSet<string>(snap.Metadata.PartitionColumns, StringComparer.OrdinalIgnoreCase);

        // The outermost projection renames logical → physical for the written file (data files carry physical
        // names in BOTH mapping modes); the row-tracking columns keep their literal, unmapped names.
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var select = new List<string>(dataCols.Count);
        foreach (var c in dataCols)
        {
            if (partSet.Contains(c))
            {
                continue;
            }
            var f = snap.Schema.Fields.FirstOrDefault(x => string.Equals(x.Name, c, StringComparison.Ordinal));
            string phys = f is null ? c : EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(f, mode);
            select.Add(string.Equals(phys, c, StringComparison.Ordinal) ? Q(c) : $"{Q(c)} AS {Q(phys)}");
        }
        string projection = string.Join(", ", select);
        int bits = clusterCols.Count > 1 ? Math.Min(15, 63 / clusterCols.Count) : 0;
        string BuildSource(string inner)
        {
            if (clusterCols.Count == 1 || bits < 1)
            {
                // One key (or a degenerate >63-key spec): plain lexicographic order — hilbert n=1 is the identity.
                return $"SELECT {projection} FROM ({inner}) ORDER BY " + string.Join(", ", clusterCols.Select(Q));
            }
            // Hilbert over per-key ntile range-buckets: rank-based bucketing is type-agnostic (strings, dates —
            // no 63-bit truncation) and derives the value distribution from the data itself, exactly Spark's
            // range_partition_id approach. Consecutive output rows are hilbert-neighbors in EVERY key.
            long buckets = 1L << bits;
            var tiles = new List<string>(clusterCols.Count);
            var coords = new List<string>(clusterCols.Count);
            for (int i = 0; i < clusterCols.Count; i++)
            {
                tiles.Add($"ntile({buckets.ToString(CultureInfo.InvariantCulture)}) OVER (ORDER BY {Q(clusterCols[i])}) - 1 AS __fabricator_h{i}");
                coords.Add($"__fabricator_h{i}");
            }
            return $"SELECT {projection} FROM (SELECT *, {string.Join(", ", tiles)} FROM ({inner})) "
                   + $"ORDER BY hilbert_index([{string.Join(", ", coords)}], {bits})";
        }

        // Stats schema: PHYSICAL names, USER columns only — the row-tracking columns stay out of the Delta
        // stats (the established materialized-column convention) and partition columns aren't in the files.
        var arrow = EngineeredWood.DeltaLake.Schema.SchemaConverter.ToArrowSchema(snap.Schema);
        var statsFields = new List<Field>(snap.Schema.Fields.Count);
        for (int i = 0; i < snap.Schema.Fields.Count; i++)
        {
            if (partSet.Contains(snap.Schema.Fields[i].Name))
            {
                continue;
            }
            statsFields.Add(new Field(
                EngineeredWood.DeltaLake.Schema.ColumnMapping.GetPhysicalName(snap.Schema.Fields[i], mode),
                arrow.FieldsList[i].DataType, nullable: true));
        }
        var statsSchema = new Schema(statsFields, null);
        string? fieldIdsSpec = DeltaWriter.BuildFieldIdsSpec(snap.Schema,
            partitioned ? new HashSet<string>(snap.Metadata.PartitionColumns, StringComparer.Ordinal) : null);

        // File split: ONE output file per group when its estimated output fits the target (the zero-crossing
        // fast path — the whole group runs inside one COPY), else SEQUENTIAL per-file COPYs cut at batch
        // boundaries so every output file is a CONTIGUOUS cluster range with tight min/max on all keys.
        // DuckDB's own FILE_SIZE_BYTES rotation is NOT usable here: the planner FORCE-disables order
        // preservation for any rotated/per-thread/partitioned COPY regardless of the
        // preserve_insertion_order setting, and the explicit PRESERVE_ORDER option THROWS with these
        // parameters (plan_copy_to_file.cpp — "PRESERVE_ORDER is not supported with these parameters"),
        // so the parallel sink interleaves the query's order across the rotating files (probed:
        // interleaved ranges + in-file inversions), which would defeat the clustering. Rows-per-file is
        // estimated from the source files' own add stats (bytes-per-row of the same data).
        string root = ToReadableRoot(path);
        long targetBytes = ResolveTargetFileSize(snap.Metadata.Configuration);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var files = new List<WrittenDataFile>();
        var removes = new List<DeltaAction>();

        // ZCUBE INCREMENTAL (Spark parity): a clustered rewrite tags its outputs with a shared
        // tags[ZCUBE_ID] (one fresh cube per group per run) + tags[ZCUBE_ZORDER_BY] (the keys it was
        // clustered by). On the next OPTIMIZE, files of a STABLE cube (total size >= the target cube
        // size, clustered by the CURRENT keys) are never rewritten — the candidates are the UNCLUSTERED
        // files (plain appends, stale-key cubes, pre-ZCube rewrites) plus at most ONE partial cube (the
        // most recent — merging one per run bounds write amplification), so OPTIMIZE cost tracks NEW
        // data, not table size. `OPTIMIZE <table> FULL` ignores cubes and reclusters everything.
        string byJson = System.Text.Json.JsonSerializer.Serialize(clusterCols);
        long targetCubeBytes = ResolveSizeProperty(
            snap.Metadata.Configuration, "fabricator.targetCubeSize", 100L * 1024 * 1024 * 1024);
        foreach (var g in groups)
        {
            var groupFiles = g.ToList();
            List<NativeScanFile> candidates;
            if (full)
            {
                candidates = groupFiles;
            }
            else
            {
                var unclustered = new List<NativeScanFile>();
                var cubes = new Dictionary<string, List<NativeScanFile>>(StringComparer.Ordinal);
                foreach (var f in groupFiles)
                {
                    // a cube clustered by DIFFERENT keys is stale — its files re-enter as candidates
                    if (f.ZCubeId is { } id && string.Equals(f.ZCubeBy, byJson, StringComparison.Ordinal))
                    {
                        if (!cubes.TryGetValue(id, out var members))
                        {
                            cubes[id] = members = new List<NativeScanFile>();
                        }
                        members.Add(f);
                    }
                    else
                    {
                        unclustered.Add(f);
                    }
                }
                List<NativeScanFile>? mergeCube = null;
                long mergeRecency = -1;
                foreach (var kv in cubes)
                {
                    if (kv.Value.Sum(f => f.SizeBytes ?? 0) >= targetCubeBytes)
                    {
                        continue; // STABLE — never rewritten
                    }
                    long recency = kv.Value.Max(f => f.CommitVersion ?? 0);
                    if (recency > mergeRecency)
                    {
                        mergeRecency = recency;
                        mergeCube = kv.Value;
                    }
                }
                candidates = unclustered;
                if (mergeCube is not null
                    && (unclustered.Count > 0 || mergeCube.Any(f => f.Dv.Length > 0)))
                {
                    candidates = unclustered.Concat(mergeCube).ToList();
                }
            }
            if (candidates.Count == 0 || (candidates.Count == 1 && candidates[0].Dv.Length == 0))
            {
                continue; // nothing new to cluster (a lone DV-less file joins the next round's merge)
            }
            var cubeTags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ZCUBE_ID"] = Guid.NewGuid().ToString(),
                ["ZCUBE_ZORDER_BY"] = byJson,
            };
            // The group's Hive directory (decoded), inherited from its sources — one partition = one dir;
            // "" for an unpartitioned table.
            string rel0 = groupFiles[0].Uri.StartsWith(root + "/", StringComparison.Ordinal)
                ? groupFiles[0].Uri.Substring(root.Length + 1) : groupFiles[0].Uri;
            int dirSlash = rel0.LastIndexOf('/');
            string dir = dirSlash >= 0 ? rel0.Substring(0, dirSlash + 1) : "";
            var partitionValues = groupFiles[0].PartitionValues;

            var subListing = new NativeScanList
            {
                Version = listing.Version,
                PartitionColumns = listing.PartitionColumns,
                Files = candidates,
                AnyUri = candidates[0].Uri,
                LogicalToPhysical = listing.LogicalToPhysical,
                LogicalToFieldId = listing.LogicalToFieldId,
                MappedSchema = listing.MappedSchema,
                TableSchema = listing.TableSchema,
            };
            string source = BuildSource(DeltaNativeReader.FullTableSql(subListing, dataCols));

            long estBytes = 0, estRows = 0;
            bool sizesKnown = true;
            foreach (var f in candidates)
            {
                if (f.SizeBytes is { } sb && f.NumRecords is { } nr)
                {
                    estBytes += sb;
                    estRows += nr;
                }
                else
                {
                    sizesKnown = false; // external add without stats — can't estimate, keep one file
                    break;
                }
            }
            var copied = new List<NativeParquetDataFileWriter.CopiedFile>();
            if (!sizesKnown || estRows == 0 || estBytes <= targetBytes + targetBytes / 4)
            {
                copied = NativeParquetDataFileWriter.RunCopySql(
                    root, $"{dir}{Guid.NewGuid():N}.parquet", source, token, statsSchema, fieldIdsSpec);
            }
            else
            {
                long rowsPerFile = Math.Max(1024, (long)((double)targetBytes * estRows / estBytes));
                using var src = HostFs.Query(source, ct: token);
                while (true)
                {
                    var first = src.ReadNextRecordBatchAsync(token).AsTask().GetAwaiter().GetResult();
                    if (first is null)
                    {
                        break;
                    }
                    string chunkRel = $"{dir}{Guid.NewGuid():N}.parquet";
                    using var chunk = new BudgetedStream(src, first, rowsPerFile, token);
                    var one = NativeParquetDataFileWriter.RunCopy(
                        root, chunkRel, chunk, token, statsSchema, fieldIdsSpec: fieldIdsSpec);
                    copied.Add(new NativeParquetDataFileWriter.CopiedFile(
                        chunkRel, one.Rows, one.Size, null, one.Stats));
                }
            }
            foreach (var cf in copied)
            {
                files.Add(new WrittenDataFile(cf.RelativePath, cf.Size, cf.Rows, partitionValues, cf.Stats,
                    cubeTags));
            }
            // The candidates' old files become tombstones — removes hand-built from the SNAPSHOT's adds
            // (full action fields incl. deletion vectors), joined by the add's encoded path, so untouched
            // partitions AND stable cubes keep their files ACTIVE (partial/incremental recluster).
            var candidatePaths = new HashSet<string>(
                candidates.Select(f => f.AddPath).Where(x => x is not null)!, StringComparer.Ordinal);
            foreach (var add in addsByKey[g.Key])
            {
                if (!candidatePaths.Contains(add.Path))
                {
                    continue;
                }
                removes.Add(new RemoveFile
                {
                    Path = add.Path,
                    DeletionTimestamp = now,
                    DataChange = false,
                    ExtendedFileMetadata = true,
                    PartitionValues = add.PartitionValues,
                    Size = add.Size,
                    DeletionVector = add.DeletionVector,
                });
            }
        }
        if (files.Count == 0)
        {
            return null; // every partition already one contiguous DV-less range — no-op (Spark parity)
        }
        try
        {
            return await table.CommitDataFilesAsync(files, DeltaWriteMode.Append,
                cancellationToken: token, extraActions: removes,
                expectedVersion: listing.Version, operation: "OPTIMIZE",
                dataChange: false, clusteringProvider: partitioned ? null : "liquid").ConfigureAwait(false);
        }
        catch (DeltaConflictException)
        {
            throw ConcurrentModification("OPTIMIZE");
        }
    }

    /// <summary>The clustered rewrite's per-file size target: the <c>delta.targetFileSize</c> table property
    /// (Databricks; plain bytes or a b/kb/mb/gb suffix), else 128 MiB — engineered-wood's own
    /// <c>CompactionOptions.TargetFileSize</c> default, so clustered and bin-pack OPTIMIZE aim alike.</summary>
    private static long ResolveTargetFileSize(IReadOnlyDictionary<string, string>? cfg)
        => ResolveSizeProperty(cfg, "delta.targetFileSize", 128L * 1024 * 1024);

    /// <summary>A byte-size table property (plain bytes or a b/kb/mb/gb suffix); the default when absent
    /// or unparseable. Also serves <c>fabricator.targetCubeSize</c> — the ZCube stability threshold for
    /// incremental reclustering (default 100 GiB, the Databricks target cube size).</summary>
    private static long ResolveSizeProperty(IReadOnlyDictionary<string, string>? cfg, string key, long Default)
    {
        if (cfg is null || !cfg.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        {
            return Default;
        }
        v = v.Trim().ToLowerInvariant();
        long mult = 1;
        if (v.EndsWith("gb", StringComparison.Ordinal)) { mult = 1L << 30; v = v.Substring(0, v.Length - 2); }
        else if (v.EndsWith("mb", StringComparison.Ordinal)) { mult = 1L << 20; v = v.Substring(0, v.Length - 2); }
        else if (v.EndsWith("kb", StringComparison.Ordinal)) { mult = 1L << 10; v = v.Substring(0, v.Length - 2); }
        else if (v.EndsWith("b", StringComparison.Ordinal)) { v = v.Substring(0, v.Length - 1); }
        return long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n * mult
            : Default;
    }

    // One output file's slice of the shared sorted stream: yields `first`, then keeps pulling the SOURCE
    // until the cumulative row count reaches the budget (cut at batch boundaries — no slicing, so an
    // imported batch is never split across lifetimes). Does NOT own the source; the outer loop pulls the
    // next file's first batch itself (null = source exhausted).
    private sealed class BudgetedStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _src;
        private readonly long _budget;
        private readonly CancellationToken _ct;
        private RecordBatch? _first;
        private long _rows;

        public BudgetedStream(IArrowArrayStream src, RecordBatch first, long budget, CancellationToken ct)
        {
            _src = src;
            _first = first;
            _budget = budget;
            _ct = ct;
        }

        public Schema Schema => _src.Schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            if (_first is { } f)
            {
                _first = null;
                _rows = f.Length;
                return new ValueTask<RecordBatch?>(f);
            }
            if (_rows >= _budget)
            {
                return new ValueTask<RecordBatch?>((RecordBatch?)null);
            }
            var next = _src.ReadNextRecordBatchAsync(_ct).AsTask().GetAwaiter().GetResult();
            if (next is not null)
            {
                _rows += next.Length;
            }
            return new ValueTask<RecordBatch?>(next);
        }

        public void Dispose()
        {
            _first?.Dispose();
            _first = null;
        }
    }

    /// <summary>Maintenance: VACUUM — deletes data files no longer referenced by the log and older than the
    /// retention period (default the table's <c>VacuumRetention</c>). <paramref name="dryRun"/> lists without
    /// deleting. Returns the number of files deleted (0 on a dry run).</summary>
    public static long Vacuum(nint opener, string path, bool dryRun, double? retentionHours, CancellationToken ct)
        => VacuumAsync(opener, path, dryRun, retentionHours, ct).GetAwaiter().GetResult();

    private static async Task<long> VacuumAsync(
        nint opener, string path, bool dryRun, double? retentionHours, CancellationToken ct)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), token).ConfigureAwait(false);
        try
        {
            System.TimeSpan? retention = retentionHours is { } h ? System.TimeSpan.FromHours(h) : null;
            var r = await table.VacuumAsync(retention, dryRun, token).ConfigureAwait(false);
            DmlLog.LogInformation("delta vacuum {Path}: files_deleted={Files} dry_run={Dry}",
                path, r.FilesDeleted, dryRun);
            return r.FilesDeleted;
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Per-file copy-on-write UPDATE: only files containing a target <paramref name="rowIds"/> are
    /// rewritten. <paramref name="rewriteFile"/> (ordinal, the file's batches) returns the same rows with the SET
    /// columns modified on matched positions (the caller owns the typed substitution); engineered-wood re-writes
    /// them as plain remove+add with a clean schema. Opens with the standard write options (path_in_schema).</summary>
    public static void UpdateByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds,
        System.Func<long, IReadOnlyList<RecordBatch>, IReadOnlyList<RecordBatch>> rewriteFile, CancellationToken ct,
        bool nativeWrite = false, bool nativeRead = false)
        => UpdateByRowIdsAsync(opener, path, rowIds, rewriteFile, ct, nativeWrite, nativeRead)
            .GetAwaiter().GetResult();

    private static async Task UpdateByRowIdsAsync(nint opener, string path, IReadOnlyCollection<long> rowIds,
        System.Func<long, IReadOnlyList<RecordBatch>, IReadOnlyList<RecordBatch>> rewriteFile, CancellationToken ct,
        bool nativeWrite, bool nativeRead)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        // native_write => DuckDB's parquet writer produces the rewritten file (bloom/stats/footer). EW reads the
        // affected files + hands the batches to the rewriteFile callback (the in-process typed substitution);
        // native_read routes THAT read through read_parquet (the IDataFileReader seam). EW master owns the
        // rewrite semantics — the former IDataFileRewriter SQL-join substitution was dropped upstream, and its
        // row-tracking id projection is obsolete (master preserves ids through rewrites itself).
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = await DeltaTable.OpenAsync(fs,
                DeltaWriter.Options(dataFileWriter: writer, dataFileReader: fileReader), token)
            .ConfigureAwait(false);
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
            // NOTE (EW master): the CoW UPDATE has no row-level retry (the fork's rowLevelRetry rebase is
            // gone) — a concurrent commit aborts with DeltaConflictException → the retry-the-statement error.
            await table.UpdateByRowIdsAsync(rowIds, rewriteFile, token)
                .ConfigureAwait(false);
            DmlLog.LogInformation("delta update-rewrite {Path}: rowids={RowIds} writer={Writer}",
                path, rowIds.Count, writer is null ? "engineered-wood" : "native-duckdb");
        }
        catch (DeltaConflictException ex)
        {
            throw ConcurrentModification("UPDATE", ex);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }
}
