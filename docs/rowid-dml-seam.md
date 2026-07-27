# Design: the DML row-identity seam, and how it evolves

**Status (2026-07-27): §3's DV-DML half is BUILT and green** on `v1.5-variegata` — the deletion-vector
DML boundary between fabricator and engineered-wood is now keyed by `(path, positions)`, not by a file
ordinal. See §3.1 for exactly what landed and what did not. The rest (§4's STRUCT rowid, the remaining
`*ByRowIds*` entry points) is still design. Companion to [rowid-concepts.md](rowid-concepts.md), which
pins the CONCEPTS and the measured user surface; this doc is the PLAN.

Note this did NOT need to wait for a fabricator `main` branch pinned to duckdb `main`: the translation
is ours, on our side of the boundary, so it is independent of whatever eventually supersedes rowid DML.
Only the questions in §7 are.

Everything below is marked **VERIFIED** (checked in source, with the location), **PROPOSED**, or
**UNKNOWN**. That distinction is the point of the document: this area has repeatedly produced confident
statements that the code contradicted, including several of mine in the session that produced this file.

---

## 1. The problem in one paragraph

DuckDB identifies rows for DELETE/UPDATE by `rowid`. Our Delta provider mints a TRANSIENT LOCATOR
`(fileOrdinal << 40) | position` at scan time and hands it back at DML time; engineered-wood's DML APIs
take that ordinal and re-derive the same path-sort to resolve it. So a DuckDB implementation detail (a
64-bit rowid) has propagated through our encoding into EW's public API shape. Three layers are coupled
to one representation. The goal is to decouple them: EW should take `(path, positions)` — a
self-describing key — and we should own the translation.

---

## 2. Verified facts (the ones the design rests on)

| # | fact | where |
|---|---|---|
| V1 | **`LogicalDelete` carries NO predicate** — only `table`, `table_index`, `return_chunk`, `bound_constraints`. The WHERE lives in the child plan over a rowid-producing scan. This is WHY rowid DML exists for Delta: a "capture the FilterNode" predicate delete would be unsafe because pushdown is a superset. | `duckdb@d8cdaa33 src/include/duckdb/planner/operator/logical_delete.hpp` |
| V2 | **A compound VIRTUAL rowid as a STRUCT is ALREADY implemented** — the branch exists and builds `LogicalType::STRUCT` from `virtual_rowid_columns`. Its comment says "not used by the Delta provider, which has a single BIGINT row id". | `src/catalog/fabricator_schema_entry.cpp:256` |
| V3 | ...but that branch hardcodes `LogicalType::BIGINT` per member, so it yields STRUCT-of-BIGINT. A `file_path VARCHAR` member needs provider-supplied member types. Precedent sits directly below it: `provider_virtual_columns` already carries `(name, LogicalType)` pairs from C#. | same file, the block after |
| V4 | **`BuildModifyTarget` already destructures a STRUCT rowid** into per-member types, so the DML side needs no work for STRUCT. | `src/catalog/fabricator_catalog.cpp:391-397` |
| V5 | **Our DV delete is already ZERO-data-read**: its only read is `_dvReader.ReadAsync(addFile.DeletionVector)`. No `ReadFileAsync`. So "zero-read selection DELETE" is not a new capability FOR US — the gain there is naming and a spec-facing surface, and the performance gain belongs to EW's other callers. | `DeleteByRowIdsViaVectorsAsync`, EW `DeltaTable.cs` |
| V6 | **The transient locator and the stable id are NOT confused in code, only in names.** The buffered-UPDATE loop uses `rid` for the DV target (`rid >> RowIdPositionBits` → `DeletedByOrdinal`) and `srcTracking[..].Ids` for identity, in adjacent lines. | `dotnet/Fabricator.Bridge/DeltaCatalog.cs` ~2745-2765 |
| V7 | **duckdb-mysql's remote pushdown**: a pure-remote DELETE (`WHERE i = 0`, `WHERE i >= 3 OR v = 20`, bare `DELETE FROM t`) becomes ONE remote SQL statement, visible as `MYSQL_QUERY` in EXPLAIN. Anything with `USING` is not pushed down — and duckdb-mysql has **no rowid fallback**, it simply errors ("DELETE syntax not supported"). | `duckdb/duckdb-mysql` `test/sql/remote_pushdown/remote_pushdown_delete.test` |
| V8 | The packing limits: 40 bits position, leaving 23 signed bits of ordinal ⇒ **~8.4M files** and ~1.1T rows/file. | `RowIdPositionBits = 40`, EW `DeltaTable.cs` |
| V9 | **The ordinal round-trip was a pure detour.** `RebaseDvDmlActionsAsync`'s FIRST act with `newPositionsByOrdinal` was to convert it to an `oursByPath` dictionary and use only that for the rest of the method — so the caller encoded path→ordinal and EW immediately decoded ordinal→path, with a lossy integer in between. `ComputeDeletionVectorActionsAsync` likewise used the ordinal for nothing but `ordered[ordinal]`. | EW `DeltaTable.cs`, both methods (pre-change) |
| V10 | **An unresolvable ordinal was SILENTLY SKIPPED by both** — `if (ordinal < 0 \|\| ordinal >= ordered.Count) continue;` and `if (kvp.Key >= 0 && kvp.Key < fromOrdered.Count)`. So identifiers captured against the wrong snapshot did not fail; they deleted NOTHING, with no error. This is what makes the path key a correctness fix rather than tidying: a path that is not active is recognisably wrong. | same, pre-change |

