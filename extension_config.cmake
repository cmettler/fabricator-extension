# DuckDB extension build configuration for mssql_net.
# EXTENSION_VERSION is set explicitly so the build does not require a git commit
# in this repo (DuckDB otherwise derives the version via `git describe`).
duckdb_extension_load(mssql_net
    SOURCE_DIR ${CMAKE_CURRENT_LIST_DIR}
    EXTENSION_VERSION "0.0.1"
    LOAD_TESTS
)
