# The `adbc` provider (experimental, opt-in)

A provider that loads a **native ADBC driver** through the ADBC C interface and talks to it in Arrow and
Substrait, so the remote pushdown built for SQL Server (docs/duckdb-2.0-remote-execution.md §3–§5) can serve any
driver that accepts Substrait plans. First driver: [mssql-adbc](https://github.com/clast-project/mssql-adbc)'s
NativeAOT build. Kusto ADBC is the obvious second.

```sql
ATTACH 'driver=D:/repos/mssql-adbc/artifacts/native-win-x64/adbc_driver_sqlserver.dll;adbc.mssql.server=localhost,1433;adbc.mssql.database=TestDB;username=sa;password=…;adbc.mssql.trust_server_certificate=true'
    AS a (TYPE fabricator, PROVIDER 'adbc');
```

`driver` names the library (`entrypoint`, default `AdbcDriverInit`, its init function); every other key is a
database option handed to the driver unchanged.

**Status: experimental, not in a release payload.** `scripts/publish-managed.ps1 -IncludeAdbc` publishes it; CI only
compiles it (`.github/actions/build-extension`), because every test needs a native driver and a server. There is no
known use case yet; it is kept in main so a change to the contract it implements breaks the build rather than an
unmaintained branch.

| ATTACH option | default | meaning |
|---|---|---|
| `max_in_list` | 1000 | longest IN list a filter sends as a list; longer goes as a range (integers, temporals) or not at all |
| `consistent_reads` | false | the user's promise that one statement reads the same rows alike twice (RCSI / snapshot, or no concurrent writers); without it a plan carrying `fabricator.repeated_reads` is declined |

Drivers tried (both forks, branches pushed, no PR):

| driver | fork / branch | notes |
|---|---|---|
| mssql-adbc | [cmettler/mssql-adbc](https://github.com/cmettler/mssql-adbc) `fabricator-tsql` | our T-SQL emitter ported in; `artifacts/native-port-win-x64` |
| Kusto (Fabric eventhouse, ADX) | [cmettler/kusto_adbc](https://github.com/cmettler/kusto_adbc) `fabricator` | Arrow 23 / ADBC 1.0, `Prepare` + `ExecuteSchema`, a NativeAOT project `KustoAdbc.Native`, `GetStatistics` (row counts from `.show tables details`); options `adbc.kusto.endpoint`, `adbc.kusto.database`, `adbc.kusto.access_token` (a bearer token, not refreshed) |

## How it works

| piece | how |
|---|---|
| discovery | `GetObjects(depth = tables)` for the connection's current catalog (`adbc.connection.catalog`); the default schema from `adbc.connection.db_schema` |
| table schema | `GetTableSchema` |
| scan | a Substrait plan of one `ReadRel` (named table, full base schema, projection mask) built in C# (`SubstraitReadPlan`), with the host's filters as its `best_effort_filter` (see below). Limits are not sent. Substrait has no unsigned integers, so `uint8/16/32` are declared as the next wider signed type |
| filters | the provider declares `superset_filter_pushdown`, so the host hands every scan its filters (a hash join's runtime keys among them) and re-applies them. `SubstraitFilter` turns the filter tree (`FilterNode` + its value batch) into Substrait expressions: comparisons, IS [NOT] NULL, AND/OR, IN as a `SingularOrList` (longer than ATTACH option `max_in_list`, default 1000: its range for integers and temporals, else left out — a driver that writes the list as literals would build a statement the server refuses only when it runs; the `fabricator-tsql` port sends a long list as one OPENJSON array, so it takes `max_in_list 100000`). A scan sends them as `ReadRel.best_effort_filter`; a pushed query that takes a join's keys (way B) gets a `FilterRel` around its root (`SubstraitPlanFilter`) and keeps the columns the spec projects. Whatever the driver refuses at `Prepare` is left out, conjunct by conjunct; `Fabricator.Adbc` (Debug) logs how many went and the plan's size |
| statistics | the table's row count from ADBC `GetStatistics` (approximate). Without it DuckDB takes a remote table for one row, builds the join's hash table over it, and no join key reaches the driver |
| remote pushdown | `remote_translate` hands over the host's Substrait plan: the provider sets it on a statement, calls `Prepare()` (a driver refuses there, before anything runs) and `ExecuteSchema()`, keeps the plan under an opaque handle and returns the handle as the query. `ExecuteQuery(handle)` runs the plan. Result columns are renamed to `c0…` by position; no ORDER BY keys are declared, so the driver's row order is kept |
| connections | one ADBC connection per statement (a driver connection is not thread-safe) |
| writes | not in this slice (refused) |

Nothing on the C++ side changed: the ABI v96 contract already lets a provider return any query text for a plan,
and ABI v99 hands a pushed query its table filters.

### Filters, measured (2026-11-05)

200 000-row table on the docker SQL Server, compared with DuckDB over a local copy; the server's rows from
`sys.dm_exec_query_stats`:

| query | rows the server returned |
|---|---|
| `SELECT id, g … WHERE id < 10 OR id > 199995` (plain scan) | 14 (was 200 000) |
| `… WHERE g = 'g7' AND v > 100` | 1 184 |
| `… WHERE g > 'g8' AND id < 500` | 499 — string ordering is not sent by the host (no `string_order_pushable`) |
| `loc JOIN t ON t.id = loc.k`, `loc` 4 rows (runtime keys on a scan) | 3, `WHERE id IN (5, 77, 199999)` |
| `loc JOIN (SELECT id % 1000 AS b, max(v) … GROUP BY 1) s ON s.b = loc.k` (keys on a pushed aggregate) | 2, `… ) AS [t1] WHERE [t1].[_g0] IN (…)` |

A long list (28 572 join keys, `SET dynamic_or_filter_threshold = 100000`, `max_in_list 100000`): one 226 KB plan,
all keys sent and rendered as OPENJSON by the port (docs/duckdb-2.0-remote-execution.md §4.31). The statement time
did not move with it (2.8 s with the list, 2.8 s with the range): the cost sits outside the server — local DuckDB
takes 0.5 s for the same query, the SQL Server provider 1.5 s. Not chased yet; suspects are the 28 572-column value
batch the host builds for the constants and the driver translating the plan twice (Prepare, then execute).

All answers equal DuckDB's. The 139 edge cases are unchanged: 139/139 with the ported driver (51 pushed), and the
same 8 differences as before with `main`. Not measured: the fallback when a driver refuses a conjunct.

## Measured so far (2026-11-05)

The 139 pushdown edge cases (`scripts/pushdown_edge_cases.py`, `EDGE_ADBC_DRIVER=<driver library>`) against
DuckDB evaluating the same rows. With mssql-adbc unchanged (`main`, `590a2d0`): 42 pushed, **8 wrong answers**:

| class | cases | upstream's view |
|---|---|---|
| trailing spaces ignored (`'abc' = 'abc  '`) | distinct trailing, group trailing, distinct case, intersect, union, join string key | **documented as accepted** (`SqlServerSubstrait` remarks, dialect `metadata.semantics`) |
| `avg` of a decimal cut to scale 6 | avg dec | a bug |
| ROLLUP on an empty input loses the grand total | rollup empty | a bug |

The provider changes no answer by itself: every difference is the driver's translation.

With our emitter ported into the driver (branch below): **139/139 agree, 51 pushed** — exactly the 51 our own
SqlServer provider pushes, none fewer.

## Limitations (2026-11-05)

What works: everything the host pushes as a plain query plan — aggregates, joins, windows, set operations, and the
correlated subqueries and laterals DuckDB turns into joins before translation (the scalar subquery as `OUTER APPLY`,
CTEs inlined). The driver translates those with the same emitter as our SqlServer provider.

What does not, and why. The driver turns a plan into T-SQL with `SqlServerSubstrait.Translate`
(`SqlServerStatement.cs`), while our SqlServer provider calls `TranslatePushdown` and hands it what it knows about the
server. Everything in that hand-over is missing on the ADBC path:

| # | gap | effect |
|---|---|---|
| 1 | **the plan hint `fabricator.repeated_reads` is ignored by the driver — the PROVIDER now declines such a plan** (unless `consistent_reads`) | without that, **wrong answers were possible.** When DuckDB inlines a CTE at two places, the server reads the same rows twice; under READ COMMITTED with concurrent writers the two reads can differ (measured: 28 wrong NULLs in 8 runs, docs/duckdb-2.0-remote-execution.md §4.16). The SqlServer provider declines such a plan unless the reads are consistent (RCSI, snapshot); the ADBC path runs it. No suite catches it — the edge cases have no concurrent writer. Fix belongs in the provider: it sees the hint and can decline in `TranslateRemotePlan` |
| 2 | **catalog functions** (scalar UDFs, TVFs, a TVF per row in a `CROSS APPLY`) | stay local, or do not bind at all: the provider declares no functions (ADBC `GetObjects` lists none), so `a.dbo.f(x)` is unknown to DuckDB; and the driver's emitter has no callback saying a function runs on the server, so it refuses `extension:fabricator:catalog_function`. Possible: the provider reads the routines through the driver with SQL, and since every catalog function of an ADBC catalog is a server function, the driver could write `[schema].[name](…)` directly |
| 3 | the plan hint `fabricator.single_join_any_row` is ignored by the driver — the provider now declines such a plan | DuckDB takes any row itself; before, the driver failed where DuckDB answers |
| 4 | no declared lengths of string columns | equality keys are `varchar(max)`, which SQL Server cannot hash: string joins and GROUP BY are slower, answers unchanged |
| 5 | server features assumed | IGNORE NULLS, DATETRUNC, LTRIM/RTRIM with characters (SQL Server 2022) and OPENJSON (compatibility level 130) are taken as present; an older server fails when the query runs, not at Prepare |
| 6 | columns the emitter must not compare (`sql_variant` and similar) are unknown | the driver may translate a plan the SqlServer provider declines |
| 7 | a TVF per row (`FROM t, a.dbo.tvf(t.a)`) | not a Substrait path at all — it is the lateral session (ABI v79), which this provider does not implement |
| 8 | writes | the provider is read-only: INSERT / UPDATE / DELETE / MERGE / CTAS are refused |
| 9 | long IN lists | sent as a list up to `max_in_list` (default 1000), else as a range or not at all; a driver that writes a list as literals fails on a large one only when it runs |
| 10 | per-statement cost outside the server | a scan with 28 572 keys took 2.8 s where the SqlServer provider took 1.5 s (see above); not investigated |

### The way to close them

Our SqlServer provider has its own copy of the same code (`dotnet/Fabricator.SqlServer/Substrait/`, namespace
`Clast.Drivers.SqlServer.Substrait`) and calls `SqlServerSubstrait.TranslatePushdown` in-process
(`SqlServerBackend.cs`), answering its callbacks from what it knows about the server. The mssql-adbc port carries
`TranslatePushdown` too, but `SqlServerStatement` calls `Translate`, and the ADBC C interface cannot carry callbacks.

So the driver calls `TranslatePushdown` itself and answers the callbacks over its own connection — it can look up
nearly all of it:

| callback / fact | the driver's source | closes |
|---|---|---|
| consistent reads (`fabricator.repeated_reads`) | `sys.databases` (`is_read_committed_snapshot_on`, `snapshot_isolation_state`) and the session's isolation level; without them the plan is refused at Prepare | 1 |
| `fabricator.single_join_any_row` | read by `TranslatePushdown` from the plan itself | 3 |
| declared string lengths, column SQL types, uncollatable columns | `sys.columns` / `sys.types` for the tables the plan reads | 4, 6 |
| SQL Server 2022 functions, OPENJSON | `SERVERPROPERTY('ProductMajorVersion')`, `compatibility_level` | 5 |
| catalog functions | every function of an ADBC catalog is a server function: answer `[schema].[name]` once the routine exists (`sys.objects`) | 2 (driver half) |

The provider only switches it on: a statement option meaning "translate this plan as a DuckDB pushdown" — needed
because `TranslatePushdown` reads our `fabricator.*` hints and follows the pushdown's conventions (result columns
`c0…`, ORDER BY through hidden key columns), which a plain ADBC client does not expect.

What stays for the provider: declaring the server's routines to DuckDB (gap 2, host half), read through the driver
with an SQL query. Gaps 7–10 are outside this.

## Kusto (Fabric eventhouse), measured 2026-11-05

Eventhouse with two tables (`Stocks` 749 597 rows, `Logs` 214 769), the Fabric service principal's token. Schema =
the database (a GUID in Fabric): `k."93181802-…".Stocks`. Eleven queries, each compared with a local copy both ways:
all equal. Pushed as KQL: GROUP BY with count/sum/min/max, `LIKE 'A%'` (a range plus `matches regex`), ORDER BY …
LIMIT (`sort … | take`), `count(DISTINCT)`, a scalar subquery, a self-join, a TIMESTAMPTZ bound (converted to UTC). A
filtered scan sends its filter (`| where ((['Ticker'] == 'MSFT') and (['Close'] > real(50.0)))`), and with
`GetStatistics` a join with three local keys sends them (`| where (['Ticker'] in ('KO', 'IBM', 'PEP'))`). Local:
`date_trunc` on TIMESTAMPTZ (our producer declines it), and inline `VALUES` (the Kusto translator has no virtual table).

What the Kusto driver needed, all on the fork branch: ADBC 1.0 (the 0.22 exporter failed to load under our 1.0
importer, "Undefined error"), `Prepare` / `ExecuteSchema`, source-generated JSON for the request (reflection is off
under NativeAOT), a stream that knows its schema before the first batch, quoted column names from `cslschema`
(`['1mbin']`), and `notruncation` (Kusto cuts a result at 500 000 rows and puts the error inside the rows).

## The mssql-adbc side (branch `fabricator-tsql` in `D:/repos/mssql-adbc`, local only)

Our T-SQL emitter started as a copy of mssql-adbc's at `677676c`; upstream has not changed those files since.
The branch copies ours back, with what mssql-adbc's build demands that ours never had to: the dialect YAML entry
point, an AOT-safe JSON parse, and netstandard2.0 compatibility.

mssql-adbc's own tests against the docker server: 7 failures on `main` (environment), 30 with the port. The 23 new
ones are expected SQL text that changed (string keys), new refusals (ROLLUP/CUBE with the empty set — a real fix;
`sqrt`/`ln` domain — a DuckDB assumption, see 4.), wider translations (correlated scalar subquery via OUTER APPLY,
code-point `CHARINDEX`), and the dialect YAML not listing the float `divide`/`power` variants.

Open questions before an upstream PR:
1. **Trailing spaces and UTF-16 are upstream's documented choice.** Our exact string keys change that contract,
   so they would go in as an option (or a proposal to change the default), not as a bug fix.
2. **Fabricator-specific encodings** (catalog functions, table functions / APPLY, MERGE, the moved CTE, the
   `repeated_reads` hint, the `extension:fabricator:*` function URNs) need a neutral name or stay ours.
3. **Server facts** our emitter takes from the provider (string column byte lengths, uncollatable columns, the
   SQL Server 2022 gate) have no source inside the driver yet.
4. **DuckDB assumptions.** Some refusals follow what DuckDB does rather than what the plan says (e.g. a domain
   error is NaN in DuckDB). A driver for any client should read the plan's function options
   (`on_domain_error`, `on_division_by_zero`, …) instead; our producer would then have to state them.
