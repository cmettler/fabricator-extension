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
| `fabricator_delta_set_transaction_version(…)` | parks the app-transaction version; flush CASes it against the latest snapshot and emits the spec `txn` action atomically with the fused commit (idempotent-append protection) | requires an explicit transaction |

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
| OneLake / abfss (`onelake://`) | ADLS conditional create (`If-None-Match: *`) | multi-process safe for DATA (no lost writes measured), but a losing writer can surface an ERROR instead of retrying — see §8.1 |
| Fabric fuse mount (`/lakehouse/default`) | `O_EXCL` over fuse — doubtful | treat as **single-writer** |
| S3 plain ATTACH (httpfs) | **none** — httpfs never sends `If-None-Match` | documented **single-writer** |
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
`CALL fabricator_delta_set_transaction_version(catalog, 'schema.table', app_id, version [, expected_previous])`
inside the explicit transaction — the flush compare-and-swaps against the latest snapshot's
`AppTransactions` on every retry attempt and emits the spec `txn` action atomically with the fused
commit; a duplicate retry fails the CAS ("transaction version conflict") instead of duplicating
data. Read the committed high-water mark with `fabricator_delta_get_transaction_version(…)`.

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
   rejects every value except `Serializable`) — change it with `fabricator_delta_set_tblproperties`.

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
SELECT * FROM fabricator_delta_set_tblproperties('lake', 'main.t',
                                                 '{"delta.isolationLevel":"WriteSerializable"}');
```

That is the spelling to use when another engine must honour the looser level, and it works: Fabric
Spark refuses to *set* `WriteSerializable` via its own DDL but **honours** it when it is already on the
table (both measured). Trade-off: such a table's level then cannot be changed *from* Spark — use
`fabricator_delta_set_tblproperties`.

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