### The consequence of V7 that reverses an earlier conclusion

Pure-remote DML needs an engine to send the statement to. SQL Server has one; **Delta does not** — it is
files plus a log. So the pure-remote path is structurally inapplicable to Delta *as SQL*, and **the Delta
rowid path is PERMANENT, not transitional.** An earlier version of this reasoning said "don't invest in
the rowid shape, it's about to be replaced" — that was wrong, and it is the main reason this work is
worth doing rather than deferring.

But the Delta *equivalent* of pure-remote exists: hand the provider the **predicate** instead of the
statement. EW already has `DeleteAsync(Predicate)` / `UpdateAsync(Predicate)`. Per V1 the only blocker is
that DuckDB gives us no predicate at `PlanDelete`. So whatever mechanism supersedes rowid DML would
UNBLOCK predicate-based Delta DML — a capability our own notes record as blocked.

---

## 3. The design (DV-DML half BUILT — see §3.1; the rest PROPOSED)

EW ends up with **two entry points, neither of which knows anything about DuckDB**:

| entry point | fed by | EW does |
|---|---|---|
| predicate | a future pure-remote/predicate hook | its existing mask-based DML |
| `(path, positions)` — `FileRowSelection` | today's rowid, translated by us | DV union / rewrite |

Our boundary becomes: *whatever DuckDB gives us* → `(path, positions)`. When DuckDB's mechanism changes,
only that translation moves; EW and the Delta log layer never notice.

**Translation uses `PlanFiles`, which already returns both keys** —
`PlannedFile(AddFile File, int Ordinal)`. Decode `rowid >> 40` → find the `PlannedFile` with that
ordinal → `File.Path`. `PlanFiles` therefore becomes MORE central under this design, as the ordinal↔path
dictionary.

### Scope choice, and why additive

Add path-keyed **overloads** rather than replacing the ordinal-keyed ones: upstream's own callers and
tests keep working, so the change is upstreamable rather than a fork. `FileRowSelection` from the parked
proposal is exactly this shape.

### The ordinal-keyed EW surface we would migrate

