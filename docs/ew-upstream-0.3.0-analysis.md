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

**Moved DOWN and made PUBLIC** (verified against the `upstream/main` tree, not inferred from commit subjects):

| type | from | to | accessibility |
|---|---|---|---|
| `ConflictChecker`, `ConflictResult`, `ConflictType` | `.Table.Concurrency` | `…DeltaLake.Concurrency` | **public** |
| `ReadSet` | `.Table` | `…DeltaLake.Concurrency` (in `ConflictChecker.cs`) | **public** |
| `IsolationLevel` | `.Table` | `…DeltaLake` | **public** |
| `DeltaFilePruner` | `.Table` | `…DeltaLake` | **public** |
| `BuildPartitionPath` | `.Table.Partitioning/PartitionUtils` | `…DeltaLake/DeltaPath` | **public** |

⚠ **Two corrections to this doc's first draft, both of which were guesses from diffstats.**
`DeltaFilePruner` is `public sealed class` — NOT "moved but still internal". And `PartitionUtils` did
**not** move and is still `internal` in `.Table`; what became public is the one METHOD we cared about, on a
different type (`DeltaPath.BuildPartitionPath`). Read the tree, not the commit list.

New public API in `…DeltaLake.Log`: **`LogCommitter`**, plus `LogCommitRequest` / `LogCommitResult` /
`LogCommitOptions` / `ICommitRebaseHandler` / `CommitRebase` / `CommitRebaseContext` /
`RecomputeRebaseHandler` / `LogVersions`.

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

## 1a. THE FINDING THAT CHANGES THE PLAN — upstream built the public seam our patch set exists to reach

Read `LogCommitter`'s own summary: *"a caller with a data plane of its own — its own parquet, its own
statistics, its own deletion vectors — can use this without bringing one along."* That is a description of
fabricator. Three public members carry our two live needs, and neither needs a patch:

1. **`LogCommitRequest.Reads` is a caller-supplied `ReadSet`**, and `ReadSet.WholeTable` is `{ get; init; }`.
   Our `ExemptRowLevelFromWholeTableRead` patch is, in its entirety, `reads with { WholeTable = false }`
   applied just before `ConflictChecker.Check`. A caller that BUILDS the read set states that directly. The
   patch exists only because `DeltaTable` built the read set from `DeltaTransaction`'s declarations and gave
   the host no way in — ⇒ **it is a plumbing patch, not a semantics one, and the plumbing is now public.**
   - ⚠ This also **retracts the offer**, not just the patch. CLAUDE.md lists it as offer (2) —
     *"`ExemptRowLevelFromWholeTableRead`, pitched as a DEPARTURE not an inconsistency"*. There is nothing
     left to ask for: upstream did not adopt our flag, it removed the need for one. Same outcome as
     `RowUpdateMode` and the `WriteChangeDataFilesForAsync` overload — **the cheapest way to retire a patch
     is to stop needing it**, and that is now three for three.
2. **`ICommitRebaseHandler.RebaseAsync` returns `CommitRebase(Actions, RowLevelResolvedPaths)`.** That second
   field is exactly the argument `ConflictChecker.Check` takes for row-level DV reconciliation. So the
   concurrent row-level DML story — rebase each staged delete's DV onto the concurrent one, then tell the
   checker those paths are settled — is expressible through the public interface. It is the piece I expected
   to be missing and it is there.
3. **`ConflictChecker` is public and documented as a pure function** — so its verdicts are unit-testable from
   our side, and a divergence between what we think we declared and what the checker sees stops being
   arguable.

**⚠ THIS IS A READING OF THE API SURFACE, NOT A BUILD.** This project's own standing rule:
*building the Bridge is what finds the host's needs; reading the diff is not.* Every claim above is that
these needs are EXPRESSIBLE, not that they are met. Specifically unestablished:
- Whether committing through `LogCommitter` means bypassing `DeltaTransaction`'s staging
  (`StageRowDeletesAsync` / `StageDataFilesAsync` / `StageChangeDataAsync`) — which is where our actions
  come from today — or whether `DeltaTransaction` itself now routes through it and exposes the request.
