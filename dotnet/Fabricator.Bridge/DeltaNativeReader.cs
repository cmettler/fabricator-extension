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
///
/// <para><b>THE PER-FILE LOOP IS NOT THE ONLY PATH ANY MORE (2026-08-06).</b> One <c>Host.Query</c> per file
/// costs real fixed overhead, and it scales with FILE COUNT — which dbt grows, since every incremental run
/// appends one — so <see cref="BatchPlan"/> collapses the files it can into ONE
/// <c>read_parquet([f1, f2, …], schema = map {…})</c> and leaves the rest here. MEASURED, same rows and same
/// answers either way (<c>FABRICATOR_DELTA_BATCH_MIN_FILES=0</c> disables it, which is how both legs were
/// timed): <b>200 files x 100 rows: 0.464 s → 0.090 s (5.2x)</b>; 200 files x 20k rows:
/// 0.794 s → 0.493 s; 50 files x 20k rows: 0.211 s → 0.123 s. Consistently <b>~1.5–1.9 ms of overhead removed
/// per file</b>, which is why the relative win tracks how FRAGMENTED the table is rather than how big it is —
/// the fragmented-small-file shape is the dbt-incremental one this exists for.</para>
///
/// <para>⚠ <b>A 13x figure previously recorded here was CONFOUNDED and is withdrawn.</b> It compared our scan
/// (412 ms) against DuckDB reading the same files in one plan (31 ms) — but that plan AGGREGATES in place,
/// while we must hand every row back across the Arrow boundary for DuckDB to aggregate above the scan. It was
/// a floor no batching can reach, not an alternative. The honest comparison is our own scan with batching on
/// vs off, above. (A second version of the same mistake nearly replaced it: the first batched-vs-plain timing
/// said <c>schema</c> cost 10x, because the probe query's <c>count(s)</c> was answered from parquet null counts
/// without decoding anything. With a real decode forced, <c>schema</c> costs NOTHING measurable — 18–27 ms vs
/// 21–33 ms hand-aliased.)</para>
///
/// <para>The loop still owns every shape a single call cannot express — the transient rowid, deletion vectors,
/// partition literals, per-file <c>baseRowId</c> row tracking, per-file pruning predicates, and id-mode column
/// mapping. Each of those is a numbered, measured case on <see cref="BatchPlan"/>; read them there before
/// widening the gate, because two of them fail SILENTLY rather than loudly.</para>
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
    /// = the resolved time-travel/pinned snapshot ("version"/"timestamp"), or null for latest.
    /// <paramref name="bound"/> = the transaction's bound table for the listing's table open reuse (threaded
    /// by the catalog — this class is static and cannot reach the per-catalog manager; a global-function
    /// caller passes none and the listing open is owned + disposed, the pre-cache behaviour).</summary>
    public static IArrowArrayStream Read(
        nint opener, string path, Schema userSchema, ScanSpec? spec,
        IReadOnlyList<object?> filterValues, string? unit, string? value,
        IReadOnlyList<EngineeredWood.DeltaLake.Table.WrittenDataFile>? pendingFiles = null,
        IReadOnlyDictionary<int, HashSet<long>>? pendingDeletes = null,
        EngineeredWood.DeltaLake.Schema.StructType? pendingSchema = null,
        DeltaTableBinding? bound = null)
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

        // Bare-LIMIT pushdown (ScanSpec.Top): appended to every generated query, so a `LIMIT 1` stops the
        // read instead of scanning all N files and discarding above the scan (with the plain form's DECLARED
        // schema, footer reads are deferred to execution, so the limit also caps how many files are OPENED at
        // all — the ~25 s -> ~1 s shape on the profiled 89-file remote table). Safe by the HOST's own gate:
        // arrow_ingest emits "top" only when the scan carries NO filter, static or dynamic, because a
        // best-effort filter's superset plus an early limit could starve real matches. Gated here on ORDER BY
        // being absent as well — TryPushTopN pushes top+order_by TOGETHER for non-string keys, and a TopN's
        // limit without its order is an arbitrary subset that DuckDB's kept TopN above cannot repair (it
        // re-sorts whatever arrives; the missing rows are simply gone). Applying the ORDER BY too would be
        // TopN pushdown for this reader — a separate enhancement, not smuggled into a LIMIT fix. Per looped
        // file the limit is a SUPERSET (each file capped at n; DuckDB re-limits above), the same contract as
        // every other best-effort pushdown here.
        string? topSuffix = spec?.Top is { } topN && topN >= 0 && spec.OrderBy is not { Count: > 0 }
            ? " LIMIT " + topN.ToString(CultureInfo.InvariantCulture)
            : null;

        var listing = DeltaReader.ListNativeScanFiles(opener, path, unit, value, prune, Log,
                                                      schemaOverride: pendingSchema, bound: bound);
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
        // A file whose deletion vector covers EVERY one of its rows contributes nothing, so skip it rather
        // than open it and exclude all of its rows one by one. This is not a corner case: a merge-on-read
        // UPDATE of a whole file DV-deletes all of it and appends the post-images beside it, so a full-table
        // UPDATE leaves exactly this shape behind and EVERY later scan paid to read the dead file.
        // Placed AFTER the pending-delete merge on purpose — a file can be finished off by deletes buffered in
        // THIS transaction, and read-your-writes must see that.
        // ⚠ Trusts stats.numRecords (see DvRangeCondition for why that is the standard assumption and what it
        // costs if a writer lies). Skipped only when the count is KNOWN: an add without stats is always read.
        if (listing.Files.Count > 0)
        {
            var live = listing.Files.Where(f => f.NumRecords is not { } n || n <= 0 || f.Dv.Length < n).ToList();
            if (live.Count != listing.Files.Count)
            {
                Log.LogInformation("delta native fully-deleted skip {Path}: files {Before} -> {After}",
                                   path, listing.Files.Count, live.Count);
                listing = WithFiles(listing, live);
            }
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
        // Seed the onelake VFS's path→size side table from the snapshot's AddFiles, so the literal-glob
        // echo can carry each file's size and DuckDB's OpenFileExtended skips the per-file properties
        // round trip (the measured ~2–5 props-opens per scanned file). Sound because a Delta data file is
        // IMMUTABLE (UUID-named, never overwritten in place) and the size is part of the commit — the same
        // argument duckdb-iceberg's multi-file reader makes when it stamps its OpenFileInfos
        // (validate_external_file_cache=false + dummy identity). Pending (uncommitted) files carry no
        // SizeBytes and are skipped; only this reader feeds the table, so the generic VFS never learns a
        // size for a path whose content could change.
        foreach (var f in listing.Files)
        {
            if (f.SizeBytes is { } sz && sz > 0 && f.Uri.StartsWith("onelake://", StringComparison.Ordinal))
            {
                OneLakeForwardFs.SeedKnownSize(f.Uri, sz);
            }
        }
        // Collapse the DV-free files into ONE read_parquet([…]) where that is expressible (see BatchPlan.Build);
        // whatever is left keeps the per-file loop unchanged.
        var batch = BatchPlan.Build(listing, dataCols, wantRowId, where, rowIdFilter, trackingFilter,
                                    hasTop: topSuffix is not null);
        var loopFiles = batch?.LoopFiles ?? listing.Files;
        var schema = ProbeSchema(listing, userSchema, dataCols, wantRowId, batch);
        int prefetch = Prefetch();

        Log.LogInformation(
            "delta native scan {Path}: v{Version} files={Files} batched={Batched} cols=[{Cols}] rowid={RowId} where=[{Where}] top={Top} prefetch={Prefetch} colmap={Map}",
            path, listing.Version, listing.Files.Count, batch?.Files.Count ?? 0, string.Join(",", dataCols),
            wantRowId, where ?? "", spec?.Top?.ToString(CultureInfo.InvariantCulture) ?? "-", prefetch,
            listing.LogicalToPhysical is not null ? "name" : listing.LogicalToFieldId is not null ? "id" : "none");

        return new AsyncEnumerableArrowStream(
            schema,
            StreamFiles(listing, loopFiles, batch, dataCols, wantRowId, where, prefetch, rowIdFilter,
                        trackingFilter, topSuffix));
    }

    /// <summary>
    /// ONE <c>read_parquet([f1, f2, …])</c> standing in for the per-file loop over the files it covers — the
    /// answer to the ~8 ms-per-file host-query overhead documented on this class. MEASURED on a 50-file /
    /// 1M-row / 4-column table, warm, summing every column (so nothing is answered from footer metadata):
    /// <b>0.211 s through the per-file loop vs 0.021 s batched — 10x</b>, and the <c>schema</c> parameter
    /// itself costs nothing (18–27 ms batched with the map vs 21–33 ms for a plain multi-file read aliased by
    /// hand, i.e. indistinguishable).
    /// <para>⚠ The naive version of that comparison said <c>schema</c> was 10x SLOWER than a plain read (21 ms
    /// vs 2 ms). It was measuring nothing: the probe query's <c>count(s)</c> was answered from parquet null
    /// counts without decoding the column. Force a real decode (<c>sum(length(s))</c>) before believing any
    /// number here.</para>
    /// <para><b>What makes it expressible is <c>read_parquet</c>'s <c>schema</c> parameter</b>, whose semantics
    /// were pinned by experiment (the docs are thin and several plausible readings are wrong) —
    /// <c>schema = map { &lt;key&gt;: {'name': …, 'type': …, 'default_value': …} }</c>:
    /// the MAP KEY is the identifier (VARCHAR ⇒ match by name, INTEGER ⇒ <c>BY_FIELD_ID</c>), <c>'name'</c> is
    /// the OUTPUT name — so it performs the physical→logical rename for us — <c>'type'</c> casts per file, a
    /// column absent from a file arrives as <c>default_value</c> (that is the schema-evolution backfill, and it
    /// makes the per-file footer probe unnecessary), and a column present in the file but absent from the map is
    /// ignored (that is the post-DROP-COLUMN read).</para>
    /// <para><b>⚠ The gates below are not caution, they are measured limits.</b> Each one is a shape where a
    /// single call is either refused or — worse — silently wrong:</para>
    /// <list type="number">
    /// <item><b><c>file_row_number</c> forces field-id keys — and ⚠ <c>filename</c> does NOT share that limit,
    /// which an earlier version of this note wrongly claimed.</b> <c>file_row_number</c> composes with an
    /// INTEGER-keyed map but FAILS with a VARCHAR-keyed one (<c>Invalid Input Error: … column "2147483645" …
    /// could not be found</c> — its sentinel id resolved by name). <c>filename</c> composes with the
    /// VARCHAR-keyed map fine (MEASURED 2026-08-15; it is constant per file, answered by
    /// <c>GetConstantVirtualColumn</c> before the column-mapping path — the same reason it escapes the
    /// field-id assertion). So everything needing a row POSITION (the transient rowid, a deletion vector, a
    /// derived row-tracking id) stays on the loop here, but a per-file-CONSTANT join keyed on
    /// <c>filename</c> is available under this form. And field-id keys are not a free substitute for the
    /// position shapes: a file carrying NO parquet field ids raises <c>INTERNAL Error: No default expression
    /// in FieldId Map</c>, and name mode does not require a writer to stamp them.</item>
    /// <item><b>ID-mode column mapping is a CONTRACT gate, and the honest statement of it is narrower than it
    /// first looks.</b> This path resolves columns by NAME (the map key), while id mode's contract is that a
    /// reader matches by FIELD ID and the stored name is <i>not</i> authoritative — a legacy engineered-wood
    /// id-mode file stores LOGICAL names under its field ids where a current one stores <c>col-&lt;guid&gt;</c>,
    /// and an external writer is free to do either. A name-keyed map meeting such a file finds nothing and hands
    /// back <c>default_value</c>: a silently ALL-NULL column, or for a struct interior a partial-overlap cast
    /// that DROPS the members it did not match. Both measured directly on <c>read_parquet</c> — children (a, b)
    /// with only b renamed gave <c>{'a': 20, 'b': NULL}</c>, no error; fully disjoint children DO error
    /// (<i>STRUCT to STRUCT cast must have at least one matching member</i>), so partial overlap is the
    /// dangerous shape and partial overlap is what one rename produces.
    /// <para>⚠ <b>But OUR OWN writers never produce the divergence, so no test here can kill this gate, and it
    /// must not be described as if one did.</b> MEASURED: an id-mode table taken through a nested RENAME and then
    /// a top-level RENAME has all four of its files storing BYTE-IDENTICAL physical names — the physicalName is
    /// assigned once at column creation in id mode exactly as in name mode. A mutant with this gate removed
    /// therefore passes the whole suite. It is kept because name-matching a table whose contract is
    /// id-matching is unsound for files we did not write, not because a reachable bug was reproduced.</para>
    /// NAME mode needs no such gate: there the physical name IS authoritative and stable across renames, which
    /// is the reason the mode exists.</item>
    /// <item><b>Partition values are per file and absent from the data files</b>, so a single call would
    /// backfill them as <c>default_value</c> NULL. Reaching them needs a <c>filename</c> join — which case 1
    /// now records as AVAILABLE under the VARCHAR map (<c>filename => true</c> composes; measured
    /// 2026-08-15), so this gate is UNIMPLEMENTED rather than blocked: the fix is the full form's per-file
    /// constants input joined on <c>filename</c>, brought over to this form. Until built, a projected
    /// partition column still declines here.</item>
    /// <item><b>A per-file predicate cannot be expressed IN ONE SELECT</b> — the deletion vector's prunable
    /// bound, the rowid's position range, the row-tracking condition. Those are pruning, not correctness
    /// (DuckDB re-applies every predicate above the scan), but forfeiting them is the opposite of the point.
    /// The deletion vector is the one decided PER FILE, and since 2026-08-16 a mixed table takes
    /// <see cref="TryUnionForm"/>: the plain branch over the clean files <c>UNION ALL</c> one
    /// <see cref="FileSql"/> branch per DV file, each branch KEEPING its prunable bound — a per-file predicate
    /// is inexpressible in one SELECT and perfectly expressible in one BRANCH. ⚠ On a REMOTE root only a scan
    /// carrying a pushed bare LIMIT takes it (a measured per-op execution anomaly of the host-query path —
    /// TryUnionForm's remarks carry the numbers); the rowid/tracking fast-path filters stay on the loop
    /// (excluded one gate earlier). The old behaviour — one deletion vector sending the whole scan to
    /// <see cref="TryFullForm"/>, which forfeits the bound for every file and pays its two O(N) footer
    /// sweeps — remains the remote-unlimited route and the fallback when a union branch cannot be built.</item>
    /// </list>
    /// <para><b>What retires case 2 (and it is a concrete upstream change, not a hope):</b> duckdb/duckdb
    /// <b>#24407</b> — "extend the <c>schema</c> option to support NESTED schema definitions", by Tishj, OPEN
    /// against <c>main</c> as of 2026-08-06. Declaring a struct's children with their own identifiers is exactly
    /// what makes one declared type describe files of two vintages, so id mode + structs becomes expressible
    /// and the silent NULL-fill above stops being reachable. It targets <c>main</c>, so it lands on the future
    /// line, not <c>v1.5-variegata</c> — the gate stands here regardless.</para>
    /// <para>⚠ One exposure shared with the loop rather than introduced here: the batch CASTs to the CURRENT
    /// Delta type while the loop reads each file's stored type as-is, so a type-WIDENED table can advertise one
    /// type and stream another. That is already true of the loop across vintages (<c>typeWidening</c> is
    /// untested, see CLAUDE.md); a mixed batch+loop scan does not make it worse, but it is the shape to check
    /// first if widening is ever wired up.</para>
    /// </summary>
    internal sealed class BatchPlan
    {
        internal BatchPlan(string sql, IReadOnlyList<DeltaReader.NativeScanFile> files,
                           IReadOnlyList<DeltaReader.NativeScanFile> loopFiles,
                           Func<IReadOnlyList<(string, IArrowArrayStream)>>? inputs = null,
                           IReadOnlyList<string>? viewNames = null)
        {
            Sql = sql;
            Files = files;
            LoopFiles = loopFiles;
            Inputs = inputs;
            ViewNames = viewNames ?? System.Array.Empty<string>();
        }

        /// <summary>The single query covering <see cref="Files"/>.</summary>
        internal string Sql { get; }

        /// <summary>The files the one query reads.</summary>
        internal IReadOnlyList<DeltaReader.NativeScanFile> Files { get; }

        /// <summary>The files that still need their own per-file query.</summary>
        internal IReadOnlyList<DeltaReader.NativeScanFile> LoopFiles { get; }

        /// <summary>Builds the bound Arrow inputs the SQL references (deletion vectors, per-file metadata), or
        /// null when it references none. A FACTORY rather than a list because each is single-use: the schema
        /// probe runs the same SQL with <c>LIMIT 0</c> and must not consume the streams the real scan needs.</summary>
        internal Func<IReadOnlyList<(string, IArrowArrayStream)>>? Inputs { get; }

        /// <summary>The bound-input view names this plan registers, dropped once its query has been drained
        /// (they are NOT temporary views — see <see cref="NextViewName"/>).</summary>
        internal IReadOnlyList<string> ViewNames { get; }

        /// <summary>Runs this plan, binding its inputs.</summary>
        internal IArrowArrayStream Query(string? suffix = null)
        {
            string sql = suffix is null ? Sql : Sql + suffix;
            return Inputs is null ? Host.Query(sql) : Host.Query(sql, Inputs());
        }

        /// <summary>Files below this count are not worth batching — kept on the proven per-file path.
        /// <c>FABRICATOR_DELTA_BATCH_MIN_FILES=0</c> disables batching entirely (which is how a suite pins the
        /// per-file pruning behaviour), and 1 forces it wherever it is expressible.</summary>
        private static int MinFiles()
        {
            var text = Environment.GetEnvironmentVariable("FABRICATOR_DELTA_BATCH_MIN_FILES");
            return !string.IsNullOrWhiteSpace(text) && int.TryParse(text, NumberStyles.Integer,
                                                                    CultureInfo.InvariantCulture, out var n) && n >= 0
                ? n
                : 2;
        }

        /// <summary>Returns the plan, or null when this scan's shape is not expressible as one call (every
        /// reason is a numbered case in the class remarks). <paramref name="hasTop"/> = a bare LIMIT was
        /// pushed (consumed by the union form's remote gate — see <see cref="TryUnionForm"/>).</summary>
        internal static BatchPlan? Build(
            DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols, bool wantRowId, string? where,
            DeltaRowIdFilter? rowIdFilter, DeltaRowTrackingFilter? trackingFilter, bool hasTop = false)
        {
            int min = MinFiles();
            if (min == 0 || listing.Files.Count == 0)
            {
                return null;
            }
            // A per-file PREDICATE is the one thing neither form can carry (its whole value is per-file row-group
            // pruning, and one call has one WHERE).
            if (rowIdFilter is not null || trackingFilter is not null)
            {
                return null;
            }
            bool wantsTracking = false;
            foreach (var c in dataCols)
            {
                if (string.Equals(c, RowTrackingIdColumn, StringComparison.Ordinal)
                    || string.Equals(c, RowTrackingVersionColumn, StringComparison.Ordinal))
                {
                    wantsTracking = true;
                }
            }
            // ⚠ THE PLAIN FORM IS TRIED FIRST, and it is not merely a preference — it is strictly cheaper to
            // ATTEMPT. TryPlainForm is pure string work; TryFullForm issues PresentNames, a
            // `parquet_schema([every file])` query, which on remote storage is an O(files) FOOTER read.
            // MEASURED on 89 remote files (each variant in its own process, so no footer cache is shared):
            // PresentNames 20.50 s, then the full form's own bind-time `LIMIT 0` schema probe another 20.66 s —
            // against 0.49 s for the plain form's probe, 42x, because a declared `schema` map needs no footer at
            // all. On the profiled Fabric query that pair is 34 s of 77. Before this the expensive-to-attempt
            // form was the one tried first, and it SUCCEEDS on the commonest shape (a plain projection over
            // DV-free files with no rowid), so the cheap form was unreachable exactly where it wins most.
            var plain = TryPlainForm(listing, dataCols, wantRowId, wantsTracking, where, min);
            if (plain is not null)
            {
                if (plain.LoopFiles.Count == 0)
                {
                    return plain;
                }
                // THE MIXED CASE (some files DV-carrying): the plain form serves the clean files and its
                // LoopFiles are EXACTLY the DV files (TryPlainForm splits on nothing else), so compose ONE
                // query — the plain branch UNION ALL one FileSql branch per DV file. Each DV branch pays one
                // footer probe (D of them, against the full form's TWO O(N) footer sweeps), and it RESTORES
                // the deletion vector's prunable bound, which the full form structurally forfeits (one WHERE
                // cannot carry a per-file range, but one BRANCH can). ⚠ REMOTELY it is gated — see
                // TryUnionForm's remarks for the measured execution anomaly that forces that.
                var mixed = TryUnionForm(plain, listing, dataCols, where, hasTop);
                if (mixed is not null)
                {
                    return mixed;
                }
            }
            if (listing.Files.Count >= min)
            {
                // The FULL form: `union_by_name` + `filename`/`file_row_number`, so the rowid, the deletion
                // vectors, the partition values and row tracking all fit in ONE call covering EVERY file.
                // Reached when the plain form declined outright (rowid / row tracking / a projected partition
                // column), or when a mixed table's union branch could not be built.
                var full = TryFullForm(listing, dataCols, wantRowId, where);
                if (full is not null)
                {
                    return full;
                }
            }
            // Neither serves everything: the partial plain plan (its DV-free files batched, the rest looped) is
            // still better than no plan, and is exactly what this returned before the preference was reversed.
            return plain;
        }

        /// <summary>
        /// The PLAIN single-call form: <c>read_parquet([DV-free files], schema = map {&lt;physical name&gt;: …})</c>.
        /// Declares the schema rather than discovering it, so it reads NO parquet footer at bind — the property
        /// that makes it 42x cheaper to probe than <see cref="TryFullForm"/> on remote storage (0.49 s vs 20.66 s
        /// over 89 files). It is PARTIAL BY DESIGN: DV-carrying files land in <c>LoopFiles</c>, which
        /// <see cref="Build"/> first offers to <see cref="TryUnionForm"/> (one query, per-DV-file branches
        /// keeping their prunable bound) and only failing that leaves on the per-file loop.
        /// <para>Cannot express anything needing a row POSITION — the transient rowid, a deletion vector, the
        /// derived row-tracking ids — because those need <c>file_row_number</c>, which a VARCHAR-keyed map
        /// refuses (case 1 in the class remarks). Nor a PROJECTED partition column: <c>schema</c> is refused
        /// together with <c>hive_partitioning</c>.</para>
        /// <para>⚠ It passes NO <c>hive_partitioning</c> flag where the full form disables it explicitly, and
        /// that is safe rather than an oversight: MEASURED on a table whose files really do live under
        /// <c>edwYear=2012/edwMonth=10/</c>, <c>DESCRIBE SELECT *</c> under the schema map returns exactly the
        /// declared columns — a declared <c>schema</c> SUPPRESSES hive auto-detection, so no phantom column is
        /// injected. ⚠ The zero-column (<c>COUNT(*)</c>) branch declares no map at all and is therefore still
        /// exposed to auto-detection; it selects a constant, so an injected column is unreferenced — untested
        /// rather than safe.</para>
        /// <para>⚠ It CASTs to the DECLARED Delta type where the full form takes each file's STORED type, so on a
        /// type-widened table the two forms would advertise different types (<c>typeWidening</c> is untested —
        /// see the tail of the class remarks). Conversely it HANDLES structs where the full form declines, so
        /// preferring it is not uniformly a narrowing.</para>
        /// </summary>
        private static BatchPlan? TryPlainForm(
            DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols, bool wantRowId, bool wantsTracking,
            string? where, int min)
        {
            if (wantRowId || wantsTracking)
            {
                return null;
            }
            // The DV-free files batch; the rest keep the loop (and their prunable bound — case 4).
            var batchFiles = new List<DeltaReader.NativeScanFile>(listing.Files.Count);
            var loopFiles = new List<DeltaReader.NativeScanFile>();
            foreach (var f in listing.Files)
            {
                (f.Dv.Length == 0 ? batchFiles : loopFiles).Add(f);
            }
            if (batchFiles.Count < min)
            {
                return null;
            }

            string source = "read_parquet([" + string.Join(", ", batchFiles.Select(f => Literal(f.Uri))) + "]";
            var sb = new StringBuilder("SELECT ");
            if (dataCols.Count == 0)
            {
                // The zero-column shape (COUNT(*)): no column is read, so mapping / evolution / types cannot
                // matter and no schema map is needed — which also means id mode and partition columns are
                // irrelevant here. A filter would have to bind a column the projection does not name, so
                // require none (DuckDB projects a filtered column anyway, making this unreachable in practice).
                if (where is not null)
                {
                    return null;
                }
                sb.Append("1 FROM ").Append(source).Append(')');
                return new BatchPlan(sb.ToString(), batchFiles, loopFiles);
            }

            if (listing.TableSchema is null || listing.LogicalToFieldId is not null)
            {
                return null; // case 2: no schema to declare, or id-mode column mapping
            }
            // Whether to key the map by the PHYSICAL name at all. PhysicalName reads field metadata without
            // regard to the table's mapping MODE, so this is what stops a table whose mode is 'none' but whose
            // schema still carries physicalName metadata from being keyed physically against files that store
            // logical names — which would read as an all-NULL column with no error. The listing's own two maps
            // are the mode evidence (LogicalToPhysical is top-level-only and null when no top-level name
            // differs, so MappedSchema has to be consulted too for a table where only a NESTED name does).
            bool nameMapped = listing.LogicalToPhysical is not null || listing.MappedSchema is not null;
            var entries = new List<string>(dataCols.Count);
            var inner = new List<string>(dataCols.Count);
            bool needsInner = false;
            foreach (var c in dataCols)
            {
                if (listing.PartitionColumns.Count > 0 && ContainsName(listing.PartitionColumns, c))
                {
                    return null; // case 3
                }
                var field = FindField(listing.TableSchema, c);
                if (field is null)
                {
                    return null; // unknown column: let the loop's own resolution answer for it
                }
                string stored = nameMapped ? PhysicalName(field) : field.Name;
                string type;
                try
                {
                    type = PhysicalTypeText(field.Type, nameMapped);
                }
                catch (NotSupportedException)
                {
                    return null; // no CAST-target rendering (variant, or a type we do not map) — loop it
                }
                entries.Add($"{Literal(stored)}: {{'name': {Literal(c)}, 'type': {Literal(type)}, "
                            + "'default_value': NULL}");
                // `schema` renames only the TOP level, so a mapped struct's interior arrives physical. Rebuild it
                // with logical member names in SQL — not for cosmetics: a pushed struct-member predicate
                // (`(s).b IS NULL`) binds against this projection, and without the rebuild it fails to bind
                // (`Could not find key "b" in struct`, caught by verify_delta_catalog_nested_alter). Same reason
                // the per-file path has RebuildExpr. No presence check is needed here — the declared type already
                // NULL-fills members a file predates.
                var rebuilt = nameMapped ? LogicalStructExpr(field.Type, Quote(c)) : null;
                inner.Add(rebuilt is null ? Quote(c) : $"{rebuilt} AS {Quote(c)}");
                needsInner |= rebuilt is not null;
            }
            string projection = string.Join(", ", dataCols.Select(Quote));
            string scan = source + ", schema = map {" + string.Join(", ", entries) + "})";
            // The WHERE goes ABOVE the rebuild so it binds logical names at every level (the per-file path's
            // outer-WHERE contract).
            sb.Append(projection).Append(" FROM ")
              .Append(needsInner ? $"(SELECT {string.Join(", ", inner)} FROM {scan})" : scan);
            if (!string.IsNullOrEmpty(where))
            {
                sb.Append(" WHERE ").Append(where);
            }
            return new BatchPlan(sb.ToString(), batchFiles, loopFiles);
        }

        /// <summary>
        /// The UNION form for a MIXED table: the partial plain plan's own SQL over the clean files,
        /// <c>UNION ALL</c> one per-file branch (the loop's <see cref="FileSql"/>, verbatim machinery) for each
        /// deletion-vector file — ONE query covering everything, with no <c>union_by_name</c> and no
        /// <c>PresentNames</c> sweep.
        /// <para><b>Why it beats both alternatives it replaced.</b> Against the FULL form: that one pays TWO
        /// O(N) parquet-footer sweeps at bind (PresentNames + the <c>LIMIT 0</c> probe — 41 s cold on 89 remote
        /// files) where this pays exactly D single-file footer probes (D = DV files, normally a handful from
        /// recent DML), and it FORFEITS the deletion vector's prunable bound where a per-file BRANCH keeps it
        /// (<see cref="DvRangeCondition"/>). Against the plain+loop split: one host query instead of 1+D (the
        /// union's measured marginal cost is ~0.4 ms/branch against the loop's ~1.9 ms/file), and one
        /// <c>LIMIT</c>/early-stop boundary instead of D+1.</para>
        /// <para><b>⚠ ON A REMOTE ROOT IT IS GATED to scans carrying a pushed bare LIMIT — because of a
        /// measured EXECUTION anomaly of our own path, not of the form.</b> Live A/Bs on OneLake
        /// <c>lake.dbo.frag</c> (200 files, 2 DV), all through the extension: union full-scan <b>120.4 s</b>
        /// against the full form's <b>17.7 s</b> — while the BYTE-EQUIVALENT plain-branch SQL pasted RAW
        /// into duckdb.exe runs in <b>6.3 s</b>. The anomaly has TWO measured terms. (1) Per-file props
        /// fetches at execution — RETIRED by the seeded-size echo + the per-file
        /// <c>validate_external_file_cache=false</c> declaration (see <c>OneLakeForwardFs.SeedKnownSize</c>),
        /// which took the union's cold scan 120.4 → 44.7 s and the FULL form's 17.7 → <b>4.7 s</b>.
        /// (2) A residual CPU term in the union-shaped query's execution through the host-query fetch:
        /// with IDENTICAL IO (200 zero-IO opens + 200 reads each), the cold union burns <b>42.6 s of user
        /// CPU against the full form's ~2.9 s</b> — 44.7 s vs 4.7 s wall on the same table in the same
        /// minutes. Cache-warm the union is fine (1.6 s), so the burn sits in the cold union execution
        /// itself; unattributed (the raw run proves the SQL shape is innocent), and until it is found the
        /// gate stands.</para>
        /// <para><b>The LIMIT exception is measured, not hoped:</b> with a pushed bare LIMIT the union stops
        /// after a handful of opens (frag <c>LIMIT 1</c>: 10 opens, <b>3.65–3.8 s</b>) where the full form
        /// must pay BOTH O(N) sweeps before its first row — the profiled-query shape, and the reason the
        /// gate is not a flat remote refusal. Local roots take the union unconditionally (~10 ms/op there;
        /// the raw A/B and the loop-vs-union marginals both favour it).</para>
        /// <para><b>Eligibility is exactly the plain form's</b> — this is only called on its PARTIAL result, so
        /// no rowid, no row tracking, no projected partition column, no id mode, every column renderable. Each
        /// DV branch additionally needs its footer probe and typed-NULL rendering to succeed; any failure
        /// falls back to <see cref="TryFullForm"/> (the pre-2026-08-16 route), never to a worse answer.</para>
        /// <para><b>The struct-interior union hazard does NOT apply here</b> (the one
        /// <see cref="FullTableSql"/> documents): every branch projects LOGICAL names — the plain branch via
        /// the map's <c>'name'</c> rename + <see cref="LogicalStructExpr"/>, the DV branches via FileSql's
        /// aliases + RebuildExpr — so the branches agree on names at every level BY CONSTRUCTION, and in name
        /// mode physical names are file-independent so the rebuilt interiors agree too.</para>
        /// <para><b>Deletion vectors cross ONCE:</b> vectors above <see cref="DvLiteralMax"/> ride a single
        /// shared <c>(fn, pos)</c> input (<see cref="FileDvStream"/> over just those files) wrapped in a
        /// <c>WITH __fab_d AS MATERIALIZED</c> CTE — materialization is what makes several branches scanning
        /// one single-use stream sound (each branch anti-joins its own filename slice, the fn a LITERAL since
        /// a branch knows its file, so no <c>filename => true</c> is needed). Small vectors stay inline
        /// literals and never touch the CTE. ⚠ Both CTE columns are read by every referencing branch
        /// (<c>fn</c> in the literal comparison, <c>pos</c> in the anti-join), which is the bound-input
        /// non-prefix-projection invariant (docs/duckdb-upstream-issues.md §2).</para>
        /// </summary>
        private static BatchPlan? TryUnionForm(
            BatchPlan plain, DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols, string? where,
            bool hasTop)
        {
            // The remote gate (see remarks). A scheme separator marks a remote root (onelake://, s3://,
            // abfss://, …); a local path (drive letter or /) has none.
            if (!hasTop && listing.Files[0].Uri.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }
            var dvFiles = plain.LoopFiles;
            var boundFiles = new List<DeltaReader.NativeScanFile>();
            foreach (var f in dvFiles)
            {
                if (BindDv(f.Dv))
                {
                    boundFiles.Add(f);
                }
            }
            string dvView = NextViewName(DvViewName);
            var sb = new StringBuilder();
            if (boundFiles.Count > 0)
            {
                sb.Append($"WITH __fab_d AS MATERIALIZED (SELECT * FROM {Quote(dvView)}) ");
            }
            sb.Append(plain.Sql);
            foreach (var f in dvFiles)
            {
                string branch;
                try
                {
                    // The footer probe (presence/stored names for THIS file) — the D-probes cost stated in the
                    // remarks. Skipped for the zero-column (COUNT(*)) shape: FileSql dereferences the mapping
                    // only per data column, and probing D footers to count rows would be the full form's
                    // mistake in miniature.
                    var fm = dataCols.Count == 0 ? default : ResolveFileMapping(listing, f.Uri);
                    branch = FileSql(dataCols, wantRowId: false, where, f, fm, listing.TableSchema,
                                     listing.PartitionColumns,
                                     dvView: BindDv(f.Dv) ? "__fab_d" : null, dvFn: f.Uri);
                }
                catch (Exception ex)
                {
                    // A failed probe or an unrenderable typed-NULL backfill: decline the whole form (the
                    // PresentNames precedent — never guess at presence) and let Build fall back to the full
                    // form, whose own machinery answers or declines for itself.
                    Log.LogDebug("delta native union form declined at {Uri}: {Msg}", f.Uri, ex.Message);
                    return null;
                }
                sb.Append(" UNION ALL ").Append(branch);
            }
            Func<IReadOnlyList<(string, IArrowArrayStream)>>? inputs = null;
            IReadOnlyList<string>? viewNames = null;
            if (boundFiles.Count > 0)
            {
                var bound = boundFiles;
                inputs = () => new (string, IArrowArrayStream)[]
                {
                    (dvView, new SingleScanArrowStream(FileDvStream(bound), dvView)),
                };
                viewNames = new[] { dvView };
            }
            return new BatchPlan(sb.ToString(), listing.Files,
                                 System.Array.Empty<DeltaReader.NativeScanFile>(), inputs, viewNames);
        }

        /// <summary>
        /// The FULL single-call form: <c>read_parquet([…], schema = map {&lt;field id&gt;: …}, filename => true,
        /// file_row_number => true)</c> with the deletion vectors bound as ONE <c>(filename, pos)</c> input and
        /// the per-file constants (global ordinal for the rowid, partition values) as a second one, both wrapped
        /// in <c>WITH … AS MATERIALIZED</c> CTEs so each single-use stream is scanned exactly once. Covers EVERY
        /// file of the scan, deletion vectors included, so there is no loop left.
        /// <para><b>⚠ IT USES <c>union_by_name</c>, NOT THE <c>schema</c> MAP, AND THAT IS FORCED BY A DuckDB
        /// BUG</b> (reproduced on stock 1.5.5 with controls — docs/duckdb-upstream-issues.md §1; the assertion
        /// INVALIDATES THE DATABASE, so it is not a containable error). The obvious route is a field-id-keyed map,
        /// because <c>filename</c> / <c>file_row_number</c> compose with an INTEGER-keyed map and FAIL with a
        /// VARCHAR-keyed one. But a field-id-keyed map plus <c>file_row_number</c> (specifically —
        /// <c>filename</c> is unaffected) raises <c>INTERNAL Error: No default expression in FieldId Map</c>
        /// whenever the FILE contains a column that has no field id — and a materialized <c>__delta_row_id</c> is exactly that
        /// (row-tracking columns are not column-mapped, so they carry none). Row tracking is ON by default for
        /// tables we create, and every merge-on-read post-image file has that column, so the field-id route is
        /// unusable on the DEFAULT table shape. The same file reads fine with the map and NO virtual columns,
        /// which is what makes it an upstream assertion bug rather than a contract.</para>
        /// <para>So instead: <c>union_by_name => true</c> (which NULL-fills a column an older file predates — the
        /// schema-evolution backfill the map's <c>default_value</c> would have given) plus an explicit
        /// physical→logical alias projection. That needs no field ids, so there is nothing to probe and no
        /// assertion to trip. It works because a NAME-mode physical name is FILE-INDEPENDENT; id mode is gated
        /// off for the same reason as everywhere else.</para>
        /// <para>⚠ The price is nested columns: <c>union_by_name</c> MERGES struct interiors rather than
        /// declaring them, so a member no file carries cannot be projected at all (where the map's declared type
        /// would have NULL-filled it). Struct-typed columns therefore stay on the plain form or the loop — which
        /// is also what keeps the measured union struct hazard out of reach here.</para>
        /// <para><b>⚠ It gives up the deletion vector's PRUNABLE BOUND</b>, which the per-file path emits
        /// alongside the exact anti-join so DuckDB can skip whole row groups — one WHERE cannot carry a
        /// per-file range. That is a deliberate trade and the evidence is already in
        /// <see cref="DvRangeCondition"/>: its own controlled A/B found the bound "demonstrably works and does
        /// not show up in wall time" (2.17 s vs 2.21 s on 10M rows with 9M deleted), because marshalling and
        /// hashing the vector dominates either way. Expect that to change where I/O dominates instead — remote
        /// storage with a mostly-deleted file is the shape to re-measure before assuming this is free there.</para>
        /// <para><b>⚠ EVERY COLUMN OF A BOUND INPUT MUST BE READ BY THIS SQL — a correctness invariant.</b> A
        /// bound input carrying a column DuckDB's projection PRUNES makes the scan ask for a NON-PREFIX column
        /// set, which SEGFAULTS (confirmed upstream bug; repro <c>test/repro/duckdb_arrow_scan_nonprefix.c</c>,
        /// docs/duckdb-upstream-issues.md §2). That is why <see cref="MetaStream"/> takes
        /// <c>withFileOrdinal</c>: <c>file_ord</c> is emitted only when the rowid expression reads it. Partitioned
        /// tables were gated off this form until that was understood — they are supported now, and stay
        /// supported only as long as the invariant holds. ⚠ Do NOT try to fix a future unread column by
        /// wrapping the query: a subquery, a plain CTE and even a MATERIALIZED one all still crash (measured —
        /// projection pushdown goes straight through). Add a bound column only together with the SQL that
        /// reads it.</para>
        /// <para><b>ROW TRACKING IS SERVED HERE (lifted 2026-08-07)</b> —
        /// <c>COALESCE(materialized, baseRowId + file_row_number)</c> for the id, the per-file
        /// <c>defaultRowCommitVersion</c> for the version, with both constants riding the metadata input like
        /// <c>file_ord</c>. It had been gated on a reason that went stale: <i>"a materialized
        /// <c>__delta_row_id</c> is not column-mapped, so it has no field id to key by, and a DuckDB
        /// <c>map</c> cannot mix INTEGER and VARCHAR keys"</i> — every clause of which is about the field-id
        /// <c>schema</c> map this form ABANDONED when it moved to <c>union_by_name</c>. The gate was simply not
        /// revisited when the mechanism under it changed, which is the "a stale justification stops the next
        /// person looking" failure this codebase keeps recording. ⚠ What makes it work is that the materialized
        /// column is stored under its LITERAL name, so one <c>present</c> lookup answers it for the whole list,
        /// and <c>union_by_name</c> NULL-fills the files that lack it — which is exactly the COALESCE's
        /// fallthrough for a scan that MIXES materialized and derived files (an UPDATE's post-image file
        /// materializes; the untouched files do not). A rowid/tracking fast-path FILTER is still excluded, one
        /// gate earlier: its whole value is per-file row-group pruning, and one call has one WHERE.</para>
        /// </summary>
        private static BatchPlan? TryFullForm(
            DeltaReader.NativeScanList listing, IReadOnlyList<string> dataCols, bool wantRowId, string? where)
        {
            if (dataCols.Count == 0 || listing.TableSchema is null)
            {
                return null;
            }
            // ID mode + a struct is the measured silent-loss case (a struct interior is matched by NAME and cast,
            // and only in id mode can a file's stored child names differ) — see case 2 in the class remarks.
            if (listing.LogicalToFieldId is not null)
            {
                return null; // id mode: a file's stored names are its own vintage's — case 2
            }
            bool nameMapped = listing.LogicalToPhysical is not null || listing.MappedSchema is not null;

            var partitionCols = new List<string>();
            var entries = new List<string>(dataCols.Count);
            var inner = new List<string>(dataCols.Count + 2);
            var wanted = new List<(string Logical, string Stored, DeltaSchema.StructField Field)>(dataCols.Count);
            var trackingCols = new List<string>(2);
            foreach (var c in dataCols)
            {
                if (string.Equals(c, RowTrackingIdColumn, StringComparison.Ordinal)
                    || string.Equals(c, RowTrackingVersionColumn, StringComparison.Ordinal))
                {
                    // Virtual: not in the table schema at all. Its expression needs `present` (is the
                    // materialized column stored in ANY file of this list?), which is resolved after this loop,
                    // so only the name is recorded here. `inner` is order-insensitive — the outer SELECT names
                    // its columns — so appending them later is free.
                    trackingCols.Add(c);
                    continue;
                }
                var field = FindField(listing.TableSchema, c);
                if (field is null)
                {
                    return null;
                }
                if (listing.PartitionColumns.Count > 0 && ContainsName(listing.PartitionColumns, c))
                {
                    // Absent from the data files: the value comes from the bound per-file input, CAST to the
                    // column's declared type (the input carries every partition value as VARCHAR, since one
                    // Arrow column cannot hold several columns' types).
                    string type0;
                    try
                    {
                        type0 = TypeText(field.Type);
                    }
                    catch (NotSupportedException)
                    {
                        return null;
                    }
                    inner.Add($"CAST(__fab_f.{Quote("p" + partitionCols.Count)} AS {type0}) AS {Quote(c)}");
                    partitionCols.Add(c);
                    continue;
                }
                if (ContainsStructAnywhere(field.Type))
                {
                    // union_by_name merges struct interiors instead of declaring them, so a member no file
                    // carries is unprojectable. The plain `schema`-map form handles nested columns; this one
                    // does not pretend to.
                    return null;
                }
                string stored = nameMapped ? PhysicalName(field) : field.Name;
                entries.Add(stored);
                wanted.Add((c, stored, field));
            }
            if (entries.Count == 0 && trackingCols.Count == 0)
            {
                return null; // every requested column was a partition column: nothing to read from the files
            }
            var files = listing.Files;
            // ⚠ ONE presence query for the whole scan, and it is REQUIRED, not an optimisation.
            // union_by_name can only produce a column that SOME file in the list carries; a column absent from
            // every one of them is a BINDER ERROR ("Referenced column ... not found in FROM clause"). That is not
            // an edge case: Delta-log pruning routinely drops the only file holding a newly-ADDed column, which
            // is how `WHERE extra IS NULL` broke on a table whose other six files predate `extra`. So the
            // columns present across the LIST are resolved up front and the rest become typed NULLs — the
            // backfill the `schema` map's default_value would have given. parquet_schema takes the whole list, so
            // this is one query per scan, never one per file.
            var present = PresentNames(files);
            if (present is null)
            {
                return null;
            }
            foreach (var w in wanted)
            {
                string expr;
                if (present.Contains(w.Stored))
                {
                    expr = string.Equals(w.Stored, w.Logical, StringComparison.Ordinal)
                        ? Quote(w.Logical) : $"{Quote(w.Stored)} AS {Quote(w.Logical)}";
                }
                else
                {
                    string t;
                    try
                    {
                        t = TypeText(w.Field.Type);
                    }
                    catch (NotSupportedException)
                    {
                        return null;
                    }
                    expr = $"CAST(NULL AS {t}) AS {Quote(w.Logical)}";
                }
                inner.Add(expr);
            }

            bool anyDv = false;
            foreach (var f in files)
            {
                if (f.Dv.Length > 0)
                {
                    anyDv = true;
                    break;
                }
            }
            // The row-tracking virtual columns, expressed exactly as the per-file path does (see RowTrackingExpr)
            // but with the per-file constants arriving on the bound input instead of being inlined:
            //   __delta_row_id             = COALESCE(materialized, baseRowId + file_row_number)
            //   __delta_row_commit_version = COALESCE(materialized, defaultRowCommitVersion)
            // ⚠ Two things make the batched form work where the per-file one needed a footer probe. (a) The
            // materialized column is stored under its LITERAL name (materialized columns are not column-mapped),
            // so one `present` lookup over the whole list answers it — and `union_by_name` NULL-fills the files
            // that lack it, which is precisely the COALESCE's fallthrough. (b) baseRowId / commitVersion are
            // per-FILE constants, so they ride the metadata input like `file_ord`. A file with neither reads
            // NULL, same as before.
            bool wantTrackId = ContainsName(trackingCols, RowTrackingIdColumn);
            bool wantTrackVersion = ContainsName(trackingCols, RowTrackingVersionColumn);
            foreach (var c in trackingCols)
            {
                bool isId = string.Equals(c, RowTrackingIdColumn, StringComparison.Ordinal);
                string derived = isId
                    ? "(__fab_f.base_row_id + __fab_rp.file_row_number)"
                    : "__fab_f.commit_version";
                string expr = present.Contains(c)
                    ? $"COALESCE(__fab_rp.{Quote(c)}, {derived})"
                    : derived;
                inner.Add($"{expr} AS {Quote(c)}");
            }
            bool needsMeta = wantRowId || partitionCols.Count > 0 || trackingCols.Count > 0;
            if (wantRowId)
            {
                inner.Add($"((__fab_f.file_ord << {TransientRowAddress.PositionBits}) | __fab_rp.file_row_number) "
                          + $"AS {Quote(RowIdColumn)}");
            }

            string metaView = NextViewName(MetaViewName);
            string dvView = NextViewName(DvViewName);
            var ctes = new List<string>(2);
            if (needsMeta)
            {
                // MATERIALIZED is load-bearing, not a hint: the view is a single-use stream, so a second scan
                // would silently contribute nothing (SingleScanArrowStream turns that into an error).
                ctes.Add($"__fab_f AS MATERIALIZED (SELECT * FROM {Quote(metaView)})");
            }
            if (anyDv)
            {
                ctes.Add($"__fab_d AS MATERIALIZED (SELECT * FROM {Quote(dvView)})");
            }

            var sb = new StringBuilder();
            if (ctes.Count > 0)
            {
                sb.Append("WITH ").Append(string.Join(", ", ctes)).Append(' ');
            }
            sb.Append("SELECT ").Append(string.Join(", ", dataCols.Select(Quote)));
            if (wantRowId)
            {
                sb.Append(", ").Append(Quote(RowIdColumn));
            }
            sb.Append(" FROM (SELECT ").Append(string.Join(", ", inner))
              .Append(" FROM read_parquet([").Append(string.Join(", ", files.Select(f => Literal(f.Uri))))
              // hive_partitioning is OFF deliberately, and it is a correctness guard rather than the workaround
              // it was first tried as (it does NOT cure the upstream non-prefix crash). DuckDB AUTO-DETECTS
              // hive layout from the paths, so any directory of the form `x=y` anywhere in a table's path would
              // inject a phantom column into the scan. The Delta log's partitionValues is the authoritative
              // source — paths are opaque here and are never parsed.
              .Append("], union_by_name => true, hive_partitioning => false, filename => true, "
                     + "file_row_number => true) __fab_rp");
            if (needsMeta)
            {
                // INNER join on the exact URI string we listed — `filename` was verified to echo it verbatim.
                sb.Append(" JOIN __fab_f ON __fab_f.fn = __fab_rp.filename");
            }
            if (anyDv)
            {
                // NOT EXISTS, never NOT IN (SELECT …): one NULL position in an IN-subquery makes the predicate
                // NULL for every row and returns an EMPTY table.
                sb.Append(" WHERE NOT EXISTS (SELECT 1 FROM __fab_d WHERE __fab_d.fn = __fab_rp.filename")
                  .Append(" AND __fab_d.pos = __fab_rp.file_row_number)");
            }
            sb.Append(')');
            if (!string.IsNullOrEmpty(where))
            {
                sb.Append(" WHERE ").Append(where);
            }

            var partCols = partitionCols;
            Func<IReadOnlyList<(string, IArrowArrayStream)>>? inputs = null;
            if (needsMeta || anyDv)
            {
                inputs = () =>
                {
                    var list = new List<(string, IArrowArrayStream)>(2);
                    if (needsMeta)
                    {
                        list.Add((metaView,
                                  new SingleScanArrowStream(
                                      MetaStream(files, partCols, listing, withFileOrdinal: wantRowId,
                                                 withBaseRowId: wantTrackId,
                                                 withCommitVersion: wantTrackVersion),
                                      metaView)));
                    }
                    if (anyDv)
                    {
                        list.Add((dvView, new SingleScanArrowStream(FileDvStream(files), dvView)));
                    }
                    return list;
                };
            }
            var viewNames = new List<string>(2);
            if (needsMeta) { viewNames.Add(metaView); }
            if (anyDv) { viewNames.Add(dvView); }
            return new BatchPlan(sb.ToString(), files, System.Array.Empty<DeltaReader.NativeScanFile>(), inputs,
                                 viewNames);
        }

        private static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
    }

    /// <summary>The name of the bound per-file constants input (global ordinal, partition values) the FULL
    /// batched form joins on <c>filename</c>. Sibling of <see cref="DvViewName"/>.</summary>
    internal const string MetaViewName = "__fab_files";


    // The set of stored column names present in AT LEAST ONE of these files, from a single parquet_schema over
    // the whole list. Null when the query fails (fall back rather than guess at presence).
    private static HashSet<string>? PresentNames(IReadOnlyList<DeltaReader.NativeScanFile> files)
    {
        var sb = new StringBuilder("SELECT DISTINCT name FROM parquet_schema([");
        for (int i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append("'").Append(files[i].Uri.Replace("'", "''")).Append("'");
        }
        sb.Append("])");
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            using var s = Host.Query(sb.ToString());
            while (true)
            {
                var batch = s.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                if (batch is null)
                {
                    break;
                }
                using (batch)
                {
                    var col = (StringArray)batch.Column(0);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        if (col.GetString(i) is { } n)
                        {
                            names.Add(n);
                        }
                    }
                }
            }
            return names;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsStructAnywhere(DeltaSchema.DeltaDataType type) => type switch
    {
        DeltaSchema.StructType => true,
        DeltaSchema.ArrayType at => ContainsStructAnywhere(at.ElementType),
        DeltaSchema.MapType mt => ContainsStructAnywhere(mt.KeyType) || ContainsStructAnywhere(mt.ValueType),
        _ => false,
    };

    /// <summary>Every file's deletion-vector positions as ONE <c>(fn, pos)</c> stream — the single-input form the
    /// batched read anti-joins, replacing the per-file inline literal list whose cost is characterised on
    /// <see cref="DvLiteralMax"/> (~0.4 ms and ~1.2 KB per deleted row). Columns are NON-NULLABLE by
    /// construction.</summary>
    private static IArrowArrayStream FileDvStream(IReadOnlyList<DeltaReader.NativeScanFile> files)
    {
        var fn = new StringArray.Builder();
        var pos = new Int64Array.Builder();
        long n = 0;
        foreach (var f in files)
        {
            foreach (var p in f.Dv)
            {
                fn.Append(f.Uri);
                pos.Append(p);
                n++;
            }
        }
        var schema = new Schema(new[]
        {
            new Field("fn", StringType.Default, nullable: false),
            new Field("pos", Int64Type.Default, nullable: false),
        }, null);
        return new InMemoryArrayStream(
            schema, new[] { new RecordBatch(schema, new IArrowArray[] { fn.Build(), pos.Build() }, (int)n) });
    }

    /// <summary>The per-file constants the batched read joins on <c>filename</c>: the file's ordinal (which the
    /// transient rowid folds in) and each requested partition column's raw value as VARCHAR, cast to the column's
    /// declared type in SQL. One row per file, so the join is 1:1 and cannot duplicate rows.
    /// <para>⚠ <c>file_ord</c> is the FILE's index in the scan's list — <b>one value per file, not per row</b>.
    /// It is nothing like SQL's <c>WITH ORDINALITY</c>, which numbers rows AS THEY ARE EMITTED and would be
    /// nondeterministic under a parallel multi-row-group scan. The only per-row half of the rowid is
    /// <c>file_row_number</c>, which DuckDB derives from the parquet footer's row-group offsets, so neither
    /// half depends on emission order (DuckDB guarantees none). And the ordinal is attached by a JOIN on
    /// <c>filename</c>, never zipped positionally, so it cannot land on the wrong file.</para>
    /// <para>⚠ <b>EVERY COLUMN HERE MUST BE READ BY THE GENERATED SQL — that is a CORRECTNESS invariant, not
    /// tidiness.</b> A bound input carrying a column DuckDB's projection PRUNES makes the scan ask for a
    /// NON-PREFIX column set, which SEGFAULTS (or corrupts a string length into an assertion that invalidates
    /// the database): confirmed upstream bug, repro in
    /// <c>test/repro/duckdb_arrow_scan_nonprefix.c</c>, docs/duckdb-upstream-issues.md §2. Hence
    /// <paramref name="withFileOrdinal"/> — <c>file_ord</c> is emitted only when the rowid expression reads it, so the
    /// consumed set always equals the produced set and the bug is unreachable BY CONSTRUCTION rather than by
    /// luck. ⚠ Wrapping the query in a subquery or even a MATERIALIZED CTE does NOT help (measured — projection
    /// pushdown goes straight through), so do not "fix" a future column that way. Add a column here only
    /// together with the SQL that reads it.</para></summary>
    private static IArrowArrayStream MetaStream(
        IReadOnlyList<DeltaReader.NativeScanFile> files, IReadOnlyList<string> partitionCols,
        DeltaReader.NativeScanList listing, bool withFileOrdinal, bool withBaseRowId = false,
        bool withCommitVersion = false)
    {
        var fn = new StringArray.Builder();
        var ord = new Int64Array.Builder();
        var baseRowId = new Int64Array.Builder();
        var commitVersion = new Int64Array.Builder();
        var parts = new StringArray.Builder[partitionCols.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = new StringArray.Builder();
        }
        foreach (var f in files)
        {
            fn.Append(f.Uri);
            ord.Append(f.Ordinal);
            if (f.BaseRowId is { } brid)
            {
                baseRowId.Append(brid);
            }
            else
            {
                baseRowId.AppendNull();
            }
            if (f.CommitVersion is { } cv)
            {
                commitVersion.Append(cv);
            }
            else
            {
                commitVersion.AppendNull();
            }
            for (int i = 0; i < parts.Length; i++)
            {
                var field = FindField(listing.TableSchema, partitionCols[i]);
                string stored = field is not null ? PhysicalName(field) : partitionCols[i];
                var v = LookupPartitionValue(f.PartitionValues, partitionCols[i], stored);
                if (v is null)
                {
                    parts[i].AppendNull();
                }
                else
                {
                    parts[i].Append(v);
                }
            }
        }
        var fields = new List<Field>(2 + parts.Length)
        {
            new Field("fn", StringType.Default, nullable: false),
        };
        var arrays = new List<IArrowArray>(2 + parts.Length) { fn.Build() };
        if (withFileOrdinal)
        {
            fields.Add(new Field("file_ord", Int64Type.Default, nullable: false));
            arrays.Add(ord.Build());
        }
        // ⚠ Each of these is emitted ONLY when the generated SQL reads it — the bound-input invariant (an
        // unread column makes DuckDB ask the scan for a non-prefix column set, which segfaults). Nullable
        // because a file may carry no row tracking at all, which is the NULL the COALESCE falls through to.
        if (withBaseRowId)
        {
            fields.Add(new Field("base_row_id", Int64Type.Default, nullable: true));
            arrays.Add(baseRowId.Build());
        }
        if (withCommitVersion)
        {
            fields.Add(new Field("commit_version", Int64Type.Default, nullable: true));
            arrays.Add(commitVersion.Build());
        }
        for (int i = 0; i < parts.Length; i++)
        {
            fields.Add(new Field("p" + i.ToString(CultureInfo.InvariantCulture), StringType.Default, nullable: true));
            arrays.Add(parts[i].Build());
        }
        var schema = new Schema(fields, null);
        return new InMemoryArrayStream(schema, new[] { new RecordBatch(schema, arrays, files.Count) });
    }

    // The PHYSICAL (in-file) name of a top-level or nested field under NAME-mode column mapping: the declared
    // physicalName, else the logical name (no mapping). Deliberately does NOT consult field ids — BatchPlan
    // gates id mode out precisely because that answer is per-file.
    private static string PhysicalName(DeltaSchema.StructField field)
        => field.Metadata is { } md
           && md.TryGetValue(DeltaSchema.ColumnMapping.PhysicalNameKey, out var phys)
           && !string.IsNullOrEmpty(phys)
            ? phys
            : field.Name;

    // Rebuilds a mapped struct with LOGICAL member names for the batched read, or null when nothing at any depth
    // needs renaming (so the caller can skip the wrapping projection entirely). The physical-name lookup is the
    // schema's declared physicalName, which is FILE-INDEPENDENT in name mode — that is the whole reason one
    // expression can serve every file here where the per-file path needs a per-file FileMapping. The CASE keeps a
    // NULL struct NULL (struct_pack alone would materialize a non-NULL struct of NULLs). List/map element structs
    // pass through with physical interiors, exactly as RebuildExpr leaves them: no struct-member predicate can
    // reach inside a list/map, and ArrowColumnMappingRename fixes the names per batch.
    private static string? LogicalStructExpr(DeltaSchema.DeltaDataType type, string src)
    {
        if (type is not DeltaSchema.StructType st || st.Fields.Count == 0)
        {
            return null;
        }
        var parts = new List<string>(st.Fields.Count);
        bool changed = false;
        foreach (var ch in st.Fields)
        {
            string phys = PhysicalName(ch);
            changed |= !string.Equals(phys, ch.Name, StringComparison.Ordinal);
            string childSrc = $"({src}).{Quote(phys)}";
            var nested = LogicalStructExpr(ch.Type, childSrc);
            changed |= nested is not null;
            parts.Add($"{Quote(ch.Name)} := {nested ?? childSrc}");
        }
        return changed
            ? $"CASE WHEN {src} IS NULL THEN NULL ELSE struct_pack({string.Join(", ", parts)}) END"
            : null;
    }

    // TypeText's sibling for the batched read's CAST target: struct member names are the PHYSICAL ones, because
    // `schema` matches a struct's interior BY NAME against what the file stores and only the top level is
    // renamed by 'name'. Nested logical names are restored per batch by ArrowColumnMappingRename, exactly as on
    // the per-file path (which likewise projects physical struct children and renames afterwards).
    private static string PhysicalTypeText(DeltaSchema.DeltaDataType type, bool nameMapped) => type switch
    {
        DeltaSchema.StructType st => "STRUCT(" + string.Join(
            ", ", st.Fields.Select(f =>
                $"{Quote(nameMapped ? PhysicalName(f) : f.Name)} {PhysicalTypeText(f.Type, nameMapped)}")) + ")",
        DeltaSchema.ArrayType at => PhysicalTypeText(at.ElementType, nameMapped) + "[]",
        DeltaSchema.MapType mt =>
            $"MAP({PhysicalTypeText(mt.KeyType, nameMapped)}, {PhysicalTypeText(mt.ValueType, nameMapped)})",
        // A variant column's logical type is the registered extension type, not a CAST target the parquet
        // reader can be handed — refuse rather than guess (BatchPlan falls back to the per-file path).
        DeltaSchema.PrimitiveType { TypeName: "variant" } => throw new NotSupportedException(
            "delta native batched read: variant columns are read per file."),
        _ => TypeText(type),
    };

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
    /// <param name="dvView">Name of a bound Arrow input carrying this file's deletion-vector positions, or
    /// null to always inline them as literals. See <see cref="DvCondition"/> — a caller that passes a name
    /// MUST bind that input whenever <see cref="BindDv"/> is true for the file, and the two decisions read
    /// the same predicate so they cannot drift.</param>
    /// <param name="dvFn">Set (to this file's URI) when <paramref name="dvView"/> names a SHARED
    /// <c>(fn, pos)</c> input rather than a per-file positions one — the union form, where several branches
    /// of ONE query each anti-join their own filename slice.</param>
    private static string FileSql(IReadOnlyList<string> dataCols, bool wantRowId, string? where,
                                  DeltaReader.NativeScanFile f, FileMapping fm,
                                  DeltaSchema.StructType? tableSchema,
                                  IReadOnlyList<string>? partitionCols = null,
                                  string? rowIdCond = null,
                                  string? innerCond = null,
                                  string? dvView = null,
                                  string? dvFn = null)
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
            conds.Add(DvCondition(f.Dv, dvView, dvFn));
            // ...plus a PRUNABLE bound on the same vector. Exactness comes from the condition above; this
            // conjunct exists purely so DuckDB's parquet reader can skip whole row groups (see
            // DvRangeCondition).
            if (DvRangeCondition(f.Dv, f.NumRecords) is { } dvRange)
            {
                conds.Add(dvRange);
            }
        }
        if (conds.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", conds));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The name of the connection-scoped Arrow input a per-file SELECT anti-joins its deletion vector from.
    /// One input per QUERY: the streaming scan issues one <see cref="Host.Query(string)"/> per file, so this
    /// view is scanned exactly once and a single-use Arrow stream is safe. (<see cref="FullTableSql"/> unions
    /// many files into ONE query and therefore does NOT use it — see its remarks.)
    /// </summary>
    internal const string DvViewName = "__fab_dv";

    /// <summary>
    /// A per-QUERY unique name for a bound input view. ⚠ Load-bearing, not hygiene: DuckDB's
    /// <c>duckdb_arrow_scan</c> registers the input with <c>CreateView(name, replace: true, temporary: FALSE)</c>
    /// — a CATALOG-level view, shared by every connection on the database and silently replacing any existing
    /// one — and it must stay alive until the streaming result has been fully fetched. Two host queries binding
    /// the same name therefore race over the whole fetch. MEASURED with two shipped, documented settings
    /// (<c>FABRICATOR_DELTA_PREFETCH=8</c> + <c>FABRICATOR_DELTA_BATCH_MIN_FILES=0</c>, so each deletion-vector
    /// file gets its own concurrent query): every scan failed with
    /// <c>failed to register input view '__fab_dv'</c>. That is the LOUD outcome; the same race can also let one
    /// query's view be replaced by another's stream, which is silent wrong rows.
    /// </summary>
    private static long _viewSeq;

    private static string NextViewName(string prefix)
        => prefix + "_" + System.Threading.Interlocked.Increment(ref _viewSeq).ToString(CultureInfo.InvariantCulture);

    /// <summary>Drops the bound-input views once their query has been fully drained. Needed because the view is
    /// NOT temporary: it outlives the connection that made it and shows up in the user's <c>duckdb_views()</c>
    /// (measured). With one shared name that was a single stale entry; with per-query names it would accumulate
    /// one per scan, so dropping is what makes uniqueness affordable. Best-effort — a failure here must never
    /// fail the scan, which already produced its rows.</summary>
    private static void DropViews(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }
        try
        {
            var sb = new StringBuilder();
            foreach (var n in names)
            {
                sb.Append("DROP VIEW IF EXISTS ").Append(Quote(n)).Append("; ");
            }
            using var _ = Host.Query(sb.ToString());
        }
        catch (Exception ex)
        {
            Log.LogDebug("delta native: dropping bound-input views failed ({Msg})", ex.Message);
        }
    }

    /// <summary>
    /// Deletion vectors at or below this many positions stay an inline <c>file_row_number NOT IN (…)</c>
    /// literal list; above it they are bound as an Arrow input and anti-joined.
    /// <para>The value is DuckDB'S OWN <c>IN</c>-rewrite boundary, not a number of ours.
    /// <c>InClauseRewriter::VisitReplace</c> (<c>src/optimizer/in_clause_rewriter.cpp</c>) keeps
    /// <c>children.size() &lt; 6</c> — i.e. up to 4 values beside the LHS — as a plain conjunction of
    /// <c>&lt;&gt;</c> comparisons, and from 5 values up materialises a <c>ColumnDataCollection</c> and a MARK
    /// JOIN. So below the boundary the predicate really is just an expression (no join, no collection, and no
    /// Arrow input to marshal per file), while at or above it DuckDB builds a join anyway — at which point
    /// handing it a bound input is strictly better, because it skips parsing and evaluating N literals to get
    /// there. ⚠ That 6 is HARDCODED upstream with no setting to tune it (checked <c>src/main/settings/</c> and
    /// <c>config.hpp</c>), so a future DuckDB bump could move it silently; this is the constant to re-check.</para>
    /// <para>⚠ An earlier version of this comment justified a much larger threshold (256) on the grounds that
    /// the inline form stays row-group-prunable where an anti-join does not. That was WRONG twice over: the
    /// rewrite above makes any list of 5+ values a mark join, exactly as opaque as our anti-join; and a NOT IN
    /// over scattered positions could hardly prune a row group anyway (the prunable predicate nearby is
    /// <c>rowIdCond</c>, a positive range). Kept as a note because the number looked principled and was not.</para>
    /// <para>Why the inlining had to go at all — the cost is in CONSTRUCTION, not evaluation, since the rewrite
    /// is a planner pass that runs only after the text has been parsed and bound into one
    /// <c>BoundConstantExpression</c> per deleted row and then <c>TryEvaluateScalar</c>'d one value at a time.
    /// MEASURED on a 200k-row table, scanning after deleting N rows: N=0 → 1 s/68 MB, 10k → 5 s/85 MB,
    /// 100k → 42 s/196 MB, 199k → 68 s/301 MB, i.e. ~0.4 ms and ~1.2 KB PER DELETED ROW. The control is
    /// decisive: the SAME 100k rows deleted copy-on-write (no DV at all) scanned in 1 s/66 MB, so the
    /// predicate and not the deletion was 42x the cost. With the anti-join, 199k scans in 1.1 s/93 MB — FLAT
    /// in vector size and indistinguishable from the no-DV control. It matters because the cost was paid by
    /// EVERY read until an OPTIMIZE rewrote the files, so an incrementally-merged table got slower every run.</para>
    /// </summary>
    private const int DvLiteralMax = 4;

    /// <summary>True when <paramref name="dv"/> is large enough to bind as an Arrow input rather than inline.
    /// The SQL builder and the input binder BOTH call this, so the condition and the binding cannot diverge —
    /// a mismatch would either reference an unbound view (a loud error) or, far worse, bind nothing and
    /// silently resurrect deleted rows.</summary>
    private static bool BindDv(long[] dv) => dv.Length > DvLiteralMax;

    /// <summary>The DV exclusion predicate: an anti-join against the bound input for a large vector, else the
    /// inline literal list. <paramref name="dvFn"/> selects the input's SHAPE: null means a per-file input
    /// carrying positions only (the loop's <see cref="DvStream"/>, one query per file so no discrimination is
    /// needed); a file URI means a SHARED <c>(fn, pos)</c> input (<see cref="FileDvStream"/>) that several
    /// branches of ONE query slice by filename — the union form, where each branch knows its own file as a
    /// literal so no <c>filename => true</c> virtual column is needed.</summary>
    private static string DvCondition(long[] dv, string? dvView, string? dvFn = null) =>
        dvView is not null && BindDv(dv)
            ? dvFn is null
                ? $"NOT EXISTS (SELECT 1 FROM {dvView} d WHERE d.pos = file_row_number)"
                : $"NOT EXISTS (SELECT 1 FROM {dvView} d WHERE d.fn = '{dvFn.Replace("'", "''")}' AND d.pos = file_row_number)"
            : $"file_row_number NOT IN ({string.Join(",", dv.Select(p => p.ToString(CultureInfo.InvariantCulture)))})";

    /// <summary>
    /// A PRUNABLE bound on the surviving positions, derived from the deletion vector — emitted ALONGSIDE
    /// <see cref="DvCondition"/>, never instead of it.
    /// <para>Why it is needed at all: only a predicate on the RAW <c>file_row_number</c> column can skip row
    /// groups. DuckDB synthesizes exact per-row-group min/max for it — <c>ParquetColumnSchema::Stats</c> has an
    /// explicit <c>FILE_ROW_NUMBER</c> branch computing them from the row-group row offsets — but neither of
    /// the exact forms is visible to that: our <c>NOT EXISTS</c> becomes an anti-join ABOVE the scan, and the
    /// inline <c>NOT IN</c> becomes a mark join too as soon as it has 5+ values
    /// (<see cref="DvLiteralMax"/>). So without this conjunct a deletion vector prunes NOTHING, and the file
    /// is read in full however much of it is deleted.</para>
    /// <para>It excludes only leading/trailing positions ENTIRELY covered by the vector, so it cannot drop a
    /// surviving row, and exactness stays with the anti-join — a bug here degrades pruning rather than
    /// corrupting results. ⚠ THAT HOLDS UNCONDITIONALLY FOR THE LOWER BOUND ONLY, which is derived from the
    /// vector alone. The UPPER bound needs to know where the file ENDS, so it trusts
    /// <paramref name="numRecords"/> = the add action's <c>stats.numRecords</c>: if a non-conforming writer
    /// UNDERSTATED it, rows past the claimed end could be excluded while still live. That is the same trust
    /// every Delta engine places in that field (it is what answers <c>count(*)</c> without touching data) and
    /// the same this reader already places on it for row-tracking derived-id ranges — but it is an assumption,
    /// not a construction, and it is why the bound is omitted entirely when stats are absent.</para>
    /// <para>Shape it serves is the common one: a contiguous delete (a merge that removed a batch, or
    /// <c>DELETE … WHERE id &lt;= N</c>) collapses to one <c>&gt;=</c> / <c>&lt;=</c>. A scattered vector yields
    /// no usable bound and this returns null rather than emitting a no-op conjunct.</para>
    /// <para>IT DEMONSTRABLY WORKS, AND IT DOES NOT SHOW UP IN WALL TIME — both halves verified, because each
    /// alone invites the wrong conclusion. Read from <c>EXPLAIN ANALYZE</c> on a 1M-row / 9-row-group file with
    /// a contiguous 900k prefix deleted: the scan operator carries <c>Filters: file_row_number&gt;=900000</c> and
    /// emits <b>100,000 rows</b>, against <b>1,000,000</b> (the whole file) with the conjunct removed — a 10x
    /// cut in scan output, so the predicate really is pushed into <c>READ_PARQUET</c>. The same <c>EXPLAIN</c>
    /// confirms the exact form is a <c>HASH_JOIN / RIGHT_ANTI</c> above the scan, i.e. invisible to pruning,
    /// which is why this conjunct has to exist separately.</para>
    /// <para>Yet a controlled A/B on the SAME table (10M rows x 4 cols, contiguous 9M deleted, bound on vs off,
    /// 3 runs) timed 2.17s vs 2.21s best — no difference. Both results are real: the scan is not the bottleneck
    /// at that shape, because marshalling and hashing a multi-million-position vector dominates and is paid
    /// identically either way. Expect the win to appear where I/O dominates instead (remote storage) or where
    /// the vector is small relative to the file.</para>
    /// <para>⚠ TWO MEASUREMENT TRAPS, both fallen into here in sequence. (1) An earlier comparison showing
    /// 2.23s vs 3.33s was CONFOUNDED — it put a contiguous vector against a SCATTERED one, measuring DV shape
    /// (1M probes vs 10M), not pruning. (2) The clean timing A/B then said "no benefit", which is true about
    /// TIME and false about EFFECT. Only the PLAN settles what the SQL does; timing can only say whether it
    /// mattered. If this ever needs re-justifying, read the scan cardinality in EXPLAIN ANALYZE, do not
    /// re-time it.</para>
    /// <para>The upper bound needs the file's row count, which the add action's stats supply and external
    /// writers may omit — hence <paramref name="numRecords"/> is nullable and only the lower bound is emitted
    /// when it is unknown. ⚠ <c>lo &gt; hi</c> means the file is entirely deleted and should not be read at
    /// all; that is a planning-time skip (the listing still carries such files today) and is deliberately not
    /// smuggled in here.</para>
    /// </summary>
    private static string? DvRangeCondition(long[] dv, long? numRecords)
    {
        if (dv.Length == 0)
        {
            return null;
        }
        // dv is sorted ascending. Walk the dense prefix: the first position NOT present is the smallest
        // surviving one. `<=` rather than `==` so a duplicated position cannot stall the walk.
        long lo = 0;
        for (int i = 0; i < dv.Length && dv[i] <= lo; i++)
        {
            if (dv[i] == lo)
            {
                lo++;
            }
        }
        var conds = new List<string>(2);
        if (lo > 0)
        {
            conds.Add($"file_row_number >= {lo.ToString(CultureInfo.InvariantCulture)}");
        }
        if (numRecords is { } n && n > 0)
        {
            long hi = n - 1;
            for (int j = dv.Length - 1; j >= 0 && dv[j] >= hi; j--)
            {
                if (dv[j] == hi)
                {
                    hi--;
                }
            }
            if (hi < n - 1)
            {
                conds.Add($"file_row_number <= {hi.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        return conds.Count == 0 ? null : string.Join(" AND ", conds);
    }

    /// <summary>This file's deletion-vector positions as a one-column Arrow stream for
    /// <see cref="Host.Query(string, IReadOnlyList{ValueTuple{string, IArrowArrayStream}})"/>.
    /// <para>⚠ The column is NON-NULLABLE by construction and that matters: we chose <c>NOT EXISTS</c> over
    /// <c>NOT IN (SELECT …)</c> precisely because a single NULL position in an IN-subquery makes the predicate
    /// NULL for every row and silently returns an EMPTY table. NOT EXISTS is null-safe regardless, so the two
    /// defences are independent.</para></summary>
    private static IArrowArrayStream DvStream(long[] dv)
    {
        var builder = new Int64Array.Builder();
        builder.Reserve(dv.Length);
        foreach (var pos in dv)
        {
            builder.Append(pos);
        }
        var schema = new Schema(new[] { new Field("pos", Int64Type.Default, nullable: false) }, null);
        return new InMemoryArrayStream(
            schema, new[] { new RecordBatch(schema, new IArrowArray[] { builder.Build() }, dv.Length) });
    }

    /// <summary>Runs a per-file SELECT, binding the file's deletion vector as an Arrow input when it is large
    /// enough that inlining it would be pathological (<see cref="DvLiteralMax"/>).</summary>
    private static IArrowArrayStream QueryFile(string sql, DeltaReader.NativeScanFile f, string dvView)
        => BindDv(f.Dv)
            ? Host.Query(sql, new (string, IArrowArrayStream)[]
                              { (dvView, new SingleScanArrowStream(DvStream(f.Dv), dvView)) })
            : Host.Query(sql);

    /// <summary>The WHOLE table as one SQL text: the per-file SELECTs (logical names, DV exclusion, column
    /// mapping, schema-evolution backfill, partition literals, row-tracking expressions for
    /// <see cref="RowTrackingIdColumn"/>/<see cref="RowTrackingVersionColumn"/> entries in
    /// <paramref name="dataCols"/>) joined by UNION ALL. Serves the clustered-OPTIMIZE rewrite, which needs a
    /// single query it can ORDER BY globally (DuckDB's spilling sort) and feed straight into a COPY —
    /// zero boundary crossings for the data. NOT usable for nested MAPPED columns (the per-batch
    /// <see cref="ArrowColumnMappingRename"/> has no hook inside one SQL statement — callers gate).
    /// <para>⚠ That gate is load-bearing and the failure is SILENT — MEASURED 2026-08-06. <c>UNION ALL</c> merges
    /// STRUCT INTERIORS BY NAME and NULL-fills what a branch lacks, so two files storing a struct child under
    /// different physical names yield ONE struct carrying BOTH names with half the values NULL, and no error:
    /// on children (a, b) where one file renamed only b, the union produced
    /// <c>{'a': …, 'b': …, 'col-b': NULL}</c> / <c>{'a': …, 'b': NULL, 'col-b': …}</c>. Note this is the union's
    /// OWN hazard, distinct from (and worse than) the cast-based one <see cref="BatchPlan"/> documents: the
    /// output TYPE is wrong too. Any future union route must normalise struct child names IN SQL per branch —
    /// which for NAME mode is what <see cref="LogicalStructExpr"/> already does, since physical names there are
    /// file-independent; for ID mode it needs upstream #24407.</para>
    /// <para>⚠ This form passes NO <c>dvView</c>, so it still INLINES deletion-vector positions as literals
    /// and keeps the cost characterised on <see cref="DvLiteralMax"/> (~0.4 ms + ~1.2 KB per deleted row).
    /// That is deliberate, not an oversight: it unions many files into ONE query, so a single bound input
    /// would be scanned once per branch and a single-use Arrow stream would be EXHAUSTED after the first —
    /// silently contributing no exclusions, i.e. resurrecting deleted rows. Fixing it needs the vector bound
    /// as one <c>(ordinal, pos)</c> input wrapped in a <c>WITH … AS MATERIALIZED</c> CTE (verified supported
    /// on 1.5.5) so it is evaluated exactly once, with each branch anti-joining its own ordinal, plus the
    /// input threaded through this method's caller (the clustered-OPTIMIZE rewrite, DeltaReader). Its blast
    /// radius is bounded to that one rewrite path rather than every scan.</para></summary>
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
                                      IReadOnlyList<string> dataCols, bool wantRowId, BatchPlan? batch)
    {
        if (batch is not null)
        {
            // Probe the BATCH's own SQL: it is the one query that must agree with the advertised schema.
            // ⚠ WHAT THIS COSTS DEPENDS ENTIRELY ON WHICH FORM WAS CHOSEN, and a comment here used to claim
            // "no footer read (the whole point of the `schema` map)" for both — true of the plain form,
            // MEASURED FALSE of the full one. Over 89 remote files, cold: plain-form probe 0.49 s (the schema
            // is DECLARED, so nothing is opened) vs full-form probe 20.66 s, on top of the 20.50 s PresentNames
            // already spent building that SQL. Reversing BatchPlan.Build's preference is what moved the common
            // read onto the cheap side; the expensive side remains for rowid / DML / deletion-vector shapes,
            // where it is the biggest single span left on a remote scan (see CLAUDE.md, "fuse ProbeSchema with
            // the real query" — the LIMIT 0 is a duplicate bind, since Host.Query exposes .Schema without
            // fetching a row).
            using var bs = batch.Query(" LIMIT 0");
            DropViews(batch.ViewNames);
            return listing.MappedSchema is { } bms
                ? ArrowColumnMappingRename.RenameSchema(bs.Schema, bms, toPhysical: false)
                : bs.Schema;
        }
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
        DeltaReader.NativeScanList listing, IReadOnlyList<DeltaReader.NativeScanFile> loopFiles,
        BatchPlan? batch, IReadOnlyList<string> dataCols, bool wantRowId, string? where,
        int prefetch, DeltaRowIdFilter? rowIdFilter = null, DeltaRowTrackingFilter? trackingFilter = null,
        string? topSuffix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (loopFiles.Count == 0 && batch is null)
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
        // Drains one query's batches into the channel, restoring nested logical names on the way (the top level
        // is already logical — the per-file SELECT aliases it, the batch's `schema` map renames it).
        async Task DrainAsync(IArrowArrayStream stream)
        {
            using var s = stream;
            while (true)
            {
                var b = await s.ReadNextRecordBatchAsync(ct).ConfigureAwait(false);
                if (b is null)
                {
                    break;
                }
                if (listing.MappedSchema is { } ms)
                {
                    b = ArrowColumnMappingRename.RenameBatch(b, ms, toPhysical: false);
                }
                await writer.WriteAsync(b, ct).ConfigureAwait(false);
            }
        }

        var pump = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(prefetch);
            var tasks = new List<Task>(loopFiles.Count + 1);
            try
            {
                if (batch is not null)
                {
                    // The batched files as ONE query, alongside (not before) the loop's — it takes a prefetch
                    // slot like any other unit of work, so a table with both kinds still overlaps them.
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    Log.LogDebug("delta native batch: {Sql}",
                                 topSuffix is null ? batch.Sql : batch.Sql + topSuffix);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await DrainAsync(batch.Query(topSuffix)).ConfigureAwait(false);
                        }
                        finally
                        {
                            DropViews(batch.ViewNames);
                            sem.Release();
                        }
                    }, ct));
                }
                foreach (var f in loopFiles)
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
                            var dvView = NextViewName(DvViewName);
                            var sql = FileSql(dataCols, wantRowId, where, file,
                                              fm, listing.TableSchema,
                                              listing.PartitionColumns, rowIdFilter?.PositionCondition(file.Ordinal),
                                              trackingCond, dvView: dvView);
                            if (topSuffix is not null)
                            {
                                sql += topSuffix;
                            }
                            Log.LogDebug("delta native file: {Sql}", sql);
                            try
                            {
                                await DrainAsync(QueryFile(sql, file, dvView)).ConfigureAwait(false);
                            }
                            finally
                            {
                                if (BindDv(file.Dv))
                                {
                                    DropViews(new[] { dvView });
                                }
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