| API | keyed by | status |
|---|---|---|
| `ComputeDeletionVectorActionsAsync(positionsByOrdinal, …, resolveAgainst)` | ordinal | ✅ **DONE** (§3.1) — path-keyed core; the buffered DV DELETE flush and merge-on-read UPDATE both use it |
| `RebaseDvDmlActionsAsync(… newPositionsByOrdinal …)` | ordinal | ✅ **DONE** (§3.1) — path-keyed core (Layer 3 (B) row-level remap across a concurrent rewrite) |
| `DeleteByRowIdsViaVectorsAsync` / `DeleteByRowIdsAsync` | rowid | ✅ **DONE** (§3.2) — new `DeleteBySelectionViaVectorsAsync` / `DeleteBySelectionAsync` are the cores; the rowid forms are adapters |
| `UpdateByRowIdsAsync` (×3 overloads) | rowid | ✅ **DONE** (§6) — the core is `UpdateBySelectionCoreAsync` and hands the rewriter `(filePath, batches, positionsPerBatch)`; all three rowid overloads are re-packing adapters |
| `ReadRowsByRowIdsAsync(rowIds, …)` | rowid | ⬜ **THE LAST ONE.** Takes rowids AND emits packed rowids (`rowIdsOut`) plus `sourceRowTrackingOut` — converting it means replacing those three out-params with the `_metadata` struct, which is the same unification (§6.1) |
| `CommitDataFilesAsync(… deletedPositionsByFileIndex …)` | **index into not-yet-committed written files** | ✅ **stays index-keyed, correctly** — a DIFFERENT index space (our `0x780000+` pending files). Those files are in no snapshot, so no path can name them. Not a gap. |

### 3.1 What landed (2026-07-27)

**EW** — `FileRowSelection.cs` (taken from the parked prototype so the shape stays compatible with it:
`IReadOnlyDictionary<string /*add.path*/, IReadOnlyCollection<long> /*absolute positions*/>`), plus
path-keyed overloads of the two methods above. The path-keyed form is the CORE; the ordinal-keyed form
is a thin adapter (`SelectionFromOrdinals`) that resolves and delegates, so there is one implementation.

The two forms differ deliberately in strictness, which is the whole point of V10:

- the **ordinal** adapter keeps SKIPPING an out-of-range ordinal — its historical contract, and upstream's
  own tests depend on it (**11** call sites across `BufferedTransactionTests` / `ReadWithRowIdsTests` /
  `SparkInteropTests`, which now also give the adapter free coverage);
- the **path** core THROWS on a path that is not active in the resolved snapshot, and `RebaseDvDmlActionsAsync`
  additionally throws if a selected path was not active in `from` — the snapshot the selection claims to
  come from.

**Bridge** — the buffered flush (`DeltaCatalog.FlushDmlTransaction`) and the merge-on-read UPDATE
(`DeltaReader.MergeOnReadUpdateAsync`) now decode the rowid's ordinal to a path THEMSELVES, via
`PlanFiles` (`PathsByOrdinal`), and pass a `FileRowSelection`. An ordinal that does not resolve is a loud
error naming the version and the active-file count. The ordinal is *our* encoding of *our* rowid, so the
decode belongs on our side.

**The loop is now closed through one planner, on both read paths** (checked, not assumed): the native
minting path is `BuildNativeScanListAsync` → `PlanFiles`, and the CODEC minting path is EW's
`ReadWithTransientRowIdsAsync`, which `foreach (var (addFile, ordinal) in PlanFiles(filter, snapshot))`
— it was moved onto `PlanFiles` by the earlier increment. So every ordinal that exists was produced by
`PlanFiles`, and every ordinal that is consumed is resolved by `PlanFiles`. Encode and decode cannot drift.

**And this is where the PRE-PRUNE ordinal contract earns its keep a second time.** The codec minting scan
calls `PlanFiles(filter, …)` — pruning leaves GAPS — while the decode calls it UNFILTERED. That only works
because the ordinal is an index into the *unfiltered* path-sorted set: the unfiltered plan is therefore a
superset containing every ordinal any filtered plan could have emitted, so a selection resolves correctly
no matter which predicate the scan that minted it happened to push. A post-prune (dense-per-filter)
ordinal would make the decode depend on reproducing the scan's filter — which the DML side does not have.

