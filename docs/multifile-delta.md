# MultiFileReader + engineered-wood — native-parquet Delta path (design; Phase-A slices BUILDING)

> **Phase-A slice 1a DONE (2026-07-03, ABI v57) — the real MultiFileReader integration:**
> `fabricator_delta_mfr_scan(path)` clones `parquet_scan` and swaps in `FabricatorDeltaMultiFileReader` (a DuckDB
> `MultiFileReader`, `src/fabricator/fabricator_delta_mfr.cpp`) whose `CreateFileList` gets the EXACT active files from
> engineered-wood (new ABI `delta_list_files` → JSON `[{"path":…}]` → a `SimpleMultiFileList`); DuckDB's **native
> parquet MultiFileReader** reads them (cached over `onelake://` for OneLake). Matches the C# reader row-for-row
> (`test/verify_delta_mfr_scan.test`, 36). This is the **C++ foundation** the rest builds on — chosen over
> extending the host_query pre-spike because the MultiFileReader framework is where DV, partition elimination, and
> dynamic-filter file pruning live (confirmed in duckdb-delta: `DeltaMultiFileList::DynamicFilterPushdown` +
> `PushdownInternal` prune files by the Delta-log `add` stats — static AND dynamic; partition values from the log
> both prune + inject as constants; `FinalizeBind` attaches a per-file `DeltaDeleteFilter`). The host_query path
> (`fabricator_delta_native_scan`) can't do any of those (it sees only parquet footers, no `_delta_log`) → it stays
> the simple/no-DV fallback. **Next slices (on this foundation):** 1b — a custom `FabricatorDeltaMultiFileList`
> holding per-file metadata + `FinalizeBind` → `DeltaDeleteFilter` from a C#-supplied DV bitmap (DV **correctness**);
> 1c — partition-value constants; 1d — `Complex/DynamicFilterPushdown` → re-call `delta_list_files` with the filter
> (the `push` arg) so engineered-wood prunes files by log stats; 1e — fold into the ATTACH catalog (credential
> available there for the OneLake log read). Below is the full design.
>
> **Slice 1b DONE (2026-07-03) — deletion vectors (correctness):** `delta_list_files` now emits per file the
> DELETED row positions (`"dv":[…]`, resolved by engineered-wood's `DeletionVectorReader`); the C++ side gained a
> custom `FabricatorDeltaMultiFileList` (per-file DV parallel to the file list), an `InitializeGlobalState` override
> (so `FinalizeBind` can reach the list), and `FinalizeBind` attaches an `FabricatorDeltaDeleteFilter` (over the
> sorted deleted positions) to the parquet reader → DuckDB's native read EXCLUDES the deleted rows. Matches the C#
> reader on a `deletion_vectors true` table (`test/verify_delta_mfr_dv.test`, 23). **Two gotchas:** (1) the parquet
> reader hands `Filter()` an uninitialized `SelectionVector` (`Initialize(nullptr)` → null buffer), so the
> DeleteFilter MUST `result_sel.Initialize(STANDARD_VECTOR_SIZE)` before writing (else segfault — matches the delta
> ext); (2) **bare `count(*)` uses the parquet row-group metadata path and does NOT apply the DeleteFilter → it
> OVER-COUNTS on a DV table** (any column/predicate scan is DV-correct; disabling that metadata-count optimization
> for the delta reader is a follow-up). No ABI bump.
>
> **1c (partition) + 1d (filter pushdown) — LARGELY ALREADY WORK via the inherited parquet_scan (verified 2026-07-03):**
> because `fabricator_delta_mfr_scan` clones `parquet_scan`, it inherits **filter pushdown** and **hive partitioning**.
> (1) **Filters push to the row-group level automatically** — `EXPLAIN … WHERE id>7` shows `Filters: id>7` INSIDE
> the scan operator (no separate FILTER), so static AND dynamic (join/TopN) filters prune row-groups in the native
> read with no custom `Complex/DynamicFilterPushdown`. (2) **Partition columns resolve automatically** — engineered-
> wood writes Hive layout (`<col>=<value>/*.parquet`) and the inherited `hive_partitioning` recovers the value from
> the path (verified: a `PARTITIONED BY (region)` table reads `region` correctly through the mfr scan). So 1a+1b +
> the inherited parquet features already give a nearly complete native Delta read (native reader + projection +
> filter/row-group pushdown + hive partitions + parallelism + ExternalFileCache + DV). **What genuinely remains:**
> (1d-file) Delta-**log**-stats FILE-level pruning (skip whole files without opening — a pure optimization over the
> row-group pruning; needs an engineered-wood "prune files by predicate" API, deferred); (1c-edge) log-authoritative
> partition-value injection for edge cases hive inference misses (typed/NULL partitions — robustness follow-up);
 and the bare-`count(*)`-on-DV fix.
>
> **1e slice 1 DONE (2026-07-03) — native read folded into the ATTACH catalog (opt-in, C#-only, NO ABI/C++):**
> the Delta folder-catalog ATTACH option **`native_read true`** makes a plain `SELECT … FROM lake.main.t` source
> its bytes through **DuckDB's own parquet reader** — `DeltaCatalog.ScanTable`'s plain-read branch runs
> `Host.Query("SELECT * FROM read_parquet([<exact active files>])")` (the validated `fabricator_delta_native_scan`
> mechanism: engineered-wood lists the exact `add` set via `GetActiveFileUrisWithDv`; DuckDB's native reader reads
> them with tuned decode + cross-file parallelism + ExternalFileCache, over `onelake://` for OneLake) instead of
> engineered-wood's C# parquet reader. **This deliberately does NOT drive the C++ MultiFileReader from the catalog
> entry** — `GetScanFunction` would have to hand DuckDB a pre-bound parquet scan, and `TableFunctionBindInput`
> needs a live `Binder` + `TableFunctionRef` the catalog entry doesn't cleanly have (fragile, DuckDB-internal-
> coupled). Routing through the existing C# `ScanTable` seam keeps ALL catalog plumbing intact (three-part names,
> stats, DML, time travel) and is a pure byte-source switch. **Opt-in + read-only fallbacks:** a scan needing the
> transient rowid (UPDATE/DELETE), a time-travel scan (`AT`), or a table carrying **deletion vectors** transparently
> falls back to the C# reader (the native path has no DeleteFilter / rowid / snapshot logic) — verified to stay
> correct (`test/verify_delta_catalog_native_read.test`, 53: plain read/projection/filter/aggregate/multi-file
> append, DELETE+UPDATE via the rowid C# reader, and a DV table falling back). **Caveats / follow-ups (native read):**
> the pushed FILTER is NOT translated into the host SQL yet (DuckDB re-applies above the scan; native `read_parquet`
> gets no WHERE → no Delta-log/row-group pruning on this path — the C# reader still does that), so a selective scan
> may read more files; column PROJECTION is likewise left to DuckDB above the scan; the bind-time schema (from the
> COLUMNS metadata = engineered-wood's `GetSchema`) must match `read_parquet`'s output BY NAME (holds for
> engineered-wood- and Spark-written plain tables; decimals align since both now emit `Decimal128`). **The full
> MultiFileReader-in-catalog fold** (below — native DeleteFilter for DV, dynamic join/TopN filter pushdown into
> row-groups, native rowid via `file_row_number`+path-sorted ordinal for DML, native time travel) remains the
> heavier follow-up; slice-1 delivers the headline native-read win (tuned decode + parallelism + caching) with
> near-zero risk. Below is the full design.

> **⚠ SUPERSEDED 2026-08-13 — READ THIS BEFORE THE PARAGRAPH BELOW.** The pre-spike's "first slice: plain
> tables" caveat was not a limitation you could opt into: `fabricator_delta_native_scan` shipped in the
> GLOBAL function registry, so any user could call it, and on a table with a **deletion vector** it returned
> the deleted rows. MEASURED — 10 rows, a DV delete of 3, and it returned all ten (ids 1..10, sum 55) while
> the catalog and `fabricator_delta_scan` both returned 7 / 49, silently. A DV records the deletion in the
> LOG and leaves the parquet untouched, so a file list plus `read_parquet` cannot see it. The function now
> delegates to `DeltaNativeReader` — the reader an ATTACH catalog uses under `native_read`, where all the
> follow-up slices actually landed — so DVs, partition columns, column mapping and schema evolution are all
> applied, and it is a genuine counterpart to `fabricator_delta_scan` rather than a spike. Gate:
> `verify_delta_native_scan.test` 36 → **59**, mutation-tested (restoring the old query survives 44
> assertions and dies at the DV one). ⚠ Neither function pushes the PROJECTION — `BindingBoundTableFunction`
> declares the binding's full `OutputSchema` at bind, so a projected subset would mismatch it.
>
> **Phase-A pre-spike DONE (2026-07-03):** `fabricator_delta_native_scan(path)` — engineered-wood lists the EXACT
> active data files + schema (`DeltaReader.GetActiveFileUris`, the `add` set, NOT a glob), and DuckDB's **native
> parquet reader** reads them via `read_parquet([...])` run on the host engine (`Host.Query`/host_query). Matches
> the C# reader (`fabricator_delta_scan`) row-for-row on the local fixture (`test/verify_delta_native_scan.test`,
> 36 assertions); for OneLake the file URIs are rewritten to `onelake://` so the read is native **and cached**
> (ExternalFileCache, confirmed). C#-only (no ABI — reuses the `onelake://` FS v56 + host_query); `parquet` is now
> statically linked into the test binaries (`extension_config.cmake`). **First slice: plain tables** — no deletion
> vectors, no partition columns, no pushdown (DuckDB projects/filters above the scan); OneLake needs the ambient
> credential for the log read (works from the ATTACH catalog path — a later slice). This validates the whole
> "engineered-wood lists → DuckDB reads natively" architecture cheaply, before the full `MultiFileList` C++ work.
> Remaining below is the full design.

> Status: design note. Source-grounded 2026-07-02 against `D:\repos\duckdb-delta`
> (the official DuckDB Delta extension) and DuckDB v1.5.4's `MultiFileReader` API (submodule
> `duckdb/src/include/duckdb/common/multi_file/`). Captures integrating DuckDB's native, tuned parquet
> reader/writer with a **C# metadata layer** (engineered-wood), so DuckDB does all parquet I/O while
> engineered-wood only reads/writes the `_delta_log`. Builds on the filesystem bridge
> ([filesystem-bridge.md](filesystem-bridge.md)) + host-query ([host-query.md](host-query.md)).

## The core inversion (read this first)

The instinct "use the parquet scanner *from* our C# code" is backwards. **The scanner stays in C++/DuckDB.**
C# is demoted to a **metadata provider**: it supplies a file list + deletion vectors + partition values +
schema, and DuckDB's native parquet reader does the reading. Today's ABI is a **data plane** (C# produces
Arrow rows → C++ ingests). This model turns it into a **metadata plane** — C# never touches parquet bytes.

engineered-wood's **weakest** part (its from-scratch parquet reader/writer — the source of the decimal, DV
byte-format, `path_in_schema`, roaring-bitmap bugs, and the DataPage-V2 / signed-min/max interop caveats)
**falls away**. Its **strongest, most spec-stable** part (reading and writing the `_delta_log`: snapshot
resolution, `add`/`remove`/`cdc` actions, protocol, checkpoints, DV encode) **stays**. This is precisely
DuckDB's own `delta` extension architecture — with a **C# log layer instead of the Rust kernel**.

