# dbt pre/post hooks with `mssql_net` — behavior & limitations

> How dbt-duckdb pre/post hooks interact with the `mssql_net` provider's per-transaction connections
> (see [transaction-concurrency.md](transaction-concurrency.md)). Findings from the `dbt_mssql_test/`
> harness (gitignored), validated against **box SQL Server 2025** and a **Fabric Warehouse** (no MARS,
> SNAPSHOT). Hook semantics reference: dbt's
> [pre-hook/post-hook docs](https://docs.getdbt.com/reference/resource-configs/pre-hook-post-hook).

## How dbt frames a model + hooks

dbt-duckdb **does** use transactions (its base `SQLConnectionManager` issues `BEGIN`/`COMMIT` around each
model; it does not override them). So a `materialized='table'` model runs as:

```
BEGIN                               -- one DuckDB transaction T
  <pre-hooks, transaction:true>
  CREATE TABLE mssql.dbo.model AS … -- our CTAS, on T's pinned provider connection (uncommitted)
  <post-hooks, transaction:true>
COMMIT                              -- our CommitTransaction(T) commits the provider SqlTransaction
```

- A hook is **inside T by default** (`transaction: true`). `transaction: false` (or the `after_commit()` /
  `before_begin()` macros) runs it **outside** T.
- A hook that needs **SQL-Server-specific DDL** (index, `PRIMARY KEY`/`UNIQUE` constraint, etc.) cannot be a
  DuckDB statement — DuckDB's `ALTER` on a foreign catalog can't express it, and our `ALTER` path supports
  only rename / add-drop-column / type / null / default. So it must call **`mssql_net_exec('mssql', '<T-SQL>')`**.

## Results matrix (validated)

| Scenario | Box (SQL Server 2025) | Fabric Warehouse |
|---|---|---|
| In-txn post-hook **errors** (bad T-SQL) | model **rolled back** — table absent ✓ | model **rolled back** — table absent ✓ |
| `transaction:false` post-hook adds an index via `mssql_net_exec` | model committed **+ index created** ✓ | model committed; index **fails** (`22424 CREATE INDEX is not supported`) → table persists **without** index |
| In-txn post-hook modifies the model via `mssql_net_exec` | **30s Execution Timeout** (self-block) ✗ | same (provider-agnostic) ✗ |

## Findings

### 1. Rollback-of-resource works — on box AND Fabric (a strength)

An **in-transaction** (default) post-hook that errors propagates the error to dbt, which rolls back
transaction T → our `RollbackTransaction(T)` rolls back the provider `SqlTransaction` → **the model's
`CREATE` is undone**. Confirmed on box and Fabric (the model table is absent afterward).

This means **Fabric Warehouse supports transactional DDL rollback** — notably *unlike* Snowflake/BigQuery,
which dbt explicitly warns have limited/no transaction support. Our per-transaction-connection model
([transaction-concurrency.md](transaction-concurrency.md)) carries this through correctly.

### 2. SQL-Server-specific DDL in a hook: use `transaction: false`

```sql
{{ config(
    materialized='table',
    post_hook={
      "sql": "select mssql_net_exec('{{ this.database }}', 'CREATE INDEX [ix_{{ this.identifier }}] ON [{{ this.schema }}].[{{ this.identifier }}] ([id])')",
      "transaction": false
    }
) }}
```

The model **commits first**, so `mssql_net_exec` (which runs on its own autocommit connection) can see the
table and add the index. **Trade-off (inherent, not fixable): the hook is NOT atomic with the model** — if
the hook fails (e.g. Fabric's `CREATE INDEX`), the model is already committed and stays in place without the
index. This is exactly dbt's own caveat about out-of-transaction hooks.

> `PRIMARY KEY` adds an extra wrinkle: CTAS-created columns are **nullable**, and a PK needs `NOT NULL`. So a
> PK hook must first `ALTER TABLE … ALTER COLUMN [id] <type> NOT NULL` then `ADD CONSTRAINT … PRIMARY KEY`.
> `UNIQUE` has no such requirement. On Fabric, both must be `NONCLUSTERED … NOT ENFORCED` (the model's own
> PK/UNIQUE-on-CREATE path already emits that form — see warehouse-support.md §3.5).

### 3. LIMITATION — an in-transaction hook must NOT modify the just-created model via `mssql_net_exec`

A **default** (`transaction:true`) post-hook calling `mssql_net_exec` to touch the model **self-blocks to a
30-second command timeout** (`-2: Execution Timeout Expired`):

- The model's `CREATE` is **uncommitted**, holding schema locks on **dbt's transaction connection** `C_T`.
- `mssql_net_exec` **autocommits on a separate connection** `C_X` (by design — it's a raw passthrough; see
  transaction-concurrency.md), which **blocks** on `C_T`'s locks.
- dbt won't `COMMIT` (release `C_T`) until the hook returns, and the hook (`C_X`) can't return until `C_T`
  commits → a **client-mediated distributed deadlock** that SQL Server's deadlock monitor can't see (the two
  sessions wait through the dbt client), so it resolves only by the `SqlCommand` timeout.

**Workaround (idiomatic):** use `transaction: false` / `after_commit()` (finding #2). This is the standard
dbt pattern for DDL-ish hooks anyway.

**Possible future fix (not implemented):** route `mssql_net_exec` onto the **active transaction's** connection
when the catalog already has one for this DuckDB transaction (i.e. a model write is in progress) — it would
then run the DDL on `C_T` itself, see the uncommitted model, and commit/roll back **atomically** with the
model. Today `mssql_net_exec` always autocommits because, standalone, its string-arg target doesn't trigger
the catalog's transaction lifecycle (nothing would ever commit a pinned connection). A "join the active
transaction connection iff one already exists, else autocommit" rule would make the in-txn hook atomic
without regressing the standalone case. Tracked as an enhancement.

### 4. LIMITATION (provider, unsolvable in the extension) — Fabric DDL gaps

- **`CREATE INDEX` is unsupported on Fabric** (`22424`). Fabric Warehouse has no user-defined nonclustered
  indexes (only the implicit clustered columnstore). A hook that creates an index can't be made to work on
  Fabric — author Fabric-aware hooks, or rely on the model's `mssql_default_table_type='cci'` /
  PK-as-NONCLUSTERED-NOT-ENFORCED paths instead.
- PK/UNIQUE on Fabric must be `NONCLUSTERED … NOT ENFORCED` via a separate `ALTER` (inline-in-CREATE is
  rejected, error 24584) — the model's CREATE path already handles this; an ad-hoc hook must match it.

## Summary

| Limitation | Solvable? | How |
|---|---|---|
| In-txn hook modifying the model via `mssql_net_exec` self-blocks | Yes (workaround) | use `transaction: false` / `after_commit()` |
| …same, made atomic | Yes (code, future) | route `mssql_net_exec` onto the active txn connection if present |
| `transaction:false`/`after_commit` hooks aren't atomic with the model | No (inherent) | dbt design + provider; accept or move logic into the model |
| Fabric `CREATE INDEX` in a hook | No (provider) | Fabric has no nonclustered indexes |
| Rollback-of-resource on hook error | Works ✓ | in-transaction (default) hooks; box AND Fabric |
