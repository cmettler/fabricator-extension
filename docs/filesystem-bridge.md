# Host FileSystem bridge — reverse callbacks + Delta reader (SPIKE / foundation)

> Status: **spike, validated** (ABI v41). Proves a managed component can do **secret-backed remote IO via
> DuckDB's `FileSystem`** AND, on top of it, that **Curt Hagenlocher's *engineered-wood* (pure-C# Delta Lake)
> reads a real Delta table through that bridge**, surfaced as Arrow → DuckDB. The foundation for a C#
> lakehouse-format provider (Delta/Iceberg/Lance/ORC/Avro) that reuses DuckDB's filesystem + secrets instead
> of shipping its own cloud SDK filesystems. Not a user feature yet.

## Why

A C# lakehouse reader needs to read remote files (`az://`, `s3://`, `https://`) with credentials.
DuckDB's `FileSystem` already implements all those backends (via httpfs / azure) and resolves credentials
from **DuckDB secrets**. Rather than re-implement cloud IO + auth in C# (engineered-wood has
`EngineeredWood.Azure/.Aws/.Gcs`), the managed side can **call back into DuckDB's FileSystem** — one auth
config (DuckDB secrets) shared with native DuckDB reads, and no SDK duplication. The abstraction fit is
near 1:1: DuckDB's `FileSystem` is offset-addressed (`Read(handle, buf, nr, location)`) with a
credential-carrying opener on every call, matching engineered-wood's `IRandomAccessFile` /
`ITableFileSystem`.

## The mechanism (reverse callbacks)

The vtable is C++→C# (the host fills it). The filesystem bridge is the **reverse direction**: the host
provides `FabricatorHostServices` (in `abi.h`) — a struct of function pointers the **managed side calls** to
reach DuckDB's `FileSystem`:

- `fs_open_read(opener, path, *out_file, err)` / `fs_size` / `fs_read(file, buf, nr, location, err)` /
  `fs_close` / `free_str`.

The host fills it (`RegisterFsSpike` → `InstallHostFsServices`, **before the bridge boots**) and passes it to
`Bootstrap.Initialize(vtable, size, host)`; the managed side caches it in `HostFs`. C# `HostFileSystemSpike`
wraps the callbacks.

### The opener / secret threading (the key finding)

`FileSystem::GetFileSystem(context)` returns an **`OpenerFileSystem`** that **auto-pushes** the context's
`FileOpener` (secret resolution) into every call — so the host callback opens with **no explicit opener**
(passing one errors: *"OpenerFileSystem cannot take an opener — the opener is pushed automatically"*).
Secret-backed paths therefore resolve their DuckDB secret exactly as a native read does. The `opener`
argument across the ABI is just the **`ClientContext`** of the operator that initiated the managed call
(valid for that synchronous call) — secrets are context-scoped, so filesystem ops happen at points where the
host holds a context (here: an `fabricator_fs_spike(path)` table-function execution).

## What the spike proves (validated)

`fabricator_fs_spike(path)` (table function) → hands the managed side the `ClientContext` → C# opens the path
via the host callbacks, reads head + tail bytes + size, returns a string:

- **Local parquet:** `ok size=41128 head='PAR1' tail='PAR1'` — reverse callback + offset footer read.
- **Remote `https://` via httpfs:** `ok size=1842 head='…' tail='…'` — a real remote FileSystem, a **range
  GET at `size-4`**, with the opener auto-pushed (httpfs used it for the request).

So: C# → DuckDB `FileSystem` → offset reads, both local and remote; the opener/secret channel works. For
`az://`/`s3://` + a DuckDB secret it is the *same* path (httpfs/azure resolves the secret off the auto-pushed
opener — and azure-secret resolution itself is already validated, see provider-extensibility.md §2.1). The
only piece not directly smoke-tested is a live `az://` blob (no blob store + file handy), but it shares the
proven code path.

