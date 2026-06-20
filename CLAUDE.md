# CLAUDE.md — project knowledge for `mssql_net`

> Canonical project memory. Maintained in the repo (not in per-user agent memory) so it's
> easy to edit and shared across machines. Keep this current as the implementation evolves.

## What this is

`mssql_net` is a **DuckDB extension** that connects DuckDB to **Microsoft SQL Server** by hosting a
C# layer (**CoreCLR, in-process**) and exchanging data + metadata as **Apache Arrow** over the Arrow
C Stream Interface (`ArrowArrayStream`). It is a direct, in-process replacement for the Arrow-Flight
transport used by the "Airport" extension.

Unlike the native-TDS sibling `mssql-extension` (`D:\repos\mssql-extension`, the compatibility
target), **all SQL Server I/O happens in C# via `Microsoft.Data.SqlClient`**; the C++ extension only
registers DuckDB functions and ingests Arrow. Full phased plan:
`C:\Users\c.mettler\.claude\plans\i-want-to-create-soft-crown.md`.

## Architecture (layered for reuse)

Layered so a future **Power BI / DAX** connector reuses the same C++ core + managed bridge:

- **C++ generic core** — `namespace arrownet`, dirs `src/arrownet/` + `src/include/arrownet/`:
  `clr_host` (CoreCLR bootstrap + vtable wrappers), `arrow_ingest` (ArrowArrayStream → DataChunk),
  `arrow_produce` (DataChunk → ArrowArray), `abi.h` (the C ABI contract).
- **C++ DuckDB-API layer** — `namespace duckdb`, classes named `ArrowNet*`, files `arrownet_*`:
  catalog / schema_entry / table_entry / transaction / metadata (`src/catalog/arrownet_*`), DML
  insert / modify / ctas (`src/dml/arrownet_*`), optimizer (`src/arrownet_optimizer.cpp`). The
  internal catalog scan function is `"arrownet_scan"`.
- **C++ provider layer** — keeps the `mssql_net` / `MssqlNet*` name: extension entry
  (`src/mssql_net_extension.cpp`), `mssql_net_secret`, `mssql_net_storage` (ATTACH/connstr),
  `src/copy/mssql_net_copy.cpp`, and all user-facing names (extension `mssql_net`, functions
  `mssql_net_query`/`_exec`/`_refresh_cache`/`_invalidate_cache`, `TYPE mssql_net`, `mssql_*`
  settings, `mssql://` URI, the `"mssql_net"` catalog-type string).
- **C# `ArrowNet.Bridge`** (`dotnet/ArrowNet.Bridge`) — backend-agnostic: C-ABI `[UnmanagedCallersOnly]`
  exports + vtable (`Bootstrap.cs`, `Abi.cs`), handle table, Arrow export/import, `IBackend`/
  `IBackendCatalog`, `ArrowDataReader` (IArrowArrayStream→DbDataReader), `BulkSession`/
  `ChannelArrowStream` (streaming bulk), `StubBackend`.
- **C# `ArrowNet.SqlServer`** (`dotnet/ArrowNet.SqlServer`) — the `Microsoft.Data.SqlClient` backend +
  composition root; published self-contained next to the extension. Discovered via `BackendRegistry`
  reflection (env `ARROWNET_BACKEND_ASSEMBLY`, default `ArrowNet.SqlServer`).

### Target architecture: ONE binary, MULTIPLE providers (corrected goal, 2026-06-20)

The end goal is a **single `arrownet` extension binary that hosts several providers** (SQL Server via
SqlClient, Power BI/DAX via ADOMD, …) — NOT a separate binary per provider. Implications (planned;
current code still uses the single-provider `mssql_net` naming):

- **Generic user-facing names**: `arrownet_query` / `arrownet_exec` (not `mssql_net_query`). The user is
  fine breaking `gen_mssqlcompat_tests.sh` and renaming the kept tests.
