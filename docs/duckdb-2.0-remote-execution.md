# DuckDB 2.0: planner, optimizer and catalog features for remote execution

**Status: survey, nothing built (2026-10-24).** Inputs: the DuckDB 2.0 source (our submodule, `v2.0.0`,
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

## 4. Order of work

1. **DELETE shortcut, SQL Server** — BUILT (§2.4).
2. **UPDATE shortcut, SQL Server** — BUILT (§2.5).
3. **Delta metadata-only DELETE** — whole-file removal from partition values and exact statistics.
4. **`get_partition_stats` for Delta** — BUILT (§3.1).
5. **Remote optimizer spike for SQL Server** — `EXECUTE_QUERY_NODE` behind an ATTACH option, SELECT only
   at first, with a T-SQL renderer and a veto for everything it cannot render.
