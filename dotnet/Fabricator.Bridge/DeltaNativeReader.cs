using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using EngineeredWood.DeltaLake.Table;
using EngineeredWood.Expressions;
using Microsoft.Extensions.Logging;
using DeltaSchema = EngineeredWood.DeltaLake.Schema;

namespace Fabricator.Bridge;

/// <summary>
/// The native Delta reader: C# lists the table's exact active data files (via <see cref="DeltaReader"/>) and
/// DuckDB's own <c>read_parquet</c> does the decode — one query per file on a fresh host connection
/// (<see cref="Host.Query"/>), tuned + <c>ExternalFileCache</c>-backed, over <c>onelake://</c> for OneLake. Per
/// file it pushes the <b>static filter</b> into the <c>read_parquet WHERE</c> (row-group pruning), excludes the
/// file's <b>deletion vector</b>, projects the requested columns, and computes the transient
/// <c>_metadata.row_id = (fileOrdinal &lt;&lt; 40) | file_row_number</c> when requested — so DELETE/UPDATE work
/// natively (no fallback to the engineered-wood reader). Files are read with a bounded prefetch
/// (<c>FABRICATOR_DELTA_PREFETCH</c>, default 1 = sequential; &gt;1 = concurrent file fetch — the cloud-I/O win).
///
/// <para>The per-file loop is the decision point that the single <c>read_parquet([list])</c> lacks: Delta-log
/// FILE pruning (skip a file whose stats can't match) + early-stop. It keeps <c>filter_pushdown = false</c>
/// (superset-safe; DuckDB re-applies every predicate above the scan), so a partial WHERE only forfeits pruning.
/// Dynamic (join) filter pushdown at this decision point is a later slice (a live-filter host callback).</para>
/// </summary>
internal static class DeltaNativeReader
{
    // One source of truth for the DuckDB-facing virtual rowid name (see DeltaCatalog.RowIdColumn for why
    // it is deliberately NOT engineered-wood's TransientRowAddress.ColumnName).
    private const string RowIdColumn = DeltaCatalog.RowIdColumn;

    /// <summary>The STABLE row-tracking id virtual column (the Delta materialized-column name): per row
    /// <c>COALESCE(materialized __delta_row_id, baseRowId + position)</c>. Advertised (and served) only on
    /// native_read catalogs for tables with <c>delta.enableRowTracking</c>; NULL for a transaction's pending
    /// (uncommitted) files — baseRowId is assigned at commit.</summary>
    internal const string RowTrackingIdColumn = "__delta_row_id";

    /// <summary>The stable per-row commit version (materialized __delta_row_commit_version, else the file's
    /// defaultRowCommitVersion). Same gating as <see cref="RowTrackingIdColumn"/>.</summary>
    internal const string RowTrackingVersionColumn = "__delta_row_commit_version";
    private static readonly ILogger Log = FabricatorLog.CreateLogger("Fabricator.Delta.Native");

