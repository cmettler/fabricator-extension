# Delta provider — transaction, concurrency & isolation semantics

Reference for the **engineered-wood Delta provider** (`PROVIDER 'engineeredwooddelta'`, or its one
alias `'delta'` — the redundant `'deltalake'` was removed 2026-07-29): how DuckDB transactions map
onto Delta commits under every combination of
execution mode (autocommit vs explicit `BEGIN..COMMIT`), data path (EW codec vs `native_read` /
`native_write`), and the `isolation_level` ATTACH option. The `deltars` provider is NOT covered —
see the short note at the end.

Design history lives in CLAUDE.md ("EXPLICIT TRANSACTIONS — SNAPSHOT-ISOLATED, BUFFERED" + the
eager-write slices). This doc is the consolidated *semantics* reference. Gate test:
`test/verify_delta_catalog_transactions.test`.

---

## 1. The two execution modes

The host tells the provider which mode a transaction is in via the ABI v60
`begin_transaction(is_explicit)` flag (C++ reads `context.transaction.IsAutoCommit()`):

| | **Autocommit** (bare statement) | **Explicit** (`BEGIN … COMMIT/ROLLBACK`) |
|---|---|---|
| Delta commits | one commit per statement | **one fused commit per table** at `COMMIT` |
| DELETE/UPDATE | direct per-statement paths (DV delete / merge-on-read / copy-on-write, CDF capture inline) | **buffered** (positions parked, applied at flush) |
| Appends (INSERT/CTAS) | buffered per (txn, table), flushed at statement end — byte-identical to a direct commit | buffered, fused into the COMMIT |
| Reads | statement-level snapshot | transaction-level snapshot (see §3) |
| ROLLBACK | n/a | discards the buffer; nothing reaches the `_delta_log` |

`BEGIN; <one statement>; COMMIT` is an **explicit** transaction (the flag comes from DuckDB's
autocommit detection, not statement counting) — it takes the buffered paths, semantically
equivalent to autocommit for a single statement.

**Important divergence from file-COPY semantics:** `COPY … TO '<path>' (FORMAT delta)` (the
path-targeted, no-ATTACH form) drives its own transient catalog and commits at COPY finalize — it
is its own atomic Delta commit and deliberately does **not** roll back with a surrounding DuckDB
`BEGIN`.

---

## 2. The three path settings — what changes transactionally

ATTACH options `native_read` / `native_write` select the *byte source* for data files; they do
**not** change transaction semantics. **Their defaults come from the PROVIDER NAME** (since
2026-07-29): `PROVIDER 'delta'` defaults **both on** (the native hybrid — the production path),
`PROVIDER 'engineeredwooddelta'` defaults **both off** (the pure-EW codec path). Either option can
still be set explicitly on any spelling, so `PROVIDER 'delta', native_write false` is the codec
writer under the default name. The transaction machinery (buffer, pin, flush, rebase) is identical
whichever way the flags land; only the mechanics of "get the rows onto storage / off storage"
differ:

| | **EW codec** (both off) | **`native_write`** | **`native_read`** |
|---|---|---|---|
| Statement-time data files | eager-written via EW's parquet writer (`TryEagerWriteBatches`), *explicit txns only*; autocommit parks batches (flushed at statement end anyway) | **streamed** via DuckDB's `COPY` (bounded memory) in all modes; files parked as `WrittenDataFile` | n/a (read side) |
| Mid-txn read-your-writes | `ScanCodec` overlay (§5) | pending files served through whichever read path the catalog has | pending files appended to the per-file `read_parquet` loop; pending deletes merged into each file's DV exclusion |
| Snapshot pin honored by | `StreamAt`/`StreamWithRowIdsAt` (codec reader) | — | per-file loop resolves the pinned version |

The **eager-write invariant** holds on every path: by the end of a buffered statement, its data
rows exist as parquet files on storage (or, in the few fallback cases below, as in-memory batches),
and the `DeltaTxnBuffer` holds only *actions and positions*. Memory in an explicit transaction caps
at roughly one statement.

**Remaining batch-park fallbacks** (by design, collect instead of eager file): IcebergCompat
tables, identity appends **under a pending ALTER**, and the autocommit codec path (semantics-
identical, flushes immediately). Everything else — CTAS, partitioned CTAS, inserts under a buffered
ALTER, UPDATE post-images, identity appends, row-tracking appends — writes files at
statement time.

---

## 3. Read semantics (snapshot isolation)

### Explicit transactions
The **first scan** in the transaction captures one UTC instant (`SnapshotPinning`, keyed by the
DuckDB `global_transaction_id`); each table resolves that instant to a version on first touch
(`PinnedVersion`). Every subsequent scan in the transaction — codec and native alike — reads that
consistent cut. This is the MVCC snapshot-at-first-read shape (Postgres REPEATABLE READ): a
concurrent commit is **invisible mid-transaction**; capturing at literal `BEGIN` is impossible
because catalogs are touched lazily.

- Pending-created tables are excluded from pinning (nothing on storage yet).
- Explicit `AT (VERSION/TIMESTAMP)` time travel overrides the pin **and excludes pending changes**.
- Under `isolation_level 'serializable'` the first *read* of a table also pins the transaction's
  commit base (so an append-only transaction that read the table routes through the checked flush,
  §6).

### Autocommit
- **Codec path:** the statement's FIRST scan pins the version it opened at, and every later reference
  to that table in the same statement reads AT it. The pin is seeded from that scan's own schema open
  (`GetSchemaAndVersion`), so it costs no extra `_delta_log` read; `ScanTable` then consults it before
  fetching the schema, so schema and data come from one version even if a concurrent ALTER lands.
  ⚠ **This was NOT true before 2026-07-29** — the pin was consulted only inside an explicit
  transaction, because it had been written inside the explicit-only read-set block below (a gate that
  exists for the buffered-DML *write* capability boundary and has nothing to do with reads). The
  earlier claim here, "a single statement is one snapshot by construction (one open of the table)",
  was false in both halves: a statement opens the table **twice per reference** (a bind-time schema
  probe + the execution; measured), each at latest, so a statement naming one table more than once —
  a self-join, `t UNION ALL t`, a correlated re-scan, `INSERT INTO t … FROM t JOIN t` — could read
  two different versions if a writer committed in between. Pinned by
  `verify_delta_autocommit_pin`, whose assertion is that exactly ONE pin is established per
  (statement, table) however many times the statement names it.
- **Native path:** `SnapshotPinning` fires per statement, giving a consistent cross-table cut for
  multi-table joins within the statement. Unchanged — it always pinned in autocommit, resolving the
  transaction's instant through `ResolveVersionAsOf` (which costs its own open; the codec seeding
  above deliberately avoids that helper).

### Read-set tracking (feeds conflict detection, §6)
In explicit transactions, `ScanTable` records each scan's **pushed predicate** (the built EW
`Predicate` — a superset of the rows consumed, since unpushed residue filters above the scan), or a
whole-table flag when nothing pushed, onto the buffer (`ReadPredicates` / `ReadWholeTable`).
Bind-time schema probes (`spec == null`) are excluded. Read-only entries never trip the
pending-changes guards.

---

## 4. Write semantics per statement kind

What each statement does inside an **explicit** transaction (autocommit = the direct per-statement
equivalent):

| Statement | Buffered behavior | Notes / requirements |
|---|---|---|
| INSERT / CTAS append | file(s) written eagerly at statement time; `add`s parked | streams under `native_write`; codec eager via `TryEagerWriteBatches` |
| CREATE TABLE / CTAS (fresh) | ⚠ the CREATE is **IMMEDIATE** since hoist slice 5 — commit-0 (`protocol`+`metaData`, an empty table) lands at statement time and is visible to other sessions for the transaction's life; only the DATA waits for COMMIT. A ROLLBACK drops the table best-effort | that immediacy is what buys ALTER + DELETE on a table your own transaction created (both used to throw). CTAS data still streams to the table folder; partitioned CTAS streams Hive layout. §7.1 + [delta-transaction-hoist.md](delta-transaction-hoist.md) §3 |
| CREATE + identity marker | buffered; INSERTs generate real ids at statement time from the parked schema, marks chain; flush bakes final HWM into commit-0 | |
| DELETE | rowids decoded to (pinned ordinal → positions), parked in `DeletedByOrdinal`; flush emits DV remove/add pairs | requires **deletion vectors enabled** on the table |
| DELETE of rows inserted in the same txn | works — positions keyed on the pending file's ordinal (≥ `0x780000`); flush builds an **inline DV born on our own add** | in-memory batch rows (`0x700000..0x77ffff`) rejected |
| UPDATE | old rows parked as delete positions + post-image rows **eagerly written** as a file; read-back is version-pinned (`atVersion: PinnedVersion`) | requires DV + `SupportsExternalCommit` (not identity/IcebergCompat); on row-tracking tables the post-images bake the ORIGINAL stable ids (materialization is implied by row tracking; partitioned works) |
| UPDATE of same-txn rows | **rejected** ("COMMIT the inserts first") | |
| MERGE INTO | each action runs its own operator (rowid UPDATE / DELETE + bulk INSERT) and parks exactly as the standalone statements do, so the flush **fuses them into ONE commit**. ⚠ A merge carrying **>= 2 `UPDATE`/`DELETE` actions is FORCED to buffer even in AUTOCOMMIT** — `PlanMergeInto` marks the statement's transaction buffered at execution time | Requires a **rowid**; `RETURNING` rejected. **The forcing is a CORRECTNESS requirement, not atomicity polish:** those actions address rows located by ONE join scan, and while they committed separately a copy-on-write DELETE renumbered the rows the other had already addressed — measured DESTROYING a row on a one-file non-DV table ([known-limitations.md](known-limitations.md) 1.13). Consequence: such a merge needs **deletion vectors** and is refused without them. ⚠ The count excludes `INSERT` (it addresses no existing rows and commits last), so a merge with at most ONE `UPDATE`/`DELETE` — including the common `UPDATE` + `INSERT` — keeps the direct path and still works on a non-DV table. The same-transaction guards do not bite: matched/not-matched are disjoint AND those guards key on the ordinal's pending-vs-committed range |
| ALTER ADD/RENAME/DROP COLUMN, nested ADD/DROP FIELD | metaData (+ protocol upgrade) computed at statement time, parked; joins the fused commit; overlays serve the pending schema mid-txn | ALTERs must precede the txn's data changes; nested RENAME FIELD stays immediate; RENAME of a partition column rejected in a txn |
| RENAME TABLE | immediate (physical folder move) — **except** a pending-created table, which re-keys the buffer + moves the eagerly-streamed files (the dbt tmp-swap case) | |
| CREATE OR REPLACE, DROP, OPTIMIZE, VACUUM | **immediate**, never buffered; rejected with "uncommitted buffered changes" if the table has pending changes | replace removes are snapshot-coupled; DROP/VACUUM are physical |
| CDF tables (any DML/append) | the statement's `_change_data` files are written eagerly (split per partition, partition columns excluded from the bytes) and the cdc actions parked — **including plain inserts** (a commit carrying any cdc action is read cdc-only, so appends fused with DML would otherwise vanish from the feed) | DML-after-buffered-ALTER on CDF rejected; identity×CDF in a txn rejected |
| `delta.set_transaction_version(…)` | parks the app-transaction version; flush CASes it against the latest snapshot and emits the spec `txn` action atomically with the fused commit (idempotent-append protection) | requires an explicit transaction |

A statement error **aborts the whole DuckDB transaction** (standard DuckDB behavior); the buffer is
discarded on rollback.

### Guard summary (clean errors, autocommit is always the escape hatch)

From `EnsureBufferedDmlEligible` + the buffering gates:

