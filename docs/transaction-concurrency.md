# Write concurrency — diagnosis & fix options

> Surfaced by the dbt-duckdb concurrency harness (`dbt_mssql_test/`, gitignored): `dbt run --threads 4`
> building 4 CTAS models into one attached `mssql` catalog fails with `595: Bulk Insert with another
> outstanding result set …` / `Invalid operation. The connection is closed.`. `--threads 1` is clean.

## Status — FIXED via Option C (per-DuckDB-transaction connections)

**Implemented + validated** (ABI v35): `dbt run --threads 4` builds 4 × 200k-row CTAS models concurrently
into one attached `mssql` catalog with **0 errors** (was: all 4 fail with 595), full `verify_*` suite stays
**30/30** (incl. `verify_proc_inout` explicit-`BEGIN` read-your-writes + rollback). See "Implementation" below.

## Diagnosis (confirmed)

dbt-duckdb runs N threads as N DuckDB connections (cursors) → **N concurrent DuckDB transactions against one
attached catalog**. But the original model was **single-session per catalog**:

- C++ `ArrowNetTransactionManager::StartTransaction` called `arrownet::BeginTransaction(handle_)` keyed **only by
  the catalog handle** — not by the transaction.
- C# `SqlServerCatalog` held a **single shared** `_inTransaction` / `_txnConnection` / `_txn`.

So all concurrent transactions collapsed onto **one shared `SqlConnection`**, and concurrent `SqlBulkCopy`
(and pinned reads) on a non-thread-safe connection → error 595 / "connection closed". `transactions.md`
already calls the model "single-session by design"; dbt's concurrency is the first thing to hit it.

**Correction (the original Option A premise was wrong):** dbt-duckdb does **NOT** run models in autocommit —
the base `dbt.adapters.sql.SQLConnectionManager` issues a real `BEGIN`/`COMMIT` around each model and
dbt-duckdb does not override it, so each model runs in an **explicit transaction**. `IsAutoCommit()` is
therefore **false** during a model's CTAS, so an "autocommit gets its own connection" fix (Option A) never
fires for dbt — empirically the 595 persisted with A in place. The concurrent transactions are explicit, so
only a true per-transaction-connection model (Option C) fixes it. A was abandoned for C.

## Option A (rejected) — autocommit writes get their own connection

Make `BeginWrite` use a fresh connection when `ClientContext.transaction.IsAutoCommit()`. **Rejected after
implementation**: dbt runs models in explicit transactions, so `IsAutoCommit()` is false and A never fires
(595 persisted). A only helps genuinely-autocommit concurrent writes — which dbt is not. Superseded by C
(which subsumes it: in C an autocommit statement is its own transaction id ⇒ its own connection anyway).

## Option C (full, IMPLEMENTED) — per-DuckDB-transaction connections

Key the C# connection/txn state by the **DuckDB transaction id**, so every concurrent transaction (autocommit
*or* explicit) gets its own `SqlConnection`. This is what was built. See "Implementation".

- **Covers everything** incl. concurrent explicit transactions (dbt); the principled model, matching the
  native `mssql-extension`'s per-`MSSQLTransaction` connection.

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

## Implementation (Option C, ABI v35; join-only refinement at v36)

Per-DuckDB-transaction provider connections, keyed by `global_transaction_id`:

- **C# (`SqlServerBackend`)**: replaced the single `_inTransaction`/`_txnConnection`/`_txn` with a
  `ConcurrentDictionary<long, TxnState>` (one connection + `SqlTransaction` per transaction id, opened lazily
  on the first write). `BeginWrite()` / `ExecuteQuery(... readYourWrites)` / `EndTransaction(id, commit)` /
  `Dispose` operate on the state for the **active** transaction id. The id is carried per-thread in
  `AmbientTransaction.Current` (Bridge), read by these methods — so it need not thread through every method
  signature or the internal SQL helpers. With no active transaction (id 0) a write takes a fresh
  autocommit connection (`owns=true`), a read a pooled connection (unchanged). The `BulkInsert` path runs on
  a background task, so the id is captured at `begin_bulk` and the consumer re-establishes the ambient on its
  own thread.
- **ABI v35**: `begin_bulk`'s `autocommit` arg became `int64 txn_id`; one new entry `set_active_txn(handle,
  txn_id)` sets the managed per-thread ambient. The host calls `set_active_txn` IMMEDIATELY before each
  connection-using call, on the same thread (the calls are synchronous).
- **C++ sourcing**: `ArrowNetTransaction` stores `txn_id_` (= `MetaTransaction::Get(context)
  .global_transaction_id`), captured at `StartTransaction`; lifecycle (`StartTransaction`/`CommitTransaction`/
  `RollbackTransaction`) sets it before begin/commit/rollback. Every connection-using operator sets it before
  its call: scans centrally in `arrow_ingest`'s `ArrowStreamInitGlobal` (covers all reads incl. TVF /
  `mssql_net_query` — read-your-writes), DDL in `ArrowNetSchemaEntry::CreateTable/DropEntry/Alter` +
  `ArrowNetCatalog::CreateSchema/DropSchema`, DML in `arrownet_modify`/`arrownet_insert`, the proc `_each`
  exchange in `ArrowNetExchange{InitGlobal,Function}` (so its `BeginWrite` joins DuckDB's pinned write txn),
  the catalog-visibility re-fetch in `FetchTableColumns`, and `mssql_net_exec`. `begin_bulk` carries the id
  explicitly (background thread). Helper: `ArrowNetSetActiveTxn(handle, context)` in
  `catalog/arrownet_txn_util.hpp`.
- **`mssql_net_exec` join-only mode (ABI v36).** A raw exec can't blindly key to the active transaction: its
  string-arg target never triggers DuckDB's transaction lifecycle, so a pinned connection it created would
  never be committed (rolled back at teardown). `set_active_txn` gained a `join_only` flag the exec sets:
  `BeginWrite` then runs on the active transaction's pinned connection **iff one already exists** (a
  DuckDB-managed write — e.g. a dbt model's CTAS — is in flight), making the exec **atomic** with that
  transaction and able to see its uncommitted writes (the fix for the in-transaction-hook self-block — see
  [dbt-hooks.md](dbt-hooks.md) §3); otherwise it autocommits on its own connection without pinning. Normal
  DuckDB-managed writes pass `join_only=0` (create + own the per-transaction connection).

## Validation

Rebuild the loadable extension (`OVERRIDE_GIT_DESCRIBE=v1.5.4`) + republish the bridge; then `dbt run
--threads 4`. **Box: PASS=4/4** (4 × 200k CTAS concurrent, 0 errors — was all-595). **Fabric (no MARS):
PASS=4/4** (genuinely concurrent — ~22s each, ~29s wall; catalog-visibility re-fetch joins the txn). Full
`verify_*` suite **30/30** (incl. `verify_proc_inout` explicit-`BEGIN` read-your-writes + rollback).
