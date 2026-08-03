# Fabric REST API custom functions — design + as-built (the whole curated set is BUILT)

> Status 2026-08-02: **the entire curated set is BUILT.** P0, notebook runs, jobs + table maintenance
> (§9e), introspection, semantic models incl. enhanced refresh (§9f), P3 promotion/platform + the XMLA half
> (§9g), and the SQL Server catalog binding (§9h) — all shipped. **Live-validated:** everything through P2,
> semantic models, XMLA, and §9h. **NOT live-validated: the 15 P3 functions** (§9g) — this tenant has no
> git-connected workspace, no deployment pipeline and no mirrored database, so they are wired and reviewed
> but unexercised. **SDK pin: `Microsoft.Fabric.Api` 2.18.0** (bumped from 2.14.0 on 2026-08-02 — §9i).
> Refresh is
> not in the Fabric SDK at all; it lives in the Power BI REST API, on an audience we already mint. §9c is the as-built record, §9d
> settles how notebook parameters actually arrive, §10 sweeps the whole API with a verdict per area; §4's
> table marks what is shipped. The research below is
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

**The SDK is already shipped.** `Microsoft.Fabric.Api` (**2.18.0** since 2026-08-02; this section was
researched against 2.14.0 and its findings re-verified on the new pin — §9i) is a `Fabricator.Bridge`
PackageReference and is used in production code today:
[FabricLakehouse.cs](../dotnet/Fabricator.Bridge/FabricLakehouse.cs) calls `GetLakehouse` (schema-enabled
flag + `sqlEndpointProperties`), `WorkspacesClient.ListWorkspaces` and `ItemsClient.ListItems`
(name→GUID resolution), and `TablesClient.ListTables` (the Unity-Catalog alternative is raw HTTP in the
same file). Dependencies are lean (netstandard2.0; Azure.Core + DiagnosticSource + IdentityModel.Tokens.Jwt
— all already in our closure). **The pinned dll already contains everything P0/P1 needs** —
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
- **Gap 2 — custom scalar/table functions are positional-only. CLOSED for TABLE functions (2026-07-31);
  see §7 slice 6. Still true for scalars, and unfixable there — DuckDB scalar functions have no named
  parameters.** The original finding: Named parameters with defaults exist
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
| **P0 ✅** | `fabric_run_notebook`/`_ex` | table, blocking | `POST …/items/{id}/jobs/RunNotebook/instances` + instance polling | **user-elevated (2026-07-30)**: parameterized notebook runs from dbt hooks, returning final status **+ `exit_value`** (`mssparkutils.notebook.exit`) for conditional orchestration. SP execution already proven live on this tenant by `scratchpad/fabricnb`. See §5 |
| P1↑ ✅ | `fabric_items(item_type := 'Notebook')` / **`fabric_notebook_parameters`** — both BUILT; `fabric_notebook_definition` **DROPPED**, see below | table | `GET …/items?type=Notebook`; `POST …/notebooks/{id}/getDefinition` (LRO) | **notebook inspection, user-elevated above the rest of P1**: list, plus a convenience that parses the `parameters`-tagged cell into (name, default) rows — heuristic by nature (regex over `name = literal` lines), flagged as such |
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
6. **Named-parameter slice — DONE (2026-07-31).** The `fabricator.named` tag now drives plain TABLE
   function registration too (catalog AND global paths in `fabricator_schema_entry.cpp`), so an optional
   argument is written `recreate := true` and the `_ex` siblings are RETIRED —
   `fabric_refresh_sql_endpoint`, `fabric_list_shortcuts`, `fabric_run_notebook` and `fabric_items` are one
   function each again. Authoring surface: `ITableFunction.NamedParameters` (default empty, so nothing
   else changes).
   - **The binding still reads arguments BY POSITION**, and the positions are `Parameters` ++
     `NamedParameters` in declared order; the host marshals EVERY declared parameter, substituting a typed
     NULL for an omitted named one. That is what makes the conversion free: an omitted optional argument
     and an explicit NULL were already the same thing to a nullable trailing argument, so no binding code
     changed when the `_ex` split collapsed.
   - **SCALAR functions are NOT included, and cannot be**: DuckDB `ScalarFunction` has no named-parameter
     concept at all. So the shortcut scalars keep positional signatures, and
     `fabric_create_shortcut_ex(…, conflict_policy)` stays a real sibling rather than a workaround.
   - **Positional and named MIX freely**, and that is the combination worth testing rather than either alone:
     the host marshals EVERY declared parameter, substituting a typed NULL for an omitted named one, so an
     off-by-one there corrupts the POSITIONAL values instead of erroring. Verified live on
     `fabric_run_notebook('nb', wait_seconds := 900, params_json := '{…}')` — named args supplied OUT of
     declared order with the intervening `config_json` omitted — by reading the values back out of the
     notebook (`p_text: "mixed-args", p_int: 99`), which a shift would have turned into defaults.
   - Gates: `verify_delta_catalog_functions` §6 (both `:=` and `=>` spellings; that the value really crosses
     the ABI rather than being filtered above the scan; that a misspelled name is a clean binder error with
     candidates rather than silently ignored; that a named parameter is not positionally callable) and
     `verify_global_functions` (the demo global `fabricator_seq(n, start := …)` now carries a MIXED signature,
     so the global registration path is pinned hermetically too — live tests are not in CI).

## 8. Deferred / out of scope (recorded so they aren't re-litigated)

- **T-SQL DDL interception** (`CREATE SHORTCUT …` parsed out of `fabricator_exec` and translated to
  API calls) — the user's explicit "later stage". Note Microsoft may ship real T-SQL for shortcuts
  eventually; interception should mirror whatever grammar they choose, not invent one.
- ~~**Semantic-model refresh** — carrier decision pending~~ **RESOLVED, and BOTH carriers are built**:
  Power BI REST enhanced refresh as `fabric_refresh_semantic_model` (§9f) and XMLA/TMSL as
  `dax_refresh`/`_table`/`_partition` in the DAX provider (§9g). The Job Scheduler was never a
  candidate once measured — the Fabric SDK cannot refresh a model at all.
- ~~**SqlServer-catalog binding** of these functions (Fabric Warehouse attaches) — the largest remaining gap
  in reach; credential plumbing, not new API surface~~ **DONE + live-validated 2026-08-02 (§9h).** The
  diagnosis was half right: the credential really was just plumbing (a connstr marker, no ABI change), but the
  actual blocker was that the function context held a OneLake *root* and parsed workspace/item out of it —
  a Fabric SQL connection string can supply neither, so the set was structurally unreachable from any other
  provider. Defaults now come from `workspace`/`item` ATTACH options. Building it found two shipped bugs
  (`fabric_lakehouses`/`fabric_warehouses` throwing on every call; every hand-rolled timestamp 1000× too
  small) — see §9h.