**Tests** — EW `FileRowSelectionTests` (6): the two keyings name the same rows; a path-keyed delete
removes exactly the selected rows across files; **the silent-loss case** (identifiers resolved against a
shrunk snapshot ⇒ ordinal form reports 0 rows deleted with no error, path form throws); unknown path;
the row-level rebase composing with a concurrent same-file delete; a rebase selection naming a
non-`from` file. Every fixture has **three files** per §6's trap.

**Gate** — EW Table.Tests **555** × {net10.0, net8.0, net472} (was 549) / DeltaLake 217 / Expressions 139
/ Core 430; fabricator `verify_delta_catalog_transactions` 941, `verify_delta_row_level_concurrency` 70,
`verify_delta_row_tracking_virtual` 299, the full hermetic tier **53/53 @ 4152** and the service tier
**42/42 @ 1227** — both the SAME counts as before the change, which is the signal that re-keying the
DV-DML boundary is behaviour-neutral for everything pinned (a re-key that quietly dropped or mistargeted a
row would move a count, not just fail a suite). The service tier is the one that matters most here:
`verify_delta_catalog_s3` runs the buffered DML flush over S3, i.e. against the real `add.path` keys.

**One trap re-learned:** `Dictionary.TryAdd` does not exist on **netstandard2.0**, so it broke the net472
build only — the same class as the earlier `blob[n..]` range-indexer break. Anything offered upstream must
build on every TFM upstream declares, and only the net472 leg proves it.

---

### 3.2 What landed next (2026-07-27, same day): the autocommit DELETEs

`DeleteBySelectionViaVectorsAsync` (deletion-vector) and `DeleteBySelectionAsync` (copy-on-write) are now
the cores; `DeleteByRowIdsViaVectorsAsync` / `DeleteByRowIdsAsync` are adapters that decode via
`SelectionFromRowIds` and delegate. Both fabricator call sites (`DeltaReader`) decode on OUR side through
`PlanFiles` and pass a selection, so the loud-error property extends to the autocommit paths.

Increment 1's core was refactored onto a shared `ResolveSelection(selection, snapshot, op)`, so there is
now ONE place that maps a selection to `AddFile`s and ONE error message shape.

**Note the hazard here is genuinely weaker than in §3.1, and saying so matters:** the autocommit paths run
scan-then-mutate inside ONE statement against ONE snapshot, and `rowLevelRetry`'s reload re-validates
through the already-path-keyed `DeleteDvEdit` records. So there is no pin, no rebase of a stale selection,
and therefore no live silent-loss bug on these paths. What this buys is uniformity plus the removal of a
DuckDB-shaped 64-bit packing from a Delta library's public API — the V8 file-count ceiling does not apply
to a caller that uses the selection form. It is cleanliness with a defensive edge, not a bug fix.

**One trap worth keeping, caught only by reading rather than by the compiler:** the copy-on-write loop
probes the selected positions ONCE PER ROW of the file it rewrites (`targets.Contains(abs)`). The decode
had always handed it a `HashSet<long>`; passing the caller's `IReadOnlyCollection<long>` straight through
compiles fine and silently binds those probes to **LINQ's O(n) `Contains`**, turning a rewrite into
O(rows × selected). `ResolveSelection` therefore materialises one `HashSet` per file, and its doc comment
says why so nobody "simplifies" it back.

### 3.3 The real remaining blocker: PER-ROW identity, not a file key (VERIFIED 2026-07-27)

`UpdateByRowIdsAsync` and `ReadRowsByRowIdsAsync` are **not** waiting on a path-keyed file key. Both carry
per-row identity ACROSS the boundary as a packed rowid, and that — not the file key — is where the
DuckDB-shaped encoding actually lives:

- `UpdateByRowIdsCoreAsync`'s `rewriteFile` callback receives `rowIdsPerBatch`, an `Int64Array` of **packed
  rowids per source row**, so the caller can substitute new values by O(1) lookup against a rowid-keyed
  dictionary. The file-ordinal argument beside it is nearly vestigial: the overload fabricator actually
  uses (`UpdateByRowIdsAsync(RecordBatch updates, …)`) **ignores it entirely** —
  `(ordinal, sourceBatches, rowIdsPerBatch) => ApplyRowIdKeyedUpdates(…)`.
