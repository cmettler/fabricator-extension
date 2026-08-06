# dbt × MERGE INTO × Delta on S3 — measured behaviour

A dbt-duckdb harness that answers one question: **does dbt's `incremental_strategy='merge'` work against
fabricator's Delta provider, and in which transaction mode does dbt run it?**

Rig: dbt-core 1.11.11 / dbt-duckdb 1.10.1 / duckdb 1.5.5, Delta catalog on the docker MinIO rig
(`../docker/docker-compose.yml`). No live credentials here — the MinIO keys are the committed test-only ones.

```bash
export FABRICATOR_MANAGED_DIR="$(cygpath -w ../build/release/extension/fabricator/fabricator)"
P=../dbt_mssql_test/.venv/Scripts/python.exe          # same package set; no separate venv needed
$P -m dbt.cli.main run --profiles-dir . --target s3 --vars 'batch: 1'   # creates
$P -m dbt.cli.main run --profiles-dir . --target s3 --vars 'batch: 2'   # merges
```

## 1. Does dbt open an explicit transaction? YES.

`DuckDBConnectionManager` inherits `begin()`/`commit()` from `SQLConnectionManager` **unoverridden**, and
`execute()` defaults `auto_begin=True`, so the first statement of a model opens a real transaction. Measured
in `logs/dbt.log`:

```
On model.dbt_merge_s3_test.merge_default: BEGIN
    MERGE INTO "lake"."main"."merge_default" AS DBT_INTERNAL_DEST
        USING "merge_default__dbt_tmp..." AS DBT_INTERNAL_SOURCE
        ON (DBT_INTERNAL_SOURCE.id = DBT_INTERNAL_DEST.id)
    WHEN MATCHED THEN UPDATE BY NAME
    WHEN NOT MATCHED THEN INSERT BY NAME
SQL status: OK in 2.240 seconds
On model.dbt_merge_s3_test.merge_default: COMMIT
```

## 2. Does that make it fail? NO — and the explicit transaction is the BETTER mode.

The expectation behind this experiment was "fabricator supports merge in autocommit only, so dbt will fail".
That is not the rule. What the buffered (explicit-transaction) path requires is **deletion vectors on the
table**, and DVs are the Delta default — so the default dbt incremental merge works. It is also *more*
correct there: the whole merge fuses into **one** atomic Delta commit (`operation = TRANSACTION`), whereas
autocommit splits a 1-action merge across two versions.

## 3. The measured matrix

| table | merge actions | dbt default (`BEGIN`…`COMMIT`) | autocommit |
|---|---|---|---|
| DV **on** (default) | `UPDATE` + `INSERT` | ✅ **1 commit** `TRANSACTION` | ✅ 2 commits (`UPDATE`,`WRITE`) — atomicity lost |
| DV **on** (default) | `DELETE`+`UPDATE`+`INSERT` | ✅ **1 commit** `TRANSACTION` | ✅ same (forced to buffer anyway) |
| DV **off** | `UPDATE` + `INSERT` | ❌ refused, table untouched | ✅ 2 commits (`UPDATE`,`WRITE`) |
| DV **off** | `DELETE`+`UPDATE`+`INSERT` | ❌ refused | ❌ **refused in every mode** |

Verified results — `merge_default` (1..3 kept, 4..5 updated, 6..8 inserted) and `merge_delete_update`
(3,4 deleted, 5 updated, 6,7 inserted, 1,2 untouched). The second is the shape that silently destroyed a row
before the forcing rule existed, so its correctness here is the dbt-level regression check for it.

**The last row is the important one.** Two row-addressing actions on a positional (Delta virtual) rowid are
forced to buffer *regardless of ambient mode* — that forcing is what stops a copy-on-write DELETE from
renumbering the rows the UPDATE already addressed. So `deletion_vectors=false` and a multi-action merge are
mutually exclusive everywhere: **a non-DV table serves at most one UPDATE/DELETE action per merge.**

`deletion_vectors=false` is not exotic — SQL Server's and Fabric's Delta readers are protocol 1.0 only, so a
table published for T-SQL/OPENROWSET *must* be written that way. That is the real reachable failure.

## 4. Can a SINGLE model use autocommit? YES, with no code — but read the cost.