- **deltars provider** hosting; **per-function plugin packaging**; everything marked skip in the
  §10 sweep (each with its recorded reason — don't re-litigate without new demand).
- ~~**Shared function-dispatch extraction**~~ **DONE (§9g)** — `CatalogFunctionSet` covers all six
  catalog-bound kinds and SqlServer/DAX/Delta all dispatch through it.

## 9. Risks / verify-at-spike checklist

Rows marked **RESOLVED** were settled by the slice-1 spike (§9b); the rest still stand.

| risk | why it's real | probe |
|---|---|---|
| SP gated off tenant-side despite docs | **RESOLVED — and it BIT TWICE**: `ResetShortcutCache` 400 `PrincipalTypeNotSupported`, notebook CREATE 403 `FeatureNotAvailable`, both documented as SP-supported. All P0 data-path calls DO work as SP | done (§9b) |
| refresh latency makes a blocking hook painful | **RESOLVED (partly)**: 7.5 s on `LH` — a blocking hook is fine. Unknown on a large/cold endpoint; still worth surfacing elapsed time | re-measure after a bulk create |
| `refreshMetadata` semantics on schema-enabled lakehouses | statuses are `tableName`-keyed; schema qualification unclear | STILL OPEN — `LH` is schema-enabled but the run returned before new tables existed to inspect; re-check with a fresh table |
| throttling on name resolution | ListWorkspaces/ListItems are per-principal throttled | cache per instance; probe burst behavior |
| SDK model gaps vs current REST | e.g. newer conflict policies | **RESOLVED** — every needed client/method/policy was already in 2.14.0, so no bump was ever forced; the pin moved to **2.18.0** on 2026-08-02 to track latest, and it changes nothing here (§9i) |
| tableMaintenance is preview | may change shape | keep P1; pass-through `execution_data_json` in `_ex` as the stable escape hatch |
| `exitValue` retrieval | **RESOLVED (partly)** — it is `properties.exitValue` on the NOTEBOOK-scoped GET only, and absent from the SDK model in 2.14.0/2.18.0. Returned NULL in every run despite the notebook calling `exit`, so it is best-effort | done (§9d) |
| notebook param typing | **RESOLVED** — the `executionData.parameters` map IS honoured (types `string`/`int`/`float`/`bool` → `str`/`int`/`float`/`bool`); the top-level `parameters[]` array is silently ignored for notebooks | done (§9d) |
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
**`fabric_run_notebook()`/`_ex`** (built once §9d settled the parameter shape, and proven end-to-end: a
SQL call passing `{"p_text":"from-sql","p_int":42,…}` was READ BACK from the notebook's own output with
correct Python types), and **`fabric_notebook_parameters(notebook)`**.

Hermetic tier **62 runs / 5558 assertions green**; the whole P0 set exercised end-to-end against workspace
`Test` / lakehouse `LH` (create → list → alter → drop → drop-again → refresh, self-cleaning).

**Every catalog-bound TABLE function also takes `workspace :=` / `item :=` overrides** (2026-07-31, once
named parameters existed to express them). The attach supplies the defaults, so the zero-argument call is
unchanged; passing them retargets the SAME attach at another lakehouse or workspace — the common case for a
dbt project writing to several lakehouses, which otherwise needs a second ATTACH purely to refresh an
endpoint. `FabricApiClient.ResolveWorkspace/ResolveItem` already accepted an override, so this was
declarations plus wiring; `ResolveItem` gained an explicit `workspaceId` so a cross-workspace lookup does not
silently search the attach's own. Verified live: `fabric_refresh_sql_endpoint()` → LH's 19 tables,
`(item := 'LH2')` → 0 (a different, empty lakehouse) through one attach; an unknown item errors naming both
the item and the workspace. Scalars (the shortcut writers) are excluded — no named parameters there — so they
always act on the ATTACHED item.

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
schema across the C data interface — in EITHER direction.**

Both ways of building the schema CONSTRUCT fine, so the object is not the problem and neither is a
workaround — the throw is in the marshaling. Measured in both directions with a one-field round-trip as
the positive control (so this is the library, not the harness):

| operation on a 0-field schema | result |
|---|---|
| `new Schema(Array.Empty<Field>(), metadata: null)` | OK — `FieldsList.Count == 0` |
| `new Schema.Builder().Build()` | OK — `FieldsList.Count == 0` |
| `CArrowSchemaExporter.ExportSchema` | throws `ArgumentNullException(Parameter 'fields')` |
| `CArrowSchemaExporter.ExportType(new StructType(empty))` | throws, identically |
| `CArrowSchemaImporter.ImportSchema` on a hand-built empty struct | throws, identically |

Those are the only two public export entry points, so there is no supported path.

The failure mode is why it stayed hidden: `GetOrCreateScalarFunction`/`GetOrCreateTableFunction` treat ANY
schema-fetch failure as "this discovered name is stale" and **silently erase the function**. So a
zero-argument function does not error — it simply never appears in the catalog. The only trace is a
Debug-level `GetFunctionParamSchema failed: Value cannot be null. (Parameter 'fields')` WARN, which
CLAUDE.md had already recorded and written off as benign. It was not benign; it was this.

Fixed in two halves, both required:
1. **Export** (`ArrowSchemaExport.Export`, used by `Bootstrap.GetFunctionParamSchema`): delegates to
   Apache.Arrow when there is ≥1 field, otherwise hand-builds the empty struct (`format="+s"`,
   `n_children=0`) through a layout mirror, since `CArrowSchema.release` is internal. Ownership follows
   the C data interface — the consumer's `release` frees the format string and NULLs itself. This works
   even though Apache.Arrow cannot IMPORT what it emits, because the consumer here is the C++ host, which
   reads the schema with its own `ReadArrowSchema`.
2. **Import avoided entirely** (`fabricator_schema_entry.cpp`, the table-bind factory): for an
   argument-less function the host now passes **no args stream at all** rather than an empty one. `args`
   was already nullable by contract, so this is the contract's intended shape — no ABI bump.

**⚠ CORRECTED 2026-08-02 — this section used to end "zero-argument SCALAR functions remain impossible,
and that is not worth fixing: a scalar's argument batch is also how the host conveys the ROW COUNT, so
'no columns, N rows' has nowhere to live." THAT REASON IS WRONG, and zero-argument scalars now work.**
A 0-column Arrow array carries its length perfectly well — a 0-column, 5-row `RecordBatch` reports
`Length=5`, and EXPORTING it succeeds (both measured). "No columns, N rows" has exactly the place the
Arrow spec gives it. The obstacle was only ever the zero-FIELD SCHEMA above, whose IMPORT half went
unfixed because a zero-argument TABLE function never needs it: the host sends no args stream, so nothing
is imported. A scalar's arg batch travels the other way, so it does.

The fix is a **throwaway column**: for a zero-parameter scalar the host marshals one BOOLEAN column of
`row_count` rows. No ABI change and no managed change — a zero-argument function reads only
`RecordBatch.Length`, and the scalar dispatch never validated column count. Gate:
`verify_global_functions` (72 → 80) via the demo `fabricator_batch_seq()`, mutation-tested. The mutant is
worth knowing: with the fix reverted the function still REGISTERS (registration needs only the
param-schema export, which was already fixed) and fails at CALL time — so a registration-only assertion
would not have caught it.

`fab_delta_info()` is still a TABLE function, but now by CHOICE (it returns rows), not by necessity.

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
each, `fabric_warehouses()` → 1, `fabric_items(item_type := 'Notebook')` → 3, `fabric_notebook_parameters` → 0 rows
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

## 9d. Notebook parameters + exitValue — RESOLVED live (2026-07-30), and one lesson about method

The `fabricator_api_spike` notebook was created interactively, then filled via `UpdateItemDefinition`
(SP-allowed) and run repeatedly. **Precondition verified first**: the definition was read BACK and the
`parameters` tag confirmed present on cell 0 (`round-trip cell[0] tags=[parameters]`) with the intended
kernel — without that check a negative result would have been measuring our own upload.

**What works:**

| payload | result |
|---|---|
| `{"executionData":{"parameters":{"p_text":{"value":"hello","type":"string"}, …}}}` | **HONOURED** — the notebook observed `hello` / `7` / `true`, typed `str`/`int`/`float`/`bool` |
| top-level `{"parameters":[{"name":…,"value":…,"type":"Text"}]}` | **ACCEPTED (202) BUT SILENTLY IGNORED** — the notebook saw its defaults |

Both submit routes work for the honoured shape — the items-scoped
`…/items/{id}/jobs/RunNotebook/instances` and the notebook-scoped
`…/notebooks/{id}/jobs/execute/instances?jobType=RunNotebook`. So the legacy `executionData.parameters`
MAP is the shape to send, and the generic top-level `parameters` array is not usable for notebooks
(consistent with the API reference's own warning that it "is not broadly supported"). Type strings are
`string` / `int` / `float` / `bool`.

**`exitValue` is at `properties.exitValue`, and ONLY on the notebook-scoped GET** —
`…/notebooks/{id}/jobs/execute/instances/{jobInstanceId}` (with or without `?beta=true`; the items-scoped
Location URL never returns `properties` at all). That `properties` object is genuinely useful beyond the
exit value: `compute` (`Jupyter`/`Spark`), and `computeDetails.monitoringInfo` carrying
`executionSnapshotUrl` plus, on Spark, `sparkUiUrl` / `driverLogUrl` / `sparkJobDetailsUrl` — i.e. a
diagnosis link for a failed run, surfaceable from SQL.

**But exitValue came back `null` in EVERY run**, on both Jupyter and Spark compute, even though the
notebook-side API demonstrably exists and is called (`hasattr(notebookutils.notebook,'exit')` → `true`,
confirmed by having the notebook report it). So treat `exit_value` as **best-effort, frequently NULL** —
a `fabric_run_notebook` must not promise it, and a flow needing a result should have the notebook write
to a table or file. (Writing works: plain `open('/lakehouse/default/Files/…')` through the fuse mount
succeeded, while `notebookutils.fs.put` wrote nothing on the python kernel — the mount is the reliable
channel, and it requires the ipynb to declare a default lakehouse in `metadata.dependencies.lakehouse`.)

**⚠ The method lesson, which cost two full rounds of live runs.** The first two attempts concluded
"BOTH shapes are ignored" — WRONG. Both shapes were submitted in sequence and the notebook's result file
was read ONCE afterwards, so the second (ignored) submission's output was attributed to both. A shared
side-channel read after N experiments measures only the last one. The fix — clear the marker before each
submission and read it per shape — is what produced the real answer. This is the repo's standing
"a negative result is not a measurement until the method is shown to work" rule in a new disguise:
here the method ran fine and the ATTRIBUTION was broken.

## 9e. Jobs, maintenance and introspection — BUILT (2026-07-31)

Eight more functions, on one shared submit+poll path (`SubmitItemJobAsync` / `PollItemJobAsync`, generalized
out of the notebook runner):

| function | kind | verified |
|---|---|---|
| `fabric_table_maintenance(table [, schema :=] [, v_order :=] [, z_order_by :=] [, vacuum_retention :=] [, purge_deletion_vectors :=] [, wait_seconds :=] [, workspace :=] [, item :=])` | table, blocking | **LIVE — `Completed`** with `v_order := true` on `dbo.arrownet_ckpt`, and the table still read back correctly afterwards (18 rows) |
| `fabric_run_job(item, job_type [, execution_data_json :=] [, wait_seconds :=] [, workspace :=] [, item_type :=])` | table | **LIVE** — submitted `RunNotebook` with `wait_seconds := 0` → `NotStarted` + instance id |
| `fabric_job_status(item, job_instance_id [, workspace :=] [, item_type :=])` | table | **LIVE** — `Completed` |
| `fabric_job_instances(item [, workspace :=] [, item_type :=])` | table | **LIVE** — 17 `RunNotebook`/`Completed` rows for the spike notebook |
| `fabric_cancel_job(item, job_instance_id)` | scalar → BOOLEAN | **LIVE** — accepted |
| `fabric_lakehouse_tables([workspace :=] [, item :=])` | table | **LIVE** — 5 rows on the FLAT lakehouse; see the limitation below |
| `fabric_operation_status(operation_id)` | table | **LIVE** — clean `NotFound` for a bogus id |
| `fabric_reset_shortcut_cache([workspace :=])` | table | **wired, success path UNTESTABLE here** — see below |

**Why table maintenance is worth having next to our own OPTIMIZE**: **V-Order** is Microsoft's proprietary
parquet layout optimization and we cannot produce it, so a table that Power BI or the SQL endpoint reads hot
belongs in this job even though we compact perfectly well ourselves. It also offers Z-order, VACUUM and
`REORG … APPLY (PURGE)` for deletion vectors. Following the API's own convention, an ABSENT settings object
means "skip that part", so the defaults here do nothing rather than something surprising.

### Findings

- **`fabric_lakehouse_tables` is REFUSED on a schema-enabled lakehouse**:
  `UnsupportedOperationForSchemasEnabledLakehouse` — *"The operation is not supported for Lakehouse with
  schemas enabled."* The same call against the flat `LH_no_schema` returns its 5 tables, so this is Fabric's
  limitation and not our wiring. Our own catalog discovery covers the schema-enabled case anyway.
- **`Wrap` did NOT cover a paged read, and the first failure proved it.** `PageableResponse<T>` is lazy, so
  the request happens during ENUMERATION — outside the try — and the refusal above arrived as a raw Azure dump
  complete with a header list rather than our formatted message. Fixed by `WrapList`, which materializes
  inside the guard; every paged read now uses it. Worth remembering as a shape: *a guard around a call that
  returns a lazy sequence guards nothing.*
- **A table function cannot take a SUBQUERY argument** — `Binder Error: Table function cannot contain
  subqueries`. So `fabric_job_status(item, (SELECT job_instance_id FROM j))` is rejected while the SCALAR
  `fabric_cancel_job(item, (SELECT …))` accepts it. Pass a literal, or keep the id in a variable. A DuckDB
  rule, not ours, but it shapes how these compose.
- **`fabric_reset_shortcut_cache` is implemented BLIND, deliberately, and is nonetheless proven wired**: the
  SP is refused with `400 PrincipalTypeNotSupported` (as measured in §9b), so the SUCCESS path could not be
  exercised — but the call reaches the service and returns the service's own error, which is everything except
  the permission. Expected to work under a USER identity, and in particular under a Fabric notebook's AMBIENT
  token, which is user-delegated rather than an SP. It is a TABLE function because returning a row lets it
  report which workspace it acted on. (It also used to cite "a zero-argument SCALAR is impossible" — obsolete
  as of 2026-08-02, see §9c; the returns-a-row reason stands on its own.)

## 9f. SEMANTIC MODELS — what exists, and where refresh actually lives (analysis, 2026-07-31, NOT built)

Both a **Lakehouse** and a **Warehouse** get a **default semantic model**, and both are reachable with the
credential we already hold. The question is which API surface serves them.

**The Fabric SDK cannot refresh a semantic model at all.** Probed in the pinned 2.14.0 with a zero control:
`RefreshSemanticModel` 0, `SemanticModelRefresh` 0, `EnhancedRefresh` 0, `RefreshSchedule` 0.
`SemanticModel.ItemsClient` offers only CRUD, `GetSemanticModelDefinition`/`UpdateSemanticModelDefinition`,
`ListSemanticModels`, and `BindSemanticModelConnection`. So:

| capability | surface | availability for us |
|---|---|---|
| list / get models | `SemanticModel.Items.ListSemanticModels` (Fabric SDK) | **available now**, same client, no new auth |
| model definition (TMDL/TMSL parts) | `GetSemanticModelDefinition` (LRO) | available now; base64 parts, same shape as the notebook definition |
| rebind to another connection | `BindSemanticModelConnection` | available now |
| **REFRESH** | **Power BI REST** `POST /v1.0/myorg/groups/{ws}/datasets/{id}/refreshes` | **different HOST, but the SAME audience we already mint** |
| refresh history | Power BI REST `GET …/refreshes` | ditto |
| per-table / per-partition refresh | **XMLA + TMSL** through the existing DAX provider | ditto (ADOMD is already wired) |

**The auth story is already solved, which is the important part.** The Power BI REST API wants
`Dataset.ReadWrite.All` on the **`https://analysis.windows.net/powerbi/api`** audience — and
`FabricCredentialResolver.PowerBiScope` is exactly that constant, already used by the DAX provider
(`DaxTokenAuth`). So the same `fabric_sp` secret and the same ambient notebook token serve semantic-model
refresh with **no new scope, no new credential path** — only a second base URL. That is a strictly smaller
change than the Fabric API integration was.

**The enhanced-refresh body is expressive enough to be worth exposing properly** (not just a fire-and-forget):
`type` (Full / ClearValues / Calculate / DataOnly / Automatic / Defragment), `commitMode`
(Transactional / PartialBatch), `objects[{table, partition}]`, `applyRefreshPolicy`, `effectiveDate`,
`maxParallelism`, `retryCount`, `timeout`. It returns **202 + `Location` + `x-ms-request-id`**, and status
comes from `GET …/refreshes` — the same submit-then-poll shape `SubmitItemJobAsync`/`PollItemJobAsync`
already implement, so the polling logic is reusable rather than new.

**Why this matters right next to `fabric_refresh_sql_endpoint`:** after a Delta write there are TWO consumers
to make current, and they are refreshed by different calls. `fabric_refresh_sql_endpoint` makes the table
visible to **T-SQL**; refreshing the **semantic model** (a DirectLake reframe for a lakehouse/warehouse
default model) is what makes the new data visible to **Power BI**. A dbt flow that ends in a report wants
both, and today we only offer the first.

**Constraints to design around, all documented rather than guessed:**
- **Enhanced refresh is NOT supported on shared capacity** (and shared capacity is limited to 8 refreshes/day,
  body restricted to `notifyOption`). So the rich form requires Fabric/Premium capacity — measured as
  satisfied on this tenant (`isOnDedicatedCapacity: true`), but the error must still say so for others.
- **`notifyOption` is not applicable to a service-principal call**, and an enhanced refresh requires at least
  one non-`notifyOption` field. So the SP path must send a real body and must NOT send `notifyOption` — the
  two rules interact, and getting it wrong yields a plain refresh instead of the requested one.
- **Resolving a lakehouse's default semantic model is by NAME convention** (it carries the item's name), not
  by a documented link — `ListSemanticModels` has no "this is the default for item X" field. Any function
  doing the lookup should say so, and accept an explicit model name/GUID.
- **SP access to the Power BI REST surface is a SEPARATE tenant setting from the Fabric-API one** — Admin
  portal → **Tenant settings → Developer settings → "Service principals can call Fabric public APIs"**
  (optionally scoped to a security group), and it is NOT the same thing as granting the SP a workspace role:
  the tenant setting decides whether service principals may call these APIs at all, the workspace role decides
  what this one may do to that workspace. Both must hold. **On THIS tenant it is already satisfied — MEASURED,
  not assumed** (2026-07-31, `dotnet run live pbi`): as the same `fabric_sp`,
  `GET /v1.0/myorg/groups` → **200** and `GET /groups/{ws}/datasets` → **200**. So semantic-model refresh needs
  no admin change here. Two bonus facts from that probe: the workspace reports
  `isOnDedicatedCapacity: true`, so the shared-capacity restriction on enhanced refresh **does not apply**, and
  the datasets list confirms the name convention empirically — lakehouse `LH` has a model literally named
  `LH`, alongside `Test Warehouse Model1`/`Model2`, `LH_semtest` and `hm`, every one `isRefreshable: true`.
  (For the XMLA/TMSL route there is a THIRD, capacity-level gate: the Semantic models workload's **XMLA
  endpoint must be Read Write**, and the SP needs workspace Member/Admin.)

### BUILT + LIVE-VALIDATED the same day (2026-07-31)

All three shipped, on the Power BI REST surface (`FabricApi/FabricPowerBiRest.cs`, a `partial` half of
`FabricApiClient`) with the functions in `FabricApi/FabricSemanticModelFunctions.cs`:

| function | live result |
|---|---|
| `fabric_semantic_models([workspace :=])` | 5 models — `LH` (the lakehouse default), `Test Warehouse Model1`/`Model2`, `LH_semtest`, `hm`, all `is_refreshable` |
| `fabric_refresh_semantic_model(model [, type :=] [, objects_json :=] [, commit_mode :=] [, max_parallelism :=] [, retry_count :=] [, timeout :=] [, wait_seconds :=] [, workspace :=])` | **`Completed`**, `refresh_type = ViaEnhancedApi` — so the ENHANCED path really was taken, not a plain refresh |
| `fabric_semantic_model_refreshes(model [, top :=] [, workspace :=])` | history rows incl. `ViaEnhancedApi`, `DirectLakeFraming`, `WebModeling` |

Three things the live run settled that the design could only assume:

- **`refresh_type = ViaEnhancedApi` is the proof the body was right.** The API treats a request whose only
  field is `notifyOption` as a PLAIN refresh, and rejects `notifyOption` outright for a service principal —
  two rules that interact. Sending `type` (default `Full`) and NEVER `notifyOption` is the one combination
  valid for both identity kinds, and the returned refresh_type is how you can tell which path you got.
- **`DirectLakeFraming` appears in the history as its own refresh type**, which is the reframe mechanism named
  explicitly — the thing that makes a Delta write visible to Power BI.
- **Power BI reports IN-PROGRESS as `status = "Unknown"`**, not a distinct running state, and a just-submitted
  request may not be in the history yet at all. The poll treats both as "still running"; a naive
  `status != "Completed"` check would have exited immediately with a misleading value.

Implementation notes worth keeping: the request id arrives ONLY in the `x-ms-request-id` header (the 202 has
no body; a `Location` tail is the documented fallback, and absent both we say so rather than return a blank
id), and Power BI nests its errors under `error.{code,message}` where Fabric uses flat `errorCode`/`message`
— so the two surfaces need different error extraction, which is why `PowerBiReadAsync` exists next to
`Describe`.

**Original recommendation, kept for the record** — three functions, in this order of value:

1. `fabric_refresh_semantic_model(model [, type :=] [, objects_json :=] [, commit_mode :=] [, max_parallelism :=] [, retry_count :=] [, timeout :=] [, wait_seconds :=] [, workspace :=])` → one row (`request_id`, `status`, `start_time`, `end_time`, `error_message`), blocking by default like the rest.
2. `fabric_semantic_models([workspace :=])` → id, name, description — Fabric SDK, trivial, and the discovery half of (1).
3. `fabric_semantic_model_refreshes(model [, workspace :=] [, top :=])` → refresh history (`request_id`,
   `refresh_type`, `status`, `extended_status`, times, error), for asserting in a hook that the LAST refresh
   actually succeeded.

**And the ADOMD/XMLA route stays the better answer for fine-grained work**, which is why (1) should not try to
grow into it: the DAX provider already holds an ADOMD connection on the same token, so a TMSL `refresh`
command there gives per-table/partition control, `sequence` batching, and the model-level operations the REST
API does not express at all. The natural split is **REST for "refresh this model, tell me when it is done"
(`fabric_*`), XMLA/TMSL for "refresh exactly these partitions in this order" (`dax_*`)** — and the second one
belongs in the DAX provider's namespace, not this one.

## 9g. P3 — the promotion + platform surfaces, and the XMLA half (BUILT 2026-07-31)

With P0–P2 shipped, the remaining §10 verdicts were either **P3 demand-driven** or **skip**. The P3 set was
asked for and is now built; every **skip** stands, with its recorded reason. **Validation status is mixed and
stated per function below** — the Fabric reads are wired and reviewed but NOT live-validated in this pass (the
tenant has no git-connected workspace, no deployment pipeline and no mirrored database to exercise them
against), and the XMLA functions sit behind a MANUAL gate by construction.

### The SDK surface, measured first

`dotnet run p3` in `scratchpad/fabricspike` dumps every public method of the relevant clients plus the model
shapes, with a positive and a negative control (`FabricClient` present / `NoSuchClient` absent) so a filter typo
cannot masquerade as "the API does not have this". Findings that shaped the functions:

- **Git lives on `Core.Git`** and every call is an LRO the SDK BLOCKS on (`timeoutInMinutes`, default 60) —
  `GetStatus`, `GetConnection`, `CommitToGit`, `UpdateFromGit`. So there is no submit-and-poll shape here and no
  request id; a commit is simply a long statement.
- **`WorkspaceConflictResolution(ConflictResolutionType, ConflictResolutionPolicy)`** — the type has exactly ONE
  member (`Workspace`) and the policy two (`PreferRemote`, `PreferWorkspace`). Worth probing rather than
  guessing: these are Azure *extensible enums*, so a wrong string compiles and reaches the service.
- **`ListDataAccessRoles` returns `Response<DataAccessRoles>`, not a `PageableResponse`** — it carries its own
  `ContinuationToken`, so it is the one read here that does not go through `WrapList`.
- **Mirroring splits across two clients**: `MirroredDatabase.Items` (list/get) and `MirroredDatabase.Mirroring`
  (`GetMirroringStatus`, `GetTablesMirroringStatus`, `StartMirroring`, `StopMirroring`).
- **`TableMirroringMetrics.LastSyncDateTime` and `GitSyncDetails.LastSyncTime` are NON-nullable
  `DateTimeOffset` on a NULLABLE parent** — so the null test must be on the parent. Written the other way it
  would silently report the .NET epoch as a sync time.

### What was built

| function | kind | status |
|---|---|---|
| `fabric_git_status([workspace :=])` | table | wired; one row with NULL change columns when the workspace is clean, so "in sync" ≠ "not connected" |
| `fabric_git_connection([workspace :=])` | table (1 row) | wired |
| `fabric_git_commit([mode :=] [, comment :=] [, items_json :=] [, workspace_head :=] [, wait_seconds :=])` | table (1 row) | wired; `mode := 'Selective'` REQUIRES `items_json` and says so |
| `fabric_git_update(remote_commit_hash [, conflict_resolution :=] [, allow_override :=] [, workspace_head :=])` | table (1 row) | wired; the hash is positional and required — see below |
| `fabric_deployment_pipelines()` | table | wired |
| `fabric_deployment_pipeline_stages(pipeline)` | table | wired |
| `fabric_deployment_pipeline_items(pipeline, stage)` | table | wired |
| `fabric_deploy(pipeline, source_stage, target_stage [, note :=] [, wait_seconds :=])` | table (1 row) | wired; whole-stage deploy only |
| `fabric_deployment_pipeline_operations(pipeline)` | table | wired; same columns as `fabric_deploy`, so a submit and a history row read identically |
| `fabric_capacities()` | table | wired |
| `fabric_environments([workspace :=])` | table | wired — the name→id helper §10 anticipated for `fabric_run_notebook`'s `config_json` |
| `fabric_data_access_roles([item :=] [, workspace :=])` | table | wired, READ only |
| `fabric_mirrored_databases([workspace :=])` | table | wired |
| `fabric_mirroring_status(database)` | table (1 row) | wired |
| `fabric_mirrored_tables(database)` | table | wired |

Design points worth keeping:

- **`fabric_git_update`'s commit hash is REQUIRED and positional**, deliberately. "Update to whatever is on the
  branch now" is how a promotion flow silently deploys an unreviewed commit; making the caller read the hash
  from `fabric_git_status()` in the same script keeps the decision explicit. `workspace_head` is the API's
  OPTIMISTIC CONCURRENCY token on both commit and update — supply it and a racing commit fails the statement
  instead of overwriting.
- **`wait_seconds` is the vocabulary everywhere, but these APIs only accept MINUTES.** The value is rounded UP
  and floored at 1, because 0 would mean "give up immediately" rather than "do not wait" — a distinction these
  endpoints cannot express at all, unlike the job APIs where `wait_seconds := 0` genuinely submits and returns.
- **Stage resolution accepts a GUID, a display name, or an ORDER number**, tried in that sequence — so a stage
  literally named "1" resolves to itself rather than to order 1. Positional reference ("promote 0 to 1") is how
  people talk about stages, but name has to win or the naming is a trap.
- **What is deliberately still out**, per area: git `Connect`/`Disconnect` and the credential calls (rule 1 —
  they carry a PAT); pipeline/stage CRUD, role assignments and workspace-to-stage assignment; capacity ASSIGN
  (the list only ever existed to feed it); environment PUBLISH (meaningless without the library-definition
  writes rule 2 excludes); data-access-role WRITE (folder security policy from a SQL string); and
  `StartMirroring`/`StopMirroring` — reconfiguring someone else's ingestion is not a transformation's business,
  whereas *reading* whether it has caught up is exactly a data-path concern. Reading and advancing an existing
  configuration is in; establishing or re-pointing one is not.
- **`fabric_notebook_definition` is DROPPED, not deferred.** §4 listed it, and only
  `fabric_notebook_parameters` was ever built (it reads the definition internally). Exposing the raw parts is
  the base64-payload-in-SQL shape exclusion rule 2 exists to prevent, the call is a ~20 s LRO, and the parsed
  parameter list is the part anyone actually wanted. Recorded here so the §4 row stops reading like unfinished
  work.

### The XMLA/TMSL half — `dax_*`, in the DAX provider (§9f's other side)

§9f concluded the split: **REST for "refresh this model, tell me when it is done" (`fabric_*`), XMLA/TMSL for
"refresh exactly these partitions in this order" (`dax_*`)**. The second half now exists, on a DAX attach:

| function | notes |
|---|---|
| `dax_refresh([type :=] [, objects_json :=] [, max_parallelism :=])` | whole model, or exactly the tables/partitions in `objects_json` |
| `dax_refresh_table(table [, type :=])` | the single-table convenience |
| `dax_refresh_partition(table, partition [, type :=])` | **the operation REST cannot express at all** — the reason this exists |

- **They are SYNCHRONOUS**, which is the sharpest practical difference from the REST path: the XMLA command
  does not return until the refresh finishes, so there is no request id, no polling, and no "in-progress"
  status to misread (contrast Power BI reporting in-progress as `status = "Unknown"`, §9f trap 2). A long
  refresh is a long statement, cancellable through the same tier-3 `InterruptScope` mechanism as a DAX scan —
  which matters more here than for a scan, since a full refresh runs for minutes.
- **TMSL's type vocabulary is camelCase and NOT the REST one** (`full`/`clearValues`/`dataOnly`, vs REST's
  `Full`/`ClearValues`/`DataOnly`). Both spellings are accepted case-insensitively — someone who copied a type
  from `fabric_refresh_semantic_model` should not be punished — and an unknown value is REJECTED locally,
  because the engine's own answer for a bad type is a generic XMLA parse failure.
