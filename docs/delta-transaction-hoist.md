# Hoisting the EW `DeltaTransaction` to statement time

> **Status: PLAN, nothing built.** Decided 2026-08-04 (user). Supersedes the "one open EW transaction
> per table is an ARCHITECTURAL change" framing in `CLAUDE.md` — it is smaller than that, see §1.

## 1. What this actually is — a HOIST, not an adoption

**We already use `DeltaTransaction`.** `FlushDmlTransactionAsync` calls
`table.StartTransaction(pinnedSnap, isolation)` (`DeltaCatalog.cs:3661`) at **COMMIT** time and then stages
everything the buffer accumulated during the DuckDB transaction: `StageDataFilesAsync`,
`StageRowDeletesAsync`, `StageSchemaChange`, `RequireAppTransaction`, `DeclareRead`,
`DeclareWholeTableRead`. The proposal moves that **one line earlier in the lifecycle** — create the
transaction at the FIRST WRITE to a (DuckDB txn, table) pair and hold it — so per-statement work stages
directly into it instead of into our own parking structures.

Two earlier estimates were wrong and are corrected here:

- **It is not "replace the buffering with `DeltaTransaction`".** Most of `PendingAppends` serves
  READ-YOUR-WRITES, which `DeltaTransaction` cannot serve at all: its `Snapshot` property is
  `_baseSnapshot`, the state at transaction start, so every `Stage*` is invisible until `CommitAsync`.
  The overlay stays ours.
- **It does not add a snapshot pin.** The flush already pins `pinnedSnap` — OUR per-txn
  `SnapshotPinning` snapshot, established at first touch, deliberately NOT `CurrentSnapshot` (the
  comment at `:3643` explains why: DV positions are keyed to that version's file ordinals and the commit
  must be validated against everything since). An early transaction pins **the snapshot we already
  pinned**, so there is no new cost and no change to the base version.

## 2. Why do it — it retires an upstream ask, and that is the main prize

With a transaction alive at statement time, `StageChangeDataAsync` becomes callable, so our 45-line
`WriteChangeDataFilesAsync` duplicate retires. **All three objections `CLAUDE.md` records against
`StageChangeDataAsync` are consequences of the buffering, not properties of the API:**

| recorded objection | under the hoist |
|---|---|
| it is a method ON `DeltaTransaction`, and the buffered path has none at statement time | this is precisely what the hoist fixes |
| deferring to flush would hold pre/post-image ROWS in memory until COMMIT | gone — CDF parquet is written at statement time; only actions are held |
| it RETURNS NOTHING, but we need the `CdcFile` list for `pending.PendingCdc` | gone — there is no `pending` to park on; the transaction holds the actions |

⇒ **The `WriteChangeDataFilesForAsync` public-overload offer must NOT be sent before this decision.**
It exists to work around our own architecture. Spending upstream credibility on a request we can
delete ourselves is the mistake already recorded for `RowUpdateMode`.

Secondary gains: the flush's hand-rolled action assembly, its declaration wiring, and
`FlushCreateTransactionAsync` all shrink or disappear.

## 3. What is accepted, explicitly (user, 2026-08-04)

Lakehouses are not SQL warehouses; Databricks does not allow DDL in multi-statement transactions at
all. So:

- **v0 becomes visible for the transaction's life.** A concurrent session can see an empty table.
  ⚠ Note this is a **quantitative** loss, not a new class of anomaly: today's flush already emits v0
  (empty) then v1, so a reader can already land between them — the hoist widens that window from two
  adjacent log writes to the transaction's life.
- **ROLLBACK of a table created in the transaction becomes a best-effort DROP.** Consent is present
  (the user typed ROLLBACK, on a table their own transaction created), which is what distinguishes this
  from the autocommit-CTAS compensation refused in
  [delta-transactions.md](delta-transactions.md) §7.1.
