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

## 1d. TWO CHANGES THE COMMIT LIST DOES NOT MENTION, both of which land on limitations we documented as ours to live with

Found in `src/Directory.Build.props`' `PackageReleaseNotes`, not in any commit subject. **Read the release
notes of a pre-1.0 dependency; the subjects do not carry the breaking changes.**

### (a) `ITableFileSystem.RenameAsync` is REMOVED, replaced by `TryWriteAllBytesAsync` — and the contract is
the exact hazard we measured

> *"Atomically writes the entire contents of a small file only if the file does not already exist. Returns
> true when the file was created, or false when an existing file was left unchanged. … The atomicity is
> LOAD-BEARING … An implementation that writes unconditionally, or that checks existence and then writes, lets
> two concurrent writers both believe they won; the loser's commit silently overwrites the winner's and the
> table loses whatever that version recorded. **There is no error path for this.**"*

That paragraph describes, precisely, three failures this project measured and wrote up as substrate problems:
local Windows (`EXCLUSIVE_CREATE` succeeds on an existing file AND `MoveFile` overwrites ⇒ 400 of 900 rows
landed, every writer exit 0); secretless S3 (8 of 48 commits landed, 40 lost silently); unguarded abfss
(41 of 48). ⇒ **the commit primitive stops being two operations we hope compose and becomes ONE method with a
`bool` return**, per filesystem, with the requirement stated.

**This is a REQUIRED implementation, not an opportunity** — it is 3 of our types
(`AdlsGen2TableFileSystem`, `DuckDbTableFileSystem`, `S3CommitFileSystem`) and the first thing the compiler
asks for. And the honest implementation is where it gets interesting: **`DuckDbTableFileSystem` cannot satisfy
this contract on the backends we measured**, because it routes through DuckDB's `FileSystem` whose
`EXCLUSIVE_CREATE` is non-conditional on local Windows and on S3. That is not a regression — it is the
existing silent hazard becoming a contract we can fail loudly against instead of a footnote in
[known-limitations.md](known-limitations.md). Decide deliberately: refuse the write, or route those roots to a
filesystem that can (the S3/ADLS ones already do).

### (b) `DeltaTable.CreateOrReplaceAsync` — the static factory that retires limitation 1.5/1.6

> *"publishes protocol, metadata, removes and initial data in one commit, creating a table with data or
> atomically replacing an existing one while preserving its history."*

CLAUDE.md and [delta-transactions.md](delta-transactions.md) §7.1 record that a CREATE-plus-data is **two
versions** (v0 empty, v1 data) *in plain autocommit as well as in a transaction*, that a concurrent reader can
therefore observe the empty table, that a data-write failure leaves an empty committed table behind a
statement the user saw fail, and that fixing it needs **"an upstream static/factory form"** because all three
doors were instance methods on an already-created table. `CreateOrReplaceAsync` is `public static`. ⇒ the
door is open, and the compensation analysis we could not act on (§7.1's version-checked-delete-races-a-writer
problem) is moot if the empty version is never published.

⚠ Not yet verified: whether it accepts the pre-assigned schema and materialized row ids our buffered CTAS
needs (§1b), which is precisely the patch family this would have to subsume to be a net win.

## 1e. THE MEASUREMENT — the Bridge compiled against bare `fa9b556`, and the total need is SIX errors

Branch `fabricator-patches-v2` off `upstream/main`, no patches, `dotnet build Fabricator.Bridge -f net10.0`.

**Layer 1 — 3 × CS0535**, all the same new obligation: `ITableFileSystem.TryWriteAllBytesAsync` on
`AdlsGen2TableFileSystem`, `DuckDbTableFileSystem`, `S3CommitFileSystem` (§1d(a)).

**⚠ LAYER 1 IS NOT THE ANSWER, AND READING IT AS ONE WAS THE FIRST MISTAKE HERE.** CS0535 is a
DECLARATION-level error, and Roslyn does not bind method BODIES while declaration errors are present. So the
first build's tidy "3 errors, all one member" was the compiler having stopped before it looked at any of our
code. Caught by a positive control — `DeltaCatalog.cs:4058` sets `ExemptRowLevelFromWholeTableRead`, which I
had already verified is absent upstream, so its silence was proof the enumeration was incomplete rather than
proof the code was fine. **Stub the declaration errors and build again.**

**Layer 2 — 3 errors, and only ONE is patch-shaped:**

| error | what it is | verdict |
|---|---|---|
| `DeltaTransaction` has no `ExemptRowLevelFromWholeTableRead` (`DeltaCatalog.cs:4058`) | our patch | **re-express via `LogCommitRequest.Reads`** — §1a.1 |
| `EngineeredWood.DeltaLake.Table.IsolationLevel` does not exist ×2 (`:4130`, `:4131`) | the type moved namespace | a `using` change, not a patch |

**⚠ AND "0 errors" WAS REPORTED ONCE ON THE WAY, FALSELY** — a `sed` in the capture pipeline swallowed the
lines while the build was plainly failing. The same positive control caught it, one turn after catching the
first one. Two different zero-traps in one enumeration, both from trusting a filtered count over the artifact:
`ls` the DLL, or read the raw tail.

**Why the number is so small: five of the six members our `DeltaTable.cs` patch added are ALREADY UPSTREAM.**
Checked by name against `upstream/main` — `PlanFiles`, `preAssignedSchema`, `materializedRowIds`,
`identityValuesPreGenerated`, `RebaseDvDmlActionsAsync` all present; only `deletionVectorsByFileIndex` is
absent and nothing of ours calls it. ⇒ §1b's worry that "half of `DeltaTable.cs` is write-path plumbing the
public commit layer says nothing about" is **answered, and answered better than by the commit layer**:
upstream absorbed those patches directly. The branch model paying out again.

