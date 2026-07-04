# Native Delta write — DuckDB parquet writer + engineered-wood metadata

> **Status: P0 spike + P1 (INSERT/CTAS/append) + P3 (DELETE) + P4 (UPDATE) copy-on-write native-write half +
> P5-append (row tracking) DONE (2026-07-04), and VALIDATED LIVE on Fabric OneLake (write DML round-trips +
> DuckDB-native-writer bloom signature over `onelake://`). The FULLY-NATIVE copy-on-write REWRITE + SQL-join
> UPDATE substitution is now DONE too (2026-07-04, commit `143c09d`): DELETE/UPDATE read the source via
> `read_parquet` AND apply the row-level transform in DuckDB SQL (WHERE-filter / LEFT JOIN), retiring the
> in-process `BuildArray`; DV-aware + schema-evolution backfill; validated by the official `delta_scan` +
> LIVE on Fabric OneLake. Still open: CDF native change files + P5-rewrite (materialize id/version on rewrite)
> + alias-default — see §10.**
> Completes the "inversion" begun by the native *read* path
> (`docs/multifile-delta.md` §"Native-read fold"): C# is a pure Delta-**metadata** provider, and DuckDB's
> native parquet reader **and writer** do all data-file I/O. engineered-wood's weakest surface (its parquet
> reader/writer — source of every decimal/DV-format/DataPage-V2/signed-min-max/`path_in_schema` bug we fixed)
> falls away; its strongest surface (the `_delta_log` protocol layer) stays.

## 1. Motivation

Every engineered-wood defect this project hit lived in its **parquet layer**, never its `_delta_log` layer:
decimal read corruption, the RoaringBitmap DV byte format, DataPage-V2-by-default, signed min/max without
`column_orders`, missing `path_in_schema`, the copy-on-write footer bugs. DuckDB's parquet writer is
battle-tested: automatic bloom filters, dictionary + correct min/max stats, standard encodings,
Spark/Fabric-readable footers. So:

- **Data files** ← DuckDB's native parquet reader/writer (already reading; add writing).
- **`_delta_log` commit** (protocol, `add`/`remove`, DV, CDF, row tracking, snapshots, OCC retry) ← keep
  engineered-wood. This is spec-defined, stable, and Fabric-validated.

This **de-risks the part of engineered-wood whose future is uncertain** (its parquet codec) while keeping the
part that is genuinely good, and it eliminates the entire interop-caveat list at the source.

## 2. Positioning — toggles + alias defaults, NOT a code fork

Do **not** duplicate the catalog/DML/metadata machinery into two providers. One backend, two ATTACH toggles
symmetric to the existing `native_read`:

| ATTACH option | data-file **read** | data-file **write** | `_delta_log` |
|---|---|---|---|
| (default) | engineered-wood | engineered-wood | engineered-wood |
| `native_read true` | `read_parquet` (native) | engineered-wood | engineered-wood |
| `native_write true` | — | native `COPY … TO … (FORMAT parquet)` | engineered-wood |
| both | native | native | engineered-wood |

Then the **provider-name aliases set the defaults** (we already register `delta`/`deltalake` as aliases of
`engineeredwooddelta`):

