# Hoisting the EW `DeltaTransaction` to statement time

> **Status: slices 1a, 1b+2 and 5 BUILT (2026-08-04); slice 3 is BLOCKED and slice 4 is optional.** Decided 2026-08-04 (user).
> Supersedes the "one open EW transaction per table is an ARCHITECTURAL change" framing in `CLAUDE.md` —
> it is smaller than that, see §1. Built so far: the buffer entry owns the EW table + transaction
> (`0bfdd8c`), CDF stages into it at statement time (`86cb374`), and our 45-line EW
> `WriteChangeDataFilesAsync` is deleted — **the `WriteChangeDataFilesForAsync` upstream offer is now
> RETIRED, as §2 predicted.** §6 is the defect that settling slice 2's mutation question exposed.

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
   - **✅ SHARING THE HELD TABLE IS SAFE — checked 2026-08-04, and it was the last open risk.** The CDF
     path opens its OWN table with `DeltaWriter.Options()`, i.e. WITHOUT the native data-file writer, while
     the held table carries one (the flush's `WriteDataFilesAsync` needs it). That looked like it would
     silently reroute `_change_data` parquet through DuckDB's COPY. It does not:
     `WriteChangeDataFilesForAsync` delegates to `ChangeDataFeed.CdfWriter.WriteAsync`, which writes via
     `fs.CreateAsync` and **never consults `_options.DataFileWriter`**. Change files are always EW-codec
     written. So the CDF path's plain options were never load-bearing for the CDF write itself.
   - **⚠ THE IO WIN OF 1b LIVES HERE, and it is per STATEMENT:** `WriteCdcFilesAsync` opens and disposes
     its own `DeltaTable` on the first non-empty batch of EVERY buffered CDF statement. Reusing the held
     table removes one table open (one `_delta_log` LIST) per such statement — the reduction that slice 1a
     was wrongly credited with.
   - **⚠ THE FLUSH ROUTING CONDITION MUST CHANGE WITH IT** (`DeltaCatalog.cs:2593`). It currently routes a
     table to `FlushDmlTransaction` when `pending.PendingCdc.Count > 0`, among others. Once CDF actions go
     straight into the transaction, `PendingCdc` is empty and a CDF-only statement would fall through to
     the plain-append path — losing the cdc actions entirely, silently. Replace that disjunct with
     `pending.HeldTxn is not null`, which is the honest signal ("something staged into a transaction").
     This is the one part of the slice that fails SILENTLY if missed, so gate it with a CDF-only buffered
     statement (a buffered INSERT on a CDF table, which writes its cdc counterpart and nothing else).
     - **⚠ SETTLED 2026-08-04 — THE MUTANT IS UNOBSERVABLE, SO THERE IS NO GATE. The change stays, honestly
       labelled DEFENSIVE.** Reverting the disjunct leaves `verify_delta_catalog_changes` (73) and
       `verify_delta_catalog_transactions` (944) green, and that is CORRECT rather than a coverage hole:
       `CdfReader` is all-or-nothing PER VERSION (`if (cdcFiles.Count > 0)` … `else` infer from add/remove),
       so a dropped insert-`cdc` simply hands the version back to inference, which reports the same rows,
       types, versions and timestamps. The one column that WOULD differ — row identity — is not projected by
       `fabricator_delta_changes` at all, so no SQL query can tell the two apart. A gate written anyway would
       have passed for a reason unrelated to what it claimed to check.
       - It is still the right change: it becomes observable the moment identity is passed (§6) or exposed,
         and "the actions I staged reach the commit" is not a property to leave resting on inference.
   - **Scope note: retiring our 45-line `WriteChangeDataFilesAsync` is an ENGINEERED-WOOD change** — DONE
     2026-08-04, its own commit on `fabricator-patches` plus the pin move. It was a **pure addition** in the
     diff against `upstream/main` (plus one stray blank line, deleted with it), so the removal restores
     upstream byte-for-byte in that region and drops `DeltaTable.cs` divergence **183 → 137** lines.
     EW Table.Tests **868 × {net10.0, net8.0, net472}** — the pin's recorded count, UNCHANGED, which is the
     proof nothing covered the method: upstream's public singular `WriteChangeDataFileAsync` has seven test
     references, our public plural had **zero**, in EW or the Bridge.
   - **A CAPABILITY GAIN, not just a deletion: this slice can close the CDF row-identity gap** — and
     measuring the routing mutation showed the gap is **worse than "identity is NULL"**: it is a DIVERGENCE
     between our own two paths, and the cheap-looking fix is unsafe. See §6.
3. ~~**Data files + row deletes stage at statement time**~~ — **⚠ BLOCKED, BOTH HALVES, against today's EW
   API. Established 2026-08-04 by reading the callee before writing the slice** (the rule that has paid
   every time on this work). The buffer's per-transaction MERGE — `Files` as one list, `DeletedByOrdinal` as
   one dictionary — is doing load-bearing work that `DeltaTransaction` does not do:
   - **Adds cannot move, because of BORN-DELETED rows.** `bornDeleted` is a PARAMETER of the
     `StageDataFilesAsync` call, so it must be known when the add is staged. But a row inserted by statement
     1 and deleted by statement 3 of the same transaction is exactly what it encodes (ordinal
     `PendingFileOrdinalBase + idx`, `DeltaCatalog.cs:3654`) — the add is born with an inline DV so those
     rows never reach a committed version. Staging statement 1's add immediately forecloses statement 3.
     There is no later route either: `StageRowDeletesAsync` resolves paths through
     `ActiveFilesByPath(_baseSnapshot)` and **throws `StaleSelectionPath` for a path the base snapshot does
     not hold** — which a file staged by this very transaction never is.
   - **Deletes cannot move either, because DV computation does not see staged actions.**
     `ComputeDvActionsWithEditsAsync` reads `addFile.DeletionVector` from the BASE snapshot only. Two
     `StageRowDeletesAsync` calls naming the same committed file — two DELETEs in one transaction, or a
     DELETE and an UPDATE — would each compute from the base DV, so the second's vector omits the first's
     rows and **the first delete's rows come back**, plus two remove/add pairs for one path. Today they are
     merged into one call by `DeletedByOrdinal`.
   - ✅ **One thing that is NOT a blocker, so do not "fix" it:** multiple `StageDataFilesAsync` calls on one
     transaction are explicitly supported — `DeltaTransaction._nextRowId` exists precisely so two staged
     appends do not reserve the same stable row ids. The obstacle is the born-deleted parameter, not
     repetition.
   - ⇒ Moving either half needs a NEW EW surface (a DV edit against a file staged in the same transaction,
     which is also what would let the two accumulate). **That is worth noting against the hoist's own
     premise:** the hoist existed to REMOVE an upstream ask, and slice 3 would create one. Do not open that
     conversation to buy statement-time staging whose only gain is bookkeeping symmetry — the flush already
     produces one atomic commit per table, which is the property that matters.
4. **Conflict declarations** (`DeclareRead`, `DeclareWholeTableRead`, `RequireAppTransaction`) move to the
   statement that causes them. These are SETS — idempotent, order-free, no cross-statement merge — so they
   are the one part of slices 3–4 that is genuinely unblocked. **`StageSchemaChange` must NOT come with
   them:** a commit may carry only ONE `metaData` action, and the flush deliberately FUSES the eager identity
   high-water mark INTO the buffered ALTER's metaData (`BuildIdentityMetadataAction`, `:3758`) or synthesises
   one when there is no ALTER — a per-statement stage would produce two. Gate: txn_version 65, row-level
   concurrency 93.
   - Value is honestly small (declarations already reach the same transaction), so this is a tidiness slice.
     Weigh it against slice 5, which is the one with user-visible consequence.
5. **CREATE becomes immediate; best-effort drop on rollback.** The behaviour-changing slice. Gate: the
   transactions suite WITH its rollback assertions rewritten, plus a new assertion that a rollback of a
   created table leaves no table when the drop succeeds and names the orphan when it does not.
   - **⚠ ITS VALUE IS UNDERSTATED ABOVE, AND THE UNDERSTATEMENT MATTERS: slice 5 LIFTS TWO SHIPPED
     REFUSALS.** Surveyed 2026-08-04 (17 `PendingCreate` sites). Because the table does not exist on
     storage, `DeltaCatalog.cs:3092` and `:3184` throw **`NotSupportedException`** — *"DELETE/UPDATE on a
     table created in the same transaction is not supported yet"* — so
     `BEGIN; CREATE TABLE t AS SELECT …; DELETE FROM t WHERE …; COMMIT;` fails today. An immediate create
     makes both ordinary DML. It also un-gates the **streaming native write** for such a table
     (`tryStream` at `:2232` and `TryWriteStreamingCoreAsync` at `:3445` both bail on `PendingCreate`) and
     lets the CDF capability probe run (`:2222`, which cannot probe a table that is not there). So this is
     not "a cost we accepted for tidiness" — it is a capability slice whose PRICE is the accepted v0
     visibility. Weigh it that way when deciding order.
   - **⚠ `PendingCreate` must NOT be deleted — it CHANGES MEANING**, and that reframing is what makes the
     slice safe. It currently means *"the create has not happened yet"*; afterwards it means *"this
     transaction created this table"*, which ROLLBACK still needs in order to know what it may drop. So the
     17 sites split in two, and each must be classified before it is touched:
     - **(a) "not on storage yet" ⇒ delete or simplify:** `ScanPendingCreated` and its two callers
       (`:1473`, `:1777`) — reads go through the normal path once the table exists; the write-path gates
       (`:2222`, `:2232`, `:2278`, `:3445`); the DML refusals (`:3092`, `:3184`); and the DROP / RENAME /
       ALTER special cases for an uncommitted table (`:3944`, `:4437`, `:4502`, `:4551`).
     - **(b) "we created it" ⇒ keep:** the rollback drop, and `IF NOT EXISTS` (`:2779`).
   - ⚠ **The two halves cannot land separately.** Making the create immediate WITHOUT the rollback drop
     ships the regression with no mitigation, so §3's accepted trade only holds if both are in one commit —
     which is also why the suite rewrite belongs to that same commit rather than a follow-up.
   - **Three things inside `FlushCreateTransactionAsync` that the immediate create must ACCOUNT FOR, not
     merely relocate** (read 2026-08-04; each would have surfaced mid-implementation):
     - **IDENTITY high-water marks cannot ride commit-0 any more, and that is FINE — it makes the two paths
       uniform.** Today `BakeIdentityMarks(PendingArrowSchema, PendingIdentityHwm)` puts the transaction's
       FINAL chained marks into the create's own schema, which is only possible because the create is
       deferred until every statement has generated its values. With an immediate create, v0 carries the
       base marks and the flush's `metaData` action updates them — **exactly what already happens for a
       table the transaction did not create** (`BuildIdentityMetadataAction`, `:3758`). So the change is
       "stop special-casing", not "lose a guarantee". ⚠ But it IS observable: v0 of a created identity table
       would no longer show the final mark.
     - **`preAssignedSchema` / `PendingDeltaSchema` may become unnecessary — check, do not assume.** It
       exists because eagerly-streamed CTAS files are written against a pre-assigned column-mapping schema
       (physical names are random GUIDs) that the deferred create must then REUSE rather than re-assign.
       An immediate create assigns that schema FIRST and the streaming path reads it off the table, which
       inverts the dependency. The ordering holds — `FabricatorPhysicalCreateTableAs` creates before
       `begin_bulk` — so the table exists before the first batch.
     - **The concurrent-create guard MOVES AND IMPROVES.** `TableExists(tablePath)` at flush turns a
       concurrent create into a clean *"rolled back; retry it"* at COMMIT. With an immediate create the race
       is decided by commit-0 itself, i.e. by the put-if-absent primitive, at the CREATE statement — earlier
       and by the storage layer rather than by a TOCTOU check. ⚠ On a backend where that primitive is not
       conditional ([delta-transactions.md](delta-transactions.md) §8.5 — a local Windows root) it is
       therefore WEAKER than today's explicit probe, which is a genuine trade to state rather than a pure win.

### 4.2 WHERE THE HOIST STANDS — the prize is banked, and the rest is smaller than the slicing implies

Worth stating plainly so nobody reads slices 3–5 as remaining value:

| slice | state |
|---|---|
| 1a, 1b+2 | **DONE.** Statement-time transaction; CDF stages into it; our EW duplicate deleted; the upstream offer retired; one fewer `_delta_log` LIST per buffered CDF statement |
| 3 | **BLOCKED** on EW surfaces, both halves — and unblocking it would CREATE an upstream ask, which is the opposite of the point |
| 4 | declarations only; genuinely possible, honestly low value. `StageSchemaChange` excluded (one `metaData` per commit) |
| 5 | the behaviour change (CREATE immediate + best-effort rollback drop). Independent of 3 and 4, and the only one a USER can see |

⇒ **Slice 5 is the next one worth doing, and it does not depend on 3 or 4.** The reason the original order put
it last was blast radius, not prerequisites.

### 4.3 SLICE 5 AS BUILT (2026-08-04) — six things the design got wrong or under-specified

Gate: `verify_delta_catalog_transactions` **944 → 965**. Every correction below came from RUNNING it, not
from review; the design in §4.1 was already the product of two reading passes.

1. **⚠ THE CREATE FOR A CTAS DOES NOT GO THROUGH `DeltaCatalog.CreateTable` — it happens inside
   `begin_bulk` with `create=true`.** The design (and a `CLAUDE.md` line about
   `FabricatorPhysicalCreateTableAs` creating before `begin_bulk`) assumed one entry point, so the ownership
   mark was set in `CreateTable` alone and never fired for a CTAS. **Diagnosed by the ABSENCE of the log line
   the change adds**, next to a `delta bulk … create=True` — a positive control that cost nothing to build
   and settled in one run what reading had got wrong. Symptom: three versions instead of two
   (`:1687`), because the bulk created the table AND committed its data immediately, and the later INSERT
   flushed separately. The immediate create is therefore performed in BOTH places, schema-only in the bulk.
2. **⚠ THE SILENT-CORRUPTION GUARD IS `createdHere` NEUTRALISING `overwrite`.** A CTAS arrives with
   `createTable=true`, which makes `mode = Overwrite`, which makes the write NON-bufferable (`bufferable`
   requires Append) — so `BEGIN; CREATE TABLE t AS SELECT …; ROLLBACK` would COMMIT its data immediately.
   **Every existing suite would still have passed**: they assert the ROWS, and the rows are right. A table
   this transaction just created is empty, so there is nothing to overwrite.
3. **⚠ THE DROP GUARD'S ORDER AND CONDITION ARE BOTH LOAD-BEARING.** A first version removed the buffer
   entry UNCONDITIONALLY and BEFORE `ThrowIfPendingAppends`, which made `BEGIN; DELETE …; DROP TABLE …;`
   SUCCEED — the guard found nothing left to complain about. Caught by `:339`, which exists for that shape.
   CREATE+DROP still cancels out for the buffer, but the real drop must now actually run.
4. **⚠ THE "TWO REFUSALS LIFTED" CLAIM WAS AN OVER-CLAIM — it is ALTER and DELETE, not UPDATE.** Removing
   the create-specific refusals exposes the INDEPENDENT "UPDATE of rows inserted in the same transaction"
   limitation, and every row of a table created in this transaction was necessarily inserted in it, so UPDATE
   still fails — with a different message. DELETE composes because a pending file's add is born with an
   inline deletion vector; UPDATE has no such path. Pinned as a `statement error` with a comment saying which
   guard fires and why, so the next reader does not take it for slice 5 not working.
5. **⚠ TWO BACKEND-SPECIFIC OPERATIONS HAD TO BE EXTRACTED, NOT REIMPLEMENTED** — `RemoveTableFolder` and
   `RenameTableFolder`, each with three branches (ADLS DFS / host-FS / S3 SDK). A hand-rolled
   `HostFs.RemoveDir` in the rollback path would have "worked" on a local root and left an orphan table on
   **every remote one** — Azure's `MoveFile` is unimplemented and httpfs' S3 `RemoveDirectory` fails its own
   per-file remove. This is the class of bug a local-only test tier cannot see.
6. **⚠ RENAMING A CREATED TABLE NEEDS THE BUFFER RE-KEYED AND THE HELD TABLE DISPOSED** — the dbt table
   materialization (`CREATE …__dbt_tmp AS …; RENAME m → …__dbt_backup; RENAME …__dbt_tmp → m`), caught at
   `:2872`. The folder rename is now ordinary, but the buffer is keyed BY PATH and holds the rows plus the
   ownership mark (lose it: the flush writes nothing and the rollback drops nothing), and the held EW
   table/transaction were opened against the OLD path.

**⚠ And one test-quality trap worth carrying: TWO ROLLBACK-DROP ASSERTIONS PASSED VACUOUSLY.** They globbed
`txn_flush2/...` where the attach root is `txn_codec/...`, and a glob on a path that never existed returns 0
— the same answer as a successful deletion. 965 green assertions, two measuring nothing. Both now carry a
POSITIVE CONTROL asserting the folder is non-empty mid-transaction, so the 0 afterwards measures a deletion.

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

## 6. The CDF row-identity gap, as MEASURED — and why the cheap fix is wrong

Found 2026-08-04 while settling the routing mutation, i.e. by chasing a question about a *test*, not about
CDF. Recorded as [known-limitations.md](known-limitations.md) 1.7. All of this is direct `_delta_log` +
change-parquet inspection on a `change_data_feed true, row_tracking true` catalog, not reasoning.

**The finding is a DIVERGENCE between our own two write paths for the same statement:**

| path | commit contains | feed identity comes from |
|---|---|---|
| autocommit `INSERT` | `add` only, **`cdc=0`** | INFERENCE — `add.baseRowId + position`, `add.defaultRowCommitVersion` ⇒ **real ids** |
| buffered `INSERT` (inside `BEGIN…COMMIT`) | `add` **+ `cdc`** | the `cdc` file, whose `__delta_row_id` / `__delta_row_commit_version` are **NULL** |

Measured directly, one table, four versions in sequence: `CREATE` ⇒ v0; autocommit `INSERT` ⇒ v1 `cdc=0`;
autocommit `INSERT` ⇒ v2 `cdc=0`; `BEGIN; INSERT; COMMIT` ⇒ v3 **`cdc=1`**. Rows were already present before
the buffered one, so it is **path-determined, not a first-insert special case** — that ordering is what makes
the experiment discriminating rather than two separate runs would have been. The buffered file's identity columns read NULL beside an `add` carrying `baseRowId:0`,
`defaultRowCommitVersion:1`. So writing the change file **destroys** identity that inference would have
recovered, and the buffered path is strictly worse than autocommit for the same logical statement. This
predates the hoist entirely; the hoist only changes which code writes the file.

**⚠ THE OBVIOUS FIX — "stop writing a `cdc` file for a blind append, let inference do it, like Delta's own
writers" — IS UNSAFE, and the reason is the all-or-nothing rule.** `CdfReader` chooses per VERSION, and a
buffered transaction FUSES statements into ONE commit. Measured: `BEGIN; INSERT; DELETE; COMMIT` on a CDF
table yields a single version carrying `cdc=2, add=2, remove=1`. Drop the insert's `cdc` from that commit
and the delete's `cdc` still suppresses inference for the whole version ⇒ **the inserted row would vanish
from the feed, silently.** ⚠ That last step is DEDUCED from two measured facts (the fused commit shape, and
`CdfReader`'s per-version branch read in source) — the shortcut was never built, so the loss was never
observed. Stated as a prediction, not a measurement. The saving (one less parquet, correct ids for free) is
real for an ISOLATED append and becomes data loss as soon as the transaction contains anything else, which
is common.

⇒ **The fix is to pass `rowIds` + `rowCommitVersions` to `StageChangeDataAsync`, unconditionally.** Two traps
in that one signature: omitting `rowIds` silently yields NULL ids with no error (this bug), and omitting
`rowCommitVersions` defaults every row to the COMMITTING version — right for a post-image, **wrong for a
pre-image**, so the pre-image call must pass the version each row was last changed in, which is what a
`DeltaRowMetadata.RowTracking` read reports. Not built; it is a fidelity fix with its own gate, deliberately
not folded into a slice whose claim is behaviour preservation.
- ⚠ **Any gate for it must assert the PARQUET, not the SQL.** `fabricator_delta_changes` projects only
  `id, val, _change_type, _commit_version, _commit_timestamp` — no identity column — so a SQL-level
  assertion cannot see the bug or its fix. Read `__delta_row_id` out of `_change_data/*.parquet` with
  `read_parquet`, which is how it was found.
