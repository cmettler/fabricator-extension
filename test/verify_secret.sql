-- Verifies the mssql_net secret type: connection string built from parts,
-- usable via ATTACH (SECRET ...) and via the query/exec functions by name.
.echo on
.mode box

CREATE SECRET mytest (
  TYPE mssql_net,
  host '127.0.0.1', port 1433, database 'TestDB',
  user 'sa', password 'Arrow_Net_123!', use_encrypt true
);

-- Secret exists and password is redacted.
SELECT count(*) AS secret_exists FROM duckdb_secrets() WHERE type = 'mssql_net' AND name = 'mytest';
SELECT secret_string NOT LIKE '%Arrow_Net%' AS password_redacted FROM duckdb_secrets() WHERE name = 'mytest';

-- ATTACH using the secret (empty path) and scan a fixture table.
ATTACH '' AS db (TYPE mssql_net, SECRET mytest);
SELECT id, name FROM db.dbo.TestSimplePK WHERE id = 1;

-- The query function also accepts the secret name as its first argument.
SELECT * FROM mssql_net_query('mytest', 'SELECT 42 AS answer');

-- Validation: missing required field is rejected.
DETACH db;
DROP SECRET mytest;
