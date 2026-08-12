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

### FIRST ATTEMPT — started 2026-08-01, merge ABORTED deliberately; five findings that change the plan

Branches exist and are the designated place to resume: **`bump/upstream-2026-08`** (EW, off
`fabricator-patches` at `cbe5bb6`) and **`bump/ew-upstream-2026-08`** (parent, off `v1.5-variegata`).
Both are EMPTY of commits — the merge was aborted once its true size was measured, because a
half-resolved merge sitting in a submodule across a session boundary is the state most likely to be
lost or half-repeated. Re-running `git merge upstream/master` reproduces the identical 4 conflicts;
what was expensive was the DECISIONS below, not the mechanics.

**1. The measurement UNDERCOUNTED: there is a second migration, roughly as large as the 24 call sites.**
Upstream's `RowSelection` (slice 2, #18) is the successor to our `FileRowSelection`, and
`FileRowSelection` is **GONE upstream** (`git grep` on `upstream/master` returns nothing). Ours is used
at **38 sites in EW `src/`, 5 in EW tests, and 5 in the Bridge** — none of which the original table
lists, because that table only counted removed `DeltaTable` METHODS. Budget for ~48 more edit sites.
Upstream's type is a strict superset in capability (`ByPath` / `FromLocatorColumns` /
`FromRowAddresses` / `Paths` / `PositionsFor`), so this is a rename-and-widen, not a redesign.

**2. Upstream EXPLICITLY DECLINED our row-level read exemption, and said what it would take.** This is
the one place where taking upstream wholesale silently REVERTS shipped behaviour of ours, so it is the
hunk to be most careful with. `DeclareWholeTableRead`'s own remark upstream reads: *"It is NOT
implemented here: today the declaration is honoured at both levels … if it is ever adopted it should be
an explicit per-transaction opt-in rather than an inference — a library must not claim on a host's
behalf that it read less than it declared."* Our branch DOES implement it, as an inference, in the
commit loop (`effectiveReads = rowLevel && isolation != Serializable ? reads with { WholeTable = false }
: reads`).

**⚠ CORRECTION (measured 2026-08-01): the claim that `verify_delta_row_level_concurrency` depended on it
was FALSE, and the suite has now been given the section that makes it true.** Mutation testing showed
every one of §1–§10 passing with the exemption REMOVED *and* with it INVERTED (`WholeTable = true`
forced) — with an env-gated tripwire throw proving the line is REACHED, so this was a real null result
and not a skipped code path. Two structural reasons, both properties of the suite rather than of the
feature: every scenario used a PUSHABLE predicate (`WHERE id = 2`), so the scan declares a PREDICATE and
never sets `ReadWholeTable`, and the exemption only ever drops `WholeTable`; and the fixture is ONE
file, so a racer can only touch a file the transaction also touched — which row-level resolution has
already put in `resolvedPaths`, and the checker skips those BEFORE consulting reads at all. **That skip,
not this exemption, is what makes §1–§9 compose** — a distinction worth keeping straight, because it is
the one that makes concurrent disjoint-row DML work.

**The exemption IS load-bearing, for a narrower case than advertised.** New §11 supplies what was
missing — a table with THREE files, a NON-pushable predicate (`id % 100 = 2` ⇒ `StageWholeTableRead`),
and a racer landing a `dataChange=true` remove on a DIFFERENT file (⇒ not in `resolvedPaths`). With the
exemption the buffered COMMIT lands; with `effectiveReads = reads` it fails with a conflict, and the
mutant fails at exactly that COMMIT and nowhere else. Suite 82 → 93.

So the decision above stands, but now on evidence rather than on an assumed gate — and the offer is much
stronger for it, since upstream's stated objection is partly that this is an inference *"a host cannot
observe from the outside"*. §11 is precisely an outside observation of it.

**DECISION, revised — adopt the OPT-IN DURING the merge rather than preserving the inference.** The
first call here was "keep our behaviour, redesign later", on entanglement grounds. That was too
cautious once the size was checked: `rowLevel` is only `rowLevelDeletes is { Count: > 0 }`, so the
opt-in is a field + a public property on `DeltaTransaction`, ONE condition in the commit loop
(the inference becomes the PRECONDITION rather than the trigger:
`_exemptRowLevel && rowLevel && isolation != Serializable`), and the Bridge setting it at the one flush
site that gets the behaviour implicitly today.

Three reasons it beats preserving the inference:

- **It touches the most contested file ONCE.** `DeltaTable.cs` is where upstream wrote +1325 lines
  against our +1165; resolving the hunk and then re-opening it for a redesign pays that cost twice.
- **It converts a behavioural MODIFICATION into an ADDITIVE patch.** That is the difference that
  matters for every future bump — and it removes this exact hazard, because a modification of
  upstream's own lines is what makes "take upstream wholesale" compile, pass most suites, and silently
  revert us. That nearly happened on the first attempt.
- **It is what upstream ASKED FOR**, so it is offerable, and an absorbed patch is one we stop carrying.

**The OFFER still has to wait for the bump to land**, per generate-never-maintain: an offer is cut off
`upstream/master`, and writing it against the pre-bump API means writing against methods upstream has
already replaced — the same mistake flagged for the isBlindAppend write half. Order: bump → opt-in
during resolution → offer cut from the result.

**Fallback, kept deliberately:** if upstream's restructured commit loop does not leave `effectiveReads`
in a shape where the gate drops in cleanly, preserve the inference exactly as today and correct
upstream's `DeclareWholeTableRead` remark on our branch so it describes what we actually do. Decide by
reading the merged loop, not in advance.

**3. Hunk-wise resolution is REQUIRED, not a matter of taste.** A plain `git checkout --theirs` on
`DeltaTransaction.cs` looks equivalent and is not: it discards our changes that auto-merged OUTSIDE the
conflict regions. Verified by resolving hunk-wise, then diffing against the `--theirs` content — they
differ, and the difference was real code of ours. Resolve with:
`awk '/^<<<<<<< HEAD/{s=1;next} /^=======$/{if(s==1){s=2;next}} /^>>>>>>> upstream\/master/{s=0;next} s!=1{print}'`

**4. Dropping the shredding patch is CONFIRMED safe, by measurement rather than by the commit subject.**
Upstream did not delete the file, it MOVED it (`Parquet/Data/VariantShredding.cs` →
`Parquet/VariantShredding.cs`, namespace `EngineeredWood.Parquet.Data` → `EngineeredWood.Parquet`) and
its surface is a superset of ours (adds `InferSchema`/`Shred`). For the add/add test conflict, upstream
has **11** tests to our **7** and **every one of our test names is present upstream** (`comm -23` on the
method names returns empty) — so `--theirs` loses no coverage. Our `VariantTransport.cs` calls
`VariantShredding.TryShred`/`.Reassemble` and will need the namespace updated.

**5. `StageActions`' extra `operation` parameter is ours and UNUSED** — the Bridge's single call site
(`DeltaCatalog.cs:3459`) passes one argument — so upstream's narrower `StageActions(actions)` can be
taken without a shim.

Everything else matched the measurement exactly: 8 commits, 4 conflicts, 30 hunks in `DeltaTable.cs`,
6 in `DeltaTransaction.cs`, and the hunks are the predicted overload consolidation
(`Delete*`/`Update*`/`Read*` families → `DeleteRowsAsync`/`UpdateRowsAsync`/`ReadCoreAsync`).

### SECOND ATTEMPT (2026-08-01) — merged, EW SRC BUILDS; the exact remaining mapping

Branch `bump/upstream-2026-08`: `50839f0` (merge + conflicts resolved) → `f496526` (src builds on
net10.0/net8.0/net472) → `9d616c9` (`ReadAllWithMetadataAsync` retired). **`fabricator-patches` is
untouched and nothing is pushed.** What is left is the EW TEST project, then the Bridge.

**FIVE auto-merge grafts were found in total, and every one differed from upstream by ONE LINE** — git
welded our method's body into upstream's method of the same name, so they read as correct and compile
or nearly compile. `StageRowDeletesAsync`, `BuildStagedAppendActionsAsync`, `DeleteRowsViaVectorsAsync`,
`ComputeDvActionsWithEditsAsync`, `ComputeDeletionVectorActionsAsync`. Each was replaced with upstream's
version WHOLESALE and diffed to byte-identity. **Never hand-merge these.** Also removed a duplicated
block in `CommitTransactionAsync` that would have emitted every `txn` action TWICE.

**The exemption is back as `DeltaTransaction.ExemptRowLevelFromWholeTableRead`** (default `false`),
consumed by `CommitOccAsync` via a defaulted parameter. ⚠ **THE BRIDGE DOES NOT SET IT YET**, so as of
`9d616c9` our shipped behaviour is DISABLED and `verify_delta_row_level_concurrency` §11 would fail.
The wiring is one line in `DeltaCatalog`'s flush, beside the existing `DeclareRead` /
`DeclareWholeTableRead` calls (~3483): `txn.ExemptRowLevelFromWholeTableRead = true;`. It was left until
after the Bridge's 24 call sites because the Bridge cannot compile against the new EW API before then,
and a flag wired into a non-building file is unverifiable.

**`FileRowSelection` is deleted** in favour of upstream's `RowSelection`. The recorded "38 src sites"
was wrong — it counted doc-comment mentions; there were NINE real occurrences. `RowSelection.ByPath`
takes exactly the dictionary our record wrapped; `Paths` / `PositionsFor` / `TotalPositions` cover every
use of `RowsByFile`.

**The remaining test migration is NOT a sed**, and the compiler proves it: our `Update*` returned
`(RowsUpdated, Version)` TUPLES while upstream's `UpdateRowsAsync` returns a plain `long` version, so
every `var (rows, _) = await …` site needs `rows` obtained another way (`selection.TotalPositions` where
the updater touches every selected row — check each, two sites in `MetadataColumnTests` at ~496 and
~590). The mapping, all in `test/EngineeredWood.DeltaLake.Table.Tests`:

| ours (gone) | upstream successor | note |
|---|---|---|
| `UpdateBySelectionAsync(sel, updater)` | `UpdateRowsAsync(sel, updater)` | returns `long`, not a tuple |
| `UpdateBySelectionAsync(batch)` | `UpdateRowsAsync(batch)` | locator-carrying batch form |
| `UpdateBySelectionViaVectorsAsync(…)` | `UpdateRowsAsync(…)` | upstream COLLAPSED MoR + CoW update into one entry point |
| `DeleteBySelectionAsync(sel)` | `DeleteRowsAsync(sel, RowDeleteMode.CopyOnWrite)` | tuple return survives |
| `DeleteByRowIdsViaVectorsAsync(ids)` | `DeleteRowsAsync(RowSelection.FromOrdinals(…, StaleAddressPolicy.Skip, …), RowDeleteMode.DeletionVector)` | needs the snapshot to resolve ordinals |
| `DeleteByRowIdsAsync(ids)` | same, `RowDeleteMode.CopyOnWrite` | |
| `ReadAllWithRowIdsAsync()` | `ReadAsync(new DeltaReadOptions { Metadata = DeltaRowMetadata.RowAddress })` | |
| `ReadAllWithRowTrackingAsync()` | `… DeltaRowMetadata.RowTracking` | |
| `ReadAllWithMetadataAsync()` | `… DeltaRowMetadata.Locator` | DONE in `9d616c9` |

One assertion also needs re-pointing: `MetadataColumnTests` ~637 asserts an error message names
`UpdateBySelectionAsync` as the alternative; upstream's message names the read option instead.

### THIRD ATTEMPT — DONE (2026-08-01). What the plan below did NOT predict

The bump is complete: EW `df4f918`, parent `597e97a` + the pin move. Everything in the order below
happened roughly as written. Four things did not appear in it at all, and they are the reusable part.

**1. `upstream/master` IS STALE — the branch is `upstream/main` now.** Upstream renamed it (`8caf8d8`,
"ci: follow the branch rename from master to main"), so a merge of `upstream/master` lands on a branch
upstream has moved off. The first merge here did exactly that and had to be extended by 12 commits
(4 code) afterwards. **`git fetch upstream` then read `upstream/main`, and do not trust a `master`
remote-tracking ref that still resolves.** `upstream/HEAD -> upstream/master` still points at the old
name locally, which is what makes this quiet.

**2. THE MERGE SILENTLY DROPPED ONE OF OUR PATCHES, and no test said so.** Conflict resolution took an
upstream region wholesale that contained `UpdateBySelectionViaVectorsAsync` — our MERGE-ON-READ UPDATE.
Upstream has no equivalent: `DELETE` has `RowDeleteMode.DeletionVector`, `UPDATE` always rewrites
(`UpdateRowsCoreAsync` → `RewriteRowsToNewFileAsync`). It was found by TRIPPING over it — the MoR tests
would not compile — and the reflex fix (point them at `UpdateRowsAsync`) would have converted five
merge-on-read tests into copy-on-write tests. **The audit that should have run first:** diff the PUBLIC
surface of the pre-merge `DeltaTable` against the merged one, then classify each absent method by
whether it existed in the MERGE BASE. That separates upstream's consolidation from our losses in one
pass — 14 absent, 10 upstream's, 3 ours-with-a-successor, 1 genuinely lost.

