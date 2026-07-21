# Migration: re-pin fabricator onto clast-project/engineered-wood master

Branch: `migrate/ew-clast-master` (off `main`). Status: **compile stage DONE (2026-07-21) — sweep running.**

## Compile-stage outcome (supersedes the dry-compile inventory below)

The dry compile had been SHALLOW — the real build surfaced 8 causes, 3 of which needed **additive EW
patches** (the anticipated "fabricator patches on top of clast master" model). EW branch
**`fabricator-patches`** (= `e48f449` + 2 commits, local): `0bfd020` DeltaFilePruner → public;
`6ddfcc1` CreateAsync/OpenOrCreateAsync gain `configuration` (merged into commit-0, `delta.enable*`
keys enable + declare their features — incl. NEW inCommitTimestamp/changeDataFeed derivation) +
`preAssignedSchema` (buffered-CTAS pre-assigned mapping) + OpenOrCreate gains `columnMappingMode`;
and WriteDataFilesAsync gains `materializedRowIds` (buffered-UPDATE stable-id bake, attached under the
DECLARED materialized column name, nullable — master's convention). EW Table.Tests 411/411 +
DeltaLake.Tests 210/210 green on the branch. Bridge-side (fabricator `d17db9a`): rewriter removal per
guide §1 (+ the UPDATE SQL-join substitution block deleted — EW's rewriteFile callback is the path;
the `UpdateByRowIdsAsync(RecordBatch updates)` host-join overload is a follow-up), UPDATE loses
`rowLevelRetry` (master CoW UPDATE aborts on conflict; DV DELETE keeps it), writer seam →
IAsyncEnumerable (first-batch peek + stream), **`VariantMarker`** (Bridge-owned `fabricator.variant`
detector — master models variant as canonical `VariantType`, so the blob⇄struct TRANSPORT at the EW
boundary is an OPEN follow-up; expect verify_delta_catalog_variant red), WriteChangeDataFileAsync →
single CdcFile (buffered CDC on PARTITIONED tables re-guarded to autocommit — the fork's in-EW split
is gone), NoWarn EWDELTA0001/EWPARQUET0002 (fabricator IS the in-tree impl of the Experimental seams).
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

## Runtime-triage outcome (2026-07-21, sweep rounds 1–5) — near-complete

EW `fabricator-patches` grew to **e48f449 + 4 commits** (`0bfd020` pruner public / `6ddfcc1` create-config
+ preAssignedSchema + materializedRowIds / `2007c39` rowIdsOut + derived-id fallback + CoW-CDF capture +
plural `WriteChangeDataFilesAsync` + DV-aware CDF inference / `7981487` schema-evolved-compaction fixes:
name-matched `WidenBatch` [positional pairing RELABELED one column's data as another's — silent-corruption
class], per-batch reconcile in `CompactionExecutor`, rename-only array reuse in `ColumnMappingRecursive`).
EW Table.Tests 412/412 + DeltaLake.Tests 210/210. Bridge rounds (fabricator `bb10e5b` + `5c0e7e3` + this):
decimal widening via master's `DecimalOutput=Decimal128` read option centralized in
`DeltaWriter.ReadOptions()` (narrow Decimal32/64 corrupt the Arrow C crossing — the 10.4<>1.5 class, hit
~20 suites); UPDATE moved to the host-join `UpdateByRowIdsAsync(RecordBatch)`; **composed merge-on-read
UPDATE** from master's primitives (DV-delete compute + post-image `WriteDataFilesAsync` with original ids
+ CDF pre/post capture + one fused `CommitDataFilesAsync`) — restores the fork's MoR shape, feed
exactness, and id preservation; read-backs re-keyed on `rowIdsOut` (master yields NO trailing rowid
column — the old blind last-column drop was silently removing a USER column from CDC capture); two pins
updated to the master append shape (appends materialize nothing — readers derive baseRowId+position).

**FINAL SWEEP (2026-07-21): 48/49 delta files green, variant the ONLY red.** Full fork counts:
transactions 941, row_tracking_virtual 299, column_mapping 251, s3 161, native_write 147,
clustered_optimize 138, alter 116, nested_alter 100, copy_format 109, partition_overwrite 90,
changes 73, row_level 74 (with the abort pins), update 63, verify_delta 60, dv_default 58,
late_materialization 57, txn_version 51, + every other delta suite. Cross-provider spot-check green:
**verify_mssql_s3_polybase 252/252** (the SQL-Server↔S3-Delta full circle — protocol-1.0 CoW DML now
WITH CDF capture, identity slices, external-table DDL), with_options 68, SQL function suites
(scalar 26 / custom 89 / table 33 / procs 24 / global 63).

**`fabricator-patches` IS PUSHED to the fork** (cmettler/engineered-wood, branch `fabricator-patches`
@ `7981487`) — the pin-move target is fetchable. Remaining before the pin move: the VARIANT TRANSPORT
port (next session: port the fork's `VariantTransport` — `git show 99e2c3a:src/EngineeredWood.DeltaLake.Table/VariantTransport.cs`,
316 lines — onto master's VariantType/extension-registry flow as another fabricator-patches commit:
marker-tagged blob ⇄ VariantArray at WriteCoreAsync/ProcessFileBatchesAsync, `FromArrowField`
marker→"variant"; gate = verify_delta_catalog_variant 133 + master's own VariantInteropTests);
live OneLake/Spark spot-checks; then the pin/gitlink move + CLAUDE.md rewrite (EW workflow + the
superseded fork-era notes). Upstream-discussion bundle for Curt = the fabricator-patches branch +
the design questions recorded here (booleans-vs-configuration on CreateAsync; a separate entry point
for preAssignedSchema; the buffered remap-across-rewrite follow-up).

**Open (2 workstreams, designs known):**
1. **Variant transport** — master models variant as canonical `VariantType` (`arrow.parquet.variant`,
   struct storage); the C++ boundary needs the LEAF-blob `fabricator.variant` form (canonical struct
   crashes DuckDB's `ArrowAppender::FinalizeChild`; name collides with built-in handlers).
   verify_delta_catalog_variant: 69/70 then SIGSEGV at the ABI crossing. Fix = port the fork's
   `VariantTransport` semantics keyed on the marker (blob⇄`VariantArray` at EW's write/read boundary,
   `FromArrowField` marker→"variant"), implemented over master's own VariantArray utilities — an EW
   `fabricator-patches` commit, or discuss with Curt whether the marker-aware transport belongs upstream.
2. **Row-level rebase across rewrites** — master's buffered flush aborts (clean
   `DeltaConflictException`, correctness preserved) where the fork's v1/v2 rebased (`RebaseDvDmlActions`
   row-disjoint re-union exists; the `RemapRowsAcrossRewriteAsync` stable-id remap across a concurrent
   OPTIMIZE/CoW does not). verify_delta_row_level_concurrency: 30/31, failing §(buffered DML through a
   concurrent compaction). Options: pin the abort (capability regression vs the fork's beyond-Databricks
   arc) or port the remap as the next upstream proposal (pairs naturally with the parked `_metadata` RFC).

## Branch / working-tree state
- fabricator: `migrate/ew-clast-master` (this branch). `main` clean; the committed submodule pin is
  STILL `99e2c3a` (our fork) — **do not move the pin (or .gitmodules) until the full sweep is green**.
- EW submodule working tree: local branch **`fabricator-patches`** (= `upstream/master` e48f449 + 4).
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
