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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
    /// (empty when none). Backs <c>delta.tblproperties</c>.</summary>
    public static IReadOnlyDictionary<string, string> GetTableProperties(nint opener, string path)
        => GetTablePropertiesAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<IReadOnlyDictionary<string, string>> GetTablePropertiesAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
    /// <c>delta.set_tblproperties</c>. Pure config change — no protocol upgrade (the caller rejects
    /// feature-enabling keys); the merged metaData rides <c>extraActions</c> exactly like a buffered ALTER.</summary>
    public static long SetTableProperties(nint opener, string path, IReadOnlyList<KeyValuePair<string, string?>> updates)
        => SetTablePropertiesAsync(opener, path, updates).GetAwaiter().GetResult();

    /// <summary>Writes a checkpoint for the table's CURRENT version (engineered-wood's
    /// <c>DeltaTable.CheckpointAsync</c> — the table's own ParquetWriteOptions/CheckpointFormat, and log
    /// cleanup runs after it exactly as on an automatic checkpoint). Returns the version checkpointed.
    /// Opens FRESH deliberately: a checkpoint of a stale cached snapshot would silently checkpoint an old
    /// version — the caller asked for "now".</summary>
    public static long Checkpoint(nint opener, string path)
        => CheckpointAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<long> CheckpointAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
        try
        {
            return await table.CheckpointAsync().ConfigureAwait(false);
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<long> SetTablePropertiesAsync(
        nint opener, string path, IReadOnlyList<KeyValuePair<string, string?>> updates)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
                expectedVersion: snapshot.Version, operation: "SET TBLPROPERTIES",
                // ⚠ VACUOUSLY TRUE, AND THAT IS THE WHOLE JUSTIFICATION — the file list is
                // Array.Empty, so this commit writes NO ROWS and there is nothing a CHECK constraint,
                // an invariant or a generation expression could be violated by. engineered-wood applies
                // its refusal to the CALL rather than to the rows, so without this a table declaring
                // delta.constraints.* rejects every property edit — including the one that would REMOVE
                // the constraint. MEASURED before fixing: `set_tblproperties('…', '{"delta.constraints.
                // pos": null}')` failed with "this write path cannot evaluate it against the rows",
                // i.e. the declaration was a ONE-WAY DOOR and the only escape was DROP + re-create.
                // ⚠ Legitimate ONLY while the file list is empty. Passing this on a call that writes
                // files would be a claim we cannot support, and a wrong one poisons the table for every
                // later reader.
                constraintsEnforcedByCaller: true).ConfigureAwait(false);
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
                    operation: canonical.Count > 0 ? "SET SORTED BY" : "RESET SORTED BY",
                    // Vacuously true — empty file list, so no rows exist to violate anything. Same
                    // argument as the SET TBLPROPERTIES commit above, which carries the full note.
                    // ⚠ Only THIS branch needs it: the non-partitioned branch below goes through
                    // SetClusteringColumnsAsync, which does not run the write-time expression check at
                    // all (verified — SET SORTED BY on a non-partitioned constrained table already
                    // worked). So the two spellings agree rather than one being an oversight.
                    constraintsEnforcedByCaller: true)
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
        EngineeredWood.DeltaLake.Schema.StructType? schemaOverride = null, DeltaTableBinding? bound = null)
        => ListNativeScanFilesAsync(opener, path, unit, value, prune, log, schemaOverride, bound)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Opens a table for a READ, reusing the one <paramref name="bound"/>'s transaction already has open
    /// when there is one (the shared open lives ON the <see cref="DeltaTableBinding"/> since 4c). Returns
    /// whether the result is SHARED — a shared table must NOT be disposed by the borrower, because
    /// engineered-wood's <c>Dispose</c> latches <c>_disposed</c> and the next reader would then throw.
    /// </summary>
    /// <remarks>
    /// ⚠ READ PATHS ONLY. Sharing is sound only because no engineered-wood read path assigns
    /// <c>_currentSnapshot</c>; a writer must open its own, which the commit flush independently requires so
    /// its conflict range stays empty (<c>verify_delta_catalog_transactions</c> §41). All four callers pass
    /// the identical <c>DeltaWriter.Options()</c> — a site whose options differ (a reader/writer seam, a
    /// write spec) must NOT join this cache, since the options are baked into the table at construction.
    /// <para>⚠ THE BINDING IS A PARAMETER, NOT AN AMBIENT (slice 4b, retyped by 4c): this class is static
    /// while the binding is per-catalog state (owned by the catalog's <see cref="DeltaTransaction"/>), so
    /// the catalog threads it in — the write-spec-saga shape. A null binding means "no reuse": the caller
    /// owns what it opens and disposes it, the pre-cache behaviour. That is what a path-based global
    /// function or an ExternalTableRouting schema probe gets — the old static registry cached their opens
    /// under the ambient id and, with no catalog to release that id, LEAKED the entry until the 4096 panic
    /// clear. A TRANSIENT binding (an AT bind, or a transaction-free bind) declines retention itself
    /// (<see cref="DeltaTableBinding.CanRetainOpen"/>) and behaves exactly like null here.</para>
    /// </remarks>
    private static async Task<(DeltaTableBinding.OpenTable Open, bool Shared)> OpenForReadAsync(
        nint opener, string path, DeltaTableBinding? bound)
    {
        if (bound?.TryGetOpen() is { } hit)
        {
            return (hit, true);
        }
        bool retainable = bound?.CanRetainOpen == true;
        // A MISS is the thing worth logging, because each one is a whole `_delta_log` replay (~20-27 s on the
        // profiled Fabric table). The txn id is on the line because reuse is scoped to it: two misses for one
        // table in one statement mean the crossings ran under DIFFERENT transaction ids, which is a host-side
        // question and not a cache bug. txn 0 = no binding, never cached at all.
        DmlLog.LogDebug("delta table open {Path} (txn={Txn}) — cache miss", path, bound?.OwnerId ?? 0);
        // outlivesThisCall: the filesystem must not capture the host opener when it is about to be cached.
        var fs = TableFileSystems.Create(opener, path, outlivesThisCall: retainable);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
        if (!retainable)
        {
            return (new DeltaTableBinding.OpenTable(table, fs), false);
        }
        // A concurrent opener may have won; take the winner so "one table per (txn, path)" holds. Ours is
        // then orphaned to the GC, which costs nothing — Dispose only sets a flag.
        // ⚠ `Shared` comes from Publish, NOT from retainability alone: the binding DECLINES past its
        // transaction's open cap (catalog enumeration), and a declined entry is ours to dispose exactly
        // as before.
        var (entry, cached) = bound!.PublishOpen(new DeltaTableBinding.OpenTable(table, fs));
        return (entry, cached);
    }

    private static async Task<NativeScanList> ListNativeScanFilesAsync(
        nint opener, string path, string? unit, string? value, Predicate? prune, ILogger log,
        EngineeredWood.DeltaLake.Schema.StructType? schemaOverride, DeltaTableBinding? bound)
    {
        var (open, shared) = await OpenForReadAsync(opener, path, bound).ConfigureAwait(false);
        var (table, fs) = (open.Table, open.Fs);
        try
        {
            var snap = unit is null
                ? table.CurrentSnapshot
                : await ResolveSnapshotAsync(table, unit, value ?? "", default).ConfigureAwait(false);
            return await BuildNativeScanListAsync(fs, path, table, snap, prune, log, schemaOverride).ConfigureAwait(false);
        }
        finally
        {
            if (!shared)
            {
                await table.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // The post-open core of ListNativeScanFilesAsync, callable against an ALREADY-OPEN table's snapshot —
    // the clustered-OPTIMIZE rewrite lists against the SAME snapshot its commit pins (expectedVersion), so
    // a writer landing between two separate opens can't produce a spurious conflict. `snap` is passed
    // alongside `table` rather than defaulted from it precisely for that: it may be an AT-version snapshot.
    private static async Task<NativeScanList> BuildNativeScanListAsync(
        EngineeredWood.IO.ITableFileSystem fs, string path, DeltaTable table,
        EngineeredWood.DeltaLake.Snapshot.Snapshot snap,
        Predicate? prune, ILogger log, EngineeredWood.DeltaLake.Schema.StructType? schemaOverride)
    {
        // add.Path is the spec's URL-ENCODED table-relative path; decode it to the on-disk name.
        static string FileUri(string root, string addPath) =>
            root + "/" + EngineeredWood.DeltaLake.DeltaPath.Decode(addPath).Replace('\\', '/').TrimStart('/');
        {
            // schemaOverride: a buffered transaction's PENDING (ALTERed) schema — presence handling, mapping
            // maps and pruning key off it so a pending-added column reads as typed NULL from every committed
            // file (the same machinery as committed schema evolution; no stats => pruning stays superset-safe).
            var schemaForMaps = schemaOverride ?? snap.Schema;
            var root = ToReadableRoot(path);
            // Planning — the path-sorted GLOBAL ordinal and the Delta-log file pruning — belongs to
            // engineered-wood: the ordinal it hands back is the same one its own row-id encoder uses and its
            // DML paths DECODE, so `(Ordinal << 40) | file_row_number` cannot drift from what the library
            // means by a rowid. (We used to re-sort the active set and drive DeltaFilePruner by hand here,
            // which agreed only by inspection.) schemaOverride rides through as the prune schema so a
            // buffered transaction's PENDING (ALTERed) column names resolve; unresolvable => file kept.
            var planned = table.PlanFiles(prune, snap, schemaOverride);
            var dvReader = new DeletionVectorReader(fs);
            var files = new List<NativeScanFile>();
            int pruned = snap.ActiveFiles.Count - planned.Count;
            // AnyUri is PRE-prune BY CONTRACT — it is what the schema probe falls back to when every file
            // pruned away, exactly the case where `planned` is empty. So take it from the active set, not
            // from the plan. Path-sorted minimum rather than first-enumerated to keep it deterministic
            // (it is the same file the old hand-rolled sort picked), in one pass instead of a second sort.
            string? minPath = null;
            foreach (var add in snap.ActiveFiles.Values)
            {
                if (minPath is null || string.CompareOrdinal(add.Path, minPath) < 0)
                    minPath = add.Path;
            }
            string? anyUri = minPath is null ? null : FileUri(root, minPath);
            foreach (var (ordinal, add) in planned)
            {
                var uri = FileUri(root, add.Path);
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
                path, snap.Version, snap.ActiveFiles.Count, files.Count, pruned, mode);
            return new NativeScanList
            {
                Version = snap.Version,
                PartitionColumns = snap.Metadata.PartitionColumns, Files = files, AnyUri = anyUri,
                LogicalToPhysical = logicalToPhysical, LogicalToFieldId = logicalToFieldId,
                MappedSchema = mappedSchema, TableSchema = schemaForMaps,
            };
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
            var (_, fsName, under) = AdlsGen2TableFileSystem.ParseAbfss(p);
            return "onelake://" + fsName + "/" + under;
        }
        return p;
    }

    /// <summary>Opens the Delta table at <paramref name="path"/> and returns its Arrow schema only (no data
    /// read). Used at table-function bind. <paramref name="opener"/> = the calling operator's ClientContext.</summary>
    public static Schema GetSchema(nint opener, string path, DeltaTableBinding? bound = null)
        => GetSchemaAsync(opener, path, bound).GetAwaiter().GetResult();

    private static async Task<Schema> GetSchemaAsync(nint opener, string path, DeltaTableBinding? bound)
    {
        var (open, shared) = await OpenForReadAsync(opener, path, bound).ConfigureAwait(false);
        try
        {
            // Variant fields cross the C ABI in the ew.variant_transport LEAF-binary transport form (EW
            // master advertises the canonical VariantType) — align the bind schema with the batches.
            return VariantMarker.ToTransportSchema(open.Table.ArrowSchema);
        }
        finally
        {
            if (!shared) { await open.Table.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>Like <see cref="GetSchema"/> but also reports whether <c>delta.enableRowTracking</c> is set —
    /// in the SAME table open, so the catalog's column fetch can cache the flag for the (immediately
    /// following) virtual-columns metadata fetch without a second <c>_delta_log</c> read (OneLake cost).</summary>
    public static Schema GetSchemaAndRowTracking(nint opener, string path, out bool rowTracking,
                                                 DeltaTableBinding? bound = null)
    {
        var (schema, rt) = GetSchemaAndRowTrackingAsync(opener, path, bound).GetAwaiter().GetResult();
        rowTracking = rt;
        return schema;
    }

    /// <summary>
    /// Whether a Delta table EXISTS at <paramref name="path"/> — by the same definition the engine uses
    /// (<c>_delta_log</c> holds at least one commit), so the two cannot drift apart.
    ///
    /// <para>Deliberately NOT "can it be opened": a table whose log has a hole, whose credential expired, or
    /// whose store is briefly unreachable EXISTS and must not be reported as absent. The host converts
    /// absence into "this table is gone" — dropping the catalog entry and removing the name from
    /// enumeration — so a wrong `true` here makes a table with intact data VANISH.</para>
    ///
    /// <para>Intended for the FAILURE path only. It costs a log listing, which is exactly the per-table cost
    /// the OneLake enumeration work exists to avoid, so never call it to pre-check a fetch that is about to
    /// happen anyway — call it after one has already failed, to classify why.</para>
    /// </summary>
    public static bool TableExists(nint opener, string path)
        => TableExistsAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<bool> TableExistsAsync(nint opener, string path)
    {
        try
        {
            var fs = TableFileSystems.Create(opener, path);
            var log = new EngineeredWood.DeltaLake.Log.TransactionLog(fs);
            return await log.GetLatestVersionAsync().ConfigureAwait(false) >= 0;
        }
        catch (System.Exception)
        {
            // Could not even list the log. That is "unknown", and UNKNOWN IS NOT ABSENCE — answer "exists"
            // so the caller keeps the original failure rather than erasing a table it cannot see.
            return true;
        }
    }

    private static async Task<(Schema Schema, bool RowTracking)> GetSchemaAndRowTrackingAsync(
        nint opener, string path, DeltaTableBinding? bound)
    {
        var (open, shared) = await OpenForReadAsync(opener, path, bound).ConfigureAwait(false);
        try
        {
            var cfg = open.Table.CurrentSnapshot.Metadata.Configuration;
            bool rowTracking = cfg is not null
                && cfg.TryGetValue("delta.enableRowTracking", out var v)
                && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
            return (VariantMarker.ToTransportSchema(open.Table.ArrowSchema), rowTracking);
        }
        finally
        {
            if (!shared) { await open.Table.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>Like <see cref="GetSchema"/> but also reports the VERSION it read (the latest at this moment) —
    /// from the SAME table open, so a scan can pin that version for every later reference to the table in the
    /// same statement at <b>zero extra IO</b> (the alternative, <see cref="ResolveVersionAsOf"/>, costs its own
    /// <c>_delta_log</c> open). Same reasoning as <see cref="GetSchemaAndRowTracking"/>: the value is already in
    /// hand, so asking for it separately would be a second read of the log we just replayed.</summary>
    public static Schema GetSchemaAndVersion(nint opener, string path, out long version,
                                             DeltaTableBinding? bound = null)
    {
        var (schema, v) = GetSchemaAndVersionAsync(opener, path, bound).GetAwaiter().GetResult();
        version = v;
        return schema;
    }

    private static async Task<(Schema Schema, long Version)> GetSchemaAndVersionAsync(
        nint opener, string path, DeltaTableBinding? bound)
    {
        var (open, shared) = await OpenForReadAsync(opener, path, bound).ConfigureAwait(false);
        try
        {
            return (VariantMarker.ToTransportSchema(open.Table.ArrowSchema), open.Table.CurrentSnapshot.Version);
        }
        finally
        {
            if (!shared) { await open.Table.DisposeAsync().ConfigureAwait(false); }
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
        // Row-group pruning (bloom-refined) + the Decimal128 widening ride the shared read options.
        var options = DeltaWriter.Options() with { ParquetReadOptions = DeltaWriter.ReadOptions(filter) };
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
                var mapped = nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
                // Canonical VariantArray -> the ew.variant_transport leaf blob the C ABI carries. EW emits
                // canonical now (VariantColumnCoercion, UNPATCHED); flattening at the boundary is ours.
                yield return VariantTransport.ToTransport(mapped);
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renames engineered-wood's trailing transient-address column to the name DuckDB binds.
    /// </summary>
    /// <remarks>
    /// EW calls it <see cref="TransientRowAddress.ColumnName"/> (<c>_ew_row_address</c>) — deliberately, since
    /// it is a snapshot-scoped ADDRESS, not Spark's stable <c>_metadata.row_id</c>; the two were the SAME
    /// string until upstream separated them. DuckDB binds our virtual rowid under
    /// <see cref="DeltaCatalog.RowIdColumn"/>, and <c>arrow_ingest</c> maps the returned columns to the
    /// requested projection BY NAME, so the rename has to happen before the batch crosses the ABI — a
    /// mismatch here does not fail loudly, it makes the rowid column simply not resolve.
    /// </remarks>
    private static RecordBatch RenameRowAddressToDuckDbRowId(RecordBatch batch)
    {
        int idx = -1;
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            if (batch.Schema.FieldsList[i].Name == TransientRowAddress.ColumnName) { idx = i; break; }
        }
        if (idx < 0)
            return batch; // already renamed, or the caller did not ask for the address column

        var fields = new List<Field>(batch.ColumnCount);
        var columns = new IArrowArray[batch.ColumnCount];
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            var f = batch.Schema.FieldsList[i];
            fields.Add(i == idx
                ? new Field(DeltaCatalog.RowIdColumn, f.DataType, f.IsNullable, f.Metadata)
                : f);
            columns[i] = batch.Column(i);
        }
        var sb = new Apache.Arrow.Schema.Builder();
        foreach (var f in fields)
            sb.Field(f);
        return new RecordBatch(sb.Build(), columns, batch.Length);
    }

    /// <summary>Like <see cref="Stream"/> but each batch carries a trailing non-null Int64
    /// <c>_metadata.row_id</c> column (the TRANSIENT (file, position) address, renamed from
    /// engineered-wood's <c>_ew_row_address</c> by <see cref="RenameRowAddressToDuckDbRowId"/>) — used to
    /// surface the DuckDB rowid for UPDATE/DELETE.</summary>
    public static IAsyncEnumerable<RecordBatch> StreamWithRowIds(
        nint opener, string path, IReadOnlyList<string>? columns, Predicate? filter, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        // Row-group pruning (bloom-refined) + the Decimal128 widening ride the shared read options.
        var options = DeltaWriter.Options() with { ParquetReadOptions = DeltaWriter.ReadOptions(filter) };
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
            await foreach (var raw in table.ReadAsync(
                new DeltaReadOptions
                {
                    Columns = columns, Filter = filter, Metadata = DeltaRowMetadata.RowAddress,
                }, token).ConfigureAwait(false))
            {
                var batch = RenameRowAddressToDuckDbRowId(raw);
                var mapped = nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
                yield return VariantTransport.ToTransport(mapped); // canonical -> ew.variant_transport blob
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the rows whose transient <c>_metadata.row_id</c> is in <paramref name="rowIds"/>
    /// (deletion vectors). Returns the number of rows deleted.</summary>
    /// <param name="spec">The catalog's resolved write tuning (compression / row-group size / bloom columns).
    /// ⚠ REQUIRED for correctness of the user's configuration, not decoration: a rewrite that omits it writes
    /// the file at engineered-wood's defaults, so a table accumulates MIXED settings — measured before this was
    /// threaded (CTAS zstd, rewrite snappy). See the write-options entry in CLAUDE.md.</param>
    public static long DeleteByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                      CancellationToken ct, bool nativeWrite = false, bool nativeRead = false,
                                      DeltaWriteSpec? spec = null)
        => DeleteByRowIdsAsync(opener, path, rowIds, ct, nativeWrite, nativeRead, spec).GetAwaiter().GetResult();

    private static async Task<long> DeleteByRowIdsAsync(nint opener, string path, IReadOnlyCollection<long> rowIds,
                                      CancellationToken ct, bool nativeWrite, bool nativeRead,
                                      DeltaWriteSpec? spec = null)
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
            ? new NativeParquetDataFileWriter(path, spec)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(spec, dataFileWriter: writer,
                                                                 dataFileReader: fileReader), token)
            .ConfigureAwait(false);
        try
        {
            long deleted = (await table.DeleteRowsAsync(
                    SelectionFromRowIds(table, table.CurrentSnapshot, rowIds, "copy-on-write DELETE"),
                    RowDeleteMode.CopyOnWrite, cancellationToken: token)
                .ConfigureAwait(false)).RowsDeleted;
            DmlLog.LogInformation("delta delete-rewrite {Path}: deleted={Deleted} writer={Writer}",
                path, deleted, writer is null ? "engineered-wood" : "native-duckdb");
            MemoryProbe.Mark("delta delete: copy-on-write rewrite done", deleted);
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

    /// <summary>
    /// The same decision against an ALREADY-READ configuration, so a caller that needs several properties
    /// pays for ONE table open instead of one per property (each of these helpers otherwise opens the table,
    /// which on OneLake/S3 is a <c>_delta_log</c> LIST). The parse stays here, in one place.
    /// </summary>
    public static bool IsDeletionVectorsEnabled(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("delta.enableDeletionVectors", out var v)
           && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsDeletionVectorsEnabledAsync(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
    /// UPDATE post-images then bake each row's ORIGINAL stable id into that column.
    /// <para><paramref name="AllFilesRowTracked"/> = every ACTIVE file at <paramref name="Version"/> carries a
    /// <c>baseRowId</c>, so a read-back of any of them resolves a stable id for every row. It is what lets a
    /// buffered UPDATE decide the statement-wide all-or-nothing id rule BEFORE reading, which is the
    /// precondition for writing its post-images in groups instead of accumulating them
    /// (see <c>UpdateGroupBytes</c>). ⚠ It describes <paramref name="Version"/> and nothing else — a consumer
    /// reading back at a DIFFERENT pinned version must not trust it, because the file set differs.</para></summary>
    public readonly record struct TxnDmlProfile(
        bool DvEnabled, bool CdfEnabled, bool SupportsExternalCommit, long Version, bool Partitioned,
        bool MaterializeRowIds, bool AllFilesRowTracked);

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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
            bool allTracked = true;
            foreach (var add in table.CurrentSnapshot.ActiveFiles.Values)
            {
                if (add.BaseRowId is null) { allTracked = false; break; }
            }
            return new TxnDmlProfile(dv, cdf, table.SupportsExternalDataFileCommit,
                                     table.CurrentSnapshot.Version,
                                     table.CurrentSnapshot.Metadata.PartitionColumns.Count > 0,
                                     matIds, allTracked);
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
        => GetTableConfigAll(opener, path) is { } cfg && cfg.TryGetValue(key, out var v) ? v : null;

    /// <summary>Reads a table's WHOLE configuration map in ONE open — null when the TABLE is absent (an
    /// append-shaped implicit create has nothing to read). ⚠ Prefer this over repeated
    /// <see cref="GetTableConfig"/> calls: each open is a <c>_delta_log</c> LIST, cheap locally and NOT on
    /// OneLake/S3, so asking for two keys separately doubles a remote round trip. The catalog reads it once
    /// per table path and caches (see <c>_tableConfigCache</c>).</summary>
    public static IReadOnlyDictionary<string, string>? GetTableConfigAll(nint opener, string path)
        => GetTableConfigAllAsync(opener, path).GetAwaiter().GetResult();

    private static async Task<IReadOnlyDictionary<string, string>?> GetTableConfigAllAsync(nint opener, string path)
    {
        try
        {
            var fs = TableFileSystems.Create(opener, path);
            await using var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
            return table.CurrentSnapshot.Metadata.Configuration;
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
    /// <param name="nativeRead">The CATALOG's engine choice. ⚠ This was hardcoded off, so a buffered UPDATE's
    /// read-back took the engineered-wood CODEC reader even on a <c>PROVIDER 'delta'</c> catalog whose
    /// <c>native_read</c> is on — the wrong engine for what the ATTACH asked for. It also changes BATCHING: the
    /// codec reader yields ONE BATCH PER ROW GROUP where <c>read_parquet</c> yields 2048-row vectors, which is
    /// what made the UPDATE grouped flush inert on this path (measured 30 flushes vs 1 on a 60k-row
    /// UPDATE).</param>
    public static IEnumerable<RecordBatch> ReadRowsByRowIds(
        nint opener, string path, IReadOnlyCollection<long> rowIds, CancellationToken ct,
        long? atVersion = null,
        List<(long?[] Ids, long?[] Versions)>? sourceTrackingOut = null,
        List<long[]>? rowIdsOut = null,
        bool nativeRead = false)
        => BlockingEnumerable(ReadRowsByRowIdsAsync(opener, path, rowIds, atVersion, sourceTrackingOut,
                                                    rowIdsOut, nativeRead, ct));

    private static async IAsyncEnumerable<RecordBatch> ReadRowsByRowIdsAsync(
        nint opener, string path, IReadOnlyCollection<long> rowIds,
        long? atVersion, List<(long?[] Ids, long?[] Versions)>? sourceTrackingOut,
        List<long[]>? rowIdsOut, bool nativeRead = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Cancel a slow buffered-UPDATE read-back of the matched rows over OneLake/S3 on interrupt.
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        var readBackReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        await using var table = await DeltaTable
            .OpenAsync(fs, DeltaWriter.Options(dataFileReader: readBackReader), token).ConfigureAwait(false);
        // The correlation key crosses as EW's `_ew_row_address` METADATA COLUMN (DeltaRowMetadata.RowAddress
        // — the same flag, names and semantics as its other reads), and is UNPACKED here into the
        // out-param our callers want. Two reasons the adaptation lives at this seam rather than in them:
        // the yielded batches keep carrying USER COLUMNS ONLY, which the buffered-UPDATE consumer depends on
        // (it indexes columns positionally against the pending schema and reconciles against it, so a
        // trailing metadata column would shift every index), and a plain long[] means no caller manages an
        // Arrow buffer lifetime. Not requested ⇒ not asked for, so there is nothing to strip.
        // The rowids' ordinals are path-sort positions in the snapshot they were SCANNED against (a buffered
        // transaction's pinned version), so resolve there — against a moved CurrentSnapshot a concurrent
        // commuting append shifts the ordering and the ordinals name the wrong files.
        var snapshot = atVersion is { } v && v != table.CurrentSnapshot.Version
            ? await table.GetSnapshotAtVersionAsync(v, token).ConfigureAwait(false)
            : table.CurrentSnapshot;
        // BOTH identities now arrive as COLUMNS, in one pass: the address under RowAddress and the stable
        // id/commit version under RowTracking (EW's `sourceRowTrackingOut` out-param was retired upstream —
        // the two surfaces DISAGREED about failure, and the column form is the one that complains).
        // ⚠ Only ask for RowTracking when a caller wants it: on a table WITHOUT row tracking the ask is
        // REFUSED (where the out-param quietly returned all-nulls), and that refusal is correct — it is a
        // configuration mistake worth hearing about. Our one caller allocates sourceTrackingOut only under
        // TxnDmlProfile.MaterializeRowIds, i.e. only when the table declares the materialized columns, so
        // the two conditions line up. Keep them lined up.
        await foreach (var batch in ReadSelectedRowsAsync(table, snapshot,
                           SelectionFromRowIds(table, snapshot, rowIds, "row read-back",
                                               skipUnresolvable: true),
                           sourceTrackingOut, rowIdsOut, token).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The scoped read-back itself, on an ALREADY-OPEN table and an already-built selection: reads only the
    /// selected rows, resolving their addresses against <paramref name="snapshot"/>, and yields batches
    /// carrying USER COLUMNS ONLY with the requested identities lifted into the out-params.
    /// </summary>
    /// <remarks>
    /// Shared by the buffered-UPDATE read-back (which decodes rowids first) and the autocommit merge-on-read
    /// UPDATE (which already holds the selection it is about to stage). Both correlate by the returned rowid,
    /// never by position — batching and deletion-vector filtering each break positional correspondence.
    /// <para>⚠ <paramref name="sourceTrackingOut"/> is what asks for <see cref="DeltaRowMetadata.RowTracking"/>,
    /// and that ask is REFUSED on a table without row tracking. Allocate it only when the table declares the
    /// materialized columns; the refusal is correct, and silence there would be the configuration mistake.</para>
    /// </remarks>
    private static async IAsyncEnumerable<RecordBatch> ReadSelectedRowsAsync(
        DeltaTable table, EngineeredWood.DeltaLake.Snapshot.Snapshot snapshot, RowSelection selection,
        List<(long?[] Ids, long?[] Versions)>? sourceTrackingOut,
        List<long[]>? rowIdsOut,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var metadata = (rowIdsOut is null ? DeltaRowMetadata.None : DeltaRowMetadata.RowAddress)
                       | (sourceTrackingOut is null ? DeltaRowMetadata.None : DeltaRowMetadata.RowTracking);
        await foreach (var batch in table.ReadRowsAsync(
                           selection,
                           new DeltaRowReadOptions { Metadata = metadata, ResolveAgainst = snapshot },
                           ct)
                           .ConfigureAwait(false))
        {
            // Transport form, like every other read exit: the buffered-UPDATE consumer substitutes SET values
            // that arrive from DuckDB as ew.variant_transport blobs (DeltaCatalog keeps the marker on them),
            // so a canonical read-back column would not match its replacement.
            if (metadata == DeltaRowMetadata.None)
            {
                yield return VariantTransport.ToTransport(batch);
                continue;
            }
            yield return VariantTransport.ToTransport(StripMetadata(batch, rowIdsOut, sourceTrackingOut));
        }
    }

    /// <summary>
    /// Takes EW's appended metadata columns off a read-back batch — the transient address into
    /// <paramref name="rowIdsOut"/>, the stable id/commit version into <paramref name="sourceTrackingOut"/>
    /// — and returns the batch carrying USER COLUMNS ONLY. Every column is located BY NAME, not by position:
    /// a metadata column's placement is EW's contract to change, and stripping the wrong one would silently
    /// drop a user column and shift the rest.
    /// <para>Stripping is not cosmetic. The buffered-UPDATE consumer indexes columns POSITIONALLY against
    /// the pending schema, so a trailing metadata column shifts every index; and since #50 EW REFUSES a
    /// write whose batch carries a column the table does not declare, forwarding one as a post-image would
    /// now be an error rather than the silent extra parquet column it used to be.</para>
    /// </summary>
    private static RecordBatch StripMetadata(
        RecordBatch batch, List<long[]>? rowIdsOut,
        List<(long?[] Ids, long?[] Versions)>? sourceTrackingOut)
    {
        var drop = new HashSet<int>();

        if (rowIdsOut is not null)
        {
            var addresses = (Apache.Arrow.Int64Array)batch.Column(
                RequireMetadataColumn(batch, TransientRowAddress.ColumnName, "RowAddress", drop));
            var rids = new long[batch.Length];
            for (int i = 0; i < batch.Length; i++)
                rids[i] = addresses.GetValue(i)!.Value;
            rowIdsOut.Add(rids);
        }

        if (sourceTrackingOut is not null)
        {
            const string prefix = EngineeredWood.DeltaLake.Table.DeltaMetadataColumns.DefaultPrefix;
            var ids = (Apache.Arrow.Int64Array)batch.Column(RequireMetadataColumn(
                batch, prefix + EngineeredWood.DeltaLake.Table.DeltaMetadataColumns.RowIdSuffix,
                "RowTracking", drop));
            var vers = (Apache.Arrow.Int64Array)batch.Column(RequireMetadataColumn(
                batch, prefix + EngineeredWood.DeltaLake.Table.DeltaMetadataColumns.RowCommitVersionSuffix,
                "RowTracking", drop));
            // Nullable on purpose: a file predating row tracking on the table carries no baseRowId, so its
            // rows have no derivable id. The consumer treats a null as "materialisation off for the whole
            // statement" rather than inventing one, which would change row identity silently.
            var idVals = new long?[batch.Length];
            var verVals = new long?[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                idVals[i] = ids.IsNull(i) ? null : ids.GetValue(i);
                verVals[i] = vers.IsNull(i) ? null : vers.GetValue(i);
            }
            sourceTrackingOut.Add((idVals, verVals));
        }

        var fields = new List<Field>(batch.ColumnCount - drop.Count);
        var columns = new List<IArrowArray>(batch.ColumnCount - drop.Count);
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            if (drop.Contains(c))
                continue;
            fields.Add(batch.Schema.FieldsList[c]);
            columns.Add(batch.Column(c));
        }
        return new RecordBatch(new Apache.Arrow.Schema(fields, batch.Schema.Metadata), columns, batch.Length);
    }

    /// <summary>Resolves a requested metadata column by name and marks it for removal. Absent means the read
    /// surface changed under us — we asked for the flag, so the column is owed.</summary>
    private static int RequireMetadataColumn(RecordBatch batch, string name, string flag, HashSet<int> drop)
    {
        int idx = batch.Schema.GetFieldIndex(name);
        if (idx < 0)
        {
            throw new System.InvalidOperationException(
                $"row read-back: the '{name}' metadata column is missing — it was requested via "
                + $"DeltaRowMetadata.{flag}, so its absence means the read surface changed.");
        }
        drop.Add(idx);
        return idx;
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
            long deleted = (await table.DeleteRowsAsync(
                    SelectionFromRowIds(table, table.CurrentSnapshot, rowIds, "deletion-vector DELETE"),
                    RowDeleteMode.DeletionVector, rowLevelRetry, token)
                .ConfigureAwait(false)).RowsDeleted;
            // The no-boxing DML floor, and therefore the control to compare an UPDATE against: this path
            // carries ROWIDS ONLY across the seam. Measured 204 MB for 1M rows where the equivalent
            // one-column UPDATE took 454 MB.
            MemoryProbe.Mark("delta delete: deletion vector committed", deleted);
            return deleted;
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

    /// <summary>
    /// The table's live row count, summed from the LOG: every <c>add</c> carries <c>stats.numRecords</c> and
    /// every deletion vector carries its own <c>cardinality</c>, so this opens no data file and reads no
    /// deletion-vector file. <paramref name="unit"/>/<paramref name="value"/> time-travel exactly as
    /// <see cref="GetSchemaAt"/> does (null = latest).
    /// </summary>
    /// <returns>
    /// The count, or <b>null</b> when it cannot be established. Null is ALL-OR-NOTHING: one active file whose
    /// writer recorded no <c>numRecords</c> makes the whole answer unknown, rather than a silent under-count.
    /// That is Delta's own rule for its log-answered <c>count(*)</c>, and the reason to follow it here is that
    /// the consumer is the PLANNER — an estimate that is wrong by an unknown amount steers join ordering worse
    /// than no estimate at all, which DuckDB already handles (a null cardinality is its normal "unknown").
    /// </returns>
    /// <remarks>
    /// ⚠ EXACT, despite the <c>ApproximateRowCount</c> surface it feeds: the Delta log is the authority on
    /// which rows are live, so this is the same arithmetic <c>DeltaNativeReader.LiveRowCount</c> uses to
    /// SYNTHESIZE rows for a partition-only scan — measured against ground truth there on live Fabric tables
    /// (89 files ⇒ 659,278; a 200-file table with deletion vectors ⇒ 9,968). What is trusted is
    /// <c>numRecords</c>, the writer's declared count; the DV term is a cardinality the descriptor states.
    /// <para>⚠ It costs a snapshot, which on remote storage is the log replay (seconds on OneLake) — and it is
    /// free in practice because the host fetches stats LAZILY from <c>BuildScanFunction</c>, i.e. only for a
    /// table about to be scanned, whose open the transaction's shared cache then serves. It is NOT called
    /// during catalog enumeration.</para>
    /// </remarks>
    public static long? GetRowCount(nint opener, string path, string? unit, string? value,
                                    DeltaTableBinding? bound = null)
        => GetRowCountAsync(opener, path, unit, value, bound).GetAwaiter().GetResult();

    private static async Task<long?> GetRowCountAsync(nint opener, string path, string? unit, string? value,
                                                      DeltaTableBinding? bound)
    {
        var (open, shared) = await OpenForReadAsync(opener, path, bound).ConfigureAwait(false);
        try
        {
            var snap = unit is not null && value is not null
                ? await ResolveSnapshotAsync(open.Table, unit, value, default).ConfigureAwait(false)
                : open.Table.CurrentSnapshot;
            long total = 0;
            foreach (var add in snap.ActiveFiles.Values)
            {
                if (add.GetNumRecords() is not { } records)
                {
                    return null;   // one undeclared file ⇒ the table's count is unknown, not approximate
                }
                // The DV states its own cardinality, so the count never needs the vector's POSITIONS — which
                // is what keeps this free of per-DV-file IO.
                total += records - (add.DeletionVector?.Cardinality ?? 0);
            }
            return total >= 0 ? total : null;
        }
        finally
        {
            if (!shared) { await open.Table.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>Time travel — the Arrow schema of the table AS OF a version/timestamp (the schema can differ from
    /// the latest, e.g. before an ADD COLUMN). <paramref name="unit"/> is "version" or "timestamp" (the DuckDB
    /// <c>AT</c> clause unit); <paramref name="value"/> is the BIGINT version or a parseable timestamp.</summary>
    public static Schema GetSchemaAt(nint opener, string path, string unit, string value,
                                     DeltaTableBinding? bound = null)
        => GetSchemaAtAsync(opener, path, unit, value, bound).GetAwaiter().GetResult();

    private static async Task<Schema> GetSchemaAtAsync(nint opener, string path, string unit, string value,
                                                       DeltaTableBinding? bound)
    {
        var (open, shared) = await OpenForReadAsync(opener, path, bound).ConfigureAwait(false);
        try
        {
            var snap = await ResolveSnapshotAsync(open.Table, unit, value, default).ConfigureAwait(false);
            return VariantMarker.ToTransportSchema(snap.ArrowSchema);
        }
        finally
        {
            if (!shared) { await open.Table.DisposeAsync().ConfigureAwait(false); }
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
        // Row-group pruning (bloom-refined) + the Decimal128 widening ride the shared read options.
        var options = DeltaWriter.Options() with { ParquetReadOptions = DeltaWriter.ReadOptions(filter) };
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
                var mapped = nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
                yield return VariantTransport.ToTransport(mapped);
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).ConfigureAwait(false);
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
            await foreach (var batch in table.ReadChangesAsync(
                new DeltaChangeReadOptions { StartVersion = fromVersion, EndVersion = end }, ct)
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

    /// <summary>
    /// The Change Data Feed with TYPED bounds (the <c>delta.changes</c> surface): version bounds pass through
    /// untouched; a TIMESTAMP bound resolves against the commit history — <paramref name="fromTs"/> = the FIRST
    /// version committed AT OR AFTER that instant (Delta's <c>startingTimestamp</c>), <paramref name="toTs"/> =
    /// the LAST version committed AT OR BEFORE it (<c>endingTimestamp</c>, the as-of rule). A timestamp bound
    /// past the applicable end of the history yields an EMPTY feed (deterministic, and safe under concurrent
    /// commits) rather than an error. Mutual-exclusion validation is the function surface's job; this assumes a
    /// resolvable starting bound exists.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately NOT built on <see cref="ResolveVersionAsOf"/>: that resolver serves snapshot PINNING,
    /// where "cannot resolve ⇒ fall back to latest" is the right degradation — for a FEED BOUND the same
    /// fallback would silently change which rows return. A commit with no timestamp at all refuses timestamp
    /// bounds outright, naming the version-bound spelling as the way out.
    /// </remarks>
    public static IArrowArrayStream GetChangesBounded(
        nint opener, string path, long? from, long? to, DateTime? fromTs, DateTime? toTs)
    {
        long toV = to ?? -1;
        if (fromTs is null && toTs is null)
        {
            return GetChanges(opener, path, from ?? 0, toV);
        }
        var history = CollectVersionTimes(opener, path).GetAwaiter().GetResult();
        long fromV;
        if (fromTs is { } f)
        {
            long? first = null;
            foreach (var (v, ts) in history)
            {
                if (ts is null)
                {
                    throw new InvalidOperationException(
                        $"delta.changes: version {v} of this table carries no commit timestamp, so timestamp "
                        + "bounds cannot be resolved — use version bounds (starting_version := / ending_version :=).");
                }
                if (ts >= f && (first is null || v < first))
                {
                    first = v;
                }
            }
            if (first is null)
            {
                // from_ts is after the last commit: nothing has happened at-or-after it.
                return new InMemoryArrayStream(EmptyChangeSchema(opener, path), System.Array.Empty<RecordBatch>());
            }
            fromV = first.Value;
        }
        else
        {
            fromV = from!.Value;
        }
        if (toTs is { } t)
        {
            long? last = null;
            foreach (var (v, ts) in history)
            {
                if (ts is null)
                {
                    throw new InvalidOperationException(
                        $"delta.changes: version {v} of this table carries no commit timestamp, so timestamp "
                        + "bounds cannot be resolved — use version bounds (starting_version := / ending_version :=).");
                }
                if (ts <= t && (last is null || v > last))
                {
                    last = v;
                }
            }
            if (last is null)
            {
                // to_ts is before the first commit: nothing existed at-or-before it.
                return new InMemoryArrayStream(EmptyChangeSchema(opener, path), System.Array.Empty<RecordBatch>());
            }
            toV = last.Value;
        }
        if (toV >= 0 && fromV > toV)
        {
            return new InMemoryArrayStream(EmptyChangeSchema(opener, path), System.Array.Empty<RecordBatch>());
        }
        return GetChanges(opener, path, fromV, toV);
    }

    private static async Task<List<(long Version, DateTime? Ts)>> CollectVersionTimes(nint opener, string path)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
        var rows = new List<(long, DateTime?)>();
        try
        {
            await foreach (var h in table.GetHistoryAsync().ConfigureAwait(false))
            {
                rows.Add((h.Version, h.TimestampMs is { } ms
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : null));
            }
        }
        finally
        {
            await table.DisposeAsync().ConfigureAwait(false);
        }
        return rows;
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
        // Row-group pruning (bloom-refined) + the Decimal128 widening ride the shared read options.
        var options = DeltaWriter.Options() with { ParquetReadOptions = DeltaWriter.ReadOptions(filter) };
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
            await foreach (var raw in table.ReadAsync(
                new DeltaReadOptions
                {
                    AtVersion = snap.Version, Columns = columns, Filter = filter,
                    Metadata = DeltaRowMetadata.RowAddress,
                }, token).ConfigureAwait(false))
            {
                var batch = RenameRowAddressToDuckDbRowId(raw);
                var mapped = nested is null
                    ? batch
                    : ArrowColumnMappingRename.RenameBatch(batch, nested, toPhysical: false);
                yield return VariantTransport.ToTransport(mapped); // canonical -> ew.variant_transport blob
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
            // ⚠ THE REQUESTED VERSION IS USUALLY THE ONE WE ALREADY HOLD, and rebuilding it is a WHOLE second
            // log replay. Every caller here opened the table first, and `OpenAsync` builds at LATEST; the
            // snapshot pin is SEEDED from exactly such an open, so on the ordinary read path
            // (`ScanNative`/`ScanCodec` pin at latest, then every later reference reads AT that pin) the two
            // versions are the SAME and the second build is pure waste.
            //
            // Provably equivalent rather than merely usually right: `SnapshotBuilder.BuildAsync` computes
            // `targetVersion = atVersion ?? listing.LatestVersion`, so with `atVersion == LatestVersion` the
            // two calls select the same checkpoint and replay the same commit range. A Delta version is
            // immutable, so a concurrent commit landing in between changes the fresh listing's LATEST but not
            // the replay up to v.
            //
            // ⚠ AND IT IS UPSTREAM'S OWN RULE, not an invention here: engineered-wood's `ResolveReadSnapshot`
            // is `options.AtVersion is { } v && v != CurrentSnapshot.Version ? null : CurrentSnapshot`. Our
            // `Stream*At` paths already got it for free by passing `AtVersion` into `ReadAsync`; this method
            // is the one place that duplicated the resolution WITHOUT it.
            //
            // MEASURED on a Fabric lakehouse table at v1850 (1851 commits, 18 checkpoints at interval 100):
            // an open at latest cost ~26 s and an "at v1850" open ~48 s — the pin was roughly DOUBLING the
            // cost of every open it exists to make consistent.
            if (table.CurrentSnapshot is { } current && current.Version == version)
            {
                return current;
            }
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
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options()).ConfigureAwait(false);
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
    /// <param name="spec">The catalog's resolved write tuning. ⚠ This is the site where omitting it hurt
    /// most: compaction rewrites the MAJORITY of a table's bytes, so it actively undid the setting it was
    /// configured for (measured: every pre-OPTIMIZE file zstd, the compacted output snappy).</param>
    public static long Optimize(nint opener, string path, CancellationToken ct, bool nativeWrite = false,
                                bool nativeRead = false, bool full = false, DeltaWriteSpec? spec = null)
        => OptimizeAsync(opener, path, ct, nativeWrite, nativeRead, full, spec).GetAwaiter().GetResult();

    private static async Task<long> OptimizeAsync(nint opener, string path, CancellationToken ct,
                                bool nativeWrite, bool nativeRead, bool full, DeltaWriteSpec? spec = null)
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
            ? new NativeParquetDataFileWriter(path, spec)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = await DeltaTable.OpenAsync(fs, DeltaWriter.Options(spec, dataFileWriter: writer, dataFileReader: fileReader), token)
            .ConfigureAwait(false);
        try
        {
            // A clustering-declared table (the delta.clustering domain, else the fabricator.sortedBy property)
            // RECLUSTERS instead of bin-packing when the native writer is available: ONE host query reads every
            // active file (DV rows excluded), globally re-orders — hilbert_index over ntile range-buckets for
            // 2+ keys (Spark's range_partition_id shape, type-agnostic), plain ORDER BY for one — and COPYs the
            // clustered file; ONE dataChange=false commit swaps the active set, tagging add.clusteringProvider.
            // DuckDB's spilling sort does the reorder, so the rewrite streams (data never crosses the C ABI).
            // Resolved UNCONDITIONALLY (metadata only, no IO) so a clustering-declared table that cannot be
            // reclustered says so. Both fallbacks below are otherwise SILENT — the user asked for clustering
            // and got bin-packing — and that matters more now that `delta` and `engineeredwooddelta` select
            // different writer defaults, so the writer-absent case is reachable by ATTACH spelling alone.
            var clusterCols = ResolveClusteringColumns(table);
            if (clusterCols is { Count: > 0 })
            {
                string cols = string.Join(",", clusterCols);
                if (writer is null)
                {
                    DmlLog.LogWarning(
                        "delta optimize {Path}: table declares clustering by [{Cols}] but the NATIVE WRITER is "
                        + "not enabled — bin-packing instead of reclustering. The recluster is one host query "
                        + "whose global ORDER BY relies on DuckDB's spilling sort (engineered-wood has no "
                        + "external sort), so it REQUIRES native_write.", path, cols);
                }
                else if (!ClusteredRewriteEligible(table))
                {
                    DmlLog.LogWarning(
                        "delta optimize {Path}: table declares clustering by [{Cols}] but its shape is not served "
                        + "by the clustered rewrite (identity/IcebergCompat, a variant column, or nested columns "
                        + "under column mapping) — bin-packing instead of reclustering.", path, cols);
                }
                else
                {
                    long? cv = await ClusteredRewriteAsync(fs, path, table, clusterCols, full, token)
                        .ConfigureAwait(false);
                    DmlLog.LogInformation("delta optimize {Path}: {Result} clustered by [{Cols}] full={Full}", path,
                        cv.HasValue ? $"reclustered → v{cv.Value}" : "nothing to recluster", cols, full);
                    return 0;
                }
            }
            var v = await table.CompactAsync(null, token).ConfigureAwait(false);
            DmlLog.LogInformation("delta optimize {Path}: {Result} writer={Writer}", path,
                v.HasValue ? $"compacted → v{v.Value}" : "nothing to compact",
                writer is null ? "engineered-wood" : "native-duckdb");
            MemoryProbe.Mark("delta optimize: compaction done");
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
        var listing = await BuildNativeScanListAsync(fs, path, table, snap, prune: null, DmlLog, schemaOverride: null)
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

    /// <summary>
    /// How many bytes of Arrow data an UPDATE's read-back may hold before its post-images are written out and
    /// the batches dropped. Overridable via <c>FABRICATOR_DELTA_UPDATE_GROUP_BYTES</c> — set it absurdly high
    /// to reproduce the pre-grouping behaviour, which is how the A/B for this was measured.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a pure MEMORY bound with no effect on file layout</b>, which is why it needs no size
    /// policy of its own and why it can be picked freely: <see cref="DeltaTable.WriteDataFilesAsync"/> writes
    /// ONE parquet file per (input batch × partition), so N read-back batches become N data files whether they
    /// arrive in one call or in a hundred. Do NOT reach for <c>delta.targetFileSize</c> here — it would read
    /// as if the grouping were sizing files, which it is not.</para>
    /// <para>The accounting deliberately DOUBLE-COUNTS a buffer shared between a pre-image and its post-image
    /// (an unchanged column is aliased, not copied), so the estimate errs high and the group flushes early.</para>
    /// <para><b>⚠ MEASURED, AND THE HEADLINE IS NOT THE ONE THIS WAS BUILT FOR (2026-08-06).</b> On the shape
    /// that favours it most — 600k rows × 16 VARCHAR columns, UPDATE every row, SET one column — the MANAGED
    /// HEAP peak falls <b>327 → 171 MB</b> (and is now bounded by the group rather than by the statement, which
    /// is the durable property), while the PROCESS peak working set falls only <b>614 → 548 MB</b>. Time is
    /// flat (9.3 s → 9.6 s; 71 flushes is as fast as 5, so flush count costs nothing measurable). On a NARROW
    /// table the grouping does not fire at all: 1M rows × 3 columns accumulates only ~50 MB of read-back, well
    /// under the threshold, and peak is identical either way (449 MB).</para>
    /// <para><b>⚠ So the UPDATE's dominant memory term is NOT this, and a caller must not read the grouping as
    /// "UPDATE memory is fixed".</b> Instrumenting the working set through the path put <b>~180 MB already
    /// spent before the read-back begins</b> (1M × 3 columns: 253 MB) — that is DuckDB's own side of the
    /// statement plus, on our side, <c>DeltaCatalog.ExecuteUpdate</c>'s <c>Dictionary&lt;long, object?[]&gt;</c>
    /// of BOXED SET values, the Arrow batch rebuilt from it, and <c>updRowByRid</c>. All three scale with
    /// matched rows and are complete before any provider work starts. Fixing THAT means keeping the SET values
    /// in Arrow form per input chunk instead of boxing them — a change to the DML seam, not to this file.</para>
    /// <para>64 MiB rather than 16 MiB (which measured marginally better, 152 MB heap) because the BUFFERED
    /// path's per-group write opens the table, i.e. one <c>_delta_log</c> LIST per group — cheap locally,
    /// not on OneLake/S3. 5 flushes buys nearly all of the 16-MiB benefit at a quarter of the opens.</para>
    /// </remarks>
    internal static readonly long UpdateGroupBytes = ResolveEnvBytes(
        "FABRICATOR_DELTA_UPDATE_GROUP_BYTES", 64L * 1024 * 1024);

    private static long ResolveEnvBytes(string name, long fallback)
        => long.TryParse(System.Environment.GetEnvironmentVariable(name),
                         NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0
            ? v
            : fallback;

    /// <summary>
    /// A batch's approximate in-memory footprint: every buffer of every array, all nesting levels. Used only
    /// to decide when an accumulating group is big enough to write, so an over-estimate is harmless (it
    /// flushes sooner) — shared buffers are counted once per array that references them.
    /// </summary>
    internal static long ApproxBatchBytes(RecordBatch batch)
    {
        long total = 0;
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            total += ApproxArrayBytes(batch.Column(c).Data);
        }
        return total;
    }

    private static long ApproxArrayBytes(ArrayData? data)
    {
        if (data is null)
        {
            return 0;
        }
        long total = 0;
        foreach (var buf in data.Buffers)
        {
            total += buf.Length;
        }
        if (data.Children is not null)
        {
            foreach (var child in data.Children)
            {
                total += ApproxArrayBytes(child);
            }
        }
        total += ApproxArrayBytes(data.Dictionary);
        return total;
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

    /// <summary>Per-file copy-on-write UPDATE from a host-side join result (EW master's host-join shape,
    /// pr4-to-master guide §1): <paramref name="updates"/> carries one row per changed rowid — the transient
    /// <c>_metadata.row_id</c> column plus one column per SET column (LOGICAL table-column names, matching
    /// Arrow types). EW rewrites only the files containing a matched row, substituting the SET columns keyed
    /// by rowid (type-agnostic; structs included) — no substitution code host-side. Opens with the standard
    /// write options (path_in_schema).</summary>
    /// <param name="catalogSerializable">The catalog's ATTACH <c>isolation_level</c> default, used ONLY when the
    /// table declares no <c>delta.isolationLevel</c> of its own. Passed rather than read here so the merge-on-read
    /// path costs no extra <c>_delta_log</c> read: it resolves the precedence against the configuration it
    /// already has in hand (<see cref="EffectiveSerializable"/>).</param>
    /// <param name="spec">The catalog's resolved write tuning — the merge-on-read POST-IMAGE file and the
    /// copy-on-write rewrite are both written here, and both came out at engineered-wood's defaults before this
    /// was threaded.</param>
    public static void UpdateByRowIds(nint opener, string path, RecordBatch updates, CancellationToken ct,
        bool nativeWrite = false, bool nativeRead = false, bool catalogSerializable = false,
        DeltaWriteSpec? spec = null)
        => UpdateByRowIdsAsync(opener, path, updates, ct, nativeWrite, nativeRead, catalogSerializable, spec)
            .GetAwaiter().GetResult();

    /// <summary>
    /// The effective isolation for a table: its own <c>delta.isolationLevel</c> property WINS, and
    /// <paramref name="catalogDefault"/> (the ATTACH <c>isolation_level</c>) applies only when the table
    /// declares nothing. The ONE expression of that precedence — <c>DeltaCatalog</c> delegates here — because a
    /// table that has DECLARED a level must not be weakened by a local attach-time option, whichever path
    /// happens to be asking.
    /// </summary>
    internal static bool EffectiveSerializable(
        IReadOnlyDictionary<string, string> config, bool catalogDefault)
        => config.TryGetValue("delta.isolationLevel", out var lvl)
            ? lvl.Replace("_", "").Equals("serializable", System.StringComparison.OrdinalIgnoreCase)
            : catalogDefault;

    private static async Task UpdateByRowIdsAsync(nint opener, string path, RecordBatch updates,
        CancellationToken ct, bool nativeWrite, bool nativeRead, bool catalogSerializable,
        DeltaWriteSpec? spec = null)
    {
        using var interrupt = new InterruptScope(opener, ct);
        var token = interrupt.Token;
        var fs = TableFileSystems.Create(opener, path);
        // native_write => DuckDB's parquet writer produces the rewritten file (bloom/stats/footer);
        // native_read routes the affected-file read through read_parquet (the IDataFileReader seam). EW
        // master owns the rewrite semantics — the former IDataFileRewriter SQL-join substitution was dropped
        // upstream, and its row-tracking id projection is obsolete (master preserves ids through rewrites).
        // NOTE (EW master): the CoW UPDATE has no row-level retry (the fork's rowLevelRetry rebase is
        // gone) — a concurrent commit aborts with DeltaConflictException → the retry-the-statement error.
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path, spec)
            : null;
        var fileReader = nativeRead && NativeParquetDataFileReader.Available
            ? new NativeParquetDataFileReader(path)
            : null;
        var table = await DeltaTable.OpenAsync(fs,
                DeltaWriter.Options(spec, dataFileWriter: writer, dataFileReader: fileReader), token)
            .ConfigureAwait(false);
        try
        {
            // MERGE-ON-READ on deletion-vector tables (the fork's UpdateViaVectorsAsync, now COMPOSED from
            // master's primitives per the pr4-to-master guide §2): DV-delete the old rows + append a small
            // post-image file + optional CDF pre/post-image capture, fused into ONE commit — no full-file
            // rewrite, and row-tracking ids are preserved via the materialized column. Copy-on-write remains
            // the path for DV-less tables (master's CoW preserves ids itself; it rejects CDF — a DV-less CDF
            // table's UPDATE surfaces that clean error).
            var cfg = table.CurrentSnapshot.Metadata.Configuration;
            bool mor = EngineeredWood.DeltaLake.DeletionVectors.DeletionVectorConfig.IsEnabled(cfg)
                       && !table.IsIcebergCompat;
            // One table open serving both: the DV mode above and the isolation level below come from the SAME
            // configuration read, so honouring the table's declaration costs no extra _delta_log LIST.
            bool serializable = EffectiveSerializable(cfg, catalogSerializable);
            if (mor)
            {
                await MergeOnReadUpdateAsync(table, updates, serializable, token).ConfigureAwait(false);
            }
            else
            {
                // Copy-on-write, addressed by (add.path, absolute position) rather than by the packed rowid:
                // we re-key the updates batch onto engineered-wood's `_metadata` struct, so nothing of ours
                // depends on its rowid UPDATE surface. The packing stays where it belongs — on the DuckDB side
                // of this method, because DuckDB's own rowid is a single BIGINT.
                // VARIANT: the re-key passes the SET value columns through unchanged, so it works in either
                // dialect — but what it RETURNS goes to engineered-wood, hence canonical.
                await table.UpdateRowsAsync(
                        VariantTransport.ToCanonical(ReKeyUpdatesOntoMetadata(table, updates)),
                        cancellationToken: token)
                    .ConfigureAwait(false);
            }
            DmlLog.LogInformation("delta update-rewrite {Path}: rowids={RowIds} mode={Mode} writer={Writer}",
                path, updates.Length, mor ? "merge-on-read" : "copy-on-write",
                writer is null ? "engineered-wood" : "native-duckdb");
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

    // The split is engineered-wood's, not ours: TransientRowAddress owns the encoding its
    // ReadAllWithRowIdsAsync emits, so we decode with its helpers rather than a copied literal.

    /// <summary>
    /// Replaces the transient-rowid column of a DuckDB UPDATE batch with engineered-wood's <c>_metadata</c>
    /// struct, so the copy-on-write UPDATE is addressed by <c>(add.path, absolute position)</c> instead of by a
    /// packed 64-bit id. The SET columns pass through untouched.
    /// </summary>
    /// <remarks>
    /// Only the two LOCATOR members are filled — this addresses rows physically, and engineered-wood ignores the
    /// identity members here (it resolves each row's own stable id from the file it rewrites). The packing is
    /// decoded on THIS side because it exists to satisfy DuckDB, whose <c>rowid</c> is a single BIGINT; it is not
    /// something a Delta library should have to know about.
    /// </remarks>
    private static RecordBatch ReKeyUpdatesOntoMetadata(DeltaTable table, RecordBatch updates)
    {
        int ridIdx = -1;
        for (int c = 0; c < updates.ColumnCount; c++)
        {
            if (updates.Schema.FieldsList[c].Name == DeltaCatalog.RowIdColumn) { ridIdx = c; break; }
        }
        if (ridIdx < 0 || updates.Column(ridIdx) is not Int64Array rids)
        {
            throw new System.InvalidOperationException(
                $"copy-on-write UPDATE: updates batch has no '{DeltaCatalog.RowIdColumn}' column.");
        }

        var snapshot = table.CurrentSnapshot;
        var pathByOrdinal = new Dictionary<int, string>(snapshot.ActiveFiles.Count);
        foreach (var planned in table.PlanFiles(snapshot: snapshot))
        {
            pathByOrdinal[planned.FileOrdinal] = planned.File.Path;
        }

        long posMask = (1L << TransientRowAddress.PositionBits) - 1;
        var pathB = new StringArray.Builder();
        var idxB = new Int64Array.Builder();
        for (int i = 0; i < updates.Length; i++)
        {
            long rid = rids.GetValue(i)!.Value;
            int ordinal = (int)(TransientRowAddress.FileOrdinal(rid));
            if (!pathByOrdinal.TryGetValue(ordinal, out var filePath))
            {
                throw new System.InvalidOperationException(
                    $"copy-on-write UPDATE: row-id file ordinal {ordinal} does not name an active file of "
                    + $"version {snapshot.Version} ({pathByOrdinal.Count} active) — the row identifiers were "
                    + "captured against a different snapshot.");
            }
            pathB.Append(filePath);
            idxB.Append(rid & posMask);
        }

        // The two LOCATOR columns, flat and dot-named — EW's `_metadata.*` convention, shared with the
        // identity pair ReadAllWithRowTrackingAsync owns. We fill only the locator: EW resolves each row's own
        // stable id from the file it rewrites, so supplying ids would assert identity we do not own.
        var fields = new List<Field>(updates.ColumnCount + 1);
        var arrays = new List<IArrowArray>(updates.ColumnCount + 1);
        for (int c = 0; c < updates.ColumnCount; c++)
        {
            if (c == ridIdx)
            {
                fields.Add(new Field(
                    RowSelection.DefaultMetadataPrefix + RowSelection.FilePathColumnSuffix, Apache.Arrow.Types.StringType.Default, false));
                arrays.Add(pathB.Build());
                fields.Add(new Field(
                    RowSelection.DefaultMetadataPrefix + RowSelection.RowIndexColumnSuffix, Apache.Arrow.Types.Int64Type.Default, false));
                arrays.Add(idxB.Build());
            }
            else
            {
                fields.Add(updates.Schema.FieldsList[c]);
                arrays.Add(updates.Column(c));
            }
        }
        return new RecordBatch(new Apache.Arrow.Schema(fields, null), arrays, updates.Length);
    }

    /// <summary>
    /// Decodes transient rowids into the self-describing <c>(add.path -&gt; absolute positions)</c> key
    /// engineered-wood's DML entry points prefer. The ordinal is OUR encoding, so WE own the decode;
    /// <see cref="DeltaTable.PlanFiles"/> supplies the ordering, and it is the SAME planner that minted these
    /// ordinals — natively in <c>BuildNativeScanListAsync</c>, and on the codec path inside EW's own
    /// <c>ReadWithTransientRowIdsAsync</c>. Called UNFILTERED on purpose: the ordinal indexes the unfiltered
    /// path-sorted active set, so an unfiltered plan is a superset of whatever a filtered scan emitted.
    /// An ordinal that names no active file is a LOUD error — engineered-wood's ordinal-keyed forms would
    /// merely skip it, which is indistinguishable from a file with nothing to change.
    /// </summary>
    /// <param name="skipUnresolvable">Drop an ordinal that names no active file instead of reporting it.
    /// ONLY for the read-back of committed row CONTENT, whose caller may legitimately pass rows of THIS
    /// transaction's own pending files — those are in no committed snapshot by construction, and the caller
    /// rejects or reroutes them itself right after. Every DML path leaves this false: there an unresolvable
    /// ordinal means a delete or update silently going missing.</param>
    private static RowSelection SelectionFromRowIds(
        DeltaTable table, EngineeredWood.DeltaLake.Snapshot.Snapshot snapshot,
        IReadOnlyCollection<long> rowIds, string op, bool skipUnresolvable = false)
    {
        long posMask = (1L << TransientRowAddress.PositionBits) - 1;
        var pathByOrdinal = new Dictionary<int, string>(snapshot.ActiveFiles.Count);
        foreach (var planned in table.PlanFiles(snapshot: snapshot))
        {
            pathByOrdinal[planned.FileOrdinal] = planned.File.Path;
        }
        var byFile = new Dictionary<string, IReadOnlyCollection<long>>(System.StringComparer.Ordinal);
        foreach (long rid in rowIds)
        {
            int ordinal = (int)(TransientRowAddress.FileOrdinal(rid));
            if (!pathByOrdinal.TryGetValue(ordinal, out var filePath))
            {
                if (skipUnresolvable)
                {
                    continue;
                }
                throw new System.InvalidOperationException(
                    $"{op}: row-id file ordinal {ordinal} does not name an active file of version "
                    + $"{snapshot.Version} ({pathByOrdinal.Count} active) — the row identifiers were captured "
                    + "against a different snapshot.");
            }
            if (!byFile.TryGetValue(filePath, out var set))
            {
                byFile[filePath] = set = new HashSet<long>();
            }
            ((HashSet<long>)set).Add(rid & posMask);
        }
        return RowSelection.ByPath(byFile);
    }

    /// <summary>
    /// Merge-on-read UPDATE: DV-mask the matched rows and append their post-images as small new file(s), one
    /// atomic commit, no data rewrite — the cheap shape for a small update against a large file.
    /// </summary>
    /// <remarks>
    /// <para><b>Composed from engineered-wood's PUBLIC primitives</b>: <see cref="DeltaTable.ReadRowsAsync"/>
    /// for the selection-scoped read-back, <see cref="DeltaTable.WriteDataFilesAsync"/> for the post-images
    /// (carrying each row's ORIGINAL stable id through <c>materializedRowIds</c>), and a
    /// <see cref="DeltaTransaction"/> to fuse the deletion-vector mask, the append and the CDF pair into ONE
    /// version. This is the SAME composition the buffered/explicit path already performs; autocommit used to
    /// call a bespoke engineered-wood entry point instead, and that divergence is what this removes.</para>
    /// <para><b>Three things the transaction buys that the previous shape could not.</b> It committed through
    /// <c>CommitDataFilesAsync(expectedVersion:)</c> — a bare compare-and-set, so ANY concurrent commit failed
    /// the statement even when it touched nothing we did; the OCC retry loop is disabled outright whenever
    /// <c>expectedVersion</c> is set; and no per-file deletion-vector edits were recorded, so there was no
    /// row-level reconciliation on this path AT ALL. Staging the mask through
    /// <see cref="DeltaTransaction.StageRowDeletesAsync"/> records those edits.
    /// <para>⚠ That is a MECHANISM claim, not a measured one, and the distinction matters. It rests on this
    /// path now making the SAME staging calls the buffered path makes — which
    /// <c>verify_delta_row_level_concurrency</c> §3/§5/§8 do cover — NOT on an observation of two autocommit
    /// UPDATEs composing. Such an observation is out of reach in-process (sqllogictest runs its connections
    /// sequentially, so a bare autocommit statement has no window between its scan and its commit; that suite's
    /// closing note says so) and was ALSO out of reach on the Windows local root it was attempted on, because
    /// that substrate has no put-if-absent at all: <c>fabricator_fs_write_probe</c> reports
    /// <c>EXCLUSIVE_CREATE</c> succeeding on an existing file AND <c>MoveFile</c> overwriting its target, and a
    /// 6-writer INSERT control on it lost 500 of 900 rows with every writer exiting 0. Measuring the gain needs
    /// a substrate whose commit is genuinely conditional (OneLake/abfss, S3 with a NAMED secret, POSIX local) —
    /// harness in <c>scratchpad/mor_update_race.sh</c>, which is an A/B against the pre-change build precisely
    /// so that a green leg cannot be mistaken for a measurement.</para></para>
    /// <para><b>The isolation level is the TABLE's</b>, resolved by the caller. A merge-on-read UPDATE on a
    /// table declaring <c>Serializable</c> must not be quietly relaxed to write-serializable just because that
    /// is engineered-wood's <see cref="DeltaTable.StartTransaction(IsolationLevel)"/> default — the same
    /// contract rule that the autocommit DELETE path already follows.</para>
    /// <para>What remains genuinely ours: decoding DuckDB's packed rowid, and substituting the SET values a
    /// host-side join produced. Correlation is by ROWID on BOTH sides — never by emission order, since
    /// batching and deletion-vector filtering each break positional correspondence.</para>
    /// </remarks>
    private static async Task MergeOnReadUpdateAsync(
        DeltaTable table, RecordBatch updates, bool serializable, CancellationToken token)
    {
        int ridIdx = -1;
        for (int c = 0; c < updates.ColumnCount; c++)
        {
            if (updates.Schema.FieldsList[c].Name
                == DeltaCatalog.RowIdColumn)
            {
                ridIdx = c;
                break;
            }
        }
        if (ridIdx < 0 || updates.Column(ridIdx) is not Int64Array updRids)
        {
            throw new System.InvalidOperationException("merge-on-read UPDATE: updates batch has no rowid column.");
        }

        var snapshot = table.CurrentSnapshot;
        var cfg = snapshot.Metadata.Configuration;

        // rowid -> the row of `updates` holding that row's new values. The rowid is the correlation key on
        // BOTH sides: the read-back hands it back per row (DeltaRowMetadata.RowAddress), so nothing here
        // depends on the order rows are emitted in.
        var updRowByRid = new Dictionary<long, int>(updates.Length);
        for (int i = 0; i < updates.Length; i++)
        {
            if (!updRids.IsNull(i)) { updRowByRid[updRids.GetValue(i)!.Value] = i; }
        }
        if (updRowByRid.Count == 0)
        {
            return;
        }

        // The rows to mask — and the SAME object the transaction stages below, so the positions the read
        // resolved and the positions the commit validates cannot drift apart.
        MemoryProbe.Mark("delta update mor: rowid map built", updRowByRid.Count);
        var selection = SelectionFromRowIds(table, snapshot, updRowByRid.Keys, "merge-on-read UPDATE");

        var setCols = new Dictionary<string, IArrowArray>(System.StringComparer.Ordinal);
        for (int c = 0; c < updates.ColumnCount; c++)
        {
            if (c != ridIdx) { setCols[updates.Schema.FieldsList[c].Name] = updates.Column(c); }
        }

        // Materialized row tracking: the post-images must carry their ORIGINAL stable ids, so a row's identity
        // survives the UPDATE (Spark's reference behaviour). ⚠ Ask for RowTracking ONLY when the table declares
        // the materialized columns — on a table without row tracking the ask is REFUSED, and correctly so.
        var (matRowId, matRowVer) = EngineeredWood.DeltaLake.RowTracking.RowTrackingConfig
            .TryGetMaterializedColumnNames(cfg);
        bool materialize = EngineeredWood.DeltaLake.RowTracking.RowTrackingConfig.IsEnabled(cfg)
                           && matRowId is not null && matRowVer is not null;
        bool cdfEnabled = EngineeredWood.DeltaLake.ChangeDataFeed.CdfConfig.IsEnabled(cfg);

        // ── GROUPED FLUSH: the read-back streams, it is not accumulated ────────────────────────────────
        // A group of read-back batches becomes data files (and, on a CDF table, its change files) as soon as
        // it is big enough, and only the returned WrittenDataFile metadata is kept. Peak memory therefore
        // tracks the GROUP rather than the statement — it was ~474 MB for a 1M-row UPDATE, which is the whole
        // reason this shape exists. Still exactly ONE commit.
        //
        // ⚠ FILE LAYOUT IS UNCHANGED BY CONSTRUCTION, which is what makes the grouping free rather than a
        // trade: WriteDataFilesAsync writes one parquet file per (input batch × partition), so N read-back
        // batches yield N data files whether they arrive in one call or in G calls. See UpdateGroupBytes.
        //
        // ⚠ The transaction is created BEFORE the read-back — it HAS to be, because StageChangeDataAsync is a
        // method on it and each group's CDF pair is staged as that group is written. It is still based on the
        // PINNED snapshot, so the commit's validation is unchanged; the only difference is that it stays open
        // across the read-back, holding a pin it already held. (Nothing re-opens the table in between, so
        // WriteDataFilesAsync' own CurrentSnapshot read is the same snapshot for every group.)
        await using var txn = table.StartTransaction(
            snapshot, serializable ? IsolationLevel.Serializable : IsolationLevel.WriteSerializable);
        txn.Operation = "UPDATE";

        // ⚠ THE ALL-OR-NOTHING ROW-ID RULE NOW HAS TO BE DECIDED UP FRONT, BECAUSE A GROUP IS WRITTEN BEFORE
        // THE LATER GROUPS' IDS ARE KNOWN — and the rule is statement-wide (a partially materialised statement
        // would leave identity depending on which rows happened to resolve). The read-back yields a null id
        // only for a row whose file carries no baseRowId AND no materialized value; a writer that materializes
        // ids also stamps baseRowId (the spec requires one on every `add` of a row-tracking table), so
        // "every selected file has a baseRowId" is the same condition, and it is a dictionary lookup per
        // selected path with no extra IO.
        // Where it can NOT be established the group threshold is DISABLED, so that statement buffers whole and
        // behaves exactly as it did before — a legacy shape keeps its old behaviour instead of acquiring new
        // semantics from a memory fix.
        bool idsPreResolved = materialize && AllSelectedFilesRowTracked(snapshot, selection);
        long groupBytes = !materialize || idsPreResolved ? UpdateGroupBytes : long.MaxValue;

        var preGroup = cdfEnabled ? new List<RecordBatch>() : null;
        var postGroup = new List<RecordBatch>();
        // Flat across the GROUP's batches, in emission order — that is how WriteDataFilesAsync consumes it.
        var idGroup = materialize ? new List<long?>() : null;
        var written = new List<WrittenDataFile>();
        long groupAccum = 0;
        long matched = 0;
        bool idsUnresolvable = false;
        bool wroteWithIds = false;

        async System.Threading.Tasks.Task FlushGroupAsync()
        {
            if (postGroup.Count == 0)
            {
                return;
            }
            // VARIANT: everything below hands batches to engineered-wood (the data files and the CDF pair), so
            // canonicalise here. The images were built from a TRANSPORT read-back plus transport SET values,
            // which is why ReadSelectedRowsAsync must stay in the transport dialect — the two have to agree
            // before the substitution, and only the EW-facing side converts.
            var ewPost = VariantTransport.ToCanonical(postGroup);
            var ewPre = preGroup is null ? null : VariantTransport.ToCanonical(preGroup);

            List<long?>? ids = null;
            if (idGroup is not null && !idsUnresolvable)
            {
                ids = new List<long?>(idGroup);
                wroteWithIds = true;
            }
            else if (wroteWithIds)
            {
                // Unreachable unless the spec invariant the up-front decision rests on is violated (a file
                // carrying materialized ids but no baseRowId). Loud, because the alternative is a statement
                // whose earlier rows kept their identity and whose later rows silently did not.
                throw new System.InvalidOperationException(
                    "merge-on-read UPDATE: a row-id became unresolvable after post-images had already been "
                    + "written with their original ids — the selection's files disagree about row tracking.");
            }

            // Post-images become data files NOW (invisible until the commit references them). An abort
            // therefore leaves them as orphans for VACUUM — the same eager-write contract the buffered path
            // documents.
            written.AddRange(await table.WriteDataFilesAsync(ewPost, token, materializedRowIds: ids)
                .ConfigureAwait(false));

            if (ewPre is not null)
            {
                // ⚠ rowIds/rowCommitVersions are deliberately NOT supplied, which leaves the staged change
                // rows with NULL ids on the feed. That is what BOTH previous paths did — the retired
                // engineered-wood entry point and the buffered one — so passing them here would be a behaviour
                // change riding along in a refactor whose gates exist to prove equivalence. Supplying them is
                // a real improvement on a row-tracking table (a `cdc` action has no baseRowId, so the change
                // file is the only place a change row's identity can live) and the ids are in hand as
                // `idGroup` — but it is its own change, with its own gate.
                // Pre-images then post-images WITHIN the group, so a single-group statement stages them in
                // exactly the order it always did.
                foreach (var pre in ewPre)
                {
                    await txn.StageChangeDataAsync(pre,
                            EngineeredWood.DeltaLake.ChangeDataFeed.CdfConfig.UpdatePreimage, token)
                        .ConfigureAwait(false);
                }
                foreach (var post in ewPost)
                {
                    await txn.StageChangeDataAsync(post,
                            EngineeredWood.DeltaLake.ChangeDataFeed.CdfConfig.UpdatePostimage, token)
                        .ConfigureAwait(false);
                }
            }

            // Drop the references — this is the point of the whole shape. Not Dispose(): an unchanged column
            // is ALIASED from the pre-image into the post-image, so disposing both would release one buffer
            // twice.
            postGroup.Clear();
            preGroup?.Clear();
            idGroup?.Clear();
            groupAccum = 0;
            // The mark that shows the grouping working: heap should be FLAT across these, not climbing.
            MemoryProbe.Mark("delta update mor: group flushed", matched);
        }

        // Scoped read-back of ONLY the selected rows, with their identities as columns. Not a whole-table
        // read: ReadRowsAsync resolves the selection against the pinned snapshot and touches just its files.
        // Both out-lists are drained per batch (their producer only ever appends, never reads them back), so
        // the rowids and source tracking do not accumulate across the statement either.
        var ridsPerBatch = new List<long[]>();
        var srcTracking = materialize ? new List<(long?[] Ids, long?[] Versions)>() : null;
        await foreach (var batch in ReadSelectedRowsAsync(table, snapshot, selection,
                           sourceTrackingOut: srcTracking, rowIdsOut: ridsPerBatch, ct: token)
                           .ConfigureAwait(false))
        {
            var rids = ridsPerBatch[ridsPerBatch.Count - 1];
            ridsPerBatch.Clear();
            var srcIds = srcTracking is { Count: > 0 } ? srcTracking[srcTracking.Count - 1].Ids : null;
            srcTracking?.Clear();

            // Row-aligned index into `updates` for each row of this batch, keyed by ROWID.
            var takeIdx = new List<int>(batch.Length);
            for (int i = 0; i < batch.Length; i++)
            {
                takeIdx.Add(i < rids.Length && updRowByRid.TryGetValue(rids[i], out int u) ? u : -1);
            }
            var columns = new IArrowArray[batch.ColumnCount];
            for (int c = 0; c < batch.ColumnCount; c++)
            {
                var f = batch.Schema.FieldsList[c];
                columns[c] = setCols.TryGetValue(f.Name, out var upd)
                    ? EngineeredWood.Arrow.ArrowCompute.Take(upd, takeIdx)
                    : batch.Column(c);
            }
            var post = new RecordBatch(batch.Schema, columns, batch.Length);
            preGroup?.Add(batch);
            postGroup.Add(post);
            matched += batch.Length;
            groupAccum += ApproxBatchBytes(batch) + ApproxBatchBytes(post);

            if (idGroup is not null)
            {
                for (int i = 0; i < batch.Length; i++)
                {
                    long? id = srcIds is not null && i < srcIds.Length ? srcIds[i] : null;
                    if (id is null) { idsUnresolvable = true; }
                    idGroup.Add(id);
                }
            }

            if (groupAccum >= groupBytes)
            {
                await FlushGroupAsync().ConfigureAwait(false);
            }
        }
        await FlushGroupAsync().ConfigureAwait(false);

        if (matched == 0)
        {
            // Nothing selected resolved to a row. Leaving via `await using` ABORTS the transaction, which has
            // staged nothing, so this is the same no-op the early return always was.
            return;
        }

        // ONE version: the deletion-vector mask, the post-image append and the CDF pair. Based on the PINNED
        // snapshot — the parameterless StartTransaction would make the commit's validation vacuous, since it
        // would ask what landed since the version it just read.
        await txn.StageRowDeletesAsync(selection, token).ConfigureAwait(false);
        await txn.StageDataFilesAsync(written, cancellationToken: token).ConfigureAwait(false);
        await txn.CommitAsync(token).ConfigureAwait(false);
        MemoryProbe.Mark("delta update mor: committed", matched);
    }

    /// <summary>
    /// Whether every data file the selection names carries a <c>baseRowId</c> — i.e. whether the read-back is
    /// guaranteed to resolve a stable id for every selected row, decidable WITHOUT reading anything.
    /// A path the snapshot does not know counts as untracked: unknown is not "yes".
    /// </summary>
    private static bool AllSelectedFilesRowTracked(
        EngineeredWood.DeltaLake.Snapshot.Snapshot snapshot, RowSelection selection)
    {
        foreach (var path in selection.Paths)
        {
            if (!snapshot.ActiveFiles.TryGetValue(path, out var add) || add.BaseRowId is null)
            {
                return false;
            }
        }
        return true;
    }
}