- Buffered DELETE/UPDATE ⇒ table must have **deletion vectors** (else: autocommit copy-on-write).
- Buffered UPDATE ⇒ additionally not identity/IcebergCompat.
- CDF + identity/IcebergCompat ⇒ buffered DML rejected (partitioned CDF works).
- DML on a CDF table after a buffered ALTER ⇒ rejected (cdc files would be pre-ALTER-shaped).
- DML/ALTER on a **pending-created** table ⇒ rejected ("COMMIT the CREATE first"); a later INSERT
  into it is fine (it joins the create's single WRITE).
- The provider **never silently degrades to non-atomic behavior** — anything it can't buffer
  correctly errors with an instruction, it does not fall back to an immediate commit inside your
  transaction.

---

## 5. Read-your-writes overlays and the rowid/ordinal contract

Every transient rowid is `(fileOrdinal << 40) | positionInFile`, ordinals resolved against the
**pinned snapshot's path-sorted active set**. Three disjoint ordinal ranges carry the overlay
routing:

| Range | Meaning |
|---|---|
| `< 0x700000` | committed file (position in the pinned snapshot's `OrderedActiveFiles`) |
| `0x700000 … 0x77ffff` | in-memory pending **batch** rows (the few park fallbacks) |
| `0x780000 + i` | eagerly-written pending **file** `pending.Files[i]` |

- **Codec path:** `ScanCodec` is the single virtual-table read — pinned base stream ⊕
  pending-delete exclusion (rowid stream forced when needed) ⊕ pending-ALTER schema reconcile ⊕
  pending-batch overlay; every step conditional, so a no-pending scan passes straight through.
- **Native path:** pending files are appended to the per-file loop (ordinals `0x780000+`), pending
  deletes merge into each file's DV exclusion (`WithPendingDeletes` matches by ordinal — the same
  mechanism serves committed DVs and same-txn deletes), the buffer pin overrides `SnapshotPinning`.
- **Pending CREATE:** `ScanPendingCreated` serves the table entirely from the buffer (pending files
  via host `read_parquet` + mapping rename + partition literals; batches via `ProjectStream`).

The composition **is** the virtual table. A "synthetic EW Snapshot" (pinned ⊕ pending actions as a
real `Snapshot`) was evaluated and rejected: EW path-sorts the whole active set, so uuid-named
pending files would interleave into the committed ordinal range and break this contract (recorded
on `ScanCodec`'s doc comment).

---

## 6. COMMIT: flush, rebase, conflict detection

At `COMMIT`, each touched table flushes as **one Delta commit** (Delta has no cross-table
atomicity — multi-table transactions become one commit *per table*, applied sequentially; a
conflict on table N aborts the DuckDB transaction, tables 1..N−1 already committed — the standard
Spark/Delta reality).

Flush mechanics: remaining batches → `WriteDataFilesAsync`; parked delete positions split at
`0x780000` (committed ordinals → pinned-snapshot DV pairs via
`ComputeDeletionVectorActionsAsync(resolveAgainst: pinned)`, pending ordinals → inline DVs born on
our own adds); extra actions (metaData/protocol from buffered ALTERs, cdc actions, app-`txn`
actions) join the single `CommitDataFilesAsync(expectedVersion: …)`. commitInfo operation = the
statement kind when single-kind (WRITE/DELETE/UPDATE/"ADD COLUMNS"/…), `TRANSACTION` when mixed.

### Conflict detection = Spark ConflictChecker parity (file/action-level) + row-level DV reconciliation

Under `write_serializable`, before the checks below, DML deletion-vector pairs are REBASED onto the
latest snapshot (`RebaseDvDmlActionsAsync`): a concurrent DV swap of the same file re-unions when the
touched rows are disjoint, and a concurrent REWRITE (OPTIMIZE / copy-on-write) of a touched file
REMAPS the rows onto the new files by stable row id (`RemapRowsAcrossRewriteAsync`, row-level
concurrency §10.4) — so the delete/delete check then passes naturally, and the read-set checks are
fully skipped (`rowLevelDml` — the row-level write validation replaces them; same-row overlap or a
concurrently updated/deleted target row throws the row-level conflict). Under `serializable` no
rebase happens — everything below applies strictly to the pinned-resolved actions.

When the table moved past `PinnedVersion`, `CheckLogicalRebaseAsync` walks the concurrent commits
`pinned+1 … latest` and runs four checks. Row-tracking ids play **no** role in detection — the
conflict unit is the log action and the **(path, deletionVector) pair**:

1. **metadataChangedCheck** — any concurrent `metaData` (schema/partitioning/config) aborts.
   Buffered ALTERs are chained against the pinned metadata. This is also what makes concurrent
   **identity** consumption safe for free: an identity-consuming commit necessarily carries
   `metaData` (the new high-water mark) and trips this check.
2. **protocolChangedCheck** — concurrent protocol change aborts.
3. **concurrentDeleteDeleteCheck** — any planned `RemoveFile` whose (path, DV) is no longer active
   *unchanged* aborts: catches concurrent DELETE/UPDATE/OPTIMIZE of a file we modify. Two
   transactions DV-deleting *different rows of the same file* still conflict (the first commit
   swapped the file's DV) — deliberately coarser than row-level.
4. **Read-set checks** (from §3's recorded predicates):
   - *concurrentDeleteRead* — a concurrent **data-changing** remove of a file our reads consumed
     aborts.
   - *concurrentAppend* — a concurrent **data-changing** add matching our read predicates
     (`DeltaFilePruner.ShouldInclude` over the pinned schema — partition values exact, stats
     conservative/superset-safe) aborts. From non-blind-append commits always; from **blind
     appends** only under `serializable`.

   `dataChange=false` actions (OPTIMIZE) are exempt from the read checks (rows unchanged); a
   compaction of a file we *modify* still hits delete/delete.

If every check passes, the concurrent commits **commute** and the flush rebases on top: DV
ordinals/old DVs resolve against the *pinned* snapshot, remove+add pairs stay valid (check 3 just
proved our files unchanged), and row-id / identity high-water marks **re-derive from the snapshot
committed onto** (fresh `baseRowId`s above the latest HWM — assignment during rebase, not
detection). The commit runs in a bounded reopen+revalidate retry loop against `expectedVersion`
(a writer landing mid-flush).

A real conflict surfaces as: `transaction conflict … the concurrent changes do not commute` — the
DuckDB transaction aborts; retry the whole transaction.

### `isolation_level` ATTACH option

| | `'write_serializable'` (*Databricks'* default; ours until 2026-08-01 — §10.6) | `'serializable'` (**our default since 2026-08-01** — Fabric Spark's too) |
|---|---|---|
| Concurrent **blind appends** vs our reads | commute (feed inference/order may place them "before" our reads logically) | abort if they match our read predicates — commit order = logical order |
| Append-only txn that *read* the table | blind OCC path (no read checks) — documented divergence: Spark would deleteRead-check it | first read pins the base version → routes through the checked flush |
| Everything else | identical (all four checks apply to read-write transactions in both levels) | |

### Autocommit concurrency (no buffer, per-statement OCC)

- **Appends** (INSERT/CTAS/COPY): snapshot-independent → on `DeltaConflictException` the writer
  reopens at the new latest and retries, bounded `MaxCommitAttempts = 16`.
- **Identity appends**: the retry *regenerates* values from the fresh snapshot's HWM
  (regenerate-retry — a liveness trade vs the explicit-txn abort policy).
- **Rowid DELETE/UPDATE**: **no retry** — the positions are tied to the scanned snapshot; a
  concurrent change surfaces as a clear "concurrent modification — retry the statement" error.

---

## 7. ROLLBACK

`ROLLBACK` (or a statement error aborting the transaction) **discards the buffer**. Because data
files were written eagerly, they remain on storage as **invisible orphans** — never referenced by
any `_delta_log` entry (a rolled-back CTAS leaves parquet in a `_delta_log`-less folder, which is
not a table to any reader; a same-name re-create works). Cleanup is `VACUUM`'s job — the exact
shape of Spark's OptimisticTransaction rollback.

**⚠⚠ NARROWED TWICE ON 2026-08-02, AND THE SECOND TIME LEAVES ALMOST NOTHING OF IT.** The paragraph above
is now HISTORICAL: since EW #52, `ROLLBACK` also **reclaims the eagerly-written DATA files** — the class the
first narrowing explicitly could not touch. What stays true of it: not committing IS the rollback, atomically
and for free; the reclamation is about the BYTES, which used to sit on storage until VACUUM's retention
horizon, billed the whole time.
- **It had to be a VERB, not a disposal.** `WriteDataFilesAsync` returns a plain list and keeps no handle ON
  PURPOSE — a write may outlive its call and be committed by a later, unrelated one, so only the HOST knows
  the commit is not coming. Ours does: `DeltaCatalog.RollbackTransaction` hands EW exactly the
  `pending.Files` it wrote.
- **⚠ It needed a C++ fix first, and that is the interesting part.**
  `FabricatorTransactionManager::RollbackTransaction` **never called `SetActiveOpener`** — unlike the commit
  path directly above it. Harmless while rollback did no IO, but `AmbientOpener.Current` there held whatever
  an earlier call left: a **stale `ClientContext*`**, which this repo already records as a use-after-free
  rather than staleness. Rollback now takes its OWN short-lived connection + opener, for the same reason
  commit does — deleting through DuckDB's FileSystem resolves SECRETs and the secret manager demands an
  active transaction. Unlike `CommitTransaction`, this override is handed **no `ClientContext`**, so there is
  nothing to restore to and the opener is cleared to `0` BEFORE the connection dies.
- **Never throws.** Rollback is already the failure path; a cleanup error must not replace the user's real
  one, and a failed discard logs and leaves the orphan — exactly the old behaviour, so strictly no worse.
  The throw it most plausibly swallows is EW REFUSING a file the table references (checked against a freshly
  read log, validate-then-apply). Ours are uncommitted by construction, but the immediate-by-design
  operations (identity creates, CREATE OR REPLACE, partition overwrite) commit INSIDE the transaction, so
  treating "referenced" as impossible would be an assumption about a list we do not fully own.
- **Gate**: `verify_delta_catalog_transactions` 943 → **944**, mutation-tested. ⚠ That section had asserted
  the parquet count only BEFORE the rollback for a year, so the behaviour could change under it in silence
  and its comment ("stays as an invisible orphan") went false without a single test noticing. The
  before-count is now documented as the positive control: a post-rollback count of 4 is also what a
  statement that never wrote its post-image would leave. Hermetic floor 5654 → 5656 (+2 — doubled suite).

The FIRST narrowing follows, kept because the provenance rule it describes is still what separates the two
mechanisms: EW's ledger collects what EW's OWN writers made, `DiscardDataFilesAsync` collects what the HOST
names. The statement above is still exactly true of the eagerly-written **data** files. It is no longer true of what
engineered-wood's own writers put on storage during the FLUSH — above all the **deletion vector a
buffered DELETE stages**, since `StageRowDeletesAsync` writes the vector before the commit is judged.
The flush's transaction is now `await using`, so a flush that does not commit takes those back (EW
#46's `WrittenFileLedger` + #49). EW's provenance rule never collects a host-written file, which is
what keeps the two halves apart — our data files are written before that transaction exists.
- **MEASURED both ways**, because the obvious probe is void: a *small* delete stores its vector
  INLINE in the commit json (below a 1 KB roaring-bitmap threshold), so there is no file to leak and
  the orphan does not reproduce. At 500k rows it does — a stray `deletion_vector_*.bin` that
  survived the refused commit forever. Gate: `verify_delta_txn_version` §9 (51 → 65), whose delete is
  sized above the threshold on purpose and which asserts ZERO vectors *before* the refused flush so a
  later zero cannot be read as "never written".
- **⚠ Do not backport the `await using` past EW #49.** #46 introduced the ledger, but `CommitOccAsync`
  refreshed the snapshot AFTER the commit json was durable and inside the same `try`, so a commit that
  LANDED and then threw still named its live files — disposal would have deleted committed data. #49
  empties the ledger the instant `WriteCommitAsync` returns.

Not undone by ROLLBACK (because they were immediate): DROP, OPTIMIZE/VACUUM, CREATE OR REPLACE,
RENAME TABLE of a *committed* table, nested RENAME FIELD, and the path-targeted `COPY (FORMAT
delta)`. The C++ side calls `InvalidateAllEntries()` on rollback so no stale schema survives a
rolled-back ALTER.

### 7.1 ⚠ A CREATE-plus-data lands as TWO versions, not one — in a TRANSACTION **and IN AUTOCOMMIT** — MEASURED 2026-08-03, scope corrected 2026-08-04

Recorded because it existed only as an inline comment (*"v0 create + v1 write — today's flush shape"*,
`DeltaCatalog.cs`) and nowhere else. Surfaced by a design question — *why can't a `DeltaTransaction` contain the
CREATE?* — which is the right question, because the Delta protocol permits exactly that: **version 0 may carry
`protocol` + `metaData` + `add` actions in one commit.**

Measured (`BEGIN; CREATE TABLE t1(…); INSERT … 100 rows; COMMIT;` on a local root):

```
_delta_log/00000000000000000000.json  →  commitInfo, protocol, metaData      ← no `add`: an EMPTY table
_delta_log/00000000000000000001.json  →  the 100 rows
```

`FlushCreateTransactionAsync` calls `DeltaWriter.Create(...)` **unconditionally** and only then writes the data, so
both of its branches produce two versions — the eager-CTAS branch via `CommitDataFilesAsync`, the parked-batches
branch via `DeltaWriter.Write`.

**⚠ SCOPE CORRECTION (2026-08-04): this is NOT a buffered-transaction property, and describing it as one — which
this section did — hid the common case.** A plain **autocommit `CREATE TABLE … AS SELECT`** produces the identical
two commits, by a different path: `DeltaWriter.WriteAsync` calls `OpenOrCreateAsync` (which commits v0 for a new
table) before `table.WriteAsync` (v1). Measured the same way:

```
00000000000000000000.json  →  commitInfo operation=CREATE TABLE, protocol, metaData   ← an EMPTY table
00000000000000000001.json  →  commitInfo operation=WRITE, add numRecords=5, domainMetadata
```

So the consequence table below applies to EVERY create-with-data, and the statement it most often applies to is a
one-line CTAS with no `BEGIN` in sight.

**What protects you today: every REACHABLE failure fires BEFORE v0, and that is structural rather than luck.** The
Arrow→Delta **schema conversion is a precondition of the create** — `OpenOrCreateAsync` cannot be called without a
Delta schema — so a schema-level rejection necessarily precedes it. Two measured, both leaving NO table behind: a
`TIMESTAMP_NS` column (refused at `complete_bulk`) and an `INTERVAL` column (*"Cannot convert Arrow type interval to
Delta type"*). What remains exposed is a failure of the DATA write or its commit — storage error, permission, disk
full, network — for which there is no compensation (`WriteAsync`'s `finally` only disposes the table; a commit
CONFLICT is handled by the retry loop, other failures are not). Still not measured.

**⚠ AND RE-RUNNING USED NOT TO RECOVER — the shared C++ layer NEVER CHECKED `ERROR_ON_CONFLICT`. FIXED
2026-08-04.** `FabricatorSchemaEntry::CreateTable` handled `REPLACE_ON_CONFLICT` (drops first) and
`IGNORE_ON_CONFLICT` (forwards `if_not_exists`) and passed everything else straight through, so a plain create
reached the provider as an ordinary create and Delta's `OpenOrCreateAsync` simply OPENED the existing table. Two
shapes therefore succeeded while doing nothing, both measured with a positive control (the same statements over
DuckDB's own in-memory table error correctly):

- `CREATE TABLE t AS SELECT …` — over a 10-row table, `… AS SELECT range(2)` left **10 rows**, exit 0, no error.
  So the orphan above was recoverable only via `OR REPLACE`/DROP, and — independently of orphans — a user
  re-running a CTAS believing it had replaced the data silently kept the OLD data.
- `CREATE TABLE t (a INTEGER, b VARCHAR)` — no error, and the **declared schema silently ignored**; the table
  kept its original columns. ⚠ This half was missing from the first write-up. It appeared only from running both
  shapes rather than reasoning about the CTAS one — the same failure mode as the `mode = Overwrite` correction
  below, one paragraph apart.

Now refused with DuckDB's own `CatalogException::EntryAlreadyExists`, so the message *and* its structured
`ENTRY_ALREADY_EXISTS` extra-info match every other DuckDB catalog. `OR REPLACE` and `IF NOT EXISTS` unchanged.
Gates `verify_delta_catalog_write` (+12, engine-doubled) and `verify_ctas_text_type` (+8), both mutation-tested.

The existence oracle is `GetOrCreateEntry` rather than a bare `table_types_` lookup, because a table can exist
without appearing in the discovered name list: an ATTACH `table_filter` bounds ENUMERATION only, and that path
fetches by name. The gate pins this by raising its conflict against a table that exists on storage and has not
been read through the attach. It is also the call the successful create already makes, so materialization is paid
only on the conflict path.

**⚠ THE MECHANISM IS NOT WHAT IT LOOKS LIKE, and the two symptoms have DIFFERENT OWNERS.** `PhysicalPlanGenerator::CreatePlan(LogicalCreateTable &)` (`duckdb/src/execution/physical_plan/plan_create_table.cpp:37`) probes for an existing entry and, when one is found and the conflict action is not REPLACE, routes the statement to a bare `PhysicalCreateTable` — **discarding the child plan, i.e. the SELECT.** Proven directly: `EXPLAIN CREATE TABLE IF NOT EXISTS m AS SELECT * FROM range(1000000)` over an existing table prints a physical plan of `CREATE_TABLE` alone, with no scan in it. So **"no rows written" was DuckDB's plan downgrade, not the provider swallowing a write** — the write was never planned. Only "no error" was ours: `PhysicalCreateTable` calls `schema.CreateTable(...)`, which is the check that was missing.

Two consequences worth keeping. **`mode = Overwrite` was never even reached in the broken shape** — `overwrite = createTable || replace` (`DeltaCatalog.cs:2039`) lives on the `begin_bulk` path under `FabricatorPhysicalCreateTableAs`, and the downgrade bypasses that operator entirely; so it is not merely "correct given DuckDB should have rejected first", it is off the path. And **one check covers BOTH the plain CREATE and the CTAS** not by luck but by DuckDB's design: it delegates the conflict decision to the catalog and funnels both spellings into the operator that asks the catalog.

**⚠ THE SCOPE QUESTION IS SETTLED AND THE ANSWER IS NOT UNIFORM** (this section previously recorded it as
UNVERIFIED). **SQL Server was never in the dangerous half** — its own `CREATE TABLE` rejects a duplicate, so no
write was ever lost; the user merely got the raw provider error (`2714: There is already an object named …`),
which reads as a SQL Server problem rather than the ordinary catalog conflict. **DAX is structurally exempt** (it
refuses CREATE outright). So the silent data-keeping was **Delta-only** while the confusing message was
**shared**; one fix covers both, and the gate spans both tiers because they share the code path, not because
they shared the symptom.


**⚠ THE ORPHAN RISK IS UNCONDITIONAL ONCE v0 LANDS — ordering changes its PROBABILITY, not its existence, and
we do NOT compensate.** Both paths put the create OUTSIDE the guarded region: autocommit calls
`DeltaTable.OpenOrCreateAsync` before the `try` and its `finally` only DISPOSES
(`DeltaGlobalTableFunction.cs`), and `FlushCreateTransactionAsync` calls `DeltaWriter.Create` before its own
`try`/`finally`-dispose. Only `DeltaConflictException` is handled, by retrying (and the retry's
`OpenOrCreateAsync` then OPENS, so retries cannot multiply tables). Any other failure propagates with no
cleanup. **`RollbackTransaction` cannot be the compensation**: it reclaims DATA FILES, and
`DiscardBufferedFiles` calls `DeltaTable.OpenAsync` to do so, i.e. it structurally presupposes the table
exists.

**And a version-checked delete is the wrong answer — measured 2026-08-04.** "Read the latest version, delete
if still 0" races any writer that commits v1 in the window (a plain `INSERT` from another connection — a
`dbt run --threads N` is a fleet of them — or a foreign engine that discovers the table). Measured outcomes,
by delete SCOPE:
- deleting only `_delta_log/…0.json` makes the table **UNREADABLE** (*"Delta log is incomplete: version 0 is
  missing or unreadable … requires every version in [0..1]"*), though every parquet and the v1 commit survive,
  so a human can recover;
- deleting the **whole table folder** destroys the other writer's data IRREVERSIBLY, and widens the window to
  the whole multi-second delete (a recursive delete is atomic on no backend here — on S3 our own `DropTable`
  deletes `glob(/**)` file-by-file because httpfs's `RemoveDirectory` is broken), so it can also partially
  complete and leave a log referencing files we removed.

**⚠ THE OBJECTION IS AUTHORITY, NOT ATOMICITY — and an earlier draft of this section led with atomicity, which
does NOT survive one comparison: `DROP TABLE` is the SAME unconditional recursive folder delete**
(`DeltaCatalog.DropTable` → `HostFs.RemoveDir`, per-file fallback on S3 swallowing per-object errors), and we
ship it. So "a recursive delete can partially complete" cannot be what rules the compensation out. What
separates them is CONSENT: `DROP TABLE x` is destruction the user REQUESTED of a table they NAMED, with the
user present to see the result, and re-running `DROP` finishes a partial one — losing a concurrent writer's
rows is simply what DROP means (no Delta engine has a transactional DROP). The compensation would infer
"destroy this table" from a failure WE caused, on a path the user asked us to CREATE, with a third-party victim
who ran only an `INSERT` and nobody to notice the corruption.

⇒ the right shape is **delete the files you WROTE, by name** — `DiscardDataFilesAsync`, which re-reads a FRESH
log and refuses anything the table now references. That needs no authority beyond our own write, which is the
actual reason it is acceptable where a folder delete is not. It becomes available only AFTER the reordering
below, because then the folder is not a table yet: nothing can be discovered at a path with no `_delta_log`,
and a competing CREATE races on commit-0 (a put-if-absent) rather than on our bytes.

**The cheap improvement that does NOT need upstream, still not built:** reorder the autocommit CTAS to write
the data files FIRST and create+commit afterwards — the shape `TryStreamCreateFiles` already implements for
the buffered path (it writes parquet into a log-less folder and the flush creates after). A data-write failure
would then precede any commit, leaving nothing behind, and the only remaining window is between two adjacent
log writes with no data movement in between. It would NOT reduce the version count.
- **⚠ An earlier version of this paragraph said it "is non-partitioned-only today". That is WRONG** — the
  restriction belongs to `TryWriteStreamingCoreAsync` (the open-table streaming write), NOT to
  `TryStreamCreateFiles`, which partitions via `RunCopyPartitioned` (one DuckDB `COPY … PARTITION_BY`).
  Measured: a buffered partitioned CTAS writes through DuckDB into a Hive layout with `_delta_log` untouched
  until the flush. So the reorder would cover partitioned CTAS too — it is not a simple-case-only mitigation.
- What it genuinely would NOT cover is the **codec** provider (`engineeredwooddelta`), which has no DuckDB
  writer to stage files with. ⚠ That would make the two engines DIVERGE on failure semantics where today they
  agree — worth stating in the slice that takes it.

| | consequence |
|---|---|
| single writer | harmless — a millisecond window, correct end state |
| concurrent reader (Spark, delta-rs) | can observe an existing **EMPTY** table mid-flush |
| the v1 write FAILS | **an empty committed table is left behind by a transaction the user saw fail** — the inverse of every other flush path, where a failure leaves nothing. Reasoned from the measured shape, NOT itself measured (injecting the failure was not attempted) |

**Why it is this way, and what would fix it.** Not a protocol limit and not a decision — an EW API-shape limit:
`StartTransaction` is an INSTANCE method on `DeltaTable`, and `DeltaTable.OpenAsync` reads `_delta_log`, so there is
no way to express "a transaction that creates the table". `CreateAsync`/`DeltaWriter.Create` write v0 immediately.
A fix needs a static/factory transaction form (or a `CreateAsync` that accepts the actions to fuse into v0) —
i.e. an upstream capability, not something the Bridge can compose. Until then the two-version shape stands, and it
is the one place the buffer's "all-or-nothing at COMMIT" promise is weaker than everywhere else.

---

## 8. Multi-writer safety by storage backend

The commit primitive is put-if-absent on `_delta_log/<version>.json`. Whether that guard is real
depends on the filesystem:

| Storage | Commit guard | Verdict |
|---|---|---|
| Local POSIX | `O_EXCL` exclusive create | multi-process safe (validated: 4 processes × 200 rows → 800/800) |
| **Local WINDOWS (`D:\…`, DuckDB's `LocalFileSystem`)** | **NONE THAT HOLDS** — `fabricator_fs_write_probe` reports `EXCLUSIVE_CREATE` **SUCCEEDING on an existing file** ("NO put-if-absent guard") *and* `MoveFile` **overwriting** its target, so neither primitive is conditional | **MEASURED 2026-08-03 (§8.5): 6 writers × 3 INSERTs × 50 rows ⇒ 400 of 900 rows landed, 500 SILENTLY LOST, every writer exited 0.** Single-writer only |
| OneLake / abfss (`onelake://`) | ADLS conditional create (`If-None-Match: *`) | multi-process safe for DATA (no lost writes measured), but a losing writer can surface an ERROR instead of retrying — see §8.1 |
| Plain ADLS Gen2 `abfss://` **with an azure `SECRET` NAMED** | the SAME ADLS conditional create — a credentialed abfss root now takes the direct-SDK filesystem, which is what OneLake always took | **MEASURED 2026-08-02 (§8.4): 6 writers × 8 commits ⇒ 48/48, zero losses, commit versions fully interleaved across writers so contention was real** |
| Plain ADLS Gen2 `abfss://` with **no named secret** | **none that holds** — duckdb-azure's `ExclusiveCreate` is a client-side existence CHECK, so it races | **MEASURED 2026-08-02 (§8.4): 41 of 48 landed, six of the seven losses SILENT.** Single-writer only; RENAME/DROP also unavailable |
| Fabric fuse mount (`/lakehouse/default`) | `O_EXCL` over fuse — doubtful | treat as **single-writer** |
| S3 plain ATTACH (httpfs) | **none** — httpfs never sends `If-None-Match` | **MEASURED 2026-08-02 (§8.3): 6 writers × 8 commits ⇒ 8 of 48 landed, 40 SILENTLY LOST, zero errors.** Single-writer, and the "guarded" alternative needs the secret NAMED in the ATTACH |
| S3 ATTACH **with an s3 `SECRET`** | real conditional PUT via the AWS SDK (`S3CommitFileSystem`: Get(temp) → Put(target, `If-None-Match:"*"`) → Delete(temp); 412 → conflict → OCC/rebase) | multi-process / multi-engine safe (validated on MinIO: 4 × 10 commits × 20 rows → 40/40, 800/800, across checkpoint boundaries) |

### 8.1 OneLake multi-writer — MEASURED 2026-07-31 (it had only been INFERRED)

Until this measurement the OneLake row above read "multi-process / multi-engine safe" with **no
validation numbers**, while the local-POSIX and S3 rows carried real ones. That verdict was an
inference from the `EXCLUSIVE_CREATE` probe (`docs/delta-catalog.md`) plus the fact that the conflict
checker is storage-agnostic. Both halves of the inference were right; the conclusion was too strong.

Harness: `scratchpad/iso_race.sh` — N `duckdb.exe` processes committing autocommit INSERTs to ONE
OneLake table, each row tagged `(writer, commit)` so a lost write shows up as a **missing group**, not
just a wrong total. Two things made it a measurement rather than a green tick: the per-commit log
lines are a **positive control** that the log sink works (at `Warning` a conflict-free run leaves an
empty log, which is indistinguishable from broken logging), and the flush's OCC retry is now **logged
at all** — it used to be a silent `catch`, so a run where writers merely serialized looked identical
to one where the guard rejected and retried.

| writers × commits × rows | commits | result |
|---|---|---|
| 4 × 5 × 20 | 20 | 400/400 rows, 20/20 groups, versions v1–v20 unique+contiguous, 0 conflicts |
| 4 × 8 × 20 | 32 | 640 rows, v1–v32 unique+contiguous, 0 conflicts |
| 8 × 12 × 1 | 96 | **2 of 8 writers FAILED** (`_last_checkpoint` JSON parse) |
| 8 × 12 × 1 | 96 | **1 of 8 FAILED** (same) |
| 8 × 12 × 1 *(after fix 1)* | 96 | **1 of 8 FAILED** — raw Azure 412 `ConditionNotMet` out of `complete_bulk` |
| 8 × 12 × 1 *(after fix 1)* | 96 | **96/96, v1–v96 contiguous, 0 failures** |
| 10 × 15 × 1 *(after fix 1)* | 150 | **2 of 10 FAILED** — the 412 again, now WITH a stack trace |
| 10 × 15 × 1 *(after fix 2)* | 150 | **150/150 commits, 0 failures, 0 × 412** |

**What is now established:**

- **No lost writes, no corruption, ever** — across every run the landed versions were unique and
  contiguous and every `(writer, commit)` group was complete. The commit guard is *sound*: two writers
  never both took a version.
- **The guard fires AND the retry works — observed end to end (2026-08-01).** A 10 × 15 run logged
  `delta flush …: commit conflict — reopening at latest (attempt 1/16)` and that writer then **committed
  successfully**: 150/150 rows, 150/150 groups, 1 OCC retry, 0 failures. This is the piece every earlier
  run was missing, and it only became visible because that retry is now logged. (It also exposed a
  harness bug worth avoiding: the run script's `grep -iE 'error|conflict'` counted the healthy retry line
  as a writer failure, making a good run look broken.)
- **Low contention never exercises the guard.** 32 commits across 4 processes produced **0** conflicts:
  each INSERT spends most of its ~1.7 s writing parquet, so the read-latest→write-commit windows rarely
  overlap. A green low-contention run therefore proves nothing about put-if-absent, which is exactly why
  the earlier "safe" verdict was unsupported. Contention has to be *forced* (many tiny commits).
- **One real bug found and FIXED** (`CheckpointReader`): `_last_checkpoint` is updated by non-atomic
  OVERWRITE (`UploadAsync(overwrite: true)`), so a concurrent reader could see it at **zero bytes** and
  die in `JsonDocument.Parse` with *"The input does not contain any JSON tokens"* — a failed COMMIT
  caused by an **advisory** file. Now treated as absent (spec-conformant: readers must fall back to
  listing). Gate: `test/verify_delta_last_checkpoint.test` (34, hermetic, mutation-tested).
- **The second failure — a raw `412 ConditionNotMet` — is now ROOT-CAUSED AND FIXED, and it turned out
  to be the SAME root object as the first.** It escaped as a generic error (never became a
  `DeltaConflictException`, so no retry) and the statement failed. Two hypotheses, in order:

  **Hypothesis 1 (WRONG): the exists-conflict wasn't mapped.** `OneLakeDataLakeFileSystem.CreateAsync`
  caught only 409 while `RenameAsync` beside it catches 409 **and** 412. `scratchpad/adlsprobe` settles
  this *deterministically* — no race required, because an existing target is an existing target:

  | operation against an EXISTING path (live OneLake, 2026-07-31) | result |
  |---|---|
  | conditional CREATE (`IfNoneMatch=*`) | `RequestFailedException` **409 `PathAlreadyExists`** |
  | conditional RENAME onto existing destination | **409 `PathAlreadyExists`** |
  | unconditional CREATE | succeeds (overwrites) |
  | `UploadAsync(overwrite: true)` | succeeds — this is the non-atomic `_last_checkpoint` update |

  So ADLS reports an exists-conflict as **409, never 412**; the 409-only catch was already sufficient
  and that site cannot be the source. (Both catches keep the 412 for symmetry and for ADLS-compatible
  endpoints, not because it is reachable there.) Crucially this *redirected* the search: a 412 means an
  **ETag precondition** failed, so something had to be sending one.

  **Hypothesis 2 (CORRECT), and it was read off a stack trace, not guessed.** With the log sink now
  recording traces at `Debug`, the failure reproduced on the FIRST attempt (2 of 10 writers) and named
  the site outright:

  ```
  Azure…BlobRestClient.DownloadAsync(… ifMatch, ifNoneMatch …)
    ← BlobBaseClient.OpenReadInternal → LazyLoadingReadOnlyStream.ReadAsync
    ← OneLakeDataLakeFileSystem.ReadAllBytesAsync
    ← CheckpointReader.ReadLastCheckpointAsync
    ← SnapshotBuilder.BuildAsync → DeltaTable.OpenAsync
    ← DeltaCatalog.FlushDeferredFilesAsync
  ```

  `ReadAllBytesAsync` used `OpenReadAsync`, which returns Azure's **lazy `LazyLoadingReadOnlyStream`**:
  it fetches the blob in successive RANGE requests and, to keep a multi-request read self-consistent,
  **pins the ETag from the first response and sends `If-Match` on every later one**. `_last_checkpoint`
  is overwritten in place by a concurrent writer's checkpoint → the read tears → **412**.

  **So both multi-writer failures are the same root object by two mechanisms**: a non-atomically
  overwritten `_last_checkpoint` read while it changes — once observed as *empty content* (fixed in
  `CheckpointReader`'s parse guards), once as a *torn ranged read* (this). The parse guards could never
  have caught the 412 because it is thrown by the filesystem read, before any parsing.

  **Fix, in two layers.** (1) `ReadAllBytesAsync` now issues ONE unconditional request
  (`ReadContentAsync`) — a single-request read cannot tear, so the precondition cannot arise, and
  `ITableFileSystem` documents this method as being for *small* files, which have no use for a
  resumable ranged stream. (2) `ReadLastCheckpointAsync` additionally treats **any** read failure as
  "no hint" (cancellation excepted) — belt to that braces, for any store that can tear.

  **The trap worth carrying forward: a client library can add a conditional header you never wrote.**
  This doc previously asserted "no read path sends a precondition" — true of *our* code and false of
  the behaviour, because `OpenRead` inserts `If-Match` internally. Grepping our source for `IfMatch`
  therefore "proved" the wrong thing; only the trace showed it.

  **Method notes.** The two techniques are complementary and both were needed: the *deterministic*
  probe (ask the service what status it returns) cheaply FALSIFIED hypothesis 1 and redirected the
  search, while the *instrumented repro* PINNED the site. The sink change is what made the second
  possible — it previously logged exception type + message, naming *what* failed and never *where*.
  Reproduce/regress with `ATTEMPTS=N bash scratchpad/hunt412.sh`.

**Practical guidance.** The normal case never had a problem (dbt `--threads N` writes model-per-table,
so writers don't contend on one table). For the hard case — many processes appending to *one* OneLake
table — both observed failure modes are now fixed and the 150-commit shape that reproduced them runs
clean. Retrying a failed statement is still sound advice for any OCC system, but it is no longer
required to work around a known defect.

### 8.2 The "teardown hang" — investigated, and it does NOT survive scrutiny

An earlier revision of this section reported that a `duckdb.exe` "finished all its work and then failed
to exit", on runs both with and without errors, and called it an unexplained teardown defect. Chasing it
with `dotnet-stack` dissolved the claim. Recorded because the *investigation* is the useful part:

- **The detector's first hit was a FALSE POSITIVE.** "A process alive with no commit-log activity for
  40 s" also describes the harness's **verify** step — a plain `SELECT` whose OneLake table open writes
  no commit lines and legitimately takes that long. Its stack (blocked in
  `DeltaReader.GetSchemaAndVersion`) looked alarming; the query then returned the correct 48/48. *A
  process blocked in a stack you don't like is not evidence of a hang — slow and stuck have identical
  stacks.* The fix was to require the writer phase to still be in progress, not merely "quiet".
- **The strays correlate with FAILING writers**, not with completion. They appeared after the runs that
  hit the `_last_checkpoint` JSON error and the 412, and not after clean ones. On the fixed build, two
  full 10 × 15 runs left no writer-phase stray.
- **The last stray was self-inflicted.** Editing `iso_race.sh` *while it was running* corrupted the
  harness: bash reads a script by byte offset, so the edit made it resume mid-token (`p: command not
  found`) and relaunch work after the verify had already printed. The surviving process was doing real
  work — its CPU time kept climbing — not deadlocked. **Never edit a running shell script.**

**Conclusion: no evidence of an independent teardown defect on the current build.** The honest residual
is narrower and worth keeping: when a writer *errors* during a OneLake commit, the process has been seen
not to exit promptly. Both known causes of such errors are now fixed, so it no longer triggers here; if
it resurfaces, capture stacks with `dotnet-stack report -p <pid>` before killing the process — that took
the question from "unexplained" to "answered" in one reading.

Two S3 findings worth remembering: a **conditional CopyObject is silently unguarded** on MinIO
(AWS documents conditional writes for PutObject/CompleteMultipartUpload only) — the guard must be a
conditional *Put*; and httpfs **pins the ETag captured at open** (re-served from its caches), so a
concurrently overwritten `_last_checkpoint` must be read through the SDK, not the host FS.

External writers (Spark, delta-rs) compose with all of the above: our rebase treats their commits
like any concurrent commit; delta-rs gets native S3 conditional-put with
`conditional_put: "etag"` in its storage options.

### Idempotent producer retries

Plain OCC cannot protect a *retried batch whose first attempt actually committed*
(crash-after-commit). Use Delta **application transactions**:
`SELECT * FROM <catalog>.delta.set_transaction_version('schema.table', app_id, version [, expected := previous])`
inside the explicit transaction — the flush compare-and-swaps against the latest snapshot's
`AppTransactions` on every retry attempt and emits the spec `txn` action atomically with the fused
commit; a duplicate retry fails the CAS ("transaction version conflict") instead of duplicating
data. Read the committed high-water mark with `<catalog>.delta.get_transaction_version(…)`.

### 8.3 S3 without a NAMED secret — MEASURED 2026-08-02 (it had only been INFERRED), and it is worse than the caveat said

The "S3 plain ATTACH" row above carried no numbers for a year while the local-POSIX and secret-routed
S3 rows carried real ones — the same gap §8.1 found for OneLake, where the inference turned out to be
WRONG. Here it was right, and understated.

**A/B, one option apart, `scratchpad/s3_race.sh` (6 `duckdb.exe` processes × 8 autocommit INSERTs into
one MinIO Delta table, each row tagged `(writer, commit)`):**

| ATTACH | attempted | commit files | rows | distinct groups |
|---|---|---|---|---|
| no `SECRET` clause | 48 | **8** | 8 | 8 |
| `… , SECRET minio_s3, …` | 48 | **48** | 48 | 48 |

**40 of 48 commits silently lost, and NOT ONE WRITER REPORTED AN ERROR.** The guarded control is the
positive control: same harness, same contention, so the harness is not what produced the zeros.

**⚠ THE SHARPENING THAT MATTERS, and the caveat's wording hides it: the guard is opt-in PER ATTACH, and
having an s3 secret in scope is NOT enough.** `BuildConnectionString` appends the credential marker only
for the secret the ATTACH NAMES; `TableFileSystems.Create` then wraps the root in `S3CommitFileSystem`
(real `PutObject` + `If-None-Match:"*"`). Without the clause the catalog falls back to
`DuckDbTableFileSystem` — while httpfs happily uses the very same ambient secret for DATA IO. So the
unsafe configuration **authenticates, writes, reads and passes every single-writer test**, and looks
correctly configured to anyone who created a secret. Measured directly: an unnamed-secret ATTACH does
its CTAS fine and then fails `ALTER TABLE … RENAME` with *"S3FileSystem: MoveFile is not implemented"* —
the documented secretless signature.

**Why the loss is SILENT rather than an error, which is the part worth understanding.** EW's commit is
`WriteCommitAsync` → `_fs.RenameAsync(temp, target)`, and `DuckDbTableFileSystem.RenameAsync` does NOT
call MoveFile — it emulates put-if-absent by creating the TARGET with **`EXCLUSIVE_CREATE`** and copying
the bytes in. `fabricator_fs_write_probe` on `s3://` reports
*"EXCLUSIVE_CREATE on an existing file SUCCEEDED — NO put-if-absent guard"*, so both writers' creates
succeed, the later one overwrites, and neither sees a failure. Every link is now measured rather than
argued.
- ⚠ **The `ALTER TABLE` failure above is a DIFFERENT operation** (`fs_move_dir`, a directory rename) and
  reading it as the commit path suggests secretless commits fail LOUDLY. They do not. Two operations
  named "rename", one implemented and unguarded, the other unimplemented.

**Fix for a user: name the secret** — `ATTACH 's3://…' AS lake (TYPE fabricator, PROVIDER 'delta',
SECRET my_s3, READ_ONLY false)`.

**And since 2026-08-02 the attach WARNS when you have not** (`duckdb_logs`, category `Fabricator.Delta`),
naming the remedy and the fact that a secret merely being in scope is not enough. Justified by the
asymmetry rather than by taste: the failure is silent, severe and invisible to every single-writer test,
and the fix is one option.
- **It needed one piece of plumbing.** `READ_ONLY` is a DuckDB ATTACH KEYWORD, not a member of
  `options.options`, so no provider could see it. `fabricator_storage.cpp` now forwards a SYNTHETIC
  `"access_mode"` (`read_only` / `read_write` / `automatic` / `undefined`) in the options JSON — no ABI
  change, the JSON is already free-form, and "am I allowed to write here?" is a fair provider question.
  `AUTOMATIC` is passed through as itself rather than resolved: what it resolves to is DuckDB's business.
- **⚠ The gate is `read_write` SPECIFICALLY, and that is a measured decision, not caution.** An
  `s3://` attach with no `READ_ONLY` clause is **bumped to read-only by DuckDB** — measured, a `CREATE`
  against it fails with *"attached in read-only mode"*. So `READ_ONLY false` is the ONLY route to a
  writable S3 catalog, which gives the warning complete coverage of the dangerous shape with no false
  positives. Warning on `AUTOMATIC` too would fire on catalogs that can never reach the unguarded commit
  path, which is how a real warning gets trained away.
- Gate: `verify_delta_catalog_s3` §11 (161 → **171**), **two mutants killed in opposite directions by ONE
  assertion** — suppressing the warning yields 0, ignoring `access_mode` yields 2 (the AUTOMATIC attach
  warns too). The second is what proves the new C++ plumbing is READ rather than merely forwarded.

### 8.4 Plain (non-OneLake) ADLS Gen2 — MEASURED 2026-08-02, and the probe said it was FINE

Support for a plain ADLS Gen2 storage account (`abfss://<fs>@<account>.dfs.core.windows.net/…`, as opposed
to Fabric OneLake) started from the reasonable-looking position that it already worked: on a live account,
ATTACH, discovery, CTAS, INSERT, DELETE, DROP and both directions of parquet IO through duckdb-azure all
succeeded on the first try. Two things did not, and only one of them announced itself.

**Defect 1 — RENAME TABLE was impossible.** `AzureDfsStorageFileSystem: MoveFile is not implemented!`. Loud,
but easy to under-rate: a dbt table model's swap is two renames, so *every re-deploy* of a table model
against such an account failed. This is the same gap OneLake had, and OneLake had a fix — the DFS-native
atomic directory rename — that was gated on `IsOneLake` and therefore unreachable for any other account.

**Defect 2 — the commit guard did not hold, and a capability probe SAID IT DID.**
`fabricator_fs_write_probe` reports `exclusive_create_existing_fails = true` on abfss, with duckdb-azure's
own message *"ExclusiveCreate specified while file already exists"*. That is a genuine throw and a correct
single-threaded answer. It is also a **client-side existence check**, so two writers at the same Delta
version both pass it, both create, and one silently wins — plus their appends can collide, which is where
the one non-silent symptom came from:

| ATTACH shape | attempted | commit files | rows | missing groups | writer errors |
|---|---|---|---|---|---|
| no named secret (host-FS commit) | 48 | 42 | 41 | **7** | 1 (`InvalidFlushPosition`) |
| `SECRET adls_kv` (direct-SDK commit) | 48 | 48 | 48 | **0** | 0 |

Six of the seven losses raised nothing at all. The seventh surfaced as Azure
*"InvalidFlushPosition … the uploaded data is not contiguous"* — an error that reads like a transient upload
fault rather than "your commit was overwritten", so even the loud case misdirects. Harness:
`scratchpad/adls_race.sh` (`W`/`C`/`NAMED`).

**The lesson is specifically about probes.** §8.3's S3 case was detectable by capability probe — httpfs
overwrites on `EXCLUSIVE_CREATE`, so the probe fails and the answer is visible without concurrency. Here the
probe PASSES and the implementation is still unsafe, because "throws on an existing file" and "is atomic"
are different claims and only the second one matters for a commit. **A capability probe can rule a backend
OUT; it cannot rule one IN.** Only a concurrent run distinguishes a conditional PUT from a check.

**The fix is one mechanism for both defects**: split the discriminator that had conflated transport with
catalog. `AdlsPath.IsAdlsGen2` (is this the ADLS Gen2 DFS transport?) now selects the filesystem, the
directory ops and the commit primitive; `FabricLakehouse.IsOneLake` (is this a Fabric lakehouse?) keeps
selecting only what is genuinely Fabric — Unity Catalog discovery, the schema-enabled flag, the `fabric_*`
functions. The direct-SDK filesystem was never OneLake-specific; it had always parsed its endpoint host out
of the `abfss://` path, so *only the gate* said otherwise (it was renamed `OneLakeDataLakeFileSystem` →
`AdlsGen2TableFileSystem` to stop the name claiming a restriction the code did not have).

**Credentials gained a shape, and that is the only genuinely new code.** Everything ADLS-facing assumed an
Entra `TokenCredential`. A plain account commonly ships as an account key or a storage connection string,
which no Entra path can consume — hence `AdlsCredential` (token *or* shared key). Note the asymmetry, since
it is easy to state backwards: **a plain ADLS account accepts BOTH** (Entra via RBAC is fully supported
there and is the better practice); **OneLake is Entra-ONLY**. So the credential shape follows the SECRET,
not the kind of account, with one guard — `entraOnly` — so that an azure secret carrying a
`connection_string` cannot silently downgrade a Fabric attach to key auth OneLake would reject. An
explicitly configured service principal outranks key material for the same reason in reverse.

**Naming the secret is load-bearing, exactly as on S3.** The credential reaches the catalog only via the
marker `BuildConnectionString` appends, and that runs only when the ATTACH NAMES a secret. An azure secret
merely in scope authenticates duckdb-azure's DATA IO — so the unguarded configuration reads, writes and
passes every single-writer test. Hence the attach-time warning (§8.3's, generalized to both backends).

**`COPY … TO 'abfss://…' (FORMAT delta)` is routed through the direct-SDK filesystem too** — and this was
initially got WRONG, in a way worth recording. The first pass shipped it on the host-FS path and justified
that as acceptable ("no `SECRET` clause, one statement, one commit"). But "has no SECRET clause" described
the plumbing, not a constraint: with `FORMAT delta` we build the catalog ourselves and we know the target is
abfss, so we can resolve a credential exactly as the `onelake://` FileSystem already does. **A limitation
that is really an unimplemented case should not be written up as a design trade-off.**

The resolution rule is a **scope match**, not a name: a DuckDB secret's scope IS a path prefix, so a secret
that matches was declared for this location, and `azure` secrets scope to `abfss://` by default — the common
case needs no user action. There is deliberately **no "any secret of this type" fallback** (the `onelake://`
FileSystem has one only because that scheme matches no azure secret's default scope): guessing among
accounts is how a write lands somewhere the user did not intend. New helper
`BuildConnectionStringFromScopedSecret` (C++), best-effort by contract — any failure means "no credential",
never a failed statement.

⚠ **The same auto-resolution was deliberately NOT applied to ATTACH,** and the reason is a hazard found while
trying: at that point in `fabricator_storage.cpp` the `provider` may be EMPTY (no `PROVIDER` option — it is
inferred later from the scheme), and an empty provider resolves to the DEFAULT backend, whose azure branch
merges the fields into a **SQL Server** connection string. That would mangle the abfss path and break an
attach that currently works. COPY is safe because its provider is hardcoded `"delta"`. Auto-resolving at
ATTACH needs provider dispatch settled first; until then the remedy there stays the explicit `SECRET`, which
the attach warning names.

Also unchanged: `fabricator_delta_scan` (a GLOBAL function, no catalog, therefore no credential) still reads
through the host filesystem. That is a read — the commit guard does not apply — so it is a dependency on
duckdb-azure, not a correctness gap.

**The filesystem choice is invisible from SQL**, which is why a `Fabricator.Delta.Fs` debug line now names it
per table open. That log plus a negative control is how the COPY routing was actually verified (azure secret
in scope ⇒ `AdlsGen2TableFileSystem`; no secret at all ⇒ `DuckDbTableFileSystem`) — the suite's COPY section
can only assert the round trip, and says so.

Gate: `test/verify_delta_catalog_adls.test` (52 assertions, manual/live-account tier — outside both CI tiers
by construction, since its `require-env`s are not in the service tier's provided list). The RENAME section is
mutation-tested: reverting the gate to `IsOneLake` kills it at exactly that line with the original
`MoveFile is not implemented` error. ⚠ The DISCOVERY section is deliberately documented as pinning the
outcome and **not** the mechanism — the host glob also answers correctly on a plain account (unlike OneLake,
where duckdb-azure's mid-path wildcard is broken), so swapping the DFS-SDK walk back for the glob does not
fail it.

---

### 8.5 Local WINDOWS — MEASURED 2026-08-03, and it is NOT safe (found incidentally)

Found while trying to measure something else entirely (the autocommit merge-on-read UPDATE's new row-level
reconciliation, [ew-master-migration.md](ew-master-migration.md) §THE `*BySelection*` QUESTION). It is
**independent of that change** and applies to every writer on a local Windows Delta root.

**The row above was never wrong — it says "Local POSIX" — but nothing said anything about Windows, and "local"
reads as covering it.** The `O_EXCL` guarantee is a POSIX one; DuckDB's `LocalFileSystem` on Windows does not
deliver it here.

`SELECT * FROM fabricator_fs_write_probe('D:/…')` on this box:

| step | ok | detail |
|---|---|---|
| `exclusive_create_existing_fails` | **false** | `EXCLUSIVE_CREATE` on an existing file **SUCCEEDED** — NO put-if-absent guard (unsafe for commits) |
| `move_overwrite_behavior` | true | `MoveFile` **OVERWROTE** the existing target ⇒ NOT a put-if-absent primitive either |

So **neither** commit primitive is conditional, and the measurement agrees with the probe: 6 concurrent
`duckdb.exe` writers × 3 autocommit INSERTs × 50 rows against one local table ⇒ **400 of 900 rows landed, 500
silently lost, one writer's rows missing ENTIRELY, and every writer exited 0.** Same shape as the secretless S3
row (§8.3) and the unnamed-secret ADLS row (§8.4): no error, no retry, just absence.

**A second, louder symptom on the same root: TORN READS.** A concurrent writer that re-reads the log (a
merge-on-read UPDATE does, on every OCC attempt) parses a commit file *mid-write* and fails with
`'w' is an invalid start of a value` or `Expected end of string … BytePositionInLine: 9` — and **9 is exactly the
length of `{"remove"`**, which is what identified it as a truncated commit rather than a conflict. Because the
target file is created and then written into, rather than published atomically, a partial commit is *observable*.
Under load the table can be left where even `get_metadata` fails.

**Why this was never caught:** §8.1's harness and the fuse-race harness both check that COMMITTED groups are
complete and versions unique — i.e. whether the WINNER is unique. Neither checks whether a concurrent READER can
observe a partial commit, and the INSERT path re-reads the log less than the UPDATE path does, so INSERT-only
runs fail silently (lost rows) while UPDATE runs fail loudly (torn JSON). One root cause, two symptoms.

⚠ **Consequence for harness design: a local Windows root cannot host ANY multi-writer experiment** — the
substrate's own losses and torn reads swamp whatever is under test, in *both* legs of an A/B. Use OneLake/abfss,
S3 with a NAMED secret, or POSIX local (WSL). `scratchpad/mor_update_race.sh` carries that warning at the top.
**Check the probe before believing a multi-writer result**, exactly as §8.4 concluded for the opposite reason
(there the probe was too OPTIMISTIC; here it is correct and was simply never run on this platform).

**ROOT-CAUSED 2026-08-08 — it is a DuckDB bug, NOT a Windows limitation, and NOT our mapping.** Full
write-up ready to file: [duckdb-upstream-issues.md](duckdb-upstream-issues.md) §4. In one line:
**`FileOpenFlags::ExclusiveCreate()` is read in exactly one place in all of DuckDB** —
`local_file_system.cpp:370-371`, inside the **POSIX** branch (`open_flags |= O_EXCL`). The Windows
`OpenFile` (`:1069-1075`) consults only `CreateFileIfNotExists()` → `OPEN_ALWAYS` and
`OverwriteExistingFile()` → `CREATE_ALWAYS`; **`CREATE_NEW` — the Win32 disposition that fails with
`ERROR_FILE_EXISTS`, i.e. exactly this primitive — appears nowhere in the file.** So the flag is silently
dropped and the open falls to `OPEN_ALWAYS` = "open, creating if absent", which is precisely what the probe
reports. Windows has had the feature since forever; DuckDB just never selects it.

⚠ **The earlier speculation in this paragraph was half right in a misleading way.** It guessed "no
`CREATE_NEW` … on Windows", which reads as *the platform lacks it*. The platform has it; the mapping omits
it. Worth keeping as a reminder that a plausible cause written into a doc gets read as a finding.

Two things make it easy to believe and easy to miss: the flag is **public, documented surface** — `Verify()`
asserts its combination rules and the C API exposes it as `DUCKDB_FILE_FLAG_CREATE_NEW` — while
`FILE_FLAGS_EXCLUSIVE_CREATE` has **zero internal DuckDB callers**, so no upstream test can reach the gap.
And DuckDB has *two* names containing `CREATE_NEW` with opposite meanings (the C API's = exclusive, the
internal `FILE_FLAGS_FILE_CREATE_NEW` = truncate), so the Windows branch looks complete.

The upstream fix is ~3 lines (test `ExclusiveCreate()` **first**, map to `CREATE_NEW`) and would make local
Windows as safe as local POSIX **with no change on our side** — `HostFsOpenWrite` already passes
`WRITE | FILE_CREATE | EXCLUSIVE_CREATE`. A local workaround (write-temp-then-atomic-publish, or a
platform-specific conditional create in our own filesystem) is therefore deliberately NOT attempted: it
would duplicate a fix that belongs one layer down. Single-writer behaviour — every suite in the hermetic
tier — is unaffected either way.

#### 8.5a ⚠ `fabricator_fs_write_probe` HAS A FALSE-POSITIVE MODE — FOUND, **NOT FIXED** (2026-08-03)

Found by running the README example verbatim (the standing "run the README's SQL before committing it" rule
paying for itself). **Point the probe at a path whose parent does not exist and the one cell that matters reports
the guard as WORKING:**

```
create_directory                | true  | CreateDirectory did not create the directory      <-- ok=true, message says otherwise
write_create                    | false | threw: ... The system cannot find the path specified
file_exists                     | true  | FileExists=false (unexpected)                     <-- ok=true, message says otherwise
exclusive_create_existing_fails | true  | EXCLUSIVE_CREATE on an existing file threw (put-if-absent works): ... cannot find the path
```

`exclusive_create_existing_fails` is recorded as `threw` (`fabricator_fs_spike.cpp:534`) **without checking its
precondition — that `f1` exists at all.** With `write_create` failed, the exclusive open throws because the
DIRECTORY is missing, and the probe reads that as the put-if-absent guard firing. This is the
"a probe whose PRECONDITION failed is VOID, not evidence" rule violated *inside the diagnostic that exists to
supply the evidence* — and it fails in the UNSAFE direction, reporting safe.

Two neighbouring cells have the same shape for a different reason: `run()` records `ok = did not throw`, while
`create_directory` and `file_exists` *return a message* on failure instead of throwing, so both report `ok=true`
with a detail that says they failed.

**The fix is small and deliberately not taken here** (this pass is C#-only; touching this file needs a full C++
rebuild, and mixing that into a behaviour-preserving refactor is how a clean bisect gets lost): gate the
exclusive-create verdict on `fs.FileExists(f1)` and record a VOID result when the precondition is absent; and make
`create_directory`/`file_exists` throw on their own invariant so `run()`'s `ok` means something. Until then, the
README block and §8.5 both say to confirm `create_directory` and `write_create` are `true` before reading the
verdict.

---

## 9. Feature interactions cheat-sheet

| Feature | In explicit transactions |
|---|---|
| Deletion vectors (default on) | required for buffered DML; same-txn deletes produce adds *born with* an inline DV |
| CDF | works (cdc files eager per statement, fused commit is cdc-only-read); **not** partitioned CDF, not DML-after-ALTER, not identity×CDF |
| Identity columns | appends work (statement-time generation, chained HWM, abort on concurrent consumption); buffered CREATE works (HWM baked into commit-0); under a pending ALTER → batch park |
| Row tracking / materialize | appends eager (ids derive from `baseRowId`+position at commit); materialize UPDATE bakes original ids (unpartitioned); rebase re-derives HWMs |
| Column mapping | transparent (physical names pre-assigned at buffer time for pending creates) |
| Time travel `AT` | reads the requested version, excludes pending changes |
| VARIANT | follows its own path gates (docs/variant-support.md); no extra transactional rules |

---

## 10. Databricks / Spark comparison — worked SQL scenarios

Reference: [Databricks isolation levels](https://docs.databricks.com/aws/en/optimizations/isolation/isolation-levels)
+ [row-level concurrency](https://docs.databricks.com/aws/en/optimizations/isolation/row-level-concurrency).
Summary: we implement the SAME two write-isolation levels with the same semantics and default
(`write_serializable` | `serializable`, Spark ConflictChecker parity, §6), but reads and multi-statement
behavior differ in our favor, while Databricks' proprietary row-level concurrency and the
`delta.isolationLevel` table property are things we don't have. Scenario notation: session A = us or
Databricks as stated, session B = any concurrent writer (another process/engine).

### 10.1 Repeatable reads across statements — WE ARE STRONGER

```sql
-- session A                                   -- session B
BEGIN;
SELECT count(*) FROM lake.main.t;  -- 100
                                               INSERT INTO t VALUES (...);  -- commits v+1
SELECT count(*) FROM lake.main.t;  -- STILL 100 (snapshot pinned at first read)
COMMIT;
```

Ours: both SELECTs read the SAME pinned snapshot (MVCC / REPEATABLE READ, §3) — B's commit is
invisible mid-transaction. **Classic Databricks/Spark: there is no multi-statement transaction at
all** — each SELECT is its own query with its own snapshot, so the second SELECT returns 101 under
BOTH of their isolation levels (those govern write conflicts, not cross-query read stability; their
multi-statement-transactions preview via UC coordinated commits is the exception). Same caveat for
us in autocommit: without BEGIN we also re-resolve per statement (deliberate Spark parity) — but
WITHIN one autocommit statement every reference to a table reads one version (§3), which is the
guarantee a self-join needs and which Spark also gives per query.

### 10.2 Atomic multi-statement write + ROLLBACK — WE ARE STRONGER

```sql
BEGIN;
INSERT INTO lake.main.t SELECT ...;   -- buffered (files eager, commit deferred)
DELETE FROM lake.main.t WHERE k = 3;  -- buffered DV
UPDATE lake.main.t SET v = 9 WHERE k = 5;
ROLLBACK;                             -- NOTHING happened (orphan parquet for VACUUM)
-- or COMMIT;                         -- ONE Delta commit, operation=TRANSACTION
```

Ours: one atomic Delta version per table, real ROLLBACK. Classic Databricks/Spark: three separate
commits, no undo — a failure mid-script leaves the first statements applied. (Their preview feature
targets this gap; different mechanism.)

### 10.3 Concurrent blind append during a transaction — PARITY

```sql
-- session A (ours)                            -- session B
BEGIN;
INSERT INTO lake.main.t VALUES (1);
                                               INSERT INTO t VALUES (2);  -- commits first
COMMIT;  -- rebase: appends commute -> BOTH land, no error
```

Same outcome as Databricks under WriteSerializable (blind appends never conflict). Under
`serializable` + session A having READ `t` with a predicate matching B's rows, our COMMIT aborts —
exactly their Serializable behavior. Documented divergence (§6): an APPEND-ONLY transaction that
read the table stays on the blind path under write_serializable (Spark would run the read checks).

### 10.4 Concurrent DML on the SAME FILE — PARITY+ (row-level concurrency, v1 2026-07-14)

```sql
-- session A                                   -- session B
UPDATE t SET v = 1 WHERE id = 10;              UPDATE t SET v = 2 WHERE id = 20;
-- rows 10 and 20 sit in the SAME parquet file → BOTH succeed
```

Databricks (DBR 14.3+, unpartitioned + DVs) and now US: concurrent UPDATE/DELETE touching
**different rows of the same file** both land — the loser's COMMIT rebases its deletion-vector
pairs onto the winner's state (`RebaseDvDmlActionsAsync`: the touched row sets are checked disjoint,
the DVs re-union, post-image adds re-derive their row-id range). The SAME row modified by both →
`row-level conflict on file '…': N row(s) … concurrently deleted or updated` (first committer wins,
no lost update). OSS Spark, delta-rs, delta-kernel all still conflict at FILE level — this is
otherwise Databricks-proprietary. Where we go BEYOND Databricks: it works on **partitioned** tables
(the DV mechanics don't care) and inside **multi-statement transactions** (their row-level scope is
per statement); the buffered-txn read checks relax to row level too (a concurrent DV swap of a file
the transaction merely READ no longer aborts — `CheckLogicalRebaseAsync(rowLevelDml:)`). Applies
under `write_serializable` only ( `serializable` keeps strict file-level checks) and to DV tables
only (copy-on-write rewrites can't reconcile — the `deletion_vectors false` PolyBase recipe keeps
file-level conflicts). **v2 (same day): the rewrite boundary is GONE — a concurrent OPTIMIZE /
copy-on-write rewrite of a touched file REMAPS instead of conflicting** (`RemapRowsAcrossRewriteAsync`):
the tombstoned source file (still on storage until VACUUM) resolves the target rows' stable ids +
ORIGINAL commit versions; the post-rewrite files are scanned for those ids (compaction-shaped
`dataChange=false` adds first, early exit; fresh appends can't hold them — their derived ids sit above
the base HWM); the row's **commit version is the concurrent-modification discriminator** (relocated
untouched row keeps its original version; a concurrently UPDATED row carries the rewrite's version ⇒
row-level conflict; an id found nowhere was concurrently DELETED ⇒ row-level conflict); the found
positions become DV pairs on the NEW files. Requires row tracking (the default) — Databricks itself
still conflicts with compaction here. Under `rowLevelDml` the read-set checks are fully replaced by
the row-level write validation (WriteSerializable's definition: reads don't serialize — matches
Databricks' matrix, where inserts/DML never conflict with a WS transaction's reads). Autocommit DML
gets the same reconciliation via a bounded commit-retry loop.
Test: `test/verify_delta_row_level_concurrency.test` (70 — disjoint same-file DELETE/UPDATE compose,
same-row conflicts, DELETE and buffered UPDATE THROUGH a concurrent OPTIMIZE compose,
same-row-through-rewrite conflicts, serializable strict, three-writer pile-up; kernel-validated
readback of the remapped commits).

### 10.5 WriteSerializable's "state that never existed" — PARITY (same artifact)

```sql
-- session A (long DELETE)                     -- session B
DELETE FROM t WHERE grp = 'x';                 INSERT INTO t VALUES ('x', 99);  -- blind append
-- both succeed under write_serializable; history may order the INSERT *before* the DELETE
-- even though it committed after -> a reader can see a snapshot with the new row already
-- present but the old rows not yet deleted (never a "real" serial state).
```

We inherit the same reordering artifact by design (it's what write_serializable means). It's also
why CREATE-OR-REPLACE / partition-overwrite are guarded inside our explicit transactions: a
concurrent blind append could logically reorder past the overwrite. `serializable` removes the
artifact on both systems (the append aborts instead).

### 10.6 `delta.isolationLevel` table property — MEASURED against Fabric Spark (2026-07-31)

This section previously said "**we do NOT read it**" and proposed honoring it as a cheap follow-up.
That follow-up **shipped** (`PendingSerializable`): the table's own property wins, with the ATTACH
option as the fallback for a property-less table. What follows replaces that stale text with a live
measurement against **Fabric Spark 4.1.1 (Delta 4.x)**, workspace `Test` / lakehouse `LH`.

**Method + controls** (probe: `sparkprobe isolation`; a rejection is only meaningful if a known-good
property is accepted through the identical statement shape, and an acceptance only if a bad value is
demonstrably rejected):

| experiment | result |
|---|---|
| *control +* `delta.appendOnly='false'` at CREATE | **accepted** — the statement shape works |
| *control −* `delta.isolationLevel='Bogus'` | **rejected**: `[DELTA_INVALID_ISOLATION_LEVEL] invalid isolation level 'Bogus'` |
| *control −* `delta.appendOnly='notabool'` | **rejected**: `For input string: "notabool"` |
| `delta.isolationLevel='Serializable'` at CREATE | **accepted**, stored, writes fine |
| `delta.isolationLevel='WriteSerializable'` at CREATE | **REJECTED**: `requirement failed: delta.isolationLevel must be Serializable` |
| `delta.isolationLevel='SnapshotIsolation'` at CREATE | **REJECTED**, same message |
| `ALTER TABLE … SET TBLPROPERTIES ('delta.isolationLevel'='WriteSerializable')` | **REJECTED**: `Unsupported table change: requirement failed: …`; property stays absent |

Note the two negative controls fail *differently*: `'Bogus'` does not parse as an isolation level at
all, while `'WriteSerializable'` parses fine and then fails a `must be Serializable` requirement. So
OSS Delta **knows** the enum value — it is the **table-property validator** that admits only
`Serializable`. `WriteSerializable` as a table property is a **Databricks** feature.

**Three consequences, in order of how likely they are to bite:**

1. **Fabric Spark's own default is `Serializable`, not WriteSerializable.** `DESCRIBE HISTORY` on a
   Spark-created, Spark-written table records `Serializable` for `CREATE OR REPLACE TABLE`, `WRITE`
   (blind append) and `DELETE`. ⇒ our default (`write_serializable`) matches **Databricks**, and on a
   shared table with the property **absent** the two engines apply **different** levels — ours the
   more permissive (concurrent blind appends commute past our reads; Spark would abort). To make them
   agree, either ATTACH with `isolation_level 'serializable'`, or stamp the property (see 3).
   Every "write_serializable — Spark's default too" claim in this repo was wrong and is now corrected.
2. **We never write `isolationLevel` into `commitInfo`**; Spark does. So in `DESCRIBE HISTORY` our
   commits show a blank in that column (verified in the EW source — the field is simply not emitted —
   and in the live history). Cosmetic: `commitInfo` is informational, but a Fabric user cannot tell
   from history which level our commit used.
3. **A `WriteSerializable` property WE stamp is not hostile to Spark — it is honored.** On a table we
   created with `WITH ("delta.isolationLevel"='WriteSerializable')`, Fabric Spark read the data, read
   the property back as `WriteSerializable`, INSERTed, DELETEd — all fine — and recorded
   **`WriteSerializable`** as the level of *its own* commits. The evidence is an **A/B**: two tables
   created by us in the same run, identical except for the property, then given the identical Spark
   INSERT+DELETE. `DESCRIBE HISTORY` on the stamped one reports `WriteSerializable` for both Spark
   commits; on the unstamped one, `Serializable`. The only input that differed was the property, so
   Spark is reading it and committing at it. (In both, our own commits show a blank in that column —
   consequence 2.) Caveat: such a table's isolation is no longer manageable **from Spark** (its ALTER
   rejects every value except `Serializable`) — change it with `delta.set_tblproperties`.

   **Does the level actually change Spark's conflict behaviour, or is it just recorded?** Two of the
   three links are now established from primary sources; the third is inferred.

   1. **The level materially changes conflict detection** — from Delta's own `ConflictChecker.scala`
      (`delta-io/delta`, master):

      ```scala
      val addedFilesToCheckForConflicts = isolationLevel match {
        case WriteSerializable if !currentTransactionInfo.metadataChanged =>
          winningCommitSummary.changedDataAddedFiles              // blind appends EXCLUDED
        case Serializable | WriteSerializable =>
          winningCommitSummary.changedDataAddedFiles ++
            winningCommitSummary.blindAppendAddedFiles            // blind appends INCLUDED
        case SnapshotIsolation => Seq.empty
      }
      ```

      So under WriteSerializable a concurrent **blind append** is exempt from the read-conflict check;
      under Serializable it is checked. Exactly our own semantics (§6). The level is not decorative.
   2. **The table property selects the level Spark commits at** — the A/B above: nothing but the
      property differed, and the recorded level followed it.
   3. **Measured end to end (2026-08-01), and the answer is a PROBLEM ON OUR SIDE.** A live A/B against
      Fabric Spark 4.1.1.5.5 / **Delta-Lake 4.2.0** (`ConflictChecker.scala` re-read at the `v4.2.0`
      tag — identical to master): a 200M-row table, Spark running `DELETE … WHERE id % 7 = 3`, our
      writer committing an append inside the window.

      | table's declared level | overlap proven | Spark's DELETE |
      |---|---|---|
      | `Serializable` | ✅ Spark named our commit (v8) | **ABORTED** — `DELTA_CONCURRENT_APPEND` |
      | `WriteSerializable` | ✅ Spark named our commit (v23) | **ABORTED** — same error |

      > ⚠ **"we never emit that field" IS HISTORY — the write half shipped 2026-08-08 and the surface grew
      > again on 2026-08-11.** A buffered append inside an explicit transaction now declares `true` when it
      > read nothing and `false` when it did; autocommit still declares nothing, deliberately, because
      > scan-time read recording is gated on the transaction being explicit. Since the engineered-wood bump
      > onto `upstream/main`, EW ALSO writes the field for commits it drives itself — and its plain-append
      > path claimed `true` on our behalf, which our patch reverses (a host that scanned the table and
      > staged the result made a read EW never saw). **That patch is UPSTREAM as of 2026-08-12 (#137), so
      > there is no `fabricator-patches-v<n>` branch any more** — and upstream extended it with #143, which
      > makes a declared-`false` append non-rebase-safe, i.e. the claim now governs the RETRY and not only
      > the record. See [ew-master-migration.md](ew-master-migration.md) §THE 2026-08-12 PIN ONTO UPSTREAM.
      > The measurement below stands as the reason the field matters; only the "we emit nothing" premise
      > has moved.

      **Both abort.** The relaxation exists (link 1) and the property does select the level (link 2),
      but it buys our appends nothing — because the exemption applies only to files from a commit
      marked `isBlindAppend`, and **we never emit that field**:

      ```scala
      val isBlindAppendOption = commitInfo.flatMap(_.isBlindAppend)
      val blindAppendAddedFiles = if (isBlindAppendOption.getOrElse(false)) addedFiles else Seq()
      val changedDataAddedFiles = if (isBlindAppendOption.getOrElse(false)) Seq() else addedFiles
      ```

      `getOrElse(false)` — an absent flag means "not blind", so our appends land in
      `changedDataAddedFiles`, which is checked under **both** levels. Confirmed from three sides: the
      source above; our commitInfo on disk (`{"timestamp":…,"operation":"WRITE","engineInfo":
      "EngineeredWood.DeltaLake","operationParameters":{}}` — no flag); and Spark's own
      `DESCRIBE HISTORY`, where `isBlindAppend` reads `True` for its blind append and blank for ours.

      ⇒ **A Spark transaction will abort against our concurrent append whatever the table declares.**
      Setting `WriteSerializable` does not help in that direction *while the flag is missing*. It remains
      correct for OUR writer's own conflict checks and for Spark-vs-Spark concurrency — and it becomes
      the level that DOES help once we emit the flag (see the table below).

      **The mirror-image defect on the READING side is FIXED (2026-08-01).** We inferred another
      engine's blind-append from action shape, which errs the UNSAFE way: an `INSERT … SELECT` from the
      same table emits only adds yet plainly read, so we exempted a commit we should have checked.
      `ConflictChecker` now consumes `commitInfo.isBlindAppend` when the writer declared it and infers
      only when it is absent. Full record: [ew-master-migration.md](ew-master-migration.md) §isBlindAppend.

   **The fix is to emit `isBlindAppend` — and it must be TRUTHFUL, not convenient.** Delta's definition
   is *the transaction read nothing* (`readPredicates.isEmpty && readFiles.isEmpty`), not "the commit
   contains only adds". Deriving it from action shape would mark an `INSERT … SELECT` from the same
   table as blind, and a wrong `true` makes OTHER engines **skip** a check they should run — the unsafe
   direction. Our buffered transaction already tracks a read set for its own OCC check, so the
   information exists; derive it from there. Not yet built.

   **⇒ HOW MUCH emitting it would actually buy, settled from the source (2026-08-01).** The same file's
   isolation dispatch decides which list the predicate check even runs on:

   ```scala
   val addedFilesToCheckForConflicts = isolationLevel match {
     case WriteSerializable if !currentTransactionInfo.metadataChanged =>
       winningCommitSummary.changedDataAddedFiles                                   // empty if we declare blind
     case Serializable | WriteSerializable =>
       winningCommitSummary.changedDataAddedFiles ++ winningCommitSummary.blindAppendAddedFiles
     case SnapshotIsolation => Seq.empty
   }
   val fileMatchingPartitionReadPredicates =
     getFirstFileMatchingPartitionPredicates(addedFilesToCheckForConflicts)
   ```

   | table's level | today (no flag) | with a truthful `isBlindAppend: true` |
   |---|---|---|
   | `WriteSerializable` | our adds are `changedDataAddedFiles` ⇒ examined ⇒ **ABORT** (measured above) | list is EMPTY ⇒ **Spark COMMITS** |
   | `Serializable` | examined ⇒ **ABORT** (measured above) | blind appends examined too, BY DESIGN ⇒ **still aborts, correctly** |

   So the write half fixes the `WriteSerializable` case ONLY, and the `Serializable` abort is not a
   defect at all — it is what the level means. Claim that scope, not "it fixes the interop aborts".

   **This also retires a planned experiment.** The worry (from upstream PR #24) was that a whole-table
   read declaration might conflict even with a blind append, which would have made the write half
   worthless; the plan was to re-run the A/B with a prunable predicate to find out. The source shows the
   question is malformed: on the `WriteSerializable` path the list handed to the predicate matcher is
   already empty, so **predicates are never consulted** and how broad the reader's declaration was cannot
   matter. The exemption is applied one step EARLIER than the predicate comparison, not dodged by it.
   The experiment was therefore **not run**.

   Still a PREDICTION, not a measurement: the live A/B must be re-run once the flag is emitted.

   **Method note, because this experiment was void FOUR times before it measured anything.** Each void
   run looked like a clean result ("no conflict"). The window has to be *proven*, not assumed: the
   verdict here is Spark naming the concurrent version in its own error, or `readVersion` ordering
   showing our append landed between the DELETE's read and its commit. What kept failing was our end,
   not Spark's — the append needed ~20 s (process start, CLR boot, ATTACH discovery), most of the
   DELETE's lifetime. Fixes that finally worked: pre-attach the writer so firing costs only the commit
   (~13 s), and make the DELETE genuinely expensive (`id % 7 = 3` rewrites nearly every file — minutes)
   instead of a ~200-row delete that finished in under 17 s.

We do **not** block or rewrite a `WriteSerializable` stamp: the value is functional and honored
cross-engine, so refusing it would remove a working capability to guard against a Spark **DDL**
limitation that costs the user nothing at read or write time.

### 10.6a What this changed (2026-08-01) — the default flipped, and the auto-stamp is GONE

Consequence 1 above is a silent, engine-dependent weakening: on a table that declares nothing, the
guarantee depended on which engine happened to write. Two changes.

**1. The catalog default is now `serializable`** (was `write_serializable`), matching Fabric Spark, so
silence now means agreement. Explicit `isolation_level 'write_serializable'` selects the old behaviour,
and a table's own `delta.isolationLevel` still overrides the catalog either way.

**⚠ Breaking, and the biggest practical effect is NOT the blind-append rule — it is that
[row-level concurrency](#104-concurrent-dml-on-the-same-file--parity-row-level-concurrency-v1-2026-07-14)
is a WriteSerializable-ONLY relaxation.** Under `serializable` the strict file-level checks apply, so
concurrent disjoint-row DML on the same file conflicts where it used to compose. That is what three
suites caught the moment the default moved. Single-writer behaviour is unchanged. **If you rely on
concurrent DML composing, attach with `isolation_level 'write_serializable'`** — one option, same
behaviour as before.

**2. A CREATE no longer stamps `delta.isolationLevel` at all.** It used to bake the catalog's ATTACH
level into the table. That conflates a per-catalog *behaviour knob* with a durable per-table
*declaration*, and since the property **wins over any catalog** (§10.6, `PendingSerializable`), the
stamp made an attach-time choice permanent and silently overrode a *different* catalog's explicit
setting on the same table later. Measured directly: with the stamp in place, attaching one path twice
at two levels stopped honouring the second — the exact composition our own level-contrast suites rely
on. So the stamp was not merely redundant, it broke a working capability.

Declaring a level is now explicit and per-table:

```sql
CREATE TABLE lake.main.t WITH ("delta.isolationLevel"='WriteSerializable') AS SELECT …;
SELECT * FROM lake.delta.set_tblproperties('main.t',
                                                 '{"delta.isolationLevel":"WriteSerializable"}');
```

That is the spelling to use when another engine must honour the looser level, and it works: Fabric
Spark refuses to *set* `WriteSerializable` via its own DDL but **honours** it when it is already on the
table (both measured). Trade-off: such a table's level then cannot be changed *from* Spark — use
`delta.set_tblproperties`.

Gate: `verify_delta_tblproperties` (58) pins the default itself, that neither level auto-stamps, and
that the explicit `WITH` still lands in commit-0. It has to pin the default directly, because every
other isolation assertion in the tree now states its level explicitly — otherwise a regression in the
default would fail nothing.

**3. The ATTACH option is now the fallback EVERYWHERE — it was not.** The precedence "table property
wins, catalog is the default when the table is silent" held in the buffered/explicit-transaction path
(`PendingSerializable`) but **not** in the autocommit rowid DELETE, which read the catalog flag
directly. So `delta.isolationLevel = Serializable` + `ATTACH … isolation_level 'write_serializable'`
behaved *inconsistently on one table*: strict inside `BEGIN..COMMIT`, row-level-relaxed for a bare
`DELETE`. Both now route through one `EffectiveSerializable`, so no attach-time option can outrank a
table's own declaration.

The old defence was that a single autocommit statement has no cross-statement reads to serialize, so
the flag is "only a resilience knob". That is true about the isolation *semantics* and beside the point
about the *contract*: once a table declares Serializable, a local option must not weaken it.

**Not covered by a test, and that is why it survived.** `rowLevelRetry` only changes behaviour when
that statement's own commit hits a concurrent DV change, and sqllogittest runs connections
**sequentially** — a bare autocommit DELETE has no window between its scan and its commit for another
connection to act in. Every scenario in `verify_delta_row_level_concurrency` therefore drives the
*buffered* path (con1 `BEGIN` pins, con2 commits, con1's flush sees it). Exercising the autocommit path
needs true concurrency, i.e. separate processes (`scratchpad/iso_race.sh`), so the fix rests on review;
the suite carries a note saying so rather than pretending coverage.

While fixing it: `ExecuteDelete` now reads the table configuration **once** and derives both
`enableDeletionVectors` and the isolation level from it. Each helper otherwise opens the table
separately, so naively adding the isolation read would have cost a second `_delta_log` LIST per DELETE
on OneLake/S3 — this is one open where there were already going to be two.

### 10.7 Cross-TABLE atomicity — CAVEAT ON OUR SIDE

```sql
BEGIN;
INSERT INTO lake.main.a VALUES (1);
INSERT INTO lake.main.b VALUES (2);
COMMIT;   -- one Delta commit per TABLE, flushed sequentially
```

Delta has no multi-table commit: each table's flush is atomic, but a crash between the two flushes
leaves `a` committed and `b` not. Classic Databricks has the same limitation (worse — per
statement); their UC coordinated-commits preview is the only mechanism that spans tables.

---

## 11. deltars provider (out of scope here)

The `deltars` provider has **no transaction buffer**: every statement is its own delta-rs commit
regardless of `BEGIN..COMMIT`, ROLLBACK does not undo them, DELETE/UPDATE are copy-on-write MERGEs
(never DVs), and snapshot pinning does not apply. Use `engineeredwooddelta` when you need
transactional semantics.