- `ReadRowsByRowIdsAsync` fills `rowIdsOut` with packed rowids per returned row, and its own comment states
  the reason: *"emission order alone cannot key a lookup."*

So path-keying only their INPUT would remove nothing — the packing would still cross in the callback and
the out-param. Converting them means replacing per-row identity with `(file_path, row_index)`, which is
**exactly the parked prototype's `_metadata` shape** (§5). That reframes the remaining work: it is not
"two more overloads", it is the prototype revival, and it should be scheduled as that.

Corollary for §4: a STRUCT rowid would not help here either. The per-row identity is the same problem one
layer in, and `_metadata` already solves it in a Delta-native, Spark-shaped vocabulary.

## 4. Optional second step: the STRUCT rowid (PROPOSED, weigh separately)

Per V2/V3/V4 a `STRUCT(file_path VARCHAR, row_index BIGINT)` rowid is achievable — the machinery exists,
needing only provider-supplied member types. It would retire, all at once: the ordinal, `PlannedFile`
(collapsing `PlanFiles` to `IReadOnlyList<AddFile>`, since `AddFile` already carries `Path`), the
pre-prune-ordinal contract, the V8 file-count ceiling, and the fossil `_metadata.row_id`-as-locator name.

**`PlannedFile` exists for exactly one reason**, worth stating because it is not obvious: pruning removes
files, and a list of survivors cannot tell you where the holes were. Given `[fileB, fileD]` you cannot
recover that they were at positions 1 and 3. If the rowid needed no integer file id, the wrapper would
have no purpose.

**Costs, which are real:**
- A wider rowid (a path string, ~40-100 B, vs 8 B) flowing through every DELETE/UPDATE plan. **Measure a
  large DELETE before committing to this.**
- The integer packing is *exploited*, not merely used: `DeltaRowIdFilter` decodes `rid >> 40` to select
  files and rewrites the low half into a `file_row_number` predicate that parquet ROW-GROUP-prunes; late
  materialization joins back on the integer rowid. Both need reworking (~350 assertions:
  `verify_delta_row_tracking_virtual` 299 + `verify_delta_late_materialization` 57).

Recommendation: do §3 first. §4 is separable and should be justified on its own measurement.

---

## 5. Revival mechanics for the parked prototype

The design's read/selection half is already prototyped, on EW branch **`proto/metadata-dml`**:
`PROPOSAL-metadata-dml.md` @ `0db9507`, prototype `72f2d3d` (read + delete-by-selection) + `2780334`
(update + symbolic `_metadata` predicate lowering). `MetadataDmlTests` 11/11, full suite 339/339 at its
base. It proposes `ReadAllWithMetadataAsync` returning a Spark-shaped `_metadata` struct of `file_path` +
absolute `row_index`.

**Base drift: it sits on `45cced1`, now 78 commits behind `fabricator-patches`.**

### MEASURED conflict surface (2026-07-27) — cheaper than the park note's guess

The park note said "real but bounded conflicts", written against a base long since moved and never
re-checked. Measured with `git merge-tree` (no working tree touched), cherry-picking **in order**:

| commit | prediction |
|---|---|
| `72f2d3d` (read + delete-by-selection) | `DeltaTable.cs` **AUTO-MERGES**. Sole conflict: add/add on `FileRowSelection.cs` — because §3.1 added that file already. Trivial (keep ours; it is the same record with fuller docs). |
| `2780334` (update-by-selection + predicate lowering) | **ONE** content conflict in `DeltaTable.cs`. (Its reported `MetadataDmlTests.cs` modify/delete is an artifact of measuring it against a HEAD where `72f2d3d` has not landed; picking in order removes it.) |

78 commits of drift largely MISSED the prototype because its additions are appended methods rather than
edits to churned regions.

⚠ **Two honest limits on that number.**