- Whether the DV computation a row-level rebase needs is reachable from outside (`StageRowDeletesAsync` is
  the public door onto EW's internal DV core today; a `LogCommitter`-only route may not have one).
- The **whole `DeltaTable.cs` half of our patch set**, which is NOT about concurrency at all — see §1b.

## 1b. What our 4 patched files actually contain — because the summary "row-level DML + isBlindAppend" is incomplete

Measured hunk by hunk against the merge base, not recalled:

| file | lines | what it is | does §1a retire it? |
|---|---|---|---|
| `ConflictChecker.cs` | 42 | the isBlindAppend READING half | **no — still needed, see §1c** |
| `DeltaTransaction.cs` | 26 | `ExemptRowLevelFromWholeTableRead` (a property + its doc) | **likely yes** — §1a.1 |
| `DeltaFilePruner.cs` | 4 | a doc paragraph pointing at `PlanFiles` | **yes, trivially** — the type is public now |
| `DeltaTable.cs` | 137 | **six unrelated things, mostly NOT concurrency** | **unknown — the real work** |

`DeltaTable.cs` breaks down as: the `ExemptRowLevelFromWholeTableRead` plumbing (~40 lines, the other end of
the `DeltaTransaction` property); create-time **`configuration`** and **`preAssignedSchema`** params (a
buffered-transaction CTAS has already written data files against assigned physical names, so re-assignment
would orphan them); `WriteDataFilesAsync`'s **`materializedRowIds` / `deletionVectorsByFileIndex` /
`identityValuesPreGenerated`**; the **path-keyed `FileRowSelection`** docs and the ordinal-keyed
`RebaseDvDmlActionsAsync` compatibility overload; a `UpdateAsync` overload carrying `readPredicates`; and a
copyright header.

⇒ **Roughly half of `DeltaTable.cs` is host-plumbing on the WRITE path, not OCC.** Those are the ones §1a says
nothing about, and they are what a fresh-branch refactor has to answer: does the public commit layer let us
supply pre-assigned schemas, materialized row ids and pre-computed DVs, or do those still need `DeltaTable`?

## 1c. isBlindAppend — STILL not upstream, and upstream's own doc comment is the argument for the offer

Verified in the tree, not assumed: `ConflictChecker.IsBlindAppend(actions)` at `upstream/main` is the pure
**inference** — *"at least one add, and no remove, metadata, or protocol action"*. It never reads
`commitInfo.isBlindAppend`. So our 42-line patch is live, and the file moving to a public namespace makes the
offer CLEANER rather than obsolete.

⚠ Upstream's doc comment on it says the inference is *"the reader-side inference the protocol relies on."*
Our own reading of Delta at the `v4.2.0` tag says otherwise, and the discrepancy is the offer: Delta is
`isBlindAppendOption.getOrElse(false)` — the flag, defaulting to NOT blind — and it **computes
`onlyAddFiles` and pointedly does not use it** for this decision. The inference errs in the UNSAFE direction
(an `INSERT INTO t SELECT … FROM t` emits only adds and plainly read the table), which is the dbt-incremental
anti-join shape, i.e. the common case.

Note our patch is deliberately NOT Delta-equivalent either: we fall back to the inference when the flag is
ABSENT, because EW emits no flag itself and `getOrElse(false)` would make ordinary EW-to-EW concurrent
appends start conflicting. Offer it as what it is — believe a declaration when one is made — and say that the
fallback is a back-compat choice, not parity.

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

0. **RESOLVED, see §1a/§1c — these two were the top of this list and are now measured, not candidates.**
   `ExemptRowLevelFromWholeTableRead` (both halves, ~66 lines) is retired by `LogCommitRequest.Reads`, and the
   OFFER is withdrawn with it. `DeltaFilePruner`'s 4 lines are retired by the type going public. The
   isBlindAppend patch is NOT retired: upstream still infers.
1. ~~**Our `ConflictChecker` isBlindAppend patch (offer 1, 42 lines).**~~ **Checked — upstream did NOT take an
   equivalent** (`IsBlindAppend` is the bare action-shape inference at `fa9b556`). The patch re-cuts onto a
   public type, which makes the offer easier to send, and §1c has the argument.
2. **`BuildPartitionPath` is PUBLIC — on `DeltaPath`, not `PartitionUtils`.** CLAUDE.md currently says *"do not
   hand-roll the partition split"* and records that a request to make `PartitionUtils` public was superseded.
   The METHOD is public anyway,
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

## 5. The approach: a FRESH branch off upstream, not a merge (decided 2026-08-07, user)

**Do not `git merge upstream/main` into `fabricator-patches`. Branch `fabricator-patches-v2` off
`upstream/main` (`fa9b556`) and re-derive.** The reasoning, in order of weight:

1. **A merge asks the wrong question.** It asks "how do our 175 lines re-apply onto the refactor?" — which is
   how the 2026-08-01 bump silently kept a patch it should have retired and silently lost one it needed. A
   fresh branch asks *"what, if anything, does the host still need?"* — and §1a says the honest answer may be
   **one patch** (isBlindAppend), which is an OFFER rather than a need.
2. **The delete/modify hazard disappears** rather than being carefully navigated. Two of four patched files no
   longer exist at the path we patch; a merge can resolve either as "deleted by them" (patch silently gone) or
   as "modified by us" (patch silently resurrected at a dead path). Neither is detectable from a clean merge.
3. **It matches the user's instruction for this bump** — early version, no back-compat, prefer a clean refactor
   over overlaying, especially where an upstream feature can replace our own implementation. §1a is exactly
   that case.

The cost is real and worth stating: the fresh branch loses the 60-commit history that records WHY each patch
exists. Mitigation — `fabricator-patches` is not deleted, so `git log -S` still answers "did we once need
this?", and every retired patch's reasoning is in CLAUDE.md and these docs already.

### Order

1. **Read the four behaviour-changing PRs** (#87, #83, #76, #73) — the DOC hunks, not the subjects.
   #87 matters most: we `catch (DeltaConflictException)` in the OCC retry loops and it now covers two
   different failures behind one type.
2. `git checkout -b fabricator-patches-v2 upstream/main`. Nothing of ours on it yet.
3. **Build the Bridge against bare upstream and let the COMPILER enumerate the needs.** This is the step that
   replaces the surface audit, and it is strictly better: an audit lists what moved, a build lists what we
   cannot express. Expect the §1b write-path params to be most of the errors.
4. **For each error, ask "public API or patch?" in that order** — `LogCommitter` / `LogCommitRequest` /
   `ICommitRebaseHandler` / `DeltaFilePruner` / `DeltaPath` first. Only what survives that question becomes a
   patch, and each one gets its `// [FABRICATOR-PATCH: …]` marker at birth, with what would retire it.
5. **Then, and only then, the concurrency question the user actually wants answered:** what does *proper*
   concurrent row-level DML + WriteSerializable need, given `ReadSet` is ours to build and
   `CommitRebase.RowLevelResolvedPaths` exists? That is a design pass, not a port — and it is the one place
   where we should consider changing OUR side rather than EW's, since the two things our current
   implementation gets wrong (the unconditional exemption at `DeltaCatalog.cs:3775`, and the missing
   provenance on `ReadWholeTable`) are both host-side.
6. Gates: EW Table.Tests × {net10.0, net8.0, net472} (only net472 proves a change offerable), then hermetic
   (67/67 — 6689) and service (45/45 — 1678).
7. Re-take §4's numbers.
8. Push EW to the fork BEFORE bumping the pointer (the pin must be fetchable), then re-pin and update
   `.gitmodules`' `branch =`.
