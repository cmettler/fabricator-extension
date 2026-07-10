# DuckDB extension build configuration for mssql_net.
# EXTENSION_VERSION is set explicitly so the build does not require a git commit
# in this repo (DuckDB otherwise derives the version via `git describe`).
duckdb_extension_load(mssql_net
    SOURCE_DIR ${CMAKE_CURRENT_LIST_DIR}
    EXTENSION_VERSION "0.0.1"
    LOAD_TESTS
)

# Statically build + link the core `json` extension into the test binaries so the DuckDB `JSON` type is
# available. This build reports version v0.0.1, so `json` can't be autoloaded from the extension repo
# (404); static linkage compiles it from the pinned duckdb submodule, matching the engine exactly.
duckdb_extension_load(json)

# Likewise `icu` — provides the `TimeZone` setting + timezone-aware timestamp ops needed to validate the
# TIMESTAMPTZ <-> SQL Server datetime/datetimeoffset value conversions (warehouse-support §3.1) under a
# non-UTC session zone. Same v0.0.1 autoload problem -> link it statically.
duckdb_extension_load(icu)

# And `parquet` — the native-read path (arrownet_delta_native_scan, docs/multifile-delta.md Phase A) runs
# `read_parquet([...])` on the host engine to read Delta data files with DuckDB's native reader. The shell
# ships parquet, but the test binaries need it linked (v0.0.1 can't autoload it).
duckdb_extension_load(parquet)

# And `httpfs` — s3:// (MinIO) targets for the Delta providers (verify_delta_catalog_s3 +
# verify_mssql_s3_polybase). Out-of-tree since DuckDB 1.3 — pinned to the SAME sha DuckDB v1.5.4's own
# CI uses (duckdb/.github/config/extensions/httpfs.cmake). Requires OpenSSL + curl: configure with the
# vcpkg toolchain + `-DVCPKG_TARGET_TRIPLET=x64-windows-static` (static CRT, matching /MT) after
# `vcpkg install openssl:x64-windows-static curl:x64-windows-static`.
duckdb_extension_load(httpfs
    GIT_URL https://github.com/duckdb/duckdb-httpfs
    GIT_TAG c3f215ab360f04dc3d3d5305fa81849c0121f111
)
