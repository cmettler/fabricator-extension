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
/// [, deletion_vectors := true | false] [, timestamp_mapping := 'utc' | 'ntz'] [, metadata_schema := '…'])</c> — writes a
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
        return new SyncBinding(lake, table, mapping, FabricArgs.Str(args, 3), timestamps, deletionVectors);
    }

    private sealed class SyncBinding : ITableFunctionBinding
    {
        private readonly string _lake;
        private readonly string? _table;
        private readonly string _mapping;
        private readonly string? _metadataSchema;
        private readonly string _timestamps;
        private readonly bool _deletionVectors;

        public SyncBinding(string lake, string? table, string mapping, string? metadataSchema, string timestamps,
            bool deletionVectors)
        {
            _timestamps = timestamps;
            _deletionVectors = deletionVectors;
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
            var results = new DuckLakeDeltaSync(AmbientOpener.Current, _lake, _mapping, _timestamps, _deletionVectors).Run(_table, _metadataSchema, ct);
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
    private string _ms = "";

    // What a DuckDB TIMESTAMP (no time zone) is declared as. 'utc' (the DEFAULT, user decision 2026-10-27)
    // declares it a Delta timestamp, i.e. the same microseconds read as a UTC instant, because Fabric's SQL
    // endpoint REFUSES timestamp_ntz: the table appears without the column ("Columns of the specified data types
    // are not supported … TIMESTAMP_NTZ", measured 2026-10-27). 'ntz' is the spec's answer, for readers that
    // take it. Chosen at a table's first sync and persisted, since changing it later changes the column's type.
    internal const string TimestampKey = "fabricator.ducklake.timestampMapping";

    internal sealed record Result(
        string Schema, string Table, long Snapshot, long Version, long Added, long Removed, long Dvs, bool Checkpoint);

    public DuckLakeDeltaSync(nint opener, string lake, string mapping, string timestamps, bool deletionVectors)
    {
        _deletionVectors = deletionVectors;
        _opener = opener;
        _lake = lake;
        _meta = "__ducklake_metadata_" + lake;
        _mapping = mapping;
        _timestamps = timestamps;
    }

    public List<Result> Run(string? tableFilter, string? metadataSchema, CancellationToken ct)
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

        var results = new List<Result>();
        bool matched = false;
        foreach (var t in tables)
        {
            string table = (string)t[2]!;
            string schema = (string)t[5]!;
            if (tableFilter is not null && !MatchesFilter(tableFilter, schema, table))
            {
                continue;
            }
            matched = true;
            string root = dataPath;
            root = Bool(t[7]) ? root + (string)t[6]! : (string)t[6]!;
            root = Bool(t[4]) ? root + (string)t[3]! : (string)t[3]!;
            var sync = new TableSync(this, Long(t[0]), Str(t[1]), root.TrimEnd('/', '\\'), current, checkpointsSafe);
            results.AddRange(sync.Run(ct));
        }
        if (tableFilter is not null && !matched)
        {
            throw new InvalidOperationException($"ducklake_delta_sync: no table '{tableFilter}' in lake '{_lake}'.");
        }
        return results;
    }

    private static bool MatchesFilter(string filter, string schema, string table)
    {
        int dot = filter.IndexOf('.');
        return dot < 0
            ? string.Equals(filter, table, StringComparison.OrdinalIgnoreCase)
            : string.Equals(filter.Substring(0, dot), schema, StringComparison.OrdinalIgnoreCase)
              && string.Equals(filter.Substring(dot + 1), table, StringComparison.OrdinalIgnoreCase);
    }

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
                         $"SELECT snapshot_id, changes_made FROM {_o.M("ducklake_snapshot_changes")} " +
                         $"WHERE snapshot_id > {last} AND snapshot_id <= {_current} ORDER BY snapshot_id"))
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
                    $"encryption_key, mapping_id FROM {_o.M("ducklake_data_file")} WHERE table_id = {_tableId} AND {Active("", s)}")
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
                    };
                })
                .ToList();

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

            long version = delta is null ? 0 : delta.Version + 1;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var actions = new List<DeltaAction>();

            // protocol + metaData: written whenever what they would say differs from what the log says.
            var protocol = schemaPlan.Protocol(delta?.Protocol);
            bool protocolChanged = delta is null || !SameFeatures(delta.Protocol, protocol);
            var config = schemaPlan.Configuration(delta?.Metadata.Configuration);
            string schemaString = schemaPlan.SchemaString();
            bool metaChanged = delta is null
                || delta.Metadata.SchemaString != schemaString
                || !SameConfig(delta.Metadata.Configuration, config);

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

            var stats = Stats(newAdds, schemaPlan);
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
                    PartitionValues = new Dictionary<string, string>(),
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
                    PartitionColumns = Array.Empty<string>(),
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
        private Dictionary<long, string> Stats(List<DlFile> files, SchemaPlan plan)
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
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteNumber("numRecords", f.Records);
                    var rows = perFile.TryGetValue(f.Id, out var l) ? l : new List<object?[]>();
                    var tops = rows.Where(r => plan.TopLevelPhysical(Long(r[1])) is not null).ToList();
                    WriteBound(w, "minValues", tops, plan, isMin: true);
                    WriteBound(w, "maxValues", tops, plan, isMin: false);
                    w.WriteStartObject("nullCount");
                    foreach (var r in tops)
                    {
                        if (r[2] is not null)
                        {
                            w.WriteNumber(plan.TopLevelPhysical(Long(r[1]))!, Long(r[2]));
                        }
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                result[f.Id] = Encoding.UTF8.GetString(ms.ToArray());
            }
            return result;
        }

        private static void WriteBound(Utf8JsonWriter w, string name, List<object?[]> rows, SchemaPlan plan, bool isMin)
        {
            w.WriteStartObject(name);
            foreach (var r in rows)
            {
                long col = Long(r[1]);
                string? value = (string?)r[isMin ? 3 : 4];
                bool exact = r[isMin ? 6 : 7] is null || Bool(r[isMin ? 6 : 7]);
                bool nan = r[5] is not null && Bool(r[5]);
                if (value is null || !exact)
                {
                    continue;
                }
                string phys = plan.TopLevelPhysical(col)!;
                switch (plan.TypeAtCurrent(col) ?? "")
                {
                    case "int8" or "int16" or "int32" or "int64":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
                        {
                            w.WriteNumber(phys, lv);
                        }
                        break;
                    case "float32" or "float64":
                        if (!nan && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
                            && !double.IsNaN(dv) && !double.IsInfinity(dv))
                        {
                            w.WriteNumber(phys, dv);
                        }
                        break;
                    case var t when t.StartsWith("decimal(", StringComparison.Ordinal):
                        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                        {
                            w.WritePropertyName(phys);
                            w.WriteRawValue(value);
                        }
                        break;
                    case "varchar" or "date":
                        w.WriteString(phys, value);
                        break;
                }
            }
            w.WriteEndObject();
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
                             "one physical name must cover every file. Rewrite the old files (DuckLake compaction) or mirror " +
                             "with column_mapping := 'id'");
            }
            else
            {
                physical = namesInFiles.Count == 1 ? namesInFiles.First() : c.Name;
                if (_mode == "none" && physical != c.Name)
                {
                    throw Refuse($"column '{c.Name}' was renamed from '{physical}' and live data files still use the old " +
                                 "name; without column mapping a Delta reader looks for the new name and finds nothing. " +
                                 "Rewrite the old files (DuckLake compaction) or mirror with column_mapping := 'name'");
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
                        if (At(c.Id, f.Begin) is not null)
                        {
                            continue;
                        }
                        var clash = _all.FirstOrDefault(o => o.Id != c.Id && o.Parent == c.Parent && o.ActiveAt(f.Begin)
                                                             && string.Equals(o.Name, physical, StringComparison.Ordinal));
                        if (clash is not null)
                        {
                            throw Refuse($"column '{c.Name}' reuses the name of a dropped column whose values are still in " +
                                         $"live data files; in '{_mode}' column mapping a Delta reader would return those old " +
                                         "values for it. Rewrite the old files or mirror with column_mapping := 'id'");
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

        public string? TopLevelPhysical(long id) =>
            _active.FirstOrDefault(c => c.Id == id && c.Parent is null) is { } c && IsPrimitive(c.Type) ? _physical[id] : null;

        public string? TypeAtCurrent(long id) => _active.FirstOrDefault(c => c.Id == id)?.Type;

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

        public ProtocolAction Protocol(ProtocolAction? existing)
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

        public Dictionary<string, string> Configuration(IReadOnlyDictionary<string, string>? existing)
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

    private string M(string table) => $"{Q(_meta)}.{Q(_ms)}.{Q(table)}";

    private string L(string schema, string table) => $"{Q(_lake)}.{Q(schema)}.{Q(table)}";

    private static string Active(string alias, long s)
    {
        string p = alias.Length == 0 ? "" : alias + ".";
        return $"{p}begin_snapshot <= {s} AND ({p}end_snapshot IS NULL OR {p}end_snapshot > {s})";
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    private static long Long(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static long? NLong(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static bool Bool(object? v) => v switch
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

    private List<object?[]> Rows(string sql)
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
