//===----------------------------------------------------------------------===//
//                         mssql_net — catalog (impl)
//===----------------------------------------------------------------------===//

#include "catalog/mssql_net_catalog.hpp"

#include "arrownet/clr_host.hpp"
#include "catalog/mssql_net_metadata.hpp"
#include "catalog/mssql_net_schema_entry.hpp"
#include "catalog/mssql_net_table_entry.hpp"
#include "dml/mssql_net_ctas.hpp"
#include "dml/mssql_net_insert.hpp"
#include "dml/mssql_net_modify.hpp"
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

namespace duckdb {

MssqlNetCatalog::MssqlNetCatalog(AttachedDatabase &db, string internal_name, ArrowNetHandle handle, string db_path)
    : Catalog(db), handle_(handle), db_path_(std::move(db_path)) {
}

MssqlNetCatalog::~MssqlNetCatalog() {
	arrownet::CloseCatalog(handle_);
}

void MssqlNetCatalog::LoadCatalog(ClientContext &context) {
	lock_guard<mutex> lock(schema_lock_);

	auto ensure_schema = [&](const string &schema_name) -> MssqlNetSchemaEntry & {
		auto it = schemas_.find(schema_name);
		if (it != schemas_.end()) {
			return *it->second;
		}
		CreateSchemaInfo info;
		info.schema = schema_name;
		auto entry = make_uniq<MssqlNetSchemaEntry>(*this, info, handle_);
		auto &ref = *entry;
		schemas_[schema_name] = std::move(entry);
		return ref;
	};

	for (auto &schema_name : DiscoverSchemas(handle_)) {
		ensure_schema(schema_name);
	}
	for (auto &table : DiscoverTables(handle_)) {
		ensure_schema(table.schema_name).AddTable(table.table_name, table.table_type);
	}
}

void MssqlNetCatalog::RefreshCache(ClientContext &context) {
	lock_guard<mutex> lock(schema_lock_);

	auto ensure_schema = [&](const string &schema_name) -> MssqlNetSchemaEntry & {
		auto it = schemas_.find(schema_name);
		if (it != schemas_.end()) {
			return *it->second;
		}
		CreateSchemaInfo info;
		info.schema = schema_name;
		auto entry = make_uniq<MssqlNetSchemaEntry>(*this, info, handle_);
		auto &ref = *entry;
		schemas_[schema_name] = std::move(entry);
		return ref;
	};

	// Drop every cached table (so dropped tables vanish + columns re-fetch), then
	// re-run discovery to pick up tables created out-of-band (e.g. via mssql_net_exec).
	for (auto &entry : schemas_) {
		entry.second->ClearTables();
	}
	for (auto &schema_name : DiscoverSchemas(handle_)) {
		ensure_schema(schema_name);
	}
	for (auto &table : DiscoverTables(handle_)) {
		ensure_schema(table.schema_name).AddTable(table.table_name, table.table_type);
	}
}

void MssqlNetCatalog::Initialize(bool load_builtin) {
	// Discovery happens in LoadCatalog (called from attach, where a context exists).
}

string MssqlNetCatalog::GetCatalogType() {
	return "mssql_net";
}

optional_ptr<SchemaCatalogEntry> MssqlNetCatalog::LookupSchema(CatalogTransaction transaction,
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

void MssqlNetCatalog::ScanSchemas(ClientContext &context, std::function<void(SchemaCatalogEntry &)> callback) {
	lock_guard<mutex> lock(schema_lock_);
	for (auto &entry : schemas_) {
		callback(*entry.second);
	}
}

DatabaseSize MssqlNetCatalog::GetDatabaseSize(ClientContext &context) {
	return DatabaseSize();
}

bool MssqlNetCatalog::InMemory() {
	return false;
}

string MssqlNetCatalog::GetDBPath() {
	return db_path_;
}

optional_ptr<CatalogEntry> MssqlNetCatalog::CreateSchema(CatalogTransaction transaction, CreateSchemaInfo &info) {
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
	auto entry = make_uniq<MssqlNetSchemaEntry>(*this, info, handle_);
	auto &ref = *entry;
	schemas_[info.schema] = std::move(entry);
	return &ref;
}
void MssqlNetCatalog::DropSchema(ClientContext &context, DropInfo &info) {
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	arrownet::DropSchema(handle_, info.name, if_exists);
	lock_guard<mutex> lock(schema_lock_);
	schemas_.erase(info.name);
}
PhysicalOperator &MssqlNetCatalog::PlanCreateTableAs(ClientContext &context, PhysicalPlanGenerator &planner,
                                                     LogicalCreateTable &op, PhysicalOperator &plan) {
	auto &create_info = op.info->base->Cast<CreateTableInfo>();

	MssqlNetCtasInfo info;
	info.schema_name = create_info.schema;
	info.table_name = create_info.table;
	for (auto &col : create_info.columns.Logical()) {
		info.column_names.push_back(col.Name());
		info.column_types.push_back(col.Type());
	}
	info.replace = op.info->base->on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT;
	info.handle = handle_;
	info.schema_entry = &op.schema.Cast<MssqlNetSchemaEntry>();

	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &ctas = planner.Make<MssqlNetPhysicalCreateTableAs>(std::move(result_types), op.estimated_cardinality,
	                                                         std::move(info));
	ctas.children.push_back(plan);
	return ctas;
}
PhysicalOperator &MssqlNetCatalog::PlanInsert(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalInsert &op, optional_ptr<PhysicalOperator> plan) {
	MssqlNetInsertTarget target;
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
	auto &insert = planner.Make<MssqlNetPhysicalInsert>(std::move(result_types), op.estimated_cardinality,
	                                                    std::move(target), handle_);
	if (plan) {
		insert.children.push_back(*plan);
	}
	return insert;
}
static MssqlNetModifyTarget BuildModifyTarget(LogicalOperator &, TableCatalogEntry &table) {
	auto &entry = table.Cast<MssqlNetTableEntry>();
	if (!entry.HasRowId()) {
		throw BinderException(
		    "mssql_net: UPDATE/DELETE requires a table with a primary key or unique index for row identity. "
		    "Table '%s' has neither (use mssql_net_exec for set-based UPDATE/DELETE)",
		    entry.name);
	}
	MssqlNetModifyTarget target;
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

PhysicalOperator &MssqlNetCatalog::PlanDelete(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalDelete &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("mssql_net: DELETE ... RETURNING is not supported yet");
	}
	auto target = BuildModifyTarget(op, op.table);
	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &del = planner.Make<MssqlNetPhysicalDelete>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	del.children.push_back(plan);
	return del;
}

PhysicalOperator &MssqlNetCatalog::PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner,
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
	auto &upd = planner.Make<MssqlNetPhysicalUpdate>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	upd.children.push_back(plan);
	return upd;
}

} // namespace duckdb
