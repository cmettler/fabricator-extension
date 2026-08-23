//===----------------------------------------------------------------------===//
//                         fabricator — catalog (impl)
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_partition_util.hpp"

#include <regex>

#include "fabricator/arrow_ingest.hpp"
#include "fabricator/clr_host.hpp"
#include "duckdb/execution/operator/scan/physical_table_scan.hpp"
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
#include "duckdb/parallel/task_scheduler.hpp"
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

// schema_filter / table_filter are now applied provider-side (the managed discovery entries return only matching
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

	// The provider's init hook (ABI v78) — FIRST crossing after the ambients, before any discovery. This is
	// the only place a provider can do setup that needs a client context: open_catalog runs with NO ambients
	// (it only constructs), so before this hook existed such work had to hang off whichever discovery
	// crossing ran first — and the order below is not part of the contract. In practice get_capabilities
	// became the de-facto init hook by accident of being first, which is how SQL Server's first CONNECT came
	// to happen inside a call documented as reading a doc of booleans.
	//
	// ⚠ NOT wrapped in a catch, unlike the capabilities read directly below: an init failure is the provider
	// declining the catalog, and the ATTACH is where that must surface. FabricatorAttach wraps it into the
	// "connection validation failed" IOException, so no catalog is created.
	DUCKDB_LOG_DEBUG(context, "fabricator: catalog_init");
	fabricator::CatalogInit(handle_);

	// The catalog's capability doc, read ONCE here (ABI v71): `is_binary_collation` => SQL Server's
	// byte-order string sort matches DuckDB, so string-keyed ORDER BY+LIMIT can be pushed;
	// `exact_filter_pushdown` => the provider applies pushed filters exactly, so the scan may advertise
	// filter_pushdown=true (DuckDB then delivers runtime dynamic/join filters) — true only for the Delta
	// catalog in Exact pushdown mode; see docs/multifile-delta.md §"Batch 2". Best-effort: a failed
	// crossing leaves every capability off (the safe defaults — pushdown stays superset-and-re-apply).
	try {
		auto caps = FetchCapabilities(handle_);
		string_order_pushable_ = caps.string_order_pushable;
		exact_filter_pushdown_ = caps.exact_filter_pushdown;
		null_order_expressible_ = caps.null_order_expressible;
	} catch (std::exception &ex) {
		string_order_pushable_ = false;
		exact_filter_pushdown_ = false;
		null_order_expressible_ = false;
		// ⚠ IT WARNS NOW, and the silence was the defect. This catch guards TWO unrelated things: a provider
		// that cannot answer (fine — defaults are the safe direction, superset-and-re-apply) and a TRANSIENT
		// failure of whatever the provider needs to answer. The second one used to disable string ORDER BY
		// pushdown and exact filter pushdown for the CATALOG'S WHOLE LIFE, with no signal anywhere — a
		// permanent, invisible performance regression from a momentary blip. Deliberately still not fatal:
		// the defaults are CORRECT, just slower, so turning a degradation into a failed ATTACH would be a
		// worse trade. Made visible instead of made fatal.
		DUCKDB_LOG_WARNING(context, StringUtil::Format(
		                                "fabricator: capability detection failed for catalog '%s' — every "
		                                "capability is off for this attach (pushdown stays superset-and-"
		                                "re-apply, string ORDER BY+LIMIT is not pushed): %s",
		                                GetName(), ex.what()));
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
		} else if (func.kind == "table_sql") {
			// Provider-authored SQL-GENERATING table function (v68): registered with bind_replace only — the
			// call `db.schema.fn(args)` is rewritten into the provider's generated SQL at bind time, so the
			// referenced scans keep their own pushdown and no data crosses the bridge at execution.
			schema.AddSqlTableFunction(func.name);
		} else if (func.kind == "inout") {
			// Provider-authored custom table-in-out (4g, pure C#): a {TABLE}-param table function under
			// the bare name (no scalar-arg scan form, no `_each` alias — it is already in-out).
			schema.AddInOutFunction(func.name);
		} else if (func.kind == "lateral") {
			// Provider-authored ROW-MAPPED (correlated LATERAL) function: its POSITIONAL parameters are real
			// value types and no {TABLE} marker, so `db.schema.fn(t.a, t.b)` binds against an outer relation.
			// See catalog/fabricator_lateral.hpp.
			schema.AddLateralFunction(func.name);
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
	// Provider-declared CATALOG-BOUND macros. Their own dedicated entry (catalog_macros), NOT a column on the
	// functions stream: that stream is provider SQL run on the server, and a macro body is a purely local
	// declaration. Same schema rule as functions (only a schema registered above), so an ATTACH
	// schema_filter gates macros too.
	// DiscoverCatalogMacros never throws — declaring macros is optional.
	for (auto &macro : DiscoverCatalogMacros(handle_)) {
		auto sit = schemas_.find(macro.schema_name);
		if (sit == schemas_.end()) {
			continue;
		}
		sit->second->AddMacro(macro.name, macro.create_sql);
	}
	// Provider-declared CATALOG-BOUND views (catalog_views). Same local-declaration contract and the same
	// schema rule as macros. ⚠ Registered AFTER the tables so AddView can see the discovered names and
	// report a collision — see FabricatorSchemaEntry::AddView for why a collision is refused at LOOKUP
	// rather than here.
	for (auto &view : DiscoverCatalogViews(handle_)) {
		auto sit = schemas_.find(view.schema_name);
		if (sit == schemas_.end()) {
			continue;
		}
		sit->second->AddView(view.name, view.create_sql);
	}
}

