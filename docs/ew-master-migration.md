# Migration: re-pin fabricator onto clast-project/engineered-wood master

Branch: `migrate/ew-clast-master` (off `main`). Status: **COMPLETE (2026-07-22) — pin moved to
`fabricator-patches` @ `7fecc2b` (pushed), 49/49 suites green, live OneLake/Spark validated.
Remaining: merge this branch to `main` (user's call) + the Curt upstream bundle.**

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

**FINAL SWEEP (2026-07-21): 48/49 delta files green, variant the ONLY red** (variant CLOSED in the
follow-up session — see "Variant transport port" below; the sweep is now 49/49). Full fork counts:
transactions 941, row_tracking_virtual 299, column_mapping 251, s3 161, native_write 147,
clustered_optimize 138, alter 116, nested_alter 100, copy_format 109, partition_overwrite 90,
changes 73, row_level 74 (with the abort pins), update 63, verify_delta 60, dv_default 58,
late_materialization 57, txn_version 51, + every other delta suite. Cross-provider spot-check green:
**verify_mssql_s3_polybase 252/252** (the SQL-Server↔S3-Delta full circle — protocol-1.0 CoW DML now
WITH CDF capture, identity slices, external-table DDL), with_options 68, SQL function suites
(scalar 26 / custom 89 / table 33 / procs 24 / global 63).

**`fabricator-patches` IS PUSHED to the fork** (cmettler/engineered-wood; the variant-port commits of
the follow-up session extend it — push on the next explicit "push"). The VARIANT TRANSPORT port is
**DONE** (see the section below), and the **LIVE OneLake/Spark spot-checks PASSED (2026-07-22)** —
see "Live spot-checks" below. Remaining before the pin move: the pin/gitlink move + CLAUDE.md
rewrite (EW workflow + the superseded fork-era notes) — the user's call.
Upstream-discussion bundle for Curt = the fabricator-patches branch + the design questions recorded
here (booleans-vs-configuration on CreateAsync; a separate entry point for preAssignedSchema; the
buffered remap-across-rewrite follow-up; whether the marker-keyed variant transport + the three
relabeling/width codec fixes belong upstream — the narrow-int corruption certainly does).

## Variant transport port — DONE (2026-07-21 follow-up session); the sweep is 49/49

**verify_delta_catalog_variant: GREEN at 144** (fork was 133; +11 = NEW positive coverage — see the
capability gain below). Ported the fork's `VariantTransport` onto master's canonical-`VariantType` flow
as EW `fabricator-patches` commits — the branch is now e48f449 + 7 (`ee7ee02` parquet narrow-int fix /
`a622297` pass-through source-field fixes / `7fecc2b` the variant transport; LOCAL, push on explicit
authorization) — adapted to master's architecture (the fork's INTERNAL representation
was the blob with conversion at the parquet codec; master's internal representation stays the canonical
`VariantArray` with conversion at the HOST boundary):

- **EW `SchemaConverter`**: `VariantTransportExtensionName = "fabricator.variant"` +
  `IsVariantTransportField`; `FromArrowField` maps a marker-tagged BINARY → `variant` (top-level +
  struct-nested via the recursive field conversion; list/map inner markers rejected — the fork's
  degradation guard); `FilterArrowMetadata` also drops `ARROW:extension:*` (transport hints never
  persist into the Delta schema — fork parity).
- **EW `VariantTransport`** (ported, over master's `VariantArray` + `Apache.Arrow.Operations` 23.0.0
  shredding toolkit — new Table.csproj package ref): `ToVariantArrays` (write ingress, marker-keyed —
  a no-op for canonical input, so master's own hosts/tests are unaffected; shreds uniform columns via
  `ShredSchemaInferer`/`VariantShredder`, SQL-null as storage validity) + `ToTransportBlobs` (read
  egress: `VariantArray` incl. shredded reassembly via `GetLogicalVariantValue`, bare struct, and
  seam-delivered blob passthrough with marker re-tagging).
- **EW `DeltaTableOptions.VariantTransportBlob`** (default false = canonical): selects the read
  direction; wired at the `ProcessFileBatchesAsync` coercion point (replaces `VariantColumnCoercion.
  Coerce` when set). The 4 write sites' CODEC branches call `ToVariantArrays` then re-apply the
  `EmitVariantLogicalType` policy; the `IDataFileWriter` SEAM branches pass blobs through untouched
  (the host writer produced the marker — it encodes the layout itself via the C++ ArrowTypeExtension).
- **Bridge**: `DeltaWriter.Options()` sets `VariantTransportBlob = true`; `VariantMarker.
  ToTransportSchema` (recursive `VariantType` → tagged BINARY) wraps the three EW-advertised schema
  returns (`GetSchema`/`GetSchemaAndRowTracking`/`GetSchemaAt`) + the buffered-ALTER
  `PendingArrowSchema`; the `EnsureVariantWritable` gates (nested placement, CDF) unchanged.
