# Transactions & MARS — how `mssql_net` maps DuckDB transactions onto SQL Server

> Reference for the transaction lifecycle across the C++ ⇄ C# boundary: how DuckDB's
> autocommit / `BEGIN` / `COMMIT` / `ROLLBACK` drive the SQL Server connection, when a
> connection is pinned, and why MARS is enabled. Grounded in the current implementation —
> file references use repo-root-relative paths.

## TL;DR

- DuckDB owns the commit decision; the extension only **relays** it to SQL Server. There is no
  point where the extension decides to commit or roll back on its own.
- **Autocommit is not "no transaction"** — it's an implicit one-statement transaction (begin
  before the statement, commit on success / rollback on error).
- A SQL Server connection is **pinned only by a write**, lazily. Reads reuse a pin if one exists
  but never create one. A read-only transaction pins nothing.
- **MARS** (`MultipleActiveResultSets=true`) is forced so a still-open scan reader and the
  transaction's DML can coexist on the single pinned connection (read-your-writes).

## 1. DuckDB's core transaction model

DuckDB connections start in **autocommit** mode (`TransactionContext` ctor sets `auto_commit = true`,
`current_transaction = nullptr` — `duckdb/src/transaction/transaction_context.cpp`). Each statement is
bracketed in `duckdb/src/main/client_context.cpp`:

- **Before** (`BeginQueryInternal`): `if (transaction.IsAutoCommit()) transaction.BeginTransaction();`
  — a fresh transaction is created unconditionally (not gated on writes).
- **After** (`EndQueryInternal`): if still in autocommit, `success ? Commit() : Rollback()`.

So "autocommit" = an implicit per-statement transaction, committed at statement end.

**Explicit `BEGIN` / `COMMIT` / `ROLLBACK` just toggle the autocommit flag.** `BEGIN` runs *as* an
autocommit statement, so `BeginQueryInternal` already created the transaction; the BEGIN operator then
calls `SetAutoCommit(false)`, and because a transaction already exists `EndQueryInternal` does *not*
commit it → it persists. Subsequent statements reuse it (autocommit off → no begin, no commit). `COMMIT`
(`TransactionContext::Commit`) calls `ClearTransaction()` → `SetAutoCommit(true)`, restoring autocommit;
`ROLLBACK` is symmetric. The `TransactionContext` destructor rolls back any still-open transaction on
connection teardown.

### MetaTransaction — the multi-catalog coordinator

A DuckDB transaction is a **`MetaTransaction`** (`duckdb/src/transaction/meta_transaction.cpp`) spanning
*all* attached databases, not one per catalog. Two behaviors matter here:

- **Lazy per-catalog start.** `MetaTransaction::GetTransaction(db)` calls
  `db.GetTransactionManager().StartTransaction(context)` the first time a catalog is *touched* within the
  transaction, then caches it. So the extension's transaction starts on first access to the SQL Server
  catalog — not at `ATTACH`, not at `BEGIN`.
- **Commit fans out.** One DuckDB `COMMIT` walks every participating transaction in reverse order and
  calls each manager's `CommitTransaction` (if one fails, the rest roll back). `Rollback` is symmetric.
- **One-writer rule.** `MetaTransaction::ModifyDatabase` enforces that a single transaction writes to only
  **one** attached database — `BEGIN; INSERT INTO mssql.t …; INSERT INTO local.t …; COMMIT` throws. A
  user-facing constraint worth knowing.

## 2. The three lazy levels

"Lazy on first write" is often misremembered as applying to `StartTransaction`. It does not — these are
three *distinct* deferral points:

| Level | What | When |
|-------|------|------|
| **L1** | DuckDB `BeginTransaction()` (the `MetaTransaction`) | **Always**, per statement in autocommit (unconditional in `BeginQueryInternal`). |
| **L2** | Extension `ArrowNetTransactionManager::StartTransaction` → C# `_inTransaction = true` | **Lazy on first catalog touch** within the transaction (via `MetaTransaction::GetTransaction`). |
| **L3** | C# pins the `SqlConnection` + opens the `SqlTransaction` (`BeginWrite`) | **Lazy on the first *write*** only. |

So even a plain autocommit `SELECT * FROM mssql.dbo.t` runs L1 + L2 (flag flips), but **not** L3 — no
connection is pinned, the scan uses a pooled connection, and the statement-end commit is a no-op.

## 3. How the extension participates

`src/catalog/arrownet_transaction.cpp` (`ArrowNetTransactionManager`) is a thin participant in DuckDB's
protocol:

- `StartTransaction` → `arrownet::BeginTransaction(handle)` → C# `SqlServerCatalog.BeginTransaction()`,
  which only sets `_inTransaction = true` ("pin lazily on the first write"). **Best-effort** — a failure
  here must not abort the statement (a later write would fail loudly on its own).
- `CommitTransaction` → `arrownet::CommitTransaction(handle)` → C# `EndTransaction(commit: true)`.
- `RollbackTransaction` → `arrownet::RollbackTransaction(handle)` → C# `EndTransaction(commit: false)`.
  **Never throws** (rollback must be safe on every teardown path).

C# transaction state lives on `SqlServerCatalog` in `dotnet/ArrowNet.SqlServer/SqlServerBackend.cs`
(`_txnLock`, `_inTransaction`, `_txnConnection`, `_txn`). `EndTransaction` commits/rolls back `_txn` if
present, then disposes `_txn` + `_txnConnection` and clears `_inTransaction`. A read-only transaction
reaches `EndTransaction` with `_txn == null` → a graceful no-op.

## 4. Connection pinning rules

The pin (`_txnConnection` + its `SqlTransaction`) is created in exactly one place: `BeginWrite()`
(`SqlServerBackend.cs`). It is the **sole** pinner:

```
internal (SqlConnection, SqlTransaction?, bool owns) BeginWrite() {
    lock (_txnLock) {
        if (_inTransaction) {
            if (_txnConnection is null) {           // ← the pin, first write only
                _txnConnection = OpenConnection(); _txnConnection.Open();
                _txn = _txnConnection.BeginTransaction();
            }
            return (_txnConnection, _txn, owns: false); // borrowed — caller must NOT dispose
        }
    }
    var c = OpenConnection(); c.Open();
    return (c, null, owns: true);                       // autocommit: fresh connection, caller disposes
}
```

Callers that pin (the write paths): `ExecuteNonQuery`, `BulkInsert`, `ExecuteDelete`, `ExecuteUpdate`,
`InsertReturning`, `CreateTable` (and other DDL routed through `BeginWrite`), and `SqlServerProcEach`
(per-row proc `_each`).

**Reads never pin.** `ExecuteQuery` does `pinned = (_inTransaction && _marsEnabled) ? _txnConnection : null`
— it *reads* the field, never assigns it. Consequences:

| Sequence (within a transaction) | Read connection |
|---|---|
| Write, then read (**MARS on**) | The **pinned** connection + its `SqlTransaction` → **read-your-writes**. |
| Write, then read (**MARS off**) | A **fresh pooled** connection → **no read-your-writes** (see §5.1). |
| Read before any write | `_txnConnection` is null → a **fresh pooled** connection. |
| Read-only transaction | Nothing pinned; every scan is pooled; commit/rollback are no-ops. |

**Invariant:** because pinning happens only in the write path, which opens the `SqlTransaction` at the
same moment, a reused pin is *always* inside the live transaction. That's what makes read-your-writes
sound.

## 5. MARS (Multiple Active Result Sets)

`SqlServerCatalog` forces `MultipleActiveResultSets = true` on the connection string
(`SqlServerBackend.cs`, in the ctor via `SqlConnectionStringBuilder`).

**Why.** A non-MARS `SqlConnection` allows exactly one active command/`SqlDataReader` at a time; opening a
second while a reader is live throws *"There is already an open DataReader associated with this Command
which must be closed first."* DuckDB executes **pull-based**: a scan's `SqlDataReader` stays open across
many `get_next` calls, drained one Arrow batch at a time. When a transaction pins one connection and
routes both reads and writes through it, a still-open scan reader and a DML command end up active on the
same connection simultaneously → MARS is required.

**MARS is interleaved-serial execution, NOT parallelism.** Only one request executes on the server at any
instant; the sessions are multiplexed (SMUX over the one socket) and time-sliced at yield points, never
run concurrently. What MARS lifts is the *non*-MARS rule "fully drain reader A before you can run command
B" — with MARS you can hold A open (partially read), run/read B, then resume A, *in turns*. A `SELECT`
yields after each result packet (the client must fetch to continue); a DML statement runs to completion
before yielding. So we enable it for **coexistence** (an open reader + DML on the pinned connection), never
for speed. Corollary: inside a transaction, once a write pins the connection, reads funnel through it and
MARS **serializes** them — cross-scan read parallelism is given up there (the cost of single-connection
read-your-writes). Real read parallelism (separate scan threads → separate physical connections) happens
only *outside* a transaction via the ADO.NET pool.

**When it's strictly necessary.** The unambiguous case is **read-your-writes**: a write pins the
connection + opens the `SqlTransaction`, then a later scan in the same transaction *deterministically*
reuses the pinned connection (`ScanFromSource`) and must coexist with the transaction's DML. Outside a
transaction MARS is moot — ADO.NET pools by connection string, so independent operations each get their
own physical connection.

