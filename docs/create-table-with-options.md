# `CREATE TABLE … WITH (…)` options + SQL Server external tables (design)

Status: **PLANNED, nothing built** (2026-07-19). Three slices, ordered A → C → B by value/risk.
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
  (`fabricator_delta_set_tblproperties`, metadata kinds 13/14).
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

## Slice A — WITH-options plumbing + Delta per-table properties/write tuning (ABI v67)

The foundation: get the clause across the boundary, and make it useful on the Delta provider.

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
DV-default catalog); TBLPROPERTIES read back via `fabricator_delta_tblproperties` (one commit total);
precedence over `delta_write_options`; guards (unknown key, `delta.enable*` pointer, non-constant
value, `location` on delta, SQL Server unknown key). Regression: partition 54 / sorted_by 30 /
tblproperties 42 / native_write 147 / copy_format 109.

---

## Slice C — external-table detection + write routing (C#-only, **no ABI**)

The highest-value slice: INSERT into a detected S3 external table routes through storage — a
capability SQL Server itself does not have (it can't INSERT into S3 external tables at all, and can't
write Delta ever).

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
- UPDATE / DELETE / ALTER on an external table: intercept with a clear error pointing at a direct
  Delta ATTACH of the location (better than SQL Server's own error). 
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

## Slice B — `CREATE TABLE … WITH (location=…, table_type=…)` on the SQL provider (CETAS-analog)

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
- IDENTITY / PK / DEFAULT clauses: rejected in combination with `location` (external tables carry none).

### Tests

`test/verify_mssql_external_create.test` (same env gates): full auto-provision DDL → SQL reads back
exactly; `data_source=` reuse path; PARQUET variant; guards (ICEBERG, replace, explicit txn, empty
PARQUET create, missing secret).

---

## Execution order + gates

1. **A** — ABI v67 + Delta options (self-contained; gate: `verify_with_options` + the regression list).
2. **C** — detection + routing (no ABI; gate: extended `verify_mssql_s3_polybase` + SQL suites).
3. **B** — CETAS-analog DDL (gate: `verify_mssql_external_create` + polybase suite).

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
write is strictly more capable, Delta included); UPDATE/DELETE routed to storage (rowid domains don't
mix — use a direct Delta ATTACH); reading external tables via storage instead of SQL Server (reads stay
SQL-side by design); `location` on the Delta provider; deltars participation.