> **Update (ABI v47): `fabricator_delta_scan` is now a connection-free GLOBAL host-FS table function, not a
> bespoke C++ table function.** The bespoke `fabricator_delta.cpp` + the `delta_schema`/`delta_scan` vtable entries
> were removed; delta is a pure-C# global `ITableFunction` (`DeltaGlobalTableFunction`, declared in
> `CustomFunctions.GlobalTable`) dispatched through the v29 table session (`table_bind`/`table_execute`). The
> only new plumbing is the opener: a global host-FS reader needs the calling operator's `ClientContext` (for
> DuckDB secret resolution) at bind + execute, threaded via a per-thread ambient (`AmbientOpener`, mirroring
> `set_active_txn`) the host sets with the appended `set_active_opener` ABI entry. So a NEW lakehouse format
> (Iceberg/Lance/…) is now added with **zero C++** — see docs/global-functions.md §"Host-FS global table
> functions". The DeltaReader / DuckDbTableFileSystem C# below are unchanged; only the dispatch moved. The
> sections below describe the original (now-superseded) bespoke wiring.

## Delta reader on the bridge — `fabricator_delta_scan(path)` (BUILT, validated; now a global host-FS fn)

`fabricator_delta_scan('<delta table root>')` reads a Delta Lake table via engineered-wood, with **all IO going
through DuckDB's FileSystem** over the host callbacks — so local, `az://`, `s3://`, `https://` paths and DuckDB
secrets all work, one auth config shared with native reads.

- **`HostFs.fs_glob`** added to `FabricatorHostServices` (DuckDB `FileSystem::Glob` → JSON `[{path,size}]`) — the
  directory listing engineered-wood needs to enumerate `_delta_log/`.
- **C# `DuckDbTableFileSystem : ITableFileSystem`** + `DuckDbRandomAccessFile : IRandomAccessFile`
  (`DuckDbTableFileSystem.cs`) — read-only, over the host callbacks. Paths are root-relative (matching
  `LocalTableFileSystem`): `ListAsync` returns paths relative to the table root, re-resolved by
  `OpenReadAsync`/`ReadAllBytesAsync`. Write methods throw. Reads are synchronous host calls in completed
  `ValueTask`s (the hostfxr CLR has no `SynchronizationContext`, so engineered-wood's `await` upstream can't
  deadlock).
- **`DeltaReader`** (`DeltaReader.cs`): `delta_schema` (bind) = `DeltaTable.OpenAsync(fs).ArrowSchema` (no data
  read); `delta_scan` (execute) = `OpenAsync` + `ReadAllAsync()` **materialized** into an `InMemoryArrayStream`
  (all host IO happens while the opener/ClientContext is valid — the opener need not outlive the call).
- **C++ `fabricator_delta_scan`** (`fabricator_delta.cpp`): bind → `DeltaSchema` → `ReadArrowSchema` → return
  types; init_global → `DeltaScan` → `ArrowStreamReader`; scan → `Read()` per chunk. DuckDB applies
  projection/filter/aggregation above the scan.
- **engineered-wood patch (local clone):** `ActionSerializer` read optional `add`/`remove` numeric fields
  (`baseRowId`, `defaultRowCommitVersion`, remove `size`/`deletionTimestamp`) with a bare `GetInt64()`, which
  throws on delta-rs's explicit `"field":null` (engineered-wood's own writer omits them). Guarded with
  `TokenType == Null ? null : GetInt64()` — an upstream-worthy robustness fix for reading delta-rs tables.

**Validated** (`test/verify_delta.test`, 60 assertions; fixture `test/fixtures/delta_simple`, a delta-rs table
of 10 rows id/name/amount): full scan with correct bind-time types, filter+aggregate, `DESCRIBE` schema, and
the pushed-filter cases (`=`/`IN`/`AND`-range, and string `=`/`>`/`<>` — all pushed into engineered-wood
skipping, byte-order-sound) — all green. The Apache.Arrow version is aligned (engineered-wood + the bridge both
**23.0.0**, both net10.0). Now streams lazily (no materialization) with filter pushdown — see the section below.

## Streaming + filter pushdown (DONE, ABI v47)

- **Streaming (not materialized) — DONE**: `DeltaReader.Stream` is a lazy `IAsyncEnumerable<RecordBatch>` over
  engineered-wood's `ReadAllAsync` (one batch per host pull, ≤1 buffered). The opener (captured at `Execute`)
  stays valid across the scan — the ClientContext lives for the whole table-function execution. Replaced the
  old materialize-into-`InMemoryArrayStream`.
