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
provides `ArrowNetHostServices` (in `abi.h`) — a struct of function pointers the **managed side calls** to
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
host holds a context (here: an `arrownet_fs_spike(path)` table-function execution).

## What the spike proves (validated)

`arrownet_fs_spike(path)` (table function) → hands the managed side the `ClientContext` → C# opens the path
via the host callbacks, reads head + tail bytes + size, returns a string:

- **Local parquet:** `ok size=41128 head='PAR1' tail='PAR1'` — reverse callback + offset footer read.
- **Remote `https://` via httpfs:** `ok size=1842 head='…' tail='…'` — a real remote FileSystem, a **range
  GET at `size-4`**, with the opener auto-pushed (httpfs used it for the request).

So: C# → DuckDB `FileSystem` → offset reads, both local and remote; the opener/secret channel works. For
`az://`/`s3://` + a DuckDB secret it is the *same* path (httpfs/azure resolves the secret off the auto-pushed
opener — and azure-secret resolution itself is already validated, see provider-extensibility.md §2.1). The
only piece not directly smoke-tested is a live `az://` blob (no blob store + file handy), but it shares the
proven code path.

> **Update (ABI v47): `arrownet_delta_scan` is now a connection-free GLOBAL host-FS table function, not a
> bespoke C++ table function.** The bespoke `arrownet_delta.cpp` + the `delta_schema`/`delta_scan` vtable entries
> were removed; delta is a pure-C# global `ITableFunction` (`DeltaGlobalTableFunction`, declared in
> `CustomFunctions.GlobalTable`) dispatched through the v29 table session (`table_bind`/`table_execute`). The
> only new plumbing is the opener: a global host-FS reader needs the calling operator's `ClientContext` (for
> DuckDB secret resolution) at bind + execute, threaded via a per-thread ambient (`AmbientOpener`, mirroring
> `set_active_txn`) the host sets with the appended `set_active_opener` ABI entry. So a NEW lakehouse format
> (Iceberg/Lance/…) is now added with **zero C++** — see docs/global-functions.md §"Host-FS global table
> functions". The DeltaReader / DuckDbTableFileSystem C# below are unchanged; only the dispatch moved. The
> sections below describe the original (now-superseded) bespoke wiring.

## Delta reader on the bridge — `arrownet_delta_scan(path)` (BUILT, validated; now a global host-FS fn)

`arrownet_delta_scan('<delta table root>')` reads a Delta Lake table via engineered-wood, with **all IO going
through DuckDB's FileSystem** over the host callbacks — so local, `az://`, `s3://`, `https://` paths and DuckDB
secrets all work, one auth config shared with native reads.

- **`HostFs.fs_glob`** added to `ArrowNetHostServices` (DuckDB `FileSystem::Glob` → JSON `[{path,size}]`) — the
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
- **C++ `arrownet_delta_scan`** (`arrownet_delta.cpp`): bind → `DeltaSchema` → `ReadArrowSchema` → return
  types; init_global → `DeltaScan` → `ArrowStreamReader`; scan → `Read()` per chunk. DuckDB applies
  projection/filter/aggregation above the scan.
- **engineered-wood patch (local clone):** `ActionSerializer` read optional `add`/`remove` numeric fields
  (`baseRowId`, `defaultRowCommitVersion`, remove `size`/`deletionTimestamp`) with a bare `GetInt64()`, which
  throws on delta-rs's explicit `"field":null` (engineered-wood's own writer omits them). Guarded with
  `TokenType == Null ? null : GetInt64()` — an upstream-worthy robustness fix for reading delta-rs tables.

**Validated** (`test/verify_delta.test`, 39 assertions; fixture `test/fixtures/delta_simple`, a delta-rs table
of 10 rows id/name/amount): full scan with correct bind-time types, filter+aggregate pushed by DuckDB above
the scan, `DESCRIBE` schema, and joins against a values table — all green. The Apache.Arrow version is aligned
(engineered-wood + the bridge both **23.0.0**, both net10.0).

## Next (a real lakehouse provider — not built)

- **Streaming** (not materialized): `delta_scan` currently buffers the whole table; wrap `ReadAllAsync` as a
  lazy `IArrowArrayStream` whose `get_next` pulls a batch at a time. The opener must then stay valid across
  the scan (it does — the ClientContext lives for the whole table-function execution).
- **Predicate / projection pushdown into Delta** (file skipping): engineered-wood's `ReadAllAsync(columns,
  Predicate)` prunes files by partition values + column stats. Forward the scan's `spec_json`/`filter_values`
  (as the TVF path already does) into a Delta `Predicate` + column list.
- **More formats / a provider surface**: Iceberg/Lance/… via engineered-wood; promote `arrownet_delta_scan` to
  a provider-style `ATTACH`-able lakehouse catalog. The reverse-callback set is already general (open/read/
  size/close/glob).
- Watch: don't double-coalesce (httpfs vs engineered-wood's `CoalescingFileReader`); buffer-copy at the
  boundary is network-dominated but measure for many-small-metadata-read patterns.

## ABI

v40 appended `fs_spike` to the vtable + introduced `ArrowNetHostServices` (passed to `Bootstrap.Initialize`,
which gained the `host` param). **v41** appended `delta_schema`/`delta_scan` to the vtable and `fs_glob` to
`ArrowNetHostServices`. **v47** REMOVED `delta_schema`/`delta_scan` (delta became a connection-free global
host-FS `ITableFunction` on the v29 table session) and appended `set_active_opener` — a per-thread ambient
opener (mirroring `set_active_txn`) so a global host-FS reader resolves DuckDB secrets through the `fs_*`
callbacks. So `ArrowNetHostServices` (fs_open_read/size/read/close/glob + host_query) is the reusable C#
host-IO foundation, and a new lakehouse format is now pure-C# (declare a global `ITableFunction`). See
docs/global-functions.md §"Host-FS global table functions".
