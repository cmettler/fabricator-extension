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
| [#6](https://github.com/clast-project/engineered-wood/pull/6) | variant shredding, write direction (his gap 8) | Parquet builds; +7 tests × {net8.0, net472}; all 17 `Variant*` green |
| [#7](https://github.com/clast-project/engineered-wood/pull/7) | `ReadAllWithMetadataAsync` — the `_metadata` locator | Table.Tests 643 × {net8.0, net472} |
| [#8](https://github.com/clast-project/engineered-wood/pull/8) | `StartTransaction(snapshot)` | 640 × 2 TFMs; **mutant** (ignore the arg) fails 2/4 |
| [#9](https://github.com/clast-project/engineered-wood/pull/9) | `StageAppTransaction` (idempotent-producer CAS) | 642 × 2 TFMs; **mutant** (drop the per-attempt check) fails exactly 1/6 |
| [#10](https://github.com/clast-project/engineered-wood/pull/10) | `StageDataFilesAsync` + `SetOperation` | 642 × 2 TFMs; **mutant** (ignore the identity bypass) fails 1/6 |
| [#12](https://github.com/clast-project/engineered-wood/pull/12) | **the isolation bound** on row-level reconciliation | 637 × 2 TFMs; **mutant** (ungate it) fails with *"No exception was thrown"* — upstream lets the second delete COMMIT under `Serializable` |
| [#13](https://github.com/clast-project/engineered-wood/pull/13) | `StageReadPredicate` / `StageWholeTableRead` | 640 × 2 TFMs; **two** mutants, each killed by its own test (1:1, neither covering for the other) |

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
3. **`UpdateBySelectionViaVectorsAsync`** — the one genuine CAPABILITY gain, and his own landing notes record
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
