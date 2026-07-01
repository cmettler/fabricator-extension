# delta-rs provider (design note — NOT built)

A second Delta Lake provider backed by **delta-rs** (the reference Rust implementation) via the
**delta-dotnet** binding (`delta-incubator/delta-dotnet`, a C# wrapper over delta-rs + delta-kernel-rs FFI),
as an alternative to the pure-C# **engineered-wood** provider (`engineeredwooddelta`, the current default —
see [delta-catalog.md](delta-catalog.md)). This is a design + feasibility note; nothing is built.

Both coexist as `IBackend` providers behind the same C++ core: `engineeredwooddelta` (default, pure C#,
full DuckDB-native rowid DML) and `deltars` (opt-in, production-grade reads/writes/maintenance + MERGE).

## Implementation status — v1 BUILT + verified (local FS)

A working `deltars` provider is implemented in the new `dotnet/ArrowNet.DeltaRs` project (`DeltaRsBackend` +
`DeltaRsCatalog` + `StorageOptionsCodec`), registered in `BackendRegistry` (default list; skipped if the
assembly/native libs aren't published) and published opt-in via `publish-managed.ps1 -IncludeDeltaRs` (brings
`DeltaLake.dll` + the two native Rust DLLs). **No ABI/C++ change** — pure-C# sibling provider. Verified end to
end on Windows via `test/verify_delta_rs.test` (25 assertions) and a live shell smoke:

- **Working**: `ATTACH … (TYPE arrownet, PROVIDER 'deltars')` (+ alias `'delta-rs'`); discovery (local FS);
  metadata (schemas/tables/columns); **scan** (read the whole table into an owned `Apache.Arrow.Table` via
  `ReadAsArrowTableAsync`, streamed as batches — DuckDB projects/filters above); **CREATE/CTAS/INSERT**
  (append + overwrite via `CreateTableAsync`/`InsertAsync`); **DELETE + UPDATE** (rowid → record-batch MERGE,
  see below); **snapshots** (`arrownet_delta_snapshots` → `HistoryAsync`, real commit history:
  `CREATE TABLE`/`WRITE`/`MERGE`/…); re-attach durability. `test/verify_delta_rs.test` (51). No regression to
  the engineered-wood provider (its full suite stays green).
