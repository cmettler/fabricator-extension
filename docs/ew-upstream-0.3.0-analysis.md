# The engineered-wood 0.3.0 bump — analysis before the merge (2026-08-07)

**Status: ANALYSIS ONLY. Nothing merged, nothing re-pinned, no code changed.** This is the pre-flight for a
bump that is bigger than any since the clast-master re-pin, because upstream refactored the layer our patch
set sits on. Read [ew-master-migration.md](ew-master-migration.md) first — its standing rules all apply.

Current pin: **`3794fe4`**. Upstream tip: **`fa9b556`** (tag **`v0.3.0`**). **15 commits behind.**

⚠ `git ls-tree HEAD engineered-wood` is the authority on the pin, not this prose.

---

## 1. The headline: upstream extracted the OCC core, and our patches sit on the files that moved

Issue **#65** → PR **#83** (`09248de`, *"the log layer cannot commit — OCC and the commit loop live in an
8090-line table class"*). Upstream's own framing: the two-package split *"does not currently earn its keep"*,
so the OCC core moved DOWN into the log layer to make `EngineeredWood.DeltaLake` a standalone logging engine
and `ConflictChecker` *"a pure function of its inputs — no I/O, no snapshot mutation."*

**Moved DOWN and made PUBLIC:**

| type | from | to |
|---|---|---|
| `ConflictChecker`, `ConflictResult`, `ConflictType` | `.Table.Concurrency` | `…DeltaLake.Concurrency` |
| `ReadSet` | `.Table` | `…DeltaLake.Concurrency` |
| `IsolationLevel` | `.Table` | `…DeltaLake` (`[TypeForwardedTo]` left behind) |
| `PartitionUtils.BuildPartitionPath` | `.Table.Partitioning` | `…DeltaLake` |

Moved but still `internal`: `DeltaFilePruner`, `DeltaFileStats`, `DeltaFileStatsAccessor` → `…DeltaLake`.

New public API in `…DeltaLake.Log`: **`LogCommitter`**, plus `LogCommitRequest` / `LogCommitResult` /
`LogCommitOptions` / `ICommitRebaseHandler` / `RecomputeRebaseHandler` / `LogVersions`.

### ⚠ THE MERGE HAZARD, and it is the one that already bit us once

Our patch set is **+175 / −34 across 4 files** (measured `merge-base c469d9d..HEAD`; ⚠ NOT
`git diff upstream/main`, which is contaminated by upstream's 15 new commits appearing as deletions — that
mismeasurement is easy to make and reports 43 files):

| our patched file | our lines | upstream commits touching it | upstream DELETED it? |
|---|---|---|---|
| `.Table/Concurrency/ConflictChecker.cs` | 42 | 1 | **YES — moved to `…DeltaLake`** |
| `.Table/DeltaFilePruner.cs` | 4 | 1 | **YES — moved to `…DeltaLake`** |
| `.Table/DeltaTable.cs` | 137 | 4 | no, but heavily refactored |
| `.Table/DeltaTransaction.cs` | 26 | 0 | no — clean |

**Two of our four patched files NO LONGER EXIST at the path we patch.** A delete/modify conflict can resolve
as "deleted by them", silently discarding our change — which is exactly how the 2026-08-01 bump lost
`UpdateBySelectionViaVectorsAsync` and nearly converted five merge-on-read tests into copy-on-write tests.

