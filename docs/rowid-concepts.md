# Rowid concepts — DuckDB rowid vs transient locator vs stable row-tracking id

Working note, 2026-07-21 (moved from the engineered-wood submodule into `docs/` 2026-07-23; its
one-time companions `DIVERGENCE.md` / `EW-ADAPTER-FEASIBILITY.md` / `RECONCILE.md` were fork-era
migration analysis, superseded by the clast-master re-pin — see [ew-master-migration.md](ew-master-migration.md) —
and deleted).

> **Those three companions are unrecoverable** (checked 2026-07-27): zero commits in either repo's
> history — untracked working files, so "deleted" means gone. But they are NOT where the metadata-surface
> analysis went:
>
> ### 👉 The `_metadata` proposal — EW branch `proto/metadata-dml` (pushed to the fork)
> `PROPOSAL-metadata-dml.md` @ `0db9507`, prototype `72f2d3d` (read + delete-by-selection) + `2780334`
> (update + symbolic lowering). **PARKED 2026-07-21**, not abandoned: Curt's PR#4-parity landing shipped
> the rowid DML fabricator needed, so it stopped being load-bearing. `MetadataDmlTests` 11/11, full
> DeltaLake.Table.Tests 339/339 at base `45cced1`.
>
> It proposes `ReadAllWithMetadataAsync` — a trailing `_metadata` STRUCT of `file_path` (the log
> `add.path`) + `row_index` (the ABSOLUTE physical parquet index, Spark semantics: DV-masked rows excluded
> from output but still counted) — plus `FileRowSelection` and `DeleteAsync(selection)` (**zero data reads**
> on a non-CDF table: DV union + commit, vs O(active files) for a predicate delete), plus lowering of
> `_metadata` predicate conjuncts onto that selection.
>
> **This is the real answer to the naming problem below**, and its own park note says so: it "retires the
> fossil `_metadata.row_id`-as-transient-locator naming at the engine boundary" by giving the locator
> Spark's vocabulary (`file_path`/`row_index`), which frees `_metadata.row_id` for the stable id. Reviving
> it means rebasing over Curt's rowid landing — "real but bounded conflicts" per the note. Prefer this over
> a bare rename: it fixes the names AND adds a capability.
>
> (Searching for this is genuinely hard, which is why this pointer exists: the API name appears in NO
> current worktree, and a `git grep` over `git rev-list --all` MISSES it unless the rev list is uncapped —
> the commits sit outside the first few hundred. `git log --all -S"WithMetadataAsync"` finds it.)
>
> Separately, the closest thing upstream has: `engineered-wood/doc/row-tracking-conformance-brief.md` —
> clast master strips the hidden materialized columns so they never leak to a reader and exposes each
> survivor's resolved id/version via **optional out-params**, where pr-4 (ours) appended a **trailing
> row-id column**. That divergence is why our `__delta_row_id` is user-visible at all — we surface what
> upstream hides — and therefore why it needs a stable, caller-chosen name.
Purpose: pin the THREE distinct concepts that earlier analysis (mine) partially conflated, with
code-grounded facts per codebase, so the adapter/upstream work doesn't mix them up again.

