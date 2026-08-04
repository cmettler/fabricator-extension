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
| 1.5 | **`BEGIN; CREATE; INSERT; COMMIT` lands as TWO versions, not one** — v0 `protocol`+`metaData` (an empty table), v1 the data. A concurrent reader can see the empty table, and **a v1 failure leaves an empty committed table behind a transaction the user saw fail** | `_delta_log` inspected directly. §7.1. Not a protocol limit (Delta permits `protocol`+`metaData`+`add` in v0) but an engineered-wood API-shape one |

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
