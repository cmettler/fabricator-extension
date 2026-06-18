-- Verifies catalog DDL (CREATE/DROP TABLE + CREATE/DROP SCHEMA) end to end.
.echo on

-- Pre-clean any leftovers from a prior run (raw T-SQL, before ATTACH).
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF OBJECT_ID(''ddltest.t'',''U'') IS NOT NULL DROP TABLE ddltest.t') AS pre_drop_table;
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF SCHEMA_ID(''ddltest'') IS NOT NULL DROP SCHEMA ddltest') AS pre_drop_schema;

ATTACH 'Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true' AS mssql (TYPE mssql_net);

-- CREATE SCHEMA
CREATE SCHEMA mssql.ddltest;

-- CREATE TABLE (id NOT NULL; name/amount nullable)
CREATE TABLE mssql.ddltest.t (id INTEGER NOT NULL, name VARCHAR, amount DECIMAL(10,2));

-- Confirm column definitions on SQL Server (nullability + types) via raw query.
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, IS_NULLABLE AS nullable, DATA_TYPE AS type FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddltest'' AND TABLE_NAME=''t'' ORDER BY ORDINAL_POSITION');

-- Writable through the catalog
INSERT INTO mssql.ddltest.t VALUES (1, 'Alice', 10.50), (2, 'Bob', 20.00);
SELECT * FROM mssql.ddltest.t ORDER BY id;

-- CREATE TABLE IF NOT EXISTS is a no-op when present (different cols ignored)
CREATE TABLE IF NOT EXISTS mssql.ddltest.t (id INTEGER NOT NULL);
SELECT count(*) AS n_after_ifnotexists FROM mssql.ddltest.t;

-- DROP TABLE
DROP TABLE mssql.ddltest.t;
SELECT count(*) AS table_remaining FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT 1 AS x FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=''ddltest'' AND TABLE_NAME=''t''');

-- DROP SCHEMA
DROP SCHEMA mssql.ddltest;
SELECT count(*) AS schema_remaining FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT 1 AS x FROM sys.schemas WHERE name=''ddltest''');
