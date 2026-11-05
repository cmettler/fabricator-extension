# The `adbc` provider (experimental, branch `feat/adbc-provider`)

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
