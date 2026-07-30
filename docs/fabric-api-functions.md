# Fabric REST API custom functions — design + as-built (P0 shipped, P1/P2 designed)

> Status 2026-07-30: **P0 + P2-introspection BUILT and validated LIVE; P1 jobs / notebook-RUN still
> design (blocked — see §9b).** §9c is the as-built record; §4's table marks what
> is shipped. The research below is
> file:line-verified against the working tree and the pinned SDK binary; treat the REST shapes as
> verified against learn.microsoft.com on 2026-07-30 and re-check anything marked *spike*.
> REST reference root: <https://learn.microsoft.com/en-us/rest/api/fabric/articles/> (LRO handling,
> identity/SP support, scopes and throttling live under this root; per-endpoint pages under
> `/rest/api/fabric/<service>/…`).

## 1. Why

Two concrete pains drive this, both from dbt flows that mix the Delta provider with T-SQL:

1. **T-SQL endpoint discovery lag.** A lakehouse table created through our Delta provider takes an
   unpredictable time (seconds to minutes) to appear in the lakehouse's SQL analytics endpoint —
   Fabric's auto-detection is asynchronous. A dbt DAG whose downstream model reads the new table via
   T-SQL (our SqlServer provider on the endpoint) races that detection. Fabric has a **manual
   refresh API** (`POST …/sqlEndpoints/{id}/refreshMetadata`) that runs the sync synchronously and
   reports per-table status — exactly the hook a dbt flow needs between "Delta write committed" and
   "T-SQL model runs".
2. **Shortcut management.** Creating/dropping/re-pointing OneLake shortcuts is API-only today —
   Microsoft ships no T-SQL for it. We want `SELECT lake.dbo.fabric_create_shortcut(…)` instead of a
   Python notebook. (Intercepting a hypothetical `CREATE SHORTCUT` T-SQL through `fabricator_exec`
   and translating it ourselves is explicitly a **later stage** — see §8.)

3. **Parameterized notebook runs** (user-elevated 2026-07-30): dbt hooks that submit a Fabric
   notebook with parameters, block on completion, and branch on the notebook's
   `mssparkutils.notebook.exit(…)` value — plus inspection (list notebooks, read a notebook's
   parameters cell) to make those hooks discoverable/validatable.

Beyond those, the platform API surface (generic jobs, table maintenance with V-Order, workspace/item
introspection) maps naturally onto our custom-function machinery. This doc inventories what is worth
wrapping, decides the shapes (scalar vs table, parameter and output encoding), and orders the work.

## 2. What already exists (research findings, verified)

**The SDK is already shipped.** `Microsoft.Fabric.Api` **2.14.0** is a `Fabricator.Bridge`
PackageReference and is used in production code today:
[FabricLakehouse.cs](../dotnet/Fabricator.Bridge/FabricLakehouse.cs) calls `GetLakehouse` (schema-enabled
flag + `sqlEndpointProperties`), `WorkspacesClient.ListWorkspaces` and `ItemsClient.ListItems`
(name→GUID resolution), and `TablesClient.ListTables` (the Unity-Catalog alternative is raw HTTP in the
same file). Dependencies are lean (netstandard2.0; Azure.Core + DiagnosticSource + IdentityModel.Tokens.Jwt
— all already in our closure). **The pinned 2.14.0 dll already contains everything P0/P1 needs** —
byte-probed with positive (`GetLakehouse` 2) and negative (nonsense string 0) controls:
`RefreshSqlEndpointMetadata` 2, `TableSyncStatus` 1, `CreateShortcut`/`DeleteShortcut`/`ListShortcuts`/
`ResetShortcutCache` 2 each, `ShortcutConflictPolicy` + `CreateOrOverwrite` present,
`RunOnDemandItemJob`/`GetItemJobInstance`/`RunOnDemandTableMaintenance` 2 each, `GetOperationState` 2.
No version bump required; exact client-class names resolve at the spike (they follow the
`Microsoft.Fabric.Api.<Service>.ItemsClient` pattern our code already uses).

