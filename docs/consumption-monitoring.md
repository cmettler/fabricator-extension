# Consumption / CU monitoring on Fabric — what is reachable, and how to attribute it to a dbt run

**Status: analysis measured live 2026-07-31; TWO THINGS BUILT out of it** — the `application_name` fix (§2.2)
and `db.dbo.fabricator_session_tag()` (§2.4b, gate `verify_session_tag`). The CU half remains analysis.** The question that prompted it: can a dbt run —
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

> **⚠ DEFECT FOUND AND FIXED: our `application_name` secret field was declared but never applied.**
> `SqlServerBackend.SecretFields` declares `application_name`, and `BuildMssqlConnectionString` never emitted it —
> so `CREATE SECRET (TYPE mssql, …, application_name 'x')` accepted the value and silently dropped it. That is why
> the live `program_name` above was the SqlClient default. Found by reading the builder after the measurement
> disagreed with the declaration; now emitted.

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

### 2.4 Session context — the third vector, and the only one that can change MID-SESSION

`sp_set_session_context` is **explicitly supported on Warehouse and the SQL analytics endpoint**, and
Microsoft's own example B for it is literally *"Set and return a **client correlation ID**"* — so correlation is
the documented intent, not a repurposing. Limits: key ≤ 128 bytes, value ≤ 8 000 bytes (`sql_variant`), 1 MB
total per session, optional `@read_only` to freeze a key for the rest of the connection.

Its appeal over the other two vectors is grain. `Application Name` is fixed when the connection opens, so it
cannot distinguish models within a run unless each model gets its own connection; `OPTION (LABEL)` needs the
statement text rewritten. Session context can be **re-set at any point on a live connection**, so in principle a
dbt pre-hook could stamp the current model onto the pinned connection and every subsequent statement would
inherit it, with no SQL rewriting at all.

**MEASURED — it works, but only within one batch, and that caveat is the whole story:**

| what was run | result |
|---|---|
| `EXEC sp_set_session_context 'dbt_model','…'` then a **separate** `SELECT SESSION_CONTEXT(…)`, inside an explicit `BEGIN`…`COMMIT` | **NULL** — the value was not visible |
| both statements in **ONE batch** through a single `fabricator_query` | **the tag round-trips** (`spid` 52) |
| `SET CONTEXT_INFO <binary>` + `CONVERT(varchar, CONTEXT_INFO())` in one batch | **the tag round-trips** |

So the mechanism is fine on Fabric; what failed is that **two consecutive extension calls did not land on the
same session**, even inside an explicit DuckDB transaction (the two probes reported different `@@SPID`s). Which
connection each call takes needs its own investigation before anything is built on this — the ABI v36 work made
`fabricator_exec` join the model's pinned connection, and a read-only transaction that has not yet pinned a
write connection is the obvious suspect. **Until that is understood, session context is not a usable tag
through this extension**: a tag that silently fails to stick is worse than no tag.

**The two mechanisms are not interchangeable, and only one is monitoring-visible.** `SESSION_CONTEXT()` values
appear in no `queryinsights` column — they are for in-session logic (row-level security is the canonical use),
which is presumably also why Fabric's own `root_activity_id` (§2.5) is set that way and consumed by Fabric's
pipeline rather than exposed to us. `SET CONTEXT_INFO` is the older, cruder mechanism — one unnamed
`varbinary(128)` per session — but `exec_sessions_history` has a **`context_info` column**, and it joins to
per-statement CPU cleanly:

```
SET CONTEXT_INFO '<tag>'  →  exec_sessions_history.context_info + .connection_id
                          →  exec_requests_history.connection_id  →  allocated_cpu_time_ms per statement
```

`connection_id` is a `uniqueidentifier` present on both views, which matters: `session_id` (the spid) is reused
over time, so joining on it alone is ambiguous across a day's history.

**MEASURED — `context_info` really does land there, and the join really works.** A batch that set the tag and
ran one labelled statement produced, after ~6 minutes:

```
session_id | context_info                       | label                  | cpu_ms
        52 | 0x66616263692D31373835343838373830 | fabci-1785488780-work  |      0
```

`0x66616263692D…` is the ASCII of the tag, so the value survived intact and is reachable alongside the
per-statement label and CPU.