- **Filter pushdown into file + row-group skipping — DONE**: `DeltaFilterBuilder` maps the scan's `FilterNode`
  tree (constants read from `filter_values` via `ArrowValueReader`) into an engineered-wood
  `EngineeredWood.Expressions.Predicate`. The superset-safety policy lives UPSTREAM in the shared C++
  `FilterSerializer`, gated on `string_order_pushable`: numeric/temporal/bool comparisons + string `=`/`IN`
  always push; string ordering (`<`/`>`/…) + string `BETWEEN` push only when the source is byte-ordered.
  `DeltaGlobalTableFunction.StringOrderPushable => true` (Parquet stats are byte-ordered, matching DuckDB's
  default binary string comparison), so ALL string comparisons push for Delta. (The SQL catalog scan shares
  the encoder and gets the same relaxation under a binary `_BIN2` collation — proven by `dm_exec` in
  `verify_collation_pushdown`.) `and` keeps pushable children, `or` is all-or-nothing; temporal/GUID/binary
  literals aren't pushed yet. The predicate
  drives BOTH the Delta file pruner (`ReadAllAsync(columns, filter)`) AND per-file Parquet row-group/stats
  pruning (set via `ParquetReadOptions.Filter` on the per-scan `DeltaTableOptions` — no engineered-wood change
  needed). engineered-wood never re-applies per row, and DuckDB re-applies above the scan, so the result is a
  correct superset. `test/verify_delta.test` (60 — incl. `=`/`IN`/`AND`-range pushed, and string `=`/`>`/`<>`
  correctly NOT pushed but still filtered by DuckDB).
- **Column projection into the Parquet read — still deferred**: engineered-wood's `ReadAllAsync(columns, …)`
  can read only the requested columns, but the shared `BindingBoundTable` wraps the result stream with the
  binding's FULL `OutputSchema`, so returning a projected column SUBSET mismatches the declared schema
  (arrow_ingest SIGSEGV). DuckDB still projects columns above the scan (by name), so this only forfeits the
  Parquet column-read I/O savings, never correctness. Doing it needs a pushdown-native `IBoundTable` that
  declares the projected schema (like the bespoke `SqlServerTableValuedFunction`) — a small follow-up.

## Next (a real lakehouse provider — not built)

- **More formats / a provider surface**: Iceberg/Lance/… via engineered-wood; promote `fabricator_delta_scan` to
  a provider-style `ATTACH`-able lakehouse catalog. The reverse-callback set is already general (open/read/
  size/close/glob).
- Watch: don't double-coalesce (httpfs vs engineered-wood's `CoalescingFileReader`); buffer-copy at the
  boundary is network-dominated but measure for many-small-metadata-read patterns.

## ABI

v40 appended `fs_spike` to the vtable + introduced `FabricatorHostServices` (passed to `Bootstrap.Initialize`,
which gained the `host` param). **v41** appended `delta_schema`/`delta_scan` to the vtable and `fs_glob` to
`FabricatorHostServices`. **v47** REMOVED `delta_schema`/`delta_scan` (delta became a connection-free global
host-FS `ITableFunction` on the v29 table session) and appended `set_active_opener` — a per-thread ambient
opener (mirroring `set_active_txn`) so a global host-FS reader resolves DuckDB secrets through the `fs_*`
callbacks. So `FabricatorHostServices` (fs_open_read/size/read/close/glob + host_query) is the reusable C#
host-IO foundation, and a new lakehouse format is now pure-C# (declare a global `ITableFunction`). See
docs/global-functions.md §"Host-FS global table functions".

**v48** added the **WRITE** surface to `FabricatorHostServices` — `fs_open_write`(exclusive)/`fs_write`/
`fs_close_write`/`fs_remove`/`fs_create_dir`(recursive) + the `FABRICATOR_ALREADY_EXISTS` status. The C#
`DuckDbTableFileSystem` write methods sit on these; the Delta commit's put-if-absent rides `EXCLUSIVE_CREATE`
(DuckDB `MoveFile` overwrites on local + is unimplemented on Azure DFS, so `RenameAsync` is emulated as
exclusive-create-copy → false on an existing target → `DeltaConflictException`). `HostFsGlob` normalizes an
object-store 404 (glob of a missing prefix) to empty. Validated end-to-end (write+read round-trip) on local +
a live OneLake lakehouse via `fabricator_delta_write_demo(path)` (`test/verify_delta_write.test`). The Delta
write-back foundation — see docs/delta-catalog.md (recommendation step 0).

