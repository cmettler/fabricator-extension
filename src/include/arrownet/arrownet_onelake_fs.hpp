//===----------------------------------------------------------------------===//
// arrownet/arrownet_onelake_fs.hpp
//
// A DuckDB FileSystem for the `onelake://` scheme, registered in the VFS at extension load. Its read ops are
// forwarded to the managed Azure DataLake SDK (via the onelake_* vtable entries → OneLakeForwardFs) — so
// DuckDB's native readers + ExternalFileCache use OneLake uniformly, bypassing duckdb-azure's OneLake gaps.
// Read-only for now (write ops throw). See docs/filesystem-bridge.md §3.
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
