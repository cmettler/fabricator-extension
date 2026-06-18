# mssql_net — DuckDB ⇄ SQL Server via a C# (CoreCLR) Arrow bridge

A DuckDB extension that connects to Microsoft SQL Server by hosting a C# layer
(via CoreCLR) **in-process** and exchanging data and metadata as **Apache Arrow**
over the Arrow C Stream Interface (`ArrowArrayStream`). It is a direct, in-process
replacement for the Arrow-Flight transport used by the "Airport" extension.

Unlike the native-TDS `mssql-extension`, all SQL Server I/O happens in C# using
`Microsoft.Data.SqlClient`; the C++ extension only registers DuckDB functions and
ingests the Arrow streams the bridge produces.

See `C:\Users\c.mettler\.claude\plans\i-want-to-create-soft-crown.md` for the full
phased plan.

## Status

**Phase 1 — complete and verified against real SQL Server (DuckDB v1.5.3).**

`ATTACH` exposes SQL Server schemas/tables/views as a DuckDB catalog and scans
them automatically:

```sql
ATTACH 'Server=host,1433;Database=db;User Id=sa;Password=***;TrustServerCertificate=true;Encrypt=true'
  AS mssql (TYPE mssql_net);

-- ...or a mssql:// connection URI (encrypt defaults on; ?encrypt=false to disable):
ATTACH 'mssql://sa:***@host:1433/db' AS mssql (TYPE mssql_net);

-- ...or store the connection as a secret and reference it by name. The
-- connection string is assembled from the secret's parts (password/access_token
-- redacted in duckdb_secrets()). Works for ATTACH and the query/exec functions.
CREATE SECRET sql1 (TYPE mssql_net,
  host 'host', port 1433, database 'db', user 'sa', password '***', use_encrypt true);
ATTACH '' AS mssql (TYPE mssql_net, SECRET sql1);
SELECT * FROM mssql_net_query('sql1', 'SELECT 1');

-- Azure Entra ID auth (e.g. Microsoft Fabric SQL endpoints, which require Entra).
-- `authentication` maps to the Microsoft.Data.SqlClient Authentication keyword;
-- field names mirror the C++ mssql extension's secret for cross-compatibility.
CREATE SECRET fab_sp (TYPE mssql_net,           -- service principal
  host 'xxx.datawarehouse.fabric.microsoft.com', database 'wh',
  authentication 'service_principal', user '<app-client-id>', password '<client-secret>');
CREATE SECRET fab_mi (TYPE mssql_net,           -- (user-assigned) managed identity
  host '...', database 'wh', authentication 'managed_identity', user '<mi-client-id>');
CREATE SECRET fab_def (TYPE mssql_net,          -- DefaultAzureCredential chain
  host '...', database 'wh', authentication 'default');
CREATE SECRET fab_tok (TYPE mssql_net,          -- bring-your-own Entra token
  host '...', database 'wh', access_token '<jwt>');
-- also: interactive, device_code, workload_identity, password.
ATTACH '' AS fab (TYPE mssql_net, SECRET fab_sp);

SELECT * FROM mssql.dbo.people WHERE id > 1;       -- automatic table scan
SELECT count(*) FROM mssql.dbo.people;             -- aggregates
SELECT s.name, o.amount                            -- joins across SQL Server tables
  FROM mssql.dbo.people s JOIN mssql.sales.orders o ON o.order_id = 1000 + s.id;

-- Projection + filter pushdown: only the referenced columns are SELECTed from SQL
-- Server, and a parameterized WHERE is pushed for the supported predicates
-- (=, <>, <, <=, >, >=, IS [NOT] DISTINCT FROM, IS [NOT] NULL, IN, BETWEEN, AND/OR).
-- Pushdown is best-effort: DuckDB always re-applies every predicate, so anything
-- not pushed is still filtered correctly. Below, only [id]/[name] cross the wire
-- and `id >= 10 AND name = 'Bob'` is sent as a parameterized WHERE:
SELECT id, name FROM mssql.dbo.people WHERE id >= 10 AND name = 'Bob';

-- A bare LIMIT (no ORDER BY, no filter) is pushed as SELECT TOP (n), so previews
-- fetch only n rows instead of the whole table:
SELECT * FROM mssql.dbo.big_table LIMIT 100;

INSERT INTO mssql.dbo.target (id, name) VALUES (1, 'Alice'), (2, 'Bob');  -- INSERT
INSERT INTO mssql.dbo.target SELECT id, name FROM read_csv('data.csv');  -- INSERT … SELECT

-- INSERT … RETURNING uses SQL Server's OUTPUT INSERTED clause, so server-side
-- IDENTITY values and DEFAULTs come back with the inserted rows.
INSERT INTO mssql.dbo.target (name) VALUES ('Carol') RETURNING id, name;
```

