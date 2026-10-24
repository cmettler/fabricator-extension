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

No in-tree DuckDB test exercises it either: `test/extension/test_remote_optimizer.cpp` is an unrelated
2024 OptimizerExtension demo that ships a serialized logical plan over a socket.

### 1.4 What it would take for SQL Server, and why it is not first

- **A dialect renderer.** `RemoteExecute` receives a DuckDB `QueryNode`; `ToString()` renders DuckDB SQL,
  not T-SQL (functions, `LIMIT`, literals, casts and quoting all differ). We would render the AST to T-SQL
  ourselves and use the `SupportsPushdown(expression)` veto for everything we cannot render — the same
  exactness discipline as §2, over a far larger surface.
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
  ⚠ UNVERIFIED: whether a table WITHOUT a PK or IDENTITY (no rowid today, so no DELETE/UPDATE at all) would
  get through binding — `Binder::BindRowIdColumns` runs before planning, so the refusal may come first.
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

## 4. Order of work

1. **DELETE shortcut, SQL Server** — plan-less `PlanDelete`, exact rendering or fallback, RETURNING falls
   back. Gate: differential (every statement also run with the shortcut disabled, identical table state
   and row count), plus a fallback case per rule in §2.1.
2. **UPDATE shortcut, SQL Server** — the same, with the SET-expression rules.
3. **Delta metadata-only DELETE** — whole-file removal from partition values and exact statistics.
4. **`get_partition_stats` for Delta** (COUNT/MIN/MAX from the log).
5. **Remote optimizer spike for SQL Server** — `EXECUTE_QUERY_NODE` behind an ATTACH option, SELECT only
   at first, with a T-SQL renderer and a veto for everything it cannot render.
