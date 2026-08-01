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

### Sync-over-async cleanup — DONE (convention: sync ABI wrapper blocks ONCE on an async core)

The Delta bridge is FULLY converted (DeltaReader/DeltaWriter/DeltaCatalog/DeltaGlobalTableFunction);
a whole-codebase scan found the per-await anti-pattern was DELTA-ONLY — the other bridges are
single-blocking-point wrappers or sync-native and must be LEFT ALONE. Do NOT treat a nonzero
`.GetAwaiter().GetResult()` grep count as remaining work. Adopt the wrapper→core shape for NEW code.
Full record (moved verbatim from here): [docs/ew-master-migration.md](docs/ew-master-migration.md).

### THE EW CLAST-MASTER RE-PIN (2026-07-22 — the current engine; full record: [docs/ew-master-migration.md](docs/ew-master-migration.md))

The engineered-wood submodule pin moved from our long-lived fork lineage (`99e2c3a`) onto
**clast-project/engineered-wood master (`e48f449`, Curt's PR#4-parity landing) + the additive
`fabricator-patches` branch** (7 commits, pushed to the cmettler fork, pin `7fecc2b`;
`.gitmodules` `branch = fabricator-patches`). The strategy: fabricator-specific needs live as a
SMALL upstreamable patch set ON TOP of clast master — never a fork again — so future EW bumps are
merge-master-into-fabricator-patches + re-pin. What the patches carry: the **`DeltaTable.PlanFiles`
planning API** (proposed to Curt 2026-07-25, endorsed, and BUILT by us 2026-07-26 — it REPLACED the
earlier `DeltaFilePruner`-public patch, which is retired; full record in the `PlanFiles` subsection below);
create-time `configuration`/`preAssignedSchema`/`materializedRowIds` params; rowid read-back
`rowIdsOut` correlation + derived-id fallback + CoW CDF capture + partition-aware cdc writes +
DV-aware CDF inference; schema-evolved compaction fixes; the **narrow-int parquet write-corruption
fix** (1-/2-byte Arrow arrays reinterpreted at the 4-byte physical width — silent corruption,
pre-existing, upstream-candidate); pass-through source-field relabel fixes (WidenBatch/
BackfillMissingColumns); and the **variant TRANSPORT** (`SchemaConverter.VariantTransportExtensionName
= "ew.variant_transport"`, `VariantTransport` blob⇄`VariantArray` at EW's host boundary,
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

**⚠ A BUMP IS PENDING AND MEASURED, NOT STARTED (2026-07-31).** Upstream landed **all five #15 slices in
one day** (#18–#22) plus the shredding rework (#16/#17) — and **our PR #6 is MERGED upstream**. Our pin is
**8 commits behind**; a trial `merge-tree` gives **4 conflicts**, upstream rewrote **+1325** lines of
`DeltaTable.cs` against our +1165 in the same file, and **15 public `DeltaTable` methods were removed of
which our Bridge calls 12, across 24 call sites** (the read/DML overload families collapse into
`ReadAsync(DeltaReadOptions)` / `DeleteRowsAsync(RowSelection…)` / `UpdateRowsAsync(RowSelection…)`;
`DeltaTransaction`'s `Stage*` splits into `Stage*`/`Require*`/`Declare*` by RETRY CONTRACT, so those calls
need re-classifying, not renaming). **`PlanFiles` survived** (doc-only change to `PlannedFile`), and Curt
kept `ReadAllAsync`/`ReadAtVersionAsync` as wrappers, so those call sites are safe. He DEFERRED open
question 4 (the transaction-level exemption opt-in) himself, and will return to our porting offer "once
there is a branch worth porting against". Two traps: `git cherry` finds **0** redundant patches even though
the shredding one IS upstream (#16 reworked it, so patch-ids differ) — judge by reading, and DROP that
patch rather than merging it, which removes the modify/delete conflict; and this wants its own branch.
Full measurement + suggested order: **[docs/ew-master-migration.md](docs/ew-master-migration.md) §PENDING
BUMP.**

**The bump-by-bump journal** (every EW pin move, `PlanFiles`, the path-keyed DV DML, the `_metadata`
surface, the variant-transport decision + shredding split, the `DeltaTransaction` flush migration, and
the `TransientRowAddress` analysis) **moved verbatim to
[docs/ew-master-migration.md](docs/ew-master-migration.md) §Appendix — read it BEFORE the next EW bump
or upstream offer.** Standing rules distilled there and still binding: merge upstream/master into
fabricator-patches (fast-forward pins, NEVER force-push — release tags pin EW shas); after taking a
method wholesale from upstream, diff it against upstream and demand byte-identity (the auto-merged
duplicate-statement trap); only the net472 leg proves a change offerable; check `git log -S` before
assuming upstream reimplemented us (it may be convergence); read the DOC hunks of a conflict, not just
commit subjects.

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
  (the storage extension is registered under both `fabricator` and `fabricator`). Its gate, `test/verify_generic_names.test`, was DELETED by the rename (`2a26b7a`) together with the aliases it pinned — there is no such suite now. <!-- check-docs:ignore (naming it IS the point) -->
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
- **KEEP `README.md` IN SYNC — a standing rule, not a task (user, 2026-07-30).** `README.md` is the
  **user-facing** surface; this file and `docs/` are project memory (organised by the order things were built,
  dense with why-we-rejected-X, written for whoever maintains this next). **Whenever a change to CLAUDE.md or
  `docs/` adds or alters something an extension USER can see — a function, a setting, an ATTACH option, a
  behaviour, a gotcha — update `README.md` in the SAME commit.** It is not a separate deliverable and must
  never be parked again.
  - Why the rule exists: it had already drifted badly. When it was introduced (2026-07-30) the README had
    **zero mentions** of provider macros (global, shipped 2026-07-24, *and* catalog-bound),
    `fabricator_host_query`, `fabricator_delta_scan`, or SQL-generating table functions — four user-visible
    capabilities with no user-facing documentation at all. Nothing was wrong in the README; it was simply
    never updated alongside the internal docs, which is precisely what this rule prevents.
  - **Run the README's SQL examples before committing them.** They are copy-pasted by users, so an untested
    example is a defect shipped to the least-equipped audience. All examples added in that pass were executed
    first.
  - Two docs are flagged ⚠ in the documentation index because their prose is stale
    (`multifile-delta.md`'s "Phase-A slices BUILDING" header; `native-delta-write.md`'s pre-flip defaults
    table + the removed `deltalake` alias). **Do not source README content from either until they are fixed** —
    a user-facing page repeating a wrong default propagates it to the audience least able to spot it.
- **FABRIC REST API FUNCTIONS — P0 BUILT + VALIDATED LIVE; P1/P2 designed (2026-07-30):
  [docs/fabric-api-functions.md](docs/fabric-api-functions.md) (§9c = as-built, §10 = the full
  API sweep with a verdict per area).** `fabric_*` functions over `Microsoft.Fabric.Api` (already a
  Bridge PackageReference, 2.14.0 — no bump; the pinned dll carries every P0/P1 method).
  **Shipped:** `fabric_refresh_sql_endpoint()`/`_ex`, `fabric_list_shortcuts()`/`_ex`,
  `fabric_create_shortcut` / `_alter_` / `_json` / `fabric_drop_shortcut`, plus `fab_delta_info()`.
  Catalog-bound on a **OneLake** Delta attach ONLY, inheriting workspace+lakehouse+credential from the
  ATTACH (dbt runs OFF Fabric, so the ambient chain is useless there and a GLOBAL function has no route
  to a DuckDB secret). Gate `verify_delta_catalog_functions` 21 (hermetic); hermetic tier 62/5558.
  - **Enabling refactor, reusable: the Delta catalog now HOSTS catalog-bound functions** (all 7 ABI
    members used to throw; FUNCTIONS metadata was the 1-column fallback). New Bridge pieces
    `FunctionsMetadata` (the kind-6 stream built IN MEMORY — no SQL engine to `UNION ALL` through) and
    `CatalogFunctionSet` (registry + the five members + the `__all__` schema sentinel), so DAX/deltars
    can host functions by wiring the same two. C#-only, no ABI change.
  - **⚠ ZERO-ARGUMENT FUNCTIONS WERE IMPOSSIBLE, AND FAILED SILENTLY — now fixed.** Apache.Arrow 23
    cannot represent an EMPTY schema across the C interface in EITHER direction (export and import both
    throw `ArgumentNullException('fields')`; verified with a positive control). The host treats a failed
    schema fetch as "discovered name is stale" and **erases the function**, so the only symptom was the
    Debug WARN `GetFunctionParamSchema failed: … 'fields'` that this file previously recorded as
    "benign — global functions pass". It was not benign, it was this. Fix needs BOTH halves: C#
    `ArrowSchemaExport` hand-builds the empty struct (`+s`, 0 children) since `CArrowSchema.release` is
    internal; C++ passes **no args stream at all** for an argument-less table function (`args` was
    already nullable). Zero-arg SCALARS stay impossible by design — a scalar's arg batch is also how row
    COUNT crosses — so an argument-less function must be a TABLE function.
  - **`fabric_run_notebook()`/`_ex` BUILT + proven end-to-end** (the elevated ask). Parameters ride
    **`executionData.parameters`** `{name:{value,type}}` — LIVE-VERIFIED honoured; the generic top-level
    `parameters[]` array is accepted with 202 and **SILENTLY IGNORED** for notebooks, so a hand-rolled REST
    call looks like it works while the notebook runs on defaults. Proof was reading the values BACK from the
    notebook's own output (`{"p_text":"from-sql","p_int":42,…}` with correct str/int/float/bool). Blocking by
    default (cap 1 h; cold Spark ≈ minutes); `wait_seconds := 0` submits only. **`exitValue` lives at
    `properties.exitValue` on the NOTEBOOK-scoped instance GET only** (absent from the SDK model in 2.14.0
    AND 2.18.0) and came back **NULL in every run** on both computes despite the notebook API existing and
    being called ⇒ documented best-effort, do NOT build control flow on it. That same `properties` carries
    `compute` + `executionSnapshotUrl` (+ Spark UI/driver-log links) — a portal diagnosis link from SQL.
    **Poll the ITEMS-scoped instance, enrich from the NOTEBOOK-scoped one**: the latter 404s
    (`ItemNotFound` / "no notebook execution state found for the runId") for a while after submission, so
    reading it first turns a healthy run into an error.
  - Also BUILT: the P2 introspection set `fabric_workspaces` / `fabric_items`+`_ex` / `fabric_lakehouses` (with the SQL
    endpoint connstr — the bridge to a T-SQL ATTACH) / `fabric_warehouses` / `fabric_connections` /
    `fabric_notebook_parameters` (heuristic — parses the papermill `parameters`-tagged cell; 0 rows is a
    legitimate "no tagged cell"; `GetNotebookDefinition` is an LRO, ~20 s, never per-row). All live-verified.
  - **Live findings that change USAGE:** `status='NotRun'` from a refresh means **already in sync, NOT
    failure** (all 19 tables on `LH`; a hook asserting `='Success'` fails on a healthy refresh — assert
    `<>'Failure'`); `table_name` is **schema-qualified** on a schema-enabled lakehouse; the SP is refused
    (`PrincipalTypeNotSupported`/`FeatureNotAvailable`) for **ResetShortcutCache** and **notebook
    CREATION** despite documented support (notebook creation stays a one-time portal action;
    `UpdateItemDefinition` IS allowed, which is how the spike notebook gets filled). **`fabric_connections()` returning 0 is
    identity scope, not absence**: connections carry their own role assignments, so an SP sees only its
    own — `LH` certainly has connections (its ADLS/S3 shortcuts require them) and the SP saw none.
  - **⚠ EXPERIMENT-DESIGN trap that produced a WRONG answer twice.** The first two parameter runs concluded
    "both payload shapes are ignored"; both shapes were submitted in sequence and the notebook's result file
    read ONCE afterwards, so the second (genuinely ignored) shape's output was attributed to BOTH. A shared
    side-channel read after N experiments measures only the last. Clearing the marker and reading PER shape
    gave the real answer. The standing "a negative result is not a measurement" rule in a new disguise: the
    method worked, the ATTRIBUTION was broken. Also re-learned: verify the precondition first — the
    `parameters` cell tag was confirmed to survive the definition round-trip before trusting any of it.
  - **C# trap:** an Azure *extensible enum* has an implicit conversion FROM string, so
    `cond ? Policy.X : null` infers `string` and calls `op_Implicit(null)` → `ArgumentNullException` at
    run time. Annotate `(Policy?)null`. Finding it needed a stack trace the ABI does not carry, hence the
    `Wrap`/`Guarded` helpers that append `StackTrace` for UNEXPECTED exceptions only.
  - **NAMED PARAMETERS for custom TABLE functions — BUILT (2026-07-31), and it retired the `_ex` siblings.**
    The `fabricator.named` field-metadata tag (already used by sqlgen) now drives plain table-function
    registration on BOTH the catalog and global paths, so an optional argument is `recreate := true` and
    `fabric_refresh_sql_endpoint` / `_list_shortcuts` / `_run_notebook` / `_items` are ONE function each again
    instead of a plain+`_ex` pair. Authoring: `ITableFunction.NamedParameters` (default empty ⇒ nothing else
    changes). **The binding still reads BY POSITION** — positions are `Parameters` ++ `NamedParameters` in
    declared order and the host marshals EVERY declared parameter, substituting a typed NULL for an omitted
    named one; that equivalence ("omitted" == "explicit NULL") is why collapsing `_ex` changed no binding
    code. **Scalars are excluded and unfixable**: DuckDB `ScalarFunction` has no named-parameter concept, so
    `fabric_create_shortcut_ex(…, conflict_policy)` remains a genuine sibling. Gate
    `verify_delta_catalog_functions` §6 (27) — both spellings, the value really crossing the ABI, a
    misspelled name as a clean binder error, and no positional callability. **Positional + named MIX freely**,
    which is the case that fails SILENTLY if the NULL substitution is off by one (it would corrupt the
    POSITIONAL value rather than error) — pinned hermetically by the demo global `fabricator_seq(n, start := …)`
    in verify_global_functions (72), and verified live on
    `fabric_run_notebook('nb', wait_seconds := 900, params_json := '{…}')` with the args out of declared order
    and the intervening one omitted, read back from the notebook's own output.
  - **`workspace :=` / `item :=` OVERRIDES on every catalog-bound TABLE function (2026-07-31)** — expressible
    only once named parameters existed. The attach still supplies the defaults (the zero-arg call is
    unchanged), but ONE attach can now drive several lakehouses, which a dbt project writing to more than one
    otherwise solves with a second ATTACH purely to refresh an endpoint. Live: `fabric_refresh_sql_endpoint()`
    → LH's 19 tables vs `(item := 'LH2')` → 0 through the same attach. `ResolveItem` gained an explicit
    `workspaceId` so a cross-workspace lookup does not silently search the attach's own workspace. The
    shortcut SCALARS are excluded (no named parameters) and always act on the ATTACHED item.
  - **JOBS + MAINTENANCE + the last introspection — BUILT and live-validated (2026-07-31, §9e):**
    `fabric_table_maintenance` (**V-Order**, which our OPTIMIZE cannot produce — complementary, not a
    duplicate; live `Completed`, table re-read fine afterwards), `fabric_run_job` / `_job_status` /
    `_job_instances` / `_cancel_job` (one shared submit+poll path generalized out of the notebook runner),
    `fabric_lakehouse_tables`, `fabric_operation_status`, and `fabric_reset_shortcut_cache` — the last
    implemented BLIND because the SP is refused (`PrincipalTypeNotSupported`), yet PROVEN WIRED: it reaches
    the service and returns the service's own error, so only the permission is missing (expect it to work on
    a notebook's AMBIENT user-delegated token).
    - **⚠ `Wrap` did NOT cover a PAGED read, and the first live failure exposed it**: `PageableResponse<T>` is
      lazy, so the request happens during ENUMERATION — outside the try — and the error arrived as a raw Azure
      dump with a header list instead of our formatted message. Fixed by `WrapList` (materializes inside the
      guard); all paged reads use it. General shape: *a guard around a call returning a lazy sequence guards
      nothing.*
    - Two API limits found: `fabric_lakehouse_tables` is REFUSED on a **schema-enabled** lakehouse
      (`UnsupportedOperationForSchemasEnabledLakehouse`; works on a flat one — our own discovery covers it
      anyway), and **a DuckDB table function cannot take a SUBQUERY argument** (`Binder Error: Table function
      cannot contain subqueries`) while a SCALAR can — so `fabric_job_status` needs a literal id.
  - **SEMANTIC MODELS — BUILT + LIVE-VALIDATED the same day (§9f):** `fabric_semantic_models`,
    `fabric_refresh_semantic_model` (ENHANCED refresh — live `Completed` with `refresh_type=ViaEnhancedApi`,
    which is the PROOF the enhanced path was taken rather than a plain refresh), `fabric_semantic_model_refreshes`
    (history showed `ViaEnhancedApi` / **`DirectLakeFraming`** / `WebModeling`). On the **Power BI REST**
    surface — `FabricApi/FabricPowerBiRest.cs`, a `partial` half of `FabricApiClient` — because:
  - **the Fabric SDK CANNOT refresh a semantic model (§9f).** The Fabric SDK **cannot refresh one at all** (probed with
    a zero control: `RefreshSemanticModel`/`EnhancedRefresh`/`RefreshSchedule` all 0; only CRUD + definition +
    `BindSemanticModelConnection`). Refresh lives in the **Power BI REST API**
    (`POST /v1.0/myorg/groups/{ws}/datasets/{id}/refreshes`) — a different HOST but the **same audience we
    already mint**: `FabricCredentialResolver.PowerBiScope` is exactly the `powerbi/api` scope the DAX
    provider uses, so the same `fabric_sp`/ambient token works with NO new credential path. Both a Lakehouse
    and a Warehouse have a DEFAULT semantic model (resolved by NAME convention — there is no "default for item
    X" field), and refreshing it is what makes a Delta write visible to **Power BI**, the way
    `fabric_refresh_sql_endpoint` makes it visible to **T-SQL**. Constraints: enhanced refresh needs
    Fabric/Premium (unsupported on shared capacity, 8/day), `notifyOption` is invalid for an SP yet an
    enhanced refresh needs a non-`notifyOption` body, and SP access rides a SEPARATE tenant setting
    (Admin portal → Tenant settings → Developer settings → **"Service principals can call Fabric public
    APIs"** — a DIFFERENT axis from granting the SP a workspace role, which is the confusion this invites: the
    tenant setting says whether SPs may call the APIs at all, the workspace role says what this one may do
    there; both must hold). **MEASURED as already satisfied on this tenant**: the same `fabric_sp` gets 200
    from `GET /v1.0/myorg/groups` and `/groups/{ws}/datasets`, the workspace is `isOnDedicatedCapacity: true`
    (so the shared-capacity enhanced-refresh restriction does not apply), and the model list confirms the name
    convention — lakehouse `LH` has a model named `LH`, plus two `Test Warehouse Model*`, all
    `isRefreshable: true`. So refresh needs NO admin change here. Note NEITHER gate explains
    `PrincipalTypeNotSupported`/`FeatureNotAvailable` — those are per-API principal-type limits no setting
    lifts. The XMLA route adds a third, CAPACITY-level gate (Semantic models workload → XMLA = Read Write). Split to keep: **REST for "refresh this model, tell me when done" (`fabric_*`), XMLA/TMSL through
    the DAX provider for per-table/partition control (`dax_*`)**.
    - **Three traps the live run settled.** (1) The API treats a body of only `notifyOption` as a PLAIN refresh
      AND rejects `notifyOption` for an SP — interacting rules, so we always send `type` (default `Full`) and
      never `notifyOption`; `refresh_type` in the result is how you tell which path you got. (2) Power BI
      reports IN-PROGRESS as **`status = "Unknown"`**, and a just-submitted request may be absent from the
      history entirely — both mean "still running", so a naive `!= 'Completed'` poll exits immediately with a
      misleading value. (3) The request id arrives ONLY in the `x-ms-request-id` header (the 202 has no body;
      a `Location` tail is the fallback). Also: Power BI nests errors under `error.{code,message}` where Fabric
      uses flat `errorCode`/`message`, hence a separate `PowerBiReadAsync` beside `Describe`.
  - **P3 + THE XMLA HALF + the dispatch extraction — BUILT 2026-07-31 (§9g), and this CLOSED every §8
    deferral except one.** The remaining §10 verdicts were P3-demand-driven or skip; the P3 set is now built and
    every **skip** stands with its reason. **Fabric P3 (15 functions, WIRED + reviewed but NOT live-validated —
    the tenant has no git-connected workspace, no deployment pipeline and no mirrored DB to exercise):**
    `fabric_git_status`/`_connection`/`_commit`/`_update`; `fabric_deployment_pipelines`/`_stages`/`_items`/
    `fabric_deploy`/`_operations`; `fabric_capacities`; `fabric_environments`; `fabric_data_access_roles`;
    `fabric_mirrored_databases`/`fabric_mirroring_status`/`fabric_mirrored_tables`.
    **XMLA/TMSL (`dax_*`, the other side of the §9f split, on a DAX attach):** `dax_refresh` /
    `dax_refresh_table` / `dax_refresh_partition` — the LAST is the operation REST cannot express at all.
    - **Standing rules this pass produced.** (1) **`wait_seconds` is our vocabulary but git/deploy accept only
      MINUTES** — rounded UP, floored at 1, because 0 there means "give up immediately", NOT "don't wait" (the
      job APIs' `wait_seconds := 0` genuinely submits-and-returns; these cannot). (2) **A non-nullable
      `DateTimeOffset` on a NULLABLE parent** (`GitSyncDetails.LastSyncTime`,
      `TableMirroringMetrics.LastSyncDateTime`) must be null-tested on the PARENT — written the other way it
      reports the .NET epoch as a sync time. (3) **`ListDataAccessRoles` returns `Response<T>` with its own
      continuation token, NOT a `PageableResponse`** — the one read here that must not go through `WrapList`.
      (4) `fabric_git_update`'s commit hash is **required and positional** on purpose: "update to whatever is on
      the branch now" is how a promotion flow silently deploys an unreviewed commit. (5) Stage resolution takes
      GUID → NAME → ORDER, in that order, so a stage literally named "1" wins over order 1.
    - **XMLA specifics:** SYNCHRONOUS (no request id, no polling — the opposite of the REST path, and it means
      no "Unknown"-status trap), TMSL types are **camelCase and NOT the REST vocabulary** (both accepted,
      unknown rejected locally), `maxParallelism` needs a TMSL `sequence` wrapper, the command is built with
      `Utf8JsonWriter` so a quoted table name cannot alter its structure, and **`refresh` is the ONLY verb
      exposed** — no generic `dax_tmsl(command)`, since the same `ExecuteNonQuery` path would run
      `createOrReplace`/`delete` and turn a read-only provider into arbitrary model mutation.
    - **`fabric_notebook_definition` is DROPPED, not pending** (§4 had listed it): raw base64 parts in SQL is
      the shape rule 2 exists to prevent, the call is a ~20 s LRO, and `fabric_notebook_parameters` is the part
      anyone wanted.
    - **HOUSEKEEPING DONE: ONE registry for all six catalog-bound kinds.** `CatalogFunctionSet` grew from
      2 kinds to 6 (scalar/table/`table_sql`/`inout`/`collector`/`aggregate`) and owns the lookup, the ABI
      members AND the declaration rows; **SqlServer's six static dictionaries and DAX's hand-rolled dispatch are
      gone**. The prize is the KIND STRINGS: the host silently ignores an unknown kind, so a typo there makes a
      function quietly not exist — now written once, `aggregate` vs `aggregate_spill` decided in one place.
      `FunctionsMetadata.Declaration` gained `ParamCount`/`ReturnType` (not host-read columns — the SqlServer
      catalog builds the same declarations as a five-column T-SQL `UNION ALL`, so one producer feeds both). The
      `__all__` sentinel now throws LOUDLY on SqlServer rather than silently dropping such a function. What
      stayed provider-specific: the fallback to a DISCOVERED routine, and the in-out isolation wiring. New
      `FabricRowBuilder` replaced the per-function parallel-builder plumbing (strict about type: a string into a
      timestamp column throws rather than yielding NULLs that look like "the service returned nothing");
      `fab_delta_info` was moved onto it deliberately, because it is the only function on that path with a
      HERMETIC gate. Gate: all **11** service suites over the six kinds green + hermetic 62/5573.
  - Output shape rule (D4): typed flat columns + one raw-JSON column for polymorphic parts; **no STRUCT
    wrapping** (adding a column is additive for `SELECT *`; adding a struct FIELD changes a column's type
    and breaks bound views), no JSON-only. Every `table`-kind function also gets a dead `_each` sibling —
    pre-existing host behaviour, shared with SqlServer's custom table functions.
  A table function that sets neither `serialize` nor `deserialize` still takes part in DuckDB's
  **common-subplan optimizer** (1.5.4+), which dedups subplans by SERIALIZING each operator and hashing
  the bytes. `FunctionSerializer::Serialize` writes only name+arguments in that case and **does not
  throw**, so `fabricator_scan`'s signature carried NO table identity — ours lives in
  `ArrowStreamBindData`. `LogicalGet::Serialize` does contribute returned_types/names/filters, and
  `common_subplan_optimizer.cpp:120` canonicalizes `table_index` to 0 before hashing ⇒ **two scans of
  DIFFERENT tables that share a schema hashed IDENTICALLY**, one was materialized as a CTE, and both
  consumers read the FIRST table's rows. `ArrowStreamBindData::Equals` was correct all along and is
  never consulted — the optimizer compares BYTES, not `FunctionData::Equals`. That is the whole trap.
  - Found by reading **hugr-lab/mssql-extension#211** (same defect, same fix) and checking whether we
    had it. We did, on **every provider** — one `FabricatorTableEntry` / one `fabricator_scan` serves
    SQL Server, Delta and DAX; reproduced on the first two, and DAX reaches the identical path via
    `DaxCatalog.ScanTable`. DAX was arguably the most exposed in practice: it is read-only and the
    failing shapes are pure read shapes (a measure over table A vs table B).
  - **Affected shapes** (all silent): identical aggregate subplans over two same-schema tables — a
    `UNION ALL` of aggregates, or two scalar subqueries. **Unaffected:** joins / EXCEPT / INTERSECT /
    plain unions of rows (bare gets are not materialized), differing column names or types (they ARE in
    the signature), same-table-different-filter (the differing `Filter` child changes the subplan
    signature — safe by plan shape, not by design), and every global/discovered function (their args
    ride in `parameters`, which IS serialized).
  - **Fix**: `FabricatorScanSerialize` writes catalog/schema/table **plus the pushed spec**
    (`filter_json`+constants, `native_filter_sql`, `top_n`, `order_by_json`, `at_unit`/`at_value`) so
    two differently-pushed scans of ONE table cannot collapse either; identical scans still dedup, which
    for a remote provider is a real win. Gate `verify_delta_subplan_dedup` (36), mutation-tested.
  - **`PRAGMA verify_serializer` does NOT work on a fabricator catalog scan, and never did.**
    `LogicalGet::Deserialize` calls `FunctionSerializer::DeserializeBase` UNCONDITIONALLY (before it
    checks has_serialize), resolving the function BY NAME against `TABLE_FUNCTION_ENTRY`; the catalog
    scan is handed out by `GetScanFunction` and is not a registered catalog function ⇒ "Failed to find
    function fabricator_scan()". So `FabricatorScanDeserialize` is UNREACHABLE — and must still exist,
    because `Serialize` only emits bind data when BOTH callbacks are set. Do not "clean it up".
- **⚠ CROSS-ENGINE GAP FOUND + MEASURED (2026-08-01), NOT YET FIXED: we never emit `commitInfo.isBlindAppend`,
  so a Fabric Spark transaction ABORTS against our concurrent append whatever the table declares.** Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §10.6.
  - Live A/B on Fabric Spark 4.1.1.5.5 / **Delta-Lake 4.2.0** (`ConflictChecker.scala` re-read at the `v4.2.0`
    TAG — the Fabric build, not master): 200M-row table, Spark `DELETE … WHERE id % 7 = 3`, our append committed
    inside the window. `Serializable` ⇒ Spark ABORTS (`DELTA_CONCURRENT_APPEND`, naming our v8). `WriteSerializable`
    ⇒ Spark ABORTS TOO (naming our v23). Overlap PROVEN both times by Spark naming the concurrent version.
  - Cause, from Delta's source: `blindAppendAddedFiles = if (commitInfo.flatMap(_.isBlindAppend).getOrElse(false))
    addedFiles else Seq()`. An ABSENT flag = "not blind" ⇒ our appends land in `changedDataAddedFiles`, which is
    checked under BOTH levels. Confirmed three ways: the source; our commitInfo on disk (operation/engineInfo/
    operationParameters only); and Spark's `DESCRIBE HISTORY` showing `isBlindAppend` True for ITS blind append
    and blank for ours.
  - **UPSTREAM OFFER + the plan are WRITTEN DOWN**: [docs/ew-master-migration.md](docs/ew-master-migration.md)
    §isBlindAppend. The `_last_checkpoint` tolerance fix (`14a74a9`) is engine-agnostic and spec-conformant —
    a bug for every EW user, so it should be OFFERED UPSTREAM AS-IS (the host-side one-request read stays
    ours). Upstream is 8 ahead = exactly the known pending bump (#6, #16–#22); **none of those touch isolation
    or blind-append**, so neither item waits for it. And to settle the worry directly: **EW has NOT lost
    WriteSerializable support** — `StartTransaction` still defaults to it and `ConflictChecker` still
    implements the relaxation; what is missing is interop plumbing, not semantics.
  - **The READING half is wrong too, in the OPPOSITE (unsafe) direction.** `ConflictChecker.IsBlindAppend`
    INFERS blind-append from action shape ("only AddFiles"), so another engine's `INSERT … SELECT` from the
    same table — only adds, but it READ — is treated as blind and we skip a check we owe. Correct shape:
    CONSUME `commitInfo.isBlindAppend` when present, fall back to inference only when absent.
  - **The fix must be TRUTHFUL:** Delta's definition is "the transaction READ NOTHING"
    (`readPredicates.isEmpty && readFiles.isEmpty`), NOT "the commit contains only adds" — deriving it from
    action shape would mark `INSERT … SELECT` from the same table as blind, and a wrong `true` makes other
    engines SKIP a check they should run (the unsafe direction). The buffered txn already tracks a read set for
    its own OCC check; derive it from there.
  - **⚠ METHOD: this experiment was VOID FOUR TIMES and each void looked like a clean "no conflict".** The
    window must be PROVEN (Spark naming the concurrent version, or `readVersion` ordering), never assumed. What
    kept failing was OUR end — the append needed ~20 s (process start + CLR + ATTACH discovery), most of the
    DELETE's life. What finally worked: PRE-ATTACH the writer so firing costs only the commit, and make the
    DELETE genuinely expensive (a ~200-row delete finished in <17 s; `id % 7 = 3` rewrites nearly every file).
    Re-creating the table did NOT help — the warmth that matters is the SPARK CLUSTER's, so whichever leg runs
    second is fast; each level needs its own run in the cold first slot (`sparkprobe conflict <Level>`).
- **THE DELTA ISOLATION DEFAULT FLIP — DONE (2026-08-01, behaviour-breaking for CONCURRENT writers).** The
  catalog default is now **`serializable`** (was `write_serializable`), because the measurement below showed
  the old default made us the WEAKER writer than Fabric Spark on any table that declares no level — so the
  effective guarantee depended on which engine wrote. Single-writer behaviour is unchanged; concurrent
  read-write transactions now conflict-abort against a matching blind append where they used to commute.
  Explicit `isolation_level 'write_serializable'` restores the old behaviour, and a table's own
  `delta.isolationLevel` still overrides the catalog.
  - **⚠ The biggest practical effect is NOT the blind-append rule — ROW-LEVEL CONCURRENCY is a
    WriteSerializable-ONLY relaxation**, so under the new default concurrent disjoint-row DML on one file
    CONFLICTS where it used to compose. Three suites caught it the moment the default moved. Users who rely
    on that must attach `isolation_level 'write_serializable'` (one option, old behaviour).
  - **The ATTACH option is now the FALLBACK EVERYWHERE — it was not.** "Table property wins, catalog default
    applies only when the table is silent" held in the buffered path (`PendingSerializable`) but NOT in the
    autocommit rowid DELETE, which read the catalog flag directly. So `delta.isolationLevel = Serializable`
    + ATTACH `write_serializable` behaved INCONSISTENTLY on ONE table: strict inside BEGIN..COMMIT,
    row-level-relaxed for a bare DELETE. Both now route through one `EffectiveSerializable`. The old defence
    ("a single autocommit statement has no cross-statement reads to serialize, so it is only a resilience
    knob") is true about the SEMANTICS and beside the point about the CONTRACT — a table that has DECLARED
    Serializable must not be weakened by a local option.
    - **NOT TEST-COVERED, which is why it survived:** `rowLevelRetry` only bites when that statement's own
      commit races, and sqllogictest runs connections SEQUENTIALLY — a bare autocommit DELETE has no window
      between its scan and its commit. Every row-level scenario drives the BUFFERED path instead. Exercising
      it needs separate processes (`scratchpad/iso_race.sh`); the suite carries a note saying so rather than
      pretending coverage.
    - En route: `ExecuteDelete` now reads the table config ONCE and derives both `enableDeletionVectors` and
      the isolation level (each helper opens the table separately, so adding the isolation read naively would
      have cost a SECOND `_delta_log` LIST per DELETE on OneLake/S3).
  - **The automatic create-time stamp is GONE (not inverted — removed).** A CREATE used to bake the
    catalog's ATTACH level into the table. That conflates a per-catalog BEHAVIOUR knob with a durable
    per-table DECLARATION, and since the property WINS over any catalog, the stamp made an attach-time
    choice permanent AND silently overrode a DIFFERENT catalog's explicit setting on the same table later —
    measured: with the stamp in place, attaching one path twice at two levels stopped honoring the second,
    which is exactly the composition our level-contrast suites rely on. Declaring a level is now explicit
    and per-table (`WITH ("delta.isolationLevel"=…)` or `fabricator_delta_set_tblproperties`), and that is
    the spelling to use when Spark must honor the looser level (it HONORS a stamped WriteSerializable even
    though its DDL refuses to set it). `CreateConfig`'s `serializable` parameter is now inert — removing it
    is a mechanical ~6-signature cleanup left for later, deliberately not mixed into a behaviour change.
- **ISOLATION + ONELAKE MULTI-WRITER — MEASURED LIVE 2026-07-31; one bug FIXED, one gap OPEN. Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §8.1 (multi-writer) + §10.6 (Spark isolation).**
  Two long-standing claims in this file were wrong, and both were beliefs never measured.
  - **`write_serializable` is DATABRICKS' default, NOT Spark's** — every "Spark's default too" here was FALSE.
    Fabric Spark 4.1.1 records **`Serializable`** for its own commits AND its DDL validator **REJECTS**
    `delta.isolationLevel='WriteSerializable'` outright (`requirement failed: … must be Serializable`) at CREATE
    *and* ALTER; `SnapshotIsolation` likewise; only `Serializable` is accepted. Controls both fired, and the two
    negative controls fail DIFFERENTLY (`'Bogus'` doesn't parse at all) — so OSS Delta knows the enum and it is
    the *table-property validator* that admits one value. **Consequence: on a shared table with the property
    ABSENT we apply WriteSerializable while Fabric Spark applies Serializable — we are the more permissive.**
    ATTACH `isolation_level 'serializable'` to match Fabric Spark. A `WriteSerializable` value WE stamp is
    **honored** by Spark (it read, INSERTed, DELETEd, and recorded `WriteSerializable` for its own commits) — it
    just can't SET it, so such a table's isolation is only manageable via `fabricator_delta_set_tblproperties`.
    We deliberately do NOT block the stamp. Corrected in README + `DeltaCatalog`/`DeltaTxnBuffer`/
    `DeltaGlobalTableFunction` comments + `verify_delta_tblproperties`.
  - **OneLake multi-writer was "safe" by INFERENCE only** (its §8 row carried no numbers while local/S3 did).
    Now measured: **no lost writes ever** (versions always unique+contiguous, all groups complete), but
    **low contention never exercises the guard** — 32 commits over 4 processes produced ZERO conflicts, so a
    green low-contention run proves nothing about put-if-absent. Forcing contention (8 writers × 12 tiny
    commits) reproducibly broke writers.
  - **BUG FIXED (EW `CheckpointReader`, on `fabricator-patches`): `_last_checkpoint` is an advisory HINT and was
    treated as authoritative.** It is updated by NON-ATOMIC overwrite, so a concurrent reader can see it at
    **zero bytes** → `JsonDocument.Parse` → *"The input does not contain any JSON tokens"* → a **failed COMMIT
    caused by a file that carries no truth**. Now empty/invalid/field-less ⇒ treated as absent (fall back to
    listing the log, which is what the Delta protocol requires). Gate `verify_delta_last_checkpoint` (34,
    hermetic, MUTATION-TESTED); the live 8×12 shape went from 1–2 failures per run to **96/96 clean**.
  - **SECOND BUG ROOT-CAUSED + FIXED — and it is the SAME root object as the first.** A raw Azure **412
    `ConditionNotMet`** escaped `complete_bulk` (never became a `DeltaConflictException` ⇒ no retry ⇒ the
    statement failed). Mechanism: `OneLakeDataLakeFileSystem.ReadAllBytesAsync` used `OpenReadAsync`, i.e.
    Azure's **lazy `LazyLoadingReadOnlyStream`**, which fetches a blob in successive RANGE requests and
    **pins the ETag, sending `If-Match` on the later ones** — so a `_last_checkpoint` overwritten in place
    mid-read TEARS. Both multi-writer failures are therefore one root cause (that file being overwritten
    non-atomically) by two mechanisms: *empty content* (the parse guards) and a *torn ranged read* (this);
    the parse guards could never catch the 412, which is thrown by the READ, before parsing. Fixed in two
    layers: `ReadAllBytesAsync` now does ONE unconditional `ReadContentAsync` (a single request cannot tear,
    and `ITableFileSystem` documents the method as being for SMALL files), plus `ReadLastCheckpointAsync`
    treats **any** read failure as "no hint" (cancellation excepted).
    - **A WRONG hypothesis is recorded on purpose.** The obvious suspect was `CreateAsync` catching only 409
      while `RenameAsync` catches 409|412. `scratchpad/adlsprobe` **falsified it deterministically** (no race
      needed): on live OneLake a conditional CREATE and a conditional RENAME onto an existing path both raise
      **409 `PathAlreadyExists`**, never 412 — so 409-only was already correct there. That falsification is
      what redirected the search to "something is sending an ETag precondition".
    - **⚠ THE TRAP: a client library can add a conditional header you never wrote.** Our source contains no
      `IfMatch` on any read path, so grepping for it "proved" the wrong thing — `OpenRead` inserts it
      internally. Only a stack trace showed this, which is why the log sink now appends the inner-exception
      chain + full **stack trace at `Debug`** (it used to log type + message only: *what* failed, never
      *where*). With that in place the failure reproduced on the FIRST attempt and named its own site.
      Harness: `ATTEMPTS=N bash scratchpad/hunt412.sh`. Verified after the fix: the same 10×15 shape ran
      **150/150 commits with zero 412s**.
    - **UNEXPLAINED, unrelated, and seen repeatedly — do not mistake it for a lost commit:** in several runs a
      single `duckdb.exe` finished ALL its work (last commit logged, every version landed) and then **did not
      exit**, blocking the harness's `wait`. Observed both before and after these fixes and on runs with no
      errors, so it is a teardown issue on the OneLake+hosted-CLR path. Not investigated.
  - **Diagnostic gap closed en route:** the txn-buffer flush's OCC retry was a SILENT `catch`, so multi-writer
    behaviour was unobservable — a run whose writers merely serialized looked exactly like one where the guard
    rejected and retried. It now logs `delta flush …: commit conflict — reopening at latest (attempt n/16)`.
  - **Method notes worth reusing:** at `Warning` level a conflict-free run leaves an EMPTY log, which is
    indistinguishable from a broken sink ⇒ log at `Information` so the per-commit lines are a POSITIVE CONTROL;
    and `rm *.log` does NOT match `*.fablog`, which silently mixed a previous run's counts into a later one.
- **THE DELTA NATIVE DEFAULTS FLIP — DONE (2026-07-29, behaviour-breaking for `PROVIDER 'delta'`).**
  `native_read`/`native_write` used to default **off** everywhere, so the production path was opt-in and
  the *tested* path was the pure-EW codec. Now **the provider NAME selects a default profile**
  (`DeltaBackend.NativeDefaultsFor`): **`PROVIDER 'delta'` ⇒ both ON** (DuckDB reads/writes the parquet
  bytes, EW owns the log — the hybrid we actually ship), **`PROVIDER 'engineeredwooddelta'` ⇒ both OFF**
  (pure codec). Explicit options still win on either spelling. The name reaches the backend through a
  3-arg `IBackend.OpenCatalog` **default-implementation interface overload**, so SqlServer/DAX/deltars are
  untouched — no ABI bump. The redundant alias `deltalake` was REMOVED in the same pass.
  - **The flip found two real bugs that were unreachable while native was opt-in** — the whole point of
    making the shipped path the default one: (1) `FIELD_IDS` described the whole table schema instead of
    the STREAM, so a write whose stream omitted columns was REFUSED (`00f0475`); (2) a buffered append
    **committed inside an EXPLICIT transaction** when the table had an IDENTITY column, so `ROLLBACK` left
    the rows behind (`b9ed65e`). Bug 2's code comment defended the shortcut with an argument about append
    *commutativity* — which says nothing about *atomicity*. Treat a comment justifying a shortcut on
    concurrency grounds as unexamined for rollback.
  - **The engines are NOT at parity and cannot be:** clustered/Z-order OPTIMIZE needs `native_write`
    because the recluster's global ORDER BY uses DuckDB's **spilling** sort, and EW has no external sort.
    A clustering-declared table on a codec catalog therefore **WARNs** rather than silently bin-packing
    (`3a1c898`, `verify_delta_clustered_optimize` §8).
  - **Suite strategy: split by intent, plus a doubled leg.** Each suite is pinned to the engine it is
    *about* (`verify_delta_catalog_variant`, `_row_tracking_virtual`, `_autocommit_pin` → codec, since the
    codec IS their subject); the core four (write/transactions/update/delete) run **twice**, once per
    engine, via `${DELTA_PROVIDER}` interpolation driven by `run-suites.sh`. `verify_delta_rename` pins
    that each spelling selects its documented engine, observed through the data files' own
    `parquet_file_metadata(...).created_by` (`DuckDB version …` vs `EngineeredWood`) — a change to
    `NativeDefaultsFor` or the alias table then fails loudly and names the consequence.
  - **The flip put ~18 delta suites on DuckDB's parquet reader/writer, which they never declared.** They
    passed locally only because this box has `~/.duckdb/extensions/…/parquet.duckdb_extension`; on a bare
    runner they fail with *Copy Function with name "parquet" is not in the catalog*. `require parquet`
    added to all 18 — same class as the tier-2 `verify_mssql_s3_polybase` finding, and again only the
    empty-`USERPROFILE`/`HOME` trick shows it.
- **DELTA SNAPSHOT CACHING (perf) — PREREQUISITES DONE, the cache itself NOT BUILT and the full version
  NOT RECOMMENDED. Full design + every finding: [docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md).**
  Every table REFERENCE costs **4 snapshot constructions per statement**, dead linear in references
  (self-join 8, three references 12), each a `_delta_log` LIST that `ExternalFileCache` does not serve — so
  OneLake/S3 pay most. Shipped so far: `SnapshotPinning.Release` wired to commit/rollback (it was DEAD CODE,
  and the 4096-entry panic `Clear()` it left as the only reclamation was silently breaking snapshot isolation
  for in-flight transactions), and the host-FS opener now resolved PER CALL (`142b350`) — a cached table
  holding a stale `ClientContext*` is a use-after-free, not staleness, and would not fault on this box.
  - **The headline number is a COUNT, not a profile** — nobody has measured what the redundant opens cost in
    wall-clock. The Fabric notebook's 305 s → 15 s came from two OTHER fixes, dominated by `HostFsGlob`'s
    open-per-matched-file (258 s → 2 s). Do not call this "the biggest remaining perf item" again without
    profiling it; that inference is what the doc's decision gate exists to stop.
  - **If anything is built, cache the immutable `Snapshot` — NOT a `DeltaTable`, NOT a `NativeScanList`.**
    `DeltaTable.OpenAsync` is *entirely* "LIST the log, replay it into a `Snapshot`, wrap it in a cheap
    holder", and `Snapshot` is init-only over `IReadOnlyDictionary`. So caching it per (txn, path, version)
    captures ALL the redundant cost while every call still builds its own table — which dissolves disposal,
    the dangling opener AND the thread-safety dependency on an unenforced EW invariant. Serves BOTH engines.
    Needs one small additive EW patch (`FromSnapshot`, since the snapshot-taking ctor is private). Caching a
    live `DeltaTable` buys nothing over this and costs a lease threaded through 6 async iterators. Caching a
    `NativeScanList` is WRONG: it is post-prune, so sharing it between scans with different pushed predicates
    silently DROPS ROWS.
  - **⚠ Two traps recorded in the doc:** there is NO intra-call shortcut (the `Stream*` methods are async
    ITERATORS, so the schema open completes and disposes BEFORE the stream open begins, in a different ABI
    call), and `TableFunction::function_info` is the WRONG shelf for cached state (its lifetime is the PLAN,
    and plans are re-executed across transactions) — use `ClientContextState`/`registered_state`, or the
    existing per-txn `SnapshotPinning` structure whose `Release` is already the disposal point.
- **SINGLE-FILE DISTRIBUTION — BUILT + validated live (phases 1–4 of 5; REMAINING: user-facing install
  docs + CI matrix — CI tier 3 exists).** ONE `fabricator.duckdb_extension` self-installs (extract +
  chain-load + CLR boot; ~2–3 s cold, 0.01–0.2 s warm; win 61 MB standalone / linux 40 MB standard —
  the Fabric-notebook SKU). Build: `scripts/pack-distribution.ps1`; smoke:
  `test/distribution/smoke_distribution.py` (12 checks). Design + findings:
  [docs/distribution-installer.md](docs/distribution-installer.md) §12/§14/§15/§16. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
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
- **`CREATE TABLE … WITH (…)` options + SQL Server EXTERNAL TABLES — ALL FOUR SLICES DONE (ABI v67).**
  WITH write-tuning/CREATE-flag-overrides/TBLPROPERTIES on Delta; S3 external-table INSERT/identity-keyed
  UPDATE+DELETE routing to storage; the CETAS-analog `WITH (location=…, table_type=…)` DDL.
  [docs/create-table-with-options.md](docs/create-table-with-options.md); gates verify_with_options 68 +
  verify_mssql_s3_polybase 252. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **SQL-GENERATING TABLE FUNCTIONS — DONE (ABI v68 `generate_table_sql`, global + catalog-bound).** The
  call DISAPPEARS at bind (`bind_replace` → SubqueryRef); arg-dependent schema + full pushdown for free.
  Rule: fixed SQL text + varying VALUES ⇒ macro; SQL TEXT depends on args ⇒ sqlgen.
  [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §2; verify_sqlgen 59 +
  verify_sqlgen_catalog 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **PROVIDER-DECLARED DuckDB MACROS — DONE (no ABI bump; decl kind `macro` + body column).** DuckDB
  parses the full CREATE MACRO grammar; registered into the SYSTEM catalog at load; injection-free by
  construction. [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1;
  verify_macros 41 + verify_plugin 10. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
  - **CATALOG-BOUND (attach-time) macros — DONE (2026-07-30, no ABI bump; new metadata kind 15).** Resolve
    as `db.schema.m(…)`; the old "§2 covers it" dismissal was **half wrong** and that half is what got
    built. Works by the pattern we already ship: a macro entry returned from `LookupEntry` is expanded
    normally, because DuckDB looks up `SCALAR_FUNCTION_ENTRY`/`TABLE_FUNCTION_ENTRY` and then dispatches on
    the entry's ACTUAL type — the same one-namespace fact that forces our scalar lookup to surface custom
    aggregates. **A schema gives NAMESPACING, not resolution scope**: expansion captures no search path, so
    an unqualified table reference in the body resolves in the CALLER's context (silent wrong table, not an
    error) — so sqlgen (§2) really is the answer for a table macro naming its own catalog, but sqlgen is
    TABLE-valued only, so it is NO answer for a per-catalog **scalar** helper, and the 4e custom scalar is
    marshaled where a macro crosses nothing. Gate `verify_macros_catalog` 50 (hermetic); full record in
    [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1.4.
    **Three traps worth carrying forward:** (1) the body rides its **own metadata kind**, NOT a column on
    the FUNCTIONS stream — that stream is built as **T-SQL executed on the server**, so a column there would
    have shipped a local declaration to SQL Server and back and made declaring a macro depend on server
    reachability (and offered nothing to the SQL-less Delta catalog); reading the producer is what caught
    it. (2) `GetOrCreateMacro` MUST filter by wanted kind: the binder `Cast<>`s on the entry type without
    checking, so handing a scalar lookup a table macro is an unchecked bad cast. (3) macros must be emitted
    by the **SCALAR/TABLE_FUNCTION** `Scan`s, since those are the only types `duckdb_functions()` asks for
    (it switches on the actual type itself). Also fixed en route: a latent OOB read in `ReadStringTable`
    (asks for N columns, and a provider answering an unimplemented kind returns its 1-column `_ =>`
    fallback — a Delta catalog does exactly that for FUNCTIONS, which asks for 3). The check is per BATCH
    and only when `length > 0`; validating the SCHEMA's width instead **broke every Delta ATTACH**, so that
    leniency is load-bearing, not merely tolerated.
- **`hilbert_index` + `bucket` global scalars, declared scalar VOLATILITY, and the FULL LIQUID-CLUSTERING
  stack — ALL DONE, Spark-interop validated live BOTH directions** (writes to clustered tables; SORTED BY
  declares clustering; clustered OPTIMIZE incl. multi-file + partitioned partial recluster + ZCube
  incremental — Spark recognized OUR cubes as its own; `ALTER … SET/RESET SORTED BY` (alter kinds 12/13);
  `SORTED_COLUMNS` COPY option). Gate verify_delta_clustered_optimize 138 + hilbert 27 + bucket 34 +
  sorted_by 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **`SORTED BY` → Delta ORDERED writes — DONE.** Persists as the `fabricator.sortedBy` table property;
  INSERTs re-apply the ORDER BY via a host-side spilling sort. verify_delta_sorted_by 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **dbt DAX→Delta pipeline — DONE + validated live** (`dbt_dax_test/`, gitignored; plain-DAX model bodies
  via the custom `dax_table` materialization). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Eager-write DeltaTxnBuffer — ALL SLICES DONE (A, B, C1–C3, D + edge lifts).** Data files always land
  on storage at statement time; the buffer holds ACTIONS; rollback = invisible orphans for VACUUM. Incl.
  S3 multi-writer conditional-PUT commits (SECRET-routed), the dbt table-swap RENAME fix, buffered
  IDENTITY/CDF/same-txn-DML, and the partitioned×native_read partition-column bug fix. Gate
  verify_delta_catalog_transactions (now 941); semantics [docs/delta-transactions.md](docs/delta-transactions.md).
  Still immediate by design: identity creates, DROP/OPTIMIZE/VACUUM, CREATE-OR-REPLACE/partition-overwrite.
  Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Fabric-notebook AMBIENT AUTH — DONE + validated live.** All three providers work with ZERO
  credentials on Fabric compute via `FabricNotebookCredential` (the trident token service; per-scope
  refreshing tokens); azure `access_token` secrets consumed for SQL. Pinned gap: a STATIC storage-token
  secret cannot serve the fabric+storage audiences for abfss ATTACH — use ambient. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Sync-over-async (Bridge) — DONE** (superseded note; see the entry near the top + the full record in
  docs/ew-master-migration.md). AsyncLocal ambients (`0533eb7`) keep the opener/txn across pool hops. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure` / bespoke
  `SqlServerTableValuedFunction`; dispatch unified under `IBoundTable`, v29). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Load-time GLOBAL functions — ALL FIVE KINDS DONE (scalar/in-out/collector/table/aggregate, ABI
  v46/v47)** incl. the host-FS global table sub-case (`set_active_opener`; `fabricator_delta_scan` is
  pure C# — a new lakehouse format costs zero C++). [docs/global-functions.md](docs/global-functions.md);
  verify_global_functions 63. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **VARIANT for the Delta provider — DONE through SIX passes** (leaf-blob transport
  `ew.variant_transport` — DuckDB #24157 filed for the canonical struct; codec + FULL shredding tiers;
  DML/OPTIMIZE via the IDataFileReader seam; mapped/nested gates; Spark + kernel validated live; the
  Fabric T-SQL endpoint REJECTS VARIANT and id-mode mapping).
  [docs/variant-support.md](docs/variant-support.md); verify variant 157. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **NESTED STRUCT-field schema evolution — DONE** (alter kinds 9–11; recursive read reconcile; the
  native_read presence probe lifted the top-level + nested limitations). verify nested_alter 100. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Delta write-side NOT NULL enforcement — DONE** (`DeltaNullability`, nested included; per-statement
  Delta commits pinned as a documented divergence). verify constraints 50. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Delta IDENTITY columns — DONE** (v53 marker `id BIGINT AS (0)`; OCC retry regenerates from the fresh
  HWM — safer than Spark). verify identity(delta) 38. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **DAX / ADOMD 2nd provider — DONE slices 1–6** (PBI Desktop + workspace XMLA + Fabric SP/ambient auth;
  scan pushdown + streaming to 10.5M rows; `system` DMV schema; `daxeval`/`daxevaltable`(collector)/
  `daxeach`; the read-past-EOF ADOMD gotcha). Read-only **for DATA** — since 2026-07-31 it also hosts a
  `CatalogFunctionSet` and the TMSL refresh trio (`dax_refresh`/`_table`/`_partition`), which move data
  INTO a model; model AUTHORING stays out (no `dax_tmsl`). [docs/dax-provider.md](docs/dax-provider.md);
  verify_dax 29 (manual — needs PBI Desktop). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Multi-edition support (Fabric WH / Synapse / box) — DONE slices 1–6** (`ServerProfile`, MARS gating +
  connection mode, profile-driven type mapping, collation-gated ORDER BY pushdown, JSON/UUID/tz
  read-side; write-side rich types deliberately deferred).
  [docs/warehouse-support.md](docs/warehouse-support.md). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Settings refactor — DONE, all three flavors** (settings v33/v34 + ATTACH options v37 + secret fields
  v38 — the provider is fully self-describing; `RESET` does not fire set-callbacks, restore with SET).
  [docs/provider-extensibility.md](docs/provider-extensibility.md). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Plugin system — default-context SPI DONE** (`FABRICATOR_PLUGIN_DIR`; plugins load into the BRIDGE's
  ALC and must align their dependency closure — Apache.Arrow above all; `Fabricator.Abstractions` is the
  contract assembly; per-plugin ALC isolation deferred). [docs/plugin-system.md](docs/plugin-system.md);
  verify_plugin 10. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
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
  - **FABRIC WAREHOUSE + dbt `table` models — WAS BROKEN, ROOT-CAUSED AND FIXED (2026-07-31):
  [docs/warehouse-support.md](docs/warehouse-support.md) §6.5.** Every dbt table model died at the swap with
  `15225: No item by the name of '[dbo].[<model>__dbt_tmp]' could be found`; box was fine, and a HOOKLESS control
  failed identically (so unrelated to the session-tag work). **Root cause: on Fabric a statement that ERRORS
  inside an explicit transaction ABORTS it — and we were issuing, inside the user's transaction, statements we
  KNEW fail there and then SWALLOWING the failure.** Two of them: `ProbeExternalTable`'s
  `sys.external_file_formats` (a **PolyBase** view, box-only; Fabric answers `15871 'external_file_formats' is
  not supported`), logged as the benign-looking "external-table probe failed … treated as not external"; and the
  `RowCount`/`ColumnNdv` stats DMVs (`dm_db_partition_stats`, `dm_db_stats_histogram`, also unsupported). The
  transaction was poisoned SILENTLY and the NEXT real statement failed confusingly (`15225`, or `208 Invalid
  object name` for a plain INSERT after a CREATE in the same txn). **Fix: both are capability-gated on
  `Profile.IsWarehouse` and never issued** — correct on the merits (a warehouse has no PolyBase external tables;
  stats are costing-only, never pruning) and one fewer round trip per table. Verified: the Fabric dbt target
  builds again (1 model, then 4 at `--threads 4`, plus a session-tag pre-hook model); box re-checked on the gated
  paths (polybase 252, cardinality 4, column_ndv 6, server_profile 15, with_options_mssql 9).
  - **THE STANDING RULE THIS ESTABLISHES: on a warehouse engine, never issue a statement whose failure you intend
    to swallow.** A best-effort probe is free on box and DESTRUCTIVE on Fabric. Capability-gate on `ServerProfile`
    instead of discovering support by try/catch.
  - **A WRONG intermediate conclusion is recorded in §6.5 on purpose.** An earlier pass blamed "a bulk load into a
    table created in the same transaction" because its tests varied TWO things at once (who issued the CREATE and
    bulk-vs-plain insert); holding the CREATE constant showed the bulk was irrelevant — a plain INSERT failed too,
    and a CREATE with NO insert failed as well.
  - Diagnostics added, and they are what made this findable: the bulk path's own DDL is now logged
    (`bulk ddl [txn=… own=…]` — previously only "bulk <table>: create=True", never the statement), and
    `ddl create`/`ddl alter` now carry the txn id + whether the connection was pinned. The decisive datum was
    `ddl create [txn=4 own=False]` followed by `exec [txn=4 own=False]` failing with 208 — same txn, same pinned
    connection, so a different-connection explanation was ruled out.
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
  gated by `SET mssql_exec_invalidate_cache` (default false, Postgres-scanner parity). **Default off ⇒ after
  out-of-band DDL you must call `fabricator_refresh_cache(cat)` / `fabricator_invalidate_cache(cat[, regex])`
  yourself** (both are SCALAR functions — `SELECT fabricator_refresh_cache('db')`, NOT `CALL`). Prefer the
  scoped 2-arg invalidate when you know what you touched; the auto path runs a **full `RefreshCache`**.
  Three conditions must ALL hold for the automatic refresh to fire, and the third is the one that surprises
  (verified 2026-07-30): the setting is on, the SQL matches the heuristic, **and the first argument named an
  ATTACHED CATALOG** — with a raw connstr or a secret name we own no cache for it (`owns == true`), so nothing
  is refreshed and the call silently has no cache effect. Also note the detection is a plain **substring**
  match over `CREATE/DROP/ALTER/TRUNCATE/RENAME/EXEC`, so `UPDATE t SET created_at = …` contains `CREATE` and
  triggers a full re-discovery. Deliberate ("a false positive just refreshes") but NOT uniformly cheap: on a
  Delta/OneLake catalog re-discovery is the expensive glob, not a metadata query. The setting is `mssql_`-
  prefixed while the mechanism is provider-agnostic (`SqlDdl` lives in the Bridge, consulted on every
  `ExecuteDml`).

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

### Shipped function machinery (4b–4h + Phases 5–6) — as-built records moved

Scalar UDFs (4b), TVFs (4c), the Bind/Binding refactor + table session (Phase 5, v27/v29/v30/v32),
stored procs incl. named/OUTPUT params (4d), custom C# scalar/table (4e/4f), table-in-out incl. the
retired push model + per-row procs + OperatorFinalize (4g), the streaming exchange (Phase 6, v28/v31),
and custom aggregates incl. opt-in spill + holistic (4h, v25/v26). All DONE and verified; the design
contracts (which paths push down, session lifetimes, the state-vectorized aggregate rules) are in
[docs/feature-history.md](docs/feature-history.md) §Function machinery +
[docs/custom-functions-design.md](docs/custom-functions-design.md).

- **Filtering**: discovered scalar UDFs + TVFs/procs are gated by the ATTACH `schema_filter` (icase
  `std::regex`, applied in `LoadCatalog`/`RefreshCache`); `table_filter` is table-only and does NOT apply to functions.
- **Parallel partitioned reads** (ConnectorX-style `partition_on`/`partition_num`) — **design note, deferred,
  nothing built**: [docs/parallel-partitioned-read.md](docs/parallel-partitioned-read.md). Two wins to keep
  distinct — parallel *fetch* (form A: C# runs N range queries concurrently + `ParallelMerge` → the existing
  single-stream scan, no ABI) vs parallel DuckDB *pipeline/core usage* (form B: N streams → N scan threads via
  a parallel multi-stream scan = the native form of the proven `UNION ALL` core-saturation trick; bigger). On
  `fabricator_query` the two surface as optional NAMED params (the `daxeval` pattern); a custom
  `IArrowTableFunction` could return `IAsyncEnumerable<IAsyncEnumerable<RecordBatch>>` (outer = partitions).
- **`function_filter` ATTACH option + scoped `fabricator_invalidate_cache(catalog, regex)` + the
  filters-are-enumeration-only lift — ALL DONE (2026-07-15).** A *_filter bounds DISCOVERY, not
  targeted-by-name access. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
## C ABI contract (`src/include/fabricator/abi.h`)

- The managed `Bootstrap.Initialize` fills an `FabricatorVTable` of C function pointers; tabular results
  flow through caller-allocated `ArrowArrayStream`; errors = status code + owned UTF-8 string freed via
  `free_error`. C# error messages prepend the provider error number when available (`FormatError`
  duck-types an `int Number` property → e.g. `"2627: …"`; provider-agnostic, no SqlClient ref in Bridge).
- **`COPY … TO '<path>/<table>' (FORMAT delta, …)` — DONE: path-targeted Delta write, NO ATTACH**
  (transient per-execution catalog; `MODE` = the Spark/delta-rs save-mode vocabulary incl.
  `overwrite_partitions`; repartition-on-overwrite; PARTITION_COLUMNS with every mode; its own atomic
  commit — deliberately NOT rolled back by a surrounding BEGIN). verify_delta_copy_format 109. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
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
- **Prior versions v16–v67: [docs/abi-history.md](docs/abi-history.md)** — the full per-version records,
  moved verbatim from here (incl. the cancellation tiers v65/v66, the native_read/MultiFileReader saga +
  rowid/late-materialization/row-tracking-virtual work under v57, the onelake:// filesystem v55–v64, the
  Delta catalog/DML/column-mapping/native-write records under v47–v49, and the BINARY STATUS notes).
  Read it when touching an existing ABI entry or wondering why one has its shape.
  **Bump rule:** when you add a vtable entry OR change a signature, bump
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
  **⚠ That one-liner does NOT survive Git Bash quoting** (verified 2026-07-28, three variants failed: `cmd //c`
  with escaped inner quotes → *"is not recognized as an internal or external command"*; a bare relative
  `build_cpp.bat` → not found; and a `>nul` redirect inside → *"The system cannot find the path specified"*,
  which looks like a MISSING VS install and is not). What works reliably: write a two-line `.bat`
  (`call "…vcvars64.bat"` then the `cmake --build`) and invoke it by ABSOLUTE path — from PowerShell,
  `cmd /c "D:\...\build_cpp.bat"`. A copy lives in the session scratchpad. **The tell that you did not actually
  rebuild is the binary's mtime** — check `ls -l build/release/test/unittest.exe` rather than trusting exit 0,
  since a failed `cmd` line still exits 0 through the pipe.
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
| 3 | `distribution.yml` | the single-file artifact per platform + the **12-check smoke against a STOCK DuckDB wheel** (`test/distribution/smoke_distribution.py`). 3 platforms; needs `OVERRIDE_GIT_DESCRIBE` (the one tier that does) | dispatch + `v*` tags |
| — | manual | `verify_dax` (Power BI Desktop), live Fabric/OneLake (gitignored SP creds), the 7 deltars suites (`-IncludeDeltaRs`, ~240 MB), and on macOS: Gatekeeper/`com.apple.quarantine` + code signing | by hand |

**Proven-in-CI status (2026-07-26).** Tier 0 green. **Tier 1 green on ALL THREE platforms in ONE run**
(`30192450794`, sha `124ad4f`) — each independently 53/53 suites / 4152 assertions, verified from the job
logs rather than the status tick. **Tier 2 green** (`30192508662`) — 42/42 / 1221, nothing skipped,
`verify_mssql_s3_polybase` at its full 252. Both defects that the first CI runs surfaced (the macOS
`ArrowProducer` use-after-free and the undeclared `require parquet`) are fixed and confirmed IN CI, not
merely locally — a distinction this repo's history says to insist on. **`distribution.yml` is now GREEN
on all THREE platforms too** (`30195834247`): each packs the single-file artifact and passes all 12 smoke
checks against a STOCK DuckDB wheel — cold LOAD, `['fabricator','fabricator_core']` both reporting
loaded, a Delta round trip through the extracted core, the warm fast path, and both
must-not-touch-disk rejections. Artifacts upload as `fabricator-v1.5.5-<platform>-<sku>`
(windows_amd64 Standalone 62 MB / osx_arm64 Standalone 60 MB / linux_amd64 Standard 40 MB). **⇒ ALL FOUR
TIERS ARE PROVEN IN CI.** It took three dispatches: the first run failed on both platforms for two
DIFFERENT reasons and enabling macOS exposed a third defect (findings 4 and 5) — none of them in the
exotic machinery the tier exists to cover, all of them in build-environment assumptions that a
developer box silently satisfied.

**Tier 1 has since stayed green across every pin bump and the `PlanFiles` work** — `01994fb` (second EW
bump) and `5c28297` (`PlanFiles`) both green on all three platforms. A green tier-1 job is a stronger
claim than it looks: `run-suites.sh` floors on 53 suites / 4152 assertions and fails on any SKIP, so the
tick alone proves the counts without reading logs. **One CI gap was closed the same day**: the path
filter listed `.gitmodules` but not the submodule POINTERS, so a pin bump ran NO CI at all (see the traps
list). Note that fix is not self-proving — every commit since has also touched `dotnet/`, so it is only
exercised the next time a pin moves on its own.

**Suite selection is DERIVED, never a hand-kept list** — `scripts/list-hermetic-suites.sh` and
`scripts/list-service-suites.sh` classify by the `require-env`/`require` directives each suite
declares, so a new suite cannot silently sit outside CI. The accounting is complete and checked:
**53 + 42 + 9 excluded = 104**, no overlap. `scripts/run-suites.sh <hermetic|service>` runs them ONE
PROCESS PER SUITE with a fresh scratch dir, and asserts what `unittest` will not: nothing SKIPPED, the
runner never says "No tests ran", and floors on the selected suite/assertion counts. The hermetic tier
CLEARS the service env vars (proving hermeticity); the service tier DEMANDS them and names any that
are missing.

**Per-platform coverage is deliberately unequal — state it, never imply parity:**

| | tier 1 | tier 2 | tier 3 | notes |
|---|---|---|---|---|
| `linux_amd64` | ✅ | ✅ | ✅ Standard | the Fabric deployment target |
| `windows_amd64` | ✅ | (local only) | ✅ Standalone | the development platform; DAX/ADOMD fully supported here |
| `osx_arm64` | ✅ | ❌ impossible | ✅ Standalone | hosted macOS runners **cannot run containers**, so SQL Server/MinIO are unreachable. Demand-driven (DuckDB's user base skews Apple Silicon); DAX untested. Gatekeeper + signing are also outside CI (a runner never quarantines what it built) |

**Traps that cost real cycles — do not rediscover them:**
- **A NEGATIVE RESULT IS NOT A MEASUREMENT UNTIL THE METHOD IS SHOWN TO WORK.** The first two entries
  below are this rule in one narrow form, and `run-suites.sh` institutionalises it (it asserts positive
  facts — "All tests passed" present, nothing skipped, floors met — rather than trusting an exit status).
  It applies identically to every AD-HOC probe, which is where it keeps being rediscovered; three separate
  times on 2026-07-30 alone, each costing a wrong conclusion:
  - **Zero/empty needs a POSITIVE CONTROL.** A missing tool, a typo'd pattern and a genuine absence all
    produce the same `0`. (`strings` is NOT installed in this Git Bash — `strings <bin> | grep -c X`
    silently yields 0 for every X. Use `grep -ac X <bin>`, and check a string that must NOT be there.)
  - **A probe whose PRECONDITION failed is VOID, not evidence** — in either direction. An OPTIMIZE probe
    asserted the concurrent compaction had committed, that assertion failed for an unrelated reason, and
    the failure got read as "no compaction happened, so the code path was never exercised". It had been
    committing all along.
  - **Confirm the query answers the question you asked.** `max(operation)` over a string column returns
    the ALPHABETICAL maximum, not the latest row's value.
  - **Corollary, and the expensive one — to establish that code path A never reaches B, INSTRUMENT B.**
    A backwards grep encodes the searcher's assumed call shape and returns a plausible but incomplete
    enumeration: a regex requiring `…Async` cannot see `table.StartTransaction(...)`, which is exactly how
    "our flush never reaches `CommitOccAsync`" got asserted — on an upstream PR — when the real chain is
    `StartTransaction` → `txn.CommitAsync()` → `CommitTransactionAsync` → `CommitOccAsync`. Reading which
    code emits an error message settles such questions in seconds; backwards tracing does not settle them
    at all.
- **A no-match sqllogictest filter exits ZERO** ("No tests ran"), and the filter is Catch-style, so a
  MID-pattern `*` matches nothing (`test/verify_x*.test` fails, `test/verify_x*` works). A green run
  proves nothing without a positive assertion. (An instance of the rule above.)
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
- **A path filter must list the SUBMODULE POINTER, or a pin bump runs no CI.** `extension.yml` listed
  `.gitmodules` but not `engineered-wood`, so bumping the Delta engine — the highest-risk change we make
  — matched nothing. Both 2026-07-26 bumps ran only because they happened to touch `dotnet/` too; the
  test-deletion bump (`70528db`) ran nothing at all. A gitlink appears in the diff as that exact path, so
  the pattern is `engineered-wood`, NOT `engineered-wood/**` (there are no files under it from the parent
  repo's point of view). `duckdb` + `extension-ci-tools` are listed for the same reason — cheaper than
  reasoning about whether a bump happens to co-edit `extension_config.cmake`. `DuckDB.ExtensionKit` is
  deliberately absent: tier 1 never compiles it, only the dispatch-triggered packaging tier does.

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

**Four defects CI found in its first hours — every one of them invisible on a developer box** (two
destruction-order bugs that only a different allocator faults on, and two "works because of prior
state" bugs that only a CLEAN machine reveals). The through-line: an environment that already has what
you need — an installed extension, a previous build's output — silently satisfies a dependency the code
never actually declares, so a passing local run proves nothing about a fresh one:
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

4. **The packaging tier could never have worked on a clean machine — `pack-distribution.ps1` probed for
   its own build output BEFORE producing it (FIXED).** `$shellLibrary` was resolved at the top of the
   script, then the NativeAOT publish ran ~40 lines later; on a machine with no previous publish the
   probe returned `$null` and the script threw *"Installer shell (Fabricator.Installer.so for linux-x64)
   not found — publish it on a linux-x64 machine first"* immediately after that very publish printed
   `Generating native code` and succeeded. Every prior run — mine, and the WSL linux build — passed only
   because an earlier publish had left the file on disk. The probe is now a `Resolve-ShellLibrary`
   function called AFTER the publish step. Both jobs fail identically, so it is one fix for both
   platforms. This is the single best argument for having built the packaging tier at all: the artifact
   had been produced correctly by hand many times, and the script was still broken for anyone starting
   from nothing.

5. **The dual entry point — the keystone of the single-file distribution — was silently NOT EXPORTED on
   macOS (FIXED).** Enabling osx_arm64 in the packaging tier produced a clean build, a clean AOT link and
   a clean pack, then failed the smoke test: *"Extension … fabricator_core.duckdb_extension did not
   contain the expected entrypoint function 'fabricator_core_duckdb_cpp_init'"*. Cause is UPSTREAM, in
   `duckdb/extension/extension_build_tools.cmake`: on Apple a loadable extension is linked with hidden
   visibility, `-dead_strip`, and an explicit ONE-symbol whitelist
   `-Wl,-exported_symbol,_${NAME}_duckdb_cpp_init`. Our second entry is therefore stripped. Linux
   (`--gc-sections`/`--exclude-libs,ALL`) and Windows (`dllexport`) both keep it, so macOS is the ONLY
   platform where the one-binary-two-filenames trick fails — and it fails at LOAD time, not build time.
   Fixed in our `CMakeLists.txt` with an APPLE-guarded extra
   `-Wl,-exported_symbol,_fabricator_core_duckdb_cpp_init` (ld64 accumulates repeated `-exported_symbol`
   flags, so it adds to DuckDB's whitelist; the leading underscore is the Mach-O C prefix). Worth
   remembering as a general rule: **anything relying on an exported symbol other than the single blessed
   entry point needs an explicit macOS whitelist entry.**

### TWO CONCURRENT RELEASE LINES — releases MUST be distinguishable (requirement, 2026-07-26)

We will ship builds for BOTH lines at once: the current **`v1.5-variegata`** (DuckDB 1.5.x) and an
upcoming **`main`** tracking duckdb `main` (the next, unreleased version). A user must be able to tell
which artifact belongs to which line, and must not be able to grab the wrong one by accident.

**The constraint that decides the design: the shipped file CANNOT be renamed.** DuckDB derives an
extension's entry symbol from its FILENAME (proved during the distribution work — the identical bytes
that load as `fabricator.duckdb_extension` fail as `fabricator_core.duckdb_extension`), so the installer
shell must stay exactly `fabricator.duckdb_extension`. A version can therefore never be encoded in the
extension's own filename; it must ride the CONTAINER — release tag, release-asset grouping, artifact
name, download directory.

What already protects users, for free: the artifact footer records the DuckDB version
(`OVERRIDE_GIT_DESCRIBE`), a stock DuckDB checks it BEFORE any extension code runs, and the installer's
own gate re-checks version+platform against its manifest. So a 1.5.5 artifact loaded into a main-line
DuckDB fails with a friendly error rather than misbehaving — the safety property holds; what is missing
is only human-facing labelling.

**How this is now implemented (2026-07-26).** CI artifacts are
`fabricator-<duckdbversion>-<platform>-<sku>` (e.g. `fabricator-v1.5.5-osx_arm64-Standalone`), and the
`release` job in `distribution.yml` attaches ONE ZIP PER (platform × SKU) to a GitHub release.

**The release assets are ZIPs, and that is forced, not cosmetic.** An asset named
`fabricator-v1.5.5-linux_amd64-Standard.duckdb_extension` would DOWNLOAD fine and then FAIL to load —
DuckDB would derive the entry symbol `fabricator-v1.5.5-linux_amd64-Standard_duckdb_cpp_init` from the
file name. And the bare name `fabricator.duckdb_extension` cannot be used for all three either, because
asset names must be unique within a release. A versioned ZIP satisfies both: the ARCHIVE name
distinguishes platform/SKU/DuckDB version (hence the line), the file inside keeps the mandatory name, and
the release notes say "do not rename it".

**TAG SCHEME — DECIDED + PROVEN END-TO-END (2026-07-26).** Format **`v<fabricator>-duckdb<duckdbversion>`**,
first tag `v0.0.1-duckdb1.5.5`, with one rule that makes it safe: **never publish a bare `vX.Y.Z`.** SemVer
reads the `-` suffix as a PRERELEASE, so a bare `v0.0.1` would sort ABOVE it; with the suffix always present,
ordering within a line stays correct (`v0.1.0-duckdb1.5.5` > `v0.0.1-duckdb1.5.5`) and the two lines stay
distinguishable in the one `v*` namespace the trigger requires. `+duckdb…` would be the semantically correct
SemVer (build metadata) but `+` is %-encoded in URLs and most tooling IGNORES build metadata when comparing,
so the two lines would compare EQUAL — not worth the purity. Use the real DuckDB version for the future line
(`v0.0.1-duckdb1.6.0`), never `-duckdbmain`: a moving target makes a poor release identity. `0.0.1` because
that is what the binary reports (`fabricator_version()` + the footer) — tagging a number the artifact does not
claim mislabels the release against its own contents. Nothing in the workflow hardcodes the tag: title and
notes derive the DuckDB version from the single `DUCKDB_VERSION` var; the tag is used verbatim.

**Release status (CORRECTED 2026-07-28 — the note below used to say "DRAFT … unpublished" and was WRONG,
which nearly caused a published release to be retagged):**
- **`v0.0.1-duckdb1.5.5` is PUBLISHED** (`draft=false`), created 2026-07-27, pinning **`a8de094`** — NOT the
  `5c28297` this note recorded; it was retagged again after that. Three ZIPs attached, 0 downloads:
  `linux_amd64-Standard` 40.1 MB / `osx_arm64-Standalone` 60.0 MB / `windows_amd64-Standalone` 62.2 MB.
- **`v0.0.2-duckdb1.5.5`** cut on 2026-07-28 at `21e7be5`, +30 commits over v0.0.1: both EW clast-master
  bumps, the variant shredding split, the `_metadata` locator conformance, and the `TransientRowAddress`
  helper migration. `distribution.yml` run **green on all three platforms** and the **DRAFT** release exists
  with its three ZIPs (linux_amd64-Standard 40.2 MB / osx_arm64-Standalone 60.1 MB /
  windows_amd64-Standalone 62.2 MB). Publishing is still a human decision.
- **`v0.0.1-duckdb1.5.5`'s RELEASE object was DELETED BY THE USER, deliberately (confirmed 2026-07-29)** — so
  the API lists only v0.0.2. **Its TAG deliberately survives on the remote at `a8de094`**, which is the part
  that matters: the tag is what keeps that release's source reproducible (`git submodule update` cannot
  reliably fetch an unreachable sha, so an orphaned commit would make the tagged build unbuildable). Nothing
  to investigate — do NOT "restore" it.
**⚠ CHECK `draft` VIA THE API BEFORE TREATING A RELEASE AS MOVABLE — do not trust this file.** The retag rule
below is real and still applies; what went stale was the FACT it was applied to. Once published, a tag move
is not merely history-rewriting: the attached assets were built from the OLD commit, so moving the tag leaves
a release whose **source tag and binaries disagree** — worse than a tag that is simply behind. 0 downloads is
luck, not a guarantee. Ship newer code as a NEW tag instead.

**The version number is NOT free to choose: it must match what the binary reports**, or the release is
mislabelled against its own contents. That means bumping **BOTH** declarations, which are easy to miss:
`CMakeLists.txt`'s `FABRICATOR_EXTENSION_VERSION` (→ the `FABRICATOR_VERSION` compile definition, i.e. what
`fabricator_version()` returns) **and** `extension_config.cmake`'s `EXTENSION_VERSION` (→ the extension
footer). `v0.0.2` was preceded by exactly that bump.

Earlier moves, while it genuinely was a draft: `0eadd00` → `c2af48a` (to pick up the first EW bump's two
silent-corruption fixes — the UTF-16-vs-UTF-8 comparator that could make pruning SKIP a file containing
matching rows, and stats truncation splitting a surrogate pair) → `5c28297` (the second EW bump, the
ns/second timestamp guard, and `PlanFiles`) → `a8de094`. Each tag message records what that move gained, so
the reasoning survives without this file. **`distribution.yml`'s release job creates the release with
`--draft`**, so a new tag yields a draft and publishing stays a human decision.

**Still to build:** nothing in the tiers themselves. **macOS Gatekeeper is
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
  - **⚠ It is NARROWER than it sounds, and the wording above invites over-reading it (measured 2026-07-30).**
    The self-heal is in the COLUMN FETCH, so it only covers an entry that has not been MATERIALIZED yet: the
    name is in the discovered list, the fetch fails, the name + entry are evicted, and the caller sees a clean
    `Catalog Error: Table with name X does not exist!` (which is what lets `CREATE … IF NOT EXISTS` work).
    Once a table has been READ in the session, its entry is cached, so an out-of-band DROP is not noticed at
    bind at all — the scan runs and fails with the provider's RAW error, observed as
    `IO Error: Fabricator: scan_table failed: 208: Invalid object name 'dbo.x'`. Both orders were measured
    against SQL Server; the difference is purely whether the entry was already materialized. So "a dropped
    table leaves no stale entry" holds for the un-read case only. A rough edge rather than a designed
    behaviour — nothing depends on the 208 text, and turning it into a clean catalog error would mean
    classifying provider errors at scan time (an object-not-found probe on every scan failure).
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
- **Never set `USE_TMP_FILE` on our COPYs** — it is ALREADY false on every fabricator write path (its
  default needs the target to pre-exist as a REGULAR file; Delta data files are immutable so ours never
  do), and setting it explicitly THROWS combined with `PARTITION_BY` — a defensive blanket setting would
  break `RunCopyPartitioned` at bind. Half-written files are handled by the COMMIT ORDER (orphans →
  VACUUM), not by COPY. Full analysis (2026-07-29): [docs/abi-history.md](docs/abi-history.md), v64 entry.
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

## Documentation index (`docs/`) — with a STATUS per doc

Every doc is listed here, and `scripts/check-docs.sh` FAILS if one is missing — an unreferenced doc is not
wrong but it is undiscoverable, and undiscoverable is how a doc rots unnoticed. When this index was written
(2026-07-30) **11 of 32 docs were unreachable from this file, and all five whose last substantive edit was the
2026-07-15 rename were among them.**

The **status** column is the part no script can produce. `check-docs.sh` verifies that every path, link and
`verify_*` suite a doc cites still exists; it cannot tell whether the prose is still TRUE. `multifile-delta.md`
is the standing example — every reference in it resolves, and its header still announces work the production
path never adopted. Keep the status honest; a wrong status is worse than none.

| doc | status |
|---|---|
| [abi-history.md](docs/abi-history.md) | **current** — per-version ABI records v16–v67. Read before touching an existing entry |
| [aot-bridge.md](docs/aot-bridge.md) | **design only, nothing built** (2026-07-25) |
| [cancellation.md](docs/cancellation.md) | **current** — the three cancellation tiers (ABI v65/v66) |
| [consumption-monitoring.md](docs/consumption-monitoring.md) | **analysis + TWO BUILT** (2026-07-31) — CU/consumption attribution for a dbt run; the `application_name` fix and `db.dbo.fabricator_session_tag()` (gate `verify_session_tag`) came out of it, the CU half is still analysis. ⚠ §2.4c: the session tag is MEASURED UNRELIABLE as a dbt pre-hook at `--threads>1` (a model's body frequently saw a STALE run's tag — worse than none), so `application_name` is the recommended dbt vector; mechanism not yet established, one suspect is DuckDB txn-id reuse colliding in our per-transaction `_txns` keying. THREE tagging vectors CONFIRMED live (`OPTION (LABEL)` on all 5 statement shapes incl. CTAS; `Application Name`→`program_name`; and the WINNER — a run UUID in `sp_set_session_context`, which is SELF-BRIDGING because the EXEC's own `command` text is recorded, so a session's whole statement set attributes by `connection_id` with NO registry, NO label and NO extension feature; a session can also read its own `connection_id`/`dist_statement_id` from `sys.dm_exec_requests`, the latter being the Capacity Metrics join key). Records a live `application_name` DEFECT, the finding that consecutive extension calls do NOT share a session, and a REPORTED-not-confirmed Aug-2026 Warehouse metering change that would undercut per-model costing |
| [create-table-with-options.md](docs/create-table-with-options.md) | **current** — all four `WITH (…)` slices shipped |
| [custom-functions-design.md](docs/custom-functions-design.md) | **current** — the 4b–4h contract; §11.1 is the in-out design |
| [dax-provider.md](docs/dax-provider.md) | **current** — read-only DAX/ADOMD provider, slices 1–6. Gate is MANUAL (needs Power BI Desktop) |
| [dbt-hooks.md](docs/dbt-hooks.md) | **current** — validated box + Fabric |
| [dbt-incremental.md](docs/dbt-incremental.md) | **current** — validated box + Fabric |
| [delta-catalog.md](docs/delta-catalog.md) | **current** — the main Delta provider reference |
| [delta-rs-provider.md](docs/delta-rs-provider.md) | **current but SECONDARY** — the delta-rs provider is opt-in (`-IncludeDeltaRs`, `FABRICATOR_DELTARS=1`); its 7 suites are outside CI |
| [delta-snapshot-caching.md](docs/delta-snapshot-caching.md) | **design + decision gate; the cache is NOT built** and the full version is not recommended |
| [delta-transactions.md](docs/delta-transactions.md) | **current** — buffered-DML semantics. §8.1 = the MEASURED OneLake multi-writer result (2026-07-31; one bug fixed, one gap left OPEN); §10.6 = the MEASURED Fabric Spark isolation-property matrix, replacing a stale "we do NOT read it" |
| [distribution-installer.md](docs/distribution-installer.md) | **current** — single-file SKU, phases 1–4 of 5 |
| [ew-master-migration.md](docs/ew-master-migration.md) | **current** — the EW pin journal. Read BEFORE the next EW bump |
| [fabric-api-functions.md](docs/fabric-api-functions.md) | **current — P0 BUILT + live-validated, P1/P2 design** (2026-07-30). §9b spike results, §9c as-built (incl. the zero-argument Arrow fix), §10 the full API sweep with a verdict per area |
| [feature-history.md](docs/feature-history.md) | **archive** — as-built records moved verbatim out of this file. Historical by design |
| [filesystem-bridge.md](docs/filesystem-bridge.md) | **current mechanism, untouched since the rename** — the v40 host-FS bridge is very much live (see the per-call opener fix, `142b350`) |
| [global-functions.md](docs/global-functions.md) | **current** — all five load-time global kinds |
| [host-query.md](docs/host-query.md) | **current** — incl. session-state inheritance + attached-catalog visibility (2026-07-30) |
| [inout-collector-mode.md](docs/inout-collector-mode.md) | **current mechanism, untouched since the rename** — the collector path is live (`verify_collector`) |
| [macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) | **current** — §1 global macros, §1.4 catalog-bound macros, §2 sqlgen |
| [multifile-delta.md](docs/multifile-delta.md) | ⚠ **STALE HEADER.** Says "Phase-A slices BUILDING"; slice 1a shipped as the standalone `fabricator_delta_mfr_scan` (+ its suite) and the PRODUCTION catalog read path never adopted it. Treat as a design record, not a description of the shipped read path |
| [native-delta-write.md](docs/native-delta-write.md) | ⚠ **PRE-DATES THE DEFAULTS FLIP (2026-07-29).** Its §2 table still says the default is engineered-wood everywhere, and it cites the `deltalake` alias the flip REMOVED. The mechanism description is sound; the defaults are not |
| [parallel-partitioned-read.md](docs/parallel-partitioned-read.md) | **design only, nothing built** |
| [plugin-system.md](docs/plugin-system.md) | **current** — default-context SPI; per-plugin ALC isolation deferred |
| [provider-extensibility.md](docs/provider-extensibility.md) | **current** — the self-describing-provider surfaces |
| [rowid-concepts.md](docs/rowid-concepts.md) | **current** — transient vs stable row identity |
| [rowid-dml-seam.md](docs/rowid-dml-seam.md) | **current** — the DML seam after the EW re-pin |
| [settings-architecture.md](docs/settings-architecture.md) | **current, refactor DONE** (settings v33/v34, ATTACH v37, secret fields v38) |
| [transaction-concurrency.md](docs/transaction-concurrency.md) | **current** — per-DuckDB-transaction provider connections (ABI v35) |
| [transactions.md](docs/transactions.md) | **current** — the three lazy levels, MARS, the one-writer rule |
| [variant-support.md](docs/variant-support.md) | **current** — six passes, Spark + kernel validated |
| [warehouse-support.md](docs/warehouse-support.md) | **current** — Fabric WH / Synapse / box profiles, slices 1–6 |