dbt has **no model-level transaction switch**: `disable_transactions` is a profile/target field
(`dbt/adapters/duckdb/credentials.py:214`). But the materialization's hook ordering can hand the transaction
back before the model statement, and that IS per-model:

```jinja
{{ config(pre_hook="COMMIT", post_hook="BEGIN") }}
```

```
run_hooks(pre_hooks,  inside_transaction=True)   -> statement(auto_begin=True) => BEGIN, then 'COMMIT'
... the model's MERGE ...                        -> now runs in AUTOCOMMIT
run_hooks(post_hooks, inside_transaction=True)   -> 'BEGIN' re-opens one
adapter.commit()                                 -> ...so dbt's own COMMIT still has a transaction
```

The post-hook `BEGIN` is **not cosmetic**: dbt tracks `transaction_open` itself, so without it dbt's final
`COMMIT` is issued with no active transaction and the model fails *after* its data landed.

Verified (`merge_nodv_hooks`): correct upsert, and the Delta history shows `UPDATE` then `WRITE` — two
commits, so the mode provably changed rather than the error merely being dodged. Failure path is clean too
(`merge_nodv_2act_hooks`): the real fabricator error survives; no rollback-against-closed-transaction masking.

Applies declaratively to a folder, which is effectively "transaction mode per model group":

```yaml
models:
  my_project:
    interop:                 # models publishing protocol-1.0 tables for T-SQL
      +pre_hook: "COMMIT"
      +post_hook: "BEGIN"
```

**Cost:** the merge is no longer atomic, and on a DV table this is strictly worse than leaving dbt alone.
Use it only where the buffered path is refused.

### The target-level route also works, but is noisy

`disable_transactions: true` (target `s3_autocommit`) succeeds and produces the same correct result, but the
incremental materialization calls `adapter.commit()` unconditionally, so every model logs

```
[warn ] DuckDB adapter: Commit failed with DbtInternalError: Internal Error
  Tried to commit transaction on connection "model...", but it does not have one open!
```

Caught and warn-level — the run passes — but it looks like a defect to anyone reading the log.

## 5. A custom materialization is NOT needed

Step 4 gives per-model transaction control with zero custom code. A custom materialization would only be
worth it to make the intent declarative (`+transaction_mode: autocommit`) and to drop the post-hook `BEGIN`
bookkeeping trick; it would have to duplicate dbt-duckdb's `incremental` materialization to do so.

## 6. ⚠ Separate defect found here: a flat Delta root does not round-trip a schema qualifier

Found while building this harness, and it silently breaks *every* dbt incremental on a flat Delta root.

A flat (non-`schemas true`) Delta catalog exposes exactly one schema, `main`. It nevertheless **accepts a
write under any schema name, dropping the qualifier** — `CREATE TABLE lake.zzz.t` lands at `<root>/t/` — and
then **cannot resolve that name on a later attach** (`schema "zzz" does not exist`), while discovery reports
the table under `main`. Same session it resolves, because the in-memory entry is still there.

Minimal repro and the consequence:

```sql
ATTACH 's3://bucket/root' AS lake (TYPE fabricator, PROVIDER 'delta', SECRET s, READ_ONLY false);
CREATE SCHEMA IF NOT EXISTS lake.zzz;          -- accepted, creates nothing
CREATE TABLE lake.zzz.t AS SELECT 1 AS a;      -- accepted, writes to <root>/t/
-- fresh process, re-attach:
SELECT * FROM lake.zzz.t;                      -- Catalog Error: schema "zzz" does not exist
```

Because dbt's default `generate_schema_name` appends the custom schema to the target one, `+schema: main`
yields `main_main` — which never round-trips. So dbt finds no existing relation on **every** run, takes the
`CREATE TABLE AS` branch instead of the merge branch, and **overwrites**. Measured: batch-1 rows 1–3 gone,
exit 0, `OK created sql incremental model`. It also bypasses the `ERROR_ON_CONFLICT` guard, because the entry
cannot be found under the requested schema. Two different schema names also silently alias to one table.

Hence `dbt_project.yml` here deliberately sets **no** `+schema`. That is a workaround, not a fix — writes are
being accepted under names that provably cannot be read back.
