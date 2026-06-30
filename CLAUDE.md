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
  error). The **generic names are now live as ADDITIVE ALIASES** (no breakage): `arrownet_query`/`arrownet_exec`/
  `arrownet_functions`/`arrownet_server_info` (+ the existing `arrownet_version`) and `ATTACH … (TYPE arrownet)`
  (the storage extension is registered under both `mssql_net` and `arrownet`) — `test/verify_generic_names.test`.
  The **breaking removal** of the `mssql_net_*` names (+ catalog-type string `"arrownet"`, settings/secret/URI
  scheme rename, compat-corpus regen) remains the separate full-rename pass;
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
  INPUT/OUTPUT deferred); **(4e) attach-time custom C#-authored scalar functions DONE** (`ICatalogScalarFunction`)
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
  BIGINT→INT, error+recover). **(4g-custom) custom C#-authored table-in-out DONE** (now `IArrowInOutFunction` /
  the `StaticInOutFunction` base — in-out analog of 4e/4f): a pure-C# **per-chunk streaming** table-in-out (no SQL object), may keep mutable
  state across chunks (running aggregate); surfaced as `kind='inout'` → C++ `AddInOutFunction` registers a
  bare-name `{TABLE}` entry (`GetOrCreateCustomInOutFunction` + `ArrowNetCustomInOutBind`, output = the fn's
  full declared schema, no input echo), dispatched in C# (`CustomInOut` factory registry → fresh instance per
  session; `CustomInOutSessionImpl` runs `Process(chunk)`). Reuses the 4g operator path — no new ABI/C++
  operator. Verified: `test/verify_custom_functions.test` (cf_tag per-row, cf_running_sum stateful 4999-row
  multi-chunk, per-session state, no SQL object). **(4g-proc) per-row stored-proc in-out DONE**: a discovered
  proc also gets `_each` (`db.s.usp_x_each(<table>)` EXECs the proc once per input row — a proc can't be
  inline-CROSS-APPLY'd). The per-row EXECs run on **DuckDB's pinned transaction** (`BeginWrite`), so the
  proc's writes commit/roll back with **DuckDB's** COMMIT/ROLLBACK — atomic in autocommit AND inside an
  explicit DuckDB `BEGIN`, no per-row commits. **`OperatorFinalize` is NOT used for the commit** (committing
  the in-out's own txn at operator-finish would commit before a user's explicit `ROLLBACK` could undo it; the
  transaction manager is the correct signal). Now on the Phase 6 streaming exchange (`SqlServerProcEach :
  IArrowInOutBinding`, resolved by `InOutBind`; was the 4g push `ProcInOutSessionImpl`, retired in `9056eae`):
  `DoExchange` per row runs `DECLARE @t TABLE(<proc result>); INSERT @t EXEC [s].[p] @param=@p,…;
  SELECT <echoed input>, t.* FROM @t;` on the pinned conn/`_txn` (echo server-side → output = input columns ++
  proc result columns; result-set procs only). Verified: `test/verify_proc_inout.test` (echo output, autocommit
  commit, row-failure rolls back the whole statement, explicit-`BEGIN` read-your-writes + `ROLLBACK` undoes —
  31 assertions). **(4g-finalize) the injected `OperatorFinalize` DONE**: an `OptimizerExtension`
  (`RegisterArrowNetInOutFinalizer`) wraps each in-out `LogicalGet` (identified by `function.in_out_function
  == ArrowNetInOutFunction`) in a pass-through `LogicalExtensionOperator` whose `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 and, in `OperatorFinalize`, calls `holder->Finish()`
  → C# `inout_finish`. This is the reliable single "in-out finished" signal (fires **once**, sink-level, even
  above a parallel UNION — verified empirically + via `MetaPipeline`/executor finish-event scheduling),
  intended as a C# resource-cleanup hook + a clean commit of the read-only TVF's snapshot transaction
  (NOT the proc commit). **4g (table-in-out) is fully complete.**

## Next up (open threads for future sessions)

In-flight / planned refactors (all C#-only unless noted; tests stay green per slice):
- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure.cs` `6da8033`,
  `SqlServerTableValuedFunction.cs` `eb6c34e`). The inline TVF/proc SQL moved out of `SqlServerCatalog`
  into top-level `internal` wrappers (like `SqlServerScalarFunction`/`SqlServerTvfEach`), so
  `ExecuteTable` + `GetFunctionOutputSchema` are now **thin custom/TVF/proc dispatchers**. **Two
  asymmetric shapes** (a real finding, not laziness): `SqlServerProcedure : IArrowTableFunction` (proc
  EXEC has no pushdown → full batches match the bind-time `OutputSchema`, so the `IAsyncEnumerable` +
  `AsyncEnumerableArrowStream` shape is correct); `SqlServerTableValuedFunction` is a **bespoke** wrapper
  (`OutputSchema` property + `ExecuteScan` returning the stream **directly**) — a pushdown source is
  stream-native: `ScanFromSource`'s stream schema already reflects the PROJECTED columns, so it matches
  the projected batches and must NOT be re-wrapped with the full schema (doing so crashed `arrow_ingest`
  with SIGSEGV on `SELECT sq FROM tf_ms(4)`). `ProcResultColumns`/`ProcOutputParams`/`FunctionOutputColumns`/
  `ScanFromSource` widened to `internal`. The **table-function session ABI v29** (`c2e452f`+`1f9fe96`) then
  unified the dispatch under `IBoundTable` (`table_bind`/`table_execute`/`table_close`); see the Phase 5
  section. The bespoke TVF could now fold into `IArrowTableFunction` (`table_execute` returns a stream) but
  needn't — the dispatch is already unified.
