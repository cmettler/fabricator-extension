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
- C# seams: `IBackendCatalog.CreateTable(…)` + `BulkInsert(…)` (`dotnet/Fabricator.Abstractions/IProvider.cs:159/:271`)
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
  external INSERT rejected inside an explicit transaction until 2026-10-26 (see
  §INSERT inside an explicit transaction).
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
- Statement-atomic in autocommit (slice C semantics). Inside an explicit transaction (since 2026-10-26) the
  DELETE is buffered until COMMIT and refused once the transaction has written to the table; the UPDATE likewise
  since 2026-11-05 (it was refused until the Delta layer could buffer an UPDATE on an identity table) — see §DELETE
  inside an explicit transaction.
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
the only sound bridge; use a direct Delta ATTACH otherwise — ⚠ SUPERSEDED for a DELETE with an exact predicate,
which needs no bridge because it is evaluated on the storage side: see "DELETE without an identity column" below);
UPDATE/DELETE on PARQUET-format external
tables (no log, no DML semantics); reading external tables via storage instead of SQL Server (reads stay
SQL-side by design); `location` on the Delta provider; deltars participation.

## DELETE without an identity column — BUILT 2026-10-26 (no ABI change)

Before, an external Delta table without a Delta identity column had no rowid, and every DELETE was refused with
the rowid path's *"requires a primary key or unique index"*. Now the plan-less predicate DELETE (ABI v93) reaches
an external table too: `SupportsDeleteWhereCore` answers yes for a routable external DELTA table, and
`DeleteWhereCore` sends the predicate to `ExternalTableRouting.DeleteWhere` instead of to SQL Server (where a
DELETE on an external table cannot run at all). `UPDATE` keeps the old rule — by identity or not at all.

- **Partition columns only** → engineered-wood's predicate delete (#506): whole files removed unread, no deletion
  vector.
- **Anything else** → a scan of the transient Delta catalog for the predicate's columns + the transient rowid, the
  predicate RE-EVALUATED exactly with engineered-wood's row evaluator, then the delta provider's own rowid DELETE —
  copy-on-write on a table without deletion vectors, so it stays protocol 1.0 and SQL Server keeps reading it.
  ⚠ The re-evaluation is LOAD-BEARING, not defensive: the scan's pushed filter is only a superset. MEASURED with the
  re-evaluation removed, `val > 5 AND name <> 'n1'` deleted 20 rows instead of 5.
- VARCHAR is exact here (`exact_strings` for an external Delta table only): the Delta engine compares bytes, not SQL
  Server's collation. An ordinary SQL Server table keeps VARCHAR on the rowid path.
- Autocommit only, like external DELETE/UPDATE generally (an INSERT can wait for COMMIT since 2026-10-26, a delete
  cannot — see §INSERT inside an explicit transaction); inside an explicit transaction it is buffered, see
  §DELETE inside an explicit transaction.
- ⚠ Through the EXTERNAL TABLE, SQL Server reads a Delta partition column as NULL (Microsoft's documented
  limitation; `OPENROWSET` reads it correctly). Pinned in the gate.

Gate `verify_mssql_external_delete` (43, service): routing, a twin-table differential against the rowid path,
SQL Server reading the result (OPENROWSET and the external table), the raw log holding no deletion vector (with a
control that the log was read), and the two refusals (explicit transaction; a predicate that does not translate).
One mutant (no re-evaluation) killed at the data-predicate row.

## INSERT inside an explicit transaction (2026-10-26)

Inside `BEGIN … COMMIT` an INSERT into an external **Delta** table no longer refuses. `ExternalDeltaSession`
(Fabricator.Delta) keeps one transient delta catalog per storage root for the life of the SQL Server transaction,
each in an explicit Delta transaction: the INSERT writes its data files at statement time and parks the actions,
the SQL Server catalog's `EndTransaction` commits them (one Delta commit per table) or rolls them back (the files are
deleted). C#-only, no ABI change.

- **Order at COMMIT: Delta first, then SQL Server.** A failed Delta commit rolls the SQL Server transaction back
  too. The reverse cannot be undone: if SQL Server's commit fails after the Delta commit, the Delta rows stay.
  Several tables commit one after the other (Delta has no cross-table transaction).
- **No read-your-writes.** SQL Server reads committed storage, so the transaction's own INSERTs are invisible through
  the external table until COMMIT. Pinned in the gate.