**v49** appended `fs_remove_dir`(opener,path) — a RECURSIVE directory delete (DuckDB's
`FileSystem::RemoveDirectory`, idempotent). Powers Delta catalog **DROP TABLE** (`DeltaCatalog.DropTable` →
`HostFs.RemoveDir` deletes the table's whole `<root>/<table>/` folder). See docs/delta-catalog.md.

**v56** appended 3 vtable entries `onelake_open_write`/`onelake_write`/`onelake_close_write` — the WRITE forward
callbacks for the `onelake://` FileSystem (slice 2): sequential create→append→flush, so `COPY … TO 'onelake://…'`
(and any DuckDB writer) writes to OneLake through the managed Azure DataLake SDK. Read-side caching via DuckDB's
`ExternalFileCache` was confirmed already working on the native `read_parquet('onelake://…')` path (no wiring).

**v55** appended 5 vtable entries `onelake_open`/`onelake_read`/`onelake_close`/`onelake_glob`/`onelake_exists`
— the FORWARD callbacks (host C++ → managed) for the `onelake://` DuckDB FileSystem subsystem (Phase-3 step 3,
read-only). The C++ `FabricatorOneLakeFileSystem` (registered in the VFS at load) dispatches its reads to the
managed Azure DataLake SDK (`OneLakeForwardFs`), so DuckDB's native readers + `ExternalFileCache` use OneLake
uniformly, bypassing duckdb-azure. Credential = the azure secret the host resolves from the opener (fields as
JSON) → `FabricCredentialResolver`. See §3 above. (v51–v54 were the Delta partitioning / cluster-by / identity /
schema-mode entries, prior sessions.)

**v50** appended `fs_move_dir`(opener,src,dest) — a directory rename/move (DuckDB's `FileSystem::MoveFile`:
atomic on a local filesystem; object stores throw "not implemented"). Powers **local/S3 Delta catalog RENAME
TABLE** (`DeltaCatalog.AlterTable` RenameTable → `HostFs.MoveDir` moves the `<root>/<table>/` folder). OneLake
RENAME does NOT use this (Azure `MoveFile` is unimplemented) — it renames via the DFS SDK
(`DataLakeDirectoryClient.RenameAsync`) directly, like the OneLake DROP. See docs/delta-catalog.md.

> **Azure-DFS gap — `fs_remove_dir` does NOT work on OneLake/ADLS** (so OneLake Delta DROP bypasses the host FS):
> `FileSystem::RemoveDirectory` throws `AzureDfsStorageFileSystem: RemoveDirectory is not implemented!` (duckdb-azure
> has no recursive delete on the DFS endpoint), and the glob-files-then-`fs_remove` fallback is dead — `fs_glob`
> hits the **same duckdb-azure mid-path-wildcard bug** (PR #174, `type must be string, but is null`), so azure
> `glob()` returns 0 rows at every OneLake level. **Resolution (DONE, validated live 2026-06-30):** the Delta
> catalog talks to the **OneLake DFS endpoint directly via the Azure SDK** (`Azure.Storage.Files.DataLake`,
> `FabricLakehouse`) — `GetPaths` for table discovery and `DataLakeDirectoryClient.DeleteIfExistsAsync` for DROP —
> bypassing both DuckDB's azure FileSystem and the Fabric `ListTables` REST API (which 400s on schema-enabled
> lakehouses). `DeltaCatalog.DropTable`/`DiscoverTables` branch on `FabricLakehouse.IsOneLake`; local/S3 keep the
> host-FS `fs_remove_dir` + glob. **Use the ASYNC Azure APIs** (`GetPathsAsync`/`DeleteIfExistsAsync`): the SYNC
> `GetPaths`/`DeleteIfExists` use `HttpClient.Send`, whose sync transport hangs under the hostfxr-hosted CLR (a
> single discovery never returns; ~1s in a console host) — the async path uses `SendAsync` and works, like every
> other Bridge IO path. Note `fs_remove` (single file) DOES work on Azure DFS — only the recursive directory delete
> and recursive glob through duckdb-azure are missing, which the direct DFS SDK sidesteps.

## OneLake filesystem + unified Fabric credential (design, DEFERRED — nothing built)

> Motivation (2026-07-02): the duckdb-azure `abfss://` gaps (recursive glob PR #174, `RemoveDirectory`/`MoveFile`
> unimplemented on the DFS endpoint) are a recurring source of OneLake workarounds. Rather than keep working
> *around* duckdb-azure per-operation, build a **C# OneLake filesystem** on the Azure SDK (the extension already
> hosts C# and already uses `Azure.Storage.Files.DataLake` in `FabricLakehouse` for discovery + DROP) — bypassing
> duckdb-azure for everything C# touches. Requirement: the extension + providers must run **both locally and inside
> a Fabric Python notebook**, with **DAX + SQL + OneLake/Lakehouse** all authenticating seamlessly (SP locally;
> managed/workspace identity in Fabric).

**Blueprint checked, not copied — `D:\repos\duckdb_onelake`** (C++/Rust, uses delta-kernel-rs; appears unmaintained)
**does NOT fix the filesystem** — it reads OneLake through DuckDB's `httpfs`/`delta` against the DFS endpoint (hence
its fragile `set azure_transport_option_type='curl'` + `CURL_CA_PATH` requirement) with a bearer token. It leans on
duckdb-azure exactly where we want to escape it. Its value is the **secret/auth layering**: a dedicated `TYPE ONELAKE`
secret for the **Fabric REST API** (workspace/lakehouse resolution) alongside a `TYPE azure` secret for **storage**,
with auth modes SP / workspace managed identity / `credential_chain` (`env`/`cli`). So: good model for secrets, no
model for the filesystem.

