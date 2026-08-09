# Transactions & MARS — how `fabricator` maps DuckDB transactions onto SQL Server

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
| **L2** | Extension `FabricatorTransactionManager::StartTransaction` → C# `_inTransaction = true` | **Lazy on first catalog touch** within the transaction (via `MetaTransaction::GetTransaction`). |
| **L3** | C# pins the `SqlConnection` + opens the `SqlTransaction` (`BeginWrite`) | **Lazy on the first *write*** only. |

So even a plain autocommit `SELECT * FROM mssql.dbo.t` runs L1 + L2 (flag flips), but **not** L3 — no
connection is pinned, the scan uses a pooled connection, and the statement-end commit is a no-op.

## 3. How the extension participates

`src/catalog/fabricator_transaction.cpp` (`FabricatorTransactionManager`) is a thin participant in DuckDB's
protocol:

- `StartTransaction` → `fabricator::BeginTransaction(handle)` → C# `SqlServerCatalog.BeginTransaction()`,
  which only sets `_inTransaction = true` ("pin lazily on the first write"). **Best-effort** — a failure
  here must not abort the statement (a later write would fail loudly on its own).
- `CommitTransaction` → `fabricator::CommitTransaction(handle)` → C# `EndTransaction(commit: true)`.
- `RollbackTransaction` → `fabricator::RollbackTransaction(handle)` → C# `EndTransaction(commit: false)`.
  **Never throws** (rollback must be safe on every teardown path).

C# transaction state lives on `SqlServerCatalog` in `dotnet/Fabricator.SqlServer/SqlServerBackend.cs`
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
row the same transaction is **writing** blocks on the writer's locks — so don't read rows the open
transaction is modifying when you force MARS off on box. ⚠ **That last clause is measured, and it is worse
than "don't do it": the block happens at BIND, it is unbounded, and `mssql_materialize=false` does not avoid
it** — see "the bind-time schema scan" below.

#### ⚠ MEASURED LIVE on Fabric Warehouse, 2026-08-08 — the paragraph above was reasoning; this is the number