- **DELETE / UPDATE now work — rowid mapped to a record-batch MERGE** (the crux, solved). The provider
  advertises **all columns as the rowid** (a full-row identity), so DuckDB's rowid-based `PlanDelete`/
  `PlanUpdate` hand `ExecuteDelete`/`ExecuteUpdate` the scanned rows; the catalog builds a delta-rs MERGE
  matching those rows on **every column, NULL-safe** (`(t.c = s.c) OR (t.c IS NULL AND s.c IS NULL)`): DELETE
  = `WHEN MATCHED THEN DELETE`; UPDATE = `WHEN MATCHED THEN UPDATE SET …` with the source columns renamed
  (`s__`/`k__`) to separate the SET values from the match keys. **Sound because a WHERE cannot distinguish
  identical rows**, so DuckDB's rowid set is always a complete equivalence class and a full-row match deletes/
  updates exactly it. `target`/`source` are arbitrary MERGE aliases (delta-rs parses the SQL). Verified:
  DELETE, UPDATE (incl. an expression `id = id + 10`), DELETE∘UPDATE composition, re-attach durability.
  *Caveat*: a duplicated pre-image row could make delta-rs reject an UPDATE as an ambiguous multi-match
  (identical rows can't be selectively updated by a WHERE anyway). This is the record-batch-MERGE mapping the
  design anticipated; a future DuckDB "remote MERGE" optimizer step could push a MERGE statement directly.
- **Deferred with a clean error** (delta-dotnet limitations found while building):
  - **Time travel** — delta-dotnet reads only the *latest* snapshot via the Delta Kernel; **any versioned
    load disables the kernel** (`Table.LoadVersionAsync` sets `isKernelSupported=false`) and there is no
    non-kernel read path, so `ReadAsArrowTableAsync` at a version throws *"not supported without using the
    Delta Kernel."* `AT (VERSION/TIMESTAMP …)` therefore returns a clean "not supported (v1)" error, not
    silent latest-data. Needs the kernel wired to snapshot at a version (or a delta-rs read-at-version) — a
    delta-dotnet patch, like the ones we make to engineered-wood.
  - **ALTER** — delta-dotnet has no schema-DDL API.
- **Not yet wired** (design is below): cloud discovery (v1 lists local roots only; `storage_options` mapping
  is coded in `StorageOptionsCodec` but unproven), SQL/filter pushdown (scan is a full read), maintenance
  functions (OPTIMIZE/VACUUM/CHECKPOINT), CDF read (`QueryTableChangesAsync` is wired but untested), MERGE.
- **Scan schema note**: the scan uses `ReadAsArrowTableAsync` (schema == the bound `table.Schema()`), NOT
  DataFusion `QueryAsync` "SELECT *" — the latter's output schema diverged from the bound schema and
  **SIGSEGV'd `arrow_ingest`**. Materialize-and-serve is correct-first; streaming + pushdown is the follow-up.

## Build feasibility — CONFIRMED on Windows (no WSL)

delta-dotnet's `.sh` scripts are only prerequisite-installers; the build is driven by MSBuild targets in
`src/DeltaLake/DeltaLake.csproj` that invoke `cargo`. Verified building `D:\repos\delta-dotnet` on Windows:

- **Toolchain**: Rust 1.96 (`x86_64-pc-windows-msvc`), .NET 9/10 SDKs, VS18 MSVC. rustup's msvc toolchain
  links without a vcvars shell.
- **Clean deps**: the whole stack is **rustls** (no `openssl-sys` in the tree — the usual Windows blocker is
  absent) and needs **no `protoc`** (only the `prost` runtime, pre-generated protos). `clang` is only needed
  to *regenerate* the P/Invoke bindings (committed), not to build.
- **One gotcha**: the `delta-kernel-rs` submodule was stale at `v0.11.0`; the pin file wants `v0.23.0` (and the
  csproj builds it with `--features arrow-58`, which the old kernel lacks). `git submodule update --init` fixes
  it.
- **Build**: `dotnet build src/DeltaLake/DeltaLake.csproj -c Debug -f net9.0` (the `-f net9.0` sidesteps the
  project's `net472;net8.0` TFMs whose runtimes aren't installed). MSBuild runs `cargo build` for both crates
  and copies the DLLs: `delta_kernel_ffi.dll` (~63 MB, rustls+arrow-58, ~3 min) and `delta_rs_bridge.dll`
  (~179 MB, full `deltalake`+`datafusion`+azure/gcs/s3, ~5 min).
- **Functional smoke** (net9.0 console referencing the built lib): local create → insert 3 rows → read back =
  `Table: 2 columns by 3 rows`. The native FFI loads and round-trips on Windows.
- **Arrow 23.0.0** — same as our Bridge + engineered-wood, so the C-data-interface handoff aligns (no Arrow
  version conflict).

## Why delta-rs is the better reader/writer/maintenance engine

Not a sales pitch — the concrete reasons, with the honest counterweight after.

1. **It's the reference implementation.** delta-rs is delta-io's own Rust engine (same org as the spec), the
   core under Python `deltalake`, production-proven at scale. engineered-wood is a from-scratch pure-C#
   implementation we've had to **patch repeatedly** (this session alone: roaring-bitmap DV byte format,
   `metaData.format.options`/`configuration`, parquet `path_in_schema`, `Time32`→`Time64`, decimal
   `Decimal128` mapping, the `TakeRows` survivor filter). That patch cadence *is* the maturity gap.
2. **Standard-compliance by construction.** We spent real effort making engineered-wood output readable by
   delta-kernel/Spark/Fabric (parquet footer fields, DV format, protocol feature declarations, the OneLake
   converter rejecting our DV/writer-v7 tables). delta-rs writes are what Databricks/Fabric consume in
   production — those interop battles largely evaporate.
3. **Maintenance operations engineered-wood does not have:** OPTIMIZE (bin-pack small files — essential after
   incremental/streaming writes), **Z-ORDER** (multi-dimensional data skipping), VACUUM (retention-bounded GC
   of tombstoned files), **CHECKPOINT** (log compaction — without it, log replay degrades as versions
   accumulate), RESTORE. Each would be a substantial engineered-wood build.
4. **DataFusion-grade read pushdown** — predicate + projection → Delta file/stats skipping + parquet
   row-group/page pruning, mature and well-optimized. Ours is hand-rolled and narrower.
