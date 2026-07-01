# mssql_net — DuckDB ⇄ SQL Server via a C# (CoreCLR) Arrow bridge

A DuckDB extension that connects to **Microsoft SQL Server** by hosting a C# layer (via **CoreCLR**)
**in-process** and exchanging data + metadata as **Apache Arrow** over the Arrow C Stream Interface
(`ArrowArrayStream`). It is a direct, in-process replacement for the Arrow-Flight transport used by
the "Airport" extension.

**How it differs from the native-TDS [`mssql` extension](https://github.com/hugr-lab/mssql-extension):**
that extension speaks the TDS wire protocol directly in C++. This one delegates *all* SQL Server I/O
to C# using **`Microsoft.Data.SqlClient`** — the C++ extension only registers DuckDB functions and
ingests the Arrow streams the bridge produces. Connection strings are therefore plain
`Microsoft.Data.SqlClient` strings, and connection pooling / Windows & Azure auth come from SqlClient
natively. The C++ core (`arrownet`) and managed `ArrowNet.Bridge` are transport- and backend-agnostic,
intended for reuse by a future Power BI / DAX connector.

**Works against box SQL Server, Azure SQL Database, and the Microsoft Fabric / Synapse warehouse family.**
The extension detects a server **capability profile** at ATTACH and adapts connection behavior (MARS,
isolation) and type mapping (collation-driven `VARCHAR`/`NVARCHAR`, `datetime2` scale, …) to the engine —
including Fabric Warehouse over an Entra service principal. See
[Microsoft Fabric & Synapse](#microsoft-fabric--synapse-warehouse).

**One binary, multiple providers.** The C++ core (`arrownet`) and managed `ArrowNet.Bridge` are
provider-agnostic, and the same extension hosts several backends selected at ATTACH via `PROVIDER` (or
inferred from the connection scheme):

- **`sqlserver`** (default) — Microsoft SQL Server / Azure SQL / Fabric & Synapse warehouse (this document).
- **`delta`** — a **Delta Lake** folder/lakehouse as a read-write catalog (local, S3, ADLS, and **Fabric
  OneLake**), with DML, time travel, snapshots, and Change Data Feed. See [Delta Lake provider](#delta-lake-provider).
- **`dax`** — **Power BI / Analysis Services** semantic models over ADOMD (read-only DAX). See
  [`docs/dax-provider.md`](docs/dax-provider.md).

> Project knowledge & build notes for contributors live in [`CLAUDE.md`](CLAUDE.md), the full
> warehouse design in [`docs/warehouse-support.md`](docs/warehouse-support.md), and the Delta catalog
> design in [`docs/delta-catalog.md`](docs/delta-catalog.md).

## Feature Status

| Area | Feature | Status |
|------|---------|--------|
| **Connect** | `ATTACH (TYPE mssql_net)` — connection string, `mssql://` URI, `CREATE SECRET` | ✅ |
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
| | CREATE TABLE AS / COPY TO (streaming bulk via `SqlBulkCopy`) | ✅ |
| | Bounded-memory streaming bulk write (INSERT/CTAS/COPY) | ✅ |
| | CHECK/FK constraint enforcement on INSERT | ✅ (`SqlBulkCopyOptions.CheckConstraints`; COPY/CTAS skip for speed) |
| **DDL** | CREATE/DROP TABLE, CREATE/DROP SCHEMA, ALTER TABLE | ✅ |
| | PRIMARY KEY / UNIQUE / NOT NULL / literal DEFAULT on CREATE | ✅ |
| | CHECK constraints, non-literal DEFAULTs | ❌ (use `mssql_net_exec`) |
| **Tx** | BEGIN / COMMIT / ROLLBACK (connection pinning, read-your-writes) | ✅ |
| **Functions** | `mssql_net_query`, `mssql_net_exec`, `mssql_refresh_cache`, `mssql_invalidate_cache`, `mssql_version` | ✅ |
| | `mssql_net_functions(catalog)` — list discovered routines | ✅ |
| **Callable** | Discovered scalar UDFs → `db.schema.fn(args)` (vectorized over Arrow) | ✅ |
| | Discovered table-valued functions → `SELECT * FROM db.schema.tvf(args)` (+ projection/filter pushdown) | ✅ |
| | Discovered stored procedures → `SELECT * FROM db.schema.proc(name := val)` (named/optional + OUTPUT params) | ✅ |
| | Custom C#-authored scalar / table / table-in-out functions | ✅ |
| | **Table-in-out**: `db.schema.fn_each(<input table>)` — apply a TVF (CROSS APPLY) or proc per input row | ✅ |
| | — configurable isolation (consistent snapshot per call); per-row procs run in DuckDB's transaction | ✅ |
| | **Custom C# aggregates** (UDAF) → `db.schema.agg(x)` in `GROUP BY` / parallel / `OVER(…)`; opt-in disk-spill (`SupportsSpill`) | ✅ |
| | Load-time *global* functions (connection-free); proc multi-result-set / `INOUT` params | ❌ deferred |
| **Warehouse** | Auto-detected server profile (edition / version / collation) + `mssql_server_info()` | ✅ Fabric Warehouse validated |
| | Connection mode: `mssql_mars` (auto per engine), pooled reads + SNAPSHOT writes when MARS off | ✅ |
| | Collation-adaptive `VARCHAR`/`NVARCHAR`; binary-collation string `ORDER BY` pushdown | ✅ |
| | PK/UNIQUE as `NONCLUSTERED NOT ENFORCED` (via ALTER) → rowid → UPDATE/DELETE on Fabric | ✅ |
| | Clustered columnstore tables (`mssql_default_table_type`) | ✅ (box; implicit on Fabric) |
| **Diag** | Connection-pool diagnostics (`mssql_pool_stats`, `mssql_open/close/ping`) | ❌ |
| | COPY to temp tables (`#t` / empty-schema syntax) | ❌ |

✅ implemented & verified · ⚠️ partial / via SqlClient · ❌ not yet

## Quick Start

This extension is not (yet) in the DuckDB community repository — build it from source (see
[Build](#build)), then load the unsigned extension. The managed bridge is published self-contained
next to the extension (no .NET install required on the host).

```sql
-- Connection string (a valid Microsoft.Data.SqlClient string)
ATTACH 'Server=host,1433;Database=db;User Id=sa;Password=***;TrustServerCertificate=true;Encrypt=true'
  AS mssql (TYPE mssql_net);

SELECT * FROM mssql.dbo.people WHERE id > 1;   -- automatic streaming scan + filter pushdown
SELECT count(*) FROM mssql.dbo.people;

DETACH mssql;
```

## Connection Configuration

A `mssql_net` connection is given **either** a `Microsoft.Data.SqlClient` connection string, the
`mssql://` URI convenience form, **or** a stored secret.

```sql
-- ADO.NET / SqlClient connection string
ATTACH 'Server=host,1433;Database=db;User Id=sa;Password=***;TrustServerCertificate=true;Encrypt=true'
  AS mssql (TYPE mssql_net);

-- mssql:// URI (encrypt defaults on; ?encrypt=false to disable)
ATTACH 'mssql://sa:***@host:1433/db' AS mssql (TYPE mssql_net);

-- Restrict catalog discovery on large servers (case-insensitive regex, partial match)
ATTACH 'Server=...;Database=...' AS mssql
  (TYPE mssql_net, schema_filter '^(dbo|sales)$', table_filter '^fact_');
```

### Secrets

```sql
-- SQL auth. Password/access_token are redacted in duckdb_secrets().
CREATE SECRET sql1 (TYPE mssql_net,
  host 'host', port 1433, database 'db', user 'sa', password '***', use_encrypt true);
ATTACH '' AS mssql (TYPE mssql_net, SECRET sql1);
SELECT * FROM mssql_net_query('sql1', 'SELECT 1');
```

Secret field names mirror the native `mssql` extension for cross-compatibility:
`host`, `port`, `database`, `user`, `password`, `use_encrypt`, `access_token`, `authentication`,
`azure_secret`, `schema_filter`, `table_filter`, `application_name`.

### Azure Entra ID (Microsoft Fabric SQL endpoints, which require Entra)

`Microsoft.Data.SqlClient` handles Entra natively via the `Authentication=` keyword, so most variants
need no extra code:

```sql
CREATE SECRET fab_sp (TYPE mssql_net,            -- service principal
  host 'xxx.datawarehouse.fabric.microsoft.com', database 'wh',
  authentication 'service_principal', user '<app-client-id>', password '<client-secret>');
CREATE SECRET fab_mi (TYPE mssql_net,            -- (user-assigned) managed identity
  host '...', database 'wh', authentication 'managed_identity', user '<mi-client-id>');
CREATE SECRET fab_def (TYPE mssql_net,           -- DefaultAzureCredential chain
  host '...', database 'wh', authentication 'default');
CREATE SECRET fab_tok (TYPE mssql_net,           -- bring-your-own Entra token
  host '...', database 'wh', access_token '<jwt>');
-- also: interactive, device_code, workload_identity, password.
ATTACH '' AS fab (TYPE mssql_net, SECRET fab_sp);
```

### Integrated / Windows authentication

Use SqlClient's native keywords directly — `Trusted_Connection=true` or `Integrated Security=SSPI`
(Windows). We do **not** implement the native extension's bespoke `authenticator=krb5` / `krb5-*`
connection-string dialect (see [Differences](#differences-from-the-native-mssql-extension)).

### Connection validation

ATTACH validates the connection up front and creates **no** catalog on failure (fail-fast, password
never leaked):

```sql
ATTACH 'Server=nonexistent.host,1433;Database=db;User Id=sa;Password=pass' AS db (TYPE mssql_net);
-- Error: MSSQL connection validation failed: <cause>
```

## Microsoft Fabric & Synapse (warehouse)

Beyond box SQL Server, the extension connects to **Fabric Warehouse**, the **Lakehouse SQL analytics
endpoint**, and **Synapse** pools. At ATTACH it detects a **server capability profile** (engine edition +
product version + database collation) once and adapts automatically — no configuration needed. Inspect it
with `mssql_server_info(catalog)`:

```sql
-- Fabric Warehouse via an Entra service principal:
CREATE SECRET fab (TYPE mssql_net,
  host 'xxxxx.datawarehouse.fabric.microsoft.com', database 'My Warehouse',
  authentication 'service_principal', user '<app-client-id>', password '<client-secret>');
ATTACH '' AS wh (TYPE mssql_net, SECRET fab);

SELECT property, value FROM mssql_server_info('wh');
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
  ATTACH '...' AS wh (TYPE mssql_net, add_identity true);   -- every created table gets <table>_id
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
the first write); all DML — catalog INSERT/UPDATE/DELETE and `mssql_net_exec` — runs on it, and reads
inside the transaction use it too (read-your-writes), so `ROLLBACK` undoes everything.

```sql
BEGIN;
INSERT INTO mssql.dbo.people (id, name) VALUES (9, 'temp');
SELECT mssql_net_exec('mssql', 'UPDATE dbo.people SET salary = 0 WHERE id = 1');
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
otherwise. UPDATE/DELETE require a primary key or unique index (otherwise use `mssql_net_exec`); they do
not support RETURNING.

## CREATE TABLE AS / COPY TO

`CREATE TABLE AS` and `COPY TO` stream the result to SQL Server via `SqlBulkCopy`, auto-creating the
table from the Arrow schema. Both are bounded-memory (the dataset is never fully buffered).

```sql
CREATE TABLE mssql.dbo.summary AS SELECT region, count(*) AS n FROM big GROUP BY region;

COPY (SELECT * FROM src) TO 'mssql://mssql/dbo/target' (FORMAT mssql_net);
COPY src TO 'mssql.dbo.target'  (FORMAT mssql_net);
COPY src TO 'mssql.dbo.target'  (FORMAT 'bcp');               -- 'bcp' is an accepted alias
COPY src TO 'mssql.dbo.target'  (FORMAT 'bcp', REPLACE true); -- drop + recreate
```

COPY target = `mssql://catalog/schema/table` or `catalog.schema.table` (3-part only — temp-table /
empty-schema syntax is not supported). Options: `CREATE_TABLE` (default true), `REPLACE` (default
false). The target is registered in the catalog (queryable immediately).

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
`mssql_net_exec`.

### Temporary tables

`CREATE TEMPORARY TABLE` is **not** mapped to SQL Server `#temp` tables — DuckDB routes every temp table to
its own in-memory/spillable `temp` catalog and forbids qualifying one with an attached catalog (`TEMPORARY
table names can only use the "temp" catalog`), so it never reaches the provider. Just use DuckDB's native temp
tables for transient data (faster — no round-trip, no connection affinity). If you specifically need a
*server-side* `#temp` (e.g. staging for a complex `EXEC`), create and use it via `mssql_net_exec` **inside a
DuckDB `BEGIN`** so all calls share the one pinned connection — and only on a **MARS engine (box SQL Server)**:
`#temp` tables are connection-scoped, so they don't survive across autocommit calls, and on Fabric/Synapse
(MARS off → pooled reads) they aren't visible to subsequent statements.

## Function Reference

### `mssql_net_query(context, sql) -> TABLE`

Stream a raw T-SQL query. `context` may be a connection string, a secret name, or an attached-catalog
name (reuses that catalog's connection).

```sql
SELECT id, name FROM mssql_net_query('Server=...;Database=...', 'SELECT id, name FROM dbo.people');
SELECT * FROM mssql_net_query('mssql', 'SELECT id, name FROM dbo.people');   -- attached catalog
```

### `mssql_net_exec(context, sql) -> BIGINT`

Execute arbitrary T-SQL (DDL/DML/EXEC); returns rows affected (0 for DDL / no-row statements).

```sql
SELECT mssql_net_exec('mssql', 'UPDATE dbo.people SET salary = salary + 1 WHERE id <= 2');
```

Multiple statements separated by `;` (including multiline) run as **one batch** in a single call (return
value = aggregate rows affected). `GO` is **not** supported — it's a sqlcmd/SSMS client directive, not T-SQL
(use `;`, or separate calls). For cross-statement atomicity, wrap in `BEGIN…COMMIT` or a DuckDB `BEGIN`.

```sql
SELECT mssql_net_exec('mssql', 'CREATE TABLE dbo.t (id int); INSERT INTO dbo.t VALUES (1),(2)');
```

### `mssql_refresh_cache(catalog)` / `mssql_invalidate_cache(catalog [, schema [, table]])`

Refresh cached catalog metadata after creating/dropping tables out-of-band (e.g. via
`mssql_net_exec`). `mssql_net_refresh_cache` / `mssql_net_invalidate_cache` are aliases.

```sql
SELECT mssql_net_exec('mssql', 'CREATE TABLE dbo.t (id INT)');
SELECT mssql_refresh_cache('mssql');
SELECT * FROM mssql.dbo.t;

-- ...or auto-invalidate after DDL run via mssql_net_exec (off by default; DDL detected in C#):
SET mssql_exec_invalidate_cache = true;
SELECT mssql_net_exec('mssql', 'CREATE TABLE dbo.t2 (id INT)');
SELECT * FROM mssql.dbo.t2;   -- visible immediately
```

### `mssql_server_info(catalog) -> TABLE(property, value)`

The detected server capability profile for an attached catalog (edition, version, collation, and the
derived flags: `supports_mars`, `has_nvarchar`, `has_datetimeoffset`, `max_datetime2_scale`,
`has_native_json`, `is_utf8_collation`, `is_binary_collation`, `default_write_isolation`, …). See
[Microsoft Fabric & Synapse](#microsoft-fabric--synapse-warehouse).

### `mssql_version() -> VARCHAR`

Extension version (compatibility shim). `arrownet_managed_dir()` / `arrownet_test_scan('x')` are
diagnostics for the CoreCLR + Arrow spine.

## Callable Functions

On `ATTACH`, the extension discovers the database's scalar UDFs, table-valued functions, and stored
procedures and registers them as **DuckDB catalog functions**, resolved as `db.schema.name(...)`.
Signatures and result schemas are resolved lazily on first use (so attach stays cheap) and refreshed by
`mssql_refresh_cache`. All execution is vectorized over Arrow; the C++ side is provider-agnostic (the
SQL lives in C#). `mssql_net_functions('db')` lists what was discovered.

```sql
SELECT schema_name, name, kind FROM mssql_net_functions('mssql');   -- kind: scalar | table | proc | inout
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
`IArrowInOutFunction`, or `IArrowAggregateFunction` (in `ArrowNet.Bridge`) and register them in
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
  ATTACH 'mssql://…' AS mssql (TYPE mssql_net, isolation_level 'snapshot');
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
ATTACH 'mssql://…' AS mssql (TYPE mssql_net);

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

`SET mssql_*` settings are accepted for compatibility with the native extension. Two are **active**;
the rest are accepted but currently no-ops (the C# backend uses `SqlBulkCopy` and SqlClient pooling, so
the native extension's batching/pooling/TDS knobs don't apply).

| Setting | Status | Description |
|---------|--------|-------------|
| `mssql_mars` | **Active** | MARS mode: `auto` (default, per engine — off for Fabric/Synapse) \| `true` \| `false`. Resolved once at first connection — set **before** ATTACH |
| `mssql_default_varchar_length` | **Active** | Length `n` for created text columns (`NVARCHAR(n)`/`VARCHAR(n)`); unset ⇒ `MAX`. Needed for indexable string keys |
| `mssql_default_table_type` | **Active** | Created-table storage: `''` (rowstore) \| `clustered columnstore` (CCI, box/Azure; no-op on Fabric — columnstore already) |
| `mssql_cluster_by` | **Active** | Comma-separated columns → Fabric Warehouse/Synapse `WITH (CLUSTER BY (cols))` on created tables (fallback for a native `SORTED BY` clause; no-op on box) |
| `mssql_add_identity` | **Active** | Auto-add a `BIGINT IDENTITY` surrogate key (`<table>_id`) to created tables (CREATE + CTAS); overrides the per-catalog `add_identity` ATTACH option (`SET false` to skip for fact tables) |
| `mssql_ctas_text_type` | **Active** | Whole-type override for text columns on CREATE/CTAS/COPY (e.g. `'VARCHAR(64)'`); wins over the collation choice + length |
| `mssql_exec_invalidate_cache` | **Active** | Auto-invalidate the catalog cache after DDL run via `mssql_net_exec` (default `false`) |
| `mssql_isolation_level` | **Active** | SQL transaction isolation level for table-in-out (`fn_each`) calls; overrides the ATTACH `isolation_level` per session (empty ⇒ provider default) |
| `mssql_insert_batch_size`, `mssql_insert_max_rows_per_statement`, `mssql_insert_max_sql_bytes`, `mssql_insert_use_returning_output` | Accepted | Registered with defaults + `>= 1` validation; no-op (INSERT streams via SqlBulkCopy) |
| `mssql_connection_*`, `mssql_*_timeout`, `mssql_min_connections`, `mssql_connection_cache` | Accepted | No-op (SqlClient pools by connection string) |
| `mssql_order_pushdown` | Accepted | No-op — TopN is pushed automatically when safe (always-on, not gated) |
| `mssql_copy_tablock`, `mssql_copy_flush_rows`, `mssql_ctas_use_bcp`, `mssql_convert_varchar_max`, `mssql_catalog_cache_ttl` | Accepted | No-op |

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
work), and the Delta log layer is the pure-C# [engineered-wood](https://github.com/curthagenlocher/engineered-wood)
library. Works on **local**, **S3**, **ADLS**, and **Fabric OneLake**. (Aliases: `deltalake`; the primary
name is `engineeredwooddelta`.)

```sql
-- Local / S3 / ADLS folder catalog
ATTACH '/lake/root' AS lake (TYPE mssql_net, PROVIDER 'delta');

-- Fabric OneLake (READ_ONLY false is REQUIRED — DuckDB forces remote ATTACH read-only otherwise;
-- one azure service-principal secret serves DuckDB IO + the OneLake DFS endpoint)
ATTACH 'abfss://Workspace@onelake.dfs.fabric.microsoft.com/LH.Lakehouse/Tables'
  AS lake (TYPE mssql_net, PROVIDER 'delta', SECRET fabric_sp, READ_ONLY false);

SELECT * FROM lake.main.t WHERE id > 10;          -- streaming scan + file/row-group filter pushdown
```

| Feature | Status |
|---------|--------|
| Discover tables (local/S3/ADLS host-FS glob; OneLake via the ADLS Gen2 DFS endpoint) | ✅ |
| Streaming scan + filter pushdown (Delta file pruning + Parquet row-group skipping) | ✅ |
| `CREATE TABLE` / `INSERT` / CTAS / COPY (streaming bulk via the standard write path) | ✅ |
| `DELETE` / `UPDATE` — rowid copy-on-write (plain Delta) or deletion vectors (opt-in) | ✅ |
| `DROP TABLE`, `ALTER TABLE … ADD COLUMN`, `RENAME TABLE` (local + OneLake) | ✅ |
| Multi-schema: `schemas true` (local/S3 `<root>/<schema>/<table>`); schema-enabled OneLake lakehouses | ✅ |
| Time travel: `FROM t AT (VERSION => n)` and `AT (TIMESTAMP => ts)` | ✅ |
| Snapshots/history: `arrownet_delta_snapshots('<catalog>', '<schema.>table')` | ✅ |
| **Change Data Feed**: `change_data_feed true` + `arrownet_delta_changes('<catalog>', '<schema.>table', from[, to])` | ✅ |
| **Partitioning**: native `CREATE TABLE … PARTITIONED BY (cols)` (or the `delta_write_options` setting) → Hive `col=value/` layout | ✅ |
| **Write tuning**: compression / row-group size / bloom filters via ATTACH options or the `delta_write_options` JSON setting | ✅ |
| Concurrent writers (OCC retry on append/CTAS; rowid DML is snapshot-bound) | ✅ |

```sql
-- Time travel + history
SELECT * FROM lake.main.t AT (VERSION => 3);
SELECT version, operation, timestamp FROM arrownet_delta_snapshots('lake', 'main.t') ORDER BY version;

-- Change Data Feed (enable per catalog at ATTACH, then read the row-level feed)
ATTACH '/lake/root' AS cdf (TYPE mssql_net, PROVIDER 'delta', change_data_feed true);
CREATE TABLE cdf.main.t AS SELECT * FROM (VALUES (1,'a'),(2,'b')) v(id,val);
DELETE FROM cdf.main.t WHERE id = 2;
SELECT _change_type, id, val, _commit_version, _commit_timestamp
  FROM arrownet_delta_changes('cdf', 'main.t', 0);     -- to omitted => latest
-- insert/insert (v1), delete (v2): each row tagged with its commit version + timestamp (epoch ms)
```

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
ATTACH '/lake' AS lake (TYPE mssql_net, PROVIDER 'delta',
  compression 'zstd', row_group_size 1000000, bloom_filter_columns 'id,email');

SET delta_write_options = '{"compression":"zstd","row_group_size":1000000,
                            "bloom_filter_columns":["id"],"partition_by":["region","year"]}';
```

Compression: `snappy` (default) / `zstd` / `gzip` / `brotli` / `lz4` / `uncompressed` / …. Dictionary
encoding is auto-enabled per column and min/max statistics are always collected (driving file + row-group
pruning); bloom filters are off unless requested. (Partitioning is honored by the Delta provider; the
SQL Server / DAX providers ignore `PARTITIONED BY`.)

Two more write options (also via `delta_write_options`):

- **`replace_where`** — an atomic **partition overwrite**. On an `INSERT`, `{"replace_where":{"region":"EU"}}`
  removes exactly the matching partition's files and adds the new rows in **one Delta commit** (delta-rs's
  static partition overwrite). Keys must be partition columns; the inserted data must fall within them.
  ```sql
  SET delta_write_options='{"replace_where":{"region":"EU"}}';
  INSERT INTO lake.main.sales SELECT * FROM new_eu_rows;   -- EU replaced atomically, other partitions untouched
  SET delta_write_options='';
  ```
- **`merge_schema`** — additive schema evolution. On `CREATE OR REPLACE` / CTAS, a wider incoming schema
  **adds the new columns** (nullable) instead of silently dropping them. Also a per-catalog `ATTACH` option
  (`merge_schema true`). Note: a plain `INSERT` of wider data is rejected by DuckDB's binder before it reaches
  the provider — use `ALTER TABLE ADD COLUMN` (supported) for append-time evolution, or `CREATE OR REPLACE`.

Delta ATTACH options: `PROVIDER 'delta'`, `SECRET <azure_sp>` (OneLake/ADLS auth), `READ_ONLY false`
(required for OneLake writes), `schemas true` (two-level layout on local/S3), `compression` / `row_group_size`
/ `bloom_filter_columns` (write-tuning defaults), `deletion_vectors true`
(DV-based DELETE/UPDATE), `change_data_feed true` (CDF capture), `in_commit_timestamps true` (in-protocol
monotonic timestamps for Spark/Fabric interop — `AT (TIMESTAMP)` also resolves on plain tables via the
always-on commit timestamp). Tables are written as **plain Delta** (minReader 1 / minWriter 2, no features)
by default, so Spark / delta-kernel / Fabric OneLake conversion read them; features are added only when the
corresponding option is set. Full design: [`docs/delta-catalog.md`](docs/delta-catalog.md).

## Build

### Managed bridge

```bash
dotnet build dotnet/ArrowNet.Bridge -c Release
pwsh scripts/publish-managed.ps1     # self-contained publish next to the built extension
```

### Extension

Requires the submodules (`duckdb@v1.5.4` + `extension-ci-tools@v1.5.3`):

```bash
git submodule update --init --recursive
make                                 # builds DuckDB + the extension (POSIX/CI)
pwsh scripts/publish-managed.ps1     # publish the bridge beside the extension
```

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
  -S <repo>/duckdb -B <repo>/build/release
cmake --build build/release --target mssql_net_loadable_extension duckdb shell
```

Produces `build/release/extension/mssql_net/mssql_net.duckdb_extension` and a `build/release/duckdb.exe`
that already embeds the extension (no `LOAD` needed). The bridge is located via `ARROWNET_MANAGED_DIR`,
else an `arrownet/` folder next to the loaded module — `publish-managed.ps1` writes it to
`build/release/extension/mssql_net/arrownet`, so when running `duckdb.exe` directly set
`ARROWNET_MANAGED_DIR` to that folder:

```powershell
$env:ARROWNET_MANAGED_DIR = "$PWD/build/release/extension/mssql_net/arrownet"
build/release/duckdb.exe -unsigned -c "ATTACH 'mssql://…' AS db (TYPE mssql_net); SELECT …"
```

## Layout

```
src/                         C++ DuckDB extension
  include/arrownet/abi.h     C ABI vtable + Arrow C structs (shared contract)
  arrownet/                  CoreCLR host + Arrow ingest/produce (generic core, namespace arrownet)
  catalog/  dml/  copy/      catalog, DML, COPY operators (provider-agnostic — no T-SQL in C++)
                             catalog/ also registers discovered + custom scalar/table/proc/table-in-out
                             functions and the table-in-out OperatorFinalize optimizer extension
  arrownet_optimizer.cpp     LIMIT / TopN pushdown optimizer extension
  mssql_net_extension.cpp    extension entry + mssql_net_query / _exec / cache / version functions
  mssql_net_storage.cpp      ATTACH (TYPE mssql_net); mssql_net_secret.cpp  secret type
dotnet/ArrowNet.Bridge/      backend-agnostic managed bridge (ABI, Arrow, IBackend, streaming bulk)
                             also hosts the Delta provider (DeltaCatalog/DeltaReader over engineered-wood)
dotnet/ArrowNet.SqlServer/   Microsoft.Data.SqlClient backend + composition root
dotnet/ArrowNet.AnalysisServices/  Power BI / DAX (ADOMD) backend — PROVIDER 'dax'
scripts/publish-managed.ps1  self-contained publish of the bridge + .NET runtime
test/                        verify_*.test + mssqlcompat/ (regenerated from the native extension)
CMakeLists.txt, Makefile, extension_config.cmake, vcpkg.json, CLAUDE.md
```