## How the official delta extension wires it (the reusable trick)

Verified in `D:\repos\duckdb-delta\src`. It does **not** build a `MultiFileFunction<>` from scratch. It
**clones `parquet_scan` and swaps only the multi-file reader** (`delta_scan.cpp:83-121`):

```cpp
// clone parquet_scan from the catalog, then inject:
function.get_multi_file_reader = DeltaMultiFileReader::CreateInstance;
```

That inherits DuckDB's entire parquet scan — all encodings, DataPage V2, page index, bloom, dictionary,
SIMD, prefetch — **plus multi-file parallelism plus dynamic (join/TopN) filter pushdown**, for free. Only
two small classes are custom:

- **`DeltaMultiFileList : SimpleMultiFileList`** (`delta_multi_file_list.{hpp,cpp}`) — supplies the file
  list. Overrides `GetFile`/`GetFileInternal`/`GetAllFiles`/`GetTotalFileCount`/`GetExpandResult` +
  `ComplexFilterPushdown` + `DynamicFilterPushdown` + `GetCardinality`. Per file it stores a
  `DeltaFileMetaData { delta_snapshot_version; file_number; cardinality; ffi::KernelBoolSlice
  selection_vector; partition_map; transform_expression; }`.
- **`DeltaMultiFileReader : MultiFileReader`** (`delta_multi_file_reader.{hpp,cpp}`) — overrides
  `CreateInstance`, `CreateFileList`, `Bind`, `BindOptions`, `InitializeGlobalState`, `InitializeReader`,
  `FinalizeBind`, `FinalizeChunk`, `ParseOption`.

**Deletion vectors are essentially free** — DuckDB's parquet reader has a native `deletion_filter` hook
(`BaseFileReader::deletion_filter`, base type `DeleteFilter` with `idx_t Filter(row_t start, idx_t count,
SelectionVector &sel)`). The delta ext attaches one in `FinalizeBind`:

```cpp
struct DeltaDeleteFilter : DeleteFilter {         // delta_multi_file_reader.cpp:24
  idx_t Filter(row_t start, idx_t count, SelectionVector &sel) override; // keep row iff dv.ptr[row_id]
};
reader.deletion_filter = make_uniq<DeltaDeleteFilter>(file_metadata.selection_vector);
```

The reader drops deleted rows itself. **Partition values** are injected as constants via
`reader_data.constant_map` in `FinalizeBind`. **`file_row_number`** is a built-in parquet virtual column
(`ParquetOptions.file_row_number`, column id `COLUMN_IDENTIFIER_FILE_ROW_NUMBER`) — the physical
within-file row index.

**Write** (`delta_insert.cpp`): `PlanInsert` clones DuckDB's built-in **parquet COPY** into a
`PhysicalCopyToFile` pointed at the table dir, return type `WRITTEN_FILE_STATISTICS`. DuckDB writes the
parquet; the sink harvests `(path, row_count, size, footer_size, column_stats MAP, partition_info)`; the
extension builds `add` actions and the kernel writes the `_delta_log` commit. Blind append only (rejects
RETURNING / ON CONFLICT).

## MultiFileReader API cost (DuckDB v1.5.4)

From `duckdb/src/include/duckdb/common/multi_file/`. A custom `MultiFileList` needs ~5 pure virtuals
(`GetAllFiles`, `GetExpandResult`, `GetTotalFileCount`, `GetFile`; fewer via `SimpleMultiFileList`). We
**reuse `ParquetMultiFileInfo` (the parquet `MultiFileReaderInterface`) verbatim** — no custom
`BaseFileReader` needed unless we want exotic per-file behavior. Total custom C++ ≈ **400–1700 LOC**,
mostly delegation. Dynamic filters arrive via `MultiFileReader::DynamicFilterPushdown(... TableFilterSet
&)`; parallelism is built into `MultiFileGlobalState` (per-file `atomic file_index`, lock only around
metadata, concurrent I/O). Column mapping is `MultiFileColumnMappingMode::{BY_NAME, BY_FIELD_ID}` — the
latter is what column-mapping Delta tables need.