**Auth plumbing exists.** [FabricCredentialResolver.cs](../dotnet/Fabricator.Bridge/FabricCredentialResolver.cs)
turns secret fields into a `TokenCredential` (`access_token` → static; SP → `ClientSecretCredential`;
`managed_identity`; else the ambient chain incl.
[FabricNotebookCredential.cs](../dotnet/Fabricator.Bridge/FabricNotebookCredential.cs), which mints
per-resource tokens from the trident token service on Fabric compute). The SDK supplies its own
audience internally — clients are constructed with just the credential — and `FabricNotebookCredential`
handles arbitrary scopes by stripping `/.default`, so **no new scope constant is needed for SDK calls**
(add `FabricScope = "https://api.fabric.microsoft.com/.default"` only if a raw-HTTP endpoint is ever
needed; the raw-HTTP template is `FabricLakehouse.ListTablesViaUnityCatalogAsync` and
`scratchpad/fabricnb`'s LRO/job-polling loops).

**The OneLake attach already knows everything a catalog-bound function needs.**
[DeltaCatalog.cs](../dotnet/Fabricator.Bridge/DeltaCatalog.cs) holds `_root` (the attach URI) and
`_fabricCredential` (the resolved `TokenCredential`, published per-call to an AsyncLocal), and
`FabricLakehouse.ParseOneLake(root)` extracts `(workspace, lakehouse)` — names or GUIDs, with
`ResolveLakehouseId` handling both. So on
`ATTACH 'abfss://Test@onelake…/LH.Lakehouse/Tables' (… SECRET fabric_sp)` a catalog-bound function can
default **workspace, lakehouse AND credential** from the attach: `lake.dbo.fabric_refresh_sql_endpoint()`
with zero arguments.

**The function machinery fits, with two gaps.**
- Global functions: `IBackend.Global*` unioned across backends in
  [GlobalFunctions.cs](../dotnet/Fabricator.Bridge/GlobalFunctions.cs); the Bridge-resident
  [DeltaGlobalTableFunction.cs](../dotnet/Fabricator.Bridge/DeltaGlobalTableFunction.cs)
  (`fabricator_delta_scan`) is the precedent for declaring a pure-Bridge function through the
  always-present SqlServer backend's [CustomFunctions.cs](../dotnet/Fabricator.SqlServer/CustomFunctions.cs)
  arrays. Table functions get **args at Bind** (arg-dependent output schema is proven by `cf_columns`).
- **Gap 1 — the Delta catalog hosts no functions.** All seven function ABI members in `DeltaCatalog`
  are `throw NoFunctions()` one-liners, and the FUNCTIONS metadata kind falls to the 1-column empty
  fallback. Hosting catalog-bound functions there needs: a registry, a real in-memory ≥3-column
  FUNCTIONS stream (`schema_name, name, kind`, built like `CatalogMacroMetadata.Stream` — Delta has no
  SQL engine to `UNION ALL` through), and five member implementations — each mostly a one-liner over
  existing Bridge pieces (`GlobalFunctions.ExecuteScalar`, `BindingBoundTable`,
  `ScalarFunctionMetadata.TagVolatility`, `fn.Bind(args).OutputSchema`). Schema names expand across
  `SchemaNames()` exactly like `ExpandCatalogMacroSchemas` does for catalog macros (a declared schema
  must be discovered or C++ drops the function). **No C++ or ABI change** — the catalog machinery is
  provider-agnostic. Leave `InOutBind`/`AggOpen`/`GenerateTableSql` throwing.
- **Gap 2 — custom scalar/table functions are positional-only.** Named parameters with defaults exist
  for sqlgen (`ISqlTableFunction.NamedParameters`, the `fabricator.named` tag), in-out/collector, and
  discovered procs — but the plain table/scalar registration ignores the tags
  (`src/catalog/fabricator_schema_entry.cpp` builds `TableFunction(positional)` for both catalog and
  global paths). Extending named params to custom table functions is a contained C++ change with two
  in-repo templates (the proc named-arg marshaling + the sqlgen tag split) — scheduled as its own
  slice (§7), NOT a P0 dependency.

**Plugin SPI is not needed.** The SDK is already a Bridge dependency; a plugin would only complicate
the single-file distribution. (A plugin could still ship third-party Fabric functions later — the SPI
supports global functions today.)

## 3. Design decisions

**D1 — two surfaces, catalog-bound first.** The primary surface is **catalog-bound on the Delta
provider when the attach root is OneLake** (`IsOneLake(_root)`; not registered otherwise): the attach
already carries the credential + workspace/item, which is precisely the dbt shape — dbt runs on a
build agent, NOT on Fabric compute, and its credential is the attach's `SECRET fabric_sp`. Global
`fabric_*` variants (explicit `workspace`/`item` args) run on the **ambient chain** only — zero-config
on Fabric notebooks, env-credential on dev boxes — because a global function has no path to a DuckDB
secret (C++ resolves secrets; the host-FS opener covers storage, not REST). Error text for an
unauthenticated global points at the catalog-bound form. SqlServer-catalog binding (Fabric Warehouse
attaches): deferred — its catalog holds a connstr, not a `TokenCredential`.

**D2 — scalar vs table.** Anything returning a list or per-item statuses is a **table function**
(refresh statuses, shortcuts, workspaces, items, jobs). Single-action calls with one small result are
**scalars** (create/drop shortcut, cancel job) returning a typed value (`VARCHAR` path / `BOOLEAN`).
Long-running actions surface as **table functions that block until done** by default (refresh, run-job)
— a dbt hook must not return before the work is finished — returning the final state rows; polling
honors the service's `Retry-After` and the binding's `CancellationToken`.

**D3 — parameters: flat, positional, GUID-or-name; JSON escape hatch for unions.**
- Positional args only (Gap 2). Design signatures so the common case has few args; where an options
  knob is genuinely needed, ship a sibling `_ex` function with the full arity (trailing args nullable,
  `NULL` = default) rather than one mega-signature. The named-param slice later dissolves the `_ex`
  split. Global macros with `:=` defaults are an available alternative for globals (a macro can wrap a
  global impl function) but NOT for catalog-bound ones (a macro body cannot qualify its own catalog —
  the §1.4 namespacing finding), so we skip the asymmetry and use `_ex` uniformly.
- `workspace`/`item` args accept **GUID or display name** (existing `FabricLakehouse` resolution;
  cache resolutions per catalog/function instance — ListWorkspaces is throttled).
- **Nested INPUT (the shortcut target union, 9 member types): flatten the dominant case, JSON for the
  rest.** `fabric_create_shortcut(path, name, target_workspace, target_item, target_path)` covers the
  OneLake-internal target (no connectionId needed). All external targets (adlsGen2/amazonS3/
  azureBlobStorage/gcs/s3Compatible/dataverse/oneDriveSharePoint — each needing a pre-provisioned
  `connectionId` anyway) go through `fabric_create_shortcut_json(path, name, target_json)` where
  `target_json` is the REST `target` object verbatim. One flattened function per union member would be
  8 functions × 4-5 args of API we don't use; a DuckDB STRUCT literal param would marshal fine over
  Arrow but ties our signature to a union Microsoft extends over time ("Additional types may be added")
  — the JSON passthrough is deliberately evolution-proof.

**D4 — nested OUTPUT: typed flat columns for what people filter on; raw JSON for the polymorphic
remainder; no STRUCT wrapping.** (The user's explicit question — the rule:)
- Lists → **rows** of a table function, obviously.
- Stable scalar fields (ids, names, status enums, timestamps, error code/message) → **typed columns**,
  nested one level flattened with a prefix (`target_workspace_id`, `error_message`). This is the shape
  dbt/jinja, `WHERE status = 'Failure'`, and CSV export all want.
- Genuinely polymorphic parts (the shortcut `target` union) → a **`target_type` column + flattened
  common fields (`target_location`, `target_subpath`, `target_connection_id`, and the OneLake trio) +
  one full-fidelity `target_json` VARCHAR** carrying the original object for everything else.
- **Rejected: JSON-only outputs** (untyped; pushes parsing onto every consumer; defeats the point of a
  typed function). **Rejected: STRUCT wrapping as the default** — `t.target.oneLake.workspaceId` is
  clunky in jinja, and API evolution is harsher on structs: adding a COLUMN is additive for `SELECT *`
  consumers, while adding a struct FIELD changes the column's type and breaks bound views. Structs stay
  available for small closed records if a case ever clearly wants one; none of the P0–P2 set does.

**D5 — errors.** Fabric error codes are strings, so the C# exception message is formatted
`"<errorCode>: <message> (requestId <id>)"` — same reading experience as the SqlClient `Number`
prefixing, without touching `FormatError`. 429s ride the SDK's Azure.Core retry pipeline
(`Retry-After`-aware); `isRetriable` errors are NOT auto-retried beyond that (a dbt hook should fail
loudly, not hang).

**D6 — side effects are immediate and non-transactional.** An API call fires when the function
executes; a surrounding DuckDB `BEGIN`/`ROLLBACK` does not undo it (same class as Delta DROP/OPTIMIZE).
**The dbt consequence is sharp and must be documented loudly:** our Delta writes buffer their LOG
COMMIT until DuckDB COMMIT, so a refresh in a default (in-transaction) post-hook runs BEFORE the
table's Delta commit lands and syncs nothing. The refresh hook must be
`{{ config(post_hook={'sql': '…', 'transaction': false}) }}` or an `on-run-end` hook. This is the #1
usage trap of the entire feature.

**D7 — cancellation.** Table bindings receive a `CancellationToken` — wire it through every SDK call
and poll loop (poll loops also respect `Retry-After`). Scalars have no token, so **no scalar ever
polls**; anything long-running is a table function (D2).

**D8 — SDK, not raw REST.** Already shipped, surface verified, LRO/retry handled. Raw-HTTP fallback
pattern exists in-repo should an endpoint be missing from 2.14.0 (then prefer bumping the package —
2.18.0 is current, same dependency closure). AOT: [aot-bridge.md](aot-bridge.md) already lists
`Microsoft.Fabric.Api` as a verify item; Azure.Core-generated models serialize via hand-generated
`IUtf8JsonSerializable`, so the expectation is AOT-clean, but the AOT SKU gate re-verifies.

## 4. Prioritized inventory

Naming: `fabric_*` (the provider-scoped naming rule — these name the PLATFORM, like `dax_*`/`delta_*`).
Catalog-bound versions resolve as `db.<schema>.fabric_*` (registered in every discovered schema, the
macro precedent); globals as bare `fabric_*` with explicit `workspace`/`item` args prepended.
This table is the CURATED set; the exhaustive area-by-area sweep of the whole API surface — including
everything deliberately SKIPPED and why — is **§10**, so the curation cannot silently miss an area.

| P | function | kind | wraps (REST) | notes |
|---|---|---|---|---|
| **P0 ✅** | `fabric_refresh_sql_endpoint` | table, blocking | `POST …/sqlEndpoints/{id}/refreshMetadata` (LRO) | **SHIPPED.** THE dbt unblocker; zero-arg + `_ex(recreate, timeout_seconds)`. ⚠ `NotRun` = already in sync, NOT failure (§9c) |
| **P0 ✅** | `fabric_create_shortcut` | scalar → VARCHAR path | `POST …/items/{id}/shortcuts` (policy `Abort`) | OneLake target flattened; name-or-GUID target workspace/item; `NULL` target_workspace = same workspace |
| **P0 ✅** | `fabric_alter_shortcut` | scalar → VARCHAR path | same, policy `OverwriteOnly` | SQL-ish semantics: fails if absent. `CreateOrOverwrite` via `fabric_create_shortcut_ex(…, conflict_policy)` |
| **P0 ✅** | `fabric_create_shortcut_json` | scalar → VARCHAR path | same | full 9-member target union as verbatim JSON (external targets need a `connectionId` anyway) |
| **P0 ✅** | `fabric_drop_shortcut` | scalar → BOOLEAN | `DELETE …/shortcuts/{path}/{name}` | idempotent via `if_exists`; bare call 404s (verified) | |
| **P0 ✅** | `fabric_list_shortcuts` | table | `GET …/items/{id}/shortcuts` (paged) | flattened + `target_json` (the D4 showcase); optional parent-path filter via `_ex` |
| **P0** | `fabric_run_notebook` | table, blocking | `POST …/items/{id}/jobs/RunNotebook/instances` + instance polling | **user-elevated (2026-07-30)**: parameterized notebook runs from dbt hooks, returning final status **+ `exit_value`** (`mssparkutils.notebook.exit`) for conditional orchestration. SP execution already proven live on this tenant by `scratchpad/fabricnb`. See §5 |
| P1↑ (partly ✅) | `fabric_items_ex('Notebook')` / `fabric_notebook_definition` / **`fabric_notebook_parameters` BUILT** | table | `GET …/items?type=Notebook`; `POST …/notebooks/{id}/getDefinition` (LRO) | **notebook inspection, user-elevated above the rest of P1**: list; raw definition parts (ipynb/fabricGitSource, base64); and a convenience that parses the `parameters`-tagged cell into (name, default) rows — heuristic by nature (regex over `name = literal` lines), flagged as such |
| P1 | `fabric_run_job` | table, blocking by default | `POST …/items/{id}/jobs/{jobType}/instances` + instance polling | the generic engine `fabric_run_notebook` is built on (any jobType, e.g. `Pipeline`); `execution_data_json` passthrough; `wait_seconds` 0 = fire-and-return |
| P1 | `fabric_job_status` / `fabric_job_instances` / `fabric_cancel_job` | table / table / scalar | `GET` instance / `GET` list / `POST cancel` | status enum: NotStarted/InProgress/Completed/Failed/Cancelled/Deduped; `fabric_job_instances(item)` is run-history inspection |
| P1 | `fabric_table_maintenance` | table, blocking | `POST …/lakehouses/{id}/jobs/tableMaintenance/instances` (preview) | **V-Order** optimize + zOrderBy + vacuum + purge DVs — the recluster our own OPTIMIZE cannot do (V-Order is proprietary); complementary, not competing |
| P1 | `fabric_reset_shortcut_cache` | scalar, blocking | `POST …/workspaces/{id}/onelake/resetShortcutCache` (LRO) | after re-pointing shortcuts |
| P2 ✅ | `fabric_workspaces` | table | `GET /workspaces` (paged) | **BUILT** — id, name, type, capacity_id, description |
| P2 ✅ | `fabric_items` / `_ex(item_type)` | table | `GET …/items?type=` (paged) | **BUILT** — id, name, type, description |
| P2 ✅ | `fabric_lakehouses` / `fabric_warehouses` | table | `GET …/lakehouses` / `GET …/warehouses` + properties | incl. `sql_endpoint_id`, `sql_endpoint_connection_string` / warehouse `connection_string`, `provisioning_status` — feeds a subsequent T-SQL ATTACH |
| P2 ✅ | `fabric_connections` | table | `GET /connections` (paged) | external shortcut targets REQUIRE a pre-provisioned `connectionId`; listing (id, name, type, path) from SQL closes the `fabric_create_shortcut_json` loop. LIST only — connection CRUD carries credentials and stays out (§10) |
| P2 | `fabric_lakehouse_tables` | table | `GET …/lakehouses/{id}/tables` (paged) | overlaps our own discovery; cheap and occasionally useful cross-workspace |
| P2 | `fabric_operation_status` | table (1 row) | `GET /operations/{id}` | the generic LRO peek for `wait_seconds => 0` flows |
| P3 | workspace/item CRUD, capacity assign, git (status/commit/update), deployment pipelines, connections, external data shares, OneLake data-access roles, `loadTables` | — | — | admin surface; demand-driven |
| P3 | semantic-model refresh | — | Job Scheduler vs Power BI enhanced-refresh vs XMLA/TMSL through the DAX provider | needs its own decision — the DAX provider may already be the better carrier (no new API host) |

## 5. P0 function specs

**`fabric_refresh_sql_endpoint`** — catalog-bound `lake.dbo.fabric_refresh_sql_endpoint()` (zero-arg;
workspace/lakehouse/credential from the attach) and global
`fabric_refresh_sql_endpoint(workspace, item)`. Resolution chain: workspace name→id, lakehouse
name→id (existing helpers), `GetLakehouse → sqlEndpointProperties.id`, then `refreshMetadata`; block
on the LRO. `_ex` adds `(recreate BOOLEAN, timeout_s INT)` → body `recreateTables` + `timeout`
(service default 15 min; enum'd `timeUnit`). Output columns:

```
table_name VARCHAR, status VARCHAR,            -- Success | Failure | NotRun (open enum)
start_time TIMESTAMP, end_time TIMESTAMP, last_successful_sync TIMESTAMP,
error_code VARCHAR, error_message VARCHAR      -- NULL unless status = 'Failure'
```

dbt usage (the trap from D6 spelled out):

```yaml
# model post-hook — MUST be non-transactional: the Delta log commit lands at DuckDB COMMIT,
# and an in-transaction hook would refresh BEFORE the table exists in the log.
post_hook:
  - sql: "SELECT count(*) FROM lake.dbo.fabric_refresh_sql_endpoint()"
    transaction: false
```

**Shortcuts** (catalog-bound shown; globals prepend `workspace, item`):

```sql
SELECT lake.dbo.fabric_create_shortcut('Tables', 'ref_orders', 'OtherWS', 'OtherLH', 'Tables/orders');
SELECT lake.dbo.fabric_alter_shortcut ('Tables', 'ref_orders', 'OtherWS', 'OtherLH', 'Tables/orders_v2');
SELECT lake.dbo.fabric_create_shortcut_json('Files/landing', 'partner',
       '{"adlsGen2": {"location": "https://acct.dfs.core.windows.net", "subpath": "/c/data", "connectionId": "…"}}');
SELECT lake.dbo.fabric_drop_shortcut('Tables', 'ref_orders');
SELECT * FROM lake.dbo.fabric_list_shortcuts();
```

Create/alter return the created shortcut's full path (from the response `Location`/body). Conflict
policies map: create = `Abort`, alter = `OverwriteOnly`, `fabric_create_shortcut_ex(…, conflict_policy)`
exposes all four (`CreateOrOverwrite`, `GenerateUniqueName`). `fabric_list_shortcuts` columns:

```
path VARCHAR, name VARCHAR, target_type VARCHAR,
target_workspace_id VARCHAR, target_item_id VARCHAR, target_path VARCHAR,   -- OneLake targets
target_location VARCHAR, target_subpath VARCHAR, target_connection_id VARCHAR,  -- external targets
target_json VARCHAR                                                          -- full fidelity, always set
```

A shortcut pointing at a Delta table only becomes visible to OUR catalog after
`fabricator_refresh_cache('lake')` (discovery is cached) — document the pairing next to the function.

**`fabric_run_notebook`** — catalog-bound `lake.dbo.fabric_run_notebook(notebook, params_json)` and
global `fabric_run_notebook(workspace, notebook, params_json)`;
`_ex(…, config_json, wait_seconds)`. Blocking by default (D2): submit → poll the job instance
honoring `Retry-After` + the binding's `CancellationToken` → return ONE row of final state.
`wait_seconds => 0` (via `_ex`) returns the accepted instance immediately for
`fabric_job_status` polling. Output columns:

```
job_instance_id VARCHAR, status VARCHAR,       -- Completed | Failed | Cancelled | Deduped | …
start_time TIMESTAMP, end_time TIMESTAMP,
exit_value VARCHAR,                            -- what the notebook passed to mssparkutils.notebook.exit()
error_code VARCHAR, error_message VARCHAR      -- from failureReason when status = 'Failed'
```

- **Parameters** ride the NOTEBOOK-specific `executionData.parameters` map (name →
  `{value, type}`, types `string|int|float|bool`) — NOT the top-level generic `parameters` array
  (which many item types reject with `FeatureNotAvailable`). `params_json` is a plain JSON object;
  we infer the Fabric type from the JSON value (string→`string`, integer→`int`, other number→
  `float`, boolean→`bool`), and a verbose `{"value": …, "type": "…"}` member is passed through for
  explicit control. The notebook needs a `parameters`-tagged cell (papermill convention — Fabric
  injects an override cell after it).
- **`config_json`** passes `executionData.configuration` verbatim (`conf`, `environment`,
  `defaultLakehouse`, `useStarterPool`, `useWorkspacePool`; session-reuse tagging — verify the exact
  key at the spike). **Catalog-bound default: `defaultLakehouse` = the attached lakehouse** when the
  caller doesn't set one — the notebook runs against the same lakehouse the dbt flow is writing.
- **`exit_value` is the orchestration payoff**: a notebook ends with
  `mssparkutils.notebook.exit("<string, e.g. JSON>")` and the value comes back on the job-instance
  GET, so a dbt hook can assert on it in SQL (`WHERE exit_value = 'success'` fails the hook loudly
  otherwise). The docs' sample retrieves it with a `?beta=true` query flag — if the 2.14.0 SDK model
  lacks the field, read the instance via the in-repo raw-HTTP pattern (spike decides).
- `Deduped` means an instance of the same job type was already running and this submission was
  skipped — surface it as-is; a dbt hook should treat it explicitly (usually as failure).
- SP support: documented for execute/monitor/cancel AND already exercised live on this tenant —
  `scratchpad/fabricnb` runs `RunNotebook` + polls as the SP (notebook CREATION was the 403'd one).

## 6. Architecture

New Bridge folder `FabricApi/` under `dotnet/Fabricator.Bridge` (namespace `Fabricator.Bridge`, like
the Abstractions convention):

- **`FabricApiClient`** — thin wrapper owning: client construction from a `TokenCredential`; the
  GUID-or-name resolution cache (workspace, item, sql-endpoint id; per-instance, since throttling is
  per-principal); the LRO/poll helper (Retry-After + `CancellationToken`); error normalization (D5).
  Everything below it is the SDK. Always use async SDK calls blocked at the wrapper edge — the
  sync-over-async convention, and `FabricNotebookCredential`'s "async transport only under hostfxr"
  note applies to any HttpClient here too.
- **Function classes** — each implements `ITableFunction`/`IScalarFunction` plus the `ICatalog*`
  variant; the catalog-bound instances are constructed WITH the owning `DeltaCatalog`'s credential +
  parsed workspace/item, the globals with the ambient chain. Bindings that ignore pushdown dispose
  `scan.FilterValues` in a plain method before delegating to an async iterator (the documented macOS
  use-after-free pattern on `StaticTableFunction`).
- **Declaration** — globals via the SqlServer backend's `CustomFunctions.Global*` arrays (the
  `fabricator_delta_scan` precedent); catalog-bound via a new Delta-provider registry consumed by the
  new `DeltaCatalog` function hosting (§2 Gap 1). Registered on the OneLake-rooted Delta catalog only;
  both provider spellings (`delta`, `engineeredwooddelta`) share `DeltaCatalog`, so both get them. The
  delta-rs provider is out of scope (its catalog also throws; secondary provider).
- **Logging** — a `Fabricator.Fabric` ILogger category: every call logs op + workspace/item + outcome
  at Debug, mutations (shortcut create/drop, job start) at Information. Never log tokens.

## 7. Implementation plan

Slices land independently, tests green per slice. Estimated shape, not a schedule:

1. **Spike (live, ~half day).** Console probe against the real tenant (SP from the gitignored
   `dax_secret.sql`): resolve exact 2.14.0 client/method names; run `refreshMetadata` + shortcut
   create/list/drop + `resetShortcutCache` as the SP; measure refresh latency on `LH`. **Explicitly
   verify SP permission reality vs docs** — this tenant already taught us docs lie about SP support
   (notebook creation 403 `FeatureNotAvailable` despite documented support). Go/no-go per endpoint;
   record findings here.
2. **Delta catalog function hosting (hermetic, the enabling refactor).** Registry + in-memory
   FUNCTIONS stream + five `DeltaCatalog` member implementations (§2 Gap 1). Gated by a new hermetic
   suite with demo functions on a local Delta attach — no Fabric involved; it tests the HOSTING. <!-- check-docs:ignore (suite lands with the slice) -->
   Reuses `GlobalFunctions.ExecuteScalar`, `BindingBoundTable`, `TagVolatility`, the macro
   schema-expansion pattern. No C++/ABI change; loadable rebuild not required.
3. **P0 functions.** `FabricApiClient` + refresh + the shortcut five + `fabric_run_notebook`
   (build the generic job engine — submit/poll/cancel plumbing — here; only the notebook sugar is
   exposed in this slice), catalog-bound + global. Tests:
   dotnet unit tests for arg validation/row mapping (mocked pipeline), plus a live-gated manual suite
   (env-gated like `verify_dax`; it mutates a real workspace, so it stays out of CI tiers). dbt
   validation on the lakehouse target: post-hook `transaction: false` refresh → downstream T-SQL model
   sees the table. **README + CLAUDE.md in the same commit** (standing rule) incl. the D6 trap.
4. **P1.** Notebook inspection (`fabric_notebooks`, `fabric_notebook_definition`,
   `fabric_notebook_parameters` — user-elevated first), then the generic job surface
   (`fabric_run_job`/`fabric_job_status`/`fabric_job_instances`/`fabric_cancel_job` — thin exposure
   of the slice-3 engine), `fabric_table_maintenance`, `fabric_reset_shortcut_cache`.
5. **P2.** Introspection set (workspaces/items/lakehouses incl. endpoint connstr, operation status).
6. **Named-parameter slice (UX, independent).** Extend the `fabricator.named` machinery to custom
   table/scalar functions (C++ registration + args marshaling; templates: the proc named-arg path and
   the sqlgen tag split). Dissolves the `_ex` siblings into `recreate := true`-style calls. Benefits
   every custom function, not just these.

## 8. Deferred / out of scope (recorded so they aren't re-litigated)

- **T-SQL DDL interception** (`CREATE SHORTCUT …` parsed out of `fabricator_exec` and translated to
  API calls) — the user's explicit "later stage". Note Microsoft may ship real T-SQL for shortcuts
  eventually; interception should mirror whatever grammar they choose, not invent one.
- **Semantic-model refresh** — carrier decision pending (Job Scheduler vs Power BI enhanced refresh
  API (different host + audience) vs XMLA/TMSL through the existing DAX provider, which needs no new
  API surface at all).
- **SqlServer-catalog binding** of these functions (Fabric Warehouse attaches) — needs a
  `TokenCredential` path the mssql secret doesn't currently produce.
- **deltars provider** hosting; **per-function plugin packaging**; everything marked skip/P3 in the
  §10 sweep (each with its recorded reason — don't re-litigate without new demand).
- **Shared function-dispatch extraction** — the ~120-line `TryGetValue` dispatch block is now
  hand-copied in SqlServer/DAX (and would be a third copy in Delta); worth extracting to the Bridge
  when slice 2 touches it, but not a goal in itself.

## 9. Risks / verify-at-spike checklist

Rows marked **RESOLVED** were settled by the slice-1 spike (§9b); the rest still stand.

| risk | why it's real | probe |
|---|---|---|
| SP gated off tenant-side despite docs | **RESOLVED — and it BIT TWICE**: `ResetShortcutCache` 400 `PrincipalTypeNotSupported`, notebook CREATE 403 `FeatureNotAvailable`, both documented as SP-supported. All P0 data-path calls DO work as SP | done (§9b) |
| refresh latency makes a blocking hook painful | **RESOLVED (partly)**: 7.5 s on `LH` — a blocking hook is fine. Unknown on a large/cold endpoint; still worth surfacing elapsed time | re-measure after a bulk create |
| `refreshMetadata` semantics on schema-enabled lakehouses | statuses are `tableName`-keyed; schema qualification unclear | STILL OPEN — `LH` is schema-enabled but the run returned before new tables existed to inspect; re-check with a fresh table |
| throttling on name resolution | ListWorkspaces/ListItems are per-principal throttled | cache per instance; probe burst behavior |
| 2.14.0 model gaps vs current REST | e.g. newer conflict policies | **RESOLVED for P0/P1** — every needed client/method/policy is in 2.14.0; NO bump needed (and 2.18.0 would not add `ExitValue` either) |
| tableMaintenance is preview | may change shape | keep P1; pass-through `execution_data_json` in `_ex` as the stable escape hatch |
| `exitValue` retrieval | **RESOLVED — CONFIRMED ABSENT from the SDK in 2.14.0 AND 2.18.0.** Raw-HTTP instance GET with `?beta=true` is the only route (whether the field then appears is still unverified — needs the blocked notebook) | raw GET, once a spike notebook exists |
| notebook param typing | our JSON→`{value,type}` inference (int vs float) vs what the notebook cell expects; AND which of the two payload shapes RunNotebook honours | **BLOCKED on a human click** — SP cannot create the notebook; create `fabricator_api_spike` once in the portal, then `dotnet run live params` |
| session reuse key | exact configuration key for high-concurrency session tagging unconfirmed | probe; matters for hook latency (cold Spark session ≈ minutes, so P0 targets the jupyter/python kernel in the spike notebook) |
| `GetNotebookDefinition` is slow (20.5 s measured) | it is an LRO, so `fabric_notebook_definition`/`_parameters` are NOT cheap reads | document the cost; never call it per-row |
| AOT SKU | generated serializers expected AOT-clean, unverified | existing aot-bridge.md verify item |

## 9b. Slice-1 spike RESULTS — live against the tenant, 2026-07-30

Run via `scratchpad/fabricspike` (gitignored; `dotnet run reflect` is hermetic and dumps the SDK
surface, `dotnet run live` runs the P0 endpoints as the SP from `dax_secret.sql`). Workspace `Test`,
lakehouse `LH`. **Every P0 data-path endpoint works as the service principal**; two adjacent ones do
not, and both were predicted by the "docs lie about SP support" risk row.

**Verified working as SP** (with timings, on this tenant):

| call | result |
|---|---|
| `Core.Workspaces.ListWorkspaces()` / `Core.Items.ListItems(ws, type:)` | OK, ~0.1–0.8 s — name→GUID resolution |
| `Lakehouse.Items.GetLakehouse(ws, lh)` | OK 0.6 s → `Properties.SqlEndpointProperties{Id, ConnectionString, ProvisioningStatus=Success}` + `OneLakeTablesPath`/`OneLakeFilesPath`/`DefaultSchema` |
| `SQLEndpoint.Items.RefreshSqlEndpointMetadata(ws, epId, req?, ct, timeoutInMinutes=60)` | **OK 7.5 s**, returns `TableSyncStatuses.Value : IReadOnlyList<TableSyncStatus>` — already a BLOCKING LRO helper, no hand-rolled polling needed |
| `Core.OneLakeShortcuts.CreateShortcut(ws, item, req, policy?)` | OK 1.0 s |
| …same, no policy, name exists | **409 `EntityConflict`/`ShorcutsOperationNotAllowed`** ("operation set to abort") — confirms default = Abort, so `fabric_create_shortcut` gets create-semantics for free (note Microsoft's typo in the code, do not "fix" it in a test assertion) |
| …with `ShortcutConflictPolicy.OverwriteOnly`, re-pointed | OK 0.9 s — confirms `fabric_alter_shortcut` |
| `GetShortcut` / `ListShortcuts(ws, item, parentPath:)` | OK 0.4 s |
| `DeleteShortcut` | OK 0.7 s; **a second delete 404s** `EntityNotFound`/`ShortcutNotFound` |
| `Notebook.Items.ListNotebooks(ws)` | OK 0.1 s |
| `Notebook.Items.GetNotebookDefinition(ws, nb, format:"ipynb")` | OK but **20.5 s** — an LRO, NOT a cheap metadata read |

**Blocked for the service principal — both documented as SP-supported, both refused:**

- **`ResetShortcutCache` → 400 `PrincipalTypeNotSupported`.** So the P1 `fabric_reset_shortcut_cache`
  is **user-credential-only**; it must carry that caveat in its error text rather than looking broken.
- **Notebook item CREATION → 403 `FeatureNotAvailable`** (re-probed; CLAUDE.md's older finding still
  holds). `UpdateItemDefinition` IS allowed, so the pattern stays "create once in the portal, automate
  after". Consequence: the notebook **parameter-shape matrix is still unrun** — it needs a
  `fabricator_api_spike` notebook (a `parameters`-tagged cell + `notebookutils.notebook.exit`) created
  interactively once; `dotnet run live params` then fills it via UpdateItemDefinition and runs both
  candidate payloads. **This is the one P0 blocker left, and it needs a human click.**

**SDK shape findings that change the implementation** (all from `reflect`):

- **`Core.Models.OneLake`'s ctor is `(Guid itemId, Guid workspaceId, string path)` — ITEM FIRST.** The
  intuitive `(workspaceId, itemId, …)` compiles (both `Guid`) and silently points the shortcut at the
  wrong item. **Always construct it with named arguments.**
- `SqlEndpointProperties.Id` is a **`string`** while `RefreshSqlEndpointMetadata` takes a **`Guid`** —
  parse, don't assume (`?? Guid.Empty` does not even compile).
- `ListShortcuts` returns **`ShortcutTransformFlagged`**, not `Shortcut` (adds `IsShortcutTransform`,
  which came back null on every real row). It takes `parentPath` — that is the `_ex` filter.
- **Returned paths carry a LEADING SLASH** (`/Files/staging`) while `CreateShortcut` accepts
  `Files`. `fabric_list_shortcuts` must normalize, or round-tripping list→drop breaks.
- Real rows on `LH` are **`AdlsGen2`** targets, so the external-target flattening (D4) is exercised
  by day-one data, not hypothetical.
- `RunOnDemandItemJob` returns a **bare `Response`** — the job-instance id is only in the `Location`
  header. `ItemJobInstance.StartTimeUtc`/`EndTimeUtc` are **strings**; `Id`/`ItemId` are `Guid?`.
- **`ItemJobInstance` has NO `ExitValue` in 2.14.0 *or* 2.18.0** (both byte-probed). A package bump
  does not solve it ⇒ `exit_value` requires the raw instance GET with `?beta=true`. This makes
  `FabricApiClient` a hybrid (SDK + one raw call) by necessity, not by taste.
- `RunOnDemandItemJobRequest.ExecutionData` is `object`; the typed notebook models
  (`RunSparkNotebookExecutionData`, `RunJupyterNotebookExecutionData`) carry **compute configuration
  only** (`SparkNotebookComputeConfiguration`; `JupyterNotebookComputeConfiguration` has
  `DefaultLakehouse`/`AttachedEnvironment`) — **no parameters property**. So parameters ride either the
  legacy `executionData.parameters` map or the top-level `Parameters` list
  (`Parameter(name, ItemJobParameterType, value)`; types Text/Integer/Number/Boolean/DateTime/Guid/
  VariableReference/Automatic). Which one RunNotebook honours is exactly the blocked matrix above.
- Useful extras confirmed present: `SQLEndpoint.Items.GetConnectionString`,
  `Warehouse.Items.GetConnectionString` + `WarehouseProperties.ConnectionString`,
  `Lakehouse.Tables.ListTables` → `Table{Type,Name,Location,Format}`,
  `Core.LongRunningClient.GetOperationState(Guid)` → `OperationState{Status, PercentComplete, Error, …}`,
  and `FabricClient(TokenCredential, …)` — which is exactly what `FabricCredentialResolver` hands us.
- `FabricClient` exposes ~55 per-workload service properties; the ones we need are `Core`,
  `Lakehouse`, `SQLEndpoint` (that casing), `Notebook`, `Warehouse`.

## 9c. AS BUILT — slices 2 + 3 shipped (2026-07-30), P0 validated LIVE

**Status change: this is no longer design-only for P0.** What exists now:

| piece | where |
|---|---|
| `FunctionsMetadata` — the kind-6 stream built IN MEMORY | `dotnet/Fabricator.Bridge/FunctionsMetadata.cs` |
| `CatalogFunctionSet` — provider-agnostic registry + the five ABI members, `__all__` schema sentinel | `dotnet/Fabricator.Bridge/CatalogFunctionSet.cs` |
| `ArrowSchemaExport` — the empty-parameter-schema export Apache.Arrow cannot do | `dotnet/Fabricator.Bridge/ArrowSchemaExport.cs` |
| Delta catalog hosting (metadata kind + 5 members, OneLake-gated Fabric registration) | `dotnet/Fabricator.Bridge/DeltaCatalog.cs` |
| `fab_delta_info()` — attach diagnostics + the zero-arg canary | `dotnet/Fabricator.Bridge/DeltaCatalogInfoFunction.cs` |
| `FabricApiClient` / `FabricApiContext` — SDK wrapper, name→GUID cache, error normalization | `dotnet/Fabricator.Bridge/FabricApi/FabricApiClient.cs` |
| the P0 functions | `dotnet/Fabricator.Bridge/FabricApi/FabricApiFunctions.cs` |
| target union mapping + path/policy helpers | `dotnet/Fabricator.Bridge/FabricApi/FabricShortcutTarget.cs` |
| positional-arg readers | `dotnet/Fabricator.Bridge/FabricApi/FabricArgs.cs` |
| hermetic gate (21) | `test/verify_delta_catalog_functions.test` |

Shipped functions — **P0 (validated live)**: `fabric_refresh_sql_endpoint()` + `_ex(recreate,
timeout_seconds)`, `fabric_list_shortcuts()` + `_ex(parent_path)`, `fabric_create_shortcut`,
`fabric_alter_shortcut`, `fabric_create_shortcut_ex(…, conflict_policy)`, `fabric_create_shortcut_json`,
`fabric_drop_shortcut`. **P2 introspection**
(`FabricApi/FabricInspectFunctions.cs`, built after the P0 live run and **since exercised live too** — see
below): `fabric_workspaces()`,
`fabric_items()`/`_ex(item_type)`, `fabric_lakehouses()`, `fabric_warehouses()`, `fabric_connections()`,
and **`fabric_notebook_parameters(notebook)`** — the first piece of the elevated notebook work that needed
no blocked prerequisite.

Hermetic tier **62 runs / 5558 assertions green**; the whole P0 set exercised end-to-end against workspace
`Test` / lakehouse `LH` (create → list → alter → drop → drop-again → refresh, self-cleaning).

Why the introspection set is worth its weight: the WRITE functions need identifiers a user otherwise hunts
for in the portal — an external shortcut target needs a cloud connection's GUID (`fabric_connections`), a
T-SQL ATTACH needs the endpoint connection string (`fabric_lakehouses`/`fabric_warehouses`), and a
cross-workspace shortcut needs the target's name or id. `fabric_notebook_parameters` is explicitly
HEURISTIC and says so in its own output: Fabric follows the papermill convention (the override cell is
injected after the cell tagged `parameters`), so there is no declaration to read — only top-level
`name = literal` assignments to parse. Zero rows is a legitimate answer meaning "no tagged cell", which is
what `LH`'s existing notebook actually has. It is also NOT cheap: `GetNotebookDefinition` is an LRO, ~20 s
measured.

### THE ZERO-ARGUMENT PROBLEM AND ITS FIX — the most reusable thing here

A catalog-bound function that infers everything from its ATTACH wants to be called with **no arguments**.
That was **impossible**, for a reason nothing surfaced: **Apache.Arrow 23.0.0 cannot represent an empty
schema across the C data interface — in EITHER direction.** `CArrowSchemaExporter.ExportSchema(new
Schema(no fields))` and `ExportType(new StructType(no fields))` both throw
`ArgumentNullException(Parameter 'fields')`, and the importer fails the same way on a zero-column batch
(verified with a one-field positive control, so this is the library, not the harness).

The failure mode is why it stayed hidden: `GetOrCreateScalarFunction`/`GetOrCreateTableFunction` treat ANY
schema-fetch failure as "this discovered name is stale" and **silently erase the function**. So a
zero-argument function does not error — it simply never appears in the catalog. The only trace is a
Debug-level `GetFunctionParamSchema failed: Value cannot be null. (Parameter 'fields')` WARN, which
CLAUDE.md had already recorded and written off as benign. It was not benign; it was this.

Fixed in two halves, both required:
1. **Export** (`ArrowSchemaExport.Export`, used by `Bootstrap.GetFunctionParamSchema`): delegates to
   Apache.Arrow when there is ≥1 field, otherwise hand-builds the empty struct (`format="+s"`,
   `n_children=0`) through a layout mirror, since `CArrowSchema.release` is internal. Ownership follows
   the C data interface — the consumer's `release` frees the format string and NULLs itself.
2. **Import avoided entirely** (`fabricator_schema_entry.cpp`, the table-bind factory): for an
   argument-less function the host now passes **no args stream at all** rather than an empty one. `args`
   was already nullable by contract, so this is the contract's intended shape — no ABI bump.

**Zero-argument SCALAR functions remain impossible** and that is not worth fixing: a scalar's argument
batch is also how the host conveys the ROW COUNT, so "no columns, N rows" has nowhere to live. Any
zero-arg function must therefore be a TABLE function — which is why `fab_delta_info()` is one.

### Live findings that change how these functions must be USED

- **`NotRun` is the normal outcome of a refresh, and it does NOT mean failure.** On a lakehouse whose 19
  tables were already in sync, every row came back `status='NotRun'` with `error_code IS NULL` and a
  populated `last_successful_sync`. The enum means "the sync did not need to run". **A dbt hook asserting
  `status = 'Success'` would fail on a perfectly healthy refresh** — assert on
  `status <> 'Failure'`, or on `error_code IS NULL`. This is the single most likely way to misuse the
  headline function.
- **`table_name` is SCHEMA-QUALIFIED on a schema-enabled lakehouse** (`dbo.people-10m`) — this answers
  the open risk-table question. Do not join it to a bare table name.
- **Shortcut paths round-trip correctly now**: the service returns `Tables/dbo` / `Files` (and with a
  leading slash in the raw API), and `fabric_list_shortcuts` normalizes, so piping list → drop works.
- **Real targets are external**: `LH`'s existing shortcuts are `S3Compatible` and `AdlsGen2`, so the
  flattened `target_location`/`target_subpath` columns plus `target_json` carry day-one data — the D4
  output rule is exercised immediately, not hypothetically.
- **Every `table`-kind function also gets a synthetic `_each` sibling** (`fabric_refresh_sql_endpoint_each`
  …), because the host adds one unconditionally in `AddTableFunction` for the 4g per-row form. On this
  provider they are dead entries that error if called — pre-existing behaviour shared with SqlServer's
  custom table functions, not introduced here. Cosmetic noise in `duckdb_functions()`; suppressing it
  would need a host-side "no `_each`" registration variant.

### Shortcut metadata is EVENTUALLY CONSISTENT — a real trap for scripted create/drop cycles

Running the same create → alter → drop → drop-again sequence TWICE back-to-back produced a self-
contradictory second run: `create` failed `EntityConflict` ("shortcut with same name already exists") while
`alter` and `drop` in the SAME run failed with "shortcut … is not found". The first run had completed
successfully and its drop HAD taken effect (a subsequent list confirms only the lakehouse's four original
shortcuts). So the name was briefly still reserved from the delete's point of view while already gone from
the read path.

Consequences to design around, rather than retry blindly: a **re-create of a just-dropped name may
transiently conflict**, so an idempotent script should use `fabric_create_shortcut_ex(…,
'CreateOrOverwrite')` rather than drop-then-create; and a listing taken immediately after a mutation may
not reflect it. This is also what `ResetShortcutCache` exists for — which this SP cannot call
(`PrincipalTypeNotSupported`), so on a service principal the only remedy is to tolerate the delay.

### Introspection: live results, and one that needs care

Exercised against workspace `Test` (2026-07-30): `fabric_workspaces()` → 1 (this SP sees only `Test`),
`fabric_lakehouses()` → `LH` / `LH2` / `LH_no_schema` with endpoint status `Success` and a connection string
each, `fabric_warehouses()` → 1, `fabric_items_ex('Notebook')` → 3, `fabric_notebook_parameters` → 0 rows
(no tagged cell — the legitimate answer). `default_schema` came back **NULL for `LH_no_schema`** and `dbo`
for the other two, which matches what those lakehouses actually are, so the function is reading real state
rather than echoing defaults.

**`fabric_connections()` returned ZERO — and that is not a bug, nor is it "there are no connections".**
`LH` demonstrably HAS `AdlsGen2` and `S3Compatible` shortcuts, and every external shortcut target requires
a cloud connection, so connections certainly exist. Connections are **permissioned per identity** (they
carry their own role assignments), so a service principal sees only the ones it has a role on — here,
none; the ones behind those shortcuts belong to an interactive user. The call itself succeeded (a
well-formed empty result, no error, from the same client that listed workspaces and items in the same
session). Document it that way for users: an empty `fabric_connections()` means "none visible to THIS
identity", and creating an external shortcut as an SP requires granting that SP access to the connection.

### A C# trap worth carrying forward

`ShortcutConflictPolicy` (and every Azure-style *extensible enum*) has an **implicit conversion from
`string`**. So this compiles cleanly and throws at run time:

```csharp
var policy = isAlter ? ShortcutConflictPolicy.OverwriteOnly : null;   // WRONG
```

The ternary infers **`string`** (because `null` is a valid `string` and the implicit conversion exists),
then converts back via `op_Implicit(null)` → `ArgumentNullException("value")` from inside the SDK, with a
message that names nothing recognizable. Always annotate: `(ShortcutConflictPolicy?)null`.

Finding it needed a stack trace, which the ABI does not carry — the bridge's sink renders only exception
type + message. Both `FabricApiClient.Wrap` and `FabricApiFunctions.Guarded` now append `StackTrace` for
**unexpected** exceptions (ours already name their cause and pass through untouched). Worth keeping: a
framework `ArgumentNullException` crossing this boundary is otherwise unlocatable.

## 10. The full API sweep — every area, with a verdict

The complete Fabric REST surface (Core services + workload APIs), each with implement/defer/skip and
the reason. **Every group below was presence-verified in the pinned 2.14.0 dll** (namespace/operation
string probe with positive + negative controls, 2026-07-30) — so nothing here depends on a package
bump. Two client-class names did not match first-guess casing (`SQLEndpoint` namespace, the LRO
client); the *operations* are verified present, exact names resolve at the spike.

Standing exclusion rules, applied throughout: (1) **anything carrying credentials or security policy
is not wrapped for WRITE** — a SQL function that mutates security or stores connection secrets is a
footgun in query text and logs; (2) **authoring/deployment surfaces stay with deployment tooling**
(git, fabric-cicd, pipelines) — base64 definition parts are hostile in SQL and the blast radius is
wrong; (3) **tenant-admin APIs are out entirely** — different consent model, different audience.

### Core services

| area | operations (condensed) | verdict |
|---|---|---|
| Workspaces | list/get; create/update/delete; role assignments; assign to capacity | **P2 list/get** (`fabric_workspaces`). CRUD + roles + capacity assign: **skip** (rules 1+2 — IaC/portal territory) |
| Capacities | list | **P3** — only feeds the capacity-assign flow we skip |
| Items (generic) | list/get; CRUD; get/updateDefinition; item connections | **P2 list/get** (`fabric_items`). Definition GET: **P1 for notebooks only** (§4). Generic CRUD/updateDefinition: **skip** (rule 2; `scratchpad/fabricnb` proves updateDefinition works SP-driven if ever needed) |
| Job Scheduler | run on demand / cancel / get instance / list instances | **P0/P1** (§4/§5) |
| Job Scheduler — item schedules | CRUD of cron/daily schedules | **skip for now** — in a dbt flow, dbt IS the scheduler; revisit only on demand |
| Long Running Operations | get state / get result | **P2** (`fabric_operation_status`) — the generic peek for `wait_seconds => 0` flows |
| OneLake Shortcuts | create / get / list / delete / reset cache | **P0/P1** (§4/§5) |
| OneLake Data Access Security | list / create-or-update roles | read: **P3**. write: **skip** (rule 1 — folder-security policy from SQL) |
| External Data Shares | create / list / revoke | **skip** — cross-tenant sharing is an admin/governance act, not a data-flow step |
| Connections | list / get; CRUD; supported types | **P2 LIST** (`fabric_connections` — the `connectionId` feeder for external shortcut targets). CRUD: **skip** (rule 1 — connection credentials) |
| Deployment Pipelines | list / stages / deploy | **P3 demand-driven** (rule 2) |
| Git | status / commit / update-from-git / connect | **P3 demand-driven** (rule 2) — `fabric_git_status`/`fabric_git_update` would serve promotion flows if users ask |
| Gateways | list / CRUD / members | **skip** — network infra admin |
| Folders | CRUD | **skip** — workspace sub-folders, a portal-UI organization feature (items carry a `folderId`); invisible to OneLake paths, ATTACH and discovery. If ever wanted: a `folder_id` column on `fabric_items`, not a function |
| Tags | list; apply/unapply on items | **skip** — centrally-defined governance labels for portal filtering/reporting (NOT Purview sensitivity labels); applying governance metadata from SQL is rule 1 territory |
| Managed Private Endpoints, workspace private links | — | **skip** — tenant/network admin (rule 3-adjacent) |
| Admin.* (tenant inventory, users, labels, domains) | — | **skip** (rule 3) — requires tenant-admin consent; a data extension must not carry that blast radius |

### Workload APIs

| area | operations (condensed) | verdict |
|---|---|---|
| SQLEndpoint | refreshMetadata | **P0** — the headline (§5) |
| Lakehouse | list/get (incl. `sqlEndpointProperties`) | **P2** (`fabric_lakehouses`) |
| Lakehouse — tables | list | **P2** (`fabric_lakehouse_tables`) — overlaps our own discovery; cheap, useful cross-workspace |
| Lakehouse — loadTable | file→Delta load (Spark-side) | **P3** — our own `COPY`/CTAS already does file→Delta natively and transactionally; the one thing loadTable adds is Spark-side V-Order on ingest, which `fabric_table_maintenance` covers after the fact |
| Lakehouse — table maintenance | OPTIMIZE (V-Order, zOrderBy) / VACUUM / purge DVs | **P1** — the recluster our engine cannot do (§4) |
| Lakehouse — Livy sessions | Spark session mgmt | **skip** — dev-driver territory (`fabricnb` uses it as a tool, not a SQL shape) |
| Notebook | list; getDefinition; CRUD/updateDefinition | **P1 inspection** (list/definition/parameters, §4). Authoring: **skip** (rule 2) |
| Warehouse | list/get; create/delete | **P2 list/get** (`fabric_warehouses` — connection string feeds a T-SQL ATTACH). CRUD: **skip** |
| Data Pipeline | CRUD (execution rides Job Scheduler) | run: **P1** via `fabric_run_job`. CRUD: **skip** (rule 2) |
| Semantic Model | list; CRUD/definition; refresh | refresh: **deferred decision** (§8 — Job Scheduler vs PBI enhanced refresh vs XMLA through our DAX provider). list: **P3** (the DAX provider already enumerates). CRUD: **skip** |
| Environment | list/get; publish | **P3 helper** — name→id resolution for `fabric_run_notebook`'s `config_json.environment`; likely resolved inline rather than exposed |
| Spark (pools, workspace settings) | — | **skip** — compute infra admin |
| Mirrored Database | CRUD, start/stop mirroring, status | **skip, but WATCH** — SQL-facing; revisit if mirrored sources enter our flows (e.g. `fabric_mirroring_status` before reading a mirrored table) |
| Report, Dashboard, Dataflow, Eventstream, Eventhouse, KQL Database/Queryset, ML Model/Experiment, GraphQL API, SQL Database, Mounted Data Factory | — | **skip** — not in the DuckDB data path; nothing a SQL function adds over the portal/their own tooling |
