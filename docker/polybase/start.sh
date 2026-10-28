#!/bin/bash
# The External Execution Service (EES) is what serves PolyBase's ODBC sources on Linux. The package starts it
# through a systemd unit, and a container has no systemd -- so this does what that unit does (ExecStartPre
# generates the TLS certs, ExecStart runs the .NET service as mssql_ees), then hands over to SQL Server.
# Both live under /var/opt/mssql, which is a volume, so they are created at start and not at build time.
set -e
mkdir -p /var/opt/mssql/log/polybase-ees-log /var/opt/mssql/duckdb
chown mssql_ees:mssql /var/opt/mssql/log/polybase-ees-log
chmod 775 /var/opt/mssql/log/polybase-ees-log /var/opt/mssql/duckdb
chown mssql:mssql /var/opt/mssql/duckdb
/opt/mssql/bin/generateeescerts.sh || echo "EES certificates not generated; EES falls back to plain gRPC"
cd /var/opt/mssql/duckdb
runuser -u mssql_ees -- env DOTNET_ROOT=/opt/mssql-ees-dotnet TMPDIR=/var/tmp/dotnettmp \
  /opt/mssql-ees-dotnet/dotnet /opt/mssql/lib/ExternalExecutionService.dll \
  >> /var/opt/mssql/log/polybase-ees-log/ees-stdout.log 2>&1 &
exec runuser -u mssql -- /opt/mssql/bin/launch_sqlservr.sh /opt/mssql/bin/sqlservr
