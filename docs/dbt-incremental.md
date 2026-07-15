# dbt incremental models + schema evolution with `fabricator`

> Findings from the `dbt_mssql_test/` harness (gitignored), validated against **box SQL Server 2025** and a
> **Fabric Warehouse** with the per-transaction connection model
> ([transaction-concurrency.md](transaction-concurrency.md)). Models: `materialized='incremental'`,
> `incremental_strategy='append'`, `on_schema_change='append_new_columns'`, run at `--threads 4`.
>
> **Box and Fabric behave identically** here: concurrent incremental append works at `--threads 4` and schema
> evolution works at any thread count on both.

## Results

| Scenario | Result |
|---|---|
| Run 1 — create (CTAS), `--threads 4` | ✓ 4 models created concurrently |
| Run 2 — incremental **append** (more rows), `--threads 4` | ✓ appends only new ids (`is_incremental()` filter), 4 concurrent, no contention |
| Run 3 — **schema evolution** (model SELECT gains a column → `ALTER ADD COLUMN`), `--threads 1` | ✓ all models gain the column |
| Run 3 — **schema evolution**, `--threads 4` | ✓ all models gain the column (~0.5 s each) — **fixed** (was a ~90 s deadlock; see below) |

## What works

- **Concurrent incremental append** is solid: each model runs in its own DuckDB transaction → its own
  provider connection (per the concurrency fix), the `is_incremental()` `WHERE id > (select max(id) from
  {{ this }})` reads the model on the same connection, and the append `INSERT` streams via the bulk path.
  No cross-model contention at `--threads 4`.
- **Concurrent schema evolution** (`on_schema_change='append_new_columns'` → DuckDB `ALTER TABLE … ADD
  COLUMN` → our catalog `ALTER` path) now works at `--threads 4` (see the fix below).

## Concurrent schema evolution at `--threads > 1` — was a deadlock, now FIXED

Before the fix, a dbt run that introduced a column change to **multiple incremental models concurrently**
failed: each model errored after a ~30 s command timeout (~90 s across 4 models) with a DuckDB `Catalog
Error: Table with name <model> does not exist!`.

### Root cause (captured via `sys.dm_os_waiting_tasks`)

```
session 60  SUSPENDED  wait_type=LCK_M_IS  blocked_by=57   SELECT * FROM [dbo].[inc_a] WHERE 1 = 0
session 57  (holds an incompatible lock on inc_a — the uncommitted ALTER … ADD COLUMN's Sch-M lock)
```

- Our `ALTER … ADD COLUMN` runs on the model's per-transaction connection (session 57) and **evicts the
  table's cached catalog entry**. The Sch-M (schema-modification) lock it takes is held until the model's
  transaction commits, and blocks *all* other access to the table — including the schema-stability lock a
  `SELECT * FROM inc_a WHERE 1=0` needs.
- Because the entry was evicted, the **next bind of the table re-fetches its columns** via our
  `get_metadata(COLUMNS)` query `SELECT * FROM inc_a WHERE 1=0`. Under dbt, that next bind happens in a
  **different (introspection) transaction** with no pinned connection, so the re-fetch runs on a **pooled**
  connection (session 60) → it **blocks** on session 57's uncommitted Sch-M lock → ~30 s `SqlCommand`
  timeout → our self-healing catalog **re-evicts** the entry → the next bind reports "table does not exist".
- It was threads-specific only incidentally: at `--threads 1` the model commits (releasing the lock) before
  another transaction binds the table; the plain incremental **append** is fine at any thread count because
  it takes no Sch-M lock.

### The fix — eager same-connection re-fetch on ALTER

`FabricatorSchemaEntry::Alter` no longer evicts-and-waits-for-a-lazy-refetch. After the `ALTER` it **re-fetches
the columns eagerly, on the model's own connection** (the ambient transaction is set at the top of `Alter`,
so the metadata read routes to session 57 — which *owns* the Sch-M lock, so a read-your-writes probe on the
same session sees the new schema with **no lock wait**) and caches that fresh entry. The later bind (even in
a different transaction) then finds the entry **cached** and never issues the blocking pooled re-fetch.
Result: concurrent schema evolution succeeds (~0.5 s/model).

Because the eager re-fetch reflects the *uncommitted* schema, a transaction that later **rolls back** would
leave a stale entry — so `FabricatorTransactionManager::RollbackTransaction` calls
`FabricatorCatalog::InvalidateAllEntries()` (drops materialized entries, keeps the discovered name lists for a
lazy re-fetch) on rollback. Verified: `ALTER … ADD COLUMN` inside a transaction that `ROLLBACK`s leaves the
table at its original schema (no stale column). The commit path needs nothing — the cached entry already
matches the committed schema.

This is the same family of fix as the dbt **post-hook** join-only routing ([dbt-hooks.md](dbt-hooks.md) §3):
keep the in-transaction work on the transaction's own connection so it never blocks on its own uncommitted
locks. C++-only (no ABI change).
