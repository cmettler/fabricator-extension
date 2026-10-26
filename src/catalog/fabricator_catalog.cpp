// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — catalog (impl)
//===----------------------------------------------------------------------===//

#include "duckdb/logging/logger.hpp" // 2.0: DUCKDB_LOG_* macros live here
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
#include "duckdb/planner/operator/logical_filter.hpp"
#include "duckdb/planner/expression/bound_columnref_expression.hpp"
#include "duckdb/planner/table_filter_set.hpp"
#include "duckdb/planner/expression_iterator.hpp"
#include "duckdb/common/enums/logical_operator_type.hpp"
#include "duckdb/common/printer.hpp"
#include <cstdlib>
#include "duckdb/planner/operator/logical_get.hpp"
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

FabricatorSchemaEntry &FabricatorCatalog::EnsureSchemaPathLocked(const vector<string> &path) {
	// Level 0 lives in the catalog's own map; every deeper level is a CHILD of the one before it. The two
	// identical lambdas this replaced only ever did the first half, because every provider emitted
	// single-element paths -- which is still true of every provider that does not declare
	// supports_nested_schemas, so for them this is byte-for-byte the old behaviour.
	const string root_name = path.empty() ? string() : path.front();
	FabricatorSchemaEntry *current;
	auto it = schemas_.find(root_name);
	if (it != schemas_.end()) {
		current = it->second.get();
	} else {
		CreateSchemaInfo info;
		info.SetSchema(Identifier(root_name));
		auto entry = make_uniq<FabricatorSchemaEntry>(*this, info, handle_);
		current = entry.get();
		schemas_[root_name] = std::move(entry);
	}
	for (idx_t i = 1; i < path.size(); i++) {
		current = &current->GetOrCreateChildSchema(path[i]);
	}
	return *current;
}

FabricatorSchemaEntry *FabricatorCatalog::FindSchemaPathLocked(const vector<string> &path) {
	if (path.empty()) {
		return nullptr;
	}
	auto it = schemas_.find(path.front());
	if (it == schemas_.end()) {
		return nullptr;
	}
	FabricatorSchemaEntry *current = it->second.get();
	for (idx_t i = 1; i < path.size() && current; i++) {
		current = current->FindChildSchema(path[i]);
	}
	return current;
}

