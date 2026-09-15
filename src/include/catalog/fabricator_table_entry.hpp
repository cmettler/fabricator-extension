// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — table catalog entry
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
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
void FabricatorComplexFilterPushdown(ClientContext &context, LogicalGet &get, FunctionData *bind_data,
                                   vector<unique_ptr<Expression>> &filters);

class FabricatorTableEntry : public TableCatalogEntry {
public:
	//! `table_handle` is the managed TABLE-SESSION handle (ABI v72 `table_open`) this entry OWNS for its
	//! whole life — released by the destructor (`table_close`, best-effort). It wraps the stateless table
	//! DEFINITION (+ the AT clause for a time-travel entry), never a binding: every table_* call re-binds
	//! against the ambient transaction, so keeping it across the retire-don't-destroy graveyard is safe.
	FabricatorTableEntry(Catalog &catalog, SchemaCatalogEntry &schema, CreateTableInfo &info,
	                   FabricatorHandle table_handle, vector<idx_t> rowid_columns, LogicalType rowid_type,
	                   vector<string> virtual_rowid_columns = {},
	                   vector<std::pair<string, LogicalType>> provider_virtual_columns = {});
	~FabricatorTableEntry() override;

	//! Produces a table scan that streams `SELECT * FROM [schema].[table]` from
	//! SQL Server as Arrow into DuckDB.
	TableFunction GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data) override;
	//! Time-travel overload: DuckDB binds `FROM t AT (...)` and passes the bound clause via the lookup info.
	//! TIMESTAMP maps to `FOR SYSTEM_TIME AS OF` (system-versioned tables); VERSION is rejected managed-side.
	TableFunction GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data,
	                              const EntryLookupInfo &lookup_info) override;

	unique_ptr<BaseStatistics> GetStatistics(ClientContext &context, column_t column_id) override;
	TableStorageInfo GetStorageInfo(ClientContext &context) override;

	//! The base decides which EXTRA columns an UPDATE must project. We take every one of its rules except
	//! ONE: it turns an update that assigns a LIST / ARRAY / MAP / UNION / VARIANT / GEOMETRY column (or a
	//! STRUCT containing any of those) into a delete+insert and therefore projects the WHOLE ROW.
	//!
	//! ⚠⚠ THAT RULE IS ABOUT DuckDB'S OWN ROW-GROUP STORAGE — you cannot patch a LIST in place — AND IT HAS
	//! NOTHING TO SAY ABOUT A REMOTE TABLE WE UPDATE BY COLUMN NAME OVER A WIRE PROTOCOL. Inherited, it made
	//! a one-column UPDATE arrive at the provider as every column of the row, with NOTHING on the wire
	//! saying which happened — so a provider that diffs the incoming row to decide what really changed
	//! cannot tell "the user set 17 columns" from "DuckDB expanded one".
	//!
	//! ⚠ `LogicalType::SupportsRegularUpdate()` is NOT virtual and takes no catalog, so there is no flag an
	//! entry can set to opt out — overriding this method is the only hook. See the .cpp for the per-branch
	//! reasoning and for what must be re-verified before changing it.
	void BindUpdateConstraints(Binder &binder, LogicalGet &get, LogicalProjection &proj, LogicalUpdate &update,
	                           ClientContext &context) override;

	//! SQL Server tables are exposed without a DuckDB rowid (no row-identity
	//! virtual column), so count(*)/scans don't require projection pushdown.
	virtual_column_map_t GetVirtualColumns() const override;
	vector<column_t> GetRowIdColumns() const override;

	//! The owned table-session handle (ABI v72 `table_open`), for the session entries a CALLER drives rather
	//! than this entry — today only `table_alter`, which FabricatorSchemaEntry::Alter reaches through the
	//! entry DuckDB itself just resolved. Never null for a live entry; valid for the entry's whole life,
	//! graveyard included, because it wraps the stateless DEFINITION.
	FabricatorHandle TableHandle() const {
		return table_handle_;
	}

	bool HasRowId() const {
		return !rowid_columns_.empty() || !virtual_rowid_columns_.empty();
	}
	//! Indices (in table column order) of the rowid/PK columns.
	const vector<idx_t> &RowIdColumnIndices() const {
		return rowid_columns_;
	}
	//! True when the rowid is a virtual (non-user-column) provider column, e.g. Delta's `_metadata.row_id`.
	bool HasVirtualRowId() const {
		return !virtual_rowid_columns_.empty();
	}
	//! The virtual rowid source column names (empty unless HasVirtualRowId()).
	const vector<string> &VirtualRowIdColumns() const {
		return virtual_rowid_columns_;
	}
	//! The rowid's DuckDB type (scalar for a single column, STRUCT for a compound key).
	const LogicalType &RowIdType() const {
		return rowid_type_;
	}

private:
	//! Shared body of both GetScanFunction overloads; `at_clause` (nullable) is the time-travel snapshot.
	TableFunction BuildScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data,
	                                optional_ptr<BoundAtClause> at_clause);

	//! The owned table-session handle (see the constructor note). Never null for a live entry.
	FabricatorHandle table_handle_;
	//! Indices (in table column order) of the rowid/PK columns; empty => no rowid.
	vector<idx_t> rowid_columns_;
	//! Virtual rowid source column names (provider-supplied, not in the user schema); empty => none.
	vector<string> virtual_rowid_columns_;
	//! Provider-declared virtual columns beyond the rowid (name, type): queryable by name, not in
	//! SELECT * — e.g. the Delta catalog's stable __delta_row_id / __delta_row_commit_version.
	//! Registered in GetVirtualColumns at fabricator::ProviderVirtualBase() + index.
	vector<std::pair<string, LogicalType>> provider_virtual_columns_;
	//! rowid type: scalar (single column) or STRUCT (compound key).
	LogicalType rowid_type_;
	//! Lazily-fetched approximate row count for the optimizer (-1 = unknown). Cached for the entry's
	//! lifetime (refreshed by RefreshCache, which rebuilds the entry).
	int64_t row_count_ = -1;
	//! Lazily-fetched per-column NDV (distinct count) keyed by column name; columns
	//! absent => unknown. Cached for the entry's lifetime.
	std::unordered_map<string, int64_t> column_ndv_;
	//! Guards the ONE lazy `table_stats` crossing that fills both fields above (was two crossings —
	//! kinds 4 + 5 — with a flag each). The map may legitimately stay empty.
	bool stats_fetched_ = false;
};

} // namespace duckdb
