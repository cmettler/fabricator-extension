# DAX / Analysis Services provider (the second provider)

> Status: **in progress.** Slice 1 (project + `dax` provider + connection modes + ATTACH) **DONE + validated**
> against a live local Power BI Desktop instance. This is the "one binary, many providers" goal made real:
> a second `IBackend` (`Fabricator.AnalysisServices`) hosted by the same fabricator core + bridge, reached via
> `ATTACH … (TYPE fabricator, PROVIDER 'dax')`. Reference for the design: the old Arrow-Flight server
> `D:\repos\SqlServerFlights` (`Airport/Flights/SemanticModel/*`, `Airport/Catalogs/SemanticModelFlightCatalog.cs`).

## Why it fits fabricator with almost no C++

A semantic model maps onto the existing catalog/scan/function machinery; the new work is **a C# provider**,
not C++:

| DAX concept | fabricator home (reused) |
|---|---|
| semantic model = catalog; models→schemas, tables, columns | `IBackend.OpenCatalog` + `IBackendCatalog.GetMetadata` (DMV queries) → the C++ catalog core |
| table scan → `EVALUATE SELECTCOLUMNS('T', …)` | `IBackendCatalog.ScanTable` (projection pushdown; filter best-effort/skip) |
| `AdomdDataReader` → Arrow | `AdomdDataReader : DbDataReader` → the bridge's `DbDataReaderArrowStream` + a DAX→Arrow type map |
| `daxeval(expr, params)` | `IArrowTableFunction.Bind(args)` (the no-describe schema resolution → the v27 binding model) |
| `daxevaltable(expr, <table>)` (DATATABLE inject) | `IArrowInOutFunction.DoExchange` (Phase 6 exchange) |
| `daxapply(expr, <table>)` (per-row params) | `IArrowInOutFunction.DoExchange` (per-row bind) |
| connstr/auth, settings, secret fields | the provider-self-description family (v33/v37/v38) |

So the C++ core, the function machinery, and the provider plumbing are reused verbatim — **no ABI change**
for the read path.

## What the old SqlServerFlights code actually does (analysis)

- **Schema resolution (ADOMD has no result-set describe):** execute the DAX at **bind time**, read the
  schema from `AdomdDataReader.GetSchemaTable()` (column name + CLR `DataType` + `AllowDBNull`), and for the
  simple `daxeval` **stash the open reader+connection** keyed by `(catalog, serialized-params)` to stream
  rows later (`DaxEvalFlight.cs`). The in-out variants execute once with dummy `DBNull` params at bind purely
  to read the schema, discard, then re-execute per row. (There is **no** `TOPN(0)` trick.)
- **Parameter passing — two mechanisms:** (a) ADOMD `command.Parameters.Add(name, value)` named binding
  (`DaxEvalFlight`, `DaxApplyFlight` per-row); (b) **DATATABLE injection** — the input table rendered as
  `DEFINE TABLE _parameter_ = DATATABLE("col",TYPE,{{…}})`, `_parameter_`→`__parameter__`, the user's
  duplicate `DEFINE` stripped, prepended to the DAX (`DaxEvalTableFlight.cs`). Type-aware value formatting
  (`BLANK()`, E-notation doubles, quoted strings, dates `:s`).
- **Filter pushdown is NOT implemented** — the scan does projection only (`EVALUATE SELECTCOLUMNS`); the
  `ticket.Filters.ToSql()` line is commented out (`SemanticModelFlight.cs`). Nothing to port; we build it
  fresh (limited DAX `FILTER`/`CALCULATETABLE`) or skip it and let DuckDB re-filter.
- **Metadata = DMV `SELECT`s** on the ADOMD connection (NOT the UNION-ALL function-declaration trick):
  `$SYSTEM.TMSCHEMA_MODEL/TABLES/COLUMNS` (+ the TOM `DataType` enum) and `$SYSTEM.DBSCHEMA_TABLES/COLUMNS`
  for `$SYSTEM`.
- **Local Power BI Desktop port detection is heavily Windows-only** (process enum of `msmdsrv`, P/Invoke
  `iphlpapi!GetExtendedTcpTable`, WMI parent-process, Win32 window titles). Needed **only** for local PBI
  Desktop — SSAS/Fabric use an explicit connection string.

## Connection round-trip spike (validated)

A throwaway net10 console (`scratchpad/dax-spike`) proved end-to-end against the live local instance:
`Microsoft.AnalysisServices.AdomdClient` **19.96.1** (the plain managed package, **not** `.retail.amd64`
which caps at 19.84.1) loads in net10 (win-x64), connects to local PBI Desktop, `DBSCHEMA_CATALOGS` +
`TMSCHEMA_TABLES` discovery works, `EVALUATE ROW(...)` / `TOPN(3,'T')` / `SELECTCOLUMNS(...)` return rows, and
`GetSchemaTable()` gives the column schema. Findings that shape the design:
- **Column naming:** `EVALUATE 'T'` returns columns as `T[Col]`; `SELECTCOLUMNS` returns `[Col]` (bracketed).
  Both need normalizing to bare names for DuckDB; `SELECTCOLUMNS` lets us control the alias.