- **Still autocommit only:** an INSERT into a **parquet** external table — the file is visible the moment it
  exists; and the CETAS-analog `CREATE TABLE … WITH (location=…)`. DELETE followed on the same day, once
  engineered-wood could stage deletes copy-on-write — see §DELETE inside an explicit transaction.
- A catalog teardown without COMMIT/ROLLBACK rolls the session back (`SqlServerTransaction.Dispose`).

Gate `verify_mssql_external_txn` (62, service): two INSERTs into one table become one Delta commit, nothing reaches
the log before COMMIT, SQL Server (external table and OPENROWSET) reads the result, ROLLBACK deletes the written
files (with a control that a file was written), SQL Server's own changes roll back and commit with it, and the
DELETE refusal. Two mutants: committing at the statement dies at "nothing reached the log"; a ROLLBACK that discards
nothing dies at the file count. `verify_mssql_s3_polybase` (263 → 267) now asserts the ROLLBACK where it pinned the
old refusal, and pins the parquet refusal.

## DELETE inside an explicit transaction (2026-10-26)

Since engineered-wood stages deletes copy-on-write (`StageRowDeletesAsync(selection, RowDeleteMode.CopyOnWrite)`,
upstream #531) and our flush picks that mode on a table without deletion vectors, a DELETE on an external Delta
table can join the transaction too. It runs through the same `ExternalDeltaSession` as the INSERTs: the session's
catalog for the table's root buffers the rows, and COMMIT makes one Delta commit per table with the touched files
rewritten — no deletion vector, so SQL Server keeps reading the table. C#-only, no ABI change; `GuardExternalDml`
is gone, `SqlServerBackend.ExternalSession(txnId)` hands out the session (null in autocommit).

- **Predicate DELETE (`DeleteWhere`) always takes the scan route inside a transaction** — a scan through the
  session (which sees the transaction's own writes) plus engineered-wood's exact row evaluator — even for a
  partition predicate, because engineered-wood's predicate delete reads only the committed table.
- **The identity-keyed DELETE is refused once the session has changed rows of that table**
  (`ExternalDeltaSession.HasWrites`). Its ids come from SQL Server's scan, which reads committed storage: after an
  INSERT it would miss the new rows, after an UPDATE it would match the old values. The rule is deliberately
  coarser than that (a DELETE after only DELETEs would be correct) so that it is easy to state.
- **Still refused:** deleting a row inserted in the same transaction (`BufferDeleteRows`: hiding it needs a deletion
  vector); and UPDATE — every external UPDATE is identity-keyed, and `EnsureBufferedDmlEligible` does not buffer an
  UPDATE on an identity table (`!SupportsExternalCommit`), so it fails with the Delta layer's own message.
- The refactor gave each storage DML path one core with two callers: `Autocommit(root, …)` (own catalog, own commit)
  and `ExternalDeltaSession.Run(root, leaf, txnId, …)` (the transaction's catalog, no commit).

Gate `verify_mssql_external_txn` (62 → 111): a predicate DELETE invisible to SQL Server before COMMIT and undone by
ROLLBACK; an INSERT plus two DELETEs as ONE commit with no `deletionVector` in the log and OPENROWSET agreeing; the
same-transaction delete refused; identity-keyed DELETE inside a transaction, refused after an INSERT, and the UPDATE
refusal pinned. Since 2026-11-05 (-> 127) the UPDATE works inside a transaction: invisible before COMMIT, a second
SQL-Server-keyed UPDATE refused, ROLLBACK undoing it, and OPENROWSET agreeing after COMMIT. Two mutants: a predicate DELETE that commits at once dies at the no-read-your-writes row; dropping
the after-write refusal dies at its row. `verify_mssql_external_delete` (43 → 45) and `verify_mssql_s3_polybase`
(267 → 268) assert a ROLLBACK where they pinned the old refusal.

## Appendix — records moved verbatim from CLAUDE.md (2026-11-16)

These were open-thread or reference records in CLAUDE.md. They are closed, so they moved here word for word in the 2026-11-16 cleanup; links inside them are written relative to the repo root, as they were in CLAUDE.md.

### From CLAUDE.md §Next up — THE WRITE-OPTIONS REVISIT (2026-08-06/07)

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
      TABLE SHAPE (found 2026-08-07, REFUSED until 2026-10-24, when EW 65a69a6's translation let the refusal be
      lifted — see the 2026-10-23 bump entry).** engineered-wood matches the list against the PARQUET
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