- **Dispatch is handle/catalog-based** and already works: `Handles.Resolve<IBackendCatalog>(handle)`
  returns a backend-specific catalog, so any ABI call already routes to the right provider. Multi-provider
  mainly needs: C# `BackendRegistry` keyed by provider name (providers self-register, not `Active`=one) +
  **provider selection at open time** (`ATTACH … (TYPE arrownet, PROVIDER 'sqlserver')`, or inferred from
  the `mssql://`/`dax://` scheme, or the secret's provider). `open_catalog` ABI gains a `provider` arg; the
  catalog-type string becomes the generic `"arrownet"` (provider stored on the catalog).
- **Provider-specific logic lives in C#**: connection-string assembly + auth mapping (move out of
  `mssql_net_secret.cpp`), type mapping, all SQL. The C++ `arrownet` core owns registration + dispatch +
  the function machinery, reused verbatim by every provider.
- **Custom scalar / table / table-in-out functions** (Airport-style, Phase 3) drive this. Two registration
  phases through one ABI shape (`list_global_functions(provider)` / `list_catalog_functions(handle)` +
  `execute_scalar`/`execute_table`/`execute_inout`, decls = Arrow-serialized name/kind/in-schema/out-schema/
  decl_id): **(A) load-time global** via `loader.RegisterFunction` — DuckDB only allows global registration
  during `Extension::Load()`, so this forces the **bridge to boot at extension load** (not lazily);
  **(B) attach-time catalog-bound** — discovered SQL Server procs/UDFs become `ScalarFunctionCatalogEntry`/
  `TableFunctionCatalogEntry` in `ArrowNetSchemaEntry` (resolved as `db.schema.proc(args)`, refreshable via
  the existing cache invalidation). New core file `arrownet_functions.{hpp,cpp}` holds this. Table-in-out
  (`in_out_function`) is the hard part → Phase 4. **Full design: [docs/custom-functions-design.md](docs/custom-functions-design.md)**
  (ABI, the C# authoring API — lambda / attribute(SQLCLR-style, columnar) / derived — and
  `sp_describe_first_result_set` late-binding for table procs).
- Suggested order: (1) C# multi-backend registry; (2) generic rename + provider selection + regenerate the
  compat corpus; (3) connstr/auth logic → C#; (4) dynamic functions (attach-time catalog fns first, then
  load-time global, then table-in-out).

## Implementation status (current)

**Phases 1–2 complete + streaming bulk write; verified against real SQL Server on DuckDB v1.5.4.**

Implemented and verified:
- **ATTACH + catalog**: schemas/tables/views, three-part naming, cross-catalog joins; `schema_filter`/
  `table_filter` (case-insensitive regex); ATTACH-time connection validation (no orphan catalog on
  failure); `mssql://` URI; `CREATE SECRET (TYPE mssql_net, …)` incl. Azure Entra/Fabric auth.
- **Read path** fully in C# behind `get_metadata`/`scan_table` ABI calls — **C++ has zero T-SQL**.
- **Pushdown**: projection (by-name), filter (best-effort via `pushdown_complex_filter`, never erases →
  DuckDB always re-applies; superset-safe shapes only), bare `LIMIT` (`TOP n`), `ORDER BY`+`LIMIT`
  (TopN, gated: non-string keys, NULL-order compatible, no pushed filter).
- **Statistics → optimizer**: cardinality (row count from `sys.dm_db_partition_stats`) + per-column NDV
  (leading-column histogram). **min/max deliberately NOT reported** (DuckDB prunes filters on min/max →
  stale SQL Server stats could drop rows; NDV is costing-only so stale is safe).
- **rowid** from PK / smallest unique index (scalar + compound STRUCT) → enables UPDATE/DELETE.
- **DML**: INSERT (+ INSERT…SELECT, + RETURNING via `OUTPUT INSERTED.*`), UPDATE, DELETE (rowid-based,
  parameterized). INSERT/CTAS/COPY use a **streaming bulk path** (see below).
