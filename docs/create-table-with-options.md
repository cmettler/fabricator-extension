# `CREATE TABLE … WITH (…)` options + SQL Server external tables (design)

Status: **ALL FOUR SLICES DONE (2026-07-19).** A (ABI v67) → C → D → B, ordered by value/risk.
Origin: user request — DuckDB parses a `WITH (key='value', …)` clause on CREATE TABLE / CTAS
(the Iceberg-style `WITH (location=…, table_type=…, format=…)` shape); we should surface those
options to the C# providers to (1) set Delta TBLPROPERTIES + parquet write tuning per table,
(2) create SQL Server **external tables** over S3 with auto-provisioned credentials/file format,
and (3) detect existing external tables in the SQL catalog and route writes to their storage
via the Delta provider (or a plain parquet COPY).

```sql
-- the motivating shapes
CREATE TABLE lake.main.woot
  PARTITIONED BY (i)
  SORTED BY (j)
  WITH (parquet_compression='zstd', parquet_row_group_size=1000000)
AS FROM range(9001) t1(i) SELECT i, i+1 AS j;

CREATE TABLE db.dbo.trips
  WITH (location='s3://minio:9000/fabricator/polybase/trips', table_type='DELTA')
AS SELECT …;   -- writes Delta to S3 client-side + provisions the SQL external table
```

## Verified facts (grounding)

- **DuckDB v1.5.4 already parses the clause**: `CreateTableInfo::options` is a
  `case_insensitive_map_t<unique_ptr<ParsedExpression>>`
  (`duckdb/src/include/duckdb/parser/parsed_data/create_table_info.hpp:37`), sitting right next to
  `partition_keys`/`sort_keys` (which we already thread, v51/v52). Our
  `FabricatorCatalog::SupportsCreateTable` currently **rejects** any non-empty `options`
  (`src/catalog/fabricator_catalog.cpp:296`). So slice A is the exact `PARTITIONED BY` precedent:
  permit → extract → thread across the ABI.
- The two ABI entries that must carry the options: `create_table` (`abi.h:269`, empty CREATE) and
  `begin_bulk` (`abi.h:354`, CTAS/COPY/INSERT bulk). Both already grew nullable string params the
  same way (`partition_columns` v51, `sort_columns` v52, `schema_mode` v54) — **one bump, v67**.
- C# seams: `IBackendCatalog.CreateTable(…)` + `BulkInsert(…)` (`dotnet/Fabricator.Abstractions/IBackend.cs:159/:271`)
  gain a `string? optionsJson`; Delta write tuning resolves in `DeltaCatalog.ResolveWriteSpec`
  (`DeltaCatalog.cs:450` — precedence today: ATTACH defaults < `delta_write_options` session setting);
  Delta table config stamps in `DeltaWriter.CreateConfig`; per-table property DDL already exists
  (`delta.set_tblproperties` — a catalog-bound function since ABI v70; it rode metadata kinds 13/14 until then).
- **SQL Server external tables are read-only for SQL Server itself** (no INSERT into an S3 external
  table; CETAS can export parquet/CSV but can NEVER write Delta). External tables appear in
  `sys.tables` with `is_external = 1` (so our existing discovery/scan already lists + reads them);
  the config lives in `sys.external_tables` (location, `data_source_id`, `file_format_id`) →
  `sys.external_data_sources` (location = `s3://host:port/`) → `sys.external_file_formats`
  (`format_type` = PARQUET / DELTA / CSV). Credentials are NOT retrievable from SQL Server —
  the client-side write must use a DuckDB s3 secret.
- SQL Server's DELTA reader is **protocol 1.0 only** (pinned in `verify_mssql_s3_polybase`): a
  SQL-readable Delta table needs `deletion_vectors false, column_mapping 'none'`. Plain appends and
  CoW DML keep such a table readable (pinned).
