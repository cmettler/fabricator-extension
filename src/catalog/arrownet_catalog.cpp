//===----------------------------------------------------------------------===//
//                         arrownet — catalog (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_catalog.hpp"

#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "catalog/arrownet_schema_entry.hpp"
#include "catalog/arrownet_table_entry.hpp"
#include "dml/arrownet_ctas.hpp"
#include "dml/arrownet_insert.hpp"
#include "dml/arrownet_modify.hpp"
#include "duckdb/catalog/catalog_entry/schema_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/parser/parsed_data/create_schema_info.hpp"
#include "duckdb/parser/parsed_data/create_table_info.hpp"
#include "duckdb/parser/parsed_data/drop_info.hpp"
#include "duckdb/planner/operator/logical_create_table.hpp"
#include "duckdb/planner/operator/logical_delete.hpp"
#include "duckdb/planner/operator/logical_insert.hpp"
#include "duckdb/planner/operator/logical_update.hpp"
#include "duckdb/storage/database_size.hpp"

#include <algorithm>
#include <regex>

namespace duckdb {

namespace {

// Compiles the (optional) icase catalog filters once and matches names by substring
// regex search — mirrors the C++ mssql extension's schema_filter / table_filter.
struct CatalogFilters {
	bool has_schema = false;
	bool has_table = false;
	std::regex schema_re;
	std::regex table_re;

