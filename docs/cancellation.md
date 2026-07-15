# Cancellation — hooking DuckDB interrupt to cancel long-running C# I/O

## Problem

A query that is parked inside a single long-blocking C# I/O call cannot be cancelled by the user
(Ctrl+C in the shell) or by a timeout — the shell hangs until the I/O returns on its own (or forever,
on a hung socket). Concrete windows:

- **SQL Server scan** — the eager `ExecuteReader()` on a slow query (a big aggregation with no rows
  yet), and each `reader.Read()` network fetch.
- **Delta / engineered-wood read** — one `ReadAllAsync` batch = a large parquet row-group over
  OneLake / S3 (seconds).
- **`SqlBulkCopy.WriteToServer`** — a batch send to a slow server.
- **A hung connection** — no timeout exists anywhere.

### Why it happens (state before this work)

1. **C# CancellationTokens are dead-wired.** They thread through every async signature (engineered-wood
   takes them properly) but **~124 call sites pass `default`/`CancellationToken.None`**. The only real
   `CancellationTokenSource` in the bridge is `BulkSession._consumerExited` — an internal producer/consumer
   teardown signal, not query cancellation.
2. **The SQL Server / DAX backends use SYNC ADO.NET** (`SqlServerBackend`: 38 sync `Execute*`/`WriteToServer`,
   0 async; DAX: sync ADOMD). Sync ADO.NET ignores tokens by construction; there is no `SqlCommand.Cancel()`
   and no `CommandTimeout`.
3. **The C++ extension has zero interrupt awareness.** `arrow_ingest.cpp` never checks
   `context.interrupted`; the scan just calls `stream.get_next(...)`, which blocks the DuckDB task thread on
   the C# `ReadNextRecordBatchAsync().GetResult()`. There is **no ABI cancel/interrupt hook**.

### How DuckDB cancellation works, and where the gap bites

Ctrl+C → `Connection::Interrupt()` → `ClientContext::Interrupt()` sets the public `atomic<bool> interrupted`.
The `PipelineExecutor` checks `context.client.interrupted` and throws `InterruptException` **between operator
calls** (`pipeline_executor.cpp:193/427/550`). The table-function callback even receives `ClientContext &`.

⇒ **DuckDB already cancels our scans *between* `get_next` calls.** The ONLY uninterruptible window is a
single long-blocking call, during which the DuckDB task thread is stuck inside our C# I/O and the interrupt
is never observed. So the scope of this work is bounded to that single-long-call window (plus adding a
timeout backstop, which no interrupt covers).

## Design

The opener handle threaded via `set_active_opener` **is literally a `ClientContext*`**
(`reinterpret_cast<ClientContext *>(opener)` in `fabricator_fs_spike.cpp`) — the same handle the `fs_*`
callbacks use for secret resolution. So the interrupt flag is one dereference away.

### Token source (provider-agnostic) — the crux

- **New host callback `int32_t is_interrupted(FabricatorHandle opener)`** on `FabricatorHostServices`
  (the reverse host→managed struct the C++ side fills), returning `context.interrupted`. (ABI v65 — a host
  struct addition, like `host_log` at v58.)
- **`InterruptScope`** (C#): a per-operation `CancellationTokenSource` + a lightweight poller (one pool-thread
  task) that calls `is_interrupted(opener)` every ~50 ms while a blocking op is in flight and cancels the CTS
  on interrupt. The DuckDB task thread stays blocked in `get_next`, but the poller (a *different*, pool thread)
  trips the token → the I/O throws `OperationCanceledException` → `get_next` returns an error → the task
  thread unblocks into DuckDB's normal error path. The scope wraps the operation's lifetime (the scan stream,
  a DML/bulk op); it is disposed at operation end, before the `ClientContext` is freed, and the poller only
  runs while the opener (`ClientContext*`) is valid — which is the whole table-function execution.
- **Bonus: timeouts for free** — `CancelAfter(...)` from a future `command_timeout`/`io_timeout` setting closes
  the "hung connection" hole that no interrupt covers.

Polling is required because the interrupt is a poll-only atomic flag (no push signal to link a CTS to). A
50 ms poll is imperceptible to a human Ctrl+C and negligible overhead (an atomic read via one P/Invoke).

### Token consumption — splits by backend

| Backend | Path | How the token cancels I/O |
|---|---|---|
| **engineered-wood / Delta** | already async, already takes the token | replace `default` with the scope token → OneLake/S3 reads/writes cancel |
| **SQL Server** | **switch the hot calls to async SqlClient** (`OpenAsync`/`ExecuteReaderAsync`/`ReadAsync`/`ExecuteNonQueryAsync`/`WriteToServerAsync`), block once at the ABI boundary, pass the token | native — the token aborts the command; a cancelled command's connection is dropped by the pool (do not reuse it) |
| **`SqlBulkCopy`** | the reader-throws-on-pull mechanism (no async conversion required) | the consumer pulls via `ArrowDataReader.Read()` → `ChannelArrowStream.ReadNextRecordBatchAsync(ct)` → `WaitToReadAsync(ct)`; a cancelled token throws `OperationCanceledException` up out of `WriteToServer` → aborts the load. **Already proven**: `BulkSession.Abort()` faults the channel for exactly this reason. Wire the token into `ArrowDataReader` (feed it a real token instead of `default`) **or** have the watcher call `BulkSession.Abort()`. Caveat: aborts promptly when *pulling* (backpressure — the common park); mid *network-send* it lands after the current batch send (WriteToServer calls `Read()` again). `WriteToServerAsync(ct)` is only needed if the mid-send window ever matters. |
| **DAX / ADOMD** | ADOMD has **no** usable async surface | the watcher calls `AdomdCommand.Cancel()` (or connection close) — the one place the manual cancel-from-another-thread is still required |