- ⚠ **This is a REGRESSION on a shape that is clean today.** `BEGIN; CREATE; INSERT; ROLLBACK` currently
  leaves NOTHING (the create is buffered, `_delta_log` untouched — gate at `DeltaCatalog.cs:2148`).
  After slice 5 it leaves an empty table when the drop fails. Suites assert the current behaviour, so
  slice 5 must change them **deliberately and alone** — never as a side effect of an earlier slice.

## 4. Slices — value first, the behaviour change last and isolated

Each slice keeps every suite green on its own; the floors move only where a slice adds coverage.

**⚠ Two corrections to the first draft of this slicing, both found by writing slice 1 out rather than by
reviewing it. They change the order, so read them before starting.**

- **CDF CANNOT MOVE FIRST, and "smallest contained consumer" was the wrong criterion.** If CDF stages
  into a held transaction while data files and DV deletes still stage into the flush's own transaction,
  ONE table gets TWO commits — destroying the single-atomic-commit property the buffer exists to
  provide. Whatever moves, **all of a table's actions must live in the same transaction**, so the
  held transaction has to become *the* transaction the flush uses before any staging moves.
- **A holder that unconditionally opens a table is a COST REGRESSION, not a no-op.** Slice 1 was
  written as "plumbing, nothing routed through it", but the transaction needs a live `DeltaTable`, so
  creating one at first write while the flush still opens its own adds a `_delta_log` LIST per
  (txn, table) — paid on OneLake/S3. A slice whose gate is "byte-identical assertions" would have
  passed while shipping that.

### 4.0 FEASIBILITY IS CONFIRMED — the groundwork already exists

The biggest unknown was whether a `DeltaTable` can be held across ABI calls at all. **It can, and the fix
that made it possible is already in** (`142b350`): the host-FS opener is a `ClientContext*` valid only for
the duration of one ABI call, so `DuckDbTableFileSystem` capturing it was *"the reason a `DeltaTable`
cannot be held open ACROSS calls"* — a use-after-free, not a staleness bug. It now reads
`AmbientOpener.Current` first and keeps the captured value only as a fallback, and its own comment says
the change *"becomes load-bearing the moment something is cached."* This is that moment.

Checked all three `ITableFileSystem` implementations, because one exception would sink the design:

| implementation | opener |
|---|---|
| `DuckDbTableFileSystem` | reads `AmbientOpener.Current` per call, captured value as fallback ✅ |
| `AdlsGen2TableFileSystem` | none at all — SDK credentials ✅ |
| `S3CommitFileSystem` | reads `AmbientOpener.Current` dynamically, delegates the rest to the opener-safe inner FS ✅ |

### 4.1 Slice 1 SPLITS — separate "who owns the transaction" from "when is it created"

Only the second half can change behaviour, so they must not land together.

- **1a — ownership refactor, behaviour-identical BY CONSTRUCTION.** Extract the flush's
  `TableFileSystems.Create` → `OpenAsync` → `StartTransaction` sequence into an
  `EnsureHeldTransaction(...)` that parks the (table, transaction, pinnedSnap) triple on the buffer entry,
  and have `FlushDmlTransactionAsync` obtain it from there. Still CALLED from the flush, at the same
  moment as today, so nothing observable moves. **Two constraints that must survive the move, both
  currently expressed by the flush's scoping:**
  - **DISPOSAL ORDER is load-bearing.** `await using var txn` is declared INSIDE the try so it runs
    BEFORE the `finally` disposes the table — the transaction's cleanup needs the table's filesystem.
    Moving disposal to commit/rollback must keep txn-then-table.
  - **`await using` is what ABORTS a flush that does not commit**, reclaiming what EW's own writers
    staged — measured: a buffered DELETE whose commit is refused otherwise leaves a
    `deletion_vector_*.bin`, because `StageRowDeletesAsync` writes the vector at STAGING time, before the
    precondition is judged. Whatever replaces the `await using` must abort on every non-committing path,
    including an exception out of the flush. ⚠ Safe only from EW #49 — do not reintroduce it any earlier.
