# Test environment — SQL Server 2025 + MinIO (S3)

The compose setup replaces the old ad-hoc `mssql-fabricator` container and adds MinIO so the Delta
providers and SQL Server's native S3 data virtualization (external tables / `OPENROWSET … FORMAT =
'DELTA'`) can be tested locally.

## Bring-up

```pwsh
# 0. one-time: remove the old ad-hoc container if it exists
docker stop mssql-fabricator; docker rm mssql-fabricator

# 1. generate the MinIO TLS cert (gitignored; SANs: minio, localhost, 127.0.0.1)
pwsh docker/certs/generate-certs.ps1

# 2. start everything (SQL Server 1433, MinIO S3 9000 / console 9001, bucket `fabricator`)
docker compose -f docker/docker-compose.yml up -d

# 3. create the test databases (ArrowTest + TestDB — everything else self-provisions in tests)
pwsh docker/provision.ps1
```

## Fixed test credentials (test-only, deliberately committed)

| what | value |
|---|---|
| SQL Server | `sa` / `Arrow_Net_123!`, port 1433, DBs `ArrowTest` + `TestDB` |
| MinIO | `miniouser` / `miniosecret123` (alphanumeric — SQL's S3 credential requires it), bucket `fabricator` |
| MinIO endpoint (host/DuckDB) | `localhost:9000` (HTTPS, self-signed) |
| MinIO endpoint (from SQL Server) | `minio:9000` (compose DNS; the cert SAN + CA mount make it trusted) |

## Why TLS

SQL Server's `s3://` connector requires HTTPS (no HTTP bypass exists). MinIO auto-enables TLS from
`docker/certs/{public.crt,private.key}` mounted at `/root/.minio/certs`; SQL Server trusts the
self-signed cert via the mount at `/var/opt/mssql/security/ca-certificates/minio.crt` (read once at
process start — restart the `sqlserver` service after rotating certs).

## SQL Server 2025 + S3 notes

- File queries (CSV/Parquet/**DELTA**) over S3 are a **native engine capability** in 2025 — no
  PolyBase package, no `sp_configure 'polybase enabled'`, no trace flag 13702 (those apply to 2022).
  A commented Dockerfile for the optional `mssql-server-polybase` package (RDBMS connectors only)
  is in `docker-compose.yml`.
- The DELTA reader supports **Delta protocol 1.0 only**: tables with deletion vectors (reader v3) or
  column mapping are NOT readable — write PolyBase-facing tables with
  `deletion_vectors false, column_mapping 'none'`.
- Partitioned delta: the partition column reads NULL through `CREATE EXTERNAL TABLE` but correctly
  through `OPENROWSET` (documented MS limitation).

## Test env vars

```bash
export FABRICATOR_S3_ENDPOINT=localhost:9000   # gates test/verify_delta_catalog_s3.test
export FABRICATOR_S3_SQL_ENDPOINT=minio:9000   # gates test/verify_mssql_s3_polybase.test (with MSSQL_TESTDB_DSN)
```