**Filesystem ↔ secret relationship (the key clarification).** DuckDB natively binds a FileSystem to secrets
*implicitly* via the `FileOpener`/`SecretManager` (opening `abfss://…` scope-matches an `azure` secret). In our
C# model there is **no implicit binding** — we resolve a credential **once at ATTACH/catalog-open and hand it
explicitly** to the filesystem instance (as we already do for DAX `DaxTokenAuth`, SQL Entra tokens, and OneLake
`FabricLakehouse`). The credential is a constructor argument, not a scope lookup. Simpler and fully under our control.

### The four pieces (sequenced)

1. **`FabricCredentialResolver` — DONE (2026-07-02).** One shared C# `TokenCredential` in the Bridge
   (`dotnet/Fabricator.Bridge/FabricCredentialResolver.cs`, `public static`): `azure` SP secret present ⇒
   `ClientSecretCredential` (local / CI); `managed_identity` ⇒ `ManagedIdentityCredential`; else ⇒
   `DefaultAzureCredential` (Fabric: managed / workspace identity via the MSI endpoint; local: az CLI / env / VS).
   Scope constants `PowerBiScope` / `StorageScope` / `SqlScope` + `Resolve(fields)` / `MintCredential(strings)` /
   `ResolveForRemoteTarget(connstr)` / `GetToken`(Async). The prior duplicates now delegate: `DaxTokenAuth`
   (`Resolve` + `DefaultCredentialForTarget` + `GetToken`, PowerBiScope aliased), `FabricLakehouse.BuildCredential` +
   `MintCredential` + the storage scope. SQL keeps SqlClient's native connstr-level Entra auth (consumes the same
   secret *fields*, not a `TokenCredential`) — deliberately unchanged. Behavior-preserving for the SP + no-secret
   cases; now also handles `managed_identity` consistently for OneLake (previously only DAX did). Builds across all
   three assemblies; local Delta suite unregressed; DAX loads cleanly. **Caveat (unchanged):** the MSI/workspace-
   identity path must still be **verified in a live Fabric notebook** — `DefaultAzureCredential` should pick up the
   MSI endpoint, but that is assumption, not yet tested. This is the shared entry point step 2's OneLake FS consumes.
