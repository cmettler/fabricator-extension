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
  `localhost` marker → auto-detect the local PBI port (Windows-only, newest workspace's `msmdsrv.port.txt`) →
  `Data Source=localhost:<port>`; any other target is passed through verbatim as an ADOMD connection string
  (SSAS / Fabric / AAS). Token/Entra auth via a secret is a later slice (`BuildConnectionString` returns the
  base target for now).
- **DAX is read-only:** all write paths throw; `BEGIN/COMMIT/ROLLBACK` are no-ops so a wrapping DuckDB
  read-only transaction doesn't fail.

## Slice plan

1. **Project + `dax` provider + connection modes + ATTACH** — **DONE + validated.** `ATTACH 'pbidesktop://'
   AS pbi (TYPE mssql_net, PROVIDER 'dax')` connects to the live local instance; `GetMetadata(Schemas)` =
   model name(s) from `TMSCHEMA_MODEL`, so the model shows as a DuckDB schema. SqlServer unregressed; unknown
   provider errors cleanly listing `sqlserver, dax`. (TYPE is still `mssql_net` — the generic `arrownet`
   rename is deferred to when this provider matures.)
2. **DMV metadata → catalog** — `GetMetadata` Tables (`TMSCHEMA_TABLES`) + Columns (`TMSCHEMA_COLUMNS` + the
   DAX→Arrow type map). Multi-database SSAS = multiple catalogs/schemas; PBI Desktop = one model.
3. **Table scan** — `ScanTable` → `EVALUATE SELECTCOLUMNS('T', "Col", 'T'[Col], …)` projection; column-name
   de-bracketing; `AdomdDataReader`→Arrow. No filter pushdown initially (DuckDB re-filters).
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
- **Multi-instance local PBI** — slice 1 picks the newest workspace's port; a way to target a specific open
  file (by window title / workspace) is a later nicety.
- **The generic rename** (`TYPE arrownet`, `arrownet_query`/`_exec`) — do it once the DAX provider is real,
  per the "Next up" thread in CLAUDE.md.