```bash
sig() { grep -oE '^ +public (async )?[A-Za-z0-9_<>,?\.\(\) ]+ [A-Za-z0-9_]+\(' \
        | sed 's/.* \([A-Za-z0-9_]*\)($/\1/' | sort -u; }
T=src/EngineeredWood.DeltaLake.Table/DeltaTable.cs
git show <pre-merge-sha>:$T | sig > /tmp/ours.txt
sig < $T > /tmp/now.txt
comm -23 /tmp/ours.txt /tmp/now.txt          # then check each against the MERGE BASE
```

**3. BUILDING THE BRIDGE is what finds the host's needs — reading the diff is not.** Two upstream
consolidations dropped things only a caller notices: `ReadRowsAsync` could no longer surface the
transient address (the host's row identifiers ARE that address, and the method exposes no absolute
position, so it cannot be reconstructed from outside), and the app-transaction precondition throws a
bare `InvalidOperationException` that cannot be told from any other commit-time failure. Both are back
as additive patches.

> **⚠ THE FIRST FIX WAS THE WRONG SHAPE, and finding out took one question.** I added a bespoke
> `rowAddressesOut` out-param, and assessed the resulting offer as the weakest of the three because
> upstream's doc points callers at `DeltaRowMetadata.Locator` instead. Asked whether the locator might
> already carry what we needed, I actually READ the enum — and **`DeltaRowMetadata.RowAddress` has been
> a first-class metadata kind the whole time**, emitting exactly the packed address as a column. The
> address was never missing from the library; it was missing from ONE read:
>
> | read | metadata support |
> |---|---|
> | `ReadAsync` | `DeltaReadOptions.Metadata` — all three kinds, as columns |
> | `ReadChangesAsync` | `DeltaChangeReadOptions.Metadata` — same |
> | `ReadRowsAsync` | **none** — instead `sourceRowTrackingOut`, a bespoke out-param DUPLICATING `DeltaRowMetadata.RowTracking` |
>
> So `ReadRowsAsync` was already the odd one out, and a second out-param beside the first made it
> worse. Rewritten as the `DeltaRowMetadata` parameter the other two reads take (`f9d1827`), mirroring
> `ReadCoreAsync`'s own helpers rather than a parallel path. That turned the weakest offer into the
> strongest — a consistency fix upstream can motivate without reference to us, justified by the enum's
> own words ("asking for two kinds costs ONE pass").
>
> **The lesson is narrow and repeatable: before adding a parameter, read the enum/options type the
> neighbouring methods already accept.** The bespoke out-param compiled, passed, and was defensible in
> isolation; it was only wrong relative to a convention sitting one file away.
>
> The Bridge adapts the column back to the out-param its own callers want, at the `ReadRowsByRowIds`
> seam — its buffered-UPDATE consumer indexes columns positionally against the pending schema, so a
> trailing metadata column would shift every index. Stripped BY NAME, never by position.

**4. A COMPILING BRIDGE IS NOT A MIGRATED ONE.** Two behaviour changes survived the compiler and were
caught only by the suites: upstream's `RequireAppTransaction` documents `expectedPrevious: null` as
"do not check", where OUR `fabricator_delta_set_transaction_version` means "must not exist yet" — a
replayed first batch would have gone from a failed CAS to an unconditional write, DUPLICATING DATA on a
user-facing exactly-once mechanism (fixed with an additive `requireAbsent`); and `SelectionFromRowIds`
is loud about an unresolvable ordinal, which is right for every DML path and wrong for the CDF
read-back, whose caller legitimately passes rows of this transaction's own pending files.

**And the reason the opt-in had a gate at all:** `ExemptRowLevelFromWholeTableRead` was wired LAST, and
until it was, nothing failed. `verify_delta_row_level_concurrency` §11 was written BEFORE the migration
for exactly that reason (82 → 93).