- `PROVIDER 'delta'` → `native_read`+`native_write` **on** = the hybrid **production** path.
- `PROVIDER 'engineeredwooddelta'` → both **off** = pure engineered-wood = the **sample/reference** path
  (and the end-to-end exerciser for engineered-wood's other format drivers — Iceberg, **Lance** — as they land).

So "two providers" is a *positioning* distinction (different option defaults), zero code duplication. Explicit
options always override the alias default.

Capability wiring reuses the slice-2 pattern: `DeltaCatalog` reports `native_write` on the `ServerInfo`
metadata; the C++ side only needs it if a scan/plan decision depends on it (writes are C#-driven, so likely
no C++ change beyond what `native_read` already added).

## 3. Write path — INSERT / CTAS / COPY

The bulk ABI (`begin_bulk`/`push_batch`/`complete_bulk` → `BulkSession` → `DeltaCatalog.BulkInsert`) is
**unchanged**. Only the data-file write inside `BulkInsert` swaps:

- **engineered-wood today:** `DeltaWriter.Materialize` IPC-round-trips the streamed batches, EW writes the
  parquet file(s) + commits.
- **native_write:** feed the streamed Arrow batches to DuckDB and let it write the parquet:
  `COPY (SELECT * FROM <input>) TO '<root>/<table>/<uuid>.parquet' (FORMAT parquet [, PARTITION_BY (…)] )`,
  then engineered-wood commits the `add`(s) with stats from DuckDB's write output.

### 3.1 The input-binding decision (key plumbing choice)

The batches arrive at C# as Arrow; DuckDB's `COPY` needs to *scan* them. Two options — **pick in the
prototype**:

- **(A) Bind an input Arrow stream into `host_query`** (small ABI extension): `COPY (SELECT * FROM
  __arrownet_input) TO …` where `__arrownet_input` is the C#-exported stream. Zero extra materialization;
  cleanest. Cost: one additive ABI entry (host_query gains an optional bound input stream).
- **(B) Temp Arrow-IPC file:** C# writes batches to a temp `.arrow`, `COPY (SELECT * FROM read_arrow('<tmp>'))
  TO …`. No ABI change, but a double write (IPC temp + parquet). Fine as a first prototype; replace with (A).

Recommendation: prototype with (B) to de-risk the writer/stats/commit quickly, then move to (A) for prod.

### 3.2 Partitioning

`COPY … TO '<dir>' (FORMAT parquet, PARTITION_BY (col,…))` writes the Hive layout DuckDB already supports →
multiple files. engineered-wood records one `add` per file with the partition values (it already reads
`Metadata.PartitionColumns`). The native `PARTITIONED BY` clause (ABI v51) drives this.

**DONE (2026-07-04):** partitioned `native_write` works — EW splits by partition and calls the writer once per
Hive-partition file (`region=US/<uuid>.parquet`). The one wrinkle: DuckDB's **single-file `COPY` does NOT create
the target's parent directory**, so `NativeParquetDataFileWriter` creates `<col>=<value>/` first
(`HostFs.CreateDir`, recursive/idempotent; best-effort — object stores have implicit dirs, so a failure there is
non-fatal since the blob write creates the path). Verified in `verify_delta_catalog_native_write.test`
(per-partition counts + partition-column filter + re-attach). This same parent-dir create is the prerequisite
for native CDF `_change_data/` files (deferred).

## 4. The stats bridge (the one genuinely new piece)

The Delta `add` needs `numRecords` + per-column `{min, max, nullCount}`. Today EW computes them while
writing; now DuckDB owns the file, so we read the stats **back from DuckDB's write output**:

- **Preferred:** `COPY … (FORMAT parquet, RETURN_STATS)` → a result set of `(filename, count, file_size_bytes,
  column_statistics{min,max,null_count,…})` per written file. One shot, no re-read. *(Verify DuckDB 1.5.4
  surfaces column-level stats via RETURN_STATS; if only row_count, use the fallback.)*
- **Fallback:** after the COPY, `SELECT * FROM parquet_metadata('<file>')` and aggregate per-row-group
  `stats_min/stats_max/stats_null_count` to file-level.

C# maps these to engineered-wood's `add.stats` JSON. **This same `count` feeds `baseRowId` assignment** for
row tracking (§6). Delta stats truncation (string min/max prefix, top-level-primitives only) is applied
C#-side to match the spec — cheap, and it's metadata not data.

## 5. DML — DELETE / UPDATE (native rewrite)

Both are **rowid-based copy-on-write** today; native_write swaps the *rewrite* from EW to `COPY`, and — a nice
simplification — lets the value substitution happen **in SQL** instead of the C# `BuildArray`/`ReadScalar`
typed code.

- **DELETE (copy-on-write):** for each affected file,
  ```sql
  COPY (SELECT <datacols>
        FROM read_parquet('<oldfile>', file_row_number => true)
        WHERE file_row_number NOT IN (<deleted positions>))
  TO '<newfile>.parquet' (FORMAT parquet)
  ```
  commit `remove(old)` + `add(new)` (stats from RETURN_STATS). (For a large delete set, the `NOT IN` list
  scales poorly — see §5.1.)

- **UPDATE (copy-on-write):** the new per-row values arrive from DuckDB's update stream (rowid → new SET
  values) as Arrow. Express the substitution as a **LEFT JOIN in SQL** (no C# typed substitution):
  ```sql
  COPY (SELECT COALESCE(u.c1, p.c1) AS c1, …, p.<untouched cols>
        FROM read_parquet('<oldfile>', file_row_number => true) p
        LEFT JOIN <updates_input> u ON p.file_row_number = u.pos)
  TO '<newfile>.parquet' (FORMAT parquet)
  ```
  where `<updates_input>` is the (pos, new-values) rows for that file (bound via §3.1). Types are handled by
  DuckDB — this **retires** `BuildArray`/the typed inverse of `ReadScalar`.

- **DELETE via deletion vectors (`deletion_vectors true`):** no data rewrite — engineered-wood writes the DV
  (roaring bitmap, format already fixed) + commits `remove(path,old-DV)`+`add(path,new-DV)`. **Native writer
  not involved** → this path is unchanged and is the *preferred* delete under row tracking (§6).

### 5.1 Rowid decode invariant (must hold for native rewrite)

The transient rowid is `(fileOrdinal << 40) | file_row_number`, `fileOrdinal` = index in the
**relative-path-sorted** active-file set (`OrderedActiveFiles`), `file_row_number` = physical position. Native
read already computes it. For the native rewrite the decode is identical — C# decodes matched rowids →
`(file, positions)` → the per-file `COPY … WHERE/JOIN` above. The rewrite produces new files with fresh
`file_row_number`s and new ordinals; that's fine because **a transient rowid is valid only within the scanned
snapshot** and is consumed by the same statement (already a documented invariant).

## 6. Rowid vs row tracking — the load-bearing distinction for DML

There are **two** ids, and conflating them is the classic Delta-write bug. Both must be right. And Delta
**row tracking is itself a *pair*** of per-row fields — a row **id** and a row **commit version** — so "get
row tracking right" means getting *both* right, each with the same default-vs-materialize rule.

| | Transient rowid | Stable **row tracking** (Delta `rowTracking`) |
|---|---|---|
| Fields | one: `(fileOrdinal<<40) \| file_row_number` | **two**: row id + row commit version |
| Form | computed | id = `add.baseRowId + file_row_number` (or materialized `_metadata.row_id`); version = `add.defaultRowCommitVersion` (or materialized `_metadata.row_commit_version`) |
| Persisted? | No — recomputed each scan | Yes — on the `add` (defaults) + optionally materialized in the parquet |
| Purpose | **the DML mechanism** (scan→mutate in one snapshot) | **interop** (Spark/Fabric stable ids + per-row "which commit last wrote this row") |
| Native-write concern | rewrite must keep the path-sorted-ordinal decode (§5.1) | `baseRowId`/`defaultRowCommitVersion` assignment + **preservation across rewrite** |

**DML correctness rests entirely on the transient rowid** — it needs *no* Delta feature and is unaffected by
who writes the parquet. This is the existing, validated design (`row_tracking` is *not* a DML mechanism).

**Row tracking is a separate write-side interop feature** that must be maintained *correctly* when enabled.
The key rule for both fields: **the version/id lives on the `add` by default, and is written into the parquet
ONLY when rows within one file diverge.** Native write changes two things:

1. **`add`-level assignment on new files (metadata only — NO parquet column).** Each file owns
   `[baseRowId, baseRowId + numRecords)` where `baseRowId` = (domainMetadata high-water mark + 1), and
   `defaultRowCommitVersion` = the commit version being written (per-file constant). `numRecords` comes from
   DuckDB's **RETURN_STATS** (§4). For a **fresh write / blind append every row shares the commit version, so
   you do NOT write a version column to the parquet** — `defaultRowCommitVersion` on the `add` suffices;
   likewise `baseRowId` covers the ids. This is the "one gap to add in engineered-wood" the multifile-delta
   note flagged — either EW assigns both from the C#-provided row_count + commit version, or C# computes and
   hands them over. DuckDB's parquet bytes are untouched here.

2. **Preservation across copy-on-write (the subtle part — MATERIALIZE both fields).** A rewrite removes
   deleted rows → survivors **shift position** and a rewritten file mixes **survivors** (which must keep their
   *original* row id AND *original* commit version) with **changed rows** (new commit version). So neither
   `baseRowId + position` nor a single `defaultRowCommitVersion` is correct for the whole file → you must
   **materialize both `_metadata.row_id` and `_metadata.row_commit_version` as physical columns** in the
   rewritten parquet (Delta's "materialized row IDs / row commit versions on rewrite"). Concretely the native
   rewrite `COPY` carries two extra computed columns: each survivor's id/version read from the old file
   (physical materialized column if present, else `old.baseRowId + old_position` / `old.defaultRowCommitVersion`),
   and the new commit version for changed rows. Options:
   - **Preferred for the hybrid: DV-mode DELETE + minimize rewrites.** A DV delete does **not** rewrite the
     file → positions, ids, AND commit versions are all preserved *for free*, no materialized columns. This is
     why DV + row tracking compose cleanly and is the recommended delete under row tracking.
   - **When a rewrite is unavoidable (UPDATE, or DV disabled): materialize the pair** as above.

   **Scope note:** step 2 (materialize the id+version pair on rewrite) is the highest-complexity item here. A
   defensible first release: row tracking preserved for **DV-mode DELETE** (free) + **blind INSERT/append**
   (`baseRowId`/`defaultRowCommitVersion` on the `add`, no parquet column, correct), and **UPDATE under row
   tracking documented as not-yet-preserving stable id/version** (transient-rowid DML still correct; only
   external stable-tracking stability across an UPDATE is deferred). Standalone-EW provider keeps its current
   behavior as the reference. Note this also unblocks the DuckLake-style per-row `snapshot_id`
   (= `_metadata.row_commit_version`) that CLAUDE.md records as "not built" — it comes for free once the
   version field is materialized/exposed.

## 7. Change Data Feed (native change-file writes)

CDF stays entirely a `_delta_log`/`_change_data` concern; native write only changes who writes the change
*parquet*:

- **INSERT / blind append:** inferred from `add` — no change file (free), unchanged.
- **DELETE:** the deleted rows are already read for the rewrite/DV → `COPY (SELECT <cols>, 'delete' AS
  _change_type FROM read_parquet('<oldfile>', file_row_number => true) WHERE file_row_number IN (<deleted>))
  TO '<root>/<table>/_change_data/<uuid>.parquet'`. engineered-wood records the `cdc` action.
- **UPDATE:** two change rows per matched row — pre-image (`update_preimage`, from the old file) and post-image
  (`update_postimage`, the new values) — each a native `COPY` with the literal `_change_type` column added.
  Reuse the same `<updates_input>` join as §5.
- **Read side** (`arrownet_delta_changes`) is done and unchanged. The `_commit_version`/`_commit_timestamp`
  come from the always-on `commitInfo` (already emitted).
- **Guard:** CDF capture only when `delta.enableChangeDataFeed` (the `change_data_feed true` catalog option),
  same as today; `DropVirtualRowId` before writing the change file so schemas match (the SIGSEGV fix stays
  relevant — the change file must not carry the transient `_metadata.row_id`).

## 8. Atomicity, commit, credentials — unchanged (engineered-wood owns)

The commit protocol stays 100% engineered-wood: temp-file + `EXCLUSIVE_CREATE` put-if-absent rename (host-FS
`fs_*` v48/`RenameAsync`), OCC retry (`DeltaConflictException` → reopen at latest, bounded), the always-on
`commitInfo`. Cloud writes: DuckDB's `COPY … TO 'onelake://…'` uses the v56 OneLake write callbacks (+ S3 via
an S3 secret); engineered-wood's log writes use the host-FS bridge. **No change to the transaction/opener
threading** (`SetActiveOpener` before the bulk begin; `AmbientOpener` re-established on the consumer thread).

## 9. Validation — the acid test

The whole point is standard-readability, which EW's writer kept failing. Every prototype slice must pass:

1. **delta-kernel-rs reads it** — via DuckDB's *official* `delta_scan` on the native-written table (the
   reference reader Spark/Fabric use). This is the gate EW's writer repeatedly failed (footer/`path_in_schema`).
2. **Fabric OneLake conversion + SQL-endpoint query** — write to a live lakehouse, confirm it registers and is
   queryable (the end-to-end check we've been doing for DV/CDF/ICT). **VALIDATED LIVE (2026-07-04):** on
   workspace `Test` / lakehouse `LH` (schema-enabled), `native_write true` CTAS + INSERT + DELETE + UPDATE on
   `lake.dbo.arrownet_nwtest` all round-tripped correctly over `onelake://` (via the v56 OneLake write
   callbacks + DuckDB's native parquet writer), and a 5000-row low-cardinality table showed the
   **DuckDB-native-writer bloom signature** on the dict-encoded column via `parquet_metadata('onelake://…')`
   (engineered-wood writes no bloom). Fabric-portal conversion / SQL-endpoint query is the user's final
   confirmation, as for the DV/CDF/ICT paths.
3. **Round-trip parity** — our own reader + the standalone-EW provider read the native-written table with
   identical results (incl. decimals, temporal, the types EW's codec mangled).
4. **Bloom filter present** — confirm DuckDB auto-wrote bloom filters (`parquet_metadata` shows
   `bloom_filter_offset`), the concrete maturity win.

## 10. Phasing

- **P0 — spike: format-level acid test DONE (2026-07-04).** Proven with zero C#/EW code: DuckDB-written
  parquet + a **hand-authored `_delta_log`** (protocol v1/2 + metaData + commitInfo + `add`) is read correctly
  by the **official `delta_scan` (delta-kernel-rs — the reference reader Spark/Fabric use)** — 5/5 rows, exact
  types + aggregation. This is the test EW's own writer repeatedly failed; **DuckDB's parquet is
  standard-readable out of the box.** Feasibility gates all green (see below). *Remaining P0→P1 = wire it
  through the EW `WriteCoreAsync` seam + the Arrow→COPY input path — productionization, now known-feasible.*
  **Findings:**
  - **Bloom** (`WRITE_BLOOM_FILTER true`): DuckDB writes bloom filters **only for dictionary-encoded columns**
    (`FinalizeWrite`/`FlushDictionary`, `templated_column_writer.hpp:294`) — i.e. low/medium-cardinality
    lookup/join columns, exactly where bloom helps; high-cardinality all-distinct columns are (correctly) not
    dict-encoded → no bloom. It was encoding-gated, **not** `PARQUET_VERSION` (V2 made no difference). Pass
    `WRITE_BLOOM_FILTER true` on the COPY.
  - **Stats bridge is simpler than §4 assumed** for the bulk path: the streamed Arrow batches are in hand, so
    stats come from EW's existing `StatsCollector.Collect(batches)` — **no `RETURN_STATS` needed** there.
    `RETURN_STATS`/`parquet_metadata` is only for a pure rewrite where we don't re-hold the batches.
  - **Gate A (EW external-file commit) is clean:** `AddFile` is a `public record`, `TransactionLog`.
    `WriteCommitAsync(version, actions)` is public, and `WriteCoreAsync` (which already assigns `BaseRowId`,
    stats, the `AddFile`, and commits) is the surgical injection seam — swap "EW writes the parquet" for "an
    injected writer (DuckDB COPY) produces the file + path/size," everything else unchanged.
- **P1 — INSERT/CTAS/append DONE (2026-07-04, C#-only + one EW seam, no ABI/C++).** The `native_write true`
  ATTACH option routes INSERT/CTAS/append through DuckDB's native parquet writer; the `_delta_log` commit stays
  in engineered-wood. **Input binding used gate (A) directly** — the existing `Host.Query(sql, inputs)`
  (host-query v42–v45) binds the streamed Arrow batch as a connection-scoped view, so the temp-IPC gate (B) was
  skipped entirely. **The EW seam** = `DeltaTableOptions.DataFileWriter` (a new `IDataFileWriter` interface):
  when set, `WriteCoreAsync` delegates each partition file's parquet bytes to it instead of the built-in
  `ParquetFileWriter` — everything else (partition split, row tracking, column mapping, stats, the `add` action,
  the commit, OCC retry) is unchanged (the override is null by default → the default path is byte-identical).
  ArrowNet's `NativeParquetDataFileWriter` runs `COPY (SELECT * FROM <bound batch>) TO '<root>/<file>' (FORMAT
  parquet, WRITE_BLOOM_FILTER true, RETURN_STATS)` on a fresh host connection and reads back `file_size_bytes`
  for `add.Size`; stats come from EW's own `StatsCollector.Collect(batches)` (batches in hand — no RETURN_STATS
  dependency for stats, per the P0 finding). URIs reuse `DeltaReader.ToReadableRoot` (onelake:// rewrite). **§9
  acid test PASSED locally:** the official `delta_scan` (delta-kernel-rs) reads the native-written table (6 rows,
  exact `DECIMAL(10,2)` + `DATE`), and bloom filters flow through on dict-encoded columns (`grp` 5-distinct →
  `has_bloom=true`; `id` all-distinct → none — the concrete maturity win, and a deterministic native-write
  signature since EW's default writes no bloom). `test/verify_delta_catalog_native_write.test` (36); the default
  (EW-codec) path unregressed (write/decimal/temporal/partition/overwrite_merge/update/delete all green).
  **Opt-in only (default off); the `delta`-alias default-on is deferred** (needs the resolved provider name
  threaded to the `DeltaCatalog` ctor — a separate, mechanism-independent policy step). DELETE/UPDATE rewrites
  still use EW's codec (P3/P4). Live OneLake/Fabric validation of the native-write path is pending.
- **P2 — input-binding (A) DONE as part of P1** (gate B was never needed — `Host.Query(inputs)` already exists).
- **P3 — DELETE copy-on-write rewrite DONE (2026-07-04, native WRITE half).** Under `native_write true` the
  copy-on-write DELETE rewrites the surviving rows into a **DuckDB-written** parquet file (the same
  `IDataFileWriter` seam, now generalized to a batch **list** so the rewrite's per-file survivor batches write
  as one file); engineered-wood still selects/reads the affected files and commits `remove(old)+add(new)`. The
  **read half stays EW's reader** (survivor filtering via `TakeRows`, decimal-correct since the earlier fix) —
  the fully-native `read_parquet … WHERE file_row_number NOT IN (…)` rewrite (§5, retiring EW's data-file read)
  is a later refinement. **DV-mode DELETE is unchanged** (no data rewrite → native writer N/A). §9 acid test
  PASSED locally (10 rows → delete evens → the official `delta_scan` reads the 5 survivors with exact decimals).
  `test/verify_delta_catalog_native_write.test` (48 — incl. DELETE + compose + durability); default-path
  delete/dv/update unregressed. CDF delete change files + fully-native rewrite still open.
- **P4 — UPDATE copy-on-write rewrite DONE (2026-07-04, native WRITE half).** Under `native_write true` the
  UPDATE rewrite writes the modified file with DuckDB's native writer (same list-form seam at the UPDATE site);
  EW reads the affected files, applies the SET substitution (still the C# `BuildArray` inverse of `ReadScalar`
  — the SQL-join substitution to retire it is the deferred refinement), and commits `remove(old)+add(new)`. §9
  acid test PASSED locally (constant/string/expression updates → `delta_scan` matches, exact decimals). Test +
  33 assertions in `verify_delta_catalog_native_write.test` (now 68); default-path update/delete/dv/changes
  unregressed.
- **P3+P4 fully-native REWRITE (read half) DONE (2026-07-04, commit `143c09d`).** DELETE/UPDATE now read the
  source AND apply the row-level transform in DuckDB SQL, not just write natively. New engineered-wood seam
  **`IDataFileRewriter`** (`DeltaTableOptions.DataFileRewriter`): the copy-on-write DELETE/UPDATE loops delegate
  the source read + transform to it for the clean shape (no column mapping, no partitions, no type widening, no
  CDF), falling back to EW's own reader + in-process transform otherwise. ArrowNet's `NativeParquetDataFileRewriter`
  runs, per affected file, `SELECT <cols> FROM read_parquet(src, file_row_number => true) WHERE file_row_number
  NOT IN (<deleted positions>)` (DELETE) or a `LEFT JOIN` against the (position → new SET values) rows bound as an
  Arrow view, substituting via `CASE` (UPDATE) — **retiring the in-process typed `BuildArray`** for the native
  path. **DV-aware** (the file's existing deletion-vector positions are passed as `excludePositions`, so a
  copy-on-write UPDATE on a DV table rewrites only live rows) and **schema-evolution backfill** (the rewriter
  probes the source file's columns and emits a typed `NULL` for a column ADDed after the file was written — no
  gate needed). **Stats stay EXACT via EW's `StatsCollector`** on the returned batches: `RETURN_STATS` /
  `parquet_metadata` surface DECODED VARCHAR min/max, whose float/double rounding could skip live rows (violates
  the exact-or-nothing rule) — so the stats bridge is StatsCollector-on-the-batches, NOT the footer strings. §9
  acid test PASSED (official `delta_scan` reads the native-rewritten table — exact decimal/date/double, DELETE +
  UPDATE applied) AND **VALIDATED LIVE on Fabric OneLake** (`read_parquet('onelake://…')` source read + native
  write over `onelake://`, DELETE + UPDATE round-trip, decimals exact). `verify_delta_catalog_native_write.test`
  (147 — native DELETE/UPDATE + compose, DV-table UPDATE, schema-evolution UPDATE, durability); default-path +
  delete/update/dv/changes/partition suites unregressed. **Still deferred: CDF pre/post-image native change files
  (CDF tables keep the EW-read path) + P5-rewrite (materialize id/version on rewrite).**
- **P5 — row tracking**: **the APPEND half already works FREE on the native path (verified 2026-07-04).**
  With `row_tracking true` + `native_write true`, engineered-wood materializes the row-id column into
  `physicalBatch` and assigns `baseRowId`/`defaultRowCommitVersion` on the `add` BEFORE the writer is called, so
  DuckDB's writer simply emits the bytes: commit-0 declares writer-v7 + `rowTracking` + `delta.enableRowTracking`,
  the two write commits get `baseRowId:0` (CTAS, 5 rows) / `baseRowId:5` (append), and the official `delta_scan`
  (delta-kernel-rs) reads it. `verify_delta_catalog_native_write.test` covers it (87 assertions). **Still open:**
  the *rewrite* half (§6.2) — materialize the `_metadata.row_id`+`_metadata.row_commit_version` pair on
  copy-on-write DELETE/UPDATE so survivors keep their original id/version (today the rewrite doesn't preserve
  stable ids across UPDATE); DV-mode delete preserves them free. (Also unblocks per-row `snapshot_id`.)
- **DV is now the DEFAULT DML mode (2026-07-04, commit `66e97f5`)** — see the CLAUDE.md Delta bullet. This
  RELOCATES the row-tracking-on-rewrite gap: DV DELETE preserves ids/versions for free (no rewrite), so the only
  rewrites left are copy-on-write UPDATE (opt-out / DV-table) and **compaction**. Compaction
  (`CompactionExecutor`) is **not wired into this provider** (no OPTIMIZE command — latent) AND currently would
  BREAK stable ids for a spec reader: it assigns a fresh `baseRowId` per compacted file and EW does not declare
  `delta.rowTracking.materializedRowIdColumnName`, so delta-kernel/Spark compute ids from `baseRowId + position`
  and every row's id changes (it copies the physical `__delta_row_id` through, but undeclared → readers ignore
  it). Correct compaction under row tracking = declare the materialized row-id + row-commit-version column names
  in metadata + materialize BOTH on the rewrite (mixed source files → no single `baseRowId`) — a focused EW
  slice needing delta-kernel validation. Same materialize-on-rewrite machinery would then also close the P5
  copy-on-write-UPDATE gap above.

Each slice: build, `verify_delta_catalog_*` green, then the §9 acid test.

## 11. Risks / open questions

- **RETURN_STATS coverage** — does DuckDB 1.5.4's `COPY … RETURN_STATS` surface per-column min/max/null_count,
  or only row_count? Decides §4 (one-shot vs `parquet_metadata` re-read). *Verify in P0.*
- **Input binding** — (A) needs a host_query input-stream ABI entry; confirm the exporter/consumer lifetime
  across the `COPY`. (B) works now but double-writes.
- **Stats-truncation parity** — Delta's string min/max truncation + top-level-primitive-only rule must be
  applied C#-side so external readers' data-skipping stays correct.
- **Row-tracking preservation across UPDATE** — the materialized-row-id path (§6.2) is the hardest item;
  scope it explicitly (DV-first, or defer UPDATE stable-id preservation).
- **Two round-trips for rewrite** — DELETE/UPDATE read via `read_parquet` and write via `COPY` on the host
  connection; confirm no opener/txn re-entrancy issue (host_query runs on a fresh connection — already proven
  re-entrancy-safe).
- **Decimal/temporal round-trip** — validate the types EW's codec mangled now write correctly via DuckDB
  (expected, since it's DuckDB's writer, but it's the whole point — test explicitly).

## 12. What moves vs what stays

| Concern | Native write | engineered-wood |
|---|---|---|
| parquet data-file **bytes** (encodings, bloom, stats, footer) | ✅ DuckDB | — |
| change-data parquet bytes | ✅ DuckDB | — |
| deletion-vector bitmap bytes | — | ✅ EW (format fixed) |
| `_delta_log` actions / protocol / OCC / commitInfo | — | ✅ EW |
| `add.stats` **values** | ✅ (from RETURN_STATS) | ✅ EW assembles the action |
| `baseRowId` + `defaultRowCommitVersion` / domainMetadata high-water | ✅ (row_count + commit version) | ✅ EW assigns/records |
| materialized `_metadata.row_id` + `_metadata.row_commit_version` (rewrite only) | ✅ DuckDB writes the computed columns | ✅ EW records on the `add` |
| CDF `cdc` action / snapshots / time travel | — | ✅ EW |
| file listing / DV read / partition values (read) | ✅ DuckDB reads, EW lists | ✅ EW |

Net: engineered-wood shrinks to the **log/protocol/DV-bitmap** layer (stable, spec-defined, Fabric-validated);
every parquet byte — read and write — is DuckDB's. That is the production risk posture you want, and pure-EW
remains the standalone sample/driver-test path.

## 13. Cross-check: DuckLake, and a future DuckLake → Delta bridge

DuckLake (`D:\repos\ducklake`) independently confirms the §6 row-tracking model. It's a catalog-driven
lakehouse (parquet data; metadata catalog = DuckDB or Postgres) where `row_id_start` per data file +
`begin_snapshot`/`end_snapshot` bounds live in the **catalog DB**, and `rowid`/`snapshot_id` are **virtual
columns** (`DuckLakeMultiFileReader`, `COLUMN_IDENTIFIER_SNAPSHOT_ID`) **constructed at query time** from
`row_id_start + file_row_number` — *not* stored on a plain INSERT — then **materialized into the parquet on a
rewrite** (compaction sorts by `(row_id, snapshot_id)`; DML likewise). That is exactly Delta's
`baseRowId`/`defaultRowCommitVersion`-on-the-`add` + construct-at-read + materialize-on-rewrite pattern; the
sole difference is **where the bounds live** (a SQL catalog vs the `_delta_log`). Strong independent
validation that this design's "default on the metadata, materialize on rewrite" rule is the industry norm.

**Future direction (not this project): DuckLake → Delta as a metadata-only publish.** Because both formats
store plain parquet and derive rowids virtually, a DuckLake table can be exposed as a Delta table **without
rewriting data** — read DuckLake's catalog (`row_id_start`, snapshot bounds, file list + stats) and emit a
spec-compliant `_delta_log` over the *same* parquet files (`row_id_start → baseRowId`, snapshot bounds →
`defaultRowCommitVersion`, add/remove/DV/CDF/snapshots). **The "engineered-wood = pure metadata layer"
isolation this design produces is exactly the reusable component for that bridge**: a Delta-log writer that
takes catalog facts and emits the log, decoupled from who wrote the parquet. Worth keeping that seam clean
(catalog-facts-in → `_delta_log`-out, no parquet dependency) so it can be lifted into that future project.
