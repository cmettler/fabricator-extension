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

> Project knowledge & build notes for contributors live in [`CLAUDE.md`](CLAUDE.md), the full
> warehouse design in [`docs/warehouse-support.md`](docs/warehouse-support.md), and the Delta catalog
> design in [`docs/delta-catalog.md`](docs/delta-catalog.md).

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
| | `fabricator_delta_scan(path)` — read a Delta table by path, no ATTACH | ✅ |
| | `fabricator_host_query(sql)` — run a query on DuckDB itself (inherits your search path + `TimeZone`) | ✅ |
| **Macros** | Provider **global** macros — bare `fn(...)` / `FROM fn(...)`, every database, no ATTACH | ✅ |
| | Provider **catalog-bound** macros → `db.schema.m(...)` (namespaced per catalog; expanded by the binder) | ✅ |
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
| | — configurable isolation (consistent snapshot per call); per-row procs run in DuckDB's transaction | ✅ |
| | **Custom C# aggregates** (UDAF) → `db.schema.agg(x)` in `GROUP BY` / parallel / `OVER(…)`; opt-in disk-spill (`SupportsSpill`) | ✅ |
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
-- max_datetime2_scale=6, is_utf8_collation=true, is_binary_collation=true, default_write_isolation=snapshot
```

What the profile drives automatically:

- **Connection mode.** Fabric/Synapse reject MARS, so it's auto-disabled (`mssql_mars=auto`); with MARS off,
  data scans use **pooled** connections and the write transaction runs at **SNAPSHOT** isolation. Override
  with `SET mssql_mars='true'|'false'|'auto'` **before** ATTACH. (Read-your-writes for *scans* is given up on
  MARS-off engines — a documented trade-off — but *metadata* reads still see your own uncommitted DDL, so
  `CREATE TABLE` then immediate use works.)
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
**TopN** is pushed only when all order keys are non-string and NULL-ordering-compatible (SQL Server
ASC = NULLS FIRST, DESC = NULLS LAST), and there is no pushed filter; the DuckDB sort node is kept, so
results are always correct.

The optimizer also receives each table's approximate **row count** (from partition stats) and
**per-column NDV** (distinct-value estimate) for better join ordering. Min/max bounds are intentionally
*not* reported (DuckDB would prune filters on them, and SQL Server stats are sampled/stale).

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
`has_native_json`, `is_utf8_collation`, `is_binary_collation`, `default_write_isolation`, …). See
[Microsoft Fabric & Synapse](#microsoft-fabric--synapse-warehouse).

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

### `fabricator_delta_scan(path) -> TABLE`

Read a Delta table **by path, with no ATTACH** — the quickest way to look at one:

```sql
SELECT * FROM fabricator_delta_scan('s3://bucket/lake/trips') LIMIT 10;
SELECT * FROM fabricator_delta_scan('abfss://ws@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables/t');
```

Filesystem credentials come from DuckDB secrets exactly as for `read_parquet`. For a whole folder of tables,
plus writes and DML, ATTACH the [Delta provider](#delta-lake-provider) instead.

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
catalog like discovered ones. Implement `ICatalogScalarFunction`, `IArrowTableFunction`,
`IArrowInOutFunction`, or `IArrowAggregateFunction` (in `Fabricator.Bridge`) and register them in
`CustomFunctions` — each receives an Arrow `RecordBatch` and returns Arrow, fully vectorized.

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
- Custom C#-authored in-out functions (`IArrowInOutFunction`, or the fixed-schema `StaticInOutFunction`
  base) are called by their **bare name** (e.g. `db.dbo.cf_tag(<table>)`, not `_each`) and stream on the same
  gate-based exchange; the author's `DoExchange` reads the input and yields output + a per-input sentinel.

### Custom aggregates (UDAF)

Provider-authored aggregate functions written in C# (`IArrowAggregateFunction`) are registered as DuckDB
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

| Setting | Status | Description |
|---------|--------|-------------|
| `mssql_mars` | **Active** | MARS mode: `auto` (default, per engine — off for Fabric/Synapse) \| `true` \| `false`. Resolved once at first connection — set **before** ATTACH |
| `mssql_command_timeout` | **Active** | `SqlCommand.CommandTimeout` (seconds) for scans / DML / bulk; **default `0` = infinite**. Server-enforced per round-trip; overrides the per-catalog `command_timeout` ATTACH option |
| `mssql_default_varchar_length` | **Active** | Length `n` for created text columns (`NVARCHAR(n)`/`VARCHAR(n)`); unset ⇒ `MAX`. Needed for indexable string keys |
| `mssql_default_table_type` | **Active** | Created-table storage: `''` (rowstore) \| `clustered columnstore` (CCI, box/Azure; no-op on Fabric — columnstore already) |
| `mssql_cluster_by` | **Active** | Comma-separated columns → Fabric Warehouse/Synapse `WITH (CLUSTER BY (cols))` on created tables (fallback for a native `SORTED BY` clause; no-op on box) |
| `mssql_add_identity` | **Active** | Auto-add a `BIGINT IDENTITY` surrogate key (`<table>_id`) to created tables (CREATE + CTAS); overrides the per-catalog `add_identity` ATTACH option (`SET false` to skip for fact tables) |
| `mssql_ctas_text_type` | **Active** | Whole-type override for text columns on CREATE/CTAS/COPY (e.g. `'VARCHAR(64)'`); wins over the collation choice + length |
| `mssql_exec_invalidate_cache` | **Active** | Auto-invalidate the catalog cache after DDL run via `fabricator_exec` (default `false`) |
| `mssql_isolation_level` | **Active** | SQL transaction isolation level for table-in-out (`fn_each`) calls; overrides the ATTACH `isolation_level` per session (empty ⇒ provider default) |
| `mssql_insert_batch_size`, `mssql_insert_max_rows_per_statement`, `mssql_insert_max_sql_bytes`, `mssql_insert_use_returning_output` | Accepted | Registered with defaults + `>= 1` validation; no-op (INSERT streams via SqlBulkCopy) |
| `mssql_connection_*`, `mssql_*_timeout`, `mssql_min_connections`, `mssql_connection_cache` | Accepted | No-op (SqlClient pools by connection string) |
| `mssql_order_pushdown` | Accepted | No-op — TopN is pushed automatically when safe (always-on, not gated) |
| `mssql_copy_tablock`, `mssql_copy_flush_rows`, `mssql_ctas_use_bcp`, `mssql_convert_varchar_max`, `mssql_catalog_cache_ttl` | Accepted | No-op |

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

Partitioned tables are batched as well — the partition value is not stored in the data files, so it travels
alongside them and is applied in the same query.

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
`fabricator_delta_tblproperties`.

**Precedence, lowest first: ATTACH default < `SET delta_write_options` < the table's property < the
statement's `WITH`.** The property outranks the session setting deliberately — it is a property *of the
table*, so a stray `SET` in someone's session must not silently change a table's storage format; `WITH`
remains the per-statement escape hatch.

⚠ A `CREATE OR REPLACE` **inherits** the declaration and cannot **change** it: its `WITH` applies to that
statement's write only. (Same as every create flag — `deletion_vectors` and friends are also fixed at
creation.) To change a declaration, use
`SELECT * FROM fabricator_delta_set_tblproperties('lake', 'main.t',
'{"fabricator.parquet.compression":"gzip"}')` (a table function, so it needs the `FROM`).
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
work), and the Delta log layer is the pure-C# [engineered-wood](https://github.com/cmettler/engineered-wood)
library (an in-tree submodule).

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
> Two things follow. A **concurrent reader** (Spark, delta-rs) can open the table between the commits and
> see it *empty*. And if the data write **fails**, the empty table stays — a statement you saw fail has
> still created something.
>
> In practice the failures you are likely to hit are refused *before* the table is created, because the
> schema must convert to Delta types first — e.g. a `TIMESTAMP_NS` or `INTERVAL` column errors and leaves
> nothing behind. What is exposed is a *storage* failure during the write (permissions, disk full,
> network).
>
> **⚠ Recover with `CREATE OR REPLACE TABLE … AS SELECT` (or `DROP TABLE` then create) — a plain re-run
> does not.** Once the table exists, a plain `CREATE TABLE t AS SELECT …` is refused with
> *"Table with name t already exists!"*, and `CREATE TABLE IF NOT EXISTS` leaves the empty table by
> definition. Only `CREATE OR REPLACE` overwrites it.
>
> This is a Delta-writer API limitation, not a Delta format one — the format allows a single commit that
> both creates the table and adds data.

| Feature | Status |
|---------|--------|
| Discover tables — local (`System.IO`), S3 (host-FS glob), OneLake (Fabric Unity Catalog REST API) | ✅ (generic non-OneLake ADLS not supported — duckdb-azure glob #174) |
| Streaming scan + filter pushdown (Delta file pruning + Parquet row-group skipping) | ✅ |
| `CREATE TABLE` / `INSERT` / CTAS / COPY (streaming bulk via the standard write path) | ✅ |
| `DELETE` / `UPDATE` — rowid deletion-vectors / merge-on-read (default) or copy-on-write (`deletion_vectors false`) | ✅ |
| `DROP TABLE`, `ALTER TABLE … ADD COLUMN`, `RENAME TABLE` (local + OneLake) | ✅ |
| Multi-schema: `schemas true` (local/S3 `<root>/<schema>/<table>`); schema-enabled OneLake lakehouses | ✅ |
| Time travel: `FROM t AT (VERSION => n)` and `AT (TIMESTAMP => ts)` | ✅ |
| Snapshots/history: `fabricator_delta_snapshots('<catalog>', '<schema.>table')` | ✅ |
| **Exactly-once appends** (Delta application transactions / Spark `txnAppId`): `fabricator_delta_set_transaction_version` + `_get_transaction_version` | ✅ |
| **Change Data Feed**: `change_data_feed true` + `fabricator_delta_changes('<catalog>', '<schema.>table', from[, to])` | ✅ |
| **Partitioning**: native `CREATE TABLE … PARTITIONED BY (cols)` (or the `delta_write_options` setting) → Hive `col=value/` layout | ✅ |
| **Write tuning**: compression / row-group size / bloom filters via ATTACH options, the `delta_write_options` setting, or per-table `WITH (…)` | ✅ |
| **Liquid clustering**: `SORTED BY (cols)`, `bucket()` / `hilbert_index()`, clustered `OPTIMIZE` (incremental ZCube), `ALTER … SET SORTED BY` | ✅ |
| Per-table `WITH (…)` — Delta properties / feature-flag overrides / write tuning stamped in the CREATE commit | ✅ |
| `native_write` / `native_read` — DuckDB's own Parquet reader/writer for data files (EW keeps the `_delta_log`) | ✅ |
| VARIANT columns; `set_tblproperties` / `tblproperties`; `OPTIMIZE` / `VACUUM` maintenance | ✅ |
| Concurrent writers: OCC retry (append/CTAS) + **row-level concurrency** (disjoint-row DML on DV tables); S3 multi-writer via a secret | ✅ |
| Concurrent writers on **OneLake**, many processes appending to ONE table — no lost writes (measured: 96 concurrent commits, all landed), but a losing writer can occasionally surface an error rather than retrying transparently, so retry the statement | ⚠ |

```sql
-- Time travel + history
SELECT * FROM lake.main.t AT (VERSION => 3);
SELECT version, operation, timestamp FROM fabricator_delta_snapshots('lake', 'main.t') ORDER BY version;