> **⚠ Trap, and it nearly produced a wrong conclusion.** `CONVERT(varchar(200), context_info)` yields the
> **hex text** `0x6661…`, NOT the decoded string — so `WHERE CONVERT(varchar(200), s.context_info) LIKE 'mytag%'`
> **never matches**. Compare binary to binary instead:
> `WHERE s.context_info = CONVERT(varbinary(128), 'mytag')`. The row above was found only by the `label` arm of
> the same `WHERE`; without that positive control the measurement would have read as "context_info is not
> recorded", which is the opposite of the truth.

Two limits on that route, both structural: it is **session-grained** (per-model attribution needs one session
per model), and `exec_sessions_history` records **completed** sessions — so under ADO.NET pooling the row does
not appear until the physical connection actually closes, which can be long after the work finished.

**So the two session mechanisms split cleanly, and it is the opposite of what the naming suggests:**

| | `sp_set_session_context` | `SET CONTEXT_INFO` |
|---|---|---|
| read back in-session | `SESSION_CONTEXT(N'key')` | `CONTEXT_INFO()` |
| shape | many named keys, ≤8 KB each | ONE unnamed `varbinary(128)` |
| documented for correlation ids | ✅ explicitly (example B) | ✗ |
| **visible in `queryinsights`** | **✗ no column anywhere** | **✅ `exec_sessions_history.context_info`** |
| what Fabric itself uses | ✅ `root_activity_id`, `fabric_submitter_name` | — |

The modern mechanism has no monitoring *column*; the crude legacy one does. **But that table understates
`sp_set_session_context`, and the correction below is the most useful finding in this document.**

### 2.4a A run UUID in session context is self-bridging — MEASURED end to end, and it needs no new machinery

`SESSION_CONTEXT()` values are in no column, but **the `EXEC sp_set_session_context` call is itself a recorded
statement, and `command` holds its full text** (`varchar(max)`). So setting a UUID *is* the monitoring-visible
marker: find the EXEC by its UUID, take its `connection_id`, and every statement that session ran is attributed.
No registry table, no label, no SQL rewriting — and because every key involved is a GUID, **spid reuse is
irrelevant**, which was the point of choosing a UUID in the first place.

Proven in three steps against the live Warehouse:

1. **A session can read its own monitoring keys.** `sys.dm_exec_requests` is queryable, and
   `SELECT connection_id, dist_statement_id FROM sys.dm_exec_requests WHERE session_id = @@SPID` returns them.
   (`CONNECTIONPROPERTY('connection_id')` returns NULL on Fabric — not available.) `dist_statement_id` is
   especially valuable: it is the Capacity Metrics **`Operation Id`**, i.e. the hop to CU seconds.
2. **Those ids match what monitoring records.** Querying `exec_requests_history` by the `connection_id` a session
   had read about itself returned **all ten** of that session's statements; querying by its `dist_statement_id`
   returned exactly the right one, with the same `connection_id`. The id spaces are shared — an assumption that
   had to be checked, since a DMV reporting different ids than the history would have broken the whole idea.
3. **The end-to-end query works from the UUID alone.** No registry, no label:

```sql
WITH tagged AS (
  SELECT DISTINCT connection_id
  FROM queryinsights.exec_requests_history
  WHERE command LIKE '%sp[_]set[_]session[_]context%<run-uuid>%')
SELECT count(*) AS statements,
       SUM(r.allocated_cpu_time_ms) AS total_cpu_ms,
       SUM(CAST(r.data_scanned_remote_storage_mb AS decimal(18,3))) AS remote_mb
FROM queryinsights.exec_requests_history r
JOIN tagged t ON t.connection_id = r.connection_id;
```

which returned `statements = 10, total_cpu_ms = 21, remote_mb = 0.000` for the probe run — including the
statements the extension issues on its own, which no query hint could have labelled.

**Why this is the best of the three vectors.** It needs no statement rewriting (unlike `OPTION (LABEL)`), it can
change mid-session so per-model grain is possible (unlike `Application Name`, fixed at connection open), it
covers the SqlBulkCopy write, and it reaches CU via `dist_statement_id`. Practical shape: a dbt `pre_hook` runs
`SELECT fabricator_exec('mssql', 'EXEC sp_set_session_context ''dbt_model'', ''…''')`, and the model's own
statements inherit the tag through the connection.