- **Load-time global functions = the 4th provider-self-description capability** (**ALL FIVE kinds DONE — global
  SCALAR + IN-OUT + COLLECTOR + TABLE + AGGREGATE, ABI v46; + the host-FS table sub-case DONE, ABI v47**). The
  **host-FS global table** sub-case (secret-backed lakehouse readers) is now built: a global table reader that
  does IO through DuckDB's FileSystem needs the calling operator's opener (`ClientContext`, for secret
  resolution), threaded to the C# binding via **one appended ABI entry `set_active_opener`** — a per-thread
  ambient (`AmbientOpener`, mirroring `set_active_txn`) the host sets in the shared table bind/init hooks
  (`PopulateReturnSchema` + `ArrowStreamInitGlobal`), read by the host-FS binding in `Bind`/`Execute`. NO opener
  param + NO new operator (the v29 table session is reused verbatim). **`arrownet_delta_scan` migrated to a
  pure-C# global host-FS `ITableFunction`** (`DeltaGlobalTableFunction`, Bridge, over engineered-wood +
  `DuckDbTableFileSystem`, declared in `CustomFunctions.GlobalTable`); the bespoke `arrownet_delta.cpp` + the
  `delta_schema`/`delta_scan` ABI entries were **removed** — so a NEW lakehouse format (Iceberg/Lance/…) is added
  with **zero C++** (a pure-C# `ITableFunction` reading `AmbientOpener.Current` + files via the host `fs_*`
  callbacks, declared as a global). `test/verify_delta.test` (60), `test/verify_global_functions.test` (63), full
  SQL function suite unregressed. See [docs/global-functions.md](docs/global-functions.md) §"Host-FS global
  table functions". **The reader STREAMS lazily** (captures the opener — valid for the whole execution — and
  pulls one batch at a time, no materialization) **and pushes the FILTER into engineered-wood file + row-group
  skipping** (`DeltaFilterBuilder` maps the `FilterNode` → `EngineeredWood.Expressions.Predicate`; the
  superset-safe policy lives in the shared C++ `FilterSerializer` gated on `string_order_pushable` — string
  ordering + `BETWEEN` push only for a byte-ordered source. `DeltaGlobalTableFunction.StringOrderPushable=>true`
  (Parquet stats byte-ordered like DuckDB's default), so all comparisons `=`/`<>`/`<`/`<=`/`>`/`>=` + `IN` +
  `BETWEEN` push incl. strings. The SQL catalog scan shares the encoder + flag, so it ALSO pushes string
  ordering/`BETWEEN` under a binary `_BIN2` collation (latent win, `dm_exec`-proven in
  `verify_collation_pushdown`); discovered SQL TVFs stay equality-only (collation-dependent). The C# signal
  rides the `list_global_functions` metadata (a `string_order` column) → `ArrowNetTableFunctionInfo` →
  `bind_data.string_order_pushable`; no ABI bump.
  The predicate drives the Delta file pruner `ReadAllAsync(columns, filter)` AND per-file Parquet row-group
  pruning via `ParquetReadOptions.Filter` on a per-scan `DeltaTableOptions` — no engineered-wood change). Column
  PROJECTION into the Parquet read stays deferred: the shared `BindingBoundTable` wraps the result stream with
  the binding's FULL `OutputSchema`, so a projected subset mismatches it (arrow_ingest SIGSEGV) — DuckDB projects
  above the scan instead (a pushdown-native `IBoundTable` would be needed; small follow-up). See
  docs/filesystem-bridge.md §"Streaming + filter pushdown".
  Connection-free, ATTACH-free functions registered at `Extension::Load`
  via `loader.RegisterFunction`. **Slice 1 built + verified**: `list_global_functions` enumerates the
  provider-union at load + a **`handle==0`** branch on `get_function_*_schema`/`execute_scalar` resolves a
  function by name against the C# `GlobalFunctions` registry; `IBackend.GlobalScalarFunctions` declares them;
  C++ `RegisterArrowNetGlobalFunctions` builds a `ScalarFunction` per scalar decl (shared
  `BuildArrowNetScalarFunction`, handle=0) at load (best-effort — skipped if the bridge can't boot). Demo
  **`arrownet_render(template, params)`** — the **Fluid/Liquid** template engine (secure-by-default,
  parse-once cached); `params` accepts a **DuckDB STRUCT** (`{'name':'x'}`, type-safe) OR a JSON string via the
  **`SQLNULL→ANY` sentinel now wired for scalars** (the scalar builder maps SQLNULL→ANY + marshals the exec chunk
  by its runtime `DataChunk::GetTypes()`, not the declared signature; `Invoke` reads a StructArray or StringArray).
  Resolves as a bare `fn(...)` with NO ATTACH (`test/verify_global_functions.test`;
  validated live via the shell). Unblocks the TMDL render step (render = pure global scalar; apply = table/
  collector). **Full plan: [docs/global-functions.md](docs/global-functions.md)** — covers **all four kinds** (scalar / table
  / in-out / collector) through **one mechanism**: +1 ABI entry `list_global_functions` (enumerate the
  provider-union at load) + a **`handle==0` marker** on the existing *bind* entries (`get_function_*_schema` +
  `execute_scalar`; `table_bind`; `inout_bind`) so the per-call binding resolves against a global registry — the
  returned binding handle is concrete, so `table_execute`/`inout_exchange_open`/etc. are unchanged. **Global
  table + in-out cost ZERO new ABI beyond the scalar entry** (arg-dependent output schema is already solved by the
  v29 `table_bind` / v28 `inout_bind` sessions). C# = a base/derived interface split per kind (`IScalarFunction`
  + `ICatalogScalarFunction` [rename of `IArrowScalarFunction`], same for `ITableFunction`/`IInOutFunction`/
  `ICollectorTableFunction`) + `IBackend.GlobalScalarFunctions`/`GlobalTableFunctions`/`GlobalInOutFunctions`/
  `GlobalCollectorFunctions`; C++ `RegisterArrowNetGlobalFunctions` branches on `kind` at load →
  `loader.RegisterFunction`. Slices: (1) scalar **DONE** — template engine **`arrownet_render`** via **Fluid**
  (Liquid, secure-by-default); (2) in-out/collector **DONE** (pure-C#, **no opener**; demos `arrownet_tag`
  streaming + `arrownet_collect_sum` collector; `inout_bind` handle-0 → C# global registry; reuses the v28
  exchange ABI, no bump — enables the effectful global *apply* half, e.g. `arrownet_apply_tmdl` collector);
  (3) compute/connstr table **DONE** (`table_bind` handle-0 → `GlobalFunctions.ResolveTable` over the v29
  session; the handle-0 `get_function_param_schema` is kind-agnostic via `GlobalFunctions.ParamSchema`;
  `BindingBoundTable` moved to the Bridge; demos `arrownet_seq` fixed-schema + `arrownet_columns` arg-dependent
  schema); (4) aggregate **DONE** (`IAggregateFunction` base + `ICatalogAggregateFunction`; `AggSessionImpl` →
  the Bridge as public `AggregateSession` shared by catalog+global; `agg_open` handle-0 →
  `GlobalFunctions.ResolveAggregate`; `ParamSchema`/`ReturnField` kind-agnostic; shared
  `BuildArrowNetAggregateFunction`; reuses the v25/v26 `agg_*` ABI; demo `arrownet_product` — GROUP BY/parallel/
  OVER); (5) **deferred** host-FS table (secret-backed readers like delta) — needs an **opener arg** on
  `table_bind`, delta stays bespoke until a 2nd such reader.
  Composes with TMDL = render-via-(global)scalar then apply-via-(global)table/collector. The rest of this bullet
  is the original table-case detail.
  Today provider functions are **attach-time catalog-bound** (4e/4f/4g — resolved as `db.schema.fn`, dispatched
  via the catalog `handle`). The deferred **Phase 3-A** alternative is **load-time global** functions
  (connection-free, bare `fn(...)`, registered at `Extension::Load`). **This does NOT break the catalog-only
  concept — the two are orthogonal scopes that coexist**: catalog-bound = needs an ATTACH'd catalog + its
  connection (discovered SQL UDFs/procs/TVFs, custom fns using the catalog's SQL conn); global = connection-free
  (`arrownet_delta_scan(path)`, future `arrownet_iceberg_scan`, lakehouse readers — they belong to no SQL Server
  catalog). **The original objection has dissolved:** Phase 3 deferred global functions to avoid booting the CLR
  at `Extension::Load()`, but the settings refactor (v33) + the fs/delta spike already boot the bridge
  best-effort at load. So global functions are now the natural **4th member of the "provider declares; core
  stays name-agnostic" family** (after settings v33 / ATTACH options v37 / secret fields v38): a
  `list_global_functions(provider)` ABI at load → C++ registers each declared scalar/table function
  **generically** (dispatch to C# by name/`decl_id`), the provider authoring them in C# with **zero per-function
  C++**. `arrownet_delta_scan` is **already a global function** (bespoke `RegisterDeltaScan` in
  `arrownet_delta.cpp`) — proof the scope exists. **Two wrinkles found while scoping the generic build (why it's
  deferred until a 2nd lakehouse format/provider lands, not justified by one function):** (1) **arg-dependent
  output schema** — a global table fn's columns depend on its args (delta's schema comes from the `path`), so the
  generic registration must use the v27/v29 `table_bind`(args→schema+binding) shape, not the no-arg
  `get_function_output_schema`; (2) **the opener vs SQL-connection split** — `table_bind`/`table_execute` pass the
  **catalog handle** to C# (SQL fns use the catalog's `SqlConnection`), but `arrownet_delta_scan` needs the
  **host-FS opener (ClientContext)** for IO, which that path doesn't thread through. So **"build the generic
  global path" and "migrate delta onto it" are separable**: the generic path is cleanest for connection/
  connstr-style global functions; **delta is better kept bespoke** (its host-FS-opener need is special) unless the
  global table-fn bind/execute ABI gains an opener arg (SQL fns ignore it). Build it when the 2nd lakehouse
  format/provider arrives; until then delta stays the hand-written ~60-line `arrownet_delta.cpp`.
- **DAX / ADOMD 2nd provider** (the "one binary, many providers" goal) — **design + slices:
  [docs/dax-provider.md](docs/dax-provider.md)**. **Slice 1 DONE + validated against a live local Power BI
  Desktop instance**: new project `ArrowNet.AnalysisServices` (`DaxBackend : IBackend` provider `"dax"`,
  aliases `adomd`/`powerbi`/`ssas`/`fabric`; `DaxCatalog : IBackendCatalog`; `PowerBiDesktop` port detection).
  `ATTACH 'pbidesktop://' AS pbi (TYPE mssql_net, PROVIDER 'dax')` auto-detects the local msmdsrv port
  (Windows-only, newest workspace's `msmdsrv.port.txt`) → AdomdConnection; `GetMetadata(Schemas)` = model
  name(s) from `$SYSTEM.TMSCHEMA_MODEL` so the model shows as a DuckDB schema. Other targets pass through as
  an ADOMD connstr (SSAS/Fabric/AAS). **No ABI/C++ change — pure C# provider** reusing the catalog/scan/
  function machinery. `BackendRegistry` default is now `ArrowNet.SqlServer,ArrowNet.AnalysisServices` (missing
  assembly skipped; SqlServer stays default → existing ATTACHes unchanged); `publish-managed.ps1` publishes
  both into one `arrownet/` dir (Bridge + SqlClient + `Microsoft.AnalysisServices.AdomdClient` 19.96.1 — the
  plain managed package, **not** `.retail.amd64`). Connection round-trip de-risked via `scratchpad/dax-spike`
  (AdomdClient loads in net10, DMV + `EVALUATE` + `GetSchemaTable` all work). DAX is **read-only** (writes
  throw; BEGIN/COMMIT/ROLLBACK no-op). **Key analysis findings** (from `D:\repos\SqlServerFlights`): schema
  resolution = execute-at-bind + `GetSchemaTable` + stash the reader (NO `TOPN(0)`); params = ADOMD named
  binding ++ DATATABLE injection; **filter pushdown was never implemented** (projection-only); metadata = DMV
  `SELECT`s (NOT the UNION-ALL trick). Cross-platform AdomdClient (Linux/Fabric XMLA) is **Windows-first,
  Linux-TBD**. **Slices 2–3 DONE + validated** (live local model): slice 2 = DMV table/column discovery
  (`TMSCHEMA_TABLES`; columns via `EVALUATE TOPN(0,'T')`+`GetSchemaTable` = real engine types, no TOM-enum
  guessing; `DaxTypeMap` CLR→Arrow incl. `Decimal`→`Decimal128(p,s)`; `'T'[Col]`→`Col`); slice 3 = table
  scan via `EVALUATE SELECTCOLUMNS` projection + **filter pushdown** (`DaxFilterBuilder` wraps the table in
  `FILTER('T', <pred>)`; superset-safe — DuckDB re-applies; string `=`/`IN`/`ISBLANK` push for any type,
  `<>`/range push only for non-string since DAX strings are case-insensitive/collation-dependent; `and`
  drops unpushable children, `or` is all-or-nothing; constants inlined as DAX literals via the Bridge-shared
  `ArrowValueReader`), **TRUE incremental
  streaming** (`DaxArrowStream`, ≤1 batch buffered — validated to **10.5M rows**), a **`system` schema**
  exposing a curated set of VertiPaq/`$SYSTEM` DMVs as tables (`db.system."TMSCHEMA_TABLES"` etc. —
  `DaxCatalog.SystemTables`; bare `SELECT * FROM $SYSTEM.<dmv>`, no pushdown; metadata/scan branch on the
  `system` schema; 14 DMVs validated live), **`daxeval(expression := …, params := …)` function** (slice 4 +
  param binding — under the model schema; evaluates an arbitrary DAX `EVALUATE`/`DEFINE…EVALUATE` query,
  output schema resolved at bind via `GetSchemaTable` no-describe, `DaxEvalBoundTable`, `SupportsPushdown=false`,
  streams; validated ROW/COUNTROWS/SUMMARIZECOLUMNS/full-table. **Registered `kind='proc'`** (not `'table'`)
  so args are NAMED params — that's what allows the optional `params` arg without breaking the no-arg call.
  **`params` accepts EITHER a DuckDB `STRUCT` (`{'a':40,'b':2}`, preferred — type-safe, no quoting) OR a
  JSON string (`'{"a":40}'`, for programmatic callers)** — each field/key bound as an ADOMD `@<name>` param
  the DAX references (`ReadStructParams`/`ParseDaxParams` → `BindDaxParams`, for both the bind probe + each
  execution; args read by field name). **The struct crosses with NO ABI change** via a generic marker: a
  provider declares an "accept any value" named param as the **`NullType` sentinel** → C++ registers a
  `SQLNULL`-typed named param as `LogicalType::ANY` (`GetOrCreateTableFunction`) so DuckDB passes the literal
  UNCAST, and the shared table-bind marshaling keeps the value's **runtime** type for a `SQLNULL`-declared
  param (`ArrowNetTableFunctionBind`) so a `STRUCT` marshals as a real Arrow struct. The guard is
  `SQLNULL`-only → every concrete-typed function is unaffected (full SQL fn suite green). Validated
  numeric/string/filter params, struct + JSON. No ABI change — reuses the proc named-param marshaling + v29
  table session), **`daxevaltable(<input>, expression
  := …)` in-out** (slice 5 — injects the input table as a DAX `DATATABLE` named `_input`, evaluates once,
  `DaxEvalTableBinding`/`DaxDataTable`; this required wiring **cost args (named params) through the shared
  exchange** — `GetOrCreateCustomInOutFunction` declares named params via a tolerant `FetchFunctionParamSchema`
  (empty for cf_tag → unchanged) + `ArrowNetExchangeBind` marshals supplied named params into `inout_bind`
  args, else nullptr (`_each` unchanged); no ABI bump. Whole-table op, but the exchange has no emit-at-end
  hook [finalize drain discards trailing output] — **this single-chunk cap is now LIFTED: `daxevaltable` is a
  [collector](docs/inout-collector-mode.md)** (see below), so an arbitrarily large injected table works
  (validated live to 5000 rows). **The collector table-in-out (pipeline breaker) is BUILT + verified**: a
  second in-out execution shape (a Sink+Source: collect all input, emit at input-EOF) that coexists with the
  streaming exchange, picked by a new additive `kind='collector'`; reuses the v28
  `inout_bind`/`inout_exchange_open` ABI as-is (no bump). C# `IArrowCollectorTableFunction`/
  `IArrowCollectorBinding` (+ `StaticCollectorFunction` base, the `CollectorInOutBinding` adapter); C++
  `ArrowNetCollector*` (in-out `Execute` buffers input into an `ArrowProducer` on the refcounted holder; the
  injected `ArrowNetCollectorPhysical` Sink+Source opens the exchange at Finalize and **streams** the C# output
  — the Source pulls the `ArrowStreamReader` a vector-slice at a time, so **input is fully buffered (inherent)
  but output is never materialized**). SqlServer demo `dbo.cf_collect` (`test/verify_collector.test`, 40 —
  whole-table total, 5000-row multi-chunk, sequential-UNION threads=1, empty, NULLs, prepared re-exec; +50k-row
  streamed-output smoke). **`daxevaltable` migrated onto it**
  (`DaxEvalTableBinding : IArrowCollectorBinding`, `kind='collector'`; reads the whole input into one DATATABLE
  → no 2048 cap; `daxeach` stays streaming `inout`) — validated live against Power BI Desktop
  (`test/verify_dax.test`, 29). In-out regression green: custom 89 / table_inout 63 /
  proc_inout 31 / isolation 17) + **`daxeach(<input>, expression := …)` in-out** (slice 5b — per-input-row
  ADOMD `@<col>` param binding, output = the DAX result per row, no echo; `DaxEachBinding` reuses one
  conn+command across rows, emits per chunk so NO input-size limit; the "each" analog of the SQL `_each`,
  renamed from the old `daxapply`). `verify_dax.test` 25/25. `WHERE`/`ORDER BY`/`LIMIT`,
  aggregation, exact decimals, `DESCRIBE`. **CRITICAL ADOMD GOTCHA (the real root cause):** `AdomdDataReader.Read()`
  called AFTER it already returned `false` (past end-of-data) does NOT return `false` again — it **throws**
  `AdomdUnknownResponseException` ("the server sent an unrecognizable response", `XmlaClient.ReadEndElementS`).
  Unlike `SqlDataReader` it is not idempotent at EOF. DuckDB pulls one batch AFTER the final (partial) one, so
  a batched reader must remember EOF (set `_done` when `Read()` first returns false) and never call `Read()`
  again — one-line fix in `DaxArrowStream`. **This invalidates the earlier (WRONG) "in-process Arrow-import
  interleaving / hosting-topology, must use `AdomdDataAdapter.Fill`/materialize/out-of-process sidecar"
  conclusion** — it was misdiagnosed as "fails on the 2nd chunk." Instrumentation (max-in-flight + per-call
  thread + rows-read) proved `maxInFlight=1` (no parallel access; `MaxThreads()==1` + `get_next` under mutex),
  single thread (no hopping), and the throw on the pull AFTER the last row (read-past-EOF), not the 2nd chunk.
  `Fill` and any tight `while(Read())` loop only "worked" because they stop at the first `false`. **Slice 6
  (Fabric/AAS Entra token auth) DONE + validated live against two Fabric semantic models on one warehouse.**
  ADOMD has no interactive auth in the CLR host, so `DaxTokenAuth` mints a Power BI-scoped token
  (`…/powerbi/api/.default`) and sets `AdomdConnection.AccessToken` (+ `OnAccessTokenExpired` refresh) — the
  principal is the **same azure SP secret the warehouse uses** (reused via the v39 foreign-secret path:
  `ATTACH 'Data Source=powerbi://…;Initial Catalog=<model>' (…, PROVIDER 'dax', SECRET <azure_sp>)` →
  `DaxBackend.BuildConnectionString` carries the secret fields to the catalog via a connstr marker → a
  `ClientSecretCredential`); a secretless remote XMLA endpoint falls back to `DefaultAzureCredential` (the
  "Active Directory Default" analog). New dep `Azure.Identity`. **Also fixed: honor the explicit `Initial
  Catalog`** — a workspace XMLA endpoint lists many models in `DBSCHEMA_CATALOGS` and we were binding to the
  FIRST (e.g. a lakehouse default model), so every DMV/metadata query came back empty; the connection's
  current catalog now wins (auto-discover only when none given). **Storage provenance via system tables
  validated:** `TMSCHEMA_PARTITIONS` (Mode/Type=5 = DirectLake Entity) → `ExpressionSourceID` →
  `TMSCHEMA_EXPRESSIONS.Expression` is the discriminator — `AzureStorage.DataLake(onelake…)` = DirectLake on
  OneLake vs `Sql.Database(…datawarehouse.fabric.microsoft.com)` = DirectLake on SQL (two models on one
  warehouse item). **DAX→SQL bypass PROVEN** (`SELECT count(*) FROM wh.dbo.Trip` via the SQL provider on the
  warehouse SQL endpoint = the DAX model scan, 2,838,927 rows) → a documented (NOT built) **DirectLake
  passthrough** idea: route base-table reads to the cheap SQL endpoint (measures/calc stay DAX), write
  warehouses via SQL / lakehouses via Delta + reframe, optional far-future TMSL model sync — design + honest
  good/bad triage in [docs/dax-provider.md](docs/dax-provider.md) ("DirectLake passthrough"). A follow-on
  **TMDL model-management** note (same doc) covers retrieve/apply a model definition via TOM: the
  generate-vs-apply split (pure scalar `render_tmdl` vs effectful table-function `apply_tmdl` — never a
  side-effecting scalar, optimizer purity) + bind-constant-vs-table-argument (dynamic per-row apply = the
  in-out `_each` form, fixed bind-time output schema) + three apply shapes: `apply_tmdl` (TVF, one commit) /
  `apply_tmdl_each` (in-out, N serialized commits) / **`apply_tmdl_agg` (4h aggregate — collect many
  fragments, ONE atomic commit at finalize; side-effect-safe since the effect is in Finalize, run once
  single-threaded)**. Then the **generic rename**
  (`arrownet_query`/`_exec`, catalog-type `"arrownet"`) + `BackendRegistry` multi-provider polish are due.
- **Multi-edition support** (Synapse / Fabric Warehouse / Lakehouse SQL endpoint) — **design:
  [docs/warehouse-support.md](docs/warehouse-support.md)**. **Slices 1–4 DONE + validated end-to-end against
  a real Fabric Warehouse** (edition 11, `BIN2_UTF8`): (1) `ServerProfile` (`ServerProfile.cs`) detected
  lazily on first connection via a **non-MARS probe** (so Fabric/Synapse, which reject a MARS connection, are
  classified before the MARS decision); **MARS gated on `profile.SupportsMars`** (the connection only works on
  Fabric because of this); (2) `mssql_server_info(catalog)` diagnostic (`test/verify_server_profile.test`);
  (3) **profile-driven `MapArrowToSqlType`** — `NVARCHAR`→`VARCHAR` by `HasNVarchar`, `datetime2`/`time` scale
  by `MaxDateTime2Scale`, tz→`datetimeoffset`|UTC-`datetime2` by `HasDatetimeOffset`; box-preserving; CTAS to
  Fabric verified (`varchar(MAX)`+`datetime2(6)`, µs round-trip; **Fabric accepts `varchar(MAX)`**). Read +
  write paths (incl. `SqlBulkCopy`) both confirmed working on Fabric. (4) **connection mode** (C#-only, no ABI):
  `mssql_mars` tri-state provider setting (`auto`=`profile.SupportsMars` | `true` | `false`) resolved
  once at first connection; **MARS-off data SCANS take a fresh pooled connection** (no read-your-writes for
  scans in a write txn — `ExecuteQuery` gates the pinned-read branch on `_marsEnabled`). **Metadata reads are
  exempt** (`ExecuteMetadataQuery`, read-your-writes regardless of MARS): they're short (no held reader) and
  the pinned conn carries no concurrent scan on MARS-off, so a just-CREATEd table's column/rowid re-fetch sees
  the uncommitted `CREATE` on the pinned connection — without this the self-healing cache evicts the new table
  on Fabric (same-session `CREATE`+DML failed). **Fabric write transactions run at
  SNAPSHOT** (`ServerProfile.DefaultWriteIsolation`, edition-11-only — box/Synapse keep the server default).
  `mssql_mars` is **global** (`SET` before ATTACH); a per-catalog `mars` ATTACH option is now straightforward
  to add (the ATTACH-options→C# refactor landed, ABI v37 — `SqlServerCatalog` parses the options JSON; just
  add a `mars` key alongside `schema_filter`/`table_filter`/`isolation_level`). **Caveat:** `RESET` of an
  extension option does NOT fire its set-callback
  (`config.cpp ResetOption`), so it never clears the process-global `ProviderSettingsStore` — restore a setting
  with `SET name='<default>'`, not `RESET` (matters for `.test` hygiene across files). Verified:
  `test/verify_connection_mode.test` (20). (5) **collation-aware string `ORDER BY` pushdown** (no ABI):
  `FetchBinaryCollation` (reads the `SERVER_INFO` profile) caches a flag on the catalog at `LoadCatalog` →
  scan `bind_data.string_order_pushable` → `arrownet_optimizer` gate `is_string && !string_order_pushable`;
  string keys push only on a binary (`_BIN`/`_BIN2`) collation (byte-order sort == DuckDB).
  `test/verify_collation_pushdown.test` + `test/verify_orderby_pushdown.test`. (6) **JSON read-side gate**
  (C#-only): a SQL `json` column is tagged `arrow.json` in `SqlArrowMapping.ToArrowField` → DuckDB imports it
  as the `JSON` logical type (unregistered-extension fallback = `VARCHAR`, so it's safe + round-trips); the
  core `json` extension is **statically embedded** (`extension_config.cmake`) so the test binaries have the
  `JSON` type + functions (this build is v0.0.1 → json can't be autoloaded). `test/verify_json.test` (`require
  json`). **The box test DB is now SQL Server 2025** (major 17, native `json`). **§3.4 granular-types investigated
  (2026-06-24) → write-side DEFERRED** (`docs/warehouse-support.md` §3.4.1): `arrow_lossless_conversion` toggles
  an Arrow extension rep for 6 types (`BOOLEAN`→`arrow.bool8`/Int8, `HUGEINT`, `UUID`, `TIME_TZ`, `BIT`, `JSON`).
  The **read path** (C#-authored Arrow) is the principled, low-risk half — JSON read-side + UUID read-side done
  (`uniqueidentifier`→`FixedSizeBinary(16)`+`arrow.uuid`→DuckDB `UUID`, big-endian RFC-4122 bytes; scale-aware
  `time(7)`/`datetime2(7)`→ns; decimal already `(p,s)` — `verify_granular_types.test`). The
  **write path** flip is high blast radius (`ColumnAppender` has no `Int8` case → would corrupt every BOOLEAN
  write; also filter/UPDATE/DELETE value readers) AND **low warehouse value** (Fabric has no native `json`, so
  `varchar(MAX)` is already correct + the only option there; native-`json` write only helps box-2025/Azure where
  `nvarchar(max)` already holds the JSON). Recommendation: keep the STANDARD boundary; revisit via field-metadata
  injection or a json-column-indices ABI arg only if a box-2025/Azure target needs it (or with the DAX provider).
  (6) **tz validated (3b) — naive↔naive**: with `icu` statically embedded (`extension_config.cmake`), validated
  under a non-UTC session zone (`America/New_York`) that a `TIMESTAMPTZ` preserves its instant (stored UTC
  `datetimeoffset`, re-displayed in the session zone) and a naive `TIMESTAMP` is unshifted; no code change
  needed. The reinterpret-as-session-local semantic is deliberately NOT adopted. `test/verify_timezone.test`.
  **Warehouse read-side is now complete**; the only remaining warehouse item is write-side rich types (lossless
  flip). (`mssql_default_varchar_length` — done via the settings refactor;
  applies to all created text columns.) A `ServerProfile`
  (EngineEdition + product version + DB collation) detected at OpenCatalog drives connection behavior
  (no MARS → pooled reads + snapshot, see [docs/transactions.md](docs/transactions.md)) AND type mapping
  (no `NVARCHAR` → `VARCHAR`; no `DATETIMEOFFSET` → UTC `datetime2(6)`; `datetime2` scale ≤ 6; native
  `json` on 2025+). Collation is the principled `VARCHAR`/`NVARCHAR` driver (`_UTF8`) + gates string
  `ORDER BY` pushdown (`_BIN2`); the doc records the **no-universal-collation** cross-stack reality
  (DuckDB/SQL-endpoint = CS binary vs DAX/Vertipaq = CI). New `mssql_default_varchar_length` setting
  (length policy, separate from the varchar/nvarchar choice; existing `mssql_ctas_text_type` is the
  blunt whole-string escape hatch). Open: the naive-`datetime2` reinterpret-as-session-local semantic.
- **Settings refactor** (provider-declared, C#-accessible settings) — **design:
  [docs/settings-architecture.md](docs/settings-architecture.md)**. **Mechanism DONE (ABI v33, steps 1–3,
  behavior-preserving)**: a provider declares its settings in C# (`IBackend.Settings` → `ProviderSetting`),
  the host registers them as DuckDB extension options **at extension load** via the generic `list_settings`
  ABI (booting the bridge best-effort at load), and value changes **push** to C#'s `ProviderSettingsStore`
  via `set_setting` from a **per-slot trampoline** set-callback (`SetTrampoline<I>` — DuckDB's set-callback
  carries no setting name + fires before the store, so one generic callback can't work). The provider-agnostic
  core no longer names a setting; `RegisterCompatSettings` + the hardcoded `mssql_*` list are gone. Min-validation
  preserved (now names the setting). **`mssql_default_varchar_length` DONE** (the original motivator): declared
  in C#, read from `ProviderSettingsStore` inside `MapArrowToSqlType` (no ABI param), bounds **all** created
  text columns incl. CTAS/COPY (`NVARCHAR(n)`/`VARCHAR(n)` vs `(MAX)`); `mssql_ctas_text_type` whole-type
  override still wins (`test/verify_default_varchar_length.test`, 19). **Step 4 cutover — `ctas_text_type`
  DONE (ABI v34)**: `MapArrowToSqlType` reads `mssql_ctas_text_type` from the store; the `text_type` param is
  dropped from `create_table` across C#/ABI/C++ (proving a per-setting param can be removed); it now applies
  to CTAS/COPY too, not just explicit CREATE (closing the old gap). The C++11 trampoline array was also
  hardened (hand-rolled `IndexSeq` replacing `std::make_index_sequence`). The `isolation` entanglement is now
  resolved: the **ATTACH-options→C# refactor (ABI v37) landed** — the per-catalog `isolation_level` ATTACH
  option lives on the C# `SqlServerCatalog`, and in-out isolation resolves in C# (`mssql_isolation_level`
  setting ?? the catalog's `isolation_level`), so no global store holds a per-catalog value (see
  [docs/provider-extensibility.md](docs/provider-extensibility.md) §3). Replaces the old "hardcode `mssql_*` in
  C++ / read in C++ / pass each value
  through an ABI method param" model (O(settings × providers) churn): **net ABI reduction** — two generic
  entries replace the per-setting params. Trade-offs: boot the CLR at extension
  load (needed for `SET` before first ATTACH; aligns with Phase-3 load-time functions) + catalog/provider
  scope (not session-local). **Directly unblocks `mssql_default_varchar_length`** (C# reads the length from
  `Settings`, no `begin_bulk`/`create_table` signature changes). Prerequisite for the 2nd provider. The same
  provider-declared pattern also covers **ATTACH options** (DONE, ABI v37 — `open_catalog(options_json)`;
  C# parses `schema_filter`/`table_filter`/`isolation_level`, filtering applied in `get_metadata`,
  `PROVIDER`/`SECRET` stay C++ meta-options) and **secret fields** (DONE, ABI v38 — `list_secret_fields`;
  C# declares `SecretType`/`SecretFields`, C++ `RegisterProviderSecrets` registers the type + params
  generically, validation in `BuildConnectionString`). **All three flavors are built; the `mssql` provider is
  fully self-describing** — **design:
  [docs/provider-extensibility.md](docs/provider-extensibility.md)** (the unified "provider declares; core
  stays name-agnostic" model).
- **Plugin system (third-party backends + global functions)** — **default-context SPI BUILT + verified; ALC
  isolation deferred: [docs/plugin-system.md](docs/plugin-system.md)**. A plugin dropped into an
  **`ARROWNET_PLUGIN_DIR`** folder is discovered at load (`BackendRegistry.ScanPluginDirectories`), its
  `IBackend`(s) registered + global functions surfaced as bare `fn(...)` with NO ATTACH — no ABI/C++ change (the
  scan runs in `Discover()` before the `list_global_functions` union). Demo `ArrowNet.SamplePlugin`'s
  `plug_greet` (`test/verify_plugin.test`). **Key finding: plugins load into the BRIDGE's ALC**
  (`AssemblyLoadContext.GetLoadContext(typeof(BackendRegistry).Assembly)`), NOT `Default` — hostfxr loads the
  bridge into a non-default context, so loading into Default bound the plugin to a separate `ArrowNet.Bridge`
  copy (different, non-assignable `IBackend` → 0 backends). The loader skips host-context-loaded assemblies (the
  shared set) + a `Resolving` hook probes plugin dirs for private deps. **Plugins must align their full
  dependency closure with the host (Apache.Arrow always)** — no version isolation without ALC. **The contract
  assembly `ArrowNet.Abstractions` is extracted** (the `I*Function`/`IBackend`/`IBoundTable`/`IAggregateSession`
  interfaces + `ProviderSetting`/`SecretField`/`TableFunctionScan`/`ScanSpec`/`FilterNode`, kept in the
  `ArrowNet.Bridge` namespace — assembly split only, zero source churn; Bridge references it, the
  ABI/marshaling/`BackendRegistry`/Static-bases/adapters stay in Bridge). `ArrowNet.SamplePlugin` references
  **Abstractions only** (+ Apache.Arrow) — Bridge-independent. Per-plugin `AssemblyLoadContext` isolation (for
  conflicting deps) is a deferred, non-breaking loader-internal upgrade. **Crux for that day:
  `Apache.Arrow`(+`.C`) MUST be SHARED (default context), never isolated** — every cross-boundary call traffics
  Arrow types, and cross-ALC types aren't assignable, so all plugins pin the bridge's Arrow version (isolation
  frees their OTHER deps only). The one fix over the textbook sketch: the `PluginLoadContext.Load` must return
  null for an explicit **shared-name allowlist** (`ArrowNet.Abstractions` + `Apache.Arrow`/`.C`) BEFORE the
  resolver, else `AssemblyDependencyResolver` loads an isolated Arrow copy and breaks everything. Clean shape:
  extract a thin shared **`ArrowNet.Abstractions`** (interfaces + Arrow-typed contracts) + non-collectible
  per-plugin ALCs, additive beside the default-context first-party providers (which gain nothing from isolation).
  Adopt isolation only when a real dependency conflict / third-party plugin lands.

## Implementation status (current)

**Phases 1–2 complete + streaming bulk write; verified against real SQL Server on DuckDB v1.5.4.**

Implemented and verified:
- **ATTACH + catalog**: schemas/tables/views, three-part naming, cross-catalog joins; `schema_filter`/
  `table_filter` (case-insensitive regex); ATTACH-time connection validation (no orphan catalog on
  failure); `mssql://` URI; `CREATE SECRET (TYPE mssql_net, …)` incl. Azure Entra/Fabric auth.
- **Read path** fully in C# behind `get_metadata`/`scan_table` ABI calls — **C++ has zero T-SQL**.
- **Pushdown**: projection (by-name), filter (best-effort via `pushdown_complex_filter`, never erases →
  DuckDB always re-applies; superset-safe shapes only), bare `LIMIT` (`TOP n`), `ORDER BY`+`LIMIT`
  (TopN, gated: NULL-order compatible, no pushed filter, and **string keys only under a binary database
  collation** — `ArrowStreamBindData::string_order_pushable`, set at scan bind from
  `ArrowNetCatalog::StringOrderPushable()`, which `LoadCatalog` caches via `FetchBinaryCollation` reading
  the `ARROWNET_META_SERVER_INFO` profile; binary `_BIN/_BIN2` collation sorts bytewise == DuckDB. No ABI.
  `test/verify_collation_pushdown.test`).
- **Statistics → optimizer**: cardinality (row count from `sys.dm_db_partition_stats`) + per-column NDV
  (leading-column histogram). **min/max deliberately NOT reported** (DuckDB prunes filters on min/max →
  stale SQL Server stats could drop rows; NDV is costing-only so stale is safe).
- **rowid** from PK / smallest unique index (scalar + compound STRUCT) → enables UPDATE/DELETE.
- **Time travel** (`FROM cat.t AT (TIMESTAMP => ts)`) → SQL Server temporal tables `FOR SYSTEM_TIME AS OF`
  (`eeae2e2`). The AT clause is a **bind-time, per-table-reference constant** (not per-scan pushdown), so it
  flows through the binding: `ArrowNetCatalog::SupportsTimeTravel()→true` (else the binder rejects it with
  "Catalog type does not support time travel" before the scan), `ArrowNetTableEntry::GetScanFunction(EntryLookupInfo)`
  reads `lookup_info.GetAtClause()` {unit,value} onto `ArrowStreamBindData` (the basic + lookup overloads share
  `BuildScanFunction`), `BuildScanSpec` folds it into the existing `spec_json` (`"at":{unit,value}` — **no new
  ABI**), and C# `ScanFromSource` emits `FOR SYSTEM_TIME AS OF @__at` (a datetime2 param). `AT (VERSION => …)`
  (an Iceberg/Delta snapshot-id notion) has no SQL Server equivalent → a clean "not supported" error (no silent
  current-data result). Verified: `test/verify_time_travel.test` (14 — current/future/past + a
  `dm_exec_query_stats` `FOR SYSTEM_TIME AS OF` proof + the VERSION error).
- **DML**: INSERT (+ INSERT…SELECT, + RETURNING via `OUTPUT INSERTED.*`), UPDATE, DELETE (rowid-based,
  parameterized). INSERT/CTAS/COPY use a **streaming bulk path** (see below).
- **DDL**: CREATE/DROP TABLE, CREATE/DROP SCHEMA, ALTER TABLE (rename table/column, add/drop column,
  change type, SET/DROP NOT NULL, SET/DROP literal DEFAULT); PRIMARY KEY/UNIQUE/literal DEFAULT on CREATE.
  **On a warehouse profile (Fabric/Synapse) PK/UNIQUE are emitted as `NONCLUSTERED NOT ENFORCED` via a
  separate `ALTER TABLE ADD CONSTRAINT`** (inline-in-CREATE is rejected, error 24584); they're hints (not
  enforced) but appear in `sys.indexes`, so they seed rowid discovery → **UPDATE/DELETE work on Fabric**
  (validated 2026-06-24). Box keeps the inline form. See [docs/warehouse-support.md](docs/warehouse-support.md) §3.5.
  **`mssql_default_table_type`** (`''` rowstore | `clustered columnstore`/`cci`): on box/Azure SQL, CREATE/CTAS
  emit an inline `INDEX [cc_<schema>_<table>] CLUSTERED COLUMNSTORE` (PK/UNIQUE forced `NONCLUSTERED` — the
  columnstore is the clustered index); no-op on Fabric/Synapse (columnstore implicit). §3.6,
  `test/verify_columnstore.test`.
- **Transactions**: BEGIN/COMMIT/ROLLBACK with a pinned connection (lazy on first write); reads inside
  the txn use it too (read-your-writes); MARS forced so a scan reader + DML coexist. **Full design:
  [docs/transactions.md](docs/transactions.md)** (autocommit = implicit per-statement txn; the three
  lazy levels — DuckDB `BeginTransaction` always / extension `StartTransaction` on catalog touch / C#
  connection-pin on first write; MetaTransaction fan-out + one-writer rule; why MARS, and the exchange's
  deliberately MARS-free serialized connection; the `INSERT…SELECT` pin-timing race; per-row proc `_each`
  on DuckDB's pinned txn).
  - **Per-DuckDB-transaction connections (write concurrency, ABI v35) — DONE + validated.** The pinned
    connection is now **per `global_transaction_id`**, not a single shared one: C# keys connection state by a
    `ConcurrentDictionary<long, TxnState>`, and the active id rides a per-thread `AmbientTransaction` set by a
    new `set_active_txn(handle, txn_id)` ABI entry that the host calls immediately before each
    connection-using call (same thread, synchronous); `begin_bulk`'s old `autocommit` arg became `txn_id`
    (the bulk runs on a background thread so the id is captured + re-established by the consumer). C++ sources
    `MetaTransaction::Get(context).global_transaction_id` (`ArrowNetTransaction::txn_id_` for lifecycle;
    `arrow_ingest` `ArrowStreamInitGlobal` centrally for all scans/read-your-writes; the DDL/DML/exchange/
    `FetchTableColumns`/`mssql_net_exec` callsites via `catalog/arrownet_txn_util.hpp`'s `ArrowNetSetActiveTxn`).
    So concurrent DuckDB transactions (e.g. **dbt `--threads N`** building several models at once) each get
    their OWN provider connection instead of colliding on one non-thread-safe `SqlConnection` (was error
    **595**). Matches the native `mssql-extension`'s per-`MSSQLTransaction` connection. **Validated: `dbt run
    --threads 4` PASS=4/4 on box (4×200k concurrent CTAS) AND Fabric (no MARS); `verify_*` 30/30.** Design +
    the abandoned Option A (dbt uses explicit txns, not autocommit — so an autocommit-detection fix never
    fired): [docs/transaction-concurrency.md](docs/transaction-concurrency.md). Harness:
    `dbt_mssql_test/` (gitignored — holds live SP creds, never commit). It has THREE targets: `box` (local SQL
    Server), `fabric` (Fabric **Warehouse** via the SQL endpoint), and `lakehouse` (Fabric **Lakehouse** via the
    **Delta** provider on OneLake — the `mssql` catalog is a Delta folder-catalog, not a SQL endpoint). The
    lakehouse target can't use dbt-duckdb's profile `attach:` (its renderer can't emit `READ_ONLY false`, which
    OneLake REQUIRES — DuckDB bumps a remote `abfss://` ATTACH to read-only under AUTOMATIC); instead a tiny
    dbt-duckdb **plugin** (`dbt_mssql_test/plugins/onelake_attach.py`) ATTACHes `mssql` writable in
    `configure_connection` (runs per connection, AFTER the profile `secrets:` create `fabric_sp` and BEFORE dbt's
    per-connection schema creation — so all of dbt's cursors see the catalog). Uses `TYPE mssql_net` (the loadable
    registers that storage-extension name; `arrownet` is a shell-only alias) + `PROVIDER 'delta'`. **CRITICAL —
    point it at an EMPTY lakehouse** (validated against the flat `LH_no_schema`, schema `main`): dbt runs
    `information_schema.tables` before building, which scans the **WHOLE `mssql` catalog** (the
    `WHERE table_schema=…` filters AFTER), and our catalog **materializes every table during enumeration**
    (`FetchTableColumns` → a `_delta_log` read per table over OneLake). Against the populated `LH` (10 tables incl.
    a 10M-row one) that effectively HANGS — even when the target schema is empty, because the scan still touches
    every other table. Against the empty `LH_no_schema` a single-model build is ~11s and **`dbt run --threads 4`
    is PASS=4/4** (4 concurrent CTAS → 4 separate `Tables/<model>` Delta tables, ~19s — validates the parallel
    OneLake bulk-write path, same as box/fabric). (A lazy table-enumeration that doesn't fetch columns until
    needed would fix the populated-catalog slowness generally — deferred.) Per-target
    schema via `+schema: "{{ target.schema }}"` (box/fabric `dbo`, lakehouse `main`). **The loadable extension
    must be rebuilt on an
    ABI bump** (`cmake --build … --target mssql_net_loadable_extension`) — dbt loads the loadable, not the
    static `unittest`/`duckdb.exe`, so a stale loadable vs a freshly-published bridge throws
    `Bootstrap.Initialize returned 2` (ABI mismatch).
  - **dbt pre/post hooks — behavior + limitations: [docs/dbt-hooks.md](docs/dbt-hooks.md)** (validated box +
    Fabric). Highlights: an **in-transaction post-hook error rolls back the model's CREATE on BOTH box AND
    Fabric** (Fabric Warehouse supports transactional DDL rollback — unlike Snowflake). SQL-Server-specific
    DDL in a hook (index/PK/UNIQUE) must call `mssql_net_exec`. A **default in-txn** post-hook touching the
    model via `mssql_net_exec` now runs **atomically with the model** (ABI v36 join-only: the exec runs on the
    model's own pinned connection — box: model + index in ~0.3s; previously a 30s self-block). `transaction:
    false` still works (model commits first; non-atomic post-processing). Fabric **`CREATE INDEX` is
    unsupported** (`22424`) — a provider limitation no hook can avoid (the in-txn form then rolls the model
    back with it).
  - **dbt incremental models — [docs/dbt-incremental.md](docs/dbt-incremental.md)** (validated box + Fabric).
    Concurrent **incremental append** (`incremental_strategy='append'`) works at `--threads 4`, and
    **concurrent schema evolution** (`on_schema_change='append_new_columns'` → `ALTER ADD COLUMN`) now works
    at `--threads 4` too (~0.5s/model). It **used to deadlock** at `--threads > 1`: our `ALTER` evicted the
    cached entry, so the next bind (in a different transaction, no pinned connection) re-fetched columns
    (`SELECT * FROM <model> WHERE 1=0`) on a **pooled** connection that blocked `LCK_M_IS` on the ALTER's
    still-uncommitted Sch-M lock → 30s timeout → re-eviction → "Table does not exist" (captured via
    `sys.dm_os_waiting_tasks`). **Fix (C++-only): `ArrowNetSchemaEntry::Alter` re-fetches the columns
    EAGERLY on the model's OWN connection** (which owns the Sch-M lock → read-your-writes, no block) and
    caches them, so the later bind finds the entry cached and never issues the blocking pooled re-fetch.
    Since that cached entry reflects the uncommitted schema, **`RollbackTransaction` calls
    `ArrowNetCatalog::InvalidateAllEntries()`** (drops materialized entries, keeps name lists for lazy
    re-fetch) so a rolled-back ALTER leaves no stale schema (verified). Same family as the post-hook
    join-only fix — keep in-transaction work on the transaction's own connection.
- **Functions**: `mssql_net_query` (raw scan), `mssql_net_exec` (raw exec) — both accept a connstr, a
  secret name, OR an attached-catalog name; `mssql_refresh_cache`/`mssql_invalidate_cache` (+ `_net_`
  aliases, arities 1/2/3); `mssql_version()`; `arrownet_managed_dir()` / `arrownet_test_scan()` /
  `mssql_server_info(catalog)` (diag — the latter surfaces the detected `ServerProfile`).
- **Cache invalidation after DDL via `mssql_net_exec`**: DDL detection in C# (`SqlDdl.MayChangeSchema`),
  gated by `SET mssql_exec_invalidate_cache` (default false, Postgres-scanner parity).

Compat suite: ~96/122 of the C++ mssql-extension tests pass (corpus regenerated from upstream via
`scripts/gen_mssqlcompat_tests.sh`, lives in `test/mssqlcompat/`, gitignored). Remaining failures are
non-data: error-WORDING/number assertions (corpus expects native-extension text), C++-only surfaces
(`mssql_pool_stats`/`mssql_open` diagnostics, krb5 connstr parser), COPY-to-temp-table empty-schema
syntax, and catalog-after-rollback staleness.

**Not yet / out of scope:**
**load-time global** functions
(Phase 3 — scalar UDFs, TVFs, stored procs + custom C#-authored scalar, table, table-in-out & aggregate
functions + discovered-TVF & per-row-proc table-in-out + the OperatorFinalize cleanup signal all done, see
"Callable scalar UDFs (4b)" / "table functions (4c)" / "stored procedures (4d)" / "custom functions (4e scalar,
4f table)" / "table-in-out (4g — incl. custom C# in-out, per-row procs, OperatorFinalize)" / "aggregate
functions (4h — custom C# UDAF, GROUP BY + parallel + window)"; proc
multi-result-set + INPUT/OUTPUT + OUTPUT-param-only `_each` still deferred; a custom aggregate `window`
callback is deliberately NOT implemented (DuckDB's segment-tree path drives our combine/finalize — cheaper for
a marshaled bridge); aggregate disk-spill is **opt-in** per aggregate (`SupportsSpill` → bytes-in-blob, 1 KB
state cap; default is fast in-C# state, no spill) — `serialize`/`deserialize` for variable/unbounded state and
distributed-plan serialization stay deferred;
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
  schema, func, out)` + `get_function_return_schema(…)` (each fills a bare `ArrowSchema *out` giving the
  arg/return `LogicalType`s, read via `ReadArrowSchema` — was a zero-row stream until **v32**) +
  `execute_scalar(handle, schema, func, args, out)` (runs the UDF over an N-row arg batch; consumes `args`).
- **ABI v20/v21** entries (table functions): `get_function_output_schema(handle, schema, func, out)`
  (a bare `ArrowSchema` = the TVF's output columns, **v32**) + `execute_table(handle, schema, func, args, spec_json,
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
- **ABI v25** entries (custom aggregates, 4h): `agg_open(handle, schema, func, *out_session)` (opens a managed
  session = a `ConcurrentDictionary<id, accumulator>`; closed via `agg_close`) + `agg_update(session, batch)`
  (`batch` = `[int64 state_id ++ params]`, N rows; C# groups by id + folds each group) + `agg_combine(session,
  batch)` (`batch` = `[int64 target_id, int64 source_id]`; merges source→target per row) + `agg_finalize(session,
  ids, out)` (`ids` = `[int64 state_id]`; `out` = one result column in id order) + `agg_destroy(session, ids)`
  (drops those states — bounds memory for the window paths; best-effort) + `agg_close(session)` (frees the
  session; best-effort). Arg/return schemas reuse `get_function_param_schema`/`get_function_return_schema`. See
  "Callable aggregate functions (4h)" below.
- **ABI v26** entries (spillable aggregates, 4h opt-in): `agg_update_spill(session, group_states, batch, out)`
  (`group_states` = BLOB[G] current state per distinct group, `batch` = `[int64 slot ++ params]`; `out` =
  BLOB[G] new state) + `agg_combine_spill(session, target_states, batch, out)` (`target_states` = BLOB[G]
  distinct targets, `batch` = `[int64 slot, BLOB source]` — a target may repeat, e.g. the window segment-tree
  merges several nodes into one frame state; `out` = BLOB[G] merged) + `agg_finalize_spill(session, states,
  out)` (`states` = BLOB[N]; `out` = one result column). For a spillable aggregate the per-group state is
  serialized into a fixed, pointer-free state blob (`[uint32 len][byte data[ARROWNET_AGG_SPILL_CAP]]`, cap =
  1 KB) so DuckDB's external GROUP BY spills it; state crosses as an Arrow BLOB column (NULL row = fresh).

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

### Function-abstraction refactor (Phase 5 — done)
A DuckDB-faithful `Bind`/`Binding` model + expressing SQL-Server functions as the authoring interfaces.
Complete: scalar wrappers, arg-dependent table output schema, in-out, the TVF/proc wrapper extraction, the
v29 table-function session, and the v30 removal of the dead `execute_table`/`execute_proc`.
- **Scalar — DONE** (`refactor(scalar)`, commit `60ea6f0`): discovered SQL UDFs are a `SqlServerScalarFunction
  : ICatalogScalarFunction` (in `SqlServerScalarFunction.cs`) — `ResolveScalar` returns a custom registry entry
  or the wrapper as the base `IScalarFunction`, so `ExecuteScalar`/param/return-schema dispatch through ONE path. The
  chunked `SELECT [s].[f](@..) UNION ALL` (≤2100-param cap) moved into the wrapper's single-batch `Invoke`; the
  per-cap sub-queries merge into one column via a typed builder (no `ArrowArrayConcatenator`). C#-only, no ABI.
- **Table `Bind` — DONE** (`feat(table)`, commit `85de4df`, **ABI v27**): `IArrowTableFunction.Bind(RecordBatch
  args) → IArrowTableFunctionBinding { OutputSchema; SupportsPushdown; Execute(TableFunctionScan) }` — a custom
  TVF's **output schema may depend on its constant args** (the gap before: a static `OutputSchema` property).
  `get_function_output_schema` gained a nullable `args` 1-row stream (the C++ table bind marshals the args once
  for both the output-schema resolution and the scan; the in-out `_each` base lookup passes null). A
  `StaticTableFunction` base keeps fixed-schema functions trivial (`cf_range`). Demo `dbo.cf_columns(n)` returns
  `n` columns `c1..cn` — schema resolved from the arg at bind (`verify_custom_functions.test`).
- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure.cs` `6da8033`,
  `SqlServerTableValuedFunction.cs` `eb6c34e`): the inline TVF/proc SQL (the `ExecuteTable`/`ExecuteProc`/
  `GetFunctionOutputSchema` branches + `FunctionOutputColumns`/`ProcResultColumns`/`ProcOutputParams`/
  `ScanFromSource`, the last four widened to `internal`) moved into top-level `internal` wrappers, leaving
  `ExecuteTable` + `GetFunctionOutputSchema` as **thin custom/TVF/proc dispatchers**. `SqlServerProcedure :
  IArrowTableFunction` (proc EXEC has no pushdown → full batches match the bind-time `OutputSchema`, so the
  `IAsyncEnumerable`/`AsyncEnumerableArrowStream` shape is correct); but `SqlServerTableValuedFunction` is a
  **bespoke** wrapper (`OutputSchema` property + `ExecuteScan` → the `ScanFromSource` stream **returned
  directly**) — a pushdown source is stream-native, its stream schema IS the PROJECTED schema (matching the
  projected batches; re-wrapping it with the full schema crashed `arrow_ingest` — SIGSEGV on a column-subset
  projection `SELECT sq FROM tf_ms(4)`). C#-only (`execute_table`/`execute_proc` unchanged). Verified: full
  function suite green.
- **Table-function session — DONE** (ABI v29: `c2e452f` surface + `1f9fe96` C++ rewire): `table_bind`
  (resolve a per-plan binding → output schema/return types + `supports_pushdown` + an opaque handle) /
  `table_execute` (run the scan, per execution) / `table_close` (free the binding at plan teardown), the
  session-handle successor to `get_function_output_schema`+`execute_table`/`execute_proc` in the table scan.
  C++ `ArrowNetTableFunctionBind` uses them; the `is_proc` **execute** branch is gone (`table_execute`
  unifies TVF/proc/custom — C# `SqlServerCatalog.TableBind` classifies + returns an `IBoundTable`:
  `TvfBoundTable` (SQL pushdown) or `BindingBoundTable` (proc positional / custom by-name)). `push_projection`
  = the binding's `supports_pushdown` (= `!is_proc`, behavior-preserving; `is_proc` survives only for the
  named-vs-positional arg marshaling). The binding is **reused across (prepared) re-executions** — proven by
  a `PREPARE`/`EXECUTE`-twice test (R2); `SqlServerTableValuedFunction.ExecuteScan` no longer consumes its
  args, and the per-execution connection lives in `table_execute`'s stream (released by the arrow scan), so
  the refcounted `TableBindState` only frees binding metadata at teardown (no `arrow_ingest` hook needed).
- **`execute_table`/`execute_proc` removed — DONE** (ABI v30, `8e2a194`): unused in C++ since v29, so the 2
  vtable entries + their `clr_host` wrappers + the Bootstrap handlers/assignments + the `Abi.cs` delegates +
  the `IBackendCatalog`/`StubBackend`/`SqlServerCatalog` methods are gone. A **mid-struct** removal (shifts
  every later entry's offset), so `abi.h` + `Abi.cs` field order stay in exact sync; the function suite is
  the alignment gate. **Optional remaining**: fold the bespoke `SqlServerTableValuedFunction` into
  `IArrowTableFunction` now that `table_execute` returns a stream (organizational — the dispatch is already
  unified under `IBoundTable`). Full design: the plan file's "Phase 5".
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
  a proc ⇒ OUTPUT params (`ProcOutputParams`) + `return_value` if any, else its first result set via
  **`sys.sp_describe_first_result_set`** (over `EXEC [s].[p] @a=@a,…` with the input params declared NULL;
  `system_type_name` used directly). **Uses the sp, NOT the `dm_exec_describe_first_result_set_for_object`
  DMV** — Fabric Warehouse doesn't support that DMV (error 15871) but supports the sp; the sp works on box
  too, so one path serves both (Fabric-validated 2026-06-24). Auto-routes by object kind. Empty ⇒ "no
  describable result set".
- **Execution** (`ExecuteProc`): no-output proc ⇒ `EXEC [s].[p] @name=@p0,…` (streams the first result set);
  output proc ⇒ the `DECLARE/EXEC OUTPUT/SELECT` batch above. Input param types come from
  `INFORMATION_SCHEMA.PARAMETERS` (reused `get_function_param_schema`, whose field names are the de-@'d names).
- **Verified**: `usp_sc(minSalary := 200)` → rows; local projection+filter; aggregation; **optional param**
  omitted → proc `DEFAULT` (`usp_opt(base:=10)`→60) and supplied → override (`…, bonus:=5`→15); **OUTPUT
  params** flat (`usp_outp(a:=10,b:=3)` → `sum=13, diff=7, return_value=42`). Committed test:
  `test/verify_stored_procs.test`.

### Custom (provider-authored) functions (4e scalar, 4f table)
- Beyond functions *discovered* from SQL Server, a provider can **author custom functions in C#**:
  - **4e scalar** — `ICatalogScalarFunction` (Bridge, derives the base `IScalarFunction`) = `SchemaName`/`Name`/`Parameters`(arg fields)/
    `Result`(field)/`Invoke(RecordBatch)→IArrowArray`. Demo `CustomFunctions.Scalar`: `dbo.cf_add(a,b)=a+b`.
  - **4f table** — `IArrowTableFunction` (Bridge) = `SchemaName`/`Name`/`Parameters` + `Bind(args) →
    IArrowTableFunctionBinding` (`OutputSchema` + `IAsyncEnumerable<RecordBatch> Execute(scan, ct)` — args = the
    1-row positional call args; yields result batches **async/lazily**, streamed to the host via
    `AsyncEnumerableArrowStream`). Fixed-schema functions derive from `StaticTableFunction` (override
    `OutputSchema` + `IAsyncEnumerable<RecordBatch> Invoke(args, ct)`). Demos `CustomFunctions.Table`:
    `dbo.cf_range(n)` → `(value, squared)` (StaticTableFunction) + `dbo.cf_columns(n)` (output schema from the
    arg, via Bind).
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
  same `IScalarFunction` authoring + the existing `execute_scalar` with a handle-less marker would reuse
  this path.)
- **Verified**: scalar `db.dbo.cf_add(2,3)=5`, vectorized, NULL→NULL; table `cf_range(3)`→`(1,1),(2,4),(3,9)`
  with projection (`squared` only) + filter (`value>1`) + aggregation; both discovered (`scalar`/`table`)
  with **no SQL object** (`sys.objects` count 0). Committed test: `test/verify_custom_functions.test`.

### Callable table-in-out (4g)
> **Fully superseded by the streaming exchange (Phase 6) — this push/materialize model is RETIRED.** The
> per-chunk materializing model described here (a C# `_ready` buffer + a C# lock, over
> `inout_open`/`push`/`finish`/`abort`) served custom in-out + discovered TVFs (moved to the exchange in
> Phase 6) and finally stored procs (`SqlServerProcEach`, moved in `9056eae`). The C# push sessions are gone;
> the C++ push operator + ABI are dead-in-place. These 4g notes remain the reference for the per-row CROSS
> APPLY / proc-EXEC **semantics** + the echo output schema (unchanged on the exchange); only the transport
> changed (push/materialize → gate + two pull streams). See "Streaming table-in-out exchange (Phase 6)".
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
- **Custom C#-authored in-out (4g-custom)** — *unified in Phase 6: the per-chunk `Process` shape below is now
  the `StaticInOutFunction` base under the single `IArrowInOutFunction`, and runs on the streaming exchange
  (`InOutBind`), not the push `CustomInOutSessionImpl`/`InOutOpen` described here. See "Streaming table-in-out
  exchange (Phase 6)". The per-chunk semantics are unchanged; the original push wiring below is historical.*
  Original (push) design: `IArrowTableInOutFunction` (Bridge) =
  `SchemaName`/`Name`/`InputSchema`/`OutputSchema` + `IEnumerable<RecordBatch> Process(chunk)` (the in-out
  analog of 4e `ICatalogScalarFunction` / 4f `IArrowTableFunction`). A pure-C# **per-chunk streaming**
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
- **Per-row stored procs (4g-proc → now on the exchange, `9056eae`)**: a discovered proc also gets `_each`
  (C++ `AddTableFunction` registers the alias for procs too; a proc can't be inline-CROSS-APPLY'd, so it's
  EXEC'd per input row). **Now on the streaming exchange** (`SqlServerProcEach : IArrowInOutBinding`, resolved
  by `InOutBind` — proc vs TVF classified by ROUTINE_COLUMNS): `DoExchange` runs, per input row, `DECLARE @t
  TABLE(<proc result>); INSERT @t EXEC [s].[p] @param=@p,…; SELECT <echoed input>, t.* FROM @t;` on **DuckDB's
  pinned connection/`_txn`** (`BeginWrite`) — echo is server-side (output = input cols ++ proc result cols),
  result-set procs only. It does **not** commit/dispose the pinned scope, so the proc's writes commit/roll
  back with **DuckDB's** transaction (autocommit + explicit `BEGIN`); the gate (`MaxThreads=1`) serializes the
  EXECs on the pinned connection. (Was the 4g push `ProcInOutSessionImpl`, retired in `9056eae`.) Verified:
  `test/verify_proc_inout.test`.
- **OperatorFinalize cleanup signal (4g-finalize)**: an `OptimizerExtension` (`RegisterArrowNetInOutFinalizer`,
  registered at load) wraps each in-out `LogicalGet` (identified by `function.in_out_function ==
  ArrowNetInOutFunction`, RTTI-free) in a pass-through `LogicalExtensionOperator`; its `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 (`Execute = chunk.Reference(input)`) and, in
  `OperatorFinalize`, calls `holder->Finish()` → C# `inout_finish`. Fires **once**, sink-level, even above a
  parallel UNION (unlike per-branch `in_out_function_final`) — a reliable C# resource-cleanup hook + the clean
  commit of a read-only TVF's snapshot transaction (NOT the proc commit — DuckDB drives that). Transparent to
  data flow (`GetColumnBindings`/`ResolveTypes` delegate to the child); the holder destructor's `inout_abort`
  stays the LIMIT/error backstop (idempotent via `_scopeClosed`).

### Streaming table-in-out exchange (Phase 6, ABI v28)
The streaming successor to the 4g push/materialize in-out model, for **ALL `_each` forms — custom C# in-out,
discovered TVFs, AND stored procs** (procs unified onto the exchange in `9056eae`; the 4g push path is now
fully retired). No per-chunk materialization: output streams via two pull-based Arrow streams coordinated by
a C++ "gate" mutex; the lock moved C#→C++. Commits `ca111e7` (ABI), `49f9a1d` (operator + custom), `330d2c7`
(discovered TVF), `9056eae` (proc). Design + the 6.0 spike: the plan file's "Phase 6".
- **Three bindings, one `DoExchange` shape** (resolved by `InOutBind`, which classifies custom / TVF / proc):
  a custom C# in-out (`IArrowInOutFunction`); a discovered TVF `_each` (`SqlServerTvfEach` — per-row CROSS
  APPLY on its **own read-only** connection at the configured isolation); a stored-proc `_each`
  (`SqlServerProcEach` — per-row `EXEC` on **DuckDB's pinned write** connection (`BeginWrite`), no commit/
  dispose, so the proc's writes commit/roll back with DuckDB's COMMIT/ROLLBACK). The gate (`MaxThreads=1`)
  serializes the proc EXECs on the pinned connection; the transactional contract (autocommit / explicit-BEGIN
  read-your-writes + ROLLBACK) holds — verified by `verify_proc_inout`. The 4g push operator (`ArrowNetInOut*`)
  + the `inout_open`/`push`/`finish`/`abort` ABI + `IInOutSession`/`InOutOpen` were **removed at ABI v31**
  (`49e6d94`); the exchange is the only in-out path.
- **Author API** (`IArrowInOutBinding`, Bridge): `Schema OutputSchema` + `IAsyncEnumerable<RecordBatch>
  DoExchange(IAsyncEnumerable<RecordBatch> input, ct)`. `input` yields one batch per DuckDB input chunk; the
  returned enumerable maps to the operator contract — non-empty = HAVE_MORE_OUTPUT, **length-0 = the
  per-input sentinel (NEED_MORE_INPUT)**, end-of-enumerable = FINISHED. **The author yields the sentinel** (the
  decision after the 6.0 spike — a free-form `DoExchange` is incompatible with the single-slot gate unless the
  author delimits per chunk; the framework can't inject it without deadlocking). `IArrowInOutIsolation` is the
  optional SQL-isolation hook (the framework sets it before `DoExchange`; pure-C# bindings ignore it).
- **ABI v28** (`abi.h`): `inout_bind(handle, schema, func, args, input_schema, out_schema, out_binding)` resolves
  the FULL output schema (input echo ++ the function's own columns) in C# + returns a binding handle (reused
  across prepared re-executions, freed via `inout_bind_close`); `inout_exchange_open(binding, input, isolation,
  output)` runs one execution — the host exports the INPUT stream (its get_next hands the gate-holder's one
  chunk to C#), C# exports the OUTPUT stream the host pulls. (The 4g `inout_open`/`push`/`finish`/`abort`
  were removed at ABI v31 once procs joined the exchange.)
- **C# pump** (`InOutExchange.cs`, Bridge): `InOutExchangeStream` exposes `DoExchange` as a pull-based
  `IArrowArrayStream` — the Arrow C-stream exporter blocks on `ReadNextRecordBatchAsync` (sync-over-async; the
  hostfxr CLR has no `SynchronizationContext`, so `GetResult` can't deadlock — proven by the 6.0 spike).
  Custom authors implement **one** interface, `IArrowInOutFunction.Bind(args,inputSchema) →
  IArrowInOutBinding` (registry `CustomInOut`, resolved by `InOutBind`): the author writes `DoExchange` —
  reads the input stream, yields output batches, and yields a length-0 sentinel after each input chunk, with
  cross-chunk state in `DoExchange` locals (a fresh enumerator runs per exchange, so state never leaks across
  re-executions). For a FIXED output schema, derive from the convenience base **`StaticInOutFunction`**
  (override `OutputSchema` + `DoExchange`; the base supplies the `Bind`→binding wiring) — it is to
  `IArrowInOutFunction` what `StaticTableFunction` is to `IArrowTableFunction`. Demos `cf_tag` (stateless),
  `cf_running_sum` (cumulative-sum local), `cf_exchange` (row-index local) all use it. `SqlServerTvfEach`
  (`SqlServerTvfEach.cs`) runs the per-row CROSS APPLY inside `DoExchange` on one pinned connection +
  transaction (the streaming successor to the deleted `InOutSessionImpl`). `InOutExchange.EmptyBatch` builds
  the length-0 sentinel matching the output schema.
- **C++ operator** (`arrownet_schema_entry.cpp`, anon ns): `ArrowNetExchange{Bind,InitGlobal,InitLocal,Function}`
  + `ArrowNetExchangeGlobalState` (the gate `std::mutex`, the single input `slot`, `input_eof`, the output
  reader, `MaxThreads()=1`) + a host-side input stream whose get_next hands the gate-holder's slot to C#.
  `Execute` holds the gate across the chunk's HAVE_MORE_OUTPUT cycle (ownership in the per-thread local state),
  releases it on the sentinel/EOF **or on a thrown managed error** (RAII-style — never leaks). `ArrowStreamReader`
  gained **sentinel-aware** `Pull()`/`HasPending()`/`Drain()` (its `Read()` skips empty batches, so the sentinel
  needs explicit length inspection + <=STANDARD_VECTOR_SIZE slicing). **EOF is the injected `OperatorFinalize`**
  (`ArrowNetExchangeFinalizePhysical`, parallel to the 4g one): once, sink-level, after all branches it sets
  `input_eof` + drains the output to terminal-null so the managed `DoExchange` finishes + disposes — NOT a
  producer counter (the rejected premature-finish design). `ExchangeHolder` (refcounted on the bind data) frees
  the binding once. Registration: custom in-out (`GetOrCreateCustomInOutFunction`) + **every** `_each`
  (`GetOrCreateInOutFunction`) use the exchange callbacks (proc `_each` moved here in `9056eae` — no
  base-is-proc branch); the managed `InOutBind` classifies custom / TVF / proc and returns the matching
  binding (`SqlServerTvfEach` read-only conn / `SqlServerProcEach` DuckDB's pinned write conn).
- **Verified**: `verify_custom_functions` (73 — incl. parallel UNION ALL + a `threads=1` sequential-union case,
  the schedule a premature-finish bug would drop rows on); `verify_table_inout` (63, TVF now on the exchange);
  `verify_inout_isolation` (17, via `IArrowInOutIsolation`); `verify_proc_inout` (31, push, unregressed). Full
  suite 20/20.

### Callable aggregate functions (4h — custom C# UDAF)
- **Scope**: provider-authored aggregates in C# (there are no SQL Server aggregates to discover), attach-time +
  catalog-bound (`db.dbo.cf_agg(x)`), usable in `GROUP BY`, parallel aggregation, and window (`OVER`) contexts.
  Authored via Bridge `IArrowAggregateFunction` (`SchemaName`/`Name`/`Parameters`/`Result`/`CreateState()`) +
  `IArrowAggregateState` (`Update(RecordBatch)`/`Combine(other)`/`object? Finalize()`). Demos
  `CustomFunctions.Aggregate`: `dbo.cf_product` (numeric product) + `dbo.cf_bit_or` (bitwise OR fold).
- **State model (the crux)**: DuckDB's aggregate model is *state-vectorized* — it owns a contiguous array of
  fixed-size state blobs and drives reduction through `state_size`/`initialize`/`update`/`simple_update`/
  `combine`/`finalize`/`destructor` callbacks. We keep each **blob as a mere `int64` id** (`state_size=8`);
  the real per-group accumulator lives in **C#** behind that id (a `ConcurrentDictionary<id, accumulator>` per
  bound aggregate). `initialize` writes `id = counter.fetch_add(1)` from a `std::atomic<int64_t>` on the
  function's `function_info` → **monotonic ids never collide** across threads or prepared-statement
  re-executions, so correctness needs no destructor. (State-in-C# was the user's call: matches the existing
  handle-based architecture, no per-call (de)serialization; the trade-off is that the managed map isn't visible
  to DuckDB's memory manager → no disk-spill for billions of distinct keys.)
- **Session**: opened in the aggregate `bind` (a `FunctionData` = `ArrowNetAggregateBindData` holding a
  refcounted `AggSessionHolder`; its destructor calls `agg_close`). `bind` runs once per bound plan; update/
  combine/finalize/destructor reach the session via `AggregateInputData.bind_data`. Identity (handle/schema/
  func) + the counter ride on `ArrowNetAggregateFunctionInfo : AggregateFunctionInfo` (reachable from
  `initialize` via `function.function_info`, which has no `bind_data`).
- **Two correctness rules from the DuckDB source** (both verified): (1) **read state pointers via
  `UnifiedVectorFormat`, never `FlatVector::GetData<data_ptr_t>`** — the ungrouped path passes a **CONSTANT**
  state vector to `finalize`/`simple_update`; (2) implement **both** `update` (grouped, FLAT state vector) and
  `simple_update` (ungrouped fast path, single `data_ptr_t` → one id, no C# grouping). Threading: each id is
  touched by one thread at a time (DuckDB's per-thread local hash tables; combine is partition-disjoint), so a
  `ConcurrentDictionary` suffices with **no** per-accumulator lock. Empty group: an init'd-but-never-updated id
  is absent from the map → finalize makes a fresh accumulator → its `Finalize()` is the empty value (NULL).
- **Window (OVER) needs no custom `window` callback**: DuckDB drives windowing through our update/combine/
  finalize via `WindowSegmentTree` (chosen at `window_aggregate_function.cpp`), which batches updates + does
  O(log n) combines per output row. A custom `window` callback would cost **one C++↔C# crossing per output
  row** — strictly worse for a marshaled bridge — so we deliberately don't implement it. The window paths churn
  many transient states, so the **destructor IS wired** (`agg_destroy`) to bound the C# map; `agg_close` is the
  backstop.
- **C++** (`arrownet_schema_entry.cpp`, anon ns): `ArrowNetAggregate{StateSize,Init,Bind,Update,SimpleUpdate,
  Combine,Finalize,Destroy}` static callbacks marshal `[id ++ inputs]` / `[target_id, source_id]` / `[id]`
  Arrow batches (`ArrowAppender` + `arrownet::Agg*`); finalize ingests the single result column via
  `ArrowStreamReader` + `VectorOperations::Copy`. The aggregate callbacks get **no `ClientContext`** (unlike
  scalar/table execution), so the connection context + client properties are captured at `bind`.
  `GetOrCreateAggregateFunction` mirrors `GetOrCreateScalarFunction` → an `AggregateFunctionCatalogEntry`
  (`AggregateFunctionSet` of one). **Resolution**: DuckDB stores scalar/aggregate/macro in one namespace and
  the binder looks up `SCALAR_FUNCTION_ENTRY` then dispatches on the returned entry's actual type, so
  `LookupEntry(SCALAR_FUNCTION_ENTRY)` **falls back to the aggregate** (plus an explicit `AGGREGATE_FUNCTION_ENTRY`
  branch). Discovery routes `kind=='aggregate'` → `AddAggregateFunction`.
- **Opt-in disk-spill (`SupportsSpill`, ABI v26)**: by default the state lives in C# (fast, bounded by managed
  memory, no spill). A provider can instead set `IArrowAggregateFunction.SupportsSpill=true` (+ implement
  `IArrowAggregateState.Serialize()`/`Load()`) → **bytes-in-blob mode**: the per-group state is serialized into
  DuckDB's fixed, pointer-free state blob (`[uint32 len][byte data[ARROWNET_AGG_SPILL_CAP=1 KB]]`) so DuckDB's
  external GROUP BY spills it to disk under memory pressure. Surfaced as `kind='aggregate_spill'`; `state_size`/
  `initialize` (sentinel len, no C# call) and update/combine/finalize/destroy all branch on the `spillable`
  flag (on `function_info` for the first two, `bind_data` for the rest). The spill callbacks marshal state as
  Arrow BLOB columns over the v26 `agg_*_spill` ABI; C# is **stateless per call** (rehydrate→apply→reserialize)
  so the destructor is a no-op (the blob owns the state). **Two correctness points found in the build:** (1)
  update + combine assign **dense group/target slots** so a group whose rows interleave (update) OR a target
  combined with several sources in one batch (the **window** segment-tree merges many nodes into one frame
  state) accumulates into one transient accumulator — a naive read-once/write-once loses merges (caught by the
  windowed-spill test); (2) serialized state must fit the 1 KB cap (enforced on write-back) → spill suits
  fixed/small state (sum/product/bitwise/avg/moments), not unbounded state (string concat). Demo
  `dbo.cf_sum_spill`.
- **Non-additive (holistic) aggregates** work in the fast mode with **no special support**: the accumulator's
  state IS the collected values (`Update` collects, **`Combine` merges the two collections** — concatenation,
  not arithmetic, the same way DuckDB's own `median`/`list`/`mode` combine — and `Finalize` computes over the
  union). Correct under parallel partial-state merging for order-independent aggregates. `SupportsSpill` stays
  false (an unbounded collection can't fit the blob cap). Demo `dbo.cf_median` (collect → sort → middle). The
  author must **copy values out in `Update`** (the batch's Arrow buffers are freed after it returns; don't
  retain the `RecordBatch`).
- **Verified**: `test/verify_custom_aggregates.test` (58 assertions) — discovery (`kind='aggregate'` +
  `'aggregate_spill'`) + no SQL object, ungrouped (`simple_update`), implicit INTEGER→BIGINT, NULL-skip,
  empty/all-NULL → NULL, `GROUP BY` (incl. a NULL-only group), parallel `bit_or` cross-checked vs DuckDB's
  built-in (threads=4), grouped+parallel `product` cross-checked vs DuckDB's `product()`, running-frame `OVER`,
  windowed `OVER`/`PARTITION BY` cross-checked vs the built-in; the spillable `cf_sum_spill` across ungrouped /
  `GROUP BY` / high-cardinality (50k rows × 1000 groups) / low-`memory_limit` (80k × 4000) / window — each
  cross-checked vs `sum()`; AND the holistic `cf_median` (odd/even/NULL/empty + a 5000-row × 50-group parallel
  cross-check vs DuckDB's `median()`, proving combine-as-merge under parallel aggregation).

- **Filtering**: discovered scalar UDFs + TVFs/procs are gated by the ATTACH `schema_filter` (icase
  `std::regex`, applied in `LoadCatalog`/`RefreshCache`); `table_filter` is table-only and does NOT apply to functions.
- **Parallel partitioned reads** (ConnectorX-style `partition_on`/`partition_num`) — **design note, deferred,
  nothing built**: [docs/parallel-partitioned-read.md](docs/parallel-partitioned-read.md). Two wins to keep
  distinct — parallel *fetch* (form A: C# runs N range queries concurrently + `ParallelMerge` → the existing
  single-stream scan, no ABI) vs parallel DuckDB *pipeline/core usage* (form B: N streams → N scan threads via
  a parallel multi-stream scan = the native form of the proven `UNION ALL` core-saturation trick; bigger). On
  `arrownet_query` the two surface as optional NAMED params (the `daxeval` pattern); a custom
  `IArrowTableFunction` could return `IAsyncEnumerable<IAsyncEnumerable<RecordBatch>>` (outer = partitions).
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
- **Current version: ABI v50** (v50 = **directory move/rename** — appended `fs_move_dir(opener,src,dest,…)` to
  `ArrowNetHostServices` (the reverse host→managed struct): maps to DuckDB's `FileSystem::MoveFile` — an atomic
  directory rename on a local filesystem; object stores (S3/Azure DFS) throw "not implemented". Powers **local/S3
  Delta catalog RENAME TABLE** (`DeltaCatalog.AlterTable` RenameTable → `HostFs.MoveDir`; OneLake still renames via
  the DFS SDK since Azure `MoveFile` is unimplemented). `test/verify_delta_catalog_schemas.test`. v49 =
  **recursive directory delete** — appended `fs_remove_dir(opener,path,…)` to
  `ArrowNetHostServices` (the reverse host→managed struct, not the vtable): deletes a directory RECURSIVELY via
  DuckDB's `FileSystem::RemoveDirectory` (idempotent — no error if absent). Powers **Delta catalog DROP TABLE**
  (`DeltaCatalog.DropTable` → `HostFs.RemoveDir` removes the table's whole `<root>/<table>/` folder; opener
  threaded by `DropEntry`'s `ArrowNetSetActiveTxn`). `test/verify_delta_catalog_write.test` (31). **OneLake DROP
  goes a different route** (`fs_remove_dir` → `FileSystem::RemoveDirectory` throws `AzureDfsStorageFileSystem:
  RemoveDirectory is not implemented!` — duckdb-azure has no recursive-delete on the DFS endpoint): `DropTable`
  branches on `FabricLakehouse.IsOneLake(root)` → a **direct ADLS Gen2 / OneLake DFS recursive delete**
  (`FabricLakehouse.DeleteDirectory` → `DataLakeDirectoryClient.DeleteIfExistsAsync`, idempotent) using the SP
  `ClientSecretCredential` the catalog mints — bypassing duckdb-azure entirely; local/S3 keep `fs_remove_dir`.
  Validated live 2026-06-30 on both `LH` (schema-enabled) and `LH_no_schema` (flat). See the OneLake-discovery
  paragraph below — discovery + DROP now share the DFS endpoint. v48 =
  **host-FS WRITE surface** — the Delta write-back foundation: appended five
  WRITE callbacks to `ArrowNetHostServices` (the reverse host→managed struct, not the vtable) —
  `fs_open_write(opener,path,exclusive,…)` / `fs_write` / `fs_close_write` / `fs_remove` / `fs_create_dir` — plus
  the `ARROWNET_ALREADY_EXISTS=4` status. `exclusive=1` opens with `EXCLUSIVE_CREATE` (the put-if-absent commit
  primitive — honored on OneLake/ADLS + POSIX; returns `ALREADY_EXISTS` if the target exists). `fs_create_dir`
  is recursive (mkdir -p; DuckDB's is single-level). The C# `DuckDbTableFileSystem` write methods
  (`CreateAsync`/`WriteAllBytesAsync`/`RenameAsync`/`DeleteAsync` + `DuckDbSequentialFile`) sit on these;
  **`RenameAsync` is emulated as exclusive-create-copy + delete-source** because DuckDB's `MoveFile` overwrites
  on local and is *unimplemented* on Azure DFS — so the commit's put-if-absent guard rides `EXCLUSIVE_CREATE`,
  and engineered-wood's temp+rename commit works unchanged (a conflicting target → `RenameAsync` returns false
  → `DeltaConflictException`). `HostFsGlob` now normalizes a not-found glob (object-store 404) to empty so a
  brand-new table's missing `_delta_log/` reads as "create". Demo `arrownet_delta_write_demo(path)` — a global
  host-FS table fn writing a fixed 5-row Delta table via engineered-wood (`DeltaWriteMode.Overwrite`,
  idempotent), validated end-to-end (write+read round-trip) on **local AND a live OneLake lakehouse** (SP
  azure secret). `test/verify_delta_write.test`. Single-writer; concurrent commits work where `EXCLUSIVE_CREATE`
  is honored (OneLake/POSIX — not Windows local). **Portability (validated with delta-kernel-rs, the reference
  reader, via DuckDB's official `delta` extension): engineered-wood's defaults are NOT standard-readable (incl.
  Fabric) — three fixes:** (1) `metaData.format.options` + (2) `metaData.configuration` always emitted
  (engineered-wood `ActionSerializer` — were omitted when empty/null, non-nullable for strict readers); (3)
  parquet `path_in_schema` (engineered-wood `OmitPathInSchema` defaults TRUE → drops this REQUIRED field →
  `TProtocolException: Invalid data`) — fixed our side via `ParquetWriteOptions { OmitPathInSchema = false }` in
  `DeltaWriteDemoFunction`. With all three, delta-kernel-rs reads it locally; a fresh `Tables/dbo/arrownet` table
  written to OneLake for Fabric. (#1/#2 are engineered-wood-repo patches; #3 is a write option. DuckDB's official
  `delta_scan` can't LIST a OneLake `_delta_log` — a delta-kernel azure/secret quirk, "No files in log segment" —
  so OneLake validation is via our reader + the local delta-kernel read. A table written BEFORE the fixes stays
  broken on its version-0 metaData → write a fresh one.) **`arrownet_delta_write(<input>, path := '…')`** — a
  global host-FS **collector** that writes ANY input table (a DuckDB query result) to a Delta table (Overwrite),
  returning `(version, rows_written)`; buffers input (Arrow-IPC round-trip copy), commits one version via the
  shared `DeltaWriter`. Cost args ride as NAMED params (`Parameters` added to `IInOutFunction`/
  `ICollectorTableFunction` + handle-0 `GlobalFunctions.ParamSchema`); the opener is threaded into the collector
  Source `GetDataInternal` (where C# `Collect` runs — Finalize-only was racy) AND into the shared
  `ArrowNetSetActiveTxn` helper (so any connection-using callsite sets it). Validated local + a live OneLake
  managed table (`Tables/dbo/arrownet_query`). `test/verify_delta_write.test` (18). **Delta folder-as-catalog
  (READ + WRITE) DONE**: `DeltaBackend` (3rd `IBackend`, `"delta"`/`"deltalake"`, registered explicitly in
  `BackendRegistry.Discover` — Bridge-resident) + `DeltaCatalog`. `ATTACH '/lake'
  (TYPE arrownet, PROVIDER 'delta')` discovers subdirs-with-`_delta_log/` as tables under a flat `main` schema
  (glob `<root>/*/_delta_log/*.json`), columns via `DeltaReader.GetSchema`, scan via `DeltaReader.Stream` with
  filter pushdown. The opener is threaded into the catalog metadata path (`LoadCatalog`/`RefreshCache` call
  `ArrowNetSetActiveTxn` before discovery; `FetchTableColumns` already did). `test/verify_delta_catalog.test`
  (17 — discovery + filter + join, LOCAL). **CATALOG STREAMING WRITE DONE** (the chosen slice): `CREATE TABLE`/
  `INSERT`/CTAS/COPY stream straight to engineered-wood via the **standard bulk path** (`begin_bulk`/`push_batch`/
  `complete_bulk` → `BulkSession` → `DeltaCatalog.BulkInsert`), exactly like the SQL/DAX backends — the global
  `arrownet_delta_write` collector is no longer needed for the catalog case (it stays as the no-ATTACH function
  form). **Opener threading into the bulk path:** `SetActiveOpener(&context)` is set immediately before
  `BeginBulk` in the insert/CTAS/COPY operators; `BulkSession` captures it at `begin_bulk` and **re-establishes
  `AmbientOpener.Current` (+ the txn id) on its background consumer thread** (the opener's `ClientContext` stays
  valid until `complete_bulk`, which blocks on the consumer). `createTable`/`replace` ⇒ `DeltaWriteMode.Overwrite`,
  plain INSERT ⇒ `Append`; one commit per statement; `CreateTable` writes empty commit-0 (PK/UNIQUE/DEFAULT
  ignored — Delta has none); `DeltaWriter.Materialize` IPC-round-trips the streamed batches for the commit. NO
  ABI change (reuses bulk + the v47 `set_active_opener`). `test/verify_delta_catalog_write.test` (31 — CREATE/
  INSERT/append/CTAS/aggregate + DROP TABLE + detach/re-attach durability, LOCAL). **DROP TABLE DONE** (ABI v49
  — appended `fs_remove_dir` to `ArrowNetHostServices`: recursive directory delete via DuckDB's
  `FileSystem::RemoveDirectory`, idempotent; `DeltaCatalog.DropTable` deletes the table's whole `<root>/<table>/`
  folder via `HostFs.RemoveDir(AmbientOpener.Current, …)`, opener threaded by `DropEntry`'s `ArrowNetSetActiveTxn`).
  **DELETE DONE — copy-on-write via a TRANSIENT (file,position) rowid; tables are PLAIN Delta (no features)**
  (mirrors the SQL Server backend's rowid DML — reuses the existing rowid operators wholesale, NO OptimizerExtension/
  custom operator, NO ABI change). **Why this shape (3 live-Fabric iterations):** DuckDB doesn't expose the WHERE
  at `PlanDelete` (`LogicalDelete` keeps only the table + a rowid-producing child) → predicate-capture is unsafe
  (pushdown is a superset → over-delete) → the rowid path is correct. The FIRST attempt used **Delta row tracking**
  (stable `_metadata.row_id`) + **deletion vectors**, but Fabric's OneLake converter / Spark could NOT read those
  commits: engineered-wood's protocol omitted feature dependencies (rowTracking needs `domainMetadata`; DVs need
  reader-v3 + `deletionVectors`) AND — even with a fully spec-compliant protocol — engineered-wood's inline DV
  byte format isn't what Spark/Fabric decode. **Final design = plain Delta, no table features:** the rowid is a
  TRANSIENT `(fileOrdinal << 40) | rowPositionInFile` (file ordinal in the path-sorted active set), computed at
  scan time, NOT persisted; DELETE is **copy-on-write** — rewrite each affected file without the deleted rows,
  committing plain `remove`+`add` (no DV). minReaderVersion 1 / minWriterVersion 2, zero features → every reader
  (Fabric OneLake conversion, Spark, delta-kernel) reads it. **engineered-wood** (local working changes):
  `ReadAllWithRowIdsAsync` appends the transient rowid (`OrderedActiveFiles` path-sort + per-file position);
  `DeleteByRowIdsAsync` decodes rowids → positions-per-file → copy-on-write rewrite; `CreateAsync` writes plain
  Delta (the feature-declaration logic stays but is unused — `DeltaWriter` passes no config). **Two parquet
  footer gotchas the rewrite hit** (both made the rewritten file unreadable by delta-kernel/Spark/Fabric with
  `TProtocolException: Invalid data`, while OUR reader tolerated it): (1) the rewrite must open with
  `DeltaWriter.Options()` (`OmitPathInSchema=false`) — `DeltaTableOptions.Default` drops the REQUIRED parquet
  `path_in_schema`; (2) the rewrite must REBUILD each kept batch with a CLEAN schema (drop the parquet reader's
  field metadata, e.g. an existing `PARQUET:field_id`) before re-writing — else `SetParquetFieldIds` collides
  and the footer is malformed. With both, delta-kernel reads our copy-on-write output (verified locally via
  DuckDB's official `delta_scan` — the reference reader Spark/Fabric use). **Virtual rowid
  threading** (the crux — `_metadata.row_id` is NOT a user column; surfacing it as one would break INSERT):
  `FetchRowIdColumns` returning a name absent from the schema is treated as a VIRTUAL rowid — `ArrowNetTableEntry`/
  `ArrowStreamBindData` carry the NAMES (not indices) in `virtual_rowid_columns`, `HasRowId`/`GetVirtualColumns`/
  `GetRowIdColumns` honor them, `BuildScanSpec` adds them to the fetch list when rowid is requested, `arrow_ingest`
  resolves their result positions BY NAME for `BuildRowId`, and `BuildModifyTarget` uses the virtual names +
  BIGINT. SQL Server is unaffected (its rowid names always resolve to real columns; the virtual branch never
  fires — verified `verify_proc_inout`/`verify_time_travel`/`verify_columnstore` green). `DeltaCatalog`:
  `GetMetadata(RowId)` ALWAYS returns `_metadata.row_id` (the transient rowid works on ANY Delta table);
  `ScanTable` streams WITH the row-id column when requested; `ExecuteDelete` collects the ids → `DeleteByRowIdsAsync`.
  `test/verify_delta_catalog_delete.test` (28 — equality/range/name predicates + durable across re-attach +
  DELETE-all). **Live Fabric: plain-Delta CTAS+DELETE validated end-to-end** on the schema-enabled `LH` lakehouse
  (`arrownet_deltest4`: v0 protocol = minReader 1/minWriter 2/no features, DELETE = plain remove+add, our read
  correct); the OneLake table-format conversion is expected to succeed on plain Delta (pending user confirm — the
  earlier row-tracking/DV tables failed conversion). **A transient rowid is valid only within one snapshot** (a
  scan's rowids must be consumed by the DELETE before another write changes the file set — true for a single
  DML statement). **UPDATE DONE — rowid-based PER-FILE copy-on-write** (matches DELETE). `ExecuteUpdate` receives
  the new SET-column values (named) + the transient `_metadata.row_id` per row; it builds a `rowid → new values`
  map and calls engineered-wood `UpdateByRowIdsAsync(rowIds, rewriteFile)`, which rewrites ONLY the files
  containing a matched row (decoded from `rowid >> 40`) — each affected file's batches are handed back via the
  `rewriteFile` callback, where ArrowNet rebuilds the SET columns on the matched positions as CLEAN Apache.Arrow
  batches (`BuildArray`, a typed inverse of `ArrowValueReader.ReadScalar` — bool/ints/uints/float/double/
  decimal128/string/date32/timestamp; rowid recomputed as `(ordinal << 40) | positionInFile` to match the scan),
  and engineered-wood re-writes them as plain `remove`+`add` with a CLEAN schema (the parquet-footer fix). The
  typed substitution stays in ArrowNet (reuses `BuildArray`/`ReadScalar`); engineered-wood stays generic (file
  selection + read + clean write). Unaffected files are untouched. NO C++ change (reuses the DELETE virtual-rowid
  planning + the `ExecuteUpdate` ABI). Verified single-row / multi-row / expression (`amt=amt+1`) updates,
  UPDATE∘DELETE composition, re-attach durability, a delta-kernel `delta_scan` read-back, AND live on Fabric
  (`arrownet_updtest` on the schema-enabled `LH` lakehouse). `test/verify_delta_catalog_update.test` (63).
  **SCHEMA EVOLUTION — `ALTER TABLE … ADD COLUMN` DONE** (the only supported ALTER kind on Delta): a
  **metadata-only commit** appending a nullable column (NO file rewrite) — engineered-wood `DeltaTable.AddColumnAsync`
  writes a new `MetadataAction` (current Arrow schema ++ the new field → `SchemaConverter.FromArrowSchema` →
  `DeltaSchemaSerializer` → `metaData` at version+1; rejects column-mapping tables [no field-id assignment] +
  non-nullable + duplicate names). The crux is the **read-side NULL backfill** (`DeltaTable.BackfillMissingColumns` +
  `MakeNullArray`, in `ReadFileAsync` before the rowid append): a column added after a data file was written is
  absent from that file's parquet, so each batch is reconciled to the current schema — present columns by name, the
  missing one as an all-NULL typed array (no-op fast path when the file already has every column). `DeltaReader.AddColumn`
  → `DeltaCatalog.AlterTable`; `a1`=name, the `Field` carries type+nullability. C++ `ArrowNetSchemaEntry::Alter` now
  **`SetActiveOpener` before the alter** (host-FS opener for the Delta metadata write; no-op for SQL) + the existing
  eager column re-fetch surfaces the new column in-session. NO ABI change (reuses the v2 `alter_table` entry).
  Standard-compliant by construction (a textbook metaData commit on already-standard data files →
  delta-kernel/Spark/Fabric backfill old-file NULLs natively). Verified: `test/verify_delta_catalog_alter.test` (81 —
  old rows NULL, new rows valued, 2nd-column add, predicate on the new column, re-attach durability,
  RENAME/DROP/TYPE error). **`RENAME TABLE` DONE (OneLake only)** — the table IS its folder and Delta's `_delta_log`
  uses table-relative paths, so renaming = moving the whole folder. OneLake uses the DFS endpoint's **atomic native
  rename** (`FabricLakehouse.RenameDirectory` → `DataLakeDirectoryClient.RenameAsync`; the destination path is
  filesystem-relative WITHOUT the workspace prefix — OneLake requires the leading segment to be the
  `<item>.Lakehouse`, else 400 "item type extension is missing"). C++ `Alter`'s RENAME_TABLE branch already updates
  the entry cache. Validated live on `LH` (create → rename → new name reads → drop). **local/S3 RENAME — DONE (ABI
  v50)** via the new host `fs_move_dir` → `FileSystem::MoveFile` (`DeltaCatalog.AlterTable` → `HostFs.MoveDir`):
  an atomic directory rename on local; an object store whose FileSystem doesn't implement `MoveFile` throws a clean
  error. Verified local (`verify_delta_catalog_schemas.test` — rename + reattach durability). So RENAME works on
  local + OneLake (S3 iff DuckDB's S3 FileSystem implements `MoveFile`). **Still unsupported** (clean error): raw
  exec, RENAME/DROP COLUMN + ALTER COLUMN TYPE (need column mapping / rewrite). **DROP SCHEMA** is supported in
  `schemas` mode (see below), else unsupported. (Recursive DROP TABLE already works on local/S3 via `fs_remove_dir`,
  ABI v49.)
  **Multi-schema for local/S3 — the `schemas true` ATTACH option (DONE)**: by default a non-OneLake Delta catalog is
  FLAT (single `main` schema; the schema component is ignored, so `db.staging.t` and `db.main.t` would both map to
  `<root>/t` — a silent collision footgun). `ATTACH '…' (TYPE arrownet, PROVIDER 'delta', schemas true)` switches it
  to the two-level `<root>/<schema>/<table>` layout (the same layout a schema-enabled OneLake lakehouse uses):
  `DeltaCatalog._schemas` (parsed from the forwarded ATTACH options JSON, like `deletion_vectors`) drives
  `SchemaLayout` → `TablePath` nests by schema; `DiscoverTablePairs` globs the two-level
  `<root>/*/*/_delta_log/*.json` (the segment before `_delta_log` = table, the one before that = schema);
  `SchemaNames` enumerates the discovered schemas + always `main`; `CreateSchema` materializes the
  `<root>/<schema>/` subfolder (`HostFs.CreateDir`, recursive) so a fresh schema works; `DropSchema` removes it
  recursively (`HostFs.RemoveDir`; `main` is protected). OneLake ignores the option (its layout is driven by the
  lakehouse schema-enabled flag). NO ABI change (the option rides the v37 ATTACH-options→JSON forwarding). Verified:
  `test/verify_delta_catalog_schemas.test` (23 — subfolder layout, same-name tables in two schemas don't collide,
  re-attach rediscovers schemas, CREATE/DROP SCHEMA, RENAME-unsupported-locally). **Empty created schemas don't
  survive re-attach** (no `_delta_log` to glob) — a documented limitation. **Gotcha found:** unqualified
  `staging.t` resolves to DuckDB's DEFAULT (memory) catalog, not the attached one — always use `db.schema.table`. **OneLake table discovery + DROP
  — via the ADLS Gen2 / OneLake DFS endpoint directly** (`FabricLakehouse`, Bridge; `Azure.Storage.Files.DataLake`
  12.21.0): DuckDB's azure glob can't recurse a OneLake `_delta_log` tree (mid-path wildcard → `type must be
  string, but is null`, duckdb-azure PR #174), so a OneLake root (`abfss://<ws>@onelake…/<lh>.Lakehouse/Tables`)
  lists its tables via the **Azure SDK `DataLakeFileSystemClient.GetPaths`** (NOT DuckDB's azure ext, NOT the
  Fabric `ListTables` REST API, NOT the SQL endpoint) — flat lakehouse → table dirs under `Tables/` (schema
  `main`), schema-enabled → schema dirs under `Tables/` then table dirs under each (`Tables/<schema>/<table>`);
  **local/S3/plain-ADLS roots keep the host-FS glob** (`DeltaCatalog.DiscoverTables` branches on
  `FabricLakehouse.IsOneLake`). This is immune to the glob bug AND the `ListTables` 400 on schema-enabled
  lakehouses, AND free of the SQL-endpoint sync lag (DFS reflects committed files immediately). **The Fabric REST
  API is kept ONLY for the schema-enabled flag** (`GetLakehouse.DefaultSchema` — authoritative even for an empty
  lakehouse, where the DFS structure alone is ambiguous) **+ workspace/lakehouse name→GUID resolution**
  (`WorkspacesClient`/`ItemsClient`; GUIDs in the path skip it). **CRITICAL — use the ASYNC DataLake APIs**
  (`GetPathsAsync`/`DeleteIfExistsAsync` + `GetAwaiter().GetResult()` at the boundary): the SYNC `GetPaths` uses
  `HttpClient.Send`, whose sync transport HANGS under the hostfxr-hosted CLR (a single discovery never returns —
  isolated to the sync path; the same call is ~1s in a normal console host) — same reason every other Bridge IO
  path is async. Auth = the **ATTACH'd azure SP secret** (`ATTACH '…OneLake…' (TYPE arrownet, PROVIDER 'delta',
  SECRET <azure_sp>)` → v39 foreign-secret path → `DeltaBackend.BuildConnectionString` appends a cred marker on
  the root → `DeltaCatalog` mints a `ClientSecretCredential`, mirroring DAX); the data files are still
  read/written through DuckDB's FileSystem (the opener + a DuckDB azure secret) — the DFS endpoint is used for
  table LISTING + DROP only. **Live Fabric tests use the gitignored `dax_secret.sql`** at the repo root (`CREATE
  OR REPLACE SECRET fabric_sp (TYPE azure, PROVIDER service_principal, TENANT_ID/CLIENT_ID/CLIENT_SECRET …)` — the
  Fabric-Warehouse SP; ATTACH `… (PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false)`; one secret serves DuckDB
  OneLake IO + the Fabric REST API + the DFS endpoint). **Schema-enabled AND flat lakehouse support — DONE +
  VALIDATED LIVE (2026-06-30) on workspace `Test`, lakehouses `LH` (schema-enabled, tables at
  `Tables/<schema>/<table>`) and `LH_no_schema` (flat, `Tables/<table>`).** `DeltaCatalog` is **multi-schema**:
  `GetMetadata(Schemas)` returns the lakehouse schemas (+ always the `DefaultSchema`), and `TablePath` is
  schema-aware (`<root>/<schema>/<table>` when schema-enabled, else flat `<root>/<table>`). **Validated live
  end-to-end:** on both lakehouses, DFS discovery + CTAS + scan + DROP (DFS recursive delete, confirmed the table
  dir is gone); plus on `LH` CTAS into `lake.dbo.arrownet_deltest` → `DELETE WHERE id=2` (deletion-vector commit)
  → `SELECT` returns 1,a/3,c. **`READ_ONLY false` is REQUIRED** for OneLake writes: DuckDB force-bumps any remote
  (`abfss://`) ATTACH to read-only when the access mode is AUTOMATIC (`database_manager.cpp:105`); Delta supports
  remote writes, so set it explicitly. **Caveat:** `SHOW TABLES` / `duckdb_tables()` over a OneLake catalog is
  slow (it materializes every table's COLUMNS = N OneLake `_delta_log` reads — DFS table LISTING itself is fast,
  ~1–7s; it's the per-table column fetch that's slow) — use targeted `lake.<schema>.<t>` access.
  **Fabric-compat history (2026-06-29, SUPERSEDED — kept as the trail):** the first DELETE used row tracking +
  deletion vectors. Two protocol bugs surfaced first (rowTracking missing its `domainMetadata` dependency →
  `DELTA_FEATURES_PROTOCOL_METADATA_MISMATCH`; DVs written without the `deletionVectors` reader-v3 feature →
  OneLake conversion `INTERNAL_ERROR`); declaring all features fixed the protocol, but Fabric/Spark STILL could
  not read it — engineered-wood's inline DV byte format isn't Spark-decodable (Fabric DOES support DVs, so it's
  our format). At that point we shipped plain Delta + copy-on-write + transient rowid (see the DELETE paragraph
  above) as the default — validated live. **DV FORMAT BUG NOW FIXED (upstream engineered-wood, verified against
  delta-kernel via DuckDB's official `delta_scan`).** Two bugs in engineered-wood's `RoaringBitmapWriter`/`Reader`:
  (1) it omitted the 64-bit **`RoaringBitmapArray` wrapper** — wrote `[magic][32-bit bitmap]` instead of
  `[magic][int64 sub-bitmap-count][int32 high-key + 32-bit bitmap]…`; (2) the inner 32-bit bitmap used a
  non-standard no-run cookie `((count-1)<<16)|12346` instead of the CRoaring portable form
  `[12346 full u32][int32 size][descriptive][offset-header-ALWAYS]`. Fixed both + made
  `RoaringBitmap.DeserializePortable` return bytes-consumed (to walk multi-sub-bitmap arrays); the reader now
  parses the array wrapper with the legacy bare-bitmap fallback. `delta_scan` reads an engineered-wood DV table
  with the deleted row correctly removed (`scratchpad/dvtest` harness). This also fixes engineered-wood's own
  `DeleteAsync` — report upstream. **Finding:** the Delta `rowTracking` FEATURE is NOT actually needed for DV
  deletes — an ABSOLUTE-position transient `(file, position)` rowid composes correctly across repeated DV deletes
  (the parquet file is never rewritten, so absolute positions are stable); rowTracking only adds stable-ids-across-
  compaction, which our DML doesn't use. **DELETION-VECTOR MODE DONE — opt-in, delta-kernel-verified.** Activate
  with the ATTACH option `deletion_vectors true` → tables CREATED in that catalog enable the `deletionVectors` +
  `rowTracking` features (`DeltaWriter.DeletionVectorConfig`; `CreateAsync` declares reader-v3 + the features).
  DELETE follows the TABLE's `delta.enableDeletionVectors` config (`DeltaReader.IsDeletionVectorsEnabled`):
  DV-enabled → `DeleteByRowIdsViaVectorsAsync` (union the new in-file positions into the file's DV, write a fresh
  DV, commit `remove`(old path+DV)+`add`(same path, new DV) — NO rewrite); else copy-on-write. Repeat DV deletes
  COMPOSE because the scan now emits **ABSOLUTE** file positions (`ReadFileAsync` tracks the pre-DV-filter index;
  SAFE for copy-on-write — no-DV tables have absolute==sequential). UPDATE on a DV table is copy-on-write: it reads
  the file WITH the absolute-rowid column (so it matches post-DV survivors) and rewrites a clean file, and its
  `RemoveFile` carries the file's DV so it matches the active `(path, DV)` entry (without that the old file stayed
  active → duplicated rows — the bug found in testing). Verified local (`test/verify_delta_catalog_dv.test`, 48 —
  DV delete + composition + UPDATE-on-DV + post-UPDATE DV delete + re-attach) + delta-kernel `delta_scan` read-back;
  copy-on-write delete (28)/update (63)/write (31) unregressed. **OCC RETRY DONE (concurrent writers):**
  engineered-wood `WriteCommitAsync` throws `DeltaConflictException` when a concurrent writer takes the target
  version; `DeltaWriter.Write`/`Create` (append/CTAS/create) catch it and retry by reopening at the new latest
  version (bounded `MaxCommitAttempts=16`) — the data is snapshot-independent so re-commit is safe. Rowid
  DELETE/UPDATE do NOT retry (their absolute positions are tied to the scanned snapshot; a concurrent change
  invalidates them) — `DeltaReader` surfaces a clear "concurrent modification — retry the statement" error.
  Verified: 4 parallel processes appending 200 rows each to ONE local Delta table → 800/800 distinct, no lost
  commits, no surfaced conflicts. **PROVIDER RENAMED to `engineeredwooddelta`** (the engineered-wood-backed Delta
  provider), with **`delta` + `deltalake` kept as aliases** (non-breaking — `BackendRegistry` resolves Name +
  Aliases case-insensitively; all `verify_delta_*` tests still ATTACH with `PROVIDER 'delta'`). The distinct
  primary name reserves space for a future delta-rs/delta-kernel production provider. `test/verify_delta_rename.test`
  (12 — new name + both aliases). Other remaining (OPTIONAL): a `delta-rs` production provider. See docs/delta-catalog.md + docs/filesystem-bridge.md. v47 =
  **host-FS global table functions**: appended one vtable entry `set_active_opener(opener)` — a per-thread ambient (`AmbientOpener`, mirroring `set_active_txn`) recording the
  calling operator's `ClientContext` so a connection-free GLOBAL host-FS table reader (a lakehouse format)
  resolves DuckDB secrets while reading through the host `fs_*` callbacks; set in the shared `PopulateReturnSchema`
  + `ArrowStreamInitGlobal` arrow-scan hooks, read by the host-FS binding in `Bind`/`Execute`. **REMOVED**
  `delta_schema`/`delta_scan` (a mid-struct removal → offsets of later entries shift; `abi.h` ↔ `Abi.cs` kept in
  lockstep, function/delta suites the gate): `arrownet_delta_scan` is now a pure-C# global `ITableFunction`
  (`DeltaGlobalTableFunction`) on the v29 table session — a new lakehouse format costs zero C++. See the
  load-time-global-functions bullet + [docs/global-functions.md](docs/global-functions.md) §"Host-FS global table
  functions". v46 = **load-time global functions**: appended one vtable entry
  `list_global_functions` (enumerate the provider-union of connection-free global functions at extension load);
  the scalar entries `get_function_param_schema`/`get_function_return_schema`/`execute_scalar` gained a
  **`handle==0`** branch that resolves a function by name against the C# global registry instead of a catalog
  (all five global kinds — scalar/in-out/collector/table/aggregate — reuse it).
  v42–v45 = the **host-query** feature, prior session. v41 (its `delta_schema`/`delta_scan`, removed at v47) was
  the **Delta lakehouse reader on the filesystem bridge** SPIKE: appended
  `delta_schema`/`delta_scan` to the vtable + `fs_glob` to `ArrowNetHostServices`. `arrownet_delta_scan(path)`
  reads a Delta Lake table via **engineered-wood** (Curt Hagenlocher's pure-C# Delta), with ALL IO through
  DuckDB's `FileSystem` over the host callbacks — so local/`az://`/`s3://`/`https://` + DuckDB secrets all work.
  C# `DuckDbTableFileSystem : ITableFileSystem` (root-relative paths, read-only) + `DuckDbRandomAccessFile` over
  the callbacks; `DeltaReader` = `delta_schema` (bind, `OpenAsync().ArrowSchema`, no data) + `delta_scan`
  (execute, `ReadAllAsync()` **materialized** into an `InMemoryArrayStream` while the opener is valid); C++
  `arrownet_delta.cpp` binds via `DeltaSchema`+`ReadArrowSchema`, scans via `DeltaScan`+`ArrowStreamReader`.
  DuckDB applies projection/filter/aggregation above the scan. engineered-wood is referenced from
  `ArrowNet.Bridge` (sibling repo `D:\repos\engineered-wood`, **Apache.Arrow 23.0.0 aligned**, net10.0) +
  published transitively. **One local engineered-wood patch:** `ActionSerializer` read optional `add`/`remove`
  numeric fields (`baseRowId`/`defaultRowCommitVersion`/remove `size`/`deletionTimestamp` — the **Delta
  row-tracking** fields) with a bare `GetInt64()` that throws on delta-rs's explicit `"field":null`; guarded with
  `TokenType==Null?null:GetInt64()`. Validated: `test/verify_delta.test` (39 — fixture `test/fixtures/delta_simple`,
  a delta-rs 10-row table; full scan + filter/aggregate + DESCRIBE + join). SPIKE, not a user feature. See
  [docs/filesystem-bridge.md](docs/filesystem-bridge.md). A faster future path — engineered-wood as a Delta
  **snapshot/file-list provider** feeding DuckDB's C++ **`MultiFileReader`** + native parquet reader (the
  architecture of DuckDB's own `delta` ext, swapping delta-kernel-rs for the C# log layer), with a cheaper
  `host_query`+`read_parquet` middle-ground first — is captured as a design note (deferred, nothing built):
  [docs/multifile-delta.md](docs/multifile-delta.md). A separate deferred note,
  [docs/delta-catalog.md](docs/delta-catalog.md), covers a **Delta WRITE-BACK** path: expose a Delta **folder
  as an ATTACH catalog root** (`ATTACH '/lake/root' (TYPE arrownet, PROVIDER 'delta')`; each `_delta_log` subdir
  = a table, discovered via `fs_glob`) as the 3rd `IBackend` reusing the provider-agnostic C++ catalog/scan/DML
  wholesale. engineered-wood is full read-write (INSERT/DELETE/UPDATE via `WriteAsync`/`DeleteAsync`/`UpdateAsync`
  + deletion vectors + commit writing; NO merge). Clean first slice = read + INSERT + CREATE (no rowid); the one
  real decision is DELETE/UPDATE = rowid→deletion-vector (fits our rowid DML, needs an engineered-wood
  position-delete) vs predicate (`FilterNode`→engineered-wood `Predicate` via its Arrow row evaluator — clean,
  gap-free in that direction); UPDATE-SET evaluation + Delta's per-commit (non-cross-table-ACID) semantics are
  the caveats. The note also records **scan data-skipping**: engineered-wood file-pruning (Delta `add` stats +
  partition values) works once we pass the predicate; row-group/bloom skipping exists in its parquet reader but
  isn't plumbed through the Delta filter path (small engineered-wood fix); and **dynamic (join/TopN) filters**
  can be applied by reading the live `TableFilterSet` (`TableFunctionInitInput.filters`) at execute time +
  merging into the predicate — the Delta scan's per-file C# loop even lets it re-check before each file. v40 =
  filesystem reverse-callback SPIKE/foundation: a new `ArrowNetHostServices`
  struct (host→managed function pointers: `fs_open_read`/`fs_size`/`fs_read`/`fs_close`/`free_str`) is passed
  to `Bootstrap.Initialize(vtable, size, host)` so the managed side can call back into DuckDB's `FileSystem`
  (secret-backed remote IO via DuckDB), plus an `fs_spike` vtable entry + `arrownet_fs_spike(path)` table fn
  that proves it. Key finding: `FileSystem::GetFileSystem(context)` is an `OpenerFileSystem` that AUTO-pushes
  the context's `FileOpener` (secrets) — open with NO explicit opener. Validated: local parquet (PAR1 footer)
  + remote https via httpfs (range GET). Foundation for a future C# lakehouse-format provider (engineered-wood
  Delta/Iceberg/Lance/… reusing DuckDB IO + secrets). See [docs/filesystem-bridge.md](docs/filesystem-bridge.md).
  v39 = foreign-secret consumption: `build_connection_string` gained
  `secret_type` + `base_connstr` args so a provider can reuse a secret of ANOTHER extension's type. C++
  `BuildConnectionStringFromSecret` resolves ANY secret (`IsMssqlNetSecret`→`IsKnownSecret`) and passes the
  type + fields + the ATTACH target to C#; `SqlServerBackend` maps an `azure` service_principal/managed_identity
  secret to Entra auth merged onto the ATTACH target (`ATTACH 'Server=…;Database=…' (TYPE mssql_net, SECRET
  <azure_sp>)`), and rejects `credential_chain` (storage-scoped/lazy token) pointing to
  authentication='Active Directory Default'. Validated end-to-end against a live Fabric Warehouse (manual);
  error paths in `verify_azure_secret.test` (`require azure`). See [docs/provider-extensibility.md](docs/provider-extensibility.md) §2.1.
  v38 = the secret-field declaration refactor: a `list_secret_fields` entry was
  appended; the provider declares its secret type + fields in C# (`IBackend.SecretType`/`SecretFields`), and
  `RegisterProviderSecrets` registers one DuckDB secret type per declared type generically (fields = the
  `CREATE SECRET` named params; one shared `CreateProviderSecret` keyed by `input.type`). The C++ core names
  no secret type/field — the `kHost…` constants, `ValidateFields`, and `CreateMssqlNetSecret` are gone;
  validation moved to C# `BuildConnectionString` (surfaces at connect time). `IsMssqlNetSecret`→
  `IsProviderSecret`. See [docs/provider-extensibility.md](docs/provider-extensibility.md) §2.
  v37 = the ATTACH-options→C# refactor: `open_catalog` gained an `options_json`
  arg carrying the provider-owned ATTACH options as a flat JSON object, and `inout_exchange_open` dropped its
  `isolation` arg. `MssqlNetAttach` now extracts only PROVIDER/SECRET (meta) and forwards every other option
  as JSON; C# `SqlServerCatalog` parses `schema_filter`/`table_filter` (applied in `get_metadata`, with the
  regex validated C#-side) + `isolation_level` (resolved with `mssql_isolation_level` in `InOutBind`). The
  C++ `CatalogFilters`/`ValidateCatalogFilters`/`ResolveInOutIsolation` + the catalog's filter/isolation
  members are gone; function discovery is schema-filtered by only registering functions whose schema is
  already registered. See [docs/provider-extensibility.md](docs/provider-extensibility.md) §3.
  v36 = the `mssql_net_exec` join-only refinement: `set_active_txn` gained an
  `int32 join_only` arg — a raw exec joins the active transaction's pinned connection iff one already exists
  (atomic with an in-flight DuckDB-managed write, e.g. a dbt post-hook adding an index to the model), else
  autocommits without pinning; fixes the in-transaction-hook self-block, see [docs/dbt-hooks.md](docs/dbt-hooks.md) §3.
  v35 = the write-concurrency fix: `begin_bulk`'s `autocommit` int32 arg became
  `int64 txn_id`, and one new entry `set_active_txn(handle, txn_id, …)` was appended — the host sets the active
  DuckDB `global_transaction_id` so the managed side keys a per-transaction provider connection; see the
  "Per-DuckDB-transaction connections" bullet under Transactions + [docs/transaction-concurrency.md](docs/transaction-concurrency.md).
  v33/v34 = the settings refactor: v33 added `list_settings` + `set_setting`; v34 dropped `create_table`'s
  `text_type` param. v32 changed `get_function_param_schema`/`get_function_return_schema`/
  `get_function_output_schema` to fill a bare `ArrowSchema *out` instead of an `ArrowArrayStream *out` — they
  are schema-only, so the zero-row-stream-carrying-a-schema is gone; C# returns `Schema`, C++ reads it via
  the new `ReadArrowSchema` (sharing a `ReadSchemaColumns` core with `PopulateReturnSchema`). A signature
  change, not a slot change → no offset shift. v31 **removed** the dead 4g push entries `inout_open`/`inout_push`/
  `inout_finish`/`inout_abort` — every `_each` form runs on the streaming exchange since `9056eae`; the
  C++ push operator + `IInOutSession`/`InOutOpen` went with them. **Mid-struct** removal (they sat before the
  agg/exchange/table entries), so `abi.h` + `Abi.cs` field order stays in exact sync — the function/agg/in-out
  suites are the alignment gate. v30 **removed** `execute_table`/`execute_proc` — superseded by the v29
  table-function session; they had been unused in C++ since v29, so this is the cleanup. NOTE: that too was a
  **mid-struct** removal (between `get_function_output_schema` and the inout entries), so it shifted every later
  entry's offset — `abi.h` + `Abi.cs` field order must stay in exact sync. v29 appended the three
  **table-function session** entries `table_bind`/`table_execute`/`table_close` — the session-handle
  successor to `execute_table`/`execute_proc`; see the "Table-function session — DONE" bullet under
  "Function-abstraction refactor (Phase 5)".
  v28 appended the three streaming table-in-out **exchange** entries `inout_bind`/
  `inout_exchange_open`/`inout_bind_close` — Phase 6, see "Streaming table-in-out exchange (Phase 6)" below.
  v27 added a nullable `args` 1-row stream to `get_function_output_schema` so a
  custom table function's output schema can depend on its constant arguments — the **table `Bind`** capability;
  see "Callable table functions (4c)". v26 appended the three spillable-aggregate entries `agg_update_spill`/
  `agg_combine_spill`/`agg_finalize_spill` + the `ARROWNET_AGG_SPILL_CAP` constant — 4h opt-in spill; v25
  appended the six custom-aggregate entries `agg_open`/`agg_update`/`agg_combine`/`agg_finalize`/`agg_destroy`/
  `agg_close` — 4h; v24 added `inout_open`'s `isolation` arg so the in-out session runs its per-chunk CROSS
  APPLY in one transaction at that SQL isolation level for a consistent view). **Bump rule:** when you add a
  vtable entry OR change a signature, bump
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
    (the loadable; needed to `LOAD` into a duckdb that does NOT embed it — e.g. the **official `duckdb==1.5.4`
    Python wheel** for the dbt-duckdb concurrency tests). **To load into the official wheel, reconfigure with
    `-DOVERRIDE_GIT_DESCRIBE=v1.5.4`** so the extension footer declares `duckdb_version=v1.5.4` — the shallow
    submodule has no git tag, so it otherwise defaults to `v0.0.1` and the official engine rejects it on the
    version check (NOT bypassed by `allow_unsigned_extensions`). Then `LOAD` with `allow_unsigned_extensions`
    + set `ARROWNET_MANAGED_DIR` (the bridge isn't next to the python `.pyd`). Verified loads + ATTACH +
    query against the official wheel. (This also fixes `json`/`icu` autoload, though we embed those.)
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
- **The C++↔C# Arrow boundary always uses STANDARD encoding** (`arrownet::BoundaryClientProperties`, used at
  every DuckDB→Arrow site instead of `context.GetClientProperties()`): it keeps the session time zone +
  Arrow output version but forces `arrow_lossless_conversion`/`arrow_offset_size`/`produce_arrow_string_view`/
  `arrow_use_list_view` to their standard form. Our bridge maps Arrow→provider types itself, so a user's
  **global** `SET arrow_lossless_conversion = true` must not change our boundary encoding — otherwise DuckDB
  exports `BOOLEAN` as Arrow `Int8` and our mapper turns it into SQL `SMALLINT` (1/0) instead of `BIT`
  (true/false), and `HUGEINT` into `nvarchar`. Verified: `test/verify_arrow_lossless.test`.
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
`SqlServerFlights` (reusable C# SqlClient/DAX→Arrow; its `Airport/Data` `ArrowTypeConverter.cs`/`FlightField.cs`
are the granular type-conversion reference — original SQL type + precision/scale/length carried on Arrow
field metadata for precise + lossless round-trip, and Arrow extension names `arrow.bool8`/`arrow.uuid`/
`arrow.json` to disambiguate same-storage types; see [docs/warehouse-support.md](docs/warehouse-support.md)
§3.4 for the future type-mapping refinement), `ArrowSerializer` (POCO↔Arrow for Phase 3),
`vgi` (source-available — never copy code, design patterns only).