- **`maxParallelism` requires wrapping the refresh in a TMSL `sequence`**; there is no flat form.
- **The command is built with `Utf8JsonWriter`, not string concatenation**, so a table or partition name
  containing a quote cannot alter its structure. Names arrive straight from a SQL literal — same reason the SQL
  side parameterizes.
- **`refresh` is the ONLY TMSL verb exposed, on purpose.** The identical `ExecuteNonQuery` path would run
  `createOrReplace` or `delete` just as happily, which would turn a documented read-only provider into an
  arbitrary model-mutation surface reachable from any SQL string. There is deliberately **no generic
  `dax_tmsl(command)` escape hatch**. Refresh moves DATA, which is what a post-Delta-write flow needs; model
  authoring stays with the tools that own it.
- **Enabling change:** `DaxCatalog` now hosts a `CatalogFunctionSet` (see below), so these are ordinary
  catalog-bound table functions declared in the MODEL schema — not in `system`, which is a DMV namespace. The
  three bespoke functions (`daxeval` / `daxevaltable` / `daxeach`) keep their hand-written declarations and are
  still dispatched by name ahead of the set, since their kinds are `proc` / `collector` / `inout`.
- **Validation: NOT live-validated.** `verify_dax` is a MANUAL gate needing Power BI Desktop or a live XMLA
  endpoint, so the automated tiers cover none of this. What WAS verified offline: the provider still resolves
  and reaches ADOMD's connection attempt (a bogus endpoint returns `AdomdConnectionException`, proving the
  catalog constructs and the assembly loads after the rewiring), and the whole hermetic tier stayed green.

