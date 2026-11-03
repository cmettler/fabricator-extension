// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Log;
using EngineeredWood.DeltaLake.Table;
using Microsoft.Extensions.Logging;

namespace Fabricator.Bridge;

/// <summary>
/// <c>ducklake_delta_maintain(lake [, table_name := 'schema.table'] [, merge := true] [, rewrite_deletes := false]
/// [, older_than := '7 days'] [, orphans := false] [, dry_run := false] [, column_mapping / deletion_vectors /
/// timestamp_mapping / metadata_schema / partitioning / change_data_feed as for ducklake_delta_sync])</c> — DuckLake's maintenance in the one order
/// that keeps a Delta mirror valid (docs/ducklake-catalog.md §11.10):
/// <list type="number">
/// <item>flush inlined rows, so the newest snapshot is complete;</item>
/// <item>merge adjacent files (<c>merge</c>) and fold deletes into new files (<c>rewrite_deletes</c>);</item>
/// <item>sync the Delta log (<c>ducklake_delta_sync</c>), so it no longer lists the files the merge replaced;</item>
/// <item>with <c>older_than</c>: expire older snapshots, then delete the files DuckLake scheduled for deletion —
/// refused while a Delta log still lists one of them (a sync that is behind);</item>
/// <item>with <c>orphans</c>: delete Parquet/puffin files under data_path that neither DuckLake nor a Delta log
/// knows, older than <c>older_than</c>. DuckLake's own orphan cleanup cannot do this for a mirrored lake: it is
/// off while the global auto_compact is false, which a mirror needs, and it would delete our checkpoints.</item>
/// </list>
/// One row per step and object. <c>dry_run</c> applies to the deleting steps (4 and 5); flush, merge and sync always run.
/// </summary>
public sealed class DuckLakeDeltaMaintainFunction : ITableFunction
{
    public string Name => "ducklake_delta_maintain";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("lake", StringType.Default, nullable: false),
        Params.Named("table_name", StringType.Default),
        Params.Named("merge", BooleanType.Default),
        Params.Named("rewrite_deletes", BooleanType.Default),
        Params.Named("older_than", StringType.Default),
        Params.Named("orphans", BooleanType.Default),
        Params.Named("dry_run", BooleanType.Default),
        Params.Named("column_mapping", StringType.Default),
        Params.Named("deletion_vectors", BooleanType.Default),
        Params.Named("timestamp_mapping", StringType.Default),
        Params.Named("metadata_schema", StringType.Default),
        Params.Named("partitioning", BooleanType.Default),
        Params.Named("change_data_feed", BooleanType.Default),
    }, null);

    internal static readonly Schema ResultSchema = new(new[]
    {
        new Field("step", StringType.Default, nullable: false),
        new Field("object", StringType.Default, nullable: false),
        new Field("count", Int64Type.Default, nullable: false),
        new Field("detail", StringType.Default, nullable: false),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args)
    {
        var o = new Options
        {
            Lake = FabricArgs.Str(args, 0)
                ?? throw new ArgumentException("ducklake_delta_maintain: the lake (an attached DuckLake catalog) is required."),
            Table = FabricArgs.Str(args, 1),
            Merge = FabricArgs.Bool(args, 2) ?? true,
            RewriteDeletes = FabricArgs.Bool(args, 3) ?? false,
            OlderThan = FabricArgs.Str(args, 4),
            Orphans = FabricArgs.Bool(args, 5) ?? false,
            DryRun = FabricArgs.Bool(args, 6) ?? false,
            Mapping = (FabricArgs.Str(args, 7) ?? "name").Trim().ToLowerInvariant(),
            DeletionVectors = FabricArgs.Bool(args, 8) ?? true,
            Timestamps = (FabricArgs.Str(args, 9) ?? "utc").Trim().ToLowerInvariant(),
            MetadataSchema = FabricArgs.Str(args, 10),
            Partitioning = FabricArgs.Bool(args, 11) ?? true,
            ChangeDataFeed = FabricArgs.Bool(args, 12) ?? false,
        };
        if (o.Mapping is not ("name" or "id" or "none"))
        {
            throw new ArgumentException($"ducklake_delta_maintain: column_mapping must be 'name', 'id' or 'none', not '{o.Mapping}'.");
        }
        if (o.Timestamps is not ("ntz" or "utc"))
        {
            throw new ArgumentException($"ducklake_delta_maintain: timestamp_mapping must be 'ntz' or 'utc', not '{o.Timestamps}'.");
        }
        if (o.Orphans && o.OlderThan is null)
        {
            // A file another process is writing right now is unknown to DuckLake until it commits; only its age
            // separates it from an orphan. DuckLake's own orphan cleanup has the same rule (older_than or cleanup_all).
            throw new ArgumentException(
                "ducklake_delta_maintain: orphans := true needs older_than (e.g. older_than := '1 day'): a file another " +
                "process is still writing is unknown to the lake until it commits, and only its age tells it from an orphan.");
        }
        return new MaintainBinding(o);
    }

    internal sealed class Options
    {
        public string Lake = "";
        public string? Table;
        public bool Merge;
        public bool RewriteDeletes;
        public string? OlderThan;
        public bool Orphans;
        public bool DryRun;
        public string Mapping = "name";
        public bool DeletionVectors = true;
        public string Timestamps = "utc";
        public string? MetadataSchema;
        public bool Partitioning = true;
        public bool ChangeDataFeed;
    }

    private sealed class MaintainBinding : ITableFunctionBinding
    {
        private readonly Options _o;

        public MaintainBinding(Options o) => _o = o;

        public Schema OutputSchema => ResultSchema;
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            // The work runs NOW, while the opener (this call's ClientContext) is valid — as in ducklake_delta_sync.
            AmbientAdlsCredential.Current = null;
            var rows = new DuckLakeDeltaMaintain(AmbientOpener.Current, _o).Run(ct);
            return Single(DuckLakeDeltaMaintain.ToBatch(rows));
        }

        private static async IAsyncEnumerable<RecordBatch> Single(RecordBatch batch)
        {
            yield return batch;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose() { }
    }
}

