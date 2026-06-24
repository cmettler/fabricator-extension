# Write concurrency — diagnosis & fix options

> Surfaced by the dbt-duckdb concurrency harness (`dbt_mssql_test/`, gitignored): `dbt run --threads 4`
> building 4 CTAS models into one attached `mssql` catalog fails with `595: Bulk Insert with another
> outstanding result set …` / `Invalid operation. The connection is closed.`. `--threads 1` is clean.

## Diagnosis (confirmed)

dbt-duckdb runs N threads as N DuckDB connections (cursors) → **N concurrent DuckDB transactions against one
attached catalog**. But our model is **single-session per catalog**:

- C++ `ArrowNetTransactionManager::StartTransaction` calls `arrownet::BeginTransaction(handle_)` keyed **only by
  the catalog handle** — not by the transaction.
- C# `SqlServerCatalog` holds a **single shared** `_inTransaction` / `_txnConnection` / `_txn`.

So all concurrent transactions collapse onto **one shared `SqlConnection`**, and concurrent `SqlBulkCopy`
(and pinned reads) on a non-thread-safe connection → error 595 / "connection closed". `transactions.md`
already calls the model "single-session by design"; dbt's concurrency is the first thing to hit it.

Key sub-fact: in **autocommit** (dbt's default — each model is its own statement/transaction), the writes
*don't actually need* a shared connection — they only collapse onto one because the lazy `StartTransaction`
sets the shared `_inTransaction`, so `BeginWrite` pins/reuses the single `_txnConnection`. The shared pinned
connection is only genuinely needed for an **explicit multi-statement `BEGIN`** (read-your-writes), which is a
single user session run **sequentially**.

## Option A (targeted) — autocommit writes get their own connection

Make `BeginWrite` use a **fresh, dedicated connection** when the statement is in **autocommit**, and the
shared pinned connection only inside an **explicit** transaction. The signal is reliable at the *write
operator* (`ClientContext.transaction.IsAutoCommit()` is correct there — the `BEGIN` operator has already
flipped auto-commit by the time a write runs; it is *not* reliable at `StartTransaction` time).

- **Fixes the real case:** concurrent autocommit CTAS/INSERT → each gets its own `SqlConnection` → no
  collision. Reads in autocommit already use pooled connections; with no shared pin, they stay pooled.
- **Preserves** explicit-`BEGIN` read-your-writes (still uses the one pinned connection, single session).
- **ABI:** add a `bool autocommit` to the ~6 write entries (`begin_bulk`, `execute_dml`, `create_table`,
  `delete_rows`, `update_rows`, `insert_returning`); the C++ write operators pass `context.transaction
  .IsAutoCommit()`. C# `BeginWrite(autocommit)`: autocommit ⇒ own connection (owns=true).
- **Leaves uncovered:** *concurrent explicit transactions* on one catalog (two threads each `BEGIN…`). Rare;
  dbt doesn't do it. Would still serialize/collide — document it.
- **Effort:** small-moderate, low regression risk (autocommit path already opens own connections for the
  non-pinned case).

## Option C (full) — per-DuckDB-transaction connections

Key the C# connection/txn state by the **DuckDB transaction id** (`ConcurrentDictionary<long, TxnState>`),
so every concurrent transaction (autocommit *or* explicit) gets its own `SqlConnection`.

- C++ `ArrowNetTransaction` gets a monotonic `id_`; lifecycle calls pass it; **every connection-using
  data-path call** (writes + read-your-writes reads + `get_metadata`) gains a `txn_id`, sourced at the call
  site via `Transaction::Get(context, catalog).Cast<ArrowNetTransaction>().id_`.
- **Covers everything** incl. concurrent explicit transactions; the principled model.
- **Effort:** large + higher risk — ~10 ABI signature changes (lockstep C++/C#), txn-id sourcing threaded
  through the **generic** arrow-scan machinery (a layering touch), C# state rewrite. One big ABI bump.
- Real upside over A is small for this provider: concurrent writes to one SQL Server / Fabric DB contend
  server-side regardless, and explicit concurrent transactions on one attached catalog are uncommon.

## What the native `mssql-extension` does (and why a plain lock isn't enough for us)

The native TDS sibling solves the same problem with a **per-transaction connection**: each `MSSQLTransaction`
pins **its own** connection (`pinned_connection_`), plus a `connection_mutex_` (`GetConnectionMutex()`) that
serializes parallel *operators within one transaction*. So its robustness is **per-transaction isolation**, not
a catalog-wide lock. Its connection lives C++-side in the transaction object, so it has no ABI boundary to
cross — which is exactly the part that makes the equivalent expensive for us (our connection is in C#).

A **catalog-wide lock on our single shared connection** (the naive "just lock it") would prevent the 595 error
but is **incorrect**: concurrent DuckDB transactions would share one `SqlConnection` **and one `SqlTransaction`**,
so their commits/rollbacks would mix. Locking only makes a *per-transaction-connection* model safe (native's
intra-transaction mutex), it does not substitute for it.

## Decision — Option A (autocommit own-connection), with C documented for later

Chosen because it is **correct** (not just non-erroring) and robust to dbt's actual behavior, at modest cost:

- dbt models run in **autocommit** (each model = its own statement/transaction). Autocommit writes don't need a
  shared pinned connection; giving each its **own** connection makes concurrent model builds correct + parallel.
- It is the **autocommit slice of the per-transaction model** (Option C) — same principle, scoped to the case
  that actually occurs. The shared pinned connection remains **only** for an explicit multi-statement `BEGIN`
  (read-your-writes), which is one user session run sequentially.
- It composes with the Fabric **metadata read-your-writes** fix: in autocommit the `CREATE` **commits** before
  the catalog re-fetch (a fresh connection then sees it — verified Fabric shows committed objects); the pinned
  read-your-writes path still covers the explicit-`BEGIN` case.
- **Not covered:** two *concurrent explicit* transactions on one catalog (rare; dbt doesn't do it — hooks run
  sequentially on the model's connection). If that ever becomes real, do Option C (per-transaction connections,
  matching native). A catalog-wide lock is explicitly **not** the fallback (incorrect, see above).

## Validation (either option)

Rebuild the loadable extension (`OVERRIDE_GIT_DESCRIBE=v1.5.4`) + republish the bridge; then:
`dbt run --threads 4` against box (must go green) and Fabric; full `verify_*` suite stays 30/30 (esp.
`verify_proc_inout` — explicit-`BEGIN` read-your-writes — must still pass).