### Note on `SqlCommand.Cancel()` vs async SqlClient

Async `Microsoft.Data.SqlClient` (`ExecuteReaderAsync(ct)`, `ReadAsync(ct)`, `WriteToServerAsync(ct)`, …)
honors the token natively and is Microsoft's recommended cancellation model, so it replaces the
`SqlCommand.Cancel()`-from-another-thread trick for SQL Server. `Cancel()` stays only for DAX/ADOMD (no
async). This revises the earlier "leave `SqlServerBackend` sync — it's not the sync-over-async anti-pattern"
stance: **converting the SQL scan/DML sites to async is justified once token-driven cancellation is the goal**
(the backend becomes genuinely async, in the same block-once sync-over-async shape as the Delta bridge). A
cancelled SqlClient command can leave its connection unusable — fine for pooled scan connections (the pool
drops it) and correct for the pinned-transaction connection (a cancelled query is aborting the txn anyway),
but such a connection must be disposed, not reused.

## As built

- **Tier 1 (commit `8daac29`, ABI v65):** `is_interrupted(opener)` host callback + `InterruptScope` (CTS +
  50 ms `Timer` poller; `Dispose` waits out the in-flight callback). Wired into the engineered-wood streaming
  reads (`DeltaReader.Stream`/`StreamWithRowIds`/`StreamAt`/`StreamWithRowIdsAt`).
- **Tier 2a (commit `4d33f68`) — SQL data scan:** `ExecuteQuery` uses `OpenAsync`/`ExecuteReaderAsync(token)`
  and `DbDataReaderArrowStream` fetches with `ReadAsync(token)`; the stream owns the `InterruptScope`. Gated to
  data scans (`!readYourWrites`) — short metadata reads stay uncancelled.
- **Tier 2b (commit `6e952f6`) — bulk write (INSERT/CTAS/COPY):** `BulkSession` builds an `InterruptScope(opener)`
  and `token.Register`s its existing `Complete(abort)` teardown — faulting the channel stops `WriteToServer` +
  unblocks a backpressure-parked `push_batch`. Works for SQL bulk *and* Delta streaming writes.
- **Tier 2c (commit `<this>`) — SQL DML/exec:** `ExecuteNonQuery` (raw `fabricator_exec`), `ExecuteDelete`, and
  `ExecuteUpdate` build an `InterruptScope(AmbientOpener.Current)` and run their DB writes with
  `ExecuteNonQueryAsync(token)` (chunked loops share one scope), so a long rowid DELETE/UPDATE or a slow
  `fabricator_exec` cancels.

### The opener-freshness constraint (load-bearing)

`is_interrupted` dereferences the opener as a `ClientContext*`, and **`AmbientOpener` is never cleared** (no
`SetActiveOpener(0)`), so `AmbientOpener.Current` retains the last value set on the thread. Interrupt polling is
therefore only safe where the opener was **freshly set right before** the operation. Every write path DOES set it
fresh: the scan (`arrow_ingest` `SetActiveOpener(&context)`), the bulk (`fabricator_insert.cpp` before
`begin_bulk`), the **DELETE/UPDATE modify operator** (`Finalize` → `FabricatorSetActiveTxn` → `SetActiveOpener`,
`catalog/fabricator_txn_util.hpp`), and `fabricator_exec` (`fabricator_extension.cpp:501`). So 2a/2b/2c all capture
the current statement's live `&context` — never a stale pointer. **What is NOT safe** is capturing
`AmbientOpener.Current` in a path with no preceding `SetActiveOpener` (short metadata reads, DDL via
`CreateTable`/`AlterTable`) — those are left uncancelled (they're short anyway).

## Tiers

- **Tier 1 (this effort) — token source + wire the token-native paths.** `is_interrupted` host callback +
  `InterruptScope` (CTS + poller); thread the token through the engineered-wood read/write call sites (they
  already honor it). This closes the biggest window (a long OneLake/S3 batch read) with the least churn.
- **Tier 2 — SQL Server async conversion.** Convert `SqlServerBackend`'s hot scan/DML/bulk paths to async
  SqlClient + token (bulk via the reader-throw, no conversion needed). Optional `command_timeout` setting.
- **Tier 3 — DAX `Cancel()`** and any remaining paths.
- **Tier 4 (proper, larger) — async source.** Rearchitect the arrow scan as a DuckDB async/BLOCKED source
  (`InterruptState`): run the I/O on a background thread, return `BLOCKED` so the task thread is *released*
  during I/O, resume via the InterruptState callback. Native interruption + better parallelism under slow I/O
  (today a blocked scan holds a whole task slot). Big change to `arrow_ingest`; do it only if scan concurrency
  under slow I/O becomes a goal.

## Caveats

- The poller is one pool-thread task per in-flight scope — cheap, but it *is* a thread, so a scope should
  wrap an actually-blocking operation, not be created gratuitously.
- `AmbientOpener` is `AsyncLocal`; capture the opener explicitly for the poller rather than relying on flow
  into a detached task.
- Opener/`ClientContext` lifetime: the scope must be disposed before the `ClientContext` is freed (it is —
  the scope wraps the scan/op, which ends within the query); `Dispose` stops the poller and waits briefly so
  no poll outlives the context.
- Live interrupt behavior (Ctrl+C mid-read) is validated manually (a slow OneLake/SQL query + Ctrl+C); the
  automated suites verify only that a never-tripped token is behavior-neutral (full delta sweep unchanged).
