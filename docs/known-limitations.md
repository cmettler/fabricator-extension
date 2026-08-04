# Known limitations and unverified claims

**Purpose.** One place that answers *"what does not work, and what have we claimed without proving?"* Written
because those answers were scattered across four documents and a commit message, which made the state
unreadable even to the people who produced it.

**⚠ SCOPE — read this or you will misuse the page.** It covers **storage, concurrency and transaction
behaviour**, plus diagnostics for those. It is **NOT an exhaustive list of the extension's limitations** — the
per-area docs in the index own theirs (SQL Server type mapping, DAX, Fabric API coverage, the distribution
SKUs…). **Absence from this page does not mean "no limitation".**

**How to know what DOES work.** The test tiers, not this page. `scripts/run-suites.sh hermetic` (63 runs /
5686 assertions) and `service` (44 / 1458) are the standing answer, and they fail on a skip, so a green run
means the suites genuinely ran. Anything they cover works on the substrates they cover — which is
**single-writer** local plus, in the service tier, real SQL Server and MinIO.

---

## 1. Measured — these do not work

| # | limitation | evidence |
|---|---|---|
| 1.1 | **A local WINDOWS path is single-writer only.** Concurrent writers lose commits SILENTLY | 6 processes × 3 INSERTs × 50 rows ⇒ **400 of 900 rows landed, 500 lost, every process exited 0.** `fabricator_fs_write_probe` reports `EXCLUSIVE_CREATE` succeeding on an EXISTING file and `MoveFile` overwriting its target, so neither commit primitive is conditional. Full record: [delta-transactions.md](delta-transactions.md) §8.5 |
| 1.2 | Same shape on **`s3://` with no NAMED secret** | 8 of 48 commits landed, 40 silently lost ([delta-transactions.md](delta-transactions.md) §8.3) |
| 1.3 | Same shape on **`abfss://` with no NAMED secret** | 41 of 48 landed, six of the seven losses silent (§8.4) |
| 1.4 | **`fabricator_fs_write_probe` can report the commit guard as WORKING when it tested nothing** — it fails in the UNSAFE direction | Aimed at a path whose parent does not exist, `exclusive_create_existing_fails` reads `true` ("put-if-absent works") because the exclusive open threw for a MISSING DIRECTORY. Confirm `create_directory` and `write_create` are both `true` before believing the verdict. §8.5a |
| 1.5 | **A CREATE-plus-data is NOT atomic — it lands as TWO versions, in a transaction AND in plain autocommit.** v0 = `protocol`+`metaData` (an EMPTY table), v1 = the data. So a concurrent reader can observe the empty table, and **a failure of the data write leaves an empty committed table behind a statement the user saw fail** | `_delta_log` inspected directly for both shapes: `BEGIN; CREATE; INSERT; COMMIT` (via `FlushCreateTransactionAsync`) and a plain **autocommit `CREATE TABLE … AS SELECT`** (via `DeltaWriter.WriteAsync` → `OpenOrCreateAsync` then `table.WriteAsync`). §7.1. Not a protocol limit — Delta permits `protocol`+`metaData`+`add` in v0 — but an engineered-wood API-shape one: `StartTransaction` and `CommitDataFilesAsync` are both INSTANCE methods, so "a transaction that creates its table" is inexpressible |
| 1.6 | **`CREATE TABLE IF NOT EXISTS t AS SELECT …` leaves 1.5's empty table** — correct per its own semantics, but it means the `IF NOT EXISTS` spelling never recovers the orphan | The working recoveries are `CREATE OR REPLACE TABLE … AS SELECT` and a `DROP TABLE` + CREATE. A **plain** `CREATE TABLE … AS SELECT` no longer keeps the old data silently — it now ERRORS (see the note below), so it does not recover the orphan either, but it says so |
| 1.8 | **A `CREATE TABLE` inside `BEGIN … COMMIT` is IMMEDIATE, so a concurrent session sees an EMPTY table for the transaction's life, and a ROLLBACK whose drop FAILS leaves that empty table behind.** Accepted deliberately (it buys ALTER + DELETE on a table created in the same transaction, both of which used to throw) | Pinned by `verify_delta_catalog_transactions` §28/§30 (v0 present mid-transaction, one version, no data commit) and by the rollback sections (folder gone afterwards, with a positive control that it existed first). The drop is best effort BY DESIGN — rollback is already the failure path — and a failure is logged naming the path. ⚠ It can also lose a concurrent writer's rows if someone INSERTed while the table was visible. Rationale + the six build corrections: [delta-transaction-hoist.md](delta-transaction-hoist.md) §3, §4.3 |
| 1.7 | **A change feed built from a BUFFERED append carries NULL row identity, where the same statement in AUTOCOMMIT carries real ids.** The two paths disagree: autocommit writes NO `cdc` action for an append and lets the reader infer it from the `add` (which carries `baseRowId`/`defaultRowCommitVersion`); the buffered path writes a `_change_data` file whose `__delta_row_id` / `__delta_row_commit_version` are **NULL**, and a present `cdc` file SUPPRESSES the inference | `_delta_log` + the change parquet inspected directly on a `change_data_feed true, row_tracking true` catalog: three autocommit `INSERT`s ⇒ `cdc=0` each; `BEGIN; INSERT; COMMIT` ⇒ `cdc=1` with both identity columns NULL beside an `add` carrying `baseRowId:0`, `defaultRowCommitVersion:1`. **Invisible from SQL** — `fabricator_delta_changes` projects only `id, val, _change_type, _commit_version, _commit_timestamp`, so no query distinguishes them. It is a file-layer fidelity gap, visible only to a reader that consumes the identity columns a change file carries — **whether any specific engine surfaces them on a CDF read is UNVERIFIED here**; what is measured is what our files contain. Fix + the trap in the seemingly cheaper alternative: [delta-transaction-hoist.md](delta-transaction-hoist.md) §6 |