- **DDL**: CREATE/DROP TABLE, CREATE/DROP SCHEMA, ALTER TABLE (rename table/column, add/drop column,
  change type, SET/DROP NOT NULL, SET/DROP literal DEFAULT); PRIMARY KEY/UNIQUE/literal DEFAULT on CREATE.
- **Transactions**: BEGIN/COMMIT/ROLLBACK with a pinned connection (lazy on first write); reads inside
  the txn use it too (read-your-writes); MARS forced so a scan reader + DML coexist.
- **Functions**: `mssql_net_query` (raw scan), `mssql_net_exec` (raw exec) — both accept a connstr, a
  secret name, OR an attached-catalog name; `mssql_refresh_cache`/`mssql_invalidate_cache` (+ `_net_`
  aliases, arities 1/2/3); `mssql_version()`; `arrownet_managed_dir()` / `arrownet_test_scan()` (diag).
- **Cache invalidation after DDL via `mssql_net_exec`**: DDL detection in C# (`SqlDdl.MayChangeSchema`),
  gated by `SET mssql_exec_invalidate_cache` (default false, Postgres-scanner parity).

Compat suite: ~96/122 of the C++ mssql-extension tests pass (corpus regenerated from upstream via
`scripts/gen_mssqlcompat_tests.sh`, lives in `test/mssqlcompat/`, gitignored). Remaining failures are
non-data: error-WORDING/number assertions (corpus expects native-extension text), C++-only surfaces
(`mssql_pool_stats`/`mssql_open` diagnostics, krb5 connstr parser), COPY-to-temp-table empty-schema
syntax, and catalog-after-rollback staleness.

**Not yet / out of scope:** Airport-style custom scalar/table/in-out functions (Phase 3); connection
pooling knobs / `mssql_pool_stats` (ADO.NET pools by connstr already); COPY to temp tables
(`mssql://cat//#t`, `cat..#t` — `ParseTarget` only accepts strict 3-part names); CHECK constraints +
non-literal/expression DEFAULTs on CREATE; UPDATE/DELETE…RETURNING; length-aware VARCHAR mapping (so
string columns can be PK/UNIQUE keys); bespoke `authenticator=krb5` connstr parsing (see constraints).

## Streaming bulk write (INSERT / CTAS / COPY)

INSERT, CTAS and COPY stream record batches to the provider instead of buffering the whole dataset
(bounded memory for warehouse-scale writes). The concurrency lives in C#:

- **ABI v16** entries: `begin_bulk(handle, schema, table, create, replace, check_constraints,
  ArrowSchema*, out_session)` + `push_batch(session, ArrowArray*)` + `complete_bulk(session, abort,
  *affected)`.
- C#: `BulkSession` = a bounded `Channel<RecordBatch>` (capacity 8) + a `Task.Run` consumer that calls
  the existing `catalog.BulkInsert(... ChannelArrowStream ...)` (so all SqlBulkCopy / CREATE /
  KeepIdentity / transaction logic is reused). `push_batch` blocks for backpressure; the consumer's
  `finally` completes+drains the channel so a fault never deadlocks the producer; the real error
  surfaces from `complete_bulk`. `abort` faults the channel so an in-flight load rolls back.
- C++: each operator begins the session at init, pushes per sink chunk, completes at finalize (+ a
  gstate destructor that aborts on early failure). INSERT…RETURNING is unchanged (small result; still
  buffered via the producer).
- **`check_constraints`**: INSERT passes **true** → `SqlBulkCopyOptions.CheckConstraints` (so a
  constraint-violating INSERT fails like a classic INSERT — SqlBulkCopy skips CHECK/FK by default).
  CTAS/COPY pass **false** (bulk-load speed). NOT NULL is still caught client-side by SqlBulkCopy.
- The legacy `bulk_insert` ABI entry + its `clr_host` wrapper are now unused by C++ (left in place).

## C ABI contract (`src/include/arrownet/abi.h`)