- Cross-provider delegation is already possible **purely in C#**: `BackendRegistry.Resolve("delta")
  .OpenCatalog(root, optionsJson)` returns an `IBackendCatalog` (the same transient-catalog shape
  the `COPY … (FORMAT delta)` C++ path uses, `src/copy/fabricator_copy.cpp` — root/leaf split, flat
  layout, `copy_disposition` riding the options JSON, park-append + `CommitTransaction` flush).
  `BulkSession` re-establishes `AmbientOpener`/`AmbientTransaction` on its consumer thread, so a
  nested Delta write inside `SqlServerCatalog.BulkInsert` sees a live opener + txn id.

---

## Slice A — WITH-options plumbing + Delta per-table properties/write tuning (ABI v67) — **DONE**

Built 2026-07-19 as planned, plus three findings the build surfaced:

- **DuckDB's parser LOWERCASES every WITH key** (`transformer.cpp TransformTableOptions` — quoting does
  not help), but Delta config keys are case-sensitive. Well-known `delta.*`/`fabricator.*` keys are
  re-cased C#-side from a canonical list (`DeltaWithOptions.CanonicalKeys` — isolationLevel,
  targetFileSize, appendOnly, retention/durations, dataSkipping*, parquet.compression.codec, …);
  arbitrary mixed-case custom keys must use `delta.set_tblproperties` (JSON preserves case).
- **Boolean literals arrive as postgres `'t'/'f'`** (a bare `true` parses as `CAST('t' AS BOOLEAN)` and
  the constant extraction unwraps one CAST level) — the bool parser accepts them.
- **Pre-existing gap closed en route: the native_write COPY paths carried NO write tuning** —
  `delta_write_options` compression only reached the EW codec writer. `RunCopy`/`RunCopyPartitioned` +
  the per-file `NativeParquetDataFileWriter` now render `COMPRESSION`/`ROW_GROUP_SIZE` from the resolved
  spec (`CopyTuning`), so tuning applies uniformly (bloom-filter COLUMNS remain codec-only — DuckDB's
  writer blooms dictionary-encoded columns automatically). Note DuckDB's writer has a row-group flush
  floor: `parquet_row_group_size` below ~2048 coalesces.

Verified: `test/verify_with_options.test` (68) + `test/verify_with_options_mssql.test` (4) + a
14-suite regression sweep (see the CLAUDE.md bullet). Original design below.

### Surface (Delta provider)

| Key | Meaning |
|---|---|
| `parquet_compression` (alias `compression`) | parquet codec for this table's writes (`zstd`, `snappy`, …) |
| `parquet_row_group_size` (alias `row_group_size`) | rows per row group |
| `parquet_bloom_filter_columns` (alias `bloom_filter_columns`) | comma list |
| `deletion_vectors`, `column_mapping`, `row_tracking`, `change_data_feed`, `in_commit_timestamps` | **per-table override of the ATTACH create flags** — same value syntax as the ATTACH options |
| `delta.*` / `fabricator.*` | TBLPROPERTIES merged into the CREATE's table configuration (one commit — kills the create-then-`set_tblproperties` two-step). `delta.isolationLevel` included. |
| `table_type='DELTA'`, `format='parquet'` | accepted as **validated no-ops** (portability with the Iceberg-style sample); any other value errors |

Explicitly rejected with pointers:
- `delta.enable*` / `delta.columnMapping.*` spellings → "use the `deletion_vectors` / `column_mapping` /
  … WITH keys" (one spelling per feature; these need protocol-declaration wiring, which the explicit
  keys already have — mirrors the `set_tblproperties` guard).
- `partitioned_by` / `sorted_by` → native clauses exist.
- `location` → on the Delta folder-catalog, path = table identity; placing a table elsewhere breaks
  discovery. (Meaningful on the SQL provider — slice B.)
- **Any unknown key → error, never silently ignored** (the PARTITION_OVERWRITE precedent).

**Precedence:** WITH (per table, strongest) > `delta_write_options` session setting > ATTACH defaults.
The per-table create-flag overrides make the PolyBase/Fabric-endpoint recipe a one-liner without a
dedicated ATTACH: `CREATE TABLE lake.main.t WITH (deletion_vectors=false, column_mapping='none') AS …`.

Other providers in this slice: SQL Server / DAX / deltars **reject** any non-empty options they don't
recognize (SQL Server starts recognizing keys in slice B; DAX is read-only anyway).

### Changes

- **C++**: `SupportsCreateTable` permits `options` (drop the rejection); new
  `fabricator::TableOptionsArg(const case_insensitive_map_t<unique_ptr<ParsedExpression>>&)` beside
  `PartitionColumnsArg` (`fabricator_partition_util.hpp`) — accepts **ConstantExpression values only**
  (else "WITH option values must be constants"), emits a flat JSON object with ALL values as strings
  (`{"parquet_compression":"zstd","parquet_row_group_size":"1000000"}` — the C# ParseXxxOption
  helpers parse strings). Wire at both creation sites: `PlanCreateTableAs` → `FabricatorCtasInfo`
  (`fabricator_catalog.cpp:314` area, threaded through `fabricator_ctas.cpp:70` → `begin_bulk`) and
  the DDL `FabricatorSchemaEntry::CreateTable` (`fabricator_schema_entry.cpp:2365` area → `create_table`).
- **ABI v67** (signature change, lockstep): `create_table(…, const char *options_json)` +
  `begin_bulk(…, const char *options_json)` (nullable). `clr_host` wrappers get a defaulted param;
  `Abi.cs` delegates + `Bootstrap.cs` handlers + `AbiVersion = 67`; `IBackendCatalog.CreateTable` /
  `BulkInsert` gain `string? optionsJson`; `StubBackend` + all providers updated. Rebuild the
  **loadable** too (the dbt stale-loadable trap).
- **C# Delta**: `ResolveWriteSpec` gains a per-statement WITH layer (parsed from optionsJson, wins per
  key); `DeltaCatalog.CreateTable`/`BulkInsert` compute effective create flags = WITH override ??
  catalog default, thread `delta.*`/`fabricator.*` extras into `DeltaWriter.Create*` → `CreateConfig`
  merge (WITH wins over derived keys; validated like `set_tblproperties`).
- Note: WITH exists only on CREATE/CTAS — plain INSERT `begin_bulk` passes null (per-statement write
  tuning for inserts stays `delta_write_options`).

### Tests (`test/verify_with_options.test`)

Compression/row-group pinned via `parquet_metadata()` on the written file; per-table
`deletion_vectors=false, column_mapping='none'` override pinned via the commit protocol shape (in a
DV-default catalog); TBLPROPERTIES read back via `delta.tblproperties` (one commit total);
precedence over `delta_write_options`; guards (unknown key, `delta.enable*` pointer, non-constant
value, `location` on delta, SQL Server unknown key). Regression: partition 54 / sorted_by 30 /
tblproperties 42 / native_write 147 / copy_format 109.

---

## Slice C — external-table detection + write routing (C#-only, **no ABI**) — **DONE**

Built 2026-07-19 as planned. Notes from the build:

- The routing lives in Bridge (`ExternalTableRouting` — public, so the SqlServer assembly reaches
  `Host.Query`/`BackendRegistry` without widening internals): `AppendDelta` = transient delta catalog
  over the parent folder (flat layout + native_write) → `BulkInsert` parks the append →
  `CommitTransaction()` flushes it as ONE Delta commit (the C# mirror of the `COPY (FORMAT delta)`
  finalize; `RollbackTransaction` on failure = discard-only backstop); `AppendParquet` = one
  `COPY … TO '<folder>/<uuid>.parquet'` host query.
- Detection: `SqlServerCatalog.DetectExternalTable` — lazy probe (`sys.external_tables` →
  data source → LEFT JOIN file format), cached positive AND negative per table (a normal table's
  INSERT never pays a repeat metadata query), profile-tolerant (probe failure ⇒ not external),
  invalidated on DROP/CREATE/replace through the catalog.
- Explicitness tracking: `BeginTransaction(isExplicit)` now records explicit txn ids (the ambient id
  is set by `set_active_txn` immediately before — verified in `fabricator_transaction.cpp`); the
  external INSERT rejects inside an explicit transaction.
- `DROP TABLE` on a detected external table emits `DROP EXTERNAL TABLE` (OBJECT_ID-guarded for
  IF EXISTS); `INSERT … RETURNING` rejects (no OUTPUT INSERTED rows on the storage path).
- Test conventions: an exec-created external table needs `fabricator_refresh_cache` before catalog
  access (existing §4 convention); parquet-INSERT pins are accumulation-tolerant (uuid files persist
  across re-runs on the shared bucket).
- Environment repair en route: the running SQL Server container was the PRE-compose `mssql-arrownet`
  while MinIO was on the new compose network — SQL couldn't resolve `minio:9000` (error 13807). The
  compose `mssql-fabricator` service is now the live one (old container stopped, provision re-run).

Verified: `verify_mssql_s3_polybase.test` **167** (new §6 Delta INSERT round-trip via OPENROWSET +
catalog scan + our own delta view, explicit-txn + RETURNING guards, §6b parquet INSERT, §6c DROP
EXTERNAL TABLE routing with data-stays pin) + regression: SQL fn suites (scalar 26 / table 33 /
procs 24 / proc_inout 31 / table_inout 63 / functions 13), delta s3 161, connection_mode 20,
time_travel 14, orderby 7. Original design below.

### Detection

Lazy + cached per table, at first write-path touch (NOT per entry materialization — no per-table cost
on ATTACH): `SqlServerCatalog.BulkInsert`/`DropTable` on cache miss run one metadata query
(`sys.external_tables et JOIN sys.external_data_sources eds JOIN sys.external_file_formats eff WHERE
et.object_id = OBJECT_ID(@qualified)`) → `ExternalTableInfo(location, dataSourceLocation, formatType)`.
Scans/discovery are UNCHANGED (external tables already list via `sys.tables` and read through SQL
Server — its reader, its pushdown). Fabric Warehouse/Synapse: no S3 data virtualization → the probe
returns empty → normal path (profile-tolerant by construction).

### INSERT routing

Compose the table URI: `dataSourceLocation` (`s3://host:port/`) + table `location` (`/bucket/path/t`)
→ **parse to `s3://bucket/path/t`, DISCARDING the host** — the endpoint SQL Server uses is from ITS
network perspective (`minio:9000`); the client-side write resolves a **DuckDB s3 secret by scope**
on `s3://bucket/…` and takes the secret's `ENDPOINT` as authoritative (this is exactly the
`FABRICATOR_S3_ENDPOINT` vs `FABRICATOR_S3_SQL_ENDPOINT` split the polybase test already models).
No matching secret → clean error naming the URI.

