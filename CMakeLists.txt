cmake_minimum_required(VERSION 3.21)

# Extension name — must match the DUCKDB_CPP_EXTENSION_ENTRY(...) symbol.
set(TARGET_NAME fabricator)
set(EXTENSION_NAME ${TARGET_NAME}_extension)
set(LOADABLE_EXTENSION_NAME ${TARGET_NAME}_loadable_extension)

# THE VERSION LIVES IN ONE FILE, and that is a fix rather than a style choice: it used to be a literal
# here AND a second literal in extension_config.cmake, and CLAUDE.md records that bumping BOTH is easy to
# miss -- the prose tracking it went stale four separate times. Since the managed side needs the same
# number too (the NuGet package version + AssemblyInformationalVersion, dotnet/Directory.Build.props), a
# third literal would have made a known problem worse. All three read ./VERSION.
# CMAKE_CURRENT_LIST_DIR, not CMAKE_CURRENT_SOURCE_DIR: this file is processed from DuckDB's build, so
# only the former reliably names THIS repo.
file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/VERSION" FABRICATOR_EXTENSION_VERSION LIMIT_COUNT 1)
# ⚠⚠ WITHOUT THIS, EDITING ./VERSION CHANGES NOTHING UNTIL SOMEBODY RE-CONFIGURES BY HAND — and the build
# then produces a binary reporting the OLD version from a tree containing the new one, which is precisely
# the half-applied bump the single file exists to prevent, reintroduced one layer down. MEASURED: after
# 0.0.13 -> 0.0.14, build.ninja still carried FABRICATOR_VERSION="0.0.13".
# file(STRINGS) READS a file; it does not make it a configure input. This does.
# ⚠ Registering it ONCE is enough for both readers: a re-configure re-processes extension_config.cmake too,
# so its own file(STRINGS) picks the new value up in the same pass.
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/VERSION")

project(${TARGET_NAME} CXX)

# DuckDB controls the C++ standard; do not override it here.
include_directories(src/include)

set(EXTENSION_SOURCES
    src/fabricator_extension.cpp
    src/fabricator_secret.cpp
    src/fabricator_storage.cpp
    src/fabricator_fs_spike.cpp
    src/fabricator_wait.cpp
    src/fabricator_host_query.cpp
    src/fabricator_http.cpp
    src/fabricator_optimizer.cpp
    src/fabricator/clr_host.cpp
    src/fabricator/fabricator_onelake_fs.cpp
    src/fabricator/arrow_ingest.cpp
    src/fabricator/scan_wait.cpp
    src/fabricator/arrow_produce.cpp
    src/catalog/fabricator_metadata.cpp
    src/catalog/fabricator_catalog.cpp
    src/catalog/fabricator_lateral.cpp
    src/catalog/fabricator_merge_into.cpp
    src/catalog/fabricator_remote_select.cpp
    src/catalog/fabricator_schema_entry.cpp
    src/catalog/fabricator_table_entry.cpp
    src/catalog/fabricator_transaction.cpp
    src/dml/fabricator_insert.cpp
    src/dml/fabricator_modify.cpp
    src/dml/fabricator_ctas.cpp
    src/copy/fabricator_copy.cpp
)

# The Substrait producer for the remote SELECT pushdown (third_party/duckdb-substrait, docs/duckdb-2.0-remote-execution.md
# §3.4). OFF by default: it needs protobuf and substrait-protobuf, the latter from the substrait-packaging vcpkg
# registry, which our CI does not install yet.
# The Substrait producer behind the remote pushdown (src/catalog/fabricator_remote_select.cpp). Needs protobuf +
# substrait-protobuf, installed from deps/substrait/vcpkg.json into a root of their own and passed in with
# CMAKE_PREFIX_PATH (see .github/actions/build-extension).
list(APPEND EXTENSION_SOURCES
     third_party/duckdb-substrait/src/to_substrait.cpp
     third_party/duckdb-substrait/src/custom_extensions.cpp
     third_party/duckdb-substrait/src/custom_extensions_generated.cpp)
include_directories(third_party/duckdb-substrait/include)

build_static_extension(${TARGET_NAME} ${EXTENSION_SOURCES})
build_loadable_extension(${TARGET_NAME} " " ${EXTENSION_SOURCES})

# dlopen/dlsym for the CoreCLR host on POSIX (no-op on Windows).
target_link_libraries(${EXTENSION_NAME} ${CMAKE_DL_LIBS})
target_link_libraries(${LOADABLE_EXTENSION_NAME} ${CMAKE_DL_LIBS})