> ## STATUS 2026-07-27 — the three-concept model still holds; every "what clast lacks" answer is OBSOLETE
>
> Re-verified against clast master `e28f70e` (grep, not memory). **The conceptual content below is
> still correct and worth reading. The per-codebase FACTS about clast are not** — upstream absorbed the
> whole area in its PR#4-parity landings and the 2026-07-26/27 commits:
>
> | the note says | today |
> |---|---|
> | clast has **no** transient locator, no `ByRowIds` API | it has the WHOLE surface: `ReadAllWithRowIdsAsync`, `DeleteByRowIdsAsync`, `UpdateByRowIdsAsync`, `DeleteByRowIdsViaVectorsAsync`, `ReadRowsByRowIdsAsync` |
> | clast **hardcodes** the materialized name and ignores `materializedRowIdColumnName` (grep=0) | it READS the key at **8 sites** (`TryGetMaterializedColumnNames`); the hardcoded `RowIdColumnName` const is **vestigial — zero usages** |
> | clast **refuses every** data-changing write on a row-tracking table | the fence is now CONDITIONAL: it refuses only when the table declares row tracking but NOT its materialized names, because without them a copy-on-write rewrite cannot preserve ids |
> | rewrite-preservation is pr-4-only (our fork) | ported upstream, plus CoW change-data capture |
>
> **Consequences: §"Consequences for the adapter / upstream plan" items 1 and 2 are DONE.** No adapter
> exposing a "position-consuming tail" is needed — clast exposes the rowid APIs directly — and the
> config-key upstream item is resolved. Item 3 (naming cleanup: give our transient locator its own name
> and free `_metadata.row_id` for the spec's stable virtual) is the only one left, and is still breaking
> for queries using the current names. Of our patches only **`PlanFiles`** remains absent upstream.
>
> **What upstream does now, and why we still patch one line of it:** at create it GENERATES
> `_row_id_<guid>` / `_row_commit_version_<guid>`, records them in `configuration`, and honors whatever
> is recorded on every read/rewrite — spec-conformant, and the unconditional generation is what keeps the
> fence above from ever tripping on its own tables. Our patch makes a **caller-supplied name win**, because
> `__delta_row_id` is a name we SURFACE to users (concept 3 below), and a fresh GUID per table cannot be
> queried by name. The patch is safe against that fence by construction: it generates whichever name the
> caller did not supply, so "both names present" still holds on every path.

**Where the PLAN lives:** this file pins the concepts and the measured surface.
[rowid-dml-seam.md](rowid-dml-seam.md) is the design + implementation plan for changing them (path-keyed
EW DML, the optional STRUCT rowid, the prototype-revival mechanics, and what must be resolved first).

> **2026-07-27 — the DV-DML boundary is now PATH-KEYED (built, green).** What §"Consequences" item 1 below
> describes as future ("fabricator … decodes it to `(path, positions)`") is now what actually happens, and
> it needed no new EW tail: the two deferred DV-DML entry points
> (`ComputeDeletionVectorActionsAsync`, `RebaseDvDmlActionsAsync`) gained `FileRowSelection` overloads as
> their CORE, the ordinal-keyed forms became thin adapters, and fabricator does the rowid→path decode
> itself through `PlanFiles`. Two facts made this a correctness fix rather than tidying: the ordinal
> round-trip was a **pure detour** (EW's first act was to convert back to paths), and an unresolvable
> ordinal was **silently skipped** — so identifiers captured against the wrong snapshot deleted nothing
> without an error. Details + the remaining rowid-keyed entry points: rowid-dml-seam.md §3.1.
>
> **The three concepts below are unaffected** — this changed how a row is ADDRESSED across one API
> boundary, not what the transient locator or the stable id MEAN, and no user-visible name moved. Item 3
> (the naming cleanup) is still open and still breaking.

## What a USER can actually type — MEASURED 2026-07-27, settles a recurring confusion

Run against a 3-file Delta table (`native_read true`, row tracking on by default), one row per commit.
This is the answer to "which of these names is a virtual column?" — a question that has come up three
times, because `_metadata.row_id` is prominent in the CODE and in this document yet is **not a user
surface at all**.

| you type | works? | in `SELECT *`? | what you get |
|---|---|---|---|
| `rowid` | **YES** | no | the **TRANSIENT LOCATOR** `(fileOrdinal << 40) \| position`. DuckDB's own rowid name; the only user-facing name for it. |
| `__delta_row_id` / `__delta_row_commit_version` | **YES** | no | the **STABLE row-tracking id** / commit version. These are the Delta MATERIALIZED (physical) column names AND our provider virtual-column names — the same string is both. |
| `_metadata.row_id` | **NO** — `Binder Error: Referenced column "_metadata.row_id" not found` | — | nothing. INTERNAL ONLY: the wire name for the transient column between C++ and C# (`FetchRowIdColumns` → `virtual_rowid_columns` → the per-file SQL). The binder never exposes it. |

Measured output, which also shows WHY the two are not interchangeable — stable ids follow COMMIT order,
locator ordinals follow PATH-SORT order (uuid filenames), so they disagree on the same rows:

```
 id | __delta_row_id |          rowid | ordinal | position
  0 |              0 |  2199023255552 |       2 |        0
  1 |              1 |              0 |       0 |        0
  2 |              2 |  1099511627776 |       1 |        0
```

⚠ **Reproduce this on a MULTI-FILE table or it proves nothing.** With one file the ordinal is 0, so
`(0 << 40) | position` == `position` == the stable id for a fresh append — the two coincide exactly, and
any mix-up is invisible. Every discriminating test in this area needs ≥ 2 files.

## The three concepts (keep separate!)

1. **DuckDB's `rowid`** — a *binding concept*, not a representation. A catalog entry advertises rowid
   support via `GetRowIdColumns()` / the scan's `get_row_id_columns` hook (fabricator:
   `fabricator_table_entry.cpp:658/760/797`); DuckDB's DELETE/UPDATE plans then identify target rows by
   that column. What it MAPS TO is the provider's choice:
   - SQL Server provider → **physical** key columns (PK / unique index / IDENTITY, `RowIdSql`).
   - Delta provider → the **virtual transient locator** (below).
   DuckDB itself also has per-source transient ordinals as a general device (`WITH ORDINALITY`;
   `read_parquet(..., file_row_number => true)` — which is exactly what fabricator's native path uses).

2. **The transient (file, position) locator** — fabricator's Delta DML mechanism:
   `(fileOrdinal << 40) | file_row_number`, minted AT SCAN TIME, valid for ONE snapshot (scan→mutate in
   one statement). It is a *physical address* (a deletion-vector target), NOT an identity. Produced:
   - native path: in fabricator's per-file SQL (`DeltaNativeReader.cs:366`) from DuckDB `file_row_number`;
   - codec path: by our EW fork's `ReadAllWithRowIdsAsync` (trailing column).
   **Misnomer:** both call the column `_metadata.row_id` — the name Delta/Spark reserve for the STABLE id.
   (Spark's transient analog is `_metadata.row_index`.) Rename candidate when aligning with clast.

3. **The stable row-tracking id** — the durable identity (`delta.enableRowTracking`). ONE id, TWO forms:
   - **physical**: a materialized parquet column, name declared by the table config
     `delta.rowTracking.materializedRowIdColumnName` — our tables stamp **`__delta_row_id`**
     (+ `__delta_row_commit_version`); written NULLABLE-per-row on rewrites.
   - **virtual**: the reader-facing column that maps onto it — spec/Spark name **`_metadata.row_id`** —
     resolving per row to `COALESCE(materialized, baseRowId + row_position)`.
   Fabricator exposes it under the PHYSICAL names (`__delta_row_id`/`__delta_row_commit_version`,
   metadata kind 12) because it already spent `_metadata.row_id` on concept 2. Never used to target DML.

## Name matrix (code-verified)

The `clast EW` column below was captured 2026-07-21 and is **superseded** — see the corrected column.
(The "our EW fork" column is fork-era history; the fork lineage is retired, our changes are a small
patch set on clast master now.)

| concept | Delta spec / Spark | fabricator (DuckDB surface) | our EW fork (retired) | clast EW @ 2026-07-21 | **clast EW @ e28f70e (verified 2026-07-27)** |
|---|---|---|---|---|---|
| DuckDB rowid binding | — | virtual `_metadata.row_id` (Delta) / physical PK cols (SQL) | — | — | — |
| transient locator | `_metadata.row_index` (per-file position) | `_metadata.row_id` ⚠ misnomer | trailing `_metadata.row_id` col (ReadAllWithRowIdsAsync) ⚠ | none | **present** — `ReadAllWithRowIdsAsync` emits it as a trailing `VirtualRowIdColumn` (= `_metadata.row_id`, same misnomer), and the full `*ByRowIds*` DML surface consumes it |
| stable id, physical col | name from `materializedRowIdColumnName` config | queryable virtuals `__delta_row_id`/`__delta_row_commit_version` | writes `__delta_row_id`, config-stamped | hardcoded `_metadata.row_id`; config key not read | **config-driven**: generates `_row_id_<guid>` at create, records it, reads it back via `TryGetMaterializedColumnNames` at 8 sites. `RowIdColumnName` const survives but is VESTIGIAL (0 usages) |
| stable id, virtual | `_metadata.row_id` | (occupied by the locator) | — (fabricator surfaces it) | const defined, no read-side exposure | still no reader-facing virtual; the const now names the TRANSIENT column instead |

## Who USES what (the "what depends on it" answer, corrected)
- **Transient locator = MUST for catalog DELETE/UPDATE** (+ the S3 external-table identity DML routed
  through them): DuckDB gives the provider matched rows by rowid, never a reliable exact WHERE
  (pushdown is a superset) → exact-row targeting requires it. Also the late-materialization TopN
  fast path + count-via-rowid (optimizations only).
- **Stable id = interop/identity**: Spark/`table_changes` correlation, keyless dedup, the stable-id
  fast path (file/row-group skipping via baseRowId ranges), update-stable ids for PolyBase/shortcut
  tables. It is NEVER the DML key — resolving id→(file,position) would only add work inside one statement.

## clast: does it have a transient AND a physical? (the question — ANSWER BELOW IS SUPERSEDED, see STATUS)

> **Answer today: YES to both.** Kept verbatim because the reasoning about *what would have to be true*
> is still the right way to interrogate a Delta writer, and because the last bullet's watch-point is the
> thing that turned out to matter — it is now resolved upstream, which is why our remaining patch there
> is one line about NAME CHOICE rather than about conformance.

- **Transient: NO.** No position/ordinal-emitting read anywhere; its predicate DELETE/UPDATE compute
  positions INTERNALLY (mask → matched indices → DV / rewrite) and never surface them. The position
  *machinery* exists one layer down — that's the adapter's target — but no locator API.
- **Physical: machinery present, writes FENCED.** `RowTrackingWriter` (fresh-id append, strip) + HWM
  emission exist on clast master and `WriteCoreAsync` has the `rowTrackingEnabled → AddRowIdColumn`
  branch — but `RejectRowTrackingWrite` throws for EVERY data-changing op (shared write precondition
  + `CompactAsync`) on a `delta.enableRowTracking=true` table: *"Reading such a table is supported; a
  spec-conformant writer is planned."* So the write-side code is currently unreachable.
- **Preservation across ops: NOT on clast master.** Rewrite-preservation (CoW/compaction/MoR baking the
  ORIGINAL id) is pr-4-only (our fork); clast's `row-tracking-conformance-brief.md` describes porting it
  (their deferred #5/#8). Until then clast avoids corruption by refusing the write entirely.
- ⚠ **Interop watch-point:** clast HARDCODES the materialized column name `_metadata.row_id` and ignores
  the spec's `materializedRowIdColumnName` config — our tables declare `__delta_row_id` via that config.
  If clast's writer is ever un-fenced as-is, it would name the column differently than our tables declare
  and mis-strip/mis-read config-named tables. → an upstream item: clast must honor the config key
  (read AND write) before/with the preservation port.

## The COALESCE fallbacks — what they're for (the forgotten part)
`COALESCE(materialized __delta_row_id, baseRowId + file_row_number)` (`DeltaNativeReader.RowTrackingExpr`)
covers THREE distinct absent/NULL cases:
1. **Whole file has no materialized column — every fresh APPEND.** By design (Spark parity) appends never
   materialize; readers derive `baseRowId + position`. Detected per file by the footer probe → the
   expression degenerates to the pure derived form.
2. **Per-row NULL inside a REWRITTEN file.** A CoW/buffered rewrite of a source file that PREDATED row
   tracking cannot resolve the original id → bakes NULL for exactly those rows (the columns are written
   NULLABLE, `RowTrackingWriter.cs:88-90`) → the reader derives a FRESH id for exactly that row.
3. (fast-path cousin) **pre-enablement adds have NULL `baseRowId`** → under a stable-id value constraint
   the whole file is skipped outright (no derivable ids).
The commit-version twin works the same: `COALESCE(materialized version, defaultRowCommitVersion)`.

## Why fabricator's `_metadata.row_id` carries the spec's stable-id name — it's a FOSSIL
The first Delta DELETE design (2026-06-29) really DID use the spec's stable row-tracking id under
`_metadata.row_id` (+ deletion vectors). Fabric's OneLake converter / Spark could not read those commits →
the design switched to plain Delta + the TRANSIENT `(ordinal<<40)|position` rowid — **but the column name
was kept**. When row tracking returned (2026-07-13) as the virtual-columns feature, the stable id had to
take the PHYSICAL names (`__delta_row_id`/`__delta_row_commit_version`) because `_metadata.row_id` was
already occupied by the locator. Proof points: the transient decode (`rid >> RowIdPositionBits`) is
structurally incompatible with a stable id (no ordinal, post-compaction ids exceed old ranges);
`verify_delta_row_tracking_virtual.test` queries only `__delta_*` (42+11 hits, zero `_metadata.row_id`);
the Spark-side stable-id validation (sparkprobe) queries `_metadata.row_id` on the SPARK surface, where
the name does mean the stable id. (`test/verify_delta_catalog_delete.test`'s header described that
abandoned design — FIXED 2026-07-27. It was wrong on a third count too: it claimed the rows are removed
via deletion vectors, while the suite pins `deletion_vectors false` precisely to exercise copy-on-write.)

## Corrections to earlier analysis in this session (for the record)
- I described clast's `_metadata.row_id` as "the stable **virtual** name like Spark". Half-right: on clast
  it is (also) the **hardcoded MATERIALIZED column name**, and clast ignores the config key that is
  supposed to define it. The virtual/physical distinction is real; clast collapses the names.
- I said "clast = read-only row tracking, write refused" — right, but incomplete: the write-side
  materialization code EXISTS on clast (append fresh-ids, HWM) and is fenced by `RejectRowTrackingWrite`,
  so un-fencing = the preservation port + the config-key fix, not greenfield.
- Any earlier phrasing implying fabricator's `_metadata.row_id` is the spec's stable id: it is NOT — it is
  the transient locator wearing the spec name (concept 2), while the stable id rides `__delta_row_id`.

## Consequences for the adapter / upstream plan
1. Adapter unchanged in substance: fabricator keeps minting the transient locator (DuckDB-side on the
   native path), decodes it to `(path, positions)`, and needs clast to expose the position-consuming tail
   of `ComputeDelete/UpdateActionsAsync` (`DeleteByPositionsAsync` / update analog).
2. NEW upstream item (from the watch-point): clast honors `materializedRowIdColumnName` — prerequisite
   for reading/writing OUR tables' `__delta_row_id` correctly once its writer is un-fenced.
3. Naming cleanup when aligning: fabricator's transient locator gets its own name (spec-friendly:
   something `row_index`-based, e.g. `_fabricator.row_locator`), freeing `_metadata.row_id` for the
   stable virtual per spec (today's `__delta_row_id`/`__delta_row_commit_version` virtuals become
   `_metadata.row_id`/`_metadata.row_commit_version`). Breaking for queries using the current names.