**Stability caveat:** the API is public-ish (`DUCKDB_API`, no `@internal`) but *churns* (`DynamicFilterPushdown`
is new in ~1.5; the delta ext notes const-correctness debt around `GetTotalFileCount`). We are pinned to
1.5.4, but every DuckDB bump touches this glue. This is the single biggest architectural cost — it partly
reverses our "C++ thin / all logic in C#" principle. Mitigation: the glue is **provider-agnostic** (it's
"read a supplied list of parquet files with DV + column mapping") and lives in `fabricator-core`, reused by
every future lakehouse provider.

## Our shape: generic C++ core + a C# `ILakehouseSnapshot` interface

**C++ (new, `fabricator-core`, provider-agnostic):** `FabricatorMultiFileList : SimpleMultiFileList` +
`FabricatorMultiFileReader : MultiFileReader`, whose file list comes from an ABI metadata call (not
Delta-specific — Iceberg/Lance/plain-parquet all just return a file list). Register e.g.
`fabricator_delta_scan` by cloning `parquet_scan` + injecting `get_multi_file_reader`.

**ABI (metadata plane — mirrors the kernel FFI, but as our ABI + Arrow):**

*Read:*
```c
scan_schema(handle, table, version, ArrowSchema *out);              // logical schema
list_scan_files(handle, table, version, pushdown_json,             // one row per active data file:
                ArrowArrayStream *out);
//   columns: path (string) | partition_values (map/json) | record_count (int64)
//          | deletion_vector (blob = resolved bool/roaring) | stats_json (string, optional)
//          | base_row_id (int64, nullable) | commit_version (int64, nullable)   ← row tracking, see below
```
C++ builds the `MultiFileList` from that batch, opens each file via DuckDB's `FileSystem` (its
azure/s3/httpfs secrets — **reads don't even need the host-FS callbacks**), wraps the DV blob as a
`DeleteFilter`, injects partition values as constants.

*Write:*
```c
// DuckDB writes the parquet natively (PhysicalCopyToFile); we harvest WRITTEN_FILE_STATISTICS:
commit_add_files(handle, table, ArrowArray *written_files, mode);   // (path,partitionValues,size,modTime,stats)
//   -> engineered-wood writes the _delta_log commit (its strongest capability)
```

**C# (`ILakehouseSnapshot` — the reusable "file-list provider", NOT a reader reimplementation):**
```csharp
interface ILakehouseSnapshot {
    Schema LogicalSchema { get; }
    IReadOnlyList<DataFileEntry> ListFiles(FilterNode? pushdown);
    long CommitAddFiles(IReadOnlyList<WrittenFile> files, WriteMode mode);
}
record DataFileEntry(string Path, IReadOnlyDictionary<string,string?> Partitions, long? RecordCount,
                     byte[]? DeletionVector, string? StatsJson, long? BaseRowId, long? CommitVersion);
```
engineered-wood implements it over its log layer (snapshot resolution, `add` enumeration, DV decode — its
RoaringBitmap reader is now fixed). Every future parquet-backed provider reuses the same C++ core.

## Row tracking, commit version, CDF in this model — YES, and cleaner

The key realization: **row tracking / commit versions / CDF actions are all LOG-side metadata.** They are
written by whoever writes the `_delta_log` (engineered-wood, in both the current and the native model). So
the native-parquet-write model is **orthogonal** to them — it changes who writes the *parquet*, not who
writes the *log actions*. And in several ways the native model makes them **easier**.

### Real row tracking (`baseRowId`) — achievable, and better here

