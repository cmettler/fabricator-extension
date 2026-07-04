//===----------------------------------------------------------------------===//
// arrownet/arrownet_onelake_fs.hpp
//
// A DuckDB FileSystem for the `onelake://` scheme, registered in the VFS at extension load. Its ops are
// forwarded to the managed Azure DataLake SDK (via the onelake_* vtable entries → OneLakeForwardFs) — so
// DuckDB's native readers/writers + ExternalFileCache use OneLake uniformly, bypassing duckdb-azure's OneLake
// gaps. Supports read + sequential write + the directory checks DuckDB's partitioned COPY needs (dirs are
// implicit on ADLS Gen2). RemoveFile/MoveFile/RemoveDirectory throw (DROP goes via the DFS SDK directly).
// See docs/filesystem-bridge.md §3.
//===----------------------------------------------------------------------===//
#pragma once

#include "duckdb/common/file_system.hpp"

namespace duckdb {
class DatabaseInstance;
}

namespace arrownet {

// Register the onelake:// FileSystem in the database's VFS (best-effort; safe to call once at load).
void RegisterOneLakeFileSystem(duckdb::DatabaseInstance &db);

} // namespace arrownet