	CatalogFilters(const string &schema_filter, const string &table_filter) {
		if (!schema_filter.empty()) {
			schema_re = std::regex(schema_filter, std::regex::icase);
			has_schema = true;
		}
		if (!table_filter.empty()) {
			table_re = std::regex(table_filter, std::regex::icase);
			has_table = true;
		}
	}
	bool MatchSchema(const string &n) const {
		return !has_schema || std::regex_search(n, schema_re);
	}
	bool MatchTable(const string &n) const {
		return !has_table || std::regex_search(n, table_re);
	}
};

} // namespace

void ArrowNetCatalog::ValidateCatalogFilters(const string &schema_filter, const string &table_filter) {
	auto check = [](const string &pattern) {
		if (pattern.empty()) {
			return;
		}
		try {
			std::regex(pattern, std::regex::icase);
		} catch (const std::regex_error &e) {
			throw InvalidInputException("mssql_net: Invalid regex in catalog filter '%s': %s", pattern, e.what());
		}
	};
	check(schema_filter);
	check(table_filter);
}

void ArrowNetCatalog::SetCatalogFilters(const string &schema_filter, const string &table_filter) {
	ValidateCatalogFilters(schema_filter, table_filter);
	schema_filter_ = schema_filter;
	table_filter_ = table_filter;
}

ArrowNetCatalog::ArrowNetCatalog(AttachedDatabase &db, string internal_name, ArrowNetHandle handle, string db_path)
    : Catalog(db), handle_(handle), db_path_(std::move(db_path)) {
}

ArrowNetCatalog::~ArrowNetCatalog() {
	arrownet::CloseCatalog(handle_);
}

void ArrowNetCatalog::LoadCatalog(ClientContext &context) {
	lock_guard<mutex> lock(schema_lock_);

	// Detect the database collation's sort semantics once (binary => SQL Server's byte-order string sort
	// matches DuckDB, so string-keyed ORDER BY+LIMIT can be pushed). Best-effort: a failure leaves it off.
	try {
		string_order_pushable_ = FetchBinaryCollation(handle_);
	} catch (...) {
		string_order_pushable_ = false;
	}

	auto ensure_schema = [&](const string &schema_name) -> ArrowNetSchemaEntry & {
		auto it = schemas_.find(schema_name);
		if (it != schemas_.end()) {
			return *it->second;
		}
		CreateSchemaInfo info;
		info.schema = schema_name;
		auto entry = make_uniq<ArrowNetSchemaEntry>(*this, info, handle_);
		auto &ref = *entry;
		schemas_[schema_name] = std::move(entry);
		return ref;
	};

	CatalogFilters filters(schema_filter_, table_filter_);
	for (auto &schema_name : DiscoverSchemas(handle_)) {
		if (filters.MatchSchema(schema_name)) {
			ensure_schema(schema_name);
		}
	}
	for (auto &table : DiscoverTables(handle_)) {
		if (filters.MatchSchema(table.schema_name) && filters.MatchTable(table.table_name)) {
			ensure_schema(table.schema_name).AddTable(table.table_name, table.table_type);
		}
	}
	// Expose discovered routines as callable catalog functions: scalar UDFs
	// (db.schema.fn(args)), table-valued functions and stored procedures
	// (SELECT * FROM db.schema.fn(args) — procs run via EXEC, see AddTableFunction).
	for (auto &func : DiscoverFunctions(handle_)) {
		if (!filters.MatchSchema(func.schema_name)) {
			continue;
		}
		if (func.kind == "scalar") {
			ensure_schema(func.schema_name).AddScalarFunction(func.name);
		} else if (func.kind == "table") {
			ensure_schema(func.schema_name).AddTableFunction(func.name, /*is_proc=*/false);
		} else if (func.kind == "proc") {
			ensure_schema(func.schema_name).AddTableFunction(func.name, /*is_proc=*/true);
		} else if (func.kind == "inout") {
			// Provider-authored custom table-in-out (4g, pure C#): a {TABLE}-param table function under
			// the bare name (no scalar-arg scan form, no `_each` alias — it is already in-out).
			ensure_schema(func.schema_name).AddInOutFunction(func.name);
		} else if (func.kind == "aggregate") {
			// Provider-authored custom aggregate (4h, UDAF, pure C#): an AggregateFunctionCatalogEntry.
			ensure_schema(func.schema_name).AddAggregateFunction(func.name, /*spillable=*/false);
		} else if (func.kind == "aggregate_spill") {
			// Spillable variant: state serialized into DuckDB's blob so external GROUP BY can spill to disk.
			ensure_schema(func.schema_name).AddAggregateFunction(func.name, /*spillable=*/true);
		}
	}
}

void ArrowNetCatalog::RefreshCache(ClientContext &context) {
	lock_guard<mutex> lock(schema_lock_);

	auto ensure_schema = [&](const string &schema_name) -> ArrowNetSchemaEntry & {
		auto it = schemas_.find(schema_name);
		if (it != schemas_.end()) {
			return *it->second;
		}
		CreateSchemaInfo info;
		info.schema = schema_name;
		auto entry = make_uniq<ArrowNetSchemaEntry>(*this, info, handle_);
		auto &ref = *entry;
		schemas_[schema_name] = std::move(entry);
		return ref;
	};

	// Drop every cached table (so dropped tables vanish + columns re-fetch), then
	// re-run discovery to pick up tables created out-of-band (e.g. via mssql_net_exec).
	for (auto &entry : schemas_) {
		entry.second->ClearTables();
	}
	CatalogFilters filters(schema_filter_, table_filter_);
	for (auto &schema_name : DiscoverSchemas(handle_)) {
		if (filters.MatchSchema(schema_name)) {
			ensure_schema(schema_name);
		}
	}
	for (auto &table : DiscoverTables(handle_)) {
		if (filters.MatchSchema(table.schema_name) && filters.MatchTable(table.table_name)) {
			ensure_schema(table.schema_name).AddTable(table.table_name, table.table_type);
		}
	}
	// Expose discovered routines as callable catalog functions: scalar UDFs
	// (db.schema.fn(args)), table-valued functions and stored procedures
	// (SELECT * FROM db.schema.fn(args) — procs run via EXEC, see AddTableFunction).
	for (auto &func : DiscoverFunctions(handle_)) {
		if (!filters.MatchSchema(func.schema_name)) {
			continue;
		}
		if (func.kind == "scalar") {
			ensure_schema(func.schema_name).AddScalarFunction(func.name);
		} else if (func.kind == "table") {
			ensure_schema(func.schema_name).AddTableFunction(func.name, /*is_proc=*/false);
		} else if (func.kind == "proc") {
			ensure_schema(func.schema_name).AddTableFunction(func.name, /*is_proc=*/true);
		} else if (func.kind == "inout") {
			// Provider-authored custom table-in-out (4g, pure C#): a {TABLE}-param table function under
			// the bare name (no scalar-arg scan form, no `_each` alias — it is already in-out).
			ensure_schema(func.schema_name).AddInOutFunction(func.name);
		} else if (func.kind == "aggregate") {
			// Provider-authored custom aggregate (4h, UDAF, pure C#): an AggregateFunctionCatalogEntry.
			ensure_schema(func.schema_name).AddAggregateFunction(func.name, /*spillable=*/false);
		} else if (func.kind == "aggregate_spill") {
			// Spillable variant: state serialized into DuckDB's blob so external GROUP BY can spill to disk.
			ensure_schema(func.schema_name).AddAggregateFunction(func.name, /*spillable=*/true);
		}
	}
}

void ArrowNetCatalog::Initialize(bool load_builtin) {
	// Discovery happens in LoadCatalog (called from attach, where a context exists).
}

string ArrowNetCatalog::GetCatalogType() {
	return CATALOG_TYPE;
}

optional_ptr<SchemaCatalogEntry> ArrowNetCatalog::LookupSchema(CatalogTransaction transaction,
                                                              const EntryLookupInfo &schema_lookup,
                                                              OnEntryNotFound if_not_found) {
	lock_guard<mutex> lock(schema_lock_);
	auto it = schemas_.find(schema_lookup.GetEntryName());
	if (it != schemas_.end()) {
		return it->second.get();
	}
	if (if_not_found == OnEntryNotFound::THROW_EXCEPTION) {
		throw BinderException("mssql_net: schema \"%s\" not found", schema_lookup.GetEntryName());
	}
	return nullptr;
}

void ArrowNetCatalog::ScanSchemas(ClientContext &context, std::function<void(SchemaCatalogEntry &)> callback) {
	lock_guard<mutex> lock(schema_lock_);
	for (auto &entry : schemas_) {
		callback(*entry.second);
	}
}

DatabaseSize ArrowNetCatalog::GetDatabaseSize(ClientContext &context) {
	return DatabaseSize();
}

bool ArrowNetCatalog::InMemory() {
	return false;
}

string ArrowNetCatalog::GetDBPath() {
	return db_path_;
}

optional_ptr<CatalogEntry> ArrowNetCatalog::CreateSchema(CatalogTransaction transaction, CreateSchemaInfo &info) {
	if (info.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT) {
		arrownet::DropSchema(handle_, info.schema, /*if_exists=*/true);
	}
	bool if_not_exists = info.on_conflict == OnCreateConflict::IGNORE_ON_CONFLICT;
	arrownet::CreateSchema(handle_, info.schema, if_not_exists);

	lock_guard<mutex> lock(schema_lock_);
	auto it = schemas_.find(info.schema);
	if (it != schemas_.end()) {
		if (info.on_conflict != OnCreateConflict::REPLACE_ON_CONFLICT) {
			return it->second.get(); // already present (IGNORE / ERROR-but-cached)
		}
		schemas_.erase(it);
	}
	auto entry = make_uniq<ArrowNetSchemaEntry>(*this, info, handle_);
	auto &ref = *entry;
	schemas_[info.schema] = std::move(entry);
	return &ref;
}
void ArrowNetCatalog::DropSchema(ClientContext &context, DropInfo &info) {
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	arrownet::DropSchema(handle_, info.name, if_exists);
	lock_guard<mutex> lock(schema_lock_);
	schemas_.erase(info.name);
}
PhysicalOperator &ArrowNetCatalog::PlanCreateTableAs(ClientContext &context, PhysicalPlanGenerator &planner,
                                                     LogicalCreateTable &op, PhysicalOperator &plan) {
	auto &create_info = op.info->base->Cast<CreateTableInfo>();

	ArrowNetCtasInfo info;
	info.schema_name = create_info.schema;
	info.table_name = create_info.table;
	for (auto &col : create_info.columns.Logical()) {
		info.column_names.push_back(col.Name());
		info.column_types.push_back(col.Type());
	}
	info.replace = op.info->base->on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT;
	info.handle = handle_;
	info.schema_entry = &op.schema.Cast<ArrowNetSchemaEntry>();

	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &ctas = planner.Make<ArrowNetPhysicalCreateTableAs>(std::move(result_types), op.estimated_cardinality,
	                                                         std::move(info));
	ctas.children.push_back(plan);
	return ctas;
}
PhysicalOperator &ArrowNetCatalog::PlanInsert(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalInsert &op, optional_ptr<PhysicalOperator> plan) {
	ArrowNetInsertTarget target;
	target.returning = op.return_chunk;
	target.schema_name = op.table.schema.name;
	target.table_name = op.table.name;

	auto all_names = op.table.GetColumns().GetColumnNames();
	vector<LogicalType> all_types;
	for (auto &col : op.table.GetColumns().Logical()) {
		all_types.push_back(col.Type());
	}
	if (op.column_index_map.empty()) {
		// INSERT without a column list: all columns in table order.
		target.columns = all_names;
		target.column_types = all_types;
	} else {
		// Preserve the INSERT statement's column order (source index order).
		vector<std::pair<idx_t, idx_t>> col_pairs;
		for (idx_t i = 0; i < all_names.size(); i++) {
			PhysicalIndex phys(i);
			if (i < op.column_index_map.size()) {
				auto mapped = op.column_index_map[phys];
				if (mapped != DConstants::INVALID_INDEX) {
					col_pairs.emplace_back(mapped, i);
				}
			}
		}
		std::sort(col_pairs.begin(), col_pairs.end());
		for (auto &pair : col_pairs) {
			target.columns.push_back(all_names[pair.second]);
			target.column_types.push_back(all_types[pair.second]);
		}
	}

	// For RETURNING, the operator emits all table columns (op.types); otherwise a
	// single rows-affected BIGINT.
	vector<LogicalType> result_types = op.return_chunk ? op.types : vector<LogicalType> {LogicalType::BIGINT};
	auto &insert = planner.Make<ArrowNetPhysicalInsert>(std::move(result_types), op.estimated_cardinality,
	                                                    std::move(target), handle_);
	if (plan) {
		insert.children.push_back(*plan);
	}
	return insert;
}
static ArrowNetModifyTarget BuildModifyTarget(LogicalOperator &, TableCatalogEntry &table) {
	auto &entry = table.Cast<ArrowNetTableEntry>();
	if (!entry.HasRowId()) {
		throw BinderException(
		    "mssql_net: UPDATE/DELETE requires a table with a primary key or unique index for row identity. "
		    "Table '%s' has neither (use mssql_net_exec for set-based UPDATE/DELETE)",
		    entry.name);
	}
	ArrowNetModifyTarget target;
	target.schema_name = entry.schema.name;
	target.table_name = entry.name;
	auto names = entry.GetColumns().GetColumnNames();
	vector<LogicalType> types;
	for (auto &col : entry.GetColumns().Logical()) {
		types.push_back(col.Type());
	}
	for (auto idx : entry.RowIdColumnIndices()) {
		target.rowid_columns.push_back(names[idx]);
		target.rowid_types.push_back(types[idx]);
	}
	return target;
}

PhysicalOperator &ArrowNetCatalog::PlanDelete(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalDelete &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("mssql_net: DELETE ... RETURNING is not supported yet");
	}
	auto target = BuildModifyTarget(op, op.table);
	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &del = planner.Make<ArrowNetPhysicalDelete>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	del.children.push_back(plan);
	return del;
}

PhysicalOperator &ArrowNetCatalog::PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalUpdate &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("mssql_net: UPDATE ... RETURNING is not supported yet");
	}
	auto target = BuildModifyTarget(op, op.table);
	auto names = op.table.GetColumns().GetColumnNames();
	vector<LogicalType> types;
	for (auto &col : op.table.GetColumns().Logical()) {
		types.push_back(col.Type());
	}
	for (auto &physical_index : op.columns) {
		target.set_columns.push_back(names[physical_index.index]);
		target.set_types.push_back(types[physical_index.index]);
	}
	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &upd = planner.Make<ArrowNetPhysicalUpdate>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	upd.children.push_back(plan);
	return upd;
}

} // namespace duckdb
