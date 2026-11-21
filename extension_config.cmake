# DuckDB extension build configuration for fabricator.
# EXTENSION_VERSION is set explicitly so the build does not require a git commit
# in this repo (DuckDB otherwise derives the version via `git describe`).
# It is READ FROM ./VERSION rather than written twice: this value and CMakeLists.txt's
# FABRICATOR_EXTENSION_VERSION must agree (one becomes the artifact footer, the other what
# fabricator_version() returns), and they are in different CMake scopes, so each reads the file itself.
file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/VERSION" FABRICATOR_EXTENSION_VERSION LIMIT_COUNT 1)
duckdb_extension_load(fabricator
    SOURCE_DIR ${CMAKE_CURRENT_LIST_DIR}
    EXTENSION_VERSION "${FABRICATOR_EXTENSION_VERSION}"
    LOAD_TESTS
)
# ⚠ DuckDB 2.0 REMOVED `DONT_LINK` and INVERTED the default: `duckdb_extension_load` now only BUILDS an
# extension, and static linking into duckdb.exe / unittest.exe is opt-in per extension. Without this call the
# build is green, the loadable is produced, and every `.test` file silently SKIPS with `require fabricator` —
# which reads as a missing managed bridge, not as a link-configuration change. `json`/`icu`/`parquet` keep
# working through it because DuckDB's own extension/extension_config.cmake lists them.
duckdb_extension_statically_link(fabricator)

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

# TPC-H / TPC-DS generators and queries, for the distributed benchmark suites (test/verify_tpch_mssql.test,
# test/verify_tpcds_mssql.test). Linked in so CI needs no download: a test build reports a dev version, for which the
# extension repository has nothing, and a `require` that cannot load SKIPS the suite.
duckdb_extension_load(tpch)
duckdb_extension_statically_link(tpch)
duckdb_extension_load(tpcds)
duckdb_extension_statically_link(tpcds)

# And `httpfs` — s3:// (MinIO) targets for the Delta providers (verify_delta_catalog_s3 +
# verify_mssql_s3_polybase). Out-of-tree since DuckDB 1.3 — pinned to the SAME sha DuckDB v2.0-cyanoptera's own
# CI uses (duckdb/.github/config/extensions/httpfs.cmake at v2.0.1).
# Requires OpenSSL + curl: configure with the vcpkg toolchain +
# `-DVCPKG_TARGET_TRIPLET=x64-windows-static` (static CRT, matching /MT) after
# `vcpkg install openssl:x64-windows-static curl:x64-windows-static`.
duckdb_extension_load(httpfs
    GIT_URL https://github.com/duckdb/duckdb-httpfs
    GIT_TAG f71cfdb4bff85b9e15b1f3b842d9aa395aff0167
)
duckdb_extension_statically_link(httpfs)

# And `ducklake` — ducklake_delta_sync (docs/ducklake-catalog.md §11) reads a DuckLake through DuckDB, and its
# gates need a lake in the test binaries. Pinned to the SAME sha DuckDB v2.0-cyanoptera's own CI uses
# (duckdb/.github/config/extensions/ducklake.cmake at v2.0.1; the DuckLake work up to 2026-11 was measured on the
# v2.0.0 pin 417a9117f5). Statically linked, so a suite needs `require ducklake` and no INSTALL — the hermetic
# tier forbids network.
# WITH DuckDB's APPLY_PATCHES since v2.0.1, like DuckDB's own build: two of its patches change product code
# (0029: sorted compaction relies on 2.0.1's order-preserving file rotation instead of DuckLake's own workaround;
# 0032: a locked SQLite COMMIT is recognised by the new error text). Up to v2.0.0 every patch touched only
# DuckLake's tests and we built without them. Applying them needs GNU `patch` on PATH: scripts/build_v2_all.bat
# appends Git for Windows' usr\bin for that.
duckdb_extension_load(ducklake
    GIT_URL https://github.com/duckdb/ducklake
    GIT_TAG 08089b16dc8a18ce04abb6efd87e25cb4d64803a
    APPLY_PATCHES
)
duckdb_extension_statically_link(ducklake)