- **PBI Desktop catalog is a GUID** → the user-facing schema is the model **name** (`TMSCHEMA_MODEL`).
- **`AdomdDataReader : DbDataReader`** → the bridge's existing `DbDataReader`→Arrow path applies.
- **Cross-platform:** validated on Windows. AdomdClient may have Windows transport deps for *local*
  connections; Fabric/AAS XMLA-over-HTTPS is the likely cross-platform path. Treated **Windows-first,
  Linux-TBD** (the bridge publishes per-RID, so this isn't blocking).

## Architecture

- **`Fabricator.AnalysisServices`** (new project) — `DaxBackend : IBackend` (provider `"dax"`, aliases
  `adomd`/`powerbi`/`ssas`/`fabric`) + `DaxCatalog : IBackendCatalog` + `PowerBiDesktop` (port detection).
  References `Fabricator.Bridge` + `Microsoft.AnalysisServices.AdomdClient`.
- **Discovery:** `BackendRegistry` loads the assemblies in `FABRICATOR_BACKEND_ASSEMBLY` (default now
  `Fabricator.SqlServer,Fabricator.AnalysisServices`; a missing assembly is skipped). SqlServer loads first → it
  stays the default provider, so existing `fabricator` ATTACHes (no `PROVIDER`) are unchanged.
- **Publish:** `publish-managed.ps1` publishes both providers into the same `fabricator/` dir (Bridge +
  SqlClient + AdomdClient + both backend dlls); the CoreCLR host initializes against Bridge's runtimeconfig.
- **Connection modes** (`DaxBackend.ResolveConnectionString`): empty target or a `pbidesktop[://]` /
  `localhost` marker → auto-detect the local PBI port → `Data Source=localhost:<port>`; any other target is
  passed through verbatim as an ADOMD connection string (SSAS / Fabric / AAS). Token/Entra auth via a secret
  is a later slice (`BuildConnectionString` returns the base target for now).
- **Local-instance autodetect** (`PowerBiDesktop`, Windows-only): scans the `msmdsrv.port.txt` workspace
  files across **all editions** — classic (`…\Power BI Desktop\…`), Report Server (`…\Power BI Desktop
  SSRS\…`), and Store/MSIX (`%LOCALAPPDATA%\Packages\Microsoft.MicrosoftPowerBIDesktop*`) — and prefers the
  **newest port that is actually listening** (a 250 ms loopback TCP connect), so a stale port file from a
  closed instance is skipped. Falls back to the newest port file if none verifies. (Replaces the old
  classic-path-only scan that missed the SSRS/Store editions.)
- **DAX is read-only:** all write paths throw; `BEGIN/COMMIT/ROLLBACK` are no-ops so a wrapping DuckDB
  read-only transaction doesn't fail.

## Slice plan

1. **Project + `dax` provider + connection modes + ATTACH** — **DONE + validated.** `ATTACH 'pbidesktop://'
   AS pbi (TYPE fabricator, PROVIDER 'dax')` connects to the live local instance; `GetMetadata(Schemas)` =
   model name(s) from `TMSCHEMA_MODEL`, so the model shows as a DuckDB schema. SqlServer unregressed; unknown
   provider errors cleanly listing `sqlserver, dax`. (TYPE is still `fabricator` — the generic `fabricator`
   rename is deferred to when this provider matures.)
- **System ($SYSTEM DMV) tables** — **DONE + validated.** A curated set of VertiPaq/`$SYSTEM` DMVs is
  exposed as tables under a **`system` schema** in the same catalog (`db.system."TMSCHEMA_TABLES"`, etc.).
  `DaxCatalog.SystemTables` lists them (extend freely): `TMSCHEMA_MODEL/TABLES/COLUMNS/MEASURES/
  RELATIONSHIPS/PARTITIONS/HIERARCHIES`, `DBSCHEMA_TABLES/COLUMNS`, `DISCOVER_STORAGE_TABLES/
  STORAGE_TABLE_COLUMNS/STORAGE_TABLE_COLUMN_SEGMENTS/CALC_DEPENDENCY/OBJECT_MEMORY_USAGE`. The DMV query
  language is very limited, so the scan is a **bare `SELECT * FROM $SYSTEM.<dmv>`** (no `EVALUATE`, no
  projection/filter pushdown — DuckDB applies those above the scan); column discovery uses the same query +
  `GetSchemaTable` (reads no rows). Metadata/scan reuse the model path: `GetMetadata(Schemas)` adds `system`,
  `GetMetadata(Tables)` appends the DMVs (`table_type='SYSTEM TABLE'`), `GetMetadata(Columns)`/`ScanTable`
  branch on the `system` schema. Only DMVs queryable without a restriction `WHERE` belong in the list (all 14
  validated live: e.g. `TMSCHEMA_COLUMNS` 792 rows, `DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS` 4063 — the
  latter >2048 rows, so it also exercises multi-batch DMV streaming).
2. **DMV metadata → catalog** — **DONE + validated.** `GetMetadata(Tables)` = `$SYSTEM.TMSCHEMA_TABLES`
   (under the single model = schema; Power BI's auto `LocalDateTable_*`/`DateTableTemplate_*` filtered out).
   `GetMetadata(Columns)` resolves each table's columns by running `EVALUATE TOPN(0, '<table>')` and reading
   `GetSchemaTable()` — the **no-describe** approach (real engine types, no internal RowNumber column, no TOM
   enum guessing). `DaxTypeMap` maps the CLR result types → Arrow (incl. `Decimal`→`Decimal128(p,s)` from the
   schema table's precision/scale, `DateTime`→`Timestamp(ms)`); `DebracketColumn` strips `'T'[Col]`→`Col`.
   Validated against the live model: `SHOW ALL TABLES` lists all 6 tables with correct types
   (`DECIMAL(19,4)` currency, `TIMESTAMP_MS`, `BIGINT`, `BOOLEAN`, `VARCHAR`).
3. **Table scan** — **DONE + validated.** `ScanTable` → `EVALUATE SELECTCOLUMNS('T', "Col", 'T'[Col], …)`
   projection (no projection → `EVALUATE 'T'`); de-bracketed column names match discovery. **Filter pushdown
   DONE** (see below). Validated: full scan, projection, `WHERE`+`ORDER BY`+`LIMIT`, aggregation, exact
   `DECIMAL(38,2)` sums, `TIMESTAMP_MS` min, and `DESCRIBE` — all green against the live model. **Committed
   test: `test/verify_dax.test`** — gated by `require-env` (a live model isn't a portable fixture) and
   asserting **model-agnostic invariants** (non-empty scan; projection preserves row count; `IS NULL` +
   `IS NOT NULL` pushdown partitions all rows = superset-safe/complete; `IS NOT NULL` == `count(col)`;
   contradiction → 0). Set `FABRICATOR_DAX_DSN` (e.g. `pbidesktop://`), `FABRICATOR_DAX_TABLE` (a quoted table
   ref), `FABRICATOR_DAX_COL` (a column with some NULLs); it runs against any tabular model and skips
   otherwise.
   - **Filter pushdown** (`DaxFilterBuilder`): the C++ catalog scan already passes the pushed predicate
     (`spec.Filter` + `filterValues`) to `ScanTable`; we render it into a DAX boolean and wrap the table in
     `FILTER('T', <pred>)` (VertiPaq pushes storage-engine-friendly parts down, iterates the rest in the
     formula engine). Best-effort + **superset-safe** (DuckDB re-applies, so a superset is fine but a subset
     would drop rows). DAX has no parameters → constants are inlined as DAX literals (`"…"`, `TRUE()`,
     `DATE()+TIME()`, invariant numerics); `ArrowValueReader` (promoted to Bridge, shared with the SQL
     provider) turns the `filterValues` batch into CLR values. **Safety gating** (DAX semantics differ from
     SQL): string comparison is case-insensitive, so `=` / `IN` on strings yield a *superset* (safe) but `<>`
     on strings yields a *subset* (also excludes case-variants DuckDB keeps) → NOT pushed; string ordering
     (`<`/`>`) can differ by collation → NOT pushed; so `<>`/`<`/`<=`/`>`/`>=` push only for **non-string**
     values, while `=`/`IN`/`IS NULL`(→`ISBLANK`)/`IS NOT NULL` push for any type. `and` drops unpushable
     children (still a superset); `or` is all-or-nothing (dropping a branch would narrow). Validated against
     the live model: `= 'N/A'` → 9117 (= ground-truth group count), `IS NULL` → 21019, `IN ('N/A')` → 9117,
     a **case-insensitive superset proof** (`= 'n/a'` → 0, DuckDB re-narrows DAX's case-insensitive match),
     and a date range (`>= 1900` → all 13949 non-null, `>= 2999` → 0).
   - **It is TRUE incremental streaming** (`DaxArrowStream`, ≤1 batch buffered) — validated to **10.5M rows**.
     The earlier belief that streaming was impossible in-process (and that `AdomdDataAdapter.Fill` /
     materialization was required) was **WRONG** — see the correction below.
   - **THE ACTUAL BUG (and fix) — never call `AdomdDataReader.Read()` past end-of-data.** `AdomdDataReader.Read()`
     called *after it has already returned `false`* does **not** return `false` again — it **throws**
     `AdomdUnknownResponseException: "The server sent an unrecognizable response"` (at `XmlaClient.ReadEndElementS`
     → `EndRowsetResponseS`, trying to parse the rowset's closing XML). Unlike `SqlDataReader`, it is not
     idempotent at end-of-stream. DuckDB pulls one batch *after* the final (partial) one, so a batched reader
     that returns the partial last batch **without remembering EOF** gets called once more and reads past the
     end → throws. The fix is one line: when the read loop sees `Read() == false`, set a `_done` flag and
     never call `Read()` again (return the partial batch, then `null` on the next pull). See `DaxArrowStream`.
   - **Why the months of wrong theories.** The failure was misread as "fails on the 2nd ~2048-row chunk" and
     blamed on in-process Arrow-import interleaving / hosting topology. Instrumenting the stream
     (max-in-flight + per-call thread id + rows-read) disproved all of it: `maxInFlight=1` (no parallel
     access — `MaxThreads()==1` + `get_next` under a mutex), all calls on **one** thread (no hopping), and the
     throw came on the **pull *after* the last data row** (`partialRows=0`), i.e. the read past EOF — not the
     2nd chunk. Everything is consistent: `Fill` and any tight `while(Read())` loop (the inline diagnostic,
     the standalone spike, the old Airport server) work because they **stop at the first `false`**; every
     batched/lazy/materialize/worker attempt failed because it called `Read()` one time too many. It was
     never concurrency, threads, GC, transport, PBI/`msmdsrv` version, AdomdClient version, or process
     topology.
4. **`daxeval(expression := …, params := …)`** — **DONE + validated (incl. parameter binding).** A function
   (under the model schema) that evaluates an arbitrary DAX query — a complete `EVALUATE` / `DEFINE…EVALUATE`
   statement — and returns its result table: `SELECT * FROM db."<model>".daxeval(expression := 'EVALUATE …')`.
   Registered via `GetMetadata(Functions)` with **`kind='proc'`** (not `'table'`) so its args register as
   **named parameters** — that's what lets it take an *optional* second arg without breaking the no-arg call.
   `GetFunctionParamSchema` returns `expression VARCHAR` (required) + `params VARCHAR` (optional). The C++
   table-session path calls `TableBind(schema, func, args)` → `DaxEvalBoundTable`: **bind** resolves the
   output schema by executing the query + `GetSchemaTable` (no rows fetched — the no-describe approach;
   arg-dependent, the columns follow the DAX); **`Execute`** re-runs the query and streams via `DaxArrowStream`.
   `SupportsPushdown = false` (an arbitrary DAX query can't be wrapped — DuckDB projects/filters/aggregates
   above the scan). Trade-off: the query runs at bind (schema, no rows) + once per execution.
   - **Parameter binding** (the old `DaxEvalFlight` mechanism): `params` is a bag of values, each bound as an
     ADOMD `AdomdParameter` the expression references as `@<name>`. **Two accepted shapes (dual-accept):**
     a DuckDB **`STRUCT`** — `params := {'a': 40, 'b': 2}` (type-safe, no quoting — the preferred shape), read
     field-by-field (`ReadStructParams`); or a **JSON string** — `params := '{"a": 40}'` (handy for
     programmatic callers), parsed by `ParseDaxParams` (number→int64/double, string, bool, null→`BLANK`).
     `BindDaxParams` adds them for **both** the bind-time schema probe and each execution; args are read **by
     field name** (named params arrive in arbitrary order).
   - **How the struct crosses with no ABI change**: `params` is declared in `GetFunctionParamSchema` as the
     **`NullType` sentinel** = "accept any value". There's no Arrow type for DuckDB `ANY`, so the host treats a
     `SQLNULL`-typed named parameter as `ANY` (`GetOrCreateTableFunction`), and the shared table-bind
     marshaling keeps the supplied value's **runtime** type for such a param (`FabricatorTableFunctionBind`) —
     so a `STRUCT` literal marshals across as a real Arrow struct instead of being coerced to the declared
     type. The guard is `LogicalTypeId::SQLNULL`-only, so every other function (concrete-typed params) is
     unaffected — full SQL function suite green (procs 24, TVFs 33, scalar 26, custom 85, in-out 63/31/17).
   - Validated live: no-param `EVALUATE ROW(…)` (schema from arbitrary DAX), `COUNTROWS`/`SUMMARIZECOLUMNS`,
     `EVALUATE {1,2,3}`, full-table `EVALUATE 'T'` (multi-batch streaming), **and** parameter binding —
     numeric `@a + @b`, a string `@who`, and a param in a table filter (`FILTER(…, [Value] > @t)`).
     `verify_dax.test` covers it (model-agnostic `ROW`/table-constructor/param cases; needs `FABRICATOR_DAX_SCHEMA`).
5. **`daxevaltable` in-out** — **DONE + validated.** `daxevaltable(<input>, expression := 'EVALUATE …')`
   injects the input table into the DAX as a table named `_input` (`DEFINE TABLE _input = DATATABLE(…)`
   prepended; the expression is a single `EVALUATE` referencing `_input`), evaluates ONCE, returns the
   result. Registered `kind='inout'`; resolved by `InOutBind` → `DaxEvalTableBinding`; the DATATABLE literal
   (`DaxDataTable`: Arrow→DATATABLE type map + value formatting) is built from the input rows, output schema
   probed at bind via a 1-row dummy DATATABLE. **Required wiring the long-deferred cost args through the
   shared exchange** (C++ `fabricator_schema_entry.cpp`): `GetOrCreateCustomInOutFunction` now declares the
   function's constant args as **named parameters** (tolerant `FetchFunctionParamSchema` — empty for a
   no-arg custom in-out like `cf_tag`, so unchanged), and `FabricatorExchangeBind` marshals the supplied named
   params into the `inout_bind` args (else `nullptr` — `_each` declares none, so unchanged). No ABI change.
   **Whole-table limit:** the exchange has no emit-at-end hook (a whole-table op is a pipeline breaker; the
   operator's finalize drain *discards* trailing output), so the result is emitted during the input chunk's
   tenure — the input must arrive in a **single chunk (≤ 2048 rows)**; a larger input errors clearly (the
   intended use is a small parameter/lookup table). Validated live (`EVALUATE _input` echo; `SUMX` over the
   injected table) + `verify_dax.test`; existing in-out suites unregressed (`verify_custom_functions` 85,
   `verify_table_inout` 63, `verify_proc_inout` 31, `verify_inout_isolation` 17).
6. **`daxeach` in-out** — **DONE + validated.** `daxeach(<input>, expression := 'EVALUATE …')` runs the DAX
   once PER INPUT ROW, binding each input column as an ADOMD `@<column>` parameter the expression references;
   output = the DAX result per row (no input echo — reference `@col` to carry input values through). The
   "each" analog of the SQL provider's `_each` (renamed from the old Airport `daxapply`). `DaxEachBinding`
   reuses one connection + command across rows (only the param values change), reads each row's result via
   the shared end-of-data-guarded `ReadBatches`, and emits per chunk — so unlike `daxevaltable` there is **no
   input-size limit** (per-row emit fits the streaming exchange). Validated live (`ROW("sq", @n*@n)` per row;
   a row yielding 3 results → 2 inputs × 3 = 6 rows) + `verify_dax.test`. **The DAX eval/apply function
   family (daxeval + params, daxevaltable, daxeach) is complete.** `verify_dax.test` is at 25 assertions.
7. **Fabric / AAS token auth (Entra)** — **DONE + validated live against two Fabric semantic models.** ADOMD
   has no interactive auth in the CoreCLR host ("interactive authentication is not supported … an external
   access-token is required"), so we mint a Power BI-scoped token (`https://analysis.windows.net/powerbi/api/.default`)
   from an Azure principal and set `AdomdConnection.AccessToken` (with an `OnAccessTokenExpired` refresh
   callback — mirrors how `SqlServerCatalog` sets `SqlConnection.AccessToken`). The principal is the **same
   azure service-principal secret the Fabric Warehouse uses** (reused via the v39 foreign-secret path):
   `ATTACH 'Data Source=powerbi://…;Initial Catalog=<model>' AS m (TYPE fabricator, PROVIDER 'dax', SECRET
   <azure_sp>)` → `DaxBackend.BuildConnectionString` carries the secret fields to the catalog (a connstr
   marker), which builds a `ClientSecretCredential` / `ManagedIdentityCredential` (`DaxTokenAuth`). A
   secretless remote XMLA endpoint falls back to `DefaultAzureCredential` (the "Active Directory Default"
   analog — env / MI / VS / az CLI). New dep: `Azure.Identity`. **Also fixed: honor the explicit `Initial
   Catalog`** — a workspace XMLA endpoint lists MANY models in `DBSCHEMA_CATALOGS`, and we were binding to
   the *first* one (e.g. a lakehouse default model) instead of the requested model, so every metadata/DMV
   query came back empty; now the connection's current catalog wins, auto-discovery only when none is given.
8. *(later/optional)* limited filter pushdown into DAX `FILTER`/`CALCULATETABLE`.

## DirectLake passthrough — read (and maybe write) the underlying store directly (design idea, DEFERRED)

**Validated facts (2026-06-26, live).** Both Fabric models `Test Warehouse Model1/2` are 100% **DirectLake**
(every partition `Mode=5`/`Type=5` = Entity), and every table maps 1:1 to a `dbo.<entity>` object in **one
shared warehouse item** `486cd767-9ea3-4e40-be6c-49824d91d841`. The discriminator is the single source
expression (`TMSCHEMA_EXPRESSIONS`):
- **Model1 = DirectLake on OneLake** — `AzureStorage.DataLake("https://onelake.dfs.fabric.microsoft.com/<workspaceId>/486cd767…")`
- **Model2 = DirectLake on SQL** — `Sql.Database("…datawarehouse.fabric.microsoft.com", "486cd767…")`

The OneLake *item id* equals the SQL *database* — the same warehouse, two source modes. **The bypass is
proven:** `ATTACH 'Server=…datawarehouse.fabric.microsoft.com;Database=486cd767…' (TYPE fabricator, SECRET
<azure_sp>)` and `SELECT count(*) FROM wh.dbo.Trip` returns **2,838,927 rows — identical to the DAX scan of
the model table**. So the DMVs hand us everything to route a DAX model's table scans to the SQL endpoint.

**The idea (and an honest good/bad triage). Build only on demand.**

- **READ passthrough — clearly good, low risk.** At ATTACH of a DAX model, read these DMVs, detect DirectLake
  tables, resolve `(SQL endpoint, database, dbo.entity)`, and route base-table scans to the SQL provider
  (the cheap path) instead of DAX/Vertipaq compute. Identical data (DirectLake reads the same Delta). **Hard
  boundary:** SQL only returns the **base tables** — DAX **measures / calculated columns / calculated tables /
  relationships / RLS** live only in the semantic layer, so those still require DAX. The passthrough is for raw
  table/column extraction, not model-computed results.
- **WRITE to a Warehouse via the SQL provider — good, mostly free.** Our SQL provider already does
  INSERT/UPDATE/DELETE/CTAS/bulk against a Fabric Warehouse (validated). A DirectLake model serves a pinned
  Delta snapshot, so after a write the model must **reframe** to see new rows — Fabric reframes automatically
  (default), or an explicit refresh (XMLA/TMSL `Refresh`, TOM, or the Fabric REST API) forces it. Note: the
  write (warehouse) and the reframe (model) are **two systems → eventually consistent, not atomic**.
- **WRITE Delta directly (engineered-wood / delta-rs) — Lakehouse only, you own correctness.** For a
  *Lakehouse* OneLake item, writing Delta with delta-rs (or an engineered-wood write path — today it's
  read-only in this repo) is legitimate, but you take on the Delta commit protocol: optimistic concurrency,
  schema enforcement, stats/checkpoints, deletion vectors, column mapping. **Do NOT write Delta directly into
  a Warehouse-managed item** (e.g. `486cd767…`) — the Warehouse SQL engine owns those files and external
  writes can corrupt its metadata; write a Warehouse via SQL, write a Lakehouse via Delta. Either way, reframe
  after.
- **ALTER passthrough + sync the semantic model — powerful, high risk, far-future.** ALTER on the warehouse
  table is already a SQL-provider DDL passthrough; but to surface a new/changed column the **model metadata**
  must be edited too (TMSL/TOM over the XMLA *read-write* endpoint). That crosses from "data connector" into
  "model management" — needs model write permission + premium/Fabric XMLA read-write, and a wrong TMSL edit can
  break the model. Treat as opt-in, carefully scoped, only if a clear need appears.

**Net take:** the read bypass and warehouse writes are the safe, high-value 80% and reuse machinery we already
have (SQL provider + the azure-SP token path). Direct Delta writes are a Lakehouse-only specialization, and
automatic model/DDL sync is the speculative, high-blast-radius 20% — document it, don't build it yet.

## XMLA/TMSL REFRESH — BUILT (2026-07-31), and the one TMSL verb we expose

`dax_refresh([type :=] [, objects_json :=] [, max_parallelism :=])`, `dax_refresh_table(table [, type :=])` and
`dax_refresh_partition(table, partition [, type :=])` — catalog-bound table functions on the MODEL schema of a
DAX attach (`DaxRefreshFunctions.cs`). Full as-built record, including the SDK measurements behind it:
[fabric-api-functions.md](fabric-api-functions.md) §9g.

Why it lives here rather than on the Fabric REST side: the Power BI enhanced-refresh API answers "refresh this
model, tell me when it is done" and **cannot address a partition at all**. TMSL over XMLA can, and this provider
already holds an ADOMD connection on exactly the right token — so the split is REST = `fabric_*`,
XMLA = `dax_*`, and `dax_refresh_partition` is the operation that only exists on this side.

Four things a future editor needs to know:

- **Synchronous.** The XMLA command does not return until the refresh completes — no request id, no polling,
  no "in-progress" status to misread. It is cancellable through the same tier-3 `InterruptScope` mechanism as a
  scan, which matters more here: a full refresh runs for minutes, and without it Ctrl+C would leave the
  statement blocked until the engine finished.
- **TMSL's type vocabulary is camelCase and is NOT the REST one** (`full`/`clearValues`/`dataOnly` vs
  `Full`/`ClearValues`/`DataOnly`). Both are accepted case-insensitively; an unknown value is rejected locally,
  because the engine's own answer for a bad type is a generic XMLA parse failure.
- **`refresh` is the ONLY verb exposed, and deliberately so.** The identical `AdomdCommand.ExecuteNonQuery`
  path would run `createOrReplace` or `delete` just as happily. Refresh moves DATA; exposing a generic
  `dax_tmsl(command)` would turn a documented read-only provider into an arbitrary model-mutation surface
  reachable from any SQL string — which is precisely the escape hatch the DEFERRED design below keeps
  proposing, and the reason it stays deferred rather than being quietly smuggled in with refresh.
- **NOT live-validated.** `verify_dax` is a manual gate (Power BI Desktop or a live XMLA endpoint). What was
  verified offline: the provider still resolves and reaches ADOMD (a bogus endpoint gives
  `AdomdConnectionException`, proving the catalog constructs after the `CatalogFunctionSet` rewiring) and the
  hermetic tier stayed green. Treat the refresh path as reviewed-but-unexercised until someone runs it.

Enabling change in the same pass: `DaxCatalog` now hosts a **`CatalogFunctionSet`**, so it can carry
C#-authored catalog-bound functions of any of the six kinds instead of only its three hand-dispatched ones
(`daxeval` / `daxevaltable` / `daxeach`, which keep their bespoke declarations and are still matched by name
first). That is what any future `dax_*` function should plug into.

### TMDL model management — retrieve / apply a model definition (design idea, DEFERRED)

The "sync the semantic model" piece above, fleshed out. The goal: read a model's **TMDL** (Tabular Model
Definition Language — the human-readable text format) and apply edits, through the provider.

**Mechanics.** TMDL is a **TOM** feature (`Microsoft.AnalysisServices.Tabular`): `TmdlSerializer` does TMDL↔
model, `Model.SaveChanges()` applies (TOM emits TMSL under the hood). TOM connects to the **same XMLA endpoint
with the same token** `DaxTokenAuth` already mints — so auth is free, but it needs the **read-write** XMLA
endpoint + model-write permission (premium/Fabric), and TOM is a sizable new dependency. TMSL (a single JSON
`alter`/`createOrReplace`/`refresh` command sent over XMLA) is the lower-level escape hatch under TMDL.

**The design crux — generate is pure, apply is effectful → they take different shapes.** That is fine for
*generating* text but wrong for *applying* a change. Marking the scalar `VOLATILE` (our scalar-UDF path
already does) removes only the value-caching hazards — no **constant-folding**, no **CSE/dedup** — but it does
**not** give the properties a model commit needs, and DuckDB has **no scalar flag** that does:
- **Not exactly-once.** A scalar in `SELECT apply(x) FROM t` runs once per row, and if its result column is
  projected away upstream the optimizer can still **prune** it — `VOLATILE` governs value caching, not
  side-effect *preservation* (SQL assumes scalars are side-effect-free, so even `random()` in an unused column
  is elided).
- **Not serialized.** `VOLATILE` says nothing about concurrency/order — the vectorized executor still runs the
  projection across **multiple threads / morsels in parallel**, so a side-effecting `apply_tmdl(...)` races on
  the model commit (TOM `SaveChanges`/TMSL must be serialized).

The needed properties — *runs, exactly once per intended unit, in order* — aren't expressible on a scalar at
all. They ARE what a source operator gives: a table function is materialized + evaluated once as written, and
the `_each` in-out additionally pins `MaxThreads=1`, serializing commits by construction. So, mirroring
`fabricator_exec` (a table function used for effect), **apply is a table function, never a scalar.**

**Bind-constant vs. table-argument (how to take DYNAMIC input).** A plain table function resolves its
arguments at **bind time**, so an arg must be a bind-constant (a literal, or a scalar folded over literals) —
it cannot reference another relation's column (no "current row" at bind), so `FROM driving d, apply_tmdl(d.x)`
is impossible. DuckDB's general mechanism for *per-row dynamic* table input is the **`in_out_function` with a
`{LogicalType::TABLE}` argument** — i.e. exactly our Phase 6 exchange / `_each`: the dynamic value rides in the
**input table**, not in a scalar argument, and the operator emits output per input row. The enabling
constraint (a relation has ONE schema) is naturally satisfied: an in-out's **output schema is fixed and
resolved at bind** — for `apply` it's a constant `(status, …)` shape regardless of the TMDL coming in. So the
lateral-capable apply is `apply_tmdl_each(<rows>)`; no new "lateral table function" capability is needed — the
`_each` operator IS it, and it serializes the applies (gate `MaxThreads=1` → no racing commits).

**The function family (each in its natural shape; TOM-backed):**
- **`render_tmdl(template, args…)` — scalar (pure):** the templating engine — composes/chains anywhere, runs
  per row. Authored as a custom `ICatalogScalarFunction` (the 4e machinery) — or, connection-free, a global
  `fabricator_render` (see [global-functions.md](global-functions.md)) — or just DuckDB `format`/`concat`.
- **`<model>.tmdl()` — table function (pure read):** scripts the model to TMDL, one row per object/document
  `(path, content)`. `tmdl_of('Table','Sales')` scalar = one object (pure read → scalar is fine here).
- **`<model>.apply_tmdl(text)` — table function (effect, status row):** apply one; arg is a bind-constant, so
  it composes with `render_tmdl(…)` over literals: `FROM apply_tmdl(render_tmdl('tmpl', {…}))`.
- **`<model>.apply_tmdl_each(<rows>)` — table-in-out:** per-row apply — dynamic per-row TMDL from the input
  relation (templating upstream: `apply_tmdl_each(SELECT render_tmdl(…) AS tmdl FROM driving)`), **N commits**,
  serialized (gate `MaxThreads=1`). Use when you want independent/streaming per-row applies.
- **`<model>.apply_tmdl_agg(fragment)` — custom aggregate (4h machinery):** collect many fragments and apply
  them in **ONE atomic commit** at finalize — the best fit for assembling a model edit from many fragments
  (`SELECT apply_tmdl_agg(render_tmdl(…)) FROM edits`; `GROUP BY scope` → one atomic apply per scope). Reuses
  the holistic/collect aggregate pattern (Update collects the fragment string, Combine merges the collections,
  Finalize opens one TOM model + applies all + a single `SaveChanges`). **Side-effect-safe by construction:**
  the collection (Update/Combine) is parallel + pure; the effect is isolated in **Finalize, which DuckDB runs
  exactly once per group, single-threaded** on the merged state — the "runs once, serialized" property a
  commit needs, achieved structurally (vs an unsafe scalar). Vs `_each`: one transaction + one reframe instead
  of N. Caveats: Combine order isn't guaranteed (fine for independent fragments; one `SaveChanges` lets TOM
  resolve the object graph at once); **don't use it windowed** (`OVER` drives finalize per frame → repeated
  apply) — plain / `GROUP BY` only; empty input → no-op. (Note: `apply_tmdl((SELECT string_agg(frag) …))`
  does NOT work — a TVF arg must be a bind-constant, not a runtime aggregate subquery — and a `string_agg`
  + `_each` variant assumes the fragments textually concatenate into one valid TMDL document; the aggregate
  batches at the OBJECT level instead, so they needn't.)
- **`<model>.apply_tmsl(json)` / `<model>.refresh([table])` — table functions:** the raw-TMSL escape hatch +
  the DirectLake reframe companion.

**Build order if pursued:** (1) `tmdl()` reader — low risk, immediately useful (version/diff/CI a model's
definition); (2) `render_tmdl` + `apply_tmdl`/`apply_tmdl_each`/`apply_tmdl_agg` + `refresh` — real editing,
scoped, prefer per-object alter over whole-model `createOrReplace` (and `apply_tmdl_agg` for one atomic
multi-fragment commit); (3) whole-model replace + DDL→model auto-sync — deferred
(high blast radius). Non-atomic with warehouse writes throughout (two systems).

## Open questions / deferred

- **Cross-platform AdomdClient** (Linux) for the Fabric XMLA path — validate when a Linux Fabric target is
  wired (Windows + the token path is validated).
- **Friendly model schema name on Fabric** — `TMSCHEMA_MODEL.Name` is often empty for a Fabric model, so the
  DuckDB schema falls back to the literal `Model` (the GUID/empty isn't user-friendly). Cosmetic; the catalog
  (`Initial Catalog`) is the real model selector.
- **Multi-instance local PBI** — autodetect picks the newest *listening* workspace port (across all
  editions); a way to target a specific open file (by window title / workspace) is a later nicety.
- **The generic rename** (`TYPE fabricator`, `fabricator_query`/`_exec`) — do it once the DAX provider is real,
  per the "Next up" thread in CLAUDE.md.