-- Change Data Feed (enable per catalog at ATTACH, then read the row-level feed)
ATTACH '/lake/root' AS cdf (TYPE fabricator, PROVIDER 'delta', change_data_feed true);
CREATE TABLE cdf.main.t AS SELECT * FROM (VALUES (1,'a'),(2,'b')) v(id,val);
DELETE FROM cdf.main.t WHERE id = 2;
SELECT _change_type, id, val, _commit_version, _commit_timestamp
  FROM fabricator_delta_changes('cdf', 'main.t', 0);     -- to omitted => latest
-- insert/insert (v1), delete (v2): each row tagged with its commit version + timestamp (epoch ms)
```

> **Caveat if you also use `row_tracking true` and read the feed from another engine.** An `INSERT` run
> inside an explicit `BEGIN … COMMIT` writes a `_change_data` file whose row-id columns are **NULL**, where
> the same `INSERT` in autocommit records no change file at all and a reader derives real row ids from the
> data file. The rows, change types, commit versions and timestamps are identical either way — and
> `fabricator_delta_changes` never projects row identity — so this is invisible through DuckDB and matters
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

`fabricator_delta_set_transaction_version(catalog, '<schema.>table', app_id, version [, expected_previous])`
**parks** the version on the current transaction; at `COMMIT` it is compared-and-swapped against the
latest snapshot. It therefore **requires an explicit `BEGIN … COMMIT`** — that is what makes the swap
atomic with the write. Omit `expected_previous` to mean *"the table must record nothing for this
producer yet"* (a first batch); pass it to chain batch to batch.

```sql
ATTACH '/lake/root' AS lake (TYPE fabricator, PROVIDER 'delta');
CREATE TABLE lake.main.t (id INTEGER);