### Housekeeping: ONE catalog-function registry, for all six kinds

§8's last deferred item — "the ~120-line `TryGetValue` dispatch block is now hand-copied in SqlServer/DAX (and
would be a third copy in Delta)" — is closed. `CatalogFunctionSet` was extended from two kinds to **all six**
(scalar, table, `table_sql`, `inout`, `collector`, `aggregate`) and now owns the lookup, the ABI members and
the declaration rows; SqlServer's six static dictionaries and DAX's hand-rolled dispatch are gone.

- **The KIND STRINGS are the real prize.** The host's registration switch silently ignores a kind it does not
  recognize, so a typo there does not fail — it makes a function quietly not exist. They are now written once,
  and the `aggregate` vs `aggregate_spill` decision is made in one place.
- **`FunctionsMetadata.Declaration` gained `ParamCount` + `ReturnType`.** They are not among the three columns
  the host reads; they exist because the SQL-Server catalog assembles the same declarations as a T-SQL
  `UNION ALL` against its discovered routines, whose shape is five columns wide, so every branch must supply
  all five. Carrying them lets ONE producer feed both the in-memory stream and that SQL.
- **The `__all__` sentinel is rejected LOUDLY on SqlServer** (`NoSchemaExpansion` throws, naming the reason):
  its declarations name real schemas and its discovery stream is built as T-SQL with no catalog instance in
  scope, so there is nothing to expand against. An empty list would have silently dropped such a function.
  **⚠ SUPERSEDED by §9h (2026-08-02): the sentinel is now IMPLEMENTED there** (`ExpandAllSchemas`), because
  hosting the Fabric set on a SQL Server catalog gave it a real caller. Throwing was the right answer while
  nothing used it; the reasoning above ("no catalog instance in scope") was a fact about the call site, and
  it stopped being true when `FunctionsMetadataSql` became an instance method.
