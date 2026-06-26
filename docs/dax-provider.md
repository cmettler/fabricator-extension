# DAX / Analysis Services provider (the second provider)

> Status: **in progress.** Slice 1 (project + `dax` provider + connection modes + ATTACH) **DONE + validated**
> against a live local Power BI Desktop instance. This is the "one binary, many providers" goal made real:
> a second `IBackend` (`ArrowNet.AnalysisServices`) hosted by the same arrownet core + bridge, reached via
> `ATTACH … (TYPE mssql_net, PROVIDER 'dax')`. Reference for the design: the old Arrow-Flight server
> `D:\repos\SqlServerFlights` (`Airport/Flights/SemanticModel/*`, `Airport/Catalogs/SemanticModelFlightCatalog.cs`).

## Why it fits arrownet with almost no C++

A semantic model maps onto the existing catalog/scan/function machinery; the new work is **a C# provider**,
not C++:

| DAX concept | arrownet home (reused) |
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

- **`ArrowNet.AnalysisServices`** (new project) — `DaxBackend : IBackend` (provider `"dax"`, aliases
  `adomd`/`powerbi`/`ssas`/`fabric`) + `DaxCatalog : IBackendCatalog` + `PowerBiDesktop` (port detection).
  References `ArrowNet.Bridge` + `Microsoft.AnalysisServices.AdomdClient`.
- **Discovery:** `BackendRegistry` loads the assemblies in `ARROWNET_BACKEND_ASSEMBLY` (default now
  `ArrowNet.SqlServer,ArrowNet.AnalysisServices`; a missing assembly is skipped). SqlServer loads first → it
  stays the default provider, so existing `mssql_net` ATTACHes (no `PROVIDER`) are unchanged.
- **Publish:** `publish-managed.ps1` publishes both providers into the same `arrownet/` dir (Bridge +
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
   AS pbi (TYPE mssql_net, PROVIDER 'dax')` connects to the live local instance; `GetMetadata(Schemas)` =
   model name(s) from `TMSCHEMA_MODEL`, so the model shows as a DuckDB schema. SqlServer unregressed; unknown
   provider errors cleanly listing `sqlserver, dax`. (TYPE is still `mssql_net` — the generic `arrownet`
   rename is deferred to when this provider matures.)
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
   contradiction → 0). Set `ARROWNET_DAX_DSN` (e.g. `pbidesktop://`), `ARROWNET_DAX_TABLE` (a quoted table
   ref), `ARROWNET_DAX_COL` (a column with some NULLs); it runs against any tabular model and skips
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
4. **`daxeval(expr, params)`** — `IArrowTableFunction.Bind` executes + reads `GetSchemaTable()` for the output
   schema (stashing the reader), streams rows in `Execute`.
5. **`daxevaltable` / `daxapply` in-out** — `IArrowInOutFunction.DoExchange` (DATATABLE injection / per-row
   param binding).
6. *(later/optional)* limited filter pushdown into DAX `FILTER`/`CALCULATETABLE`; Fabric/AAS token auth via a
   secret + XMLA endpoint connection mode (cross-platform validation).

## Open questions / deferred

- **Cross-platform AdomdClient** (Linux) for the Fabric XMLA path — validate when a Fabric target is wired.
- **Fabric/AAS auth** — access-token / Entra via a provider secret (reuse the azure-secret work, §2.1 of
  provider-extensibility.md). Slice 6.
- **Multi-instance local PBI** — autodetect picks the newest *listening* workspace port (across all
  editions); a way to target a specific open file (by window title / workspace) is a later nicety.
- **The generic rename** (`TYPE arrownet`, `arrownet_query`/`_exec`) — do it once the DAX provider is real,
  per the "Next up" thread in CLAUDE.md.