**The exchange is deliberately MARS-free.** The in-out streaming exchange (`SqlServerTvfEach`, the `_each`
CROSS APPLY path) serializes access to its own connection via the C++ gate (`MaxThreads = 1`) instead of
relying on MARS — a conscious choice for Fabric / Azure endpoints where MARS may be unavailable. So: the
**pinned transaction connection** depends on MARS; the **exchange connection** sidesteps it with
serialization.

### 5.1 Connection mode — `mssql_mars` + the warehouse (no-MARS) path

MARS is **not** unconditionally forced. It is resolved once, at the first connection (ATTACH validates →
`ServerProfile.Detect` runs on a deliberately MARS-free probe → the working connection string is finalized),
from the `mssql_mars` provider setting:

| `mssql_mars` | Effect |
|---|---|
| `auto` (default) / unset | MARS = `profile.SupportsMars` — on for box SQL Server / Azure SQL DB, **off** for Synapse / Fabric (which reject a MARS connection outright). |
| `true` | Force MARS on. On a non-MARS engine this fails loudly at connect — the user's choice. |
| `false` | Force MARS off, even on box SQL Server (pooled reads, no read-your-writes — see below). |

`mssql_mars` is a **global** provider setting (`SET mssql_mars=…`), resolved at first connection, so **set it
before `ATTACH`** — a later `SET` does not re-finalize an already-connected catalog (same lifetime as the
detected profile). A per-catalog `mars` ATTACH option is deferred to the ATTACH-options→C# refactor (see
`docs/provider-extensibility.md`); the global `SET` is the override available today, symmetric to
`SET mssql_isolation_level` alongside its own deferred per-catalog cutover. *(Implementation note for tests:
`RESET mssql_mars` does **not** clear the value — DuckDB does not fire an extension option's set-callback on
RESET — so restore the default with `SET mssql_mars='auto'`, which does.)*

**With MARS off, data SCANS never reuse the pinned write connection.** An open scan reader and the
transaction's DML can only coexist on one connection under MARS, so when MARS is off `ExecuteQuery` routes
every **scan** through a **fresh pooled connection**, even inside a write transaction. The trade-off: **no
read-your-writes for scans within the write transaction** — a scan sees the last committed state, not the
transaction's uncommitted writes. This is the documented warehouse behavior ("pin only for writes, pooled
connections for reads"). On a **snapshot**-isolation engine (Fabric) the pooled read sees a consistent
committed snapshot and never blocks; on a non-snapshot box engine with `mssql_mars=false`, a pooled read of a
row the same transaction is **writing** would block on the writer's locks — so don't read rows the open
transaction is modifying when you force MARS off on box.

**Metadata reads are the exception — they keep read-your-writes even with MARS off.** A short metadata read
(`ExecuteMetadataQuery`: `FetchTableColumns` / `FetchRowIdColumns` / the catalog discovery queries) reuses the
pinned write connection whenever one exists, **regardless of MARS**. It holds no long-lived reader (it drains
immediately), and on a MARS-off engine the pinned connection never carries a concurrent scan reader, so reusing
it is safe. This is **required**: `CREATE TABLE` runs on the pinned connection inside the lazy write
transaction, then the catalog immediately re-fetches the new table's columns to build its entry — on Fabric a
pooled read couldn't see the still-uncommitted `CREATE`, so the self-healing cache would evict the table the
`CREATE` just made (symptom: "table … does not exist" right after a same-session `CREATE`). The metadata read
on the pinned connection sees the uncommitted `CREATE` and the entry materializes correctly.

**Warehouse write transactions run at SNAPSHOT.** Fabric Warehouse / Lakehouse SQL endpoint support only
snapshot isolation, so `BeginWrite` opens the pinned `SqlTransaction` at `IsolationLevel.Snapshot` for those
engines (`ServerProfile.DefaultWriteIsolation`); box SQL Server / Azure SQL DB / Synapse dedicated keep the
connection/server default (Synapse dedicated is intentionally **not** forced to snapshot — its default is READ
UNCOMMITTED and snapshot may be disabled).

### Contrast: the native `mssql-extension` (TDS sibling) does NOT use MARS

The compatibility-target sibling `mssql-extension` (`D:\repos\mssql-extension`, native C++ TDS, no
SqlClient) **disables MARS** and solves the same transactional problem a different way. Verified in that
repo:

- PRELOGIN advertises the MARS option but sets the data byte to `0` (`src/tds/tds_protocol.cpp`,
  "MARS data: disabled (0)"); it never implements the TDS **SMUX** session-multiplexing layer MARS rides on.