### 2.4b Why `fabricator_exec` cannot do it, and what shipped instead — BUILT

§2.4's "the tag did not stick" was not a fluke, and reading the code explains it exactly:
**`fabricator_exec` runs `AmbientTransaction.JoinOnly`** — it joins the transaction's pinned connection *only if
one already exists*, otherwise it takes a fresh connection it deliberately does **not** retain, because "nothing
would ever commit it". And a provider connection is pinned lazily **on the first WRITE**. So at the moment a dbt
`pre_hook` runs there is nothing to join: the tag lands on a pooled connection that is handed straight back, and
the model's statements then run somewhere else. **A user-level wrapper is structurally incapable of tagging the
connection the work will use** — only the extension can force the pin.

Hence **`db.dbo.fabricator_session_tag(key, value)`** (`SqlServerSessionTag.cs`), a catalog-bound table function
that goes through `BeginWrite()` *without* the join-only restriction — pinning the transaction's connection — then
returns `connection_id`, `session_id`, `dist_statement_id` and the tag it set. Gate: `verify_session_tag`
(25 assertions, service tier).

Four design points, each forced by something measured:

- **It refuses to run in autocommit.** There the pin is committed and released with that very statement, so the
  tag would be set and instantly discarded. The error names the fix ("use a pre-hook on a transactional model").
  A silent no-op is the failure this function exists to remove, so it must be loud.
- **The values are parameters, and therefore the statement carries a comment.** A parameterized EXEC is recorded
  as `EXEC sp_set_session_context @key, @value` with the **values absent** — measured — so §2.4a's
  "self-bridging" property is LOST the moment you do the injection-safe thing. A caller that keeps the returned
  `connection_id` does not care, but a dbt pre-hook discards its result set. So the function appends
  `/* fabricator_session_tag key=value */`, which puts the tag back into `command` while the values stay
  parameters; `*/` is defanged so a hostile value cannot escape the comment.
- **`connection_id` is read from `sys.dm_exec_CONNECTIONS`, not `dm_exec_requests`.** The request-level view
  describes the currently-executing request and intermittently had no row yet, so `MAX()` returned NULL and the
  suite failed at a moving line about one run in three. The connection-level view always has a row for a live
  session. (Box returns several rows per spid — MARS maps many logical connections onto one session; Fabric,
  without MARS, returns one.) The id read is also wrapped: losing a *diagnostic* must not fail a tag that was
  actually set.
- **The ambient transaction id is captured at BIND and re-established at EXECUTE.** `AmbientTransaction` is an
  `AsyncLocal`, and a table function's scan can run on a DuckDB worker thread where it reads 0 — measured as
  roughly one call in six wrongly reporting "not in an explicit transaction", and worse, the tag would have gone
  to a throwaway connection. Same fix `begin_bulk` already uses for its background consumer. **This is the
  transferable lesson: any catalog function that must reach the transaction's connection has to capture the
  ambient id at bind.**

**Validated live on Fabric, end to end.** Tagging a transaction and then writing a model in it, the connection the
function reported carried the *whole* write: `CREATE TABLE`, `BULK INSERT` (862 cpu_ms, 2000 rows), the internal
`COPY INTO` (829 cpu_ms) and the metadata scans — 24 statements on one `connection_id`. That is the design
working: **one tag per transaction attributes everything the model did, including the bulk load no query hint can
reach.**

### 2.4c ⚠ RUN THROUGH dbt — the pre-hook DOES share the transaction, but the tag is NOT reliable at `--threads > 1`

Measured in `dbt_mssql_test` (box target, dbt 1.11.11 / dbt-duckdb 1.10.1). The model body reads
`SESSION_CONTEXT()` back out of the provider, so the model itself reports whether its own pre-hook reached it.

**Single model, single hook statement: it works.** `hook_shares_model_transaction = true` — the tag the pre-hook
set was visible to the model's body. So the mechanism is right and §2.4b's premise holds.

**At `--threads 4` over four models it is not dependable.** Three consecutive runs of the same four models:

| run | models | had a tag | tag matched THIS run | distinct connections |
|---|---|---|---|---|
| 1 | 4 | 4 | **4** | 3 |
| 2 | 4 | 4 | **0** | 4 |
| 3 | 4 | 4 | **0** | 4 |

