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
- **Build**: `-DFABRICATOR_SUBSTRAIT=ON` (default OFF) with protobuf 5.29.3 + substrait-protobuf 0.102.0 from a
  manifest-mode vcpkg install (`CMAKE_PREFIX_PATH`, and `Protobuf_PROTOC_EXECUTABLE`, which protobuf's config
  demands even though nothing is generated). MSVC builds it unchanged.
- **The pipeline** (probe level 5): bind a copy → `RecursiveDependentJoinPlanner` + `DecorrelateIndependent` →
  `ResolveOperatorTypes` + `ColumnBindingResolver` (to_substrait needs positional references — the first run
  failed on `BOUND_COLUMN_REF` without it) → `to_substrait` in strict mode. No optimizer.
- ⚠ **A name resolved only through `USE db.dbo` is not pushed at all** — DuckDB's remote optimizer acts on
  catalog-qualified names; `RemoteExecute` was never called.
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
  Equality/GROUP BY keep the cheaper §4.3 key. ⚠ Needs UTF-8 collations (SQL Server 2019+, Fabric Warehouse);
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

## 4. Order of work

1. **DELETE shortcut, SQL Server** — BUILT (§2.4).
2. **UPDATE shortcut, SQL Server** — BUILT (§2.5).
3. **Delta metadata-only DELETE** — whole-file removal from partition values and exact statistics.
4. **`get_partition_stats` for Delta** — BUILT (§3.1).
5. **Remote optimizer spike for SQL Server** — BUILT as a spike (§4.1), behind `remote_pushdown`.

### 4.1 The SELECT spike — BUILT (2026-10-24), `remote_pushdown` ATTACH option, off by default

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
