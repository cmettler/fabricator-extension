# dbt incremental models + schema evolution with `mssql_net`

> Findings from the `dbt_mssql_test/` harness (gitignored), validated against **box SQL Server 2025** with
> the per-transaction connection model ([transaction-concurrency.md](transaction-concurrency.md)). Models:
> `materialized='incremental'`, `incremental_strategy='append'`, `on_schema_change='append_new_columns'`,
> run at `--threads 4`.

## Results

| Scenario | Result |
|---|---|
| Run 1 — create (CTAS), `--threads 4` | ✓ 4 models created concurrently |
| Run 2 — incremental **append** (more rows), `--threads 4` | ✓ appends only new ids (`is_incremental()` filter), 4 concurrent, no contention |
| Run 3 — **schema evolution** (model SELECT gains a column → `ALTER ADD COLUMN`), `--threads 1` | ✓ all models gain the column |
| Run 3 — **schema evolution**, `--threads 4` | ✗ **deadlock → 30s timeout per model → "Table does not exist"** |

## What works

- **Concurrent incremental append** is solid: each model runs in its own DuckDB transaction → its own
  provider connection (per the concurrency fix), the `is_incremental()` `WHERE id > (select max(id) from
  {{ this }})` reads the model on the same connection, and the append `INSERT` streams via the bulk path.
  No cross-model contention at `--threads 4`.
- **Schema evolution itself works** (`on_schema_change='append_new_columns'` → DuckDB `ALTER TABLE … ADD
  COLUMN` → our catalog `ALTER` path) — at `--threads 1`, all models gain the new column.

## LIMITATION — concurrent schema evolution (`on_schema_change` ALTER) at `--threads > 1` deadlocks

Running a dbt run that **introduces a column change** to **multiple incremental models concurrently**
fails: each model errors after a ~30 s command timeout with a DuckDB `Catalog Error: Table with name
<model> does not exist!` (with 4 models the failures serialize to ~90 s — see below).

### Root cause (captured via `sys.dm_os_waiting_tasks`)

```
session 60  SUSPENDED  wait_type=LCK_M_IS  blocked_by=57   SELECT * FROM [dbo].[inc_a] WHERE 1 = 0
session 57  (holds an incompatible lock on inc_a — the uncommitted ALTER … ADD COLUMN's Sch-M lock)
```

- dbt's incremental + `on_schema_change` path **re-introspects the table's schema** (`SELECT * FROM <model>
  WHERE 1=0`) as part of materializing it. That introspection runs on a **different connection** (a separate,
  autocommit query) than the model's transaction.
- The model's transaction is mid-materialization and holds the **uncommitted `ALTER … ADD COLUMN`'s Sch-M
  (schema-modification) lock** on the table — which blocks *all* other access, including the `IS` lock the
  introspection needs.
- dbt won't `COMMIT` the model (release the Sch-M lock) until the materialization — including the blocked
  introspection — finishes → a **client-mediated distributed deadlock** invisible to SQL Server's deadlock
  monitor, resolved only by the `SqlCommand` 30 s timeout. The timed-out metadata read then makes our
  self-healing catalog **evict** the entry → the next bind reports "table does not exist".
- With 4 models the catalog's entry-lock serializes the timeouts (~90 s total).

This is **threads-specific**: at `--threads 1` the same schema change succeeds (the model commits before the
next introspection needs the lock). The plain incremental **append** (no `ALTER`) is fine at `--threads 4`
because it takes no Sch-M lock.

### Solvable?

**Not in the extension** for the concurrent case. The conflict is between dbt holding a model's transaction
open across a DDL `ALTER` (Sch-M lock) and dbt re-introspecting that table's schema on a separate
autocommit connection. The two are different DuckDB transactions, so the per-transaction-connection routing
can't unify them onto one connection; and a Sch-M lock blocks even snapshot / `READ UNCOMMITTED` reads, so
the introspection can't be made non-blocking. This is the same family as the dbt **post-hook** self-block
([dbt-hooks.md](dbt-hooks.md) §3) — there it was solvable because the hook's exec **is** in the model's
transaction (join-only routing); here the introspection is a separate autocommit query, so there is nothing
to join.

### Workaround (validated)

Run **schema-evolution migrations at `--threads 1`**, then steady-state incremental loads at `--threads N`:

```bash
dbt run --threads 1 --select state:modified   # the run that changes columns
dbt run --threads 4                            # normal concurrent incremental loads
```

This matches the common operational pattern of applying schema migrations as a separate, serialized step.
Alternatives: `on_schema_change='ignore'` (or `'fail'`) + manage the `ALTER` out-of-band (e.g. a
`--threads 1` pre-hook / a separate migration), or split schema-changing models into their own selector.

### Possible extension-side mitigation (not implemented)

Lower blast radius without fixing the deadlock: don't hold the catalog entry-lock across the metadata SQL
(so one blocked introspection doesn't serialize other models' catalog operations — turns the ~90 s
cascade into independent ~30 s failures), and/or set a short `lock_timeout` on metadata reads so they fail
fast instead of waiting the full command timeout. Neither makes concurrent schema evolution *work* — the
`--threads 1` migration step remains the supported path.