- The managed `Bootstrap.Initialize` fills an `ArrowNetVTable` of C function pointers; tabular results
  flow through caller-allocated `ArrowArrayStream`; errors = status code + owned UTF-8 string freed via
  `free_error`. C# error messages prepend the provider error number when available (`FormatError`
  duck-types an `int Number` property → e.g. `"2627: …"`; provider-agnostic, no SqlClient ref in Bridge).
- **Current version: ABI v16.** **Bump rule:** when you add a vtable entry OR change a signature, bump
  **BOTH** `ARROWNET_ABI_VERSION` in `abi.h` AND `vtable->AbiVersion = N` in `Bootstrap.Initialize`,
  else the host throws "ABI version mismatch". Adding an *enum value* (e.g. a new metadata/alter kind)
  is additive and needs NO bump.
- Ownership: the managed side **consumes/releases** every `ArrowArrayStream`/`ArrowSchema`/`ArrowArray`
  passed in (the C++ caller never releases them; a rare failure leaks rather than double-frees).

## Build & test

- **Target DuckDB v1.5.4** (new extension API: `Extension::Load(ExtensionLoader&)` +
  `loader.RegisterFunction(...)` + `DUCKDB_CPP_EXTENSION_ENTRY(mssql_net, loader)`). Submodules pinned
  to `duckdb@08e34c4` (v1.5.4) + `extension-ci-tools@v1.5.3` (no 1.5.4 branch exists; v1.5.3 is the
  latest tooling for the 1.5.x line). `duckdb` is a **shallow** clone — bump via
  `git -C duckdb fetch --depth 1 origin <sha> && git -C duckdb checkout <sha>`.
- **Windows build needs the VS dev env** — a plain shell fails with `Cannot open include file: 'stdint.h'`.
  Use vcvars (`C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat`).
  Incremental rebuild of the test binary:
  `cmd /c '"…\vcvars64.bat" && cmake --build build/release --target unittest'`.
- Full configure (first time), run inside vcvars64:
  `cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 -DDUCKDB_EXTENSION_CONFIGS=<repo>/extension_config.cmake
  -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 -DENABLE_EXTENSION_AUTOLOADING=1
  -DENABLE_EXTENSION_AUTOINSTALL=1 -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release
  -S <repo>/duckdb -B <repo>/build/release`. `EXTENSION_VERSION "0.0.1"` is set in
  `extension_config.cmake` (the repo has commits now, but keep it — avoids relying on `git describe`).
- **Managed publish:** `pwsh scripts/publish-managed.ps1` → publishes `ArrowNet.SqlServer` (+ Bridge +
  self-contained .NET 10 runtime) into `build/release/extension/mssql_net/arrownet/`. A C#-only change
  needs only a republish (no C++ rebuild) unless an ABI signature changed.
- **`EXTENSION_STATIC_BUILD=1` caveat:** the extension is statically linked into `duckdb.exe`/
  `unittest.exe` AND built loadable; after changing extension code rebuild the **`unittest`**/`shell`
  target (relinks the binary) — `LOAD '<path>'` is a no-op when the binary already embeds it.
- **CoreCLR hosting:** init via `hostfxr_initialize_for_dotnet_command_line` (argv[0] =
  `ArrowNet.Bridge.dll`) then `hdt_load_assembly_and_get_function_pointer`.
  `hostfxr_initialize_for_runtime_config` FAILS for self-contained deployments. The bridge finds its
  files via `ARROWNET_MANAGED_DIR`, else an `arrownet/` folder next to the extension binary.
- **C++ standard gotcha:** DuckDB compiles extensions pre-C++17 → `std::string/wstring::data()` is
  `const`; use `&s[0]` for `MultiByteToWideChar`/`WideCharToMultiByte` out buffers.