5. **`object_store`** — battle-tested S3/Azure/GCS/ADLS with retries, multipart, credential chains. We hit
   OneLake glob-recursion + DFS-sync-lag quirks and had to hand-roll `FabricLakehouse` DFS discovery;
   object_store absorbs those cloud quirks natively.
6. **Hard features done right:** correct deletion vectors, column mapping, timestamp_ntz, invariants, and
   **MERGE** (engineered-wood has none).

**Honest counterweight — why engineered-wood stays the default:** it is tiny pure C# (no Rust build, no 240 MB
native payload), we *control* it (we fixed its bugs at the source this session), and — decisively — it **fits
DuckDB's rowid DML** because it can perform a low-level copy-on-write file rewrite. delta-rs cannot (see "The
DML crux"), so "better engine" ≠ "better fit for our DML plumbing."

## What delta-dotnet is (shapes the whole integration)

`IEngine` + `ITable` — **single-table, no catalog concept**: you `LoadTableAsync(path, storageOptions)` per
table. It does its **own IO** via delta-rs's `object_store` (native azure/gcs/s3/adls), with credentials passed
as a `Dictionary<string,string>` (`aws_access_key_id`/`aws_secret_access_key`/`aws_default_region`;
`account_name`/`account_key`/`bearer_token`; `service_account_key`; …). Reads/DML/MERGE are
predicate/SQL-string based (DataFusion). Native libs total **~240 MB**.

## Where it plugs in — a pure-C# sibling provider, NO ABI/C++ change

Exactly like today's `DeltaCatalog`: a `DeltaRsBackend : IBackend` (name `deltars`, aliases e.g. `delta-rs`) +
`DeltaRsCatalog : IBackendCatalog`, reusing the entire provider-agnostic C++ core, the rowid/DML operators, the
v29 table session, and the snapshots/changes metadata kinds. Registered in `BackendRegistry.Discover` beside
the SqlServer/DAX/engineered-wood backends. `publish-managed.ps1` gains the `DeltaLake.Net` project reference +
its two native DLLs (the +240 MB cost). A net10 host referencing the net9.0 assembly is forward-compatible.

## Three divergences from the engineered-wood provider