BEGIN;
CALL fabricator_delta_set_transaction_version('lake', 't', 'loader', 1);
INSERT INTO lake.main.t VALUES (1), (2);
COMMIT;                                   -- data + the txn action, one version

SELECT * FROM fabricator_delta_get_transaction_version('lake', 't', 'loader');
-- loader  1

-- A REPLAY of that same batch fails the compare-and-set instead of duplicating the rows:
BEGIN;
CALL fabricator_delta_set_transaction_version('lake', 't', 'loader', 1);
INSERT INTO lake.main.t VALUES (1), (2);
COMMIT;                                   -- error: transaction version conflict … for 'loader'
                                          -- the table still holds 2 rows

-- The next batch states the version it expects to be at:
BEGIN;
CALL fabricator_delta_set_transaction_version('lake', 't', 'loader', 2, 1);
INSERT INTO lake.main.t VALUES (3);
COMMIT;
```

A failed swap is **not a conflict to retry** — retrying cannot make an already-committed batch
un-commit. Read the recorded version back and decide whether the batch still needs writing.

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
| `isolation_level` | **`serializable`** | Matches Fabric Spark, which commits at `Serializable` — so a table with no `delta.isolationLevel` property gets the same guarantee whichever engine writes it. **Changed 2026-08-01** (was `write_serializable`, Databricks' default, which made us the weaker writer on any undeclared table). ⚠ **Row-level concurrency needs `write_serializable`**: under `serializable` concurrent disjoint-row DML on one file conflicts instead of composing, so set `isolation_level 'write_serializable'` if you rely on it |
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
- **We do not yet emit the flag ourselves.** Other engines therefore treat *our* appends as
  possibly-having-read and check them under every isolation level, so a Spark transaction can abort
  against our concurrent append (`DELTA_CONCURRENT_APPEND`) even on a `write_serializable` table where it
  would have committed against Spark's own blind append. Retry is the workaround; the fix is tracked.

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

### Extension

Dependencies — four git submodules, all pinned by SHA. **Init non-recursively:**

```bash
git submodule update --init          # NOT --recursive — see engineered-wood below
```

- **`duckdb@v1.5.5`** + **`extension-ci-tools`** — the DuckDB source + build tooling, both
  `shallow = true` so the checkout stays small. Neither has a `branch =` line, so
  `git submodule update --remote` can't silently move the pin to an unreleased tip; bump them by
  checking out a new SHA and committing the gitlink.
- **`engineered-wood`** — the pure-C# Delta/Parquet library (pinned to the
  [`cmettler/engineered-wood`](https://github.com/cmettler/engineered-wood) fork), referenced in-tree by
  `Fabricator.Bridge`. **This is why the init must be non-recursive:** it has a nested
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

(`CLAUDE.md` has the full from-a-fresh-clone quickstart + prerequisites.)

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
CMakeLists.txt, Makefile, extension_config.cmake, CLAUDE.md
```

## License

[MIT](LICENSE) © 2026 Christoph Mettler.

Bundled submodules keep their own licenses (all MIT): [DuckDB](https://github.com/duckdb/duckdb),
[extension-ci-tools](https://github.com/duckdb/extension-ci-tools), and
[engineered-wood](https://github.com/cmettler/engineered-wood).
