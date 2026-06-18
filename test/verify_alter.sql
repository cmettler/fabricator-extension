-- Verifies catalog ALTER TABLE: ADD/DROP/RENAME column, ALTER column type, RENAME table.
.echo on

SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF OBJECT_ID(''ddltest.t'',''U'') IS NOT NULL DROP TABLE ddltest.t') AS c1;
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF OBJECT_ID(''ddltest.t2'',''U'') IS NOT NULL DROP TABLE ddltest.t2') AS c2;
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF SCHEMA_ID(''ddltest'') IS NOT NULL DROP SCHEMA ddltest') AS c3;

ATTACH 'Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true' AS mssql (TYPE mssql_net);

CREATE SCHEMA mssql.ddltest;
CREATE TABLE mssql.ddltest.t (id INTEGER NOT NULL, name VARCHAR);
INSERT INTO mssql.ddltest.t VALUES (1, 'a'), (2, 'b');

-- ADD COLUMN
ALTER TABLE mssql.ddltest.t ADD COLUMN amount DECIMAL(10,2);
SELECT * FROM mssql.ddltest.t ORDER BY id;

-- ALTER COLUMN TYPE (id INTEGER -> BIGINT)
ALTER TABLE mssql.ddltest.t ALTER COLUMN id TYPE BIGINT;

-- RENAME COLUMN name -> label
ALTER TABLE mssql.ddltest.t RENAME COLUMN name TO label;
SELECT * FROM mssql.ddltest.t ORDER BY id;

-- DROP COLUMN amount
ALTER TABLE mssql.ddltest.t DROP COLUMN amount;
SELECT * FROM mssql.ddltest.t ORDER BY id;

-- RENAME TABLE t -> t2
ALTER TABLE mssql.ddltest.t RENAME TO t2;
SELECT * FROM mssql.ddltest.t2 ORDER BY id;

-- Final column layout on the server (id should be bigint; columns id,label)
SELECT * FROM mssql_net_query('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'SELECT COLUMN_NAME AS col, DATA_TYPE AS type FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=''ddltest'' AND TABLE_NAME=''t2'' ORDER BY ORDINAL_POSITION');

DROP TABLE mssql.ddltest.t2;
DROP SCHEMA mssql.ddltest;