2. **`AdlsGen2TableFileSystem : ITableFileSystem` — DONE + LIVE-VALIDATED on Fabric (2026-07-02).** A full
   read/write filesystem on `Azure.Storage.Files.DataLake`
   (`dotnet/Fabricator.Bridge/AdlsGen2TableFileSystem.cs`; named `OneLakeDataLakeFileSystem` until 2026-08-02,
   when plain ADLS Gen2 accounts started routing through it too — it had always parsed its endpoint host out
   of the `abfss://` path, so only the SELECTOR was OneLake-specific):
   `ListAsync` (GetPaths, 404→empty), `OpenReadAsync`/`ReadAllBytesAsync` (range GET + OpenRead), `CreateAsync`
   (put-if-absent via `DataLakePathCreateOptions.Conditions IfNoneMatch=*`), **`RenameAsync` = a TRUE atomic ADLS
   rename** (`DataLakeFileClient.RenameAsync` with `IfNoneMatch=*` → 409/412 ⇒ false = the Delta commit-conflict
   signal, replacing the host-FS copy+exclusive-create emulation), `DeleteAsync`/`ExistsAsync`/`WriteAllBytesAsync`,
   + `OneLakeRandomAccessFile` / `OneLakeSequentialFile` (append+flush). All ASYNC (sync DataLake hangs under the
   hostfxr CLR). Selected by `TableFileSystems.Create(opener, path)`: OneLake root AND a Fabric credential in scope
   (`AmbientOneLakeCredential`, [ThreadStatic], mirroring `AmbientOpener`) ⇒ this FS; else `DuckDbTableFileSystem`.
   `DeltaCatalog.Opener()` publishes `_fabricCredential` to the ambient wherever it fetches the opener (incl. the bulk
   consumer thread — the FS is constructed synchronously there, capturing the credential before any async hop); the
   connection-free global `fabricator_delta_*` functions clear the ambient (host-FS path). The 16
   `new DuckDbTableFileSystem(...)` sites now route through the factory; `DeltaReader` helpers widened to
   `ITableFileSystem`. **Local regression green** (9 Delta suites, 412 assertions — the local suites exercise the
   credential-null fallback to `DuckDbTableFileSystem`). **Live-validated on Fabric** (workspace `Test`, flat
   lakehouse `LH_no_schema`, `SECRET fabric_sp`): ATTACH → CTAS (count = 3) → `DELETE WHERE id=2` (→ 1,3, copy-on-write
   + atomic-rename commit) → DROP, all through the direct DataLake SDK (no duckdb-azure). **Bug found + fixed during
   validation:** `ListAsync("_delta_log/")` trimmed the trailing slash, treating the directory prefix as a file
   prefix in the table root → listed the root non-recursively → returned only the `_delta_log` *directory* entry
   (skipped) = empty log → the read-back saw no commits → `SnapshotBuilder` "Table has no metadata action". Fixed by
   keeping the trailing slash when computing the listing directory. **Known OneLake characteristic (not a bug):** the
   very first read immediately after the first write in a session can transiently miss the just-committed
   `_delta_log/…json` (DFS `GetPaths` listing lag) → a stale/empty read that resolves within ~1-2s; observed once,
   did not reproduce. A future mitigation could trust the writer's known committed version instead of re-listing.
