# CLAUDE.md — project knowledge for `fabricator`

> Canonical project memory. Maintained in the repo (not in per-user agent memory) so it's
> easy to edit and shared across machines. Keep this current as the implementation evolves.

## What this is

`fabricator` is a **DuckDB extension** that connects DuckDB to **Microsoft SQL Server** by hosting a
C# layer (**CoreCLR, in-process**) and exchanging data + metadata as **Apache Arrow** over the Arrow
C Stream Interface (`ArrowArrayStream`). It is a direct, in-process replacement for the Arrow-Flight
transport used by the "Airport" extension.

Unlike the native-TDS sibling `mssql-extension` (`D:\repos\mssql-extension`, the compatibility
target), **all SQL Server I/O happens in C# via `Microsoft.Data.SqlClient`**; the C++ extension only
registers DuckDB functions and ingests Arrow. Full phased plan:
`C:\Users\c.mettler\.claude\plans\i-want-to-create-soft-crown.md`.

### THE FABRICATOR RENAME (2026-07-15, breaking — no aliases)

The extension + generic core was renamed **ArrowNet/`mssql_net` → `Fabricator`/`fabricator`** ahead of publish
(one branch: `refactor/fabricator-rename`). All the old `mssql_net_*` and `arrownet_*` names are **GONE** — no
back-compat aliases were kept (user decision). What is now `fabricator`:
- **Extension**: `LOAD fabricator`, `ATTACH … (TYPE fabricator)`, catalog-type string `"fabricator"`, entry
  `DUCKDB_CPP_EXTENSION_ENTRY(fabricator, …)`, artifact `fabricator.duckdb_extension`.
- **User functions**: `fabricator_query` / `fabricator_exec` / `fabricator_refresh_cache` /
  `fabricator_invalidate_cache` / `fabricator_version` / `fabricator_server_info` / `fabricator_functions` /
  all `fabricator_delta_*` (single registration each — the old dual `mssql_net_*`+`arrownet_*` aliasing removed).
- **C++**: namespace `fabricator`, dirs `src/fabricator/` + `src/include/fabricator/`, files `fabricator_*.cpp/hpp`
  (incl. `fabricator_extension`/`_secret`/`_storage`, `copy/fabricator_copy`), classes `Fabricator*` (catalog/
  schema-entry/etc.), internal scan fn `"fabricator_scan"`.
- **C#**: projects/assemblies/namespaces `Fabricator.Bridge` / `.SqlServer` / `.AnalysisServices` / `.DeltaRs` /
  `.Abstractions` / `.SamplePlugin`; bridge entry `Fabricator.Bridge.Bootstrap`; managed dir published to
  `build/release/extension/fabricator/fabricator/`.
- **Env vars / ABI constants**: `FABRICATOR_*` (`FABRICATOR_MANAGED_DIR`, `_DOTNET_ROOT`, `_BACKEND_ASSEMBLY`,
  `_PLUGIN_DIR`, `_LOG_LEVEL`/`_LOG_FILE`, `_DELTA_WRITE_DIR`, `_DELTA_PREFETCH`, `_ABI_VERSION`, `_META_*`, …).

**Provider-scoped names deliberately KEPT** (a setting/URI/secret/format names its PROVIDER, not the extension —
the DAX provider has `dax_*`, Delta `delta_*`): the ~35 SQL-Server settings stay `mssql_*` (`mssql_mars`,
`mssql_isolation_level`, `mssql_default_varchar_length`, …); the SQL `mssql://` URI shorthand; the SQL secret
**`TYPE mssql`** (was `mssql_net`); the SQL bulk COPY **`FORMAT mssql`** (+ `bcp`); `PROVIDER 'sqlserver'|'delta'|
'dax'|'deltars'|'engineeredwooddelta'`; secret FIELD names. **Gotcha for future edits:** `TYPE mssql` in an
ATTACH is the storage-extension keyword → must be `fabricator`; `TYPE mssql` in a CREATE SECRET is the secret
type → stays `mssql`. Renamed on the branch + validated (representative verify sweep green; loadable rebuilt).

### BRIDGE-CROSSING LOGGING (2026-07-15, additive, off by default)

Expanded the `FabricatorLog` (ILogger) coverage so a query/filter/mode/DDL/crossing is visible without a
profiler. Off by default (`FABRICATOR_LOG_LEVEL`, file sink `FABRICATOR_LOG_FILE`, + the `host_log` forwarding to
`duckdb_logs`). Categories: **`Fabricator.Bridge`** — EVERY *failed* ABI crossing logged centrally (a
`[CallerMemberName]` on `Bootstrap.SetError` records the op name + exception, so no per-handler code), plus
`open_catalog` (provider+options; connstr NEVER logged — password) / `get_metadata` (kind+args) control
crossings; **`Fabricator.Sql`** (new, in `SqlServerCatalog`) — every T-SQL statement: scans with the pushed
projection/WHERE/TOP/ORDER BY, the connection routing (pinned/pooled, read-your-writes, txn id, param count),
DML (`dml … DELETE/UPDATE …`), DDL (`ddl create/alter …`), and bulk (`bulk <table>: create/replace/
checkConstraints/options` + `N rows copied`); **`Fabricator.Delta*`** — already rich (bulk/write/scan mode /
native_filter / active·scanned·pruned files / resolved snapshot version). Logging OFF is byte-neutral (verify
suites unaffected). It immediately surfaced a pre-existing caught load-time `GetFunctionParamSchema` null-`fields`
WARN (benign — global functions pass).

### Sync-over-async cleanup — CONVENTION established, remainder is incremental

The documented shape (thin sync ABI wrapper that blocks ONCE on a private `async` core using
`ConfigureAwait(false)` throughout) is now demonstrated + verified on the leaf `DeltaReader.GetActiveFileUris`
(exemplar with an inline doc comment; native_read suite green — confirms the AsyncLocal ambients flow across the
pool-thread hops). **`DeltaReader.cs` is now FULLY converted (2026-07-15, C#-only)**: every leaf ABI-facing method
(the read-only log probes `GetSchema`/`GetSchemaAndRowTracking`/`IsDeletionVectorsEnabled`/`IsColumnMapped`/
`GetTxnDmlProfile`/`GetAppTransactionVersion`/`GetOrderedActiveBaseRowIds`/`ComputeSchemaChange`/`ResolveVersionAsOf`/
`GetSchemaAt`; the DML/maintenance ops `DeleteByRowIds`/`DeleteByRowIdsViaVectors`/`UpdateByRowIds`/`Optimize`/
`Vacuum`/`AddColumn`/`AddField`/`RenameField`/`DropField`/`RenameColumn`/`DropColumn`; the list/read-back paths
`ListNativeScanFiles`/`ListScanFilesJson`/`ReadRowsByRowIds`) is now a thin sync wrapper (`=> XxxAsync(...).GetAwaiter().GetResult()`)
over a private `XxxAsync` core using `ConfigureAwait(false)` at every await — retiring the per-await
`.GetAwaiter().GetResult()`/`.AsTask().GetAwaiter().GetResult()` form. (The `Stream*`/`GetChanges`/`GetSnapshots`
paths already had async cores; their remaining single blocking points are deliberate schema-peeks, not per-await.)
Verified: the FULL delta sweep green (24 suites, ~2500 assertions — write/delete/update/optimize/alter/native_read/
native_write/native_write_streaming/transactions 941/time_travel/row_tracking_virtual 299/txn_version/late_mat/
column_mapping/partition/partition_overwrite/dv/dv_default/variant 133/nested_alter/struct_filter/dynamic_filter/
compaction_rowtracking/copy_format/decimal/temporal/schemas/constraints/rename). **`DeltaWriter` (in
`DeltaGlobalTableFunction.cs`) + the self-contained `DeltaCatalog` flush helpers are ALSO converted (2026-07-15):**
DeltaWriter `Write`/`Create`/`Materialize`/`MergeSchema` + `TryWriteStreaming` (the `out rowsWritten` case → its async
core returns a `(long? Result, long RowsWritten)` tuple, the sync wrapper unpacks the out — the compiler enforces
every return is a tuple, so a missed conversion is a build error not a silent bug; `TryStreamCreateFiles` was already
sync — RunCopy is synchronous). **`DeltaCatalog.cs` is now FULLY converted too**: the flush helpers
(`FlushDeferredFiles`/`WriteCdcFiles`/`TryEagerWriteBatches`), the orchestrators (`FlushDmlTransaction` — the
~200-line buffered-DML commit/rebase hot path — and `FlushCreateTransaction`), and the stream-consuming paths
(`ReadFilterValues`; `ExecuteDelete`/`ExecuteUpdate` — their rowid/update-stream drains extracted into
`CollectRowIds`/`ParseUpdateStream` async-core helpers so the txn/rewrite branching stays sync; the S3-rename
copy+delete fallback → `MoveFilesByCopy`). All open EW directly (or block once on a DeltaReader/DeltaWriter leaf
wrapper). **The `DeltaGlobalTableFunction` reader filter-loop (`ReadValues`) is converted too, and `OneLakeForwardFs`
was found ALREADY conformant** — every one of its methods has a SINGLE async call blocked once at the top (the
documented block-once shape; `Glob` is already a proper wrapper→core), which IS the convention (the per-await
anti-pattern only arises in MULTI-await methods; a single-await method blocking once is correct as-is). **⇒ the Delta
bridge sync-over-async cleanup is COMPLETE** (DeltaReader / DeltaWriter / DeltaCatalog / DeltaGlobalTableFunction all
converted; OneLakeForwardFs conformant) — verified the FULL delta sweep green after each increment (commits `38a9e7f`
DeltaReader+EW-variant-fix / `382eda2` DeltaCatalog helpers / `a2fdb98` DeltaWriter / `97ca5f8` DeltaCatalog
orchestrators+stream-loops / + this final `ReadValues`). Any remaining `.GetAwaiter().GetResult()` in the Delta bridge
is now a single-blocking-point sync wrapper, not a per-await site. The ambient-loss landmine stays disarmed
(AsyncLocal, `0533eb7`). Adopt the sync-wrapper→async-core shape for NEW code now.
**Whole-codebase scan (2026-07-15) — the anti-pattern was DELTA-ONLY; nothing else needs converting:** a sweep of
every bridge assembly found the deeply-async-blocked-at-every-await anti-pattern existed ONLY in the Delta/EW bridge
(now done). The rest are NOT the anti-pattern and should be LEFT ALONE (converting is zero-value churn — no deadlock
risk since the hostfxr CLR has no `SynchronizationContext`, and the sync backend work dominates the thread anyway):
**`SqlServerBackend`** uses SYNC `Microsoft.Data.SqlClient` (38 sync `Execute*`/`WriteToServer`, 0 async) — its only
async touchpoints are Arrow C-stream boundary reads (`ReadNextRecordBatchAsync`), already block-once-per-batch in
otherwise-synchronous methods (e.g. `ExecuteScalar`'s loop body is a pure-sync `fn.Invoke`); **DAX/`Fabricator.AnalysisServices`**
is sync ADOMD (`ExecuteReader`, 0 async); **`Fabricator.DeltaRs`** already routes its delta-dotnet async ops through a
`Run()`/`Run<T>()` block-once helper (15 uses); **`FabricLakehouse`** uses proper wrapper→core + single-await block-once;
**`BulkSession`/`Bootstrap`** are single-await ABI-marshaling handlers (block-once). So the RAW `.GetAwaiter().GetResult()`
grep counts across the bridges are now ALL either single-blocking-point sync wrappers or already-conformant
Arrow-boundary reads — do NOT treat a nonzero count as remaining work. The sync-over-async initiative is DONE.

### THE EW CLAST-MASTER RE-PIN (2026-07-22 — the current engine; full record: [docs/ew-master-migration.md](docs/ew-master-migration.md))

The engineered-wood submodule pin moved from our long-lived fork lineage (`99e2c3a`) onto
**clast-project/engineered-wood master (`e48f449`, Curt's PR#4-parity landing) + the additive
`fabricator-patches` branch** (7 commits, pushed to the cmettler fork, pin `7fecc2b`;
`.gitmodules` `branch = fabricator-patches`). The strategy: fabricator-specific needs live as a
SMALL upstreamable patch set ON TOP of clast master — never a fork again — so future EW bumps are
merge-master-into-fabricator-patches + re-pin. What the patches carry: `DeltaFilePruner` public
(**a replacement was proposed to Curt 2026-07-25 and is awaiting his call**: a `DeltaTable.PlanFiles(filter,
snapshot, schema) -> IReadOnlyList<PlannedFile>` planning API instead of exposing the pruner class. The
motivation is not encapsulation — it is that our one call site, `DeltaReader.BuildNativeScanListAsync`, also
re-implements EW's PRIVATE `OrderedActiveFiles` ordering by hand, and that ordering defines the file ordinal
in the transient rowid `(ordinal << 40) | position` which EW itself DECODES. Encoded by our copy, decoded by
theirs, with nothing enforcing agreement; a planner that returns the ordinal deletes that hazard. The
signature must carry three things or our call site cannot move: the PRE-prune global ordinal, an optional
prune-schema override (we plan against a buffered txn's pending schema), and a caller-supplied snapshot (the
clustered-OPTIMIZE rewrite lists against the same snapshot its commit pins). Deliberately NOT async and NOT
DV-resolving — see the reply for why);
create-time `configuration`/`preAssignedSchema`/`materializedRowIds` params; rowid read-back
`rowIdsOut` correlation + derived-id fallback + CoW CDF capture + partition-aware cdc writes +
DV-aware CDF inference; schema-evolved compaction fixes; the **narrow-int parquet write-corruption
fix** (1-/2-byte Arrow arrays reinterpreted at the 4-byte physical width — silent corruption,
pre-existing, upstream-candidate); pass-through source-field relabel fixes (WidenBatch/
BackfillMissingColumns); and the **variant TRANSPORT** (`SchemaConverter.VariantTransportExtensionName
= "fabricator.variant"`, `VariantTransport` blob⇄`VariantArray` at EW's host boundary,
`DeltaTableOptions.VariantTransportBlob` — EW's INTERNAL model is now the canonical
`arrow.parquet.variant` `VariantType`; the Bridge sets the option in `DeltaWriter.Options()` and
converts advertised schemas via `VariantMarker.ToTransportSchema`). Bridge-side migration:
`IDataFileRewriter` retired (EW owns rewrite semantics; only the encoding seams remain), UPDATE on
the host-join `UpdateByRowIdsAsync(RecordBatch)` + a composed merge-on-read (`MergeOnReadUpdateAsync`
in DeltaReader), decimal widening via `DecimalOutput=Decimal128` read option, writer seam
`IAsyncEnumerable`. **Capability gain: pure-codec variant REWRITES work** (the fork gated them);
**the one capability regression is CLOSED (2026-07-23, EW-only on fabricator-patches): buffered DML
through a concurrent OPTIMIZE/rewrite now REMAPS again** — clast master already shipped the full
stable-id remap (`RemapRowLevelDeletesAsync`, its "Layer 3 (B)", serving autocommit +
`DeltaTransaction`); the buffered surface's `RebaseDvDmlActionsAsync` just threw on a vanished path.
Now it collects rewritten-away touched paths as `DeleteDvEdit`s and routes them through that SAME
remap (row tracking required — without it the clean rewrite conflict remains; the remap's new-file DV
pairs keep their own baseRowId, no HWM impact; the fork's bespoke `RemapRowsAcrossRewriteAsync` stays
retired). No Bridge/ABI change. EW BufferedTransactionTests +3 (Table.Tests 421);
`verify_delta_row_level_concurrency` back at the fork-era 70 (§5 buffered DELETE + §8 buffered UPDATE
compose through OPTIMIZE; §9 = precise "row-level conflict"); regression transactions 941 /
row_tracking_virtual 299 / optimize 40 / dv_default 58 / update 63 / delete 28 green. This closes
PR #4's "Known follow-up". Original migration validation: 49/49 delta suites at
full counts (variant now 144), EW suites green, and the LIVE OneLake/Spark round-trip incl. row-id
parity both directions + Spark decoding codec-written variant. **Fork-era EW notes below this point
are HISTORICAL** — they describe the retired fork lineage; the mechanisms survive but live in the
fabricator-patches shapes above.

## Architecture (layered for reuse)

Layered so a future **Power BI / DAX** connector reuses the same C++ core + managed bridge:

- **C++ generic core** — `namespace fabricator`, dirs `src/fabricator/` + `src/include/fabricator/`:
  `clr_host` (CoreCLR bootstrap + vtable wrappers), `arrow_ingest` (ArrowArrayStream → DataChunk),
  `arrow_produce` (DataChunk → ArrowArray), `abi.h` (the C ABI contract).
- **C++ DuckDB-API layer** — `namespace duckdb`, classes named `Fabricator*`, files `fabricator_*`:
  catalog / schema_entry / table_entry / transaction / metadata (`src/catalog/fabricator_*`), DML
  insert / modify / ctas (`src/dml/fabricator_*`), optimizer (`src/fabricator_optimizer.cpp`). The
  internal catalog scan function is `"fabricator_scan"`.
- **C++ provider layer** — keeps the `fabricator` / `Fabricator*` name: extension entry
  (`src/fabricator_extension.cpp`), `fabricator_secret`, `fabricator_storage` (ATTACH/connstr),
  `src/copy/fabricator_copy.cpp`, and all user-facing names (extension `fabricator`, functions
  `fabricator_query`/`_exec`/`_refresh_cache`/`_invalidate_cache`, `TYPE mssql`, `mssql_*`
  settings, `mssql://` URI, the `"fabricator"` catalog-type string).
- **C# `Fabricator.Bridge`** (`dotnet/Fabricator.Bridge`) — backend-agnostic: C-ABI `[UnmanagedCallersOnly]`
  exports + vtable (`Bootstrap.cs`, `Abi.cs`), handle table, Arrow export/import, `IBackend`/
  `IBackendCatalog`, `ArrowDataReader` (IArrowArrayStream→DbDataReader), `BulkSession`/
  `ChannelArrowStream` (streaming bulk), `StubBackend`.
- **C# `Fabricator.SqlServer`** (`dotnet/Fabricator.SqlServer`) — the `Microsoft.Data.SqlClient` backend +
  composition root; published self-contained next to the extension. Discovered via `BackendRegistry`
  reflection (env `FABRICATOR_BACKEND_ASSEMBLY`, default `Fabricator.SqlServer`).

### Target architecture: ONE binary, MULTIPLE providers (corrected goal, 2026-06-20)

The end goal is a **single `fabricator` extension binary that hosts several providers** (SQL Server via
SqlClient, Power BI/DAX via ADOMD, …) — NOT a separate binary per provider. Implications (planned;
current code still uses the single-provider `fabricator` naming):

- **Generic user-facing names**: `fabricator_query` / `fabricator_exec` (not `fabricator_query`). The user is
  fine breaking `gen_mssqlcompat_tests.sh` and renaming the kept tests.
- **Dispatch is handle/catalog-based** and already works: `Handles.Resolve<IBackendCatalog>(handle)`
  returns a backend-specific catalog, so any ABI call already routes to the right provider. Multi-provider
  mainly needs: C# `BackendRegistry` keyed by provider name (providers self-register, not `Active`=one) +
  **provider selection at open time** (`ATTACH … (TYPE fabricator, PROVIDER 'sqlserver')`, or inferred from
  the `mssql://`/`dax://` scheme, or the secret's provider). `open_catalog` ABI gains a `provider` arg; the
  catalog-type string becomes the generic `"fabricator"` (provider stored on the catalog).
- **Provider-specific logic lives in C#**: connection-string assembly + auth mapping (move out of
  `fabricator_secret.cpp`), type mapping, all SQL. The C++ `fabricator` core owns registration + dispatch +
  the function machinery, reused verbatim by every provider.
- **Custom scalar / table / table-in-out functions** (Airport-style, Phase 3) drive this. Two registration
  phases through one ABI shape (`list_global_functions(provider)` / `list_catalog_functions(handle)` +
  `execute_scalar`/`execute_table`/`execute_inout`, decls = Arrow-serialized name/kind/in-schema/out-schema/
  decl_id): **(A) load-time global** via `loader.RegisterFunction` — DuckDB only allows global registration
  during `Extension::Load()`, so this forces the **bridge to boot at extension load** (not lazily);
  **(B) attach-time catalog-bound** — discovered SQL Server procs/UDFs become `ScalarFunctionCatalogEntry`/
  `TableFunctionCatalogEntry` in `FabricatorSchemaEntry` (resolved as `db.schema.proc(args)`, refreshable via
  the existing cache invalidation). New core file `fabricator_functions.{hpp,cpp}` holds this. Table-in-out
  (`in_out_function`) is the hard part → Phase 4. **Full design: [docs/custom-functions-design.md](docs/custom-functions-design.md)**
  (ABI, the C# authoring API — lambda / attribute(SQLCLR-style, columnar) / derived — and
  `sp_describe_first_result_set` late-binding for table procs).
- Suggested order: (1) **C# multi-backend registry — DONE** (`BackendRegistry` is provider-keyed:
  `IBackend.Name`/`Aliases`, `Resolve(provider)`, `Active`=default; multi-assembly discovery via
  `FABRICATOR_BACKEND_ASSEMBLY` comma-list; SqlServer = `"sqlserver"`/alias `"mssql"`. Behavior-preserving —
  `Active` still routes to SqlServer); (2) **provider selection — DONE** (`open_catalog(provider,…)` ABI
  v17 → `BackendRegistry.Resolve`; ATTACH `PROVIDER` option + `scheme://` inference; clean unknown-provider
  error). The **generic names are now live as ADDITIVE ALIASES** (no breakage): `fabricator_query`/`fabricator_exec`/
  `fabricator_functions`/`fabricator_server_info` (+ the existing `fabricator_version`) and `ATTACH … (TYPE fabricator)`
  (the storage extension is registered under both `fabricator` and `fabricator`) — `test/verify_generic_names.test`.
  The **breaking removal** of the `fabricator_*` names (+ catalog-type string `"fabricator"`, settings/secret/URI
  scheme rename, compat-corpus regen) remains the separate full-rename pass;
  (3) **connstr/auth → C# — DONE** (`build_connection_string` ABI v18: `fabricator_secret.cpp` reads the
  secret + emits its fields as JSON, `SqlServerBackend.BuildConnectionString` assembles the SqlClient
  connstr; `MapAuthentication`/`QuoteConnValue`/the access-token marker are now C#-only — C++ has no connstr
  knowledge); (4) dynamic functions — **(4a) function discovery DONE** (`fabricator_functions(catalog)` table
  fn + `FABRICATOR_META_FUNCTIONS`); **(4b) attach-time catalog-bound scalar UDFs DONE** (discovered scalar
  UDFs become `ScalarFunctionCatalogEntry` in `FabricatorSchemaEntry`, resolved as `db.schema.fn(args)` and
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
  `FabricatorSchemaEntry::inout_functions_`. (2) **Output is emitted SYNCHRONOUSLY per input chunk** (each
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
  bare-name `{TABLE}` entry (`GetOrCreateCustomInOutFunction` + `FabricatorCustomInOutBind`, output = the fn's
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
  (`RegisterFabricatorInOutFinalizer`) wraps each in-out `LogicalGet` (identified by `function.in_out_function
  == FabricatorInOutFunction`) in a pass-through `LogicalExtensionOperator` whose `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 and, in `OperatorFinalize`, calls `holder->Finish()`
  → C# `inout_finish`. This is the reliable single "in-out finished" signal (fires **once**, sink-level, even
  above a parallel UNION — verified empirically + via `MetaPipeline`/executor finish-event scheduling),
  intended as a C# resource-cleanup hook + a clean commit of the read-only TVF's snapshot transaction
  (NOT the proc commit). **4g (table-in-out) is fully complete.**

## Next up (open threads for future sessions)

In-flight / planned refactors (all C#-only unless noted; tests stay green per slice):
- **SINGLE-FILE DISTRIBUTION — BUILT AND WORKING ON WINDOWS *AND LINUX* (2026-07-25; phases 1-4 of 5
  — remaining: user-facing docs + CI): [docs/distribution-installer.md](docs/distribution-installer.md)
  §12 (spike) + §14 (Installer.Core) + §15 (AOT shell + packaging) + §16 (linux).** ONE
  `fabricator.duckdb_extension` now installs and runs itself: `LOAD` it into an EMPTY extension
  directory with ZERO env vars → ~2 s cold (extract + chain-load + CLR boot), **0.01 s warm**, then
  `fabricator_version()`, managed calls and a Delta CTAS round trip all work. **Both platforms pass
  the same 12 checks** (`test/distribution/smoke_distribution.py`, incl. both must-not-touch-disk
  rejections): **windows_amd64 61 MB standalone** (self-contained runtime) + **linux_amd64 40 MB
  standard** (framework-dependent — the Fabric-notebook-relevant SKU; the bridge booted on the
  preinstalled runtime with NO env vars). Build with `scripts/pack-distribution.ps1` (core → managed
  publish → AOT publish → pack → footer; each step skippable, `-WithNegatives` emits the harness's
  failure-path artifacts, `-CorePath`/`-ManagedPath` let ONE machine assemble another platform's
  artifact since only the C++ core + AOT shell must be built on-target). Ship ONE
  `fabricator.duckdb_extension` per (DuckDB version × platform × SKU): a **NativeAOT C#
  installer extension** (on the MIT `DuckDB.ExtensionKit`, `D:\repos\DuckDB.ExtensionKit` —
  C_STRUCT ABI ⇒ DuckDB-version-portable outer shell) carrying the real payload
  (`fabricator_core.duckdb_extension` = today's CPP-ABI loadable with an added forwarding
  `fabricator_core_duckdb_cpp_init` export, + the `fabricator/` managed dir, FDD ~20 MB or
  self-contained ~100 MB SKU) as a **polyglot append** (lib + payload + index + the DuckDB
  footer via `append_extension_metadata.py` — a plain file append, no resource compiler).
  At every load: version/platform gate (`PRAGMA version/platform` vs manifest, friendly
  error) → extract into `<extdir>/extensions/<ver>/<platform>/` (sha-marker idempotent,
  lock + staging) → nested `duckdb_query("LOAD '<extdir>/fabricator_core…'")`. Zero env
  vars: clr_host's default lookup is a hardcoded `fabricator/` next to the loaded module
  (clr_host.cpp:345). Source-verified keystones: nested LOAD is lock-safe (per-extension
  locks; we already autoload parquet during our own load), C_STRUCT footer checks the C API
  semver not the DuckDB version, name-LOAD works without an `.info`. Why C# not C++: resource
  embedding in C++ is 3 per-OS toolchain mechanisms (untestable off-platform, the macOS
  problem); the C# extraction/packaging logic is ONE code path, unit-testable as plain xunit
  anywhere — AOT only at publish (no cross-OS compile; per-OS machines still build+smoke).
  `allow_unsigned_extensions` stays required (both loads; unchanged vs today).
  **BUILT so far:** the spike validated every keystone on Windows against the OFFICIAL
  `duckdb==1.5.5` wheel — a C_STRUCT AOT kit extension loads, discovers its own
  `.duckdb_extension` path, reads a polyglot payload, and **chain-LOADs the CPP-ABI core, whose
  CLR boots with zero env vars** (managed calls `hilbert_index`/`bucket` prove it, not just
  `fabricator_version()`); a **138 MB artifact loads in 0.56 s** (R1 retired). Then
  **`dotnet/Fabricator.Installer.Core`** (net10.0;net8.0, `IsAotCompatible`, **zero package refs**)
  + `.Tests` — **91 tests green on both TFMs**: deterministic packer, polyglot writer/reader,
  extdir resolution, compatibility gate, extractor, staging/lock/promote state machine; plus a
  gated real-payload E2E (`FABRICATOR_E2E_CORE`/`_MANAGED`) that packs the built core + the 115 MB
  managed dir in ~4 s and whose install tree **loads in the official wheel with no env vars**.
  **Measured: the standalone artifact is 58 MB, not the estimated 90-110.**
  **FOUR FINDINGS (details in §14):** (1) the extension-directory rule in the design was WRONG —
  a custom `extension_directory` gets **NO `extensions/` component** (it exists only because the
  DEFAULT base string is `~/.duckdb/extensions`), and the plural `extension_directories` LIST
  setting is a second source; empirically pinned by watching where `INSTALL '<file>'` lands.
  (2) A zip's DOS timestamp has no timezone — .NET encodes the wall-clock part verbatim (so a
  fixed UTC epoch IS reproducible across build machines, which the payload-sha-as-marker scheme
  requires) but reattaches the READER's local offset on read; pinned at the byte level.
  (3) The payload must be packed at stream position 0 — zip offsets are stream-absolute, so an
  archive written at an offset is unreadable through the payload window. (4) Windows
  upgrade-in-use is largely SOLVED, not just reported: displaced files are RENAMED aside (the
  loader opens images with `FILE_SHARE_DELETE`, so rename is permitted where delete is not), so
  an upgrade succeeds while another session holds the old core loaded. Also **confirmed the
  forwarding export is genuinely required**: the same bytes that load as
  `fabricator.duckdb_extension` fail as `fabricator_core.duckdb_extension` (filename-derived
  entry symbol) — hence `CoreFileName` is a manifest field so today's name can be produced.
  **PHASE 3 (§15) added:** the core's **second entry point**
  `DUCKDB_CPP_EXTENSION_ENTRY(fabricator_core, loader)` (same `LoadInternal`; one binary, two file
  names), `dotnet/Fabricator.Installer` (the 2.9 MB NativeAOT shell — own-path discovery, gate,
  install, chain-LOAD; ~60 lines of flow), `dotnet/Fabricator.Installer.Pack` (build-time CLI over
  the Core so the ps1 is pure orchestration), `scripts/pack-distribution.ps1`, and
  `test/distribution/smoke_distribution.py`. **THREE MORE FINDINGS:** (1) the distinct core name is
  a HARD REQUIREMENT, not cosmetics — `BeginLoad` locks per extension NAME and a path-LOAD derives
  the name from the FILENAME, so an installer named `fabricator` chain-loading a file that also
  resolves to `fabricator` would block on its own load lock (this reordered the plan: the dual entry
  symbol had to land BEFORE the shell). (2) **`duckdb_fetch_chunk` takes `duckdb_result` BY VALUE**
  (a 48-byte struct) while the kit's mirror types it as a pointer — ABI-correct only where large
  structs pass indirectly (Windows x64, AArch64), WRONG on x64 SysV (linux/macOS-x64) where it is
  copied onto the stack; the shell re-types the function pointer so the compiler emits the right
  convention per platform (found by reading duckdb.h, not by debugging Linux). (3) the shell
  deliberately AVOIDS `duckdb_value_varchar`/`duckdb_row_count` (3 lines vs 40) because duckdb.h
  marks them "scheduled for removal" and the installer is the version-PORTABLE half — their removal
  would null the struct slot = a crash; also any deprecated accessor marks the result
  `CAPI_RESULT_TYPE_DEPRECATED`, after which `duckdb_fetch_chunk` returns null, so mixing is
  forbidden. **sqllogictest is the WRONG harness** for the distribution (our `unittest` embeds
  fabricator statically → the chain-loaded core would collide with registered functions) — hence the
  python harness against a stock wheel. Two build traps encoded in the scripts: an AOT project must
  CLEAR the repo-wide `TargetFrameworks` (a single-value plural still counts as cross-targeting), and
  `dotnet run` forwards an unrecognized `--nologo` to the program. **Loading BOTH spellings in one
  process is unsupported** (hostfxr initializes once per process; the second module's CLR fails and
  its global functions never register).
  **PHASE 4 (§16) — LINUX DONE + the by-value ABI fix CONFIRMED on x64 SysV** (the platform where the
  kit's pointer-shaped `fetch_chunk` signature would have been wrong: the gate + extdir resolution are
  built on those reads, so a bad ABI would have failed the load before extraction — it didn't). AOT on
  linux needed NOTHING beyond the distro clang 18 (no extra packages, and NOT
  `IlcUseEnvironmentalTools` — that's a Windows/vswhere workaround); `dladdr` own-path discovery works
  via the libc-then-libdl fallback. **THREE more findings:** (1) **negative test artifacts must be
  PER-PLATFORM** — DuckDB checks the footer's platform BEFORE any extension code runs, so a Windows
  negative on linux fails with "built for the platform windows_amd64" and proves nothing (it produced
  two falsely-green PASSes on the first linux run; now generated as siblings of the real artifact by
  `-WithNegatives`); (2) the publish output path is NOT stable across hosts — Windows lands under
  `bin/x64/Release/...`, WSL under `bin/Release/...` (the platform segment appears only when the build
  sets `Platform`), so the script PROBES both; (3) the harness's wheel must match the local interpreter
  — the cp310 wheel kept in `build/linux-payload/` for the Fabric flow will NOT install on Ubuntu
  24.04's python 3.12, and the harness must run from OUTSIDE the repo (the repo root's `duckdb/` source
  dir shadows the module). **The linux core was rebuilt (35 MB, now exports BOTH entry symbols, ABI
  v68) at `build/linux-dist/core/`** — NOTE `build/linux-payload/` (the separate Fabric-notebook
  payload: loadable + loose-root FDD zip + wheel) is STILL the Jul-23 v67 build and remains stale.
  Phasing: spike ✅ → `Fabricator.Installer.Core` ✅ → AOT shell + `pack-distribution.ps1` +
  the core dual entry symbol ✅ → linux artifact ✅ → user-facing install docs + CI matrix.
- **NativeAOT BRIDGE SKU (design only, 2026-07-25 — nothing built):
  [docs/aot-bridge.md](docs/aot-bridge.md).** An optional AOT-compiled variant of the
  managed layer (Bridge + providers → ONE native lib, `NativeLib=Shared`) beside the CoreCLR
  SKU — zero .NET prerequisite, est. 40–80 MB total. **Key audit finding: the ABI is already
  AOT-shaped** (vtable of `[UnmanagedCallersOnly]` statics both directions) — only the
  bootstrap changes: a `FabricatorBridgeInit` native export + clr_host mode 3 (managed dir
  contains `Fabricator.Bridge.Native.<ext>` ⇒ plain dlopen/dlsym, no hostfxr). The complete
  dynamic-code inventory is FIVE sites: BackendRegistry reflection discovery → a
  **`Fabricator.Generators` Roslyn source generator** (`[FabricatorBackend]` attr → emitted
  `CompiledBackends` factory in a HEAD project `Fabricator.Bridge.Native.csproj`, trim-rooted
  by construction; reflection branch behind an AppContext feature switch ILC trims away);
  the plugin ALC → **compile-time plugins** (reference + republish the head — the head IS the
  plugin config; native-plugin C-ABI + a DAX CoreCLR sidecar noted as deferred);
  `FormatError`'s `GetProperty("Number")` duck-type → `IBackend.GetErrorNumber(Exception)`
  DIM; ~10 `JsonSerializer<T>` files (+ EW `ActionSerializer`) → one source-gen
  `JsonSerializerContext`; Regex is AOT-fine as-is. **DAX/ADOMD stays CoreCLR-only** (the
  original non-AOT reason — closed-source, not AOT-able); AOT SKU = SqlServer + Delta/EW
  (**SqlClient AOT feasibility USER-VALIDATED 2026-07-25 on the 7.1 preview — the AOT SKU
  targets 7.1+**, remaining work is the version bump + our-paths coverage via the suite;
  Fluid must run interpreted). Gate = the existing SQL-level verify sweep (minus verify_dax)
  against the native bridge. Endgame option noted: `NativeLib=Static` linked INTO the C++
  loadable = literally one file, no trampoline (experimental, later). Composes with the
  distribution installer (payload → core + one native lib, no .NET probing).
- **`CREATE TABLE … WITH (…)` options + SQL Server EXTERNAL TABLES —
  [docs/create-table-with-options.md](docs/create-table-with-options.md). ALL FOUR SLICES DONE
  (2026-07-19): A (ABI v67) + C + D + B.** DuckDB v1.5.4 parses the clause (`CreateTableInfo::options`).
  **A (DONE)** = `options_json` threaded through `create_table`+`begin_bulk` (v67; C++
  `TableOptionsArg` — constants only, flat string-valued JSON) → the Delta provider consumes THREE
  key kinds (`DeltaWithOptions.Parse`, guarded — unknown keys ERROR): per-statement **write tuning**
  (`parquet_compression`/`parquet_row_group_size`/`parquet_bloom_filter_columns`, DuckLake-parity
  names + bare aliases; precedence WITH > `delta_write_options` > ATTACH; CTAS-only — rejected on an
  empty CREATE), per-table **CREATE-flag overrides** (`deletion_vectors`/`column_mapping`/
  `row_tracking`/`change_data_feed`/`in_commit_timestamps` — the protocol-1.0 PolyBase recipe
  `WITH (deletion_vectors=false, column_mapping='none')` per table, no dedicated ATTACH), and
  `delta.*`/`fabricator.*` **TBLPROPERTIES stamped in the CREATE commit** (rides
  `DeltaWriteSpec.CreateProperties` → `CreateConfig` merge, WITH wins over derived keys;
  `delta.enable*`/`delta.columnMapping.*`/`fabricator.sortedBy` spellings rejected with pointers to
  the explicit keys/clauses). Buffered (explicit-txn) CREATE/CTAS parks the options on the txn
  buffer (`PendingAppends.CreateOptions`) and the flush applies them. **Same pass closed a
  PRE-EXISTING gap: the native_write COPY paths now carry the resolved tuning**
  (`NativeParquetDataFileWriter.CopyTuning` → `COMPRESSION`/`ROW_GROUP_SIZE` COPY options on
  RunCopy/RunCopyPartitioned + the per-file writer ctor) — previously `delta_write_options`
  compression only reached the EW codec writer (bloom COLUMNS stay codec-only; DuckDB blooms
  automatically). NOTE DuckDB's parquet writer has a row-group flush floor — tiny
  `parquet_row_group_size` values (< ~2048) coalesce. Two findings: the parser LOWERCASES all WITH
  keys (canonical re-casing of well-known `delta.*` keys C#-side; custom mixed-case keys → use
  `set_tblproperties`) and bools arrive as postgres `'t'/'f'` (accepted). SQL Server/DAX/deltars
  reject any options (never silent). `test/verify_with_options.test` (68 — tuning pins via
  parquet_metadata, precedence, both flag-override directions incl. empty CREATE + a DV-off catalog
  opting IN, property canonicalization + commit-0 stamp pin, buffered CTAS + ROLLBACK, 10 guards,
  Iceberg-shape no-ops) + `verify_with_options_mssql.test` (4). Regression: partition 54 / sorted_by
  30 / tblproperties 42 / native_write 147 / native_write_streaming 29 / copy_format 109 /
  transactions 941 / identity(delta) 38 / column_mapping 251 / dv_default 58 / columnstore 20 /
  identity(sql) 64 / cluster_by 18 / custom_functions 89 green.
  **C — DONE (2026-07-19, C#-only, no ABI): INSERT INTO an S3 EXTERNAL TABLE routes to STORAGE** —
  a capability SQL Server itself lacks (it can't INSERT into S3 external tables at all, and can't
  write Delta ever). `SqlServerCatalog.DetectExternalTable` (lazy `sys.external_tables` join probe,
  positive+negative cached per table, profile-tolerant, invalidated on DROP/CREATE) → Bridge-public
  **`ExternalTableRouting`**: DELTA = transient delta catalog over the parent folder
  (`BackendRegistry.Resolve("delta").OpenCatalog(root, native_write)` → BulkInsert parks →
  `CommitTransaction()` flushes ONE Delta commit — the C# mirror of the COPY(FORMAT delta) finalize);
  PARQUET = one `COPY … TO '<folder>/<uuid>.parquet'` host query. **Endpoint asymmetry**: SQL's
  LOCATION host (`minio:9000`) is discarded — the DuckDB s3 secret (bucket-scope match) supplies the
  client endpoint. Guards: explicit-txn rejection (`BeginTransaction(isExplicit)` now RECORDS explicit
  txn ids — `set_active_txn` precedes it, verified), `INSERT…RETURNING` rejection, non-s3/non-DELTA/
  PARQUET formats reject cleanly; appends never change table features so protocol-1.0 tables stay
  SQL-readable; `DROP TABLE` on a detected external table emits **`DROP EXTERNAL TABLE`**
  (metadata-only, data stays). `verify_mssql_s3_polybase` **167** (§6 Delta INSERT round-trip via
  OPENROWSET + catalog scan, guards; §6b parquet INSERT; §6c DROP routing); regression: SQL fn suites
  (26/33/24/31/63/13) + delta s3 161 / connection_mode 20 / time_travel 14 / orderby 7 green.
  **ENV REPAIR en route: the live SQL container was the PRE-compose `mssql-arrownet` while MinIO was
  on the new compose network (SQL couldn't resolve `minio:9000`, error 13807) — the compose
  `mssql-fabricator` service is now the live 1433 server (old container STOPPED, not removed;
  provision.ps1 re-run; fresh volume — suites self-provision).**
  **D — DONE (2026-07-19, C#-only): identity-keyed UPDATE/DELETE on detected external Delta tables.**
  A Delta IDENTITY column bridges the rowid domains — PolyBase-visible data column + standard stats,
  SNAPSHOT-INDEPENDENT (the only sound bridge; `_metadata.row_id` can NOT serve this: off-schema by
  spec + appends carry no physical id). The cached external probe also resolves the identity column
  (`ExternalTableRouting.FindDeltaIdentityColumn` — `delta.identity.*` field metadata); the entry's
  rowid OVERRIDES to it (`RowIdMetadata` branch — the standard identity-as-rowid machinery, zero
  C++); `ExecuteDelete`/`ExecuteUpdate` route to storage: identity→transient-rowid resolution via
  chunked PRUNED `ScanSpec` IN-scans (`{"columns":[id,"_metadata.row_id"],"filter":in}` + value
  batch, 500/chunk; superset-safe → exact client re-filter) → the delta provider's own rowid
  DELETE/UPDATE (CoW keeps protocol 1.0). UPDATE alignment trick: unresolved ids become NULL rowids,
  which the delta update parser SKIPS = concurrently-deleted-matches-nothing for free. Guards:
  SET-of-identity rejected, explicit-txn rejected (`GuardExternalDml`). `verify_mssql_s3_polybase`
  **209** (§6d: scan-via-SQL-Server + apply-via-Delta UPDATE incl. expression, DELETE, ids
  preserved, guards, zero-match DELETE); regression identity 64 / columnstore 20 / delta s3 161 /
  arrow_lossless 10 green.
  **B — DONE (2026-07-19, C#-only): `CREATE TABLE … WITH (location=…, table_type='DELTA'|'PARQUET'
  [, data_source=…, file_format=…]) [AS …]` on the SQL provider — the CETAS-analog, one DDL statement.**
  Data-first/DDL-second: `ExternalTableRouting.CreateDeltaAs` (CTAS, `copy_disposition:'error'`) /
  `CreateDeltaEmpty` (empty CREATE) write the client-side Delta table (protocol-1.0 plain, identity
  marker rides through → slice-D-capable from birth), then `ProvisionExternalTable` auto-creates the
  EXTERNAL FILE FORMAT (unless `file_format=` given) + EXTERNAL TABLE with a column list from the write
  schema (`BuildExternalColumnList` — one source of truth). `data_source=` REQUIRED (no-secret-material
  posture; `secret=` auto-provisioning rejected with a pointer). **Two type findings:** external text
  columns need explicit lengths with a TYPE-DEPENDENT cap — `VARCHAR(8000)` but `NVARCHAR(4000)` (8000
  is error 2717); the identity marker is ALLOWED with `location`+DELTA (declared plain BIGINT SQL-side).
  Guards: CREATE OR REPLACE / explicit-txn / PK-UNIQUE-DEFAULT / PARQUET-empty-create / ICEBERG rejected.
  **Pre-existing S3 bug found + fixed in the test:** an EMPTY `CREATE OR REPLACE TABLE t (cols)` over an
  EXISTING S3 delta table fails "version 0 already exists" (the DropTable+create-v0 path's post-delete
  `_delta_log` listing is stale within the one statement; a CTAS CREATE OR REPLACE is unaffected — it
  Overwrites). Workaround: separate `DROP TABLE IF EXISTS` + `CREATE` (the view settles across the
  statement boundary) — the polybase test's one empty-create-or-replace (iddml) uses the split.
  `verify_mssql_s3_polybase` **252** (§6e auto-provision DDL round-trip + INSERT compose + guards; §6f
  the FULL CIRCLE — empty CREATE + identity → INSERT → UPDATE → DELETE all through the SQL catalog),
  re-runnable back-to-back; `verify_with_options_mssql` 9; regression identity 64 / columnstore 20 /
  delta s3 161 / scalar 26 / procs 24 / native_write 147 green.
- **SQL-GENERATING TABLE FUNCTIONS — DONE (2026-07-24, ABI v68, global + catalog-bound; design + as-built:
  [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §2).** The provider-authored
  twin of `query_table()`: a provider declares `ISqlTableFunction` (global, connection-free) or
  `ICatalogSqlTableFunction` (attach-time, `db.schema.fn(args)`) whose `GenerateSql(args)` returns a SQL
  statement; the C++ `bind_replace` hook (`FabricatorSqlGenBindReplace`) parses it and hands the binder a
  `SubqueryRef`, so **the function call DISAPPEARS at bind — the plan that executes is the generated SQL's,
  and NO data crosses the ABI at execution.** Two capabilities fall out for free: **the output schema is
  arg-dependent** (it's whatever the SQL binds to — nothing is declared C#-side), and **everything the SQL
  references keeps its own pushdown/parallelism**, including our own catalog scans. Registration =
  `TableFunction(name, args, nullptr /*scan*/, nullptr /*bind*/)` + `bind_replace` only; globals at load
  (decl kind `table_sql`), catalog-bound via discovery → `AddSqlTableFunction` → `GetOrCreateSqlTableFunction`
  (the entry carries `catalog.GetName()` = the ATTACH alias). **The catalog-bound generator gets a
  `SqlGenContext` (alias + the live `IBackendCatalog`), so it can LOOK THINGS UP at bind time** — which is
  what makes the flagship demo work: `db.dbo.cf_union_by_pattern('sg_sales_%')` queries `sys.tables` on the
  catalog's own connection and emits a `UNION ALL` over quoted three-part names. **Measured payoff:** a
  `WHERE yr = 2024` over that union reaches SQL Server as TWO statements, `(@p0 int)SELECT [id],[nm],[yr]
  FROM [dbo].[sg_sales_2024] WHERE [yr] = @p0` (+ the 2023 twin) — predicate AND projection pushed per member
  (pinned via `dm_exec_query_stats`); a marshaled `ITableFunction` would have streamed every row through C#.
  Globals: `fabricator_sql_seq(n[, cols := k])` (arg-dependent projection) + `fabricator_delta_union(paths[,
  by_name := …])` (UNION over Delta tables BY PATH — LIST/BOOLEAN constants cross fine).
  **Dividing rule vs macros: fixed SQL text + varying VALUES ⇒ macro; SQL TEXT depends on the args ⇒
  sqlgen.** Authoring contract (documented + enforced by shape): deterministic + side-effect-free (binds
  repeat: EXPLAIN/DESCRIBE/view re-bind), exactly ONE SELECT statement (a PIVOT without an explicit IN list
  is rejected — inherited from upstream's `ParseSubquery`), and quote what you splice
  (`DuckSql.QuoteIdent`/`QuoteName`/`Literal`). Errors all land at bind: NULL for a non-nullable parameter
  (per-parameter message — better than `query_table()`'s blanket rejection), the generator's own validation
  verbatim, unknown named param → DuckDB's candidate list, non-constant arg → "does not support lateral join
  column parameters". **Two gotchas worth keeping:** `EXPLAIN` is NOT usable as a subquery, so the
  "call vanished" pin uses `json_serialize_plan(sql, optimize := true)` (subquery-usable, inspects the
  optimized logical plan); and `Scan(TABLE_FUNCTION_ENTRY)` must enumerate `sql_table_functions_` or the
  function resolves by name but is invisible to `duckdb_functions()`. `test/verify_sqlgen.test` (59) +
  `test/verify_sqlgen_catalog.test` (30, needs `MSSQL_TESTDB_DSN`); regression: all 17 function suites +
  transactions 941 / native_write 147 / row_level_concurrency 70 green.
- **PROVIDER-DECLARED DuckDB MACROS — DONE (2026-07-24, C#+C++ lockstep, NO ABI bump; design +
  as-built: [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1).** A provider
  ships SQL TEMPLATES beside its marshaled functions: `IBackend.GlobalMacros` →
  `MacroDefinition(Name, CreateSql)` where `CreateSql` is a **complete `CREATE MACRO` statement**; the C++
  load-time registrar hands it to **DuckDB's OWN `Parser`** and registers the resulting `CreateMacroInfo`
  via `ExtensionLoader::RegisterFunction(CreateMacroInfo&)` into the **SYSTEM catalog** (like a built-in ⇒
  bare `fn(...)` / `FROM fn(...)` in EVERY database, no ATTACH). Because DuckDB parses it, the FULL grammar
  works with zero bespoke encoding: scalar AND table macros (the parsed statement carries the kind),
  named-parameter defaults, overload sets, no 8-param cap. **Nothing crosses the bridge at runtime** — the
  binder expands the macro, substituting parameters as EXPRESSIONS (structure/identifiers frozen at
  declaration ⇒ injection-free by construction). Declaration rides `list_global_functions` as kind `macro`
  + a new leading `body` string column (`ReadStringTable(stream, 4)`) — the `string_order` no-bump
  precedent. **Dividing rule vs the (planned) SQL-generating table functions: fixed SQL text + varying
  VALUES ⇒ macro; SQL TEXT depends on the args ⇒ sqlgen (§2, not built).** Flags = `internal = true`
  ONLY (`BuiltinFunctions` parity — not the `DefaultFunctionGenerator`'s `temporary` pair). Provider
  qualification (`somecat.sch.foo`) is REJECTED, not ignored (macros land in system `main`; namespace via
  the NAME prefix). Bad DDL ⇒ WARN + skip, never blocks the extension (**the load-time WARN only reaches
  `duckdb_logs` on the LOAD path** — a static binary loads before logging can be enabled; the sample plugin
  ships a deliberately malformed `plug_bad_macro` beside a good `plug_double` to pin skip-doesn't-block).
  **Two findings:** a DEFAULTED macro param also binds POSITIONALLY (`fabricator_bucket_of('alice', 16)`,
  not just `n := 16`) ⇒ overload sets are only for genuinely different arities; an overload set is ONE
  catalog entry but N rows in `duckdb_functions()`. Demos (SqlServer = the always-present default provider,
  the `fabricator_render` slot): `fabricator_rowid_parts(rid)` → `{file_ordinal, row_position}` (the
  TRANSIENT rowid splitter, docs/rowid-concepts.md), `fabricator_rowid_of(ord, pos)` / `(parts)` (overload
  set, the round-trip inverse), `fabricator_bucket_of(v, n := 8)` (named default over `bucket()`),
  `fabricator_delta_head(path, n := 100)` (TABLE macro over `fabricator_delta_scan`). Plugins ship macros
  too (`Fabricator.SamplePlugin`, zero bridge/ABI change). `test/verify_macros.test` (41) +
  `verify_plugin.test` (10); regression global_functions 63 / custom_functions 89 / hilbert 27 / bucket 34.
- **`hilbert_index` global scalar — DONE (2026-07-18, C#-only, no ABI): the liquid-clustering-style
  ordered-write primitive.** `hilbert_index(coords BIGINT[], bits) → BIGINT` (Bridge
  `HilbertIndexFunction`, declared in `CustomFunctions.GlobalScalar` — the fabricator_render slot):
  n-dim Hilbert-curve position via Skilling's transpose algorithm (the curve OSS delta-spark's
  `HilbertClustering` uses; LIST arg = per-row dimension count, sidestepping the fixed-arity global-scalar
  decl), coords CLAMPED into [0,2^bits) (layout-advisory, never correctness), n*bits<=63, n=1=identity,
  NULL→NULL. **Usage = `ORDER BY hilbert_index([...], b)` in a CTAS/COPY/dbt model → DuckDB's EXTERNAL
  (spilling) sort does the global reorder — the write pipeline stays streaming**; consecutive rows are
  neighbors in EVERY clustering key → tight per-file/row-group min-max on all keys → stats skipping for any
  predicate subset (pre-bucket with `width_bucket`). `test/verify_hilbert_index.test` (27 — full-grid
  unit-step property pins 2D+3D = the defining Hilbert property, U-order literals, clamp/NULL/errors, 100k
  ordered CTAS); global_functions 63 unregressed.
  **`bucket` global scalar + DECLARED SCALAR VOLATILITY — DONE (2026-07-19, C#+C++ lockstep, no ABI bump):
  the Iceberg/DuckLake bucket transform** (`bucket(num_buckets, value) → INTEGER`, DuckLake arg order;
  `BucketFunction`, Bridge, beside hilbert_index): Murmur3 x86-32 seed 0 over Iceberg's canonical byte
  encodings (spec Appendix B — ints/dates→LE 8-byte long, ts/time→µs, decimal→minimal BE two's-complement
  of the UNSCALED value, string→UTF-8, blob raw) then `(hash & Int32.Max) % n` — bucket values agree with
  DuckLake/Iceberg/Spark (spec known-answer vectors pinned + cross-checked vs an independent murmur3);
  NULL→NULL, float/double/bool rejected (Iceberg parity), value = the SQLNULL→ANY sentinel (runtime-type
  dispatch). **Delta has no transform partitioning → bucket partitioning = materialize the column**:
  `CREATE TABLE … PARTITIONED BY (user_bucket) AS SELECT *, bucket(8, user_name) AS user_bucket …`, prune
  with `WHERE user_bucket = bucket(8, 'alice')`. **That pruning required the general capability the user
  asked for: scalar functions now DECLARE volatility** — `IScalarFunction.IsVolatile` (default TRUE;
  override false = PURE), riding the return-schema FIELD metadata `fabricator.volatile="0"` (the variant
  metadata channel — absent=volatile, old bridges/plugins unchanged) → `FetchFunctionReturnType(out bool)`
  → `BuildFabricatorScalarFunction` registers `FunctionStability::CONSISTENT` ⇒ constant args FOLD at plan
  time (the folded literal is what reaches the scan as a partition filter; VOLATILE = never folded stays
  the default for discovered/remote UDFs). Applies to global AND catalog custom scalars; hilbert_index is
  now CONSISTENT too. `test/verify_bucket.test` (34 — vectors, distribution, guards, partitioned CTAS +
  duckdb_logs `pruned=2` pin proving fold→prune); regressions global_functions 63 / hilbert 27 / scalar 26
  / custom 89 / sorted_by 30 / clustered_optimize 138 green.
  **WRITES TO LIQUID-CLUSTERED TABLES — DONE (2026-07-18, EW-only, local commit): the `clustering` writer
  feature is allowlisted in EW `ProtocolVersions.SupportedWriterFeatures`** — previously EVERY write to a
  Databricks/Fabric-Spark `CREATE TABLE … CLUSTER BY` table failed with "unsupported writer features:
  [clustering]". The feature is advisory LAYOUT (the spec permits plain unclustered appends/DML; a later
  clustering OPTIMIZE reclusters them), and the preservation obligations were ALREADY met (the
  `delta.clustering` domainMetadata survives commits AND checkpoints — SnapshotBuilder/CheckpointWriter
  carry all domains; `add.clusteringProvider` round-trips). EW `ClusteredTableTests` (3 — synthetic
  clustered log append, domain-survives-checkpoint, provider round-trip; Table.Tests 185). **VALIDATED
  LIVE (Fabric Spark 4.1, sparkprobe `createclustered`/`verifyclustered`):** Spark `CLUSTER BY (grp,id)`
  create + OPTIMIZE (8 clustered files, reader v3/writer v7 with clustering+DV+domainMetadata) → OUR
  append (500 rows) + DV DELETE (id%100) on `lake.dbo.fabricator_clustered` → Spark reads 1485/1/1499
  exact, deleted ids invisible, **clusteringColumns intact in DESCRIBE DETAIL**, and a further Spark
  OPTIMIZE reclusters incl. our unclustered files. Remaining liquid follow-ups (not started): writing
  CLUSTERED files ourselves (hilbert_index layout in OPTIMIZE + `clusteringProvider` tagging),
  ZCube incremental recluster.
  **CLUSTERING-AWARE CREATE — DONE (2026-07-18, EW `13f7fce` local + Bridge pass-through): `SORTED BY`
  now also DECLARES the table liquid-clustered.** EW `CreateAsync`/`OpenOrCreateAsync` gained
  `clusteringColumns` (writer-v7 `clustering` + its `domainMetadata` dependency + the `delta.clustering`
  domain in commit-0, byte-shaped like Spark's captured live form
  `{"clusteringColumns":[["a"],["b"]],"domainName":"delta.clustering"}`); `DeltaWriter` passes
  `clusteringColumns: sortedBy` at all three `OpenOrCreateAsync` sites — so a SORTED BY create is both
  physically ordered AND advertises the clustering spec Spark's OPTIMIZE consumes. **SPEC FINDING (cost a
  live None.get crash): the domain stores PHYSICAL column names.** OSS Spark's `ClusteringColumnInfo.apply`
  (`ClusteringColumn.scala:97`, via `extractLogicalNames`) resolves the domain's paths against the schema's
  PHYSICAL names — under our default `column_mapping 'name'` a logical-named domain made `DESCRIBE DETAIL`
  AND `OPTIMIZE` crash with `None.get` (reads + INSERTs worked — only the clustering-info resolution
  failed; the Spark-created reference table had no mapping so logical==physical masked it). Fix in EW:
  callers supply LOGICAL names, CreateAsync resolves each through the mapping-assigned schema
  (`ColumnMapping.GetPhysicalName`; unknown column → clear DeltaFormatException). EW `ClusteredTableTests`
  now 6 (mapped-create physical-name pin + unknown-column throw; Table.Tests 188). **VALIDATED LIVE
  (sparkprobe `verifyoursorted`):** our `CREATE TABLE lake.dbo.fabricator_sorted SORTED BY (grp,id) AS …`
  (5000 rows, name-mapped, physical-name domain) → Spark `DESCRIBE DETAIL` shows
  `clusteringColumns ["grp","id"]` + tableFeatures incl. clustering, **Spark OPTIMIZE runs its CLUSTERING
  strategy** (clusteringStats + per-column clusteringQuality grp/id "ok"; no rewrite — the single file is
  already sorted), counts exact, Spark INSERT works.
  **CLUSTERED OPTIMIZE — DONE (2026-07-18, EW seam + Bridge, no ABI): OPTIMIZE on a clustering-declared
  table RECLUSTERS instead of bin-packing.** Detection (`DeltaReader.ResolveClusteringColumns`): the
  `delta.clustering` domain (authoritative, PHYSICAL names → resolved to logical via the mapped schema;
  nested paths/unknown columns → bin-pack) else `fabricator.sortedBy` (pre-domain tables). Mechanism =
  **ONE host query, data never crosses the C ABI**: `COPY (SELECT <logical→physical renames> FROM (<UNION
  ALL of per-file FileSql — DV rows excluded, mapping/schema-evolution/row-tracking handled>) ORDER BY …)
  TO <uuid>.parquet (RETURN_STATS, FIELD_IDS…)` — order = `hilbert_index` over per-key
  `ntile(2^bits) OVER (ORDER BY col)` range-buckets for 2+ keys (bits=min(15,63/k); rank-based bucketing
  is type-agnostic — strings/dates, no 63-bit truncation — and IS Spark's range_partition_id shape;
  1 key = plain ORDER BY), then ONE commit via EW `CommitDataFilesAsync(Overwrite, dataChange:false,
  clusteringProvider:"liquid", expectedVersion: <the listed version>, operation:"OPTIMIZE")` — new EW
  params: `dataChange` (removes+adds; `HonorWriterFeatures` treats dataChange=false as appendOnly-legal)
  + `clusteringProvider` stamped on adds. Row-tracking stable ids PRESERVED: when the table declares the
  materialized columns, `__delta_row_id`/`__delta_row_commit_version` are projected as data columns (the
  per-file COALESCE(materialized, baseRowId+position)) and ride the sort into the clustered file (out of
  stats; the add gets fresh baseRowId per compaction semantics). The listing runs against the OPEN
  table's own snapshot (`BuildNativeScanListAsync`, the extracted core of ListNativeScanFiles) so
  expectedVersion can't race; a concurrent commit → clean "retry" error. NO-OP when active = 1 DV-less
  file (Spark parity — no commit). Gates → bin-pack fallback: no native_write, identity/IcebergCompat,
  variant, nested columns under mapping. `test/verify_delta_clustered_optimize.test`
  (46 — 2-key hilbert order pinned via deterministic ntile recompute [unique keys] + lag monotonicity,
  DV rows not resurrected, stable id preserved, clusteringProvider+dataChange:false commit pins, no-op
  re-OPTIMIZE, VACUUM→1 file, 1-key lexicographic, sortedBy-only detection, partitioned+plain fallbacks);
  regression optimize 40/sorted_by 30/native_write 147/native_read 88/partition 54/row_tracking_virtual
  299/transactions 941/tblproperties 42/late_mat 57/dynamic_filter 21 + EW 189&168. **VALIDATED LIVE
  (OneLake `fabricator_sorted` + sparkprobe):** 2000-row unsorted append (3 files) → our OPTIMIZE → 1
  file, counts exact, the provider commit on OneLake; **Spark DESCRIBE DETAIL fine with our tagged file,
  Spark OPTIMIZE judges the hilbert file clusteringQuality "ok" (avgCoverage 100) and rewrites NOTHING**,
  Spark INSERT works.
  **MULTI-FILE clustered output — DONE (same day, C#-only):** the rewrite splits into TARGET-SIZED files,
  each a CONTIGUOUS cluster range (tight per-file min/max on all keys = the actual file-skipping payoff).
  Target = the `delta.targetFileSize` table property (Databricks; plain bytes or b/kb/mb/gb suffix) else
  128 MiB (== EW `CompactionOptions.TargetFileSize`, so clustered + bin-pack aim alike). **DuckDB's own
  COPY `FILE_SIZE_BYTES` rotation is UNUSABLE for this — a HARD incompatibility, not a tunable: the
  planner FORCE-disables order preservation for any rotated/per-thread/partitioned COPY regardless of
  `preserve_insertion_order`, and the explicit `PRESERVE_ORDER` COPY option THROWS with these parameters
  (`plan_copy_to_file.cpp:27-34`); probed: the parallel sink interleaves order across the rotating files**
  (in-file inversions + interleaved ranges + a 0-row trailing file) — so the split is OURS: single-file fast path when the estimated output fits
  (~1.25× target; the whole rewrite stays ONE zero-crossing COPY), else the sorted stream comes back over
  `HostFs.Query` and SEQUENTIAL per-file `RunCopy`s cut at BATCH boundaries (`BudgetedStream` — no
  slicing, no lifetime hazards; rows-per-file estimated from the source adds' own bytes/rows stats).
  **CRUX BUG FIXED en route (pre-existing, ALL Host.Query consumers): the host stream's `get_next` is NOT
  idempotent at EOF** — a second call after end re-`Fetch()`es the CLOSED StreamQueryResult ("Attempting
  to execute an unsuccessful or closed pending query result"; the DuckDB flavor of the ADOMD
  read-past-EOF gotcha). Latched centrally in `InterruptibleQueryStream` (`_done` — Arrow C stream end is
  sticky), protecting every consumer; my chunked loop was just the first to pull past EOF.
  `verify_delta_clustered_optimize.test` now 55 (§5 multi-file: >1 file, per-file order, NO strictly
  interleaving ranges, every add provider-tagged, counts exact). (The ZCube incremental recluster
  noted here as remaining is DONE — see the later bullet.)
  **PARTITIONED COMPACTION — SILENT CORRUPTION FOUND + FIXED AT THE EW ROOT (2026-07-18, EW `13f7fce`+
  local commit; the clustered-OPTIMIZE tests exposed it):** EW's `CompactionExecutor` compacted ALL
  candidates into ONE file at the TABLE ROOT stamped with the FIRST candidate's `partitionValues` — after
  a single OPTIMIZE on a partitioned table EVERY row read one partition's value (probed live: 400/400/400
  across three regions → 1200 rows all "US"; the reader derives partition columns from the add's
  partitionValues, so the corruption was total and silent on EVERY path — pure-EW included). The
  NULL-backfill of partition columns into the compacted file also misaligned the `IDataFileReader` seam
  (the index-out-of-bounds / SIGSEGV under native_read+native_write — the LOUD variant of the same gap).
  **Fix (EW):** candidates group BY PARTITION (`CanonicalPartitionKey`, now internal — tolerant of mixed
  logical/physical partitionValues vintages under mapping) and each ≥2-file group compacts independently
  (`CompactGroupAsync`): adds carry the group's partitionValues + land in the group's Hive dir (inherited
  encoded path prefix); the widening/backfill target schema EXCLUDES partition columns (data files never
  carry them). Unpartitioned = single group (behavior unchanged); single-file partitions untouched;
  all-DV-deleted groups left alone (conservative no-op parity). The Bridge's reopen-without-reader-seam
  guard is REMOVED (the seam now aligns). EW `CompactionTests` +2 (per-partition values/files/Hive-dirs
  pin + single-file-partition), Table.Tests 191; extension test §4 pins exact per-partition values + one
  file per partition through the once-crashing native_read+native_write path
  (`verify_delta_clustered_optimize` 68); sweep: optimize 40 / compaction_rowtracking 24 /
  materialize_rowtracking 17 / row_tracking 33 / row_tracking_virtual 299 / sorted_by 30 / native_write
  147 / native_read 88 / partition 54 / partition_overwrite 90 / transactions 941 / dv_default 58 /
  column_mapping 251 / variant 133 green.
  **PARTITIONED × CLUSTERED — DONE (2026-07-18, EW guard + Bridge): `PARTITIONED BY` + `SORTED BY`
  COMPOSE on one table, the Databricks ZORDER-on-partitioned analog.** (1) **A partitioned create
  declares NO `delta.clustering`** — liquid clustering and partitioning are mutually exclusive
  (Spark's CLUSTER BY REPLACES PARTITIONED BY; the combo put Spark's clustering-info paths in
  None.get territory — probed, we WERE writing it): EW `CreateAsync` now THROWS on
  clusteringColumns+partitionColumns (`ClusteredTableTests` 8) and `DeltaWriter` passes
  `clusteringColumns: null` when `spec.PartitionColumns` present — the partitioned SORTED BY table
  keeps only `fabricator.sortedBy`. (2) **Clustered OPTIMIZE now serves PARTITIONED tables as
  PER-PARTITION recluster**: the listing groups by canonical partition key (physical-normalized, EW
  parity), each fragmented group (≥2 files or any DV) rewrites into ordered file(s) in ITS Hive dir
  with ITS partitionValues, and — the payoff of hand-built removes — **PARTIAL recluster**: clean
  partitions' files stay ACTIVE, untouched (commit = Append + extraActions RemoveFile[dataChange:false]
  for rewritten groups only; the unpartitioned path moved to the same shape — was mode=Overwrite).
  Partitioned rewrites carry **NO clusteringProvider tag** (no liquid declaration exists for them);
  unpartitioned keeps "liquid". Per-group ntile = per-partition range buckets (correct hilbert
  locality within each partition); partition columns ride the INNER select as literals (a cluster key
  may be one) but are EXCLUDED from the written files/stats/FIELD_IDS. NOTE: SORTED BY on partitioned
  WRITES is approximate-only (the partitioned COPY is in the planner's force-unordered list — same
  plan_copy_to_file.cpp rule), so OPTIMIZE is also what RESTORES strict order per partition.
  `verify_delta_clustered_optimize` 80 (§4 rework: no-domain pin, per-partition order + exact values,
  partial-recluster commit shape "2 removes + 1 add", no provider tag).
  **ZCUBE INCREMENTAL RECLUSTER — DONE (2026-07-18, EW Tags seam + Bridge): OPTIMIZE cost tracks NEW
  data, not table size.** Clustered outputs now carry **`tags["ZCUBE_ID"]`** (one fresh uuid per group
  per run — Spark's incremental-clustering cube identity, same tag name) + **`tags["ZCUBE_ZORDER_BY"]`**
  (the JSON key list the cube was clustered by). On the next OPTIMIZE, per group: files of a **STABLE
  cube** (total size ≥ `fabricator.targetCubeSize`, default 100 GiB — Databricks' target cube size;
  parsed like targetFileSize) clustered by the CURRENT keys are NEVER rewritten; candidates = the
  UNCLUSTERED files (plain appends, pre-ZCube rewrites, **stale-key cubes** — `ZCUBE_ZORDER_BY`
  mismatch re-enters them, so changing `fabricator.sortedBy` self-heals incrementally) + at most ONE
  partial cube (the most recent by commit version; merging one per run bounds write amplification, OSS
  parity). A lone DV-less candidate skips (joins the next round's merge). Removes = the candidates'
  adds only (correlated by the add's encoded path — `NativeScanFile` gained `AddPath`/`ZCubeId`/
  `ZCubeBy`). **`OPTIMIZE <table> FULL`** (the Databricks dialect) ignores cube identities — full
  recluster; use it after changing keys on a DOMAIN-declared table (**the domain is the authoritative
  key source — changing `fabricator.sortedBy` does NOT re-key a SORTED BY-created table**; pinned).
  EW seam: `WrittenDataFile.Tags` → `CommitDataFilesAsync` stamps `add.tags` (round-trip pinned in
  `ClusteredTableTests`, 8). Incremental trade-off: cubes overlap in key ranges (point lookup ≤ N-cubes
  files) — same as Databricks. `verify_delta_clustered_optimize` 113 (§6: stable cube untouched across
  a fragmenting append cycle [2 active files = cube A + cube B], no-op when all stable, FULL → 1 file,
  property-only key-change invalidation reorders by the NEW key). **VALIDATED LIVE (sparkprobe
  `verifyzcube`, Fabric Spark 4.1):** OneLake `fabricator_partclust` (PARTITIONED BY + SORTED BY: our
  per-partition recluster 6→3 files, 2000/region exact, NO domain in commit-0) — Spark reads exact,
  DESCRIBE DETAIL shows partitionColumns [region] + empty clusteringColumns, Spark OPTIMIZE runs fine
  over our tagged files; `fabricator_sorted` (2 of our cubes: stable A untouched by the incremental run
  + merged B) — counts exact, clusteringColumns intact, and **Spark's clustering OPTIMIZE RECOGNIZED
  OUR CUBES AS ITS OWN: clusteringStats inputZCubeFiles=2/inputOtherFiles=0 (it parsed our ZCUBE_ID
  tags), filesPassedToZCubeFilter=2, per-column quality "ok", ZERO rewrites** — full incremental-
  clustering interop, both directions; Spark INSERT still works. Also pinned live: a lone DV-less
  append file correctly SKIPS (joins the next round's merge).
  **`ALTER TABLE … SET/RESET SORTED BY` — DONE (2026-07-19, C++ additive alter kinds 12/13 + EW seam +
  Bridge): the DOMAIN RE-KEY DDL (the ALTER CLUSTER BY analog).** DuckDB v1.5.4 parses
  `ALTER TABLE t SET SORTED BY (a, b)` / `RESET SORTED BY` natively (`AlterTableType::SET_SORTED_BY`;
  its own tables throw — the clause exists FOR catalog extensions; there's also SET/RESET PARTITIONED
  BY). C++ crosses both as additive `FABRICATOR_ALTER_SET_SORTED_BY=12`/`_SET_PARTITIONED_BY=13` (a1 =
  JSON array, [] = RESET; plain ASC column refs only — DESC/expressions rejected, clustering has no
  direction). Delta: ONE metadata commit updates the `fabricator.sortedBy` ordered-write property AND —
  unpartitioned — the `delta.clustering` domain via new EW **`SetClusteringColumnsAsync`** (declare/
  re-key/remove + `extraActions` fusion + **`UpgradeProtocolForWriterFeatures`** — the WRITER-ONLY twin
  of UpgradeProtocolForFeatures: clustering/domainMetadata must NOT enter readerFeatures, a legacy
  reader-1 table upgrades to writer-7 while staying reader-1). **This closes the earlier limitation:
  re-keying a SORTED BY-created (domain) table now works by DDL** — the old cubes go stale
  (ZCUBE_ZORDER_BY mismatch) and the next OPTIMIZE reclusters by the NEW key incrementally. A
  PARTITIONED table takes the property only (ordered writes; mutual exclusion); `SET PARTITIONED BY` →
  clean guidance error (repartitioning = COPY MODE 'overwrite' + PARTITION_COLUMNS); SQL Server/DAX
  reject the kinds. `_sortedByCache` invalidated. EW `ClusteredTableTests` 10 (declare/re-key/remove/
  no-op, partitioned throw, writer-only-upgrade pin: reader stays 1, legacy writer features
  enumerated); `verify_delta_clustered_optimize` 138 (§7: DDL declare on plain table + protocol upgrade
  + INSERT re-applies order, domain re-key → incremental reorder by new key, RESET removes property+
  domain [removed:true pin], unknown-column/DESC/SET PARTITIONED BY guards, partitioned property-only).
  **`SORTED_COLUMNS` COPY option — DONE (2026-07-19, C++ option-parse only, no ABI): DECLARATIVE
  clustering for the no-ATTACH COPY surface** (dbt `delta_external` etc. — the SORTED BY analog next to
  `PARTITION_COLUMNS`; deliberately NOT `ORDER_BY`, planner-intercepted). `COPY … TO '<path>/<t>'
  (FORMAT delta, SORTED_COLUMNS 'a,b')` orders every run's stream (the existing v52 `begin_bulk
  sort_columns` param — the C++ COPY passed "" before) and is **declarative, dbt-style convergent**: at
  create it persists `fabricator.sortedBy` + declares the `delta.clustering` domain (unpartitioned);
  on a run against an EXISTING table whose persisted spec DIFFERS, `DeltaCatalog.BulkInsert` RE-KEYS
  FIRST (the SetSortedBy machinery — one metadata commit, property+domain; old ZCubes go stale → next
  OPTIMIZE reclusters incrementally) then writes — so repeated runs are metadata-idempotent and a
  config change converges the table on the next run. Runs AFTER the MODE dispositions (no metadata side
  effects from 'error'/'ignore'); ABSENT option keeps the persisted spec (PARTITION_COLUMNS precedent);
  REMOVAL is DDL (`ALTER … RESET SORTED BY` — an empty option value can't cross the column-list ABI,
  `SplitColumnList` collapses it to null); changing the spec inside an explicit txn → clean error (the
  re-key is an immediate commit). Composes with `PARTITION_COLUMNS` (property-only, no domain). Also
  reaches the SQL Server provider (warehouse `CLUSTER BY` on CREATE_TABLE copies, v52 machinery — no
  re-key surface there, create-time only). `verify_delta_copy_format` 109 (create persists property+
  domain + ordered file, same-spec append = exactly one data commit, changed-spec run = SET SORTED BY
  commit + data commit with the property converged, partitioned = property-only). NOTE a create-COPY is
  TWO commits (v0 CREATE + v1 WRITE).
- **`SORTED BY` → Delta ORDERED (clustered) writes — DONE (2026-07-18, C#-only, no ABI).** The v52 native
  clause (`CREATE TABLE lake.s.t SORTED BY (a,b) [AS …]` — `sort_columns` already crossed the ABI to
  `create_table`/`begin_bulk`; Delta previously ignored it) now drives the Delta provider:
  **ONE up-front interception in `DeltaCatalog.BulkInsert`** wraps the live input stream in
  `SortStream` = `Host.Query("SELECT * FROM __fabricator_sort_input ORDER BY …", inputs)` — DuckDB's
  EXTERNAL (disk-spilling) sort does the global reorder, the bridge stays streaming, and EVERY downstream
  path (native COPY / codec collect / buffered CTAS / eager writes) consumes the already-sorted stream
  (crucially the sort runs on OUR side of the ABI, downstream of any DuckDB source parallelism). The spec
  PERSISTS as the **`fabricator.sortedBy` table property** (JSON array; threaded like the
  `delta.isolationLevel` create stamp through `DeltaWriter.Create/Write/TryWriteStreaming` → `CreateConfig`;
  buffered creates park `CreateSortColumns` on the txn buffer for the flush) — so **plain INSERTs re-apply
  the ORDER BY** (`SortedByFromConfig`: read once per catalog instance via `DeltaReader.GetTableConfig`,
  cached in `_sortedByCache`, invalidated on set_tblproperties/DROP/RENAME; persisted columns missing from
  a write's schema are tolerantly skipped). Change/disable per table via
  `fabricator_delta_set_tblproperties('lake','main.t','{"fabricator.sortedBy":null}')`. SORTED BY =
  LEXICOGRAPHIC order (knowledge-free — works on a first load); for multi-key clustering put
  `hilbert_index(...)` in an explicit ORDER BY (bucketing needs distribution knowledge — see the
  hilbert_index bullet). `test/verify_delta_sorted_by.test` (30 — native + codec CTAS file order via
  per-file rowid lag checks, append re-applies, UNSET disables, empty CREATE + first INSERT, buffered-txn
  CTAS, re-attach durability); native_write 147 / write 31 / tblproperties 42 / transactions 941
  unregressed. NOTE the SQL-Server-warehouse `SORTED BY → WITH (CLUSTER BY …)` mapping (v52) is untouched —
  the same clause now means the analogous thing on both providers.
- **dbt DAX→Delta pipeline — DONE + VALIDATED LIVE (2026-07-16): `dbt_dax_test/` (gitignored — NO creds in
  the project).** A second dbt-duckdb harness proving DAX EVALUATE results materialized as OneLake Delta
  tables: the `plugins/fabric_attach.py` plugin executes the repo-root `dax_secret.sql` per connection
  (secrets never enter the project) then ATTACHes BOTH catalogs — `lake` (LH lakehouse, `PROVIDER 'delta'`,
  `READ_ONLY false`) + `dax` (`PROVIDER 'dax'`, workspace XMLA `powerbi://…/Test;Initial Catalog=Test
  Warehouse Model2`). TWO model shapes, both PASS: (1) plain SQL wrapping the DAX —
  `SELECT … FROM dax."Model".daxeval(expression := 'EVALUATE …')`, ordinary `table` materialization CTASes
  into `lake.dbt.*` (NOT `fabricator_exec` — daxeval is the result-returning surface); (2) **PLAIN DAX as
  the whole model body** via the custom **`dax_table` materialization** (`macros/dax_table.sql`): dbt never
  parses model SQL — it renders jinja and hands the text to the materialization, which wraps `compiled_code`
  in `daxeval(expression := '<body, quotes doubled>')` + `CREATE OR REPLACE TABLE {{ this }} AS …`
  (config `dax_catalog`/`dax_model`; jinja caveat: literal `{{`/`{%` in DAX needs `{% raw %}` — single
  braces/table constructors fine). The FULL LOOP is validated: the user-created **`LH_semtest`** semantic
  model over the LH lakehouse (table `arrownet_bigdv`) → plain-DAX model `EVALUATE TOPN(100,
  'arrownet_bigdv')` → `lake.dbt.bigdv_top100` back on the SAME lakehouse (100 rows exact; the plugin
  attaches a LIST of models — `daxlh` = LH_semtest, `dax` = Test Warehouse Model2). GOTCHAS: the
  **LH lakehouse's DEFAULT semantic model is EMPTY** → daxeval errors ("needs at least one table") — use a
  user-created model (LH_semtest) or a populated one; a
  workspace XMLA endpoint WITHOUT `Initial Catalog` auto-binds the FIRST model with the schema named by
  GUID (give `Initial Catalog` → schema binds by name, e.g. `Model`); a `+schema:` in dbt_project.yml
  CONCATENATES with the profile schema (dbt default `generate_schema_name` → `dbt_dbt`) — set the schema in
  the PROFILE only. Run: `cd dbt_dax_test && ../dbt_mssql_test/.venv/Scripts/dbt.exe run` (~50s incl.
  introspection; models 4-6s each).
- **Eager-write DeltaTxnBuffer (PLANNED, analysis 2026-07-13 — nothing built).** Goal: the buffer holds
  Delta ACTIONS only; data files are ALWAYS written eagerly to storage at statement time (Spark's
  OptimisticTransaction shape — files land immediately, commit deferred, rollback = invisible orphans for
  VACUUM). Kills the three RAM-bound shapes (CTAS-in-txn, insert-under-pending-ALTER, UPDATE post-images)
  and unifies the read overlays. Slices, each independently shippable
  (`verify_delta_catalog_transactions` is the gate):
  - **A — DONE (2026-07-13, C#-only, no ABI):** (1) eager UPDATE post-images — on a `native_write`
    catalog `BufferUpdateRows` writes them as a parquet file at statement time
    (`table.WriteDataFilesAsync(postImages, schemaOverride: pending.PendingDeltaSchema)` → parks
    `WrittenDataFile`s into `pending.Files` instead of batches; NOT NULL validated at statement time
    since the flush only validates Batches; the existing pending-FILES ScanNative routing +
    `DeltaNativeReader(pendingFiles/pendingDeletes/pendingSchema)` serve read-your-writes; ROLLBACK
    orphans the file). (2) `TryWriteStreaming` gained a `pendingSchema` param (deferred-append-only,
    guarded) driving the NOT NULL wrap + mapping rename/FIELD_IDS/stats keying — so
    insert-under-pending-ALTER STREAMS (the `BulkInsert` gate no longer excludes `PendingMetadata`;
    a streaming fallback (identity/iceberg) under a pending ALTER still collects — the data must not
    commit before the schema). Codec catalogs keep in-memory batches (slice C).
    `verify_delta_catalog_transactions.test` §30 (629, +33 write-shape pins: post-image file on storage
    before COMMIT with the log unmoved, streamed 1000-row ALTER'd insert mid-txn, commit adds no data
    file, rollback leaves the orphan); full delta sweep green (native_write 147 / native_read 66 /
    update 63 / alter 116 / column_mapping 251 / constraints 50 / dv_default 58 / write 31 / variant 133
    / partition 54 / nested_alter 100).
  - **B — DONE (2026-07-13, C#-only + one EW param):** CTAS-in-transaction STREAMS on `native_write`
    catalogs. `DeltaWriter.TryStreamCreateFiles` writes the CTAS data to a parquet file in the (not yet
    existing) table folder via the native COPY — **no `_delta_log` is touched** — and parks
    `WrittenDataFile`s + the **pre-assigned column-mapping schema** (`ColumnMapping.AssignColumnMapping`
    runs at BUFFER time — physical names are RANDOM GUIDs, `GeneratePhysicalName` = `col-{Guid}`, so the
    correction to the plan: assignment is NOT deterministic and must happen once, before the files). The
    EW seam is `CreateAsync`/`OpenOrCreateAsync(preAssignedSchema:)` — the create adopts the parked
    schema instead of re-assigning (maxId via `GetMaxColumnId`). Flush = v0 CREATE (pre-assigned) + ONE
    `CommitDataFilesAsync` fusing the streamed files with any later collected batches;
    `ScanPendingCreated` reads the pending files back via host `read_parquet` +
    `ArrowColumnMappingRename.Wrap(toPhysical:false)` + the new `DeltaTxnBuffer.ProjectStream`
    (transient-source projector, ownership moves — unlike `ProjectPending`'s clone-and-keep). Rollback
    leaves orphan parquet in a `_delta_log`-less folder (not a table to any reader; same-name re-create
    works — pinned). Partitioned pending creates keep the collect path (read-back would need Hive-dir
    reconstruction). `verify_delta_catalog_transactions.test` §31 (653, +24 pins: file on storage with
    ZERO log entries mid-txn, read-your-writes incl. WHERE + later-INSERT overlay, v0+v1 commit shape,
    the all-NULL-on-physical-name-mismatch cross-check, rollback orphan + re-create);
    **delta-kernel reads the eager-CTAS commit exactly** (delta_scan probe); sweep green (native_write
    147 / write 31 / column_mapping 251 / update 63 / partition 54 / native_read 66 / txn_version 51 /
    copy_format 96).
  - **C1 — DONE (2026-07-13, C#-only): codec catalogs go eager-per-statement.** In EXPLICIT transactions
    (autocommit keeps the byte-identical park-batches shape) a statement's collected batches become a
    data file at statement end via the shared `TryEagerWriteBatches` (`WriteDataFilesAsync` — EW codec
    writer, or DuckDB's under native_write; write-tuning spec passed; NOT NULL validated at statement
    time) — codec-transaction memory caps at ONE statement. The A1 UPDATE post-image eager write now
    uses the same helper (codec included). Mid-txn reads route through the existing
    Files>0→`ScanNative` path — now exercised on pure-codec catalogs (read_parquet reads codec files,
    already broadly validated by the native_read suite). Fallbacks that still park batches:
    identity/iceberg (no external commit), `materialize_row_tracking` (codec appends must materialize
    `__delta_row_id`, only EW's committing writer does), pending-created tables (later INSERTs).
    §32 (+28 pins) — and the pre-existing codec-txn sections §1–9 (400+ assertions) pass unchanged on
    the new path.
  - **C2 — DONE (2026-07-13): CDF in explicit transactions** (was rejected — a real user-facing hole).
    Every buffered statement on an (unpartitioned) CDF table writes its `_change_data` file at
    STATEMENT time and parks the `CdcFile` action (`DeltaTxnBuffer.PendingCdc`), fused into the ONE
    commit at flush — **inserts included**: a commit carrying ANY cdc action is read cdc-ONLY by the
    CDF reader (inference disabled), so appends fused with DML would otherwise vanish from the feed.
    Mechanics: appends → insert-cdc from the statement's batches (CDF appends skip the streamed path —
    the rows must be in hand; probed once per (txn, table) via `TxnDmlProfile` + cached
    `CdfEnabled`, which also pins the base version so the CDF flush always has a rebase base);
    DELETE → one extra `ReadRowsByRowIds` read-back for the deleted rows' content (the position set has
    no values); UPDATE → pre-images (read-back, committed values) + post-images, the autocommit
    merge-on-read pair. EW seam: public `DeltaTable.WriteChangeDataFileAsync` (wraps the internal
    `CdfWriter.WriteAsync`; cdc actions carry `DataChange=false` so concurrent readers' dataChange
    checks ignore them; rebase-safe — delete-delete/deleteRead guarantee our touched files unchanged,
    so captured CDC content stays exact). Still guarded (clean errors): PARTITIONED CDF tables
    (cdc partitionValues/column re-add semantics deferred — their buffered appends stay cdc-less +
    inference-correct since their DML is rejected, commits never mix) and DML-after-buffered-ALTER on
    CDF (cdc files would be written against the pre-ALTER shape). §7 rewritten (positive: cdc file on
    storage pre-COMMIT, log unmoved, rollback orphans it) + §33 (fused-commit feed EXACT per type:
    2 inserts + delete + update pre/post with correct values; pure-append txn feed; autocommit
    inference intact) — `verify_delta_catalog_transactions` now 732; changes 73 / dv_default 58 / dv 48
    / update 63 / delete 28 / native_write 147 / write 31 / partition_overwrite 90 / txn_version 51 /
    constraints 50 green.
  - **Eager-write EDGE LIFTS — DONE (2026-07-13, follow-up pass):** (a) **materialize_row_tracking
    appends eager-write** (the C1 gate was over-conservative — a FRESH append needs no physical
    `__delta_row_id`; readers derive `baseRowId + position`, the validated streamed-native shape the
    flush's own `WriteDataFilesAsync` batch path already produced). (b) **IDENTITY appends eager-write
    with abort-on-concurrent-consumption**: values GENERATED at statement time from the pinned/chained
    HWM (`DeltaTxnBuffer.PendingIdentityHwm` chains across statements; read-your-writes now shows REAL
    ids — the batch park showed NULLs), files written via
    `WriteDataFilesAsync(identityValuesPreGenerated:)` and committed via
    `CommitDataFilesAsync(identityValuesPreGenerated:)` (both gates gained the bypass; Iceberg still
    rejected); the flush composes the HWM into the (single) metaData action
    (`BuildIdentityMetadataAction(marks, pending.PendingMetadata)` — never two metaData actions). EW
    seams: `DeltaTable.GenerateIdentityValues(batches, chainedHwm)` (wraps `IdentityColumnWriter.
    ProcessBatch` with seeded configs) + `BuildIdentityMetadataAction` + `HasIdentityColumns`/
    `IsIcebergCompat`. Concurrency = Spark's policy for FREE: a concurrent identity-consuming commit
    necessarily carries metaData → the rebase metadataChangedCheck aborts us (vs autocommit's
    regenerate-retry — a deliberate liveness trade on the rare same-table-concurrent-insert);
    non-consuming concurrent commits rebase fine. Identity×CDF in a txn is REJECTED (cdc capture would
    precede value generation → NULL ids in the feed; the CDF probe also excludes identity tables so
    their pure appends stay inference-correct). Kernel-validated (fused identity commit → 5 distinct
    ids 1..5 via delta_scan). (c) **materialize UPDATE post-images bake the ORIGINAL stable ids**:
    lifted the buffered-UPDATE materialize rejection (now partitioned-only); stable id =
    `baseRowId[ordinal] + position` against the ordinal-ordered active set (EW's own merge-on-read
    rule — line-checked: it does NOT read source materialized columns either), via new EW
    `OrderedActiveBaseRowIds()` + `WriteDataFilesAsync(materializedRowIds:)` (flat/aligned,
    unpartitioned-only like merge-on-read; appended after ToPhysical). An eager-write failure on a
    materialize UPDATE is a HARD error (the batch park can't bake ids — never silently degrade).
    §34+§35 (781 total): materialize txn-append file-on-storage; identity chained ids across
    statements + distinct-after-commit + HWM continuation + rollback-regeneration; the post-image
    parquet carries `__delta_row_id` with the ORIGINAL id (=1 for row 2). **The pre-existing
    read-back race is FIXED (same pass)**: `ReadRowsByRowIdsAsync` + `OrderedActiveBaseRowIdsAsync`
    gained `atVersion` (resolve the snapshot the rowids were SCANNED against — ordinals are path-sort
    positions in THAT active set), and all three consumers (buffered UPDATE read-back, CDF-DELETE
    read-back, materialize base-id resolution) pass `pending.PinnedVersion` — a concurrent commuting
    append between the transaction's first scan and its DML statement can no longer shift the ordinal
    resolution. §36 pins it with a two-connection racer (three appends land between pin and UPDATE;
    the correct pre-image is captured deterministically — previously luck-of-the-uuid-sort);
    `verify_delta_catalog_transactions` now 805.
  - **C3 same-txn-DML lift — DONE (2026-07-13): DELETE of rows inserted in the SAME transaction.**
    The eager-write architecture made it cheap: all DML-eligible tables park FILES now, so a
    same-transaction delete = an inline deletion vector BORN ON our own pending add. Routing: rowid
    ordinals ≥ `PendingFileOrdinalBase` (0x780000, = index into `pending.Files`) key straight into
    `DeletedByOrdinal` — the native reader's pending-file exclusion (`WithPendingDeletes` matches by
    ordinal, and pending files carry 0x780000+idx) serves read-your-writes with ZERO new read code; the
    flush splits high keys off to the new EW
    `CommitDataFilesAsync(deletedPositionsByFileIndex:)` (builds the inline DV via
    `DeletionVectorWriter.CreateAsync` + marks the add's stats `tightBounds:false` via the existing
    `StatsWithLooseBounds`) while committed-ordinal keys keep the pinned-snapshot DV-pair path. Mixed
    committed+pending deletes in one statement work. Still guarded (clean errors): UPDATE of same-txn
    rows; same-txn DELETE on CDF tables (the insert-cdc file already captured the row); in-memory
    BATCH rows (0x700000..0x77ffff — the identity-under-ALTER/iceberg fallbacks, practically
    unreachable). Buffered DML's DvEnabled requirement covers the add-DV's reader-v3 need. §37 (+ the
    old "rejected (later slice)" pin converted positive): codec + native_write catalogs, partial +
    mixed deletes, rollback discards both halves, guards — `verify_delta_catalog_transactions` now
    861; **delta-kernel reads the born-with-DV add exactly** (9 rows, deleted ids absent).
  - **C3 virtual-table refactor — DONE (2026-07-13, behavior-identical):** the codec read paths
    collapsed into ONE **`ScanCodec`** — the codec-path virtual-table read (pinned base stream ⊕
    pending-delete exclusion [rowid stream forced when needed] ⊕ pending-ALTER schema/reconcile ⊕
    pending-batch overlay; every step conditional, so no-pending scans pass straight through).
    `ScanCodecWithPendingDml` deleted (it was ~80% a copy of ScanTable's tail); shared
    `SchemaWithRowId` + `TranslateProjectionToCommitted` helpers; net −28 lines. **The EW-synthetic-
    Snapshot form ("pinned ⊕ pending actions" as a real `Snapshot`) was evaluated and REJECTED on an
    architectural finding:** EW's `OrderedActiveFiles` path-sorts the WHOLE active set, so uuid-named
    pending files would interleave into the committed ordinal range and break the transient-rowid
    contract every layer shares (committed ordinals < 0x700000, pending files at 0x780000+idx — scans,
    position parking, the flush's DV split, the same-txn-DML routing). The overlay composition IS the
    virtual table, with the ordinal spaces disjoint by construction (recorded on ScanCodec's doc
    comment). Gate: transactions 861 unchanged + the FULL delta sweep (36 suites) green.
  - **D — DONE (2026-07-13): S3 MULTI-WRITER COMMITS via our own conditional PUT.** httpfs never passes
    `If-None-Match`, so plain-ATTACH S3 stays documented single-writer; **ATTACH with an s3 `SECRET`**
    (`ATTACH 's3://…' (…, PROVIDER 'delta', SECRET minio_s3, READ_ONLY false)`) routes the COMMIT rename
    through a REAL put-if-absent: new Bridge `S3CommitFileSystem` (S3CommitFileSystem.cs) — the HYBRID
    FS: all data IO delegates to `DuckDbTableFileSystem` (opener secrets, host transport/caching), but
    `RenameAsync` = SDK **GetObject(temp) → PutObject(target, If-None-Match:"*") → Delete(temp)** (412 →
    false → `DeltaConflictException` → the OCC/rebase machinery). **CRITICAL PROBE FINDING: a
    conditional CopyObject is SILENTLY UNGUARDED on MinIO** (the copy succeeds over an existing target —
    AWS's documented conditional writes are PutObject/CompleteMultipartUpload only), so EW's
    `S3TableFileSystem.RenameAsync` copy-based primitive gave NO commit safety — **fixed in EW** to the
    same Get→conditional-Put→Delete shape. Wiring mirrors OneLake: `DeltaBackend.BuildConnectionString`
    s3-secret branch → `S3CommitCredential.AppendMarker` (`;FabricatorS3Cred=`) → catalog `_s3Credential`
    → `Opener()` publishes `AmbientS3Credential` → `TableFileSystems.Create` wraps s3:// roots.
    Credential mapping: key_id/secret/session_token/endpoint/region; `URL_STYLE 'path'` →
    `ForcePathStyle`; `USE_SSL`; a CUSTOM endpoint tolerates self-signed certs (the rig posture — AWS
    default endpoint keeps full validation); empty key_id → the SDK default chain. **Second finding:
    `S3CommitFileSystem.ReadAllBytesAsync` goes through the SDK too** — httpfs pins the etag recorded at
    open (and re-serves it from its caches), so a concurrent writer's IN-PLACE `_last_checkpoint`
    overwrite failed host reads with "ETag … has changed" EVEN ACROSS REOPENS; a plain GetObject always
    returns a consistent copy (small files only — data files stay host-path: immutable + cache-friendly;
    a bounded etag-retry also went into `DuckDbTableFileSystem.ReadAllBytesAsync` for the secretless
    path). **VALIDATED LIVE on MinIO:** 4 racing processes × 10 commits × 20 rows → **40/40 commits,
    800/800 rows, zero errors, across 4 checkpoint boundaries** (before the SDK-reads fix: loud
    checkpoint-read failures, NO silent loss — the guard held); a WRONG-key marker secret fails the
    commit with the SDK signature error while data IO succeeds (proving the SDK is in the loop);
    `verify_delta_catalog_s3.test` §9 (144 — SECRET-route lifecycle incl. fused txn; secretless
    sections unchanged). Outcome: S3 catalogs with a SECRET are safe multi-process/multi-engine.
    **The deltars provider could get the same for free via storage_options** (delta-rs enables native S3
    conditional-put locking with `conditional_put: "etag"`, no DynamoDB `AWS_S3_LOCKING_PROVIDER` needed —
    one line on `DeltaRsCatalog`'s `storage_options` when/if S3 discovery lands). **DECISION (2026-07-15): NOT
    NEEDED / won't prioritize.** engineeredwooddelta ALREADY ships S3 multi-writer (codec + native_write, via
    the hand-rolled `S3CommitFileSystem` conditional PUT, validated live on MinIO), so the etag option would
    only bring the OPT-IN deltars provider to parity on a capability we already have — no concurrency gap to
    close. deltars stays valuable for OTHER reasons (reference reader/writer, DataFusion pushdown, the
    maintenance ops EW lacks: OPTIMIZE/Z-ORDER/VACUUM/CHECKPOINT/MERGE); if S3 *discovery* is ever wired for
    those, adding the one storage-option line is trivial then. Do NOT re-propose it as an S3-concurrency item.
  - **Partitioned × native_read BUG FIXED + partitioned pending-CTAS lifted (2026-07-13).** Probing the
    "why keep partitioned pending-creates buffered" question exposed a REAL committed-read bug: under
    `native_read`, a PARTITIONED table's partition column read **all-NULL in BOTH mapping modes** — the
    per-file presence probe saw it absent from the parquet footer and NULL-backfilled it like schema
    evolution (hive auto-detection never engaged; the combination was untested). Fix is
    log-authoritative: `NativeScanFile` carries each add's `partitionValues` +
    `NativeScanList.PartitionColumns`, and `FileSql` renders partition columns as **typed literals**
    (`CAST('US' AS VARCHAR) AS "region"`; dual logical|physical key lookup — new mapped commits key
    physical, old EW commits logical; missing/sentinel ⇒ NULL) — never path parsing (partitionValues is
    the spec's authoritative source; paths are opaque). WHERE/pruning on the partition column work
    (outer WHERE sees the literal; DeltaFilePruner already pruned by partitionValues).
    `verify_delta_catalog_native_read` 66→88 (both modes, GROUP BY/filter pins). **The same literal
    machinery lifted the partitioned pending-CTAS collect gate**: `TryStreamCreateFiles` gained the
    partitioned branch (`RunCopyPartitioned` — Hive layout streamed in one COPY, physical dirs +
    FIELD_IDS-minus-partition-cols under mapping, per-file partitionValues on the `WrittenDataFile`s),
    and `ScanPendingCreated`'s read-back renders per-file partition literals (physical→logical via the
    pre-assigned schema; `TypeText`/`LookupPartitionValue` widened internal for reuse). §39 pins the
    streamed partitioned CTAS-in-txn (Hive dirs pre-COMMIT + zero log entries, read-your-writes incl.
    GROUP BY/filter on the partition column, v0+v1 commit); kernel reads the fused commit;
    `verify_delta_catalog_transactions` 912.
  - **Buffered IDENTITY create — LIFTED (2026-07-13, same pass):** `CREATE TABLE t (id BIGINT AS (0), …)`
    inside `BEGIN..COMMIT` is now TRANSACTIONAL (was immediate — a ROLLBACK couldn't undo it). Simpler
    than the committed-table identity case because a never-committed table has NO concurrent-HWM problem:
    the identity metadata attaches BEFORE the buffer gate (shared `WithIdentityMetadata`); INSERTs into
    the pending table generate values at STATEMENT time from the parked schema via the new EW **static**
    `DeltaTable.GenerateIdentityValuesForSchema(schema, batches, chained)` (the instance method now
    delegates; marks chain via `PendingIdentityHwm` — read-your-writes shows REAL ids, previously NULLs
    would have shown); the flush **bakes the final marks into commit-0's schema metadata**
    (`BakeIdentityMarks` — no separate metaData action) and writes the batches with
    `identityValuesPreGenerated: true` (regeneration would double-consume the mark). §40 (create leaves
    zero log entries pre-COMMIT, chained distinct ids, HWM continues post-commit in autocommit,
    ROLLBACK leaves nothing); kernel-exact (ids 1..3 buffered + 4 autocommit);
    `verify_delta_catalog_transactions` 934, identity 38 unchanged. Remaining batch-park cases:
    iceberg, identity-under-pending-ALTER, autocommit codec (by design).
  - **dbt REGRESSION found + FIXED by re-running the lakehouse harness (2026-07-13):** dbt's table
    materialization swaps via `CREATE <model>__dbt_tmp AS …; ALTER <model> RENAME TO <model>__dbt_backup;
    ALTER <model>__dbt_tmp RENAME TO <model>; COMMIT` — the second RENAME hits a table CREATED in the
    same transaction, which the buffered-CREATE work rejected ("uncommitted buffered changes"), failing
    EVERY dbt table model on Delta targets (and the already-executed immediate old→backup folder rename
    is NOT undone by the rollback — the pre-existing non-transactional-RENAME hole made it worse). Fix:
    **RENAME TABLE of a PENDING-CREATED table** re-keys the buffer (`DeltaTxnBuffer.RenameTable`) and
    moves any eagerly-streamed files to the new folder (OneLake DFS rename / `HostFs.MoveDir` / per-file
    copy+delete fallback for S3); the flush then creates the table at its FINAL path. §38 pins the dbt
    swap on codec + native_write catalogs (`verify_delta_catalog_transactions` 888).
    **dbt harness status (all re-validated):** `lakehouse` (OneLake) `dbt run --threads 4` PASS=4/4
    (~77s); NEW **`minio` target** (S3 Delta catalog; profile s3 secret + the onelake_attach plugin
    gained `curl_insecure` for the rig's self-signed TLS; the ATTACH `SECRET minio_s3` also engages the
    slice-D conditional commits) PASS=4/4 (~9s). NEW **`delta_external` materialization**
    (`macros/delta_external.sql`): dbt-duckdb's built-in `external` whitelists csv/parquet/json, so a
    custom materialization runs `COPY (model) TO '<location>' (FORMAT delta, MODE …)` (any location —
    s3://, onelake://, local; no ATTACH) + registers a view over `fabricator_delta_scan(location)` for
    downstream refs (model config: `database=target.database` so the view lands in the writable local
    db); demo `models/ext_delta.sql` aggregates a Delta-catalog model → standalone Delta table on
    MinIO, read-back verified. NEW **dbt SNAPSHOTS work on Delta catalogs** (`snapshots/customers_snap`,
    check strategy): the SCD-2 merge = staging CTAS (a buffered pending-create the post-snapshot DROP
    cancels) + UPDATE (close old versions) + INSERT (new versions) in ONE dbt transaction — the buffered
    DML machinery end-to-end. Crux: dbt-duckdb's `make_temp_relation` NULLS database/schema → the
    staging lands in the LOCAL db and DuckDB's one-write-database-per-transaction rule kills the merge
    ("a single transaction can only write to a single attached database") — project macro
    `build_snapshot_staging_table` override stages IN THE TARGET'S database
    (`macros/snapshot_staging.sql`). SCD-2 validated on MinIO (changed row: old version closed by the
    UPDATE + new current INSERTed; new key inserted) AND live OneLake (bronze→silver, two versions).
  - **Stays immediate:** identity creates (value generation + HWM update are coupled inside EW's
    committing writer — `WriteDataFilesAsync` rejects identity tables by design), DROP/OPTIMIZE/VACUUM
    (physical/administrative — no rollback semantics possible). CREATE-OR-REPLACE + partition-overwrite
    are REPRESENTABLE as actions later (snapshot-tied removes: delete-delete guards them; needs
    whole-table-read recording, and note Spark's WriteSerializable permits the concurrent-blind-append
    reorder past an overwrite) — keep guarded until after slice C.
- **Fabric-notebook AMBIENT AUTH — DONE + VALIDATED LIVE (2026-07-14, C#-only, no ABI).** In a Fabric
  notebook/Spark session ALL THREE providers work with ZERO credentials: **SQL**
  (`ATTACH 'Server=<endpoint>;Database=<wh>'` bare, or `authentication 'default'`) against Warehouse AND
  Lakehouse SQL endpoints; **Delta-on-OneLake** (`ATTACH 'abfss://…/Tables' (TYPE fabricator, PROVIDER
  'delta', READ_ONLY false)` — no SECRET); **DAX** (`ATTACH 'Data Source=powerbi://…;Initial
  Catalog=<model>' (PROVIDER 'dax')` — **AdomdClient PROVEN on Fabric Linux compute** (the old
  "Linux-TBD" is resolved; sempy uses Adomd.NET on the same image); at a WORKSPACE XMLA endpoint the
  model SCHEMA binds by the semantic-model GUID (not displayName), and an EMPTY default model errors
  "DAX Evaluate queries work only on databases which have at least one table" — iterate models).
  Mechanism = **`FabricNotebookCredential`** (Bridge): DefaultAzureCredential is SOURCELESS on Fabric
  compute (no IMDS, no AZURE_* env — every chain link fails, proven live), so NO SqlClient
  `Authentication=Active Directory *` keyword can work there; the ambient identity is brokered by the
  Fabric TOKEN SERVICE (the same local service `notebookutils.credentials.getToken` fronts):
  `GET {AZURE_FABRIC_TOKEN_SERVICE_URL}?resource=<audience>` with `x-ms-partner-token` = the spark-conf
  `trident.session.token` (env `MSNOTEBOOKUTILS_TRIDENT_SESSION_TOKEN` — **NOT**
  `/opt/token-service/tokenservice.config.json`'s sessionToken, a DIFFERENT cluster-level token that
  401s `SignedPayloadValidationException`; found by request ablation on the live runtime) + MANDATORY
  `x-ms-proxy-host` (host of `trident.lakehouse.tokenservice.endpoint`, 400 without) + cluster id
  (`AZURE_FABRIC_CLUSTER_IDENTIFIER`) + tenant (tid claim of the session token); response body = the
  raw AAD token; cached per resource, re-minted 5 min pre-expiry ⇒ **REFRESHING, PER-SCOPE tokens**
  (fabric-REST + storage + SQL + powerbi audiences — the multi-audience need one static token can't
  cover). Wiring: `FabricCredentialResolver.AmbientChain()` (FabricNotebookCredential when the Fabric
  env markers exist, else DefaultAzureCredential) replaced every direct DefaultAzureCredential fallback
  (resolver Build + ResolveForRemoteTarget, FabricLakehouse ×3, OneLakeForwardFs); **SQL ambient
  interception** in `SqlServerCatalog`: a NO-credential connstr (bare / AD-Default; never when any
  password/token/integrated/other-AD auth is present) on Fabric compute switches to
  `SqlConnection.AccessTokenCallback` minting database.windows.net tokens (SqlClient re-acquires per
  connection open); DAX refresh = the existing `AdomdConnection.OnAccessTokenExpired` → fresh mint.
  UNDOCUMENTED internal protocol — engaged ONLY when the env markers exist (off-Fabric byte-identical,
  local suites green); re-capture harness = scratchpad/fabricnb's token-service ablation step. **Drift
  risk is LOW: sempy's own leaf (`fabric.analytics.environment.notebook_plugin.token_service_client.
  TokenServiceClient`, source-verified 2026-07-14) uses the IDENTICAL request** — same URL composition,
  same `trident.session.token` partner token (config file read ONLY for tokenServiceEndpoint+clusterName),
  same proxy-host, JWT-exp expiry; the contract is load-bearing for sempy/SynapseML/notebookutils (their
  resource shortforms `pbi`/`storage`/`sql`/`keyvault`/`kusto`/`ml` also work beside full audience URLs;
  they send moniker=clusterName, we send the artifact id — both accepted). sempy also ships the SAME
  managed AdomdClient via pythonnet with the SAME AccessToken+OnAccessTokenExpired wiring — independent
  confirmation of our DAX stack on Fabric Linux.
  **Also: duckdb AZURE `PROVIDER access_token` secrets are now consumed** (the common Fabric-notebook
  blog pattern): the SQL provider maps the field onto `SqlConnection.AccessToken` (the token must be
  database.windows.net-audience — a storage-scoped one 18456s; validated live) and
  `FabricCredentialResolver.Resolve` serves it as a static credential (expiry from the JWT exp; NO
  refresh — re-create the secret). **Pinned gap:** an abfss OneLake ATTACH with a STATIC storage-token
  azure secret fails at the Fabric REST hop (401 InvalidToken — a single-token secret can't serve the
  fabric + storage audiences) — in notebooks use the ambient no-secret form instead. Identity context:
  interactive = the user; pipeline / on-demand job = the submitting SP / workspace identity (probe
  showed `idtyp: app`). Notebook probe: `scratchpad/fabricnb` (pbi-vs-database token audiences —
  the SQL endpoint REJECTS powerbi/api-audience tokens with 18456; bare/default ambient SQL; secretless
  delta abfss read; DAX daxeval → 2; azure-secret forms; `notebookutils` token-passing forms, now
  superseded by ambient). Local-catalog gotcha re-proven: `SHOW ALL TABLES`/`duckdb_tables()` scan EVERY
  attached catalog (the populated-LH fuse hang) — use catalog-qualified/targeted queries in notebooks.
- **Sync-over-async cleanup (Bridge) — ENABLER DONE (`0533eb7`), refactor DEFERRED.** The Bridge blocks the
  C++↔C# boundary with `.GetAwaiter().GetResult()` sprinkled at every `await` site. This is **correct + safe**
  here (the hostfxr CLR has NO `SynchronizationContext`, so sync-over-async can't deadlock; the ABI is
  synchronous), just ugly + slightly worse for exception unwrapping — so there's **no urgency**. The clean shape
  = a sync ABI-facing method that blocks ONCE on a private `async` core (`ConfigureAwait(false)` throughout).
  **The landmine** is the ambients (`AmbientOpener`/`AmbientTransaction`/`AmbientOneLakeCredential`): they were
  `[ThreadStatic]`, so a real-async core with `ConfigureAwait(false)` would hop pool threads and LOSE the
  opener/credential/txn mid-op (silent — passes local tests where opener=0 works, breaks on OneLake + explicit
  txns). **Fixed as the prerequisite: converted to `AsyncLocal<T>` (`0533eb7`)** — flows across await/pool-thread
  hops, behavior-identical for the current sync code (validated: full local delta catalog suite + a live OneLake
  CTAS/DELETE/readback/DROP, so the opener+credential still cross the `set_active_opener`→use ABI-call boundary).
  So the refactor is now UNBLOCKED. **When: later + incremental** (leaf-first — `DeltaReader` read/write have a
  clean single-blocking-point shape — one seam at a time, `verify_delta_catalog_*` after each; never interleaved
  with feature work), and adopt the sync-wrapper→async-core shape as a **convention for NEW code now**.
  `IAsyncDisposable` is lower value (the C++ boundary disposes synchronously → keep a sync `Dispose()` that blocks
  once on `DisposeAsync()`; use `await using` only INSIDE the async cores). Logging note: keep it OUT of the hot
  ambient accessors + per-row/scan paths (log-spam + file-sink serialization risk); the write/DML path logging
  (`3d60cb5`) + DDL logging (`0533eb7`) sit at low-frequency decision points only.
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
  param + NO new operator (the v29 table session is reused verbatim). **`fabricator_delta_scan` migrated to a
  pure-C# global host-FS `ITableFunction`** (`DeltaGlobalTableFunction`, Bridge, over engineered-wood +
  `DuckDbTableFileSystem`, declared in `CustomFunctions.GlobalTable`); the bespoke `fabricator_delta.cpp` + the
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
  rides the `list_global_functions` metadata (a `string_order` column) → `FabricatorTableFunctionInfo` →
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
  C++ `RegisterFabricatorGlobalFunctions` builds a `ScalarFunction` per scalar decl (shared
  `BuildFabricatorScalarFunction`, handle=0) at load (best-effort — skipped if the bridge can't boot). Demo
  **`fabricator_render(template, params)`** — the **Fluid/Liquid** template engine (secure-by-default,
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
  `GlobalCollectorFunctions`; C++ `RegisterFabricatorGlobalFunctions` branches on `kind` at load →
  `loader.RegisterFunction`. Slices: (1) scalar **DONE** — template engine **`fabricator_render`** via **Fluid**
  (Liquid, secure-by-default); (2) in-out/collector **DONE** (pure-C#, **no opener**; demos `fabricator_tag`
  streaming + `fabricator_collect_sum` collector; `inout_bind` handle-0 → C# global registry; reuses the v28
  exchange ABI, no bump — enables the effectful global *apply* half, e.g. `fabricator_apply_tmdl` collector);
  (3) compute/connstr table **DONE** (`table_bind` handle-0 → `GlobalFunctions.ResolveTable` over the v29
  session; the handle-0 `get_function_param_schema` is kind-agnostic via `GlobalFunctions.ParamSchema`;
  `BindingBoundTable` moved to the Bridge; demos `fabricator_seq` fixed-schema + `fabricator_columns` arg-dependent
  schema); (4) aggregate **DONE** (`IAggregateFunction` base + `ICatalogAggregateFunction`; `AggSessionImpl` →
  the Bridge as public `AggregateSession` shared by catalog+global; `agg_open` handle-0 →
  `GlobalFunctions.ResolveAggregate`; `ParamSchema`/`ReturnField` kind-agnostic; shared
  `BuildFabricatorAggregateFunction`; reuses the v25/v26 `agg_*` ABI; demo `fabricator_product` — GROUP BY/parallel/
  OVER); (5) **deferred** host-FS table (secret-backed readers like delta) — needs an **opener arg** on
  `table_bind`, delta stays bespoke until a 2nd such reader.
  Composes with TMDL = render-via-(global)scalar then apply-via-(global)table/collector. The rest of this bullet
  is the original table-case detail.
  Today provider functions are **attach-time catalog-bound** (4e/4f/4g — resolved as `db.schema.fn`, dispatched
  via the catalog `handle`). The deferred **Phase 3-A** alternative is **load-time global** functions
  (connection-free, bare `fn(...)`, registered at `Extension::Load`). **This does NOT break the catalog-only
  concept — the two are orthogonal scopes that coexist**: catalog-bound = needs an ATTACH'd catalog + its
  connection (discovered SQL UDFs/procs/TVFs, custom fns using the catalog's SQL conn); global = connection-free
  (`fabricator_delta_scan(path)`, future `fabricator_iceberg_scan`, lakehouse readers — they belong to no SQL Server
  catalog). **The original objection has dissolved:** Phase 3 deferred global functions to avoid booting the CLR
  at `Extension::Load()`, but the settings refactor (v33) + the fs/delta spike already boot the bridge
  best-effort at load. So global functions are now the natural **4th member of the "provider declares; core
  stays name-agnostic" family** (after settings v33 / ATTACH options v37 / secret fields v38): a
  `list_global_functions(provider)` ABI at load → C++ registers each declared scalar/table function
  **generically** (dispatch to C# by name/`decl_id`), the provider authoring them in C# with **zero per-function
  C++**. `fabricator_delta_scan` is **already a global function** (bespoke `RegisterDeltaScan` in
  `fabricator_delta.cpp`) — proof the scope exists. **Two wrinkles found while scoping the generic build (why it's
  deferred until a 2nd lakehouse format/provider lands, not justified by one function):** (1) **arg-dependent
  output schema** — a global table fn's columns depend on its args (delta's schema comes from the `path`), so the
  generic registration must use the v27/v29 `table_bind`(args→schema+binding) shape, not the no-arg
  `get_function_output_schema`; (2) **the opener vs SQL-connection split** — `table_bind`/`table_execute` pass the
  **catalog handle** to C# (SQL fns use the catalog's `SqlConnection`), but `fabricator_delta_scan` needs the
  **host-FS opener (ClientContext)** for IO, which that path doesn't thread through. So **"build the generic
  global path" and "migrate delta onto it" are separable**: the generic path is cleanest for connection/
  connstr-style global functions; **delta is better kept bespoke** (its host-FS-opener need is special) unless the
  global table-fn bind/execute ABI gains an opener arg (SQL fns ignore it). Build it when the 2nd lakehouse
  format/provider arrives; until then delta stays the hand-written ~60-line `fabricator_delta.cpp`.
- **VARIANT for the Delta provider — V1 BUILT (2026-07-06): [docs/variant-support.md](docs/variant-support.md)
  §"AS BUILT"**. `CREATE TABLE lake.s.t (v VARIANT)` / CTAS / INSERT / SELECT / **DV-DELETE** work under
  `native_read true, native_write true` (`test/verify_delta_catalog_variant.test`, 55; **delta-kernel reads
  the result** — the unverified-kernel risk is resolved). The mechanism is NOT the planned per-operator
  pre-cast: **one `ArrowTypeExtension` registered at load** (`src/fabricator/fabricator_variant.cpp` —
  `RegisterFabricatorVariantExtension`; the exporter's `default:` branch consults the registry UNGATED by
  `arrow_lossless_conversion`) makes VARIANT cross EVERY Arrow boundary transparently (bulk/CTAS/COPY
  appenders, host-query result AND input streams — so the streaming COPY sees real VARIANT with no SQL cast —
  scan ingest, `FetchTableColumns` bind schema). Conversions = the parquet extension's scalars via
  `FunctionBinder`/`ExpressionExecutor` (`variant_to_parquet_variant` out / `variant_bytes_to_variant` in).
  **Transport = ONE self-delimiting BLOB per row** (metadata bytes ++ value bytes), marker
  **`fabricator.variant`** — NOT the canonical struct: `ArrowAppender::FinalizeChild` walks the LOGICAL type's
  children (VARIANT = 4) against the internal-type appender (2) → a nested internal type crashes upstream
  ("index 2 within vector of size 2"; **FILED UPSTREAM 2026-07-25 as duckdb/duckdb#24157**, "Support VARIANT
  over the Arrow interface (arrow.parquet.variant)" — use `extension_data->GetInternalType()` there. If it
  lands, the leaf-blob transport below could in principle become the canonical struct, but do NOT plan on
  that: the transport is also what makes EW's internal model the canonical `VariantType`, and Spark/kernel
  interop is validated against the blob form);
  a LEAF internal type (the bool8/geoarrow shape) sidesteps it. EW: `"variant"` primitive ⇄ tagged blob
  (`SchemaConverter.VariantExtensionName`), `variantType` reader+writer feature at create + protocol upgrade
  on ADD COLUMN/SetSchema via the generalized `UpgradeProtocolForFeatures`/`RequiredSchemaFeatures` (replaced
  `UpgradeProtocolForTimestampNtz`), allowlists, stats leaf (nullCount only). Gates (clean errors): variant
  requires native_read+native_write (Bridge, at CREATE/INSERT/scan), CDF-on-create rejected, EW backstops
  reject codec data writes + CoW DELETE / **UPDATE** / **OPTIMIZE** ("would strip the VARIANT annotation" —
  the rewrite READ half is the codec reader even under native_write; DV DELETE is exempt = works, and DV is
  the default). Gotchas: NULL rows can arrive as VALID zero-length blobs (validity dropped in the crossing) —
  ingest maps empty→NULL (minimal `01 00 00 00` variant-null substitution, spec-exact `v IS NULL` round-trip);
  member access = dot `(v).a` (`variant_extract('$.a')` returns NULL in 1.5.4; `->` casts via JSON and fails);
  DuckDB's writer SHREDS small variants by default (read-transparent). Regression: delta 39/39, SQL fn suites
  11/11, EW 168+141. **Fabric Runtime 2.0 VALIDATED LIVE (Spark 4.1.1, both directions)**: we write
  (`lake.dbo.fabricator_varlive`, onelake:// streaming COPY) → Spark `to_json`/`variant_get` read it exactly;
  Spark writes (`fabricator_var_spark`, `parse_json`) → we read typed VARIANT + dot access + correct SQL-NULL.
  One nuance: a SQL-NULL variant WE write reads in Spark as variant JSON-null (`v IS NULL` false there;
  DuckDB reads the same file as SQL NULL — DuckDB-writer representation, not transport). The **SQL Server
  provider REJECTS variant columns** cleanly (`BuildCreateTable` guard — else the tagged blob would silently
  become VARBINARY; cast `v::JSON` to move variant into SQL Server). **V2 DONE — UPDATE/CoW-DELETE/OPTIMIZE
  LIFTED via the `IDataFileReader` codec seam** (docs/variant-support.md §"AS BUILT" second pass): EW gained
  the read-side counterpart of `IDataFileWriter` (`DeltaTableOptions.DataFileReader` — RAW physical batches
  in FILE ORDER, DV rows included; `ReadFileAsync` + `CompactionExecutor` route through it; the per-batch
  pipeline extracted as `ProcessFileBatchesAsync`); Bridge `NativeParquetDataFileReader` = `read_parquet(...,
  file_row_number => true) ORDER BY file_row_number` (explicit order — positions are correctness), wired on
  `native_read` into DeleteByRowIds/UpdateByRowIds/Optimize. With BOTH seams the EW variant gates return
  early → variant UPDATE (incl. SET of the variant value — `BuildArray` Binary case + the rewriter
  update-view keeps the marker metadata so the bound view types as VARIANT) + CoW DELETE + OPTIMIZE all work,
  kernel-validated post-DML (`verify_delta_catalog_variant.test` 72). **Crux fix: the clean-rebuild before
  every rewrite (`DeltaTable.CleanField`, 4 sites) now preserves `ARROW:extension:*` metadata** — it stripped
  ALL metadata, which would silently drop the variant tag on any rewrite. Seam caveat: id-mode projection
  under the seam resolves by physical NAME (field-id resolution needs the parquet footer the seam hides —
  exact for spec-written files). Regression: delta 39/39 + EW 141/168 green. **Third pass closed both
  loose ends** (`verify_delta_catalog_variant.test` now 87): (a) **mapped variant WORKS with zero code**
  (id-mode default: physical `col-<guid>` + field_id on the variant group in the parquet, RENAME COLUMN
  metadata-only, UPDATE of the variant value post-rename, kernel-reads — the FIELD_IDS-on-VARIANT concern
  didn't materialize); (b) **variant is TOP-LEVEL-only, enforced up front**: DuckDB's parquet writer
  REJECTS a non-root VARIANT ("requires a transform, but is not a root column" — upstream limitation), so
  `EnsureVariantWritable` rejects struct/list/map-nested variant at CREATE with that reason, and EW's
  `FromArrowType` throws on a variant list-element/map-entry marker instead of silently degrading it to
  `binary` (the degradation bug the probe found). Struct-nested variant maps fine at the EW schema layer
  (an external Spark table could carry it; reads may work) — only WRITES are gated.
  **Fourth pass — CODEC-PATH variant (tier 1, `VariantTransport`): the native-flag requirement for plain
  CREATE/INSERT/SELECT is GONE** (`verify_delta_catalog_variant.test` 102). Key discovery: EW's PARQUET
  layer already had complete, tested unshredded variant (the pinned **Apache.Arrow 23.0.0 nuget contains
  `VariantArray` + `VariantType` extension** — the release-content question is answered; EW's
  `ArrowToSchemaConverter` maps the extension → VARIANT-annotated group, `NestedLevelWriter` unwraps it,
  the reader wraps back via `ParquetReadOptions.ExtensionRegistry`, round-trip tests in
  `VariantArrayRoundTripTests` — EW's known-issues "VARIANT not supported" note is STALE). The missing
  piece was Delta-layer glue: **`EngineeredWood.DeltaLake.Table/VariantTransport`** converts the
  `fabricator.variant` blob transport ⇄ `VariantArray`/bare storage struct — write side in `WriteCoreAsync`'s
  codec branch (replacing the gate; splits each blob via the self-delimiting metadata header), read side in
  `ProcessFileBatchesAsync` after the renames (struct→blob+tag; a host-reader blob passes through;
  **shredded files [typed_value child] → clean error** — reassembly needs a variant engine). Bridge:
  `EnsureVariantWritable` keeps only the top-level-only + CDF rejections; `ThrowIfVariantCodecRead` removed.
  Cross-codec matrix: codec-written (unshredded) is read by EVERYONE (kernel-validated + native reader);
  native-written is SHREDDED by default → codec read = the clean shredded error (native/Spark/kernel read
  it fine). Codec REWRITES (UPDATE/CoW/OPTIMIZE) stay gated by the EW backstop on codec-only catalogs
  (their write sites aren't transformed; DV DELETE works). Tier 2/3 (shredded read / shredded write in EW)
  deferred — blueprints: DuckDB in-tree + **the pinned Apache.Arrow.Scalars ships the FULL variant toolkit**
  (`VariantBuilder`/`VariantValueWriter`/`VariantMetadataBuilder` + readers — tier 2's engine exists).
  **Codec form VALIDATED LIVE on Fabric Spark 4.1 (fifth pass)** after one fix: EW emitted the parquet
  `VariantType` annotation as an EMPTY thrift struct — **Spark requires `specification_version` (= 1)**
  (generic `FAILED_READ_FILE`; kernel/DuckDB tolerate the empty form; DuckDB's writer sets it too). Isolated
  via an A/B/C matrix on OneLake (codec+variant failed, codec+rowTracking-no-variant read fine → the
  annotation, not encodings/row-id). With the version byte written (`MetadataEncoder` case 16), Spark reads
  BOTH codec-written OneLake variant tables (object/null/array exact, default DV+rowTracking config
  included). Fix recorded in EW doc/upstream-candidates.md slice 1.
  **Sixth pass — FULL SHREDDING for the EW codec (tiers 2+3, `verify_delta_catalog_variant.test` 133).**
  The entire engine came from **`Apache.Arrow.Operations` 23.0.0 (published nuget, now referenced by EW
  DeltaLake.Table)**: `ShredSchemaInferer` (per-file schema from the column's values), `VariantShredder`
  (rows → typed+residual with a SHARED metadata dictionary so residual field-name refs stay valid),
  `ShreddedVariantArrayBuilder` (the shredded Arrow storage), and
  `VariantArrayShreddingExtensions.GetLogicalVariantValue` (per-row reassembly of ANY spec layout).
  `VariantTransport` now: WRITE = parse blobs → infer → shred when a schema applies (uniform
  objects/primitives/arrays; mixed stays unshredded; **SQL-null rows re-applied as storage VALIDITY** after
  the builder — distinct from variant JSON null); READ = `typed_value` present (or no `value` column) →
  `GetLogicalVariantValue` + `VariantBuilder.Encode` per row (passthrough concat kept for unshredded).
  **Cross-validated: DuckDB native, delta-kernel AND raw `read_parquet` all decode the codec-SHREDDED file
  exactly** (incl. the residual-merge row with an extra field), and the codec reads DuckDB-native-shredded
  tables — the cross-codec matrix is now FULL (both write both forms' semantics, both read everything).
  **Spark VALIDATED LIVE on the codec-SHREDDED form** (`lake.dbo.fabricator_varshred`, sparkprobe
  `variantshred`): all rows exact via `to_json` incl. the residual merge; `variant_get` on a SHREDDED
  field works (Spark exploits the typed columns); and the SQL NULL round-trips as TRUE SQL NULL
  (`WHERE v IS NULL` matches — the storage-validity null; NOTE this is BETTER than the unshredded
  DuckDB-native-written form, whose SQL NULL reads as JSON null in Spark). The Operations builder's
  OPTIONAL-field-groups deviation (spec says REQUIRED) is tolerated by ALL strict readers
  (Spark/kernel/DuckDB) — arrow-dotnet upstream-mention material, not a blocker.
  **Fabric T-SQL endpoint verdicts (user-tested live 2026-07-06):** (a) **id-mode column mapping is
  UNSUPPORTED** — the table doesn't even LIST (`UnsupportedColumnMappingMode`); `name`/none are fine;
  our catalog DEFAULT is id → open decision: flip the default to 'name' for endpoint reach (name has the
  same metadata-only RENAME/DROP). (b) struct/list columns degrade GRACEFULLY (table reads, nested
  columns silently omitted — the endpoint projects scalars only). (c) **a VARIANT column errors the
  ENTIRE table at footer parse** ("Msg 15813 … Thrift LogicalType that is not recognized" — their parquet
  stack predates the VARIANT logical type; even scalar projections fail). Guidance: endpoint-reachable
  semi-structured data → JSON (VARCHAR) columns, NOT VARIANT; VARIANT = Spark/DuckDB/kernel pipelines.
- **NESTED STRUCT-field schema evolution — DONE (2026-07-07; additive alter kinds, NO ABI bump).**
  DuckDB's field DDL (`ALTER TABLE t ADD/DROP/RENAME COLUMN s.f ...` — `AddFieldInfo`/`RemoveFieldInfo`/
  `RenameFieldInfo` with a `column_path`, first-class in 1.5.4) now works on the Delta catalog as
  METADATA-ONLY commits. C++ `Alter` gained ADD_FIELD/REMOVE_FIELD/RENAME_FIELD cases → new
  `FABRICATOR_ALTER_ADD_FIELD/DROP_FIELD/RENAME_FIELD` kinds (additive enum values 9-11; paths cross as a
  JSON array of segments since names may contain dots; the new field rides the existing single-field
  Arrow stream). EW gained the nested analogs `AddFieldAsync`/`RenameFieldAsync`/`DropFieldAsync`
  (`TransformStructAt` schema rebuild at any depth; mapping assigns ids+physicalNames to an added field
  RECURSIVELY via the create-time `AssignColumnMapping` — struct/array/map-typed additions get ids on
  every descendant; the same helper FIXED a latent top-level `AddColumnAsync` bug that committed
  spec-violating metadata for struct-typed adds under mapping, kernel-validated; rename/drop
  require mapping, same rule as top-level; protocol upgrade fires for schema-driven features of the new
  type). **The crux: the read reconciliation is now RECURSIVE** — `BackfillMissingColumns` +
  `ReconcileColumn` rebuild a struct whose child set differs from the current schema (ADDed member -> a
  typed all-NULL child sized to the PHYSICAL child length [parent offset+len, the TakeRows convention],
  DROPped member removed, children recursed; parent validity/offset preserved) — shared by the reader,
  compaction and the rewrite paths, so DML on evolved tables just works. `MakeNullArray` extended
  (struct/list/Decimal256/Date64/Time32/Time64 + honest THROW on unknown types — it silently backfilled
  a StringArray before, a latent wrong-type corruption for non-primitive top-level adds).
  delta-kernel reads the evolved tables exactly (standard metadata commits).
  `test/verify_delta_catalog_nested_alter.test` (71 — two-level adds, mixed-vintage reads + predicates,
  rename/drop, DV DELETE + UPDATE on evolved tables, re-attach durability, unmapped guards, struct-typed
  add on plain AND mapped incl. top-level, + native_read over evolved mixed-vintage tables — now 100); delta suite green;
  EW 147+168. SQL Server/DAX reject the new kinds cleanly. **The native_read PRESENCE PROBE (second pass,
  same day) lifted the evolution limitations — and fixed the PRE-EXISTING top-level one:** `native_read`
  of a file predating ANY added column/member failed loudly in BOTH mapping modes (top-level: the alias/
  projection referenced a column absent from the old file — broken since slice 1, just never tested;
  nested: the struct rebuild referenced the new member). Now `ResolveFileMapping` ALWAYS footer-probes
  each file's `parquet_schema` (`ProbeFileNodes` — DFS path reconstruction via the num_children stack:
  node PATHS [PathSep=-joined, dot-safe], per-node field ids, direct-children map; the id-mode
  fid probe is subsumed; the footer bytes are cache-warm for the subsequent read_parquet). `FileSql` is
  now presence-aware per column: absent top-level -> `CAST(NULL AS <type>) AS "c"`; a struct whose
  CURRENT member tree differs from the file (`StructShapeDiffers`: mapped rename, ADDed member [absent
  by fid OR stored path], DROPped member [extra file children], recursive) -> the struct_pack REBUILD
  with `CAST(NULL AS ...)` for absent members; `TypeText(DeltaDataType)` renders the DuckDB cast targets
  (timestamp->TIMESTAMPTZ, timestamp_ntz->TIMESTAMP, decimal(p,s) passthrough, STRUCT(..) recursive
  with LOGICAL member names, arrays/maps; variant -> loud throw). This ALSO fixes the advertised-schema
  staleness (ProbeSchema's LIMIT-0 probe file could be an OLD vintage — its FileSql now emits the
  current shape). Cost: one footer-only host query per file on every native scan (was id-mode-only).
  `verify_delta_catalog_nested_alter.test` now 91 (native_read evolved sections, both modes).
- **Delta write-side NOT NULL enforcement — DONE (2026-07-07; C#-only, no ABI).** Found by adapting
  duckdb-delta's `non_nullable` test: our Delta INSERT happily wrote a NULL into a column whose Delta schema
  declared `nullable:false` (a spec violation — writers MUST enforce; Spark trusts non-nullable schemas on
  read). New Bridge `DeltaNullability` (driven by the table's authoritative Delta schema, active only when it
  carries a constraint): per-batch validation on the **collect path** (`DeltaWriter.Write`, Append — before any
  file is written) + a **lazy validating stream wrapper** on the streaming path (`TryWriteStreaming`, wraps
  BEFORE the physical rename; fallback `return null` leaves the stream unconsumed) + **UPDATE SET** validation
  (`ExecuteUpdate` — scalar + the ReadScalarDeep struct dictionary, recursive; missing key = implicit NULL).
  Covers NESTED constraints (struct fields incl. deep nesting, list `containsNull=false`, map
  `valueContainsNull=false` — parent-validity-masked per row, children indexed at `Data.Offset + i` per the
  TakeRows convention) — external Spark tables declare these even though DuckDB DDL can't. Errors match DuckDB
  wording: `NOT NULL constraint failed: <table>.<path>`. Overwrite/REPLACE/CTAS adopt the input schema →
  deliberately not validated (drop+recreate semantics). Partial-column INSERT omitting a non-nullable column =
  implicit NULL → error. **Adopted from duckdb-delta** (test survey 2026-07-07): the
  `null_constraints_structs` fixture (`test/fixtures/` — Spark-declared nested non-nullables; their
  `null_constraints_lists` fixture was dropped — its commits are pretty-printed multi-line JSON, spec says
  NDJSON, EW rightly rejects it), the issue-297 class (all-NULL column stats + prune safety), the issue-303
  class (partition equality / single-value IN must not over-prune), and **explicit-transaction semantics
  PINNED as a documented divergence**: our Delta writes commit PER STATEMENT — multiple INSERTs in a BEGIN are
  separate Delta commits and ROLLBACK does NOT undo them (duckdb-delta buffers appends until COMMIT; a
  per-transaction append buffer is a possible future). `test/verify_delta_catalog_constraints.test` (50).
  Still open from the survey: a DAT conformance test (the delta-incubator Delta Acceptance Testing corpus via
  `require-env FABRICATOR_DAT_PATH` — validates default/native_read/deltars readers against golden tables incl.
  Spark checkpoints).
- **Delta IDENTITY columns — DONE (2026-07-06)**: the v53 generated-column marker (`id BIGINT AS (0)`) now
  works on the Delta provider (`test/verify_delta_catalog_identity.test`, 38; kernel-reads). The heavy lifting
  ALREADY EXISTED in engineered-wood (`IdentityColumn` config/metadata keys + `IdentityColumnWriter.ProcessBatch`
  per-batch generation + same-commit `delta.identity.highWaterMark` update in `WriteCoreAsync`;
  `SupportsExternalDataFileCommit=false` for identity tables → the streaming COPY path falls back to collect,
  which under `native_write` STILL emits native parquet via the `IDataFileWriter` seam) — only two pieces were
  missing: (1) EW `CreateAsync` now declares the **`identityColumns` writer feature** (writer-only, v7 list)
  when the schema carries `delta.identity.*` metadata; (2) Bridge `DeltaCatalog.CreateTable` attaches
  `IdentityColumn.CreateMetadata(1,1,false)` (GENERATED ALWAYS) to the marked fields (previously ignored;
  kept nullable=true DuckDB-side — the INSERT stream carries NULLs that ProcessBatch replaces). Values
  continue across statements + re-attach (hwm in schema metadata); an explicit NULL is engine-assigned.
  **OCC retry is SAFE for identity appends** (better than Spark, which rejects concurrent identity txns):
  the retry reopens at the new version and ProcessBatch REGENERATES from the fresh snapshot's high-water
  mark (input batches unmutated) — no baked-values problem, no special-casing needed.
- **DAX / ADOMD 2nd provider** (the "one binary, many providers" goal) — **design + slices:
  [docs/dax-provider.md](docs/dax-provider.md)**. **Slice 1 DONE + validated against a live local Power BI
  Desktop instance**: new project `Fabricator.AnalysisServices` (`DaxBackend : IBackend` provider `"dax"`,
  aliases `adomd`/`powerbi`/`ssas`/`fabric`; `DaxCatalog : IBackendCatalog`; `PowerBiDesktop` port detection).
  `ATTACH 'pbidesktop://' AS pbi (TYPE fabricator, PROVIDER 'dax')` auto-detects the local msmdsrv port
  (Windows-only, newest workspace's `msmdsrv.port.txt`) → AdomdConnection; `GetMetadata(Schemas)` = model
  name(s) from `$SYSTEM.TMSCHEMA_MODEL` so the model shows as a DuckDB schema. Other targets pass through as
  an ADOMD connstr (SSAS/Fabric/AAS). **No ABI/C++ change — pure C# provider** reusing the catalog/scan/
  function machinery. `BackendRegistry` default is now `Fabricator.SqlServer,Fabricator.AnalysisServices` (missing
  assembly skipped; SqlServer stays default → existing ATTACHes unchanged); `publish-managed.ps1` publishes
  both into one `fabricator/` dir (Bridge + SqlClient + `Microsoft.AnalysisServices.AdomdClient` 19.96.1 — the
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
  **FABRICATOR RENAME — LIVE-VALIDATED (2026-07-15):** the rename holds through the real tooling, not just the
static unittest. **dbt** (`dbt_mssql_test`, gitignored — loads the rebuilt loadable into the official duckdb
1.5.4 wheel): `box` target 10 models green (CTAS/incremental/index-post-hooks via `fabricator_exec`, threads=4);
`minio` target s3 Delta green (`fabricator_delta_scan`). **Fabric notebook** (`scratchpad/fabricnb`, gitignored):
25 probe steps pass — `load extension`, delta local/OneLake-fuse/create-read-txn/abfss-ambient, DAX ambient, and
every SQL-auth form (bare/authentication-default ambient + database-audience/lakehouse/warehouse token secrets);
the only 2 fails are documented-expected (pbi-audience token → 18456 reject; static-azure-secret abfss → the
pinned single-audience gap, ambient works). TWO harness bugs found+fixed from the ATTACH-vs-CREATE-SECRET
ambiguity (harnesses weren't in the tracked-code rename scope): probe `CREATE SECRET (TYPE fabricator)` →
`TYPE mssql`; dbt `ext_delta.sql` s3 bucket `s3://arrownet` → `s3://fabricator` (the compose + tracked s3 tests
use the `fabricator` bucket — `docker-compose.yml` was renamed too, so the MinIO bucket is `fabricator`).

`SQLNULL`-typed named param as `LogicalType::ANY` (`GetOrCreateTableFunction`) so DuckDB passes the literal
  UNCAST, and the shared table-bind marshaling keeps the value's **runtime** type for a `SQLNULL`-declared
  param (`FabricatorTableFunctionBind`) so a `STRUCT` marshals as a real Arrow struct. The guard is
  `SQLNULL`-only → every concrete-typed function is unaffected (full SQL fn suite green). Validated
  numeric/string/filter params, struct + JSON. No ABI change — reuses the proc named-param marshaling + v29
  table session), **`daxevaltable(<input>, expression
  := …)` in-out** (slice 5 — injects the input table as a DAX `DATATABLE` named `_input`, evaluates once,
  `DaxEvalTableBinding`/`DaxDataTable`; this required wiring **cost args (named params) through the shared
  exchange** — `GetOrCreateCustomInOutFunction` declares named params via a tolerant `FetchFunctionParamSchema`
  (empty for cf_tag → unchanged) + `FabricatorExchangeBind` marshals supplied named params into `inout_bind`
  args, else nullptr (`_each` unchanged); no ABI bump. Whole-table op, but the exchange has no emit-at-end
  hook [finalize drain discards trailing output] — **this single-chunk cap is now LIFTED: `daxevaltable` is a
  [collector](docs/inout-collector-mode.md)** (see below), so an arbitrarily large injected table works
  (validated live to 5000 rows). **The collector table-in-out (pipeline breaker) is BUILT + verified**: a
  second in-out execution shape (a Sink+Source: collect all input, emit at input-EOF) that coexists with the
  streaming exchange, picked by a new additive `kind='collector'`; reuses the v28
  `inout_bind`/`inout_exchange_open` ABI as-is (no bump). C# `IArrowCollectorTableFunction`/
  `IArrowCollectorBinding` (+ `StaticCollectorFunction` base, the `CollectorInOutBinding` adapter); C++
  `FabricatorCollector*` (in-out `Execute` buffers input into an `ArrowProducer` on the refcounted holder; the
  injected `FabricatorCollectorPhysical` Sink+Source opens the exchange at Finalize and **streams** the C# output
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
  (`fabricator_query`/`_exec`, catalog-type `"fabricator"`) + `BackendRegistry` multi-provider polish are due.
- **Multi-edition support** (Synapse / Fabric Warehouse / Lakehouse SQL endpoint) — **design:
  [docs/warehouse-support.md](docs/warehouse-support.md)**. **Slices 1–4 DONE + validated end-to-end against
  a real Fabric Warehouse** (edition 11, `BIN2_UTF8`): (1) `ServerProfile` (`ServerProfile.cs`) detected
  lazily on first connection via a **non-MARS probe** (so Fabric/Synapse, which reject a MARS connection, are
  classified before the MARS decision); **MARS gated on `profile.SupportsMars`** (the connection only works on
  Fabric because of this); (2) `fabricator_server_info(catalog)` diagnostic (`test/verify_server_profile.test`);
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
  scan `bind_data.string_order_pushable` → `fabricator_optimizer` gate `is_string && !string_order_pushable`;
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
  **`FABRICATOR_PLUGIN_DIR`** folder is discovered at load (`BackendRegistry.ScanPluginDirectories`), its
  `IBackend`(s) registered + global functions surfaced as bare `fn(...)` with NO ATTACH — no ABI/C++ change (the
  scan runs in `Discover()` before the `list_global_functions` union). Demo `Fabricator.SamplePlugin`'s
  `plug_greet` (`test/verify_plugin.test`). **Key finding: plugins load into the BRIDGE's ALC**
  (`AssemblyLoadContext.GetLoadContext(typeof(BackendRegistry).Assembly)`), NOT `Default` — hostfxr loads the
  bridge into a non-default context, so loading into Default bound the plugin to a separate `Fabricator.Bridge`
  copy (different, non-assignable `IBackend` → 0 backends). The loader skips host-context-loaded assemblies (the
  shared set) + a `Resolving` hook probes plugin dirs for private deps. **Plugins must align their full
  dependency closure with the host (Apache.Arrow always)** — no version isolation without ALC. **The contract
  assembly `Fabricator.Abstractions` is extracted** (the `I*Function`/`IBackend`/`IBoundTable`/`IAggregateSession`
  interfaces + `ProviderSetting`/`SecretField`/`TableFunctionScan`/`ScanSpec`/`FilterNode`, kept in the
  `Fabricator.Bridge` namespace — assembly split only, zero source churn; Bridge references it, the
  ABI/marshaling/`BackendRegistry`/Static-bases/adapters stay in Bridge). `Fabricator.SamplePlugin` references
  **Abstractions only** (+ Apache.Arrow) — Bridge-independent. Per-plugin `AssemblyLoadContext` isolation (for
  conflicting deps) is a deferred, non-breaking loader-internal upgrade. **Crux for that day:
  `Apache.Arrow`(+`.C`) MUST be SHARED (default context), never isolated** — every cross-boundary call traffics
  Arrow types, and cross-ALC types aren't assignable, so all plugins pin the bridge's Arrow version (isolation
  frees their OTHER deps only). The one fix over the textbook sketch: the `PluginLoadContext.Load` must return
  null for an explicit **shared-name allowlist** (`Fabricator.Abstractions` + `Apache.Arrow`/`.C`) BEFORE the
  resolver, else `AssemblyDependencyResolver` loads an isolated Arrow copy and breaks everything. Clean shape:
  extract a thin shared **`Fabricator.Abstractions`** (interfaces + Arrow-typed contracts) + non-collectible
  per-plugin ALCs, additive beside the default-context first-party providers (which gain nothing from isolation).
  Adopt isolation only when a real dependency conflict / third-party plugin lands.

## Implementation status (current)

**Phases 1–2 complete + streaming bulk write; verified against real SQL Server on DuckDB v1.5.4.**

Implemented and verified:
- **ATTACH + catalog**: schemas/tables/views, three-part naming, cross-catalog joins; `schema_filter`/
  `table_filter` (case-insensitive regex); ATTACH-time connection validation (no orphan catalog on
  failure); `mssql://` URI; `CREATE SECRET (TYPE mssql, …)` incl. Azure Entra/Fabric auth.
- **Read path** fully in C# behind `get_metadata`/`scan_table` ABI calls — **C++ has zero T-SQL**.
- **Pushdown**: projection (by-name), filter (best-effort via `pushdown_complex_filter`, never erases →
  DuckDB always re-applies; superset-safe shapes only), bare `LIMIT` (`TOP n`), `ORDER BY`+`LIMIT`
  (TopN, gated: NULL-order compatible, no pushed filter, and **string keys only under a binary database
  collation** — `ArrowStreamBindData::string_order_pushable`, set at scan bind from
  `FabricatorCatalog::StringOrderPushable()`, which `LoadCatalog` caches via `FetchBinaryCollation` reading
  the `FABRICATOR_META_SERVER_INFO` profile; binary `_BIN/_BIN2` collation sorts bytewise == DuckDB. No ABI.
  `test/verify_collation_pushdown.test`).
- **Statistics → optimizer**: cardinality (row count from `sys.dm_db_partition_stats`) + per-column NDV
  (leading-column histogram). **min/max deliberately NOT reported** (DuckDB prunes filters on min/max →
  stale SQL Server stats could drop rows; NDV is costing-only so stale is safe).
- **rowid** from PK / smallest unique index (scalar + compound STRUCT) → enables UPDATE/DELETE. **An IDENTITY
  column is also usable as the rowid** (engine-generated, effectively unique) so UPDATE/DELETE work on a table
  with NO PK/UNIQUE at all — `RowIdSql` composes an `IF EXISTS(...) <a> ELSE <b>` with the precedence flipped by
  engine: **Fabric/Synapse warehouse prefers the IDENTITY column** (their PK/UNIQUE are NON-ENFORCED hints =
  weak uniqueness) — `IF is_identity → identity ELSE PK/unique-index`; **box / Azure SQL prefer PK/unique** (their
  PKs are enforced/intended) and fall back to the IDENTITY column only when the table has no key constraint —
  `IF has_pk_or_unique → PK/unique-index ELSE identity`. Both validated live (identity-only table, no PK →
  UPDATE + DELETE via the identity rowid, on box AND Fabric Warehouse). Falls back to no rowid when neither
  exists (as before).
- **Time travel** (`FROM cat.t AT (TIMESTAMP => ts)`) → SQL Server temporal tables `FOR SYSTEM_TIME AS OF`
  (`eeae2e2`). The AT clause is a **bind-time, per-table-reference constant** (not per-scan pushdown), so it
  flows through the binding: `FabricatorCatalog::SupportsTimeTravel()→true` (else the binder rejects it with
  "Catalog type does not support time travel" before the scan), `FabricatorTableEntry::GetScanFunction(EntryLookupInfo)`
  reads `lookup_info.GetAtClause()` {unit,value} onto `ArrowStreamBindData` (the basic + lookup overloads share
  `BuildScanFunction`), `BuildScanSpec` folds it into the existing `spec_json` (`"at":{unit,value}` — **no new
  ABI**), and C# `ScanFromSource` emits the timestamp travel per engine profile: **box / Azure SQL** →
  `FOR SYSTEM_TIME AS OF @__at` (a datetime2 param; requires a system-versioned temporal table); **Fabric
  Warehouse / Synapse** (`profile.IsWarehouse`) → the statement-level hint `OPTION (FOR TIMESTAMP AS OF
  '<literal>')` appended after WHERE/ORDER BY, which works on ANY table (no temporal setup). The Fabric literal
  is a fixed-format `yyyy-MM-ddTHH:mm:ss.fff` (OPTION takes no parameter, so it's inlined — no injection, it's a
  reformatted datetime) **truncated to milliseconds** (Fabric rejects ≥4 fractional digits, error 22440; UTC
  only). Each catalog table scan is its own server query, so the query-level OPTION hint is per-table-correct
  even across a join/union of different `AT` timestamps. `AT (VERSION => …)` (an Iceberg/Delta snapshot-id
  notion) has no SQL Server equivalent → a clean "not supported" error (no silent current-data result).
  Verified: `test/verify_time_travel.test` (14 — box temporal, current/future/past + a `dm_exec_query_stats`
  `FOR SYSTEM_TIME AS OF` proof + the VERSION error); Fabric Warehouse `OPTION (FOR TIMESTAMP AS OF)` validated
  **live** (point-in-time correct — a post-timestamp INSERT is invisible AS OF the earlier instant — and the
  ≥4-digit truncation confirmed, no 22440).
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
    `MetaTransaction::Get(context).global_transaction_id` (`FabricatorTransaction::txn_id_` for lifecycle;
    `arrow_ingest` `ArrowStreamInitGlobal` centrally for all scans/read-your-writes; the DDL/DML/exchange/
    `FetchTableColumns`/`fabricator_exec` callsites via `catalog/fabricator_txn_util.hpp`'s `FabricatorSetActiveTxn`).
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
    per-connection schema creation — so all of dbt's cursors see the catalog). Uses `TYPE mssql` (the loadable
    registers that storage-extension name; `fabricator` is a shell-only alias) + `PROVIDER 'delta'`. **CRITICAL —
    point it at an EMPTY lakehouse** (validated against the flat `LH_no_schema`, schema `main`): dbt runs
    `information_schema.tables` before building, which scans the **WHOLE `mssql` catalog** (the
    `WHERE table_schema=…` filters AFTER), and our catalog **materializes every table during enumeration**
    (`FetchTableColumns` → a `_delta_log` read per table over OneLake). Against the populated `LH` (10 tables incl.
    a 10M-row one) that effectively HANGS — even when the target schema is empty, because the scan still touches
    every other table. Against the empty `LH_no_schema` a single-model build is ~11s and **`dbt run --threads 4`
    is PASS=4/4** (4 concurrent CTAS → 4 separate `Tables/<model>` Delta tables, ~19s — validates the parallel
    OneLake bulk-write path, same as box/fabric). (**Lazy table-enumeration is INFEASIBLE** — investigated
    2026-07: DuckDB's `duckdb_tables`/`information_schema.tables` reads `GetColumns().LogicalColumnCount()` +
    the full `GetInfo()` CREATE SQL + `HasPrimaryKey()` from EVERY entry (`duckdb_tables.cpp:139/147/153`), and
    `duckdb_columns`/`information_schema.columns` share the same `SchemaEntry::Scan(TABLE_ENTRY)` path — so a
    full catalog scan inherently materializes every table's columns; there is no names-only enumeration API and
    a `TableCatalogEntry` requires its columns. Targeted access (`db.schema.t`) is already lazy/fast per-table.
    The realistic mitigation for the OneLake slowness is *cheaper* materialization: fetch a table's columns from
    the **OneLake Unity Catalog single-table GET** (`…/unity-catalog/tables/<full_name>` returns
    columns[name,type_name,nullable] — proven) instead of a heavy delta-rs `_delta_log` open, turning
    enumeration from N log-replays into N light REST calls. Not built — the bind schema would have to match the
    delta-rs read schema across all types.) Per-target
    schema via `+schema: "{{ target.schema }}"` (box/fabric `dbo`, lakehouse `main`). **The loadable extension
    must be rebuilt on an
    ABI bump** (`cmake --build … --target fabricator_loadable_extension`) — dbt loads the loadable, not the
    static `unittest`/`duckdb.exe`, so a stale loadable vs a freshly-published bridge throws
    `Bootstrap.Initialize returned 2` (ABI mismatch).
  - **dbt pre/post hooks — behavior + limitations: [docs/dbt-hooks.md](docs/dbt-hooks.md)** (validated box +
    Fabric). Highlights: an **in-transaction post-hook error rolls back the model's CREATE on BOTH box AND
    Fabric** (Fabric Warehouse supports transactional DDL rollback — unlike Snowflake). SQL-Server-specific
    DDL in a hook (index/PK/UNIQUE) must call `fabricator_exec`. A **default in-txn** post-hook touching the
    model via `fabricator_exec` now runs **atomically with the model** (ABI v36 join-only: the exec runs on the
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
    `sys.dm_os_waiting_tasks`). **Fix (C++-only): `FabricatorSchemaEntry::Alter` re-fetches the columns
    EAGERLY on the model's OWN connection** (which owns the Sch-M lock → read-your-writes, no block) and
    caches them, so the later bind finds the entry cached and never issues the blocking pooled re-fetch.
    Since that cached entry reflects the uncommitted schema, **`RollbackTransaction` calls
    `FabricatorCatalog::InvalidateAllEntries()`** (drops materialized entries, keeps name lists for lazy
    re-fetch) so a rolled-back ALTER leaves no stale schema (verified). Same family as the post-hook
    join-only fix — keep in-transaction work on the transaction's own connection.
- **Functions**: `fabricator_query` (raw scan), `fabricator_exec` (raw exec) — both accept a connstr, a
  secret name, OR an attached-catalog name; `fabricator_refresh_cache`/`fabricator_invalidate_cache` (+ `_net_`
  aliases, arities 1/2/3); `fabricator_version()`; `fabricator_managed_dir()` / `fabricator_test_scan()` /
  `fabricator_server_info(catalog)` (diag — the latter surfaces the detected `ServerProfile`).
- **Cache invalidation after DDL via `fabricator_exec`**: DDL detection in C# (`SqlDdl.MayChangeSchema`),
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
  serialized into a fixed, pointer-free state blob (`[uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP]]`, cap =
  1 KB) so DuckDB's external GROUP BY spills it; state crosses as an Arrow BLOB column (NULL row = fresh).

### Callable scalar UDFs (4b)
- **Discovery**: `FabricatorCatalog::LoadCatalog`/`RefreshCache` call `DiscoverFunctions` (reads
  `FABRICATOR_META_FUNCTIONS`, first 3 string cols) and `AddScalarFunction(name)` for every `kind=='scalar'`
  in a matched schema. Names cached in `FabricatorSchemaEntry::scalar_functions_`; entries materialized lazily.
- **Registration**: `FabricatorSchemaEntry::LookupEntry`/`Scan` now handle `CatalogType::SCALAR_FUNCTION_ENTRY`
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
  `kind=='table'` in a matched schema (`table_functions_`). `FabricatorSchemaEntry::LookupEntry`/`Scan` handle
  `CatalogType::TABLE_FUNCTION_ENTRY` → `GetOrCreateTableFunction` builds a `TableFunctionCatalogEntry` and
  caches it (stale-on-fetch self-heals, like the table/scalar paths).
- **Bind**: `table_function_bind_t` is a **raw fn pointer** (can't capture, unlike `scalar_function_t`), so
  the identity rides an `FabricatorTableFunctionInfo : TableFunctionInfo` on the `TableFunction`, read in the
  static bind via `input.info`. The bind (1) resolves the output schema via `get_function_output_schema`
  (zero-row → `PopulateReturnSchema`, so the TVF isn't executed just to bind), then (2) installs a capturing
  `StreamFactory` (which **is** `std::function`) that marshals the constant call args (`input.inputs`) into a
  1-row Arrow batch (`ArrowAppender`+`ArrowProducer`) and calls `execute_table`. Reuses `ArrowStreamScan`/
  `ArrowStreamInitGlobal`/`Local`.
- **Projection + filter pushdown (real, SQL-level)**: the TVF reuses the **catalog table scan's** pushdown
  machinery. `push_projection=true` on the bind_data + `projection_pushdown=true` + `pushdown_complex_filter
  = FabricatorComplexFilterPushdown` (extracted from `fabricator_table_entry.cpp` out of its anon namespace,
  declared in `fabricator_table_entry.hpp`, shared by both scans). The scan factory forwards the request's
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
  C++ `FabricatorTableFunctionBind` uses them; the `is_proc` **execute** branch is gone (`table_execute`
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
  static bind via an `is_proc` flag on `FabricatorTableFunctionInfo`. Proc branch: factory calls
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
- **Registration** (`fabricator_schema_entry.cpp`): `AddTableFunction(name,false)` also registers
  `inout_functions_["<name>_each"] = name`; `GetOrCreateTableFunction` resolves the alias via
  `GetOrCreateInOutFunction` → a single `{LogicalType::TABLE}` `TableFunction` (`in_out_function` only,
  `function_info.func` = the **base** TVF). A real same-named `_each` function wins (matched first). `Scan`
  lists the aliases so they're discoverable.
- **Operator** (all in the anon namespace of `fabricator_schema_entry.cpp`): `FabricatorInOutBind` (output
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
  ATTACH `isolation_level` option (per-catalog default, `FabricatorCatalog::isolation_level_`) overridable by
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
  (`GetOrCreateCustomInOutFunction` + `FabricatorCustomInOutBind`, reusing the 4g operator callbacks — no new
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
- **OperatorFinalize cleanup signal (4g-finalize)**: an `OptimizerExtension` (`RegisterFabricatorInOutFinalizer`,
  registered at load) wraps each in-out `LogicalGet` (identified by `function.in_out_function ==
  FabricatorInOutFunction`, RTTI-free) in a pass-through `LogicalExtensionOperator`; its `PhysicalOperator`
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
  read-your-writes + ROLLBACK) holds — verified by `verify_proc_inout`. The 4g push operator (`FabricatorInOut*`)
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
- **C++ operator** (`fabricator_schema_entry.cpp`, anon ns): `FabricatorExchange{Bind,InitGlobal,InitLocal,Function}`
  + `FabricatorExchangeGlobalState` (the gate `std::mutex`, the single input `slot`, `input_eof`, the output
  reader, `MaxThreads()=1`) + a host-side input stream whose get_next hands the gate-holder's slot to C#.
  `Execute` holds the gate across the chunk's HAVE_MORE_OUTPUT cycle (ownership in the per-thread local state),
  releases it on the sentinel/EOF **or on a thrown managed error** (RAII-style — never leaks). `ArrowStreamReader`
  gained **sentinel-aware** `Pull()`/`HasPending()`/`Drain()` (its `Read()` skips empty batches, so the sentinel
  needs explicit length inspection + <=STANDARD_VECTOR_SIZE slicing). **EOF is the injected `OperatorFinalize`**
  (`FabricatorExchangeFinalizePhysical`, parallel to the 4g one): once, sink-level, after all branches it sets
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
- **Session**: opened in the aggregate `bind` (a `FunctionData` = `FabricatorAggregateBindData` holding a
  refcounted `AggSessionHolder`; its destructor calls `agg_close`). `bind` runs once per bound plan; update/
  combine/finalize/destructor reach the session via `AggregateInputData.bind_data`. Identity (handle/schema/
  func) + the counter ride on `FabricatorAggregateFunctionInfo : AggregateFunctionInfo` (reachable from
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
- **C++** (`fabricator_schema_entry.cpp`, anon ns): `FabricatorAggregate{StateSize,Init,Bind,Update,SimpleUpdate,
  Combine,Finalize,Destroy}` static callbacks marshal `[id ++ inputs]` / `[target_id, source_id]` / `[id]`
  Arrow batches (`ArrowAppender` + `fabricator::Agg*`); finalize ingests the single result column via
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
  DuckDB's fixed, pointer-free state blob (`[uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP=1 KB]]`) so DuckDB's
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
  `fabricator_query` the two surface as optional NAMED params (the `daxeval` pattern); a custom
  `IArrowTableFunction` could return `IAsyncEnumerable<IAsyncEnumerable<RecordBatch>>` (outer = partitions).
- **`function_filter` ATTACH option — DONE (2026-07-15).** `ATTACH … (TYPE fabricator, function_filter
  '^ff_keep$')` — an icase regex on the routine NAME gates which discovered scalar UDFs / TVFs / procs register
  (symmetric with `table_filter`, which is table-only; `schema_filter` still gates functions by schema C++-side).
  Applied in C# `SqlServerCatalog.FilteredFunctions` (a type-preserving row filter over the Functions discovery
  stream — the diagnostic `fabricator_functions` keeps `param_count` as INT; `DiscoverFunctions` C++-side only
  reads the 3 string cols so it's tolerant). Parsed like schema_filter/table_filter (v37 ATTACH-options JSON, no
  ABI change). `test/verify_function_filter.test` (15 — exact-anchor keeps one/drops the other, substring matches
  both, cleanup); function suites unregressed (functions 13 / scalar 26 / procs 24 / table 33 / custom 89 /
  catalog_filter 7).
- **Open design items (refresh)** — deliberated, not yet built:
  - **Scoped refresh — DONE (2026-07-15).** `fabricator_invalidate_cache(catalog, name_regex)` — a non-empty
    2nd arg is an icase name pattern: SCOPED invalidation of only the MATCHING objects (drop their materialized
    entries; an ALTER'd one re-fetches its fresh schema on next access, a DROPped one self-heals when the
    column re-fetch fails), leaving the rest of the cache warm — "refresh only what I touched via
    fabricator_exec". No regex (arity 1, or an empty/NULL 2nd arg) = full `RefreshCache` (whole catalog within
    its filtered enumeration baseline). C++ `FabricatorCatalog::InvalidateMatching(pattern)` compiles the
    std::regex (bad pattern → clean error) + per-schema `FabricatorSchemaEntry::InvalidateMatching(pred)` evicts
    the matching *_entries_ caches (keeps the name lists). No ABI/C# change. `test/verify_invalidate_scoped.test`
    (18). The legacy `(catalog, schema, table)` arity still works (3rd arg ignored). The `fabricator_exec`
    auto-refresh (gated by `mssql_exec_invalidate_cache`) stays a **full** `RefreshCache` — the C# DDL detector
    returns only a bool `schema_may_change` (no object name crosses the ABI), so it can't auto-scope; do a
    manual `fabricator_invalidate_cache(cat, '<regex>')` for scoped, or full otherwise.
  - **ATTACH object filter is ENUMERATION-ONLY, not a cage — DONE (2026-07-15).** `schema_filter`/`table_filter`/
    `function_filter` make ATTACH fast on a huge catalog by discovering a SUBSET — but they bound ENUMERATION
    (SHOW TABLES / `duckdb_tables` / full refresh), NOT targeted-by-name access. `FabricatorSchemaEntry::
    GetOrCreateEntry` now, on a miss WHEN an object filter is active (`FabricatorCatalog::HasObjectFilter`, set
    at ATTACH from the presence of a *_filter option), FETCHES the table by name (the miss may be a real table
    the filter merely excluded from enumeration); the entry is cached in `entries_` but NOT added to
    `table_types_`, so enumeration stays filtered while `db.schema.OutOfFilterTable` resolves. Without a filter,
    the discovery list is authoritative so a miss is genuinely absent (no wasted round-trip — CREATE-new is
    unaffected). This means a restrictive perf-filter no longer CAGES the catalog: you can always reach an
    object by name (and a scoped `invalidate_cache` reconciles it), the filter is just a discovery speed-up. The
    filter was never a security boundary anyway (`fabricator_query` bypasses the catalog). `verify_invalidate_scoped`
    covers it; `verify_catalog_filter` (enumeration counts) unchanged.

## C ABI contract (`src/include/fabricator/abi.h`)

- The managed `Bootstrap.Initialize` fills an `FabricatorVTable` of C function pointers; tabular results
  flow through caller-allocated `ArrowArrayStream`; errors = status code + owned UTF-8 string freed via
  `free_error`. C# error messages prepend the provider error number when available (`FormatError`
  duck-types an `int Number` property → e.g. `"2627: …"`; provider-agnostic, no SqlClient ref in Bridge).
- **`COPY … TO '<path>/<table>' (FORMAT delta, …)` — path-targeted Delta write, NO ATTACH (2026-07-10,
  C++-only, no ABI).** A third registered copy function (`"delta"` beside `fabricator`/`bcp`; the official
  duckdb-delta extension registers NO copy function — its only write surface is INSERT into an attached
  table — so the name is free and the capability is ours alone): the target is a raw path (local / `s3://` /
  `onelake://` / abfss), split into `<root>/<table>`; the bind opens NOTHING — `CopyToInitGlobal` opens a
  **transient per-execution engineered-wood catalog** (`fabricator::OpenCatalog(root, "delta", options_json)`,
  flat layout, owned by the copy global state) and streams through the EXACT catalog-COPY bulk machinery, so
  the write disposition is the **`MODE` option — the Spark/delta-rs save-mode vocabulary**: `'overwrite'`
  (the default when no options given — create or fully replace, COPY-to-file intuition) | `'append'`
  (create-if-missing + append-if-exists — Spark `mode=append`; EW's append path OpenOrCreates commit-0) |
  `'error'`/`'errorifexists'` (Spark's default save mode — fail on an existing target) | `'ignore'` (silent
  no-op on existing) | `'error_if_not_exists'` (strict append — fail on a MISSING target instead of
  implicitly provisioning; the inverse of 'error', no Spark equivalent) | `'overwrite_partitions'` (dynamic
  partition overwrite — Spark has no mode name for it, it's `overwrite` + the `partitionOverwriteMode=dynamic`
  conf; with PARTITION_COLUMNS a MISSING target is created PARTITIONED and the first run is a plain append —
  idempotent first-run; without them a missing target is rejected UP FRONT provider-side, since the
  append-shaped implicit create would otherwise leave an empty unpartitioned commit-0 behind before the
  partitioned-target guard fired). **`PARTITION_COLUMNS` is allowed with EVERY mode and applies whenever the
  write actually CREATES the table** (explicit or implicit — the `createTable||replace` gate in `BulkInsert`
  was dropped; EW's `OpenOrCreateAsync(partitionColumns:)` applies them at creation only; a mismatched
  PARTITION_COLUMNS on APPEND is tolerated + ignored; NOTE under the default name-mapping the Hive dirs carry
  the PHYSICAL `col-<guid>` names per the Spark convention — `COLUMN_MAPPING 'none'` for logical-named dirs).
  **REPARTITION-on-overwrite (2026-07-10): `MODE 'overwrite'` + PARTITION_COLUMNS differing from an EXISTING
  table's partitioning CHANGES the partitioning** — the Delta protocol allows a new
  `metaData.partitionColumns` ONLY when every active file is removed in the same commit (readers interpret
  each `add.partitionValues` against the current partition schema), i.e. exactly a full overwrite (Spark:
  `overwriteSchema=true` + new `partitionBy`; there is NO `ALTER TABLE … PARTITIONED BY`). EW
  `WriteAsync(repartitionTo:)` → `WriteCoreAsync` splits by the NEW columns + folds the metaData swap into
  the overwrite commit (guarded: full overwrite only — a partition-scoped/dynamic overwrite would keep
  nonconforming files; coordinated with the identity-HWM metadata so one commit never carries two metaData
  actions); the streaming path falls back to collect for this shape (`TryWriteStreaming` returns null — no
  metaData-swap support there; checked BEFORE SetSchemaAsync so the fallback leaves no half-done commit).
  Absence of PARTITION_COLUMNS on overwrite = keep the current partitioning (no implicit departitioning);
  kernel-validated (delta_scan reads the repartitioned table). Previously this case SILENTLY kept the old
  partitioning — a real gap found by probing the protocol question. The dispositions (`error`/`ignore`/
  `error_if_not_exists`) are checked provider-side: the per-statement disposition rides the TRANSIENT
  catalog's options JSON (`copy_disposition` — sound because that catalog serves exactly one COPY;
  `DeltaCatalog.BulkInsert` probes `TableExists`; the ignore no-op returns without consuming the stream —
  BulkSession's finally drains). The legacy **`CREATE_TABLE`/`REPLACE`/`PARTITION_OVERWRITE` flags** (shared
  with `FORMAT mssql`) still work but CANNOT be mixed with `MODE`; `SCHEMA_MODE 'merge'|'overwrite'`
  composes with either spelling, plus **`PARTITION_COLUMNS 'a,b'`** (create-time Hive
  partitioning — deliberately NOT the generic `PARTITION_BY`, which DuckDB's planner intercepts for
  file-based copies) and **provider-option passthrough** (`NATIVE_WRITE` — defaults TRUE here, bounded-memory
  streaming; `DELETION_VECTORS`/`COLUMN_MAPPING` — e.g. `DELETION_VECTORS false, COLUMN_MAPPING 'none'` for a
  SQL-Server-/protocol-1.0-readable table; `ROW_TRACKING`/`MATERIALIZE_ROW_TRACKING`/`CHANGE_DATA_FEED`/
  `IN_COMMIT_TIMESTAMPS`/`COMPRESSION`/`ROW_GROUP_SIZE`/`BLOOM_FILTER_COLUMNS`). **The transaction crux:** the
  provider PARKS plain appends per (txn, table) and flushes at `commit_transaction` — a transient handle has
  no TransactionManager, so the COPY drives it itself: finalize does `SetActiveTxn(handle, txn_id)` +
  `fabricator::CommitTransaction(handle)` (no-op for create/replace, flushes the parked append as ONE commit) +
  `CloseCatalog`; the gstate destructor is the failure backstop (`RollbackTransaction` — discard-only, no
  opener needed — then close). ⇒ the COPY is its own atomic Delta commit and deliberately does NOT roll back
  with a surrounding DuckDB BEGIN (file-COPY semantics; the transient catalog's buffer is invisible to the
  user txn). Verified: `test/verify_delta_copy_format.test` (41 — create/overwrite/append-flush/replace/
  partitioned + dynamic partition overwrite/schema merge+overwrite/protocol-shape pins/ATTACH-reads-it-back/
  path validation), S3 section in `verify_delta_catalog_s3.test` (131 — COPY to `s3://fabricator/copyfmt` incl.
  partition overwrite + re-ATTACH), catalog-COPY suites unregressed (partition_overwrite 90 / overwrite_merge
  47 / native_write_streaming 29), **live OneLake** (`COPY … TO 'onelake://Test/LH.Lakehouse/Tables/dbo/…'
  (FORMAT delta)` → native streamed v1 + exact readback; an `onelake://` root doesn't match the
  `FabricLakehouse.IsOneLake` abfss check, so it rides the plain host-FS path with opener-resolved secrets —
  which is exactly right). Kernel reads the outputs (known quirk unchanged: kernel shows a MAPPED partitioned
  table's partition column as NULL; plain shape exact).
- **Current version: ABI v68** (v68 = **`generate_table_sql`** — ONE appended entry backing
  **SQL-GENERATING table functions**: `generate_table_sql(handle, schema, func, catalog_name, args,
  out_sql)` returns the SQL that REPLACES a function call at bind time (DuckDB's `bind_replace`, the
  `query_table()` mechanism). `handle == 0` = the global registry (schema/catalog_name empty), non-zero =
  the catalog's, with `catalog_name` = the DuckDB **ATTACH alias** (only the host knows it, so a
  catalog-bound generator can qualify references back into its own catalog). `args` = the 1-row constant-arg
  batch (positional ++ supplied named, by field name; nullable). **BIND-time only, possibly repeated**
  (EXPLAIN / DESCRIBE / a view re-bind) ⇒ generators must be deterministic + side-effect-free; NO data path.
  Same pass, NO extra entry: `get_function_param_schema` now carries POSITIONAL ++ NAMED parameters in one
  schema, the named ones tagged `fabricator.named="1"` in FIELD metadata (`FetchFunctionParamSchema`'s new
  optional `out_named` — the `fabricator.volatile` channel/shape). See the sqlgen bullet in "Next up".)
- **Prior: ABI v67** (v67 = **`options_json`** — `create_table` + `begin_bulk` each gained a
  nullable `const char *options_json` carrying the `CREATE TABLE [AS] ... WITH (key='value', ...)` clause
  as a flat JSON object of STRING values (`fabricator::TableOptionsArg` — constants only, one CAST level
  unwrapped so `true`/`false` arrive as postgres `'t'/'f'`; `SupportsCreateTable` now PERMITS `options`).
  The PROVIDER parses the keys it knows and REJECTS unknown ones — never silently ignored. Delta consumes
  three key kinds (see the WITH-options bullet in "Next up"); SQL Server/DAX/deltars reject any options
  (SQL Server's keys land with slice B). **Parser gotcha (load-bearing):** DuckDB's transformer LOWERCASES
  every WITH key (`transformer.cpp TransformTableOptions` — quoting does NOT help), so case-sensitive
  `delta.*` property keys are re-cased C#-side from a canonical well-known list (`DeltaWithOptions.
  CanonicalKeys`); arbitrary mixed-case custom keys must use `fabricator_delta_set_tblproperties`.)
- **Prior: ABI v66** (v66 = **host_query CANCELLATION** — `host_query` gained a nullable
  `void **out_interrupt` (a heap `shared_ptr<ClientContext>` to the query's FRESH context) + two appended
  host-service entries `host_query_interrupt` (thread-safe `Interrupt()`; no-op after the query ends — the
  shared_ptr keeps the context alive) / `host_query_interrupt_free`. Why: every `Host.Query` (native_write
  parquet writes + rewrites, `DeltaNativeReader` per-file `read_parquet`, `HostBatchFilter`, codec
  `IDataFileReader`) runs on a fresh connection INVISIBLE to the user query's Ctrl+C — a heavy first Fetch
  (the rewrite's anti-join build, a big COPY) was uncancellable. The C# `HostFs.Query` CENTRALIZES the fix:
  it wraps every result in an `InterruptibleQueryStream` (an `InterruptScope` on the AMBIENT opener whose
  token trips the interrupt; dispose order = registration [waits in-flight] → scope → inner stream →
  free-handle) — zero per-call-site changes. This closed the "native_write host_query rewrite" deferred
  item; Tier 4 (async/BLOCKED source) remains the only deferred rearchitecture. See docs/cancellation.md.)
- **Prior: ABI v65** (v65 = **`is_interrupted`** — a host→managed reverse callback on
  `FabricatorHostServices` reading the calling operator's `ClientContext::interrupted` (the atomic set by
  Ctrl+C via `Connection::Interrupt()` or a query timeout). The opener handle already IS a `ClientContext*`
  (the `fs_*` secret-resolution handle), so `HostIsInterrupted` is a one-line cast+read. **Cancellation
  Tier 1 — design + tiers: [docs/cancellation.md](docs/cancellation.md).** THE PROBLEM: C# CancellationTokens
  were dead-wired (~124 `default` sites; the only real CTS was BulkSession's internal consumer-exit); sync
  SqlClient/ADOMD ignore tokens; and the C++ extension never checked interruption — so a query parked inside a
  single long-blocking C# I/O call (a big OneLake/S3 read, a slow SQL scan, a hung socket) could only be
  cancelled AFTER that call returned (DuckDB cancels BETWEEN `get_next` calls — `pipeline_executor.cpp` throws
  `InterruptException` on `context.interrupted` — but a blocked `get_next` holds the pipeline). THE FIX (this
  slice): `is_interrupted` + a C# **`InterruptScope`** (a per-operation `CancellationTokenSource` + a
  `System.Threading.Timer` polling `is_interrupted(opener)` every 50 ms on a pool thread — NOT the blocked
  task thread — that trips the token on interrupt; `Dispose` waits for any in-flight callback so no poll
  outlives the `ClientContext`). Wired into the **engineered-wood streaming read paths**
  (`DeltaReader.Stream`/`StreamWithRowIds`/`StreamAt`/`StreamWithRowIdsAt` → their `*Impl` cores now take the
  opener, build an `InterruptScope`, and pass its token to `DeltaTable.OpenAsync`/`ReadAllAsync*` — EW already
  honors it, so a long OneLake/S3 batch read cancels between chunks). A never-tripped token is BYTE-NEUTRAL
  (full delta sweep + SQL fn suites green at v65). **Tier 2 SQL Server DONE (2a `4d33f68` + 2b `6e952f6`,
  C#-only):** the two long-running SQL windows now cancel. **2a — data scan:** `ExecuteQuery` uses
  `OpenAsync`/`ExecuteReaderAsync(token)` + `DbDataReaderArrowStream` fetches with `ReadAsync(token)` (async
  SqlClient honors the token natively — Microsoft's model, no `SqlCommand.Cancel()` trick; the stream owns the
  `InterruptScope`; gated to data scans, short metadata reads stay uncancelled). **2b — bulk (INSERT/CTAS/COPY):**
  `BulkSession` builds an `InterruptScope(opener)` + `token.Register`s its existing `Complete(abort)` teardown
  (fault the channel → `WriteToServer` stops + rolls back AND a backpressure-parked `push_batch` unblocks — no
  `WriteToServerAsync` needed); works for SQL bulk AND Delta streaming writes. **2c — SQL DML/exec DONE
  (`<pending-commit>`, C#-only):** `ExecuteNonQuery` (raw `fabricator_exec`), `ExecuteDelete`, `ExecuteUpdate` run
  their writes with `ExecuteNonQueryAsync(token)` under an `InterruptScope(AmbientOpener.Current)` (chunked
  DELETE/UPDATE loops share one scope) — a long rowid DELETE/UPDATE or slow `fabricator_exec` cancels.
  **Load-bearing constraint (verified):** `is_interrupted` derefs the opener as a `ClientContext*` and
  `AmbientOpener` is NEVER cleared, so polling is only safe where the opener is set FRESH right before the op —
  and EVERY write path does: scan (`arrow_ingest`), bulk (`fabricator_insert`), **the DELETE/UPDATE modify
  operator** (`Finalize` → `FabricatorSetActiveTxn` → `SetActiveOpener`, `fabricator_txn_util.hpp` — the earlier
  "modify doesn't set the opener" note was WRONG; it's inside that helper), and `fabricator_exec`
  (`fabricator_extension.cpp:501`). So 2a/2b/2c capture the current statement's live `&context`. Paths WITHOUT a
  preceding `SetActiveOpener` (metadata reads, DDL via CreateTable/AlterTable) are left uncancelled (short anyway).
  **Delta EW DML/maintenance DONE (`<pending-commit>`, C#-only):** the EW write cores `DeleteByRowIds`/
  `DeleteByRowIdsViaVectors`/`UpdateByRowIds`/`Optimize`/`Vacuum` each build an `InterruptScope(opener, ct)`
  internally (like the Tier 1 read paths) and pass the token to `OpenAsync` + the EW op — a slow OneLake/S3
  copy-on-write/DV rewrite, OPTIMIZE, or VACUUM cancels (codec path fully; native_write's parquet rewrite runs on
  a separate host_query connection, uncancelled — Tier 4). AUTOCOMMIT only. **Buffered explicit-txn path DONE
  (`<pending-commit>`, C#-only):** the COMMIT-phase flushes (`FlushDmlTransaction` — incl. breaking OUT of the
  OCC/rebase retry loop via `ThrowIfCancellationRequested`, token to `WriteDataFilesAsync`/`Compute…`/`Rebase…`/
  `CheckLogicalRebaseAsync`/`CommitDataFilesAsync`/`OpenAsync`; `FlushCreateTransaction`; `FlushDeferredFiles`) +
  the per-statement buffering (`WriteCdcFiles`, `TryEagerWriteBatches`, buffered-UPDATE `ReadRowsByRowIds`
  read-back) each build an `InterruptScope(opener)` and pass the token to their EW calls. Safe: a cancel before
  the atomic `_delta_log` commit lands = invisible orphan files (VACUUM reclaims) = rollback; a cancel isn't a
  `DeltaConflictException` so it exits the retry loop, not swallowed as a conflict. transactions 941 / txn_version
  51 / row_tracking_virtual 299 green. **2d — SQL command_timeout DONE (`<pending-commit>`, C#-only):** `mssql_command_timeout` setting
  (seconds; **default 0 = infinite**) + a per-catalog `command_timeout` ATTACH option, resolved `SET ?? ATTACH
  ?? 0` (`ResolveCommandTimeout`) → `SqlCommand.CommandTimeout` on scans/DML + `BulkCopyTimeout`. NATIVE,
  server-enforced, PER-ROUND-TRIP (aborts a hung round-trip, doesn't kill a long-but-progressing scan — which a
  client `CancelAfter` on the whole scope would); complements the interrupt token (token = Ctrl+C cancel; timeout
  = non-interactive/hung safety net). Default 0 removes the prior implicit SqlClient 30 s (a warehouse-query
  footgun). `test/verify_command_timeout.test` (6). The `io_timeout` for OneLake was DROPPED (httpfs/duckdb-azure
  have their own HTTP timeouts; a coarse whole-scope CancelAfter would mis-fire on long streaming reads).
  **Tier 3 DAX — DONE (2026-07-16, C#-only):** ADOMD has no async, so `InterruptScope` +
  `AdomdCommand.Cancel()` (thread-safe server-side abort) from the poller thread, wired at the chokepoints:
  `DaxCatalog.StreamCommand` (ALL data-returning DAX — scans/DMV/daxeval/daxevaltable; scope armed BEFORE
  `ExecuteReader` so the long initial evaluation is covered, ownership transfers into `DaxArrowStream`,
  disposed registration-first), `ProbeSchema` (the bind-time probe EXECUTES the query), and `DaxEachBinding`
  (ctor probe + ONE scope covering every per-row ExecuteReader in `DoExchange`, linked to the exchange ct).
  Metadata/DMV discovery reads stay uncancelled (SQL-provider policy); `AdomdConnection.Open` has no async —
  accepted. `verify_dax` 29 green vs live PBI Desktop. The host_query rewrite window closed at v66.
  **Remaining (deferred): Tier 4 only** — the arrow scan as a DuckDB async/BLOCKED source
  (`InterruptState`) to free the task thread during I/O (native interrupt + better parallelism, bigger). Live Ctrl+C
  behavior is a MANUAL check (a slow OneLake/SQL query + interrupt); the suites verify only behavior-neutrality.
  **BINARY STATUS (2026-07-23): ALL targets CURRENT on DuckDB v1.5.5 + the EW clast-master engine at
  ABI v67.** Windows: unittest + `duckdb.exe` shell + the loadable (2026-07-22). Linux (2026-07-23):
  `build/linux-payload/` fully refreshed — `fabricator.duckdb_extension` (35 MB, linux_amd64, built in
  WSL Ubuntu 24.04; **glibc symbol ceiling 2.38 = Azure Linux 3's glibc, Fabric-compatible**), the FDD
  zip (net8.0 loose-root, now incl. the AWSSDK assemblies), and the notebook wheel swapped to
  `duckdb-1.5.5-cp310` (the 1.5.4 wheel removed — `fabricnb` globs `duckdb-*.whl`; its Program.cs 1.5.4
  refs updated). Load-smoke green in WSL on the official 1.5.5 wheel + FDD + downloaded .NET 8 runtime
  (load, delta CTAS, explicit txn, DELETE, variant cast). **VALIDATED LIVE ON FABRIC (2026-07-23,
  fabricnb upload + RunNotebook): 16/18 probe steps green on the new payload — duckdb 1.5.5 wheel,
  loadable on AZL3 (the glibc-2.38 ceiling holds live), fuse Tables incl. txn append, ambient
  SQL/delta/DAX, token secrets; the 2 fails are the documented-expected pair (pbi-audience → 18456,
  static-azure-secret abfss → the pinned single-audience 401).** NOTE the harness prints the result,
  but the durable copy is `Files/fabricator_ext/result.json` on LH. To fetch it from Windows: `.read
  dax_secret.sql` then **`copy (select content from read_text('onelake://Test/LH.Lakehouse/Files/
  fabricator_ext/result.json')) to '<ABSOLUTE windows path>' (format csv, quote '', header false);`** —
  `read_text` is a TABLE function (a scalar call is a binder error), and the COPY target must be an
  absolute Windows path since duckdb.exe does not resolve a bash-style `/tmp`. Then parse the JSON
  locally (the probe's step list is the readable part). `fabricnb/Program.cs` repo path fixed to `d:\repos\fabricator-extension` (was the
  pre-rename path). `dbt_mssql_test/.venv` (serves dbt_dax_test too) upgraded to `duckdb==1.5.5` — the venv is uv-managed (NO pip module; use
  `uv pip install --python .venv/Scripts/python.exe`). Linux rebuild recipe unchanged: rsync `src/` +
  `test/` + `extension_config.cmake` → WSL `~/sqlext`, fresh duckdb clone at v1.5.5,
  `-DOVERRIDE_GIT_DESCRIBE=v1.5.5` + the `~/vcpkg` x64-linux toolchain, target
  `fabricator_loadable_extension`.
  **THE FABRIC NOTEBOOK NOW USES THE SINGLE FILE — the three-piece payload is RETIRED (2026-07-25,
  validated live).** `fabricnb` uploads ONE `fabricator.duckdb_extension` (the 40 MB linux_amd64
  STANDARD/framework-dependent SKU from `scripts/pack-distribution.ps1`) instead of loadable + FDD zip +
  `FABRICATOR_MANAGED_DIR`; the notebook stages that one file and just `load_extension`s it with **ZERO
  env vars** — the installer unpacks the core + bridge into DuckDB's own extension directory itself.
  **LIVE RESULT: 16/18 probe steps green (the 2 fails are the same documented-expected pair), i.e. NO
  capability lost.** Load timings measured IN the notebook: **3.22 s cold** (extract 39 MB + chain-load +
  CLR boot) and **0.18 s warm in a FRESH process** — the sha marker short-circuits extraction, proven on
  Fabric. **KEY FINDING: the DEFAULT extension directory is WRITABLE in a Fabric notebook** —
  `HOME=/home/trusted-service-user`, the bridge landed at
  `/home/trusted-service-user/.duckdb/extensions/v1.5.5/linux_amd64/fabricator`, so NO
  `SET extension_directory` is needed (the harness keeps a `/tmp` fallback attempt for environments where
  HOME is not usable; it did not fire). Everything else still passes on the new payload: fuse Tables
  (attach/read/create/txn append), delta local + lakehouse-files + abfss-ambient, warehouse SQL via
  database-audience token / `authentication default` / bare connstr, lakehouse SQL endpoint, warehouse
  ATTACH via token secret, azure access_token secret, and DAX ambient (14 tables, daxeval). The lakehouse
  `Files/fabricator_ext/` folder is down to TWO uploads (artifact + the duckdb wheel, which is STILL
  required because the core is CPP-ABI-locked to its DuckDB version while notebooks preinstall an older
  one) — the FDD zip and a stale 1.5.4 wheel were deleted, and the driver now retires anything in that
  folder that is not one of the current files. **⇒ `build/linux-payload/` no longer feeds the notebook
  flow; the recurring "refresh the stale payload" chore is GONE** (that dir remains only as the raw linux
  core + wheel source; `build/linux-dist/` holds the current v68 linux core).)
- **Prior: ABI v64** (v64 = **`onelake_move`** — atomic single-file rename via the ADLS Gen2
  DFS **native rename** (`DataLakeFileClient.RenameAsync`, a metadata op that overwrites an existing
  destination = MoveFile semantics; destination path filesystem-relative with the `<item>.Lakehouse`
  leading segment, same quirk as `FabricLakehouse.RenameDirectory`; same-workspace only). The onelake FS
  now implements `MoveFile`, which makes **DuckDB's default COPY tmp-file staging work on `onelake://`**:
  COPY to an EXISTING file writes `<file>.tmp` then `MoveFile` — a branch taken ONLY because `onelake://`
  classifies as LOCAL in DuckDB's hardcoded remote-prefix list (`bind_copy.cpp` forces
  `use_tmp_file=false` for remote schemes like `abfss://`, so duckdb-azure never hits its missing
  MoveFile). Previously the overwrite threw "MoveFile is not supported" unless `USE_TMP_FILE false`;
  live-validated (default COPY over an existing onelake file → new data, no `.tmp` leftover). NOTE:
  engineered-wood's Delta COMMIT rename deliberately stays on the exclusive-create-copy + delete
  emulation (it needs put-if-absent on the destination; a plain rename overwrites).)
- **Prior: v63** (v63 = **etag/mtime-backed cache validation on `onelake://`** —
  `onelake_open` gained `char **out_etag` (owned UTF-8, freed via free_error) + `int64 *out_modified_ms`
  outs: when the managed open DOES fetch properties (bare open, `known_size<0`) it returns the file's
  ETag + LastModified alongside the size (the SAME response — zero extra IO); a known-size open leaves
  them untouched and the host takes them from the listing's `extended_info` (v62 already carried
  `etag`/`last_modified` there). Both land on `FabricatorOneLakeFileHandle`, and the FS now overrides
  **`GetVersionTag`** (returns the etag) + `GetLastModifiedTime` (the real mtime; 0 when unknown).
  Why this matters: `onelake://` is NOT in DuckDB's hardcoded `EXTENSION_FILE_PREFIXES` remote list →
  `IsRemoteFile()=false`, but the cache-validation default is **`validate_external_file_cache =
  'VALIDATE_ALL'`** — validation RUNS for onelake, and `ExternalFileCache::IsValid` prefers the version
  tag whenever either side has one (exact etag compare; empty-vs-empty falls to the mtime + 10s-threshold
  path, which with our previous constant-0 mtime degenerated to always-valid). So cached ranges of an
  IN-PLACE OVERWRITTEN onelake file are now invalidated by default — for free, since the etag always
  rides an existing response. Unknown identity (no listing info on a bare open that skipped properties —
  can't happen; both sources covered) degrades to the old immutable-file assumption.)
- **Prior: v62** (v62 = **listing-metadata riding + skip-HEAD opens on `onelake://`**:
  `onelake_open` gained an `int64 known_size` arg (−1 = fetch) — the managed open SKIPS its per-file
  GetProperties round trip when the size is already known. Sources of "known": the OneLake DataLake
  listing now emits `size` + `last_modified` + `etag` in the glob JSON (all FREE fields of GetPaths), the
  C++ `FabricatorOneLakeFileSystem::Glob` surfaces them as `OpenFileInfo.extended_info` under the SAME keys
  httpfs fills (`file_size`/`last_modified`/`etag`), and the FS now opts into DuckDB's
  **`SupportsOpenFileExtended`/`OpenFileExtended`** so glob-fed opens (read_parquet globs, file lists)
  carry the info through — the exact mechanism httpfs uses to skip its HEAD request. Same pass, NO-bump
  fixes: **`HostFsGlob` no longer does OpenFile+GetFileSize PER MATCHED FILE** (on a fuse mount an open
  can DOWNLOAD the blob — this was the 258 s-ATTACH root cause; on S3 a HEAD per match) — size now comes
  from the glob entry's `extended_info` (object stores fill it in the LIST response, verified in httpfs
  s3fs.cpp) else −1, and the managed `DuckDbTableFileSystem.ListAsync` fills LOCAL files via a cheap
  `FileInfo.Length` (only consumer of size = VACUUM's bytes metric, best-effort by design).)
- **Prior: v61** (v61 = **`onelake://` is now WRITE-COMPLETE for Delta commits** —
  `onelake_open_write` gained an `int32 exclusive` arg (put-if-absent via ADLS conditional create,
  `If-None-Match:*` — the C++ onelake FS now honors `EXCLUSIVE_CREATE` instead of silently ignoring it) and
  one appended entry `onelake_remove(path, cred_json)` (`DataLakeFileClient.DeleteIfExists`, idempotent) backs
  `RemoveFile` — engineered-wood's commit rename is emulated as exclusive-create-copy + DELETE-SOURCE, so both
  were needed before **`fabricator_delta_write(<input>, path := 'onelake://…')` works** (previously abfss:// only;
  live-validated, v3 Overwrite commit + exact readback). Same pass, C++-only: `CreateDirRecursive`
  (fabricator_fs_spike.cpp) got a **scheme-authority recursion FLOOR** — the old guard only matched a literal
  trailing `scheme://`, and since the onelake FS reports `DirectoryExists=false` always (implicit ADLS dirs),
  the recursion walked past `onelake://Test` to `onelake:` which fell through to the LOCAL filesystem
  ("Failed to create directory \"onelake:\""); abfss:// never hit it because duckdb-azure answers the
  ancestor-exists probe. `MoveFile` on the onelake FS still throws (RENAME TABLE keeps the DFS-SDK route).)
- **Prior: v60** (v60 = `begin_transaction` gained an `int32 is_explicit` arg — 1 for a user
  `BEGIN..COMMIT`, 0 for the implicit per-statement autocommit wrapper (C++ reads
  `context.transaction.IsAutoCommit()` in `FabricatorTransactionManager::StartTransaction`). Drives the Delta
  provider's **buffered transactional DML** (slice 2 — see the explicit-transactions bullet): DELETE/UPDATE
  buffer ONLY in explicit transactions; autocommit keeps the direct per-statement paths (CDF capture,
  copy-on-write) byte-identical. Other providers ignore the flag.)
- **Prior: v59** (v59 = `begin_bulk` gained an `int32 partition_overwrite` arg — the
  **`PARTITION_OVERWRITE` COPY option**: DYNAMIC partition overwrite (Spark `partitionOverwriteMode=dynamic`) —
  `COPY src TO 'cat.sch.t' (FORMAT mssql, CREATE_TABLE false, PARTITION_OVERWRITE true)` atomically replaces
  exactly the partitions PRESENT IN THE INPUT in ONE Delta commit (their active files removed + the new files
  added — a log-level swap, no physical delete, so time travel keeps working; unlike DuckDB COPY's local-only
  physical OVERWRITE flag); untouched partitions are unaffected. The SQL-friendly successor to the
  `delta_write_options` `replace_where` setting (which stays for INSERT — STATIC explicit filter vs this DYNAMIC
  data-derived one; combining both errors). Guards: C++ bind rejects it with CREATE_TABLE/REPLACE; the provider
  rejects SCHEMA_MODE 'overwrite' combos + an unpartitioned target; providers WITHOUT partition semantics
  (SQL Server/DAX/deltars) REJECT it when set — an overwrite flag must never be silently ignored (deliberate
  break from the advisory schema/sort-option precedent). Delta: STREAMS under `native_write` (the partitioned
  COPY runs as usual; EW `CommitDataFilesAsync(dynamicPartitionOverwrite:)` derives the touched set from the
  written files' partitionValues and removes the matching active files) and collects otherwise (EW
  `DynamicOverwriteAsync` → `WriteCoreAsync(dynamicPartitionOverwrite:)` collects touched canonical partition
  keys during the write — `CanonicalPartitionKey`, physical-keyed under mapping with logical-key tolerance for
  old commits). `appendOnly` enforcement treats it as a non-append (it removes files).
  **CDF respects it**: the overwrite commit carries no `_change_data` files, so the feed INFERS the swap —
  removed files' rows → `delete`, added files → `insert` (Spark's representation of an overwrite). Making that
  correct fixed TWO PRE-EXISTING CDF-inference gaps (EW `CdfReader` rewritten to take the current snapshot):
  (1) on a PARTITIONED table the inferred rows lacked the partition column (data files exclude it — now re-added
  from the action's `partitionValues` via `PartitionUtils.AddPartitionColumns`, dual logical|physical keys);
  (2) a removed/added file's **deletion vector** was ignored — already-DV-deleted rows were re-reported as
  deletes (now excluded via `DeletionVectorFilter`; they were reported when the DV committed).
  `test/verify_delta_catalog_partition_overwrite.test` (85 — collect + streaming, one-commit swap, time travel,
  CDF feed incl. the DV edge, guardrails).)
- **Prior: v58** (v58 = additive `host_log` on `FabricatorHostServices` — forward managed ILogger events into DuckDB internal logging; wired + lockstep-verified + **`duckdb_logs` surfacing CONFIRMED LIVE**: `CALL enable_logging(storage='memory')` then `SELECT * FROM duckdb_logs WHERE type LIKE 'Fabricator%'` shows the events with the ILogger category as `type` [`Fabricator.Delta`/`.Native`] + mapped `log_level`. The earlier 0-rows was two red herrings, not a code bug: the enable form is `CALL enable_logging(...)` not `PRAGMA`, and the **shell** defaults log storage to a console-printing sink [`storage='memory'` is the `unittest`/API default]. The `FABRICATOR_LOG_LEVEL`+`FABRICATOR_LOG_FILE` file sink is the always-on independent trace. **2026-07-16 FIX: `HostLogService` now gates on `Logger::ShouldLog` before `WriteLog`** — `Logger::WriteLog` writes UNCONDITIONALLY (bypasses enabled/level/type config), and the shell's console sink printed EVERY forwarded Debug/Info event (`DEBUG: abi get_metadata …` on each metadata call — first visible when the v66 rebuild gave the shell a working bridge again). Semantics are now DuckDB-native: the shell surfaces Fabricator WARNINGs only (empirically its default config passes WARNING and blocks Info/Debug — e.g. the benign per-ATTACH DAX `TMSCHEMA_PARTITION_SOURCES` probe failure via the central failed-crossing WARN), unittest/API stay silent, and `duckdb_logs` shows what the user enables. **Gotcha: the `.test` duckdb_logs pins on Debug messages (per-file `read_parquet` SQL) must enable with `CALL enable_logging(level := 'debug', storage := 'memory')`** — the default enabled level is INFO (late_materialization / row_tracking_virtual / dynamic_filter updated).)
- **Prior: v57** (v57 = **`delta_list_files`** — one appended vtable entry for the native-read
  MultiFileReader path (docs/multifile-delta.md Phase A slice 1a): `fabricator_delta_mfr_scan(path)` clones
  `parquet_scan` + swaps in `FabricatorDeltaMultiFileReader` (`src/fabricator/fabricator_delta_mfr.cpp`), whose
  `CreateFileList` calls `delta_list_files(path, push_json)` → C# `DeltaReader.ListScanFilesJson` (engineered-wood's
  EXACT active `add` files as JSON `[{"path":<uri>}]`, onelake:// for OneLake) → a `SimpleMultiFileList`; DuckDB's
  **native parquet MultiFileReader** reads them (cached). The C++ MultiFileReader foundation for DV / partition /
  dynamic-filter pushdown (later slices 1b–1e); `parquet` statically linked (extension_config.cmake). Live/local:
  `test/verify_delta_mfr_scan.test` (36, matches the C# reader). **Slice 1b DONE (deletion vectors, no ABI bump):**
  `delta_list_files` emits per file the deleted row positions (`"dv":[…]`, via engineered-wood's
  `DeletionVectorReader`); C++ gained a custom `FabricatorDeltaMultiFileList` (per-file DV) + `InitializeGlobalState`
  override + `FinalizeBind` attaching an `FabricatorDeltaDeleteFilter` → DuckDB's native read EXCLUDES deleted rows
  (`test/verify_delta_mfr_dv.test`, 23). Gotchas: the DeleteFilter must `result_sel.Initialize(STANDARD_VECTOR_SIZE)`
  before writing (reader passes a null sel_vector → else segfault); **bare `count(*)` over-counts on a DV table**
  (empty-projection parquet-metadata count path skips the DeleteFilter — use a column scan; follow-up can disable
  it). **1c (partition) + 1d (filter pushdown) LARGELY ALREADY WORK via the inherited parquet_scan (verified):**
  `fabricator_delta_mfr_scan` clones parquet_scan → inherits **filter pushdown** (EXPLAIN shows `Filters:` INSIDE the
  scan → static + dynamic filters prune row-groups natively, no custom Complex/DynamicFilterPushdown) + **hive
  partitioning** (engineered-wood's `<col>=<value>/` layout → `region` resolves from the path; verified on a
  PARTITIONED BY table). So 1a+1b + inherited features = a nearly complete native read (reader + projection +
  filter/row-group pushdown + hive partitions + parallelism + ExternalFileCache + DV). **Remaining:** 1d-file =
  Delta-log FILE-level pruning (optimization over row-group pruning; needs an engineered-wood prune-by-predicate
  API), 1c-edge = log-authoritative partition injection for typed/NULL edge cases, the bare-count(*)-on-DV fix.
  **1e slice 1 DONE (2026-07-03, C#-only, NO ABI/C++) — native read folded into the ATTACH catalog:** the Delta
  folder-catalog ATTACH option **`native_read true`** routes a plain `SELECT … FROM lake.main.t` through DuckDB's
  own parquet reader — `DeltaCatalog.ScanTable`'s plain-read branch runs `Host.Query("SELECT * FROM
  read_parquet([<exact active files>])")` (files via `DeltaReader.GetActiveFileUrisWithDv`; tuned decode +
  cross-file parallelism + ExternalFileCache, over `onelake://` for OneLake) instead of engineered-wood's C#
  reader. **NOT the C++ MultiFileReader-from-the-catalog fold** (that needs a pre-bound parquet scan out of
  `GetScanFunction` — `TableFunctionBindInput` wants a live `Binder`+`TableFunctionRef` the catalog entry lacks,
  fragile/DuckDB-coupled); routing through the C# `ScanTable` seam keeps all catalog plumbing (three-part names,
  stats, DML, time travel) intact and is a pure byte-source switch. **Opt-in (default off) + read-only:** a rowid
  scan (UPDATE/DELETE), a time-travel scan (`AT`), or a DV table transparently falls back to the C# reader (no
  DeleteFilter/rowid/snapshot on the native path) — `test/verify_delta_catalog_native_read.test` (53: read/
  projection/filter/aggregate/multi-file append + DELETE/UPDATE via the rowid C# reader + DV fallback). **Caveats:**
  the pushed FILTER is not yet translated into the host SQL (no Delta-log/row-group pruning on this path — the C#
  reader still prunes), projection is left to DuckDB above the scan, and the bind-time COLUMNS schema must match
  `read_parquet` BY NAME (holds for engineered-wood/Spark plain tables; decimals align at `Decimal128`). **Remaining
  (heavier follow-up) = the full MultiFileReader-in-catalog fold** (native DeleteFilter for DV, dynamic join/TopN
  filter pushdown into row-groups, native rowid via `file_row_number`+path-sorted ordinal for DML, native time
  travel). **Direct-fold design decisions captured (docs/multifile-delta.md §"Native-read fold"):** (a) **rowid is
  a VIRTUAL COLUMN** (duckdb-delta sets `function.get_virtual_columns` declaring `file_row_number`/`rowid`/
  `delta_file_number`) requested at scan-init, NOT a bind-time decision → native DML is achievable directly; our
  transient rowid `(fileOrdinal<<40)|file_row_number` = `file_list_idx<<40 | file_row_number` in `FinalizeChunk`,
  needing a **relative-path-sorted** file list to match engineered-wood's `OrderedActiveFiles` ordinal; (b)
  **snapshot consistency across a multi-table join** — capture one UTC instant per DuckDB transaction
  (`AmbientTransaction`/`TxnState`, v35) and pass it as an implicit `AT (TIMESTAMP)` into `catalog_list_scan_files`
  (explicit `AT` overrides; resolved via always-on `commitInfo.timestamp`; cache the resolved version per
  (txn,table)); (c) **dynamic filters + parallelism** inherited from `parquet_scan` for free — `global_state.filters`
  is a live pointer applied per-file-at-open, so dynamic filters only prune NOT-YET-opened files and a very high
  thread count opens files before the join filter materializes (parallelism-vs-late-filter trade-off, self-bounded by
  the #threads look-ahead); (d) **S3/MinIO write** works with an S3 secret + NO opener conflict (`_fabricCredential`
  null → host-FS + opener secret), caveats: httpfs must be linked for tests, EXCLUSIVE_CREATE (commit put-if-absent)
  may not be enforced on S3 httpfs (concurrent-writer safety), DROP/RENAME dir-ops may be unimplemented on the S3 FS.
  **DECISION (2026-07-03): the pure-C# native reader is the target path, NOT the C++ MFR fold.** Analysis showed the
  MFR's advantages all replicate in a C#-orchestrates-`read_parquet`-via-`Host.Query` reader for the cloud target:
  native decode + cache (read_parquet), rowid/DML (`(ordinal<<40)|file_row_number` in SQL, no `get_virtual_columns`),
  DV (drop positions per file), projection + static filter (into read_parquet WHERE), Delta-log file pruning +
  early-stop (per-file loop), and even **dynamic (join) filters are NOT MFR-exclusive** (they flow to any
  `filter_pushdown=true` table function via `input.filters`, `physical_table_scan.cpp:35-36`; mid-scan-materializing
  filters only prune the not-yet-opened tail even in the MFR, closable in C# via a per-file live-filter callback).
  The ONLY non-replicable MFR edge = **downstream multi-lane parallelism** (one Arrow stream = one `get_next` lane),
  relevant for CPU-bound-local star-schema joins, **secondary for cloud I/O**, and **additive later** (partition the
  file list into N per-thread streams — doesn't touch rowid/DV/filter, ordinal stays global-path-sorted). Plan
  (docs/multifile-delta.md §"Concrete plan"): grow the `native_read` branch into a per-file loop
  (prefetch/bounded-channel ≈ threads) with `file_row_number` rowid/DML + DV + projection + static filter + log
  file-pruning (slice 1, C#-only), optional live-filter host-callback for dynamic pruning (slice 2), multi-lane
  parallelism (slice 3). Build the C++ `fabricator_delta_mfr_scan` MFR (slices 1a/1b done, standalone) only if
  CPU-bound-local multi-lane becomes a goal. **Slice 1 DONE (2026-07-03, C#-only, no ABI):** `DeltaNativeReader`
  is a **per-file loop** (`FABRICATOR_DELTA_PREFETCH`, default 1 = sequential, >1 = concurrent file fetch) emitting
  per file `SELECT <proj>[, ((ord::BIGINT<<40)|file_row_number) AS "_metadata.row_id"] FROM read_parquet(<file>,
  file_row_number => true) [WHERE <static> [AND file_row_number NOT IN (dv)]]` via `Host.Query` — so plain SELECT,
  **DELETE/UPDATE (native rowid via file_row_number, no C#-reader fallback)**, DV exclusion, projection, static
  filter + **Delta-log FILE pruning** (`DeltaFilePruner`, now `public`), and time travel (`AT VERSION/TIMESTAMP`)
  all run natively. File list **relative-path-sorted** for `OrderedActiveFiles` rowid-decode parity; output schema
  **probed** (`read_parquet … LIMIT 0`) to match batches by type. **Snapshot consistency DONE:** `SnapshotPinning`
  captures one UTC instant per DuckDB transaction (`AmbientTransaction`, keyed on the txn id — fires per statement
  in autocommit, once per explicit `BEGIN`) → resolves+pins the version per (txn,table) via
  `DeltaReader.ResolveVersionAsOf` (commitInfo.timestamp; falls back to latest), so a multi-table join reads a
  consistent cut. **Logging DONE (ILogger, C#-only):** `FabricatorLog` (off by default; `FABRICATOR_LOG_LEVEL` +
  `FABRICATOR_LOG_FILE` → file sink; factory pluggable for a future DuckDB-forwarding provider) traces the resolved
  snapshot version, file list (active/scanned/pruned), and each per-file `read_parquet` SQL. Verified:
  `test/verify_delta_catalog_native_read.test` (66); Delta write/delete/update/decimal/time_travel/changes
  unregressed. **Slice 2 DONE (dynamic/join filter pushdown, C++/C#, NO ABI — the predicted per-file host
  callback was unnecessary):** a hash join builds before the probe scan inits, so the runtime filter set is
  rendered ONCE at `ArrowStreamInitGlobal`. A C#-declared catalog capability (`DeltaCatalog` →
  `exact_filter_pushdown=true` on `ServerInfo`, ONLY under `native_read`) → C++ `FetchExactFilterPushdown` →
  `BuildScanFunction` sets `function.filter_pushdown=true` for the native catalog ONLY (SQL Server / DAX /
  non-native Delta stay false → unchanged, verified). `arrow_ingest::RenderLiveFilters`/`RenderTableFilter` walk
  the live `TableFilterSet` (keys→names as `PhysicalTableScan::GetFilterInfo`), emitting exact DuckDB SQL: unwrap
  `OptionalFilter`, resolve `DynamicFilter` under its lock (skip if not `initialized`), skip bare `BLOOM` (all
  three are pruning-only — the join re-applies — so skip-safe), recurse `CONJUNCTION` per child (not `ToString`,
  which leaks `optional:`), else `TableFilter::ToString`; merged into `spec_json.native_filter` (slice-1 channel).
  Mandatory erased static filters render 1:1 (read_parquet target IS DuckDB) → correct; bonus: string `<>`/ordering
  now pushes exactly on the native path. `test/verify_delta_catalog_dynamic_filter.test` (21) + native_read (66) /
  delete (28) / update (63) / partition (54) / dv (48) / time_travel (48) + SQL Server pushdown suites green.
  Nuance: a mid-scan-refined dynamic filter (TopN) is captured only as of scan-init (best-effort). **Remaining:
  slice 3** (multi-lane, deferred by user).
  **STRUCT-member filter pushdown/PRUNING DONE (2026-07-06).** `WHERE (s).a = 5` (arrives at
  `pushdown_complex_filter` as a `struct_extract` `BoundFunctionExpression` chain — verified in DuckDB source;
  the ColumnIndex-with-children rewrite is projections-only and runs AFTER filter pushdown) is now serialized
  as a **dotted-path FilterNode** (`"path":["s","a"]`, `col` null — a renderer without path support throws →
  its caller falls back to no pushdown, superset-safe; SQL Server/DAX can't have struct columns) and drives
  engineered-wood **file-level pruning via nested Delta stats + parquet row-group/bloom pruning**:
  `FilterSerializer::ColumnPath` unwraps `struct_extract`/`struct_extract_at` (member names resolved from the
  child struct type via the bound `StructExtractBindData` index — exact case), all shapes (compare / IS NULL /
  IN / BETWEEN / conjunctions); `DeltaFilterBuilder` joins the path to EW's dotted convention ("s.a") which the
  parquet `StatisticsAccessor`/bloom probe ALREADY resolve natively (`ColumnDescriptor.DottedPath`); EW
  `ColumnStats.Parse` now FLATTENS nested minValues/maxValues/nullCount into dotted keys (nested nullCount was
  previously dropped at parse) + `DeltaFilePruner` registers struct leaves dotted (dual logical|physical under
  column mapping; a literal dotted column name colliding with a struct path is POISONED — no pruning, never a
  guess; EW `NestedStatsPruningTests` 6). **Nested nodes emit NO native-SQL twin** (JSON-only): the rendered
  logical member access would mis-bind inside `read_parquet` on a column-mapped table (physical child names) —
  so the Conjunction/top-level SQL joins tolerate empty per-part SQL (AND skips = superset; OR drops its whole
  SQL twin). **Exact mode**: the erased StructFilter renders as proper `struct_extract("s",'name')` SQL (new
  `RenderTableFilter` STRUCT_EXTRACT case — the bare `ToString` dot form mis-quotes exotic names); works on the
  codec path (HostBatchFilter runs over logically-renamed batches → mapped tables fine), unmapped native, AND
  **column-mapped native_read via the LOGICAL STRUCT REBUILD** (second pass, same day): `DeltaNativeReader`'s
  per-file inner subquery rebuilds each mapped struct column with logical member names — `CASE WHEN "col-s" IS
  NULL THEN NULL ELSE struct_pack("a" := ("col-s")."col-a", …) END AS "s"` (recursive; the CASE preserves
  NULL-struct semantics — bare struct_pack would materialize a non-NULL struct of NULLs; quoted named args
  verified). Child names resolve per level via `StoredChildName`: **id mode through THIS file's parquet
  field_ids** (`FileMapping.FieldIdToName` — the pre-existing per-file footer probe, reads every vintage incl.
  old EW id-files that stored logical names) else the declared `physicalName` (name mode), else logical.
  `NeedsRebuild` skips no-op trees (unmapped/equal names keep the plain alias + parquet filter pushdown);
  list/map members pass through unrebuilt (per-batch `ArrowColumnMappingRename` still fixes their inner names —
  its tolerant either-name matching makes it a no-op on rebuilt columns). `DeltaSqlFilter` also renders path
  nodes as `struct_extract` chains (a nested-only WHERE now applies in-query on the static native path too).
  **DuckDB's `supports_pushdown_type` veto was tried and REJECTED** (would pull
  struct filters back out of the scan): it requires `filter_prune`-maintained projection_ids and DuckDB's veto
  path (plan_get.cpp) corrupts rowid DML plans (RemoveUnusedColumns early-outs on everything_referenced →
  empty projection_ids → the veto's append turns identity into a 1-column projection) and crashes on rowid
  entries (`virtual_columns.at` reads the FUNCTION-level hook, not the entry's) — upstream only pairs it with
  the DML-less arrow scan; upstream-report candidate. `ArrowStreamInitGlobal` now honors `input.projection_ids`
  (the filter_prune contract, currently always empty=identity — defensive). **CRUX BUG FIXED en route (latent,
  affected ALL exact-mode scans): `pushdown_complex_filter` is re-invoked by a later optimizer round with an
  EMPTY list once the first round's predicates were erased into the TableFilterSet — the callback's
  clear-on-entry WIPED `filter_json`, silently forfeiting Delta file pruning on every exact-mode
  (native_read / `pushdown_filters 'all'`) scan.** Now an empty re-invocation KEEPS the previous serialization
  (still the query's own predicates → pruning stays superset-correct); proven: `delta native list … pruned=1`
  on a nested predicate. `test/verify_delta_catalog_struct_filter.test` (67 — all predicate shapes, two-level
  nesting, cross-mode agreement none/static/exact, exact-mode DML with struct WHERE, **name- AND id-mapped
  native_read struct filters via the rebuild** incl. NULL-struct semantics, unmapped native_read, plain +
  mapped codec); delta suite 42/42 + SQL Server suites green.
  **DYNAMIC-filter I/O PRUNING DONE (2026-07-06, third pass — C++-only, no ABI).** The live TableFilterSet
  render at scan init now has a SECOND, structured channel: `SerializeLiveFilters` (arrow_ingest.cpp, beside
  `RenderLiveFilters`) converts the erased statics + **materialized dynamic/join filters** into a FilterNode
  JSON tree — AND-merged with the bind-time `filter_json` in `BuildScanSpec` (both are true predicates;
  duplicated statics are idempotent for pruning) — so a hash-join's runtime bounds now drive engineered-wood
  **Delta file pruning + parquet row-group/bloom skipping** on BOTH Delta paths (previously dynamics reached
  only the SQL channel = read_parquet row-groups on native; the codec path got nothing). Handles
  Constant/IsNull/In/Conjunction/Optional/Dynamic(under its lock)/StructFilter(→ path nodes); OR is
  all-or-nothing with constants staged in a scratch vector so a dropped branch can't leak indices; constants
  extend a **PER-EXECUTION copy** of the bind constants (bind data is shared across executions — never
  mutated); string-ordering gate honored; TOP/ORDER-BY spec emission now also gated on the live JSON (a
  pushed TOP over a superset-filtered scan could drop rows). Proven: JOIN probe scan logs `active=2 scanned=1
  pruned=1` (the out-of-range file skipped BEFORE any I/O) with the IN + min/max bounds also in the WHERE.
  SQL Server/DAX unaffected (input.filters exists only under exact-mode filter_pushdown). Same pass: the **6
  EW Parquet.Tests decimal assertions rewritten for the committed Decimal128 widening** (read tests assert
  Decimal128Type/Array with preserved precision/scale; roundtrips write narrow → read wide, values lossless)
  — EW Parquet.Tests now 573/585, the remaining 12 = the pre-existing ALP bit-exact + parquet-testing sweep
  failures (separate triage).
  **ROWID FAST PATH + LATE MATERIALIZATION — DONE (2026-07-13, C++/C#, no ABI).** The `native_read` Delta
  scan opts into DuckDB's late-materialization rewrite (`function.late_materialization = true` + the
  FUNCTION-level `get_row_id_columns` hook — distinct from the entry-level `GetRowIdColumns` that serves
  DML planning; the flag defaults FALSE and only DuckDB's own seq_scan/read_duckdb set it, so the rewrite
  never fired on extension scans before): `ORDER BY x LIMIT n` becomes TopN-on-a-narrow-scan + **SEMI join
  back on rowid** (JoinFilterPushdown supports SEMI → the tiny build side's dynamic min/max — and for small
  builds an IN-list — lands on the probe scan's rowid column via the exact-mode `input.filters`). Rowid
  filters (dynamic + user `WHERE rowid =/IN/range`) are now SERIALIZED — `SerializeLiveFilters`/
  `RenderLiveFilters` previously SKIPPED `COLUMN_IDENTIFIER_ROW_ID`, which in exact mode was a **latent
  correctness bug** (an ERASED static `WHERE rowid = X` was silently unapplied); they now resolve it to the
  single virtual rowid name (`_metadata.row_id`, gated `virtual_rowid_columns.size()==1`) — and **DECODED
  by the native reader** (`DeltaRowIdFilter`): the ordinal half (`rowid >> 40`) selects EXACTLY the matching
  files (no stats — the transient rowid is a LOCATOR, which is why `__delta_row_id`-style stats are not
  needed for fast access), the position half becomes a per-file `file_row_number` predicate which the
  parquet reader ROW-GROUP-prunes (verified: `ParquetColumnSchema::Stats` synthesizes exact per-row-group
  min/max for FILE_ROW_NUMBER). Rowid conjuncts are STRIPPED from the EW prune tree (no rowid stats;
  dropping an AND-conjunct widens = superset-safe) while the rendered SQL keeps them exact — the per-file
  SELECT aliases the rowid expression and DuckDB permits SELECT-alias references in WHERE. Enablement
  required **`ArrowStreamBindData::Copy`** (the rewrite clones the fetch-side get's bind data via
  `LateMaterializationHelper::CreateLHSGet`; `schema_root`/`arrow_table` are bind-time-only — nothing reads
  them after PopulateReturnSchema, scans build per-scan converters from the live stream — so the copy
  shares everything else and leaves them empty). Gated to `ExactFilterPushdown()` + virtual BIGINT rowid ⇒
  **Delta native_read only** (SQL Server/DAX deliberately off: their TopN pushdown is superior and a
  join-back would re-scan the server; entry-level rowid/DML unaffected). Fetch cost = O(matched files):
  LIMIT 1 → `files 3 -> 1` + `file_row_number IN (p)`. NOT used by DML — UPDATE/DELETE/MERGE plans scan
  ONCE with the WHERE pushed and rowids flow UP to the modify operator (no rowid-filtered re-scan exists
  there). Fixed en route: `WithPendingDeletes` dropped `PartitionColumns` from its listing rebuild (a
  partitioned table with buffered DELETEs read its partition column as NULL mid-txn under native_read).
  `test/verify_delta_late_materialization.test` (57 — TopN + prepared re-exec, point/IN/range/edge-bound/
  contradictory rowid lookups [layout-independent counts: file ordinals are path-sorted RANDOM uuids], DV
  composition, pending-file ordinals inside a txn, non-native catalog unaffected, duckdb_logs pins of
  `rowid prune … 3 -> 1` + `file_row_number IN`); regression: native_read 88 / dynamic_filter 21 /
  struct_filter 67 / update 63 / delete 28 / dv_default 58 / time_travel 48 / transactions 934 + SQL
  scalar/table-fn/pushdown suites green. `docker/provision.ps1` now also creates the shared read-only
  `dbo.TestSimplePK` fixture (7 suites read it; nothing re-created it after the compose migration).
  **STABLE ROW-TRACKING VIRTUAL COLUMNS — DONE (2026-07-13, additive metadata kind 12, NO ABI bump).**
  `SELECT __delta_row_id, __delta_row_commit_version FROM lake.s.t` works on **row-tracking tables under
  `native_read`** (the Delta materialized-column names; Spark's `_metadata.row_id`/`_metadata.
  row_commit_version` equivalents): queryable by name, EXCLUDED from `SELECT *`, per row =
  `COALESCE(materialized __delta_row_id column, baseRowId + file_row_number)` /
  `COALESCE(materialized version, defaultRowCommitVersion)` — distinct from the transient `rowid` (a
  locator), these are the durable identity. Mechanism = **generic provider virtual columns**:
  `FABRICATOR_META_VIRTUAL_COLUMNS = 12` (arg1/2 = schema/table → (name, type-text) rows; best-effort
  try/catch fetch in `GetOrCreateEntry`; every other provider returns empty) → the entry registers them in
  `GetVirtualColumns()` at `fabricator::ProviderVirtualBase()` (= `VIRTUAL_COLUMN_START + 0x100`; DuckDB's
  `TableBinding` maps virtual names for bare-name binding, and a REAL same-named column shadows the
  virtual) → `ArrowStreamBindData.provider_virtual_columns` → `BuildScanSpec` fetches by name,
  `BuildProjectionMapping` resolves 1:1 by name, and both live-filter serializers resolve virtual-id
  filters (exact-mode erased `WHERE __delta_row_id = k` applied exactly, like the rowid fix). C#:
  `DeltaCatalog` advertises the pair gated on `_nativeRead` + `delta.enableRowTracking` (flag cached per
  path by the Columns fetch via `GetSchemaAndRowTracking` — no extra `_delta_log` read on entry
  materialization, the OneLake cost concern); `NativeScanFile` carries `BaseRowId`/`CommitVersion` from
  the add actions; `DeltaNativeReader.RowTrackingExpr` renders the per-file expression (materialized
  presence via the existing footer probe). Semantics validated: append ids follow COMMIT order
  (deterministic); **buffered (explicit-txn) UPDATE preserves the id + bumps the version** (post-images
  bake materialized ids); DV DELETE removes the id; **OPTIMIZE preserves both** (compaction materializes);
  a txn's PENDING rows read NULL (baseRowId assigned at commit); non-tracked tables / codec catalogs
  don't bind the names (clean binder error). **Pinned known gap:** an AUTOCOMMIT UPDATE on a (default)
  column-mapped table takes the copy-on-write rewrite whose new add carries NO row tracking (the
  documented deferred P5-rewrite gap) — those rows read NULL until the EW rewrite materializes ids.
  NOTE: with `deletion_vectors` defaulting true (which enables rowTracking), most default-catalog tables
  are row-tracking → the columns bind broadly under native_read.
  `test/verify_delta_row_tracking_virtual.test` (87); regression: transactions 934 / row_tracking 33 /
  materialize 17 / compaction_rowtracking 24 / native_read 88 / dv_default 58 / update 63 /
  column_mapping 251 / late_materialization 57 + SQL scalar/table-fn/procs/orderby green (SQL/DAX/deltars/
  stub each gained an explicit empty VirtualColumns metadata case).
  **STABLE-ID FAST PATH — DONE (2026-07-14, `DeltaRowTrackingFilter`): filters on
  `__delta_row_id`/`__delta_row_commit_version` skip FILES + ROW GROUPS** (point lookups, dedup DELETEs
  without a unique key, `version > X` incremental extracts) — NO log-stats writes needed (the materialized
  column is off-schema; Spark writes no stats for it either, and neither do we). Per file, decided AFTER
  the footer probe every native scan already runs: **derived file** (no materialized column — every plain
  append) → the log alone bounds ids to `[baseRowId, baseRowId + stats.numRecords)` (`NativeScanFile.
  NumRecords` added) — file SKIPPED on no intersection, else the constraint rewrites to a
  `file_row_number` predicate (the same synthesized-zone-map machinery as the rowid path); the version is
  a per-file CONSTANT (`defaultRowCommitVersion`) — whole-file match/skip; **materialized file**
  (rewrites — ORIGINAL ids, decoupled from the fresh baseRowId, so the derived-range subtraction does NOT
  apply) → the constraint pushes onto the PHYSICAL column in the per-file INNER WHERE as
  `(pred(col) OR col IS NULL)` (single-column ⇒ parquet zone maps prune; the IS NULL arm keeps
  derived-fallback rows visible); files with NULL BaseRowId under a value constraint (pre-enablement adds,
  a txn's pending files) skip outright (the column reads NULL). Extraction = AND-reachable compare/IN
  conjuncts + SINGLE-COLUMN OR-of-equals (how the live serializer renders erased/dynamic IN filters);
  conjuncts stripped from the EW prune tree (no log stats — dropping widens). The condition sits on the
  INNER subquery over read_parquet (inner WHERE binds source columns before SELECT aliases → hits the raw
  physical column, not the COALESCE alias); superset-safe — the outer WHERE still applies the exact
  predicate. NOTE OPTIMIZE consumes fresh baseRowId space for the compacted add (HWM jumps), so
  post-compaction inserts get ids ABOVE the old range (pinned). Spark/OSS-delta have NO row-id skipping
  (docs+source checked — `_metadata.row_id` is computed post-scan, file-constant-only metadata predicates)
  — our stable-id lookups are now faster than Spark's on the same tables. Guidance stands: IDENTITY
  columns for lookup keys (standard stats everywhere); row-ids for correlation + keyless dedup.
  **CRITICAL PRE-EXISTING BUG FOUND + FIXED by the dedup test (C++, ALL providers):
  `DELETE … WHERE x [NOT] IN (subquery)` read the WRONG child column as the rowid** —
  `AppendModifyBatch` assumed "rowid = last column", but a mark-join DELETE plan has NO projection
  between FILTER and DELETE, so the child chunk ends with the BOOLEAN mark (crash
  `Vector::Reference … BIGINT referenced BOOLEAN`; a same-width plan could have deleted wrong rows).
  Fix mirrors upstream `DuckCatalog::PlanDelete`: the rowid position comes from
  `LogicalDelete::expressions[0]` (the bound row-identifier ref) → `FabricatorModifyTarget.
  rowid_child_index`; UPDATE keeps the last-column contract (its binder-built projection guarantees it,
  as upstream PhysicalUpdate assumes). `verify_delta_row_tracking_virtual.test` now 299 (fast-path
  sections: point/IN/range + version filters with duckdb_logs skip pins + the `file_row_number` rewrite
  pin, post-OPTIMIZE materialized-column pushdown pin, DELETE by id list, mark-join dedup DELETE);
  regression: transactions 934 / late_materialization 57 / native_read 88 / update 63 / delete 28 /
  dynamic_filter 21 / SQL scalar 26 green.**
  The MoR eligibility check in EW `UpdateByRowIdsAsync` still required `mappingMode == None` — a **stale
  gate**: the 2026-07-06 `ColumnMappingRecursive.ToPhysical` pass had already made `UpdateViaVectorsAsync`'s
  post-image append mapping-capable, but the caller's condition was never relaxed, so since `column_mapping
  'name'` became the default (same day) EVERY autocommit UPDATE on a default-created table silently took
  copy-on-write (full-file rewrite + the P5 row-tracking loss; unnoticed because the DV/MoR suites pin
  `column_mapping 'none'`). Now lifted: autocommit UPDATE on mapped (name AND id) DV tables is merge-on-read
  on both writer modes — kernel (delta-kernel) reads the commits exactly; the commit shape is
  remove+add(same path, DV, **baseRowId preserved**) + post-image add. **Two more EW fixes in the pass:**
  (1) the post-image add's stats were collected over the LOGICAL-named batches — now over the
  physical-renamed, pre-`__delta_row_id` batches (spec: stats keys are physical under mapping);
  (2) **materialized-source ids honored**: updating a row in a file that ITSELF carries materialized
  `__delta_row_id` (a compacted file, an earlier update's post-image) resolved the id as `baseRowId +
  position` = the file-LOCAL id — silently changing row identity post-OPTIMIZE (caught by the new virtual-
  columns pin: id 9 read 18 after OPTIMIZE→UPDATE). `ProcessFileBatchesAsync`/`ReadFileAsync` gained an
  optional `strippedRowIdsOut` collector (the strip already had the ids in hand) and `UpdateViaVectorsAsync`
  prefers the source's materialized value per row. **The BUFFERED path still has this post-OPTIMIZE caveat**
  (its read-back resolves via `OrderedActiveBaseRowIds` + ordinal arithmetic, no materialized-source read —
  follow-up). Remaining MoR gates (the rest of the full-matrix plan): partitions (slice 2 — route the append
  through `WriteDataFilesAsync` for the partition split), CDF (slice 3/4 — pre/post-image cdc emission;
  rows already in hand), type widening (validation-only). **PolyBase interop tables are unaffected** (DV
  off ⇒ MoR never engages; their protocol-1.0 CoW path is untouched). No memory-shape change: both UPDATE
  paths materialize one affected file at a time (pre-existing; MoR appends only the matched rows).
  Noted for separate triage (pre-existing, surfaced while probing): `(v).a` member access reads NULL on a
  `CREATE TABLE + INSERT CAST(… AS VARIANT)` shape while the full `v` value + kernel read are exact (both
  reader paths; before any UPDATE — not MoR-related; the variant suite's own dot-access pins pass).
  `verify_delta_row_tracking_virtual.test` now 93 (autocommit UPDATE on the mapped default table preserves
  `__delta_row_id` + bumps the version — incl. a post-OPTIMIZE source; the remaining-CoW pin moved to a
  PARTITIONED table); full delta sweep (update 63 / dv_default 58 / dv 48 / variant 133 / column_mapping
  251 / changes 73 / delete 28 / materialize 17 / compaction 24 / row_tracking 33 / native_write 147 /
  native_read 88 / nested_alter 100 / constraints 50 / partition 54 / time_travel 48 / transactions 934 /
  late_mat 57) + EW DeltaLake 168 & Table 147 (all TFMs) green.
  **MERGE-ON-READ UPDATE: PARTITION GATE LIFTED (slice 2, 2026-07-13, EW + one Bridge guard removal).**
  Partitioned DV tables now take merge-on-read too: `UpdateViaVectorsAsync`'s post-image append routes
  through **`WriteDataFilesAsync`** when the table is partitioned (partition split → Hive dirs + per-file
  physical-keyed partitionValues + per-file stats + the `IDataFileWriter` seam), and builds the `add`s from
  the returned `WrittenDataFile`s exactly as `CommitDataFilesAsync` does (`DeltaPath.Encode`, baseRowId
  sequencing per file, numRecords-fallback stats, HWM domainMetadata). **A SET of the PARTITION column just
  works** — the post-image row lands in its new partition's file (DV-delete in the old partition, add in the
  new; verified: `region=EU → APAC/` dir in the commit, identity preserved). **`WriteDataFilesAsync`'s
  `materializedRowIds` unpartitioned-only restriction is lifted** — the id column is attached BEFORE the
  partition split (rides the regrouping with its row), then strip→ToPhysical→re-append per partition file
  (out of stats) — which also lifted the Bridge's buffered `materialize_row_tracking × partitioned UPDATE`
  rejection (`EnsureBufferedDmlEligible`). MoR passes `identityValuesPreGenerated: true` (post-images carry
  their existing identity values; regeneration would reassign) and the MoR gate gained `!IsIcebergCompat`
  (WriteDataFilesAsync rejects iceberg; falls to CoW). Remaining MoR gates: **CDF** (slice 3/4 — the last
  CoW-with-row-tracking-loss shape, pinned) and type widening (validation-only). Unpartitioned MoR keeps the
  single-file bespoke append unchanged (slice-1-validated); the partitioned path writes one file per
  (post-image batch × partition) — small-file inefficiency only, OPTIMIZE compacts. Kernel-validated:
  unmapped partitioned MoR reads exactly (partition column included); mapped partitioned shows the known
  kernel partition-column-NULL quirk (all commits of such tables, not MoR-specific; Spark is the reference).
  `verify_delta_row_tracking_virtual.test` now 110 (partitioned MoR preservation + partition-key SET moves
  the row with identity intact + buffered partitioned materialize UPDATE + the CDF CoW-NULL pin); sweep:
  update/dv_default/dv/variant/column_mapping/changes/delete/materialize/compaction/row_tracking/
  native_write/native_read/partition 54/partition_overwrite 90/constraints/identity 38/transactions 934 +
  EW 168 & 147 green.
  **MERGE-ON-READ UPDATE: CDF GATE LIFTED (slice 3, 2026-07-13, EW-only).** Unpartitioned Change-Data-Feed
  tables now take merge-on-read too: `UpdateViaVectorsAsync` emits **`update_preimage`/`update_postimage`
  cdc files per affected file** — the exact copy-on-write shapes (pre = the matched OLD rows from the
  source batch, post = the substituted rows; `CdfWriter.WriteAsync` handles the physical rename + the
  unmapped `_change_type` column) — and since a commit carrying ANY cdc action is read cdc-ONLY, the DV
  re-add and the post-image add never double-count in the feed (verified: the update commit's feed is
  EXACTLY one pre + one post; the full feed = 5 inserts + 1 pre + 1 post). Row-tracking identity is
  preserved on CDF tables now too. The MoR gate is down to: DV-enabled AND NOT (CDF × PARTITIONED — the
  cdc partitionValues/column semantics corner, same as the buffered path, slice 4) AND no type widening
  AND not IcebergCompat. **The full matrix from the plan is now: mapping (name+id) ✓ / partitions ✓ /
  CDF-unpartitioned ✓ / identity ✓ / row tracking + materialize ✓ — autocommit AND explicit txn; remaining:
  CDF × partitioned (slice 4) + the type-widening validation pass.**
  `verify_delta_row_tracking_virtual.test` now 127 (CDF MoR preservation + the exact cdc-only feed pins +
  the CDF×partitioned CoW-NULL pin as the last remaining shape); changes 73 / update / dv_default / dv /
  variant / column_mapping / delete / materialize / row_tracking / native_write / partition /
  transactions 934 + EW 168 & 147 green.
  **FABRIC LIVE VALIDATION — FULL PASS (2026-07-13, workspace Test / LH; + ONE REAL EW FIX).** The whole
  2026-07-13 row-tracking/merge-on-read block validated on all three surfaces. Our provider created on
  OneLake: `lake.dbo.fabricator_rtdef` (pure defaults → mapped + DV + materialized row tracking, MoR UPDATE),
  `fabricator_rtpart` (partitioned MoR + partition-key SET US→APAC), `fabricator_rtcdf` (CDF MoR). **Spark
  (Livy, sparkprobe `rtmatrix`)**: reads every table with EXACT `_metadata.row_id`/`_metadata.
  row_commit_version` (id 2 preserved/ver 2; the APAC-moved row keeps id 4; mapped PARTITIONED partition
  column reads fine — Spark, unlike kernel), `table_changes` shows exactly pre+post for the MoR commit,
  and **Spark WROTE BACK** (UPDATE id=3 → Spark itself preserved row_id 3 honoring OUR materialized
  declaration; INSERT → fresh id from the continued HWM). **SQL endpoint** (after the usual metadata-sync
  lag): all three tables registered + queryable incl. post-Spark state. **Our read-back of Spark's write
  initially 404'd — a REAL pre-existing EW bug: on-disk (`storageType "u"`) deletion vectors were
  unreadable** (never exercised — EW only writes inline DVs; Spark's DV UPDATE writes `.bin` files). Three
  spec bugs in `DeletionVectorReader`: resolved to `_delta_log/` (spec: TABLE ROOT + the optional
  random-prefix DIRECTORY from pathOrInlineDv's leading chars), UUID built with .NET's little-endian
  `Guid(byte[])` (spec: canonical big-endian/Java rendering — now formatted by hand), and the offset slice
  ignored the on-disk framing (spec: `<dataSize: 4-byte BE int><bitmap><CRC-32>` with offset at the size
  field — now detected by the size-field match, bitmap extracted, CRC unverified; raw-slice fallback kept).
  With the fix our reader matches Spark's own view byte-for-byte through Spark's u-DV. dv 48 / dv_default
  58 / update 63 / delete 28 / changes 73 / native_read 88 unregressed; validation tables left on LH for
  inspection.
  **MERGE-ON-READ UPDATE: CDF × PARTITIONED LIFTED (slice 4 — the FULL MATRIX is complete) + ON-DISK
  DELETION VECTORS FIXED BOTH WAYS (2026-07-13, EW + Bridge guards).** The last MoR gate: cdc files now
  follow the DATA-FILE convention on partitioned tables — new `CdfWriter.WriteSplitAsync` splits the
  change rows by partition (rows arrive WITH partition columns from the read paths; `SplitByPartition`
  excludes them from the file bytes; per-file `partitionValues` physical-keyed under mapping), which also
  makes a partition-key SET's update_postimage land in its NEW partition's cdc file (feed shows pre=EU,
  post=APAC). ALL cdc emission sites route through it (MoR + CoW UPDATE pre/post, rowid + DV DELETE,
  predicate UpdateAsync, the buffered `WriteChangeDataFileAsync` wrapper — now returns the split list);
  `CdfReader.ReadCdcFileAsync` re-adds partition columns from the cdc action's partitionValues
  (presence-checked for legacy baked-in files; the file's `_change_type` column detached around
  AddPartitionColumns since Spark cdc files may mix change types per row). Bridge: the buffered
  partitioned-CDF guards lifted (`CdfEnabled` probe + `EnsureBufferedDmlEligible` keep only
  identity/IcebergCompat) — buffered INSERT+UPDATE+DELETE on a partitioned CDF table fuses into ONE
  commit with an exact feed. The MoR gate is now just: DV-enabled, not IcebergCompat, no type widening.
  **The full matrix (DV + mapping name/id + partitions + CDF + row tracking + identity, autocommit AND
  explicit txn) is COMPLETE**; the only shapes still falling to CoW (row tracking lost) are type-widened +
  IcebergCompat — neither creatable by this provider, so the CoW-NULL pin is retired.
  **On-disk ("u") deletion vectors — BOTH sides were spec-broken, found by the slice-4 gate + the Fabric
  write-back:** the READER fixes (table root + prefix dir, canonical big-endian UUID, the
  `<version><size BE><bitmap><CRC>` framing) landed in the Fabric-validation pass; this pass fixed the
  WRITER (`DeletionVectorWriter.CreateFileAsync` wrote a raw blob to `_delta_log/` under a
  little-endian-Guid name — internally consistent with the old reader, unreadable by Spark/kernel; any
  DELETE whose bitmap exceeded the 1 KB inline threshold has been writing these all along). Now writes the
  spec shape (table root, canonical name, framing + CRC-32 via System.IO.Hashing — new DeltaLake package
  ref); the reader keeps a LEGACY fallback (`_delta_log/` + little-endian name) so old tables read.
  **delta-kernel now reads our large-DV deletes exactly** (20k rows, 6667 scattered deletions → on-disk
  DV; count + predicate exact) — previously every external reader broke on them. VACUUM safety checked:
  it only sweeps `.parquet`, so live `.bin` DVs are never deleted (orphaned `.bin`s stay uncollected — the
  documented gap, now with real orphans possible).
  `verify_delta_row_tracking_virtual.test` now 165 (partitioned-CDF MoR preservation + the partition-move
  feed + the buffered fused feed); transactions 934 / changes 73 / dv 48 / dv_default 58 / update /
  delete / optimize / partition / partition_overwrite / column_mapping 251 / variant 133 / native_write
  147 / native_read 88 / time_travel / snapshots + EW 168 & 147 (all TFMs) green.
  **P5 REWRITE GAP CLOSED — COPY-ON-WRITE DELETE/UPDATE MATERIALIZE ROW-TRACKING IDS (2026-07-14, EW +
  Bridge rewriter; motivated by PolyBase-recipe tables synced to Fabric via SHORTCUT needing update-stable
  ids).** Both CoW loops (`DeleteByRowIdsAsync` survivors + `UpdateByRowIdsAsync` rewrites) now bake each
  row's ORIGINAL `__delta_row_id` + `__delta_row_commit_version` into the rewritten file (the compaction
  rule; an UPDATED row's version = the new commit) AND assign fresh `baseRowId`/`defaultRowCommitVersion`
  on the new add + the HWM domainMetadata (the old CoW add carried NONE — a spec gap). Per-row source =
  the source file's materialized value where present (chained rewrites carry through) else
  `baseRowId + originalPosition`; NULL when underivable (pre-row-tracking sources — readers then derive a
  fresh id for exactly that row; the materialized columns are written NULLABLE for this,
  `AddRowIdAndCommitVersionColumns(nullable:)`). **Two byte paths:** codec — `ReadFileAsync`/
  `ProcessFileBatchesAsync` gained a `strippedVersionsOut` collector (the version column was dropped by
  the schema reconcile with its VALUES lost) and the loops build row-aligned arrays from in-hand
  positions; native — **`IDataFileRewriter.ReadRewriteAsync` gained an optional `RowTrackingRewrite`
  record** (SourceBaseRowId, SourceDefaultCommitVersion, NewCommitVersion) and the Bridge
  `NativeParquetDataFileRewriter` projects the two columns IN SQL (`COALESCE(source materialized,
  base + file_row_number)`; the UPDATE join's CASE sets the new version on matched rows), returned
  TRAILING per the contract and detached around clean/ToPhysical EW-side (`DetachRowTrackingColumns`);
  stats collect over the detached user batches. Gated on the table's DECLARED materialized column
  (config-driven — old undeclared tables just get the fresh base/default spec fix). **VALIDATED:** smoke
  incl. the chained rewrite (2nd UPDATE of an already-rewritten file keeps ids + old versions); kernel
  reads the outputs; **Spark reads the OneLake CoW table's `_metadata.row_id` PRESERVED while showing the
  mechanics** (`base_row_id=12` fresh + shuffled `row_index`, yet row_id = original — the override
  working); **PolyBase OPENROWSET reads the rewritten file with BOTH extra columns exactly**
  (`verify_mssql_s3_polybase` §5c extended, 137 — the update-stable-ids × protocol-1.0 combination now
  COMPOSES; the id pin is RELATIVE (+1 between consecutive rows) since the persistent bucket's HWM grows
  across re-runs). `verify_delta_row_tracking_virtual` now 247 (CoW preservation on the PolyBase recipe,
  both writer modes, chained); full sweep green (delete 28 / update 63 / dv 48 / dv_default 58 / changes
  73 / native_write 147 / native_read 88 / column_mapping 251 / variant 133 / compaction 24 / materialize
  17 / row_tracking 33 / optimize 40 / partition 54 / constraints 50 / transactions 934) + EW 168 & 147.
  The ONLY remaining CoW-without-id-preservation shapes: type-widened + IcebergCompat (not creatable by
  this provider). **The BUFFERED-path post-OPTIMIZE caveat is CLOSED too (same pass):**
  `ReadRowsByRowIdsAsync` gained an optional `sourceRowTrackingOut` collector (one row-aligned entry per
  yielded batch: original id/version = the source's MATERIALIZED value else baseRowId + position — plain
  `long?[]` value arrays — trivially lifetime-free), threaded through
  the Bridge wrapper; `BufferUpdateRows` now takes its stableIds from the read-back (the
  `GetOrderedActiveBaseRowIds` ordinal-arithmetic call is gone from that path) — so a buffered UPDATE of
  a COMPACTED file bakes the ORIGINAL id (pinned: post-OPTIMIZE buffered UPDATE keeps `__delta_row_id=4`).
  ANY unresolvable row (pre-row-tracking source) disables materialization for the whole statement (fresh
  ids — never a wrong/colliding id; replaces the old `?? 0` flaw).
  **ROW TRACKING × PolyBase — WORKS (probed live + pinned 2026-07-13):** a `row_tracking true` table
  (writer v7, minReader stays 1) on the PolyBase recipe (`deletion_vectors false, column_mapping 'none'`)
  reads exactly via SQL Server OPENROWSET — INCLUDING after OPTIMIZE materializes the physical
  `__delta_row_id` column into the compacted file (the reader ignores extra parquet columns not in the
  Delta schema). So the full SQL-Server-facing lifecycle is: CoW DML + identity + CDF + row tracking all
  OK; only DV (reader v3) and column mapping are hard rejections. `verify_mssql_s3_polybase` §5c (129).
  **Slice-4 LIVE VALIDATION — FULL PASS (2026-07-13, workspace Test / LH):** our provider created on
  OneLake `lakecdf.dbo.fabricator_rtcdfp` (partitioned CDF + row tracking: MoR UPDATE, partition-key SET
  US→APAC, DV DELETE) and `lake.dbo.fabricator_bigdv` (20k rows, scattered DELETE → the new SPEC-SHAPED
  ON-DISK DV over OneLake). **Spark** (sparkprobe `dvcdfp`): reads the on-disk DV exactly (13333/1/19999,
  zero deleted ids) and `table_changes` returns the partitioned MoR feed byte-identical to ours (per-
  partition cdc incl. the EU→APAC pre/post pair + the delete; `_metadata.row_id` preserved). **SQL
  endpoint**: reads BOTH tables exactly — including decoding the on-disk `.bin` deletion vector (the
  endpoint's DV support covers our inline AND on-disk forms) and the partitioned-CDF post-DML state.
  **ROW TRACKING NOW IMPLIES MATERIALIZATION — `materialize_row_tracking` DROPPED (2026-07-13, C#-only).**
  Spark parity: `delta.enableRowTracking` promises ids stable across rewrites, implemented via the
  materialized columns (Spark auto-declares them at enablement) — our opt-in split was Fabric-conversion
  caution that's since been validated away. Now `DeltaWriter.CreateConfig` declares
  `delta.rowTracking.materializedRowIdColumnName`/`...RowCommitVersionColumnName` WHENEVER row tracking is
  on (standalone `row_tracking true` OR the `deletion_vectors` default) — so **default-created tables
  preserve `__delta_row_id` across UPDATE/OPTIMIZE out of the box** (verified: pure-default catalog, id
  preserved + version bumped, kernel-exact). The ATTACH option is REMOVED (an old `materialize_row_tracking
  true` in an ATTACH is silently ignored — providers parse known keys only); the buffered-UPDATE bake gate
  switched from the catalog flag to the TABLE's declared materialized column (`TxnDmlProfile` gained
  `MaterializeRowIds`) — so tables created by OLDER versions without the declaration keep their old behavior
  (config-driven, never an undeclared physical column). Appends still never materialize (readers derive
  baseRowId + position). Tests updated (the option stripped from materialize_rowtracking 17 /
  compaction_rowtracking 24 / transactions §34-§36 / row_tracking_virtual 127 — all pass on the implied
  default); sweep green (update / dv_default / dv / variant / column_mapping 251 / changes / delete /
  native_write 147 / native_write_streaming 29 / native_read / partition / partition_overwrite 90 /
  optimize 40 / identity / constraints / copy_format 96 / transactions 934 / row_tracking 33).
  **COLUMN MAPPING on the native read — DONE (2026-07-05, C#-only, no ABI; live Fabric-Spark-validated).** A
  column-mapping Delta table (`delta.columnMapping.mode` = `name` or `id`) stores columns under PHYSICAL names
  (`col-<guid>`), so the plain `read_parquet` SELECT-by-logical-name failed (*"Referenced column 'id' not found;
  candidates: col-…"*). Fixed by mimicking the MultiFileReader's identifier mapping in the C# reader: `DeltaReader`
  captures logical→physical from THIS snapshot's field metadata (`delta.columnMapping.physicalName`, read DIRECTLY
  — `ColumnMapping.GetPhysicalName` returns the logical name in id mode since EW matches id mode by field-id, but
  Delta writes a physicalName in BOTH modes and Spark stores the parquet columns under it) onto `NativeScanList.
  LogicalToPhysical`; `DeltaNativeReader.FileSql` then reads through an **inner alias subquery** `(SELECT "phys" AS
  "logical", …, file_row_number FROM read_parquet(…))` so the OUTER projection, user filter (incl. filters on a
  mapped/renamed column), rowid, and DV condition all reference logical names unchanged — no filter rewrite, and
  DuckDB pushes the outer filter down into `read_parquet` (mapped to the physical column) for row-group pruning.
  Validated LIVE on Fabric Spark-created tables (`fabricator_cm_name` name mode + `fabricator_cm_id` id mode, each with
  a post-create column RENAME so logical≠physical): both read correctly via `native_read`, incl. filter on the
  mapped column + aggregation. The default (EW) reader already handled both modes (`RenameColumns` for name /
  `RenameByFieldId` for id) — confirmed on the same tables. **Top-level columns only** (a nested mapped column
  would need field-id matching via `parquet_schema.field_id` — deferred, clean follow-up mirroring
  duckdb-delta's `MultiFileColumnMapper`). Spark-created via the `scratchpad/sparkprobe createcolmap` harness.
  **CREATING a column-mapping table — the `column_mapping 'name'` ATTACH option — DONE (2026-07-05; live
  Fabric-Spark round-trip).** `ATTACH … (PROVIDER 'delta', column_mapping 'name')` → tables CREATED in the catalog
  enable Delta column mapping (`DeltaCatalog._columnMappingMode` → `DeltaWriter.Create`/`Write` → EW
  `OpenOrCreateAsync(columnMappingMode:)` → `CreateAsync` assigns physical `col-<guid>` names + bumps the protocol).
  Two EW fixes made the created table Spark-readable (our lenient reader tolerated the gaps; Spark didn't):
  (1) `CreateAsync` now DECLARES `columnMapping` in BOTH the reader + writer feature lists when in table-features
  mode (reader v3 / writer v7 — forced by the DV default), else Spark throws `DELTA_FEATURES_PROTOCOL_METADATA_MISMATCH`;
  (2) `DeltaSchemaSerializer` emits `delta.columnMapping.id` as a JSON NUMBER (Delta field-id is numeric — Spark's
  `Metadata.getLong` threw "String cannot be cast to Long" on the old string form). The write rides the existing
  EW-codec `RenameToPhysical` path.
  **COLUMN MAPPING IS THE DEFAULT — `column_mapping 'name'` (DEFAULT since 2026-07-06) | `'id'` | `'none'`.**
  Tables CREATED in a Delta catalog default to **name-mode column mapping**, so `ALTER TABLE … RENAME
  COLUMN` / `DROP COLUMN` work out of the box as **metadata-only commits** (no rewrite) AND the table stays
  readable by the **Fabric T-SQL endpoint — which REJECTS id-mode tables outright**
  (`UnsupportedColumnMappingMode`, user-validated live 2026-07-06; Spark/kernel/DuckDB read both modes).
  The default was `id` from 2026-07-05 until this finding; id stays opt-in for its field-id resolution.
  Opt out with `column_mapping 'none'` for a plain minimal-protocol table.
  (`verify_delta_catalog_column_mapping.test` asserts the name default; the explicit-id sections keep
  id-mode coverage.) The original id-default rationale + mechanics (all mode-agnostic — physical names +
  field_ids are written in BOTH modes):
  - **EW spec fix — id-mode files now use PHYSICAL names** (the earlier "id writes logical names" behavior was an
    EW spec violation: the Delta protocol requires physical names + parquet `field_id` in BOTH modes — Spark's
    id-mode files are `col-<guid>`+ids). `ColumnMapping.GetPhysicalName`/`BuildLogical↔PhysicalMap`/
    `FindFieldIdForArrowField` are now mode-agnostic (mapping-on ⇒ physical), so every EW write path (codec write,
    copy-on-write rewrite, DV update) emits physical names + field_ids for id mode too. **Proven with the
    reference reader**: DuckDB's official `delta_scan` (delta-kernel-rs — what Spark/Fabric use) reads our
    default-id tables correctly incl. after RENAME+ADD+DROP (it read ALL-NULLs on the old logical-named layout —
    the bug that triggered this pass). The old `'id'-REJECTED` note above is SUPERSEDED.
  - **Field-id native read**: `NativeScanList.LogicalToFieldId` (id mode; `LogicalToPhysical` stays for name
    mode) + `DeltaNativeReader.LogToPhys` probes each file's `parquet_schema` (`field_id → physical name`,
    footer-only) and composes logical→field_id→physical for the alias subquery — per-file, so mixed-vintage files
    across a RENAME read correctly, and both EW-created and external-Spark id layouts work.
  - **RENAME/ADD/DROP COLUMN under mapping** (all metadata-only): EW `RenameColumnAsync` (keeps id+physicalName),
    `AddColumnAsync` now assigns a fresh id (`max(columnMapping.maxColumnId, schema-max)+1`) + physicalName +
    bumps maxColumnId, new `DropColumnAsync` (retires the id; partition columns + last column rejected);
    `BackfillMissingColumns` reconciles EXTRA columns away too (DROP) — not just missing (ADD). Plain tables:
    RENAME/DROP still cleanly rejected (would need a full rewrite); ADD works as before. Bridge:
    `DeltaCatalog.AlterTable` gained RenameColumn/DropColumn cases → `DeltaReader.RenameColumn`/`DropColumn`.
  - **`native_write` STREAMS mapping tables (both modes)**: the COPY projection aliases `"logical" AS "physical"`
    (`NativeParquetDataFileWriter.SelectList`) + stamps `FIELD_IDS {'<physical>': id}`; stats are typed/keyed by a
    physical-renamed schema (spec: stats keys are physical). EW `SupportsExternalDataFileCommit` now allows
    mapping (caller contract: physical names + field_ids + physical-keyed stats documented on the property);
    `TryWriteStreaming` passes `columnMappingMode` to OpenOrCreate (a NEW table is created WITH the mode), skips
    `SetSchemaAsync` for a same-shape mapping replace, and falls back to collect for partitioned-mapping or a
    mapping replace that CHANGES the schema. EW `SetSchemaAsync` now supports mapping (re-assigns fresh field ids
    continuing from maxColumnId — sound for REPLACE since the paired Overwrite removes the old files) with a
    LOGICAL-shape no-op compare (`LogicalSchemaString` — the SchemaString always differs by ids). **`CREATE OR
    REPLACE` with a changed schema works on mapping tables** (collect path).
  - **Compaction (OPTIMIZE) is mapping-aware**: `CompactionExecutor` widens against a physical-renamed target
    schema, reconciles each source batch to the current column set (`BackfillMissingColumns` — mixed ADD/DROP
    vintages compact correctly; also fixes plain-table compaction-after-ADD, a pre-existing hole), and rebuilds
    CLEAN + `SetParquetFieldIds` so the compacted file KEEPS the mapping identity (without this it read back
    all-NULL). **CDF is mapping-aware**: `CdfReader` renames physical→logical on inferred (data-file) + `_change_data`
    reads (`ReadChangesAsync` passes the current `physicalToLogical`).
  Validated: `test/verify_delta_catalog_column_mapping.test` (122 — name/id create+read, physical names + field_ids
  in the parquet for BOTH modes, RENAME across mixed-vintage files via default+native readers, default=id declares
  the mode + RENAME/ADD/DROP + OPTIMIZE/VACUUM round-trip, id+native_write streams with FIELD_IDS, 'none' opt-out,
  unknown-mode error); `verify_delta_catalog_alter.test` (116 — ADD/RENAME/DROP positive, TYPE rejected); FULL
  delta suite 34/34 + SQL Server function suites green. Raw-file-shape tests pinned `column_mapping 'none'`
  (native_write/streaming/dv_default/materialize_rowtracking/optimize/compaction_rowtracking/overwrite_merge/
  mfr_dv — they assert physical parquet layouts or use the non-mapping-aware C++ MFR spike). Live: OneLake
  default-id CTAS + RENAME + ADD + post-rename INSERT round-trip (`lake.dbo.fabricator_cmidlive`, native_write
  streaming).
  **PARTITIONED + mapping (second pass, same day) — STREAMS + full Spark round-trip.** Convention settled
  EMPIRICALLY against a Spark-created partitioned id-mode fixture (`fabricator_cm_part`, via `sparkprobe
  createpartcm` + `_delta_log` inspection): `metaData.partitionColumns` = LOGICAL names (updated by a
  partition-column RENAME), `add.partitionValues` keys = **PHYSICAL** names (stable across the rename — the
  reason they're physical), `add.path` = opaque (Spark writes no Hive dirs under mapping; paths are opaque to
  readers, so our physical-named Hive dirs are spec-fine). Implemented: EW `WriteCoreAsync` tracks
  partitionValues (+ dirs) by physical name under mapping; `RenameColumnAsync` also updates
  `metaData.partitionColumns` (without this a renamed partition column broke reads/writes/kernel);
  `PartitionValuesMatch` (replace_where file selection), `PartitionUtils.AddPartitionColumns` (read-side re-add)
  and `DeltaFilePruner` (partition pruning + stats) all do DUAL lookup (logical | physical key) so old
  logical-keyed EW commits, new physical-keyed ones, AND external Spark tables all resolve. Bridge:
  `TryWriteStreaming` no longer falls back for partitioned mapping — `RunCopyPartitioned` gets the aliased
  projection + `PARTITION_BY (<physical>)` + physical-keyed FIELD_IDS (partition cols excluded — not in the
  files) + physical stats schema; `RETURN_STATS.partition_keys` then come back physical = the committed
  partitionValues. Validated: local smoke (rename of the partition column mid-life, pruning), full delta suite
  34/34 (`verify_delta_catalog_column_mapping` 157 — incl. partitioned CTAS streams/physical dirs/prune/rename/
  durability; `verify_delta_catalog_partition` 54 unchanged), and LIVE both directions: **Spark reads our
  partitioned mapped table** (`fabricator_cmpartlive`, created via native_write streaming with BOTH a data and the
  partition column renamed) AND **our provider reads Spark's** `fabricator_cm_part` (values + agg + pruning on the
  renamed partition column). NOTE: DuckDB's official `delta_scan` (duckdb-delta/kernel) reads mapped PARTITIONED
  tables' partition column as NULL — a kernel-side integration gap (our layout matches Spark's own convention;
  Spark is the reference).
  **NESTED-type column mapping — DONE (2026-07-05, third pass; kernel-validated).** Nested (STRUCT) columns on a
  mapping table now follow the spec at EVERY level: data files store nested struct children under PHYSICAL
  `col-<guid>` names + field_ids (both were LOGICAL/id-less before → delta-kernel read nested children as
  all-NULL — the top-level bug one level down; the Delta metadata was already recursive via
  `AssignColumnMapping`). Per the user's direction the nested WRITE is **native (COPY) only**:
  - **`ArrowColumnMappingRename` (Bridge)** — the C#-side analog of duckdb-delta's `MultiFileColumnMapper`:
    recursive logical↔physical rename of an Arrow schema/batch/stream driven by the EW Delta schema
    (physicalName metadata), matching EITHER name at every level (tolerant of aliased/legacy sources). Arrays
    are rebuilt by re-wrapping `ArrayData` with the renamed type tree — zero copy. Structs recurse to any depth;
    lists/maps recurse into struct elements.
  - **WRITE (streaming COPY)**: `TryWriteStreaming` renames the input STREAM to physical (all levels — replacing
    the old top-level SQL alias) and stamps a RECURSIVE `FIELD_IDS` spec (`DeltaWriter.BuildFieldIdsSpec` —
    struct fields via DuckDB's `__duckdb_field_id` sentinel; list/map emit their own id as a leaf, struct-fields
    INSIDE list/map not yet stamped — follow-up). The **EW-codec (collect) path was GATED for nested+mapping (gate LIFTED 2026-07-06 via
    `ColumnMappingRecursive.ToPhysical` — see Known limitations)**
    (clear error → use `native_write` or `column_mapping 'none'`): EW's writer renames/stamps top-level only, so
    its nested files would be silently Spark-unreadable. Nested WITHOUT mapping stays allowed on both paths.
  - **READ**: the native reader renames nested children physical→logical per batch
    (`NativeScanList.MappedSchema`); the DEFAULT (EW) reader path gets the same recursive rename in
    `DeltaReader.Stream/StreamWithRowIds/StreamAt/StreamWithRowIdsAt` (EW's own `RenameColumns`/`RenameByFieldId`
    are top-level-only — upstream-fix candidate). AT-version streams rename per the AS-OF snapshot's schema.
  - **EW fix**: `LogicalSchemaString` strips mapping metadata RECURSIVELY — without it a fresh nested CTAS
    falsely "differed" in SetSchema's no-op compare and re-assigned every column id (the file/metadata id
    mismatch seen in the baseline probe).
  Verified: `verify_delta_catalog_column_mapping.test` (185 — nested CTAS/INSERT via native_write, member
  projection/predicate, physical names + 4 distinct field_ids in the parquet, struct-column RENAME reading old
  data, native_read nested, the EW-codec gate error); official `delta_scan` (delta-kernel) reads the nested
  mapped table incl. after the RENAME; full delta suite 35/35; live OneLake nested CTAS+RENAME+INSERT
  round-trip (`lake.dbo.fabricator_cmnested`) + Fabric Spark read.
  **BUG FIXED while stabilizing (latent, pre-existing): bulk-session double-complete.** `complete_bulk` CONSUMES
  the session handle managed-side EVEN WHEN IT RETURNS AN ERROR, but the COPY/CTAS/INSERT `Finalize` set
  `bulk_completed = true` only AFTER the call — a thrown provider error left the flag false, so the gstate
  destructor called `CompleteBulk(abort)` AGAIN on the freed value. GCHandle slots are RECYCLED → the second
  free killed an arbitrary unrelated live handle → intermittent (~1 in 4) `commit_transaction failed: stale
  catalog handle` on LATER statements (surfaced by the new PARTITION_OVERWRITE guardrail tests, which made an
  erroring complete_bulk a routine path). Fix: all three operators mark the session consumed BEFORE the call.
  Also `Bootstrap.RunTransactionOp` no longer falls back to `Active.OpenCatalog("")` on an unresolvable handle
  (nonsense "empty connection string" errors) — it now throws a real stale-handle diagnostic incl. what the
  handle resolves to.
  **Nested follow-ups CLOSED (same day, fourth pass):** (1) **CDF on nested mapped tables** — the feed leaked
  physical struct-child names → `DeltaReader.GetChanges` applies the recursive rename (CDF metadata columns pass
  through); (2) **DELETE on nested tables** — EW `DeletionVectorFilter.TakeRows` + `PartitionUtils.TakeRows`
  gained a recursive `StructArray` case (children indexed at parentOffset+r — `StructArray.Fields` does NOT
  incorporate the parent offset; validity rebuilt), so the DV-delete's CDC capture + copy-on-write survivor
  filtering work on struct columns (previously a clean throw); verified insert/insert/delete feed with logical
  nested names; (3) **struct-in-LIST field_ids** — `BuildFieldIdsSpec` renders
  `{'<phys>': {__duckdb_field_id: id, 'element': {<children>}}}` (DuckDB accepts an element dict without its own
  sentinel — the element node has no Delta id, unrepresentable in the Delta schema); list column + element-struct
  fields all carry physical names+ids, kernel-read verified; (4) **EW-codec stats keyed PHYSICAL under mapping**
  (`WriteCoreAsync` collects over the top-level-renamed batch — stats cover top-level primitives only, so the
  flat rename suffices; matches the streaming writer + spec readers' skipping).
  **STRUCT UPDATE + EW parquet-writer bug hunt (fifth pass, same day).** `UPDATE t SET s = {'a':…}` (struct SET
  values) now works on UNMAPPED tables via `ArrowValueReader.ReadScalarDeep` (struct → `Dictionary<string,object?>`
  recursive, deep-copied; kept SEPARATE from `ReadScalar` — filter callers rely on unsupported-type throws
  meaning "don't push") + a `BuildArray` StructType case (children built recursively, validity rebuilt). Works on
  both writers incl. `SET s = NULL`; MAPPED tables initially gated struct-SET (LIFTED 2026-07-06 — see Known
  limitations) (`DeltaReader.IsColumnMapped`
  — the EW-codec rewrite can't produce the spec nested layout; scalar SET on mapped nested tables works).
  Chasing kernel "Out of buffer" on the merge-on-read post-images uncovered **three EW parquet-WRITER bugs**
  (NOT documented limitations — known-issues.md's write-reject list never included struct/list/map; minimal
  repro harness `scratchpad/ewstruct`):
  1. **Null-struct child misalignment** (`NestedLevelWriter`): a null STRUCT row was treated like a null LIST
     (no child slot), but Arrow struct children are 1:1 with parent rows — every child value after a null
     struct row shifted one slot (def levels + values both wrong → file unreadable by DuckDB/kernel; only our
     lenient reader tolerated it). Fixed by threading an explicit per-level VALUE MAP from struct parents
     through struct/leaf/list/map decomposition (+ the sliced-struct case: children are NOT sliced with the
     parent, so the map bakes in `Data.Offset` — same subtlety as the TakeRows fix).
  2. **`ExpandArray` default 8-byte stride**: unknown fixed-width leaf types expanded as `long` — corrupting
     Int8/Int16/Date32/Time32/… whenever expansion triggered. Now width-dispatched; genuinely unsupported
     types throw instead of corrupting.
  3. **All-null pages declared a delta encoding with a 0-byte payload** — DELTA_BINARY_PACKED /
     DELTA_LENGTH_BYTE_ARRAY require a header even for zero values → readers underrun ("Out of buffer"). An
     all-null page now declares PLAIN (the only encoding whose empty representation is valid). This hit ANY
     all-null column page (e.g. a merge-on-read post-image whose struct is NULL, an all-null flat column page).
  **Plus the cheap known-issues interop fixes** (user: "if we can fix EW issues then fix"): ns-timestamp/time
  `converted_type` OMITTED (no ns variant exists — was mislabeled micros, 1000x for converted_type-trusting
  readers); `SchemaConverter.FromArrowField` PRESERVES per-field metadata (comments/mapping ids/invariants;
  `PARQUET:*` transport keys filtered); deprecated `Statistics.min`/`max` restricted to signed-order-safe types
  (parquet-mr parity — UTF-8/binary/unsigned/decimal-FLBA get `min_value`/`max_value` only); the stale
  known-issues `column_orders`-never-written entry removed (ColumnOrderBuilder already populates it);
  known-issues.md updated accordingly.
  Verified: repro trio (all-null / mixed / no-null structs) reads in DuckDB; struct UPDATE round-trips +
  kernel-reads on both writers; `verify_delta_catalog_column_mapping` 232 assertions; full delta suite 35/35 +
  SQL suites; EW's own parquet test suite.
  **Known limitations (2026-07-06 wrap-up):** a mapping REPLACE that CHANGES the schema now STREAMS
  (`TryWriteStreaming` adopts the new schema via `SetSchemaAsync` BEFORE building the maps/FIELD_IDS/COPY —
  metadata commit then streamed Overwrite, kernel-validated with a full schema swap incl. nested struct) and
  `ToArrowField` (Delta→Arrow) preserves per-field metadata (the reverse of `FromArrowField`). Deliberately
  REMAINING: binary partition values (clean error — the spec byte-escape encoding is not implemented; exotic),
  orphan DV `.bin` vacuum (we only write inline DVs — no `.bin` files to orphan). **Nested-field stats are
  BUILT (EW `850ffad`):** `StatsCollector` recurses into struct leaves — spec nested JSON objects for
  minValues/maxValues/nullCount; nullCount EXACT per leaf (parent-null OR child-null — IS NULL pruning needs
  exactness); min/max via the flat collectors (parent-null slots can only WIDEN bounds → superset, prune-safe);
  32-char string truncation at every level; physical-keyed at every level under mapping (the codec stats batch
  now goes through the recursive `ToPhysical`). Applies to the EW-codec/collect + rewrite/compaction paths;
  the streamed native COPY keeps DuckDB's `RETURN_STATS` (top-level — DuckDB reports no nested column stats;
  a follow-up if it ever does). Kernel reads the nested-stats tables fine.
  **Closed 2026-07-06:** (a) **map-of-struct field_ids on the COPY FIELD_IDS spec** — `BuildFieldIdsSpec`
  renders map fields as `{__duckdb_field_id: id, 'key': …, 'value': …}` (+ `AppendInnerNode` recursion for
  arbitrarily deep list/map-of-struct; structural element/key/value nodes carry no id, per DuckDB); validated:
  a `MAP(VARCHAR, STRUCT)` column under id mapping + native_write writes inner struct children physical-named
  with their ids (parquet_schema-verified) and kernel-reads. (b) **EW's own reader now renames nested levels**
  — `ColumnMappingRecursive.ToLogical` (the read direction of the same transform, no id stamping) is wired
  into EW `ReadFileAsync` (after the flat `RenameByFieldId`/`RenameColumns`), the `UpdateAsync` predicate
  read, and `CdfReader`'s three feed yield sites — EW standalone now returns logical nested names; the
  Bridge's `ArrowColumnMappingRename` read-side wraps stay as tolerant no-ops. **BOTH former gates are LIFTED (2026-07-06,
  kernel-validated, `column_mapping` test 251):** (a) **struct SET on MAPPED tables works** — new EW
  `ColumnMappingRecursive.ToPhysical` (recursive physical rename + PARQUET:field_id at EVERY level, tolerant
  either-name matching, zero-copy ArrayData rewrap — the EW-side sibling of the Bridge's
  `ArrowColumnMappingRename`) replaced the top-level-only `RenameToPhysical`+`SetParquetFieldIds` pair at all
  mapped write sites (CoW DELETE/UPDATE rewrites, UpdateViaVectors append, UpdateAsync, WriteCoreAsync codec,
  CdfWriter); PLUS the crux found in validation: EW hands the rewrite callback source batches with PHYSICAL
  nested child names (EW read rename is top-level only), so `BuildArray`'s logical-name carry-over of
  NON-updated rows read NULL — `DeltaReader.UpdateByRowIds` now wraps the callback to rename source batches
  to LOGICAL first (recursive, Bridge `ArrowColumnMappingRename`), EW's recursive ToPhysical converts back.
  Works across a column RENAME; pass-through rows keep values; kernel reads exact. (b) **the EW-codec
  (collect) nested+mapping write gate is lifted** — WriteCoreAsync now writes nested physical names + ids, so
  a nested CTAS/INSERT on a mapped table works WITHOUT `native_write` (kernel-validated). (CDC `_change_data`
  files are written PHYSICAL-named + field_id'd under mapping via the same recursive transform in `CdfWriter`;
  `_change_type` stays unmapped; Spark reads cdc parquet through the table mapping.)
  **WRITING to a Spark-created (external) table — DONE (2026-07-05, EW-only; live Fabric-Spark round-trip).** An
  INSERT initially failed at engineered-wood's `ProtocolVersions.ValidateWriteSupport`: *"unsupported writer
  features: [appendOnly, invariants]"*. Root cause (grounded in the table's `_delta_log` protocol): enabling
  `columnMapping`+`deletionVectors` upgrades the table to the writer-v7 "table features" protocol, which
  ENUMERATES the legacy writer-v2 features `appendOnly` + `invariants` EXPLICITLY — even though neither is ACTIVE
  (`delta.appendOnly` isn't set; no constraints declared). EW's allowlist just lacked the two names. Fix:
  (1) added `appendOnly`/`invariants`/`checkConstraints` to `SupportedWriterFeatures`; (2) new
  `DeltaTable.HonorWriterFeatures(isAppend)` (called from `WriteCoreAsync`/`CommitDataFilesAsync` + the rowid
  DELETE/UPDATE entrypoints) enforces them **only when active**: `appendOnly` → a non-append write throws when
  `delta.appendOnly=true`; `invariants`/`checkConstraints` (arbitrary SQL expressions in a column's
  `delta.invariants` metadata / `delta.constraints.*` config — which we can't evaluate in the C# writer) → the
  write is REJECTED with a clear error rather than silently writing possibly-violating data (Delta constraints
  are write-time-only; NOT NULL is schema nullability, unaffected). A table that merely LISTS the features (the
  common v7-upgrade case) writes normally. **Column-mapping WRITE rides the existing EW-codec collect path**
  (`WriteCoreAsync` → `RenameToPhysical` for name mode / `SetParquetFieldIds` for id mode) — no new alias/FIELD_IDS
  code needed. Validated LIVE: INSERT (4,'d'),(5,'e') into the Spark name-mode table `fabricator_cm_name` via our
  provider → committed v3 → **Spark reads all 5 rows back through the column mapping** (round-trip). Local Delta
  write/delete/update/dv/native_write suites unregressed (HonorWriterFeatures is a no-op on our own mode=none
  tables). **STREAMING native write to a mapping table stays on the collect path** (`SupportsExternalDataFileCommit`
  is false for mapping → `Materialize`+EW codec, correct but RAM-bounded); bounded-memory streaming to an EXTERNAL
  mapping table (COPY `FIELD_IDS` + physical-name alias) is deferred — niche (you bulk-write tables our provider
  creates, which stream; external mapping tables are typically read).
  v56 = **`onelake://` WRITE forward callbacks** — appended 3 vtable entries
  `onelake_open_write`/`onelake_write`/`onelake_close_write`; the C++ `FabricatorOneLakeFileSystem` `OpenFile(write)`/
  `Write` (sequential append → managed `OneLakeForwardFs` create/append/flush) make **`COPY … TO 'onelake://…'` +
  any DuckDB writer** write to OneLake (Phase-3 step-3 slice 2; live-validated: COPY a parquet, read back 5/5).
  `read_csv`/`read_json` reads already worked via the slice-1 OpenFile/Read path, so any reader/writer now works on
  OneLake. Read caching via DuckDB's `ExternalFileCache` was confirmed already transparent on the native
  `read_parquet('onelake://…')` path (`duckdb_external_file_cache()` shows the file cached). Non-sequential writes +
  directory ops throw; caching engineered-wood's reverse `fs_*` reads deferred (low value). v55 = **`onelake://` FileSystem forward callbacks** — appended 5 vtable entries
  `onelake_open`/`onelake_read`/`onelake_close`/`onelake_glob`/`onelake_exists` (host C++ → managed). A C++
  `FabricatorOneLakeFileSystem : FileSystem` (`src/fabricator/fabricator_onelake_fs.{hpp,cpp}`) is registered in DuckDB's
  VFS at load (`RegisterOneLakeFileSystem`, `CanHandleFile` = the `onelake://` scheme) and forwards its **read** ops
  to the managed Azure DataLake SDK (`OneLakeForwardFs`, reusing the step-2 `OneLakeDataLakeFileSystem` logic) — so
  DuckDB's **native parquet reader + ExternalFileCache** use OneLake uniformly, bypassing duckdb-azure. Credential =
  C++ resolves the azure secret from the calling opener (`SecretManager::LookupSecret(path,"azure")`, fallback to any
  `azure` secret since `onelake://` doesn't match azure's default scopes) → fields JSON → C# `FabricCredentialResolver`
  (empty ⇒ `DefaultAzureCredential`). This is Phase-3 step 3 of the OneLake-filesystem design
  ([docs/filesystem-bridge.md](docs/filesystem-bridge.md) §3): the C#→C++→C# path (OneLake IO logic in C#, registered
  as a C++ FileSystem, reached back in C#). **Slice 1 (read-only) DONE + live-validated on Fabric**:
  `SELECT count(*) FROM read_parquet('onelake://Test/LH_no_schema.Lakehouse/Tables/t/*.parquet')` reads OneLake Delta
  data files through DuckDB's native reader over the subsystem (no duckdb-azure); local regression green. Write ops on
  the C++ FS throw NotImplemented; slice 2 (caching the reverse `fs_*` reads, routing the Delta catalog through
  `onelake://`, `read_json`/`csv`/`excel`/`COPY TO`) deferred. v54 = **`SCHEMA_MODE` COPY option** — appended a `schema_mode` param to
  `begin_bulk` (nullable: "merge" | "overwrite"); the Delta provider does append+union / replace+adopt-schema,
  and `CREATE OR REPLACE` is now a true schema replace via engineered-wood `SetSchemaAsync`. Provider-agnostic —
  SQL Server / DAX ignore it. See the Delta partitioning/write bullet ("SCHEMA EVOLUTION on write"). v53 = **IDENTITY columns** — appended an `identity_columns` param (nullable,
  comma-separated) to `create_table` (begin_bulk NOT changed — CTAS has no generated columns; the auto-identity
  is a C#-side setting). DuckDB has no IDENTITY concept, so TWO mechanisms: **(1) a DuckDB GENERATED column**
  (`col BIGINT AS (0)`) is (mis)used as an IDENTITY MARKER — the C++ DDL `CreateTable` detects `col.Generated()`
  (binder allows generated columns on an attached catalog; no capability gate) and passes the name(s); the
  generated-ness exists only at create time (the table is re-fetched from SQL Server as a normal identity BIGINT
  afterward). **(2) an `add_identity` ATTACH option + `mssql_add_identity` SET** (`ResolveAddIdentity`: SET wins)
  auto-appends a `<table>_id BIGINT IDENTITY` surrogate key on CREATE + CTAS, skipped when a column is explicitly
  marked OR the target name already exists. C# `BuildCreateTable` emits **box `IDENTITY(1,1)` / Fabric bare
  `IDENTITY`** (Fabric supports only BIGINT IDENTITY, no seed/increment) via `IdentityClause(profile)`; identity
  columns are always BIGINT, no NULL/DEFAULT. The engine assigns values on INSERT — the identity column is absent
  from the source Arrow stream, so SqlBulkCopy's name-based `ColumnMappings` naturally skips it (works for
  explicit CREATE + INSERT and CTAS). The **read + insert paths already handle IDENTITY** (discovery +
  `KeepIdentity`), so only CREATE-side emission was new. Delta / DAX ignore `identity_columns`. Validated:
  `test/verify_identity.test` (45 — marker→IDENTITY [IsIdentity=1], add_identity SET, CTAS, skip-if-present,
  OFF=no column) + SqlServer/Delta suites unregressed; **live on Fabric Warehouse** (generated marker → real
  IDENTITY, 3 distinct auto values; add_identity CTAS → `fabricator_idfab2_id`, 4 distinct). v52 = **native `SORTED BY` → Fabric Warehouse `CLUSTER BY`** — appended a
  `sort_columns` param (nullable, comma-separated) to **both** `create_table` and `begin_bulk`, mirroring the v51
  `partition_columns`. DuckDB v1.5.4 parses `CREATE TABLE [t] SORTED BY (cols) [AS …]` into
  `CreateTableInfo::sort_keys`; `FabricatorCatalog::SupportsCreateTable` now permits BOTH partition_keys AND
  sort_keys (only the WITH-options clause stays rejected). C++ extracts the sort columns (reusing
  `fabricator::PartitionColumnsArg`) in the DDL create + CTAS and passes them; the **SQL Server provider maps them
  to a Fabric Warehouse / Synapse `WITH (CLUSTER BY (cols))`** layout in `BuildCreateTable` — **only on a
  warehouse profile** (`profile.IsWarehouse`; box SQL Server has no such syntax → the clause is a no-op there),
  for both explicit CREATE and CTAS. A **`mssql_cluster_by` session setting** (comma-separated columns) is the
  fallback when there's no native clause (`ResolveClusterColumns`: native SORTED BY wins). Delta / DAX ignore
  `sort_columns`. Validated: `test/verify_cluster_by.test` (18 — box plumbing + graceful no-op), columnstore +
  Delta suites unregressed, and **live on Fabric Warehouse**: `CREATE TABLE … SORTED BY (CustomerID, SaleDate)` +
  CTAS both accepted (data correct), and a bad cluster column is **rejected by Fabric** (error 1911 — proving the
  `WITH (CLUSTER BY …)` clause is emitted, not dropped). v51 = **native `PARTITIONED BY`** — appended a `partition_columns` param (nullable,
  comma-separated column names) to **both** `create_table` and `begin_bulk` (a signature change, not a slot add).
  DuckDB v1.5.4 parses `CREATE TABLE [t] PARTITIONED BY (cols) [AS …]` into `CreateTableInfo::partition_keys`
  (clause precedes `AS` for CTAS); the base `Catalog::SupportsCreateTable` REJECTS any partition_keys, so
  `FabricatorCatalog::SupportsCreateTable` is overridden to **permit** them (SORTED BY + WITH-options stay
  unsupported). C++ extracts the column names (`fabricator::PartitionColumnsArg`, `catalog/fabricator_partition_util.hpp`
  — column-refs only) in the DDL create (`FabricatorSchemaEntry::CreateTable`) and CTAS (`FabricatorCtasInfo`), passes
  them to `create_table`/`begin_bulk`; C# `SplitColumnList` → `IReadOnlyList<string>` → `DeltaCatalog` →
  engineered-wood `CreateAsync(partitionColumns:)` (Hive `<table>/<col>=<value>/*.parquet`, reads
  `Metadata.PartitionColumns` so INSERT/Append preserve the layout). **Provider-agnostic**: SQL Server / DAX
  ignore the arg (only Delta partitions). Also (C#-only, NO ABI): a **`delta_write_options` DuckDB session
  setting** (JSON — `compression`/`row_group_size`/`bloom_filter_columns`/`partition_by`) overlaying per-catalog
  ATTACH write-tuning defaults (`compression`/`row_group_size`/`bloom_filter_columns`), resolved by
  `DeltaCatalog.ResolveWriteSpec` → `DeltaWriteSpec` → `DeltaWriter.Options`; a native `PARTITIONED BY` clause
  overrides the setting's `partition_by`. engineered-wood's `ParquetWriteOptions` is delta-rs-class (auto
  dictionary + always-on min/max stats; bloom off by default). Validated: `test/verify_delta_catalog_partition.test`
  (54 — native CTAS/empty-CREATE+INSERT/multi-column/setting/override/re-attach), full Delta suite + SqlServer
  columnstore (CREATE+CTAS) unregressed, native partitioning **live on Fabric OneLake** (`LH.dbo.fabricator_parttest`,
  `region=US/EU/APAC`). **`delta_write_options` also carries `replace_where`** (C#-only, no ABI): **`replace_where`
  = `{partcol:val,…}`** turns an INSERT into an ATOMIC partition-overwrite — engineered-wood
  `DeltaTable.OverwritePartitionsAsync` (new; `WriteAsync`→ private `WriteCoreAsync` core with an
  `overwritePartitions` filter) removes exactly the matching-partition files + adds the new data in ONE commit
  (delta-rs static partition overwrite); keys MUST be partition columns (else `DeltaFormatException` — file-level
  removal is only exact for partition predicates) and the input must fall within them (else it errors, no
  silent append). `DeltaCatalog` gates it to plain INSERT (dropped for CREATE/CTAS/REPLACE).

  **SCHEMA EVOLUTION on write — `SCHEMA_MODE` COPY option + true CREATE OR REPLACE (ABI v54).** Because DuckDB's
  INSERT binder rejects wider-than-table data BEFORE the provider, schema evolution lives on **COPY** (COPY-TO
  isn't schema-checked, so arbitrary source schemas reach the provider) — surfaced as a `SCHEMA_MODE` COPY option
  threaded through **`begin_bulk`** (the ABI v54 arg `schema_mode`, next to partition/sort columns; provider-
  agnostic — SQL Server / DAX ignore it). **`SCHEMA_MODE 'merge'`** = append + UNION (engineered-wood
  `AddColumnAsync` per incoming-new column, then Append; old rows read NULL). **`SCHEMA_MODE 'overwrite'`** =
  replace data + adopt the incoming source schema (drop/add/retype) via new engineered-wood
  `DeltaTable.SetSchemaAsync` (a metadata-only `metaData` commit adopting the Arrow schema; no-op if identical;
  rejects column-mapping tables) then an Overwrite. **`CREATE OR REPLACE` / CTAS-replace is now a TRUE replace**:
  the Overwrite path always calls `SetSchemaAsync(incoming)` so the table adopts EXACTLY the new schema — a
  dropped column is GONE (not a lingering NULL), a new column appears — matching DuckDB's drop+create semantics
  and the SQL Server provider (which drops+recreates on replace). This **replaced the earlier confusing
  `merge_schema`-on-CREATE-OR-REPLACE band-aid**. `DeltaSchemaMode` (None/Merge/Overwrite) on `DeltaWriteSpec`,
  resolved by `ResolveWriteSpec` (COPY `SCHEMA_MODE` arg > `delta_write_options` `schema_mode`/`merge_schema` >
  the `merge_schema` ATTACH option, → Merge for append). Also fixed: history-preserving (the metaData commit keeps
  time-travel; old versions still show the old schema). Verified:
  `test/verify_delta_catalog_overwrite_merge.test` (47 — atomic partition overwrite, true CREATE OR REPLACE
  narrower-drops/wider-adds, COPY SCHEMA_MODE merge + overwrite); full Delta + SqlServer (columnstore/identity)
  suites unregressed. **Delta DECIMAL read + rowid-DML corruption — FIXED at the source (engineered-wood, no
  Bridge widening).** TWO root causes, both in engineered-wood, both fixed there: (1) **read corruption** — the
  parquet reader mapped a decimal to its parquet PHYSICAL width (INT32 → narrow Arrow `Decimal32`, INT64 →
  `Decimal64`), and the newer narrow decimal types are mishandled crossing the Arrow C-data-interface to DuckDB
  (read as 128-bit over the 4/8-byte buffer → garbage; e.g. `CAST(1.5 AS DECIMAL(2,1))` → `10.4`,
  `DECIMAL(10,2) 123.45` → `0.00`). DuckDB's native `read_parquet` reads the SAME files correctly → the WRITE was
  fine, only the read handoff was wrong. **Fix:** `ArrowSchemaConverter.MakeDecimalType` now always emits the
  classic `Decimal128` (≤38) / `Decimal256` (>38) regardless of physical width (`BuildDecimalFromInt32/Int64`
  already sign-extend to any byteWidth, so it's lossless). (2) **rowid `DELETE`/`UPDATE` corruption + crash** —
  the copy-on-write survivor filter `DeletionVectorFilter.TakeRows` had no decimal case → its `default: return
  source` passed the decimal column through UNFILTERED (all original rows), so the rewritten file had a
  row-count mismatch (e.g. `id` filtered to 2 values, `b` still 3) → mispaired reads + a `ReserveValues` buffer
  overrun. **Fix:** `TakeRows` now handles `Decimal128Array`/`Decimal256Array` via a byte-slice copy of the
  fixed-width value buffer (avoids `System.Decimal`'s 28-digit cap so precision 29–38 survives). With (1) the
  Bridge-side `DecimalWidening` (schema+batch widen on the Delta read boundary) is redundant and **removed** —
  the source now emits `Decimal128` directly. Verified: `test/verify_delta_catalog_decimal.test` (47 —
  Decimal32/64/128 physical widths, negatives, filter/aggregate, INSERT, time-travel AT VERSION, **DELETE**,
  **UPDATE**, re-attach durability); full Delta catalog suite (write/delete/update/changes/snapshots/
  time_travel/dv/alter/schemas) unregressed. **The decimal bug was one instance of a broader pattern — swept +
  fixed the rest (engineered-wood).** Both per-column "take rows" filters ended in `default: return source`,
  silently passing ANY unenumerated type through UNFILTERED (wrong-length column → the same corruption + overrun):
  `DeletionVectorFilter.TakeRows` (copy-on-write DELETE/UPDATE survivor filter) had **no Date/Timestamp/decimal
  case** — Date and Timestamp are ordinary columns, so this corrupted plain DELETE/UPDATEs, not just decimals;
  `PartitionUtils.TakeRows` (partition-split on partitioned writes) had no decimal case. Both now route every
  fixed-width Arrow type through a generic offset-aware value-buffer slicer and **THROW** on a genuinely
  unsupported (nested) type instead of returning it unfiltered. Also fixed: `ArrowSchemaConverter` mapped a
  parquet `TIME(micros/nanos)` to a malformed `Time32Type` (4-byte type, 8-byte semantics + a `<int>` value
  decode over an INT64 physical) → now `Time64` for micro/nano (general parquet-correctness fix; Delta itself has
  no TIME/unsigned type so it's not Delta-reachable). Verified: `test/verify_delta_catalog_temporal.test` (63 —
  DATE/TIMESTAMP/DECIMAL through DELETE, UPDATE, native partitioned write, re-attach durability); full Delta
  catalog suite unregressed.
  v50 = **directory move/rename** — appended `fs_move_dir(opener,src,dest,…)` to
  `FabricatorHostServices` (the reverse host→managed struct): maps to DuckDB's `FileSystem::MoveFile` — an atomic
  directory rename on a local filesystem; object stores (S3/Azure DFS) throw "not implemented". Powers **local/S3
  Delta catalog RENAME TABLE** (`DeltaCatalog.AlterTable` RenameTable → `HostFs.MoveDir`; OneLake still renames via
  the DFS SDK since Azure `MoveFile` is unimplemented). `test/verify_delta_catalog_schemas.test`. v49 =
  **recursive directory delete** — appended `fs_remove_dir(opener,path,…)` to
  `FabricatorHostServices` (the reverse host→managed struct, not the vtable): deletes a directory RECURSIVELY via
  DuckDB's `FileSystem::RemoveDirectory` (idempotent — no error if absent). Powers **Delta catalog DROP TABLE**
  (`DeltaCatalog.DropTable` → `HostFs.RemoveDir` removes the table's whole `<root>/<table>/` folder; opener
  threaded by `DropEntry`'s `FabricatorSetActiveTxn`). `test/verify_delta_catalog_write.test` (31). **OneLake DROP
  goes a different route** (`fs_remove_dir` → `FileSystem::RemoveDirectory` throws `AzureDfsStorageFileSystem:
  RemoveDirectory is not implemented!` — duckdb-azure has no recursive-delete on the DFS endpoint): `DropTable`
  branches on `FabricLakehouse.IsOneLake(root)` → a **direct ADLS Gen2 / OneLake DFS recursive delete**
  (`FabricLakehouse.DeleteDirectory` → `DataLakeDirectoryClient.DeleteIfExistsAsync`, idempotent) using the SP
  `ClientSecretCredential` the catalog mints — bypassing duckdb-azure entirely; local/S3 keep `fs_remove_dir`.
  Validated live 2026-06-30 on both `LH` (schema-enabled) and `LH_no_schema` (flat). See the OneLake-discovery
  paragraph below — discovery + DROP now share the DFS endpoint. v48 =
  **host-FS WRITE surface** — the Delta write-back foundation: appended five
  WRITE callbacks to `FabricatorHostServices` (the reverse host→managed struct, not the vtable) —
  `fs_open_write(opener,path,exclusive,…)` / `fs_write` / `fs_close_write` / `fs_remove` / `fs_create_dir` — plus
  the `FABRICATOR_ALREADY_EXISTS=4` status. `exclusive=1` opens with `EXCLUSIVE_CREATE` (the put-if-absent commit
  primitive — honored on OneLake/ADLS + POSIX; returns `ALREADY_EXISTS` if the target exists). `fs_create_dir`
  is recursive (mkdir -p; DuckDB's is single-level). The C# `DuckDbTableFileSystem` write methods
  (`CreateAsync`/`WriteAllBytesAsync`/`RenameAsync`/`DeleteAsync` + `DuckDbSequentialFile`) sit on these;
  **`RenameAsync` is emulated as exclusive-create-copy + delete-source** because DuckDB's `MoveFile` overwrites
  on local and is *unimplemented* on Azure DFS — so the commit's put-if-absent guard rides `EXCLUSIVE_CREATE`,
  and engineered-wood's temp+rename commit works unchanged (a conflicting target → `RenameAsync` returns false
  → `DeltaConflictException`). `HostFsGlob` now normalizes a not-found glob (object-store 404) to empty so a
  brand-new table's missing `_delta_log/` reads as "create". Demo `fabricator_delta_write_demo(path)` — a global
  host-FS table fn writing a fixed 5-row Delta table via engineered-wood (`DeltaWriteMode.Overwrite`,
  idempotent), validated end-to-end (write+read round-trip) on **local AND a live OneLake lakehouse** (SP
  azure secret). `test/verify_delta_write.test`. Single-writer; concurrent commits work where `EXCLUSIVE_CREATE`
  is honored (OneLake/POSIX — not Windows local). **Portability (validated with delta-kernel-rs, the reference
  reader, via DuckDB's official `delta` extension): engineered-wood's defaults are NOT standard-readable (incl.
  Fabric) — three fixes:** (1) `metaData.format.options` + (2) `metaData.configuration` always emitted
  (engineered-wood `ActionSerializer` — were omitted when empty/null, non-nullable for strict readers); (3)
  parquet `path_in_schema` (engineered-wood `OmitPathInSchema` defaults TRUE → drops this REQUIRED field →
  `TProtocolException: Invalid data`) — fixed our side via `ParquetWriteOptions { OmitPathInSchema = false }` in
  `DeltaWriteDemoFunction`. With all three, delta-kernel-rs reads it locally; a fresh `Tables/dbo/fabricator` table
  written to OneLake for Fabric. (#1/#2 are engineered-wood-repo patches; #3 is a write option. DuckDB's official
  `delta_scan` can't LIST a OneLake `_delta_log` — a delta-kernel azure/secret quirk, "No files in log segment" —
  so OneLake validation is via our reader + the local delta-kernel read. A table written BEFORE the fixes stays
  broken on its version-0 metaData → write a fresh one.) **`fabricator_delta_write(<input>, path := '…')`** — a
  global host-FS **collector** that writes ANY input table (a DuckDB query result) to a Delta table (Overwrite),
  returning `(version, rows_written)`; buffers input (Arrow-IPC round-trip copy), commits one version via the
  shared `DeltaWriter`. Cost args ride as NAMED params (`Parameters` added to `IInOutFunction`/
  `ICollectorTableFunction` + handle-0 `GlobalFunctions.ParamSchema`); the opener is threaded into the collector
  Source `GetDataInternal` (where C# `Collect` runs — Finalize-only was racy) AND into the shared
  `FabricatorSetActiveTxn` helper (so any connection-using callsite sets it). Validated local + a live OneLake
  managed table (`Tables/dbo/fabricator_query`). `test/verify_delta_write.test` (18). **Delta folder-as-catalog
  (READ + WRITE) DONE**: `DeltaBackend` (3rd `IBackend`, `"delta"`/`"deltalake"`, registered explicitly in
  `BackendRegistry.Discover` — Bridge-resident) + `DeltaCatalog`. `ATTACH '/lake'
  (TYPE fabricator, PROVIDER 'delta')` discovers subdirs-with-`_delta_log/` as tables under a flat `main` schema
  (glob `<root>/*/_delta_log/*.json`), columns via `DeltaReader.GetSchema`, scan via `DeltaReader.Stream` with
  filter pushdown. The opener is threaded into the catalog metadata path (`LoadCatalog`/`RefreshCache` call
  `FabricatorSetActiveTxn` before discovery; `FetchTableColumns` already did). `test/verify_delta_catalog.test`
  (17 — discovery + filter + join, LOCAL). **CATALOG STREAMING WRITE DONE** (the chosen slice): `CREATE TABLE`/
  `INSERT`/CTAS/COPY stream straight to engineered-wood via the **standard bulk path** (`begin_bulk`/`push_batch`/
  `complete_bulk` → `BulkSession` → `DeltaCatalog.BulkInsert`), exactly like the SQL/DAX backends — the global
  `fabricator_delta_write` collector is no longer needed for the catalog case (it stays as the no-ATTACH function
  form). **Opener threading into the bulk path:** `SetActiveOpener(&context)` is set immediately before
  `BeginBulk` in the insert/CTAS/COPY operators; `BulkSession` captures it at `begin_bulk` and **re-establishes
  `AmbientOpener.Current` (+ the txn id) on its background consumer thread** (the opener's `ClientContext` stays
  valid until `complete_bulk`, which blocks on the consumer). `createTable`/`replace` ⇒ `DeltaWriteMode.Overwrite`,
  plain INSERT ⇒ `Append`; one commit per statement; `CreateTable` writes empty commit-0 (PK/UNIQUE/DEFAULT
  ignored — Delta has none); `DeltaWriter.Materialize` IPC-round-trips the streamed batches for the commit. NO
  ABI change (reuses bulk + the v47 `set_active_opener`). `test/verify_delta_catalog_write.test` (31 — CREATE/
  INSERT/append/CTAS/aggregate + DROP TABLE + detach/re-attach durability, LOCAL). **DROP TABLE DONE** (ABI v49
  — appended `fs_remove_dir` to `FabricatorHostServices`: recursive directory delete via DuckDB's
  `FileSystem::RemoveDirectory`, idempotent; `DeltaCatalog.DropTable` deletes the table's whole `<root>/<table>/`
  folder via `HostFs.RemoveDir(AmbientOpener.Current, …)`, opener threaded by `DropEntry`'s `FabricatorSetActiveTxn`).
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
  `FetchRowIdColumns` returning a name absent from the schema is treated as a VIRTUAL rowid — `FabricatorTableEntry`/
  `ArrowStreamBindData` carry the NAMES (not indices) in `virtual_rowid_columns`, `HasRowId`/`GetVirtualColumns`/
  `GetRowIdColumns` honor them, `BuildScanSpec` adds them to the fetch list when rowid is requested, `arrow_ingest`
  resolves their result positions BY NAME for `BuildRowId`, and `BuildModifyTarget` uses the virtual names +
  BIGINT. SQL Server is unaffected (its rowid names always resolve to real columns; the virtual branch never
  fires — verified `verify_proc_inout`/`verify_time_travel`/`verify_columnstore` green). `DeltaCatalog`:
  `GetMetadata(RowId)` ALWAYS returns `_metadata.row_id` (the transient rowid works on ANY Delta table);
  `ScanTable` streams WITH the row-id column when requested; `ExecuteDelete` collects the ids → `DeleteByRowIdsAsync`.
  `test/verify_delta_catalog_delete.test` (28 — equality/range/name predicates + durable across re-attach +
  DELETE-all). **Live Fabric: plain-Delta CTAS+DELETE validated end-to-end** on the schema-enabled `LH` lakehouse
  (`fabricator_deltest4`: v0 protocol = minReader 1/minWriter 2/no features, DELETE = plain remove+add, our read
  correct); the OneLake table-format conversion is expected to succeed on plain Delta (pending user confirm — the
  earlier row-tracking/DV tables failed conversion). **A transient rowid is valid only within one snapshot** (a
  scan's rowids must be consumed by the DELETE before another write changes the file set — true for a single
  DML statement). **UPDATE DONE — rowid-based PER-FILE copy-on-write** (matches DELETE). `ExecuteUpdate` receives
  the new SET-column values (named) + the transient `_metadata.row_id` per row; it builds a `rowid → new values`
  map and calls engineered-wood `UpdateByRowIdsAsync(rowIds, rewriteFile)`, which rewrites ONLY the files
  containing a matched row (decoded from `rowid >> 40`) — each affected file's batches are handed back via the
  `rewriteFile` callback, where Fabricator rebuilds the SET columns on the matched positions as CLEAN Apache.Arrow
  batches (`BuildArray`, a typed inverse of `ArrowValueReader.ReadScalar` — bool/ints/uints/float/double/
  decimal128/string/date32/timestamp; rowid recomputed as `(ordinal << 40) | positionInFile` to match the scan),
  and engineered-wood re-writes them as plain `remove`+`add` with a CLEAN schema (the parquet-footer fix). The
  typed substitution stays in Fabricator (reuses `BuildArray`/`ReadScalar`); engineered-wood stays generic (file
  selection + read + clean write). Unaffected files are untouched. NO C++ change (reuses the DELETE virtual-rowid
  planning + the `ExecuteUpdate` ABI). Verified single-row / multi-row / expression (`amt=amt+1`) updates,
  UPDATE∘DELETE composition, re-attach durability, a delta-kernel `delta_scan` read-back, AND live on Fabric
  (`fabricator_updtest` on the schema-enabled `LH` lakehouse). `test/verify_delta_catalog_update.test` (63).
  **SCHEMA EVOLUTION — `ALTER TABLE … ADD COLUMN` DONE** (the only supported ALTER kind on Delta): a
  **metadata-only commit** appending a nullable column (NO file rewrite) — engineered-wood `DeltaTable.AddColumnAsync`
  writes a new `MetadataAction` (current Arrow schema ++ the new field → `SchemaConverter.FromArrowSchema` →
  `DeltaSchemaSerializer` → `metaData` at version+1; rejects column-mapping tables [no field-id assignment] +
  non-nullable + duplicate names). The crux is the **read-side NULL backfill** (`DeltaTable.BackfillMissingColumns` +
  `MakeNullArray`, in `ReadFileAsync` before the rowid append): a column added after a data file was written is
  absent from that file's parquet, so each batch is reconciled to the current schema — present columns by name, the
  missing one as an all-NULL typed array (no-op fast path when the file already has every column). `DeltaReader.AddColumn`
  → `DeltaCatalog.AlterTable`; `a1`=name, the `Field` carries type+nullability. C++ `FabricatorSchemaEntry::Alter` now
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
  `<root>/t` — a silent collision footgun). `ATTACH '…' (TYPE fabricator, PROVIDER 'delta', schemas true)` switches it
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
  `staging.t` resolves to DuckDB's DEFAULT (memory) catalog, not the attached one — always use `db.schema.table`.
  **TIME TRAVEL — `FROM t AT (VERSION => n)` DONE** (C#-only; the plumbing already existed —
  `FabricatorCatalog::SupportsTimeTravel()` is `true` for all providers and the `AT` clause already flows to the
  scan's `spec_json` as `"at":{unit,value}` → `ScanSpec.At`, which the SQL backend uses for `FOR SYSTEM_TIME AS
  OF`). `DeltaCatalog.ScanTable` now honors `spec.At`: `DeltaReader.StreamAt` / `GetSchemaAt` resolve the
  snapshot (engineered-wood `ReadAtVersionAsync` + `GetSnapshotAtVersionAsync`) and stream as of that version,
  advertising the schema AS OF that version. **Filter pushdown applies under time travel** (`ReadAtVersionAsync`
  takes the predicate → file/row-group pruning). Unlike the SQL provider (timestamp-only, `FOR SYSTEM_TIME`),
  Delta accepts **VERSION** (the natural Delta form) — and works under JOIN/UNION version comparison. **Rowid
  under time travel:** DuckDB's `count(*)`-via-rowid optimization can request the virtual `_metadata.row_id` on a
  time-travel scan; the branch routes that to a version-aware rowid stream
  (`DeltaReader.StreamWithRowIdsAt` → engineered-wood `ReadAtVersionWithRowIdsAsync`, the version analog of
  `ReadAllWithRowIdsAsync`) — without it, the rowid (BIGINT) collided with the first user column (`INTERNAL
  Error: Vector::Reference … BIGINT referenced INTEGER`) when the same table was scanned at multiple versions in
  one statement. **`AT (TIMESTAMP => ts)`: opt-in via `in_commit_timestamps true`.** engineered-wood's
  `GetSnapshotAtTimestampAsync` resolves a timestamp via the Delta **in-commit-timestamps** feature (NOT the
  commit-file mtime — mtime is mutable/non-monotonic on object stores, the very problem inCommitTimestamps
  solves). On a plain table (default) it has no timestamp to read → a clean "Ensure the table has in-commit
  timestamps enabled" error; **VERSION travel always works**. The **`in_commit_timestamps true` ATTACH option**
  (mirrors `deletion_vectors`; `DeltaCatalog._inCommitTimestampsOnCreate` → `delta.enableInCommitTimestamps=true`
  in the create config; engineered-wood `CreateAsync` declares the `inCommitTimestamp` **writer** feature
  [writer v7, NOT a reader feature — readers read normally] + `EnsureCommitInfo` writes the per-commit timestamp)
  makes TIMESTAMP travel resolve. Enabled at creation (v0), so no
  `inCommitTimestampEnablementVersion/Timestamp` props needed. **delta-rs/delta-kernel reads it** (DuckDB's
  official `delta_scan`), and our provider write+read+timestamp-travel works on OneLake. **Fabric OneLake
  conversion — GATED ON A FABRIC TIME-TRAVEL SETTING (validated live 2026-06-30):** on a lakehouse WITHOUT it
  (`LH_no_schema`) the converter rejects the writer-v7 table — it shows "Unable to identify these objects as
  tables or views"; on a lakehouse WHERE the Fabric time-travel setting is ENABLED (`LH2`) the converter
  **accepts** it — the table registers AND is **queryable via the Fabric SQL endpoint** (confirmed live, not just
  visible in the Tables list). So it's NOT a blanket rejection — Fabric's converter accepts the
  inCommitTimestamp (writer-only) feature once the workspace/lakehouse time-travel setting is on. (The setting is
  documented for the Warehouse but also affects the Lakehouse converter — confirmed.) Earlier
  `deletion_vectors`/row-tracking failures were a different cause (reader-v3 / domainMetadata / DV byte format),
  not just "writer v7". **⇒ `in_commit_timestamps` works on Fabric lakehouses with time-travel enabled, AND on
  local/S3 + delta-rs/Spark; without the Fabric setting use plain tables (VERSION travel) on Fabric.** A
  **commit-file mtime** path (timestamp travel on PLAIN tables, no writer-v7, no Fabric setting needed — a clean
  engineered-wood spec-fix since `ITableFileSystem.ListAsync` already returns `LastModified`, + a host-FS mtime
  callback our side) remains an option but is now lower-priority since inCommitTimestamps works on Fabric.
  **VERSION travel works everywhere (plain Delta) and is the universal form.** NO ABI/C++ change. Verified:
  `test/verify_delta_catalog_time_travel.test` (47 — version 0/1/2/3, filter pushdown, multi-version
  count/JOIN/UNION, re-attach durability, plain-table timestamp error, and `in_commit_timestamps` timestamp
  travel [local]).
  **Snapshots/history function — DONE: `fabricator_delta_snapshots('<catalog>', '<schema.>table')`** (DuckLake-style
  snapshots view). First arg = the ATTACH'd catalog NAME (resolved to its handle via `ResolveConnection` — no
  abfss path needed, the catalog's own `TablePath` builds the location); second = the table, schema-qualified
  (schema **mandatory on a schema-enabled lakehouse**, defaults to `main` on a flat catalog; C++ splits on the
  first `.`, C# resolves the default/required schema). Returns `(version BIGINT, timestamp TIMESTAMP, operation
  VARCHAR, operation_parameters VARCHAR)` from the `_delta_log`. C++ `SnapshotsBind` mirrors `ServerInfoBind`
  (catalog→handle, factory = `GetMetadata(handle, FABRICATOR_META_SNAPSHOTS=8, schema, table)`, reuses
  `ArrowStreamScan`); C# `DeltaCatalog.GetMetadata(Snapshots)` → `DeltaReader.GetSnapshots` → engineered-wood
  `DeltaTable.GetHistoryAsync` (new — `ListVersionsAsync` + `ReadCommitAsync` → `CommitInfo`). **Additive enum →
  NO ABI bump.** **`commitInfo` is now written on EVERY commit by default** (engineered-wood
  `InCommitTimestamp.EnsureCommitInfo` always prepends a `commitInfo` with `operation` + a `timestamp` — standard
  feature-free fields, no protocol bump, writer v2; `CreateAsync`'s v0 also gets one, operation `CREATE TABLE`).
  So **plain tables now show a full operation + timestamp history** (`CREATE TABLE`/`WRITE` per version), not just
  the version list. The opt-in `inCommitTimestamp` field is added on top ONLY for `in_commit_timestamps` tables.
  **`AT (TIMESTAMP)` time travel now works on PLAIN tables too** (`GetTimestamp(CommitInfo)` reads
  `inCommitTimestamp ?? commitInfo.timestamp`, and `GetSnapshotAtTimestampAsync` uses it) — the always-on
  `commitInfo.timestamp` is enough to resolve a snapshot, so the `in_commit_timestamps` feature is now only needed
  for the **in-protocol monotonic** guarantee (Spark/Fabric interop), NOT for local timestamp travel. A far-future
  instant → latest version; an instant before commit-0 → clean "No commit found" error
  (`test/verify_delta_catalog_time_travel.test`, 48). Validated local + **live Fabric**: `LH2.dbo.fabricator_ict2` (ICT) AND a PLAIN table on
  `LH_no_schema` both show v0 `CREATE TABLE` + `WRITE`s with timestamps — and the plain `commitInfo` table on
  `LH_no_schema` (no time-travel setting) **registers + is SQL-endpoint-queryable** in Fabric (confirmed), i.e.
  always-on `commitInfo` is Fabric-safe on plain writer-v2 tables. `test/verify_delta_catalog_snapshots.test`
  (28). Pairs with VERSION time travel: read the
  snapshots to pick a version. **Per-row commit version as a virtual column (DuckLake
  `snapshot_id` analog) — NOT built**: needs the Delta **row-tracking** feature (`_metadata.row_commit_version`),
  which our plain tables don't enable (only the opt-in `deletion_vectors true` path enables row tracking) + a
  second-virtual-column plumbing beyond `_metadata.row_id` + uncertain Fabric read-compat of row-tracking
  commits. **Change Data Feed (CDF) — DONE + VALIDATED LIVE on Fabric (2026-06-30).** Enabled per-catalog via the
  ATTACH option **`change_data_feed true`** (`DeltaCatalog._changeDataFeedOnCreate`): tables CREATEd in that
  catalog declare the Delta **`changeDataFeed` writer feature** (writer-v7; `DeltaWriter.CreateConfig` →
  engineered-wood `CreateAsync` adds `delta.enableChangeDataFeed=true` + the feature). Then INSERT/DELETE/UPDATE
  **capture CDC change files**: blind appends infer naturally, and the rowid copy-on-write DELETE/UPDATE +
  DV-delete paths emit `_change_data/*.parquet` (`CdfConfig.Delete`/`UpdatePreimage`/`UpdatePostimage`) — they
  already read the changed rows for the rewrite, so capture is "for free" there. **Read** via
  **`fabricator_delta_changes('<catalog>', '<schema.>table', from [, to])`** (2 overloads — `to` omitted/-1 ⇒
  latest): the row-level feed between two versions with `_change_type` (insert/delete/update_preimage/
  update_postimage) ++ `_commit_version BIGINT` ++ `_commit_timestamp BIGINT` (epoch ms, from the always-on
  commitInfo). C++ `ChangesBind` mirrors `SnapshotsBind` (catalog→handle, arg2 = `"from:to"`, factory =
  `GetMetadata(handle, FABRICATOR_META_CHANGES=9, schema.table, range)`); C# `DeltaCatalog.GetMetadata(Changes)` →
  `DeltaReader.GetChanges` → engineered-wood `DeltaTable.ReadChangesAsync`. **Additive enum → NO ABI bump.** **Two
  fixes that made the read work:** (1) the rowid-DML CDC capture must drop the **virtual** `_metadata.row_id`
  trailing column (`DropVirtualRowId` — NOT `RowTrackingWriter.StripRowIdColumn`, which strips the *physical*
  `__delta_row_id`) before writing the change file, else the update_preimage batch has 6 cols vs 5 elsewhere → a
  schema mismatch across change batches → **arrow_ingest SIGSEGV**; (2) `DeltaReader.GetChanges` **streams lazily**
  (peek-the-first-batch for the schema, table stays open for the whole enumeration — kept for BOUNDED MEMORY; the
  original "materializing then disposing the table frees the batches' Arrow buffers = use-after-free, 'Out of Range
  string size'" diagnosis was DISPROVEN 2026-07-16 — see the lifetime-correction note in the buffered-DML slice-2
  bullet; the corruption was almost certainly fix (1)'s schema mismatch). **CDF-enabled guard:**
  engineered-wood's `CdfReader` silently INFERS changes from add/remove on a non-CDF table (misleading for
  copy-on-write — survivors look like inserts), so `GetChanges` requires `CdfConfig.IsEnabled(config)` and throws
  "Change Data Feed is not enabled" otherwise (Spark `DELTA_CHANGE_DATA_FEED_NOT_ENABLED` parity). Validated local
  (`test/verify_delta_catalog_changes.test`, 45 — full feed + change-type breakdown + pre/post images + bounded
  ranges + 3-arg-latest + CDF-off error) AND **live Fabric** (`Test`/`LH` schema-enabled: CTAS → DELETE → UPDATE
  on `lake.dbo.fabricator_cdftest` → correct feed [3 ins/v1, del/v2, upd pre+post/v3] + snapshots v0 CREATE/v1
  WRITE/v2 DELETE/v3 UPDATE). **OneLake table discovery + DROP
  — via the ADLS Gen2 / OneLake DFS endpoint directly** (`FabricLakehouse`, Bridge; `Azure.Storage.Files.DataLake`
  12.21.0): DuckDB's azure glob can't recurse a OneLake `_delta_log` tree (mid-path wildcard → `type must be
  string, but is null`, duckdb-azure PR #174), so a OneLake root (`abfss://<ws>@onelake…/<lh>.Lakehouse/Tables`)
  lists its tables via the **Azure SDK `DataLakeFileSystemClient.GetPaths`** (NOT DuckDB's azure ext, NOT the
  Fabric `ListTables` REST API, NOT the SQL endpoint) — flat lakehouse → table dirs under `Tables/` (schema
  `main`), schema-enabled → schema dirs under `Tables/` then table dirs under each (`Tables/<schema>/<table>`);
  **local/S3/plain-ADLS roots keep the host-FS glob** (`DeltaCatalog.DiscoverTables` branches on
  `FabricLakehouse.IsOneLake`). This is immune to the glob bug AND the `ListTables` 400 on schema-enabled
  lakehouses, AND free of the SQL-endpoint sync lag (DFS reflects committed files immediately). **DESIGN (deferred,
  nothing built): generalize `FabricLakehouse` into a full C# OneLake filesystem to escape duckdb-azure entirely** —
  [docs/filesystem-bridge.md](docs/filesystem-bridge.md) §"OneLake filesystem + unified Fabric credential": (1) a
  shared `FabricCredentialResolver` (SP secret ⇒ `ClientSecretCredential`, else `DefaultAzureCredential` picking up
  Fabric managed/workspace identity — one credential feeding DAX/SQL/OneLake with per-service scopes; the "seamless
  local + Fabric-notebook" requirement); (2) `OneLakeDataLakeFileSystem : ITableFileSystem` on the Azure DataLake SDK,
  swapped in for OneLake roots instead of the host `fs_*` callbacks (removes duckdb-azure from ALL C#-side IO); (3)
  **with the native Multifile-Delta model, register it as a DuckDB `onelake://` FileSystem (forward callbacks)** — NOT
  Delta-specific, it lifts EVERY DuckDB reader/writer (read_json/csv/parquet, excel, `COPY … TO 'onelake://…'`) onto
  OneLake, so Excel/JSON round-trips ride the same transport for free; (4) optional `TYPE fabric` secret (v38 machinery)
  for explicit auth UX (redundant over azure-secret + DefaultAzureCredential; pure clarity). `duckdb_onelake` is the
  secret-layering blueprint only — it still leans on duckdb-azure/httpfs, doesn't fix the FS. **The Fabric REST
  API is kept ONLY for the schema-enabled flag** (`GetLakehouse.DefaultSchema` — authoritative even for an empty
  lakehouse, where the DFS structure alone is ambiguous) **+ workspace/lakehouse name→GUID resolution**
  (`WorkspacesClient`/`ItemsClient`; GUIDs in the path skip it). **CRITICAL — use the ASYNC DataLake APIs**
  (`GetPathsAsync`/`DeleteIfExistsAsync` + `GetAwaiter().GetResult()` at the boundary): the SYNC `GetPaths` uses
  `HttpClient.Send`, whose sync transport HANGS under the hostfxr-hosted CLR (a single discovery never returns —
  isolated to the sync path; the same call is ~1s in a normal console host) — same reason every other Bridge IO
  path is async. Auth = the **ATTACH'd azure SP secret** (`ATTACH '…OneLake…' (TYPE fabricator, PROVIDER 'delta',
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
  dir is gone); plus on `LH` CTAS into `lake.dbo.fabricator_deltest` → `DELETE WHERE id=2` (deletion-vector commit)
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
  compaction, which our DML doesn't use. **DELETION VECTORS ARE THE DEFAULT DML MODE (2026-07-04, commit
  `66e97f5`).** New Delta catalog tables use deletion vectors by default (the modern Delta standard): a DELETE
  marks rows in a DV bitmap instead of rewriting the file — cheap, and it preserves row-tracking ids/versions
  for free (no rewrite). `DeltaCatalog._deletionVectorsOnCreate` now defaults **true** (`ParseBoolOption` gained a
  defaulted overload distinguishing absent → default from explicit false). **Opt OUT with `deletion_vectors
  false`** for a plain copy-on-write table (`minReader 1`, no reader-v3 bump) — e.g. a consumer that can't read
  reader-v3 DV tables. Copy-on-write stays the fallback (opt-out) + the compaction path. **UPDATE on a DV table
  is MERGE-ON-READ** (engineered-wood `UpdateViaVectorsAsync`, picked internally by `UpdateByRowIdsAsync` when
  `delta.enableDeletionVectors` + clean shape [no mapping/partitions/type-widening/CDF]): DV-delete the matched
  OLD rows (union their positions into the file's DV — NO file rewrite) + APPEND their post-image rows as one
  small new file, committed atomically as `remove`(old,oldDV)+`add`(same,newDV)+`add`(post-image file). Big
  write-amplification win for a small update on a large file, and it re-ids FEWER rows than copy-on-write (which
  re-ids every row in the rewritten file — only the appended rows get fresh ids; non-updated rows keep their
  original id/version). **Stable-id preservation across UPDATE is now DONE (opt-in `materialize_row_tracking
  true`) + VALIDATED ON FABRIC SPARK** — see the "Materialized row tracking" bullet below (the appended row
  carries its ORIGINAL `__delta_row_id`, so Spark reads `_metadata.row_id` preserved; default off keeps the
  validated DV path untouched). Validated: official `delta_scan` reads the DV-original +
  appended result + LIVE on Fabric OneLake; `test/verify_delta_catalog_dv_default.test` asserts the append is
  small (a 2-row post-image file beside the 10-row original, not a 10-row rewrite). Non-DV (opt-out) UPDATE stays
  copy-on-write (native rewriter under `native_write`). `test/verify_delta_catalog_dv_default.test`
  proves default ⇒ DV (on-disk parquet count stays 1 after DELETE = no rewrite) vs `deletion_vectors false` ⇒
  copy-on-write (2 files = rewrite); the CoW-intent tests (`verify_delta_catalog_delete` + the native_write CoW
  catalogs) are pinned `deletion_vectors false` to keep that coverage. **OPTIMIZE + VACUUM maintenance WIRED
  (2026-07-04).** Under DV-default, DVs + merge-on-read append small files accumulate, so bin-pack compaction
  matters: `fabricator_exec('<catalog>', 'OPTIMIZE <schema.table>')` (+ `VACUUM <schema.table> [RETAIN <hours>
  HOURS] [DRY RUN]`) → `DeltaCatalog.ExecuteNonQuery` → `DeltaReader.Optimize`/`Vacuum` → engineered-wood
  `CompactAsync`/`VacuumAsync` (mirrors the delta-rs provider's maintenance dialect). **Under `native_write` the
  OPTIMIZE compacted files are written by DuckDB's native writer too** (`CompactionExecutor` gained an
  `IDataFileWriter` branch; `DeltaReader.Optimize` opens with the native writer when `_nativeWrite`) — so an
  OPTIMIZE KEEPS the native-write quality (bloom/stats/decimal) instead of reverting to the EW codec. Proven: the
  compacted file (after OPTIMIZE+VACUUM leaves only it) carries the native bloom signature (bloom on the
  dict-encoded `grp`, none on all-distinct `id`); validated LIVE on Fabric OneLake. **`CompactionExecutor` fixed
  to be DV-AWARE** — it now EXCLUDES each candidate file's deletion-vector-deleted rows (else compaction would
  RESURRECT them — a data bug, since DV is the default) + strips the internal `__delta_row_id` + carries each
  removed file's DV on the `remove`. **The exec path now threads the host-FS opener** (`fabricator_extension.cpp`
  `FabricatorExecFunction` calls `SetActiveOpener` before `ExecuteDml`; C++-only, no ABI) — without it a
  fresh-connection OPTIMIZE segfaulted (the Delta provider's `_delta_log` listing needs the opener; SQL Server /
  delta-rs ignore it). Validated: `test/verify_delta_catalog_optimize.test` (30 — 4 small files + DV-delete →
  OPTIMIZE consolidates, deleted rows NOT resurrected, VACUUM cleans, durability) + delta_scan + LIVE Fabric
  OneLake (`compacted → v6`, data {1,3} correct). **Row-tracking-on-rewrite (compaction + merge-on-read UPDATE)
  — NOW PRESERVED under `materialize_row_tracking true` (2026-07-04, Fabric-Spark-validated).** By default the
  compacted/appended files get a FRESH `baseRowId` and EW does NOT declare the materialized row-tracking columns,
  so a spec reader (delta-kernel/Spark) computes ids from `baseRowId + position` → a rewrite CHANGES the stable id
  of the rewritten rows (the DATA is always correct — delta_scan + Fabric validated — only external
  stable-row-tracking drifts). With **`materialize_row_tracking true`** both the UPDATE-append AND **compaction**
  now materialize the ORIGINAL ids: the UPDATE case needs only `__delta_row_id` (appended rows share the new
  commit version = the file's `defaultRowCommitVersion`), but **compaction mixes rows from several source files**,
  so `CompactionExecutor` materializes BOTH `__delta_row_id` (source `baseRowId + position`, or the source's own
  materialized id when present) AND `__delta_row_commit_version` (the source file's `defaultRowCommitVersion`) —
  a single `baseRowId`/`defaultRowCommitVersion` on the compacted `add` can't represent them (new
  `RowTrackingWriter.AddRowIdAndCommitVersionColumns` + the compaction read loop tracking survivor id/version
  arrays aligned with the DV filter's keep order). **Validated live on Fabric Spark (2026-07-04):** after
  CTAS+2×INSERT (3 files) → OPTIMIZE+VACUUM, Spark reads `_metadata.row_id` = 0,1,2 (PRESERVED write-order ids,
  NOT the fresh `base_row_id`=3 range) and `_metadata.row_commit_version` = 1,2,3 (per-row original versions,
  overriding the single `default_row_commit_version`=1). Local write-shape test
  `test/verify_delta_catalog_compaction_rowtracking.test` (24 — compacted parquet carries original ids + 3
  distinct versions + durability); default (non-materialize) compaction path unchanged
  (`verify_delta_catalog_optimize` green). **Fabric Spark row-id validator:** the preview **Microsoft ADO.NET
  Driver for Fabric Data Engineering**
  below. **Fabric Spark row-id validator:** the preview **Microsoft ADO.NET Driver for Fabric Data Engineering**
  (`Microsoft.Spark.Livy.AdoNet`, download-center zip) — its own session-create POST 404s, but the underlying
  **Fabric Livy REST API** works with the `fabric_sp` SP (`ClientSecretCredential`, `livyApi` version
  `2023-12-01`) and Spark exposes `_metadata.row_id`/`_metadata.row_commit_version`. Harness =
  `scratchpad/sparkprobe` (drives Livy raw: create session → `POST /statements` Spark SQL → read output; reads
  the SP from `dax_secret.sql` at runtime). This is THE way to validate Delta row tracking end-to-end.
  **Activate DV explicitly** with the ATTACH option
  `deletion_vectors true` (now also the default) → tables CREATED in that catalog enable the `deletionVectors` +
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
  copy-on-write delete (28)/update (63)/write (31) unregressed. **DV write now also live-validated on OneLake
  (2026-07):** CTAS + `DELETE … WHERE id IN (2,4)` on `LH.dbo` with `deletion_vectors true` produced a v2 commit
  that `remove`+`add`s the SAME file (numRecords 5 unchanged, no rewrite) with an **inline `deletionVector`**
  (`storageType:"i"`, `cardinality:2` = the two deleted rows) + row-tracking `baseRowId`/`defaultRowCommitVersion`;
  our read returned the correct survivors (1,3,5). Confirms the RoaringBitmap format fix writes correctly to
  OneLake. **FABRIC READS IT — confirmed (user-verified live, 2026-07):** the reader-v3 + inline-DV table
  (`LH.dbo.fabricator_ewdv_live`) registers in Fabric AND is **SQL-endpoint-queryable** with the deleted rows
  removed. So engineered-wood's DV mode is END-TO-END validated on OneLake: DV write → our read → Fabric SQL
  endpoint. (This supersedes the earlier row-tracking/DV Fabric-conversion failures, which were the pre-fix
  byte-format/protocol bugs; the current inline-DV format is Fabric-readable. Contrast the delta-rs provider,
  which copy-on-writes DELETE and produces no DV.)
  **STANDALONE ROW TRACKING DONE — ATTACH option `row_tracking true`.** Enables the Delta
  `delta.enableRowTracking` WRITER feature (writer-v7 + `domainMetadata`) **independent of `deletion_vectors`**,
  and — unlike DV mode — WITHOUT a reader-v3 bump (`minReaderVersion` stays 1). Verified: commit-0
  `writerFeatures:["rowTracking","domainMetadata"]` + `delta.enableRowTracking=true`; the WRITE commit's `add`
  carries `baseRowId`/`defaultRowCommitVersion` (stable ids for Spark/Fabric). `DeltaCatalog._rowTrackingOnCreate`
  (parsed like `deletion_vectors`) → `DeltaWriter.Create`/`Write(rowTracking:)` → `CreateConfig`'s standalone
  `delta.enableRowTracking` branch (the DV→RT coupling is unchanged). **Our DELETE/UPDATE still use the transient
  `(file,position)` rowid, NOT the stable id** — Delta DML is physical (the copy-on-write rewrite / DV path need
  `(file,position)`, which the transient rowid already IS, computed at scan time); a stable id would have to be
  resolved back to `(file,position)` via per-file `baseRowId` ranges for no benefit under DuckDB's single-statement
  atomic scan→mutate (the transient rowid is valid for exactly that snapshot window). Stable-id DML only pays off
  for cross-snapshot retry, which DuckDB never needs → `row_tracking` is a **write-side interop feature for external
  readers**, not a DML mechanism. `verify_delta_catalog_row_tracking.test` (33 — feature declared, baseRowId
  materialized, DELETE/UPDATE/INSERT unaffected). See [docs/delta-catalog.md](docs/delta-catalog.md).
  **Materialized row tracking (SUPERSEDED 2026-07-13: the `materialize_row_tracking` ATTACH option is
  GONE — materialization is now IMPLIED whenever row tracking is enabled, incl. via the DV default; see
  the consolidation bullet. Historical record of the original opt-in below.) — STABLE ROW ID PRESERVED ACROSS
  MERGE-ON-READ UPDATE, VALIDATED ON FABRIC SPARK (2026-07-04).** The gap: our merge-on-read UPDATE appends the
  post-image row in a NEW file, so its `base_row_id + row_index` changed (stable id 1→3) — proven via Spark
  (`_metadata.base_row_id`). Root cause: EW enabled row tracking but never declared
  `delta.rowTracking.materializedRowIdColumnName`, so Spark couldn't even synthesize `_metadata.row_id` for our
  tables, and the UPDATE-append wrote a fresh `__delta_row_id`. **Fix (opt-in):** `materialize_row_tracking true`
  → `DeltaCatalog._materializeRowTracking` → `CreateConfig` declares
  `delta.rowTracking.materializedRowIdColumnName=__delta_row_id` (+ `...RowCommitVersionColumnName`) at create;
  engineered-wood `UpdateViaVectorsAsync` materializes each appended row's **ORIGINAL** stable id
  (`sourceAddFile.BaseRowId + position`) into `__delta_row_id` (new `RowTrackingWriter.AddRowIdColumn(batch,
  Int64Array)` overload) instead of a fresh id. **Validated live on Fabric Spark:** after `UPDATE … WHERE id=2`,
  Spark reads `_metadata.row_id=1` (PRESERVED, was 3 without this) + `_metadata.row_commit_version=2` (correctly
  bumped) — matching Spark's own writer's reference behavior; untouched rows keep ids 0,2. Local write-shape test
  `verify_delta_catalog_materialize_rowtracking.test` (the appended row carries `__delta_row_id=1`); the row-id
  READBACK is Spark-only (see the validator below). **Default OFF** (no new feature declaration on the DV-default
  path → no Fabric-conversion risk to the validated path). **Compaction materialization — DONE + Fabric-Spark-
  validated (2026-07-04).** Because compaction mixes rows from several source files into one file, `CompactionExecutor`
  materializes BOTH `__delta_row_id` (each source's `baseRowId + position`, or the source's own materialized id when
  present) AND `__delta_row_commit_version` (each source's `defaultRowCommitVersion`) — a single
  `baseRowId`/`defaultRowCommitVersion` on the compacted `add` can't represent them (new
  `RowTrackingWriter.AddRowIdAndCommitVersionColumns`; the compaction read loop builds survivor id/version arrays
  in the DV filter's keep order so they stay aligned). Validated live: CTAS+2×INSERT (3 files) → OPTIMIZE+VACUUM,
  Spark reads `_metadata.row_id` = 0,1,2 (preserved, not the fresh `base_row_id`=3 range) and
  `_metadata.row_commit_version` = 1,2,3 (per-row originals, overriding the single `default_row_commit_version`=1).
  `test/verify_delta_catalog_compaction_rowtracking.test` (24); default compaction path (`verify_delta_catalog_optimize`)
  unchanged.
  **known-issues Delta SWEEP — FIXED (2026-07-05, EW `5e0bba2` + Bridge `50e37ca`; kernel-validated, delta+deltars
  suites green, EW DeltaLake 168 + Table 141 green):** (1) **CHECKPOINT corruption (critical)** — `CheckpointWriter`
  dropped `add.deletionVector` (+ `baseRowId`/`defaultRowCommitVersion`) and the protocol
  `readerFeatures`/`writerFeatures`, so with DV-default DML and `CheckpointInterval=10` **deleted rows RESURRECTED
  after 10 commits** (even same-session; repro'd then fixed writer+reader). Also made checkpoints
  delta-kernel-readable: `metaData.format.options` emitted + top-level action structs NULLABLE per the spec schema
  (kernel rejects always-present structs with null required children — the DV struct is nullable too). (2)
  **Partition-value interop** — null partitions = JSON null in `add.partitionValues` (`__HIVE_DEFAULT_PARTITION__`
  is only the DIRECTORY name; serializer + checkpoint map values null-safe); reads decode null/sentinel/missing as
  typed NULL columns (`BuildConstantArray` also gained Int16/Int8/Float/Double/Decimal128 + invariant culture +
  THROWS on unsupported instead of a wrong-typed string fallback; `GetStringValue` same policy on write); decimal
  partitions dot-notation; timestamp partition values emit the no-fraction spec form when fraction==0. (3) **PATH
  ENCODING (new EW `DeltaPath`)** — partition dirs use Spark's `escapePathName` (escapes `:` `%` `/` …, NOT space —
  Windows-safe), and **`add.path` is the URL-ENCODED form of the on-disk relative path (spec), URL-DECODED at every
  EW read site + the Bridge native-reader URI builders** (rowid ordinal sort stays on the RAW encoded add.path on
  both sides); vacuum protects encoded+decoded names; pre-fix EW tables whose add.path held literal `%XX` need a
  rewrite (documented). This also makes Spark tables with escaped paths (spaces etc.) readable by us. (4)
  **`timestampNtz` feature** — a schema containing `timestamp_ntz` (any naive DuckDB TIMESTAMP!) now declares the
  required reader+writer feature at create; `AddColumnAsync`/`SetSchemaAsync` emit a protocol UPGRADE in the same
  commit when introducing it (legacy versions upgraded to table-features mode with implied features enumerated —
  `LegacyWriter/ReaderFeatures`); previously Spark/kernel REJECTED every table with a naive-timestamp column.
  `generatedColumns` allowlisted + enforced like invariants. (5) **Stats/row-tracking** — `tightBounds:false` on
  DV-carrying adds; string min/max truncated at 32 chars Spark-style (max side last-char-incremented, omitted when
  impossible); **`delta.rowTracking` domainMetadata high-water mark emitted on every id-assigning commit**
  (write/external-commit/merge-on-read-update/compaction) and preferred via max() on snapshot rebuild — without it
  removes REGRESSED the derived mark and a later writer could reassign used row ids; `rowTracking` accepted as a
  READER feature; `commitInfo` gains `engineInfo`+`operationParameters`. **Known kernel quirk (not ours):**
  duckdb-delta/delta-kernel reads a column-MAPPED partitioned table's partition column as all-NULL (physical-keyed
  partitionValues per the Spark convention we validated live; Spark reads it fine). **Second pass (same day):**
  (6) **checkpoint REMOVE TOMBSTONES** — snapshots track tombstones (`Snapshot.Tombstones`, keyed by
  ReconciliationKey — a DV remove+re-add of the same path keeps the old-(path,DV) tombstone, correct);
  checkpoints include unexpired ones (`delta.deletedFileRetentionDuration` honored when parseable, default 7d)
  + `add.tags` preserved through checkpoints; (7) **CDF `_change_data` files under mapping** written
  PHYSICAL-named + field_id'd like data files (`CdfWriter` gets the snapshot; `_change_type` stays unmapped) —
  Spark reads cdc parquet through the table mapping. Remaining documented gaps: binary partition values (clean
  error), orphan DV `.bin` vacuum, nested-field stats.
  **engineered-wood WRITE interop caveats (reviewed 2026-07-02; from its `doc/known-issues.md`):** engineered-wood
  is a from-scratch C# Parquet/Delta stack, so the write path diverges from Spark/parquet-mr in a few subtle ways —
  none a show-stopper (Fabric/DuckDB/arrow-rs read our output, validated live), but the two "Spark-ecosystem
  friendliness" candidates are: (1) **writes DataPage V2 by DEFAULT, not V1** (`ParquetWriteOptions.DataPageVersion`
  defaults to V2; `DeltaWriter.Options()` doesn't override → all Delta writes are DataPage V2, *newer* than Spark's
  V1 default and still "experimental" in parts of the ecosystem; a V1 pin is a one-liner — `DataPageVersion.V1` in
  `DeltaWriter.Options()`); (2) **deprecated parquet `min`/`max` always emitted with SIGNED byte ordering + no
  `column_orders`** → wrong ordering for UTF-8/unsigned columns *if* a legacy reader falls back to them (modern
  readers use `min_value`/`max_value`, correct → low risk). Lesser: ns-timestamp write sets `ConvertedType=micros`;
  Delta stats have no string-min/max truncation + only top-level primitives; DVs are inline/UUID-relative only,
  one file per delete; features only settable at create (matches our design). Both cheap defensive fixes are
  DEFERRED until a concrete reader complains. Full list: [docs/delta-catalog.md](docs/delta-catalog.md)
  §"engineered-wood interop caveats". **The structural fix for all of these = the native-write inversion**
  (DuckDB's parquet writer for data files + engineered-wood only for the `_delta_log` metadata) — **design +
  phasing (incl. rowid/row-commit-version materialization for DML + CDF + a future DuckLake→Delta bridge):
  [docs/native-delta-write.md](docs/native-delta-write.md)**. Positioning: one backend, a `native_write`
  toggle symmetric to `native_read`, with the `delta` alias defaulting both on (hybrid = prod) vs
  `engineeredwooddelta` (pure EW = sample/driver-test). **P0 spike + P1 (INSERT/CTAS/append) DONE (2026-07-04,
  C#-only + one EW seam, NO ABI/C++).** The **`native_write true`** ATTACH option routes INSERT/CTAS/append data
  files through DuckDB's OWN native parquet writer; the `_delta_log` commit stays engineered-wood. **The EW
  seam** = `DeltaTableOptions.DataFileWriter` (new `IDataFileWriter`): `WriteCoreAsync` delegates each partition
  file's parquet bytes to it instead of the built-in `ParquetFileWriter` — partition split / row tracking /
  column mapping / stats / the `add` / commit / OCC unchanged (override null by default → default path
  byte-identical). Fabricator's `NativeParquetDataFileWriter` binds the streamed batch via the existing
  `Host.Query(sql, inputs)` (host-query v42–v45 — so **gate A input-binding worked directly; the temp-IPC gate B
  was never needed**) and runs `COPY (SELECT * FROM <batch>) TO '<root>/<file>' (FORMAT parquet,
  WRITE_BLOOM_FILTER true, RETURN_STATS)`, reading back `file_size_bytes` for `add.Size` (stats stay EW's
  `StatsCollector.Collect(batches)` — batches in hand). URIs reuse `DeltaReader.ToReadableRoot` (onelake://
  rewrite). **§9 acid test PASSED (local):** the official `delta_scan` (delta-kernel-rs) reads it (6 rows, exact
  `DECIMAL(10,2)`+`DATE` — the types EW's codec mangled), and **bloom filters flow through** on dict-encoded
  columns (`grp` 5-distinct → `has_bloom=true`; `id` all-distinct → none) = the maturity win AND a deterministic
  native-write signature (EW default writes no bloom). `test/verify_delta_catalog_native_write.test` (36); default
  path unregressed (write/decimal/temporal/partition/overwrite_merge/update/delete green). **Opt-in only (default
  off);** the `delta`-alias default-on is deferred (needs the resolved provider name threaded to the
  `DeltaCatalog` ctor — a separate policy step). **P3 (DELETE) DONE (2026-07-04):** under `native_write` the
  copy-on-write DELETE rewrites the survivor file with DuckDB's native writer too (the `IDataFileWriter` seam
  generalized to a batch **list** — a rewrite writes a file's survivor batches as one parquet), EW still
  selects/reads the affected files + commits `remove`+`add`; the READ half stays EW's reader (fully-native
  `read_parquet … WHERE file_row_number NOT IN` rewrite deferred), DV-mode delete unchanged (no data rewrite).
  Acid-tested (delta_scan reads the 5 survivors, exact decimals); default delete/dv/update unregressed.
  **P4 (UPDATE) DONE (2026-07-04):** the copy-on-write UPDATE rewrite likewise writes the modified file with
  DuckDB's native writer (same list-form seam at the UPDATE site); EW reads the affected files, applies the SET
  substitution (still C# `BuildArray` — the SQL-join substitution is deferred), and commits `remove`+`add`.
  Acid-tested (constant/string/expression updates → delta_scan matches, exact decimals).
  **P5-append (row tracking) works FREE on the native path (2026-07-04):** `row_tracking true` + `native_write
  true` → EW materializes the row-id column into `physicalBatch` + assigns `baseRowId`/`defaultRowCommitVersion`
  on the `add` before the writer, so DuckDB just emits the bytes (writer-v7 + `rowTracking`, `baseRowId` 0/5,
  delta_scan reads it). `verify_delta_catalog_native_write.test` (87 — INSERT/CTAS/append + bloom signature +
  DELETE + UPDATE + row_tracking + durability); default update/delete/dv/changes unregressed. **VALIDATED LIVE
  on Fabric OneLake (2026-07-04):** workspace `Test` / lakehouse `LH` (schema-enabled) — `native_write true`
  CTAS + INSERT + DELETE + UPDATE on `lake.dbo.fabricator_nwtest` round-tripped correctly over `onelake://` (v56
  OneLake write callbacks + DuckDB's native writer), and a 5000-row low-card table showed the DuckDB bloom
  signature on the dict column via `parquet_metadata('onelake://…')` (EW writes none) — confirming DuckDB (not
  EW) wrote the OneLake parquet. **Partitioned `native_write` DONE (2026-07-04):** EW splits by partition +
  calls the writer per Hive-partition file; `NativeParquetDataFileWriter` creates the `<col>=<value>/` parent
  dir first (`HostFs.CreateDir`, best-effort) since DuckDB's single-file COPY doesn't mkdir — also the
  prerequisite for native CDF `_change_data/`. `verify_delta_catalog_native_write.test` (107).
  **Fully-native copy-on-write REWRITE + SQL-join UPDATE substitution DONE (2026-07-04, commit `143c09d`) —
  read half native, `BuildArray` retired for the native path.** DELETE/UPDATE now READ the source via
  `read_parquet` AND apply the row-level transform in DuckDB SQL, not just write natively. New engineered-wood
  seam **`IDataFileRewriter`** (`DeltaTableOptions.DataFileRewriter`, next to `DataFileWriter`): the copy-on-write
  DELETE/UPDATE loops delegate the source read + transform to it for the clean shape (no column mapping, no
  partitions, no type widening, no CDF — gated in EW), falling back to EW's own reader + the in-process
  `rewriteFile`/`TakeRows` transform otherwise. Fabricator's `NativeParquetDataFileRewriter` (a *reader*: returns
  the transformed batches; EW keeps stats/writer/commit) runs per affected file `SELECT <cols> FROM
  read_parquet(src, file_row_number => true) WHERE file_row_number NOT IN (<deleted positions, bound anti-join>)`
  (DELETE) or a `LEFT JOIN` against the (position → new SET values) rows bound as an Arrow view, substituting via
  `CASE WHEN u.__fabricator_pos IS NOT NULL THEN u.<col> ELSE p.<col> END` (UPDATE) — **retiring the in-process
  typed `BuildArray`** for the native path. **DV-aware** (the file's existing deletion-vector positions are
  passed as `excludePositions`, so a copy-on-write UPDATE on a DV table rewrites only live rows) + **schema-
  evolution backfill** (the rewriter probes the source file's columns via `read_parquet … LIMIT 0` and emits a
  typed `CAST(NULL AS <t>)` for a column ADDed after the file was written — no gate needed). **Stats stay EXACT
  via EW's `StatsCollector` on the returned batches** — `RETURN_STATS`/`parquet_metadata` surface DECODED VARCHAR
  min/max whose float/double rounding could skip live rows (violates the exact-or-nothing rule), so the stats
  bridge is StatsCollector-on-the-batches NOT the footer strings. Verified: §9 acid test (official `delta_scan`/
  delta-kernel reads the native-rewritten table — exact decimal/date/double, DELETE + UPDATE applied) +
  **LIVE on Fabric OneLake** (`read_parquet('onelake://…')` source read + native write over `onelake://`, DELETE
  + UPDATE round-trip, decimals exact); `verify_delta_catalog_native_write.test` (147 — + DV-table UPDATE +
  schema-evolution UPDATE); default-path + delete/update/dv/changes/partition unregressed. **Still pending:
  the P5 REWRITE half (materialize `row_id`+`row_commit_version` on copy-on-write DELETE/UPDATE — the deferred
  gap in BOTH paths, a deep EW row-tracking change) + CDF native change files (CDF tables keep the EW-read path
  today — needs the rewriter to also produce the deleted/pre/post-image rows + `CdfWriter` on the native writer)
  + delta-alias default-on.**
  **STREAMING native bulk write — bounded memory (2026-07-04, C#-only + one EW seam, NO ABI/C++; live-OneLake
  validated).** The prior native_write INSERT/CTAS/append path fully COLLECTED the dataset in C# first
  (`DeltaWriter.Materialize` — an Arrow-IPC round-trip into a `List<RecordBatch>`) before writing — bounded by
  RAM, a problem in a Fabric notebook. **`native_write true` now STREAMS** the bulk write: `DeltaWriter.TryWriteStreaming`
  binds the LIVE channel stream (the same `ChannelArrowStream` the SQL Server path feeds `SqlBulkCopy`, cap 8,
  backpressured) straight into ONE DuckDB `COPY (SELECT * FROM <stream>) TO '<root>/<uuid>.parquet' (FORMAT
  parquet, WRITE_BLOOM_FILTER true, RETURN_STATS)` — DuckDB pulls batches incrementally + streams row-groups to
  disk, so the whole dataset NEVER materializes in C#. `RETURN_STATS` yields the file's `count`(→numRecords) +
  `file_size_bytes`(→add.Size); the single `add` is committed via the **new EW commit-only seam**
  `DeltaTable.CommitDataFilesAsync(IReadOnlyList<WrittenDataFile>, mode)` — the commit half of `WriteCoreAsync`,
  factored out: it builds the `add`(+ `remove`s for Overwrite), assigns row-tracking `baseRowId`/
  `defaultRowCommitVersion` per file (high-water mark derived on snapshot rebuild — no domainMetadata action),
  prepends commitInfo, and writes with OCC retry. **Row tracking rides on `baseRowId`** (no physical
  `__delta_row_id` column — the collect+native path injects one; its ABSENCE in the streamed parquet is the test
  signal that streaming ran). **FULL data-skipping stats** (min/max/nullCount + numRecords) are emitted for the
  streamed file — parsed from DuckDB's `RETURN_STATS.column_statistics` (a **MAP(colname → MAP(statname → text))** —
  a map-of-maps, everything stringified; `NativeParquetDataFileWriter.BuildDeltaStats` walks both `MapArray`
  levels via `KeyValues`) and typed from the write schema into the Delta stats JSON via an **exact-or-omit**
  policy: min/max emitted for integer / decimal / string / boolean / date / naive-timestamp (decoded text is
  exact; integers/decimals via `WriteRawValue` so no double round-trip; timestamp space→'T'), and **OMITTED for
  float/double** (decoded text may round → a too-narrow min could wrongly skip a file — correctness-safe by
  omission) and tz-timestamp / time / nested (format risk); `nullCount` + `numRecords` always (exact integers).
  On any parse hiccup it falls back to numRecords-only (never fails the write for stats). So the streamed file
  gets the SAME skipping quality as the collect path (better on float/double, which the collect path emits
  possibly-imprecise). **PARTITIONED writes STREAM too (local/S3)** — `DeltaWriter.TryWriteStreaming` branches on
  the table's `Metadata.PartitionColumns`: non-partitioned → one COPY to a single file; partitioned →
  `NativeParquetDataFileWriter.RunCopyPartitioned` runs ONE `COPY … TO '<root>' (PARTITION_BY (cols), APPEND true,
  FILENAME_PATTERN '{uuid}', RETURN_STATS)` that streams the whole Hive `col=val/<uuid>.parquet` layout in a
  single pass (bounded memory), excluding the partition columns from the files (Delta convention); `RETURN_STATS`
  returns one row PER FILE → each becomes a `WrittenDataFile` with its `partitionValues` (from `partition_keys`, a
  `MAP(colname→value)`) + per-file stats, all committed in one `CommitDataFilesAsync`. `RunCopy` returns per-file
  `CopiedFile` records (relpath from `filename`, rows, size, partitionValues, stats); the shared `ReadFileStats`
  parses both. **Partitioned streaming works on OneLake too** — DuckDB's partitioned COPY needs a writable
  DIRECTORY target, which the `onelake://` C++ FileSystem now supports: `FabricatorOneLakeFileSystem` overrides
  `DirectoryExists`→false + `CreateDirectory`→no-op (ADLS Gen2 dirs are implicit — a blob write materializes the
  hierarchy), and the managed `OneLakeForwardFs.Exists` returns FALSE for a directory (via the `hdi_isfolder`
  metadata marker) so DuckDB's setup doesn't mistake the table root for a file (the old *"exists and is a file,
  not a directory"* error). With `APPEND true` + `FILENAME_PATTERN '{uuid}'`, `CheckDirectory` early-returns and
  the per-partition files are written by `OpenFile`-for-writing (the same mechanism the single-file path uses).
  **Falls back to the collect path** (`Materialize` + `Write`, `data` untouched — the fallback is decided BEFORE
  any COPY via EW's `SupportsExternalDataFileCommit` + `Metadata.PartitionColumns`, so no orphan file) only for:
  `replace_where`, `schema_mode=merge`, or a table needing EW's own writer (column mapping / identity /
  IcebergCompat). The **EW-codec path (`native_write` off) is unchanged** — still collects (the user's call: only
  the native path streams). DELETE/UPDATE (rewriter paths) unaffected.
  Verified: `test/verify_delta_catalog_native_write_streaming.test` (29 — no `__delta_row_id` in the streamed
  file, bloom signature present, **min/max/nullCount in the commit: int→JSON number, string→JSON string,
  double→omitted**, 8000-row append, **partitioned CTAS+INSERT stream: Hive layout, partition column excluded, no
  `__delta_row_id`, per-file partitionValues+stats in the commit**) + native_write (147) + optimize/partition/
  overwrite_merge/update/dv/write/native_read/decimal/changes/time_travel/alter/snapshots unregressed; **live
  OneLake** (non-partitioned CTAS 5000 + append→8000 + DELETE→7920; **partitioned CTAS+INSERT stream —
  `partitioned copy onelake://… → committed v1/v2 files=3 (native COPY, bounded memory)`, 200 rows/region**;
  commit carries `minValues`/`maxValues`/`nullCount`).
  **OCC RETRY DONE (concurrent writers):**
  engineered-wood `WriteCommitAsync` throws `DeltaConflictException` when a concurrent writer takes the target
  version; `DeltaWriter.Write`/`Create` (append/CTAS/create) catch it and retry by reopening at the new latest
  version (bounded `MaxCommitAttempts=16`) — the data is snapshot-independent so re-commit is safe. Rowid
  DELETE/UPDATE do NOT retry (their absolute positions are tied to the scanned snapshot; a concurrent change
  invalidates them) — `DeltaReader` surfaces a clear "concurrent modification — retry the statement" error.
  Verified: 4 parallel processes appending 200 rows each to ONE local Delta table → 800/800 distinct, no lost
  commits, no surfaced conflicts.
  **EXPLICIT TRANSACTIONS — SNAPSHOT-ISOLATED, BUFFERED (slices 1–4, 2026-07-07). Consolidated
  semantics reference (modes × paths × isolation, rebase checks, guards, multi-writer by storage
  + §10 Databricks-comparison SQL scenarios — repeatable reads + atomic multi-statement + true
  ROLLBACK are OUR advantages over classic Databricks/Spark [no multi-statement txns there,
  snapshot per QUERY]; Databricks' advantages = row-level concurrency [DBR 14.3+, DV-based
  same-file disjoint-row DML — we conflict at FILE level like OSS Spark] and the
  `delta.isolationLevel` TABLE property — **NOW HONORED (2026-07-15): the effective isolation for the
  buffered-DML flush's OCC check + row-level relaxation is the TABLE's `delta.isolationLevel` (Serializable
  vs the WriteSerializable default), read once per (txn,table) and cached on the buffer
  (`PendingSerializable`); the ATTACH `isolation_level` option is the FALLBACK when the property is absent
  (backward-compatible — property-less tables follow the catalog default). So our writer conforms to the
  guarantee the table ADVERTISES, uniform with Spark — the whole reason Delta centralizes isolation as a
  table property (mixed per-writer levels don't corrupt — each writer's own check preserves its own
  guarantee — but they make the table's advertised guarantee non-uniform). Autocommit single-statement DML
  still uses the catalog default for its row-level-retry resilience knob (documented minor divergence).
  Change a table's level (or any `delta.*` config) with the new **`fabricator_delta_set_tblproperties(catalog,
  'schema.table', '{"delta.isolationLevel":"Serializable"}')`** table function (SET/UNSET via ONE metaData
  commit — merged `configuration`, rides `extraActions` like a buffered ALTER; feature-enabling keys
  [`delta.enable*`, `columnMapping.mode`] rejected — those need a protocol upgrade at CREATE via the ATTACH
  option; kernel-valid; re-attach-durable) + **`fabricator_delta_tblproperties(catalog, 'schema.table')`**
  (READ, (property,value) rows). Both are table functions (side-effecting op must NOT be a scalar — optimizer
  purity), additive metadata kinds 13/14 (NO ABI bump), Bridge-only (no EW/C++-operator change — just 2 binds).
  `test/verify_delta_tblproperties.test` (34 — round-trip/UNSET/guard/re-attach + the property overriding a
  write_serializable catalog in a two-connection racer). Regression-free (transactions 941 / row_level 70 /
  update 63 / delete 28 / row_tracking_virtual 299 / dv_default 58 / txn_version 51 / changes 73 / native_write
  147). **CREATE-TIME STAMP — DONE (2026-07-15):** ATTACH `isolation_level 'serializable'` now stamps
  `delta.isolationLevel=Serializable` into CREATEd tables (all 3 create paths — `DeltaWriter.Write`/
  `TryWriteStreaming`/`Create` gained a `serializable` param threaded from `_serializable`); write_serializable
  is the Spark default so it's left ABSENT (no stamp), matching Spark's minimal metadata. `verify_delta_tblproperties`
  now 42 (serializable-ATTACH create stamps it; default create leaves it absent). **STALE-BINARY:** the loadable +
  linux payload predate the 2 new C++ function binds — rebuild before the next dbt/notebook run.):
  [docs/delta-transactions.md](docs/delta-transactions.md).** The engineered-wood
  Delta provider buffers a DuckDB transaction's writes per (txn, table) (`DeltaTxnBuffer`, keyed by the v35
  `AmbientTransaction` id) and flushes at COMMIT as **ONE atomic Delta commit per table** (Delta has no
  cross-table txn); **ROLLBACK discards** (streamed-but-uncommitted parquet = invisible orphans → vacuum,
  Spark's rollback shape). **Slice 1 — appends (all txns incl. autocommit, semantics-neutral there):**
  `native_write` streams files as before but PARKS the `WrittenDataFile` list
  (`TryWriteStreaming(deferCommitTo:)`; flush = `CommitDataFilesAsync` with OCC retry — appends are
  snapshot-independent); the codec path parks materialized batches (flush = `DeltaWriter.Write`).
  **Slice 2 — buffered DELETE/UPDATE = snapshot isolation (EXPLICIT txns only, gated by the v60
  `begin_transaction(is_explicit)` flag; autocommit keeps the direct per-statement paths — CDF capture,
  copy-on-write — byte-identical):** DELETE buffers (pinned-snapshot file ordinal → absolute positions)
  per table (`BufferDeleteRows` — rowids decoded Bridge-side); UPDATE buffers its old rows the same way +
  builds post-image rows at statement time (`BufferUpdateRows`: EW `ReadRowsByRowIdsAsync` reads exactly
  the matched rows, then the SET values substitute via the existing `BuildArray`; post-images join the
  pending append batches). **LIFETIME CORRECTION (2026-07-16, user-driven): the long-recorded "EW batch
  buffers don't outlive the open table" belief is FALSE and the read-back's Arrow-IPC deep copy was
  REMOVED** — EW batches are SELF-OWNED (every `ArrowArrayBuilder` output is a fresh
  `Apache.Arrow.Memory.NativeBuffer` native allocation behind a refcounted `SharedMemoryHandle` WITH a
  finalizer; decode-path `ArrayPool` rentals are all rent→copy-out→return in-scope; `DeltaTable.Dispose`
  has been a no-op `_disposed` flag since EW's original April commit — it never freed anything). Proven
  empirically (scratchpad/ewlifetime harness: materialize `ReadRowsByRowIdsAsync` + `ReadChangesAsync`
  with NO copy → dispose table → 20 re-reads churning the ArrayPool + disposing churn batches → forced
  GC+finalizers ×3 → all 20k rows byte-exact). The 2026-06-30 GetChanges "Out of Range string size" that
  spawned the belief was almost certainly the CDF update_preimage 6-vs-5-column schema mismatch fixed in
  the SAME commit (`7f0563a`), misattributed to buffer lifetime. `GetChanges` stays lazy purely for
  bounded memory. Batches imported over the C ABI (bulk channel) are a DIFFERENT lifetime domain — their
  copies (`DeltaWriter.Materialize`, `ProjectPending` clone) are NOT covered by this finding; leave them.
  **Follow-up (same day, user's design): the read-back is now a LAZY STREAM** — `DeltaReader.
  ReadRowsByRowIds` returns `IEnumerable<RecordBatch>` (async iterator holding the table open for the
  enumeration + a net8-safe `BlockingEnumerable` adapter — .NET 9's `ToBlockingEnumerable` is unavailable
  on the net8.0 target; EW's `ReadRowsByRowIdsAsync` param order changed to token-LAST, sourceTracking
  entries appended BEFORE each yield so in-loop indexing works). The **CDF-DELETE capture is fully
  streaming** (read-back → `DropRowIdStreaming` [per-batch dispose after consumption — the derived batch
  aliases the source columns] → `WriteCdcFiles(IEnumerable<RecordBatch>)` with LAZY table open, so an
  empty statement still never touches storage — one batch in flight, a huge CDF DELETE never materializes
  its matched rows). The **buffered-UPDATE deliberately keeps its pre/post-image accumulation**: the
  all-or-nothing stable-ids decision (original vs fresh `__delta_row_id`) is only known after the WHOLE
  statement, so its post-images can't stream into the eager write without changing row-identity semantics.
  **SNAPSHOT READS ARE THE DEFAULT (2026-07-11)**: inside an explicit transaction, the FIRST scan captures
  one UTC instant (`SnapshotPinning`, per txn — the MVCC snapshot-at-first-read shape, like Postgres
  REPEATABLE READ; capturing at literal BEGIN is impossible since catalogs are touched lazily) and each
  table resolves it to a version on first touch — EVERY scan in the transaction then reads that consistent
  cut on BOTH paths (native always did; the CODEC plain/rowid reads now route through
  `GetSchemaAt`/`StreamAt`/`StreamWithRowIdsAt` at the pin — previously codec reads floated to latest until
  the first DML pinned). A concurrent commit is invisible mid-transaction; autocommit is unchanged (a
  single codec statement is one snapshot anyway; native pins per statement for the cross-table cut).
  Recording happens in `ScanTable` alongside the read-set capture (skipped for pending-CREATED tables —
  nothing on storage to pin, the gotcha found in the build). **PinnedVersion** (= the pin above; the DML
  paths reuse it via `TryGetPinned ?? profile.Version`, so buffered positions are always consistent with
  what the pinned scans read); duckdb-delta's `VERSION`/`PIN_SNAPSHOT` ATTACH options were considered and
  REJECTED for the folder-catalog (they pin ONE table — duckdb-delta attaches a single table; a
  catalog-wide version is meaningless across tables — the per-txn instant→per-table-version mapping is the
  correct multi-table analog and is now the default). **Flush fusion**
  (`FlushDmlTransaction`) with **Spark-style LOGICAL REBASE on concurrent writers (2026-07-11), FULL
  ConflictChecker parity incl. READ-PREDICATE tracking**: when the table moved past PinnedVersion, EW
  **`CheckLogicalRebaseAsync(pinnedSnapshot, plannedActions, readPredicates, readWholeTable,
  serializable)`** walks the concurrent commit range (pinned+1..latest via `TransactionLog.ReadCommitAsync`)
  and decides whether the concurrent commits COMMUTE — commuting appends pass (the COMMIT rebases on top:
  DV ordinals/old-DVs resolve against the PINNED snapshot via
  `ComputeDeletionVectorActionsAsync(resolveAgainst:)` since the newer snapshot's path-sorted ordinals
  differ, the remove+add pairs stay valid because the touched files are unchanged, and row-id/identity
  high-water marks re-derive from the snapshot committed onto); REAL conflicts abort with the clear
  "transaction conflict … the concurrent changes do not commute" error. **The four checks (Spark
  ConflictChecker parity)**: metadataChangedCheck (schema/partitioning/config — buffered ALTERs chained
  against pinned metadata), protocolChangedCheck, concurrentDeleteDeleteCheck (any planned `RemoveFile`
  whose (path, DV) is no longer active unchanged — covers concurrent DELETE/UPDATE/OPTIMIZE of a file we
  modify), and the **READ-SET checks**: concurrentDeleteReadCheck (a concurrent data-changing remove of a
  file our reads consumed) + concurrentAppendCheck (a concurrent data-changing add matching our reads —
  from non-blind-append commits always; from blind appends only under `isolation_level 'serializable'`).
  **The read set**: `ScanTable` records every explicit-txn scan's PUSHED predicate (the built EW
  `Predicate` — a superset of the rows consumed, since unpushed residue filters above the scan) or a
  whole-table flag when nothing pushed, on `DeltaTxnBuffer.PendingAppends.ReadPredicates`/`ReadWholeTable`
  (deliberately NOT in `HasAny` — read-only entries trip no guards; `spec == null` scans are the BIND-TIME
  schema probe, excluded — the gotcha found in the build: every statement's first ScanTable call is the
  probe with no spec, which recorded a false whole-table read). Predicate-vs-file matching =
  `DeltaFilePruner.ShouldInclude` over the pinned schema (partition values exact, stats conservative);
  `dataChange=false` actions (OPTIMIZE) are exempt from the read checks (rows unchanged; a compaction of a
  file we MODIFY still hits delete/delete). **The `isolation_level` ATTACH option**
  (`'write_serializable'` default = Spark's default | `'serializable'`): serializable makes commit order
  the logical order — blind appends matching the reads abort too — and the FIRST read of a table also PINS
  the txn's base version (`SnapshotPinning`), so an APPEND-ONLY transaction that read the table routes
  through the checked flush (under write_serializable it stays on the blind OCC path — a documented
  divergence: Spark would deleteRead-check append-only txns too). The commit runs `expectedVersion:
  CurrentSnapshot.Version` in a bounded reopen+revalidate retry loop (a writer landing mid-flush).
  Two-connection racer tests pin all outcomes: append→rebase-success (both changes land), same-file
  delete/delete→abort, concurrent ALTER→abort, serializable+predicate-matching append→abort,
  serializable+non-matching append→rebase-success (stats exclude), whole-table-read (unpushable WHERE) +
  concurrent delete of an unmodified file→deleteRead abort. **ROW-LEVEL CONCURRENCY v1 (2026-07-14,
  Databricks-style — OSS Spark/delta-rs/kernel are all FILE-level): concurrent DML touching the SAME
  FILE no longer conflicts when the touched ROWS are disjoint** (under `write_serializable` only;
  `serializable` keeps strict file-level checks; DV tables only — CoW rewrites can't reconcile, so
  the `deletion_vectors false` PolyBase recipe keeps file conflicts). Mechanism = EW
  **`RebaseDvDmlActionsAsync`**: the loser's DV remove+add pairs re-target onto the LATEST snapshot —
  path must still be active (a concurrent OPTIMIZE/CoW rewrite stays a conflict; v2 idea = row-id
  remap across rewrites via `__delta_row_id`), OUR newly-deleted positions must be DISJOINT from the
  concurrent deletions (absolute in-file positions are stable across DV swaps — same-row overlap ⇒
  "row-level conflict on file … N row(s) … concurrently deleted or updated"), then remove(path,
  currentDV) + add(path, currentDV ∪ ours); post-image adds re-derive baseRowId/version + the HWM
  action from the target snapshot. Wired THREE places: the buffered-flush retry loop (rebases per
  attempt; `CheckLogicalRebaseAsync(rowLevelDml:)` relaxes deleteRead for still-active paths +
  concurrentAppend for swap re-adds — so a txn that READ a file concurrently DV-swapped no longer
  aborts) + BOTH autocommit DML paths (`DeleteByRowIdsViaVectorsAsync`/`UpdateByRowIdsAsync` gained
  `rowLevelRetry` — a bounded reload+rebase+retry loop replaces the old "retry the statement" error).
  BEYOND Databricks: works on PARTITIONED tables and inside multi-statement transactions (theirs is
  unpartitioned + per-statement). Semantics note: position-targeted DML can't produce write skew
  (SET values derive only from the matched row), so the relaxation is serial-equivalent. Transactions
  §6e REWRITTEN (the old whole-table-read deleteRead abort now COMMITS — both deletes land — with a
  `serializable` strict counterpart pinned; suite 941). **v2 (same day): the REWRITE boundary is gone —
  a concurrent OPTIMIZE / CoW rewrite of a touched file REMAPS instead of conflicting**
  (EW `RemapRowsAcrossRewriteAsync`, invoked from the rebase when a touched path vanished): the
  TOMBSTONED source file (parquet on storage until VACUUM) is read at the base snapshot to resolve the
  target rows' STABLE IDS + ORIGINAL commit versions (materialized columns else baseRowId+position /
  defaultRowCommitVersion); the post-rewrite files (active-in-to \ active-in-from; compaction-shaped
  dataChange=false candidates first, early exit; fresh appends can't hold the ids — derived ids sit
  above the base HWM, so only materialized-id files match) are scanned for the ids; **the row's commit
  version is the concurrent-modification discriminator** (relocated-untouched keeps its original
  version — compaction + CoW pass-through both materialize it; concurrently UPDATED carries the
  rewrite's version ⇒ row-level conflict; found NOWHERE ⇒ concurrently deleted ⇒ conflict — a
  DV-hidden row resolves here too); found positions become DV pairs on the NEW files. Requires row
  tracking (the default; no-tracking tables keep the path-level conflict). **Databricks itself still
  conflicts on compaction — v2 is capability beyond ANY Delta engine.** The rowLevelDml read-check
  relaxation is now a FULL skip (the row-level write validation replaces deleteRead+append checks —
  WriteSerializable's reads-don't-serialize definition; matches Databricks' WS matrix). Racer test now
  70 (DELETE + buffered UPDATE THROUGH a concurrent OPTIMIZE compose — value lands on the compacted
  file, post-image kept; same-row-through-rewrite conflicts via the version discriminator / not-found);
  kernel reads the remapped commits exactly; dv 48 / dv_default 58 / update 63 / delete 28 /
  changes 73 / row_tracking_virtual 299 / txn_version 51 / optimize 40 / transactions 941 + EW 168 &
  147 (all TFMs) green. Semantics doc: docs/delta-transactions.md §6 + §10.4 (now PARITY+). **EW now carries SELF-CONTAINED
  xunit suites for the whole transaction-era API surface (2026-07-14, for upstream review — Curt can run
  them without our DuckDB harness): `RowLevelConcurrencyTests` (7 — v1/v2 racers via two table handles),
  `LogicalRebaseTests` (7 — WriteSerializable vs Serializable blind-append semantics, stats-based read
  matching, deleteRead/delete-delete/metadata, compaction exemption), `BufferedTransactionTests` (6 —
  fused ALTER+INSERT+DELETE one-version commit, chained Compute*, ReconcileBatchToFields overlay,
  ReadRowsByRowIdsAsync atVersion, expectedVersion abort, txn-action round-trip),
  `SchemaWriteModesTests` (6 — SetSchemaAsync adopt/no-op, repartition-on-overwrite, static+dynamic
  partition overwrite, appendOnly), `IdentityTransactionSeamsTests` (4 — mark chaining, fused HWM
  commit, ForSchema pending-create, un-valued guard), `WriterFeatureEnforcementTests` (4),
  `TimestampResolutionTests` (1) — Table.Tests now 182 (was 147 pre-row-level; older "EW 168 & 147"
  gate counts in this file are historical). doc/upstream-candidates.md refreshed: on-disk-DV second
  pass in slice 2, row-tracking preservation + MoR matrix + S3 conditional-put in slice 8, NEW slice 9
  (row-level concurrency), test pointers per slice. Fork pushed through `d09b966` (PR #4 auto-tracks).
  STALE-BINARY NOTE: ~~the linux notebook payload + the loadable were built BEFORE the mark-join DELETE
  fix~~ — **REBUILT 2026-07-15 on the Fabricator-rename branch** (post-rename + post-DELETE-fix):
  `build/linux-payload/fabricator.duckdb_extension` (34 MB, linux_amd64) + `fabricator-fdd-linux-x64.zip`
  (loose-root FDD, net8.0) + the Windows loadable `build/release/extension/fabricator/fabricator.duckdb_extension`
  were all current AS OF 2026-07-15 (the linux payload has since gone stale again — see the BINARY
  STATUS note at the ABI-version bullet). The `scratchpad/fabricnb` driver was updated to the fabricator payload names (its Fabric
  NOTEBOOK item name stays `arrownet_ext_probe` — the SP can't recreate notebooks). Linux build: rsync the
  renamed `src/` into WSL `~/sqlext`, `cmake --build … --target fabricator_loadable_extension`.
  **LINUX LOAD-SMOKE VALIDATED (no dotnet needed pre-installed):** the FDD payload is framework-dependent, so
  a **downloaded-and-extracted** .NET 8 runtime suffices — `dotnet-install.sh --runtime dotnet --install-dir
  ~/dotnet8` (no root/install), `export DOTNET_ROOT=~/dotnet8` + `FABRICATOR_MANAGED_DIR=<extracted FDD zip>`,
  then the duckdb wheel MATCHING the loadable's declared version (1.5.5 since the bump; `pip install --target`, no venv) `load_extension`s the loadable. On WSL Ubuntu
  22.04 (glibc 2.35 = Fabric Azure-Linux-3 baseline): `fabricator_version()`, delta CTAS/scan/filter-pushdown,
  and rowid DELETE all pass — so the payload is proven to LOAD + RUN on linux, not just compile.** **IDEMPOTENT APPENDS (2026-07-11) — Delta
  APPLICATION TRANSACTIONS (the `txn` action; duckdb-delta/Spark txnAppId parity, additive metadata kinds
  10/11 — NO ABI bump):** `CALL fabricator_delta_set_transaction_version(catalog, 'schema.table', app_id,
  version [, expected_previous])` PARKS the version on the current EXPLICIT transaction
  (`DeltaTxnBuffer.AppTxnVersions`; requires BEGIN — error otherwise; also pins the base version, so an
  append-only producer transaction routes through the checked flush); at COMMIT the flush
  compares-and-swaps against the LATEST snapshot's `AppTransactions` on EVERY retry-loop attempt (expected
  omitted/NULL = must-not-exist) and emits one spec `txn` action per app ATOMICALLY with the fused commit —
  a retried batch whose first attempt actually landed (crash-after-commit, the failure class plain OCC
  can't protect) fails the CAS with "transaction version conflict" instead of duplicating data.
  `fabricator_delta_get_transaction_version(catalog, 'schema.table', app_id)` reads the committed
  high-water mark (NULL row when never set; EW `Snapshot.AppTransactions` — the read side always existed).
  The C++ binds set the FIXED (app_id, version) schema directly — deliberately NO PopulateReturnSchema
  probe, so the side-effecting factory runs only at EXECUTION where ArrowStreamInitGlobal establishes the
  ambient txn (the bind-time probe would see no transaction and throw). kernel-reads the txn-action
  commits; `test/verify_delta_txn_version.test` (51 — lifecycle, blind-retry CAS failure + no duplicates,
  chained expected, two-producer race → exactly one wins, per-app independence, ROLLBACK discards,
  re-attach durability). Flush mechanics: write pending batches as files via the new
  EW **`WriteDataFilesAsync`** (write-no-commit half of
  the batch path: partition split, recursive mapping rename+field-ids, variant transport, `IDataFileWriter`
  seam, stats; NO row-id materialization — baseRowId assigned at commit like the streaming writer), compute
  DV actions via the new EW **`ComputeDeletionVectorActionsAsync`** (positionsByOrdinal → remove/add pairs
  with unioned inline DVs — pure metadata, no CDF), then ONE
  **`CommitDataFilesAsync(files, Append, extraActions: dvActions, expectedVersion: pinned, operation:)`**
  (extended: extraActions join the commit; expectedVersion ⇒ conflict-abort instead of the append retry;
  `HonorWriterFeatures(isAppend:false)` when removes present). commitInfo operation = DELETE / UPDATE /
  WRITE when single-kind, TRANSACTION when mixed. **Read-your-writes** on every in-txn scan: codec appends
  concat `ProjectPending` (`ArrayData.Clone` per yield; list snapshotted; synthetic rowid
  `(0x700000<<40)|pos`); with pending DML the codec scan is FORCED onto `StreamWithRowIdsAt(pinned)` and
  `DeltaTxnBuffer.ExcludeDeleted` drops the pending-deleted rows (rowid col removed again unless requested);
  native_read merges pending deletes into each file's DV exclusion (`WithPendingDeletes`) + appends pending
  FILES to the per-file loop (ordinals from 0x780000) + honors the buffer pin over SnapshotPinning; a
  `native_write`-without-`native_read` catalog mid-txn routes scans through `ScanNative`. Explicit `AT`
  time travel EXCLUDES pending. **Guards (never silently non-atomic — clean errors with the autocommit
  escape):** buffered DML requires DV-enabled + non-CDF tables (UPDATE additionally
  `SupportsExternalDataFileCommit` + not materialize_row_tracking); DML on rows inserted/updated in the
  SAME transaction ("COMMIT the inserts first" — pending rowids ≥ 0x700000); DROP/CREATE-OR-REPLACE/
  OPTIMIZE/VACUUM/non-append writes with ANY pending changes ("uncommitted buffered changes — COMMIT
  first"). A statement error ABORTS the DuckDB txn. **Slice 3 — buffered `ALTER TABLE … ADD COLUMN`
  (top-level; explicit txns):** the metaData (+ merged protocol-upgrade) action is computed at statement
  time via the new EW **`ComputeAddColumn(field, baseMetadata, baseProtocol)`** (compute-only extraction of
  AddColumnAsync — chained adds compose against the previous pending metadata/protocol; Bridge
  `MergeProtocol` unions feature lists) and parked on the buffer (`PendingMetadata`/`PendingProtocol`/
  `PendingDeltaSchema`/`PendingArrowSchema`); it joins the SAME fused commit → **ALTER + INSERT + DELETE +
  UPDATE in one BEGIN..COMMIT = ONE atomic Delta version** (kernel-validated: delta-kernel reads the fused
  metaData+protocol+DV+adds commit exactly), and ROLLBACK undoes the column. Overlays: `GetMetadata(Columns)`
  serves the pending schema (covers DuckDB's bind + the C++ eager post-ALTER re-fetch); codec scans strip
  pending-only names from the EW projection and RECONCILE each batch to the pending shape
  (`ReconcileBatch` — added columns backfilled via the typed-NULL `BuildArray`; `ExcludeDeleted` now derives
  its output shape from each batch); native scans pass the pending Delta schema into
  `ListNativeScanFiles(schemaOverride:)` so the per-file presence machinery emits `CAST(NULL AS type)`
  exactly like committed schema evolution (mapping maps recomputed from the pending schema — the added
  column's id/physicalName was assigned by the compute step); the buffered UPDATE's read-back reconciles
  before substitution (so `UPDATE t SET <new column> = …` works in the same txn); flush writes batches via
  `WriteDataFilesAsync(schemaOverride:)` and `BulkInsert` skips the streamed COPY under a pending ALTER
  (collect path, still native-written via the writer seam); pure-ALTER commit operation = the tracked kind
  ("ADD COLUMNS"/"RENAME COLUMN"/"DROP COLUMNS", several kinds → "ALTER TABLE"), mixed with data =
  "TRANSACTION". Order rule: ALTERs must come BEFORE the transaction's data changes ("after buffered data
  changes" error — writes then run schema-overridden; changing the schema under buffered rows is
  unsupported). **Slices 3b+3c — buffered RENAME/DROP COLUMN + nested ADD/DROP FIELD** (same mechanism; EW
  gained the chainable compute-only counterparts `ComputeRenameColumn`/`ComputeDropColumn`/`ComputeAddField`/
  `ComputeDropField` + the public **`ReconcileBatchToFields`** export of the read path's RECURSIVE
  schema-evolution reconcile — which made the nested codec overlay free). RENAME's read overlay is a
  **rename map** (pending name → committed name, composed across chained renames; a renamed pending-ADDed
  column deliberately has no entry): the codec projection translates pending→committed names for the EW
  read, `ReconcileBatch` re-labels batch columns committed→pending BEFORE the recursive reconcile (a naive
  name-match would silently NULL a renamed column — the trap that kept rename out of 3a), and the native
  path gets rename free (mapping physical names/field-ids are rename-stable, maps recomputed from the
  pending schema). DROP falls out of the reconcile's project-target-fields semantics. `AlterOps` on the
  buffer tracks kinds. Boundaries: **nested RENAME FIELD** stays IMMEDIATE (a per-level nested name map in
  the overlay — deferred; pinned in the test) + **RENAME of a partition column** in a txn is rejected (the
  flush's partition split runs against committed partition columns; clear error) + RENAME TABLE stays
  immediate (physical folder move). **Slice 4 — buffered CREATE TABLE / CTAS (fresh tables, explicit
  txns):** the table exists ONLY in the buffer until COMMIT (`PendingCreate` +
  `PendingArrowSchema`/`CreatePartitionColumns`; NOTHING touches the `_delta_log` before the flush — the
  key constraint is that DuckDB's rollback callback has no ClientContext/opener, so rollback can only
  DISCARD, never clean storage). Scans (`ScanPendingCreated` — both codec + native entry points) and
  binds (`GetMetadata(Columns)` via `PendingArrowSchema`) serve entirely from the buffer; CTAS collects
  (the streamed COPY would OpenOrCreate commit-0 mid-txn — the append branch also skips streaming under
  `PendingCreate`); the flush = today's autocommit CTAS commit shape (v0 CREATE TABLE + ONE WRITE for ALL
  buffered rows — CTAS + later INSERTs merge; single-commit CTAS = EW follow-up), with a concurrent
  same-name create conflict-aborting (commit-0 put-if-absent arbitrates; pre-check for the clear error).
  **CREATE + DROP in one txn cancels out** (`RemoveTable` — nothing ever on storage). Boundaries:
  ALTER/DELETE/UPDATE on a pending-created table error cleanly ("created in the same transaction — COMMIT
  the CREATE first"); identity-marked creates + CREATE OR REPLACE + CTAS over an existing table stay
  immediate (replace removes are snapshot-coupled; identity needs EW's committing writer). dbt is
  unaffected either way (a buffered model build is simply atomic now). Spark-style logical rebase on
  conflict: DONE 2026-07-11 — see the flush-fusion paragraph above (`CheckLogicalRebase`,
  WriteSerializable). C++: `CommitTransaction` sets `SetActiveOpener(&context)`
  before the ABI call (the flush writes `_delta_log` through the host FS); rollback needs no opener (the C++
  `InvalidateAllEntries` on rollback drops the pending-schema catalog entry).
  `test/verify_delta_catalog_transactions.test` (542 — atomic multi-INSERT, buffered DELETE/UPDATE with
  mid-txn visibility + rollback-undoes + one-commit-per-txn + operation names, mixed
  INSERT+DELETE+UPDATE→TRANSACTION, conflict-abort via a second connection, DV-off/CDF/same-txn-row guards,
  multi-table, AT-excludes-pending, re-attach durability, native_write fused flush (1000-row stream +
  DELETE + UPDATE), native_read read-your-writes, autocommit version counts + direct-DELETE path, buffered
  ALTER+DML fused commit + rollback-undoes-column + chained ADDs + SET-on-added-column + ALTER-after-data
  guard + both native paths, buffered RENAME (data under the new name mid-txn + filter-on-renamed +
  rollback restores) + DROP (rollback restores column+data; pure-DROP op name) + chained
  add→rename-pending→add mixed + nested ADD/DROP FIELD fused with INSERT + nested-RENAME-stays-immediate +
  partition-column-rename guard, buffered CREATE + CTAS (mid-txn queryable, rollback-leaves-nothing incl.
  re-attach proof, CTAS+INSERT one WRITE, CREATE+DROP cancels, pending-create ALTER/DML guards, empty
  create, partitioned CTAS-in-txn, native_write CTAS + rollback + retry));
  `verify_delta_catalog_constraints.test` §4 re-pinned; full delta sweep 45/45; EW DeltaLake 168 + Table
  147 (both TFMs); SQL fn suites 7/7 (v60 lockstep); kernel readback of the fused commits
  (metaData+protocol+DV+adds, rename+nested-add/drop, and buffered-CREATE shapes) via the official delta
  extension. **PROVIDER RENAMED to `engineeredwooddelta`** (the engineered-wood-backed Delta
  provider), with **`delta` + `deltalake` kept as aliases** (non-breaking — `BackendRegistry` resolves Name +
  Aliases case-insensitively; all `verify_delta_*` tests still ATTACH with `PROVIDER 'delta'`). The distinct
  primary name reserves space for a future delta-rs/delta-kernel production provider. `test/verify_delta_rename.test`
  (12 — new name + both aliases). Other remaining (OPTIONAL): a `delta-rs` production provider (`deltars`) via the
  **delta-dotnet** binding — **design + build-feasibility (compiles on Windows, no WSL) in
  [docs/delta-rs-provider.md](docs/delta-rs-provider.md)**: it's the better reader/writer/maintenance engine
  (reference impl, standard-compliant writes, OPTIMIZE/Z-ORDER/VACUUM/CHECKPOINT/MERGE, DataFusion pushdown,
  object_store cloud IO); DELETE/UPDATE now work by mapping DuckDB's rowid DML to a **record-batch MERGE**
  (all columns = the rowid; see below), so `deltars` coexists as an opt-in read/write/**DML**/maintenance
  provider (`engineeredwooddelta` stays the default, incl. ALTER). **v1 BUILT +
  verified (local FS)**: `dotnet/Fabricator.DeltaRs` (`DeltaRsBackend`/`DeltaRsCatalog`/`StorageOptionsCodec`),
  registered in `BackendRegistry` (skipped if not published), opt-in publish via `publish-managed.ps1
  -IncludeDeltaRs` (adds `DeltaLake.dll` + the ~240 MB native `delta_rs_bridge.dll`/`delta_kernel_ffi.dll`);
  NO ABI/C++ change. Working: `ATTACH … (PROVIDER 'deltars')` (+ alias `delta-rs`), scan (via
  `ReadAsArrowTableAsync` — NOT DataFusion `QueryAsync`, whose schema diverged and SIGSEGV'd arrow_ingest),
  CREATE/CTAS/INSERT/**COPY** (append+overwrite; `COPY … (FORMAT mssql)`: CREATE_TABLE false→append,
  default→overwrite, new-table→create; `SCHEMA_MODE 'overwrite'` adopts schema [SchemaMode::Overwrite],
  `'merge'` appends + unions new source columns/old rows NULL [the bridge's `table_insert` maps a plain Append
  with `overwrite_schema=false` to delta-rs `SchemaMode::Merge`, so merge = force Append]), metadata, snapshots
  (`HistoryAsync`), **DELETE + UPDATE**, re-attach durability; `test/verify_delta_rs*.test`
  (56/12/27/31/39/29), engineered-wood suite unregressed. **DELETE/UPDATE = rowid →
  record-batch MERGE** (the DML crux, solved): the provider advertises ALL columns as the rowid, so DuckDB's
  rowid `PlanDelete`/`PlanUpdate` give `ExecuteDelete`/`ExecuteUpdate` the scanned rows; the catalog builds a
  delta-rs MERGE matching them on every column NULL-safe (`WHEN MATCHED THEN DELETE` / `… UPDATE SET`, source
  cols renamed `s__`/`k__` to split SET vs key) — sound because a WHERE can't distinguish identical rows, so
  DuckDB's rowid set is a complete equivalence class. Caveat: a duplicated pre-image row may make delta-rs
  reject an UPDATE as an ambiguous multi-match. **DELETE/UPDATE are always copy-on-write — NO deletion vectors**
  (verified 2026-07: with `delta.enableDeletionVectors=true`, BOTH our MERGE-delete AND delta-rs's predicate
  `DeleteAsync` emit `add`+`remove`/rewritten file, never a `deletionVector`, in deltalake 0.32.1). So there is
  **no `deletion_vectors` ATTACH option** for delta-rs (declaring the feature would only bump the table to
  reader-v3, risking Fabric-converter breakage, for zero DV benefit) — use the `engineeredwooddelta` provider's
  `deletion_vectors true` for real DVs. **Maintenance** (OPTIMIZE / Z-ORDER / VACUUM / CHECKPOINT —
  ops engineered-wood lacks) via a command dialect on `fabricator_exec('<catalog>', 'OPTIMIZE main.t ZORDER
  (id)' | 'VACUUM … [DRY RUN]' | 'CHECKPOINT main.t')` (C#-only `ExecuteNonQuery`, no ABI change;
  `test/verify_delta_rs_maintenance.test`, 12). **Filter pushdown** (FilterNode→DataFusion WHERE via QueryAsync,
  superset-safe/unpushable→TRUE; `_pushdown` 27), **time travel** (`AT (VERSION => n)` via QueryAsync — DataFusion
  reads the loaded snapshot, sidestepping the kernel-only `ReadAsArrowTableAsync`; composes with pushdown;
  `_time_travel` 36; VERSION 0 reads latest — delta-dotnet `Version=0` sentinel), and **Change Data Feed**
  (`change_data_feed` ATTACH option + `fabricator_delta_changes`; `_cdf` 31) all work. **ALTER ADD COLUMN**
  works via a 0-row merge-append (Append+OverwriteSchema=false → `SchemaMode::Merge` unions the widened schema,
  old rows NULL — pure delta-dotnet, all backends, no engineered-wood IO seam) + **RENAME TABLE** (local folder
  move); `_alter` 47. **Deferred (clean error): RENAME/DROP COLUMN + ALTER TYPE** (need column mapping), cloud
  RENAME. **Not yet wired**: a first-class MERGE surface + S3/plain-ADLS discovery
  (OneLake IS wired — see next). **OneLake via the
  Unity Catalog REST API**: OneLake exposes a Unity-Catalog-compatible REST API
  (`onelake.table.fabric.microsoft.com/delta/<wsGuid>/<lhGuid>/api/2.1/unity-catalog/schemas` + `/tables`,
  **paginated** via `next_page_token`, storage.azure.com-scope token) that lists schemas + tables + each
  table's `storage_location` — cleaner than the DFS `GetPaths` (no recursion, immune to the duckdb-azure
  mid-path-glob bug). **The engineered-wood provider now discovers OneLake tables via this API**
  (`FabricLakehouse.ListTablesViaUnityCatalog`, replacing GetPaths in `Resolve`; live-validated on
  `LH_no_schema`). **delta-rs OneLake read is PROVEN**: `object_store` reads OneLake with a **GUID-based** abfss
  path (`abfss://<wsGuid>@onelake.dfs.fabric.microsoft.com/<lhGuid>/Tables/[schema/]table` + `bearer_token` +
  `account_name=onelake` + `use_fabric_endpoint=true`) — both the kernel AND DataFusion reads succeed
  (`load_a`→2000 rows); the NAME-based path fails with delta-kernel's "No files in log segment" (that error,
  also from DuckDB's official `delta_scan` on OneLake, was purely name→GUID resolution, NOT a kernel limit).
  The UC `storage_location` returns GUIDs — exactly the read form. **The delta-rs OneLake path is now WIRED +
  live-validated**: `ATTACH 'abfss://<ws>@onelake…/<lh>.Lakehouse/Tables' (PROVIDER 'deltars', SECRET
  <azure_sp>)` → `DeltaRsCatalog` detects the OneLake root, resolves GUIDs + schema-enabled + the UC table list
  via `FabricLakehouse.ResolveOneLakeTables` (made `public`; `Resolve` → `internal`), builds the GUID-abfss
  `TableUri`, and augments `storage_options` with `azure_storage_account_name=onelake` +
  `_use_fabric_endpoint=true` over the SP client-creds (auto-refresh, no static bearer). Validated on
  `LH_no_schema`: 4 tables discovered under `main`, `load_a`→2000 rows, filter pushdown works. S3/plain-ADLS
  discovery still needs a lister. See docs/delta-rs-provider.md. v47 =
  **host-FS global table functions**: appended one vtable entry `set_active_opener(opener)` — a per-thread ambient (`AmbientOpener`, mirroring `set_active_txn`) recording the
  calling operator's `ClientContext` so a connection-free GLOBAL host-FS table reader (a lakehouse format)
  resolves DuckDB secrets while reading through the host `fs_*` callbacks; set in the shared `PopulateReturnSchema`
  + `ArrowStreamInitGlobal` arrow-scan hooks, read by the host-FS binding in `Bind`/`Execute`. **REMOVED**
  `delta_schema`/`delta_scan` (a mid-struct removal → offsets of later entries shift; `abi.h` ↔ `Abi.cs` kept in
  lockstep, function/delta suites the gate): `fabricator_delta_scan` is now a pure-C# global `ITableFunction`
  (`DeltaGlobalTableFunction`) on the v29 table session — a new lakehouse format costs zero C++. See the
  load-time-global-functions bullet + [docs/global-functions.md](docs/global-functions.md) §"Host-FS global table
  functions". v46 = **load-time global functions**: appended one vtable entry
  `list_global_functions` (enumerate the provider-union of connection-free global functions at extension load);
  the scalar entries `get_function_param_schema`/`get_function_return_schema`/`execute_scalar` gained a
  **`handle==0`** branch that resolves a function by name against the C# global registry instead of a catalog
  (all five global kinds — scalar/in-out/collector/table/aggregate — reuse it).
  v42–v45 = the **host-query** feature, prior session. v41 (its `delta_schema`/`delta_scan`, removed at v47) was
  the **Delta lakehouse reader on the filesystem bridge** SPIKE: appended
  `delta_schema`/`delta_scan` to the vtable + `fs_glob` to `FabricatorHostServices`. `fabricator_delta_scan(path)`
  reads a Delta Lake table via **engineered-wood** (Curt Hagenlocher's pure-C# Delta), with ALL IO through
  DuckDB's `FileSystem` over the host callbacks — so local/`az://`/`s3://`/`https://` + DuckDB secrets all work.
  C# `DuckDbTableFileSystem : ITableFileSystem` (root-relative paths, read-only) + `DuckDbRandomAccessFile` over
  the callbacks; `DeltaReader` = `delta_schema` (bind, `OpenAsync().ArrowSchema`, no data) + `delta_scan`
  (execute, `ReadAllAsync()` **materialized** into an `InMemoryArrayStream` while the opener is valid); C++
  `fabricator_delta.cpp` binds via `DeltaSchema`+`ReadArrowSchema`, scans via `DeltaScan`+`ArrowStreamReader`.
  DuckDB applies projection/filter/aggregation above the scan. engineered-wood is referenced from
  `Fabricator.Bridge` (**in-tree git submodule `engineered-wood/` at the repo root**, pinned to the
  `cmettler/engineered-wood` fork, **Apache.Arrow 23.0.0 aligned**, net10.0) + published transitively. **One local engineered-wood patch:** `ActionSerializer` read optional `add`/`remove`
  numeric fields (`baseRowId`/`defaultRowCommitVersion`/remove `size`/`deletionTimestamp` — the **Delta
  row-tracking** fields) with a bare `GetInt64()` that throws on delta-rs's explicit `"field":null`; guarded with
  `TokenType==Null?null:GetInt64()`. Validated: `test/verify_delta.test` (39 — fixture `test/fixtures/delta_simple`,
  a delta-rs 10-row table; full scan + filter/aggregate + DESCRIBE + join). SPIKE, not a user feature. See
  [docs/filesystem-bridge.md](docs/filesystem-bridge.md). A faster future path — engineered-wood as a Delta
  **snapshot/file-list provider** feeding DuckDB's C++ **`MultiFileReader`** + native parquet reader (the
  architecture of DuckDB's own `delta` ext, swapping delta-kernel-rs for the C# log layer), with a cheaper
  `host_query`+`read_parquet` middle-ground first — is captured as a design note (deferred, nothing built):
  [docs/multifile-delta.md](docs/multifile-delta.md). **Phase-A pre-spike BUILT (2026-07-03):**
  `fabricator_delta_native_scan(path)` — engineered-wood lists the EXACT active files + schema
  (`DeltaReader.GetActiveFileUris`, the `add` set not a glob), DuckDB's NATIVE parquet reader reads them via
  `read_parquet([...])` on the host engine (`Host.Query`/host_query); OneLake file URIs rewritten to `onelake://`
  → native + ExternalFileCache-cached. Matches the C# reader row-for-row (`test/verify_delta_native_scan.test`,
  36; `parquet` now statically linked in the test binaries). C#-only (no ABI — reuses onelake:// v56 + host_query).
  Plain tables only (no DV/partition/pushdown; OneLake log read needs the ambient credential = the ATTACH-catalog
  path, a later slice). Validates the "engineered-wood lists → DuckDB reads natively" architecture before the full
  MultiFileList C++ work. **Design fleshed out + source-grounded 2026-07-02**
  (against `D:\repos\duckdb-delta` + DuckDB 1.5.4's `MultiFileReader` API): the **inversion** = C# becomes a
  pure **metadata provider** (`ILakehouseSnapshot`: file list + DV + partition values + schema + baseRowId),
  DuckDB's native parquet reader/writer does ALL parquet I/O → engineered-wood's weakest part (its parquet
  reader/writer, source of the decimal/DV-format/DataPage-V2/signed-min-max issues) FALLS AWAY; its strongest
  part (the `_delta_log` layer) stays. **The wiring trick** (from duckdb-delta): don't build `MultiFileFunction<>`
  — **clone `parquet_scan` + inject `function.get_multi_file_reader`**, inheriting all encodings + multi-file
  parallelism + dynamic (join/TopN) filter pushdown for free; custom = `FabricatorMultiFileList : SimpleMultiFileList`
  (file list) + `FabricatorMultiFileReader : MultiFileReader` (~8 overrides), ~400–1700 LOC provider-agnostic C++
  in `fabricator-core`, reuses `ParquetMultiFileInfo` verbatim. **DVs are ~free** (native `BaseFileReader::deletion_filter`
  hook — attach a `DeleteFilter` from the C#-supplied DV). **Row tracking / commit version / CDF are all LOG-side
  (C#), orthogonal to who writes parquet, and EASIER here**: `baseRowId` assigned on commit from the exact per-file
  `WRITTEN_FILE_STATISTICS` row_count + the `delta.rowTracking` domainMetadata high-water mark (the one gap to
  add in engineered-wood); the readable stable id = `baseRowId + file_row_number` (native parquet virtual column)
  → **this is exactly the read-side stable id delta-rs couldn't expose**; `defaultRowCommitVersion` = the commit
  version (per-file constant); CDF INSERT is inferred (free), DELETE/UPDATE need one extra tagged `_change_data/`
  write (engineered-wood already has the CDF log machinery). **DELETE with `deletion_vectors true` is
  rewrite-free** (pure log+DV write = engineered-wood's existing Fabric-validated capability, NO native parquet
  writer — so DV-DELETE rides on Phase A/read alone); copy-on-write DELETE + UPDATE use the native writer;
  merge-on-read DV-UPDATE (DV old + small postimage file) is an optional later refinement. Biggest cost =
  coupling to DuckDB's churning
  MultiFileReader internals (mitigated: provider-agnostic, in `fabricator-core`); no benefit for SQL Server / DAX
  (Delta-only). Phased: A read-only (host_query+read_parquet pre-spike optional) → B native write → C DML+CDF.
  A separate deferred note,
  [docs/delta-catalog.md](docs/delta-catalog.md), covers a **Delta WRITE-BACK** path: expose a Delta **folder
  as an ATTACH catalog root** (`ATTACH '/lake/root' (TYPE fabricator, PROVIDER 'delta')`; each `_delta_log` subdir
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
  filesystem reverse-callback SPIKE/foundation: a new `FabricatorHostServices`
  struct (host→managed function pointers: `fs_open_read`/`fs_size`/`fs_read`/`fs_close`/`free_str`) is passed
  to `Bootstrap.Initialize(vtable, size, host)` so the managed side can call back into DuckDB's `FileSystem`
  (secret-backed remote IO via DuckDB), plus an `fs_spike` vtable entry + `fabricator_fs_spike(path)` table fn
  that proves it. Key finding: `FileSystem::GetFileSystem(context)` is an `OpenerFileSystem` that AUTO-pushes
  the context's `FileOpener` (secrets) — open with NO explicit opener. Validated: local parquet (PAR1 footer)
  + remote https via httpfs (range GET). Foundation for a future C# lakehouse-format provider (engineered-wood
  Delta/Iceberg/Lance/… reusing DuckDB IO + secrets). See [docs/filesystem-bridge.md](docs/filesystem-bridge.md).
  v39 = foreign-secret consumption: `build_connection_string` gained
  `secret_type` + `base_connstr` args so a provider can reuse a secret of ANOTHER extension's type. C++
  `BuildConnectionStringFromSecret` resolves ANY secret (`IsFabricatorSecret`→`IsKnownSecret`) and passes the
  type + fields + the ATTACH target to C#; `SqlServerBackend` maps an `azure` service_principal/managed_identity
  secret to Entra auth merged onto the ATTACH target (`ATTACH 'Server=…;Database=…' (TYPE fabricator, SECRET
  <azure_sp>)`), and rejects `credential_chain` (storage-scoped/lazy token) pointing to
  authentication='Active Directory Default'. Validated end-to-end against a live Fabric Warehouse (manual);
  error paths in `verify_azure_secret.test` (`require azure`). See [docs/provider-extensibility.md](docs/provider-extensibility.md) §2.1.
  v38 = the secret-field declaration refactor: a `list_secret_fields` entry was
  appended; the provider declares its secret type + fields in C# (`IBackend.SecretType`/`SecretFields`), and
  `RegisterProviderSecrets` registers one DuckDB secret type per declared type generically (fields = the
  `CREATE SECRET` named params; one shared `CreateProviderSecret` keyed by `input.type`). The C++ core names
  no secret type/field — the `kHost…` constants, `ValidateFields`, and `CreateFabricatorSecret` are gone;
  validation moved to C# `BuildConnectionString` (surfaces at connect time). `IsFabricatorSecret`→
  `IsProviderSecret`. See [docs/provider-extensibility.md](docs/provider-extensibility.md) §2.
  v37 = the ATTACH-options→C# refactor: `open_catalog` gained an `options_json`
  arg carrying the provider-owned ATTACH options as a flat JSON object, and `inout_exchange_open` dropped its
  `isolation` arg. `FabricatorAttach` now extracts only PROVIDER/SECRET (meta) and forwards every other option
  as JSON; C# `SqlServerCatalog` parses `schema_filter`/`table_filter` (applied in `get_metadata`, with the
  regex validated C#-side) + `isolation_level` (resolved with `mssql_isolation_level` in `InOutBind`). The
  C++ `CatalogFilters`/`ValidateCatalogFilters`/`ResolveInOutIsolation` + the catalog's filter/isolation
  members are gone; function discovery is schema-filtered by only registering functions whose schema is
  already registered. See [docs/provider-extensibility.md](docs/provider-extensibility.md) §3.
  v36 = the `fabricator_exec` join-only refinement: `set_active_txn` gained an
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
  `agg_combine_spill`/`agg_finalize_spill` + the `FABRICATOR_AGG_SPILL_CAP` constant — 4h opt-in spill; v25
  appended the six custom-aggregate entries `agg_open`/`agg_update`/`agg_combine`/`agg_finalize`/`agg_destroy`/
  `agg_close` — 4h; v24 added `inout_open`'s `isolation` arg so the in-out session runs its per-chunk CROSS
  APPLY in one transaction at that SQL isolation level for a consistent view). **Bump rule:** when you add a
  vtable entry OR change a signature, bump
  **BOTH** `FABRICATOR_ABI_VERSION` in `abi.h` AND `vtable->AbiVersion = N` in `Bootstrap.Initialize`,
  else the host throws "ABI version mismatch". Adding an *enum value* (e.g. a new metadata/alter kind)
  is additive and needs NO bump.
- Ownership: the managed side **consumes/releases** every `ArrowArrayStream`/`ArrowSchema`/`ArrowArray`
  passed in (the C++ caller never releases them; a rare failure leaks rather than double-frees).

## Build & test

### Build from a fresh clone (Windows) — the quickstart

The detail bullets below explain the *why* of each step + the gotchas; this is the from-zero sequence.
`<repo>` = the checkout root (`D:\repos\fabricator-extension`). Run every cmake/ninja command **inside a
VS 18 vcvars64 shell** (see the VS-dev-env bullet — VS 2022 fails at link).

**Prerequisites** (install first):
- **Visual Studio 18** (or its Build Tools) with the C++ workload — the toolset the build links against.
- **.NET SDK 10** (the managed projects target `net10.0;net8.0`; `publish-managed.ps1` needs the 10 SDK).
- **CMake ≥ 3.21 + Ninja** (the generator).
- **vcpkg** — bootstrapped, with `VCPKG_ROOT` set (supplies OpenSSL + curl for the statically-linked `httpfs`).
- **PowerShell 7 (`pwsh`)** — runs the managed publish script.

**Steps:**
1. **Dependencies.** FOUR git submodules: `duckdb` + `extension-ci-tools` (the DuckDB source + build
   tooling, both `shallow = true`), `engineered-wood` (the Delta engine), and `DuckDB.ExtensionKit`
   (MIT, needed ONLY to build the single-file distribution — the normal build never touches it).
   **Init NON-recursively** — `--recursive` would drag in engineered-wood's nested `parquet-testing`
   corpus (~½ GB of test data the build does not need; EW's own corpus-dependent Parquet.Tests then
   fail, which is expected):
   ```
   git submodule update --init          # NOT --recursive
   ```
2. **vcpkg deps** (once): `vcpkg install openssl:x64-windows-static curl:x64-windows-static`
3. **Configure** (first time; ONE command WITH the vcpkg toolchain — httpfs is linked unconditionally so
   these flags are mandatory, not optional):
   ```
   cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 -DDUCKDB_EXTENSION_CONFIGS=<repo>/extension_config.cmake ^
     -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 -DENABLE_EXTENSION_AUTOLOADING=1 ^
     -DENABLE_EXTENSION_AUTOINSTALL=1 -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release ^
     -DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%/scripts/buildsystems/vcpkg.cmake ^
     -DVCPKG_TARGET_TRIPLET=x64-windows-static ^
     -S <repo>/duckdb -B <repo>/build/release
   ```
4. **Build the C++** (targets → binaries detailed below):
   `cmake --build <repo>/build/release --target unittest shell fabricator_loadable_extension`
5. **Publish the managed bridge**: `pwsh scripts/publish-managed.ps1` (lands in
   `build/release/extension/fabricator/fabricator/`).
6. **Run**: set `FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator` before running
   `duckdb.exe`/`unittest.exe` directly (see the managed-dir gotcha). Iteration: a C#-only change needs
   only step 5; a C++ change needs step 4 for the target you'll run (the stale-embedded-copy trap below).

### Reference (the why + gotchas)

- **Target DuckDB v1.5.5** (since 2026-07-22; new extension API: `Extension::Load(ExtensionLoader&)` +
  `loader.RegisterFunction(...)` + `DUCKDB_CPP_EXTENSION_ENTRY(fabricator, loader)`). `duckdb` +
  `extension-ci-tools` are **git submodules** (converted 2026-07-25 — previously gitignored manual
  clones whose shas lived only in this prose, which had already drifted: the tooling pin said v1.5.3
  while upstream had v1.5.4 AND v1.5.5 branches, and it pointed at a moving BRANCH TIP rather than a
  sha. A submodule makes the pin a reviewable diff line and gives CI one deterministic bootstrap).
  Pinned to `duckdb@d8cdaa33` (the v1.5.5 tag) + `extension-ci-tools@72e76e99` (its v1.5.5 branch —
  by convention the tooling version matches the DuckDB version; upstream branches it per patch
  release while duckdb itself branches per LINE, `v1.5-variegata`). Both carry `shallow = true`, so
  `git describe` still has no tag context and the build still needs `-DOVERRIDE_GIT_DESCRIBE`.
  Neither has a `branch =` line ON PURPOSE — `git submodule update --remote` would jump the pin to an
  unreleased tip. Bump duckdb via
  `git -C duckdb fetch --depth 1 origin <sha> && git -C duckdb checkout <sha>` then `git add duckdb`
  (a version bump also
  means: re-run cmake with `-DOVERRIDE_GIT_DESCRIBE=v<new>`, match the out-of-tree httpfs pin in
  `extension_config.cmake` to the sha in duckdb's `.github/config/extensions/httpfs.cmake`, and
  `pip install duckdb==<new>` in the dbt/notebook envs — the official wheel rejects a loadable whose
  declared version differs). **1.5.5 verification (2026-07-22):** C++ compiled unchanged, full delta
  sweep + SQL function suites + s3 161/polybase 252 green on the new httpfs sha (`827222fb`).
  **DuckDB's variant limitations are all UNFIXED in 1.5.5** (source-diffed + runtime-probed): the
  `ArrowAppender::FinalizeChild` nested-extension crash (why the transport is a leaf blob), the
  parquet writer's non-root-VARIANT rejection (why nested variant is gated), and `variant_extract`
  returning NULL (dot access stays the way). 1.5.5 DOES fix an FLBA-decimal `RETURN_STATS` min/max
  unification bug (big-endian stats compared as little-endian across row groups) — our native-write
  Delta stats for precision>18 decimals in multi-row-group files are correct-by-upstream now.
- **engineered-wood is an in-tree git submodule** (`engineered-wood/` at the repo root, since
  2026-07-19; was a `D:\repos\engineered-wood` sibling ProjectReference). Pinned to the
  **`fabricator-patches` branch on the `cmettler/engineered-wood` fork** = **clast-project master
  (`e48f449`) + our small additive patch set** (see "THE EW CLAST-MASTER RE-PIN" near the top;
  `.gitmodules` `branch = fabricator-patches`; `upstream` remote = clast-project/engineered-wood).
  `Fabricator.Bridge.csproj` references `..\..\engineered-wood\src\EngineeredWood.DeltaLake.Table\…`.
  **Init NON-recursively** — `git submodule update --init engineered-wood` — to skip EW's nested
  `parquet-testing` corpus (its test data, ~half a GB, not needed to build; note EW's own
  Parquet.Tests corpus-dependent tests fail without it — expected). **Workflow:** EW dev happens
  INSIDE the submodule working tree on `fabricator-patches`; the build uses the working tree, so
  day-to-day edits/commits there don't touch the parent's pin. Keep every EW change as an ADDITIVE,
  upstreamable commit on `fabricator-patches` (never fork-style divergence); to take a new upstream
  EW, merge `upstream/master` into `fabricator-patches`, re-run the delta sweep, push, re-pin. To
  RECORD a known-good EW version in fabricator: push EW to the fork FIRST (the pin must be
  fetchable — pushes still only on the user's explicit authorization), THEN bump the pointer
  (`git add engineered-wood && git commit`). (The old `D:\repos\engineered-wood` sibling is
  redundant; the scratchpad spike csprojs still point at it but scratchpad is gitignored.)
- **DuckDB.ExtensionKit is an in-tree git submodule too** (`DuckDB.ExtensionKit/` at the root, since
  2026-07-25; was a `D:\repos\DuckDB.ExtensionKit` absolute ProjectReference). MIT, upstream
  `Giorgi/DuckDB.ExtensionKit`, **pinned by SHA (`882f080`) with no `branch =` line** — deliberately
  NOT floating: the AOT shell depends on internals of the kit's `DuckDBExtApiV1` mirror, so an
  unpinned bump could silently change the ABI surface. **It is NOT on NuGet** (checked: the
  flat-container id 404s and a search returns 0 hits), so a `PackageReference` is not an option today;
  a submodule also keeps it patchable, which matters because two upstream-candidate issues are already
  known (the `duckdb_result` out-param typed as `nint*`, and `duckdb_fetch_chunk` typed as taking a
  pointer when the C API takes the struct BY VALUE — see the distribution bullet's §15/§16 findings).
  Only `dotnet/Fabricator.Installer` references it (`$(MSBuildThisFileDirectory)..\..\DuckDB.ExtensionKit`,
  overridable via `-p:DuckDBExtensionKitPath=`), and nothing else in the repo builds that project — so a
  missing submodule cannot break the normal build; the csproj errors with the exact `git submodule`
  command instead. Switching a build between Windows and WSL over the SAME working tree makes the kit's
  `obj/` restore for the other OS: the first cross-OS build can fail once in `ResolvePackageAssets`, and
  simply re-running it succeeds.
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
  - `fabricator_loadable_extension` → `build/release/extension/fabricator/fabricator.duckdb_extension`
    (the loadable; needed to `LOAD` into a duckdb that does NOT embed it — e.g. the **official `duckdb==1.5.5`
    Python wheel** for the dbt-duckdb concurrency tests). **To load into the official wheel, reconfigure with
    `-DOVERRIDE_GIT_DESCRIBE=v1.5.5`** so the extension footer declares `duckdb_version=v1.5.5` — the shallow
    clone has no git tag context, so it otherwise defaults to `v0.0.1` and the official engine rejects it on
    the version check (NOT bypassed by `allow_unsigned_extensions`). The wheel version MUST match the declared
    version — after the 1.5.5 bump, dbt venvs / notebook flows still on `duckdb==1.5.4` reject the new
    loadable until they `pip install duckdb==1.5.5`. Then `LOAD` with `allow_unsigned_extensions`
    + set `FABRICATOR_MANAGED_DIR` (the bridge isn't next to the python `.pyd`). Verified loads + ATTACH +
    query against the official wheel. (This also fixes `json`/`icu` autoload, though we embed those.)
  - `cmake --build build/release` (no `--target`) builds all of them.
  - **After changing C++ extension code, rebuild the target whose binary you'll run.** Building only
    `fabricator_loadable_extension` then running `duckdb.exe`/`unittest.exe` runs the STALE embedded copy
    (a `LOAD '<path>'` is then a no-op). This is the #1 "my change didn't take" trap.
- Full configure (first time), run inside vcvars64:
  `cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 -DDUCKDB_EXTENSION_CONFIGS=<repo>/extension_config.cmake
  -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 -DENABLE_EXTENSION_AUTOLOADING=1
  -DENABLE_EXTENSION_AUTOINSTALL=1 -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release
  -S <repo>/duckdb -B <repo>/build/release`. `EXTENSION_VERSION "0.0.1"` is set in
  `extension_config.cmake` (the repo has commits now, but keep it — avoids relying on `git describe`).
- **Managed publish:** `pwsh scripts/publish-managed.ps1` → publishes `Fabricator.SqlServer` (+ Bridge +
  self-contained .NET 10 runtime) into `build/release/extension/fabricator/fabricator/`. A C#-only change
  needs only a republish (no C++ rebuild) unless an ABI signature changed.
- **TWO DEPLOYMENT MODES + PROVIDED-RUNTIME hosting (2026-07-12; Windows + Linux validated, Fabric live).**
  All extension projects multi-target **`net10.0;net8.0`** (`dotnet/Directory.Build.props`; EW already did)
  with `RollForward=LatestMajor`. `publish-managed.ps1 -Mode Framework [-Rid linux-x64]` produces a
  **framework-dependent** payload (~35 MB win / ~25 MB zipped linux vs ~250 MB self-contained; net8.0 +
  rollForward ⇒ ONE payload runs on .NET 8 AND 10+). `clr_host` detects the layout by **hostfxr's presence
  in the managed dir** (self-contained carries it): absent ⇒ resolve a PROVIDED .NET install —
  **`FABRICATOR_DOTNET_ROOT` > `DOTNET_ROOT` > platform defaults** (win `%ProgramFiles%\dotnet`; linux
  `/etc/dotnet/install_location`, `/usr/share/dotnet`, `/usr/lib/dotnet`; mac `/usr/local/share/dotnet`) —
  load `<root>/host/fxr/<highest>/hostfxr` and pass the root via `hostfxr_initialize_parameters.dotnet_root`
  (NO env mutation; `host_path=null` = current process). **Gotcha found: a dotnet_root with FORWARD slashes
  fails at CreateCoreCLR with a cryptic E_INVALIDARG** (framework resolution tolerates them) — clr_host
  normalizes on Windows. Validated: FDD on the global install (rolls to newest), full suites on a
  net8-ONLY private root via `FABRICATOR_DOTNET_ROOT` (the "local .NET 10 beside global .NET 8" selector,
  inverted), `DOTNET_ROLL_FORWARD` respected, SC unchanged (the publish script CLEANS the output dir on a
  mode change — a stale hostfxr would flip the detection).
- **LINUX (linux_amd64) BUILDS + FULL SUITES GREEN (WSL Ubuntu 22.04, gcc 11 — glibc 2.35 baseline runs on
  Fabric's Azure Linux 3).** Build = same configure as Windows minus vcvars, plus
  `-DOVERRIDE_GIT_DESCRIBE=v1.5.4` (no .git in the copied tree) + vcpkg toolchain with `x64-linux`
  (openssl+curl for httpfs); the C++ compiled with ZERO changes (the clr_host ifdefs held). Suites on
  linux + the apt `dotnet-runtime-8.0` (auto-probed at `/usr/lib/dotnet`, no env var): delta transactions
  596 / txn_version 51 / SQL Server-over-docker scalar 26 + custom 89 / **S3-MinIO 131** / copy_format 96.
  **CROSS-PLATFORM BUG found by the first Linux run: EW `ListVersionsAsync` returned commit versions in RAW
  DIRECTORY-LISTING order** — Windows/S3/ADLS list sorted, but Linux readdir returns inode-hash order, and
  the callers assume ascending replay (SnapshotBuilder's latest-wins metadata/protocol, timestamp
  resolution's monotonic early-break, the history view). Symptom: the per-txn snapshot pin resolved "now"
  to v0 → an in-transaction DELETE scanned an empty snapshot and silently deleted nothing. Fixed at the
  source (materialize + sort ascending; the log dir is bounded by the checkpoint interval).
- **FABRIC NOTEBOOK VALIDATED LIVE (2026-07-12, Livy pyspark on workspace `Test`/`LH`):** the Fabric
  compute is **Azure Linux 3** (`6.6.141.1-1.azl3`) with **dotnet preinstalled at `/usr/share/dotnet`,
  .NET 8.0.28 ONLY, no DOTNET_ROOT set** — our default probe finds it with ZERO configuration. Flow:
  upload `fabricator.duckdb_extension` (linux_amd64) + the zipped FDD payload to the lakehouse
  `Files/fabricator_ext/` (OneLake DFS), then in the session: `pip install --force-reinstall duckdb==1.5.5` (must match the loadable's declared version)
  (never import duckdb in the kernel before the pip — read the preinstalled version via
  `importlib.metadata`; the duckdb work runs in a SUBPROCESS interpreter, which also isolates a crash from
  the kernel), stage to /tmp, `FABRICATOR_MANAGED_DIR` + `load_extension` → `fabricator_version()` works,
  delta CTAS + explicit transaction correct. Driver: `scratchpad/fabricnb` (gitignored; reads the SP from
  dax_secret.sql) — `dotnet run livy` = the Spark-session path; raw Livy sessions have NO
  `/lakehouse/default` fuse mount — the probe stages via `spark.sparkContext.binaryFiles(abfss://…)` there.
  **The TRUE PYTHON-NOTEBOOK path is ALSO validated (RunNotebook job, 75 s):** the notebook session runs
  Azure Linux 3 + dotnet 8.0.27 at `/usr/share/dotnet` (only runtime, no DOTNET_ROOT) and — unlike the Livy
  session — HAS the fuse mount AND a **preinstalled duckdb 1.2.2**; `pip install --force-reinstall
  duckdb==1.5.5` overrides it (works without a kernel restart BECAUSE duckdb is never imported in the
  kernel), the extension loads on the preinstalled .NET 8, the delta transaction smoke passes, and a Delta
  table written through the fuse mount (`ATTACH '/lakehouse/default/Files/…'`) reads back. Fabric-API
  gotchas hit on the way: **Notebook-item CREATION is not SP-enabled on this tenant** (`403
  FeatureNotAvailable`, bare create too — the notebook must be created interactively ONCE; the SP-driven
  `updateDefinition` + `RunNotebook` then work), `updateDefinition?updateMetadata=true` requires a
  `.platform` part (omit the flag — the default-lakehouse binding rides in the ipynb metadata), and the
  portal can save a display name with a TRAILING SPACE (`'fabricator_ext_probe '`) — resolve by trimmed
  comparison. `dotnet run run` = update+run the existing notebook; `upload` = refresh the OneLake
  distribution (`LH/Files/fabricator_ext/`). **The MANAGED Tables area works through the fuse mount** —
  `ATTACH '/lakehouse/default/Tables' (TYPE fabricator, PROVIDER 'delta', schemas true)`: credential-free
  read + CREATE + explicit-txn append on `tlake.dbo.*`, all sub-second per op (single-writer only: the
  commit's O_EXCL put-if-absent is doubtful over fuse — concurrent writers should use abfss/onelake).
  **PERF (measured per-step): the notebook's in-session work went ~305 s → ~15 s** via two fixes:
  (1) **local-root discovery fast path** (`DeltaCatalog.DiscoverTablePairs`): a root that
  `Directory.Exists` (fuse mount, any local dir) discovers via direct System.IO enumeration
  (schema dirs → table dirs → `Directory.Exists(_delta_log)`) instead of the host glob — the glob's
  commit-file matching + per-match stat was **258 s over fuse on the populated LH → 2 s**; object stores
  keep the glob. **Root cause of the old cost, now ALSO fixed at the source: `HostFsGlob` did an
  `OpenFile(READ)+GetFileSize` PER MATCHED FILE** (DuckDB's FileSystem has no path-stat — size needs a
  handle) purely for a `size` field discovery never reads — and on a fuse mount an open can DOWNLOAD the
  blob into the local cache, so the old ATTACH effectively downloaded every commit json of every table
  (on S3 it was a HEAD per match). Now: size comes from the glob entry's `extended_info["file_size"]`
  when the filesystem's listing provides it (object stores), else -1 → the managed
  `DuckDbTableFileSystem.ListAsync` fills LOCAL files via a cheap `FileInfo.Length` metadata stat,
  unknown ⇒ 0 (the only consumer is VACUUM's bytes-to-delete metric — best-effort by design). The
  wildcard-on-contents glob shape (`…/_delta_log/*.json`) itself is CORRECT for object stores — a
  "directory" doesn't exist as an object there; only a FILE under it proves the table — which is why the
  glob remains the object-store path and only local roots take the System.IO walk.
  (2) the **duckdb wheel ships with the distribution** and installs
  `pip --no-deps --no-compile --target /tmp/fabricator_pyduck` + `PYTHONPATH` for the probe subprocess
  (37 s PyPI force-reinstall → 3.3 s; the session's own duckdb stays untouched). Remaining wall-clock ≈
  Fabric job scheduling/session spin-up (~45–60 s, not ours).
- **Managed-dir resolution gotcha:** `clr_host` looks for the bridge in `FABRICATOR_MANAGED_DIR`, else an
  `fabricator/` folder *next to the loaded module*. For the static `duckdb.exe`/`unittest.exe` the module IS
  the exe, so the default lookup is `build/release/fabricator` (next to `duckdb.exe`) — but
  `publish-managed.ps1` lands the bridge in `build/release/extension/fabricator/fabricator`. So when running
  an exe **directly** you MUST set `FABRICATOR_MANAGED_DIR` to that publish dir (symptom otherwise:
  `Fabricator: failed to load hostfxr from …\build\release\fabricator\hostfxr.dll`). Manual smoke, e.g.:
  `FABRICATOR_MANAGED_DIR=…/extension/fabricator/fabricator build/release/duckdb.exe -unsigned -batch < q.sql`.
- **CoreCLR hosting:** init via `hostfxr_initialize_for_dotnet_command_line` (argv[0] =
  `Fabricator.Bridge.dll`) then `hdt_load_assembly_and_get_function_pointer`.
  `hostfxr_initialize_for_runtime_config` FAILS for self-contained deployments. The bridge finds its
  files via `FABRICATOR_MANAGED_DIR`, else an `fabricator/` folder next to the extension binary.
- **C++ standard gotcha:** DuckDB compiles extensions pre-C++17 → `std::string/wstring::data()` is
  `const`; use `&s[0]` for `MultiByteToWideChar`/`WideCharToMultiByte` out buffers.
- **Tests:** `build/release/test/unittest.exe --test-dir <repo-root> "test/mssqlcompat/<dir>/*"` (and
  `test/verify_*.test`). Set `FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator` +
  `MSSQL_TESTDB_DSN` (and `MSSQL_TEST_SERVER`/`_CONNECTION_STRING` = the same full DSN for the tests
  that ATTACH it directly). The corpus is regenerated from `D:\repos\mssql-extension/test/sql` by
  `scripts/gen_mssqlcompat_tests.sh`; it lives at `test/mssqlcompat/` and is **gitignored** (keep the
  duckdb submodule clean).
- **Test env (docker compose, `docker/docker-compose.yml` — replaced the ad-hoc container 2026-07-10):**
  SQL Server 2025 (`mcr.microsoft.com/mssql/server:2025-latest`, container `mssql-fabricator`, port 1433,
  `sa` / `Arrow_Net_123!`, DBs `ArrowTest` + `TestDB` — created by `docker/provision.ps1`; all other test
  objects self-provision inside the tests) + **MinIO** (S3-compatible: `miniouser` / `miniosecret123` —
  deliberately ALPHANUMERIC, SQL's S3 credential requires it; bucket `fabricator`; S3 API 9000 / console
  9001; **HTTPS** via the self-signed cert from `docker/certs/generate-certs.ps1`, SANs
  `minio`/`localhost`/`127.0.0.1` — SQL Server's `s3://` connector REQUIRES TLS, trusted via the compose
  mount at `/var/opt/mssql/security/ca-certificates`). Bring-up: certs → compose up → provision
  (docker/README.md). Connstr needs `TrustServerCertificate=true;Encrypt=true`. `sqlcmd` v18 in-container:
  `docker exec mssql-fabricator /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C`.
- **S3 / MinIO / SQL Server data virtualization (2026-07-10).** `httpfs` is now statically linked
  (`extension_config.cmake` — out-of-tree pin `duckdb-httpfs @ 827222fb` since the 1.5.5 bump, always
  the sha DuckDB's own CI
  uses; needs OpenSSL+curl via the vcpkg toolchain: `vcpkg install openssl:x64-windows-static
  curl:x64-windows-static`, configure with `-DCMAKE_TOOLCHAIN_FILE=$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake
  -DVCPKG_TARGET_TRIPLET=x64-windows-static` — the `-static` triplet must match the /MT build). The
  engineered-wood Delta catalog works on `s3://` (MinIO): ATTACH/discovery, CREATE/CTAS/INSERT, pushdown,
  DV DELETE + merge-on-read UPDATE, snapshots, explicit transactions, re-attach, and the
  native_write/native_read variant — `test/verify_delta_catalog_s3.test` (60, gated
  `FABRICATOR_S3_ENDPOINT`; re-runnable via CREATE OR REPLACE against the persistent bucket). Self-signed
  TLS: `SET GLOBAL enable_curl_server_cert_verification = false` (GLOBAL — the transaction flush runs on
  its own connection; production alternative `ca_cert_file`). **Four real bugs found by the S3 rig:**
  (1) `DuckDbTableFileSystem.ExistsAsync` probed via a wildcard-free glob — httpfs' S3 glob ECHOES
  literal paths back without checking the store, so every commit-0 hit a phantom "version 0 already
  exists"; now probes via OpenRead (a HEAD on object stores). (2) The transaction flush used the
  committing context as opener, but the SECRET MANAGER requires an ACTIVE transaction ("ActiveTransaction
  called without active transaction" on s3) — `FabricatorTransactionManager::CommitTransaction` now gives
  the flush its OWN short-lived `Connection` + transaction as the opener (local paths need no secrets, so
  no local test ever saw this). (3) EW `WriteCoreAsync`'s Overwrite-removes omitted the file's
  `deletionVector`, so a REPLACE over a DV-carrying file never matched the active (path,DV) entry — the
  file stayed active FOREVER (duplicated rows after CREATE OR REPLACE of a DV-deleted table); one-line
  fix mirroring CommitDataFilesAsync. (4) **EW `CheckpointReader.ExtractMetadata` DROPPED
  `metaData.configuration`** — after the first checkpoint (interval 10) a table silently lost
  `enableDeletionVectors`/`enableChangeDataFeed`/`columnMapping.mode`/`maxColumnId`, and the loss is
  VIRAL (the NEXT checkpoint persists the config-less metadata — permanently poisoned even after the fix;
  wipe/re-create such tables). Fixed via the existing `GetStringMapField`. **The full circle — SQL Server
  reads our Delta from MinIO:** SQL Server 2025 (17.x) reads CSV/Parquet/**DELTA** on S3 **natively** (no
  PolyBase package, no `sp_configure 'polybase enabled'`, no TF13702 — those are 2022 requirements;
  `mssql-server-polybase` exists for 17.x/Ubuntu 24.04 but is only needed for RDBMS connectors).
  `test/verify_mssql_s3_polybase.test` (70, gated `MSSQL_TESTDB_DSN` + `FABRICATOR_S3_ENDPOINT` +
  `FABRICATOR_S3_SQL_ENDPOINT`): our provider CTASes to `s3://fabricator/polybase` → `fabricator_exec`
  provisions MASTER KEY + `DATABASE SCOPED CREDENTIAL (IDENTITY='S3 Access Key', SECRET='key:secret')` +
  `EXTERNAL DATA SOURCE (LOCATION='s3://minio:9000/')` + `EXTERNAL FILE FORMAT (FORMAT_TYPE=DELTA)` →
  `OPENROWSET(BULK '/fabricator/polybase/trips', FORMAT='DELTA', DATA_SOURCE='s3_ds')` matches row-for-row
  → `CREATE EXTERNAL TABLE` + read back through the ATTACHed catalog as a normal scan (DuckDB →
  fabricator → SQL Server → S3 delta reader → MinIO → table written by our Delta provider). **SQL Server's
  DELTA reader = Delta protocol 1.0 ONLY** — the interop table MUST be written `deletion_vectors false,
  column_mapping 'none'`: a DV-default reader-v3 table errors, and a NAME-mapped table is rejected with
  the SPECIFIC error `19725: Column mapping is not enabled` (the reader recognizes the feature but gates
  it off — both pinned; same finding class as the Fabric T-SQL endpoint). Copy-on-write DELETE/UPDATE on
  the plain table KEEP it SQL-Server-readable (plain remove+add stays protocol 1.0 — OPENROWSET reads the
  post-DML state exactly; pinned), so the full DML lifecycle works for SQL-Server-facing tables — just on
  the CoW path instead of DVs. Partitioned delta: the external table reads the partition column as NULL, OPENROWSET
  reads it correctly (documented MS limitation). **IDENTITY on S3 works end-to-end** (v53 marker; values continue
  across re-attach — hwm durable on MinIO) **and stays SQL-Server-readable** (identityColumns is a
  WRITER-only feature, reader stays v1 — pinned). **DROP TABLE on S3 works via a per-file fallback**:
  httpfs' S3 `RemoveDirectory` re-lists keys WITHOUT the scheme prefix and fails its own remove ("URL
  needs to start with s3://"), so `DeltaCatalog.DropTable` catches the failure and deletes glob(`/**`)
  file-by-file + the zero-byte directory-marker keys (`RemoveFile` IS implemented for s3). S3 caveats
  (documented): no put-if-absent on httpfs S3 (single-writer without a SECRET; `fabricator_fs_write_probe`
  shows EXCLUSIVE_CREATE unguarded); `DROP EXTERNAL TABLE IF EXISTS` is not T-SQL (use
  `IF OBJECT_ID(...) IS NOT NULL DROP EXTERNAL TABLE ...`). **Committed-table RENAME TABLE on S3 — DONE
  (2026-07-17, C#-only) for SECRET-routed attaches:** `S3CommitFileSystem.RenameDirectory` renames the whole
  table folder SERVER-SIDE via the SDK (ListObjectsV2 → `CopyObject` per key — unconditional copies are fine,
  only the CONDITIONAL CopyObject is unguarded on MinIO — → batched DeleteObjects; copy-ALL-then-delete so a
  mid-failure leaves the source intact; no data crosses the client; 5 GB/object single-call CopyObject cap
  noted). Wired in `DeltaCatalog.AlterTable` RenameTable + `RenamePendingCreated` (SDK preferred over the
  per-file host-FS copy) when `_s3Credential` is present; SECRETLESS s3 keeps the clean "MoveFile is not
  implemented" error. This unblocks **dbt table-model RE-DEPLOYS on S3-Delta** (the swap's two renames +
  backup drop — previously any re-run of an existing table model failed; found by the 4x1M perf sweep).
  `verify_delta_catalog_s3` §10 (161 — rename + DV commit moved, old name gone, re-attach durable,
  round-trip); dbt minio full-refresh over EXISTING tables green. **CDF on S3 works end-to-end** (change files write to + read
  from the bucket; the feed is exact) **and a CDF table stays SQL-Server-readable** (changeDataFeed is
  writer-only too — pinned). **FIFTH S3-rig bug (EW, parquet-layer):** `ColumnChunkWriter.CompressTo`
  returned a 0-BYTE payload for an empty input — but a valid snappy stream of nothing is the single
  `0x00` length varint, so an ALL-NULL DataPage-V2 values section was "corrupt snappy" to strict decoders
  → **SQL Server failed every table whose read crossed an EW CHECKPOINT** (checkpoints are full of
  all-null column chunks; error 19787 on the `.checkpoint.parquet`; DuckDB/kernel tolerate 0 bytes). Fix:
  let the codec encode emptiness (Snappier emits the valid empty stream); verified — SQL Server reads
  through a fresh v10 checkpoint (12-version table, exact counts). EW Parquet.Tests 585/585. Test sizes
  now: verify_delta_catalog_s3 114, verify_mssql_s3_polybase 118 (+ column-mapping/identity/CDF pins,
  CoW-DML readability, DROP-on-S3, CDF feed over S3).
- **Copy-paste test env** (Bash tool; test-only creds — the REAL Fabric SP lives only in the gitignored
  `dax_secret.sql`, never here). Run the loadable/shell/unittest from `build/release/`:
  ```bash
  export FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator
  DSN='Server=localhost,1433;Database=TestDB;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true'
  export MSSQL_TESTDB_DSN="$DSN" MSSQL_TEST_SERVER="$DSN" MSSQL_TEST_CONNECTION_STRING="$DSN"
  # a Delta catalog verify test needs a writable base dir:
  export FABRICATOR_DELTA_WRITE_DIR="$(mktemp -d)"      # each test file wants its OWN fresh dir
  # S3/MinIO tests (docker compose stack must be up):
  export FABRICATOR_S3_ENDPOINT=localhost:9000          # gates verify_delta_catalog_s3
  export FABRICATOR_S3_SQL_ENDPOINT=minio:9000          # + MSSQL_TESTDB_DSN gates verify_mssql_s3_polybase
  export FABRICATOR_DELTARS=1                           # gates the 7 verify_delta_rs_* suites; set ONLY when
                                                        # publish-managed.ps1 -IncludeDeltaRs has actually run
  # run one test at a time (the runner concatenates multiple filters into one bad glob):
  build/release/test/unittest.exe --test-dir . "test/verify_delta_catalog_native_write.test"
  # trace the write path: prepend FABRICATOR_LOG_LEVEL=Debug (logs off by default)
  # NOTE: the sqllogictest runner AUTO-SKIPS a test whose error message contains 'HTTP' (network-flake
  # tolerance) — an S3 test that "skips" may actually be FAILING; reproduce via the shell to see why.
  # live Fabric OneLake: a .sql script starting with  .read dax_secret.sql  then
  #   ATTACH 'abfss://Test@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables' AS lake
  #     (TYPE fabricator, PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false [, native_write true]);
  #   piped:  build/release/duckdb.exe -unsigned -batch < script.sql   (LH = schema-enabled, dbo)
  ```

### CI — introduced 2026-07-25 (`.github/workflows/`), tiered by what it needs

Nothing existed before this; the repo was developed and validated by hand on one Windows box. The
tiers are separated by their DEPENDENCIES, not by taste, and each is path-filtered so documentation
commits do not compile DuckDB:

| tier | workflow | what | trigger |
|---|---|---|---|
| 0 | `installer-core.yml` | `Fabricator.Installer.Core.Tests`, 92 × {net8.0,net10.0} × {win,linux}. No C++, no vcpkg, no submodules (the closure is Installer.Core + xunit). ~2 min | push/PR |
| 1 | `extension.yml` | build + the **53 hermetic suites / 4152 assertions** (scratch dir + in-repo fixtures only). 3 platforms | push/PR |
| 2 | `integration.yml` | the **42 service suites / 1221 assertions** via `docker/docker-compose.yml` (SQL Server 2025 + MinIO + generated certs + `provision.ps1`). linux only | schedule + dispatch |
| — | manual | `verify_dax` (Power BI Desktop), live Fabric/OneLake (gitignored SP creds), the 7 deltars suites (`-IncludeDeltaRs`, ~240 MB) | by hand |

**Proven-in-CI status (2026-07-26).** Tier 0 green. **Tier 1 green on ALL THREE platforms in ONE run**
(`30192450794`, sha `124ad4f`) — each independently 53/53 suites / 4152 assertions, verified from the job
logs rather than the status tick. **Tier 2 green** (`30192508662`) — 42/42 / 1221, nothing skipped,
`verify_mssql_s3_polybase` at its full 252. Both defects that the first CI runs surfaced (the macOS
`ArrowProducer` use-after-free and the undeclared `require parquet`) are fixed and confirmed IN CI, not
merely locally — a distinction this repo's history says to insist on. **`distribution.yml` remains the
only tier never executed**, and it is the one whose failure modes the others structurally cannot reach:
NativeAOT publish, the polyglot append, and the version gate against a stock DuckDB wheel (plus it is the
only tier that REQUIRES `OVERRIDE_GIT_DESCRIBE` — the very flag that broke Tier 1 when set).

**Suite selection is DERIVED, never a hand-kept list** — `scripts/list-hermetic-suites.sh` and
`scripts/list-service-suites.sh` classify by the `require-env`/`require` directives each suite
declares, so a new suite cannot silently sit outside CI. The accounting is complete and checked:
**53 + 42 + 9 excluded = 104**, no overlap. `scripts/run-suites.sh <hermetic|service>` runs them ONE
PROCESS PER SUITE with a fresh scratch dir, and asserts what `unittest` will not: nothing SKIPPED, the
runner never says "No tests ran", and floors on the selected suite/assertion counts. The hermetic tier
CLEARS the service env vars (proving hermeticity); the service tier DEMANDS them and names any that
are missing.

**Per-platform coverage is deliberately unequal — state it, never imply parity:**

| | tier 1 | tier 2 | notes |
|---|---|---|---|
| `linux_amd64` | ✅ | ✅ | the Fabric deployment target |
| `windows_amd64` | ✅ | (local only) | the development platform; DAX/ADOMD fully supported here |
| `osx_arm64` | ✅ | ❌ impossible | hosted macOS runners **cannot run containers**, so SQL Server/MinIO are unreachable. Demand-driven (DuckDB's user base skews Apple Silicon); DAX untested |

**Traps that cost real cycles — do not rediscover them:**
- **A no-match sqllogictest filter exits ZERO** ("No tests ran"), and the filter is Catch-style, so a
  MID-pattern `*` matches nothing (`test/verify_x*.test` fails, `test/verify_x*` works). A green run
  proves nothing without a positive assertion.
- **`unittest -f <list>` (batch mode) is unusable here**: one CLR per process means earlier suites'
  finalizers run during later ones — SIGSEGV at suite 41/53 inside Apache.Arrow's
  `ImportedArrowArrayStream` finalizer. One process per suite is not a style choice.
- **`git update-index --chmod=+x` is required for CI scripts.** `core.fileMode=false` on Windows means
  a local `chmod +x` is never recorded, and Linux then refuses to execute (exit 126).
- **`.gitattributes` forces `*.sh` to LF.** With `core.autocrlf=true` a checkout would give the scripts
  CRLF, breaking the shebang and — worse, silently — inverting `[ "$RUNNER_OS" = 'Windows' ]`.
- **vcpkg infers manifest mode from the CURRENT DIRECTORY.** The steps `cd "$RUNNER_TEMP"` first; a
  `vcpkg.json` at the repo root (there was a stale one, now deleted) makes `vcpkg install <pkg>` fail
  outright. The build consumes the CLASSIC global tree, since CMake's source dir is the duckdb
  submodule and it has no manifest.
- **Do NOT set `-DOVERRIDE_GIT_DESCRIBE` for the TEST build.** It is required for the loadable (a stock
  DuckDB rejects a version mismatch) but it changed autoload resolution enough to make fabricator's own
  `Load` fail on `parquet_scan`. The packaging tier sets it; the test tier must not.
- **`set >> $GITHUB_ENV` corrupts the environment.** GITHUB_ENV is line-oriented, so one variable
  containing a newline breaks every later step; the MSVC step exports only PATH/INCLUDE/LIB/LIBPATH,
  with the redirect written FIRST on each line (`echo VAR=%VAR%>>file` is misparsed when the value ends
  in a digit).
- **VS 18 is NOT an absolute requirement** (correcting the reference bullet above): the local failure is
  a MIXED-toolset artifact — configure with VS 18's STL, link with VS 2022. CI compiles and links with
  one toolset and the runner image's own works fine.

**Reproducing a bare runner locally — the single most useful trick here.** Point the profile at an
empty directory so no extensions are installed on disk:

```bash
EMPTY=$(mktemp -d); export USERPROFILE="$(cygpath -w $EMPTY)" HOME="$EMPTY"
./scripts/run-suites.sh hermetic
```

DuckDB resolves `~/.duckdb/extensions` from there, so autoload-from-disk cannot mask a missing
dependency. This turned a 25-minute push-and-wait loop into a 30-second check and immediately found
five suites that passed **only** because this machine happens to have
`~/.duckdb/extensions/v1.5.5/windows_amd64/parquet.duckdb_extension`. (Beware `HOME` under Git Bash: it
is `/z/`, NOT the Windows profile, so a bare `ls ~/.duckdb` misleads.)

**Two latent bugs CI found in its first hours, both destruction-order, both invisible on Windows** —
the pattern to expect from a new platform, and the reason a passing platform proves nothing about this
class of defect:
1. **Aggregate state destructor = use-after-free (FIXED).** `PhysicalOperator::sink_state` is a
   BASE-class member while the bound aggregate expressions owning the `FunctionData` are derived
   members, so at plan teardown the bind data is already freed when the state destructor dereferences
   it. Deterministic ordering, allocator-dependent fault: Linux SIGSEGV, macOS SIGABRT, Windows silent.
   No destructor is registered now; `AggSessionHolder`'s `agg_close` reclaims.
2. **A late `ArrowProducer` stream release aborted on macOS (FIXED).** `verify_global_functions` died at
   assertion 41 with `libc++abi: terminating due to uncaught exception of type std::system_error: mutex
   lock failed: Invalid argument` (exit 134). Two-line repro, which aborts on its own (NOT
   state-dependent — the statement bisect and an isolated run both land here):
   ```sql
   SELECT squared FROM fabricator_seq(5) WHERE value > 3 ORDER BY squared;
   ```
   lldb (`-k`, not `-o` — see the traps list) put the whole diagnosis in five frames: `std::mutex::lock()`
   ← `ArrowProducer::Release` ← six unsymbolized JIT frames ← `ArrowStreamScan` ←
   `PhysicalTableScan::GetDataInternal`, on the MAIN thread's pipeline (so not a finalizer).

   **Root cause needs BOTH halves, which is why it hid so well:**
   - **C++:** `BuildFilterValues`' producer was a `unique_ptr` LOCAL to `ArrowStreamInitGlobal`, promising
     only to "outlive the scan_table call". It dies when InitGlobal returns.
   - **C#:** the binding's `Execute` was an `async IAsyncEnumerable`, so its `scan.FilterValues?.Dispose()`
     did NOT run at call time — an async-iterator body starts at the first `MoveNextAsync`, i.e. inside
     `get_next`, long after InitGlobal returned. `ArrowProducer::Stream()` hands out a pointer INTO the
     object, so that release locks a destroyed `std::mutex`.

   **Why only macOS reported it:** Apple's `pthread_mutex_lock` validates the signature and returns
   EINVAL, which `std::mutex::lock` turns into a throw; glibc and Windows lock a destroyed mutex
   silently. Same lesson as bug 1 — a passing platform proves nothing about a use-after-free.

   **The `WHERE` is load-bearing** (no predicate ⇒ no filter constants ⇒ no producer ⇒ no crash), and the
   reason filter values exist at all for a function whose binding says `SupportsPushdown => false` is that
   `BindingBoundTable` reports `true` for a global/custom function — that flag is the host's BY-NAME
   projection mapping, not SQL pushdown (its doc says so). Reading the binding's flag instead of the
   wrapper's is what made an earlier pass wrongly "rule out" the filter-values path; the other earlier
   ruling-out was checking `StaticTableFunction.Execute` (a plain method — correct for THAT class) while
   `fabricator_seq` is `GfSeqFunction`, which implements `ITableFunction` directly with an async iterator.

   **Fix, in two layers:** the producer is now owned by `ArrowStreamGlobalState::filter_value_producer`,
   so it lives for the whole scan; the destructor body releases `stream` BEFORE member destructors run
   (a destructor body always does), so a dispose triggered by that release still sees a live producer.
   And the four bindings that ignore pushed filters now dispose in a PLAIN method and delegate to a
   private iterator (`GfSeqFunction`, `GfColumnsFunction`, `cf_columns`, `SqlServerProcedure`) — needed
   independently, because an iterator that is never enumerated never disposes at all, leaving the release
   to the GC finalizer. The contract note lives on `StaticTableFunction.Execute`, which already did it right.

   **How it was found without a CI cycle, and the transferable technique:** a destruction-ORDER bug is
   deterministic, so the non-faulting platform executes the same sequence and can be made to *detect* it.
   A temporary out-of-band liveness registry in `ArrowProducer` (origin string + alive flag in a static
   map, so `Release` never dereferences freed memory) printed
   `LATE RELEASE (use-after-free) of producer … created at [BuildFilterValues]` **on Windows**, first try.
   Then a **class sweep** with the diagnostic still armed over all 53 hermetic suites (4152 assertions)
   came back with ZERO other late releases, so this was the only instance. Reach for this before paying
   for a 20-minute remote debug cycle: you do not have to debug on the platform that faults.

3. **Tier 2's first CI run found an undeclared parquet dependency — the same class as the six hermetic
   suites, in the tier that had never run (FIXED).** All infrastructure came up green (build, TLS certs,
   compose, provisioning); `verify_mssql_s3_polybase` then failed at line 267 — its ONLY
   `native_write true` section, whose data files are written by a host `COPY … (FORMAT parquet)` — with
   `Copy Function with name "parquet" is not in the catalog`. The suite never declared `require parquet`
   (Tier 1's native-write suite does, line 12), so nothing loaded it and the copy-function lookup fell
   back to autoload-from-DISK. **Reproduced locally in one shot with the empty-USERPROFILE trick** —
   identical line and identical 117/116 assertion counts to CI — which is the proof that trick is worth
   keeping: it turns a service-tier CI failure into a local edit loop. A developer box passes either way
   because it has parquet under `~/.duckdb`. Adding the directive does not change the derived
   classification (still 53 hermetic / 42 service; the classifier keys on `require-env`).

**Still to build:** a release job (attach `pack-distribution.ps1` output to a GitHub release). The
packaging tier itself EXISTS now as `distribution.yml` (dispatch + `v*` tags) but has never been run —
its first run is unvalidated, and it is the one tier that DOES need `OVERRIDE_GIT_DESCRIBE`. **macOS Gatekeeper is
a caveat CI structurally cannot cover**: a browser-downloaded `.duckdb_extension` carries
`com.apple.quarantine`, which can refuse an unsigned dylib, while a runner never quarantines what it
built. Needs a real Mac and an install-doc note.

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
  lives in `Fabricator.SqlServer`. Keep it that way.
- **The C++↔C# Arrow boundary always uses STANDARD encoding** (`fabricator::BoundaryClientProperties`, used at
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
- **Catalog-entry evictions RETIRE, never destroy (2026-07-16 — use-after-free fix).** Every eviction of
  a materialized entry (`InvalidateEntryCache`/`InvalidateAllEntries` on rollback, `InvalidateMatching`,
  the ALTER re-key/eager-refresh, DropEntry, CREATE-OR-REPLACE re-adds, all self-heal evicts — and the
  catalog-level `schemas_` on DROP/REPLACE SCHEMA) moves the `unique_ptr` into a GRAVEYARD
  (`retired_entries_` / `retired_schemas_`, freed at teardown) instead of destroying it: the lookup paths
  hand DuckDB's binder RAW pointers held across bind→plan→execute, so a concurrent eviction destroying
  the entry was a UAF — hit under `dbt --threads 4` incremental full-refresh (4×1M, box) as
  `INTERNAL Error: CatalogEntry::ParentSchema called on catalog entry without schema` (the binder's
  virtual call landing on the destroyed entry's REWOUND vptr — that error text is the fingerprint of a
  destructed catalog entry; a concurrent thread's post-commit ROLLBACK → `InvalidateAllEntries` destroyed
  the `__dbt_tmp` entry between the rename's bind lookup and its `ParentSchema()` call,
  bind_simple.cpp:160). Stale-but-alive matches the cache's existing staleness semantics; schema entries
  must outlive table entries (each entry's `ParentSchema()` is a REFERENCE into its schema entry). Never
  "optimize" evictions back to immediate destruction.
- **CHECK constraints + non-literal DEFAULTs on CREATE: deliberately skipped** (per user).
- **BRANCH NAMING mirrors DuckDB's (adopted 2026-07-25).** The default branch is
  **`v1.5-variegata`** — the same name DuckDB uses for its 1.5 release line (`refs/heads/v1.5-variegata`;
  its predecessors are `v1.4-andium`, `v1.3-ossivalis`), which is also what the extension ecosystem
  (duckdb-httpfs/-delta/-azure) does. The duckdb submodule pin belongs to the branch: `v1.5-variegata`
  pins release tags within the 1.5 line and moves tag by tag. **`main` is RESERVED for tracking duckdb
  `main`** (the next, unreleased version) and **does not exist yet** — deliberately: creating it means
  absorbing continuous upstream API churn (the 1.5 `ExtensionLoader` break is the precedent) and
  doubling CI minutes for zero consumers. Add it as a nightly allowed-to-fail branch when there's a 1.6
  preview worth tracking. Note the sharp edge: `main` will eventually exist again but MEAN something
  different, so don't treat a `main` reference in older notes as "the current line".
- **Commit only when asked.** The Python scaffold (`main.py`/`pyproject.toml`/`uv.lock`/
  `.python-version`) is intentionally left untracked. `.gitignore` note: `**/fabricator/` would match the
  *source* `src/fabricator/` + `src/include/fabricator/` — negations re-include them; never re-broaden it.

## Sibling repos (reference under `D:\repos\`)

(engineered-wood is no longer here — it's an in-tree submodule `engineered-wood/`, see "Build & test".)
`SqlServerFlights` (reusable C# SqlClient/DAX→Arrow; its `Airport/Data` `ArrowTypeConverter.cs`/`FlightField.cs`
are the granular type-conversion reference — original SQL type + precision/scale/length carried on Arrow
field metadata for precise + lossless round-trip, and Arrow extension names `arrow.bool8`/`arrow.uuid`/
`arrow.json` to disambiguate same-storage types; see [docs/warehouse-support.md](docs/warehouse-support.md)
§3.4 for the future type-mapping refinement), `ArrowSerializer` (POCO↔Arrow for Phase 3)
