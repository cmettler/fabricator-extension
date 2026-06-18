cmake_minimum_required(VERSION 3.21)

# Extension name — must match the DUCKDB_CPP_EXTENSION_ENTRY(...) symbol.
set(TARGET_NAME mssql_net)
set(EXTENSION_NAME ${TARGET_NAME}_extension)
set(LOADABLE_EXTENSION_NAME ${TARGET_NAME}_loadable_extension)

set(MSSQL_NET_EXTENSION_VERSION "0.0.1")

project(${TARGET_NAME} CXX)

# DuckDB controls the C++ standard; do not override it here.
include_directories(src/include)

set(EXTENSION_SOURCES
    src/mssql_net_extension.cpp
    src/mssql_net_secret.cpp
    src/mssql_net_storage.cpp
    src/mssql_net_optimizer.cpp
    src/arrownet/clr_host.cpp
    src/arrownet/arrow_ingest.cpp
    src/arrownet/arrow_produce.cpp
    src/catalog/mssql_net_metadata.cpp
    src/catalog/mssql_net_catalog.cpp
    src/catalog/mssql_net_schema_entry.cpp
    src/catalog/mssql_net_table_entry.cpp
    src/catalog/mssql_net_transaction.cpp
    src/dml/mssql_net_insert.cpp
    src/dml/mssql_net_modify.cpp
    src/dml/mssql_net_ctas.cpp
    src/copy/mssql_net_copy.cpp
)

build_static_extension(${TARGET_NAME} ${EXTENSION_SOURCES})
build_loadable_extension(${TARGET_NAME} " " ${EXTENSION_SOURCES})

# dlopen/dlsym for the CoreCLR host on POSIX (no-op on Windows).
target_link_libraries(${EXTENSION_NAME} ${CMAKE_DL_LIBS})
target_link_libraries(${LOADABLE_EXTENSION_NAME} ${CMAKE_DL_LIBS})

target_compile_definitions(${EXTENSION_NAME} PRIVATE MSSQL_NET_VERSION="${MSSQL_NET_EXTENSION_VERSION}")
target_compile_definitions(${LOADABLE_EXTENSION_NAME} PRIVATE MSSQL_NET_VERSION="${MSSQL_NET_EXTENSION_VERSION}")

install(
    TARGETS ${EXTENSION_NAME}
    EXPORT "${DUCKDB_EXPORT_SET}"
    LIBRARY DESTINATION "${INSTALL_LIB_DIR}"
    ARCHIVE DESTINATION "${INSTALL_LIB_DIR}")