void FabricatorCatalog::InvalidateAllEntries() {
	lock_guard<mutex> lock(schema_lock_);
	for (auto &entry : schemas_) {
		entry.second->InvalidateEntryCache();
	}
}

void FabricatorCatalog::InvalidateMatching(const string &pattern) {
	std::regex re;
	try {
		re = std::regex(pattern, std::regex::ECMAScript | std::regex::icase);
	} catch (const std::regex_error &e) {
		throw InvalidInputException("fabricator_invalidate_cache: invalid name pattern '%s': %s", pattern, e.what());
	}
	lock_guard<mutex> lock(schema_lock_);
	for (auto &entry : schemas_) {
		entry.second->InvalidateMatching([&re](const string &n) { return std::regex_search(n, re); });
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
		} else if (func.kind == "table_sql") {
			// Provider-authored SQL-GENERATING table function (v68): registered with bind_replace only — the
			// call `db.schema.fn(args)` is rewritten into the provider's generated SQL at bind time, so the
			// referenced scans keep their own pushdown and no data crosses the bridge at execution.
			schema.AddSqlTableFunction(func.name);
		} else if (func.kind == "inout") {
			// Provider-authored custom table-in-out (4g, pure C#): a {TABLE}-param table function under
			// the bare name (no scalar-arg scan form, no `_each` alias — it is already in-out).
			schema.AddInOutFunction(func.name);
		} else if (func.kind == "lateral") {
			// Provider-authored ROW-MAPPED (correlated LATERAL) function: its POSITIONAL parameters are real
			// value types and no {TABLE} marker, so `db.schema.fn(t.a, t.b)` binds against an outer relation.
			// See catalog/fabricator_lateral.hpp.
			schema.AddLateralFunction(func.name);
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
	// Provider-declared CATALOG-BOUND macros. Their own dedicated entry (catalog_macros), NOT a column on the
	// functions stream: that stream is provider SQL run on the server, and a macro body is a purely local
	// declaration. Same schema rule as functions (only a schema registered above), so an ATTACH
	// schema_filter gates macros too.
	// DiscoverCatalogMacros never throws — declaring macros is optional.
	for (auto &macro : DiscoverCatalogMacros(handle_)) {
		auto sit = schemas_.find(macro.schema_name);
		if (sit == schemas_.end()) {
			continue;
		}
		sit->second->AddMacro(macro.name, macro.create_sql);
	}
	// Provider-declared CATALOG-BOUND views (catalog_views). Same local-declaration contract and the same
	// schema rule as macros. ⚠ Registered AFTER the tables so AddView can see the discovered names and
	// report a collision — see FabricatorSchemaEntry::AddView for why a collision is refused at LOOKUP
	// rather than here.
	for (auto &view : DiscoverCatalogViews(handle_)) {
		auto sit = schemas_.find(view.schema_name);
		if (sit == schemas_.end()) {
			continue;
		}
		sit->second->AddView(view.name, view.create_sql);
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
		retired_schemas_.push_back(std::move(it->second)); // retire, never destroy (see header)
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
	auto it = schemas_.find(info.name);
	if (it != schemas_.end()) {
		retired_schemas_.push_back(std::move(it->second)); // retire, never destroy (see header)
		schemas_.erase(it);
	}
}
ErrorData FabricatorCatalog::SupportsCreateTable(BoundCreateTableInfo &info) {
	// Permit PARTITIONED BY (Delta partitions the data), SORTED BY (SQL Server maps it to a Fabric Warehouse
	// WITH (CLUSTER BY (cols)) layout), AND the WITH (key='value', ...) options clause — the options cross the
	// ABI as a flat JSON object (create_table/begin_bulk `options_json`, v67) and the PROVIDER parses the keys
	// it knows (unknown keys are rejected provider-side, never silently ignored). The base Catalog rejects all
	// three clauses.
	return ErrorData();
}
void FabricatorCatalog::MarkSinkOnOwnScans(PhysicalOperator &plan, const string &sink_schema,
                                           const string &sink_table, const string &sink_kind) {
	if (plan.type == PhysicalOperatorType::TABLE_SCAN) {
		auto &scan = plan.Cast<PhysicalTableScan>();
		// dynamic_cast, not Cast<>: the plan may hold ANY table function's bind data (read_parquet, a
		// custom TVF, another catalog's scan), and only ours carries an ArrowStreamBindData.
		auto *bind_data = dynamic_cast<fabricator::ArrowStreamBindData *>(scan.bind_data.get());
		// `table` is null for a raw fabricator_query scan, which belongs to no catalog and therefore
		// cannot be the sink's own — leave it streaming.
		if (bind_data && bind_data->table && &bind_data->table->schema.catalog == this) {
			bind_data->sink_schema = sink_schema;
			bind_data->sink_table = sink_table;
			bind_data->sink_kind = sink_kind;
		}
	}
	for (auto &child : plan.children) {
		MarkSinkOnOwnScans(child.get(), sink_schema, sink_table, sink_kind);
	}
}

//! Whether a WRITE sink may run on several tasks at once.
//!
//! ⚠ THE FLAG GOVERNS THE WHOLE PIPELINE, NOT ONLY THE SINK. `Pipeline::ScheduleParallel` tests
//! `!sink->ParallelSink()` FIRST — before the source, the intermediate operators or `MaxThreads()` — and falls
//! through to `ScheduleSequentialTask`. So a serial sink puts the scan, every projection and the sort on ONE
//! task, which is why every write into a fabricator table used to be flat in `SET threads` while the same work
//! streaming to the client scaled (docs/scan-concurrency.md §7a: 2919/2952 ms vs 2730 -> 1227 ms).
static bool FabricatorParallelWrite(ClientContext &context, optional_ptr<PhysicalOperator> plan) {
	if (!plan) {
		return false; // INSERT ... VALUES: no source pipeline to parallelize.
	}
	if (TaskScheduler::GetScheduler(context).NumberOfThreads() <= 1) {
		return false;
	}
	// ⚠ WE DELIBERATELY DO NOT CONSULT `preserve_insertion_order`, where DuckDB's own PlanInsert does, and the
	// difference is a property of the TARGET rather than a liberty taken. That setting is about the order of a
	// RESULT handed to a client, and DuckDB's own inserts must honour it because its storage IS ordered. A
	// fabricator table has no insertion order to preserve: a scan of it returns rows in whatever order the
	// provider yields (Delta reads its active files in listing order; a T-SQL SELECT without ORDER BY promises
	// nothing). So the only ordering that has to survive a write here is one the PLAN states explicitly — which
	// is exactly FIXED_ORDER, and is what the test below keeps serial.
	//
	// What a parallel sink costs even so, stated rather than implied: a table that was getting its file
	// clustering INCIDENTALLY from source order stops getting it, because the provider cuts output files at
	// batch boundaries and batches now interleave across tasks. That costs pruning quality, never a wrong
	// answer. A DECLARED ordering is unaffected — SORTED BY / the `fabricator.sortedBy` property / a clustered
	// Delta table are imposed by the provider DOWNSTREAM of the channel these tasks feed, so producers
	// interleaving upstream of it cannot disturb an ordering applied afterwards.
	return PhysicalPlanGenerator::OrderPreservationRecursive(*plan) != OrderPreservationType::FIXED_ORDER;
}

PhysicalOperator &FabricatorCatalog::PlanCreateTableAs(ClientContext &context, PhysicalPlanGenerator &planner,
                                                     LogicalCreateTable &op, PhysicalOperator &plan) {
	auto &create_info = op.info->base->Cast<CreateTableInfo>();
	MarkSinkOnOwnScans(plan, create_info.schema, create_info.table, "create");

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
	info.options_json = fabricator::TableOptionsArg(create_info.options);                 // WITH (key='value', ...)
	info.handle = handle_;
	info.schema_entry = &op.schema.Cast<FabricatorSchemaEntry>();
	info.parallel = FabricatorParallelWrite(context, &plan);

	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &ctas = planner.Make<FabricatorPhysicalCreateTableAs>(std::move(result_types), op.estimated_cardinality,
	                                                         std::move(info));
	ctas.children.push_back(plan);
	return ctas;
}
PhysicalOperator &FabricatorCatalog::PlanInsert(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalInsert &op, optional_ptr<PhysicalOperator> plan) {
	// INSERT ... SELECT reading this same catalog: name the sink on those scans so the provider can decide
	// whether to drain them. `plan` is null for INSERT ... VALUES, which reads nothing and needs no mark.
	if (plan) {
		MarkSinkOnOwnScans(*plan, op.table.schema.name, op.table.name, "insert");
	}
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
	// RETURNING stays serial: the OUTPUT rows would come back in an arbitrary order, and the accumulating
	// producer they land in is shared across sink threads.
	target.parallel = !op.return_chunk && FabricatorParallelWrite(context, plan);

	vector<LogicalType> result_types = op.return_chunk ? op.types : vector<LogicalType> {LogicalType::BIGINT};
	auto &insert = planner.Make<FabricatorPhysicalInsert>(std::move(result_types), op.estimated_cardinality,
	                                                    std::move(target), handle_);
	if (plan) {
		insert.children.push_back(*plan);
	}
	return insert;
}
FabricatorModifyTarget FabricatorBuildModifyTarget(TableCatalogEntry &table) {
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
	auto target = FabricatorBuildModifyTarget(op.table);
	// The rowid's position in the child chunk comes from the bound row-identifier expression — NOT
	// "the last column": a mark-join DELETE (WHERE x [NOT] IN (subquery)) feeds the raw FILTER output
	// [cols..., rowid, mark] into the sink, so the last column is the BOOLEAN mark.
	if (!op.expressions.empty() && op.expressions[0]->GetExpressionType() == ExpressionType::BOUND_REF) {
		target.rowid_child_index = op.expressions[0]->Cast<BoundReferenceExpression>().index;
	}
	// DuckDB's OWN PhysicalDelete/PhysicalUpdate declare ParallelSink() == true UNCONDITIONALLY, which is the
	// precedent here rather than something to argue with; we keep the same plan-time gate as the write sinks so
	// the four cannot drift. What parallelizes is the scan, the filter and the rowid append — the provider work
	// happens once at Finalize either way.
	target.parallel = FabricatorParallelWrite(context, &plan);
	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &del = planner.Make<FabricatorPhysicalDelete>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	del.children.push_back(plan);
	return del;
}

// Fills the SET half of an UPDATE target: which columns are assigned, and WHERE in the child chunk each
// assigned value lives. Shared by a plain UPDATE and a MERGE's WHEN MATCHED THEN UPDATE, because the
// position rule is identical for both — it is whatever the bound expression says, never the ordinal.
void FabricatorFillUpdateSetColumns(TableCatalogEntry &table, const vector<PhysicalIndex> &columns,
                                    const vector<unique_ptr<Expression>> &expressions,
                                    FabricatorModifyTarget &target) {
	D_ASSERT(columns.size() == expressions.size());
	auto names = table.GetColumns().GetColumnNames();
	vector<LogicalType> types;
	for (auto &col : table.GetColumns().Logical()) {
		types.push_back(col.Type());
	}
	for (idx_t i = 0; i < columns.size(); i++) {
		auto &expr = *expressions[i];
		const auto &col_name = names[columns[i].index];
		if (expr.GetExpressionType() == ExpressionType::VALUE_DEFAULT) {
			// Upstream's PhysicalUpdate evaluates the column's bound default here. We do not carry the
			// defaults into the operator, and reading the ordinal instead is what USED to happen: a DEFAULT
			// contributes no projection column, so every later SET value silently shifted one position
			// (measured: an INTERNAL error + a fatally invalidated database when the shifted types differ,
			// and WRONG DATA committed with exit 0 when they coincide). Refuse instead.
			throw NotImplementedException(
			    "fabricator: UPDATE ... SET %s = DEFAULT is not supported — write the default value "
			    "explicitly",
			    col_name);
		}
		if (expr.GetExpressionType() != ExpressionType::BOUND_REF) {
			// The binder always emits a reference into its own projection (Binder::BindUpdateSet), and the
			// column-binding resolver rewrites it to a BOUND_REF before physical planning. Anything else
			// would mean reading an unrelated column as this one's new value.
			throw NotImplementedException("fabricator: unsupported UPDATE assignment for column %s (%s)", col_name,
			                              expr.ToString());
		}
		target.set_columns.push_back(col_name);
		target.set_types.push_back(types[columns[i].index]);
		target.set_child_indices.push_back(expr.Cast<BoundReferenceExpression>().index);
	}
}

PhysicalOperator &FabricatorCatalog::PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalUpdate &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("fabricator: UPDATE ... RETURNING is not supported yet");
	}
	auto target = FabricatorBuildModifyTarget(op.table);
	FabricatorFillUpdateSetColumns(op.table, op.columns, op.expressions, target);
	// ⚠ THE ONE THING A PARALLEL UPDATE CHANGES BEYOND SPEED: `ExecuteUpdate` keys its post-image dictionary by
	// rowid and is LAST-WRITE-WINS, so a plan whose join matches ONE target row twice (`UPDATE … FROM other`)
	// currently resolves in the order the sink happened to see the batches, and will now resolve in an
	// arbitrary one. That order was never a promise — it is a hash join's probe order — and DuckDB accepts the
	// same nondeterminism in its own parallel PhysicalUpdate. (Its ON CONFLICT DO UPDATE path is the one that
	// serializes, and for a different reason: it must DETECT the double update in order to error.)
	target.parallel = FabricatorParallelWrite(context, &plan);
	vector<LogicalType> result_types {LogicalType::BIGINT};
	auto &upd = planner.Make<FabricatorPhysicalUpdate>(std::move(result_types), op.estimated_cardinality,
	                                                 std::move(target), handle_);
	upd.children.push_back(plan);
	return upd;
}

} // namespace duckdb