**Run the surface audit FIRST** (ew-master-migration.md's rule): diff the public surface of the pre-merge
assemblies against the merged one, then classify each absent member by whether it was in the MERGE BASE. That
separates upstream's consolidation from our losses. Do not trust a clean `git merge`.

---

## 2. Every commit since the pin, with what it means for us

Newest first. **"Impact" is a first read from the subject line and the diffstat — NOT verified against our
code.** Each one needs checking before it is believed.

| # | commit | what it is | impact on us |
|---|---|---|---|
| #87 | `fa9b556` | `DeltaConflictException` is a lost commit slot AND an invalidated read set behind one type, with `-1` as the only tell | **HIGH — we CATCH this type in the OCC retry loops.** If the two failure kinds are now distinguishable, our retry may be reacting to the wrong one. Check every `catch (DeltaConflictException)` in the Bridge. |
| #83 | `09248de` | the OCC/commit-loop extraction (issue #65) | **HIGHEST — see §1.** Our `ConflictChecker` patch's file moved and went public. |
| #82 | `785eaae` | an undecodable checkpoint blamed the log for its own limitation | low; error-message/classification |
| #81 | `d9a966a` | **a V2 checkpoint written by Spark could not be read at all** | **HIGH for interop** — we validate against Fabric Spark. Possible capability GAIN. |
| #80 | `46cb9f0` | error codes for the table layer | medium — we may be able to classify provider errors instead of matching text |
| #78 | `f524ed3` | stable error code on every log-layer failure | as above; `DeltaErrorCodes.cs` is new (+219) |
| #76 | `ea16a5a` | **nothing public enumerated checkpoint-only versions, and the two version APIs contradicted each other** | **HIGH — directly touches `ListVersionsAsync`/`GetLatestVersionAsync`**, the code behind this session's log-growth finding. New `LogVersions.cs`. |
| #77 | `ba21bf9` | test: Spark partition-path encoding — EW already matches, delta-rs is the outlier | **retires a documented worry**: CLAUDE.md warns not to hand-roll the partition split because of Delta's partition-value string encoding. Now measured AND `BuildPartitionPath` is public. |
| #75 | `8531e34` | a checkpoint's typed statistics were read then hidden behind `internal` | medium — possible replacement for some of our own stats handling |
| #73 | `18f2c0a` | **a V2 checkpoint drops every tombstone, and snapshot replay reads every commit at once** | **HIGH — may revise this session's S3 perf finding.** "Reads every commit at once" is precisely the cost measured at ~10 ms/dead-commit. Re-measure after the bump. |
| #63 | `2c33d95` | prepare the 0.3.0 release | — |
| #62 | `847543b` | GCS client bump | none (we do not use GCS) |
| #60 | `65c7702` | **performance improvements for Delta Lake table** | **HIGH — re-take every number.** See §4. |
| #59 | `a99cc41` | doc: pushdown ecosystem findings + vacuum hidden-directory spec | read it; may inform our pushdown |
| #58 | `7575c4d` | ci: doc-only changes skip the build | none |

---

## 3. What this could RETIRE or IMPROVE on our side

The user's instruction for this bump: **early version, no back-compat needed — prefer a clean refactor over
overlaying, especially where an upstream feature can replace our own implementation.** Candidates, in
descending order of confidence, all UNVERIFIED:

1. **Our `ConflictChecker` isBlindAppend patch (offer 1, 42 lines).** The file moved and is now public. Either
   upstream took an equivalent, or our patch re-cuts onto a public type — which would also make the offer
   trivial to send. **Check `git log -S` before assuming upstream reimplemented us** (it may be convergence,
   the ew-master-migration rule).
2. **`PartitionUtils.BuildPartitionPath` is PUBLIC.** CLAUDE.md currently says *"do not hand-roll the partition
   split"* and records that a request to make `PartitionUtils` public was superseded. It is now public anyway,
   and #77 measured EW's encoding as Spark-matching. Anywhere we avoided partition-path work for this reason
   is unblocked.
3. **Error codes (#78/#80).** We classify provider failures by NUMBER on SQL Server and by predicate on Delta
   (`ObjectNotFoundException`). Stable Delta error codes may let the `FABRICATOR_NOT_FOUND` classification and
   the conflict/retry decisions key on a code instead of a type or a message.
4. **`LogCommitter` + `ICommitRebaseHandler`.** We hand-roll an OCC retry in more than one place
   (`DeltaCatalog` flush, `CommitDataFilesAsync` callers, `MaxCommitAttempts = 16`). If `LogCommitter` is the
   public commit loop, some of that is deletable — the "stop needing the patch" outcome this project prefers.
5. **`LogVersions` (#76).** Our log-growth analysis had to reason about `ListVersionsAsync` vs
   `GetLatestVersionAsync` disagreeing about checkpoint-only versions; upstream says they *contradicted each
   other* and fixed it. Re-read our assumptions there.
6. **Log cleanup is still ABSENT** as of this tip — verify after the merge, since it is offer (4) and the
   controlled experiment (§CLAUDE.md) was run against the OLD pin.

---

## 4. ⚠ Numbers this bump invalidates

**#60 (perf) and #73 (snapshot replay reads every commit at once) both land in the code this session
measured.** Everything below was taken against pin `3794fe4` and must be RE-TAKEN, not carried forward:

- ~10 ms per dead commit file per scan (S3), and the ~85 ms per active data file.
- `lake/t` 148 commits → 2.61 s warm vs 1.18 s fresh.
- The OPTIMIZE control: 19.8 s → 7.2 s.
- The unattributed 3× residual (7.2 s at ~151 commits/1 file vs 2.6 s at 148 commits) — #73 is a plausible
  explanation for it and should be checked first.
- The [delta-snapshot-caching](delta-snapshot-caching.md) decision gate, which #41 already re-priced once.

---

## 5. Suggested order

1. Fetch + read the four PRs whose subjects promise behaviour change (#87, #83, #76, #73). Read the DOC hunks,
   not just the subjects (ew-master-migration rule).
2. `git merge upstream/main` into `fabricator-patches` — **`upstream/main`, never `master`** (renamed; the
   stale ref still resolves and lands on an abandoned branch).
3. **Surface audit before building anything.** Classify every absent member against the merge base.
4. Re-cut the two patches whose files moved; decide per patch whether it is still needed at all.
5. Build the Bridge — *building is what finds the host's needs; reading the diff is not.*
6. Gates: EW Table.Tests × {net10.0, net8.0, net472} (only the net472 leg proves a change offerable), then
   hermetic (67/67 — 6689) and service (45/45 — 1640).
7. Re-take §4's numbers.
8. Only then re-pin, and push EW to the fork BEFORE bumping the pointer (the pin must be fetchable).
