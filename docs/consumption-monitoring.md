# Consumption / CU monitoring on Fabric — what is reachable, and how to attribute it to a dbt run

**Status: ANALYSIS, measured live 2026-07-31. Nothing built.** The question that prompted it: can a dbt run —
a whole DAG, or one model — be monitored for consumption / CU cost, and can a Warehouse query be *tagged* so it
can be found again in monitoring?

**Short answer.** Yes to tagging, and it works better than Microsoft documents it. Per-**statement** attribution
is fully available inside the Warehouse (`queryinsights`, 30-day history, with a `label` column we control and
`allocated_cpu_time_ms` per statement). Per-statement **CU seconds** exist only in the Capacity Metrics semantic
model, which is XMLA-only — reachable through our own DAX provider — and joins back to `queryinsights` on a
documented key. The gaps that decide the design are: **a service principal generally cannot see capacity data
at all**, **our bulk write path is not a labellable T-SQL statement**, and **Warehouse CU metering is reported
to be changing in August 2026** in a way that weakens per-query attribution as a costing basis.

Everything below marked **MEASURED** was run against the live `Test` workspace / `Test Warehouse` through this
extension. Everything marked **REPORTED** comes from third parties and could not be confirmed in Microsoft's
own documentation — the distinction matters here, because one of those items would change the recommendation.

---

## 1. The three layers, and what each can actually attribute

| layer | grain | has CU? | reachable from SQL today | retention / latency |
|---|---|---|---|---|
| `queryinsights.*` views (Warehouse + SQL analytics endpoint) | one row per completed **statement** | ✗ (CPU ms, not CU) | ✅ **yes, now** — `fabricator_query('wh', 'SELECT … FROM queryinsights.exec_requests_history')` | 30 days; up to 15 min ingestion |
| Fabric **Capacity Metrics** semantic model | per operation, per 30 s timepoint | ✅ **`Total CU (s)`**, `Timepoint CU (s)` | ⚠️ via **XMLA → our `dax` provider**, if installed + permitted | ~14 days in-app; minutes' latency |
| Fabric **REST API / SDK** | — | ✗ **nothing** | — | — |

### 1.1 The SDK has no consumption surface at all — MEASURED

Reflected over the pinned `Microsoft.Fabric.Api` 2.14.0 (`dotnet run usage` in `scratchpad/fabricspike`), with
controls so an empty result could not be a filter typo:

- No `MetricsClient`, no `UsageClient` (negative controls: absent, as expected).
- `CapacitiesClient` exists (positive control: present) but offers only `GetCapacity` / `ListCapacities` — no
  metrics, no utilization.
- `FabricClient.Admin` exposes Domains, ExternalDataShares, Items, Labels, SharingLinks, Tags, Tenants, Users,
  Workloads, Workspaces — **no Capacities client and no activity-events client**.

So there is no REST route to CU. The Capacity Metrics *semantic model* is the API.

### 1.2 `queryinsights` — the per-statement layer, and it is genuinely good

Enumerated live on the Fabric Warehouse. Five views exist: `exec_requests_history`, `exec_sessions_history`,
`frequently_run_queries`, `long_running_queries`, and **`sql_pool_insights`** (pool pressure / resource
percentage — not in older write-ups).

`exec_requests_history` carries, per statement: `distributed_statement_id`, `label`, `program_name`,
`statement_type`, `status`, **`allocated_cpu_time_ms`**, `total_elapsed_time_ms`, `row_count`,
`data_scanned_remote_storage_mb` / `_memory_mb` / `_disk_mb`, `result_cache_hit`, `sql_pool_name`, `query_hash`,
`batch_id`, `root_batch_id`, `session_id`, `connection_id`, `submit_time`/`start_time`/`end_time`, `command`
(full text), `error_code`.

Caveats that matter:
- **User-context statements only** — system-generated work is excluded from the views, yet is still *billed*.
  So `queryinsights` can never sum to the invoice.
- **`data_scanned_*` excludes intermediate data movement**, and is `0` for `COPY INTO`.
- The docs type `total_elapsed_time_ms` as `int`; `sys.columns` reports `bigint`. Harmless, but trust the engine.

---

## 2. Tagging a query — CONFIRMED, and broader than documented

The user's hypothesis was right. Two independent tagging vectors exist, and they attribute at different grains.

### 2.1 `OPTION (LABEL = '…')` — MEASURED on all five statement shapes

Microsoft documents `label` as *"Optional label string associated with **some `SELECT`** query statements"*.
That understates it. Emitted one labelled statement of each shape against a real user table, then read the
history back:

| shape emitted | `statement_type` recorded | label recorded? | `allocated_cpu_time_ms` |
|---|---|---|---|
| `CREATE TABLE … AS SELECT … OPTION (LABEL=…)` | **`SELECT INTO`** | ✅ | 1365 |
| `INSERT INTO … SELECT … OPTION (LABEL=…)` | `INSERT` | ✅ | 545 |
| `UPDATE … OPTION (LABEL=…)` | `UPDATE` | ✅ | 577 |
| `DELETE … OPTION (LABEL=…)` | `DELETE` | ✅ | 302 |
| `SELECT … OPTION (LABEL=…)` | `SELECT` | ✅ | 143 / 22 |

So the label covers **the entire dbt statement set**, not just reads. `varchar(8000)`, so a whole JSON tag
object fits. Note CTAS surfaces as `SELECT INTO`, which is what to filter on.

**Two false leads worth recording, because each looked like a finding.** A first CTAS attempt failed with
*"The data type 'sys.sysname' … is not supported"* and a second with *"references an object that is not
supported in distributed processing mode"* — both from selecting out of `sys.objects`, not from the hint. An
**unlabelled control failed identically**, which is what isolated it; without that control the conclusion would
have been "Fabric rejects LABEL on CTAS", which is wrong.

### 2.2 `Application Name` → `program_name` — MEASURED, and the better run-level vector

Adding `;Application Name=fabricator-attr-probe` to the connection string made every statement of that session
appear with `program_name = 'fabricator-attr-probe'`. Without it, everything reads
`Core Microsoft SqlClient Data Provider` — indistinguishable across runs.

This is the more attractive vector for **run-level** attribution because it needs no SQL rewriting: one
connection property tags every statement the run issues, including the ones the extension generates internally
**and the bulk load itself** (§2.3), which no query hint can reach. Measured over the probe hour, the tag
partitions the history cleanly: `Core Microsoft SqlClient Data Provider` 114 statements (untagged sessions),
`fabricator-attr-probe` 35, `fabbulk-…` 31.

> **⚠ DEFECT FOUND: our `application_name` secret field is declared but never applied.**
> `SqlServerBackend.SecretFields` declares `application_name`, and `BuildMssqlConnectionString` never emits it —
> so `CREATE SECRET (TYPE mssql, …, application_name 'x')` accepts the value and silently drops it. That is why
> the live `program_name` was the SqlClient default. Confirmed by reading the builder and by the measurement.
> Fixing it is a two-line change and is the single cheapest step toward run attribution.

### 2.3 One CTAS is SEVEN billable statements — MEASURED, and it reframes the cost question

A single `CREATE OR REPLACE TABLE wh.dbo.t AS SELECT * FROM local_src` (5 000 rows) through the extension's
bulk path produced these history rows, all carrying the session's `program_name`:

| # | `statement_type` | command (truncated) | cpu_ms | rows |
|---|---|---|---|---|
| 1 | `COND` | `IF OBJECT_ID(N'dbo.t','U') IS NOT NULL …` | 0 | 0 |
| 2 | `COND` | `IF OBJECT_ID(N'dbo.t','U') IS NULL …` | 0 | 0 |
| 3 | `CREATE TABLE` | `CREATE TABLE [dbo].[t] ([id] BIGINT NULL, …)` | 0 | 0 |
| 4 | `SELECT` | `select * from [dbo].[t]` | 0 | 0 |
| 5 | `EXECUTE PROC` | `exec ..sp_tablecollations_100 N'[dbo].[t]'` | 0 | 0 |
| 6 | **`BULK INSERT`** | `insert bulk [dbo].[t] ([id] BigInt, [nm] …)` | **1017** | 5000 |
| 7 | **`INSERT`** | **`COPY INTO [Test Warehouse].[dbo].[t] (…)`** | **770** | 5000 |

Rows 4–5 are SqlBulkCopy's own metadata handshake; row 7 shows Fabric implementing the bulk load internally as
`COPY INTO`. **So the write IS visible and IS attributable** — which corrects the natural assumption that a
SqlBulkCopy load is opaque to `queryinsights`.

Two things follow. First, **`queryinsights` will always show many more statements than the user wrote**, so any
per-model roll-up must aggregate by tag rather than count statements. Second, **catalog and DMV queries are
billable** (Microsoft states this explicitly), and this extension issues a lot of them during ATTACH and
discovery — a real cost item on Fabric, not merely latency.

Related, measured but with an inferred cause: a single `fabricator_query` call with one labelled `SELECT`
produced **two** history rows carrying that same label, both with the full row count (143 ms then 22 ms). The
catalog scan path instead probes cheaply with `SELECT … WHERE 1 = 0` (17 ms) before its real scan. The doubling
is measured; that it comes from `fabricator_query` re-executing the statement to resolve its schema — having no
describe available for arbitrary SQL — is inference and should be confirmed before acting on it.

### 2.4 What Fabric tags on its own — MEASURED, unexpected

Every session shows two statements we do **not** issue (grepped the whole repo to be sure):

