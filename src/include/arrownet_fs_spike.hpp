//===----------------------------------------------------------------------===//
//                         arrownet — filesystem reverse-callback SPIKE
//
// Proves a managed component can do secret-backed remote IO via DuckDB's
// FileSystem: it installs the host-services callbacks (so C# can call back into
// DuckDB's FileSystem) and registers an `arrownet_fs_spike(path)` table function
// that asks the managed side to open the path + report its head/tail bytes + size.
// Foundation for a future C# lakehouse reader using DuckDB IO + secrets.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/main/extension/extension_loader.hpp"

namespace duckdb {

//! Installs the host FileSystem callbacks (before the bridge boots) and registers the
//! `arrownet_fs_spike(path VARCHAR)` table function. Call early in extension load.
void RegisterFsSpike(ExtensionLoader &loader);

} // namespace duckdb