**⚠ WHAT THE COMPILER CANNOT SEE, and it is the whole remaining risk.** A green build says our calls RESOLVE,
not that they still MEAN what they meant. Everything in §2's behaviour-change column compiles silently:
#87 (`DeltaConflictException` now covers two different failures behind one type — we catch it in the OCC retry
loops), #76 (the two version APIs contradicted each other), #73 and #60 (the perf/replay changes that
invalidate §4's numbers). Those need reading and gates, not a build.

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

## 2a. ⚠ THE ISSUE TRACKER — read it, the commits do not carry the plan (user-prompted, 2026-08-07)

I analysed 15 commits, the release notes and the public API and never opened
<https://github.com/clast-project/engineered-wood/issues>. The user did, and four open issues change our
conclusions. **Upstream's tracker states intent; the commit log only states what already happened.**

### #88 — our isBlindAppend patch is upstream's own filed issue, and it asks for THREE things we do not do

*"the blind-append exemption is inferred from a commit's actions, and the inference forgives commits Spark
would not."* Our patch is one of its four decisions. The rest:

1. **WRITE the flag**, sourced from `LogCommitRequest` (blind read set + no planned removes). ⇒ this is the
   **cross-engine gap CLAUDE.md carries as OPEN** — *"we do not emit `commitInfo.isBlindAppend`, so a Fabric
   Spark transaction ABORTS against our concurrent append"*, measured live on Fabric Spark 4.1.1. It is now
   cheap for exactly the reason §1a gives: `LogCommitRequest` carries the read set. **Our write-half work and
   upstream's issue are the same work** — do not build it privately.
2. **TWO divergent inferences**, confirmed by reading both: `ConflictChecker.IsBlindAppend` requires
   `hasAdd`; the one inside `CheckLogicalRebaseAsync` (`DeltaTable.cs:7513`) starts `blindAppend = true` and
   clears only on remove/metadata/protocol, so an **EMPTY commit counts as blind** there. We reach only the
   first, but an offer should unify them.
3. **"Absent" is explicitly UNDECIDED upstream** — the issue lists both "trust the inference (permissive,
   current)" and "treat as not-blind (conservative)". Our patch picks the first and has the measured reason
   (EW emits no flag itself, so `getOrElse(false)` would make ordinary EW-to-EW concurrent appends start
   conflicting). ⇒ we have a stake in that decision and should say so rather than ship a fait accompli.

Upstream also draws a distinction ours does not: *"a recorded `false` should be trusted absolutely, while a
recorded `true` is a claim by another writer."*

### #86 — DML-written tables NEVER CHECKPOINT — **FIXED 2026-08-07, and it is our second patch**

**Implemented on `fabricator-patches-v2` (`d3a1301`), marked `[FABRICATOR-PATCH: OFFER-READY — #86]`.**
The whole change is **one line**: `CommitOccAsync` passed `WriteCheckpointOnInterval = false`, and that
loop has SIX callers, so flipping it covers every path at once. `LogCommitRequest`'s default is already
`true` — this simply stops opting out. The condition and the ordering are `LogCommitter`'s own, identical
to the batch path that already set it, so no new mechanism is introduced.

MEASURED after: all three write paths produce 3 checkpoints on 26 commits — autocommit DML, DML inside an
explicit transaction, and the batch append — each with a `_last_checkpoint`, where the first two produced
**0** before.

**MEASURED WHAT IT BUYS (2026-08-07), and the two legs disagree in a way that is itself the lesson.** Same
table, same 81 commits, only the checkpoint objects differ — timed with them present, then again after
deleting the 8 checkpoint parquets and `_last_checkpoint`:

| transport | with checkpoints | without | delta |
|---|---|---|---|
| **MinIO / localhost** | 2708 / 2714 / 2787 ms | 3112 / 2952 / 2989 ms | **≈275 ms (9%), ranges disjoint** |
| **local filesystem** | 368 / 341 / 328 ms | 360 / 325 / 406 ms | **VOID — ranges overlap** |

≈3.8 ms per commit skipped on localhost MinIO. ⚠ **The local leg is VOID, not negative**: ~300 ms of
process start and CLR boot dominates and local JSON reads cost nothing, so that measurement cannot see the
variable under test. **Checkpoints are an object-store optimisation and a local A/B will never show one** —
worth knowing before someone repeats the local run and concludes the fix is worthless.

⚠ Both numbers are a FLOOR: the saving is one round trip per skipped commit file, so it scales with real
object-store latency (this is localhost) and with commit count. And holding the commit count fixed
UNDERSTATES it, because the compounding half is that without checkpoints the log can never be cleaned
either — so on a DML-only table the count itself grows without bound.

⚠ **No public `CheckpointAsync()` was added, although upstream's issue proposes one.** The condition is
`version % CheckpointInterval == 0` on the ABSOLUTE version, so an existing under-checkpointed table
SELF-HEALS within one interval; an uncalled public API would be divergence for nothing. If a host ever
needs to force one out-of-band, that is when to add it — and it is upstream's item 3, so ask there first.

Gate `DmlCheckpointTests` (3), mutation-tested: restoring `= false` kills exactly the two DML cases and
leaves the batch-append POSITIVE CONTROL passing — which is what distinguishes "the DML loop is broken"
from "checkpointing is broken generally".

Original finding, kept because the consequence chain is the argument for the fix:

*"CheckpointInterval is honoured by two commit paths out of twelve."* Honoured: `WriteAsync`,
`CommitDataFilesAsync`. **Not honoured: `DeltaTransaction.CommitAsync`, every delete, every update,
`CompactAsync`/OPTIMIZE, every schema change** — i.e. our entire write surface beyond a plain append.

**MEASURED on our side**, same table shape, 26 commits each: 24 INSERTs ⇒ **3 checkpoints**; 24 DELETEs ⇒
**0 checkpoints**.