3. **Register the C# FS as a DuckDB `onelake://` subsystem (forward callbacks) — SLICE 1 (read-only) DONE +
   LIVE-VALIDATED on Fabric (2026-07-03, ABI v55).** A C++ `FabricatorOneLakeFileSystem : FileSystem`
   (`src/fabricator/fabricator_onelake_fs.{hpp,cpp}`) is registered in DuckDB's VFS at extension load
   (`RegisterOneLakeFileSystem`, `CanHandleFile` = the `onelake://` scheme). Its read ops forward C++→C# via 5 new
   vtable entries (`onelake_open`/`onelake_read`/`onelake_close`/`onelake_glob`/`onelake_exists`) to
   `OneLakeForwardFs` (the managed Azure DataLake SDK, reusing step 2's logic). **The credential** is resolved
   C++-side from the calling opener: `SecretManager::LookupSecret(path,"azure")`, falling back to ANY registered
   `azure` secret (OneLake `onelake://` paths don't match an azure secret's default scopes) → the fields as JSON →
   C# `FabricCredentialResolver` (empty ⇒ `DefaultAzureCredential`). So this is exactly the C#→C++→C# path: the
   OneLake IO logic is C#, registered as a C++ FileSystem, reached back in C#. **The charm (key upside): NOT
   Delta-specific — `onelake://` lifts EVERY DuckDB reader onto OneLake**, and (unlike step 2's standalone FS) it
   is behind the VFS so DuckDB's native parquet reader + `ExternalFileCache` use it. **Validated live:** CTAS a Delta
   table via the catalog, then `SELECT count(*) FROM read_parquet('onelake://Test/LH_no_schema.Lakehouse/Tables/t/
   *.parquet')` = 3, rows correct — read through DuckDB's NATIVE parquet reader over the subsystem, no duckdb-azure.
   **Bugs found + fixed during validation:** (a) `OneLakeForwardFs.Glob` blocked the AsyncPageable enumerator
   per-item (`MoveNextAsync().GetAwaiter().GetResult()`) → `NotSupportedException` under the hostfxr CLR — fixed to
   `await foreach` in an async method blocked once at the top (the FabricLakehouse pattern); (b) glob matched only
   the before-`*` prefix → pulled in `_delta_log/*.json` (read as parquet → "no magic bytes") — fixed to enforce
   the after-`*` suffix + a single-`*`-doesn't-cross-`/` rule; (c) the parquet reader calls
   `GetLastModifiedTime` → added (returns a constant, correct since Delta data files are immutable).
   **Caching CONFIRMED (the core motivation):** the native `read_parquet('onelake://…')` path already flows
   through DuckDB's `ExternalFileCache` transparently — `duckdb_external_file_cache()` shows the `onelake://…`
   parquet cached after a read, and a second read hits it. So step 3's caching win is delivered by slice 1 for the
   native reader (no extra wiring). **Slice 2 — WRITE + general readers DONE + live-validated (2026-07-03, ABI
   v56):** 3 more forward entries (`onelake_open_write`/`onelake_write`/`onelake_close_write`) + the C++ FS
   `OpenFile(write)`/`Write` (sequential append; a non-sequential write throws — Azure DFS + COPY are sequential)
   → `COPY (…) TO 'onelake://…/Files/x.parquet'` writes via DuckDB's native parquet writer + `OneLakeForwardFs`
   (create → append → flush at the final length), read back correct (5/5 rows live on Fabric). `read_csv`/
   `read_json` READ already worked via the slice-1 OpenFile/Read path (same VFS dispatch) — so **any DuckDB
   reader/writer works on OneLake** now (the "Excel/JSON/CSV on the same path" charm). **Still deferred:** caching
   engineered-wood's own reverse `fs_*` reads (route the Delta catalog through `onelake://` — low value since the
   catalog's heavy reads are the native reader's and the log reads are small); non-sequential writes; directory
   ops (CreateDirectory/RemoveFile throw). Cost noted: per-range C++↔C# marshaling (cache-mitigated).
4. **(optional, UX) a dedicated `TYPE fabric` secret (small).** Via the provider-declared-secret machinery (ABI v38),
   modeling auth intent explicitly (`AUTH 'service_principal' | 'managed_identity' | 'workspace_identity' | 'cli' |
   'default'`). Functionally redundant over "azure secret + `DefaultAzureCredential` fallback" (which already covers
   local SP + Fabric managed identity) — pure clarity. Reuse the `azure` secret now; add `fabric` only if the UX
   warrants it.

### Assessment / sequencing

Steps **1 + 2** are clearly worthwhile and architecturally clean: they make us **independent of duckdb-azure's OneLake
maturity for the entire C# path**, reusing what `FabricLakehouse` already proves (async DataLake SDK, SP credential
minting). Step **3** is the strategic multiplier — a single `onelake://` DuckDB FileSystem serves *all* formats
(Delta + Excel/JSON/CSV/parquet + `COPY`) and is what the native reader needs — but it is the largest lift and is
gated on adopting the native Multifile-Delta model. Step **4** is optional polish. **Nothing built** — design note only;
`FabricLakehouse` (direct DFS SDK for discovery + DROP) is the working precedent to generalize.