1. `merge-tree` measures TEXTUAL conflicts only. The prototype's `DeleteAsync(selection)` "reuses the mask
   path's tail verbatim" — and that tail has since gained row-level-concurrency `DeleteDvEdit` records and
   the stable-id remap. A clean textual merge can still be SEMANTICALLY stale, which is precisely the
   failure mode this repo has hit before (a merge that applied cleanly while quietly dropping a behaviour).
   Budget review time for the merged DML tail, not just for conflict markers.
2. **The revival should NOT take the prototype's DELETE half at all.** §3.1/§3.2 already landed
   selection-keyed deletes, integrated with row-level concurrency and the remap — strictly ahead of the
   prototype's. Taking both would produce duplicate entry points (`DeleteAsync(FileRowSelection)` vs
   `DeleteBySelectionAsync`).

⇒ **What the revival is actually FOR** is the part §3.3 identified and nothing landed yet:
`ReadAllWithMetadataAsync`'s per-row `_metadata`, the `UpdateAsync(selection, updater)` shape that consumes
it, and `MetadataPredicate.TryLower` (the zero-read predicate DELETE). Cherry-picking is therefore the wrong
verb — PORT those three, drop the delete half, and reconcile naming against `DeleteBySelectionAsync`.

### 5.1 `_metadata` should carry FOUR members, not the prototype's two (decision, 2026-07-27)

The prototype's struct is `file_path` + `row_index` — scoped to what a selection DELETE needs. It should
also carry the STABLE identity, because that is what Spark's `_metadata` does for a row-tracking Delta
table, and our own live Fabric/Spark validation has been querying exactly those names all along
(`_metadata.row_id` ×22, `_metadata.row_commit_version` ×5, `_metadata.base_row_id` ×1 in CLAUDE.md).

| member | role | nullable | notes |
|---|---|---|---|
| `file_path` | LOCATOR — the log `add.path`, URL-encoded as stored | no | the selection key |
| `row_index` | LOCATOR — absolute physical position, counting DV-masked rows (Spark semantics) | no | the selection key |
| `row_id` | STABLE identity | **yes** | only meaningful with row tracking on |
| `row_commit_version` | STABLE identity | **yes** | ditto |

**Why the ids must be nullable** (the three `COALESCE` cases in [rowid-concepts.md](rowid-concepts.md)):
a file with NO materialized column (every fresh append — readers derive `baseRowId + position`); a per-row
NULL inside a file rewritten from a source that PREDATED row tracking; and pre-enablement adds whose
`baseRowId` is NULL. The locator members have no such case, so the nullability split is real, not defensive.

**Why this is the shape worth porting.** One struct collapses THREE surfaces that are inconsistent today:

1. EW's trailing `_metadata.row_id` column, which currently holds the **transient locator** under the
   stable id's spec name — the fossil documented in rowid-concepts.md;
2. EW's stable-id **out-params** (`sourceRowTrackingOut`, `strippedRowIdsOut`/`strippedVersionsOut`) — a
   different mechanism for the sibling concept, verified 2026-07-27 to be upstream's, not ours;
3. fabricator's SQL-reconstructed `__delta_row_id`/`__delta_row_commit_version` virtual columns
   (`DeltaNativeReader.RowTrackingExpr`), which exist only because (1) took the name.

It also FREES `_metadata.row_id` to mean the stable id, as spec and Spark do — the naming cleanup that
rowid-concepts.md §"Consequences" item 3 has been carrying as the last open item.

**Separable from the breaking rename.** Unifying EW's internals does NOT require renaming fabricator's
user-visible `__delta_row_id`. Do the internal unification first; the query-breaking rename stays a
separate, explicitly-scheduled decision.

`base_row_id` is deliberately omitted: it is a per-FILE constant, so per-row it is redundant (Spark exposes
it anyway; we can add it if an interop case ever wants byte-parity).

```
EW:          git switch -c rowid-cleanup fabricator-patches   # inherits ALL current patches,
                                                              # variant transport included —
                                                              # nothing to re-apply
             git cherry-pick 72f2d3d 2780334                  # skip 0db9507 (the park doc)
                                                              # conflicts expected in the DML regions;
                                                              # the park note calls them "real but bounded"
fabricator:  git switch -c feat/rowid-path-keying             # pin -> the EW work branch
```