- **It revises the S3 slowness analysis.** The measured ~10 ms per dead commit is a cost only because there
  is no checkpoint to resume from. ⚠ And the **unattributed 3× residual** compared two tables without ever
  checking their checkpoint state — that is the variable I did not control, the same confound shape caught
  earlier the same day with the per-commit/per-file slope. **Do not quote that residual until it is retaken
  with checkpoint presence held constant.**
- **It revises the log-cleanup offer (4).** "engineered-wood never deletes a superseded commit" is true but
  incomplete: cleanup DEPENDS on checkpoints, and a DML-written table has none — so even a correct cleanup
  could reclaim nothing on our tables. The two compound, and #86 is the one to fix first.

### #54 — a live risk in what this session COMMITTED

*"VACUUM collects every sidecar directory it does not know about, starting with `_delta_index`."* The S3
suite teardown added today runs `VACUUM … RETAIN 0 HOURS`. Harmless on the rig (nothing there writes
sidecars) — but it must not be recommended to users, and any table with a sidecar index is exposed.

### #85 / #84 — the partition-path opportunity is NOT as clean as §3.2 says

§3.2 reads #77 as retiring the "do not hand-roll the partition split" warning. #85 says that ground truth is
a **macOS-only measurement and Spark disagrees with itself on Windows**, and #84 says partition values
containing `< > |` or a trailing space **cannot be written on Windows at all**. ⇒ `DeltaPath.BuildPartitionPath`
being public does not mean the encoding question is settled. Treat §3.2 as downgraded.

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

## 4a. PHASE A — DONE. The patch set is 4 files / +175 → **2 files / +69**, and both are offer-ready

⚠ The bump itself reduced it to **ONE file / +45** (the isBlindAppend read half). The second patch is the
#86 checkpoint fix, added deliberately AFTER the bump was green — see §2a. Keeping them separate is the
point: Phase A's claim is "behaviour-preserving, hermetic identical at 6689", and #86 CHANGES behaviour
(checkpoints and `_last_checkpoint` start appearing), so folding it in would have destroyed the
attribution that made a red tier diagnosable.

Branch `fabricator-patches-v2` off `fa9b556`. What it took, in full:

| change | kind |
|---|---|
| `IsolationLevel` namespace, 2 sites | mechanical |
| `TryWriteAllBytesAsync` on 3 filesystems, replacing `RenameAsync` | required by 0.3.0 |
| the whole-table exemption moved OUT of EW into the host | **patch retired** |
| isBlindAppend re-cut onto the public `ConflictChecker` | **the one remaining patch** |

The surviving patch carries `// [FABRICATOR-PATCH: OFFER-READY]` naming what retires it — the marking
convention CLAUDE.md has wanted since the upstream-strategy entry, now cheap because there is one of them.

### ⚠ 4a.1 THE FINDING THAT MATTERS MOST: 0.3.0 turned a latent commit unsafety into a WRONG ANSWER

`verify_delta_row_level_concurrency` §1 failed at the **first** scenario after the bump: a buffered DELETE
committed **v2 on top of a concurrent autocommit DELETE's v2**, silently resurrecting the row the other
statement had removed. Both statements reported success.

Cause, from the debug log rather than from reading: pre-0.3.0, `TransactionLog.WriteCommitAsync` began with
`if (await _fs.ExistsAsync(targetPath)) throw` before its write-to-temp-then-rename. **0.3.0 deleted that
probe** — correctly, since a check-then-write is not a commit guarantee — leaving the entire guarantee on
`TryWriteAllBytesAsync`. Our `DuckDbTableFileSystem` implements it with DuckDB's `EXCLUSIVE_CREATE`, which a
local **Windows** root does not honour (measured long ago, docs/delta-transactions.md §8.5).

So the property was ALWAYS broken; what changed is that EW stopped compensating for it. And because
sqllogictest runs connections SEQUENTIALLY, the deleted probe had been enough to make every one of these
suites pass — **the old code was correct only for the case with no race, which is the case a test harness
produces.**

Restored as an explicit, labelled approximation inside `DuckDbTableFileSystem.TryWriteAllBytesAsync` (the
probe moved from EW into the one layer that can still see the path). It is pre-bump behaviour exactly:
correct when the writers are ordered, racy when they are not, and NOT a satisfaction of upstream's contract.
It is not paid where it would hurt — a SECRET-named s3 or abfss attach commits through
`S3CommitFileSystem` / `AdlsGen2TableFileSystem`, which have real conditional writes and never reach it.

**The honest end state is Phase D:** make that method REFUSE rather than approximate, behind a per-backend
capability probe rather than a platform guess. Upstream now states the contract, which makes the refusal
defensible where before it would have looked like gratuitous strictness.

### 4a.2 The exemption did not need re-cutting — it needed MOVING, and upstream's objection was right

The 66-line `ExemptRowLevelFromWholeTableRead` patch made the LIBRARY ignore a whole-table declaration the
host had just made. It is now the host **not making the declaration**: `DeltaCatalog` skips
`DeclareWholeTableRead()` when the flush stages row-level deletes at `write_serializable` — EW's own gate
(`exempt && rowLevel && isolationLevel != Serializable`) reproduced on our side of the line.

Identical behaviour, zero divergence, and it lands exactly where upstream said it belonged when it declined
the feature: *"a library must not decide on a host's behalf that it read less than it declared."* Whether our
whole-table flag is a real dependency or a scan artefact is the HOST's knowledge. **We were asking EW to
disbelieve a declaration when we should have been not making it.**

⇒ **offer (2) is withdrawn, and this is the third patch retired by ceasing to need it** (after `RowUpdateMode`
and the `WriteChangeDataFilesForAsync` overload). The known over-breadth is unchanged and now sits in code we
own, where the provenance fix can actually live.

Gate: `verify_delta_row_level_concurrency` **93**, and §11's mutation note was rewritten against the new
mechanism — the old one named a variable (`effectiveReads`) that no longer exists.