```sql
-- UPDATE / DELETE use a rowid derived from the PK (or smallest unique index;
-- compound keys supported). SELECT rowid is also available.
UPDATE mssql.dbo.people SET salary = salary * 1.1 WHERE id = 1;
DELETE FROM mssql.dbo.people WHERE id = 3;
```

```sql
-- BEGIN/COMMIT/ROLLBACK map to a real SQL Server transaction: a connection is
-- pinned (lazily, on the first write) and all DML — catalog INSERT/UPDATE/DELETE
-- and mssql_net_exec — runs on it, so ROLLBACK undoes everything. Reads inside
-- the transaction use the pinned connection too (read-your-writes / uncommitted).
BEGIN;
INSERT INTO mssql.dbo.people (id, name) VALUES (9, 'temp');
SELECT mssql_net_exec('mssql', 'UPDATE dbo.people SET salary = 0 WHERE id = 1');
SELECT count(*) FROM mssql.dbo.people;   -- sees the uncommitted INSERT
ROLLBACK;                                -- both the INSERT and UPDATE are undone
```

```sql
-- CREATE TABLE AS and COPY TO use SqlBulkCopy (table auto-created from the
-- Arrow schema). The C++ side stays provider-agnostic (produces Arrow); the
-- C# backend maps types, creates the table, and bulk-loads.
CREATE TABLE mssql.dbo.summary AS SELECT region, count(*) AS n FROM big GROUP BY region;
COPY (SELECT * FROM src) TO 'mssql://mssql/dbo/target' (FORMAT mssql_net);
COPY src TO 'mssql.dbo.target' (FORMAT mssql_net);
COPY src TO 'mssql.dbo.target' (FORMAT 'bcp');   -- 'bcp' is an accepted alias
```

The COPY target is registered in the catalog (queryable immediately afterwards),
and `IDENTITY` columns are auto-preserved when the source includes them
(`SqlBulkCopy KeepIdentity`) or auto-generated when it doesn't. For compatibility
with the C++ `mssql` extension, `mssql_version()` and the `SET mssql_*` settings
are accepted.

```sql
-- DDL: CREATE/DROP TABLE and CREATE/DROP SCHEMA go through the catalog.
-- Column types map DuckDB -> SQL Server; NOT NULL is honored.
CREATE SCHEMA mssql.staging;
CREATE TABLE mssql.staging.t (id INTEGER NOT NULL, name VARCHAR, amount DECIMAL(10,2));
CREATE TABLE IF NOT EXISTS mssql.staging.t (id INTEGER);   -- no-op if present

-- PRIMARY KEY / UNIQUE are honored (a PK enables rowid → UPDATE/DELETE);
-- literal DEFAULTs are carried to the SQL Server table.
CREATE TABLE mssql.staging.k (
  id INTEGER PRIMARY KEY, a INTEGER, b INTEGER,
  status VARCHAR DEFAULT 'active', qty INTEGER DEFAULT 0,
  UNIQUE (a, b));

-- ALTER TABLE: rename table/column, add/drop column, change type,
-- toggle NOT NULL, and set/drop a literal DEFAULT.
ALTER TABLE mssql.staging.t ADD COLUMN note VARCHAR;
ALTER TABLE mssql.staging.t ALTER COLUMN id TYPE BIGINT;
ALTER TABLE mssql.staging.t ALTER COLUMN note SET NOT NULL;
ALTER TABLE mssql.staging.t ALTER COLUMN note DROP NOT NULL;
ALTER TABLE mssql.staging.t ALTER COLUMN note SET DEFAULT 'n/a';
ALTER TABLE mssql.staging.t ALTER COLUMN note DROP DEFAULT;
ALTER TABLE mssql.staging.t RENAME COLUMN note TO comment;
ALTER TABLE mssql.staging.t DROP COLUMN comment;
ALTER TABLE mssql.staging.t RENAME TO t_renamed;

DROP TABLE mssql.staging.t_renamed;
DROP SCHEMA mssql.staging;
```