internal sealed class DuckLakeDeltaMaintain
{
    private static readonly ILogger _log = FabricatorLog.CreateLogger("Fabricator.DuckLake");

    internal sealed record Row(string Step, string Object, long Count, string Detail);

    private readonly nint _opener;
    private readonly DuckLakeDeltaMaintainFunction.Options _o;
    private readonly List<Row> _rows = new();

    public DuckLakeDeltaMaintain(nint opener, DuckLakeDeltaMaintainFunction.Options o)
    {
        _opener = opener;
        _o = o;
    }

    private static string Q(string s) => DuckLakeDeltaSync.Q(s);

    private static string Lit(string s) => DuckLakeDeltaSync.Lit(s);

    private static List<object?[]> Query(string sql) => DuckLakeDeltaSync.Query(sql);

    private void Add(string step, string obj, long count, string detail)
    {
        _rows.Add(new Row(step, obj, count, detail));
        _log.LogInformation("ducklake maintain {Lake}: {Step} {Object}: {Count} ({Detail})", _o.Lake, step, obj, count, detail);
    }

    public List<Row> Run(CancellationToken ct)
    {
        string lake = Lit(_o.Lake);
        string? cutoff = null;
        if (_o.OlderThan is not null)
        {
            // Validated here, before anything changes: DuckDB's own interval parser, the one older_than goes through.
            cutoff = $"(now() - CAST({Lit(_o.OlderThan)} AS INTERVAL))";
            Query($"SELECT {cutoff} IS NOT NULL");
        }
        var sync = new DuckLakeDeltaSync(_opener, _o.Lake, _o.Mapping, _o.Timestamps, _o.DeletionVectors, _o.Partitioning,
            _o.ChangeDataFeed);
        var (dataPath, checkpointsSafe, _, tables) = sync.Describe(_o.MetadataSchema);
        var targets = tables.Where(t => _o.Table is null || DuckLakeDeltaSync.MatchesFilter(_o.Table, t.Schema, t.Table)).ToList();
        if (_o.Table is not null && targets.Count == 0)
        {
            throw new InvalidOperationException($"ducklake_delta_maintain: no table '{_o.Table}' in lake '{_o.Lake}'.");
        }
        if (!checkpointsSafe)
        {
            Add("config", "", 0, "the global auto_compact is not false: a DuckLake CHECKPOINT would delete Delta checkpoints, " +
                "so the sync writes none (docs/ducklake-catalog.md §11.3)");
        }

        // 1-2: flush, merge, rewrite — per table, and only where DuckLake would do them: with the global auto_compact
        // false, a table needs auto_compact true on itself or its schema, or DuckLake skips these calls WITHOUT a word
        // (measured: rows_flushed empty while 10 rows sat inlined).
        var compact = AutoCompact();
        foreach (var t in targets)
        {
            string name = $"{t.Schema}.{t.Table}";
            if (!compact(t.Schema, t.Table))
            {
                Add("flush", name, 0, "skipped: auto_compact is false for this table, so DuckLake would not flush, merge or " +
                    $"rewrite it; CALL {Q(_o.Lake)}.set_option('auto_compact', true, schema => {Lit(t.Schema)})");
                continue;
            }
            long flushed = Query($"SELECT CAST(rows_flushed AS BIGINT) FROM ducklake_flush_inlined_data({lake}, schema_name => {Lit(t.Schema)}, " +
                $"table_name => {Lit(t.Table)})").Sum(r => DuckLakeDeltaSync.Long(r[0]));
            Add("flush", name, flushed, "rows moved from the metadata into Parquet");
            if (_o.Merge)
            {
                var m = Query($"SELECT CAST(files_processed AS BIGINT), CAST(files_created AS BIGINT) FROM ducklake_merge_adjacent_files({lake}, {Lit(t.Table)}, " +
                    $"schema => {Lit(t.Schema)})");
                Add("merge", name, m.Sum(r => DuckLakeDeltaSync.Long(r[0])),
                    $"files merged into {m.Sum(r => DuckLakeDeltaSync.Long(r[1]))}");
            }
            if (_o.RewriteDeletes)
            {
                var w = Query($"SELECT CAST(files_processed AS BIGINT), CAST(files_created AS BIGINT) FROM ducklake_rewrite_data_files({lake}, {Lit(t.Table)}, " +
                    $"delete_threshold => 0, schema => {Lit(t.Schema)})");
                Add("rewrite", name, w.Sum(r => DuckLakeDeltaSync.Long(r[0])),
                    $"files with deletes rewritten into {w.Sum(r => DuckLakeDeltaSync.Long(r[1]))}");
            }
        }

        // 3: the Delta log follows, so it lists the merged files instead of the ones they replaced.
        foreach (var r in sync.Run(_o.Table, _o.MetadataSchema, ct))
        {
            Add("sync", $"{r.Schema}.{r.Table}", r.Version,
                $"snapshot {r.Snapshot}: +{r.Added} -{r.Removed} files, {r.Dvs} deletion vectors{(r.Checkpoint ? ", checkpoint" : "")}");
        }

        if (cutoff is null)
        {
            return _rows;
        }
        string dry = _o.DryRun ? "true" : "false";

        // 4: expire, then delete what DuckLake scheduled — lake-wide, as DuckLake's functions are.
        long expired = Query($"SELECT CAST(count(*) AS BIGINT) FROM ducklake_expire_snapshots({lake}, older_than => {cutoff}, dry_run => {dry})")
            .Sum(r => DuckLakeDeltaSync.Long(r[0]));
        Add("expire", _o.Lake, expired, _o.DryRun ? "snapshots that would expire" : "snapshots expired");

        var deltaFiles = DeltaActiveFiles(tables, ct);
        var scheduled = Query($"SELECT path FROM ducklake_cleanup_old_files({lake}, older_than => {cutoff}, dry_run => true)")
            .Select(r => (string)r[0]!).ToList();
        var stillListed = scheduled.Where(p => deltaFiles.Contains(Canonical(p))).ToList();
        if (stillListed.Count > 0)
        {
            // A sync that is behind (an incomplete newest snapshot, or live deletes under deletion_vectors := false)
            // still lists files DuckLake has scheduled; deleting them would leave the Delta log pointing at nothing.
            Add("cleanup", _o.Lake, 0, $"skipped: a Delta log still lists {stillListed.Count} of the {scheduled.Count} " +
                $"scheduled files (e.g. {stillListed[0]}) — the sync is behind; rerun after it caught up");
        }
        else
        {
            long cleaned = scheduled.Count;
            if (!_o.DryRun && cleaned > 0)
            {
                cleaned = Query($"SELECT CAST(count(*) AS BIGINT) FROM ducklake_cleanup_old_files({lake}, older_than => {cutoff})")
                    .Sum(r => DuckLakeDeltaSync.Long(r[0]));
            }
            Add("cleanup", _o.Lake, cleaned, _o.DryRun ? "scheduled files that would be deleted" : "scheduled files deleted");
        }

        // 5: orphans — our own rule, DuckLake's minus what a Delta log owns.
        if (_o.Orphans)
        {
            Orphans(sync, dataPath, cutoff, deltaFiles);
        }
        return _rows;
    }

