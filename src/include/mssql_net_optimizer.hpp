//===----------------------------------------------------------------------===//
//                         mssql_net — optimizer extension
//
// Pushes a bare LIMIT (no ORDER BY) on an mssql_net catalog scan down to SQL
// Server as `SELECT TOP (n)`. Safe because an unordered LIMIT may return any n
// rows, and DuckDB keeps its own LIMIT operator (the pushdown only reduces the
// rows that cross the wire). ORDER BY / TopN pushdown is intentionally not done
// here yet (it needs column-nullability + collation handling to stay correct).
//===----------------------------------------------------------------------===//

#pragma once

namespace duckdb {

class DBConfig;

void RegisterMssqlNetOptimizer(DBConfig &config);

} // namespace duckdb
