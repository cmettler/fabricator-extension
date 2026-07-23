# Rowid concepts — DuckDB rowid vs transient locator vs stable row-tracking id

Working note, 2026-07-21 (moved from the engineered-wood submodule into `docs/` 2026-07-23; its
one-time companions `DIVERGENCE.md` / `EW-ADAPTER-FEASIBILITY.md` / `RECONCILE.md` were fork-era
migration analysis, superseded by the clast-master re-pin — see [ew-master-migration.md](ew-master-migration.md) —
and deleted).
Purpose: pin the THREE distinct concepts that earlier analysis (mine) partially conflated, with
code-grounded facts per codebase, so the adapter/upstream work doesn't mix them up again.

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

| concept | Delta spec / Spark | fabricator (DuckDB surface) | our EW fork | clast EW |
|---|---|---|---|---|
| DuckDB rowid binding | — | virtual `_metadata.row_id` (Delta) / physical PK cols (SQL) | — | — |
| transient locator | `_metadata.row_index` (per-file position) | `_metadata.row_id` ⚠ misnomer | trailing `_metadata.row_id` col (ReadAllWithRowIdsAsync) ⚠ | **none** (no position-emitting reader, no ByRowIds API) |
| stable id, physical col | name from `materializedRowIdColumnName` config | queryable virtuals `__delta_row_id`/`__delta_row_commit_version` | writes `__delta_row_id` (`RowTrackingWriter.RowIdColumn`), config-stamped | **hardcoded `_metadata.row_id`** (`RowTrackingConfig.RowIdColumnName`); the config key is NOT read (grep=0) ⚠ |
| stable id, virtual | `_metadata.row_id` | (occupied by the locator) | — (fabricator surfaces it) | `VirtualRowIdColumn` const defined = `_metadata.row_id`; no read-side exposure found |

## Who USES what (the "what depends on it" answer, corrected)
- **Transient locator = MUST for catalog DELETE/UPDATE** (+ the S3 external-table identity DML routed
  through them): DuckDB gives the provider matched rows by rowid, never a reliable exact WHERE
  (pushdown is a superset) → exact-row targeting requires it. Also the late-materialization TopN
  fast path + count-via-rowid (optimizations only).
- **Stable id = interop/identity**: Spark/`table_changes` correlation, keyless dedup, the stable-id
  fast path (file/row-group skipping via baseRowId ranges), update-stable ids for PolyBase/shortcut
  tables. It is NEVER the DML key — resolving id→(file,position) would only add work inside one statement.

## clast: does it have a transient AND a physical? (the question)
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
the name does mean the stable id. ⚠ STALE DOC: `test/verify_delta_catalog_delete.test`'s header still
describes the abandoned first design ("a stable _metadata.row_id surfaced as the DuckDB rowid") — fix it.

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