    /// <summary>Builds the Arrow stream for a native Delta scan. <paramref name="unit"/>/<paramref name="value"/>
    /// = the resolved time-travel/pinned snapshot ("version"/"timestamp"), or null for latest.</summary>
    public static IArrowArrayStream Read(
        nint opener, string path, Schema userSchema, ScanSpec? spec,
        IReadOnlyList<object?> filterValues, string? unit, string? value,
        IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile>? pendingFiles = null,
        IReadOnlyDictionary<int, HashSet<long>>? pendingDeletes = null,
        EngineeredWood.DeltaLake.Schema.StructType? pendingSchema = null)
    {
        bool wantRowId = spec?.Columns is { } c0 && c0.Contains(RowIdColumn);
        var dataCols = spec?.Columns is { Count: > 0 } cols
            ? cols.Where(c => c != RowIdColumn).ToList()
            : userSchema.FieldsList.Select(f => f.Name).ToList();

        // ROWID fast path: a filter on `_metadata.row_id` (late-materialization semi join / WHERE rowid=…)
        // decodes EXACTLY — ordinal half → file selection, position half → per-file file_row_number predicate
        // (parquet row-group skip). The rowid conjuncts are STRIPPED from the engineered-wood prune tree
        // (it has no rowid stats; dropping a conjunct only widens = superset-safe).
        var rowIdFilter = DeltaRowIdFilter.Extract(spec?.Filter, filterValues, RowIdColumn);
        // STABLE-ID fast path: filters on the row-tracking virtual columns skip whole files (derived-id
        // ranges from the log; per-file constant versions) and push zone-map-prunable conditions into the
        // per-file query (see DeltaRowTrackingFilter). Decided per file AFTER the footer probe (materialized
        // presence is per file), so the skip happens in the pump.
        var trackingFilter = DeltaRowTrackingFilter.Extract(
            spec?.Filter, filterValues, RowTrackingIdColumn, RowTrackingVersionColumn);
        var pruneNode = spec?.Filter;
        if (rowIdFilter is not null)
        {
            pruneNode = DeltaRowIdFilter.Strip(pruneNode, RowIdColumn);
        }
        if (trackingFilter is not null)
        {
            // The Delta log has no stats for the row-tracking columns — strip their conjuncts so the
            // engineered-wood pruner keeps working on the rest (dropping a conjunct only widens).
            pruneNode = DeltaRowTrackingFilter.Strip(pruneNode, RowTrackingIdColumn, RowTrackingVersionColumn);
        }

        // Static filter → engineered-wood predicate (Delta-log FILE pruning) + SQL WHERE (read_parquet row-group pruning).
        Predicate? prune = null;
        if (pruneNode is { } node)
        {
            try
            {
                prune = new DeltaFilterBuilder(filterValues).Build(node);
            }
            catch
            {
                prune = null; // unbuildable shape (e.g. rowid inside an OR): forfeit file pruning, never correctness
            }
        }
        // Prefer the host's 1:1 native SQL rendering (literals inlined, DuckDB self-render → exact). It carries the
        // SAME superset-safe predicates as spec.Filter, so it's correctness-neutral (DuckDB re-applies above the
        // scan). Fall back to translating the FilterNode ourselves when the host didn't emit one.
        string? where = !string.IsNullOrEmpty(spec?.NativeFilter)
            ? spec!.NativeFilter
            : spec?.Filter is { } node2 ? DeltaSqlFilter.ToWhere(node2, filterValues) : null;

        var listing = DeltaReader.ListNativeScanFiles(opener, path, unit, value, prune, Log,
                                                      schemaOverride: pendingSchema);
        if (pendingFiles is { Count: > 0 })
        {
            // Read-your-writes: this transaction's streamed-but-uncommitted files join the per-file loop
            // (same probe / WHERE / DV=none mechanics as committed files — they're real parquet on storage,
            // Hive-layout included). High disjoint ordinals: no collision with active files or the buffered-
            // batch overlay's 0x700000 base, and in-transaction DML is rejected anyway.
            listing = WithPendingFiles(listing, path, pendingFiles);
        }
        if (pendingDeletes is { Count: > 0 })
        {
            // Read-your-writes for buffered DML: the transaction's pending-DELETEd positions join each
            // file's DV exclusion (ordinals are pinned-snapshot ordinals — the caller pinned this scan to
            // exactly that version).
            listing = WithPendingDeletes(listing, pendingDeletes);
        }
        if (rowIdFilter is not null)
        {
            // Exact file selection by the rowid's ordinal half — no stats, no I/O; applies uniformly to
            // committed AND pending-file ordinals (one encoding). Position bounds land per file below.
            var kept = listing.Files.Where(f => rowIdFilter.OrdinalMayMatch(f.Ordinal)).ToList();
            if (kept.Count != listing.Files.Count)
            {
                Log.LogInformation("delta native rowid prune {Path}: files {Before} -> {After}",
                                   path, listing.Files.Count, kept.Count);
                listing = WithFiles(listing, kept);
            }
        }
        var schema = ProbeSchema(listing, userSchema, dataCols, wantRowId);
        int prefetch = Prefetch();

        Log.LogInformation(
            "delta native scan {Path}: v{Version} files={Files} cols=[{Cols}] rowid={RowId} where=[{Where}] prefetch={Prefetch} colmap={Map}",
            path, listing.Version, listing.Files.Count, string.Join(",", dataCols), wantRowId, where ?? "", prefetch,
            listing.LogicalToPhysical is not null ? "name" : listing.LogicalToFieldId is not null ? "id" : "none");

        return new AsyncEnumerableArrowStream(
            schema, StreamFiles(listing, dataCols, wantRowId, where, prefetch, rowIdFilter, trackingFilter));
    }

    // Merges the transaction's pending-DELETEd positions into the per-file DV exclusion lists (positions
    // keyed by the same pinned-snapshot global ordinal the listing carries).
    private static DeltaReader.NativeScanList WithPendingDeletes(
        DeltaReader.NativeScanList listing, IReadOnlyDictionary<int, HashSet<long>> pendingDeletes)
    {
        var files = new List<DeltaReader.NativeScanFile>(listing.Files.Count);
        foreach (var f in listing.Files)
        {
            if (pendingDeletes.TryGetValue(f.Ordinal, out var extra) && extra.Count > 0)
            {
                var merged = new HashSet<long>(f.Dv);
                merged.UnionWith(extra);
                var arr = new long[merged.Count];
                merged.CopyTo(arr);
                System.Array.Sort(arr);
                files.Add(f with { Dv = arr });
            }
            else
            {
                files.Add(f);
            }
        }
        return WithFiles(listing, files);
    }