- Its connection-pooling spec lists MARS as *"not planned"* and states the design assumption *"Single-threaded
  connection use is acceptable (no MARS requirement)"* (`specs/003-tds-connection-pooling/spec.md`).

It still **pins a connection per transaction** (`src/catalog/mssql_transaction.cpp` `pinned_connection_` /
`SetPinnedConnection`; `mssql_catalog.cpp` routes in-transaction schema lookups through it). The difference
is *how* operations share that pinned connection:

- **Native — one active command at a time.** It binds each `SQL_BATCH` to the transaction by stamping the
  **TDS Transaction Descriptor header** into the ALL_HEADERS prefix (`tds_protocol.cpp` `BuildSqlBatch`) —
  the 8-byte descriptor captured from `BEGIN TRAN`'s ENVCHANGE. Operations are serialized; a read completes
  before the next runs. That header (not MARS) is how it does transactions; its code comment implying MARS
  is required for it is a misnomer — ALL_HEADERS + Transaction Descriptor is mandatory for `SQL_BATCH` in
  TDS 7.2+ regardless of MARS.
- **`mssql_net` — two active result sets at once.** SqlClient + DuckDB's pull-based execution keep a
  `SqlDataReader` open across many `get_next` calls, and we route that read *and* the transaction's DML
  through the one pinned connection (read-your-writes) → an open reader and a DML command live on the same
  connection → MARS.

Net: the native extension's serialized model can't interleave a still-open streaming scan reader with DML
on the pinned connection; our streaming-reader-plus-DML pattern requires MARS to make that legal. Same
pinned-connection transaction shape, opposite answer on MARS.

## 6. `INSERT INTO xx SELECT … FROM y` — pin timing is a race (and harmless)

INSERT uses the streaming bulk path (`begin_bulk` / `push_batch` / `complete_bulk`,
`dotnet/ArrowNet.Bridge/BulkSession.cs`). The pin is **deferred to a background task**: the `BulkSession`
ctor spins up `Task.Run(() => catalog.BulkInsert(...))`, and the pin happens inside that pool-thread task
when `BulkInsert` → `BeginWrite()` runs. `SqlBulkCopy` then loads `xx` on the pinned connection + `SqlTransaction`.

The SELECT's connection is therefore chosen by a **scheduling race**, decided once when the scan of `y`
opens its reader:

- The DuckDB executor thread returns from `begin_bulk` and proceeds synchronously to pull the source, so
  the scan usually opens **before** the freshly-queued consumer pins → it reads `y` on a **fresh pooled**
  connection.
- If the consumer pins first, the scan reuses the **pinned** connection — and *this* is where MARS lets
  the in-flight bulk-copy write to `xx` and the scan reader on `y` coexist.

**Both outcomes are correct** for `y ≠ xx`: the load into `xx` is transactional either way, and reading a
*different* table needs no read-your-writes, so the race is invisible in results.

Caveat: `INSERT INTO xx SELECT … FROM xx` (same table) would be a Halloween/consistency hazard if the
scan shared the pinned connection mid-load; channel buffering + the usual pooled-scan outcome keep them
decoupled, but it's not a pattern to rely on without a dedicated test.

## 7. Per-row stored procedures (`_each`) ride DuckDB's transaction

`SqlServerProcEach` (the proc `_each` exchange binding, `dotnet/ArrowNet.SqlServer/SqlServerProcEach.cs`)
runs its per-row `EXEC` on **DuckDB's pinned write connection** via `BeginWrite()`, and deliberately does
**not** commit or dispose the scope. So the proc's writes commit/roll back with **DuckDB's**
`COMMIT`/`ROLLBACK` — atomic in autocommit *and* inside an explicit `BEGIN`, with no per-row commits. The
exchange gate (`MaxThreads = 1`) serializes the EXECs on the pinned connection. A thrown EXEC error
propagates out → fails the statement → DuckDB rolls it back. This is the proc analog of the general
"DuckDB owns the commit signal" rule.

## Key invariants & safety nets

- **DuckDB owns commit/rollback.** The extension relays the decision; it never self-commits. The one place
  C# opens a `SqlTransaction` (`BeginWrite`) is matched only by `EndTransaction`, driven by DuckDB's
  `Commit/RollbackTransaction`.
- **Rollback never throws** (`RollbackTransaction`, `EndTransaction` cleanup) — safe on every teardown,
  including the `TransactionContext` destructor path.
- **Pins are write-created, read-reused.** A reused pin is always inside the live `SqlTransaction`.
- **MARS for the pinned connection; serialization (gate) for the exchange connection.**
- **One attached DB written per transaction** (DuckDB's `MetaTransaction::ModifyDatabase`).