```
EXEC sp_set_session_context 'root_activity_id', '<guid>'
EXEC sp_set_session_context 'fabric_submitter_name', '<app…>'
```

Fabric's own SQL frontend sets these. `root_activity_id` is a correlation id into Fabric's monitoring, so it is
a pre-existing join handle we did not have to invent — worth investigating before building anything bespoke.

---

## 3. CU seconds: the Capacity Metrics model, and the documented join key

`queryinsights` gives CPU milliseconds, not CU. CU lives in the **Capacity Metrics** semantic model, whose
`TimePointBackgroundDetail` / `TimePointInteractiveDetail` tables carry `Total CU (s)`, `Timepoint CU (s)`,
`Duration (s)`, `Operation`, `Item`, `Workspace`, `User`, `Billing type`, `Status`, `OperationStartTime`/`EndTime`.

**The join key is documented, which is the important part:** the app's **`Operation Id` IS the
`distributed_statement_id`**, and Microsoft's own billing page tells you to use it against
`queryinsights.exec_requests_history` and `sys.dm_exec_requests` for end-to-end traceability. That closes the
loop:

```
label (ours)  →  queryinsights.exec_requests_history.distributed_statement_id
              →  Capacity Metrics "Operation Id"  →  Total CU (s)
```

And because the metrics model is a **semantic model reachable over XMLA**, our own `dax` provider can attach it
and query it with `daxeval` — meaning per-model CU attribution needs **no new extension feature**, only the
two ends joined in SQL.

Constraints on that path, in descending severity:

1. **A service principal usually cannot see capacity data — MEASURED here.** `fabric_capacities()` (live,
   newly built) returned two capacities: `Premium Per User - Reserved` (PP3) and a Trial (`FT1`). The `Test`
   workspace's own `capacity_id` (`763ce0dd-…`) is **not either of them** — the SP cannot see the capacity it is
   running on. Same shape as the `fabric_connections()`-returns-0 finding: capacities carry their own role
   assignments. So a dbt-run cost monitor driven by an SP will be blocked by *permission*, not by wiring.
2. **The Capacity Metrics app must be installed, and its workspace shared.** This tenant's SP sees exactly
   **1 workspace**, and no metrics-app workspace among them — so the CU half could not be validated here at all.
   Installing it requires capacity admin.
3. **XMLA read must be enabled** on the capacity (Semantic models workload → XMLA endpoint), a third gate
   distinct from the two the semantic-model refresh work already documented.
4. **Warehouse operations are `background`, so CU is smoothed over 24 hours.** "What did this DAG run cost" must
   read `Total CU (s)` for the operation, never the timepoint CU — the timepoint value is a slice of a 24-hour
   smear and will look absurd.
5. Latency of a few minutes; ~14 days retention in the app.

---

## 4. ⚠ The metering change that could invalidate per-query costing — REPORTED, NOT CONFIRMED

Multiple third-party sources state that **from August 2026, Warehouse CU consumption moves from per-query CPU
time to a per-workspace "virtual node" allocated-time model**: a virtual node being a 4-core unit, the rate
changing from 2 CU/vCore to 0.53 CU/vCore, with a **one-minute minimum per workspace, rounded up**.

**I could not confirm this in Microsoft's own documentation.** The official Data Warehouse billing page (last
updated 2026-03-03) still describes the per-statement model, and a search restricted to `learn.microsoft.com`
and `blog.fabric.microsoft.com` found no such announcement. Treat it as a credible rumour with a specific date,
not as fact — **and verify it before building anything that costs money to build.**

Why it matters so much here: if billing becomes *allocated node-time per workspace*, then

- `allocated_cpu_time_ms` per statement stops being proportional to cost;
- a **one-minute minimum, rounded up**, makes many small dbt models cost the same as one large one, so
  per-model cost attribution becomes actively misleading;
- the useful question changes from "which model burned the most CPU" to "how long did this workspace hold
  Warehouse compute, and how well did we pack the DAG into it" — which favours measuring **wall-clock
  concurrency of a run**, not per-model CPU.

Per-model *performance* attribution (the `label` work) keeps its value either way. Per-model *cost* attribution
is what is at risk.

---

## 5. The lakehouse / Spark side

Different workloads, different meters — a dbt project writing Delta to OneLake does not bill as Warehouse:

- **`OneLake Compute`** — charged for all OneLake reads and writes, so our Delta provider's traffic lands here.
  There is no per-statement view equivalent to `queryinsights`.
- **Spark / notebook runs** — `fabric_job_instances(item)` (already shipped) gives run history with status and
  timings; Spark's own monitoring gives resource usage. A notebook's CU shows in the metrics app as its own
  operation.
