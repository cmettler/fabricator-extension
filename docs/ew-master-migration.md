# Migration: re-pin fabricator onto clast-project/engineered-wood master

Branch: `migrate/ew-clast-master` (off `main`). Status: **COMPLETE (2026-07-22) — pin moved to
`fabricator-patches` @ `7fecc2b` (pushed), 49/49 suites green, live OneLake/Spark validated.
Remaining: merge this branch to `main` (user's call) + the Curt upstream bundle.**

> **This is a SNAPSHOT of that migration, not the current patch set.** Two upstream bumps on 2026-07-26
> shrank the patches from 8 to 4 by diff (four were reimplemented upstream), and the `0bfd020`
> DeltaFilePruner-public patch named below was RETIRED and replaced by a real `DeltaTable.PlanFiles`
> API. CLAUDE.md's "EW BUMP" + "`PlanFiles`" subsections are the live record; read this one for the
> original migration's reasoning only.

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
IAsyncEnumerable (first-batch peek + stream), **`VariantMarker`** (Bridge-owned `ew.variant_transport`
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

- **EW `SchemaConverter`**: `VariantTransportExtensionName = "ew.variant_transport"` +
  `IsVariantTransportField`; `FromArrowField` maps a marker-tagged BINARY → `variant` (top-level +
  struct-nested via the recursive field conversion; list/map inner markers rejected — the fork's
  degradation guard); `FilterArrowMetadata` also drops `ARROW:extension:*` (transport hints never
  persist into the Delta schema — fork parity).
- **EW `VariantTransport`** (ported, over master's `VariantArray`): `ToVariantArrays` (write ingress,
  marker-keyed — a no-op for canonical input, so master's own hosts/tests are unaffected) +
  `ToTransportBlobs` (read egress: `VariantArray` incl. shredded input, bare struct, and
  seam-delivered blob passthrough with marker re-tagging).
  **SHREDDING ITSELF IS NO LONGER HERE (2026-07-28 — Curt's gap 8, "a general-purpose passenger worth
  separating"):** both directions moved to the parquet layer's public
  `EngineeredWood.Parquet.Data.VariantShredding` (`TryShred` beside the pre-existing `Reassemble`), so
  **`Apache.Arrow.Operations` is off `DeltaLake.Table` entirely** — and at zero net cost, because
  `EngineeredWood.Parquet` already referenced it for shredded-read reassembly. `VariantTransport` now
  decodes/encodes blobs only (`Apache.Arrow.Scalars` rides along with `Apache.Arrow` core) and delegates
  the layout decision. Full record incl. the namespace-capture trap it exposed: CLAUDE.md
  "THE SHREDDING SPLIT".
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

**Open workstreams: NONE — the last one closed 2026-07-23.**
1. **Row-level rebase across rewrites — DONE (2026-07-23, EW-only, on `fabricator-patches`).** The
   regression was never missing machinery, only a missing ROUTE: clast master already ships the full
   stable-id remap as `RemapRowLevelDeletesAsync` (its "Layer 3 (B)", private, Spark-interop-validated)
   serving master's own autocommit/`DeltaTransaction` surfaces — the buffered surface's
   `RebaseDvDmlActionsAsync` just threw on a vanished path ("the explicit buffered remap-across-rewrite
   is a follow-up"). The fix routes that case through the existing remap: vanished touched paths are
   collected as `DeleteDvEdit`s (row tracking required — without it the clean rewrite conflict remains),
   their staged DV pairs are dropped from the rebase, and the remap's new-file DV pairs are appended
   (they keep the new files' own `baseRowId` — no HWM impact). No Bridge/ABI change — the flush's
   rebase→check→commit loop is untouched (`CheckLogicalRebaseAsync`'s delete/delete check validates the
   REMAPPED removes against the same snapshot). The fork's bespoke `RemapRowsAcrossRewriteAsync` stays
   retired; buffered and autocommit now share ONE remap. EW `BufferedTransactionTests` +3
   (compose-through-compaction, same-row row-level conflict, no-row-tracking conflict; Table.Tests 421);
   `verify_delta_row_level_concurrency` back at the fork-era **70** (§5 buffered DELETE + §8 buffered
   UPDATE compose through a concurrent OPTIMIZE, §9 same-row-through-rewrite = the precise "row-level
   conflict"). This closes PR #4's "Known follow-up".

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
  for this migration (name parity ⇒ direct retarget), but that branch's `delta-engine-contract.md` per-op mapping
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
  `ew.variant_transport`; master uses its own variant extension registry (`VariantExtensionDefinition`) +
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

---

## Staying in sync with an ACTIVE upstream — the working model (2026-07-28)

Both sides move now: Curt fixes general-purpose bugs and reimplements our patches; we add
Fabricator-shaped seams. Three bumps in, each easier than the last, the model that keeps the burden
low for both is **one branch we keep, offers we generate.**

### Measured divergence (2026-07-28, after merging clast master `b1de9d8`)

`git diff --shortstat upstream/master..HEAD` → **15 files, +3725 / −141**, 18 non-merge commits ahead,
**0 behind**. The shape is what matters:

- **~3,100 lines are NEW FILES + tests** (`FileRowSelection`, `MetadataPredicate`, `VariantTransport`,
  seven test files). These have produced **zero conflicts across four bumps** — a file upstream does not
  have cannot conflict. (`PlannedFile` left this list at the fourth bump: we adopted upstream's
  `PlanFiles` and deleted ours.)
- Only **five shared source files** carry our edits: `DeltaTable.cs` (the bulk, and the ONLY conflicted
  file in each of the four bumps), `DeltaTransaction.cs` (+171, the `Stage*` additions),
  `SchemaConverter.cs` (+50), `DeltaTableOptions.cs` (+15), and `DeltaFilePruner.cs` (+4, doc only).
- **143 deletions total** — we are almost purely additive, which is what makes the patch set offerable.

**Re-measured 2026-07-28 after the shredding split AND the `9669796` bump:** **20 files, +4680 / −143**, 26
non-merge commits ahead, **0 behind** — the insertion count went DOWN across a bump that added upstream code,
because conforming `_metadata`'s locator to his flat shape deleted more of ours than it added (no struct type,
no struct walking, no duplicated identity resolution). That is the shape of a patch set converging rather than
accreting. The split ADDED a shared file outside the Delta layer —
`EngineeredWood.Parquet/Parquet/Data/VariantShredding.cs` (+173) — and that is the point rather than a
cost: it is the file Curt asked for (gap 8), it removed `Apache.Arrow.Operations` from
`DeltaLake.Table`, and it is the most independently-offerable thing in the set, since it touches no
Delta concept at all.

### The two branch roles

| branch | purpose | lifecycle |
|---|---|---|
| `fabricator-patches` | what the submodule pin tracks; our integration branch | **permanent.** Merge `upstream/master` periodically. **NEVER rebase or force-push** — published release tags pin shas on it (`v0.0.1-duckdb1.5.5` → EW `8aa7cfb`), and orphaning one makes a downloadable release unbuildable from source. |
| `offer/<feature>` | ONE upstream PR each: cut fresh off `upstream/master`, one curated commit | **disposable. Regenerate, never maintain.** |

**Why offers are generated, not maintained** — this is the load-bearing decision. Curt lands our patches
INDEPENDENTLY (three of eight by the second bump, two more by the third), so a long-lived offer branch
decays the moment he does. PR #4 went **48 commits behind** exactly that way, until its diff read as
deleting 17,150 lines of his own newer work. A branch generated on demand from the current
`fabricator-patches` is current by construction and costs a couple of minutes to recreate.

### Offer queue as of 2026-07-28 — five open, three to go

All five are **draft**, each cut fresh off `upstream/master` per generate-never-maintain, and each
**built and tested against PRISTINE upstream** (not merely against our tree) before opening:

| PR | offer | validated |
|---|---|---|
| [#6](https://github.com/clast-project/engineered-wood/pull/6) — **MERGED 2026-07-31** (`6745de0`) | variant shredding, write direction (his gap 8) | Parquet builds; +7 tests × {net8.0, net472}; all 17 `Variant*` green |
| [#7](https://github.com/clast-project/engineered-wood/pull/7) | `ReadAllWithMetadataAsync` — the `_metadata` locator | Table.Tests 643 × {net8.0, net472} |
| [#8](https://github.com/clast-project/engineered-wood/pull/8) | `StartTransaction(snapshot)` | 640 × 2 TFMs; **mutant** (ignore the arg) fails 2/4 |
| [#9](https://github.com/clast-project/engineered-wood/pull/9) | `StageAppTransaction` (idempotent-producer CAS) | 642 × 2 TFMs; **mutant** (drop the per-attempt check) fails exactly 1/6 |
| [#10](https://github.com/clast-project/engineered-wood/pull/10) | `StageDataFilesAsync` + `SetOperation` | 642 × 2 TFMs; **mutant** (ignore the identity bypass) fails 1/6 |
| [#12](https://github.com/clast-project/engineered-wood/pull/12) | **the isolation bound** on row-level reconciliation | 637 × 2 TFMs; **mutant** (ungate it) fails with *"No exception was thrown"* — upstream lets the second delete COMMIT under `Serializable` |
| [#13](https://github.com/clast-project/engineered-wood/pull/13) | `StageReadPredicate` / `StageWholeTableRead` | 640 × 2 TFMs; **two** mutants, each killed by its own test (1:1, neither covering for the other) |
| [#14](https://github.com/clast-project/engineered-wood/pull/14) | **`FileRowSelection`** — a DML key that fails loudly | 640 × 2 TFMs, and **all 636 of upstream's own tests pass through the rowid adapter**; **mutant** (loud error → silent skip) fails both path-keyed tests |

**Mutation-test every load-bearing claim ON THE OFFER BRANCH, not from our history.** It has paid for
itself three times: it produced the sharpest line in each PR body (*which* test carries the weight), and
on #10 a mutant **SURVIVED** — dropping `StatsWithLooseBounds` failed nothing, so the spec's
`tightBounds=false` on a DV-bearing add was unpinned in OUR suite too (fixed both places, EW `7639414`).

**Two integration findings that our tree HID** — the argument for splicing against pristine master rather
than assuming portability: (1) upstream builds `latestSnapshot` only when `rowLevel || rowTrackingEnabled`,
so #9's CAS had nothing to read on a plain append (surfaced as an NRE in the twin-producer test, not by
review; `hasAppTransactions` now joins that condition and the PR flags it as his call); (2) upstream's
`CommitDataFilesAsync` **already carries** the `identityValuesPreGenerated` bypass, so #10 argues literal
parity rather than new policy — checked before proposing, and it reframed the whole PR.

**~~Remaining three~~ — the first is DONE (both halves, #12 and #13, split on purpose so an API review cannot
hold up a correctness fix). Two remain, and the first needs SCOPING before it is started:**

1. ~~**Read declaration + the ISOLATION GATE**~~ — **SHIPPED as #12 (the gate) + #13 (the API).** The split
   was right: `ReadSet` already had `Predicates` AND `WholeTable`, so #13 is two public methods + a flag +
   one field, changing no semantics; #12 is ~2 lines changing them. The `#9 trick` worked for BOTH test sets
   (call `StartTransaction()` BEFORE the concurrent commit — the handle's own snapshot is then already the
   pre-concurrent version), so neither depends on #8.
2. **`FileRowSelection` — MEASURED 2026-07-28 as TOO BIG for one offer; scope it as below.** The whole
   surface is **33 references / 8 public-or-internal methods in `DeltaTable.cs` / a 491-line test file**, and
   three of those methods (`UpdateBySelectionViaVectorsAsync` ×2 + `UpdateBySelectionAsync`) belong to item 3
   rather than here. **Scope the offer to the minimum that carries the ARGUMENT:**
   - `FileRowSelection.cs` (the type, 38 lines — a `path → positions` dictionary, no Arrow shape);
   - **ONE** conversion: `DeleteBySelectionViaVectorsAsync` becomes the path-keyed core and upstream's
     `DeleteByRowIdsViaVectorsAsync` becomes a thin adapter over `SelectionFromRowIds`. That is the path the
     decisive test exercises;
   - `ResolveSelection` — the shared ordinal→path mapping, the LOUD error on a path that is not active, and
     the note that it materialises one `HashSet` per file (the copy-on-write loop probes
     `targets.Contains(abs)` once per ROW, so passing the caller's `IReadOnlyCollection` through compiles
     fine and silently binds those probes to LINQ's O(n) `Contains`);
   - **three** tests: the wrong-row test, the silent-loss test, and unknown-path-throws.
   **Lead with `OrdinalKeyed_AfterAConcurrentRemoveRenumbersTheSet_DeletesTheWRONGRow`** — silent WRONG DATA
   is the argument, and neither a range check nor `TransientRowAddress` can catch it. Pitch: *"the address
   type is right; the DML boundary needs a key that fails loudly."* ⚠ **Every fixture needs THREE files** —
   with one file the ordinal is always 0, so a single-file fixture cannot fail these tests. And the intended
   ordinal in the wrong-row test must be **1, not 2**: removing ordinal 0 shifts old ordinal 2 INTO slot 1,
   so aiming at 2 goes out of range while aiming at 1 hits the wrong file.
3. **`UpdateBySelectionViaVectorsAsync` — BUILT AND HELD (2026-07-29): `offer/mor-update` @ `ae17594`, one
   commit on top of `offer/file-row-selection`, NOT PUSHED.** Waiting on #14's verdict is about the KEY, not
   about having the work ready — so it is built, gated and parked; pushing it is one command whenever he
   rules. Gates: `MergeOnReadUpdateTests` (6) green on **net10.0, net8.0 AND net472**, Table.Tests **646** on
   all three (upstream's own + #14's 6 + these 6), build 0 warnings, and **mutation-tested** — passing
   `materializedRowIds: null` kills exactly `PreservesTheStableRowId` and no other test, so the capability
   claim is load-bearing and isolated. Diff vs master: **+907/−25** across 4 files, of which #14 is
   +250/−19; this commit alone is **+555/−6**.
   **THREE findings from actually porting it, none of them in the scope note:**
   - **The tests could NOT be ported.** Ours read through `ReadMetaAsync` → `ReadAllWithMetadataAsync`, which
     is offer #7 and not on this branch. Rebuilt on upstream's OWN surfaces instead —
     `ReadAllWithRowIdsAsync` + `PlanFiles` for the locator (`_ew_row_address` → ordinal → path) and
     `ReadAllWithRowTrackingAsync` for identity — which is better for the offer anyway: it demonstrates the
     feature against his API, not ours.
   - **It needed ONE MORE re-key than the note said**, and this is the load-bearing discovery: our tree's
     `ComputeDeletionVectorActionsAsync` is selection-keyed, but #14 deliberately left it ordinal-keyed
     (#14 converts only the DELETE). A path-keyed UPDATE that converted its selection BACK to ordinals to
     compute its mask would defeat its own argument, so the offer adds a `FileRowSelection` overload. Kept
     strictly additive: upstream's body became a shared `ComputeDvActionsForFilesAsync` (everything after a
     file is identified is independent of how it was named), the ordinal-keyed public signature and its
     documented skip-out-of-range leniency are untouched, `DeltaTransaction` is untouched, and upstream's
     **11 ordinal call sites** cover the old form unchanged.
   - **One public API DROPPED from the offer:** our `WriteChangeDataFilesAsync` (public, partition-aware) was
     verified BEHAVIOURALLY IDENTICAL to upstream's internal `WriteChangeDataFilesForAsync` — independent
     convergence, like the `configuration` parameter — so the CDF capture calls HIS. His shape is better here
     too: it takes the snapshot explicitly, which this path already holds.
   **Historical scope note (what was expected before building):** ~184 lines
   (two overloads + core) and it TAKES a `FileRowSelection`, so its branch must stack on #14 rather than cut
   from master. That is the real reason to wait rather than a context one: **#14 asks him to accept that key,
   and building a new OPERATION on a key he has not judged means rebuilding it if he prefers a different
   one.** When it goes: cut off `offer/file-row-selection`, say plainly in the body that it contains #14's
   commit, and lead with the fact that it is the one genuine CAPABILITY gain — the one genuine CAPABILITY gain, and his own landing notes record
   merge-on-read UPDATE as **unowned** upstream after the PR rewrite dropped it. Offer it last but flag that
   status, since it is the only item filling a hole he has named.

### Why per-feature and not one draft

He triages by **who each gap serves** (`doc/upstream-landing-notes.md` upstream) and lands the
general-purpose ones immediately. One omnibus PR forces all-or-nothing review; separate ones let him
take what he wants now and leave the Fabricator-shaped seams — which is how he already behaves. Keep
PR #4 open regardless: upstream's own notes reference it.

### Two habits that keep OUR side cheap

1. **Stay additive.** 141 deletions is the number to protect. A deletion in our diff turns an offer
   into "I removed your API", the least upstreamable shape there is, and it becomes a permanent conflict
   point against an active upstream.
2. **Prefer a NEW FILE over editing `DeltaTable.cs`.** Every new file we added has cost zero merge
   conflicts; `DeltaTable.cs` has been the single conflicted file in all three bumps. When a feature can
   live beside it rather than inside it, that is worth real weight.

### Bump checklist (what the three bumps converged on)

1. `git fetch upstream` → read the commit SUBJECTS **and the doc hunks** — an absorbed semantic may
   announce itself only in documentation (the caller-supplied materialized-name semantic did exactly that).
2. `git merge-tree --write-tree --merge-base=$(git merge-base HEAD upstream/master) HEAD upstream/master`
   to predict conflicts before touching the tree.
3. Merge; resolve **by class** (take upstream where it reimplemented us — its version has been better
   every time; keep ours where it is genuinely absent upstream; combine where both improved one line).
4. Watch for **auto-merged duplicates**: our code that sat OUTSIDE a conflict hunk can end up saying the
   same thing twice beside upstream's. Markers show textual DISAGREEMENT, not redundancy. Two kinds, and
   the second is the dangerous one:
   - a duplicate **declaration** → `CS0128`/`CS0111`, the compiler catches it (bump 3: `dvEnabled`);
   - a duplicate **STATEMENT** → nothing catches it (bump 4: upstream moved the materialized-row-id
     re-attach later in `WriteDataFilesAsync`, our old placement stayed, and the id column was appended
     TWICE; `AddRowIdColumn` appends unconditionally, so every materialized file would carry two
     identically-named `__delta_row_id` columns. **EW Table.Tests was 656/656 green with it present** —
     a reader resolving by name gets a correct value from one of the two copies).
5. **After taking a method wholesale from upstream, diff it against upstream's copy and require
   byte-identity.** That is what found the duplicate above. Any surviving difference must be one you can
   name — ours are `RebaseDvDmlActionsAsync` (path-keyed) and `ReadRowsByRowIdsAsync` (our derived-id
   fallback, which upstream measured redundant in ITS tree and we keep as defensive).
6. **On any upstream COLUMN-NAME rename, grep the Bridge for string-keyed column lookups.** The compiler
   cannot see them, and the C ABI hides the mismatch: `ScanCodec` declares its stream schema with OUR
   names while the batches carry EW's, and `arrow_ingest` reads the DECLARED schema then works
   positionally — so DuckDB is unaffected and the hermetic tier passes. Bump 4's `_metadata.row_id` →
   `_ew_row_address` rename surfaced in exactly ONE place, `ExternalTableRouting`'s
   `b.Schema.GetFieldIndex` (service tier only). Fix at the source so declared and actual schemas agree,
   and reference a constant rather than a literal.
7. Verify both directions: our patches still present, upstream's new work arrived, superseded code gone.
8. Gate: EW suites × all three TFMs, then fabricator hermetic + service. **Unchanged fabricator counts
   are the signal** that the bump is behaviour-neutral for what we pin.
9. Verify every historically-pinned EW sha is still an ancestor — especially any a PUBLISHED release depends on.


---

## PENDING BUMP — upstream landed ALL FIVE #15 slices in one day (measured 2026-07-31, NOT started)

**Read this before touching the pin.** This is the largest bump of the five so far, and it lands squarely on
the file our patch set is most concentrated in. Every number below is measured (`git merge-tree`, `git diff
--stat`, public-name diffs, call-site greps with a zero control), not estimated.

**Where we are:** pin `5272681` on `fabricator-patches`; merge-base with `upstream/master` is `7ab5d4f`.
Upstream is **8 commits ahead**; we carry **39 commits** on top (`git cherry` counts 31 by patch-id).

| upstream commit | slice / subject |
|---|---|
| `e314af5` #22 | slice 4 — `Stage*` / `Require*` / `Declare*` split (a precondition and an effect shared a prefix) |
| `6f3033e` #21 | slice 5 — a transaction validating against the latest version validates nothing |
| `c66a55e` #20 | slice 3 — an identity table's appends could not be staged |
| `98407ea` #19 | slice 1 — eight read methods collapse into one |
| `db45c82` #18 | slice 2 — four places could address the wrong file (the stale-ordinal sites) |
| `8054040` #17 | shredded-variant output measured against Spark, DuckDB and pyarrow |
| `f0fea65` #16 | shredding becomes a caller decision, one batch at a time |
| `6745de0` #6 | **OUR variant-shredding PR, merged** (authored "Christoph") |

Curt's decisions on #15, both in our favour: **open question 1 resolved** — `ReadAllAsync` /
`ReadAtVersionAsync` STAY as thin wrappers (verified present in master, so our 4 call sites on them are
safe); **open question 4 (the transaction-level exemption opt-in) DEFERRED** by him until slices 1–5 are in
— so that thread is parked by upstream, not by us, and the measurement stays recorded in the issue body.
Build order he published: **2, 1, then 5 and 3, with 4 whenever**. On our porting offer: *"I'll come back to
your porting offer once there is a branch worth porting against."*

### The cost, measured

`git merge-tree --write-tree` (no working-tree change) reports **4 conflicts**:
`DeltaTable.cs`, `DeltaTransaction.cs`, `Parquet/Data/VariantShredding.cs` (**modify/delete**), and
`test/…/VariantShreddingTests.cs` (add/add). Churn on the same files — upstream **+1325** lines in
`DeltaTable.cs` against our **+1165** in it; `DeltaTransaction.cs` +216 vs +188.

**15 public `DeltaTable` methods removed, 6 added, and our Bridge calls 12 of the removed ones across 24
call sites** (counted with a zero-control grep):

| removed upstream | our call sites | successor |
|---|---|---|
| `DeleteByRowIdsAsync` 3, `DeleteByRowIdsViaVectorsAsync` 2, `DeleteBySelectionAsync` 1, `DeleteBySelectionViaVectorsAsync` 1 | 7 | `DeleteRowsAsync(RowSelection, RowDeleteMode, rowLevelRetry, …)` |
| `UpdateByRowIdsAsync` 3, `UpdateBySelectionAsync` 1, `UpdateBySelectionViaVectorsAsync` 3 | 7 | `UpdateRowsAsync(RowSelection, rewriteFile, …)` |
| `ReadAllWithRowIdsAsync` 3, `ReadAllWithRowTrackingAsync` 1, `ReadAtVersionWithRowIdsAsync` 1, `ReadRowsByRowIdsAsync` 3 | 8 | `ReadAsync(DeltaReadOptions)` + `ReadRowsAsync` + `GetReadSchema(DeltaReadOptions)` |
| `WriteChangeDataFilesAsync` | 2 | **no direct successor — investigate** |

`DeltaTransaction` likewise: `SetOperation`, `StageAppTransaction`, `StageReadPredicate`,
`StageWholeTableRead` are gone; `RequireAppTransaction`, `DeclareRead`, `DeclareWholeTableRead` replace
them. The rename encodes a real contract difference, so it is not purely mechanical: `Stage*` = an EFFECT
(rebased and retried), `Require*` = a PRECONDITION (re-checked before EVERY attempt; violation throws
`InvalidOperationException` and is NOT retried), `Declare*` = a DECLARATION (widens the read set, subject
to isolation policy). Our buffered-DML code must land each call in the right one of the three.

**Unaffected, and worth knowing before pricing the work:** `PlanFiles` SURVIVED as public API upstream, and
`PlannedFile` took a **documentation-only** change — so our 8 `PlanFiles` call sites need nothing. The
removals above are mostly overload consolidation rather than semantic change, which is what makes 24 call
sites tractable.

### Two traps specific to THIS bump

1. **`git cherry` is useless here for finding redundant patches.** It reports 0 of our 31 as upstream — yet
   the shredding work demonstrably IS upstream (#6 is ours, merged), because #16 then reworked it and the
   patch-ids no longer match. Judge redundancy by READING, not by tooling. Concretely: the shredding patch
   we still carry is the modify/delete conflict, and it should be **dropped, not merged** — that removes a
   conflict rather than resolving one.
2. **Do not entangle this with unrelated work.** The Fabric-API commits sitting unpushed when this was
   measured have nothing to do with EW; a bump this size wants its own branch and its own sweep.

### Suggested order when it is taken

Drop the superseded shredding patch → merge `upstream/master` into `fabricator-patches` → migrate the 24
Bridge call sites (Delete/Update first: they share `RowSelection`, which slice 2 introduced and slice 5
consumes) → resolve `WriteChangeDataFilesAsync` → full `verify_delta_*` sweep. Then the standing rules from
this doc apply unchanged: **diff any method taken wholesale against upstream and demand byte-identity** (the
auto-merged duplicate-statement trap), **only the net472 leg proves a change offerable**, and **fast-forward
the pin, never force-push** (release tags pin EW shas).

## Appendix — the CLAUDE.md journal (moved verbatim 2026-07-29)

> Moved VERBATIM out of `CLAUDE.md` on 2026-07-29 (conservative split — see the commit message).
> Append-only as-built history; the live summary + pointers stay in `CLAUDE.md`.
> Paths/links inside are REPO-ROOT-relative (the text was written for `CLAUDE.md`).

### Sync-over-async cleanup — CONVENTION established, remainder is incremental

The documented shape (thin sync ABI wrapper that blocks ONCE on a private `async` core using
`ConfigureAwait(false)` throughout) is now demonstrated + verified on the leaf `DeltaReader.GetActiveFileUris`
(exemplar with an inline doc comment; native_read suite green — confirms the AsyncLocal ambients flow across the
pool-thread hops). **`DeltaReader.cs` is now FULLY converted (2026-07-15, C#-only)**: every leaf ABI-facing method
(the read-only log probes `GetSchema`/`GetSchemaAndRowTracking`/`IsDeletionVectorsEnabled`/`IsColumnMapped`/
`GetTxnDmlProfile`/`GetAppTransactionVersion`/`GetOrderedActiveBaseRowIds`/`ComputeSchemaChange`/`ResolveVersionAsOf`/
`GetSchemaAt`; the DML/maintenance ops `DeleteByRowIds`/`DeleteByRowIdsViaVectors`/`UpdateByRowIds`/`Optimize`/
`Vacuum`/`AddColumn`/`AddField`/`RenameField`/`DropField`/`RenameColumn`/`DropColumn`; the list/read-back paths
`ListNativeScanFiles`/`ListScanFilesJson`/`ReadRowsByRowIds`) is now a thin sync wrapper (`=> XxxAsync(...).GetAwaiter().GetResult()`)
over a private `XxxAsync` core using `ConfigureAwait(false)` at every await — retiring the per-await
`.GetAwaiter().GetResult()`/`.AsTask().GetAwaiter().GetResult()` form. (The `Stream*`/`GetChanges`/`GetSnapshots`
paths already had async cores; their remaining single blocking points are deliberate schema-peeks, not per-await.)
Verified: the FULL delta sweep green (24 suites, ~2500 assertions — write/delete/update/optimize/alter/native_read/
native_write/native_write_streaming/transactions 941/time_travel/row_tracking_virtual 299/txn_version/late_mat/
column_mapping/partition/partition_overwrite/dv/dv_default/variant 133/nested_alter/struct_filter/dynamic_filter/
compaction_rowtracking/copy_format/decimal/temporal/schemas/constraints/rename). **`DeltaWriter` (in
`DeltaGlobalTableFunction.cs`) + the self-contained `DeltaCatalog` flush helpers are ALSO converted (2026-07-15):**
DeltaWriter `Write`/`Create`/`Materialize`/`MergeSchema` + `TryWriteStreaming` (the `out rowsWritten` case → its async
core returns a `(long? Result, long RowsWritten)` tuple, the sync wrapper unpacks the out — the compiler enforces
every return is a tuple, so a missed conversion is a build error not a silent bug; `TryStreamCreateFiles` was already
sync — RunCopy is synchronous). **`DeltaCatalog.cs` is now FULLY converted too**: the flush helpers
(`FlushDeferredFiles`/`WriteCdcFiles`/`TryEagerWriteBatches`), the orchestrators (`FlushDmlTransaction` — the
~200-line buffered-DML commit/rebase hot path — and `FlushCreateTransaction`), and the stream-consuming paths
(`ReadFilterValues`; `ExecuteDelete`/`ExecuteUpdate` — their rowid/update-stream drains extracted into
`CollectRowIds`/`ParseUpdateStream` async-core helpers so the txn/rewrite branching stays sync; the S3-rename
copy+delete fallback → `MoveFilesByCopy`). All open EW directly (or block once on a DeltaReader/DeltaWriter leaf
wrapper). **The `DeltaGlobalTableFunction` reader filter-loop (`ReadValues`) is converted too, and `OneLakeForwardFs`
was found ALREADY conformant** — every one of its methods has a SINGLE async call blocked once at the top (the
documented block-once shape; `Glob` is already a proper wrapper→core), which IS the convention (the per-await
anti-pattern only arises in MULTI-await methods; a single-await method blocking once is correct as-is). **⇒ the Delta
bridge sync-over-async cleanup is COMPLETE** (DeltaReader / DeltaWriter / DeltaCatalog / DeltaGlobalTableFunction all
converted; OneLakeForwardFs conformant) — verified the FULL delta sweep green after each increment (commits `38a9e7f`
DeltaReader+EW-variant-fix / `382eda2` DeltaCatalog helpers / `a2fdb98` DeltaWriter / `97ca5f8` DeltaCatalog
orchestrators+stream-loops / + this final `ReadValues`). Any remaining `.GetAwaiter().GetResult()` in the Delta bridge
is now a single-blocking-point sync wrapper, not a per-await site. The ambient-loss landmine stays disarmed
(AsyncLocal, `0533eb7`). Adopt the sync-wrapper→async-core shape for NEW code now.
**Whole-codebase scan (2026-07-15) — the anti-pattern was DELTA-ONLY; nothing else needs converting:** a sweep of
every bridge assembly found the deeply-async-blocked-at-every-await anti-pattern existed ONLY in the Delta/EW bridge
(now done). The rest are NOT the anti-pattern and should be LEFT ALONE (converting is zero-value churn — no deadlock
risk since the hostfxr CLR has no `SynchronizationContext`, and the sync backend work dominates the thread anyway):
**`SqlServerBackend`** uses SYNC `Microsoft.Data.SqlClient` (38 sync `Execute*`/`WriteToServer`, 0 async) — its only
async touchpoints are Arrow C-stream boundary reads (`ReadNextRecordBatchAsync`), already block-once-per-batch in
otherwise-synchronous methods (e.g. `ExecuteScalar`'s loop body is a pure-sync `fn.Invoke`); **DAX/`Fabricator.AnalysisServices`**
is sync ADOMD (`ExecuteReader`, 0 async); **`Fabricator.DeltaRs`** already routes its delta-dotnet async ops through a
`Run()`/`Run<T>()` block-once helper (15 uses); **`FabricLakehouse`** uses proper wrapper→core + single-await block-once;
**`BulkSession`/`Bootstrap`** are single-await ABI-marshaling handlers (block-once). So the RAW `.GetAwaiter().GetResult()`
grep counts across the bridges are now ALL either single-blocking-point sync wrappers or already-conformant
Arrow-boundary reads — do NOT treat a nonzero count as remaining work. The sync-over-async initiative is DONE.


#### EW BUMP 2026-07-26 — clast master `babdb00` merged (clean); two silent-corruption fixes inherited, one NEW fabricator-side guard required

`git merge-tree` predicted clean and the merge was clean: 10 upstream commits, overlap with our patches in
exactly 3 files (`DeltaTable.cs`, `SchemaConverter.cs`, `ColumnChunkWriter.cs`) and no conflicts — upstream's
ColumnChunkWriter edit only threads the new `options.MaxLiteralGroups`, disjoint from our narrow-int widening.
**Our patch set is still all 8 commits — none absorbed upstream yet**, including the narrow-int
write-corruption fix (still an upstream candidate, alongside the `PlanFiles` proposal).

**Two data-correctness fixes we INHERIT — both were silently wrong for us before:**
- `525bf94` `LiteralValue.CompareTo` compared strings with `string.CompareOrdinal` = UTF-16 code UNITS, while
  Parquet/Delta/Iceberg all define string min/max over UTF-8 BYTES. They disagree wherever a supplementary
  character (≥U+10000, surrogate-encoded) meets U+E000..U+FFFF. That comparator is what our pushed filters
  reach through `DeltaFilePruner`, so we could **skip a file containing matching rows** — silent missing data.
  New `StringOrdering.cs`.
- `937eac1` truncated string stats now cut on CODE POINT boundaries (previously could split a surrogate pair,
  so we *wrote* invalid stats).

**Timestamp precision hardening (`6cb60fa`/`adb920d`/`b8a3aa3`/`babdb00`) — a real behaviour change.** EW now
REFUSES nanosecond and second Arrow timestamps at write instead of mislabelling them (a second-unit column
read back a MILLION times too small; ns lost its sub-µs digits). Guards sit in `SchemaConverter`, two
`DeltaTable` chokepoints, the parquet writer and partition-value formatting. **Reachable from fabricator:**
`SqlArrowMapping` maps `datetime2(7)`/`time(7)` → Arrow NANOSECOND, so a SQL→Delta CTAS of such a column now
errors. Our `WriteDataFilesAsync` seam is covered for free (upstream put a guard in that method, which is
THEIRS — our patch only added a `materializedRowIds` parameter).

**But the merge does NOT close the equivalent gap on our side, so we added `DeltaWriter.EnsureTimestampUnitsWritable`.**
Under `native_write` the data files are produced by DuckDB's COPY and EW only ever sees the finished files
(`CommitDataFilesAsync`), never the Arrow batches — so its guard *structurally cannot* fire there. DuckDB's
parquet writer DOES support NANOS, so without our check we would emit a NANOS-annotated file inside a table
whose Delta schema declares micros: readable by DuckDB, wrong for every other reader. The check recurses into
struct/list/map, and is called from `TryWriteStreaming` (the native chokepoint) and `DeltaCatalog.BulkInsert`
(so every INSERT/CTAS/COPY is checked once, early). Like EW we REFUSE rather than round — which rounding is
right is the caller's call. Coverage added to `verify_granular_types` (24, was 18): the ns write is refused
AND the documented `CAST(dt2_7 AS TIMESTAMP)` works (`.123456` — DuckDB truncates). That suite also gained the
`require parquet` its new native_write section needs (finding-3 class: undeclared, it passes on a developer box
and fails on a bare runner).

**Parquet read perf + a follow-up** (`082aa63` fixed-length-list fast path, `68761d8` opt-in batching of
bit-packed literal runs → new `ParquetReadOptions.MaxLiteralGroups`, `370493e` fixes batched NESTED reads,
`8ac71a9` restores the net472 test build): all opt-in, no TFM/csproj changes, so our build and behaviour are
untouched — available if we ever want them.

**Gates:** EW Table.Tests 444 (was 421) / DeltaLake 210 / Expressions 139 green. EW Parquet.Tests shows 115
failures, ALL `DirectoryNotFoundException` on `parquet-testing/data` — the nested corpus we deliberately do
not init (115 failures, 115 mentions of that path); zero regressions. Plus the full fabricator hermetic +
service sweeps.

#### EW BUMP 2026-07-26 (second, same day) — clast master `8ef4a7c`: FOUR OF OUR PATCHES ARE NOW UPSTREAM; pin `4588594`

Curt reimplemented four of the eight patches we were carrying, so **the patch set shrinks 8 → 4 by diff**
(the commits stay in history — a merge does not remove them; the honest measure is
`git diff upstream/master..HEAD`, which is also what a PR would show). This is the fork-avoidance strategy
working exactly as intended.

All **6 conflicts** sat precisely where upstream had reimplemented us, and every one was resolved by TAKING
UPSTREAM: `ColumnChunkWriter` (`ee7ee02` → `6eff6c4`, and upstream's is BETTER — it guards on
Int8/UInt8/Int16/UInt16 where ours widened every Int32-physical array and relied on the helper to no-op, and
it uses the shared `ArrowCompute.Widen`); `CompactionExecutor` + `ColumnMappingRecursive` + `SchemaEvolution`
(**comment-only** conflicts — the code was identical on both sides); `ValueWidener` (rewritten onto
`ArrowCompute`); `CdfReader` (our DV-inference part → `ac9a003`, better factored with a shared
`DeletionVectorReader`; upstream already carried the partition re-add too).

**A trap worth remembering: `git checkout --theirs <file>` takes the whole FILE, not just the conflicted
hunks** — so it silently discards our non-conflicting edits to that file. Before overwriting each one we
verified that the ONLY commits touching it were the superseded ones; none is touched by `7fecc2b` (variant
transport), `d8b041e` (row-level remap), `6ddfcc1` (create-time seams) or `0bfd020` (pruner public).

**One deliberate behaviour change, settled empirically.** Upstream's `WidenBatch` returns
`new Schema(fields, null)`; ours preserved `batch.Schema.Metadata` (the pre-merge base used `targetSchema`
wholesale — the bug both sides fixed). We took upstream rather than carry a one-line permanent conflict
point, and the suites decided it: `variant` at its full 144 is the case where schema-level metadata would
bite, and it passes — the variant tag rides FIELD metadata, so schema-level metadata is unused on our paths.

**BREAKING API, migrated in the same increment:** `04eaac4` consolidated four row-take implementations into
**`EngineeredWood.Arrow.ArrowCompute`** and deleted `DeletionVectorFilter.TakeRowsPublic` (a seam that
existed for us). Fabricator called it at two sites — `DeltaReader` (the merge-on-read post-image build) and
`DeltaTxnBuffer` (the pending-delete exclusion) — now on `ArrowCompute.Take(IArrowArray, List<int>)`, same
signature. `EngineeredWood.Core` reaches the Bridge transitively through `DeltaLake.Table`, so no new
reference was needed. **This is a compile break, not a deprecation: it must land WITH the pin bump.**

Also inherited: `72c128f` sliced Arrow columns wrote the WRONG ROWS (nulls right, values wrong,
self-consistent) — assessed as NOT live for us (we make no `IArrowArray.Slice` calls and DuckDB hands us
offset-0 arrays) but a real trap removed, since `ArrowColumnMappingRename` faithfully preserves
`data.Offset`; `6407c20` two decimal partition defects; `dc1e43b`/`8ef4a7c` `Take` now handles
list/map/fixed-size-list. **Still NOT implemented upstream: the Delta `PlanFiles` API** — Curt endorsed the
shape ("yes! This shape is nice") but `DeltaFilePruner` is still `internal` there. (The
`PlanFiles`/`PlannedFile` hits in upstream are **Iceberg**'s pre-existing `TableScan.PlanFiles`, which is
probably why the shape landed well — it asks Delta to gain the API Iceberg already has.) **We implemented
it ourselves on 2026-07-26 — see the next subsection; the `0bfd020` visibility patch is now retired.**

#### `PlanFiles` — DONE (2026-07-26, EW + Bridge): the pruner-visibility patch is RETIRED, replaced by a real API

`DeltaTable.PlanFiles(filter?, snapshot?, pruneSchema?) -> IReadOnlyList<PlannedFile>` (new
`PlannedFile.cs`: a `readonly record struct (AddFile File, int Ordinal)`) returns the snapshot's
active files **path-sorted, each carrying its ordinal, with provably-matchless files pruned out** —
the Delta counterpart of Iceberg's `TableScan.PlanFiles`, in the shape Curt endorsed. Sync and NOT
DV-resolving by design (everything it needs is already in the snapshot; resolving DVs means I/O,
which we do ourselves from the returned `add`s). All three load-bearing parameters are there: the
**PRE-prune** ordinal (pruning leaves GAPS, so two plans over one snapshot agree regardless of their
filters — a filter must not change what a rowid means), the **prune-schema override** (a buffered
txn's PENDING schema), and the **caller-supplied snapshot**.

**Why it is a correctness fix, not tidying.** Our `DeltaReader.BuildNativeScanListAsync` re-sorted the
active set and drove `DeltaFilePruner` by hand, so the ordinal in `(ordinal << 40) | file_row_number`
was **encoded by our copy of the rule and decoded by EW's** (`ComputeDeletionVectorActionsAsync`,
`ReadRowsByRowIdsAsync`), with nothing enforcing agreement — a THIRD site being EW's own encoder in
`ReadWithTransientRowIdsAsync`. That method is now also on `PlanFiles`, so the planner is the single
source of the ordering and the drift is structurally impossible. `0bfd020` (pruner `public`) is
reverted — the class is `internal` again, matching upstream, with only a doc paragraph pointing at
`PlanFiles`; `InternalsVisibleTo` already covered EW's own `NestedStatsPruningTests`.

**One regression caught in review, worth remembering: `NativeScanList.AnyUri` is PRE-prune by
contract** — it is the schema probe's fallback *when every file pruned away*, i.e. exactly when
`PlanFiles` returns empty. Taking it from the plan silently broke that; it now comes from the active
set directly (path-sorted minimum in one pass, so it is still the same file and still deterministic).
A planner that returns only survivors cannot serve a pre-prune question.

**Also fixed en route — our variant patch had re-broken the net472 test build.** Upstream's `8ac71a9`
deliberately restored it, and `VariantTransportTests.cs:190` used an array range indexer (`blob[n..]`),
which lowers to `RuntimeHelpers.GetSubArray` — absent on net472 (CS0656). Pre-existing (proven by
building a pristine HEAD), invisible because the gates ran net10.0/net8.0. Now `System.Array.Copy`
(`System.` qualified — bare `Array` is `Apache.Arrow.Array` in that file), and **Table.Tests runs 523
on ALL THREE TFMs including net472**, which previously did not compile. Anything we send upstream has
to build on the TFMs upstream declares.

`PlanFilesTests` (7) pins the properties that justify the API: dense ordinals unfiltered + path-sorted
order; the pre-prune ordinal survives a prune (a post-prune implementation would report 0 whenever one
file survives); ordinals agree across filters; **an ordinal decoded from a rowid names the file that
actually HOLDS the row** — asserted against file CONTENT (the add's recorded min/max must bracket the
row's id, and the three id ranges are disjoint) rather than against another ordinal, because once EW's
own encoder moved onto `PlanFiles` an ordinal-vs-ordinal check would only compare the API with itself;
caller-supplied snapshot; the prune-schema override changing whether a reference resolves; no active
files. Gates: EW Table.Tests **523** × {net10.0, net8.0, net472}.

**Gates:** EW Table.Tests **517** (was 444) / DeltaLake 211 / Expressions 139 / the NEW `Core.Tests` **430**,
all green; EW builds 0 warnings. Fabricator hermetic 53/53 @ **4152 (unchanged)** and service 42/42 @ 1227 —
the unchanged hermetic count being the signal that the bump is behaviour-neutral for everything we pin.
Merged with a **fast-forward, never a force-push**: the release tag `v0.0.1-duckdb1.5.5` pins `253e834`, and
orphaning that commit would make the tagged release unbuildable from source (`git submodule update` cannot
reliably fetch an unreachable sha). Verified `253e834` is still an ancestor after the merge.

#### EW BUMP 2026-07-27 — clast master `e28f70e`: COW CDF HANDED OFF UPSTREAM; two CreateAsync semantics we had to KEEP; pin `8aa7cfb`

10 upstream commits, 2 conflicted files, **14 hunks**. Three more of our patches are reimplemented
upstream and leave our diff: **CoW CDF capture** (`c9a29b1` — its own message says it is a port of
PR #4's branches onto master's read path, i.e. our work, MEASURED against Spark 4.0.1 on a
partitioned CDF table), the **partitioned-CDF lost-column fix** (`8e86061`), and **`configuration`
on `CreateAsync`**. Upstream's CDF is BETTER than what it replaces: it writes each change file per
rewritten SOURCE FILE with that file's `partitionValues` — which ours omitted, exactly the defect
`8e86061` fixes — and carries FIVE CDF tests where we had one (partitioned, whole-file, pre/post).
Three blocks of ours were DELETED as redundant rather than merged.

**The `configuration` collision was CONVERGENCE, not reimplementation** (checked, and it changes how to
pitch this upstream): `git log -S` shows upstream added the parameter in **`d9a913a`, the CHECKPOINT
commit** — Curt needed a way to set `delta.checkpoint.writeStatsAsJson` for his own tests — and upstream
has NEVER had feature-derivation, so he did not remove it. Two people needed a `configuration` parameter
for unrelated reasons and landed on the same name and position. That makes our derivation ADDITIVE rather
than a disagreement about precedence, so it is a coherent thing to offer upstream.

**Two `CreateAsync` differences were NOT taken — upstream's version is not equivalent despite the
identical signature, and taking it wholesale would have broken us SILENTLY:**
- **A `delta.enable*` property no longer ENABLES its feature upstream** (the boolean argument is the
  sole source of truth). But `inCommitTimestamps` and `changeDataFeed` have NO boolean argument, so a
  property is the only route, and our Bridge sets exactly those keys in the config dict
  (`DeltaGlobalTableFunction.cs` ~499-509). A property recorded WITHOUT its writer feature declared is
  the metadata/protocol mismatch Spark rejects outright. Kept the derivation, wired to the declaration
  so the two cannot come apart. Note upstream's doc says the boolean "remains the source of truth", but
  its default is FALSE and an omitted argument is indistinguishable from an explicit `false` — so under
  that rule the property is simply UNREACHABLE for DV/row tracking, which reads as a gap rather than a
  policy. Ours only ADDS an enabling route; it never overrides an argument that was explicitly true.
- **Upstream ALWAYS overwrites the row-tracking materialized column names with a fresh
  `_row_id_<guid>`.** Ours lets a caller-supplied name win, and must: `__delta_row_id` is a
  USER-VISIBLE queryable column we advertise (`verify_delta_row_tracking_virtual`, 299), not an
  internal name. A random per-table name would change what is written to disk and break those pins.

Also kept: `preAssignedSchema`, `PlanFiles`, the variant transport, the buffered row-level remap.
`PlanFiles` now threads the new `PreferTypedCheckpointStats` — otherwise the one path would quietly
serve typed columns to a caller who set it false to force the JSON path (the documented escape hatch).

**Inherited worth naming:** pruning from a checkpoint's **typed** stats (`9b8831c`; upstream measures
~14× faster with ~100× less allocation over a 100,000-file checkpoint — `DeltaFileStats` now parses
the JSON LAZILY, so a file answered from typed columns never touches its stats string), **checkpoints
Spark could not read** + a `stats_parsed` nothing could use (`d9a913a` — note the new
`CheckpointStatsMode` is `internal` and driven by `delta.checkpoint.writeStatsAsJson`/`...AsStruct`,
BOTH defaulting true, so checkpoints now carry both forms and no Bridge change is needed; reach the
keys via `fabricator_delta_set_tblproperties` if a strict reader ever needs JSON-only), **wide mark
bounds wherever a DV is attached** (`71547af`), a **CoW rewrite writing the partition column into the
file** (`54ba3e3`), and ORC/Avro/Lance fixes.

**Gates:** EW Table.Tests **549** × {net10.0, net8.0, net472} (was 523) / DeltaLake **217** /
Expressions 139 / Core 430 / Orc 235 / Avro 294. **The Bridge builds with NO change** (upstream kept
`configuration`'s name and position). Fabricator hermetic 53/53 @ **4152 — the same count as before
the merge**, which is the signal that a checkpoint-writer rewrite + a new pruning path + a CoW CDF
reimplementation are all behaviour-neutral for everything we pin; service 42/42 @ 1227 with
`verify_delta_catalog_s3` 161 and `verify_mssql_s3_polybase` 252 at full counts — the two that read
these checkpoints back THROUGH SQL Server, which is where a checkpoint regression would surface (it
has before: the snappy empty-payload bug once failed every read crossing an EW checkpoint).
Fast-forward, never a force-push; verified `253e834`/`4588594`/`b06b782`/`e5d6f04` are all still
ancestors, `e5d6f04` being the pin the current release tag depends on.

#### PATH-KEYED DV DML — DONE (2026-07-27, EW + Bridge, no ABI): the deletion-vector DML boundary stops speaking DuckDB's rowid

The deferred DV-DML entry points are now keyed by **`(path, absolute positions)`** — a new
`FileRowSelection` (`IReadOnlyDictionary<string /*add.path*/, IReadOnlyCollection<long>>`, taken verbatim
from the parked `proto/metadata-dml` proposal so the shape stays compatible with it) — instead of by a
file's PATH-SORTED ORDINAL in a snapshot's active set. `ComputeDeletionVectorActionsAsync` and
`RebaseDvDmlActionsAsync` take it as their CORE; the ordinal-keyed signatures survive as thin adapters
(`SelectionFromOrdinals` resolves and delegates), so upstream's callers and its 11 test call sites
(`BufferedTransactionTests` / `ReadWithRowIdsTests` / `SparkInteropTests`) keep working — and now give the
adapter free coverage. Design + the remaining rowid-keyed entry points:
[docs/rowid-dml-seam.md](docs/rowid-dml-seam.md) §3.1; concepts: [docs/rowid-concepts.md](docs/rowid-concepts.md).

**This is a correctness fix, not tidying, and two verified facts are why.** (1) The ordinal round-trip was
a **pure detour**: `RebaseDvDmlActionsAsync`'s very first act with `newPositionsByOrdinal` was to build an
`oursByPath` dictionary and use only that — the caller encoded path→ordinal and EW immediately decoded
ordinal→path, with a lossy integer in between; `ComputeDeletionVectorActionsAsync` used the ordinal for
nothing but `ordered[ordinal]`. (2) An unresolvable ordinal was **SILENTLY SKIPPED** by both
(`if (ordinal < 0 || ordinal >= ordered.Count) continue;`), so row identifiers captured against the wrong
snapshot did not fail — they deleted NOTHING, with no error. A path that is not active is recognisably
wrong, so the path-keyed core THROWS (and the rebase additionally throws if a selected path was not active
in `from`, the snapshot the selection claims to come from). The ordinal adapters KEEP the old leniency
deliberately — that is their historical contract.

**Bridge:** the buffered flush (`DeltaCatalog.FlushDmlTransaction`) and the merge-on-read UPDATE
(`DeltaReader.MergeOnReadUpdateAsync`) now do the rowid→path decode THEMSELVES via **`PlanFiles`**
(`PathsByOrdinal`), erroring loudly on an ordinal that names no active file (message carries the version
+ active count). The ordinal is OUR encoding of OUR rowid, so the decode belongs on our side.
**The loop is now closed through ONE planner on BOTH read paths** (verified): the native minting path is
`BuildNativeScanListAsync` → `PlanFiles`, and the CODEC minting path is EW's
`ReadWithTransientRowIdsAsync`, which iterates `PlanFiles(filter, snapshot)` directly (moved there by the
`PlanFiles` increment above) — so every ordinal that exists was produced by `PlanFiles` and every ordinal
consumed is resolved by `PlanFiles`; encode and decode cannot drift. **This is also where the PRE-PRUNE
ordinal contract earns its keep a second time:** the codec minting scan plans WITH a filter (pruning
leaves gaps) while the decode plans UNFILTERED, and that is only sound because the ordinal indexes the
*unfiltered* path-sorted set — the unfiltered plan is a superset of every ordinal any filtered plan could
emit. A post-prune ordinal would force the DML side to reproduce the scan's filter, which it does not
have.
**`CommitDataFilesAsync`'s `deletedPositionsByFileIndex` correctly STAYS index-keyed** — a different index
space (our `0x780000+` pending eager files), which are in no snapshot, so no path can name them. Not a gap.

**Same day, second slice — the AUTOCOMMIT DELETEs too:** `DeleteBySelectionViaVectorsAsync` (DV) and
`DeleteBySelectionAsync` (copy-on-write) are now the cores; `DeleteByRowIdsViaVectorsAsync` /
`DeleteByRowIdsAsync` are adapters. Both fabricator call sites decode via `PlanFiles` on our side
(`DeltaReader.SelectionFromRowIds`), so the loud error covers the autocommit paths too, and increment 1's
core was refactored onto a shared `ResolveSelection` (one mapping, one error shape). **Be honest about the
value here: the hazard is WEAKER than the buffered case** — autocommit is scan-then-mutate in ONE statement
against ONE snapshot, and `rowLevelRetry`'s reload re-validates through already-path-keyed `DeleteDvEdit`
records, so there was no live silent-loss bug on these paths. The win is uniformity + removing a
DuckDB-shaped 64-bit packing (and its ~8.4M-file ceiling) from a Delta library's public API.
**Trap caught by reading, not by the compiler:** the copy-on-write loop probes the selected positions ONCE
PER ROW (`targets.Contains(abs)`); the decode had always handed it a `HashSet<long>`, and passing the
caller's `IReadOnlyCollection<long>` through compiles fine but silently binds those probes to **LINQ's O(n)
`Contains`** → O(rows × selected). `ResolveSelection` materialises one `HashSet` per file and its doc says why.

**The REMAINING blocker is not a file key — it is PER-ROW identity (verified 2026-07-27).**
`UpdateByRowIdsAsync` and `ReadRowsByRowIdsAsync` carry packed rowids ACROSS the boundary per ROW:
`UpdateByRowIdsCoreAsync`'s `rewriteFile` callback gets `rowIdsPerBatch` (an `Int64Array` of packed rowids
per source row, for O(1) substitution), and the overload we actually use IGNORES the file-ordinal argument
entirely; `ReadRowsByRowIdsAsync` fills `rowIdsOut` with packed rowids, its own comment explaining that
"emission order alone cannot key a lookup". So path-keying only their INPUT would remove nothing. Converting
them = replacing per-row identity with `(file_path, row_index)` — **exactly the parked `proto/metadata-dml`
`_metadata` shape** — so it should be scheduled as the prototype revival, not as "two more overloads".
Corollary: a §4 STRUCT rowid would not help either; it is the same problem one layer in.
See [docs/rowid-dml-seam.md](docs/rowid-dml-seam.md) §3.2/§3.3.

**Tests:** EW `FileRowSelectionTests` (6) — the two keyings name the same rows; a path-keyed delete removes
exactly the selected rows across files; **the silent-loss case** (identifiers resolved against a snapshot an
overwrite shrank ⇒ the ordinal form reports 0 rows deleted with no error, the path form throws); unknown
path; the row-level rebase composing with a concurrent same-file delete; a rebase selection naming a
non-`from` file. **Every fixture has THREE files** — with one file the ordinal is always 0, so a
single-file fixture cannot fail these tests (the trap recorded in rowid-dml-seam.md §6).
**Gates:** EW Table.Tests **555** × {net10.0, net8.0, net472} (was 549) / DeltaLake 217 / Expressions 139 /
Core 430; `verify_delta_catalog_transactions` 941 / `verify_delta_row_level_concurrency` 70 /
`verify_delta_row_tracking_virtual` 299; hermetic **53/53 @ 4152** AND service **42/42 @ 1227** — both the
SAME counts as before the change, which is the signal that re-keying the DV-DML boundary is
behaviour-neutral for everything pinned (a re-key that quietly dropped or mistargeted a row would MOVE a
count, not merely fail a suite). The service tier matters here specifically because
`verify_delta_catalog_s3` runs the buffered DML flush over S3, where the `add.path` keys are the ones a
path-keyed selection is built from.
**Trap re-learned:** `Dictionary.TryAdd` does NOT exist on **netstandard2.0** → it broke the net472 leg
only (same class as the earlier `blob[n..]` range-indexer break). Only the net472 leg proves a change is
offerable upstream.

#### THE `_metadata` SURFACE — BUILT (2026-07-27, EW-only, no ABI/Bridge change): one per-row identity column

The parked `proto/metadata-dml` design is ported, in the shape the user settled: a trailing **`_metadata`
STRUCT** with **FOUR** members, not the prototype's two — `file_path` + `row_index` (non-null **LOCATOR**, a
physical address valid for this snapshot) and `row_id` + `row_commit_version` (**nullable IDENTITY**, durable
across rewrites). That is Spark's own vocabulary for a row-tracking Delta table, which our live Fabric/Spark
validation has been querying all along (`_metadata.row_id` ×22 in this file). Full record + what remains:
[docs/rowid-dml-seam.md](docs/rowid-dml-seam.md) §5.1/§6.

**Why one struct.** It collapses THREE surfaces that were inconsistent: EW's trailing column (which held the
TRANSIENT locator under the stable id's spec name — the fossil), EW's stable-id **out-params**
(`sourceRowTrackingOut`/`strippedRowIdsOut`/`strippedVersionsOut` — verified upstream's, not ours), and
fabricator's SQL-reconstructed `__delta_row_id` virtuals, which exist only because the first took the name.
The **nullability split is load-bearing, not defensive**: the locator always exists; the ids are NULL wherever
underivable (a file predating row tracking, a row rewritten from one, an add with no `baseRowId`) — the
existing `COALESCE` story expressed as a type. Shape is FIXED at four members even with row tracking off (ids
all-null), so a consumer binds one schema whatever the table config (Spark varies its shape; we chose stability).

**NOT a port of the prototype's reader — deliberately.** That hand-rolls its own parquet open, mapping rename
and DV filter, and its own note conceded "full schema, no projection, partition columns not re-added". The
current base computes every member on the MAINTAINED read path (`ReadFileAsync`'s `strippedAbsPositionsOut` =
`row_index`, `strippedRowIdsOut`/`strippedVersionsOut` = the ids, path from the planned add), so
`ReadAllWithMetadataAsync` is ~25 lines of pure assembly that inherits projection, column mapping, partition
re-add, schema reconciliation and DV semantics for free.

**UPDATE is now path-keyed — §3.3's UPDATE half is CLOSED.** `UpdateBySelectionCoreAsync` hands the rewriter
`(filePath, sourceBatches, positionsPerBatch)` instead of `(fileOrdinal, …, rowIdsPerBatch)`. Two entry
points: `UpdateBySelectionAsync(selection, rewriteFile)` and — the payoff —
`UpdateBySelectionAsync(RecordBatch updates)`, a **round trip** (read with `_metadata`, change values, hand the
batch back; no substitution code, no ordinal, no packed rowid) which reuses the rowid-keyed substitution helper
VERBATIM because within one file a position is already a unique key. All three `UpdateByRowIdsAsync` overloads
became re-packing adapters; the `RecordBatch`-keyed one stays rowid-keyed BY CONTRACT (its input carries a
rowid column).

**Predicate lowering + a MEASURED zero-read DELETE.** `MetadataPredicate` (self-contained) is wired at the head
of `DeleteAsync(predicate)`: a physically-addressing predicate lowers to a `FileRowSelection`; one that MENTIONS
`_metadata` but cannot lower is **rejected loudly** rather than handed to the row mask, which binds data columns
only and would delete the wrong rows silently. `UpdateAsync(predicate)` is **symmetric** (added after review
caught that a DELETE-only guard left UPDATE passing `_metadata` straight to a data-column row mask — a live
mis-evaluation hazard); `MatchedRowsUpdater` adapts the predicate surface's matched-rows-only updater onto the
selection primitive. Data-column predicates are untouched (`TryLower` returns false; the guard no-ops).
**The zero-data-reads claim is MEASURED and SCOPED — it holds in ONE configuration only**, and three tests
pin the boundary rather than prose describing it: **DV on + CDF off ⇒ 0 data-parquet opens** (the real fast
path); **DV on + CDF on ⇒ reads only the SELECTED file(s)** for change-feed content; **DV off ⇒ copy-on-write,
reads AND rewrites the affected files — NOT a fast path**. In the lower two tiers the lowering still helps but
differently in kind (it names files directly instead of evaluating a mask over pruning candidates).

**Gates:** EW Table.Tests **571** × {net10.0, net8.0, net472} (was 555 pre-`_metadata`) / DeltaLake 217 /
Expressions 139 / Core 430; fabricator hermetic **53/53 @ 4152** and service **42/42 @ 1227**, BOTH unchanged.

**FABRICATOR IS MIGRATED OFF EW's ROWID UPDATE SURFACE (same day, no ABI change):** `DeltaReader`'s
copy-on-write UPDATE calls `UpdateBySelectionAsync`, re-keying the DuckDB updates batch onto the `_metadata`
struct (`ReKeyUpdatesOntoMetadata`; fills only the two LOCATOR members — EW resolves each row's own stable id
from the file it rewrites, so passing ids would assert identity we do not own). ⚠ **Unlike the rest of this
work it is NOT behaviour-neutral by construction** — it re-keys a LIVE path — so it was gated targeted-first
(`update` 63 / `dv_default` 58 / `native_write` 147) before the full tiers, and the service tier matters
because `verify_mssql_s3_polybase` is protocol 1.0 ⇒ DV off ⇒ exactly this copy-on-write path.
**The packing did not vanish, it RELOCATED**: `(ordinal << 40) | position` now exists ONLY in fabricator's
DuckDB adapter — where it belongs, since DuckDB's own `rowid` is a single BIGINT — and no longer in a Delta
library's public API. That is §1's "we should own the translation", achieved. **`UpdateByRowIdsAsync` itself
is NOT ours to delete**: upstream's public surface, exercised by upstream's `RowIdDmlTests` /
`SparkInteropTests` / `PartitionedRewriteLayoutTests`; removing it would delete upstream tests and make the
patch set LESS upstreamable. The `_metadata` READ surface still has no fabricator consumer — wiring that is
really the §4 STRUCT-rowid question in disguise.

**The LAST rowid-keyed API is `ReadRowsByRowIdsAsync`**, and it is the interesting one: it both TAKES rowids and
EMITS them (`rowIdsOut`) beside `sourceRowTrackingOut`, so converting it collapses **three out-params into the
one struct** — finishing the unification. It reaches the Bridge's buffered UPDATE read-back and CDF delete
capture, so it is its own increment, not a tail to sweep up.

#### EW BUMP 2026-07-27 (second, same day) — clast master `b1de9d8`: TWO MORE PATCHES ABSORBED; Curt has published a TRIAGE of our PR

14 upstream commits, **one** conflicted file (`DeltaTable.cs`, 13 hunks), pin → EW `5c5f99f`.

**Both `CreateAsync` semantics the FIRST bump recorded as "deliberately KEPT" are now upstream:**
- **`7df0b42`** absorbs the feature-derivation. Upstream now enables a feature from its `delta.enable*`
  PROPERTY as well as its boolean argument — and **wider than ours**: `deletionVectors`, `rowTracking`,
  `inCommitTimestamps`, `changeDataFeed`, **`icebergCompatV1/V2`** and **`columnMapping.mode`**, each
  declared in the commit-0 protocol, with the same precedence we chose (argument beats property;
  enablement is one-directional). It also fixes something worse than we knew: `delta.columnMapping.mode`
  passed as a PROPERTY produced an **UNREADABLE table** — metadata claiming name-mapping over a schema
  whose fields were never assigned physical names or ids.
- **The materialized-NAME semantic too** — found in a DOC hunk, not in any commit subject: upstream's
  `CreateAsync` doc now states "caller-supplied row-tracking materialized column names win". That is the
  second thing we were carrying for `__delta_row_id`'s sake. **Lesson: read the doc hunks of a conflict,
  not just the commit messages — an absorbed semantic can announce itself only there.**
- **`9258706` is OUR projected-read fix, ported and CORRECTED** ("Ported from PR #4 commit `2007c39`"):
  minus its "read one column the file does have so row counts survive" guard, which upstream MEASURED as
  unnecessary — its reader takes the row count from the ROW GROUP, not from the columns returned.
  ⚠ **Do NOT propagate that correction to our seam reader** — and this is now MEASURED, not merely
  suspected (2026-07-28): `SELECT FROM read_parquet(…)` is `Parser Error: SELECT clause without selection
  list`, so a zero-column result set is NOT EXPRESSIBLE in DuckDB SQL. `NativeParquetDataFileReader` gets
  its row counts from the returned batches, so our "read one column the file does have" guard is load-bearing
  on this seam even though upstream's own reader (which takes the count from the ROW GROUP) does not need it.
  Settled: a real divergence, not a redundancy.

**Resolution by class, not by reflex:** hunks 1–8 (the `CreateAsync` feature-declaration region) take
UPSTREAM as a strict superset; 9–10 keep OURS (`preAssignedSchema` is still not upstream); 11 COMBINES
(our positions-builder semantics + upstream's `.Reserve(length)`); 12–13 take UPSTREAM's corrected fix.
**Merge hazard worth remembering: our `dvEnabled`/`rowTrackingOn` derivation sat OUTSIDE any conflict
hunk, so git auto-merged it into a DUPLICATE declaration beside upstream's** (`CS0128` caught it).
Conflict markers show where two sides DISAGREE textually — not where they have begun saying the same
thing twice. Verified after resolving: all 8 of our patches still present, upstream's IcebergCompat /
enable-property work arrived, `rowTrackingOn` gone to 0.

**Inherited:** `d0b7bf8` (a union ANYWHERE in a nested subtree broke `MakeNullArray` — shapes this
library itself writes via ORC), six parquet/delta PERF commits on paths we hammer (dictionary
double-copy, oversized string dictionaries, constant-column discovery by hashing every row,
high-cardinality analysis skipped, the CDF `_change_type` column appended row-at-a-time, the identity
column built straight into its Arrow buffer), and `b1de9d8` (assemblies now strongly named, new
`Clast.snk` + root `Directory.Build.props` — our build is unaffected, .NET Core does not enforce
strong-name identity).

**⭐ THE FIND: `doc/upstream-landing-notes.md` now carries Curt's OWN triage of our nine PR commits,
"classified by who they serve, because that is the question that decides whether they land."** Read it
before offering anything upstream — it is the roadmap written by the reviewer, and it revises what to
lead with: he names the **buffered rebase remap** "the strongest non-Fabricator argument of this group:
two public surfaces currently answer the same conflict differently"; he asks for **`VariantTransport` to
be SPLIT** because variant **shredding on write** is "a general-purpose passenger worth separating";
his gap 9 (public `DeltaFilePruner`) is **stale — we retired that ask for `PlanFiles`**; and he
understands why we need `rowIdsOut` ("without it a caller cannot key returned rows back to the rowids it
requested"). He also **independently confirms the PR #4 hazard** — "merging as-is would revert
run-end-encoded definition levels, per-column dictionary control, `ArrowCompute.MakeNullArray`, the
shared DV-reader reuse and the compaction builder `Reserve`" — which is the same finding as its diff
reading −17150 lines. **And he records that "merge-on-read UPDATE is no longer on offer from upstream"**
after the PR rewrite dropped it: it appears nowhere in his gap list, so our
`UpdateBySelectionViaVectorsAsync` fills a hole he has flagged as unowned.
**One thing to report back:** his new open-decision item says the codec seam's read path hands its
projection to a host decoder, so "a host codec faces the schema-evolution problem itself, unstated" —
we already intersect it (`NativeParquetDataFileReader`), which is evidence for that audit.

**Gates:** EW Table.Tests **613** × {net10.0, net8.0, net472} (was 583, +30 upstream tests) / DeltaLake
217 / Expressions 139 / Core **452** (was 430 — the union arms) / Orc **237** / Avro 294 passed **+ 7
newly skipped** (new upstream skips, not failures); fabricator hermetic **53/53 @ 4152** unchanged.

#### MERGE-ON-READ UPDATE MOVED INTO EW — `UpdateBySelectionViaVectorsAsync` (2026-07-27, no ABI change)

**The only piece of the 2026-07-27 Delta work that adds a CAPABILITY rather than re-keying an existing one.**
Every prior UPDATE entry point in EW paid a FULL FILE REWRITE, because Delta has no "row updated" bitmap — so
the cheap shape (DV-mask the old rows, APPEND their post-images, one fused commit) existed only as a
composition fabricator had worked out and was assembling by hand in `MergeOnReadUpdateAsync`. Full record:
[docs/rowid-dml-seam.md](docs/rowid-dml-seam.md) §6.6.

A **LIFT, not a fresh implementation**: the four primitives and their ordering
(`ComputeDeletionVectorActionsAsync` + `WriteDataFilesAsync(materializedRowIds:)` +
`WriteChangeDataFilesAsync` + `CommitDataFilesAsync(extraActions:, expectedVersion:)`) were already proven by
`update` 63 / `dv_default` 58 / `row_tracking_virtual` 299 and by live Fabric/Spark validation — what changed
is where the semantics live. **Row tracking is PRESERVED** (each moved row keeps its ORIGINAL stable id,
materialised into the post-image file — copy-on-write cannot, since its rewrite re-derives every row's id; an
underivable id abandons materialisation for the WHOLE statement rather than baking a wrong one). CDF rides as
the `update_preimage`/`update_postimage` pair. DV required + IcebergCompat refused, both clean errors NAMING
the copy-on-write alternative — never a silent fallback, since the IO costs differ by orders of magnitude.

**TWO overloads, and the KEYED one is load-bearing:** `updater(matched)` suits a transform that is a FUNCTION
of the row (`salary * 2`); `updater(filePath, matched, positions)` suits values from a host-side JOIN, keyed by
`(file, position)`. Fabricator could NOT use the simple form — its values come from a DuckDB join, so aligning
them by EMISSION ORDER would be wrong (the same fragility the parked prototype's `updater(matchBatch)` has).
So the keyed form is demonstrably necessary, not speculative.

⚠ **INVOCATION GRANULARITY differs between the two selection UPDATE surfaces** (undocumented until this pass,
and our own code was correct only by accident of keying): the merge-on-read updater fires once per SOURCE BATCH
containing a match — possibly several times per file — while `UpdateBySelectionAsync`'s `rewriteFile` fires once
per FILE with all its batches. **Key by `(filePath, position)`; never accumulate state per call.**

**Bridge: `MergeOnReadUpdateAsync` delegates to it, −26 lines net.** What remains is only what is genuinely
ours: decode DuckDB's packed rowid → `(add.path, absolute position)`, substitute the join's values. Gated
targeted-first as a re-key of the LIVE autocommit DV UPDATE path (`update` 63 / `dv_default` 58 /
`row_tracking_virtual` 299 / `changes` 73), then hermetic **53/53 @ 4152** + service **42/42 @ 1227**, both
unchanged. EW Table.Tests **583** × {net10.0, net8.0, net472}.

#### EW BUMP 2026-07-28 — clast master `fe74b0c`: THREE MORE PATCHES ABSORBED, and the `_metadata.row_id` name is now FREE for our surface

10 upstream commits, 4 conflicted files, **23 hunks** in `DeltaTable.cs`. Curt has worked through his own
triage doc, so the upstream offer sequence collapses from seven items to three.

**Three more of our patches are reimplemented upstream and leave our diff:**
- **`88adb02` = `PlanFiles`**, whose message names it *"the alternative to making DeltaFilePruner public that
  PR #4 proposed"*. He arrived at the SAME signature and BOTH extra parameters independently, including the
  PRE-prune gapped-ordinal invariant pinned by a test that fails if you renumber survivors.
- **`d7c9d73` = the buffered rebase remap** — *"Ported from PR #4 d8b041e, adapted to master"*, with 6 tests
  where we had 3. This was item 2 of our offer sequence, the one he called the strongest non-Fabricator
  argument of the group.
- **`e20af70` = `preAssignedSchema` + `materializedRowIds` + `rowIdsOut`** — *"the last of PR #4's additive
  seams, all three"*.

**⭐ `72b3888` (BREAKING) renames the transient column `_metadata.row_id` → `_ew_row_address`** behind a new
`TransientRowAddress` type owning the encoding and the pack/unpack helpers. He found the fossil we had
documented, independently and by measurement ("a helper reading stable ids from that column returned 2^40 for
the first row of file 1"), and his reason is the one that matters to us: *"When the read side eventually
exposes real row-tracking ids, the only correct name for that column is `_metadata.row_id`, and it is
occupied."* **That read side is our `ReadAllWithMetadataAsync`** — so he has cleared the runway for our
`_metadata` surface rather than colliding with it, and our four-member struct now occupies a name upstream
deliberately vacated.

**Why we took HIS `PlanFiles` rather than keeping ours** (checked, not assumed): ours differed in exactly two
ways, and the first is DEAD — our `filter is null or TruePredicate` short-circuit only avoided constructing
the pruner, because `DeltaFilePruner.ShouldInclude` already short-circuits `TruePredicate` on its first line,
and our Bridge never passes one (zero sites). The second is one test he lacks (empty table). Against that his
has 11 tests to our 7 (DV-reported-not-resolved, unknown-column-keeps-every-file, after-dispose-throws) and a
sharper doc on the `schemaOverride` trust boundary: a name mapped to the WRONG physical name reads another
column's statistics and can prove `AlwaysFalse` for a file that does hold matching rows — silent data loss,
which ours only implied. Cost of adopting: `PlannedFile` is `(int FileOrdinal, AddFile File)` — fields swapped
AND renamed — so every deconstruction needed touching. The compiler caught all of them.

**⚠ THE TRAP OF THIS BUMP — an auto-merged duplicate STATEMENT, which the compiler CANNOT catch.** The
previous bump's trap was a duplicate *declaration* (`CS0128`). This one was a duplicate *statement*: upstream
moved the materialized-row-id re-attach to AFTER the variant-annotation strip (its comment explains why — the
column name comes from table metadata and is already physical, so passing it through the mapping would rename
it), while our old placement sat in non-conflicting context. The merge kept both, so
`WriteDataFilesAsync` appended the id column TWICE — `RowTrackingWriter.AddRowIdColumn` appends
unconditionally, so every materialized data file would have carried two identically-named `__delta_row_id`
columns. **EW Table.Tests ran 656/656 green WITH the duplicate present** — verified both ways — including
upstream's brand-new `HostRowIdentityTests` and its two-partition-split case, because a reader resolving by
name gets a correct value from one of the two copies. Silent, and invisible to both suites.
**The habit that found it, worth keeping: after taking a method wholesale from upstream, diff it against
upstream's copy and demand BYTE-IDENTITY.** `WriteDataFilesAsync` and `CreateAsync` are now identical to
upstream's; the two methods that still differ (`RebaseDvDmlActionsAsync`, `ReadRowsByRowIdsAsync`) differ
for reasons we can name. A one-line test asserting exactly ONE materialized id column is a cheap upstream offer.

**Three breaking changes migrated.** (1) `RowTrackingConfig.VirtualRowIdColumn` is gone — and the three Bridge
sites did NOT want its replacement: the rowid column in the updates batch is named by OUR DuckDB-facing
constant, and the two strings were only ACCIDENTALLY equal until upstream separated them. Now
`DeltaCatalog.RowIdColumn` (widened to `internal`, with the reasoning on it). (2) `PlannedFile` field order +
name. (3) `rowIdsOut` is `List<long[]>` and moved after the cancellation token — his shape and the better one,
since a plain value array leaves the caller no Arrow buffer lifetime to manage, which is exactly what
`2348d69` argues for.

**⚠ THE SECOND TRAP, and the one that justifies the service tier's existence: a COLUMN-NAME rename breaks
STRING-KEYED lookups, and the C ABI MASKS the mismatch.** EW's `ReadAllWithRowIdsAsync` now emits
`_ew_row_address`, but `DeltaCatalog.ScanCodec` DECLARES its stream schema with `RowIdColumn`
(`_metadata.row_id`) — so the declared schema and the batch schemas disagreed. `arrow_ingest` reads the
DECLARED schema and is positional thereafter, so **DuckDB never noticed and hermetic passed 53/53 @ 4152**.
The only consumer that looks at a *batch's* schema in C# is `ExternalTableRouting`'s identity→rowid resolution
(`b.Schema.GetFieldIndex`), which fails LOUDLY — so the break surfaced in exactly one suite,
`verify_mssql_s3_polybase` (slice D, identity-keyed UPDATE on a detected external Delta table), which is
service-tier-only. Fixed at the SOURCE — `DeltaReader.RenameRowAddressToDuckDbRowId` renames EW's column to
DuckDB's at the boundary in both `StreamWithRowIds*` paths, so the declared and actual schemas agree again —
and the four hardcoded `"_metadata.row_id"` literals in `ExternalTableRouting` now reference
`DeltaCatalog.RowIdColumn` so the next rename cannot break it silently. **Lesson: after an upstream column
rename, grep for string-keyed column lookups; a declared-schema/batch-schema disagreement is invisible across
the C ABI and therefore invisible to almost every test.**

**~~Kept deliberately~~ — DELETED 2026-07-28: our derived-id fallback in `ReadRowsByRowIdsAsync` was DEAD
CODE, verified in OUR tree rather than taken on trust.** Upstream declined to port it and said why, having
measured it. We kept it as "cheap and behaviour-identical wherever the claim holds" — but the claim holds
unconditionally here: `ReadFileAsync` (~line 7465) computes
`id = mid ?? (BaseRowId is {} ab ? ab + thisBatchStart + i : null)` / `ver = mv ?? DefaultRowCommitVersion`,
and its own doc says `strippedRowIdsOut` carries the per-row **RESOLVED** value. Our fallback re-applied that
identical derivation to a value already through it, so a null there means the source genuinely predates row
tracking and nothing remains to derive. **Worth checking before deleting rather than after**: had it been
load-bearing, removal would have produced null stable ids SILENTLY, and the buffered UPDATE would then abandon
materialization for the whole statement instead of preserving ids. ⇒ `ReadRowsByRowIdsAsync` is now
**byte-identical to upstream's**, retiring one of the two methods that still differed (`RebaseDvDmlActionsAsync`
is the remaining one). The reasoning lives HERE and deliberately not as a comment in `DeltaTable.cs` — a comment
in the file that conflicts on every bump is a permanent conflict point for no behavioural gain.

**Inherited worth naming:** **`2348d69`** — a span over a caller's Arrow buffer outliving its
`NativeMemoryManager`, whose finalizer frees the native memory a span does not root. It took an
`AccessViolationException` in CI *on a docs-only commit*, and the reason it is worth more than a one-line fix
is that **the span still READS CORRECTLY after the free** (freed HGlobal pages stay mapped), so the common
outcome is silent wrong data and the AV is the lucky case. He audited all 325 sites and drew the line by
PROVENANCE, not by which code reads: EW builds every buffer it owns with `new ArrowBuffer(byte[])` (no
`ArrowBuffer.Builder` anywhere in `src/`), so the whole read/decode path is managed-backed and safe by
construction; only the write/analysis path receives caller arrays, and it is rooted at the OUTERMOST method
that receives one. Also **`f1a64f5`** — a host-facing `Stage*` API on `DeltaTransaction` (`StageDataFiles`,
`StageRowDeletesAsync`, `StageSchemaChange`, `StageChangeDataAsync`, `StageActions`) plus a public
`DeltaTransaction.Snapshot`, which fixed a PRE-EXISTING row-id double-reservation bug (two staged appends of 3
rows each both got `baseRowId=3`; the mark ended at 6 for 9 rows, and nothing failed at commit). **We are
immune — we never touch `DeltaTransaction`** (verified) and our fused flush assigns every baseRowId in ONE
`CommitDataFilesAsync`. That API is nonetheless an OPPORTUNITY: it could replace our hand-rolled ~200-line
`FlushDmlTransaction` loop, whose invariants (re-rebase from the ORIGINAL actions on retry, pass
`rowLevelDml` or silently get file-granularity conflicts) are exactly the ones he says are enforced by no type.
And **`e86b5a6`** — `AddColumnAsync`/`ComputeAddColumn` overloads taking a Delta `StructField` directly, plus
`CodecSeamValueBlindnessTests` MEASURING that the write path never inspects a column's values, so a host may
present its own physical representation for a declared Delta type (the read direction is deliberately NOT
symmetric and that asymmetry is asserted too).

**A DECISION IS OPEN, and it is ours to make.** `e86b5a6` ends: *"Together with preAssignedSchema this is what
PR #4's variant transport needs from the library: two overloads and a documented contract, instead of a new
file, an Apache.Arrow.Operations reference, a public VariantTransportBlob option, and a marker string naming a
specific downstream project. Whether that is enough is cmettler's call; the seam is now measured rather than
inferred either way."* Taking it would retire our LARGEST remaining patch (`VariantTransport.cs` + its tests +
the `DeltaTableOptions` option) by moving blob⇄`VariantArray` — and the shredding built on
`Apache.Arrow.Operations` — to the Bridge, which also removes the one thing in our patch set that is least
defensible upstream: a marker string naming `fabricator` inside a general-purpose library. Not done here; it
is a scoped follow-up, not part of a pin bump.

#### THE VARIANT-TRANSPORT DECISION — KEEP the transport, RENAME the marker (2026-07-28; EW + Bridge + C++)

Curt's `e86b5a6` put it to us directly (*"whether that is enough is cmettler's call"*): retire our
`VariantTransport` patch in favour of his `StructField` overloads + the measured codec-seam contract, moving
blob⇄`VariantArray` to the Bridge. **Answer: no, and the reason is structural.** His framing — *"two overloads
and a documented contract, instead of a new file, an Apache.Arrow.Operations reference, a public option, and a
marker string"* — assumes the transport is ONE boundary. It is **five call sites**
(`ComputeUpdateActionsAsync`, `ComputeWriteActionsAsync`, `WriteDataFilesAsync`, `RewriteRowsToNewFileAsync`,
`ProcessFileBatchesAsync`), and the read-side one sits at the BOTTOM of a pipeline with **14 `ReadFileAsync`
callers** — the plain reader, the predicate UPDATE, `ReadRowsByRowIdsAsync`, CDF capture, merge-on-read UPDATE,
compaction. One gated conversion point is what keeps host-facing and EW-INTERNAL paths consistent across all of
them; moving it out means converting at N boundaries (two of them a CALLBACK and an async STREAM nested inside
EW operations) and guaranteeing no internal path sees the wrong form. **The failure mode is silent** — a variant
column reads as a bare struct-of-binary — on the feature with our most extensive live validation.

**What we DID instead kills the actual objection for one line.** His complaint is specific and narrow: *"a
marker string naming a specific downstream project."* That string was ONE constant, so
`SchemaConverter.VariantTransportExtensionName` is now **`ew.variant_transport`** — named for the library whose
boundary form it is, mirroring the `_ew_` prefix upstream chose for `_ew_row_address`. No general-purpose
library file mentions a downstream project any more, and the placement that makes 14 consumers correct is
untouched. **Safe because the marker is BOUNDARY-ONLY**: in-memory Arrow field metadata, while the Delta schema
records `variant` and the parquet file carries the canonical annotation — nothing persisted references it, so no
table written under the old name needs migration.
**Chosen over making it configurable**: a `DeltaTableOptions` setting would have to reach `FromArrowSchema`
(static, 8 call sites) for no behavioural gain — the value only has to be AGREED between a host and the library,
and a neutral constant agrees as well as a threaded parameter.
**⚠ A MARKER RENAME IS THE QUIET FORM OF THE STALE-BINARY TRAP.** The stale-binary note in "Build & test"
covers C++ BEHAVIOUR changes; this was a change to a STRING that must match across the ABI, so a stale binary
does not fail — it silently degrades the type (VARIANT → BLOB), and every variant operation then reports a
plausible-looking conversion error. Cost real time: only `unittest` had been rebuilt, so probing in
`duckdb.exe` looked like a code defect until CREATE and ALTER failing IDENTICALLY gave it away (a genuine
ALTER-specific bug cannot break CREATE). **After such a rename rebuild ALL THREE — `unittest`, `shell`,
`fabricator_loadable_extension` — and check MTIMES, not exit codes.** All three are current as of 2026-07-28.
**⚠ The rename is a THREE-LAYER lockstep and needs a C++ REBUILD, not just a republish:** the same string is
`VariantMarker.ExtensionName` (Bridge) and `kVariantExtensionName` (`fabricator_variant.cpp`), and the C++ one
is the name registered in DuckDB's Arrow extension registry — it must match what crosses the ABI, so a stale
`unittest`/loadable would mismatch a fresh bridge. Also updated `engineered-wood/doc/codec-seam-investigation.md`,
which is HIS doc and referenced the old name.
**HIS `StructField` OVERLOAD IS NOT NEEDED BY US — and checking that found a real bug (2026-07-28).** His
premise, *"ADD COLUMN can only ever say Delta `binary`, permanently"*, holds for a host WITHOUT a marker
mechanism — i.e. exactly the host we would have become had we moved the transport out. Under our marker,
`ALTER TABLE … ADD COLUMN v VARIANT` already commits a Delta `variant`, because
`SchemaConverter.FromArrowField` checks the marker BEFORE the storage type and the ALTER path crosses the new
column as an ordinary single-field Arrow schema. So the overload buys us nothing — which is one more (small)
argument for the placement we kept. `CodecSeamValueBlindnessTests` we take for free: it is a guarantee, not code.
**But the capability was UNPINNED, and probing it hit `delta native read: no NULL-backfill type mapping for
'variant'`** — `DeltaNativeReader.TypeText` renders `CAST(NULL AS <type>)` for a column absent from an older
file and had no `variant` case (a deliberate throw from the nested-variant gating era). So the metadata commit
and new rows worked while READING the table failed whenever any file predated the added column.
`CAST(NULL AS VARIANT)` is valid DuckDB, so it is one line; now pinned by 13 assertions
(`verify_delta_catalog_variant` **157**, was 144) including a re-ATTACH, which proves the DELTA SCHEMA says
variant rather than just this session's binding. **Diagnostic trap worth remembering: `duckdb_columns()`
reports BLOB for a variant column on BOTH the CREATE and ALTER paths** — that is the storage type and the scan
resolves the marker at bind, so it is not a signal about this at all; reading it as one sent the first
investigation down a false path.
**Gates (marker rename):** variant **144** (pre-backfill-fix) / native_write **147**; EW Table.Tests 678 ×3
TFMs / DeltaLake 217; hermetic **53/53 @ 4152** and service **42/42 @ 1227**, both unchanged.

#### THE SHREDDING SPLIT — DONE (2026-07-28, EW-only, no Bridge/ABI change): gap 8's "general-purpose passenger", separated

He asked for variant **shredding on write** to be separated as *"a general-purpose passenger worth
separating"*, noting it "is also what drags in the new `Apache.Arrow.Operations` dependency on the Delta
layer". Both halves are done and **the dependency half costs nothing**: `EngineeredWood.Parquet` ALREADY
referenced Operations (upstream's own project) for shredded-read reassembly — we had only added it to
`DeltaLake.Table` — so the split lands in Parquet, where shredding belongs anyway (a physical-layout
concern, the VariantShredding spec), and **`Apache.Arrow.Operations` is now GONE from
`EngineeredWood.DeltaLake.Table` with zero net dependency change across the repo.**
**Verified, not assumed, that this is a real −1 and not a swap:** `Apache.Arrow.Scalars` — where
`VariantValue`/`VariantReader`/`VariantBuilder` live — is a dependency of **`Apache.Arrow` itself**
(checked in the nuspec), so the Delta layer keeps blob encode/decode with no package of its own.

**The target class ALREADY EXISTED** — `EngineeredWood.Parquet.Data.VariantShredding`, `internal`, holding
only `Reassemble` (the read half, used by `NestedAssembler`/`VariantNestedWrapper`). It is now `public` with
the write half beside it, so the pair reads as one owner of the layout decision. Three shape decisions, each
pinned by a test rather than described (`VariantShreddingTests`, 7):
- **`TryShred` takes already-DECODED `VariantValue`s** + a separate SQL-null mask, NOT a `VariantArray`. A
  host arriving with an encoded form must parse each row to decide anything, so passing the parsed values
  keeps the decode at ONE per row; a `VariantArray`-only entry point would force it to encode a canonical
  array and have us decode it again. `TryShred(VariantArray, out …)` is the convenience overload for a
  caller that genuinely starts canonical (it uses `GetLogicalVariantValue`, so a shredded input re-shreds).
- **It returns `false` rather than an unshredded array** when no schema applies — building one would
  re-encode every row and discard bytes the caller already holds. Our transport then builds the unshredded
  array from its ORIGINAL blobs, exactly as before.
- **The null mask is a separate parameter** because SQL null-ness rides storage VALIDITY and is distinct
  from a variant JSON null in the value bytes; conflating them changes what `IS NULL` means. One test
  asserts both in a single column (masked row → `IsNull`, `VariantValue.Null` row → present value).

**A bonus simplification on the read side:** `VariantTransport.ToTransportBlobs` no longer hand-rolls a
per-row `GetLogicalVariantValue` + `VariantBuilder.Encode` loop for shredded input — it normalises through
`Reassemble` and falls into the SAME metadata‖value concat the unshredded path already used, so two branches
became one. The reassembly is followed by a **checked post-condition** (a surviving `typed_value` throws)
because the concat would otherwise read the RAW `value` child, which is EMPTY for every shredded row — the
silent-empty-variant trap `VariantShredding`'s own remarks warn about.

**⚠ THE TRAP OF THIS CHANGE — MOVING CODE BETWEEN NAMESPACES CAN SILENTLY REBIND A TYPE NAME.** The moved
`WithValidity` calls `ArrowArrayFactory.BuildArray`. In its old home that bound to
**`Apache.Arrow.ArrowArrayFactory`** (handles struct); inside `namespace EngineeredWood.Parquet.Data` the
same unqualified name binds to **that namespace's own internal `ArrowArrayFactory`** (`ArrowArrayBuilder.cs`),
which throws `NotSupportedException: Cannot construct Arrow array for type 'struct'` — a type in the
enclosing namespace beats an imported one. It COMPILED (same method name and signature shape) and failed only
at runtime, and only on the SQL-null path, so only the null-mask test caught it. Both directions are now
explicitly `Apache.Arrow.`-qualified with the reason on them, including the two surviving call sites in
`VariantTransport` — those bind correctly today only because the Parquet type is `internal` and
`DeltaLake.Table` is not in its `InternalsVisibleTo`, i.e. adding one later would silently rebind them.
**Habit: after moving code into a different namespace, grep the moved block for unqualified type names and
check whether the destination namespace declares any of them.** Same class as the marker rename — a
compile-clean change that degrades behaviour rather than failing.
**Gates:** EW Table.Tests **678** × {net10.0, net8.0, net472} and DeltaLake **217** — the SAME counts as
before the split, which is the signal that relocating the shredder is behaviour-neutral for EW;
`Parquet.Tests` +7 (`VariantShreddingTests`) with its `parquet-testing` corpus failures unchanged at **115**
(the nested corpus we deliberately do not init — an unchanged failure COUNT is what proves no regression
there, since the tier can never be green); fabricator variant **157** / native_write **147** and hermetic
**53/53 @ 4165**, all exact.

**We also ADDED one thing upstream will want:** a `StageRowDeletesAsync(FileRowSelection)` overload beside his
ordinal-keyed one, both feeding the now selection-keyed core. His public API and its 11 test call sites are
untouched; the composition of hunks 11–13 is the interesting part of this merge — we BOTH re-cored
`ComputeDeletionVectorActionsAsync`, he to report `DeleteDvEdit`s + touched paths for `DeltaTransaction`, we to
key it by path, and those compose (his internal core, our key).

#### EW BUMP 2026-07-28 (second, same day) — clast master `9669796`: the FIRST CLEAN merge in four bumps, and `_metadata` CONFORMS to upstream's shape

Spotted while cutting the shredding offer off a freshly fetched master, not by looking for it. `git merge-tree`
predicted clean and delivered: **`DeltaTable.cs` did NOT conflict — the first bump where it didn't.** All 7 patch
symbols present after; Table.Tests **696** × {net10.0, net8.0, net472} (was 678, +18 upstream tests) / DeltaLake
**217**. **Inherited and worth naming: `519f695` makes `ReadFileAsync` ASK for the materialized row-tracking
columns when the read PROJECTS** — and a partitioned read always projects, so ids were previously resolved off
the wrong values. That reaches our codec read-back paths (`ReadRowsByRowIdsAsync`, the buffered-UPDATE
pre-image), so it is a free correctness fix, same class as the `525bf94` comparator find.

**`_metadata` NOW CONFORMS TO HIS SHAPE (user's call, and the right one).** His `9669796` emits the identity
pair as **two FLAT columns whose NAMES are literally** `"_metadata.row_id"` / `"_metadata.row_commit_version"`
(`RowTrackingConfig.RowIdColumnName`; dots INSIDE the name, not struct nesting). Ours was a four-member STRUCT
carrying the same identity plus the locator — one namespace, two encodings. Now:
`ReadAllWithMetadataAsync` appends **`_metadata.file_path` + `_metadata.row_index`, flat, both non-null**, and
**no longer re-emits the identity pair** (his columns, his resolution — one concept, one owner; our copy was
duplicating a resolution we do not own). `UpdateBySelectionAsync(RecordBatch)` reads the two flat columns
instead of walking a struct; the Bridge's `ReKeyUpdatesOntoMetadata` builds them.
**The signal that conforming was right rather than deferential: `MetadataPredicate` ALREADY used the flat
dotted names** (`FilePathColumn = "_metadata.file_path"`), so the struct was the odd one out — and those
constants are now the single source of the names, in EW and in the Bridge.
**Nothing capability-level moved.** The locator is what the selection APIs consume: `UpdateBySelectionAsync`
reads `file_path` + `row_index` and ignores identity entirely (a stable row id cannot say which file/position to
DV-mask), and `FileRowSelection` is a `path → positions` dictionary with no Arrow shape at all.
**Framing to keep for the offer:** these two columns are the **UNPACKED, spec-named form of
`_ew_row_address`** — the same physical address, spelled as the file that HOLDS the row rather than as its
ordinal in a path-sorted set — which is exactly what lets a DML boundary VALIDATE what it was handed (an
inactive `add.path` is recognisably wrong; a stale ordinal is indistinguishable from a fresh one). Conforming
turns the offer from a COMPETING shape into an EXTENSION of his convention, which is why it is now much more
offerable than the struct was.
**Two test changes are improvements, not translations:** the helper reads the locator from our surface and
identity from his and **ASSERTS the two streams align row-for-row** (the only thing making a zip legitimate);
and `WithoutRowTracking_ShapeIsUnchanged_AndTheIdsAreNull` became
`WithoutRowTracking_TheLocatorStillWorks_AndTheIdentitySurfaceRefuses` — asserting his deliberate refusal beats
asserting all-null ids, because "this table does not track identity" is the truth all-null columns would
misstate. (His other adoptable refusal, on a user column colliding with a generated name, comes for free.)
**Gates, targeted-first because the Bridge's copy-on-write UPDATE is a LIVE consumer:** `update` **63** /
`dv_default` **58** / `native_write` **147** / `row_tracking_virtual` **299**, all exact; then hermetic
**53/53 @ 4165** and service **42/42 @ 1227**, both unchanged. **The service tier matters specifically here** —
`verify_mssql_s3_polybase` is protocol 1.0 ⇒ DVs off ⇒ UPDATE takes exactly the copy-on-write path whose input
was re-keyed, and it is service-tier-only.
**⚠ PROCESS TRAP RE-HIT: a no-match sqllogictest filter exits ZERO.** I ran
`verify_delta_catalog_row_tracking_virtual` for a suite actually named `verify_delta_row_tracking_virtual` and <!-- check-docs:ignore (the wrong name IS the subject here) -->
got a silent blank, which reads exactly like a pass. Already recorded under CI; re-recording because it bit
during a LIVE-path gate, where a false pass is worst. Always read the assertion COUNT, never the absence of a
failure.

**⭐ CURT WROTE US A MIGRATION GUIDE — `doc/upstream-landing-notes.md` is no longer the only doc to read.**
A PR #4 comment (2026-07-28 03:05) plus a NEW **`doc/embedding-host-guide.md`** walk through what changed on
OUR side, "in the order I'd tackle it": (1) the `_ew_row_address` rename + `TransientRowAddress`; (2)
`PlanFiles` instead of a public pruner; (3) **stage work on a `DeltaTransaction` instead of driving the commit
loop — "this is the big one"**; (4) the create-time/row-identity parameters; (5) the buffered rebase relocating
rather than aborting; (6) a row-id bug; (7) the variant transport. **Items 1–5 were all already done on our
side** when it arrived, and two of his open questions are now CLOSED: his offer to *"prioritize implementing"*
the stable row-tracking id is moot (he built it in `9669796`), and item 6's question — did we write tables with
DUPLICATE stable ids, because every staging call restarted its row-id reservation — is **no, twice over**: our
flush makes exactly ONE `StageDataFilesAsync` call per transaction (his bug needed two staged appends) and
everything before the move used a single `CommitDataFilesAsync`. Reply:
[PR #4 comment](https://github.com/clast-project/engineered-wood/pull/4#issuecomment-5105893565).
**His `>> 40` note was the one real actionable, and for us it is a correctness matter rather than style
(fixed, parent `b1fe4c4`):** we carried **FOUR** copies of the split — `DeltaCatalog`, `DeltaReader`,
`DeltaNativeReader`, `DeltaRowIdFilter` — one with a comment conceding it "MUST match engineered-wood's". It
must, because the codec read path renames EW's `_ew_row_address` to `RowIdColumn` and passes ITS packed value
straight through to DuckDB, so a moved `PositionBits` would silently mis-decode (wrong ordinal, wrong position,
no error) — the same compile-clean-but-wrong shape as the variant marker rename and the `ArrowArrayFactory`
namespace capture. All four now derive from `TransientRowAddress.PositionBits`, with decodes via
`.FileOrdinal`/`.Position`. **Use his helpers for anything touching HIS column; our own DuckDB-side packing
(`PendingOrdinalBase` and up) deliberately shares that space, so it follows the same split by construction.**
**Item 7 is where I had answered the WRONG question and corrected it:** his version is MEASURED — the write
direction already passes the codec seam untouched, and the read side can split blob → `(metadata, value)` →
struct-of-binary in our `IDataFileReader`, with the library returning a canonical `VariantArray` we convert on
our side. Accepted his Delta-typed `ComputeAddColumn`/`AddColumnAsync` overloads as the better ALTER fix (no
marker in his wire contract), and told him about the **DuckDB PR the user filed to fix VARIANT over the Arrow C
interface** before he spends the 30 lines — if `ArrowAppender::FinalizeChild` learns nested extension types the
canonical struct crosses the ABI and most of the transport question dissolves, our marker included.

**Historical (the note as first written, before the merge):** two commits landed past `fe74b0c`, both touching
**`DeltaTable.cs`** — the file that had conflicted in every single bump:
- **`9669796` overlaps `ReadAllWithMetadataAsync` on the IDENTITY half ONLY — and the overlap is in the two
  members our own consumer IGNORES, so the offer is in better shape than it first looks.** His new
  `ReadAllWithRowTrackingAsync`/`ReadAtVersionWithRowTrackingAsync` append **two FLAT top-level columns whose
  NAMES are literally the strings** `"_metadata.row_id"` / `"_metadata.row_commit_version"`
  (`RowTrackingConfig.RowIdColumnName`; dots INSIDE the column name, not struct nesting; materialized value
  else `baseRowId + position` / `defaultRowCommitVersion`, both nullable, appended AFTER the read pipeline so
  `ProcessFileBatchesAsync`'s reconciliation never sees them). Ours is **ONE STRUCT named `_metadata` with
  FOUR members** — the same two identity members PLUS the non-null LOCATOR pair (`file_path`, `row_index`).
  **Checked, because it decides the offer: the LOCATOR is what the selection APIs consume, and upstream emits
  it NOWHERE in any shape.** `UpdateBySelectionAsync(RecordBatch updates)` — the round trip — does
  `GetFieldIndex(MetadataColumnName)`, REQUIRES a struct, and reads only `file_path` + `row_index`, with the
  identity members explicitly ignored (`DeltaTable.cs` ~6056/6072-6091); the Bridge's `ReKeyUpdatesOntoMetadata`
  fills only those two for the same reason. Identity cannot substitute — a stable row id does not say which
  file and position to DV-mask. `FileRowSelection` itself is a `path → positions` dictionary with no Arrow
  shape at all, so it is untouched either way. ⇒ the additive part (locator, + the struct the round trip
  binds) is unopposed; the DISAGREEMENT is confined to the identity pair. **An argument for our shape worth
  making: Spark's `_metadata` genuinely IS a struct**, so a flat column named `_metadata.row_id` reads
  identically in SQL while being a different schema — ours is the Spark-faithful encoding, his the pragmatic
  one. Note he refuses a non-row-tracking table rather than serving all-null columns, and refuses a user
  column colliding with a generated name — both worth adopting regardless of which shape wins. He also names
  the methods for the FEATURE deliberately (`…WithRowTracking` = durable IDENTITY vs `…WithRowIds` =
  snapshot-scoped ADDRESS), the same distinction `72b3888` drew at the column level.
- **`519f695` "a partitioned table's read never asked for the materialized row ids"** — a real bug fix, and
  our partitioned row-tracking/MoR paths are the ones that would be exposed to it. Evaluate against our tree
  at the bump rather than assuming we are immune.

**How that resolved (so the historical block is not mistaken for guidance): we CONFORMED to his flat shape.**
The Spark-is-a-struct argument above is real but was outweighed — extending his convention makes the locator
offerable where a competing struct would not be, and `MetadataPredicate` had been using the flat dotted names
all along. See the bump subsection above for the as-built.

**What remains ours after this bump:** the `FileRowSelection` selection-DML (V9/V10), `ReadAllWithMetadataAsync`'s
**LOCATOR pair** (`_metadata.file_path` / `_metadata.row_index` — reshaped to his convention; the identity pair
is now HIS) + `MetadataPredicate`, `UpdateBySelectionViaVectorsAsync`, `VariantTransport` (decision settled —
KEPT, marker renamed), and `VariantShredding`'s write half in the parquet layer. All five verified still absent
upstream. **Offerability order changed on 2026-07-28:** the shredding split went out as draft PR #6 (Curt asked
for it by name, it touches no Delta concept, and it removes the `Apache.Arrow.Operations` reference he objected
to); the **locator is now second**, because conforming turned it from a competing shape into a two-column
extension of a convention he just established.

#### THE BUFFERED FLUSH IS ON EW's `DeltaTransaction` — our OCC loop is GONE (2026-07-28, EW + Bridge, no ABI)

`DeltaCatalog.FlushDmlTransactionAsync` no longer hand-rolls the commit loop: it stages onto a
`DeltaTransaction` and calls `CommitAsync()` ONCE. **234 → 187 lines, and `CheckLogicalRebaseAsync` /
`RebaseDvDmlActionsAsync` now have ZERO Bridge callers** — the conflict check, the rebase, the retry, the
row-level DV remap and the per-attempt idempotency guard are engineered-wood's to keep correct. That matters
because upstream named the hazard itself: those invariants (re-rebase from the ORIGINAL staged actions, never
from a prior attempt's; pass `rowLevelDml` or silently get file-granularity conflicts) are enforced by no type.

**Four EW additions were required, each a real gap for a host that owns its data plane, each mutation-tested:**
- **`StartTransaction(snapshot)`** — `StartTransaction()` pins `CurrentSnapshot`; our base is the version the
  transaction READ (statements earlier, and we cannot hold the table open across ABI calls). From the latest
  version the set of "commits landed since" is EMPTY, so the validation would be VACUOUS — not merely
  mis-pinned. The ctor already took a snapshot; this is the 4th instance of a pattern upstream established
  (`PlanFiles(snapshot:)`, `ComputeDeletionVectorActionsAsync(resolveAgainst:)`, `RebaseDvDmlActionsAsync(from:)`).
  Mutant (ignore the argument) fails 2 of 4 tests.
- **`StageAppTransaction(appId, version, expectedPrevious)`** — the idempotent-producer CAS must be
  re-validated on EVERY attempt, which `StageActions` + a hand-built `TransactionId` cannot do; its own doc
  rules itself out ("anything carrying snapshot-relative state belongs in a typed method"). The failure needs
  two producers running one batch: ours loses the race, the retry's read-set check PASSES (a concurrent append
  invalidates nothing we read), and the batch commits a SECOND time. Mutant (keep only the base pre-check,
  drop the per-attempt one) fails exactly the twin-producer test — i.e. the check a caller *could* write for
  itself is not the one that matters.
- **`StageDataFilesAsync` + `SetOperation`** — `StageDataFiles` was a REDUCED peer of `CommitDataFilesAsync`,
  missing `identityValuesPreGenerated` (so an identity table's appends could not be staged AT ALL),
  `deletedPositionsByFileIndex` (the same-txn INSERT-then-DELETE inline DV, slice C3), and the operation name
  (his inference yields `"WRITE"` for anything mixed; our suites pin `"TRANSACTION"`/`"UPDATE"`/`"ADD COLUMNS"`).
  Kept ADDITIVE: his sync `StageDataFiles` is untouched, the new one is a sibling — async for the same reason
  `StageRowDeletesAsync` is, because hiding rows means WRITING a vector, and that write is hoisted into
  `BuildInlineDeletionVectorsAsync` so the action builder stays sync.
- **`StageReadPredicate` / `StageWholeTableRead` + the isolation gate** — see below; the only place we changed
  his semantics rather than extending them.

**⚠ THE SEMANTIC FIND: row-level reconciliation ignored the isolation level, in BOTH directions.** Upstream's
loop derives `rowLevel` purely from "are there DV edits", so (a) under **`serializable`** two deletes touching
one file reconciled because their rows were disjoint — admitting exactly the interleaving that level exists to
forbid, since there commit order IS the logical order; and (b) under **`write_serializable`** the read-check
exemption was NARROWER than the level's definition — only the RECONCILED paths were exempt, so a file we
merely READ and never touched could be compacted away by a concurrent writer and abort us, though no row we
delete was invalidated. Ours had always been the full skip (reads don't serialize under WS — Databricks' WS
matrix). Both halves now hang off ONE gate: `rowLevel` is true only under write_serializable. **Our suite
caught both** (`row_level_concurrency:162` "Query unexpectedly succeeded" = (a); `transactions:537`
concurrentDeleteRead = (b)) — the value of pinning both isolation levels rather than just the default.

**Process notes worth keeping.** (1) A first attempt reworded EW's row-level conflict message to match ours
and broke FOUR upstream tests that pin the substring `"row level"`; reverted, and OUR three `.test` pins moved
to upstream's wording instead — aligning the two phrasings upstream uses for one condition
(`RebaseDvDmlActionsAsync` says "row-level conflict on file", `ResolveRowLevelDeletesAsync` says "conflicts at
row level") is something to SUGGEST, not impose. (2) **`immediate_transaction_mode` does NOT help the pin
question** (asked + analysed): it controls when DuckDB starts ITS transaction, while our pin is per TABLE at
first scan — at `BEGIN` we do not yet know which tables a transaction will touch, and resolving a version for
every table in the catalog is the enumeration cost we avoid. (3) **Holding the `DeltaTable` open across ABI
calls would be a real perf win** but the blocker is NOT statefulness: `DuckDbTableFileSystem` CAPTURES the
per-call opener (`ClientContext*`), and the rollback path gets no opener at all — so the prerequisite is making
the FS resolve `AmbientOpener.Current` lazily per call. Not needed for CORRECTNESS since the pinned-snapshot
overload, but **the cost is bigger than "N× per multi-statement transaction" — it is per TABLE REFERENCE per
STATEMENT, and MEASURED (2026-07-29):**

| statement (local codec catalog, `native_read` off = the default) | snapshot constructions |
|---|---|
| `SELECT sum(id) FROM t` (steady state) | **4** |
| `SELECT … FROM t a JOIN t b` (self-join) | **8** |
| three references to `t` in one statement | **12** |
| `INSERT INTO t SELECT … FROM t a JOIN t b` (autocommit) | **10** (8 scan + 2 write) |
| the same INSERT inside `BEGIN … COMMIT` | **13** |
| any of the above with `native_read true` | **+1** (the shared version pin) |

Dead linear at **4 per reference**, decomposed and confirmed as **2 `ScanTable` calls per reference** (the
bind-time `spec == null` schema probe + the execution) **× 2 opens per call** (`GetSchema`/`GetSchemaAt`, then
`Stream`/`StreamAt` on the codec path or the file listing on the native one). Method: every
`DeltaTable.OpenAsync` is preceded by `TableFileSystems.Create`, so ONE temporary debug line there counts opens
exactly; a second Delta table scanned between the statements under test delimits them in the log (its own opens
show up under its own path). Probe reverted after measuring. Each open costs a `_delta_log` LIST — which
`ExternalFileCache` does NOT serve (it caches file CONTENT ranges, not listings) — plus the commit/checkpoint
reads and the replay CPU, so on OneLake/S3 the repeated listing dominates. **Only the resolved VERSION is
shared** (`SnapshotPinning` caches a `long` per (txn, table), never a `DeltaTable`).

**A CONSISTENCY GAP found by the same measurement — FIXED the same day (2026-07-29, C#-only, no ABI); the
diagnosis is kept because it explains the shape.** Fix: the codec path's schema fetch already opens the table
at latest, so `DeltaReader.GetSchemaAndVersion` (new; mirrors the existing `GetSchemaAndRowTracking`
same-open pattern) reports the version it saw and `ScanCodec` seeds `SnapshotPinning` with it — **zero extra
IO**, deliberately NOT via `ResolveVersionAsOf`, which would open the log again. `ScanTable`'s pin READ lost
its `IsExplicit` gate so later references consume the seed; reading the pin BEFORE the schema fetch is
load-bearing (it makes schema and data come from one version, which seeding alone would not guarantee if a
concurrent ALTER landed). The explicit-transaction pin is instant-resolved and untouched — `PinVersion`'s
`GetOrAdd` never overwrites it. **`verify_delta_autocommit_pin` (34)**: the seeding branch runs at most ONCE
per (txn, table), so the "delta codec pin" log-line COUNT *is* the sharing property — exactly one however
many times the statement names the table (self-join, three-way, `UNION ALL`, correlated re-scan,
`INSERT INTO t … FROM t JOIN t`), plus a native_read catalog emitting none. **MUTATION-TESTED**: restoring
the `IsExplicit` gate makes a SINGLE-reference statement log **2** (bind probe + execution each seeding
independently) and the suite fails — so the assertion distinguishes all three states (0 = never pinned,
2-per-reference = seeded but not shared, 1 = correct). Deliberately NOT attempted: an end-to-end racer for
the interleaving itself — a concurrent commit cannot be scheduled BETWEEN two scans of one statement from
sqllogictest, so it would be flaky rather than load-bearing; that reading AT a pinned version yields that
version's data is already covered by the explicit-txn snapshot sections. **The diagnosis, for the record:**
The version pin was consulted
on the codec read path ONLY when `_txnBuffer.IsExplicit(scanTxn)` (`DeltaCatalog.cs` ~1171), so in AUTOCOMMIT
`pinnedReadValue` is null and **each scan opens at LATEST independently** — the +1 pin open appears in the
table above only for the `BEGIN` row, which is the empirical proof. Two references to one table can therefore
straddle a concurrent commit (reference `a` reads v5, `b` v6): a self-join, `t UNION ALL t`, a correlated
subquery re-scanning `t`, or `INSERT INTO t … FROM t JOIN t`. `native_read` is UNAFFECTED — it pins whenever
`txn != 0` (~1434), autocommit included. The in-code comment "autocommit statements keep reading latest (a
single codec statement is one snapshot anyway)" is true for ONE reference and false for two. **The gate is
INHERITED, not decided — and it must STAY for DML, for a CAPABILITY reason, so do not "simplify" it away:**
`MarkExplicit` (`7cf1f8a`, ABI v60) exists because the buffered DML path is strictly LESS CAPABLE than the
direct per-statement one — it expresses a DELETE as buffered DV actions and CANNOT express a COPY-ON-WRITE
rewrite, so a `deletion_vectors false` table (the protocol-1.0 PolyBase/SQL-Server-readable recipe) can only
be deleted from directly; `EnsureBufferedDmlEligible`'s first guard still says exactly that ("run it in
autocommit (copy-on-write)"). CDF capture was the SECOND such capability at the time (slice C2 later taught
the buffered path eager `_change_data` files, so the residual CDF guard now covers only identity/IcebergCompat).
Routing autocommit through the buffer would therefore have BROKEN working capabilities — it was never a cost
optimization (in autocommit one statement is one commit either way, so there is nothing to win). **That
reasoning is entirely about WRITES.** The codec READ pin was later written INSIDE that pre-existing
explicit-only block (`20ec7d5`, "snapshot reads by default") and none of it applies to a read: the block is
correctly explicit-only because read predicates feed a COMMIT-time conflict check, but the PIN rode along. ⚠ Note what
this means for any future "simplification": **dropping the `IsExplicit` gate from the DML side would be a
capability regression** (autocommit DELETE on a `deletion_vectors false` table would lose copy-on-write), which
is why the fix seeds the pin instead of touching that gate — only the pin's READ was ungated.

**Still hand-rolled, by upstream's own constraint** ("append-shaped only: the overwrite family removes the
active set, which is exactly what a rebase cannot re-derive"): `FlushCreateTransaction`, CREATE OR REPLACE,
partition overwrite, and the pending-CREATE path (no table exists yet to open a transaction on).

**Gates:** EW Table.Tests **678** × {net10.0, net8.0, net472} (was 656 after the bump); targeted-first
`transactions` **941** / `txn_version` **51** / `row_level_concurrency` **70** — all three at their exact
historical counts — then hermetic **53/53 @ 4152** and service **42/42 @ 1227**, both unchanged. For a rewrite
of the buffered-DML commit path, unchanged counts across all five are the signal.

#### `TransientRowAddress` does NOT retire the selection APIs — and here is the test that decides it

Asked directly (2026-07-28) whether upstream's new address type makes our path-keyed `*BySelection*` surface
redundant. It does not, but **the argument changed**: we neither eliminated ordinals nor should want to —
`TransientRowAddress` makes them a documented first-class concept, and DuckDB's `rowid` genuinely IS one
BIGINT, so a host must mint and decode a packed address regardless. What survives is narrower:

> An ordinal is a fine **address** for a host to mint and decode. It is the wrong **key** for a library's DML
> boundary to accept, because at that boundary a stale address is indistinguishable from a fresh one.

Neither motivating defect is touched by the new type: the lossy round-trip is still there (the type names the
integer in the middle), and so is the silent skip. **The cheap alternative — keep ordinals, make them strict —
was evaluated and does NOT work**, which is now pinned rather than argued: our existing coverage only had the
OUT-OF-RANGE case ("deletes nothing"), which a range check would catch. The new
`OrdinalKeyed_AfterAConcurrentRemoveRenumbersTheSet_DeletesTheWRONGRow` does the case it cannot — a concurrent
commit REMOVING an earlier file renumbers the path-sorted set, so a captured ordinal still EXISTS (nothing can
reject it) and names a DIFFERENT file: the delete succeeds and removes **a row nobody selected**. Silent WRONG
DATA, strictly worse than a silent no-op. (Construction gotcha: the intended ordinal must be **1**, not 2 —
removing ordinal 0 shifts old ordinal 2 INTO slot 1, so aiming at 2 goes out of range while aiming at 1 hits
the wrong file. The first draft got this backwards and the test failed, correctly.)
Upstream's own doc supplies the argument: the address "says WHERE a row sits rather than promising an identity
it cannot keep", and a concurrent append renumbers ordinals.
**Shape to keep:** selection forms are the CORES, rowid forms are thin adapters retaining the historical
leniency (upstream's callers + its 11 test call sites). `ReadRowsByRowIdsAsync` stays rowid-keyed and SHOULD —
it carries per-ROW identity across the boundary (`rowIdsOut` beside `sourceRowTrackingOut`), so path-keying its
input alone removes nothing; that is the `_metadata` question, not the selection one.
**Upstream pitch, revised:** not "replace ordinals" (he just invested in the type) but "the address type is
right; the DML boundary needs a key that fails loudly — here is a test where the ordinal form deletes the wrong
row and neither a range check nor the new type can see it."

**UPSTREAM: this is the STRONGEST candidate of the whole seam effort** — a capability EW genuinely lacks, proven
in production against Spark, additive, no magic strings (unlike the predicate lowering). Offer it ahead of the
rest. **Process lesson recorded twice in one day:** a callback-carrying overload added to satisfy a caller gets
tested "by proxy" through that caller and its OWN contract (row-alignment, absoluteness, granularity) stays
unpinned — which is exactly what silently breaks a keyed consumer while every existing assertion stays green.
Both times the gap was found by review, not by the suite.


---

## isBlindAppend — an UPSTREAM OFFER and an OPEN FINDING (2026-08-01)

Written down before the context that produced it is lost. Two separable items: a fix that is ready to
offer, and a defect that is measured but not yet built.

### 1. UPSTREAM OFFER (ready): `CheckpointReader` must tolerate an unreadable `_last_checkpoint`

Committed on `fabricator-patches` as `14a74a9`. **Engine-agnostic, spec-conformant, and not
fabricator-specific — this is a bug in EW for every user, so it should go upstream as-is.**

`_last_checkpoint` is an optimization HINT: it only saves the reader from listing `_delta_log` to find
the newest checkpoint, and the Delta protocol requires readers to fall back to that listing when it is
absent **or unusable**. `ReadLastCheckpointAsync` handled only "absent", so anything else propagated to
the caller — and the caller is frequently a COMMIT.

Reachable because the file is updated by non-atomic OVERWRITE (`WriteAllBytesAsync` →
`UploadAsync(overwrite: true)`), so a concurrent reader can observe it mid-change:

| observed | old behaviour |
|---|---|
| zero bytes | `JsonDocument.Parse` → *"The input does not contain any JSON tokens"* |
| truncated | `JsonException` |
| valid JSON missing `version`/`size` | `KeyNotFoundException` |
| the READ itself fails (ADLS 412 on a torn ranged read) | raw Azure exception |

All four now mean "no hint". MEASURED on Fabric OneLake: 8 concurrent writers × 12 commits (checkpoint
interval 10) killed 2 of 8, then 1 of 8; a 10 × 15 run reproduced the torn-read variant in 2 of 10.
A single-writer run can never reach any of it. Gate:
`test/verify_delta_last_checkpoint.test` (39, hermetic, mutation-tested) — it writes each corrupt state
directly rather than depending on a race that only sometimes collides.

The host-side half (read whole small files in ONE request instead of Azure's lazy ETag-pinned ranged
stream) lives in the Bridge and is NOT part of the offer.

**OFFERED 2026-08-01 — branch `offer/last-checkpoint` on the `cmettler/engineered-wood` fork**, one
commit on top of `upstream/master` (`e314af5`). `CheckpointReader.cs` had not been touched upstream since
our merge-base, so the cherry-pick was clean. Opening the PR is a manual step (`gh` is not installed on
this box): https://github.com/cmettler/engineered-wood/pull/new/offer/last-checkpoint

**Writing the offer changed the fix, which is the argument for writing offers properly.** Our evidence
was a fabricator sqllogictest upstream cannot run, so the offer needed an EW-level suite
(`LastCheckpointToleranceTests`, 11 cases). Two things fell out of writing it:

- **A FIFTH unhandled shape, and a real second bug.** Valid JSON that is not an OBJECT (`[1,2,3]`) still
  threw: the guard read the required fields off the root assuming it was one, and `TryGetProperty` does
  not return `false` for a non-object — it throws `InvalidOperationException` ("requires an element of
  type 'Object'"). So the hint could still fail a caller through the very guard meant to stop that.
  Not a shape an interrupted overwrite plausibly leaves behind (the file is always written as an object),
  which is exactly why only a completeness-driven suite would find it — ours tests plausible shapes.
  Fixed with a `ValueKind` check, ported back to `fabricator-patches`, and pinned in our suite as §6.
- **An over-claim in the original commit message, corrected.** Per-guard mutation testing shows
  `data.Length == 0` kills NO test: `Parse("")` already raises `JsonException`, so the catch covers empty
  too. The check stays as a deliberate fast path and is now documented as that rather than as
  load-bearing. The other four guards each kill at least one test.

**METHOD NOTE — the mutation run was VOID TWICE before it measured anything, both times looking clean.**
First, CRLF: the `perl -0` patterns used `\n`, matched nothing, and reported all five mutants "passing",
i.e. five runs of the unmodified baseline. Second, a mutant that did not COMPILE — with `--no-build` the
test command happily re-ran the previous binary and again reported green. Both are the standing
"a negative result is not a measurement" rule wearing new clothes, and both are cheap to prevent: assert
the file actually changed (`cmp`), and assert the build SUCCEEDED, before believing any result.

### 2. OPEN FINDING (measured, NOT built): blind-append classification is wrong in BOTH directions

Delta defines a blind append as *the transaction read nothing*
(`readPredicates.isEmpty && readFiles.isEmpty`) and the WRITER declares it in
`commitInfo.isBlindAppend`. EW does neither half the way the spec expects:

| half | what EW does | direction of the error |
|---|---|---|
| **writing** | never emits `commitInfo.isBlindAppend` | **too strict** — other engines treat our appends as changed-data and check them under EVERY isolation level. **STILL OPEN**; cleared to build, see §4a |
| **reading** | `ConflictChecker.IsBlindAppend(actions)` INFERS it from "only AddFiles, no removes/metadata/protocol" | **too lenient** — an `INSERT … SELECT` from the same table produces only adds but DID read, so we may skip a check we owe. **FIXED 2026-08-01**, see below |

The writing half is measured end to end against Fabric Spark 4.1.1.5.5 / Delta-Lake 4.2.0 — a live A/B
where Spark's `DELETE` aborted with `DELTA_CONCURRENT_APPEND` against our concurrent append under
**both** `Serializable` and `WriteSerializable`, overlap proven each time by Spark naming our commit
version. Delta's own code is why:

```scala
val blindAppendAddedFiles = if (commitInfo.flatMap(_.isBlindAppend).getOrElse(false)) addedFiles else Seq()
```

Full record + method notes: [delta-transactions.md](delta-transactions.md) §10.6.

**Is a truthful emission possible?** Yes, if the writer knows whether the transaction read — and the
buffered transaction does, since it already tracks a read set for its own OCC check. The rule that keeps
it safe: emit `true` ONLY when no read is provable, otherwise OMIT. A wrong `true` makes other engines
SKIP a check (unsafe); a missing flag only costs spurious aborts (safe, and is today's behaviour). So
the change is monotone — it can only remove false conflicts, never create silent ones.

**The reading half is BUILT (2026-08-01, EW `fabricator-patches`):** `ConflictChecker.IsBlindAppend` now
**consumes `commitInfo.isBlindAppend` when present** and falls back to the action-shape inference —
renamed `InferBlindAppend`, unchanged — only when it is absent (legacy commits, or writers that omit it,
which still includes us until the write half lands). A non-boolean value counts as absent: a declaration
we cannot read is worth no more than one that was never made.

Three things that pass for detail and are not:

- **The declaration outranks the inference in BOTH directions**, and each direction has its own test. The
  unsafe one is the point (`isBlindAppend: false` on an adds-only commit — the `INSERT … SELECT` shape —
  must now be examined, where inference alone called it blind and skipped the check). The permissive one
  matters too: a commit containing a remove but declaring `true` stays exempt, because the writer knows
  what it read and we do not.
- **Absent must keep meaning "infer", not "not blind".** Almost every commit in the wild omits the flag,
  so defaulting a missing flag to `false` would start conflicting on ordinary concurrent appends — a
  behaviour change dressed as a correctness fix. Pinned by its own test.
- **The declaration has to SURVIVE the log round trip or the whole change is dead code.** The verdict
  tests hand `ConflictChecker` a `CommitInfo` directly, which proves the decision and nothing about
  reachability; the checker's real input comes from `TransactionLog.ReadCommitAsync`. So a round-trip
  test writes a commit carrying the flag, reads it back, and asserts it is readable exactly the way the
  checker reads it. It is — `CommitInfo.Values` keeps arbitrary keys as `JsonElement`, so no model change
  was needed.

Mutation-tested: replacing the consumption with the bare inference kills exactly the two direction tests.
EW `ConflictCheckerTests` 16, DeltaLake.Table.Tests 727 on net472 + net8.0.

### 3. Upstream state at the time of writing

`upstream/master` is **8 commits ahead** — `#6`, `#16`–`#22`, i.e. exactly the pending bump already
recorded above (variant shredding + the five #15 slices). **None of them touch isolation, conflict
checking, or blind-append handling**, so neither item here is at risk of colliding with that bump, and
neither should wait for it. The two isolation-related upstream commits (`ffb89e5`,
`1ba52a4` "row-level reconciliation ignored the isolation level") are **already in `fabricator-patches`**.

To be explicit, because it was asked: **EW has NOT lost WriteSerializable support.**
`StartTransaction`'s default is still `IsolationLevel.WriteSerializable`, and `ConflictChecker` still
implements the relaxation (`examineAdds = isolation == Serializable || !concurrentIsBlindAppend`). What
is missing is the interop plumbing in item 2, not the semantics.

### 4. Upstream PR #24 — TEST-ONLY (does not break us), but it changes the plan's risk

`test(delta): "what does Spark do at WriteSerializable" has no answer — the level is not in OSS Delta`,
merged upstream 2026-07-31. Adds `ConflictSemanticsInteropTests`; **no public API change, so the pin
bump is unaffected and nothing of ours breaks.**

Upstream independently reached our DDL finding (`requirement failed: delta.isolationLevel must be
Serializable`) and concluded the question is unanswerable for OSS Delta. **Our measurement goes
further and contradicts that framing** — the level cannot be SET from Spark, but a table can still
CARRY it if another engine stamps it (we do), and Fabric Spark then reads it, commits at it, and
records it in its own `commitInfo`. So the question is answerable, and §10.6 of
[delta-transactions.md](delta-transactions.md) answers it. That is worth offering upstream alongside
the `_last_checkpoint` fix.

**⚠ The caveat that must be checked BEFORE building the isBlindAppend write half.** Upstream's tests
report that *a whole-table read declaration conflicts even with a blind append that removes nothing*.
If that holds in Delta 4.2.0, then a Spark statement whose predicate is non-prunable (exactly our
`DELETE … WHERE id % 7 = 3`, and the common case) declares a whole-table read and will abort against a
concurrent append **whether or not it is marked blind** — i.e. emitting `isBlindAppend` would NOT stop
the aborts we measured, because the exemption is dodged by a different check
(`checkForDeletedFilesAgainstCurrentTxnReadFiles` / the read-declaration path) rather than
`checkForAddedFilesThatShouldHaveBeenReadByCurrentTxn`.

This is READ FROM A SUMMARY OF UPSTREAM'S TESTS, NOT VERIFIED — do not act on it either way. The cheap
check is to re-run `sparkprobe conflict WriteSerializable` with a PRUNABLE predicate (e.g.
`WHERE id = <literal>`, which declares a narrow read) and see whether Spark still aborts against our
append. If a narrow read commits and a whole-table read aborts, the exemption is real and the write
half is worth building; if both abort, the write half buys nothing against Spark and only the READING
half (§2, the unsafe direction) is worth doing.

### 4a. THE CAVEAT IS RESOLVED — it does NOT apply at WriteSerializable, so the write half PAYS (2026-08-01)

**Settled by reading Delta's source at the `v4.2.0` tag** (the Fabric build) rather than by a live run,
because the source names which branch executes and the live A/B had already measured the
flag-absent case. `ConflictChecker.scala`, verbatim:

```scala
val blindAppendAddedFiles: Seq[AddFile]   = if (isBlindAppendOption.getOrElse(false)) addedFiles else Seq()
val changedDataAddedFiles: Seq[AddFile]   = if (isBlindAppendOption.getOrElse(false)) Seq() else addedFiles

val addedFilesToCheckForConflicts = isolationLevel match {
  case WriteSerializable if !currentTransactionInfo.metadataChanged =>
    winningCommitSummary.changedDataAddedFiles
  case Serializable | WriteSerializable =>
    winningCommitSummary.changedDataAddedFiles ++ winningCommitSummary.blindAppendAddedFiles
  case SnapshotIsolation => Seq.empty
}
val fileMatchingPartitionReadPredicates =
  getFirstFileMatchingPartitionPredicates(addedFilesToCheckForConflicts)
```

**The predicate check never runs on a declared blind append at WriteSerializable.** The list handed to
`getFirstFileMatchingPartitionPredicates` is EMPTY, so how broad the reader's declaration was — narrow
predicate, whole-table, anything — cannot matter. The caveat imagined the exemption being dodged by a
different check; it is not dodged, it is applied one step EARLIER than the predicate comparison.
**⇒ the prunable-predicate experiment (item 3 of the old order) is MOOT and was not run: predicates are
not consulted on that path at all.**

What this predicts for the two levels we measured, and it matches both observations exactly:

| table's level | our append today (no flag) | our append WITH a truthful `isBlindAppend: true` |
|---|---|---|
| `WriteSerializable` | `getOrElse(false)` ⇒ our adds land in `changedDataAddedFiles` ⇒ examined ⇒ **Spark ABORTS** (measured) | `changedDataAddedFiles` empty ⇒ nothing examined ⇒ **Spark COMMITS** |
| `Serializable` | examined ⇒ **Spark ABORTS** (measured) | blind appends are examined too, BY DESIGN ⇒ **still aborts** |

So the Serializable abort is CORRECT and unfixable by any flag — that is what the level means — while
the WriteSerializable abort is ours to fix and is caused precisely by the missing flag. The write half
pays, for `WriteSerializable` tables specifically, and the honest scope claim is that narrow one rather
than "it fixes the interop aborts".

Note this also means the write half's value is gated on a table DECLARING `WriteSerializable` — which
Fabric Spark's DDL validator refuses to set (§10.6 of [delta-transactions.md](delta-transactions.md)),
so in practice such a table is one WE stamped. Spark honours a stamped value; it just cannot write one.

**Still to MEASURE after building it:** that Spark actually commits in the A/B once we emit the flag.
The source says it must; the standing rule here is that a source-derived prediction is a prediction.

**⚠ THE CRUX FOR WHOEVER BUILDS IT: the truth lives in the HOST, not in EW — so deriving the flag from
EW's transaction state alone would emit a LIE, in the unsafe direction.** `DeltaTransaction` does track
reads (`_readPredicates`, `_readWholeTable` via `StageWholeTableRead`, plus staged DELETE edits), which
makes `!_readWholeTable && _readPredicates.Count == 0 && no removes staged` look like a ready-made
blind-append test. It is not, and the field comments say why: those collections are *"left empty by the
functional-predicate and append-only paths"*, and `StageWholeTableRead` is something **the host calls**.
An `INSERT INTO t SELECT … FROM t` is executed by DuckDB — DuckDB reads the target and hands EW rows to
append — so EW sees a transaction with no staged read and would conclude "blind" for the one shape the
whole exercise is about. Emitting `true` there tells every other engine to SKIP a check it owes.

So the write half is NOT a derivation inside EW; it needs the host to positively assert that the
statement read nothing, and the safe default when nobody asserts is to OMIT the field (today's
behaviour, which only costs spurious aborts). Concretely: an explicit declaration on the transaction
(the `StageWholeTableRead` shape, inverted) that the Bridge sets from what it knows about the DuckDB
plan, with EW's own read tracking as a veto rather than as the source of truth — a declaration can only
be DOWNGRADED to "not blind" by staged reads, never upgraded to "blind" by their absence.

**Recommended order, updated again:** (1) offer the `_last_checkpoint` fix upstream — DONE, branch
`offer/last-checkpoint` on the fork; (2) fix the READING half — DONE, and it found that the writer's
declaration must outrank our inference in BOTH directions; (3) the prunable-predicate check — MOOT, see
above; (4) build the WRITING half — cleared to proceed, scoped as in the table above, with the live A/B
re-run as its gate.