- **THREE more genuine master bugs found + fixed en route** (same silent-relabeling/width class as the
  compaction fixes): (1) `ValueWidener.WidenBatch` relabeled an UNCONVERTED column with the target type
  when `WidenArray` passed an unsupported pair through (the blob-vs-VariantType pair leaked a canonical
  extension label onto blob data → the seam writer's schema export failed registration); (2)
  `SchemaEvolution.BackfillMissingColumns` relabeled PRESENT pass-through columns with the expected
  field — now keeps the SOURCE field (only rebuilt/backfilled columns take the expected label); (3)
  **`ColumnChunkWriter` narrow-int corruption (pre-existing, variant-independent!)**: 1-/2-byte Arrow
  arrays (Int8/UInt8/Int16/UInt16 — Int32 parquet physical) were reinterpreted AT THE PHYSICAL WIDTH by
  the value extraction (dictionary + PLAIN + V2 encoders), packing four int8 values per int read —
  SILENT corruption whenever Arrow's 64-byte buffer padding hid the overrun (probed: a plain Int8
  column wrote "successfully" then failed to read back). Fixed at the one chokepoint
  (`WidenNarrowIntArray` in `WriteColumnCore`, beside the FLBA byte-reversal precedent); pinned by a
  new `NarrowIntColumns_RoundTrip` theory (dictionary + plain paths, min/max/null values). This very
  likely affected the FORK too (same encoder lineage) — TINYINT/SMALLINT Delta columns via the EW codec
  were simply never test-covered. Upstream-candidate material, all three.
- **CAPABILITY GAIN over the fork: pure-codec variant REWRITES work.** The fork gated codec-path
  UPDATE/CoW/OPTIMIZE on variant tables ("not supported yet" backstops); master's variant-native codec
  + the transport at its boundary handle them — the suite's gate pin flipped to positive coverage
  (codec MoR UPDATE incl. SET of the variant value + codec OPTIMIZE, all read back exactly).
- **New EW test `VariantTransportTests` (4)**: marker→variant schema mapping (+ no metadata pollution),
  transport-written → canonical readback (byte-exact `VariantArray`), canonical-written → transport
  readback (byte-exact blobs, SQL-null as null row), uniform-column shred + reassembly round-trip
  (value halves byte-exact; reassembly may re-encode metadata header flag bits — asserted accordingly).

**Gates (all green):** verify_delta_catalog_variant 144; EW DeltaLake.Table.Tests 416 + DeltaLake.Tests
210; extension regression at full counts — transactions 941, row_tracking_virtual 299, column_mapping
251, native_write 147, clustered_optimize 138, alter 116, nested_alter 100, native_read 88, changes 73,
struct_filter 67, update 63, temporal 63, verify_delta 60 (global fabricator_delta_scan, blob-form reads),
dv_default 58, late_materialization 57, decimal 47, optimize 40, native_write_streaming 29,
compaction_rowtracking 24, materialize_rowtracking 12. (EW Parquet.Tests: the parquet-testing corpus
submodule is NOT initialized in this checkout — its corpus-dependent tests fail on missing files
independent of any code change; A/B'd with the narrow-int fix stashed: 114 failed on BOTH sides —
zero new failures, +2 new passing NarrowInt pins with the fix.)

## Live spot-checks — FULL PASS (2026-07-22, workspace Test / LH, Fabric Spark via Livy)

Both directions, on the EW-master engine (fabricator-patches @ 7fecc2b), validation tables left on
LH for inspection (`sparkprobe verifymigration` re-runs the Spark half; sparkprobe's secret path was
fixed to the fabricator-extension repo root):

- **`lake.dbo.fabricator_mig`** (pure defaults: DV + row tracking + name mapping; native seams):
  our CTAS + INSERT + composed merge-on-read UPDATE + DV DELETE over OneLake → our
  `__delta_row_id`/`__delta_row_commit_version` readback shows id 2 with **row_id 1 preserved,
  version 3** and id 5 gone; **Spark reads the identical cut** (`_metadata.row_id` 0,1,2,3,5 /
  ver 3 on the updated row; DV-deleted row invisible); **Spark wrote back** (UPDATE id 3 — Spark
  itself preserved row_id 2 honoring our materialized declaration; INSERT id 7 → fresh row_id 8,
  correctly consuming the HWM past our post-image allocations) and **our provider re-read Spark's
  writes exactly** — both engines agree on every row id, byte-for-byte.
- **`lake.dbo.fabricator_migvar`** (VARIANT through the PURE EW CODEC — the new VariantTransport
  over OneLake, no native flags): create + insert (object/NULL/array/string) + **codec MoR UPDATE
  of the variant value** → our readback exact (incl. dot access + SQL-NULL semantics); **Spark
  decodes the codec-written files exactly** — `to_json` all rows, `variant_get` on the object AND
  on the MoR-updated value, `WHERE v IS NULL` matches only the SQL-NULL row.

**Open (1 workstream, design known):**
1. **Row-level rebase across rewrites** — master's buffered flush aborts (clean
   `DeltaConflictException`, correctness preserved) where the fork's v1/v2 rebased (`RebaseDvDmlActions`
   row-disjoint re-union exists; the `RemapRowsAcrossRewriteAsync` stable-id remap across a concurrent
   OPTIMIZE/CoW does not). verify_delta_row_level_concurrency: 30/31, failing §(buffered DML through a
   concurrent compaction). Options: pin the abort (capability regression vs the fork's beyond-Databricks
   arc) or port the remap as the next upstream proposal (pairs naturally with the parked `_metadata` RFC).

## Branch / working-tree state
- fabricator: `migrate/ew-clast-master` (this branch). **The pin IS moved** (2026-07-22): gitlink
  `7fecc2b` = fork `fabricator-patches` (pushed), `.gitmodules` `branch = fabricator-patches`.
  NOTE `main` still pins the fork lineage (`99e2c3a`) — checking out `main` needs
  `git submodule update` to flip the EW tree back; merge this branch to `main` to retire that.
- EW submodule working tree: branch **`fabricator-patches`** @ `7fecc2b` (= `upstream/master`
  e48f449 + 7), in sync with the pin.
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
