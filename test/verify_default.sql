-- Verifies literal column DEFAULTs on CREATE TABLE (CHECK omitted by design).
.echo on

SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF SCHEMA_ID(''ddld'') IS NOT NULL BEGIN DROP TABLE IF EXISTS ddld.t; DROP SCHEMA ddld; END') AS pre;

ATTACH 'Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true' AS mssql (TYPE mssql_net);
CREATE SCHEMA mssql.ddld;

-- Literal defaults across types, including NULL and an embedded apostrophe.
CREATE TABLE mssql.ddld.t (
  id     INTEGER NOT NULL,
  status VARCHAR DEFAULT 'active',
  qty    INTEGER DEFAULT 0,
  price  DECIMAL(10,2) DEFAULT 9.99,
  flag   BOOLEAN DEFAULT true,
  note   VARCHAR DEFAULT NULL,
  label  VARCHAR DEFAULT 'O''Brien'
);

-- The DEFAULT constraints should be present on the server.
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, COLUMN_DEFAULT AS def FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddld'' AND TABLE_NAME=''t'' ORDER BY ORDINAL_POSITION');

-- A raw insert that omits the defaulted columns must pick up the server defaults.
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'INSERT INTO ddld.t (id) VALUES (1)') AS inserted;
SELECT * FROM mssql.ddld.t;

DROP TABLE mssql.ddld.t;
DROP SCHEMA mssql.ddld;