- **Tests:** `build/release/test/unittest.exe --test-dir <repo-root> "test/mssqlcompat/<dir>/*"` (and
  `test/verify_*.test`). Set `ARROWNET_MANAGED_DIR=build/release/extension/mssql_net/arrownet` +
  `MSSQL_TESTDB_DSN` (and `MSSQL_TEST_SERVER`/`_CONNECTION_STRING` = the same full DSN for the tests
  that ATTACH it directly). The corpus is regenerated from `D:\repos\mssql-extension/test/sql` by
  `scripts/gen_mssqlcompat_tests.sh`; it lives at `test/mssqlcompat/` and is **gitignored** (keep the
  duckdb submodule clean).
- **Test DB:** Docker `mcr.microsoft.com/mssql/server:2022-latest`, container `mssql-arrownet`, port
  1433, `sa` / `Arrow_Net_123!` (test-only). DBs `ArrowTest` and `TestDB`. Connstr needs
  `TrustServerCertificate=true;Encrypt=true`. `sqlcmd` v18 in-container:
  `docker exec mssql-arrownet /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C`.

## Key decisions & constraints

- **Connection strings must be valid `Microsoft.Data.SqlClient` strings**, passed straight through. We
  do NOT replicate the C++ extension's bespoke connstr dialect (`authenticator=krb5|ntlm|winsspi`,
  `krb5-*`, its client-side conflict validator). The only connstr-shaped input we parse is our own
  `mssql://` URI, translated into a SqlClient connstr. SqlClient already does integrated/Windows/Entra
  auth natively (`Trusted_Connection`, `Integrated Security=SSPI`, `Authentication=Active Directory …`).
  → `integrated_auth/parsing.test` is failing-by-design. If verbatim cross-compat for an
  `authenticator=krb5` string is ever needed, do a thin keyword *translation*, never a validating parser.
- **Secret parameter names mirror the C++ mssql secret** (host/port/database/user/password/use_encrypt/
  access_token/authentication/azure_secret/schema_filter/table_filter/application_name) — left as-is for
  cross-compat (user decision: "leave as is").
- **Statistics: report ONLY NDV, never min/max.** DuckDB's StatisticsPropagator prunes filters on
  min/max (→ FILTER_ALWAYS_FALSE/TRUE), so they must be exact; SQL Server stats are sampled/stale →
  reporting min/max could drop rows. NDV only feeds selectivity (never pruning) → stale is safe.
- **Pushdown is best-effort and never erases** — DuckDB re-applies every predicate, so an
  over-approximation (superset) is correct; map filters/projection **by name**, not positionally.
- **C++ is provider-agnostic** — the operators only produce Arrow + table/column identity; every SQL
  Server specific (SqlBulkCopy, parameterized UPDATE/DELETE, type mapping, DDL generation, all `sys.*`)
  lives in `ArrowNet.SqlServer`. Keep it that way.
- **Self-healing catalog cache:** `GetOrCreateEntry` evicts on a `FetchTableColumns` failure (a table
  dropped out-of-band leaves no stale entry). Do NOT remove this to match
  `exec_invalidate_cache_setting.test`'s setting-OFF stale-cache footgun — it's a deliberate robustness
  difference, not a bug.
- **CHECK constraints + non-literal DEFAULTs on CREATE: deliberately skipped** (per user).
- **Commit only when asked.** The Python scaffold (`main.py`/`pyproject.toml`/`uv.lock`/
  `.python-version`) is intentionally left untracked. `.gitignore` note: `**/arrownet/` would match the
  *source* `src/arrownet/` + `src/include/arrownet/` — negations re-include them; never re-broaden it.

## Sibling repos (reference under `D:\repos\`)

`mssql-extension` (C++ TDS — compat target; adapting permitted, it's the user's repo),
`adbc_scanner` (Arrow→DuckDB ingestion pattern), `airport` (function-declaration pattern),
`SqlServerFlights` (reusable C# SqlClient/DAX→Arrow), `ArrowSerializer` (POCO↔Arrow for Phase 3),
`vgi` (source-available — never copy code, design patterns only).