Delta's default (non-materialized) row-tracking scheme does **not** store a `row_id` column in the parquet.
Instead each `add` carries **`baseRowId`**, and the stable id of row `i` in that file is
`baseRowId + i` (i = physical position = DuckDB's `file_row_number`). The next assignable id is tracked in
a **`domainMetadata` action for domain `delta.rowTracking`** (`rowIdHighWaterMark`).

In the native model this is squarely a C# log-layer job and is *helped* by the write path:
- DuckDB writes plain parquet (no id column needed). `WRITTEN_FILE_STATISTICS` gives us the **exact
  per-file `row_count`** — precisely what advances the high-water mark.
- On commit, our log layer assigns `add.baseRowId = highWaterMark + 1`, advances the mark by the file's
  `row_count`, and writes the `delta.rowTracking` `domainMetadata`.
- **Reading stable ids becomes trivial and Spark-compatible**: `row_id = baseRowId[file] + file_row_number`.
  `baseRowId` is a constant per file (from the log, carried in `list_scan_files`), `file_row_number` is a
  native parquet virtual column → compute the virtual `_metadata.row_id` column in `FinalizeChunk` (a
  per-file constant offset + the virtual column). **This is exactly the read-side stable id that the
  delta-rs binding could not expose** (see [delta-rs-provider.md](delta-rs-provider.md) "The DML crux") — the
  MultiFileReader model gives it to us for free.
- **Gap to close in engineered-wood:** its `known-issues.md` notes it currently emits `baseRowId` on the
  `add` but **not** the `delta.rowTracking` `domainMetadata` high-water mark (spec-required for a compliant
  reader to know the next id). Adding that domainMetadata action is small and is the only real work for
  *real* (spec-complete) row tracking.

**Consequence for DML:** in this model the transient `(file, position)` rowid and the stable row id become
inter-convertible — `(file, position) = (file_of, file_row_number)` and `stable_id = baseRowId + position`.
Physical copy-on-write still keys on `(file, position)` (what a rewrite needs), but we can *also* surface
the stable id as a user-visible column. Best of both; no reason to key DML on the stable id (same conclusion
as [delta-catalog.md](delta-catalog.md)).

### DELETE / UPDATE: copy-on-write vs deletion vectors (`deletion_vectors true`)

The `(file, position)` rowid feeds BOTH deletion strategies identically; the strategy is chosen by the
table's `delta.enableDeletionVectors` (our `deletion_vectors true` ATTACH option). Three cases:

- **DELETE, `deletion_vectors true` → DV write, NO data rewrite (cleanest case).** Mark the matched
  positions in a per-file roaring bitmap, commit `remove`(old path+old DV) + `add`(same path, new DV). This
  is **pure log + DV-file write = 100% engineered-wood's existing, Fabric-validated capability** — DuckDB's
  parquet writer is **not involved at all**. Repeat deletes compose via absolute positions (already solved).
  **Notably this rides on Phase A (read) alone** — it needs the scan's `(file, position)` rowid but no native
  *write* — so DV-DELETE could ship before/without Phase B. This is the case I under-specified as generic
  "copy-on-write" in an earlier draft.
- **DELETE, `deletion_vectors false` → copy-on-write.** DuckDB reads survivors (native) → DuckDB writes a new
  parquet file (native, Phase B) → commit `remove`+`add`. Whole affected file rewritten.
- **UPDATE — subtler.** A DV only *removes* rows, so an UPDATE can't be a pure DV op. Two shapes:
  - *Copy-on-write* (what engineered-wood does today, even on DV tables): rewrite each affected file with the
    new values. Native writer writes the whole file. Simple, correct, more I/O.
  - *Merge-on-read* (Delta/Spark's DV UPDATE — **not built today, a future option**): DV-mask only the matched
    old rows + append a **small new file** holding just the updated versions of those rows (unmatched rows
    stay live in place). Native writer writes only the small postimage file — far less I/O when few rows/file
    change. New work: split matched/unmatched, write the postimage file, DV the old positions, commit.

So `deletion_vectors true` genuinely turns DELETE into a rewrite-free DV write here (a real win the earlier
draft missed), and *could* do the same for UPDATE via merge-on-read (deferred). Engineered-wood's DV byte
format is fixed + Fabric-read-validated (see [delta-catalog.md](delta-catalog.md)), so the DV path is
trustworthy. **CDF interaction:** a DV-DELETE still emits `_change_data` for the deleted rows when CDF is on
— engineered-wood already does this on its DV-delete path.

### Commit version / `defaultRowCommitVersion` — trivial

The commit version is just the `_delta_log/NNNNN.json` number our log writer assigns. Row tracking's
per-row **`defaultRowCommitVersion`** = that same number, written on each `add`. The readable
`_metadata.row_commit_version` column = a **constant per file** (from the log) → injected in `FinalizeChunk`
exactly like `baseRowId`. Fully under our control; if anything easier in the native model because we own the
commit and the exact per-file row counts.

### CDF (Change Data Feed) — INSERT free, DELETE/UPDATE need change-data files

- **INSERT / blind append:** no change-data files needed — CDF *infers* inserts from the `add` actions. Free.
- **DELETE / UPDATE:** the spec needs `_change_data/*.parquet` holding the changed rows with
  a `_change_type` column (`insert`/`delete`/`update_preimage`/`update_postimage`) + a `cdc` action in the
  log. In the native model this means driving a **second** `PhysicalCopyToFile`: DuckDB reads the affected
  rows, we tag `_change_type` (preimage from the old file, postimage from the new values), DuckDB writes them
  to `_change_data/`. The log-side `cdc` action + `commitInfo` is engineered-wood's job — **it already does
  CDF** (validated live on Fabric). So the only new piece is orchestrating the extra tagged write; the
  hardest part of CDF (the log protocol) already exists.

**Bottom line on the three:** all log-side, all under our control, none blocked by the native model. Row
tracking is actually *unlocked* on the read side by `file_row_number` + `baseRowId`. DV-DELETE is rewrite-free
(pure engineered-wood, rides on Phase A). The only genuine new work is (a) engineered-wood emitting the
`delta.rowTracking` domainMetadata high-water mark, (b) the CDF DELETE/UPDATE change-data write orchestration,
and (optionally) (c) merge-on-read UPDATE with DVs.

## Benefits vs costs

**Benefits:** native tuned parquet **read** (all encodings/V2/page-index/bloom/SIMD → engineered-wood's
reader gaps become moot) + **multi-file parallelism + dynamic filter pushdown** (we have neither on the C#
stream path today) + native parquet **write** (kills the DataPage-V2 / signed-min/max interop caveats) +
Spark-compatible readable row ids for free + reusable across providers.

**Costs / risks:** (1) new C++ coupling to DuckDB's churning `MultiFileReader` internals — the biggest price,
partly reverses "thin C++"; mitigated by keeping it provider-agnostic in `fabricator-core`. (2) Two read paths
(native for parquet-backed lakehouse; Arrow bridge for SQL/DAX) — acceptable, different provider kinds. (3)
No benefit for SQL Server / DAX — a Delta/lakehouse-only investment. (4) DV correctness: engineered-wood must
decode DVs to a bool/position set matching `file_row_number` semantics (its roaring reader is fixed).

## Native-read fold — design decisions captured (planning, 2026-07-03)

Three decisions/findings from planning the direct MultiFileReader-in-catalog fold (the real 1e, superseding the
`native_read` Host.Query slice `9f5ec40`).

### Rowid is a VIRTUAL COLUMN, not a bind-time decision (corrects an earlier worry)

The concern "`BuildScanFunction` can't know at bind time whether the rowid is requested → native path must be
read-only" was **wrong**. duckdb-delta sets `function.get_virtual_columns = DeltaVirtualColumns`
(`delta_scan.cpp:104`), which declares `file_row_number` (`COLUMN_IDENTIFIER_FILE_ROW_NUMBER`), `rowid`
(`COLUMN_IDENTIFIER_ROW_ID`) and `delta_file_number` as **virtual columns on the TableFunction**
(`delta_scan.cpp:57-67`). The rowid is therefore a **projected virtual column requested at scan-init via
`column_ids`** and produced on demand — never a bind decision. `file_row_number` is a native parquet virtual
column (physical within-file position); the file ordinal is `reader_data.reader->file_list_idx.GetIndex()`
(duckdb-delta stashes it via `constant_map`, `delta_multi_file_reader.cpp:77`). So our transient rowid
`(fileOrdinal << 40) | file_row_number` is exactly `file_list_idx << 40 | file_row_number`, produced in
`FinalizeChunk`. **Native DML is thus achievable directly** (no read-only compromise for the rowid reason). The
three real constraints that remain: (1) the file list handed to the MFR must be **relative-path-sorted** so
`file_list_idx` == engineered-wood's `OrderedActiveFiles` ordinal (`string.CompareOrdinal` on the relative
`add.path`) — the one critical parity point for `DeleteByRowIdsAsync` decode; (2) DV: `file_row_number` is the
absolute (pre-DeleteFilter) physical position, matching engineered-wood's absolute-position DV decode; (3)
schema evolution — missing columns are NULL-backfilled in the MFR via `constant_map`/`default_expression`
(duckdb-delta `delta_multi_file_reader.cpp:96-101,157-159`), so evolved/time-travel tables need that wiring,
else fall back to the C# reader.

### Snapshot consistency across multiple Delta tables (a join) — capture a per-transaction instant

Each table scan resolves its snapshot **independently** at `CreateFileList`/`ScanTable` time (true for BOTH the
C# reader today and the native path — a pre-existing property, not new). A join over A and B where a writer
commits to B between the two ABI calls reads B newer than A; a re-scan of one table can also see a different
version. Fix (matches Delta snapshot-isolation semantics): **at transaction start, capture one UTC instant** in
our per-DuckDB-transaction state (`AmbientTransaction`/`TxnState`, keyed on `global_transaction_id`, ABI v35),
and pass it as an **implicit `AT (TIMESTAMP)`** into `catalog_list_scan_files(handle, schema, table, at)` for
every scan lacking an explicit `AT`. engineered-wood resolves instant→version via the now-always-written
`commitInfo.timestamp` (no `in_commit_timestamps` opt-in needed). **Explicit `AT` overrides** per table. Also
**cache the resolved version per `(txn, catalog, schema, table)`** so re-scans are stable and timestamp→version
resolves once. Autocommit ⇒ one instant per statement (a single join reads a consistent cut); explicit `BEGIN`
⇒ one instant for the whole transaction (repeatable read). Caveat: timestamp→version is inherently fuzzy
(writer/reader clock skew) → pinning the resolved version (not re-deriving from the timestamp each scan) is the
robust form. Parallel ABI calls on one `DeltaCatalog` are fine — `DeltaTable.OpenAsync` per call is independent
and the `[ThreadStatic]` opener/credential are set per call.

### Dynamic filters + parallelism (inherited from parquet_scan — nothing to build, one tuning knob)

Verified against `duckdb/src/include/duckdb/common/multi_file/multi_file_function.hpp`: `global_state.filters =
input.filters.get()` (`:522`) is a **live pointer** to the scan's `TableFilterSet` incl. the hash-join-populated
dynamic filters. At **each file open**, `TryOpenNextFile` → `InitializeReader(..., global_state.filters, ...)`
(`:307-309`) consults the **current** filters for row-group pruning + whole-file skip (`SKIP_READING_FILE`).
Plus a one-time file-list prune at init (`MultiFileFilterPushdown`→`DynamicFilterPushdown`, `:491`; dynamic
filters usually not ready yet there). Concurrency = `TaskScheduler::NumberOfThreads()` (`:525`, look-ahead
`:273`), optionally capped by the interface `MaxThreads` (`:553`) — so ≈ #threads files in flight. **Key
consequence:** dynamic filters apply only to **not-yet-opened** files (an in-flight file scan is never
re-pruned), so a **very high thread count opens many files before the join's dynamic filter materializes →
they escape pruning**. Moderate parallelism lets the build side produce the filter early so later file opens
are pruned — a real parallelism-vs-late-filter trade-off (self-bounded by the #threads look-ahead). The native
fold **inherits all of this for free** from the cloned `parquet_scan` + our `FabricatorDeltaMultiFileList` (static
+ dynamic, row-group + file-skip) — no extra work. The only lever is deckeling a Delta scan's parallelism via
the interface `MaxThreads` if we ever want to prioritize dynamic-filter effectiveness over raw parallelism
(feintuning, not required). Later 1d-file can also push the **static** filter into `catalog_list_scan_files` so
engineered-wood drops whole files by Delta-log stats before DuckDB opens them (log-level file pruning on top of
row-group pruning).

### Cloud writes (S3 / MinIO) — works with S3 secrets, no opener conflict; three caveats

The opener model is uniform, so **writing to MinIO/S3 works when an S3 secret is present, with no S3-specific
opener conflict**: an `s3://` root has `_fabricCredential == null` → `TableFileSystems.Create` picks
`DuckDbTableFileSystem` (host `fs_*` callbacks) → DuckDB's `OpenerFileSystem(context)` auto-pushes the opener's
`FileOpener` → resolves the best-scoped S3 secret. The `[ThreadStatic]` opener is re-established on the
background bulk consumer thread by `BulkSession` (same as local/OneLake); `AmbientOneLakeCredential` stays null
so the OneLake SDK path is never taken. Caveats (NOT opener conflicts): (1) **httpfs must be present** — the
test binaries statically link only `json`/`icu`/`parquet`, so `s3://` needs `duckdb_extension_load(httpfs)` for
tests (the real loadable autoloads it); (2) **commit atomicity** — the Delta commit's put-if-absent guard rides
`RenameAsync`→`TryOpenWriteExclusive` (`EXCLUSIVE_CREATE`); DuckDB's httpfs S3 write likely does not emit a
conditional PUT (`If-None-Match`), so single-writer works but concurrent writers could clobber a version
(lost commit; OCC-retry can't fire if the conflict isn't surfaced) — MinIO supports conditional PUT only if
httpfs sends it, to verify; (3) **DROP/RENAME dir-ops** — `fs_remove_dir`→`RemoveDirectory` /
`fs_move_dir`→`MoveFile` may be unimplemented on httpfs' S3 FS (as on Azure DFS, where we route to the SDK), so
DROP/RENAME could fail on S3 while CREATE/INSERT/CTAS/COPY/SELECT/DELETE/UPDATE (file writes, no dir-ops) work.

### Decision (2026-07-03): the C# native reader is the target path; C++ MFR only for CPU-bound-local

After grounding the analysis, the C++ MultiFileReader's advantages over a **pure-C# native reader** (C# lists +
orchestrates, DuckDB's `read_parquet` does the native decode via `Host.Query`) reduce to almost nothing for the
**cloud lakehouse target** (OneLake/MinIO, I/O-dominated):

- **Native decode + ExternalFileCache** — C# gets it (via `read_parquet`).
- **rowid/DML** — C# computes it in SQL: `(ordinal::BIGINT << 40) | file_row_number` (relative-path-sorted
  ordinal), matching engineered-wood's `DeleteByRowIdsAsync` decode. No `get_virtual_columns` needed (the
  catalog entry already declares the rowid virtual column; the scan just produces `_metadata.row_id`).
- **DV** — C# drops deleted positions per file.
- **Projection + static filter** — pushed into the `read_parquet` SQL.
- **Static Delta-log FILE pruning + early-stop (LIMIT)** — per-file decision point in the C# loop.
- **Dynamic (join/TopN) filters** — NOT MFR-exclusive: they flow to any table function with `filter_pushdown =
  true` via `TableFunctionInitInput::filters` (PhysicalTableScan merges `op.dynamic_filters` at source-state
  init, `physical_table_scan.cpp:35-36`). C# can read them at init and translate to `read_parquet … WHERE`
  (caveat: `filter_pushdown = true` removes filters from the plan → must translate the full set faithfully;
  today we use `filter_pushdown = false` + `pushdown_complex_filter` for superset-safe partial pushdown).
- **Mid-scan-materializing filters** — even the MFR only prunes the *not-yet-opened* tail (already-open files
  aren't re-pruned; `InitializeReader` fixes a file's row-group skip at open), and that tail shrinks with high
  parallelism. A C# per-file loop + a **live-filter host-callback** (re-read the outer scan's filters before
  each file) matches the MFR's tail-pruning exactly. So this is *closable* in C#, not a categorical MFR edge.

