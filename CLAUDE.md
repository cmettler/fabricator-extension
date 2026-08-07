# CLAUDE.md — project knowledge for `fabricator`

> Canonical project memory. Maintained in the repo (not in per-user agent memory) so it's
> easy to edit and shared across machines. Keep this current as the implementation evolves.

## What this is

`fabricator` is a **DuckDB extension** that connects DuckDB to **Microsoft SQL Server** by hosting a
C# layer (**CoreCLR, in-process**) and exchanging data + metadata as **Apache Arrow** over the Arrow
C Stream Interface (`ArrowArrayStream`). It is a direct, in-process replacement for the Arrow-Flight
transport used by the "Airport" extension.

Unlike the native-TDS sibling `mssql-extension` (`D:\repos\mssql-extension`, the compatibility
target), **all SQL Server I/O happens in C# via `Microsoft.Data.SqlClient`**; the C++ extension only
registers DuckDB functions and ingests Arrow. Full phased plan:
`C:\Users\c.mettler\.claude\plans\i-want-to-create-soft-crown.md`.

### THE FABRICATOR RENAME (2026-07-15, breaking — no aliases)

The extension + generic core was renamed **ArrowNet/`mssql_net` → `Fabricator`/`fabricator`** ahead of publish
(one branch: `refactor/fabricator-rename`). All the old `mssql_net_*` and `arrownet_*` names are **GONE** — no
back-compat aliases were kept (user decision). What is now `fabricator`:
- **Extension**: `LOAD fabricator`, `ATTACH … (TYPE fabricator)`, catalog-type string `"fabricator"`, entry
  `DUCKDB_CPP_EXTENSION_ENTRY(fabricator, …)`, artifact `fabricator.duckdb_extension`.
- **User functions**: `fabricator_query` / `fabricator_exec` / `fabricator_refresh_cache` /
  `fabricator_invalidate_cache` / `fabricator_version` / `fabricator_server_info` / `fabricator_functions` /
  all `fabricator_delta_*` (single registration each — the old dual `mssql_net_*`+`arrownet_*` aliasing removed).
- **C++**: namespace `fabricator`, dirs `src/fabricator/` + `src/include/fabricator/`, files `fabricator_*.cpp/hpp`
  (incl. `fabricator_extension`/`_secret`/`_storage`, `copy/fabricator_copy`), classes `Fabricator*` (catalog/
  schema-entry/etc.), internal scan fn `"fabricator_scan"`.
- **C#**: projects/assemblies/namespaces `Fabricator.Bridge` / `.SqlServer` / `.AnalysisServices` / `.DeltaRs` /
  `.Abstractions` / `.SamplePlugin`; bridge entry `Fabricator.Bridge.Bootstrap`; managed dir published to
  `build/release/extension/fabricator/fabricator/`.
- **Env vars / ABI constants**: `FABRICATOR_*` (`FABRICATOR_MANAGED_DIR`, `_DOTNET_ROOT`, `_BACKEND_ASSEMBLY`,
  `_PLUGIN_DIR`, `_LOG_LEVEL`/`_LOG_FILE`, `_DELTA_WRITE_DIR`, `_DELTA_PREFETCH`, `_ABI_VERSION`, `_META_*`, …).

**Provider-scoped names deliberately KEPT** (a setting/URI/secret/format names its PROVIDER, not the extension —
the DAX provider has `dax_*`, Delta `delta_*`): the ~35 SQL-Server settings stay `mssql_*` (`mssql_mars`,
`mssql_isolation_level`, `mssql_default_varchar_length`, …); the SQL `mssql://` URI shorthand; the SQL secret
**`TYPE mssql`** (was `mssql_net`); the SQL bulk COPY **`FORMAT mssql`** (+ `bcp`); `PROVIDER 'sqlserver'|'delta'|
'dax'|'deltars'|'engineeredwooddelta'`; secret FIELD names. **Gotcha for future edits:** `TYPE mssql` in an
ATTACH is the storage-extension keyword → must be `fabricator`; `TYPE mssql` in a CREATE SECRET is the secret
type → stays `mssql`. Renamed on the branch + validated (representative verify sweep green; loadable rebuilt).

### BRIDGE-CROSSING LOGGING (2026-07-15, additive, off by default)

Expanded the `FabricatorLog` (ILogger) coverage so a query/filter/mode/DDL/crossing is visible without a
profiler. Off by default (`FABRICATOR_LOG_LEVEL`, file sink `FABRICATOR_LOG_FILE`, + the `host_log` forwarding to
`duckdb_logs`). Categories: **`Fabricator.Bridge`** — EVERY *failed* ABI crossing logged centrally (a
`[CallerMemberName]` on `Bootstrap.SetError` records the op name + exception, so no per-handler code), plus
`open_catalog` (provider+options; connstr NEVER logged — password) / `get_metadata` (kind+args) control
crossings; **`Fabricator.Sql`** (new, in `SqlServerCatalog`) — every T-SQL statement: scans with the pushed
projection/WHERE/TOP/ORDER BY, the connection routing (pinned/pooled, read-your-writes, txn id, param count),
DML (`dml … DELETE/UPDATE …`), DDL (`ddl create/alter …`), and bulk (`bulk <table>: create/replace/
checkConstraints/options` + `N rows copied`); **`Fabricator.Delta*`** — already rich (bulk/write/scan mode /
native_filter / active·scanned·pruned files / resolved snapshot version). Logging OFF is byte-neutral (verify
suites unaffected). It immediately surfaced a pre-existing caught load-time `GetFunctionParamSchema` null-`fields`
WARN (benign — global functions pass).

**`Fabricator.Memory` — MEMORY MARKS ON THE HEAVY PATHS (2026-08-06, additive, off by default).**
`MemoryProbe.Mark(where, rows)` (`dotnet/Fabricator.Bridge/MemoryProbe.cs`, public so any backend assembly can
use it) logs `ws=` / `heap=` / `alloc=` / `rows=` at named points in the row-scaling paths. Enable with
`FABRICATOR_LOG_LEVEL=Debug`; grep the `Fabricator.Memory` category. **Why it is shipped rather than a
throwaway probe: the UPDATE grouping was built against a plausible story about where the memory went and the
story was WRONG** (it halved the heap and moved the process peak ~11%, because the dominant term was upstream
of the code being changed) — that was only findable by marking the working set along the path, so the marks stay.
- **⚠ Read `ws` and `heap` TOGETHER — they answer different questions, and reporting one as the other is the
  mistake this exists to prevent.** `heap` is what OUR allocations control and responds immediately to dropped
  references; `ws` includes DuckDB's own side of the statement and LAGS, because the OS does not take pages back
  when the GC frees objects. A change that halves `heap` and barely moves `ws` has bounded our share, not the
  statement's. `alloc` is CUMULATIVE, so its DELTA between two marks is the churn a stage caused whether or not
  it retained anything — a small heap with a huge alloc delta is a COPYING problem, not a retention one.
- Marks today: `delta update: set values parsed (BOXED)` / `arrow batch rebuilt`; `delta update mor: rowid map
  built` / `group flushed` / `committed`; `delta buffered update: group flushed`; `delta delete: deletion vector
  committed` (the **no-boxing DML floor**, i.e. the control to compare an UPDATE against) / `copy-on-write
  rewrite done`; `delta bulk: streamed to files, actions parked` / `batches PARKED until commit`;
  `delta flush: begin`; `bulk: load complete` (the provider-agnostic bulk seam — every INSERT/CTAS/COPY on every
  backend ends there, so it is the one mark worth comparing ACROSS backends); `mssql delete|update: rowid DML
  complete`.
- **⚠ Gated on `ILogger.IsEnabled` and it must stay that way** — `Environment.WorkingSet` queries OS process
  counters, and some marks sit in per-group loops. Never compute the values before the check. Verified: hermetic
  **66/66 — 6367** with the marks in, and an UPDATE with logging off is unchanged (3.25 s best of 5).

### Sync-over-async cleanup — DONE (convention: sync ABI wrapper blocks ONCE on an async core)

The Delta bridge is FULLY converted (DeltaReader/DeltaWriter/DeltaCatalog/DeltaGlobalTableFunction);
a whole-codebase scan found the per-await anti-pattern was DELTA-ONLY — the other bridges are
single-blocking-point wrappers or sync-native and must be LEFT ALONE. Do NOT treat a nonzero
`.GetAwaiter().GetResult()` grep count as remaining work. Adopt the wrapper→core shape for NEW code.
Full record (moved verbatim from here): [docs/ew-master-migration.md](docs/ew-master-migration.md).

### THE UPSTREAM STRATEGY (user goal, 2026-08-04 — full plan: [docs/ew-master-migration.md](docs/ew-master-migration.md) §THE STRATEGY)

Goal: run on **ORIGINAL upstream engineered-wood** with our needs met by high-probability PRs; maintain our own only
if that is impossible, and then **make our amendments clear IN THE CODE**.
- **THE VARIANT TRANSPORT HAS LEFT EW — DONE 2026-08-04, BOTH DIRECTIONS. Patch set 867 → **221 insertions
  across 4 files**, with ZERO variant divergence.** It was 60% of the patch and is OURS-BY-DESIGN (never offer
  it) — it just did not have to live in EW. The patch **replaced** upstream's `VariantColumnCoercion.Coerce`
  instead of running after it, which is why it had to normalise FOUR layouts (canonical / shredded / bare struct
  from an unannotated file / seam blob) keyed off the Delta schema. Letting `Coerce` run UNPATCHED and converting
  **canonical ⇄ blob in the BRIDGE** is one layout each way, detected by Arrow TYPE rather than by consulting the
  Delta schema. The C-interface crash never applied at that seam: EW hands us in-process .NET objects, so we hold
  a canonical `VariantArray` and flatten only on export. **This also supersedes an earlier note saying zero-patch
  was "gated on DuckDB #24157" — it never was.** What remains in EW: `ConflictChecker` 42 (offer-ready),
  `DeltaTable` 183, `DeltaTransaction` 26, `DeltaFilePruner` 4.
  - **READ half:** `Fabricator.Bridge/VariantTransport.cs` owns canonical⇄blob at **THREE** boundaries — the 5
    `DeltaReader` read exits (canonical→blob), the native-read seam (blob→canonical), and
    **`NativeParquetDataFileWriter`** (canonical→blob). ⚠ **The third was not in the plan; the variant suite
    caught it** at the OPTIMIZE section — the native writer feeds DuckDB's `COPY`, so it needs flattening too,
    **including the PEEKED batch whose schema builds the COPY** (converting only the stream would describe the
    file with a variant struct and then feed it blobs).
  - **WRITE half:** new `VariantMarker.ToCanonicalSchema`/`ToCanonicalField` + a list overload of
    `ToCanonical`. **Three FUNNELS** (`DeltaWriter.WriteAsync` schema+batches — which also covers the
    `OverwritePartitions`/`DynamicOverwrite`/`WriteAsync` trio inside it; `DeltaWriter.CreateAsync` schema;
    `DeltaCatalog.AlterTable`'s `Field?`, covering ADD COLUMN/FIELD buffered *and* immediate) plus per-site
    conversions for the CDF/`WriteDataFiles`/`UpdateRows`/`StageChangeData` calls, and **SCHEMA-ONLY** conversion
    where the STREAM must stay transport for DuckDB's COPY (`TryStreamCreateFiles`, `TryWriteStreamingCoreAsync`,
    the four `FromArrowSchema` calls).
    - **⚠ ONE funnel is right at `DeltaWriter.WriteAsync` and was WRONG at `BulkInsert`** — the difference is
      SINK COUNT, not depth. `WriteAsync` has one sink: its `native_write` variant reaches DuckDB's COPY
      *through* EW (`NativeParquetDataFileWriter` flattens back itself). **The ingest-funnel design was built and
      REVERTED — do not retry it**: that stream has TWO sinks with opposite needs (codec wants canonical,
      `native_write` hands the SAME stream back to `COPY` via `TryStreamCreateFiles`, wanting the blob). Symptom
      `complete_bulk failed: … INTERNAL Error: Attempted to access index 2 within vector of size 2` — the COPY,
      naming neither variants nor EW. It needed FOUR compensating conversions; needing that many to keep one
      funnel honest is the signal it is in the wrong place.
    - **⚠ THE 13-SITE ENUMERATION IN THE PLAN WAS INCOMPLETE, and the missed ones were the dangerous kind.** A
      grep of the CALLEE side (`OpenOrCreateAsync|SetSchemaAsync|AddColumnAsync|AddFieldAsync|ComputeAdd*|
      MergeSchemaAsync|Write*Async|Stage*Async|UpdateRowsAsync|ToDeltaField`) found **four more**: the STREAMING
      native-write path's `OpenOrCreateAsync` and its **two** `SetSchemaAsync(data.Schema)`, plus the
      copy-on-write `UpdateRowsAsync`. Three of the four hand EW a SCHEMA — the durable-corruption class below.
      **Enumerate by grepping the callee, never by listing call sites from memory.**
    - **⚠ The failure mode, restated because it is the worst in the variant surface:** with the `SchemaConverter`
      patch gone, a transport-marked field reaching EW maps to Delta **`binary`**, and a `metaData` commit is not
      revisable — a CREATE/CTAS/ADD COLUMN would record the wrong type DURABLY and SILENTLY, surfacing far away
      as an insert that cannot convert VARIANT to BLOB.
    - **⚠ The green intermediate CANNOT prove completeness** (the EW patch is still there to cover a missed
      site) — it separates "my conversions are right" from "the deletion broke something". Only the
      patch-removed run tests completeness. Both were run: variant **157** each time.
    - **⚠ Each of the four EW codec sites carried a SECOND `StripAnnotation`** that existed ONLY to undo the
      annotation `ToVariantArrays` re-introduced (upstream already strips on `physicalBatch` before the writer
      branch). The revert deletes both lines. **Read the surrounding upstream hunk, not just the line you added.**
    - **⚠ Latent, noted in `NativeParquetDataFileWriter`:** the canonical→transport conversion assumes
      `EmitVariantLogicalType` stays TRUE (its default; nothing of ours sets it). With it FALSE, EW's own
      `StripAnnotation` flattens the `VariantArray` to a bare struct BEFORE our writer sees it — indistinguishable
      from an ordinary struct — so the conversion would silently not fire and the COPY would write a struct
      instead of a parquet VARIANT. Unreachable today; the fix would be to pass the Delta schema in.
    - **One site the plan listed that must NOT be converted:** `ExternalTableRouting.cs`'s `Materialize(data)` —
      its batches go back INTO `DeltaCatalog.ExecuteUpdate`, the same dialect boundary as the C ABI. Only that
      file's `FromArrowSchema` needed the wrap.
  - ⚠ **The marker string is still `ew.variant_transport`, and the name now LIES** — engineered-wood knows
    nothing about it; the constant is `VariantMarker.ExtensionName`, ours alone. Renaming it is safe in principle
    (it is an in-memory discriminator that never persists — the Delta schema records `variant` and the parquet
    file carries the canonical annotation) but it must change in LOCKSTEP with the C++ ArrowTypeExtension
    registration in `src/fabricator/fabricator_variant.cpp`, so it is a C++-touching rename, deliberately not
    bundled into this pass.
  - Gates: EW Table.Tests **868 × {net10.0, net8.0, net472}** (871 − exactly the 3 deleted transport tests; the
    one whose SUBJECT is EW's — shred-on-write/reassemble-on-read — is already covered by upstream's own
    `Interop/VariantShreddingInteropTests`, so adapting it would have duplicated upstream rather than preserved
    ours), hermetic **63/63 — 5686** AND service **44/44 — 1458**, both byte-identical to baseline, variant **157**. ⚠ Runtime cost is one extra
    materialisation per batch on variant tables — UNMEASURED; and the live Spark/kernel round trip has NOT been
    re-run since.
- **Sequencing: offering and building are NOT sequential** — the branch model does both, proved by the 2026-08-02
  bump (three of eight upstream commits were our own offers coming back re-cut). Stay on `fabricator-patches` and
  keep building. **Pull ONE thing forward: MARK the amendments** (`// [FABRICATOR-PATCH: OFFER-READY | OFFERED #n |
  OURS-BY-DESIGN]` + why + what retires it). `DeltaTable.cs` alone has **27 unmarked hunks**; today "is this ours?"
  is answerable only by `git diff upstream/main`. Marking needs nobody's agreement and turns the eventual
  fork-vs-upstream decision into a grep.
- **Offer order** (probability × independence): ~~(1) public overload of `WriteChangeDataFilesForAsync`~~ —
  **RETIRED 2026-08-04, NEVER SENT, and this is the branch model working rather than a change of mind.** The
  hoist made `StageChangeDataAsync` callable at statement time, so our 45-line public duplicate is DELETED and
  there is nothing left for the overload to serve. The lesson generalises: an offer that exists to work around
  our OWN architecture should be re-derived after each architectural slice, because the cheapest way to retire
  a patch is to stop needing it — cf. `RowUpdateMode`, solved by removal. Full record:
  [docs/delta-transaction-hoist.md](docs/delta-transaction-hoist.md) §2. So the live list is:
  (1) `ConflictChecker` isBlindAppend — 42 lines, internal, 7 tests, but
  present BOTH shapes; (2) `ExemptRowLevelFromWholeTableRead` — **only after the §2.2 fix**, and pitched as a
  DEPARTURE not an inconsistency; (3) a transaction that can CREATE a table — a design conversation; and
  **(4) LOG CLEANUP — CONFIRMED ABSENT 2026-08-07 BY A CONTROLLED EXPERIMENT, and worth offering because it
  is a plain SPEC GAP rather than a fabricator-shaped need.** engineered-wood accepts and stores
  `delta.logRetentionDuration`, never reads it, and never deletes a commit a checkpoint subsumes.
  - **THE MEASUREMENT (this is what settles it — the two greps below do not):** two local tables, 26 commits
    each, two checkpoints each; one with `delta.logRetentionDuration = 'interval 1 seconds'`, one with the
    property unset. **28 commit JSONs vs 27** — the difference being only the extra `set_tblproperties`
    commit. With a one-second horizon and a checkpoint at v20, an implemented cleanup would have reclaimed
    ~20 files. Nothing was reclaimed, and the property changed nothing.
  - Corroborating, and the stronger of the two static reads: **`IntervalParser`'s ONLY call site is
    `DeltaTable.DeletedFileRetention`**, which reads `delta.deletedFileRetentionDuration` — the VACUUM knob
    for DATA files. The parser whose doc comment names `logRetentionDuration` is wired only to the other one.
  - ⚠ **MY FIRST TWO ARGUMENTS FOR THIS WERE BOTH INADEQUATE, and the sequence is the point.** (a) A
    BACKWARDS GREP for `Cleanup*` / `DeleteAsync`-on-a-log-path — the very search pattern this file records as
    having produced a wrong "we never reach `CommitOccAsync`" conclusion; a cleanup under an unguessed name
    would not appear. (b) Observing `lake/t` go 148 → 151 across an OPTIMIZE + VACUUM — which **does not
    discriminate**, because Delta's default retention is 30 DAYS and every file in the rig is minutes old, so
    a correct implementation would have done nothing either. Only forcing the horizon to one second separates
    the hypotheses. **A negative result needs a control that would have produced a positive.**
  - Cost, MEASURED: ~10 ms per dead commit file per scan on S3, so an hourly dbt model adds ~90 s of pure
    metadata to every scan after a year. The offer needs nothing of ours, is self-contained, and is what the
    spec already says should happen — the three properties that make an offer land. **Never**
  offer the variant transport.
- **Decision gate:** drop the branch for a `PackageReference` when the patch set is variant-transport-only AND
  #24157 is fixed. Until then the branch is correct, not a failure.

### THE EW CLAST-MASTER RE-PIN (2026-07-22 — the current engine; full record: [docs/ew-master-migration.md](docs/ew-master-migration.md))

The engineered-wood submodule pin moved from our long-lived fork lineage (`99e2c3a`) onto
**clast-project/engineered-wood master (`e48f449`, Curt's PR#4-parity landing) + the additive
`fabricator-patches` branch** (7 commits, pushed to the cmettler fork, pin `7fecc2b`;
`.gitmodules` `branch = fabricator-patches`). The strategy: fabricator-specific needs live as a
SMALL upstreamable patch set ON TOP of clast master — never a fork again — so future EW bumps are
merge-upstream-into-fabricator-patches + re-pin. **⚠ That upstream branch is now
`upstream/main`, NOT `master`** — upstream renamed it (`8caf8d8`) and the stale `upstream/master`
remote-tracking ref still resolves, so a merge of it silently lands on an abandoned branch.
**Current pin: `3794fe4`** (the variant-transport removal in both directions, then the
`WriteChangeDataFilesAsync` deletion the hoist enabled; ⚠ the line here read `d9d204b` for two commits after
that stopped being true — **`git ls-tree HEAD engineered-wood` is the authority, this prose is not**).
**Patch set MEASURED 2026-08-04 (re-measured after the CDF deletion, `git diff upstream/main --stat -- src/`):
+175 / −34 lines across FOUR files** — `DeltaTable.cs` 137, `ConflictChecker` 42, `DeltaTransaction` 26,
`DeltaFilePruner` 4. (It was +221 before that deletion, and +867 across 8 files on 2026-08-03; the variant
transport was ~60% of that and has since left entirely — §THE UPSTREAM STRATEGY.)
- **⚠ THE MERGE-ON-READ UPDATE LEFT EW (2026-08-03) — the audit's "DO NOT MOVE IT TO THE BRIDGE" verdict was
  WRONG and is reversed. Full record: [docs/ew-master-migration.md](docs/ew-master-migration.md) §THE
  `*BySelection*` QUESTION.** `UpdateBySelectionViaVectorsAsync` + `BuildInlineDeletionVectorsAsync` (218 lines)
  are GONE; the Bridge now COMPOSES the same effect from PUBLIC, UPSTREAM API —
  `ReadRowsAsync(RowSelection, …)` + `WriteDataFilesAsync(…, materializedRowIds:)` +
  `DeltaTransaction.StageRowDeletesAsync`/`StageDataFilesAsync`/`StageChangeDataAsync` + `CommitAsync`. The
  error to not repeat: the audit asked whether the METHOD BODY could be RELOCATED (which does need EW's
  `internal` DV core) instead of whether the EFFECT could be COMPOSED — and `StageRowDeletesAsync` is a PUBLIC
  door onto exactly that core. The **buffered path already did it this way**, so autocommit was the outlier.
  Gains, because the retired method CAS'd on `expectedVersion`: the OCC retry loop was *disabled* by that
  argument, no DV edits were recorded (⇒ **no row-level reconciliation on this path at all**), and the table's
  own `delta.isolationLevel` was ignored — now honoured via a shared `DeltaReader.EffectiveSerializable` that
  `DeltaCatalog` delegates to, resolved from the config the path already reads (no extra `_delta_log` LIST).
  ⚠ The concurrency gain is a MECHANISM claim, NOT measured — see the substrate finding below.
  ~~**`WriteChangeDataFilesAsync` (45 lines) STAYS for now**~~: **DELETED 2026-08-04 by the hoist** — the
  buffered CDF path now calls `StageChangeDataAsync` at statement time, so its second consumer is gone.
  ⚠ The grep lesson still stands, and applied at deletion time too: a grep of EW alone called it
  self-contained; **the second consumer was in the Bridge — grep both trees.**
  - **`StageChangeDataAsync` does not fit the buffered path** (asked 2026-08-03) — ⚠ **SUPERSEDED
    2026-08-04: it fits, and all three reasons below were CONSEQUENCES OF OUR OWN BUFFERING rather than
    properties of the API.** The hoist created the transaction at statement time, which dissolved reason 1;
    reason 2 was backwards (`StageChangeDataAsync` writes the parquet IMMEDIATELY, so rows were never going
    to be held — and the hoist holds LESS, since `PendingCdc` is gone); reason 3 dissolved with the parking
    structure it names. Kept verbatim because the SHAPE of the error is the reusable part: three defensible
    objections, each true about the code as it stood, none about the API.
    [docs/delta-transaction-hoist.md](docs/delta-transaction-hoist.md) §2. Original text: it is a method ON
    `DeltaTransaction` and the buffered path has none at statement time (created at FLUSH,
    `DeltaCatalog.cs:3653`); and deferring the call to flush would hold the pre/post-image ROWS in memory until
    COMMIT, which eager CDC capture (slice C2) exists to avoid. It also RETURNS NOTHING — `StageInternal` files
    the actions into that transaction — while we need the `CdcFile` list back to park on `pending.PendingCdc`.
    - **⚠ A THIRD REASON RECORDED HERE WAS FALSE, exposed by asking "would an OCC retry rewrite the CDF
      parquets?" (answer: NO).** It claimed an early transaction "fights the flush's OCC retry, which reopens at
      latest". **`FlushDmlTransactionAsync` has NO retry loop** — the retry is inside `txn.CommitAsync`, which
      re-rebases from the ORIGINAL staged actions and never re-runs staging (`DeltaCatalog.cs:3777`). The false
      reason described a hand-rolled loop that moved into EW, and came from mistaking the OTHER retry loop
      (`:2720`, around `CommitDataFilesAsync`) for the flush's. ⇒ an early-created, long-lived transaction per
      (txn, table) COULD use `StageChangeDataAsync`; that is an ARCHITECTURAL change (one open EW transaction per
      table for the DuckDB transaction's life, each pinning a snapshot, all aborted on ROLLBACK), not an
      impossibility. **Frame the offer as "the smallest change that preserves the current architecture."**
  - ⚠ **And EW's own autocommit DML does NOT use `StageChangeDataAsync` either** — `DeleteRowsAsync`/
    `UpdateRowsAsync`/appends call the internal `ChangeDataFeed.CdfWriter.WriteAsync` DIRECTLY (7 sites) and fuse
    the actions into their own commit; `StageChangeDataAsync` is HOST-facing (its only in-EW use is its own body,
    `DeltaTransaction.cs:499`). The one autocommit path using it is OURS. Don't state those as one mechanism.
  - ~~**THE OFFER: make the internal PLURAL public — upstream already HAS it.**~~ **RETIRED 2026-08-04, NEVER
    SENT — the hoist deleted the thing it existed to serve.** `WriteChangeDataFilesForAsync`
    (`internal`) IS the partition-splitting plural that `StageChangeDataAsync` calls, so our 45 lines were a
    public duplicate of it; the offer was to expose a public overload without its `internal WrittenFileLedger?`
    param. With CDF staging into a statement-time transaction, the Bridge calls `StageChangeDataAsync` directly
    and **our duplicate is gone** (see the hoist entry under "Next up"). Nothing about the offer was wrong — it
    stopped being needed, which is the cheapest way to retire a patch and the same outcome as `RowUpdateMode`.
    Two facts from it are still live and worth keeping: it takes `rowIds`/`rowCommitVersions`, i.e. **the CDF
    identity our feed still leaves NULL** (now MEASURED and worse than "NULL" — the buffered and autocommit
    paths DIVERGE, [docs/delta-transaction-hoist.md](docs/delta-transaction-hoist.md) §6); and ⚠ **do not
    hand-roll the partition split** — the risk is Delta's partition-value STRING ENCODING matching what EW
    writes for data files, and the Bridge only ever READS those values from `RETURN_STATS.partition_keys`,
    never formats them. (This had already superseded an earlier recommendation to make `PartitionUtils` public.)
  Gates: hermetic **63/63 — 5686** (byte-identical to pre-change ⇒ behaviour-preserving), EW Table.Tests
  **877 → 872** (exactly the 5 tests of the retired member). **`RowUpdateMode` is SOLVED BY REMOVAL and is OFF the
  offer list** — no divergence left for it to retire and no need for it, so do NOT bring it; spending credibility on
  a request we do not need weakens the ones we do.
- **⚠ LOCAL WINDOWS ROOTS ARE NOT MULTI-WRITER SAFE — measured 2026-08-03, INDEPENDENT of the above, found while
  trying to measure it ([docs/delta-transactions.md](docs/delta-transactions.md) §8.5).**
  `fabricator_fs_write_probe` on `D:\` reports `EXCLUSIVE_CREATE` **succeeding on an existing file** AND
  `MoveFile` **overwriting** its target ⇒ neither commit primitive is conditional. Measured: 6 writers × 3
  autocommit INSERTs × 50 rows ⇒ **400 of 900 rows landed, 500 silently lost, every writer exited 0** — the
  secretless-S3 shape. Second symptom on the same cause: a concurrent reader parses a commit **mid-write**
  (`BytePositionInLine: 9` is the length of `{"remove"`), so a log re-read fails with torn JSON rather than a
  conflict. The §8 table's "Local POSIX" row was never wrong — `O_EXCL` is a POSIX guarantee — but nothing said
  anything about Windows and "local" reads as covering it. **Consequence: a local Windows root cannot host any
  multi-writer experiment** (the substrate swamps both legs of an A/B); use OneLake/abfss, S3 with a NAMED
  secret, or WSL. Single-writer behaviour is unaffected (the whole hermetic tier is green).
  - **⚠ AND `fabricator_fs_write_probe` HAS A FALSE-POSITIVE MODE — FOUND, NOT FIXED (§8.5a).** Aimed at a path
    whose parent does not exist, `exclusive_create_existing_fails` reports **`true` ("put-if-absent works")**
    because the exclusive open threw for a MISSING DIRECTORY — the verdict is recorded as "it threw"
    (`fabricator_fs_spike.cpp:534`) without checking that the file exists at all. It fails in the UNSAFE
    direction. `create_directory` and `file_exists` likewise report `ok=true` while their own detail says they
    failed (`run()` records ok = "did not throw", and those two RETURN a failure message instead of throwing).
    Fix is small (gate the verdict on `FileExists`, make the two steps throw on their invariant) and was
    deliberately NOT taken in a C#-only pass. **Found by running the README example verbatim** — the
    "run the README's SQL before committing it" rule paying for itself. Until fixed: confirm `create_directory`
    and `write_create` are both `true` before believing the verdict.
**`MetadataPredicate` (182 lines) is GONE** — the
predicate lowering was unreachable from a rowid-keyed host (its job is to PRODUCE the `RowSelection` we
already hold), so it cost divergence for a path we can never take; removing it does not foreclose OFFERING
it, since `offer/*` branches cut off `upstream/main` and history keeps the file. What the patches carry: the **`DeltaTable.PlanFiles`
planning API** (proposed to Curt 2026-07-25, endorsed, and BUILT by us 2026-07-26 — it REPLACED the
earlier `DeltaFilePruner`-public patch, which is retired; full record in the `PlanFiles` subsection below);
create-time `configuration`/`preAssignedSchema`/`materializedRowIds` params; rowid read-back
`rowIdsOut` correlation + derived-id fallback + CoW CDF capture + partition-aware cdc writes +
DV-aware CDF inference; schema-evolved compaction fixes; the **narrow-int parquet write-corruption
fix** (1-/2-byte Arrow arrays reinterpreted at the 4-byte physical width — silent corruption,
pre-existing, upstream-candidate); pass-through source-field relabel fixes (WidenBatch/
BackfillMissingColumns); and the **variant TRANSPORT** (`SchemaConverter.VariantTransportExtensionName
= "ew.variant_transport"`, `VariantTransport` blob⇄`VariantArray` at EW's host boundary,
`DeltaTableOptions.VariantTransportBlob` — EW's INTERNAL model is now the canonical
`arrow.parquet.variant` `VariantType`; the Bridge sets the option in `DeltaWriter.Options()` and
converts advertised schemas via `VariantMarker.ToTransportSchema`). Bridge-side migration:
`IDataFileRewriter` retired (EW owns rewrite semantics; only the encoding seams remain), UPDATE on
the host-join `UpdateByRowIdsAsync(RecordBatch)` + a composed merge-on-read (`MergeOnReadUpdateAsync`
in DeltaReader), decimal widening via `DecimalOutput=Decimal128` read option, writer seam
`IAsyncEnumerable`. **Capability gain: pure-codec variant REWRITES work** (the fork gated them);
**the one capability regression is CLOSED (2026-07-23, EW-only on fabricator-patches): buffered DML
through a concurrent OPTIMIZE/rewrite now REMAPS again** — clast master already shipped the full
stable-id remap (`RemapRowLevelDeletesAsync`, its "Layer 3 (B)", serving autocommit +
`DeltaTransaction`); the buffered surface's `RebaseDvDmlActionsAsync` just threw on a vanished path.
Now it collects rewritten-away touched paths as `DeleteDvEdit`s and routes them through that SAME
remap (row tracking required — without it the clean rewrite conflict remains; the remap's new-file DV
pairs keep their own baseRowId, no HWM impact; the fork's bespoke `RemapRowsAcrossRewriteAsync` stays
retired). No Bridge/ABI change. EW BufferedTransactionTests +3 (Table.Tests 421);
`verify_delta_row_level_concurrency` back at the fork-era 70 (§5 buffered DELETE + §8 buffered UPDATE
compose through OPTIMIZE; §9 = precise "row-level conflict"); regression transactions 941 /
row_tracking_virtual 299 / optimize 40 / dv_default 58 / update 63 / delete 28 green. This closes
PR #4's "Known follow-up". Original migration validation: 49/49 delta suites at
full counts (variant now 144), EW suites green, and the LIVE OneLake/Spark round-trip incl. row-id
parity both directions + Spark decoding codec-written variant. **Fork-era EW notes below this point
are HISTORICAL** — they describe the retired fork lineage; the mechanisms survive but live in the
fabricator-patches shapes above.

### THE 2026-08-02 BUMP — DONE, pin `3b95599` (full record: [docs/ew-master-migration.md](docs/ew-master-migration.md) §THE 2026-08-02 BUMP)

Eight upstream commits the day after the last bump — **#40, #41, #43, #46, #48, #49, #50, and #39 which is
OURS, merged.** Five conflicted files, every one exactly where one of our three superseded patches sat;
nothing else conflicted. Three of the eight ARE our offers taken and re-cut, so **the conflicts were the
cost of being ABSORBED, not of having diverged** — the branch model paying out. Gates: EW Table.Tests
**875/875 × {net10.0, net8.0, net472}**, hermetic **63/63 — 5640**, service **44/44 — 1424**.

- **#43 supersedes our #37 and subsumes #38**: `expectedPrevious`/`requireAbsent` become the
  `AppTransactionPrecondition` union. **⚠ `Expected is null` maps to `Absent`, NOT the union's `None`** —
  both compile, and `None` writes unconditionally, so a replayed first batch of
  `fabricator_delta_set_transaction_version` would commit TWICE and rewrite the recorded version with the
  same value, leaving nothing in the table to say so. The default is the dangerous answer.
- **#48 deleted `sourceRowTrackingOut`**; both row identities now arrive as COLUMNS via `DeltaRowMetadata`.
  Ask for `RowTracking` ONLY when the table has it — the column form is REFUSED where the out-param quietly
  returned nulls. Our gate is `TxnDmlProfile.MaterializeRowIds`; that alignment is now load-bearing.
- **#50 refuses a write carrying an undeclared column.** It does not fire on us: the Bridge asks for
  metadata columns in three places, two of which are scans feeding DuckDB and the third strips.
- **#46 + #49 give `DeltaTransaction` `AbortAsync`/`IAsyncDisposable` and make the six auto-committing
  paths collect their own orphans.** ⚠ **#49 also closed a data-destruction window #46 opened** (a commit
  that landed but threw could have its live files deleted), so adopting `await using` on our buffered flush
  is safe only from #49 onward — **deliberately NOT taken in this bump**, since "rollback leaves invisible
  orphans for VACUUM" is documented behaviour and a bump is the wrong place to change behaviour.
- **#41 cuts `_delta_log` walks per snapshot build from four to one** — which re-prices the
  [delta-snapshot-caching](docs/delta-snapshot-caching.md) decision gate downward. Its "4 constructions per
  statement" was 16 listings and is now 4; **any future measurement must be retaken against this pin.**

### THE 2026-08-01 BUMP — DONE, onto **`upstream/main`** (full record: [docs/ew-master-migration.md](docs/ew-master-migration.md) §THIRD ATTEMPT)

The pin moved from `7fecc2b` onto upstream's #15 slices (#18–#22) **plus the four commits that landed the
same day** (#24, #25, #32, #33, #35). The mechanical part went as measured — the read/DML overload families
collapsed into `ReadAsync(DeltaReadOptions)` / `DeleteRowsAsync(RowSelection, RowDeleteMode)` /
`UpdateRowsAsync(RowSelection, …)`, and `DeltaTransaction`'s `Stage*` split into `Stage*`/`Require*`/
`Declare*` **by RETRY CONTRACT**, so each call had to be re-classified rather than renamed
(`StageReadPredicate`→`DeclareRead`, `StageWholeTableRead`→`DeclareWholeTableRead`,
`StageAppTransaction`→`RequireAppTransaction`, `SetOperation`→the `Operation` property). Gates:
EW Table.Tests **828/828 on net8.0 AND net472**, DeltaLake.Tests 248, hermetic **63/63 / 5639**.

**Five things this cost real time, none of them in the measurement:**

1. **`upstream/master` IS STALE — the live branch is `upstream/main`** (`8caf8d8` renamed it). A merge of
   `master` lands on a branch upstream has moved off, and `upstream/HEAD -> upstream/master` still resolves
   locally, which is what makes it quiet. **`git fetch upstream` and read `upstream/main`.**
2. **The merge SILENTLY DROPPED one of our patches** — `UpdateBySelectionViaVectorsAsync`, the merge-on-read
   UPDATE (upstream has NO DV mode for UPDATE; it always rewrites). Found by tripping over it, and the
   reflex fix would have converted five MoR tests into copy-on-write tests. **Run the surface audit FIRST:**
   diff the public surface of the pre-merge `DeltaTable` against the merged one, then classify each absent
   method by whether it was in the MERGE BASE — that separates upstream's consolidation from our losses
   (14 absent → 10 upstream's, 3 ours-with-a-successor, 1 lost). Command in the doc.
3. **Building the Bridge is what finds the host's needs; reading the diff is not.** Two consolidations
   dropped things only a caller notices → additive patches: a **`DeltaRowMetadata` parameter on
   `ReadRowsAsync`** and `AppTransactionPreconditionException`.
   - **⚠ And the first one was the WRONG SHAPE at first, which one question exposed.** I added a bespoke
     `rowAddressesOut` out-param; `DeltaRowMetadata.RowAddress` had been a first-class metadata kind all
     along, emitting exactly that packed address as a COLUMN on `ReadAsync`/`ReadChangesAsync`. The
     address was never missing from the library — it was missing from ONE read, which already stood out by
     carrying a bespoke `sourceRowTrackingOut` duplicating `DeltaRowMetadata.RowTracking`. **Standing rule:
     before adding a parameter, read the enum/options type the neighbouring methods already accept.** The
     out-param compiled, passed, and was defensible in isolation; it was wrong only relative to a
     convention one file away.
4. **A COMPILING Bridge is not a migrated one.** Upstream documents `RequireAppTransaction`'s
   `expectedPrevious: null` as "do not check" where our `fabricator_delta_set_transaction_version` means
   "must not exist yet" — a replayed first batch would have gone from a failed CAS to an unconditional
   write, **duplicating data** on a user-facing exactly-once mechanism (fixed by an additive
   `requireAbsent`). And our loud unresolvable-ordinal error is right for every DML path and WRONG for the
   CDF read-back. Both survived the compiler; the suites caught them.
5. **`ExemptRowLevelFromWholeTableRead` was wired LAST and nothing failed until it was.**
   `verify_delta_row_level_concurrency` §11 was written before the migration for exactly that reason
   (82 → 93). **`DeclareFilesRead` (#25) does NOT retire it** — declaring the files a scan touched drops the
   APPEND rule, which is the phantom-row protection `serializable` (our default) exists for.
   - **⚠ AND `DeclareWholeTableRead` DOES NOT REPLACE IT EITHER — that one is UPSTREAM and ours NARROWS it**
     (upstream's own doc calls the narrowing a downstream proposal it has not implemented). They are a pair.
     **OFFER OUR PROPERTY AS-IS**: it is NOT an API inconsistency but a **DEPARTURE** from Delta's
     `concurrentDeleteRead` rule (honoured at BOTH levels by Delta and EW; Spark gates only `concurrentAppend` on
     the level), and it is already the *"explicit per-transaction opt-in rather than an inference"* shape upstream
     said it would require. **A `DeclareWholeTableRead(forAppends:, forRemoves:)` "facet split" was proposed here
     and is RETRACTED** (2026-08-03): writing the call site showed we cannot make the judgment it hands us
     (`ReadWholeTable` is set by ANY unfiltered scan, `DeltaCatalog.cs:1412`), and `forRemoves: false` is precisely
     the *"claim on a host's behalf that it read less than it declared"* upstream objected to. It was the reframe
     pass's own caveat violated — a semantics request dressed as an inconsistency.
   - **⚠ OVER-BROAD OPT-IN IN WHAT WE SHIP (reasoned, NOT measured, untested) — but INERT UNDER OUR DEFAULT.**
     The Bridge sets the opt-in **UNCONDITIONALLY** (`DeltaCatalog.cs:3775`) while the departure is justified by
     ROW-LOCALITY. EW's gate is three-way (`DeltaTable.cs:2404`):
     `exempt && rowLevel && isolationLevel != Serializable` — and since the 2026-08-01 flip our default IS
     `serializable`, so the flag is **ignored by default**; do not call this broken out of the box. It bites only
     on `write_serializable` (ATTACH option or table property) plus a txn staging DV deletes, where
     `BEGIN; SELECT avg(x) FROM t; DELETE FROM t WHERE x > 42; COMMIT;` gets exempted although the row-level
     validation covers only the REMOVED rows, not a threshold derived from a whole-table read. Fixing it needs
     provenance on `ReadWholeTable` (DML's own scan vs an arbitrary SELECT), which the buffer lacks today — a
     behaviour change with its own test, deliberately not folded into the merge-on-read work.

**⚠ OUR `_last_checkpoint` OFFER WAS MERGED (#32) AND THEN CORRECTED TWICE — ours is retired, upstream's is
in.** #33 found the `Exists` probe ran BEFORE the try (so the fix did not fix the case it was written for)
and that guarding root kind + field PRESENCE misses field TYPE and the nested `v2Checkpoint`; one try/catch
around the whole read-and-decode replaces all six guards. #35 is the important one: **the argument both
fixes rested on — "a reader can always recover by listing the log" — was never implemented.** Nothing
listed the log; `SnapshotBuilder` read a null hint as `replayFrom = 0`. Once Delta's metadata cleanup
removes the subsumed commits, replay rebuilds nothing and the table is **UNREADABLE** (measured: *"Table
has no metadata action"*), which is squarely the OneLake shape we were fixing for. **Standing lesson: a fix
whose justification names a fallback must verify the fallback exists.**

Also: **#24 independently CONFIRMS our live isolation measurement** — delta-spark 4.0.0 rejects
`delta.isolationLevel='WriteSerializable'`; it is a Databricks extension and OSS Delta has one level, which
is what we found against Fabric Spark 4.1.1 by another route. It further found EW and Delta agree on the
OUTCOME and disagree on the LABEL (Delta reports `ConcurrentAppend` where EW reports
`ConcurrentDeleteRead`).

**⚠ #36 came in on the same bump and carries TWO USER-VISIBLE behaviour changes** (*"a replay that skipped
a version said nothing about it"* — the follow-on to #35). `SnapshotBuilder` used to apply whatever commits
it happened to find, skipping a missing or unreadable version **in silence** (once via a literal
`catch { /* Skip missing commits */ }`) while still labelling the result with the target version. Both
replay paths now demand contiguous coverage and name the first hole.
- **`AT (VERSION => n)` PAST THE END OF THE LOG now ERRORS** (*"Delta log is incomplete: version 3 is
  missing or unreadable and no checkpoint covers it"*) where it used to return the **newest** snapshot under
  the requested label — so a stale pin or an off-by-one silently got real rows for a version that does not
  exist. **Measured, then pinned** (`verify_delta_catalog_time_travel` 48 → 49); nothing asserted either
  answer before, so it could have flipped back unnoticed in either direction.
- **A transient READ FAILURE of a commit is now a HOLE, not a skip.** **MEASURED LIVE on OneLake
  (2026-08-01) — no spurious failures.** 16 writers × 20 commits: **19 OCC retries** (so the commit guard
  was genuinely under test), 320/320 commits, 320 groups, no short groups, all writers clean, all exited on
  their own. ⚠ **8 × 12 and 10 × 15 both produced ZERO retries that day** and are therefore NOT measurements
  of this — the harness's own void condition ("must be > 0, else the writers serialized and the guard was
  never under test"). Contention varies run to run; **check the retry count before believing a green.**
  (The pre-fix "8 × 12 reproducibly broke writers" line elsewhere in the docs describes the state BEFORE the
  `_last_checkpoint` and 412 fixes — the same table records 96/96 clean after them, so a clean 8 × 12 today
  is the documented behaviour, not a lost baseline.)
- `ListCheckpointVersionsAsync` is deleted upstream, and his message says *"Confirmed absent from
  fabricator before removing"* — checked, and true (only compiled EW DLLs match; no source call site).
  **Upstream is checking our tree before removing API.**

**The bump-by-bump journal** (every EW pin move, `PlanFiles`, the path-keyed DV DML, the `_metadata`
surface, the variant-transport decision + shredding split, the `DeltaTransaction` flush migration, and
the `TransientRowAddress` analysis) **moved verbatim to
[docs/ew-master-migration.md](docs/ew-master-migration.md) §Appendix — read it BEFORE the next EW bump
or upstream offer.** Standing rules distilled there and still binding: merge `upstream/main` (NOT `master` — renamed) into
fabricator-patches (fast-forward pins, NEVER force-push — release tags pin EW shas); after taking a
method wholesale from upstream, diff it against upstream and demand byte-identity (the auto-merged
duplicate-statement trap); only the net472 leg proves a change offerable; check `git log -S` before
assuming upstream reimplemented us (it may be convergence); read the DOC hunks of a conflict, not just
commit subjects.

## Architecture (layered for reuse)

Layered so a future **Power BI / DAX** connector reuses the same C++ core + managed bridge:

- **C++ generic core** — `namespace fabricator`, dirs `src/fabricator/` + `src/include/fabricator/`:
  `clr_host` (CoreCLR bootstrap + vtable wrappers), `arrow_ingest` (ArrowArrayStream → DataChunk),
  `arrow_produce` (DataChunk → ArrowArray), `abi.h` (the C ABI contract).
- **C++ DuckDB-API layer** — `namespace duckdb`, classes named `Fabricator*`, files `fabricator_*`:
  catalog / schema_entry / table_entry / transaction / metadata (`src/catalog/fabricator_*`), DML
  insert / modify / ctas (`src/dml/fabricator_*`), optimizer (`src/fabricator_optimizer.cpp`). The
  internal catalog scan function is `"fabricator_scan"`.
- **C++ provider layer** — keeps the `fabricator` / `Fabricator*` name: extension entry
  (`src/fabricator_extension.cpp`), `fabricator_secret`, `fabricator_storage` (ATTACH/connstr),
  `src/copy/fabricator_copy.cpp`, and all user-facing names (extension `fabricator`, functions
  `fabricator_query`/`_exec`/`_refresh_cache`/`_invalidate_cache`, `TYPE mssql`, `mssql_*`
  settings, `mssql://` URI, the `"fabricator"` catalog-type string).
- **C# `Fabricator.Bridge`** (`dotnet/Fabricator.Bridge`) — backend-agnostic: C-ABI `[UnmanagedCallersOnly]`
  exports + vtable (`Bootstrap.cs`, `Abi.cs`), handle table, Arrow export/import, `IBackend`/
  `IBackendCatalog`, `ArrowDataReader` (IArrowArrayStream→DbDataReader), `BulkSession`/
  `ChannelArrowStream` (streaming bulk), `StubBackend`.
- **C# `Fabricator.SqlServer`** (`dotnet/Fabricator.SqlServer`) — the `Microsoft.Data.SqlClient` backend +
  composition root; published self-contained next to the extension. Discovered via `BackendRegistry`
  reflection (env `FABRICATOR_BACKEND_ASSEMBLY`, default `Fabricator.SqlServer`).

### Target architecture: ONE binary, MULTIPLE providers (corrected goal, 2026-06-20)

The end goal is a **single `fabricator` extension binary that hosts several providers** (SQL Server via
SqlClient, Power BI/DAX via ADOMD, …) — NOT a separate binary per provider. Implications (planned;
current code still uses the single-provider `fabricator` naming):

- **Generic user-facing names**: `fabricator_query` / `fabricator_exec` (not `fabricator_query`). The user is
  fine breaking `gen_mssqlcompat_tests.sh` and renaming the kept tests.
- **Dispatch is handle/catalog-based** and already works: `Handles.Resolve<IBackendCatalog>(handle)`
  returns a backend-specific catalog, so any ABI call already routes to the right provider. Multi-provider
  mainly needs: C# `BackendRegistry` keyed by provider name (providers self-register, not `Active`=one) +
  **provider selection at open time** (`ATTACH … (TYPE fabricator, PROVIDER 'sqlserver')`, or inferred from
  the `mssql://`/`dax://` scheme, or the secret's provider). `open_catalog` ABI gains a `provider` arg; the
  catalog-type string becomes the generic `"fabricator"` (provider stored on the catalog).
- **Provider-specific logic lives in C#**: connection-string assembly + auth mapping (move out of
  `fabricator_secret.cpp`), type mapping, all SQL. The C++ `fabricator` core owns registration + dispatch +
  the function machinery, reused verbatim by every provider.
- **Custom scalar / table / table-in-out functions** (Airport-style, Phase 3) drive this. Two registration
  phases through one ABI shape (`list_global_functions(provider)` / `list_catalog_functions(handle)` +
  `execute_scalar`/`execute_table`/`execute_inout`, decls = Arrow-serialized name/kind/in-schema/out-schema/
  decl_id): **(A) load-time global** via `loader.RegisterFunction` — DuckDB only allows global registration
  during `Extension::Load()`, so this forces the **bridge to boot at extension load** (not lazily);
  **(B) attach-time catalog-bound** — discovered SQL Server procs/UDFs become `ScalarFunctionCatalogEntry`/
  `TableFunctionCatalogEntry` in `FabricatorSchemaEntry` (resolved as `db.schema.proc(args)`, refreshable via
  the existing cache invalidation). New core file `fabricator_functions.{hpp,cpp}` holds this. Table-in-out
  (`in_out_function`) is the hard part → Phase 4. **Full design: [docs/custom-functions-design.md](docs/custom-functions-design.md)**
  (ABI, the C# authoring API — lambda / attribute(SQLCLR-style, columnar) / derived — and
  `sp_describe_first_result_set` late-binding for table procs).
- Suggested order: (1) **C# multi-backend registry — DONE** (`BackendRegistry` is provider-keyed:
  `IBackend.Name`/`Aliases`, `Resolve(provider)`, `Active`=default; multi-assembly discovery via
  `FABRICATOR_BACKEND_ASSEMBLY` comma-list; SqlServer = `"sqlserver"`/alias `"mssql"`. Behavior-preserving —
  `Active` still routes to SqlServer); (2) **provider selection — DONE** (`open_catalog(provider,…)` ABI
  v17 → `BackendRegistry.Resolve`; ATTACH `PROVIDER` option + `scheme://` inference; clean unknown-provider
  error). The **generic names are now live as ADDITIVE ALIASES** (no breakage): `fabricator_query`/`fabricator_exec`/
  `fabricator_functions`/`fabricator_server_info` (+ the existing `fabricator_version`) and `ATTACH … (TYPE fabricator)`
  (the storage extension is registered under both `fabricator` and `fabricator`). Its gate, `test/verify_generic_names.test`, was DELETED by the rename (`2a26b7a`) together with the aliases it pinned — there is no such suite now. <!-- check-docs:ignore (naming it IS the point) -->
  The **breaking removal** of the `fabricator_*` names (+ catalog-type string `"fabricator"`, settings/secret/URI
  scheme rename, compat-corpus regen) remains the separate full-rename pass;
  (3) **connstr/auth → C# — DONE** (`build_connection_string` ABI v18: `fabricator_secret.cpp` reads the
  secret + emits its fields as JSON, `SqlServerBackend.BuildConnectionString` assembles the SqlClient
  connstr; `MapAuthentication`/`QuoteConnValue`/the access-token marker are now C#-only — C++ has no connstr
  knowledge); (4) dynamic functions — **(4a) function discovery DONE** (`fabricator_functions(catalog)` table
  fn + `FABRICATOR_META_FUNCTIONS`); **(4b) attach-time catalog-bound scalar UDFs DONE** (discovered scalar
  UDFs become `ScalarFunctionCatalogEntry` in `FabricatorSchemaEntry`, resolved as `db.schema.fn(args)` and
  executed over Arrow — ABI v19); **(4c) attach-time catalog-bound table-valued functions DONE** (discovered
  TVFs become `TableFunctionCatalogEntry`, resolved as `SELECT * FROM db.schema.tvf(args)`, with real
  SQL-level projection + best-effort filter pushdown reusing the table scan's machinery — ABI v21);
  **(4d) attach-time catalog-bound stored procedures DONE** (procs with a determinable result set resolved
  as table functions — `sp_describe` schema, `EXEC` execution, no pushdown — ABI v22; **named/optional
  params DONE (4d-2)**; **OUTPUT params + RETURN value as flat columns DONE (4d-3)**; multi-result-set +
  INPUT/OUTPUT deferred); **(4e) attach-time custom C#-authored scalar functions DONE** (`ICatalogScalarFunction`)
  **+ (4f) custom table functions DONE** (`IArrowTableFunction`) — both reuse the catalog scalar/TVF path,
  C#-only, no ABI; chosen over load-time global (deferred). **(4g) table-in-out DONE** (ABI v23 —
  `inout_open`/`push`/`finish`/`abort`; full plan + corrections:
  [docs/custom-functions-design.md](docs/custom-functions-design.md) §11.1): a discovered TVF `db.s.tf`
  gains a **sibling `db.s.tf_each(<input table>)`** (⚠ since 2026-08-02 the PROVIDER declares that sibling —
  see "THE `_each` MOVE" below; the host no longer invents it) that applies it **once per input row** via SQL-Server
  `CROSS APPLY` (T-SQL generated by C#, run on SQL Server — NOT a DuckDB lateral join), over the §11
  coordinated bounded channel (one session per call; parallel `UNION ALL` input fed thread-safely). Output =
  the echoed input columns (typed as the TVF **parameters**, since C# CASTs the VALUES to them) ++ the TVF
  output columns. **Two corrections found during the build:** (1) DuckDB forbids a `{LogicalType::TABLE}`
  overload coexisting with the scalar-arg scan form under one name (`bind_table_function.cpp`: "TABLE
  parameter, and multiple function overloads — not supported") → the in-out form is a **separate `_each`
  catalog entry** (single TABLE overload), the scan form (4c) keeps the bare name; alias tracked in
  `FabricatorSchemaEntry::inout_functions_`. (2) **Output is emitted SYNCHRONOUSLY per input chunk** (each
  `inout_push` runs that chunk's CROSS APPLY to completion + returns its full output) → there is **no tail**,
  so emitting rows never depends on detecting which parallel branch finishes last. This replaced an unsound
  first attempt (an atomic last-branch counter in `in_out_function_final`): `PhysicalUnion::BuildPipelines`
  can run UNION branch pipelines **sequentially**, so branch 1 could finish (counter→0, premature finish)
  before branch 2 starts → lost rows; it passed tests only by scheduling luck (caught in review). Also
  `OperatorFinalize` is a **global single-shot hook handed no `DataChunk`** so it **can't emit rows** — it's
  reserved for the per-row-proc COMMIT (a no-rows action). Session lifecycle = a refcounted
  `InOutSessionHolder` on the bind data; its **RAII destructor → `inout_abort`** is the release/rollback
  backstop on every teardown path (also frees the GCHandle). Verified: `test/verify_table_inout.test` (63
  assertions — incl. parallel UNION ALL, ORDER BY+LIMIT, WHERE, aggregate, empty, multi-column, large
  BIGINT→INT, error+recover). **(4g-custom) custom C#-authored table-in-out DONE** (now `IArrowInOutFunction` /
  the `StaticInOutFunction` base — in-out analog of 4e/4f): a pure-C# **per-chunk streaming** table-in-out (no SQL object), may keep mutable
  state across chunks (running aggregate); surfaced as `kind='inout'` → C++ `AddInOutFunction` registers a
  bare-name `{TABLE}` entry (`GetOrCreateCustomInOutFunction` + `FabricatorCustomInOutBind`, output = the fn's
  full declared schema, no input echo), dispatched in C# (`CustomInOut` factory registry → fresh instance per
  session; `CustomInOutSessionImpl` runs `Process(chunk)`). Reuses the 4g operator path — no new ABI/C++
  operator. Verified: `test/verify_custom_functions.test` (cf_tag per-row, cf_running_sum stateful 4999-row
  multi-chunk, per-session state, no SQL object). **(4g-proc) per-row stored-proc in-out DONE**: a discovered
  proc also gets `_each` (`db.s.usp_x_each(<table>)` EXECs the proc once per input row — a proc can't be
  inline-CROSS-APPLY'd). The per-row EXECs run on **DuckDB's pinned transaction** (`BeginWrite`), so the
  proc's writes commit/roll back with **DuckDB's** COMMIT/ROLLBACK — atomic in autocommit AND inside an
  explicit DuckDB `BEGIN`, no per-row commits. **`OperatorFinalize` is NOT used for the commit** (committing
  the in-out's own txn at operator-finish would commit before a user's explicit `ROLLBACK` could undo it; the
  transaction manager is the correct signal). Now on the Phase 6 streaming exchange (`SqlServerProcEach :
  IArrowInOutBinding`, resolved by `InOutBind`; was the 4g push `ProcInOutSessionImpl`, retired in `9056eae`):
  `DoExchange` per row runs `DECLARE @t TABLE(<proc result>); INSERT @t EXEC [s].[p] @param=@p,…;
  SELECT <echoed input>, t.* FROM @t;` on the pinned conn/`_txn` (echo server-side → output = input columns ++
  proc result columns; result-set procs only). Verified: `test/verify_proc_inout.test` (echo output, autocommit
  commit, row-failure rolls back the whole statement, explicit-`BEGIN` read-your-writes + `ROLLBACK` undoes —
  31 assertions). **(4g-finalize) the injected `OperatorFinalize` DONE**: an `OptimizerExtension`
  (`RegisterFabricatorInOutFinalizer`) wraps each in-out `LogicalGet` (identified by `function.in_out_function
  == FabricatorInOutFunction`) in a pass-through `LogicalExtensionOperator` whose `PhysicalOperator`
  (`PhysicalOperatorType::EXTENSION`) forwards rows 1:1 and, in `OperatorFinalize`, calls `holder->Finish()`
  → C# `inout_finish`. This is the reliable single "in-out finished" signal (fires **once**, sink-level, even
  above a parallel UNION — verified empirically + via `MetaPipeline`/executor finish-event scheduling),
  intended as a C# resource-cleanup hook + a clean commit of the read-only TVF's snapshot transaction
  (NOT the proc commit). **4g (table-in-out) is fully complete.**

## Next up (open threads for future sessions)

In-flight / planned refactors (all C#-only unless noted; tests stay green per slice):
- **THE WRITE-OPTIONS REVISIT — AGREED, NOT STARTED (user, 2026-08-06). Where each parquet knob BELONGS, not
  just whether it is plumbed.** Triggered by the measured gap below, but the user's ask is deliberately wider:
  it is a surface question, so decide the surface before finishing the plumbing.
  - **(1) PARTLY DONE 2026-08-07 — five more parquet options, on all three surfaces (`WITH` / `SET` /
    ATTACH), same precedence.** Added `parquet_row_group_size_bytes` (→ native `ROW_GROUP_SIZE_BYTES`, EW
    `RowGroupMaxBytes`), `parquet_version` V1/V2 (→ native `PARQUET_VERSION`, EW `DataPageVersion`), and
    `parquet_dictionary_size_limit` (native only). Gate `verify_with_options` 96 → **113**.
    - **⚠ `dictionary_size_limit` IS NOT EW's `DictionaryPageSizeLimit` — mapping them would have been
      silently wrong.** DuckDB's is a cap on DISTINCT VALUES; EW's is BYTES. DuckDB's byte-valued knob is a
      SEPARATE option (`string_dictionary_page_size_limit`). Hence native-only, with the reason in the error.
    - **⚠ THE TWO FILE-ROTATING OPTIONS ARE REFUSED, and each path has its OWN measured reason.** Native +
      NOT partitioned: DuckDB writes `<name>.parquet/data_0.parquet` while the Delta `add` records
      `<name>.parquet` — **the commit SUCCEEDS and the data file is a directory (silent corruption)**. Native
      + PARTITIONED: DuckDB refuses outright (*"Can't combine file rotation (e.g., ROW_GROUPS_PER_FILE) and
      PARTITION_BY for COPY"*) — worth recording because **OUR side would have coped**: `RunCopyPartitioned`
      already targets a directory and `ReadFileStats` already registers one `add` per `RETURN_STATS` row, so
      when upstream lifts that limit only the refusal has to go. Codec: no equivalent.
    - **⚠ `spec.PartitionColumns` IS NOT "this write is partitioned"** — it carries the STATEMENT's
      `PARTITIONED BY`, so an INSERT into an existing partitioned table has it EMPTY. Gating on it refused a
      valid write (measured). The table's partitioning is only known further down, in the writer.
    - **⚠ `dataFileWriter == null` IS NOT "the codec engine" either** — a native CTAS writes through DuckDB's
      COPY on a separate route and still opens EW with no writer, so that inference refused valid options on
      the DEFAULT provider (measured). Read the catalog's `_nativeWrite`.
    - ⚠ `ROW_GROUP_SIZE_BYTES` needs `SET preserve_insertion_order=false` (DuckDB binder). We deliberately do
      NOT set that on our COPY connection — it would silently break `SORTED BY` writes; DuckDB's error surfaces.
    - **⚠ BLOOM FILTERS ON THE NATIVE PATH NEED NOTHING — a note here previously said to build
      `write_bloom_filter` + `bloom_filter_false_positive_ratio`, and that was WRONG (corrected 2026-08-07,
      user-prompted).** DuckDB enables bloom filters by DEFAULT, we always pass `WRITE_BLOOM_FILTER true`, and
      its writer picks the COLUMNS itself — per-column selection is not a DuckDB concept because the decision
      is per column on cardinality: `parquet_extension.cpp:75` — *"After how many distinct values should we
      abandon dictionary compression AND BLOOM FILTERS?"* — i.e. the cutoff IS `dictionary_size_limit`, the
      knob added in this same pass. So `parquet_bloom_filter_columns` is codec-only by nature, not by a gap,
      and the README says so. It is accepted-and-ignored on native rather than refused, which is a small
      inconsistency with the "never silently ignore" rule elsewhere — harmless, because DuckDB blooms those
      columns anyway; tighten it only if the rule is ever made absolute.
    - **STILL OPEN from (1) — CLOSED 2026-08-07: `compression_level` and `bloom_filter_false_positive_ratio`
      are BUILT, on all three surfaces, and BOTH are two-engine.** `parquet_compression_level` → native
      `COMPRESSION_LEVEL`, EW **`CustomCompressionLevel`** (NOT `CompressionLevel`: DuckDB's is a NATIVE codec
      level and so is that one, while EW's `CompressionLevel` is a coarse `BlockCompressionLevel` ENUM —
      mapping to it would silently reinterpret the number as one of a handful of presets).
      `parquet_bloom_filter_false_positive_ratio` → native `BLOOM_FILTER_FALSE_POSITIVE_RATIO`, EW
      `BloomFilterFpp`. Both persist as `fabricator.parquet.*`.
      - ⚠ The **DEFAULTS DIFFER and are deliberately NOT normalised** (DuckDB 0.01, EW 0.05), so an unset
        option does NOT make the two engines write equivalent files — normalising would silently change the
        codec engine's behaviour for a user who asked for nothing. Pinned so any doc claiming equivalence has
        to reckon with it.
      - **The gates read the FILE, never the option**, because parquet records the CODEC and not the level:
        same data compressed at level 1 vs 19 (native 35808 → 28532 bytes; codec 25745 → 25332), and bloom
        bytes at fpp 0.3 vs 1e-6 (**544 → 8224**). ⚠ The bloom cardinality is chosen so DuckDB actually WRITES
        a filter — it abandons dictionary encoding AND bloom filters past `dictionary_size_limit` distinct
        values, and a first attempt at 50 000 distinct values produced two filter-less files and a vacuously
        equal comparison.
      - Both ends of the fpp range are REFUSED rather than clamped: 0 asks for a filter with no false
        positives (impossible) and 1 for one that matches everything (useless, and still costs bytes).
    - **⚠ A SHIPPED BUG THIS PASS FOUND: `parquet_bloom_filter_columns` WAS A SILENT NO-OP ON THE DEFAULT
      TABLE SHAPE (found 2026-08-07, now REFUSED).** engineered-wood matches the list against the PARQUET
      path (`HasBloomFilter(pathInSchema)`), and on a **column-mapped** table that path is the PHYSICAL name
      (`col-e090d9ee…`), never the logical one. MEASURED: **0 of 10 column chunks got a filter with mapping
      on, 10 of 10 with it off.** Column mapping is the DEFAULT, so the option did nothing for almost
      everyone.
      - **Nothing caught it because NO SUITE HAD EVER ASSERTED THAT A BLOOM FILTER WAS WRITTEN.** The option
        was accepted, the statement succeeded, and `verify_with_options` checked only that. It was found by
        adding the fpp gate — i.e. by writing a test that reads the FILE — which is the same lesson the rest
        of this surface keeps producing: *a "the statement succeeded" test cannot distinguish a working write
        option from one that never reached the writer.*
      - **REFUSED rather than fixed, and the bound is real:** the physical names are assigned by the CREATE
        itself and engineered-wood takes `ParquetWriteOptions` AT OPEN, so translating logical → physical
        needs either a two-phase open or an EW-side resolution against the Delta schema. A loud error naming
        the one-word workaround (`column_mapping='none'`) beats a knob that writes nothing. **The real fix is
        an upstream-shaped ask and is the right thing to do later.**
      - **⚠ Only the `WITH` layer refuses.** A `SET delta_write_options` bloom list spans every table in the
        session (some mapped, some not) and a PERSISTED one is a declaration another engine may read —
        failing either would punish writes that never asked for anything here. Consistent with the
        ignore/refuse split above.
      - Gate `verify_with_options` §9c, **mutation-tested** (dropping the guard makes the refusal assertion
        fail), with the `column_mapping='none'` leg as the POSITIVE CONTROL — otherwise the refusal would pass
        equally if bloom filters had simply stopped working everywhere.
  - **(1-original) EVERY parquet option must be expressible in `CREATE TABLE … WITH (…)`, INCLUDING bloom-filter
    columns for the EW codec.** Today `WITH (parquet_compression=…, parquet_row_group_size=…,
    parquet_bloom_filter_columns=…)` is the DuckLake-parity set gated by `verify_with_options`; confirm each
    reaches BOTH engines (⚠ the codec and native paths take different routes — see the void measurement below)
    and add whatever is missing.
  - **(2) Bloom-filter columns are NOT wanted as an ATTACH default (user, explicit).** They are a per-TABLE
    property — which columns you probe — so a catalog-wide default is the wrong shape and should not be added
    even though the spec can carry one.
  - **⚠ THE SQL SERVER `unicode`/NVARCHAR WITH OPTION WAS SCOPED AND THEN NOT BUILT — because the behaviour
    the user wanted ALREADY EXISTS (2026-08-07).** The ask was "same DuckDB code writes to prod SQL Server
    (default collation, NVARCHAR) and to Fabric (VARCHAR only), and new SQL Server databases should default to
    VARCHAR + UTF-8". `MapArrowToSqlType` already resolves exactly that, per connection, from the COLLATION:
    `IsUtf8Collation ? VARCHAR : HasNVarchar ? NVARCHAR : VARCHAR`. All three targets land correctly with NO
    setting. **Check whether an ask is already satisfied before designing an option for it** — a redundant knob
    would have added a way to get it WRONG (forcing VARCHAR on a legacy collation is silently lossy).
    - **BOTH legs are now gated (2026-08-07), and the reason the second one was not is that a claim here was
      FALSE.** `verify_default_varchar_length` asserts `nvarchar/-1` on the docker box's default collation
      AND `varchar/-1` on a UTF-8 one (8 → 44 assertions). ⚠ This entry used to read *"the UTF-8-collation ⇒
      VARCHAR leg is NOT gated — the rig has no UTF-8-collation database"*, and recommended provisioning one.
      **The rig has had one all along**: `BinCollTest` is `Latin1_General_100_BIN2_UTF8` (provisioned to
      reproduce Fabric Warehouse's default collation, which is binary AND UTF-8) and `IsUtf8Collation` is a
      plain `_UTF8` SUFFIX test, so it qualified from the start. The half a new-database user depends on was
      one ATTACH away from covered, and the write-up said it was impossible. **Check the rig before recording
      a gap as unclosable.**
      - The section carries a POSITIVE CONTROL (`is_utf8_collation` really is true on that attach) — without
        it the VARCHAR assertion would pass equally on a server that had merely lost NVARCHAR support, which
        is a different bug — plus a non-ASCII ROUND TRIP, which is the assertion that JUSTIFIES the rule
        rather than restating it, and the `mssql_ctas_text_type` escape in the OTHER direction (keeping
        NVARCHAR on a UTF-8 database, which a migration needs).
    - Escape hatches already exist: `mssql_default_varchar_length` (bounds whichever type is chosen) and
      `mssql_ctas_text_type` (replaces the type outright). What is genuinely missing is only the PER-TABLE
      form of these — the (3) item below.
  - **(2b) THE PARQUET TUNING IS NOW PERSISTED AS TABLE PROPERTIES — BUILT + GATED 2026-08-07 (C#-only, no
    ABI).** A `CREATE … WITH (parquet_compression='zstd') AS …` writes `fabricator.parquet.compression` into
    the Delta table CONFIG, and every later write to that table reads it back: plain INSERT, merge-on-read
    post-image, copy-on-write rewrite, and **OPTIMIZE's compaction output** — whichever catalog or session runs
    them. New BCL-only format file `dotnet/Fabricator.Bridge/DeltaParquetProperties.cs` (`ParquetTuning`), keys
    `fabricator.parquet.{compression,row_group_size,row_group_size_bytes,version,dictionary_size_limit,
    row_groups_per_file,file_size_bytes,bloom_filter_columns}`.
    - **The problem it fixes, measured before:** `CREATE TABLE t WITH (parquet_compression='zstd') AS …` wrote
      ZSTD and a later plain `INSERT INTO t` wrote **SNAPPY**, so a table accumulated MIXED compression; on an
      incrementally-built dbt model most bytes were snappy whatever it was configured for, and OPTIMIZE — which
      rewrites the MAJORITY of a table's bytes — actively undid the setting it was configured for.
    - **PRECEDENCE, as decided: ATTACH < `SET delta_write_options` < TABLE PROPERTY < statement `WITH`.** The
      property outranks the session setting deliberately — a stray `SET` must not silently change a table's
      storage format. Implemented as a fourth layer inside `ResolveWriteSpec`, which now takes a REQUIRED
      `tablePath` (the structural obstacle this entry predicted): required rather than optional so the compiler
      forces every present and future call site to answer "which table?" — the same omission on the rewrite
      paths is what let OPTIMIZE undo its own configuration for months.
    - **⚠ ONLY THE STATEMENT'S OWN `WITH` PERSISTS, never the resolved spec.** Persisting the resolved value
      would turn a session `SET` into a permanent property of the table — exactly what the precedence rule
      exists to prevent, and durably. The keys ride `CreateProperties`, whose only consumer is `CreateConfig`
      at creation (EW's `OpenOrCreateAsync` returns early for an existing table and ignores `configuration`
      entirely), so it is create-only BY CONSTRUCTION and a plain INSERT cannot rewrite it.
    - **⚠ THE BUG THAT ONLY MEASUREMENT FOUND, and it would have silently broken `fabricator.sortedBy` too.**
      The write spec is resolved BEFORE the write runs, so a CTAS asks for the config of a table that does not
      exist yet. Caching that MISS made the CREATE's own declaration invisible to every later statement in the
      session — the property landed in the table and the next plain INSERT still wrote SNAPPY. Fix: **a miss is
      never cached.** The collateral damage is the instructive part: this pass UNIFIED `_sortedByCache` into one
      `_tableConfigCache` (one open per table instead of two, one set of invalidation sites), and `sortedBy` had
      never been read on a create path — so caching the miss would have made an ordered table quietly stop
      ordering its appends. **Unifying two caches inherits the WORST staleness behaviour of either consumer.**
    - **⚠ A `CREATE OR REPLACE` INHERITS the declaration and CANNOT CHANGE it — measured, and the intuitive
      answer is the wrong one.** My first version treated a replace as "redefines the table, inherit nothing".
      But a REPLACE does NOT re-create the Delta table: the log continues and the metaData commit COPIES the
      configuration forward (measured — a replace with a different schema emits `CHANGE COLUMNS` and the
      `fabricator.parquet.*` keys survive it). So inheriting nothing writes the replace's files at the engine
      default INTO a zstd-declared table, and the next plain INSERT flips back — **mixed compression produced by
      one statement**, the exact disease. Now it inherits. The corollary is a real limitation, pinned and in the
      README: a REPLACE's `WITH` applies to that statement's write only, because create-time configuration is
      applied at v0. That is NOT specific to these keys — it is equally true of every create flag
      (`deletion_vectors` / `column_mapping` / `row_tracking` / `change_data_feed`) and of the `delta.*` WITH
      properties beside them. `fabricator_delta_set_tblproperties` is what changes a declaration.
    - **THE IGNORE/REFUSE SPLIT, as decided and now built.** A `WITH`/`SET` option is an ACTIVE REQUEST in THIS
      statement ⇒ an engine that cannot honour it FAILS (`ValidateSpecForEngine`, unchanged). A persisted
      property is a DECLARATION ABOUT THE TABLE, read later by a possibly DIFFERENT engine ⇒ apply what fits,
      ignore the rest, `Fabricator.Delta` **Debug** line naming what was dropped (not a Warning — ignoring here
      is correct rather than degraded, and the choice is invisible from SQL otherwise). The persisted layer
      deliberately does NOT route through `ValidateSpecForEngine`. Verified both directions on ONE key and ONE
      engine, which is what makes it a demonstration rather than two unrelated assertions:
      `WITH (parquet_dictionary_size_limit=…)` on a codec catalog ERRORS, while the codec engine writes happily
      into a table DECLARING it (that key ignored, the compression still honoured).
    - **A MALFORMED persisted value THROWS, naming the key** — `set_tblproperties` writes arbitrary
      `fabricator.*` keys so someone will hand-set garbage, and swallowing an UNPARSEABLE value is a different
      case from ignoring a well-formed-but-unhonourable one. Recoverable from SQL (re-set the key), which is
      what makes refusing acceptable rather than a trap.
    - **⚠ PERSISTING `row_group_size_bytes` HAS A SESSION PREREQUISITE — found by writing the suite, and it is
      the one key that changes character when persisted.** DuckDB's binder refuses `ROW_GROUP_SIZE_BYTES` while
      preserving insertion order. As a per-statement option that is fine (the user sets the flag in the same
      breath); persisted, it means **a plain INSERT naming nothing now fails** until the session sets
      `preserve_insertion_order=false`. We deliberately do NOT set it on our COPY connection — it would silently
      break `SORTED BY` writes — so DuckDB's error surfaces. The codec engine has no such constraint. Pinned
      (§8g) so a future change cannot start swallowing it.
    - **⚠ `dictionary_size_limit` is still NOT mappable to EW's `DictionaryPageSizeLimit`** — DuckDB's is a cap
      on DISTINCT VALUES, EW's is BYTES — so it stays native-only in BOTH layers. The two file-ROTATING keys
      persist but can be honoured on NO path today; persisting them is still right (the declaration outlives the
      limitation, and when upstream lifts it they start being honoured with no migration).
    - Gates: `verify_with_options` 113 → **171** (§8–§8g), pinned on the **NATIVE** engine because a codec-only
      gate would pass while the shipped default path stayed broken; hermetic **67/67 — 6661**. Tier-0
      `Fabricator.Bridge.Tests` 85 → **106** (the format round trip + every malformed case, offline — those are
      reachable from SQL only through `set_tblproperties`, one service round trip each). **Mutation-tested with
      two mutants, both killed at the same assertion** — the plain-INSERT one, which is the whole feature:
      caching the miss, and never reading the persisted layer at all.
  - **(3) Re-examine the `SET` parameters and MOVE what belongs per-table into `WITH`.** `delta_write_options`
    is one JSON blob mixing genuinely session-scoped things (compression as a storage-cost policy) with
    per-table ones (partitioning, `replace_where`, `schema_mode`). Decide each knob's home rather than
    accepting the current split, and keep an escape for the per-statement case.
  - **(3b) THE SQL SERVER PER-TABLE `WITH` FORMS — BUILT + GATED 2026-08-07 (C#-only, no ABI).**
    `CREATE TABLE … WITH (table_type=…, varchar_length=…, text_type=…)`: the per-table forms of
    `mssql_default_table_type`, `mssql_default_varchar_length` and `mssql_ctas_text_type`. The per-table value
    OUTRANKS the session setting, same precedence rule as the Delta write tuning.
    - **⚠ I FIRST WROTE THIS UP AS BLOCKED ON TWO OBSTACLES AND BOTH WERE OVERSTATED — the user challenged
      the first one ("why does table_type collide? it depends on location") and was right, which is why the
      correction is recorded rather than quietly fixed.**
      - **"`table_type` COLLIDES" — IT DOES NOT.** It already meant `DELTA`/`PARQUET` for the external-table
        CETAS analog, and I called adding `CLUSTERED COLUMNSTORE`/`HEAP` a collision needing a design
        decision. The two vocabularies are DISJOINT, so a value determines its own branch; and `location`
        corroborates it (an external `WITH` has always REQUIRED `location`, so a `WITH` without one is
        currently an ERROR — the regular branch is purely additive and can shadow nothing). Sharing the key
        is the BETTER option, because it keeps the per-table spelling equal to the setting it mirrors.
      - **"IT NEEDS THREADING THROUGH THE WHOLE DDL CHAIN INTO A SHARED TYPE MAPPER" — it needed ONE optional
        parameter.** `optionsJson` was ALREADY in scope at both create paths, `BuildCreateTable` has three
        call sites, and `MapArrowToSqlType` takes two optional overrides that DEFAULT to the old
        session-store reads — so the ALTER paths, which carry no `WITH`, are untouched by construction.
      - The generalisable bit: **both objections came from reading a method SIGNATURE and inferring the
        blast radius, instead of counting the call sites.** The same shortcut produced the "we never reach
        `CommitOccAsync`" error already recorded in this file.
    - **The branch rule, and the one asymmetry in it:** `location` present ⇒ external (so a missing/foreign
      `table_type` errors AGAINST the external vocabulary); otherwise the value picks the branch. An ordinary
      table needs no `table_type` at all — `WITH (varchar_length=200)` alone is valid — which is why
      "`location` is present" ALSO forces the external branch rather than `table_type` alone. An unknown value
      falls to the REGULAR branch, so a typo reads as "not a valid storage form" and offers both vocabularies
      rather than being silently treated as external.
    - `varchar_length` / `text_type` are REFUSED on an external table: its column types come from the storage
      files, so accepting them would do nothing — the failure mode this whole surface exists to prevent.
    - **⚠ THE GATE'S LOAD-BEARING ASSERTION IS THE OPT-OUT, NOT THE POSITIVE.** With the SESSION default set
      to columnstore, `WITH (table_type='HEAP')` must win — a test that only checked "columnstore produces a
      columnstore index" passes with the per-table value ignored entirely. It carries a POSITIVE CONTROL that
      the session default really was in force. Gate `verify_with_options_mssql` 9 → **41**,
      **mutation-tested** (dropping `with?.TableType ??` kills it at exactly the HEAP assertion).
    - **⚠ One third of the original ask was already satisfied and was NOT built.** The `unicode`/NVARCHAR flag
      is unnecessary: `MapArrowToSqlType` resolves the text type per connection from the COLLATION
      (`IsUtf8Collation ? VARCHAR : HasNVarchar ? NVARCHAR : VARCHAR`), so prod SQL Server gets NVARCHAR, a
      UTF-8 database gets VARCHAR and Fabric gets VARCHAR, with no setting. Now gated on BOTH legs — see the
      `verify_default_varchar_length` note above. `text_type` remains the escape hatch in either direction.
  - **(4) THE PLUMBING GAP IS FIXED (2026-08-07), and fixing it needed TWO changes where the write-up assumed
    one.** `ResolveWriteSpec` lives on the `DeltaCatalog` INSTANCE while the rewrite paths sit in the STATIC
    `DeltaReader`, so merge-on-read post-images, copy-on-write DELETE/UPDATE rewrites and **OPTIMIZE's
    compaction output** were written at engineered-wood's defaults whatever the user configured. Now threaded:
    `DeleteByRowIds` / `UpdateByRowIds` / `Optimize` take a `DeltaWriteSpec?` and the catalog passes
    `ResolveWriteSpec(...)`; `ReadRowsByRowIds` takes the catalog's `native_read` (the streaming-audit item —
    done together, as this entry predicted it should be).
    - **⚠ THREADING THE SPEC INTO THE EW OPEN WAS NECESSARY AND NOT SUFFICIENT — measured, and the first
      re-measurement still showed SNAPPY.** Under `native_write` (the `PROVIDER 'delta'` DEFAULT) the file is
      written by **DuckDB's COPY** via `NativeParquetDataFileWriter`, so EW's `ParquetWriteOptions` never
      apply; the spec also had to reach that writer's COPY options. It already accepted a `spec` — the three
      `DeltaReader` sites simply constructed it without one. **Re-measuring after the "fix" is what caught
      this**; the EW-only change would have shipped as done.
    - MEASURED, native engine, `SET delta_write_options='{"compression":"zstd"}'`: before, CTAS **ZSTD** but
      the UPDATE post-image **SNAPPY** and OPTIMIZE's compaction output **SNAPPY**; after, **zero SNAPPY
      chunks anywhere**. Gate `verify_with_options` 82 → **96**, mutation-tested (dropping the spec from
      `NativeParquetDataFileWriter` dies at exactly the post-image assertion). ⚠ The gate is pinned on the
      **NATIVE** engine on purpose — a codec-only gate would have passed while the shipped default path was
      still wrong.
  - **⚠ THAT VOID MEASUREMENT IS NOW SETTLED (2026-08-07), AND ITS SUSPICION WAS WRONG — the "fourth gap" does
    not exist.** It had read: *"`SET delta_write_options='{"compression":"zstd"}'` on `PROVIDER 'delta'` produced
    SNAPPY data files … so the session setting may simply not reach the native COPY"*. **It does reach it.**
    MEASURED per file: with that SET, a CTAS on `PROVIDER 'delta'` (native COPY) writes **ZSTD**, as does the
    per-table `WITH (parquet_compression='zstd')` and as does the codec engine. `SET` and `WITH` ARE equivalent
    on the statement's own write, so the surface question can be decided on merits rather than on plumbing.
  - **⚠ THE REAL AXIS IS *WHICH WRITE*, NOT `SET`-vs-`WITH` — and it is now MEASURED ON THE NATIVE ENGINE, not
    just reasoned or seen on the codec.** With the SET in force on `PROVIDER 'delta'`: CTAS/INSERT files
    **ZSTD**, the merge-on-read UPDATE's post-image file **SNAPPY**, and **OPTIMIZE's compaction output
    SNAPPY** (before: 6 ZSTD chunks and zero SNAPPY; after: the same 6 plus 4 SNAPPY, the compacted file). So
    this file's prediction that *"OPTIMIZE is the one that stings — it rewrites the MAJORITY of a table's bytes,
    so it actively undoes the setting it was configured for"* is CONFIRMED rather than inferred, and it is not a
    codec-only defect. Cause is unchanged: `ResolveWriteSpec` lives on the `DeltaCatalog` INSTANCE while ~33
    bare `DeltaWriter.Options()` opens sit in the STATIC `DeltaReader` and pass no spec.
- **THE STREAMING/BUFFERING AUDIT — STARTED 2026-08-07; `Materialize` DONE, the rest still open. A
  whole-codebase pass over EVERY path that holds batches.** The UPDATE work (grouped flush → unboxed input)
  kept turning up buffering that nobody had decided on, so the remaining ones get looked at deliberately
  rather than one at a time when they hurt.
  - **⚠ THE SHAPE THE USER WANTS, and it is a standing rule for new code, not just this audit:
    `IAsyncEnumerable` consumed with `await foreach`, with the STATE HELD BY THE CODE THAT YIELDS THE BATCHES
    and released once everything has been yielded.** So the producer owns its resources for exactly the
    enumeration's life (an iterator's `finally` / `await using` is the release point), and no consumer
    accumulates a list to keep something alive. Where a consumer genuinely needs the whole set at once (EW's
    `WriteAsync` commits over all batches), that requirement must be stated at the seam rather than met by a
    silent collect upstream.
  - **`Materialize`'S IPC COPY IS GONE — DONE 2026-08-07 (C++ + C#, no ABI). MEASURED: peak working set
    427 MB → 232 MB (−46%) on a 1.5M-row partitioned collect-path INSERT, time flat (8.8 s → 8.6 s),
    byte-identical data.** `DeltaWriter.Materialize` used to write every batch into a `MemoryStream` and read
    them back, documented as needed because *"the source batches may be freed after consumption"*. It now
    RETAINS them; `FABRICATOR_MATERIALIZE_COPY=1` restores the old path.
    - **The justification was false, and the Arrow C data interface is why**: a consumed `ArrowArray` is the
      CONSUMER's property. Our own producer implements exactly that (`ArrowProducer::GetNext` moves the batch
      out of its queue — *"ownership transfers to the consumer"* — and `Release` frees only what is STILL
      QUEUED), and `PushBatch`'s array is imported by `CArrowArrayImporter.ImportRecordBatch`, which takes
      ownership. `ChannelArrowStream` disposes nothing it has yielded; `BulkSession`'s drain disposes only what
      is still IN the channel.
    - **⚠ NONE OF THAT SETTLED IT, DELIBERATELY — the verification is the reusable part.** A use-after-free
      here is SILENT on Windows and Linux (exactly how the `ArrowProducer::Release` mutex bug hid until macOS
      CI ran it), so green suites and correct data prove nothing. New **`ArrowLiveness`**
      (`src/fabricator/arrow_produce.cpp`, `FABRICATOR_ARROW_LIVENESS=1|2`, off by default) INTERPOSES the
      release callback of every batch handed to the managed side — the standard C-data-interface wrap: stash
      the original callback + `private_data`, restore them before delegating — and ATTRIBUTES each free.
      - **COUNTING (level 1), swept over the hermetic suites: 1292 batches handed out, `released_by_producer=0`,
        `double_released=0`, zero suites with a bad verdict.** A `ProducerFreeScope` thread-local marks
        `ArrowProducer::Release`/the destructor, so a free fired from OUR side is distinguishable from the
        consumer disposing what it owns.
      - **⚠ COUNTING ALONE CANNOT ANSWER THE QUESTION, which is why there is a level 2.** "Released once" is
        equally true of a batch freed BEFORE the write read it. At level 2 the consumer prints its own markers
        to the same stderr, so the interleaving decides. MEASURED: all three handouts, then `materialize
        retained 3 batches`, then `write BEGIN` … `write END`, and only THEN the three releases. Nothing is
        freed while the write is reading.
      - Reproduce: `FABRICATOR_ARROW_LIVENESS=2` on a partitioned codec INSERT (the collect path); or the
        level-1 sweep, `for s in $(./scripts/list-hermetic-suites.sh); do unittest --test-dir . "$s"; done |
        grep handed_out`.
    - **⚠ THE INSTRUMENTATION WAS FIRST PUT IN THE WRONG PLACE, and a zero nearly read as a clean result.**
      `ArrowProducer::GetNext` is NOT the path that feeds `Materialize` — a bulk write goes through
      `PushBatch` into the C# channel, and the producer's queue serves only `RETURNING`/modify/function args.
      The first armed run reported `handed_out=0`, which is indistinguishable from "nothing was freed". The
      positive control (does the registry print at all?) is what caught it. **Instrument the seam the data
      actually crosses, and never accept a zero without one.**
    - **⚠ THE CONTROL RUN FOUND A SECOND THING: THE OLD PATH LEFT THE ORIGINALS UNRELEASED.** With
      `FABRICATOR_MATERIALIZE_COPY=1` the counters read `handed_out=3 released=0` — the source batches are
      never deterministically freed, so peak memory held the ORIGINALS, the serialized `MemoryStream` AND the
      decoded copies at once. That is most of the 195 MB the change gives back. (`released=0` is at `atexit`;
      a finalizer may reclaim them eventually — the claim is that release was not DETERMINISTIC, not that they
      leaked forever.) In retain mode the releases fire after `write END`, i.e. EW's `WriteAsync` disposes the
      batches it was given — so retaining also makes the disposal deterministic.
    - ⚠ Across the sweep `released` (776) is well below `handed_out` (1292), so ~40% of handed-out batches are
      not deterministically released on OTHER paths either. Not chased here; it is a lead for the rest of the
      audit, not a defect anyone has demonstrated.
    - **⚠ Empty batches are still SKIPPED and that is load-bearing** — engineered-wood writes one parquet file
      per input batch, so passing a zero-row batch through would commit an empty data file.
    - Gate: the whole hermetic tier at **67/67 — 6661, IDENTICAL to the pre-change counts**, which is the
      behaviour-preservation claim; plus the liveness sweep above. The registry SHIPS (env-gated, like the
      `Fabricator.Memory` marks and for the same reason): the removal rests on a measurement taken on ONE
      platform, so a macOS or foreign-producer surprise should be one environment variable away from being
      isolated rather than a rebuild.
  - Ladder to price each remaining site against: **retain = 0 copies** (what `Materialize` now does),
    **`ArrowCompute.Take` = 1 copy** (new buffers, type-agnostic incl. nested/extension — what
    `ParseUpdateStream` now uses), **IPC round-trip = 2 copies + serialization**.
  - **⚠ THE BUFFERED READ-BACK SILENTLY IGNORED THE CATALOG'S `native_read` — FIXED 2026-08-07 by giving
    `ReadRowsByRowIds` the catalog's flag (both call sites are in `DeltaCatalog`, so it was bounded). Original
    finding, kept because the reasoning is what generalises:**
    `ReadRowsByRowIdsAsync` opens with a bare `DeltaWriter.Options()` (`DeltaReader.cs:974`), passing **no
    `dataFileReader`**, so a buffered UPDATE's read-back takes the EW CODEC reader even on a
    `PROVIDER 'delta'` catalog where the user's `native_read` is on. Two consequences: it is the wrong engine
    for what the attach asked for, and **the codec reader yields ONE BATCH PER ROW GROUP** where DuckDB's
    `read_parquet` yields 2048-row vectors — measured 30 flushes vs 1 on a 60k-row UPDATE, and a 300k-row
    control giving exactly 3 batches at the 122880 default. That is what makes the UPDATE grouped flush inert
    on that path, and it means the buffered read-back materialises a whole row group at a time on the path a
    dbt `BEGIN…COMMIT` model takes.
    - Fix = pass the catalog's reader (and writer, and write spec) into that open — **the SAME structural
      change the write-options revisit needs for the spec, so do them together.** Then re-measure the grouped
      flush on the buffered path; it should start behaving like autocommit.
    - ⚠ Sweep the other bare `DeltaWriter.Options()` opens in `DeltaReader` for the same question — the
      grep is ~30 sites and this one was found by accident, not by looking.
  - Other paths the audit must cover, not only `Materialize`: `pending.Batches` (the buffered park),
    `BulkSession`'s bounded channel (already streaming — confirm, do not assume), the CDF capture,
    `DeltaWriter.Write`'s batch list, `ArrowDataReader`, and the collector/in-out sessions. The
    `Fabricator.Memory` marks exist to make each one answerable rather than arguable.
- **`MERGE INTO` — BUILT + GATED 2026-08-05 (C++-only, no ABI bump). ⚠ It SHIPPED A SILENT-DATA-DESTRUCTION
  BUG FOR HALF A DAY BEFORE THE FIX, and the shape of that miss is the most reusable thing here.**
  - **⛔ THE BUG, MEASURED (found by the user asking "can we actually do a delete update insert in these
    order? i think we are in trouble" — the answer was yes, we were).** Delta × `deletion_vectors=false` ×
    AUTOCOMMIT × ≥2 mutating actions: a later action addressed the **WRONG ROW**. Every action consumes rowids
    captured from the merge's ONE join scan, but each action committed separately — and a **copy-on-write
    DELETE removes a row, shifting every LATER row's position down one**, so a subsequent action's captured
    `(fileOrdinal, position)` named a different row. On a one-file table `(1,10)(2,20)(3,30)(4,40)` with
    conditional deletes of id1 and id3 the survivors were **`2, 3`** — id3 NOT deleted, **id4 DESTROYED**,
    exit 0. The update variant silently lost the update instead.
    - **⚠ WHY EVERY TEST MISSED IT, which is the transferable part: the hazard needs the rows in ONE FILE.**
      With a row per file a copy-on-write rewrite renumbers nothing, so all four of my earlier
      multi-action/multi-file probes were correct — and both tiers were GREEN through the bug. It is strictly
      positional (corrupt iff the deleted row precedes the other action's target), so even a single-file test
      passes if the delete happens to sit last. **A merge test that does not put several affected rows in one
      file, with the delete FIRST, tests nothing about this.**
    - **⚠ AND THE GREEN TIERS WERE THE TRAP.** 65/65 and 45/45 were reported as evidence the feature was
      finished. They were evidence only that the shapes I had imagined worked. The user's question was worth
      more than the whole suite run.
  - **THE FIX: a merge carrying ≥2 `UPDATE`/`DELETE` actions is FORCED TO BUFFER, even in autocommit.**
    `PlanMergeInto` counts them and sets `force_buffered` on each target; each operator's `GetGlobalSinkState`
    then calls `BeginTransaction(handle, is_explicit=true)`. Both actions stage against ONE pinned snapshot ⇒
    neither can renumber the other's targets ⇒ one commit.
    - **⚠ THE MARK MUST HAPPEN AT EXECUTION TIME, NOT PLAN TIME.** A prepared statement's physical plan is
      reused across transactions, so a plan-time mark would apply to the first one only. `GetGlobalSinkState`
      is the right hook because `PhysicalMergeInto` builds every action's global sink state UP FRONT, before
      any action does provider work — so whichever action runs first sets it and the rest observe it,
      including the INSERT's own `begin_bulk`, which therefore buffers instead of committing on its own.
    - **⚠ THE COUNT EXCLUDES `INSERT`, AND MY FIRST VERSION GOT THIS WRONG — user-caught, via DuckLake's own
      docs.** Counting every MUTATING action was measured to REFUSE the single most common merge shape,
      `WHEN MATCHED THEN UPDATE` + `WHEN NOT MATCHED THEN INSERT`, on a non-DV table where it had always been
      correct and was never unsafe. An `INSERT` addresses no existing rows, so it can neither renumber another
      action's targets nor hold targets of its own — and it commits LAST regardless (it is the one action that
      always routes through the transaction buffer, as the instrumented log shows). So the broad count bought
      no safety and cost the common case. **The hazard needs TWO ROW-ADDRESSING actions, nothing less.**
      - **This is the boundary DuckLake documents** ("MERGE INTO with DuckLake only supports a single
        UPDATE/DELETE action currently", https://ducklake.select/docs/stable/duckdb/usage/upserting) — arrived
        at independently, which is some evidence it is the real fault line. **We are STRICTLY more capable:
        DuckLake REFUSES two such actions outright; we SERVE them by fusing, and refuse only when the table
        cannot be buffered at all.** ⚠ So the earlier note here claiming we are "more permissive than DuckLake"
        because we accept 4-action merges is still true, but for a narrower reason than it implied: what we add
        is fusion, not permissiveness about the hazard.
    - **ONE row-addressing action keeps the direct path** — nothing to collide with — so a non-DV table loses
      no capability. Asserted as a POSITIVE CONTROL, since otherwise the §11 refusal would pass equally if
      non-DV tables had simply become unwritable.
    - **⚠ NO ABI BUMP, by REUSING `BeginTransaction(isExplicit)` — whose real meaning is "the USER opened a
      transaction", not "buffer this statement". That overload HAD a measured cost, and fixing it is the second
      narrowing this feature needed.** On SQL Server that entry also gates three EXTERNAL-TABLE guards, so a
      2-action merge into an identity-equipped SQL Server external table was **refused** by the pre-existing
      *"storage-side DML … cannot roll back with an explicit transaction"* check — MEASURED, after first
      probing a table with no row identity and getting a different error, which nearly recorded the wrong
      conclusion in both directions.
      - **THE FIX, and it is the right scoping rather than a workaround: force only where row identity is
        POSITIONAL** — `rowid_actions >= 2 && entry.HasVirtualRowId()`. The hazard is one action RENUMBERING
        rows another addressed, which requires a TRANSIENT (file, position) rowid, i.e. a provider VIRTUAL
        rowid as Delta's `_metadata.row_id` is. Where the rowid is real KEY COLUMNS (SQL Server's PK / unique
        index / IDENTITY) it is a VALUE, stable under any rewrite — measured immune to both corrupting shapes —
        so forcing bought nothing there and only cost the external-table capability. **Provider-agnostic: it
        names an identity KIND, not a provider**, which is why it belongs in the shared layer.
    - **TRADE-OFF ACCEPTED:** a merge with one `UPDATE`/`DELETE` plus an `INSERT` is therefore NOT fused, so in
      autocommit it is two commits — correct but not atomic, i.e. the pre-existing Delta per-statement
      divergence. `BEGIN … COMMIT` still fuses it. That is the right way round: refusing the common shape on a
      non-DV table to buy atomicity would trade a capability for a guarantee nobody asked for.
    - **⚠ "TWO COMMITS" IS NOT THE SMELL — "TWO OPERATIONS ADDRESSING PRE-EXISTING ROWS" IS.** Worth stating
      because I had been using the commit count as the diagnostic, and a user question about CTAS showed it does
      not discriminate. MEASURED, same session: `CREATE TABLE … AS SELECT` is **2 commits** (`CREATE TABLE` then
      `WRITE`) in autocommit **AND inside an explicit transaction** — so its two-ness is NOT caused by the
      autocommit/buffered decision and forcing the buffer cannot fix it (it is limitation 1.5: EW's
      `OpenOrCreateAsync` commits v0 before any transaction on that table can exist). Yet a CTAS has NO hazard —
      a new table has no pre-existing rows to renumber. Conversely `CREATE OR REPLACE … AS SELECT` over an
      existing table is **1 commit** (a single `WRITE`), i.e. two operations fused with no forcing at all. So
      commit count tracks neither risk nor atomicity reliably.
      - **The audit that follows from the right smell:** MERGE is the ONLY statement DuckDB plans as multiple
        DML operators sharing ONE scan's row addresses, and `INSERT … ON CONFLICT` is the same mechanism (the
        binder rewrites it into a MERGE) which we already refuse. So the exposure was unique to MERGE rather
        than one member of a class we have patched only partially — checkable, and checked.
  - **Scope of the original hazard, all measured:** `deletion_vectors` defaults ON and a **DV delete PRESERVES
    positions**, so the default was always safe (all four position combinations verified). An EXPLICIT
    transaction already refused the non-DV path. **SQL Server is IMMUNE** — its rowid is a PK VALUE, stable
    under any rewrite (verified with both corrupting shapes). So the blast radius was exactly Delta × non-DV ×
    autocommit × ≥2 actions.
  - **⚠ The hazard was NOT two actions touching one row.** `PhysicalMergeInto` removes each row from the
    candidate set as an action claims it, so actions are row-DISJOINT by construction, and the existing
    same-transaction guards ("cannot delete rows inserted in this transaction") key on the ordinal's
    pending-vs-committed RANGE — a different axis. The hazard was one action RENUMBERING rows another had
    already addressed, which no guard covered and none of that family would have caught.
  - Gates: `verify_merge_into.test` **209 × 2 engine legs** (hermetic, ENGINE-DOUBLED) + `verify_merge_into_mssql.test`
    **106** (service). §11 is the destruction regression gate (refusal + table bit-for-bit intact + the
    single-action positive control), §11b the same shape on a DV table asserting BOTH the right answer and the
    fusion — a correct result reached by three commits would mean the unsound mechanism is still running and
    merely got lucky. **Mutation-tested**: disabling the forcing reproduces `2, 3` exactly and kills the suite.
  - **⚠ `ON CONFLICT` came along for free ARCHITECTURALLY and still does NOT work — for a reason upstream of
    the merge (see below).** One override,
  `FabricatorCatalog::PlanMergeInto` (`src/catalog/fabricator_merge_into.cpp`), lifted the shared refusal
  `Database type "fabricator" does not support MERGE INTO or ON CONFLICT` for **every** provider at once.
  Measured working on Delta AND SQL Server: matched UPDATE, matched conditional DELETE, not-matched INSERT
  (with and without a column list), `WHEN NOT MATCHED BY SOURCE`, `DO NOTHING`, the `ERROR` action, and
  ROLLBACK. Gates: `verify_merge_into.test` **130 × 2 engine legs** (hermetic, ENGINE-DOUBLED — a merge is
  composed of exactly the update/delete/insert paths that list already doubles) + `verify_merge_into_mssql.test`
  **90** (service).
  - **THE LOWERING IS DuckDB'S, NOT OURS — that is the whole reason this was small.** Each action becomes the
    same `Logical{Update,Delete,Insert}` the standalone statement produces, routed through our OWN
    `PlanUpdate`/`PlanDelete`/`PlanInsert`. So MERGE INHERITS every property of our rowid DML rather than
    re-deriving it: provider dispatch, the buffered-transaction fusion, the change feed, identity handling.
  - **⚠ DuckLake IS the reference, NOT `DuckCatalog` — and the earlier note here saying otherwise cost time.**
    `ducklake/src/storage/ducklake_merge_into.cpp` is a CUSTOM catalog doing exactly this (synthesize the
    logical op, call its own `Plan*`), which is our situation; `DuckCatalog` plans against its own storage. **We
    are MORE permissive than DuckLake on two axes**, both measured: it refuses more than ONE update-or-delete
    action total (*"MERGE INTO with DuckLake only supports a single UPDATE/DELETE action currently"*) while we
    serve DELETE + UPDATE + INSERT + NOT-MATCHED-BY-SOURCE in one statement, because each action gets its own
    operator and the buffer fuses their actions at COMMIT. (Both of us refuse RETURNING.)
  - **⚠ `PhysicalMergeInto` drives the sub-operators as MANUAL SINKS** — it calls
    `GetGlobalSinkState`/`GetLocalSinkState`/`Sink`/`Combine`/`Finalize` directly on sliced chunks, never as a
    pipeline. Ours are already self-contained sinks (our `PlanInsert` already accepted a null child), so they
    slotted in unchanged. **`parallel` MUST be false and that is load-bearing, not caution**: every action
    shares ONE global sink state, and `FabricatorPhysicalInsert` streams into a single bulk session whose
    `PushBatch` takes no lock — documented as safe only because `ParallelSink()` is false. DuckLake passes
    `true` because its operators are parallel-safe; ours are not.
  - **⚠ THE ONE REAL CODE CHANGE WAS WHERE AN UPDATE READS ITS SET VALUES, AND IT FIXED A LIVE CORRUPTION BUG.**
    `AppendModifyBatch` read them POSITIONALLY from chunk `0..n-1`, which is right only because a plain
    UPDATE's binder projection happens to put them there. Two things break it: a MERGE's UPDATE action shares
    ONE projection with every other action (arbitrary positions), and **`SET x = DEFAULT` contributes NO
    projection column, shifting every later SET value by one**. The second was already shipping. **Measured on
    `(a BIGINT DEFAULT 99, b BIGINT, c INTEGER)`: `SET a = DEFAULT, b = 5` SUCCEEDED and committed `a=5, b=0`**
    (b got the rowid) where correct is `a=99, b=5`; where the shifted types differ instead it raised an
    INTERNAL error and **fatally invalidated the database**. Now `FabricatorModifyTarget.set_child_indices`
    carries the BOUND_REF position per SET column (upstream `PhysicalUpdate` reads them the same way), shared by
    both paths via `FabricatorFillUpdateSetColumns` so they cannot drift; `SET = DEFAULT` is REFUSED rather
    than guessed (evaluating it needs the bound defaults in the operator — a feature, deliberately not smuggled
    into a MERGE change). Gate in `verify_delta_catalog_update.test` (63 → 73), mutation-tested: reverting to
    the positional read kills BOTH merge suites at their FIRST merge statement.
  - **⚠ THE `!HasRowId()` GUARD IS REQUIRED FOR *EVERY* MERGE, INCLUDING AN INSERT-ONLY ONE.** DuckDB decides
    matched-vs-not by testing the rowid column for NULL, so with no rowid `BindRowIdColumns` appends nothing and
    `row_id_start` points ONE PAST the chunk's width. `ComputeMatches` reads `chunk.data[row_id_index]`
    unconditionally. An insert-only merge never reaches `FabricatorBuildModifyTarget`'s own check, so without
    this guard it is an out-of-bounds read — **mutation-tested: `INTERNAL Error: Attempted to access index 2
    within vector of size 2`, then the database is FATALLY INVALIDATED.** Refuse at plan time, where it can
    still be a message.
  - **⚠ ATOMICITY IS THE TRANSACTION'S, NOT THE STATEMENT'S — measured both ways, and autocommit is NOT atomic.**
    A merge is several DML operators, so on Delta an **autocommit `MERGE` produces ONE COMMIT PER ACTION**
    (measured: baseline 2 → 4; three actions ⇒ three commits) while `BEGIN; MERGE; COMMIT;` fuses them into
    **ONE** (2 → 3). The DATA is correct either way; only atomicity differs. **The change feed of the fused
    form is exact** — an `update_preimage`/`update_postimage` pair plus the `insert`, all at one version (this
    was the stated priority) — while the autocommit one is SPLIT across versions. Same per-statement-commit
    divergence the rest of the Delta provider has; every number is pinned (`verify_merge_into.test`
    §3 / §3b / §5 / §12) so a change reads as deliberate.
    - **⚠ THE MECHANISM IS NOT "ONE `DeltaTransaction` PER ACTION" — INSTRUMENTED, because the obvious guess is
      wrong in both directions.** There are exactly TWO `StartTransaction` sites in the Bridge.
      **EXPLICIT: ONE shared transaction** — `pending.HeldTxn ??= table.StartTransaction(...)`
      (`DeltaCatalog.cs:3701`), keyed per DuckDB-transaction × table, so every action stages into it and one
      `CommitAsync` writes one version. **AUTOCOMMIT: three commits by THREE DIFFERENT mechanisms** — the DV
      DELETE commits directly with **no `DeltaTransaction` at all**, the merge-on-read UPDATE creates its OWN
      short-lived one (`DeltaReader.cs:2620`, `await using`), and the INSERT **still routes through the txn
      buffer** (autocommit has an implicit DuckDB transaction — the log shows `buffered … for txn 12`) so it is
      flushed LAST, after the delete and update have already committed. So the intermediate states an observer
      can see are delete → delete+update → all three, and the INSERT commits last despite its bulk session
      being opened FIRST at merge init.
    - **⚠ INTEROP: `commitInfo.operation` is `TRANSACTION` for a fused merge, and NOTHING we write ever says
      `MERGE`.** Autocommit labels each action instead (`DELETE`/`UPDATE`/`WRITE`). Measured via
      `fabricator_delta_snapshots` (identical on BOTH engines) and pinned per VERSION in §13 — never as an
      aggregate, since `max(operation)` over a string column returns the ALPHABETICAL maximum. A foreign
      consumer keying on `operation = 'MERGE'` will not match us.
  - **⚠ AND THE MODES DIFFER IN CAPABILITY, OPPOSITE TO THE ATOMICITY TRADE-OFF: a merge with an UPDATE/DELETE
    action WORKS in autocommit on a `deletion_vectors=false` table and is REFUSED inside a transaction.** The
    buffered path requires DVs; the autocommit path rewrites copy-on-write and does not. So wrapping a working
    merge in `BEGIN` to gain atomicity can COST the statement (*"… requires deletion vectors on the table … run
    it in autocommit (copy-on-write), or COMMIT first"*, table left unchanged). Inherited from the plain
    statements rather than MERGE-specific — which is the lowering working as designed — and it bites only where
    DVs were switched off. Pinned in BOTH directions with a positive control (§11).
  - **The same-transaction hazards do NOT bite, and the reason is structural.** `UPDATE of rows inserted in the
    same transaction` is refused on any table, `DELETE of rows inserted in the same transaction` on a CDF table
    — but both guards are **PER-ROW, keyed on the rowid's FILE ORDINAL** (`>= PendingOrdinalBase`), not on the
    mere presence of pending appends. A merge's matched rows come from the pre-merge snapshot, so they carry
    committed ordinals. ⇒ **MERGE does not need hoist slice 3.**
  - **STILL OPEN — the SQL Server half is CORRECT BUT NOT OPTIMISED.** Actions run as per-row DML on the pinned
    connection, NOT as a server-side T-SQL `MERGE`. Generating one server-side statement needs the SOURCE to be
    server-side too, and a DuckDB MERGE's source is a DuckDB relation (the README example merges a DuckDB temp
    table INTO SQL Server, which is exactly the shape that cannot be pushed down). A pushdown would have to
    detect "source and target are both in this catalog" and fall back otherwise.
  - **⚠ `ON CONFLICT` IS NOT AN INDEPENDENT FEATURE — THIS FILE SAID IT WAS, AND THAT WAS WRONG.** Since 1.5.x
    the binder **REWRITES `INSERT … ON CONFLICT` into a MERGE** (`Binder::Bind(InsertStatement&)` →
    `GenerateMergeInto`, `bind_insert.cpp:541`), which is why ONE message covered both features and ONE
    override lifted both. It still does not WORK, for a reason upstream of the merge: `GenerateMergeInto` keys
    the join on a UNIQUE/PK constraint and `FabricatorTableEntry::GetStorageInfo` returns an EMPTY
    `TableStorageInfo`, so DuckDB finds no uniqueness. Measured: with a target ⇒ *"The specified columns as
    conflict target are not referenced by a UNIQUE/PRIMARY KEY CONSTRAINT or INDEX"*; without ⇒ *"There are no
    UNIQUE/PRIMARY KEY constraints that refer to this table"*. **On Delta that refusal is semantically CORRECT**
    — Delta enforces no unique constraint on user columns, so there is nothing to conflict against and
    "fixing" it would claim a guarantee the format lacks. On SQL Server a real PK/unique index exists, so the
    remaining work is `GetStorageInfo`, NOT the merge hook. Pinned by `verify_merge_into.test` §10.
    - The old deferral rationale is **right about T-SQL and irrelevant to the path DuckDB takes**: SQL Server's
      `IGNORE_DUP_KEY = ON` is an option on a UNIQUE INDEX, so it expresses only `DO NOTHING` and only where the
      index was built that way. That matters for a *native* pushdown; through the merge rewrite ON CONFLICT
      needs no server feature at all.
  - `update_is_del_and_insert` is ignored: the merge binder hardcodes it FALSE (`bind_merge_into.cpp:87`) and we
    do not override `BindUpdateConstraints`, so nothing sets it — and our UPDATE operator owns that choice
    anyway (Delta copy-on-write already rewrites).
  - **⚠ A C++ TRAP worth remembering: do NOT declare `namespace fabricator` INSIDE `namespace duckdb`.** The
    extension's generic core is the GLOBAL `::fabricator`; a nested `duckdb::fabricator` shadows it for every
    TU that includes the header, so every existing `fabricator::PartitionColumnsArg` /
    `BoundaryClientProperties` call fails to compile with *"is not a member of duckdb::fabricator"*. Hence the
    two shared helpers are `FabricatorBuildModifyTarget` / `FabricatorFillUpdateSetColumns`, in `duckdb`
    directly.
- **TIMESTAMP BOUNDS FOR `fabricator_delta_changes` — AGREED, NOT BUILT (user, 2026-08-04). ⚠ C++-TOUCHING**
  (two `TableFunction` overloads at `fabricator_extension.cpp:627`), so it needs the full rebuild, not just a
  managed republish. Ours is `BIGINT`-only today — `(catalog, '<schema.>table', from [, to])` — while Delta's
  `table_changes(table, start [, end])` accepts EITHER, and `table_changes_by_path` for a path. We already own
  the machinery: `DeltaReader.ResolveVersionAsOf`, which `AT (TIMESTAMP => …)` uses.
  - **⚠ DO NOT COPY DELTA'S DUAL-TYPING.** Verified in `DeltaTableValueFunctions.scala` at `v4.0.0`: ONE
    argument position carries both meanings and the LITERAL'S TYPE selects (`toDeltaOption("starting", …)` →
    integer/long ⇒ `startingVersion`, string/timestamp ⇒ `startingTimestamp`). So `table_changes('t', '0')`
    means "since the epoch", not "since version 0" — a wrong answer with no error. Declare overloads on a real
    `LogicalType::TIMESTAMP` (a quoted number then cannot drift in), or use named parameters, which we support
    and their TVF grammar does not.
  - `ChangesBind` currently encodes the bounds as a `"from:to"` STRING (`Abi.cs:349`), so it needs a marker
    distinguishing version from timestamp bounds; resolve in C# with the SAME helper the AT clause uses so the
    two surfaces cannot drift.
  - **⚠ Pin the boundary semantics explicitly** — Delta's `startingTimestamp` means *the first version at or
    after* that instant, and an off-by-one there is invisible in a small test. Gate in
    `verify_delta_catalog_changes`: a timestamp bound returning the SAME rows as the equivalent version bound,
    plus a bound before v0 and one after the last commit.
  - Context for why it came up: a `cdc` action carries no `baseRowId`, which is why the row-identity gap in
    [docs/delta-transaction-hoist.md](docs/delta-transaction-hoist.md) §6 is unrecoverable — but note that gap
    is in `__delta_row_commit_version` (row identity, file-supplied), NOT in `_commit_version` (the feed's own
    column, stamped by the reader from the version it replays). **Range filtering is therefore unaffected by
    it**, and conflating the two is the easy mistake here.
- **KEEP `README.md` IN SYNC — a standing rule, not a task (user, 2026-07-30).** `README.md` is the
  **user-facing** surface; this file and `docs/` are project memory (organised by the order things were built,
  dense with why-we-rejected-X, written for whoever maintains this next). **Whenever a change to CLAUDE.md or
  `docs/` adds or alters something an extension USER can see — a function, a setting, an ATTACH option, a
  behaviour, a gotcha — update `README.md` in the SAME commit.** It is not a separate deliverable and must
  never be parked again.
  - Why the rule exists: it had already drifted badly. When it was introduced (2026-07-30) the README had
    **zero mentions** of provider macros (global, shipped 2026-07-24, *and* catalog-bound),
    `fabricator_host_query`, `fabricator_delta_scan`, or SQL-generating table functions — four user-visible
    capabilities with no user-facing documentation at all. Nothing was wrong in the README; it was simply
    never updated alongside the internal docs, which is precisely what this rule prevents.
  - **Run the README's SQL examples before committing them.** They are copy-pasted by users, so an untested
    example is a defect shipped to the least-equipped audience. All examples added in that pass were executed
    first.
  - Two docs are flagged ⚠ in the documentation index because their prose is stale
    (`multifile-delta.md`'s "Phase-A slices BUILDING" header; `native-delta-write.md`'s pre-flip defaults
    table + the removed `deltalake` alias). **Do not source README content from either until they are fixed** —
    a user-facing page repeating a wrong default propagates it to the audience least able to spot it.
- **FABRIC REST API FUNCTIONS — P0 BUILT + VALIDATED LIVE; P1/P2 designed (2026-07-30):
  [docs/fabric-api-functions.md](docs/fabric-api-functions.md) (§9c = as-built, §10 = the full
  API sweep with a verdict per area).** `fabric.*` functions over `Microsoft.Fabric.Api` (already a
  Bridge PackageReference, **2.18.0** since 2026-08-02 — bumped to track latest, forced by nothing and
  changing nothing; §9i re-probed the two absences the design rests on, `ExitValue` and semantic-model
  refresh, WITH controls, and both still hold).
  **Shipped:** `refresh_sql_endpoint()`/`_ex`, `list_shortcuts()`/`_ex`,
  `create_shortcut` / `_alter_` / `_json` / `drop_shortcut`, plus `fab_delta_info()`.
  Catalog-bound on a **OneLake** Delta attach ONLY, inheriting workspace+lakehouse+credential from the
  ATTACH (dbt runs OFF Fabric, so the ambient chain is useless there and a GLOBAL function has no route
  to a DuckDB secret). Gate `verify_delta_catalog_functions` 21 (hermetic); hermetic tier 62/5558.
  - **Enabling refactor, reusable: the Delta catalog now HOSTS catalog-bound functions** (all 7 ABI
    members used to throw; FUNCTIONS metadata was the 1-column fallback). New Bridge pieces
    `FunctionsMetadata` (the kind-6 stream built IN MEMORY — no SQL engine to `UNION ALL` through) and
    `CatalogFunctionSet` (registry + the five members + the `__all__` schema sentinel), so DAX/deltars
    can host functions by wiring the same two. C#-only, no ABI change.
  - **⚠ ZERO-ARGUMENT FUNCTIONS WERE IMPOSSIBLE, AND FAILED SILENTLY — now fixed.** Apache.Arrow 23
    cannot represent an EMPTY schema across the C interface in EITHER direction (export and import both
    throw `ArgumentNullException('fields')`; verified with a positive control). The host treats a failed
    schema fetch as "discovered name is stale" and **erases the function**, so the only symptom was the
    Debug WARN `GetFunctionParamSchema failed: … 'fields'` that this file previously recorded as
    "benign — global functions pass". It was not benign, it was this. Fix needs BOTH halves: C#
    `ArrowSchemaExport` hand-builds the empty struct (`+s`, 0 children) since `CArrowSchema.release` is
    internal; C++ passes **no args stream at all** for an argument-less table function (`args` was
    already nullable).
    - **⚠ CORRECTED 2026-08-02 — "zero-arg SCALARS stay impossible by design, because a scalar's arg batch
      is also how row COUNT crosses" WAS WRONG, and zero-arg scalars now WORK.** The stated reason does not
      hold: a 0-column Arrow array carries its length perfectly well, and **exporting** one succeeds
      (measured; a 0-column/5-row `RecordBatch` reports `Length=5`). The obstacle was never the count — it
      is the same zero-FIELD **schema** limit as above, whose *import* half was simply never addressed
      because a zero-argument TABLE function does not need it (the host sends no args stream at all, so
      nothing is imported). A SCALAR's arg batch crosses the other way, so it does.
    - Fix: for a zero-parameter scalar the host marshals **one throwaway BOOLEAN column** of `row_count`
      rows (`BuildFabricatorScalarFunction`). No ABI change, no C# change — a zero-argument function reads
      only `RecordBatch.Length`, and `GlobalFunctions.ExecuteScalar` never validated column count.
      `ExpressionExecutor` sets the argument chunk's cardinality OUTSIDE the children loop
      (`execute_function.cpp`), so `args.size()` is the true row count even with no columns.
    - Gate: `verify_global_functions` 72 → **80**, via the demo `fabricator_batch_seq()` (returns the row's
      1-based position, so the DISTINCT count pins PER-ROW invocation — a constant-valued zero-arg function
      would prove the count crossed but not that). **Mutation-tested**, and the mutant is instructive: with
      the fix reverted the function still **REGISTERS** fine (registration only needs the param-schema
      export, already fixed) and fails only at CALL time — so a registration-only test would have missed it.
  - **`run_notebook()`/`_ex` BUILT + proven end-to-end** (the elevated ask). Parameters ride
    **`executionData.parameters`** `{name:{value,type}}` — LIVE-VERIFIED honoured; the generic top-level
    `parameters[]` array is accepted with 202 and **SILENTLY IGNORED** for notebooks, so a hand-rolled REST
    call looks like it works while the notebook runs on defaults. Proof was reading the values BACK from the
    notebook's own output (`{"p_text":"from-sql","p_int":42,…}` with correct str/int/float/bool). Blocking by
    default (cap 1 h; cold Spark ≈ minutes); `wait_seconds := 0` submits only. **`exitValue` lives at
    `properties.exitValue` on the NOTEBOOK-scoped instance GET only** (absent from the SDK model in 2.14.0
    AND 2.18.0) and came back **NULL in every run** on both computes despite the notebook API existing and
    being called ⇒ documented best-effort, do NOT build control flow on it. That same `properties` carries
    `compute` + `executionSnapshotUrl` (+ Spark UI/driver-log links) — a portal diagnosis link from SQL.
    **Poll the ITEMS-scoped instance, enrich from the NOTEBOOK-scoped one**: the latter 404s
    (`ItemNotFound` / "no notebook execution state found for the runId") for a while after submission, so
    reading it first turns a healthy run into an error.
  - Also BUILT: the P2 introspection set `workspaces` / `items`+`_ex` / `lakehouses` (with the SQL
    endpoint connstr — the bridge to a T-SQL ATTACH) / `warehouses` / `connections` /
    `notebook_parameters` (heuristic — parses the papermill `parameters`-tagged cell; 0 rows is a
    legitimate "no tagged cell"; `GetNotebookDefinition` is an LRO, ~20 s, never per-row). All live-verified.
  - **Live findings that change USAGE:** `status='NotRun'` from a refresh means **already in sync, NOT
    failure** (all 19 tables on `LH`; a hook asserting `='Success'` fails on a healthy refresh — assert
    `<>'Failure'`); `table_name` is **schema-qualified** on a schema-enabled lakehouse; the SP is refused
    (`PrincipalTypeNotSupported`/`FeatureNotAvailable`) for **ResetShortcutCache** and **notebook
    CREATION** despite documented support (notebook creation stays a one-time portal action;
    `UpdateItemDefinition` IS allowed, which is how the spike notebook gets filled). **`connections()` returning 0 is
    identity scope, not absence**: connections carry their own role assignments, so an SP sees only its
    own — `LH` certainly has connections (its ADLS/S3 shortcuts require them) and the SP saw none.
  - **⚠ EXPERIMENT-DESIGN trap that produced a WRONG answer twice.** The first two parameter runs concluded
    "both payload shapes are ignored"; both shapes were submitted in sequence and the notebook's result file
    read ONCE afterwards, so the second (genuinely ignored) shape's output was attributed to BOTH. A shared
    side-channel read after N experiments measures only the last. Clearing the marker and reading PER shape
    gave the real answer. The standing "a negative result is not a measurement" rule in a new disguise: the
    method worked, the ATTRIBUTION was broken. Also re-learned: verify the precondition first — the
    `parameters` cell tag was confirmed to survive the definition round-trip before trusting any of it.
  - **C# trap:** an Azure *extensible enum* has an implicit conversion FROM string, so
    `cond ? Policy.X : null` infers `string` and calls `op_Implicit(null)` → `ArgumentNullException` at
    run time. Annotate `(Policy?)null`. Finding it needed a stack trace the ABI does not carry, hence the
    `Wrap`/`Guarded` helpers that append `StackTrace` for UNEXPECTED exceptions only.
  - **NAMED PARAMETERS for custom TABLE functions — BUILT (2026-07-31), and it retired the `_ex` siblings.**
    The `fabricator.named` field-metadata tag (already used by sqlgen) now drives plain table-function
    registration on BOTH the catalog and global paths, so an optional argument is `recreate := true` and
    `refresh_sql_endpoint` / `_list_shortcuts` / `_run_notebook` / `_items` are ONE function each again
    instead of a plain+`_ex` pair. Authoring: **⚠ SUPERSEDED 2026-08-02 by the UNIFIED PARAM PROTOCOL below —
    `NamedParameters` no longer exists**; a named parameter is a field of the ONE `Parameters` schema tagged
    `fabricator.param_style="named"`. **The binding still reads BY POSITION** — position is simply that
    schema's field order, and the host marshals EVERY declared parameter, substituting a typed NULL for an
    omitted named one; that equivalence ("omitted" == "explicit NULL") is why collapsing `_ex` changed no binding
    code. **Scalars are excluded and unfixable**: DuckDB `ScalarFunction` has no named-parameter concept, so
    `create_shortcut_ex(…, conflict_policy)` remains a genuine sibling. Gate
    `verify_delta_catalog_functions` §6 (27) — both spellings, the value really crossing the ABI, a
    misspelled name as a clean binder error, and no positional callability. **Positional + named MIX freely**,
    which is the case that fails SILENTLY if the NULL substitution is off by one (it would corrupt the
    POSITIONAL value rather than error) — pinned hermetically by the demo global `fabricator_seq(n, start := …)`
    in verify_global_functions (72), and verified live on
    `run_notebook('nb', wait_seconds := 900, params_json := '{…}')` with the args out of declared order
    and the intervening one omitted, read back from the notebook's own output.
  - **`workspace :=` / `item :=` OVERRIDES on every catalog-bound TABLE function (2026-07-31)** — expressible
    only once named parameters existed. The attach still supplies the defaults (the zero-arg call is
    unchanged), but ONE attach can now drive several lakehouses, which a dbt project writing to more than one
    otherwise solves with a second ATTACH purely to refresh an endpoint. Live: `refresh_sql_endpoint()`
    → LH's 19 tables vs `(item := 'LH2')` → 0 through the same attach. `ResolveItem` gained an explicit
    `workspaceId` so a cross-workspace lookup does not silently search the attach's own workspace. The
    shortcut SCALARS are excluded (no named parameters) and always act on the ATTACHED item.
  - **JOBS + MAINTENANCE + the last introspection — BUILT and live-validated (2026-07-31, §9e):**
    `table_maintenance` (**V-Order**, which our OPTIMIZE cannot produce — complementary, not a
    duplicate; live `Completed`, table re-read fine afterwards), `run_job` / `_job_status` /
    `_job_instances` / `_cancel_job` (one shared submit+poll path generalized out of the notebook runner),
    `lakehouse_tables`, `operation_status`, and `reset_shortcut_cache` — the last
    implemented BLIND because the SP is refused (`PrincipalTypeNotSupported`), yet PROVEN WIRED: it reaches
    the service and returns the service's own error, so only the permission is missing (expect it to work on
    a notebook's AMBIENT user-delegated token).
    - **⚠ `Wrap` did NOT cover a PAGED read, and the first live failure exposed it**: `PageableResponse<T>` is
      lazy, so the request happens during ENUMERATION — outside the try — and the error arrived as a raw Azure
      dump with a header list instead of our formatted message. Fixed by `WrapList` (materializes inside the
      guard); all paged reads use it. General shape: *a guard around a call returning a lazy sequence guards
      nothing.*
    - Two API limits found: `lakehouse_tables` is REFUSED on a **schema-enabled** lakehouse
      (`UnsupportedOperationForSchemasEnabledLakehouse`; works on a flat one — our own discovery covers it
      anyway), and **a DuckDB table function cannot take a SUBQUERY argument** (`Binder Error: Table function
      cannot contain subqueries`) while a SCALAR can — so `job_status` needs a literal id.
  - **SEMANTIC MODELS — BUILT + LIVE-VALIDATED the same day (§9f):** `semantic_models`,
    `refresh_semantic_model` (ENHANCED refresh — live `Completed` with `refresh_type=ViaEnhancedApi`,
    which is the PROOF the enhanced path was taken rather than a plain refresh), `semantic_model_refreshes`
    (history showed `ViaEnhancedApi` / **`DirectLakeFraming`** / `WebModeling`). On the **Power BI REST**
    surface — `FabricApi/FabricPowerBiRest.cs`, a `partial` half of `FabricApiClient` — because:
  - **the Fabric SDK CANNOT refresh a semantic model (§9f).** The Fabric SDK **cannot refresh one at all** (probed with
    a zero control: `RefreshSemanticModel`/`EnhancedRefresh`/`RefreshSchedule` all 0; only CRUD + definition +
    `BindSemanticModelConnection`). Refresh lives in the **Power BI REST API**
    (`POST /v1.0/myorg/groups/{ws}/datasets/{id}/refreshes`) — a different HOST but the **same audience we
    already mint**: `FabricCredentialResolver.PowerBiScope` is exactly the `powerbi/api` scope the DAX
    provider uses, so the same `fabric_sp`/ambient token works with NO new credential path. Both a Lakehouse
    and a Warehouse have a DEFAULT semantic model (resolved by NAME convention — there is no "default for item
    X" field), and refreshing it is what makes a Delta write visible to **Power BI**, the way
    `refresh_sql_endpoint` makes it visible to **T-SQL**. Constraints: enhanced refresh needs
    Fabric/Premium (unsupported on shared capacity, 8/day), `notifyOption` is invalid for an SP yet an
    enhanced refresh needs a non-`notifyOption` body, and SP access rides a SEPARATE tenant setting
    (Admin portal → Tenant settings → Developer settings → **"Service principals can call Fabric public
    APIs"** — a DIFFERENT axis from granting the SP a workspace role, which is the confusion this invites: the
    tenant setting says whether SPs may call the APIs at all, the workspace role says what this one may do
    there; both must hold). **MEASURED as already satisfied on this tenant**: the same `fabric_sp` gets 200
    from `GET /v1.0/myorg/groups` and `/groups/{ws}/datasets`, the workspace is `isOnDedicatedCapacity: true`
    (so the shared-capacity enhanced-refresh restriction does not apply), and the model list confirms the name
    convention — lakehouse `LH` has a model named `LH`, plus two `Test Warehouse Model*`, all
    `isRefreshable: true`. So refresh needs NO admin change here. Note NEITHER gate explains
    `PrincipalTypeNotSupported`/`FeatureNotAvailable` — those are per-API principal-type limits no setting
    lifts. The XMLA route adds a third, CAPACITY-level gate (Semantic models workload → XMLA = Read Write). Split to keep: **REST for "refresh this model, tell me when done" (`fabric.*`), XMLA/TMSL through
    the DAX provider for per-table/partition control (`dax_*`)**.
    - **Three traps the live run settled.** (1) The API treats a body of only `notifyOption` as a PLAIN refresh
      AND rejects `notifyOption` for an SP — interacting rules, so we always send `type` (default `Full`) and
      never `notifyOption`; `refresh_type` in the result is how you tell which path you got. (2) Power BI
      reports IN-PROGRESS as **`status = "Unknown"`**, and a just-submitted request may be absent from the
      history entirely — both mean "still running", so a naive `!= 'Completed'` poll exits immediately with a
      misleading value. (3) The request id arrives ONLY in the `x-ms-request-id` header (the 202 has no body;
      a `Location` tail is the fallback). Also: Power BI nests errors under `error.{code,message}` where Fabric
      uses flat `errorCode`/`message`, hence a separate `PowerBiReadAsync` beside `Describe`.
  - **P3 + THE XMLA HALF + the dispatch extraction — BUILT 2026-07-31 (§9g), and this CLOSED every §8
    deferral except one.** The remaining §10 verdicts were P3-demand-driven or skip; the P3 set is now built and
    every **skip** stands with its reason. **Fabric P3 (15 functions, WIRED + reviewed but NOT live-validated —
    the tenant has no git-connected workspace, no deployment pipeline and no mirrored DB to exercise):**
    `git_status`/`_connection`/`_commit`/`_update`; `deployment_pipelines`/`_stages`/`_items`/
    `deploy`/`_operations`; `capacities`; `environments`; `data_access_roles`;
    `mirrored_databases`/`mirroring_status`/`mirrored_tables`.
    **XMLA/TMSL (`dax_*`, the other side of the §9f split, on a DAX attach):** `dax_refresh` /
    `dax_refresh_table` / `dax_refresh_partition` — the LAST is the operation REST cannot express at all.
    - **Standing rules this pass produced.** (1) **`wait_seconds` is our vocabulary but git/deploy accept only
      MINUTES** — rounded UP, floored at 1, because 0 there means "give up immediately", NOT "don't wait" (the
      job APIs' `wait_seconds := 0` genuinely submits-and-returns; these cannot). (2) **A non-nullable
      `DateTimeOffset` on a NULLABLE parent** (`GitSyncDetails.LastSyncTime`,
      `TableMirroringMetrics.LastSyncDateTime`) must be null-tested on the PARENT — written the other way it
      reports the .NET epoch as a sync time. (3) **`ListDataAccessRoles` returns `Response<T>` with its own
      continuation token, NOT a `PageableResponse`** — the one read here that must not go through `WrapList`.
      (4) `git_update`'s commit hash is **required and positional** on purpose: "update to whatever is on
      the branch now" is how a promotion flow silently deploys an unreviewed commit. (5) Stage resolution takes
      GUID → NAME → ORDER, in that order, so a stage literally named "1" wins over order 1.
    - **XMLA specifics:** SYNCHRONOUS (no request id, no polling — the opposite of the REST path, and it means
      no "Unknown"-status trap), TMSL types are **camelCase and NOT the REST vocabulary** (both accepted,
      unknown rejected locally), `maxParallelism` needs a TMSL `sequence` wrapper, the command is built with
      `Utf8JsonWriter` so a quoted table name cannot alter its structure, and **`refresh` is the ONLY verb
      exposed** — no generic `dax_tmsl(command)`, since the same `ExecuteNonQuery` path would run
      `createOrReplace`/`delete` and turn a read-only provider into arbitrary model mutation.
    - **`notebook_definition` is DROPPED, not pending** (§4 had listed it): raw base64 parts in SQL is
      the shape rule 2 exists to prevent, the call is a ~20 s LRO, and `notebook_parameters` is the part
      anyone wanted.
    - **HOUSEKEEPING DONE: ONE registry for all six catalog-bound kinds.** `CatalogFunctionSet` grew from
      2 kinds to 6 (scalar/table/`table_sql`/`inout`/`collector`/`aggregate`) and owns the lookup, the ABI
      members AND the declaration rows; **SqlServer's six static dictionaries and DAX's hand-rolled dispatch are
      gone**. The prize is the KIND STRINGS: the host silently ignores an unknown kind, so a typo there makes a
      function quietly not exist — now written once, `aggregate` vs `aggregate_spill` decided in one place.
      `FunctionsMetadata.Declaration` gained `ParamCount`/`ReturnType` (not host-read columns — the SqlServer
      catalog builds the same declarations as a five-column T-SQL `UNION ALL`, so one producer feeds both). The
      `__all__` sentinel now throws LOUDLY on SqlServer rather than silently dropping such a function. What
      stayed provider-specific: the fallback to a DISCOVERED routine, and the in-out isolation wiring. New
      `FabricRowBuilder` replaced the per-function parallel-builder plumbing (strict about type: a string into a
      timestamp column throws rather than yielding NULLs that look like "the service returned nothing");
      `fab_delta_info` was moved onto it deliberately, because it is the only function on that path with a
      HERMETIC gate. Gate: all **11** service suites over the six kinds green + hermetic 62/5573.
- **THE `_each` MOVE: the PROVIDER declares its per-row form, the host stopped inventing one — DONE
  2026-08-02 (user-directed).** `FabricatorSchemaEntry::AddTableFunction` used to synthesise a
  `<name>_each` table-in-out alias for **every `table`-kind function of every provider**. That made a
  SQL-Server semantic (CROSS APPLY / per-row EXEC) the HOST's business and produced entries that can only
  fail wherever there is nothing to apply per row — **measured: 30 of the 70 names on a Fabric attach were
  dead `_each` siblings**, all advertised in `duckdb_functions()`.
  - Now: `SqlServerBackend.FunctionsSql` emits `<routine>_each` itself as an ordinary **`inout`**
    declaration (only for routines that TAKE parameters — nothing to apply per row otherwise), and it
    arrives through `AddInOutFunction` like any other provider-declared in-out. **No new kind was needed**:
    the unified param protocol lets the declaration carry a `Params.TableInput` field, which is what made
    the whole thing expressible.
  - **The `_each` SUFFIX IS NOW A PROVIDER CONVENTION** (`SqlServerCatalog.EachSuffix` + `StripEach`): the
    provider chose the name, so the provider strips it to find the underlying routine. The host does not
    know the convention exists. A provider may name its per-row form anything.
  - DELETED from the host: the synthesis, the `inout_functions_` alias map, and `GetOrCreateInOutFunction`
    (54 lines) — the latter redundant because `SqlServerTvfEach` already computes the echo schema
    (input columns ++ TVF output) in C#, so the ordinary custom-in-out path serves it.
  - Gate `verify_functions` 15 → **27**: the `_each` is declared (`kind='inout'`) beside its routine AND
    still applies it per row, plus **zero** `cf%_each` — a C#-authored table function has nothing to apply
    per row and must get none. ⚠ The `cf_*` count asserted just above it is that check's POSITIVE CONTROL,
    and the `LIKE … ESCAPE` pattern was itself verified to match a synthetic name — a
    mangled escape made it pass for the wrong reason once.

- **THE UNIFIED PARAMETER PROTOCOL — DONE 2026-08-02 (behaviour-preserving; no ABI bump).** A function now
  declares **ONE parameter schema** whose every field carries its STYLE in Arrow field metadata
  (`fabricator.param_style` = `named` | `table`; ABSENT ⇒ positional). This replaced a split
  `Parameters` + `NamedParameters` pair plus a third `InputSchema` on the in-out/collector kinds.
  `dotnet/Fabricator.Abstractions/ParamStyle.cs` (`ParamStyle` / `Params`) is the whole protocol; C++ reads it
  as `FabricatorParamStyle` (`FetchFunctionParamSchema`'s `out_styles`, replacing `vector<bool> arg_is_named`).
  - **Why**: the split forced every consumer to reconstruct one ordering rule ("positions are `Parameters` ++
    `NamedParameters`"), and a host that got the NULL substitution off by one would corrupt a POSITIONAL value
    rather than error. With one schema, position IS declaration order and that bug cannot be written.
  - **⚠ BOTH ordering rules are DuckDB's, not ours** — verified in `bind_table_function.cpp`: *"Unnamed
    parameters cannot come after named parameters"* and *"Table function can have at most one subquery
    parameter"*. `Params.Validate` moves those from CALL time to DECLARATION time. Named on a SCALAR is a
    declaration ERROR (DuckDB `ScalarFunction` has no named-parameter concept), never silently ignored.
  - **⚠ A table input is POSITIONAL-ONLY, and that is forced**: the binder's named-parameter path sets the
    argument name and the subquery branch then ignores it, so `f(t := (SELECT …))` silently binds as THE
    positional table arg. It MAY sit between positionals — DuckDB pushes a placeholder for the subquery slot
    (`parameters.emplace_back()`), so later positions keep their index. Its declared `StructType` is carried
    for US only: DuckDB registers `{LogicalType::TABLE}` and never sees it, so any schema validation is a
    BIND-TIME check of our own (not built).
  - `param_count` is **derived** (`Params.DeclaredCount`), excluding the table input so the number keeps
    meaning "arguments you pass a value for". It is not host-read at all (registration reads 3 columns) but IS
    user-visible via `fabricator_functions()`. Retired: `SqlGen.ParamSchema` + the `fabricator.named` tag.
  - **⚠ THE COMPILER FINDS ALMOST NONE OF THIS.** Removing an interface member leaves `override`s of a
    BASE-CLASS member compiling happily as DEAD CODE — ~25 declarations would have silently stopped being
    read. The gate is a GREP (zero live `fabricator.named`), not a green build. 18 classes that hold the two
    halves apart keep their shorthand via an EXPLICIT interface implementation
    (`Schema ITableFunction.Parameters => Params.Combine(...)`); consequence to know: reading `Parameters` off
    a CONCRETE subclass yields only the positional half.
  - **⚠ Do NOT script structural edits to C#.** A brace-matching insertion loop ran away (no damage — it never
    reached its write). A single-pass anchored insertion with an explicit class→interface map is the safe form.
  - **POSITIONAL and/or NAMED constant args now work on an IN-OUT / COLLECTOR too, not just named** (user
    requirement, 2026-08-02). The old bind marshalled `input.named_parameters` ALONE, which was fine only
    while cost args were named BY CONVENTION; the moment one could be declared positional, the signature
    accepted `f((SELECT …), 3)` and the 3 was **silently dropped before reaching C#** — a half-offered
    capability, worse than refusing it. `FabricatorMarshalInOutArgs` now walks the DECLARED order:
    TABLE_INPUT consumes its reserved slot and emits nothing (DuckDB pushes a placeholder Value for the
    subquery, so skipping the slot would shift every later positional), POSITIONAL takes the next
    `input.inputs` value, NAMED takes the supplied value or a typed NULL. Demo + gate: the global
    `fabricator_mix(<input>, factor, bias := k)`.
    - ⚠ **A named parameter must not be a DuckDB RESERVED WORD** — the demo first used `offset :=` and the
      call was a *parser* error, which reads as a broken function rather than a bad name.
  - **⚠ TWO DEFECTS THAT BOTH TIERS COULD NOT SEE, found by reading `duckdb_functions()` directly.** (1) With
    the input table a declared parameter but the host still tagging every declared parameter as named, `input`
    LEAKED into in-out/collector signatures as `input := STRUCT(…)`; an extra OPTIONAL named parameter breaks
    no call, so nothing failed. (2) For in-out/collector the OLD `Parameters` meant *named cost args*, so
    unflagged fields silently became POSITIONAL and `fabricator_delta_write(…, path := '…')` stopped binding.
    Both are now gated by asserting the SIGNATURE itself (verify_global_functions), which is the only thing
    that can catch "accepts an argument the implementation never receives".
  - **⚠ Apache.Arrow 23 cannot even CONSTRUCT `new StructType(empty)`** — `ArgumentNullException('fields')` on
    a non-null EMPTY list, so the message names the wrong problem. It fires in a STATIC FIELD INITIALIZER,
    taking down `CustomFunctions` and, through `ListGlobalFunctions`, silently dropping every global function
    registered after it — the visible symptom was an unrelated table function "not existing". Hence
    `Params.TableInput` uses a scalar placeholder when no columns are declared. This extends the known
    zero-field hostility one step earlier than export/import.
  - Gates: hermetic **63/63 — 5685** and service **44/44 — 1458**. The protocol refactor alone was
    5664/1446 — IDENTICAL to pre-refactor, which is the behaviour-preservation claim; the rest is the new
    signature/mixed-arg and `_each` coverage.
  - **THE SQL SERVER BINDING — BUILT + LIVE-VALIDATED 2026-08-02 (§9h). This closes §8's "largest remaining
    gap in reach", and building it found TWO SHIPPED BUGS.** The whole set was bound to a OneLake **Delta**
    attach, so a dbt project on a Fabric **Warehouse** over T-SQL could not call even
    `refresh_sql_endpoint`. Now: `ATTACH 'Server=<ep>.datawarehouse.fabric.microsoft.com;Database=LH'
    AS w (TYPE fabricator, SECRET fabric_sp)` → `w.fabric.refresh_sql_endpoint()` (the two ATTACH options this
    originally required are now INFERRED and renamed — §9n below).
    **No ABI and no C++ change** — `fabricator_storage.cpp` already forwards unknown ATTACH options as JSON.
    - **The recorded diagnosis ("credential plumbing") was the SMALLER half.** The real blocker: the function
      context held the OneLake **ROOT** and parsed workspace+item out of it, so the set was structurally
      unreachable from any other provider. ⚠ **The reason recorded here — "a Fabric SQL connstr supplies
      neither, its host is an opaque per-workspace routing GUID" — is FALSE, corrected 2026-08-03 (§9n):** the
      host's second base32 label IS the workspace GUID and `Database` IS the item. The refactor was still
      needed; only that justification was wrong. Context is now `(Workspace, Item, Credential)`, each provider supplying the pair its
      own way. `Root` had exactly two uses, both in `FabricApiClient`.
    - Credential rides a connstr marker (`;FabricatorFabricCred=`), the mechanism already proven by
      `AccessTokenKeyword` + `FabricatorDeltaCred`. **⚠ ORDER IS LOAD-BEARING** — the access-token marker means
      "everything after me is the token", so this one is appended AFTER and stripped BEFORE it.
    - **⚠ A pre-minted `access_token` is deliberately NOT carried** (SQL audience ≠ `api.fabric.microsoft.com`
      ⇒ guaranteed 401); carrying nothing falls through to the ambient chain, which works on and off Fabric.
      **`azure_tenant_id` — declared in `SecretFields` since the beginning, consumed by nothing — is now
      load-bearing**: SqlClient infers the tenant from the server, `ClientSecretCredential` cannot.
    - **⚠ The gate is the HOST (`*.fabric.microsoft.com`), NOT `ServerProfile.IsWarehouse`** — `EngineEdition
      == 11` also means **Synapse serverless**, and `IsWarehouse` would force profile detection (a connection)
      at ATTACH just to decide registration.
    - **⚠ BUG 1 (pre-existing): `lakehouses()` and `warehouses()` THREW ON EVERY CALL.** The
      `workspace :=` pass added the `args[0]` read to every catalog-bound table function but the declaration to
      all except these two; the base sizes the args array from the declared count ⇒ `IndexOutOfRangeException`,
      total not partial. It landed one day AFTER both were live-validated, and their only gate is live.
    - **⚠ BUG 2 (pre-existing): EVERY timestamp on the hand-rolled functions read as JANUARY 1970** — 15 sites
      / 5 files incl. the flagship refresh, all four job functions, the notebook runner, both semantic-model
      functions. `new TimestampArray.Builder()` **defaults to MILLISECOND** while the columns declare
      MICROSECOND; nothing reports the mismatch, so the host faithfully reads a number 1000× too small. It
      survived live validation of every affected function because each was checked for status/ids and **nobody
      looked at the times**. Functions on `FabricRowBuilder` were immune (it builds FROM the declared field);
      the fix gives the rest that property via one shared `TsType`/`TsBuilder()`.
    - `__all__` is now IMPLEMENTED on SqlServer (`ExpandAllSchemas`, lazy + `schema_filter`-aware), superseding
      §9g's "rejected loudly" — that was correct only while nothing used the sentinel.
    - Gate `verify_functions` 13 → **15** (negative control + a `cf_*` POSITIVE control, mutation-tested); the
      positive live path is manual, like `verify_dax`.
  - **VARIABLE LIBRARIES — 10 functions BUILT + LIVE-VALIDATED end to end (2026-08-03), §9j.** Fabric's
    per-environment config item (default value set + alternative sets, exactly one ACTIVE, flipped per stage by
    a deployment pipeline). Reads: `variable_libraries` / `variables(lib, value_set := …)` /
    `variable_value_sets` / the scalar `variable(lib, name)`. Writes:
    `create_variable_library` / `set_variable` / `set_variables_json` /
    `set_variable_override` / `set_active_value_set` / `drop_variable_library`. No new
    dependency — the pinned 2.18.0 SDK already carries `FabricClient.VariableLibrary.Items`.
    - **Why it earns its place: an `ItemReference` variable stores exactly `{workspaceId, itemId}`, which is what
      our own `workspace :=` / `item :=` overrides consume.** Proven live:
      `refresh_sql_endpoint(item := variable('cfg','target') ->> 'itemId')` refreshed the real
      lakehouse's 21 tables. So a dbt project reads its target from the library instead of hardcoding it.
    - **⚠ There is NO effective-value API** — the typed model stops at `ActiveValueSetName` and every value lives
      in the item DEFINITION as base64 parts, so resolution is ours (decode `variables.json`, overlay
      `valueSets/<name>.json` by name). Same shape as `notebook_parameters`.
    - **⚠ The definition API is WHOLE-DOCUMENT and has no ETag.** A write that sends only the part it changed
      DELETES the value sets and settings, so every setter reads all parts and writes all parts back — and that
      read-modify-write is LAST-WRITER-WINS. `set_variables_json` is the single-call declarative
      alternative (it also REPLACES, so an omitted variable is removed).
    - **⚠ Reads and writes are LONG-RUNNING OPERATIONS: the 13-step live script took 7m39s** for ~15 definition
      operations. Two `variable()` calls in one SELECT list are two reads — the "no cache across calls"
      decision is right for configuration but not cheap.
    - **`variable` is declared CONSISTENT and that is load-bearing** — our scalar default is VOLATILE, and
      a volatile function is never folded, so the default would cost one LRO PER ROW.
      `BoundFunctionExpression::IsFoldable()` is exactly `stability != VOLATILE`. (As a table-function argument
      it is evaluated once regardless: `bind_table_function.cpp` checks only `IsScalar()` then calls
      `EvaluateScalar(…, allow_unfoldable: true)` at bind.) Consequence: a PREPARED statement bakes the value in.
      Conversely **every WRITE function must stay VOLATILE** or it may run at bind, once for N rows, or be elided.
    - **⚠ CREATION is refused for a service principal** (`FeatureNotAvailable`), contradicting the docs' *"the
      variable library REST APIs support service principals"* — same as `ResetShortcutCache`, same error code as
      notebook creation. **Scope settled by measurement, not inference:** a library created by another identity
      is then fully driveable by the SP (definition GET/PUT, properties update, list all permitted) ⇒ principal-
      scoped and specific to creation, exactly like notebooks. **The error names the wrong cause** — the feature
      IS available. So creating the library is a one-time human action; everything after automates.
    - **⚠ Microsoft's docs contradict themselves in FOUR places, each a silent wrong answer if guessed**: the
      value-set folder is spelled both `valueSets\…` and `valueSet/…` (we read either + normalize `\`, write
      plural); `type` casing is unstable (`"String"` beside `"boolean"`) so types pass through VERBATIM; the REST
      page's type table OMITS `Guid` and `ConnectionReference` (so no closed enum — an unknown type parses as
      JSON, falling back to string, with the service as the validating backstop); and **`VariableOverride.value`
      is typed `String` and that is wrong** — mutation-testing showed it breaks **Integer** too, not just the
      object types. Variable names are NOT case sensitive, so both the read overlay and the write upsert match
      that way (otherwise an upsert appends a second entry and invalidates the library).
    - **⚠ THE DEFECT LIVE VALIDATION FOUND, and why the offline test could not:**
      `JsonElement.GetRawText()` returns the raw SOURCE SPAN, so a pretty-printed object value arrived in a SQL
      column as `{\r\n        "workspaceId": …}`. It is a READ-side bug (the portal or a git sync may indent, so
      normalizing belongs on read) fixed by re-serializing through a `Utf8JsonWriter`. **The offline round trip
      was blind to it because `ToJsonString()` emits compact JSON — the harness was reading back its own
      formatting convention. A round trip only tests the shapes you generate.**
    - Coverage: the live lifecycle incl. three negative controls (unknown value set, undeclared override,
      mistyped value) each erroring with the library left unchanged — plus **`dotnet/Fabricator.Bridge.Tests`,
      THE FIRST TEST PROJECT FOR BRIDGE LOGIC** (2026-08-03): 47 cases × {net10.0, net8.0} in ~100 ms over the
      format, incl. the write→read round trip, the pretty-printed case, and a named test per documentation
      contradiction. **Mutation-tested with three mutants, each killed at exactly its own tests** (folder
      spelling → 6; the docs' `value: String` → 4, incl. Integer; `Render`→`GetRawText` → 2).
      - **⚠ It has NO `ProjectReference` to `Fabricator.Bridge`, and adding one would BREAK TIER 0** — the Bridge
        project-references **engineered-wood (a submodule)** plus Arrow/Fabric SDK/SqlClient/AWS/Azure, and tier
        0's defining property is needing no C++, no vcpkg and no submodules. It **compiles selected Bridge source
        files directly**, admission rule: *a file belongs there only if its closure is the BCL*. A forcing
        function, not a workaround — it rewards keeping parsing/resolution/rendering out of the Arrow/SDK
        boundary, which is why `FabricVariableLibraryFormat.cs` was split out in the first place.
      - **WIRED INTO TIER 0** as a SECOND JOB (`bridge`) in `installer-core.yml`, floor 47, both TFMs × both
        OSes. Three things about that wiring are deliberate and easy to undo by accident:
        - **A separate job, not another step** — the count tripwire reads the FIRST `Total:` in its output file
          (`head -1`), so a second `dotnet test` piped into the same file would leave this project's floor
          silently unchecked. A tripwire that looks armed and is not.
        - **The path filter lists `dotnet/Fabricator.Bridge/**`, not the individual linked file** — filtering the
          one file would silently stop covering the next one someone links, the same failure as the
          submodule-pointer omission (a filter that misses the change the gate exists to guard).
        - **Both OSes**, because the format code normalises PATH SEPARATORS and asserts on CRLF in stored JSON;
          pinning that on one platform is how an accidental `Path.DirectorySeparatorChar` /
          `Environment.NewLine` dependency hides.
        ⚠ The workflow is still NAMED `installer-core` for a historical reason only (renaming it renames every
        status check) — read it as "tier 0", not "the installer".
  - **SPARK SESSIONS — `sessions([workspace := …])` BUILT + LIVE-VALIDATED (2026-08-03), §9k.** 27
    columns over the WORKSPACE-scoped `Spark.LivySessions.ListLivySessions(workspaceId)` — so, unlike
    `job_instances(item)`, it answers "what is on the Spark compute" with **no item argument and one
    request**. No ABI/C++ change, no new dependency. It is the Spark half in DETAIL (queued vs running time,
    runtime version, attempt number, `spark_application_id`, high-concurrency — none of which a job instance
    carries), not a better job list: job instances still cover Pipeline/Dataflow/TableMaintenance, which never
    appear as sessions.
    - **⚠ TWO CLAIMS I WROTE FIRST AND THE DATA FALSIFIED.** (1) "Interactive sessions have no job instance" —
      wrong: all 115 sessions carried a `job_instance_id`, and a spot-checked one WAS in that notebook's
      `job_instances` history, so it is a real join key. (2) **`JupyterSession` does NOT mean
      "interactive"** — every one observed was created by the RunNotebook JOB api with nobody clicking; the
      value names the session KIND (a Jupyter kernel), not its trigger. Whether a portal-driven session lacks a
      job instance is **UNVERIFIED** (no such session existed in the data) — do not restate it either way.
    - **⚠ The SAME work is labelled differently in TWO columns.** One identical `job_instance_id` reads
      `job_type='JupyterSession'`/`state='Succeeded'` as a session and `job_type='RunNotebook'`/
      `status='Completed'` as a job instance ⇒ a predicate carried across matches NOTHING. Values pass through
      VERBATIM (normalising would hide which API answered; both are extensible enums that can grow).
    - **⚠ THE SQL VALUES ARE NOT THE SDK MEMBER NAMES**: the member is `NotStarted`, the column says
      **`Not Started` — with a space** (captured live), while `InProgress` has none ⇒ a predicate derived by
      reading the enum is wrong. Observed: `Not Started`/`InProgress`/`Succeeded`; `Failed`/`Cancelled`/
      `Unknown` are declared but their SPELLING IS UNCONFIRMED. Also **casing differs across columns of the SAME
      ROW** — `item_type` is lower-case (`notebook`) while `job_type`/`state` are PascalCase. And `submitter`
      (display name) was EMPTY on all 115 rows while `submitter_id` was populated on all 115 — group by the id.
    - **⚠ ALL 34 FIELDS SHIP — and a WRONG CONCLUSION OF MINE IS RECORDED HERE ON PURPOSE.** The seven
      ALLOCATION columns (driver/executor cores+memory, num_executors, dynamic allocation ×2) were NULL in all
      116 observed sessions and I first DROPPED them, claiming "the list endpoint never populates them". Wrong.
      NULL across finished sessions was correctly rejected as insufficient ("only reported while running"
      explained it equally well), so a session was manufactured (`run_notebook(…, wait_seconds := 0)`) and polled
      to `InProgress` — still NULL. That kills the LIFECYCLE explanation and says NOTHING about the structural
      one, **because the variable that actually differed is session KIND**: by `runtime_version` the history is
      `jupyter1.0`/`JupyterSession` (Python notebook runs) plus `2.0`/`SparkSession`+`SparkBatch` (SYSTEM-managed
      `Lakehouse Operations`/`Table Maintenance`) — **no user-authored PySpark session at all**, the only kind
      carrying a real executor allocation. A NULL there means "this workload has no Spark allocation", i.e.
      information. PySpark population is EXPECTED but UNVERIFIED here. **Standing lesson: eliminating one rival
      explanation is not eliminating all of them — name the variables you did NOT control before generalising
      from a negative result.** (`executor_cores` is VARCHAR because the SDK types it `object`.)
      **The manufactured session was still worth it — it is the POSITIVE CONTROL for the headline feature**:
      every prior row was `Succeeded`, so the function had never once been observed doing what it exists for;
      polling showed `InProgress` with `running_seconds` 26→53.
    - **⚠ Live cell OUTPUT is an API absence, not a gap to fill**: `spark_application_id`/`resource_uri` are
      POINTERS, and the whole SDK assembly has no log-fetching method. This gets you to the session, not inside it.
    - **`Duration` is a `{value, unit}` PAIR, not a TimeSpan** — a CLASS (absent-able) whose `TimeUnit` is an
      Azure EXTENSIBLE enum (Seconds/Minutes/Hours/Days). Normalised to seconds as DOUBLE, compared against the
      TYPED members (a rename is then a compile error), and an **unknown unit yields NULL, not the raw number**:
      a column mixing seconds with minutes makes every `ORDER BY` wrong, and wrong is worse than absent. ⚠ It
      also collides by name with `Apache.Arrow.Types.TimeUnit` — hence the alias.
    - **`FabricRowBuilder.EndRow()` now VERIFIES every column got exactly one value** and names those that did
      not. Identity there is a bare INDEX and this function writes 27, so the off-by-one the class exists to
      prevent was one edit away; a skipped column used to surface as a length mismatch deep in `RecordBatch`
      construction, or — two skips in different rows — as a batch that builds fine with values in the WRONG
      rows. All 7 pre-existing sites were audited against their declared counts FIRST (all correct), so the
      guard changed nothing. Also gained DOUBLE support. Hermetic **63/63 — 5685** (unchanged ⇒
      behaviour-preserving; `fab_delta_info` is what exercises the shared builder offline). The function itself
      is live-only, like `verify_dax`.
  - **ATTACH OPTIONS INFERRED + RENAMED `API_WORKSPACE`/`API_ITEM` — DONE 2026-08-03 (breaking), §9n.** A Fabric
    SQL attach now needs NO Fabric-specific option: `ATTACH 'Server=<ep>…;Database=MyLH' AS w (TYPE fabricator,
    SECRET fabric_sp)`.
    - **⚠ THE ENDPOINT HOST IS NOT OPAQUE — this file said it was, and that was FALSE.**
      `<base32(cluster GUID)>-<base32(WORKSPACE GUID)>.datawarehouse.fabric.microsoft.com`: 26 unpadded
      lower-case RFC-4648 base32 chars per label = 130 bits carrying a 16-byte GUID, and the **second** label
      decodes **little-endian** (.NET `Guid.ToByteArray()` order) to exactly the workspace id. Established by:
      all 3 lakehouses AND a warehouse in one workspace returning a BYTE-IDENTICAL host while their own
      `sql_endpoint_id`s matched neither label ⇒ label 2 = workspace, label 1 = a workspace-level SQL cluster.
      The **item** needs no decoding — on a Fabric SQL endpoint `Database` IS the item.
    - **Live proof is DISCRIMINATING, not just "rows came back"**: two attaches differing ONLY in `Database`,
      same server, no options ⇒ `LH` **21** tables vs `LH2` **0**; and `API_ITEM 'LH2'` on the `LH` attach ⇒ 0,
      so an override still outranks the default.
    - **⚠ The inference must NEVER GUESS.** The encoding is UNDOCUMENTED, one tenant, one region ⇒
      `WorkspaceIdFromHost` returns **null** on any doubt (wrong suffix / label count / label length / a char
      outside the base32 alphabet) and the caller falls back to demanding the option. A WRONG workspace id would
      aim REST calls at a different workspace the identity may well have access to, so silence is the only
      acceptable failure. The enumerate-and-match fallback (list workspaces → compare each item's endpoint
      connstr to this host) is **deliberately NOT built**: O(workspaces × items) REST calls AT ATTACH to convert
      a clear error into a slow success, on the one path whose defining property is costing no round trip.
    - **Why RENAMED and not merely optional**: `WORKSPACE`/`ITEM` read as if they selected the ATTACH TARGET.
      They do not — they scope the `fabric.*` functions only, and two attaches differing solely in `ITEM` expose
      IDENTICAL tables (the option is invisible until a function runs). **⚠ The old names now ERROR rather than
      being ignored, and that guard is load-bearing**: unknown ATTACH keys are dropped for forward-compat, so
      leaving them unhandled would silently fall back to the inferred default — redirecting a refresh at the
      `Database` item instead of the named one, with no message. Verified live.
    - **The OneLake side ALREADY did this — no work needed.** `ParseOneLake` takes the container as the workspace
      and the first segment as the item, and BOTH resolvers short-circuit on `Guid.TryParse`, so a **pure-GUID
      root costs ZERO resolution calls** (verified live: 21 tables + `fabric.sessions()` 116).
    - **Which identifiers the APIs accept** (easy to assume wrongly): the Fabric REST API is **GUID-ONLY** —
      every SDK method takes `Guid workspaceId`/`Guid itemId`. Accepting a NAME is purely our convenience layer
      (`ResolveWorkspace`/`ResolveItem` list + display-name match, cached per catalog in `_idCache`), so a name
      costs one listing on first use and a GUID costs none.
    - **Gate: `Fabricator.Bridge.Tests` 47 → 85, tier-0 floor raised.** `FabricSqlEndpointHost` is BCL-only BY
      DESIGN — it hand-rolls the base32 decode (the BCL has none) and the connstr parse instead of using
      `SqlConnectionStringBuilder` — so the undocumented part is testable OFFLINE. **It paid for itself
      immediately: the tests failed on first run and caught a real bug** (the label extraction took the substring
      after the LAST dot, yielding `datawarehouse` instead of the encoded pair).
  - **JOB-INSTANCE FAN-OUT — DONE 2026-08-03 (breaking on ONE parameter), §9m.**
    `fabric.job_instances([item := …] [, item_type := …])`: omitting `item` fans out over every item of
    `item_type`, one `ListItemJobInstances` per item, with `item_name`/`item_id` APPENDED (appended, not
    prepended — D4 keeps `SELECT *` additive). Live: `'Notebook'` → 53 runs across 2 notebooks; `'Lakehouse'` →
    LivySession 47 / TableLoad 9 / TableMaintenance 7.
    - **⚠ `item` moved POSITIONAL → NAMED and HAD to**: DuckDB arity is fixed, so a positional parameter cannot
      be omitted, and omitting it is what selects fan-out. Shipped in the SAME breaking window as §9l so callers
      migrate once.
    - **Why here and not in `sessions()`**: sessions are already workspace-scoped in one request but Spark-ONLY;
      job instances cover every item kind and the API is strictly per-item, so enumerating items is the only way
      to ask "what ran in this workspace". The two CROSS-VALIDATED — the lakehouse fan-out's `LivySession` = 47
      is exactly the 47 `Session Livy Run` rows `sessions()` reports.
    - **Two deliberate refusals**: omitting BOTH `item` and `item_type` ERRORS rather than sweeping the workspace
      (unbounded × per-principal throttle), and there is **no `max_items` cap** — a cap would under-report while
      looking complete. One item's failure fails the whole statement, on purpose (a partial result that looks
      complete is worse). Cost is stated in the error, not hidden.
    - **⚠ A job instance's `StartTimeUtc`/`EndTimeUtc` are ISO STRINGS** (a Livy session's are `DateTimeOffset`)
      ⇒ `FabricRowBuilder.Iso`, not `.Ts`. The compiler catches it. Binding moved to `FabricRowBuilder` (10 cols).
    - **THE SECOND AXIS — `sessions(all_workspaces := true)` — ALSO DONE (2026-08-03).** The job fan-out
      enumerates ITEMS inside one workspace; this enumerates WORKSPACES (one `ListWorkspaces` + one
      `ListLivySessions` each) and appends `workspace_name`/`workspace_id`. Mutually exclusive with
      `workspace :=` (errors — naming one workspace and asking for all is contradictory). `all_workspaces` is a
      REAL `BooleanType` read via `FabricArgs.Bool`, safe because this binding reads args individually — the
      "BOOLEAN named parameter silently reads NULL" hazard is specific to `FabricRowsFunction`.
      - **⚠ THE MULTI-WORKSPACE AGGREGATION IS UNVERIFIED — the tenant exposes exactly ONE workspace to this SP**,
        so a fan-out result is INDISTINGUISHABLE from the single-workspace one. Do not read a green run as
        coverage; a second workspace is the only thing that settles combining/attribution/paging.
      - **What IS proven is that the fan-out PATH executes, via a constructed discriminator** rather than a row
        count: attach by a **GUID** root so the single-workspace default carries no name ⇒ `sessions()` gives
        `workspace_name = NULL` while `sessions(all_workspaces := true)` gives `Test`, which could ONLY come from
        `ListWorkspaces`. (Hence `workspace_name` is NULL in single mode on a GUID default — same rule as the job
        fan-out's `item_name`: echo what the caller knows, never pay for a listing to restate it.)
      - A per-workspace failure fails the WHOLE statement (consistent with the item fan-out). **Unvalidated for
        the interesting case** — "can see a workspace but cannot list its sessions" has never been observed here.
        If that proves common the answer is an `error` COLUMN, not a silent skip.
    - **§9m carries the FAN-OUT VERDICT for every other candidate.** The deciding pattern: fan out when the
      per-item call is a cheap LIST; REFUSE when it is a long-running definition read. So
      `semantic_model_refreshes` (over `semantic_models()`) — **dropped from the recommendation by the user
      2026-08-03: not needed** — then `mirroring_status`/`mirrored_tables`, `data_access_roles`, the
      deployment-pipeline trio, `list_shortcuts`; **`git_status` over WORKSPACES is DEFERRED (user, 2026-08-03)
      until a git-connected workspace exists to test against** — writing it blind would ship an untested
      promotion surface, the same reason P3 is "wired but NOT live"; and `notebook_parameters` + `variables` are
      **NO** — each is a ~20 s LRO, so fanning out multiplies a multi-minute operation behind a call site that
      looks cheap.
  - **THE `fabric` SCHEMA — DONE 2026-08-03 (BREAKING, no aliases), §9l.** `dbo.fabric_sessions()` →
    **`fabric.sessions()`**: one dedicated schema, the `fabric_` prefix dropped from all **51** functions.
    C#-only — no ABI, no C++ change. Why: the `__all__` sentinel declares each function once PER DISCOVERED
    SCHEMA, so on `dbo`+`dbt` the set rendered as **102** rows in `duckdb_functions()` (measured **51** after);
    and it separates a DATA schema discovered from storage from a FUNCTION namespace the provider declares.
    - **⚠ IT IS NOT A RENAME — it is a catalog-structure change.** `fabricator_catalog.cpp:99` SILENTLY SKIPS a
      declared function whose schema the provider did not DISCOVER (`if (sit == schemas_.end()) continue;`) —
      deliberately, since that is how ATTACH `schema_filter` reaches functions. So the schema must be ADVERTISED
      by each hosting provider, gated on the SAME condition as the registration:
      `DeltaCatalog.CatalogSchemaNames()` (gate `IsOneLake`) and `SqlServerBackend.SchemasMetadata()` (gate
      `IsFabricEndpoint`). **MUTATION-TESTED because "silently" is the claim**: with the Delta gate reverted the
      ATTACH still SUCCEEDED with no error or warning, `duckdb_functions()` showed 0 in `fabric`, and the call
      failed as *"Table Function with name sessions does not exist! Did you mean main.seq_scan?"* — pointing
      nowhere near the cause.
    - **⚠ The name must NOT join the `__all__` EXPANSION list** — that list means "every DATA schema", and
      feeding `fabric` in would re-declare the provider macros + `fab_delta_info` inside it, restoring the very
      duplication this removes. Hence `CatalogSchemaNames()` is SEPARATE from `SchemaNames()`, and
      `ExpandAllSchemas()` still reads `SchemasSql` directly. The two lists look redundant and are not.
    - `fabric` is deliberately EXEMPT from `schema_filter` on both providers (that option scopes DATA discovery;
      deleting the whole Fabric API because someone narrowed their tables would be a surprising coupling —
      `function_filter` is the option for functions). DDL into it is REFUSED
      (`DeltaCatalog.RejectFunctionSchemaDdl`), because `CREATE TABLE cat.fabric.t` would otherwise create a
      real `fabric/` folder that the NEXT attach discovers as a data schema — the namespace quietly stops being
      separate. Only where the synthetic schema exists; elsewhere `fabric` is a name a user may legitimately use.
    - **⚠ TWO NEGATIVE CONTROLS WERE ABOUT TO GO VACUOUSLY GREEN**: `verify_delta_catalog_functions` §4 and
      `verify_functions` both asserted `function_name LIKE 'fabric\_%'` = 0, which after the rename matches
      NOTHING whether or not the set is registered. Both now key on `schema_name='fabric'`; the Delta suite also
      asserts the SCHEMA is absent, and THAT is the load-bearing one — mutating the `IsOneLake` gate leaves the
      function-count assertion passing (a local root registers nothing to leak) and is caught only by the schema
      assertion (verified: the mutant failed at exactly that line). 27 → **28**.
    - **Rename mechanics worth reusing: the substitution is NOT `s/fabric_//g`.** Three token classes had to be
      protected first — **`fabric_sp`** (the SECRET in every example; stripping gives `sp` and breaks every
      snippet), **`fabric_*`** (the GLOB in prose; a blind strip leaves a meaningless bare `` `*` `` — it became
      `fabric.*`), and **`<cat>.dbo.fabric_<fn>`** (33 qualified call sites in the README, 12 in docs; stripping
      only the prefix leaves `<cat>.dbo.<fn>`, wrong in a way that still LOOKS right). The glob was found because
      the occurrence COUNT disagreed by one with the number of function references — **an arithmetic disagreement
      between two ways of counting is what caught it**, not review.
    - Gates: hermetic **63/63 — 5686**; live end-to-end (`fabric` beside `dbo`/`dbt`, 51 functions ×1 each, a
      table fn, a named-parameter call, a scalar, and the DDL refusal).
  - Output shape rule (D4): typed flat columns + one raw-JSON column for polymorphic parts; **no STRUCT
    wrapping** (adding a column is additive for `SELECT *`; adding a struct FIELD changes a column's type
    and breaks bound views), no JSON-only. Every `table`-kind function also gets a dead `_each` sibling —
    pre-existing host behaviour, shared with SqlServer's custom table functions.
  A table function that sets neither `serialize` nor `deserialize` still takes part in DuckDB's
  **common-subplan optimizer** (1.5.4+), which dedups subplans by SERIALIZING each operator and hashing
  the bytes. `FunctionSerializer::Serialize` writes only name+arguments in that case and **does not
  throw**, so `fabricator_scan`'s signature carried NO table identity — ours lives in
  `ArrowStreamBindData`. `LogicalGet::Serialize` does contribute returned_types/names/filters, and
  `common_subplan_optimizer.cpp:120` canonicalizes `table_index` to 0 before hashing ⇒ **two scans of
  DIFFERENT tables that share a schema hashed IDENTICALLY**, one was materialized as a CTE, and both
  consumers read the FIRST table's rows. `ArrowStreamBindData::Equals` was correct all along and is
  never consulted — the optimizer compares BYTES, not `FunctionData::Equals`. That is the whole trap.
  - Found by reading **hugr-lab/mssql-extension#211** (same defect, same fix) and checking whether we
    had it. We did, on **every provider** — one `FabricatorTableEntry` / one `fabricator_scan` serves
    SQL Server, Delta and DAX; reproduced on the first two, and DAX reaches the identical path via
    `DaxCatalog.ScanTable`. DAX was arguably the most exposed in practice: it is read-only and the
    failing shapes are pure read shapes (a measure over table A vs table B).
  - **Affected shapes** (all silent): identical aggregate subplans over two same-schema tables — a
    `UNION ALL` of aggregates, or two scalar subqueries. **Unaffected:** joins / EXCEPT / INTERSECT /
    plain unions of rows (bare gets are not materialized), differing column names or types (they ARE in
    the signature), same-table-different-filter (the differing `Filter` child changes the subplan
    signature — safe by plan shape, not by design), and every global/discovered function (their args
    ride in `parameters`, which IS serialized).
  - **Fix**: `FabricatorScanSerialize` writes catalog/schema/table **plus the pushed spec**
    (`filter_json`+constants, `native_filter_sql`, `top_n`, `order_by_json`, `at_unit`/`at_value`) so
    two differently-pushed scans of ONE table cannot collapse either; identical scans still dedup, which
    for a remote provider is a real win. Gate `verify_delta_subplan_dedup` (36), mutation-tested.
  - **`PRAGMA verify_serializer` does NOT work on a fabricator catalog scan, and never did.**
    `LogicalGet::Deserialize` calls `FunctionSerializer::DeserializeBase` UNCONDITIONALLY (before it
    checks has_serialize), resolving the function BY NAME against `TABLE_FUNCTION_ENTRY`; the catalog
    scan is handed out by `GetScanFunction` and is not a registered catalog function ⇒ "Failed to find
    function fabricator_scan()". So `FabricatorScanDeserialize` is UNREACHABLE — and must still exist,
    because `Serialize` only emits bind data when BOTH callbacks are set. Do not "clean it up".
- **⚠ CROSS-ENGINE GAP MEASURED (2026-08-01). The READING half is now FIXED; the WRITING half is still
  OPEN — we do not emit `commitInfo.isBlindAppend`, so a Fabric Spark transaction ABORTS against our
  concurrent append whatever the table declares.** Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §10.6 +
  [docs/ew-master-migration.md](docs/ew-master-migration.md) §isBlindAppend §4a.
  - **THE WRITE HALF IS CLEARED TO BUILD, and its scope is narrower than "fixes the aborts".** The blocking
    uncertainty was upstream PR #24's report that a whole-table read declaration conflicts even with a blind
    append; **reading `ConflictChecker.scala` at the `v4.2.0` tag REFUTES that for WriteSerializable** — with
    the flag set, `changedDataAddedFiles` is `Seq()`, and that EMPTY list is what the predicate check runs on,
    so how broad the reader's declaration was cannot matter. It is applied one step EARLIER than the predicate
    comparison, not dodged. ⇒ **the planned prunable-predicate experiment is MOOT and was not run.** Emitting
    the flag would make Spark COMMIT on a `WriteSerializable` table and would change NOTHING on a
    `Serializable` one (blind appends are examined there by design — that abort is correct). Both halves of
    that prediction match the live A/B exactly. Since Fabric Spark's DDL refuses to SET `WriteSerializable`,
    the tables this helps are ones WE stamped — Spark honours a stamped value, it just cannot write one.
  - Live A/B on Fabric Spark 4.1.1.5.5 / **Delta-Lake 4.2.0** (`ConflictChecker.scala` re-read at the `v4.2.0`
    TAG — the Fabric build, not master): 200M-row table, Spark `DELETE … WHERE id % 7 = 3`, our append committed
    inside the window. `Serializable` ⇒ Spark ABORTS (`DELTA_CONCURRENT_APPEND`, naming our v8). `WriteSerializable`
    ⇒ Spark ABORTS TOO (naming our v23). Overlap PROVEN both times by Spark naming the concurrent version.
  - Cause, from Delta's source: `blindAppendAddedFiles = if (commitInfo.flatMap(_.isBlindAppend).getOrElse(false))
    addedFiles else Seq()`. An ABSENT flag = "not blind" ⇒ our appends land in `changedDataAddedFiles`, which is
    checked under BOTH levels. Confirmed three ways: the source; our commitInfo on disk (operation/engineInfo/
    operationParameters only); and Spark's `DESCRIBE HISTORY` showing `isBlindAppend` True for ITS blind append
    and blank for ours.
  - **UPSTREAM OFFER — MERGED as #32 (`12b0d39`), then CORRECTED TWICE by upstream; OURS IS RETIRED.**
    #33 found the `Exists` probe ran BEFORE the `try` (so it did not fix the case it was written for) and
    that guarding root kind + field PRESENCE misses field TYPE and the nested `v2Checkpoint`; one try/catch
    around the whole read-and-decode replaces all six guards. #35 then found **the argument both fixes
    rested on was never implemented** — nothing listed the log, so after Delta's metadata cleanup a
    hint-less read makes the table UNREADABLE, not merely slow. Full account:
    [docs/ew-master-migration.md](docs/ew-master-migration.md) §1. The host-side one-request read stays
    ours. And to settle the worry directly: **EW has NOT lost WriteSerializable support** —
    `StartTransaction` still defaults to it and `ConflictChecker` still implements the relaxation; what is
    missing is interop plumbing, not semantics.
    - **Writing the offer PROPERLY found a second bug**, because it needed an EW-level suite (upstream cannot
      run our sqllogictest): valid JSON that is not an OBJECT still threw, since `TryGetProperty` raises
      `InvalidOperationException` rather than returning false on a non-object root. Ported back; our suite is
      39 (§6). It also corrected an over-claim — `data.Length == 0` kills no test (`Parse("")` already raises
      `JsonException`), so it is documented as a fast path, not a load-bearing guard.
  - **The READING half was wrong too, in the OPPOSITE (unsafe) direction — FIXED 2026-08-01 (EW-only).**
    `ConflictChecker.IsBlindAppend` INFERRED blind-append from action shape ("only AddFiles"), so another
    engine's `INSERT … SELECT` from the same table — only adds, but it READ — was treated as blind and we
    skipped a check we owe. It now CONSUMES `commitInfo.isBlindAppend` when present and falls back to the
    inference (`InferBlindAppend`, unchanged) only when absent; a non-boolean counts as absent. Three
    non-obvious parts: the declaration outranks the inference in BOTH directions (each has a test); ABSENT
    keeps meaning "infer", since almost every commit in the wild omits the flag and defaulting to
    "not blind" would conflict on ordinary appends; and a **round-trip test** proves the flag survives
    `TransactionLog.ReadCommitAsync` — without it the verdict tests would be pinning dead code. No model
    change needed (`CommitInfo.Values` already keeps arbitrary keys). Mutation-tested; Table.Tests 727.
    - **⚠ "FIXED" MEANS FIXED FOR *DECLARED* COMMITS ONLY — we still DIVERGE FROM DELTA on the absent case, in
      the weaker direction (established 2026-08-03 by re-reading `ConflictChecker.scala` at `v4.2.0`).** Delta is
      `isBlindAppendOption.getOrElse(false)`: **absent ⇒ NOT blind**, so those adds stay in
      `changedDataAddedFiles` and ARE examined even under WriteSerializable. Delta even computes
      `onlyAddFiles = actions.collect{case f: FileAction => f}.forall(_.isInstanceOf[AddFile])` and **pointedly
      does NOT use it** for blind-append — so our fallback is precisely the inference Delta declined to make.
      Ours is a deliberate back-compat choice (EW emits no flag itself, so `getOrElse(false)` would make
      ordinary EW-to-EW concurrent appends start conflicting), NOT a claim of parity. Do not describe the
      reading half as "matching Delta".
    - Second, smaller divergence: Delta's WriteSerializable branch is guarded by
      `!currentTransactionInfo.metadataChanged`, so a metadata change in OUR OWN transaction re-examines blind
      appends. EW's `examineAdds = isolation == Serializable || !concurrentIsBlindAppend` has no such guard.
      Not investigated; note it before offering the reading half upstream as "Delta-equivalent".
  - **The fix must be TRUTHFUL:** Delta's definition is "the transaction READ NOTHING"
    (`readPredicates.isEmpty && readFiles.isEmpty`), NOT "the commit contains only adds" — deriving it from
    action shape would mark `INSERT … SELECT` from the same table as blind, and a wrong `true` makes other
    engines SKIP a check they should run (the unsafe direction).
    - **⚠ THE DECIDING SHAPE is `INSERT INTO t SELECT … FROM t …`** — an anti-join insert ("insert only rows
      not already there"), i.e. the standard dbt-incremental/dedupe pattern, so the COMMON case not an exotic
      one. It READS the target ⇒ not blind, yet emits **only AddFiles** ⇒ every action-shape derivation calls
      it blind. That is both why the reading half needed fixing and the trap the writing half must avoid.
    - **⚠ Whether we can derive the flag depends on AUTOCOMMIT vs EXPLICIT — measured, and it narrows the
      problem** (`DeltaCatalog.cs:1232`, gated on `_txnBuffer.IsExplicit(scanTxn)`): inside `BEGIN…COMMIT` a
      scan DOES stage a predicate / `StageWholeTableRead`, so EW's read set is non-empty and a derivation
      would correctly say "not blind"; in **autocommit nothing is recorded at all**, so the anti-join insert
      is indistinguishable from `INSERT … VALUES` and deriving would emit the lie. ⇒ derive on the
      buffered/explicit path; for autocommit either extend the (same, currently-gated) scan-time recording, or
      OMIT the field — today's behaviour, which costs only spurious aborts. Keep the asymmetry either way: a
      declaration may be DOWNGRADED to "not blind" by staged reads, never UPGRADED to "blind" by their absence.
  - **⚠ METHOD: this experiment was VOID FOUR TIMES and each void looked like a clean "no conflict".** The
    window must be PROVEN (Spark naming the concurrent version, or `readVersion` ordering), never assumed. What
    kept failing was OUR end — the append needed ~20 s (process start + CLR + ATTACH discovery), most of the
    DELETE's life. What finally worked: PRE-ATTACH the writer so firing costs only the commit, and make the
    DELETE genuinely expensive (a ~200-row delete finished in <17 s; `id % 7 = 3` rewrites nearly every file).
    Re-creating the table did NOT help — the warmth that matters is the SPARK CLUSTER's, so whichever leg runs
    second is fast; each level needs its own run in the cold first slot (`sparkprobe conflict <Level>`).
- **THE DELTA ISOLATION DEFAULT FLIP — DONE (2026-08-01, behaviour-breaking for CONCURRENT writers).** The
  catalog default is now **`serializable`** (was `write_serializable`), because the measurement below showed
  the old default made us the WEAKER writer than Fabric Spark on any table that declares no level — so the
  effective guarantee depended on which engine wrote. Single-writer behaviour is unchanged; concurrent
  read-write transactions now conflict-abort against a matching blind append where they used to commute.
  Explicit `isolation_level 'write_serializable'` restores the old behaviour, and a table's own
  `delta.isolationLevel` still overrides the catalog.
  - **⚠ The biggest practical effect is NOT the blind-append rule — ROW-LEVEL CONCURRENCY is a
    WriteSerializable-ONLY relaxation**, so under the new default concurrent disjoint-row DML on one file
    CONFLICTS where it used to compose. Three suites caught it the moment the default moved. Users who rely
    on that must attach `isolation_level 'write_serializable'` (one option, old behaviour).
  - **The ATTACH option is now the FALLBACK EVERYWHERE — it was not.** "Table property wins, catalog default
    applies only when the table is silent" held in the buffered path (`PendingSerializable`) but NOT in the
    autocommit rowid DELETE, which read the catalog flag directly. So `delta.isolationLevel = Serializable`
    + ATTACH `write_serializable` behaved INCONSISTENTLY on ONE table: strict inside BEGIN..COMMIT,
    row-level-relaxed for a bare DELETE. Both now route through one `EffectiveSerializable`. The old defence
    ("a single autocommit statement has no cross-statement reads to serialize, so it is only a resilience
    knob") is true about the SEMANTICS and beside the point about the CONTRACT — a table that has DECLARED
    Serializable must not be weakened by a local option.
    - **NOT TEST-COVERED, which is why it survived:** `rowLevelRetry` only bites when that statement's own
      commit races, and sqllogictest runs connections SEQUENTIALLY — a bare autocommit DELETE has no window
      between its scan and its commit. Every row-level scenario drives the BUFFERED path instead. Exercising
      it needs separate processes (`scratchpad/iso_race.sh`); the suite carries a note saying so rather than
      pretending coverage.
    - En route: `ExecuteDelete` now reads the table config ONCE and derives both `enableDeletionVectors` and
      the isolation level (each helper opens the table separately, so adding the isolation read naively would
      have cost a SECOND `_delta_log` LIST per DELETE on OneLake/S3).
  - **The automatic create-time stamp is GONE (not inverted — removed).** A CREATE used to bake the
    catalog's ATTACH level into the table. That conflates a per-catalog BEHAVIOUR knob with a durable
    per-table DECLARATION, and since the property WINS over any catalog, the stamp made an attach-time
    choice permanent AND silently overrode a DIFFERENT catalog's explicit setting on the same table later —
    measured: with the stamp in place, attaching one path twice at two levels stopped honoring the second,
    which is exactly the composition our level-contrast suites rely on. Declaring a level is now explicit
    and per-table (`WITH ("delta.isolationLevel"=…)` or `fabricator_delta_set_tblproperties`), and that is
    the spelling to use when Spark must honor the looser level (it HONORS a stamped WriteSerializable even
    though its DDL refuses to set it). `CreateConfig`'s `serializable` parameter is now inert — removing it
    is a mechanical ~6-signature cleanup left for later, deliberately not mixed into a behaviour change.
- **PLAIN (non-OneLake) ADLS Gen2 SUPPORT — BUILT + LIVE-VALIDATED 2026-08-02. Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §8.4; gate `test/verify_delta_catalog_adls.test`
  (**55**, manual/live-account tier — ⚠ this line read **140** until 2026-08-07, a number transcribed from
  the `verify_mssql_adls_polybase` gate beside it. The suite has said 55 since the commit that added it
  (`33eb3e1`) and has never changed. Caught by RUNNING it and disbelieving the shortfall; a wrong gate
  number is worse than none, because the next person reads a green 55 as a suite that aborted).** A Delta catalog on `abfss://<fs>@<account>.dfs.core.windows.net/…` —
  a plain storage account, not a Fabric lakehouse. It LOOKED like it already worked (attach, discovery,
  CTAS, INSERT, DELETE, DROP and both parquet directions through duckdb-azure all passed first try); two
  things did not.
  - **The core insight: TRANSPORT and CATALOG had been conflated in one predicate.** `IsOneLake` was
    answering both "how do we do IO here" and "is there a Fabric catalog to ask". Split into
    **`AdlsPath.IsAdlsGen2`** (the ADLS Gen2 DFS transport — selects the filesystem, the directory ops and
    the commit primitive) and **`FabricLakehouse.IsOneLake`** (a Fabric lakehouse — keeps Unity Catalog
    discovery, the schema-enabled flag, the `fabric.*` functions). **Every OneLake root is an ADLS root;
    the converse is false.** The direct-SDK filesystem was NEVER OneLake-specific — it always parsed its
    endpoint host out of the `abfss://` path — so only the gate said otherwise; renamed
    `OneLakeDataLakeFileSystem` → `AdlsGen2TableFileSystem` so the name stops claiming a restriction the
    code does not have. **OneLake behaviour is unchanged** (re-validated live: 21 tables via UC REST, and a
    full CTAS/INSERT/DELETE/DROP round trip).
  - **⚠ A CAPABILITY PROBE CAN RULE A BACKEND OUT; IT CANNOT RULE ONE IN.** `fabricator_fs_write_probe`
    reports duckdb-azure's `EXCLUSIVE_CREATE` as WORKING on abfss (it really does throw on an existing
    file) — and it is a **client-side existence check**, so it races. Measured, 6 writers × 8 commits:
    unguarded **41 of 48 landed, six of the seven losses silent**; with the secret NAMED **48/48** with
    commit versions fully interleaved across writers (so contention was real). Note this is the OPPOSITE
    detectability from the S3 case (§8.3), where the probe fails and no concurrency is needed to see it.
  - **RENAME TABLE was impossible** (`AzureDfsStorageFileSystem: MoveFile is not implemented!`) — which
    breaks a dbt table model on EVERY re-deploy, since its swap is two renames. One mechanism fixes this
    and the commit race together: a credentialed abfss root now takes the DFS-native ops OneLake always
    took (`UseAdlsDirectoryOps`). Mutation-tested — reverting the gate to `IsOneLake` kills the suite at
    exactly that line with the original error.
  - **New: `AdlsCredential` (Entra token OR shared key).** Everything ADLS-facing had assumed a
    `TokenCredential`; a plain account commonly ships as an account key or a storage connection string.
    **⚠ State the asymmetry the right way round: a plain ADLS account accepts BOTH** (Entra via RBAC is
    fully supported there and is the better practice) — **OneLake is Entra-ONLY.** So the shape follows the
    SECRET, not the kind of account, with an `entraOnly` guard so a secret carrying a `connection_string`
    cannot silently downgrade a Fabric attach to key auth OneLake would reject, and an explicitly
    configured service principal outranking key material for the same reason in reverse.
  - **Naming the secret is load-bearing, exactly as on S3** — the credential reaches us only via the marker
    `BuildConnectionString` appends, which runs only when the ATTACH NAMES a secret; an azure secret merely
    in scope still authenticates duckdb-azure's DATA IO, so the unsafe shape reads, writes and passes every
    single-writer test. The S3 attach warning was generalized to cover it (`WarnIfUnguardedRemoteWrite`).
  - Discovery for such a root walks DFS DIRECTORIES (`AdlsTableDiscovery`) — there is no Unity Catalog for
    a storage account. The host glob also works here (unlike OneLake, where duckdb-azure's mid-path
    wildcard is broken), so this is O(tables) vs O(commit files), not a correctness fix — and the suite
    says so rather than implying it pins the mechanism.
  - **No new URI scheme, and `onelake://` is untouched**: duckdb-azure handles `abfss://` parquet READ and
    WRITE (both measured), so native_read/native_write need no VFS of ours. `onelake://` stays Fabric-only.
  - **`COPY … TO 'abfss://…' (FORMAT delta)` routes through our filesystem too — and the first pass got this
    WRONG and wrote the mistake up as a trade-off.** It shipped on the host-FS path justified as "no `SECRET`
    clause, one statement, one commit". But *"has no SECRET clause"* described the PLUMBING, not a
    constraint: with `FORMAT delta` we build the catalog ourselves and know the target is abfss, so we can
    resolve a credential exactly as the `onelake://` FS already does. **A limitation that is really an
    unimplemented case must not be documented as a design decision** — that is how a gap becomes permanent.
    Fixed by `BuildConnectionStringFromScopedSecret` (C++): a SCOPE match, not a name (a DuckDB secret's
    scope IS a path prefix, and azure secrets cover `abfss://` by default, so the common case needs no user
    action), with **no "any secret of this type" fallback** — guessing among accounts is how a write lands
    somewhere unintended. Note this ALSO fixes it for OneLake, where a COPY had the same gap.
    - ⚠ **Deliberately NOT applied to ATTACH, because trying it surfaced a hazard**: in
      `fabricator_storage.cpp` the `provider` may be EMPTY (no `PROVIDER` option — inferred later from the
      scheme), and an empty provider resolves to the DEFAULT backend, whose azure branch merges the fields
      into a **SQL Server** connstr — mangling the abfss path and breaking an attach that works today. COPY
      is safe only because its provider is hardcoded `"delta"`.
    - ⚠ **The filesystem choice is INVISIBLE from SQL**, so a `Fabricator.Delta.Fs` Debug line now names it
      per table open. That log + a negative control is what actually verified the routing (secret in scope ⇒
      `AdlsGen2TableFileSystem`; no secret ⇒ `DuckDbTableFileSystem`); the suite's COPY section can only
      assert the round trip and says so rather than implying it pins the route.
- **ISOLATION + ONELAKE MULTI-WRITER — MEASURED LIVE 2026-07-31; one bug FIXED, one gap OPEN. Full record:
  [docs/delta-transactions.md](docs/delta-transactions.md) §8.1 (multi-writer) + §10.6 (Spark isolation).**
  Two long-standing claims in this file were wrong, and both were beliefs never measured.
  - **`write_serializable` is DATABRICKS' default, NOT Spark's** — every "Spark's default too" here was FALSE.
    Fabric Spark 4.1.1 records **`Serializable`** for its own commits AND its DDL validator **REJECTS**
    `delta.isolationLevel='WriteSerializable'` outright (`requirement failed: … must be Serializable`) at CREATE
    *and* ALTER; `SnapshotIsolation` likewise; only `Serializable` is accepted. Controls both fired, and the two
    negative controls fail DIFFERENTLY (`'Bogus'` doesn't parse at all) — so OSS Delta knows the enum and it is
    the *table-property validator* that admits one value. **Consequence: on a shared table with the property
    ABSENT we apply WriteSerializable while Fabric Spark applies Serializable — we are the more permissive.**
    ATTACH `isolation_level 'serializable'` to match Fabric Spark. A `WriteSerializable` value WE stamp is
    **honored** by Spark (it read, INSERTed, DELETEd, and recorded `WriteSerializable` for its own commits) — it
    just can't SET it, so such a table's isolation is only manageable via `fabricator_delta_set_tblproperties`.
    We deliberately do NOT block the stamp. Corrected in README + `DeltaCatalog`/`DeltaTxnBuffer`/
    `DeltaGlobalTableFunction` comments + `verify_delta_tblproperties`.
  - **OneLake multi-writer was "safe" by INFERENCE only** (its §8 row carried no numbers while local/S3 did).
    Now measured: **no lost writes ever** (versions always unique+contiguous, all groups complete), but
    **low contention never exercises the guard** — 32 commits over 4 processes produced ZERO conflicts, so a
    green low-contention run proves nothing about put-if-absent. Forcing contention (8 writers × 12 tiny
    commits) reproducibly broke writers.
  - **BUG FIXED (EW `CheckpointReader`, on `fabricator-patches`): `_last_checkpoint` is an advisory HINT and was
    treated as authoritative.** It is updated by NON-ATOMIC overwrite, so a concurrent reader can see it at
    **zero bytes** → `JsonDocument.Parse` → *"The input does not contain any JSON tokens"* → a **failed COMMIT
    caused by a file that carries no truth**. Now empty/invalid/field-less ⇒ treated as absent (fall back to
    listing the log, which is what the Delta protocol requires). Gate `verify_delta_last_checkpoint` (34,
    hermetic, MUTATION-TESTED); the live 8×12 shape went from 1–2 failures per run to **96/96 clean**.
  - **SECOND BUG ROOT-CAUSED + FIXED — and it is the SAME root object as the first.** A raw Azure **412
    `ConditionNotMet`** escaped `complete_bulk` (never became a `DeltaConflictException` ⇒ no retry ⇒ the
    statement failed). Mechanism: `OneLakeDataLakeFileSystem.ReadAllBytesAsync` used `OpenReadAsync`, i.e.
    Azure's **lazy `LazyLoadingReadOnlyStream`**, which fetches a blob in successive RANGE requests and
    **pins the ETag, sending `If-Match` on the later ones** — so a `_last_checkpoint` overwritten in place
    mid-read TEARS. Both multi-writer failures are therefore one root cause (that file being overwritten
    non-atomically) by two mechanisms: *empty content* (the parse guards) and a *torn ranged read* (this);
    the parse guards could never catch the 412, which is thrown by the READ, before parsing. Fixed in two
    layers: `ReadAllBytesAsync` now does ONE unconditional `ReadContentAsync` (a single request cannot tear,
    and `ITableFileSystem` documents the method as being for SMALL files), plus `ReadLastCheckpointAsync`
    treats **any** read failure as "no hint" (cancellation excepted).
    - **A WRONG hypothesis is recorded on purpose.** The obvious suspect was `CreateAsync` catching only 409
      while `RenameAsync` catches 409|412. `scratchpad/adlsprobe` **falsified it deterministically** (no race
      needed): on live OneLake a conditional CREATE and a conditional RENAME onto an existing path both raise
      **409 `PathAlreadyExists`**, never 412 — so 409-only was already correct there. That falsification is
      what redirected the search to "something is sending an ETag precondition".
    - **⚠ THE TRAP: a client library can add a conditional header you never wrote.** Our source contains no
      `IfMatch` on any read path, so grepping for it "proved" the wrong thing — `OpenRead` inserts it
      internally. Only a stack trace showed this, which is why the log sink now appends the inner-exception
      chain + full **stack trace at `Debug`** (it used to log type + message only: *what* failed, never
      *where*). With that in place the failure reproduced on the FIRST attempt and named its own site.
      Harness: `ATTEMPTS=N bash scratchpad/hunt412.sh`. Verified after the fix: the same 10×15 shape ran
      **150/150 commits with zero 412s**.
    - **UNEXPLAINED, unrelated, and seen repeatedly — do not mistake it for a lost commit:** in several runs a
      single `duckdb.exe` finished ALL its work (last commit logged, every version landed) and then **did not
      exit**, blocking the harness's `wait`. Observed both before and after these fixes and on runs with no
      errors, so it is a teardown issue on the OneLake+hosted-CLR path. Not investigated.
  - **Diagnostic gap closed en route:** the txn-buffer flush's OCC retry was a SILENT `catch`, so multi-writer
    behaviour was unobservable — a run whose writers merely serialized looked exactly like one where the guard
    rejected and retried. It now logs `delta flush …: commit conflict — reopening at latest (attempt n/16)`.
  - **Method notes worth reusing:** at `Warning` level a conflict-free run leaves an EMPTY log, which is
    indistinguishable from a broken sink ⇒ log at `Information` so the per-commit lines are a POSITIVE CONTROL;
    and `rm *.log` does NOT match `*.fablog`, which silently mixed a previous run's counts into a later one.
- **THE DELTA NATIVE DEFAULTS FLIP — DONE (2026-07-29, behaviour-breaking for `PROVIDER 'delta'`).**
  `native_read`/`native_write` used to default **off** everywhere, so the production path was opt-in and
  the *tested* path was the pure-EW codec. Now **the provider NAME selects a default profile**
  (`DeltaBackend.NativeDefaultsFor`): **`PROVIDER 'delta'` ⇒ both ON** (DuckDB reads/writes the parquet
  bytes, EW owns the log — the hybrid we actually ship), **`PROVIDER 'engineeredwooddelta'` ⇒ both OFF**
  (pure codec). Explicit options still win on either spelling. The name reaches the backend through a
  3-arg `IBackend.OpenCatalog` **default-implementation interface overload**, so SqlServer/DAX/deltars are
  untouched — no ABI bump. The redundant alias `deltalake` was REMOVED in the same pass.
  - **The flip found two real bugs that were unreachable while native was opt-in** — the whole point of
    making the shipped path the default one: (1) `FIELD_IDS` described the whole table schema instead of
    the STREAM, so a write whose stream omitted columns was REFUSED (`00f0475`); (2) a buffered append
    **committed inside an EXPLICIT transaction** when the table had an IDENTITY column, so `ROLLBACK` left
    the rows behind (`b9ed65e`). Bug 2's code comment defended the shortcut with an argument about append
    *commutativity* — which says nothing about *atomicity*. Treat a comment justifying a shortcut on
    concurrency grounds as unexamined for rollback.
  - **The engines are NOT at parity and cannot be:** clustered/Z-order OPTIMIZE needs `native_write`
    because the recluster's global ORDER BY uses DuckDB's **spilling** sort, and EW has no external sort.
    A clustering-declared table on a codec catalog therefore **WARNs** rather than silently bin-packing
    (`3a1c898`, `verify_delta_clustered_optimize` §8).
  - **Suite strategy: split by intent, plus a doubled leg.** Each suite is pinned to the engine it is
    *about* (`verify_delta_catalog_variant`, `_row_tracking_virtual`, `_autocommit_pin` → codec, since the
    codec IS their subject); the core four (write/transactions/update/delete) run **twice**, once per
    engine, via `${DELTA_PROVIDER}` interpolation driven by `run-suites.sh`. `verify_delta_rename` pins
    that each spelling selects its documented engine, observed through the data files' own
    `parquet_file_metadata(...).created_by` (`DuckDB version …` vs `EngineeredWood`) — a change to
    `NativeDefaultsFor` or the alias table then fails loudly and names the consequence.
  - **The flip put ~18 delta suites on DuckDB's parquet reader/writer, which they never declared.** They
    passed locally only because this box has `~/.duckdb/extensions/…/parquet.duckdb_extension`; on a bare
    runner they fail with *Copy Function with name "parquet" is not in the catalog*. `require parquet`
    added to all 18 — same class as the tier-2 `verify_mssql_s3_polybase` finding, and again only the
    empty-`USERPROFILE`/`HOME` trick shows it.
- **DELTA SNAPSHOT CACHING (perf) — PREREQUISITES DONE, the cache itself NOT BUILT and the full version
  NOT RECOMMENDED. Full design + every finding: [docs/delta-snapshot-caching.md](docs/delta-snapshot-caching.md).**
  Every table REFERENCE costs **4 snapshot constructions per statement**, dead linear in references
  (self-join 8, three references 12), each a `_delta_log` LIST that `ExternalFileCache` does not serve — so
  OneLake/S3 pay most. Shipped so far: `SnapshotPinning.Release` wired to commit/rollback (it was DEAD CODE,
  and the 4096-entry panic `Clear()` it left as the only reclamation was silently breaking snapshot isolation
  for in-flight transactions), and the host-FS opener now resolved PER CALL (`142b350`) — a cached table
  holding a stale `ClientContext*` is a use-after-free, not staleness, and would not fault on this box.
  - **The headline number is a COUNT, not a profile** — nobody has measured what the redundant opens cost in
    wall-clock. The Fabric notebook's 305 s → 15 s came from two OTHER fixes, dominated by `HostFsGlob`'s
    open-per-matched-file (258 s → 2 s). Do not call this "the biggest remaining perf item" again without
    profiling it; that inference is what the doc's decision gate exists to stop.
  - **If anything is built, cache the immutable `Snapshot` — NOT a `DeltaTable`, NOT a `NativeScanList`.**
    `DeltaTable.OpenAsync` is *entirely* "LIST the log, replay it into a `Snapshot`, wrap it in a cheap
    holder", and `Snapshot` is init-only over `IReadOnlyDictionary`. So caching it per (txn, path, version)
    captures ALL the redundant cost while every call still builds its own table — which dissolves disposal,
    the dangling opener AND the thread-safety dependency on an unenforced EW invariant. Serves BOTH engines.
    Needs one small additive EW patch (`FromSnapshot`, since the snapshot-taking ctor is private). Caching a
    live `DeltaTable` buys nothing over this and costs a lease threaded through 6 async iterators. Caching a
    `NativeScanList` is WRONG: it is post-prune, so sharing it between scans with different pushed predicates
    silently DROPS ROWS.
  - **⚠ Two traps recorded in the doc:** there is NO intra-call shortcut (the `Stream*` methods are async
    ITERATORS, so the schema open completes and disposes BEFORE the stream open begins, in a different ABI
    call), and `TableFunction::function_info` is the WRONG shelf for cached state (its lifetime is the PLAN,
    and plans are re-executed across transactions) — use `ClientContextState`/`registered_state`, or the
    existing per-txn `SnapshotPinning` structure whose `Release` is already the disposal point.
- **SINGLE-FILE DISTRIBUTION — BUILT + validated live (phases 1–4 of 5; REMAINING: user-facing install
  docs + CI matrix — CI tier 3 exists).** ONE `fabricator.duckdb_extension` self-installs (extract +
  chain-load + CLR boot; ~2–3 s cold, 0.01–0.2 s warm; win 61 MB standalone / linux 40 MB standard —
  the Fabric-notebook SKU). Build: `scripts/pack-distribution.ps1`; smoke:
  `test/distribution/smoke_distribution.py` (12 checks). Design + findings:
  [docs/distribution-installer.md](docs/distribution-installer.md) §12/§14/§15/§16. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **NativeAOT BRIDGE SKU (design only, 2026-07-25 — nothing built):
  [docs/aot-bridge.md](docs/aot-bridge.md).** An optional AOT-compiled variant of the
  managed layer (Bridge + providers → ONE native lib, `NativeLib=Shared`) beside the CoreCLR
  SKU — zero .NET prerequisite, est. 40–80 MB total. **Key audit finding: the ABI is already
  AOT-shaped** (vtable of `[UnmanagedCallersOnly]` statics both directions) — only the
  bootstrap changes: a `FabricatorBridgeInit` native export + clr_host mode 3 (managed dir
  contains `Fabricator.Bridge.Native.<ext>` ⇒ plain dlopen/dlsym, no hostfxr). The complete
  dynamic-code inventory is FIVE sites: BackendRegistry reflection discovery → a
  **`Fabricator.Generators` Roslyn source generator** (`[FabricatorBackend]` attr → emitted
  `CompiledBackends` factory in a HEAD project `Fabricator.Bridge.Native.csproj`, trim-rooted
  by construction; reflection branch behind an AppContext feature switch ILC trims away);
  the plugin ALC → **compile-time plugins** (reference + republish the head — the head IS the
  plugin config; native-plugin C-ABI + a DAX CoreCLR sidecar noted as deferred);
  `FormatError`'s `GetProperty("Number")` duck-type → `IBackend.GetErrorNumber(Exception)`
  DIM; ~10 `JsonSerializer<T>` files (+ EW `ActionSerializer`) → one source-gen
  `JsonSerializerContext`; Regex is AOT-fine as-is. **DAX/ADOMD stays CoreCLR-only** (the
  original non-AOT reason — closed-source, not AOT-able); AOT SKU = SqlServer + Delta/EW
  (**SqlClient AOT feasibility USER-VALIDATED 2026-07-25 on the 7.1 preview — the AOT SKU
  targets 7.1+**, remaining work is the version bump + our-paths coverage via the suite;
  Fluid must run interpreted). Gate = the existing SQL-level verify sweep (minus verify_dax)
  against the native bridge. Endgame option noted: `NativeLib=Static` linked INTO the C++
  loadable = literally one file, no trampoline (experimental, later). Composes with the
  distribution installer (payload → core + one native lib, no .NET probing).
- **`CREATE TABLE … WITH (…)` options + SQL Server EXTERNAL TABLES — ALL FOUR SLICES DONE (ABI v67).**
  WITH write-tuning/CREATE-flag-overrides/TBLPROPERTIES on Delta; external-table INSERT/identity-keyed
  UPDATE+DELETE routing to storage — **`s3://` AND (since 2026-08-02) `adls://`**, see the ADLS Gen2
  data-virtualization entry for why that took two gates and not one; the CETAS-analog
  `WITH (location=…, table_type=…)` DDL.
  [docs/create-table-with-options.md](docs/create-table-with-options.md); gates verify_with_options 68 +
  verify_mssql_s3_polybase 252 + verify_mssql_adls_polybase 140. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **SQL-GENERATING TABLE FUNCTIONS — DONE (ABI v68 `generate_table_sql`, global + catalog-bound).** The
  call DISAPPEARS at bind (`bind_replace` → SubqueryRef); arg-dependent schema + full pushdown for free.
  Rule: fixed SQL text + varying VALUES ⇒ macro; SQL TEXT depends on args ⇒ sqlgen.
  [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §2; verify_sqlgen 59 +
  verify_sqlgen_catalog 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **PROVIDER-DECLARED DuckDB MACROS — DONE (no ABI bump; decl kind `macro` + body column).** DuckDB
  parses the full CREATE MACRO grammar; registered into the SYSTEM catalog at load; injection-free by
  construction. [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1;
  verify_macros 41 + verify_plugin 10. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
  - **CATALOG-BOUND (attach-time) macros — DONE (2026-07-30, no ABI bump; new metadata kind 15).** Resolve
    as `db.schema.m(…)`; the old "§2 covers it" dismissal was **half wrong** and that half is what got
    built. Works by the pattern we already ship: a macro entry returned from `LookupEntry` is expanded
    normally, because DuckDB looks up `SCALAR_FUNCTION_ENTRY`/`TABLE_FUNCTION_ENTRY` and then dispatches on
    the entry's ACTUAL type — the same one-namespace fact that forces our scalar lookup to surface custom
    aggregates. **A schema gives NAMESPACING, not resolution scope**: expansion captures no search path, so
    an unqualified table reference in the body resolves in the CALLER's context (silent wrong table, not an
    error) — so sqlgen (§2) really is the answer for a table macro naming its own catalog, but sqlgen is
    TABLE-valued only, so it is NO answer for a per-catalog **scalar** helper, and the 4e custom scalar is
    marshaled where a macro crosses nothing. Gate `verify_macros_catalog` 50 (hermetic); full record in
    [docs/macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) §1.4.
    **Three traps worth carrying forward:** (1) the body rides its **own metadata kind**, NOT a column on
    the FUNCTIONS stream — that stream is built as **T-SQL executed on the server**, so a column there would
    have shipped a local declaration to SQL Server and back and made declaring a macro depend on server
    reachability (and offered nothing to the SQL-less Delta catalog); reading the producer is what caught
    it. (2) `GetOrCreateMacro` MUST filter by wanted kind: the binder `Cast<>`s on the entry type without
    checking, so handing a scalar lookup a table macro is an unchecked bad cast. (3) macros must be emitted
    by the **SCALAR/TABLE_FUNCTION** `Scan`s, since those are the only types `duckdb_functions()` asks for
    (it switches on the actual type itself). Also fixed en route: a latent OOB read in `ReadStringTable`
    (asks for N columns, and a provider answering an unimplemented kind returns its 1-column `_ =>`
    fallback — a Delta catalog does exactly that for FUNCTIONS, which asks for 3). The check is per BATCH
    and only when `length > 0`; validating the SCHEMA's width instead **broke every Delta ATTACH**, so that
    leniency is load-bearing, not merely tolerated.
- **`hilbert_index` + `bucket` global scalars, declared scalar VOLATILITY, and the FULL LIQUID-CLUSTERING
  stack — ALL DONE, Spark-interop validated live BOTH directions** (writes to clustered tables; SORTED BY
  declares clustering; clustered OPTIMIZE incl. multi-file + partitioned partial recluster + ZCube
  incremental — Spark recognized OUR cubes as its own; `ALTER … SET/RESET SORTED BY` (alter kinds 12/13);
  `SORTED_COLUMNS` COPY option). Gate verify_delta_clustered_optimize 138 + hilbert 27 + bucket 34 +
  sorted_by 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **`SORTED BY` → Delta ORDERED writes — DONE.** Persists as the `fabricator.sortedBy` table property;
  INSERTs re-apply the ORDER BY via a host-side spilling sort. verify_delta_sorted_by 30. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **dbt DAX→Delta pipeline — DONE + validated live** (`dbt_dax_test/`, gitignored; plain-DAX model bodies
  via the custom `dax_table` materialization). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **THE BATCHED NATIVE DELTA READ — DONE 2026-08-06 (C#-only, no ABI). One `read_parquet([f1, f2, …],
  schema = map {…})` replaces the per-file host query for the files it can cover** (`DeltaNativeReader.BatchPlan`);
  everything else keeps the existing loop, file by file. Threshold `FABRICATOR_DELTA_BATCH_MIN_FILES`, default 2,
  `0` disables. Gates: hermetic **67/67 — 6513** (the pre-change tier was 66/6403 and every shared suite kept its
  exact assertion count ⇒ behaviour-preserving), new suite `verify_delta_batched_read` **110**, and
  service **45/45 — 1583** — which matters beyond regression coverage: `verify_delta_catalog_s3`
  (177 × 2 engine legs) is the only leg that puts the batched `read_parquet([…])` on **`s3://`** URIs
  rather than local paths.
  - **MEASURED, both legs run through our own scan with the env var flipped** (the only honest A/B, see below):
    **200 files × 100 rows 0.464 s → 0.090 s (5.2x)**; 200 files × 20k rows 0.794 → 0.493; 50 files × 20k rows
    0.211 → 0.123. That is **~1.5–1.9 ms of overhead removed per file**, consistent across all three, so the
    RELATIVE win tracks how FRAGMENTED the table is rather than how big — i.e. it lands on the dbt-incremental
    shape (every run appends a file) that motivated it.
  - **⚠ A 13x FIGURE THIS FILE'S PREDECESSOR NOTE CARRIED IS WITHDRAWN — IT WAS CONFOUNDED, and the confound is
    architectural rather than a slip.** It put our scan (412 ms) against DuckDB reading the same files in ONE
    plan (31 ms). That plan AGGREGATES IN PLACE; our scan must hand every row back across the Arrow boundary for
    DuckDB to aggregate above it. So 31 ms was a FLOOR no batching can reach, not an alternative — and the
    residual after batching is exactly that hand-back, which is inherent to the native-read design. **Lesson in
    one line: a comparison against a plan that does not carry your data is not a comparison.**
    - ⚠ **The replacement measurement nearly repeated the mistake in a new disguise.** The first batched-vs-plain
      timing said `schema` cost 10x (21 ms vs 2 ms) — because the probe's `count(s)` was answered from parquet
      NULL COUNTS without decoding the column at all. With a real decode forced (`sum(length(s))`), `schema`
      costs **nothing measurable**: 18–27 ms with the map vs 21–33 ms hand-aliased. **A parquet aggregate that
      the footer can answer is not a read.**
    - ⚠ **And an attribution I nearly wrote up was wrong too**: `SELECT … LIMIT 0` costs 19 ms where the same
      scan costs 274 ms, which I read as "the Delta log replay is cheap, so the residual is elsewhere". `LIMIT 0`
      never executes the scan, so it measures nothing about it. The snapshot-construction cost is still
      unmeasured here — do NOT cite this work as evidence either way for
      [delta-snapshot-caching](docs/delta-snapshot-caching.md).
  - **THE `schema` PARAMETER'S SEMANTICS, pinned by experiment because the docs are thin and several plausible
    readings are wrong.** `schema = map { <key>: {'name': …, 'type': …, 'default_value': …} }`: the **MAP KEY is
    the identifier** (VARCHAR ⇒ match by name, INTEGER ⇒ `BY_FIELD_ID`), **`'name'` is the OUTPUT name** — so it
    performs the physical→logical rename for us — `'type'` casts per file, a column ABSENT from a file arrives as
    `default_value` (**that is the schema-evolution backfill, and it is what makes the per-file footer probe
    unnecessary** — the probe was ~1.6 ms of the per-file cost), and a column present in the file but absent from
    the map is IGNORED (the post-`DROP COLUMN` read). A non-NULL default really lands, not just NULL.
  - **⚠ FOUR MEASURED LIMITS, and they are what shape the gates. Two fail LOUDLY and two SILENTLY.**
    1. **`filename` / `file_row_number` compose with an INTEGER-keyed map and FAIL with a VARCHAR-keyed one** —
       `Invalid Input Error: … column "2147483645" … could not be found`, i.e. the virtual column's sentinel id
       resolved by NAME. Since every shape needing a row position (the transient rowid, a deletion vector, a
       derived row-tracking id) needs `file_row_number`, **that single incompatibility is why the batch covers
       plain scans only.**
    2. **An INTEGER-keyed map over a file with NO parquet field ids is `INTERNAL Error: No default expression in
       FieldId Map`** (a DuckDB assertion, so also a candidate to report upstream). Name mode does not require a
       writer to stamp field ids, so field-id keys are not a free substitute for case 1.
    3. **`schema` is REFUSED together with `hive_partitioning`** (`Binder Error`), so partition literals cannot
       ride along — they need a `filename` join, which needs case 1's field-id keys.
    4. **⚠ STRUCT INTERIORS ARE MATCHED BY NAME AND THEN CAST — the silent one.** Children `(a, b)` where one
       file renamed only `b`: the batch returned **`{'a': 20, 'b': NULL}`**, the value DROPPED, exit 0. FULLY
       disjoint children DO error (*STRUCT to STRUCT cast must have at least one matching member*), so **partial
       overlap is the dangerous shape and partial overlap is exactly what one rename produces.**
  - **⚠ ITEM 3 OF THE STANDING LIST IS NOW MEASURED, AND IT IS A DIFFERENT HAZARD FROM CASE 4 ABOVE — worse.**
    A `UNION ALL` route (which `FullTableSql` uses for the clustered-OPTIMIZE rewrite) merges struct interiors BY
    NAME and NULL-fills what a branch lacks, so the same two files yield ONE struct carrying **both** names with
    half the values NULL: `{'a':…,'b':…,'col-b':NULL}` / `{'a':…,'b':NULL,'col-b':…}`. The output TYPE is wrong
    too, not just the values. So the two routes are unsafe in different ways and neither can be fixed by the
    per-batch `ArrowColumnMappingRename`, which runs after the SQL. `FullTableSql`'s doc now records this at the
    gate that protects it.
  - **THE GATES, and one of them must NOT be described as tested.** Batched: plain scans, incl. the zero-column
    `COUNT(*)` shape (its own branch, no map at all — nothing is read, so mapping/evolution/types cannot matter).
    Per-file: the transient rowid / DML, any file carrying a **deletion vector** (decided PER FILE, so a
    merge-heavy table still collapses its clean files and the DV file keeps its prunable position bound),
    partition columns, the row-tracking virtual columns, a rowid/tracking fast-path filter, variant, and
    `column_mapping 'id'`.
    - **⚠ THE ID-MODE GATE IS A *CONTRACT* GATE AND NO TEST CAN KILL IT — a mutant with it removed passes the
      whole suite, and I nearly wrote it up as mutation-tested.** The batch resolves by NAME while id mode's
      contract is that a reader matches by FIELD ID and the stored name is not authoritative (a legacy
      engineered-wood id file stores LOGICAL names under its field ids; an external writer may do either) — so a
      name-keyed map meeting such a file silently yields an ALL-NULL column, or case 4's dropped members.
      **MEASURED why no test reproduces it: an id-mode table taken through a nested RENAME *and* a top-level
      RENAME has all four of its files storing BYTE-IDENTICAL physical names** — in id mode too, `physicalName` is
      assigned once at column creation. Keep the gate because name-matching a table whose contract is id-matching
      is unsound for files WE DID NOT WRITE, and say exactly that rather than implying a bug was reproduced.
    - **⚠ AND THE DEFAULT COLUMN-MAPPING MODE IS `name`, NOT `id`** — measured off the `metaData` of a plain
      `PROVIDER 'delta'` create. `verify_delta_catalog_column_mapping`'s own header says *"DEFAULT = 'id'"* and is
      STALE. It matters here and not academically: were `id` the default, the gate above would disable batching
      for nearly every table and this feature would be inert out of the box.
  - **WHAT RETIRES THE ID-MODE GATE, concretely: duckdb/duckdb #24407** — *"extend the `schema` option to support
    NESTED schema definitions"* (Tishj), **OPEN against `main`** as of 2026-08-06. Declaring a struct's children
    with their own identifiers is precisely what lets one declared type describe files of two vintages. ⚠ It
    targets `main`, so it lands on the FUTURE line, not `v1.5-variegata` — the gate stands here regardless.
  - **⚠ ONE REAL CODE REQUIREMENT WAS FOUND BY RUNNING THE SUITES, NOT BY READING.** `schema` renames the TOP
    level only, so a mapped struct's interior arrives PHYSICAL — and a pushed struct-member predicate then fails
    to bind (`Binder Error: Could not find key "b" in struct`, caught by `verify_delta_catalog_nested_alter`,
    which is also the reason the per-file path has `RebuildExpr`). Fixed by `LogicalStructExpr`: ONE `struct_pack`
    rebuild serves every file, because name mode's physical names are file-independent. Note the two suites that
    caught it reported PARTIAL assertion counts while broken (26 and 156, vs 100 and 251 when passing) — an
    aborted suite's count is not a coverage number.
  - **Mutation-tested, each mutant killed at its own section**: removing the struct rebuild dies at the
    struct-member predicate (§3, the binder error above); removing the DV split dies at §4 with the exact
    resurrection (**300 rows where 290 survive** — the 10 deleted rows back). The id-mode mutant SURVIVES, per
    the note above.
  - **⚠ The suite is run at `FABRICATOR_DELTA_BATCH_MIN_FILES=1` by `run-suites.sh`, and the reason is the MIRROR
    IMAGE of the UPDATE-grouping case.** Here the shipped default (2) IS exercised by every other delta suite, so
    what would go untested is the batched path on a ONE-file scan — the shape most suites actually build. The
    `unset` for every other suite is load-bearing in an extra direction too: a stray `0` in a developer's shell
    DISABLES batching everywhere, so a green tier would be testing only the old loop while looking complete.
  - **THE FULL FORM — DONE, and it is what the original ask actually was.** The first pass shipped a NARROW
    version that batched only plain scans and gated off the rowid and the deletion vectors — i.e. it gated off
    the substance ("composes with filename+file_row_number so the DV becomes ONE input keyed (filename,pos)")
    and reported the easy half as the feature. The user caught it: *"you actually implemented nothing I thought
    you were implementing."* **Do not narrow a deliverable and report it as done** — the facts needed for the
    real form were already measured in the same session and I took the small branch anyway.
    - Now: ONE query covers EVERY file, with the deletion vectors of all files bound as a single
      `(filename, pos)` Arrow input anti-joined once, and the per-file global ordinal bound as a second input
      joined on `filename`, so `_metadata.row_id` = `(ord << 40) | file_row_number` is expressible ⇒ **UPDATE /
      DELETE scans batch too.** Both inputs sit in `WITH … AS MATERIALIZED` CTEs. MEASURED on 100 files that ALL
      carry a deletion vector: **0.416 s → 0.145 s (~2.8x)**, identical answers.
    - **⚠ IT DOES NOT USE THE `schema` MAP, AND THAT IS FORCED BY A DuckDB ASSERTION BUG.** Field-id keys are the
      only kind that composes with the virtual columns — but a field-id-keyed map plus a virtual column raises
      **`INTERNAL Error: No default expression in FieldId Map`** whenever the FILE contains a column with no field
      id, and a materialized `__delta_row_id` is exactly that (row-tracking columns are not column-mapped). Row
      tracking is ON by default and every merge-on-read post-image file has that column, so the field-id route is
      unusable on the DEFAULT table shape. The same file reads fine with the map and no virtual columns, and with
      `filename` instead of `file_row_number` ⇒ an upstream assertion. **REPRODUCED on the stock 1.5.5 wheel with
      four controls, and it INVALIDATES THE DATABASE** (the next unrelated query returns *"FATAL Error: … database
      has been invalidated"*), so it is not a containable error — write-up ready to file in
      [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §1. So the full form uses **`union_by_name => true`** plus an explicit
      physical→logical alias projection, which needs no field ids at all.
    - **⚠ `union_by_name` CANNOT PRODUCE A COLUMN NO FILE IN THE LIST CARRIES** — binder error, and PRUNING makes
      it routine (Delta-log pruning dropped the only file holding a newly-ADDed column, so `WHERE extra IS NULL`
      broke). Fixed by ONE `parquet_schema([… whole list …])` query per scan resolving which stored names exist,
      with the rest emitted as `CAST(NULL AS …)`. One query per SCAN, never per file.
    - **PARTITIONED TABLES ARE BATCHED (2026-08-07) — the gate is gone, replaced by an INVARIANT.** They were
      gated on a **confirmed upstream DuckDB bug**: `duckdb_arrow_scan` + a projection that is NOT A PREFIX of
      the bound stream's columns SEGFAULTS (`SELECT a0, a2` over a 3-column input dies; `SELECT a0, a1` is
      fine), or non-deterministically corrupts a string length into the `4294967296` assertion and INVALIDATES
      THE DATABASE. **Reproduced on plain v1.5.5 with no extensions and no fabricator code** —
      `test/repro/duckdb_arrow_scan_nonprefix.c`, ~110 lines of pure C with two passing positive controls,
      ready to file. Full record: [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §2.
      - **THE FIX IS STRUCTURAL, and it is now a standing rule for every bound host-query input: EVERY COLUMN
        MUST BE READ BY THE GENERATED SQL.** `MetaStream` takes `withFileOrdinal` — the per-file ordinal is
        emitted only when the rowid expression reads it — so the consumed set always equals the produced set and
        the bug is unreachable BY CONSTRUCTION. A partitioned scan tripped it because that ordinal is dead
        weight when no rowid is wanted. **Add a bound column only together with the SQL that reads it.**
      - **⚠ `file_ord` IS NOT `WITH ORDINALITY`, and the old names (`withOrdinal` / `ord`) invited exactly that
        reading — renamed 2026-08-07 after they did.** `rowid = (file_ord << 40) | file_row_number`, and the two
        halves have DIFFERENT granularity: `file_ord` is the FILE's index in the scan's list, **one value per
        file**, attached by a JOIN on `filename` (never zipped positionally); `file_row_number` is the row's
        physical position inside its own parquet file, which DuckDB derives from the FOOTER's row-group offsets.
        So neither half depends on emission order — which matters because **DuckDB guarantees no row order**, and
        a `WITH ORDINALITY`-style running counter WOULD be nondeterministic under a parallel multi-row-group
        scan. MEASURED on 2 files × 10 row groups / 40k rows: the id→rowid checksum is identical at threads 1, 8
        and 4, identical between the batched path and the per-file loop, and a threaded UPDATE hit exactly its 9
        predicate rows with 0 mismatches. Gate: `verify_delta_batched_read` §8.
      - **⚠ AND THE ROWID IS *TRANSIENT ACROSS CREATIONS*, which that gate had to be rewritten to respect.** A
        first version pinned a literal checksum and was FLAKY: `file_ord` follows listing order over UUID-named
        data files, so two creations of the same logical table legitimately produce different rowids (measured:
        two distinct checksums over three runs). Stable WITHIN a scan — all DML needs, since the rowid is
        captured and consumed inside one statement — but never pin one across runs.
      - **⚠ WRAPPING THE QUERY DOES NOT WORK — measured, because it is the obvious first guess (and was the
        user's).** A subquery, a plain CTE and even a **MATERIALIZED** CTE all still crash: projection pushdown
        goes straight through every one. That is exactly why the real query still died despite already being
        `WITH … AS MATERIALIZED (SELECT * FROM <view>)`. The only variants that survive are the ones where the
        skipped column stays REFERENCED (`ORDER BY a1` inside, `WHERE a1 IS NOT NULL`) — i.e. the scan is asked
        for the full set. Do not rely on a stray reference either: constant folding or a provably-true predicate
        can drop it again.
      - **⚠ THE GATE IS THE *FILTERED* QUERY, and mutation-testing proves it: an unfiltered partitioned scan
        reads every bound column anyway and passed happily while the bug was live.** With `withOrdinal` forced
        true, `verify_delta_batched_read` survives **89 assertions** and dies at exactly
        `WHERE p = 'p1'`. A partition test without a WHERE tests nothing about this.
      - **⚠ THE ERROR TEXT NAMES THE WRONG SUBSYSTEM.** `2^32` comes from `ColumnDataAllocator`'s CHECKED
        `NumericCast<uint32_t>` on an allocation SIZE, because `SetVectorString`'s `UnsafeNumericCast` is
        UNCHECKED in Release and lets a garbage string length through silently. Read it as "something made a
        string length absurd", never as an allocator or cast bug.
      - **⚠ HOW OWNERSHIP WAS ESTABLISHED, because the obvious control got it BACKWARDS.** A pyarrow
        `RecordBatchReader` on the stock wheel survives the same pruning — which looks like it convicts OUR
        export and does not: **Python's `register` never goes through `duckdb_arrow_scan`**. What settled it was
        **swapping the PRODUCER while holding the CALL SITE fixed**: feeding `duckdb_arrow_scan` a
        DuckDB-produced stream (`ArrowAppender` output) crashes identically to an Apache.Arrow C#-produced one.
        Producer-independent ⇒ the consumer owns it. **A control that changes the mechanism under test is not a
        control** — same shape as the `count(*)`-is-not-a-read trap.
      - **THREE HYPOTHESES DIED FIRST, all from bisecting the QUERY rather than the mechanism**, and the third
        had been recorded HERE AS THE ANSWER: hive-column collision (`hive_partitioning => false` does not fix
        it — kept anyway as a real guard against an `x=y` directory injecting a phantom column); a
        `union_by_name` + virtual-column assertion (does not reproduce on the stock wheel); and **"a bound input
        with a second VARCHAR column breaks"** — REFUTED, `(utf8, int64, utf8)` round-trips perfectly when every
        column is read, and the evidence for it ("with `p0` as `Int64` the query runs") was timing luck.
      - **A SEPARATE REAL BUG OF OURS WAS FOUND ON THE WAY AND FIXED (C++-only, no ABI) — and it is NOT this
        one.** `MakeHostQueryStream` gave `duckdb_arrow_scan` the CALLER's `ArrowArrayStream *` — which DuckDB
        stores as a RAW POINTER in the view — then ran `conn->SendQuery`, a STREAMING result, so the managed
        `finally` released and freed that storage before a row was fetched. The comment above the loop said the
        stream was "consumed + released during the (materializing) query" while the next LINE OF CODE said
        `// streaming (lazy Fetch)`. Fixed by `OwnedArrowInputs`: each input is MOVED (struct copy + zero the
        source, the C-data-interface move) into storage owned by the `HostQueryStream` and declared as its
        FIRST member. ⚠ **The upstream crash is unchanged with it in** — a latent hazard the investigation
        surfaced, hidden the same way (a `WITH … AS MATERIALIZED` CTE drains the input during `SendQuery`'s
        first chunk push, so almost every shape was safe by PLAN ACCIDENT).
      - **`BoundInput.Drop` is therefore CORRECTNESS, not tidiness**: `duckdb_arrow_scan` creates a
        CATALOG-level (non-temporary) view, so it outlives both the connection and the stream owning the
        input's storage. The one lazy-stream site, `DeltaCatalog.SortStream`, now defers its drop to Dispose via
        the new `BoundInput.WrapDrop` — closing the leak this entry previously recorded as owed.
      - **⚠ METHOD, and it is the transferable part: STOP BISECTING THE QUERY, GET A STACK TRACE.** Running the
        statement outside sqllogictest printed `0xC0000005 at Interop+Kernel32.LocalAlloc ← HostFs.Query`, and
        four checkpoint log lines then placed the death inside `host_query`. Three throwaway tools did the rest
        and are worth rebuilding next time: an env-var UN-GATE so the failure could be provoked on demand; a
        temporary global table function `fabricator_probe_input(sql, shape, vals)` binding an arbitrary
        hand-built Arrow batch under a name substituted into arbitrary SQL; and a temporary `__PROBE__<sql>`
        marker on `fabricator_host_query` that NESTS `MakeHostQueryStream` — giving a DuckDB-produced input
        through the identical call site for free. **Build the probe that isolates ONE variable instead of
        re-running the composite.**
      - ⚠ And one control that looked decisive was worthless: "the byte-identical statement with the metadata
        as an inline `VALUES` CTE returns the right rows" — an inline CTE has NO bound input, so it changed the
        very variable under test while appearing to hold everything constant.
    - It gives up the DV's PRUNABLE BOUND (one WHERE cannot carry a per-file range). Deliberate, and the evidence
      is already in `DvRangeCondition`: its own A/B found that bound "demonstrably works and does not show up in
      wall time". ⚠ Re-measure on REMOTE storage with a mostly-deleted file before calling it free there.
    - Still per-file: `column_mapping 'id'`, nested STRUCT columns in the full form (the plain `schema`-map form
      still handles those), and any scan with a rowid/tracking fast-path filter (its whole value is per-file
      row-group pruning, and one call has one WHERE). **Partitioned tables AND the row-tracking virtual columns
      both came OFF this list on 2026-08-07.**
      - **THE ROW-TRACKING LIFT WAS PURE STALENESS — no new mechanism, just a gate nobody re-derived.** Its
        stated reason (*"a materialized `__delta_row_id` is not column-mapped, so it has no field id to key by,
        and a DuckDB `map` cannot mix INTEGER and VARCHAR keys"*) is entirely about the field-id `schema` map
        that this form ABANDONED for `union_by_name`. Under name resolution the materialized column is just
        another column, and `baseRowId`/`defaultRowCommitVersion` are per-FILE constants that ride the metadata
        input like `file_ord` ⇒ `COALESCE(materialized, baseRowId + file_row_number)` in one query.
      - **⚠ THE CASE THAT MATTERS IS A *MIXED* TABLE, and a test on a uniform one proves nothing.** An UPDATE's
        post-image file MATERIALIZES ids while the untouched files derive them, so the scan spans both;
        `union_by_name` NULL-fills the files that lack the column, which is exactly the COALESCE's fallthrough.
        MEASURED: 200 rows / 200 distinct ids / 0 NULLs, the 6 rewritten rows KEEPING their original ids
        (max 185, range still 0–199), with `batched=4` in the log as the positive control that the batched path
        was actually taken. Gate: `verify_delta_batched_read` §6b.
    - **⚠ A `count(*)` CONTROL PRODUCED A FALSE "IT WORKS" FOR THE THIRD TIME IN ONE SESSION.** The field-id +
      virtual-column combination was pronounced fine on a multi-file `count(*)` — which DuckDB answers without
      building the full mapping. Forcing a real decode reproduced the assertion immediately. **A parquet
      aggregate the footer can answer is not a read; never use one as a control here.**
    - **Gates for the full form: hermetic 67/67 — 6513 AND service 45/45 — 1583, both IDENTICAL to the narrow
      version's counts** ⇒ every answer unchanged while the rowid and DV shapes moved onto the batched path. The
      service leg is the load-bearing one here: `verify_delta_catalog_s3` (177 × 2 engine legs) is the only place
      the batched `read_parquet([…])` — now with two bound Arrow inputs and MATERIALIZED CTEs — runs against
      **`s3://`** URIs rather than local paths.
    - New: `SingleScanArrowStream` wraps each bound input so a SECOND scan THROWS. Without it, a re-scan of the
      single-use DV view returns zero rows and silently resurrects deleted rows; `MATERIALIZED` is what makes the
      single scan true today, and this makes a future planner change fail instead of corrupting an answer.
  - **⚠ THE BOUND-INPUT VIEW WAS A GLOBAL, CATALOG-LEVEL NAME — FIXED 2026-08-06, and it was a SHIPPED bug
    reachable from two documented settings.** Found by the user asking whether the DV view could collide when
    joining several Delta tables, or whether it is session-scoped. It is not session-scoped: DuckDB's
    `duckdb_arrow_scan` registers the input with **`CreateView(name, replace: true, temporary: FALSE)`**
    (`duckdb/src/main/capi/arrow-c.cpp` → `Ingest`), i.e. a CATALOG-level view shared by every connection on the
    database, silently replacing any existing one — and it must stay alive until the STREAMING result is fully
    fetched, so two host queries binding one name race over the whole fetch.
    - **MEASURED with `FABRICATOR_DELTA_PREFETCH=8` + `FABRICATOR_DELTA_BATCH_MIN_FILES=0`** (both shipped and
      documented, so each DV file gets its own concurrent query): **every scan of a deletion-vector table failed**
      with *"failed to register input view '__fab_dv'"*. That is the LOUD outcome; the same race can instead let
      one query's view be REPLACED by another's stream, which is silent wrong rows.
    - **It also LEAKED**: because the view is not temporary it outlived its connection and showed up in the
      user's own `duckdb_views()` (measured — `__fab_dv` sitting there after a plain DELETE + SELECT, next to a
      pre-existing `__fabricator_delta_write_src` from the write path, which has the same shape and is NOT fixed
      here).
    - Fix: **per-query unique names** (`__fab_dv_<n>` / `__fab_files_<n>`, an interlocked counter) plus an
      explicit `DROP VIEW IF EXISTS` once the query has been drained. The drop is what makes uniqueness
      affordable — without it the catalog would accumulate one view per scan instead of one stale one. Verified:
      the failing configuration returns correct results, and the leaked-view count is **0**.
    - **⚠ AND THE WRITE PATH HAD THE SAME BUG, WORSE — `NativeParquetDataFileWriter` (fixed in the same pass).**
      Its `__fabricator_delta_write_src` is the identical fixed-name binding, and it is hit by CONCURRENT WRITERS
      rather than a tuning knob. **MEASURED: six concurrent Delta writers in ONE process — exactly the
      `dbt run --threads N` shape, on `PROVIDER 'delta'` where `native_write` is the DEFAULT — and FIVE OF THE
      SIX FAILED** (*"failed to register input view '__fabricator_delta_write_src'"*), leaving their tables
      absent. After the fix: 0 errors, all six at their full row count, 0 leaked views. ⚠ Note CLAUDE.md records
      a dbt `--threads 4` lakehouse run as PASS=4/4 — that predates the native-write default flip, so do not
      read it as coverage of this.
    - **⚠ THEN SWEPT, and there were THREE MORE fixed names — five sites in total, not two.** The two found by
      tripping over them were not the whole class: `HostBatchFilter` (`__fabricator_scan_batch`, every pushed
      batch filter), `ExternalTableRouting` (`__fabricator_external_insert`, every routed external-table
      INSERT) and `DeltaCatalog.SortStream` (`__fabricator_sort_input`, every SORTED BY write) had the same
      shape. All now take per-call names. **Do the grep, do not fix only what bit you** —
      `grep -rn "(string, IArrowArrayStream)\[\]" dotnet/` finds them all.
    - **⚠ `SortStream`'s DROP IS OWED and deliberately NOT faked**: it returns a LAZY stream, so the view must
      outlive the call and no point in that method knows the caller has finished draining. It needs a stream
      wrapper that drops on Dispose, unlike the COPY/filter sites whose queries materialize before returning.
      Until then a sorted write leaves one view per call in the catalog — **the RACE is fixed there, the LEAK is
      not.**
    - The shared plumbing is now `BoundInput.NextName` / `BoundInput.Drop`
      (`dotnet/Fabricator.Bridge/SingleScanArrowStream.cs`), so any future bound input gets both halves by
      construction rather than by remembering.
    - **⚠ TWO OF MY THREE TESTS HERE PROVED NOTHING, and the user named the second.** A join of two DV tables
      returned right answers — but a hash join materialises one side before probing, so the scans never overlap.
      A `UNION ALL` with `threads=8` also passed — and the user pointed out a union CAN produce concurrent
      queries, which is right: **`PhysicalUnion::BuildPipelines` may run branch pipelines SEQUENTIALLY**, a fact
      already recorded in this file from the 4g premature-finish bug. So a green union test is a scheduling
      accident. Only forcing concurrency through our own prefetch knob reproduced it. **When testing a race, do
      not accept a passing shape until you have shown that shape actually overlaps.**
  - **⚠ THE GATED SHAPES SHOULD GO TO A `UNION ALL`, NOT TO THE LOOP — MEASURED 2026-08-06 after the user asked
    "why didn't you choose the union instead of the looping part?", and the answer is that they were right and I
    had not measured it.** Marginal cost per file on one 200-file table: **single `read_parquet([…], schema=…)`
    ~0.2 ms / `UNION ALL` of per-file SELECTs ~0.4 ms / one host query per file ~1.9 ms** (40 files: 0.012 s vs
    0.025 s; 200 files: 0.042 s vs 0.124 s — the union−single gap is ~0.4 ms per branch, roughly linear; all
    three return the identical checksum). So the loop is ~4x worse than a union for shapes the single call cannot
    express, and only the DV one was actually BLOCKED (on the `FullTableSql` literal-inlining problem below).
    Rowid, partition literals and row tracking were not blocked at all.
    - **A union does NOT lose per-file pruning, so `BatchPlan`'s case 4 is true of the single call and FALSE of a
      union.** Established from the PLAN, not from timing (the trap `DvRangeCondition` records): with one branch
      carrying `file_row_number >= 4000`, its `READ_PARQUET` shows `Filters: file_row_number>=4000` and emits
      **1,000 of 5,000** rows while the sibling branch emits all 5,000.
    - **⚠ Two things to settle BEFORE building it, either of which shrinks the win.** (a) A union keeps the
      per-file FOOTER PROBE (`ResolveFileMapping`) that `schema`'s `default_value` is what removed — so the
      ~0.4 ms/file above is the QUERY cost only and the probe is on top, possibly the larger term. Isolate it
      first. (b) `FullTableSql`'s "NOT usable for nested MAPPED columns" gate looks OVER-BROAD on inspection: in
      name mode `StructShapeDiffers` is true for every file (stored != logical for every mapped child), so every
      branch rebuilds to LOGICAL names and they agree; where names stay physical they do so in every branch. The
      hazard needs branches to DISAGREE, which a shared table schema makes hard to arrange. Establish that rather
      than assume it — the measured union hazard above is real, it just may not be reachable here.
  - **STILL OPEN, in the order the win would arrive:** (0) ⚠ **partitioned tables are DONE — off the loop since
    2026-08-07**, so the remaining gated set is smaller than the union item below assumes; (1) the gated shapes
    via the union above; (2) id mode,
    once #24407 lands; (3) `FullTableSql`'s inlined DV literals — still the documented `WITH … AS MATERIALIZED`
    fix, and now a PREREQUISITE of (1) rather than a separate cleanup, since the DV files are what a union of the
    gated shapes has to carry. A `filename`-keyed metadata join was the route recommended here first; the union
    supersedes it (per-branch constants need no join at all), but the finding that made it plausible stands and
    is worth keeping: **`filename` echoes the EXACT string passed in the list**, so it is a stable join key —
    with the caveat that a mismatch would silently DROP that file's rows, so any such join needs a `LEFT JOIN`
    plus an `error()` on an unmatched file. And the residual per-row cost is the Arrow hand-back, which no
    batching of any shape touches.
- **THE UPDATE POST-IMAGE GROUPED FLUSH — DONE 2026-08-06 (C#-only, no ABI). ⚠ IT DOES NOT FIX "UPDATE
  MEMORY", AND THE MEASUREMENT SAYING SO IS THE MOST USEFUL THING HERE.** Both UPDATE paths
  (`DeltaReader.MergeOnReadUpdateAsync` autocommit, `DeltaCatalog.BufferUpdateRows` buffered) used to
  accumulate EVERY post-image batch — and every pre-image on a CDF table — before writing anything. They now
  write a group's worth as the read-back streams and keep only the `WrittenDataFile`/`CdcFile` actions. Still
  exactly ONE commit. Threshold `DeltaReader.UpdateGroupBytes`, 64 MiB of Arrow data, env-overridable via
  `FABRICATOR_DELTA_UPDATE_GROUP_BYTES`.
  - **⚠ FILE LAYOUT IS UNCHANGED BY CONSTRUCTION, which is what makes the grouping free rather than a
    trade-off** — and it is worth knowing independently: `WriteDataFilesAsync` writes **one parquet file per
    (input batch × partition)** (`DeltaTable.cs:5053`, a `foreach` over the batches), so N read-back batches
    become N data files whether they arrive in one call or a hundred. The file count of an UPDATE's post-images
    is therefore its BATCH count and no size target touches it. Measured: a 5000-row UPDATE adds 3 files, 50k
    adds 25, 200k adds 98 — i.e. ~2048 rows per batch.
  - **⚠ IT IS INERT ON THE BUFFERED PATH, and this entry claimed otherwise until it was measured
    (2026-08-06).** A group boundary can only fall BETWEEN read-back batches, and the two paths batch
    differently. Same table, same 60k-row UPDATE, threshold forced to 1 byte: **autocommit 30 group flushes,
    buffered 1** — the buffered read-back hands over all 60,000 rows as ONE batch (confirmed independently by
    the post-image file count, 30 files vs 1, since `WriteDataFilesAsync` writes one file per input batch).
    So on the buffered path the group IS the statement and the grouping changes nothing. The autocommit
    numbers below are real; do not generalise them.
    - **⚠ MECHANISM — MEASURED, and it is NOT autocommit-vs-buffered at all: it is WHICH READER is in play.**
      Same autocommit UPDATE, same shape, threshold 1: `native_read true` ⇒ **30** flushes,
      `native_read false` ⇒ **1**. DuckDB's `read_parquet` yields standard 2048-row vectors; engineered-wood's
      codec reader yields **one batch per ROW GROUP** — pinned by a 300k-row control giving exactly **3**
      batches at the 122880 default. And the buffered read-back opens with a bare `DeltaWriter.Options()`,
      passing **no `dataFileReader`** (`DeltaReader.cs:974`), so it takes the codec reader ALWAYS — see the
      `native_read` entry in the streaming audit, which is the real defect here.
    - The candidates an earlier pass listed are all RETIRED: `BlockingEnumerable` was correctly cleared (it is
      a lazy pass-through), and `atVersion` / `skipUnresolvable` / `ReconcileBatch` were all wrong. **The
      answer was in the OPTIONS passed at open, not in the enumeration** — which is the reusable lesson: when
      two callers of one method behave differently, diff what they CONSTRUCT it with before diffing the call.
  - **⚠ MEASURED, and the headline is not the one this was built for.** On the shape that favours it most
    (600k rows × 16 VARCHAR, UPDATE every row, SET one column): **managed heap peak 327 → 171 MB** and now
    bounded by the GROUP rather than by the statement — but **process peak working set only 614 → 548 MB**.
    Time is flat (9.3 → 9.6 s; **71 flushes is as fast as 5**, so flush count costs nothing measurable). On a
    NARROW table the grouping does not fire at all: 1M rows × 3 columns accumulates ~50 MB of read-back, under
    the threshold, and peak is **identical either way (449 MB)** — so the earlier "~474 MB per 1M matched rows"
    figure was never mostly this.
  - **⚠ THE ACTUAL DOMINANT TERM, found by instrumenting the working set through the path: ~180 MB is already
    spent BEFORE the read-back begins (253 MB at 1M × 3 cols).** That is DuckDB's own side of the statement
    plus, on ours, `DeltaCatalog.ExecuteUpdate`'s `Dictionary<long, object?[]>` of **BOXED** SET values, the
    Arrow batch rebuilt from it, and `updRowByRid` — all three complete before any provider work starts, all
    three scaling with MATCHED rows.
    - **⚠ NOW MEASURED, not inferred, and the SLOPE is what makes it conclusive (2026-08-06).** One table,
      1M rows, every row touched, three statements differing only in how many SET values cross the seam:
      **DELETE (rowids only, no boxes) 204 MB / 1.7 s** — the floor; **UPDATE 1 SET column 454 MB / 5.5 s**
      (+250 MB); **UPDATE 3 SET columns 651 MB / 5.6 s** (+447 MB). So **~98 MB per ADDITIONAL SET column per
      1M rows ≈ 98 BYTES PER 8-BYTE BIGINT VALUE**, a ~12× representation overhead, and the first column costs
      more (~250 MB) because it carries the per-ROW costs too (the `object?[]` header + the dictionary entry).
      The DELETE floor is the control that makes this OURS rather than DuckDB's: same rows, same table, same
      scan, no SET values. Note the TIME gap as well — 3.2× for the same rows.
    - **NEXT FIX: keep the SET values in ARROW form instead of boxing them** — `ParseUpdateStream` builds
      Arrow columns directly from the incoming chunks and `updRowByRid` becomes rowid → ordinal. Expected
      ~250 MB → ~50 MB for the one-column case. It is a DML-SEAM change (`ParseUpdateStream` /
      `ExecuteUpdate` / `BufferUpdateRows`), not a Delta one; `ExternalTableRouting` also calls
      `ExecuteUpdate`, so check that path too.
      - **⚠ THE CONSTRAINTS FOUND WHILE SCOPING IT, all of which make the naive version wrong.**
        (1) **⚠ THE INCOMING BATCHES CANNOT BE RETAINED — this is the one that decides the design, and it is
        already established in this codebase.** `DeltaWriter.Materialize` does a full Arrow **IPC round-trip**
        (write every batch to a `MemoryStream`, read them back) precisely because *"the source batches may be
        freed after consumption"*; and `ParseUpdateStream`'s own `ReadScalarDeep` is documented as deep-copying
        because *"the batch is disposed after this loop"*. So "keep the chunks and address rows inside them" is
        a use-after-free, not an optimisation. The cheap independent copy is
        `ArrowCompute.Take(batch, schema, identityIndices)`, which allocates new buffers.
        (2) **⚠ A CLAIM RECORDED HERE WAS FALSE AND IS CORRECTED: `Apache.Arrow.ArrowArrayConcatenator.Concatenate`
        EXISTS and is public** (engineered-wood uses it in six places, e.g. `DeltaTable.cs:6509`,
        `LanceFileReader`, `VortexFileReader`). The earlier note said there was no Concat — that came from
        reading `EngineeredWood.Arrow.ArrowCompute`'s surface, which has `Take`/`Widen`/`MakeNullArray` and no
        concat, and generalising from ONE class to the whole Arrow surface. It is the same backwards-search
        error the tier-1 notes warn about: **a grep that finds nothing has only established where you looked.**
        With Concat available, the per-chunk copies can be joined into one array per column and the design does
        NOT need a bespoke gather helper.
        (3) **`updates[rid] = vals` DEDUPLICATES by rowid, last-write-wins** — reachable via
        `UPDATE … FROM other` whose join matches a target row twice — and it also sets the statement's
        REPORTED row count. Appends cannot overwrite, so the replacement must append everything, keep
        rowid → LAST ordinal, and compact with one `Take` at the end.
        (4) **⚠ The boxing is currently also doing a TYPE CONVERSION**: `BuildArray(field.DataType, values)`
        rebuilds each SET column at the TARGET column's type, so an incoming array of a different width or
        unit is silently converted through the boxed value. Reusing the incoming Arrow array directly changes
        that behaviour. Cheapest faithful answer: reuse Arrow only where the incoming type EQUALS the target
        type, and keep the boxed rebuild for that column otherwise — behaviour-preserving where it matters and
        free in the common case.
      - **Shape that follows from (1)–(4):** per chunk, `Take` an independent compact copy and record
        rid → packed (chunk, row); at the end `Concatenate` per column and apply ONE `Take` with the surviving
        ordinals — which yields `updatesBatch` DIRECTLY, so `ExecuteUpdate` stops rebuilding it and
        `BufferUpdateRows` reads its values from that batch instead of from boxes.
  - **⚠ THE ALL-OR-NOTHING ROW-ID RULE HAD TO MOVE EARLIER, and that is the one semantic consequence.** A
    group is written before the later groups' ids are known, so "every selected row resolved a stable id" can
    no longer be decided after the read-back. It is now decided BEFORE it, from the files: the read-back yields
    a null id only where the row's file has no `baseRowId` AND no materialized value, and a writer that
    materializes ids also stamps `baseRowId` (the spec requires one on every `add` of a row-tracking table), so
    "every selected file has a baseRowId" is the same condition — a dictionary lookup per selected path
    (`snapshot.ActiveFiles`), no extra IO. Autocommit checks the SELECTION's paths; the buffered path uses the
    new `TxnDmlProfile.AllFilesRowTracked` (computed in the probe it already does) and trusts it ONLY when the
    pinned version IS the version it describes. **Where it cannot be established the threshold is DISABLED and
    the statement buffers whole, byte-identically to before** — a legacy table keeps its old behaviour instead
    of acquiring new semantics from a memory fix. A null appearing after a group was written WITH ids throws
    loudly rather than silently splitting identity.
  - Also trimmed: `ridsPerBatch` / `srcTracking` are now drained per batch (their producer only appends, never
    reads them back), so they no longer accumulate across the statement either.
  - **64 MiB rather than 16 MiB** (which measured marginally better, 152 MB heap) because the buffered path's
    per-group write used to **open the table** — one `_delta_log` LIST per group, cheap locally and not on
    OneLake/S3. **FIXED 2026-08-06: `TryEagerWriteBatches` now reuses the pair's HELD table**
    (`EnsureHeldTableAsync`) instead of opening and disposing its own, so an eager write costs no log read at
    all. It no longer disposes the table either — that belongs to the buffer entry and pulling it out from
    under the held transaction would break every later statement of the DuckDB transaction.
    - **⚠ THE SWAP WAS NOT THE PURE PERF CHANGE IT LOOKED LIKE, AND DOING IT FIRST WOULD HAVE BROKEN A
      USER-FACING FEATURE.** `TryEagerWriteBatches` was the ONLY open in the whole Bridge passing a WRITE SPEC
      (`ResolveWriteSpec`); the held table passed none. Reusing it would have made the eager path lose the
      user's `delta_write_options` rather than making the held one honour them. So the spec was added to
      `EnsureHeldTableAsync` FIRST — which fixed a real defect in its own right (below) and only then made
      the swap equivalent.
  - **⚠ THE DEFECT THAT FOUND: WRITE TUNING REACHED THE BULK PATH AND ALMOST NOTHING ELSE (fixed for the
    buffered surface 2026-08-06, still open elsewhere).** `delta_write_options`
    (`compression` / `row_group_size` / `bloom_filter_columns`) is resolved by `ResolveWriteSpec`, which
    returns **null** when nothing is configured — so the divergence is invisible until a user sets something,
    which is why nothing caught it. MEASURED per file on the codec engine with `compression 'zstd'`: the CTAS
    files came out **ZSTD** and, in the SAME table, the CDF change files **SNAPPY** and the merge-on-read
    UPDATE's post-image file **SNAPPY**. A table therefore accumulates MIXED compression, and on an
    incrementally-updated dbt model most bytes would silently be snappy.
    - **⚠ The codec engine is required to see any of this**: under `native_write` (the `PROVIDER 'delta'`
      default) DuckDB's COPY writes the data files and EW's `ParquetWriteOptions` never apply — a first
      attempt at this measurement on `PROVIDER 'delta'` returned SNAPPY for everything and was VOID, not an
      answer. The gate pins the codec engine for the same reason and carries a positive control.
    - Fixed here: `EnsureHeldTableAsync` now passes the spec, so the CDF change files and any batches the
      flush parks honour it. **STILL OPEN and measured, for the audit:** every other EW open in
      `DeltaReader` passes no spec — the merge-on-read UPDATE post-images, the copy-on-write DELETE/UPDATE
      rewrites, and OPTIMIZE's compaction output. Those need the spec plumbed from the catalog into a static
      reader, which is more than a one-line change.
    - Gate `verify_with_options` 68 → **82**, mutation-tested (reverting the spec on `EnsureHeldTableAsync`
      fails at exactly the CDF assertion with `SNAPPY`).
  - **⚠ GATE: `verify_delta_update_grouped.test` (72), and it needs the runner to FORCE the threshold.** No
    hermetic suite comes within two orders of magnitude of 64 MiB, so without this the grouped path ships with
    ZERO coverage; `run-suites.sh` gives this ONE suite `FABRICATOR_DELTA_UPDATE_GROUP_BYTES=1` and `unset`s it
    for every other (load-bearing in both directions — a value left in the developer's shell would otherwise
    group every suite and the shipped default would go untested). It updates **6000 rows on purpose** (~2048 per
    batch ⇒ three groups) and asserts the ONE commit per statement, read-your-writes + ROLLBACK on the buffered
    path, the CDF pair joining row-for-row across group boundaries, and stable ids surviving. It passes
    IDENTICALLY with the default threshold — that equivalence is the point. **Mutation-tested with two mutants,
    each killed at its own section**: not clearing the per-group id list dies at the FIRST grouped UPDATE
    (*"materializedRowIds must carry one entry per row"*), and not clearing the per-group pre-images **survives
    51 assertions** before the CDF section catches **12144 pre-images for 6000 rows** — which is precisely why
    that section exists.
  - Gates: hermetic **66/66 — 6367**; the three engine-doubled delta suites also re-run with
    `GROUP_BYTES=1` at identical assertion counts.
- **Eager-write DeltaTxnBuffer — ALL SLICES DONE (A, B, C1–C3, D + edge lifts).** Data files always land
  on storage at statement time; the buffer holds ACTIONS. **"Rollback = invisible orphans for VACUUM" is
  now HISTORICAL — as of 2026-08-02 a ROLLBACK RECLAIMS the bytes, via two mechanisms with different
  owners** (full record: [docs/delta-transactions.md](docs/delta-transactions.md) §7):
  (a) the flush's transaction is `await using`, so a flush that does not commit takes back what EW's OWN
  writers staged — e.g. the deletion vector of a buffered DELETE (`StageRowDeletesAsync` writes it before
  the commit is judged). ⚠ Safe only from EW #49; at #46 the same line would have deleted COMMITTED data.
  Measured — a small delete's vector is INLINE, so the orphan only reproduces above the 1 KB roaring
  threshold. Gate verify_delta_txn_version §9 (65).
  (b) `RollbackTransaction` calls EW #52's **`DiscardDataFilesAsync`** on the eagerly-written DATA files,
  the class (a) structurally could not touch — EW's provenance rule never collects a host-written file, so
  the host has to name them. ⚠ This needed a C++ fix first: `FabricatorTransactionManager::
  RollbackTransaction` **never set an opener**, so it held a STALE `ClientContext*` — harmless while
  rollback did no IO, a use-after-free the moment it does any. It now takes its own short-lived connection
  like the commit path, and clears the opener to 0 (there is no caller context to restore). Never throws:
  a failed discard logs and leaves the orphan, i.e. the old behaviour. Gate
  verify_delta_catalog_transactions 943 → 944, mutation-tested. Both gates mutation-tested. Incl.
  S3 multi-writer conditional-PUT commits (SECRET-routed), the dbt table-swap RENAME fix, buffered
  IDENTITY/CDF/same-txn-DML, and the partitioned×native_read partition-column bug fix. Gate
  verify_delta_catalog_transactions (now 941); semantics [docs/delta-transactions.md](docs/delta-transactions.md).
  Still immediate by design: identity creates, DROP/OPTIMIZE/VACUUM, CREATE-OR-REPLACE/partition-overwrite.
  - **⚠ A CREATE-PLUS-DATA IS NOT ATOMIC — TWO VERSIONS, AND IN PLAIN AUTOCOMMIT TOO, NOT JUST IN A TRANSACTION**
    (measured 2026-08-03; **scope corrected 2026-08-04** — [docs/delta-transactions.md](docs/delta-transactions.md)
    §7.1, [docs/known-limitations.md](docs/known-limitations.md) 1.5/1.6). v0 = `protocol`+`metaData` (an EMPTY
    table), v1 = the data. **⚠ This was recorded here and in §7.1 as a BUFFERED-FLUSH property, which hid the common
    case**: a plain autocommit `CREATE TABLE … AS SELECT` produces the identical two commits by a DIFFERENT path
    (`DeltaWriter.WriteAsync` → `OpenOrCreateAsync` commits v0, then `table.WriteAsync` v1), so the statement it
    most often applies to has no `BEGIN` in sight. Consequences: a concurrent reader can observe the empty table,
    and **a data-write failure leaves an empty committed table behind a statement the user saw fail** — the inverse
    of every other flush path (reasoned from the measured shape, not itself measured).
    - **What protects it today is STRUCTURAL, not luck: every reachable failure fires BEFORE v0**, because the
      Arrow→Delta schema conversion is a PRECONDITION of the create (`OpenOrCreateAsync` cannot be called without a
      Delta schema). Measured: a `TIMESTAMP_NS` column and an `INTERVAL` column both refuse with NO table created.
      The residue is a DATA-write/commit failure (storage, permission, disk full, network), which has no
      compensation — `WriteAsync`'s `finally` only disposes; a commit CONFLICT is retried, other failures are not.
    - **⚠ A SEPARATE, BIGGER BUG WAS FOUND WHILE DOCUMENTING THIS AND IS NOW FIXED (2026-08-04): the shared
      C++ layer NEVER CHECKED `ERROR_ON_CONFLICT`**, so a plain create reached the provider as an ordinary create
      — `FabricatorSchemaEntry::CreateTable` handled `REPLACE_ON_CONFLICT` (drop first) and `IGNORE_ON_CONFLICT`
      (forward the flag) and passed everything else through. On Delta, `OpenOrCreateAsync` then just OPENED the
      existing table, so **two** shapes succeeded while doing nothing: `CREATE TABLE t AS SELECT` wrote no rows and
      kept the OLD data (measured with a positive control — 10-row table + a CTAS of 2 rows ⇒ still 10 rows, exit 0;
      the same shape on DuckDB's own table errored), and **`CREATE TABLE t (a INTEGER, b VARCHAR)` silently IGNORED
      THE DECLARED SCHEMA**. ⚠ That second half was NOT in the original write-up — it surfaced only from running both
      shapes instead of reasoning about the CTAS one, which is the same lesson as the mode-`Overwrite` correction
      below. Now refused with DuckDB's own `CatalogException::EntryAlreadyExists`, so both the message and its
      structured `ENTRY_ALREADY_EXISTS` extra-info match every other DuckDB catalog; `OR REPLACE` / `IF NOT EXISTS`
      untouched. Gates `verify_delta_catalog_write` (+12, engine-doubled) + `verify_ctas_text_type` (+8), both
      mutation-tested (the mutant dies at the first assertion with *"Query unexpectedly succeeded"*).
      - **⚠ THE SCOPE QUESTION IS SETTLED AND THE ANSWER IS NOT UNIFORM** — this file previously recorded it as
        UNVERIFIED. **SQL Server was never in the dangerous half**: its own `CREATE TABLE` rejects a duplicate, so
        no write was ever lost; the user just got the raw provider error (`2714: There is already an object named
        …`), which reads as a SQL Server problem rather than an ordinary catalog conflict. **DAX is structurally
        exempt** (its provider refuses CREATE outright). So the silent data-keeping was Delta-ONLY while the
        confusing message was SHARED — one fix covers both, and the gate spans both tiers because they share the
        code path, not because they shared the symptom.
      - The existence oracle is **`GetOrCreateEntry`, not a bare `table_types_` lookup**: a table can exist without
        being in the discovered name list, because an ATTACH `table_filter` bounds ENUMERATION only and that path
        fetches BY NAME. Pinned by making the gate's conflict against a table that exists on storage and has NOT
        been read through the attach. It is also the call the successful create already makes, so the
        materialization cost is paid only on the conflict path.
      - **⚠ THE MECHANISM IS NOT WHAT IT LOOKS LIKE — the two symptoms have DIFFERENT OWNERS, and an earlier
        write-up of this (mine) attributed both to Delta.** `PhysicalPlanGenerator::CreatePlan(LogicalCreateTable&)`
        (`duckdb/src/execution/physical_plan/plan_create_table.cpp:37`) probes for an existing entry and, finding
        one with a non-REPLACE conflict action, routes the statement to a bare `PhysicalCreateTable` — **DISCARDING
        THE CHILD PLAN, i.e. the SELECT.** Proven directly rather than read: `EXPLAIN CREATE TABLE IF NOT EXISTS m
        AS SELECT * FROM range(1000000)` over an existing table prints a physical plan of `CREATE_TABLE` ALONE,
        no scan in it. So **"no rows written" was DuckDB's plan downgrade, not the provider swallowing a write** —
        the write was never planned; only "no error" was ours.
      - Two consequences. **`mode = Overwrite` was never REACHED in the broken shape**: `overwrite = createTable ||
        replace` (`DeltaCatalog.cs:2039`) sits on the `begin_bulk` path under `FabricatorPhysicalCreateTableAs`, and
        the downgrade bypasses that operator entirely — so it is not merely "correct given DuckDB should have
        rejected the conflict first" (the weaker claim recorded here before), it is OFF THE PATH. And **one check
        covers BOTH the plain CREATE and the CTAS** by DuckDB's design, not by luck: it delegates the conflict
        decision to the catalog and funnels both spellings into the operator that asks the catalog.
    - **Not a protocol limit** — Delta permits `protocol`+`metaData`+`add` in v0 — but an EW API-shape one, and
      THREE doors are locked the same way: `StartTransaction` is an INSTANCE method needing `OpenAsync`,
      `CreateAsync` writes v0 at once, and `CommitDataFilesAsync` (whose `extraActions` could carry
      metaData+protocol) is ALSO an instance method. So a transaction that creates its table is inexpressible;
      fixing it needs an upstream static/factory form.
    - **A cheap improvement that needs NO upstream change, NOT built:** reorder the autocommit CTAS to write the
      data files FIRST and create+commit after — the shape `TryStreamCreateFiles` already implements for the
      buffered path. A data-write failure would then precede any commit; the residual window shrinks to two
      adjacent log writes. It does NOT reduce the version count and needs the host-query native writer.
      - **⚠ THIS LINE USED TO SAY "and is non-partitioned-only today". THAT WAS WRONG** — the restriction is
        `TryWriteStreamingCoreAsync`'s, NOT `TryStreamCreateFiles`', which partitions via `RunCopyPartitioned`
        (one DuckDB `COPY … PARTITION_BY`). MEASURED: a buffered partitioned CTAS writes through DuckDB into a
        Hive layout with `_delta_log` untouched until the flush, and a partitioned CTAS works on BOTH engines
        today (`created_by` = `DuckDB version …` on `PROVIDER 'delta'`, `EngineeredWood` on the codec — the
        `verify_delta_rename` technique). So the reorder would cover partitioned CTAS too; it is not a
        simple-case-only mitigation, which makes it worth more than this note claimed. What it would NOT cover
        is the CODEC provider (no DuckDB writer to stage with) ⇒ it would make the engines DIVERGE on failure
        semantics where today they agree. Say so in the slice that takes it.
    - **⚠ THE ORPHAN IS UNCONDITIONAL ONCE v0 LANDS AND WE DO NOT COMPENSATE — and a version-checked delete is
      NOT the fix (measured 2026-08-04).** Both paths put the create OUTSIDE the guarded region and both
      `finally` blocks only DISPOSE; only a commit CONFLICT is retried. `RollbackTransaction` cannot help — it
      reclaims DATA FILES, and `DiscardBufferedFiles` OPENS the table to do so, presupposing it exists.
      "Check the version, delete if still 0" races any writer committing v1 in the window (a plain INSERT from
      another connection — `dbt --threads N` is a fleet of them — or a foreign engine): deleting just
      `…0.json` leaves the table UNREADABLE (measured error names the missing version) though recoverable by
      hand, while deleting the whole FOLDER destroys the other writer's data irreversibly AND is the worse
      scope because a recursive delete is atomic on NO backend here (on S3 `DropTable` goes file-by-file), so
      it can partially complete and leave a log referencing removed files. **⚠ BUT THE OBJECTION IS AUTHORITY,
      NOT ATOMICITY, and the first draft of this note led with atomicity — which does not survive one
      comparison: `DROP TABLE` is the SAME unconditional recursive folder delete** (`DeltaCatalog.DropTable` →
      `HostFs.RemoveDir`, S3 per-file fallback swallowing per-object errors) **and we ship it.** The separator
      is CONSENT: DROP destroys a table the USER NAMED with the user present, and re-running it finishes a
      partial one (losing a concurrent writer's rows IS what DROP means — no Delta engine has a transactional
      DROP); the compensation would infer destruction from a failure WE caused, on a path the user asked us to
      CREATE, with a third-party victim who ran only an INSERT and nobody to notice. **The safe primitive is
      deleting the files you WROTE by name** (`DiscardDataFilesAsync` refuses anything a FRESH log references —
      needing no authority beyond our own write, which is the real reason it is acceptable) — and it is
      legitimate only AFTER the reorder above, when the folder is not yet a table (nothing is discoverable at a
      path with no `_delta_log`; a competing CREATE races on commit-0, a put-if-absent, not on our bytes).
      Full record: [docs/delta-transactions.md](docs/delta-transactions.md) §7.1.
    - **⚠ Temp-name-then-rename does NOT fix the version count** (the temp table still gets v0 then v1) — it only
      hides both from readers of the final name. And it costs an O(bytes) commit on S3 (rename = ListObjectsV2 +
      CopyObject per key + DeleteObjects) and LOSES the conditional create: today two concurrent `CREATE TABLE t`
      race on commit-0, a put-if-absent, while a rename is unconditional on the backends where §8.5 applies —
      so the second rename would silently destroy the first table. Assessed and REJECTED 2026-08-04.
  Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Fabric-notebook AMBIENT AUTH — DONE + validated live.** All three providers work with ZERO
  credentials on Fabric compute via `FabricNotebookCredential` (the trident token service; per-scope
  refreshing tokens); azure `access_token` secrets consumed for SQL. Pinned gap: a STATIC storage-token
  secret cannot serve the fabric+storage audiences for abfss ATTACH — use ambient. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Sync-over-async (Bridge) — DONE** (superseded note; see the entry near the top + the full record in
  docs/ew-master-migration.md). AsyncLocal ambients (`0533eb7`) keep the opener/txn across pool hops. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Discovered TVF/proc wrapper extraction — DONE** (`SqlServerProcedure` / bespoke
  `SqlServerTableValuedFunction`; dispatch unified under `IBoundTable`, v29). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Load-time GLOBAL functions — ALL FIVE KINDS DONE (scalar/in-out/collector/table/aggregate, ABI
  v46/v47)** incl. the host-FS global table sub-case (`set_active_opener`; `fabricator_delta_scan` is
  pure C# — a new lakehouse format costs zero C++). [docs/global-functions.md](docs/global-functions.md);
  verify_global_functions 63. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **VARIANT for the Delta provider — DONE through SIX passes** (leaf-blob transport
  `ew.variant_transport` — DuckDB #24157 filed for the canonical struct; codec + FULL shredding tiers;
  DML/OPTIMIZE via the IDataFileReader seam; mapped/nested gates; Spark + kernel validated live; the
  Fabric T-SQL endpoint REJECTS VARIANT and id-mode mapping).
  [docs/variant-support.md](docs/variant-support.md); verify variant 157. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **NESTED STRUCT-field schema evolution — DONE** (alter kinds 9–11; recursive read reconcile; the
  native_read presence probe lifted the top-level + nested limitations). verify nested_alter 100. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Delta write-side NOT NULL enforcement — DONE** (`DeltaNullability`, nested included; per-statement
  Delta commits pinned as a documented divergence). verify constraints 50. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Delta IDENTITY columns — DONE** (v53 marker `id BIGINT AS (0)`; OCC retry regenerates from the fresh
  HWM — safer than Spark). verify identity(delta) 38. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **DAX / ADOMD 2nd provider — DONE slices 1–6** (PBI Desktop + workspace XMLA + Fabric SP/ambient auth;
  scan pushdown + streaming to 10.5M rows; `system` DMV schema; `daxeval`/`daxevaltable`(collector)/
  `daxeach`; the read-past-EOF ADOMD gotcha). Read-only **for DATA** — since 2026-07-31 it also hosts a
  `CatalogFunctionSet` and the TMSL refresh trio (`dax_refresh`/`_table`/`_partition`), which move data
  INTO a model; model AUTHORING stays out (no `dax_tmsl`). [docs/dax-provider.md](docs/dax-provider.md);
  verify_dax 29 (manual — needs PBI Desktop). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Multi-edition support (Fabric WH / Synapse / box) — DONE slices 1–6** (`ServerProfile`, MARS gating +
  connection mode, profile-driven type mapping, collation-gated ORDER BY pushdown, JSON/UUID/tz
  read-side; write-side rich types deliberately deferred).
  [docs/warehouse-support.md](docs/warehouse-support.md). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Settings refactor — DONE, all three flavors** (settings v33/v34 + ATTACH options v37 + secret fields
  v38 — the provider is fully self-describing; `RESET` does not fire set-callbacks, restore with SET).
  [docs/provider-extensibility.md](docs/provider-extensibility.md). Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Plugin system — default-context SPI DONE** (`FABRICATOR_PLUGIN_DIR`; plugins load into the BRIDGE's
  ALC and must align their dependency closure — Apache.Arrow above all; `Fabricator.Abstractions` is the
  contract assembly; per-plugin ALC isolation deferred). [docs/plugin-system.md](docs/plugin-system.md);
  verify_plugin 10. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
## Implementation status (current)

**Phases 1–2 complete + streaming bulk write; verified against real SQL Server on DuckDB v1.5.4.**

Implemented and verified:
- **ATTACH + catalog**: schemas/tables/views, three-part naming, cross-catalog joins; `schema_filter`/
  `table_filter` (case-insensitive regex); ATTACH-time connection validation (no orphan catalog on
  failure); `mssql://` URI; `CREATE SECRET (TYPE mssql, …)` incl. Azure Entra/Fabric auth.
- **Read path** fully in C# behind `get_metadata`/`scan_table` ABI calls — **C++ has zero T-SQL**.
- **Pushdown**: projection (by-name), filter (best-effort via `pushdown_complex_filter`, never erases →
  DuckDB always re-applies; superset-safe shapes only), bare `LIMIT` (`TOP n`), `ORDER BY`+`LIMIT`
  (TopN, gated: NULL-order compatible, no pushed filter, and **string keys only under a binary database
  collation** — `ArrowStreamBindData::string_order_pushable`, set at scan bind from
  `FabricatorCatalog::StringOrderPushable()`, which `LoadCatalog` caches via `FetchBinaryCollation` reading
  the `FABRICATOR_META_SERVER_INFO` profile; binary `_BIN/_BIN2` collation sorts bytewise == DuckDB. No ABI.
  `test/verify_collation_pushdown.test`).
- **Statistics → optimizer**: cardinality (row count from `sys.dm_db_partition_stats`) + per-column NDV
  (leading-column histogram). **min/max deliberately NOT reported** (DuckDB prunes filters on min/max →
  stale SQL Server stats could drop rows; NDV is costing-only so stale is safe).
- **rowid** from PK / smallest unique index (scalar + compound STRUCT) → enables UPDATE/DELETE. **An IDENTITY
  column is also usable as the rowid** (engine-generated, effectively unique) so UPDATE/DELETE work on a table
  with NO PK/UNIQUE at all — `RowIdSql` composes an `IF EXISTS(...) <a> ELSE <b>` with the precedence flipped by
  engine: **Fabric/Synapse warehouse prefers the IDENTITY column** (their PK/UNIQUE are NON-ENFORCED hints =
  weak uniqueness) — `IF is_identity → identity ELSE PK/unique-index`; **box / Azure SQL prefer PK/unique** (their
  PKs are enforced/intended) and fall back to the IDENTITY column only when the table has no key constraint —
  `IF has_pk_or_unique → PK/unique-index ELSE identity`. Both validated live (identity-only table, no PK →
  UPDATE + DELETE via the identity rowid, on box AND Fabric Warehouse). Falls back to no rowid when neither
  exists (as before).
- **Time travel** (`FROM cat.t AT (TIMESTAMP => ts)`) → SQL Server temporal tables `FOR SYSTEM_TIME AS OF`
  (`eeae2e2`). The AT clause is a **bind-time, per-table-reference constant** (not per-scan pushdown), so it
  flows through the binding: `FabricatorCatalog::SupportsTimeTravel()→true` (else the binder rejects it with
  "Catalog type does not support time travel" before the scan), `FabricatorTableEntry::GetScanFunction(EntryLookupInfo)`
  reads `lookup_info.GetAtClause()` {unit,value} onto `ArrowStreamBindData` (the basic + lookup overloads share
  `BuildScanFunction`), `BuildScanSpec` folds it into the existing `spec_json` (`"at":{unit,value}` — **no new
  ABI**), and C# `ScanFromSource` emits the timestamp travel per engine profile: **box / Azure SQL** →
  `FOR SYSTEM_TIME AS OF @__at` (a datetime2 param; requires a system-versioned temporal table); **Fabric
  Warehouse / Synapse** (`profile.IsWarehouse`) → the statement-level hint `OPTION (FOR TIMESTAMP AS OF
  '<literal>')` appended after WHERE/ORDER BY, which works on ANY table (no temporal setup). The Fabric literal
  is a fixed-format `yyyy-MM-ddTHH:mm:ss.fff` (OPTION takes no parameter, so it's inlined — no injection, it's a
  reformatted datetime) **truncated to milliseconds** (Fabric rejects ≥4 fractional digits, error 22440; UTC
  only). Each catalog table scan is its own server query, so the query-level OPTION hint is per-table-correct
  even across a join/union of different `AT` timestamps. `AT (VERSION => …)` (an Iceberg/Delta snapshot-id
  notion) has no SQL Server equivalent → a clean "not supported" error (no silent current-data result).
  Verified: `test/verify_time_travel.test` (14 — box temporal, current/future/past + a `dm_exec_query_stats`
  `FOR SYSTEM_TIME AS OF` proof + the VERSION error); Fabric Warehouse `OPTION (FOR TIMESTAMP AS OF)` validated
  **live** (point-in-time correct — a post-timestamp INSERT is invisible AS OF the earlier instant — and the
  ≥4-digit truncation confirmed, no 22440).
- **DML**: INSERT (+ INSERT…SELECT, + RETURNING via `OUTPUT INSERTED.*`), UPDATE, DELETE (rowid-based,
  parameterized), and **MERGE INTO** (every action kind; lowered to those same rowid operators — see the
  as-built entry in "Next up"). INSERT/CTAS/COPY use a **streaming bulk path** (see below).
  Not supported: `UPDATE … SET col = DEFAULT` (refused), `INSERT … ON CONFLICT` (refused at bind — no
  unique constraint is advertised), `MERGE … RETURNING`.
- **DDL**: CREATE/DROP TABLE, CREATE/DROP SCHEMA, ALTER TABLE (rename table/column, add/drop column,
  change type, SET/DROP NOT NULL, SET/DROP literal DEFAULT); PRIMARY KEY/UNIQUE/literal DEFAULT on CREATE.
  **On a warehouse profile (Fabric/Synapse) PK/UNIQUE are emitted as `NONCLUSTERED NOT ENFORCED` via a
  separate `ALTER TABLE ADD CONSTRAINT`** (inline-in-CREATE is rejected, error 24584); they're hints (not
  enforced) but appear in `sys.indexes`, so they seed rowid discovery → **UPDATE/DELETE work on Fabric**
  (validated 2026-06-24). Box keeps the inline form. See [docs/warehouse-support.md](docs/warehouse-support.md) §3.5.
  **`mssql_default_table_type`** (`''` rowstore | `clustered columnstore`/`cci`): on box/Azure SQL, CREATE/CTAS
  emit an inline `INDEX [cc_<schema>_<table>] CLUSTERED COLUMNSTORE` (PK/UNIQUE forced `NONCLUSTERED` — the
  columnstore is the clustered index); no-op on Fabric/Synapse (columnstore implicit). §3.6,
  `test/verify_columnstore.test`.
- **Transactions**: BEGIN/COMMIT/ROLLBACK with a pinned connection (lazy on first write); reads inside
  the txn use it too (read-your-writes); MARS forced so a scan reader + DML coexist. **Full design:
  [docs/transactions.md](docs/transactions.md)** (autocommit = implicit per-statement txn; the three
  lazy levels — DuckDB `BeginTransaction` always / extension `StartTransaction` on catalog touch / C#
  connection-pin on first write; MetaTransaction fan-out + one-writer rule; why MARS, and the exchange's
  deliberately MARS-free serialized connection; the `INSERT…SELECT` pin-timing race; per-row proc `_each`
  on DuckDB's pinned txn).
  - **Per-DuckDB-transaction connections (write concurrency, ABI v35) — DONE + validated.** The pinned
    connection is now **per `global_transaction_id`**, not a single shared one: C# keys connection state by a
    `ConcurrentDictionary<long, TxnState>`, and the active id rides a per-thread `AmbientTransaction` set by a
    new `set_active_txn(handle, txn_id)` ABI entry that the host calls immediately before each
    connection-using call (same thread, synchronous); `begin_bulk`'s old `autocommit` arg became `txn_id`
    (the bulk runs on a background thread so the id is captured + re-established by the consumer). C++ sources
    `MetaTransaction::Get(context).global_transaction_id` (`FabricatorTransaction::txn_id_` for lifecycle;
    `arrow_ingest` `ArrowStreamInitGlobal` centrally for all scans/read-your-writes; the DDL/DML/exchange/
    `FetchTableColumns`/`fabricator_exec` callsites via `catalog/fabricator_txn_util.hpp`'s `FabricatorSetActiveTxn`).
    So concurrent DuckDB transactions (e.g. **dbt `--threads N`** building several models at once) each get
    their OWN provider connection instead of colliding on one non-thread-safe `SqlConnection` (was error
    **595**). Matches the native `mssql-extension`'s per-`MSSQLTransaction` connection. **Validated: `dbt run
    --threads 4` PASS=4/4 on box (4×200k concurrent CTAS) AND Fabric (no MARS); `verify_*` 30/30.** Design +
    the abandoned Option A (dbt uses explicit txns, not autocommit — so an autocommit-detection fix never
    fired): [docs/transaction-concurrency.md](docs/transaction-concurrency.md). Harness:
    `dbt_mssql_test/` (gitignored — holds live SP creds, never commit). It has THREE targets: `box` (local SQL
    Server), `fabric` (Fabric **Warehouse** via the SQL endpoint), and `lakehouse` (Fabric **Lakehouse** via the
    **Delta** provider on OneLake — the `mssql` catalog is a Delta folder-catalog, not a SQL endpoint). The
    lakehouse target can't use dbt-duckdb's profile `attach:` (its renderer can't emit `READ_ONLY false`, which
    OneLake REQUIRES — DuckDB bumps a remote `abfss://` ATTACH to read-only under AUTOMATIC); instead a tiny
    dbt-duckdb **plugin** (`dbt_mssql_test/plugins/onelake_attach.py`) ATTACHes `mssql` writable in
    `configure_connection` (runs per connection, AFTER the profile `secrets:` create `fabric_sp` and BEFORE dbt's
    per-connection schema creation — so all of dbt's cursors see the catalog). Uses `TYPE mssql` (the loadable
    registers that storage-extension name; `fabricator` is a shell-only alias) + `PROVIDER 'delta'`. **CRITICAL —
    point it at an EMPTY lakehouse** (validated against the flat `LH_no_schema`, schema `main`): dbt runs
    `information_schema.tables` before building, which scans the **WHOLE `mssql` catalog** (the
    `WHERE table_schema=…` filters AFTER), and our catalog **materializes every table during enumeration**
    (`FetchTableColumns` → a `_delta_log` read per table over OneLake). Against the populated `LH` (10 tables incl.
    a 10M-row one) that effectively HANGS — even when the target schema is empty, because the scan still touches
    every other table. Against the empty `LH_no_schema` a single-model build is ~11s and **`dbt run --threads 4`
    is PASS=4/4** (4 concurrent CTAS → 4 separate `Tables/<model>` Delta tables, ~19s — validates the parallel
    OneLake bulk-write path, same as box/fabric). (**Lazy table-enumeration is INFEASIBLE** — investigated
    2026-07: DuckDB's `duckdb_tables`/`information_schema.tables` reads `GetColumns().LogicalColumnCount()` +
    the full `GetInfo()` CREATE SQL + `HasPrimaryKey()` from EVERY entry (`duckdb_tables.cpp:139/147/153`), and
    `duckdb_columns`/`information_schema.columns` share the same `SchemaEntry::Scan(TABLE_ENTRY)` path — so a
    full catalog scan inherently materializes every table's columns; there is no names-only enumeration API and
    a `TableCatalogEntry` requires its columns. Targeted access (`db.schema.t`) is already lazy/fast per-table.
    The realistic mitigation for the OneLake slowness is *cheaper* materialization: fetch a table's columns from
    the **OneLake Unity Catalog single-table GET** (`…/unity-catalog/tables/<full_name>` returns
    columns[name,type_name,nullable] — proven) instead of a heavy delta-rs `_delta_log` open, turning
    enumeration from N log-replays into N light REST calls. Not built — the bind schema would have to match the
    delta-rs read schema across all types.) Per-target
    schema via `+schema: "{{ target.schema }}"` (box/fabric `dbo`, lakehouse `main`). **The loadable extension
    must be rebuilt on an
    ABI bump** (`cmake --build … --target fabricator_loadable_extension`) — dbt loads the loadable, not the
    static `unittest`/`duckdb.exe`, so a stale loadable vs a freshly-published bridge throws
    `Bootstrap.Initialize returned 2` (ABI mismatch).
  - **dbt pre/post hooks — behavior + limitations: [docs/dbt-hooks.md](docs/dbt-hooks.md)** (validated box +
    Fabric). Highlights: an **in-transaction post-hook error rolls back the model's CREATE on BOTH box AND
    Fabric** (Fabric Warehouse supports transactional DDL rollback — unlike Snowflake). SQL-Server-specific
    DDL in a hook (index/PK/UNIQUE) must call `fabricator_exec`. A **default in-txn** post-hook touching the
    model via `fabricator_exec` now runs **atomically with the model** (ABI v36 join-only: the exec runs on the
    model's own pinned connection — box: model + index in ~0.3s; previously a 30s self-block). `transaction:
    false` still works (model commits first; non-atomic post-processing). Fabric **`CREATE INDEX` is
    unsupported** (`22424`) — a provider limitation no hook can avoid (the in-txn form then rolls the model
    back with it).
  - **FABRIC WAREHOUSE + dbt `table` models — WAS BROKEN, ROOT-CAUSED AND FIXED (2026-07-31):
  [docs/warehouse-support.md](docs/warehouse-support.md) §6.5.** Every dbt table model died at the swap with
  `15225: No item by the name of '[dbo].[<model>__dbt_tmp]' could be found`; box was fine, and a HOOKLESS control
  failed identically (so unrelated to the session-tag work). **Root cause: on Fabric a statement that ERRORS
  inside an explicit transaction ABORTS it — and we were issuing, inside the user's transaction, statements we
  KNEW fail there and then SWALLOWING the failure.** Two of them: `ProbeExternalTable`'s
  `sys.external_file_formats` (a **PolyBase** view, box-only; Fabric answers `15871 'external_file_formats' is
  not supported`), logged as the benign-looking "external-table probe failed … treated as not external"; and the
  `RowCount`/`ColumnNdv` stats DMVs (`dm_db_partition_stats`, `dm_db_stats_histogram`, also unsupported). The
  transaction was poisoned SILENTLY and the NEXT real statement failed confusingly (`15225`, or `208 Invalid
  object name` for a plain INSERT after a CREATE in the same txn). **Fix: both are capability-gated on
  `Profile.IsWarehouse` and never issued** — correct on the merits (a warehouse has no PolyBase external tables;
  stats are costing-only, never pruning) and one fewer round trip per table. Verified: the Fabric dbt target
  builds again (1 model, then 4 at `--threads 4`, plus a session-tag pre-hook model); box re-checked on the gated
  paths (polybase 252, cardinality 4, column_ndv 6, server_profile 15, with_options_mssql 9).
  - **THE STANDING RULE THIS ESTABLISHES: on a warehouse engine, never issue a statement whose failure you intend
    to swallow.** A best-effort probe is free on box and DESTRUCTIVE on Fabric. Capability-gate on `ServerProfile`
    instead of discovering support by try/catch.
  - **A WRONG intermediate conclusion is recorded in §6.5 on purpose.** An earlier pass blamed "a bulk load into a
    table created in the same transaction" because its tests varied TWO things at once (who issued the CREATE and
    bulk-vs-plain insert); holding the CREATE constant showed the bulk was irrelevant — a plain INSERT failed too,
    and a CREATE with NO insert failed as well.
  - Diagnostics added, and they are what made this findable: the bulk path's own DDL is now logged
    (`bulk ddl [txn=… own=…]` — previously only "bulk <table>: create=True", never the statement), and
    `ddl create`/`ddl alter` now carry the txn id + whether the connection was pinned. The decisive datum was
    `ddl create [txn=4 own=False]` followed by `exec [txn=4 own=False]` failing with 208 — same txn, same pinned
    connection, so a different-connection explanation was ruled out.
- **dbt incremental models — [docs/dbt-incremental.md](docs/dbt-incremental.md)** (validated box + Fabric).
    Concurrent **incremental append** (`incremental_strategy='append'`) works at `--threads 4`, and
    **concurrent schema evolution** (`on_schema_change='append_new_columns'` → `ALTER ADD COLUMN`) now works
    at `--threads 4` too (~0.5s/model). It **used to deadlock** at `--threads > 1`: our `ALTER` evicted the
    cached entry, so the next bind (in a different transaction, no pinned connection) re-fetched columns
    (`SELECT * FROM <model> WHERE 1=0`) on a **pooled** connection that blocked `LCK_M_IS` on the ALTER's
    still-uncommitted Sch-M lock → 30s timeout → re-eviction → "Table does not exist" (captured via
    `sys.dm_os_waiting_tasks`). **Fix (C++-only): `FabricatorSchemaEntry::Alter` re-fetches the columns
    EAGERLY on the model's OWN connection** (which owns the Sch-M lock → read-your-writes, no block) and
    caches them, so the later bind finds the entry cached and never issues the blocking pooled re-fetch.
    Since that cached entry reflects the uncommitted schema, **`RollbackTransaction` calls
    `FabricatorCatalog::InvalidateAllEntries()`** (drops materialized entries, keeps name lists for lazy
    re-fetch) so a rolled-back ALTER leaves no stale schema (verified). Same family as the post-hook
    join-only fix — keep in-transaction work on the transaction's own connection.
- **Functions**: `fabricator_query` (raw scan), `fabricator_exec` (raw exec) — both accept a connstr, a
  secret name, OR an attached-catalog name; `fabricator_refresh_cache`/`fabricator_invalidate_cache` (+ `_net_`
  aliases, arities 1/2/3); `fabricator_version()`; `fabricator_managed_dir()` / `fabricator_test_scan()` /
  `fabricator_server_info(catalog)` (diag — the latter surfaces the detected `ServerProfile`).
- **Cache invalidation after DDL via `fabricator_exec`**: DDL detection in C# (`SqlDdl.MayChangeSchema`),
  gated by `SET mssql_exec_invalidate_cache` (default false, Postgres-scanner parity). **Default off ⇒ after
  out-of-band DDL you must call `fabricator_refresh_cache(cat)` / `fabricator_invalidate_cache(cat[, regex])`
  yourself** (both are SCALAR functions — `SELECT fabricator_refresh_cache('db')`, NOT `CALL`). Prefer the
  scoped 2-arg invalidate when you know what you touched; the auto path runs a **full `RefreshCache`**.
  Three conditions must ALL hold for the automatic refresh to fire, and the third is the one that surprises
  (verified 2026-07-30): the setting is on, the SQL matches the heuristic, **and the first argument named an
  ATTACHED CATALOG** — with a raw connstr or a secret name we own no cache for it (`owns == true`), so nothing
  is refreshed and the call silently has no cache effect. Also note the detection is a plain **substring**
  match over `CREATE/DROP/ALTER/TRUNCATE/RENAME/EXEC`, so `UPDATE t SET created_at = …` contains `CREATE` and
  triggers a full re-discovery. Deliberate ("a false positive just refreshes") but NOT uniformly cheap: on a
  Delta/OneLake catalog re-discovery is the expensive glob, not a metadata query. The setting is `mssql_`-
  prefixed while the mechanism is provider-agnostic (`SqlDdl` lives in the Bridge, consulted on every
  `ExecuteDml`).

Compat suite: ~96/122 of the C++ mssql-extension tests pass (corpus regenerated from upstream via
`scripts/gen_mssqlcompat_tests.sh`, lives in `test/mssqlcompat/`, gitignored). Remaining failures are
non-data: error-WORDING/number assertions (corpus expects native-extension text), C++-only surfaces
(`mssql_pool_stats`/`mssql_open` diagnostics, krb5 connstr parser), COPY-to-temp-table empty-schema
syntax, and catalog-after-rollback staleness.

**Not yet / out of scope:**
**load-time global** functions
(Phase 3 — scalar UDFs, TVFs, stored procs + custom C#-authored scalar, table, table-in-out & aggregate
functions + discovered-TVF & per-row-proc table-in-out + the OperatorFinalize cleanup signal all done, see
"Callable scalar UDFs (4b)" / "table functions (4c)" / "stored procedures (4d)" / "custom functions (4e scalar,
4f table)" / "table-in-out (4g — incl. custom C# in-out, per-row procs, OperatorFinalize)" / "aggregate
functions (4h — custom C# UDAF, GROUP BY + parallel + window)"; proc
multi-result-set + INPUT/OUTPUT + OUTPUT-param-only `_each` still deferred; a custom aggregate `window`
callback is deliberately NOT implemented (DuckDB's segment-tree path drives our combine/finalize — cheaper for
a marshaled bridge); aggregate disk-spill is **opt-in** per aggregate (`SupportsSpill` → bytes-in-blob, 1 KB
state cap; default is fast in-C# state, no spill) — `serialize`/`deserialize` for variable/unbounded state and
distributed-plan serialization stay deferred;
load-time global deferred in
favor of attach-time custom functions); connection
pooling knobs / `mssql_pool_stats` (ADO.NET pools by connstr already); COPY to temp tables
(`mssql://cat//#t`, `cat..#t` — `ParseTarget` only accepts strict 3-part names); CHECK constraints +
non-literal/expression DEFAULTs on CREATE; UPDATE/DELETE…RETURNING; length-aware VARCHAR mapping (so
string columns can be PK/UNIQUE keys); bespoke `authenticator=krb5` connstr parsing (see constraints).

## Streaming bulk write (INSERT / CTAS / COPY)

INSERT, CTAS and COPY stream record batches to the provider instead of buffering the whole dataset
(bounded memory for warehouse-scale writes). The concurrency lives in C#:

- **ABI v16** entries: `begin_bulk(handle, schema, table, create, replace, check_constraints,
  ArrowSchema*, out_session)` + `push_batch(session, ArrowArray*)` + `complete_bulk(session, abort,
  *affected)`.
- C#: `BulkSession` = a bounded `Channel<RecordBatch>` (capacity 8) + a `Task.Run` consumer that calls
  the existing `catalog.BulkInsert(... ChannelArrowStream ...)` (so all SqlBulkCopy / CREATE /
  KeepIdentity / transaction logic is reused). `push_batch` blocks for backpressure; the consumer's
  `finally` completes+drains the channel so a fault never deadlocks the producer; the real error
  surfaces from `complete_bulk`. `abort` faults the channel so an in-flight load rolls back.
- C++: each operator begins the session at init, pushes per sink chunk, completes at finalize (+ a
  gstate destructor that aborts on early failure). INSERT…RETURNING is unchanged (small result; still
  buffered via the producer).
- **`check_constraints`**: INSERT passes **true** → `SqlBulkCopyOptions.CheckConstraints` (so a
  constraint-violating INSERT fails like a classic INSERT — SqlBulkCopy skips CHECK/FK by default).
  CTAS/COPY pass **false** (bulk-load speed). NOT NULL is still caught client-side by SqlBulkCopy.
- The legacy `bulk_insert` ABI entry + its `clr_host` wrapper are now unused by C++ (left in place).
- **ABI v17–v19** entries: `open_catalog(provider, conn, …)` (v17); `build_connection_string(provider,
  fields_json, …)` (v18); and the **scalar-function trio** (v19): `get_function_param_schema(handle,
  schema, func, out)` + `get_function_return_schema(…)` (each fills a bare `ArrowSchema *out` giving the
  arg/return `LogicalType`s, read via `ReadArrowSchema` — was a zero-row stream until **v32**) +
  `execute_scalar(handle, schema, func, args, out)` (runs the UDF over an N-row arg batch; consumes `args`).
- **ABI v20/v21** entries (table functions): `get_function_output_schema(handle, schema, func, out)`
  (a bare `ArrowSchema` = the TVF's output columns, **v32**) + `execute_table(handle, schema, func, args, spec_json,
  filter_values, out)` (`args` = 1-row batch of the constant call args; `spec_json`+`filter_values` carry
  projection + best-effort filter pushdown exactly like `scan_table`; `out` = the result rows). The
  `spec_json`/`filter_values` params were added at **v21**.
- **ABI v22** entry (stored procs): `execute_proc(handle, schema, func, args, out)` — runs `EXEC [s].[p]
  @p0,…` over the 1-row positional args, `out` = the proc's first result set. No `spec_json` (a proc's EXEC
  isn't inline-wrappable → no pushdown). Procs reuse `get_function_param_schema` (input params) +
  `get_function_output_schema` (which auto-detects proc vs TVF — `sp_describe` vs `ROUTINE_COLUMNS`).
- **ABI v23/v24** entries (table-in-out, 4g): `inout_open(handle, schema, func, input_schema, isolation,
  *out_session)` (input table columns = the TVF's positional params; managed side consumes the schema;
  `isolation` added at v24 — the session opens ONE transaction at that SQL isolation level so all its
  per-chunk queries share a consistent view; from `SET mssql_isolation_level` ?? the ATTACH `isolation_level`
  option) + `inout_push(session, in_chunk, out)` (runs that chunk's CROSS APPLY synchronously; `out` = its
  full output) + `inout_finish(session, out)` (commit; `out` empty in the synchronous model) +
  `inout_abort(session)` (rollback + frees the GCHandle; idempotent). `inout_abort` (not `inout_finish`)
  frees the handle, so the C++ holder destructor always calls it. See "Callable table-in-out (4g)" below.
- **ABI v25** entries (custom aggregates, 4h): `agg_open(handle, schema, func, *out_session)` (opens a managed
  session = a `ConcurrentDictionary<id, accumulator>`; closed via `agg_close`) + `agg_update(session, batch)`
  (`batch` = `[int64 state_id ++ params]`, N rows; C# groups by id + folds each group) + `agg_combine(session,
  batch)` (`batch` = `[int64 target_id, int64 source_id]`; merges source→target per row) + `agg_finalize(session,
  ids, out)` (`ids` = `[int64 state_id]`; `out` = one result column in id order) + `agg_destroy(session, ids)`
  (drops those states — bounds memory for the window paths; best-effort) + `agg_close(session)` (frees the
  session; best-effort). Arg/return schemas reuse `get_function_param_schema`/`get_function_return_schema`. See
  "Callable aggregate functions (4h)" below.
- **ABI v26** entries (spillable aggregates, 4h opt-in): `agg_update_spill(session, group_states, batch, out)`
  (`group_states` = BLOB[G] current state per distinct group, `batch` = `[int64 slot ++ params]`; `out` =
  BLOB[G] new state) + `agg_combine_spill(session, target_states, batch, out)` (`target_states` = BLOB[G]
  distinct targets, `batch` = `[int64 slot, BLOB source]` — a target may repeat, e.g. the window segment-tree
  merges several nodes into one frame state; `out` = BLOB[G] merged) + `agg_finalize_spill(session, states,
  out)` (`states` = BLOB[N]; `out` = one result column). For a spillable aggregate the per-group state is
  serialized into a fixed, pointer-free state blob (`[uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP]]`, cap =
  1 KB) so DuckDB's external GROUP BY spills it; state crosses as an Arrow BLOB column (NULL row = fresh).

### Shipped function machinery (4b–4h + Phases 5–6) — as-built records moved

Scalar UDFs (4b), TVFs (4c), the Bind/Binding refactor + table session (Phase 5, v27/v29/v30/v32),
stored procs incl. named/OUTPUT params (4d), custom C# scalar/table (4e/4f), table-in-out incl. the
retired push model + per-row procs + OperatorFinalize (4g), the streaming exchange (Phase 6, v28/v31),
and custom aggregates incl. opt-in spill + holistic (4h, v25/v26). All DONE and verified; the design
contracts (which paths push down, session lifetimes, the state-vectorized aggregate rules) are in
[docs/feature-history.md](docs/feature-history.md) §Function machinery +
[docs/custom-functions-design.md](docs/custom-functions-design.md).

- **Filtering**: discovered scalar UDFs + TVFs/procs are gated by the ATTACH `schema_filter` (icase
  `std::regex`, applied in `LoadCatalog`/`RefreshCache`); `table_filter` is table-only and does NOT apply to functions.
- **Parallel partitioned reads** (ConnectorX-style `partition_on`/`partition_num`) — **design note, deferred,
  nothing built**: [docs/parallel-partitioned-read.md](docs/parallel-partitioned-read.md). Two wins to keep
  distinct — parallel *fetch* (form A: C# runs N range queries concurrently + `ParallelMerge` → the existing
  single-stream scan, no ABI) vs parallel DuckDB *pipeline/core usage* (form B: N streams → N scan threads via
  a parallel multi-stream scan = the native form of the proven `UNION ALL` core-saturation trick; bigger). On
  `fabricator_query` the two surface as optional NAMED params (the `daxeval` pattern); a custom
  `IArrowTableFunction` could return `IAsyncEnumerable<IAsyncEnumerable<RecordBatch>>` (outer = partitions).
- **`function_filter` ATTACH option + scoped `fabricator_invalidate_cache(catalog, regex)` + the
  filters-are-enumeration-only lift — ALL DONE (2026-07-15).** A *_filter bounds DISCOVERY, not
  targeted-by-name access. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
## C ABI contract (`src/include/fabricator/abi.h`)

- The managed `Bootstrap.Initialize` fills an `FabricatorVTable` of C function pointers; tabular results
  flow through caller-allocated `ArrowArrayStream`; errors = status code + owned UTF-8 string freed via
  `free_error`. C# error messages prepend the provider error number when available (`FormatError`
  duck-types an `int Number` property → e.g. `"2627: …"`; provider-agnostic, no SqlClient ref in Bridge).
- **`COPY … TO '<path>/<table>' (FORMAT delta, …)` — DONE: path-targeted Delta write, NO ATTACH**
  (transient per-execution catalog; `MODE` = the Spark/delta-rs save-mode vocabulary incl.
  `overwrite_partitions`; repartition-on-overwrite; PARTITION_COLUMNS with every mode; its own atomic
  commit — deliberately NOT rolled back by a surrounding BEGIN). verify_delta_copy_format 109. Full as-built record (moved verbatim from here): [docs/feature-history.md](docs/feature-history.md).
- **Current version: ABI v68** (v68 = **`generate_table_sql`** — ONE appended entry backing
  **SQL-GENERATING table functions**: `generate_table_sql(handle, schema, func, catalog_name, args,
  out_sql)` returns the SQL that REPLACES a function call at bind time (DuckDB's `bind_replace`, the
  `query_table()` mechanism). `handle == 0` = the global registry (schema/catalog_name empty), non-zero =
  the catalog's, with `catalog_name` = the DuckDB **ATTACH alias** (only the host knows it, so a
  catalog-bound generator can qualify references back into its own catalog). `args` = the 1-row constant-arg
  batch (positional ++ supplied named, by field name; nullable). **BIND-time only, possibly repeated**
  (EXPLAIN / DESCRIBE / a view re-bind) ⇒ generators must be deterministic + side-effect-free; NO data path.
  Same pass, NO extra entry: `get_function_param_schema` now carries POSITIONAL ++ NAMED parameters in one
  schema, the named ones tagged `fabricator.named="1"` in FIELD metadata (`FetchFunctionParamSchema`'s new
  optional `out_named` — the `fabricator.volatile` channel/shape). See the sqlgen bullet in "Next up".)
- **Prior versions v16–v67: [docs/abi-history.md](docs/abi-history.md)** — the full per-version records,
  moved verbatim from here (incl. the cancellation tiers v65/v66, the native_read/MultiFileReader saga +
  rowid/late-materialization/row-tracking-virtual work under v57, the onelake:// filesystem v55–v64, the
  Delta catalog/DML/column-mapping/native-write records under v47–v49, and the BINARY STATUS notes).
  Read it when touching an existing ABI entry or wondering why one has its shape.
  **Bump rule:** when you add a vtable entry OR change a signature, bump
  **BOTH** `FABRICATOR_ABI_VERSION` in `abi.h` AND `vtable->AbiVersion = N` in `Bootstrap.Initialize`,
  else the host throws "ABI version mismatch". Adding an *enum value* (e.g. a new metadata/alter kind)
  is additive and needs NO bump.
- Ownership: the managed side **consumes/releases** every `ArrowArrayStream`/`ArrowSchema`/`ArrowArray`
  passed in (the C++ caller never releases them; a rare failure leaks rather than double-frees).

## Build & test

### Build from a fresh clone (Windows) — the quickstart

The detail bullets below explain the *why* of each step + the gotchas; this is the from-zero sequence.
`<repo>` = the checkout root (`D:\repos\fabricator-extension`). Run every cmake/ninja command **inside a
VS 18 vcvars64 shell** (see the VS-dev-env bullet — VS 2022 fails at link).

**Prerequisites** (install first):
- **Visual Studio 18** (or its Build Tools) with the C++ workload — the toolset the build links against.
- **.NET SDK 10** (the managed projects target `net10.0;net8.0`; `publish-managed.ps1` needs the 10 SDK).
- **CMake ≥ 3.21 + Ninja** (the generator).
- **vcpkg** — bootstrapped, with `VCPKG_ROOT` set (supplies OpenSSL + curl for the statically-linked `httpfs`).
- **PowerShell 7 (`pwsh`)** — runs the managed publish script.

**Steps:**
1. **Dependencies.** FOUR git submodules: `duckdb` + `extension-ci-tools` (the DuckDB source + build
   tooling, both `shallow = true`), `engineered-wood` (the Delta engine), and `DuckDB.ExtensionKit`
   (MIT, needed ONLY to build the single-file distribution — the normal build never touches it).
   **Init NON-recursively** — `--recursive` would drag in engineered-wood's nested `parquet-testing`
   corpus (~½ GB of test data the build does not need; EW's own corpus-dependent Parquet.Tests then
   fail, which is expected):
   ```
   git submodule update --init          # NOT --recursive
   ```
2. **vcpkg deps** (once): `vcpkg install openssl:x64-windows-static curl:x64-windows-static`
3. **Configure** (first time; ONE command WITH the vcpkg toolchain — httpfs is linked unconditionally so
   these flags are mandatory, not optional):
   ```
   cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 -DDUCKDB_EXTENSION_CONFIGS=<repo>/extension_config.cmake ^
     -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 -DENABLE_EXTENSION_AUTOLOADING=1 ^
     -DENABLE_EXTENSION_AUTOINSTALL=1 -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release ^
     -DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%/scripts/buildsystems/vcpkg.cmake ^
     -DVCPKG_TARGET_TRIPLET=x64-windows-static ^
     -S <repo>/duckdb -B <repo>/build/release
   ```
4. **Build the C++** (targets → binaries detailed below):
   `cmake --build <repo>/build/release --target unittest shell fabricator_loadable_extension`
5. **Publish the managed bridge**: `pwsh scripts/publish-managed.ps1` (lands in
   `build/release/extension/fabricator/fabricator/`).
6. **Run**: set `FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator` before running
   `duckdb.exe`/`unittest.exe` directly (see the managed-dir gotcha). Iteration: a C#-only change needs
   only step 5; a C++ change needs step 4 for the target you'll run (the stale-embedded-copy trap below).

### Reference (the why + gotchas)

- **Target DuckDB v1.5.5** (since 2026-07-22; new extension API: `Extension::Load(ExtensionLoader&)` +
  `loader.RegisterFunction(...)` + `DUCKDB_CPP_EXTENSION_ENTRY(fabricator, loader)`). `duckdb` +
  `extension-ci-tools` are **git submodules** (converted 2026-07-25 — previously gitignored manual
  clones whose shas lived only in this prose, which had already drifted: the tooling pin said v1.5.3
  while upstream had v1.5.4 AND v1.5.5 branches, and it pointed at a moving BRANCH TIP rather than a
  sha. A submodule makes the pin a reviewable diff line and gives CI one deterministic bootstrap).
  Pinned to `duckdb@d8cdaa33` (the v1.5.5 tag) + `extension-ci-tools@72e76e99` (its v1.5.5 branch —
  by convention the tooling version matches the DuckDB version; upstream branches it per patch
  release while duckdb itself branches per LINE, `v1.5-variegata`). Both carry `shallow = true`, so
  `git describe` still has no tag context and the build still needs `-DOVERRIDE_GIT_DESCRIBE`.
  Neither has a `branch =` line ON PURPOSE — `git submodule update --remote` would jump the pin to an
  unreleased tip. Bump duckdb via
  `git -C duckdb fetch --depth 1 origin <sha> && git -C duckdb checkout <sha>` then `git add duckdb`
  (a version bump also
  means: re-run cmake with `-DOVERRIDE_GIT_DESCRIBE=v<new>`, match the out-of-tree httpfs pin in
  `extension_config.cmake` to the sha in duckdb's `.github/config/extensions/httpfs.cmake`, and
  `pip install duckdb==<new>` in the dbt/notebook envs — the official wheel rejects a loadable whose
  declared version differs). **1.5.5 verification (2026-07-22):** C++ compiled unchanged, full delta
  sweep + SQL function suites + s3 161/polybase 252 green on the new httpfs sha (`827222fb`).
  **DuckDB's variant limitations are all UNFIXED in 1.5.5** (source-diffed + runtime-probed): the
  `ArrowAppender::FinalizeChild` nested-extension crash (why the transport is a leaf blob), the
  parquet writer's non-root-VARIANT rejection (why nested variant is gated), and `variant_extract`
  returning NULL (dot access stays the way). 1.5.5 DOES fix an FLBA-decimal `RETURN_STATS` min/max
  unification bug (big-endian stats compared as little-endian across row groups) — our native-write
  Delta stats for precision>18 decimals in multi-row-group files are correct-by-upstream now.
- **engineered-wood is an in-tree git submodule** (`engineered-wood/` at the repo root, since
  2026-07-19; was a `D:\repos\engineered-wood` sibling ProjectReference). Pinned to the
  **`fabricator-patches` branch on the `cmettler/engineered-wood` fork** = **clast-project master
  (`e48f449`) + our small additive patch set** (see "THE EW CLAST-MASTER RE-PIN" near the top;
  `.gitmodules` `branch = fabricator-patches`; `upstream` remote = clast-project/engineered-wood).
  `Fabricator.Bridge.csproj` references `..\..\engineered-wood\src\EngineeredWood.DeltaLake.Table\…`.
  **Init NON-recursively** — `git submodule update --init engineered-wood` — to skip EW's nested
  `parquet-testing` corpus (its test data, ~half a GB, not needed to build; note EW's own
  Parquet.Tests corpus-dependent tests fail without it — expected). **Workflow:** EW dev happens
  INSIDE the submodule working tree on `fabricator-patches`; the build uses the working tree, so
  day-to-day edits/commits there don't touch the parent's pin. Keep every EW change as an ADDITIVE,
  upstreamable commit on `fabricator-patches` (never fork-style divergence); to take a new upstream
  EW, merge `upstream/main` into `fabricator-patches` (`master` is the STALE pre-rename name, and its
  remote-tracking ref still resolves), re-run the delta sweep, push, re-pin. To
  RECORD a known-good EW version in fabricator: push EW to the fork FIRST (the pin must be
  fetchable — pushes still only on the user's explicit authorization), THEN bump the pointer
  (`git add engineered-wood && git commit`). (The old `D:\repos\engineered-wood` sibling is
  redundant; the scratchpad spike csprojs still point at it but scratchpad is gitignored.)
- **DuckDB.ExtensionKit is an in-tree git submodule too** (`DuckDB.ExtensionKit/` at the root, since
  2026-07-25; was a `D:\repos\DuckDB.ExtensionKit` absolute ProjectReference). MIT, upstream
  `Giorgi/DuckDB.ExtensionKit`, **pinned by SHA (`882f080`) with no `branch =` line** — deliberately
  NOT floating: the AOT shell depends on internals of the kit's `DuckDBExtApiV1` mirror, so an
  unpinned bump could silently change the ABI surface. **It is NOT on NuGet** (checked: the
  flat-container id 404s and a search returns 0 hits), so a `PackageReference` is not an option today;
  a submodule also keeps it patchable, which matters because two upstream-candidate issues are already
  known (the `duckdb_result` out-param typed as `nint*`, and `duckdb_fetch_chunk` typed as taking a
  pointer when the C API takes the struct BY VALUE — see the distribution bullet's §15/§16 findings).
  Only `dotnet/Fabricator.Installer` references it (`$(MSBuildThisFileDirectory)..\..\DuckDB.ExtensionKit`,
  overridable via `-p:DuckDBExtensionKitPath=`), and nothing else in the repo builds that project — so a
  missing submodule cannot break the normal build; the csproj errors with the exact `git submodule`
  command instead. Switching a build between Windows and WSL over the SAME working tree makes the kit's
  `obj/` restore for the other OS: the first cross-OS build can fail once in `ResolvePackageAssets`, and
  simply re-running it succeeds.
- **Windows build needs the VS dev env** — a plain shell fails at *compile* with `Cannot open include
  file: 'stdint.h'`. **Use the VS 18 vcvars, NOT VS 2022:**
  `C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat`. The build is
  configured against the VS 18 toolset (`…/VC/Tools/MSVC/14.50.35717`, see `CMAKE_CXX_COMPILER` in
  `build/release/CMakeCache.txt`); linking with an older toolset (VS 2022 = `14.44.x`) **fails at link**
  with `unresolved external symbol __std_find_first_not_of_trivial_pos_1` / `__std_rotate` /
  `__std_unique_1` — newer STL vectorized-algorithm intrinsics that `duckdb_static.lib` references but the
  older vcruntime lacks. Run every cmake/ninja command inside that vcvars shell, e.g.
  `cmd /c '"…\18\…\vcvars64.bat" && cmake --build build/release --target <target>'`.
  **⚠ That one-liner does NOT survive Git Bash quoting** (verified 2026-07-28, three variants failed: `cmd //c`
  with escaped inner quotes → *"is not recognized as an internal or external command"*; a bare relative
  `build_cpp.bat` → not found; and a `>nul` redirect inside → *"The system cannot find the path specified"*,
  which looks like a MISSING VS install and is not). What works reliably: write a two-line `.bat`
  (`call "…vcvars64.bat"` then the `cmake --build`) and invoke it by ABSOLUTE path — from PowerShell,
  `cmd /c "D:\...\build_cpp.bat"`. A copy lives in the session scratchpad. **The tell that you did not actually
  rebuild is the binary's mtime** — check `ls -l build/release/test/unittest.exe` rather than trusting exit 0,
  since a failed `cmd` line still exits 0 through the pipe.
- **Targets → binaries** (`EXTENSION_STATIC_BUILD=1` ⇒ the extension is statically embedded in BOTH exes
  *and* built loadable):
  - `shell` → `build/release/duckdb.exe` (interactive shell; **embeds** the extension).
  - `unittest` → `build/release/test/unittest.exe` (runs the `.test` suites; **embeds** the extension).
  - `fabricator_loadable_extension` → `build/release/extension/fabricator/fabricator.duckdb_extension`
    (the loadable; needed to `LOAD` into a duckdb that does NOT embed it — e.g. the **official `duckdb==1.5.5`
    Python wheel** for the dbt-duckdb concurrency tests). **To load into the official wheel, reconfigure with
    `-DOVERRIDE_GIT_DESCRIBE=v1.5.5`** so the extension footer declares `duckdb_version=v1.5.5` — the shallow
    clone has no git tag context, so it otherwise defaults to `v0.0.1` and the official engine rejects it on
    the version check (NOT bypassed by `allow_unsigned_extensions`). The wheel version MUST match the declared
    version — after the 1.5.5 bump, dbt venvs / notebook flows still on `duckdb==1.5.4` reject the new
    loadable until they `pip install duckdb==1.5.5`. Then `LOAD` with `allow_unsigned_extensions`
    + set `FABRICATOR_MANAGED_DIR` (the bridge isn't next to the python `.pyd`). Verified loads + ATTACH +
    query against the official wheel. (This also fixes `json`/`icu` autoload, though we embed those.)
  - `cmake --build build/release` (no `--target`) builds all of them.
  - **After changing C++ extension code, rebuild the target whose binary you'll run.** Building only
    `fabricator_loadable_extension` then running `duckdb.exe`/`unittest.exe` runs the STALE embedded copy
    (a `LOAD '<path>'` is then a no-op). This is the #1 "my change didn't take" trap.
- Full configure (first time), run inside vcvars64:
  `cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 -DDUCKDB_EXTENSION_CONFIGS=<repo>/extension_config.cmake
  -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 -DENABLE_EXTENSION_AUTOLOADING=1
  -DENABLE_EXTENSION_AUTOINSTALL=1 -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release
  -S <repo>/duckdb -B <repo>/build/release`. `EXTENSION_VERSION "0.0.1"` is set in
  `extension_config.cmake` (the repo has commits now, but keep it — avoids relying on `git describe`).
- **Managed publish:** `pwsh scripts/publish-managed.ps1` → publishes `Fabricator.SqlServer` (+ Bridge +
  self-contained .NET 10 runtime) into `build/release/extension/fabricator/fabricator/`. A C#-only change
  needs only a republish (no C++ rebuild) unless an ABI signature changed.
- **TWO DEPLOYMENT MODES + PROVIDED-RUNTIME hosting (2026-07-12; Windows + Linux validated, Fabric live).**
  All extension projects multi-target **`net10.0;net8.0`** (`dotnet/Directory.Build.props`; EW already did)
  with `RollForward=LatestMajor`. `publish-managed.ps1 -Mode Framework [-Rid linux-x64]` produces a
  **framework-dependent** payload (~35 MB win / ~25 MB zipped linux vs ~250 MB self-contained; net8.0 +
  rollForward ⇒ ONE payload runs on .NET 8 AND 10+). `clr_host` detects the layout by **hostfxr's presence
  in the managed dir** (self-contained carries it): absent ⇒ resolve a PROVIDED .NET install —
  **`FABRICATOR_DOTNET_ROOT` > `DOTNET_ROOT` > platform defaults** (win `%ProgramFiles%\dotnet`; linux
  `/etc/dotnet/install_location`, `/usr/share/dotnet`, `/usr/lib/dotnet`; mac `/usr/local/share/dotnet`) —
  load `<root>/host/fxr/<highest>/hostfxr` and pass the root via `hostfxr_initialize_parameters.dotnet_root`
  (NO env mutation; `host_path=null` = current process). **Gotcha found: a dotnet_root with FORWARD slashes
  fails at CreateCoreCLR with a cryptic E_INVALIDARG** (framework resolution tolerates them) — clr_host
  normalizes on Windows. Validated: FDD on the global install (rolls to newest), full suites on a
  net8-ONLY private root via `FABRICATOR_DOTNET_ROOT` (the "local .NET 10 beside global .NET 8" selector,
  inverted), `DOTNET_ROLL_FORWARD` respected, SC unchanged (the publish script CLEANS the output dir on a
  mode change — a stale hostfxr would flip the detection).
- **⚠ SHIPPING A LINUX BUILD TO FABRIC BY HAND — THREE TRAPS, each hit for real on 2026-08-01, each costing
  a Spark session. All three produce an artifact that looks fine locally and fails only on the far side.**
  Scripts: `scratchpad/linux_sync_build.sh` (sync + build), `strip_linux.sh` (strip + footer + verify),
  `glibc_check.sh`. **The through-line: derive every value from a KNOWN-GOOD ARTIFACT, never from reasoning
  about what it should be, and verify the EFFECT rather than the tool's own success message.**
  1. **`strip` DESTROYS the extension.** A loadable is an ELF with DuckDB's metadata footer appended AFTER
     the image; `strip` rewrites the file and discards it. The ELF stays valid, nothing local complains, and
     the only symptom is at LOAD: *"The file is not a DuckDB extension. The metadata at the end of the file
     is invalid"*. Re-append with `extension-ci-tools/scripts/append_extension_metadata.py`. Worth doing —
     697 MB → 28 MB.
  2. **Its `--abi-type` DEFAULT (`C_STRUCT`) IS WRONG FOR US.** We use `DUCKDB_CPP_EXTENSION_ENTRY`, so it
     must be `CPP`. Under `C_STRUCT` the `duckdb_version` field means the **C API** version, so encoding
     `v1.5.5` yields *"built for DuckDB C API version 'v1.5.5', but we can only load ... 'v1.2.0' and
     lower"* — an error that reads like a DuckDB version problem and is not one. Read the fields off a
     shipped artifact instead: `tail -c 512 … ` gives e.g. `CPP | 0.0.2 | v1.5.5 | windows_amd64 | 4`. **That
     also catches the extension version** — the stale linux tree encoded `0.0.1` against `0.0.2` at the time.
     Read the CURRENT version off `CMakeLists.txt` rather than this line (it is `0.0.3` as of 2026-08-03);
     the point is to compare, not the literal. Check ALL FOUR fields after re-appending; a `C_STRUCT` footer contains the same version
     strings, so grepping for "some expected strings" passes while the artifact is unloadable.
  3. **`publish-managed.ps1` reported `Framework, net8.0, linux-x64` while the output dir still held a
     WINDOWS self-contained payload** (`hostfxr.dll`, `coreclr.dll`, `createdump.exe`). Uploaded to Linux it
     surfaces as `Bootstrap.Initialize (0x80070057)` — an error pointing at CoreCLR hosting, not at "wrong
     OS". Found by DIFFING against the known-good payload (313 files vs 178). **Verify by content**: no
     Windows PE, and a file count matching the reference.
  - **glibc: Fabric compute is Azure Linux 3.0, `ldd 2.38`, max exported `GLIBC_2.38` (measured on the
    compute).** Building on Ubuntu 24.04 (glibc 2.39) is FINE — symbols bind to the oldest version that
    provides them, and our build comes out needing exactly 2.38 — but that is a **zero-margin** result, so
    one future dependency pulling a 2.39 symbol breaks Fabric loading with no other symptom. `glibc_check.sh`
    gates it. (The older note below that 2.35 "runs on" Azure Linux 3 says 2.35 is SAFE; it does not mean
    anything higher fails, and reading it that way sent this session down a wrong path.)
- **LINUX (linux_amd64) BUILDS + FULL SUITES GREEN (WSL Ubuntu 22.04→24.04, gcc 11→13.3 — the toolchain and
  `~/vcpkg` + the `~/sqlext` copied build tree SURVIVED the distro upgrade; `VCPKG_ROOT` is simply unset in a
  non-login shell, which is not the same as vcpkg being absent).** Build = same configure as Windows minus vcvars, plus
  `-DOVERRIDE_GIT_DESCRIBE=v1.5.4` (no .git in the copied tree) + vcpkg toolchain with `x64-linux`
  (openssl+curl for httpfs); the C++ compiled with ZERO changes (the clr_host ifdefs held). Suites on
  linux + the apt `dotnet-runtime-8.0` (auto-probed at `/usr/lib/dotnet`, no env var): delta transactions
  596 / txn_version 51 / SQL Server-over-docker scalar 26 + custom 89 / **S3-MinIO 131** / copy_format 96.
  **CROSS-PLATFORM BUG found by the first Linux run: EW `ListVersionsAsync` returned commit versions in RAW
  DIRECTORY-LISTING order** — Windows/S3/ADLS list sorted, but Linux readdir returns inode-hash order, and
  the callers assume ascending replay (SnapshotBuilder's latest-wins metadata/protocol, timestamp
  resolution's monotonic early-break, the history view). Symptom: the per-txn snapshot pin resolved "now"
  to v0 → an in-transaction DELETE scanned an empty snapshot and silently deleted nothing. Fixed at the
  source (materialize + sort ascending; the log dir is bounded by the checkpoint interval).
- **FABRIC NOTEBOOK VALIDATED LIVE (2026-07-12, Livy pyspark on workspace `Test`/`LH`):** the Fabric
  compute is **Azure Linux 3** (`6.6.141.1-1.azl3`) with **dotnet preinstalled at `/usr/share/dotnet`,
  .NET 8.0.28 ONLY, no DOTNET_ROOT set** — our default probe finds it with ZERO configuration. Flow:
  upload `fabricator.duckdb_extension` (linux_amd64) + the zipped FDD payload to the lakehouse
  `Files/fabricator_ext/` (OneLake DFS), then in the session: `pip install --force-reinstall duckdb==1.5.5` (must match the loadable's declared version)
  (never import duckdb in the kernel before the pip — read the preinstalled version via
  `importlib.metadata`; the duckdb work runs in a SUBPROCESS interpreter, which also isolates a crash from
  the kernel), stage to /tmp, `FABRICATOR_MANAGED_DIR` + `load_extension` → `fabricator_version()` works,
  delta CTAS + explicit transaction correct. Driver: `scratchpad/fabricnb` (gitignored; reads the SP from
  dax_secret.sql) — `dotnet run livy` = the Spark-session path; raw Livy sessions have NO
  `/lakehouse/default` fuse mount — the probe stages via `spark.sparkContext.binaryFiles(abfss://…)` there.
  **The TRUE PYTHON-NOTEBOOK path is ALSO validated (RunNotebook job, 75 s):** the notebook session runs
  Azure Linux 3 + dotnet 8.0.27 at `/usr/share/dotnet` (only runtime, no DOTNET_ROOT) and — unlike the Livy
  session — HAS the fuse mount AND a **preinstalled duckdb 1.2.2**; `pip install --force-reinstall
  duckdb==1.5.5` overrides it (works without a kernel restart BECAUSE duckdb is never imported in the
  kernel), the extension loads on the preinstalled .NET 8, the delta transaction smoke passes, and a Delta
  table written through the fuse mount (`ATTACH '/lakehouse/default/Files/…'`) reads back. Fabric-API
  gotchas hit on the way: **Notebook-item CREATION is not SP-enabled on this tenant** (`403
  FeatureNotAvailable`, bare create too — the notebook must be created interactively ONCE; the SP-driven
  `updateDefinition` + `RunNotebook` then work), `updateDefinition?updateMetadata=true` requires a
  `.platform` part (omit the flag — the default-lakehouse binding rides in the ipynb metadata), and the
  portal can save a display name with a TRAILING SPACE (`'fabricator_ext_probe '`) — resolve by trimmed
  comparison. `dotnet run run` = update+run the existing notebook; `upload` = refresh the OneLake
  distribution (`LH/Files/fabricator_ext/`). **The MANAGED Tables area works through the fuse mount** —
  `ATTACH '/lakehouse/default/Tables' (TYPE fabricator, PROVIDER 'delta', schemas true)`: credential-free
  read + CREATE + explicit-txn append on `tlake.dbo.*`, all sub-second per op.
  - **⚠ THE "single-writer only" CAVEAT THAT USED TO SIT HERE WAS WRONG, AND IT WAS AN INFERENCE — MEASURED
    AND CORRECTED 2026-08-01.** It read: *"the commit's O_EXCL put-if-absent is doubtful over fuse —
    concurrent writers should use abfss/onelake"*, i.e. it warned about SILENT LOST COMMITS on the path a
    notebook user reaches for BY DEFAULT. **Three live runs, 16 writers × 20 single-row commits each: 960
    attempted commits, 249 REAL collisions, ZERO lost writes** — every `(w,c)` group complete, versions
    unique and contiguous, every run. The fuse `O_EXCL` put-if-absent IS atomic; the guard detects the
    conflict correctly.
  - **What was actually broken is the opposite failure, and it is now FIXED.** A losing writer died with a
    RAW `EEXIST` instead of retrying (1 of 16 writers, reproducibly, in each of the first two runs), taking
    its remaining commits with it. `HostFsOpenWrite` classifies a failed exclusive open by probing
    `fs.FileExists(path)` — chosen deliberately over "fragile message matching" — and **on a fuse mount that
    probe answers FALSE for a file another PROCESS created moments earlier**, because the kernel serves it
    from a cached negative lookup. The `O_EXCL` open itself is correct (it reaches the driver); only the
    follow-up `stat` is stale. So the conflict became a generic IO error, never a `DeltaConflictException`,
    and the retry loop never saw it. **Not budget exhaustion** — instrumenting per-writer retry counts showed
    the highest attempt reached across all writers was **4 of 16**, which is what ruled that out.
    - Fix: check `errno == EEXIST` FIRST, from the structured `"errno"` field DuckDB serializes into the
      exception (a JSON field, not prose — `"File exists"` is locale-dependent, the errno is not), keeping
      the existence probe as the fallback for backends that raise no errno. ⚠ **It is coupled to DuckDB's
      error-serialization format**: if that changes the check silently stops matching and behaviour reverts
      to this bug, with the probe as the only oracle again.
    - Verified: the same 16 × 20 shape after the fix — **90 collisions, 0 writers failed, 320/320 commits,
      `TOTALS [(320,320)]`, `SHORT []`, zero EEXIST escapes.** A quiet run would have proven nothing, which
      is why the harness reports `VOID_no_contention` when retries are 0.
  - **Prefer abfss for concurrent writers on PERFORMANCE grounds, not correctness**: the same shape took
    888 s on fuse vs 261 s on abfss, ~2.8× slower per commit.
  - Harness: `scratchpad/fuse_race.py` via `dotnet run run` (⚠ a raw **Livy session has NO fuse mount** —
    measured `fuse_default: False` — so this needs the RunNotebook path); results land in
    `Files/fabricator_ext/fuse_race_result.json`, fetched with `dotnet run fetch <name>`.
  **PERF (measured per-step): the notebook's in-session work went ~305 s → ~15 s** via two fixes:
  (1) **local-root discovery fast path** (`DeltaCatalog.DiscoverTablePairs`): a root that
  `Directory.Exists` (fuse mount, any local dir) discovers via direct System.IO enumeration
  (schema dirs → table dirs → `Directory.Exists(_delta_log)`) instead of the host glob — the glob's
  commit-file matching + per-match stat was **258 s over fuse on the populated LH → 2 s**; object stores
  keep the glob. **Root cause of the old cost, now ALSO fixed at the source: `HostFsGlob` did an
  `OpenFile(READ)+GetFileSize` PER MATCHED FILE** (DuckDB's FileSystem has no path-stat — size needs a
  handle) purely for a `size` field discovery never reads — and on a fuse mount an open can DOWNLOAD the
  blob into the local cache, so the old ATTACH effectively downloaded every commit json of every table
  (on S3 it was a HEAD per match). Now: size comes from the glob entry's `extended_info["file_size"]`
  when the filesystem's listing provides it (object stores), else -1 → the managed
  `DuckDbTableFileSystem.ListAsync` fills LOCAL files via a cheap `FileInfo.Length` metadata stat,
  unknown ⇒ 0 (the only consumer is VACUUM's bytes-to-delete metric — best-effort by design). The
  wildcard-on-contents glob shape (`…/_delta_log/*.json`) itself is CORRECT for object stores — a
  "directory" doesn't exist as an object there; only a FILE under it proves the table — which is why the
  glob remains the object-store path and only local roots take the System.IO walk.
  (2) the **duckdb wheel ships with the distribution** and installs
  `pip --no-deps --no-compile --target /tmp/fabricator_pyduck` + `PYTHONPATH` for the probe subprocess
  (37 s PyPI force-reinstall → 3.3 s; the session's own duckdb stays untouched). Remaining wall-clock ≈
  Fabric job scheduling/session spin-up (~45–60 s, not ours).
- **Managed-dir resolution gotcha:** `clr_host` looks for the bridge in `FABRICATOR_MANAGED_DIR`, else an
  `fabricator/` folder *next to the loaded module*. For the static `duckdb.exe`/`unittest.exe` the module IS
  the exe, so the default lookup is `build/release/fabricator` (next to `duckdb.exe`) — but
  `publish-managed.ps1` lands the bridge in `build/release/extension/fabricator/fabricator`. So when running
  an exe **directly** you MUST set `FABRICATOR_MANAGED_DIR` to that publish dir (symptom otherwise:
  `Fabricator: failed to load hostfxr from …\build\release\fabricator\hostfxr.dll`). Manual smoke, e.g.:
  `FABRICATOR_MANAGED_DIR=…/extension/fabricator/fabricator build/release/duckdb.exe -unsigned -batch < q.sql`.
- **CoreCLR hosting:** init via `hostfxr_initialize_for_dotnet_command_line` (argv[0] =
  `Fabricator.Bridge.dll`) then `hdt_load_assembly_and_get_function_pointer`.
  `hostfxr_initialize_for_runtime_config` FAILS for self-contained deployments. The bridge finds its
  files via `FABRICATOR_MANAGED_DIR`, else an `fabricator/` folder next to the extension binary.
- **C++ standard gotcha:** DuckDB compiles extensions pre-C++17 → `std::string/wstring::data()` is
  `const`; use `&s[0]` for `MultiByteToWideChar`/`WideCharToMultiByte` out buffers.
- **Tests:** `build/release/test/unittest.exe --test-dir <repo-root> "test/mssqlcompat/<dir>/*"` (and
  `test/verify_*.test`). Set `FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator` +
  `MSSQL_TESTDB_DSN` (and `MSSQL_TEST_SERVER`/`_CONNECTION_STRING` = the same full DSN for the tests
  that ATTACH it directly). The corpus is regenerated from `D:\repos\mssql-extension/test/sql` by
  `scripts/gen_mssqlcompat_tests.sh`; it lives at `test/mssqlcompat/` and is **gitignored** (keep the
  duckdb submodule clean).
- **Test env (docker compose, `docker/docker-compose.yml` — replaced the ad-hoc container 2026-07-10):**
  SQL Server 2025 (`mcr.microsoft.com/mssql/server:2025-latest`, container `mssql-fabricator`, port 1433,
  `sa` / `Arrow_Net_123!`, DBs `ArrowTest` + `TestDB` — created by `docker/provision.ps1`; all other test
  objects self-provision inside the tests) + **MinIO** (S3-compatible: `miniouser` / `miniosecret123` —
  deliberately ALPHANUMERIC, SQL's S3 credential requires it; bucket `fabricator`; S3 API 9000 / console
  9001; **HTTPS** via the self-signed cert from `docker/certs/generate-certs.ps1`, SANs
  `minio`/`localhost`/`127.0.0.1` — SQL Server's `s3://` connector REQUIRES TLS, trusted via the compose
  mount at `/var/opt/mssql/security/ca-certificates`). Bring-up: certs → compose up → provision
  (docker/README.md). Connstr needs `TrustServerCertificate=true;Encrypt=true`. `sqlcmd` v18 in-container:
  `docker exec mssql-fabricator /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C`.
  - **⚠ THE MinIO BUCKET MUST BE CLEANED PERIODICALLY — the S3 suite gets MONOTONICALLY SLOWER, and it is
    the whole cost of the service tier. MEASURED 2026-08-04.** One leg of `verify_delta_catalog_s3` against
    a bucket holding **12,195 objects** (2,002 of them under its own `lake/` root) **did not finish in
    600 s**; the same leg against a freshly emptied bucket took **128 s** for its full 171 assertions. So
    ≥4.7×, and the true ratio is unknown because the dirty run never completed. Since the tier runs that
    suite TWICE (engine-doubled), it dominates the tier's wall clock — this is what made the tier look hung
    earlier that day.
    - **Mechanism: the suite is re-runnable by design via `CREATE OR REPLACE`, which adds a version with
      removes and RECLAIMS NOTHING.** One run leaves 35 objects (16 `lake/`, 11 `copyfmt`, 8 `condsuite`),
      so growth is ~16/run under `lake/` and the observed 2,002 is roughly 125 accumulated runs. Nothing is
      wrong with the suite; the rig just has no reclamation.
  - **⚠ WHY THE S3 SUITE GETS SLOWER — MEASURED 2026-08-07, and the dominant term is ACTIVE DATA FILES, not
    the log and not `CREATE OR REPLACE`. Two priors of mine were wrong before the numbers arrived.**
    - **WRONG PRIOR 1: "ATTACH discovery dominates, because an S3 root globs `*/_delta_log/*.json` and is
      O(total commit files)."** It is O(commit files) — but MEASURED, **ATTACH costs 0.143 s** against the
      accumulated bucket. Discovery is not the problem at this scale.
    - **WRONG PRIOR 2: "it is the log length."** Real but SECONDARY. Same 4-row table, same data, warm:
      a 2-commit copy scans in **1.18 s**, the 148-commit original in **2.61 s** ⇒ ~10 ms per dead commit.
    - **THE DOMINANT TERM, isolated by a control that moves the two variables in OPPOSITE directions:**
      a table built by 150 single-row INSERTs (150 commits, 150 ACTIVE data files) scans in **19.8 s**;
      after `OPTIMIZE` — which makes the log LONGER by another commit while collapsing the files to one —
      it scans in **7.2 s, twice** (−64%). ⇒ ~85 ms per ACTIVE FILE against S3, roughly 8× the per-dead-commit
      cost. **The lever is OPTIMIZE, and it works.**
    - **⚠ MY FIRST SLOPE MEASUREMENT WAS CONFOUNDED AND I NEARLY PUBLISHED IT.** Timing scans at 10 / 60 / 150
      commits gave a beautifully linear ~115 ms per commit — but every one of those commits also ADDED A DATA
      FILE, so the "per commit" slope was really per-file. The `OPTIMIZE` control is what separated them, and
      it is the same discipline this file keeps recording: **a slope over a variable you did not isolate is a
      slope over something else.**
    - **⚠ THE RESIDUAL IS NOT FULLY ATTRIBUTED — do not quote it as a log-length constant.** After OPTIMIZE the
      table still costs 7.2 s with ~151 commits and ONE file, while the 148-commit `lake/t` costs 2.6 s. Those
      two disagree by ~3×, so log length alone does not explain the residual (the OPTIMIZE commit carries 150
      REMOVE actions, which replay is not free). Establish that before optimising for it.
    - **⚠ ENGINEERED-WOOD NEVER DELETES A SUPERSEDED COMMIT FILE — CONFIRMED by forcing
      `delta.logRetentionDuration` to one second and finding 28 commit JSONs still there past two
      checkpoints (see the offer list for the experiment and for the two weaker arguments it replaced).**
      It writes
      a checkpoint every 10 versions (`DeltaTableOptions.CheckpointInterval`) and keeps every commit the
      checkpoint subsumes. Verified: the ONLY `DeleteAsync` on a log path is the temp-file cleanup after a
      failed conditional rename; no `Cleanup*` method exists; and **`delta.logRetentionDuration` appears ONLY
      in doc comments and is never READ**. So `_delta_log` grows monotonically for the life of a table.
      - ⚠ `VacuumExecutor` itself says log files are *"governed by `delta.logRetentionDuration` and log
        cleanup, not by vacuum"* — naming a mechanism that does not exist. Same shape as the `_last_checkpoint`
        saga (#35): **a justification that names a fallback must verify the fallback exists.**
      - Consequence beyond the rig: a dbt incremental model commits once per run, so the log grows without
        bound. At the measured ~10 ms per dead commit an hourly model would add ~90 s of pure metadata to
        every scan after a year — while the far larger per-file term is what makes it hurt sooner. This is a
        product issue, not a test-rig annoyance.
    - **What this means for the rig, in order of payoff:** run `OPTIMIZE` (and then `VACUUM`) on the S3 suite's
      tables, or empty the bucket — the documented maintenance step. `table_filter` on the ATTACH bounds
      ENUMERATION only and would not have helped here, since discovery was never the cost.
    - **⚠ It also re-prices [delta-snapshot-caching](docs/delta-snapshot-caching.md) DOWNWARD again.** That
      gate's headline is a COUNT (4 snapshot constructions per table reference) that was never profiled;
      these numbers say the per-statement metadata floor on S3 is ~1.2 s for a 2-commit table, and that the
      thing worth attacking first is file COUNT, which caching a snapshot does not touch.
    - Clean with `docker exec minio-fabricator sh -c 'mc alias set l https://localhost:9000 miniouser
      miniosecret123 --insecure && mc rm --recursive --force --insecure l/fabricator'` (⚠ **https +
      `--insecure`** — the stack is TLS-only, and `http://` silently reports 0 objects, which reads as an
      empty bucket rather than a failed connection). Everything in there is regenerable: every suite
      self-provisions, and `dbtlake` — 5,891 objects, nearly half — belongs to dbt runs that are in NEITHER
      CI tier. A `VACUUM` at the end of the suite would make it self-maintaining; not built.
    - **⚠ Do NOT bake the clean into provisioning unconditionally: dirty state has DIAGNOSTIC value.** The
      PolyBase deletion-vector finding below (a table that has ever materialized a DV stays unreadable
      through `CREATE OR REPLACE`) was found precisely by RE-RUNNING — the assertion passed on a clean
      account and failed on the second run. A green tier on a freshly cleaned bucket is therefore weaker
      evidence for those particular assertions than a green one on a dirty bucket.
- **ADLS Gen2 / SQL Server data virtualization — BUILT + LIVE-VALIDATED 2026-08-02. Gate
  `test/verify_mssql_adls_polybase.test` (140, manual/live-account tier).** The abfss analogue of the S3
  PolyBase circle below: our Delta provider CTASes a protocol-1.0 table to an Azure storage account, then
  `fabricator_exec` provisions MASTER KEY + DATABASE SCOPED CREDENTIAL + EXTERNAL DATA SOURCE + EXTERNAL
  FILE FORMAT, and SQL Server reads it through `OPENROWSET(FORMAT='DELTA')` **and** a `CREATE EXTERNAL
  TABLE` that our catalog then scans as an ordinary table. Manual tier by necessity — **there is no usable
  ADLS Gen2 emulator** (Azurite does not serve the DFS endpoint), so it runs against a real account and
  cannot join either CI tier.
  - **⚠ `abfss://` IS NOT A VALID SQL SERVER LOCATION** (`46548: contains an unsupported ...`) — and that is
    precisely the scheme everyone writes, including our own ATTACH two lines earlier. SQL Server wants
    **`adls://<fs>@<acct>.dfs.core.windows.net`** (DFS) or **`abs://<fs>@<acct>.blob.core.windows.net`**
    (blob); both MEASURED working, with the `BULK` path container-relative and the leading `/` optional.
    Pinned, because the failure is at DDL time with an error that does not name the alternatives.
  - **The credential is a SAS, not the account key**: `IDENTITY = 'SHARED ACCESS SIGNATURE'`. Read+list at
    container scope is enough and is all the test grants — the engine reading our table has no business
    writing to it. Minted from the account key by `scratchpad/adlsgen2probe sas <fs>` (no leading `?`).
  - Env split mirrors the S3 suite's two endpoints, for the same reason (SQL Server addresses the account
    by a different scheme AND a container-relative path than we do): `FABRICATOR_ADLS_SQL_LOCATION` +
    `FABRICATOR_ADLS_SQL_PREFIX` beside `FABRICATOR_ADLS_ROOT`/`_CONNSTR`, plus `FABRICATOR_ADLS_SAS`.
  - **A dropped EXTERNAL TABLE does NOT delete the underlying data** (user, 2026-08-02) — which is what
    makes this suite re-runnable, and means storage cleanup is a separate, storage-side act. Its
    DEPENDENCY CHAIN does bite though: table → data source → credential, refused in that order (33165 /
    33164), so SETUP must clear the whole chain, not just teardown — a run that dies mid-suite leaves it.
  - **WRITE-BACK (slice C/D) now works on ADLS too — it did NOT, and it failed in a MISLEADING PLACE.**
    Identity-keyed UPDATE/DELETE and routed INSERT through a SQL Server external table were gated on
    `ComposeS3Uri`, which returns null for any non-s3 data source. **Two gates, not one**: the write
    routing AND the identity discovery (`if (info.IsDelta && info.S3Uri is { } uri)`), and the identity one
    fires FIRST — so the symptom was not "not routable" but *"UPDATE/DELETE requires a table with a primary
    key or unique index"*, a message about the SQL side that says nothing about the real cause. Generalized
    to `ComposeStorageUri`. Live: UPDATE, DELETE, routed INSERT, and both engines agreeing on the final
    state; mutation-tested (removing the adls arm reproduces the original binder error at that exact line).
    - **⚠ The two schemes COMPOSE DIFFERENTLY, which is why it is one function and not a prefix test.**
      `s3://` DISCARDS the data-source host (SQL Server's network view; the table LOCATION already carries
      `/bucket/path`). `adls://` KEEPS the authority — it names the filesystem and the real endpoint — and
      the LOCATION is only the path within it, so the client URI is that authority re-spelled `abfss://`.
    - **`abs://` is deliberately NOT routable for WRITES** (reads are fine): deriving a DFS host from
      `.blob.core.windows.net` is right for the public cloud and wrong for sovereign clouds, private
      endpoints and custom DNS. A clean "not routable" beats a guessed hostname that writes elsewhere.
    - En route: `SplitTable`'s parent-folder guard was `slash <= "s3://".Length` — a hardcoded scheme
      length that would wave through `abfss://fs@host` (whose last slash sits inside `://`), yielding the
      root `abfss:/` and a "table" named after the host. Now checks the SHAPE of what remains.
    - The routed INSERT's identity value is **engine-assigned**: an explicitly supplied value is ignored
      and the row continues the table's own high-water mark (pinned — the suite inserts 99 and gets 5).
  - This pass is also what CORRECTED the DV/column-mapping conflation recorded under the S3 entry below.
- **S3 / MinIO / SQL Server data virtualization (2026-07-10).** `httpfs` is now statically linked
  (`extension_config.cmake` — out-of-tree pin `duckdb-httpfs @ 827222fb` since the 1.5.5 bump, always
  the sha DuckDB's own CI
  uses; needs OpenSSL+curl via the vcpkg toolchain: `vcpkg install openssl:x64-windows-static
  curl:x64-windows-static`, configure with `-DCMAKE_TOOLCHAIN_FILE=$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake
  -DVCPKG_TARGET_TRIPLET=x64-windows-static` — the `-static` triplet must match the /MT build). The
  engineered-wood Delta catalog works on `s3://` (MinIO): ATTACH/discovery, CREATE/CTAS/INSERT, pushdown,
  DV DELETE + merge-on-read UPDATE, snapshots, explicit transactions, re-attach, and the
  native_write/native_read variant — `test/verify_delta_catalog_s3.test` (60, gated
  `FABRICATOR_S3_ENDPOINT`; re-runnable via CREATE OR REPLACE against the persistent bucket). Self-signed
  TLS: `SET GLOBAL enable_curl_server_cert_verification = false` (GLOBAL — the transaction flush runs on
  its own connection; production alternative `ca_cert_file`). **Four real bugs found by the S3 rig:**
  (1) `DuckDbTableFileSystem.ExistsAsync` probed via a wildcard-free glob — httpfs' S3 glob ECHOES
  literal paths back without checking the store, so every commit-0 hit a phantom "version 0 already
  exists"; now probes via OpenRead (a HEAD on object stores). (2) The transaction flush used the
  committing context as opener, but the SECRET MANAGER requires an ACTIVE transaction ("ActiveTransaction
  called without active transaction" on s3) — `FabricatorTransactionManager::CommitTransaction` now gives
  the flush its OWN short-lived `Connection` + transaction as the opener (local paths need no secrets, so
  no local test ever saw this). (3) EW `WriteCoreAsync`'s Overwrite-removes omitted the file's
  `deletionVector`, so a REPLACE over a DV-carrying file never matched the active (path,DV) entry — the
  file stayed active FOREVER (duplicated rows after CREATE OR REPLACE of a DV-deleted table); one-line
  fix mirroring CommitDataFilesAsync. (4) **EW `CheckpointReader.ExtractMetadata` DROPPED
  `metaData.configuration`** — after the first checkpoint (interval 10) a table silently lost
  `enableDeletionVectors`/`enableChangeDataFeed`/`columnMapping.mode`/`maxColumnId`, and the loss is
  VIRAL (the NEXT checkpoint persists the config-less metadata — permanently poisoned even after the fix;
  wipe/re-create such tables). Fixed via the existing `GetStringMapField`. **The full circle — SQL Server
  reads our Delta from MinIO:** SQL Server 2025 (17.x) reads CSV/Parquet/**DELTA** on S3 **natively** (no
  PolyBase package, no `sp_configure 'polybase enabled'`, no TF13702 — those are 2022 requirements;
  `mssql-server-polybase` exists for 17.x/Ubuntu 24.04 but is only needed for RDBMS connectors).
  `test/verify_mssql_s3_polybase.test` (70, gated `MSSQL_TESTDB_DSN` + `FABRICATOR_S3_ENDPOINT` +
  `FABRICATOR_S3_SQL_ENDPOINT`): our provider CTASes to `s3://fabricator/polybase` → `fabricator_exec`
  provisions MASTER KEY + `DATABASE SCOPED CREDENTIAL (IDENTITY='S3 Access Key', SECRET='key:secret')` +
  `EXTERNAL DATA SOURCE (LOCATION='s3://minio:9000/')` + `EXTERNAL FILE FORMAT (FORMAT_TYPE=DELTA)` →
  `OPENROWSET(BULK '/fabricator/polybase/trips', FORMAT='DELTA', DATA_SOURCE='s3_ds')` matches row-for-row
  → `CREATE EXTERNAL TABLE` + read back through the ATTACHed catalog as a normal scan (DuckDB →
  fabricator → SQL Server → S3 delta reader → MinIO → table written by our Delta provider). **SQL Server's
  DELTA reader = Delta protocol 1.0 ONLY** — the interop table MUST be written `deletion_vectors false,
  column_mapping 'none'`.
  - **⚠ CORRECTED 2026-08-02 — this used to read "a DV-default reader-v3 table errors", which CONFLATED TWO
    INDEPENDENT REFUSALS and is wrong in a way that misdiagnoses.** A DV-default attach also turns column
    mapping on, so the MAPPING error fires first and gets read as the DV error. Measured separately, and
    IDENTICALLY on ADLS and S3: column mapping `'id'`/`'name'` ⇒ **19725 'Column mapping is not enabled'**;
    deletion vectors DECLARED but none written ⇒ **READS FINE** (the protocol bump alone is tolerated);
    a deletion vector MATERIALIZED by a DELETE ⇒ **19726 'Feature Deletion Vectors is not supported'**.
    So a table can read today and start failing at its first DELETE, with no config change.
  - **⚠ AND IT DOES NOT HEAL: `CREATE OR REPLACE` over a table that has ever materialized a DV leaves it
    UNREADABLE** (still 19726) — the replace is a new version in the SAME log, which still carries
    deletion-vector references. **The recovery is a real `DROP TABLE` + CREATE**, verified. So the interop
    contract is not "write it with the flags off" but "never let a DV materialize on this table". Found by
    RE-RUNNING the suite: the "declared but not materialized" assertion passed on a clean account and failed
    on the second run — a one-shot suite would never have seen it.
  (Same finding class as the Fabric T-SQL endpoint.) Copy-on-write DELETE/UPDATE on
  the plain table KEEP it SQL-Server-readable (plain remove+add stays protocol 1.0 — OPENROWSET reads the
  post-DML state exactly; pinned), so the full DML lifecycle works for SQL-Server-facing tables — just on
  the CoW path instead of DVs. Partitioned delta: the external table reads the partition column as NULL, OPENROWSET
  reads it correctly (documented MS limitation). **IDENTITY on S3 works end-to-end** (v53 marker; values continue
  across re-attach — hwm durable on MinIO) **and stays SQL-Server-readable** (identityColumns is a
  WRITER-only feature, reader stays v1 — pinned). **DROP TABLE on S3 works via a per-file fallback**:
  httpfs' S3 `RemoveDirectory` re-lists keys WITHOUT the scheme prefix and fails its own remove ("URL
  needs to start with s3://"), so `DeltaCatalog.DropTable` catches the failure and deletes glob(`/**`)
  file-by-file + the zero-byte directory-marker keys (`RemoveFile` IS implemented for s3). S3 caveats
  **MEASURED 2026-08-02 (was an inference, and it UNDERSTATED the problem — full A/B:
  [docs/delta-transactions.md](docs/delta-transactions.md) §8.3): an s3 ATTACH that does not NAME a secret
  loses commits SILENTLY — 6 writers × 8 commits ⇒ 8 of 48 landed, 40 lost, ZERO errors; the same shape with
  `SECRET minio_s3` named ⇒ 48/48.** ⚠ **Having a secret in scope is NOT enough** — the marker
  `BuildConnectionString` appends (and hence `S3CommitFileSystem`'s real conditional PUT) rides on the secret
  the ATTACH NAMES, while httpfs uses the same ambient secret for DATA IO, so the unsafe configuration
  authenticates, writes, reads and passes every single-writer test. Silent because EW's commit is
  `RenameAsync`, and the host-FS one emulates put-if-absent with `EXCLUSIVE_CREATE`, which
  `fabricator_fs_write_probe` shows is unguarded on s3 (both creates succeed; the later overwrites). Do NOT
  read the secretless `ALTER TABLE … RENAME` error as the commit path — that is `fs_move_dir`, a different
  operation. Harness `scratchpad/s3_race.sh`. **The ATTACH now WARNS on that shape** (gate
  `verify_delta_catalog_s3` §11, 161 → 171, two mutants killed in opposite directions), which needed a new
  SYNTHETIC ATTACH option: **`access_mode`** (`read_only`/`read_write`/`automatic`/`undefined`) forwarded by
  `fabricator_storage.cpp` into the options JSON, because `READ_ONLY` is a DuckDB ATTACH KEYWORD that no
  provider could otherwise see — no ABI change, the JSON is free-form. ⚠ The warning is gated on
  `read_write` specifically because **an s3 attach with NO `READ_ONLY` clause is bumped to READ-ONLY by
  DuckDB** (measured), so `READ_ONLY false` is the only route to a writable S3 catalog — complete coverage,
  no false positives. Other S3 caveats: `DROP EXTERNAL TABLE IF EXISTS` is not T-SQL (use
  `IF OBJECT_ID(...) IS NOT NULL DROP EXTERNAL TABLE ...`). **Committed-table RENAME TABLE on S3 — DONE
  (2026-07-17, C#-only) for SECRET-routed attaches:** `S3CommitFileSystem.RenameDirectory` renames the whole
  table folder SERVER-SIDE via the SDK (ListObjectsV2 → `CopyObject` per key — unconditional copies are fine,
  only the CONDITIONAL CopyObject is unguarded on MinIO — → batched DeleteObjects; copy-ALL-then-delete so a
  mid-failure leaves the source intact; no data crosses the client; 5 GB/object single-call CopyObject cap
  noted). Wired in `DeltaCatalog.AlterTable` RenameTable + `RenamePendingCreated` (SDK preferred over the
  per-file host-FS copy) when `_s3Credential` is present; SECRETLESS s3 keeps the clean "MoveFile is not
  implemented" error. This unblocks **dbt table-model RE-DEPLOYS on S3-Delta** (the swap's two renames +
  backup drop — previously any re-run of an existing table model failed; found by the 4x1M perf sweep).
  `verify_delta_catalog_s3` §10 (161 — rename + DV commit moved, old name gone, re-attach durable,
  round-trip); dbt minio full-refresh over EXISTING tables green. **CDF on S3 works end-to-end** (change files write to + read
  from the bucket; the feed is exact) **and a CDF table stays SQL-Server-readable** (changeDataFeed is
  writer-only too — pinned). **FIFTH S3-rig bug (EW, parquet-layer):** `ColumnChunkWriter.CompressTo`
  returned a 0-BYTE payload for an empty input — but a valid snappy stream of nothing is the single
  `0x00` length varint, so an ALL-NULL DataPage-V2 values section was "corrupt snappy" to strict decoders
  → **SQL Server failed every table whose read crossed an EW CHECKPOINT** (checkpoints are full of
  all-null column chunks; error 19787 on the `.checkpoint.parquet`; DuckDB/kernel tolerate 0 bytes). Fix:
  let the codec encode emptiness (Snappier emits the valid empty stream); verified — SQL Server reads
  through a fresh v10 checkpoint (12-version table, exact counts). EW Parquet.Tests 585/585. Test sizes
  now: verify_delta_catalog_s3 114, verify_mssql_s3_polybase 118 (+ column-mapping/identity/CDF pins,
  CoW-DML readability, DROP-on-S3, CDF feed over S3).
- **Copy-paste test env** (Bash tool; test-only creds — the REAL Fabric SP lives only in the gitignored
  `dax_secret.sql`, never here). Run the loadable/shell/unittest from `build/release/`:
  ```bash
  export FABRICATOR_MANAGED_DIR=build/release/extension/fabricator/fabricator
  DSN='Server=localhost,1433;Database=TestDB;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true'
  export MSSQL_TESTDB_DSN="$DSN" MSSQL_TEST_SERVER="$DSN" MSSQL_TEST_CONNECTION_STRING="$DSN"
  # a Delta catalog verify test needs a writable base dir:
  export FABRICATOR_DELTA_WRITE_DIR="$(mktemp -d)"      # each test file wants its OWN fresh dir
  # S3/MinIO tests (docker compose stack must be up):
  export FABRICATOR_S3_ENDPOINT=localhost:9000          # gates verify_delta_catalog_s3
  export FABRICATOR_S3_SQL_ENDPOINT=minio:9000          # + MSSQL_TESTDB_DSN gates verify_mssql_s3_polybase
  export FABRICATOR_DELTARS=1                           # gates the 7 verify_delta_rs_* suites; set ONLY when
                                                        # publish-managed.ps1 -IncludeDeltaRs has actually run
  # ── the FULL service tier (scripts/run-suites.sh service) needs these FOUR MORE and refuses to start
  #    without them. Added 2026-08-02: the block above ran individual suites but never the tier it looks
  #    like it describes.
  export MSSQL_BINCOLL_DSN='Server=localhost,1433;Database=BinCollTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true'
  export MSSQL_TESTDB_URI='mssql://sa:Arrow_Net_123!@localhost:1433/TestDB?TrustServerCertificate=true&Encrypt=true'
  export MSSQL_TEST_PASS='Arrow_Net_123!'
  # ⚠ RID-QUALIFIED. Pointing this at .../net10.0 (which holds only the win-x64 subdir) makes
  #   verify_plugin fail with "Scalar Function with name plug_greet does not exist" — INDISTINGUISHABLE
  #   from a plugin that loaded and failed to register. The plugin SPI has no "found nothing to load"
  #   signal. Build it first: dotnet build dotnet/Fabricator.SamplePlugin -c Release
  export FABRICATOR_PLUGIN_DIR="$PWD/dotnet/Fabricator.SamplePlugin/bin/Release/net10.0/win-x64"
  # run one test at a time (the runner concatenates multiple filters into one bad glob):
  build/release/test/unittest.exe --test-dir . "test/verify_delta_catalog_native_write.test"
  # trace the write path: prepend FABRICATOR_LOG_LEVEL=Debug (logs off by default)
  # NOTE: the sqllogictest runner AUTO-SKIPS a test whose error message contains 'HTTP' (network-flake
  # tolerance) — an S3 test that "skips" may actually be FAILING; reproduce via the shell to see why.
  # live Fabric OneLake: a .sql script starting with  .read dax_secret.sql  then
  #   ATTACH 'abfss://Test@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables' AS lake
  #     (TYPE fabricator, PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false [, native_write true]);
  #   piped:  build/release/duckdb.exe -unsigned -batch < script.sql   (LH = schema-enabled, dbo)
  ```

### CI — introduced 2026-07-25 (`.github/workflows/`), tiered by what it needs

Nothing existed before this; the repo was developed and validated by hand on one Windows box. The
tiers are separated by their DEPENDENCIES, not by taste, and each is path-filtered so documentation
commits do not compile DuckDB:

| tier | workflow | what | trigger |
|---|---|---|---|
| 0 | `installer-core.yml` — **TWO jobs** | job `test`: `Fabricator.Installer.Core.Tests`, floor **92**. job `bridge`: `Fabricator.Bridge.Tests`, floor **106** (the variable-library format, the Fabric SQL endpoint-host derivation, and the persisted Delta parquet tuning). Both × {net8.0,net10.0} × {win,linux}. No C++, no vcpkg, **no submodules**. ~2 min | push/PR |
| 1 | `extension.yml` | build + the hermetic tier, **67 runs / 6689 assertions** as of 2026-08-07 (scratch dir + in-repo fixtures only). 3 platforms | push/PR |
| 2 | `integration.yml` | the service tier, **45 runs / 1640 assertions** as of 2026-08-07, via `docker/docker-compose.yml` (SQL Server 2025 + MinIO + generated certs + `provision.ps1`). linux only | schedule + dispatch |
| 3 | `distribution.yml` | the single-file artifact per platform + the **12-check smoke against a STOCK DuckDB wheel** (`test/distribution/smoke_distribution.py`). 3 platforms; needs `OVERRIDE_GIT_DESCRIBE` (the one tier that does) | dispatch + `v*` tags |
| — | manual | `verify_dax` (Power BI Desktop), live Fabric/OneLake (gitignored SP creds), the 7 deltars suites (`-IncludeDeltaRs`, ~240 MB), and on macOS: Gatekeeper/`com.apple.quarantine` + code signing | by hand |

**Proven-in-CI status (2026-07-26).** Tier 0 green. **Tier 1 green on ALL THREE platforms in ONE run**
(`30192450794`, sha `124ad4f`) — each independently 53/53 suites / 4152 assertions, verified from the job
logs rather than the status tick. **Tier 2 green** (`30192508662`) — 42/42 / 1221, nothing skipped,
`verify_mssql_s3_polybase` at its full 252. Both defects that the first CI runs surfaced (the macOS
`ArrowProducer` use-after-free and the undeclared `require parquet`) are fixed and confirmed IN CI, not
merely locally — a distinction this repo's history says to insist on. **`distribution.yml` is now GREEN
on all THREE platforms too** (`30195834247`): each packs the single-file artifact and passes all 12 smoke
checks against a STOCK DuckDB wheel — cold LOAD, `['fabricator','fabricator_core']` both reporting
loaded, a Delta round trip through the extracted core, the warm fast path, and both
must-not-touch-disk rejections. Artifacts upload as `fabricator-v1.5.5-<platform>-<sku>`
(windows_amd64 Standalone 62 MB / osx_arm64 Standalone 60 MB / linux_amd64 Standard 40 MB). **⇒ ALL FOUR
TIERS ARE PROVEN IN CI.** It took three dispatches: the first run failed on both platforms for two
DIFFERENT reasons and enabling macOS exposed a third defect (findings 4 and 5) — none of them in the
exotic machinery the tier exists to cover, all of them in build-environment assumptions that a
developer box silently satisfied.

**Tier 1 has since stayed green across every pin bump and the `PlanFiles` work** — `01994fb` (second EW
bump) and `5c28297` (`PlanFiles`) both green on all three platforms. A green tier-1 job is a stronger
claim than it looks: `run-suites.sh` floors on the run/assertion counts and fails on any SKIP, so the
tick alone proves the counts without reading logs. **One CI gap was closed the same day**: the path
filter listed `.gitmodules` but not the submodule POINTERS, so a pin bump ran NO CI at all (see the traps
list). Note that fix is not self-proving — every commit since has also touched `dotnet/`, so it is only
exercised the next time a pin moves on its own.

**Suite selection is DERIVED, never a hand-kept list** — `scripts/list-hermetic-suites.sh` and
`scripts/list-service-suites.sh` classify by the `require-env`/`require` directives each suite
declares, so a new suite cannot silently sit outside CI. The accounting is complete and checked:
**62 hermetic + 44 service + 11 excluded = 117 suite FILES** (recomputed 2026-08-07), no overlap. ⚠ Suite
FILES and suite RUNS differ and the floors are on RUNS: five hermetic suites and one service suite are
engine-doubled, so 62 files ⇒ **67 runs / 6689 assertions** and 44 ⇒ **45 runs / 1640**. Recompute rather
than copy — the line here read `53 + 42 + 9 = 104` for a while after the counts had moved, and then
`59 + 43 + 11 = 113` for a while after THAT, which is what a hand-copied number does. The one-liner:
`H=$(./scripts/list-hermetic-suites.sh | wc -l); S=$(./scripts/list-service-suites.sh | wc -l);
T=$(ls test/verify_*.test | wc -l); echo "$H + $S + $((T-H-S)) = $T"`. `scripts/list-hermetic-suites.sh | wc -l` and its service twin are the source of
truth; the 11 excluded are `verify_azure_secret`, `verify_dax`, `verify_delta_catalog_adls`,
`verify_mssql_adls_polybase` and the seven `verify_delta_rs_*`. `scripts/run-suites.sh <hermetic|service>` runs them ONE
PROCESS PER SUITE with a fresh scratch dir, and asserts what `unittest` will not: nothing SKIPPED, the
runner never says "No tests ran", and floors on the selected suite/assertion counts. The hermetic tier
CLEARS the service env vars (proving hermeticity); the service tier DEMANDS them and names any that
are missing.

**Per-platform coverage is deliberately unequal — state it, never imply parity:**

| | tier 1 | tier 2 | tier 3 | notes |
|---|---|---|---|---|
| `linux_amd64` | ✅ | ✅ | ✅ Standard | the Fabric deployment target |
| `windows_amd64` | ✅ | (local only) | ✅ Standalone | the development platform; DAX/ADOMD fully supported here |
| `osx_arm64` | ✅ | ❌ impossible | ✅ Standalone | hosted macOS runners **cannot run containers**, so SQL Server/MinIO are unreachable. Demand-driven (DuckDB's user base skews Apple Silicon); DAX untested. Gatekeeper + signing are also outside CI (a runner never quarantines what it built) |

**Traps that cost real cycles — do not rediscover them:**
- **A NEGATIVE RESULT IS NOT A MEASUREMENT UNTIL THE METHOD IS SHOWN TO WORK.** The first two entries
  below are this rule in one narrow form, and `run-suites.sh` institutionalises it (it asserts positive
  facts — "All tests passed" present, nothing skipped, floors met — rather than trusting an exit status).
  It applies identically to every AD-HOC probe, which is where it keeps being rediscovered; three separate
  times on 2026-07-30 alone, each costing a wrong conclusion:
  - **Zero/empty needs a POSITIVE CONTROL.** A missing tool, a typo'd pattern and a genuine absence all
    produce the same `0`. (`strings` is NOT installed in this Git Bash — `strings <bin> | grep -c X`
    silently yields 0 for every X. Use `grep -ac X <bin>`, and check a string that must NOT be there.)
  - **A probe whose PRECONDITION failed is VOID, not evidence** — in either direction. An OPTIMIZE probe
    asserted the concurrent compaction had committed, that assertion failed for an unrelated reason, and
    the failure got read as "no compaction happened, so the code path was never exercised". It had been
    committing all along.
  - **Confirm the query answers the question you asked.** `max(operation)` over a string column returns
    the ALPHABETICAL maximum, not the latest row's value.
  - **Corollary, and the expensive one — to establish that code path A never reaches B, INSTRUMENT B.**
    A backwards grep encodes the searcher's assumed call shape and returns a plausible but incomplete
    enumeration: a regex requiring `…Async` cannot see `table.StartTransaction(...)`, which is exactly how
    "our flush never reaches `CommitOccAsync`" got asserted — on an upstream PR — when the real chain is
    `StartTransaction` → `txn.CommitAsync()` → `CommitTransactionAsync` → `CommitOccAsync`. Reading which
    code emits an error message settles such questions in seconds; backwards tracing does not settle them
    at all.
- **A no-match sqllogictest filter exits ZERO** ("No tests ran"), and the filter is Catch-style, so a
  MID-pattern `*` matches nothing (`test/verify_x*.test` fails, `test/verify_x*` works). A green run
  proves nothing without a positive assertion. (An instance of the rule above.)
- **`unittest -f <list>` (batch mode) is unusable here**: one CLR per process means earlier suites'
  finalizers run during later ones — SIGSEGV at suite 41/53 inside Apache.Arrow's
  `ImportedArrowArrayStream` finalizer. One process per suite is not a style choice.
- **⚠ A TIMED-OUT FOREGROUND RUN LEAVES THE WHOLE PROCESS TREE ALIVE, and the orphan silently CORRUPTS the
  next run. Cost two service-tier runs on 2026-08-07.** The service tier takes LONGER THAN THE 10-MINUTE
  tool cap, so running it in the foreground always ends in a timeout — which kills the SHELL and nothing
  else. `run-suites.sh` and its `unittest` child keep going, invisibly, and the re-run then executes
  CONCURRENTLY with the orphan against the SAME SQL Server databases and the SAME MinIO bucket. Both runs
  are then meaningless: the suites are re-runnable but NOT concurrency-safe with each other (they
  `CREATE OR REPLACE` the same table names).
  - The tell is not in the log — it looked like one run merely stalled at suite 14 for ten minutes. What
    identified it was `Get-CimInstance Win32_Process -Filter "Name='unittest.exe'"`: **TWO** processes, on
    two different suites, under two different `run-suites.sh` trees.
  - **Always start the service tier with `run_in_background: true`**, and before starting one, check that no
    `unittest.exe` / `run-suites.sh` is already running. Kill by walking the tree
    (`Get-CimInstance Win32_Process … CommandLine -like '*run-suites.sh*'`), not by killing the shell.
  - ⚠ It also means a timed-out run must never be treated as "no result" — it is a RUNNING result, and the
    numbers from anything started next to it are void.
- **⚠ NEVER EDIT A SHELL SCRIPT WHILE A BACKGROUND JOB IS EXECUTING IT — it kills the RUN, and the error
  blames the FILE.** bash reads a script INCREMENTALLY, so inserting lines shifts the byte offsets under
  the already-running shell and it resumes mid-token. Symptoms are a syntax error at a line that is
  perfectly valid (`syntax error near unexpected token 'elif'`) or a fragment executed as a command
  (`st: command not found`) — and `bash -n` on the file afterwards passes, which is what makes it read as
  corruption rather than as a self-inflicted race. It cost TWO full tier runs on 2026-08-05, the second
  one AFTER this lesson had already been learned and written down in the session, so treat it as a hard
  rule rather than a caution: while `run-suites.sh` is running, edit NOTHING it reads — least of all its
  own floors, which is exactly when the temptation arises (the measured number has just landed).
  Test SOURCES are safe to edit mid-run; the runner and anything it sources are not.
- **`git update-index --chmod=+x` is required for CI scripts.** `core.fileMode=false` on Windows means
  a local `chmod +x` is never recorded, and Linux then refuses to execute (exit 126).
- **`.gitattributes` forces `*.sh` to LF.** With `core.autocrlf=true` a checkout would give the scripts
  CRLF, breaking the shebang and — worse, silently — inverting `[ "$RUNNER_OS" = 'Windows' ]`.
- **vcpkg infers manifest mode from the CURRENT DIRECTORY.** The steps `cd "$RUNNER_TEMP"` first; a
  `vcpkg.json` at the repo root (there was a stale one, now deleted) makes `vcpkg install <pkg>` fail
  outright. The build consumes the CLASSIC global tree, since CMake's source dir is the duckdb
  submodule and it has no manifest.
- **Do NOT set `-DOVERRIDE_GIT_DESCRIBE` for the TEST build.** It is required for the loadable (a stock
  DuckDB rejects a version mismatch) but it changed autoload resolution enough to make fabricator's own
  `Load` fail on `parquet_scan`. The packaging tier sets it; the test tier must not.
- **`set >> $GITHUB_ENV` corrupts the environment.** GITHUB_ENV is line-oriented, so one variable
  containing a newline breaks every later step; the MSVC step exports only PATH/INCLUDE/LIB/LIBPATH,
  with the redirect written FIRST on each line (`echo VAR=%VAR%>>file` is misparsed when the value ends
  in a digit).
- **VS 18 is NOT an absolute requirement** (correcting the reference bullet above): the local failure is
  a MIXED-toolset artifact — configure with VS 18's STL, link with VS 2022. CI compiles and links with
  one toolset and the runner image's own works fine.
- **A path filter must list the SUBMODULE POINTER, or a pin bump runs no CI.** `extension.yml` listed
  `.gitmodules` but not `engineered-wood`, so bumping the Delta engine — the highest-risk change we make
  — matched nothing. Both 2026-07-26 bumps ran only because they happened to touch `dotnet/` too; the
  test-deletion bump (`70528db`) ran nothing at all. A gitlink appears in the diff as that exact path, so
  the pattern is `engineered-wood`, NOT `engineered-wood/**` (there are no files under it from the parent
  repo's point of view). `duckdb` + `extension-ci-tools` are listed for the same reason — cheaper than
  reasoning about whether a bump happens to co-edit `extension_config.cmake`. `DuckDB.ExtensionKit` is
  deliberately absent: tier 1 never compiles it, only the dispatch-triggered packaging tier does.

**Reproducing a bare runner locally — the single most useful trick here.** Point the profile at an
empty directory so no extensions are installed on disk:

```bash
EMPTY=$(mktemp -d); export USERPROFILE="$(cygpath -w $EMPTY)" HOME="$EMPTY"
./scripts/run-suites.sh hermetic
```

DuckDB resolves `~/.duckdb/extensions` from there, so autoload-from-disk cannot mask a missing
dependency. This turned a 25-minute push-and-wait loop into a 30-second check and immediately found
five suites that passed **only** because this machine happens to have
`~/.duckdb/extensions/v1.5.5/windows_amd64/parquet.duckdb_extension`. (Beware `HOME` under Git Bash: it
is `/z/`, NOT the Windows profile, so a bare `ls ~/.duckdb` misleads.)

**Four defects CI found in its first hours — every one of them invisible on a developer box** (two
destruction-order bugs that only a different allocator faults on, and two "works because of prior
state" bugs that only a CLEAN machine reveals). The through-line: an environment that already has what
you need — an installed extension, a previous build's output — silently satisfies a dependency the code
never actually declares, so a passing local run proves nothing about a fresh one:
1. **Aggregate state destructor = use-after-free (FIXED).** `PhysicalOperator::sink_state` is a
   BASE-class member while the bound aggregate expressions owning the `FunctionData` are derived
   members, so at plan teardown the bind data is already freed when the state destructor dereferences
   it. Deterministic ordering, allocator-dependent fault: Linux SIGSEGV, macOS SIGABRT, Windows silent.
   No destructor is registered now; `AggSessionHolder`'s `agg_close` reclaims.
2. **A late `ArrowProducer` stream release aborted on macOS (FIXED).** `verify_global_functions` died at
   assertion 41 with `libc++abi: terminating due to uncaught exception of type std::system_error: mutex
   lock failed: Invalid argument` (exit 134). Two-line repro, which aborts on its own (NOT
   state-dependent — the statement bisect and an isolated run both land here):
   ```sql
   SELECT squared FROM fabricator_seq(5) WHERE value > 3 ORDER BY squared;
   ```
   lldb (`-k`, not `-o` — see the traps list) put the whole diagnosis in five frames: `std::mutex::lock()`
   ← `ArrowProducer::Release` ← six unsymbolized JIT frames ← `ArrowStreamScan` ←
   `PhysicalTableScan::GetDataInternal`, on the MAIN thread's pipeline (so not a finalizer).

   **Root cause needs BOTH halves, which is why it hid so well:**
   - **C++:** `BuildFilterValues`' producer was a `unique_ptr` LOCAL to `ArrowStreamInitGlobal`, promising
     only to "outlive the scan_table call". It dies when InitGlobal returns.
   - **C#:** the binding's `Execute` was an `async IAsyncEnumerable`, so its `scan.FilterValues?.Dispose()`
     did NOT run at call time — an async-iterator body starts at the first `MoveNextAsync`, i.e. inside
     `get_next`, long after InitGlobal returned. `ArrowProducer::Stream()` hands out a pointer INTO the
     object, so that release locks a destroyed `std::mutex`.

   **Why only macOS reported it:** Apple's `pthread_mutex_lock` validates the signature and returns
   EINVAL, which `std::mutex::lock` turns into a throw; glibc and Windows lock a destroyed mutex
   silently. Same lesson as bug 1 — a passing platform proves nothing about a use-after-free.

   **The `WHERE` is load-bearing** (no predicate ⇒ no filter constants ⇒ no producer ⇒ no crash), and the
   reason filter values exist at all for a function whose binding says `SupportsPushdown => false` is that
   `BindingBoundTable` reports `true` for a global/custom function — that flag is the host's BY-NAME
   projection mapping, not SQL pushdown (its doc says so). Reading the binding's flag instead of the
   wrapper's is what made an earlier pass wrongly "rule out" the filter-values path; the other earlier
   ruling-out was checking `StaticTableFunction.Execute` (a plain method — correct for THAT class) while
   `fabricator_seq` is `GfSeqFunction`, which implements `ITableFunction` directly with an async iterator.

   **Fix, in two layers:** the producer is now owned by `ArrowStreamGlobalState::filter_value_producer`,
   so it lives for the whole scan; the destructor body releases `stream` BEFORE member destructors run
   (a destructor body always does), so a dispose triggered by that release still sees a live producer.
   And the four bindings that ignore pushed filters now dispose in a PLAIN method and delegate to a
   private iterator (`GfSeqFunction`, `GfColumnsFunction`, `cf_columns`, `SqlServerProcedure`) — needed
   independently, because an iterator that is never enumerated never disposes at all, leaving the release
   to the GC finalizer. The contract note lives on `StaticTableFunction.Execute`, which already did it right.

   **How it was found without a CI cycle, and the transferable technique:** a destruction-ORDER bug is
   deterministic, so the non-faulting platform executes the same sequence and can be made to *detect* it.
   A temporary out-of-band liveness registry in `ArrowProducer` (origin string + alive flag in a static
   map, so `Release` never dereferences freed memory) printed
   `LATE RELEASE (use-after-free) of producer … created at [BuildFilterValues]` **on Windows**, first try.
   Then a **class sweep** with the diagnostic still armed over all 53 hermetic suites (4152 assertions)
   came back with ZERO other late releases, so this was the only instance. Reach for this before paying
   for a 20-minute remote debug cycle: you do not have to debug on the platform that faults.

3. **Tier 2's first CI run found an undeclared parquet dependency — the same class as the six hermetic
   suites, in the tier that had never run (FIXED).** All infrastructure came up green (build, TLS certs,
   compose, provisioning); `verify_mssql_s3_polybase` then failed at line 267 — its ONLY
   `native_write true` section, whose data files are written by a host `COPY … (FORMAT parquet)` — with
   `Copy Function with name "parquet" is not in the catalog`. The suite never declared `require parquet`
   (Tier 1's native-write suite does, line 12), so nothing loaded it and the copy-function lookup fell
   back to autoload-from-DISK. **Reproduced locally in one shot with the empty-USERPROFILE trick** —
   identical line and identical 117/116 assertion counts to CI — which is the proof that trick is worth
   keeping: it turns a service-tier CI failure into a local edit loop. A developer box passes either way
   because it has parquet under `~/.duckdb`. Adding the directive does not change the derived
   classification (still 53 hermetic / 42 service; the classifier keys on `require-env`).

4. **The packaging tier could never have worked on a clean machine — `pack-distribution.ps1` probed for
   its own build output BEFORE producing it (FIXED).** `$shellLibrary` was resolved at the top of the
   script, then the NativeAOT publish ran ~40 lines later; on a machine with no previous publish the
   probe returned `$null` and the script threw *"Installer shell (Fabricator.Installer.so for linux-x64)
   not found — publish it on a linux-x64 machine first"* immediately after that very publish printed
   `Generating native code` and succeeded. Every prior run — mine, and the WSL linux build — passed only
   because an earlier publish had left the file on disk. The probe is now a `Resolve-ShellLibrary`
   function called AFTER the publish step. Both jobs fail identically, so it is one fix for both
   platforms. This is the single best argument for having built the packaging tier at all: the artifact
   had been produced correctly by hand many times, and the script was still broken for anyone starting
   from nothing.

5. **The dual entry point — the keystone of the single-file distribution — was silently NOT EXPORTED on
   macOS (FIXED).** Enabling osx_arm64 in the packaging tier produced a clean build, a clean AOT link and
   a clean pack, then failed the smoke test: *"Extension … fabricator_core.duckdb_extension did not
   contain the expected entrypoint function 'fabricator_core_duckdb_cpp_init'"*. Cause is UPSTREAM, in
   `duckdb/extension/extension_build_tools.cmake`: on Apple a loadable extension is linked with hidden
   visibility, `-dead_strip`, and an explicit ONE-symbol whitelist
   `-Wl,-exported_symbol,_${NAME}_duckdb_cpp_init`. Our second entry is therefore stripped. Linux
   (`--gc-sections`/`--exclude-libs,ALL`) and Windows (`dllexport`) both keep it, so macOS is the ONLY
   platform where the one-binary-two-filenames trick fails — and it fails at LOAD time, not build time.
   Fixed in our `CMakeLists.txt` with an APPLE-guarded extra
   `-Wl,-exported_symbol,_fabricator_core_duckdb_cpp_init` (ld64 accumulates repeated `-exported_symbol`
   flags, so it adds to DuckDB's whitelist; the leading underscore is the Mach-O C prefix). Worth
   remembering as a general rule: **anything relying on an exported symbol other than the single blessed
   entry point needs an explicit macOS whitelist entry.**

### TWO CONCURRENT RELEASE LINES — releases MUST be distinguishable (requirement, 2026-07-26)

We will ship builds for BOTH lines at once: the current **`v1.5-variegata`** (DuckDB 1.5.x) and an
upcoming **`main`** tracking duckdb `main` (the next, unreleased version). A user must be able to tell
which artifact belongs to which line, and must not be able to grab the wrong one by accident.

**The constraint that decides the design: the shipped file CANNOT be renamed.** DuckDB derives an
extension's entry symbol from its FILENAME (proved during the distribution work — the identical bytes
that load as `fabricator.duckdb_extension` fail as `fabricator_core.duckdb_extension`), so the installer
shell must stay exactly `fabricator.duckdb_extension`. A version can therefore never be encoded in the
extension's own filename; it must ride the CONTAINER — release tag, release-asset grouping, artifact
name, download directory.

What already protects users, for free: the artifact footer records the DuckDB version
(`OVERRIDE_GIT_DESCRIBE`), a stock DuckDB checks it BEFORE any extension code runs, and the installer's
own gate re-checks version+platform against its manifest. So a 1.5.5 artifact loaded into a main-line
DuckDB fails with a friendly error rather than misbehaving — the safety property holds; what is missing
is only human-facing labelling.

**How this is now implemented (2026-07-26).** CI artifacts are
`fabricator-<duckdbversion>-<platform>-<sku>` (e.g. `fabricator-v1.5.5-osx_arm64-Standalone`), and the
`release` job in `distribution.yml` attaches ONE ZIP PER (platform × SKU) to a GitHub release.

**The release assets are ZIPs, and that is forced, not cosmetic.** An asset named
`fabricator-v1.5.5-linux_amd64-Standard.duckdb_extension` would DOWNLOAD fine and then FAIL to load —
DuckDB would derive the entry symbol `fabricator-v1.5.5-linux_amd64-Standard_duckdb_cpp_init` from the
file name. And the bare name `fabricator.duckdb_extension` cannot be used for all three either, because
asset names must be unique within a release. A versioned ZIP satisfies both: the ARCHIVE name
distinguishes platform/SKU/DuckDB version (hence the line), the file inside keeps the mandatory name, and
the release notes say "do not rename it".

**TAG SCHEME — DECIDED + PROVEN END-TO-END (2026-07-26).** Format **`v<fabricator>-duckdb<duckdbversion>`**,
first tag `v0.0.1-duckdb1.5.5`, with one rule that makes it safe: **never publish a bare `vX.Y.Z`.** SemVer
reads the `-` suffix as a PRERELEASE, so a bare `v0.0.1` would sort ABOVE it; with the suffix always present,
ordering within a line stays correct (`v0.1.0-duckdb1.5.5` > `v0.0.1-duckdb1.5.5`) and the two lines stay
distinguishable in the one `v*` namespace the trigger requires. `+duckdb…` would be the semantically correct
SemVer (build metadata) but `+` is %-encoded in URLs and most tooling IGNORES build metadata when comparing,
so the two lines would compare EQUAL — not worth the purity. Use the real DuckDB version for the future line
(`v0.0.1-duckdb1.6.0`), never `-duckdbmain`: a moving target makes a poor release identity. `0.0.1` because
that is what the binary reports (`fabricator_version()` + the footer) — tagging a number the artifact does not
claim mislabels the release against its own contents. Nothing in the workflow hardcodes the tag: title and
notes derive the DuckDB version from the single `DUCKDB_VERSION` var; the tag is used verbatim.

**Release status (CORRECTED 2026-07-28 — the note below used to say "DRAFT … unpublished" and was WRONG,
which nearly caused a published release to be retagged):**
- **`v0.0.1-duckdb1.5.5` is PUBLISHED** (`draft=false`), created 2026-07-27, pinning **`a8de094`** — NOT the
  `5c28297` this note recorded; it was retagged again after that. Three ZIPs attached, 0 downloads:
  `linux_amd64-Standard` 40.1 MB / `osx_arm64-Standalone` 60.0 MB / `windows_amd64-Standalone` 62.2 MB.
- **`v0.0.2-duckdb1.5.5`** cut on 2026-07-28 at `21e7be5`, +30 commits over v0.0.1: both EW clast-master
  bumps, the variant shredding split, the `_metadata` locator conformance, and the `TransientRowAddress`
  helper migration. `distribution.yml` run **green on all three platforms** and the **DRAFT** release exists
  with its three ZIPs (linux_amd64-Standard 40.2 MB / osx_arm64-Standalone 60.1 MB /
  windows_amd64-Standalone 62.2 MB). Publishing is still a human decision.
- **`v0.0.1-duckdb1.5.5`'s RELEASE object was DELETED BY THE USER, deliberately (confirmed 2026-07-29)** — so
  the API lists only v0.0.2. **Its TAG deliberately survives on the remote at `a8de094`**, which is the part
  that matters: the tag is what keeps that release's source reproducible (`git submodule update` cannot
  reliably fetch an unreachable sha, so an orphaned commit would make the tagged build unbuildable). Nothing
  to investigate — do NOT "restore" it.
**⚠ CHECK `draft` VIA THE API BEFORE TREATING A RELEASE AS MOVABLE — do not trust this file.** The retag rule
below is real and still applies; what went stale was the FACT it was applied to. Once published, a tag move
is not merely history-rewriting: the attached assets were built from the OLD commit, so moving the tag leaves
a release whose **source tag and binaries disagree** — worse than a tag that is simply behind. 0 downloads is
luck, not a guarantee. Ship newer code as a NEW tag instead.

**The version number is NOT free to choose: it must match what the binary reports**, or the release is
mislabelled against its own contents. That means bumping **BOTH** declarations, which are easy to miss:
`CMakeLists.txt`'s `FABRICATOR_EXTENSION_VERSION` (→ the `FABRICATOR_VERSION` compile definition, i.e. what
`fabricator_version()` returns) **and** `extension_config.cmake`'s `EXTENSION_VERSION` (→ the extension
footer). `v0.0.2` was preceded by exactly that bump.

- **THE SOURCE VERSION IS NOW `0.0.3`** (bumped 2026-08-03; both declarations). **No tag or release has been
  cut for it** — the bump only makes the tree ready for one. Verified the way this section demands rather
  than by reading the build files: `fabricator_version()` returns `0.0.3`, and the rebuilt loadable's footer
  reads `CPP | 0.0.3 | v1.5.5 | windows_amd64 | 4` (all four fields, per the strip/append trap above).
  The define has exactly ONE consumer (`fabricator_extension.cpp` returning it), and no suite pins the
  literal — `verify_names` and the distribution smoke both assert only `IS NOT NULL` — so the bump cannot
  change behaviour elsewhere.
  - Content since `v0.0.2` (`21e7be5`): the ADLS Gen2 support + PolyBase circle + external-table write-back,
    the Fabric SQL catalog binding (§9h) and the two bugs it found, Fabric SDK 2.18, zero-argument scalars,
    the unified parameter protocol, and the provider-declared `_each`.

Earlier moves, while it genuinely was a draft: `0eadd00` → `c2af48a` (to pick up the first EW bump's two
silent-corruption fixes — the UTF-16-vs-UTF-8 comparator that could make pruning SKIP a file containing
matching rows, and stats truncation splitting a surrogate pair) → `5c28297` (the second EW bump, the
ns/second timestamp guard, and `PlanFiles`) → `a8de094`. Each tag message records what that move gained, so
the reasoning survives without this file. **`distribution.yml`'s release job creates the release with
`--draft`**, so a new tag yields a draft and publishing stays a human decision.

**Still to build:** nothing in the tiers themselves. **macOS Gatekeeper is
a caveat CI structurally cannot cover**: a browser-downloaded `.duckdb_extension` carries
`com.apple.quarantine`, which can refuse an unsigned dylib, while a runner never quarantines what it
built. Needs a real Mac and an install-doc note.

## Key decisions & constraints

- **Connection strings must be valid `Microsoft.Data.SqlClient` strings**, passed straight through. We
  do NOT replicate the C++ extension's bespoke connstr dialect (`authenticator=krb5|ntlm|winsspi`,
  `krb5-*`, its client-side conflict validator). The only connstr-shaped input we parse is our own
  `mssql://` URI, translated into a SqlClient connstr. SqlClient already does integrated/Windows/Entra
  auth natively (`Trusted_Connection`, `Integrated Security=SSPI`, `Authentication=Active Directory …`).
  → `integrated_auth/parsing.test` is failing-by-design. If verbatim cross-compat for an
  `authenticator=krb5` string is ever needed, do a thin keyword *translation*, never a validating parser.
- **Secret parameter names mirror the C++ mssql secret** (host/port/database/user/password/use_encrypt/
  access_token/authentication/azure_secret/schema_filter/table_filter/application_name) — left as-is for
  cross-compat (user decision: "leave as is").
- **Statistics: report ONLY NDV, never min/max.** DuckDB's StatisticsPropagator prunes filters on
  min/max (→ FILTER_ALWAYS_FALSE/TRUE), so they must be exact; SQL Server stats are sampled/stale →
  reporting min/max could drop rows. NDV only feeds selectivity (never pruning) → stale is safe.
- **Pushdown is best-effort and never erases** — DuckDB re-applies every predicate, so an
  over-approximation (superset) is correct; map filters/projection **by name**, not positionally.
- **C++ is provider-agnostic** — the operators only produce Arrow + table/column identity; every SQL
  Server specific (SqlBulkCopy, parameterized UPDATE/DELETE, type mapping, DDL generation, all `sys.*`)
  lives in `Fabricator.SqlServer`. Keep it that way.
- **The C++↔C# Arrow boundary always uses STANDARD encoding** (`fabricator::BoundaryClientProperties`, used at
  every DuckDB→Arrow site instead of `context.GetClientProperties()`): it keeps the session time zone +
  Arrow output version but forces `arrow_lossless_conversion`/`arrow_offset_size`/`produce_arrow_string_view`/
  `arrow_use_list_view` to their standard form. Our bridge maps Arrow→provider types itself, so a user's
  **global** `SET arrow_lossless_conversion = true` must not change our boundary encoding — otherwise DuckDB
  exports `BOOLEAN` as Arrow `Int8` and our mapper turns it into SQL `SMALLINT` (1/0) instead of `BIT`
  (true/false), and `HUGEINT` into `nvarchar`. Verified: `test/verify_arrow_lossless.test`.
- **Self-healing catalog cache:** `GetOrCreateEntry` evicts on a `FetchTableColumns` failure (a table
  dropped out-of-band leaves no stale entry). Do NOT remove this to match
  `exec_invalidate_cache_setting.test`'s setting-OFF stale-cache footgun — it's a deliberate robustness
  difference, not a bug.
  - **⚠ It is NARROWER than it sounds, and the wording above invites over-reading it (measured 2026-07-30).**
    The self-heal is in the COLUMN FETCH, so it only covers an entry that has not been MATERIALIZED yet: the
    name is in the discovered list, the fetch fails, the name + entry are evicted, and the caller sees a clean
    `Catalog Error: Table with name X does not exist!` (which is what lets `CREATE … IF NOT EXISTS` work).
    Once a table has been READ in the session, its entry is cached, so an out-of-band DROP is not noticed at
    bind at all — the scan runs and fails with the provider's RAW error, observed as
    `IO Error: Fabricator: scan_table failed: 208: Invalid object name 'dbo.x'`. Both orders were measured
    against SQL Server; the difference is purely whether the entry was already materialized. So "a dropped
    table leaves no stale entry" holds for the un-read case only. A rough edge rather than a designed
    behaviour — nothing depends on the 208 text, and turning it into a clean catalog error would mean
    classifying provider errors at scan time (an object-not-found probe on every scan failure).
  - **⚠ IT USED TO INFER ABSENCE FROM ANY FAILURE — FIXED 2026-08-01 (no ABI bump).** `GetOrCreateEntry`
    caught `std::exception` and read every one as "the table is gone", so a table that merely could not be
    READ was **erased**: entry dropped, name removed from `table_types_` (so it left ENUMERATION too — `SHOW
    TABLES` showed nothing), and the provider's real error discarded one frame after it was produced. A
    Delta table with an incomplete log demonstrated it end to end: `fabricator_delta_scan` reported
    engineered-wood naming the exact missing version, while the catalog path said *"Table with name t does
    not exist!"* for a table whose data was entirely intact. Same for an expired credential or a brief
    outage. A user told "does not exist" checks spelling and permissions; nothing in that search leads to a
    missing commit file.
    - **The fix is CLASSIFICATION, not removing the catch** (which is load-bearing — see above).
      `FABRICATOR_NOT_FOUND = 3` had been in `abi.h` from the start, **never produced and never consumed**;
      wiring it needed no version bump. C# gained `ObjectNotFoundException` → `Bootstrap.GetMetadata`
      returns that status; C++ `GetMetadata` maps it to `fabricator::ObjectNotFoundException` (deriving from
      `IOException`, so an UNCAUGHT one reads exactly as before); the catch narrowed to that type alone.
    - **Each provider must ESTABLISH absence, never guess it.** Delta: no commit in `_delta_log`, i.e.
      `GetLatestVersionAsync() >= 0` — the engine's OWN predicate, so the two cannot drift. SQL Server:
      **error NUMBER 208**, not message text (note 208 also covers an object the principal cannot SEE —
      SQL Server reports it identically on purpose — so treating it as absence preserves the prior
      semantics exactly). Both classify ONLY on the failure path, so a healthy fetch costs nothing extra,
      and both answer "exists" when the probe itself fails: **unknown is not absence**, and answering
      otherwise would erase a table we merely cannot see.
    - **⚠ THE PATH HAD NO COVERAGE AT ALL, and the service tier proved it by staying green while it was
      broken** (44/44). Gate added: `verify_exec_invalidate_cache` §out-of-band drop (10 → 21). It must run
      with `mssql_exec_invalidate_cache` **OFF** — with the auto-invalidate ON, which the rest of that suite
      needs, the DROP refreshes the whole cache and the name leaves the discovered list, so the lookup
      answers "does not exist" without ever fetching columns. Written that way first, the section passed
      with the provider's absence detection DISABLED; mutation-testing is what caught it measuring the
      wrong thing.
- **Catalog-entry evictions RETIRE, never destroy (2026-07-16 — use-after-free fix).** Every eviction of
  a materialized entry (`InvalidateEntryCache`/`InvalidateAllEntries` on rollback, `InvalidateMatching`,
  the ALTER re-key/eager-refresh, DropEntry, CREATE-OR-REPLACE re-adds, all self-heal evicts — and the
  catalog-level `schemas_` on DROP/REPLACE SCHEMA) moves the `unique_ptr` into a GRAVEYARD
  (`retired_entries_` / `retired_schemas_`, freed at teardown) instead of destroying it: the lookup paths
  hand DuckDB's binder RAW pointers held across bind→plan→execute, so a concurrent eviction destroying
  the entry was a UAF — hit under `dbt --threads 4` incremental full-refresh (4×1M, box) as
  `INTERNAL Error: CatalogEntry::ParentSchema called on catalog entry without schema` (the binder's
  virtual call landing on the destroyed entry's REWOUND vptr — that error text is the fingerprint of a
  destructed catalog entry; a concurrent thread's post-commit ROLLBACK → `InvalidateAllEntries` destroyed
  the `__dbt_tmp` entry between the rename's bind lookup and its `ParentSchema()` call,
  bind_simple.cpp:160). Stale-but-alive matches the cache's existing staleness semantics; schema entries
  must outlive table entries (each entry's `ParentSchema()` is a REFERENCE into its schema entry). Never
  "optimize" evictions back to immediate destruction.
- **CHECK constraints + non-literal DEFAULTs on CREATE: deliberately skipped** (per user).
- **Never set `USE_TMP_FILE` on our COPYs** — it is ALREADY false on every fabricator write path (its
  default needs the target to pre-exist as a REGULAR file; Delta data files are immutable so ours never
  do), and setting it explicitly THROWS combined with `PARTITION_BY` — a defensive blanket setting would
  break `RunCopyPartitioned` at bind. Half-written files are handled by the COMMIT ORDER (orphans →
  VACUUM), not by COPY. Full analysis (2026-07-29): [docs/abi-history.md](docs/abi-history.md), v64 entry.
- **BRANCH NAMING mirrors DuckDB's (adopted 2026-07-25).** The default branch is
  **`v1.5-variegata`** — the same name DuckDB uses for its 1.5 release line (`refs/heads/v1.5-variegata`;
  its predecessors are `v1.4-andium`, `v1.3-ossivalis`), which is also what the extension ecosystem
  (duckdb-httpfs/-delta/-azure) does. The duckdb submodule pin belongs to the branch: `v1.5-variegata`
  pins release tags within the 1.5 line and moves tag by tag. **`main` is RESERVED for tracking duckdb
  `main`** (the next, unreleased version) and **does not exist yet** — deliberately: creating it means
  absorbing continuous upstream API churn (the 1.5 `ExtensionLoader` break is the precedent) and
  doubling CI minutes for zero consumers. Add it as a nightly allowed-to-fail branch when there's a 1.6
  preview worth tracking. Note the sharp edge: `main` will eventually exist again but MEAN something
  different, so don't treat a `main` reference in older notes as "the current line".
- **Commit only when asked.** The Python scaffold (`main.py`/`pyproject.toml`/`uv.lock`/
  `.python-version`) is intentionally left untracked. `.gitignore` note: `**/fabricator/` would match the
  *source* `src/fabricator/` + `src/include/fabricator/` — negations re-include them; never re-broaden it.

## Sibling repos (reference under `D:\repos\`)

(engineered-wood is no longer here — it's an in-tree submodule `engineered-wood/`, see "Build & test".)
`SqlServerFlights` (reusable C# SqlClient/DAX→Arrow; its `Airport/Data` `ArrowTypeConverter.cs`/`FlightField.cs`
are the granular type-conversion reference — original SQL type + precision/scale/length carried on Arrow
field metadata for precise + lossless round-trip, and Arrow extension names `arrow.bool8`/`arrow.uuid`/
`arrow.json` to disambiguate same-storage types; see [docs/warehouse-support.md](docs/warehouse-support.md)
§3.4 for the future type-mapping refinement), `ArrowSerializer` (POCO↔Arrow for Phase 3)

## Documentation index (`docs/`) — with a STATUS per doc

Every doc is listed here, and `scripts/check-docs.sh` FAILS if one is missing — an unreferenced doc is not
wrong but it is undiscoverable, and undiscoverable is how a doc rots unnoticed. When this index was written
(2026-07-30) **11 of 32 docs were unreachable from this file, and all five whose last substantive edit was the
2026-07-15 rename were among them.**

The **status** column is the part no script can produce. `check-docs.sh` verifies that every path, link and
`verify_*` suite a doc cites still exists; it cannot tell whether the prose is still TRUE. `multifile-delta.md`
is the standing example — every reference in it resolves, and its header still announces work the production
path never adopted. Keep the status honest; a wrong status is worse than none.

| doc | status |
|---|---|
| [abi-history.md](docs/abi-history.md) | **current** — per-version ABI records v16–v67. Read before touching an existing entry |
| [aot-bridge.md](docs/aot-bridge.md) | **design only, nothing built** (2026-07-25) |
| [cancellation.md](docs/cancellation.md) | **current** — the three cancellation tiers (ABI v65/v66) |
| [consumption-monitoring.md](docs/consumption-monitoring.md) | **analysis + TWO BUILT** (2026-07-31) — CU/consumption attribution for a dbt run; the `application_name` fix and `db.dbo.fabricator_session_tag()` (gate `verify_session_tag`) came out of it, the CU half is still analysis. ⚠ §2.4c: the session tag is MEASURED UNRELIABLE as a dbt pre-hook at `--threads>1` (a model's body frequently saw a STALE run's tag — worse than none), so `application_name` is the recommended dbt vector; mechanism not yet established, one suspect is DuckDB txn-id reuse colliding in our per-transaction `_txns` keying. THREE tagging vectors CONFIRMED live (`OPTION (LABEL)` on all 5 statement shapes incl. CTAS; `Application Name`→`program_name`; and the WINNER — a run UUID in `sp_set_session_context`, which is SELF-BRIDGING because the EXEC's own `command` text is recorded, so a session's whole statement set attributes by `connection_id` with NO registry, NO label and NO extension feature; a session can also read its own `connection_id`/`dist_statement_id` from `sys.dm_exec_requests`, the latter being the Capacity Metrics join key). Records a live `application_name` DEFECT, the finding that consecutive extension calls do NOT share a session, and a REPORTED-not-confirmed Aug-2026 Warehouse metering change that would undercut per-model costing |
| [create-table-with-options.md](docs/create-table-with-options.md) | **current** — all four `WITH (…)` slices shipped |
| [custom-functions-design.md](docs/custom-functions-design.md) | **current** — the 4b–4h contract; §11.1 is the in-out design |
| [dax-provider.md](docs/dax-provider.md) | **current** — read-only DAX/ADOMD provider, slices 1–6. Gate is MANUAL (needs Power BI Desktop) |
| [dbt-hooks.md](docs/dbt-hooks.md) | **current** — validated box + Fabric |
| [dbt-incremental.md](docs/dbt-incremental.md) | **current** — validated box + Fabric |
| [delta-catalog.md](docs/delta-catalog.md) | **current** — the main Delta provider reference |
| [delta-rs-provider.md](docs/delta-rs-provider.md) | **current but SECONDARY** — the delta-rs provider is opt-in (`-IncludeDeltaRs`, `FABRICATOR_DELTARS=1`); its 7 suites are outside CI |
| [delta-snapshot-caching.md](docs/delta-snapshot-caching.md) | **design + decision gate; the cache is NOT built** and the full version is not recommended |
| [delta-transaction-hoist.md](docs/delta-transaction-hoist.md) | **slices 1a, 1b+2 and 5 BUILT; slice 3 BLOCKED, slice 4 optional** (2026-08-04, user-decided). **Slice 5 makes a CREATE inside a transaction IMMEDIATE**, which LIFTS the ALTER and DELETE refusals on a same-transaction-created table (not UPDATE — §4.3.4) and makes ROLLBACK drop it best-effort; the price is that v0 is visible for the transaction's life. §4.3 lists the six things the design got wrong, incl. that a **CTAS creates inside `begin_bulk`, not via `CreateTable`** — hoist EW's `DeltaTransaction` from flush time to STATEMENT time. ⚠ It is a HOIST, not an adoption: the flush already calls `StartTransaction`, and the read-your-writes overlay stays ours because `DeltaTransaction.Snapshot` is the BASE snapshot. The main prize is BANKED — `StageChangeDataAsync` is now called at statement time, our 45-line EW duplicate is deleted, and the `WriteChangeDataFilesForAsync` offer is **RETIRED, never sent**. §6 is the CDF row-identity DIVERGENCE that settling slice 2's mutation question exposed (buffered append writes NULL ids where autocommit yields real ones) — with the reason the cheap fix silently loses rows. ⚠ §4.1 slice 3 is **BLOCKED, both halves** (born-deleted rows are a PARAMETER of `StageDataFilesAsync`; DV computation reads only the base snapshot) and unblocking it would CREATE an upstream ask — so **slice 5 is the next one worth doing and depends on neither 3 nor 4** (§4.2) |
| [delta-transactions.md](docs/delta-transactions.md) | **current** — buffered-DML semantics. §8.1 = the MEASURED OneLake multi-writer result (2026-07-31; one bug fixed, one gap left OPEN); §10.6 = the MEASURED Fabric Spark isolation-property matrix, replacing a stale "we do NOT read it" |
| [duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) | **current** — DuckDB bugs found from here, reproduced on the STOCK wheel with controls. §1 is ready to file (a `read_parquet` assertion that INVALIDATES the database); §2 is the counter-example the file exists to enforce — a finding that looked upstream, did not reproduce, and must NOT be filed |
| [distribution-installer.md](docs/distribution-installer.md) | **current** — single-file SKU, phases 1–4 of 5 |
| [ew-upstream-0.3.0-analysis.md](docs/ew-upstream-0.3.0-analysis.md) | **ANALYSIS ONLY — nothing merged, nothing re-pinned** (2026-08-07). Pre-flight for the v0.3.0 bump (15 commits, pin `3794fe4` → `fa9b556`), bigger than any since the clast-master re-pin because upstream extracted the OCC core out of `DeltaTable` (issue #65 / PR #83). ⚠ TWO of our four patched files were DELETED from `.Table` and moved — the delete/modify shape that silently dropped a patch in the 2026-08-01 bump. Also lists the numbers this bump INVALIDATES |
| [ew-master-migration.md](docs/ew-master-migration.md) | **current** — the EW pin journal. Read BEFORE the next EW bump. §FULL PATCH-SET AUDIT (2026-08-03) is the file-by-file verdict against `v0.2.0` with a KEEP/OFFER/DROP per file; §THE `*BySelection*` QUESTION records why the merge-on-read UPDATE must NOT move into the Bridge and what to offer instead; §2b is the ConflictChecker reading half incl. the two ways it DIVERGES from Delta |
| [fabric-api-functions.md](docs/fabric-api-functions.md) | **current — the whole curated set is BUILT; P0/P1/P2 + semantic models + XMLA live-validated, P3 wired but NOT live** (no git-connected workspace / pipeline / mirrored DB on this tenant). §9b spike results, §9c as-built (incl. the zero-argument Arrow fix), §9h the SQL Server binding + the two shipped bugs it found, §9j variable libraries, §9k Spark sessions, §9l the `fabric` SCHEMA move, §9m job-instance fan-out + the fan-out verdict per remaining function, §9n the inferred/renamed ATTACH options + the endpoint-host encoding, §10 the full API sweep with a verdict per area |
| [feature-history.md](docs/feature-history.md) | **archive** — as-built records moved verbatim out of this file. Historical by design |
| [known-limitations.md](docs/known-limitations.md) | **current — READ THIS FIRST when asking "does X work?"** One page for what does NOT work (measured) and what has been CLAIMED but not measured, for storage/concurrency/transaction behaviour. ⚠ Deliberately NOT exhaustive for the whole extension — absence from it does not mean "no limitation"; per-area docs own theirs. Exists because those answers were scattered across four docs and a commit message |
| [filesystem-bridge.md](docs/filesystem-bridge.md) | **current mechanism, untouched since the rename** — the v40 host-FS bridge is very much live (see the per-call opener fix, `142b350`) |
| [global-functions.md](docs/global-functions.md) | **current** — all five load-time global kinds |
| [host-query.md](docs/host-query.md) | **current** — incl. session-state inheritance + attached-catalog visibility (2026-07-30) |
| [inout-collector-mode.md](docs/inout-collector-mode.md) | **current mechanism, untouched since the rename** — the collector path is live (`verify_collector`) |
| [macros-and-sqlgen-functions.md](docs/macros-and-sqlgen-functions.md) | **current** — §1 global macros, §1.4 catalog-bound macros, §2 sqlgen |
| [multifile-delta.md](docs/multifile-delta.md) | ⚠ **STALE HEADER.** Says "Phase-A slices BUILDING"; slice 1a shipped as the standalone `fabricator_delta_mfr_scan` (+ its suite) and the PRODUCTION catalog read path never adopted it. Treat as a design record, not a description of the shipped read path |
| [native-delta-write.md](docs/native-delta-write.md) | ⚠ **PRE-DATES THE DEFAULTS FLIP (2026-07-29).** Its §2 table still says the default is engineered-wood everywhere, and it cites the `deltalake` alias the flip REMOVED. The mechanism description is sound; the defaults are not |
| [parallel-partitioned-read.md](docs/parallel-partitioned-read.md) | **design only, nothing built** |
| [plugin-system.md](docs/plugin-system.md) | **current** — default-context SPI; per-plugin ALC isolation deferred |
| [provider-extensibility.md](docs/provider-extensibility.md) | **current** — the self-describing-provider surfaces |
| [rowid-concepts.md](docs/rowid-concepts.md) | **current** — transient vs stable row identity |
| [rowid-dml-seam.md](docs/rowid-dml-seam.md) | **current** — the DML seam after the EW re-pin |
| [settings-architecture.md](docs/settings-architecture.md) | **current, refactor DONE** (settings v33/v34, ATTACH v37, secret fields v38) |
| [transaction-concurrency.md](docs/transaction-concurrency.md) | **current** — per-DuckDB-transaction provider connections (ABI v35) |
| [transactions.md](docs/transactions.md) | **current** — the three lazy levels, MARS, the one-writer rule |
| [variant-support.md](docs/variant-support.md) | **current** — six passes, Spark + kernel validated |
| [warehouse-support.md](docs/warehouse-support.md) | **current** — Fabric WH / Synapse / box profiles, slices 1–6 |
