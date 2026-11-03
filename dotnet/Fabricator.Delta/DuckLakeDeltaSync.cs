// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake;
using EngineeredWood.DeltaLake.Actions;
using EngineeredWood.DeltaLake.DeletionVectors;
using EngineeredWood.DeltaLake.Log;
using EngineeredWood.DeltaLake.Table;
using EngineeredWood.IO;
using Microsoft.Extensions.Logging;
using Array = System.Array;

namespace Fabricator.Bridge;

/// <summary>
/// <c>ducklake_delta_sync(lake [, table_name := 'schema.table'] [, column_mapping := 'name' | 'id' | 'none']
/// [, deletion_vectors := true | false] [, timestamp_mapping := 'utc' | 'ntz'] [, partitioning := true | false]
/// [, change_data_feed := false | true] [, metadata_schema := '…'])</c> — writes a
/// Delta log next to every DuckLake table's data files, so an engine that reads only Delta (Fabric's SQL
/// endpoint, Spark, Direct Lake) reads the table from the SAME Parquet files DuckLake wrote. DuckLake stays
/// the source of truth; the Delta log is derived from it and nothing else may write to it.
///
/// <para><b>How.</b> The structure (files, row ids, columns, snapshots) is read from the lake's metadata
/// catalog (<c>__ducklake_metadata_&lt;lake&gt;</c>), whatever database hosts it. Deleted rows are NOT read from
/// DuckLake's delete files — DuckLake has three forms of them (parquet position files with embedded snapshots,
/// puffin deletion vectors, deletes inlined into the metadata) — but asked of DuckLake itself: the positions a
/// scan at that snapshot still returns are the live ones. Commits are written with engineered-wood's
/// <see cref="TransactionLog"/> (put-if-absent), not through a <c>DeltaTransaction</c>, because the row ids
/// must be DuckLake's (<c>row_id_start</c> → <c>baseRowId</c>) and a transaction assigns its own.</para>
///
/// <para><b>One Delta version per COMPLETE DuckLake snapshot that touched the table</b>, each a STATE diff (the
/// Delta table is brought to DuckLake's state at that snapshot), so a snapshot that expired in between is simply
/// squashed into the next. A snapshot is complete when the table holds no live INLINED rows at it: those live in
/// the metadata database, not in any Parquet file, so a Delta version taken there would silently lack them. Such
/// a snapshot is skipped and the next complete one (usually the next flush) carries its changes — which also
/// keeps DuckLake's batching of small commits on the Delta side. With <c>deletion_vectors := false</c> a snapshot
/// must also have no live deletes (rewrite them away with <c>ducklake_rewrite_data_files</c>), for readers that
/// cannot read deletion vectors, e.g. SQL Server's DELTA reader. The first sync of a table is one commit at the
/// newest complete snapshot. Progress is the
/// <c>txn</c> action (<c>appId</c> <c>ducklake</c>, <c>version</c> = the DuckLake snapshot), so an interrupted
/// sync resumes from the log alone. See docs/ducklake-catalog.md §11.</para>
/// </summary>
public sealed class DuckLakeDeltaSyncFunction : ITableFunction
{
    public string Name => "ducklake_delta_sync";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("lake", StringType.Default, nullable: false),
        Params.Named("table_name", StringType.Default),
        Params.Named("column_mapping", StringType.Default),
        Params.Named("metadata_schema", StringType.Default),
        Params.Named("timestamp_mapping", StringType.Default),
        Params.Named("deletion_vectors", BooleanType.Default),
        Params.Named("partitioning", BooleanType.Default),
        Params.Named("change_data_feed", BooleanType.Default),
    }, null);

    internal static readonly Schema ResultSchema = new(new[]
    {
        new Field("schema_name", StringType.Default, nullable: false),
        new Field("table_name", StringType.Default, nullable: false),
        new Field("ducklake_snapshot", Int64Type.Default, nullable: false),
        new Field("delta_version", Int64Type.Default, nullable: false),
        new Field("files_added", Int64Type.Default, nullable: false),
        new Field("files_removed", Int64Type.Default, nullable: false),
        new Field("deletion_vectors", Int64Type.Default, nullable: false),
        new Field("checkpoint", BooleanType.Default, nullable: false),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var lake = FabricArgs.Str(args, 0)
            ?? throw new ArgumentException("ducklake_delta_sync: the lake (an attached DuckLake catalog) is required.");
        var table = FabricArgs.Str(args, 1);
        var mapping = (FabricArgs.Str(args, 2) ?? "name").Trim().ToLowerInvariant();
        if (mapping is not ("name" or "id" or "none"))
        {
            throw new ArgumentException(
                $"ducklake_delta_sync: column_mapping must be 'name', 'id' or 'none', not '{mapping}'. 'name' is what " +
                "Fabric's SQL endpoint reads; 'id' follows every DuckLake rename but Fabric silently skips such a table; " +
                "'none' declares no column mapping at all, for readers without it (SQL Server's DELTA reader).");
        }
        var timestamps = (FabricArgs.Str(args, 4) ?? "utc").Trim().ToLowerInvariant();
        if (timestamps is not ("ntz" or "utc"))
        {
            throw new ArgumentException(
                $"ducklake_delta_sync: timestamp_mapping must be 'ntz' or 'utc', not '{timestamps}'.");
        }
        bool deletionVectors = FabricArgs.Bool(args, 5) ?? true;
        bool partitioning = FabricArgs.Bool(args, 6) ?? true;
        bool changeDataFeed = FabricArgs.Bool(args, 7) ?? false;
        return new SyncBinding(lake, table, mapping, FabricArgs.Str(args, 3), timestamps, deletionVectors, partitioning,
            changeDataFeed);
    }

    private sealed class SyncBinding : ITableFunctionBinding
    {
        private readonly string _lake;
        private readonly string? _table;
        private readonly string _mapping;
        private readonly string? _metadataSchema;
        private readonly string _timestamps;
        private readonly bool _deletionVectors;
        private readonly bool _partitioning;
        private readonly bool _changeDataFeed;

        public SyncBinding(string lake, string? table, string mapping, string? metadataSchema, string timestamps,
            bool deletionVectors, bool partitioning, bool changeDataFeed)
        {
            _timestamps = timestamps;
            _deletionVectors = deletionVectors;
            _partitioning = partitioning;
            _changeDataFeed = changeDataFeed;
            _lake = lake;
            _table = table;
            _mapping = mapping;
            _metadataSchema = metadataSchema;
        }

        public Schema OutputSchema => ResultSchema;
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // The work runs NOW, while the opener (this call's ClientContext) is valid; a lazy body would run
            // inside get_next, after the call returned. Connection-free → the host-FS path.
            AmbientAdlsCredential.Current = null;
            var results = new DuckLakeDeltaSync(AmbientOpener.Current, _lake, _mapping, _timestamps, _deletionVectors,
                _partitioning, _changeDataFeed).Run(_table, _metadataSchema, ct);
            return Single(DuckLakeDeltaSync.ToBatch(results));
        }

        private static async IAsyncEnumerable<RecordBatch> Single(RecordBatch batch)
        {
            yield return batch;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose() { }
    }
}

internal sealed class DuckLakeDeltaSync
{
    private static readonly ILogger _log = FabricatorLog.CreateLogger("Fabricator.DuckLake");

    internal const string AppId = "ducklake";

    // DuckLake writes the original row id into this column when an UPDATE rewrites a row (measured); a file
    // without it derives the id from baseRowId + position. The commit-version column exists in no file, so a
    // reader always falls back to defaultRowCommitVersion — the version of the commit that added the file.
    internal const string MaterializedRowId = "_ducklake_internal_row_id";
    internal const string MaterializedRowCommitVersion = "_ducklake_internal_row_commit_version";

    private readonly nint _opener;
    private readonly string _lake;
    private readonly string _meta;
    private readonly string _mapping;
    private readonly string _timestamps;
    private readonly bool _deletionVectors;
    private readonly bool _partitioning;
    private readonly bool _changeDataFeed;
    private string _ms = "";

    // What a DuckDB TIMESTAMP (no time zone) is declared as. 'utc' (the DEFAULT, user decision 2026-10-27)
    // declares it a Delta timestamp, i.e. the same microseconds read as a UTC instant, because Fabric's SQL
    // endpoint REFUSES timestamp_ntz: the table appears without the column ("Columns of the specified data types
    // are not supported … TIMESTAMP_NTZ", measured 2026-10-27). 'ntz' is the spec's answer, for readers that
    // take it. Chosen at a table's first sync and persisted, since changing it later changes the column's type.
    internal const string TimestampKey = "fabricator.ducklake.timestampMapping";

    internal sealed record Result(
        string Schema, string Table, long Snapshot, long Version, long Added, long Removed, long Dvs, bool Checkpoint);

    // The DuckLake column ids the Delta table is partitioned by, comma-separated ("" for none). Chosen at a table's first
    // sync and fixed from then on, because Delta cannot change a table's partition columns without rewriting it. Ids, not
    // names: a rename changes the logical name in partitionColumns, not which column it is.
    internal const string PartitionKey = "fabricator.ducklake.partitionColumnIds";

    public DuckLakeDeltaSync(nint opener, string lake, string mapping, string timestamps, bool deletionVectors,
        bool partitioning = true, bool changeDataFeed = false)
    {
        _partitioning = partitioning;
        _changeDataFeed = changeDataFeed;
        _deletionVectors = deletionVectors;
        _opener = opener;
        _lake = lake;
        _meta = "__ducklake_metadata_" + lake;
        _mapping = mapping;
        _timestamps = timestamps;
    }

    /// <summary>A DuckLake table as the sync sees it: its schema, name, metadata id, uuid and data folder.</summary>
    internal sealed record LakeTable(string Schema, string Table, long Id, string Uuid, string Root);

    /// <summary>The lake's data_path (with a trailing separator), whether checkpoints are safe, the current
    /// snapshot and every live table with its data folder — what a sync and the maintenance both need.</summary>
    internal (string DataPath, bool CheckpointsSafe, long Current, List<LakeTable> Tables) Describe(string? metadataSchema)
    {
        // DuckLake attaches its metadata catalog HIDDEN — duckdb_databases()/duckdb_tables() do not list it — and
        // ducklake_settings() does not name METADATA_SCHEMA, so the schema is found by probing (measured).
        var candidates = metadataSchema is not null ? new[] { metadataSchema } : new[] { "main", "dbo", "public" };
        _ms = candidates.FirstOrDefault(c => Probe($"SELECT 1 FROM {Q(_meta)}.{Q(c)}.ducklake_snapshot LIMIT 0"))
            ?? throw new InvalidOperationException(
                $"ducklake_delta_sync: '{_lake}' is not an attached DuckLake catalog, or its metadata lives in a schema " +
                $"other than {string.Join("/", candidates)} (pass metadata_schema := '<METADATA_SCHEMA>').");

        long current = Long(Rows($"SELECT max(snapshot_id) FROM {M("ducklake_snapshot")}")[0][0]);
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows($"SELECT key, value FROM {M("ducklake_metadata")} WHERE scope IS NULL"))
        {
            global[(string)r[0]!] = (string?)r[1] ?? "";
        }
        string dataPath = global.TryGetValue("data_path", out var dp) ? dp : throw new InvalidOperationException(
            "ducklake_delta_sync: the lake has no data_path.");
        if (!dataPath.EndsWith("/", StringComparison.Ordinal) && !dataPath.EndsWith("\\", StringComparison.Ordinal))
        {
            dataPath += "/";
        }
        // DuckLake's orphan cleanup deletes every .parquet under data_path it does not know — a Delta checkpoint
        // included (measured, docs/ducklake-catalog.md §11.3) — unless the GLOBAL auto_compact is false. So a
        // checkpoint is written only when it cannot be deleted under us; otherwise the log stays JSON only.
        bool checkpointsSafe = global.TryGetValue("auto_compact", out var ac)
            && string.Equals(ac, "false", StringComparison.OrdinalIgnoreCase);