void FabricatorCatalog::RegisterDeclarationsLocked() {
	// Discovered routines (scalar UDFs / TVFs / procs / custom in-out & aggregates), macros and views register
	// onto the schema at their PATH (ABI v97) — but ONLY a schema already registered by discovery. The managed
	// schema_filter keeps non-matching schemas out of DiscoverSchemas, so skipping a declaration whose schema is
	// absent applies the filter to all three (the core never re-creates a filtered-out schema). See
	// docs/provider-extensibility.md §3.
	//
	// ⚠ FOUND BY PATH, NEVER BY LEAF. Until v97 these looked up `schemas_` by a single name, so a declaration
	// could only ever land at the top level, and under nested schemas the leaf is exactly what is NOT unique.
	for (auto &func : DiscoverFunctions(handle_)) {
		auto schema_ptr = FindSchemaPathLocked(func.schema_path);
		if (!schema_ptr) {
			continue;
		}
		auto &schema = *schema_ptr;
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
	// functions stream: that stream may be provider SQL run on the server, and a macro body is a purely local
	// declaration. DiscoverCatalogMacros never throws — declaring macros is optional.
	for (auto &macro : DiscoverCatalogMacros(handle_)) {
		if (auto schema = FindSchemaPathLocked(macro.schema_path)) {
			schema->AddMacro(macro.name, macro.create_sql);
		}
	}
	// Provider-declared CATALOG-BOUND views (catalog_views). Same local-declaration contract and the same
	// schema rule as macros. ⚠ Registered AFTER the tables so AddView can see the discovered names and
	// report a collision — see FabricatorSchemaEntry::AddView for why a collision is refused at LOOKUP
	// rather than here.
	for (auto &view : DiscoverCatalogViews(handle_)) {
		if (auto schema = FindSchemaPathLocked(view.schema_path)) {
			schema->AddView(view.name, view.create_sql);
		}
	}
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
		supports_nested_schemas_ = caps.supports_nested_schemas;
		remote_select_ = caps.remote_select;
	} catch (std::exception &ex) {
		string_order_pushable_ = false;
		exact_filter_pushdown_ = false;
		null_order_expressible_ = false;
		supports_nested_schemas_ = false;
		remote_select_ = false;
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

	// schema_filter / table_filter are applied provider-side now (DiscoverSchemas/DiscoverTables already
	// return only matches), so register everything discovered.
	for (auto &schema_path : DiscoverSchemaPaths(handle_)) {
		EnsureSchemaPathLocked(schema_path);
	}
	for (auto &table : DiscoverTables(handle_)) {
		EnsureSchemaPathLocked(table.schema_path).AddTable(table.table_name, table.table_type);
	}
	RegisterDeclarationsLocked();
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

	// Drop every cached table (so dropped tables vanish + columns re-fetch), then
	// re-run discovery to pick up tables created out-of-band (e.g. via fabricator_exec).
	for (auto &entry : schemas_) {
		entry.second->ClearTables();
	}
	// schema_filter / table_filter are applied provider-side now (DiscoverSchemas/DiscoverTables already
	// return only matches), so register everything discovered.
	for (auto &schema_path : DiscoverSchemaPaths(handle_)) {
		EnsureSchemaPathLocked(schema_path);
	}
	for (auto &table : DiscoverTables(handle_)) {
		EnsureSchemaPathLocked(table.schema_path).AddTable(table.table_name, table.table_type);
	}
	RegisterDeclarationsLocked();
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
	// ⚠⚠ THE WALK IS THIS FUNCTION'S JOB, not the caller's — `DuckCatalog::LookupSchema` does exactly the
	// same delegation, and omitting it is what made a depth-3 reference unresolvable. MEASURED before the
	// fix: `cat.a.b.t` reported *schema "a.b" does not exist* while `cat.a` and `cat.s.t` both resolved,
	// because a straight `schemas_.find()` answers only the FIRST level and every deeper one needs
	// `FabricatorSchemaEntry::LookupEntry(SCHEMA_ENTRY)` per level. A two-level layout cannot see it: its
	// lookups never go past the root, which is why slices 1 and 2a stayed green while this was missing.
	auto resolved = LookupSchemaPath(transaction, schema_lookup, OnEntryNotFound::RETURN_NULL,
	                                 [&](const EntryLookupInfo &root) -> optional_ptr<CatalogEntry> {
		                                 auto it = schemas_.find(root.GetEntryName());
		                                 return it == schemas_.end() ? nullptr
		                                                             : optional_ptr<CatalogEntry>(it->second.get());
	                                 });
	if (resolved) {
		return resolved;
	}
	if (if_not_found == OnEntryNotFound::THROW_EXCEPTION) {
		// Our own message, kept rather than letting LookupSchemaPath throw DuckDB's MissingEntry: suites
		// pin this text, and the walk is asked for RETURN_NULL precisely so the choice stays here.
		throw BinderException("fabricator: schema \"%s\" not found", schema_lookup.GetEntryName());
	}
	return nullptr;
}

void FabricatorCatalog::ScanSchemas(ClientContext &context, std::function<void(SchemaCatalogEntry &)> callback) {
	lock_guard<mutex> lock(schema_lock_);
	// ⚠ THE WHOLE TREE, not just the top level — `duckdb_schemas()` and `information_schema.schemata` are
	// built on this, so reporting only the roots would make a nested schema queryable (the binder walks
	// LookupEntry) yet INVISIBLE to every listing. DuckCatalog delegates the same walk to
	// SchemaCatalogEntry::ScanSchemaTree; ours recurses over child_schemas_ directly, which keeps the
	// traversal inside the lock we already hold and does not depend on our own Scan() reporting
	// SCHEMA_ENTRY. Depth is bounded by the provider's own path depth.
	std::function<void(FabricatorSchemaEntry &)> visit = [&](FabricatorSchemaEntry &schema) {
		callback(schema);
		for (auto &child : schema.ChildSchemas()) {
			visit(*child.second);
		}
	};
	for (auto &entry : schemas_) {
		visit(*entry.second);
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
	// The NEW schema's OWN path: the levels between the catalog and it, plus its own name. DuckDB offers
	// `CREATE SCHEMA a.b` the moment SupportsNestedSchemas() is true, and since v92 create_schema carries
	// the whole path, so the provider creates the last level inside the ones before it.
	// ⚠ `ParentSchemas()` is DuckDB's own reader of the qualified name (it skips the catalog AND the empty
	// trailing slot) — do not re-derive it from Path() by hand; the shape differs with and without nesting,
	// which is exactly the trap DropSchema below records.
	vector<string> new_path;
	for (auto &parent : info.ParentSchemas()) {
		new_path.push_back(parent.GetIdentifierName());
	}
	new_path.push_back(info.GetQualifiedName().Schema().GetIdentifierName());

	if (transaction.context) {
		FabricatorSetActiveTxn(handle_, *transaction.context);
	}
	if (info.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT) {
		fabricator::DropSchema(handle_, new_path, /*if_exists=*/true);
	}
	bool if_not_exists = info.on_conflict == OnCreateConflict::IGNORE_ON_CONFLICT;
	fabricator::CreateSchema(handle_, new_path, if_not_exists);

	lock_guard<mutex> lock(schema_lock_);
	if (new_path.size() > 1) {
		// NESTED: the entry belongs to its parent, not to schemas_. EnsureSchemaPathLocked makes any
		// missing intermediate level, which matters because the provider was just asked to create the
		// whole chain. ⚠ A REPLACE keeps the existing entry (the graveyard rule — a raw pointer may be
		// held across bind→execute) and CLEARS its tables instead, which is what "replaced" has to mean
		// for anything already materialized under it.
		vector<string> parents(new_path.begin(), new_path.end() - 1);
		auto &parent = EnsureSchemaPathLocked(parents);
		auto &child = parent.GetOrCreateChildSchema(new_path.back());
		if (info.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT) {
			child.ClearTables();
		}
		return &child;
	}
	auto it = schemas_.find(info.GetQualifiedName().Schema().GetIdentifierName());
	if (it != schemas_.end()) {
		if (info.on_conflict != OnCreateConflict::REPLACE_ON_CONFLICT) {
			return it->second.get(); // already present (IGNORE / ERROR-but-cached)
		}
		retired_schemas_.push_back(std::move(it->second)); // retire, never destroy (see header)
		schemas_.erase(it);
	}
	auto entry = make_uniq<FabricatorSchemaEntry>(*this, info, handle_);
	auto &ref = *entry;
	schemas_[info.GetQualifiedName().Schema().GetIdentifierName()] = std::move(entry);
	return &ref;
}
void FabricatorCatalog::DropSchema(ClientContext &context, DropInfo &info) {
	// The dropped schema's OWN path, read COMPONENT BY COMPONENT.
	//
	// ⚠⚠ THE PATH LENGTH IS NOT A USABLE READING, AND NEITHER IS StripCatalog() — both were tried and both
	// mis-classified an ORDINARY `DROP SCHEMA <catalog>.<schema>`. The shape depends on whether nested
	// schemas are ON: without them a schema drop is built as (catalog, EMPTY schema, name) and arrives as
	// `["lake", "", "staging"]` (three elements, measured at verify_delta_catalog_schemas:104); with them
	// the same statement arrives as `["lk", "west"]` (two, no placeholder) while a genuinely nested drop
	// is `["lk", "sales", "eu"]`. So: drop a leading element that is this catalog's own name, skip empty
	// placeholders, and keep the rest — the last of which is the schema being dropped.
	auto &drop_path = info.GetQualifiedName().Path();
	idx_t first = 0;
	if (drop_path.size() > 1 &&
	    StringUtil::Lower(drop_path[0].GetIdentifierName()) == StringUtil::Lower(GetName().GetIdentifierName())) {
		first = 1;
	}
	vector<string> path;
	for (idx_t i = first; i < drop_path.size(); i++) {
		if (!drop_path[i].empty()) {
			path.push_back(drop_path[i].GetIdentifierName());
		}
	}
	if (path.empty()) {
		throw BinderException("fabricator: DROP SCHEMA names no schema");
	}
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	FabricatorSetActiveTxn(handle_, context);
	fabricator::DropSchema(handle_, path, if_exists);
	lock_guard<mutex> lock(schema_lock_);
	RetireSchemaPathLocked(path);
}

void FabricatorCatalog::RetireSchemaPathLocked(const vector<string> &path) {
	// Retire, never destroy — the entry may be held as a raw pointer across bind→plan→execute, which is
	// the use-after-free the catalog's graveyard exists to prevent. A child moves into the SAME graveyard
	// as a top-level schema, so the parent stops owning it while it stays alive to the end of the catalog.
	if (path.size() == 1) {
		auto it = schemas_.find(path[0]);
		if (it != schemas_.end()) {
			retired_schemas_.push_back(std::move(it->second));
			schemas_.erase(it);
		}
		return;
	}
	// Walk to the PARENT without creating anything: a drop must not materialize a level that is absent.
	auto it = schemas_.find(path[0]);
	if (it == schemas_.end()) {
		return;
	}
	FabricatorSchemaEntry *parent = it->second.get();
	for (idx_t i = 1; i + 1 < path.size(); i++) {
		parent = parent->FindChildSchema(path[i]);
		if (!parent) {
			return;
		}
	}
	if (auto retired = parent->ReleaseChildSchema(path.back())) {
		retired_schemas_.push_back(std::move(retired));
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
	MarkSinkOnOwnScans(plan, create_info.GetQualifiedName().Schema().GetIdentifierName(), create_info.GetQualifiedName().Name().GetIdentifierName(), "create");

	FabricatorCtasInfo info;
	info.schema_path = op.info->schema.Cast<FabricatorSchemaEntry>().SchemaPathStrings();
	info.schema_name = create_info.GetQualifiedName().Schema().GetIdentifierName();
	info.table_name = create_info.GetQualifiedName().Name().GetIdentifierName();
	for (auto &col : create_info.columns.Logical()) {
		info.column_names.push_back(col.Name().GetIdentifierName());
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
		MarkSinkOnOwnScans(*plan, op.table.schema.name.GetIdentifierName(), op.table.name.GetIdentifierName(), "insert");
	}
	FabricatorInsertTarget target;
	target.returning = op.return_chunk;
	target.schema_path = op.table.schema.Cast<FabricatorSchemaEntry>().SchemaPathStrings();
	target.schema_name = op.table.schema.name.GetIdentifierName();
	target.table_name = op.table.name.GetIdentifierName();

	auto all_names = op.table.GetColumns().GetColumnNames();
	vector<LogicalType> all_types;
	for (auto &col : op.table.GetColumns().Logical()) {
		all_types.push_back(col.Type());
	}
	if (op.column_index_map.empty()) {
		// ⚠⚠ AN EMPTY MAP NO LONGER MEANS "NO COLUMN LIST" ON 2.0, AND READING IT THAT WAY WRITES NULL
		// OVER A SERVER-SIDE DEFAULT. `LogicalInsert::column_index_map` is marked *"Deprecated: only
		// populated by older versions"* — 2.0's BINDER builds the defaults projection itself
		// (`Binder::ResolveInputProjection`), so by the time a catalog plans the insert the child is always
		// FULL WIDTH and an omitted column already carries whatever default DuckDB knows for it. For a
		// foreign table that is NULL whenever we WITHHOLD the default, which is exactly the case the
		// literals-only read-back exists to protect: a server `getdate()` must be applied BY THE SERVER.
		//
		// ⚠⚠ MEASURED on 1.5.6 vs 2.0, same statement, same table: `INSERT INTO t (i) VALUES (1)` with a
		// `DEFAULT getdate()` column left the server's clock on 1.5.6 and wrote NULL on 2.0; an IDENTITY
		// column failed outright (*"Column 'OrderID' does not allow DBNull.Value"*), which is the loud half
		// of the same defect.
		//
		// ⚠ THE DISCRIMINATOR IS THE MOVED-FROM DEFAULT, and it is exact rather than heuristic:
		// ResolveInputProjection does `std::move(insert.bound_defaults[storage_idx])` for a column the user
		// OMITTED and passes it BY REFERENCE for a column the user NAMED. So a null entry here means
		// "omitted" and nothing else. MEASURED on all three shapes:
		//     INSERT t (i) VALUES (1)              -> defaults = [live, MOVED, MOVED]
		//     INSERT t (i, nl, s) VALUES (2, …)    -> defaults = [live, live, live]
		//     INSERT t VALUES (3, …)               -> defaults = [live, live, live]
		// ⚠ It reads a moved-from unique_ptr, i.e. an upstream implementation detail rather than a
		// contract — which is why `verify_keep_nulls` and `verify_identity` gate it directly, and why the
		// fallback below is to send EVERY column (1.5's behaviour for a list-less insert), never to guess.
		//
		// ⚠ WHAT THIS DELIBERATELY DOES NOT COVER, because nothing can at this layer: an EXPLICIT
		// `VALUES (…, DEFAULT)`. `Binder::ExpandDefaultExpression` rewrites that keyword in the PARSED tree
		// — before any plan exists — into `ConstantExpression::FromValue(Value(type))`, a typed NULL, for
		// any column whose default DuckDB does not know. It is then identical to a written NULL. ⚠ That is
		// NOT a regression: measured identical on 1.5.6, and verify_keep_nulls already pins it as the one
		// cell `mssql_keep_nulls` costs.
		const auto physical_count = op.table.GetColumns().PhysicalColumnCount();
		const bool defaults_usable = op.bound_defaults.size() == physical_count;
		for (auto &col : op.table.GetColumns().Physical()) {
			const auto logical = col.Logical().index;
			if (defaults_usable && !op.bound_defaults[col.StorageOid()]) {
				continue; // omitted by the statement: leave it out so the PROVIDER applies its own default
			}
			target.columns.push_back(all_names[logical]);
			target.column_types.push_back(all_types[logical]);
			target.source_columns.push_back(logical);
		}
		if (target.columns.size() == all_names.size()) {
			target.source_columns.clear(); // identity: nothing to narrow
		}
		if (target.columns.empty()) {
			// Every column omitted (INSERT ... DEFAULT VALUES): send the lot, as 1.5 did, rather than an
			// empty column list the bulk cannot describe.
			target.columns = all_names;
			target.column_types = all_types;
			target.source_columns.clear();
		}
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
	target.schema_path = entry.schema.Cast<FabricatorSchemaEntry>().SchemaPathStrings();
	target.schema_name = entry.schema.name.GetIdentifierName();
	target.table_name = entry.name.GetIdentifierName();
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

// ⚠ THE RULE THIS PATH LIVES BY: render EXACTLY or fall back. A scan's pushed filter may be a SUPERSET because
// DuckDB re-applies every predicate above the scan; a DELETE has no such net, so a predicate rendered WEAKER
// than the user's deletes rows nobody named. Every refusal below returns null, and null means the rowid
// path — never an error.
// FABRICATOR_DUMP_DML=1 prints why a DELETE kept the rowid path (or that it took the shortcut). What route a
// DELETE takes is visible in EXPLAIN, but WHY it fell back is not, and a silent fallback is the one failure
// mode of this path that costs performance without saying so.
static void DmlTrace(const string &message) {
	static const bool enabled = [] {
		auto v = std::getenv("FABRICATOR_DUMP_DML");
		return v && *v && string(v) != "0";
	}();
	if (enabled) {
		Printer::Print("[fabricator dml] " + message);
	}
}

// The kill switch, and the reference oracle the differential gates run against. Absent (no bridge) or on
// => the shortcut is allowed.
static bool DmlPushdownEnabled(ClientContext &context) {
	Value enabled;
	if (context.TryGetCurrentSetting("fabricator_dml_pushdown", enabled) && !enabled.IsNull() &&
	    !enabled.GetValue<bool>()) {
		DmlTrace("rowid path: fabricator_dml_pushdown = false");
		return false;
	}
	return true;
}

// The ONE shape both statements accept below their own top: [FILTER] -> a scan of this very table. Anything
// else (a join for USING / FROM / IN (subquery), a second relation) keeps the rowid path. Measured on 2.0: the
// WHOLE WHERE survives as the FILTER's expressions because our pushdown never erases what it pushes.
// Returns the scan and points `filters` at the predicate (an empty list when there is no FILTER).
// `filter_map` receives the FILTER's projection map, or null. ⚠ The FILTER's own expressions read the SCAN's
// output, but its OUTPUT is narrowed by that map (plan_filter.cpp turns it into a projection), so anything
// ABOVE it — an UPDATE's SET values — indexes the narrowed list and must be remapped through it before a
// position means a scan column. Measured: `UPDATE … SET n = n + 1, v = 'z' WHERE id > 1` carries one.
static optional_ptr<LogicalGet> FindExactScan(LogicalOperator &child, TableCatalogEntry &table,
                                              vector<unique_ptr<Expression>> *&filters,
                                              vector<unique_ptr<Expression>> &no_filters,
                                              const vector<ProjectionIndex> *&filter_map) {
	auto *node = &child;
	filters = &no_filters;
	filter_map = nullptr;
	if (node->type == LogicalOperatorType::LOGICAL_FILTER) {
		auto &filter = node->Cast<LogicalFilter>();
		if (node->children.size() != 1) {
			DmlTrace("rowid path: filter with several children");
			return nullptr;
		}
		if (filter.HasProjectionMap()) {
			filter_map = &filter.projection_map;
		}
		filters = &node->expressions;
		node = node->children[0].get();
	}
	if (node->type != LogicalOperatorType::LOGICAL_GET) {
		DmlTrace("rowid path: child is " + LogicalOperatorToString(node->type) + ", not a scan");
		return nullptr;
	}
	auto &get = node->Cast<LogicalGet>();
	if (get.GetTable().get() != &table) {
		DmlTrace("rowid path: the scan is not of the statement's table");
		return nullptr;
	}
	if (get.table_filters.HasFilters()) {
		// The scan claimed these filters (a provider that applies them exactly — Delta's native read — lets
		// DuckDB ERASE them from the FILTER above), so they are part of the WHERE and must reach the predicate.
		// Each becomes an expression over a column ref to THIS scan and goes through the same strict serializer
		// as the rest; an optional/dynamic join-filter wrapper does not render, which sends the statement to the
		// rowid path — never a predicate missing a part. SQL Server's scan never erases, so it is unaffected.
		if (filters != &no_filters) { // the FILTER's own conjuncts first (never iterate the vector being filled)
			for (auto &f : *filters) {
				no_filters.push_back(f->Copy());
			}
		}
		auto &column_ids = get.GetColumnIds();
		for (auto &entry : get.table_filters) {
			auto idx = entry.GetIndex();
			if (idx.GetIndex() >= column_ids.size()) {
				DmlTrace("rowid path: a scan filter on an unknown column");
				return nullptr;
			}
			auto &col = column_ids[idx.GetIndex()];
			if (!col.HasPrimaryIndex() || col.HasChildren() || col.IsVirtualColumn() ||
			    col.GetPrimaryIndex() >= get.returned_types.size()) {
				DmlTrace("rowid path: a scan filter on a virtual or nested column");
				return nullptr;
			}
			BoundColumnRefExpression ref(get.returned_types[col.GetPrimaryIndex()],
			                             ColumnBinding(get.table_index, idx));
			no_filters.push_back(entry.Filter().ToExpression(ref));
		}
		filters = &no_filters;
	}
	return &get;
}

static string RenderPredicate(const vector<unique_ptr<Expression>> &filters) {
	string predicate;
	for (auto &f : filters) {
		predicate += predicate.empty() ? "" : " AND ";
		predicate += f->ToString();
	}
	return predicate;
}

static optional_ptr<PhysicalOperator> TryPlanDeleteWhere(ClientContext &context, PhysicalPlanGenerator &planner,
                                                         LogicalDelete &op, Catalog &catalog,
                                                         FabricatorHandle catalog_handle) {
	if (op.return_chunk) {
		DmlTrace("rowid path: RETURNING");
		return nullptr; // RETURNING needs the deleted rows themselves
	}
	if (!DmlPushdownEnabled(context)) {
		return nullptr;
	}
	if (&op.table.ParentCatalog() != &catalog || op.children.size() != 1) {
		DmlTrace("rowid path: not this catalog's single-child DELETE");
		return nullptr;
	}
	vector<unique_ptr<Expression>> no_filters;
	vector<unique_ptr<Expression>> *filters = nullptr;
	const vector<ProjectionIndex> *filter_map = nullptr; // irrelevant here: a DELETE reads nothing above it
	auto get = FindExactScan(*op.children[0], op.table, filters, no_filters, filter_map);
	if (!get) {
		return nullptr;
	}
	auto &entry = op.table.Cast<FabricatorTableEntry>();
	// Asked BEFORE the predicate is rendered, because what counts as exact and which columns may appear are
	// the provider's answer. One crossing, cached per entry after the first DELETE/UPDATE.
	auto &caps = entry.DmlCapabilities();
	if (!caps.delete_where) {
		DmlTrace("rowid path: the table does not support predicate DELETE");
		return nullptr;
	}
	if (caps.delete_where_autocommit_only && !context.transaction.IsAutoCommit()) {
		// The provider's predicate statement cannot see this transaction's pending writes (Delta: the
		// engine deletes against the BASE snapshot), so inside BEGIN … COMMIT the rowid path keeps
		// read-your-writes.
		DmlTrace("rowid path: explicit transaction");
		return nullptr;
	}
	string json;
	vector<Value> constants;
	vector<string> referenced;
	if (!FabricatorSerializeExactFilter(*get, *filters, json, constants, caps.exact_strings, &referenced)) {
		DmlTrace("rowid path: the predicate does not render exactly");
		return nullptr;
	}
	if (caps.restrict_delete_columns) {
		for (auto &col : referenced) {
			if (caps.delete_where_columns.find(col) == caps.delete_where_columns.end()) {
				// Delta: a column the provider cannot decide a whole file on (not a partition column, and no
				// statistics opt-in) — the native rowid scan is the faster path for it.
				DmlTrace("rowid path: column " + col + " is not one the provider deletes by");
				return nullptr;
			}
		}
	}
	string spec = json.empty() ? "{}" : "{\"filter\":" + json + "}";
	DmlTrace("delete-where: " + spec);
	vector<LogicalType> result_types {LogicalType::BIGINT};
	return planner.Make<FabricatorPhysicalDmlWhere>(std::move(result_types), op.estimated_cardinality,
	                                                catalog_handle, entry.TableHandle(), std::move(spec),
	                                                std::move(constants), RenderPredicate(*filters),
	                                                /*is_update=*/false, string());
}

PhysicalOperator &FabricatorCatalog::PlanDelete(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalDelete &op) {
	if (auto shortcut = TryPlanDeleteWhere(context, planner, op, *this, handle_)) {
		return *shortcut;
	}
	return Catalog::PlanDelete(context, planner, op);
}

// The UPDATE twin (ABI v94). Its child is ONE projection — the SET values, then the rowid — over the same
// [FILTER] -> scan the DELETE takes (measured on 2.0: `UPDATE … SET n = n + 1 WHERE id > 5` plans as
// UPDATE -> PROJECTION((n + 1), rowid) -> FILTER -> SCAN; the optimizer folds the binder's second projection
// away). Each SET value must render exactly too (FabricatorSerializeExactValue); one that does not sends the
// whole statement to the rowid path, exactly like a predicate conjunct.
static optional_ptr<PhysicalOperator> TryPlanUpdateWhere(ClientContext &context, PhysicalPlanGenerator &planner,
                                                         LogicalUpdate &op, Catalog &catalog,
                                                         FabricatorHandle catalog_handle) {
	if (op.return_chunk || op.capture_old_rows) {
		DmlTrace("rowid path: RETURNING");
		return nullptr;
	}
	if (!DmlPushdownEnabled(context)) {
		return nullptr;
	}
	if (&op.table.ParentCatalog() != &catalog || op.children.size() != 1 ||
	    op.columns.size() != op.expressions.size() || op.columns.empty()) {
		DmlTrace("rowid path: not this catalog's single-child UPDATE");
		return nullptr;
	}
	auto &proj = *op.children[0];
	if (proj.type != LogicalOperatorType::LOGICAL_PROJECTION || proj.children.size() != 1) {
		DmlTrace("rowid path: UPDATE child is " + LogicalOperatorToString(proj.type) + ", not one projection");
		return nullptr;
	}
	vector<unique_ptr<Expression>> no_filters;
	vector<unique_ptr<Expression>> *filters = nullptr;
	const vector<ProjectionIndex> *filter_map = nullptr;
	auto get = FindExactScan(*proj.children[0], op.table, filters, no_filters, filter_map);
	if (!get) {
		return nullptr;
	}
	auto &entry = op.table.Cast<FabricatorTableEntry>();
	string filter_json;
	vector<Value> constants;
	if (!FabricatorSerializeExactFilter(*get, *filters, filter_json, constants)) {
		DmlTrace("rowid path: the predicate does not render exactly");
		return nullptr;
	}
	auto names = op.table.GetColumns().GetColumnNames();
	string set_json = "[";
	string assignments;
	for (idx_t i = 0; i < op.columns.size(); i++) {
		// op.expressions[i] is a position in the PROJECTION's output (the same rule the rowid path reads,
		// FabricatorFillUpdateSetColumns); the value itself is that projection expression.
		auto &ref = *op.expressions[i];
		if (ref.GetExpressionType() != ExpressionType::BOUND_REF) {
			DmlTrace("rowid path: SET target is not a projection reference");
			return nullptr;
		}
		auto pos = ref.Cast<BoundReferenceExpression>().Index();
		if (pos >= proj.expressions.size() || op.columns[i].index >= names.size()) {
			DmlTrace("rowid path: SET target out of range");
			return nullptr;
		}
		auto &value = *proj.expressions[pos];
		// The value reads the FILTER's output; turn each position into the scan-output position it came from
		// (a copy — the logical plan is still DuckDB's, and the rowid path may yet need it unchanged).
		auto mapped = value.Copy();
		bool mapped_ok = true;
		if (filter_map) {
			ExpressionIterator::VisitExpressionMutable<BoundReferenceExpression>(
			    mapped, [&](BoundReferenceExpression &ref, unique_ptr<Expression> &) {
				    if (ref.Index() >= filter_map->size()) {
					    mapped_ok = false;
					    return;
				    }
				    ref.IndexMutable() = (*filter_map)[ref.Index()].GetIndex();
			    });
		}
		string value_json;
		if (!mapped_ok || !FabricatorSerializeExactValue(*get, *mapped, value_json, constants)) {
			DmlTrace("rowid path: a SET value does not render exactly");
			return nullptr;
		}
		auto &col = names[op.columns[i].index];
		set_json += i ? ",{\"col\":" : "{\"col\":";
		set_json += "\"" + fabricator::EscapeJsonString(col) + "\"";
		set_json += ",\"value\":" + value_json + "}";
		assignments += (i ? ", " : "") + col + " = " + value.ToString();
	}
	set_json += "]";
	if (!entry.DmlCapabilities().update_where) {
		DmlTrace("rowid path: the table does not support predicate UPDATE");
		return nullptr;
	}
	string spec = "{\"set\":" + set_json + (filter_json.empty() ? "" : ",\"filter\":" + filter_json) + "}";
	DmlTrace("update-where: " + spec);
	vector<LogicalType> result_types {LogicalType::BIGINT};
	return planner.Make<FabricatorPhysicalDmlWhere>(std::move(result_types), op.estimated_cardinality,
	                                                catalog_handle, entry.TableHandle(), std::move(spec),
	                                                std::move(constants), RenderPredicate(*filters),
	                                                /*is_update=*/true, std::move(assignments));
}

PhysicalOperator &FabricatorCatalog::PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner,
                                              LogicalUpdate &op) {
	if (auto shortcut = TryPlanUpdateWhere(context, planner, op, *this, handle_)) {
		return *shortcut;
	}
	return Catalog::PlanUpdate(context, planner, op);
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
		target.rowid_child_index = op.expressions[0]->Cast<BoundReferenceExpression>().Index();
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
		// ⚠⚠ `SET <col> = DEFAULT` USED TO BE REFUSED HERE, AND DuckDB 2.0 REMOVED THE REASON.
		// On 1.5 a DEFAULT contributed NO projection column, so reading the ordinal shifted every later SET
		// value by one — measured as an INTERNAL error plus a fatally invalidated database when the shifted
		// types differed, and WRONG DATA committed with exit 0 when they coincided. 2.0's `Binder::
		// BindUpdateSet` now PUSHES the column's bound default into the projection and emits an ordinary
		// column reference to it (bind_update.cpp), so a DEFAULT is just another projected value and the
		// position rule below is exact for it. The refusal is therefore gone, and what protects us is the
		// generic check beneath: anything that is not a reference into the binder's own projection is
		// refused, which a VALUE_DEFAULT reaching here would now be.
		// ⚠ A provider that does not RECORD column defaults (Delta: `column_default` is NULL for every
		// column) resolves the default to NULL — the same answer an INSERT omitting the column already
		// gives on that table, so the two surfaces agree rather than one of them being wrong.
		if (expr.GetExpressionType() != ExpressionType::BOUND_REF) {
			// The binder always emits a reference into its own projection (Binder::BindUpdateSet), and the
			// column-binding resolver rewrites it to a BOUND_REF before physical planning. Anything else
			// would mean reading an unrelated column as this one's new value.
			throw NotImplementedException("fabricator: unsupported UPDATE assignment for column %s (%s)", col_name,
			                              expr.ToString());
		}
		target.set_columns.push_back(col_name);
		target.set_types.push_back(types[columns[i].index]);
		target.set_child_indices.push_back(expr.Cast<BoundReferenceExpression>().Index());
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