    // Clones the listing with a different file list, keeping every snapshot-derived property.
    private static DeltaReader.NativeScanList WithFiles(
        DeltaReader.NativeScanList listing, List<DeltaReader.NativeScanFile> files) =>
        new()
        {
            Version = listing.Version,
            Files = files,
            AnyUri = listing.AnyUri ?? (files.Count > 0 ? files[files.Count - 1].Uri : null),
            LogicalToPhysical = listing.LogicalToPhysical,
            LogicalToFieldId = listing.LogicalToFieldId,
            MappedSchema = listing.MappedSchema,
            TableSchema = listing.TableSchema,
            PartitionColumns = listing.PartitionColumns,
        };

    // Appends the transaction's pending (uncommitted) streamed files to the committed listing, keeping all
    // snapshot-derived properties (mapping, schema). Ordinal base 0x780000 — disjoint from real file ordinals
    // AND the buffered-batch synthetic rowid base (0x700000), so a count(*)-via-rowid scan stays unique.
    private static DeltaReader.NativeScanList WithPendingFiles(
        DeltaReader.NativeScanList listing, string path,
        IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile> pendingFiles)
    {
        var root = DeltaReader.ToReadableRoot(path);
        var files = new List<DeltaReader.NativeScanFile>(listing.Files.Count + pendingFiles.Count);
        files.AddRange(listing.Files);
        for (int i = 0; i < pendingFiles.Count; i++)
        {
            var uri = root + "/" + pendingFiles[i].RelativePath.Replace('\\', '/').TrimStart('/');
            files.Add(new DeltaReader.NativeScanFile(0x780000 + i, uri, System.Array.Empty<long>(),
                pendingFiles[i].PartitionValues is { Count: > 0 } pv ? pv : null));
        }
        return WithFiles(listing, files);
    }

    // Everything the per-file SQL needs to know about ONE data file: the top-level logical→physical alias
    // map (column mapping), and this file's ACTUAL parquet schema nodes (paths + field ids, footer-probed) —
    // driving stored-name resolution (every vintage/layout) AND per-file PRESENCE: a column/member the file
    // predates (schema evolution) is emitted as a typed NULL instead of a mis-binding reference.
    private readonly record struct FileMapping(
        IReadOnlyDictionary<string, string>? LogToPhys,
        FileNodes Nodes);

    private const char PathSep = ''; // joins stored-name path segments (names may contain dots)

    // The per-file expression for a stable row-tracking virtual column. The materialized physical column
    // (footer-probed presence; stored under its literal name — materialized columns are not column-mapped)
    // wins per row where non-NULL; else baseRowId + file_row_number (id) / the constant
    // defaultRowCommitVersion (version); a file with neither (pending/no row tracking) reads NULL.
    private static string RowTrackingExpr(string name, DeltaReader.NativeScanFile f, FileMapping fm)
    {
        bool isId = string.Equals(name, RowTrackingIdColumn, StringComparison.Ordinal);
        string? derived = isId
            ? f.BaseRowId is { } b
                ? $"(CAST({b.ToString(CultureInfo.InvariantCulture)} AS BIGINT) + file_row_number)" : null
            : f.CommitVersion is { } v
                ? $"CAST({v.ToString(CultureInfo.InvariantCulture)} AS BIGINT)" : null;
        bool hasMaterialized = fm.Nodes.Paths.Contains(name);
        if (hasMaterialized)
        {
            return derived is null ? Quote(name) : $"COALESCE({Quote(name)}, {derived})";
        }
        return derived ?? "CAST(NULL AS BIGINT)";
    }

