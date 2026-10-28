#!/usr/bin/env bash
# Stage the ODBC side of the PolyBase/DuckLake experiment into docker/polybase/stage, which the `sqlpoly`
# service mounts at /opt/duckodbc (docs/ducklake-catalog.md §12). Run in WSL after building:
#   - the DuckDB ODBC driver with the -Wl,-Bsymbolic fix   (DRIVER, default ~/duckdb-odbc-src/...)
#   - our linux loadable with a v2.0.0 footer               (LOADABLE, default ~/sqlext2/build/odbc/...)
#   - a SELF-CONTAINED linux-x64 managed payload            (PAYLOAD, published from Windows)
# The payload is self-contained so it does not depend on whatever .NET the PolyBase service brings.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
STAGE=$HERE/stage
DRIVER=${DRIVER:-$HOME/duckdb-odbc-src/build/release/libduckdb_odbc.so}
LOADABLE=${LOADABLE:-$HOME/sqlext2/build/odbc/extension/fabricator/fabricator.duckdb_extension}
PAYLOAD=${PAYLOAD:-/mnt/d/repos/fabricator-extension/scratchpad/linuxpub-sc/extension/fabricator/fabricator}
# DuckLake metadata lives in the SAME SQL Server (inside the container it is localhost:1433); the data on MinIO,
# reached by service name. Test-only credentials, the same as the rest of the rig.
META="Server=localhost,1433;Database=DLPoly;User Id=sa;Password=Arrow_Net_123!;TrustServerCertificate=true;Encrypt=true"

rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$DRIVER" "$STAGE/libduckdb_odbc.so"
cp "$LOADABLE" "$STAGE/fabricator.duckdb_extension"
cp -r "$PAYLOAD" "$STAGE/fabricator"

cat > "$STAGE/odbcinst.ini" <<EOF
[DuckDB]
Description = DuckDB ODBC driver (rebuilt with -Wl,-Bsymbolic, see docs/ducklake-catalog.md §12)
Driver = /opt/duckodbc/libduckdb_odbc.so
EOF

# session_init_sql_file is accepted ONLY from a DSN, never from a connection string.
cat > "$STAGE/odbc.ini" <<EOF
[DuckLake]
Driver = DuckDB
allow_unsigned_extensions = true
extension_directory = /var/opt/mssql/duckdb/extensions
home_directory = /var/opt/mssql/duckdb
session_init_sql_file = /opt/duckodbc/init.sql
EOF

cat > "$STAGE/init.sql" <<EOF
LOAD '/opt/duckodbc/fabricator.duckdb_extension';
INSTALL httpfs;
LOAD httpfs;
INSTALL ducklake;
LOAD ducklake;
-- MinIO's certificate is self-signed (docker/certs); the test rig does not verify it, same as the suites
SET GLOBAL enable_curl_server_cert_verification = false;
CREATE SECRET minio_s3 (TYPE s3, KEY_ID 'miniouser', SECRET 'miniosecret123', ENDPOINT 'minio:9000',
                        URL_STYLE 'path', REGION 'us-east-1', USE_SSL true);
-- PolyBase always passes the FIRST part of an external table's LOCATION as Database=, and the driver opens that
-- as a database FILE (relative to EES's working directory, see start.sh). So LOCATION 'lake.main.t' leaves an
-- empty file database named "lake" as the default; move off it, detach it and attach the lake under its name.
ATTACH ':memory:' AS polybase_home;
USE polybase_home;
DETACH DATABASE IF EXISTS lake;
ATTACH 'ducklake:fabricator:$META' AS lake (DATA_PATH 's3://fabricator/odbc-dl/', METADATA_SCHEMA 'dbo',
                                           META_HIDDEN_ROWID true);
/* DUCKDB_CONNECTION_INIT_BELOW_MARKER */
USE lake;
EOF

ls -la "$STAGE"
