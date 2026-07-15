//===----------------------------------------------------------------------===//
//                         fabricator — catalog (impl)
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_partition_util.hpp"

#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_metadata.hpp"
#include "catalog/fabricator_schema_entry.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "catalog/fabricator_table_entry.hpp"
#include "dml/fabricator_ctas.hpp"
#include "dml/fabricator_insert.hpp"
#include "dml/fabricator_modify.hpp"
#include "duckdb/catalog/catalog_entry/schema_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"
#include "duckdb/parser/parsed_data/create_schema_info.hpp"
#include "duckdb/parser/parsed_data/create_table_info.hpp"
#include "duckdb/parser/parsed_data/drop_info.hpp"
#include "duckdb/common/error_data.hpp"
#include "duckdb/planner/operator/logical_create_table.hpp"
#include "duckdb/planner/parsed_data/bound_create_table_info.hpp"
#include "duckdb/planner/operator/logical_delete.hpp"
#include "duckdb/planner/operator/logical_insert.hpp"
#include "duckdb/planner/operator/logical_update.hpp"
#include "duckdb/storage/database_size.hpp"

#include <algorithm>

namespace duckdb {

// schema_filter / table_filter are now applied provider-side (the managed get_metadata returns only matching
// schemas/tables — see docs/provider-extensibility.md §3), so the catalog registers everything it discovers.

FabricatorCatalog::FabricatorCatalog(AttachedDatabase &db, string internal_name, FabricatorHandle handle, string db_path)
    : Catalog(db), handle_(handle), db_path_(std::move(db_path)) {
}

FabricatorCatalog::~FabricatorCatalog() {
	fabricator::CloseCatalog(handle_);
}

void FabricatorCatalog::LoadCatalog(ClientContext &context) {
	lock_guard<mutex> lock(schema_lock_);

	// Set the active txn + host-FS opener for the metadata discovery below: a host-FS provider (the Delta
	// folder catalog) globs the root + opens tables through DuckDB's FileSystem, which needs this context's
	// opener (secret resolution). Harmless for SQL/DAX (they ignore the opener ambient).
	FabricatorSetActiveTxn(handle_, context);

	// Detect the database collation's sort semantics once (binary => SQL Server's byte-order string sort
	// matches DuckDB, so string-keyed ORDER BY+LIMIT can be pushed). Best-effort: a failure leaves it off.
	try {
		string_order_pushable_ = FetchBinaryCollation(handle_);
	} catch (...) {
		string_order_pushable_ = false;
	}

	// Whether the provider applies pushed filters exactly => the scan may advertise filter_pushdown=true (so
	// DuckDB delivers runtime dynamic/join filters). True only for the Delta native_read catalog; default off
	// keeps the safe superset model for SQL Server / DAX / non-native Delta. See docs/multifile-delta.md §"Batch 2".
	try {
		exact_filter_pushdown_ = FetchExactFilterPushdown(handle_);
	} catch (...) {
		exact_filter_pushdown_ = false;
	}

	auto ensure_schema = [&](const string &schema_name) -> FabricatorSchemaEntry & {
		auto it = schemas_.find(schema_name);
		if (it != schemas_.end()) {
			return *it->second;
		}
		CreateSchemaInfo info;
		info.schema = schema_name;
		auto entry = make_uniq<FabricatorSchemaEntry>(*this, info, handle_);
		auto &ref = *entry;
		schemas_[schema_name] = std::move(entry);
		return ref;
	};

	// schema_filter / table_filter are applied provider-side now (DiscoverSchemas/DiscoverTables already
	// return only matches), so register everything discovered.
	for (auto &schema_name : DiscoverSchemas(handle_)) {
		ensure_schema(schema_name);
	}
	for (auto &table : DiscoverTables(handle_)) {
		ensure_schema(table.schema_name).AddTable(table.table_name, table.table_type);
	}
	// Discovered routines (scalar UDFs / TVFs / procs / custom in-out & aggregates) register onto their
	// schema — but ONLY a schema already registered above. The managed schema_filter keeps non-matching
	// schemas out of DiscoverSchemas, so skipping a function whose schema is absent applies the filter to
	// functions too (the core never re-creates a filtered-out schema). See docs/provider-extensibility.md §3.
	for (auto &func : DiscoverFunctions(handle_)) {
		auto sit = schemas_.find(func.schema_name);
		if (sit == schemas_.end()) {
			continue;
		}
		auto &schema = *sit->second;
		if (func.kind == "scalar") {
			schema.AddScalarFunction(func.name);
		} else if (func.kind == "table") {
			schema.AddTableFunction(func.name, /*is_proc=*/false);
		} else if (func.kind == "proc") {
			schema.AddTableFunction(func.name, /*is_proc=*/true);
		} else if (func.kind == "inout") {
			// Provider-authored custom table-in-out (4g, pure C#): a {TABLE}-param table function under
			// the bare name (no scalar-arg scan form, no `_each` alias — it is already in-out).
			schema.AddInOutFunction(func.name);
		} else if (func.kind == "collector") {
			// Provider-authored custom collector (pipeline breaker, pure C#): a {TABLE}-param table function
			// routed to the Sink+Source operator — buffers all input, then emits. See docs/inout-collector-mode.md.
			schema.AddCollectorFunction(func.name);
		} else if (func.kind == "aggregate") {
			// Provider-authored custom aggregate (4h, UDAF, pure C#): an AggregateFunctionCatalogEntry.
			schema.AddAggregateFunction(func.name, /*spillable=*/false);
		} else if (func.kind == "aggregate_spill") {
			// Spillable variant: state serialized into DuckDB's blob so external GROUP BY can spill to disk.
			schema.AddAggregateFunction(func.name, /*spillable=*/true);
		}
	}
}

void FabricatorCatalog::InvalidateAllEntries() {
	lock_guard<mutex> lock(schema_lock_);
	for (auto &entry : schemas_) {
		entry.second->InvalidateEntryCache();
	}
}

void FabricatorCatalog::RefreshCache(ClientContext &context) {
	lock_guard<mutex> lock(schema_lock_);

	// Set the active host-FS opener for re-discovery (see LoadCatalog).
	FabricatorSetActiveTxn(handle_, context);

	auto ensure_schema = [&](const string &schema_name) -> FabricatorSchemaEntry & {
		auto it = schemas_.find(schema_name);
		if (it != schemas_.end()) {
			return *it->second;
		}
		CreateSchemaInfo info;
		info.schema = schema_name;
		auto entry = make_uniq<FabricatorSchemaEntry>(*this, info, handle_);
		auto &ref = *entry;
		schemas_[schema_name] = std::move(entry);
		return ref;
	};

	// Drop every cached table (so dropped tables vanish + columns re-fetch), then
	// re-run discovery to pick up tables created out-of-band (e.g. via fabricator_exec).
	for (auto &entry : schemas_) {
		entry.second->ClearTables();
	}
	// schema_filter / table_filter are applied provider-side now (DiscoverSchemas/DiscoverTables already
	// return only matches), so register everything discovered.
	for (auto &schema_name : DiscoverSchemas(handle_)) {
		ensure_schema(schema_name);
	}
	for (auto &table : DiscoverTables(handle_)) {
		ensure_schema(table.schema_name).AddTable(table.table_name, table.table_type);
	}
	// Discovered routines (scalar UDFs / TVFs / procs / custom in-out & aggregates) register onto their
	// schema — but ONLY a schema already registered above. The managed schema_filter keeps non-matching
	// schemas out of DiscoverSchemas, so skipping a function whose schema is absent applies the filter to
	// functions too (the core never re-creates a filtered-out schema). See docs/provider-extensibility.md §3.
	for (auto &func : DiscoverFunctions(handle_)) {
		auto sit = schemas_.find(func.schema_name);
		if (sit == schemas_.end()) {
			continue;
		}
		auto &schema = *sit->second;
		if (func.kind == "scalar") {
			schema.AddScalarFunction(func.name);
		} else if (func.kind == "table") {
			schema.AddTableFunction(func.name, /*is_proc=*/false);
		} else if (func.kind == "proc") {
			schema.AddTableFunction(func.name, /*is_proc=*/true);
		} else if (func.kind == "inout") {
			// Provider-authored custom table-in-out (4g, pure C#): a {TABLE}-param table function under
			// the bare name (no scalar-arg scan form, no `_each` alias — it is already in-out).
			schema.AddInOutFunction(func.name);
		} else if (func.kind == "collector") {
			// Provider-authored custom collector (pipeline breaker, pure C#): a {TABLE}-param table function
			// routed to the Sink+Source operator — buffers all input, then emits. See docs/inout-collector-mode.md.
			schema.AddCollectorFunction(func.name);
		} else if (func.kind == "aggregate") {
			// Provider-authored custom aggregate (4h, UDAF, pure C#): an AggregateFunctionCatalogEntry.
			schema.AddAggregateFunction(func.name, /*spillable=*/false);
		} else if (func.kind == "aggregate_spill") {
			// Spillable variant: state serialized into DuckDB's blob so external GROUP BY can spill to disk.
			schema.AddAggregateFunction(func.name, /*spillable=*/true);
		}
	}
}

void FabricatorCatalog::Initialize(bool load_builtin) {
	// Discovery happens in LoadCatalog (called from attach, where a context exists).
}

string FabricatorCatalog::GetCatalogType() {
	return CATALOG_TYPE;
}

optional_ptr<SchemaCatalogEntry> FabricatorCatalog::LookupSchema(CatalogTransaction transaction,
                                                              const EntryLookupInfo &schema_lookup,
                                                              OnEntryNotFound if_not_found) {
	lock_guard<mutex> lock(schema_lock_);
	auto it = schemas_.find(schema_lookup.GetEntryName());
	if (it != schemas_.end()) {
		return it->second.get();
	}
	if (if_not_found == OnEntryNotFound::THROW_EXCEPTION) {
		throw BinderException("fabricator: schema \"%s\" not found", schema_lookup.GetEntryName());
	}
	return nullptr;
}

void FabricatorCatalog::ScanSchemas(ClientContext &context, std::function<void(SchemaCatalogEntry &)> callback) {
	lock_guard<mutex> lock(schema_lock_);
	for (auto &entry : schemas_) {
		callback(*entry.second);
	}
}

DatabaseSize FabricatorCatalog::GetDatabaseSize(ClientContext &context) {
	return DatabaseSize();
}

bool FabricatorCatalog::InMemory() {
	return false;
}

string FabricatorCatalog::GetDBPath() {
	return db_path_;
}

optional_ptr<CatalogEntry> FabricatorCatalog::CreateSchema(CatalogTransaction transaction, CreateSchemaInfo &info) {
	if (transaction.context) {
		FabricatorSetActiveTxn(handle_, *transaction.context);
	}
	if (info.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT) {
		fabricator::DropSchema(handle_, info.schema, /*if_exists=*/true);
	}
	bool if_not_exists = info.on_conflict == OnCreateConflict::IGNORE_ON_CONFLICT;
	fabricator::CreateSchema(handle_, info.schema, if_not_exists);

	lock_guard<mutex> lock(schema_lock_);
	auto it = schemas_.find(info.schema);
	if (it != schemas_.end()) {
		if (info.on_conflict != OnCreateConflict::REPLACE_ON_CONFLICT) {
			return it->second.get(); // already present (IGNORE / ERROR-but-cached)
		}
		schemas_.erase(it);
	}
	auto entry = make_uniq<FabricatorSchemaEntry>(*this, info, handle_);
	auto &ref = *entry;
	schemas_[info.schema] = std::move(entry);
	return &ref;
}
void FabricatorCatalog::DropSchema(ClientContext &context, DropInfo &info) {
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	FabricatorSetActiveTxn(handle_, context);
	fabricator::DropSchema(handle_, info.name, if_exists);
	lock_guard<mutex> lock(schema_lock_);
	schemas_.erase(info.name);
}
ErrorData FabricatorCatalog::SupportsCreateTable(BoundCreateTableInfo &info) {
	// Permit PARTITIONED BY (Delta partitions the data) and SORTED BY (the SQL Server provider maps it to a
	// Fabric Warehouse WITH (CLUSTER BY (cols)) layout) — the base Catalog rejects both. The WITH-options clause
	// stays unsupported.
	auto &base = info.Base().Cast<CreateTableInfo>();
	if (!base.options.empty()) {
		return ErrorData(ExceptionType::CATALOG,
		                 StringUtil::Format("WITH clause is not supported for tables in a %s catalog", GetCatalogType()));
	}
	return ErrorData();
}
PhysicalOperator &FabricatorCatalog::PlanCreateTableAs(ClientContext &context, PhysicalPlanGenerator &planner,
                                                     LogicalCreateTable &op, PhysicalOperator &plan) {
	auto &create_info = op.info->base->Cast<CreateTableInfo>();

	FabricatorCtasInfo info;
	info.schema_name = create_info.schema;
	info.table_name = create_info.table;
	for (auto &col : create_info.columns.Logical()) {
		info.column_names.push_back(col.Name());
		info.column_types.push_back(col.Type());
	}
	info.replace = op.info->base->on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT;
	info.partition_columns = fabricator::PartitionColumnsArg(create_info.partition_keys); // native PARTITIONED BY
	info.sort_columns = fabricator::PartitionColumnsArg(create_info.sort_keys);           // native SORTED BY (→ CLUSTER BY)
	info.handle = handle_;
	info.schema_entry = &op.schema.Cast<FabricatorSchemaEntry>();

	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &ctas = planner.Make<FabricatorPhysicalCreateTableAs>(std::move(result_types), op.estimated_cardinality,
	                                                         std::move(info));
	ctas.children.push_back(plan);
	return ctas;
}
PhysicalOperator &FabricatorCatalog::PlanInsert(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalInsert &op, optional_ptr<PhysicalOperator> plan) {
	FabricatorInsertTarget target;
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
	auto &insert = planner.Make<FabricatorPhysicalInsert>(std::move(result_types), op.estimated_cardinality,
	                                                    std::move(target), handle_);
	if (plan) {
		insert.children.push_back(*plan);
	}
	return insert;
}
static FabricatorModifyTarget BuildModifyTarget(LogicalOperator &, TableCatalogEntry &table) {
	auto &entry = table.Cast<FabricatorTableEntry>();
	if (!entry.HasRowId()) {
		throw BinderException(
		    "fabricator: UPDATE/DELETE requires a table with a primary key or unique index for row identity. "
		    "Table '%s' has neither (use fabricator_exec for set-based UPDATE/DELETE)",
		    entry.name);
	}
	FabricatorModifyTarget target;
	target.schema_name = entry.schema.name;
	target.table_name = entry.name;
	if (entry.HasVirtualRowId()) {
		// Virtual rowid (Delta `_metadata.row_id`): the key columns are provider-supplied, not in the schema.
		const auto &rowid_type = entry.RowIdType();
		const auto &vnames = entry.VirtualRowIdColumns();
		for (idx_t i = 0; i < vnames.size(); i++) {
			target.rowid_columns.push_back(vnames[i]);
			target.rowid_types.push_back(rowid_type.id() == LogicalTypeId::STRUCT
			                                 ? StructType::GetChildType(rowid_type, i)
			                                 : rowid_type);
		}
		return target;
	}
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

PhysicalOperator &FabricatorCatalog::PlanDelete(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalDelete &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("fabricator: DELETE ... RETURNING is not supported yet");
	}
	auto target = BuildModifyTarget(op, op.table);
	// The rowid's position in the child chunk comes from the bound row-identifier expression — NOT
	// "the last column": a mark-join DELETE (WHERE x [NOT] IN (subquery)) feeds the raw FILTER output
	// [cols..., rowid, mark] into the sink, so the last column is the BOOLEAN mark.
	if (!op.expressions.empty() && op.expressions[0]->GetExpressionType() == ExpressionType::BOUND_REF) {
		target.rowid_child_index = op.expressions[0]->Cast<BoundReferenceExpression>().index;
	}
	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &del = planner.Make<FabricatorPhysicalDelete>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	del.children.push_back(plan);
	return del;
}

PhysicalOperator &FabricatorCatalog::PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalUpdate &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("fabricator: UPDATE ... RETURNING is not supported yet");
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
	auto &upd = planner.Make<FabricatorPhysicalUpdate>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	upd.children.push_back(plan);
	return upd;
}

} // namespace duckdb
