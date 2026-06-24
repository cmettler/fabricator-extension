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
