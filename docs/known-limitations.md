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

## Maintaining this page

Add a row when a limitation is **measured**, or when a claim is made that is **not** measured — the second kind
is what this page exists for, and it is the kind that silently becomes folklore. Move a row from §2 to §1 (or
delete it) only when a measurement or a suite settles it, and say which. Do not let §2 grow without a note on
what would settle each entry; an unprovable claim is a claim to remove, not to keep.