### 4a.3 `CheckLogicalRebaseAsync` — upstream ALREADY implements our exemption, on the other surface

Found by asking whether that method is useful to us elsewhere. Its signature carries
**`bool rowLevelDml = false`**, documented as: *"Read-set checks … run UNLESS `rowLevelDml` — row-level mode
replaces them with the row-granular validation the rebase already performed."*

That is our exemption's argument, and **broader**: ours dropped only the `WholeTable` facet, this drops the
read-set checks entirely for a row-level DML.

⇒ **upstream DECLINED these semantics on the transaction surface while shipping them as a public parameter on
the buffered surface.** That makes the offer a genuine INCONSISTENCY argument rather than a semantics request
— which matters, because CLAUDE.md records an earlier attempt to pitch this as an inconsistency being
retracted for being a semantics request in disguise. This one is not: the two surfaces disagree, and
`CheckLogicalRebaseAsync` is the one we agree with.

**Two other uses, one real and one to avoid:**
- **Fail-fast on the eager-write buffer (real).** Our buffered flush writes data files at STATEMENT time and
  learns of a conflict only at COMMIT, so a doomed `BEGIN … COMMIT` writes every byte and then relies on
  `DiscardDataFilesAsync` to reclaim them. This method is cheap (reads the commits since base, no data IO)
  and could surface the doom earlier. ⚠ It THROWS rather than returning a verdict, and a conflict at
  statement k need not be one at commit (the loop may rebase past it) — so the honest use is a DIAGNOSTIC or
  a warning, never an early hard abort, which would kill transactions that would have committed.
- ⚠ **And the framing to keep:** EW's own comment calls this "what the buffered caller re-validates with",
  and we do not call it — we route through `DeltaTransaction.CommitAsync`. The useful reading is not "so our
  patch covers our path" but **"EW expects a buffered host to use this and we don't"** — a divergence from the
  intended shape that nobody chose. Settle it in Phase C.

### 4a.5 THE OFFERS (2026-08-07) — TWO are open as drafts. A third exists and is NOT being offered

| PR | branch | on the pin? | status |
|---|---|---|---|
| [engineered-wood#90](https://github.com/clast-project/engineered-wood/pull/90) | `offer/dml-checkpoint` | **YES** — we need it | draft, **Closes #86** |
| [engineered-wood#91](https://github.com/clast-project/engineered-wood/pull/91) | `offer/blind-append-declaration` | **YES** — we need it | draft, **Addresses #88**, decision 2 of 4 |
| — | `offer/commit-retry-signal` | **NO** | **built, NOT pushed, NOT recommended** — see below |

Each is cut off `upstream/main` with ONE change and its tests — never off `fabricator-patches-v2`, which
carries the first two — following the `offer/*` convention already in the fork (13 prior branches).

**⚠ THE THIRD IS AN ORPHAN AND SHOULD NOT BE SENT — read this before treating it as pending work.**
`LogCommitRequest.OnRetry` (a callback reporting a commit retry the loop recovered from) is **not a fix**;
nothing is broken, and it adds observability a host without its own retry loop would want. **We are not such
a host.** It was built as §4b.1's unblocker — the plan said deleting our outer OCC retry loop required a
replacement diagnostic first — and then §4b.1 was REVERSED: the loop must stay. So the enabler outlived the
thing it enabled, which is an order-of-operations mistake (build the enabler only after the thing it enables
is confirmed).

Why NOT offer it anyway, given it is written and green (360/360 × 3 TFMs against BARE upstream): this file
already records the rule, from `RowUpdateMode` — *"no divergence left for it to retire and no need for it, so
do NOT bring it; spending credibility on a request we do not need weakens the ones we do."* It retires none
of our divergence and we consume none of it, and #90/#91 are pending review, so a third weaker PR competes
with the two that matter.

**What it is NOT:** it is not on `fabricator-patches-v2`, so the patch set stays **2 files / +69** and nothing
we build contains it. Moving it off the working branch is the whole reason it lives on its own — carrying it
would have made the pin 4 files / +109 for an API no caller in this tree touches.

**What would revive it:** taking §4b.3 / Phase C (constructing `LogCommitRequest` ourselves to reach
`OnCommitDurable`), at which point we would want the retry signal in the same breath and the two go upstream
as a motivated pair. Absent that, **deleting the branch loses nothing** — the design is described here well
enough to re-derive in an afternoon.

**⚠ THE INTERNAL MARKERS MUST BE STRIPPED, and this is the step easiest to forget.** Both branches were
verified to contain **zero** `FABRICATOR` references: the `[FABRICATOR-PATCH: OFFER-READY]` tags and
host-specific wording ("the row-level path our host takes") are OUR bookkeeping and read as noise
upstream. Both were also built and tested STANDALONE off upstream — a patch that only compiles in our
tree is not an offer.

**Each PR states what it deliberately omits**, rather than leaving a reviewer to find the gaps: #91 lists
the three parts of #88 it does not do (writing the flag, unifying the two inferences, the
`metadataChanged` guard); #90 flags that `delta.checkpointInterval` is still ignored.

**⚠ #91 explicitly DECLINES to settle upstream's open question.** #88 lists "absent ⇒ infer" vs "absent ⇒
not blind" as undecided. Ours picks permissive with a stated reason, says plainly that this is
back-compat and **NOT parity with Delta**, and pins it with a named test so switching is one line with a
failing test already pointing at it. Shipping a fait accompli on someone else's open design question is
how an offer gets rejected on grounds that have nothing to do with its merits.

### 4a.4 PHASE B — ALL GATES GREEN

| gate | result | what it proves |
|---|---|---|
| hermetic | **67/67 — 6689**, run TWICE | identical to pre-bump ⇒ behaviour-preserving |
| service | **45/45 — 1678** | `verify_delta_catalog_s3` 196 × 2 legs is the ONLY place the new `TryWriteAllBytesAsync` meets real `s3://` through `S3CommitFileSystem` — the commit primitive I replaced |
| EW Table.Tests | **832 × {net10.0, net8.0, net472}** | only the net472 leg proves a change offerable |
| EW DeltaLake.Tests | **364 × 3 TFMs** | the isBlindAppend patch's own gate, mutation-tested |

⚠ The hermetic tier CANNOT reach `S3CommitFileSystem` at all — the branch is
`if (s3 is not null && path.StartsWith("s3://"))`, and every hermetic table is a local scratch path while
`run-suites.sh hermetic` CLEARS the service env vars to prove hermeticity. So the branch is unreachable by
construction, not merely uncovered.

**⚠ AND THE CI TIERS ARE NOT THE WHOLE STORY — user-caught. `AdlsGen2TableFileSystem` is reached by NEITHER
tier, and its rewrite changed MECHANISM (a conditional RENAME became a conditional UPLOAD with
`IfNoneMatch=*`) where S3's only changed shape.** Both live legs were therefore run on the 0.3.0 branch,
and in each case the assertion count is NOT the evidence — the routing log is:

| live leg | result | the evidence that it exercised the new primitive |
|---|---|---|
| plain ADLS Gen2 (`verify_delta_catalog_adls`) | **55**, passed | `AdlsGen2TableFileSystem` selected **53×**, **8 commits** written |
| Fabric OneLake (round trip: CTAS → INSERT → DV DELETE → fused txn → DROP) | all five steps arithmetically correct | `AdlsGen2TableFileSystem` **37×** with **0** fallbacks, `onelake://` **9×**, commits at v1/v2/v4, **0** errors/conflicts |

**The OneLake run also VERIFIES THE TWO-SCHEME SPLIT on 0.3.0**, which is the part worth knowing: the
**LOG** commits over `abfss://` through our direct-SDK filesystem (⇒ straight through the new
`TryWriteAllBytesAsync`), while the **DATA** parquet moves over the `onelake://` VFS to DuckDB's own
reader/writer. Two different transports in one statement, both green.

⚠ Why this mattered more than the S3 leg: CLAUDE.md records a MEASURED live finding that a conditional
create on an EXISTING OneLake path answers **409 PathAlreadyExists, not 412**. Had `UploadAsync` with
`IfNoneMatch` answered with anything my catch does not list, the exception would ESCAPE instead of
returning `false` — a hard commit failure with no OCC retry, which is the exact shape of the raw-412 bug
already in the record. A compile and a green hermetic tier say nothing about that.

⚠ §4's numbers are STILL not re-taken, and deliberately: they are confounded by #86 (DML tables never
checkpoint), so measuring before that is understood would produce another figure needing withdrawal.

