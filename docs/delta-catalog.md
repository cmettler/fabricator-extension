# Delta Lake catalog (folder-as-root) + write-back — design idea, DEFERRED

## Provider strategy + findings (2026-06-29)

Two Delta providers are emerging — keep them distinct:
- **`engineeredwooddelta`** (rename of the current Bridge-resident `DeltaBackend`/`DeltaCatalog`, built on
  Curt Hagenlocher's engineered-wood + the host-FS bridge). Read DONE (local); write via engineered-wood.
- **`delta-rs`** (future, production) — a wrapper over **delta-dotnet** (`D:\repos\delta-dotnet`: a C# wrapper
  around delta-rs `rust-v0.17.0` + delta-kernel-rs via a Rust FFI bridge, with DataFusion → a SQL layer for
  read/merge). It has its OWN object store, so we'd grab the DuckDB azure/etc. secrets and pass them through.
  It can't use DuckDB dynamic (join/TopN) filters directly; the alternative is a **C# MultiFileReader** like
  DuckDB's own delta extension (which manages dynamic filters) — i.e. delta-dotnet as a snapshot/file-list +
  Arrow source, dynamic filters applied in our scan loop. Native build is Rust (Linux-oriented README; Windows
  via WSL or a native toolchain — feasibility TBD).

**OneLake table discovery — via the Fabric REST API (DONE; the upstream glob bug is duckdb-azure #174, not ours).**
A mid-path-wildcard glob (`<root>/*/_delta_log/…`) throws `type must be string, but is null` recursing a OneLake
listing, so a OneLake root discovers its tables via the **Fabric REST API** instead of `HostFs.Glob`
(`FabricLakehouse`, Bridge): `TablesClient.ListTables(workspaceId, lakehouseId)` (`Microsoft.Fabric.Api` 2.14.0)
returns each `Table { Name, Location, Format }`. **Local / S3 / plain-ADLS roots keep the glob** —
`DeltaCatalog.DiscoverTables` branches on `FabricLakehouse.IsOneLake(_root)` (host contains `onelake.`).
Workspace + lakehouse are parsed from `abfss://<ws>@onelake…/<lh>[.Lakehouse]/Tables`: GUID segments are used
directly, display names are resolved via `WorkspacesClient`/`ItemsClient`. Auth = a `TokenCredential` minted from
the **ATTACH'd azure SP secret** — `ATTACH '…OneLake…/Tables' (TYPE arrownet, PROVIDER 'delta', SECRET <azure_sp>,
READ_ONLY false)` flows the secret through the v39 foreign-secret path → `DeltaBackend.BuildConnectionString`
appends a base64 cred marker on the root → `DeltaCatalog` extracts it into a `ClientSecretCredential` (mirrors
DAX). **Schema-enabled lakehouses** (`GetLakehouse.DefaultSchema` set; tables at `Tables/<schema>/<table>`) can't
be listed by `ListTables` (400) nor glob, so they're discovered via the lakehouse **SQL analytics endpoint**
(`SqlEndpointProperties.ConnectionString` + INFORMATION_SCHEMA, an Entra SQL token from the same SP —
`Microsoft.Data.SqlClient`); `DeltaCatalog` is multi-schema (schema-aware `TablePath`). The Fabric API / SQL
endpoint are used ONLY to list tables; data files go through DuckDB's FileSystem (the opener + a DuckDB azure
secret). **VALIDATED LIVE (2026-06-29)** on a schema-enabled lakehouse: ATTACH → CTAS `lake.dbo.t` → `DELETE
WHERE` → correct read-back. Catalog tables are written as **PLAIN Delta (no table features)** with **copy-on-write
DELETE** + a **transient `(file,position)` rowid** — NOT row tracking / deletion vectors, which Fabric/Spark
could not read (engineered-wood's DV byte format isn't Spark-decodable; see §3 below for the full trail).
**`READ_ONLY false` is required** — DuckDB force-bumps
remote (`abfss://`) attaches to read-only unless the access mode is explicit (`database_manager.cpp:105`); Delta
supports remote writes. **Caveat:** `duckdb_tables()` over OneLake is slow (materializes every table's columns);
use targeted `lake.<schema>.<t>`. The Unity Catalog API is an alternative but read-only.

**Write quality:** the engineered-wood write path emits one parquet file per input `RecordBatch` (TargetFileSize
is compaction-only). Row-group size IS controllable (`ParquetWriteOptions.RowGroupMaxRows` — now set to DuckDB's
default **122880** in `DeltaWriter`). To avoid the small-files problem on real writes, coalesce input batches.
The **catalog streaming write is now implemented** (`CREATE TABLE`/`INSERT`/CTAS/COPY stream straight to
engineered-wood via the standard bulk path, like the SQL/DAX backends) — so for the catalog case the global
`arrownet_delta_write` collector is no longer needed (it remains as the connection-free, no-ATTACH function form).

---
# (original) Delta Lake catalog (folder-as-root) + write-back — design idea

> Status: **design note only — nothing built.** Captures exposing a Delta Lake **folder** as an ATTACH-able
> catalog (each `_delta_log` subdir = a table), reusing the existing multi-provider architecture so a Delta
> table is read/written like any other catalog table. Write-back (INSERT / DELETE / UPDATE) is backed by
> **engineered-wood**'s read-write Delta implementation. Builds on the validated filesystem bridge
> ([docs/filesystem-bridge.md](filesystem-bridge.md)), the existing read-only `arrownet_delta_scan`
> ([docs/multifile-delta.md](multifile-delta.md)), and the provider model
> ([docs/provider-extensibility.md](provider-extensibility.md)).

## Why

Today Delta is a single **read-only global table function**, `arrownet_delta_scan(path)` (bespoke
`arrownet_delta.cpp`, materialized C# read). There is **no write target** — a SQL `DELETE FROM <delta>` /
`INSERT` has nothing to bind to. engineered-wood, however, is **full read-write** (see the capability survey
below), so the missing piece is a *catalog* that turns a Delta location into addressable, writable table
entries.

A Delta "database" has no server and no namespace — it's just a **directory tree of tables**. So the natural
catalog root is a **folder path**: `ATTACH '/lake/root' AS lake (TYPE arrownet, PROVIDER 'delta')`. Subdirs
containing a `_delta_log/` are the tables.

## It drops into the existing provider architecture (almost no new C++)

A Delta catalog is the **3rd `IBackend`** (after SqlServer and DAX) — `DeltaBackend` / `DeltaCatalog`,
registered in `BackendRegistry`, connstr = the folder path. The C++ core is already provider-agnostic: ATTACH,
the catalog/schema/table entries, the scan operator, and the INSERT/CTAS/DML operators all dispatch to the
handle's C# catalog. So a Delta catalog is **a C# `IBackendCatalog` + engineered-wood calls, with ~zero new
C++**.

- **Open** — `open_catalog(provider='delta', conn='/lake/root', options_json)` → a `DeltaCatalog` rooted at the
  folder. (Provider selection + ATTACH options already exist, ABI v17/v37.)
- **Discovery** — `get_metadata(Tables)`: list subdirs of the root containing `_delta_log/` via the FS bridge
  **`fs_glob`** (ABI v41), so it works for local + `az://` / `s3://` / `https://` + DuckDB secrets, exactly
  like the scan. `get_metadata(Columns)`: engineered-wood's snapshot schema (no data read), same path the
  current scan binds with.
- **Namespace** — Delta has none. Simplest: **flat** — every `_delta_log` dir under the root is a table in a
  single `main` schema. Alternative: one subdir level = schema, next = table (a 2-level namespace). The folder
  root gives a 1-/2-level namespace for free; start flat.
- **Scan** — reuse the engineered-wood `DeltaReader` already wired for `arrownet_delta_scan`, now per
  table-entry (projection/filter applied by DuckDB above the scan, as today; pushdown into the snapshot is a
  later optimization — see [docs/multifile-delta.md](multifile-delta.md)).

## engineered-wood write capability (surveyed)

`src/EngineeredWood.DeltaLake` is genuinely read-write, not a reader:

| Op | engineered-wood API | Notes |
|---|---|---|
| INSERT / APPEND | `DeltaTable.WriteAsync(batches, DeltaWriteMode.Append)` | clean — no rowid needed |
| OVERWRITE | `WriteAsync(batches, DeltaWriteMode.Overwrite)` | replace all data |
| CREATE | `DeltaTable.CreateAsync(...)` | writes protocol + metadata |
| DELETE | `DeltaTable.DeleteAsync(Func<RecordBatch,BooleanArray> predicate)` | **predicate-driven** (deletion vectors) |
| UPDATE | `DeltaTable.UpdateAsync(predicate, Func<RecordBatch,RecordBatch> updater)` | file rewrite; **updater is a batch transform**, not SET-expressions |
| MERGE / UPSERT | — | **not implemented** |

Plus: transaction-log commit writing (`TransactionLog.WriteCommitAsync(version, actions)`, atomic
temp-then-rename + version-conflict detection — no `OptimisticTransaction` type, the atomic version check is
the concurrency control), `ActionSerializer` (add/remove/metaData/protocol/commitInfo/txn/cdc/domainMetadata),
deletion-vector **read and write** (`DeletionVectorReader` / `DeletionVectorWriter`), CDC, Vacuum, Compaction.
All IO routes through the host `FileSystem` over the bridge callbacks (so secrets/remote work).

## The clean first slice — read + INSERT + CREATE (no rowid)

These map directly onto the existing DML operators and need no row identity:
- **INSERT / INSERT…SELECT / COPY** → `WriteAsync(Append)` (reuse the streaming bulk path's batches).
- **CTAS** → `CreateAsync` + `WriteAsync`.
- **CREATE TABLE** → `CreateAsync` with the column schema.
- (OVERWRITE / replace → `WriteAsync(Overwrite)`.)

This alone makes a Delta lakehouse a read+append target via the SQL surface — useful on its own (e.g. the
DirectLake-write idea in [docs/dax-provider.md](dax-provider.md): write the warehouse via SQL, the lakehouse
via this Delta catalog).

## Scan, data-skipping, and dynamic filters

engineered-wood does stats-driven skipping, but at different levels — and there's one plumbing gap + one
runtime-filter opportunity worth recording.

**File-level skipping — works (wired into the read scan).** `DeltaTable.ReadAllAsync(columns, filter)` builds a
`DeltaFilePruner` and drops each `add` file whose stats prove no match —
`StatisticsEvaluator.Evaluate(filter, stats, accessor) != AlwaysFalse` (`DeltaFilePruner.cs:37-44`) over the
Delta `add`-action **per-file column stats** (`MinValues`/`MaxValues`/`NullCount`/`NumRecords`) **+ partition
values**. So if we pass our predicate (`FilterNode → engineered-wood Predicate`) to `ReadAllAsync`, whole files
are pruned before any page is read — the big lever on partitioned/clustered tables.

**Row-group + bloom skipping — capability exists, NOT engaged for the Delta query filter (a plumbing gap).**
`ParquetFileReader.ReadAllAsync` can skip row groups when `_options.Filter` is set —
`StatisticsEvaluator.Evaluate(filter, RowGroups[i], accessor)` → skip `AlwaysFalse`, with an optional bloom
check on `Unknown` (`ParquetFileReader.cs:187-205`). **But** the Delta layer's `ReadFileAsync` opens the reader
with the *table-level* `_options.ParquetReadOptions` (`DeltaTable.cs:1086-1087`) and uses the per-query `filter`
only for the file-level pruner — so within an included file **every row group is read**. Closing this is a
small engineered-wood change: thread the per-query filter into the parquet reader's options inside
`ReadFileAsync` (the evaluator + bloom code is already there). Worth it for big files / selective predicates.
Correctness is unaffected either way — our pushdown never erases and DuckDB re-applies every predicate above the
scan; this is purely I/O+decode avoidance.

**Dynamic filters (join / TopN) — applicable via the live `TableFilterSet`.** The high-value case for a Delta
fact table is a *join* with no static predicate on the fact (`SELECT … FROM delta_fact f JOIN small_dim d ON
f.k = d.k`): at runtime DuckDB's **join-filter-pushdown** (`join_filter_pushdown.hpp`) has the hash-join build
side (the small dim) compute a min/max (and sometimes an IN set) over the key and attach it to the probe-side
scan as a **dynamic `TableFilter`** (TopN does the same with a threshold). These reach a table-function scan via
`TableFunctionInitInput.filters` (`optional_ptr<TableFilterSet>`, `table_function.hpp:144`) — the *live* set,
which for a hash-join probe is **resolved by the time the probe-side scan executes** (build completes first).

The hook: our static pushdown runs at bind (`pushdown_complex_filter` → `FilterNode`), *before* dynamic filters
exist. To use them, the Delta scan reads `init.filters` at **execute time** (`init_global` / the per-execution
stream factory), translates the resolved dynamic `ConstantFilter`/min-max (and IN) for the relevant columns
into our `FilterNode`/`Predicate`, **merges it with the static predicate**, and hands the combined predicate to
`ReadAllAsync` for file pruning (and, once the gap above is closed, row-group pruning). A min/max dynamic filter
maps exactly onto what `DeltaFilePruner` already evaluates against `MinValues`/`MaxValues`, so star-schema
queries skip files whose key-range doesn't overlap the dim — a big win with no static WHERE.

A nice property of the Delta scan specifically: it **iterates files in a C# loop**, so it can **re-read the
(updating) dynamic filter set before opening each file** — catching late-resolving filters and giving per-file
late-binding skipping, rather than snapshotting once at init. Dynamic filters are always safe
over-approximations (min/max), so this never drops rows, and DuckDB re-applies above the scan regardless.

**Caveat / to verify:** whether DuckDB's join-filter-pushdown optimizer actually *targets* our catalog scan
(it's designed for base-table scans that support filter pushdown + report stats — which our catalog tables do;
the Delta catalog would need to present the same way). Confirm the dynamic filter reaches
`TableFunctionInitInput.filters` for a Delta catalog scan before relying on it. This is additive on top of the
static file-pruning slice — build static first, add dynamic when the join-skipping win is wanted.

## The one real design decision — DELETE / UPDATE: rowid vs predicate

Our existing DML is **rowid-driven**: DuckDB scans with the filter, collects the **rowids** of matching rows,
and hands them to the catalog's delete (for SQL Server: `DELETE WHERE rowid IN (...)`). engineered-wood is
**predicate-/rewrite-driven**. Two ways to bridge, pulling the design differently:

1. **rowid = (file, row-index)** — the natural Delta address. Reuses our **entire** rowid-based DML path:
   DuckDB gives a rowid set → group by file → write **deletion vectors** for those positions → add/remove
   actions. Best fit for the existing machinery, but engineered-wood's public delete is predicate-only (the
   `DeletionVectorWriter` is internal to `DeleteAsync`), so this needs a **small engineered-wood addition**: a
   position-based delete ("apply a DV to these (file, rowindex) sets"). A scan must also surface a stable
   (file, rowindex) rowid (engineered-wood already addresses rows this way for DVs).

2. **predicate-based** — map the DELETE **filter** (`FilterNode → engineered-wood Predicate`, see below) and
   call `DeleteAsync` directly; engineered-wood owns the file rewrite / DV. No rowid, but it means **bypassing
   DuckDB's default rowid-driven delete** for this catalog (a more divergent DML path than SQL/DAX use).

**`FilterNode → Predicate` is clean and gap-free in the direction we need.** engineered-wood
(`EngineeredWood.Expressions`) has a real predicate model + an Arrow row evaluator
(`IRowEvaluator.EvaluatePredicate(Predicate, RecordBatch) → BooleanArray`), so a `Predicate` adapts to
`DeleteAsync`'s lambda in one line: `batch => evaluator.EvaluatePredicate(pred, batch)`. The map:

| our `FilterNode` | engineered-wood |
|---|---|
| `and` / `or` | `AndPredicate` / `OrPredicate` (both N-ary) |
| `compare` `=,<>,<,<=,>,>=` | `ComparisonPredicate` |
| `is_null` / `is_not_null` | `UnaryPredicate(IsNull/IsNotNull)` |
| `in` | `SetPredicate(In)` |
| `is_distinct` / `is_not_distinct` | `NullSafeEqual` (± a `NotPredicate` for polarity) |

The gaps a naive comparison surfaces (`StartsWith`, `IsNaN`, `True/False`, field-id binding) all live in the
*other* direction (engineered-wood features SQL can't represent) and don't bite us — `FilterNode` is the
subset. Our constants are indices into a separate Arrow value batch → resolve via `ArrowValueReader.ReadScalar`
→ `LiteralValue.Of(...)`. So the predicate path is a ~60-line tree walk regardless of which delete strategy
wins (it's also reusable for the UPDATE WHERE).

**UPDATE's SET clause** is the genuinely open part: engineered-wood's updater is `Func<RecordBatch,RecordBatch>`
(not per-column SET expressions). The WHERE reuses the predicate map; the SET needs either a **value**-expression
evaluator (confirm engineered-wood exposes `Expression → column`, not just `Predicate → bool`) or a
batch-transform we build from the bound SET assignments. DELETE is the cleaner target first.

## Transaction semantics caveat

engineered-wood commits **per write** (atomic version-N + conflict detection). A DuckDB multi-statement
`BEGIN…COMMIT` spanning several Delta writes is therefore **not** one atomic Delta transaction — Delta is
per-commit, no cross-table ACID. State this up front; it's a provider property, not a bug. (A single
INSERT/DELETE = one Delta commit = atomic, which covers the common case.)

## Host-FS write capability — probe findings (the commit-primitive blocker)

Before building any Delta write-back, `arrownet_fs_write_probe(base_path)` (a C++ spike in `arrownet_fs_spike.cpp`,
no ABI) exercises DuckDB's `FileSystem` write surface directly — the managed reverse-callbacks would forward to
these same calls, and the opener/secret path is identical, so it faithfully answers "is DuckDB's FileSystem
capable?" for local AND (when pointed at an `az://`/`s3://` prefix with a secret) object stores.

**Result on Windows local (`build/release/duckdb.exe`):** write / read-back / `FileExists` / `MoveFile`-to-new /
`RemoveFile` / `TryRemoveFile` / `CreateDirectory` all **work**. The blocker is the **commit primitive**:

- **`EXCLUSIVE_CREATE` is IGNORED on Windows local.** `WRITE|FILE_CREATE|EXCLUSIVE_CREATE` on an *existing* file
  **succeeded** (it should fail = put-if-absent). Root cause (`local_file_system.cpp`, Windows `OpenFile`):
  `FILE_CREATE → OPEN_ALWAYS`, `FILE_CREATE_NEW → CREATE_ALWAYS` (overwrite), and `flags.ExclusiveCreate()` is
  **not consulted** — there is no `CREATE_NEW` disposition. (The POSIX branch *does* map `FILE_CREATE | EXCLUSIVE_CREATE`
  → `O_CREAT | O_EXCL` = a real put-if-absent, so this is a Windows-local gap, not universal.)
- **`MoveFile` OVERWRITES the target** on every platform (POSIX `rename()` — with a source `//! FIXME: rename does
  not guarantee atomicity or overwriting target`; Windows `MoveFileExW`). So it is **not** a fail-if-exists commit
  primitive either.

**Implication.** engineered-wood's commit is `write temp → RenameAsync(temp, N.json)` and keys conflict detection
off the rename/target-exists check. On DuckDB's FileSystem that check **does not hold** (rename overwrites;
EXCLUSIVE_CREATE unreliable), so a Delta write-back through the host-FS bridge is **last-writer-wins on the version
file → silent lost commits** under concurrency. **So "fully implement the host filesystem" is necessary for the
data-file I/O but NOT sufficient for safe *concurrent* commits** — DuckDB's `FileSystem` abstraction does not expose
a portable put-if-absent / atomic-no-overwrite.

**Consequences for sequencing:**
- **Single-writer Delta write-back is fine** on this foundation (one committer, no race) — the realistic first target.
- **Safe concurrent commits need a primitive DuckDB FS doesn't give.** Options: (a) an external commit lock /
  coordinator (Delta-on-S3's historical DynamoDB pattern); (b) a dedicated put-if-absent **host callback that
  bypasses `FileSystem`** and uses the store's native conditional create (Azure `If-None-Match: *`, S3 conditional
  PUT, POSIX `O_CREAT|O_EXCL`) — i.e. don't route the commit through DuckDB's `MoveFile`; (c) accept single-writer.
**OneLake (`abfss://Test@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Files/test`) — full probe, validated
live (`azure`+`httpfs` autoloaded; service-principal azure secret):**
- write / read-back / `FileExists` / `RemoveFile` / `TryRemoveFile` / `CreateDirectory` all **work**.
- **`EXCLUSIVE_CREATE` IS HONORED on OneLake/ADLS DFS** — exclusive create on an existing file threw
  *"AzureDfsStorageFileSystem will not open file: … ExclusiveCreate specified while file already exists."*, and
  on a new path it succeeded. So the **put-if-absent commit primitive EXISTS on the real cloud target** (unlike
  Windows local, which ignores the flag). This is the key positive result.
- **`MoveFile` is NOT IMPLEMENTED on Azure DFS** — *"AzureDfsStorageFileSystem: MoveFile is not implemented!"*.
- Azure DFS writes are **sequential or at location=0 only** (no random mid-file writes) — fine for Parquet/JSON
  which are written sequentially.

**Conclusion (flips positive): safe concurrent Delta commits ARE achievable on OneLake through DuckDB's
FileSystem — but the commit must write `N.json` DIRECTLY with `EXCLUSIVE_CREATE`, never via temp+rename**
(MoveFile throws there). engineered-wood's `TransactionLog.WriteCommitAsync` hardcodes temp+rename, so a
write-back needs the commit routed through a direct exclusive-create instead — either a small engineered-wood
addition (a put-if-absent commit-write) or our own commit step that calls a put-if-absent host-FS write
(`WRITE|FILE_CREATE|EXCLUSIVE_CREATE`) and maps the throw to `DeltaConflictException` → reopen → retry.
- Caveat — the matrix is FS-specific: Windows local ignores `EXCLUSIVE_CREATE` (no guard) and `MoveFile`
  overwrites; POSIX local has `O_CREAT|O_EXCL` but `MoveFile` overwrites (FIXME); OneLake/ADLS honors
  `EXCLUSIVE_CREATE` but has no `MoveFile`. So the **commit path must be exclusive-create everywhere** (the one
  primitive that's safe on the cloud target), with local-Windows treated as single-writer/dev-only.
- The SP used was the Fabric-Warehouse service principal; it has OneLake Files read+write here, so one secret
  serves SQL endpoint + OneLake (matches the v39 foreign-secret reuse).

## Recommendation (sequenced; build on demand)

0. **Host-FS WRITE surface — DONE (ABI v48).** `ArrowNetHostServices` gained `fs_open_write`(exclusive) /
   `fs_write` / `fs_close_write` / `fs_remove` / `fs_create_dir`(recursive); `DuckDbTableFileSystem` implements
   the write side (`CreateAsync`/`WriteAllBytesAsync`/`RenameAsync`/`DeleteAsync` + `DuckDbSequentialFile`). The
   commit's put-if-absent rides `EXCLUSIVE_CREATE` (since DuckDB `MoveFile` overwrites on local + is unimplemented
   on Azure DFS, `RenameAsync` is emulated as exclusive-create-copy → returns false on an existing target →
   engineered-wood maps to `DeltaConflictException`). `HostFsGlob` normalizes object-store 404 to empty.
   **Validated end-to-end on local AND a live OneLake lakehouse** by the `arrownet_delta_write_demo(path)` global
   host-FS table fn (writes a 5-row Delta table via engineered-wood, idempotent Overwrite, round-trips with
   `arrownet_delta_scan`) — `test/verify_delta_write.test`. Single-writer; concurrent commits are safe where
   `EXCLUSIVE_CREATE` is honored (OneLake/POSIX).
   - **Portability of engineered-wood output (validated with the REFERENCE reader, delta-kernel-rs via DuckDB's
     official `delta` extension).** engineered-wood's defaults are NOT readable by standard Delta/parquet tooling
     (incl. Microsoft Fabric); three fixes were required so a written table reads in delta-kernel-rs / DuckDB
     native / Fabric:
     1. **`metaData.format.options`** — engineered-wood omitted it when empty; it's non-nullable for strict
        readers. Fixed in engineered-wood `ActionSerializer` (always emit `"options":{}`).
     2. **`metaData.configuration`** — same (omitted when null). Fixed (always emit `{}`).
     3. **parquet `path_in_schema`** — engineered-wood's `ParquetWriteOptions.OmitPathInSchema` defaults `true`,
        dropping this REQUIRED column-chunk field → standard parquet readers throw `TProtocolException: Invalid
        data` (Apache-Thrift "required field missing"). Fixed on our side: write with
        `ParquetWriteOptions { OmitPathInSchema = false }`.
     With all three, delta-kernel-rs reads the table locally. (#1/#2 are engineered-wood-repo patches alongside
     the existing `ActionSerializer` fix; #3 is our write option.) NOTE: DuckDB's official `delta_scan` can't
     LIST a OneLake `_delta_log` (delta-kernel azure object_store + DuckDB-secret quirk: "No files in log
     segment") so it can't validate on OneLake — but our own reader does, and the local delta-kernel read proves
     the format. A table written before these fixes stays broken on its version-0 `metaData`; write a FRESH
     table.
1. **Folder-root `DeltaCatalog` + read — DONE (local; OneLake discovery caveat below).** `DeltaBackend` (3rd
   `IBackend`, name `"delta"`/`"deltalake"`, registered explicitly in `BackendRegistry.Discover` since it lives
   in the Bridge alongside `DeltaReader`) + `DeltaCatalog : IBackendCatalog` (read-only this slice; writes
   throw). `ATTACH '/lake' AS lake (TYPE arrownet, PROVIDER 'delta')` → tables = immediate subdirs with a
   `_delta_log/` (globbed `<root>/*/_delta_log/*.json`), flat `main` schema; columns via `DeltaReader.GetSchema`;
   scan via `DeltaReader.Stream` with filter pushdown (projection left to DuckDB above). The host-FS **opener**
   is threaded into the catalog metadata path: `LoadCatalog`/`RefreshCache` now call `ArrowNetSetActiveTxn`
   (which also sets the opener) before discovery, and `FetchTableColumns` already did. Validated on a LOCAL
   Delta root: `test/verify_delta_catalog.test` (17 — discovery, filter pushdown, aggregate, cross-table join).
   **OneLake table discovery uses the Fabric REST API** (the DuckDB azure glob can't recurse a OneLake
   `_delta_log` tree — duckdb-azure #174 — `<root>/*/_delta_log/…` throws `type must be string, but is null`).
   `DeltaCatalog.DiscoverTables` branches on `FabricLakehouse.IsOneLake(_root)`: OneLake →
   `TablesClient.ListTables`; **local / S3 / plain ADLS keep the glob**. See the "OneLake table discovery" note
   near the top of this doc for auth (the ATTACH'd azure SP secret) + workspace/lakehouse resolution. Live Fabric
   validation pending.
2. **Write arbitrary data — DONE, both the function form AND the catalog (streaming) form.**
   - **Function form** (`arrownet_delta_write(<input>, path := '…')`): a connection-free GLOBAL host-FS
     **collector** (`DeltaWriteCollectorFunction`) that writes ANY input table (a DuckDB query result) to a Delta
     table at `path` (Overwrite), returning `(version, rows_written)`. It buffers the input (copying it out via an
     Arrow IPC round-trip — the operator frees each batch after consumption), then commits one Delta version
     through the shared `DeltaWriter` (OmitPathInSchema=false → Fabric-readable). The opener is threaded through
     the collector operator's Source `GetDataInternal` (where the C# `Collect` actually runs, sync-over-async on
     the pull thread — setting it only in Finalize was racy). Cost args ride as NAMED params (`Parameters` added
     to `IInOutFunction`/`ICollectorTableFunction`, surfaced via the handle-0 `GlobalFunctions.ParamSchema`).
     Validated local (`test/verify_delta_write.test`, official delta-kernel read-back) + a live OneLake managed
     table (`Tables/dbo/arrownet_query`, 20-row query result).
   - **Catalog (streaming) form — DONE** (`ATTACH … (TYPE arrownet, PROVIDER 'delta')`; `CREATE TABLE` / `INSERT
     INTO lake.t` / CTAS / COPY): the catalog INSERT/CTAS/COPY operators now stream straight to engineered-wood
     via the **existing streaming bulk path** (`begin_bulk`/`push_batch`/`complete_bulk` → `BulkSession` →
     `DeltaCatalog.BulkInsert`), exactly like the SQL Server / DAX backends — no separate global collector needed
     for the catalog case. **Opener threading:** the host-FS opener (`ClientContext*`) is set via
     `SetActiveOpener` immediately before `BeginBulk` in the insert/CTAS/COPY operators; `BulkSession` captures it
     at `begin_bulk` and **re-establishes `AmbientOpener.Current` on its background consumer thread** (alongside
     the txn id) — the opener stays valid until `complete_bulk`, which blocks on the consumer. `DeltaCatalog`:
     `createTable`/`replace` ⇒ `DeltaWriteMode.Overwrite` (CTAS/REPLACE: the table becomes exactly the query
     result), plain INSERT ⇒ `Append`; one Delta commit per statement. `CreateTable` writes an empty commit-0
     (schema only; PK/UNIQUE/DEFAULT ignored — Delta has no such constraints). `DeltaWriter.Materialize` does the
     Arrow IPC round-trip from the streamed `ChannelArrowStream` into retained batches for the single commit.
     Validated local: `test/verify_delta_catalog_write.test` (31 — CREATE/INSERT/append/CTAS/aggregate + DROP
     TABLE + detach/re-attach durability). **DROP TABLE** recursively deletes the table's `<root>/<table>/` folder.
     **Local/S3** use the host-FS callback `fs_remove_dir` (ABI v49 — DuckDB's `FileSystem::RemoveDirectory`,
     idempotent). **OneLake** uses a **direct ADLS Gen2 / OneLake DFS recursive delete** instead
     (`DropTable` branches on `FabricLakehouse.IsOneLake` → `FabricLakehouse.DeleteDirectory` →
     `DataLakeDirectoryClient.DeleteIfExistsAsync`, the Azure SDK, idempotent) — because DuckDB's azure FileSystem
     throws `AzureDfsStorageFileSystem: RemoveDirectory is not implemented!` (no recursive delete on the DFS
     endpoint), and the glob-files-then-`fs_remove` fallback is dead (the duckdb-azure mid-path-wildcard glob bug,
     PR #174, returns 0 rows at every OneLake level). The DFS delete uses the same SP `ClientSecretCredential` the
     catalog mints (the data files are still read/written through DuckDB's FileSystem; the DFS endpoint is used for
     listing + delete only). Validated live 2026-06-30 on `LH` (schema-enabled) and `LH_no_schema` (flat) — DROP
     succeeds and the table directory is confirmed gone via a DFS re-list. **ADD COLUMN — DONE**: a metadata-only
     commit (`DeltaTable.AddColumnAsync`) + read-side NULL backfill of old files (`BackfillMissingColumns`);
     validated local (`verify_delta_catalog_alter.test`, 81) + live on `LH`. **RENAME TABLE — DONE (OneLake only)**:
     the table is its folder + Delta logs are table-relative, so rename = move the folder; OneLake uses the DFS
     atomic native rename (`FabricLakehouse.RenameDirectory` → `DataLakeDirectoryClient.RenameAsync`, destination
     filesystem-relative without the workspace prefix). **local/S3 RENAME — DONE (ABI v50)** via a new host
     `fs_move_dir` → `FileSystem::MoveFile` (atomic dir rename on local; object stores throw cleanly if MoveFile
     is unimplemented). Validated live on `LH` + local (`verify_delta_catalog_schemas.test`). DROP/RENAME COLUMN + ALTER COLUMN TYPE stay unsupported (column mapping /
     rewrite). **Multi-schema for local/S3 — `schemas true` ATTACH option (DONE)**: default is a FLAT main-only
     catalog (schema ignored → `db.staging.t` and `db.main.t` would collide at `<root>/t`); `schemas true` switches
     to the two-level `<root>/<schema>/<table>` layout (`SchemaLayout` drives `TablePath`/discovery/`CreateSchema`/
     `DropSchema`; globs `<root>/*/*/_delta_log/*.json`). OneLake ignores it (layout from the lakehouse flag). No
     ABI change (rides the v37 options-JSON forwarding). `verify_delta_catalog_schemas.test` (23). **Still
     unsupported** (clean error): raw exec; DROP SCHEMA outside `schemas` mode.
3. **DELETE — FINAL: copy-on-write + transient `(file,position)` rowid, PLAIN Delta (no features).** The
   detailed design below (row tracking + deletion vectors) is the SUPERSEDED first attempt — kept as the trail.
   Why it changed: Fabric's OneLake converter / Spark could not read our row-tracking + DV commits (first from
   missing protocol feature declarations — `domainMetadata` dep, `deletionVectors` reader-v3 — and then, even
   with a compliant protocol, because engineered-wood's inline DV byte format isn't Spark-decodable). Final
   design: write **plain Delta** (minReader 1 / minWriter 2, no features); the DuckDB rowid is a TRANSIENT
   `(fileOrdinal << 40) | rowPosition` (file ordinal in the path-sorted active set) computed at scan
   (`ReadAllWithRowIdsAsync`), and `DeleteByRowIdsAsync` decodes it → **copy-on-write** rewrite of each affected
   file (plain `remove`+`add`, no DV). Validated live on the schema-enabled `LH` lakehouse (`arrownet_deltest4`:
   v0 protocol minReader 1/minWriter 2/no features, DELETE = plain remove+add). The virtual-rowid C++ threading +
   `DeltaCatalog.ExecuteDelete` wiring are unchanged from the SUPERSEDED notes — only the rowid *meaning*
   (transient, not stable) and the delete *mechanism* (rewrite, not DV) changed. **(SUPERSEDED below:)** the rowid
   pattern via Delta row tracking (mirrors the SQL Server
   backend — reuses ArrowNet's existing rowid DML operators wholesale, no OptimizerExtension / custom operator).
   **Key finding that drove this:** DuckDB does NOT expose the WHERE predicate at the catalog's `PlanDelete`/
   `PlanUpdate` hook (`LogicalDelete` retains only the table + a rowid-producing child plan), so a "capture the
   FilterNode" predicate-delete is unsafe (the pushdown filter is a superset; a residual filter would over-delete)
   AND would need a custom operator. The rowid pattern sidesteps all of that.
   - **engineered-wood additions — DONE** (`D:\repos\engineered-wood`, local working changes; the repo's
     row-tracking was write-only): (a) `DeltaTable.ReadAllWithRowIdsAsync(columns, filter)` — appends a trailing
     non-null Int64 `_metadata.row_id` column (the stable row-tracking id, captured after DV filtering,
     re-appended after transforms; `ReadFileAsync` gained an `includeRowId` flag that reads the materialized
     `__delta_row_id` physical column); (b) `DeltaTable.DeleteByRowIdsAsync(IReadOnlyCollection<long> rowIds)` —
     deletes by stable id via deletion vectors (reads each file's `__delta_row_id`, maps ids→positions, writes a
     DV + RemoveFile/add). Both require `delta.enableRowTracking=true`. Builds clean.
   - **ArrowNet wiring — DELETE DONE** (`test/verify_delta_catalog_delete.test`, 28): (1) `DeltaCatalog.CreateTable`/
     `BulkInsert` enable `delta.enableRowTracking=true` (catalog-created tables only; the global write
     collector/demo leave it OFF for max delta-kernel compatibility); (2) `DeltaCatalog.GetMetadata(RowId)` returns
     `_metadata.row_id` IFF the table has row tracking (external/legacy tables → no rowid → DML cleanly disabled);
     (3) **virtual-rowid threading in C++** — the rowid machinery resolved rowid names to INDICES into the user
     column list (`arrownet_schema_entry.cpp`), but `_metadata.row_id` is NOT a user column (surfacing it as one
     would break INSERT). So when `FetchRowIdColumns` returns names absent from the schema, they're a **virtual
     rowid**: `ArrowNetTableEntry` + `ArrowStreamBindData` carry the NAMES (not indices) in `virtual_rowid_columns`,
     `HasRowId()`/`GetVirtualColumns()`/`GetRowIdColumns()` honor them, `BuildScanSpec` adds them to the fetch list
     when rowid is requested, `arrow_ingest` resolves their result positions BY NAME for `BuildRowId`
     (rowid_type=BIGINT for a single virtual col), and `BuildModifyTarget` uses the virtual names + BIGINT. SQL
     Server is unaffected (its rowid names always resolve to real columns; the virtual branch never fires —
     `verify_proc_inout`/`verify_time_travel`/`verify_columnstore` green); (4) `DeltaCatalog.ScanTable` streams via
     `StreamWithRowIds`/`ReadAllWithRowIdsAsync` (advertising `_metadata.row_id`) when the spec requests it;
     (5) `DeltaCatalog.ExecuteDelete` collects the `_metadata.row_id` keys → `DeleteByRowIdsAsync`. NO ABI change.
   - **UPDATE — REMAINING** (next): `DeltaCatalog.ExecuteUpdate` → an engineered-wood `UpdateByRowIdsAsync(ids,
     newValuesByRowId)` that rewrites affected files substituting the SET columns (the `updater` analog keyed by
     row id; the SET values arrive via the existing rowid-UPDATE operator as the leading `set_count` columns).
4. MERGE/UPSERT — out of scope (engineered-wood doesn't implement it; could be composed delete+append,
   non-atomic).

**Net:** the folder-as-catalog-root + read + INSERT + CREATE is a small, well-fitting slice that reuses the
provider architecture wholesale; DELETE/UPDATE is where the one real choice (rowid→DV vs predicate, + UPDATE
SET) lives. Build when a Delta write-back need is concrete (the DirectLake-write path is the likely driver).