- **What did NOT move into the set**: SqlServer's fallback to a DISCOVERED routine when a name is not a custom
  function, and the in-out isolation wiring (the level is provider state). Both are genuinely
  provider-specific, and each ABI member returns null for an unknown name precisely so a provider WITH
  discovered routines can fall through while Delta and DAX can throw.
- **`FabricRowBuilder`** replaced the per-function parallel-builder plumbing, which was becoming the place a
  column-index slip could hide now that most reads mix strings with counts and timestamps. It is strict about
  type on purpose: writing a string into a timestamp column throws rather than producing a column of NULLs that
  looks like "the service returned nothing". `fab_delta_info` was moved onto it deliberately — it is the only
  function on this path with a HERMETIC gate, so a regression in the shared builder now fails the offline tier
  instead of surfacing only on a live tenant call.
- **Gate: all ELEVEN service suites covering the six kinds green** (custom_functions 89, scalar_functions 26,
  table_functions 33, stored_procs 24, custom_aggregates 58, table_inout 63, proc_inout 31, collector 40,
  sqlgen_catalog 30, functions 13 — now 15, see §9h, inout_isolation 17), plus the hermetic tier at 62 suites /
  5573 assertions (now 63 / 5664).

## 9h. The SQL SERVER catalog binding — BUILT + live-validated (2026-08-02), and it found two shipped bugs

