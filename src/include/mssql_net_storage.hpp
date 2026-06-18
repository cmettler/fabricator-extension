//===----------------------------------------------------------------------===//
//                         mssql_net — storage extension registration
//===----------------------------------------------------------------------===//

#pragma once

namespace duckdb {

class ExtensionLoader;

//! Registers the "mssql_net" storage extension so that
//! `ATTACH '<connstr>' AS db (TYPE mssql_net)` works.
void RegisterMssqlNetStorageExtension(ExtensionLoader &loader);

} // namespace duckdb
