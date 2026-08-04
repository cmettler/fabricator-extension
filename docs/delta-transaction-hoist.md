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

1. **Hoist creation AND make the flush use it.** A per-(txnId, tablePath) holder whose transaction is
   created **lazily on the first operation that NEEDS one** — a DV delete, schema change, CDF write or
   app-txn requirement, i.e. exactly the condition at `DeltaCatalog.cs:2592` — so the plain-append fast
   path (`FlushDeferredFiles`, which uses no transaction and has its own OCC retry) is left exactly as
   it is. `FlushDmlTransaction` then uses the HELD transaction instead of calling `StartTransaction`
   itself. Staging all still happens at flush time. Gate: hermetic + service byte-identical to
   `7ac6662`. Net effect on IO should be **one fewer** table open per flushed table, not one more —
   assert that rather than assume it.
2. **CDF onto `StageChangeDataAsync`**, now safe because there is only ever one transaction per table.
   Retires `WriteChangeDataFilesAsync` (45 lines) and the upstream offer. Gate:
   `verify_delta_catalog_changes` + the CDF sections of the transactions suite.
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
