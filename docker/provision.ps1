# Provisions the test databases inside the compose-managed SQL Server (idempotent).
# All other test objects (UDFs/TVFs/procs/tables) are created by the verify tests themselves at
# runtime via fabricator_exec — with ONE exception: the shared READ-ONLY fixture dbo.TestSimplePK,
# which several pushdown/statistics suites read but none creates (verify_cardinality,
# verify_catalog_filter, verify_column_ndv, verify_filter/limit/orderby/projection_pushdown).

$ErrorActionPreference = 'Stop'

$sql = @'
IF DB_ID('ArrowTest') IS NULL CREATE DATABASE ArrowTest;
IF DB_ID('TestDB')   IS NULL CREATE DATABASE TestDB;
-- BinCollTest reproduces Fabric Warehouse's actual default collation: binary AND UTF-8, so string
-- ORDER BY sorts bytewise (matching DuckDB, which is what lets the optimizer push string ORDER BY +
-- LIMIT) and VARCHAR still holds full Unicode. verify_collation_pushdown is gated on
-- MSSQL_BINCOLL_DSN and does NOT self-provision, so without this database that suite can only ever
-- skip — which is exactly the silent-no-coverage shape CI is meant to prevent.
IF DB_ID('BinCollTest') IS NULL CREATE DATABASE BinCollTest COLLATE Latin1_General_100_BIN2_UTF8;
-- ALLOW_SNAPSHOT_ISOLATION is a PREREQUISITE of `mssql_materialize=false`, which keeps a scan of the
-- catalog being written STREAMING by routing it to a pooled connection at SNAPSHOT isolation instead of
-- buffering it. Sessions still have to opt in (SET TRANSACTION ISOLATION LEVEL SNAPSHOT), so this changes
-- nothing for any other suite — it only makes the option reachable.
--
-- ⚠ WITHOUT IT that path raises Msg 3952 ("snapshot isolation is not allowed in this database"). That is
-- the SAFE failure and is deliberate: the alternative, silently falling back to READ COMMITTED, was
-- MEASURED to HANG — a pooled streaming reader blocks forever on the pinned writer's locks, with no error
-- and no timeout. So the prerequisite is never inferred or worked around.
--
-- ⚠ NOT READ_COMMITTED_SNAPSHOT: that one is database-wide and would redefine READ COMMITTED for every
-- other suite. ALLOW_SNAPSHOT_ISOLATION is opt-in per session, which is why it is the safe one to provision.
ALTER DATABASE TestDB SET ALLOW_SNAPSHOT_ISOLATION ON;
'@

# The collation fixture, verbatim from the header of test/verify_collation_pushdown.test.
$bincoll = @'
IF OBJECT_ID('dbo.names') IS NULL
BEGIN
    CREATE TABLE dbo.names (id INT NOT NULL PRIMARY KEY, name VARCHAR(50) NOT NULL);
    INSERT INTO dbo.names VALUES (1,'banana'),(2,'Apple'),(3,'cherry'),(4,'apple'),(5,'Banana');
END
'@

# The [value] column is needed by verify_column_ndv, which binds `SELECT id, name, value` to make the
# per-column NDV fetch happen; without it that suite fails on a missing column. The other consumers
# name id/name explicitly, and verify_cardinality's `SELECT *` is a bare `statement ok` with no row
# assertions, so widening the fixture is safe for all of them. Bracketed because VALUE is a T-SQL
# keyword. The ELSE branch matters as much as the CREATE: this table already exists in long-lived dev
# databases, so the column has to be added there too rather than only in a fresh volume.
$fixtures = @'
-- BOTH data statements run through EXEC, and neither is optional. The whole batch is BOUND before any
-- of it executes, and binding ignores the IF, so each statement is validated against the schema as it
-- is NOW: the UPDATE naming a column the ALTER has not added yet fails "Invalid column name 'value'",
-- and the 3-value INSERT fails Msg 213 "number of supplied values does not match table definition"
-- against a table that is still 2 columns wide. Deferring both to runtime is the standard fix when a
-- GO separator is not available (this is one -Q batch).
IF OBJECT_ID('dbo.TestSimplePK') IS NULL
BEGIN
    CREATE TABLE dbo.TestSimplePK (id INT NOT NULL PRIMARY KEY, name NVARCHAR(100), [value] INT NULL);
    EXEC(N'INSERT INTO dbo.TestSimplePK VALUES (1, N''First Record'', 10), (2, N''Second Record'', 20), (3, N''Third Record'', 30)');
END
ELSE IF COL_LENGTH('dbo.TestSimplePK', 'value') IS NULL
BEGIN
    ALTER TABLE dbo.TestSimplePK ADD [value] INT NULL;
    EXEC(N'UPDATE dbo.TestSimplePK SET [value] = id * 10');
END
'@

Write-Host 'Waiting for SQL Server health...'
for ($i = 0; $i -lt 30; $i++) {
    docker exec mssql-fabricator /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C -Q 'SELECT 1' -b -o /dev/null 2>$null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 5
}
if ($LASTEXITCODE -ne 0) { throw 'SQL Server did not become healthy.' }

docker exec mssql-fabricator /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C -Q $sql -b
if ($LASTEXITCODE -ne 0) { throw "provisioning failed ($LASTEXITCODE)" }

docker exec mssql-fabricator /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C -d TestDB -Q $fixtures -b
if ($LASTEXITCODE -ne 0) { throw "fixture provisioning failed ($LASTEXITCODE)" }

docker exec mssql-fabricator /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C -d BinCollTest -Q $bincoll -b
if ($LASTEXITCODE -ne 0) { throw "BinCollTest fixture provisioning failed ($LASTEXITCODE)" }

Write-Host 'Databases ArrowTest + TestDB (+ dbo.TestSimplePK) + BinCollTest (+ dbo.names) ready.'