Catalog `INSERT`/`UPDATE`/`DELETE`, `CREATE TABLE AS`, `COPY TO`, and DDL
(`CREATE`/`DROP TABLE`, `CREATE`/`DROP SCHEMA`, `ALTER TABLE`) are supported. All
of them are **provider-agnostic in C++** — the operators only produce Arrow +
table/column identity; the C# backend owns every SQL Server specific (SqlBulkCopy,
parameterized UPDATE/DELETE, type mapping, DDL generation). `CREATE TABLE`
honors `NOT NULL`, `PRIMARY KEY`, `UNIQUE`, and literal `DEFAULT`s (a PK/unique
index gives the table a rowid, so `UPDATE`/`DELETE` work on tables you create);
`ALTER COLUMN … TYPE` preserves the column's nullability, and `ALTER TABLE`
supports `SET`/`DROP NOT NULL` and `SET`/`DROP DEFAULT` (literal). `UPDATE`/
`DELETE` need a primary key or unique index (else use `mssql_net_exec`).
`INSERT … RETURNING` is supported (via `OUTPUT INSERTED.*`); non-literal
(expression) `DEFAULT`s and `CHECK` constraints are upcoming. Note: a `VARCHAR`
column maps to `NVARCHAR(MAX)`, which SQL Server
cannot use as a `PRIMARY KEY`/`UNIQUE` key column — use a fixed-width/indexable
type for keys.

The raw-query table function and a write/exec function are also available:

```sql
SELECT id, name FROM mssql_net_query(
  'Server=...;Database=...;...', 'SELECT id, name FROM dbo.people');

-- arbitrary T-SQL (DDL/DML/EXEC); returns rows affected (0 for DDL/no-row stmts)
SELECT mssql_net_exec('Server=...;Database=...;...',
                      'UPDATE dbo.people SET salary = salary + 1 WHERE id <= 2');

-- The first argument may also be the name of an already-ATTACHed mssql_net
-- catalog — the function reuses that catalog's connection (no need to repeat
-- the connection string):
ATTACH 'Server=...;Database=...;...' AS db (TYPE mssql_net);
SELECT * FROM mssql_net_query('db', 'SELECT id, name FROM dbo.people');
SELECT mssql_net_exec('db', 'UPDATE dbo.people SET salary = salary + 1');

-- After creating/dropping tables out-of-band (e.g. via mssql_net_exec), refresh
-- the cached catalog metadata so the new/removed tables are visible to SQL:
SELECT mssql_net_exec('db', 'CREATE TABLE dbo.t (id INT)');
SELECT mssql_refresh_cache('db');     -- re-discovers schemas/tables for catalog 'db'
SELECT * FROM db.dbo.t;
```

Verified against SQL Server 2022: ATTACH catalog discovery (multi-schema, views),
automatic scans, projection, WHERE filtering, joins; 12+ type mappings (int/decimal/
money/bit→boolean/date/time/datetime2→timestamp/datetimeoffset→timestamptz/
uniqueidentifier→varchar/varbinary→blob/…), and NULLs. The catalog is read-only in
Phase 1 (DML/DDL raise a clear "not supported" error).

`arrownet_test_scan('hello')` (Phase 0) remains as a stub-backed smoke test of the
C++ → CoreCLR → Arrow → DuckDB spine.

