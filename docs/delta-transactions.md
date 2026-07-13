# Delta provider — transaction, concurrency & isolation semantics

Reference for the **engineered-wood Delta provider** (`PROVIDER 'engineeredwooddelta'`, aliases
`'delta'`/`'deltalake'`): how DuckDB transactions map onto Delta commits under every combination of
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

ATTACH options `native_read` / `native_write` (both default **off**) select the *byte source* for
data files; they do **not** change transaction semantics. The transaction machinery (buffer, pin,
flush, rebase) is identical; only the mechanics of "get the rows onto storage / off storage"
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
- **Codec path:** a single statement is one snapshot by construction (one open of the table).
- **Native path:** `SnapshotPinning` fires per statement, giving a consistent cross-table cut for
  multi-table joins within the statement.

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
| CREATE TABLE / CTAS (fresh) | fully buffered — **nothing touches the `_delta_log` until COMMIT**; CTAS data streamed to the (log-less) table folder | column-mapping physical names pre-assigned at buffer time (random GUIDs — assigned once, files depend on them); partitioned CTAS streams Hive layout |
| CREATE + identity marker | buffered; INSERTs generate real ids at statement time from the parked schema, marks chain; flush bakes final HWM into commit-0 | |
| DELETE | rowids decoded to (pinned ordinal → positions), parked in `DeletedByOrdinal`; flush emits DV remove/add pairs | requires **deletion vectors enabled** on the table |
| DELETE of rows inserted in the same txn | works — positions keyed on the pending file's ordinal (≥ `0x780000`); flush builds an **inline DV born on our own add** | in-memory batch rows (`0x700000..0x77ffff`) rejected |
| UPDATE | old rows parked as delete positions + post-image rows **eagerly written** as a file; read-back is version-pinned (`atVersion: PinnedVersion`) | requires DV + `SupportsExternalCommit` (not identity/IcebergCompat); on row-tracking tables the post-images bake the ORIGINAL stable ids (materialization is implied by row tracking; partitioned works) |
| UPDATE of same-txn rows | **rejected** ("COMMIT the inserts first") | |
| ALTER ADD/RENAME/DROP COLUMN, nested ADD/DROP FIELD | metaData (+ protocol upgrade) computed at statement time, parked; joins the fused commit; overlays serve the pending schema mid-txn | ALTERs must precede the txn's data changes; nested RENAME FIELD stays immediate; RENAME of a partition column rejected in a txn |
| RENAME TABLE | immediate (physical folder move) — **except** a pending-created table, which re-keys the buffer + moves the eagerly-streamed files (the dbt tmp-swap case) | |
| CREATE OR REPLACE, DROP, OPTIMIZE, VACUUM | **immediate**, never buffered; rejected with "uncommitted buffered changes" if the table has pending changes | replace removes are snapshot-coupled; DROP/VACUUM are physical |
| CDF tables (any DML/append) | the statement's `_change_data` files are written eagerly (split per partition, partition columns excluded from the bytes) and the cdc actions parked — **including plain inserts** (a commit carrying any cdc action is read cdc-only, so appends fused with DML would otherwise vanish from the feed) | DML-after-buffered-ALTER on CDF rejected; identity×CDF in a txn rejected |
| `arrownet_delta_set_transaction_version(…)` | parks the app-transaction version; flush CASes it against the latest snapshot and emits the spec `txn` action atomically with the fused commit (idempotent-append protection) | requires an explicit transaction |

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

### Conflict detection = Spark ConflictChecker parity (file/action-level)

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

| | `'write_serializable'` (**default** — Spark's default) | `'serializable'` |
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

Not undone by ROLLBACK (because they were immediate): DROP, OPTIMIZE/VACUUM, CREATE OR REPLACE,
RENAME TABLE of a *committed* table, nested RENAME FIELD, and the path-targeted `COPY (FORMAT
delta)`. The C++ side calls `InvalidateAllEntries()` on rollback so no stale schema survives a
rolled-back ALTER.

---

## 8. Multi-writer safety by storage backend

The commit primitive is put-if-absent on `_delta_log/<version>.json`. Whether that guard is real
depends on the filesystem:

| Storage | Commit guard | Verdict |
|---|---|---|
| Local POSIX | `O_EXCL` exclusive create | multi-process safe (validated: 4 processes × 200 rows → 800/800) |
| OneLake / abfss (`onelake://`) | ADLS conditional create (`If-None-Match: *`) | multi-process / multi-engine safe |
| Fabric fuse mount (`/lakehouse/default`) | `O_EXCL` over fuse — doubtful | treat as **single-writer** |
| S3 plain ATTACH (httpfs) | **none** — httpfs never sends `If-None-Match` | documented **single-writer** |
| S3 ATTACH **with an s3 `SECRET`** | real conditional PUT via the AWS SDK (`S3CommitFileSystem`: Get(temp) → Put(target, `If-None-Match:"*"`) → Delete(temp); 412 → conflict → OCC/rebase) | multi-process / multi-engine safe (validated on MinIO: 4 × 10 commits × 20 rows → 40/40, 800/800, across checkpoint boundaries) |

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
`CALL arrownet_delta_set_transaction_version(catalog, 'schema.table', app_id, version [, expected_previous])`
inside the explicit transaction — the flush compare-and-swaps against the latest snapshot's
`AppTransactions` on every retry attempt and emits the spec `txn` action atomically with the fused
commit; a duplicate retry fails the CAS ("transaction version conflict") instead of duplicating
data. Read the committed high-water mark with `arrownet_delta_get_transaction_version(…)`.

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

## 10. deltars provider (out of scope here)

The `deltars` provider has **no transaction buffer**: every statement is its own delta-rs commit
regardless of `BEGIN..COMMIT`, ROLLBACK does not undo them, DELETE/UPDATE are copy-on-write MERGEs
(never DVs), and snapshot pinning does not apply. Use `engineeredwooddelta` when you need
transactional semantics.
