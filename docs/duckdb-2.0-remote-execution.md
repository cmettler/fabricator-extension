# DuckDB 2.0: planner, optimizer and catalog features for remote execution

**Status (2026-10-24): survey, plus BUILT — the SQL Server DELETE/UPDATE shortcut (§2.4/§2.5), Delta
COUNT/MIN/MAX from the log (§3.1) and the SQL Server SELECT spike (§4.1).** Inputs: the DuckDB 2.0 source (our submodule, `v2.0.0`,
diffed against `v1.5.6`), duckdb-postgres `e53270e`, ducklake `947c8496`, duckdb-iceberg `b92a85f74`, and
the DuckLake 1.1 release post (2026-10-21). Paths below are relative to `duckdb/` unless a repo is named.

**Recommendation.** Build the DELETE/UPDATE shortcut on the **plan-less `Catalog::PlanDelete` /
`PlanUpdate` overloads** (§2), not on the new remote pushdown optimizer (§1). The plan-less route keeps
DuckDB's binding, transaction and row-count semantics, needs only a T-SQL rendering of one predicate that
we already render for scans, and serves BOTH providers: SQL Server gets `DELETE FROM t WHERE …`, and Delta
gets a metadata-only delete (drop whole files) that neither DuckLake nor Iceberg has yet. The remote
optimizer is the bigger prize (whole SELECTs with joins and aggregates) and a separate project; §1.4 lists
what has to be settled first.

## 1. The remote pushdown optimizer (new in 2.0)

### 1.1 What it is

It is not a logical-plan optimizer. It rewrites the PARSED statement before binding:
`Planner::CreatePlan` calls `optimizer.OptimizeStatement(statement)` before `Binder::Bind`
(`src/planner/planner.cpp:290-293`). It runs only when some attached catalog declares itself remote
(`DatabaseManager::GetRemoteCatalogCount() > 0`, `src/optimizer/optimizer.cpp:216-227`), and can be
disabled with `disabled_optimizers='remote_pushdown'`.

A catalog opts in through `Catalog::Supports(RemoteCapability)` (`src/include/duckdb/catalog/catalog.hpp:99-104,
:414`):

| capability | meaning | hook |
|---|---|---|
| `IS_REMOTE` | the catalog is remote; enables the optimizer at all | — |
| `EXECUTE_QUERY_NODE` | whole SELECT / INSERT / DELETE / UPDATE / MERGE nodes may be shipped | `RemoteExecute(ctx, unique_ptr<QueryNode>)` `:417` |
| `EXECUTE_STATEMENT` | DDL (CREATE / DROP / ALTER, CTAS) may be shipped | `RemoteExecute(ctx, unique_ptr<SQLStatement>)` `:419` |
| `CONNECT` | `CONNECT` routes raw session SQL here | `RemoteExecute(ctx, const string&)` `:420` |

Four veto hooks default to `true`: `SupportsPushdown(const ParsedExpression&)`, `(const TableRef&)`,
`(const QueryNode&)`, `(const SQLStatement&)` (`catalog.hpp:421-424`).

### 1.2 What it pushes

The unit is a whole query node, never a single operator. Each AST node is classified as no catalog, a
single remote catalog, or unknown (`src/optimizer/remote_pushdown_optimizer.cpp:165-221`). A node goes
remote only if everything in it references ONE remote catalog and no veto fires. A SELECT carries its
FROM, joins, WHERE, GROUP BY, HAVING, QUALIFY, window, ORDER BY, LIMIT and DISTINCT ON (`:378-438`).
`DeleteQueryNode`, `UpdateQueryNode` and `MergeQueryNode` go whole, with their USING clauses and RETURNING
(`:485-561`), so there is no rowid anywhere. Partial pushdown exists only for an INSERT's source SELECT,
UNION children without modifiers, and a CTAS query. A remote subquery inside a local join is NOT pushed.

Before shipping, the catalog prefix is stripped from every name, the largest foldable non-volatile
subexpressions (`now()`, `1+1`) are evaluated LOCALLY and replaced by constants, and the returned TableRef
is wrapped as `SELECT * FROM <ref>`, replacing the original statement (`:1162-1299`, `:1851-1897`).

### 1.3 Who uses it

| extension | remote optimizer | what it does instead |
|---|---|---|
| duckdb-postgres | NO (`Supports` answers only `IS_REMOTE` and `CONNECT`) | ctid scans for DELETE/UPDATE; `RemoteExecute(string)` for CONNECT only. Commit `37cea87` (2026-10-12) states the intent to move to it later |
| ducklake | NO (calls only the static `StripCatalogName` helper, for stored view SQL) | its own scan + delete files |
| duckdb-iceberg | NO | its own scan + positional / Puffin deletes |
| **duckdb-mysql** | **YES — all four capabilities**, unconditionally | SELECT subtrees, UPDATE, DELETE and a subset of DDL are pushed as whole statements; see §1.3a |

Its real consumer is DuckDB's own remote protocol: the **quack** extension together with the `CONNECT`
statement (user, 2026-10-24 — quack is not checked out here, so that path was not read). No in-tree DuckDB
test exercises it either: `test/extension/test_remote_optimizer.cpp` is an unrelated
2024 OptimizerExtension demo that ships a serialized logical plan over a socket.

### 1.3a duckdb-mysql: the one extension that uses it in earnest (read 2026-10-24, user-pointed)

Read from the local clone `D:/repos/duckdb-mysql` (files below are in that repo). It is the worked example of
what §1.4 sketches, and it confirms that sketch's costs rather than retiring them.

- **Capabilities:** `MySQLCapabilities::Supports` answers true for IS_REMOTE, EXECUTE_QUERY_NODE,
  EXECUTE_STATEMENT and CONNECT (`mysql_capabilities.cpp`). The switch `mysql_enable_remote_pushdown`
  (default true) gates only `SupportsPushdown`, and it is read ONCE AT ATTACH — its tests detach, set and
  re-attach.
- **The vetoes are the bulk of the code**, all four `SupportsPushdown` overloads. Representative refusals:
  TRY_CAST and casts MySQL treats leniently; string literals unless the server has a binary NO PAD
  collation (literals are then written with `COLLATE <that collation>`); a function WHITELIST that
  deliberately omits `/`, `round`, `upper`/`lower`, `now`, … ; QUALIFY, GROUP BY ALL, GROUPING SETS;
  SEMI/ANTI/FULL/ASOF joins; time travel; **every INSERT** (only its SELECT part is pushed); UPDATE/DELETE
  with RETURNING. Statements: CREATE TABLE (no CTAS, no TEMP, constrained constraint set), DROP TABLE (no
  CASCADE), and four ALTER kinds.
- **Rendering:** `MySQLSQLWriter` walks the PARSED, UNBOUND AST and writes MySQL dialect. `RemoteExecute`
  returns a `TableFunctionRef` to `mysql_query(catalog, sql, suppress_dml_output := true, …)`, which DuckDB
  binds in place of the statement (`WrapRemoteRef`). `mysql_query` PREPAREs at bind on the transaction's
  connection, so pushed statements see the transaction's own writes.
- **What it costs, confirming §1.4:**
  - **No affected-row count.** With `suppress_dml_output` the statement's return type becomes NOTHING, so a
    pushed UPDATE/DELETE reports no changed rows (its tests use `statement ok`). Our plan-less
    `PlanDelete`/`PlanUpdate` (§2.4/§2.5) keeps DuckDB's count, because the statement IS bound locally.
  - **Dialect drift lives in the writer.** DEFAULT/CHECK/generated expressions are written with DuckDB's
    `ToString()`, ADD COLUMN writes the DuckDB type name, and a `main` schema is skipped by a TODO.
  - **⚠ A real defect, worth not copying:** in the CREATE writer the `IGNORE_ON_CONFLICT` case has no
    `break`, so `CREATE TABLE IF NOT EXISTS` falls through into the OR REPLACE branch and sends
    `DROP TABLE IF EXISTS` first — it WIPES an existing table (`mysql_sql_writer.cpp` IGNORE_ON_CONFLICT case, at
    `1bd5004`). The veto ADMITS that conflict mode (`mysql_capabilities.cpp`), and no pushdown test issues a
    pushed `CREATE TABLE IF NOT EXISTS`, so it is reachable and untested. Read, not run; worth reporting
    upstream.
- **On the DuckDB side** (optimizer `REMOTE_PUSHDOWN`, before binding, only when a remote catalog is
  attached): a subtree is pushed when it touches exactly ONE remote catalog — explicit qualification, or
  an unqualified name that only one remote catalog in the search path has. Any local table, or two remote
  catalogs, keeps it local; subqueries and set-operation children can be pushed on their own. Constant
  subtrees are folded locally first, and kept local if folding would error.

**What it means for us.** For DDL it adds nothing: our CREATE/ALTER/DROP already execute on the server,
rendered in C# from BOUND information (types mapped by the provider, defaults read from the bound
expression) — strictly better than a writer working from the unbound AST. For UPDATE/DELETE the plan-less
route we built covers the same statements while keeping the row count and DuckDB's own binding. **The real
prize is unchanged: SELECT** — a join or aggregate executed by SQL Server. duckdb-mysql shows the shape (a
renderer plus a whitelist veto, a `fabricator_query`-like TableRef as the carrier) and the price (the veto
surface IS the work).

### 1.4 What it would take for SQL Server, and why it is not first

- **A dialect renderer.** `RemoteExecute` receives a DuckDB `QueryNode`; `ToString()` renders DuckDB SQL,
  not T-SQL (functions, `LIMIT`, literals, casts and quoting all differ). We would render the AST to T-SQL
  ourselves and use the `SupportsPushdown(expression)` veto for everything we cannot render — the same
  exactness discipline as §2, over a far larger surface.
  - **A candidate transformation: Substrait.** The local repo `D:/repos/mssql-adbc` (an experiment by the
    engineered-wood author) translates Substrait v0.107 plans to T-SQL — `SqlServerSubstrait.ToSql(plan)`,
    emitter `Substrait/TSqlEmitter.cs`, function map `TSqlFunctionCatalog.cs` — and REJECTS what it cannot
    translate with the same semantics (`SubstraitTranslationException`) instead of approximating, which is
    exactly the exactness rule this needs. Its `substrait.md` (under its docs folder) names the three semantic differences it
    accepts (UTF-16 strings, trailing-space comparison, trailing-zero binary comparison) and
    `substrait-dialect-gaps.md` what a dialect file cannot express. DuckDB's side would be the
    substrait extension (`get_substrait`), which is NOT linked into our build; whether it serves 2.0, and
    whether a pushed DML node or the pre-bind AST (rather than a logical plan) can be turned into Substrait at
    all, is unchecked. The renderer is C# and so is our bridge, which is what makes it worth a look.
- **The statement is never bound locally.** For a pushed DELETE, `BindNode(DeleteQueryNode)` never runs,
  so there is no `RegisterDBModify` and no `CHANGED_ROWS` return type; the client sees whatever columns our
  TableRef returns. Transaction participation, `RETURNING`, and the affected-row count are ours to produce.
- **Parameters stay in the AST** (`$n` is recorded for the veto but not substituted), so `RemoteExecute`
  must bind them.
- **Local constant folding** evaluates `now()` in DuckDB's session, not SQL Server's — usually what a user
  means, but a semantic choice to accept explicitly.
- **It would switch on for SELECTs too**, the moment `EXECUTE_QUERY_NODE` is declared. That is the prize
  (a join and an aggregate executed by SQL Server instead of pulling rows), and also why it wants its own
  slice and its own differential test suite: every query a user runs against the catalog changes path.
- It is SQL-Server-only by nature: Delta has no remote SQL engine, and DAX is not SQL.
- **It is what makes COMPLEX DML pushable, and the plan-less route cannot be.** DuckDB's
  `RewriteNode(DeleteQueryNode)` / `(UpdateQueryNode)` merge the USING / FROM tables, the WHERE (subqueries
  included), the SET expressions and RETURNING into one catalog verdict, so `DELETE … WHERE id IN (SELECT …
  FROM <same catalog>)`, EXISTS / correlated subqueries, `UPDATE … FROM` and even MERGE INTO are pushed WHOLE
  when every reference is the one remote catalog (`remote_pushdown_optimizer.cpp`). By `PlanDelete` the same
  subquery is a semi/mark join in an optimized plan, and SQL cannot be recovered from that soundly. The
  subquery is a SELECT, so this comes with — not before — SELECT pushdown.
