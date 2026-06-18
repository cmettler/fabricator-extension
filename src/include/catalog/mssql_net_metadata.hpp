//===----------------------------------------------------------------------===//
//                         mssql_net — catalog metadata helpers
//===----------------------------------------------------------------------===//

#pragma once

// arrow_ingest pulls DuckDB's Arrow C headers first; keep it ahead of abi.h.
#include "arrownet/arrow_ingest.hpp"

#include "arrownet/abi.h"
#include "duckdb/main/client_context.hpp"

namespace duckdb {

//! A discovered SQL Server table (or view).
struct MssqlNetTableInfo {
	string schema_name;
	string table_name;
	string table_type; // "BASE TABLE" | "VIEW"
};

//! Reads every row of an Arrow stream whose columns are all UTF-8 strings.
//! Consumes and releases the stream. Returns rows[r][c].
vector<vector<string>> ReadStringTable(ArrowArrayStream &stream, idx_t expected_cols);

//! Discovers user schemas in the attached SQL Server database.
vector<string> DiscoverSchemas(ArrowNetHandle handle);

//! Discovers user tables + views across all schemas.
vector<MssqlNetTableInfo> DiscoverTables(ArrowNetHandle handle);

//! Resolves a table's column names + DuckDB types from the Arrow schema of the
//! COLUMNS metadata stream (a zero-row result; reuses the C# type mapping, no
//! duplicate type logic in C++).
void FetchTableColumns(ClientContext &context, ArrowNetHandle handle, const string &schema_name,
                       const string &table_name, vector<string> &names, vector<LogicalType> &types);

//! Discovers the row-identity columns for a table, in key order: the primary
//! key if present, else the unique index with the fewest columns. Returns empty
//! if the table has no PK or unique index.
vector<string> FetchRowIdColumns(ArrowNetHandle handle, const string &schema_name, const string &table_name);

} // namespace duckdb
