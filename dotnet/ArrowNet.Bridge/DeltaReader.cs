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
                uris.Add(root + "/" + add.Path.Replace('\\', '/').TrimStart('/'));
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
    public sealed record NativeScanFile(int Ordinal, string Uri, long[] Dv);

    /// <summary>The result of <see cref="ListNativeScanFiles"/>: the resolved snapshot <see cref="Version"/>, the
    /// surviving (post-prune) <see cref="Files"/> in path-sorted global-ordinal order, and <see cref="AnyUri"/> =
    /// any active file's URI (pre-prune) for a schema probe when everything was pruned.</summary>
    public sealed class NativeScanList
    {
        public long Version { get; init; }
        public IReadOnlyList<NativeScanFile> Files { get; init; } = System.Array.Empty<NativeScanFile>();
        public string? AnyUri { get; init; }
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
        nint opener, string path, string? unit, string? value, Predicate? prune, ILogger log)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs).GetAwaiter().GetResult();
        try
        {
            var snap = unit is null
                ? table.CurrentSnapshot
                : ResolveSnapshotAsync(table, unit, value ?? "", default).AsTask().GetAwaiter().GetResult();
            var root = ToReadableRoot(path);
            // GLOBAL path-sorted ordinal over ALL active files (matches engineered-wood OrderedActiveFiles), then prune.
            var ordered = new List<AddFile>(snap.ActiveFiles.Values);
            ordered.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var pruner = prune is null ? null : new DeltaFilePruner(snap.Schema, snap.Metadata.PartitionColumns);
            var dvReader = new DeletionVectorReader(fs);
            var files = new List<NativeScanFile>();
            string? anyUri = null;
            int pruned = 0;
            for (int ordinal = 0; ordinal < ordered.Count; ordinal++)
            {
                var add = ordered[ordinal];
                var uri = root + "/" + add.Path.Replace('\\', '/').TrimStart('/');
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
                files.Add(new NativeScanFile(ordinal, uri, dv));
            }
            log.LogDebug("delta native list: {Path} v{Version} active={Active} scanned={Scanned} pruned={Pruned}",
                path, snap.Version, ordered.Count, files.Count, pruned);
            return new NativeScanList { Version = snap.Version, Files = files, AnyUri = anyUri };
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
                var uri = root + "/" + add.Path.Replace('\\', '/').TrimStart('/');
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
        var parquet = filter is null ? ParquetReadOptions.Default : new ParquetReadOptions { Filter = filter };
        var options = DeltaTableOptions.Default with { ParquetReadOptions = parquet };
        return StreamImpl(fs, options, columns, filter, ct);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamImpl(
        ITableFileSystem fs, DeltaTableOptions options, IReadOnlyList<string>? columns,
        Predicate? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var table = await DeltaTable.OpenAsync(fs, options, ct).ConfigureAwait(false);
        try
        {
            await foreach (var batch in table.ReadAllAsync(columns, filter, ct).ConfigureAwait(false))
            {
                yield return batch;
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
        var parquet = filter is null ? ParquetReadOptions.Default : new ParquetReadOptions { Filter = filter };
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
            await foreach (var batch in table.ReadAllWithRowIdsAsync(columns, filter, ct).ConfigureAwait(false))
            {
                yield return batch;
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
                                      CancellationToken ct, bool nativeWrite = false)
    {
        var fs = TableFileSystems.Create(opener, path);
        // Open with the standard WRITE options (OmitPathInSchema=false) so the copy-on-write rewrite emits
        // standard-readable parquet — DeltaTableOptions.Default would drop path_in_schema (TProtocolException).
        // native_write => DuckDB's parquet writer produces the rewritten survivor file (bloom/stats/footer);
        // engineered-wood still selects/reads the affected files and commits remove(old)+add(new).
        var writer = nativeWrite && NativeParquetDataFileWriter.Available
            ? new NativeParquetDataFileWriter(path)
            : null;
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(dataFileWriter: writer), ct)
            .AsTask().GetAwaiter().GetResult();
        try
        {
            return table.DeleteByRowIdsAsync(rowIds, ct).AsTask().GetAwaiter().GetResult().RowsDeleted;
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
        var parquet = filter is null ? ParquetReadOptions.Default : new ParquetReadOptions { Filter = filter };
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
            await foreach (var batch in table.ReadAtVersionAsync(snap.Version, columns, filter, ct)
                               .ConfigureAwait(false))
            {
                yield return batch;
            }
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
            await foreach (var batch in table.ReadChangesAsync(fromVersion, end, ct).ConfigureAwait(false))
            {
                yield return batch;
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
        var parquet = filter is null ? ParquetReadOptions.Default : new ParquetReadOptions { Filter = filter };
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
            await foreach (var batch in table.ReadAtVersionWithRowIdsAsync(snap.Version, columns, filter, ct)
                               .ConfigureAwait(false))
            {
                yield return batch;
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

    /// <summary>Per-file copy-on-write UPDATE: only files containing a target <paramref name="rowIds"/> are
    /// rewritten. <paramref name="rewriteFile"/> (ordinal, the file's batches) returns the same rows with the SET
    /// columns modified on matched positions (the caller owns the typed substitution); engineered-wood re-writes
    /// them as plain remove+add with a clean schema. Opens with the standard write options (path_in_schema).</summary>
    public static void UpdateByRowIds(nint opener, string path, IReadOnlyCollection<long> rowIds,
        System.Func<long, IReadOnlyList<RecordBatch>, IReadOnlyList<RecordBatch>> rewriteFile, CancellationToken ct)
    {
        var fs = TableFileSystems.Create(opener, path);
        var table = DeltaTable.OpenAsync(fs, DeltaWriter.Options(), ct).AsTask().GetAwaiter().GetResult();
        try
        {
            table.UpdateByRowIdsAsync(rowIds, rewriteFile, ct).AsTask().GetAwaiter().GetResult();
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