    /// <summary>Effective auto_compact per table: the table's own option, else its schema's, else the global one,
    /// else true (DuckLake's default).</summary>
    private Func<string, string, bool> AutoCompact()
    {
        bool? global = null;
        var schemas = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Query($"SELECT value, scope, scope_entry FROM ducklake_options({Lit(_o.Lake)}) WHERE option_name = 'auto_compact'"))
        {
            bool v = string.Equals((string?)r[0], "true", StringComparison.OrdinalIgnoreCase);
            switch (((string?)r[1] ?? "").ToUpperInvariant())
            {
                case "GLOBAL": global = v; break;
                case "SCHEMA": schemas[(string?)r[2] ?? ""] = v; break;
                case "TABLE": tables[(string?)r[2] ?? ""] = v; break;
            }
        }
        return (schema, table) => tables.TryGetValue($"{schema}.{table}", out var t) ? t
            : schemas.TryGetValue(schema, out var s) ? s
            : global ?? true;
    }

    /// <summary>Every file the newest version of any table's Delta log lists, canonical.</summary>
    private HashSet<string> DeltaActiveFiles(List<DuckLakeDeltaSync.LakeTable> tables, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            var fs = TableFileSystems.Create(_opener, t.Root);
            if (new TransactionLog(fs).GetLatestVersionAsync(ct).AsTask().GetAwaiter().GetResult() < 0)
            {
                continue;
            }
            using var table = DeltaTable.OpenAsync(fs, null, ct).AsTask().GetAwaiter().GetResult();
            foreach (var a in table.CurrentSnapshot.ActiveFiles.Values)
            {
                files.Add(Canonical(t.Root + "/" + Uri.UnescapeDataString(a.Path)));
            }
        }
        return files;
    }

    private void Orphans(DuckLakeDeltaSync sync, string dataPath, string cutoff, HashSet<string> deltaFiles)
    {
        string dp = Lit(dataPath);
        string M(string table) => sync.M(table);
        // DuckLake's own "known files" (ducklake_metadata_manager.cpp, GetKnownFilesForCleanupQuery): every data and
        // delete file of any snapshot, and every file scheduled for deletion, resolved against schema/table paths.
        string known =
            "SELECT CASE WHEN NOT f.path_is_relative THEN f.path " +
            "WHEN NOT t.path_is_relative THEN t.path || f.path " +
            "WHEN NOT s.path_is_relative THEN s.path || t.path || f.path " +
            $"ELSE {dp} || s.path || t.path || f.path END AS p " +
            $"FROM (SELECT path, path_is_relative, table_id FROM {M("ducklake_data_file")} " +
            $"UNION ALL SELECT path, path_is_relative, table_id FROM {M("ducklake_delete_file")}) f " +
            $"JOIN {M("ducklake_table")} t ON t.table_id = f.table_id JOIN {M("ducklake_schema")} s ON s.schema_id = t.schema_id " +
            $"UNION ALL SELECT CASE WHEN NOT path_is_relative THEN path ELSE {dp} || path END FROM {M("ducklake_files_scheduled_for_deletion")}";
        var knownSet = new HashSet<string>(Query(known).Select(r => Canonical((string)r[0]!)), StringComparer.Ordinal);
        knownSet.UnionWith(deltaFiles);
        var candidates = Query(
            $"SELECT filename FROM read_blob({Lit(dataPath + "**")}) WHERE (suffix(filename, '.parquet') OR suffix(filename, '.puffin')) " +
            $"AND last_modified::TIMESTAMPTZ < {cutoff}").Select(r => (string)r[0]!);
        var orphans = new List<string>();
        foreach (var path in candidates)
        {
            string c = Canonical(path);
            // What a Delta log owns: checkpoints in _delta_log, change data in _change_data. DuckLake knows neither.
            if (knownSet.Contains(c) || c.Contains("/_delta_log/", StringComparison.Ordinal)
                || c.Contains("/_change_data/", StringComparison.Ordinal))
            {
                continue;
            }
            orphans.Add(path);
        }
        if (!_o.DryRun)
        {
            foreach (var path in orphans)
            {
                HostFs.Remove(_opener, path);
            }
        }
        Add("orphans", _o.Lake, orphans.Count, (_o.DryRun ? "files that would be deleted" : "files deleted")
            + (orphans.Count > 0 ? ": " + string.Join(", ", orphans.Take(5)) + (orphans.Count > 5 ? ", …" : "") : ""));
    }

    /// <summary>One spelling per file: forward slashes, a local path made absolute and, on Windows, case-folded.</summary>
    private static string Canonical(string path)
    {
        string p = path.Replace('\\', '/');
        if (p.Contains("://", StringComparison.Ordinal))
        {
            int q = p.IndexOf('?');
            return q < 0 ? p : p.Substring(0, q);
        }
        p = System.IO.Path.GetFullPath(p).Replace('\\', '/');
        return OperatingSystem.IsWindows() ? p.ToLowerInvariant() : p;
    }

    internal static RecordBatch ToBatch(List<Row> rows)
    {
        var step = new StringArray.Builder();
        var obj = new StringArray.Builder();
        var count = new Int64Array.Builder();
        var detail = new StringArray.Builder();
        foreach (var r in rows)
        {
            step.Append(r.Step);
            obj.Append(r.Object);
            count.Append(r.Count);
            detail.Append(r.Detail);
        }
        return new RecordBatch(DuckLakeDeltaMaintainFunction.ResultSchema,
            new IArrowArray[] { step.Build(), obj.Build(), count.Build(), detail.Build() }, rows.Count);
    }
}