## 4b. CAN 0.3.0 SIMPLIFY OUR BUFFERING / READ-YOUR-WRITES? — analysed AND MEASURED 2026-08-07 (user-asked).
CLOSED: **nothing to delete**, one flagged hazard that does not reach us, and a firm NO on the part everyone assumes

> ⚠ **The analysis got TWO of its five answers wrong, and both in the same direction — by reasoning about
> what the code should do instead of running it.** 4b.1 opened "✅ DELETE our outer OCC retry loop"; the loop
> is load-bearing. 4b.2 predicted our appends would start conflicting; they do not, and the same fact
> explains both. What survives is the measurement and the gate — the deletion was never made and the
> imagined regression was never real. Kept in full, with the corrections in place, because "one commit base
> is freshly opened and the other is pinned at statement time" is the fact everything here turns on and it
> is not visible from either method signature.

### 4b.1 ❌ DO **NOT** DELETE our outer OCC retry loop — the offer is BUILT, the deletion is REFUSED, and the second reason is the real one

> **RESOLVED 2026-08-07.** The ✅ below was written before 4b.2 was measured, and 4b.2 changed the answer.
> **Option (1) was BUILT — `LogCommitRequest.OnRetry`** (`CommitRetryInfo`
> carrying the lost version, the latest version, and the attempt index; mutation-tested — moving the
> invocation above the verdict throw kills the "a conflict is not a retry" test).
> **⚠ AND IT IS AN ORPHAN — built, then made pointless by this very reversal. It is NOT on the pin and is
> NOT being offered** (§4a.5 has the full reasoning): we do not consume it, our own loop already logs, and
> carrying it would grow the patch set from 2 files / +69 to 4 / +109 for an API nobody here calls. It sits
> on `offer/commit-retry-signal` off `upstream/main`, green at 360/360 × {net10.0, net8.0, net472} against
> BARE upstream. **Taking it and deleting our loop is REFUSED**, because the diagnostic was never the only
> thing the loop does:
>
> **⚠ THE OUTER LOOP REOPENS; THE INNER ONE DOES NOT — and that is a behaviour difference, not a
> duplication.** `LogCommitter` holds `BaseSnapshot` FIXED across its attempts and re-runs the checker over
> the same widening range, so a concurrent **metaData** — which conflicts unconditionally (§4b.2) — is
> permanent within it. Our loop catches the exception and OPENS THE TABLE AGAIN, so the next attempt's base
> is past the metadata commit and the append succeeds. Deleting the loop would therefore turn a currently
> successful append into a hard failure whenever a property or schema edit lands in the window between our
> open and our write. Narrow window, real behaviour. Source-established (`catch (DeltaConflictException)`
> → reopen → retry), not measured — the window is microseconds and cannot be opened from SQL.
>
> ⇒ **the loop stays, and now carries a comment saying why.** The 16 × 16 "multiplicative" objection below
> still stands on paper and has never been observed; it is the price of the recovery. And with the deletion
> refused, `OnRetry` has nothing left to serve HERE — it is not being offered either (§4a.5).
>
> **⚠ AND BE HONEST ABOUT WHAT THE REOPEN IS.** Upstream's own comment on that hunk says the opt-out it
> would accept *"reopens a real hole, so it should be asked for rather than offered"* — and a reopen-and-
> recommit is that opt-out, taken locally without asking. What we keep is therefore a deliberate
> permissiveness, not an accident: an append whose files were written against the pre-change schema can be
> committed into the post-change table. Its blast radius is bounded by the window (our open → our write) and
> by Delta's own semantics for the common case — a file missing a column added since reads as NULL, which is
> what schema evolution means. It is NOT bounded for a type change. Nobody has measured that; the honest
> statement is "narrow window, benign for ADD COLUMN, unexamined for the rest."