- **DECIDED (user, 2026-10-24): pushed DML keeps DUCKDB's semantics.** A string comparison anywhere in a
  pushed DML statement — a subquery included — is refused or rewritten exactly (a binary collation, plus a
  trailing-space guard, since SQL Server's `=` pads even under BIN2), never left to the server's collation: a
  wrong DELETE cannot be undone, and the same statement must not change meaning with the route it takes.
  Server semantics may be acceptable for SELECT, if at all. duckdb-mysql chose the other way for
  column-to-column comparisons (§1.3a).

## 2. The plan-less `PlanDelete` / `PlanUpdate` overloads (1.5 and 2.0)

`Catalog` has two overloads of each (`catalog.hpp:377-384`):

```cpp
PlanDelete(ClientContext&, PhysicalPlanGenerator&, LogicalDelete&, PhysicalOperator &plan); // pure virtual — we override this
PlanDelete(ClientContext&, PhysicalPlanGenerator&, LogicalDelete&);                          // plan-less, has a default
```

`PhysicalPlanGenerator::CreatePlan(LogicalDelete&)` calls the PLAN-LESS one
(`src/execution/physical_plan/plan_delete.cpp:37-42`); its default builds the child with
`planner.CreatePlan(*op.children[0])` and forwards to the 4-argument form. UPDATE is the same
(`plan_update.cpp:22-31`). Overriding the plan-less form means the child scan is never built, so no rowid
is ever read. INSERT, CTAS and MERGE have no plan-less form: their child is always planned first.

**Measured on our SQL Server catalog (2.0 build), the child is exactly what a remote statement needs:**

```
DELETE FROM db.dbo.t WHERE id > 5 OR lower(v) = 'x'
  FILTER  Expression: ((id > 5) OR (lower(v) = 'x'))
    FABRICATOR_SCAN  Projections: id, v

UPDATE db.dbo.t SET n = n + 1 WHERE id = 3
  PROJECTION  (n + 1), rowid
    PROJECTION  #1, #2
      FILTER  Expression: (id = 3)
        FABRICATOR_SCAN  Projections: id, n
```

The WHOLE predicate survives as a `LogicalFilter` above our scan, because our `pushdown_complex_filter`
never erases what it pushes.

### 2.1 The rule that makes it safe: render EXACTLY or fall back

Scan pushdown is allowed to be a SUPERSET — DuckDB re-applies every predicate above the scan. A remote
DELETE has no such net: a predicate rendered weaker than the user's deletes rows the user did not name.
So the shortcut fires only when the entire predicate renders to T-SQL with no dropped conjunct, and
otherwise calls the base overload, which is today's rowid path. Concretely, fall back when:

- the child is anything but `[PROJECTION…] → [FILTER] → FABRICATOR_SCAN` of the target table
  (a join, a USING table, a subquery, a second scan);
- any filter expression does not render exactly (an unknown function, a collation-sensitive string
  comparison on a non-binary collation, a cast whose T-SQL semantics differ);
- `RETURNING` is present (`op.return_chunk`);
- for UPDATE, any SET expression does not render, or reads anything but the target row's own columns and
  constants.

`MERGE` keeps the rowid path: it plans its child first (`plan_merge_into.cpp:328-335`) and needs real
rows to match against.

### 2.2 What it gives each provider

- **SQL Server:** one `DELETE FROM [s].[t] WHERE …` / `UPDATE [s].[t] SET … WHERE …` on the transaction's
  pinned connection; the affected-row count comes back from SqlClient. No scan and no rowid batches.
  MEASURED for DELETE (§2.4): a table WITHOUT a PK or IDENTITY gets through binding and is deleted from —
  the "needs a primary key" refusal comes from our 4-argument `PlanDelete`, which the shortcut never calls.
- **Delta:** a metadata-only delete. If the predicate can be decided per FILE from the log — a partition
  column predicate, or file statistics that prove every row matches (min/max inside the range, no nulls) —
  the file is dropped with a `remove` action and no data is read; a file that only partly matches still
  takes the rowid path. Neither DuckLake nor Iceberg has this: DuckLake drops a file only after scanning
  it (`TryDropFullyDeletedFile` in ducklake's `ducklake_delete.cpp:408-420`), and Iceberg's header
  comment announces it but nothing implements it (duckdb-iceberg `iceberg_catalog.hpp:168-171`).

### 2.3 Prior art

duckdb-postgres tried forwarding whole DELETE/UPDATE statements in 2023 and reverted it the same day
(`dd51b9f` → `f8da0a8`); today it scans ctids for both (duckdb-postgres `postgres_delete.cpp:129-140`,
`postgres_update.cpp:210-220`). The commit message gives no reason, so the revert is a prompt to look for
the edge case, not evidence that the design is wrong.

### 2.4 DELETE on SQL Server — BUILT (ABI v93, 2026-10-24)

`FabricatorCatalog::PlanDelete` (the plan-less overload) tries `TryPlanDeleteWhere` and otherwise calls the
base overload. It fires only for `[FILTER] → FABRICATOR_SCAN` of the target table, with no `RETURNING`, no
scan-level `table_filters`, the setting `fabricator_dml_pushdown` on, every filter expression serialized by
the scan's own `FilterSerializer` in STRICT mode, and the table's binding answering `delete_where = true`
(ABI `table_dml_info`; SQL Server says no for an EXTERNAL table, whose DELETE is routed to storage). The
operator `FABRICATOR_DELETE_WHERE` sends the predicate through `table_delete_where` (`table_dml_where` since v94), and SQL Server renders it
with the same `FilterWhereBuilder` the scan uses, so values are bound as parameters exactly as they are there.

What strict mode refuses, so the shortcut cannot widen a predicate:

- any child of an AND or an OR that does not serialize (the scan drops it from an AND — a superset);
- a comparison on a type outside BOOLEAN, the signed/unsigned integers, DECIMAL and DATE. VARCHAR is out
  because SQL Server compares under the column collation (case, accents, trailing spaces); BLOB because
  trailing zeros are ignored; FLOAT/DOUBLE, TIMESTAMP and TIME because the T-SQL types they meet
  (`datetime`, `real`) round differently;
- a struct member path.

Two things found while building it:

- **At physical planning the filter's column references are `BOUND_REF`, not `BOUND_COLUMN_REF`** — the
  `ColumnBindingResolver` has already run. They index the scan's output, so they map through
  `projection_ids` and then `column_ids` to a name. Without that the shortcut never fired at all; the
  `FABRICATOR_DUMP_DML=1` trace (env-gated, prints every fallback reason) is what showed it.
- **A top-level AND arrives as SEPARATE filter expressions**, each checked alone, so the AND-drop rule is
  reachable only for an AND nested in an OR. The gate has that shape for that reason.

Gate `test/verify_dml_pushdown_mssql.test` (60, service tier): EXPLAIN routing rows for each fallback rule;
the same eight DELETEs on two identical tables, one with the shortcut and one without, with every count and
the final contents compared; a ROLLBACK and a COMMIT; and the no-PK case. Two mutants, each killed at its own
row: types not checked (dies at the VARCHAR routing row) and the AND-drop allowed (dies at the nested-AND row).

### 2.5 UPDATE on SQL Server — BUILT (ABI v94, 2026-10-24)

`FabricatorCatalog::PlanUpdate` (plan-less) tries `TryPlanUpdateWhere`. Its child is ONE projection — the SET
values, then the rowid — over the same `[FILTER] -> FABRICATOR_SCAN` as DELETE. Every SET value must render
exactly too (`FabricatorSerializeExactValue`), and one that does not sends the whole statement to the rowid
path, like a predicate conjunct. Admitted values, and why each is exact:

- a CONSTANT of an exact type or VARCHAR. Assigning a string is not a comparison, so the collation question
  that keeps VARCHAR out of predicates does not arise, and the rowid path sends the same value as a parameter;
- a COLUMN of an exact type: the server copies its own stored value;
- integer `+ - *` (TINYINT/UTINYINT/SMALLINT/INTEGER/BIGINT operands). Both engines compute exact integer
  arithmetic, so whenever both succeed they agree. They can differ only where an intermediate result
  overflows: DuckDB keeps the operand type, SQL Server may widen. That is an error against a correct answer,
  never two different answers. A single operation overflowing the column fails on both (measured: 8115 from
  SQL Server, DuckDB's own overflow on the rowid path), and the failed statement changes no row.

Refused: a CAST (the binder adds one whenever the value's type is not the column's, which is what keeps
DECIMAL arithmetic out — DuckDB's and SQL Server's decimal precision rules differ), any other function,
division (DuckDB's `/` is float division), `UPDATE … FROM`, RETURNING.

**Found while building it: the FILTER carries a projection map.** `UPDATE … SET n = n + 1, v = 'z' WHERE
id > 1` plans its FILTER with a `projection_map`, which `plan_filter.cpp` turns into a physical projection.
The filter's OWN expressions still read the scan's output, but anything ABOVE it — the SET values — indexes
the narrowed list. So each value is copied and its positions are mapped back through the map before they
name a scan column. The first build refused every such UPDATE; a logical-plan dump (`explain_output =
'optimized_only'`) had hidden the map, and `FABRICATOR_DUMP_DML=1` showed it on the first real run. A DELETE
reads nothing above the filter, so the map is irrelevant there.

Gate: `test/verify_dml_pushdown_mssql.test` grew to 131 (+71): routing for each refusal, an eleven-statement
differential whose counts are DuckDB's own answers for the same sequence on a NATIVE table, so they are an
oracle for both paths, plus a column-by-column checksum against that native result, statement atomicity on
overflow, a ROLLBACK and a COMMIT, and UPDATE on a table without a key. Two mutants, both killed at the
differential's second statement: SET values read without the projection-map remap (the first UPDATE wrote
the wrong column), and the bridge running an UPDATE spec as a DELETE (the v94 bump's reason, see
[abi-history.md](abi-history.md) §v94).

### 2.6 DELETE on Delta — BUILT (2026-10-25, no ABI change)

The same plan-less `PlanDelete`, with engineered-wood's predicate delete (`DeltaTransaction.DeleteAsync(Predicate)`,
clast-project/engineered-wood #506, in our pin since `e598d1d`) as the provider statement. Gate
`verify_delta_delete_where` (106, hermetic), three mutants each killed at its own row.

**Where it wins, and therefore where it is offered.** engineered-wood decides a file WITHOUT READING IT in two
cases: a predicate over partition columns alone (every row shares the file's partition values), and — with
`DeltaTableOptions.DeleteTrustsStatistics` — a file whose statistics prove every row matches. A file it cannot
decide it reads with its OWN parquet reader, every column, where the rowid path reads natively and only the
predicate's columns. So the shortcut is not "every exact predicate" (that would make partial deletes slower):

| table / predicate | path |
|---|---|
| predicate on partition columns only (any table, DV or not) | shortcut — whole files removed unread |
| any column, ATTACH `delete_trusts_statistics true`, deletion vectors on | shortcut — statistics-proven files unread, the rest read by engineered-wood |
| any other column | rowid path |
| change data feed on | rowid path (the feed needs the deleted rows; every file would be read anyway) |
| inside `BEGIN … COMMIT` | rowid path (engineered-wood deletes against the BASE snapshot, blind to the transaction's own writes) |

The opt-in is off by default, as in Spark: a wrong bound costs a skipped read when pruning but DELETED ROWS here,
and two of our own write paths are known to have produced such bounds (the truncated-max native stats before
their fix, and inverted decimal statistics from the codec writer before EW #398).

**How the host learns it — three additive keys on `table_dml_info`, no ABI bump.** `delete_where_autocommit_only`,
`delete_where_columns` (the only columns a shortcut predicate may name; absent = any) and `exact_strings` (VARCHAR
joins the strict allow-list: Delta compares UTF-8 bytes with no collation and no trailing-space rule, which is
DuckDB's default). The managed side reads them from an INTERNAL `IDeleteWhereShape`, so the public plugin contract
is unchanged. ⚠ The capability doc is cached per catalog entry, so it is asked BEFORE the predicate is rendered now
(what counts as exact is the provider's answer) — one crossing per entry, as before.

**⚠⚠ THE FINDING THAT MADE IT WORK AT ALL: Delta's WHERE is ERASED INTO THE SCAN.** The first build never fired:
`FindExactScan` refused any scan with `table_filters`, a guard written for SQL Server, whose scan never claims a
filter. Delta's native read does (`filter_pushdown`), so DuckDB removes the FILTER operator and the predicate lives
only in `LogicalGet::table_filters`. Each table filter now becomes an expression again
(`TableFilter::ToExpression` over a column ref to the scan) and goes through the same strict serializer; a join
filter's optional/dynamic wrapper does not render, which sends the statement to the rowid path. SQL Server is
unaffected (no table filters, the old path).

**The predicate is rebuilt EXACTLY** (`DeltaFilterBuilder.BuildExact`): no AND child is dropped (the pruning
builder drops unmappable ones on purpose), `IS NOT DISTINCT FROM v` is `=`, `IS DISTINCT FROM v` is
`<> v OR IS NULL` (a NULL row must be deleted; `<>` alone would leave it unknown), DATE constants map to
`DateOnly`, and anything unrenderable THROWS — a failed statement, never other rows.

**"Without reading" is proven, not inferred.** §4/§5 overwrite a data file with garbage: the rowid path fails on it
(*"too small to be a Parquet file"* — the control that the file really is unreadable), the shortcut deletes the
partition / the statistics-proven file anyway, with the right count from the log.

⚠ **An observation the proof surfaced, NOT fixed:** the rowid path's scan for `p = 'c'` read the file of partition
`a`, i.e. the DELETE-shaped native scan did not prune partitions by its own filter. Worth a look on its own — it
is a cost every rowid DELETE pays.

## 3. Scan-side 2.0 features worth adopting (mostly for Delta)

| feature | API | who uses it | value for us |
|---|---|---|---|
| exact `COUNT(*)` and `MIN`/`MAX` from metadata | `TableFunction::get_partition_stats` + `PartitionRowGroup` (`table_function.hpp:536-544`); `StatisticsPropagator::TryExecuteAggregates` folds them | DuckLake (count + min/max, `ducklake_scan.cpp:149-232`), Iceberg (count only) | we already sum the log for cardinality; this answers the aggregate with no scan. `MinMaxIsExact` must be false whenever a deletion vector exists |
| TopN file ordering | `set_scan_order` / `RowGroupOrderOptions` | Iceberg (`scan_order/iceberg_scan_order.cpp:51-150`) | `ORDER BY x LIMIT n` reads only the files that can still contribute |
| TopN dynamic-filter skip | `ExpressionFilter::GetOptionalDynamicFilterData` at file open | DuckLake (`ducklake_multi_file_reader.cpp:92-147`) | same goal, decided per file as the boundary tightens |
| cross-column OR, IS NULL, struct members, join-build-side filters prune FILES | multi-column filters (`TableFilterSet::GetMultiColumnFilters`), `ExpressionFilter::column_indexes` | DuckLake 1.1 "smarter file pruning" (`ducklake_multi_file_list.cpp:306-441`) | our pruner converts single-column ExpressionFilters only (`LiveExprNode`); a multi-column OR is dropped today |
| aggregate over a subset of partitions | `set_partitions_to_scan` (new) | — | follows from `get_partition_stats` |
| table function rendered as SQL | `to_sql` (new) | — | lets a remote catalog ship a query that contains one of our functions |
| logical plan → SQL | `LogicalPlanSQLExporter` (new, `planner/logical_plan_sql_exporter.hpp:51-65`) | — | DuckDB dialect only, and DML exports keyed by rowid — not a route to `DELETE … WHERE` |
| `CONNECT` raw routing | `RemoteExecute(ctx, const string&)` | duckdb-postgres | the 2.0 form of `fabricator_query`/`fabricator_exec`: a session `CONNECT`ed to SQL Server sends its SQL there |
| several DML CTEs per statement | `Catalog::SupportsMultipleDMLCTEs()` (default false) | — | only if all of them share one provider transaction |

One observation from the DuckLake post, for our own types: DuckLake 1.0 stored an untyped `NULL` column
from a CTAS as `INTEGER` and could not widen it to `VARCHAR` later; 1.1 adds a real `"NULL"` type. Our
host-query boundary promotes an untyped NULL to INTEGER the same way (`PromoteNullColumns`, the 2.0 port).
⚠ UNCHECKED: what a Delta CTAS with a bare `NULL` column records in its schema. If it is `integer`, it is
recorded durably, and Delta has no null type to map to — so a refusal or an explicit cast would be the
honest answer rather than a silent `integer`.

### 3.1 `get_partition_stats` for Delta — BUILT (ABI v95, 2026-10-24)

`FabricatorScanPartitionStats` is the catalog scan's `get_partition_stats`; it asks the table session
(`table_partition_stats`) for per-file statistics and builds one `PartitionRowGroup` per Delta file. DuckDB's
`StatisticsPropagator::TryExecuteAggregates` then replaces an ungrouped COUNT(*)/MIN/MAX with constants:
`EXPLAIN` shows a `Column Data Scan` and no `Fabricator Scan`. Kill switch `fabricator_partition_stats`.

**The answer replaces the scan**, so the provider owns every reason it might differ:

- **The snapshot.** An AT clause, else — in an explicit transaction — exactly `ScanCore`'s call (the
  table's existing pin, else the transaction's first-read instant resolved for this table), else the
  autocommit pin or latest, SEEDING the pin so later references read the version the statistics describe.
  If a concurrent reference seeded a different version first, the answer is "no statistics".
- **The read set.** An answer from the log is still a READ. In an explicit transaction it records a
  whole-table read exactly as an unfiltered scan does, or `INSERT INTO t SELECT max(id) + 1 FROM t` commits as
  a blind append and a concurrent append can no longer conflict with it. Found by the hermetic tier, not by
  the feature's own gate: `verify_delta_catalog_transactions` §42a read `isBlindAppend: true` for exactly
  that statement. Whole-table is a superset of what a filtered aggregate read, so the cost is availability
  (more conflicts), never correctness.
- **Pending writes.** A scan overlays the transaction's buffered appends, deletes and a pending ALTER; the log
  knows none of them. Any of them on the table (or eagerly written files, buffered batches, a held EW
  transaction) means no statistics.
- **Exactness.** DuckDB's `MinMaxIsExact` means the bounds are values PRESENT in the rows, which is stronger
  than what Delta statistics promise (bounds sound for pruning). Claimed only for: a partition value
  (integer, date, decimal, string) of a file with at least one live row; integer/date/decimal data statistics
  of a file with no deletion vector and no `tightBounds: false`. Strings are truncated by writers, Spark
  timestamps are millisecond-truncated, float bounds skip NaN — each a bound, none an answer.
- **Counts.** `numRecords` minus the deletion vector's cardinality; a file without `numRecords` reports an
  unknown count, and DuckDB then does not answer COUNT.
- **Validity.** The null count drives the filter check (`x > 5` is ALWAYS_TRUE only over a column with no
  NULLs), so an unknown null count reports "may have NULLs and values".

The other two consumers of `get_partition_stats` cannot act on these: the row-group pruner requires
`set_scan_order` (not set by our scan), and partitioned execution requires min/max in the GLOBAL `statistics`
callback, which `FabricatorScanStatistics` deliberately withholds.

**Also found while building it, and NOT this feature's:** in an explicit transaction the scan reads a table
first touched later in the transaction as of the transaction's first-read INSTANT — except when that
statement is the entry's FIRST bind, whose schema probe (`SchemaProbe`) seeds the pin at LATEST first. So
whether a commit that landed mid-transaction is visible depends on catalog-entry state. The metadata answer
uses the same pin call and so agrees with the scan in both states; the gate warms the entry so both halves
test the instant. Recorded as an open snapshot-isolation question.

**Found while building it:** DuckDB itself declines a file whose column is NULL in every row
(`TryGetValueFromStats` needs a min/max; there is a TODO for it). Two of my first gate assertions expected a
metadata answer over such a column and were wrong, not the code.

Gate `test/verify_delta_partition_stats.test` (130, engine-doubled): every answer is asserted twice, from
metadata and with the switch off. Mutants, native engine:

| mutant | result |
|---|---|
| pending writes ignored | killed, §6 (line 205) — the transaction's own insert went missing |
| a partition file with no live row trusted | killed, §4 (line 147) — `min(region)` answered a deleted value |
| a DV file's data bounds trusted AND `tightBounds` ignored | killed, §3 (line 134) — `max(id)` answered the deleted max |
| a DV file's data bounds trusted (only) | SURVIVED — engineered-wood stamps `tightBounds: false` on every file it gives a vector, so the second check covers the first; both stay because other writers need not |
| the explicit-transaction branch removed (latest pinned, no read recorded) | killed, §7b (line 285) — answered latest where the scan honours the instant; and `verify_delta_catalog_transactions` §42a (blind-append claim) |
| the autocommit pin ignored | SURVIVED — inside a transaction the table's open is shared and was opened AT the pinned version, so `CurrentSnapshot` is already the pinned snapshot. The pin branch matters only when that cache declines (past the per-transaction open cap), which the gate does not reach |

### 3.2 The Substrait spike — MEASURED (2026-10-25, user-asked)

**Question:** could DuckDB's own Substrait producer plus clast **mssql-adbc** (Substrait -> T-SQL in C#) replace the
hand-written renderer of §4.1–§4.3 — DuckDB doing the binding, C# doing the dialect?

**Setup.** DuckDB **1.5.6** (the substrait community extension exists for 1.5.6 / `linux_amd64` only; the
extension repo is pinned to 1.5.6, NOT 2.0) produced a plan per query in WSL from local tables holding the exact
SQL Server data (exported through our catalog to parquet, so the types are ours), and computed DuckDB's answer.
On Windows the mssql-adbc NativeAOT ADBC driver (`dotnet publish src/Clast.Drivers.SqlServer.Native`) executed
each plan against the rig's SQL Server (database collation `SQL_Latin1_General_CP1_CI_AS`); a script compared
values (in order where the query orders) and result types. 58 queries: the 35 differential cases of
`verify_remote_select_mssql`, plus 23 shapes our renderer refuses.

| verdict | n | queries |
|---|---|---|
| same values AND types | 29 | every ORDER BY / NULL-order / LIMIT / OFFSET case, alias shadowing, HAVING-only, INNER / LEFT / three-table / self joins, DISTINCT over a join, arithmetic ORDER BY — and, beyond our renderer: IN lists, BETWEEN, CASE, IN / EXISTS subqueries, UNION ALL, FULL JOIN, LIKE, upper/length, coalesce, division, a float sum, string `<` |
| same values, other types | 3 | `sum(int)` is BIGINT where DuckDB says HUGEINT |
| server ERROR, DuckDB answers | 4 | `sum(bigint)` overflows BIGINT on the server |
| **WRONG ANSWER** | 13 | 9 string cases (`=`, `<>`, GROUP BY, HAVING, DISTINCT, count(DISTINCT), a VARCHAR/NVARCHAR join, ORDER BY); `avg` over integers, a scalar subquery using it, `avg` over a decimal; `EXCEPT ALL` |
| refused by the translator | 8 | two malformed plans (COUNT pair under a filter; ROLLUP), `subtract` without the overflow option, `min:str`, `%`, `//`, `extract`, a window |
| both error | 1 | the INTEGER overflow (`id * 2`) |

**Where each defect sits**, read from the generated T-SQL and the plan JSON:

- **mssql-adbc, by design:** strings compare under `Latin1_General_100_BIN2_UTF8`, which ignores TRAILING SPACES
  (documented in its substrait.md as one of three accepted differences). On adversarial data that is 9 wrong
  answers; our sentinel key (§4.3) is the fix it would need. The `_UTF8` collation also needs SQL Server 2019+.
- **mssql-adbc, a bug:** `avg` over an integer renders `CAST(AVG(CAST(x AS bigint)) AS float)` — integer division
  inside AVG, so `0.2307…` becomes `0.0`. Decimal `avg` keeps T-SQL's DECIMAL(38,6) where DuckDB returns a double.
- **duckdb-substrait, a bug:** `EXCEPT ALL` is emitted as `SET_OP_MINUS_PRIMARY` (no ALL) — 0 rows where DuckDB
  returns 291. Silent, and invisible to any consumer. Also two plans whose root names do not match their
  columns (refused, so safe), and arithmetic without the `overflow` option the dialect requires (refused).
- **the format:** Substrait has no 128-bit integer, so DuckDB's HUGEINT sums are declared i64 — a type change
  everywhere and an overflow error past 2^63 where DuckDB answers. A DuckDB-side wrapper could restore the type
  (it knows the bound type) but not the range.

**What it means.** The approach delivers BREADTH our renderer will never reach by hand (subqueries, set
operations, CASE, functions, any join), and it moves the dialect to C# where the architecture says it belongs.
But out of the box it does NOT meet "exact or refuse": 13 of 58 are silent wrong answers. Every one is fixable
and every fix is upstream — a strict-semantics mode in mssql-adbc (string keys, AVG, decimal avg, i64 sums), and
three producer fixes plus a 2.0 port in duckdb-substrait. The harness of this spike is the gate either project
would need (scratchpad `spike/produce.py` + `consume.py`). The renderer of §4.1–§4.3 stays the exact oracle.

### 3.3 Binding inside RemoteExecute — PROBED (2026-10-25, user-asked)

**Question:** §3.2 showed that Substrait needs types the unbound AST does not have, so producing it ourselves
pre-bind would mean writing a binder. Can `RemoteExecute` instead bind a COPY of the node with DuckDB's own
binder and take DuckDB's exact types from there?

**Probe:** `FABRICATOR_PROBE_BIND=1|2` in `fabricator_remote_select.cpp` (spike, env-gated). It copies the
node, restores this catalog's name on its tables (the optimizer stripped it), calls
`Binder::CreateBinder(context)->Bind(*copy)` and prints the outcome, the time, the result types and (level 2)
the logical plan. With the probe on, every SELECT and set operation reaches `RemoteExecute`, which declines
what the renderer does not take. Off, nothing changes (`verify_remote_select_mssql` 373, unchanged).

**Measured against the rig's SQL Server:**

- **It binds, and it does not re-enter.** Binding a copy goes through `Binder::Bind`, not the planner, so the
  remote optimizer (called from `Planner::CreatePlan`) does not run again; a re-entry guard never fired.
- **The types are DuckDB's own, which is the point:** `sum(int)` HUGEINT, `avg` DOUBLE, `year(d)` BIGINT,
  `coalesce(k, -1)` SMALLINT, `sum(amount * 2 + 1)` DECIMAL(38,2), and names as DuckDB reports them.
- **Implicit casts are explicit in the bound plan**: `k = 1.0` binds as
  `CAST(k AS DECIMAL(6,1)) = CAST(1.0 AS DECIMAL(6,1))`. A renderer over the bound tree sees exactly the
  semantics DuckDB will apply and can refuse a cast it cannot render exactly, instead of re-deriving the rules.
- **Subqueries stay subqueries**: `EXISTS` binds as a `Dependent Join` + `SUBQUERY` filter, not yet flattened
  (that happens later, in the optimizer), so it can still be rendered back as `EXISTS (…)`. UNION ALL binds as
  a `Union` over its branches.
- **Cost:** ~1–2.5 ms per statement once warm, ~38 ms for the first statement of a session (most likely the
  table entry being materialized, which the real bind needs anyway; not separated). ⚠ In the shell's agent
  mode every statement is planned twice; with `-no-agent` once.

**Two limits found:**

- **Prepared parameters do not bind**: *"Unexpected prepared parameter"*. The fresh binder has no parameter
  values, and `RemoteExecute(ClientContext&, QueryNode)` is not handed the outer binder that has them. Those
  statements must keep declining, as they do today, unless the hook is given the binder.
- **CTE references must be told apart from tables** before the catalog name is restored. The probe's naive
  restore qualified the CTE `x` and failed (and in probe mode the declined query failed with it). A real
  implementation reads the node's CTE map first.

**What it means for §3.2's question.** Binding in `RemoteExecute` removes the hardest part of producing any
intermediate form ourselves: we would not re-derive DuckDB's typing, casts or function resolution. What stays
ours is translating a BOUND tree (logical operators + bound expressions) to T-SQL, refusing anything not exact.
The intermediate form can be Substrait (still without HUGEINT, §3.2) or a narrow JSON of our own for a C#
renderer. Nothing of this is built beyond the probe.

### 3.4 Taking over to_substrait and mssql-adbc — step 1 measured (2026-10-25, user-directed)

**Decision direction (user):** do not build our own translation; take over duckdb-substrait's `to_substrait`
(ported to 2.0 until upstream has a 2.0 version) and mssql-adbc's T-SQL emitter, with references to the
originals. Gaps are fine if they decline; wrong answers are not.

- **duckdb-substrait** (`D:\repos\duckdb-substrait`, MIT, `97a46c7` of 2026-10-19) pins DuckDB **v1.5.6**.
  Compiled against our 2.0 headers with g++: `to_substrait.cpp` **180 errors**, `custom_extensions*.cpp` 0.
  The classes are the ones our own 2.0 port already solved: ~110 fields that became accessors, comparison /
  BETWEEN / cast as function expressions, the `Legacy*` table filters, `Identifier`, `TableIndex`,
  `ProjectionIndex`. Mechanical.
- **Our scan needs an adaptation there**: `TransformTableScanToSubstrait` casts the bind data to DuckDB's own
  `TableScanBindData` without a check, and our scan reports `ScanType::TABLE` too — undefined behaviour unless a
  branch for our bind data names the table.
- **Dependencies**: protobuf (with abseil) plus `substrait-protobuf` from the substrait-packaging vcpkg
  REGISTRY (`vcpkg-configuration.json`), i.e. manifest mode. Our build and CI install vcpkg packages in
  classic mode on three platforms, so this is the largest integration cost, along with protobuf's size in the
  loadable. Built in WSL in 5.5 min.
- **Flatten + optimize on the bound copy works**: `RecursiveDependentJoinPlanner::Plan` +
  `FlattenDependentJoins::DecorrelateIndependent` (what `Planner::CreatePlan` does after binding), then
  `Optimizer::Optimize`. `EXISTS` becomes a `RIGHT_SEMI` join, `IN` a `SEMI` join, `ORDER BY … LIMIT` a
  `Top N`; ~0.3–1 ms on top of the bind. ⚠ duckdb-substrait disables seven optimizers (IN_CLAUSE,
  COMPRESSED_MATERIALIZATION, MATERIALIZED_CTE, COMMON_SUBPLAN, STATISTICS_PROPAGATION, EMPTY_RESULT_PULLUP, …)
  by mutating the GLOBAL `DBConfig`, which is not safe beside concurrent queries; we need a per-call way (or
  run only the passes we need). STATISTICS_PROPAGATION must stay off for us in any case: a pushed plan must not
  depend on local statistics.
- **⚠ The optimizer is NOT needed, flattening alone is (measured the same day, user-asked: "the remote optimizes
  itself").** After flattening alone, a correlated `EXISTS` and an uncorrelated `IN` are `MARK` joins under a
  filter on the mark, and a correlated scalar subquery is a `LEFT` join on `IS NOT DISTINCT FROM` — no
  duplicate-eliminated joins. Both sides already handle these: `to_substrait` maps `MARK` to
  `JOIN_TYPE_LEFT_MARK` and `IS NOT DISTINCT FROM` to `is_not_distinct_from`; mssql-adbc emits mark joins as
  `CASE WHEN EXISTS …` and has `IS NOT DISTINCT FROM`. Skipping the optimizer drops the global-config problem,
  keeps the plan independent of local statistics, sends SQL Server something close to what the user wrote, and
  shrinks the port: only the optimizer puts filters into `LogicalGet.table_filters`, so the `Legacy*` filter
  code in `to_substrait` can decline instead of being ported.
- **mssql-adbc** (`D:\repos\mssql-adbc`, Apache 2.0, `677676c`): `src/Clast.Drivers.SqlServer/Substrait`, ~5,560
  lines of C#, BCL only plus its own protobuf reader; only the driver glue uses ADBC. It handles Read, Filter,
  Project, Fetch, Sort, Aggregate, Join, Cross, Set and TopN — **no `ReferenceRel`** (so CTEs decline) and no
  duplicate-eliminated join (which the optimizer removes for the shapes above).
- **CTEs**: a statement over this catalog alone reaches us whole; a remote-only CTE block inside a MIXED
  statement is never offered (the optimizer analyses CTE bodies but never replaces one, and `FinishPushdown`
  blocks every pushdown below a scope that has a CTE — its own `FIXME`). That needs a DuckDB change.

### 3.5 Step 2 — the vendored producer runs end to end (2026-10-25)

- **`third_party/duckdb-substrait/`**: the producer (`to_substrait.cpp`, `custom_extensions*.cpp`, headers, MIT
  license, README naming upstream commit `97a46c7`), ported to DuckDB 2.0 — 180 compile errors to 0, 30 places
  marked `[FABRICATOR-PATCH: …]`. Declined rather than ported: the table-filter translators (only the optimizer
  creates table filters), windows (2.0 reshaped them; the T-SQL emitter has none), CTAS/INSERT/DELETE (only
  SELECT is pushed). Fixed beyond the port: the table scan names its table through `LogicalGet::GetTable()` with
  schema + table instead of an unchecked cast to DuckDB's own bind data; `TRY_CAST` and exclusive `BETWEEN`
  bounds decline (both would be widened).
- **Build** (as of step 2; since §3.12 the producer is mandatory and the option is gone): `-DFABRICATOR_SUBSTRAIT=ON` (default OFF) with protobuf 5.29.3 + substrait-protobuf 0.102.0 from a
  manifest-mode vcpkg install (`CMAKE_PREFIX_PATH`, and `Protobuf_PROTOC_EXECUTABLE`, which protobuf's config
  demands even though nothing is generated). MSVC builds it unchanged.
- **The pipeline** (probe level 5): bind a copy → `RecursiveDependentJoinPlanner` + `DecorrelateIndependent` →
  `ResolveOperatorTypes` + `ColumnBindingResolver` (to_substrait needs positional references — the first run
  failed on `BOUND_COLUMN_REF` without it) → `to_substrait` in strict mode. No optimizer.
- ⚠ **A name resolved only through `USE db.dbo` was not pushed at all** — DuckDB's remote optimizer checks a bare
  name as `<catalog>.main.<name>`; `RemoteExecute` was never called. Fixed in §4.22.
- **Measured, the 58 spike queries through this path and mssql-adbc against SQL Server**: 30 identical, 3 same
  values with BIGINT for HUGEINT, 4 overflow errors (`sum` declared i64), 1 both error, 5 declined by the
  producer (strict mode: `%`, `//`, `min` on strings, an expression GROUP BY; and the window), 2 refused by the
  consumer (`extract:date`; and the unoptimized mark join for `EXISTS`, whose field references mssql-adbc
  reads as out of range — safe, but a mismatch between the two to resolve), **13 wrong answers — the same 13
  as §3.2**: 9 strings (trailing spaces), `avg` over integers ×2, `avg` over a decimal, `EXCEPT ALL`. Producing
  the plans ourselves changed nothing about them, as expected: they sit in the emitter and in to_substrait's
  set operation, i.e. in code we now own copies of.
- **One optimizer pass after all — `FilterPushdown`, alone (user-decided).** Without it `IN` / `EXISTS` stayed
  `MARK` joins, rendered as a nested `CASE WHEN EXISTS … WHEN EXISTS … IS NULL`, and the mark join for `EXISTS`
  was refused by the consumer. With it they become `SEMI` joins and plain `WHERE EXISTS (…)`. It adds no table
  filters on SQL Server (that scan declares `filter_pushdown = false`). It surfaced two producer gaps, both
  fixed: an exclusive `BETWEEN` (it folds `x > a AND x < b` into one) is emitted as its two comparisons, and a
  join condition that is an arbitrary expression (2.0 allows them; the pass moves predicates into joins)
  translates in the joined field space. **Re-measured: 31 identical, 13 wrong (unchanged), 5 declined by the
  producer, 1 refused by the consumer (`extract:date`).**

### 3.6 An exact string ORDER key — measured (2026-10-25, user-asked: window functions and string min/max are
essential, e.g. on columnstore)

§4.3 concluded string ordering cannot be exact on SQL Server (ANSI padding; UTF-16 code-unit order). It can,
with SQL Server 2019+ UTF-8 collations and an escaped sentinel:

```sql
REPLACE(REPLACE(CONVERT(VARCHAR(MAX), x COLLATE Latin1_General_100_BIN2_UTF8),
                CHAR(1), CHAR(1) + CHAR(2)), CHAR(0), CHAR(1) + CHAR(1)) + CHAR(0)
```

- **UTF-8 under a BIN2 collation compares bytes, and UTF-8 byte order IS code-point order** — DuckDB's order.
  The control (`x COLLATE Latin1_General_100_BIN2` on NVARCHAR) sorts U+1D11E before U+E000; the key does not.
  ⚠ Collate BEFORE converting: `CONVERT(VARCHAR(MAX), x COLLATE …_UTF8)` converts to UTF-8, while
  `CAST(x AS VARCHAR) COLLATE …` would convert through the database code page first.
- **The trailing `CHAR(0)` makes a prefix sort first despite padding** (`'a'` before `'a '` and before
  `'a' + CHAR(1)`): it is below the space the shorter key is padded with.
- **The escaping is needed for strings that contain `CHAR(0)`** — without it the padding wins again
  (`'a' + CHAR(0)` sorted before `'a'`). Mapping `CHAR(0)` → `CHAR(1)+CHAR(1)` and `CHAR(1)` → `CHAR(1)+CHAR(2)`
  keeps the order and leaves no `CHAR(0)` in the body, so the sentinel is strictly the smallest.
- **Measured** on 11 adversarial values (NUL and `\x01` after a prefix, trailing space, case, U+E000, U+FF21,
  U+1D11E): identical to DuckDB's order; decoding (`LEFT(k, LEN(k) - 1)`, then the two REPLACEs reversed)
  round-trips all 11. `rs_s` (VARCHAR and NVARCHAR) ordered identically too.
- **Use**: string `min`/`max` (aggregate the key, decode the result), ORDER BY, `<`/`>`, and window ORDER BY.
  Equality/GROUP BY use the cheaper equality key (§4.18 F1 — NOT §4.3's UTF-16 key, which splits unpaired
  surrogates that the scan reads as one U+FFFD). ⚠ Needs UTF-8 collations (SQL Server 2019+, Fabric Warehouse);
  older engines decline. ⚠ A per-row expression: no index seek and possibly no batch-mode aggregate on it —
  exact, but not free; measure on columnstore before relying on the speed.

### 3.7 `IGNORE NULLS` in window functions — measured (2026-10-25, user-corrected)

SQL Server supports null treatment for `LAG`, `LEAD`, `FIRST_VALUE` and `LAST_VALUE`. Measured on the rig
(17.0.4055.5) against DuckDB over 61 rows with 6 NULLs, ordered by a unique key, `ROWS` frames for the two
value functions: all four identical on every row. ⚠ The syntax differs: T-SQL puts it after the parentheses,
`LAG(k) IGNORE NULLS OVER (…)`, DuckDB inside, `lag(k IGNORE NULLS) OVER (…)`. ⚠ Measured on 2025 only; the
documented minimum is SQL Server 2022 and Fabric Warehouse is unverified, so gate on the engine version.
Other functions with `IGNORE NULLS` keep declining.

### 3.8 Step 3 — no wrong answers left (2026-10-25)

The T-SQL emitter is vendored now: `dotnet/Fabricator.SqlServer/Substrait/` (mssql-adbc `677676c`, Apache 2.0,
some files MIT from kusto_adbc; LICENSE, notices and a README beside it; ADBC dependencies removed; an
`.editorconfig` keeps IDE0073 off so the original headers stay). Harness: plans → `ts2sql` (compiles the vendored
files straight from the repo) → T-SQL executed on SQL Server → compared with DuckDB.

| fix | where | patch |
|---|---|---|
| `EXCEPT ALL` / `INTERSECT ALL` emitted as their deduplicating forms | to_substrait | `setop_all` → `MINUS_PRIMARY_ALL` / `INTERSECTION_MULTISET_ALL` (the emitter numbers duplicates) |
| integer `sum` overflowed BIGINT on the server | to_substrait | argument cast to `decimal(38,0)` when DuckDB's result is HUGEINT |
| an aggregate's `FILTER` was dropped — every row aggregated (**found by this step**) | to_substrait | translated into the measure filter; an aggregate with `ORDER BY` declines |
| `avg` truncated (integer AVG) and overflowed | emitter | exact sum (`decimal(38,0)`, or the decimal itself) divided by the count, in float, when the plan declares a float |
| strings compared/grouped/sorted by collation (trailing spaces, case, UTF-16) | emitter | the §3.6 key for `=`, `<>`, `<`…`>=`, `BETWEEN`, `IS [NOT] DISTINCT FROM`, `IN`, GROUP BY (read back with `StringFromKey`), ORDER BY, `count(DISTINCT)`; set operations that compare string rows decline (`UNION ALL` stays) |
| string `min`/`max` | both | declared under `extension:fabricator:functions_string`; `StringFromKey(MIN(StringKey(x)))` |
| unary minus, `//`, `%` declined | to_substrait | `negate`, `divide`, `modulus` (DuckDB truncates toward zero, like T-SQL) |

**Measured, 67 queries (68 since §3.9): 52 identical, 11 same values with `DECIMAL(38,0)` for HUGEINT (the pushdown wrapper casts
to DuckDB's bound types), 1 within one ulp, 1 declined by the producer (window), 1 by the emitter (`extract:date`),
1 error on both sides — and 0 wrong answers.**

- ⚠ **The reference results were wrong first**: `COPY … (FORMAT parquet)` writes a HUGEINT as a DOUBLE, so the
  harness flagged an exact server sum against a rounded reference. References go through Arrow now
  (decimal128(38,0)).
- ⚠ **DuckDB's own `avg` differs by one ulp between platforms** (long double: 80 bits on Linux, 64 on
  Windows/MSVC) — measured: Windows DuckDB returns the pushdown's value. The harness reports it as `FLOAT_ULP`.
- ⚠ `error_on_division_by_zero = false` makes DuckDB return NULL where the pushed query errors; the default
  (error) matches.
- ⚠ **Performance not measured**: every string comparison now goes through the key expression, which no index
  seek and possibly no columnstore segment elimination can use. A sargable prefilter (`raw = literal AND key =
  key`, a superset that is exact once combined) is the obvious next step for equality. → BUILT, §3.10.

### 3.9 Date parts as Substrait `extract` (2026-10-25)

Upstream to_substrait sent `year(d)` as a function `year` registered as `extract:date`, which no consumer that
follows the spec resolves (the emitter declined it). Now (`[FABRICATOR-PATCH: OFFER-READY]`, offerable upstream):
the component goes out as the spec's upper-case enum, plus the indexing enum for counting components, and the
compound name carries each enum as `req` — `extract:req_date`, `extract:req_req_date`, also for `pts` (timestamp)
and `pt` (time). `RegisterDeclaredFunction` registers a name the catalog cannot derive (it matches on argument
types, and an enum is not one).

| DuckDB | component | indexing |
|---|---|---|
| `year`, `isoyear` | `YEAR`, `ISO_YEAR` | — |
| `quarter`, `month`, `day`/`dayofmonth`, `dayofyear` | as named | `ONE` |
| `week`/`weekofyear` | `ISO_WEEK` | `ONE` |
| `isodow` / `dayofweek` | `MONDAY_DAY_OF_WEEK` / `SUNDAY_DAY_OF_WEEK` | `ONE` / `ZERO` |
| `hour`, `minute`, `second` | as named | — |

- ⚠ **Declined on purpose: `millisecond`/`microsecond`** — DuckDB counts them from the last full MINUTE
  (`second * 1000 + ms`), Substrait from the last second; `decade`/`century`/`millennium` have no component.
- Measured: `date_part`, a query over every date component and one over the time components — identical; the
  millisecond query declines. ⚠ The time query only checks midnight values (no timestamp column in the data).

### 3.10 An indexable prefilter for string equality — measured (2026-10-25, user-asked)

The §3.6 key is an expression, so `key(col) = key('x')` can never seek an index. Equality now renders as
`(col = 'x') AND (key(col) = key('x'))` (and `col IN ('a','b') AND key IN (…)`):

- the first conjunct is the column WITHOUT the query's binary collation, i.e. under its own, which its indexes
  are built in; a literal of printable ASCII goes out non-Unicode (`'x'`, not `N'x'`) so a `varchar` column is not
  converted; casts to `nvarchar(max)` are looked through (they do not change a value);
- it is a **superset** of exact equality (identical strings are equal under every collation), so it may only be
  ANDed with the key, never stand alone; `<>` and the ordering comparisons get no prefilter (a superset of `<>` is
  not useful, and order under the column's collation is not DuckDB's);
- emitted only when one side is a plain column reference and the other a column or a literal.

**Measured on a 200 000-row table with indexes on `varchar(40)` (SQL collation), `nvarchar(40)` and a UTF-8
`varchar`: all eight shapes — `=` on each type, a non-ASCII literal, `IN` on varchar and nvarchar, a literal with
a trailing space and one differing in case — are an `Index Seek` + residual `Filter`, and the answers are exact
(0 rows for the padded and the case-different literal, where the collation alone would say 1). Control, the key
alone: `Index Scan`, 518 ms against 0 ms.** The 68-query correctness set is unchanged: 0 wrong answers.

- ⚠ Not measured: columnstore segment elimination on strings (the prefilter is what makes it possible at all).

### 3.11 Step 4 — window functions (2026-10-25, user-asked: "existentiell", fast on columnstore)

**Producer** (`TransformWindow` rewritten for 2.0's `BoundWindowExpression`, where window functions are catalog
functions and lag/lead's offset and default are ordinary children). Upstream's version had three wrong answers,
fixed here (`[FABRICATOR-PATCH]`, offerable):

| upstream | here |
|---|---|
| grouped ALL expressions with the same partition/order into one relation — output columns reordered against DuckDB's | only CONSECUTIVE expressions share a relation, so the order is DuckDB's (gate: `w_mixed`) |
| `GROUPS` frames sent as `ROWS` | declined |
| `IGNORE NULLS`, `EXCLUDE`, `FILTER`, an argument `ORDER BY`, `DISTINCT` dropped | declined — except IGNORE NULLS, declared as `<fn>_ignore_nulls` under `extension:fabricator:functions_window`, so a consumer that does not know it fails instead of respecting nulls |

Also declined: RANGE frames with an offset (T-SQL's RANGE has none), non-constant ROWS offsets, `nth_value`,
`fill`. Constant ROWS offsets and the integer `sum` → `decimal(38,0)` widening (as for aggregates) go through.

**Emitter** (`EmitWindow`, `TSqlFunctionCatalog.WriteWindow`): a window aggregate is written by the ordinary
aggregate writer and gets `OVER (...)` after each of its aggregate calls — so the exact avg, the HUGEINT sum and
the string min/max via the key carry over unchanged. Ranking functions, `ntile`, `lag`/`lead`,
`first_value`/`last_value` are written directly. Partitions and window ORDER BY use the §3.6 string key (exact
partitions, peers and ranks); ORDER BY items carry DuckDB's null order. The input is made plain first (T-SQL
evaluates windows after WHERE/GROUP BY and does not nest them), and a block with window columns is wrapped before
a filter (QUALIFY) or an aggregate over it. Declined: a function that needs an order (ranks, lag/lead,
first/last_value) without one, and a frame without ORDER BY that is not the whole partition.

**Measured, 30 window queries against DuckDB's answers: 0 wrong.** Ranks with NULLs (NULLS LAST and DESC),
`percent_rank`/`cume_dist`, `ntile` (300 rows / 7 and per partition), running sums with peers (RANGE),
ROWS frames with offsets, whole-partition aggregates, exact avg and HUGEINT sums over windows, lag/lead with
offsets and defaults, first/last value with frames, exact string partitions, string min/max over windows, ranks
over strings, QUALIFY, a window over GROUP BY, mixed specs, a window inside an expression. The 9 decline cases
decline (6 in the producer, 3 in the emitter).

- ⚠⚠ **IGNORE NULLS is NOT DuckDB's semantics in two places, found by this measurement** (SQL Server 2025):
  - `LEAD(w, 2) IGNORE NULLS`: T-SQL moves 2 rows and then on to the next non-null one, DuckDB counts non-null
    rows only (row 3 of 10,20,30,NULL,50,60: T-SQL 50, DuckDB 60). They agree for an offset of 1 → any other
    offset declines.
  - With a default: when only nulls follow, DuckDB returns the default and T-SQL NULL (T-SQL's default applies
    only where the offset leaves the partition) → a non-null default declines.
  - `first_value`/`last_value` IGNORE NULLS and lag/lead IGNORE NULLS with offset 1 and no default agree.
- ⚠ IGNORE NULLS needs **SQL Server 2022+**; the integration must gate it by server version (Fabric Warehouse
  unmeasured).
- ⚠ Not measured: performance on columnstore (batch-mode window aggregate).

### 3.12 Integration — ABI v96, the Substrait path replaces the hand-written renderer (2026-10-25)

`RemoteExecute` (src/catalog/fabricator_remote_select.cpp, `SubstraitPushdown`) binds a copy of the node, flattens
it, runs the one `FilterPushdown` pass, serializes it with the vendored `to_substrait`, and hands the bytes to the
provider through `remote_translate` (ABI v96). SQL Server translates with the vendored emitter
(`SqlServerSubstrait.TranslatePushdown`) into a query whose columns are `c0…` plus the final ORDER BY keys `k0…`;
the host wraps it as `SELECT CAST(c0 AS <bound type>) AS <bound name>, … FROM fabricator_query(…) ORDER BY k0 …`
(nulls lowest). Gated: `remote_pushdown` on, not a warehouse, SQL Server 2019+ / Azure SQL (UTF-8 collations, §3.6);
IGNORE NULLS needs 2022+. **User decision (2026-10-25): this is THE path.** The §4.1–§4.3 renderer is deleted,
the build option is gone, and protobuf + substrait-protobuf come from `deps/substrait/vcpkg.json` (CI: the shared
build action, with the install root cached).

**Every query shape is offered now** (SELECT, set operations, CTE nodes); RemoteExecute declines what does not
translate. Two host rules on top: a plan of scans / filters / projections / sorts only stays on the table scan
(nothing to gain, and the scan keeps its statistics), and a result with two columns of one name stays local
(DuckDB re-wraps the pushed node as `SELECT * FROM <ref>`, so `SELECT a.id, b.id` would come back as `id, id_1`).

**What the gate and the differentials found — every one a WRONG ANSWER until declined, all measured against DuckDB
(type matrix 80 queries, string functions 38, expressions 76; 0 wrong after the fixes):**

| shape | SQL Server | DuckDB | now |
|---|---|---|---|
| `lower` / `upper` | maps 298 / 308 BMP code points differently (Greek symbol forms, ß, µ, ſ, IPA) — measured over all 63 487 | Unicode | **pushed anyway — the ONE accepted divergence** (user decision 2026-10-25, in the README; `nocase` follows, bound as `lower`) |
| `length`, `substring`, `strpos`, LIKE `_` | UTF-16 code units under `BIN2_UTF8`: `length('𝄞x𝄞')` 5, a surrogate pair split | code points | counted / cut under `Latin1_General_100_CS_AS_SC_UTF8` (never compared under it); `strpos` searches binary and converts the unit position; LIKE `_` still declined (an SC collation compares linguistically) |
| `substring` with a negative start | before the beginning | counts from the end | declined unless start and length are constants >= 0 |
| `lpad` / `rpad` | — (not in the emitter) | fill repeats from its start, a longer input is cut from the right, code points | added, length and fill constant |
| `reverse` | code units | grapheme clusters (é + U+0301 keeps its accent) | declined |
| `regexp_replace` | every match | the first (without 'g') | all `regexp_*` declined |
| binary `=`, GROUP BY, ORDER BY | ignores trailing zero bytes (0x01 = 0x0100) | exact | declined |
| float → integer CAST | truncates; ROUND goes half away from zero | half to even | rebuilt exactly from FLOOR / CEILING (the fraction is exact in binary floating point; no ROUND, which can carry 0.49999999999999994 up) |
| decimal → integer CAST | truncates | half away from zero | `CAST(ROUND(x, 0) AS int)` — exact |
| decimal arithmetic past precision 38 | reduces the scale and rounds | keeps the scale | declined where the server's scale falls below the result's |
| float `/` by a value that may be 0 | error | inf / nan | declined unless the divisor is a non-zero constant |
| `any_value` (and `nocase` GROUP BY) | MAX | first | declined |
| `SELECT rowid …` (upstream to_substrait) | — | — | upstream read the table's FIRST column; declined |
| TIMESTAMP constants (upstream to_substrait) | — | — | sent as a STRING literal (`dval.ToString()`), so `ts = TIMESTAMP '…'` failed on the server; a `precision_timestamp` in the type's unit now. BLOB constants were strings too; binary now |
| `date_diff`, `last_day`, `make_date` | — (no Substrait standard) | — | declared under `extension:fabricator:functions_datetime`; `date_diff` is `DATEDIFF_BIG` — measured on 2000 timestamp and date pairs, equal for every unit but the WEEK, which DuckDB counts as whole days / 7 (`DATEDIFF_BIG(day, a, b) / 7`, also measured exact); `last_day` → `EOMONTH`, `make_date` → `DATEFROMPARTS` |
| `date_trunc` | `DATETRUNC` (2022+) | — | producer: Substrait `round_calendar` (FLOOR, one unit, ISO week); emitter: `DATETRUNC(year/quarter/month/iso_week/day/hour/minute/second, x)`; a DATE goes in cast to TIMESTAMP, as DuckDB returns one; other parts decline |
| TIME columns (upstream to_substrait) | declared as a TIMESTAMP | — | `precision_time`, TIME_NS with 9 digits |

- ⚠ A table with ONE column of a type Substrait cannot express declined EVERY query on it (to_substrait writes
  the whole base schema): an unread column of such a type is now declared opaque (binary).
- ⚠ DuckDB 2.0 binds `lag(x)` with a typed NULL default; it is left out (T-SQL's default is NULL too), or every
  lag/lead over strings declined on the NULL cast.
- An improvement over the hand-written renderer: it computed integer intermediates in BIGINT and answered where
  DuckDB overflows; the plan carries DuckDB's INTEGER now, so both paths raise the overflow.
- ⚠⚠ **A scalar-subquery comparison is NOT a differential**: `SELECT (SELECT … FROM rp…) = (SELECT … FROM db…)` puts
  both sides in ONE expression, the remote optimizer pushes only whole nodes, so BOTH run locally and the check
  compares local with local. Ten such rows in this suite were vacuous — found when a producer mutant (timestamp
  constants as strings) survived them; the matching EXPLAIN row passed because EXPLAIN does not execute. Only the
  `EXCEPT ALL` form compares (set-operation branches ARE pushed one by one). All ten are rewritten; an order check
  is a `rank() OVER (ORDER BY …)` computed on the server instead of a local `list(… ORDER BY)`.
- Gate: `verify_remote_select_mssql` 373 → 439 (§2 rewritten: what still stays local; §2b the former declines,
  each with a differential; §13 windows; §14 the declines above). Mutants killed at their own rows: the binary
  decline removed (the binary routing row), the decimal ROUND removed (the cast differential).
- ⚠ Not measured: columnstore segment elimination; Fabric Warehouse (not offered); CI (needs protobuf).

## 4. Order of work

1. **DELETE shortcut, SQL Server** — BUILT (§2.4).
2. **UPDATE shortcut, SQL Server** — BUILT (§2.5).
3. **Delta metadata-only DELETE** — whole-file removal from partition values and exact statistics.
4. **`get_partition_stats` for Delta** — BUILT (§3.1).
5. **Remote optimizer spike for SQL Server** — BUILT as a spike (§4.1), behind `remote_pushdown`.

### 4.1 The SELECT spike — BUILT (2026-10-24), `remote_pushdown` ATTACH option, off by default

> ⚠ **Default flipped to ON on 2026-10-27** (user decision): a declined translation runs locally anyway, while
> the old default left `x = (SELECT MAX(x) FROM t)` as two full scans — DuckLake's latest-snapshot read, linear in
> the snapshot history (ducklake-catalog.md §9). `remote_pushdown 'false'` opts out; any other value is refused.
>
> ⚠⚠ **Turning it on by default found TWO defects the opt-in had hidden** (the full service tier, which until then
> ran pushdown only in `verify_remote_select_mssql`):
> - **A HANG, not an error.** The pushed query runs through `fabricator_query`, i.e. as a plain pooled read, so it
>   never passed through the scan's routing. In a transaction that has written with MARS off, it waited forever
>   on that transaction's own locks — `verify_mars_dynamic` §3 sat in `LCK_M_S` for eight hours, where the scan
>   refuses cleanly (`EnsureScanCannotSelfBlock`). `remote_translate` now runs with the transaction ambient set
>   and declines while the transaction holds a connection without MARS (and without the `read_isolation` pin);
>   the local scan then routes or refuses. Gate §8.
> - **`COLLATE` on a type that takes none.** The emitter collates every DuckDB string, and json, xml, text,
>   ntext, sql_variant and the CLR types all surface as strings while SQL Server refuses COLLATE on each
>   (Msg 447 / 4189) — `verify_json` failed. The provider now reads those columns once per refresh
>   (`UncollatableColumns`) and the emitter converts json/text/ntext to `nvarchar(max)` first, which MEASURED
>   equal to the scan's text. **xml is NOT** — the server serialises `<b/>`, the scan's reader `<b />`, found by
>   the GROUP BY differential — so xml, sql_variant and the CLR types decline when a query uses them. Gate §7.
>
> ⚠⚠ **And the flip moved what the OTHER suites test, silently.** A suite that does not name the option now runs
> its aggregates, joins and `ORDER BY … LIMIT` pushed, so the scan-pushdown suites (`verify_filter_pushdown`,
> `_limit_`, `_orderby_`, `_projection_`, `verify_collation_pushdown`) were testing the remote path — one went red
> (`ORDER BY [id]` is `[t0].[id]` there), the others passed on text that also proves nothing: their
> `sys.dm_exec_query_stats` proofs read a SERVER-WIDE plan cache that outlives a run. Each now attaches with
> `remote_pushdown 'false'`, asserts `Fabricator Scan` on a shape that would otherwise push (removing the pin dies
> at that row), and clears the plan cache first. The scan path's ROUTING (MARS, `read_isolation`, the same-catalog
> read+write mark, in-out isolation) gets a second run: `run-suites.sh` repeats those six suites with
> `FABRICATOR_REMOTE_PUSHDOWN=false`, the default for an ATTACH that does not name the option. ⚠ When the join
> filter work (§5) lands, its gate must pin the path the same way — EXPLAIN shows which one ran.

`src/catalog/fabricator_remote_select.cpp`. No ABI change: the SQL Server provider reads its own
`remote_pushdown` option and reports `"remote_select": true` in the capability doc (never on a warehouse
profile — measured on box SQL Server only); `FabricatorCatalog::Supports` answers IS_REMOTE and
EXECUTE_QUERY_NODE from it. It must be an ATTACH option, not a setting: DuckDB counts remote catalogs when the
database is attached, and the veto hooks receive no `ClientContext`.

**What it pushes.** A SELECT over ONE base table of the catalog that aggregates or groups: `count(*)`,
`count(col)`, `min`/`max` and integer/decimal `sum` over columns; `GROUP BY` plain columns; a WHERE of
`= <> < <= > >=` between a column and a constant, AND/OR/NOT and IS [NOT] NULL — over integer, decimal and date
columns. It renders `SELECT … FROM [s].[t] WHERE … GROUP BY …` and replaces the node with
`SELECT CAST(c0 AS <DuckDB type>) AS <DuckDB name>, … FROM fabricator_query('<catalog>', '<t-sql>')`.

**Two halves**, because `SupportsPushdown(QueryNode)` has no context: it checks the SHAPE only; `RemoteExecute`
resolves the table, types every column and constant, and renders. A shape whose types do not qualify is
DECLINED there by returning the node itself with this catalog's name restored, so it binds and runs locally
exactly as written. `SupportsPushdown(TableRef)` admits only a plain base table (a catalog-bound TVF or proc
would otherwise be pushed alone), `SupportsPushdown(QueryNode)` refuses every non-SELECT node (DELETE/UPDATE
keep the plan-less shortcut and its row count), and statements are never pushed.

**Exactness, and what each rule is for:**

- strings are out — collation, and T-SQL ignores trailing spaces in `=`; floats are out — NaN and summation
  order; `avg` is out — T-SQL's AVG of an INT is integer division;
- an integer `sum` is rendered `SUM(CAST(col AS DECIMAL(38,0)))` (T-SQL keeps an INT sum an INT and overflows),
  then cast to HUGEINT; a decimal sum is DECIMAL(38,s) on both engines. Accepted divergence: past 10^38 the
  server overflows where HUGEINT would still answer (up to ~1.7 x 10^38) — an error, never another number;
- **a constant must convert LOSSLESSLY to its column's type, checked by a round trip.** ⚠ The first build
  pushed `id > 1.5` as `[id] > 2` and counted 2 rows where 3 are right: DuckDB's "strict" DECIMAL -> INTEGER
  cast ROUNDS (strict governs string parsing only). The converted value must cast back to the original unchanged;
- result types and names are DuckDB's, by the outer CAST and alias (`count_star()`, `sum(id)`).

**What DuckDB does and does not push**, measured: a set operation whose branches name different catalogs pushes
its remote branch ON ITS OWN, and so does a CTAS's query; a subquery JOINED to a local table is NOT pushed
(§1.2 — no partial pushdown for one side of a join). A query inside `FROM (DESCRIBE …)` is never seen by the
optimizer at all. A pushed SELECT inside a transaction runs on the transaction's pinned connection
(`fabricator_query` resolves the catalog and the scan sets the transaction ambient), so it sees uncommitted
writes, like today's scans.

Gate `test/verify_remote_select_mssql.test` (88, service): routing for each admitted and refused shape, an
EXCEPT ALL differential of every query against a second attach WITHOUT the option (the oracle), hard values
where the two paths could be identically wrong (an INT sum past 2^31, a BIGINT sum past 2^63), result types
compared through CTAS tables, read-your-writes in a transaction, and DML still on the shortcut. Mutants, each
killed: the round-trip check removed (§2, line 116), the integer sum not widened (§3, line 155 — the server's
own overflow), strings treated as exact (§2, line 92), and the outer cast removed (§4, line 286). ⚠ The last one
first SURVIVED: §4 compared `DESCRIBE` output, which never reaches the optimizer, so both sides described the
local path. It now compares the types of CTAS tables, whose queries are pushed.

### 4.2 ORDER BY / LIMIT, HAVING and joins within the catalog — BUILT (2026-10-25)

The renderer was first restructured around ONE typer: every FROM table is a SCOPE with a T-SQL alias
(`[t0]`, `[t1]`, …), `ResolveColumn` finds exactly one scope for a column, `RenderValue` renders a column or an
aggregate with the type DuckDB would give it, and one `RenderPredicate` serves WHERE, join ON and HAVING.

- **Grouping is per position.** A plain column in the select list, HAVING or ORDER BY must be a GROUP BY column;
  a column in WHERE or ON is evaluated before grouping and may be any column. ⚠ The first build applied the
  grouping rule everywhere and declined every grouped query with a WHERE on another column — the existing
  routing row caught it.
- **ORDER BY binds like DuckDB:** an integer is a 1-based output position, a bare name that is a select ALIAS is
  that item (an alias wins over a column of the same name — `SELECT k AS id, count(*) AS k … ORDER BY k` sorts
  by the count), anything else is a column or an aggregate. Direction and NULL placement are DuckDB's own
  resolution (`DBConfig::ResolveOrder` / `ResolveNullOrder`), so `SET default_null_order` is honoured.
- **NULL order is rendered, not assumed.** T-SQL has no `NULLS FIRST/LAST` and puts NULLs first for ASC; each key
  becomes `CASE WHEN k IS NULL THEN r ELSE 1-r END, k`. It matters because of LIMIT: with the server's own NULL
  placement a different SET of rows comes back, which no local re-sort can repair.
- **Order is applied twice.** The server sorts with `OFFSET … FETCH` (or `TOP` with no ORDER BY), so only the
  needed rows travel; the keys come back as hidden columns and the wrapper sorts again, because a subquery
  carries no ordering guarantee in DuckDB. Every wrapper reference is QUALIFIED with the function's alias so a
  user alias can never shadow a server column.
- **HAVING** re-renders the aggregate (T-SQL cannot name a select alias there); a constant must convert
  losslessly to the aggregate's DuckDB type (`count(*) > 38.5` stays local; an integer sum compares as
  `DECIMAL(38,0)`).
- **Joins:** INNER and LEFT, REGULAR, with an ON condition — no FULL/SEMI/ANTI/ASOF/NATURAL/USING/implicit cross
  join. Column-to-column comparisons need the same family (numbers with numbers, dates with dates; both
  engines compare two numbers in the wider type). An UNQUALIFIED name two tables share is declined, so DuckDB
  reports the ambiguity itself rather than the server picking one. With a join a plain projection is pushed
  too (VARCHAR passes through — only comparing, grouping and sorting strings depends on collation). The
  decline path re-qualifies EVERY base table of the tree.

Gate `verify_remote_select_mssql` 88 -> 262: ordered results asserted row for row on both catalogs (the NULL
cases under LIMIT), the alias-over-column rule, `default_null_order`, HAVING and join differentials (duplicate
keys, a NULL key, unmatched keys under LEFT, three tables, a self-join), and the refusals. Mutants: the NULL
rank removed (killed at the ASC-NULLS-LAST LIMIT row — the server returned the NULL group among the top three),
aliases ignored in ORDER BY (killed at §7's first routing row), an ambiguous column resolved to one table
(killed at the ambiguity row — the query then answered instead of erroring), LEFT rendered as INNER (killed at
the LEFT join's unmatched rows). ⚠ One SURVIVES, by construction: removing the local re-sort changes nothing
today, because `fabricator_query` delivers rows in the server's order. The re-sort is insurance against an
ordering DuckDB does not promise across a subquery, not something any query can currently observe.

### 4.3 Strings, DISTINCT, count(DISTINCT) and integer arithmetic — BUILT (2026-10-25)

**Strings compare exactly, and only for equality.** Every string comparison key is
`(s + N'|') COLLATE Latin1_General_100_BIN2`:

- the forced binary collation removes case and accent folding whatever the COLUMN's collation is (the rig's
  database is case-insensitive: the server's own `s = 'a'` matches 5 rows where DuckDB matches 2);
- the appended sentinel defeats T-SQL's ANSI padding, under which `'a' = 'a '` in every collation, binary ones
  included;
- `+ N'|'` makes the key NVARCHAR, so a VARCHAR column in any code page converts losslessly and a VARCHAR =
  NVARCHAR join compares one representation. `Latin1_General_100_BIN2` exists since SQL Server 2008, unlike the
  `_UTF8` collations.

That covers `=`, `<>` (as `NOT (… = …)`, which keeps DuckDB's NULL semantics), GROUP BY, DISTINCT,
`count(DISTINCT)` and join keys. A grouped string must be selected as its GROUP BY expression in T-SQL, so it
travels WITH the sentinel and the wrapper strips it with `left(x, -1)` (measured: drops exactly one character,
non-BMP included). ⚠ **Ordering cannot be made exact** on SQL Server 2008+: padding still decides `'a'` against
`'a' + CHAR(1)` (DuckDB: shorter first; SQL Server: the space it pads with is larger), and UTF-16 code-unit order
is not code-point order beyond U+FFFF. So strings never sort, use `<`/`>`, or feed `min`/`max` here. And while
DuckDB's own `default_collation` is set (or a column carries a DuckDB collation), nothing string-shaped pushes:
DuckDB's equality has changed and the server is not told.

**DISTINCT is GROUP BY every select item**, rendered that way so strings take the same key.

**Integer arithmetic** (`+ - *`, unary minus) over SMALLINT/INTEGER/BIGINT columns and integer constants, anywhere
a column may appear (select items, sort keys, aggregate arguments, comparisons). DuckDB keeps the WIDEST column
operand's type (measured: `INTEGER + INTEGER` is INTEGER, `SMALLINT + 1` SMALLINT, `INTEGER - BIGINT` BIGINT); a
constant that does not fit that type would have widened it, so it stays local. The server computes in BIGINT and
the wrapper casts to DuckDB's type, so a FINAL value out of range is still DuckDB's error. ⚠ An INTERMEDIATE
overflow is not: `sum(id * 2)` with id near 2^31 errors locally (INTEGER) and is answered by the server — the
error-against-answer divergence the DML shortcut already accepts, pinned in the gate so a change is noticed.

Gate `verify_remote_select_mssql` 262 -> 373 (§10 strings over adversarial data — case, trailing spaces, a
control character after a prefix, an accent, NULL, a non-BMP character, VARCHAR beside NVARCHAR; §11 DISTINCT;
§12 arithmetic incl. result types through a CTAS). Mutants, each killed: the sentinel removed and the binary
collation removed (both at the first `s = 'a'` row: 3 and 5 matches where 2 are right), `default_collation`
ignored, the grouped string not selected as its key, arithmetic always typed BIGINT, the constant-fit check
removed.

**Not done:** string ORDER BY / min / max (would need the `_UTF8` collations of SQL Server 2019+ and a
varbinary key), booleans, division, decimal arithmetic, expressions in GROUP BY, and a warehouse profile.

### 4.4 `INSERT … SELECT` within the catalog — BUILT (2026-10-29, user-agreed order: DML through Substrait)

An `INSERT … SELECT` whose source reads the same SQL Server catalog runs as ONE `INSERT INTO … (…) SELECT …` on the
server. No ABI change: one additive key (`changes_rows`) in the `remote_translate` JSON and one additive init
property on `RemoteQuery`.

- **DuckDB side** (`remote_pushdown_optimizer.cpp:440`): for an INSERT whose target and SELECT are in one remote
  catalog it asks `SupportsPushdown(InsertQueryNode)`; a yes hands the whole node to `RemoteExecute`, and the
  result is re-wrapped `SELECT * FROM <our ref>`. We answer yes only for an INSERT with a SELECT that reads a
  table, without RETURNING / ON CONFLICT / DEFAULT VALUES — so `INSERT … VALUES` never enters this path.
- **Producer** (`to_substrait.cpp` `TransformInsertTable`, was a throwing stub): a `WriteRel` (`WRITE_OP_INSERT`,
  `OUTPUT_MODE_NO_OUTPUT`). ⚠ DEVIATION FROM THE SPEC, on purpose: Substrait wants every table column in
  `table_schema` with defaults stated in the input; we list only the columns the statement NAMES, found with the
  same discriminator `FabricatorCatalog::PlanInsert` uses (2.0's `ResolveInputProjection` MOVES the bound default
  out for an omitted column), so the server applies its own default (IDENTITY, `getdate()`) exactly as the bulk
  insert does. The input is the child plus a `ProjectRel` whose emit keeps the named columns.
- **Provider** (`SubstraitPlanDecoder.ReadWriteRel`, `TSqlEmitter.EmitInsert`): the input query is the ordinary
  SELECT translation (every exactness refusal of §3.12 applies to the inserted values), rendered without ORDER BY
  unless a TOP needs it. `SqlServerBackend.WriteDeclineReason` then declines where a server INSERT would behave
  differently from the bulk insert, which is the reference: a named IDENTITY column (KeepIdentity there),
  enabled triggers (a bulk copy fires none), an external table (rows go to storage), `mssql_keep_nulls = false`
  (the bulk copy writes a column's default for a NULL). One metadata round trip, at bind.
- **Execution** (`fabricator_remote_dml`, `fabricator_extension.cpp`): bind declares one BIGINT `Count`, sets
  `call_return_type = CHANGED_ROWS` and calls `RegisterDBModify` — so a READ_ONLY attach refuses it and the
  one-writer rule sees it, at the same point as for DuckDB's own INSERT. It runs once, when the statement
  EXECUTES (EXPLAIN runs nothing), after `GetCatalogTransaction` and with the ambient transaction set NON-join-only:
  `BeginWrite` pins the transaction's connection, so ROLLBACK undoes it even as the transaction's first write.
- **Declined** (and with `SET fabricator_dml_pushdown = false`, the user's per-case switch for when streaming through
  DuckDB with a parallel plan is faster): the INSERT runs on the regular path, wrapped `SELECT * FROM (INSERT …)`
  (a DML node in a subquery; the statement still reports CHANGED_ROWS because the inner bind sets it last), and its
  SELECT alone is offered to the Substrait pushdown, so an aggregate source is still computed on the server.

Gate `verify_remote_insert_mssql` (97, service): routing (§1), a differential against the switch over filtered
copies of every type, an aggregate into a wider decimal, a join with integer arithmetic in another column order,
DISTINCT, ORDER BY … LIMIT, BY NAME, an empty result and a list-less insert (§2), server defaults on both paths
(§3), the four declines (§4), transactions incl. the pushed insert as a first write, the one-writer rule and EXPLAIN
(§5), READ_ONLY (§6). Five mutants, each killed at its own row: SupportsPushdown says no (59), join-only execution
(329, the first-write rollback), no RegisterDBModify (383, one writer), no identity decline (270), no trigger
decline (284). ⚠ My first expected total (41) was wrong by hand-summing; recomputed from the data, 38.

⚠ THE TIER FOUND A BEHAVIOUR CHANGE THE GATE DID NOT: `verify_read_isolation` §6 and `verify_mars_off_same_catalog`
§5 pin that `mssql_materialize=false` beside `read_isolation` is REFUSED for a same-catalog `INSERT … SELECT`
("contradict each other"). That refusal lives in the SCAN routing; pushed, the statement has no scan and reads inside
one server statement on the transaction's connection — which is what read_isolation asks for — so it now succeeds.
Both rows now set `fabricator_dml_pushdown = false`, so they keep testing the refusal on the path it belongs to.
⚠ And the first tier run failed the new suite on a TABLE NAME: `ri_src` is also `verify_mars_off_same_catalog`'s,
and SQL Server compiles a whole batch against the existing table before its DROP runs (`213: Column name or number
of supplied values does not match`). The suite's tables are `rins_*` now.

### 4.5 `CREATE TABLE … AS` over a pushed query — BUILT (2026-10-29)

A CTAS whose query the remote pushdown already sent to this catalog creates the table exactly as the regular CTAS
does (the bulk path with `create_table` and no rows — same type mapping, `WITH` options, add-identity, hidden
rowid) and then fills it with ONE `INSERT INTO … (…) SELECT …` on the transaction's connection
(`FabricatorPhysicalRemoteCreateTableAs`, `EXPLAIN`: `Fabricator Remote Ctas`). No ABI change.

- **Why not the optimizer's own CTAS pushdown:** a whole CREATE statement reaches a catalog only through
  `EXECUTE_STATEMENT` + `RemoteExecute(SQLStatement)`, and that path has NO way back — a statement the provider
  cannot translate cannot run locally instead (duckdb-postgres throws there). The CTAS's QUERY is already pushed
  (`RewriteCreateInfo` → `FinishPushdown(query)`), so `FabricatorCatalog::PlanCreateTableAs` recognises it instead.
- **Recognition** (`RemoteInsertForCtas`): every output column, followed through the wrapper's projections and
  ORDER BY, is the pushed query's `c<i>` in order; the planned leaf is a `fabricator_query` table scan on this
  catalog's handle with no `params` / `refresh_cache`; and its SQL is one this catalog pushed — the catalog keeps the
  Substrait plan of every pushed query (`RememberRemotePlan`, keyed by the SQL, bounded at 256). That plan is wrapped
  in the same WriteRel INSERT §4.4 uses and translated again, so every exactness rule and every decline applies.
  Anything else — a plain scan (never pushed), a user's own `fabricator_query`, a source outside the catalog,
  `WITH (location=…)`, `fabricator_dml_pushdown = false` — keeps the regular CTAS.
- ⚠ **Three 2.0 facts it had to work around, each found by a failing smoke run:** (1) at physical planning the
  column references are already POSITIONS (`BoundReferenceExpression`), not bindings; (2) `LogicalGet::parameters`
  is EMPTY on 2.0 (the inputs are not kept), so the SQL comes from the bind data; (3) the child is planned BEFORE
  `PlanCreateTableAs` and its bind data has MOVED into the `PhysicalTableScan` — reading it from the LogicalGet
  dereferenced a null `unique_ptr` (an INTERNAL error). And `ArrowStreamBindData::Copy()` keeps only the base type,
  so the query and handle live there (`query_sql` / `query_handle`), not on `fabricator_query`'s derived bind data.
- ⚠ The wrapper's `CAST(c<i> AS <bound type>)` is skipped: the server converts the value into the column created
  from that bound type. The differential compares the created column types and the rows, incl. `datetime2(7)`
  values at a rounding tie and 50 ns short of the next second — identical.

**A PRE-EXISTING DEFECT THE CTAS GATE FOUND, fixed:** inside a transaction that created or altered a table, a pushed
query over that table blocked until the command timeout — `fabricator_query`'s bind-time DESCRIBE
(`DescribeQuery`, `SET FMTONLY ON`) always opened a POOLED connection, which waits for the transaction's Sch-M lock.
MEASURED with the regular CTAS (pushdown off): `BEGIN; CREATE TABLE t AS …; SELECT g, sum(x) FROM t GROUP BY g`
answered correctly only after `mssql_command_timeout` (15 s set; the suite hung past 300 s on the default). The
describe now runs on the transaction's own connection when it has one, and `QueryBind` sets the ambient transaction
before describing. ⚠ It is a LATENCY defect — the answer was always right, from the execution's fallback — so no
assertion can fail on it; the suite would hang instead.

Gate `verify_remote_insert_mssql` 97 -> 136 (§7): routing incl. a plain scan, a local source and the switch; three
CTAS pairs (aggregate with a string max; a join over every type under ORDER BY … LIMIT; DISTINCT + a window over
datetime2(7)) with equal counts, rows both ways and column types; a CTAS inside BEGIN that the transaction reads and
ROLLBACK removes; CREATE OR REPLACE; IF NOT EXISTS over an existing table. Mutant (never remote) killed at 432.

**Next, user-agreed:** DELETE / UPDATE beyond the v93/v94 shortcut (subqueries, `RETURNING` via `OUTPUT`). MERGE
stays on the rowid path (Substrait has no MERGE). → RETURNING landed first on the rowid path (`bafd129`, ABI v98);
the one-statement DELETE / UPDATE is §4.6.

### 4.6 DELETE / UPDATE as ONE server statement through Substrait — BUILT (2026-10-29)

`SupportsPushdown` now answers yes for a DELETE without RETURNING and an UPDATE without RETURNING and without
`FROM`. `SubstraitDml` (src/catalog/fabricator_remote_select.cpp) rewrites the statement as the SELECT over its
target that DuckDB would evaluate, binds and serializes it like any pushed query (`BindToSubstrait`, no worth
check), wraps it in a WriteRel DELETE / UPDATE (`WrapInWrite`), lets the provider translate it (`TranslateWrite`)
and runs it through `fabricator_remote_dml` (`EXPLAIN`: `FABRICATOR_REMOTE_DML`). No ABI change.

- **The rewrite.** DELETE ⇒ `SELECT 1 FROM target [CROSS JOIN using…] WHERE condition`. UPDATE ⇒
  `SELECT CAST(value AS column type), … FROM target WHERE condition` — the cast is the conversion DuckDB applies
  when it assigns, so DECIMAL → INTEGER rounds the DuckDB way rather than T-SQL's truncation. `SET … = DEFAULT`
  declines (it would need the column default rendered).
- **The emitter** (`TSqlEmitter.EmitDeleteOrUpdate`) writes `DELETE [t0] FROM … WHERE …` /
  `UPDATE [t0] SET [c] = …, … FROM … WHERE …`, where `[t0]` is the target's row in the query. It refuses unless the
  target is read EXACTLY ONCE at the top of the FROM (`TopLevelOccurrences`, outside parentheses, brackets and
  literals) — a self-reference in a subquery is fine, a self-join is not, because the statement must write the row
  the condition was evaluated on. UPDATE also refuses any other table in the FROM: a target row matched twice
  would get whichever match SQL Server takes. A grouped / distinct / limited / windowed input refuses.
- **What it adds over the v93/v94 shortcut:** string predicates (through the exact-string rules of §4.3), subqueries
  (`IN`, `EXISTS`, scalar), `DELETE … USING`, and DECIMAL arithmetic in SET — all of which the shortcut refuses.
  Every decline falls back to the shortcut, and from there to the rowid path.
- **Declines on the provider side** (`WriteDeclineReason`): only an EXTERNAL table, whose rowid DML is routed to
  storage. Triggers and NULL handling need no decline here — the rowid path is itself a T-SQL DELETE / UPDATE.
- **Switch:** `fabricator_dml_pushdown = false` declines INSERT and DML alike (it is the gate's reference oracle);
  `remote_pushdown 'false'` on the ATTACH keeps the shortcut.
- ⚠ `||` was not in the Substrait producer, so a SET with string concatenation stayed local — pushed since §4.21.

Gate NEW `verify_remote_dml_mssql` (87, service): routing, then a THREE-WAY differential — `on` (pushed), `off`
(the switch, i.e. the rowid path) and `loc` (a DuckDB table) — over adversarial data (case, trailing spaces, NULLs,
`NOT IN` over a NULL, a `USING` that matches one row twice, DECIMAL → INTEGER rounding at .5). Every expected count
was computed by running the same sequence on a DuckDB table, not by hand. Transactions: the statement runs on the
transaction's connection, read-your-writes and ROLLBACK. Four mutants killed at their own rows: UPDATE without its
WHERE, any read accepted as the target, no cast in SET, DELETE never pushed. `verify_dml_pushdown_mssql` now attaches
`remote_pushdown 'false'`, since it gates the shortcut; `verify_remote_select_mssql` §6 asserts both routes.

**Next, user-agreed:** the data-modifying "moved" CTE as ONE statement (`DELETE … OUTPUT DELETED.* INTO target`,
declined on a target with triggers, FKs or CHECK), then server-side MERGE after measuring T-SQL MERGE against
DuckDB's semantics. → §4.7.

### 4.7 The "moved" CTE as ONE server statement — BUILT (2026-10-29)

`WITH moved AS (DELETE FROM src … RETURNING *) INSERT INTO dst SELECT * FROM moved`, with `src` and `dst` in the
same SQL Server catalog, runs as `DELETE [t0] OUTPUT DELETED.[c…] INTO dst ([d…]) FROM … WHERE …`
(`SubstraitMoved`, `TSqlEmitter.EmitMovedDelete`). No ABI change. Gate NEW `verify_moved_rows_mssql` (126, service).

- **MEASURED FIRST, user-raised ("das Ergebnis kann auch in einen anderen Katalog oder eine Datei gehen"):** into
  another attached database — a Delta catalog, or DuckDB's own tables — DuckDB refuses the statement itself (*"a
  single transaction can only write to a single attached database"*), and nothing is deleted. Into a file
  (`COPY (WITH moved … SELECT * FROM moved) TO …`) and to the client it works, through the rowid `DELETE … RETURNING`.
  So the one-statement form has exactly one destination worth building: the same catalog.
- **How the optimizer gets there.** A CTE's result is merged into the statement's, so the `DELETE … RETURNING` body
  must report this catalog: `SupportsPushdown(DELETE)` now says yes for a RETURNING of plain columns too. Standing
  alone such a DELETE reaches `RemoteExecute`, where `SubstraitDml` declines it (it would lose its rows — guarded
  inside `SubstraitDml` as well) and the rowid path returns the rows as before. Any OTHER data-modifying CTE makes
  `SupportsPushdown` say no, so DuckDB evaluates it.
- **The plan.** The DELETE is rewritten as SubstraitDml does; its WriteRel gets `OUTPUT_MODE_MODIFIED_RECORDS` with
  the deleted columns in `table_schema`, in the order of the INSERT's columns, and is the input of the INSERT's
  WriteRel. The C# side reads the nested WriteRel and writes `OUTPUT DELETED.<src> INTO dst (<dst>)`.
- **Declines** (each one then runs locally, two steps in one transaction): a moved column whose DuckDB type differs
  from its landing column (`OUTPUT INTO` converts the T-SQL way — measured DECIMAL 2.50 -> INT is 2 on the server, 3
  in DuckDB); a target with an enabled trigger, a FOREIGN KEY on either side or a CHECK constraint (T-SQL refuses
  `OUTPUT INTO` there); source = target; an external table on either side; a RETURNING expression; an outer SELECT
  that filters, groups or computes; `INSERT BY NAME`; the INSERT rules of §4.4 (named IDENTITY, keep_nulls).
- ⚠⚠ **THE DECLINE PATH WAS BROKEN UNTIL THE GATE'S TRIGGER ROW HIT IT.** A declined statement is handed back as a
  SUBQUERY (`SELECT * FROM (<statement>)`), and the binder refuses a data-modifying CTE below the top level (*"WITH
  clause containing a data-modifying statement must be at the top level"*). The rule guards a CTE that a nested query
  would run more than once or not at all; our subquery IS the statement and runs once, so the declined node's CTEs
  are marked `is_trigger_generated` — the binder's own flag for the CTEs trigger expansion generates, whose only
  effect is to skip that check.
- ⚠ **A LATENT HAZARD CLOSED ON THE WAY:** the INSERT decline path pushed the INSERT's `SELECT` alone. With a CTE on
  the INSERT, that SELECT names the CTE as a table, and `RestoreCatalog` on the SELECT alone would qualify it as
  `<catalog>.<schema>.<cte>` — a real table of that name would have been read instead. It now skips the SELECT
  pushdown when the INSERT defines CTEs.
- Gate: routing (pushed; type change, trigger, FK, CHECK, self-move, RETURNING expression, filtering SELECT, switch
  all local; a standalone DELETE … RETURNING returns its rows; COPY to a file; another database refused with nothing
  deleted), then the three-way differential (`on` / `off` / DuckDB tables) over eight moves: a range, an exact
  string against `'b '`, NULLs, `IN (subquery)`, `NOT IN` over a NULL, reordered named columns, a declined `||` (a declined `reverse` since §4.21)
  predicate, and a target with other column names. Transactions: ROLLBACK undoes both halves, COMMIT keeps them.
  Five mutants killed at their own rows: DELETED named by the insert's columns (M8), no OUTPUT INTO decline (FK
  row), no type check (the type-change row), the shape never recognised (routing), the CTE not marked (the first
  decline). `verify_pushdown_guide_mssql` R4 asserts the route, R5 lists the declines (74 -> 76).

### 4.8 T-SQL `MERGE` against DuckDB's `MERGE INTO` — MEASURED (2026-10-29), nothing built

The user-agreed precondition for a server-side MERGE: measure where the two disagree. Eighteen scenarios, each run as
DuckDB `MERGE INTO` on DuckDB tables and as the hand-written T-SQL `MERGE` on box SQL Server from the same data
(target `id INT PRIMARY KEY, v INT, s VARCHAR`; source without a key; harness in the session scratchpad, `merge/gen.py`).

| Scenario | DuckDB | T-SQL | |
|---|---|---|---|
| upsert (MATCHED UPDATE + NOT MATCHED INSERT) | as T-SQL | as DuckDB | equal |
| two source rows match one target, UPDATE | error | error 8672 | equal (both refuse) |
| **two source rows match one target, DELETE** | **error** | **deletes, no error** | ⚠ T-SQL is more lenient |
| two source rows insert one key / match + insert one key | PK violation | PK violation | equal |
| NULL in the `ON` condition | not matched | not matched | equal |
| **three `WHEN MATCHED` clauses / two `WHEN MATCHED … UPDATE`** | first true clause wins | **refused** (at most one UPDATE and one DELETE) | ⚠ shape T-SQL cannot write |
| two `WHEN MATCHED` (conditional DELETE, then UPDATE) | as T-SQL | as DuckDB | equal |
| `WHEN NOT MATCHED BY SOURCE` DELETE / `AND cond` UPDATE | as T-SQL | as DuckDB | equal |
| NULL in a `WHEN … AND` condition | clause skipped | clause skipped | equal |
| UPDATE that changes the join key | as T-SQL | as DuckDB | equal |
| `DO NOTHING` / clause omitted | as T-SQL | as DuckDB | equal |
| empty source + `NOT MATCHED BY SOURCE DELETE` | deletes all | deletes all | equal |
| `WHEN NOT MATCHED AND cond` INSERT | as T-SQL | as DuckDB | equal |
| **`SET v = 2.5` into an INT** | **3** (rounds) | **2** (truncates) | ⚠ conversion |
| **`ON t.s = src.s` with `'a '` / `'B'` against `'a'` / `'b'`** | **no match** | **both match** (padding, case-insensitive collation) | ⚠ string semantics |

So the clause semantics agree; the differences are four, and each has a known answer from the paths already built:
conversion — the CAST into the column type that the UPDATE pushdown (§4.6) emits; strings — the exact-string
rendering of §4.3 (BIN2 + sentinel) in the `ON` and `WHEN` conditions; the clause shapes T-SQL cannot write — decline
(or fold several conditional UPDATEs into one `CASE`, later); the DELETE that T-SQL lets match twice — decline unless
the source is provably unique on the join key, or guard with a duplicate check in the same batch (`IF EXISTS (…)
THROW`), which keeps the T-SQL statement and DuckDB's refusal. ⚠ Not yet measured: triggers (T-SQL MERGE fires each
action's trigger once; the rowid path fires UPDATE/DELETE triggers but its INSERT is a bulk copy that fires none),
`RETURNING` order (equal sets, different order — DuckDB promises none), and Fabric Warehouse (it has MERGE, and no
`OUTPUT`: §6.8 of warehouse-support.md). Substrait has no MERGE, so the plan needs our own encoding — the open
design question for building it. → §4.9.

### 4.9 MERGE as ONE T-SQL `MERGE` — BUILT (2026-10-29)

`SupportsPushdown(MERGE)` says yes without RETURNING; `SubstraitMerge` (C++) and `TSqlEmitter.EmitMerge` (C#) do the
rest. No ABI change. Gate NEW `verify_merge_pushdown_mssql` (90, service).

- **The design, chosen over translating clause by clause.** T-SQL's MERGE takes at most one UPDATE and one DELETE per
  match class (§4.8), DuckDB any number with first-true-wins. So DuckDB's whole decision is computed in ONE query:
  `SELECT <target key>, tp, op, u_<col>…, i_<col>… FROM (SELECT *, 1 AS __fab_t FROM target) t <FULL|RIGHT|INNER>
  JOIN (SELECT *, 1 AS __fab_s FROM source) s ON <cond> WHERE op IS NOT NULL`. `op` (1 UPDATE / 2 DELETE / 3 INSERT,
  NULL for DO NOTHING) is a CASE over every clause in order, guarded by its match class (the two markers) and its
  condition; `u_<col>` is the CAST value of the first UPDATE clause that assigns the column (the old value when the
  row's clause does not); `i_<col>` the INSERT values. That query is bound and translated like any pushed query, so
  every exactness rule holds for every condition and value. The provider wraps it in a fixed MERGE that matches by
  the target's KEY (the rowid columns, null-safe, plus `tp = 1`) — DuckDB's NOT MATCHED BY SOURCE rows carry their
  key and are ordinary matches there, so T-SQL never needs its own BY SOURCE clause or more than three clauses.
- **The encoding.** A WriteRel with op UNSPECIFIED and the advanced-extension ENHANCEMENT `fabricator.merge` — the
  spec's way to give a rel a meaning it does not define, which a consumer must understand or refuse (the decoder
  refuses any other enhancement). `table_schema` names each column's role: `k:<key>`, `tp`, `op`, `u:<col>`,
  `i:<col>`. The enhancement's value `check_duplicates` (set when a WHEN MATCHED … DELETE exists) puts an
  `IF EXISTS (… GROUP BY <key> HAVING COUNT(*) > 1) THROW` before the MERGE: T-SQL deletes a row two source rows
  match without complaint (§4.8), DuckDB refuses to change a target row twice — and DuckDB's rule counts CHANGES
  only (measured: matches that do nothing are no error), which is why the check reads `op IN (1, 2)` rows. Two
  UPDATEs on one row T-SQL refuses itself (8672).
- **Declines** (each runs on the rowid path): RETURNING; THEN ERROR; UPDATE without SET; INSERT without values,
  BY NAME, DEFAULT, or two INSERT clauses naming different columns (one T-SQL INSERT clause would write NULL where
  DuckDB writes the default); a target without a key, with a virtual rowid, with an enabled trigger (T-SQL MERGE
  fires it per action, the rowid path per statement and not at all for its bulk insert), external; an INSERT naming
  the IDENTITY column; `mssql_keep_nulls = false`; whatever does not translate exactly.
- ⚠ **Two first-run defects, both found by the route column, not by the answers.** (1) `op` as STRING constants
  next to `NULL` became a cast of an untyped NULL to a string, which the translation refuses — every MERGE fell back,
  and the 18 scenarios still matched DuckDB because they all ran locally. `op` is an integer now, and the INSERT
  values' ELSE is the last INSERT's value instead of NULL. (2) `SupportsPushdown(TableRef)` admitted base tables and
  joins only, so a SUBQUERY source (`USING (SELECT …) AS src`, the usual MERGE source) never reached us — and with
  it any statement with a derived table in FROM. SUBQUERY, EMPTY_FROM and EXPRESSION_LIST are admitted now; what
  they contain is translated or declined like the rest. ⚠ A source of constants only still declines: DuckDB's
  Substrait producer writes a FROM-less SELECT as a ReadRel without a schema.
- ⚠⚠ **A PRE-EXISTING DEFECT THAT WIDENING EXPOSED, FIXED: a call of the catalog's OWN function failed under
  `remote_pushdown` (the default since 2026-10-27).** `SELECT db.dbo.f(id) FROM db.dbo.t` → *"Scalar Function with
  name f does not exist"*. The optimizer strips the catalog from the call (`db.dbo.f` → `dbo.f`) before
  `RemoteExecute`; a UDF does not translate, the statement declines, and `RestoreCatalog` restores TABLE names only
  — and cannot restore a function with certainty (a bare `f` may be a built-in). Invisible until now because the
  suites call catalog functions mostly without FROM, which the old `SupportsPushdown(TableRef)` vetoed
  (EMPTY_FROM); admitting it turned five suites red (scalar / custom functions, aggregates, function_filter, cdc).
  Fix: `SupportsPushdown(ParsedExpression)` vetoes a FUNCTION / WINDOW call that names this catalog, so such a
  statement is never pushed and stays exactly as written. Gate `verify_remote_select_mssql` §5b (512 -> 518), over a
  table and over a derived table; mutant (no veto) killed at its EXPLAIN row.
- Gate: routing (pushed; RETURNING, a `||` condition (`reverse` since §4.21), THEN ERROR, UPDATE without SET, a trigger target, the switch,
  a constant source all local — the declined ones also EXECUTE, which proves the subquery decline path for MERGE);
  duplicate matches (UPDATE and DELETE refused, no-op matches fine); an IDENTITY-keyed table without a PK; the
  three-way differential over six steps (upsert; three WHEN MATCHED with first-true-wins; NOT MATCHED BY SOURCE with
  a conditional DELETE then an UPDATE; DECIMAL into INT + a NULL condition + DO NOTHING + a conditional INSERT;
  strings in ON against `'B'` and `'f'`; two INSERT clauses); a transaction rolled back. Five mutants killed: no
  duplicate check, no CAST in SET, WHEN conditions dropped, MERGE never pushed, BY SOURCE without the outer join.
  `verify_merge_into_mssql` (120) runs pushed now too and is unchanged; guide M1/M2 (76 -> 80).

### 4.10 The SUBTREE pushdown: a part of a statement that reads the catalog alone — BUILT (2026-10-30, route b)

DuckDB's remote optimizer pushes whole statements, set-operation branches, an INSERT's SELECT and a CTAS query — never
a part of a statement beside local data (`Rewrite(JoinRef)` / `RewriteNode(SelectNode)` only merge the sides'
results; a parent CTE blocks pushdown altogether). So `local JOIN (SELECT region, sum(v) FROM db.dbo.big GROUP BY
region)` scanned all of `big` and aggregated in DuckDB. Measured before: a subquery (Q1) and a CTE (Q2) of that shape
both showed `HASH_GROUP_BY` over `FABRICATOR_SCAN` (10000 rows read for 5 groups).

**What runs now.** An `OptimizerExtension` (`SubtreePushdownPass` in `fabricator_remote_select.cpp`, registered
last) walks the logical plan bottom-up and finds every MAXIMAL subtree that reads ONE `remote_pushdown` catalog
and nothing else, and does more than scan / filter / project / sort (an aggregate, a join of the catalog's tables, a
window, DISTINCT, LIMIT / TopN, a set operation — the "worth it" rule of `BindToSubstrait`). Each is translated by
the same pipeline as a whole statement — a COPY of the subtree, `ColumnBindingResolver`, `to_substrait`,
`remote_translate` — and replaced by `SELECT CAST(c<i> AS <type>) … FROM fabricator_query(…) [ORDER BY k…]`, bound
by a CHILD of the statement's binder (fresh table indices). `ColumnBindingReplacer` then maps the subtree's old
output bindings to the replacement's, by position, over the whole plan. Anything that declines keeps the subtree.

- ⚠⚠ **TWO PASSES, and the first one is the one that matters.** Built post-optimize first, two of nine shapes
  declined: DuckDB's optimizer had rewritten them into forms with no T-SQL — an aggregate pushed through a join
  (`count_star() EXPORT_STATE` below the join, `combine_aggr` / `finalize` above it) and a filtered `row_number()`
  (TOP_N_WINDOW_ELIMINATION: `arg_min_nulls_last` + `UNNEST`). So the PRE-optimize pass translates each subtree as
  `BindToSubstrait` translates a statement (filter pushdown on the copy only) and replaces it before those rewrites;
  a replaced subtree is then a leaf to the join order optimizer, so it also stays together. The POST pass is a
  second chance — for a subtree the join order produced, and for one DuckDB rewrote into a translatable form:
  `regexp_matches(note, '^f')` becomes a prefix test, which T-SQL has exactly (`LIKE N'f%'` under BIN2), so a whole
  statement the remote optimizer declined is pushed after all (the guide's S3 needed a pattern DuckDB cannot
  simplify, `'i.s'`).
- ⚠ **Post-optimize alone cannot find every remote join**: measured, the join order optimizer put
  `loc ⋈ st_dim` first and `st_big` on top, so no pure subtree existed (Q3). The pre pass sees the query as written —
  which has the same limit when the SQL itself interleaves local and remote tables.
- **What is never part of a pushed subtree**, each a rule in `PushableScanCatalog` / `PushableExpression` /
  `WritesFabricatorCatalog`:
  - a function of an attached catalog (`catalog != system`): a SQL Server UDF carries only its bare name in the plan,
    so `dbo.abs` would translate as `ABS` — the gate creates exactly that UDF (`abs(x) = x + 1000`), mutant killed;
  - a statement that WRITES a table of a fabricator catalog: the write and the reads beside it are routed together
    (the pinned connection, the same-catalog read+write mark), which a `fabricator_query` would bypass — mutant
    killed at the routing row;
  - a scan with time travel, table filters, a row id / virtual column, or `filter_pushdown` (a join's dynamic filter
    keeps a POINTER to its target scan; never set on SQL Server);
  - a parameter (`$1`) or a subquery expression. ⚠ The parameter rule is DEFENSE ONLY: DuckDB plans each EXECUTE
    of a prepared statement with its values (measured: two EXECUTEs, two server queries with different constants),
    so the mutant without it survives.
- **The root.** A plan whose root is itself such a subtree is a whole statement the remote optimizer declined (a CTE
  relation in its plan, a result naming a column twice) — replaced like any other; the result's names are DuckDB's
  own, they are not in the plan. ⚠ EXPLAIN and every EXCEPT-based differential WRAP the statement, so they reach the
  subtree as a CHILD; only a top-level statement reaches the root path, and its routing is visible on the server
  alone — the gate counts the translated query's `execution_count` in `sys.dm_exec_query_stats` before and after
  (mutant without the root path killed there).
- **Inside a transaction that wrote the table** the pushed query runs on the transaction's own connection and sees
  the uncommitted row (measured: A's sum +1000; rolled back after).
- **CTEs**: one translation per CTE body, whether it is materialized, inlined, or `NOT MATERIALIZED` read twice
  (DuckDB copies the replaced body; `fabricator_query` survives the copy).
- Kill switch `SET fabricator_subtree_pushdown = false` (host setting, default true) — the reference oracle.
- Gate NEW `verify_subtree_pushdown_mssql` (123): routing + a differential against DuckDB on LOCAL COPIES of the
  server tables for ten shapes (subquery, CTE, LEFT JOIN, a join of two server tables, a scalar subquery, IN with
  HAVING, a filtered window, ORDER BY … LIMIT, DISTINCT, a CTE read twice) and two that stay local (a filtered scan,
  a UDF); the `abs` UDF, a write into the catalog, a root CTE, a root with a duplicate name, a prepared statement, a
  transaction, the switch. Strings with case and a trailing space as group and join keys. Mutants: no pre pass
  (dies at P4's routing), UDF veto (the `abs` row), write rule, root path — killed; parameter rule — survives (above).
  Guide S5 / S6 + the switch (`verify_pushdown_guide_mssql` 80 -> 94).

### 4.11 Every routing decision in `duckdb_logs` — BUILT (2026-10-30, user-asked)

Until here a decision was visible only with `FABRICATOR_DUMP_REMOTE=1` / `FABRICATOR_DUMP_DML=1`, printed to
stderr; EXPLAIN shows where a statement ran, never why. Now `FabricatorRoutingTrace`
(`src/include/fabricator_trace.hpp`) writes each decision to DuckDB's logger at DEBUG under `Fabricator.Remote` /
`Fabricator.Dml`, and still prints when the variable is set.

- **Registered log types.** `enable_logging(['Fabricator.Remote'])` REFUSES an unregistered type ("Unknown log
  type"), so the extension registers `Fabricator.Remote`, `Fabricator.Dml` and `Fabricator.Sql` at load (DEBUG —
  selecting one also sets the level). `Fabricator.Sql` is the managed category, forwarded through `host_log`.
  ⚠ The managed categories reach `duckdb_logs` WITHOUT `FABRICATOR_LOG_LEVEL` — that variable only drives the file
  sink; the README said otherwise and was corrected.
- **Reasons that were thrown away before:** the exact conjunct / SET value that does not render (the serializers'
  new `why`, which replaced their own env-gated prints); for a subtree that reads only the server, the expression
  that keeps it local (`AGGREGATE stays local because it calls pg_net() of catalog mssql`, or a parameter). The
  provider's own decline reasons (`TranslateRemotePlan`) already reached C++ as `reason` and are logged there, once.
- to_substrait's strict-mode preamble ("Strict Mode is set to true, and the following warnings/errors happened.") is
  replaced by `no Substrait translation:`.
- Subtree messages name the pass (`before` / `after the optimizers`), since both passes see most plans.
- Gate: `verify_pushdown_guide_mssql` L1–L4 (94 -> 109); documented in sql-server-pushdown.md §Why it ran where it ran.
- Seen on the way, not changed then: a bare `SELECT count(*) FROM db.t` stays local (`row id and virtual columns are
  not supported`). It always did; nothing asserted it. FIXED 2026-10-31, §4.20.

### 4.12 Inverse-thinking pass over the pushdowns — three fixes (2026-10-30, user-asked)

Asked "how would this go wrong?" rather than "does it work?". The differentials cannot see a pushdown that answers
RIGHT but slowly, nor one that is right only because the emitter happens to lack a mapping. Three changes:

1. **A WHERE above a part was left outside the pushed query — MEASURED, a regression of route b.** The pre pass
   replaced `(SELECT region, sum(v) … GROUP BY region) r … WHERE r.region = 'A'` before DuckDB's filter pushdown ran,
   so the server aggregated the whole table (0.73 s) where the scan with the condition did an index seek (0.08 s; 200k
   rows, index on `region`). Now the pre pass leaves a subtree alone when a conjunct of some filter reads only its
   output tables (`MayReceiveFilter`); the pass after the optimizers takes it with the condition inside. Cross-side
   conditions (`r.s > l.w`) do not hold it back. Gate: `verify_subtree_pushdown_mssql` P11 + §3b (the sent T-SQL
   contains WHERE, nothing replaced before the optimizers).
2. **Functions whose value is not fixed by their arguments are vetoed explicitly** (`ConsistentNode` /
   `ConsistentPlan`, `FunctionStability != CONSISTENT`), on both routes. Until now `now()`, `current_date`, `random()`
   stayed local only because the T-SQL emitter has no mapping for them — the day one is added, the server's clock and
   time zone would answer silently. Gate: N3 + the whole-statement row in §3b.
3. **The plan is restored when it does not take the replacements.** `ColumnBindingReplacer` / `ResolveOperatorTypes`
   ran outside every catch — a failure there failed the statement (an INTERNAL one would invalidate the database).
   The replaced subtrees are kept with their slots and put back, references re-pointed. And the pass returns at once
   for a plan with no `fabricator_scan`: it runs for every query of every database the extension is loaded in.

Recorded, not changed: per-subtree planning round trips (translate + describe, per pass, per EXECUTE of a prepared
statement); literals instead of parameters fill SQL Server's plan cache with single-use plans; the dependency on
DuckDB internals (pre-optimize hook, child binder, `ColumnBindingReplacer`, `LogicalOperator::Copy` via serialization,
the remote optimizer itself — the 2.1 canary already shows two red suites).

### 4.13 SQL Server scalar UDFs inside a pushed part — BUILT (2026-10-30, step 1 of the user-agreed order)

Before, every call of a function of the attached catalog kept its part local, because in the plan a function carries
only its bare name and `dbo.abs` would have translated as `ABS`. MEASURED before: `SELECT id, db.dbo.f(k) FROM db.dbo.t`
fetched the table and called `SELECT [dbo].[f](@p0) UNION ALL SELECT [dbo].[f](@p1) …` once per batch.

- **to_substrait** declares a function of a non-system catalog under `extension:fabricator:catalog_function`, named by a
  JSON array of its schema path and name — never through the YAML built-ins, so no name can resolve as a built-in.
- **The provider decides** (`TSqlEmitter.CatalogFunctions`, `SqlServerBackend.ServerFunctionName`): a function in the
  CatalogFunctionSet (C#-authored, Fabric API, CDC) does not exist on the server → declined; every other scalar of the
  catalog is a discovered routine → `[schema].[name](args)`. The C++ side stays provider-agnostic.
- **The subtree must read the function's catalog**; a call of the catalog's function makes a part worth pushing on its
  own (a projection or filter over one scan). A catalog aggregate / window function stays vetoed, and so does a function
  of another catalog. Catalog scalars are exempt from the stability veto: run by the provider either way.
- **Same answer by construction:** the catalog UDF is registered SPECIAL_HANDLING (it sees NULL arguments) and its
  DuckDB result type is the mapping of the server's own return type; inline, the server computes the same value.
- **Route a still vetoes it** (the decline-after-strip problem of §5b in verify_remote_select_mssql); a whole statement
  calling a UDF reaches the server through the subtree pass's root replacement instead.
- Gates: `verify_subtree_pushdown_mssql` P12 (UDF pushed, differential) + §3 dbo.abs pushed as `[dbo].[abs]` (log) +
  N2 (C# cf_add stays local, reason logged) — 155; guide S8 + L2 (119); remote_select §5b rewritten (536). Mutant
  "catalog functions by their bare name" killed at P12. Not built: table-valued functions (step 3), UDFs in pushed DML.

### 4.14 Correlated subqueries and materialized CTEs in a pushed part — BUILT (2026-10-30, step 3a)

The plan was an APPLY encoding; looking at the plans first showed it is not needed here. DuckDB 2.0 decorrelates
BEFORE the optimizers into a `__duckdb_delim_<n>` CTE: the outer rows, their distinct correlation keys, plain joins
(an inequality becomes a join `k > k`, a LIMIT per row a window). Everything but the CTE already translated; the
decline was "local: CTE".

- `InlineCtes` (on the translated COPY, after the pre pass's FilterPushdown): each CTE reference becomes a projection
  carrying the reference's own table index over a copy of the definition. Copies keep the definition's internal
  indices — each sits under its projection, which is all the operators above can see.
- **DuckDB hangs the decorrelation CTE at the TOP of the statement**, above the join with local data. ⚠ The first
  build inlined a reference from a definition ABOVE the replaced subtree — RETRACTED by §4.16: DuckDB still read that
  definition itself, so the server's copy was a second read of the same rows in another statement. `SinkCtes` now moves
  the CTE down to the smallest subtree holding all its references instead, and a subtree must define every CTE it reads.
- Pushed: LATERAL with inequality + LIMIT, WITH … AS MATERIALIZED (its definition). Local: a correlated SCALAR
  subquery — its decorrelation keeps a SINGLE join, which T-SQL has no exact form for (before §4.16 only the part
  beside it was pushed, reading the outer rows twice); a correlated EXISTS with an OR (to_substrait fails to bind a
  column — declines safely).
- Gates: `verify_subtree_pushdown_mssql` P13–P15 (167, each with its differential), guide S9. Mutant "a CTE reference is
  never replaceable" killed at P13. Not built: a correlated subquery whose OUTER side is local (step 4, key shipping),
  TVFs inside a pushed part (step 3b, needs a table-function relation in Substrait).

### 4.15 A table-valued function with constant arguments in a pushed part — BUILT (2026-10-30, step 3b-1)

- The TVF scan keeps its call on the bind data (`ArrowStreamBindData::table_function_path/_args/_catalog`, set by
  `FabricatorTableFunctionBind` for a positional-only, non-proc function called with constants).
- **to_substrait** gets a static `table_function_resolver` (set at load): such a scan becomes a ReadRel naming the
  function, its base schema the function's columns, marked by the enhancement `fabricator.table_function` whose VALUE
  is an `Expression.Nested.Struct` of the argument literals. ⚠ Not a virtual table beside the name: `named_table` and
  `virtual_table` are a protobuf ONEOF, so the first attempt lost the name and declined every call.
- **The C# decoder** reads that enhancement (any other still refuses the plan); the emitter writes
  `FROM [s].[f](<literals>) AS [tN]` through `TSqlEmitter.CatalogTableFunctions` → `ServerTableFunctionName`, which
  declines a function of the CatalogFunctionSet (C#) and a stored procedure (no result columns in ROUTINE_COLUMNS).
- `PushableScanCatalog` takes such a scan as a leaf of its catalog. A correlated call (`FROM db.t, db.tvf(t.a)`) is
  still read and applied per chunk (§10 of lateral_unnest_analysis.md), not one statement — that needs an APPLY
  relation (step 3b-2).
- Gates: `verify_subtree_pushdown_mssql` P16 (TVF sent by name, differential vs a local table macro) + N4 (`cf_range`,
  C#, refused by the provider) — 184; guide S10/S11. Mutant "the resolver never recognizes a TVF" killed at P16.

### 4.16 Second inverse-thinking pass — CTE inlining READ THE SAME ROWS TWICE (2026-10-30, user-asked)

Asked over steps 1–3b-1 before building 3b-2 on top of the CTE inlining. The main finding was a WRONG ANSWER
introduced by §4.14, measured, and fixed:

- **DuckDB reads a CTE once; inlined, the server reads it once per reference — at READ COMMITTED (locking), two reads
  in one statement can see different rows.** Harness (scratchpad inverse2/): `ic_b` static with every key, a writer
  inserting `ic_t` rows with keys found nowhere else in `ic_t`, the probe
  `SELECT (SELECT max(b.v) FROM ic_b b WHERE b.k > t.k - 1 AND b.k <= t.k) FROM ic_t t` (an equality would be
  simplified to a plain LEFT JOIN that reads `ic_t` once). Pushed: **28 NULLs in 8 of 8 runs**; DuckDB: 0. A missing
  key in the second copy loses the row's subquery result — an answer no database state gives.
- **Fix:** InlineCtes counts copies; a definition copied more than once marks the plan with the Substrait optimization
  hint `fabricator.repeated_reads` (ignorable by any other consumer). The provider accepts it only where one statement
  reads consistently (`ConsistentStatementReads`: warehouse, READ_COMMITTED_SNAPSHOT, a SERIALIZABLE / SNAPSHOT
  transaction) or — no pinned connection, `ALLOW_SNAPSHOT_ISOLATION` on — runs it on a pooled connection at SNAPSHOT
  (remembered by SQL text, `_snapshotQueries`, and routed through the existing `snapshotRead` route); otherwise it
  declines. A failed probe answers "not consistent". Re-measured: TestDB (snapshot allowed) pushed at SNAPSHOT, 0
  NULLs; a database with RCSI pushed, 0 NULLs.
- **The cross-boundary variant was worse and found only by the gate:** a reference inlined from a definition ABOVE the
  pushed part made the server re-read rows DuckDB had read in ANOTHER statement — no isolation level reconciles two
  statements. Replaced by `SinkCtes` (move the CTE down to its references; a part must define what it reads).
- Gates: `verify_subtree_pushdown_mssql` §3b — P14 runs `at SNAPSHOT`, and inside a transaction holding a connection
  it stays local with the reason; N5 (scalar subquery) local. 193.
- Recorded, not changed: (2) a heavy definition is computed once per reference on the server — only the repeated_reads
  rule bounds it, not its cost; (3) parallel lateral TVF sessions each open their own read transaction, so one
  per-row call can see different points in time per thread (the `_each` form had one exchange per call); a
  `read_isolation 'snapshot'` DuckDB transaction does not extend to them either; (4) a non-deterministic UDF is now
  evaluated inline, i.e. a different number of times and in a different place than per batch; (5) the plan-restore
  path is still ungated.

### 4.17 A TVF called per row of a server table: CROSS APPLY in the pushed query — BUILT (2026-10-30, step 3b-2)

- **The plan.** DuckDB decorrelates `FROM db.t, db.tvf(t.a)` before the optimizers into a `__duckdb_delim` CTE over
  the outer scan, a DISTINCT over the arguments, the in-out `LogicalGet` of the TVF with that as its ONE child, and an
  `IS NOT DISTINCT FROM` join back. The get's columns are the callee's (its column ids) followed by the child columns it
  passes through (`projected_input`); the child's leading `arg_width` columns are the arguments.
- **Recognizing it.** The lateral bind data of a discovered TVF now records its catalog (`FabricatorTvfBind` passes
  `info.catalog`, not for a proc); `FabricatorLateralApply` accepts the get when every argument slot reaches the
  wire (no bind-time constant), the child is exactly `[args | passed through]` and no virtual column is read.
  `AnalyzeSubtree` makes such a get pure when its child is a pure part of the same catalog, and worth it.
- **Encoding.** to_substrait's `apply_resolver`: a CrossRel marked `fabricator.apply`, left = the child, right = the
  ReadRel of the function (`fabricator.table_function`) whose arguments are FIELD REFERENCES into the left input, and
  an emit putting the callee columns first. The C# decoder accepts that one CrossRel enhancement (any other still
  refuses the plan); `TSqlEmitter.EmitApply` renders `FROM <left> CROSS APPLY [s].[f](<left columns>) AS tN`. An
  inner apply is right for LEFT JOIN LATERAL too: the outer join DuckDB put above it keeps the unmatched rows.
- **⚠ Two things only building it found.** (1) DuckDB's `ColumnBindingResolver` does NOT descend below a
  `LogicalGet`, so the translated copy kept `BOUND_COLUMN_REF`s in the get's input — `ResolveGetInputs` resolves each
  such input on its own. (2) An UPSTREAM to_substrait bug: `IsPassthroughProjection` set no output mapping for a
  projection that starts as `#0, #1` and then repeats a column (`#0, #1, #0, #1` — exactly the get's input), so the
  ProjectRel emitted two columns instead of four. Fixed `[FABRICATOR-PATCH: OFFER-READY]`.
- **Cost.** The CTE is read twice (outer + DISTINCT), so the plan carries `fabricator.repeated_reads` and needs a
  consistent read (§4.16); elsewhere the per-chunk lateral session runs. Rewriting the join-back into a direct
  `t CROSS APPLY f(t.a)` would read once — **measured 2026-10-31 and NOT worth building** (warm, TestDB):

  | shape | pushed APPLY (reads twice) | hand-written direct APPLY | per-chunk lateral (subtree off) |
  |---|---|---|---|
  | 1M-row outer, one-row TVF | 0.12 s (server 88 ms) | 0.15 s | 5.5 s |
  | 100-row outer, TVF returning 1M rows | 0.23 s (server 189 ms) | 0.16 s (server 153 ms) | — |

  The second read is lost in the noise where the outer is large, and costs ~35 ms of server time (same logical
  reads) where the TVF's output is large — the join-back over the result, not the read. What matters is that the
  APPLY is pushed at all: ~46x over the per-chunk session.
- Gates: `verify_subtree_pushdown_mssql` P17 (aggregated + joined with local data, log asserts `CROSS APPLY
  [dbo].[st_tvf]`), P18 (LEFT JOIN LATERAL, computed argument, NULL argument), N6 (local outer stays `Fabricator
  Lateral Batched`) — 193 -> 210; guide S12. Mutants killed at P17: the apply never recognized; the passthrough fix
  reverted.

### 4.18 Review of the pushdown stack — Roofline/Amdahl → Inverse Thinking/Napkin Math → Chesterton → Pre-Mortem (2026-10-30, user-asked)

Method (user-proposed): find where it pays (which resource bounds a query shape, which share of a statement the pushed
part is), look there for waste and wrong answers, check each fence before removing it, and ask what the fix itself
could break. Correctness was taken as its own axis: the performance lenses cannot see a silent wrong row.

**Rig.** TestDB: `rv_big` 1,000,000 rows (`id` PK, `region VARCHAR(10)` with 100 values, `v INT`), nonclustered index
on `region INCLUDE (v)`, `rv_dim` 100 rows (`region` PK), inline TVF `rv_tvf(@r)` = `rv_big WHERE region = @r`. Server
numbers from `sys.dm_exec_query_stats` (last run, warm), plan operators from `sys.dm_exec_query_plan`. Baseline = the
same query hand-written in T-SQL through `fabricator_query`. Harness: scratchpad `review/`. ⚠ Wrapping a pushed query
in `SELECT count(*) FROM (…)` changes what is sent (the server elided a one-row aggregate: 0 reads) — measure the
statement itself.

| case | pushed: server ms / CPU ms / reads | hand-written: server ms / CPU ms / reads |
|---|---|---|
| C1 `WHERE region = 'R042'` (count, sum) | seek, 31 reads — the indexable prefilter `[region] = 'R042' AND <exact key>` holds | seek, 31 |
| C2 string join + filter on the dimension | 81.9 / 81.9 / 33 | 1.4 / 1.4 / 33 |
| C3 `GROUP BY region` | 536.9 / **6740** / 2825 | 148.7 / 148.7 / 2751 |
| C4 TVF per row of `rv_dim` (CROSS APPLY, 3 regions) | 93.3 / 93.3 / 97 | 4.6 / 4.6 / 95 |
| C5 `SELECT DISTINCT region` | 536.3 / **6496** / 2825 | 116.2 / 116.2 / 2751 |

Same reads, same access paths: the gap is CPU, all of it in the string key.

**F1 — equality runs on the ORDER key since the Substrait port (measured; the biggest lever).** §3.6 says *"Equality /
GROUP BY keep the cheaper §4.3 key"*; the emitter uses `StringKey` (VARCHAR(MAX) + two REPLACEs + sentinel, built for
exact ORDER) for `=`, IN, GROUP BY, DISTINCT, PARTITION BY and join keys alike. The comment *"One key serves equality,
grouping, distinct and order"* has no reason recorded beside it, and the hand-written renderer that carried the §4.3
key was deleted with the port. Grouping 1M rows by `region`:

| key | server ms | CPU ms |
|---|---|---|
| K0 today's `StringKey` | 564 | 7229 |
| K5 §4.3 `(s + N'\|') COLLATE Latin1_General_100_BIN2` | 28 | 336 |
| K1 `s COLLATE …_BIN2_UTF8` + `DATALENGTH(s)` | 39 | 416 |
| K2 `CAST(s AS varbinary(8000))` + `DATALENGTH(s)` | 34 | 359 |
| K3 plain `GROUP BY region` (inexact, serial plan) | 171 | 171 |

⇒ K5 looked like the answer (~21× less CPU, exact by §4.3's measurement for case, padding and VARCHAR = NVARCHAR).
K1/K2 are NOT safe as general keys: DATALENGTH differs between VARCHAR and NVARCHAR for the same text, and a BIN2
collation without `_UTF8` converts a UTF-8 VARCHAR to code page 1252.

**⚠⚠ AND K5 IS WRONG TOO — found because the user asked to check mssql-adbc's new UTF-16 work first ("riecht nach
ähnlichen collation und size problemen").** mssql-adbc's Substrait translator had not changed since our port; its new
`adbc.mssql.string_encoding=utf16` (text as `clast.utf16`, for UTF-16 consumers) is of no use to DuckDB, whose strings
are UTF-8 — but its notes name UNPAIRED SURROGATES, and that is the case K5 gets wrong. NVARCHAR may hold one; our scan
reads it as U+FFFD, so DuckDB sees `NCHAR(0xD800)`, `NCHAR(0xDC00)` and `NCHAR(0xFFFD)` as ONE value. K5 compares UTF-16
code units and keeps them apart. Measured over 14 adversarial values (case, trailing space, NUL and `\x01` after a
prefix, lone surrogates, U+FFFD, `?`, precomposed vs combining é, an emoji, empty, NULL): DuckDB over the read rows 12
groups, today's key 12, **K5 14**. The UTF-8 conversion stays; what made it expensive is the MAX type:

| key | CPU ms over 1M rows | groups of the 14 (DuckDB: 12) |
|---|---|---|
| K0 today (`varchar(max)`, two REPLACEs, NUL sentinel) | 7353 | 12 |
| K6 `CONVERT(varchar(max), (s + N'\|') COLLATE Latin1_General_100_BIN2_UTF8)` | 2700 | 12 |
| K7 the same as `varchar(8000)` | 431 | 12 |
| K7 with the length check below | 477 | 12 |
| K5 `(s + N'\|') COLLATE Latin1_General_100_BIN2` | 337 | **14** |

**BUILT (2026-10-30): `TSqlEmitter.EqualityKey`** — for every EQUALITY context (`=`, `<>`, IN, GROUP BY, DISTINCT,
`count(DISTINCT)`, PARTITION BY, IS [NOT] DISTINCT FROM, join keys); `StringKey` stays for ORDER BY, `<`/`>`/BETWEEN,
string min/max. A grouped string is read back with `StringFromEqualityKey` (`LEFT(k, LEN(k) - 1)`).
- **varchar(8000) only where it cannot cut**: a bare column whose declared length is at most 2666 characters (three
  UTF-8 bytes per UTF-16 unit, plus the sentinel). The lengths come from one cached `sys.columns` read
  (`BoundedStringColumns`, tables, views and table functions; dropped with the other metadata), and a column passed
  through a derived table keeps its bound. Everything else — expressions, literals, long or MAX columns — takes K6.
- **The check**: the bounded key is `CASE WHEN DATALENGTH(col) > <declared bytes> THEN <conversion error naming
  fabricator_refresh_cache> ELSE col END`, so a column widened out of band since the lengths were read FAILS the query
  rather than cutting two different values to one key. (A NULL does not trip it: `DATALENGTH(NULL) > n` is unknown.)
- Measured after (server ms / CPU ms): C3 GROUP BY 537 / 6740 → **58 / 643**; C5 DISTINCT 536 / 6496 → **52 / 612**;
  C4 APPLY 93 → **5.4** (hand-written 4.6).
- Gate: `verify_remote_select_mssql` §16 (536 -> 570): the 14 values, eight equality shapes each pushed and compared
  with DuckDB over the read rows (EXCEPT ALL both ways), and the widened-column check. Mutants, each killed at its own
  row: a UTF-16 key (the surrogate groups), the bounded key without the check (two values cut to one group), and
  varchar(8000) for every key (the long values after the refresh).

**F2 — the CONVERT side (user's point), checked.** A string FILTER already keeps an indexable superset ANDed beside
the exact key (`IndexableString`: the column without the query collation, an ASCII literal as non-Unicode) — C1
seeks. The expensive contexts are those with no such prefilter: GROUP BY / DISTINCT / IS NOT DISTINCT FROM (DuckDB's
decorrelation join-back). F1 is what makes them cheap; a key on the column side forfeits index ORDER there (no stream
aggregate over the index), which is acceptable at K5's cost.

**F3 — fixed cost per pushed statement ≈ 5–10 ms** (describe + execute + compile; by Amdahl it dominates small
statements only). Seen on the way: one statement's bind described `rv_big` twice (`WHERE 1 = 0`). Minor.

**F4 — the APPLY reads its outer side twice** (DISTINCT over the arguments + join back), so it needs a consistent read
(§4.16) and costs a second scan of the outer. A direct `t CROSS APPLY f(t.a)` is not built.

**F5 — a waiting lateral call SPINS A CORE (measured).** A lateral TVF call blocked 13.7 s by another session's lock
cost **13.6 s of process CPU**; the same query at `SET threads = 1` 0.1 s; a blocked plain scan 0.2 s; a slow but
unblocked lateral call 0.03 s over 1.2 s. Cause (DuckDB 2.0, read): the client's result loops wait only on `BLOCKED`
(`query_result.cpp`, `CompleteDelegatedInternal`); when the one running task is another thread's synchronous call,
the executor answers `NO_TASKS_AVAILABLE` and the loop polls again without waiting. Our scan avoids it because its
pull returns BLOCKED (scan-concurrency.md §5f); the lateral operator calls synchronously. Not SqlClient: an
`ExecuteReaderAsync` probe spun the same. Fix options: an asynchronous lateral call (operator BLOCKED + reschedule), or
upstream (any long synchronous operator on a worker thread spins the client).
**Stock repro attempted (2026-10-30), NOT reproduced:** with the 2.0.0 wheel, a Python UDF sleeping 3 s ran on the
CLIENT thread at every `threads` setting (process CPU 0.0–0.19 s), and a `read_csv` over a local HTTP server that
answers after 4 s cost 0.8 s CPU (the `range` work beside it — the CSV is sniffed at bind, on the client thread).
Neither puts a blocking synchronous call on a WORKER while the client has nothing left to run, which is the shape the
lateral operator has. An upstream report needs that shape first (harness scratchpad `review/f5/`).

**F6 — a lateral TVF inside a transaction that wrote BLOCKS ON ITS OWN LOCKS (measured, pre-existing since step 2).**
`BEGIN; INSERT INTO db.dbo.rv_big …; SELECT … FROM db.dbo.rv_dim d, db.dbo.rv_tvf(d.region) f …` → 30 s, then
*"Execution Timeout Expired"*. The lateral session opens its OWN connection and transaction (`BeginInOutScope`), so
under locking READ COMMITTED it waits for the statement's own transaction; under RCSI it would not see the
transaction's writes (inferred, not measured). The pushed APPLY declines there (repeated reads + pinned connection),
so it lands on this path.
**BUILT (2026-10-30):** each call asks the scan router (`PinnedReadConnection`, `RouteScan` with read-your-writes +
drain) and, when the transaction holds a connection, runs on it under the transaction's `ExecGate` — parallel
sessions share that one connection, and without MARS it admits a single reader. Otherwise the session keeps its own
connection as before. Measured: the case above 30 s timeout → 0.16 s, the inserted row in the answer; 100 distinct
arguments in parallel inside a write transaction, MARS on and off, both see the transaction's own row. Gate
`verify_table_inout` (97 -> 115, both MARS modes, a local outer so the lateral path is certain); mutant (never the
pinned connection) dies at the first in-transaction call with the original *"Execution Timeout Expired"*.

**F7 — argument conversion in the APPLY: no difference (measured).** Truncation into VARCHAR(10), Unicode into a
non-Unicode parameter (emoji → `??`), trailing spaces, DECIMAL(12,4) → DECIMAL(10,2) rounding and NULL: the pushed
APPLY and the lateral path return identical rows (EXCEPT ALL both ways empty).

**F8 — the lateral call cannot be interrupted** (read): `SqlServerTvfLateral` runs `ExecuteReader()` without the
`InterruptScope` token the scans use, so Ctrl+C waits for the server or the 30 s timeout.
**BUILT (2026-10-30):** the call executes with the scope's token and hands the scope to the stream. Measured from the
DuckDB 2.0.0 Python wheel, `interrupt()` after 1 s of a ~23 s server call: returned after **23.5 s** before, **1.0 s**
after (*"lateral_call failed: A task was canceled"* — a cancelled scan reports the same way, docs/cancellation.md).
Not in a tier: no suite can send an interrupt mid-statement (harness scratchpad `review/interrupt.py`).

**F5, the decision.** An ASYNCHRONOUS lateral operator is not available in DuckDB 2.0: an intermediate operator may
return `BLOCKED`, but `ExecutionContext` carries no `InterruptState`, so nothing could reschedule the task — it would
hang, not spin. And the spin is not ours alone: any synchronous extension operator running on a worker while the
client thread has no task re-polls the same way (the scalar UDF crossing too). Left as an upstream candidate — the
client's result loops (`query_result.cpp`, `CompleteDelegatedInternal`) wait on `BLOCKED` but not on
`NO_TASKS_AVAILABLE` — to be reproduced on a stock build before it is filed (docs/duckdb-upstream-issues.md rule).
F6 removed its worst trigger here (a 30 s wait on the statement's own locks).

**Chesterton.** F1's fence (one key) had no recorded reason and contradicted §3.6 — but HALF of it was load-bearing
after all: the UTF-8 conversion is what makes an unpaired surrogate compare as DuckDB reads it. Removed only the
half that was not (the MAX type and the ORDER-only escaping). Kept, each with a
recorded reason: string ORDER only via `StringKey` (padding, UTF-16 order), `lower`/`upper` (Unicode 5.0 tables), the
SINGLE-join refusal (§4.16), the `filter_pushdown` veto (dangling dynamic filter), binary comparisons declined
(trailing zero bytes).

**Pre-mortem of the fixes.** F1: a grouped string travels as its key and the wrapper decodes it — a decoder that does
not match the key garbles every grouped string (loud in the differential suites); a VARCHAR = NVARCHAR join must
still meet one representation (`+ N'|'` does that); the adversarial values (NUL, `\x01` after a prefix, non-BMP,
trailing space, case) must stay in the gate. F5: an asynchronous in-out operator changes when output appears — the
provenance/drain logic was measured on the synchronous shape. F6: a pinned connection shared by parallel lateral
sessions must be serialized, and a warehouse has no MARS. Wrong, each of these degrades into "runs locally" or an
error — except F1's decoder, the one change here that could alter values.

### 4.19 Table statistics compared with mssql-adbc's GetStatistics — BUILT (2026-10-30, user-asked)

mssql-adbc (`SqlServerStatistics.cs`, commit `7fbf0dd`) reads the same kind of server metadata: approximate row counts
and per-column distinct counts, none on Synapse dedicated / Fabric Warehouse (no `sys.dm_db_stats_histogram`; we gate
on `Profile.IsWarehouse` likewise). Four differences, each checked:

| | mssql-adbc | ours before | ours now |
|---|---|---|---|
| row count | `sys.partitions.rows` | `sys.dm_db_partition_stats` | `sys.partitions` |
| object without partitions (a VIEW) | not reported (tables only) | **0** | unknown (NULL) |
| distinct count, several stats objects | the one that sampled most rows, then the newest | the MAX | as mssql-adbc |
| the histogram's NULL step | not counted | counted as a value | not counted |
| null count | reported | not reported | not reported |

- **A view counted as EMPTY (measured).** `COALESCE(SUM(row_count), 0)` gave a view 0, which DuckDB believes: a view
  over the 1M-row `rv_big` showed `est=0` beside the table's `est=1000000`, and a join with the 100-row `rv_dim` built
  its hash table over the million rows. Here that cost ~10% (0.35 s vs 0.32 s — the transfer dominates); in a
  multi-way join a false zero misorders the plan. Unknown now, so DuckDB falls back to its own default.
- **The DMV needs VIEW DATABASE PERFORMANCE STATE (measured).** A user DENIED it gets *"The user does not have
  permission to perform this action"* from `sys.dm_db_partition_stats` and the count from `sys.partitions`;
  `sys.dm_db_stats_histogram` and `sys.dm_db_stats_properties` work for it either way. That was the long-recorded
  WARNING for low-privileged logins (and why `verify_default_schema_mssql` grants the permission to its login —
  no longer needed, left in place).
- **Distinct counts** follow mssql-adbc's rule: a stale stats object can carry the larger count, and the NULL step is
  not a value. Costing only — ungated, since the rig's tables have one stats object per column.
- **Null counts are deliberately NOT adopted**, for the reason min/max are not: DuckDB would treat "no NULLs" as a
  fact (dropping `IS NULL` branches), and a stale statistic would then drop rows. Distinct counts only ever weigh
  join and filter estimates.
- mssql-adbc also offers EXACT row counts (`COUNT_BIG(*)`) and reads a whole database at once; neither fits a scan's
  lazy per-table estimate (and `count(*)` is pushed to the server anyway — a bare one too since §4.20).

Gate: `verify_cardinality` rewritten from "the query text reached the plan cache" (which an earlier run satisfies) to
behaviour — a table's `~500 rows`, a view never `~0 rows`, and the estimate for a login denied the permission — 4 ->
17. Mutants killed at their own rows: the view's 0 back (the view row), the DMV back (the low-privileged row).

### 4.20 A count that reads no column — FIXED (2026-10-31)

`SELECT count(*) FROM db.t` stayed local and moved every row: a scan nobody reads a column of gets ONE column from
`LogicalGet::GetAnyColumn`, which takes the empty column if the function declares one, else the ROW ID — and our
tables declare a row id (the key). to_substrait refuses a row id, so the statement ran as `SELECT [id] FROM t`.
Measured on `rv_big` (1M rows): 0.29 s warm, against 0.06 s for the pushed `SELECT COUNT_BIG(*)`.

The value of that column is never read, so the translated COPY may read any real column in its place:
`ReadRealColumnForUnused` (both routes, after the copy is made) puts column 0 where a scan's only column is the row id
or the empty column AND no expression, set operation, CTE or the root reads it (`CollectReadBindings`); the
subtree pass lets such a scan count as pushable with the same rule. The server then gets `COUNT_BIG(*)` and no column
at all. The original plan is untouched: a declined part keeps reading the narrow key rather than column 0.

Pushed now: the bare count, a count over a cross join of two server tables, a bare count beside local data (the
subtree pre-pass), a bare EXISTS. Still local, as they must be: `count(rowid)`, `max(rowid)`, a UNION of row ids.

**Found on the way, a regression of §5 (way A), FIXED:** `WHERE rowid > …` on any SQL Server table with a key raised
`INTERNAL Error: fabricator: a pushed filter on a virtual column` — with `remote_pushdown` off too. Since the scan
takes table filters, DuckDB hands it a row-id filter; the local re-check knew only the table's own column types. It
now takes the row id's type (and a provider virtual column's); the server's WHERE still skips it (superset).

Gate: `verify_remote_select_mssql` §17 (570 -> 593). The table's FIRST column is not the key and is NULL in a third of
the rows, so a build that put column 0 in place of a row id the query DOES read would answer `count(rowid)` /
`max(rowid)` wrong. Mutants killed at their own rows: no rewrite (the routing row), the read check dropped (the
`count(rowid)` control), the row-id type dropped from the re-check (the `rowid > 290` row).

### 4.21 `||`, trim, left / right, a date cast of a timestamp — PUSHED (2026-10-31)

An inventory of 42 common expressions in a pushed `WHERE` (scratchpad `inv/`) found most of them pushed already. Four
stayed local for reasons of ours, not of T-SQL:

- **`||`** had no Substrait name ("Could not find function `||`"). It is Substrait's `concat` with
  `null_handling = ACCEPT_NULLS` now (a NULL operand gives NULL — T-SQL `+`), and DuckDB's `concat()` asks for
  `IGNORE_NULLS` (T-SQL `CONCAT`) instead of taking the emitter's first variant by luck. Only for a VARCHAR result:
  a BLOB or a LIST `||` still declines.
- **One-argument `trim` / `ltrim` / `rtrim`** had no Substrait signature (Substrait's take the characters to remove).
  DuckDB's remove every Unicode space separator — MEASURED over every code point: exactly the 17 of category Zs
  (U+0020, U+00A0, U+1680, U+2000–U+200A, U+202F, U+205F, U+3000) — so the producer passes those 17. MEASURED on the
  server over every BMP code point of a UTF-8 varchar: `TRIM(<the 17> FROM s)`, `LTRIM(s, <the 17>)` and
  `RTRIM(…)` remove exactly those 17. `LTRIM` / `RTRIM` with characters need SQL Server 2022 (they work at
  compatibility level 150 there), so they decline below it, like `DATETRUNC`.
- **`left` / `right`** — DuckDB binds the count as BIGINT; narrowed to i32 like `substring`. The emitter wrote a bare
  `LEFT(s, n)`, unreachable until now: it uses the code-point collation now (as `SUBSTRING` does) and requires a
  constant count of at least 0 — a negative one keeps all but that many in DuckDB and errors in T-SQL.
- **`CAST(ts AS DATE) = …`** — a §5 side effect. Since a SQL Server scan takes table filters, the FilterPushdown pass
  in `BindToSubstrait` (and the subtree pass) hands the copied scan DuckDB's OPTIONAL range filter derived from the
  cast (`optional: ts >= … AND optional: ts <= …`), whose internal function has no translation, so the whole statement
  declined. `TransformGet` drops an optional filter (`WithoutOptionalFilters`) as a whole condition or as a conjunct
  of an AND: it only prunes, and the predicate it was derived from stays in the plan. Anywhere else it still
  declines. A dynamic (join) filter still declines too — dropping it would lose the pruning it brings.

Still local after the inventory, and left so: `ILIKE` (no exact case-insensitive T-SQL — the reason `lower()` is the
one accepted divergence), `TRY_CAST` (declined on purpose), `sign()` (returns TINYINT, and T-SQL's tinyint is
unsigned), `concat_ws`, `strftime`, `hash`, `epoch`. `GREATEST` / `LEAST` are SQL Server 2022 functions pushed without a
version check (pre-existing; this box cannot test an older server).

Gate: `verify_remote_select_mssql` §18 (593 -> 644): strings written BY DuckDB through the extension (NBSP, the
ideographic and the hair space, a tab, a zero-width space, a supplementary character, a combining mark, NULL, ''),
an `EXCEPT ALL` differential both ways over nvarchar and UTF-8 varchar, and pinned rows per change. Guide S14
(`verify_pushdown_guide_mssql` 167 -> 185). Five mutants killed: `||` with concat's NULL handling, trim given only
`' '`, `LEFT` without the code-point collation (all three at the differential), a negative count pushed (its own row,
with T-SQL's "Invalid length parameter" as the answer), the optional filter kept (the routing row).
`verify_merge_pushdown_mssql` and `verify_moved_rows_mssql` used `||` as their "no translation" control; it is
`reverse` now (counts unchanged, 90 / 126).

### 4.22 Bare names through `USE` — PUSHED (2026-10-31)

MEASURED first: under `USE rp.dbo` a bare SELECT was pushed — by the subtree pass, which works on the BOUND plan —
but every DML statement stayed local (INSERT … SELECT streamed the rows, a DELETE with a subquery took the rowid
path), because the subtree pass never takes a statement that writes and DuckDB's optimizer never offered it.

- **The cause is DuckDB's** (`RemotePushdownOptimizer::Rewrite(BaseTableRef)`, case 2): a bare name is checked as
  `<catalog>.main.<name>` — `DEFAULT_SCHEMA`, not the search path's schema — and this catalog has no `main`.
  duckdb-postgres answers it in `LookupSchema` (`c045f6c`, the commit adopting the optimizer): `main` resolves to
  the database's default schema unless a real one exists. Ours does the same now (`FabricatorCatalog::LookupSchema`,
  only for a provider declaring a default schema, i.e. SQL Server). Upstream candidate:
  [duckdb-upstream-issues.md](duckdb-upstream-issues.md) §14.
- **⚠ The same fix would have opened a WRONG ANSWER on our side, so it came first:** `RemoteExecute` qualified a bare
  name with the catalog's DEFAULT schema — pushed or declined. After `USE rp.sales` with the same table name in `dbo`,
  that reads `dbo.t` where DuckDB reads `sales.t` (measured with the mutant: dbo's sums). `QualifyBareNames` gives
  each bare name the first search-path schema of this catalog that holds it (the binder's rule) before anything else.
- **Limit, DuckDB's:** the optimizer's check still looks in `main` = the default schema, so a table that exists only
  in another schema stays local when named bare. Safe; it is pushed when named `schema.table`.
- **Found on the way, a pre-existing defect, FIXED:** `fabricator_refresh_cache` only ever ADDED schemas — a schema
  dropped out of band stayed listed and resolvable until the next ATTACH (measured: `duckdb_schemas()` kept it). A
  stale empty `main` then kept the alias from applying. `RefreshCache` retires a root an earlier discovery reported
  and this one does not; a schema created in the session that discovery cannot see (an empty one on an object store)
  stays.

Gate: `verify_remote_select_mssql` §19 (644 -> 687): `rp.main` = dbo; bare INSERT … SELECT and DELETE with a subquery
pushed under `USE rp.dbo`; under `USE rp.rs_use_s` the same names hold other rows and the pushed SELECT / INSERT /
UPDATE reach those; a real `main` wins; a dropped `main` leaves at the refresh. Guide S15 (185 -> 195). Four mutants
killed: no search-path qualification (the rs_use_s sums), no alias (the `rp.main` row), the alias over a real `main`
(its row), no retirement (run with a stale `main` on the server: the alias row).

## 5. Dynamic join filters for SQL Server scans — BUILT (2026-10-30, step 4 way A)

**As built.** The record below (2026-10-27) took the scan's T-SQL to have to be EXACT for `filter_pushdown = true`.
It does not: the scan RE-APPLIES the filters itself.
- **Capability `superset_filter_pushdown`** (SQL Server, ATTACH `dynamic_filters`, default true): the host sets
  `filter_pushdown = true` on the catalog's scans (`BuildScanFunction`) and the bind data's `reapply_filters`.
- **Re-application:** `ArrowStreamInitGlobal` turns the live TableFilterSet (the static filters DuckDB erased from the
  plan + the runtime join filters) into one predicate over the output columns (`TableFilter::ToExpression`);
  `ArrowStreamScan` evaluates it per chunk with an `ExpressionExecutor` (a copy per thread) and slices — a chunk with no
  row left is skipped, since 0 rows would end the scan. The server's WHERE (the existing FilterNode render, now with
  the runtime `IN` / range) only has to be a superset; measured `WHERE [region] IN (@p0, @p1)`, parameterized and
  sargable.
- **⚠ TOP**: a server TOP before a filter the scan re-applies could drop rows. The spec already withheld TOP when the
  bind-time filter or the live render was non-empty; a MANDATORY live filter that did not render (a string `>` under a
  case-insensitive collation) now withholds it too — but a TopN's own runtime bound (an internal optional wrapper)
  does not, or TOP + ORDER BY would never be pushed again (`verify_orderby_pushdown`, `verify_collation_pushdown`
  caught exactly that).
- **The subtree pushdown** declined every scan with `filter_pushdown` (a "dangling pointer" the join was thought to
  keep). It holds only `shared_ptr<DynamicTableFilterSet>` (join_filter_pushdown.hpp) — the veto is gone. A scan's
  STATIC table filters become a `FilterRel` again in to_substrait (`TransformGet`: `ToExpression` per filter, declined
  for a column the read does not return); a runtime filter there has no translation and declines the part, which
  keeps the scan with its filter. Whole-statement pushdown goes through the same translation.
- Side effect: a DELETE's predicate now reaches `FindExactScan` as table filters (the prefix test as a string range);
  the DML trace names the column now instead of `#[0.0]` (guide L3).
- **Measured:** a 2-row local table JOIN the 1M-row `rv_big`: 1.36 s → 0.19 s.
- **Gate:** NEW `verify_dynamic_filters_mssql` (30): the `IN` reaches the server (and not with `dynamic_filters
  'false'`), the answers against the switched-off catalog under a case-insensitive, padding collation, static filters
  on the scan path, TOP with a re-applied filter, TOP + ORDER BY still pushed. Guide J1. Mutants killed at their own
  rows: no re-application (a static filter's count), TOP despite a mandatory filter (the string `>` case), the
  capability not declared (the routing row).
- ⚠ EXPLAIN does not show a join's runtime filter (it is made at execution — a TopN's bound does show); the gates read
  the statement in `Fabricator.Sql`. DuckDB plans one only for a probe side worth filtering (none for the guide's
  4-row `pg_sales`, hence its 3000-row `pg_clicks`).

### 5.1 A pushed part takes the join's keys too — BUILT (2026-10-30, step 4 way B, ABI v99)

Way A narrows a plain scan. A remote part correlated with local keys — a Top-N per key (a window), a scalar
`max`/`count` per key (an aggregate) — is not a scan: the subtree pushdown (§4.10) sends it as one query, and before
this the server computed it over EVERY key (measured on the 1M-row `rv_big`, 100 regions, 3 local keys: Top-3 per key
0.27 s, scalar max+count 0.16 s). Five pieces were needed, and none of them alone changed anything:

1. **`IS NOT DISTINCT FROM` → `=`** (`EqualizeJoinKeys`, PRE pass). DuckDB joins a decorrelated subquery back on
   `key IS NOT DISTINCT FROM key`, and its join-filter optimizer skips that comparison
   (`join_filter_pushdown_optimizer.cpp`: `TODO: Need OR predicates`). The two comparisons differ only when BOTH keys
   are NULL, so where one side is provably never NULL they agree on every row — any join type, any aggregate, `count`
   included (the non-null side has no NULL-key row to count). `NeverNull` follows a binding through filters that
   reject NULL (`IS NOT NULL`, a comparison other than (NOT) DISTINCT FROM), projections, order/limit, windows and a
   single-grouping-set aggregate's group key. An explicit `IS NOT DISTINCT FROM` written by the user over a nullable
   remote column stays as written (gate row).
2. **The replacement takes table filters** (`TakeTableFilters`): `filter_pushdown`, `reapply_filters` and
   `push_projection` on the `fabricator_query` get. Not for a WHOLE statement — no join sits above it, and its own
   ORDER BY could not stand in the derived table the filter would wrap it in (measured: Msg 1033).
3. **A cardinality** (the largest table the part reads). Without one DuckDB took the replacement for ONE row, built the
   hash table over it, and the join's keys went into the LOCAL side — the plan showed `FABRICATOR_QUERY (est=1)` as
   the build side.
4. **ABI v99 `execute_query_filtered`**: the query plus the scan spec its filters became. SQL Server renders it with
   the catalog scan's own code as `SELECT [c…] FROM (<query>) AS [q] WHERE [c<i>] IN (@p0, …) OPTION (RECOMPILE)`;
   with no filter in the spec the query runs as it is. The host re-applies every filter, so the WHERE only has to be a
   superset (way A's rule).
5. **SQL Server has to apply the keys BELOW the window or aggregate**, and it did not: the partition/group key was the
   exact UTF-8 key expression (§4.18 F1), not the column the outer filter names — measured 210 ms with the outer IN
   against 200 ms without. For a `varchar`/`char` column the key's conversion is one-to-one, so `PARTITION BY key,
   [col]` / `GROUP BY key, [col]` forms the same groups, and the column is read back as itself (`KeyColumn`); SQL
   Server then pushes the IN to the index. Not for `nvarchar`: an unpaired surrogate becomes U+FFFD in the key.

**Two findings on the way, both general:**
- **An ASCII equality value is bound as `varchar`** (`FilterWhereBuilder.Param`). Inferred `nvarchar` against a
  `varchar` column under a SQL_ collation converts the COLUMN, so the index is scanned: the plain scan of way A
  measured **200 ms → 4 ms** on the server for three keys. ASCII has the same bytes in every code page, so the row
  equal to the value still matches under any collation (superset). Ranges and `<>` keep the old binding.
- **The cached parameterized plan of the wrapped query ran parallel and WAITED** — 1.7 s elapsed, 0.7 s CPU; with
  the values embedded 30 ms, `MAXDOP 1` 40 ms. Hence `OPTION (RECOMPILE)`: the keys differ per execution anyway, and
  the compile costs a few milliseconds. Not applied to plain scans.

**Measured** (warm, 1M rows, 3 local keys): Top-3 per key **0.27 s → 0.044 s**, scalar max+count per key
**0.16 s → 0.048 s**, plain join **0.37–0.67 s → 0.017 s**.

**Not covered — a DuckDB limit, an upstream candidate:** with a NULL among the local keys a RIGHT join (the scalar
shape) keeps the NULL build row, and `JoinFilterPushdownInfo::PushInFilter` refuses an IN list that contains a NULL
(`FilterCombiner::ContainsNull`); only a min/max range is pushed, which a case-insensitive server cannot render. An
equality never matches NULL, so dropping it from the list would be sound. The answers stay exact; the query is computed
for every key, as before.

**Gate:** `verify_dynamic_filters_mssql` §4 (30 → 96): Top-N and scalar per local key with keys the server folds
together ('r1', 'R1 '), answers against the switched-off catalog; the wrapped statement and `OPTION (RECOMPILE)` in
`Fabricator.Sql`; the partition and group by key + column in `Fabricator.Remote`; NULL local keys; an explicit
`IS NOT DISTINCT FROM` over NULLs; the varchar keys in the plan cache. Guide J2.

### 5.2 One predicate, sent once — FIXED (2026-10-30)

With `filter_pushdown` on, DuckDB hands a pushed predicate to the scan TWICE: to `pushdown_complex_filter` at bind
(the spec's bind-time filter) and erased into the table filters (the live filter). The spec ANDed both, so SQL Server
received `WHERE ([yr] = @p0 AND [yr] = @p1)` — harmless for the answer, a second parameter and a longer plan-cache key
for every scan. `WithoutBindDuplicates` (arrow_ingest.cpp) flattens both into AND-leaves and drops a live leaf that the
bind-time filter already holds, compared with each constant reference replaced by the constant's type and SQL text (the
two halves index one constants list at different positions). A leaf only drops when an identical one stays, so the
WHERE stays the same predicate. Gate `verify_sqlgen_catalog` §4: the member statement carries one parameter, the
plan-cache check counts only this run's statements (34 → 36); mutant (no dedupe) killed there.

### The record before it was built (2026-10-27)


DuckDB hands a hash join's DYNAMIC filter (the build side's min/max, an IN list) only to a scan whose function sets
`filter_pushdown = true` (`join_filter_pushdown_optimizer.cpp`: `if (!get.function.filter_pushdown) return;`).
Only the Delta `native_read` catalog sets it (`verify_delta_catalog_dynamic_filter`). SQL Server scans do not,
deliberately: with the flag DuckDB also ERASES the static filters it hands the scan and trusts the scan to apply
them exactly, while our T-SQL rendering is a superset (a case-insensitive collation makes `name = 'X'` match
`'x'`).

- ⚠ `supports_pushdown_type` is NOT a "dynamic filters only" switch: a column it refuses has ALL of its table
  filters moved into a filter above the scan, the dynamic ones included (`plan_get.cpp`).
- **The shape that would work:** `filter_pushdown = true` plus `supports_pushdown_type` accepting only columns
  whose type renders EXACTLY in T-SQL — the strict rule of the DELETE shortcut (§2.4: integers, DECIMAL, DATE,
  BOOLEAN). Every filter kind DuckDB may place on such a column must then render exactly or the scan must refuse:
  comparisons, IN lists, IS [NOT] NULL, conjunctions, and the dynamic min/max / IN filters themselves. A filter
  that renders weaker on an accepted column is a WRONG RESULT, since nothing re-applies it.
- **Worth it for:** ordinary joins of a SQL Server table against a small local or remote table without remote
  pushdown. **Not** a fix for DuckLake's `WHERE snapshot_id = (SELECT MAX(snapshot_id) …)`: the outer scan would get
  the filter, but the MAX side is still a full read — remote pushdown is what removes both (ducklake-catalog.md §9).