The **only structural, non-replicable** MFR advantage is **downstream multi-lane parallelism** (the MFR feeds
#threads pipeline lanes; our bridge exports one Arrow stream = one `get_next` lane). That matters for
**CPU-bound local** star-schema joins over huge local parquet, and is **secondary for cloud I/O** (where
concurrent file *fetch* — which C# does via prefetch/bounded-channel — is the bottleneck). And it is **additive
later**: partition the file list into N groups, one Arrow stream + per-thread local state per group,
`MaxThreads = N` — this does NOT touch the rowid/DV/filter design (the ordinal is global-path-sorted regardless
of which thread reads a file). So we build the single-stream C# reader now and layer multi-lane on later.

**⇒ Target = the pure-C# native reader.** Build the C++ MFR (`fabricator_delta_mfr_scan`, slices 1a/1b, already
done as a standalone function) only if CPU-bound-local multi-lane Delta scans become a real goal.

### Concrete plan — C# native catalog reader (supersedes the `native_read` Host.Query slice `9f5ec40`)

1. **Slice 1 (C#-only, no ABI) — DONE (2026-07-03).** `DeltaCatalog.ScanTable`'s `native_read` branch grew from
   one `read_parquet([list])` into a **per-file loop** (`DeltaNativeReader`, prefetch/bounded-channel via
   `FABRICATOR_DELTA_PREFETCH`, default 1 = sequential, >1 = concurrent file fetch) that per file emits
   `SELECT <projected>[, ((ordinal::BIGINT << 40) | file_row_number) AS "_metadata.row_id"] FROM
   read_parquet(<file>, file_row_number => true) [WHERE <static> [AND file_row_number NOT IN (dv)]]` via
   `Host.Query`, yields its batches, excludes the file's **DV** (`NOT IN`), and does **Delta-log FILE pruning**
   (`DeltaFilePruner.ShouldInclude`, skip a file whose `add` stats/partitions can't match the static filter) —
   the per-file decision point. rowid via `file_row_number` ⇒ **DML runs natively** (DELETE/UPDATE, no
   C#-reader fallback). File list is **relative-path-sorted** so `file_list ordinal` == engineered-wood's
   `OrderedActiveFiles` (rowid decode parity). Time-travel `AT (VERSION/TIMESTAMP)` → list files as of the
   resolved snapshot. The output schema is probed (`read_parquet … LIMIT 0`) so it matches the batches by type.
   Verified: `test/verify_delta_catalog_native_read.test` (66 — read/projection/filter/aggregate, multi-file
   append, DELETE+UPDATE via native rowid, DV exclusion, AT VERSION 0/1/2, explicit-transaction pinning); Delta
   catalog write/delete/update/decimal/time_travel/changes suites unregressed. **Logging (ILogger, C#-only):**
   `FabricatorLog` (off by default; `FABRICATOR_LOG_LEVEL`+`FABRICATOR_LOG_FILE` → a file sink; factory pluggable) traces
   the resolved snapshot version, the file list (active/scanned/pruned), and each per-file `read_parquet` SQL.
   SUPERSEDED 2026-07-26: the ordinal + the prune verdict now both come from engineered-wood's
   `DeltaTable.PlanFiles`, so the "relative-path-sorted for rowid decode parity" requirement above is met BY
   CONSTRUCTION rather than by our re-implementing the ordering. (The `DeltaFilePruner`-made-`public` change
   this bullet used to describe is retired; the class is `internal` again.) **Deferred within slice 1:**
   very large DVs use a big `NOT IN` list (fine for typical DV cardinality; a bitmap/anti-join is a later opt).
2. **Slice 2 (ABI, core-touching):** a **live-filter host-callback** so the per-file loop reads the outer
   scan's current (incl. dynamic/join) filters before each file → `read_parquet … WHERE` → dynamic file/
   row-group pruning at the per-file decision point (closes the mid-scan-filter gap).

   **Batch 2 — exact remaining ABI work (for a fresh, well-budgeted session; NOT started — an unfinished ABI
   bump breaks the extension):**
   - **(2a) DuckDB log forwarding — WIRED (ABI v58), lockstep-verified, and `duckdb_logs` surfacing CONFIRMED
     LIVE.** The additive `host_log(level, log_type, message)` entry is appended to `FabricatorHostServices`; C++
     `HostLogService` (in `fabricator_host_query.cpp`, reusing the `g_host_db` `DatabaseInstance*`) maps the level
     and calls `Logger::Get(*g_host_db).WriteLog(...)`; `HostFs.Log` + `Bootstrap` wire
     `FabricatorLog.EnableHostForwarding` when the host provides the callback (level codes stable in
     `FabricatorLog.LevelCode`, 0 Trace…5 Critical). **Confirmed:** with `CALL enable_logging(storage='memory')`,
     `SELECT * FROM duckdb_logs WHERE type LIKE 'Fabricator%'` returns the forwarded events with the ILogger
     **category as the `type`** (`Fabricator.Delta`, `Fabricator.Delta.Native`) and the mapped `log_level` — a live
     native-read query surfaced all 6 events (snapshot pin, file list, per-file `read_parquet` SQL, etc.).
     **Two prior red herrings** (why an earlier smoke showed 0 rows): (i) the enable form is `CALL
     enable_logging(...)`, **not** `PRAGMA enable_logging` (which errors → config stays at default); (ii) the
     DuckDB **shell** defaults log storage to a console-printing sink (so events print to stdout as `DEBUG:`/
     `INFO:` but never reach the in-memory table `duckdb_logs` scans) — pass `storage='memory'` (the default in
     the `unittest`/API host anyway). `WriteLog` on the global logger writes regardless of `ShouldLog`, and the
     client flushes the log buffer at each query boundary, so no instance-vs-connection-logger issue exists. **No
     code change was needed** — the forwarding was correct; only the confirmation recipe. The file sink
     (`FABRICATOR_LOG_LEVEL`+`FABRICATOR_LOG_FILE`, Batch 1) remains the always-available independent trace.
   - **(2b-slice-1) host-rendered 1:1 native filter SQL — DONE (C++/C#, NO ABI).** The bind-time
     `FilterSerializer` (`fabricator_table_entry.cpp`) now renders, alongside its superset-safe `filter_json`
     tree, an **equivalent DuckDB SQL predicate** with literals inlined via `Value::ToSQLString()` — same nodes,
     always produced together so they can't diverge (comparison/IN/IS [NOT] NULL/AND/OR/BETWEEN; column via a
     `"…"`-quoted identifier; `is_[not_]distinct` → `IS [NOT] DISTINCT FROM`). It rides `bind_data.native_filter_sql`
     → `BuildScanSpec` emits `spec_json.native_filter` → `ScanSpec.NativeFilter` → **`DeltaNativeReader` prefers it**
     over `DeltaSqlFilter.ToWhere` for the `read_parquet … WHERE`. Because the native target IS DuckDB, the render is
     exactly 1:1 (no dialect/collation risk) and **correctness-neutral** (DuckDB re-applies above the scan; only the
     policy-approved superset-safe subset is pushed — a dropped branch, e.g. VARCHAR `<>` on a non-byte-ordered
     source, just forfeits pruning). **Additive + SQL/DAX-neutral**: those providers read `Filter`+values and ignore
     `native_filter`; the `filter_json`/`filter_constants` generation is byte-identical. Verified:
     `verify_delta_catalog_native_read` (66) + delete (28) / update (63) / partition (54) green; live smoke shows
     `read_parquet(…) WHERE "id" > 3` reaching the engine. This is the mechanism **slice 2 reuses**: it renders the
     execute-time `TableFilterSet` into the same `native_filter` field via `TableFilter::ToString`.
   - **(2b-slice-2) live dynamic/join filters — DONE (C++/C#, NO ABI).** Delivered without a per-file host
     callback: a hash join builds its side **before** the probe scan inits, so the dynamic bound is already
     materialized when `ArrowStreamInitGlobal` runs → rendering `input.filters` **once at scan-init** captures it.
     Pieces: (i) a **C#-declared catalog capability** — `DeltaCatalog` returns `exact_filter_pushdown=true` (only
     under `native_read`) on the `ServerInfo` metadata; C++ `FetchExactFilterPushdown` caches it on
     `FabricatorCatalog`, and `BuildScanFunction` sets `function.filter_pushdown = ExactFilterPushdown()`. This gates
     the flip to the **native_read Delta catalog only** — SQL Server / DAX / non-native Delta keep
     `filter_pushdown=false` (their `ServerInfo` lacks the property → false → unchanged; verified: SQL Server
     filter/projection/limit/orderby/catalog_filter/table_functions suites all green). Safe because `native_read`
     routes **every** scan through `read_parquet` (exact 1:1), so DuckDB erasing the pushed static filters is fine;
     (ii) `arrow_ingest::RenderLiveFilters` walks the live `TableFilterSet` at init (keys → provider names exactly
     as `PhysicalTableScan::GetFilterInfo`: `column_ids[key]` → `names[col]`) and `RenderTableFilter` emits DuckDB
     SQL — **unwrap `OptionalFilter` → child; resolve `DynamicFilter` under `filter_data->lock` (render the inner
     `ConstantFilter` if `initialized`, else skip — `DynamicFilter::ToString` is a debug string, not the bound);
     skip bare `BLOOM` (always Optional-wrapped → skip-safe); recurse `CONJUNCTION_AND/OR` per child (NOT
     `ToString`, which would leak the `optional:` prefix from Optional children); everything else →
     `TableFilter::ToString` (exact SQL on the DuckDB target)** — combined with the slice-1 bind-time
     `native_filter` into `spec_json.native_filter`. **`OptionalFilter`/`DynamicFilter`/`BLOOM` are pruning-only**
     (`FilterSelection` is a no-op → the join re-applies), so skipping any we can't render is always correct; the
     **mandatory** erased static filters render 1:1 → correct. **Bonus:** string `<>`/ordering now pushes exactly
     on the native path (the live render is collation-free on the DuckDB target, unlike the superset-safe bind-time
     subset). Verified: `verify_delta_catalog_dynamic_filter` (21 — static exact WHERE incl. string `<>`, IN+range,
     hash-join dynamic filter into `read_parquet(...) WHERE ("id" IN (…) AND "id">=… AND "id"<=…)`, no `optional:`
     leak) + native_read (66) / delete (28) / update (63) / partition (54) / dv (48) / time_travel (48) green.
     **Remaining nuance:** a dynamic filter refined *mid-scan* (e.g. TopN) is captured only as of scan-init
     (best-effort — a later per-file re-render would need a host callback; not needed for hash joins).
3. **Slice 3 (later, additive):** downstream **multi-lane parallelism** — partition the file list across N
   per-thread Arrow streams (`MaxThreads = N`); no change to rowid/DV/filter.
4. Snapshot consistency (the per-transaction UTC instant → implicit `AT`) applies to this reader exactly as to
   the catalog generally (see above).

### Rowid fast path + late materialization (DONE 2026-07-13)

The native reader now serves **rowid-filtered scans in O(matched files)**, and the scan opts into DuckDB's
**late-materialization** rewrite so `ORDER BY x LIMIT n` fetches only the TopN rows' files:

- **Enablement (C++):** `function.late_materialization = true` + the function-level `get_row_id_columns`
  hook (BuildScanFunction; gated on `ExactFilterPushdown()` + the virtual BIGINT rowid ⇒ Delta
  `native_read` only — SQL Server/DAX keep the flag off: their TopN pushdown is superior and a join-back
  would re-scan the server). The rewrite clones the fetch-side get's bind data
  (`LateMaterializationHelper::CreateLHSGet` → `bind_data->Copy()`), so `ArrowStreamBindData::Copy` was
  implemented — `schema_root`/`arrow_table` are bind-time-only and stay empty in the copy.
- **Plan shape:** TopN over a narrow scan (order key + rowid) builds the SEMI-join side; the fetch scan
  probes. `JoinFilterPushdownOptimizer` supports SEMI joins, so the build side's **dynamic rowid filter**
  (min/max; an IN-list for small builds) lands in the probe scan's `input.filters`.
- **Serialization (C++):** `SerializeLiveFilters`/`RenderLiveFilters` previously skipped
  `COLUMN_IDENTIFIER_ROW_ID` — under exact mode that was a latent correctness bug for erased static
  `WHERE rowid = X` filters. Both now resolve it to the virtual rowid name (`_metadata.row_id`); the
  rendered SQL binds because the per-file SELECT aliases the rowid expression (DuckDB permits SELECT-alias
  references in WHERE).
- **Decode (C#, `DeltaRowIdFilter`):** the rowid is a **locator** — `ordinal = rowid >> 40` selects the
  files exactly (no stats), `position = rowid & (2^40−1)` becomes a per-file `file_row_number` predicate,
  which the parquet reader **row-group-prunes**: `ParquetColumnSchema::Stats` synthesizes exact
  per-row-group min/max for FILE_ROW_NUMBER (min = cumulative offset, max = offset+rows−1, exact). Rowid
  conjuncts are stripped from the engineered-wood prune tree (it has no rowid stats; dropping an
  AND-conjunct only widens). This is also why materialized `__delta_row_id` stats are NOT needed for fast
  row access — stable ids are identity (survive rewrites), the transient rowid is the locator.
- **Not DML:** UPDATE/DELETE/MERGE plans scan once with the WHERE pushed and rowids flow *up* to the
  modify operator — no rowid-filtered re-scan exists there. The fast path serves the late-materialization
  join-back and user `WHERE rowid …` queries.
- Test: `test/verify_delta_late_materialization.test` (57 — layout-independent count assertions since file
  ordinals are path-sorted random uuids, plus duckdb_logs pins of the prune + the `file_row_number` form).

#### What else `late_materialization = true` enables (full trigger inventory)

Beyond the TopN (`ORDER BY x LIMIT n`) rewrite above, the flag opts the scan into three more optimizer
rewrites (source: `duckdb/src/optimizer/late_materialization.cpp` +
`topn_window_elimination.cpp`, v1.5.4):

1. **Plain `LIMIT` — two shapes only.** The limit must be a **constant**. Small limits
   (≤ `late_materialization_max_rows`, setting default **50**) rewrite **only when there is an OFFSET**
   (a small bare LIMIT stops the scan early anyway; `LIMIT 10 OFFSET 100000` skips the offset on the
   narrow scan and fetches back only 10 full-width rows). **Large limits**
   (50 < n ≤ a hardcoded 1,000,000, `OptimizeLargeLimit`) rewrite only when the rowids will be
   *consecutive*: nothing but projections between the LIMIT and the scan, no table filters, and
   `preserve_insertion_order = true` (else the limit runs in parallel and the join-back can pessimize).
2. **`SAMPLE`** — a row-count sample (`USING SAMPLE 100`, not a percentage) up to the same
   50-row-default threshold: sample the narrow scan, fetch the sampled rows back by rowid.
3. **Top-N window elimination** — the rewrite turning `QUALIFY row_number() OVER (PARTITION BY … ORDER
   BY …) <= k` / top-1-per-group patterns into an aggregate consults its own
   `CanUseLateMaterialization`: with the flag, it aggregates only `(partition keys, order key, rowid)`
   and joins back for the payload columns; without it, it must struct-pack every referenced column
   through `arg_max`. It even tolerates a join between the window and the scan when all projected
   columns trace to one table. Big win for wide-table top-n-per-group on the native reader.

**Shared bail-outs** (all shapes): only projections/filters between the operator and the scan, no
volatile expressions, and skipped when the query references (nearly) all scanned columns anyway — no
width saving, no rewrite.

**What every rewrite produces / effects on this extension:**
- The plan gains a **second scan of the same table** + a **SEMI join on rowid** — the reason
  `ArrowStreamBindData::Copy()` exists (`CreateLHSGet` clones the fetch-side get's bind data). Each
  scan is a separate provider execution; the per-transaction snapshot pin keeps them on one version.
- The fetch-back side gets the **dynamic rowid filter** from JoinFilterPushdown (min/max + IN-list for
  small builds) — exactly what `DeltaRowIdFilter` decodes into exact file selection +
  `file_row_number` row-group pruning. Without that decode, every rewrite would trade one narrow scan
  for a full-width full rescan (a pessimization); with it, the fetch touches only matched files/row
  groups.
- For TopN the optimizer **re-sorts the fetched rows after the join** (the join loses order) — a small
  trailing sort appears in the plan.
- Scoped to Delta `native_read` only (the `ExactFilterPushdown()` gate): SQL Server/DAX never see these
  plan shapes — on a remote SQL source the join-back would be a second server round trip, while their
  server-side TopN pushdown is strictly better; on Delta it's local parquet I/O we prune precisely.

### Stable row-tracking virtual columns (DONE 2026-07-13)

`__delta_row_id` / `__delta_row_commit_version` (the Delta materialized-column names — Spark's
`_metadata.row_id`/`_metadata.row_commit_version` equivalents) are queryable **virtual columns** on
row-tracking tables under `native_read`: excluded from `SELECT *`, bound by bare name, per row
`COALESCE(materialized column, baseRowId + file_row_number)` and `COALESCE(materialized version,
defaultRowCommitVersion)` — the durable identity, vs the transient `rowid` locator.

- **Generic mechanism (any provider):** metadata kind `FABRICATOR_META_VIRTUAL_COLUMNS = 12` returns
  (name, type-text) rows per table (best-effort; other providers return empty). The entry registers them
  at `fabricator::ProviderVirtualBase()` (`VIRTUAL_COLUMN_START + 0x100`) in `GetVirtualColumns()` —
  DuckDB's `TableBinding` maps virtual names for bare-name binding, real same-named columns shadow. The
  scan fetches them by name (`BuildScanSpec`), maps output 1:1 by name (`BuildProjectionMapping`), and
  the live-filter serializers resolve virtual-id filters so an exact-mode erased
  `WHERE __delta_row_id = k` is applied exactly.
- **Delta gating:** advertised only when the catalog is `native_read` AND the table has
  `delta.enableRowTracking` (flag cached per path by the Columns fetch — no extra `_delta_log` read at
  entry materialization). Since `deletion_vectors` (default true) enables rowTracking, most
  default-catalog tables qualify.
- **Semantics:** ids follow commit order (deterministic); UPDATE preserves the id and bumps the version —
  buffered (post-images bake materialized ids) AND autocommit (merge-on-read since the mapping + partition
  gate lifts; the source file's own materialized ids are honored, so it holds post-OPTIMIZE too; a SET of
  the PARTITION column moves the row to its new partition with identity intact); DV DELETE removes the id;
  OPTIMIZE preserves both (compaction materializes); a transaction's pending rows read NULL (baseRowId
  assigned at commit). CDF tables preserve too — partitioned included (merge-on-read emits per-partition
  update_preimage/postimage cdc files; the commit is read cdc-only so nothing double-counts). COPY-ON-WRITE
  DELETE/UPDATE preserve as well (the closed P5 gap: the rewrite materializes each survivor's original
  id + commit version — so DV-off/protocol-1.0 tables, e.g. the PolyBase recipe synced to Fabric via a
  shortcut, keep update-stable ids). No creatable shape loses row tracking anymore (type-widened /
  IcebergCompat CoW would, but this provider can't create them). The buffered path's read-back resolves
  ids per row from the source's materialized value (else baseRowId + position) — the post-OPTIMIZE
  caveat is closed.
- Test: `test/verify_delta_row_tracking_virtual.test` (252).

### Stable-id fast path (DONE 2026-07-14)

Filters on `__delta_row_id` / `__delta_row_commit_version` skip **files and row groups**
(`DeltaRowTrackingFilter`) — point lookups, dedup DELETEs without a unique key
(`DELETE … WHERE __delta_row_id NOT IN (SELECT min(__delta_row_id) … GROUP BY <all cols>)`), and
`version > X` incremental extracts. **No Delta-log stats are written** for the columns (they're
off-schema — Spark writes none either): everything needed is already at hand.

- **Derived file** (no materialized physical column — every plain append): ids are exactly
  `baseRowId + position`, so the LOG alone bounds the file to `[baseRowId, baseRowId + numRecords)`
  (`NativeScanFile.NumRecords`, parsed from the add's stats) — skipped on no intersection, else the
  constraint rewrites to a `file_row_number` predicate (exact synthesized per-row-group min/max ⇒
  row-group skipping, the rowid-path machinery). The version is a per-file constant
  (`defaultRowCommitVersion`) — whole-file match/skip.
- **Materialized file** (rewrites carry ORIGINAL ids, decoupled from the fresh baseRowId — the
  derived-range subtraction must NOT be applied): the constraint pushes onto the PHYSICAL column in the
  per-file query's INNER WHERE as `(pred(col) OR col IS NULL)` — single-column ⇒ parquet zone maps
  prune; the IS NULL arm keeps derived-fallback rows (pre-tracking sources) visible. Inner placement is
  what binds the raw column instead of the COALESCE alias.
- Files with NULL `baseRowId` under a value constraint (pre-enablement adds, pending txn files) skip
  outright — the column reads NULL, which no value predicate matches.
- Extraction: AND-reachable compare/IN conjuncts plus single-column OR-of-equals (the live serializer's
  rendering of erased/dynamic IN filters); the conjuncts are stripped from the EW prune tree (no log
  stats — dropping only widens). Everything emitted is a superset conjunct; the outer WHERE stays exact.
- Gotcha pinned: OPTIMIZE's compacted add consumes fresh baseRowId space (HWM jump), so post-compaction
  inserts get stable ids ABOVE the pre-compaction range.
- Comparison: Spark/OSS-delta have NO skipping on `_metadata.row_id` (computed post-scan; metadata
  predicates prune only file-constant fields) — stable-id lookups here are faster than Spark's on the
  same tables. For pure lookup keys an IDENTITY column is still the portable answer (ordinary stats in
  every engine); the row-id path shines for correlation + keyless dedup.
- **Pre-existing DELETE bug found by the dedup test (fixed, all providers):**
  `DELETE … WHERE x [NOT] IN (subquery)` plans a MARK join with no projection before the DELETE, so the
  child chunk ends with the BOOLEAN mark — `AppendModifyBatch`'s "rowid = last column" read the mark as
  the rowid (`Vector::Reference … BIGINT referenced BOOLEAN`). Fix mirrors upstream
  `DuckCatalog::PlanDelete`: the rowid position comes from `LogicalDelete::expressions[0]`
  (`FabricatorModifyTarget.rowid_child_index`); UPDATE keeps the last-column contract (binder-built
  projection, as upstream `PhysicalUpdate` assumes).
- Test: `test/verify_delta_row_tracking_virtual.test` (now 299 — skip pins via duckdb_logs, the
  `file_row_number` rewrite pin, the post-OPTIMIZE physical-column pushdown pin, mark-join dedup DELETE).

## Recommendation — phased (build on demand)

1. **Phase A — read-only (the big win).** Generic `FabricatorMultiFileList/Reader` in `fabricator-core` +
   `list_scan_files`/`scan_schema` ABI + engineered-wood supplies list/DV/partitions/baseRowId. Native read
   with pushdown + parallelism + DV. Keep the current `fabricator_delta_scan` C# read as a fallback; validate
   against it. **Optional cheaper pre-spike:** `host_query` + `read_parquet([<files>])` (see
   [host-query.md](host-query.md)) with DV as an anti-joined C# input — days-scale, reuses existing plumbing,
   yields a real "native reader vs engineered-wood C# read" perf number before committing to the C++ work.
2. **Phase B — write.** `PhysicalCopyToFile`-based bulk write + `commit_add_files` ABI + engineered-wood
   commit (with `baseRowId` + `defaultRowCommitVersion` + `delta.rowTracking` domainMetadata).
3. **Phase C — DML + CDF.** rowid via `file_row_number`. **DV-DELETE (`deletion_vectors true`) is rewrite-free
   and rides on Phase A alone** (pure engineered-wood DV write, no native writer); copy-on-write DELETE/UPDATE
   use the Phase-B native write; merge-on-read DV-UPDATE is an optional later refinement. CDF change-data via a
   second tagged write.

Phase A alone resolves the "engineered-wood doesn't match Spark" concern on the read side entirely — because
then *DuckDB's* reader (the one tuned for maximal compatibility) does the reading.

## Why engineered-wood at all (vs DuckDB's `delta` extension)

DuckDB's `delta` extension does read (+ blind-append write) via delta-kernel-rs + `MultiFileReader`. Our value
over just telling users "use the official delta extension": a **pure-managed** log layer (no Rust build,
cross-platform, C#-extensible), **full DML** (DELETE/UPDATE, not blind-append-only), **multi-provider** reuse,
and **catalog integration** (`ATTACH`, three-part names, our secret/auth handling). This model keeps that
managed log layer while borrowing DuckDB's reader/writer.
