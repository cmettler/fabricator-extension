# MultiFileReader + engineered-wood — native-parquet Delta path (design, DEFERRED)

> Status: **design note — nothing built.** Source-grounded 2026-07-02 against `D:\repos\duckdb-delta`
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
"read a supplied list of parquet files with DV + column mapping") and lives in `arrownet-core`, reused by
every future lakehouse provider.

## Our shape: generic C++ core + a C# `ILakehouseSnapshot` interface

**C++ (new, `arrownet-core`, provider-agnostic):** `ArrowNetMultiFileList : SimpleMultiFileList` +
`ArrowNetMultiFileReader : MultiFileReader`, whose file list comes from an ABI metadata call (not
Delta-specific — Iceberg/Lance/plain-parquet all just return a file list). Register e.g.
`arrownet_delta_scan` by cloning `parquet_scan` + injecting `get_multi_file_reader`.

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
partly reverses "thin C++"; mitigated by keeping it provider-agnostic in `arrownet-core`. (2) Two read paths
(native for parquet-backed lakehouse; Arrow bridge for SQL/DAX) — acceptable, different provider kinds. (3)
No benefit for SQL Server / DAX — a Delta/lakehouse-only investment. (4) DV correctness: engineered-wood must
decode DVs to a bool/position set matching `file_row_number` semantics (its roaring reader is fixed).

## Recommendation — phased (build on demand)

1. **Phase A — read-only (the big win).** Generic `ArrowNetMultiFileList/Reader` in `arrownet-core` +
   `list_scan_files`/`scan_schema` ABI + engineered-wood supplies list/DV/partitions/baseRowId. Native read
   with pushdown + parallelism + DV. Keep the current `arrownet_delta_scan` C# read as a fallback; validate
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