Every model saw *a* tag; in two of three runs **none of them was its own run's**. A two-hook variant also lost a
per-model tag outright (`model_tag` NULL for one model) while only three connections served four models.

**This is worse than no tag**: a stale value silently attributes a run's cost to a *previous* run rather than
failing. So per-model — and even per-run — attribution via session context **must not be recommended for dbt**
until it is understood.

**The mechanism is NOT established, and I am not going to guess it as fact.** Two suspects, both checkable:
pooled connections retaining context within one dbt process (though a control showed `sp_reset_connection` DOES
clear it for plain pooled reads — three fresh reads returned `<none>`), and DuckDB transaction ids being reused
within a run so `_txns` state is picked up by a later model. The second would be a genuine defect in our
per-transaction connection keying and is worth a targeted look.

**What to use instead, and it is immune by construction: `application_name`.** It is a connection-STRING
property, so every connection a run opens carries it and it cannot go stale or be inherited from an earlier run
— exactly the failure above. The cost is that it is fixed per secret/attach, so it gives **run**-level
attribution, not per-model. Combined with `OPTION (LABEL)` for statement grain (§2.1), that is the combination
this analysis actually ends up recommending for dbt.

`fabricator_session_tag` remains correct **as specified and gated** — one transaction, one connection, verified
by `verify_session_tag` — and is useful for scripted single-connection work. It is the dbt-at-concurrency case
that it does not safely serve.

### 2.5 What Fabric tags on its own — MEASURED, unexpected

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

**(a) Fix `application_name` — DONE.** `BuildMssqlConnectionString` now emits the declared field, so
`CREATE SECRET (TYPE mssql, …, application_name 'dbt:<run>')` gives run-level attribution via `program_name`
with zero SQL changes. It was a live defect in a documented surface either way. (Accepting it as an ATTACH option
too remains open.)

**(b) A `mssql_query_label` setting — NOT NEEDED for the dbt case, and here is why.** The idea was to append
`OPTION (LABEL='…')` to generated T-SQL because a dbt model body is DuckDB SQL and only the extension can attach
a per-model label. §2.4b shipped a better answer: a session tag attributes **every** statement of the
transaction, including the SqlBulkCopy load that a query hint cannot reach at all. A label is still the only way
to distinguish statements *within* one connection, so keep this on the shelf for that case rather than deleting
it — but it is no longer on the critical path.

**(b2) Connection identity — RESOLVED, and it produced the shipped function.** The answer was that
`fabricator_exec` is join-only and cannot pin, so `fabricator_session_tag` exists to do it (§2.4b). One small
follow-up remains: confirm in `dbt_mssql_test` that a real dbt pre-hook runs inside the model's transaction.

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

**Method notes — each of these cost a wrong answer during this analysis, and they are the same rule in four
disguises.** (1) A poll that breaks on the first non-empty result will happily return a *previous* run's rows
when the filter includes a shared side-channel like `program_name` — filter on the run's own tag and keep waiting
for it. (2) Ingestion latency is real and variable (observed ~1 to ~12 minutes; documented as up to 15, and it
grows with concurrency), so an empty result proves nothing until a positive control shows the mechanism working.
(3) Two failed CTAS attempts looked like "Fabric rejects `OPTION (LABEL)` on CTAS" and were actually unsupported
source types — an **unlabelled control failing identically** is what isolated it. (4) The `context_info` filter
silently never matched because of the hex conversion above, and only the label arm in the same `WHERE` revealed
that the value *was* there. In every case the fix was the same: **assert a positive fact you already know to be
true in the same query, so a zero can be distinguished from a broken probe.**

All probe tables (`dbo.fabprobe_src` / `_ctas` / `_bulk` / `_plain`) were dropped afterwards; nothing was left
on `Test Warehouse`. The history rows they produced remain readable for 30 days, which is itself convenient for
re-reading these measurements without re-running them.

**One live-validation side effect worth recording:** `fabric_capacities()` — built in the previous pass and
listed there as wired-but-unvalidated — was exercised for real here and works (§3, constraint 1). It also
produced the finding that the SP cannot see its own workspace's capacity, which no amount of code review would
have revealed.
