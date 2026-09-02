# fabricator — DuckDB ⇄ SQL Server via a C# (CoreCLR) Arrow bridge

A DuckDB extension that connects to **Microsoft SQL Server** by hosting a C# layer (via **CoreCLR**)
**in-process** and exchanging data + metadata as **Apache Arrow** over the Arrow C Stream Interface
(`ArrowArrayStream`). It is a direct, in-process replacement for the Arrow-Flight transport used by
the "Airport" extension.

**How it differs from the native-TDS [`mssql` extension](https://github.com/hugr-lab/mssql-extension):**
that extension speaks the TDS wire protocol directly in C++. This one delegates *all* SQL Server I/O
to C# using **`Microsoft.Data.SqlClient`** — the C++ extension only registers DuckDB functions and
ingests the Arrow streams the bridge produces. Connection strings are therefore plain
`Microsoft.Data.SqlClient` strings, and connection pooling / Windows & Azure auth come from SqlClient
natively. The C++ core (`fabricator`) and managed `Fabricator.Bridge` are transport- and backend-agnostic,
intended for reuse by a future Power BI / DAX connector.

**Works against box SQL Server, Azure SQL Database, and the Microsoft Fabric / Synapse warehouse family.**
The extension detects a server **capability profile** at ATTACH and adapts connection behavior (MARS,
isolation) and type mapping (collation-driven `VARCHAR`/`NVARCHAR`, `datetime2` scale, …) to the engine —
including Fabric Warehouse over an Entra service principal. See
[Microsoft Fabric & Synapse](#microsoft-fabric--synapse-warehouse).

**One binary, multiple providers.** The C++ core (`fabricator`) and managed `Fabricator.Bridge` are
provider-agnostic, and the same extension hosts several backends selected at ATTACH via `PROVIDER` (or
inferred from the connection scheme):

- **`sqlserver`** (default) — Microsoft SQL Server / Azure SQL / Fabric & Synapse warehouse (this document).
- **`delta`** — a **Delta Lake** folder/lakehouse as a read-write catalog (**local**, **S3**, and **Fabric
  OneLake** — the abfss target), with DML, time travel, snapshots, Change Data Feed, and **liquid clustering** (`SORTED BY`,
  `bucket()`/`hilbert_index()`, clustered `OPTIMIZE`). See [Delta Lake provider](#delta-lake-provider).
- **`dax`** — **Power BI / Analysis Services** semantic models over ADOMD (read-only DAX). See
  [`docs/dax-provider.md`](docs/dax-provider.md).

**The providers compose.** `CREATE TABLE … WITH (…)` sets per-table Delta properties + write tuning
([WITH options](#create-table--with--options)), and — the headline — a SQL Server **external table over S3**
becomes **writable through this extension**: `INSERT` / `UPDATE` / `DELETE` on an S3 Delta (or Parquet)
external table route to storage via the Delta writer, and one `CREATE TABLE … WITH (location=…,
table_type='DELTA') AS …` writes the data + auto-provisions the external table — capabilities SQL Server
has no native equivalent for (it cannot INSERT into an S3 external table, and cannot write Delta at all).
See [SQL Server external tables on S3](#sql-server-external-tables-on-s3).

> Design notes live under [`docs/`](docs/) — the full warehouse design in
> [`docs/warehouse-support.md`](docs/warehouse-support.md), the Delta catalog design in
> [`docs/delta-catalog.md`](docs/delta-catalog.md), and what does *not* work (measured) in
> [`docs/known-limitations.md`](docs/known-limitations.md).

## Feature Status

| Area | Feature | Status |
|------|---------|--------|
| **Connect** | `ATTACH (TYPE fabricator)` — connection string, `mssql://` URI, `CREATE SECRET` | ✅ |
| | Azure Entra ID / Fabric auth (service principal, managed identity, default, token, …) | ✅ |
| | `schema_filter` / `table_filter` (regex) | ✅ |
| | ATTACH-time connection validation (no orphan catalog on failure) | ✅ |
| | Kerberos / Windows SSPI integrated auth | ⚠️ via SqlClient (`Trusted_Connection` / `Integrated Security`); no bespoke `krb5` parser |
| **Catalog** | Schemas / tables / views, three-part naming, cross-catalog joins | ✅ |
| | `rowid` from PK / smallest unique index (scalar + composite) | ✅ |
| | Metadata cache + manual / auto (after-DDL) invalidation | ✅ |
| **Read** | Streaming SELECT | ✅ |
| | Projection + filter pushdown (best-effort, parameterized WHERE) | ✅ |
| | `LIMIT` → `TOP n`; `ORDER BY`+`LIMIT` → TopN (safe keys only) | ✅ |
| | Statistics → optimizer: cardinality + per-column NDV | ✅ (NDV only; min/max intentionally not reported) |
| **Write** | INSERT, INSERT…SELECT, INSERT…RETURNING (`OUTPUT INSERTED.*`) | ✅ |
| | UPDATE / DELETE (rowid-based, parameterized) | ✅ |
| | `MERGE INTO` — all actions incl. `NOT MATCHED BY SOURCE`, `DO NOTHING`, `ERROR` | ✅ (needs a rowid; 2+ `UPDATE`/`DELETE` actions are fused and need deletion vectors on Delta; no `RETURNING`) |
| | `UPDATE … SET col = DEFAULT` | ❌ (write the value explicitly) |
| | `INSERT … ON CONFLICT` | ❌ (no unique constraint is advertised — use `MERGE INTO`) |
| | CREATE TABLE AS / COPY TO (streaming bulk via `SqlBulkCopy`) | ✅ |
| | Bounded-memory streaming bulk write (INSERT/CTAS/COPY) | ✅ |
| | CHECK/FK constraint enforcement on INSERT | ✅ (`SqlBulkCopyOptions.CheckConstraints`; COPY/CTAS skip for speed) |
| **DDL** | CREATE/DROP TABLE, CREATE/DROP SCHEMA, ALTER TABLE | ✅ |
| | PRIMARY KEY / UNIQUE / NOT NULL / literal DEFAULT on CREATE | ✅ |
| | `CREATE TABLE … WITH (…)` options (per-table Delta properties / write tuning / feature flags) | ✅ |
| | CHECK constraints, non-literal DEFAULTs | ❌ (use `fabricator_exec`) |
| **S3 external tables** | `INSERT` into a detected SQL Server S3 **Delta/Parquet** external table → routed to storage | ✅ |
| | Identity-keyed `UPDATE` / `DELETE` on an S3 Delta external table | ✅ |
| | `CREATE TABLE … WITH (location=…, table_type='DELTA'\|'PARQUET') [AS …]` — write + auto-provision the external table | ✅ |
| **Tx** | BEGIN / COMMIT / ROLLBACK (connection pinning, read-your-writes) | ✅ |
| **Functions** | `fabricator_query`, `fabricator_exec`, `fabricator_refresh_cache`, `fabricator_invalidate_cache`, `fabricator_version` | ✅ |
| | `fabricator_functions(catalog)` — list discovered routines | ✅ |
| | `fabricator_delta_scan(path)` / `fabricator_delta_native_scan(path)` — read a Delta table by path, no ATTACH (C# reader / DuckDB's parquet reader) | ✅ |
| | `fabricator_host_query(sql)` — run a query on DuckDB itself (inherits your search path + `TimeZone`) | ✅ |
| | `fabricator_host_exec(sql)` — DDL/DML on DuckDB itself, returning the affected-row count (table + scalar) | ✅ |
| | `fabricator_http_request(url, …)` — an HTTP call through DuckDB's own stack (its `TYPE http` secret, TLS trust, proxy, retries) | ✅ |
| **Macros** | Provider **global** macros — bare `fn(...)` / `FROM fn(...)`, every database, no ATTACH | ✅ |
| | Provider **catalog-bound** macros → `db.schema.m(...)` (namespaced per catalog; expanded by the binder) | ✅ |
| **Views** | Provider **catalog-bound** views → `db.schema.v` — a real relation, and unlike a macro its body binds against its OWN catalog | ✅ |
| | **SQL-generating** table functions — the call is rewritten into SQL at bind time (`kind='table_sql'`) | ✅ |
| **Fabric API** | `refresh_sql_endpoint()` — sync the lakehouse SQL analytics endpoint from SQL (the dbt unblocker) | ✅ OneLake + Fabric SQL attaches |
| | OneLake **shortcut** create / alter / drop / list, incl. non-OneLake targets via JSON | ✅ OneLake + Fabric SQL attaches |
| | Introspection: workspaces, items, lakehouses (+ SQL endpoint strings), warehouses, connections, notebook parameters | ✅ OneLake + Fabric SQL attaches |
| | **Parameterized notebook runs** (blocking, with status + portal snapshot link) | ✅ OneLake + Fabric SQL attaches |
| | **Jobs**: table maintenance (V-Order/Z-order/vacuum), generic runner, status, history, cancel | ✅ OneLake + Fabric SQL attaches |
| | **Semantic models**: list, enhanced refresh, refresh history (Power BI REST, same credential) | ✅ OneLake + Fabric SQL attaches |
| | **Git**: status, connection, commit, update-from-git; **deployment pipelines**: list, stages, items, deploy, history | ✅ OneLake + Fabric SQL attaches |
| | Platform reads: capacities, Spark environments, OneLake data-access roles, mirrored-database status | ✅ OneLake + Fabric SQL attaches |
| | **Per-table / per-partition semantic-model refresh** via XMLA/TMSL → `dax_refresh*` | ✅ DAX attaches |
| | Notebook `exitValue` | ⏳ best-effort (always NULL in practice) |
| **Callable** | Discovered scalar UDFs → `db.schema.fn(args)` (vectorized over Arrow) | ✅ |
| | Discovered table-valued functions → `SELECT * FROM db.schema.tvf(args)` (+ projection/filter pushdown) | ✅ |
| | Discovered stored procedures → `SELECT * FROM db.schema.proc(name := val)` (named/optional + OUTPUT params) | ✅ |
| | Custom C#-authored scalar / table / table-in-out functions | ✅ |
| | **Table-in-out**: `db.schema.fn_each(<input table>)` — apply a TVF (CROSS APPLY) or proc per input row | ✅ |
| | **Correlated LATERAL**: `FROM t, db.schema.fn(t.a, t.b)` — batched, 1→1/1→0/1→N | ✅ |
| | — configurable isolation (consistent snapshot per call); per-row procs run in DuckDB's transaction | ✅ |
| | **Custom C# aggregates** (UDAF) → `db.schema.agg(x)` in `GROUP BY` / parallel / `OVER(…)`; opt-in disk-spill (`SupportsSpill`) | ✅ |
| **Change data capture** | `db.cdc.tables()` / `max_position()` / `min_position()` / `health()` — inspect SQL Server CDC | ✅ SQL Server only (not Fabric / Synapse) |
| | `db.cdc.enable_database()` / `enable()` / `disable()` / `capture_now()` — set capture up from SQL, with a generated capture-instance name; the catalog refreshes itself | ✅ |
| | `db.cdc.changes(...)` — a resumable change-stream reader with a 21-byte cursor, a retention pre-check, `enable := true`, an in-window schema-change check, and a table's two capture instances read as ONE stream across the boundary | ✅ |
| | `include := 'snapshot'` / `'snapshot+changes'` — the whole table as of one consistent instant, then the changes after it, with no gap between the halves | ✅ |
| | Before-images (`images := 'both'`, with `_update_mask` and a decoded `_changed_columns`) and wall-clock window bounds | ✅ |
| | `on_schema_change := 'resync'` — re-capture and re-baseline when the capture instance no longer matches the source | ✅ |
| **Monitoring** | `db.dbo.fabricator_session_tag(k, v)` — tag a transaction's connection so Fabric query insights can attribute its cost | ✅ (see dbt caveat) |
| | `application_name` on the secret → `program_name` on every statement (run-level attribution) | ✅ |
| | Load-time *global* functions (connection-free); proc multi-result-set / `INOUT` params | ❌ deferred |
| **Warehouse** | Auto-detected server profile (edition / version / collation) + `fabricator_server_info()` | ✅ Fabric Warehouse validated |
| | Connection mode: `mssql_mars` (auto per engine), pooled reads + SNAPSHOT writes when MARS off | ✅ |
| | Collation-adaptive `VARCHAR`/`NVARCHAR`; binary-collation string `ORDER BY` pushdown | ✅ |
| | PK/UNIQUE as `NONCLUSTERED NOT ENFORCED` (via ALTER) → rowid → UPDATE/DELETE on Fabric | ✅ |
| | Clustered columnstore tables (`mssql_default_table_type`) | ✅ (box; implicit on Fabric) |
| **Diag** | Connection-pool diagnostics (`mssql_pool_stats`, `mssql_open/close/ping`) | ❌ |
| | COPY to temp tables (`#t` / empty-schema syntax) | ❌ |
| **Packaging** | Single-file distribution — one `.duckdb_extension` that unpacks itself, no env vars ([Install](#install)) | ✅ windows_amd64 + linux_amd64 |
| | Signed extension / DuckDB community repository | ❌ (`allow_unsigned_extensions` required) |

✅ implemented & verified · ⚠️ partial / via SqlClient · ❌ not yet

## Install

fabricator ships as **one file** — a `fabricator.duckdb_extension` that carries the C++ extension and
the .NET bridge inside it and unpacks them into DuckDB's own extension directory the first time it is
loaded. There is nothing to unzip and no environment variable to set.

There are no published downloads yet, and fabricator is not in the DuckDB community repository (its CI
builds C/C++/Rust from source and cannot produce or host a .NET payload), so for now you build the
artifact yourself — see the collapsed section at the end of this chapter.

```sql
-- DuckDB must be started with allow_unsigned_extensions (see below)
LOAD '/path/to/fabricator.duckdb_extension';
SELECT fabricator_version();
```

The first load takes about two seconds (unpacking); every later load takes about ten milliseconds,
because it only checks a marker file and loads the already-extracted core. Nothing is written outside
DuckDB's extension directory.

**Pick the artifact matching your DuckDB build.** The inner extension is compiled against DuckDB's
C++ internals, so it is tied to an exact DuckDB version and platform — `v1.5.5` / `windows_amd64`,
`v1.5.5` / `linux_amd64`, and so on. A mismatch is refused immediately, with a message naming the
version you are running, **before anything is written to disk**. Two flavours per platform:

| flavour | requires | measured size |
|---|---|---|
| **standalone** | nothing — the .NET runtime is inside | 61 MB (`windows_amd64`) |
| **standard** | .NET 8 or newer on the machine (found automatically; `FABRICATOR_DOTNET_ROOT` or `DOTNET_ROOT` override the search) | 40 MB (`linux_amd64`) |

(Sizes are per platform as well as per flavour — the Linux core is larger than the Windows one, so
the two figures are not a like-for-like comparison of the flavours.)

**`allow_unsigned_extensions` is required** — fabricator is not signed by DuckDB, and this is a
start-up option, not a `SET`:

```bash
duckdb -unsigned
```
```python
duckdb.connect(config={"allow_unsigned_extensions": True})
```
```yaml
# dbt profiles.yml (dbt-duckdb) — the artifact can be listed as an extension to load
config_options:
  allow_unsigned_extensions: true
extensions:
  - '/path/to/fabricator.duckdb_extension'
```

`INSTALL '/path/to/fabricator.duckdb_extension'` also works if you prefer DuckDB to copy the file into
its extension directory first, after which a plain `LOAD fabricator` resolves it by name.

<details>
<summary>Building the artifact yourself, and the two-piece layout</summary>

Build it with `scripts/pack-distribution.ps1` (see [Build](#build) for the prerequisites):

```powershell
./scripts/pack-distribution.ps1 -Sku Standalone -Rid win-x64
# -> build/distribution/windows_amd64/fabricator.duckdb_extension
```

NativeAOT and the C++ core cannot cross-compile between operating systems, so each platform's two
inputs are built on that platform; packing them is platform-neutral (`-CorePath`/`-ManagedPath` let
one machine assemble another platform's artifact from them).

The older **two-piece layout** still works and is what a plain source build produces: the
`fabricator.duckdb_extension` loadable plus a `fabricator/` directory of managed assemblies, either
beside the loadable or pointed to by `FABRICATOR_MANAGED_DIR`. The single file is that same pair,
packed. After a first single-file load you can also `LOAD fabricator_core` directly — the extracted
core is a normal two-piece install and the same binary answers to both names.

Design and internals: [docs/distribution-installer.md](docs/distribution-installer.md).

</details>

## Quick Start

With the extension loaded ([Install](#install)):

```sql
-- Connection string (a valid Microsoft.Data.SqlClient string)
ATTACH 'Server=host,1433;Database=db;User Id=sa;Password=***;TrustServerCertificate=true;Encrypt=true'
  AS mssql (TYPE fabricator);

SELECT * FROM mssql.dbo.people WHERE id > 1;   -- automatic streaming scan + filter pushdown
SELECT count(*) FROM mssql.dbo.people;

DETACH mssql;
```

> ⚠ **Set your session timezone to UTC.**
>
> ```sql
> SET TimeZone = 'UTC';
> ```
>
> DuckDB's `TimeZone` defaults to the **system** zone once `icu` is loaded, not to UTC. The Delta protocol
> stores and accepts UTC, and several surfaces here read the session zone — `fabricator_host_query` inherits
> it, `TIMESTAMPTZ` values are interpreted in it, and a `TIMESTAMPTZ → DATE` conversion resolves in it — so
> a session left on local time can silently shift a date by a day. Setting UTC once makes all of that agree.

## Connection Configuration

A `fabricator` connection is given **either** a `Microsoft.Data.SqlClient` connection string, the
`mssql://` URI convenience form, **or** a stored secret.

```sql
-- ADO.NET / SqlClient connection string
ATTACH 'Server=host,1433;Database=db;User Id=sa;Password=***;TrustServerCertificate=true;Encrypt=true'
  AS mssql (TYPE fabricator);

-- mssql:// URI (encrypt defaults on; ?encrypt=false to disable)
ATTACH 'mssql://sa:***@host:1433/db' AS mssql (TYPE fabricator);

-- Restrict catalog discovery on large servers (case-insensitive regex, partial match)
ATTACH 'Server=...;Database=...' AS mssql
  (TYPE fabricator, schema_filter '^(dbo|sales)$', table_filter '^fact_');

-- Per-catalog opt-out of buffering a scan of the catalog being written (see mssql_materialize).
-- Keeps INSERT INTO t SELECT ... FROM t STREAMING; needs ALLOW_SNAPSHOT_ISOLATION on the database.
ATTACH 'Server=...;Database=...' AS mssql (TYPE fabricator, materialize false);

-- Per-catalog MARS mode: auto (default, the engine's capability) | true | false.
-- Use this rather than SET mssql_mars when two catalogs must differ — a SET applies to the whole
-- DuckDB connection, so it would govern every catalog attached in it.
ATTACH 'Server=...;Database=...' AS mssql (TYPE fabricator, mars 'false');
```

On a **Fabric** SQL endpoint the catalog also gains a `fabric` schema of platform functions, which need no
extra options (they derive the workspace and item from the connection string; `API_WORKSPACE` / `API_ITEM`
override them) — see
[Microsoft Fabric platform functions](#microsoft-fabric-platform-functions-onelake-and-fabric-sql-attaches).

### Secrets

```sql
-- SQL auth. Password/access_token are redacted in duckdb_secrets().
CREATE SECRET sql1 (TYPE mssql,
  host 'host', port 1433, database 'db', user 'sa', password '***', use_encrypt true);
ATTACH '' AS mssql (TYPE fabricator, SECRET sql1);
SELECT * FROM fabricator_query('sql1', 'SELECT 1');
```

Secret field names mirror the native `mssql` extension for cross-compatibility:
`host`, `port`, `database`, `user`, `password`, `use_encrypt`, `access_token`, `authentication`,
`azure_tenant_id`, `azure_secret`, `schema_filter`, `table_filter`, `application_name`.

`azure_tenant_id` matters only for the Fabric platform functions: SqlClient infers the tenant from the server
it connects to, but minting a Fabric API token cannot, so a service-principal secret needs it spelled out.

### Azure Entra ID (Microsoft Fabric SQL endpoints, which require Entra)

`Microsoft.Data.SqlClient` handles Entra natively via the `Authentication=` keyword, so most variants
need no extra code:

```sql
CREATE SECRET fab_sp (TYPE mssql,            -- service principal
  host 'xxx.datawarehouse.fabric.microsoft.com', database 'wh',
  authentication 'service_principal', user '<app-client-id>', password '<client-secret>');
CREATE SECRET fab_mi (TYPE mssql,            -- (user-assigned) managed identity
  host '...', database 'wh', authentication 'managed_identity', user '<mi-client-id>');
CREATE SECRET fab_def (TYPE mssql,           -- DefaultAzureCredential chain
  host '...', database 'wh', authentication 'default');
CREATE SECRET fab_tok (TYPE mssql,           -- bring-your-own Entra token
  host '...', database 'wh', access_token '<jwt>');
-- also: interactive, device_code, workload_identity, password.
ATTACH '' AS fab (TYPE fabricator, SECRET fab_sp);
```

### Integrated / Windows authentication

Use SqlClient's native keywords directly — `Trusted_Connection=true` or `Integrated Security=SSPI`
(Windows). We do **not** implement the native extension's bespoke `authenticator=krb5` / `krb5-*`
connection-string dialect (see [Differences](#differences-from-the-native-mssql-extension)).

### Connection validation

ATTACH validates the connection up front and creates **no** catalog on failure (fail-fast, password
never leaked):

```sql
ATTACH 'Server=nonexistent.host,1433;Database=db;User Id=sa;Password=pass' AS db (TYPE fabricator);
-- Error: MSSQL connection validation failed: <cause>
```

## Microsoft Fabric & Synapse (warehouse)

Beyond box SQL Server, the extension connects to **Fabric Warehouse**, the **Lakehouse SQL analytics
endpoint**, and **Synapse** pools. At ATTACH it detects a **server capability profile** (engine edition +
product version + database collation) once and adapts automatically — no configuration needed. Inspect it
with `fabricator_server_info(catalog)`:

```sql
-- Fabric Warehouse via an Entra service principal:
CREATE SECRET fab (TYPE mssql,
  host 'xxxxx.datawarehouse.fabric.microsoft.com', database 'My Warehouse',
  authentication 'service_principal', user '<app-client-id>', password '<client-secret>');
ATTACH '' AS wh (TYPE fabricator, SECRET fab);

SELECT property, value FROM fabricator_server_info('wh');
-- engine_edition=11, supports_mars=false, has_nvarchar=false, has_datetimeoffset=false,
-- max_datetime2_scale=6, is_utf8_collation=true, is_binary_collation=true, default_write_isolation=snapshot,
-- mars_enabled=false
```

What the profile drives automatically:

- **Connection mode.** Fabric/Synapse reject MARS, so it's auto-disabled (`mssql_mars=auto`); with MARS off,
  data scans use **pooled** connections and the write transaction runs at **SNAPSHOT** isolation. Override
  with `SET mssql_mars='true'|'false'|'auto'` **before** ATTACH. (Read-your-writes for *scans* is given up on
  MARS-off engines — a documented trade-off — but *metadata* reads still see your own uncommitted DDL, so
  `CREATE TABLE` then immediate use works.)
  - **Exception, since the scan-materialisation fix:** a scan that feeds a write into the **same catalog**
    (`INSERT INTO t SELECT … FROM t`, or a CTAS reading that catalog) *does* see the transaction's own
    uncommitted rows on Fabric/Synapse, because it is buffered and can therefore run on the pinned
    connection. A standalone `SELECT` inside the transaction is unaffected and still reads committed state.
- **Type mapping** is collation/edition-driven: `VARCHAR` (UTF-8, not `NVARCHAR`), `DATETIME2(6)`, and UTC
  `DATETIME2` for tz instants (Fabric has no `datetimeoffset`). See the type table below.
- **Constraints.** Fabric accepts `PRIMARY KEY`/`UNIQUE` only as `NONCLUSTERED NOT ENFORCED` hints added via
  `ALTER`, so `CREATE TABLE … PRIMARY KEY` is automatically split into `CREATE` + `ALTER TABLE ADD CONSTRAINT
  … NONCLUSTERED NOT ENFORCED`. They aren't enforced by Fabric, but they seed rowid discovery so
  **UPDATE/DELETE work**.
- **String `ORDER BY` pushdown** turns on under Fabric's binary (`BIN2_UTF8`) collation (byte-order sort
  matches DuckDB); it stays local under case-insensitive collations (still correct).
- **Data-layout clustering.** A native `CREATE TABLE … SORTED BY (cols)` (the clause precedes `AS` for CTAS) is
  mapped to Fabric Warehouse / Synapse `WITH (CLUSTER BY (cols))` — DuckDB has no `CLUSTER BY` syntax, so its
  `SORTED BY` serves the same purpose. Emitted only on a warehouse profile (a no-op on box SQL Server). The
  `mssql_cluster_by` setting is a session fallback. Example:
  ```sql
  CREATE TABLE wh.dbo.Sales (SaleID INT, CustomerID INT, SaleDate DATE, Amount DECIMAL(10,2))
    SORTED BY (CustomerID, SaleDate);   -- → CREATE TABLE … WITH (CLUSTER BY (CustomerID, SaleDate))
  ```
- **Time travel.** `FROM t AT (TIMESTAMP => ts)` maps to the statement-level `OPTION (FOR TIMESTAMP AS OF
  '<ts>')` hint on a warehouse (works on any table; UTC, truncated to milliseconds), and to `FOR SYSTEM_TIME
  AS OF` on box SQL Server (which needs a system-versioned/temporal table). `AT (VERSION => n)` is not supported
  (no SQL Server equivalent).
  ```sql
  SELECT * FROM wh.dbo.dimension_customer AT (TIMESTAMP => '2024-05-02 20:44:13.700');
  ```
- **IDENTITY / surrogate keys.** DuckDB has no `IDENTITY`, so a **generated column** (`col BIGINT AS (0)`) is
  used as a marker → the provider emits an `IDENTITY` column (box `IDENTITY(1,1)`; Fabric bare BIGINT `IDENTITY`).
  Or set `add_identity` (ATTACH option / `SET mssql_add_identity`) to auto-add a `<table>_id BIGINT IDENTITY`
  surrogate key to created tables (CREATE + CTAS; skipped if the name already exists). The engine assigns values
  on INSERT (the identity column is absent from the source, so it's left for the engine).
  ```sql
  CREATE TABLE wh.dbo.Orders (OrderID BIGINT AS (0), CustomerID INT, Amount DECIMAL(10,2));  -- OrderID → IDENTITY
  ATTACH '...' AS wh (TYPE fabricator, add_identity true);   -- every created table gets <table>_id
  ```
  An IDENTITY column is also usable as the **`rowid`**, so `UPDATE`/`DELETE` work on a table whose only key is
  its identity column (no PK/unique). Precedence flips by engine: a **warehouse prefers the identity** (Fabric
  PK/UNIQUE are non-enforced hints, but IDENTITY is engine-unique); **box / Azure SQL prefer the PK/unique** and
  fall back to the identity only when there's no key constraint.
- **Functions.** Discovered scalar UDFs, TVFs, and stored procedures work on Fabric (proc result sets resolved
  via `sp_describe_first_result_set`), as do custom C# functions and the `fn_each` table-in-out exchange.

> **Collation reality:** no single collation is ideal across the whole OneLake stack (DuckDB + the SQL endpoint
> are case-sensitive/binary; the DAX/Vertipaq engine is case-insensitive). The extension is *adaptive* —
> correct under any collation — but does not prescribe one. Full design: [`docs/warehouse-support.md`](docs/warehouse-support.md).

## Catalog Integration

```sql
-- Browse with standard DuckDB catalog functions
SELECT schema_name FROM duckdb_schemas() WHERE database_name = 'mssql';
SELECT table_name  FROM duckdb_tables()  WHERE database_name = 'mssql' AND schema_name = 'dbo';

-- Three-part naming + cross-catalog joins with local DuckDB tables
SELECT s.name, o.amount
  FROM mssql.dbo.people s JOIN mssql.sales.orders o ON o.order_id = 1000 + s.id;
```

## Query Execution & Pushdown

Results stream directly into DuckDB. The extension pushes work to SQL Server:

```sql
-- Projection + filter pushdown: only [id]/[name] cross the wire and
-- `id >= 10 AND name = 'Bob'` is sent as a parameterized WHERE.
SELECT id, name FROM mssql.dbo.people WHERE id >= 10 AND name = 'Bob';

-- Bare LIMIT (no ORDER BY / filter) → SELECT TOP (100)
SELECT * FROM mssql.dbo.big_table LIMIT 100;

-- ORDER BY + LIMIT (top-N) → SELECT TOP (10) ... ORDER BY, when keys are safe
SELECT * FROM mssql.dbo.events ORDER BY id DESC LIMIT 10;
```

**Filter pushdown is best-effort:** DuckDB always re-applies every predicate, so anything not pushed is
still filtered correctly. Pushed shapes: `=`, `<>`, `<`, `<=`, `>`, `>=`, `IS [NOT] DISTINCT FROM`,
`IS [NOT] NULL`, `IN`, `BETWEEN`, `AND`/`OR`. `VARCHAR` predicates push only `=` / `IN` /
`IS NOT DISTINCT FROM` (SQL Server collation is a looser superset; ordering comparisons stay local).
**TopN** is pushed only when there is no pushed filter and every order key is safe for the target. On
**SQL Server** that means a non-string key (or any key under a binary collation) whose NULL ordering
matches the server's fixed convention (ASC = NULLS FIRST, DESC = NULLS LAST) — T-SQL cannot spell the
other one. On **Delta** every key qualifies, including strings and any NULL placement, because the
generated SQL is executed by DuckDB itself (see [Delta Lake provider](#delta-lake-provider)). Either way
the DuckDB sort node is kept, so results are always correct.

One thing that quietly turns TopN pushdown *off* (on any provider): ordering by a **collated** key —
an explicit `COLLATE`, or a session `SET default_collation` to anything other than binary. DuckDB then
sorts by a rule the pushed-down query would not reproduce, so the key is left local. The answer is
unchanged; only the row-trimming optimization is lost.

The optimizer also receives each table's approximate **row count** (from partition stats) and
**per-column NDV** (distinct-value estimate) for better join ordering. Min/max bounds are intentionally
*not* reported (DuckDB would prune filters on them, and SQL Server stats are sampled/stale).

The Delta provider feeds the same channel from a different source: its row count is **summed from the
transaction log** — each file's recorded `numRecords` minus each deletion vector's stated cardinality — so
it costs no data-file read and is exact rather than sampled. It reports no NDV, because a Delta `add`
records min/max and null counts per column but no distinct-value estimate.

> **⚠ Statistics are read once per table and cached for the catalog entry's lifetime**, on every provider.
> They are fetched lazily, the first time a table is *scanned* (never during catalog enumeration), and
> nothing re-reads them afterwards — not your own DML, and certainly not another process's. So after a bulk
> load, a large external `INSERT`, or a `UPDATE STATISTICS` elsewhere, plans in an existing session are
> still costed against the numbers from the first scan.
>
> This affects **plan quality only, never your results**: what is reported is an *estimate* — the row count
> is never given to DuckDB as an upper bound, and min/max are deliberately not reported at all — so a stale
> value can pick a worse join order but cannot drop or duplicate a row.
>
> To refresh them, invalidate the entry: `SELECT fabricator_refresh_cache('<catalog>')`, or the scoped
> `SELECT fabricator_invalidate_cache('<catalog>', '<regex>')` when you know which tables changed. Both
> rebuild the catalog entry, and the next scan re-reads its statistics.

### Row identity (`rowid`)

Tables with a primary key (or unique index) expose a virtual `rowid` (scalar for a single key column,
`STRUCT` for a composite key). It backs UPDATE/DELETE and can be selected:

```sql
SELECT rowid, name FROM mssql.dbo.products LIMIT 5;
```

## Transactions

`BEGIN`/`COMMIT`/`ROLLBACK` map to a real SQL Server transaction. A connection is pinned (lazily, on
the first write); all DML — catalog INSERT/UPDATE/DELETE and `fabricator_exec` — runs on it, and reads
inside the transaction use it too (read-your-writes), so `ROLLBACK` undoes everything.

```sql
BEGIN;
INSERT INTO mssql.dbo.people (id, name) VALUES (9, 'temp');
SELECT fabricator_exec('mssql', 'UPDATE dbo.people SET salary = 0 WHERE id = 1');
SELECT count(*) FROM mssql.dbo.people;   -- sees the uncommitted INSERT
ROLLBACK;                                -- both statements undone
```

**⚠ A transaction gives you read-your-writes, NOT a stable view of the rest of the world.** Two identical
`SELECT`s inside one `BEGIN … COMMIT` can return different answers if another session commits in between,
on box **and** on Fabric — no setting changes it. On box SQL Server without `READ_COMMITTED_SNAPSHOT`, even
a *single statement* is not a boundary: a statement referencing one table twice can report two different
states of it, and one scan can return part of a concurrent transaction's rows. Enabling database-level
`READ_COMMITTED_SNAPSHOT` fixes the single-statement case (Fabric / Synapse-serverless are already
snapshot-isolated). For a stable view **across** statements — and for the single-statement case too, if you
cannot change the database — there is an opt-in:

```sql
-- every read inside a transaction joins it, at SNAPSHOT — so the two SELECTs agree
SET mssql_read_isolation = 'snapshot';
BEGIN;
SELECT count(*) FROM mssql.dbo.people;   -- another session commits an insert here …
SELECT count(*) FROM mssql.dbo.people;   -- … and this still returns the same count
COMMIT;
```

It is off by default because it holds a connection and an open transaction for the whole DuckDB
transaction's life (and on Fabric/Synapse it buffers reads instead of streaming them). The alternative is
to materialise once into DuckDB (`CREATE TEMP TABLE … AS SELECT`). Details and measurements:
[known-limitations.md](docs/known-limitations.md) 1.16–1.17.

## INSERT / UPDATE / DELETE

```sql
INSERT INTO mssql.dbo.target (id, name) VALUES (1, 'Alice'), (2, 'Bob');
INSERT INTO mssql.dbo.target SELECT id, name FROM read_csv('data.csv');

-- INSERT … RETURNING uses OUTPUT INSERTED, so server-side IDENTITY/DEFAULT values come back.
INSERT INTO mssql.dbo.target (name) VALUES ('Carol') RETURNING id, name;

-- UPDATE / DELETE need a rowid (PK or unique index; compound keys supported).
UPDATE mssql.dbo.people SET salary = salary * 1.1 WHERE id = 1;
DELETE FROM mssql.dbo.people WHERE id = 3;
```

INSERT streams through `SqlBulkCopy` with `CheckConstraints` enabled, so a CHECK / FOREIGN KEY
violation fails just like a classic INSERT (CTAS/COPY skip constraint checking for bulk-load speed).
`IDENTITY` columns are preserved when the source supplies them (`KeepIdentity`) and auto-generated
otherwise. UPDATE/DELETE require a primary key or unique index (otherwise use `fabricator_exec`); they do
not support RETURNING.

`UPDATE … SET col = DEFAULT` is not supported — write the default value explicitly.

### Loading a Fabric Warehouse with `COPY INTO` (opt-in)

`SqlBulkCopy` streams rows over TDS one buffer at a time, which against a Fabric Warehouse is a round trip
per buffer. Name a storage location this extension may stage temporary parquet in and a bulk write takes a
different route instead: DuckDB writes the whole result there with its own parallel parquet writer, and the
warehouse ingests the folder with **one `COPY INTO`** — the rows never cross TDS.

```sql
-- ⚠ the OneLake workspace and item must be GUIDs, not display names (see below)
ATTACH 'Server=<ep>.datawarehouse.fabric.microsoft.com;Database=MyWH' AS wh
  (TYPE fabricator, SECRET fabric_sp,
   copy_into_staging 'abfss://<workspaceId>@onelake.dfs.fabric.microsoft.com/<lakehouseId>/Files/staging');

-- staged as parquet, then loaded with a single COPY INTO
CREATE TABLE wh.dbo.big AS SELECT * FROM read_parquet('local/*.parquet');
```

`SET mssql_copy_into_staging = '…'` is the session form and overrides the ATTACH option. Either spelling of
the location works — `abfss://<fs>@<host>/<path>` or `https://<host>/<fs>/<path>` — because the two engines
need different ones and this translates between them.

- **It is off unless you name a location**, and there is no size threshold: `COPY INTO` has seconds of fixed
  cost, so it is the wrong choice for a small INSERT and the decision stays yours.
- **⚠ A OneLake staging path must name its workspace and item by GUID.** Display names stage fine and then
  fail the load with `13840 … unsupported URL`, so they are refused at ATTACH instead. Get the ids from
  `fabric.workspaces()` and `fabric.items()` on a OneLake attach.
- **The identity running the statement needs read access to the staging area.** OneLake as a `COPY INTO`
  source supports **Entra ID only** — no SAS, no account key.
- **Staged files are removed after the load.** A cleanup failure is logged, never raised: it cannot make a
  successful load look failed.
- Ignored on engines without `COPY INTO` (box SQL Server, Azure SQL) — those keep `SqlBulkCopy`, so one
  session setting can span a mixed set of attaches safely.
- An INSERT that supplies explicit `IDENTITY` values is **refused** on this path (`COPY INTO` cannot preserve
  them); unset the option for that statement.
- The load runs inside your transaction, so `BEGIN … ROLLBACK` still governs it.
- **The source scan streams and reads a committed snapshot**, so a staged load does *not* see rows the same
  transaction wrote moments earlier. That is deliberate: this path is for bulk movement, and streaming rather
  than buffering the whole source is what makes it fast (measured ~11% faster wall clock, ~27% less CPU and
  484 MB less allocation on a 1M-row load). Unset the staging option for a statement that needs to read its
  own uncommitted writes.

### MERGE INTO

`MERGE INTO` works on every provider that supports UPDATE/DELETE (SQL Server and Delta), including
`WHEN NOT MATCHED BY SOURCE`, per-action `AND` conditions, `DO NOTHING` and `ERROR`:

```sql
BEGIN;
MERGE INTO mssql.dbo.target AS t USING staging AS s ON t.id = s.id
  WHEN MATCHED AND s.deleted THEN DELETE
  WHEN MATCHED               THEN UPDATE SET name = s.name
  WHEN NOT MATCHED           THEN INSERT (id, name) VALUES (s.id, s.name);
COMMIT;
```

Each action is executed by the same rowid machinery as the standalone statement, so the same
requirements apply: **the target needs a primary key or unique index** (row identity is how DuckDB tells
a matched row from an unmatched one, so this holds even for a merge whose only action is an INSERT), and
`MERGE … RETURNING` is not supported.

**On Delta, a merge carrying two or more `UPDATE`/`DELETE` actions is FUSED into one commit — in autocommit
as well as inside a transaction — and that is a correctness mechanism, not a convenience.** Those actions all
address rows located by one scan of the join, so applying them one commit at a time is unsound: a
copy-on-write delete renumbers the rows the other action already located. Such a merge is therefore always
staged against a single snapshot.

The rule counts `UPDATE`/`DELETE` actions only, because an `INSERT` addresses no existing rows:

| merge shape | fused? | on a `deletion_vectors=false` target |
|---|---|---|
| one `UPDATE`/`DELETE` (± an `INSERT`) | no — one commit per action | works (copy-on-write) |
| two or more `UPDATE`/`DELETE` actions | **yes, one commit** | **refused** |

The refusal is the price of the guarantee: fusing requires deletion vectors, so on a table created with
`deletion_vectors=false` a merge with two such actions reports *"requires deletion vectors on the table when
it is buffered … Enable deletion vectors on the table, or use at most one UPDATE/DELETE action per merge
outside a transaction."* Deletion vectors are on by default, so this only affects tables that switched them
off — typically for readers that cannot consume them, such as SQL Server's external-table Delta reader.

*(DuckLake draws the same line and stops there — it [refuses two such actions
outright](https://ducklake.select/docs/stable/duckdb/usage/upserting). We serve them by fusing, and refuse
only when the table cannot be buffered at all.)*

⚠ **A fused merge reports `operation = 'TRANSACTION'`, never `'MERGE'`.** Another engine's
`DESCRIBE HISTORY` will not match on `MERGE`. Wrap any merge in `BEGIN … COMMIT` if you want it atomic
regardless of how many actions it carries.

On SQL Server the actions run as per-row DML on the transaction's pinned connection (this is *not*
translated into a server-side T-SQL `MERGE`), so an explicit transaction is likewise what makes the
statement atomic.

`INSERT … ON CONFLICT` is **not supported**. DuckDB lowers it to a MERGE keyed on a unique constraint,
and fabricator tables advertise no indexes, so it fails at bind with *"no UNIQUE/PRIMARY KEY constraints
that refer to this table"*. On Delta that is also semantically right — Delta enforces no uniqueness on
user columns, so there is nothing to conflict against. Use `MERGE INTO` instead.

## CREATE TABLE AS / COPY TO

`CREATE TABLE AS` and `COPY TO` stream the result to SQL Server via `SqlBulkCopy`, auto-creating the
table from the Arrow schema. Both are bounded-memory (the dataset is never fully buffered).

```sql
CREATE TABLE mssql.dbo.summary AS SELECT region, count(*) AS n FROM big GROUP BY region;

COPY (SELECT * FROM src) TO 'mssql://mssql/dbo/target' (FORMAT mssql);
COPY src TO 'mssql.dbo.target'  (FORMAT mssql);
COPY src TO 'mssql.dbo.target'  (FORMAT 'bcp');               -- 'bcp' is an accepted alias
COPY src TO 'mssql.dbo.target'  (FORMAT 'bcp', REPLACE true); -- drop + recreate
```

COPY target = `mssql://catalog/schema/table` or `catalog.schema.table` (3-part only — temp-table /
empty-schema syntax is not supported). Options: `CREATE_TABLE` (default true), `REPLACE` (default
false). The target is registered in the catalog (queryable immediately). (This is the `FORMAT mssql`
catalog COPY; to write a **Delta** table to a raw path with no ATTACH, use `FORMAT delta` — see
[`COPY … TO` a Delta path](#copy--to-a-delta-path-no-attach).)

### Write parallelism (`SET threads`)

Writing to a fabricator table uses **all the threads DuckDB is configured for** — the scan, the filter, the
projection and the load run on several tasks at once. Measured on 2 M rows, `SET threads=1` against
`threads=4`:

| statement | 1 thread | 4 threads | |
|---|---|---|---|
| `CREATE TABLE … AS SELECT` (CPU-heavy projection) | 3.5 s | 2.1 s | 1.6x |
| `INSERT … SELECT` (same) | 3.3 s | 2.0 s | 1.7x |
| `DELETE … WHERE <CPU-heavy predicate>` | 3.2 s | 1.4 s | **2.4x** |
| `UPDATE … SET … WHERE <same>` | 7.2 s | 5.0 s | 1.4x |

(Before 2026-08-22 every one of those was flat in `SET threads`, because the whole pipeline ran on one task.)
A `DELETE` gains the most because its entire cost is that pipeline; an `UPDATE`'s remainder is the row read-back
and post-image write, which the provider does once at the end.

Two shapes keep a serial load, deliberately:

- **an explicit `ORDER BY` in the write** — `INSERT INTO t SELECT … ORDER BY x` keeps its rows arriving in
  order. ⚠ This costs far less than it sounds: only the step that *feeds the load* is single-threaded, while
  the scan and the projection run in the sort's own pipeline and still use every thread — measured 3.6 s → 1.9 s
  at threads 1 vs 4 on the same 2 M rows, i.e. essentially the unordered speed. Ordering *declared on the
  table* (`SORTED BY`, the `fabricator.sortedBy` property, a clustered Delta table) is unaffected either way:
  the provider applies it after the rows are collected, not by arrival order;
- **`INSERT … RETURNING`**, whose returned rows would otherwise come back in an arbitrary order.

⚠ One behaviour to know about if you write them: an `UPDATE … FROM other` whose join matches the **same target
row twice** is last-write-wins, and which of the two wins is now arbitrary. It was never a documented order (it
followed a hash join's probe order), but it used to be stable run to run. The reported row count is unaffected.

`COPY … TO` is the exception in the other direction: it is **serial unless you set
`preserve_insertion_order=false`** (3.4 s → 2.1 s on the same shape). DuckDB tells a copy function only whether
insertion order must be preserved, not *why*, so a copy cannot distinguish your `ORDER BY` from the default
setting the way an `INSERT` can — and silently dropping an `ORDER BY` would be the worse trade.

⚠ **What parallel writing costs**: which rows land in which parquet file is no longer deterministic, so a table
that was getting useful file clustering *incidentally* from its source order stops getting it. That affects
pruning speed, never correctness. Declare `SORTED BY` if you want a layout, or write with an `ORDER BY`.

### Type mapping (DuckDB → SQL Server, for CREATE / CTAS / COPY)

The write mapping is **profile-adaptive** — it follows the connected engine's collation, version, and edition:

| DuckDB | Box SQL Server / Azure SQL | Fabric Warehouse / Lakehouse |
|--------|----------------------------|------------------------------|
| `BOOLEAN` | `BIT` | `BIT` |
| `TINYINT`/`SMALLINT`/`INTEGER`/`BIGINT` | `TINYINT`/`SMALLINT`/`INT`/`BIGINT` | same |
| `FLOAT` / `DOUBLE` | `REAL` / `FLOAT` | same |
| `DECIMAL(p,s)` | `DECIMAL(p,s)` | same |
| `VARCHAR` | `NVARCHAR(n)` (non-UTF-8 collation) / `VARCHAR(n)` (UTF-8 collation) | `VARCHAR(n)` (UTF-8) |
| `BLOB` | `VARBINARY(MAX)` | same |
| `DATE` | `DATE` | `DATE` |
| `TIME` / `TIMESTAMP` | `TIME(7)` / `DATETIME2(7)` | `TIME(6)` / `DATETIME2(6)` |
| `TIMESTAMP WITH TIME ZONE` | `DATETIMEOFFSET(7)` | UTC `DATETIME2(6)` (no `datetimeoffset`) |
| `UUID` | `UNIQUEIDENTIFIER` | `UNIQUEIDENTIFIER` |

- **Text type** is driven by the **collation** (a `_UTF8` collation → `VARCHAR`, else `NVARCHAR`), not the
  edition. Length `n` comes from `mssql_default_varchar_length` (unset ⇒ `MAX`); `mssql_ctas_text_type` is a
  whole-type override (e.g. `'VARCHAR(64)'`). `MAX` columns can't be a `PRIMARY KEY`/`UNIQUE` key, so set a
  length for indexable string keys.
- **`datetime2`/`time` scale** is the engine max (7 on box, 6 on Fabric).

On **reads**, SQL Server types map to DuckDB via SqlClient + Arrow:
`int`/`tinyint`→`UINTEGER`/`UTINYINT`, `decimal(p,s)`/`money`→`DECIMAL`, `bit`→`BOOLEAN`,
`date`→`DATE`, `time(≤6)`/`datetime2(≤6)`→`TIME`/`TIMESTAMP` (µs), **`time(7)`/`datetime2(7)`→`TIME_NS`/`TIMESTAMP_NS`**
(the 100 ns digit is preserved), `datetimeoffset`→`TIMESTAMPTZ`, **`uniqueidentifier`→`UUID`**,
**`json`→`JSON`** (SQL Server 2025; via the `arrow.uuid`/`arrow.json` Arrow extensions), `varbinary`→`BLOB`.

## DDL

```sql
CREATE SCHEMA mssql.staging;
CREATE TABLE mssql.staging.t (id INTEGER NOT NULL, name VARCHAR, amount DECIMAL(10,2));
CREATE TABLE IF NOT EXISTS mssql.staging.t (id INTEGER);

-- PRIMARY KEY / UNIQUE (a PK enables rowid → UPDATE/DELETE); literal DEFAULTs are carried over.
CREATE TABLE mssql.staging.k (
  id INTEGER PRIMARY KEY, a INTEGER, b INTEGER,
  status VARCHAR DEFAULT 'active', qty INTEGER DEFAULT 0,
  UNIQUE (a, b));

-- ALTER TABLE: rename table/column, add/drop column, change type, toggle NOT NULL, set/drop DEFAULT.
ALTER TABLE mssql.staging.t ADD COLUMN note VARCHAR;
ALTER TABLE mssql.staging.t ALTER COLUMN id TYPE BIGINT;
ALTER TABLE mssql.staging.t ALTER COLUMN note SET NOT NULL;
ALTER TABLE mssql.staging.t ALTER COLUMN note SET DEFAULT 'n/a';
ALTER TABLE mssql.staging.t RENAME COLUMN note TO comment;
ALTER TABLE mssql.staging.t RENAME TO t_renamed;

DROP TABLE mssql.staging.t_renamed;
DROP SCHEMA mssql.staging;
```

For SQL Server-specific features (IDENTITY, CHECK, indexes, FKs, non-literal DEFAULTs), use
`fabricator_exec`.

### Temporary tables

`CREATE TEMPORARY TABLE` is **not** mapped to SQL Server `#temp` tables — DuckDB routes every temp table to
its own in-memory/spillable `temp` catalog and forbids qualifying one with an attached catalog (`TEMPORARY
table names can only use the "temp" catalog`), so it never reaches the provider. Just use DuckDB's native temp
tables for transient data (faster — no round-trip, no connection affinity). If you specifically need a
*server-side* `#temp` (e.g. staging for a complex `EXEC`), create and use it via `fabricator_exec` **inside a
DuckDB `BEGIN`** so all calls share the one pinned connection — and only on a **MARS engine (box SQL Server)**:
`#temp` tables are connection-scoped, so they don't survive across autocommit calls, and on Fabric/Synapse
(MARS off → pooled reads) they aren't visible to subsequent statements.

## Function Reference

### `fabricator_query(context, sql) -> TABLE`

Stream a raw T-SQL query. `context` may be a connection string, a secret name, or an attached-catalog
name (reuses that catalog's connection).

```sql
SELECT id, name FROM fabricator_query('Server=...;Database=...', 'SELECT id, name FROM dbo.people');
SELECT * FROM fabricator_query('mssql', 'SELECT id, name FROM dbo.people');   -- attached catalog
```

**Use `fabricator_exec` for anything that writes.** `fabricator_query` is a table function, so DuckDB needs
its column types before it can plan — which the SQL Server provider answers with a *describe*
(`sp_describe_first_result_set`, no execution). A few statement shapes cannot be described (a batch that
creates and reads a temp table is the one we've measured), and those fall back to executing once to learn the
schema and once for the rows. So a side-effecting statement put through `fabricator_query` may run **twice**;
put through `fabricator_exec` it runs once and tells you how many rows it touched.

⚠ A broken query now fails while *binding*, with SQL Server's own message (`Invalid column name 'x'`) — one
phase earlier than before.

⚠ Inside an explicit `BEGIN … COMMIT`, `fabricator_query` **does** run on the transaction's connection once
one exists, so it sees the transaction's own uncommitted writes. Note the asymmetry with `fabricator_exec`
below, which joins only if a write has already pinned that connection.

### `fabricator_exec(context, sql) -> BIGINT`

Execute arbitrary T-SQL (DDL/DML/EXEC); returns rows affected (0 for DDL / no-row statements).

```sql
SELECT fabricator_exec('mssql', 'UPDATE dbo.people SET salary = salary + 1 WHERE id <= 2');
```

Multiple statements separated by `;` (including multiline) run as **one batch** in a single call (return
value = aggregate rows affected). `GO` is **not** supported — it's a sqlcmd/SSMS client directive, not T-SQL
(use `;`, or separate calls). For cross-statement atomicity, wrap in `BEGIN…COMMIT` or a DuckDB `BEGIN`.

```sql
SELECT fabricator_exec('mssql', 'CREATE TABLE dbo.t (id int); INSERT INTO dbo.t VALUES (1),(2)');
```


⚠ **Inside an explicit `BEGIN … COMMIT`, whether this joins the transaction depends on whether something
already wrote.** `fabricator_exec` joins the transaction's pinned provider connection only if one *already
exists* — i.e. only if a DuckDB-managed write has pinned it. Otherwise it autocommits on its own connection,
because a raw string-target exec never triggers the catalog's transaction lifecycle and nothing would ever
commit that connection. If you need a statement to be reliably part of the transaction, make sure the
transaction has written first, or use a purpose-built function (`db.dbo.fabricator_session_tag()` exists for
exactly this reason).

### `fabricator_refresh_cache(catalog)` / `fabricator_invalidate_cache(catalog [, schema [, table]])`

Refresh cached catalog metadata after creating/dropping tables out-of-band (e.g. via
`fabricator_exec`). `fabricator_refresh_cache` / `fabricator_invalidate_cache` are aliases.

```sql
SELECT fabricator_exec('mssql', 'CREATE TABLE dbo.t (id INT)');
SELECT fabricator_refresh_cache('mssql');
SELECT * FROM mssql.dbo.t;

-- ...or auto-invalidate after DDL run via fabricator_exec (off by default; DDL detected in C#):
SET mssql_exec_invalidate_cache = true;
SELECT fabricator_exec('mssql', 'CREATE TABLE dbo.t2 (id INT)');
SELECT * FROM mssql.dbo.t2;   -- visible immediately
```

Three things about the automatic mode that are easy to trip over:

- **The first argument must be an ATTACHED CATALOG.** Pass a connection string or a secret name and there is
  no cached catalog to refresh, so the setting appears to do nothing. Only `fabricator_exec('<catalog>', …)`
  triggers it.
- **DDL detection is a keyword match**, so it over-triggers: a statement merely *containing* `CREATE`,
  `DROP`, `ALTER`, `TRUNCATE`, `RENAME` or `EXEC` counts — including `UPDATE t SET created_at = …`. Harmless
  on SQL Server (a metadata re-read), but on a Delta catalog over OneLake/S3 re-discovery re-lists the store,
  so leave the setting off there and invalidate explicitly.
- **Prefer the scoped form when you know what changed** — `fabricator_invalidate_cache('mssql', 'dbo', 't')`
  (or a name regex) keeps the rest of the cache warm, where the automatic path re-discovers everything.

Without the setting, one asymmetry is worth knowing: a table **created** out-of-band is invisible until you
refresh, while a table **dropped** out-of-band is handled gracefully *only if you had not already read it in
this session* (you get a clean "table does not exist"). If you had read it, the drop surfaces later as the
server's own error — e.g. SQL Server `208: Invalid object name`. Refresh after out-of-band DDL and neither
case arises.

### `fabricator_server_info(catalog) -> TABLE(property, value)`

The detected server capability profile for an attached catalog (edition, version, collation, and the
derived flags: `supports_mars`, `has_nvarchar`, `has_datetimeoffset`, `max_datetime2_scale`,
`has_native_json`, `supports_cdc`, `is_utf8_collation`, `is_binary_collation`, `default_write_isolation`,
…). See
[Microsoft Fabric & Synapse](#microsoft-fabric--synapse-warehouse).

Plus one row that is **not** a server property: **`mars_enabled`** — whether *this catalog* actually uses
MARS, i.e. `supports_mars` combined with `mssql_mars` / the `mars` ATTACH option. Since that is resolved
once, at the catalog's first connection, it is the way to confirm a `SET mssql_mars=…` was in force early
enough to take effect:

```sql
SELECT property, value FROM fabricator_server_info('db')
WHERE property IN ('supports_mars', 'mars_enabled');
```

### `db.cdc.*` — change data capture (SQL Server only)

Set capture up, inspect it, and READ the change stream — over SQL Server's [change data
capture](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-data-capture-sql-server),
with no message queue anywhere in it. They live in the catalog's `cdc` schema, so they resolve as
`db.cdc.changes('dbo.orders')` and so on.

> **⚠ SQL Server only.** CDC is a SQL Server engine feature: **Fabric Warehouse, the Fabric Lakehouse SQL
> endpoint and Synapse dedicated pools do not have it**, and on those engines these functions are not
> registered at all — `db.cdc.max_position()` reports that it does not exist. Check with
> `SELECT value FROM fabricator_server_info('db') WHERE property = 'supports_cdc'`.

| function | returns |
|---|---|
| `db.cdc.tables()` | one row per capture **instance** — `source_schema`, `source_table`, `capture_instance`, `start_lsn`, `end_lsn`, `supports_net_changes`, `has_drop_pending`, `role_name`, `index_name`, `filegroup_name`, `create_date`, `index_column_list`, `captured_column_list`, `fabricator_source` (non-NULL ⇒ this extension created the instance) |
| `db.cdc.max_position()` | the current log position (`sys.fn_cdc_get_max_lsn`) as a `BLOB`, or `NULL` |
| `db.cdc.inc_position(<10-byte LSN>)` / `db.cdc.dec_position(<10-byte LSN>)` | the next / previous representable LSN (`sys.fn_cdc_increment_lsn` / `_decrement_lsn`). `NULL` in, `NULL` out. ⚠ Both **refuse at the ends of the range** where SQL Server's own functions wrap silently to the opposite end, and refuse a 21-byte `_position` — stepping a row's LSN would skip the other rows at that LSN, and `starting_position` is already exclusive at full granularity |
| `db.cdc.ddl_history([source :=] [, starting_position :=] [, ending_position :=])` | the recorded DDL against captured tables, resolved to `source_schema` / `source_table` / `capture_instance` and bounded by LSN. `required_column_update` is true for exactly an `ALTER COLUMN <type>`. ⚠ One row per (DDL × capture instance) — a table with two instances records each DDL twice — and read `WITH (NOLOCK)` |
| `db.cdc.lsn_time_mapping([starting_position :=] [, ending_position :=])` | the LSN ↔ time bridge, one row per captured transaction, database-wide. ⚠ `tran_end_time` is a `datetime` (~3.33 ms); `start_lsn` is the ordering key. Read `WITH (NOLOCK)` |
| `db.cdc.min_position('<capture instance>' \| '<schema>.<table>')` | the **retention floor** (`sys.fn_cdc_get_min_lsn`) as a `BLOB`, or `NULL`. For a table with two capture instances, the floor of the range `cdc.changes` can read — i.e. the older instance's |
| `db.cdc.enable_database()` | `sys.sp_cdc_enable_db` — idempotent |
| `db.cdc.enable('<schema>.<table>' [, capture_instance :=] [, columns :=] [, role :=] [, index :=] [, filegroup :=] [, net :=])` | `sys.sp_cdc_enable_table`. With no `capture_instance` it generates one (`fab_…`) and is idempotent **per table**; with one, per instance |
| `db.cdc.disable('<schema>.<table>' [, capture_instance :=])` | `sys.sp_cdc_disable_table`; with no instance named, all of that table's |
| `db.cdc.retire_previous_instance('<schema>.<table>')` | disables the **older** of a table's two capture instances, making room for the next `on_schema_change := 'resync'`. ⚠ **Destructive** — the retired instance's change history is gone. A table with fewer than two instances is a reported no-op, not an error; an instance this extension did not create is refused |
| `db.cdc.capture_now()` | `sys.sp_cdc_scan` — force the capture log scan now (see the ⚠ below) |
| `db.cdc.changes('<schema>.<table>' \| '<capture instance>' [, starting_position :=] [, ending_position :=] [, starting_timestamp :=] [, ending_timestamp :=] [, capture_instance :=] [, images :=] [, commit_timestamp :=] [, enable :=] [, on_schema_change :=] [, include :=])` | the change stream, and optionally an initial snapshot before it — see below |
| `db.cdc.health()` | 12 `(property, value)` rows, in this order: `supports_cdc`, `database`, `cdc_enabled`, `captured_instances`, `capture_job`, `capture_polling_interval_seconds`, `cleanup_job`, `cleanup_retention_minutes`, `max_lsn`, `max_lsn_time`, `max_lsn_age_seconds`, `agent_status` |

The four setup functions each return one report row — `target`, `changed`, `detail`. **`changed` is separate
from success**: a call that found the work already done succeeds and reports `changed = false`, so a setup
script is safe to re-run.

```sql
-- Set capture up from SQL, no sqlcmd needed.
SELECT * FROM db.cdc.enable_database();
SELECT * FROM db.cdc.enable('dbo.orders');

-- …then read the changes. The objects the enable created are usable IMMEDIATELY, in the same session —
-- no re-ATTACH, no refresh.
SELECT * FROM db.cdc.changes('dbo.orders');

-- What is captured, and where the log currently stands?
SELECT source_table, capture_instance, fabricator_source FROM db.cdc.tables();
SELECT db.cdc.max_position() AS pos, db.cdc.min_position('dbo.orders') AS retention_floor;

-- Why is nothing arriving?
SELECT * FROM db.cdc.health();
```

**Notes that will save you an afternoon:**

- **`NULL` is a state, not an error.** `max_position()` is `NULL` when CDC is not enabled on the database *and*
  when it is enabled but the capture job has not run yet. `health()` tells you which. (Calling
  `sys.fn_cdc_get_max_lsn()` yourself in the first case raises `208 Invalid object name
  'cdc.lsn_time_mapping'` — an error about an object you never mentioned, which is why these wrappers exist.)
- **`cdc.enable` / `cdc.disable` refresh the catalog for you.** Enabling capture is DDL: it creates a change
  table and two table functions, and until the catalog knows about them they are not merely missing from
  `duckdb_tables()` — they are unreachable, even by name. These functions report the change and the catalog
  rebuilds before your next statement, so nothing extra is needed. ⚠ One gap: inside a single explicit
  `BEGIN … COMMIT`, a later statement in that *same* transaction will not see them; call
  `fabricator_refresh_cache('db')` if you need that. Enabling capture via raw `fabricator_exec` instead needs
  the refresh either way.
- **There is no `disable_database()` on purpose.** `sp_cdc_disable_db` drops every capture instance in the
  database at once, destroying all recorded history — a bigger hammer than anything else here. Use
  `fabricator_exec` if you mean it.
- **⚠ `cdc.capture_now()` is a maintenance action, not a per-query one.** It forces the capture log scan
  immediately instead of waiting a polling interval, which costs CPU that belongs to your DBA's budget. It
  also contends with the capture job for the database's single log-scan session, so it can fail — the error
  says to stop the capture job for the duration or simply wait. Useful for tests and for a container with no
  SQL Server Agent. It does **not** read anything: it moves changes into the change tables, and nothing out.
- **"Enabled" and "happening" are independent.** `sp_cdc_enable_db` / `sp_cdc_enable_table` both succeed
  with SQL Server Agent stopped, so a table can look captured and never produce a row. That is what
  `health()`'s `agent_status` is for — and it reports `unknown` rather than guessing when the connection
  lacks `VIEW SERVER STATE`.
- **Capture-instance names are generated and you never need to type one.** `cdc.enable('dbo.orders')` creates
  `fab_<16 hex>` — 20 characters whatever your table is called, which also fixes a real failure: SQL Server's
  own default is `<schema>_<table>`, the limit is 100 characters, and a long table name therefore failed with
  `Msg 22927` about a name you never chose. Pass `capture_instance :=` if you want to name one yourself.
  `cdc.tables()` lists them, and everything else takes the TABLE name.
- **`cdc.enable` on an already-captured table is a no-op, and that is deliberate.** It reports
  `changed = false` and names the existing instance rather than adding a second. A second instance is a real
  change to what `cdc.changes` returns for that table — the declared columns become the union of both, and
  rows start carrying different `_capture_instance` values — and that is not something a bare `enable` should
  do silently. An explicit `capture_instance :=` is how you deliberately create the second (that is how a
  schema change is absorbed).
- **`fabricator_source` in `cdc.tables()` tells you which instances this extension created.** It is an
  extended property written atomically with the enable; `NULL` means someone else made that instance, and
  nothing here will manage it for you.
- **`min_position` takes the capture instance OR the table.** A table may have **two** capture instances (how
  a schema change is absorbed) and their retention floors can differ; for a table name it reports the **older**
  one's, which is where the range `cdc.changes` reads begins. If either floor is momentarily unknown the
  answer is `NULL` rather than the other one. `cdc.tables()` lists the instance names.
- **`max_lsn_age_seconds` is not lag.** It is the age of the newest *captured* transaction, so on an idle
  database it grows without bound while capture is perfectly current.
- SQL Server's own per-instance functions (`cdc.fn_cdc_get_all_changes_<instance>`, and the `_each` per-row
  form this extension adds for every discovered table function) are ordinary discovered functions and are
  callable directly.

#### Reading the change stream — `db.cdc.changes(...)`

```sql
FROM db.cdc.changes('dbo.orders'                    -- a <schema>.<table> or a capture-instance name
      [, starting_position := <BLOB>]               -- EXCLUSIVE lower bound: a 10-byte LSN or a 21-byte _position
      [, ending_position   := <BLOB>]               -- INCLUSIVE upper bound; default db.cdc.max_position()
      [, starting_timestamp := <TIMESTAMP>]         -- INCLUSIVE lower bound in wall-clock time
      [, ending_timestamp   := <TIMESTAMP>]         -- INCLUSIVE upper bound
      [, capture_instance  := '<name>']             -- read ONE instance of a table that has two
      [, images            := 'after']              -- 'after' (default) | 'both'
      [, commit_timestamp  := false]                -- opt-in, see below
      [, enable            := false]                -- capture the table on first read
      [, on_schema_change  := 'error']              -- 'error' (default) | 'ignore' | 'resync' | 'null' | 'fill'
      [, include           := 'changes'])           -- 'changes' (default) | 'snapshot' | 'snapshot+changes'
```

| column | type | |
|---|---|---|
| `_change_type` | `VARCHAR` | `insert` / `update_postimage` / `delete` (plus `update_preimage` under `images := 'both'`) — the same spellings the Delta change feed uses, so a consumer that handles one handles the other |
| `_position` | `BLOB(21)` | the resume token: `start_lsn ‖ seqval ‖ operation`, whose byte order **is** the change order |
| `_commit_lsn` | `BLOB(10)` | every row of one transaction shares it |
| `_seq_val` | `BLOB(10)` | |
| `_operation` | `INTEGER` | SQL Server's raw code (1 delete, 2 insert, 3 update **before**-image, 4 update after-image) |
| `_capture_instance` | `VARCHAR` | which capture instance produced the row — what makes a `NULL` in a source column decidable when a table has two (see the note below). **`NULL` means a `include := 'snapshot'` baseline row**, read from the source rather than from a capture instance |
| `_commit_timestamp` | `TIMESTAMP` | only with `commit_timestamp := true` |
| `_update_mask` | `BLOB` | only with `images := 'both'` — which columns the update actually recorded, as SQL Server's raw bit mask; `NULL` on a `include := 'snapshot'` baseline row |
| `_changed_columns` | `VARCHAR[]` | only with `images := 'both'` — the same answer as the NAMES, decoded for you. `NULL` (not an empty list) on a baseline row. See the before-image note below |
| …source columns… | as the source table | a `delete` row carries the **deleted** values |

**The resumable idiom.** Take the window END first, read a closed window, then advance the cursor to that
end — never to `max(_position)` of what came back, which is lower than what you read whenever you filtered
and `NULL` whenever the window was empty:

```sql
-- 1. the window end, taken BEFORE the read and stored whatever the read returns
SET VARIABLE cdc_end = (SELECT db.cdc.max_position());

-- 2. a closed window, resuming from your own cursor
SET VARIABLE cdc_cur = (SELECT cur FROM my_cursors WHERE tbl = 'dbo.orders');
INSERT INTO staging
SELECT * FROM db.cdc.changes('dbo.orders',
                             starting_position := getvariable('cdc_cur'),
                             ending_position   := getvariable('cdc_end'));

-- 3. advance to the WINDOW END
UPDATE my_cursors SET cur = getvariable('cdc_end') WHERE tbl = 'dbo.orders';
```

> **⚠ `SET VARIABLE` + `getvariable()` is not a stylistic choice — it is the only spelling that binds.** An
> inline scalar subquery is refused as a table-function argument (`Binder Error: Table function cannot
> contain subqueries`) **and** as an `EXECUTE` argument (`Only scalar parameters, named parameters or NULL
> supported for EXECUTE`). A scalar *function call* is fine in an `EXECUTE`, so
> `EXECUTE q(db.cdc.max_position())` works too.

**Notes:**

- **An empty window is empty, not an error.** A cursor sitting at the window end returns zero rows; so does a
  capture instance the job has not scanned yet.
- **⚠ A cursor below the retention floor is refused, loudly, and that is the point of the function.** SQL
  Server answers every bad window — a purged cursor, a bound above the watermark, an inverted window, even a
  misspelled option — with the *same* message: `Msg 313: An insufficient number of arguments were supplied
  for the procedure or function cdc.fn_cdc_get_all_changes_ ... .` on a call with three arguments. It names
  neither the cause nor your table (the `...` is a real placeholder object SQL Server calls to raise it), and
  it sends you to inspect a call site while the real news is that **your pipeline has lost data**. This
  function checks the window itself and says so.
- **⚠ A schema change inside your window is refused by default, and that is the point.** If someone runs
  `ALTER TABLE … ADD` while your window is open, the new column is **not captured** — the read would simply
  omit it and your pipeline would lose a field with nothing failing. `cdc.changes` checks
  `cdc.ddl_history` before returning rows and refuses, naming the statement and its LSN. Read up to the
  change and re-bind, or pass `on_schema_change := 'ignore'` to read anyway (which also saves the check's
  round trip). A window that *starts after* the change is clean again.
- **`on_schema_change := 'resync'` repairs the one case a re-capture can repair.** If the source has columns
  the capture instance does not — someone ran `ALTER TABLE … ADD` — a resync creates a **fresh capture
  instance** and answers with a **baseline of the whole table in the new shape**, plus a handoff to resume
  from. It keys on a metadata comparison, not on your window, so `DESCRIBE` already shows the new column;
  the DDL itself happens at execute, so an `EXPLAIN` or a `CREATE VIEW` captures nothing. It is deliberately
  **not** a default, and it declines rather than guesses in three cases: a **lower bound** beside it (a
  cursor and a fresh baseline are two different reads), an instance **this extension did not create** (a
  full-table snapshot on someone else's configuration is not a `SELECT`'s call), and a table that already
  has **two capture instances** — SQL Server's maximum, so making room means disabling the older one and
  destroying whatever history nobody has read. The message names that operator action. When nothing is
  stale, `resync` behaves exactly like `error`: a drift it cannot repair (a type change, a drop) is still
  refused rather than swallowed.
- **⚠ A resync is NOT re-runnable if its rows fail to land, and this is the sharp edge of the feature.** The
  new capture instance is DDL and commits immediately; the baseline rows are just a result set. So if your
  `INSERT INTO … SELECT … FROM cdc.changes(…, on_schema_change := 'resync')` fails while writing — disk,
  network, a constraint — the instance is created and the rows are not, and **re-running the identical
  statement is refused**, because the table now has two capture instances. Measured, not theorised.
  - **What to do instead: re-read with `on_schema_change := 'error'` (or `'ignore'`) and
    `include := 'snapshot+changes'`.** Both instances exist now, so an ordinary read already spans them as
    one stream in the aligned shape, and a fresh snapshot leg gives you a new baseline and a new handoff.
    Nothing is lost — you just take a *different* snapshot, at the retry's instant rather than the original's.
  - **Do NOT reach for `cdc.retire_previous_instance` here.** The refusal's message suggests disabling the
    older instance, which is right when consumers have caught up and wrong in exactly this case: your
    consumer never got the baseline, so its last good cursor is still inside the OLD instance, and retiring
    it destroys the history between that cursor and the resync. Retire once everyone is past the boundary,
    not to make a failed retry go through.
- **A plain snapshot leg, by contrast, IS fully re-runnable.** `include := 'snapshot'` and
  `'snapshot+changes'` without a resync perform no DDL and consume nothing — nothing server-side records
  that you took one. Re-running gives a fresh snapshot at a new instant with a later handoff. What you
  cannot do is *resume* a half-written snapshot: it is all-or-nothing per attempt, and the handoff rides in
  the rows, so if the rows do not land neither does the cursor.
- **An `UPDATE` produces ONE row by default** (the after-image); `images := 'both'` surfaces the pair as
  `update_preimage` + `update_postimage` and adds `_update_mask` — see below.
- **To bound how much you take in one pass, use `LIMIT` — there is no `max_rows` and there will not be
  one.** `LIMIT` + `ORDER BY _position` + a cursor taken from `max(_position)` is a complete resumable
  pagination, with no gap and no duplicate between pages:

  ```sql
  SET VARIABLE cur = <your stored cursor>;   -- or db.cdc.min_position('<instance>') to start
  CREATE OR REPLACE TEMP TABLE page AS
    SELECT * FROM db.cdc.changes('dbo.orders', starting_position := getvariable('cur'))
    ORDER BY _position LIMIT 10000;
  -- …consume `page`, then advance. An EMPTY page gives NULL: do not advance, you are caught up.
  SET VARIABLE cur = coalesce((SELECT max(_position) FROM page), getvariable('cur'));
  ```

  ⚠ A row-count bound can land **mid-transaction**. Nothing is lost — the 21-byte cursor resumes exactly —
  but a consumer that needs whole transactions should extend the page to a `_commit_lsn` boundary rather
  than cutting at the row count. That caveat is why a built-in `max_rows` would have been worse than this:
  it would have had to round down to a transaction boundary and then not return the number of rows its name
  promises.

- **Wall-clock bounds are INCLUSIVE, and a `_position` is not — that asymmetry is deliberate.** A
  `_position` is a resume token, so the row it names has already been read and the bound is EXCLUSIVE; a
  timestamp is an instant you have read nothing of, so `starting_timestamp` means *at or after*, and
  `ending_timestamp` *at or before*. Passing **both kinds on the same side is refused** rather than
  reconciled — "the tighter of the two" is not a rule you could predict. Mixing sides is fine
  (`starting_position := <cursor>, ending_timestamp := <instant>`). Two things to know: the timestamp is
  **naive on both sides**, so it means that instant *on the SQL Server host's clock*, not yours; and the
  resolution is `datetime` (~3.33 ms), so two transactions inside one tick are indistinguishable by design.

  ⚠ **The clock gap is real and it is silent.** DuckDB's `now()` is *your* wall clock: a session on
  `Europe/Berlin` reading a server on UTC produces `now()::TIMESTAMP` two hours **ahead** of anything that
  server has recorded, so `starting_timestamp := now() - INTERVAL 1 HOUR` names a window in the server's
  future and quietly returns nothing. Take the instant from the data (a row's `_commit_timestamp`, which is
  already the server's clock) or read the server's own — `SELECT * FROM fabricator_query('db', 'SELECT
  SYSDATETIME()')` — rather than from the client.
  A bound that maps to no captured transaction is an **empty window, not an error**; one that maps *below
  the retention floor* is **refused**, because the reader cannot tell "those changes were purged" from
  "this capture instance did not exist yet" and a wrong guess would return a short answer silently.
- **⚠ `images := 'both'` gives you the before-image — and a trap that comes with it.** SQL Server does
  **not store** a `varchar(max)` / `nvarchar(max)` / `varbinary(max)` column in an update's before-image
  unless that update touched it, so such a column reads `NULL` there whatever the row held. We deliberately
  do **not** substitute a placeholder — a placeholder is a value, so it cannot be told apart from a row that
  genuinely holds it. Read **`_changed_columns`** instead: if the column is not in the list, the value was
  not recorded; if it is, the `NULL` is genuine.

  ```sql
  SELECT _change_type, notes, list_contains(_changed_columns, 'notes') AS notes_recorded
  FROM db.cdc.changes('dbo.orders', images := 'both');
  ```

  `_update_mask` carries the same information as SQL Server's raw bit mask, if you want it. ⚠ **If you decode
  it yourself, count the bit from the RIGHT END of the whole mask** — it is a big-endian bit string over the
  entire `varbinary`, so on a table with more than eight captured columns the intuitive "byte
  `(ordinal-1)/8`" picks the wrong end and silently reports the wrong column:
  `get_bit(_update_mask::BIT, (8 * octet_length(_update_mask) - <ordinal>)::INTEGER)`, with ordinals from
  `db.cdc.captured_columns` joined to `db.cdc.change_tables` on `object_id`. ⚠ A mask is only decodable
  against the instance that produced it, which is what `_capture_instance` is for — and what
  `_changed_columns` already accounts for.
- **`commit_timestamp` is opt-in because it costs a join.** It is the only column that needs
  `cdc.lsn_time_mapping`, DuckDB will not eliminate that join when nothing selects from it, and the value is
  a `datetime` (~3.33 ms) — metadata, never an ordering key. `_position` is the ordering key.
- **⚠⚠ A table with two capture instances reads as ONE stream, and the `NULL`s in it mean two different
  things.** A second instance is how a schema change is absorbed: the older one keeps the old column set, the
  newer one has the new one, and **both capture every change** from the moment the newer exists — so reading
  both naively would return every later change twice. `cdc.changes` splits the window at the boundary (the
  newer instance's retention floor, which SQL Server records nowhere else) and reads each side once. The
  declared columns are the **union** of both instances, by name: the newer instance's columns first, then any
  the older one alone has, with missing ones `NULL`-filled.

  **Read `_capture_instance` before you read a `NULL`.** A `NULL` in a column the row's instance did not
  capture means *"this predates the column"*; the same `NULL` on a row from the instance that does capture it
  means the value really was `NULL`. Nothing else can tell you which.

  Two consequences worth knowing:
  - **A schema change *below* the boundary does not trigger `on_schema_change`.** The second instance exists
    because of it, and the union already carries both shapes. One issued *after* the newest instance still
    refuses — nobody re-captured for that one.
  - **A column dropped and re-added with a different type is refused**, naming both types and both instances.
    It is the only way two instances can disagree on a type (an `ALTER COLUMN` is propagated to both), and a
    union would coerce one era to the other's type — silently, wherever the values happen to convert. Read
    each instance on its own with `capture_instance :=`.

  ⚠ For up to one polling interval after you create a second instance, the boundary does not exist yet and
  the read says so and asks you to retry. `capture_instance :=` works immediately.
- **Ordering is yours.** Rows arrive in no promised order; `ORDER BY _position` is correct replay order, and
  `_position` values compare as unsigned bytes so `min()`/`max()` work.
- **`enable := true` captures the table on first read**, so a pipeline needs no separate setup step. The DDL
  happens when the query RUNS, never when it is planned — `EXPLAIN`, `DESCRIBE` and `CREATE VIEW` over it
  capture nothing, because the output schema is derived from the source table.
  - ⚠ **It does not backfill.** Capture starts at the moment of the enable, so rows written before it are
    invisible and an `include := 'changes'` read returns **zero rows** — not an error, because that is a fact
    rather than a guess. Pair it with `include := 'snapshot+changes'` to get what is already in the table.
  - ⚠ It is idempotent: on an already-captured table it is a no-op and the changes simply arrive.

#### Starting from nothing — `include := 'snapshot'` and `'snapshot+changes'`

A change stream can only tell you what CHANGED. To fill an empty sink you also need the state that was
already there, joined to the stream at a position where nothing is missed and nothing arrives twice:

```sql
-- capture the table AND read everything already in it, in one statement
FROM db.cdc.changes('dbo.orders', enable := true, include := 'snapshot+changes');

-- or take the baseline alone and keep its handoff for the poller
SET VARIABLE cdc_cur = (SELECT DISTINCT _position
                        FROM db.cdc.changes('dbo.orders', include := 'snapshot'));
```

Baseline rows arrive **first**, then the changes after them. A baseline row has `_change_type = 'insert'`
(the Delta change-feed spelling, so an existing consumer needs no new branch) and **`_capture_instance IS
NULL`** — that is how you tell a baseline row from a change. Its `_commit_lsn`, `_seq_val` and
`_commit_timestamp` are `NULL`, because a baseline row is state rather than an event. Every baseline row
carries the **same** `_position`: the handoff, which is what you resume from.

How it works, and what it costs: one connection takes a **shared** table lock on the source — writers are
frozen, ordinary readers are not — reads the handoff position, and a second connection pins a `SNAPSHOT`
view inside that window. The lock is then released, and the table is read at leisure from the pinned view.
The freeze lasts milliseconds, not the length of the read.

> **⚠ It needs `ALLOW_SNAPSHOT_ISOLATION ON` for the database.** Without it the read refuses and names the
> `ALTER DATABASE` to run. `READ_COMMITTED_SNAPSHOT` is *not* required.

**Exactly-once, or at-least-once, and you can tell which.** The capture job is asynchronous, so a
transaction that committed just before the lock may not be captured yet — its rows would be in the baseline
*and* above the handoff, i.e. delivered twice. Closing that gap needs `sys.sp_cdc_scan` to run inside the
lock window, and **the capture job holds the log-scan session continuously while it is running**, so on a
database with a running capture job that call is refused and the read is at-least-once. It says so at
`WARNING` level (visible in `duckdb_logs`), it never loses a row, and it is the guarantee the usual tools for
this handoff give unconditionally. Stop the capture job (`sys.sp_cdc_stop_job`) if the duplicates matter.

**What it refuses, and why:**

- **`starting_position` beside a snapshot** — a snapshot *is* the starting point. A cursor next to it either
  replays everything since that cursor or skips what came before it; neither is what anyone means.
- **`ending_position` with `'snapshot'` alone** — a snapshot is one instant, not a window. It *is* accepted
  with `'snapshot+changes'`, where it bounds the changes half.
- **A captured column that no longer exists on the source.** A snapshot reads the source table, so a column
  dropped from it has no value to read, while the change table keeps the column and reads `NULL`. Read
  `include := 'changes'`, or capture the table afresh.
- **A transaction that has written the table you are snapshotting.** The lock would wait for locks only your
  own transaction can release. `COMMIT` or `ROLLBACK` first, or read `include := 'changes'`, which takes no
  lock.

> **⚠ An EMPTY table snapshots to no rows, and therefore to no handoff.** `SELECT DISTINCT _position FROM …`
> is then `NULL`, and a `NULL` cursor means "no lower bound" rather than an error — so the next read starts
> at the retention floor and replays. Prefer `include := 'snapshot+changes'`, where the handoff never leaves
> the reader; if you do take the baseline alone, fall back to `db.cdc.max_position()` taken **before** the
> read, which is at worst a small replay and never a skip.

- **`on_schema_change := 'null'` and `'fill'` answer the added-column drift instead of refusing it.**
  `'null'` projects the uncaptured column with the source column's type and `NULL` on every row — decidable,
  because `_capture_instance` says which instance produced the row. `'fill'` looks the value up from the
  **live source** by key.
  ⚠⚠ **`'fill'` produces a torn row and this is not a rough edge, it is what the mode is.** The captured
  columns are as of the change's LSN; the filled column is as of **now** — and since that column is not
  captured, **no later change event will ever correct it**. A `delete` row fills `NULL`, because the source
  row is gone. It also needs a key: with no primary key and no unique index there is nothing to correlate
  on, and it refuses (use `'null'`, which needs no key). Both modes leave the capture instance alone — use
  `'resync'` if you want the columns genuinely captured from now on.

### `db.dbo.fabricator_session_tag(key, value) -> TABLE` (SQL Server / Fabric)

Tags the transaction's provider connection with a session-context key, so the statements it causes can be
correlated afterwards — e.g. attributing a dbt run's cost in Fabric's
[query insights](https://learn.microsoft.com/en-us/fabric/data-warehouse/query-insights). Returns one row:
`connection_id`, `session_id`, `dist_statement_id`, `tag_key`, `tag_value`.

```sql
BEGIN;
SELECT * FROM db.dbo.fabricator_session_tag('dbt_run_id', '7f3c…');   -- e.g. a dbt pre-hook
CREATE OR REPLACE TABLE db.dbo.orders AS SELECT * FROM staged;        -- attributed to that tag
COMMIT;
```

On a Fabric Warehouse, every statement of that transaction — including the bulk load — then shares one
`connection_id`, so you can total its cost:

```sql
SELECT count(*) AS statements, SUM(allocated_cpu_time_ms) AS cpu_ms
FROM fabricator_query('db',
  'SELECT allocated_cpu_time_ms FROM queryinsights.exec_requests_history
    WHERE connection_id = ''<the connection_id returned above>''');
```

- **Must be inside an explicit `BEGIN`…`COMMIT`.** In autocommit the connection it tags is released with that
  statement, so the tag would apply to nothing — the function raises instead of silently doing nothing.
- `SELECT fabricator_exec(…, 'EXEC sp_set_session_context …')` does **not** work as a substitute: a raw exec
  joins an existing pinned connection or takes a throwaway one, and nothing is pinned yet when a pre-hook runs.
- Key ≤ 128 bytes, value ≤ 8000 (the `sp_set_session_context` limits). Values are passed as parameters; the tag
  is also written into the statement text as a comment so it stays findable in the query history.
- ⚠ **Not reliable as a dbt pre-hook at `--threads > 1`.** Measured: the tag a model's own hook set was
  frequently *not* what its body saw — a stale value from an earlier run, which silently mis-attributes cost.
  It is dependable single-threaded, and within one explicit transaction generally.
- To attribute a whole **run**, set `application_name` on the secret instead. It is a connection-string property,
  so every connection the run opens carries it and it cannot go stale — and it needs no SQL changes. Combine it
  with `OPTION (LABEL = '…')` for per-statement grain. Details and the CU story:
  [`docs/consumption-monitoring.md`](docs/consumption-monitoring.md).

### `fabricator_version() -> VARCHAR`

Extension version (compatibility shim). `fabricator_managed_dir()` / `fabricator_test_scan('x')` are
diagnostics for the CoreCLR + Arrow spine.

### `fabricator_wait(rows, millis [, threads := …] [, hold_lock := …] [, async_wait := …]) -> TABLE(id BIGINT)`

A DIAGNOSTIC source: emits `rows` BIGINTs in 2048-row chunks, sleeping `millis` before each chunk. It contains
no Arrow, no .NET and no provider — so it answers scheduling questions ("does `SET threads` reach this plan?",
"do these `UNION ALL` branches overlap?") without the extension's own machinery as a rival explanation.

```sql
SET threads=4;
SELECT count(*) FROM fabricator_wait(8192, 500);            -- 4 chunks x 500 ms over 4 threads: ~0.5 s
SELECT count(*) FROM fabricator_wait(8192, 500, threads := 1);  -- the same work serialized: ~2.0 s
```

`threads` overrides what the scan reports to the planner; `hold_lock` and `async_wait` reproduce and then fix,
in pure C++, the worker-starvation shape described in [docs/scan-concurrency.md](docs/scan-concurrency.md) §5.
A negative argument is REFUSED rather than clamped — a sleep that never returns would hang rather than fail.

### `fabricator_plugins() -> TABLE(root, path, status, provider, detail)`

What the plugin scan looked at and what it decided — one row per configured plugin root, plus one per
candidate assembly under it. This is the first place to look when a plugin does not appear.

```sql
SELECT status, provider, detail FROM fabricator_plugins() WHERE status <> 'shared';
```

| `status` | meaning |
|---|---|
| `root` | the root was searched; `detail` gives the candidate count |
| `root_missing` | a configured root does not exist — the most common cause of "my plugin is not found" |
| `loaded` | loaded and registered `provider` |
| `no_backend` | loaded, but declares no provider. Normal for a plugin's own dependency — not a failure |
| `shared` | skipped because the extension already has an assembly of that name (deliberate) |
| `rejected` | could not be loaded; `detail` carries the reason, e.g. a mismatched Apache Arrow version |

**Where plugins are looked for.** `FABRICATOR_PLUGIN_DIR` (a comma-separated list of directories) if set —
which **replaces** the defaults rather than adding to them — otherwise **two** roots, in this order:

1. **`~/.duckdb/fabricator/plugins`**, the per-user root that `fabricator_install_plugin()` writes to;
2. the **bundled** root, `plugins/` inside the extension's managed directory (what `fabricator_managed_dir()`
   reports) — this is where plugins that ship with the release live.

**The first root wins.** If the same plugin is present in both, the one in (1) loads and the other is
reported `rejected` with a provider-name collision — so installing your own copy of a bundled plugin takes
precedence, and `fabricator_plugins()` tells you the shipped copy was set aside rather than silently ignored.
The search is recursive, so a plugin may sit in a nested folder such as
`~/.duckdb/fabricator/plugins/myplugin/1.0.0/windows_amd64/`.

A plugin's global functions are registered while the extension loads, so a plugin added to the folder becomes
available the **next time** DuckDB loads fabricator — not in the running session. See
[docs/plugin-system.md](docs/plugin-system.md).

#### The Fluid plugin — `fabricator_render(...)` and `fluid_query(...)`

The **Fluid / Liquid template engine** is a plugin, not part of the extension. It contributes two global
functions, both taking a params bag that is a DuckDB `STRUCT` (preferred — typed, no quoting), a `MAP`, or a
JSON string.

**`fabricator_render(template, params)`** renders a template to **text**:

```sql
SELECT fabricator_render('Hello {{ name }}, you have {{ n }} messages', {'name':'world','n':3});
-- Hello world, you have 3 messages

SELECT fabricator_render('{% if x > 1 %}big{% else %}small{% endif %}', '{"x":5}');
-- big
```

**`fluid_query(template [, params := …])`** renders a template to **SQL** — the result is a relation, and the
rendered text *is* the statement:

```sql
SELECT * FROM fluid_query('SELECT {{ n }} AS n', params := {'n': 7});
-- 7

-- The COLUMN LIST comes from the argument, so the output schema differs per call:
SELECT * FROM fluid_query(
  'SELECT {% for c in cols %}{{ c | sql_ident }}{% unless forloop.last %}, {% endunless %}{% endfor %}
   FROM (SELECT 1 AS a, 2 AS b, 3 AS c)',
  params := {'cols': ['a','c']});
-- a=1, c=3
```

`params` is optional, so `fluid_query('SELECT 1')` works. The call **disappears at bind time**: DuckDB
substitutes the generated statement for it, so the generated SQL's own scans keep their full projection and
filter pushdown, parallelism and join reordering, and nothing streams through the bridge at execution. That
also means the generator runs during binding, repeatedly and without executing anything — an `EXPLAIN`, a
`DESCRIBE` or a `CREATE VIEW` re-renders the template.

> ⚠ **`{{ x }}` is interpolated RAW into the SQL.** That is deliberate: a template must be able to emit table
> names, predicates and whole fragments, which is the only reason to generate SQL from a template. For values
> that are **data**, use the two filters the plugin registers:
>
> - `{{ v | sql }}` — a SQL **literal** (quoted string, invariant-culture number, typed date/time, `NULL`)
> - `{{ n | sql_ident }}` — a quoted **identifier**
>
> Both are allow-lists: a value with no provably safe rendering is refused by name rather than interpolated.
>
> ```sql
> SELECT * FROM fluid_query('SELECT {{ v | sql }} AS v', params := {'v': 'O''Brien'});
> -- O'Brien   (not a syntax error, and not an injection)
> ```

The bag can come from SQL rather than a literal — `params := ?` in a prepared statement (DuckDB re-binds
every `EXECUTE`, so the template is re-rendered and even the **column list may differ between two executes of
one prepared statement**), or `params := {'cols': getvariable('cols')}` to drive it from a session variable.

**A template can read from the database.** `query(sql)` runs a `SELECT` on the DuckDB you are already
connected to and hands the template its rows, addressable by name, by index and by `.size`:

```sql
SELECT fabricator_render(
  '{% assign rs = query("SELECT region, sum(amt) AS total FROM orders GROUP BY region ORDER BY region") %}
   {% for r in rs %}{{ r.region }}={{ r.total }} {% endfor %}', NULL);
-- eu=15 us=25
```

Inside `fluid_query` this runs while the statement is being **bound**, so the database can decide what the
generated SQL — and therefore the result's own column list — should be:

```sql
CREATE TABLE wanted AS SELECT * FROM (VALUES ('region',1),('amt',2)) v(col,ord);

SELECT * FROM fluid_query(
  'SELECT {% assign rs = query("SELECT col FROM wanted ORDER BY ord") %}
   {% for r in rs %}{{ r.col | sql_ident }}{% unless forloop.last %}, {% endunless %}{% endfor %}
   FROM orders ORDER BY amt');
-- columns: region, amt
```

**Parameters bind by name.** Write `query` as a **filter** and give it named arguments; the statement
references them as `$name`:

```sql
SELECT fabricator_render(
  '{% assign rs = "SELECT sum(amt) AS total FROM orders
                   WHERE region = $region AND amt > $min" | query: region: "eu", min: 6 %}
   {% for r in rs %}{{ r.total }}{% endfor %}', NULL);
-- 10
```

> ⚠ **It is a filter, not a second argument to `query(...)`** — because that is where Liquid puts named
> arguments. `query('sql', a: 5)` is a *parse* error, and Liquid has no dictionary literal, so
> `sql | query: a: 5` is the spelling that works.

The values are **bound, never spliced**, so a parameter can never become SQL:

```sql
SELECT fabricator_render(
  '{% assign rs = "SELECT count(*) AS n FROM orders WHERE region = $r"
    | query: r: "eu'' OR 1=1 --" %}{% for r in rs %}{{ r.n }}{% endfor %}', NULL);
-- 0   (one value that matches no region — not 3, which splicing would have given)
```

Numbers, strings, booleans, dates and `nil` can be bound; an integral number binds as `BIGINT` and a
fractional one keeps its exact scale. A list or struct is refused by name — build those in SQL. Every
parameter must be **named**: a positional argument is an error rather than silently ignored, and a
parameter the statement wants but you did not supply is reported by DuckDB, naming it.

> ⚠ **`query()` runs `SELECT` statements only, and this is not a policy you can turn off.** A template is
> rendered while a statement is being *bound*, and binding repeats and happens without executing — so a
> write here would fire on an `EXPLAIN` of a statement that never runs, and again every time a view over it
> is used. Anything else is refused before it runs, by DuckDB's own parser:
>
> ```sql
> SELECT fabricator_render('{% assign r = query("DELETE FROM orders") %}x', NULL);
> -- Error: query() runs SELECT statements only ... Only SELECT statements can be serialized to json!
> ```
>
> `WITH`-prefixed writes and `COPY … TO` are refused too, as is any multi-statement string containing a
> write (a string of nothing but `SELECT`s is allowed and harmless). `DESCRIBE`, `SUMMARIZE`, `VALUES`,
> `TABLE t`, `FROM t`, CTEs and set operations all work; **`PIVOT` and `EXPLAIN` are refused** although they
> are read-only — define a view outside the template if you need them. To write, use `exec()` below.

> ⚠ **`query()` reads COMMITTED data.** It runs on its own connection, so inside an explicit transaction it
> does **not** see that transaction's uncommitted rows — a template cannot observe the writes of the
> statement running it. Rows are held in memory (a template may loop over them repeatedly), and a result
> above one million rows is refused rather than truncated.
>
> It *does* inherit your session's **`TimeZone`** and **`search_path`** (and with them `current_catalog()`
> and `current_schema()`), so an unqualified `FROM t` resolves the way it does in the statement that
> rendered the template, and a timestamp renders in the zone you set. Name and time resolution are
> inherited; the transaction is not.

**A template can also WRITE — `exec(sql)`.** It is `query()`'s mirror image: `query()` runs `SELECT`
statements only, `exec()` runs everything else, and both decide with DuckDB's own parser. It returns the
affected-row count, so a template can report what it did:

```sql
SELECT fabricator_render('inserted={{ exec("INSERT INTO audit VALUES (1), (2), (3)") }}', NULL);
-- inserted=3
```

Parameters bind by name through the filter form, exactly as for `query()` — and, exactly as for `query()`,
the value is bound rather than spliced:

```sql
SELECT fabricator_render('deleted={{ "DELETE FROM orders WHERE region = $r" | exec: r: "eu" }}', NULL);
-- deleted=2
```

Unlike `query()`, the plain form takes **several statements in one call**, and the count is the last one's:

```sql
SELECT fabricator_render('{{ exec("CREATE TABLE m AS SELECT 1 AS c; INSERT INTO m VALUES (2)") }}', NULL);
-- 1        (and m now has 2 rows)
```

> ⚠⚠ **`exec()` works in `fabricator_render` and is REFUSED in `fluid_query`.** That is not a policy — it is
> the same fact that makes `query()` read-only. A `fluid_query` template is rendered while DuckDB is
> *binding*, and binding repeats and happens without executing, so a write there would fire on an `EXPLAIN`
> of a statement that never runs, again on merely **defining** a view over it, and again on every use of that
> view. Measured: with the refusal removed, one audit table went 1 → 2 → 3 through exactly those three steps.
>
> ```sql
> SELECT * FROM fluid_query('SELECT {{ exec("INSERT INTO audit VALUES (7)") }} AS n');
> -- Error: exec() is refused here ... Use fabricator_render(...) for a template that writes
> ```
>
> It catches the **accident**, which is the case that matters — writing `exec(...)` in a `fluid_query`
> template without knowing that binds repeat. It is not a sandbox: `query()` permits any `SELECT`, and a
> `SELECT` may itself call a writing function, so a template that means to write at bind time can. Nothing
> here is a privilege boundary — a template can already run SQL.

> ⚠ **A `SELECT` is refused by `exec()`, and the reason is a wrong number.** The count is read from the
> statement's first column, so `exec('SELECT count(*) FROM t')` would report the *count of rows in `t`* as
> though that many rows had been affected. It is refused by name instead, pointing you at `query()`.
>
> Note this makes `exec()` disagree with
> [`fabricator_host_exec`](#fabricator_host_execsql---tableaffected-bigint) on one shape: a `CREATE TABLE …
> AS SELECT` reports the rows it created here (`7`) and `0` there, because only the SQL-level function can
> ask the engine to classify the statement. Plain DDL is `0` in both.

> ⚠ **`fabricator_render` is a scalar, so `exec()` runs once PER ROW.** Rendering a writing template over
> three rows performs the write three times. For DDL, call it from a statement whose cardinality you chose —
> `SELECT fabricator_render(…)` with no `FROM` is one row. (An `EXPLAIN` does *not* run it: the function is
> volatile, so it is never folded into the plan.)

`exec()` gives a template no authority you did not already have — anyone who can call `fabricator_render`
can call [`fabricator_exec`](#fabricator_execcontext-sql---bigint) — and it reads and writes on its own connection,
so the same committed-data rule as `query()` applies.

Liquid control flow (`{% if %}`, `{% for %}`) works, comparisons and arithmetic filters work on numbers from
either kind of bag, and nested `STRUCT`/`MAP`/`LIST` members are reachable by name, by index and by `.size`.
Fluid is secure-by-default: only the variables you pass are reachable, and the only registered filters and
functions are the two SQL filters (`sql`, `sql_ident`) and `query` / `exec` above.

> ⚠ **Dates and times.** A `DATE`/`TIMESTAMP`/`TIMESTAMPTZ` renders in UTC (`2026-09-01 00:00:00Z`), and
> `{{ d | date: '%Y-%m-%d' }}` formats it. Two things to know:
>
> - **Fluid does not ORDER temporal values** — `{% if a > b %}` between two dates is always false, so compare
>   formatted strings instead: `{% assign x = a | date: '%Y-%m-%d' %}` … `{% if x > y %}`. ISO-8601 sorts
>   lexicographically, so that is exact.
> - `{{ d | sql }}` always emits a **`TIMESTAMPTZ`** literal (Fluid keeps one date type internally). With
>   the session on **UTC** as [recommended above](#quick-start) that is unambiguous. In a session left on
>   local time it is a trap: `::DATE`, `::TIMESTAMP::DATE`, `date_trunc`, `strftime` and `extract` all read
>   it in the *session's* zone, so west of UTC they silently give the previous day. Either of these is safe
>   in any session — `{{ d | date: '%Y-%m-%d' | sql }}::DATE` (no timezone in play at any step), or
>   `({{ d | sql }} AT TIME ZONE 'UTC')::DATE` (names the zone; also right for a `TIMESTAMP`).
>
> A `BLOB` arrives as a lowercase hex string.

> ⚠ **Numbers are `DECIMAL` inside a template.** JSON integers and decimals are carried exactly, but a value
> outside DuckDB `DECIMAL`'s range or below its resolution (roughly beyond ±7.9e28, or under ~1e-28) cannot be
> represented and is **refused with an error naming the value** — rather than rendering as `0` or failing
> deep inside the engine. Pass such a value as a string if you only need to print it. A `DOUBLE` with more
> than 15 significant digits renders rounded.

##### Reusing templates from files — `{% include %}` and `{% render %}`

A template can pull in another one from **any storage the extension can read** — a local directory, `s3://`,
`abfss://`, `onelake://` — so shared SQL fragments and macros live in files under version control instead of
inside SQL string literals:

```sql
SET fluid_template_root = 's3://analytics/templates';

-- templates/dims/customer.liquid holds:  SELECT id, name FROM customers WHERE region = {{ region | sql }}
SELECT * FROM fluid_query('{% include ''dims/customer'' %}', params := {'region': 'eu'});
```

The included template shares the caller's variables, and it may include others in turn.

> ⚠ **An absolute path needs no root at all** — `{% include 's3://bucket/templates/dims.liquid' %}` works
> whether or not `fluid_template_root` is set, which is the escape when a process-wide setting is the wrong
> granularity. The root is a convenience for writing short names, **not a sandbox**: a template that can
> `{% include %}` is being rendered by someone who can already run SQL here, and `query()` can read any path
> the host can open. What is refused is refused for predictability — `..` in a relative path, and the glob
> characters `*`, `?`, `[`, `]`, because one include must name one file.

> ⚠ **Name the extension.** `{% include 'dims/customer' %}` asks storage for `dims/customer` *and then* for
> `dims/customer.liquid` — two round trips on remote storage where `{% include 'dims/customer.liquid' %}`
> costs one. If both files exist, the one **without** the extension wins.

The file is read the same way `query()` runs SQL — on its own connection — so a location authorised by a
**persistent** secret works, while one authorised by a `CREATE SECRET` of the calling session does not. A
template above 1 MiB is refused, and a missing include reports every path it asked for.

**It ships with the released artifact** — bundled under the extension's own managed directory — so it needs
no configuration.

> ⚠ **Setting `FABRICATOR_PLUGIN_DIR` turns it off.** That variable **replaces** every default root rather
> than adding to it, so a session that sets it to pick up your own plugin also stops seeing the bundled ones
> and these functions will not resolve. `SELECT root, status, provider FROM fabricator_plugins()` shows
> exactly which roots were searched. To keep both, name the bundled root as well — it is the `plugins` folder
> inside the directory reported by `fabricator_managed_dir()`.

> ⚠ Building from source, it is not in the default build output. `dotnet build dotnet/Fabricator.FluidPlugin
> -c Release` writes it to `build/plugins/fluid`; point `FABRICATOR_PLUGIN_DIR` there, or install
> `build/plugins/fluid/Fabricator.FluidPlugin.plugin.zip` with `fabricator_install_plugin(...)`. Because they
> are **global** functions the plugin must be present when the extension loads — installing it mid-session
> surfaces them only at the next start.

### `fabricator_install_plugin(archive [, root := …] [, replace := …]) -> TABLE`

Unpack a plugin archive into a plugin root. The plugin's **provider** becomes usable immediately, in the same
session; its **global functions** appear at the next start (see the note above — that is a DuckDB constraint,
not a limitation of the installer).

```sql
-- Off by default: an installed plugin runs in-process with the extension's privileges.
SET fabricator_allow_plugin_install = true;

SELECT name, version, destination, files, providers, activated
FROM fabricator_install_plugin('/tmp/myplugin-1.0.0.zip');
```

| column | meaning |
|---|---|
| `name` / `version` | from the archive's manifest; also the install directory |
| `platform` | DuckDB's own platform string — which directory of the archive was taken |
| `destination` | `<root>/<name>/<version>` |
| `files` | how many files were written |
| `providers` | providers registered by the re-scan (empty if none) |
| `activated` | whether the plugin is usable **now** |
| `detail` | why not, when `activated` is false |

**The archive layout.** A `fabricator-plugin.json` manifest at the root, plus an `any/` directory
(platform-independent) and/or one named for a DuckDB platform. The install is their merge, with the platform
directory overlaying `any/`:

```
fabricator-plugin.json
any/MyPlugin.dll
windows_amd64/MyPlugin.Native.dll
linux_amd64/MyPlugin.Native.so
```

```json
{
  "formatVersion": 1,
  "name": "myplugin",
  "version": "1.0.0",
  "entryAssembly": "MyPlugin.dll",
  "abstractionsVersion": "1.0.0"
}
```

A *flat* archive — assemblies at the root, no directories — is refused rather than guessed at: the alternative
would be recognising a platform directory by its name, under which an archive shipping only `linux_amd64/`
would look flat on Windows and its Linux binaries would be installed.

**Upgrades.** The install directory is version-stamped, so a new version is written beside the running one and
takes effect at the next start; a loaded assembly can never be replaced in place. `replace := true` re-installs
the **same** version (it moves the old directory aside rather than deleting it, because a locked file can be
renamed but not removed).

Remote URLs are refused — download the archive first.

**A plugin may not take a provider name that is already registered.** An `IBackend` declaring `sqlserver`,
`delta`, `dax` or `deltars` is **refused**, and `fabricator_plugins()` reports it as `rejected` naming both
what it claimed and who already had it. Registration is a plain overwrite underneath, so without the refusal
such a plugin would silently replace the first-party provider and every later `ATTACH` would go somewhere you
did not choose.

### `fabricator_uninstall_plugin(name [, version := …] [, root := …]) -> TABLE`

Takes a plugin out of the scan, and reclaims its files when it can. One row per installed version; omitting
`version` removes them all.

```sql
SET fabricator_allow_plugin_install = true;      -- the same switch gates uninstalling
SELECT name, version, removed, purged, detail FROM fabricator_uninstall_plugin('myplugin');
```

| column | meaning |
|---|---|
| `removed` | it is **out of the scan** — the provider stops resolving immediately. `false` is the only real failure |
| `purged` | the **files are gone**. `false` is ordinary, not an error — see below |

**Why those are two columns.** An assembly loaded from a file is locked on Windows and the extension's load
context cannot be unloaded, so a plugin that has been *used* in this process cannot be deleted — but it *can*
be renamed. Uninstalling moves the version directory into a hidden `.trash` folder under the plugin root,
which takes it out of the scan at once; the bytes are swept on a later install or uninstall, by which time a
restart has usually released the lock.

⚠ The provider stops resolving, but the code stays in memory until the process exits — nothing can unload it.
The re-scan simply stops finding a candidate, so it is no longer registered.

### `fabricator_http_request(url [, method := …] [, headers := …] [, body := …]) -> TABLE`

Make an HTTP request through **DuckDB's own HTTP stack**, so it picks up DuckDB's configuration rather than
.NET's: the `TYPE http` secret whose scope covers the URL, `ca_cert_file`,
`enable_curl_server_cert_verification`, `http_proxy*`, `http_timeout` and the retry settings.

```sql
CREATE SECRET api (TYPE http, BEARER_TOKEN 'eyJhbGciOi…', SCOPE 'https://api.example.com');

SELECT status, reason, body
FROM fabricator_http_request('https://api.example.com/v1/things');
```

Needs the **`httpfs`** extension — it is what gives DuckDB a TLS-capable HTTP client and the `TYPE http`
secret reader. It is auto-loaded on the first request; if it is neither loaded nor installable you get a
message telling you to `INSTALL httpfs; LOAD httpfs;` rather than a confusing scheme error.

Returns one row: `status`, `reason`, `headers` (a JSON object), `body_bytes` and `body`. An HTTP error
status is a **row**, not an error — a 401 is something you look at — while a transport failure (DNS,
connect, TLS) raises. `body` is NULL if the response is not valid UTF-8.

It exists mainly to make the transport **observable**: the same path is what a plugin uses for a REST API
(`DuckDbHttpHandler`, an ordinary .NET `HttpMessageHandler`), and none of the inheritance above is visible
from inside the plugin. Reach for it first when a plugin's calls come out unauthenticated.

Limits, all inherited from DuckDB's HTTP layer: methods are **GET / PUT / HEAD / DELETE / POST** only (a
`PATCH` is refused by name rather than sent as something else); **one value per header name** in both
directions, so repeated headers such as `Set-Cookie` cannot be carried; bodies are fully buffered, so a
large result must be paged rather than streamed; redirects are followed; responses are **not**
decompressed, so do not send an `Accept-Encoding` you cannot handle yourself.

⚠ `TYPE http` carries a **static** credential — `BEARER_TOKEN` or `EXTRA_HTTP_HEADERS`. It does not perform
an OAuth2 client-credentials exchange, and `CLIENT_ID`/`CLIENT_SECRET` are not fields of it. An API using
OAuth2 needs its own secret type and its own token exchange; the transport still gives it everything else.

⚠ **It is unrestricted, deliberately, and worth knowing about.** Anyone who can run SQL in this session can
send any of the five methods to any URL, with whatever `TYPE http` secret matches it — including `PUT`,
`POST` and `DELETE`. That is a step beyond what `httpfs` alone exposes (which reads URLs but does not offer
arbitrary writes), though it is well within what this extension already permits: `fabricator_exec` runs
arbitrary SQL on an attached server, and `fabricator_install_plugin` loads code into the process. There is
no setting gating it today; say so if you want one.

### `fabricator_host_query(sql) -> TABLE`

Run a query on **DuckDB itself** and stream the result back through the extension. Mostly an internal
service — it is how the Delta provider reuses DuckDB's parquet reader — but it is callable, and useful for
seeing what the bridge sees.

```sql
SELECT * FROM fabricator_host_query('SELECT 1 AS a, ''x'' AS b');
```

It runs on a **fresh connection**, which has two visible consequences. It **inherits your session's active
catalog/schema and `TimeZone`**, so unqualified names and timestamps mean what they mean in your session:

```sql
USE lake.main;
SELECT * FROM fabricator_host_query('SELECT count(*) FROM t');   -- resolves lake.main.t
```

But it runs in its **own transaction**, so it sees only *committed* data — inside a `BEGIN`, your own
uncommitted `INSERT`s are invisible to it. That is deliberate (reusing the in-flight connection would corrupt
the outer query), not a bug to work around.

### `fabricator_host_exec(sql) -> TABLE(affected BIGINT)`

The **DDL/DML sibling**. Same fresh connection and committed-reads semantics; one row, one column — the
engine's affected-row count.

```sql
SELECT * FROM fabricator_host_exec('INSERT INTO t VALUES (1),(2),(3)');   -- affected = 3
```

Use it whenever you do not need a result set. `fabricator_host_query` has to *describe* your SQL to declare
its output columns, which DuckDB cannot do for **several statements in one string** — so those run twice
there, and a non-idempotent one fails outright. `fabricator_host_exec` declares a fixed schema, so it
describes nothing and **runs your SQL exactly once whatever it is**:

```sql
-- fails through fabricator_host_query ("Table with name t already exists!"); works here
SELECT * FROM fabricator_host_exec('CREATE TABLE t AS SELECT 1 AS c; INSERT INTO t VALUES (2)');
```

The count comes from the statement itself, so a `SELECT` reports `0` rather than its first value, and a
`CREATE` reports `0` even when it created rows. For several statements it is the **last** statement's count.

It is also available as a **scalar**, for symmetry with [`fabricator_exec`](#fabricator_execcontext-sql---bigint):

```sql
SELECT fabricator_host_exec('INSERT INTO t VALUES (1),(2)');   -- 2
```

> ⚠ **Prefer the table form for DDL.** The scalar is evaluated **per row**, so
> `SELECT fabricator_host_exec('…') FROM range(1000)` runs your statement a thousand times; the table form
> runs it once per scan whatever the cardinality. (Both are safe from the other hazard — the scalar is
> `VOLATILE`, so an `EXPLAIN` does not execute it.) A `NULL` statement yields `NULL`, not `0`.

### `fabricator_delta_scan(path) -> TABLE`

Read a Delta table **by path, with no ATTACH** — the quickest way to look at one:

```sql
SELECT * FROM fabricator_delta_scan('s3://bucket/lake/trips') LIMIT 10;
SELECT * FROM fabricator_delta_scan('abfss://ws@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables/t');
```

Filesystem credentials come from DuckDB secrets exactly as for `read_parquet`. For a whole folder of tables,
plus writes and DML, ATTACH the [Delta provider](#delta-lake-provider) instead.

### `fabricator_delta_native_scan(path) -> TABLE`

The same read with **DuckDB's own parquet reader** instead of the C# one — same argument, same rows, so it
is a drop-in swap when you want DuckDB's reader and its external file cache:

```sql
SELECT * FROM fabricator_delta_native_scan('abfss://ws@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables/t');
```

Both functions read the Delta log the same way — deletion vectors, partition columns, column mapping and
schema evolution are all applied — and both **prune columns**: naming three columns of forty reads three,
and `COUNT(*)` reads one. Filters are used to skip whole files and row groups (by partition value and by
column statistics), and DuckDB re-applies the predicate above the scan, so the answer is exact either way.
Which of the two is faster depends on the table and the storage; measure rather than assume.

### Provider macros

The provider ships DuckDB **macros** — SQL templates expanded by the binder, so nothing crosses into C# when
they run. They come in two flavours.

**Global**, under a bare name, available in every database with no ATTACH:

```sql
SELECT fabricator_bucket_of('alice', n := 16);          -- named parameter with a default
SELECT fabricator_rowid_parts(rid);                     -- scalar -> struct
SELECT * FROM fabricator_delta_head('/data/lake/t', n := 20);   -- a TABLE macro
```

**Catalog-bound**, resolved as `db.schema.name(...)`, so two attached catalogs can expose differently shaped
helpers under the same short name:

```sql
ATTACH '/data/lake' AS lake (TYPE fabricator, PROVIDER 'delta');
SELECT lake.main.fab_pct(25, 200);                 -- 12.5
SELECT lake.main.fab_clamp(150, hi := 120);        -- 120
SELECT * FROM lake.main.fab_numbers(5);            -- a TABLE macro
```

A catalog-bound macro is **not** callable under its bare name — that is the point of binding it to a schema.
Both flavours appear in `duckdb_functions()` with `function_type` `macro` or `table_macro`.

> One thing to know if you write your own: a schema gives **namespacing, not name resolution**. DuckDB expands
> a macro in the *caller's* context, so an unqualified table reference inside a macro body resolves against
> whatever catalog/schema the caller has active — not against the macro's own catalog. Keep bodies
> self-contained (expressions, or queries over built-ins), and use a SQL-generating table function when the
> body must reach into its own catalog's tables.

### Provider views

A provider can also ship **views** bound into an attached catalog's schemas. Unlike a macro, a view is a
**relation**: it shows up in `duckdb_views()` and `duckdb_columns()`, tools that enumerate the catalog find
it, and it can be used anywhere a table can.

```sql
ATTACH '/data/lake' AS lake (TYPE fabricator, PROVIDER 'delta');
SELECT * FROM lake.main.fab_view_info;             -- a declared view
SELECT view_name FROM duckdb_views() WHERE database_name = 'lake';
```

> **This is the form to use when the body must read the catalog's own tables.** DuckDB binds a view body
> against the *view's* catalog and schema, so an unqualified table reference inside it resolves there — which
> is exactly what the macro note above says a macro body cannot rely on. Nothing is bound until the view is
> first used, so a body may reference a table that does not exist yet; a missing one is an ordinary binder
> error at that point, and listing views never resolves anything.

A declared view cannot share a name with a table in the same schema. Both would resolve through the same
catalog lookup, so rather than silently prefer one, that name is refused with an error naming both sides —
every other view and table in the catalog keeps working.

### SQL-generating table functions

Some provider functions **rewrite themselves into SQL at bind time** instead of returning rows over the
bridge: the call disappears from the plan and what executes is a plain DuckDB query. Nothing crosses the
bridge at execution, and the generated SQL keeps its own pushdown. They are used where the SQL *text* has to
depend on the arguments (object names, `UNION` fan-out, a bind-time metadata lookup) — which a macro cannot
do, since a macro substitutes arguments as expressions with its structure fixed at declaration time.

They are called like any table function, and `fabricator_functions('db')` reports them as `kind='table_sql'`.
Because the rewrite happens at bind, `EXPLAIN` shows the generated plan rather than a function call.

### Microsoft Fabric platform functions (OneLake and Fabric SQL attaches)

When a catalog is attached over **OneLake** (Delta) or to a **Fabric SQL endpoint** (T-SQL), it additionally
hosts functions that call the **Fabric REST API**, so platform operations Microsoft offers no T-SQL for can be
driven from SQL. They **inherit the workspace, the item and the credential from the ATTACH** — which is why
most of them take no arguments at all.

**They live in a dedicated `fabric` schema on the attached catalog:** `<catalog>.fabric.<name>`.

```sql
ATTACH 'abfss://MyWS@onelake.dfs.fabric.microsoft.com/MyLH.Lakehouse/Tables' AS lake
  (TYPE fabricator, PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false);

SELECT table_name, status FROM lake.fabric.refresh_sql_endpoint();
SELECT * FROM lake.fabric.sessions();

-- What this attach resolved to (root, engine defaults, whether it is OneLake). Note this one is NOT
-- a Fabric REST function — it describes the Delta catalog itself, so it lives in the data schema:
SELECT * FROM lake.dbo.fab_delta_info();
```

That schema is a **function namespace, not storage**: it holds no tables, is backed by no folder, and
`CREATE TABLE <catalog>.fabric.t` is refused rather than quietly creating a `fabric/` directory next to your
data. It appears beside the data schemas (`dbo`, `dbt`, …) and only where the functions themselves are
registered.

They are registered **only** where there is a Fabric platform to talk to — a OneLake root, or a SQL attach
whose server is `*.fabric.microsoft.com`. A local or S3 Delta attach, or a plain SQL Server, has no workspace
and no REST endpoint, so it has no `fabric` schema at all.

#### On a Fabric SQL attach (Warehouse or Lakehouse SQL endpoint)

**Usually nothing extra is needed** — the workspace and item are derived from the connection string itself:

```sql
CREATE SECRET fabric_sp (TYPE azure, PROVIDER service_principal,
                         TENANT_ID '…', CLIENT_ID '…', CLIENT_SECRET '…');

ATTACH 'Server=<endpoint>.datawarehouse.fabric.microsoft.com;Database=MyLH' AS w
  (TYPE fabricator, SECRET fabric_sp);

SELECT table_name, status FROM w.fabric.refresh_sql_endpoint();
```

The **item** comes from `Database` (on a Fabric SQL endpoint the database *is* the lakehouse or warehouse), and
the **workspace** is decoded from the endpoint host, which encodes it. Override either only when you want the
functions to act on something other than what you attached:

| ATTACH option | meaning |
|---|---|
| `API_WORKSPACE` | Workspace the `fabric.*` functions target — display name or GUID. Default: decoded from the endpoint host |
| `API_ITEM` | Item the `fabric.*` functions target — display name, `Name.Type`, or GUID. Default: the connection string's `Database` |

> **These options scope the FUNCTIONS, not the attach** — hence the `API_` prefix. They do **not** change what
> you queried: with `Database=MyLH, API_ITEM 'OtherLH'`, `w.dbo.orders` still reads **MyLH** while
> `w.fabric.refresh_sql_endpoint()` syncs **OtherLH**. That is the point — it lets a dbt project attached to a
> Fabric **Warehouse** refresh a *lakehouse* endpoint without a second, Delta-only attach.
>
> The earlier names `WORKSPACE` / `ITEM` are **rejected with a migration message** rather than ignored, because
> silently dropping them would retarget the functions at the `Database` default without saying so.

**On a OneLake (Delta) attach neither option exists or is needed** — the root names both:
`abfss://<workspace>@onelake.dfs.fabric.microsoft.com/<item>.Lakehouse/Tables`. Both segments accept a **GUID**
as well as a name, and a GUID costs no lookup:

```sql
ATTACH 'abfss://<workspaceId>@onelake.dfs.fabric.microsoft.com/<itemId>/Tables' AS lake
  (TYPE fabricator, PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false);
```

> **Credential:** use a service-principal or managed-identity secret. A pre-minted `ACCESS_TOKEN` secret
> authenticates **SQL** but cannot be reused for the Fabric API — the two need different token audiences — so
> with one of those the functions fall back to the ambient Azure credential chain (which is what works on
> Fabric compute, and via `az login` / environment variables off it). On a `TYPE mssql` secret using
> `authentication = 'Active Directory Service Principal'`, also set `azure_tenant_id`: SqlClient infers the
> tenant from the server, but minting a Fabric token cannot.

Everything is optional except where noted, because the attach supplies the defaults. Where a function accepts
`workspace :=` / `item :=`, those OVERRIDE the attached ones — so one attach can drive several lakehouses,
which is the common case for a dbt project that writes to more than one:

```sql
SELECT * FROM lake.fabric.refresh_sql_endpoint();                  -- the attached lakehouse
SELECT * FROM lake.fabric.refresh_sql_endpoint(item := 'OtherLH'); -- a different one
```

| function | kind | what it does |
|---|---|---|
| `refresh_sql_endpoint([recreate := …] [, timeout_seconds := …] [, workspace := …] [, item := …])` | table | Syncs a lakehouse's **SQL analytics endpoint** now; one row per table |
| `list_shortcuts([parent_path := …] [, workspace := …] [, item := …])` | table | A lakehouse's OneLake shortcuts, optionally under one path |
| `create_shortcut(path, name, target_workspace, target_item, target_path)` | scalar | Creates a shortcut to another OneLake item; returns its full path. Fails if the name exists |
| `alter_shortcut(…same args…)` | scalar | Re-points an **existing** shortcut (fails if absent) |
| `create_shortcut_ex(…same args…, conflict_policy)` | scalar | Same, with `Abort` / `GenerateUniqueName` / `CreateOrOverwrite` / `OverwriteOnly` — use `CreateOrOverwrite` for idempotent scripts |
| `create_shortcut_json(path, name, target_json [, conflict_policy])` | scalar | Any target type (ADLS Gen2, S3, GCS, Blob, Dataverse, SharePoint) as the REST `target` object |
| `drop_shortcut(path, name [, if_exists])` | scalar | Deletes a shortcut; `if_exists := true` returns `false` instead of erroring |
| `workspaces()` | table | Every workspace this identity can see (id, name, type, capacity) |
| `items([item_type := …] [, workspace := …])` | table | Items in a workspace, optionally filtered (`'Notebook'`, `'Lakehouse'`, …) |
| `lakehouses([workspace := …])` | table | Lakehouses **with their SQL endpoint id, status and connection string** |
| `warehouses([workspace := …])` | table | Warehouses with their T-SQL connection strings |
| `connections()` | table | Cloud connections — the `id` an external shortcut target needs |
| `run_notebook(notebook [, params_json := …] [, config_json := …] [, wait_seconds := …] [, workspace := …])` | table | **Runs a notebook with parameters and blocks** until it finishes; one row of final state |
| `notebook_parameters(notebook)` | table | Names/defaults from a notebook's `parameters`-tagged cell |
| `table_maintenance(table [, schema := …] [, v_order := …] [, z_order_by := …] [, vacuum_retention := …] [, purge_deletion_vectors := …])` | table | Runs Fabric's maintenance job — **V-Order**, Z-order, VACUUM, purge deletion vectors |
| `run_job(item, job_type [, execution_data_json := …] [, wait_seconds := …])` | table | Any on-demand item job (e.g. a pipeline); blocks unless `wait_seconds := 0` |
| `job_status(item, job_instance_id [, workspace := …] [, item_type := …])` | table | One job instance's state |
| `job_instances([item := …] [, workspace := …] [, item_type := …])` | table | Job history for one item, **or fanned out across every item of a type** (`item_type := 'Notebook'`). Rows carry `item_name`/`item_id` |
| `cancel_job(item, job_instance_id)` | scalar | Requests cancellation |
| `sessions([workspace := …] [, all_workspaces := …])` | table | **What is running on Spark right now** — every Livy session in the workspace, with queued vs running time. One request, no item argument. `all_workspaces := true` covers every workspace you can see |
| `lakehouse_tables()` | table | Tables as **Fabric** sees them (flat lakehouses only — see below) |
| `operation_status(operation_id)` | table | Generic long-running-operation status |
| `reset_shortcut_cache()` | table | Clears the workspace's shortcut cache (**needs a user identity**) |
| `semantic_models()` | table | The workspace's semantic models, incl. each lakehouse's and warehouse's default |
| `refresh_semantic_model(model [, type := …] [, objects_json := …] [, commit_mode := …] [, timeout := …])` | table | **Refreshes a semantic model** (enhanced refresh) and blocks until it settles |
| `semantic_model_refreshes(model [, top := …])` | table | That model's refresh history |
| `git_status()` | table | Uncommitted/unpulled changes vs the connected branch, plus both heads. A clean workspace still returns one row (change columns NULL) |
| `git_connection()` | table | Which repository/branch/directory the workspace is connected to, and its last sync |
| `git_commit([mode := 'All'] [, comment := …] [, items_json := …] [, workspace_head := …])` | table | Commits the workspace to the branch. `mode := 'Selective'` needs `items_json` |
| `git_update(remote_commit_hash [, conflict_resolution := …] [, allow_override := …])` | table | Pulls that commit into the workspace (`PreferRemote` / `PreferWorkspace`) |
| `deployment_pipelines()` | table | Pipelines this identity can see |
| `deployment_pipeline_stages(pipeline)` / `_items(pipeline, stage)` | table | A pipeline's stages (with the workspace assigned to each) / a stage's contents |
| `deploy(pipeline, source_stage, target_stage [, note := …])` | table | Deploys one stage into the next and blocks; one row of operation state |
| `deployment_pipeline_operations(pipeline)` | table | Deployment history — same columns as `deploy` |
| `capacities()` | table | Capacities with SKU, region and state |
| `environments([workspace := …])` | table | Spark environments + `publish_state` — the id `run_notebook`'s `config_json` needs |
| `data_access_roles([item := …])` | table | OneLake data-access roles on an item (read only) |
| `mirrored_databases([workspace := …])` | table | Mirrored databases, incl. `onelake_tables_path` (attachable directly) and the SQL endpoint |
| `mirroring_status(database)` / `mirrored_tables(database)` | table | Whether replication is running / per-table state, rows, bytes and sync latency |
| `variable_libraries([workspace := …])` | table | Variable libraries and which value set each has active |
| `variables(library [, value_set := …] [, workspace := …])` | table | One row per variable, resolved against the active value set (or the one named) |
| `variable_value_sets(library [, workspace := …])` | table | The library's alternative value sets, in declared order, flagging the active one |
| `variable(library, name)` | scalar | One variable's value through the **active** value set — usable as an argument to the functions above |
| `create_variable_library(name, description)` | scalar | Creates an empty library; returns its id. **Refused for a service principal** — see below |
| `set_variable(library, name, type, value)` | scalar | Declares a variable or replaces its default value; returns `'created'`/`'updated'` |
| `set_variables_json(library, variables_json)` | scalar | **Replaces** the whole default set in one write; returns the count. Use this to declare several at once |
| `set_variable_override(library, value_set, name, value)` | scalar | Sets a value in an alternative set, creating the set if needed. Type comes from the declaration |
| `set_active_value_set(library, value_set)` | scalar | Switches which value set the library resolves through |
| `drop_variable_library(library [, if_exists])` | scalar | Deletes the library |

**Refreshing the SQL endpoint after a Delta write** — the reason this exists. A table written through the
Delta provider is invisible to the lakehouse's T-SQL endpoint until Fabric's asynchronous detection notices
it, which races any downstream T-SQL model:

```sql
SELECT table_name, status, error_message FROM lake.fabric.refresh_sql_endpoint();
```

> **`status = 'NotRun'` is normal and means "already in sync"** — not a failure. Check
> `status <> 'Failure'` (or `error_code IS NULL`); asserting `status = 'Success'` fails on a healthy
> refresh. On a schema-enabled lakehouse `table_name` is schema-qualified (`dbo.orders`).

> **In dbt, this hook must be non-transactional.** Delta commits land at DuckDB `COMMIT`, so an
> in-transaction post-hook would refresh *before* the table exists. Use
> `post_hook: [{sql: "SELECT count(*) FROM lake.fabric.refresh_sql_endpoint()", transaction: false}]`,
> or an `on-run-end` hook.

**Shortcuts.** `target_workspace` / `target_item` accept a display name or a GUID, and `NULL` means "this
catalog's own workspace / lakehouse":

```sql
SELECT lake.fabric.create_shortcut('Tables', 'ref_orders', NULL, NULL, 'Tables/orders');
SELECT path, name, target_type, target_location, target_subpath FROM lake.fabric.list_shortcuts();
SELECT lake.fabric.alter_shortcut('Tables', 'ref_orders', 'OtherWS', 'OtherLH', 'Tables/orders_v2');
SELECT lake.fabric.drop_shortcut('Tables', 'ref_orders', true);
```

`list_shortcuts` flattens the stable target fields into typed columns (`target_type`,
`target_workspace_id`, `target_item_id`, `target_path`, `target_location`, `target_subpath`,
`target_connection_id`) and additionally returns `target_json` with the complete target object, so target
types this build does not flatten are still readable. External targets need a pre-provisioned Fabric
**cloud connection**, whose `connectionId` goes in `target_json`:

```sql
-- Find the connection first, then use its id as the target's connectionId.
SELECT id, name, connection_type, path FROM lake.fabric.connections();

SELECT lake.fabric.create_shortcut_json('Files/landing', 'partner',
  '{"adlsGen2": {"location": "https://acct.dfs.core.windows.net",
                 "subpath": "/container/data", "connectionId": "…"}}');
```

**Variable libraries — per-environment configuration, read from SQL.** A Fabric *variable library* holds
named values plus alternative **value sets** (dev/test/prod), with exactly one active at a time — which a
deployment pipeline flips per stage. Reading it from SQL means a model does not have to hardcode which
lakehouse it writes to:

```sql
SELECT name, active_value_set FROM lake.fabric.variable_libraries();

SELECT name, type, value, is_overridden, value_set
FROM lake.fabric.variables('app_config') ORDER BY name;

SELECT name, is_active, override_count FROM lake.fabric.variable_value_sets('app_config');
```

`value` is the value as text and `value_json` is the same value as JSON (a string's is quoted, so it stays
parseable). An **`ItemReference`** variable carries `{"workspaceId": …, "itemId": …}`, which is exactly what
the `item :=` / `workspace :=` overrides above take — so the scalar feeds them directly:

```sql
-- Refresh whichever lakehouse the active value set points at.
SELECT count(*) FROM lake.fabric.refresh_sql_endpoint(
  item := lake.fabric.variable('app_config', 'target_lakehouse') ->> 'itemId');
```

> `variable` is a **pure** function, so a constant call folds to a literal once per statement rather
> than running per row. The flip side: a *prepared* statement bakes the value into its cached plan and will
> not see a later change to the library.

Writing works too. Declare several at once — each call is a read-modify-write of the whole item definition,
so one bulk write is much faster than one call per variable, and it is the only form immune to a concurrent
write losing your change:

```sql
SELECT lake.fabric.set_variables_json('app_config', '[
  {"name": "env_name",   "type": "String",  "value": "dev"},
  {"name": "batch_size", "type": "Integer", "value": 500},
  {"name": "target_lakehouse", "type": "ItemReference",
   "value": {"workspaceId": "…", "itemId": "…"}}
]');

-- One variable at a time, then a prod override and a switch.
SELECT lake.fabric.set_variable('app_config', 'retries', 'Integer', '3');
SELECT lake.fabric.set_variable_override('app_config', 'prod', 'batch_size', '50000');
SELECT lake.fabric.set_active_value_set('app_config', 'prod');
```

`type` decides how `value` is rendered, so `'500'` with `type := 'Integer'` stores `500` while
`type := 'String'` stores `"500"` — a wrong type here is refused rather than silently stored. An override
takes **no** type: the declaration owns it. Overriding a variable that is not declared is refused too,
because such an override would never resolve.

> **⚠ Creating a library needs a user identity.** `create_variable_library` returns
> `FeatureNotAvailable` for a service principal — despite Microsoft documenting these APIs as SP-supported,
> and despite the error naming the wrong cause (the feature *is* available; creation is what is refused).
> Create the library once in the portal; every other function here works fine with an SP.

> **Not cheap.** Reading or writing a definition is a long-running operation — tens of seconds. Prefer
> `set_variables_json`, and note that two `variable()` calls in one SELECT list are two reads.

**Table maintenance — the one optimize this extension cannot do itself.** **V-Order** is a proprietary
parquet layout optimization, so a table that Power BI or the SQL endpoint reads hot is worth passing through
Fabric's own maintenance job even though `OPTIMIZE` compacts it perfectly well here:

```sql
SELECT status, error_message FROM lake.fabric.table_maintenance(
  'orders', schema := 'dbo', v_order := true, z_order_by := 'order_date,customer_id');
```

Omitting the optimize knobs skips optimization, and omitting `vacuum_retention` skips vacuum — that is the
API's own convention, so a bare call does nothing rather than something surprising. `vacuum_retention` takes
Fabric's `d:hh:mm:ss` form (e.g. `'7:01:00:00'`).

Any other job type goes through the generic runner, and jobs can be watched or cancelled:

```sql
SELECT * FROM lake.fabric.run_job('nightly_pipeline', 'Pipeline', item_type := 'DataPipeline');

-- one item's history
SELECT job_type, status, start_time
FROM lake.fabric.job_instances(item := 'my_notebook', item_type := 'Notebook');

-- or FAN OUT: every notebook in the workspace, in one query
SELECT item_name, status, count(*) AS runs
FROM lake.fabric.job_instances(item_type := 'Notebook')
GROUP BY 1, 2 ORDER BY runs DESC;
```

> **Fan-out costs one API call per item.** Omitting `item` fans out across the items of `item_type`; omitting
> **both** is refused rather than sweeping the whole workspace, because that is unbounded and the API is throttled
> per principal. There is no `max_items` cap on purpose — a cap would under-report while looking complete.
> `item` is a **named** parameter (`item := 'x'`), which is what makes omitting it possible.

> A table function cannot take a **subquery** argument (`Binder Error: Table function cannot contain
> subqueries`), so pass `job_instance_id` as a literal. The scalar `cancel_job` accepts one.

**Spark sessions — what is running right now.** `job_instances` needs an item and answers "what jobs has
*this thing* run". `sessions()` takes no item at all and answers "what is on the Spark compute", which is
the question the portal makes tedious:

```sql
-- anything still going, longest-running first
SELECT operation_name, item_name, state, round(running_seconds) AS running_s, submitter_id
FROM lake.fabric.sessions()
WHERE state <> 'Succeeded'
ORDER BY running_seconds DESC;

-- where the compute actually went
SELECT operation_name, count(*) AS runs, round(avg(running_seconds), 1) AS avg_s
FROM lake.fabric.sessions() GROUP BY 1 ORDER BY runs DESC;
```

It reports one row per Livy session with the Spark-level detail no job instance carries — queued time separate
from running time, the runtime version, the attempt number and `spark_application_id` — and `job_instance_id`
joins it back to `job_instances`.

**Across every workspace you can see**, `all_workspaces := true` adds `workspace_name`/`workspace_id`:

```sql
SELECT workspace_name, item_name, state, round(running_seconds) AS running_s
FROM lake.fabric.sessions(all_workspaces := true)
WHERE state = 'InProgress'
ORDER BY running_seconds DESC;
```

That costs one request per workspace, so it is opt-in rather than the default, and it cannot be combined with
`workspace :=`.

> **Three gotchas, all measured.** ① The **same** work is labelled differently in the two surfaces: as a session
> it is `job_type = 'JupyterSession'`, `state = 'Succeeded'`; as a job instance it is `job_type = 'RunNotebook'`,
> `status = 'Completed'`. A predicate carried across matches nothing. ② `item_type` arrives **lower-case**
> (`notebook`) while `job_type` and `state` are PascalCase, and one state value contains a **space** —
> `'Not Started'`, not `'NotStarted'`. Use `lower()` and match defensively. ③ `submitter` (the display name) came
> back empty on every row while `submitter_id` was always populated — group by the id.
>
> **The Spark allocation columns** (`driver_cores`, `executor_cores`, `num_executors`, …) are populated only for
> a session that actually has an allocation. A **Python** notebook run (`runtime_version = 'jupyter1.0'`) has
> none, so they read NULL — that is the workload, not a broken column. Live cell output is not available from
> the API at all: `spark_application_id` and `resource_uri` are pointers to the portal.

**Semantic models — making a Delta write visible to Power BI.** Refreshing the SQL endpoint makes a new
table visible to **T-SQL**; refreshing the semantic model is what makes the data visible to **Power BI**. Both
a lakehouse and a warehouse have a *default* semantic model, named after the item itself:

```sql
SELECT name, is_refreshable FROM lake.fabric.semantic_models();

SELECT status, refresh_type, error_message
FROM lake.fabric.refresh_semantic_model('MyLakehouse');           -- the default model

SELECT status, extended_status FROM lake.fabric.semantic_model_refreshes('MyLakehouse', top := 5);
```

It issues an **enhanced refresh**, so the full contract is available — `type` (`Full`, `ClearValues`,
`Calculate`, `DataOnly`, `Automatic`, `Defragment`), `commit_mode` (`Transactional`/`PartialBatch`),
`max_parallelism`, `retry_count`, `timeout` (`hh:mm:ss`), and per-object targeting:

```sql
SELECT status FROM lake.fabric.refresh_semantic_model('Sales',
  type := 'Full', objects_json := '[{"table": "Orders", "partition": "2026"}]');
```

Notes:

- Enhanced refresh needs **Fabric/Premium capacity**; on shared capacity the API accepts only a notification
  option and caps refreshes at 8/day.
- These calls go to the **Power BI REST API** rather than the Fabric API — a different host but the *same*
  credential, so an ATTACH secret or ambient token that works for the functions above works here too. It does
  require the tenant's *"Service principals can call Fabric public APIs"* setting for a service principal,
  which is separate from any workspace role you granted.
- Power BI reports a refresh still in flight as `status = 'Unknown'`; `wait_seconds := 0` submits without
  waiting and you can poll with `semantic_model_refreshes`.
- For per-table or per-**partition** refresh, use `dax_refresh*` through the
  [DAX provider](#power-bi--dax-provider) — the REST API cannot address a partition at all.

**Introspection.** Useful on its own, and the source of the identifiers the calls above need — for example
attaching the lakehouse's T-SQL endpoint alongside the Delta catalog:

```sql
SELECT name, sql_endpoint_status, sql_endpoint_connection_string FROM lake.fabric.lakehouses();
SELECT id, name FROM lake.fabric.items(item_type := 'Notebook');
SELECT name, default_value, inferred_type FROM lake.fabric.notebook_parameters('my_notebook');
```

`notebook_parameters` is best-effort: Fabric injects parameter overrides after the cell tagged
`parameters`, which is ordinary Python, so this reads simple top-level `name = literal` lines. No rows means
the notebook has no tagged cell. It reads the notebook definition, which is a slow API (~20 s) — don't call
it per row.

**Running a notebook with parameters.** The notebook needs a cell tagged `parameters` (Fabric injects the
overrides after it). Pass a plain JSON object; string/number/boolean values map to Fabric's parameter types
automatically, and a `{"value": …, "type": "float"}` member overrides that inference:

```sql
SELECT status, exit_value, error_message
FROM lake.fabric.run_notebook('load_dims', '{"run_date": "2026-07-30", "full_refresh": true}');
```

It blocks until the run finishes (default cap one hour; a cold Spark session alone takes minutes) and
returns one row: `job_instance_id`, `status`, `start_time`, `end_time`, `exit_value`, `compute`,
`snapshot_url`, `error_code`, `error_message`. `snapshot_url` opens the run in the portal — the thing you
want when a run fails. Use `wait_seconds := 0` to submit without waiting.

> `exit_value` (from `notebookutils.notebook.exit(...)`) is **best-effort and often NULL** — it came back
> empty on every measured run, on both Python and Spark compute, even with the notebook-side API present
> and called. Don't build control flow on it; have the notebook write its result to a table instead.

> Parameters only reach a notebook via the shape this function sends. Fabric also *accepts* a generic
> top-level parameter array and then silently ignores it, so hand-rolled REST calls can appear to work
> while the notebook quietly runs on its defaults.

Notes and limits:

- A shortcut to a Delta table becomes visible to *this* catalog only after
  `SELECT fabricator_refresh_cache('lake')` — table discovery is cached.
- **Shortcut metadata is eventually consistent on Fabric's side.** Re-creating a just-dropped name can
  briefly fail with `EntityConflict` even though a listing already shows it gone. For idempotent scripts
  prefer `create_shortcut_ex(path, name, …, 'CreateOrOverwrite')` over drop-then-create.
- These calls take effect **immediately** and are not undone by a surrounding `ROLLBACK` (like Delta
  `DROP`/`OPTIMIZE`).
- Errors lead with Fabric's own error code, e.g. `EntityConflict`, `PrincipalTypeNotSupported`.
- Some Fabric APIs reject **service principals** regardless of documented support (notably resetting the
  shortcut cache and creating notebook items); those need a user identity.
- `connections()` lists only connections the **calling identity** has a role on — a service
  principal often sees none even where connection-backed shortcuts exist. Grant the SP access to the
  connection before creating external shortcuts with it.
- `lakehouse_tables()` is **rejected by Fabric on a schema-enabled lakehouse**
  (`UnsupportedOperationForSchemasEnabledLakehouse`); it works on a flat one. The extension's own catalog
  lists a schema-enabled lakehouse's tables anyway.
- `reset_shortcut_cache()` needs a **user identity** — a service principal is refused with
  `PrincipalTypeNotSupported`. It is the remedy when a re-created shortcut name transiently conflicts.
- `duckdb_functions()` also lists an `<name>_each` sibling for each table function. That is the generic
  per-row form and is not usable on this provider.

## Callable Functions

On `ATTACH`, the extension discovers the database's scalar UDFs, table-valued functions, and stored
procedures and registers them as **DuckDB catalog functions**, resolved as `db.schema.name(...)`.
Signatures and result schemas are resolved lazily on first use (so attach stays cheap) and refreshed by
`fabricator_refresh_cache`. All execution is vectorized over Arrow; the C++ side is provider-agnostic (the
SQL lives in C#). `fabricator_functions('db')` lists what was discovered.

```sql
SELECT schema_name, name, kind FROM fabricator_functions('mssql');   -- kind: scalar | table | proc | inout
```

### Scalar UDFs

A discovered scalar UDF is callable like a built-in; it runs vectorized (a whole argument batch per
round trip) and sees NULL arguments (SQL Server semantics):

```sql
SELECT mssql.dbo.fn_full_name(first, last) FROM mssql.dbo.people;
```

### Table-valued functions

Resolved as a table source, with **real projection + filter pushdown** (inline TVFs get inlined by SQL
Server, so the predicate/column-list reach the server — best-effort, never-erase, like table scans):

```sql
SELECT id, total FROM mssql.dbo.tf_orders(2024) WHERE total > 1000 ORDER BY id;
```

### Stored procedures

A proc with a determinable result set is a table function. Parameters are **named** (mirroring
`EXEC @p = …`); optional params fall back to the proc's own `DEFAULT`. A proc with `OUTPUT` parameters
returns them (plus the integer `RETURN` value) as flat columns:

```sql
SELECT * FROM mssql.dbo.usp_orders(region := 'US', top := 10);
SELECT sum, diff, return_value FROM mssql.dbo.usp_addsub(a := 10, b := 3);   -- OUTPUT params
```

### Custom (provider-authored) functions

You can author functions in **C#** (no SQL Server object needed) and they surface into every attached
catalog like discovered ones. Implement `ICatalogScalarFunction`, `ICatalogTableFunction`,
`ICatalogInOutFunction`, or `ICatalogAggregateFunction` (in `Fabricator.Bridge`) and register them in
`CustomFunctions` — each receives an Arrow `RecordBatch` and returns Arrow, fully vectorized.

#### A scalar's return type can depend on its arguments

A scalar function declares `Field? Result`. Declare it when the return type is fixed (the usual case — a
discovered SQL Server UDF's return type is metadata) and it is used for the catalog entry, so
`duckdb_functions()` reports it. Leave it `null` and override `Bind` to resolve the type **per call site**
from the call's constant arguments, the way `strptime` picks `TIMESTAMP` vs `TIMESTAMP_TZ` from its format
string:

```csharp
public Field? Result => null;                     // resolved at bind

public IScalarFunctionBinding Bind(ScalarBindArgs args)
{
    // ⚠ Only a CONSTANT argument has a value here — ConstantArray returns null otherwise.
    var kind = (args.ConstantArray(1) as StringArray)?.GetString(0);
    return new MyBinding(kind switch { "int" => Int64Type.Default, _ => StringType.Default });
}
```

The binding also gives you somewhere to do work **once per call site** instead of once per chunk (parse a
format string, compile a regex), which the previous stateless model had no room for.

Two rules the compiler cannot enforce:

- **Only constant arguments have values at bind.** A scalar may be called as `f(t.col)`, so
  `ScalarBindArgs.IsConstant(i)` is the guard; a non-constant slot holds a NULL placeholder that looks
  exactly like an explicit `NULL` literal. (Table functions differ here — *their* arguments must be
  constant.)
- **Bind values are pre-cast; the execute batch is authoritative.** DuckDB applies argument casts after the
  bind, so a literal `1.0` passed to a declared `INTEGER` parameter is a DOUBLE at bind and an INTEGER at
  `Invoke`. Use bind values to *decide*, the batch to *compute*.

A binding returning `Result = null` means "the declared type stands" — the cheap answer, and what the default
binding does, so a fixed-return function implements nothing new.

#### Variadic arguments (`Params.VarArgs`)

A **scalar**, **table** or **SQL-generating** function may take any number of trailing arguments. Declare the
tail with `Params.VarArgs(name)` — with no type it is DuckDB `ANY`, so each argument keeps its own type and
no cast is inserted; `Params.VarArgs(name, type)` gives a homogeneous tail instead:

```csharp
public Schema Parameters => new(new[]
{
    Params.Positional("sep", StringType.Default),
    Params.VarArgs("value"),          // any number of further arguments, any type
}, metadata: null);
```

```sql
SELECT fabricator_va_concat('-', 1, 'x', DATE '2020-01-01');   -- 1-x-01/01/2020 00:00:00
SELECT * FROM fabricator_va_args('demo', 42, 'hi', TRUE);      -- one row per tail argument
SELECT * FROM fabricator_va_values(1, 'x', TRUE);              -- SELECT 1 AS v0, 'x' AS v1, true AS v2
```

- **The fields before the tail are the MINIMUM arity**, and there is no maximum — this is DuckDB's own
  `varargs` mechanism, so `duckdb_functions()` reports it and a too-short call is refused at bind with the
  signature spelled `f(VARCHAR, [ANY...])`.
- **The args batch is wider than `Parameters`.** Tail columns follow the prefix in call order, named
  `<tail>_0`, `<tail>_1`, … — read the count from the batch, never from your own declaration.
- **At most one tail, and it must be the last positional parameter** (named parameters may follow). Both are
  refused where the signature is built, rather than reinterpreted.
- **A concrete tail is not "anything, coerced".** DuckDB applies its ordinary implicit-cast rules per
  argument, so a `BIGINT` tail takes `2::SMALLINT` and refuses `3.0` at bind. Declare the ANY form
  (`Params.VarArgs(name)`) when the point is to accept anything.
- **A `LIST` parameter already covers the homogeneous case** (`f(['a','b'])`) and needs none of this. What a
  tail buys is *heterogeneous, individually-typed* arguments — the shape of DuckDB's own `printf`,
  `concat_ws` and `struct_pack`.
- **Correlated LATERAL functions take one too**, and there it means something different: a lateral's
  positional slots are its per-row input columns, so the tail widens the **wire** rather than the args
  batch. `[Params.Constant][Params.Positional][Params.VarArgs]` composes — a bind-time constant may sit
  anywhere in the declaration, including first:
  ```sql
  SELECT t.n, f.* FROM t, fabricator_lat_span('a,b', t.n, t.n * 10, t.n * 100) f;
  ```
- **Table-in-out functions take one as bind-time arguments.** Their per-row input is the `TableInput`
  alone, so the tail widens the args batch and the input stream is untouched:
  ```sql
  SELECT * FROM fabricator_inout_va((SELECT * FROM range(3) t(n)), 'L', 1, 2);  -- 3 rows in, 3 out
  ```
- Aggregate functions **cannot** take one — their state marshal has no per-call-site width. Declaring one
  there is refused by name rather than silently registered as an ordinary argument.

### Table-in-out (`fn_each`)

A discovered TVF or stored proc *also* gets a sibling `fn_each(<input table>)` that applies the function
**once per input row** — output = the input columns + the function's output columns. (DuckDB forbids a
`TABLE`-parameter overload from sharing the scalar-arg name, hence the `_each` suffix.)

```sql
-- Apply tf_orders to every region in a table (CROSS APPLY, run in SQL Server):
SELECT * FROM mssql.dbo.tf_orders_each((SELECT region FROM regions));

-- Apply a stored procedure per input row:
SELECT * FROM mssql.dbo.usp_process_each((SELECT id FROM mssql.dbo.queue));
```

- **TVFs** combine via SQL-Server `CROSS APPLY` (T-SQL generated by C#); **stored procs** can't be
  CROSS-APPLY'd, so they're `EXEC`'d once per row.
- **Consistent view:** each call wraps all its per-chunk queries in one transaction at a configurable
  isolation level — set per catalog with the `isolation_level` ATTACH option, or per session with
  `SET mssql_isolation_level` (`read uncommitted` / `read committed` / `repeatable read` / `serializable`
  / `snapshot`; `snapshot` needs `ALLOW_SNAPSHOT_ISOLATION ON`):
  ```sql
  ATTACH 'mssql://…' AS mssql (TYPE fabricator, isolation_level 'snapshot');
  SET mssql_isolation_level = 'serializable';   -- session override
  ```
- **Stored-proc writes are transactional:** the per-row `EXEC`s run on DuckDB's transaction, so they
  commit/roll back with DuckDB — atomic in autocommit *and* inside an explicit `BEGIN … COMMIT/ROLLBACK`
  (a row failure mid-stream rolls back the whole statement).
- Custom C#-authored in-out functions (`ICatalogInOutFunction`, or the fixed-schema `StaticInOutFunction`
  base) are called by their **bare name** (e.g. `db.dbo.cf_tag(<table>)`, not `_each`) and stream on the same
  gate-based exchange; the author's `DoExchange` reads the input and yields output + a per-input sentinel.

### Correlated LATERAL functions (`ILateralFunction`)

A **row-mapped** function is the shape `fn_each` cannot express: its arguments are ordinary values, so it can
be written the way you would expect — correlated against an outer relation.

```sql
-- Split a column into rows, one output row per fragment, with the outer columns carried through:
SELECT s.id, p.part, p.idx FROM src s, mssql.dbo.cf_lat_split(s.txt, ',') p;

-- The same declaration with literal arguments (no correlation):
SELECT * FROM mssql.dbo.cf_lat_split('a,b,c', ',');

-- Global (connection-free, no ATTACH):
SELECT t.id, r.i FROM t, fabricator_lat_repeat(t.n, 3) r;
```

The mapping may be 1→1, 1→0 (filtering) or 1→N (fan-out), and the outer columns are carried onto every
emitted row. Author it in C# with `ILateralFunction` (`ICatalogLateralFunction` for a catalog-bound one)
— see [docs/lateral_unnest_analysis.md](docs/lateral_unnest_analysis.md) §8.

**Calls are BATCHED.** The whole input chunk crosses in one call (up to 2048 rows) rather than one call per
outer row, which is what makes a function whose per-call cost dominates — a REST call, a model invocation, a
per-row query — usable at all. Measured on 200 000 rows with a trivially cheap callee, i.e. crossing overhead
alone: **111 calls / 0.03 s** batched against **200 000 calls / 0.90 s** row-by-row.

```sql
SET fabricator_batched_lateral = false;   -- fall back to DuckDB's row-at-a-time driver
```

Two things are worth knowing before you author one:

- **The correlated values are DE-DUPLICATED before your function is called.** DuckDB's decorrelation puts a
  `DISTINCT` under the call and re-expands the result by joining above it, so a 20 000-row table with 97
  distinct argument tuples costs 97 rows of input, not 20 000. Cost scales with distinct tuples.
- **A named argument cannot be used in the correlated shape** — `fn(t.a, opt := 5)` does not bind (a DuckDB
  limitation, [docs/duckdb-upstream-issues.md](docs/duckdb-upstream-issues.md) §5). For a plain per-call
  constant, declaring it positionally (`fn(t.a, 5)`) works in both shapes — the value arrives as a constant
  input column. Named arguments do work with literal arguments.
- **A BIND-TIME constant — one the function needs while resolving its OUTPUT SCHEMA — is its own parameter
  style**: declare it with `Params.Constant("fields")` and read its typed value in `Bind`'s `args` (it never
  appears among the per-row input columns). A bare constant works in BOTH call shapes:

  ```sql
  SELECT * FROM fabricator_lat_fields(7, 'x,y,z');                 -- literal shape
  SELECT t.n, f.* FROM t, fabricator_lat_fields(t.n, 'x,y') f;    -- correlated: also just works
  ```

  The constant may be of any type (a string, a number, a LIST, a STRUCT…), and anything that *folds at
  bind* works: constant expressions (`upper('ab') || ',cd'`), `getvariable(…)` (the same SET VARIABLE idiom
  the CDC cursor uses), and prepared-statement parameters (`f(t.n, ?)` — DuckDB re-binds each EXECUTE, so
  each execution may even produce a different schema). In the correlated shape the value is recovered from
  the expression's own rendering, guarded three ways: columns are refused, volatiles (`random()`) are
  refused, and the folded value must match the slot's bound type. Anything the guards decline gets a clean
  bind-time refusal saying what a constant slot accepts.

### Custom aggregates (UDAF)

Provider-authored aggregate functions written in C# (`ICatalogAggregateFunction`) are registered as DuckDB
aggregates and usable wherever DuckDB allows one — `GROUP BY`, parallel aggregation, and window (`OVER`)
contexts — over **any** data (local or remote). They reduce in C#; there need be no SQL Server object.

```sql
ATTACH 'mssql://…' AS mssql (TYPE fabricator);

-- dbo.cf_product is a C# UDAF (no PRODUCT aggregate exists in SQL Server):
SELECT cf_product(x) FROM (VALUES (2),(3),(4)) t(x);         -- 24

SELECT g, mssql.dbo.cf_product(x) FROM t GROUP BY g;          -- grouped + parallel
SELECT cf_bit_or(x) OVER (ORDER BY id) FROM t;                -- window (running frame)
```

- **How it maps:** DuckDB owns a contiguous array of fixed-size state blobs; each blob holds only an
  `int64` id and the real per-group accumulator lives in C# behind it (one session per bound aggregate).
  The C# author implements `CreateState()` + `Update(batch)` / `Combine(other)` / `Finalize()`.
- **Window** works with no custom window callback — DuckDB drives it through `Update`/`Combine`/`Finalize`
  (segment-tree), which is cheaper for a marshaled bridge than one boundary crossing per output row.
- **Non-additive (holistic)** aggregates work too — make the accumulator collect its values in `Update`,
  **merge collections in `Combine`** (concatenation, not arithmetic — same as DuckDB's own `median`/`list`),
  and compute in `Finalize`. (Copy values out in `Update`; the batch is freed after it returns.)
- **Disk-spill is opt-in.** By default the state lives in C# (fast; bounded by managed memory, no spill). Set
  `SupportsSpill = true` (and implement `Serialize()`/`Load()` on the state) to switch to *bytes-in-blob*
  mode: the per-group state is serialized into DuckDB's fixed state blob (≤ 1 KB) so a huge-cardinality
  `GROUP BY` spills to disk under memory pressure — at the cost of (de)serializing on every step. (Holistic
  aggregates keep `SupportsSpill = false` — an unbounded collection can't fit the blob.) Demos:
  `dbo.cf_product`, `dbo.cf_bit_or` (fast), `dbo.cf_sum_spill` (spillable), `dbo.cf_median` (holistic).

## Settings

`SET mssql_*` settings; several are **active**, the rest are accepted for compatibility with the native
extension but are currently no-ops (the C# backend uses `SqlBulkCopy` and SqlClient pooling, so the native
extension's batching/pooling/TDS knobs don't apply). The Delta provider adds the `delta_write_options` JSON
setting (see [Partitioning & write tuning](#partitioning--write-tuning)).

**Scope.** A plain `SET` applies to the **connection that issues it** and is invisible to other connections
— so a tool that builds several models in parallel (dbt at `--threads > 1`) can configure one of them
without affecting the ones running alongside it. Use `SET GLOBAL` to change the value for every connection,
and `RESET` / `RESET GLOBAL` to undo the respective layer. A session value outranks a global one.

> ⚠ **Per-connection is not the same as per-model.** dbt-duckdb reuses connections across models (measured:
> 3 connections serving 4 models), so a `SET` in one model's pre-hook stays in force for the next model that
> happens to run on that connection. If you want a setting to apply to one model only, `RESET` it in a
> post-hook.

> ⚠ `RESET` does **not** fall back to the global value — it sets *this connection* back to the setting's own
> default, so a later `SET GLOBAL` will not reach a connection that has reset. (This is DuckDB's behaviour
> for extension settings, not ours; `SELECT current_setting('<name>')` reports what the connection will
> actually use.)
>
Every `mssql_*` setting is read when it is **used**, so a `SET` applies from the next statement — including
`mssql_mars`, which is resolved each time a connection is opened. The exception is a connection already in
use: a transaction's pinned connection keeps the MARS mode it was opened with for that transaction's life.

| Setting | Status | Description |
|---------|--------|-------------|
| `mssql_mars` | **Active** | MARS mode: `auto` (default, per engine — off for Fabric/Synapse) \| `true` \| `false`. Resolved **per connection, when it is opened**, so it applies to connections this catalog opens after the `SET` — no need to set it before `ATTACH`, and two DuckDB connections sharing one attached catalog can use different modes. A transaction's pinned connection keeps the mode it was opened with. Confirm with `SELECT value FROM fabricator_server_info('<cat>') WHERE property = 'mars_enabled'`. Because a `SET` covers the whole DuckDB connection, use the per-catalog **`mars` ATTACH option** when two catalogs must differ (a `SET` outranks it). ⚠ Forcing `false` on **box SQL Server**: a scan of a table the open transaction has already written cannot run (it would deadlock against its own transaction) and is **refused with a message naming the remedies** — enable `READ_COMMITTED_SNAPSHOT` on the database, turn MARS back on, or COMMIT first. Reads of other tables are unaffected, as are Fabric/Synapse. See [known-limitations.md](docs/known-limitations.md) 1.15 |
| `mssql_materialize` | **Active** | Buffer a scan that reads the **same catalog** a statement writes to, before the write starts. **Defaults to `true`, on every engine.** Required on MARS engines — without it `INSERT INTO t SELECT … FROM t` fails at scale with `595` — and it is what gives that scan **read-your-writes**, now including on engines with no MARS at all (Fabric, Synapse). Set `false` to keep the scan **streaming** instead: it then reads from a pooled connection at SNAPSHOT (needs `ALLOW_SNAPSHOT_ISOLATION` on the database) and therefore does **not** see rows the same transaction wrote — worth it for bulk movement, where a drained scan buffers the whole source in memory with no spill (a 1M-row same-catalog CTAS measured ~27% more CPU and 484 MB more allocation drained than streamed). Overrides the per-catalog `materialize` ATTACH option |
| `mssql_command_timeout` | **Active** | `SqlCommand.CommandTimeout` (seconds) for scans / DML / bulk; **default `0` = infinite**. Server-enforced per round-trip; overrides the per-catalog `command_timeout` ATTACH option |
| `mssql_default_varchar_length` | **Active** | Length `n` for created text columns (`NVARCHAR(n)`/`VARCHAR(n)`); unset ⇒ `MAX`. Needed for indexable string keys |
| `mssql_default_table_type` | **Active** | Created-table storage: `''` (rowstore) \| `clustered columnstore` (CCI, box/Azure; no-op on Fabric — columnstore already) |
| `mssql_cluster_by` | **Active** | Comma-separated columns → Fabric Warehouse/Synapse `WITH (CLUSTER BY (cols))` on created tables (fallback for a native `SORTED BY` clause; no-op on box) |
| `mssql_add_identity` | **Active** | Auto-add a `BIGINT IDENTITY` surrogate key (`<table>_id`) to created tables (CREATE + CTAS); overrides the per-catalog `add_identity` ATTACH option (`SET false` to skip for fact tables) |
| `mssql_ctas_text_type` | **Active** | Whole-type override for text columns on CREATE/CTAS/COPY (e.g. `'VARCHAR(64)'`); wins over the collation choice + length |
| `mssql_exec_invalidate_cache` | **Active** | Auto-invalidate the catalog cache after DDL run via `fabricator_exec` (default `false`) |
| `mssql_isolation_level` | **Active** | SQL transaction isolation level for table-in-out (`fn_each`) calls; overrides the ATTACH `isolation_level` per session (empty ⇒ provider default) |
| `mssql_read_isolation` | **Active** | **Opt-in, unset by default.** Isolation level for READS inside a DuckDB transaction: routes ordinary scans onto that transaction's own connection, opened at this level, so successive statements share **one view** (without it two identical `SELECT`s can differ — see [known-limitations.md](docs/known-limitations.md) 1.16). Use `'snapshot'`: it is the only level that delivers it (`'repeatable read'` still permits the phantoms a `count(*)` sees, `'serializable'` works by blocking writers), and on box it needs `ALLOW_SNAPSHOT_ISOLATION`. **Costs** a held connection + open transaction for the transaction's life, and on a no-MARS engine (Fabric/Synapse) reads are buffered instead of streamed, with no spill — measured 83 MB peak working set streaming vs 471 MB for a 389 MB result, so the ceiling is the largest single result the transaction reads. Overrides the per-catalog `read_isolation` ATTACH option |
| `mssql_copy_into_staging` | **Active** | **Opt-in, unset by default.** A storage location this extension may write temporary parquet to (`abfss://<fs>@<host>/<path>` or the equivalent `https://<host>/<fs>/<path>`). Set on a **Fabric Warehouse / Synapse** attach, a bulk write stages its parquet there with DuckDB's parallel writer and the warehouse ingests the folder with one `COPY INTO`, instead of streaming rows over TDS with `SqlBulkCopy`. Unset ⇒ `SqlBulkCopy`; **ignored** on engines without `COPY INTO`, so one session setting can span a mixed set of attaches. The staging identity needs read access via **Entra ID only** (no SAS/key), and the path must have no `_`- or `.`-prefixed segment — `COPY INTO` skips such names, which would load zero rows without an error, so it is refused at ATTACH. Overrides the per-catalog `copy_into_staging` ATTACH option |
| `mssql_insert_batch_size`, `mssql_insert_max_rows_per_statement`, `mssql_insert_max_sql_bytes`, `mssql_insert_use_returning_output` | Accepted | Registered with defaults + `>= 1` validation; no-op (INSERT streams via SqlBulkCopy) |
| `mssql_connection_*`, `mssql_*_timeout`, `mssql_min_connections`, `mssql_connection_cache` | Accepted | No-op (SqlClient pools by connection string) |
| `mssql_order_pushdown` | Accepted | No-op — TopN is pushed automatically when safe (always-on, not gated) |
| `mssql_copy_tablock`, `mssql_copy_flush_rows`, `mssql_ctas_use_bcp`, `mssql_convert_varchar_max`, `mssql_catalog_cache_ttl` | Accepted | No-op |

### Host settings

Two things the extension itself owns rather than a provider, so they exist even when no provider has loaded:

| Setting | Status | Description |
|---------|--------|-------------|
| `fabricator_allow_plugin_install` | **Active** | Whether `fabricator_install_plugin()` (see [Function Reference](#function-reference)) may write plugin assemblies into a plugin root. **Default `false`**: an installed plugin is loaded into this process and runs with the extension's privileges, so it is opt-in rather than something a SQL statement can arrange on its own. Session-scoped like every other setting, so `RESET` closes it again |

### Delta read tuning

A few Delta knobs are environment variables rather than `SET`, because they are read below the session. The
first two tune the native read path (`native_read`, on by default for `PROVIDER 'delta'`); the last two are
diagnostics you should never need:

| Variable | Default | Meaning |
|---|---|---|
| `FABRICATOR_DELTA_BATCH_MIN_FILES` | `2` | From this many files up, a scan reads its files in ONE `read_parquet([…])` instead of one query per file. `0` disables batching (every file gets its own query); `1` batches even a single-file scan |
| `FABRICATOR_DELTA_PREFETCH` | `1` | How many per-file queries run concurrently. `1` is sequential; higher values overlap cloud I/O, which is where they pay |
| `FABRICATOR_MATERIALIZE_COPY` | unset | Set to `1` to restore the pre-2026-08-07 Arrow IPC copy on the buffered WRITE path. Diagnostic only — the copy costs roughly twice the peak memory (measured 427 MB vs 232 MB on a 1.5M-row partitioned `INSERT`) and buys nothing |
| `FABRICATOR_ARROW_LIVENESS` | unset | `1` prints a batch-ownership audit at exit (how many Arrow batches crossed to the managed side, how many were freed, and by whom); `2` additionally prints every handover and free. For diagnosing a suspected Arrow lifetime bug; costs nothing when unset |

Batching matters most on a **fragmented** table — the shape an incremental dbt model grows, since every run
appends a file. It removes roughly 1.5–2 ms of per-file overhead, so on 200 files × 100 rows a scan goes from
0.46 s to 0.09 s, while on a table where decoding dominates the difference is proportionally smaller. Results
are identical either way; if you ever need to check that, run the same query with
`FABRICATOR_DELTA_BATCH_MIN_FILES=0`.

Deleted rows and `UPDATE`/`DELETE` are batched too: the deletion vectors of every file cross as ONE bound
input and are excluded by a single anti-join. Measured on 100 files that all carry a deletion vector,
0.42 s → 0.15 s. The trade is that a combined query cannot carry a per-file row-position bound, so it loses
some parquet row-group skipping on a heavily-deleted file — which measured as no wall-clock difference
locally, but is worth re-checking on remote storage.

Partitioned tables are batched as well. A partition value is not stored in the data files at all — the log
is the authoritative source — so it travels alongside them and is matched back to the file each row came
from, in the same query.

**On remote storage, which columns you select can dominate the query.** What decides it is whether the scan
needs a row *position*, because that is the one thing only the file can supply:

| you select | what the scan does | parquet footers opened |
|---|---|---|
| only partition columns (`SELECT p, count(*)`) | answers from the log alone | **none** |
| ordinary columns, no deletion vector in play | *declares* the schema up front | **none** |
| ordinary columns, some files carry deletion vectors | the clean files as above, one extra branch per deleted file | one per *deleted* file |
| anything needing a row position — `UPDATE`/`DELETE`, the row-tracking columns | *discovers* the schema from the files | **one per file, before the first row** |

The gap between declaring and discovering is the one to know about: measured on a Fabric lakehouse table
with 89 active files, same query, same data, **0.5 s versus 34 s** — enough to take a `SELECT … LIMIT 1` on
that table from 77 s to 47 s.

Selecting a partition column is *not* one of the expensive cases, and neither is `SELECT *` on a partitioned
table. (It was until 2026-08-17, and this section said so.) On remote storage a table where **any** file
carries a deletion vector still sends ordinary reads to the discovering form unless the query pushes a plain
`LIMIT`; locally it does not.

On local storage none of this matters much, since opening a footer is nearly free. `OPTIMIZE` helps every
form, because their costs scale with the number of active files — and on a heavily-deleted table it also
clears the deletion vectors, which moves remote reads back onto the cheap form.

### Text columns on SQL Server: VARCHAR vs NVARCHAR

A DuckDB `VARCHAR` is UTF-8. Which SQL Server type that becomes is decided **per connection, from the
database's collation** — so the same DuckDB code writes correctly to a legacy-collation server, a UTF-8
server, and Fabric without a per-target setting:

| target | column type | why |
|---|---|---|
| SQL Server, default (non-UTF-8) collation | `NVARCHAR` | `VARCHAR` there is a single-byte codepage — Unicode would be **lossy** |
| SQL Server, UTF-8 collation (e.g. `Latin1_General_100_CI_AS_SC_UTF8`) | `VARCHAR` | holds full Unicode, at roughly half the storage |
| Fabric Warehouse / SQL endpoint | `VARCHAR` | it is UTF-8, and the only option |

`CHAR`/`NCHAR` follow the same choice. So pointing an existing project at a new UTF-8 database is enough to
move it to `VARCHAR` — nothing in the model changes.

To override, either bound the length with `SET mssql_default_varchar_length = 200` (applies to whichever
type is chosen), or replace the type outright with `SET mssql_ctas_text_type = 'NVARCHAR(200)'` — e.g. to
keep `NVARCHAR` on a UTF-8 database during a migration. ⚠ Forcing `VARCHAR` on a non-UTF-8 collation is
lossy for non-ASCII text; the automatic rule exists to avoid exactly that.

Both have a **per-table form** in `CREATE TABLE … WITH (…)`, which outranks the session setting:

```sql
CREATE TABLE db.dbo.t (id INTEGER, label VARCHAR) WITH (varchar_length=200);      -- NVARCHAR(200)
CREATE TABLE db.dbo.t (id INTEGER, label VARCHAR) WITH (text_type='VARCHAR(37)'); -- VARCHAR(37)
CREATE TABLE db.dbo.t (id INTEGER) WITH (table_type='clustered columnstore');     -- CCI (box/Azure)
CREATE TABLE db.dbo.t (id INTEGER) WITH (table_type='HEAP');                      -- opt OUT of a CCI default
```

`table_type` carries two disjoint vocabularies and the value picks which: `CLUSTERED COLUMNSTORE` / `CCI` /
`HEAP` / `ROWSTORE` describe an ordinary table's storage, while `DELTA` / `PARQUET` describe an S3 external
table and require `location` (see the data-virtualization section). `varchar_length` / `text_type` are
refused on an external table — its column types come from the storage files.

### Parquet write options (Delta)

Settable per statement with `CREATE TABLE ... AS SELECT ... WITH (...)`, per session with
`SET delta_write_options`, or as an ATTACH default — same names on all three.

**A `WITH` on a `CREATE` is PERSISTED as a table property, and every later write to that table honours it** —
a plain `INSERT`, an `UPDATE`'s post-image file, a copy-on-write rewrite, and `OPTIMIZE`'s compaction output,
whichever catalog or session runs them:

```sql
CREATE TABLE t WITH (parquet_compression='zstd') AS SELECT ...;  -- ZSTD, and t now DECLARES zstd
INSERT INTO t SELECT ...;                                        -- ZSTD (reads the declaration)
SELECT fabricator_exec('lake', 'OPTIMIZE main.t');               -- ZSTD (so compaction cannot undo it)
```

That is what makes it worth configuring a dbt model once instead of re-stating the tuning on every
incremental run — and it means a table written zstd stays zstd when someone else compacts it. The keys are
stored in the Delta table configuration as `fabricator.parquet.*`, visible via
`<catalog>.delta.tblproperties('<schema.>table')`.

**Precedence, lowest first: ATTACH default < `SET delta_write_options` < the table's property < the
statement's `WITH`.** The property outranks the session setting deliberately — it is a property *of the
table*, so a stray `SET` in someone's session must not silently change a table's storage format; `WITH`
remains the per-statement escape hatch.

⚠ A `CREATE OR REPLACE` **inherits** the declaration and cannot **change** it: its `WITH` applies to that
statement's write only. (Same as every create flag — `deletion_vectors` and friends are also fixed at
creation.) To change a declaration, use
`SELECT * FROM lake.delta.set_tblproperties('main.t', '{"fabricator.parquet.compression":"gzip"}')` (a table function, so it needs the `FROM`).
A bare `CREATE TABLE ... WITH (<tuning>)` with no `AS SELECT` is still refused — there is no write for it to
apply to.

| option | engines |
|---|---|
| `parquet_compression` | both |
| `parquet_row_group_size` (rows) | both |
| `parquet_row_group_size_bytes` | both — ⚠ DuckDB requires `SET preserve_insertion_order=false` |
| `parquet_version` (`V1`/`V2`) | both |
| `parquet_bloom_filter_columns` | engineered-wood codec only — ⚠ and only with `column_mapping='none'`, see below |
| `parquet_compression_level` | both — the valid range is the codec's (zstd 1–22, gzip 1–9) |
| `parquet_bloom_filter_false_positive_ratio` | both — ⚠ the engines' DEFAULTS differ (DuckDB 0.01, engineered-wood 0.05) |
| `parquet_dictionary_size_limit` (distinct values) | `native_write` only |

⚠ `parquet_bloom_filter_columns` names **logical** columns, but a column-mapped table stores **physical**
names (`col-<uuid>`) in its parquet files, so the filter would never be built. Column mapping is on by
default, so the option is **refused** there rather than silently doing nothing — pair it with
`column_mapping='none'` in the same `WITH`. (On `native_write` the option is accepted and ignored regardless:
DuckDB picks the bloom columns itself, by cardinality, using `parquet_dictionary_size_limit` as the cutoff.)

⚠ Because `parquet_row_group_size_bytes` needs `preserve_insertion_order=false`, **persisting it means every
later native-engine write to that table needs that session flag too** — a plain `INSERT` naming no options
will otherwise fail with DuckDB's binder error. (The engineered-wood codec has no such requirement.) We do not
set the flag for you: doing so would silently break `SORTED BY` writes.

`parquet_row_groups_per_file` and `parquet_file_size_bytes` are **recognised but refused** as statement
options: DuckDB cannot rotate files together with `PARTITION_BY`, and without partitioning it writes a
directory where a Delta `add` action must name a single file. Use the row-group options instead.

**Requested vs declared — the one place an option is ignored rather than refused.** A `WITH`/`SET` option is
a request in *this* statement, so an engine that cannot honour it fails. A persisted property is a
declaration *about the table*, and the next writer may legitimately be a different engine — so it applies
what it can and ignores the rest, rather than making the table unwritable because someone once set a
native-only knob. Concretely: `WITH (parquet_dictionary_size_limit=…)` on a codec catalog is an error, while
a table *declaring* it is written happily by the codec engine (that one key ignored, the others honoured).
A property whose value is *malformed* is always an error — that is a mistake, not a capability gap.

The stable row-tracking columns (`__delta_row_id`, `__delta_row_commit_version`) are batched too, including
tables where some files store those values and others derive them.

Some shapes still read file by file and need no action from you: nested (`STRUCT`) columns when the row
identity is also wanted, and tables using `column_mapping 'id'` — the latter a DuckDB limitation rather than
ours.

## Diagnostic logging

Logging is **off by default** and configured by environment variables, not by `SET` — the bridge boots before
any session exists.

| Variable | Meaning |
|---|---|
| `FABRICATOR_LOG_LEVEL` | `Trace` \| `Debug` \| `Information` \| `Warning` \| `Error`. Unset ⇒ off |
| `FABRICATOR_LOG_FILE` | Write to this file as well as forwarding to DuckDB's `duckdb_logs` |

Categories, so you can grep for the layer you care about:

| Category | At level | What it shows |
|---|---|---|
| `Fabricator.Bridge` | `Debug` | Every **failed** managed↔native crossing, plus `open_catalog` / `get_metadata`. Connection strings are never logged |
| `Fabricator.Sql` | `Debug` | Every T-SQL statement: scans with the pushed projection/WHERE/TOP/ORDER BY, connection routing, DML, DDL, bulk |
| `Fabricator.Delta`, `Fabricator.Delta.Write`, `Fabricator.Delta.Fs` | `Information` | Scan mode, active/scanned/pruned file counts, the resolved snapshot version, which filesystem a table opened with, and per-commit lines during a transaction flush |
| `Fabricator.Memory` | `Debug` | Memory marks on the **row-scaling** paths — UPDATE, DELETE, bulk write, OPTIMIZE, transaction flush |

`Fabricator.Memory` is the one to reach for when a large statement uses more memory than you expect:

```bash
FABRICATOR_LOG_LEVEL=Debug FABRICATOR_LOG_FILE=/tmp/fab.log duckdb -unsigned < big_update.sql
grep -o 'mem delta update.*' /tmp/fab.log
# mem delta update: set values parsed (BOXED): ws=138MB heap=47MB alloc=58MB rows=400000
# mem delta update: arrow batch rebuilt:       ws=155MB heap=66MB alloc=78MB rows=400000
# mem delta update mor: rowid map built:       ws=168MB heap=79MB alloc=90MB rows=400000
# mem delta update mor: group flushed:         ws=241MB heap=101MB alloc=213MB rows=400000
# mem delta update mor: committed:             ws=242MB heap=94MB alloc=250MB rows=400000
```

Reading them: **`heap`** is the extension's own managed memory and drops as soon as it releases something;
**`ws`** is the whole process, so it includes DuckDB's side of the statement and it lags, because the OS does
not reclaim pages when the garbage collector frees objects. **`alloc`** is cumulative, so the *difference*
between two marks is how much a stage allocated even if it kept none of it — a stage with a small `heap` and a
large `alloc` step is copying data, not holding it.

Enabling `Debug` also logs every T-SQL statement, which is verbose on a busy session; prefer
`FABRICATOR_LOG_FILE` over the `duckdb_logs` table for anything long-running.

## Differences from the native `mssql` extension

- **Transport:** C# (`Microsoft.Data.SqlClient`) in-process via CoreCLR, **not** native TDS. So:
  connection strings are SqlClient strings; pooling, Windows/Kerberos and Azure Entra auth come from
  SqlClient; there is no `authenticator=krb5` / `krb5-*` connstr dialect or client-side conflict
  validator (use SqlClient keywords like `Trusted_Connection`, `Integrated Security`,
  `Authentication=Active Directory …`).
- **Bulk write:** INSERT/CTAS/COPY use `SqlBulkCopy` streamed over a bounded channel; INSERT enables
  `CheckConstraints` (CTAS/COPY do not). The native extension uses classic batched INSERT statements +
  TDS BCP.
- **Warehouse-aware:** detects a server profile and adapts (Fabric/Synapse connection mode, collation-driven
  types, `NONCLUSTERED NOT ENFORCED` keys, clustered columnstore) — see
  [Microsoft Fabric & Synapse](#microsoft-fabric--synapse-warehouse).
- **ORDER BY pushdown** is always attempted when safe (the native extension gates it behind
  `mssql_order_pushdown`, default off); string keys push only under a binary collation.
- **Callable functions / table-in-out / aggregates** (discovered UDFs, TVFs, procs + custom C#-authored
  scalar / table / table-in-out / **aggregate** functions, including `fn_each` per-row apply) are implemented
  here over Arrow — see [Callable Functions](#callable-functions). Load-time *global* (connection-free)
  functions are deferred.
- **Not implemented here:** connection-pool diagnostics (`mssql_pool_stats`, `mssql_open/close/ping`),
  COPY to temp tables, multi-statement batches.

## Delta Lake provider

`PROVIDER 'delta'` attaches a **Delta Lake** root as a read-write DuckDB catalog. Each subdirectory with a
`_delta_log/` is a table; data I/O goes through DuckDB's `FileSystem` (so `azure`/`httpfs` + DuckDB secrets
work), and the Delta log layer is the pure-C#
[engineered-wood](https://github.com/clast-project/engineered-wood) library (an in-tree submodule).

**The two spellings pick different defaults, and that is the only difference between them.**
`PROVIDER 'delta'` — the name to use — defaults `native_read`/`native_write` **on**: DuckDB reads and
writes the Parquet bytes, engineered-wood owns the log. `PROVIDER 'engineeredwooddelta'` defaults them
**off**, so engineered-wood's own codec reads and writes the data files too — a pure-C# path, kept for
the codec-specific surfaces (`bloom_filter_columns`) and for isolating a bug to one layer. Both
spellings reach the same catalog implementation and accept the same options, so either default can be
overridden explicitly (`PROVIDER 'delta', native_write false`).

**Storage targets** — table *discovery* is supported on **local** filesystems (incl. the Fabric-notebook
fuse mount), **S3** (via `httpfs`), **Fabric OneLake**, and **plain ADLS Gen2 storage accounts** — the last
two both over `abfss://`. How each one finds its tables differs:

| Root | Discovery |
|---|---|
| Local (incl. fuse mount) | `System.IO` directory enumeration |
| `s3://` | host-FS glob (`httpfs`) |
| **Fabric OneLake** | the Fabric [Unity Catalog REST API](https://learn.microsoft.com/fabric/onelake/onelake-unity-catalog) — resolves workspace/lakehouse name→GUID + the schema-enabled flag via the Fabric API, then lists tables from the UC endpoint |
| **Plain ADLS Gen2** | an Azure DataLake **directory walk** — a storage account has no Unity Catalog, so the table list comes from the filesystem |

Data reads/writes and `DROP`/`RENAME` go through the ADLS DFS endpoint for both abfss shapes.

```sql
-- Local / S3 folder catalog
ATTACH '/lake/root' AS lake (TYPE fabricator, PROVIDER 'delta');

-- Fabric OneLake (READ_ONLY false is REQUIRED — DuckDB forces remote ATTACH read-only otherwise;
-- one azure service-principal secret serves DuckDB IO + the Fabric Unity Catalog REST discovery)
ATTACH 'abfss://Workspace@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables'
  AS lake (TYPE fabricator, PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false);

-- Plain ADLS Gen2 storage account. Entra (a service principal, as above) works here too and is the
-- better practice; a shared key / storage connection string is also accepted — OneLake is the one that
-- requires Entra.
CREATE SECRET adls (TYPE azure, PROVIDER config,
                    CONNECTION_STRING 'DefaultEndpointsProtocol=https;AccountName=…;AccountKey=…');

ATTACH 'abfss://myfilesystem@myaccount.dfs.core.windows.net/lake'
  AS lake (TYPE fabricator, PROVIDER 'delta', SECRET adls, READ_ONLY false);

SELECT * FROM lake.main.t WHERE id > 10;          -- streaming scan + file/row-group filter pushdown
```

### Running inside a Fabric notebook

On Fabric compute the extension authenticates with the **ambient notebook token** — no secret, no service
principal, no connection string. The token comes from the notebook's own token service and **refreshes
itself**, which a pasted `ACCESS_TOKEN` does not.

```sql
-- Managed Tables area through the fuse mount: the simplest option, no credentials at all.
ATTACH '/lakehouse/default/Tables' AS lake (TYPE fabricator, PROVIDER 'delta', schemas true);

-- OneLake by URI, also with no secret. Reads route through the onelake:// filesystem (DuckDB's native
-- parquet reader, cached); writes commit through the Azure DataLake SDK.
ATTACH 'abfss://<workspaceId>@onelake.dfs.fabric.microsoft.com/<itemId>/Tables' AS lake
  (TYPE fabricator, PROVIDER 'delta', READ_ONLY false);
```

`READ_ONLY false` is required on the URI form — DuckDB forces a remote ATTACH read-only otherwise.

Both **Python** and **PySpark** notebooks are supported; the ambient token is the notebook's own identity
either way, so nothing about the ATTACH changes between them.

**A secret is optional, and only needed to use a DIFFERENT identity than the notebook's.** When you do want
one, these are the shapes that work — the fields are authoritative, so an explicitly configured service
principal wins even if `PROVIDER` says something else:

| secret | credential used |
|---|---|
| *(none, on Fabric compute)* | the ambient notebook token — **recommended** |
| `PROVIDER credential_chain` | the ambient chain (notebook token on Fabric, `DefaultAzureCredential` elsewhere) |
| `PROVIDER service_principal` + `TENANT_ID`/`CLIENT_ID`/`CLIENT_SECRET` | that service principal |
| `PROVIDER managed_identity` (+ `CLIENT_ID` for user-assigned) | that managed identity |
| `PROVIDER access_token` + `ACCESS_TOKEN` | the token as-is, expiry read from the JWT |

> **⚠ `ACCESS_TOKEN` does not refresh.** The token source lives outside the process, so a long-running
> notebook will outlive it — re-create the secret to refresh. Ambient is strictly better on Fabric for that
> reason. A single static storage token also cannot serve an `abfss://` ATTACH at all: that path needs both
> the Fabric and storage audiences, and one token carries one.

> **⚠ Off Fabric, the `abfss://` concurrency rule below still applies.** The ambient credential is adopted
> only where the notebook token service actually exists. On a developer machine or any non-Fabric host, an
> `abfss://` attach with no NAMED secret falls back to DuckDB's azure filesystem and loses the atomic commit
> — see the box immediately below.

> ### ⚠ Writing to `abfss://` concurrently: NAME the secret (same rule as `s3://`)
>
> Without a `SECRET` clause the catalog still authenticates, reads and writes — DuckDB's `azure`
> extension picks up any azure secret in scope for the data files — but the Delta **commit guard** is
> not in effect, because the credential reaches the catalog only via the secret the ATTACH *names*.
> DuckDB-azure's exclusive-create is a client-side existence check rather than a conditional PUT, so
> concurrent writers **lose commits silently**: a measured 6-writer × 8-commit run landed **41 of 48**,
> with six of the seven losses raising no error at all. Naming the secret landed **48/48**.
> `RENAME TABLE` and `DROP TABLE` also require the named secret (Azure's `MoveFile`/`RemoveDirectory` are
> unimplemented in DuckDB's azure filesystem). The extension emits a warning at ATTACH time for the
> unsafe shape. Single-writer, append-only use is unaffected either way.
>
> `COPY … TO 'abfss://…' (FORMAT delta)` has no `SECRET` clause, but it does **not** need one: it opens a
> Delta catalog of its own and picks up the `azure` secret whose *scope* covers the target path (azure
> secrets cover `abfss://` by default), so it gets the same guarded write path as a named-secret ATTACH.
> With no matching secret it falls back to the host filesystem, as before.

> ### ⚠ Writing to `s3://` concurrently: NAME the secret
>
> ```sql
> -- SAFE for concurrent writers: the secret is NAMED, so commits use a conditional PUT
> ATTACH 's3://bucket/lake' AS lake (TYPE fabricator, PROVIDER 'delta', SECRET my_s3, READ_ONLY false);
>
> -- UNSAFE: no SECRET clause. Reads and writes work; CONCURRENT writers lose commits SILENTLY.
> ATTACH 's3://bucket/lake' AS lake (TYPE fabricator, PROVIDER 'delta', READ_ONLY false);
> ```
>
> Delta's commit needs a put-if-absent, and **httpfs cannot send one on S3** — two writers at the same
> version both succeed and the later silently overwrites the earlier. Naming an `s3` secret in the ATTACH
> routes commits through a real conditional `PutObject` (`If-None-Match: "*"`) instead.
>
> **Having a secret in scope is not enough** — only the one the ATTACH *names* reaches the commit path,
> while DuckDB uses the ambient secret for data IO either way. So the unsafe form authenticates, reads,
> writes and passes every single-writer test. Measured on MinIO: 6 writers × 8 commits landed **8 of 48**
> with no error; the same run with the secret named landed 48/48.
>
> The attach warns when you get this wrong (`SELECT message FROM duckdb_logs` after
> `CALL enable_logging(level := 'debug', storage := 'memory')`). Single-writer use is unaffected, and a
> read-only attach needs nothing — this is only about concurrent writers.

> ### ⚠ A LOCAL path on **Windows** is single-writer only
>
> Delta's commit needs a put-if-absent, and on Windows the local filesystem does not provide one through
> DuckDB — exclusive-create **succeeds on a file that already exists**, and a rename **overwrites** its
> target. Two processes committing the same version therefore both "succeed" and the later one wins:
>
> ```sql
> -- Check any root before writing to it from more than one process. Use a directory that ALREADY EXISTS
> -- and read the whole table, top to bottom -- see the caveat below.
> SELECT * FROM fabricator_fs_write_probe('D:/existing/dir');
> --  create_directory                | true  | directory exists/created
> --  write_create                    | true  | wrote 16 bytes (WRITE|FILE_CREATE)
> --  exclusive_create_existing_fails | false | ... NO put-if-absent guard (unsafe for commits)
> ```
>
> Measured: 6 concurrent processes × 3 `INSERT`s × 50 rows against one local table landed **400 of 900
> rows** — 500 lost, one process's rows missing entirely, **and every process exited successfully**. A
> concurrent reader can also observe a *half-written* commit file, which surfaces as a JSON parse error
> (`'w' is an invalid start of a value`) rather than as a conflict.
>
> **⚠ Read the probe's earlier rows before trusting its verdict.** Point it at a path whose parent does
> not exist and `exclusive_create_existing_fails` reports **`true` — "put-if-absent works"** — because the
> exclusive open threw for a *missing directory* rather than for an existing file. The rows above it give it
> away (`write_create` = `false`, "The system cannot find the path specified"), but the verdict cell on its
> own reads as SAFE on a run where nothing was tested. Confirm `create_directory` and `write_create` are
> both `true` first.
>
> This is a property of the storage, not of your SQL: **single-writer use is completely unaffected**, and
> everything the test suite covers runs this way. For concurrent writers use OneLake/`abfss://` or
> `s3://` with a named secret (above), or a POSIX filesystem — Linux/macOS local paths get a real `O_EXCL`
> and are multi-process safe.

> ### ⚠ A table with CHECK constraints is INSERT-only here
>
> Delta stores a CHECK constraint as a `delta.constraints.<name>` table property, and Spark writes them with
> `ALTER TABLE … ADD CONSTRAINT`. fabricator can **append** to such a table, and the constraint is genuinely
> enforced — a violating row is rejected and nothing is committed:
>
> ```
> INSERT INTO lake.main.t VALUES (-5);
> -- IO Error: CHECK constraint 'delta.constraints.pos' (id > 0) is violated by a row being written.
> --   No data was committed.
> ```
>
> But `UPDATE` and `DELETE` on such a table are **refused** ("this write path cannot evaluate it against the
> rows"), including an `UPDATE` whose result would satisfy the constraint and a `DELETE`, which cannot
> violate one at all. The reason is that those two are built from a lower-level Delta API that is handed
> finished files rather than rows, so the engine has nothing to check and declines rather than commit
> unchecked data. Do those statements from Spark, or drop the constraint first:
>
> ```sql
> SELECT * FROM lake.delta.set_tblproperties('main.t', '{"delta.constraints.pos":null}');
> ```
>
> The same applies to a column invariant (`delta.invariants`) and to generated columns. Note
> `CREATE OR REPLACE TABLE` is **not** an escape — it replaces the data and carries the declaration forward.
> fabricator does not create constrained tables itself (`CREATE TABLE … CHECK (…)` is unsupported), so this
> is about tables another engine wrote.

> ### ⚠ Creating a table AND filling it is two commits, not one
>
> `CREATE TABLE t AS SELECT …` writes **two** Delta versions — v0 the schema (an *empty* table), v1 the
> rows — and so does `BEGIN; CREATE TABLE t (…); INSERT …; COMMIT;`. There is no way to ask for one.
>
> ```
> t/_delta_log/00000000000000000000.json   commitInfo operation=CREATE TABLE, protocol, metaData
> t/_delta_log/00000000000000000001.json   commitInfo operation=WRITE, add
> ```
>
> One thing follows for readers: a **concurrent reader** (Spark, delta-rs) can open the table between the
> two commits and see it *empty*.
>
> **A failed CTAS no longer leaves that empty table behind (since 2026-08-17).** The data files are now
> written *before* anything touches the log, so a failure during the write leaves a folder with no
> `_delta_log` — not a table to any reader — and simply re-running the statement works. Schema failures
> were already refused before the create (a `TIMESTAMP_NS` or `INTERVAL` column errors and leaves nothing),
> so what this covers is the *storage* half: permissions, disk full, network. The window that remains is
> between the two adjacent log writes, with no data movement in between.
>
> ⚠ Two shapes keep the old ordering and can still leave an empty table if their write fails:
> `BEGIN; CREATE TABLE t (…); …` (a `CREATE` inside a transaction is immediate by design — see below) and
> `PROVIDER 'engineeredwooddelta'`. The codec engine is nonetheless safe against a failing *source* query,
> because it buffers the whole result before creating anything.
>
> **⚠ If you do end up with an empty table, recover with `CREATE OR REPLACE TABLE … AS SELECT` (or
> `DROP TABLE` then create) — a plain re-run does not.** Once the table exists, a plain
> `CREATE TABLE t AS SELECT …` is refused with *"Table with name t already exists!"*, and
> `CREATE TABLE IF NOT EXISTS` leaves the empty table by definition. Only `CREATE OR REPLACE` overwrites it.
>
> This is a Delta-writer API limitation, not a Delta format one — the format allows a single commit that
> both creates the table and adds data.

| Feature | Status |
|---------|--------|
| Discover tables — local (`System.IO`), S3 (host-FS glob), OneLake (Fabric Unity Catalog REST API) | ✅ (generic non-OneLake ADLS not supported — duckdb-azure glob #174) |
| Streaming scan + filter pushdown (Delta file pruning + Parquet row-group skipping) | ✅ |
| `LIMIT` and `ORDER BY … LIMIT` (TopN) pushed into the scan — any key type, any NULL placement | ✅ |
| Statistics → optimizer: **row count summed from the log** (`numRecords` per file minus each deletion vector's cardinality — no data file or DV file is read, and it is exact) | ✅ (no per-column NDV — a Delta `add` records min/max/nullCount but no distinct count) |
| `CREATE TABLE` / `INSERT` / CTAS / COPY (streaming bulk via the standard write path) | ✅ |
| `DELETE` / `UPDATE` — rowid deletion-vectors / merge-on-read (default) or copy-on-write (`deletion_vectors false`) | ✅ |
| `DROP TABLE`, `ALTER TABLE … ADD COLUMN`, `RENAME TABLE` (local + OneLake) | ✅ |
| Multi-schema: `schemas true` (local/S3 `<root>/<schema>/<table>`); schema-enabled OneLake lakehouses | ✅ |
| Time travel: `FROM t AT (VERSION => n)` and `AT (TIMESTAMP => ts)` | ✅ |
| Snapshots/history: `<catalog>.delta.snapshots('<schema.>table')` | ✅ |
| **Exactly-once appends** (Delta application transactions / Spark `txnAppId`): `<catalog>.delta.set_transaction_version` + `.get_transaction_version` | ✅ |
| **Change Data Feed**: `change_data_feed true` + `<catalog>.delta.changes('<schema.>table', starting_version := n, ...)` — version OR timestamp bounds | ✅ |
| **Partitioning**: native `CREATE TABLE … PARTITIONED BY (cols)` (or the `delta_write_options` setting) → Hive `col=value/` layout | ✅ |

| **Write tuning**: compression / row-group size / bloom filters via ATTACH options, the `delta_write_options` setting, or per-table `WITH (…)` | ✅ |
| **Liquid clustering**: `SORTED BY (cols)`, `bucket()` / `hilbert_index()`, clustered `OPTIMIZE` (incremental ZCube), `ALTER … SET SORTED BY` | ✅ |
| Per-table `WITH (…)` — Delta properties / feature-flag overrides / write tuning stamped in the CREATE commit | ✅ |
| `native_write` / `native_read` — DuckDB's own Parquet reader/writer for data files (EW keeps the `_delta_log`) | ✅ |
| VARIANT columns; `set_tblproperties` / `tblproperties`; `OPTIMIZE` / `VACUUM` maintenance | ✅ |
| Concurrent writers: OCC retry (a blind append / CTAS — an append that READ the table surfaces the conflict instead, see below) + **row-level concurrency** (disjoint-row DML on DV tables); S3 multi-writer via a secret | ✅ |
| Concurrent writers on **OneLake**, many processes appending to ONE table — no lost writes (measured: 96 concurrent commits, all landed), but a losing writer can occasionally surface an error rather than retrying transparently, so retry the statement | ⚠ |

> **Partition values are escaped the way Spark escapes them**, so a value containing `/ : = % ? * " ' #` or a
> backslash or a control character becomes `%XX` in the directory name (`region=a/b` → `region=a%2Fb`). The
> value you read back is always the original — Delta records it in the log, not in the path.
>
> ⚠ **On a LOCAL path on Windows, `<`, `>`, `|` and a trailing space are escaped too**, because Windows
> cannot hold them in a directory name (it silently *strips* a trailing space, which would leave the data
> file unreachable). The same table written to `s3://` or `abfss://` keeps them literal, matching Spark — so
> one logical partition value can have two directory spellings across storage backends; both are correct and
> both read back identically. Since 2026-08-11; before that such a value was written unescaped on Windows and
> the file under it could not be opened.

```sql
-- Time travel + history
SELECT * FROM lake.main.t AT (VERSION => 3);
SELECT version, operation, timestamp FROM lake.delta.snapshots('main.t') ORDER BY version;

-- Change Data Feed (enable per catalog at ATTACH, then read the row-level feed)
ATTACH '/lake/root' AS cdf (TYPE fabricator, PROVIDER 'delta', change_data_feed true);
CREATE TABLE cdf.main.t AS SELECT * FROM (VALUES (1,'a'),(2,'b')) v(id,val);
DELETE FROM cdf.main.t WHERE id = 2;
SELECT _change_type, id, val, _commit_version, _commit_timestamp
  FROM cdf.delta.changes('main.t', starting_version := 0);   -- ending_version omitted => latest
-- insert/insert (v1), delete (v2): each row tagged with its commit version + timestamp (epoch ms)

-- Or bound the feed by TIME instead of version (UTC): starting_timestamp = the first version committed
-- AT OR AFTER that instant, ending_timestamp = the last AT OR BEFORE it (Spark's starting/endingTimestamp
-- semantics). A bound past either end of the history yields an EMPTY feed, never an error.
SELECT _change_type, id, val
  FROM cdf.delta.changes('main.t', starting_timestamp := TIMESTAMP '2026-08-14 00:00:00');
```

> The `delta.*` functions are catalog-bound: they live in a synthetic `delta` schema every Delta attach
> advertises (`snapshots`, `changes`, `tblproperties`, `set_tblproperties`, `checkpoint`,
> `get_transaction_version`, `set_transaction_version`), always addressed through the catalog —
> `lake.delta.snapshots('main.t')`. Before 2026-08-14 they were global functions taking the catalog name as
> their first argument (`fabricator_delta_snapshots('lake', 'main.t')`); those spellings are **gone**, no
> aliases.

**Manual checkpoint.** `SELECT * FROM lake.delta.checkpoint('main.t')` writes a checkpoint for the table's
current version *now* — instead of waiting for a commit to land on a `delta.checkpointInterval` multiple —
and returns the version checkpointed. A checkpoint is what bounds the next reader's log replay, so the
natural moments are after a bulk load or an `OPTIMIZE`, or before handing the table to another engine. Notes:
it also runs log cleanup (commits the checkpoint covers that are older than `delta.logRetentionDuration` are
deleted, exactly as an automatic checkpoint would; set `delta.enableExpiredLogCleanup` to `'false'` on the
table to keep them), it is not free (it materialises the whole active-file set), and running it per commit
just pays for checkpointing twice.

> **Caveat if you also use `row_tracking true` and read the feed from another engine.** An `INSERT` run
> inside an explicit `BEGIN … COMMIT` writes a `_change_data` file whose row-id columns are **NULL**, where
> the same `INSERT` in autocommit records no change file at all and a reader derives real row ids from the
> data file. The rows, change types, commit versions and timestamps are identical either way — and
> `delta.changes` never projects row identity — so this is invisible through DuckDB and matters
> only to a reader that consumes the row-identity columns a change file carries. Measured, being fixed;
> details in [docs/delta-transaction-hoist.md](docs/delta-transaction-hoist.md) §6.

#### `CREATE TABLE` inside a transaction — what is and is not atomic

A `CREATE TABLE` (or CTAS) inside `BEGIN … COMMIT` **creates the table immediately**; only its DATA waits
for `COMMIT`. Two consequences worth knowing before you rely on either:

```sql
BEGIN;
CREATE TABLE lake.main.t AS SELECT * FROM lake.main.src;  -- table exists NOW, empty; rows are buffered
DELETE FROM lake.main.t WHERE id < 0;                     -- works (used to be refused)
COMMIT;                                                   -- the rows land as ONE commit

-- ALTER works too, but schema changes must come BEFORE the transaction's data statements:
BEGIN;
CREATE TABLE lake.main.u (id INTEGER);
ALTER TABLE lake.main.u ADD COLUMN note VARCHAR;          -- works (used to be refused)
INSERT INTO lake.main.u VALUES (1, 'hi');
COMMIT;
```

- ✅ **You can now ALTER and DELETE a table your own transaction created.** Both previously failed with
  *"not supported yet — COMMIT the CREATE first"*.
- ⚠ Two independent rules still apply, and their messages name them: an `ALTER` must precede the
  transaction's data statements (*"ALTER TABLE after buffered data changes"* — so it cannot follow a CTAS,
  which is itself a data statement), and **`UPDATE` on such a table still fails**, because every one of its
  rows was inserted in the same transaction and updating not-yet-committed rows is a separate limitation.
- ⚠ **Another session can see the table, empty, until you commit** — and a `ROLLBACK` drops it on a
  best-effort basis. If that drop fails (a permission or network error) an **empty table is left behind**;
  the reason is logged with the path. Set `FABRICATOR_LOG_LEVEL=Information` to see it.

Rationale and the full trade-off: [docs/delta-transaction-hoist.md](docs/delta-transaction-hoist.md) §3.

#### `MERGE INTO` on Delta — one commit per transaction, not per statement

A `MERGE` is several DML operators, so the transaction — not the statement — decides how many Delta
versions it produces:

```sql
BEGIN;
MERGE INTO lake.main.t USING src ON t.id = src.id
  WHEN MATCHED     THEN UPDATE SET v = src.v
  WHEN NOT MATCHED THEN INSERT (id, v) VALUES (src.id, src.v);
COMMIT;                                  -- ONE commit; the change feed reports it at ONE version
```

- ⚠ **In autocommit the same `MERGE` produces one commit per action** (measured: two, or three with a
  delete), so a concurrent reader can observe the update without the insert. The final data is correct
  either way — only atomicity differs. This is the same per-statement-commit divergence the rest of the
  Delta provider has.
- The change feed of a fused merge is exact: matched rows appear as an
  `update_preimage`/`update_postimage` pair and inserted rows as `insert`, all at the single version. In
  autocommit the same rows are **split across versions** (the update pair at one, the insert at the next),
  so a consumer processing version-by-version cannot see the merge as one change set.
- ⚠ In autocommit the **INSERT action commits LAST**, after the delete and update — it is the only action
  that routes through the transaction buffer, so it is flushed at statement end. The visible intermediate
  states are therefore "delete applied", then "delete + update applied", then all three.
- `WHEN MATCHED THEN UPDATE` on rows **this same transaction inserted** is refused, as it is for a plain
  `UPDATE` — but a merge does not hit that: matched and not-matched rows are disjoint by construction, and
  the guard keys on the rowid's file ordinal, so matched rows carry committed ordinals.

#### Exactly-once appends — Delta application transactions

A producer that may be replayed (a retried job, a restarted stream) records how far it has got, and the
record commits **atomically with the data**, so there is no window in which one exists without the other.
This is Delta's `txn` action — the same mechanism as Spark's `txnAppId`/`txnVersion` and duckdb-delta's.

`<catalog>.delta.set_transaction_version('<schema.>table', app_id, version [, expected := previous])`
**parks** the version on the current transaction; at `COMMIT` it is compared-and-swapped against the
latest snapshot. It therefore **requires an explicit `BEGIN … COMMIT`** — that is what makes the swap
atomic with the write. Omit `expected` to mean *"the table must record nothing for this
producer yet"* (a first batch); pass it to chain batch to batch.

```sql
ATTACH '/lake/root' AS lake (TYPE fabricator, PROVIDER 'delta');
CREATE TABLE lake.main.t (id INTEGER);

BEGIN;
SELECT * FROM lake.delta.set_transaction_version('t', 'loader', 1);
INSERT INTO lake.main.t VALUES (1), (2);
COMMIT;                                   -- data + the txn action, one version

SELECT * FROM lake.delta.get_transaction_version('t', 'loader');
-- loader  1

-- A REPLAY of that same batch fails the compare-and-set instead of duplicating the rows:
BEGIN;
SELECT * FROM lake.delta.set_transaction_version('t', 'loader', 1);
INSERT INTO lake.main.t VALUES (1), (2);
COMMIT;                                   -- error: transaction version conflict … for 'loader'
                                          -- the table still holds 2 rows

-- The next batch states the version it expects to be at:
BEGIN;
SELECT * FROM lake.delta.set_transaction_version('t', 'loader', 2, expected := 1);
INSERT INTO lake.main.t VALUES (3);
COMMIT;
```

A failed swap is **not a conflict to retry** — retrying cannot make an already-committed batch
un-commit. Read the recorded version back and decide whether the batch still needs writing.

> ### ⚠ Editing table properties while a write transaction is open can abort it
>
> Delta treats a concurrent metadata change as a conflict **unconditionally** — it does not matter what your
> transaction read. So a property or schema edit landing while a transaction is open can abort it:
>
> ```sql
> -- connection 1
> BEGIN;
> DELETE FROM lake.main.t WHERE id = 2;
>
> -- connection 2, before connection 1 commits
> SELECT * FROM lake.delta.set_tblproperties('main.t', '{"custom.k":"v"}');
>
> -- connection 1
> COMMIT;
> -- TransactionContext Error: Failed to commit: Fabricator: commit_transaction failed: delta transaction
> --   conflict on '<path>': the table moved from version 1 while the transaction was open and the
> --   concurrent changes do not commute (Concurrent commit 2 changed the table metadata.)
> --   -- the transaction is rolled back; retry it.
> ```
>
> The transaction is rolled back whole, so retrying it is safe and is the fix. Better still, do property
> and schema edits outside your write windows.
>
> **A plain `INSERT` is not affected** — an append's commit is planned against the table as it stands at
> `COMMIT`, so the property edit is simply an earlier version and the append lands on top of it. The same
> holds for `ALTER TABLE … ADD COLUMN`: rows written before the column exists read it as `NULL`, which is
> what Delta schema evolution means. It is `UPDATE` / `DELETE` inside `BEGIN … COMMIT` that hold a pinned
> snapshot and can therefore be invalidated.
>
> **⚠ An `INSERT` that READ the table is the exception, and it changed on 2026-08-12.** Inside
> `BEGIN … COMMIT`, a statement like `INSERT INTO t SELECT max(id) + 1 FROM t` computes its rows *from* the
> table, so if a concurrent writer commits first the statement now **fails and must be re-run** rather than
> quietly landing. It used to retry — and the retry re-committed the rows already computed, i.e. a value
> derived from the old `max`, with no error. Re-running is the only thing that can recompute them.
> A transaction that merely `SELECT`ed the table before appending is treated the same way, because Delta
> defines "blind append" over the whole transaction rather than over one statement. Autocommit `INSERT`s
> are unaffected and still retry.
>
> **⚠ And a transaction that CHANGES the schema or properties gives up its own exemption (2026-08-13).**
> A plain append normally commutes with whatever else lands in the window. But if *your* transaction also
> carries an `ALTER TABLE`, a `delta.set_tblproperties`, or an insert into an `IDENTITY` table
> (which records a high-water mark), it can now conflict with someone else's concurrent append. The reason
> is that an append written against the old schema need not still be valid under your new one, so the
> exemption is withdrawn — this is what Delta itself does at `write_serializable`, the default here. Retry
> the transaction, or do the schema edit on its own.

> ### What `VACUUM` does and does not touch
>
> `SELECT fabricator_exec('lake', 'VACUUM main.t RETAIN 168 HOURS');` deletes files under the table that no
> current version references and that are older than the retention period.
>
> It **does** descend into partition directories. (Before 2026-08-08 it did not — it swept only the table
> root, so a partitioned table's superseded files were never reclaimed. If you have long-lived partitioned
> tables, the first `VACUUM` after upgrading may free a lot.)
>
> It **never** touches a directory or file whose name begins with `_` or `.` — `_delta_log/`,
> `_change_data/`, and anything another engine keeps beside your data such as an index sidecar. Two
> consequences worth knowing:
>
> - **Change-data-feed files are never collected.** They are referenced by `cdc` actions rather than by
>   `add` actions, so nothing here can tell a live one from an expired one. CDF history accumulates; delete
>   it deliberately if you need the space.
> - **This is deliberately more conservative than Delta Spark**, which does collect `_delta_index` and
>   `_change_data`. Leaving them costs storage; collecting them could destroy another engine's data.
>
> A partition directory is still swept even if the column name starts with an underscore (`_region=eu/`).

### `COPY … TO` a Delta path (no ATTACH)

`COPY … TO '<path>' (FORMAT delta, …)` writes a Delta table to **any path** — local, `s3://`, `onelake://`,
`abfss://` — with **no ATTACH** (a transient per-execution catalog does the write). The disposition is the
Spark / delta-rs **save-mode vocabulary** via the `MODE` option, and the write is its **own atomic Delta
commit** (it deliberately does *not* roll back with a surrounding DuckDB `BEGIN` — file-COPY semantics).
The official duckdb-delta extension has no COPY writer, so this is unique to fabricator.

```sql
COPY (SELECT * FROM src) TO 's3://lake/sales' (FORMAT delta);                    -- MODE 'overwrite' (default)
COPY new_rows          TO 's3://lake/sales' (FORMAT delta, MODE 'append');       -- create-if-missing + append
COPY src               TO 's3://lake/sales' (FORMAT delta, MODE 'error');        -- fail if it exists (Spark default)
COPY src               TO 's3://lake/sales' (FORMAT delta, MODE 'ignore');       -- silent no-op if it exists
COPY eu                TO 's3://lake/sales' (FORMAT delta, MODE 'overwrite_partitions',
                                             PARTITION_COLUMNS 'region');        -- dynamic partition overwrite
```

`MODE` values: `overwrite` (default) · `append` · `error`/`errorifexists` · `ignore` · `error_if_not_exists`
(strict append — fail if *missing*) · `overwrite_partitions` (replace only the partitions present in the
input, one commit). Also accepts `PARTITION_COLUMNS 'a,b'`, `SORTED_COLUMNS 'a,b'` (declarative
clustering — converges on re-runs), `SCHEMA_MODE 'merge'|'overwrite'`, and the same provider knobs as ATTACH
(`NATIVE_WRITE` — **defaults true** here for bounded-memory streaming; `DELETION_VECTORS` / `COLUMN_MAPPING`
/ `ROW_TRACKING` / `CHANGE_DATA_FEED` / `IN_COMMIT_TIMESTAMPS` / `COMPRESSION` / `ROW_GROUP_SIZE` /
`BLOOM_FILTER_COLUMNS`). For a SQL-Server-readable table: `MODE 'overwrite', DELETION_VECTORS false,
COLUMN_MAPPING 'none'`. (The legacy `CREATE_TABLE` / `REPLACE` flags still work but cannot be combined with
`MODE`.)

### Partitioning & write tuning

Tables can be **partitioned** with the native DuckDB `PARTITIONED BY` clause (the column list comes before
`AS`), producing a Hive-style `<table>/<col>=<value>/*.parquet` layout that DuckDB reads transparently.
Partition columns are recorded in the table metadata, so a later `INSERT` preserves the layout.

```sql
CREATE TABLE lake.main.sales PARTITIONED BY (region) AS SELECT * FROM src;   -- CTAS
CREATE TABLE lake.main.evt (id INT, y INT, v VARCHAR) PARTITIONED BY (y);     -- empty + INSERT
```

Parquet write tuning is set either as **per-catalog ATTACH defaults** or a **session `delta_write_options`
JSON setting** (the setting overlays the ATTACH defaults per key; its `partition_by` is used when there's no
native clause). Applies to CREATE / INSERT / CTAS / COPY.

```sql
ATTACH '/lake' AS lake (TYPE fabricator, PROVIDER 'delta',
  compression 'zstd', row_group_size 1000000, bloom_filter_columns 'id,email');

SET delta_write_options = '{"compression":"zstd","row_group_size":1000000,
                            "bloom_filter_columns":["id"],"partition_by":["region","year"]}';
```

Compression: `snappy` (default) / `zstd` / `gzip` / `brotli` / `lz4` / `uncompressed` / …. Dictionary
encoding is auto-enabled per column and min/max statistics are always collected (driving file + row-group
pruning). **`bloom_filter_columns` applies only to the EW codec writer** (the default write path); under
`native_write` DuckDB's own Parquet writer produces the data files and blooms dictionary-encoded columns
**automatically**, so the explicit column list is not used there. `compression` and `row_group_size` apply
to both writers. (Partitioning is honored by the Delta provider; the SQL Server / DAX providers ignore
`PARTITIONED BY`.)

Two more write options (also via `delta_write_options`):

- **`replace_where`** — an atomic **partition overwrite**. On an `INSERT`, `{"replace_where":{"region":"EU"}}`
  removes exactly the matching partition's files and adds the new rows in **one Delta commit** (delta-rs's
  static partition overwrite). Keys must be partition columns; the inserted data must fall within them.
  ```sql
  SET delta_write_options='{"replace_where":{"region":"EU"}}';
  INSERT INTO lake.main.sales SELECT * FROM new_eu_rows;   -- EU replaced atomically, other partitions untouched
  SET delta_write_options='';
  ```
- **Schema evolution** — on **COPY** (COPY-TO isn't schema-checked, so wider/different source schemas reach the
  provider; a plain `INSERT` of wider data is rejected by DuckDB's binder first). A `SCHEMA_MODE` COPY option:
  `merge` = append + **union** the new source columns (old rows read NULL); `overwrite` = replace data + **adopt**
  the incoming source schema (drop/add columns). And **`CREATE OR REPLACE` is a true replace** — the table adopts
  exactly the new SELECT's schema (a dropped column is gone, a new one appears), like DuckDB's drop+create.
  ```sql
  COPY (SELECT id, val, new_col FROM …) TO 'lake.main.t'
    (FORMAT mssql, CREATE_TABLE false, SCHEMA_MODE 'merge');       -- append + add new_col (old rows NULL)
  COPY (SELECT … FROM …) TO 'lake.main.t' (FORMAT mssql, SCHEMA_MODE 'overwrite');  -- replace data + schema
  ```
  For append-time evolution via `INSERT`, use `ALTER TABLE ADD COLUMN` (supported) then `INSERT`.

Delta ATTACH options: `PROVIDER 'delta'`, `SECRET <azure_sp>` (OneLake auth), `READ_ONLY false`
(required for OneLake writes), `schemas true` (two-level layout on local/S3), `compression` / `row_group_size`
/ `bloom_filter_columns` (write-tuning defaults — `bloom_filter_columns` is EW-codec-only; `native_write`
blooms automatically, see [above](#partitioning--write-tuning)), `deletion_vectors true|false`,
`column_mapping 'name'|'id'|'none'`, `row_tracking true`, `change_data_feed true` (CDF capture),
`in_commit_timestamps true` (in-protocol monotonic timestamps for Spark/Fabric interop — `AT (TIMESTAMP)`
also resolves without it via the always-on commit timestamp), `native_read true` / `native_write true`
(DuckDB's own Parquet reader/writer for data files), `isolation_level 'write_serializable'|'serializable'`.
Any of these can also be set **per table** with `CREATE TABLE … WITH (…)` (above).

**Defaults** (when no options are given):

| Option | Default | Effect |
|--------|---------|--------|
| `deletion_vectors` | **`true`** | DV / merge-on-read DELETE+UPDATE (+ row tracking); bumps the table to reader v3 |
| `column_mapping` | **`'name'`** | writer v7; metadata-only RENAME / DROP COLUMN; Fabric T-SQL-endpoint compatible |
| `native_read`, `native_write` | **from the PROVIDER name** | `PROVIDER 'delta'` ⇒ both **on** (DuckDB's Parquet reader/writer — the production path); `PROVIDER 'engineeredwooddelta'` ⇒ both **off** (pure-EW codec). Either is still settable explicitly on either spelling |
| `change_data_feed`, `in_commit_timestamps`, `row_tracking`, `schemas` | `false` / off | opt-in |
| `isolation_level` | **`write_serializable`** | Concurrent **disjoint-row DML on one file composes** instead of conflicting — row-level concurrency is a `write_serializable`-only relaxation, and it is why this is the default. ⚠ **It does NOT match Fabric Spark**, which commits at `Serializable` and whose DDL will not even set `WriteSerializable`: on a table that declares no `delta.isolationLevel` we are the more permissive writer, so the effective guarantee depends on which engine wrote last. Attach `isolation_level 'serializable'` to align. (Was `serializable` between 2026-08-01 and 2026-08-11.) |
| `compression` | `snappy` | + auto dictionary encoding + always-on min/max stats |

**Concurrent writers from another engine (Spark, Databricks, delta-rs).** A writer records in its commit
whether its transaction read anything (`commitInfo.isBlindAppend`); under `write_serializable` a commit
that declares it read nothing is exempt from our conflict check, which is what lets a pure append commit
alongside your DELETE. We now **honour that declaration when it is present** and only guess from the
commit's shape when it is absent — so a foreign `INSERT … SELECT` from the same table (which adds files
but did read) is correctly treated as a conflict instead of being waved through. Two consequences worth
knowing:

- You may see a conflict where an older build silently allowed one. That is the check working; the
  earlier behaviour could let a concurrent read-then-append go unvalidated.
- **We now emit the flag ourselves** (since 2026-08-08), so another engine can exempt our appends the same
  way. It is declared only where it can be true, which is narrower than it sounds:

  | your statement | what we declare |
  |---|---|
  | `BEGIN; INSERT INTO t VALUES (…); COMMIT;` | `isBlindAppend: true` |
  | `BEGIN; INSERT INTO t SELECT … FROM t …; COMMIT;` | `false` — it read the target |
  | any **autocommit** `INSERT` | nothing |

  Autocommit says nothing because we only track what a statement read inside an explicit transaction, and
  an unrecorded append is indistinguishable from the read-then-append above. An absent flag is read as
  "not blind", so that costs a possible retry, never a skipped check — but it does mean **wrap an append in
  `BEGIN … COMMIT` if you want other engines to commute with it.**

  ⚠ This only matters on a `write_serializable` table. Under `serializable` (our default) Delta examines
  blind appends by design, so nothing changes — and Fabric Spark's DDL refuses to *set* `write_serializable`,
  so the tables it helps are ones you stamped via `WITH ("delta.isolationLevel"='WriteSerializable')`.

So a **default** table is read by Spark, delta-kernel, and Fabric Spark + OneLake conversion (all
validated live) — but **not** by SQL Server's DELTA reader (**protocol 1.0 only**) or the Fabric T-SQL
endpoint's id-mapping gate. For SQL-Server / PolyBase interop, create the table plain with
`deletion_vectors false, column_mapping 'none'` (per-table `WITH (…)`, or at ATTACH for the whole catalog) —
that yields a minReader-1 / minWriter-2 table every reader accepts. Full design:
[`docs/delta-catalog.md`](docs/delta-catalog.md).

Two details about that rule are easy to get wrong, and both are measured:

- The two flags fail **differently**. Column mapping is refused outright (`19725`). Deletion vectors are
  tolerated while merely *declared* — the table reads fine — and refused (`19726`) only once a `DELETE`
  actually materializes one. So an interop table can pass every test and then break at its first delete.
- **`CREATE OR REPLACE` does not repair it.** Once a deletion vector has existed, the replace is a new
  version in the same log and SQL Server still refuses. Recovery is a real `DROP TABLE` + create.

### SQL Server reading our Delta from Azure (ADLS Gen2)

SQL Server 2025 reads Delta on Azure storage the same way it reads it on S3, so a table this extension
writes to ADLS Gen2 can be queried straight from T-SQL:

```sql
-- LOCATION uses adls:// (DFS) or abs:// (blob). NOT abfss:// — SQL Server rejects that scheme (46548),
-- even though it is what this extension, Spark and Databricks all write.
CREATE DATABASE SCOPED CREDENTIAL adls_dc
  WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = 'sv=...&sr=c&sp=rl&sig=...';  -- no leading '?'

CREATE EXTERNAL DATA SOURCE adls_ds
  WITH (LOCATION = 'adls://myfilesystem@myaccount.dfs.core.windows.net', CREDENTIAL = adls_dc);

CREATE EXTERNAL FILE FORMAT DeltaFileFormat WITH (FORMAT_TYPE = DELTA);

SELECT * FROM OPENROWSET(BULK 'lake/trips', FORMAT = 'DELTA', DATA_SOURCE = 'adls_ds') AS r;
```

The credential is a **shared access signature**, not the account key; read+list at container scope is
enough. `BULK` is container-relative (the leading `/` is optional). A `CREATE EXTERNAL TABLE` over the same
location can then be read back through an ATTACHed `fabricator` catalog as an ordinary table — and dropping
that external table removes only the metadata, never the data.

**Writing through the external table.** SQL Server external tables are read-only to SQL Server itself, but
this extension serves `INSERT`/`UPDATE`/`DELETE` against one by writing directly to the storage it points
at, while SQL Server keeps serving the reads. `UPDATE`/`DELETE` need a row identity, and an external table
has no SQL-side key — so give the Delta table an **identity column** (`id BIGINT AS (0)`), which is a real,
readable data column and becomes the rowid:

```sql
CREATE TABLE lake.main.iddml (id BIGINT AS (0), name VARCHAR, val INTEGER);  -- on the Delta catalog
-- ...create the external table over it, then through the ATTACHed SQL Server catalog:
UPDATE sqldb.dbo.adls_iddml SET val = 99 WHERE name = 'b';
DELETE FROM sqldb.dbo.adls_iddml WHERE name = 'c';
INSERT INTO sqldb.dbo.adls_iddml (name, val) VALUES ('e', 5);   -- identity is engine-assigned
```

Works over `s3://` and `adls://` data sources. **`abs://` is readable but not writable** — it reaches the
same account through the blob endpoint, and deriving the DFS host from it would be a guess that is wrong
for sovereign clouds, private endpoints and custom DNS, so it reports "not routable" instead. Use an
`adls://` data source when you want to write. Supplying a value for the identity column on `INSERT` is
ignored; the engine assigns the next one.

### Liquid clustering (SORTED BY, bucket, hilbert_index)

Delta tables can be **ordered-on-write** and **clustered on OPTIMIZE** for tight per-file min/max (data
skipping). `CREATE TABLE … SORTED BY (cols)` persists the clustering spec and every write re-applies the
order; a later `OPTIMIZE` *reclusters* (incremental ZCube — cost tracks new data, and Databricks/Fabric
Spark recognize our cubes as their own). Two global scalars help build multi-key / bucketed layouts:

```sql
-- SORTED BY: lexicographic ordered writes + clustered OPTIMIZE
CREATE TABLE lake.main.events SORTED BY (grp, id) AS SELECT * FROM src;
CALL fabricator_exec('lake', 'OPTIMIZE main.events');            -- reclusters into contiguous ranges

-- hilbert_index(coords[], bits): multi-dimensional locality in ONE ORDER BY key (liquid-clustering style)
COPY (SELECT * FROM src ORDER BY hilbert_index([width_bucket(x,0,100,64), width_bucket(y,0,100,64)], 15))
  TO 's3://lake/geo' (FORMAT delta);      -- path-targeted Delta write (see 'COPY … TO a Delta path' above)

-- bucket(n, value): the Iceberg / DuckLake bucket transform (Murmur3, cross-engine-identical). Delta has no
-- transform partitioning, so materialize the bucket column and PARTITION BY it; queries prune by folding
-- bucket(n, <literal>) at plan time (the scalars are CONSISTENT, not volatile).
CREATE TABLE lake.main.users PARTITIONED BY (ubucket) AS
  SELECT *, bucket(8, user_name) AS ubucket FROM src;
SELECT * FROM lake.main.users WHERE user_name = 'alice' AND ubucket = bucket(8, 'alice');  -- prunes to 1 file
```

`ALTER TABLE t SET SORTED BY (a, b)` / `RESET SORTED BY` re-keys an existing table (a metadata commit; the
next `OPTIMIZE` reclusters by the new key). Scalar functions also declare **volatility** now
(`IScalarFunction.IsVolatile`) — a pure function folds constant args at plan time, which is what makes
`WHERE bucket_col = bucket(8, 'alice')` reach the scan as a partition filter.

## CREATE TABLE … WITH (…) options

`CREATE TABLE [AS] … WITH (key='value', …)` (parsed natively by DuckDB v1.5.4) passes per-table options to
the provider. On the **Delta** provider three kinds of key are consumed, and unknown keys are **rejected**
(never silently ignored):

```sql
-- write tuning (CTAS): DuckLake-parity names, winning over delta_write_options and the ATTACH defaults
CREATE TABLE lake.main.t WITH (parquet_compression='zstd', parquet_row_group_size=1000000) AS SELECT …;

-- per-table feature-flag overrides: the protocol-1.0 recipe for one table, no dedicated ATTACH
CREATE TABLE lake.main.plain WITH (deletion_vectors=false, column_mapping='none') AS SELECT …;

-- delta.* / fabricator.* table properties stamped in the CREATE commit (one commit, no follow-up set)
CREATE TABLE lake.main.t WITH ("delta.isolationLevel"='Serializable', "fabricator.myTag"='hello') AS SELECT …;
```

> **⚠ A quoted `delta.*` property is a DECLARATION stored in the table, and most of them are for OTHER
> engines to read — we do not implement every one.** Three we do act on:
>
> - `delta.isolationLevel` — a table's own level outranks the catalog's `isolation_level`. ⚠ **The catalog's
>   level is never written into a table** — not the default, and not an explicit `isolation_level` on the
>   ATTACH. It governs how *this* attach's transactions behave; only `WITH (…)` or
>   `delta.set_tblproperties` makes a durable declaration other engines can read. That matters
>   because silence does **not** mean agreement: a table declaring nothing is read as `Serializable` by
>   Spark and as `WriteSerializable` by us, so if you rely on row-level concurrency across engines, declare
>   it on the table.
> - `delta.checkpointInterval` — how often the log is checkpointed. Honoured since 2026-08-08; before that
>   it was stored and ignored, so a table declaring `100` was still checkpointed every 10.
> - `delta.logRetentionDuration` — how long a superseded commit file is kept (default 30 days). Also
>   honoured since 2026-08-08; before that `_delta_log` grew for the life of the table. Cleanup runs after a
>   checkpoint, deletes only commits that checkpoint covers, and never touches a file inside the window. Set
>   `delta.enableExpiredLogCleanup = false` to switch it off.
>
> **Version checksums (since 2026-08-25).** Every commit is now followed by a `_delta_log/<version>.crc`
> summarising the table at that version — file count, byte size, metadata, protocol and the live
> transaction / domain sets — so a reader can learn a version's size without replaying the log, and an
> engine maintaining table state incrementally can notice its view has drifted from the log's. This is what
> the Delta protocol says a writer SHOULD do and what delta-spark already did, so on a table you write from
> both engines the set is now complete instead of covering only Spark's versions. They cost one extra small
> write per commit and are reclaimed by the same log cleanup as the commits they describe. There is no
> switch to turn them off — a missing checksum is always safe, so one is written only when the state being
> named is exactly the state at that version, and failing to write one never fails the commit.

Keys: `parquet_compression` / `parquet_row_group_size` / `parquet_bloom_filter_columns`; `deletion_vectors`
/ `column_mapping` / `row_tracking` / `change_data_feed` / `in_commit_timestamps`; any quoted `delta.*` /
`fabricator.*` property; and `table_type='DELTA'` / `format='parquet'` (validated no-ops, for the
Iceberg-style DDL shape). The SQL Server provider's `WITH` keys drive external tables (next section); DAX
and deltars reject options.

## SQL Server external tables on S3

SQL Server can read CSV / Parquet / **Delta** on S3 natively, but it can **never write** an S3 external
table (and cannot write Delta at all). This extension makes those tables **writable** — the write is
performed client-side and SQL Server keeps serving the reads:

```sql
ATTACH 'Server=…;Database=db' AS sqldb (TYPE fabricator);   -- SQL Server 2025 / Azure SQL / Fabric

-- (A) INSERT into a DETECTED external table → routed to storage (Delta append / new Parquet file).
--     The extension resolves the matching DuckDB s3 secret by bucket scope; SQL Server serves the read.
INSERT INTO sqldb.dbo.trips_ext SELECT * FROM staging;
SELECT count(*) FROM sqldb.dbo.trips_ext;                   -- SQL Server reads it back

-- (B) One-statement CETAS-analog: write the Delta table client-side (protocol-1.0 plain, so SQL Server
--     reads it) + auto-provision the EXTERNAL FILE FORMAT + EXTERNAL TABLE. data_source names a
--     pre-provisioned EXTERNAL DATA SOURCE (no credentials cross this path).
CREATE TABLE sqldb.dbo.sales
  WITH (location='s3://minio:9000/lake/sales', table_type='DELTA', data_source='s3_ds')
  AS SELECT * FROM src;

-- (D) An IDENTITY column bridges the rowid domains → identity-keyed UPDATE / DELETE on the S3 Delta table
--     (copy-on-write keeps it protocol-1.0 SQL-readable). Declare it with the empty-CREATE form:
CREATE TABLE sqldb.dbo.dim (id BIGINT AS (0), name VARCHAR)
  WITH (location='s3://minio:9000/lake/dim', table_type='DELTA', data_source='s3_ds');
INSERT INTO sqldb.dbo.dim (name) VALUES ('a'), ('b');
UPDATE sqldb.dbo.dim SET name = 'bee' WHERE id = 2;         -- resolved to a Delta rowid, applied on S3
DELETE FROM sqldb.dbo.dim WHERE id = 1;
```

The identity column is the only sound bridge between SQL Server's scan (which produces identity values) and
the Delta writer (which resolves them back to physical rowids via a pruned scan) — it is snapshot-independent
and PolyBase-visible, unlike Delta's off-schema `_metadata.row_id`. `DROP TABLE` on a detected external table
emits `DROP EXTERNAL TABLE` (metadata-only; the storage data stays). Storage writes are their own commit, so
they are autocommit-only (rejected inside an explicit transaction). Full design:
[`docs/create-table-with-options.md`](docs/create-table-with-options.md).

## Power BI / DAX provider

`PROVIDER 'dax'` (aliases `adomd` / `powerbi` / `ssas` / `fabric`) attaches a **Power BI / Analysis
Services semantic model** over ADOMD as a **read-only** DuckDB catalog. Tables come from the model
(`$SYSTEM.TMSCHEMA_*`), scans run as `EVALUATE SELECTCOLUMNS(…)` with best-effort filter pushdown (`=` /
`IN` / `ISBLANK` for any type; ordering comparisons for non-string), and a `system` schema exposes a
curated set of VertiPaq / `$SYSTEM` DMVs.

```sql
-- Local Power BI Desktop (Windows — auto-detects the running msmdsrv port)
ATTACH 'pbidesktop://' AS pbi (TYPE fabricator, PROVIDER 'dax');

-- Fabric / AAS / SSAS XMLA endpoint (Entra via a foreign `azure` service-principal secret — the same
-- kind used for OneLake, e.g. `fabric_sp` above; a remote endpoint without a secret uses DefaultAzureCredential)
ATTACH 'Data Source=powerbi://api.powerbi.com/v1.0/myorg/WS;Initial Catalog=Model'
  AS m (TYPE fabricator, PROVIDER 'dax', SECRET fabric_sp);

SELECT * FROM pbi."Model"."Sales" WHERE Region = 'US';          -- scan + filter pushdown
SELECT * FROM m.system."TMSCHEMA_TABLES";                        -- $SYSTEM DMV
```

Three DAX functions are exposed under the model schema (`expression` is a named param, so the optional
ones coexist):

```sql
-- Evaluate arbitrary DAX; params bind as ADOMD @name (a DuckDB STRUCT or a JSON string)
SELECT * FROM m."Model".daxeval(expression := 'EVALUATE TOPN(10, ''Sales'')');
SELECT * FROM m."Model".daxeval(expression := 'EVALUATE FILTER(''Sales'', [Amt] > @min)',
                                params := {'min': 1000});

-- Inject a DuckDB input table as a DAX DATATABLE and evaluate once (collector — no row cap)
SELECT * FROM m."Model".daxevaltable((SELECT * FROM ids), expression := 'EVALUATE …');
-- …or once per input row (@column params)
SELECT * FROM m."Model".daxeach((SELECT region FROM regions), expression := 'EVALUATE …');
```

**Refreshing the model (XMLA/TMSL) — including a single partition**, which the Power BI REST API cannot
express at all. `refresh_semantic_model` (above) answers *"refresh this model, tell me when it's
done"*; these address individual tables and partitions:

```sql
SELECT * FROM m."Model".dax_refresh();                                  -- whole model, type 'full'
SELECT * FROM m."Model".dax_refresh_table('Sales');
SELECT * FROM m."Model".dax_refresh_partition('Sales', '2024');         -- REST cannot do this
SELECT * FROM m."Model".dax_refresh(type := 'dataOnly', max_parallelism := 4,
    objects_json := '[{"table":"Sales"},{"table":"Sales","partition":"2024"}]');
```

Each returns one row: `status`, `refresh_type`, `database`, `objects`, `duration_ms`.

- **These are synchronous** — the XMLA command doesn't return until the refresh finishes, so there is no
  request id and nothing to poll (unlike the REST path). A long refresh is a long statement; Ctrl+C cancels it.
- `type` accepts TMSL's own spellings (`full`, `clearValues`, `calculate`, `dataOnly`, `automatic`, `add`,
  `defragment`) **and** the REST vocabulary (`Full`, `ClearValues`, …), case-insensitively. An unknown value is
  rejected up front rather than becoming an XMLA parse error.
- `max_parallelism` wraps the refresh in a TMSL `sequence`, which is the only form that expresses it.
- Requires the **read-write** XMLA endpoint (Fabric/Premium capacity, *Semantic models workload → XMLA
  endpoint = Read Write*) and model-write permission.

Data writes throw (a semantic model's tables are read-only through DAX), and `BEGIN`/`COMMIT`/`ROLLBACK` are
no-ops so a wrapping DuckDB transaction over read-only DAX doesn't fail. Refresh is the **only** TMSL verb
exposed — there is no generic `dax_tmsl(command)`, deliberately, since the same path would run
`createOrReplace`/`delete` and turn a read-only provider into arbitrary model mutation. Full design + the
DirectLake-passthrough / TMDL notes: [`docs/dax-provider.md`](docs/dax-provider.md).

## Build

### Managed bridge

```bash
dotnet build dotnet/Fabricator.Bridge -c Release
pwsh scripts/publish-managed.ps1     # self-contained publish next to the built extension
```

SQL Server access uses **`Microsoft.Data.SqlClient` 7.0.2**, alongside
**`Microsoft.Data.SqlClient.Extensions.Azure`** at the same version. The second package is **not optional**:
since SqlClient 7.0 the Entra ID (Azure AD) authentication providers live there, so without it every
`Authentication=Active Directory …` connection — Fabric Warehouse with a service principal, the Fabric SQL
endpoint, Azure SQL with Entra — fails at connect with *"Cannot find an authentication provider for
'ActiveDirectoryServicePrincipal'"*. Both are declared in the csproj files; the note matters only if you
repin them.

### Extension

Dependencies — four git submodules, all pinned by SHA. **Init non-recursively:**

```bash
git submodule update --init          # NOT --recursive — see engineered-wood below
```

- **`duckdb@v1.5.5`** + **`extension-ci-tools`** — the DuckDB source + build tooling, both
  `shallow = true` so the checkout stays small. Neither has a `branch =` line, so
  `git submodule update --remote` can't silently move the pin to an unreleased tip; bump them by
  checking out a new SHA and committing the gitlink.
- **`engineered-wood`** — the pure-C# Delta/Parquet library, referenced in-tree by `Fabricator.Bridge`.
  Pinned to **upstream [`clast-project/engineered-wood`](https://github.com/clast-project/engineered-wood)**
  since 2026-08-12: fabricator used to carry a small patch set on a fork branch, and every patch has landed
  upstream, so there is nothing left to fork. **This is why the init must be non-recursive:** it has a nested
  `parquet-testing` submodule holding ~½ GB of Apache test data that the build does not need.
- **`DuckDB.ExtensionKit`** — the MIT NativeAOT extension toolkit
  ([`Giorgi/DuckDB.ExtensionKit`](https://github.com/Giorgi/DuckDB.ExtensionKit)). Needed ONLY to build
  the single-file distribution artifact; nothing else in the repo references it, so a normal build works
  without it.

This repo's default branch is **`v1.5-variegata`**, matching DuckDB's own name for the 1.5 release line;
the `duckdb` pin moves tag by tag within that line. (`main` is reserved for tracking duckdb `main`.)

`httpfs` is linked unconditionally (for `s3://`), so it needs OpenSSL + curl from **vcpkg**:
`vcpkg install openssl:x64-windows-static curl:x64-windows-static` (with `VCPKG_ROOT` set).

On Windows, build with CMake + Ninja inside a **VS 18** dev environment — run
`"C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"` first (a plain
shell fails to compile with `Cannot open include file: 'stdint.h'`; an older toolset such as VS 2022
fails at *link* with `unresolved external symbol __std_rotate` / `__std_unique_1`). The `shell` target
produces `duckdb.exe`; because the extension is statically embedded, rebuild `shell`/`unittest` (not just
the loadable target) after changing extension code:

```powershell
cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 `
  -DDUCKDB_EXTENSION_CONFIGS="<repo>/extension_config.cmake" `
  -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 `
  -DENABLE_EXTENSION_AUTOLOADING=1 -DENABLE_EXTENSION_AUTOINSTALL=1 `
  -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" `
  -DVCPKG_TARGET_TRIPLET=x64-windows-static `
  -S <repo>/duckdb -B <repo>/build/release
cmake --build build/release --target fabricator_loadable_extension duckdb shell
```

Add `-DOVERRIDE_GIT_DESCRIBE=v1.5.5` if you need the loadable to declare its DuckDB version for
loading into an official DuckDB build (the shallow clone has no tag context, so it otherwise
reports `v0.0.1` and the official engine rejects it).

**Prerequisites**, all needed before the configure step above: **Visual Studio 18** (or its Build Tools) with
the C++ workload — the toolset this links against; the **.NET SDK 10** (the managed projects target
`net10.0;net8.0`, and `publish-managed.ps1` needs the 10 SDK); **CMake ≥ 3.21 and Ninja**; **vcpkg**,
bootstrapped with `VCPKG_ROOT` set (it supplies the OpenSSL + curl that the statically linked `httpfs`
needs); and **PowerShell 7 (`pwsh`)** to run the managed publish. On Linux/macOS the same list applies minus
Visual Studio, with `x64-linux`/`arm64-osx` as the vcpkg triplet.

Produces `build/release/extension/fabricator/fabricator.duckdb_extension` and a `build/release/duckdb.exe`
that already embeds the extension (no `LOAD` needed). The bridge is located via `FABRICATOR_MANAGED_DIR`,
else an `fabricator/` folder next to the loaded module — `publish-managed.ps1` writes it to
`build/release/extension/fabricator/fabricator`, so when running `duckdb.exe` directly set
`FABRICATOR_MANAGED_DIR` to that folder:

```powershell
$env:FABRICATOR_MANAGED_DIR = "$PWD/build/release/extension/fabricator/fabricator"
build/release/duckdb.exe -unsigned -c "ATTACH 'mssql://…' AS db (TYPE fabricator); SELECT …"
```

## Layout

```
src/                         C++ DuckDB extension
  include/fabricator/abi.h     C ABI vtable + Arrow C structs (shared contract)
  fabricator/                  CoreCLR host + Arrow ingest/produce (generic core, namespace fabricator)
  catalog/  dml/  copy/      catalog, DML, COPY operators (provider-agnostic — no T-SQL in C++)
                             catalog/ also registers discovered + custom scalar/table/proc/table-in-out
                             functions and the table-in-out OperatorFinalize optimizer extension
  fabricator_optimizer.cpp     LIMIT / TopN pushdown optimizer extension
  fabricator_extension.cpp    extension entry + fabricator_query / _exec / cache / version functions
  fabricator_storage.cpp      ATTACH (TYPE fabricator); fabricator_secret.cpp  secret type
dotnet/Fabricator.Bridge/      backend-agnostic managed bridge (ABI, Arrow, IBackend, streaming bulk)
                             also hosts the Delta provider (DeltaCatalog/DeltaReader over engineered-wood)
dotnet/Fabricator.SqlServer/   Microsoft.Data.SqlClient backend + composition root
dotnet/Fabricator.AnalysisServices/  Power BI / DAX (ADOMD) backend — PROVIDER 'dax'
engineered-wood/             in-tree submodule: pure-C# Delta/Parquet library (the delta provider's log layer)
scripts/publish-managed.ps1  self-contained publish of the bridge + .NET runtime
test/                        verify_*.test + mssqlcompat/ (regenerated from the native extension)
CMakeLists.txt, Makefile, extension_config.cmake
```

## License

[Apache License 2.0](LICENSE) © 2026 Christoph Mettler.

Bundled submodules keep their own licenses: [DuckDB](https://github.com/duckdb/duckdb) (MIT),
[extension-ci-tools](https://github.com/duckdb/extension-ci-tools) (MIT),
[DuckDB.ExtensionKit](https://github.com/Giorgi/DuckDB.ExtensionKit) (MIT), and
[engineered-wood](https://github.com/clast-project/engineered-wood) (Apache 2.0).
