//===----------------------------------------------------------------------===//
//                         arrownet — table catalog entry
//===----------------------------------------------------------------------===//

#pragma once

#include "arrownet/abi.h"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/parser/parsed_data/create_table_info.hpp"

#include <unordered_map>

namespace duckdb {

class LogicalGet;
class Expression;
class BoundAtClause;

//! pushdown_complex_filter callback shared by the catalog table scan AND the table-
//! function (TVF) scan: serializes the superset-safe predicates into the scan's
//! ArrowStreamBindData (filter_json + filter_constants) and LEAVES every filter in
//! `filters` (best-effort — DuckDB re-applies them all, so an over-approximation is safe).
void ArrowNetComplexFilterPushdown(ClientContext &context, LogicalGet &get, FunctionData *bind_data,
                                   vector<unique_ptr<Expression>> &filters);

class ArrowNetTableEntry : public TableCatalogEntry {
public:
	ArrowNetTableEntry(Catalog &catalog, SchemaCatalogEntry &schema, CreateTableInfo &info, ArrowNetHandle handle,
	                   vector<idx_t> rowid_columns, LogicalType rowid_type);

	//! Produces a table scan that streams `SELECT * FROM [schema].[table]` from
	//! SQL Server as Arrow into DuckDB.
	TableFunction GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data) override;
	//! Time-travel overload: DuckDB binds `FROM t AT (...)` and passes the bound clause via the lookup info.
	//! TIMESTAMP maps to `FOR SYSTEM_TIME AS OF` (system-versioned tables); VERSION is rejected managed-side.
	TableFunction GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data,
	                              const EntryLookupInfo &lookup_info) override;

	unique_ptr<BaseStatistics> GetStatistics(ClientContext &context, column_t column_id) override;
	TableStorageInfo GetStorageInfo(ClientContext &context) override;

	//! SQL Server tables are exposed without a DuckDB rowid (no row-identity
	//! virtual column), so count(*)/scans don't require projection pushdown.
	virtual_column_map_t GetVirtualColumns() const override;
	vector<column_t> GetRowIdColumns() const override;

	bool HasRowId() const {
		return !rowid_columns_.empty();
	}
	//! Indices (in table column order) of the rowid/PK columns.
	const vector<idx_t> &RowIdColumnIndices() const {
		return rowid_columns_;
	}

private:
	//! Shared body of both GetScanFunction overloads; `at_clause` (nullable) is the time-travel snapshot.
	TableFunction BuildScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data,
	                                optional_ptr<BoundAtClause> at_clause);

	ArrowNetHandle handle_;
	//! Indices (in table column order) of the rowid/PK columns; empty => no rowid.
	vector<idx_t> rowid_columns_;
	//! rowid type: scalar (single column) or STRUCT (compound key).
	LogicalType rowid_type_;
	//! Lazily-fetched approximate row count for the optimizer (-2 = not yet fetched,
	//! -1 = unknown). Cached for the entry's lifetime (refreshed by RefreshCache).
	int64_t row_count_ = -2;
	//! Lazily-fetched per-column NDV (distinct count) keyed by column name; columns
	//! absent => unknown. Cached for the entry's lifetime. `ndv_fetched_` guards the
	//! one-time fetch (the map may legitimately be empty).
	std::unordered_map<string, int64_t> column_ndv_;
	bool ndv_fetched_ = false;
};

} // namespace duckdb
