-- Verifies the remaining ALTER DDL gaps: SET/DROP NOT NULL and SET/DROP DEFAULT.
.echo on
.mode box

SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF SCHEMA_ID(''ddla'') IS NOT NULL BEGIN DROP TABLE IF EXISTS ddla.t; DROP SCHEMA ddla; END') AS pre;

ATTACH 'Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true' AS mssql (TYPE mssql_net);
CREATE SCHEMA mssql.ddla;
CREATE TABLE mssql.ddla.t (id INTEGER NOT NULL, name VARCHAR, qty INTEGER);

-- SET NOT NULL (name) + DROP NOT NULL (id) -- restates the current type.
ALTER TABLE mssql.ddla.t ALTER COLUMN name SET NOT NULL;
ALTER TABLE mssql.ddla.t ALTER COLUMN id DROP NOT NULL;
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, IS_NULLABLE AS nul FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddla'' AND TABLE_NAME=''t'' ORDER BY ORDINAL_POSITION');

-- SET DEFAULT (literal) on two columns.
ALTER TABLE mssql.ddla.t ALTER COLUMN qty SET DEFAULT 7;
ALTER TABLE mssql.ddla.t ALTER COLUMN name SET DEFAULT 'none';
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, COLUMN_DEFAULT AS def FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddla'' AND TABLE_NAME=''t'' ORDER BY ORDINAL_POSITION');

-- A raw insert omitting the defaulted columns picks up the defaults.
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'INSERT INTO ddla.t (id) VALUES (1)') AS inserted;
SELECT * FROM mssql.ddla.t;

-- Replace an existing default (drops the old constraint first), then drop it.
ALTER TABLE mssql.ddla.t ALTER COLUMN qty SET DEFAULT 9;
ALTER TABLE mssql.ddla.t ALTER COLUMN qty DROP DEFAULT;
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, COLUMN_DEFAULT AS def FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddla'' AND TABLE_NAME=''t'' ORDER BY ORDINAL_POSITION');

DROP TABLE mssql.ddla.t;
DROP SCHEMA mssql.ddla;