- **DELTA**: split `<root>/<leaf>`; `BackendRegistry.Resolve("delta").OpenCatalog(root,
  options: flat layout + native_write)`; delegate `inner.BulkInsert("main", leaf, data,
  createTable:false, replace:false, …, txnId, …)`; then `inner.CommitTransaction()` (flushes the
  parked append as ONE Delta commit — the C# mirror of the `COPY (FORMAT delta)` finalize) +
  dispose. The ambient opener/txn are live on the bulk consumer thread (BulkSession re-establishes
  them). Appends never change table features, so a protocol-1.0 SQL-readable table STAYS readable;
  a DV/mapped external Delta table was already SQL-unreadable — `HonorWriterFeatures` governs
  writability as usual.
- **PARQUET**: write ONE new parquet file into the folder —
  `COPY (SELECT * FROM <stream>) TO '<uri>/<uuid>.parquet'` via the host-query seam (visibility note:
  `HostFs.Query` is Bridge-internal; expose a small Bridge-public helper for the SqlServer assembly,
  or place the routing helper itself in Bridge). S3 PUT visibility is atomic (multipart completes
  atomically); the external table reads all files under its LOCATION, so the rows appear on commit.
  Schema must match the declared external-table columns — validated by name/type against the INSERT
  stream (which DuckDB already bound to the SQL-declared columns).

### Semantics + guards

- **Statement-atomic, not txn-joined**: the storage write is its own Delta commit / parquet PUT and
  cannot roll back with a DuckDB transaction on the SQL catalog → **reject inside an explicit
  transaction** with a clean error (the SORTED_COLUMNS-in-txn precedent). Autocommit only, v1.
- UPDATE / DELETE on an external table WITHOUT an identity-keyed Delta target (see slice D), and
  ALTER always: intercept with a clear error pointing at a direct Delta ATTACH of the location
  (better than SQL Server's own error).
- **DROP TABLE** on a detected external table emits `DROP EXTERNAL TABLE` (SQL Server rejects plain
  `DROP TABLE` for them) — metadata-only, data stays (document; no purge in v1).
- Type risk: the external table's declared SQL types were mapped from the storage schema by whoever
  created it; the Delta write re-matches by name and errors on incompatibility — surfaced, not silent.

### Tests

Extend `test/verify_mssql_s3_polybase.test` (same env gates): create the external Delta table over
our S3 table (existing flow) → `INSERT INTO db.dbo.ext_t VALUES …` through the ATTACHed catalog →
read back via the external table AND OPENROWSET shows the new rows; the `_delta_log` gained exactly
one append commit; parquet-format external table INSERT (new file, rows visible); explicit-txn
rejection; DROP routes to `DROP EXTERNAL TABLE`; no-matching-secret error.

---

## Slice D — identity-keyed UPDATE/DELETE routing (extends C, C#-only, no ABI) — **DONE**

Built 2026-07-19 as designed. Notes:

- Resolution rides the **catalog surface**, not internal seams: `ExternalTableRouting.ResolveRowIds`
  builds a `ScanSpec` (`{"columns":[<id>,"_metadata.row_id"],"filter":{"op":"in",...}}` + a 1-row Arrow
  value batch, 500 ids/chunk) against the transient delta catalog — the IN predicate prunes files via the
  identity column's standard stats; the wanted-set re-filter client-side is the exact predicate
  (pushdown is superset-safe).
- UPDATE alignment without row surgery: the rebuilt update stream keeps the SET columns and swaps the
  key column for resolved transient rowids, with **NULL for unresolved ids** — the delta update parser
  skips NULL rowids, which IS the concurrently-deleted-matches-nothing semantics for free.
- The identity probe (`FindDeltaIdentityColumn` — `delta.identity.*` field metadata via
  `SchemaConverter.FromArrowSchema`) rides the cached external-info probe; the rowid override is a
  `GetMetadata(RowId)` branch returning the identity column name (the standard identity-as-rowid
  machinery does the rest — zero C++).
- Guards live: SET-of-identity rejected; explicit-txn rejected (shared `GuardExternalDml`).

Verified: `verify_mssql_s3_polybase.test` **209** (§6d — UPDATE via OPENROWSET-backed scan + Delta CoW
apply, expression UPDATE, DELETE, ids preserved, both guards, zero-match DELETE) + regression
identity 64 / columnstore 20 / with_options_mssql 4 / delta s3 161 / arrow_lossless 10.
Original design below.

Slice C scoped UPDATE/DELETE out because the rowid domains don't mix: the scan runs through SQL
Server (its rowid = PK/unique/identity of a *SQL* table — an external table has none), while the
Delta provider's DML wants its transient `(fileOrdinal << 40) | position` rowid, which only OUR
scan of the Delta log can produce. **A Delta IDENTITY column dissolves this**: it is a real data
column (PolyBase reads it — pinned), engine-assigned unique (HWM-tracked, OCC-safe), trivially
stable across every rewrite (rewrites copy data verbatim), and it has **standard min/max stats in
the Delta log** — so it is a cross-system key both sides can see and the Delta side can prune on.

### Mechanism

- **Detection** (rides the slice-C external-info probe): when the external table's Delta target
  declares an identity column (`delta.identity.*` field metadata on a BIGINT column, read via one
  cached `DeltaReader.GetSchema`), the entry advertises that column as its **rowid**
  (`GetMetadata(RowId)` override — the same identity-as-rowid shape regular SQL tables already
  use, so zero new C++ plumbing; the scan serves it as a normal projected column through PolyBase).
- **`ExecuteDelete(rowids)`** (rowids = identity values): resolve identity → transient rowid on
  the Delta side with a chunked pruned scan (`WHERE <id> IN (…)` → `StreamWithRowIds` projecting
  the identity column; file skipping via the identity column's standard stats — the reason identity
  is the right key), then the existing `DeleteByRowIds*` (DV or CoW per table config; the
  PolyBase-recipe table is DV-off → CoW → stays protocol-1.0 readable, pinned). One Delta commit
  per statement.
- **`ExecuteUpdate(rowids, values)`**: same resolution keyed per row (identity → position), then
  `UpdateByRowIds` with the post-images. SET of the identity column itself is rejected
  (engine-assigned).
- **Why this is semantically SOUND, not just convenient**: identity values are
  **snapshot-independent** — the SQL-side scan and the Delta-side DML do not need to agree on a
  version (unlike transient rowids, which are only valid within one snapshot). A row concurrently
  deleted between scan and DML simply matches nothing; the per-statement OCC retry is safe, exactly
  like identity appends. Bridging *position* rowids across the two systems would never have been
  correct; bridging identity is.

### Guards + trust

- No identity column on the Delta target → UPDATE/DELETE stay rejected (slice C's error, now
  pointing at "declare an IDENTITY column or use a direct Delta ATTACH").
- Autocommit-only, statement-atomic (slice C semantics); explicit-txn rejection.
- Trust assumption: identity uniqueness relies on all writers honoring the HWM (Spark + us do); a
  rogue writer's duplicate would make the DML affect all duplicates — documented, same trust class
  as any engine-assigned key.

### Tests

Extend the polybase suite: create the S3 Delta table WITH an identity marker → external table →
`UPDATE db.dbo.ext_t SET x=… WHERE id=…` and `DELETE … WHERE …` through the ATTACHed catalog →
OPENROWSET/external-table read shows the post-DML state exactly (CoW keeps protocol 1.0 — reuse
the §5 pins); multi-row + chunked (large IN) DML; SET-identity rejection; no-identity table still
rejects; concurrent-delete race (second connection removes a row between scan and DML → statement
succeeds, row simply absent).

---

## Slice B — `CREATE TABLE … WITH (location=…, table_type=…)` on the SQL provider (CETAS-analog) — **DONE**

Built 2026-07-19. Notes:

- The write is data-first, DDL-second: `CreateDeltaAs` (CTAS) / `CreateDeltaEmpty` (empty CREATE) write
  the client-side Delta table (protocol-1.0 plain — `deletion_vectors false, column_mapping 'none'`;
  CTAS uses `copy_disposition:'error'` so a pre-existing location fails), then `ProvisionExternalTable`
  runs `CREATE EXTERNAL FILE FORMAT` (auto, unless `file_format=` given) + `CREATE EXTERNAL TABLE` with
  a column list built from the write schema (`BuildExternalColumnList` — one source of truth, no drift).
- `data_source=` is REQUIRED (names a pre-provisioned EXTERNAL DATA SOURCE — the no-secret-material
  posture); `secret=` (credential auto-provisioning) is rejected with a pointer to `data_source=`.
- Two type findings the build surfaced: (1) external-table text columns need explicit lengths, and the
  cap DIFFERS — `VARCHAR(8000)` but `NVARCHAR(4000)` (its max explicit length; 8000 is error 2717).
  (2) the identity marker (`id BIGINT AS (0)`) IS allowed with `location`+`table_type='DELTA'` (declared
  plain BIGINT SQL-side) → the created table is slice-D DML-capable from birth.
- Guards: `CREATE OR REPLACE` rejected (DROP first), explicit-txn rejected, PK/UNIQUE/DEFAULT rejected
  with `location`, PARQUET empty-create rejected, ICEBERG rejected.
- **Pre-existing S3 finding (fixed in the test, noted for the product):** an EMPTY
  `CREATE OR REPLACE TABLE t (cols)` over an EXISTING S3 delta table fails with "version 0 already
  exists" — the DropTable+create-v0 path's post-delete `_delta_log` listing is stale within the one
  statement. A CTAS `CREATE OR REPLACE` is unaffected (it Overwrites — opens the existing table, commits
  a new version). Workaround: separate `DROP TABLE IF EXISTS` + `CREATE` statements (the view settles
  across the statement boundary). The polybase test's one empty-create-or-replace (iddml) uses the split.

Verified: `verify_mssql_s3_polybase.test` **252** (§6e — full auto-provision DDL round-trip + INSERT
compose + create-or-error + explicit-txn guards; §6f — the FULL CIRCLE: empty CREATE + identity marker →
INSERT → UPDATE → DELETE, all through the SQL catalog with every byte on MinIO), re-runnable across
back-to-back invocations; `verify_with_options_mssql.test` **9** (guards); regression identity 64 /
columnstore 20 / delta s3 161 / scalar 26 / procs 24 / native_write 147. Original design below.

Sugar over A (the option channel) + C (the writer): one DDL statement writes the data to S3
client-side and provisions the SQL side — the whole `verify_mssql_s3_polybase` manual flow as DDL.

### Surface (SQL Server provider)

```sql
CREATE TABLE db.dbo.trips
  WITH (location='s3://minio:9000/fabricator/polybase/trips',
        table_type='DELTA',                    -- or 'PARQUET'
        data_source='s3_ds',                   -- optional: reuse an existing EXTERNAL DATA SOURCE
        file_format='DeltaFmt',                -- optional: reuse an existing EXTERNAL FILE FORMAT
        secret='minio_s3')                     -- optional: DuckDB secret for auto-provisioning
AS SELECT …;
```

- `location` = the **SQL-visible** URL (goes verbatim into the EXTERNAL DATA SOURCE / table LOCATION);
  the client-side write again resolves the DuckDB secret by bucket scope (secret endpoint
  authoritative — same asymmetry rule as slice C).
- `table_type='DELTA'` → client writes Delta via the transient delta catalog, **forced
  `deletion_vectors false, column_mapping 'none'`** (SQL reader = protocol 1.0). `'PARQUET'` →
  client writes parquet file(s). `'ICEBERG'` → out of scope, clean error.

### Flow (in `SqlServerCatalog.BulkInsert`, createTable branch, when options carry `location`)

1. Write the CTAS stream to S3 client-side (slice C's writer, `createTable:true`).
2. Provision the SQL side on the pinned connection via the existing exec machinery:
   - `data_source=` given → reuse it (no credential handling at all — the recommended posture);
   - else auto-provision: `CREATE MASTER KEY` (guarded `IF NOT EXISTS`), `DATABASE SCOPED CREDENTIAL`
     (from the DuckDB secret's key_id/secret — **C++ resolves the secret at DDL time and passes its
     fields under a reserved options key**, the `build_connection_string`/v39 fields-JSON precedent;
     never logged), `EXTERNAL DATA SOURCE` (LOCATION = scheme+host of `location`), `EXTERNAL FILE
     FORMAT` (DELTA/PARQUET, reused if `file_format=` given).
   - `CREATE EXTERNAL TABLE dbo.trips (…) WITH (LOCATION, DATA_SOURCE, FILE_FORMAT)` — columns via
     `MapArrowToSqlType` over the SAME Arrow schema the data was written with (one source of truth,
     no drift).
3. The table is then a normal catalog entry; slice C makes later INSERTs work.

### Edges

- Empty `CREATE … WITH (…)` (no AS): DELTA → commit-0 + external table (reads 0 rows — fine);
  PARQUET → **rejected** (no file; SQL errors on an empty location anyway).
- `CREATE OR REPLACE`: v1 **rejected** (follow-up: overwrite data + recreate the external DDL).
- External DDL inside a user transaction is restricted by SQL Server → reject in explicit txn.
- PK / UNIQUE / DEFAULT clauses: rejected in combination with `location` (external tables carry none).
- **The IDENTITY marker (`id BIGINT AS (0)`) IS allowed with `location` + `table_type='DELTA'`** —
  it becomes a Delta identity column (writer-only, reader stays v1) and the external table declares
  the column as plain BIGINT (SQL external tables can't carry IDENTITY and don't need to). This is
  the recommended create shape: it makes the table **slice-D DML-capable** (identity-keyed
  UPDATE/DELETE) and gives PolyBase a visible stable row identifier (`_metadata.row_id` itself is
  unreachable there — the materialized row-tracking column is off-schema by spec and appends carry
  no physical id at all). Rejected for `table_type='PARQUET'` (no engine to assign values).

### Tests

**Planned as `test/verify_mssql_external_create.test`; that file was never created.** <!-- check-docs:ignore --> The coverage it
describes landed in **`test/verify_mssql_s3_polybase.test`** (252) instead, alongside the external-table
reads it depends on — which is the right home, since the DDL is only meaningful against a provisioned
external data source. What is covered there: full auto-provision DDL → SQL reads back exactly;
`data_source=` reuse; the PARQUET variant; and the guards (ICEBERG, replace, explicit txn, empty PARQUET
create, missing secret). The `WITH (...)` option parsing itself is `test/verify_with_options.test` (68).

---

## Execution order + gates

1. **A** — ABI v67 + Delta options (self-contained; gate: `verify_with_options` + the regression list).
2. **C** — detection + INSERT routing (no ABI; gate: extended `verify_mssql_s3_polybase` + SQL suites).
3. **D** — identity-keyed UPDATE/DELETE (small once C exists; gate: the polybase DML sections).
4. **B** — CETAS-analog DDL (gate: `verify_with_options` + the polybase suite).

Each slice ships independently; commit per slice (session convention).

## Risks

- **R1 endpoint asymmetry** (SQL-visible vs client-visible S3 host) — mitigated: secret endpoint
  authoritative, LOCATION host used only SQL-side; both tests already model the split.
- **R2 secret material crossing the ABI** (slice B auto-provision) — precedent: `build_connection_string`
  carries secret fields routinely; only on explicit `secret=` / auto-match, never logged; `data_source=`
  reuse avoids it entirely.
- **R3 type drift** between the SQL external-table declaration and the storage schema — slice B avoids
  it by generating both from one Arrow schema; slice C surfaces mismatches as write errors.
- **R4 transactionality** — storage writes are statement-atomic; explicit-txn rejections keep us honest.
- **R5 ABI v67 lockstep** — C++ + C# in one commit; rebuild unittest/shell/loadable (+ the Linux payload
  lags further).
- **R6 profile gating** — Fabric Warehouse/Synapse have no S3 data virtualization; detection degrades to
  empty, slice-B DDL should error cleanly on a warehouse profile (probe or catch error 15871-class).

## Out of scope (documented, not built)

`table_type='ICEBERG'` (no writer); CETAS-by-SQL-Server (SQL exporting parquet itself — our client-side
write is strictly more capable, Delta included); UPDATE/DELETE on external tables WITHOUT an identity
column (position rowids are snapshot-bound and can't bridge the two systems — slice D's identity key is
the only sound bridge; use a direct Delta ATTACH otherwise); UPDATE/DELETE on PARQUET-format external
tables (no log, no DML semantics); reading external tables via storage instead of SQL Server (reads stay
SQL-side by design); `location` on the Delta provider; deltars participation.
