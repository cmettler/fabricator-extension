cmake_minimum_required(VERSION 3.21)

# Extension name — must match the DUCKDB_CPP_EXTENSION_ENTRY(...) symbol.
set(TARGET_NAME fabricator)
set(EXTENSION_NAME ${TARGET_NAME}_extension)
set(LOADABLE_EXTENSION_NAME ${TARGET_NAME}_loadable_extension)

set(FABRICATOR_EXTENSION_VERSION "0.0.1")

project(${TARGET_NAME} CXX)

# DuckDB controls the C++ standard; do not override it here.
include_directories(src/include)

set(EXTENSION_SOURCES
    src/fabricator_extension.cpp
    src/fabricator_secret.cpp
    src/fabricator_storage.cpp
    src/fabricator_fs_spike.cpp
    src/fabricator_host_query.cpp
    src/fabricator_optimizer.cpp
    src/fabricator/clr_host.cpp
    src/fabricator/fabricator_onelake_fs.cpp
    src/fabricator/fabricator_delta_mfr.cpp
    src/fabricator/fabricator_variant.cpp
    src/fabricator/arrow_ingest.cpp
    src/fabricator/arrow_produce.cpp
    src/catalog/fabricator_metadata.cpp
    src/catalog/fabricator_catalog.cpp
    src/catalog/fabricator_schema_entry.cpp
    src/catalog/fabricator_table_entry.cpp
    src/catalog/fabricator_transaction.cpp
    src/dml/fabricator_insert.cpp
    src/dml/fabricator_modify.cpp
    src/dml/fabricator_ctas.cpp
    src/copy/fabricator_copy.cpp
)

build_static_extension(${TARGET_NAME} ${EXTENSION_SOURCES})
build_loadable_extension(${TARGET_NAME} " " ${EXTENSION_SOURCES})

# dlopen/dlsym for the CoreCLR host on POSIX (no-op on Windows).
target_link_libraries(${EXTENSION_NAME} ${CMAKE_DL_LIBS})
target_link_libraries(${LOADABLE_EXTENSION_NAME} ${CMAKE_DL_LIBS})

target_compile_definitions(${EXTENSION_NAME} PRIVATE FABRICATOR_VERSION="${FABRICATOR_EXTENSION_VERSION}")
target_compile_definitions(${LOADABLE_EXTENSION_NAME} PRIVATE FABRICATOR_VERSION="${FABRICATOR_EXTENSION_VERSION}")

install(
    TARGETS ${EXTENSION_NAME}
    EXPORT "${DUCKDB_EXPORT_SET}"
    LIBRARY DESTINATION "${INSTALL_LIB_DIR}"
    ARCHIVE DESTINATION "${INSTALL_LIB_DIR}")
