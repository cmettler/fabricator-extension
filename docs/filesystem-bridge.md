# Host FileSystem bridge — reverse callbacks (SPIKE / foundation)

> Status: **spike, validated** (ABI v40). Proves a managed component can do **secret-backed remote IO via
> DuckDB's `FileSystem`** — the foundation for a future C# lakehouse-format provider (e.g. Curt Hagenlocher's
> *engineered-wood*: Delta/Iceberg/Lance/ORC/Avro readers in pure C#) that reuses DuckDB's filesystem +
> secrets instead of shipping its own cloud SDK filesystems. Not a user feature yet.

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

## Next (a real lakehouse provider — not built)

- Promote the spike surface to a stable `IArrowFileSystem` host-callback set (open/read/size/glob/exists/
  delete/move) — Glob → `ITableFileSystem.ListAsync`, etc. (the 1:1 mapping in the discussion).
- A C# `DuckDbTableFileSystem : ITableFileSystem` delegating to those callbacks.
- An `arrownet` lakehouse provider: a table function that reads Delta/Iceberg/Lance/… via engineered-wood,
  IO through DuckDB's FileSystem, results as Arrow → DuckDB. Start with **Delta** as the first format.
- Watch: don't double-coalesce (httpfs vs engineered-wood's `CoalescingFileReader`); buffer-copy at the
  boundary is network-dominated but measure for many-small-metadata-read patterns.

## ABI

v40 appended `fs_spike` to the vtable + introduced `ArrowNetHostServices` (passed to `Bootstrap.Initialize`,
which gained the `host` param). Both are spike surface; the host-services struct is the reusable foundation.
