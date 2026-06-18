-- Verifies the metadata-discovery-in-C# refactor end to end.
-- Connection string for the Docker SQL Server 2022 test instance.
.echo on

-- Fresh table with a primary key (exercises rowid discovery via get_metadata).
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'IF OBJECT_ID(''dbo.meta_test'',''U'') IS NOT NULL DROP TABLE dbo.meta_test') AS drop_result;
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'CREATE TABLE dbo.meta_test (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50), salary DECIMAL(10,2))') AS create_result;
SELECT mssql_net_exec('Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true',
  'INSERT INTO dbo.meta_test (id,name,salary) VALUES (1,''Alice'',100.00),(2,''Bob'',200.00),(3,''Carol'',300.00)') AS insert_result;

-- ATTACH -> catalog discovery (DiscoverSchemas/DiscoverTables via get_metadata)
ATTACH 'Server=127.0.0.1,1433;Database=ArrowTest;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true' AS mssql (TYPE mssql_net);

-- scan_table + FetchTableColumns (column types from COLUMNS metadata schema)
SELECT * FROM mssql.dbo.meta_test ORDER BY id;

-- aggregate
SELECT count(*) AS n FROM mssql.dbo.meta_test;

-- rowid discovery (PK -> get_metadata ROWID)
SELECT rowid, id, name FROM mssql.dbo.meta_test ORDER BY id;

-- UPDATE via rowid
UPDATE mssql.dbo.meta_test SET salary = salary * 2 WHERE id = 2;

-- DELETE via rowid
DELETE FROM mssql.dbo.meta_test WHERE id = 3;

-- final state: Alice 100, Bob 400
SELECT * FROM mssql.dbo.meta_test ORDER BY id;