`DeltaCatalog.FlushDeferredFilesAsync` (`:3016`) is a hand-rolled `for (attempt = 1; ; attempt++)` with

`DeltaCatalog.FlushDeferredFilesAsync` (`:3016`) is a hand-rolled `for (attempt = 1; ; attempt++)` with
`maxAttempts = 16` that REOPENS the table and re-commits on `DeltaConflictException`. Upstream's
`CommitDataFilesAsync` now builds a `LogCommitRequest` with **`MaxAttempts = 16`** and
**`Rebase = new RecomputeRebaseHandler(BuildActionsAsync)`**.

⇒ ours is **redundant AND multiplicative** — 16 outer × 16 inner is up to 256 attempts — and it is the
WORSE loop: a blind reopen-and-recommit against EW's re-derivation of the actions against the version that
actually landed.

**⚠ But it is not a free deletion, and the reason is recorded in its own comment.** The loop exists partly
to LOG: *"a silent retry makes multi-writer behaviour unobservable — a successful concurrent run and a run
whose writers merely serialized look identical from the outside."* That diagnostic was added after the
OneLake multi-writer investigation and is what proved the commit guard was ever exercised (docs/
delta-transactions.md §8.1 relies on the retry COUNT to declare a run non-void). `LogCommitter` takes no
logger. **So the deletion must come with a replacement signal**, or we lose the one instrument that
distinguishes a real concurrency test from a vacuous one — offering an `OnConflict`/attempt callback on
`LogCommitRequest` is the natural upstream ask, and it is small.

### 4b.2 ✅ MEASURED 2026-08-07 — THE BEHAVIOUR CHANGE DOES NOT REACH US, and the reason is a structural fact worth gating

Upstream's own note on the `CommitDataFilesAsync` hunk: this path *"used to retry straight through a
concurrent schema change and commit files against a schema that had moved"*, and now the checker's
metadata/protocol rule applies — *"if a host turns out to depend on the old permissiveness … the fix is a
public opt-out on the request rather than a quiet revert."*

**This section previously read "our flush is exactly such an append" and predicted a new conflict. THAT WAS
WRONG, and it was wrong about which path our append takes.** Measured on both engine legs
(`verify_delta_catalog_transactions` §41):

