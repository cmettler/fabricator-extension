//===----------------------------------------------------------------------===//
//                         mssql_net — COPY TO (bulk load)
//===----------------------------------------------------------------------===//

#pragma once

namespace duckdb {

class ExtensionLoader;

//! Registers the `mssql_net` COPY format:
//!   COPY (query) TO 'mssql://catalog/schema/table' (FORMAT mssql_net)
//!   COPY tbl     TO 'catalog.schema.table'         (FORMAT mssql_net)
//! Reuses the generic Arrow bulk-load path (provider does CREATE TABLE + copy).
void RegisterMssqlNetCopyFunction(ExtensionLoader &loader);

} // namespace duckdb