# yyjson (vcpkg) — the JSON parser for the ABI's JSON-shaped crossings (table_info / table_stats /
# get_capabilities). ⚠ Deliberately OUR OWN copy, not DuckDB's vendored one: duckdb_static carries yyjson
# inside the C++ namespace `duckdb_yyjson` with none of it DUCKDB_API-exported, so a LOADABLE extension
# cannot resolve those symbols against the host binary. The vcpkg build exports the plain C `yyjson_*`
# names, so the two copies cannot collide in the static build either. Requires `vcpkg install yyjson`
# (same triplet as openssl/curl) — see CLAUDE.md §Build & test and the CI workflows' vcpkg lines.
find_package(yyjson CONFIG REQUIRED)
target_link_libraries(${EXTENSION_NAME} yyjson::yyjson)
target_link_libraries(${LOADABLE_EXTENSION_NAME} yyjson::yyjson)

find_package(Protobuf CONFIG REQUIRED)
find_package(SubstraitProtobuf CONFIG REQUIRED)
target_link_libraries(${EXTENSION_NAME} substrait::proto)
target_link_libraries(${LOADABLE_EXTENSION_NAME} substrait::proto)

# macOS ONLY: export the SECOND entry point too.
#
# src/fabricator_extension.cpp defines two entries — `fabricator_duckdb_cpp_init` and
# `fabricator_core_duckdb_cpp_init` — so ONE binary can be loaded under either file name (DuckDB derives
# the entry symbol from the FILE NAME, and the single-file distribution needs the chain-loaded core to
# answer to a different name than the installer that loads it; see the comment on the second entry).
#
# On Apple, DuckDB links loadable extensions with hidden visibility, -dead_strip and an explicit
# ONE-symbol whitelist (duckdb/extension/extension_build_tools.cmake):
#     set(WHITELIST "-Wl,-exported_symbol,_${NAME}_duckdb_cpp_init")
# so only _fabricator_duckdb_cpp_init survives and the second entry is stripped — the core then fails to
# load as fabricator_core.duckdb_extension with "did not contain the expected entrypoint function".
# Linux (--gc-sections/--exclude-libs) and Windows (dllexport) both keep it, so macOS is the only
# platform that needs this. ld64 accumulates repeated -exported_symbol flags, so this ADDS to DuckDB's
# whitelist rather than replacing it. The leading underscore is the Mach-O C symbol prefix.
if(APPLE)
    target_link_options(${LOADABLE_EXTENSION_NAME} PRIVATE
                        "-Wl,-exported_symbol,_${TARGET_NAME}_core_duckdb_cpp_init")
endif()

# Linux ONLY: keep the statically linked OpenSSL out of libduckdb.so's DYNAMIC symbol table.
#
# DuckDB 2.0 links unittest (and the shell) against a SHARED libduckdb.so, and the vcpkg OpenSSL that httpfs
# links statically lands in it with default visibility — 536 exported SSL_* symbols, where the 1.5
# statically linked unittest exported none. .NET does TLS through the SYSTEM libssl.so.3, and that library's
# references to libcrypto resolve through the GLOBAL scope first, i.e. into libduckdb.so. MEASURED with
# LD_DEBUG=bindings: 495 of libssl's libcrypto bindings went to libduckdb.so and none to libcrypto.so.3, so
# SqlClient's handshake ran system libssl 3.0 on top of vcpkg libcrypto 3.6.x. With CI's OpenSSL 3.6.5 every
# SQL Server connect failed ("SSL Provider, error: 31 - Encryption(ssl/tls) handshake failed") and the 2.0
# integration tier never passed. --exclude-libs leaves the copy PRIVATE to the code linked with it.
# ⚠ DEFERRED to the end of the top-level configure: DuckDB adds extension directories BEFORE src/ in the
# statically-linked build, so `duckdb` does not exist yet when this file runs — an immediate
# `if(TARGET duckdb)` was silently false (measured: the flag never reached libduckdb.so's link line).
# Shipped loadables do not link libduckdb.so and are unaffected.
function(fabricator_hide_static_openssl)
    if(TARGET duckdb)
        target_link_options(duckdb PRIVATE "LINKER:--exclude-libs,libssl.a:libcrypto.a")
    endif()
endfunction()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    cmake_language(DEFER DIRECTORY ${CMAKE_SOURCE_DIR} CALL fabricator_hide_static_openssl)
endif()

target_compile_definitions(${EXTENSION_NAME} PRIVATE FABRICATOR_VERSION="${FABRICATOR_EXTENSION_VERSION}")
target_compile_definitions(${LOADABLE_EXTENSION_NAME} PRIVATE FABRICATOR_VERSION="${FABRICATOR_EXTENSION_VERSION}")

install(
    TARGETS ${EXTENSION_NAME}
    EXPORT "${DUCKDB_EXPORT_SET}"
    LIBRARY DESTINATION "${INSTALL_LIB_DIR}"
    ARCHIVE DESTINATION "${INSTALL_LIB_DIR}")