§8 called this *"the largest remaining gap in reach"*, and the framing was right: the whole `fabric_*` set was
bound to a **OneLake Delta** attach, so a dbt project running against a Fabric **Warehouse** over T-SQL could
not call even `fabric_refresh_sql_endpoint`. It is now hosted on a Fabric SQL attach too:

```sql
ATTACH 'Server=<endpoint>.datawarehouse.fabric.microsoft.com;Database=LH' AS w
  (TYPE fabricator, SECRET fabric_sp, WORKSPACE 'Test', ITEM 'LH');

SELECT * FROM w.dbo.fabric_refresh_sql_endpoint();      -- live: 19 tables, all NotRun (= in sync)
```

**No ABI change and no C++ change at all.** `fabricator_storage.cpp` already forwards every unrecognized ATTACH
option into `options_json` verbatim, so `workspace`/`item` arrive at the catalog for free.

### What actually blocked it, and what did not

The gap was described as "credential plumbing". That was the smaller half. The real blocker was that
`FabricApiContext` held the **OneLake ATTACH ROOT** — a Delta-provider concept — and derived workspace + item
from it by parsing. A T-SQL attach has no root to parse, and a Fabric SQL connection string cannot supply one:
its host is an opaque per-workspace routing GUID that names neither the workspace nor the item.

- **Context is now `(Workspace, Item, Credential)`** and each provider supplies the pair however it knows it —
  Delta parses its root once at registration, SQL Server reads the two ATTACH options. `Root` had exactly two
  uses, both inside `FabricApiClient`, so the change was small; it is listed first because it is the one that
  made the set reusable at all. Either may be null, which is not an error: the existing `workspace :=` /
  `item :=` named parameters simply become required instead of optional, and the error says so.
- **The credential rides a connection-string marker** (`;FabricatorFabricCred=`), the mechanism already proven
  twice — `AccessTokenKeyword` here and `FabricatorDeltaCred` on the Delta side. **⚠ Order is load-bearing:**
  the access-token marker is defined as *"everything after it is the token"*, so this one is appended AFTER it
  and stripped BEFORE it. The other order silently folds this marker into the token and breaks SQL auth.
- **⚠ A pre-minted `access_token` is deliberately NOT carried.** Its audience is `database.windows.net`;
  Fabric REST needs `api.fabric.microsoft.com`. Forwarding it guarantees a 401 on the first call, whereas
  carrying nothing falls through to the ambient chain, which genuinely works both on Fabric compute and off
  it. Only a **renewable principal** is carried. (The Delta path does forward a static token and has the same
  hazard — pre-existing, on a live-validated path, left alone rather than changed as a side effect.)
- **`azure_tenant_id` was DECLARED in `SecretFields` from the beginning and consumed by nothing** (it exists
  for parity with the C++ mssql secret). It is load-bearing now: SqlClient infers the tenant from the server it
  connects to, and `ClientSecretCredential` cannot, so an mssql-secret service principal has no other tenant
  source. An `azure` secret already speaks the right vocabulary and needs nothing new.
- **⚠ The registration gate is the HOST, not `ServerProfile.IsWarehouse`.** `IsWarehouse` is the
  obvious-looking choice and is wrong twice over: `EngineEdition == 11` covers Fabric Warehouse, the Lakehouse
  SQL endpoint **and Synapse serverless**, so it would advertise Fabric functions on a Synapse attach; and it
  forces profile detection, turning function registration into a connection round trip at ATTACH. The gate is
  `host.EndsWith(".fabric.microsoft.com")`, which needs only the connection string.
- **`__all__` is now implemented rather than refused** (superseding the §9g bullet). The callback is invoked
  lazily and only when some declaration uses the sentinel, so a non-Fabric attach never runs the extra schema
  query; `schema_filter` is applied, so a function is not declared into a schema the catalog does not surface.
- Registration is per catalog and lazy, so an attach that never calls a function pays nothing. Note the set is
  registered in **every** schema on both providers, so the visible count scales with schema count — measured
  490 entries on `LH` = 70 names (40 functions + 30 dead `_each` siblings) × 7 schemas. Expected, not a defect.

### ⚠ TWO SHIPPED BUGS THIS FOUND, both pre-existing, both invisible to their own live validation

Neither is in the new code. Both were found by *calling* these functions on a fresh path, which is the point
worth carrying forward: **a live validation that checks a call's status and ids is not a validation of its
output columns.**

1. **`fabric_lakehouses()` and `fabric_warehouses()` threw on EVERY call** —
   `IndexOutOfRangeException`. The `workspace :=` override pass (§9g, 2026-07-31) added the `args[0]` read to
   every catalog-bound table function but the `NamedParameters` **declaration** to all except these two, and
   the base sizes the args array from the declared count. So the failure is total and immediate, not a wrong
   default. It landed one day AFTER both were live-validated (§9c/§9e) and their only gate is live, so nothing
   failed in between. An audit of all 21 `FabricRowsFunction` subclasses confirms these were the only two.
2. **Every timestamp on the hand-rolled functions read as JANUARY 1970** — 15 sites across 5 files:
   `fabric_refresh_sql_endpoint` (the flagship), all four job functions, the notebook runner, and both
   semantic-model functions. Cause: `new TimestampArray.Builder()`, whose **parameterless constructor defaults
   to MILLISECOND**, while the columns are declared MICROSECOND by `FabricApiFunctions.Ts`. Nothing anywhere
   reports the mismatch — the array holds millisecond values, the schema says microseconds, and the host
   faithfully reads the number it was given, 1000× too small.
   - **It survived live validation of every affected function**, because each was checked for status and ids
     and nobody looked at the times.
   - **Functions on `FabricRowBuilder` were never affected**: it creates each builder FROM the declared field.
     The fix gives the rest that same property — one `TsType` shared by `Ts()` and the new `TsBuilder()`, so
     the declaration and the builder cannot drift again.
   - Method note: Apache.Arrow 23 honours the unit correctly when told it (probed, all four units), so the
     library was ruled out before the code was blamed; and the corrected values corroborate independently
     (`2026-07-12/13/14` are exactly when those probe tables were created), rather than merely "looking bigger".

### Gates

- **`verify_functions` 13 → 15** (service tier): the negative control — a box SQL Server attach advertises no
  `fabric_*`. Its companion assertion (`cf_*` count > 0) is the **positive control** and is the point of it:
  a zero for `fabric_*` means "not advertised" only if something else IS advertised through the same path.
  **Mutation-tested** — forcing `IsFabricEndpoint` to `true` kills the suite at exactly that line.
- Live: the ATTACH above against workspace `Test` / lakehouse `LH`; `fabric_lakehouses()` (3 lakehouses with
  their SQL endpoint connection strings) and `fabric_refresh_sql_endpoint()` (19 tables) both through it.
- **Still ungated live**: everything that needs a real tenant. The positive path here is manual, like
  `verify_dax` — only the negative control can run in CI.
- Full tiers after the change: hermetic **63/63 — 5656**, service **44/44 — 1446** (1444 + the two new).
  (Hermetic is **5664** as of the zero-argument-scalar work later the same day — §9c.)

### Still open

`fabric_*` on a **non-Fabric** SQL Server remains deliberately unavailable, and the shortcut SCALARS still take
no `workspace :=` / `item :=` (DuckDB `ScalarFunction` has no named-parameter concept — §9c), so on a SQL
attach they always act on the ATTACH's own `item`.

## 9i. SDK pin 2.14.0 → 2.18.0 (2026-08-02)

Bumped to track latest. **Nothing was forced by it and nothing changed because of it** — recorded so the
next reader does not have to re-derive that.

- Compiles clean on `net10.0` and `net8.0`, no source change anywhere; four minor versions with no break
  in the surface we use.
- **The two absences the design RESTS on were re-probed on 2.18.0, with controls**, because both are
  statements about "the pinned dll" and the pin moved:
  - **`exitValue` / `ExitValue` — still absent** ⇒ the raw-HTTP call in `FabricApiClient` stays necessary
    (§9d); a bump does not retire it.
  - **`RefreshSemanticModel` / `EnhancedRefresh` / `RefreshSchedule` — still all absent** ⇒ semantic-model
    refresh stays on the **Power BI REST** surface (§9f). This is the one that would have silently
    invalidated a whole design decision had it changed.
  - Controls: `RefreshSqlEndpointMetadata` (positive, 2 hits) and a nonsense symbol (negative, 0). Without
    those, four zeros are equally consistent with "probed the wrong file".
- Live re-verified on the new pin through a Fabric SQL attach, across all three surfaces the set spans:
  Fabric core (`fabric_warehouses`, `fabric_items` 20, `fabric_capacities` 2), OneLake
  (`fabric_list_shortcuts` 4), and Power BI REST (`fabric_semantic_models` 5), plus the `item :=` override.