At the end, once green: fast-forward `fabricator-patches` to `rowid-cleanup`, re-pin, merge the fabricator
branch.

> ⚠ **NEVER rebase or force-push `fabricator-patches`.** The release tag `v0.0.1-duckdb1.5.5` pins EW
> `8aa7cfb`; rewriting that history orphans it and makes the tagged release unbuildable from source,
> because `git submodule update` cannot reliably fetch an unreachable sha. The branch advances by
> fast-forward only. Leave `proto/metadata-dml` itself alone so it stays readable as the reference.

---

## 6. Traps

- **A single-file table cannot discriminate the locator from the stable id.** With one file the ordinal is
  0, so `(0 << 40) | position` == `position` == the stable id for a fresh append. Any mix-up is invisible.
  **Every discriminating test in this area needs ≥ 2 files.**
- Renaming the DuckDB-visible surface is **breaking for user queries**. The internal half (stop building
  the locator with `RowTrackingWriter.AddRowIdColumn` under the `VirtualRowIdColumn` name) is free and
  should not wait for the breaking half.
- Upstream shares the naming collapse: its `VirtualRowIdColumn` const names the TRANSIENT column, and its
  true stable-name const `RowIdColumnName` is vestigial (0 usages). So a rename is a JOINT concern — doing
  it unilaterally re-diverges naming right after we spent effort converging it.

---

## 7. UNKNOWN — resolve before building §3

- **What supersedes rowid DML, exactly.** V7 shows the *shape* for a SQL-speaking remote (one pushed-down
  statement, no fallback in duckdb-mysql). It does NOT tell us what a non-SQL provider like Delta is
  handed in the non-pure case, or whether the hook offers a predicate in a form we can consume. Read the
  duckdb `main` catalog/planner hooks before fixing `FileRowSelection` as the long-term seam — if the
  replacement hands providers a richer row identity, shaping toward that beats optimising either rowid.

  **But the predicate MAPPING is not unknown — it exists and works** (checked 2026-07-27).
  `DeltaFilterBuilder` (Bridge) already maps DuckDB's `FilterNode` → `EngineeredWood.Expressions.Predicate`
  and drives Delta file pruning plus parquet row-group/bloom skipping in production. So if a hook hands us
  a predicate, the translation is already built.

  ⚠ **It CANNOT be reused for DML unchanged, and the reason is a one-line comment in the file:**
  ```csharp
  // AND: keep the pushable children (dropping unpushable ones still yields a superset).
  ```
  `BuildAnd` silently DROPS unmappable conjuncts. That is correct for a SCAN — DuckDB re-applies every
  predicate above us, so a superset only costs I/O. For a **DELETE a superset deletes rows the user did
  not ask for.** It is the same hazard that made predicate-delete unsafe in the first place (V1), reaching
  us through the mapper rather than through the plan.

  Note `BuildOr` is ALREADY all-or-nothing, for the mirror reason ("dropping a branch would narrow the
  result → unsafe"). So the required discipline already exists in the file; it needs extending to `AND`.

  ⇒ **Concrete work item, small and contained:** an EXACT mode on `DeltaFilterBuilder` where any
  unmappable node yields `null` for the WHOLE predicate, and the caller falls back to the rowid path.
  A flag turning `BuildAnd`'s drop into a bail-out. Do NOT let a DML caller use the superset builder.
  (Related but distinct: the existing `exact_filter_pushdown` / `ExactFilterPushdown()` capability concerns
  DuckDB ERASING filters so the native SQL must apply them exactly — a different layer, same instinct.)
- Whether a selection DELETE should escape EW's `RejectRowTrackingWrite` (it moves no rows, so arguably
  yes) — an open question in the proposal itself.
- The proposal's other open points: struct vs flat metadata columns; `numRecords`-less foreign tables;
  set-valued literals for large position sets.

---

## 8. Cost of the `main` branch itself