        var tables = Rows(
            $"SELECT t.table_id, t.table_uuid, t.table_name, t.path, t.path_is_relative, s.schema_name, s.path, s.path_is_relative " +
            $"FROM {M("ducklake_table")} t JOIN {M("ducklake_schema")} s ON s.schema_id = t.schema_id " +
            $"WHERE {Active("t", current)} AND {Active("s", current)} ORDER BY s.schema_name, t.table_name");

        var list = new List<LakeTable>();
        foreach (var t in tables)
        {
            string root = dataPath;
            root = Bool(t[7]) ? root + (string)t[6]! : (string)t[6]!;
            root = Bool(t[4]) ? root + (string)t[3]! : (string)t[3]!;
            list.Add(new LakeTable((string)t[5]!, (string)t[2]!, Long(t[0]), Str(t[1]), root.TrimEnd('/', '\\')));
        }
        return (dataPath, checkpointsSafe, current, list);
    }

    public List<Result> Run(string? tableFilter, string? metadataSchema, CancellationToken ct)
    {
        var (_, checkpointsSafe, current, tables) = Describe(metadataSchema);
        var results = new List<Result>();
        bool matched = false;
        foreach (var t in tables)
        {
            if (tableFilter is not null && !MatchesFilter(tableFilter, t.Schema, t.Table))
            {
                continue;
            }
            matched = true;
            var sync = new TableSync(this, t.Id, t.Uuid, t.Root, current, checkpointsSafe);
            results.AddRange(sync.Run(ct));
        }
        if (tableFilter is not null && !matched)
        {
            throw new InvalidOperationException($"ducklake_delta_sync: no table '{tableFilter}' in lake '{_lake}'.");
        }
        return results;
    }

    internal static bool MatchesFilter(string filter, string schema, string table)
    {
        int dot = filter.IndexOf('.');
        return dot < 0
            ? string.Equals(filter, table, StringComparison.OrdinalIgnoreCase)
            : string.Equals(filter.Substring(0, dot), schema, StringComparison.OrdinalIgnoreCase)
              && string.Equals(filter.Substring(dot + 1), table, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sync's own schema rules, for a table at snapshot <paramref name="s"/>: throws what the next sync would
    /// throw. <paramref name="columns"/> are ducklake_column rows in the sync's column order (column_id, begin_snapshot,
    /// end_snapshot, column_order, column_name, column_type, initial_default, nulls_allowed, parent_column);
    /// <paramref name="fileBegins"/> the begin_snapshot of every data file live at s.
    /// </summary>
    internal static void CheckSchema(string table, IEnumerable<object?[]> columns, IEnumerable<long> fileBegins, long s,
        string mode, string timestamps, bool deletionVectors)
    {
        var cols = columns.Select(r => new DlColumn
        {
            Id = Long(r[0]), Begin = Long(r[1]), End = NLong(r[2]), Order = Long(r[3]), Name = Str(r[4]),
            Type = Str(r[5]), InitialDefault = (string?)r[6], Nullable = r[7] is null || Bool(r[7]), Parent = NLong(r[8]),
        }).ToList();
        var files = fileBegins.Select(b => new DlFile { Begin = b }).ToList();
        // the constructor checks names and defaults, SchemaString the types (an unsupported one throws there)
        _ = new SchemaPlan(table, cols, files, s, mode, timestamps, deletionVectors).SchemaString();
        if (mode is "name" or "none")
        {
            // Stricter than the sync, which mirrors a rename until a new file is written: DuckLake then writes the new
            // name, while every compaction keeps the old one in the files it rewrites (measured 2026-11-02), so after a
            // rename with live data no further write could be mirrored. Refusing the rename itself leaves a way back.
            foreach (var c in cols.Where(c => c.ActiveAt(s)))
            {
                var old = files.Select(f => cols.FirstOrDefault(o => o.Id == c.Id && o.ActiveAt(f.Begin)))
                    .FirstOrDefault(o => o is not null && !string.Equals(o.Name, c.Name, StringComparison.Ordinal));
                if (old is not null)
                {
                    throw new InvalidOperationException(
                        $"ducklake_delta_sync: cannot mirror {table} to Delta: renaming column '{old.Name}' to '{c.Name}' while " +
                        $"data files hold it — DuckLake writes new files under the new name but keeps the old one in every file " +
                        $"its compaction rewrites, so in '{mode}' column mapping no later write could be mirrored. Rename before " +
                        "writing data, or recreate the table with the new name (CREATE TABLE … AS SELECT)");
                }
            }
        }
    }

    /// <summary>The marker table in the lake's metadata schema: which tables are mirrored, and how. A catalog that
    /// hosts the metadata (SQL Server) checks DuckLake's schema changes against it (DuckLakeMirrorGuard).</summary>
    internal const string MarkerTable = "fabricator_delta_mirror";

    // ── one table ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class DlFile
    {
        public long Id;
        public string RelPath = "";
        public string AbsPath = "";
        public long Records;
        public long Size;
        public long? RowIdStart;
        public long Begin;
        public long? PartitionId;

        // field id (= DuckLake column id) -> the name this file's Parquet schema gives it. Null when not read (the
        // catalog guard), and then the name is inferred from the metadata at Begin — which a rewrite makes wrong.
        public Dictionary<long, string>? Names;
    }

    private sealed class DlColumn
    {
        public long Id;
        public long Begin;
        public long? End;
        public long Order;
        public string Name = "";
        public string Type = "";
        public string? InitialDefault;
        public bool Nullable;
        public long? Parent;

        public bool ActiveAt(long s) => Begin <= s && (End is null || End > s);
    }

    private sealed class TableSync
    {
        private readonly DuckLakeDeltaSync _o;
        private readonly long _tableId;
        private readonly string _uuid;
        private readonly string _root;
        private readonly long _current;
        private readonly bool _checkpointsSafe;
        private readonly ITableFileSystem _fs;
        private List<DlColumn> _columns = new();

        public TableSync(DuckLakeDeltaSync o, long tableId, string uuid, string root, long current, bool checkpointsSafe)
        {
            _o = o;
            _tableId = tableId;
            _uuid = uuid;
            _root = root;
            _current = current;
            _checkpointsSafe = checkpointsSafe;
            _fs = CommitFileSystem(o._opener, root);
        }

        public List<Result> Run(CancellationToken ct)
        {
            _columns = _o.Rows(
                    $"SELECT column_id, begin_snapshot, end_snapshot, column_order, column_name, column_type, " +
                    $"initial_default, nulls_allowed, parent_column FROM {_o.M("ducklake_column")} WHERE table_id = {_tableId}")
                .Select(r => new DlColumn
                {
                    Id = Long(r[0]), Begin = Long(r[1]), End = NLong(r[2]), Order = Long(r[3]), Name = Str(r[4]),
                    Type = Str(r[5]), InitialDefault = (string?)r[6], Nullable = r[7] is null || Bool(r[7]), Parent = NLong(r[8]),
                })
                .ToList();

            // ⚠ NO COORDINATOR: several processes may sync the same table at once (each after its own DuckLake
            // commit). They meet at the commit file, which is put-if-absent — the loser gets a conflict, reads
            // the log again and continues from whatever the winner recorded. Every commit is a STATE diff against
            // the version it was computed on, so recomputing it is always safe; and a sync never writes an older
            // snapshot after a newer one, because each write targets exactly one version past what it read.
            var results = new List<Result>();
            // A lost race means another sync committed, so the log moved towards _current and the loop ends once it
            // gets there. Only a conflict that brings NO progress counts against the budget — counting every one made
            // a sync with more than 32 snapshots to write fail under contention (6 syncs, 36 versions: 13 of 15 runs).
            int stalled = 0;
            long synced = -1;
            while (true)
            {
                try
                {
                    SyncAll(results, ct);
                    EnsureMarker(Open(ct));
                    return results;
                }
                catch (DeltaConflictException) when (stalled < MaxConflictRetries)
                {
                    // ⚠ the deletion vectors written for the lost commit stay behind as unreferenced .bin
                    // files: engineered-wood owns the DV path scheme (DeletionVectorPath, internal), and DuckLake's
                    // orphan cleanup only knows .parquet/.puffin. Rare, and harmless to every reader.
                    long now = SyncedSnapshot(Open(ct));
                    stalled = now > synced ? 0 : stalled + 1;
                    synced = now;
                    _log.LogDebug("ducklake sync {Root}: lost a commit race (log at snapshot {Synced}, {Stalled} without " +
                        "progress), re-reading the log", _root, now, stalled);
                }
            }
        }

        // The column names every file's Parquet schema really carries, by field id (DuckLake writes column_id there).
        // Files never change, so each is read once per sync.
        private readonly Dictionary<long, Dictionary<long, string>> _fileNames = new();

        private void LoadFileNames(List<DlFile> files)
        {
            var missing = files.Where(f => !_fileNames.ContainsKey(f.Id)).ToList();
            if (missing.Count > 0)
            {
                var byPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in missing)
                {
                    byPath[f.AbsPath.Replace('\\', '/')] = f.Id;
                    _fileNames[f.Id] = new Dictionary<long, string>();
                }
                string list = "[" + string.Join(", ", missing.Select(f => Lit(f.AbsPath))) + "]";
                foreach (var r in _o.Rows($"SELECT file_name, field_id, name FROM parquet_schema({list}) WHERE field_id IS NOT NULL"))
                {
                    if (byPath.TryGetValue(Str(r[0]).Replace('\\', '/'), out long id))
                    {
                        _fileNames[id][Long(r[1])] = Str(r[2]);
                    }
                }
            }
            foreach (var f in files)
            {
                f.Names = _fileNames[f.Id];
            }
        }

        // Records the table and its mirror options in the metadata schema, so a catalog hosting the metadata can refuse a
        // schema change the next sync could not mirror — at the change, not one sync later. Idempotent; the options are
        // the ones the Delta table was created with (they are fixed from the first sync on).
        private void EnsureMarker(EngineeredWood.DeltaLake.Snapshot.Snapshot? state)
        {
            if (state is null)
            {
                return;
            }
            var cfg = state.Metadata.Configuration;
            string mode = cfg is not null && cfg.TryGetValue("delta.columnMapping.mode", out var m) ? m : "none";
            string ts = cfg is not null && cfg.TryGetValue(TimestampKey, out var t) ? t : "ntz";
            bool dv = cfg is not null && cfg.TryGetValue("delta.enableDeletionVectors", out var d)
                      && string.Equals(d, "true", StringComparison.OrdinalIgnoreCase);
            string marker = _o.M(MarkerTable);
            string id = _tableId.ToString(CultureInfo.InvariantCulture);
            _o.Rows($"CREATE TABLE IF NOT EXISTS {marker} (table_id BIGINT, column_mapping VARCHAR, timestamp_mapping VARCHAR, " +
                    "deletion_vectors BOOLEAN)");
            _o.Rows($"INSERT INTO {marker} SELECT {id}, {Lit(mode)}, {Lit(ts)}, {(dv ? "true" : "false")} " +
                    $"WHERE NOT EXISTS (SELECT 1 FROM {marker} WHERE table_id = {id})");
        }

        // The DuckLake snapshot the log has been brought to, -1 when there is no log yet.
        private static long SyncedSnapshot(EngineeredWood.DeltaLake.Snapshot.Snapshot? state) =>
            state is not null && state.AppTransactions.TryGetValue(AppId, out var txn) ? txn.Version : -1;

        private const int MaxConflictRetries = 32;

        private void SyncAll(List<Result> results, CancellationToken ct)
        {
            var state = Open(ct);
            long last = -1;
            if (state is not null)
            {
                if (!state.AppTransactions.TryGetValue(AppId, out var txn))
                {
                    throw new InvalidOperationException(
                        $"ducklake_delta_sync: '{_root}' already holds a Delta table that this sync did not write " +
                        "(no 'ducklake' txn action). Refusing to take it over.");
                }
                last = txn.Version;
                if (last >= _current)
                {
                    return; // another process already brought it this far
                }
            }

            if (state is null)
            {
                // A table's first sync is ONE commit, at the newest COMPLETE snapshot: the current one, or the
                // latest earlier one when the current state holds inlined rows (or live deletes, without DVs).
                long? first = null;
                foreach (long s in new[] { _current }.Concat(RelevantSnapshots(-1).Where(x => x < _current).Reverse()))
                {
                    var r = SyncTo(null, s, ct, out bool skipped);
                    if (r is not null)
                    {
                        results.Add(r);
                        state = Open(ct);
                        first = s;
                        break;
                    }
                    if (!skipped)
                    {
                        return; // the table did not exist at s, so no earlier state of it is wanted either
                    }
                }
                if (first is null)
                {
                    return; // no complete snapshot yet: nothing is mirrored until the next flush
                }
                last = first.Value;
            }
            foreach (long s in RelevantSnapshots(last))
            {
                // ⚠ state is re-read after each of our commits and may hold other syncs' commits by then: a snapshot
                // they already recorded must not be diffed against that newer state (rows would seem to come back —
                // "deletes cannot shrink", which is no conflict and was not retried).
                if (s <= SyncedSnapshot(state))
                {
                    _log.LogDebug("ducklake sync {Root}: snapshot {Snapshot} already recorded by another sync", _root, s);
                    continue;
                }
                // an incomplete snapshot is skipped; the next complete one carries its changes (a state diff)
                var r = SyncTo(state, s, ct, out _);
                if (r is not null)
                {
                    results.Add(r);
                    state = Open(ct);
                }
            }
        }

        // Whether the table has rows INLINED in the metadata database at snapshot s: rows that are in no Parquet
        // file, so a Delta version taken at s would lack them. DuckLake lists its inlined-data tables (one per
        // schema version) in ducklake_inlined_data_tables; a row is live from _ducklake_begin_snapshot until
        // _ducklake_end_snapshot
        // (set by a delete, an update or the flush that moved it into a file).
        private bool HasLiveInlinedRows(long s)
        {
            if (!_o.Probe($"SELECT 1 FROM {_o.M("ducklake_inlined_data_tables")} LIMIT 0"))
            {
                return false;
            }
            foreach (var r in _o.Rows(
                         $"SELECT DISTINCT table_name FROM {_o.M("ducklake_inlined_data_tables")} WHERE table_id = {_tableId}"))
            {
                string name = Str(r[0]);
                if (_o.Probe($"SELECT 1 FROM {_o.M(name)} LIMIT 0")
                    && _o.Rows($"SELECT 1 FROM {_o.M(name)} WHERE _ducklake_begin_snapshot <= {s} " +
                               $"AND (_ducklake_end_snapshot IS NULL OR _ducklake_end_snapshot > {s}) LIMIT 1").Count > 0)
                {
                    return true;
                }
            }
            return false;
        }

        // The commit file must be created CONDITIONALLY, or two syncs racing on one version both "win" and the
        // second silently replaces the first (measured on OneLake through the host filesystem: 41 of 48 commits
        // kept, docs/delta-transactions.md §8.4) — after which a later commit of the first can remove files or
        // vectors the log no longer holds. A local path is safe through the host filesystem (O_EXCL). A remote
        // one needs the conditional-PUT filesystem, whose credential comes from an attached Delta catalog that
        // covers the path (or, on Fabric compute, the notebook identity); without one it is refused.
        private static ITableFileSystem CommitFileSystem(nint opener, string root)
        {
            bool remote = root.Contains("://");
            if (!remote)
            {
                AmbientAdlsCredential.Current = null;
                AmbientS3Credential.Current = null;
                return TableFileSystems.Create(opener, root);
            }
            var found = DeltaCatalog.CredentialsFor(root);
            var adls = found?.Adls;
            if (adls is null && FabricLakehouse.IsOneLake(root) && FabricNotebookCredential.IsAvailable)
            {
                adls = AdlsCredential.FromToken(FabricCredentialResolver.AmbientChain());
            }
            bool isS3 = root.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);
            bool atomic = isS3 ? found?.S3 is not null : adls is not null && AdlsPath.IsAdlsGen2(root);
            if (!atomic)
            {
                throw new InvalidOperationException(
                    $"ducklake_delta_sync: {root} is on remote storage and no credential for a conditional commit was " +
                    "found, so two syncs could overwrite each other's commit. Attach the lake's data path (or a folder " +
                    "above it) as a Delta catalog with a SECRET, e.g. ATTACH '<data_path>' AS d (TYPE fabricator, " +
                    "PROVIDER 'delta', SECRET <name>, READ_ONLY false), in the same session.");
            }
            AmbientAdlsCredential.Current = isS3 ? null : adls;
            AmbientS3Credential.Current = isS3 ? found?.S3 : null;
            try
            {
                return TableFileSystems.Create(opener, root);
            }
            finally
            {
                AmbientAdlsCredential.Current = null;
                AmbientS3Credential.Current = null;
            }
        }

        private EngineeredWood.DeltaLake.Snapshot.Snapshot? Open(CancellationToken ct)
        {
            var log = new TransactionLog(_fs);
            if (log.GetLatestVersionAsync(ct).AsTask().GetAwaiter().GetResult() < 0)
            {
                return null;
            }
            using var table = DeltaTable.OpenAsync(_fs, null, ct).AsTask().GetAwaiter().GetResult();
            return table.CurrentSnapshot;
        }

        // Snapshots after `last` whose change list names this table. Over-inclusion is harmless (the state
        // diff of an untouched snapshot is empty and commits nothing); the current snapshot is always last.
        private IEnumerable<long> RelevantSnapshots(long last)
        {
            string id = _tableId.ToString(CultureInfo.InvariantCulture);
            var list = new List<long>();
            foreach (var r in _o.Rows(
                         // Only snapshots that still exist: ducklake_expire_snapshots leaves their change rows behind,
                         // and a scan AT an expired snapshot fails ("No snapshot found at version 4", measured when
                         // a sync behind live deletes met an expire). The next existing snapshot carries the changes.
                         $"SELECT snapshot_id, changes_made FROM {_o.M("ducklake_snapshot_changes")} " +
                         $"WHERE snapshot_id > {last} AND snapshot_id <= {_current} " +
                         $"AND snapshot_id IN (SELECT snapshot_id FROM {_o.M("ducklake_snapshot")}) ORDER BY snapshot_id"))
            {
                string changes = (string?)r[1] ?? "";
                foreach (var entry in changes.Split(','))
                {
                    int colon = entry.IndexOf(':');
                    if (colon < 0 || entry.IndexOf("schema", 0, colon, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    string value = entry.Substring(colon + 1);
                    if (value == id || value.Length > 0 && value[0] == '"')
                    {
                        list.Add(Long(r[0]));
                        break;
                    }
                }
            }
            return list;
        }

        private Result? SyncTo(EngineeredWood.DeltaLake.Snapshot.Snapshot? delta, long s, CancellationToken ct,
            out bool skipped)
        {
            skipped = false;
            var names = _o.Rows(
                $"SELECT t.table_name, sc.schema_name FROM {_o.M("ducklake_table")} t JOIN {_o.M("ducklake_schema")} sc " +
                $"ON sc.schema_id = t.schema_id WHERE t.table_id = {_tableId} AND {Active("t", s)} AND {Active("sc", s)}");
            if (names.Count == 0)
            {
                return null; // the table did not exist at s (created later, or dropped) — nothing to mirror
            }
            string table = Str(names[0][0]);
            string schema = Str(names[0][1]);
            string tableName = $"{schema}.{table}";

            if (HasLiveInlinedRows(s))
            {
                skipped = true;
                _log.LogDebug("ducklake sync {Table}: snapshot {Snapshot} holds inlined rows, waiting for a flush",
                    tableName, s);
                return null;
            }

            var files = _o.Rows(
                    $"SELECT data_file_id, path, path_is_relative, record_count, file_size_bytes, row_id_start, begin_snapshot, " +
                    $"encryption_key, mapping_id, partition_id FROM {_o.M("ducklake_data_file")} WHERE table_id = {_tableId} AND {Active("", s)}")
                .Select(r =>
                {
                    if (r[7] is not null)
                    {
                        throw Refuse(tableName, "its data files are encrypted, which Delta readers cannot decrypt");
                    }
                    if (r[8] is not null)
                    {
                        throw Refuse(tableName,
                            "it has files added with ducklake_add_data_files that need a name mapping, which Delta cannot express");
                    }
                    string path = Str(r[1]);
                    string rel = Bool(r[2]) ? path : RelativeTo(tableName, path);
                    return new DlFile
                    {
                        Id = Long(r[0]), RelPath = rel.Replace('\\', '/'), AbsPath = _root + "/" + rel.Replace('\\', '/'),
                        Records = Long(r[3]), Size = Long(r[4]), RowIdStart = NLong(r[5]), Begin = Long(r[6]),
                        PartitionId = NLong(r[9]),
                    };
                })
                .ToList();
            LoadFileNames(files);

            string mode = delta?.Metadata.Configuration is { } cfg && cfg.TryGetValue("delta.columnMapping.mode", out var m)
                ? m
                : _o._mapping;
            string timestamps = delta?.Metadata.Configuration is { } cfg2 && cfg2.TryGetValue(TimestampKey, out var tm)
                ? tm
                : _o._timestamps;
            bool deletionVectors = delta?.Metadata.Configuration is { } cfg3
                                   && cfg3.TryGetValue("delta.enableDeletionVectors", out var dvv)
                ? string.Equals(dvv, "true", StringComparison.OrdinalIgnoreCase)
                : _o._deletionVectors;
            var schemaPlan = new SchemaPlan(tableName, _columns, files, s, mode, timestamps, deletionVectors);
            // switched on by an option or already on; once on it stays on (the feed of later versions depends on it)
            bool changeDataFeed = _o._changeDataFeed
                || delta?.Metadata.Configuration is { } cfg4 && cfg4.TryGetValue("delta.enableChangeDataFeed", out var cdfv)
                   && string.Equals(cdfv, "true", StringComparison.OrdinalIgnoreCase);
            var partitionValues = FilePartitionValues();
            var partitionIds = PartitionColumns(delta, files, s, schemaPlan, partitionValues);
            var partitionNames = partitionIds.Select(id => schemaPlan.LogicalName(id) ?? throw Refuse(tableName,
                $"column {id}, which the Delta mirror is partitioned by, was dropped; Delta cannot change a table's partition " +
                "columns. Recreate the table, or the mirror")).ToList();

            long version = delta is null ? 0 : delta.Version + 1;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var actions = new List<DeltaAction>();

            // protocol + metaData: written whenever what they would say differs from what the log says.
            var protocol = schemaPlan.Protocol(delta?.Protocol, changeDataFeed);
            bool protocolChanged = delta is null || !SameFeatures(delta.Protocol, protocol);
            var config = schemaPlan.Configuration(delta?.Metadata.Configuration, partitionIds, changeDataFeed);
            string schemaString = schemaPlan.SchemaString();
            bool metaChanged = delta is null
                || delta.Metadata.SchemaString != schemaString
                || !SameConfig(delta.Metadata.Configuration, config)
                || !(delta.Metadata.PartitionColumns ?? Array.Empty<string>()).SequenceEqual(partitionNames, StringComparer.Ordinal);

            // deleted rows, for the files DuckLake has any deletes on at s
            var candidates = new HashSet<long>(_o.Rows(
                    $"SELECT DISTINCT data_file_id FROM {_o.M("ducklake_delete_file")} WHERE table_id = {_tableId} AND {Active("", s)}")
                .Select(r => Long(r[0])));
            string inlined = "ducklake_inlined_delete_" + _tableId.ToString(CultureInfo.InvariantCulture);
            if (_o.Probe($"SELECT 1 FROM {_o.M(inlined)} LIMIT 0"))
            {
                foreach (var r in _o.Rows($"SELECT DISTINCT file_id FROM {_o.M(inlined)} WHERE begin_snapshot <= {s}"))
                {
                    candidates.Add(Long(r[0]));
                }
            }
            var byId = files.ToDictionary(f => f.Id);
            candidates.IntersectWith(byId.Keys);
            var live = LiveCounts(schema, table, s, candidates.Select(id => byId[id]).ToList());
            if (!deletionVectors && files.Any(f => live.TryGetValue(f.Id, out var l) && f.Records > l))
            {
                // A table mirrored without deletion vectors can only show a state with no live deletes. Rewriting
                // the files (ducklake_rewrite_data_files with delete_threshold => 0) produces one.
                skipped = true;
                _log.LogDebug("ducklake sync {Table}: snapshot {Snapshot} has live deletes and the table has no " +
                              "deletion vectors, waiting for a rewrite", tableName, s);
                return null;
            }

            var existing = new Dictionary<string, AddFile>(StringComparer.Ordinal);
            if (delta is not null)
            {
                foreach (var a in delta.ActiveFiles.Values)
                {
                    existing[a.Path] = a;
                }
            }

            var expected = new HashSet<string>(StringComparer.Ordinal);
            var newAdds = new List<DlFile>();
            var dvChanges = new List<(DlFile File, AddFile Old, long Deleted)>();
            var bornWithDeletes = new Dictionary<long, long>();
            foreach (var f in files)
            {
                string enc = DeltaPath.Encode(f.RelPath);
                expected.Add(enc);
                long deleted = live.TryGetValue(f.Id, out var l) ? f.Records - l : 0;
                if (!existing.TryGetValue(enc, out var old))
                {
                    newAdds.Add(f);
                    if (deleted > 0)
                    {
                        bornWithDeletes[f.Id] = deleted;
                    }
                    continue;
                }
                long had = old.DeletionVector?.Cardinality ?? 0;
                if (deleted < had)
                {
                    throw Refuse(tableName,
                        $"file {f.RelPath} has {deleted} deleted rows in DuckLake but {had} in the Delta log — deletes cannot shrink");
                }
                if (deleted > had)
                {
                    dvChanges.Add((f, old, deleted));
                }
            }

            int removed = 0;
            foreach (var (path, a) in existing)
            {
                if (!expected.Contains(path))
                {
                    actions.Add(Remove(a, now));
                    removed++;
                }
            }

            if (!protocolChanged && !metaChanged && removed == 0 && newAdds.Count == 0 && dvChanges.Count == 0
                && delta is not null)
            {
                return null;
            }

            // row tracking: DuckLake's row_id_start IS the baseRowId, so a row has the same id in both
            long nextRowId = delta?.RowIdHighWaterMark ?? 0;
            foreach (var f in files)
            {
                if (f.RowIdStart is { } start)
                {
                    nextRowId = Math.Max(nextRowId, start + f.Records);
                }
            }
            long hwmBefore = nextRowId;

            var stats = Stats(newAdds, schemaPlan, new HashSet<long>(partitionIds));
            var dvWriter = new DeletionVectorWriter(_fs);
            int dvs = 0;
            foreach (var f in newAdds)
            {
                long baseRowId;
                if (f.RowIdStart is { } start)
                {
                    baseRowId = start;
                }
                else
                {
                    // No row_id_start: the file carries every row's id in its materialized column, so the range
                    // only has to be fresh. Reserve it above everything assigned so far.
                    baseRowId = nextRowId;
                    nextRowId += f.Records;
                }
                DeletionVector? dv = null;
                if (bornWithDeletes.ContainsKey(f.Id))
                {
                    dv = CreateDv(dvWriter, schema, table, s, f, ct);
                    dvs++;
                }
                actions.Add(new AddFile
                {
                    Path = DeltaPath.Encode(f.RelPath),
                    PartitionValues = PartitionValuesOf(f, partitionIds, partitionValues, schemaPlan, tableName),
                    Size = f.Size,
                    ModificationTime = now,
                    DataChange = true,
                    Stats = stats.TryGetValue(f.Id, out var st) ? (dv is null ? st : LooseBounds(st)) : null,
                    BaseRowId = baseRowId,
                    DefaultRowCommitVersion = version,
                    DeletionVector = dv,
                });
            }
            foreach (var (f, old, _) in dvChanges)
            {
                var dv = CreateDv(dvWriter, schema, table, s, f, ct);
                dvs++;
                actions.Add(Remove(old, now));
                actions.Add(old with
                {
                    ModificationTime = now,
                    DataChange = true,
                    DeletionVector = dv,
                    Stats = old.Stats is null ? null : LooseBounds(old.Stats),
                });
            }

            if (changeDataFeed && delta is not null)
            {
                var (cdcFiles, anyChange) = ChangeData(schema, table, SyncedSnapshot(delta), s, schemaPlan, partitionIds,
                    needsFiles: removed > 0 || dvChanges.Count > 0 || bornWithDeletes.Count > 0, ct);
                if (!anyChange)
                {
                    // Only files were rearranged (a merge or rewrite), no row changed: dataChange = false keeps such a
                    // commit out of the change feed and out of a streaming read, as Delta's own OPTIMIZE does.
                    for (int i = 0; i < actions.Count; i++)
                    {
                        actions[i] = actions[i] switch
                        {
                            AddFile a => a with { DataChange = false },
                            RemoveFile r => r with { DataChange = false },
                            var other => other,
                        };
                    }
                }
                actions.AddRange(cdcFiles);
            }

            var head = new List<DeltaAction> { CommitInfo(now, s, delta is null) };
            if (protocolChanged)
            {
                head.Add(protocol);
            }
            if (metaChanged)
            {
                head.Add(new MetadataAction
                {
                    Id = _uuid,
                    Format = new Format { Provider = "parquet", Options = new Dictionary<string, string>() },
                    SchemaString = schemaString,
                    PartitionColumns = partitionNames,
                    Configuration = config,
                    CreatedTime = delta?.Metadata.CreatedTime ?? now,
                });
            }
            actions.InsertRange(0, head);
            if (delta is null || nextRowId > delta.RowIdHighWaterMark)
            {
                actions.Add(EngineeredWood.DeltaLake.RowTracking.RowTrackingConfig.BuildHighWaterMarkAction(
                    Math.Max(nextRowId, hwmBefore)));
            }
            actions.Add(new TransactionId { AppId = AppId, Version = s, LastUpdated = now });

            new TransactionLog(_fs).WriteCommitAsync(version, actions, ct).AsTask().GetAwaiter().GetResult();
            _log.LogDebug("ducklake sync {Table}: snapshot {Snapshot} -> delta v{Version} (+{Added} -{Removed} dv {Dvs})",
                tableName, s, version, newAdds.Count, removed, dvs);

            bool checkpoint = false;
            int interval = config.TryGetValue("delta.checkpointInterval", out var ci)
                && int.TryParse(ci, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 10;
            if (_checkpointsSafe && version > 0 && version % interval == 0)
            {
                using var dt = DeltaTable.OpenAsync(_fs, null, ct).AsTask().GetAwaiter().GetResult();
                dt.CheckpointAsync(ct).AsTask().GetAwaiter().GetResult();
                checkpoint = true;
            }
            return new Result(schema, table, s, version, newAdds.Count, removed + dvChanges.Count, dvs, checkpoint);
        }

        // ── partitioning ──────────────────────────────────────────────────────────────────────────────
        // Delta has value partitions only, so an IDENTITY key of DuckLake's partitioning maps to a Delta partition column
        // and a transform (year(ts), bucket(…)) does not: declared as `ts`, partitionValues would REPLACE the column's
        // values with the year for every reader. Such a key is left undeclared; DuckLake still writes one file per value,
        // so the files' statistics are tight and Delta skips them by stats instead. Whether the path is a hive path
        // (hive_file_pattern) does not matter: Delta takes the values from partitionValues, never from the path. The
        // partition column stays in the data file too (DuckLake writes it there); a Delta reader takes partitionValues.

        // identity partition values per data file: data_file_id -> column id -> value (null for NULL)
        private Dictionary<long, Dictionary<long, string?>> FilePartitionValues()
        {
            var result = new Dictionary<long, Dictionary<long, string?>>();
            foreach (var r in _o.Rows(
                         $"SELECT v.data_file_id, c.column_id, v.partition_value FROM {_o.M("ducklake_file_partition_value")} v " +
                         $"JOIN {_o.M("ducklake_data_file")} f ON f.data_file_id = v.data_file_id " +
                         $"JOIN {_o.M("ducklake_partition_column")} c ON c.partition_id = f.partition_id AND c.table_id = v.table_id " +
                         $"AND c.partition_key_index = v.partition_key_index " +
                         $"WHERE v.table_id = {_tableId} AND c.transform = 'identity'"))
            {
                long file = Long(r[0]);
                if (!result.TryGetValue(file, out var map))
                {
                    result[file] = map = new Dictionary<long, string?>();
                }
                map[Long(r[1])] = (string?)r[2];
            }
            return result;
        }

        // The DuckLake column ids the Delta table is partitioned by. Fixed at the first sync: the identity keys of the
        // partitioning current then whose type has a Delta partition-value form and that EVERY live file carries (a file
        // written before the table was partitioned has no value, and Delta needs one in every add).
        private List<long> PartitionColumns(EngineeredWood.DeltaLake.Snapshot.Snapshot? delta, List<DlFile> files, long s,
            SchemaPlan plan, Dictionary<long, Dictionary<long, string?>> values)
        {
            if (delta is not null)
            {
                if (delta.Metadata.Configuration is not { } cfg || !cfg.TryGetValue(PartitionKey, out var text)
                    || string.IsNullOrEmpty(text))
                {
                    return new List<long>();
                }
                return text.Split(',').Select(x => long.Parse(x, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToList();
            }
            var chosen = new List<long>();
            if (!_o._partitioning)
            {
                return chosen;
            }
            foreach (var r in _o.Rows(
                         $"SELECT c.column_id FROM {_o.M("ducklake_partition_info")} p JOIN {_o.M("ducklake_partition_column")} c " +
                         $"ON c.partition_id = p.partition_id AND c.table_id = p.table_id WHERE p.table_id = {_tableId} " +
                         $"AND {Active("p", s)} AND c.transform = 'identity' ORDER BY c.partition_key_index"))
            {
                long id = Long(r[0]);
                if (!plan.PartitionEligible(id))
                {
                    _log.LogDebug("ducklake sync {Root}: partition key column {Id} has a type Delta cannot hold as a partition " +
                                  "value, left undeclared", _root, id);
                    continue;
                }
                // ⚠ an empty string is a distinct value in DuckLake and NULL in Delta, so it cannot be a partition value
                if (files.All(f => values.TryGetValue(f.Id, out var v) && v.TryGetValue(id, out var x) && x != ""))
                {
                    chosen.Add(id);
                }
            }
            return chosen;
        }

        private Dictionary<string, string> PartitionValuesOf(DlFile f, List<long> partitionIds,
            Dictionary<long, Dictionary<long, string?>> values, SchemaPlan plan, string tableName)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (long id in partitionIds)
            {
                string column = plan.LogicalName(id) ?? id.ToString(CultureInfo.InvariantCulture);
                if (!values.TryGetValue(f.Id, out var v) || !v.TryGetValue(id, out var value))
                {
                    throw Refuse(tableName,
                        $"data file {f.RelPath} has no value for partition column '{column}', which the Delta mirror is " +
                        "partitioned by: the table's partitioning changed after its first sync, and Delta cannot change a " +
                        "table's partition columns. Recreate the table, or the mirror (sync it into a new folder with " +
                        "partitioning := false)");
                }
                if (value == "")
                {
                    throw Refuse(tableName,
                        $"data file {f.RelPath} holds an empty string in partition column '{column}'; Delta reads an " +
                        "empty partition value as NULL");
                }
                result[plan.PartitionKeyName(id)] = value!;
            }
            return result;
        }

        // ── change data feed ──────────────────────────────────────────────────────────────────────────
        // The change rows of one Delta commit come from DuckLake's own change feed (ducklake_table_changes: insert,
        // delete, update_preimage, update_postimage — the Delta names), for the snapshots after `last` up to s. A commit
        // carrying cdc files is read from them alone, so they hold every change of the commit, inserts included.
        // Without any: a commit of only inserts (adds, no removes) needs none — a reader takes the adds as inserts.
        // Compaction snapshots are left out: DuckLake reports a rewrite as an update of every row it moved (measured).
        private static readonly HashSet<string> CompactionChanges =
            new(StringComparer.Ordinal) { "compacted_table", "merge_adjacent", "rewrite_delete" };

        private (List<CdcFile> Files, bool AnyChange) ChangeData(string schema, string table, long last, long s,
            SchemaPlan plan, List<long> partitionIds, bool needsFiles, CancellationToken ct)
        {
            string id = _tableId.ToString(CultureInfo.InvariantCulture);
            var compaction = new List<long>();
            foreach (var r in _o.Rows($"SELECT snapshot_id, changes_made FROM {_o.M("ducklake_snapshot_changes")} " +
                                      $"WHERE snapshot_id > {last} AND snapshot_id <= {s}"))
            {
                var kinds = ((string?)r[1] ?? "").Split(',')
                    .Select(e => (Colon: e.IndexOf(':'), Entry: e))
                    .Where(e => e.Colon > 0 && e.Entry.Substring(e.Colon + 1) == id)
                    .Select(e => e.Entry.Substring(0, e.Colon))
                    .ToList();
                if (kinds.Count > 0 && kinds.All(CompactionChanges.Contains))
                {
                    compaction.Add(Long(r[0]));
                }
            }
            // ducklake_table_changes needs an existing snapshot to start from; the changes after `last` are then selected
            // by their snapshot_id, which DuckLake keeps on the rows of an expired snapshot too (measured).
            long? start = NLong(_o.Rows($"SELECT max(snapshot_id) FROM {_o.M("ducklake_snapshot")} WHERE snapshot_id <= {last}")[0][0]);
            if (start is null)
            {
                _log.LogWarning("ducklake sync {Root}: every snapshot up to {Last} was expired, so the change data of the " +
                                "commit for snapshot {Snapshot} is taken from its added and removed files", _root, last, s);
                return (new List<CdcFile>(), true);
            }
            string from = $"ducklake_table_changes({Lit(_o._lake)}, {Lit(schema)}, {Lit(table)}, {start}, {s}) " +
                          $"WHERE snapshot_id > {last}" +
                          (compaction.Count > 0 ? $" AND snapshot_id NOT IN ({string.Join(", ", compaction)})" : "");
            var counts = _o.Rows($"SELECT count(*), count(*) FILTER (WHERE change_type <> 'insert') FROM {from}")[0];
            if (Long(counts[0]) == 0)
            {
                return (new List<CdcFile>(), false);
            }
            if (Long(counts[1]) == 0 && !needsFiles)
            {
                return (new List<CdcFile>(), true);
            }

            string select = plan.ChangeDataSelect() + ", change_type AS \"_change_type\"";
            string? fieldIds = plan.FieldIdsSpec();
            string options = "FORMAT parquet" + (fieldIds is null ? "" : ", FIELD_IDS " + fieldIds);
            // one file per partition: a cdc file carries partitionValues like an add
            var keys = partitionIds.Select(p => plan.LogicalName(p)!).ToList();
            var tuples = keys.Count == 0
                ? new List<object?[]> { Array.Empty<object?>() }
                : _o.Rows($"SELECT DISTINCT {string.Join(", ", keys.Select(k => $"CAST({Q(k)} AS VARCHAR)"))} FROM {from}");
            if (!_root.Contains("://"))
            {
                Directory.CreateDirectory(Path.Combine(_root, "_change_data"));
            }
            var result = new List<CdcFile>();
            foreach (var tuple in tuples)
            {
                ct.ThrowIfCancellationRequested();
                var pv = new Dictionary<string, string>(StringComparer.Ordinal);
                var where = new StringBuilder();
                for (int i = 0; i < keys.Count; i++)
                {
                    var value = (string?)tuple[i];
                    pv[plan.PartitionKeyName(partitionIds[i])] = value!;
                    where.Append($" AND CAST({Q(keys[i])} AS VARCHAR) IS NOT DISTINCT FROM {(value is null ? "NULL" : Lit(value))}");
                }
                string rel = "_change_data/cdc-" + Guid.NewGuid().ToString("N") + ".parquet";
                string abs = _root + "/" + rel;
                _o.Rows($"COPY (SELECT {select} FROM {from}{where}) TO {Lit(abs)} ({options})");
                long size = Long(_o.Rows($"SELECT size FROM read_blob({Lit(abs)})")[0][0]);
                result.Add(new CdcFile { Path = DeltaPath.Encode(rel), PartitionValues = pv, Size = size, DataChange = false });
            }
            return (result, true);
        }

        private string RelativeTo(string tableName, string absolute)
        {
            string a = absolute.Replace('\\', '/');
            string r = _root.Replace('\\', '/') + "/";
            if (!a.StartsWith(r, StringComparison.Ordinal))
            {
                throw Refuse(tableName,
                    $"data file {absolute} lies outside the table folder {_root}, and a Delta reader such as Fabric skips " +
                    "a table whose files are referenced by absolute path");
            }
            return a.Substring(r.Length);
        }

        // live (not deleted) row count per file at s, asked of DuckLake itself
        private Dictionary<long, long> LiveCounts(string schema, string table, long s, List<DlFile> files)
        {
            var result = new Dictionary<long, long>();
            if (files.Count == 0)
            {
                return result;
            }
            var byPath = files.ToDictionary(f => f.AbsPath, StringComparer.Ordinal);
            string inList = string.Join(", ", files.SelectMany(f => PathSpellings(f.AbsPath)).Select(Lit));
            foreach (var r in _o.Rows(
                         $"SELECT filename, count(*) FROM {_o.L(schema, table)} AT (VERSION => {s}) " +
                         $"WHERE filename IN ({inList}) GROUP BY filename"))
            {
                if (byPath.TryGetValue(Str(r[0]).Replace('\\', '/'), out var f))
                {
                    result[f.Id] = Long(r[1]);
                }
            }
            foreach (var f in files)
            {
                if (!result.ContainsKey(f.Id))
                {
                    // DuckLake drops a fully deleted file instead of keeping it live, so a file it still lists
                    // with no live row means the path did not match what the scan reports — never guess zero.
                    throw new InvalidOperationException(
                        $"ducklake_delta_sync: {schema}.{table}: file {f.AbsPath} returned no rows from DuckLake at snapshot {s}.");
                }
            }
            return result;
        }

        // DuckLake reports a LOCAL file with the platform's separator (backslashes on Windows, measured) and a
        // remote one with '/'. Both spellings go into the filter, which stays a plain IN on filename so DuckLake
        // can still skip the other files.
        private static IEnumerable<string> PathSpellings(string path)
        {
            yield return path;
            if (!path.Contains("://") && path.Contains('/'))
            {
                yield return path.Replace('/', '\\');
            }
        }

        private DeletionVector CreateDv(DeletionVectorWriter writer, string schema, string table, long s, DlFile f,
            CancellationToken ct)
        {
            var livePositions = new HashSet<long>(_o.Rows(
                    $"SELECT file_row_number FROM {_o.L(schema, table)} AT (VERSION => {s}) " +
                    $"WHERE filename IN ({string.Join(", ", PathSpellings(f.AbsPath).Select(Lit))})")
                .Select(r => Long(r[0])));
            var deleted = new List<long>();
            for (long i = 0; i < f.Records; i++)
            {
                if (!livePositions.Contains(i))
                {
                    deleted.Add(i);
                }
            }
            return writer.CreateAsync(deleted, deleted.Count, ct).AsTask().GetAwaiter().GetResult();
        }

        // Delta file statistics from DuckLake's per-file column stats, keyed by PHYSICAL name. Only types whose
        // DuckLake text form is the Delta JSON form are rendered; a missing bound only costs pruning, a wrong one
        // costs rows, so anything doubtful is left out.
        // Partition columns get no statistics: a Delta reader prunes them by partitionValues, and Spark collects stats
        // for data columns only.
        private Dictionary<long, string> Stats(List<DlFile> files, SchemaPlan plan, HashSet<long> partitionColumns)
        {
            var result = new Dictionary<long, string>();
            if (files.Count == 0)
            {
                return result;
            }
            var perFile = new Dictionary<long, List<object?[]>>();
            foreach (var r in _o.Rows(
                         $"SELECT data_file_id, column_id, null_count, min_value, max_value, contains_nan, min_is_exact, max_is_exact " +
                         $"FROM {_o.M("ducklake_file_column_stats")} WHERE table_id = {_tableId} AND data_file_id IN " +
                         $"({string.Join(", ", files.Select(f => f.Id.ToString(CultureInfo.InvariantCulture)))})"))
            {
                long id = Long(r[0]);
                if (!perFile.TryGetValue(id, out var list))
                {
                    perFile[id] = list = new List<object?[]>();
                }
                list.Add(r);
            }
            foreach (var f in files)
            {
                var rows = perFile.TryGetValue(f.Id, out var l) ? l : new List<object?[]>();
                var min = new StatNode();
                var max = new StatNode();
                var nulls = new StatNode();
                foreach (var r in rows)
                {
                    long col = Long(r[1]);
                    if (partitionColumns.Contains(col) || plan.StatsPath(col) is not { } path)
                    {
                        continue;
                    }
                    string type = plan.TypeAtCurrent(col) ?? "";
                    bool nan = r[5] is not null && Bool(r[5]);
                    min.Add(path, Bound(type, (string?)r[3], r[6], nan, isMin: true, plan.UtcTimestamps));
                    max.Add(path, Bound(type, (string?)r[4], r[7], nan, isMin: false, plan.UtcTimestamps));
                    if (r[2] is not null)
                    {
                        long n = Long(r[2]);
                        nulls.Add(path, (w, name) => w.WriteNumber(name, n));
                    }
                }
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteNumber("numRecords", f.Records);
                    min.Write(w, "minValues");
                    max.Write(w, "maxValues");
                    nulls.Write(w, "nullCount");
                    w.WriteEndObject();
                }
                result[f.Id] = Encoding.UTF8.GetString(ms.ToArray());
            }
            return result;
        }

        // One bound as Delta writes it, or null when DuckLake's text form cannot be rendered exactly. Timestamps go
        // to milliseconds (Delta's stats precision): a minimum rounds down and a maximum up, so they stay bounds.
        private static Action<Utf8JsonWriter, string>? Bound(string type, string? value, object? isExact, bool nan, bool isMin,
                                                            bool utcTimestamps)
        {
            if (value is null || (isExact is not null && !Bool(isExact)))
            {
                return null;
            }
            switch (type)
            {
                case "int8" or "int16" or "int32" or "int64":
                    return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv)
                        ? (w, n) => w.WriteNumber(n, lv) : null;
                case "float32" or "float64":
                    return !nan && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
                           && !double.IsNaN(dv) && !double.IsInfinity(dv)
                        ? (w, n) => w.WriteNumber(n, dv) : null;
                case var t when t.StartsWith("decimal(", StringComparison.Ordinal):
                    return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                        ? (w, n) => { w.WritePropertyName(n); w.WriteRawValue(value); } : null;
                case "varchar" or "date":
                    return (w, n) => w.WriteString(n, value);
                case "timestamp" or "timestamptz":
                {
                    if (!TryParseTimestamp(value, type == "timestamptz", out long ticks))
                    {
                        return null;
                    }
                    long rem = ticks % TimeSpan.TicksPerMillisecond;
                    if (rem != 0)
                    {
                        ticks += isMin ? -rem : TimeSpan.TicksPerMillisecond - rem;
                    }
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                    {
                        return null;
                    }
                    // the Delta type decides the form: timestamp is an instant (UTC), timestamp_ntz has no zone
                    bool instant = type == "timestamptz" || utcTimestamps;
                    string text = new DateTime(ticks, DateTimeKind.Unspecified)
                        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + (instant ? "Z" : "");
                    return (w, n) => w.WriteString(n, text);
                }
            }
            return null;
        }

        // DuckLake's text form: "2024-01-01 12:34:56.123456", a timestamptz with a UTC offset ("+00").
        private static bool TryParseTimestamp(string value, bool withOffset, out long ticks)
        {
            ticks = 0;
            if (withOffset)
            {
                if (!DateTimeOffset.TryParseExact(value, new[] { "yyyy-MM-dd HH:mm:ss.FFFFFFFzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                {
                    return false;
                }
                ticks = dto.UtcTicks;
                return true;
            }
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
            {
                return false;
            }
            ticks = dt.Ticks;
            return true;
        }

        // Delta's nested stats objects, one level per struct, keyed by physical name.
        private sealed class StatNode
        {
            private readonly List<(string Name, object Value)> _entries = new();

            public void Add(IReadOnlyList<string> path, Action<Utf8JsonWriter, string>? leaf, int at = 0)
            {
                if (leaf is null)
                {
                    return;
                }
                if (at == path.Count - 1)
                {
                    _entries.Add((path[at], leaf));
                    return;
                }
                var child = _entries.FirstOrDefault(e => e.Name == path[at] && e.Value is StatNode).Value as StatNode;
                if (child is null)
                {
                    child = new StatNode();
                    _entries.Add((path[at], child));
                }
                child.Add(path, leaf, at + 1);
            }

            public void Write(Utf8JsonWriter w, string name)
            {
                w.WriteStartObject(name);
                foreach (var (n, v) in _entries)
                {
                    if (v is StatNode node)
                    {
                        node.Write(w, n);
                    }
                    else
                    {
                        ((Action<Utf8JsonWriter, string>)v)(w, n);
                    }
                }
                w.WriteEndObject();
            }
        }

        private CommitInfo CommitInfo(long now, long snapshot, bool create)
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["timestamp"] = now,
                ["operation"] = create ? "CREATE TABLE" : "WRITE",
                ["operationParameters"] = new Dictionary<string, string>
                {
                    ["source"] = "ducklake",
                    ["ducklakeSnapshot"] = snapshot.ToString(CultureInfo.InvariantCulture),
                },
                ["engineInfo"] = "fabricator ducklake_delta_sync",
                ["isBlindAppend"] = false,
            });
            using var doc = JsonDocument.Parse(json);
            return new CommitInfo
            {
                Values = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            };
        }

        private InvalidOperationException Refuse(string table, string why) =>
            new($"ducklake_delta_sync: cannot mirror {table} to Delta: {why}.");
    }

    // ── schema ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Delta schema of a DuckLake table at one snapshot. Every field carries <c>delta.columnMapping.id</c> =
    /// DuckLake's column id (which is also the Parquet field id DuckLake writes), and a PHYSICAL name: in
    /// <c>name</c> mode the one name every live file uses for the column, in <c>id</c> mode a synthetic
    /// <c>col-&lt;id&gt;</c> (readers resolve by field id). A DuckLake schema change Delta cannot express without
    /// changing what an old file means is refused here, so the mirror stops instead of going wrong.
    /// </summary>
    private sealed class SchemaPlan
    {
        private readonly string _table;
        private readonly List<DlColumn> _all;
        private readonly List<DlFile> _files;
        private readonly long _s;
        private readonly string _mode;
        private readonly string _timestamps;
        private readonly bool _deletionVectors;
        private readonly List<DlColumn> _active;
        private readonly Dictionary<long, string> _physical = new();
        private readonly Dictionary<long, List<(string From, string To)>> _typeChanges = new();
        private bool _needsNtz;

        public SchemaPlan(string table, List<DlColumn> all, List<DlFile> files, long s, string mode, string timestamps,
            bool deletionVectors)
        {
            _timestamps = timestamps;
            _deletionVectors = deletionVectors;
            _table = table;
            _all = all;
            _files = files;
            _s = s;
            _mode = mode;
            _active = all.Where(c => c.ActiveAt(s)).ToList();
            foreach (var c in _active)
            {
                Plan(c);
            }
            if (_mode != "id")
            {
                foreach (var group in _active.GroupBy(c => c.Parent))
                {
                    var dup = group.GroupBy(c => _physical[c.Id]).FirstOrDefault(g => g.Count() > 1);
                    if (dup is not null)
                    {
                        throw Refuse($"two live columns use the physical name '{dup.Key}'");
                    }
                }
            }
        }

        private DlColumn? At(long id, long snapshot) =>
            _all.FirstOrDefault(c => c.Id == id && c.ActiveAt(snapshot));

        private void Plan(DlColumn c)
        {
            // the files that hold this column, and the name/type each was written with
            var namesInFiles = new HashSet<string>(StringComparer.Ordinal);
            var typesInFiles = new HashSet<string>(StringComparer.Ordinal);
            bool predatesFiles = false;
            foreach (var f in _files)
            {
                var then = At(c.Id, f.Begin);
                if (f.Names is not null)
                {
                    // ⚠ THE FILE'S OWN NAME, not the metadata's at f.Begin: ducklake_rewrite_data_files copies a file
                    // under the column names it was WRITTEN with but gives it a begin_snapshot after a rename — so the
                    // inferred name was the new one, and a Delta reader looked for a column the file does not have
                    // (MEASURED 2026-11-02: rename b -> b2, delete, rewrite, sync — DuckLake 'y', the mirror NULL).
                    if (!f.Names.TryGetValue(c.Id, out var inFile))
                    {
                        predatesFiles = true;
                        continue;
                    }
                    namesInFiles.Add(inFile);
                    typesInFiles.Add((then ?? c).Type);
                    continue;
                }
                if (then is null)
                {
                    predatesFiles = true;
                    // name mode: an old file must not hold ANOTHER column under the name this one will be read by
                    continue;
                }
                namesInFiles.Add(then.Name);
                typesInFiles.Add(then.Type);
            }

            string physical;
            if (_mode == "id")
            {
                physical = "col-" + c.Id.ToString(CultureInfo.InvariantCulture);
            }
            else if (namesInFiles.Count > 1)
            {
                throw Refuse($"column '{c.Name}' was renamed and live data files use both names " +
                             $"({string.Join(", ", namesInFiles.Select(n => "'" + n + "'"))}); in '{_mode}' column mapping " +
                             "one physical name must cover every file. DuckLake's compaction keeps the name a file was written with, so this " +
                             "does not heal: recreate the table (CREATE TABLE … AS SELECT) or mirror with column_mapping := 'id'");
            }
            else
            {
                physical = namesInFiles.Count == 1 ? namesInFiles.First() : c.Name;
                if (_mode == "none" && physical != c.Name)
                {
                    throw Refuse($"column '{c.Name}' was renamed from '{physical}' and live data files still use the old " +
                                 "name; without column mapping a Delta reader looks for the new name and finds nothing. DuckLake's compaction " +
                                 "keeps the old name in the files: mirror with column_mapping := 'name', or recreate the table");
                }
            }
            _physical[c.Id] = physical;

            if (predatesFiles)
            {
                if (c.InitialDefault is not null)
                {
                    throw Refuse($"column '{c.Name}' was added with a default ({c.InitialDefault}); DuckLake returns it " +
                                 "for rows of older files, a Delta reader returns NULL");
                }
                if (_mode != "id")
                {
                    foreach (var f in _files)
                    {
                        if (f.Names is not null ? f.Names.ContainsKey(c.Id) : At(c.Id, f.Begin) is not null)
                        {
                            continue;
                        }
                        var clash = f.Names is not null
                            ? _all.FirstOrDefault(o => o.Id != c.Id && o.Parent == c.Parent
                                                       && f.Names.TryGetValue(o.Id, out var n) && string.Equals(n, physical, StringComparison.Ordinal))
                            : _all.FirstOrDefault(o => o.Id != c.Id && o.Parent == c.Parent && o.ActiveAt(f.Begin)
                                                       && string.Equals(o.Name, physical, StringComparison.Ordinal));
                        if (clash is not null)
                        {
                            throw Refuse($"column '{c.Name}' reuses the name of a dropped column whose values are still in " +
                                         $"live data files; in '{_mode}' column mapping a Delta reader would return those old " +
                                         "values for it. Recreate the table (CREATE TABLE … AS SELECT) or mirror with column_mapping := 'id'");
                        }
                    }
                }
            }

            typesInFiles.Add(c.Type);
            if (typesInFiles.Count > 1)
            {
                var history = _all.Where(h => h.Id == c.Id && h.Begin <= _s).OrderBy(h => h.Begin).Select(h => h.Type).ToList();
                var changes = new List<(string, string)>();
                for (int i = 1; i < history.Count; i++)
                {
                    if (history[i] == history[i - 1])
                    {
                        continue;
                    }
                    if (!Widens(history[i - 1], history[i]))
                    {
                        throw Refuse($"column '{c.Name}' changed type {history[i - 1]} -> {history[i]}, which Delta type " +
                                     "widening cannot express");
                    }
                    changes.Add((DeltaPrimitive(history[i - 1], c.Name), DeltaPrimitive(history[i], c.Name)));
                }
                _typeChanges[c.Id] = changes;
            }
        }

        private static readonly string[] IntegerOrder = { "int8", "int16", "int32", "int64" };

        private static bool Widens(string from, string to)
        {
            int a = Array.IndexOf(IntegerOrder, from), b = Array.IndexOf(IntegerOrder, to);
            if (a >= 0 && b > a)
            {
                return true;
            }
            if (from == "float32" && to == "float64")
            {
                return true;
            }
            if (TryDecimal(from, out var p1, out var s1) && TryDecimal(to, out var p2, out var s2))
            {
                return p2 >= p1 && s2 >= s1 && p2 - s2 >= p1 - s1;
            }
            return false;
        }

        private static bool TryDecimal(string t, out int p, out int s)
        {
            p = s = 0;
            if (!t.StartsWith("decimal(", StringComparison.Ordinal) || !t.EndsWith(")", StringComparison.Ordinal))
            {
                return false;
            }
            var parts = t.Substring(8, t.Length - 9).Split(',');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out p)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out s);
        }

        // The physical path of a column's stats in Delta: a primitive whose ancestors are all structs (Delta keeps no
        // stats inside arrays or maps), outermost first; null otherwise.
        public bool UtcTimestamps => _timestamps == "utc";

        public IReadOnlyList<string>? StatsPath(long id)
        {
            var c = _active.FirstOrDefault(x => x.Id == id);
            if (c is null || !IsPrimitive(c.Type))
            {
                return null;
            }
            var path = new List<string>();
            for (var at = c; ; )
            {
                path.Insert(0, _physical[at.Id]);
                if (at.Parent is not { } parent)
                {
                    return path;
                }
                at = _active.FirstOrDefault(x => x.Id == parent);
                if (at is null || at.Type != "struct")
                {
                    return null;
                }
            }
        }

        public string? TypeAtCurrent(long id) => _active.FirstOrDefault(c => c.Id == id)?.Type;

        // Partition columns: top level, and a type whose DuckLake partition value IS the Delta partition-value text
        // (measured: integers, strings, dates, true/false, decimals with their scale). Timestamps are left out: Delta
        // reads a timestamp partition value in the reader's time zone unless it carries one, which DuckLake's does not.
        public bool PartitionEligible(long id) =>
            _active.FirstOrDefault(c => c.Id == id && c.Parent is null) is { } c
            && (c.Type is "int8" or "int16" or "int32" or "int64" or "varchar" or "date" or "boolean"
                || TryDecimal(c.Type, out _, out _));

        public string? LogicalName(long id) => _active.FirstOrDefault(c => c.Id == id && c.Parent is null)?.Name;

        // partitionValues are keyed by the physical name under column mapping, by the name without it
        public string PartitionKeyName(long id) => _mode == "none" ? LogicalName(id)! : _physical[id];

        // The SELECT list that writes a change row the way the table's data files hold it: physical names at every
        // level, so a cdc file is read with the same column mapping as the data files.
        public string ChangeDataSelect() =>
            string.Join(", ", Children(null).Select(c => $"{ChangeExpr(c, Q(c.Name), 0)} AS {Q(_physical[c.Id])}"));

        // The COPY FIELD_IDS option for a cdc file, as DeltaWriter.BuildFieldIdsSpec writes it for data files: keyed by
        // physical name, a struct with DuckDB's __duckdb_field_id sentinel, a list or map descending only to reach a
        // struct ('element' / 'key' / 'value' carry no Delta id). Null without column mapping.
        public string? FieldIdsSpec() =>
            _mode == "none" ? null : "{" + string.Join(", ", Children(null).Select(FieldIdEntry)) + "}";

        private string FieldIdEntry(DlColumn c) => $"'{_physical[c.Id].Replace("'", "''")}': {FieldIdValue(c)}";

        private string FieldIdValue(DlColumn c)
        {
            string id = c.Id.ToString(CultureInfo.InvariantCulture);
            return c.Type switch
            {
                "struct" => "{__duckdb_field_id: " + id + string.Concat(Children(c.Id).Select(ch => ", " + FieldIdEntry(ch))) + "}",
                "list" or "map" when ContainsStruct(c) => "{__duckdb_field_id: " + id + string.Concat(
                    Children(c.Id).Where(ContainsStruct).Select(ch => $", '{(c.Type == "list" ? "element" : ch.Name)}': {InnerNode(ch)}")) + "}",
                _ => id,
            };
        }

        // a list element or map key/value: no Delta id of its own
        private string InnerNode(DlColumn c) => c.Type switch
        {
            "struct" => "{" + string.Join(", ", Children(c.Id).Select(FieldIdEntry)) + "}",
            "list" or "map" => "{" + string.Join(", ", Children(c.Id).Where(ContainsStruct)
                .Select(ch => $"'{(c.Type == "list" ? "element" : ch.Name)}': {InnerNode(ch)}")) + "}",
            _ => "{}",
        };

        private bool ContainsStruct(DlColumn c) => c.Type == "struct" || Children(c.Id).Any(ContainsStruct);

        // whether a column's value must be rebuilt for its physical names (a list element and a map's key and value
        // are named by Parquet's structure, not by Delta)
        private bool NeedsRename(DlColumn c) => c.Type switch
        {
            "struct" => Children(c.Id).Any(ch => _physical[ch.Id] != ch.Name || NeedsRename(ch)),
            "list" or "map" => Children(c.Id).Any(NeedsRename),
            _ => false,
        };

        private string ChangeExpr(DlColumn c, string src, int depth)
        {
            if (!NeedsRename(c))
            {
                return src;
            }
            switch (c.Type)
            {
                case "struct":
                    // struct_pack of a NULL struct is a struct of NULLs, so the NULL is kept explicitly
                    return $"CASE WHEN {src} IS NULL THEN NULL ELSE struct_pack(" + string.Join(", ", Children(c.Id).Select(
                        ch => $"{Q(_physical[ch.Id])} := {ChangeExpr(ch, $"({src}).{Q(ch.Name)}", depth)}")) + ") END";
                case "list":
                {
                    var element = Children(c.Id).Single();
                    string v = "x" + depth.ToString(CultureInfo.InvariantCulture);
                    return $"list_transform({src}, lambda {v}: {ChangeExpr(element, v, depth + 1)})";
                }
                case "map":
                {
                    var kv = Children(c.Id);
                    var key = kv.First(k => k.Name == "key");
                    var value = kv.First(k => k.Name == "value");
                    string v = "e" + depth.ToString(CultureInfo.InvariantCulture);
                    return $"map_from_entries(list_transform(map_entries({src}), lambda {v}: struct_pack(key := " +
                           $"{ChangeExpr(key, v + ".key", depth + 1)}, value := {ChangeExpr(value, v + ".value", depth + 1)})))";
                }
            }
            return src;
        }

        private static bool IsPrimitive(string t) => t is not ("struct" or "list" or "map");

        private string DeltaPrimitive(string t, string column)
        {
            switch (t)
            {
                case "int8": return "byte";
                case "int16": return "short";
                case "int32": return "integer";
                case "int64": return "long";
                case "float32": return "float";
                case "float64": return "double";
                case "varchar" or "json": return "string";
                case "blob": return "binary";
                case "boolean": return "boolean";
                case "date": return "date";
                case "timestamptz": return "timestamp";
                case "timestamp" when _timestamps == "utc":
                    return "timestamp";
                case "timestamp":
                    _needsNtz = true;
                    return "timestamp_ntz";
            }
            if (TryDecimal(t, out var p, out var s))
            {
                return $"decimal({p},{s})";
            }
            throw Refuse($"column '{column}' has type {t}, which has no Delta equivalent DuckLake's Parquet encoding matches");
        }

        public string SchemaString()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                WriteStruct(w, null);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private List<DlColumn> Children(long? parent) =>
            _active.Where(c => c.Parent == parent).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();

        private void WriteStruct(Utf8JsonWriter w, long? parent)
        {
            w.WriteStartObject();
            w.WriteString("type", "struct");
            w.WriteStartArray("fields");
            foreach (var c in Children(parent))
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WritePropertyName("type");
                WriteType(w, c);
                w.WriteBoolean("nullable", c.Nullable);
                w.WriteStartObject("metadata");
                if (_mode != "none")
                {
                    w.WriteNumber("delta.columnMapping.id", c.Id);
                    w.WriteString("delta.columnMapping.physicalName", _physical[c.Id]);
                }
                if (_typeChanges.TryGetValue(c.Id, out var changes) && changes.Count > 0)
                {
                    w.WriteStartArray("delta.typeChanges");
                    foreach (var (from, to) in changes)
                    {
                        w.WriteStartObject();
                        w.WriteString("fromType", from);
                        w.WriteString("toType", to);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        private void WriteType(Utf8JsonWriter w, DlColumn c)
        {
            switch (c.Type)
            {
                case "struct":
                    WriteStruct(w, c.Id);
                    return;
                case "list":
                {
                    var element = Children(c.Id).Single();
                    w.WriteStartObject();
                    w.WriteString("type", "array");
                    w.WritePropertyName("elementType");
                    WriteType(w, element);
                    w.WriteBoolean("containsNull", element.Nullable);
                    w.WriteEndObject();
                    return;
                }
                case "map":
                {
                    var kv = Children(c.Id);
                    var key = kv.First(k => k.Name == "key");
                    var value = kv.First(k => k.Name == "value");
                    w.WriteStartObject();
                    w.WriteString("type", "map");
                    w.WritePropertyName("keyType");
                    WriteType(w, key);
                    w.WritePropertyName("valueType");
                    WriteType(w, value);
                    w.WriteBoolean("valueContainsNull", value.Nullable);
                    w.WriteEndObject();
                    return;
                }
                default:
                    w.WriteStringValue(DeltaPrimitive(c.Type, c.Name));
                    return;
            }
        }

        public ProtocolAction Protocol(ProtocolAction? existing, bool changeDataFeed)
        {
            SchemaString(); // resolves _needsNtz
            // Only what the table needs: a reader without column mapping or deletion vectors (SQL Server's DELTA
            // reader) can then read it at reader version 1. Row tracking is a writer-only feature.
            var reader = new SortedSet<string>(StringComparer.Ordinal);
            var writer = new SortedSet<string>(StringComparer.Ordinal) { "rowTracking", "domainMetadata" };
            if (_mode != "none")
            {
                reader.Add("columnMapping");
                writer.Add("columnMapping");
            }
            if (_deletionVectors)
            {
                reader.Add("deletionVectors");
                writer.Add("deletionVectors");
            }
            if (_needsNtz)
            {
                reader.Add("timestampNtz");
                writer.Add("timestampNtz");
            }
            if (_typeChanges.Values.Any(v => v.Count > 0))
            {
                reader.Add("typeWidening");
                writer.Add("typeWidening");
            }
            if (changeDataFeed)
            {
                writer.Add("changeDataFeed");
            }
            // a feature once declared stays declared: older versions of a file may still need it
            foreach (var f in existing?.ReaderFeatures ?? Array.Empty<string>())
            {
                reader.Add(f);
            }
            foreach (var f in existing?.WriterFeatures ?? Array.Empty<string>())
            {
                writer.Add(f);
            }
            return new ProtocolAction
            {
                MinReaderVersion = reader.Count > 0 ? 3 : 1,
                MinWriterVersion = 7,
                ReaderFeatures = reader.Count > 0 ? reader.ToList() : null,
                WriterFeatures = writer.ToList(),
            };
        }

        public Dictionary<string, string> Configuration(IReadOnlyDictionary<string, string>? existing,
            IReadOnlyList<long> partitionIds, bool changeDataFeed)
        {
            var config = new Dictionary<string, string>(StringComparer.Ordinal);
            if (existing is not null)
            {
                foreach (var (k, v) in existing)
                {
                    config[k] = v;
                }
            }
            config["delta.columnMapping.mode"] = _mode;
            config[TimestampKey] = _timestamps;
            if (_mode != "none")
            {
                long maxId = _all.Count == 0 ? 0 : _all.Max(c => c.Id);
                if (config.TryGetValue("delta.columnMapping.maxColumnId", out var prev)
                    && long.TryParse(prev, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                {
                    maxId = Math.Max(maxId, p);
                }
                config["delta.columnMapping.maxColumnId"] = maxId.ToString(CultureInfo.InvariantCulture);
            }
            config["delta.enableDeletionVectors"] = _deletionVectors ? "true" : "false";
            config["delta.enableRowTracking"] = "true";
            config["delta.rowTracking.materializedRowIdColumnName"] = MaterializedRowId;
            config["delta.rowTracking.materializedRowCommitVersionColumnName"] = MaterializedRowCommitVersion;
            if (_typeChanges.Values.Any(v => v.Count > 0))
            {
                config["delta.enableTypeWidening"] = "true";
            }
            config[PartitionKey] = string.Join(",", partitionIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            if (changeDataFeed)
            {
                config["delta.enableChangeDataFeed"] = "true";
            }
            return config;
        }

        private InvalidOperationException Refuse(string why) =>
            new($"ducklake_delta_sync: cannot mirror {_table} to Delta: {why}.");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static RemoveFile Remove(AddFile a, long now) => new()
    {
        Path = a.Path,
        DeletionTimestamp = now,
        DataChange = true,
        ExtendedFileMetadata = true,
        PartitionValues = a.PartitionValues,
        Size = a.Size,
        Tags = a.Tags,
        DeletionVector = a.DeletionVector,
        BaseRowId = a.BaseRowId,
        DefaultRowCommitVersion = a.DefaultRowCommitVersion,
    };

    // once a deletion vector hides rows the bounds were computed over, they are bounds, not exact values
    private static string LooseBounds(string stats)
    {
        using var doc = JsonDocument.Parse(stats);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name is "tightBounds" or "nullCount")
                {
                    continue;
                }
                p.WriteTo(w);
            }
            w.WriteBoolean("tightBounds", false);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool SameFeatures(ProtocolAction a, ProtocolAction b) =>
        a.MinReaderVersion == b.MinReaderVersion && a.MinWriterVersion == b.MinWriterVersion
        && (a.ReaderFeatures ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual((b.ReaderFeatures ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))
        && (a.WriterFeatures ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual((b.WriterFeatures ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal));

    private static bool SameConfig(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string> b) =>
        a is not null && a.Count == b.Count && b.All(kv => a.TryGetValue(kv.Key, out var v) && v == kv.Value);

    internal string M(string table) => $"{Q(_meta)}.{Q(_ms)}.{Q(table)}";

    private string L(string schema, string table) => $"{Q(_lake)}.{Q(schema)}.{Q(table)}";

    private static string Active(string alias, long s)
    {
        string p = alias.Length == 0 ? "" : alias + ".";
        return $"{p}begin_snapshot <= {s} AND ({p}end_snapshot IS NULL OR {p}end_snapshot > {s})";
    }

    internal static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    internal static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    internal static long Long(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static long? NLong(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    internal static bool Bool(object? v) => v switch
    {
        bool b => b,
        null => false,
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
    };

    private static string Str(object? v) => v?.ToString() ?? "";

    private bool Probe(string sql)
    {
        try
        {
            Rows(sql);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private List<object?[]> Rows(string sql) => Query(sql);

    internal static List<object?[]> Query(string sql)
    {
        // ⚠ A host query that reaches one of OUR catalogs (the metadata catalog is ours whenever SQL Server hosts
        // it) re-enters the bridge on this flow, and every crossing sets the ambients to ITS short-lived
        // connection — which is gone when the query ends. Left alone, the next host-FS call here dereferences
        // that dead ClientContext (measured: an access violation in HostFs.Glob). So the caller's ambients are
        // put back once the query is drained.
        nint opener = AmbientOpener.Current;
        long session = ProviderSettingsStore.CurrentSession;
        long txn = AmbientTransaction.Current;
        try
        {
            return Drain(sql);
        }
        finally
        {
            AmbientOpener.Current = opener;
            ProviderSettingsStore.CurrentSession = session;
            AmbientTransaction.Current = txn;
            AmbientAdlsCredential.Current = null;
        }
    }

    private static List<object?[]> Drain(string sql)
    {
        var rows = new List<object?[]>();
        using var stream = Host.Query(sql);
        while (true)
        {
            using var batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            for (int i = 0; i < batch.Length; i++)
            {
                var row = new object?[batch.ColumnCount];
                for (int c = 0; c < batch.ColumnCount; c++)
                {
                    row[c] = Value(batch.Column(c), i);
                }
                rows.Add(row);
            }
        }
        return rows;
    }

    private static object? Value(IArrowArray a, int i)
    {
        if (a.IsNull(i))
        {
            return null;
        }
        return a switch
        {
            Int64Array x => x.GetValue(i),
            Int32Array x => (long?)x.GetValue(i),
            Int16Array x => (long?)x.GetValue(i),
            Int8Array x => (long?)x.GetValue(i),
            UInt64Array x => (long?)x.GetValue(i),
            UInt32Array x => (long?)x.GetValue(i),
            BooleanArray x => x.GetValue(i),
            StringArray x => x.GetString(i),
            StringViewArray x => x.GetString(i),
            LargeStringArray x => x.GetString(i),
            DoubleArray x => x.GetValue(i),
            _ => throw new InvalidOperationException($"ducklake_delta_sync: unexpected column type {a.Data.DataType.Name}"),
        };
    }

    internal static RecordBatch ToBatch(List<Result> results)
    {
        var schema = new StringArray.Builder();
        var table = new StringArray.Builder();
        var snap = new Int64Array.Builder();
        var ver = new Int64Array.Builder();
        var added = new Int64Array.Builder();
        var removed = new Int64Array.Builder();
        var dvs = new Int64Array.Builder();
        var ckpt = new BooleanArray.Builder();
        foreach (var r in results)
        {
            schema.Append(r.Schema);
            table.Append(r.Table);
            snap.Append(r.Snapshot);
            ver.Append(r.Version);
            added.Append(r.Added);
            removed.Append(r.Removed);
            dvs.Append(r.Dvs);
            ckpt.Append(r.Checkpoint);
        }
        return new RecordBatch(DuckLakeDeltaSyncFunction.ResultSchema, new IArrowArray[]
        {
            schema.Build(), table.Build(), snap.Build(), ver.Build(), added.Build(), removed.Build(), dvs.Build(), ckpt.Build(),
        }, results.Count);
    }
}