- The deployed assembly was checked (`FileVersion` = 2.18.0.0), not just the csproj — a publish that
  silently kept the old dll is the failure this catches.

## 9j. VARIABLE LIBRARIES — BUILT + LIVE-VALIDATED (2026-08-03). One defect found live; CREATION is refused for an SP

Ten functions over `FabricClient.VariableLibrary.Items`, which the pinned 2.18.0 SDK already carries (no new
dependency). A variable library is Fabric's per-environment configuration item: a default value set plus
alternative sets, with exactly ONE active at a time, flipped per stage by a deployment pipeline.

**Why it belongs here rather than being one more wrapped API:** an `ItemReference` variable stores exactly a
`{workspaceId, itemId}` pair, which is what our own `workspace :=` / `item :=` overrides consume. So a dbt
project can read its target lakehouse from the library instead of hardcoding it, and
`fabric_refresh_sql_endpoint(item := fabric_variable('cfg','target') ->> 'itemId')` composes.

### The shape that decides the design

**There is no effective-value API.** The typed model stops at `VariableLibraryProperties.ActiveValueSetName`;
every value lives in the item DEFINITION as base64 parts. Resolution is ours: decode `variables.json` for the
defaults, decode `valueSets/<name>.json` for the sparse overrides, overlay by name. Same shape as
`fabric_notebook_parameters`.

**⚠ `GetVariableLibraryDefinition` is a LONG-RUNNING OPERATION** (it takes `timeoutInMinutes`, default 60),
like `GetNotebookDefinition`. Every read here costs one; every per-variable write costs TWO (see below).

**⚠ The definition API is WHOLE-DOCUMENT.** `UpdateVariableLibraryDefinition` replaces every part, so a write
that sends only the part it changed **deletes the value sets and the settings**. Every setter therefore reads
all parts, replaces one, and sends them all back. **⚠ And there is no ETag/If-Match**, so that read-modify-write
is LAST-WRITER-WINS — two concurrent `fabric_set_variable` calls on one library can lose one change.
`fabric_set_variables_json` exists as the single-call declarative alternative that cannot interleave with
itself.

### The functions

| | kind | |
|---|---|---|
| `fabric_variable_libraries([workspace := …])` | table | cheap list + `active_value_set` |
| `fabric_variables(library [, value_set := …] [, workspace := …])` | table | resolved rows: `name, type, value, value_json, is_overridden, value_set, note` |
| `fabric_variable_value_sets(library [, workspace := …])` | table | sets in declared order, which is active |
| `fabric_variable(library, name)` | scalar | one value through the ACTIVE set |
| `fabric_create_variable_library(name, description)` | scalar | → new id |
| `fabric_set_variable(library, name, type, value)` | scalar | declare/replace a default |
| `fabric_set_variables_json(library, variables_json)` | scalar | replace the whole default set in ONE write |
| `fabric_set_variable_override(library, value_set, name, value)` | scalar | override in a set, creating it |
| `fabric_set_active_value_set(library, value_set)` | scalar | properties update — cheap, not a definition write |
| `fabric_drop_variable_library(library, if_exists)` | scalar | |

**Writes are SCALARS, matching the shortcut CRUD**, for a reason worth keeping: `FabricRowsFunction`
stringifies every argument, so a BOOLEAN named parameter on a table function would silently read as NULL —
the half-offered-capability class this codebase keeps finding. Scalars take typed positional arguments.

**⚠ Every write function must stay VOLATILE (the default).** A CONSISTENT function is constant-folded at plan
time, which for a write means it may run at bind, run once for a hundred rows, or be elided. The read-side
`fabric_variable` is the opposite case — see below.

### `fabric_variable` is declared CONSISTENT, and that is load-bearing

Our scalar default is VOLATILE (`IScalarFunction.IsVolatile => true`; an absent `fabricator.volatile` tag
reads as volatile in `fabricator_metadata.cpp`). Left at the default, `fabric_variable` would run **once per
row** of whatever it was selected over, each row an LRO. Declared CONSISTENT:

- `SELECT fabric_variable('cfg','x') FROM big_table` folds to a literal in the optimizer —
  `BoundFunctionExpression::IsFoldable()` is exactly `stability != VOLATILE`.
- As a table-function argument it is evaluated once **regardless** of volatility: `bind_table_function.cpp`
  checks only `IsScalar()` and then calls `EvaluateScalar(..., allow_unfoldable: true)` at bind.
- Consequence to document: folding is plan-time, so a PREPARED statement bakes the value into its cached plan
  and will not see a later change. Right for configuration, surprising if unexpected.
- The varying-argument shape (`fabric_variable('cfg', name) FROM t`) still can't fold, so the implementation
  dedupes by library within the argument batch: one definition read per distinct library, not per row. There
  is deliberately no cache ACROSS calls — that needs a staleness policy, and this is configuration people
  expect to be able to change.

**A value set is not reachable from the scalar, on purpose.** A variable name is not bound to a value set; the
library has one active set and that is what every other consumer resolves through. Reading a different set is
inspection, served by `fabric_variables(…, value_set := …)`. It cannot be offered on the scalar anyway —
DuckDB scalars have no named parameters and match arity exactly, so a third positional argument would break
the two-argument call.

### ⚠ Microsoft's own pages contradict themselves in four places

Each of these is a silent-wrong-answer if guessed, and each is pinned by the offline harness:

1. **The value-set folder is spelled BOTH ways** — the parts table says `valueSets\valueSetName.json`, the
   payload example says `valueSet/valueSet1.json`. We READ either (and normalize `\`), WRITE plural.
2. **`type` casing is not stable** — the same example has `"String"` and lowercase `"boolean"`. Types are
   passed through VERBATIM and matched case-insensitively; no closed enum, because…
3. **…the REST page's type table omits `Guid` and `ConnectionReference`**, which the concept page lists. Any
   closed list would reject a legitimate type. An unrecognized type parses as JSON, falling back to a string;
   the service validates against the real type, which is the backstop that makes leniency safe.
4. **`VariableOverride.value` is typed `String` in the schema table, and that is wrong.** An `ItemReference`
   override is an object — and mutation-testing showed it is wrong for **Integer** too, so it is wrong for
   every non-string type, not merely the advanced ones. Overrides keep the raw `JsonElement`.

Also: variable names are **not case sensitive**, so both the read overlay and the write upsert match that way
— an upsert that didn't would append a second entry under different casing and invalidate the library.

### ⚠ CREATION is refused for a service principal; everything else is allowed

**`CreateVariableLibrary` → `FeatureNotAvailable`** (with a request id) for our `fabric_sp`, which **directly
contradicts the documentation**: *"The variable library REST APIs support service principals."* Same pattern
as `ResetShortcutCache` (documented as supported, refused in practice) and the same error code this tenant
returns for notebook creation.

**The scope is now settled, not inferred.** An empty library created by another identity, then driven entirely
by the SP: the feature is plainly available on the tenant, and `UpdateVariableLibraryDefinition`,
`UpdateVariableLibrary`, `GetVariableLibraryDefinition` and `ListVariableLibraries` are all permitted. So the
refusal is **principal-scoped and specific to creation**, exactly as with notebooks (`UpdateItemDefinition` is
allowed there too). ⇒ **creating the library is a one-time human/portal action; everything after it automates.**
`fabric_create_variable_library` stays shipped and is proven WIRED in the `fabric_reset_shortcut_cache` sense —
it reaches the service and returns the service's own error.

⚠ Note the error code **misreports the cause**: `FeatureNotAvailable` reads as "the tenant does not have this",
which is false here. Do not diagnose it from the message.

### Verified LIVE (workspace `Test`, 2026-08-03)

Full lifecycle through `scratchpad/varlib_live2.sql`, against a library created outside the SP:

- **Bulk declare** (`fabric_set_variables_json`, 5 variables) then read back with **types intact**: `500` and
  `1.1` and `true` unquoted, `"dev"` quoted, the `ItemReference` an object, the `note` carried.
- **Per-variable `fabric_set_variable`** returned `created` and the count went 5 → 6 — i.e. its whole-document
  read-modify-write **preserved** the other five, which is the failure mode that would silently destroy a
  library.
- **`fabric_set_variable_override`** created a `prod` set implicitly; `fabric_variable_value_sets` then showed
  it with `override_count = 2`, proving `variables.json` and the settings survived every definition write.
- **Resolution**: explicit `value_set := 'prod'` overrode exactly the two variables and left four at their
  defaults with `is_overridden = false`; `batch_size` came back `50000` **unquoted**, so the override went in
  typed from the DECLARATION rather than as a string.
- **`fabric_set_active_value_set('prod')`** then made the no-argument read and the scalar both resolve to
  `prod` / `50000`.
- **The point of the feature**: `fabric_variable(…,'target_lakehouse') ->> 'itemId'` equals `LH`'s real item id.
- **Three negative controls all errored** rather than answering plausibly — unknown value set (naming the known
  ones), an override of an undeclared variable, and `'not-a-number'` for an Integer — and the library was
  **unchanged (still 6)** afterwards, so a rejected write does not partially apply.

### ⚠ The defect live validation found: stored formatting leaked into the value

An object-valued variable came back as
`{\r\n        "workspaceId": "…",\r\n        "itemId": "…"\r\n      }` — literal newlines and indentation
inside a SQL column. Cause: **`JsonElement.GetRawText()` returns the raw SOURCE SPAN**, so however the document
was formatted is what the caller receives. Fixed by re-serializing through a `Utf8JsonWriter`
(`VariableLibraryFormat.Compact`), verified live on the same stored document (102 chars, no CR/LF, still
resolves).

Two things make this worth recording. It is a **read-side** bug, not an artifact of our writer — the portal, a
git sync, or any other producer may pretty-print, so normalizing belongs on read. And **the offline round trip
could not catch it**, because `ToJsonString()` emits compact JSON: the harness was writing and reading its own
formatting convention. A round trip only tests the shapes you generate. A pretty-printed-input check is now in
the harness.

### ⚠ Cost, measured

The full 13-step script took **7m39s** for roughly 15 definition operations — so a read is tens of seconds and
a per-variable write (GET + PUT, both LROs) is worse. Consequences: use `fabric_set_variables_json` to declare
several variables in one write, and remember each `fabric_variable()` call in a SELECT list is its own
definition read (step 8's two columns were two reads). This is the price of "no cache across calls"; it is the
right default for configuration, but it is not cheap.

### Verified offline — `dotnet/Fabricator.Bridge.Tests`, the format's committed gate

**47 test cases × {net10.0, net8.0}, ~100 ms.** Includes a **write → read round trip** (build documents with
the write helpers, decode them with the reader), which is what proves the two halves agree rather than each
being self-consistent — and, separately, the pretty-printed-storage case, because *the round trip is
structurally blind to formatting defects*: it serializes compactly, so it reads back its own convention. Each
of the four documentation contradictions has a named test, so a future "simplification" that trusts the docs
fails here and says which page misled it.

**Mutation-tested, three mutants, each killed at exactly the tests written for it:**

| mutant | killed |
|---|---|
| accept only the `valueSets` folder spelling | 6, incl. both `valueSet/…` theory cases and the "reads every value set" control |
| override `value` read as String only (what the docs say) | 4, incl. the Integer case |
| `Render` back to `GetRawText()` | 2, the pretty-printed and object-value tests |

**⚠ THE PROJECT HAS NO `ProjectReference` TO `Fabricator.Bridge`, AND ADDING ONE WOULD BREAK TIER 0.** The
Bridge project-references **engineered-wood, a git submodule**, plus Arrow, the Fabric SDK, SqlClient and the
AWS/Azure SDKs — and tier 0 exists precisely because it needs no C++, no vcpkg and no submodules. So the test
project **compiles selected Bridge source files directly**, with one admission rule:

> a Bridge file belongs in that project only if its closure is the .NET base class library.

That is a forcing function, not a workaround: it rewards keeping decision logic (parsing, resolution,
rendering) out of the Arrow/SDK boundary, which is exactly what makes it testable. `FabricVariableLibraryFormat.cs`
was split out of `FabricVariableFunctions.cs` for this reason. A file that cannot be admitted is telling you
its logic is entangled with I/O.

**Wired into TIER 0** as a second job (`bridge`) in `installer-core.yml` — floor 47, both TFMs × both OSes,
no submodules. It is a separate JOB rather than another step because the count tripwire reads the first
`Total:` in its output file, so a second `dotnet test` into the same file would leave the floor silently
unchecked; and the path filter covers `dotnet/Fabricator.Bridge/**` rather than the single linked file, so it
cannot stop covering a newly linked one.

The end-to-end path remains gated only by the manual live script, like `verify_dax`.

## 10. The full API sweep — every area, with a verdict

The complete Fabric REST surface (Core services + workload APIs), each with implement/defer/skip and
the reason. **Every group below was presence-verified in the then-pinned 2.14.0 dll** (the pin is now
2.18.0; the two verdict-bearing absences — `ExitValue` and semantic-model refresh — were re-probed on it
with controls and both still hold, §9i) (namespace/operation
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
| Capacities | list | **P3 ✅ BUILT** (`fabric_capacities`) — also answers "is this workspace on a Fabric capacity", which decides whether an enhanced semantic-model refresh is permitted at all. Capacity ASSIGN: **skip** |
| Items (generic) | list/get; CRUD; get/updateDefinition; item connections | **P2 list/get** (`fabric_items`). Definition GET: **P1 for notebooks only** (§4). Generic CRUD/updateDefinition: **skip** (rule 2; `scratchpad/fabricnb` proves updateDefinition works SP-driven if ever needed) |
| Job Scheduler | run on demand / cancel / get instance / list instances | **P0/P1** (§4/§5) |
| Job Scheduler — item schedules | CRUD of cron/daily schedules | **skip for now** — in a dbt flow, dbt IS the scheduler; revisit only on demand |
| Long Running Operations | get state / get result | **P2** (`fabric_operation_status`) — the generic peek for `wait_seconds => 0` flows |
| OneLake Shortcuts | create / get / list / delete / reset cache | **P0/P1** (§4/§5) |
| OneLake Data Access Security | list / create-or-update roles | read: **P3 ✅ BUILT** (`fabric_data_access_roles`) — role scoping is a common cause of "the table is there but I see no rows", and this is the only way to see one from SQL. Rule constraints are summarized as counts, not projected (§9g). write: **skip** (rule 1 — folder-security policy from SQL) |
| External Data Shares | create / list / revoke | **skip** — cross-tenant sharing is an admin/governance act, not a data-flow step |
| Connections | list / get; CRUD; supported types | **P2 LIST** (`fabric_connections` — the `connectionId` feeder for external shortcut targets). CRUD: **skip** (rule 1 — connection credentials) |
| Deployment Pipelines | list / stages / deploy | **P3 ✅ BUILT** — `fabric_deployment_pipelines` / `_stages` / `_items` / `fabric_deploy` / `_operations`. Whole-stage deploy only; pipeline/stage CRUD + role assignment + workspace-to-stage assignment stay **skip** (§9g) |
| Git | status / commit / update-from-git / connect | **P3 ✅ BUILT** — `fabric_git_status` / `_connection` / `_commit` / `_update`. `Connect`/`Disconnect` and the credential calls stay **skip** (rule 1 — they carry a PAT). Reading and advancing an existing connection is in; establishing one is not (§9g) |
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
| Semantic Model | list; CRUD/definition; refresh | refresh: **✅ BUILT BOTH WAYS** — REST enhanced refresh as `fabric_refresh_semantic_model` (§9f) and TMSL/XMLA as `dax_refresh` / `_table` / `_partition` (§9g). list: **✅ BUILT** (`fabric_semantic_models`). CRUD: **skip** |
| Environment | list/get; publish | **P3 ✅ BUILT** (`fabric_environments`) — exposed rather than resolved inline, because `publish_state` is itself worth reading: a notebook run against an environment still publishing does not get its libraries. PUBLISH: **skip** (meaningless without the library-definition writes rule 2 excludes) |
| Spark (pools, workspace settings) | — | **skip** — compute infra admin |
| Mirrored Database | CRUD, start/stop mirroring, status | **READS ✅ BUILT** (the WATCH fired) — `fabric_mirrored_databases` / `fabric_mirroring_status` / `fabric_mirrored_tables`; `last_sync_time`+latency let a model assert its source is caught up before reading it, and `onelake_tables_path` is a path a Delta ATTACH can point straight at. CRUD + start/stop: **skip** — reconfiguring someone else's ingestion is not a transformation's business |
| Report, Dashboard, Dataflow, Eventstream, Eventhouse, KQL Database/Queryset, ML Model/Experiment, GraphQL API, SQL Database, Mounted Data Factory | — | **skip** — not in the DuckDB data path; nothing a SQL function adds over the portal/their own tooling |