1. **IO/credentials = `storage_options`, NOT the host-FS bridge.** delta-rs reads/writes cloud storage itself,
   so table IO **bypasses** the `fs_*` reverse callbacks and DuckDB secrets. `BuildConnectionString(secretType,
   fields, base)` translates the ATTACH'd secret into object_store keys — e.g. an `azure` service-principal
   secret → `azure_storage_account_name`/`azure_client_id`/`azure_client_secret`/`azure_tenant_id`; an `s3`
   secret → `aws_*`. **But discovery still needs a directory lister** (delta-dotnet has no "list tables in a
   root" — it's single-table), so `DeltaRsCatalog` reuses our existing `FabricLakehouse` DFS discovery / host-FS
   glob to find `_delta_log` subdirs, then opens each via `LoadTableAsync`. → **two credential paths**
   (discovery via our machinery vs table IO via object_store) — the main wrinkle.
2. **DML model mismatch — the crux (below).**
3. **Packaging: +240 MB native + a Rust build** added to `publish-managed.ps1`, vs engineered-wood being tiny
   pure C#.

## Capability mapping (`IBackendCatalog` → delta-dotnet)

| `IBackendCatalog` method | delta-dotnet API | Fit |
|---|---|---|
| `GetMetadata(Schemas/Tables)` | *our* discovery (glob `_delta_log` / DFS) | reuse existing — delta-dotnet has no catalog API |
| `GetMetadata(Columns)` / schema | `ITable.Schema()` → `Apache.Arrow.Schema` | ✅ clean |
| `ScanTable(specJson, filterValues)` | `QueryAsync(SelectQuery "SELECT <cols> FROM t WHERE <pred>")` → `IAsyncEnumerable<RecordBatch>` | ✅ streaming + **file/stats-skipping pushdown** via DataFusion; translate our `FilterNode`→SQL WHERE. (`ReadAsArrowTableAsync` materializes the whole table — only for small/whole reads) |
| `BulkInsert` (create/replace/append) | `CreateTableAsync` + `InsertAsync(SaveMode.Append/Overwrite)`; `InsertAsync(IArrowArrayStream)` streams; `MaxRowsPerGroup` tuning | ✅ clean; **standard-compliant writes** (kills the Fabric/Spark read-compat battles) |
| time travel (`spec.At`) | `LoadTableAsync{Version=n}` / `LoadDateTimeAsync(ts)` | ✅ native **version and timestamp** |
| `arrownet_delta_snapshots` | `HistoryAsync(limit)` → `CommitInfo[]` (operation, params, timestamp, isolation, blind-append) | ✅ richer than ours |
| `arrownet_delta_changes` (CDF) | `QueryTableChangesAsync(start, end)` → `_change_type`/`_commit_version`/`_commit_timestamp` | ✅ native |
| **`ExecuteDelete(keys)`** | `DeleteAsync(predicate)` — no rowid/position API | ⚠️ **mismatch — see below** |
| **`ExecuteUpdate(setCols, data)`** | `UpdateAsync(sqlString)` — no rowid/position API | ⚠️ **mismatch** |
| `AlterTable(AddColumn)` | *no add-column API* (only `InsertOptions.OverwriteSchema` on a write) | ❌ **regresses** vs engineered-wood's `AddColumnAsync` |
| `CreateSchema`/`DropSchema` | *our* directory layout (delta-dotnet is single-table) | reuse existing |
| `DropTable` | *our* recursive dir delete (`fs_remove_dir` / DFS) | reuse existing |
| — **new capabilities** | `VacuumAsync`, `OptimizeAsync` (BinPack / **ZOrder**), `CheckpointAsync`, `RestoreAsync`, `AddConstraintsAsync` (CHECK), `MergeAsync` | ✨ expose as functions (`arrownet_delta_optimize(...)`, `_vacuum`, `_checkpoint`, `_merge`) — the catalog table-function machinery already exists |

## The DML crux (the hard part)

delta-dotnet's DML is **predicate/SQL-based** (`DeleteAsync("id > 100")`, `UpdateAsync("UPDATE t SET …")`, full
`MERGE`) and exposes **no low-level remove/position API** (`CreateWriteTransactionAsync` takes `AddAction[]` —
append-only; there is no `RemoveAction`). But DuckDB's `PlanDelete`/`PlanUpdate` on a custom catalog is
**rowid-based and never hands us the WHERE** (the filter is consumed by the scan below — the exact wall
documented for engineered-wood in [delta-catalog.md](delta-catalog.md)). Therefore:

- engineered-wood works because **it can do a low-level copy-on-write file rewrite** (its own `remove`+`add`
  commit driven by a transient `(file, position)` rowid). delta-dotnet **cannot** — no low-level remove.
- delta-rs's *nicer* predicate DML doesn't help, because **DuckDB won't give us the predicate**.

So `DELETE FROM lake.t WHERE …` / `UPDATE …` through DuckDB syntax has three honest options, none free:

