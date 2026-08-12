# DuckDB extension build configuration for fabricator.
# EXTENSION_VERSION is set explicitly so the build does not require a git commit
# in this repo (DuckDB otherwise derives the version via `git describe`).
duckdb_extension_load(fabricator
    SOURCE_DIR ${CMAKE_CURRENT_LIST_DIR}
    EXTENSION_VERSION "0.0.4"
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

# And `parquet` — the native-read path (fabricator_delta_native_scan, docs/multifile-delta.md Phase A) runs
# `read_parquet([...])` on the host engine to read Delta data files with DuckDB's native reader. The shell
# ships parquet, but the test binaries need it linked (v0.0.1 can't autoload it).
duckdb_extension_load(parquet)

# And `httpfs` — s3:// (MinIO) targets for the Delta providers (verify_delta_catalog_s3 +
# verify_mssql_s3_polybase). Out-of-tree since DuckDB 1.3 — pinned to the SAME sha DuckDB v1.5.5's own
# CI uses (duckdb/.github/config/extensions/httpfs.cmake; v1.5.5's tip commit IS the httpfs bump).
# Requires OpenSSL + curl: configure with the vcpkg toolchain +
# `-DVCPKG_TARGET_TRIPLET=x64-windows-static` (static CRT, matching /MT) after
# `vcpkg install openssl:x64-windows-static curl:x64-windows-static`.
duckdb_extension_load(httpfs
    GIT_URL https://github.com/duckdb/duckdb-httpfs
    GIT_TAG 827222fb45a043a7a852d1f7aae46901492a3cda
)
