# Migration: re-pin fabricator onto clast-project/engineered-wood master

Branch: `migrate/ew-clast-master` (off `main`). Status: **scoped, not started** (2026-07-21).
Authoritative upstream guide: `engineered-wood@e48f449:doc/pr4-to-master-migration.md` — written by Curt
FOR fabricator, with a 7-step checklist. This doc = the fabricator-side state + scoping results.

## Why now
Curt landed PR#4 parity on clast/master in 23 commits (`45cced1..e48f449`): row-tracking milestones 1–3
(+rebase; the write fence is now narrow — only rewrites of tables WITHOUT declared materialized column
names refuse, and `materializedRowIdColumnName` is honored → OUR tables pass), the complete
buffered-transaction seam (M-A..M-D3 incl. `SetSchemaAsync`), rowid DML with our fork's names +
`(ordinal<<40)|pos` encoding + the `_metadata.row_id` column, NEW host-join `UpdateByRowIdsAsync`
overloads (one source-compatible with our callback; one takes a RecordBatch of rowid+SET columns),
nested-field ALTER, CDF spec-conformance. The fork/adapter strategy is dead; direct retarget is on.

## Branch / working-tree state
- fabricator: `migrate/ew-clast-master` (this branch). `main` clean; the committed submodule pin is
  STILL `99e2c3a` (our fork) — **do not move the pin (or .gitmodules) until the full sweep is green**.
- EW submodule working tree: local branch `clast-master` = `upstream/master` (`e48f449`).
  ⚠ While on this branch the tree ≠ the pin — `main` builds only after `git -C engineered-wood checkout master`.
- Parked EW branches (pushed to cmettler fork): `proto/metadata-dml` (`0db9507` — the _metadata RFC +
  prototype, revisit post-migration), `fix/vacuum-dv-orphans` (`1ecf28d` — fork-only; master's vacuum is
  already better). Local-only: `reconcile/clast` (superseded, deletable).
- fabricator `feat/delta-engine-contract` (`9b05f82`): parked — the IDeltaEngine seam is no longer needed
  for this migration (name parity ⇒ direct retarget), but `docs/delta-engine-contract.md`'s per-op mapping
  table is the verified inventory of all 37 EW methods the Bridge calls — useful as this migration's checklist.

## Dry-compile result (Bridge vs e48f449, net8.0): 7 errors, 3 root causes — tiny
Everything else compiles as-is: rowid DML incl. `DeleteByRowIdsViaVectorsAsync`/`UpdateByRowIdsAsync`
(callback overload), `ReadAllWithRowIdsAsync`/`ReadRowsByRowIdsAsync`, `WriteDataFilesAsync`,
`CommitDataFilesAsync` with ALL named params, the rebase/compute methods, identity seams, nested ALTER,
`SetSchemaAsync`, `SetClusteringColumnsAsync`, `WriteChangeDataFileAsync`.

1. **`IDataFileRewriter` gone** (deliberate — guide §1): `NativeParquetDataFileRewriter.cs` (:31),
   `DeltaReader.cs` (:1961/:1968 — the options wiring), `DeltaGlobalTableFunction.cs` (:386 — options).
   → Checklist step 1: DELETE `NativeParquetDataFileRewriter` + the `DataFileRewriter` option wiring.
   The CoW DELETE/UPDATE read+transform moves into EW's rowid DML (the join stays in DuckDB); consider
   the new `UpdateByRowIdsAsync(RecordBatch updates)` overload for `ExecuteUpdate` (join result direct,
   zero substitution code).
2. **`RowTrackingRewrite` gone** (:67/:114 — the rewriter's record param): master preserves row-tracking
   ids through rewrites ITSELF (milestone 2) — our id-projection SQL is obsolete. Falls out with #1.
3. **`IDataFileWriter.WriteAsync` drift**: master takes `IAsyncEnumerable<RecordBatch>` (was our list).
   `NativeParquetDataFileWriter.cs` (:24) — small adapt (`.ToAsyncEnumerable()` or reshape RunCopy feed).

## Expected BEHAVIORAL deltas (compile-parity ≠ behavior parity) — what the sweep will surface
- **MoR UPDATE is gone** (`UpdateViaVectorsAsync` dropped; master's rowid UPDATE is CoW-only). Fabricator's
  autocommit UPDATE on DV tables changes shape: full file rewrite instead of DV-delete + small post-image
  append. Suites that PIN the MoR shape (dv_default 58 "append is small", update 63, row_tracking_virtual
  299 MoR sections) will fail on SHAPE, not correctness. Options per guide §2: accept CoW (ids preserved by
  milestone 2), compose MoR from `DeleteByRowIdsViaVectorsAsync` + post-image via
  `CommitDataFilesAsync(extraActions)` (the buffered path already composes this), or propose the additive
  `UpdateByRowIdsViaVectorsAsync` upstream (Curt invites it).
- **Variant marker**: our C++ (`fabricator_variant.cpp`) + EW-fork keyed the Arrow extension name
  `fabricator.variant`; master uses its own variant extension registry (`VariantExtensionDefinition`) +
  `DeltaTableOptions.EmitVariantLogicalType`. Verify the extension NAME crossing the C ABI matches what the
  C++ registry expects — likely the first variant-suite failure to chase.
- **Clustered OPTIMIZE / ZCube**: `CommitDataFilesAsync(dataChange:, clusteringProvider:)` + `Tags` compiled,
  but the guide doesn't mention the clustering arc — verify verify_delta_clustered_optimize 138 end-to-end.
- **Row-tracking OCC caveat** (DeltaTransaction doc): a row-tracking table's staged work "aborts rather
  than rebase" on contention (baseRowId recompute not implemented) — the transactions racer sections
  (§6e etc.) may see aborts where the fork rebased. Fabricator default tables ARE row-tracking.
- **On-disk DV legacy fallback**: master REMOVED the legacy `_delta_log/`-located DV read fallback (their
  slice 2 note: "library never shipped"); any OLD local test tables written by our fork's pre-fix writer
  would be unreadable — irrelevant for fresh-provisioned suites.

## Run order
1. Fix the 3 compile causes (checklist 1–2 + the writer signature). Bridge builds.
2. `pwsh scripts/publish-managed.ps1`; C++ rebuild NOT needed unless ABI touched (it isn't — C#-only).
3. Full `verify_delta_*` sweep (docker rig + FABRICATOR_DELTA_WRITE_DIR; see CLAUDE.md test-env block).
   Triage failures against the behavioral-deltas list above; decide MoR (compose vs accept vs propose).
4. Live spot-checks: OneLake CTAS/DML + a Spark read (row-tracking preservation now via master's impl).
5. Move the pin: commit the submodule gitlink at e48f449 (or a cmettler-fork mirror of it — decide URL
   posture: .gitmodules currently points at cmettler; either mirror master to the fork or switch URL to
   clast-project), update CLAUDE.md (EW workflow section + status), retire stale notes.
6. Post-migration: revisit `proto/metadata-dml` (the parked _metadata RFC) + the additive
   `UpdateByRowIdsViaVectorsAsync` proposal.