    private static bool ContainsName(IReadOnlyList<string> names, string c)
    {
        foreach (var n in names)
        {
            if (string.Equals(n, c, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // A file's partition value for a column: try the LOGICAL key then the PHYSICAL (stored) key —
    // new mapped commits key physical, old EW commits logical. Missing key / the Hive null-dir
    // sentinel => SQL NULL.
    internal static string? LookupPartitionValue(IReadOnlyDictionary<string, string>? values,
                                                 string logical, string stored)
    {
        if (values is null)
        {
            return null;
        }
        if (!values.TryGetValue(logical, out var v) && !values.TryGetValue(stored, out v))
        {
            return null;
        }
        return v is null || v.Length == 0 || v == "__HIVE_DEFAULT_PARTITION__" ? null : v;
    }

    // The per-file SELECT (ordinal folded into the rowid expression); file_row_number is read but only surfaces
    // as _metadata.row_id (and drives the DV exclusion) — never as an output column.
    private static string FileSql(IReadOnlyList<string> dataCols, bool wantRowId, string? where,
                                  DeltaReader.NativeScanFile f, FileMapping fm,
                                  DeltaSchema.StructType? tableSchema,
                                  IReadOnlyList<string>? partitionCols = null,
                                  string? rowIdCond = null,
                                  string? innerCond = null)
    {
        // Per-column projection over THIS file's actual layout:
        //   • column mapping: alias the stored PHYSICAL name to the logical one; a mapped STRUCT whose shape
        //     differs from the file (renamed/added/dropped members) is REBUILT with logical member names
        //     (RebuildExpr) so a pushed struct-member predicate binds;
        //   • schema evolution: a column/member ADDed after this file was written is emitted as
        //     CAST(NULL AS <type>) (presence via the footer-probed node paths/field ids); a DROPped member
        //     disappears because the rebuild projects only the CURRENT members.
        // The OUTER projection, user filter, rowid and DV condition all reference logical names unchanged.
        var inner = new List<string>(dataCols.Count + 1);
        bool needsInner = false;
        foreach (var c in dataCols)
        {
            var field = FindField(tableSchema, c);
            string stored = field is not null
                ? StoredChildName(field, fm)
                : fm.LogToPhys is { } m && m.TryGetValue(c, out var p) ? p : c;
            string expr;
            if (string.Equals(c, RowTrackingIdColumn, StringComparison.Ordinal)
                || string.Equals(c, RowTrackingVersionColumn, StringComparison.Ordinal))
            {
                // Stable row-tracking virtual columns: derived per file from the add action's
                // baseRowId/defaultRowCommitVersion, overridden by the materialized physical column when THIS
                // file carries one (merge-on-read/buffered-UPDATE post-images, compacted files,
                // external Spark writers). Pending (uncommitted) files have no baseRowId yet => NULL.
                expr = RowTrackingExpr(c, f, fm);
                needsInner = true;
            }
            else if (partitionCols is not null && field is not null && ContainsName(partitionCols, c))
            {
                // Partition columns are ABSENT from the data files — the log's per-file partitionValues
                // is the authoritative source (paths are opaque; the presence probe would otherwise
                // NULL-backfill them like schema evolution). Keys are PHYSICAL under column mapping
                // (new commits) or logical (old EW commits) — dual lookup, sentinel/missing => NULL.
                string? pv = LookupPartitionValue(f.PartitionValues, c, stored);
                expr = pv is null
                    ? $"CAST(NULL AS {TypeText(field.Type)})"
                    : $"CAST('{pv.Replace("'", "''")}' AS {TypeText(field.Type)})";
                needsInner = true;
            }
            else if (field is not null && !Present(field, stored, fm))
            {
                expr = $"CAST(NULL AS {TypeText(field.Type)})";
                needsInner = true;
            }
            else if (field?.Type is DeltaSchema.StructType st && StructShapeDiffers(st, stored, fm))
            {
                expr = RebuildExpr(field.Type, Quote(stored), stored, fm);
                needsInner = true;
            }
            else
            {
                expr = Quote(stored);
                if (!string.Equals(stored, c, StringComparison.Ordinal))
                {
                    needsInner = true;
                }
            }
            inner.Add(string.Equals(expr, Quote(c), StringComparison.Ordinal)
                ? Quote(c)
                : $"{expr} AS {Quote(c)}");
        }

        // The row-tracking fast-path condition references RAW file columns / file_row_number, so it goes on
        // the INNER query (over read_parquet directly — inner WHERE binds source columns before SELECT
        // aliases, so a materialized "__delta_row_id" hits the physical column, not the COALESCE alias) —
        // that's what lets parquet zone maps prune row groups. Superset-safe: the outer `where` still
        // applies the exact predicate over the aliases.
        if (innerCond is not null)
        {
            needsInner = true;
        }
        string innerWhere = innerCond is null ? "" : $" WHERE {innerCond}";
        string source;
        if (!needsInner)
        {
            source = $"read_parquet('{f.Uri.Replace("'", "''")}', file_row_number => true)";
        }
        else
        {
            inner.Add("file_row_number");
            source = $"(SELECT {string.Join(", ", inner)} FROM read_parquet('{f.Uri.Replace("'", "''")}', file_row_number => true){innerWhere})";
        }

        var sb = new StringBuilder("SELECT ");
        sb.Append(dataCols.Count == 0 ? "" : string.Join(", ", dataCols.Select(Quote)));
        if (wantRowId)
        {
            if (dataCols.Count > 0)
            {
                sb.Append(", ");
            }
            sb.Append($"((CAST({f.Ordinal.ToString(CultureInfo.InvariantCulture)} AS BIGINT) << {TransientRowAddress.PositionBits}) | file_row_number) AS {Quote(RowIdColumn)}");
        }
        if (dataCols.Count == 0 && !wantRowId)
        {
            sb.Append("1"); // degenerate projection (e.g. COUNT(*) with no columns) — a constant keeps SQL valid
        }
        sb.Append($" FROM {source}");
        var conds = new List<string>(3);
        if (!string.IsNullOrEmpty(where))
        {
            conds.Add(where!);
        }
        if (!string.IsNullOrEmpty(rowIdCond))
        {
            // The decoded rowid constraint's position half: a plain file_row_number predicate, which
            // DuckDB's parquet reader prunes ROW GROUPS with (it synthesizes exact per-row-group min/max
            // for file_row_number) — unlike the aliased rowid expression in `where`, which is exact but
            // not zone-map-prunable.
            conds.Add(rowIdCond!);
        }
        if (f.Dv.Length > 0)
        {
            conds.Add($"file_row_number NOT IN ({string.Join(",", f.Dv.Select(p => p.ToString(CultureInfo.InvariantCulture)))})");
        }
        if (conds.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", conds));
        }
        return sb.ToString();
    }