- **(a) reconstruct a predicate** from the scanned key rows → `WHERE (k1,k2) IN ((…),(…))` → `DeleteAsync`.
  Works only when the table has a real unique key (Fabric/Delta tables often don't). Fragile.
- **(b) passthrough functions** — expose delete/update/merge as explicit functions
  (`arrownet_delta_merge(...)`, a predicate-delete function) that call delta-rs directly. Full power, but not
  DuckDB `DELETE`/`UPDATE` syntax.
- **(c) defer UPDATE/DELETE** in v1 (read + append/overwrite + maintenance only) and route rowid DML to the
  `engineeredwooddelta` provider.

## Notable delta-dotnet gaps (from the API + test sweep)

- No add-column / schema-merge DDL (only `InsertOptions.OverwriteSchema`).
- No affected-row counts from UPDATE/DELETE/MERGE (stats live in commit history JSON).
- No projection/filter pushdown *except* via DataFusion SQL (so scans go through `QueryAsync`, not the
  no-pushdown `ReadAsArrowTableAsync`).
- Column mapping / deletion-vector / liquid-clustering tables exist as fixtures but are **excluded from the
  tested set** — treat as unverified.
- Tests cover local/`memory://`/Azure (`bearer_token`); **no S3/MinIO test coverage** (works, but unproven
  here).

## Recommended slicing

1. **`deltars` read provider** — discovery (reuse ours) + `ScanTable` via `QueryAsync` (pushdown) + time travel
   + `HistoryAsync` snapshots + CDF. Pure win, low risk.
2. **Append/overwrite writes** — `CreateTableAsync` + `InsertAsync`; standard-compliant, Fabric-safe by
   construction.
3. **Maintenance functions** — `arrownet_delta_optimize`/`_vacuum`/`_checkpoint`/`_zorder` (catalog-bound or
   global table functions; the machinery exists).
4. **DML** — passthrough functions first (`_delta_merge`, predicate delete); attempt rowid-parity via
   key-reconstruction only when a unique key exists.

## Skeleton sketch

```csharp
// dotnet/ArrowNet.DeltaRs/DeltaRsBackend.cs  (new project, refs DeltaLake.Net + Apache.Arrow 23.0.0)
internal sealed class DeltaRsBackend : IBackend
{
    public string Name => "deltars";
    public IEnumerable<string> Aliases => new[] { "delta-rs" };

    // Translate an ATTACH'd foreign secret (azure/s3/gcs) → object_store storage_options,
    // carried to the catalog on the connection string (same pattern as DaxBackend/DeltaBackend).
    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string,string> fields, string baseConn)
        => StorageOptions.FromSecret(secretType, fields).Encode(baseConn);

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson)
        => new DeltaRsCatalog(connectionString, optionsJson);
}

internal sealed class DeltaRsCatalog : IBackendCatalog
{
    private readonly DeltaEngine _engine = new(EngineOptions.Default);
    private readonly string _root;
    private readonly Dictionary<string,string> _storageOptions;
    // discovery reuses the existing FabricLakehouse/host-FS glob for _delta_log subdirs

    ITable Open(string schema, string table, ulong? version = null) =>
        _engine.LoadTableAsync(new TableOptions {
            TableLocation = TablePath(schema, table),
            StorageOptions = _storageOptions,
            Version = version,
        }, default).GetAwaiter().GetResult();

    public IArrowArrayStream ScanTable(string s, string t, string? specJson, IArrowArrayStream? fv)
    {
        var table = Open(s, t, VersionFrom(specJson));            // time travel via spec.At
        var sql = SelectBuilder.Build(specJson, fv, alias: "t");  // FilterNode -> WHERE, projection -> cols
        return new AsyncEnumerableArrowStream(table.Schema(),
            table.QueryAsync(new SelectQuery(sql) { TableAlias = "t" }, default));
    }

    public long BulkInsert(string s, string t, IArrowArrayStream data, bool create, bool replace,
                           bool _cc, long _txn, IReadOnlyList<string>? part, IReadOnlyList<string>? _sort, string? mode)
    {
        var table = create ? _engine.CreateTableAsync(new TableCreateOptions(TablePath(s,t), SchemaOf(data)) {
                                  PartitionBy = part?.ToList() ?? new(), StorageOptions = _storageOptions }, default).Result
                           : Open(s, t);
        var save = (replace || mode == "overwrite") ? SaveMode.Overwrite : SaveMode.Append;
        table.InsertAsync(data, new InsertOptions { SaveMode = save, OverwriteSchema = mode is "overwrite" }, default).Wait();
        return /* rows from HistoryAsync/commit */ 0;
    }

    // ExecuteDelete/ExecuteUpdate: option (b)/(c) — passthrough or key-reconstruction; NOT rowid copy-on-write.
    // AlterTable(AddColumn): unsupported (no delta-dotnet API) — clean error.
    // GetMetadata(Snapshots) -> HistoryAsync; GetMetadata(Changes) -> QueryTableChangesAsync.
}
```

## Open questions / risks

- **DML** is the deciding factor — decide (a)/(b)/(c) up front; it defines whether `deltars` is read-mostly or
  full-DML.
- **240 MB native + Rust build** in the publish pipeline — acceptable only if the maintenance/interop wins
  justify it; otherwise keep engineered-wood.
- **Two credential paths** (discovery vs object_store) — or drop our discovery and require an explicit table
  list, since delta-dotnet won't enumerate a root.
- **Do we want DataFusion in the loop at all?** Scans via `QueryAsync` run DataFusion for pushdown — a second
  query engine beneath DuckDB. Acceptable for file-skipping, but it is not "just a reader."
