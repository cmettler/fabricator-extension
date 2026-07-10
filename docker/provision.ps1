# Provisions the test databases inside the compose-managed SQL Server (idempotent).
# All other test objects (UDFs/TVFs/procs/tables) are created by the verify tests themselves at
# runtime via mssql_net_exec — only the databases need to exist.

$ErrorActionPreference = 'Stop'

$sql = @'
IF DB_ID('ArrowTest') IS NULL CREATE DATABASE ArrowTest;
IF DB_ID('TestDB')   IS NULL CREATE DATABASE TestDB;
'@

Write-Host 'Waiting for SQL Server health...'
for ($i = 0; $i -lt 30; $i++) {
    docker exec mssql-arrownet /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C -Q 'SELECT 1' -b -o /dev/null 2>$null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 5
}
if ($LASTEXITCODE -ne 0) { throw 'SQL Server did not become healthy.' }

docker exec mssql-arrownet /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Arrow_Net_123!' -C -Q $sql -b
if ($LASTEXITCODE -ne 0) { throw "provisioning failed ($LASTEXITCODE)" }
Write-Host 'Databases ArrowTest + TestDB ready.'