**`DeclareFilesRead` (#25) does NOT retire our exemption — I suggested it might, and that was wrong.**
The two solve different problems. Our whole-table declaration is made when a scan pushes NO predicate,
i.e. it genuinely read every file; declaring those files instead would keep the delete/read rule and
DROP the append rule, which is precisely the phantom-row protection `serializable` exists for — and
`serializable` is our catalog default since 2026-08-01. Upstream says as much in #25's own message
("it buys no protection against concurrent ADDS ... a host that also cares about phantom rows declares
BOTH"). The exemption is narrower and correctly scoped: WriteSerializable only, row-level DML only.
`DeclareFilesRead` remains available for a future scan that reads SOME files and wants precision.

### Suggested order when it is taken

**Updated after the first attempt** — the ordering below still holds, with the `FileRowSelection` →
`RowSelection` migration inserted BEFORE the Bridge call sites (the Bridge sites consume the type, so
doing it the other way means touching them twice), and EW building + its own suites passing as a
committable checkpoint before the Bridge is touched at all:

1. Merge `upstream/master` into `bump/upstream-2026-08`; take the shredding delete and upstream's test
   file (finding 4); resolve `DeltaTransaction.cs` and `DeltaTable.cs` hunk-wise (finding 3), taking
   upstream on every hunk — including the row-level exemption one, which is then RE-ADDED as the
   explicit opt-in rather than as our inference (finding 2, revised). Fallback if the restructured loop
   fights it: preserve the inference and fix upstream's remark instead.
2. Delete our `FileRowSelection` type and its duplicate `StageRowDeletesAsync` overload; migrate the
   38 EW `src/` + 5 EW test sites to `RowSelection` (finding 1).
3. Correct upstream's `DeclareWholeTableRead` remark on our branch so it describes what we actually do.
4. **Checkpoint: EW builds and EW's own suites pass on net472 + net8.0 — commit the merge here.**
   The Bridge is still broken at this point and that is fine; EW compiles standalone.
5. Migrate the 24 Bridge call sites + its 5 `FileRowSelection` uses → resolve `WriteChangeDataFilesAsync`
   → full `verify_delta_*` sweep → hermetic tier → fast-forward `fabricator-patches`, re-pin the parent.

Original wording, kept because the dependency reasoning in it is still the right one:
Drop the superseded shredding patch → merge `upstream/master` into `fabricator-patches` → migrate the 24
Bridge call sites (Delete/Update first: they share `RowSelection`, which slice 2 introduced and slice 5
consumes) → resolve `WriteChangeDataFilesAsync` → full `verify_delta_*` sweep. Then the standing rules from
this doc apply unchanged: **diff any method taken wholesale against upstream and demand byte-identity** (the
auto-merged duplicate-statement trap), **only the net472 leg proves a change offerable**, and **fast-forward
the pin, never force-push** (release tags pin EW shas).

## THE 2026-08-02 BUMP — DONE, pin `3b95599`. The cheapest bump so far, and it says why

Eight upstream commits the day after the last one: **#40, #41, #43, #46, #48, #49, #50 — and #39, which is
OURS, merged.** Five conflicted files, and every one of them sat exactly where one of our three superseded
patches was. Nothing else conflicted at all.

**That is the branch model paying out.** Three of the eight commits ARE our offers, taken and re-cut; the
conflicts were the cost of having them absorbed, not of having diverged. Total hands-on resolution: three
files taken wholesale, one property spliced back, two hunks merged by hand.

| upstream | what it does to us |
|---|---|
| **#43** | `expectedPrevious`/`requireAbsent` → the `AppTransactionPrecondition` union (None/Absent/Exactly/NotApplied). **Supersedes our #37, subsumes #38.** One Bridge call site |
| **#39 + #48** | `ReadRowsAsync` gains `DeltaRowReadOptions` and LOSES `sourceRowTrackingOut`. #39 is ours; #48 retired the out-param. One Bridge call site |
| **#40** | `GetLatestVersionAsync` now counts CHECKPOINT versions, not only commits |
| **#41** | four `_delta_log` walks per snapshot build → one |
| **#46 + #49** | `DeltaTransaction` gets `AbortAsync`/`IAsyncDisposable`; the six auto-committing paths collect their own orphans |
| **#50** | a write whose batch carries an UNDECLARED column is now refused |

### Resolution, and the two hunks a `--theirs` would have broken

Taken wholesale (upstream re-cut them better): `AppTransactionPreconditionException` + the precondition
validation, and `ReadRowsAsync`. Kept and spliced back onto upstream's file:
`DeltaTransaction.ExemptRowLevelFromWholeTableRead` (26 lines, 0 deletions).

**Two were BOTH-SIDES changes, where picking either side loses something silently:**
- `CommitOccAsync` — ours added `exemptRowLevelFromWholeTableRead`, upstream added the `written` ledger. It
  now takes both. `--theirs` drops our isolation exemption; `--ours` drops #49's orphan collection.
- `DeleteAsync` — ours added the `MetadataPredicate` lowering, upstream added `await using`. It now has
  both. (The lowered route reaches `DeleteRowsAsync`, which owns its own ledger under #49, so it needs no
  transaction of its own to clean up — worth stating, because the asymmetry looks like an oversight.)

**⚠ And ONE hunk auto-merged WRONG without conflicting.** Our `requireAbsent && expectedPrevious` mutual-
exclusion check was an addition relative to the merge base and upstream did not touch that exact text, so
git kept it silently — referencing two parameters that no longer exist. The compiler caught it here, but it
is the same auto-merge trap this doc already records, in a new place: **a conflict-free region inside a
conflicted method is not evidence of anything.**

### Our test that the TYPE SYSTEM retired

`RequireAbsentAndExpectedPrevious_Together_IsRejected` pinned that asking for both throws an
`ArgumentException`. With the union, the contradiction is UNREPRESENTABLE — no call can reach that throw.
Deleted, with the reason in the file. **A test whose failure mode the type system has made impossible is
not coverage; it would only assert that C# still has one parameter.** Distinguish this from deleting a test
because it became inconvenient — the behaviours it guarded are pinned by Absent/Exactly upstream.

### The migration the compiler could NOT have caught

`kv.Value.Expected is null` had to map to **`Absent`, not the union's `None`.** Both compile. `None` writes
UNCONDITIONALLY, so a replayed first batch of `fabricator_delta_set_transaction_version` would commit a
SECOND time and rewrite the recorded version with the same value — leaving nothing in the table to say it
happened. Same class as the last bump's `requireAbsent` finding, which is the point: **the precondition's
default is the dangerous answer, and it is what an unthinking migration picks.**
(`NotApplied` — Delta-Spark's monotonic rule — is a plausible third mode to expose later. It is a CONTRACT
change and does not belong in a bump.)

### `sourceRowTrackingOut`'s removal, and the mutation test that indicted the PAIRING

The out-param became `DeltaRowMetadata.RowTracking`, so both identities now arrive as COLUMNS in one pass
and `StripRowAddress` generalised to `StripMetadata`. Two things this had to get right:
- **Ask for RowTracking only when a caller wants it.** On a table WITHOUT row tracking the column form is
  REFUSED where the out-param quietly returned all-nulls (#48's whole argument). Our one caller allocates
  `sourceTrackingOut` only under `TxnDmlProfile.MaterializeRowIds`, so the conditions line up — but that is
  now LOAD-BEARING rather than incidental, and the code says so.
- **Strip every metadata column.** The buffered-UPDATE consumer indexes positionally, and since #50 an
  un-stripped column would be a REFUSED WRITE rather than the silent extra parquet column it used to be.

**The mutation test is the part worth carrying forward.** The rewrite sits on identity preservation across
an UPDATE, so a green suite was not enough. First attempt: mutate the stable-id read (`+1000`), run
`verify_delta_catalog_materialize_rowtracking` — **the mutant SURVIVED.** The tempting reading is "this code
is untested". It was the wrong suite. Instrumenting the branch with a hard throw settled it in one run:
`materialize_rowtracking` and `compaction_rowtracking` never reach it; **`verify_delta_row_tracking_virtual`
does** (line 70). Re-run there and the mutant dies at line 76, which asserts an updated row still carries
its ORIGINAL `__delta_row_id`. ⇒ **a surviving mutant indicts the PAIRING of test and code, not the code**,
and the suite whose NAME matches the feature was not the one that covers it.

### #50 does not fire on us, checked structurally rather than by a green run

The Bridge asks for metadata columns in exactly three places: two scan paths that rename the address to
DuckDB's rowid and hand it to DuckDB, and the read-back, which strips. There is no route by which a
metadata column reaches a write. (A green hermetic tier says the guard did not fire; the enumeration says
it cannot.)

### `await using` on the buffered flush — deferred out of the bump, then TAKEN as its own change (same day)

Held back from the bump on purpose ("rollback leaves invisible orphans for VACUUM" is documented
behaviour, and a bump is the wrong place to change behaviour), then done separately. ⚠ The dependency is
real: this line was UNSAFE at #46 — `CommitOccAsync` refreshed the snapshot AFTER the commit json was
durable and inside the same `try`, so a commit that LANDED and then threw still named its live files and
disposal would have deleted committed data. #49 empties the ledger the instant `WriteCommitAsync` returns.

**What it collects is narrower than "orphans", and the split is by WHO WROTE THE FILE.** Our eagerly-written
DATA files are written before the flush's transaction exists, and EW's provenance rule never collects a
host-written file — so §7 of [delta-transactions.md](delta-transactions.md) still describes them exactly.
What is collected is what EW's own writers stage during the flush, above all the **deletion vector of a
buffered DELETE**: `StageRowDeletesAsync` writes it before the precondition is judged.

**⚠ THE FIRST PROBE SAID THERE WAS NOTHING TO COLLECT, AND IT WAS VOID.** 100 rows, `DELETE … WHERE
id % 3 = 0`, refused commit: zero `.bin` files before and after — which reads as "this change is
pointless". EW stores a vector **INLINE in the commit json** below a 1 KB roaring-bitmap threshold, so a
small delete has no file to leak. At 500k rows the orphan appears and survives forever. **A cleanup whose
target is size-conditional cannot be probed at a convenient size** — the same "negative result is not a
measurement" rule, wearing the threshold as its disguise.

Gate: `verify_delta_txn_version` §9 (51 → **65**), mutation-tested — reverting to `var` fails exactly the
"the vector is GONE" assertion and nothing else. It asserts ZERO vectors BEFORE the refused flush as a
positive control, so the later zero cannot be read as "never written", and its comment says the delete must
exceed the inline threshold or the section passes while testing nothing. Hermetic floor 5640 → **5654**.

### `MetadataPredicate` REMOVED the same day (EW `141bd98`) — the patch set is 1133 → 867

The predicate lowering (`_metadata.file_path` + `_metadata.row_index` → a `RowSelection`, and a loud refusal
for a `_metadata` predicate that cannot lower) is gone, with `MatchedRowsUpdater`,
`DeleteBySelectionViaVectorsOrRewriteAsync` and its seven tests. `DeleteAsync`/`UpdateAsync` return to
upstream's exact shape.

**Not because it was wrong — because we can never reach it, structurally.** The lowering exists to PRODUCE
a `RowSelection`, and DuckDB's rowid IS our key, so the Bridge holds the selection before EW sees the
statement; routing a predicate through it could only hand back what we started with. Measured: zero Bridge
references, and its two call sites are entry points we never call (our DML goes through
`DeleteRowsAsync(RowSelection)` / `UpdateRowsAsync` / `UpdateBySelectionViaVectorsAsync`).
`Expressions.Predicate` IS used by the Bridge — for scan pushdown and the read-set declaration only.

⚠ **The deciding question was WHICH `_metadata` columns it lowers, and it is TWO of the four**:
`file_path` and `row_index` — NOT `row_id`, NOT `row_commit_version`. Physical address only, which is
exactly what a DuckDB rowid already decodes to. **Had it lowered the STABLE id the answer would flip**: a
`DELETE WHERE _metadata.row_id IN (…)` cannot be resolved to `(path, position)` without reading, so that
lowering WOULD give a rowid-keyed host something it cannot derive. It does not exist, and such a predicate
is refused outright by `ThrowIfReferencesMetadata` — so even the hypothetical future need points AWAY.

- **Tests: seven removed, the rest MIGRATED not deleted.** The other `MetadataColumnTests` uses were
  `MetadataPredicate.FilePathColumn` / `.RowIndexColumn` as COLUMN-NAME CONSTANTS, not the lowering →
  repointed at `DeltaMetadataColumns`, which is where the names belong. 875 → **868** on all three TFMs,
  exactly the seven, nothing collateral.
- **Removal does NOT foreclose the offer**, and arguably strengthens it: `offer/*` branches cut fresh off
  `upstream/main` and git history keeps the file, while "we do not use this ourselves" is the honest lead
  for a reviewer who triages by WHO EACH GAP SERVES. The hazard is real and still present upstream.
- Historical description of the feature (measurement tiers included) survives in
  [rowid-dml-seam.md](rowid-dml-seam.md) §6.3, flagged as removed.

### Upstream #52 + #53 taken the same day (pin `d9d204b`) — and the merge was CLEAN

The last two of the uncommitted-file set #46/#47/#49 opened. **Zero conflicts**, which is the
`MetadataPredicate` removal paying off within the hour: our `DeltaTable.cs` footprint is what had been
colliding with upstream's edits there, and dropping 190 lines of it removed the overlap.

**#53 — a rebased delete kept every losing attempt's deletion vector. A free win on a path WE drive.**
Our buffered flush stages `DeleteDvEdit`s through `txn.StageRowDeletesAsync`, and EW's commit loop rebases
exactly those on a collision, writing fresh `.bin` files each attempt. A FAILING run collected them via the
ledger; a SUCCEEDING one did not — the commit empties the ledger wholesale, correctly protecting the
winner's files and in the same motion forgetting every earlier attempt's. So a fabricator buffered DELETE
that hit contention and then succeeded leaked one vector per losing attempt.
- ⚠ **Our suites could not have found this, and neither could our multi-writer races.** sqllogictest runs
  connections SEQUENTIALLY, so every row-level scenario we test has no window between scan and commit —
  the same gap already recorded for the autocommit `rowLevelRetry` path. And the fuse/abfss races are no
  help despite their 90 and 19 REAL retries: those were APPEND commits, with no deletion vectors at all.
  Worth stating because reaching for those numbers here would be the wrong evidence for the right claim.

**#52 — `DiscardDataFilesAsync(files, ct)`, additive, nothing consumes it yet.** A VERB for reclaiming
files that `WriteDataFilesAsync` wrote and no version will ever name. Upstream chose a verb over a disposal
deliberately: the write is meant to outlive the call and may be committed by a later unrelated one, so only
the HOST knows the commit is not coming.
- **It is the tool for the orphan class `await using` deliberately did NOT cover** — our eagerly-written
  DATA files, which EW's provenance rule excludes precisely because the host wrote them. Adopting it would
  narrow §7 of [delta-transactions.md](delta-transactions.md) a second time, from "invisible orphans for
  VACUUM" to "reclaimed at rollback", which is a documented-behaviour change and therefore NOT part of a
  bump. Tracked as a follow-up.
- ⚠ Its guard, when we do adopt it: it **REFUSES a file the table references**, checked against a FRESHLY
  READ log rather than the cached snapshot (the commit that made them live may have come from another
  handle), validate-then-apply so a list with one committed file does not half-delete the rest.

Surface audit clean; EW Table.Tests 868 → **877** × {net10.0, net8.0, net472} (+9 upstream); Bridge builds
unchanged; fabricator hermetic **63/63 — 5654**, service **44/44 — 1444**. Patch set unchanged at
**+867 / −44 across 8 files**.

### Gates

EW Table.Tests **875/875 × {net10.0, net8.0, net472}** at the bump, **868/868** after the
`MetadataPredicate` removal; fabricator hermetic **63/63 — 5640** at the bump and **5654** after the
`await using` gate; service **44/44 — 1424**, then **1444** with the S3 attach warning. Every tier at
exactly its floor at each step.

**Harness gap found: the CLAUDE.md copy-paste block does not run the service tier.** It is missing
`MSSQL_TESTDB_URI`, `MSSQL_TEST_PASS`, `MSSQL_BINCOLL_DSN` and `FABRICATOR_PLUGIN_DIR`; `run-suites.sh`
names them and refuses to start, which is the good failure. The bad one is `FABRICATOR_PLUGIN_DIR`: it must
be the **RID-qualified** `bin/Release/net10.0/win-x64`, and pointing it one level up at `net10.0` (which
holds only the RID subdirectory) makes the suite fail with *"Scalar Function with name plug_greet does not
exist"* — **indistinguishable from a plugin that loaded and failed to register.** The plugin SPI has no
"found nothing to load" signal. Block corrected in CLAUDE.md.

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

## FULL PATCH-SET AUDIT vs `upstream/main` = `v0.2.0` = `a99cc41` (2026-08-03)

Our pin `d9d204b` on `fabricator-patches`. **Only 2 upstream commits behind** (`#58` CI, `#59` docs — both
non-functional, so a bump gains nothing but is free).

**⚠ Read the diff with `--stat=200` or no `--stat` at all. A `| tail -N` on `git diff --stat` silently drops
the head of the file list, and doing exactly that in this session produced a confident, wrong claim that
`DeltaTable.cs` had been absorbed upstream.** It has not.

Totals: **19 files, +3389/−47** — of which **9 production files, +868/−45**; the remaining 10 are tests.

### Every production file, with a verdict

| file | +/− | what it is | verdict |
|---|---|---|---|
| `Concurrency/ConflictChecker.cs` | +42 | `IsBlindAppend` consumes `commitInfo.isBlindAppend`; old logic renamed `InferBlindAppend` | **OFFER** — general correctness, see §isBlindAppend |
| `DeltaTable.cs` | +409/−38 | 3 new members + 2 modified (below) | **SPLIT** — see below |
| `VariantTransport.cs` | +322 | whole file, `internal static class VariantTransport` | **KEEP OURS** — temporary, DuckDB #24157 |
| `Schema/SchemaConverter.cs` | +50 | `VariantTransportExtensionName = "ew.variant_transport"`, `IsVariantTransportField` | **KEEP OURS** — variant transport |
| `DeltaTransaction.cs` | +26 | `ExemptRowLevelFromWholeTableRead` | **OFFERABLE** — see caution below |
| `DeltaTableOptions.cs` | +15 | `VariantTransportBlob` | **KEEP OURS** — variant transport |
| `EngineeredWood.DeltaLake.Table.csproj` | +5 | **a COMMENT ONLY** (why Apache.Arrow suffices, why the shredding toolkit is deliberately not referenced). No PackageReference change | **OFFER or DROP** — free either way |
| `DeltaFilePruner.cs` | +4 | **a doc comment ONLY** (`<para>` saying callers reach it via `PlanFiles`) | **OFFER or DROP** — free either way |
| `doc/codec-seam-investigation.md` | +2/−2 | doc text | trivial |

So ~**392 of the 868 lines (45%) are the variant transport** (322 + 50 + 15 + 5), which is ours by design.
Another **9 lines are pure comments** in two files. The real negotiable surface is `DeltaTable.cs` (409) plus
`ConflictChecker` (42) plus `DeltaTransaction` (26).

### `DeltaTable.cs` — NEW vs MODIFIED, established by grepping `upstream/main`

| member | in main? | notes |
|---|---|---|
| `UpdateBySelectionViaVectorsAsync` (public, ×2 overloads) | **NO — ours** | the merge-on-read UPDATE |
| `WriteChangeDataFilesAsync` (public) | **NO — ours** | CDF file writing |
| `BuildInlineDeletionVectorsAsync` (internal) | **NO — ours** | inline DV construction |
| `UpdateAsync` | yes (16 hits) | we MODIFIED it |
| `RebaseDvDmlActionsAsync` | yes (3 hits) | we MODIFIED it (the buffered-DML-through-OPTIMIZE remap) |

## ⚠ THE `*BySelection*` QUESTION — **RESOLVED 2026-08-03 THE OTHER WAY: IT MOVED TO THE BRIDGE, AND IS GONE FROM EW**

**The section below is the SUPERSEDED verdict. Its three "blockers" were wrong, and the reason they were wrong
is the transferable part: it asked whether the METHOD BODY could be RELOCATED — which does need its `internal`
callees — instead of whether the EFFECT could be COMPOSED from the public seams.** `DeltaTransaction` **is** that
seam, and it was built for this shape.

**What made it possible, all PUBLIC and all UPSTREAM (verified byte-identical signatures against `upstream/main`):**

| need | public upstream API | note |
|---|---|---|
| the `internal` DV core | **`DeltaTransaction.StageRowDeletesAsync(RowSelection)`** | calls `ComputeDvActionsWithEditsAsync` FOR us (`DeltaTransaction.cs:442`) and records `_dvEdits`/`_removedPaths` — so blocker 1 was a public door all along |
| scoped read of the matched rows + their identities | **`DeltaTable.ReadRowsAsync(RowSelection, DeltaRowReadOptions)`** | NOT a whole-table read. Its own doc says `DeltaRowMetadata.RowTracking` is *"the one to hand back to `WriteDataFilesAsync`' `materializedRowIds`"* — upstream DOCUMENTS this composition |
| post-images keeping their ORIGINAL stable ids | **`WriteDataFilesAsync(…, materializedRowIds:)`** | already public upstream; this is the piece expected to block and did not |
| one atomic version | `StageDataFilesAsync` + `StageChangeDataAsync` + `CommitAsync`, `Operation = "UPDATE"` | `StageChangeDataAsync`'s doc names "an update's pre- and post-images" explicitly |
| `ActiveFilesByPath` (internal) | `PlanFiles` | the Bridge already used it for ordinal→path |

**It is also the shape the BUFFERED path already used**, which is the argument that should have settled it
immediately: `DeltaCatalog.BufferUpdateRows` reads pre-images, substitutes SET values, collects original stable
ids and stages DV-delete + co-staged append into one commit — entirely on public primitives. Autocommit was the
OUTLIER calling a bespoke EW entry point. This removes a divergence rather than creating one.

**Three things the move BUYS, because the retired method committed via `CommitDataFilesAsync(expectedVersion:)`:**
that is a bare compare-and-set, so ANY concurrent commit failed the statement; `DeltaTable.cs:5479` reads
`catch (DeltaConflictException) when (attempt < 16 && expectedVersion is null)`, so **the OCC retry loop was
disabled outright**; and no per-file DV edits were recorded, so **there was no row-level reconciliation on this
path at all**. It also now honours the TABLE's `delta.isolationLevel` (the retired method hardcoded nothing and
simply CAS'd) — resolved from the configuration the update path already reads, so no extra `_delta_log` LIST,
via a shared `DeltaReader.EffectiveSerializable` that `DeltaCatalog` now delegates to so the precedence rule
stays expressed ONCE.
- ⚠ **The concurrency gain is a MECHANISM claim, not a measured one — see
  [delta-transactions.md](delta-transactions.md) §8.5 for why measurement was not available on this box**, and do
  not upgrade the wording without a measurement.
- ⚠ **ONE deliberate narrowing, and it is not covered by any suite.** Validation now comes from
  `StageRowDeletesAsync` → `ValidateWritable(snapshot, isAppend: false)`, which is `ProtocolVersions.
  ValidateWriteSupport` + **`RejectRowTrackingWrite`** + `HonorWriterFeatures` (`DeltaTable.cs:4228`). The retired
  method called `HonorWriterFeatures` ALONE — as does the autocommit DV `DeleteRowsAsync` — so the new path is
  stricter by exactly `RejectRowTrackingWrite`: a table with row tracking ON but the two spec-required
  materialized column names ABSENT (spec-invalid; anything `CreateAsync` makes has them) is now REFUSED where it
  previously proceeded with `materialize = false`, i.e. **silently reassigned row identity**. The refusal is the
  better answer and it matches what every DML through a `DeltaTransaction` already does, which is the point of
  the migration — but it is a behaviour change, it is reachable only via a foreign writer, and the hermetic tier
  (5686, unchanged) does not exercise it. Noted rather than "verified".

**What was REMOVED from EW (218 lines):** `UpdateBySelectionViaVectorsAsync` (both overloads, 196) and
`BuildInlineDeletionVectorsAsync` (22). **`WriteChangeDataFilesAsync` (45) STAYS for now** — the first removal
attempt took it too and the Bridge failed to compile: the BUFFERED CDF path (`DeltaCatalog.WriteCdcFilesAsync`)
needs it. ⚠ A grep of EW alone said "3 occurrences, all inside the island"; the second consumer was in the
BRIDGE. **Grep both trees before calling a member self-contained.**

### Why `StageChangeDataAsync` does NOT replace `WriteChangeDataFilesAsync` — and what does (2026-08-03)

The obvious question, since the autocommit MoR UPDATE now uses `StageChangeDataAsync` — and sharper still once you
notice `StageChangeDataAsync` CALLS the very method we want. It cannot serve the buffered path, and the reason is
visible in its four-line body:

```csharp
public async ValueTask StageChangeDataAsync(...)          // ← ValueTask: returns NOTHING
{
    EnsureOpen();                                          // ← requires a LIVE transaction
    var files = await _table.WriteChangeDataFilesForAsync(_baseSnapshot, …, written: _written);
    StageInternal(files);                                  // ← the actions vanish INTO this transaction
}
```

It is `WriteChangeDataFilesForAsync` **plus two things we specifically do not want:**

1. **`EnsureOpen()`** — an instance method on a transaction, and the buffered path has none at statement time (it
   is created at FLUSH, `DeltaCatalog.cs:3653`).
2. **`StageInternal(files)` with a `void` return** — it files the actions into THAT transaction and keeps no
   receipt. We need the `CdcFile` list RETURNED, to park on `pending.PendingCdc` and re-stage later via
   `txn.StageActions(baseExtra)` (`DeltaCatalog.cs:3352` → `3721`) — into whatever transaction the flush creates,
   **including a fresh one after an OCC reopen**. This is the crisp reason: "it calls the method we want" is not
   enough when it then swallows the result.

The second consequence: deferring the call to flush would hold the pre/post-image **ROWS** in memory until COMMIT,
exactly what eager CDC capture (slice C2) exists to avoid.

**⚠ CORRECTION (2026-08-03) — an earlier version of this section gave a THIRD reason that is FALSE, and the
question that exposed it was "would an OCC retry rewrite the CDF parquets?".** It claimed that creating the
transaction early "fights the flush's OCC retry, which reopens at latest — parked actions survive a reopen, a
consumed staging call does not". **`FlushDmlTransactionAsync` has NO retry loop.** The retry is INSIDE
`txn.CommitAsync`, which re-rebases from the ORIGINAL staged actions and never re-runs staging — the comment at
`DeltaCatalog.cs:3777` says so: *"ONE call replaces what used to be a hand-rolled OCC loop here … re-rebase from
the ORIGINAL staged actions, never from a prior attempt's — are engineered-wood's to keep now."* The false reason
described that retired hand-rolled loop; it was written after mistaking the OTHER retry loop
(`DeltaCatalog.cs:2720`, around `CommitDataFilesAsync` on the eager-write path) for the flush's.
- **So: a retry does NOT rewrite CDF files, in either design.** Staging happens once; only the commit is retried.
- **And an early-created, long-lived `DeltaTransaction` per (txn, table) COULD use `StageChangeDataAsync`** —
  write eagerly per statement, stage into it, commit at flush. That is an ARCHITECTURAL change (one open EW
  transaction per table for the whole DuckDB transaction, each pinning a snapshot, all needing abort on ROLLBACK),
  not an impossibility. State the offer as **"the smallest change that preserves the current architecture"**, never
  as "no alternative exists".

#### Why there is no `DeltaTransaction` at statement time (the recurring question)

There IS a DuckDB transaction. An EW one needs something DuckDB's does not guarantee — **a table that already
exists on storage** — plus three softer things:

1. **CREATE-in-transaction makes it IMPOSSIBLE, not merely awkward.** `StartTransaction()` is an instance method on
   `DeltaTable`, and `DeltaTable.OpenAsync` reads `_delta_log`. Inside
   `BEGIN; CREATE TABLE t …; INSERT INTO t …; COMMIT;` there is no `_delta_log` until the flush writes version 0,
   so at the INSERT there is no object to call it on. This is why the buffer carries `PendingCreate`
   (`DeltaCatalog.cs:2165/2181`) and why reads inside the transaction are served by our own `ScanPendingCreated`
   (`:1476/:1777`) rather than by EW.
2. **Scope mismatch.** A DuckDB transaction spans MANY tables; an EW transaction is PER TABLE ⇒ N of them, created
   lazily, each with its own lifecycle.
3. **Resource lifetime.** The Bridge opens and disposes the `DeltaTable` per operation; holding one open per table
   for the DuckDB transaction's life keeps filesystem + snapshot state alive — live remote state on OneLake/S3.
4. **Read-your-writes — and this is the loop back to the no-getter finding.** Because staged actions cannot be read
   back out of a `DeltaTransaction`, one CANNOT serve reads of its own pending state. The buffer can.

⇒ **The buffer is not a substitute for a transaction; it is what makes CREATE-in-transaction and read-your-writes
possible, neither of which a `DeltaTransaction` can do.** It therefore has to exist regardless, and once it holds
the pending state anyway, staging at flush is the natural design rather than a workaround. A long-lived transaction
beside it would duplicate what the buffer already tracks.

**⚠ AND THE CLEVER WORKAROUND DOES NOT WORK — "create a throwaway transaction, `StageChangeDataAsync`, take the
actions, dispose it".** It fails twice, and the second failure is destructive:

1. **There is no way to read the actions back out.** Every action-related public member on `DeltaTransaction` is a
   SETTER (`StageDataFiles`, `StageDataFilesAsync`, `StageActions`, `StageChangeDataAsync`); `_dataActions` has no
   getter and `Written` is `internal` (`DeltaTransaction.cs:144`). The "take the actions" step has no API.
2. **Disposing the throwaway DELETES the CDC files it just wrote.** `StageChangeDataAsync` registers them in the
   transaction's ledger (`:500`, `written: _written`), and `AbortAsync` collects that ledger
   (`:847`, `DeleteWrittenFilesAsync`) — with `DisposeAsync() => AbortAsync(...)`. So `await using` on the
   throwaway destroys the output. NOT disposing "works" only by abandoning a transaction and trusting that nobody
   ever adds a `finally` — the inverse of the `await using` discipline adopted at EW #49, where one stray dispose
   is silent data loss. (`_written.Clear()` at `:808` is the COMMIT path: after a real commit the files belong to
   the table. That is the only other way out of the ledger, and a throwaway never commits.)

**This is what validates dropping `WrittenFileLedger?` from the proposed public overload** — not cosmetics. For
this caller the CDC files must NOT be owned by a transaction that can abort: they are owned by the DuckDB
transaction's lifecycle and reclaimed on ROLLBACK by `DiscardDataFilesAsync` (§7). `written: null` is the
semantically correct call, and no transaction-mediated route can express it.

**⚠ FIRST, A CORRECTION WORTH KEEPING: EW's own autocommit DML does NOT use `StageChangeDataAsync`.** Its
`DeleteRowsAsync` / `UpdateRowsAsync` / append paths call the internal `ChangeDataFeed.CdfWriter.WriteAsync`
**directly** (7 sites: `DeltaTable.cs` 3148, 3502, 3511, 6134, 6288, 6743, 6747) and fuse the cdc actions into
their own commit. `StageChangeDataAsync` is a HOST-facing API — inside EW its only use is its own body
(`DeltaTransaction.cs:499`). The one autocommit path that uses it is OURS, the new MoR UPDATE, because we assemble
that commit ourselves. Do not describe the two as one mechanism.

**THE OFFER: make the internal PLURAL public. Upstream already HAS it.**
`WriteChangeDataFilesForAsync` (`DeltaTable.cs:5760`, **`internal`**) is the partition-splitting plural, and it is
what `StageChangeDataAsync` calls. **Our 45-line patch is essentially a PUBLIC DUPLICATE of it.**

```csharp
internal async ValueTask<IReadOnlyList<CdcFile>> WriteChangeDataFilesForAsync(
    Snapshot snapshot, RecordBatch rows, string changeType, CancellationToken ct,
    Int64Array? rowIds = null, Int64Array? rowCommitVersions = null,
    WrittenFileLedger? written = null)      // ← `internal sealed class` (WrittenFileLedger.cs:24)
```

One blocker on exposing it VERBATIM: the trailing `WrittenFileLedger` parameter is an internal type, so the offer
is a **public overload without it** — that ledger is for abort-time orphan reclamation, which a caller parking
actions on a buffer does not have anyway (we would pass null). Bonus: the signature already takes
`rowIds`/`rowCommitVersions`, i.e. the CDF identity our feed currently leaves NULL, so taking it also opens the
route to fixing that later.

**Ranked, because an earlier version of this section recommended the WORSE one:**

| offer | result |
|---|---|
| **public overload of `WriteChangeDataFilesForAsync`** | our 45 lines go, **zero duplication anywhere** — the Bridge just calls it |
| public `PartitionUtils` (`internal static class`, `PartitionUtils.cs:14`) | our 45 lines go, but ~25 lines of split + logical→physical re-key get DUPLICATED in the Bridge. Strictly worse |

For completeness, the pieces our wrapper is built from and their reachability from the Bridge: the SINGULAR
`WriteChangeDataFileAsync` (`DeltaTable.cs:3720`) is **public** — and its doc already describes the eager-capture
pattern verbatim (*"the caller's to fuse into a later commit via `CommitDataFilesAsync`' `extraActions`"*) —
`ColumnMapping.GetMode`/`BuildLogicalToPhysicalMap` are **public** (`ColumnMapping.cs:41/225`), and
`PartitionUtils.SplitByPartition` is **not** (internal class). So the public workflow upstream documents is
completable only on an UNPARTITIONED table; that is the completeness gap to name in the offer.

⚠ **Do NOT hand-roll the split in the Bridge instead.** The risk is not the grouping, it is Delta's
partition-value STRING ENCODING (nulls, dates, timestamps, decimals) having to agree byte-for-byte with what EW
writes for DATA files, or a CDF file's `partitionValues` will not match its data siblings. The Bridge's existing
partition handling READS those values from DuckDB's `RETURN_STATS.partition_keys`
(`DeltaGlobalTableFunction.cs:961/1043`) and never FORMATS them, so this would be new formatting code duplicating
EW's — the drift shape this patch set exists to avoid.

**Measured result.** Production patch set **867 → 649 insertions** (`DeltaTable.cs` **447 → 229** changed lines);
EW Table.Tests **877 → 872** (exactly the 5 tests of the retired member, deleted from our own
`MetadataColumnTests.cs`; the two `UpdateBySelection_*` tests that survive exercise upstream's
`UpdateRowsAsync`). Equivalence gate: hermetic **63/63 — 5686**, byte-identical to the pre-change count, plus
`verify_delta_catalog_update` 63 / `_changes` 73 / `_row_level_concurrency` 93 / `_row_tracking_virtual` 299 on
BOTH engines.

**Consequence for the upstream plan: `RowUpdateMode` is SOLVED BY REMOVAL and is OFF the offer list.** There is no
divergence left for it to retire and we have no need for it, so do NOT bring it — the API gap it would close (EW's
`DeleteRowsAsync` takes a `RowDeleteMode` while `UpdateRowsAsync` has none) is EW's business, not ours, and
spending credibility on a request we do not need weakens the ones we do.

**THE LIVE OFFERS (sizes are the RE-FRAMED asks, not our patch sizes — see §THE REFRAME PASS):**

| offer | ask | nature |
|---|---|---|
| **`ConflictChecker`** — consume `commitInfo.isBlindAppend` | 42 lines, `internal` class, no API surface | correctness |
| **public overload of `WriteChangeDataFilesForAsync`** | ~5 lines (visibility; drop the `internal`-typed `WrittenFileLedger?` param) | inconsistency — public singular, internal plural ⇒ their documented eager-capture workflow completes only on an unpartitioned table. **Replaces** offering our 45-line wrapper |
| **`ExemptRowLevelFromWholeTableRead` AS-IS** | 26 lines | **NOT an inconsistency — a DEPARTURE from Delta's `concurrentDeleteRead` rule**, and already the "explicit per-transaction opt-in" shape upstream said it would require. ⚠ The facet-split alternative is **RETRACTED**; see the section below, and note our opt-in is currently UNCONDITIONAL (a real, untested hazard) |
| a transaction that can CREATE a table | new API | see §7.1 of [delta-transactions.md](delta-transactions.md) — makes `BEGIN; CREATE; INSERT; COMMIT` one version |

**NOT offers, and label them honestly if they ever come up:** `RowUpdateMode` (solved by removal, above);
`VariantTransport` (ours by design, expires with DuckDB #24157); "readable staged state" — a `ReadAsync` that
overlays a transaction's own uncommitted actions, which is a genuine new CAPABILITY rather than an inconsistency,
and the precondition for replacing the buffer with a long-lived transaction.

### THE STRATEGY — how the fork-vs-upstream question actually resolves (2026-08-04)

**The goal** (user, 2026-08-04): run on ORIGINAL upstream engineered-wood, with our needs met by PRs that have a
high probability of being merged. Only if that proves impossible do we maintain our own — and then **it must be
clear IN THE CODE what our amendments are.**

**The variant transport is 392 of the 649 production lines (60%)** — `VariantTransport.cs` 322 +
`SchemaConverter` 50 + `DeltaTableOptions` 15 + csproj 5 — and exists only because DuckDB cannot carry a nested
VARIANT across the C data interface (duckdb/duckdb#24157). Upstream would be right to decline a workaround for
another project's bug, so it is **OURS-BY-DESIGN** and must never be offered.

**⚠ BUT IT DOES NOT HAVE TO LIVE IN EW — VERIFIED 2026-08-04, and this supersedes an earlier claim in this section
that a `PackageReference` was "gated on DuckDB #24157, not on Curt".** That framing was wrong: the transport is a
BOUNDARY conversion, and the boundary that matters is ours, not EW's.

The patch's real cost is that it **replaces** upstream's variant handling rather than running after it:

```csharp
cleanResult = _options.VariantTransportBlob
    ? VariantTransport.ToTransportBlobs(cleanResult, snapshot.Schema)   // ours — normalises FOUR layouts
    : VariantColumnCoercion.Coerce(cleanResult, expectedSchema);        // upstream's — same normalisation
```

Hence the 322 lines: it must handle canonical `VariantArray`, **shredded**, bare struct-of-binary (an unannotated
file from Spark 4.0.x) and a seam-delivered blob, keyed off the DELTA schema because an unannotated variant is
indistinguishable from a real struct at the Arrow level. **Upstream's `Coerce` already does that normalisation** —
to canonical. So: let it run unpatched, and convert **canonical ⇄ blob in the Bridge at the DuckDB boundary**. One
layout in, one out.

**The C-interface crash is irrelevant to this** — EW hands the Bridge RecordBatches as in-process .NET objects; the
crash only occurs on export to DuckDB, so the Bridge can hold a canonical `VariantArray` and flatten it late.

**Three checks, all passed (2026-08-04):**

| | result |
|---|---|
| a single read exit? | ✅ ONE site (`DeltaTable.cs:7796`, inside `ProcessFileBatchesAsync`). `ReadChangesAsync` (CDF) does not touch variant handling at all — a pre-existing EW gap, unaffected by the move |
| can the Bridge convert both ways? | ✅ **COMPILE-PROVEN** both directions. `VariantArray.Builder`/`VariantReader`/`VariantValue` are `Apache.Arrow.Scalars.Variant`; `VariantShredding` is EW's but `public static` and transitively referenced via the `DeltaLake.Table` ProjectReference. ⚠ The `ArrowArrayFactory` collision `VariantTransport.cs` warns about does NOT apply to the Bridge — it has no `InternalsVisibleTo` on `EngineeredWood.Parquet` |
| write sites interceptable? | ✅ all four are documented **"no-op for canonical input"**, so handing EW canonical arrays lets them be DELETED |

**⇒ Revised target: patch set == the ~257 upstreamable lines, and then ZERO — without waiting on DuckDB.**

#### DONE: the READ half has moved (2026-08-04). 649 → 469 insertions.

Built and gated. The Bridge now owns the canonical⇄blob conversion in
`Fabricator.Bridge/VariantTransport.cs` (~330 lines, one layout each way instead of four) and applies it at
**three** boundaries — not the two originally planned:

| boundary | direction |
|---|---|
| the 5 data-read exits in `DeltaReader` | canonical → transport blob |
| `NativeParquetDataFileReader` (native-read seam → EW) | blob → canonical |
| **`NativeParquetDataFileWriter`** (EW → DuckDB's COPY) | canonical → transport blob |

**⚠ The third was NOT in the plan and the suite is what caught it.** `verify_delta_catalog_variant` failed at
the OPTIMIZE section: the native writer hands EW's batches to DuckDB's `COPY`, so with EW now producing
canonical arrays that path needed flattening too — **including the PEEKED first batch**, whose schema is what
the COPY is built from. Converting only the stream would have described the file with a variant struct and then
fed it blobs.

**Removed from EW:** `ToTransportBlobs` (~160 lines) — dead the moment `VariantTransportBlob` stopped being set,
so the deletion was mechanical — plus the `DeltaTableOptions.VariantTransportBlob` flag (15) and the read-side
branch in `ProcessFileBatchesAsync`, which reverts to upstream's single `VariantColumnCoercion.Coerce` line
byte-for-byte. `VariantTransport.cs` 322 → **163** (write direction only).

**Test moves:** EW's `CanonicalWritten_ReadsBackAsTransportBlobs` is deleted (it tested the removed read
direction; the end-to-end is covered by our 157-assertion suite), and
`TransportRoundTrip_UniformColumnShredsAndReassembles` was **adapted rather than deleted** — its real subject is
that a uniform column SHREDS on write and REASSEMBLES on read, which is still this library's concern, so it now
reads back canonically and asserts the `value` child plus `typed_value` being absent (proving reassembly ran).
EW Table.Tests 872 → **871** × {net10.0, net472}.

**Gates:** hermetic **63/63 — 5686** after the wiring (identical to baseline), variant suite **157**.

#### THE WRITE HALF — DONE (2026-08-04). **The patch set is now 221 insertions across 4 files, with ZERO variant divergence.**

Both directions of the variant transport now live in the Bridge. What left EW in this pass:
`VariantTransport.ToVariantArrays` + helpers (163), the `SchemaConverter` marker support (50), the four codec
call sites in `DeltaTable.cs`, the csproj comment block, two `DeltaTable.cs` comments, and
`VariantTransportTests.cs` (164). **469 → 221 insertions, 8 files → 4** (`ConflictChecker` 42,
`DeltaTable` 183, `DeltaTransaction` 26, `DeltaFilePruner` 4). Gates: EW Table.Tests **868 × {net10.0, net8.0,
net472}** (871 − exactly the 3 deleted tests), host hermetic **63/63 — 5686** byte-identical to baseline,
variant suite **157** — measured BOTH with the EW patch still present (proving the new conversions break
nothing) and with it removed (proving they are what does the work; the first alone proves neither).

**The shape: the Bridge canonicalises what it hands EW, and nothing else moves.** New
`VariantMarker.ToCanonicalSchema`/`ToCanonicalField` (the inverse of `ToTransportSchema`) and a list overload of
`VariantTransport.ToCanonical`. Every helper returns its argument reference-identical when no variant is
present, so a non-variant write allocates nothing.

Conversion points, by kind:

| kind | where | note |
|---|---|---|
| **funnel** | `DeltaWriter.WriteAsync` (schema + batches) | covers the three `DeltaWriter.Write` sites AND the `OverwritePartitions`/`DynamicOverwrite`/`WriteAsync` trio, which are inside it |
| **funnel** | `DeltaWriter.CreateAsync` (schema) | covers both `DeltaWriter.Create` sites |
| **funnel** | `DeltaCatalog.AlterTable` (the `Field?` parameter) | covers ADD COLUMN and ADD FIELD, buffered *and* immediate — the field arrives from the C ABI here and every consumer below hands it to EW |
| batch | `WriteChangeDataFilesAsync`, three `WriteDataFilesAsync`, `UpdateRowsAsync`, both `StageChangeDataAsync` | |
| **schema only** | `TryStreamCreateFiles` + `TryWriteStreamingCoreAsync` (`ewSchema`) | the STREAM stays transport — it feeds DuckDB's COPY |
| **schema only** | the four `SchemaConverter.FromArrowSchema` calls | their inputs are bind-dialect schemas |

**Why ONE funnel is right at `DeltaWriter.WriteAsync` and was wrong at `BulkInsert`:** `WriteAsync` has a single
sink. Its `native_write` variant reaches DuckDB's COPY *through* EW — `NativeParquetDataFileWriter` flattens back
to transport itself — so no second consumer of those batches wants the blob form. At ingest, both dialects were
live at once.

**⚠ THE 13-SITE ENUMERATION WAS INCOMPLETE, and the missing ones were the dangerous kind.** A systematic grep
for every EW entry point taking Arrow data (`OpenOrCreateAsync|SetSchemaAsync|AddColumnAsync|AddFieldAsync|
ComputeAdd*|MergeSchemaAsync|Write*Async|Stage*Async|UpdateRowsAsync|ToDeltaField`) found **four more**: the
STREAMING native-write path's `OpenOrCreateAsync` and its **two** `SetSchemaAsync(data.Schema)` calls, plus the
copy-on-write `UpdateRowsAsync`. Three of those four hand EW a SCHEMA, i.e. they are exactly the
`binary`-recorded-durably failure below. **Enumerate by grepping the callee side, never by listing call sites
from memory** — and note the green intermediate could not have caught this, because the `SchemaConverter` patch
was still in place to cover for a missed site. Only step 3 tests completeness.

**⚠ The failure mode it protects against, restated because it is the worst in the variant surface:** with the
`SchemaConverter` patch gone, a transport-marked field that reaches EW maps to Delta **`binary`**. A `metaData`
commit is not revisable, so a CREATE/CTAS/ADD COLUMN would record the wrong type DURABLY and SILENTLY, surfacing
far away as an insert that cannot convert VARIANT to BLOB. (An INSERT into an *existing* variant table would more
likely error on the mismatch — it is the schema-writing paths that fail quietly.)

**⚠ Two entanglements found while reverting, neither in the plan:**
1. **Each of the four codec sites carried a SECOND `StripAnnotation`**, and it existed only to undo the
   annotation `ToVariantArrays` re-introduced — upstream already strips on `physicalBatch` before the writer
   branch. So the revert deletes both lines and restores upstream's single one. Deleting only the conversion
   would have left a stray double strip; keeping the strip "to be safe" would have diverged. **Read the
   surrounding upstream hunk, not just the line you added.**
2. **A latent hazard now noted in `NativeParquetDataFileWriter`:** the canonical→transport conversion there
   assumes `EmitVariantLogicalType` stays TRUE (its default; nothing of ours sets it). With it FALSE, EW's own
   `StripAnnotation` flattens the `VariantArray` to a bare struct BEFORE our writer sees it, at which point it is
   indistinguishable from an ordinary struct — so the conversion would silently not fire and the COPY would write
   a struct instead of a parquet VARIANT. Unreachable today; if that option is ever set, the fix is to pass the
   Delta schema in, not to guess from the Arrow type.

**One site the plan listed that must NOT be converted:** `ExternalTableRouting.cs:273`'s `Materialize(data)`.
Reading the call site shows its batches go back INTO `DeltaCatalog.ExecuteUpdate` via an `InMemoryArrayStream` —
the same dialect boundary as the C ABI — so converting there would break it. Only that file's
`FromArrowSchema` needed the wrap.

**The runtime cost is one extra materialisation per batch on variant tables** (build canonical, then flatten for
the COPY). Performance, not correctness; unmeasured. The live Spark/kernel round trip has NOT been re-run since
this change.

#### The superseded ingest-funnel attempt — kept because the diagnosis generalises

The first attempt canonicalised once at `BulkInsert(IArrowArrayStream data, …)`, whose own comment says
*"EVERY write (INSERT / CTAS / COPY, codec or native) passes exactly once"*. It was built and reverted whole.

**That stream has TWO SINKS WITH OPPOSITE DIALECT NEEDS.** The codec path materialises it for engineered-wood
(wants CANONICAL); `native_write` hands the SAME stream straight BACK to DuckDB's `COPY` via
`DeltaWriter.TryStreamCreateFiles` (wants TRANSPORT — the leaf blob is the only form DuckDB can carry).
Symptom: `complete_bulk failed: … INTERNAL Error: Attempted to access index 2 within vector of size 2` — the
COPY, not engineered-wood, and nothing naming variants. Second confirmation: the txn buffer holds two dialects
on purpose (`PendingArrowSchema` TRANSPORT for binds, `BatchSchema` matching the batches), so the funnel needed
FOUR compensating conversions. **Needing that many to keep one funnel honest is the signal it is in the wrong
place** — the generalisable lesson, and the reason the as-built version funnels at `DeltaWriter.WriteAsync`
(one sink) instead.

**Answering "stick with the branch and fix features, then clean up / decide?" — yes, with ONE thing pulled
forward.** Offering and building are NOT sequential: the branch model already handles both at once, and the
2026-08-02 bump proved it (three of eight upstream commits were our own offers coming back, re-cut). So keep
building on `fabricator-patches`. But do the MARKING now, because it is cheap, needs nobody's agreement, and it is
the thing that makes the eventual decision mechanical instead of archaeological.

#### Step 1 (do first, no upstream dependency): make the amendments SELF-DESCRIBING

Today the only way to answer "is this ours?" is `git diff upstream/main`. `DeltaTable.cs` alone carries **27
hunks**, largest 45 lines and a long tail of 25/17/14/10/8/8/7 — invisible at the point of use. Mark each with a
greppable comment carrying a STATUS, so `git grep FABRICATOR-PATCH` enumerates the whole divergence:

```csharp
// [FABRICATOR-PATCH: OFFER-READY] why it exists; what would retire it
// [FABRICATOR-PATCH: OFFERED #123]
// [FABRICATOR-PATCH: OURS-BY-DESIGN] expires with duckdb/duckdb#24157
```

Three categories are enough: **OFFER-READY**, **OFFERED #n**, **OURS-BY-DESIGN** (+ its expiry condition). The
prize is that a bump can enumerate our amendments without a diff, and a reader sees provenance where the code is.

#### Step 2 (in parallel): offer, ordered by probability × independence

| # | offer | ask | why this order |
|---|---|---|---|
| 1 | **public overload of `WriteChangeDataFilesForAsync`** | ~5 lines (visibility; drop the `internal`-typed `WrittenFileLedger?`) | ZERO behaviour change, names an inconsistency in their own API, and blocks nothing on our side — the ideal first PR |
| 2 | **`ConflictChecker`** — consume `commitInfo.isBlindAppend` | 42 lines, `internal` class, 7 tests, no API surface | correctness, but it DOES change behaviour ⇒ present both shapes (believe-flag-then-infer vs Delta parity) and let upstream choose |
| 3 | **`ExemptRowLevelFromWholeTableRead`** | 26 lines | **only after §2.2 is fixed** — offering an opt-in we apply more widely than we justify is how credibility goes. And it is a DEPARTURE from Delta, not an inconsistency: pitch it as one |
| 4 | a transaction that can CREATE a table | new API | a design conversation, not a PR ([delta-transactions.md](delta-transactions.md) §7.1) |
| — | variant transport | — | **never offer.** OURS-BY-DESIGN |

#### Step 3: the decision gate, so "decide later" has a trigger

Drop the branch for a `PackageReference` when **both** hold: the patch set is variant-transport-only, AND DuckDB
#24157 is fixed (or the transport is otherwise retired). Until then the branch is the correct answer, not a
failure — and with Step 1 done, "what is ours" is a grep rather than an investigation.

⚠ **What NOT to do while waiting:** do not let the patch set grow unmarked, and do not batch offers into one large
PR. Every offer above is independently mergeable, and the 2026-08-02 experience says small independent offers come
back re-cut and improved, which is the outcome we want.

### THE REFRAME PASS — do this BEFORE writing any upstream PR

Two of the three live offers above are not what we originally intended to send, and both improved the same way:
we stopped asking *"will they take our patch?"* and asked **"why is our patch shaped this way?"** The answer names
an upstream primitive we worked around, and that is usually an INCONSISTENCY in upstream's own surface — a much
smaller and more persuasive ask than our workaround.

| our patch | the inconsistency it reveals | ask shrank |
|---|---|---|
| 45-line CDF wrapper | public **singular** `WriteChangeDataFileAsync`, `internal` **plural** ⇒ their own documented eager-capture workflow completes only on an unpartitioned table | 45 → ~5 |
| 26-line `ExemptRowLevelFromWholeTableRead` | `ReadSet.WholeTable` conflates two facets the same record models separately (`Predicates` for adds, `Files` for removes) | 26 → ~2 |
| buffered CREATE (§7.1) | protocol permits `protocol`+`metaData`+`add` in v0, but `StartTransaction` is an INSTANCE method ⇒ inexpressible | — |

**The test: can the offer be stated in one sentence as "your API is inconsistent here", WITHOUT mentioning
fabricator?** Yes ⇒ it is a bug report, and small. No ⇒ it is a feature request; that is legitimate, but pitch it
differently, bring different evidence, and **do not disguise it as an inconsistency** — doing that on a request we
do not need (see `RowUpdateMode`) spends credibility on the ones we do.

**Questions that actually produced these** (all from the user, all reusable):
- *"X calls the function we want — so why isn't X enough?"* → exposes ownership / return-value mismatches
  (`StageChangeDataAsync` calls the right method and discards its result).
- *"Isn't upstream's Y the replacement for our Z?"* → exposes that Z NARROWS Y, and why one flag cannot say two
  things.
- *"Couldn't this just be metadata in memory?"* → exposes where an API SHAPE, not the format, is the constraint.

⚠ **Stop treating our patch's line count as the interesting number.** It measures our divergence, not the size of
the ask; every re-framed offer above collapsed to ≤5 lines while the DIAGNOSIS was the real work. The audit table's
useful column is "what upstream inconsistency does this reveal?", not "+N lines".

---

### The superseded `*BySelection*` verdict — one paragraph, because only the lesson survives

An earlier audit concluded **"do not move the merge-on-read UPDATE to the Bridge"**, on three blockers
(`ComputeDvActionsWithEditsAsync` is `internal` and shared; `ActiveFilesByPath`/`HonorWriterFeatures`/
`StaleSelectionPath` unreachable; it would reverse a consolidation) and proposed offering
`UpdateRowsAsync(…, RowUpdateMode)` upstream instead. **All three blockers were wrong and the offer is
withdrawn** — see the top of this section for what was actually done and why.

**The lesson, which is the only durable part:** it asked whether the METHOD BODY could be RELOCATED (which does
need the internal callees) instead of whether the EFFECT could be COMPOSED from the public seams. Ask the second
question first. `StageRowDeletesAsync` was a public door onto the very `internal` method the audit called a
blocker.

### ⚠⚠ `ExemptRowLevelFromWholeTableRead` — THE FACET SPLIT IS **RETRACTED**; OFFER THE PROPERTY AS-IS (2026-08-03)

**Read this before the facet-split argument below, which is kept only as a worked example of getting it wrong.**
Writing the actual CALL SITE killed it, and the reversal has three grounds, in increasing severity:

1. **We cannot make the judgment the split hands us.** `ReadWholeTable` is set by ANY scan with no pushable filter
   (`DeltaCatalog.cs:1412`) — it cannot distinguish a row-local `DELETE … WHERE x = 5` from a decision that
   depended on whole-table state. `forRemoves:` would just be EW's internal gate copied outward, not a better
   answer.
2. **It is not a read-set fact, and upstream pre-emptively rejected that framing.** `forRemoves: false` asserts
   "I did not read those files for removal purposes" — but we DID read them. The real justification is that our
   WRITES are row-local, a claim about the write path. Upstream, in `DeclareWholeTableRead`'s own remarks:
   *"a library must not claim on a host's behalf that it read less than it declared."*
3. **Our existing property is ALREADY the shape upstream said it would accept:** *"if it is ever adopted it should
   be an **explicit per-transaction opt-in** rather than an inference."* `ExemptRowLevelFromWholeTableRead` is
   exactly that.

⇒ **Offer the property AS-IS**, pitched as what it is: a **DEPARTURE** from Delta's `concurrentDeleteRead` rule
(Delta and EW both honour a whole-table read declaration at BOTH isolation levels; Spark gates only
`concurrentAppend` on the level), explicitly opt-in, with the row-level reasoning attached. This restores the
original CLAUDE.md guidance, which was right.

**⚠ This is the reframe pass's own caveat, violated while writing it:** a semantic departure was dressed up as an
API inconsistency. Re-read §THE REFRAME PASS's test — *"can it be stated as 'your API is inconsistent here'?"* — and
note that here the answer is NO, so it is a feature/semantics request and must be pitched as one.

**⚠ AND AN OVER-BROAD OPT-IN IN WHAT WE SHIP, identified by the same question — reasoned, NOT measured, untested.**
The Bridge sets `ExemptRowLevelFromWholeTableRead = true` **unconditionally** (`DeltaCatalog.cs:3775`), so it is not
limited to row-local predicates. **EW's gate is three-way** (`DeltaTable.cs:2404`):

```csharp
exemptRowLevelFromWholeTableRead && rowLevel && isolationLevel != IsolationLevel.Serializable
```

**⇒ INERT UNDER OUR DEFAULT.** Since the 2026-08-01 flip the catalog default is `serializable`, so the third
condition is false and the full read set is kept. **Do not describe this as broken out of the box** — a first draft
of this note did exactly that by omitting the isolation qualification.

| configuration | behaviour |
|---|---|
| default (`serializable`) | flag ignored entirely — correct, full read set |
| `write_serializable` (ATTACH option or `delta.isolationLevel`) **+** a txn that staged DV deletes | exemption applies — and is **over-broad** |

The over-broad case:

```sql
BEGIN;
SELECT avg(x) FROM t;          -- no pushable filter ⇒ ReadWholeTable = true
DELETE FROM t WHERE x > 42;    -- the threshold came from that avg; stages DV deletes ⇒ rowLevel = true
COMMIT;                        -- under write_serializable a concurrent remove of UNTOUCHED rows does not conflict
```

EW's own justification is that the row-level validation "has already established that no row this transaction
REMOVES was concurrently removed or moved" — which covers the removed rows and says nothing about a threshold
derived from a whole-table read. So the exemption is applied on reasoning that does not cover this shape.

**Verdict: not wrong answers by default; an opt-in applied more widely than its own justification supports, in one
non-default configuration, with no test coverage.** A fix would set the flag only when the whole-table read came
from a DML statement's own scan rather than an arbitrary `SELECT` — the buffer cannot tell today, since
`ReadWholeTable` is a single bool with no provenance. Small to add; a behaviour change needing its own test, so it
was not folded into the merge-on-read work.

---

### The retracted facet-split argument — kept to two lines

It proposed `DeclareWholeTableRead(forAppends:, forRemoves:)`, on the grounds that `ReadSet.WholeTable` conflates
two facets the same record models separately (`Predicates` drives the ADD check, `Files` the REMOVE check) and is
read in exactly two places, so splitting it is a two-line upstream change. **Retracted** for the three reasons at
the top of this section — chiefly that `forRemoves: false` asserts something about the READ set that is really a
claim about the WRITE path, which is the *"a library must not claim on a host's behalf that it read less than it
declared"* objection upstream had already written down.

### The as-shipped property — offerable as-is, with a caution

`DeltaTransaction.cs` +26. Per the §2026-08-01 record it was wired LAST and nothing failed until it was;
`DeclareFilesRead` (#25) does **not** retire it, because declaring the files a scan touched drops the APPEND
rule, which is the phantom-row protection `serializable` exists for. Offer it only with that reasoning attached
— on its own it looks like a way to weaken isolation.

## isBlindAppend — an UPSTREAM OFFER and an OPEN FINDING (2026-08-01)

Written down before the context that produced it is lost. Two separable items: a fix that is ready to
offer, and a defect that is measured but not yet built.

### 1. UPSTREAM OFFER — **MERGED as #32, then CORRECTED TWICE (2026-08-01). Ours is retired.**

> **Read this box before the account below it**, which describes the offer as it was WRITTEN. Upstream
> merged it (`12b0d39`, #32) and then found two things wrong with it. Our copy on `fabricator-patches`
> is gone — dropped at the `upstream/main` merge and replaced wholesale, byte-identical.
>
> **`b8d1452` (#33) — "a guard that starts one line late, and stops one level short".** Two defects in
> what we shipped: (a) the `Exists` probe ran BEFORE the `try`, so a store that failed the EXISTENCE
> check still failed the caller — the exact outcome the fix existed to remove. The probe is gone
> entirely now (absence throws out of the read like any other unusable hint, and it saves a round-trip
> per snapshot build). (b) Guarding root KIND and field PRESENCE covers neither field TYPE nor the
> NESTED `v2Checkpoint`; seven shapes were measured still throwing, two of them the very
> "`TryGetProperty` throws on a non-object" trap our own §6 comment named — one level down. One
> try/catch around the whole read-and-decode subsumes all six guards and is 50 lines shorter. Our
> `data.Length == 0` fast path went with them, on the strength of our own note that it killed no test.
> **The lesson generalises: shape-by-shape guarding of a parse is the wrong instrument.** We enumerated
> the shapes an interrupted overwrite plausibly leaves; the seam is `ITableFileSystem`, a PUBLIC one, so
> a host can fail in ways this layer cannot enumerate — a better argument for the broad guard than the
> one we gave.
>
> **`b50f6bb` (#35) — the argument we both rested on was not implemented.** #32 and #33 justify
> returning null with "a reader can always recover by listing the log". **Nothing listed the log.**
> `SnapshotBuilder` read a null hint as `replayFrom = 0`, and `TransactionLog.ListCheckpointVersionsAsync`
> — the one method that could have found a checkpoint without the hint — had ZERO callers. That is not
> merely slow: once Delta's metadata cleanup has removed the commits a checkpoint subsumes, the surviving
> log carries no protocol or metadata action, so replaying it rebuilds nothing. Measured on three new
> `SnapshotBuilder` tests against the previous code: all three fail with *"Table has no metadata
> action"*. **The table is UNREADABLE, not silently short** — and OneLake, where we measured the original
> failure, is exactly where cleanup runs. `FindLatestCheckpointAsync(maxVersion)` now backs the fallback,
> including for a STALE hint (naming a deleted checkpoint) and one ABOVE a time-travel target.
>
> **Method note worth keeping: a fix whose justification names a fallback should verify the fallback
> exists.** Both we and upstream asserted "the protocol requires readers to fall back to listing" and
> neither checked that this code did. It is the same shape as the standing rule about instrumenting B to
> prove A never reaches it.
>
> Our host-side half (read small files in ONE request rather than Azure's lazy ETag-pinned ranged
> stream) is untouched by all of this and remains ours, as does `verify_delta_last_checkpoint`.

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

**⚠ THE CRUX FOR WHOEVER BUILDS IT — and it is an AUTOCOMMIT problem specifically, which is narrower and
more fixable than "the host never tells EW".** The shape that decides the design is
`INSERT INTO t SELECT … FROM t …` — an anti-join insert ("insert only rows not already there"), the
standard dbt-incremental / dedupe pattern. It **reads the target**, so it is NOT a blind append, yet it
emits **only AddFiles**, so any action-shape derivation calls it blind. It is therefore both the reason
the reading half needed fixing and the trap the writing half must not fall into: a wrong `true` makes
every other engine SKIP a `concurrentAppend` check it owes.

Whether EW can see the read depends on the transaction, and this is the part worth checking before
designing anything (measured 2026-08-01 by reading `DeltaCatalog.cs:1232`, whose scan-time recording is
gated on `_txnBuffer.IsExplicit(scanTxn)`):

| statement | recorded read set | can EW derive `isBlindAppend`? |
|---|---|---|
| `BEGIN; INSERT INTO t SELECT … FROM t …; COMMIT` | the scan stages a predicate, or `StageWholeTableRead` when nothing pushed | **yes, safely** — the read set is non-empty, so the anti-join case comes out "not blind" correctly |
| autocommit `INSERT INTO t SELECT … FROM t …` | **none** — reads are only tracked inside an explicit transaction | **no** — indistinguishable from `INSERT … VALUES`, and deriving would emit the lie |

So a derivation from `DeltaTransaction` state is legitimate for the **buffered/explicit** path and
unsound for **autocommit**. Two workable designs, in preference order: extend the scan-time read
recording to autocommit statements (it is the same call site, currently gated), which makes the
derivation uniformly safe; or leave autocommit alone and OMIT the field there, which is today's
behaviour and costs only spurious aborts.

Whichever is chosen, keep the asymmetry: a declaration may be DOWNGRADED to "not blind" by staged reads,
never UPGRADED to "blind" by their absence — because absence of evidence is exactly what the autocommit
row above is.

**Recommended order, updated again:** (1) offer the `_last_checkpoint` fix upstream — DONE, branch
`offer/last-checkpoint` on the fork; (2) fix the READING half — DONE, and it found that the writer's
declaration must outrank our inference in BOTH directions; (3) the prunable-predicate check — MOOT, see
above; (4) build the WRITING half — cleared to proceed, scoped as in the table above, with the live A/B
re-run as its gate.
### 2b. THE READING HALF AS SHIPPED — and it does **NOT** match Delta (established 2026-08-03)

The reading half is on `fabricator-patches` (`ConflictChecker.cs` +42) and is the piece being offered upstream.
Recorded here in full because the earlier note called it "FIXED" without qualifying what it fixes.

**What it does.** `IsBlindAppend(actions)` now scans for a `CommitInfo` carrying `isBlindAppend`; if the value is
an actual JSON boolean it is returned. Otherwise it falls through to `InferBlindAppend(actions)` — the previous
logic, renamed and byte-unchanged ("at least one add, and no remove/metadata/protocol").

**Three decisions, none symmetric:**

| case | behaviour | why |
|---|---|---|
| flag PRESENT | believed, in **both** directions | a declaration outranks any inference — including a declared `false` on an adds-only commit, which is the read-then-append case |
| flag ABSENT | fall back to the inference | almost every writer in the wild omits it (**EW included**), so "not blind" would conflict on ordinary appends |
| flag NON-BOOLEAN | treated as absent | a hint we cannot read is no better than one that is not there |

**How EW consumes the verdict** (unchanged by us):
`bool examineAdds = isolation == IsolationLevel.Serializable || !concurrentIsBlindAppend;`

**⚠ TWO DIVERGENCES FROM DELTA — do NOT describe this as "matching Delta".** Read from
`delta-io/delta` `ConflictChecker.scala` at the **`v4.2.0`** tag:

1. **The ABSENT case, and ours is the WEAKER one.** Delta is
   `val blindAppendAddedFiles = if (isBlindAppendOption.getOrElse(false)) addedFiles else Seq()` — **absent means
   NOT blind**, so those adds stay in `changedDataAddedFiles` and ARE examined even under WriteSerializable.
   Delta even computes the same shape predicate we infer from —
   `val onlyAddFiles = actions.collect { case f: FileAction => f }.forall(_.isInstanceOf[AddFile])` — and
   **pointedly does not use it** for blind-append. So our fallback is precisely the inference Delta declined to
   make. Ours is a deliberate back-compat choice, not parity: EW emits no flag itself, so `getOrElse(false)`
   would make ordinary EW-to-EW concurrent appends start conflicting.
   ⇒ **"FIXED" means fixed for DECLARED commits only.** The undeclared case is exactly as permissive as before.
2. **The metadata guard (uninvestigated).** Delta's WriteSerializable branch is
   `case WriteSerializable if !currentTransactionInfo.metadataChanged => winningCommitSummary.changedDataAddedFiles`,
   falling through to `changedDataAddedFiles ++ blindAppendAddedFiles` otherwise — so a metadata change in **our
   own** transaction re-examines blind appends. EW's `examineAdds` has no such condition. Not investigated; do
   not claim equivalence before it is.

**Delta's full usage, for reference:**
```scala
val addedFilesToCheckForConflicts = isolationLevel match {
  case WriteSerializable if !currentTransactionInfo.metadataChanged =>
    winningCommitSummary.changedDataAddedFiles
  case Serializable | WriteSerializable =>
    winningCommitSummary.changedDataAddedFiles ++ winningCommitSummary.blindAppendAddedFiles
  case SnapshotIsolation => Seq.empty
}
```

**Tests (7, in `ConflictCheckerTests.cs` +157):** `DeclaredNotBlind_OnAddsOnlyCommit_Conflicts_WriteSerializable`,
`DeclaredBlind_Passes_WriteSerializable`, `DeclaredBlind_OutranksInference_WhenCommitAlsoRemoves`,
`DeclaredBlind_StillConflicts_Serializable`, `AbsentFlag_FallsBackToInference`, `MalformedFlag_FallsBackToInference`,
`IsBlindAppend_SurvivesTheLogRoundTrip`. **The last is load-bearing**: without it the six verdict tests could all
pass while pinning dead code, because nothing would establish that a real commit's flag reaches the checker.

**The WRITING half is still absent — verified by grep, not memory:** `isBlindAppend` appears nowhere in our EW
tree outside `ConflictChecker`. We read the flag and never emit one, which is why Fabric Spark aborts against our
concurrent appends (absent ⇒ not blind in Delta, so our adds land in `changedDataAddedFiles` under both levels).

**When offering upstream, offer BOTH shapes and let Curt choose:** (1) as we run it — believe the flag, fall back
to the inference (backwards-compatible); (2) Delta parity — believe the flag, absent means not blind (safer, but
changes conflict behaviour for every flag-less writer, so it wants a release note).


## THE 2026-08-11 BUMP — onto a FRESH branch off `upstream/main` (`154f800`), because the patch set RETIRED

The branch model's stated goal, reached: **every one of our seven patches is upstream**, so the bump was not a
merge but a `git checkout -b fabricator-patches-v3 upstream/main`. What we carry now is ONE new patch, found
by running the tier rather than by reading the diff (§4).

### 1. The patch set is subsumed — established by CONTENT, not by PR state

`upstream/main` = `154f800`, **22 commits** ahead of the merge base `fa9b556`. Ours were +448/−30 across seven
files. Each landed re-cut:

| ours | landed as |
|---|---|
| `c46a70a` + `98cf471` + `1422ce6` isBlindAppend (read + write halves) | **#125** (`56ff960`) |
| `83133cd` vacuum below the table root / hidden names | **#121** (`107b858`) |
| `a3eadba` `LogCleanup` + the `LogCommitter` call | **#112** (`3f936cc`) |
| `83133cd` `delta.checkpointInterval` honoured as declared | **#110** (`5a1f280`) |
| `d3a1301` + `618f3dc` the interval reached one trigger of two | **#108** (`2cbc497`) |

⚠ **Checked semantically rather than by reading PR titles**, because a re-cut can drop the part that mattered:
`_checkpointInterval` resolved ONCE into a field (the `618f3dc` fix, whose whole point is that the two triggers
cannot drift), `InferBlindAppend` surviving as the fallback, the vacuum rule's **partition-column exception**
(the subtle half — a partition column may be named `_region`), and `LogCleanup.RunAsync` called from
`LogCommitter`. `LogCommitRequest.IsBlindAppend`'s doc comment and `CommitDataFilesAsync`'s parameter list came
back **byte-identical**, so no Bridge call site changed.

**Upstream extended two of them, which is the branch model's better half.** #125 did what we could not: measured
delta-spark's actual `isBlindAppend` across five commit shapes and confirmed the claim we had derived from
reading Delta's source, including that `INSERT INTO t SELECT ... FROM t` records `false` while emitting only
adds. **#116 fixed a gap in our `LogCleanup`**: it reclaimed commits and checkpoint bodies but left V2
**sidecars** — the megabytes, on exactly the large tables cleanup exists for — and never parsed Spark's
`<version>.crc` at all. We had documented the sidecar limitation; the `.crc` one we never saw.

### 2. What the other 17 commits are

- **Seven are a new Spark SQL expression parser** (#105/#106/#114/#118/#120/#123/#128, `7a6b381`), entirely
  additive under `EngineeredWood.Expressions/Sql/`. We consume only `Expressions.Predicate`. No impact.
- **Five are checkpoint work** — V2 sidecars read (#98), the checkpoint writer made callable (#99), kernel's
  V1-on-a-V2-table rule (#100), plus `CheckpointFormat`/`CheckpointPolicy` as new init-only
  `DeltaTableOptions` properties with defaults. `DeltaTable`'s public method surface is **unchanged** (audited
  base vs upstream: nothing added, nothing removed).
  - **A capability GAIN worth knowing: EW now decodes a Parquet-bodied V2 checkpoint.** `SnapshotBuilder`'s
    error text went from "This implementation decodes only the NDJSON V2 body" to naming a body that is
    *neither* of the two legal forms — i.e. a table another engine checkpointed in the V2 Parquet form used to
    fail to build a snapshot and now reads. It also detects a TORN multi-part checkpoint instead of silently
    loading a prefix.
- **Two are partition-path escaping** (#89, #95) — the only ones with teeth for us, §3.
- **Three are hygiene** — BOM removal, and an interop CI tier that *reported PASSED when it could not reach its
  toolchain*.

### 3. The entire compile cost was ONE new interface member, on three classes

`ITableFileSystem` gained a required `PathNameConstraints PathConstraints { get; }`. Measured by building the
Bridge against `upstream/main`: **exactly three errors, all that member, nothing else.**

It has **one consumer** — `DeltaTable.cs:4003`, deriving the Hive partition directory name — and under the
default `PartitionPathSpelling.SparkCompatible` **only `Win32ReservedCharacters` is read at all** (control
characters are escaped unconditionally there; the trailing-dot / dot-only / trailing-space flags are consulted
only under `Portable`). That collapses the design question: the answer matters iff it decides Win32.

| our filesystem | answers | why |
|---|---|---|
| `S3CommitFileSystem` | `None` | upstream's own measured S3 answer; stated directly rather than delegated to `_inner`, since this class is only ever constructed for an `s3://` root |
| `AdlsGen2TableFileSystem` | `NoControlCharacters \| NoTrailingDot` | upstream's own `AzureTableFileSystem` answer — same backend, different SDK surface. Inert under `SparkCompatible` |
| `DuckDbTableFileSystem` | per ROOT: `scheme://` gets the object-store union; local gets `Win32` on Windows, else `None` | DuckDB's `FileSystem` is a dispatcher, so this one object fronts a local volume or an object store depending on the path it was constructed with |

⚠ **The native write path is immune BY CONSTRUCTION and this is worth not re-deriving.** Under `native_write`
the partition directory is written by DuckDB's `COPY ... PARTITION_BY` and `add.path` comes from
`RETURN_STATS.filename`, so `DeltaPath.EscapePathName` is never consulted and the directory cannot disagree
with the log. Only the **codec** engine derives the name.

⚠ **On the codec engine this IS a behaviour change on a local Windows root.** At the merge base EW escaped a
fixed set and never touched `< > |` or a space; now those are percent-escaped (and `}` stops being escaped,
matching Spark's actual list). Correct — Win32 silently strips a trailing space from a component, so the old
unescaped form created `region=a b` and then failed to open the file under it — but a value like `a b ` now
lands in a different directory than it did before.

**Nothing in the tier covered it in either direction**, so it got its own gate:
`test/verify_delta_partition_escaping.test` (**56**, hermetic). ⚠ Its assertions are about the ROUND TRIP and
never the spelling, because the correct name is PLATFORM-DEPENDENT and sqllogictest cannot branch on the
platform — pinning `region=a%20b%20` would fail on Linux, where the literal name is right. What holds
everywhere is that the value survives and the file OPENS; on Windows that can only be true if the escaping
happened, and on Linux the section is a control that passes either way. ⚠ The count/sum is the load-bearing
assertion, not the value: Delta reads partition values from `add.partitionValues`, so `SELECT region` returns
the right string even when the file is unreachable. Mutation-tested — making `DuckDbTableFileSystem` report
`None` for a local root kills it at exactly section 2's CREATE, with section 1's ordinary-value control passing
first.

### 4. ⚠ THE REGRESSION THE TIER FOUND, WHICH READING THE DIFF DID NOT — `WriteAsync` now claims `isBlindAppend` ON THE CALLER'S BEHALF

`verify_delta_catalog_transactions` failed on the **codec** leg only. Root cause, measured:
`CommitWriteAsync` hardcodes `isBlindAppend: true` for any plain append (#125), reasoning that "a plain append
takes its rows from the caller and reads no file of this table to decide what to write". That is true of what
the library does and false of what the field means — Delta's `isBlindAppend` describes the **transaction**, and
a host with its own data plane that scanned the table and staged the result made a read EW never saw.

**#125's own `DeltaTransaction.IsBlindAppend` says exactly that, one file away** ("This library cannot derive it
for a host with its own data plane..."), and `WriteAsync` IS the host-facing surface. So the derivation upstream
got right on the staged surface was skipped on the auto-committing one.

MEASURED, codec engine, autocommit, `write_serializable`:

| version | statement | recorded |
|---|---|---|
| 2 | `INSERT INTO t VALUES (100)` | `true` — correct |
| 3 | `INSERT INTO t SELECT max(id) + 1 FROM t` | **`true` — WRONG** |

v3 is the anti-join incremental shape — reads the target, emits nothing but adds — i.e. the `insert_select_self`
row **#125's own interop tier singled out** as the one the recorded flag alone can tell apart from a genuine
blind append. So #125 taught the reader to believe the flag and, on this path, made the writer emit a false one
for precisely the shape it exists to distinguish. Under WriteSerializable another engine then SKIPS the
`concurrentAppend` check it owed — the unsafe direction, which #125 itself names as the one that matters.

**The patch (the whole of `fabricator-patches-v3`, +37/-13 in `DeltaTable.cs`, OFFER-READY):** `bool?
isBlindAppend = null` threaded through the two public `WriteAsync` overloads into `WriteCoreAsync` and
`CommitWriteAsync`, replacing the hardcode. Default null = absent = the pre-#125 behaviour for a caller that
says nothing. The overwrite branch keeps its hardcoded `false` — that one EW genuinely knows, because it reads
the active-file set to decide what to remove. `DeltaTransaction.EffectiveIsBlindAppend` is untouched: its
one-directional derivation is right and is the model this mirrors.

**Bridge side:** the parked-batch flush branch now passes the SAME `wasExplicit ? !pending.HasReads : null` the
files branch already passed. ⚠ That branch is not an edge case — it is every write on the codec engine, plus
identity / iceberg / pending-ALTER on the native one.

⚠ **Two engines now legitimately DISAGREE on a version neither of us declares for**: a CTAS's data write records
`false` on the codec engine (it takes the overwrite branch, which reads the active-file set — a fact) and
nothing on the native engine, where the files are DuckDB's and the commit goes through `CommitDataFilesAsync`.
Both are safe; Delta reads absent and `false` identically (`getOrElse(false)`).

**Gate: `verify_delta_catalog_transactions` section 42 restructured** (1042 to 1040 per leg) into a declaration
pin (42a: the two versions the host declares for, byte-identical on both engines), a positive control (42b:
exactly one blind claim), a completeness check (42c), and the SAFETY PROPERTY (42d: no other version claims
blind). ⚠ The old form pinned an exact five-row table including versions neither engine controls — that pinned
an ENGINE, not a behaviour, and is what an engine-doubled suite cannot do. It also gained the autocommit
anti-join, the actual unsafe shape, which the original section 42 never had (its autocommit row was
`INSERT ... VALUES`, genuinely blind). Mutation-tested by restoring the hardcode: dies at **42b** with three
blind claims where one is true.

Offer draft (not yet sent): the argument is upstream's own, from `DeltaTransaction.IsBlindAppend` and #125's
interop measurement.

## THE 2026-08-12 PIN ONTO UPSTREAM — the fork branch is GONE, the submodule points at clast-project

The 2026-08-11 bump cut `fabricator-patches-v3` off `upstream/main` carrying ONE patch. That patch is now
upstream (#137), so the set is empty **again**, and this time there was nothing left to put on a branch:
the submodule `url` moved from the `cmettler` fork to **`https://github.com/clast-project/engineered-wood`**
with `branch = main`, pin **`9d204d7`**. This is the branch model's stated goal in its final form — not
"a small patch set on top of upstream" but *upstream*.

⚠ **Getting back is one command, and `.gitmodules` records it**: `git remote add fork …`, carry the patch on
a FRESH `fabricator-patches-v<n>` off `upstream/main`, push it, and point `url`+`branch` at the fork for as
long as the set is non-empty. A pin nobody can fetch breaks every clone, and release tags pin EW shas forever.

### 1. The patch is subsumed — established by CONTENT, at six surface points

`upstream/main` = `9d204d7`, **14 commits** past `154f800`. Ours was `6dec2b4`, +37/−13 in `DeltaTable.cs`,
landed as **#137 (`d382aa1`)** — authored by us, merged by upstream. Verified against
`git show upstream/main:src/EngineeredWood.DeltaLake.Table/DeltaTable.cs` rather than against the PR state:

| our hunk | upstream |
|---|---|
| `WriteAsync(…, bool? isBlindAppend = null)` (public, batches) | :4810, forwarded :4812 |
| `WriteCoreAsync(…, bool? isBlindAppend = null)` | :4849, forwarded :4873 |
| `CommitWriteAsync(…, bool? isBlindAppend = null)` | :5279 |
| the `isBlindAppend: true` hardcode replaced by the parameter | :5320 |
| the overwrite branch KEEPS its hardcoded `false` | :5329 |
| the `IEnumerable` overload threads it | :7967, :7975 |

**Upstream extended it TWICE, and the first extension fixes a corruption our own patch made reachable.**
- **#143 (`bafc38e`)** — `rebaseSafe: isBlindAppend != false` (:5319). Ours passed `rebaseSafe: true`
  unconditionally, so `CommitOccAsync` would rebase a collision and re-commit the staged actions verbatim —
  *"valid precisely because nothing the commit read or removed was touched"*, which is exactly what a caller
  passing `false` has just denied. For `INSERT INTO t SELECT max(id)+1 FROM t`: scan, compute, declare false,
  a concurrent append lands, no conflict against `ReadSet.Blind`, rebase, commit a row derived from the OLD
  max. No error, wrong row. Upstream's words: *"worse than the bug #137 fixes, which only misled other
  engines — this one corrupts our own table."*
- **#144 (`a69870b`)** — `InferBlindAppend` now reads a `cdc` action as POSITIVE evidence of a read. Every
  other clause it had reads an ABSENCE, so *"nothing here says it read"* had become *"it did not read"*. It
  fires on the one shape that emits `add` + `cdc` and no `remove`: an insert-only MERGE on a CDF table,
  measured against delta-rs 1.6.2. Affects only commits that declare NOTHING, so not ours — but it is the
  fallback we rely on when READING another engine's log.

### 2. The compile cost was ZERO — and that is precisely when a bump looks finished and is not

14 commits, `dotnet build dotnet/Fabricator.Bridge -c Release`: **0 errors, 8 warnings — the same 4
pre-existing ones × 2 TFMs**, no new. The 2026-08-11 bump cost one new `ITableFileSystem` member; this one
cost nothing. Per that bump's own lesson the tier was run anyway, and §4 is what it was worth.

### 3. What the other 13 commits are

- **Five are the Spark expression work continuing** (#129/#130/#132/#134/#141, plus #135 docs) under
  `EngineeredWood.Expressions/`. We consume only `Expressions.Predicate`. No impact.
- **Four are CONSTRAINT ENFORCEMENT, and they turn a refusal into a capability** — #136 evaluates CHECK
  constraints and column invariants, #138 computes generated columns, #139 re-validates an UPDATE's
  post-image and recomputes generated columns, #140 lets a host commit a constrained table by declaring
  `constraintsEnforcedByCaller`. Before these, EW refused every table that had one.
  - ⚠ **No behaviour change for us, and the reason is the default.** `constraintsEnforcedByCaller` defaults
    to `false`, so *"a host that says nothing gets the refusal it got before"*. Our commit paths say nothing.
  - ⚠ **`SupportsExternalDataFileCommit` STOPPED LYING, and we read it in five places.** #140 found it
    reported `true` for a constrained table while the commit refused — sending a caller off to write files
    it could not then commit, the orphan its own docs promise to prevent. It is now `false` there, so our
    five consumers take their fallback instead. **UNTESTED HERE** — no suite builds a constrained table
    (fabricator never creates one: CHECK on CREATE is deliberately unsupported) — but the direction is the
    safe one, and it is a real capability gap now worth reconsidering: we could opt in and let a constrained
    Spark-authored table be writable.
- **One is #145**, a docs fix (net472 is Windows-only to RUN, not to build).

### 4. WHAT THE BUMP EXPOSED IN OUR CODE: we were throwing away the signal #143 added

⚠ **`FlushDeferredFilesAsync` and `DeltaGlobalTableFunction.WriteAsync` both caught EVERY
`DeltaConflictException` and replayed.** #143 makes a declared-`false` append surface as
`RebaseUnsafe` / `ConflictRecovery.Replan` — *"rebuild these actions, do not re-commit them"* — and both
loops swallowed it, reopened at latest, and re-committed **the same already-written data files**. Nothing in
either loop can recompute a row; the rows were computed by DuckDB before the call. So the corruption #143
closed one layer down was reopened one layer up, for `BEGIN; INSERT INTO t SELECT max(id)+1 FROM t; COMMIT;`.

- **NOT introduced by the bump — pre-existing, and invisible before it.** EW used to rebase internally and
  commit the same stale rows with no signal at all. What the bump adds is the exception; what we added is
  listening to it.
- Fix: `when (attempt < maxAttempts && isBlindAppend != false)` on both catches.
- ⚠ **Null still retries, deliberately.** It means the caller said NOTHING — autocommit records no reads —
  not that it read. Same permissive reading of absence as the flag itself.
- ⚠ **It costs the reopen that clears a concurrent `metaData` conflict** (§4b.1) for this one shape. Accepted:
  reopening and replaying a read-dependent append is wrong for a metadata conflict too, so the alternative
  was a silent wrong answer rather than a loud one.
- ⚠ **The read set is PER TABLE, not per transaction** (`_byTxn[txn][tablePath]`, *"the pushed predicate of
  every in-transaction scan of THIS table"*), so reading an unrelated table does NOT disable this append's
  retry. It IS over-broad in the conservative direction within one table — `BEGIN; SELECT * FROM t;
  INSERT INTO t VALUES (1); COMMIT;` declares false and loses the retry although the VALUES rows depend on
  nothing. That over-application is Delta's own: `isBlindAppend` is defined on the TRANSACTION
  (`readPredicates.isEmpty && readFiles.isEmpty`), so matching the spec here costs a spurious conflict and
  narrowing it would cost a wrong row.
- ⚠ **REASONED, NOT MEASURED.** sqllogictest drives connections sequentially, so no suite can produce the
  concurrent commit, and the window is microseconds — a multi-process rig would need many trials. What IS
  gated is that it changes nothing without contention. To measure it later: `scratchpad/s3_race.sh`'s shape
  against MinIO with a NAMED secret (a local Windows root is not multi-writer safe, §8.5), one writer running
  the anti-join insert inside BEGIN/COMMIT.

**Two comments went stale in the same place, and both justified a live line with a dead mechanism.** Both
said EW *"claims `true` on our behalf unless told otherwise"* — the #125 hardcode our own #137 deleted. The
line is still right and the reason is now #143's, so both were rewritten rather than removed: a comment that
explains a line by a mechanism that no longer exists is how a correct line gets deleted later.

### 5. Gates

Run in TWO passes on purpose, so the bump and the fix are separately attributable — the first pass ran
against the managed dir published BEFORE §4's edits:

| pass | what it isolates | result |
|---|---|---|
| bump only (`9d204d7`, no retry guard) | 14 upstream commits are behaviour-preserving | hermetic **69/69 — 6993** |
| bump + retry guard | the guard is inert without contention | hermetic **69/69 — 6993** |

Both **byte-identical to the pre-bump tier**, which is the whole claim. `verify_delta_catalog_transactions`
holds at **1040 per leg** — §42's declaration pins (v2 `true`, v3 `false`, autocommit absent) pass against
upstream's re-cut, which is the content check §1 asks for expressed as a test rather than as a diff.
