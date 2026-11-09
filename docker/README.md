# Test environment — SQL Server 2025 + MinIO (S3)

The compose setup replaces the old ad-hoc `mssql-fabricator` container and adds MinIO so the Delta
providers and SQL Server's native S3 data virtualization (external tables / `OPENROWSET … FORMAT =
'DELTA'`) can be tested locally.

## Bring-up

```pwsh
# 0. one-time: remove the old ad-hoc container if it exists
docker stop mssql-fabricator; docker rm mssql-fabricator

# 1. generate the TLS cert (gitignored; SANs: minio, rustfs, localhost, 127.0.0.1)
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
`docker/certs/{public.crt,private.key}` mounted at **`/certs`** (the Bitnami image's certs dir — it was
`/root/.minio/certs` on the official image, see the compose comment on why we moved); SQL Server trusts
the self-signed cert via the mount at `/var/opt/mssql/security/ca-certificates/minio.crt` (read once at
process start — restart the `sqlserver` service after rotating certs).

⚠ `docker/certs/CAs/public.crt` is a third file and it is **load-bearing, not a duplicate**: the image's
`setup.sh` copies that folder into the bundled `mc` client's trust store, and `mc` is what
`MINIO_DEFAULT_BUCKETS` uses to create the `fabricator` bucket — over HTTPS, against this same cert.
Without it the container starts **healthy** and the bucket is silently absent. `generate-certs.ps1`
writes it; the certs are gitignored, so nothing else will.

⚠⚠ **After regenerating the certs, RECREATE the MinIO container — a restart is not enough** (measured
2026-11-09). `setup.sh` copies `/certs/CAs` into `mc`'s trust store only when no copy is there yet, and a
restart keeps the container's filesystem, so `mc` keeps trusting the OLD certificate: setup fails with
*"Failed to add temporary MinIO server"* (`x509: failed to verify certificate`) and the container restarts
every ~75 s — while reporting **healthy** and answering requests for most of each cycle, so suites pass or
fail by timing. Use `docker compose up -d --force-recreate --no-deps minio` (the data is in the volume).
SQL Server only needs a restart: it reads the CA at process start.

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

## RustFS (trial, profile `rustfs`)

[RustFS](https://github.com/rustfs/rustfs) (Apache-2.0, maintained) is a candidate replacement for MinIO, whose
community image is no longer published (we run Bitnami's frozen 2025.7.23 mirror). Same keys, same bucket name,
so every S3 suite runs against it by changing two env vars:

```pwsh
docker compose -f docker/docker-compose.yml --profile rustfs up -d rustfs rustfs-init
```

```bash
export FABRICATOR_S3_ENDPOINT=localhost:9100   # DuckDB on the host
export FABRICATOR_S3_SQL_ENDPOINT=rustfs:9000  # SQL Server inside the compose network
```

- `rustfs-init` creates the bucket with `rc`, RustFS's own client (`rustfs/rc`), and is re-runnable. It runs with
  `--insecure`: rc's TLS stack (rustls) refuses our certificate because it is self-signed with CA:TRUE, a CA used
  as the server's own certificate (`CaUsedAsEndEntity`); curl, SQL Server and MinIO's `mc` accept it.
- RustFS reads the certificate from `docker/certs/rustfs/` under its own file names; `generate-certs.ps1` writes
  that copy. The `rustfs` SAN is what lets SQL Server reach it as `rustfs:9000`.
- **Measured 2026-11-09 on RustFS 1.0.4, all green at full counts:** `verify_delta_catalog_s3` (222, both engines),
  `verify_mssql_s3_polybase` (297), `verify_mssql_external_delete` (45), `verify_mssql_external_txn` (127).
  Put-if-absent (`If-None-Match: *`), which our S3 commits rely on, is atomic: 20 rounds of 32 concurrent
  writers, exactly one winner each (2026-11-08).
- ⚠ SQL Server's external data source `s3_ds` in TestDB points at ONE store at a time. The SQL Server suites
  re-point it to `FABRICATOR_S3_SQL_ENDPOINT` when it differs; before 2026-11-09 they kept whatever a previous run
  left, so a switch between MinIO and RustFS read the other store's leftover files.
