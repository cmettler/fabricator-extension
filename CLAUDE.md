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
- Suggested order: (1) **C# multi-backend registry — DONE** (`BackendRegistry` is provider-keyed:
  `IBackend.Name`/`Aliases`, `Resolve(provider)`, `Active`=default; multi-assembly discovery via
  `ARROWNET_BACKEND_ASSEMBLY` comma-list; SqlServer = `"sqlserver"`/alias `"mssql"`. Behavior-preserving —
  `Active` still routes to SqlServer); (2) **provider selection — DONE** (`open_catalog(provider,…)` ABI
  v17 → `BackendRegistry.Resolve`; ATTACH `PROVIDER` option + `scheme://` inference; clean unknown-provider
  error). The **generic rename** (`arrownet_query`/`_exec`, catalog-type `"arrownet"`) + corpus regen is
  **deferred** to when the 2nd provider lands (cosmetic; the functional capability is complete);
  (3) **connstr/auth → C# — DONE** (`build_connection_string` ABI v18: `mssql_net_secret.cpp` reads the
  secret + emits its fields as JSON, `SqlServerBackend.BuildConnectionString` assembles the SqlClient
  connstr; `MapAuthentication`/`QuoteConnValue`/the access-token marker are now C#-only — C++ has no connstr
  knowledge); (4) dynamic functions — **(4a) function discovery DONE** (`mssql_net_functions(catalog)` table
  fn + `ARROWNET_META_FUNCTIONS`); **(4b) attach-time catalog-bound scalar UDFs DONE** (discovered scalar
  UDFs become `ScalarFunctionCatalogEntry` in `ArrowNetSchemaEntry`, resolved as `db.schema.fn(args)` and
  executed over Arrow — ABI v19); **(4c) attach-time catalog-bound table-valued functions DONE** (discovered
  TVFs become `TableFunctionCatalogEntry`, resolved as `SELECT * FROM db.schema.tvf(args)`, with real
  SQL-level projection + best-effort filter pushdown reusing the table scan's machinery — ABI v21);
  **(4d) attach-time catalog-bound stored procedures DONE** (procs with a determinable result set resolved
  as table functions — `sp_describe` schema, `EXEC` execution, no pushdown — ABI v22; **named/optional
  params DONE (4d-2)**; **OUTPUT params + RETURN value as flat columns DONE (4d-3)**; multi-result-set +
  INPUT/OUTPUT deferred); **(4e) attach-time custom C#-authored scalar functions DONE** (`IArrowScalarFunction`)
  **+ (4f) custom table functions DONE** (`IArrowTableFunction`) — both reuse the catalog scalar/TVF path,
  C#-only, no ABI; chosen over load-time global (deferred). **(4g) table-in-out DONE** (ABI v23 —
  `inout_open`/`push`/`finish`/`abort`; full plan + corrections:
  [docs/custom-functions-design.md](docs/custom-functions-design.md) §11.1): a discovered TVF `db.s.tf`
  gains a **sibling `db.s.tf_each(<input table>)`** that applies it **once per input row** via SQL-Server
  `CROSS APPLY` (T-SQL generated by C#, run on SQL Server — NOT a DuckDB lateral join), over the §11
  coordinated bounded channel (one session per call; parallel `UNION ALL` input fed thread-safely). Output =
  the echoed input columns (typed as the TVF **parameters**, since C# CASTs the VALUES to them) ++ the TVF
  output columns. **Two corrections found during the build:** (1) DuckDB forbids a `{LogicalType::TABLE}`
  overload coexisting with the scalar-arg scan form under one name (`bind_table_function.cpp`: "TABLE
  parameter, and multiple function overloads — not supported") → the in-out form is a **separate `_each`
  catalog entry** (single TABLE overload), the scan form (4c) keeps the bare name; alias tracked in
  `ArrowNetSchemaEntry::inout_functions_`. (2) **Output is emitted SYNCHRONOUSLY per input chunk** (each
  `inout_push` runs that chunk's CROSS APPLY to completion + returns its full output) → there is **no tail**,
  so emitting rows never depends on detecting which parallel branch finishes last. This replaced an unsound
  first attempt (an atomic last-branch counter in `in_out_function_final`): `PhysicalUnion::BuildPipelines`
  can run UNION branch pipelines **sequentially**, so branch 1 could finish (counter→0, premature finish)
  before branch 2 starts → lost rows; it passed tests only by scheduling luck (caught in review). Also
  `OperatorFinalize` is a **global single-shot hook handed no `DataChunk`** so it **can't emit rows** — it's
  reserved for the per-row-proc COMMIT (a no-rows action). Session lifecycle = a refcounted
  `InOutSessionHolder` on the bind data; its **RAII destructor → `inout_abort`** is the release/rollback
  backstop on every teardown path (also frees the GCHandle). Verified: `test/verify_table_inout.test` (63
  assertions — incl. parallel UNION ALL, ORDER BY+LIMIT, WHERE, aggregate, empty, multi-column, large
  BIGINT→INT, error+recover). **(4g-custom) custom C#-authored table-in-out DONE** (`IArrowTableInOutFunction`
  — in-out analog of 4e/4f): a pure-C# **per-chunk streaming** table-in-out (no SQL object), may keep mutable
  state across chunks (running aggregate); surfaced as `kind='inout'` → C++ `AddInOutFunction` registers a
  bare-name `{TABLE}` entry (`GetOrCreateCustomInOutFunction` + `ArrowNetCustomInOutBind`, output = the fn's
  full declared schema, no input echo), dispatched in C# (`CustomInOut` factory registry → fresh instance per
  session; `CustomInOutSessionImpl` runs `Process(chunk)`). Reuses the 4g operator path — no new ABI/C++
  operator. Verified: `test/verify_custom_functions.test` (cf_tag per-row, cf_running_sum stateful 4999-row
  multi-chunk, per-session state, no SQL object).
- **Per-row stored procs (4g-proc)**: a discovered proc also gets `_each` (C++ `AddTableFunction` registers
  the alias for procs too; the in-out bind/operator are reused). C# `InOutOpen` routes a proc (`sys.objects`
  P/PC) to `ProcInOutSessionImpl`: per input row it runs `DECLARE @t TABLE(<proc result>); INSERT @t EXEC
  [s].[p] @param=@p,…; SELECT <echoed input>, t.* FROM @t;` on **DuckDB's pinned connection/`_txn`**
  (`BeginWrite`) — echo is server-side (output = input cols ++ proc result cols), result-set procs only. The
  proc's writes commit/roll back with **DuckDB's** transaction (autocommit + explicit `BEGIN`), so
  `Finish`/`Abort` are cleanup-only. Verified: `test/verify_proc_inout.test`. **(4g-proc) per-row stored-proc in-out DONE**: a discovered
  proc also gets `_each` (`db.s.usp_x_each(<table>)` EXECs the proc once per input row — a proc can't be
  inline-CROSS-APPLY'd). The per-row EXECs run on **DuckDB's pinned transaction** (`BeginWrite`), so the
  proc's writes commit/roll back with **DuckDB's** COMMIT/ROLLBACK — atomic in autocommit AND inside an
  explicit DuckDB `BEGIN`, no per-row commits. **`OperatorFinalize` is NOT used for the commit** (committing
  the in-out's own txn at operator-finish would commit before a user's explicit `ROLLBACK` could undo it; the
  transaction manager is the correct signal). C# `InOutOpen` routes a proc (`sys.objects` type P/PC) to
  `ProcInOutSessionImpl`, which per row runs `DECLARE @t TABLE(<proc result>); INSERT @t EXEC [s].[p]
  @param=@p,…; SELECT <echoed input>, t.* FROM @t;` on the pinned conn/`_txn` (echo done server-side →
  output = input columns ++ proc result columns; result-set procs only). Verified: `test/verify_proc_inout.test`
  (echo output, autocommit commit, row-failure rolls back the whole statement, explicit-`BEGIN`
  read-your-writes + `ROLLBACK` undoes — 31 assertions). **Next: the `OperatorFinalize` cleanup signal**
  (injected operator → C# `inout_finish` as a reliable "in-out finished, release resources" hook — NOT for
  commit; for the read-only TVF it cleanly commits the own snapshot txn, and is a C# cleanup hook for other
  uses; `OperatorFinalize` fires once even above a UNION, verified via `MetaPipeline`/executor scheduling).

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

**Not yet / out of scope:** the **`OperatorFinalize` cleanup signal** (a reliable injected "in-out finished →
release resources" C# hook — NOT for commit; see the 4g sequencing note) and
**load-time global** functions
(Phase 3 — scalar UDFs, TVFs, stored procs + custom C#-authored scalar, table & table-in-out functions +
discovered-TVF & per-row-proc table-in-out done, see "Callable scalar UDFs (4b)" / "table functions (4c)" /
"stored procedures (4d)" / "custom functions (4e scalar, 4f table)" / "table-in-out (4g — incl. custom C#
in-out + per-row procs)"; proc multi-result-set + INPUT/OUTPUT + OUTPUT-param-only `_each` still deferred;
load-time global deferred in
favor of attach-time custom functions); connection
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
- **ABI v17–v19** entries: `open_catalog(provider, conn, …)` (v17); `build_connection_string(provider,
  fields_json, …)` (v18); and the **scalar-function trio** (v19): `get_function_param_schema(handle,
  schema, func, out)` + `get_function_return_schema(…)` (each a zero-row Arrow stream whose schema gives
  the arg/return `LogicalType`s, via `PopulateReturnSchema`) + `execute_scalar(handle, schema, func, args,
  out)` (runs the UDF over an N-row arg batch; the managed side consumes `args`).
- **ABI v20/v21** entries (table functions): `get_function_output_schema(handle, schema, func, out)`
  (zero-row Arrow stream = the TVF's output columns) + `execute_table(handle, schema, func, args, spec_json,
  filter_values, out)` (`args` = 1-row batch of the constant call args; `spec_json`+`filter_values` carry
  projection + best-effort filter pushdown exactly like `scan_table`; `out` = the result rows). The
  `spec_json`/`filter_values` params were added at **v21**.
- **ABI v22** entry (stored procs): `execute_proc(handle, schema, func, args, out)` — runs `EXEC [s].[p]
  @p0,…` over the 1-row positional args, `out` = the proc's first result set. No `spec_json` (a proc's EXEC
  isn't inline-wrappable → no pushdown). Procs reuse `get_function_param_schema` (input params) +
  `get_function_output_schema` (which auto-detects proc vs TVF — `sp_describe` vs `ROUTINE_COLUMNS`).
- **ABI v23/v24** entries (table-in-out, 4g): `inout_open(handle, schema, func, input_schema, isolation,
  *out_session)` (input table columns = the TVF's positional params; managed side consumes the schema;
  `isolation` added at v24 — the session opens ONE transaction at that SQL isolation level so all its
  per-chunk queries share a consistent view; from `SET mssql_isolation_level` ?? the ATTACH `isolation_level`
  option) + `inout_push(session, in_chunk, out)` (runs that chunk's CROSS APPLY synchronously; `out` = its
  full output) + `inout_finish(session, out)` (commit; `out` empty in the synchronous model) +
  `inout_abort(session)` (rollback + frees the GCHandle; idempotent). `inout_abort` (not `inout_finish`)
  frees the handle, so the C++ holder destructor always calls it. See "Callable table-in-out (4g)" below.

### Callable scalar UDFs (4b)
- **Discovery**: `ArrowNetCatalog::LoadCatalog`/`RefreshCache` call `DiscoverFunctions` (reads
  `ARROWNET_META_FUNCTIONS`, first 3 string cols) and `AddScalarFunction(name)` for every `kind=='scalar'`
  in a matched schema. Names cached in `ArrowNetSchemaEntry::scalar_functions_`; entries materialized lazily.
- **Registration**: `ArrowNetSchemaEntry::LookupEntry`/`Scan` now handle `CatalogType::SCALAR_FUNCTION_ENTRY`
  → `GetOrCreateScalarFunction` fetches the param + return schemas (`FetchFunctionParamSchema` /
  `FetchFunctionReturnType`), builds a `ScalarFunction` with a **capturing-lambda** callback (no bind/
  function_info dance — `scalar_function_t` is `std::function`), and caches a `ScalarFunctionCatalogEntry`.
  Stale-on-fetch self-heals (evict → not-found), like the table path.
- **Callback**: marshals the arg `DataChunk` → Arrow (`ArrowAppender`+`ArrowProducer`), calls
  `execute_scalar`, ingests the single-column result via `ArrowStreamReader` + `VectorOperations::Copy`
  into the result `Vector`. Registered **VOLATILE** (never folded) + **SPECIAL_HANDLING** (sees NULL args —
  SQL Server semantics).
- **C# execution** (`SqlServerCatalog.ExecuteScalar`, option **B**): runs chunked, parameterized
  `SELECT [s].[f](@..) AS result UNION ALL …` — the result column inherits the UDF's return type (correctly
  typed Arrow, no hand-built array). Chunked to ≤ ~`2000/param_count` rows/query to stay under SQL Server's
  ~2100-parameter cap. Param/return schemas via typed-NULL `SELECT CAST(NULL AS <type>) …` reconstructed
  from `INFORMATION_SCHEMA.PARAMETERS` (shared `BuildSqlType` with `ColumnTypeInfo`).
- **Verified**: `db.dbo.vf_add(1,2)=3`, string returns, NULL handling (`vf_inc(NULL)=0`), NULL-in→NULL-out
  (`vf_add(NULL,2)` IS NULL; per-row null bitmap round-trips), and a 5000-row batch summing exactly
  (exercises 2048-row vectors × the C# param-limit chunking). Strict typing: a BIGINT arg to an `INTEGER`
  UDF errors (no implicit narrowing) — cast required. Committed test: `test/verify_scalar_functions.test`.

### Callable table functions (4c)
- **Scope**: inline + multi-statement **TVFs** (`kind=='table'`), output schema **static** from
  `INFORMATION_SCHEMA.ROUTINE_COLUMNS`. **Deferred**: stored procs (need `sp_describe_first_result_set`/
  `EXEC`/named params/`_OUTPUT_`).
- **Discovery + registration**: `LoadCatalog`/`RefreshCache` `AddTableFunction(name)` for every
  `kind=='table'` in a matched schema (`table_functions_`). `ArrowNetSchemaEntry::LookupEntry`/`Scan` handle
  `CatalogType::TABLE_FUNCTION_ENTRY` → `GetOrCreateTableFunction` builds a `TableFunctionCatalogEntry` and
  caches it (stale-on-fetch self-heals, like the table/scalar paths).
- **Bind**: `table_function_bind_t` is a **raw fn pointer** (can't capture, unlike `scalar_function_t`), so
  the identity rides an `ArrowNetTableFunctionInfo : TableFunctionInfo` on the `TableFunction`, read in the
  static bind via `input.info`. The bind (1) resolves the output schema via `get_function_output_schema`
  (zero-row → `PopulateReturnSchema`, so the TVF isn't executed just to bind), then (2) installs a capturing
  `StreamFactory` (which **is** `std::function`) that marshals the constant call args (`input.inputs`) into a
  1-row Arrow batch (`ArrowAppender`+`ArrowProducer`) and calls `execute_table`. Reuses `ArrowStreamScan`/
  `ArrowStreamInitGlobal`/`Local`.
- **Projection + filter pushdown (real, SQL-level)**: the TVF reuses the **catalog table scan's** pushdown
  machinery. `push_projection=true` on the bind_data + `projection_pushdown=true` + `pushdown_complex_filter
  = ArrowNetComplexFilterPushdown` (extracted from `arrownet_table_entry.cpp` out of its anon namespace,
  declared in `arrownet_table_entry.hpp`, shared by both scans). The scan factory forwards the request's
  `spec_json`+`filter_values` to `execute_table`. So C# emits `SELECT <cols> FROM [s].[f](@a0,…) WHERE
  <filter>` — inline TVFs get inlined by SQL Server → genuine pushdown. Best-effort + never-erase (DuckDB
  re-applies every predicate), like the table scan.
- **C# execution** (`SqlServerCatalog`): `GetFunctionOutputSchema` = typed-NULL `SELECT` reconstructed from
  `INFORMATION_SCHEMA.ROUTINE_COLUMNS` (shared `BuildSqlType`); `ExecuteTable` binds the constant args as
  `@a0…` (disjoint from the filter's `@p0…`) and delegates to the shared **`ScanFromSource`** helper (also
  used by `ScanTable`), which builds the projected/filtered `SELECT … FROM <source> WHERE …` — streamed lazily.
- **Verified**: inline TVF (`tf_nums(3)`→1,2,3), multi-column (`tf_pair(7,'hi')`), multi-statement
  (`tf_ms`→squares), and aggregation over a TVF. **Pushdown proven via the plan cache**: the statement that
  reached SQL Server was `SELECT [id],[name],[salary] FROM [dbo].[tf_emp](@a0) WHERE [id] <> @p0` (column
  list, not `*`; parameterized `WHERE`). Committed test: `test/verify_table_functions.test` (incl. a
  `dm_exec_query_stats` proof that `FROM [dbo].[tf_ms] … WHERE …` reached the server).
### Callable stored procedures (4d)
- **Scope**: procs resolved as table functions (`SELECT * FROM db.schema.proc(name := val)`) with **named
  parameters** (4d-2); a proc returns either its **first result set** OR, if it has **OUTPUT params**, those
  outputs + the integer RETURN value as flat columns (4d-3). **Deferred**: multiple result sets, INPUT/OUTPUT
  (`INOUT` with a supplied value), the `_OUTPUT_`-struct broadcast-over-a-result-set shape (a proc with both
  outputs AND a result set is treated as outputs-only — per the observation that the combo doesn't occur).
- **OUTPUT params (4d-3, C#-only)**: input params are `PARAMETER_MODE='IN'` (functions are always IN, so
  this is a no-op there; for a proc it excludes OUTPUT params, mode `'INOUT'`, from the named inputs).
  `GetFunctionOutputSchema`: if a proc has OUTPUT params (`ProcOutputParams`) → output schema = those columns
  + a `return_value INT` (typed-NULL SELECT, **flat — no struct**); else its first result set (`sp_describe`).
  `ExecuteProc`: with OUTPUT params, runs a `DECLARE @o …, @_rv int; EXEC @_rv = [s].[p] @in=@p0,
  @o=@o OUTPUT; SELECT @o AS [o], @_rv AS [return_value];` batch — the final SELECT returns the captured
  outputs as a normal 1-row result set (no `Direction=Output` timing caveat, no buffering); the proc's own
  result set is ignored.
- **Named/optional params (4d-2)**: procs register their params as DuckDB **named parameters**
  (`tf.named_parameters[name]=type`, empty positional `arguments`) — mirrors `EXEC @name=val`. The bind
  gathers only the **supplied** `input.named_parameters` (each cast to its declared type) into the 1-row
  args batch whose **field names = the parameter names**; C# `ExecuteProc` builds `EXEC [s].[p] @name=@p0,…`
  from those field names. Omitting a param → it's absent from the EXEC → SQL Server uses the proc's own
  `DEFAULT` (so **optional params work for free**, no `has_default_value` discovery needed); a required
  param omitted → SQL Server errors. (TVFs stay **positional** — `input.inputs`.)
- **Unified with TVFs**: `table_functions_` is a `name -> is_proc` map; discovery routes `kind=='proc'`
  → `AddTableFunction(name, true)`. Procs reuse the **same** `TableFunctionCatalogEntry` registration +
  static bind via an `is_proc` flag on `ArrowNetTableFunctionInfo`. Proc branch: factory calls
  `execute_proc` (not `execute_table`), `push_projection=false`, and **no** `pushdown_complex_filter` — a
  proc's `EXEC` isn't inline-wrappable, so DuckDB projects + filters locally.
- **Output schema** (`SqlServerCatalog.GetFunctionOutputSchema`): TVFs use `INFORMATION_SCHEMA.ROUTINE_COLUMNS`;
  a proc ⇒ OUTPUT params (`ProcOutputParams`) + `return_value` if any, else
  `sys.dm_exec_describe_first_result_set_for_object(OBJECT_ID(@obj),0)` (`system_type_name` used directly).
  Auto-routes by object kind. Empty ⇒ "no describable result set".
- **Execution** (`ExecuteProc`): no-output proc ⇒ `EXEC [s].[p] @name=@p0,…` (streams the first result set);
  output proc ⇒ the `DECLARE/EXEC OUTPUT/SELECT` batch above. Input param types come from
  `INFORMATION_SCHEMA.PARAMETERS` (reused `get_function_param_schema`, whose field names are the de-@'d names).
- **Verified**: `usp_sc(minSalary := 200)` → rows; local projection+filter; aggregation; **optional param**
  omitted → proc `DEFAULT` (`usp_opt(base:=10)`→60) and supplied → override (`…, bonus:=5`→15); **OUTPUT
  params** flat (`usp_outp(a:=10,b:=3)` → `sum=13, diff=7, return_value=42`). Committed test:
  `test/verify_stored_procs.test`.

### Custom (provider-authored) functions (4e scalar, 4f table)
- Beyond functions *discovered* from SQL Server, a provider can **author custom functions in C#**:
  - **4e scalar** — `IArrowScalarFunction` (Bridge) = `SchemaName`/`Name`/`Parameters`(arg fields)/
    `Result`(field)/`Invoke(RecordBatch)→IArrowArray`. Demo `CustomFunctions.Scalar`: `dbo.cf_add(a,b)=a+b`.
  - **4f table** — `IArrowTableFunction` (Bridge) = `SchemaName`/`Name`/`Parameters`/`OutputSchema`/
    `Invoke(RecordBatch args)→IEnumerable<RecordBatch>` (args = the 1-row positional call args; yields result
    batches). Demo `CustomFunctions.Table`: `dbo.cf_range(n)` → `(value, squared)` rows generated in C#.
- **Reuses the entire catalog scalar/TVF path — C#-only, no ABI/C++ change** (the lean alternative to
  load-time global functions): `GetMetadata(Functions)` appends the custom functions to `FunctionsSql` via
  `UNION ALL` (`kind='scalar'`/`'table'`), so the existing C++ discovery registers them as a
  `ScalarFunctionCatalogEntry` / `TableFunctionCatalogEntry` exactly like a discovered function. The catalog
  consults `CustomScalar`/`CustomTable` registries (keyed `schema.name`, case-insensitive) **first**:
  `GetFunctionParamSchema` (both), `GetFunctionReturnSchema`/`ExecuteScalar` (scalar), `GetFunctionOutputSchema`/
  `ExecuteTable` (table) → declared schema / run the C# `Invoke`; otherwise the SQL path. A custom function
  shadows a same-named SQL object (custom wins).
- **Custom table functions get no SQL-level pushdown** (there's no SQL to push into): `ExecuteTable` ignores
  `spec_json`/`filter_values` and returns the full result; `push_projection=true` is still set (the TVF path),
  so `arrow_ingest`'s `BuildProjectionMapping` selects the projected columns **by name** from the full result
  (extra columns ignored), and DuckDB re-applies filters above the scan. Args are **positional** (TVF-style).
- **Attach-time + catalog-bound** (`db.schema.fn`), not connection-free globals — chosen over load-time
  global functions because it avoids booting the CLR at `Extension::Load()` and needs no new ABI. (Load-time
  global via `loader.RegisterFunction` remains an option if connection-free functions are ever needed; the
  same `IArrowScalarFunction` authoring + the existing `execute_scalar` with a handle-less marker would reuse
  this path.)
- **Verified**: scalar `db.dbo.cf_add(2,3)=5`, vectorized, NULL→NULL; table `cf_range(3)`→`(1,1),(2,4),(3,9)`
  with projection (`squared` only) + filter (`value>1`) + aggregation; both discovered (`scalar`/`table`)
  with **no SQL object** (`sys.objects` count 0). Committed test: `test/verify_custom_functions.test`.

### Callable table-in-out (4g)
- **Surface**: a discovered TVF `db.s.tf` ALSO gets a sibling `db.s.tf_each(<input table>)` (DuckDB
  forbids a TABLE-param overload sharing the bare name — see the §91 sequencing note). `tf_each(<table>)`
  applies `tf` **once per input row** via SQL-Server **`CROSS APPLY`** (T-SQL generated + run in C#),
  output = the echoed input columns (typed as `tf`'s **parameters** — C# CASTs the VALUES to them) ++
  `tf`'s output columns. Read-only (a TVF can't modify data). The input table's columns map **positionally**
  to `tf`'s params.
- **Registration** (`arrownet_schema_entry.cpp`): `AddTableFunction(name,false)` also registers
  `inout_functions_["<name>_each"] = name`; `GetOrCreateTableFunction` resolves the alias via
  `GetOrCreateInOutFunction` → a single `{LogicalType::TABLE}` `TableFunction` (`in_out_function` only,
  `function_info.func` = the **base** TVF). A real same-named `_each` function wins (matched first). `Scan`
  lists the aliases so they're discoverable.
- **Operator** (all in the anon namespace of `arrownet_schema_entry.cpp`): `ArrowNetInOutBind` (output
  schema, no execution) / `…InitGlobal` (`ToArrowSchema` + `inout_open` into the holder) / `…InitLocal`
  (trivial) / `…Function` (`inout_push` → the chunk's **full** output, drained across `HAVE_MORE_OUTPUT`,
  then `NEED_MORE_INPUT`). **Synchronous per chunk → no tail → no `in_out_function_final`, no counter.**
  Session lives in `InOutSessionHolder` (refcounted, on the bind data); its **RAII destructor → `inout_abort`**
  is the release/rollback backstop on every teardown path (also frees the GCHandle). `OperatorFinalize`
  reserved for the per-row-proc COMMIT (it can't emit rows). See design §11.1.
- **C#** (`SqlServerCatalog.InOutSessionImpl`): synchronous — `Push` runs `RunCrossApply` inline
  (lock-serialized for parallel branches) + stashes the chunk's output, `DrainReady` returns it, `Finish`
  drains leftovers (no tail), `Abort` releases. `RunCrossApply` builds
  `SELECT p.*, f.* FROM (VALUES (CAST(@.. AS <paramtype>),…)) p(cols) CROSS APPLY [s].[tf](p.cols) f`,
  sub-chunked under the ~2100-param cap. A CROSS APPLY error throws out of `Push`/`inout_push` → fails the query.
- **Isolation / consistent view**: the in-out session opens ONE SQL transaction (ADO.NET `SqlTransaction`,
  MARS-compatible) wrapping all its per-chunk CROSS APPLY queries, at a configurable isolation level — so a
  call sees one consistent snapshot even if another process modifies the data between chunks. Level from the
  ATTACH `isolation_level` option (per-catalog default, `ArrowNetCatalog::isolation_level_`) overridable by
  `SET mssql_isolation_level` (resolved in C++ `ResolveInOutIsolation`, passed via `inout_open`'s v24
  `isolation` arg). `SqlServerCatalog.BeginInOutScope` maps it (`read uncommitted`/`read committed`/
  `repeatable read`/`serializable`/`snapshot`; snapshot needs `ALLOW_SNAPSHOT_ISOLATION ON`); `Finish`
  commits, `Abort`/destructor rolls back. (Custom C# in-out functions run in C#, so isolation doesn't apply.)
  Verified: `test/verify_inout_isolation.test` (a TVF reporting `transaction_isolation_level`, run via
  `_each`, shows the configured level; ATTACH default + SET override + unknown-name error).
- **Verified**: `test/verify_table_inout.test` (63 assertions) — scalar-arg regression, single-chunk,
  parallel `UNION ALL` (coherent), `WHERE`, `ORDER BY`+`LIMIT`, aggregate, empty, multi-column, large
  50-row `BIGINT`→`INT` (sub-chunking + type round-trip), error mid-stream + recovery.
- **Custom C#-authored in-out (4g-custom)**: `IArrowTableInOutFunction` (Bridge) =
  `SchemaName`/`Name`/`InputSchema`/`OutputSchema` + `IEnumerable<RecordBatch> Process(chunk)` (the in-out
  analog of 4e `IArrowScalarFunction` / 4f `IArrowTableFunction`). A pure-C# **per-chunk streaming**
  table-in-out (no SQL object) that may keep mutable state across chunks (running aggregate — `Process` is
  invoked serially per session) and declares its **full** output (no input echo). There is no emit-at-end
  hook (a whole-table aggregate is a pipeline breaker, not a streaming in-out). Surfaced via
  `FunctionsMetadataSql` as `kind='inout'`; C++ `AddInOutFunction` registers a bare-name `{TABLE}` entry
  (`GetOrCreateCustomInOutFunction` + `ArrowNetCustomInOutBind`, reusing the 4g operator callbacks — no new
  ABI). C# `CustomInOut` is a **factory** registry (fresh
  instance per session so state can't leak across queries); `InOutOpen` dispatches to `CustomInOutSessionImpl`
  (runs `Process` per push) ahead of the CROSS APPLY path. Demos `CustomFunctions.InOut`: `dbo.cf_tag`
  (per-row `(n, n*n)`) + `dbo.cf_running_sum` (stateful cumulative sum, emitted per row). Verified in
  `test/verify_custom_functions.test`.

- **Filtering**: discovered scalar UDFs + TVFs/procs are gated by the ATTACH `schema_filter` (icase
  `std::regex`, applied in `LoadCatalog`/`RefreshCache`); `table_filter` is table-only and does NOT apply to functions.
- **Open design items (filters + refresh)** — deliberated, not yet built:
  - A **`function_filter`** ATTACH option (icase regex on the function name), symmetric with `table_filter`,
    to gate which UDFs/TVFs register when a catalog has many. Today functions are schema-filtered only.
  - **Targeted/scoped refresh.** `mssql_refresh_cache` is arity-1 (whole catalog); `mssql_invalidate_cache
    (catalog[,schema[,table]])` accepts the schema/table args for native-extension compat but **ignores
    them** (always a full refresh — a valid superset). The `mssql_net_exec` auto-refresh (gated by
    `mssql_exec_invalidate_cache`) is likewise a **full** `RefreshCache`: the C# DDL detector returns only a
    bool `schema_may_change` (no object/schema name crosses the ABI), so there's nothing to scope to — and
    it deliberately doesn't parse the statement. Idea: rename the `table` arg to a generic **object name** +
    implement scoped re-discovery for whichever kind it is (table/view/function/proc); the exec path would
    additionally need C# to surface the touched object (not just the bool flag). **Native parity:** the C++
    mssql extension's `mssql_exec` does the same — full `catalog.InvalidateMetadataCache()` gated by its own
    bool `ExecSqlMayChangeSchema`, no scoping; it only scopes (`InvalidateSchema`/table-set) in
    catalog-driven DDL where the name is known (as we do via per-entry eviction). It invalidates *lazily*
    (mark stale + evict, reload on next access) vs our *eager* `RefreshCache` re-discovery — so scoping the
    exec path would exceed native parity, and a lazy mark-stale would be cheaper here too.

## C ABI contract (`src/include/arrownet/abi.h`)

- The managed `Bootstrap.Initialize` fills an `ArrowNetVTable` of C function pointers; tabular results
  flow through caller-allocated `ArrowArrayStream`; errors = status code + owned UTF-8 string freed via
  `free_error`. C# error messages prepend the provider error number when available (`FormatError`
  duck-types an `int Number` property → e.g. `"2627: …"`; provider-agnostic, no SqlClient ref in Bridge).
- **Current version: ABI v24** (`inout_open` gained an `isolation` arg at v24 — the in-out session runs
  its per-chunk CROSS APPLY in one transaction at that SQL isolation level for a consistent view).
  **Bump rule:** when you add a vtable entry OR change a signature, bump
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
- **Windows build needs the VS dev env** — a plain shell fails at *compile* with `Cannot open include
  file: 'stdint.h'`. **Use the VS 18 vcvars, NOT VS 2022:**
  `C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat`. The build is
  configured against the VS 18 toolset (`…/VC/Tools/MSVC/14.50.35717`, see `CMAKE_CXX_COMPILER` in
  `build/release/CMakeCache.txt`); linking with an older toolset (VS 2022 = `14.44.x`) **fails at link**
  with `unresolved external symbol __std_find_first_not_of_trivial_pos_1` / `__std_rotate` /
  `__std_unique_1` — newer STL vectorized-algorithm intrinsics that `duckdb_static.lib` references but the
  older vcruntime lacks. Run every cmake/ninja command inside that vcvars shell, e.g.
  `cmd /c '"…\18\…\vcvars64.bat" && cmake --build build/release --target <target>'`.
- **Targets → binaries** (`EXTENSION_STATIC_BUILD=1` ⇒ the extension is statically embedded in BOTH exes
  *and* built loadable):
  - `shell` → `build/release/duckdb.exe` (interactive shell; **embeds** the extension).
  - `unittest` → `build/release/test/unittest.exe` (runs the `.test` suites; **embeds** the extension).
  - `mssql_net_loadable_extension` → `build/release/extension/mssql_net/mssql_net.duckdb_extension`
    (the loadable; only matters when `LOAD`-ing into a duckdb that does NOT embed it — rarely, here).
  - `cmake --build build/release` (no `--target`) builds all of them.
  - **After changing C++ extension code, rebuild the target whose binary you'll run.** Building only
    `mssql_net_loadable_extension` then running `duckdb.exe`/`unittest.exe` runs the STALE embedded copy
    (a `LOAD '<path>'` is then a no-op). This is the #1 "my change didn't take" trap.
- Full configure (first time), run inside vcvars64:
  `cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 -DDUCKDB_EXTENSION_CONFIGS=<repo>/extension_config.cmake
  -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 -DENABLE_EXTENSION_AUTOLOADING=1
  -DENABLE_EXTENSION_AUTOINSTALL=1 -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release
  -S <repo>/duckdb -B <repo>/build/release`. `EXTENSION_VERSION "0.0.1"` is set in
  `extension_config.cmake` (the repo has commits now, but keep it — avoids relying on `git describe`).
- **Managed publish:** `pwsh scripts/publish-managed.ps1` → publishes `ArrowNet.SqlServer` (+ Bridge +
  self-contained .NET 10 runtime) into `build/release/extension/mssql_net/arrownet/`. A C#-only change
  needs only a republish (no C++ rebuild) unless an ABI signature changed.
- **Managed-dir resolution gotcha:** `clr_host` looks for the bridge in `ARROWNET_MANAGED_DIR`, else an
  `arrownet/` folder *next to the loaded module*. For the static `duckdb.exe`/`unittest.exe` the module IS
  the exe, so the default lookup is `build/release/arrownet` (next to `duckdb.exe`) — but
  `publish-managed.ps1` lands the bridge in `build/release/extension/mssql_net/arrownet`. So when running
  an exe **directly** you MUST set `ARROWNET_MANAGED_DIR` to that publish dir (symptom otherwise:
  `ArrowNet: failed to load hostfxr from …\build\release\arrownet\hostfxr.dll`). Manual smoke, e.g.:
  `ARROWNET_MANAGED_DIR=…/extension/mssql_net/arrownet build/release/duckdb.exe -unsigned -batch < q.sql`.
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