    /// <summary>The WHOLE table as one SQL text: the per-file SELECTs (logical names, DV exclusion, column
    /// mapping, schema-evolution backfill, partition literals, row-tracking expressions for
    /// <see cref="RowTrackingIdColumn"/>/<see cref="RowTrackingVersionColumn"/> entries in
    /// <paramref name="dataCols"/>) joined by UNION ALL. Serves the clustered-OPTIMIZE rewrite, which needs a
    /// single query it can ORDER BY globally (DuckDB's spilling sort) and feed straight into a COPY —
    /// zero boundary crossings for the data. NOT usable for nested MAPPED columns (the per-batch
    /// <see cref="ArrowColumnMappingRename"/> has no hook inside one SQL statement — callers gate).</summary>
    internal static string FullTableSql(DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols)
    {
        var parts = new List<string>(listing.Files.Count);
        foreach (var f in listing.Files)
        {
            parts.Add(FileSql(dataCols, wantRowId: false, where: null, f,
                              ResolveFileMapping(listing, f.Uri), listing.TableSchema,
                              listing.PartitionColumns));
        }
        return string.Join(" UNION ALL ", parts);
    }

    // Advertises the EXACT read_parquet output schema (probed via LIMIT 0 over any active file), so the streamed
    // batches match by type. With no files, derives it from the user schema (+ the rowid field).
    private static Schema ProbeSchema(DeltaReader.NativeScanList listing, Schema userSchema,
                                      IReadOnlyList<string> dataCols, bool wantRowId)
    {
        if (listing.AnyUri is { } probe)
        {
            var probeFile = new DeltaReader.NativeScanFile(0, probe, System.Array.Empty<long>());
            var sql = FileSql(dataCols, wantRowId, where: null, probeFile,
                              ResolveFileMapping(listing, probe), listing.TableSchema,
                              listing.PartitionColumns) + " LIMIT 0";
            using var s = Host.Query(sql);
            // Nested mapped fields: the probed schema carries physical struct-child names — rename to logical
            // (top level is already logical via the SELECT alias; the transform passes it through).
            return listing.MappedSchema is { } ms
                ? ArrowColumnMappingRename.RenameSchema(s.Schema, ms, toPhysical: false)
                : s.Schema;
        }
        var fields = new List<Field>();
        foreach (var c in dataCols)
        {
            var f = userSchema.GetFieldByName(c);
            if (f is not null)
            {
                fields.Add(f);
            }
            else if (string.Equals(c, RowTrackingIdColumn, StringComparison.Ordinal)
                     || string.Equals(c, RowTrackingVersionColumn, StringComparison.Ordinal))
            {
                // Stable row-tracking virtual columns aren't in the user schema — synthesize their field
                // so an empty table's advertised schema still matches what the scan requested.
                fields.Add(new Field(c, Int64Type.Default, nullable: true));
            }
        }
        if (wantRowId)
        {
            fields.Add(new Field(RowIdColumn, Int64Type.Default, nullable: false));
        }
        return new Schema(fields, userSchema.Metadata);
    }