The question is whether we share a defect common to extensions that pin one connection per transaction:
**that connection cannot stream a result set and receive a bulk load at the same time**, so reading and
writing the same attached catalog inside a transaction fails. `duckdb-postgres` avoids it with
`MaterializePostgresScans` in `PlanCreateTableAs`, and `duckdb-mysql` with `MaterializeMySQLScans` called from
its INSERT/CTAS planner ([41ddbe2](https://github.com/duckdb/duckdb-mysql/commit/41ddbe2aff6b65455eefbd6a860cb9026631a704))
— both materialise their own scans before the sink so the single connection is never doing both at once.

Profile confirmed first: `engine_edition 11`, `is_warehouse true`, **`supports_mars false`**,
`default_write_isolation snapshot`. Then, on a 3-row seeded table:

```sql
BEGIN;
  INSERT INTO w.dbo.marsprobe VALUES (4,'intxn');
  SELECT count(*) FROM w.dbo.marsprobe;                                    -- 3, NOT 4
  INSERT INTO w.dbo.marsprobe SELECT id + 100, 'selfcopy' FROM w.dbo.marsprobe;
COMMIT;
SELECT count(*) FROM w.dbo.marsprobe;                                      -- 7
```

**The self-referencing INSERT SUCCEEDS** — we do not have that defect. And the final count is the
discriminator, chosen so one number settles it: **7** (seed 3 + intxn 1 + selfcopy 3) means the scan read the
COMMITTED state; **8** would have meant read-your-writes. Tags came back `seed 3 / intxn 1 / selfcopy 3`.

Mechanism confirmed from the `Fabricator.Sql` routing log rather than inferred: inside the explicit
transaction **every data scan logged `[pooled txn=13]`** while the writes went to the pinned connection
(`[dbo].[marsprobe]: 3 rows copied`). The only two `[pinned …]` lines both carry the ` ryw` marker — i.e. the
metadata reads that are deliberately exempt from the MARS gate, exactly as documented above.

#### ⚠⚠ AND THE "WE DO NOT HAVE IT" CONCLUSION ABOVE WAS WRONG — MEASURED 2026-08-08, FIXED THE SAME DAY

The Fabric run settled Fabric and nothing else. On **box SQL Server the same shape FAILS**, and only a
too-small probe hid it:

```
595: Bulk Insert with another outstanding result set should be run with XACT_ABORT on.
```

**It is SIZE-DEPENDENT.** With a `(INTEGER, VARCHAR)` row: 500 / 1k / 2k / 5k / 10k / 20k rows all PASS;
**30k / 50k / 75k / 100k all FAIL.** Below roughly one buffered result the scan drains before the bulk
begins and nothing collides — which is why every existing suite was green. It fails in **autocommit too**,
not just inside `BEGIN`: the bulk session pins the connection at operator init, *before* the scan streams,
so the scan then reuses it.

**⇒ MARS is not what saves us; it is what breaks us.** The pooled-scan routing we only do BECAUSE Fabric
lacks MARS is the half that was correct all along — Fabric returned 200000/200000 at the same 100k seed.

Two plausible fixes are MEASURED WRONG, and both are the obvious guesses:
- **`READ_COMMITTED_SNAPSHOT ON`** at the database level changes NOTHING — 595 fires identically. Snapshot
  isolation fixes readers BLOCKING writers, and nothing here ever blocks; a lock conflict would surface as
  a wait or a 1205. This is a MARS restriction about **error semantics**, not visibility.
- **`SET XACT_ABORT ON`** via `fabricator_exec` did not help either — but that result is **VOID, not
  negative**: it was never established that the SET landed on the connection the bulk copy used. Not
  pursued, because it would do nothing for Fabric and would make any error abort the whole transaction.

**THE FIX (2026-08-08): materialise our own scans — `FabricatorCatalog::MaterializeOwnScans`.** A recursive
walk over the physical plan in `PlanInsert`/`PlanCreateTableAs` marks every fabricator scan whose table
belongs to THIS catalog; the mark rides `spec_json` as `"materialize":true` (**no ABI bump** — that field is
free-form), and `SqlServerBackend` drains the reader and **disposes** it before returning the stream.
- **The DISPOSE is the load-bearing part, not the buffering.** `DbDataReaderArrowStream` used to close its
  reader only in `Dispose()`, while the scan releases its Arrow stream in the global state's DESTRUCTOR
  (query teardown, not pipeline end) — so a merely-blocking operator would have left a drained-but-OPEN
  reader, which SQL Server still counts as outstanding.
  - **⚠ SUPERSEDED 2026-08-09 — the stream now releases at END OF RESULT SET** (§5.3), so a drained scan
    stops being outstanding at once. That does NOT change this fix: here the scan and the bulk are
    PIPELINED, so the reader is open *while* the bulk consumes it and no eager close can help. What it does
    change is the deferred design below — a blocking operator is now sufficient on its own.
- **Both payoffs from one change**: box stops failing, and because a drained scan leaves no outstanding
  reader, it may use the PINNED connection even without MARS ⇒ **read-your-writes RESTORED on Fabric**
  (measured: the self-copy now sees the transaction's own row — final **8**, `selfcopy 4`, where before it
  was 7 / 3).
- **Ours is NARROWER than the reference implementations.** `MaterializePostgresScans` and
  `MaterializeMySQLScans` match on the function NAME, so they materialise every scan of their type in the
  plan — including one from a DIFFERENT attached database of the same kind. We match on bind-data type
  **plus catalog identity**, so a scan of another fabricator catalog keeps its pipelining.
- **Provider-agnostic by construction**: C++ only states "this scan and a sink share a catalog". Only
  `SqlServerBackend` reads the flag; Delta/DAX/DeltaRs parse the spec and ignore it, which is correct — a
  provider holding no connection has nothing to collide.
- **`max_threads = 1` is NOT needed here**, though postgres sets it: their scan is parallel by default,
  ours declares `MaxThreads() { return 1; }` ("a single Arrow C stream is consumed serially"). Checked
  rather than assumed.
- ⚠ **COST, and it is the same one postgres and mysql pay: the whole source is BUFFERED IN MEMORY, with no
  spill to disk.** Neither reference implementation spills either (both just tell the client library to
  buffer), so this is parity, not a shortcut — but it converts a hard failure at scale into a memory cost
  at scale, and the ceiling is unmeasured. The better design, deliberately deferred, is a
  `ColumnDataCollection`-backed operator (which DuckDB would spill) **plus** an eager close-on-exhaustion —
  the operator alone was not sufficient, per the DISPOSE note above.
  - **⚠ THE SECOND HALF IS NOW BUILT (2026-08-09, §5.3), so the operator IS sufficient on its own and this
    stops being a two-part change.** That materially re-prices it: the memory ceiling flagged as unmeasured
    here would be replaced by DuckDB's own spilling, and nothing else is blocking.
- Gate: `test/verify_read_write_same_catalog.test` (**68**, service tier) — autocommit and in-transaction at
  30k, read-your-writes, and a small-table control whose absence would let the others pass by self-insert
  simply having stopped working.

**⚠ So we avoid it by ROUTING where postgres/mysql avoid it by MATERIALISING — and since 2026-08-08 we do
BOTH, which is what closed the gap this paragraph used to describe as open.** They keep read-your-writes
(their materialised scan runs on the same connection); we gave it up on every no-MARS engine. Materialising
removes the open reader, which is precisely what forbids the pinned connection with MARS off — so a
**MARKED** scan now pins on a no-MARS engine too and read-your-writes is restored for it (the `materialize`
term in `ExecuteQuery`'s routing condition). ⚠ **The restoration is SCOPED to marked scans**, i.e. to a
statement that reads and writes the same catalog: an ordinary in-transaction `SELECT` is unmarked, still
pooled on a no-MARS engine, and still sees only committed state. A statement that silently reads a different
snapshot than the user expects is the failure mode to watch for there.
- ⚠ Worth stealing from the mysql commit if it is ever built: it decides **per plan** whether to stream or
  materialise (streaming only when a single scan is present) instead of materialising unconditionally as
  postgres does, and it logs the streaming flag — which is what makes the choice observable rather than
  arguable.
- Scope: SQL-Server-path only. The **Delta** provider holds no connections, so this whole class is unreachable
  there.

#### ⚠ THE BIND-TIME SCHEMA SCAN IS A THIRD ROUTE, AND WITH MARS OFF IT BLOCKS — measured 2026-08-09

> **⚠ PARTLY SUPERSEDED THE SAME DAY (§5.4): the probe no longer reads the table** — it asks for schema only,
> so SQL Server renders `WHERE 1 = 0`. **The hazard below did NOT go away, it MOVED**: re-measured after the
> change, the bind no longer blocks and the EXECUTION scan does, one query later. Read the mechanism here as
> the general shape (an unversioned pooled read against the transaction's own uncommitted rows) rather than as
> a statement about which query blocks.

**Every scan binds by opening a throwaway, UNPROJECTED `SELECT * FROM t`** purely to read the Arrow schema,
then releasing it (`PopulateReturnSchema`, `src/fabricator/arrow_ingest.cpp:179` — *"a bare request (no
projection/filter) ⇒ the provider reports the full column set"*). So a statement issues **two** scan queries,
visible at `FABRICATOR_LOG_LEVEL=Debug`:

```
47.153  query [pooled txn=11]: SELECT * FROM [dbo].[t]      ← bind-time schema scan
47.182  bulk [dbo].[t]: … txn=11                             ← the pin is created HERE
47.212  query [pinned txn=11]: SELECT [id] FROM [dbo].[t]   ← execution scan (marked, materialised)
```

**That bind scan carries NO scan spec**, so `materialize` and `snapshotRead` are both false and the routing
condition reduces to `pin exists && mars`. With MARS off it is therefore a plain **pooled READ COMMITTED**
read — and in a transaction that has already written to `t` it blocks on that transaction's own uncommitted
rows. `mssql_command_timeout` defaults to **0 = infinite**, so this is an unbounded hang, not an error.

| exp | `mssql_mars` | `mssql_materialize` | RCSI | bind scan | outcome |
|---|---|---|---|---|---|
| C | off | true | off | pooled | **blocked** (capped at 15 s to observe it) |
| D | off | false | off | pooled | **blocked** |
| E | **on** | false | off | pinned | completed |
| F | off | false | **on** | pooled | completed |

C/D vs E isolates the routing; **D vs F is byte-identical but for RCSI** and isolates the remedy.

- **The hazard is broader than same-catalog read+write.** It is: MARS off + a transaction that has already
  written to `t` + *any* later scan of `t`. The self-copy shape is merely where it was met.
- **⚠ `mssql_materialize=false` demands the WRONG prerequisite on box.** `EnsureSnapshotIsolationAllowed`
  requires `ALLOW_SNAPSHOT_ISOLATION`, which covers the **execution** scan (it issues an explicit `SET
  TRANSACTION ISOLATION LEVEL SNAPSHOT`). The **bind** scan never asks for snapshot, so only database-level
  **RCSI** covers it — experiment D has `ALLOW_SNAPSHOT_ISOLATION` on and still blocks.
- **Fabric/Synapse are unaffected**, which is why this has never been seen in the field: they are
  snapshot-isolated by construction, so a pooled read never blocks. On box it is reachable only by
  explicitly setting `mssql_mars=false`.
- **It also CONFIRMS the materialise fix's ordering assumption rather than undermining it.** The bulk pins at
  operator init *before* the execution scan runs (the trace above), so a marked scan takes the pinned
  connection even with MARS off — the fix does not depend on luck. An earlier note recorded the opposite as
  an open worry; the trace closes it.
- ⚠ **The bind scan is released SYNCHRONOUSLY at bind** (`arrow_ingest.cpp:191-199`), so it is never alive
  during the bulk — which is what rules it out as a mechanism for the 595 collision. It does cost an extra
  server round trip and a started `SELECT *` per scan; bounded, but not free.

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

### 5.2 A DuckDB transaction is NOT a read-consistency boundary — measured 2026-08-09

`BEGIN; SELECT count(*) FROM t; SELECT count(*) FROM t; COMMIT;` can return **different answers** if another
session commits an insert in between. Measured on three configurations:

| engine | config | FIRST → SECOND |
|---|---|---|
| box | `mssql_mars=false`, RCSI on, read-only txn | 3 → **4** |
| box | MARS on, txn had already written (both reads `pinned txn=2`, ONE `SqlTransaction`) | 4 → **5** |
| **Fabric Warehouse** | default (MARS off, snapshot engine) | 3 → **4** |

**Two independent causes; on box BOTH apply, on Fabric only the first.**

1. **Reads never pin.** The `TxnState` is created by `BeginWrite`, so a transaction that has not written has
   no pinned connection and every scan takes a **fresh pooled connection in its own implicit transaction** —
   two `SELECT`s are two unrelated server sessions. This is independent of MARS: MARS decides whether an
   *existing* pin may be reused, not whether one exists.
2. **READ COMMITTED is statement-scoped, and RCSI does not change that.** RCSI makes READ COMMITTED
   *versioned* (a snapshot per statement) rather than transaction-wide. The second row above is the proof:
   both reads on one connection inside one `SqlTransaction`, and the phantom still appeared.

**There was no knob; since 2026-08-09 there is an OPT-IN one — `mssql_read_isolation` / the `read_isolation`
ATTACH option (§5.8). The table above is what you get with it UNSET, which is the default.** Note the
`isolation_level` ATTACH option and `SET mssql_isolation_level` are a DIFFERENT knob and still govern
table-in-out (`fn_each`) sessions only; the names invite the wrong reading, which is exactly why the new
option did not reuse them.

✅ **RESOLVED 2026-08-09 — pinning the reads IS sufficient on Fabric, MEASURED LIVE on a Fabric Warehouse.**
This paragraph read *"UNKNOWN, and do not assume it … our routing can never put two reads on one connection on
Fabric, so the property is unexercised and untested"*. The §5.8 opt-in put two reads on one Fabric connection
for the first time, and the answer is yes: control **3 → 4**, opt-in **4 → 4** while the writer committed a
fifth row (verified afterwards — the table held 5 while both reads said 4), both reads logging `pinned txn=3`.
So SQL Server's documented transaction-scoped SNAPSHOT does hold through our routing, and cause 2 really is
inapplicable there. ⚠ The caution the paragraph carried was still right at the time: an earlier draft had
asserted it as fact without a measurement, and the fact happened to be true — which is not the same as having
known it.

**What to do instead:** set `mssql_read_isolation='snapshot'` (§5.8, opt-in — it holds a connection and an
open transaction for the DuckDB transaction's life), or materialise once into DuckDB
(`CREATE TEMP TABLE … AS SELECT`) and read that. DuckDB's MVCC covers DuckDB-native storage; an attached
catalog is a passthrough, so cross-statement read stability is whatever the remote engine's isolation gives —
never more.

#### What it takes on box — the analysis the opt-in was built from (2026-08-09)

Which isolation level actually delivers it, measured directly in T-SQL on the docker box
(`ALLOW_SNAPSHOT_ISOLATION` on, RCSI **off**), one script per level, same 9 s window and same concurrent
inserter:

| `SET TRANSACTION ISOLATION LEVEL` | first → second | the writer |
|---|---|---|
| **`SNAPSHOT`** | **3 → 3** | committed immediately — versioned, never blocked |
| `READ COMMITTED` | 3 → 4 | committed |
| **`REPEATABLE READ`** | **3 → 4** | committed |
| `SERIALIZABLE` | 3 → 3 | prevented from committing inside the window |

**⚠ Three traps, and the first two are what make the obvious design wrong.**

1. **There is no `READ COMMITTED SNAPSHOT` session level — it does not parse.** Measured, both spellings:
   `SET TRANSACTION ISOLATION LEVEL READ_COMMITTED_SNAPSHOT` ⇒ *Msg 102, Incorrect syntax near
   'READ_COMMITTED_SNAPSHOT'*; with spaces ⇒ *Msg 1018, Incorrect syntax near 'SNAPSHOT'*; `SNAPSHOT` alone is
   accepted. **RCSI is not a LEVEL, it is a reinterpretation of one**: `ALTER DATABASE … SET
   READ_COMMITTED_SNAPSHOT ON` changes what READ COMMITTED *does* on that database (row versions instead of
   shared locks), so there is nothing for a session to select — the session still selects READ COMMITTED and
   the database decides how it is implemented. That is precisely why it is a database option.
   **And it would not help even if it parsed: RCSI changes the MECHANISM, not the SCOPE.** Both flavours of
   READ COMMITTED take a fresh view per STATEMENT; RCSI changes how that view is obtained, not when it is
   taken — §5.2's second row (4 → 5 on one pinned connection) is that measurement. So "snapshot" appears in
   two unrelated senses, which is the whole trap: `READ_COMMITTED_SNAPSHOT` names a **versioning mechanism**
   with per-statement scope; `SNAPSHOT` names a **level** with per-transaction scope. The one to ask for is
   **`SNAPSHOT`**, whose prerequisite is `ALLOW_SNAPSHOT_ISOLATION`, **not** RCSI.
2. **`REPEATABLE READ` does NOT deliver it** — measured 3 → 4. It forbids re-reading a *row* differently, not
   new rows appearing, and `count(*)` is exactly the phantom case. The name promises the property and does not
   provide it.
3. **`SERIALIZABLE` works by BLOCKING writers**, which is a far worse trade for a reporting workload.
   ⚠ The blocking is inferred from a controlled contrast, not timed: under the identical delay READ COMMITTED
   and REPEATABLE READ both let the insert land inside the window and SERIALIZABLE did not.

**Three changes were needed, and MARS was the least of them.** All three are in §5.8; the analysis is kept
because two of its three conclusions were the reason the shipped shape looks as it does.

- **(a) Reads must PIN.** This is the substantive one: `TxnState` is created by `BeginWrite`, so a read-only
  transaction has no server-side transaction for an isolation level to apply to. The state would have to be
  created on first *catalog touch*, and `BeginWrite` would stop being a write-only concept.
- **(b) `isolation_level` must reach that transaction.** Already parsed into `_isolationLevel`, and
  `ParseIsolationLevel` already maps `'snapshot'` — it is simply never read outside `InOutBind`. Wire it as the
  value with `ServerProfile.DefaultWriteIsolation` as the FALLBACK, so the ATTACH option overrides the profile.
- **(c) MARS on** — but only for a statement carrying **two or more concurrent scans** of the same catalog (a
  self-join). Two sequential `SELECT`s each drain and close their reader, so the plain repeat-read case does
  not need it. ⚠ **Weakened by §5.3**: a hash join drains its build side before the probe side opens, and the
  reader is now released at that point, so the self-join no longer overlaps either — measured. Genuinely
  concurrent scans (parallel branches) would still need MARS or materialisation.

**⚠ It must be opt-in, and the cost is not the connection count.** A pinned read transaction holds a SQL
Server connection **and an open transaction** for the whole DuckDB transaction's life — pool pressure under
`dbt --threads N`, and under `SNAPSHOT` a tempdb **version store that grows for that entire duration**. A
long-running analytical DuckDB transaction would therefore impose a cost on the server that today it cannot.

⚠ **On Fabric the picture is different, and an earlier version of this line got it wrong in both directions.**
It read *"only (a) and (b) would be needed — snapshot is its only level, so (c) is moot and the level is
already right."* Corrected: **(b) is ALREADY SATISFIED** — `ServerProfile.DefaultWriteIsolation` opens the
pinned transaction at `snapshot` there, so nothing needs wiring; and **(c) is NOT moot, it is UNAVAILABLE** —
Fabric rejects MARS, so a statement with two concurrent scans could not put both on the pinned connection and
would have to MATERIALISE them. On a no-MARS engine, transaction-scoped read consistency and streaming
multi-ref reads are therefore **mutually exclusive**. So the missing piece on Fabric is (a) alone for the
single-scan case, and (a) plus unconditional materialisation for the multi-scan one.

⚠ **The ingredients already coexisted there and still delivered nothing**, which is worth stating plainly: a
pinned transaction at transaction-scoped SNAPSHOT exists on every Fabric write transaction, and ordinary
reads simply never routed onto it (only a MARKED scan did). That is what makes (a) the whole of the problem
rather than one third of it — and it is now CONFIRMED rather than argued: with (a) built, the same engine and
the same level deliver 4 → 4 live (§5.8). The transaction-scoped property flagged as UNKNOWN above holds.

### 5.3 The reader is released at END OF RESULT SET, not at query teardown (2026-08-09)

`DbDataReaderArrowStream` closed its reader only in `Dispose()`, and the consumer releases the exported stream
from the scan's global-state **destructor** — query teardown, not pipeline end. A fully-drained scan therefore
held a SQL Server result set open for the rest of the statement. It now releases the reader, command and
(owned) connection **the moment the result set ends**.

Mechanically: the read loop distinguishes *"this batch is full"* from *"the result set ended"*, so EOF is
detected on the **last data batch** rather than one pull later; `Release()` sits behind a `_released` flag and
is called from both EOF points and from `Dispose()`.

**⚠ Idempotency is now load-bearing rather than defensive.** Every drained scan releases **twice** — once
eagerly, once when the consumer releases the exported stream — so the flag is what makes the ordinary path
correct, not a guard against a rare double free. The whole service tier exercises it.

**⚠ It is only sound because the batch owns its memory**, and that is the invariant to protect: two copies
stand between a returned batch and reader-owned memory — `DbDataReader`'s indexer builds a fresh object per
row (it is `GetValue`, never a reusable caller buffer; we use neither `GetBytes`/`GetChars` nor
`SequentialAccess`), and every `ColumnAppender` copies into Arrow builder buffers. `Schema` is captured in the
constructor. An appender that ALIASED the incoming object would turn this into a use-after-free.

**Positive control** — the change is invisible from SQL, so `Release` logs `reader released (eof|dispose)` at
Debug. On a self-join inside a write transaction the release now falls BETWEEN the two scans, where before
both came at teardown:

```
query [pinned txn=8]: SELECT [id] FROM [dbo].[e1]     ← build side
reader released (eof)
query [pinned txn=8]: SELECT [id] FROM [dbo].[e1]     ← probe side
reader released (eof)
```

⚠ The two bind-time `SELECT *` scans in the same log release with `(dispose)` — they read only the schema and
never pull a batch, so they never reach EOF. That is the limitation demonstrating itself.

**What it does and does not cover.**
- **Covers** any scan the consumer drains — which includes a hash-join build side, so the multi-ref case of
  §5.2(c) stops needing MARS. Measured beforehand, both routes work and both put the scans on the pinned
  connection: MARS on with a self-join in a write transaction, and MARS **off** with a marked (materialised)
  self-join. 50 000-row table, correct answer both times.
- **Does NOT cover** a scan abandoned early — `LIMIT`, a short-circuiting join, the bind-time schema scan.
  Those still hold the reader to teardown.
- **Does NOT supersede the materialise fix for error 595.** There the scan feeds the bulk row by row, so the
  reader is open *while* the bulk runs by construction and no eager close is possible.

**Two consequences worth acting on.**
1. **It unblocks the `ColumnDataCollection` operator** (§5.1's deferred design), whose stated blocker was
   exactly the missing eager close. That would replace today's unbounded in-memory materialisation with
   DuckDB's own spilling.
2. **It brings this class in line with `DaxArrowStream`.** The old loop returned a partial batch without
   setting `_done`, so the next pull read **past EOF**. Harmless on `SqlDataReader`, which is idempotent
   there — but `docs/dax-provider.md` records that exact call throwing on ADOMD, which is why DAX has its own
   stream class. Both now stop at the first `false`.

Gate: service tier **46/46 — 1746, identical to the pre-change counts**, which is the behaviour-preservation
claim; plus the positive control above. C#-only, no ABI change.

### 5.4 The bind-time probe DESCRIBES instead of reading (2026-08-09)

Every scan's bind ran the scan factory with an empty request — an unfiltered `SELECT * FROM t` — purely to
learn the Arrow schema the catalog entry had *already* fetched by the cheap route moments earlier. Two
describes per statement, the second a full table read the server begins executing before the bind cancels it.

The bound object now decides how it is described: `ArrowStreamBindData::schema_factory`, set by a catalog
table and left null by `fabricator_query(sql)` / host queries / global table functions, for which running the
thing genuinely is the only way to know its schema. The catalog table's probe is **the same `ScanTable` call**
carrying `{"schema_only":true}` in the free-form `spec_json` (no ABI bump — the `materialize` flag's trick).
SQL Server renders `WHERE 1 = 0`; a provider that ignores the flag is still CORRECT, since the schema of a
scan returning no rows is the schema of that scan returning rows.

**⚠ TWO WRONG TURNS, BOTH ON DELTA, BOTH CAUGHT BY `verify_delta_autocommit_pin` — and the pattern is that
each looked like the obvious simplification.**

1. **Routing the probe to the COLUMNS metadata stream** — the natural "describe result set" call, and it is
   what builds the catalog entry. But on a `native_read` catalog that stream answers from the **codec** route,
   so the schema seeded a codec snapshot pin while the data seeded a native one: two independent pins a
   concurrent commit can separate, i.e. schema and rows read at different versions. Exactly the consistent-cut
   property that suite exists to defend. **Keep the probe on the provider's own scan path** — that is what
   preserves routing, native-vs-codec selection and pin seeding.
2. **Giving the probe a spec at all.** Delta identified the bind probe IMPLICITLY as `spec == null`,
   reasoning that *"every real scan carries a spec with at least its projected columns"*. A probe carrying
   `schema_only` therefore read as a data read, was recorded in the read set, and paid an extra `_delta_log`
   open through the retired as-of resolver. Now EXPLICIT (`spec is null || spec.SchemaOnly`) — better than
   before in its own right, since the old inference was fragile in both directions.

**⚠ METHOD, and it is the transferable part.** The first diagnosis was wrong: the failing assertion is the
THIRD query in that block, not the codec-pin one it sits next to — only reading the file settled it. Causation
was then established by a **one-line A/B** (disable the wiring, rebuild, re-run) rather than by argument:
**disabled ⇒ 65 assertions, resolver fires 0; enabled ⇒ 63, fires 1.** Five minutes, no doubt left.

**⚠ IT DOES NOT FIX §5.2/1.15, and predicting that it would was wrong.** Re-measured: the bind no longer
blocks, and the EXECUTION scan blocks instead, one query later. The cause is the unversioned pooled routing,
not the probe.

Gate: hermetic and service tiers at their baseline counts, plus `verify_delta_autocommit_pin` back to its
control count with the as-of resolver silent.

### 5.5 The MARS-off self-deadlock is REFUSED, not hung (2026-08-09)

§5.1's hazard was an unbounded hang: with MARS off a data scan takes a POOLED connection, so reading a table
the same transaction has already written waits on locks only that transaction can release — and it cannot,
because it is blocked waiting for that scan. A self-deadlock across two connections, invisible to SQL Server's
deadlock monitor (one session waits, the other merely sits idle), with `mssql_command_timeout` defaulting to
0 = infinite. `EnsureScanCannotSelfBlock` now refuses it up front, naming the table and the three remedies.

**⚠ THE REFUSAL IS PRECISE, AND THAT IS WHAT THE WORK WAS.** The cheap version — "MARS off and this
transaction has written *something*" — would refuse reads of tables the transaction never touched, which is
the ordinary shape (read sources, write a target). So `TxnState` tracks WHICH tables were written, populated
by the seven write paths that know their table.

| case | outcome |
|---|---|
| MARS off, no RCSI, table this txn **wrote** | **refused** |
| MARS off, table this txn did **not** write | reads normally |
| MARS **on** (the box default) | reads normally |
| MARS off + **RCSI**, data write | reads normally |
| MARS off + RCSI, uncommitted **ALTER** | **refused** |

**⚠ DATA and SCHEMA writes are not the same case.** RCSI versions ROWS, so with it on a pooled read no longer
blocks on uncommitted rows and there is nothing to refuse. It does NOT version METADATA: an uncommitted
`ALTER` holds Sch-M, which blocks a reader's Sch-S at every isolation level. Hence the last row.

**⚠ TWO BUILD ERRORS, both found by measuring rather than review.**
1. **`RecordTouch` placed before `BeginWrite` records nothing** — the `TxnState` does not exist yet on a
   transaction's FIRST write, i.e. precisely the write that creates the hazard. `GetOrAdd`, not
   `TryGetValue`.
2. **Exempting the bind-time schema probe reproduced the original hang.** The reasoning — "a probe reads no
   rows, so row locks cannot block it" — is true of ROWS and false of METADATA: `WHERE 1 = 0` still needs
   Sch-S. The debug log showed the probe WAS the blocking query. It is now exempt from the data case only.

**⚠ INCOMPLETE IN THE SAFE DIRECTION**: a write issued through raw `fabricator_exec` names no table we can
see, so a scan of a table written that way is not refused and hangs as before. A missed path costs the old
behaviour, never a wrong refusal.

Gate: `verify_read_write_same_catalog` §7 (68 → 101), **mutation-tested** — disabling the tracking kills it
at the refusal assertion. ⚠ That section deliberately sets a finite `mssql_command_timeout`: a regression
here does not FAIL, it HANGS, which would stall the whole service tier instead of breaking it. The mutant run
confirmed the timeout turns it into a loud error rather than a stall.

### 5.6 What each configuration actually buys — the consolidated matrix

#### Terminology — THREE properties, not one, and they are independent

The columns below name properties that are easy to collapse into a single vague "read consistency". Keep them
apart; each is lost by a different mechanism and fixed by a different change.

| property | the question it answers | how we lose it |
|---|---|---|
| **read-your-writes** | is this read INSIDE the transaction at all? | the read goes to a pooled connection — a different session |
| **cross-statement read stability** | do successive statements share one view? | the level is statement-scoped, or there is no shared transaction to scope |
| **intra-statement consistency** | do all scans of ONE statement share one view? | one DuckDB statement issues SEVERAL server statements, and the level is statement-scoped (§5.7) |

**⚠ Read-your-writes is NOT an isolation property.** ANSI defines isolation by the anomalies it prevents
BETWEEN transactions (dirty read, non-repeatable read, phantom); seeing your OWN uncommitted changes is
assumed by every level, including READ UNCOMMITTED. It is a question here only because our reads can leave
the session — which is a property of our architecture, not of the database.

**⚠ They COMPOSE — measured, because the opposite is a natural assumption.** `SET TRANSACTION ISOLATION LEVEL
SNAPSHOT; BEGIN TRAN; SELECT count(*) …` ⇒ 2; `INSERT`; the same `SELECT` ⇒ **3**. A snapshot transaction
holds a transaction-scoped view of everything else AND still sees its own writes. Choosing transaction-scoped
consistency costs you nothing in read-your-writes.

**⚠ A READ-YOUR-WRITES FAILURE IS NOT SNAPSHOT'S WRITE-SKEW DRAWBACK — do not file it under "that is just
what snapshot does".** The two look alike (an anti-join `INSERT … WHERE NOT EXISTS` duplicating rows) and have
different causes. Missing a CONCURRENT transaction's committed rows is write skew: general to snapshot
isolation, accepted everywhere, answered by a unique constraint or SERIALIZABLE, and nothing here makes it
better or worse. Missing YOUR OWN rows is a MEMBERSHIP failure — the read ran in a different transaction —
and no isolation level has that behaviour. Measured both ways: `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`
inside the writing transaction reads **2 then 3** across its own insert, while a pooled read beside a pinned
writer returns **2** where the transaction had inserted a third row (**3** with MARS on, i.e. on the pinned
connection). So a read outside the transaction is STRICTLY WEAKER than any isolation level: it sees neither
its own writes nor concurrent commits.

**⚠ Our system couples the first two, and that coupling is OURS.** Both require the scan to run on the pinned
connection, so losing the pin loses both at once — which is why they read like one property in the matrix.
They are not, and a fix addresses them at different depths: routing a read onto the pinned connection buys
read-your-writes outright, and buys stability only if the LEVEL is also right (§5.4).

**⚠ Avoid the bare term "read consistency" in this repo.** Oracle uses it for the STATEMENT-level default, so
a reader can take it to mean the weaker guarantee we already have; SQL Server's own split is `SNAPSHOT` vs
`READ_COMMITTED_SNAPSHOT`, the same ambiguity. Qualify it every time.

The third property is the one no standard term covers well, and it is not academic: a statement's scans can
straddle a concurrent commit **while each individual read is correctly isolated**, because the isolation is
per SERVER statement and one DuckDB statement issues several. A single "read consistency" column would hide
exactly that. **MEASURED 2026-08-09 — §5.7. It is absent on box by default, in BOTH routings, and it is NOT
a property of the `materialize=false` opt-out** (an earlier version of this section framed it that way and
was wrong: a plain `SELECT` is never marked by `MaterializeOwnScans`, so the opt-out does not apply to it at
all).

One column per property named above, so the table answers the three questions separately. On the two
CONSISTENCY columns every cell carries **M** = measured or **d** = derived from the routing and NOT measured —
kept distinct because this section has been wrong twice by reasoning where it could have measured.

| # | configuration | scan runs on | the read's ISOLATION | streams | read-your-writes | consistent WITHIN one statement | consistent ACROSS statements | scans may OVERLAP |
|---|---|---|---|---|---|---|---|---|
| 1 | **no pin yet** — autocommit, or a txn that has not written. **The commonest read there is** | pooled | server default, per statement (READ COMMITTED on box) | ✅ | — (nothing written) | ❌ **M** | ❌ **M** | ✅ **M** |
| 2 | MARS on, ordinary scan, txn HAS written — **the box default from then on** | pinned | the pinned txn's ⇒ **server default** (READ COMMITTED on box) | ✅ | ✅ | ❌ **M** | ❌ **M** | ❌ |
| 3 | same-catalog read+write, `materialize=true` — **the default** | pinned, buffered | the pinned txn's ⇒ **SNAPSHOT on Fabric/Synapse-serverless**, server default on box | ❌ | ✅ | ❌ box / ✅ Fabric **d** | ❌ **d** | ❌ |
| 4 | same-catalog read+write, `materialize=false` | pooled | **SNAPSHOT**, set per scan | ✅ | ❌ | ✅ **d** | ❌ **d** | ✅ |
| 5 | MARS off, ordinary scan — **Fabric / Synapse** | pooled | the engine default (snapshot on Fabric) | ✅ | ❌ | **✅ M** (live on Fabric) | ❌ **M** | ✅ |
| 6 | `read_isolation 'snapshot'`, MARS on — **OPT-IN** (§5.8) | pinned | **SNAPSHOT**, transaction-scoped | ✅ | ✅ | **✅ M** | **✅ M** | ❌ |
| 7 | `read_isolation 'snapshot'`, MARS off — **OPT-IN** (§5.8) | pinned, buffered + serialized | **SNAPSHOT**, transaction-scoped | ❌ | ✅ | **✅ M** | **✅ M** (also live on **Fabric**) | ❌ |

**⚠ ROW 1 WAS MISSING FROM THIS TABLE UNTIL 2026-08-09, and it is the row most reads actually take.** The pin
is created lazily by the first WRITE, so a plain `SELECT` — in autocommit or in a transaction that has not
written yet — has no pinned connection to reuse and goes POOLED. The old row 2 was written as if "MARS on"
were sufficient for a pinned scan; MARS decides whether an EXISTING pin may be reused, not whether one
exists. Measured directly: the §5.7 statement logged `pooled txn=2` for both its scans in a read-only
transaction, and `pinned txn=2` only once an INSERT had run first.

**⚠ THE WITHIN-ONE-STATEMENT COLUMN TURNS ON WHETHER THE READ IS VERSIONED — NOT on pooled-vs-pinned, and an
earlier version of this table got three cells wrong by assuming otherwise.** It read: *"❌ for every default
row … rows 3–5 are marked d: same mechanism"*, the mechanism being "each scan takes its own connection, so
they take different snapshots". **Measured live on Fabric and FALSIFIED** (row 5, below). Two things have to
go wrong for a statement to be internally inconsistent, and a separate connection is neither of them:

1. **The read is NOT versioned** — plain READ COMMITTED without RCSI. A long scan then meets rows committed
   after it started, so it diverges from a short one *and can tear within itself*. This is what rows 1 and 2
   measure (6/6, three pooled and three pinned): pinning does not help, because MARS gives the scans one
   SESSION while each stays its own server STATEMENT.
2. **The scans take their snapshots at DIFFERENT instants.** Independent subqueries start in the SAME
   MILLISECOND (measured on both engines), so where reads ARE versioned the snapshots coincide and the
   statement is consistent — even across two connections. ⚠ It is a bound, not a guarantee: a plan that
   starts its scans at different times (hash-join build then probe) reopens it.

So row 5 is ✅, row 4 is ✅ (its per-scan `SET TRANSACTION ISOLATION LEVEL SNAPSHOT` is exactly condition 2
being satisfied — the very cell the old text singled out as obviously ❌), and row 3 splits by engine because
its isolation does.

**⚠ Rows 6 and 7 are ✅ WITHIN a statement in AUTOCOMMIT too**, which was not designed: DuckDB's autocommit
carries an implicit transaction with a nonzero id, so the opt-in pins there as well and every scan of the
statement lands in one transaction-scoped snapshot (§5.7, 3/3). That is the one route to intra-statement
consistency on box that does not require a DBA to enable RCSI.

**⚠ "PINNED @ SNAPSHOT" IS NOW SELECTABLE — rows 6 and 7, via the §5.8 opt-in. The paragraph below described
the state BEFORE it and is kept because it is still true of every DEFAULT configuration** — read row 3.
`ServerProfile.DefaultWriteIsolation` is `"snapshot"` for Fabric / Synapse-serverless and EMPTY everywhere
else, so a pinned scan inherits transaction-scoped snapshot isolation on those engines and the server default
(READ COMMITTED on box, READ UNCOMMITTED on Synapse-dedicated) on the rest. Nothing chooses it: the ATTACH
`isolation_level` option reaches table-in-out sessions only (§5.2). **On box the combination is unreachable
at all.**

**⚠ THE LAST COLUMN INVERTS THE INTUITION, and it is the reason to have the table.** MARS is *interleaved*
execution, not parallel — only one request runs on a session at a time (§5) — so the pinned configurations
put every scan of a statement through ONE server session. The configurations that give each scan its **own
connection**, and are therefore the only ones where two tables are read simultaneously, are row 1 (no pin —
the DEFAULT for a plain `SELECT`) plus the two usually described as degraded (no MARS, and the
`materialize=false` opt-out). Same shape as the 595 finding: *MARS is not what saves us there either.*

**⚠ And the last two columns are in TENSION with it, which is the trade the table exists to show.** Every row
that can read two tables simultaneously is ❌ on both consistency columns, and every row that is ✅ on them
serialises the scans — because both properties come from the same mechanism, putting the scans in ONE
transaction. Row 7 is the extreme: no MARS means the reads are drained *and* serialized, so consistency costs
both the streaming and the overlap. There is no row that is ✅ everywhere and, on these engines, there cannot
be one.

**Read that column as a CEILING, not a promise.** It says our routing PERMITS overlap; whether two scans
actually overlap is DuckDB's plan's decision — a hash join drains its build side before the probe side opens,
and `PhysicalUnion::BuildPipelines` may run branches sequentially. Orthogonal and fixed: a SINGLE scan is
never internally parallel, because our table function declares `MaxThreads() { return 1; }` (one Arrow C
stream is consumed serially).

**NO DEFAULT CONFIGURATION IS ✅ FOR EITHER CONSISTENCY COLUMN** — rows 1–5 are what you get without asking,
and every one of them is ❌ twice over. Only the opt-in rows 6 and 7 are ✅, and they are unset unless you ask
(§5.8); see §5.2 and §5.7 for the measurements and §5.4 for the cost.

**Two independent ways out of the WITHIN-a-statement ❌, and they are owned by different people.**
Database-level **RCSI** fixes it (6/6 trials, both routings) and is a DBA action this extension cannot take;
the **`read_isolation` opt-in** fixes it too (3/3) and needs no database change, at the price in §5.8. Neither
is a default. ⚠ And RCSI's guarantee is per scan-START instant rather than per statement, so a plan that
starts its scans at different times reopens the gap — the opt-in's transaction-scoped snapshot does not.

### 5.7 Intra-statement consistency — MEASURED 2026-08-09, and it is absent by default

**ONE DuckDB statement can report two different states of ONE table, and a single scan can see a FRACTION of
one committed transaction.** Measured on box SQL Server, `TestDB` at its rig defaults (RCSI **off**,
`ALLOW_SNAPSHOT_ISOLATION` on), MARS on, `mssql_materialize` at its default `true`.

Shape — `SELECT (SELECT count(a) FROM t) AS ca, (SELECT count(pad) FROM t) AS cpad` over a
`(a INT, pad CHAR(4000))` table of 150k rows, with a separate session committing 5000 rows ~3 s in:

| RCSI | scan routing | trials | ca vs cpad |
|---|---|---|---|
| off | `pooled` (no pin — autocommit, or a transaction that has not written) | 3 | **3/3 DIVERGE** |
| off | `pinned` (transaction wrote first) | 3 | **3/3 DIVERGE** |
| on | `pooled` | 3 | 3/3 agree |
| on | `pinned` | 3 | 3/3 agree |

**Why the shape is what it is, and why the obvious ones do not work.** The two scans start in the SAME
MILLISECOND and run concurrently (logged: two `SELECT [a]`/`SELECT [pad]` queries at one timestamp), so a
commit landing between two *equal* scans is not aimable. The asymmetric projection is what creates the
window: `count(a)` releases after **154 ms**, `count(pad)` after **12.4 s**, leaving a 12-second interval in
which the writer's commit is provably inside the statement. ⚠ **A self-join will not show this** — a hash
join drains its build side before the probe side opens (§5.3's trace), so there is no held window to inject
into. ⚠ And the two subqueries must project **different columns**, or DuckDB's common-subplan optimizer
dedups them into one scan and the measurement is vacuous (verified in `EXPLAIN`: two `FABRICATOR_SCAN` nodes).

**Three findings, in order of how surprising they are.**

1. **Pinning does not buy it, and I recorded the opposite from a single run before repeating.** The first
   pinned trial returned 150000/150000 and looked like a clean "the pinned session is consistent"; three
   trials then diverged 3/3. MARS is *interleaved-serial*, so both scans share one SESSION — but each is
   still its own server STATEMENT, and READ COMMITTED is scoped to the statement, not the transaction. **One
   green run of a timing-dependent race is not a measurement.**
2. **A single scan can see PART of one committed transaction.** Three of the six diverging trials picked up
   **2036**, **2376** and **3770** of the writer's 5000 rows — not the before state, not the after state, a
   torn one. That is ordinary non-RCSI READ COMMITTED behaviour (an allocation-order scan meets rows
   appended ahead of its position), but it means the anomaly is *within* a scan as well as *between* scans,
   so no amount of scan-to-scan coordination on our side would fix it.
3. **RCSI fixes it, 6/6, in both routings** — and it does so for a long scan too: the 12.4 s scan did not
   pick up a commit that landed 2.7 s into it. RCSI gives each server statement a snapshot at ITS start, and
   because our scans start together the per-statement snapshots coincide. ⚠ **Read that as a bound, not a
   guarantee**: the guarantee is per scan-start instant, so where a plan starts scans at different times
   (hash-join build then probe) the spread reopens. RCSI is a DATABASE option — nothing in this extension
   selects it, and `mssql_materialize=false` demands the *other* prerequisite (`ALLOW_SNAPSHOT_ISOLATION`),
   which does not cover this (§5.1).

**Controls, because every cell here could pass for the wrong reason.** With no concurrent writer the two
different projections return the SAME count (155000 = 155000) — so divergence is the writer's doing, not an
artifact of counting different columns. Under RCSI the writer's commits are proven to have landed by the
counts advancing exactly 5000 per trial (185000 → 210000, table total 215000 afterwards) **while each
statement returned the pre-insert value** — otherwise "both agree" would be indistinguishable from an
inserter that silently died, which is the failure this repo has already paid for twice.

**⚠ THE §5.8 OPT-IN ALSO FIXES THIS, and without RCSI — measured 3/3 after the fact.** It was built for
CROSS-statement stability, and this fell out of it: DuckDB's autocommit still carries an implicit transaction
with a nonzero id, so `mssql_read_isolation='snapshot'` pins there too and all of a statement's scans land in
ONE transaction-scoped snapshot. Same shape, same concurrent writer, RCSI **off**: `150000 150000` /
`155000 155000` / `160000 160000`, the counts advancing exactly 5000 per trial (final total 165000) so every
writer is proven to have committed. Compare 6/6 divergence without it. The cost is a BEGIN/COMMIT round trip
per autocommit statement — so RCSI remains the cheaper answer where a DBA can set it, and this is the answer
where they cannot.

**Scope — MEASURED on Fabric 2026-08-09, and it confirms the boundary rather than assuming it.** Fabric /
Synapse-serverless are snapshot-isolated by construction, so a statement's scans read versioned data with no
database option to set. Live on a Fabric Warehouse, the same shape that diverges 6/6 on box: **155000 /
155000**, agreeing.
- **The window is PROVEN, not assumed** — the writer reported server time either side of its INSERT
  (`22:37:22.66` → `22:37:23.70`) against the reader's own log (`SELECT [a]` released `22:37:11.2`, `[pad]`
  released `22:37:47.4`), so the commit landed **11.4 s after the fast scan finished and 24 s before the slow
  one did**. Control: the table went 155000 → **160000** while both reads returned 155000.
- Both scans logged `pooled txn=3` at the SAME MILLISECOND on two connections — so this is row 5 of §5.6's
  matrix exactly, with no opt-in in play.
- ⚠ **This FALSIFIED the reasoning the matrix used for three cells** (see §5.6): "separate connections ⇒
  separate snapshots ⇒ inconsistent" ignores that the snapshots are taken at the same instant. Where reads are
  versioned, two connections are fine; where they are not, one connection does not save you (row 2). **The
  axis is versioned-vs-not, not pooled-vs-pinned.**
- ⚠ Getting a usable window on Fabric took two attempts: at 30k × 4000 bytes the slow scan ran only 4.0 s
  against a ~10 s connect, so the writer could not land inside. 150k × 8000 bytes gives ~36 s, and the writer
  must be PRE-WARMED (attach, a trivial query, then `WAITFOR DELAY` to align) so its insert costs one round
  trip. ⚠ `sys.all_columns` cross joins are refused in Fabric's distributed mode (*15816*) — build the probe
  table with a DuckDB CTAS instead.

### 5.8 `mssql_read_isolation` — cross-statement read stability, OPT-IN (BUILT 2026-08-09)

`SET mssql_read_isolation='<level>'`, or per catalog `ATTACH … (TYPE fabricator, read_isolation '<level>')`.
**Unset by default, and that is a requirement rather than a starting position** — it holds a SQL Server
connection AND an open transaction for the whole DuckDB transaction's life (pool pressure under
`dbt --threads N`), and under `SNAPSHOT` a tempdb version store that grows for that duration. A long
analytical DuckDB transaction would impose a server cost it cannot impose today, so it is never inferred.

**MEASURED, same harness as §5.2, with a verified concurrent writer committing between the two reads:**

| `read_isolation` | first → second read | the writer |
|---|---|---|
| unset (default) | 3 → **4** | committed |
| `'snapshot'` | 4 → **4** | committed a fifth row — and the second read still said 4 |

The writer's own count is the control: it reported 5 while the transaction kept reading 4, so "both agree" is
not an inserter that quietly failed. Both reads log `pinned txn=2` where they logged `pooled` before.

**Measured on BOTH MARS states, because they take different routes** (streaming on the pinned connection vs
drained + serialized) and only one of them had numbers at first. With `mssql_mars='false'`: across statements
**3 → 3** while the writer took the table to 4; within one statement **3/3 identical** over the two-concurrent-
scan shape, the counts advancing exactly 5000 per trial (150000 → 160000, total 165000 afterwards) so every
writer is proven to have committed. That is what earns the **M** on both opt-in rows of §5.6's matrix — before
these runs, row 7's cells were derived from row 6's and the matrix said otherwise.

**What it changes, in the order it matters.**

- **(a) A READ now creates the pin.** `EnsureTxnConnection` is factored out of `BeginWrite` and called from
  `ExecuteQuery` too, so a transaction that has not written still has a server-side transaction for a level to
  apply to. This was the whole problem: on Fabric the ingredients (a pinned transaction at transaction-scoped
  SNAPSHOT) already existed and delivered nothing, because ordinary reads never routed onto them.
- **(b) ONE resolver picks the level** (`ResolveTxnIsolation`), used by the read pin and `BeginWrite` alike.
  ⚠ With two, the level would depend on whether a READ or a WRITE touched the catalog first — the transaction
  is opened once, by whichever came first, and silently keeps that level. Fallback stays
  `ServerProfile.DefaultWriteIsolation`, so Fabric is unchanged when the option is unset.
- **(c) On a no-MARS engine the reads are DRAINED and SERIALIZED.** Both halves are required and the second
  was found by measurement, not design: draining bounds how long a reader is open, but two scalar subqueries
  over one table start in the **same millisecond on two threads**, so the second `ExecuteReader` lands while
  the first is still draining — `The connection does not support MultipleActiveResultSets`. A per-transaction
  `ExecGate` serializes execute+drain. So on Fabric/Synapse the opt-in trades streaming multi-ref reads for
  consistency; they are mutually exclusive there.

**⚠ WHAT THAT DRAIN COSTS — MEASURED 2026-08-09, and it is the whole scanned result in memory with no
spill.** 2M rows × `(INT, CHAR(200))` = **389 MB** on the server, read inside a transaction with MARS off:
**peak process working set 83 MB streaming vs 471 MB buffered** (twice each; time barely moves, 3.8 s → 3.9 s).
The delta is **388 MB against a 389 MB result — one byte of working set per byte of result.** So on a no-MARS
engine the opt-in's memory ceiling is the largest single result the transaction reads, and there is no spill.
That is the same unbounded-buffer shape as the `materialize=true` default, now reachable for ordinary reads;
the `ColumnDataCollection` operator noted in CLAUDE.md's "Next up" is what would give it DuckDB's spilling.

The seam now carries a permanent `Fabricator.Memory` mark (`mssql scan: drained to memory`) — it was the only
heavy path with none, every other mark being DML or bulk. ⚠ **Its absence IS the streaming control**: the mark
fires only when `DrainToMemory` runs, so a run with the option off produces no line at all.

**⚠ Exempting a check must be paid for by guaranteeing the condition it checked for.** The opt-in makes
`EnsureScanCannotSelfBlock` (§5.5) stand down, because a read on the transaction's own connection cannot wait
on that transaction's locks. That is only true if the read really is pinned — so the routing sets `pinned`
DIRECTLY rather than going through the `materialize` gate three lines below it. Routed the other way, any
future change to that gate would send the scan POOLED with the check already disabled, which is limitation
1.15's **unbounded hang**, not an error. This also makes the opt-in a fourth remedy in 1.15's message.

**Refusals, both because the alternative is a silently wrong view.** A bad level fails the ATTACH, not the
first `SELECT` inside a transaction (by then the failing statement mentions nothing about isolation). And
`mssql_materialize=false` + `mssql_read_isolation` is refused: the first asks for a scan OUTSIDE the
transaction on a pooled connection, the second for every read INSIDE it — honouring either silently gives the
statement a view nobody asked for.

**⚠ `'snapshot'` is the level to ask for, and the two obvious alternatives are measured wrong** (§5.4):
`REPEATABLE READ` still permits the phantom a `count(*)` sees (3 → 4), and `SERIALIZABLE` works by BLOCKING
writers. On box `'snapshot'` needs `ALLOW_SNAPSHOT_ISOLATION`; we probe and name the `ALTER` — but ONLY when
the level came from this option, never when it came from the profile, or every Fabric write transaction would
gain a round trip on the one engine where snapshot is the only level there is.

**⚠ IT PINS IN AUTOCOMMIT TOO, which was NOT designed and is the better half of the deal.** DuckDB's
autocommit carries an implicit transaction with a nonzero id, so a bare `SELECT` under the opt-in logs
`pinned txn=6`. Consequence, measured after the fact: every scan of one statement lands in ONE
transaction-scoped snapshot, so **§5.7's intra-statement inconsistency is fixed as well, without RCSI** (3/3
identical where the same shape diverges 6/6). ⚠ A first version of the gate asserted the opposite ("autocommit
must not pin"), and its assertion could not tell the difference — only the Debug log could.

**The autocommit cost, MEASURED, because "it pins every statement" invites the wrong worry.** 500 trivial
autocommit reads: **2.17 s off vs 2.79 s on**, twice each ⇒ **+1.3 ms per statement (~+29%)**. That is the
WORST-CASE ratio — a `count(*)` on a one-row table is nearly all overhead, and any real query amortises it.
**And nothing accumulates: 3 SQL Server sessions after 400 statements, identical at 50**, so ADO.NET pooling
reuses the connection and each statement's transaction is committed and released. The pool-pressure warning
in this section is about a LONG-LIVED explicit transaction holding one connection, NOT about autocommit
leaking them.

**✅ IT WORKS ON FABRIC — MEASURED LIVE 2026-08-09, and this closes the last open question in §5.2/§5.4.**
Fabric Warehouse, `Test Warehouse`, service principal: control **3 → 4**; with `read_isolation 'snapshot'`
**4 → 4** while a second process committed a fifth row, confirmed afterwards (the table held **5** while both
reads returned 4). Both reads logged `pinned txn=3` — two reads on ONE Fabric connection, which our routing
could not produce before this option existed. So Fabric's documented transaction-scoped SNAPSHOT does deliver
through our routing, and row 7 of §5.6's matrix is measured on the real engine and not only on box with MARS
forced off.
- ⚠ **The window has to be long there.** A Fabric connect costs seconds (SP token + endpoint), so a writer
  fired at t+8 s needs a wide window to land inside; a 40-second `WAITFOR DELAY` was used. **`WAITFOR DELAY`
  IS supported on Fabric Warehouse** — worth knowing, since much of the T-SQL surface is not.
- ⚠ **`read_isolation` is what makes the reads PIN there; the level itself was already right.** Fabric's
  `ServerProfile.DefaultWriteIsolation` is already `snapshot`, so the option's VALUE changes nothing on that
  engine — its whole effect is (a), routing the reads into the transaction. That is the clearest available
  evidence that (a) was the substance and (b) was bookkeeping.

Gate: `test/verify_read_isolation.test` (47, service tier). ⚠ The headline property is NOT in it — proving it
needs a concurrent writer and sqllogictest drives connections sequentially. What the suite pins is the
ROUTING, through a sharp observable: with MARS off, reading a self-written table is refused unless the read
joined the transaction. Mutation-tested with three mutants, each killed at its own section; the third (drop
the `ExecGate`) survives **40 assertions** before the concurrent-scan section catches it.

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
- **`fabricator` — two active result sets at once.** SqlClient + DuckDB's pull-based execution keep a
  `SqlDataReader` open across many `get_next` calls, and we route that read *and* the transaction's DML
  through the one pinned connection (read-your-writes) → an open reader and a DML command live on the same
  connection → MARS.

Net: the native extension's serialized model can't interleave a still-open streaming scan reader with DML
on the pinned connection; our streaming-reader-plus-DML pattern requires MARS to make that legal. Same
pinned-connection transaction shape, opposite answer on MARS.

## 6. `INSERT INTO xx SELECT … FROM y` — pin timing is a race (and harmless)

INSERT uses the streaming bulk path (`begin_bulk` / `push_batch` / `complete_bulk`,
`dotnet/Fabricator.Bridge/BulkSession.cs`). The pin is **deferred to a background task**: the `BulkSession`
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

`SqlServerProcEach` (the proc `_each` exchange binding, `dotnet/Fabricator.SqlServer/SqlServerProcEach.cs`)
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
