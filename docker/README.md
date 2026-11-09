# Test environment — SQL Server 2025 + RustFS (S3)

The compose setup runs SQL Server and [RustFS](https://github.com/rustfs/rustfs) (Apache-2.0, S3-compatible), so the
Delta providers and SQL Server's native S3 data virtualization (external tables / `OPENROWSET … FORMAT = 'DELTA'`)
can be tested locally. RustFS replaced MinIO on 2026-11-09 (see the end of this file).

## Bring-up

```pwsh
# 1. generate the TLS cert (gitignored; SANs: rustfs, localhost, 127.0.0.1)
pwsh docker/certs/generate-certs.ps1

# 2. start everything (SQL Server 1433, RustFS S3 9000; rustfs-init creates the bucket `fabricator`)
docker compose -f docker/docker-compose.yml up -d

# 3. create the test databases (ArrowTest + TestDB — everything else self-provisions in tests)
pwsh docker/provision.ps1
```

## Fixed test credentials (test-only, deliberately committed)

| what | value |
|---|---|
| SQL Server | `sa` / `Arrow_Net_123!`, port 1433, DBs `ArrowTest` + `TestDB` |
| RustFS | `miniouser` / `miniosecret123` (alphanumeric — SQL's S3 credential requires it; the names are left from MinIO), bucket `fabricator` |
| S3 endpoint (host/DuckDB) | `localhost:9000` (HTTPS, self-signed) |
| S3 endpoint (from SQL Server) | `rustfs:9000` (compose DNS; the cert SAN + CA mount make it trusted) |

## Why TLS

SQL Server's `s3://` connector requires HTTPS (no HTTP bypass exists). RustFS serves TLS from `docker/certs/rustfs/`
(the generated pair under RustFS's file names, `RUSTFS_TLS_PATH`); SQL Server trusts the self-signed cert via the
mount at `/var/opt/mssql/security/ca-certificates/s3.crt` (read once at process start).

- **Rotating the certs:** re-run `generate-certs.ps1`, then `docker compose up -d --force-recreate rustfs` and restart
  SQL Server.
- `rustfs-init` creates the bucket with `rc`, RustFS's own client (`rustfs/rc`), and is re-runnable (`-p`). It runs
  with `--insecure`: rc's TLS stack (rustls) refuses our certificate because it is self-signed with CA:TRUE, a CA
  used as the server's own certificate (`CaUsedAsEndEntity`); curl and SQL Server accept it. A separate CA plus a
  server certificate signed by it would let rc verify.
- ⚠⚠ **Renaming a bind-mounted file under `/var/opt/mssql` CRASHES SQL Server at startup** (measured 2026-11-09).
  The CA mount sits inside the `mssql-data` volume, and Docker leaves an EMPTY placeholder file in the volume for
  every bind-mount target. After the mount moved from `minio.crt` to `s3.crt`, the old empty `minio.crt` stayed in
  `ca-certificates`, and SQL Server failed with *"Failed to install certs; status=0xc0000001"* / *"Failed to load
  LSA"* in a restart loop. Fix: delete the empty file from the volume, e.g.
  `docker run --rm -v mssql-fabricator_mssql-data:/v alpine rm /v/security/ca-certificates/minio.crt`.
  A fresh volume (CI) is not affected.

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
- SQL Server's external data source `s3_ds` in TestDB points at ONE store. The SQL Server suites re-point it to
  `FABRICATOR_S3_SQL_ENDPOINT` when it differs; before 2026-11-09 they kept whatever a previous run left, so a
  switch of the store read the other store's leftover files.

## Test env vars

```bash
export FABRICATOR_S3_ENDPOINT=localhost:9000    # gates test/verify_delta_catalog_s3.test
export FABRICATOR_S3_SQL_ENDPOINT=rustfs:9000   # gates test/verify_mssql_s3_polybase.test (with MSSQL_TESTDB_DSN)
```

## From MinIO to RustFS (2026-11-09)

MinIO stopped distributing a community build: `minio/minio` on Docker Hub and quay.io refuse, and `dl.min.io`
answers 410 Gone. The rig ran Bitnami's frozen 2025.7.23 mirror until RustFS replaced it. Before the switch, RustFS
1.0.4 was measured against everything the rig needs:

- put-if-absent (`If-None-Match: *`), which our S3 commits rely on, is atomic: 20 rounds of 32 concurrent writers,
  exactly one winner each;
- the full service tier is green at the same counts as against MinIO (91 runs, 137756 assertions), PolyBase
  included (`verify_mssql_s3_polybase` 297).

⚠ The lesson from MinIO still applies: a registry can withdraw an image, and a green LOCAL run proves nothing about
that, since it uses the cached image. Only a clean machine pulls, so only CI notices. Both images are pinned to exact
tags (`rustfs/rustfs:1.0.4`, `rustfs/rc:v0.1.37`).