    private static async IAsyncEnumerable<RecordBatch> StreamFiles(
        DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols, bool wantRowId, string? where,
        int prefetch, DeltaRowIdFilter? rowIdFilter = null, DeltaRowTrackingFilter? trackingFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (listing.Files.Count == 0)
        {
            yield break;
        }
        // Bounded channel + a semaphore-gated pump: up to `prefetch` files fetched concurrently (default 1 =
        // sequential). Order across files is not preserved (DuckDB re-applies ORDER BY above the scan); the rowid
        // is per-file-correct regardless of read order.
        var channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(Math.Max(2, prefetch * 2))
        {
            SingleReader = true,
            SingleWriter = prefetch == 1,
        });
        var writer = channel.Writer;
        var pump = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(prefetch);
            var tasks = new List<Task>(listing.Files.Count);
            try
            {
                foreach (var f in listing.Files)
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    var file = f;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var fm = ResolveFileMapping(listing, file.Uri);
                            string? trackingCond = null;
                            if (trackingFilter is not null)
                            {
                                // Materialized presence comes from THIS file's footer probe (which the scan
                                // needs anyway): a skipped file costs the probe but never a data read.
                                trackingFilter.FileVerdict(
                                    file,
                                    idMaterialized: fm.Nodes.Paths.Contains(RowTrackingIdColumn),
                                    versionMaterialized: fm.Nodes.Paths.Contains(RowTrackingVersionColumn),
                                    out bool skipFile, out trackingCond);
                                if (skipFile)
                                {
                                    Log.LogInformation("delta native rowtracking skip {Uri}", file.Uri);
                                    return;
                                }
                            }
                            var sql = FileSql(dataCols, wantRowId, where, file,
                                              fm, listing.TableSchema,
                                              listing.PartitionColumns, rowIdFilter?.PositionCondition(file.Ordinal),
                                              trackingCond);
                            Log.LogDebug("delta native file: {Sql}", sql);
                            using var s = Host.Query(sql);
                            while (true)
                            {
                                var b = await s.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                                if (b is null)
                                {
                                    break;
                                }
                                // Nested mapped fields: rename physical struct-child names back to logical
                                // (zero-copy type-tree rewrap; top level already logical via the SELECT alias).
                                if (listing.MappedSchema is { } ms)
                                {
                                    b = ArrowColumnMappingRename.RenameBatch(b, ms, toPhysical: false);
                                }
                                await writer.WriteAsync(b, ct).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
            }
        }, ct);

        await foreach (var b in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return b;
        }
        await pump.ConfigureAwait(false); // observe pump faults
    }

    // Resolves the effective column-name mapping for ONE file of a column-mapping table:
    //   • name mode → the file-independent physicalName map from the listing (probe-free);
    //   • id mode   → probe THIS file's parquet `field_id → stored name` (footer read only) and compose
    //                 logical → field_id → physical name (per-file, so it stays correct across a column RENAME
    //                 where old + new files store the same field_id under different physical names); the raw
    //                 fid map is kept for the struct-rebuild's nested child resolution;
    //   • no mapping → empty (FileSql reads by logical name directly).
    private static FileMapping ResolveFileMapping(DeltaReader.NativeScanList listing, string uri)
    {
        // ALWAYS probe the file's actual schema nodes (footer-only; the footer is fetched again by the
        // subsequent read_parquet, so the bytes are cache-warm): presence is what lets a file predating a
        // schema evolution read correctly (absent columns/members become typed NULLs) — in every mapping
        // mode, at every nesting level.
        var nodes = ProbeFileNodes(uri);
        if (listing.LogicalToPhysical is { } phys)
        {
            return new FileMapping(phys, nodes);
        }
        if (listing.LogicalToFieldId is not { } logToFid)
        {
            return new FileMapping(null, nodes);
        }
        var map = new Dictionary<string, string>();
        foreach (var kv in logToFid)
        {
            if (nodes.FieldIdToName.TryGetValue(kv.Value, out var physName) && physName != kv.Key)
            {
                map[kv.Key] = physName;
            }
        }
        return new FileMapping(map.Count > 0 ? map : null, nodes);
    }

    // The stored (in-file) name of a mapped field at ANY nesting level: id mode resolves through THIS file's
    // parquet field_ids (correct for every vintage — old engineered-wood id files stored LOGICAL names under
    // their field_ids, new/Spark files store col-<guid>); otherwise the schema's declared physicalName (name
    // mode; Spark + engineered-wood both store columns under it), else the logical name.
    private static string StoredChildName(DeltaSchema.StructField field, FileMapping fm)
    {
        if (FieldIdOf(field) is { } fid && fm.Nodes.FieldIdToName.TryGetValue(fid, out var stored))
        {
            return stored;
        }
        if (field.Metadata is { } md
            && md.TryGetValue(DeltaSchema.ColumnMapping.PhysicalNameKey, out var physName)
            && !string.IsNullOrEmpty(physName))
        {
            return physName;
        }
        return field.Name;
    }

    private static int? FieldIdOf(DeltaSchema.StructField field)
        => field.Metadata is { } md
           && md.TryGetValue(DeltaSchema.ColumnMapping.FieldIdKey, out var idText)
           && int.TryParse(idText, System.Globalization.NumberStyles.Integer,
                           CultureInfo.InvariantCulture, out var fid)
            ? fid
            : null;

    // True when THIS file physically contains the field: by its declared column-mapping field id when the
    // file carries one, else by its stored-name path. False = the file predates the field (schema
    // evolution) -> the SQL emits a typed NULL instead of a mis-binding reference.
    private static bool Present(DeltaSchema.StructField field, string storedPath, FileMapping fm)
        => (FieldIdOf(field) is { } fid && fm.Nodes.FieldIdToName.ContainsKey(fid))
           || fm.Nodes.Paths.Contains(storedPath);

    private static DeltaSchema.StructField? FindField(DeltaSchema.StructType? schema, string name)
    {
        if (schema is null)
        {
            return null;
        }
        foreach (var f in schema.Fields)
        {
            if (string.Equals(f.Name, name, StringComparison.Ordinal))
            {
                return f;
            }
        }
        foreach (var f in schema.Fields)
        {
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    // True when the struct column's CURRENT member tree differs from what THIS file stores — a mapped
    // rename (stored != logical), a member the file predates (ADD), a member the file still carries that
    // the schema dropped (DROP), or any of those recursively — so the column needs the struct_pack rebuild
    // (which projects exactly the current members, backfilling absent ones as typed NULLs).
    private static bool StructShapeDiffers(DeltaSchema.StructType st, string parentPath, FileMapping fm)
    {
        var expectedStored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in st.Fields)
        {
            var stored = StoredChildName(ch, fm);
            expectedStored.Add(stored);
            if (!string.Equals(stored, ch.Name, StringComparison.Ordinal))
            {
                return true;
            }
            var childPath = parentPath + PathSep + stored;
            if (!Present(ch, childPath, fm))
            {
                return true;
            }
            if (ch.Type is DeltaSchema.StructType cst && StructShapeDiffers(cst, childPath, fm))
            {
                return true;
            }
        }
        if (fm.Nodes.Children.TryGetValue(parentPath, out var fileChildren))
        {
            foreach (var child in fileChildren)
            {
                if (!expectedStored.Contains(child))
                {
                    return true; // dropped member still in the file -> project members explicitly
                }
            }
        }
        return false;
    }

    // Rebuilds a MAPPED struct column with LOGICAL member names in SQL:
    //   CASE WHEN src IS NULL THEN NULL ELSE struct_pack("a" := (src)."col-a", …) END
    // recursing into nested structs — so the outer projection, a pushed struct-member predicate
    // (struct_extract SQL over logical names) and the probed schema all bind. The CASE keeps NULL structs
    // NULL (struct_pack alone would materialize a non-NULL struct of NULLs). List/map members pass through
    // unrebuilt: their inner struct names stay physical (the per-batch ArrowColumnMappingRename fixes them,
    // and no StructFilter can reach inside a list/map).
    private static string RebuildExpr(DeltaSchema.DeltaDataType type, string src, string srcPath, FileMapping fm)
    {
        if (type is not DeltaSchema.StructType st || st.Fields.Count == 0)
        {
            return src;
        }
        var parts = new List<string>(st.Fields.Count);
        foreach (var ch in st.Fields)
        {
            var stored = StoredChildName(ch, fm);
            var childPath = srcPath + PathSep + stored;
            string expr;
            if (!Present(ch, childPath, fm))
            {
                // This file predates the member (nested schema evolution) -> a typed NULL child.
                expr = $"CAST(NULL AS {TypeText(ch.Type)})";
            }
            else
            {
                var childSrc = $"({src}).{Quote(stored)}";
                expr = RebuildExpr(ch.Type, childSrc, childPath, fm);
            }
            parts.Add($"{Quote(ch.Name)} := {expr}");
        }
        return $"CASE WHEN {src} IS NULL THEN NULL ELSE struct_pack({string.Join(", ", parts)}) END";
    }

    // The DuckDB type text for a Delta type — the CAST target for schema-evolution NULL backfill. Struct
    // member names are the LOGICAL names (matching the rebuilt/renamed output convention).
    internal static string TypeText(DeltaSchema.DeltaDataType type) => type switch
    {
        DeltaSchema.PrimitiveType pt => pt.TypeName switch
        {
            "string" => "VARCHAR",
            "long" => "BIGINT",
            "integer" => "INTEGER",
            "short" => "SMALLINT",
            "byte" => "TINYINT",
            "double" => "DOUBLE",
            "float" => "FLOAT",
            "boolean" => "BOOLEAN",
            "binary" => "BLOB",
            "date" => "DATE",
            "timestamp" => "TIMESTAMPTZ",     // Delta timestamp is UTC-adjusted -> TIMESTAMP WITH TIME ZONE
            "timestamp_ntz" => "TIMESTAMP",
            // A file predating an ADDed variant column backfills as a NULL VARIANT. Only reachable via ALTER
            // TABLE ADD COLUMN v VARIANT — the added column is absent from every file written before it, and
            // the cast target has to be the LOGICAL type, since the outer projection presents variant (the
            // registered extension carries it across the ABI as the transport blob from there).
            "variant" => "VARIANT",
            var dec when dec.StartsWith("decimal(", StringComparison.Ordinal) => dec.ToUpperInvariant(),
            var other => throw new NotSupportedException(
                $"delta native read: no NULL-backfill type mapping for '{other}'."),
        },
        DeltaSchema.StructType st =>
            "STRUCT(" + string.Join(", ", st.Fields.Select(f => $"{Quote(f.Name)} {TypeText(f.Type)}")) + ")",
        DeltaSchema.ArrayType at => TypeText(at.ElementType) + "[]",
        DeltaSchema.MapType mt => $"MAP({TypeText(mt.KeyType)}, {TypeText(mt.ValueType)})",
        _ => throw new NotSupportedException(
            $"delta native read: no NULL-backfill type mapping for '{type}'."),
    };

    // ONE file's actual parquet schema nodes, footer-probed via parquet_schema: every node's stored-name
    // PATH (PathSep-joined, root excluded), each node's field id when present, and the direct children per
    // node — presence + stored-name resolution for column mapping AND schema evolution.
    private sealed class FileNodes
    {
        public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, string> FieldIdToName { get; } = new();
        public Dictionary<string, List<string>> Children { get; } = new(StringComparer.Ordinal);
    }

    private static FileNodes ProbeFileNodes(string uri)
    {
        var nodes = new FileNodes();
        // parquet_schema emits the footer's flat DFS pre-order (name, num_children); reconstruct each
        // node's path with a stack. The first row is the schema root (skipped; its children are depth 1).
        var sql = "SELECT name, CAST(num_children AS BIGINT), CAST(field_id AS BIGINT) "
                  + $"FROM parquet_schema('{uri.Replace("'", "''")}')";
        using var s = Host.Query(sql);
        var stack = new Stack<(string Path, long Remaining)>();
        bool sawRoot = false;
        while (true)
        {
            var batch = s.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            if (batch is null)
            {
                break;
            }
            using (batch)
            {
                var names = (StringArray)batch.Column(0);
                var childCounts = (Int64Array)batch.Column(1);
                var fids = (Int64Array)batch.Column(2);
                for (int i = 0; i < batch.Length; i++)
                {
                    string name = names.GetString(i);
                    long children = childCounts.IsValid(i) ? childCounts.GetValue(i)!.Value : 0;
                    if (!sawRoot)
                    {
                        sawRoot = true;
                        stack.Push(("", children));
                        continue;
                    }
                    if (stack.Count == 0)
                    {
                        break; // malformed ordering — stop rather than misattribute paths
                    }
                    var (parentPath, remaining) = stack.Pop();
                    stack.Push((parentPath, remaining - 1));
                    string path = parentPath.Length == 0 ? name : parentPath + PathSep + name;
                    nodes.Paths.Add(path);
                    if (!nodes.Children.TryGetValue(parentPath, out var siblings))
                    {
                        nodes.Children[parentPath] = siblings = new List<string>();
                    }
                    siblings.Add(name);
                    if (fids.IsValid(i))
                    {
                        nodes.FieldIdToName[(int)fids.GetValue(i)!.Value] = name;
                    }
                    if (children > 0)
                    {
                        stack.Push((path, children));
                    }
                    while (stack.Count > 0 && stack.Peek().Remaining == 0)
                    {
                        stack.Pop();
                    }
                }
            }
        }
        return nodes;
    }

    private static int Prefetch()
    {
        var text = Environment.GetEnvironmentVariable("FABRICATOR_DELTA_PREFETCH");
        if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text, out var n) && n >= 1)
        {
            return Math.Min(n, 64);
        }
        return 1; // sequential by default; >1 opts into concurrent file fetch (the cloud-I/O win)
    }

    private static string Quote(string col) => "\"" + col.Replace("\"", "\"\"") + "\"";
}