**On 1.5 — what protects you today, and it is structural rather than luck.** Every REACHABLE failure fires
BEFORE v0, because the Arrow→Delta **schema conversion is a precondition of the create** (`OpenOrCreateAsync` cannot
be called without a Delta schema). Two measured, both leaving NO table behind: a `TIMESTAMP_NS` column and an
`INTERVAL` column. What remains exposed is a failure of the DATA write or its commit — storage error, permission,
disk full, network — which has no compensation. **That residue is reasoned, not measured**; injecting it was not
attempted. A commit CONFLICT is handled properly by the retry loop.

**⚠ The orphan is UNCONDITIONAL once v0 lands, and we do NOT compensate.** Both paths put the create outside
the guarded region (autocommit's `OpenOrCreateAsync` precedes its `try`; the buffered flush's
`DeltaWriter.Create` precedes its own) and both `finally` blocks only DISPOSE. Only a commit CONFLICT is
handled, by retrying. `RollbackTransaction` reclaims DATA FILES and cannot help — `DiscardBufferedFiles`
opens the table to do its work, so it presupposes the table exists. **A version-checked delete is not the
fix**: measured 2026-08-04, deleting v0 under a concurrent v1 makes the table unreadable (*"Delta log is
incomplete: version 0 is missing …"*), and deleting the whole FOLDER destroys the other writer's data
irreversibly. ⚠ The objection is AUTHORITY, not atomicity — `DROP TABLE` is the same unconditional recursive
folder delete and we ship it, so "a recursive delete can partially complete" rules nothing out. What separates
them is consent: DROP destroys a table the USER NAMED, with the user present, and re-running it finishes a
partial one; the compensation would infer destruction from a failure WE caused, with a third-party victim who
ran only an `INSERT`. The safe primitive is deleting the files you WROTE by name (`DiscardDataFilesAsync`,
which refuses anything a fresh log references) — that needs no authority beyond our own write — and it is
available only after the write-files-first reordering, when the folder is not yet a table. Detail + the reorder's real scope: [delta-transactions.md](delta-transactions.md) §7.1.

**FIXED 2026-08-04, and it was BROADER than the row that used to sit here — the shared C++ layer never
checked `ERROR_ON_CONFLICT` at all.** A plain create reached the provider as an ordinary create, so
`FabricatorSchemaEntry::CreateTable` handled `REPLACE_ON_CONFLICT` (drop first) and `IGNORE_ON_CONFLICT`
(forward the flag) and passed everything else straight through. On Delta, `OpenOrCreateAsync` then simply
OPENED the existing table, so **both** spellings succeeded while doing nothing:

- `CREATE TABLE t AS SELECT …` — no rows written, exit 0, the OLD data kept. Measured with a positive
  control: over a 10-row table, `… AS SELECT range(2)` left **10 rows**, while the same shape on DuckDB's
  own catalog errored.
- `CREATE TABLE t (a INTEGER, b VARCHAR)` — no error, and the **declared schema silently ignored** (the
  table kept its original columns). This half was NOT in the original write-up; it surfaced only from
  running both shapes rather than reasoning about the CTAS one.

Now refused with DuckDB's own `CatalogException::EntryAlreadyExists`, so the message and its structured
`ENTRY_ALREADY_EXISTS` extra-info match every other DuckDB catalog. `OR REPLACE` and `IF NOT EXISTS` are
untouched. Gates `verify_delta_catalog_write` (+12, engine-doubled) and `verify_ctas_text_type` (+8),
both mutation-tested.

**⚠ THE MECHANISM IS NOT WHAT IT LOOKS LIKE, and the two symptoms have DIFFERENT OWNERS.** `PhysicalPlanGenerator::CreatePlan(LogicalCreateTable &)` (`duckdb/src/execution/physical_plan/plan_create_table.cpp:37`) probes for an existing entry and, when one is found and the conflict action is not REPLACE, routes the statement to a bare `PhysicalCreateTable` — **discarding the child plan, i.e. the SELECT.** Proven directly: `EXPLAIN CREATE TABLE IF NOT EXISTS m AS SELECT * FROM range(1000000)` over an existing table prints a physical plan of `CREATE_TABLE` alone, with no scan in it. So **"no rows written" was DuckDB's plan downgrade, not the provider swallowing a write** — the write was never planned. Only "no error" was ours: `PhysicalCreateTable` calls `schema.CreateTable(...)`, which is the check that was missing.

Two consequences worth keeping. **`mode = Overwrite` was never even reached in the broken shape** — `overwrite = createTable || replace` (`DeltaCatalog.cs:2039`) lives on the `begin_bulk` path under `FabricatorPhysicalCreateTableAs`, and the downgrade bypasses that operator entirely; so it is not merely "correct given DuckDB should have rejected first", it is off the path. And **one check covers BOTH the plain CREATE and the CTAS** not by luck but by DuckDB's design: it delegates the conflict decision to the catalog and funnels both spellings into the operator that asks the catalog.

**The scope question that row left open is now SETTLED, and the answer is not uniform.** SQL Server was
never in the dangerous half — its own `CREATE TABLE` rejects a duplicate, so no write was ever lost there;
the user simply got the raw provider error (`2714: There is already an object named …`), which reads as a
SQL Server problem rather than an ordinary catalog conflict. It now reports the catalog error like Delta.
**DAX is structurally exempt** — its provider refuses CREATE outright. So the silent data-keeping was
Delta-only, while the confusing message was shared; one fix covers both.

The existence oracle is `GetOrCreateEntry`, deliberately, not a bare `table_types_` lookup: a table can
exist without being in the discovered name list, because an ATTACH `table_filter` bounds ENUMERATION only
and that path fetches by name. Pinned by the gate creating its conflict against a table that exists on
storage and has NOT been read through the attach.

**Where concurrent writers DO work, measured** (each number from its own run — do not merge them):

- **OneLake / `abfss://`** — 16 writers × 20 commits ⇒ **320/320**, with **19 OCC retries**, which is what proves
  the commit guard was actually under test rather than the writers having serialized.
- **`s3://` with a NAMED secret** — 48/48, versions interleaved across writers.
- **`abfss://` with a NAMED secret** — 48/48, likewise interleaved.
- **Fabric fuse mount** — three runs of 16 × 20, **960 attempted commits, 249 real collisions, zero lost**; one
  run after the `EEXIST` classification fix gave 90 collisions and 320/320. Correct, but ~2.8× slower per commit
  than abfss, so prefer abfss for concurrent writers on performance grounds, not correctness.

⚠ **A green multi-writer run with ZERO collisions measures nothing** — it is indistinguishable from writers that
happened to serialize. Check the retry/collision count before believing one.

---

## 2. Claimed but NOT measured — treat as unproven

| # | claim | why it is unproven, and what would settle it |
|---|---|---|
| 2.1 | **Row-level reconciliation now applies to the autocommit merge-on-read UPDATE** (it previously had none — the retired path compare-and-set on `expectedVersion`, which also disables the OCC retry loop) | MECHANISM-level only: this path now makes the same staging calls the buffered path makes, and those ARE covered (`verify_delta_row_level_concurrency` §3/§5/§8). No observation of two autocommit UPDATEs composing exists. Unreachable in-process (sqllogictest runs connections sequentially, so an autocommit statement has no window) AND unreachable on a Windows local root (limitation 1.1 swamps it). Needs OneLake, S3-with-secret, or POSIX local: `scratchpad/mor_update_race.sh`, which is an A/B against the pre-change build so a single green leg cannot be mistaken for a measurement |
| 2.2 | **`ExemptRowLevelFromWholeTableRead` is set UNCONDITIONALLY while its justification is row-locality** — so `BEGIN; SELECT avg(x) FROM t; DELETE FROM t WHERE x > 42; COMMIT;` is exempted although the row-level validation covers only the REMOVED rows, not a threshold derived from a whole-table read | Reasoned from the source, never executed. **INERT under our default**: engineered-wood's gate is `exempt && rowLevel && isolationLevel != Serializable`, and the catalog default has been `serializable` since 2026-08-01 ⇒ the flag is ignored unless `write_serializable` is explicitly chosen. Settling it means building that shape as a suite section under `write_serializable` with a concurrent remove |
| 2.3 | The narrowing that came with the merge-on-read migration: validation now also runs `RejectRowTrackingWrite`, so a table with row tracking ON but its materialized column names ABSENT is REFUSED where it previously proceeded with fresh ids (silently reassigning row identity) | Reachable only via a foreign writer that produces a spec-invalid table; no suite constructs one. The refusal is the better answer, but it IS a behaviour change |

---

## 3. Interop constraints that are permanent, not bugs

- **SQL Server's `DELTA` reader is protocol 1.0 only.** A table it must read has to be written
  `deletion_vectors false, column_mapping 'none'`. Column mapping ⇒ error 19725; a **materialized** deletion
  vector ⇒ 19726. ⚠ **A protocol bump alone is tolerated** — a DV-declared table with no DV written reads
  fine — so a table can read today and start failing at its first DELETE. And it does **not heal**:
  `CREATE OR REPLACE` over a table that has ever materialized a DV leaves it unreadable; recovery is a real
  `DROP` + `CREATE`. Details: the S3/PolyBase entry in `CLAUDE.md`.
- **Delta has no multi-statement transaction.** One commit is exactly one version, including under a
  catalog-managed/Unity-Catalog coordinator — the catalog arbitrates *which* commit wins version `v`, it does
  not buffer uncommitted work. So the host-side transaction buffer is permanent, not a workaround awaiting a
  better catalog.

---

## 4. `write_serializable` — inert by default, and what it would take to trust

**The catalog default is `serializable` (since 2026-08-01), and the recorded reason is not the whole reason.**
CLAUDE.md justifies the flip on PARITY ("the old default made us the weaker writer than Fabric Spark on any table
that declares no level"). True, but falsifiable — someone could establish parity another way and flip back.
**The author's actual reason was that it was not clear `WriteSerializable` functions 100%** (stated 2026-08-04),
which is the stronger and more durable justification, and §2.2 has since produced concrete evidence for it. Record
both; do not undo the flip on the parity argument alone.

**One fact collapses most of the confusion in this area:** every isolation-related mechanism below is
**`write_serializable`-only**, so all of it is dead under the default.

```
ConflictChecker:  examineAdds = (isolation == Serializable) || !concurrentIsBlindAppend
                                 ^^^^^^^^^^^^^^^^^^^^^^^^^ true ⇒ the flag is never consulted
ExemptRowLevelFromWholeTableRead:  gated on isolationLevel != Serializable
```

| mechanism | protects | direction | active under |
|---|---|---|---|
| `ExemptRowLevelFromWholeTableRead` | OUR concurrent row-level DML from aborting on a whole-table read declaration | inward (our decisions) | write_serializable only |
| `isBlindAppend` **reading** half (shipped) | US from wrongly skipping a check another engine declared we owe | inward | write_serializable only |
| `isBlindAppend` **writing** half (**not built**) | OTHER engines from aborting against our appends | outward (Spark's decisions) | write_serializable only |

**⚠ Two things people get wrong here.** (1) `isBlindAppend` never "allows concurrent appends" — two appends never
conflict at ANY level, because the check asks whether a concurrent add matches *my read predicates* and an appender
has none. It decides whether a **reader** may ignore someone else's append. (2) Delta OSS behaves identically:
under `Serializable` it adds `blindAppendAddedFiles` back into the checked set, so the flag is computed and then
ignored. Its only purpose is the `WriteSerializable` branch — which OSS Delta cannot even reach on its own tables,
because its property validator REFUSES to set that level (measured on Fabric Spark 4.1.1; upstream PR #24 found the
same on delta-spark 4.0.0). It is reachable only on a table stamped by Databricks — or by us.

**Consequence worth stating plainly: row-level concurrency is SHIPPED BUT OFF.** It is a `WriteSerializable`-only
relaxation, so under the default, concurrent disjoint-row DML on one file CONFLICTS rather than composing. The
capability exists and nobody gets it without opting into the level we do not yet trust.

### What "working correctly" would require — five items, and the first two are what create value

| # | item | why |
|---|---|---|
| 4.1 | **build the `isBlindAppend` WRITING half** | **without it, stamping `WriteSerializable` changes nothing cross-engine.** Measured A/B on Fabric Spark 4.1.1 / Delta 4.2.0: `Serializable` ⇒ Spark aborts naming our v8; `WriteSerializable` ⇒ Spark aborts TOO, naming our v23 — because Delta reads an ABSENT flag as "not blind". Emitting it would make Spark commit on a stamped table and change nothing on a `Serializable` one |
| 4.2 | autocommit read-tracking, or truthful omission | the writing half is only reliable inside `BEGIN…COMMIT`, where reads are staged. In autocommit nothing is recorded, so `INSERT … SELECT FROM t` (read) is indistinguishable from `INSERT … VALUES` (blind). Asymmetry rule: staged reads may DOWNGRADE a declared `true` to `false`, never the reverse |
| 4.3 | fix the unconditional exemption (§2.2) | we currently relax more than we justify; that cannot ship as a guarantee |
| 4.4 | real-concurrency test of the AUTOCOMMIT row-level path | untestable in-process, and **impossible on a Windows local root** (§1.1). Needs OneLake or S3-with-a-named-secret |
| 4.5 | settle the `metadataChanged` divergence | Delta guards its WriteSerializable branch with `!currentTransactionInfo.metadataChanged`; EW's `examineAdds` has no such condition. Never investigated |

4.1–4.2 change what a user can observe; 4.3–4.5 are what let us *claim* it. Do not harden a guarantee nobody can
yet observe — 4.1 first, and its gate is the existing `sparkprobe conflict <Level>` A/B (whose method note says to
PROVE the overlap window, never assume it: four earlier runs were void and each void looked like a clean pass).

**⚠ Two caveats before investing.** (a) dbt's usual shape is one model → one table, so `--threads N` writes to N
DIFFERENT tables and never contends; the benefit lands on shared-table workloads (several pipelines on one table,
incremental merges, batch DML beside appends) — confirm that shape exists before treating this as a selling point.
(b) It **collides with SQL Server interop**: row-level concurrency needs deletion vectors, and a materialized DV
makes a table permanently unreadable by SQL Server's `DELTA` reader (§3). A table can have this OR T-SQL
readability, not both.

---

## Maintaining this page

Add a row when a limitation is **measured**, or when a claim is made that is **not** measured — the second kind
is what this page exists for, and it is the kind that silently becomes folklore. Move a row from §2 to §1 (or
delete it) only when a measurement or a suite settles it, and say which. Do not let §2 grow without a note on
what would settle each entry; an unprovable claim is a claim to remove, not to keep.
