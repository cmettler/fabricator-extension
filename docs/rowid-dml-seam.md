# Design: the DML row-identity seam, and how it evolves

**Status: DESIGN, nothing built (2026-07-27).** Target: a future fabricator `main` branch pinned to
duckdb `main`, once the `v1.5-variegata` line is stable. Companion to
[rowid-concepts.md](rowid-concepts.md), which pins the CONCEPTS and the measured user surface; this doc
is the PLAN.

Everything below is marked **VERIFIED** (checked in source today, with the location), **PROPOSED**, or
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

## 3. The design (PROPOSED)

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

| API | keyed by | note |
|---|---|---|
| `ComputeDeletionVectorActionsAsync(positionsByOrdinal, …, resolveAgainst)` | ordinal | the buffered DV DELETE flush |
| `RebaseDvDmlActionsAsync(… newPositionsByOrdinal …)` | ordinal | Layer 3 (B) row-level remap across a concurrent rewrite |
| `ReadRowsByRowIdsAsync(rowIds, …)` | rowid (encodes ordinal) | buffered UPDATE read-back, CDF delete capture |
| `CommitDataFilesAsync(… deletedPositionsByFileIndex …)` | **index into not-yet-committed written files** | ⚠ a DIFFERENT index space (our `0x780000+` pending files). Those files are not in the snapshot, so path-keying does not reach it — it stays index-keyed. |

---

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

**Base drift: it sits on `45cced1`, which is 76 commits behind `fabricator-patches`.**

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
- Whether a selection DELETE should escape EW's `RejectRowTrackingWrite` (it moves no rows, so arguably
  yes) — an open question in the proposal itself.
- The proposal's other open points: struct vs flat metadata columns; `numRecords`-less foreign tables;
  set-valued literals for large position sets.

---

## 8. Cost of the `main` branch itself

Tracking duckdb `main` means absorbing continuous upstream API churn — the 1.5 `ExtensionLoader` break is
the precedent — and doubling CI minutes. The branch-naming convention already reserves `main` for this
(see CLAUDE.md "BRANCH NAMING"); add it as a nightly allowed-to-fail branch rather than a gate.