| buffered path | where the COMMIT's base snapshot comes from | concurrent metadata change ⇒ |
|---|---|---|
| **APPEND** (`FlushDeferredFilesAsync`) | a FRESH `DeltaTable.OpenAsync` at COMMIT, and again on every retry | **commits** — the concurrent range is EMPTY |
| **DML** (the pair's held `DeltaTransaction`) | pinned at STATEMENT time by the hoist | **conflicts** — *"Concurrent commit N changed the table metadata."* |

(`CommitDataFilesAsync` sets `BaseSnapshot = CurrentSnapshot` and `Reads = ReadSet.Blind`, so our reopen is
exactly what moves the base — and being blind, metadata/protocol is the ONLY rule that can touch that path.)

The rule can only fire over the commits between the COMMIT's BASE version and the version it attempts. Our
flush's base IS the latest version, so there is nothing for the new rule to examine — the property edit is
simply an earlier version. Version ladder from the measurement, which is the proof the window was genuinely
open rather than the edit having landed late: `3 SET TBLPROPERTIES` then `4 WRITE`, the append's own commit.

**⚠ And the DML half is NOT new either — this is the question that produced the test.** The pre-bump
`ConflictChecker` (`git show 3794fe4:src/EngineeredWood.DeltaLake.Table/Concurrency/ConflictChecker.cs`, <!-- check-docs:ignore (a path at the OLD pin — it moved namespaces at 0.3.0, so its absence at HEAD is the point) -->
lines 121-127) carried the identical unconditional metadata rule at the identical place, and the DML commit routed through
it then as it does now. Source-established, not measured — re-measuring it would mean building the Bridge
against the old pin, which it no longer compiles against.

**And the SCHEMA case — the one upstream's comment is really about — is measured too, on both engines.** A
buffered `INSERT` racing an `ALTER TABLE … ADD COLUMN` commits (`2 ADD COLUMNS` then `3 WRITE`) and the data
is right: the eagerly-written file predates the column, and every row reads it as NULL, which is what Delta
schema evolution means. ⚠ That argument covers ADD COLUMN and is **unexamined for a type change** — say it
that way rather than "concurrent schema changes are safe".

**⇒ nothing to do, and no opt-out to ask upstream for.** What came out of it instead is a gate, because the
append half is one line from being lost: [delta-snapshot-caching.md](delta-snapshot-caching.md) proposes
caching the immutable `Snapshot` per (txn, path, version) to kill the redundant `_delta_log` listings, and
**serving the flush's open from such a cache would hand it a STALE base** — at which point every append
racing a property edit (ours, a Spark job, an OPTIMIZE writing clustering metadata) starts conflicting. That
hazard is not mentioned in the caching design and is exactly the kind of thing a "pure performance" change
gets away with silently. §41 pins both halves, mutation-tested with two mutants killed at their own
assertions.

### 4b.3 `LogCommitRequest.OnCommitDurable` — a precise guard for a hazard we currently handle by re-reading

*"Invoked THE INSTANT the commit is durable … a caller holding a list of files to clean up on failure must
forget them here … a cancellation between the write and the refresh would otherwise surface as a failed
commit whose cleanup deletes live data."*

That names our shape: `DiscardBufferedFiles` reclaims `pending.Files` on rollback, and our flush runs under
an `InterruptScope` whose token reaches `CommitDataFilesAsync` — so a Ctrl+C between `WriteCommitAsync` and
the snapshot refresh (both take that token) makes a DURABLE commit throw.

**⚠ BUT THE HAZARD DOES NOT REACH US, and the reason is NOT the one recorded here first (2026-08-08).** This
entry used to say *"we are protected today, but INDIRECTLY: `DiscardDataFilesAsync` re-reads a FRESH log and
refuses anything it references."* True about that method and beside the point, because **nothing ever asks
it**: `CommitTransaction` calls `_txnBuffer.Remove(txnId)` BEFORE the flush loop, so a throw out of the flush
leaves `RollbackTransaction` with `tables is null` and it returns immediately. There is no cleanup to be
wrong. The protection is an ORDERING in our own code, and the EW guard is a second line with no first line.

Probed for a reachable route and found none: a buffered INSERT rolled back and an identity CREATE + INSERT
rolled back both reclaim cleanly (`reclaimed 1 of 1`), and a `CREATE OR REPLACE` inside a transaction commits
its own files while contributing NONE to `pending.Files`. ⇒ **`OnCommitDurable` would close a window we do
not have.** Kept in this list only because the reasoning generalises: any future change that flushes BEFORE
removing the buffer, or that reclaims from a source the flush does not clear, re-opens it.

What the probe DID produce is a fixed log line: the referenced-file branch reported *"they remain as
invisible orphans for VACUUM"*, which asserts the opposite of what happened — a referenced file is live table
data. Now two branches with an accurate message each, and the anticipated one says plainly that no route to
it is known. ⚠ It is NOT gated: logging is off by default and no suite asserts log text, so this rests on the
probes above rather than on a test.

Still Phase C if it were ever wanted: `OnCommitDurable` is set INSIDE `CommitOccAsync` (`DeltaTable.cs:2636`,
wired to EW's own `WrittenFileLedger.Clear`) and is not a parameter on anything we call, so using it means
constructing `LogCommitRequest` ourselves — i.e. driving `LogCommitter` instead of the table API, and owning
action assembly, rebase handlers, preconditions and protocol gating with it.

### 4b.6 RESUMING 4b — the concrete plan, so it is not re-derived

**Do 4b.2 FIRST, and this ordering is not arbitrary.** 4b.2 is a *behaviour change already shipped* in the
0.3.0 pin (pushed, all tiers green) that nothing tests in either direction; 4b.1 is a cleanup that changes
nothing a user sees. Verifying what we already shipped outranks tidying what we already have.

> **✅ 4b.2 IS DONE (2026-08-07)** — answer in §4b.2 above: the change does not reach us, and the prediction
> written here was wrong about which path our append takes. Gate `verify_delta_catalog_transactions` §41,
> 965 → 1000 per leg. **The plan below is kept verbatim because the SHAPE was right even though the
> hypothesis was not** — the buffered path really is required, the control really was load-bearing, and
> "pin whichever answer is real rather than the one expected" is what made the wrong prediction cheap.
> ⚠ One correction it earns: the shape as written (`INSERT`) exercises the APPEND path, which turned out to
> be the half that does NOT conflict. The DML half needed a `DELETE`, and only running both showed that the
> two paths differ at all.

**4b.2 — append vs a concurrent metadata change.** The test shape, which needs care because sqllogictest
runs connections SEQUENTIALLY and the window has to be opened deliberately:

```
con1: BEGIN; INSERT INTO t VALUES (…);      -- pins the base snapshot, writes files eagerly, commits nothing
con2: SELECT fabricator_delta_set_tblproperties('cat', 'main.t', '{"custom.k":"v"}');   -- lands a metaData
con1: COMMIT;                                -- ← does this now conflict where it used to succeed?
```
The buffered path is REQUIRED: an autocommit INSERT has no window between pinning and committing. Assert
BOTH directions — that the metadata change is what causes it (a control run without con2 must commit) —
and pin whichever answer is real rather than the one expected. ⚠ Upstream's own note says a host depending
on the old permissiveness should ask for *"a public opt-out on the request rather than a quiet revert"*, so
if this turns out to break a real shape, the ask is upstream-shaped, not a local patch.

**4b.1 — the deletion is ~25 lines and is BLOCKED on the diagnostic, not on the code.** Three options, in
preference order:
1. **Offer upstream an attempt/conflict callback on `LogCommitRequest`** (it already carries
   `OnCommitDurable`, so the shape is established and the change is small), then delete our loop and log
   from the callback. Matches the "prefer upstream" stance and is the only option that keeps both
   properties.
2. Delete the loop and accept losing the signal. ⚠ **This breaks a documented METHOD, not just a log line**:
   [delta-transactions.md](delta-transactions.md) §8.1 uses the retry COUNT to declare a multi-writer run
   non-void, so without it a run whose writers merely serialized is indistinguishable from one where the
   commit guard fired.
3. Keep it, with a comment saying it is deliberately redundant and why. The only cost is 16 × 16 attempts
   under extreme contention — which nothing has ever hit.
**Until (1) lands, (3) is the correct state and the loop should NOT be deleted.** Leaving it there is not
an oversight; deleting it without a replacement signal would be.

**4b.3 (`OnCommitDurable`) is Phase C**, not 4b: it needs us to construct `LogCommitRequest` ourselves,
which is the `LogCommitter` architectural decision.

> **✅ 4b IS CLOSED (2026-08-07).** 4b.2 measured and gated (§41, 965 → 1021 per leg); 4b.1's offer BUILT and
> its local deletion REFUSED with a reason better than the one that blocked it; 4b.3/4b.4/4b.5 unchanged.
> **The one thing to carry forward is a WARNING, not a task**: the append's immunity in 4b.2 and the loop's
> recovery in 4b.1 are the SAME fact — our commit base is always freshly opened — so any change that makes a
> commit reuse an older snapshot breaks both at once. That is precisely what
> [delta-snapshot-caching.md](delta-snapshot-caching.md) proposes, and §3.5a there now says so.

### 4b.4 ❌ READ-YOUR-WRITES IS **NOT** SIMPLIFIABLE, and this is the firm answer

`DeltaTransaction.Snapshot => _baseSnapshot` is unchanged at 0.3.0, and the class doc still says
*"nothing is visible until the commit, but the bytes are there."* Upstream exposes **no** transaction-visible
snapshot, no pending-state read surface, nothing that overlays uncommitted actions onto a scan.

So the whole overlay stack stays ours by design, not by accident: the virtual-table composition in
`DeltaCatalog.ScanCodec` / the native reader's pending inputs / `ScanPendingCreated`, the pending-ordinal
encoding (`0x780000+idx`), the RENAME overlay map, `PendingArrowSchema`. **Do not go looking for an upstream
replacement — it does not exist, and the reason is structural**: EW commits at COMMIT, and a host that wants
statement-level visibility of its own uncommitted work has to build that visibility itself. `DeltaTxnBuffer`
is 402 lines and none of them are duplicating library code.

### 4b.5 ❌ The two file ledgers are inherently SPLIT — not a duplication to collapse

EW's `WrittenFileLedger` (`transaction.Written`) collects what **EW's own writers** produced; our
`pending.Files` + `DiscardDataFilesAsync` reclaim what the **HOST** wrote eagerly. EW's provenance rule
deliberately never collects a host-written file (a DV DELETE re-adds an EXISTING parquet that is live table
data). ⇒ two ledgers because there are two writers, which is the eager-write design working, not an
artefact.

## 4c. CHECKED AGAINST DELTA OSS `master` (2026-08-07, user-asked) — three divergences, all PERMISSIVE

Read `spark/src/main/scala/org/apache/spark/sql/delta/ConflictChecker.scala` at **master**, not the
`v4.2.0` tag this project read before. Nothing we do is a protocol violation — the conflict rules are
engine policy, not the Delta protocol — but all three departures lean the SAME way: we permit commits
Delta would conflict. State them as departures, never as parity.

### (1) The ABSENT flag — known, deliberate, still permissive

```scala
val blindAppendAddedFiles: Seq[AddFile] = if (isBlindAppendOption.getOrElse(false)) { addedFiles } else { Seq() }
val changedDataAddedFiles: Seq[AddFile] = if (isBlindAppendOption.getOrElse(false)) { Seq() } else { addedFiles }
```
`getOrElse(false)` — **absent ⇒ NOT blind**, unchanged from v4.2.0. Our patch falls back to the INFERENCE,
which can answer "blind". So an adds-only commit from a writer that emits no flag is exempted by us and
examined by Delta. Deliberate (EW emits no flag itself, so `getOrElse(false)` would make ordinary EW-to-EW
appends conflict) and now gated by `AbsentFlag_FallsBackToInference` — but it is the permissive direction,
and #88 lists exactly this as an open decision upstream. **Do not settle it unilaterally.**

### (2) ⚠ THE `metadataChanged` GUARD IS MISSING, and this is the one to fix

```scala
val addedFilesToCheckForConflicts = isolationLevel match {
  case WriteSerializable if !currentTransactionInfo.metadataChanged =>
    winningCommitSummary.changedDataAddedFiles
  case Serializable | WriteSerializable =>
    winningCommitSummary.changedDataAddedFiles ++ winningCommitSummary.blindAppendAddedFiles
  ...
```
Delta's WriteSerializable relaxation applies **only when OUR OWN transaction did not change metadata**;
a metadata-changing transaction re-examines blind appends. EW's condition is
`examineAdds = isolation == Serializable || !concurrentIsBlindAppend` — **no such guard**.

⇒ a fabricator transaction that runs at `write_serializable` AND carries a buffered ALTER (our
`pending.HasAlter` path fuses schema changes into the same commit) exempts concurrent blind appends where
Delta would examine them. CLAUDE.md already flagged this as "not investigated"; it is now CONFIRMED against
master and it is a second independent permissive divergence. It belongs in the #88 conversation, because
"read the flag" and "guard the relaxation" are the same paragraph of Delta's logic.

### (3) `readWholeTable` + ANY remove ⇒ conflict, with NO isolation gate — which confirms today's move is a DEPARTURE

```scala
if (winningCommitSummary.removedFiles.nonEmpty && currentTransactionInfo.readWholeTable) {
  throw DeltaErrors.concurrentDeleteReadException(...)
}
```
`checkForDeletedFilesAgainstCurrentTxnReadFiles` has **no isolation-level branch at all**, while
`checkForAddedFilesThatShouldHaveBeenReadByCurrentTxn` does. So Delta gates **concurrentAppend** on the
isolation level and **never** gates concurrentDeleteRead.

Our §4a.2 change withholds the whole-table declaration for a row-level DML at write_serializable, which
makes this check not fire. That is exactly the departure CLAUDE.md describes — *"a DEPARTURE from Delta's
`concurrentDeleteRead` rule, not an API inconsistency"* — and master confirms the characterisation
precisely. **Moving it from an EW patch into the host changed WHERE the decision is made, not WHETHER we
depart.** OSS Delta has no row-level-concurrency notion to appeal to (that is a Databricks feature), so
there is no upstream rule that sanctions it; the justification remains ours and rests on the row-level
write validation having already proven the removed rows were undisturbed.

**⇒ For the #88 offer:** present the read half as *"believe a declaration when one is made"*, and say
plainly that our absent-case fallback and the missing `metadataChanged` guard are NOT Delta-equivalent.
Claiming parity would be false in two places and would be caught.

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