Tracking duckdb `main` means absorbing continuous upstream API churn — the 1.5 `ExtensionLoader` break is
the precedent — and doubling CI minutes. The branch-naming convention already reserves `main` for this
(see CLAUDE.md "BRANCH NAMING"); add it as a nightly allowed-to-fail branch rather than a gate.

---

## 6. The `_metadata` port — BUILT (2026-07-27)

All three items §5 identified are in, and one of them turned out better than the plan expected.

### 6.1 `ReadAllWithMetadataAsync` — the four-member struct

Landed as §5.1 specified: `file_path` + `row_index` (non-null LOCATOR) and `row_id` +
`row_commit_version` (nullable IDENTITY), shape fixed regardless of the table's row-tracking config so a
consumer binds one schema.

**Not a port of the prototype's implementation — deliberately.** That one hand-rolls its own parquet open,
column-mapping rename and DV filter, and its own note conceded the cost ("full schema, no projection,
partition columns not re-added"). But the current base already computes every member on the MAINTAINED read
path: `ReadFileAsync`'s `strippedAbsPositionsOut` is `row_index`, `strippedRowIdsOut`/`strippedVersionsOut`
are the two ids, and the path comes from the planned add. So the implementation is pure assembly (~25 lines)
and inherits projection, column mapping, partition re-add, schema reconciliation and DV semantics for free.

It also **consumes the out-params that were the second, inconsistent mechanism** — which is the unification
this was for: the locator used to arrive as a column while its sibling identity arrived as out-params.

### 6.2 The UPDATE core is path-keyed — §3.3's UPDATE half is CLOSED

`UpdateBySelectionCoreAsync(selection, rewriteFile)` hands the rewriter
`(filePath, sourceBatches, positionsPerBatch)` instead of `(fileOrdinal, …, rowIdsPerBatch)`. Two public
entry points sit on it:

- `UpdateBySelectionAsync(selection, rewriteFile)` — the primitive, identity as (path, position);
- `UpdateBySelectionAsync(RecordBatch updates)` — **the round trip**: read with `ReadAllWithMetadataAsync`,
  change values, hand the batch back. The caller writes NO substitution code and never sees an ordinal or a
  packed rowid. It reuses the rowid-keyed substitution helper VERBATIM, because within one file a position
  is already a unique key.

All three `UpdateByRowIdsAsync` overloads became adapters that re-pack `(ordinal << 40) | position` for
their existing rewriters, so nothing downstream changed — proven by the suite staying green at each step.

Note the third overload (`RecordBatch updates` keyed by a rowid column) stays rowid-keyed BY CONTRACT: its
input batch carries a rowid column, so re-packing is correct there rather than a compromise.

### 6.3 Predicate lowering — and the zero-read DELETE is MEASURED

`MetadataPredicate` is ported as-is (self-contained; only `Expressions` types + `FileRowSelection`) and
wired at the head of `DeleteAsync(predicate)`: a predicate addressing rows only physically lowers to a
selection, and one that MENTIONS `_metadata` but cannot lower is **rejected loudly** rather than handed to
the row mask, which binds data columns only and would mis-evaluate it — deleting the wrong rows silently.

**The zero-data-reads claim is measured, not asserted:** a delegating `CountingFileSystem` counts opens of
data parquet files (log/checkpoint excluded) and the test asserts **0**. Without that instrument the claim
would be a statement about intent.

### 6.4 What remains, precisely

`ReadRowsByRowIdsAsync` is the last rowid-keyed API. It is the interesting one because it both TAKES rowids
and EMITS them (`rowIdsOut`), alongside `sourceRowTrackingOut` — three out-params that the `_metadata` struct
would replace with one column. Converting it finishes the unification; it also touches the Bridge's buffered
UPDATE read-back and CDF delete capture, so it is its own increment.

**Nothing in fabricator consumes any of §6 yet** — this is EW surface only, no ABI or Bridge change, and the
extension's behaviour is unchanged (hermetic/service counts identical). Wiring it is a separate decision from
building it.