- **Workspace monitoring (opt-in)** creates an Eventhouse + read-only KQL database in the workspace, with
  `ItemJobEventLogs` and semantic-model / Eventhouse / GraphQL log tables, queryable by KQL **or SQL**. It is
  execution logging rather than CU, and it is itself billable.
- `queryinsights` also exists on a lakehouse's **SQL analytics endpoint**, so a T-SQL read of a lakehouse table
  is attributable exactly like a Warehouse query.

---

## 6. What this means for our extension — recommendation

Ordered by value-per-effort. Nothing here is built.

**(a) Fix `application_name` (tiny, do it regardless).** Emit the declared field into the connection string,
and accept it as an ATTACH option too. Immediately gives run-level attribution via `program_name` with zero
SQL changes, and it is a live defect in a documented surface either way.

**(b) A `mssql_query_label` setting (small, high value).** A provider setting whose value the extension appends
as `OPTION (LABEL='…')` to the T-SQL it generates. dbt then labels per model with a pre-hook —
`SET mssql_query_label = 'dbt:{{ this.name }}'` — and `queryinsights` becomes queryable per model. This is the
piece that turns tagging from "possible in hand-written SQL" into "usable from dbt", because **a dbt model body
is DuckDB SQL: the user never writes the T-SQL that reaches Fabric, so only the extension can attach the label.**

> **Scope limit, measured — and it is narrower than it first appears.** INSERT/CTAS through this extension use
> **SqlBulkCopy**, which takes no query hint, so a *label* cannot cover the write. But the write is **not
> invisible**: it is fully recorded and fully attributable by `program_name` (§2.4). So the division of labour
> is clean — **`Application Name` covers everything a run does, `OPTION (LABEL)` adds per-statement grain to the
> subset that is generated T-SQL** (scans, rowid UPDATE/DELETE, DDL, `fabricator_exec` text).

**(c) `fabricator_query_history()` — a convenience read (small).** A catalog-bound table function over
`queryinsights.exec_requests_history` with sensible defaults (last N hours, this login, optional label filter).
Pure sugar over SQL a user can already write, so build it only if the label work lands.

**(d) CU joining — DON'T build until §4 is resolved and the metrics app is reachable.** The join is expressible
in SQL today with the existing `dax` provider plus `queryinsights`; a dedicated function would add little, and
if the August-2026 change is real, per-statement CU may cease to exist as a concept. Revisit after verifying.

**(e) Our own statement count is a cost item — investigate before optimizing anything else.** §2.3 measured one
CTAS expanding to seven billable statements, and Microsoft states plainly that catalog and DMV queries are
billable. Two concrete leads: `fabricator_query` appears to execute its statement **twice** (measured; cause
inferred), and ATTACH-time discovery issues a metadata query per table — the same enumeration cost already
documented as a *latency* problem against OneLake is also a *billing* problem against a Warehouse. That reframes
the existing "lazy table enumeration is infeasible" note: it was closed on latency grounds, and cost is a second
argument for the cheaper-materialization mitigation already sketched there.

---

## 7. Reproducing the measurements

The probes are ad-hoc (in the session scratchpad, gitignored) but trivially rebuilt: attach the Warehouse and
read `queryinsights` through `fabricator_query`. The shape that produced §2.1:

```sql
-- emit, tagged
ATTACH '<warehouse connstr>;Application Name=myrun' AS wh (TYPE fabricator);
SELECT fabricator_exec('wh', 'CREATE TABLE dbo.t AS SELECT id, nm FROM dbo.src OPTION (LABEL = ''run42-modelA'')');

-- read back (allow up to 15 minutes)
SELECT * FROM fabricator_query('wh',
  'SELECT statement_type, label, status, allocated_cpu_time_ms, total_elapsed_time_ms, row_count
     FROM queryinsights.exec_requests_history
    WHERE label LIKE ''run42-%'' OR program_name = ''myrun''
    ORDER BY submit_time');
```

**Two method notes, both of which cost a wrong answer during this analysis.** (1) A poll that breaks on the
first non-empty result will happily return a *previous* run's rows when the filter includes a shared
side-channel like `program_name` — filter on the run's own tag and keep waiting for it. (2) Ingestion latency is
real and variable (observed anywhere from ~1 to ~12 minutes; documented as up to 15, and it grows with
concurrency), so an empty result proves nothing until a positive control shows the mechanism working.

All probe tables (`dbo.fabprobe_src` / `_ctas` / `_bulk` / `_plain`) were dropped afterwards; nothing was left
on `Test Warehouse`. The history rows they produced remain readable for 30 days, which is itself convenient for
re-reading these measurements without re-running them.

**One live-validation side effect worth recording:** `fabric_capacities()` — built in the previous pass and
listed there as wired-but-unvalidated — was exercised for real here and works (§3, constraint 1). It also
produced the finding that the SP cannot see its own workspace's capacity, which no amount of code review would
have revealed.