- **1b — move the creation point to the first operation that needs one.** Then, and only then, the
  behaviour question arises (a transaction alive across statements).
  - **⚠ 1b ALONE IS VACUOUS AND SHOULD MERGE WITH SLICE 2** (established 2026-08-04, after 1a landed).
    Creating the transaction earlier buys nothing while every `Stage*` still happens at flush time — the
    only observable difference is that an object exists sooner. The reason they were separated (CDF in a
    held transaction while data files went to the flush's own ⇒ two commits per table) **was dissolved by
    1a**: the held transaction IS the flush's transaction now, so statement-time CDF and flush-time data
    files fuse into one commit. Do 1b+2 as one slice.

**THE SAFETY PROOF FOR THE WHOLE HOIST — holding a table across statements does NOT weaken conflict
validation.** This was the open worry, since the flush's own comment says basing the transaction on
`CurrentSnapshot` would make validation *"vacuous"*, and a long-held table's `CurrentSnapshot` is stale by
construction. It does not matter: **`CommitOccAsync` writes OPTIMISTICALLY at `baseSnapshot.Version + 1`
and re-reads the latest only when that conditional write throws `DeltaConflictException`.** Conflict
detection is therefore driven by the put-if-absent commit primitive plus `transaction.BaseSnapshot` (our
pin) — nothing in the commit path consults `table.CurrentSnapshot`. So a table opened at statement time and
held to COMMIT validates exactly as one opened at COMMIT.
- ⚠ Unchanged pre-existing caveat: where the commit primitive is NOT conditional — a local Windows root
  ([delta-transactions.md](delta-transactions.md) §8.5) — concurrent writers lose commits regardless. The
  hoist neither causes nor fixes that.

**⚠ THE ABORT LEDGER IS EXPLICIT, NOT AMBIENT — so the hoist buys NO free orphan reclamation.** A first
version of the 1a comment claimed that a transaction created before the flush's `WriteDataFilesAsync`
would collect those files into its abort ledger. **False, and checked rather than reasoned:**
`WriteDataFilesAsync(batches, ct, schemaOverride, identityValuesPreGenerated, materializedRowIds)` has no
`written:` parameter at all, and the ledger is threaded EXPLICITLY by the callers that opt in (which is
what `StageChangeDataAsync` does via `written: _written`). `StartTransaction` is likewise only
`new DeltaTransaction(this, snapshot, level)` — it registers nothing on the table and installs no ambient
state.

Consequences, both of which matter beyond slice 1a:

- Creating the transaction earlier *within one call* is a genuine no-op. The reason 1a does not do it is
  **discipline** — byte-identity is the claim, and "looks free" is not "is free" — not a mechanism.
- **A file written straight through the table is never reclaimed by an abort, at any slice.** That is why
  `DiscardDataFilesAsync` exists as a separate verb and why `RollbackTransaction` calls it independently.
  Do not plan a later slice on the assumption that holding a transaction makes our eagerly-written data
  files self-cleaning; only what EW's own writers stage (a deletion vector, a CDF file) comes back.

1. **Hoist creation AND make the flush use it.** A per-(txnId, tablePath) holder whose transaction is
   created **lazily on the first operation that NEEDS one** — a DV delete, schema change, CDF write or
   app-txn requirement, i.e. exactly the condition at `DeltaCatalog.cs:2592` — so the plain-append fast
   path (`FlushDeferredFiles`, which uses no transaction and has its own OCC retry) is left exactly as
   it is. `FlushDmlTransaction` then uses the HELD transaction instead of calling `StartTransaction`
   itself. Staging all still happens at flush time. Gate: hermetic + service byte-identical to
   `7ac6662`.
   - ⚠ **The "one fewer table open" wording above was wrong for 1a as built, and would have been
     reported as a gain that does not exist.** It was written for a version of slice 1 that hoisted
     creation and flush-usage together. **1a is IO-NEUTRAL**: the flush opens the table exactly once, as
     before — the only change is that the buffer entry owns it, so it is disposed by
     `CommitTransaction`'s per-table `finally` instead of the flush's own. The "one fewer" framing was
     about avoiding a REGRESSION (a holder that opens while the flush also opens), not achieving a
     reduction. A real reduction only arrives at 1b, when statement-time work reuses the held table
     instead of opening its own.
2. **CDF onto `StageChangeDataAsync`**, now safe because there is only ever one transaction per table.
   Retires `WriteChangeDataFilesAsync` (45 lines) and the upstream offer. Gate:
   `verify_delta_catalog_changes` + the CDF sections of the transactions suite.
   - **⚠ It does NOT mean buffering CDF ROWS** (asked 2026-08-04, and the answer is the reverse).
     `StageChangeDataAsync(rows, changeType, …)` writes the `_change_data` parquet IMMEDIATELY
     (`WriteChangeDataFilesForAsync`) and only then files the small `cdc` actions via `StageInternal`. So
     rows are eager in both designs — today via our own 45-line writer, after via EW's — and the hoist
     actually holds LESS, because `pending.PendingCdc` goes away. The `CLAUDE.md` objection this retires
     is the mirror image of the worry: it argued against DEFERRING the call to flush precisely because
     that would hold the pre/post-images until COMMIT.
   - **A CAPABILITY GAIN, not just a deletion: this slice can close the CDF row-identity gap.**
     `StageChangeDataAsync` takes `rowIds` + `rowCommitVersions`, and our feed leaves identity NULL
     today. A `cdc` action has no `baseRowId`, so the change file is the only place a change row's
     identity can live. ⚠ Two traps in one signature: omitting `rowIds` silently yields NULL ids (no
     error), and omitting `rowCommitVersions` defaults every row to the COMMITTING version — correct for
     a post-image, **wrong for a pre-image**. So the pre-image call must pass the version each row was
     last changed in, which is what a `DeltaRowMetadata.RowTracking` read reports.
3. **Data files + row deletes stage at statement time** (`StageDataFilesAsync` /
   `StageRowDeletesAsync`). The buffer KEEPS its `Files` / `DeletedByOrdinal` copies — they feed
   read-your-writes. Gate: transactions 944, update 63, delete 28, row-level concurrency 93.
4. **Schema changes + conflict declarations** (`StageSchemaChange`, `DeclareRead`,
   `DeclareWholeTableRead`, `RequireAppTransaction`) move to the statement that causes them. Gate:
   nested_alter 100, txn_version 65, row-level concurrency 93.
5. **CREATE becomes immediate; `FlushCreateTransactionAsync` + `PendingCreate` deleted; best-effort drop
   on rollback.** The behaviour-changing slice. Gate: the transactions suite WITH its rollback
   assertions rewritten, plus a new assertion that a rollback of a created table leaves no table when
   the drop succeeds and names the orphan when it does not.

## 5. Open questions to settle IN slice 1, not by guessing

- **Commit ordering across tables.** One transaction per table means N commits at COMMIT, non-atomic
  across tables — same as today (Delta has no cross-table transaction), but the FAILURE shape changes:
  today a flush failure on table 2 leaves table 1 committed, and that stays true. Confirm no suite
  depends on the current interleaving.
- **`SnapshotPinning` vs the transaction's own base snapshot.** They should be the same object; assert
  it rather than assume, because a divergence silently changes what the conflict check validates
  against.
- **Isolation resolution timing.** `EffectiveSerializable` is resolved from table config today at flush
  time; at first-write time the config read is the same one the statement already performs, so this
  should REMOVE a `_delta_log` read rather than add one. Verify.
- **A read-only transaction must not create one.** `HasAny` deliberately excludes reads so a read-only
  entry does not trip pending-changes guards; the holder must follow that rule or every SELECT inside a
  transaction opens and aborts an EW transaction.
