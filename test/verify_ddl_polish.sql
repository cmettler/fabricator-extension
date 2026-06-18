-- Verifies DDL polish: PK/UNIQUE constraints on CREATE (enabling rowid) and
-- ALTER COLUMN TYPE preserving nullability.
.echo on

-- Pre-clean (raw T-SQL, before ATTACH).
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF SCHEMA_ID(''ddlp'') IS NOT NULL BEGIN DROP TABLE IF EXISTS ddlp.pk1; DROP TABLE IF EXISTS ddlp.pk2; DROP TABLE IF EXISTS ddlp.uq1; DROP TABLE IF EXISTS ddlp.nn; DROP SCHEMA ddlp; END') AS pre;

ATTACH 'Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true' AS mssql (TYPE mssql_net);
CREATE SCHEMA mssql.ddlp;

-- 1) Single-column PRIMARY KEY -> rowid -> UPDATE / DELETE through the catalog.
CREATE TABLE mssql.ddlp.pk1 (id INTEGER PRIMARY KEY, name VARCHAR);
INSERT INTO mssql.ddlp.pk1 VALUES (1, 'a'), (2, 'b'), (3, 'c');
-- PK column must be NOT NULL on the server.
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, IS_NULLABLE AS nul FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddlp'' AND TABLE_NAME=''pk1'' ORDER BY ORDINAL_POSITION');
SELECT rowid, id, name FROM mssql.ddlp.pk1 ORDER BY id;
UPDATE mssql.ddlp.pk1 SET name = 'B' WHERE id = 2;
DELETE FROM mssql.ddlp.pk1 WHERE id = 3;
SELECT * FROM mssql.ddlp.pk1 ORDER BY id;

-- 2) Compound PRIMARY KEY -> compound rowid.
CREATE TABLE mssql.ddlp.pk2 (a INTEGER, b INTEGER, v VARCHAR, PRIMARY KEY (a, b));
INSERT INTO mssql.ddlp.pk2 VALUES (1, 1, 'x'), (1, 2, 'y');
UPDATE mssql.ddlp.pk2 SET v = 'Y' WHERE a = 1 AND b = 2;
SELECT * FROM mssql.ddlp.pk2 ORDER BY a, b;

-- 3) UNIQUE constraint -> rowid via the unique index (no PK present).
-- (UNIQUE key column uses an indexable type; string columns map to NVARCHAR(MAX),
-- which SQL Server cannot use as an index key.)
CREATE TABLE mssql.ddlp.uq1 (id INTEGER, code INTEGER UNIQUE);
INSERT INTO mssql.ddlp.uq1 VALUES (1, 10), (2, 20);
SELECT count(*) AS unique_indexes FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT 1 AS x FROM sys.indexes WHERE object_id = OBJECT_ID(''ddlp.uq1'') AND is_unique = 1');
UPDATE mssql.ddlp.uq1 SET id = 99 WHERE code = 20;
SELECT * FROM mssql.ddlp.uq1 ORDER BY code;

-- 4) ALTER COLUMN TYPE preserves NOT NULL.
CREATE TABLE mssql.ddlp.nn (id INTEGER NOT NULL, name VARCHAR);
ALTER TABLE mssql.ddlp.nn ALTER COLUMN id TYPE BIGINT;
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, DATA_TYPE AS typ, IS_NULLABLE AS nul FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddlp'' AND TABLE_NAME=''nn'' ORDER BY ORDINAL_POSITION');

-- cleanup
DROP TABLE mssql.ddlp.pk1;
DROP TABLE mssql.ddlp.pk2;
DROP TABLE mssql.ddlp.uq1;
DROP TABLE mssql.ddlp.nn;
DROP SCHEMA mssql.ddlp;