Phase 2 (done): `mssql_net_exec`, catalog `INSERT`/`UPDATE`/`DELETE` (rowid from
PK / smallest unique index, scalar + compound), and `CREATE TABLE AS` + `COPY TO`
via a generic Arrow→`SqlBulkCopy` bulk path (C++ provider-agnostic; SQL Server
specifics in C#). Metadata discovery (schemas/tables/columns/rowid) and the table
scan were then moved entirely to C# behind `get_metadata`/`scan_table` ABI calls —
**the C++ extension now contains no T-SQL at all**; every provider SQL string
(`sys.*`, PK/unique-index rowid lookup, `SELECT … WHERE 1=0` type probe, table
scans) lives in `ArrowNet.SqlServer`. Catalog DDL (`CREATE`/`DROP TABLE`,
`CREATE`/`DROP SCHEMA`, `ALTER TABLE`) followed the same split: C++ carries
column identity (a zero-row Arrow schema, NOT NULL encoded as Arrow field
nullability) and the C# backend generates the provider DDL. Next: Airport-style
custom scalar/table/table-in-out functions.

```
┌───────┬─────────┬───────────────────┐
│  id   │  name   │       query       │
├───────┼─────────┼───────────────────┤
│     1 │ alpha   │ hello from duckdb │
│     2 │ beta    │ hello from duckdb │
│     3 │ gamma   │ hello from duckdb │
└───────┴─────────┴───────────────────┘
```

Next: Phase 1 (`ATTACH … (TYPE mssql_net)` + catalog + read-only table scan via
`Microsoft.Data.SqlClient`).

## Layout

```
src/                         C++ DuckDB extension
  include/arrownet/abi.h     C ABI vtable + Arrow C structs (shared contract)
  include/arrownet/clr_host.hpp  CoreCLR bootstrap + vtable wrappers
  include/arrownet/arrow_ingest.hpp  reusable ArrowArrayStream -> DataChunk
  arrownet/clr_host.cpp      hostfxr loader (self-contained, command-line init)
  arrownet/arrow_ingest.cpp  scan loop (ports adbc_scanner pattern)
  mssql_net_extension.cpp    extension entry; arrownet_test_scan + mssql_net_query
  mssql_net_storage.cpp      ATTACH (TYPE mssql_net) storage-extension registration
  catalog/                   read-only catalog (Catalog/SchemaEntry/TableEntry/Transaction)
                             + metadata helpers that call get_metadata/scan_table (all provider
                             SQL — sys.*, rowid discovery, scans — lives in C#; C++ has no T-SQL)
dotnet/ArrowNet.Bridge/      reusable managed bridge (C ABI exports, Arrow export,
                             handle table, IBackend, DbDataReader→Arrow converter, StubBackend)
dotnet/ArrowNet.SqlServer/   SqlClient backend + composition root (published beside the extension)
scripts/publish-managed.ps1  self-contained publish of ArrowNet.SqlServer (+ bridge + runtime)
test/host_smoke/             standalone CLR+Arrow round-trip test (no DuckDB)
CMakeLists.txt, Makefile, extension_config.cmake, vcpkg.json
```

The C++ core (`arrownet/`) and the managed `ArrowNet.Bridge` are **transport- and
backend-agnostic**, intended for reuse by a future Power BI / DAX extension.

## Key technical decisions

- **DuckDB v1.5.3** (new C++ extension API: `Extension::Load(ExtensionLoader&)` +
  `loader.RegisterFunction(...)` + `DUCKDB_CPP_EXTENSION_ENTRY(mssql_net, loader)`).
  Submodules pinned to `duckdb@v1.5.3` + `extension-ci-tools@v1.5.3`.
- **Self-contained .NET 10** runtime shipped beside the extension (no prerequisite
  on the host). The CoreCLR host loads the bundled `hostfxr` and initializes via
  **`hostfxr_initialize_for_dotnet_command_line`** — `initialize_for_runtime_config`
  rejects self-contained components ("Initialization for self-contained components
  is not supported").
- **Arrow C Stream Interface** for all tabular data; the bridge exports via
  `Apache.Arrow.C.CArrowArrayStreamExporter`; the host ingests with DuckDB's
  internal `ArrowTableFunction::PopulateArrowTableType` / `ArrowToDuckDB`.
- The managed bridge finds its files via `ARROWNET_MANAGED_DIR`, else an
  `arrownet/` folder beside the loaded extension binary.

## Build

### Managed bridge

```bash
dotnet build dotnet/ArrowNet.Bridge -c Release
# self-contained publish next to a built extension:
pwsh scripts/publish-managed.ps1 -ExtensionDir build/release/extension/mssql_net
```

### Extension

Requires the submodules (`duckdb@v1.5.3` + `extension-ci-tools@v1.5.3`):

```bash
git submodule update --init --recursive
make                                # builds DuckDB + the extension (POSIX/CI)
pwsh scripts/publish-managed.ps1    # publish the bridge beside the extension
```

On Windows, Phase 0 built directly with CMake + Ninja inside a VS dev environment
(`vcvars64.bat`):

```powershell
cmake -G Ninja -DEXTENSION_STATIC_BUILD=1 `
  -DDUCKDB_EXTENSION_CONFIGS="<repo>/extension_config.cmake" `
  -DDUCKDB_EXPLICIT_PLATFORM=windows_amd64 `
  -DENABLE_EXTENSION_AUTOLOADING=1 -DENABLE_EXTENSION_AUTOINSTALL=1 `
  -DENABLE_UNITTEST_CPP_TESTS=FALSE -DCMAKE_BUILD_TYPE=Release `
  -S <repo>/duckdb -B <repo>/build/release
cmake --build build/release --target mssql_net_loadable_extension duckdb shell
```

Produces `build/release/extension/mssql_net/mssql_net.duckdb_extension` (metadata
footer auto-appended) and the matching `build/release/duckdb.exe`.
`extension_config.cmake` sets `EXTENSION_VERSION` so the build needs no git commit.

### Run the round-trip

```bash
duckdb -unsigned -c "LOAD 'path/to/mssql_net.duckdb_extension';
                     SELECT * FROM arrownet_test_scan('hello');"
```

### Smoke test (CLR + Arrow spine, no DuckDB)

Compile `test/host_smoke/host_smoke.cpp` (only needs `src/include`), set
`ARROWNET_MANAGED_DIR` to a published bridge directory, and run it. It boots
CoreCLR, fills the vtable, executes a query, and reads the exported Arrow stream.
