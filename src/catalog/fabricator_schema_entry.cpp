// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — schema catalog entry (impl)
//===----------------------------------------------------------------------===//

#include <algorithm>
#include "catalog/fabricator_schema_entry.hpp"

#include "fabricator/arrow_ingest.hpp"
#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_lateral.hpp"
#include "catalog/fabricator_metadata.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/planner/tableref/bound_at_clause.hpp"
#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/common/enums/operator_result_type.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/blob.hpp"
#include "duckdb/common/vector_operations/vector_operations.hpp"
#include "duckdb/catalog/catalog_entry/aggregate_function_catalog_entry.hpp"
#include "duckdb/execution/execution_context.hpp"
#include "duckdb/logging/logger.hpp"
#include "duckdb/execution/expression_executor_state.hpp"
#include "duckdb/execution/physical_operator.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/function/aggregate_function.hpp"
#include "duckdb/function/aggregate_state.hpp"
#include "duckdb/function/function_set.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/connection.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/planner/operator/logical_extension_operator.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
#include "duckdb/parser/constraints/not_null_constraint.hpp"
#include "duckdb/parser/constraints/unique_constraint.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "catalog/fabricator_partition_util.hpp"
#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/parsed_data/alter_table_info.hpp"
#include "duckdb/parser/parser.hpp"
#include "duckdb/parser/statement/create_statement.hpp"
#include "duckdb/parser/statement/select_statement.hpp"
#include "duckdb/parser/tableref/subqueryref.hpp"
#include "duckdb/parser/parsed_data/create_aggregate_function_info.hpp"
#include "duckdb/catalog/catalog_entry/scalar_macro_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/table_macro_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/view_catalog_entry.hpp"
#include "duckdb/parser/parsed_data/create_macro_info.hpp"
#include "duckdb/parser/parsed_data/create_view_info.hpp"
#include "duckdb/parser/parsed_data/create_scalar_function_info.hpp"
#include "duckdb/parser/parsed_data/create_table_function_info.hpp"
#include "duckdb/parser/parsed_data/create_table_info.hpp"
#include "duckdb/parser/parsed_data/drop_info.hpp"
#include "duckdb/planner/parsed_data/bound_create_table_info.hpp"
#include "duckdb/planner/expression/bound_function_expression.hpp"
#include "duckdb/execution/expression_executor.hpp"

#include <atomic>
#include <cstring>
#include <unordered_map>

namespace duckdb {

FabricatorSchemaEntry::FabricatorSchemaEntry(Catalog &catalog, CreateSchemaInfo &info, FabricatorHandle handle)
    : SchemaCatalogEntry(catalog, info), handle_(handle) {
}

// Entry evictions RETIRE (never destroy): the lookup paths hand out raw pointers DuckDB's binder holds
// across the lock (bind -> plan -> execute), so destroying a cached entry mid-session is a use-after-free
// under concurrency (see the graveyard comment in the header). Plain templates, NOT generic lambdas — the
// extension compiles as C++11 on gcc (the CLAUDE.md pre-C++17 gotcha; MSVC was permissive). Callers hold
// entry_lock_.
template <class MAP>
static void RetireErase(MAP &cache, const string &key, vector<unique_ptr<CatalogEntry>> &graveyard) {
	auto it = cache.find(key);
	if (it != cache.end()) {
		graveyard.push_back(std::move(it->second));
		cache.erase(it);
	}
}

// Evicts every TIME-TRAVEL entry for one table. at_entries_ is keyed name+US+unit+US+value, so a
// name-scoped eviction has to match the name PART — RetireErase on the bare name would never hit.
// Called wherever the LATEST entry is evicted: whatever made that stale (a REPLACE, an ALTER, a DROP,
// a self-heal) can equally have invalidated an as-of view of the same table.
template <class MAP>
static void RetireAtEntriesFor(MAP &cache, const string &table_name,
                               vector<unique_ptr<CatalogEntry>> &graveyard) {
	for (auto it = cache.begin(); it != cache.end();) {
		auto sep = it->first.find('\x1f');
		if (sep != string::npos && StringUtil::CIEquals(it->first.substr(0, sep), table_name)) {
			graveyard.push_back(std::move(it->second));
			it = cache.erase(it);
		} else {
			++it;
		}
	}
}


template <class MAP>
static void RetireAll(MAP &cache, vector<unique_ptr<CatalogEntry>> &graveyard) {
	for (auto &kv : cache) {
		graveyard.push_back(std::move(kv.second));
	}
	cache.clear();
}

template <class MAP>
static void RetireMatching(MAP &cache, const std::function<bool(const string &)> &matches,
                           vector<unique_ptr<CatalogEntry>> &graveyard) {
	for (auto it = cache.begin(); it != cache.end();) {
		if (matches(it->first)) {
			graveyard.push_back(std::move(it->second));
			it = cache.erase(it);
		} else {
			++it;
		}
	}
}

void FabricatorSchemaEntry::AddTable(const string &table_name, const string &table_type) {
	lock_guard<mutex> lock(entry_lock_);
	table_types_[table_name] = table_type;
	// Drop any cached entry so the schema is re-fetched (e.g. after CREATE OR REPLACE).
	RetireErase(entries_, table_name, retired_entries_);
	RetireAtEntriesFor(at_entries_, table_name, retired_entries_);
}

void FabricatorSchemaEntry::AddScalarFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	scalar_functions_.insert(func_name);
	// Drop any cached entry so the signature is re-fetched (e.g. after CREATE OR ALTER).
	RetireErase(function_entries_, func_name, retired_entries_);
}

void FabricatorSchemaEntry::AddTableFunction(const string &func_name, bool is_proc) {
	lock_guard<mutex> lock(entry_lock_);
	table_functions_[func_name] = is_proc;
	RetireErase(table_function_entries_, func_name, retired_entries_);
	// NOTE: the host used to invent a `<name>_each` table-in-out alias here for EVERY table-kind function of
	// EVERY provider. That made a SQL-Server semantic (CROSS APPLY / per-row EXEC) the host's business and
	// produced entries that could only fail wherever there is nothing to apply per row — 30 dead siblings on a
	// Fabric attach alone, all of them advertised in duckdb_functions(). A provider that wants a per-row form
	// now DECLARES it, as an ordinary `inout` function under whatever name it likes (SqlServerBackend's
	// FunctionsSql emits `<routine>_each`), and it arrives through AddInOutFunction like any other.
}

void FabricatorSchemaEntry::AddSqlTableFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	sql_table_functions_.insert(func_name);
	RetireErase(table_function_entries_, func_name, retired_entries_);
}

void FabricatorSchemaEntry::AddInOutFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	custom_inout_functions_.insert(func_name);
	RetireErase(table_function_entries_, func_name, retired_entries_);
}

void FabricatorSchemaEntry::AddLateralFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	custom_lateral_functions_.insert(func_name);
	RetireErase(table_function_entries_, func_name, retired_entries_);
}

void FabricatorSchemaEntry::AddCollectorFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	custom_collector_functions_.insert(func_name);
	RetireErase(table_function_entries_, func_name, retired_entries_);
}

void FabricatorSchemaEntry::AddAggregateFunction(const string &func_name, bool spillable) {
	lock_guard<mutex> lock(entry_lock_);
	aggregate_functions_[func_name] = spillable;
	// Drop any cached entry so the signature is re-fetched (e.g. after a cache refresh).
	RetireErase(aggregate_function_entries_, func_name, retired_entries_);
}

void FabricatorSchemaEntry::AddMacro(const string &macro_name, const string &create_sql) {
	lock_guard<mutex> lock(entry_lock_);
	macros_[macro_name] = create_sql;
	// Drop any cached entry so a re-declared body is re-parsed (e.g. after a cache refresh).
	RetireErase(macro_entries_, macro_name, retired_entries_);
}

void FabricatorSchemaEntry::AddView(const string &view_name, const string &create_sql) {
	lock_guard<mutex> lock(entry_lock_);
	// ⚠ COLLISION IS REFUSED, NOT RESOLVED. A view and a table share ONE lookup (TABLE_ENTRY), so if both
	// carry this name one of them must win — and either winner is a WRONG ANSWER for whoever wanted the
	// other. The declaration is dropped and the name recorded, so the lookup can refuse while naming both
	// sides and the fix. Note the check is only as good as ENUMERATION: an ATTACH table_filter can hide a
	// table that still exists (filters bound enumeration, never targeted access), in which case the view
	// wins silently — an accepted limit, since establishing absence would cost a probe per declared view.
	if (table_types_.find(view_name) != table_types_.end()) {
		view_collisions_.insert(view_name);
		views_.erase(view_name);
		RetireErase(view_entries_, view_name, retired_entries_);
		return;
	}
	views_[view_name] = create_sql;
	// Drop any cached entry so a re-declared body is re-parsed (e.g. after a cache refresh).
	RetireErase(view_entries_, view_name, retired_entries_);
}

void FabricatorSchemaEntry::ClearTables() {
	lock_guard<mutex> lock(entry_lock_);
	table_types_.clear();
	RetireAll(entries_, retired_entries_);
	scalar_functions_.clear();
	RetireAll(function_entries_, retired_entries_);
	table_functions_.clear();
	custom_inout_functions_.clear();
	custom_lateral_functions_.clear();
	custom_collector_functions_.clear();
	aggregate_functions_.clear();
	macros_.clear();
	views_.clear();
	view_collisions_.clear();
	RetireAll(view_entries_, retired_entries_);
	RetireAll(table_function_entries_, retired_entries_);
	RetireAll(aggregate_function_entries_, retired_entries_);
	RetireAll(macro_entries_, retired_entries_);
}

void FabricatorSchemaEntry::InvalidateEntryCache() {
	// Keep the discovered NAME lists (table_types_, scalar_functions_, …); drop only the materialized
	// entries so the next access re-fetches columns/rowid/return types from the (now committed) server state.
	lock_guard<mutex> lock(entry_lock_);
	RetireAll(entries_, retired_entries_);
	// Time-travel entries go too. A VERSION-keyed one is immutable so dropping it is merely wasteful, but
	// a TIMESTAMP-keyed one is not (a far-future instant resolves to a moving latest) — one rule for both.
	RetireAll(at_entries_, retired_entries_);
	RetireAll(function_entries_, retired_entries_);
	RetireAll(table_function_entries_, retired_entries_);
	RetireAll(aggregate_function_entries_, retired_entries_);
	// Macro bodies are declarations, not fetched server state, so re-parsing gains nothing — but the entries go
	// too, because they are handed out as raw pointers under the same graveyard contract as the rest and it is
	// cheaper to keep one rule than to reason about an exception.
	RetireAll(macro_entries_, retired_entries_);
	// Same reasoning for views — the BODY is a declaration and re-parsing gains nothing, but the entry also
	// caches a BINDING (ViewCatalogEntry::view_columns, what duckdb_columns()/DESCRIBE report), and that IS
	// fetched state: it describes the referenced tables' columns as they were. Dropping the entry is the
	// cheapest way to make a rolled-back or refreshed schema visible there too.
	RetireAll(view_entries_, retired_entries_);
}

void FabricatorSchemaEntry::InvalidateMatching(const std::function<bool(const string &)> &matches) {
	// Like InvalidateEntryCache but scoped: drop only the materialized entries whose NAME matches. The name
	// lists are kept, so an ALTER'd object re-fetches its fresh schema on next access and a DROPped one
	// self-heals (its column re-fetch fails -> GetOrCreateEntry evicts it). Everything else stays warm.
	lock_guard<mutex> lock(entry_lock_);
	RetireMatching(entries_, matches, retired_entries_);
	// ⚠ at_entries_ is keyed name+US+unit+US+value, so the caller's NAME predicate must be applied to the
	// name PART. Passing the composite key straight to `matches` would silently match nothing and leave a
	// time-travel entry describing a table that has since been ALTERed.
	RetireMatching(at_entries_,
	               [&](const string &key) { return matches(key.substr(0, key.find('\x1f'))); },
	               retired_entries_);
	RetireMatching(function_entries_, matches, retired_entries_);
	RetireMatching(table_function_entries_, matches, retired_entries_);
	RetireMatching(aggregate_function_entries_, matches, retired_entries_);
	RetireMatching(macro_entries_, matches, retired_entries_);
	RetireMatching(view_entries_, matches, retired_entries_);
}

// The cache key for a time-travel entry. US separators (0x1f) cannot occur in a SQL identifier or in the
// AT clause's rendered value, so no (name, unit, value) triple can collide with another.
static string AtEntryKey(const string &table_name, const string &unit, const string &value) {
	return table_name + "\x1f" + unit + "\x1f" + value;
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateEntry(ClientContext &context, const string &table_name,
                                                                 optional_ptr<BoundAtClause> at) {
	lock_guard<mutex> lock(entry_lock_);
	// A time-travel reference resolves against its OWN map: the entry it needs describes the table as of
	// that version, and putting it in entries_ would both shadow the latest one and leak into the
	// context-free Scan() overload, which walks entries_ directly.
	string at_unit, at_value, at_key;
	if (at) {
		at_unit = at->Unit();
		at_value = at->GetValue().ToString();
		at_key = AtEntryKey(table_name, at_unit, at_value);
		auto at_cached = at_entries_.find(at_key);
		if (at_cached != at_entries_.end()) {
			return at_cached->second.get();
		}
	} else {
		auto cached = entries_.find(table_name);
		if (cached != entries_.end()) {
			return cached->second.get();
		}
	}
	auto type_it = table_types_.find(table_name);
	if (type_it == table_types_.end() && !catalog.Cast<FabricatorCatalog>().HasObjectFilter()) {
		// No object filter: the discovered name list is the FULL enumeration, so a miss is genuinely absent.
		return nullptr;
	}
	// A miss WITH an object filter active is ambiguous: the discovered list is a filtered subset, so this may
	// be a real table the filter merely excluded from ENUMERATION. The filter bounds enumeration, not targeted
	// access — so fetch by name. A genuine absence throws in FetchTableSchema below and is treated as
	// not-found. The entry is cached in entries_ (fast repeat access) but NOT added to table_types_, so it
	// stays out of enumeration (SHOW TABLES / full refresh keep the filtered view).

	// Open the table SESSION first (ABI v72): a cheap handle around the stateless definition (+ the AT
	// clause, which is part of the handle's identity — the AT entry's schema answer is the provider's own
	// as-of describe). Every crossing below rides it; the ENTRY takes ownership at construction, and this
	// guard closes it on every earlier exit (absence, a failed fetch) — TableClose is noexcept.
	struct TableHandleGuard {
		FabricatorHandle handle;
		~TableHandleGuard() {
			fabricator::TableClose(handle);
		}
		FabricatorHandle Release() {
			auto h = handle;
			handle = nullptr;
			return h;
		}
	} table_guard {fabricator::TableOpen(handle_, name, table_name, at_unit, at_value)};

	vector<string> names;
	vector<LogicalType> types;
	try {
		FetchTableSchema(context, handle_, table_guard.handle, names, types);
	} catch (fabricator::ObjectNotFoundException &) {
		// The discovered name is stale — the table no longer exists on the server
		// (e.g. dropped out-of-band via fabricator_exec). Treat it as not-found so
		// CREATE TABLE IF NOT EXISTS / OR REPLACE see "absent" instead of an error.
		//
		// ONLY established absence lands here (the provider returned FABRICATOR_NOT_FOUND). This used to
		// catch std::exception — every failure — so a table that merely could not be READ was erased from
		// the catalog and reported as "does not exist": its data intact, its name gone from enumeration
		// too, and the real cause (an incomplete Delta log naming the exact missing version, an expired
		// credential, a transient outage) discarded one frame after the provider produced it. A user given
		// "table does not exist" checks their spelling and their permissions; nothing about that search
		// leads to a missing commit file.
		table_types_.erase(table_name);
		RetireErase(entries_, table_name, retired_entries_);
		RetireAtEntriesFor(at_entries_, table_name, retired_entries_);
		return nullptr;
	}

	CreateTableInfo info(catalog.GetName(), name, table_name);
	for (idx_t i = 0; i < names.size(); i++) {
		info.columns.AddColumn(ColumnDefinition(names[i], types[i]));
	}

	// Row identity + provider virtual columns — ONE table_info crossing (was kinds 3 + 12).
	auto identity = FetchTableInfo(table_guard.handle);

	// Resolve row-identity columns (PK / smallest unique index) to column indices.
	auto &rowid_names = identity.rowid_columns;
	vector<idx_t> rowid_indices;
	for (auto &rowid_name : rowid_names) {
		for (idx_t i = 0; i < names.size(); i++) {
			if (StringUtil::CIEquals(names[i], rowid_name)) {
				rowid_indices.push_back(i);
				break;
			}
		}
	}

	// A rowid name that resolves to no user column is a VIRTUAL rowid: a provider-supplied row identity that
	// is not part of the visible schema (the Delta catalog's stable `_metadata.row_id`). SQL Server's rowid is
	// always real PK/unique columns, so they all resolve and this branch never triggers there.
	vector<string> virtual_rowid_columns;
	if (!rowid_names.empty() && rowid_indices.empty()) {
		virtual_rowid_columns = rowid_names; // none resolved as a user column => treat all as virtual
	} else if (rowid_indices.size() != rowid_names.size()) {
		rowid_indices.clear(); // a partial/mixed resolution is a bad key — disable rowid rather than risk it
	}

	LogicalType rowid_type = LogicalType::BIGINT;
	if (rowid_indices.size() == 1) {
		rowid_type = types[rowid_indices[0]];
	} else if (rowid_indices.size() > 1) {
		child_list_t<LogicalType> children;
		for (auto idx : rowid_indices) {
			children.push_back(make_pair(names[idx], types[idx]));
		}
		rowid_type = LogicalType::STRUCT(std::move(children));
	} else if (virtual_rowid_columns.size() > 1) {
		// Compound virtual key (not used by the Delta provider, which has a single BIGINT row id) — STRUCT
		// of BIGINTs keyed by the virtual names.
		child_list_t<LogicalType> children;
		for (auto &vn : virtual_rowid_columns) {
			children.push_back(make_pair(vn, LogicalType::BIGINT));
		}
		rowid_type = LogicalType::STRUCT(std::move(children));
	}

	// Provider-declared VIRTUAL columns (queryable by name, excluded from SELECT *) — e.g. the Delta
	// catalog's stable __delta_row_id / __delta_row_commit_version on row-tracking tables under
	// native_read. An unknown declared type is skipped rather than guessed.
	vector<std::pair<string, LogicalType>> provider_virtual_columns;
	for (auto &vc : identity.virtual_columns) {
		LogicalType vt;
		if (vc.second == "BIGINT") {
			vt = LogicalType::BIGINT;
		} else if (vc.second == "INTEGER") {
			vt = LogicalType::INTEGER;
		} else if (vc.second == "VARCHAR") {
			vt = LogicalType::VARCHAR;
		} else if (vc.second == "DOUBLE") {
			vt = LogicalType::DOUBLE;
		} else {
			continue;
		}
		provider_virtual_columns.emplace_back(vc.first, std::move(vt));
	}

	auto entry = make_uniq<FabricatorTableEntry>(catalog, *this, info, table_guard.Release(),
	                                           std::move(rowid_indices), std::move(rowid_type),
	                                           std::move(virtual_rowid_columns),
	                                           std::move(provider_virtual_columns));
	auto &ref = *entry;
	if (at) {
		at_entries_[at_key] = std::move(entry);
	} else {
		entries_[table_name] = std::move(entry);
	}
	return &ref;
}

// -----------------------------------------------------------------------------
// Scalar functions (Phase 3 + the ABI v80 bind session). A scalar call is BOUND per call site
// (scalarfn_bind -> a managed binding) and then EXECUTED per chunk over that binding (scalarfn_execute),
// mirroring tablefn_bind / tablefn_execute / tablefn_close. Binding buys two things a stateless execute
// could not express: a result type that depends on the call's constant arguments, and somewhere for the
// provider to park work done once instead of per chunk.
// -----------------------------------------------------------------------------

// Identity carried on the ScalarFunction's function_info — the bind callback is a RAW function pointer and
// cannot capture (the same reason FabricatorAggregateFunctionInfo exists).
struct FabricatorScalarFunctionInfo : public ScalarFunctionInfo {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	vector<string> arg_names;
	//! Position of the declared VARARGS tail, or INVALID_INDEX. Needed at BIND because a variadic call is
	//! wider than the declaration, so both the marshal TYPE and the column NAME of a tail argument have to be
	//! derived rather than looked up.
	idx_t varargs_index = DConstants::INVALID_INDEX;
};

// Refcounted holder for the managed scalar binding; its destructor calls scalarfn_close at plan teardown
// (best-effort, idempotent). Mirrors AggSessionHolder — and it must be REFCOUNTED rather than owned
// outright, because FunctionData::Copy is called for a bound expression and every copy addresses the SAME
// managed binding.
struct ScalarBindingHolder {
	FabricatorHandle binding = nullptr;
	~ScalarBindingHolder() {
		fabricator::ScalarFnClose(binding);
	}
};

struct FabricatorScalarBindData : public FunctionData {
	shared_ptr<ScalarBindingHolder> holder;

	unique_ptr<FunctionData> Copy() const override {
		auto c = make_uniq<FabricatorScalarBindData>();
		c->holder = holder;
		return std::move(c);
	}
	bool Equals(const FunctionData &other_p) const override {
		return holder == other_p.Cast<FabricatorScalarBindData>().holder;
	}
};

// Bind one scalar call site: fold whatever arguments are constant, cross them to the provider, adopt the
// result type it reports, and keep the binding for the exec callback.
//
// ⚠ A SCALAR'S ARGUMENTS NEED NOT BE CONSTANT — unlike a table function's, which DuckDB pre-evaluates
// into TableFunctionBindInput::inputs. Here we are handed argument EXPRESSIONS, so we fold what we can and
// tell the provider WHICH slots are real via the mask; an unfoldable slot carries a NULL placeholder that
// must not be mistaken for an explicit NULL literal.
// =============================================================================
// VARIADIC parameters (`fabricator.param_style = "varargs"` => FabricatorParamStyle::VARARGS)
//
// DuckDB carries variadics as ONE type on `SimpleFunction::varargs`, shared by scalar / table / aggregate:
// `arguments` is the FIXED PREFIX and therefore the MINIMUM arity, and every argument beyond it must
// implicitly cast to `varargs` (function_binder.cpp `BindVarArgsFunctionCost`). There is NO maximum, and
// overload resolution costs a variadic candidate like any other, so a fixed-arity overload still wins.
//
// A declaration's variadic field is therefore NOT one of `tf.arguments` — it names the TAIL'S TYPE. Two
// structural rules are enforced rather than reinterpreted (see FabricatorVarArgsIndex).
//
// ⚠ THE PART THAT IS EASY TO MISS: every args marshal in this file initializes a DataChunk from the
// DECLARED types and then loops the SUPPLIED values. For a variadic call there are MORE values than
// declared types, so each marshal has to be widened to the actual call — FabricatorExpandVarArgs does it.
// =============================================================================

//! The index of the declared VARARGS parameter, or DConstants::INVALID_INDEX when the function is not
//! variadic. Throws on a malformed declaration, naming the function and the offending parameter.
static idx_t FabricatorVarArgsIndex(const string &func_name, const vector<string> &names,
                                    const vector<FabricatorParamStyle> &styles) {
	idx_t found = DConstants::INVALID_INDEX;
	for (idx_t i = 0; i < names.size(); i++) {
		auto style = i < styles.size() ? styles[i] : FabricatorParamStyle::POSITIONAL;
		if (style == FabricatorParamStyle::VARARGS) {
			if (found != DConstants::INVALID_INDEX) {
				throw InvalidInputException("Fabricator: function \"%s\" declares more than one variadic tail "
				                            "(\"%s\"); DuckDB carries exactly one varargs type per function",
				                            func_name, names[i]);
			}
			found = i;
			continue;
		}
		if (found != DConstants::INVALID_INDEX && style != FabricatorParamStyle::NAMED) {
			throw InvalidInputException(
			    "Fabricator: function \"%s\" declares \"%s\" after its variadic tail. The tail must be the LAST "
			    "positional parameter — every argument past the declared ones belongs to it, so a positional "
			    "parameter following it can never be filled",
			    func_name, names[i]);
		}
	}
	return found;
}

//! The DuckDB type of a variadic tail. SQLNULL is this protocol's "accept any value" sentinel and maps to
//! ANY — which is also what makes DuckDB insert NO cast, so each argument arrives as its own runtime type.
//! That is the whole point of a HETEROGENEOUS tail (a homogeneous one is better served by a LIST argument).
static LogicalType FabricatorVarArgsType(const LogicalType &declared) {
	return declared.id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : declared;
}

//! The name of the i-th ACTUAL argument of a (possibly variadic) call: the fixed prefix keeps its declared
//! name, and each tail argument is `<tail>_<k>` with k from 0. Subsumes the historical `arg<i>` fallback for
//! a call wider than its declaration, which is what a variadic call always is.
static string FabricatorArgName(const vector<string> &decl_names, idx_t varargs_index, idx_t i) {
	if (varargs_index != DConstants::INVALID_INDEX && varargs_index < decl_names.size() && i >= varargs_index) {
		return decl_names[varargs_index] + "_" + to_string(i - varargs_index);
	}
	return i < decl_names.size() ? decl_names[i] : "arg" + to_string(i);
}

//! Refuses a variadic tail on a function kind that cannot carry one — AGGREGATES only, now that scalar,
//! table, sqlgen, in-out/collector and lateral all take one. Their state/update/combine marshal was never
//! examined for a per-call-site width, and DuckDB's AggregateFunction carries the same `varargs` field, so
//! this is "unexamined", not "impossible".
//! ⚠ Doing NOTHING is worse than an error rather than merely less helpful: with no case for it, a VARARGS
//! field falls through to the positional branch and silently becomes an ordinary ANY argument — a function
//! whose documented call syntax then does not work, and whose declaration looks honoured.
static void FabricatorRefuseVarArgs(const string &func_name, const char *kind, const vector<string> &names,
                                    const vector<FabricatorParamStyle> &styles) {
	for (idx_t i = 0; i < names.size(); i++) {
		if (i < styles.size() && styles[i] == FabricatorParamStyle::VARARGS) {
			throw InvalidInputException("Fabricator: function \"%s\" declares the variadic tail \"%s\", which "
			                            "a %s function cannot take — its per-call state marshal has no "
			                            "per-call-site width",
			                            func_name, names[i], kind);
		}
	}
}

//! Widens a declared (names, types) pair to the ACTUAL positional width of a variadic call, given the
//! supplied values. A NON-VARIADIC function comes back byte-identical to its declaration, so callers need
//! no second branch.
//! ⚠ A variadic one comes back at the CALL's width, which for a call supplying NO tail arguments is the
//! prefix ALONE — one entry SHORTER than the declaration, because the tail slot names a TYPE rather than an
//! argument. That is what the marshal needs (its values are the call's), not what the declaration says.
static void FabricatorExpandVarArgs(idx_t varargs_index, const vector<string> &decl_names,
                                    const vector<LogicalType> &decl_types, const vector<Value> &values,
                                    vector<string> &out_names, vector<LogicalType> &out_types) {
	out_names.clear();
	out_types.clear();
	if (varargs_index == DConstants::INVALID_INDEX) {
		out_names = decl_names;
		out_types = decl_types;
		return;
	}
	auto tail_type = FabricatorVarArgsType(decl_types[varargs_index]);
	for (idx_t i = 0; i < values.size(); i++) {
		out_names.push_back(FabricatorArgName(decl_names, varargs_index, i));
		if (i < varargs_index) {
			out_types.push_back(decl_types[i]);
			continue;
		}
		// An ANY tail keeps the supplied value's OWN type — DuckDB inserted no cast, so the runtime type is
		// what the managed side must be handed. A concrete tail has already been cast to its declared type.
		out_types.push_back(tail_type.id() == LogicalTypeId::ANY ? values[i].type() : tail_type);
	}
}

static unique_ptr<FunctionData> FabricatorScalarBind(ClientContext &context, ScalarFunction &bound_function,
                                                    vector<unique_ptr<Expression>> &arguments) {
	auto &info = bound_function.function_info->Cast<FabricatorScalarFunctionInfo>();

	// ⚠⚠ THE AMBIENTS (txn + host-FS opener) ARE DELIBERATELY *NOT* ESTABLISHED HERE, and this is the one
	// place a scalar bind differs from every other bind in the tree. Do NOT "fix" it by adding
	// FabricatorSetActiveTxn — that was tried, and it SEGFAULTED the process.
	//
	// Every other bind (sqlgen, tablefn, the ALTER paths) is the bind of a statement's OWN source, so pushing
	// the calling context as the ambient opener is exactly right. A SCALAR binds wherever it is CALLED —
	// including inside a nested host query that some OUTER operation is running while IT holds the ambient.
	// The recluster is precisely that shape: OPTIMIZE issues a host query whose ORDER BY calls hilbert_index,
	// and the outer Delta write keeps doing host-FS IO afterwards. Setting the ambient to the INNER
	// connection's ClientContext leaves that outer IO resolving a context that is gone — the dangling-opener
	// use-after-free this codebase has paid for twice before (table_stats, RollbackTransaction).
	//
	// MEASURED, with the call as the only variable: verify_delta_clustered_optimize crashed at
	// `OPTIMIZE main.c1` (exit 127, and 139 — SIGSEGV — on its accumulated leg) with the call present, and
	// passes 147 assertions without it; a shell repro flipped the same way.
	//
	// Nothing needs it today, by construction rather than by luck: a discovered SQL UDF takes the DEFAULT
	// binding, which resolves nothing at bind (that is what the "declared type stands" sentinel is for), and
	// every custom scalar we ship is pure compute. If a provider ever does need its connection at bind, the
	// fix is a MANAGED-side scope that pushes and RESTORES the ambient (the InterruptScope shape) — the host
	// cannot restore it, because the ambient lives in an AsyncLocal the host can only overwrite.

	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	string arg_constant;
	for (idx_t i = 0; i < arguments.size(); i++) {
		auto &arg = *arguments[i];
		// A still-unresolved prepared-statement parameter can neither be folded nor typed, so the whole bind
		// must be DEFERRED: PREPARE p AS SELECT f(?) cannot yet know what f returns. This is the mechanism
		// DuckDB provides for exactly that (getvariable / strptime throw it too); without it the parameter
		// would silently arrive as an UNKNOWN-typed placeholder and be reported as a runtime slot.
		if (arg.HasParameter() || arg.return_type.id() == LogicalTypeId::UNKNOWN) {
			throw ParameterNotResolvedException();
		}
		// Marshal as the DECLARED parameter type wherever that is concrete. DuckDB is about to insert
		// exactly that cast (CastToFunctionArguments runs immediately after this bind returns), so using it
		// makes the bind's view of an argument AGREE with the batch execute will see, instead of differing by
		// a cast. It also keeps an untyped NULL literal off the SQLNULL path below: `f(NULL, …)` against a
		// declared VARCHAR parameter is a VARCHAR null here, exactly as at execute. Where the parameter is
		// ANY there is no cast, so the expression's own type is what execute sees too.
		LogicalType marshal_type = arg.return_type;
		if (i < bound_function.arguments.size() && bound_function.arguments[i].id() != LogicalTypeId::ANY &&
		    bound_function.arguments[i].id() != LogicalTypeId::INVALID) {
			marshal_type = bound_function.arguments[i];
		} else if (i >= bound_function.arguments.size() && bound_function.varargs.IsValid() &&
		           bound_function.varargs.id() != LogicalTypeId::ANY) {
			// A VARIADIC tail argument: past the fixed prefix there is no `arguments[i]` to read, and the
			// cast DuckDB is about to insert targets `varargs`. Same reasoning as the branch above — an ANY
			// tail gets no cast, so the expression's own type is what execute will see.
			marshal_type = bound_function.varargs;
		}
		bool folded = false;
		Value value(marshal_type); // NULL placeholder for a runtime expression
		if (arg.IsFoldable()) {
			try {
				value = ExpressionExecutor::EvaluateScalar(context, arg).DefaultCastAs(marshal_type);
				folded = true;
			} catch (std::exception &) {
				// Folding a constant expression can itself fail (1/0), and so can casting it to the declared
				// type. Neither is a bind failure — DuckDB's own constant-folding rule swallows such errors
				// and leaves the expression to be evaluated at execution, so we report the slot as runtime
				// and let the failure surface exactly where it does today.
				folded = false;
				value = Value(marshal_type);
			}
		}
		arg_types.push_back(marshal_type);
		arg_names.push_back(FabricatorArgName(info.arg_names, info.varargs_index, i));
		arg_constant.push_back(folded ? '1' : '0');
		arg_values.push_back(std::move(value));
	}

	auto properties = fabricator::BoundaryClientProperties(context);
	auto holder = make_shared_ptr<ScalarBindingHolder>();

	// The resolved result type arrives as a BARE ArrowSchema, read with ReadArrowSchema — the same carrier
	// and the same reader the declared-side get_function_return_schema uses, so extension types (VARIANT)
	// import identically.
	//
	// ⚠ NOT tablefn_bind's zero-row STREAM, and the difference is not cosmetic: reading a stream's schema
	// goes through PopulateReturnSchema, which SETS THE AMBIENT HOST-FS OPENER. A scalar binds wherever it is
	// called — including inside a host query that is itself doing host-FS IO, e.g. OPTIMIZE's recluster —
	// where clobbering that ambient killed the process outright (measured: verify_delta_clustered_optimize,
	// exit 127 with no output, at `OPTIMIZE main.c1`). A table function binds as the statement's own source,
	// so it never sits underneath someone else's IO.
	ArrowSchema result_schema {};
	std::memset(&result_schema, 0, sizeof(result_schema));
	if (arg_types.empty()) {
		// Zero-argument scalar: pass NO stream rather than an empty one. A zero-FIELD Arrow schema cannot
		// cross the C interface in EITHER direction (Apache.Arrow throws on 'fields'), which is the same
		// reason the exec callback sends a throwaway column and tablefn_bind passes nullptr here.
		holder->binding = fabricator::ScalarFnBind(info.handle, info.schema, info.func, nullptr,
		                                           arg_constant, MakeCallContext(context), result_schema);
	} else {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c]);
		}
		chunk.SetCardinality(1);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
		ArrowAppender appender(arg_types, 1, properties, extension_types);
		appender.Append(chunk, 0, 1, 1);
		ArrowArray array = appender.Finalize();
		// ⚠ An Arrow NULL-typed child must report null_count == length, and DuckDB does not set it:
		// ArrowNullData::Append only bumps row_count and its Finalize only clears n_buffers, so the array
		// crosses with null_count 0 and Apache.Arrow refuses it ("Length must equal null count"). Only an
		// ANY-declared parameter can still be SQLNULL here (a concrete one was cast above), which is why
		// this is reachable at all. Patch it rather than dropping the argument: "you passed an untyped NULL"
		// is exactly the kind of thing a bind resolving a result type needs to know.
		for (idx_t c = 0; c < arg_types.size() && (int64_t)c < array.n_children; c++) {
			if (arg_types[c].id() == LogicalTypeId::SQLNULL && array.children[c]) {
				array.children[c]->null_count = array.children[c]->length;
			}
		}
		fabricator::ArrowProducer producer(arg_types, arg_names, properties);
		producer.AddBatch(array);
		producer.Finish();
		holder->binding = fabricator::ScalarFnBind(info.handle, info.schema, info.func, producer.Stream(),
		                                           arg_constant, MakeCallContext(context), result_schema);
	}

	vector<LogicalType> result_types;
	vector<string> result_names;
	fabricator::ReadArrowSchema(context, result_schema, result_types, result_names);
	if (result_types.size() != 1) {
		throw BinderException("fabricator scalar function \"%s\" bound %llu result columns; exactly one is required",
		                      info.func, (uint64_t)result_types.size());
	}
	// The UNRESOLVED sentinel means "my result is the function's DECLARED type" — which the registered
	// function already carries, so leaving it alone is the whole handling. That is what lets the default
	// binding cost NOTHING: it does not have to re-derive (or re-fetch from the server) a type the host
	// resolved once when it materialized the catalog entry.
	if (result_types[0].id() != LogicalTypeId::SQLNULL && result_types[0].id() != LogicalTypeId::ANY) {
		bound_function.SetReturnType(result_types[0]);
	}
	if (bound_function.GetReturnType().id() == LogicalTypeId::ANY) {
		// Neither the declaration nor the bind produced a type. Refuse HERE, naming the function: an
		// unresolved ANY flowing onward gets no further validation (CheckTemplateTypesResolved guards only
		// TEMPLATE) and would fail far from its cause.
		throw BinderException(
		    "fabricator scalar function \"%s\" declares no return type and its bind did not resolve one",
		    info.func);
	}

	auto bind_data = make_uniq<FabricatorScalarBindData>();
	bind_data->holder = std::move(holder);
	return std::move(bind_data);
}

// Builds a ScalarFunction bound per call site by FabricatorScalarBind and executed per chunk over that
// binding: the callback marshals the arg chunk to Arrow, runs the UDF over the bridge (scalarfn_execute on
// the binding — which the bind resolved against `handle`, 0 for a connection-free GLOBAL scalar where the C#
// side resolves by name against the global registry), and ingests the single-column result. Shared by
// catalog-bound scalar UDFs (GetOrCreateScalarFunction) and load-time global scalars
// (RegisterFabricatorGlobalFunctions).
//
// `return_type` is the function's DECLARED type, used only to register the entry (so catalog listings and
// overload displays are accurate). SQLNULL there is the UNRESOLVED sentinel => registered as ANY and the
// bind MUST supply a type. Either way the bind's answer is what the call site uses.
static ScalarFunction BuildFabricatorScalarFunction(FabricatorHandle handle, const string &schema_name,
                                                  const string &fn_name, vector<LogicalType> arg_types,
                                                  vector<string> arg_names, LogicalType return_type,
                                                  bool is_volatile,
                                                  idx_t varargs_index = DConstants::INVALID_INDEX) {
	scalar_function_t exec = [arg_names, varargs_index](DataChunk &args, ExpressionState &state, Vector &result) {
		auto &ctx = state.GetContext();
		idx_t row_count = args.size();

		// Marshal the arg chunk -> a one-batch Arrow stream using the chunk's ACTUAL column types (not the
		// declared signature): for a SQLNULL-sentinel ("accept any value") param declared as ANY, DuckDB passes
		// the value UNCAST, so the runtime type (a STRUCT, a VARCHAR, …) is what must be appended. For a
		// concrete-typed param DuckDB has already cast to the declared type, so this equals the signature.
		auto actual_types = args.GetTypes();
		// Name the ACTUAL columns, not the declared ones: a VARIADIC call is wider than its declaration, and
		// the producer pairs names with types positionally — a short name list would misdescribe the batch.
		vector<string> marshal_names;
		for (idx_t c = 0; c < actual_types.size(); c++) {
			marshal_names.push_back(FabricatorArgName(arg_names, varargs_index, c));
		}

		// ── ZERO-ARGUMENT SCALAR: send one throwaway column ──────────────────────────────────────────
		// Apache.Arrow (23.0.0) cannot represent a zero-FIELD schema across the C interface in EITHER
		// direction — export AND import both raise ArgumentNullException('fields'); measured with positive
		// controls. `ArrowSchemaExport` hand-builds the empty struct for the EXPORT half, which is all a
		// zero-argument TABLE function needs (the host simply passes no args stream). A SCALAR's arg batch
		// crosses the OTHER way, so a 0-column batch would fail on IMPORT inside the bridge.
		//
		// Nothing about the row COUNT is the problem — a 0-column Arrow array carries its length perfectly
		// well, and exporting one works. (An older note here claimed zero-argument scalars were impossible
		// "because the arg batch is also how row count crosses". That reason was wrong; it is purely the
		// SCHEMA that cannot cross.) So marshal one throwaway column of `row_count` rows. The managed side
		// reads only its DECLARED parameters — none — and takes the count from the batch length, so it needs
		// no knowledge of this column.
		const bool zero_arg = actual_types.empty();
		if (zero_arg) {
			actual_types.push_back(LogicalType::BOOLEAN);
			marshal_names.push_back("__fabricator_rows");
		}

		auto properties = fabricator::BoundaryClientProperties(ctx);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(ctx, actual_types);
		ArrowAppender appender(actual_types, row_count, properties, extension_types);
		if (zero_arg) {
			// `args` has no columns to append from; build the placeholder. DuckDB sets the cardinality of the
			// argument chunk even when it has no columns (execute_function.cpp — SetCardinality is outside the
			// children loop), so `row_count` above is the true row count.
			DataChunk rows;
			rows.Initialize(Allocator::Get(ctx), actual_types, MaxValue<idx_t>(row_count, 1));
			rows.data[0].Reference(Value::BOOLEAN(false));
			rows.data[0].Flatten(row_count);
			rows.SetCardinality(row_count);
			appender.Append(rows, 0, row_count, row_count);
		} else {
			appender.Append(args, 0, row_count, row_count);
		}
		ArrowArray array = appender.Finalize();
		// ⚠ PRE-EXISTING DEFECT, fixed here rather than worked around: an Arrow NULL-typed child must report
		// null_count == length, and DuckDB does not set it (ArrowNullData::Append only bumps row_count; its
		// Finalize only clears n_buffers), so Apache.Arrow refuses the batch with "Length must equal null
		// count". Reachable whenever an ANY-declared parameter is handed an untyped NULL literal —
		// `fabricator_render('tpl', NULL)` failed this way long before the bind session existed, and no suite
		// covered it (the one NULL in the suites sat in a VARCHAR-declared position, which DuckDB casts).
		for (idx_t c = 0; c < actual_types.size() && (int64_t)c < array.n_children; c++) {
			if (actual_types[c].id() == LogicalTypeId::SQLNULL && array.children[c]) {
				array.children[c]->null_count = array.children[c]->length;
			}
		}

		fabricator::ArrowProducer producer(actual_types, marshal_names, properties);
		producer.AddBatch(array);
		producer.Finish();

		// The binding was resolved once, at bind, and is reused for every chunk. It rides the bound
		// expression's FunctionData, which is the only channel a non-capturing-identity exec has.
		auto &func_expr = state.expr.Cast<BoundFunctionExpression>();
		auto &bind_data = func_expr.bind_info->Cast<FabricatorScalarBindData>();
		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		fabricator::ScalarFnExecute(bind_data.holder->binding, *producer.Stream(), MakeCallContext(ctx), out);

		// Single-column, row_count-row result -> the output vector (matching offsets).
		fabricator::ArrowStreamReader reader(ctx, out);
		DataChunk chunk;
		chunk.Initialize(Allocator::Get(ctx), reader.Types());
		idx_t offset = 0;
		while (offset < row_count) {
			chunk.Reset();
			reader.Read(chunk);
			idx_t got = chunk.size();
			if (got == 0) {
				break; // defensive: backend returned fewer rows than requested
			}
			VectorOperations::Copy(chunk.data[0], result, got, 0, offset);
			offset += got;
		}
	};

	// Signature: a SQLNULL-typed param is the "accept any value" sentinel (no Arrow type for ANY) → register it
	// as LogicalType::ANY so DuckDB passes any literal (a STRUCT, a VARCHAR, …) UNCAST; the exec marshals the
	// runtime type. Same marker the table/proc named-param path uses (e.g. daxeval's params bag).
	vector<LogicalType> sig_types;
	for (idx_t i = 0; i < arg_types.size(); i++) {
		if (i == varargs_index) {
			continue; // the tail names a TYPE, not an argument slot — it becomes fn.varargs below
		}
		sig_types.push_back(arg_types[i].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[i]);
	}
	// An UNRESOLVED declared return (the SQLNULL sentinel) registers as ANY — the same mapping the
	// parameters above use for "accept anything", and the placeholder upstream uses for a bind-resolved
	// return type (getvariable). FabricatorScalarBind then refuses if it is still unresolved after the bind.
	if (return_type.id() == LogicalTypeId::SQLNULL) {
		return_type = LogicalType::ANY;
	}
	ScalarFunction fn(sig_types, return_type, exec, FabricatorScalarBind);
	fn.name = fn_name;
	if (varargs_index != DConstants::INVALID_INDEX) {
		// The declared parameters BEFORE the tail are now the MINIMUM arity; DuckDB accepts any number of
		// further arguments, each implicitly cast to this type (ANY => no cast at all).
		fn.varargs = FabricatorVarArgsType(arg_types[varargs_index]);
	}
	// Identity for the bind callback, which is a raw function pointer and cannot capture.
	auto fn_info = make_shared_ptr<FabricatorScalarFunctionInfo>();
	fn_info->handle = handle;
	fn_info->schema = schema_name;
	fn_info->func = fn_name;
	fn_info->arg_names = arg_names;
	fn_info->varargs_index = varargs_index;
	fn.SetExtraFunctionInfo(std::move(fn_info));
	// A remote UDF may be non-deterministic / side-effecting (VOLATILE => never folded) — the default. A
	// function DECLARED pure (fabricator.volatile = "0" on its return field, e.g. hilbert_index / bucket) is
	// CONSISTENT so constant args fold at plan time (partition pruning on `WHERE b = bucket(8, 'x')` depends
	// on it). Either way it may return non-NULL for NULL inputs, so it must see NULL args (SPECIAL_HANDLING)
	// rather than being short-circuited.
	fn.SetStability(is_volatile ? FunctionStability::VOLATILE : FunctionStability::CONSISTENT);
	fn.SetNullHandling(FunctionNullHandling::SPECIAL_HANDLING);
	return fn;
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateScalarFunction(ClientContext &context,
                                                                          const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = function_entries_.find(func_name);
	if (cached != function_entries_.end()) {
		return cached->second.get();
	}
	if (scalar_functions_.find(func_name) == scalar_functions_.end()) {
		return nullptr;
	}

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	vector<FabricatorParamStyle> arg_styles;
	LogicalType return_type;
	bool is_volatile = true;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types, &arg_styles);
		return_type = FetchFunctionReturnType(context, handle_, name, func_name, &is_volatile);
	} catch (std::exception &) {
		// The discovered name is stale — the function no longer exists on the server
		// (e.g. dropped out-of-band). Treat it as not-found rather than erroring.
		scalar_functions_.erase(func_name);
		RetireErase(function_entries_, func_name, retired_entries_);
		return nullptr;
	}

	// The per-call execution callback (shared with load-time global scalars). A malformed variadic
	// declaration throws HERE rather than in the fetch above — it is a declaration bug, not a stale name, and
	// the catch above would turn it into a silent "function does not exist".
	ScalarFunction fn = BuildFabricatorScalarFunction(
	    handle_, name, func_name, arg_types, arg_names, return_type, is_volatile,
	    FabricatorVarArgsIndex(func_name, arg_names, arg_styles));

	CreateScalarFunctionInfo info(std::move(fn));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<ScalarFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	function_entries_[func_name] = std::move(entry);
	return &ref;
}

// -----------------------------------------------------------------------------
// Custom aggregate functions (4h). DuckDB owns a contiguous array of fixed-size state blobs and drives the
// reduction through initialize/update/simple_update/combine/finalize/destructor callbacks. We keep each blob
// as just an int64 id; the real per-group accumulator lives in C# behind that id (a Dictionary keyed by id
// on a per-bound-aggregate session). The callbacks below marshal the id(s) + argument columns over the
// agg_* ABI. Window (OVER) needs no custom `window` callback — DuckDB drives windowing through these same
// update/combine/finalize via WindowSegmentTree, which is far cheaper for a marshaled bridge than one
// boundary crossing per output row; the destructor (wired) bounds the C# map for the window paths that
// churn transient states.
namespace {

// Identity + a monotonic id counter, carried on the AggregateFunction's function_info (aggregate callbacks
// are raw fn pointers — they can't capture). Reachable from initialize (function.function_info) and bind.
// Monotonic ids are never reused, so they never collide across threads or prepared-statement re-executions.
struct FabricatorAggregateFunctionInfo : public AggregateFunctionInfo {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	std::atomic<int64_t> counter {0};
	bool spillable = false; // bytes-in-blob mode (state serialized into DuckDB's blob → external spill)
};

// Spillable-mode state blob: [uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP]] — fixed-size + pointer-free so
// DuckDB's external GROUP BY spills it as raw bytes. A len of this sentinel = fresh/uninitialized (so
// `initialize` needs no C# call).
static constexpr uint32_t AGG_SPILL_SENTINEL = 0xFFFFFFFFu;

// Refcounted holder for the managed aggregate session, carried on the bind data. Its destructor calls
// agg_close (frees the managed id->accumulator map + GCHandle) on plan teardown — best-effort, idempotent.
struct AggSessionHolder {
	FabricatorHandle session = nullptr;
	~AggSessionHolder() {
		fabricator::AggClose(session);
	}
};

// Per-bound-aggregate state (FunctionData). bind runs once per bound plan; update/combine/finalize/destructor
// reach it via AggregateInputData.bind_data. Carries the managed session + the marshaling context (the
// aggregate callbacks are not handed a ClientContext, unlike scalar/table execution — so we capture what we
// need at bind: client properties, the update-batch extension types, and the connection's stable context).
struct FabricatorAggregateBindData : public FunctionData {
	shared_ptr<AggSessionHolder> holder;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	ClientProperties properties;
	optional_ptr<ClientContext> context; // connection context (stable across the bound plan); Arrow marshaling
	bool spillable = false;              // bytes-in-blob mode (see FabricatorAggregateFunctionInfo)

	unique_ptr<FunctionData> Copy() const override {
		auto c = make_uniq<FabricatorAggregateBindData>();
		c->holder = holder;
		c->arg_types = arg_types;
		c->arg_names = arg_names;
		c->properties = properties;
		c->context = context;
		c->spillable = spillable;
		return std::move(c);
	}
	bool Equals(const FunctionData &other_p) const override {
		return holder == other_p.Cast<FabricatorAggregateBindData>().holder;
	}
};

// Reads `count` state ids out of a state-pointer Vector via UnifiedVectorFormat — handles FLAT *and*
// CONSTANT (the ungrouped path passes a CONSTANT state vector to finalize/simple_update). The callback
// already receives a pointer to our {int64 id} blob, so Load<int64_t> reads the id directly.
void ReadStateIds(Vector &state, idx_t count, int64_t *out) {
	UnifiedVectorFormat sdata;
	state.ToUnifiedFormat(count, sdata);
	auto ptrs = UnifiedVectorFormat::GetData<data_ptr_t>(sdata);
	for (idx_t i = 0; i < count; i++) {
		out[i] = Load<int64_t>(ptrs[sdata.sel->get_index(i)]);
	}
}

// Marshal a single-column int64 Arrow batch from `ids` (no extension types — BIGINT has none).
ArrowArray BuildIdBatch(const ClientProperties &props, const int64_t *ids, idx_t count) {
	Vector id_vec(LogicalType::BIGINT);
	auto data = FlatVector::GetData<int64_t>(id_vec);
	for (idx_t i = 0; i < count; i++) {
		data[i] = ids[i];
	}
	vector<LogicalType> types {LogicalType::BIGINT};
	DataChunk batch;
	batch.InitializeEmpty(types);
	batch.data[0].Reference(id_vec);
	batch.SetCardinality(count);
	ArrowAppender appender(types, count, props, {});
	appender.Append(batch, 0, count, count);
	return appender.Finalize();
}

// Marshal a `[BIGINT key ++ inputs]` batch (key = state id in fast mode, dense group slot in spill mode).
ArrowArray BuildUpdateBatch(FabricatorAggregateBindData &bind, Vector &key_vec, Vector inputs[], idx_t input_count,
                            idx_t count) {
	vector<LogicalType> types;
	types.reserve(input_count + 1);
	types.push_back(LogicalType::BIGINT);
	for (idx_t i = 0; i < input_count; i++) {
		types.push_back(bind.arg_types[i]);
	}
	DataChunk batch;
	batch.InitializeEmpty(types);
	batch.data[0].Reference(key_vec);
	for (idx_t i = 0; i < input_count; i++) {
		batch.data[1 + i].Reference(inputs[i]);
	}
	batch.SetCardinality(count);
	// Extension types are recomputed here (the aggregate callbacks get no ClientContext, so we use the
	// connection context captured at bind) — mirrors the scalar path's GetExtensionTypes usage.
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(*bind.context, types);
	ArrowAppender appender(types, count, bind.properties, extension_types);
	appender.Append(batch, 0, count, count);
	return appender.Finalize();
}

// Fast mode: marshal [id ++ inputs] and send to agg_update.
void MarshalAggUpdate(FabricatorAggregateBindData &bind, Vector &id_vec, Vector inputs[], idx_t input_count,
                      idx_t count) {
	ArrowArray array = BuildUpdateBatch(bind, id_vec, inputs, input_count, count);
	fabricator::AggUpdate(bind.holder->session, array);
}

// ---- Spillable mode (bytes-in-blob): state travels as an Arrow BLOB column, one row per group; a NULL row
// = a fresh/empty group. The state blob is `[uint32 len][byte data]`. ----

// Fill a BLOB Vector with the current serialized state of each group blob (sentinel => NULL).
void FillBlobVector(Vector &vec, const data_ptr_t *blobs, idx_t count) {
	auto data = FlatVector::GetData<string_t>(vec);
	for (idx_t i = 0; i < count; i++) {
		uint32_t len = Load<uint32_t>(blobs[i]);
		if (len == AGG_SPILL_SENTINEL) {
			FlatVector::SetNull(vec, i, true);
		} else {
			data[i] = StringVector::AddStringOrBlob(
			    vec, string_t(reinterpret_cast<const char *>(blobs[i] + sizeof(uint32_t)), len));
		}
	}
}

// Build a single-column BLOB Arrow array holding the current serialized state of each group blob.
ArrowArray BuildSpillStateColumn(const ClientProperties &props, const data_ptr_t *blobs, idx_t count) {
	Vector vec(LogicalType::BLOB);
	FillBlobVector(vec, blobs, count);
	vector<LogicalType> types {LogicalType::BLOB};
	DataChunk chunk;
	chunk.InitializeEmpty(types);
	chunk.data[0].Reference(vec);
	chunk.SetCardinality(count);
	ArrowAppender appender(types, count == 0 ? 1 : count, props, {});
	appender.Append(chunk, 0, count, count);
	return appender.Finalize();
}

// Read a BLOB stream of new per-group state back into the group blobs (len-prefixed, capped).
void WriteBackSpillStates(ClientContext &context, ArrowArrayStream &out, const data_ptr_t *blobs, idx_t count) {
	fabricator::ArrowStreamReader reader(context, out);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(context), reader.Types());
	idx_t produced = 0;
	while (produced < count) {
		chunk.Reset();
		reader.Read(chunk);
		idx_t got = chunk.size();
		if (got == 0) {
			break;
		}
		UnifiedVectorFormat ovf;
		chunk.data[0].ToUnifiedFormat(got, ovf);
		auto strs = UnifiedVectorFormat::GetData<string_t>(ovf);
		for (idx_t j = 0; j < got; j++) {
			auto sv = strs[ovf.sel->get_index(j)];
			idx_t len = sv.GetSize();
			if (len > FABRICATOR_AGG_SPILL_CAP) {
				throw InvalidInputException(
				    "fabricator: spillable aggregate state is %llu bytes, exceeding the %d-byte cap", (idx_t)len,
				    FABRICATOR_AGG_SPILL_CAP);
			}
			data_ptr_t blob = blobs[produced + j];
			Store<uint32_t>(static_cast<uint32_t>(len), blob);
			std::memcpy(blob + sizeof(uint32_t), sv.GetData(), len);
		}
		produced += got;
	}
}

// Spillable update: group rows by state-blob pointer (dense slots), read each group's current state, run the
// chunk's rows for each group in C#, and write the new serialized state back into the blobs.
void AggUpdateSpillImpl(FabricatorAggregateBindData &bind, Vector inputs[], idx_t input_count, Vector &state,
                        idx_t count) {
	UnifiedVectorFormat sdata;
	state.ToUnifiedFormat(count, sdata);
	auto ptrs = UnifiedVectorFormat::GetData<data_ptr_t>(sdata);
	std::unordered_map<data_ptr_t, idx_t> slot_of;
	vector<data_ptr_t> group_blobs;
	Vector slot_vec(LogicalType::BIGINT);
	auto slot_data = FlatVector::GetData<int64_t>(slot_vec);
	for (idx_t i = 0; i < count; i++) {
		data_ptr_t blob = ptrs[sdata.sel->get_index(i)];
		auto it = slot_of.find(blob);
		idx_t slot;
		if (it == slot_of.end()) {
			slot = group_blobs.size();
			slot_of.emplace(blob, slot);
			group_blobs.push_back(blob);
		} else {
			slot = it->second;
		}
		slot_data[i] = static_cast<int64_t>(slot);
	}
	idx_t g = group_blobs.size();
	ArrowArray group_states = BuildSpillStateColumn(bind.properties, group_blobs.data(), g);
	ArrowArray batch = BuildUpdateBatch(bind, slot_vec, inputs, input_count, count);
	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	fabricator::AggUpdateSpill(bind.holder->session, group_states, batch, out);
	WriteBackSpillStates(*bind.context, out, group_blobs.data(), g);
}

// Spillable combine: assign dense slots to the distinct TARGET blobs (a target may repeat in one combine
// batch — the window segment-tree merges several source nodes into one frame target), build a `[slot, source]`
// batch, merge in C#, and write each distinct target's merged state back.
void AggCombineSpillImpl(FabricatorAggregateBindData &bind, Vector &source, Vector &target, idx_t count) {
	UnifiedVectorFormat sf, tf;
	source.ToUnifiedFormat(count, sf);
	target.ToUnifiedFormat(count, tf);
	auto sptr = UnifiedVectorFormat::GetData<data_ptr_t>(sf);
	auto tptr = UnifiedVectorFormat::GetData<data_ptr_t>(tf);

	std::unordered_map<data_ptr_t, idx_t> slot_of;
	vector<data_ptr_t> target_blobs;
	vector<data_ptr_t> source_blobs(count);
	Vector slot_vec(LogicalType::BIGINT);
	auto slot_data = FlatVector::GetData<int64_t>(slot_vec);
	for (idx_t i = 0; i < count; i++) {
		data_ptr_t tgt = tptr[tf.sel->get_index(i)];
		auto it = slot_of.find(tgt);
		idx_t slot;
		if (it == slot_of.end()) {
			slot = target_blobs.size();
			slot_of.emplace(tgt, slot);
			target_blobs.push_back(tgt);
		} else {
			slot = it->second;
		}
		slot_data[i] = static_cast<int64_t>(slot);
		source_blobs[i] = sptr[sf.sel->get_index(i)];
	}
	idx_t g = target_blobs.size();

	ArrowArray target_states = BuildSpillStateColumn(bind.properties, target_blobs.data(), g);
	// batch = [int64 slot, BLOB source]
	Vector src_vec(LogicalType::BLOB);
	FillBlobVector(src_vec, source_blobs.data(), count);
	vector<LogicalType> types {LogicalType::BIGINT, LogicalType::BLOB};
	DataChunk batch_chunk;
	batch_chunk.InitializeEmpty(types);
	batch_chunk.data[0].Reference(slot_vec);
	batch_chunk.data[1].Reference(src_vec);
	batch_chunk.SetCardinality(count);
	ArrowAppender appender(types, count, bind.properties, {});
	appender.Append(batch_chunk, 0, count, count);
	ArrowArray batch = appender.Finalize();

	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	fabricator::AggCombineSpill(bind.holder->session, target_states, batch, out);
	WriteBackSpillStates(*bind.context, out, target_blobs.data(), g);
}

// Spillable finalize: read each group's serialized state, finalize in C#, copy the result column out.
void AggFinalizeSpillImpl(FabricatorAggregateBindData &bind, Vector &state, Vector &result, idx_t count,
                          idx_t offset) {
	UnifiedVectorFormat sf;
	state.ToUnifiedFormat(count, sf);
	auto ptr = UnifiedVectorFormat::GetData<data_ptr_t>(sf);
	vector<data_ptr_t> blobs(count);
	for (idx_t i = 0; i < count; i++) {
		blobs[i] = ptr[sf.sel->get_index(i)];
	}
	ArrowArray states = BuildSpillStateColumn(bind.properties, blobs.data(), count);
	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	fabricator::AggFinalizeSpill(bind.holder->session, states, out);
	fabricator::ArrowStreamReader reader(*bind.context, out);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(*bind.context), reader.Types());
	idx_t produced = 0;
	while (produced < count) {
		chunk.Reset();
		reader.Read(chunk);
		idx_t got = chunk.size();
		if (got == 0) {
			break;
		}
		VectorOperations::Copy(chunk.data[0], result, got, 0, offset + produced);
		produced += got;
	}
}

idx_t FabricatorAggregateStateSize(const AggregateFunction &function) {
	auto &info = function.function_info->Cast<FabricatorAggregateFunctionInfo>();
	// Spillable: a fixed, pointer-free serialized-state blob ([uint32 len][data]); else an int64 id.
	return info.spillable ? (sizeof(uint32_t) + FABRICATOR_AGG_SPILL_CAP) : sizeof(int64_t);
}

void FabricatorAggregateInit(const AggregateFunction &function, data_ptr_t state) {
	auto &info = function.function_info->Cast<FabricatorAggregateFunctionInfo>();
	if (info.spillable) {
		Store<uint32_t>(AGG_SPILL_SENTINEL, state); // fresh/uninitialized; no C# call needed at init
	} else {
		Store<int64_t>(info.counter.fetch_add(1, std::memory_order_relaxed), state);
	}
}

unique_ptr<FunctionData> FabricatorAggregateBind(ClientContext &context, AggregateFunction &function,
                                               vector<unique_ptr<Expression>> &) {
	auto &info = function.function_info->Cast<FabricatorAggregateFunctionInfo>();
	auto bind_data = make_uniq<FabricatorAggregateBindData>();
	bind_data->holder = make_shared_ptr<AggSessionHolder>();
	bind_data->holder->session = fabricator::AggOpen(info.handle, info.schema, info.func);
	bind_data->arg_types = info.arg_types;
	bind_data->arg_names = info.arg_names;
	bind_data->properties = fabricator::BoundaryClientProperties(context);
	bind_data->context = &context;
	bind_data->spillable = info.spillable;
	return std::move(bind_data);
}

// Grouped GROUP BY: a FLAT vector of one state pointer per row (rows belong to different groups).
void FabricatorAggregateUpdate(Vector inputs[], AggregateInputData &aggr_input_data, idx_t input_count, Vector &state,
                             idx_t count) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<FabricatorAggregateBindData>();
	if (bind.spillable) {
		AggUpdateSpillImpl(bind, inputs, input_count, state, count);
		return;
	}
	Vector id_vec(LogicalType::BIGINT);
	ReadStateIds(state, count, FlatVector::GetData<int64_t>(id_vec));
	MarshalAggUpdate(bind, id_vec, inputs, input_count, count);
}

// Ungrouped fast path: all `count` rows fold into one state (no per-row state vector).
void FabricatorAggregateSimpleUpdate(Vector inputs[], AggregateInputData &aggr_input_data, idx_t input_count,
                                   data_ptr_t state, idx_t count) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<FabricatorAggregateBindData>();
	Vector key_vec(LogicalType::BIGINT);
	auto data = FlatVector::GetData<int64_t>(key_vec);
	if (bind.spillable) {
		// All rows fold into the single state `state`: one group (slot 0).
		for (idx_t i = 0; i < count; i++) {
			data[i] = 0;
		}
		ArrowArray group_states = BuildSpillStateColumn(bind.properties, &state, 1);
		ArrowArray batch = BuildUpdateBatch(bind, key_vec, inputs, input_count, count);
		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		fabricator::AggUpdateSpill(bind.holder->session, group_states, batch, out);
		WriteBackSpillStates(*bind.context, out, &state, 1);
		return;
	}
	int64_t id = Load<int64_t>(state);
	for (idx_t i = 0; i < count; i++) {
		data[i] = id;
	}
	MarshalAggUpdate(bind, key_vec, inputs, input_count, count);
}

// Merge partial states (parallel/windowed aggregation): source[i] merged into target[i].
void FabricatorAggregateCombine(Vector &source, Vector &target, AggregateInputData &aggr_input_data, idx_t count) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<FabricatorAggregateBindData>();
	if (bind.spillable) {
		AggCombineSpillImpl(bind, source, target, count);
		return;
	}
	vector<int64_t> tgt(count), src(count);
	ReadStateIds(target, count, tgt.data());
	ReadStateIds(source, count, src.data());
	Vector tgt_vec(LogicalType::BIGINT);
	Vector src_vec(LogicalType::BIGINT);
	auto tgt_data = FlatVector::GetData<int64_t>(tgt_vec);
	auto src_data = FlatVector::GetData<int64_t>(src_vec);
	for (idx_t i = 0; i < count; i++) {
		tgt_data[i] = tgt[i];
		src_data[i] = src[i];
	}
	vector<LogicalType> types {LogicalType::BIGINT, LogicalType::BIGINT};
	DataChunk batch;
	batch.InitializeEmpty(types);
	batch.data[0].Reference(tgt_vec); // column 0 = target_id, column 1 = source_id (C# merges source -> target)
	batch.data[1].Reference(src_vec);
	batch.SetCardinality(count);
	ArrowAppender appender(types, count, bind.properties, {});
	appender.Append(batch, 0, count, count);
	ArrowArray array = appender.Finalize();
	fabricator::AggCombine(bind.holder->session, array);
}

// Produce each group's result. `state` may be CONSTANT (ungrouped) or FLAT (grouped). The managed side
// returns one column of `count` results in id order (an absent id => a fresh accumulator => empty value).
void FabricatorAggregateFinalize(Vector &state, AggregateInputData &aggr_input_data, Vector &result, idx_t count,
                               idx_t offset) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<FabricatorAggregateBindData>();
	if (bind.spillable) {
		AggFinalizeSpillImpl(bind, state, result, count, offset);
		return;
	}
	vector<int64_t> ids(count);
	ReadStateIds(state, count, ids.data());
	ArrowArray array = BuildIdBatch(bind.properties, ids.data(), count);
	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	fabricator::AggFinalize(bind.holder->session, array, out);
	if (!bind.context) {
		if (out.release) {
			out.release(&out);
		}
		throw InternalException("fabricator: aggregate finalize is missing its client context");
	}
	fabricator::ArrowStreamReader reader(*bind.context, out);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(*bind.context), reader.Types());
	idx_t produced = 0;
	while (produced < count) {
		chunk.Reset();
		reader.Read(chunk);
		idx_t got = chunk.size();
		if (got == 0) {
			break; // defensive: backend returned fewer rows than requested
		}
		VectorOperations::Copy(chunk.data[0], result, got, 0, offset + produced);
		produced += got;
	}
}

// NO per-state destructor is registered — see BuildFabricatorAggregateFunction. It used to call
// fabricator::AggDestroy(bind.holder->session, ...) to free the managed accumulators eagerly, which is a
// USE-AFTER-FREE of the bind data:
//
//   PhysicalOperator::sink_state is a member of the BASE class, while the bound aggregate expressions that
//   OWN the FunctionData are members of the derived operator (e.g. PhysicalUngroupedAggregate). C++ destroys
//   derived members before base members, so at plan teardown the bind data is already gone by the time
//   sink_state's destructor walks the aggregate states and invokes this callback.
//
// The ordering is deterministic; whether it FAULTS depends on whether the freed memory has been reused, so
// it was invisible on Windows and fatal on POSIX — Linux SIGSEGV, macOS SIGABRT, both at the same
// assertion in verify_global_functions. gdb showed the object fully recycled: shared_ptr use_count
// 1499900208, get() = 0xd269a5bf5a0b0aef, garbage arg_types/arg_names, with only the vptr still plausible
// enough for Cast<> to succeed.
//
// AggSessionHolder's own destructor calls agg_close, which drops the whole id->accumulator map for the
// session, so nothing leaks beyond the query. What is given up is the eager release of the transient states
// the window/segment-tree paths churn: those accumulators now live until the query's session closes.
// Restoring that bound needs a design that does not depend on member destruction order — e.g. widening the
// state blob past its 8-byte id to carry the session handle, so the callback need not touch bind data at
// all. Correctness first; the optimisation can return deliberately.

} // namespace

// Builds an AggregateFunction whose state-vectorized callbacks marshal per-group int64 ids + Arrow batches to
// the C# session over the agg_* ABI (the scalar session's aggregate analog). `handle` = 0 for a connection-free
// GLOBAL aggregate (C# resolves by name); `spillable` selects the bytes-in-blob mode (the callbacks branch on
// the flag). Shared by catalog-bound aggregates (GetOrCreateAggregateFunction) + load-time global aggregates.
static AggregateFunction BuildFabricatorAggregateFunction(FabricatorHandle handle, const string &schema_name,
                                                        const string &func_name, vector<LogicalType> arg_types,
                                                        vector<string> arg_names, LogicalType return_type,
                                                        bool spillable) {
	// destructor = nullptr, deliberately: our state blob is a bare int64 id with no C++ resource to release,
	// and a callback here cannot safely reach the bind data at plan teardown (see the comment above — it was
	// a use-after-free that faulted on POSIX and hid on Windows). The managed accumulators are freed by
	// AggSessionHolder's agg_close instead.
	AggregateFunction fn(func_name, arg_types, return_type, FabricatorAggregateStateSize, FabricatorAggregateInit,
	                     FabricatorAggregateUpdate, FabricatorAggregateCombine, FabricatorAggregateFinalize,
	                     FunctionNullHandling::DEFAULT_NULL_HANDLING, FabricatorAggregateSimpleUpdate,
	                     FabricatorAggregateBind, nullptr);
	auto fn_info = make_shared_ptr<FabricatorAggregateFunctionInfo>();
	fn_info->handle = handle;
	fn_info->schema = schema_name;
	fn_info->func = func_name;
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->spillable = spillable;
	fn.function_info = std::move(fn_info);
	return fn;
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateAggregateFunction(ClientContext &context,
                                                                             const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = aggregate_function_entries_.find(func_name);
	if (cached != aggregate_function_entries_.end()) {
		return cached->second.get();
	}
	auto spill_it = aggregate_functions_.find(func_name);
	if (spill_it == aggregate_functions_.end()) {
		return nullptr;
	}
	bool spillable = spill_it->second;

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	vector<FabricatorParamStyle> arg_styles;
	LogicalType return_type;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types, &arg_styles);
		return_type = FetchFunctionReturnType(context, handle_, name, func_name);
	} catch (std::exception &) {
		// Stale discovery (the function no longer exists) — treat as not-found, like the scalar path.
		aggregate_functions_.erase(func_name);
		RetireErase(aggregate_function_entries_, func_name, retired_entries_);
		return nullptr;
	}

	// Outside the catch above: a malformed declaration is a bug to report, not a stale name to drop.
	FabricatorRefuseVarArgs(func_name, "aggregate", arg_names, arg_styles);
	AggregateFunction fn =
	    BuildFabricatorAggregateFunction(handle_, name, func_name, arg_types, arg_names, return_type, spillable);

	AggregateFunctionSet set(func_name);
	set.AddFunction(fn);
	CreateAggregateFunctionInfo info(std::move(set));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<AggregateFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	aggregate_function_entries_[func_name] = std::move(entry);
	return &ref;
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateMacro(ClientContext &context, const string &macro_name,
                                                                  bool want_table) {
	// NOTE this is the one GetOrCreate* here that makes NO bridge call: a macro body is a declaration the
	// provider already handed us at discovery, so materializing it is a local parse. `context` is used only for
	// the parser options and the skip warning.
	auto want_type = want_table ? CatalogType::TABLE_MACRO_ENTRY : CatalogType::MACRO_ENTRY;
	lock_guard<mutex> lock(entry_lock_);
	auto cached = macro_entries_.find(macro_name);
	if (cached != macro_entries_.end()) {
		// Filter by KIND, not just by name: a scalar lookup must not surface a table macro (see the header —
		// the binder Cast<>s on the entry's actual type without checking).
		return cached->second->type == want_type ? optional_ptr<CatalogEntry>(cached->second.get()) : nullptr;
	}
	auto decl = macros_.find(macro_name);
	if (decl == macros_.end()) {
		return nullptr;
	}

	unique_ptr<CatalogEntry> entry;
	try {
		// DuckDB's OWN parser owns the macro grammar — so named-parameter defaults, overload sets and
		// `AS TABLE <query>` all work, and the parsed statement is what tells us which KIND this is. Same route
		// as the global registration; only the qualification handling differs (below).
		Parser parser(context.GetParserOptions());
		parser.ParseQuery(decl->second);
		if (parser.statements.size() != 1 || parser.statements[0]->type != StatementType::CREATE_STATEMENT) {
			throw ParserException("expected a single CREATE MACRO statement");
		}
		auto &create = parser.statements[0]->Cast<CreateStatement>();
		if (create.info->type != CatalogType::MACRO_ENTRY && create.info->type != CatalogType::TABLE_MACRO_ENTRY) {
			throw ParserException("expected CREATE MACRO (scalar) or CREATE MACRO ... AS TABLE");
		}
		auto info = unique_ptr_cast<CreateInfo, CreateMacroInfo>(std::move(create.info));
		// The declared name must agree with the discovered one: we cache under the DISCOVERED name and hand the
		// entry straight to the binder, so a mismatch would leave one of the two names permanently unreachable.
		if (!StringUtil::CIEquals(info->name, macro_name)) {
			throw ParserException("declared macro name '%s' does not match the discovered name '%s'", info->name,
			                      macro_name);
		}
		// QUALIFICATION IS OVERWRITTEN HERE — the OPPOSITE of the global registration, which rejects a qualified
		// body because those land in the system catalog's main schema. A catalog-bound macro must carry THIS
		// catalog's ATTACH alias (chosen by the user at ATTACH time, so a static declaration cannot state it) and
		// this schema. Do not copy the global branch's validation into this path: it asserts the inverse.
		info->catalog = catalog.GetName();
		info->schema = name;
		// NOT `internal`: that marks a system/built-in entry, which is right for the load-time global macros and
		// wrong for a catalog entry (none of our other catalog entries set it either).
		if (info->type == CatalogType::TABLE_MACRO_ENTRY) {
			entry = make_uniq<TableMacroCatalogEntry>(catalog, *this, *info);
		} else {
			entry = make_uniq<ScalarMacroCatalogEntry>(catalog, *this, *info);
		}
	} catch (std::exception &ex) {
		// SKIP, never block — the same contract the global macro registration ships with. Dropping the
		// declaration also stops us re-parsing a broken body on every lookup; the call then fails with DuckDB's
		// ordinary "function does not exist", and duckdb_functions()/Scan keeps working (a throw here would
		// break enumeration of the whole schema, which is a far worse failure than one absent macro).
		DUCKDB_LOG_WARNING(context, StringUtil::Format("fabricator: catalog macro '%s.%s' skipped: %s", name,
		                                               macro_name, ex.what()));
		macros_.erase(macro_name);
		return nullptr;
	}

	auto &ref = *entry;
	macro_entries_[macro_name] = std::move(entry);
	// Parsed fine but it is the OTHER kind (a table macro reached through a scalar lookup, or vice versa): the
	// entry is cached either way, so the matching lookup finds it without re-parsing, but THIS lookup reports
	// not-found.
	return ref.type == want_type ? optional_ptr<CatalogEntry>(&ref) : nullptr;
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateView(ClientContext &context, const string &view_name) {
	// Like GetOrCreateMacro this makes NO bridge call — a view body is a declaration the provider handed us
	// at discovery, so materializing it is a local PARSE.
	//
	// ⚠⚠ PARSE ONLY — deliberately NOT CreateViewInfo::FromCreateView, which is the obvious helper and is
	// wrong here in two ways. It BINDS the body (create_view_info.cpp:93-94), so (a) it would re-enter
	// LookupEntry -> GetOrCreateEntry -> entry_lock_ on this thread and DEADLOCK on a non-recursive mutex,
	// and (b) it would make declaration order matter, turning "references an object declared later" from an
	// ordinary binder error at first use into a materialization failure. Leaving types/names empty is what
	// constructs the entry UNBOUND (view_catalog_entry.cpp:20-34, `if (!info.types.empty())`); DuckDB then
	// binds it lazily and calls UpdateBinding on first use.
	lock_guard<mutex> lock(entry_lock_);
	auto cached = view_entries_.find(view_name);
	if (cached != view_entries_.end()) {
		return cached->second.get();
	}
	auto decl = views_.find(view_name);
	if (decl == views_.end()) {
		return nullptr;
	}

	unique_ptr<CatalogEntry> entry;
	try {
		// DuckDB's OWN parser owns the CREATE VIEW grammar, so column aliases (`CREATE VIEW v(a,b) AS ...`)
		// and every SELECT form work without us knowing anything about them. It also fills info->sql with the
		// statement text (parser.cpp:368), which is what duckdb_views().sql reports.
		Parser parser(context.GetParserOptions());
		parser.ParseQuery(decl->second);
		if (parser.statements.size() != 1 || parser.statements[0]->type != StatementType::CREATE_STATEMENT) {
			throw ParserException("expected a single CREATE VIEW statement");
		}
		auto &create = parser.statements[0]->Cast<CreateStatement>();
		if (create.info->type != CatalogType::VIEW_ENTRY) {
			throw ParserException("expected CREATE VIEW");
		}
		auto info = unique_ptr_cast<CreateInfo, CreateViewInfo>(std::move(create.info));
		// The declared name must agree with the discovered one — we cache under the DISCOVERED name and hand
		// the entry straight to the binder, so a mismatch leaves one of the two permanently unreachable.
		if (!StringUtil::CIEquals(info->view_name, view_name)) {
			throw ParserException("declared view name '%s' does not match the discovered name '%s'",
			                      info->view_name, view_name);
		}
		// Re-qualified onto THIS catalog + schema, same as a catalog macro.
		//
		// ⚠ THIS IS NOT WHAT ANCHORS THE BODY, and a comment here said it was until a mutant refuted it:
		// removing both lines leaves the whole suite green, §4's decoy included. DuckDB's view binder takes
		// the search path from `view_catalog_entry.ParentCatalog()` / `ParentSchema().name`
		// (bind_basetableref.cpp:309-311), and those come from the ViewCatalogEntry CONSTRUCTOR arguments
		// below — which are this catalog and this schema by construction. The anchoring is free; nothing
		// here has to arrange it.
		//
		// What these two lines DO buy is that the entry describes itself consistently: CreateInfo's
		// catalog/schema feed GetInfo() / ToSQL() / duckdb_views().sql, so a provider that shipped a
		// QUALIFIED body would otherwise leave an entry living here while claiming to live somewhere else.
		info->catalog = catalog.GetName();
		info->schema = name;
		// Belt and braces: a parsed CREATE VIEW carries no bound types, and empty types is what selects the
		// UNBOUND construction path. Stating it means a future parser change cannot quietly bind us.
		info->types.clear();
		info->names.clear();
		entry = make_uniq<ViewCatalogEntry>(catalog, *this, *info);
	} catch (std::exception &ex) {
		// SKIP, never block — the macro contract. A broken body must not break enumeration of the whole
		// schema, and dropping the declaration stops us re-parsing it on every lookup. Note this catches a
		// PARSE failure only: a body that parses but references something absent fails later, at BIND, with
		// DuckDB's own error naming the missing object — which is the better message anyway.
		DUCKDB_LOG_WARNING(context, StringUtil::Format("fabricator: catalog view '%s.%s' skipped: %s", name,
		                                               view_name, ex.what()));
		views_.erase(view_name);
		return nullptr;
	}

	auto &ref = *entry;
	view_entries_[view_name] = std::move(entry);
	return &ref;
}

namespace {

// Carried on the registered TableFunction so its (static) bind can recover the catalog
// identity + signature of a discovered TVF (table_function_bind_t is a raw fn pointer,
// so it can't capture — unlike the scalar callback's std::function).
struct FabricatorTableFunctionInfo : public TableFunctionInfo {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	// Each declared parameter's STYLE, parallel to arg_names. Empty (or all POSITIONAL) is what a discovered
	// TVF and every pre-named-parameter provider function look like. The provider tags them via
	// `fabricator.param_style` in the parameter schema's field metadata — ONE schema carries all styles.
	vector<FabricatorParamStyle> arg_styles;
	//! Position of the declared VARARGS tail within arg_names/arg_types, or INVALID_INDEX. STORED rather
	//! than derived from arg_styles: the SQL-GENERATING path stores a FILTERED declaration (named parameters
	//! are re-added by name at bind), so the two lists do not share indices there.
	idx_t varargs_index = DConstants::INVALID_INDEX;
	bool is_proc = false;    // stored procedure (EXEC, no pushdown) vs TVF (FROM, pushdown)
	// SQL-generating (`table_sql`, v68) functions only: the DuckDB ATTACH ALIAS of the catalog this entry
	// belongs to (empty for a global function). Passed to generate_table_sql so a catalog-bound generator can
	// emit references back into its own catalog — C# never sees the alias otherwise.
	string catalog_name;
	// The function's source orders strings the way DuckDB does (byte/binary), so string ordering comparisons +
	// BETWEEN are superset-safe to push (e.g. a Delta/Parquet reader — byte-ordered stats). Default false:
	// discovered SQL TVFs run on SQL Server under its (possibly case-insensitive) collation, so only string
	// equality is pushed for them. Set true for a byte-ordered global host-FS reader (declared in C#). Copied
	// onto the scan bind data so FabricatorComplexFilterPushdown's FilterSerializer honors it.
	bool string_order_pushable = false;
	// ABI v81: the catalog whose metadata cache a DDL-performing function invalidates. Null for a GLOBAL
	// function, which belongs to no catalog and therefore has nothing to invalidate.
	//
	// ⚠ Lifetime: this info object is owned by the TableFunction on a catalog entry, so it cannot outlive the
	// catalog — the same scoping that makes carrying `handle` here safe. Deliberately NOT a ClientContext,
	// which is the recorded dangling-pointer class (a catalog is DATABASE-scoped and outlives the connection
	// that attached it).
	FabricatorCatalog *catalog = nullptr;
};

// Per-plan binding handle for the session-model table functions (tablefn_bind / tablefn_execute / tablefn_close).
// Held (refcounted) on the bind data's scan factory; its destructor frees the managed binding at plan
// teardown. The per-execution provider connection lives in tablefn_execute's result stream (released by the
// arrow scan at teardown), so the binding itself holds no connection — tablefn_close is metadata cleanup.
struct TableFnBindState {
	FabricatorHandle binding = nullptr;
	bool supports_pushdown = false;
	~TableFnBindState() {
		fabricator::TableFnClose(binding);
	}
};

// Bind a catalog-bound TVF / proc / custom table function (Phase 5 session model): tablefn_bind resolves the
// output schema (return types) + pushdown + an opaque binding, then a scan factory runs tablefn_execute over
// that binding per execution (which streams the result rows). See TableFnBindState + abi.h.
unique_ptr<FunctionData> FabricatorTableFunctionBind(ClientContext &context, TableFunctionBindInput &input,
                                                   vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<FabricatorTableFunctionInfo>();
	FabricatorHandle handle = info.handle;
	string schema_name = info.schema;
	string func_name = info.func;
	bool is_proc = info.is_proc;

	// Resolve the values to marshal into the 1-row args batch (the field NAMES become the
	// proc parameter names that C# uses to build `EXEC @name=@p`). TVFs: all params,
	// positional, in order (`input.inputs`). Procs: only the SUPPLIED named parameters
	// (`input.named_parameters`), each cast to its declared type — omitted params are absent.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	if (is_proc) {
		for (auto &kv : input.named_parameters) {
			LogicalType declared = kv.second.type();
			for (idx_t i = 0; i < info.arg_names.size(); i++) {
				if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
					// A SQLNULL-declared param is the "accept any value" marker (registered as ANY):
					// keep the supplied value's RUNTIME type so a STRUCT bag (or a VARCHAR, …) marshals
					// across as its real Arrow type, not coerced to the declared type.
					if (info.arg_types[i].id() != LogicalTypeId::SQLNULL) {
						declared = info.arg_types[i];
					}
					break;
				}
			}
			arg_names.push_back(kv.first);
			arg_types.push_back(declared);
			arg_values.push_back(kv.second);
		}
	} else if (std::find(info.arg_styles.begin(), info.arg_styles.end(), FabricatorParamStyle::NAMED) !=
	           info.arg_styles.end()) {
		// MIXED positional + named (a provider-authored function with optional arguments). Marshal EVERY
		// declared parameter in DECLARED ORDER, substituting a typed NULL for a named one the caller
		// omitted. That keeps the managed side reading args BY POSITION exactly as it does for a purely
		// positional function — an omitted optional argument and an explicit NULL are deliberately the same
		// thing, which is the semantic a provider already has to implement for a nullable trailing argument.
		idx_t positional_index = 0;
		for (idx_t i = 0; i < info.arg_names.size(); i++) {
			auto style = i < info.arg_styles.size() ? info.arg_styles[i] : FabricatorParamStyle::POSITIONAL;
			bool named = style == FabricatorParamStyle::NAMED;
			LogicalType declared = info.arg_types[i];
			if (style == FabricatorParamStyle::VARARGS) {
				// The VARIADIC tail takes EVERY remaining positional argument (it is the last positional
				// parameter by construction), each named `<tail>_<k>`. An ANY tail keeps each value's own
				// runtime type — DuckDB inserted no cast for it.
				auto tail_type = FabricatorVarArgsType(declared);
				for (idx_t k = positional_index; k < input.inputs.size(); k++) {
					auto &v = input.inputs[k];
					arg_names.push_back(info.arg_names[i] + "_" + to_string(k - positional_index));
					arg_types.push_back(tail_type.id() == LogicalTypeId::ANY ? v.type() : tail_type);
					arg_values.push_back(v);
				}
				positional_index = input.inputs.size();
				continue;
			}
			if (!named) {
				Value v = positional_index < input.inputs.size() ? input.inputs[positional_index]
				                                                 : Value(declared);
				positional_index++;
				// A SQLNULL-declared (ANY) parameter keeps the supplied value's RUNTIME type, as in the
				// proc branch — coercing it would defeat the point of declaring ANY.
				arg_types.push_back(declared.id() == LogicalTypeId::SQLNULL ? v.type() : declared);
				arg_names.push_back(info.arg_names[i]);
				arg_values.push_back(v);
				continue;
			}
			auto supplied = input.named_parameters.find(info.arg_names[i]);
			if (supplied != input.named_parameters.end()) {
				arg_types.push_back(declared.id() == LogicalTypeId::SQLNULL ? supplied->second.type() : declared);
				arg_names.push_back(info.arg_names[i]);
				arg_values.push_back(supplied->second);
			} else {
				// Unsupplied. An ANY-declared parameter has no runtime type to borrow here, so carry the
				// NULL as VARCHAR — the managed side only ever sees "absent" either way.
				auto null_type = declared.id() == LogicalTypeId::SQLNULL ? LogicalType::VARCHAR : declared;
				arg_types.push_back(null_type);
				arg_names.push_back(info.arg_names[i]);
				arg_values.push_back(Value(null_type));
			}
		}
	} else {
		// ⚠ The values are the ACTUAL call's, so for a VARIADIC function there are MORE of them than declared
		// types — and the marshal below initializes its chunk from `arg_types` while looping `arg_values`.
		// Expanding here is what keeps that loop in bounds; a non-variadic function comes back unchanged.
		arg_values = input.inputs;
		FabricatorExpandVarArgs(info.varargs_index, info.arg_names, info.arg_types, arg_values, arg_names,
		                        arg_types);
	}

	auto bind_data = make_uniq<fabricator::ArrowStreamBindData>();
	// A byte-ordered source (e.g. a Delta/Parquet global reader) can safely push string ordering + BETWEEN;
	// discovered SQL TVFs leave this false (collation-dependent). Read by the shared FilterSerializer.
	bind_data->string_order_pushable = info.string_order_pushable;

	auto properties = fabricator::BoundaryClientProperties(context);
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);

	// Marshal the constant call args into a 1-row Arrow array (shared by the output-schema resolution and the
	// scan): a custom table function's output schema MAY depend on the args, so they cross at bind time too.
	auto marshal_args = [arg_types, arg_values, properties, extension_types]() -> ArrowArray {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		ArrowAppender appender(arg_types, 1, properties, extension_types);
		appender.Append(chunk, 0, 1, 1);
		return appender.Finalize();
	};

	// 1) Bind the call (Phase 5 session model): tablefn_bind resolves the output schema (-> return types),
	//    whether the host should push the projection, and an opaque binding handle reused by every execution.
	//    The managed side classifies the function (TVF / proc / custom), so the host no longer branches on
	//    is_proc here (is_proc above is only the named-vs-positional arg marshaling). The binding is freed at
	//    plan teardown via the refcounted TableFnBindState captured on the scan factory.
	auto bind_state = make_shared_ptr<TableFnBindState>();
	bind_data->factory = [handle, schema_name, func_name, arg_types, arg_names, properties, marshal_args,
	                      bind_state](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		if (arg_types.empty()) {
			// A function taking NO arguments: pass no stream at all rather than an empty one. `args` is
			// nullable by contract, and an ARGUMENT-LESS Arrow batch cannot cross — a zero-field schema is
			// unrepresentable in Apache.Arrow's C-interface importer AND exporter (it throws
			// ArgumentNullException on 'fields'), so marshaling one fails the bind with an error that names
			// nothing recognizable. Keeping this branch is what makes zero-argument table functions work,
			// which is the shape a catalog-bound function that infers everything from its ATTACH wants.
			bind_state->binding = fabricator::TableFnBind(handle, schema_name, func_name, nullptr, out,
			                                          bind_state->supports_pushdown);
			return;
		}
		ArrowArray array = marshal_args();
		fabricator::ArrowProducer producer(arg_types, arg_names, properties);
		producer.AddBatch(array);
		producer.Finish();
		bind_state->binding = fabricator::TableFnBind(handle, schema_name, func_name, producer.Stream(), out,
		                                          bind_state->supports_pushdown);
	};
	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);

	// 2) Scan factory: tablefn_execute over the bound binding (per execution). spec_json/filter_values push
	//    projection + filter into the SELECT when the binding supports it (a discovered TVF); else ignored.
	// ABI v81: a provider function that performs DDL reports it through tablefn_execute's schema_may_change
	// out-flag, and the catalog RECORDS it for a deferred rebuild. Acting on it here would retire the entry
	// this very statement is scanning (see FabricatorCatalog::MarkSchemaMayChange); the refresh happens at the
	// next transaction start instead.
	//
	// ⚠ Capturing the catalog by pointer is safe for the same reason capturing `handle` above is: both are
	// DATABASE-scoped and outlive every plan that can reference them, unlike a ClientContext (whose capture
	// is the recorded dangling-pointer class). A DETACH invalidates the plans that hold either.
	auto *catalog_ptr = info.catalog;
	bind_data->factory = [bind_state, catalog_ptr](const fabricator::ArrowScanRequest &req,
	                                               ArrowArrayStream &out) {
		bool schema_may_change = false;
		fabricator::TableFnExecute(bind_state->binding, req.spec_json, req.filter_values, out,
		                           &schema_may_change);
		if (schema_may_change && catalog_ptr) {
			catalog_ptr->MarkSchemaMayChange();
		}
	};
	bind_data->push_projection = bind_state->supports_pushdown;
	return std::move(bind_data);
}

// -----------------------------------------------------------------------------
// SQL-GENERATING table functions (ABI v68; docs/macros-and-sqlgen-functions.md §2). A `table_sql` function
// has NO bind and NO scan: only `bind_replace`, DuckDB's "rewrite this call into a plan subtree" hook (what
// query_table() uses). At bind time the constant args cross to C#, which returns a SELECT statement; we parse
// it and hand the binder a SubqueryRef, which it re-binds in the calling context. So the function call
// DISAPPEARS at bind: what executes is a native DuckDB plan (with all of its pushdown/parallelism, including
// into this extension's own catalog scans), and NO data crosses the ABI at execution.
// -----------------------------------------------------------------------------

// Parse a generated statement into a subquery ref — the shape query_table() uses (query_function.cpp's
// ParseSubquery): exactly one SELECT statement. A PIVOT without an explicit IN list parses to a
// MultiStatement and is rejected here, same as upstream.
unique_ptr<TableRef> FabricatorParseGeneratedSelect(const string &sql, const ParserOptions &options,
                                                   const string &fn_name) {
	Parser parser(options);
	parser.ParseQuery(sql);
	if (parser.statements.size() != 1 || parser.statements[0]->type != StatementType::SELECT_STATEMENT) {
		throw BinderException("fabricator function \"%s\" must generate exactly one SELECT statement (a PIVOT "
		                      "needs an explicit IN list); generated: %s",
		                      fn_name, sql);
	}
	auto select_stmt = unique_ptr_cast<SQLStatement, SelectStatement>(std::move(parser.statements[0]));
	return make_uniq<SubqueryRef>(std::move(select_stmt));
}

// Build the DuckDB signature of a SQL-generating table function from its ONE declared parameter schema:
//! Resolves an ANY-declared argument slot against the value that actually arrived.
//!
//! ⚠ Two rules, and the second is the one that bites. A SQLNULL DECLARATION is this protocol's "accept any
//! value" marker, so the slot takes the VALUE's own runtime type. But a value that is itself an UNTYPED NULL
//! leaves it SQLNULL — and an untyped NULL cannot cross the Arrow boundary at all: DuckDB exports a
//! null-typed array with `null_count = 0`, which Apache.Arrow refuses ("Length must equal null count"). So
//! an unresolved slot is carried as a typed NULL VARCHAR, which every reader already treats as "absent".
static void FabricatorResolveAnyArg(LogicalType &type, Value &value) {
	if (type.id() == LogicalTypeId::SQLNULL) {
		type = value.type();
	}
	if (type.id() == LogicalTypeId::SQLNULL) {
		type = LogicalType::VARCHAR;
		value = Value(LogicalType::VARCHAR);
	}
}

// Marshals an in-out / collector call's CONSTANT arguments in DECLARED ORDER, so the managed side reads them
// by position exactly as it does for a table function. Walks the declared parameters:
//   TABLE_INPUT — consumes its slot in `input.inputs` and emits NOTHING. DuckDB reserves a positional slot for
//                 the subquery and pushes a placeholder Value into it (bind_table_function.cpp), so the slot
//                 must be consumed or every following positional would read one argument too early.
//   POSITIONAL  — takes the next value from `input.inputs`.
//   NAMED       — takes the supplied value, or a typed NULL when the caller omitted it (the same
//                 "omitted == explicit NULL" equivalence the table path uses).
//
// ⚠ This replaced a loop over `input.named_parameters` ALONE. That was correct only while an in-out's cost args
// were named BY CONVENTION; once the unified protocol let one be declared positional, the signature accepted
// `f((SELECT …), 3)` while the 3 was silently DROPPED before reaching C# — a half-offered capability, which is
// worse than not offering it at all.
static void FabricatorMarshalInOutArgs(const FabricatorTableFunctionInfo &info, TableFunctionBindInput &input,
                                       vector<string> &arg_names, vector<LogicalType> &arg_types,
                                       vector<Value> &arg_values) {
	idx_t positional_index = 0;
	for (idx_t i = 0; i < info.arg_names.size(); i++) {
		auto style = i < info.arg_styles.size() ? info.arg_styles[i] : FabricatorParamStyle::POSITIONAL;
		if (style == FabricatorParamStyle::TABLE_INPUT) {
			positional_index++; // consume the subquery's reserved slot; the table itself is not an arg value
			continue;
		}
		if (style == FabricatorParamStyle::VARARGS) {
			// The tail takes EVERY remaining positional argument (it is the last positional parameter by
			// construction), each named `<tail>_<k>`. ⚠ Without this branch the loop walks the DECLARATION
			// and indexes into the values, so surplus arguments are SILENTLY DROPPED — no crash and no
			// error, just a function that does not receive what the caller wrote. That is the failure this
			// kind's refusal used to prevent, and it is why the tail has to be marshaled rather than left to
			// fall through.
			auto tail_type = FabricatorVarArgsType(info.arg_types[i]);
			for (idx_t k = positional_index; k < input.inputs.size(); k++) {
				auto &v = input.inputs[k];
				arg_names.push_back(info.arg_names[i] + "_" + to_string(k - positional_index));
				arg_types.push_back(tail_type.id() == LogicalTypeId::ANY ? v.type() : tail_type);
				arg_values.push_back(v);
			}
			positional_index = input.inputs.size();
			continue;
		}
		const LogicalType &declared = info.arg_types[i];
		if (style == FabricatorParamStyle::NAMED) {
			Value v(declared);
			for (auto &kv : input.named_parameters) {
				if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
					v = kv.second;
					break;
				}
			}
			arg_names.push_back(info.arg_names[i]);
			// ⚠⚠ THE SQLNULL SENTINEL IS THE *ANY* DECLARATION, and this branch used to push `declared`
			// unconditionally while the POSITIONAL branch below already resolved it. An ANY-declared NAMED
			// parameter was therefore unusable in BOTH directions: supplied, the marshal tried to write the
			// caller's value into a SQLNULL vector ("Failed to cast value ... -> NULL"); omitted, it carried
			// an untyped NULL that Apache.Arrow refuses on import. Latent rather than shipped-broken — no
			// in-tree in-out or collector declared one until fluid_query_batch's `params` bag.
			auto named_type = declared;
			FabricatorResolveAnyArg(named_type, v);
			arg_types.push_back(named_type);
			arg_values.push_back(std::move(v));
			continue;
		}
		Value v = positional_index < input.inputs.size() ? input.inputs[positional_index] : Value(declared);
		positional_index++;
		arg_names.push_back(info.arg_names[i]);
		auto positional_type = declared;
		FabricatorResolveAnyArg(positional_type, v);
		arg_types.push_back(positional_type);
		arg_values.push_back(std::move(v));
	}
	// A provider that declared nothing (every discovered `_each`) still gets the historical behavior: any
	// named parameter DuckDB accepted is passed through, so an undeclared-but-bound arg is not lost.
	if (info.arg_names.empty()) {
		for (auto &kv : input.named_parameters) {
			arg_names.push_back(kv.first);
			arg_types.push_back(kv.second.type());
			arg_values.push_back(kv.second);
		}
	}
}

// Builds an in-out / collector signature FROM the declared parameter STYLES: the table input becomes the
// {LogicalType::TABLE} argument at its declared position, named parameters become DuckDB named parameters,
// and any remaining positional parameter keeps its slot. DuckDB gives the subquery its own argument slot
// (bind_table_function.cpp pushes a placeholder value for it), so a table input BETWEEN positionals keeps the
// following positions at their natural index.
//
// ⚠ This exists because the alternative — "every declared parameter is a named cost arg" — silently leaked the
// table-input field into the signature as `input := STRUCT(...)` the moment the input table became a parameter
// like any other. Nothing failed: an extra OPTIONAL named parameter breaks no existing call, so both tiers
// stayed green while the advertised signature was wrong.
// `named_any_for_null` maps a SQLNULL-declared named parameter to ANY — what the COLLECTOR path has always
// done and the in-out path has not. Kept as a flag rather than unified, because changing it would alter how a
// NullType cost arg (the daxeach params bag) binds, which is not this refactor's business.
static void FabricatorBuildInOutSignature(const vector<string> &names, const vector<LogicalType> &types,
                                          const vector<FabricatorParamStyle> &styles, TableFunction &tf,
                                          bool named_any_for_null = false) {
	for (idx_t i = 0; i < names.size(); i++) {
		auto style = i < styles.size() ? styles[i] : FabricatorParamStyle::POSITIONAL;
		switch (style) {
		case FabricatorParamStyle::VARARGS:
			// A VARIADIC TAIL of BIND-TIME COST ARGUMENTS — the args-batch mechanism, exactly as for a
			// scalar / table / sqlgen function, NOT the lateral one. An in-out's per-row input is its
			// {TABLE} argument alone; its positional and named parameters are constants resolved at bind
			// (FabricatorMarshalInOutArgs marshals them into the 1-row batch the author reads in Bind). So
			// the tail widens the ARGS BATCH and the input stream is untouched — the two cannot mix, since
			// the subquery slot binds to a BoundStatement while the tail arguments are constants in
			// `input.inputs`.
			// ⚠ DuckDB resolves this: GetTableFunctionBindType keys `has_table_parameter` on
			// `function.arguments` containing TABLE (which the {TABLE} slot supplies regardless of varargs),
			// the subquery contributes LogicalTypeId::TABLE + an empty Value to the match, and every further
			// argument is costed against `varargs` by BindVarArgsFunctionCost.
			// ⚠ SQLNULL => ANY UNCONDITIONALLY, and NOT via `named_any_for_null`. That flag exists for
			// NAMED parameters alone (its own comment says so); reusing it here made an ANY tail register as
			// varargs = SQLNULL on the CATALOG path, which only a NULL literal casts to — so every real
			// argument failed to bind while the GLOBAL path, which passes the flag true, worked. Found by
			// the catalog-bound gate; the type sentinel is a property of the DECLARATION, not of the caller.
			tf.varargs = FabricatorVarArgsType(types[i]);
			break;
		case FabricatorParamStyle::TABLE_INPUT:
			// The DECLARED type (a struct of the expected input columns) is ours alone: DuckDB only ever
			// accepts LogicalType::TABLE here, so any column-level check would be a bind-time one of our own.
			tf.arguments.push_back(LogicalType::TABLE);
			break;
		case FabricatorParamStyle::NAMED:
			tf.named_parameters[names[i]] =
			    (named_any_for_null && types[i].id() == LogicalTypeId::SQLNULL) ? LogicalType::ANY : types[i];
			break;
		default:
			tf.arguments.push_back(types[i]);
			break;
		}
	}
}

// positional fields first, then the NAMED ones (tagged fabricator.param_style="named", split by `styles`). A
// SQLNULL-declared parameter is the "accept any value" marker (registered as ANY so DuckDB passes the literal
// UNCAST and its runtime type survives). Shared by the global (load-time) and catalog (attach-time) paths.
void FabricatorBuildSqlGenSignature(const vector<string> &all_names, const vector<LogicalType> &all_types,
                                    const vector<FabricatorParamStyle> &styles, TableFunction &tf,
                                    vector<string> &arg_names,
                                    vector<LogicalType> &arg_types, idx_t *out_varargs_index) {
	vector<LogicalType> positional;
	if (out_varargs_index) {
		*out_varargs_index = DConstants::INVALID_INDEX;
	}
	for (idx_t k = 0; k < all_names.size(); k++) {
		auto style = k < styles.size() ? styles[k] : FabricatorParamStyle::POSITIONAL;
		auto type = all_types[k].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : all_types[k];
		if (style == FabricatorParamStyle::NAMED) {
			tf.named_parameters[all_names[k]] = type;
			continue;
		}
		if (style == FabricatorParamStyle::VARARGS) {
			// NOT an argument slot — it names the TYPE of every argument past the declared ones. It stays in
			// the DECLARATION (arg_names/arg_types) because FabricatorSqlGenBindReplace expands it per call;
			// only `tf.arguments` must omit it, or DuckDB would demand a value for the tail itself.
			tf.varargs = FabricatorVarArgsType(all_types[k]);
			if (out_varargs_index) {
				*out_varargs_index = arg_names.size();
			}
		} else {
			positional.push_back(type);
		}
		arg_names.push_back(all_names[k]);
		arg_types.push_back(type);
	}
	tf.arguments = positional;
}

unique_ptr<TableRef> FabricatorSqlGenBindReplace(ClientContext &context, TableFunctionBindInput &input) {
	auto &info = input.info->Cast<FabricatorTableFunctionInfo>();
	// The generator may use its provider connection at bind time (a catalog-bound one listing matching
	// tables) and/or read through the host FS; establish the transaction + opener ambients. Harmless for a
	// global function (handle 0 — set_active_txn ignores the handle, the opener is just made available).
	FabricatorSetActiveTxn(info.handle, context);

	// The 1-row constant-arg batch: POSITIONAL args in declared order, then the SUPPLIED named parameters by
	// name (the binder has already validated the names and cast each value to its declared type — except an
	// ANY-declared one, whose runtime type is preserved on purpose).
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values = input.inputs;
	// A VARIADIC generator's call is wider than its declaration; expand before the named parameters are
	// appended, so the tail keeps its positional block. Non-variadic => the declaration, unchanged.
	FabricatorExpandVarArgs(info.varargs_index, info.arg_names, info.arg_types, arg_values, arg_names,
	                        arg_types);
	for (auto &kv : input.named_parameters) {
		arg_names.push_back(kv.first);
		arg_types.push_back(kv.second.type());
		arg_values.push_back(kv.second);
	}

	auto properties = fabricator::BoundaryClientProperties(context);
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);

	// The catalog ALIAS (what the user wrote in ATTACH) is only known here — a catalog-bound generator needs
	// it to emit qualified references back into its own catalog. Empty for a global function.
	string sql;
	if (arg_types.empty()) {
		// A generator called with NO arguments AT ALL — which a VARIADIC one legitimately can be, since its
		// minimum arity is the declared prefix and that may be empty. Pass NO stream rather than an empty
		// one: a zero-FIELD Arrow schema cannot cross in either direction (Apache.Arrow raises
		// ArgumentNullException on 'fields'), so merely CONSTRUCTING the producer fails the bind with an
		// error that names nothing recognizable. The managed side already reads a null args stream as an
		// empty batch (SqlGen.Generate), so the generator's own arity rule is what refuses the call.
		// ⚠ Same rule and same reason as the zero-argument branch in FabricatorTableFunctionBind — it was
		// simply unreachable here until a generator could have minimum arity 0.
		sql = fabricator::GenerateTableSql(info.handle, info.schema, info.func, info.catalog_name, nullptr);
	} else {
		fabricator::ArrowProducer producer(arg_types, arg_names, properties);
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		ArrowAppender appender(arg_types, 1, properties, extension_types);
		appender.Append(chunk, 0, 1, 1);
		producer.AddBatch(appender.Finalize());
		producer.Finish();
		sql = fabricator::GenerateTableSql(info.handle, info.schema, info.func, info.catalog_name,
		                                   producer.Stream());
	}
	return FabricatorParseGeneratedSelect(sql, context.GetParserOptions(), info.func);
}


// =============================================================================
// Phase 6 streaming table-in-out EXCHANGE operator (read-only). Replaces the push/materialize model
// above for custom C# in-out (and, in 6.2, discovered TVFs): two pull-based Arrow streams coordinated by
// a C++ "gate" mutex, no per-chunk materialization. The host exports the INPUT stream (its get_next hands
// the current gate-holder's one input chunk to C#); C# exports the OUTPUT stream, which the host pulls —
// a non-empty batch = HAVE_MORE_OUTPUT, a length-0 batch = NEED_MORE_INPUT (the per-input sentinel C#
// yields), a released array = FINISHED. One binding (bound once) runs one exchange per execution. EOF is
// the injected OperatorFinalize (sets input_eof + drains), not a producer counter. See abi.h / the plan.
// =============================================================================

struct FabricatorExchangeGlobalState;

// Refcounted, on the bind data (survives prepared re-executions). Owns the bound binding handle (freed
// once via inout_bind_close) and points at the CURRENT execution's global state for the EOF signal.
struct ExchangeHolder {
	mutex lock;
	FabricatorHandle binding = nullptr;
	FabricatorExchangeGlobalState *active = nullptr; // set at init_global; cleared at its dtor
	~ExchangeHolder();
	void Finish(ClientContext &context); // forwards to active->FinishEof (single all-input-done signal)
};

struct FabricatorExchangeGlobalState : public GlobalTableFunctionState {
	mutex gate;                  // serializes one input chunk's full cycle across parallel branch pipelines
	ArrowArray slot {};          // the single input handoff (set by the gate-holder, moved out by input get_next)
	bool slot_full = false;
	bool input_eof = false;
	bool finished = false;
	duckdb::unique_ptr<fabricator::ArrowStreamReader> reader; // the C# output stream (sentinel-aware pull)
	// Captured for the host-side input stream's get_schema/get_next callbacks.
	vector<LogicalType> input_types;
	vector<string> input_names;
	ClientProperties props;
	string input_error;
	ExchangeHolder *holder = nullptr;

	idx_t MaxThreads() const override {
		return 1; // intra-pipeline cap; parallel UNION branches are separate pipelines (serialized by the gate)
	}

	// Single "all input consumed" action (idempotent): mark EOF and drain the output to its terminal null so
	// the managed DoExchange exits its loop and disposes (commit / connection close). Read-only => no tail rows.
	void FinishEof(ClientContext &context) {
		lock_guard<mutex> guard(gate);
		if (finished) {
			return;
		}
		finished = true;
		input_eof = true;
		if (!reader) {
			return;
		}
		DataChunk scratch;
		scratch.Initialize(Allocator::Get(context), reader->Types());
		while (true) {
			auto pr = reader->Pull();
			if (pr == fabricator::ArrowStreamReader::PullResult::END) {
				break;
			}
			if (pr == fabricator::ArrowStreamReader::PullResult::DATA) {
				while (reader->HasPending()) {
					reader->Drain(scratch);
				}
			}
		}
	}

	~FabricatorExchangeGlobalState() override {
		// Release the output stream first (the managed dispose tears down the exchange + the imported input
		// stream, so input get_next won't fire again), THEN free the slot.
		reader.reset();
		if (slot_full && slot.release) {
			slot.release(&slot);
		}
		if (holder) {
			lock_guard<mutex> guard(holder->lock);
			if (holder->active == this) {
				holder->active = nullptr;
			}
		}
	}
};

ExchangeHolder::~ExchangeHolder() {
	if (binding) {
		fabricator::InOutBindClose(binding); // best-effort; swallows errors
		binding = nullptr;
	}
}

void ExchangeHolder::Finish(ClientContext &context) {
	lock_guard<mutex> guard(lock);
	if (active) {
		active->FinishEof(context);
	}
}

// Bind data (reused across prepared re-executions; the holder is shared, the binding bound once).
struct FabricatorExchangeBindData : public TableFunctionData {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> input_types;
	vector<string> input_names;
	shared_ptr<ExchangeHolder> holder;
};

struct FabricatorExchangeLocalState : public LocalTableFunctionState {
	bool owns_gate = false; // this thread holds the gate for the current input chunk's cycle
};

// Host-side INPUT stream callbacks. private_data == the global state. Only the current gate-holder sets the
// slot, and C# pulls exactly once per tenure (after the sentinel), so a "slot empty, not EOF" pull means the
// transform read ahead of the gate (a missing sentinel) — surfaced as a stream error, never silent.
int ExchangeInputGetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *g = reinterpret_cast<FabricatorExchangeGlobalState *>(stream->private_data);
	ArrowConverter::ToArrowSchema(out, g->input_types, g->input_names, g->props);
	return 0;
}
int ExchangeInputGetNext(ArrowArrayStream *stream, ArrowArray *out) {
	auto *g = reinterpret_cast<FabricatorExchangeGlobalState *>(stream->private_data);
	if (g->slot_full) {
		*out = g->slot; // move the gate-holder's chunk to C#
		g->slot = ArrowArray {};
		g->slot_full = false;
		return 0;
	}
	if (g->input_eof) {
		out->release = nullptr; // EOF
		return 0;
	}
	g->input_error = "Fabricator: in-out transform requested input before yielding a sentinel (single-slot gate)";
	return 1;
}
const char *ExchangeInputGetLastError(ArrowArrayStream *stream) {
	auto *g = reinterpret_cast<FabricatorExchangeGlobalState *>(stream->private_data);
	return g->input_error.empty() ? nullptr : g->input_error.c_str();
}
void ExchangeInputRelease(ArrowArrayStream *stream) {
	stream->release = nullptr; // the global state (private_data) is not owned by the stream
}

// Bind: resolve the binding + its full output schema via inout_bind (no cost args for custom in-out yet).
unique_ptr<FunctionData> FabricatorExchangeBind(ClientContext &context, TableFunctionBindInput &input,
                                              vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<FabricatorTableFunctionInfo>();
	// ⚠⚠ THE AMBIENTS, and the BIND needs them as much as the scan does: this crossing runs the author's
	// Bind(), which may reach the host — open a pinned connection, read a file, resolve a setting. Without
	// them the managed side reads whatever the LAST crossing left, and AmbientOpener is a raw
	// ClientContext* whose connection may be gone. MEASURED as `host_connection_open failed: vector too
	// long` — CaptureSession dereferencing a dangling pointer, non-zero so the null guard waved it through,
	// and reproducible only with an earlier statement in the same session to leave one behind. Latent until
	// a binding did host work in Bind (fluid_query_batch's schema probe); FabricatorSetActiveTxn's own
	// comment already named "a global collector/in-out" as the case it is for.
	FabricatorSetActiveTxn(info.handle, context);
	auto bind_data = make_uniq<FabricatorExchangeBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->holder = make_shared_ptr<ExchangeHolder>();
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		bind_data->input_types.push_back(input.input_table_types[i]);
		bind_data->input_names.push_back(input.input_table_names[i]);
	}

	auto props = fabricator::BoundaryClientProperties(context);
	ArrowSchema input_schema;
	std::memset(&input_schema, 0, sizeof(input_schema));
	ArrowConverter::ToArrowSchema(&input_schema, bind_data->input_types, bind_data->input_names, props);

	// Marshal the constant args into a 1-row Arrow stream for inout_bind — e.g.
	// daxevaltable(<input>, expression := 'EVALUATE …'), or a POSITIONAL cost arg. A function with no declared
	// args supplies none => args stays null (unchanged behavior).
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	FabricatorMarshalInOutArgs(info, input, arg_names, arg_types, arg_values);
	fabricator::ArrowProducer arg_producer(arg_types, arg_names, props);
	ArrowArrayStream *args_ptr = nullptr;
	if (!arg_values.empty()) {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
		ArrowAppender appender(arg_types, 1, props, extension_types);
		appender.Append(chunk, 0, 1, 1);
		arg_producer.AddBatch(appender.Finalize());
		arg_producer.Finish();
		args_ptr = arg_producer.Stream();
	}

	ArrowArrayStream out_schema;
	std::memset(&out_schema, 0, sizeof(out_schema));
	bind_data->holder->binding =
	    fabricator::InOutBind(info.handle, info.schema, info.func, args_ptr, input_schema, out_schema);

	ArrowSchemaWrapper schema_root;
	if (out_schema.get_schema(&out_schema, &schema_root.arrow_schema) != 0) {
		// Copy the error BEFORE release: get_last_error's pointer lives in the stream's
		// private data, which release frees.
		string msg;
		if (out_schema.get_last_error) {
			if (const char *err = out_schema.get_last_error(&out_schema)) {
				msg = err;
			}
		}
		if (out_schema.release) {
			out_schema.release(&out_schema);
		}
		throw IOException(string("fabricator: failed to read in-out exchange output schema") +
		                  (msg.empty() ? string() : ": " + msg));
	}
	if (out_schema.release) {
		out_schema.release(&out_schema);
	}
	ArrowTableSchema arrow_table;
	ArrowTableFunction::PopulateArrowTableSchema(context, arrow_table, schema_root.arrow_schema);
	for (int64_t i = 0; i < schema_root.arrow_schema.n_children; i++) {
		auto &child = *schema_root.arrow_schema.children[i];
		names.push_back(child.name ? string(child.name) : "column" + to_string(i));
		return_types.push_back(arrow_table.GetColumns().at((idx_t)i)->GetDuckType());
	}
	return std::move(bind_data);
}

unique_ptr<GlobalTableFunctionState> FabricatorExchangeInitGlobal(ClientContext &context,
                                                               TableFunctionInitInput &input) {
	auto &bind = input.bind_data->Cast<FabricatorExchangeBindData>();
	auto gstate = make_uniq<FabricatorExchangeGlobalState>();
	gstate->holder = bind.holder.get();
	gstate->input_types = bind.input_types;
	gstate->input_names = bind.input_names;
	gstate->props = fabricator::BoundaryClientProperties(context);

	ArrowArrayStream input_stream;
	std::memset(&input_stream, 0, sizeof(input_stream));
	input_stream.private_data = gstate.get();
	input_stream.get_schema = ExchangeInputGetSchema;
	input_stream.get_next = ExchangeInputGetNext;
	input_stream.get_last_error = ExchangeInputGetLastError;
	input_stream.release = ExchangeInputRelease;

	ArrowArrayStream output_stream;
	std::memset(&output_stream, 0, sizeof(output_stream));
	// A proc `_each` runs its per-row EXEC on DuckDB's pinned write connection (BeginWrite), so the binding
	// must see this transaction's id when it opens that connection (read-your-writes + commit/rollback with
	// DuckDB's transaction). The id rides the per-thread ambient; also re-set in the Execute function (the
	// connection is opened lazily on the first output pull there). See docs/transaction-concurrency.md.
	FabricatorSetActiveTxn(nullptr, context);
	fabricator::InOutExchangeOpen(bind.holder->binding, input_stream, output_stream);
	gstate->reader = make_uniq<fabricator::ArrowStreamReader>(context, output_stream);

	lock_guard<mutex> guard(bind.holder->lock);
	bind.holder->active = gstate.get();
	return std::move(gstate);
}

unique_ptr<LocalTableFunctionState> FabricatorExchangeInitLocal(ExecutionContext &, TableFunctionInitInput &,
                                                              GlobalTableFunctionState *) {
	return make_uniq<FabricatorExchangeLocalState>();
}

// The gate-based operator. The gate is held across the whole chunk cycle (multiple Execute calls during
// HAVE_MORE_OUTPUT) — ownership in the per-thread local state — and released on the sentinel/EOF or on a
// thrown managed error (so the gate never leaks).
OperatorResultType FabricatorExchangeFunction(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                            DataChunk &output) {
	auto &bind = data.bind_data->Cast<FabricatorExchangeBindData>();
	auto &g = data.global_state->Cast<FabricatorExchangeGlobalState>();
	auto &l = data.local_state->Cast<FabricatorExchangeLocalState>();
	// A proc `_each` opens its pinned write connection (BeginWrite) lazily on the first output pull below,
	// which runs on THIS thread — so set the active transaction id here so it joins DuckDB's transaction
	// (read-your-writes + commit/rollback with DuckDB). Harmless for a TVF `_each` (its own read connection).
	FabricatorSetActiveTxn(nullptr, context.client);
	try {
		if (!l.owns_gate) {
			if (input.size() == 0) {
				output.SetCardinality(0);
				return OperatorResultType::NEED_MORE_INPUT;
			}
			g.gate.lock();
			l.owns_gate = true;
			// Export this input chunk into the single slot for the C# input pull.
			auto props = fabricator::BoundaryClientProperties(context.client);
			auto ext = ArrowTypeExtensionData::GetExtensionTypes(context.client, bind.input_types);
			ArrowAppender appender(bind.input_types, input.size(), props, ext);
			appender.Append(input, 0, input.size(), input.size());
			g.slot = appender.Finalize();
			g.slot_full = true;
		}
		// Drain a pending output array (one C# batch may exceed STANDARD_VECTOR_SIZE).
		if (g.reader->HasPending()) {
			g.reader->Drain(output);
			return OperatorResultType::HAVE_MORE_OUTPUT;
		}
		auto pr = g.reader->Pull();
		if (pr == fabricator::ArrowStreamReader::PullResult::SENTINEL) {
			l.owns_gate = false;
			g.gate.unlock(); // hand the gate to the next branch
			output.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		if (pr == fabricator::ArrowStreamReader::PullResult::END) {
			l.owns_gate = false;
			g.gate.unlock();
			output.SetCardinality(0);
			return OperatorResultType::FINISHED;
		}
		g.reader->Drain(output);
		return OperatorResultType::HAVE_MORE_OUTPUT;
	} catch (...) {
		if (l.owns_gate) {
			l.owns_gate = false;
			g.gate.unlock(); // never leak the gate on a managed error
		}
		throw;
	}
}

// -----------------------------------------------------------------------------

// The exchange analog: forwards rows 1:1 and drives the exchange EOF (set input_eof + drain the output to
// terminal-null so the managed DoExchange finishes + disposes) once, sink-level, after all branches.
class FabricatorExchangeFinalizePhysical : public PhysicalOperator {
public:
	FabricatorExchangeFinalizePhysical(PhysicalPlan &physical_plan, vector<LogicalType> types,
	                                 idx_t estimated_cardinality, shared_ptr<ExchangeHolder> holder)
	    : PhysicalOperator(physical_plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
	      holder(std::move(holder)) {
	}

	shared_ptr<ExchangeHolder> holder;

	string GetName() const override {
		return "FABRICATOR_INOUT_EXCHANGE_FINALIZE";
	}

	OperatorResultType Execute(ExecutionContext &, DataChunk &input, DataChunk &chunk, GlobalOperatorState &,
	                           OperatorState &) const override {
		chunk.Reference(input);
		return OperatorResultType::NEED_MORE_INPUT;
	}

	bool ParallelOperator() const override {
		return true;
	}

	bool RequiresOperatorFinalize() const override {
		return true;
	}

	OperatorFinalResultType OperatorFinalize(Pipeline &, Event &, ClientContext &context,
	                                         OperatorFinalizeInput &) const override {
		holder->Finish(context); // single all-input-done signal (idempotent): EOF + drain
		return OperatorFinalResultType::FINISHED;
	}
};

struct FabricatorExchangeFinalizeOperator : public LogicalExtensionOperator {
	explicit FabricatorExchangeFinalizeOperator(unique_ptr<LogicalOperator> child, shared_ptr<ExchangeHolder> holder)
	    : holder(std::move(holder)) {
		children.push_back(std::move(child));
	}

	shared_ptr<ExchangeHolder> holder;

	PhysicalOperator &CreatePlan(ClientContext &, PhysicalPlanGenerator &planner) override {
		auto &child_plan = planner.CreatePlan(*children[0]);
		auto &op = planner.Make<FabricatorExchangeFinalizePhysical>(children[0]->types,
		                                                          children[0]->estimated_cardinality, holder);
		op.children.push_back(child_plan);
		return op;
	}

	vector<ColumnBinding> GetColumnBindings() override {
		return children[0]->GetColumnBindings();
	}

	void ResolveTypes() override {
		types = children[0]->types;
	}

	string GetExtensionName() const override {
		return "fabricator_inout_exchange_finalize";
	}
};

// =============================================================================
// COLLECTOR table-in-out (pipeline breaker). The second in-out execution shape (alongside the streaming
// exchange above): a Sink+Source operator that buffers ALL input, then (once, after all branches) opens the
// exchange and STREAMS the output (the Source pulls the C# output one vector-slice at a time — no output
// materialization). Right for whole-table transforms (output depends on all input). Reuses the inout_bind /
// inout_exchange_open ABI verbatim — only the OPERATOR differs (no gate, no per-chunk sentinel; the C# input
// stream is a plain buffered producer, so the binding reads all input before yielding). Input is fully
// buffered (inherent); output is streamed. See docs/inout-collector-mode.md.
// =============================================================================

// Refcounted, on the bind data. Survives prepared re-executions AND the sink->source boundary, so it owns
// everything the Source needs: the bound binding handle (freed via inout_bind_close), the per-execution input
// buffer (ALL input, as Arrow — the in-out Execute appends to it during the sink phase), and the C# output
// stream reader (opened at Finalize, pulled LAZILY by the Source). The input producer lives HERE (not in the
// in-out global state) because C# reads it only on the first Source pull — by which point the in-out's global
// state may already be gone; the holder outlives both phases.
struct CollectorHolder {
	mutex lock;
	FabricatorHandle binding = nullptr;
	vector<LogicalType> input_types; // input table columns (set at bind; for the producer + Execute appender)
	vector<string> input_names;
	ClientProperties props;          // boundary props for the input producer (set at init_global)
	// per-execution (reset at init_global):
	duckdb::unique_ptr<fabricator::ArrowProducer> input_producer; // ALL input buffered here; drained by C# lazily
	bool opened = false;                                        // exchange opened (Finalize ran)
	duckdb::unique_ptr<fabricator::ArrowStreamReader> reader;     // C# output stream; Source pulls vector-slices
	bool source_done = false;
	~CollectorHolder();
	void OpenExchange(ClientContext &context); // single all-input-done: Finish input + open exchange (keep reader)
};

// Trivial per-execution state. Its init (FabricatorCollectorInitGlobal) resets the holder's per-execution buffer.
struct FabricatorCollectorGlobalState : public GlobalTableFunctionState {
	idx_t MaxThreads() const override {
		return 1;
	}
};

// Bind data (reused across prepared re-executions; the holder is shared, the binding bound once).
struct FabricatorCollectorBindData : public TableFunctionData {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	shared_ptr<CollectorHolder> holder;
};

struct FabricatorCollectorLocalState : public LocalTableFunctionState {};

CollectorHolder::~CollectorHolder() {
	// Release the output reader FIRST (its C# dispose releases the imported input stream, which points at the
	// producer), THEN the producer, THEN the binding.
	reader.reset();
	input_producer.reset();
	if (binding) {
		fabricator::InOutBindClose(binding); // best-effort; swallows errors
		binding = nullptr;
	}
}

void CollectorHolder::OpenExchange(ClientContext &context) {
	lock_guard<mutex> guard(lock);
	if (opened) {
		return;
	}
	opened = true;
	// Read-only / custom collectors don't touch DuckDB's write transaction; set a null active txn for
	// consistency (a future SQL-backed collector opening a connection in Collect would pick it up).
	FabricatorSetActiveTxn(nullptr, context);
	if (!input_producer) {
		return; // input pipeline never ran (empty plan) — the Source sees no reader => FINISHED (no rows)
	}
	input_producer->Finish(); // no more input batches
	ArrowArrayStream out_stream;
	std::memset(&out_stream, 0, sizeof(out_stream));
	// Open the exchange: C# Collect is wired up but LAZY — it doesn't read the (now fully buffered) input until
	// the Source pulls the first output batch. The Source then drains `reader` one vector-slice at a time, so
	// the output is STREAMED (never materialized). The reader holds `context` by reference (the query context,
	// valid through the source phase).
	fabricator::InOutExchangeOpen(binding, *input_producer->Stream(), out_stream);
	reader = make_uniq<fabricator::ArrowStreamReader>(context, out_stream);
}

// Bind: resolve the binding + its full output schema via inout_bind (identical to the streaming exchange bind,
// but stores a CollectorHolder + the output types for the Source). Cost args (named parameters) marshaled the
// same way so a collector can take constant args (e.g. a future daxevaltable).
unique_ptr<FunctionData> FabricatorCollectorBind(ClientContext &context, TableFunctionBindInput &input,
                                               vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<FabricatorTableFunctionInfo>();
	// ⚠⚠ THE AMBIENTS, and the BIND needs them as much as the scan does: this crossing runs the author's
	// Bind(), which may reach the host — open a pinned connection, read a file, resolve a setting. Without
	// them the managed side reads whatever the LAST crossing left, and AmbientOpener is a raw
	// ClientContext* whose connection may be gone. MEASURED as `host_connection_open failed: vector too
	// long` — CaptureSession dereferencing a dangling pointer, non-zero so the null guard waved it through,
	// and reproducible only with an earlier statement in the same session to leave one behind. Latent until
	// a binding did host work in Bind (fluid_query_batch's schema probe); FabricatorSetActiveTxn's own
	// comment already named "a global collector/in-out" as the case it is for.
	FabricatorSetActiveTxn(info.handle, context);
	auto bind_data = make_uniq<FabricatorCollectorBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->holder = make_shared_ptr<CollectorHolder>();
	auto &holder = *bind_data->holder;
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		holder.input_types.push_back(input.input_table_types[i]);
		holder.input_names.push_back(input.input_table_names[i]);
	}

	auto props = fabricator::BoundaryClientProperties(context);
	ArrowSchema input_schema;
	std::memset(&input_schema, 0, sizeof(input_schema));
	ArrowConverter::ToArrowSchema(&input_schema, holder.input_types, holder.input_names, props);

	// Marshal the constant args (positional and/or named) into a 1-row Arrow stream (else null). Same helper as
	// the streaming exchange bind — see FabricatorMarshalInOutArgs.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	FabricatorMarshalInOutArgs(info, input, arg_names, arg_types, arg_values);
	fabricator::ArrowProducer arg_producer(arg_types, arg_names, props);
	ArrowArrayStream *args_ptr = nullptr;
	if (!arg_values.empty()) {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
		ArrowAppender appender(arg_types, 1, props, extension_types);
		appender.Append(chunk, 0, 1, 1);
		arg_producer.AddBatch(appender.Finalize());
		arg_producer.Finish();
		args_ptr = arg_producer.Stream();
	}

	ArrowArrayStream out_schema;
	std::memset(&out_schema, 0, sizeof(out_schema));
	bind_data->holder->binding =
	    fabricator::InOutBind(info.handle, info.schema, info.func, args_ptr, input_schema, out_schema);

	ArrowSchemaWrapper schema_root;
	if (out_schema.get_schema(&out_schema, &schema_root.arrow_schema) != 0) {
		// Copy the error BEFORE release: get_last_error's pointer lives in the stream's
		// private data, which release frees.
		string msg;
		if (out_schema.get_last_error) {
			if (const char *err = out_schema.get_last_error(&out_schema)) {
				msg = err;
			}
		}
		if (out_schema.release) {
			out_schema.release(&out_schema);
		}
		throw IOException(string("fabricator: failed to read collector output schema") +
		                  (msg.empty() ? string() : ": " + msg));
	}
	if (out_schema.release) {
		out_schema.release(&out_schema);
	}
	ArrowTableSchema arrow_table;
	ArrowTableFunction::PopulateArrowTableSchema(context, arrow_table, schema_root.arrow_schema);
	for (int64_t i = 0; i < schema_root.arrow_schema.n_children; i++) {
		auto &child = *schema_root.arrow_schema.children[i];
		names.push_back(child.name ? string(child.name) : "column" + to_string(i));
		return_types.push_back(arrow_table.GetColumns().at((idx_t)i)->GetDuckType());
	}
	return std::move(bind_data);
}

unique_ptr<GlobalTableFunctionState> FabricatorCollectorInitGlobal(ClientContext &context,
                                                                TableFunctionInitInput &input) {
	auto &bind = input.bind_data->Cast<FabricatorCollectorBindData>();
	auto &holder = *bind.holder;
	// Reset per-execution holder state (a prepared statement may re-execute on the shared holder). Release the
	// prior reader FIRST (its C# dispose releases the prior producer's exported stream) before replacing the
	// producer with a fresh one for this execution.
	lock_guard<mutex> guard(holder.lock);
	holder.reader.reset();
	holder.opened = false;
	holder.source_done = false;
	holder.props = fabricator::BoundaryClientProperties(context);
	holder.input_producer = make_uniq<fabricator::ArrowProducer>(holder.input_types, holder.input_names, holder.props);
	return make_uniq<FabricatorCollectorGlobalState>();
}

unique_ptr<LocalTableFunctionState> FabricatorCollectorInitLocal(ExecutionContext &, TableFunctionInitInput &,
                                                               GlobalTableFunctionState *) {
	return make_uniq<FabricatorCollectorLocalState>();
}

// The in-out operator function: buffer each input chunk (as Arrow), emit NO rows. The actual output is emitted
// by the injected Sink+Source wrapper after ALL input is collected.
OperatorResultType FabricatorCollectorFunction(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                             DataChunk &output) {
	auto &holder = *data.bind_data->Cast<FabricatorCollectorBindData>().holder;
	if (input.size() > 0) {
		// holder.input_producer + holder.props are set at init_global (before any Execute) and are stable for
		// the execution; AddBatch is internally mutex-guarded, so parallel-branch appends are safe lock-free.
		auto ext = ArrowTypeExtensionData::GetExtensionTypes(context.client, holder.input_types);
		ArrowAppender appender(holder.input_types, input.size(), holder.props, ext);
		appender.Append(input, 0, input.size(), input.size());
		holder.input_producer->AddBatch(appender.Finalize());
	}
	output.SetCardinality(0);
	return OperatorResultType::NEED_MORE_INPUT;
}

// Source state — single-threaded (base MaxThreads()==1); the lock guards the lazy reader pull.
class FabricatorCollectorSourceState : public GlobalSourceState {
public:
	mutex lock;
};

// The pipeline-breaker operator: Sink consumes the (empty) in-out output (just to get the single
// all-branches-done Finalize); Finalize opens the exchange; the Source STREAMS the C# output (pulls the reader
// one vector-slice at a time — no materialization).
class FabricatorCollectorPhysical : public PhysicalOperator {
public:
	FabricatorCollectorPhysical(PhysicalPlan &physical_plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                          shared_ptr<CollectorHolder> holder)
	    : PhysicalOperator(physical_plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
	      holder(std::move(holder)) {
	}

	shared_ptr<CollectorHolder> holder;

	string GetName() const override {
		return "FABRICATOR_COLLECTOR";
	}

	// ---- Sink (collect all input) ----
	bool IsSink() const override {
		return true;
	}
	SinkResultType Sink(ExecutionContext &, DataChunk &, OperatorSinkInput &) const override {
		// The child in-out emits 0 rows; the actual input buffering happens in its Execute. Nothing to do here
		// except participate in the pipeline so Finalize fires once after all branches.
		return SinkResultType::NEED_MORE_INPUT;
	}
	SinkFinalizeType Finalize(Pipeline &, Event &, ClientContext &context, OperatorSinkFinalizeInput &) const override {
		holder->OpenExchange(context); // single, all-branches-done: open the exchange (the Source streams it)
		return SinkFinalizeType::READY;
	}

	// ---- Source (STREAM the C# output — pull the reader one vector-slice at a time) ----
	bool IsSource() const override {
		return true;
	}
	unique_ptr<GlobalSourceState> GetGlobalSourceState(ClientContext &) const override {
		return make_uniq<FabricatorCollectorSourceState>();
	}
	SourceResultType GetDataInternal(ExecutionContext &context, DataChunk &chunk, OperatorSourceInput &input) const override {
		auto &gstate = input.global_state.Cast<FabricatorCollectorSourceState>();
		lock_guard<mutex> guard(gstate.lock); // single-stream reader; MaxThreads()==1 but guard anyway
		if (!holder->reader || holder->source_done) {
			return SourceResultType::FINISHED;
		}
		// C# Collect runs lazily on THIS pull (sync-over-async on this thread), so (re)set the active txn +
		// host-FS opener here — not just in Finalize/OpenExchange, which may run on a different thread (the
		// per-thread ambient would otherwise be unset here → a host-FS collector like fabricator_delta_write
		// would see a null opener).
		FabricatorSetActiveTxn(nullptr, context.client);
		// Drain a pending array first (one C# output batch may exceed STANDARD_VECTOR_SIZE).
		if (holder->reader->HasPending()) {
			holder->reader->Drain(chunk);
			return SourceResultType::HAVE_MORE_OUTPUT;
		}
		while (true) {
			auto pr = holder->reader->Pull();
			if (pr == fabricator::ArrowStreamReader::PullResult::END) {
				holder->source_done = true;
				return SourceResultType::FINISHED;
			}
			if (pr == fabricator::ArrowStreamReader::PullResult::SENTINEL) {
				continue; // a collector yields no sentinels, but tolerate (skip empty) for robustness
			}
			holder->reader->Drain(chunk);
			return SourceResultType::HAVE_MORE_OUTPUT;
		}
	}
};

struct FabricatorCollectorFinalizeOperator : public LogicalExtensionOperator {
	explicit FabricatorCollectorFinalizeOperator(unique_ptr<LogicalOperator> child, shared_ptr<CollectorHolder> holder)
	    : holder(std::move(holder)) {
		children.push_back(std::move(child));
	}

	shared_ptr<CollectorHolder> holder;

	PhysicalOperator &CreatePlan(ClientContext &, PhysicalPlanGenerator &planner) override {
		auto &child_plan = planner.CreatePlan(*children[0]);
		auto &op = planner.Make<FabricatorCollectorPhysical>(children[0]->types, children[0]->estimated_cardinality,
		                                                   holder);
		op.children.push_back(child_plan);
		return op;
	}

	vector<ColumnBinding> GetColumnBindings() override {
		return children[0]->GetColumnBindings();
	}

	void ResolveTypes() override {
		types = children[0]->types;
	}

	string GetExtensionName() const override {
		return "fabricator_collector";
	}
};

// -----------------------------------------------------------------------------

// Recursively wrap every Fabricator table-in-out LogicalGet in a finalize operator.
void WrapFabricatorInOutNodes(unique_ptr<LogicalOperator> &op) {
	for (auto &child : op->children) {
		WrapFabricatorInOutNodes(child);
	}
	if (op->type != LogicalOperatorType::LOGICAL_GET) {
		return;
	}
	auto &get = op->Cast<LogicalGet>();
	// A table-in-out is a LogicalGet with input children + the exchange in_out_function; identify by the
	// function pointer (no RTTI), then recover the holder from its bind data to inject the EOF/finalize.
	if (get.children.empty() || !get.bind_data) {
		return;
	}
	if (get.function.in_out_function == FabricatorExchangeFunction) {
		auto holder = get.bind_data->Cast<FabricatorExchangeBindData>().holder;
		if (holder) {
			op = make_uniq<FabricatorExchangeFinalizeOperator>(std::move(op), std::move(holder));
		}
		return;
	}
	// A COLLECTOR (pipeline breaker) in-out is wrapped in its Sink+Source finalize operator instead — it
	// alone emits the output (the in-out itself emits 0 rows; it only buffers input).
	if (get.function.in_out_function == FabricatorCollectorFunction) {
		auto holder = get.bind_data->Cast<FabricatorCollectorBindData>().holder;
		if (holder) {
			op = make_uniq<FabricatorCollectorFinalizeOperator>(std::move(op), std::move(holder));
		}
	}
}

void FabricatorInOutOptimize(OptimizerExtensionInput &, unique_ptr<LogicalOperator> &plan) {
	WrapFabricatorInOutNodes(plan);
}

} // namespace

void RegisterFabricatorInOutFinalizer(DBConfig &config) {
	OptimizerExtension extension;
	extension.optimize_function = FabricatorInOutOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

void RegisterFabricatorGlobalFunctions(ExtensionLoader &loader) {
	// Load-time global (connection-free) functions: enumerate the provider-union via the bridge, then register
	// each as a bare fn(...). Best-effort — if the bridge can't boot (no managed dir) this is skipped, exactly
	// like provider settings/secrets. See docs/global-functions.md.
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		fabricator::ListGlobalFunctions(stream);
		// Columns: name, kind, string_order, body, param_count(int), return_type. We read the FOUR leading
		// string columns; the precise arg/return types come from the per-function fetch below (handle = 0 =
		// global). string_order ("1"/"0") marks a byte-ordered-string table reader; body carries the complete
		// CREATE MACRO statement for kind='macro' (empty for every other kind).
		auto rows = ReadStringTable(stream, 4);
		const auto &names = rows[0];
		const auto &kinds = rows[1];
		const auto &string_order = rows[2];
		const auto &bodies = rows[3];
		if (names.empty()) {
			return; // no global functions declared
		}
		// FetchFunctionParamSchema/ReturnType need a ClientContext to turn the Arrow schema into DuckDB types;
		// a fresh connection on the loading database provides one. (The DB instance exists by Extension::Load.)
		Connection conn(loader.GetDatabaseInstance());
		auto &context = *conn.context;
		for (idx_t i = 0; i < names.size(); i++) {
			const string &fn_name = names[i];
			const string &kind = kinds[i];
			if (kind == "scalar") {
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				vector<FabricatorParamStyle> arg_styles;
				LogicalType return_type;
				bool is_volatile = true;
				try {
					// handle = 0 + empty schema = the global marker; C# resolves the function by name.
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types, &arg_styles);
					return_type = FetchFunctionReturnType(context, nullptr, "", fn_name, &is_volatile);
				} catch (std::exception &) {
					continue; // skip a global whose schema can't be resolved
				}
				ScalarFunction fn = BuildFabricatorScalarFunction(
				    nullptr, "", fn_name, arg_types, arg_names, return_type, is_volatile,
				    FabricatorVarArgsIndex(fn_name, arg_names, arg_styles));
				loader.RegisterFunction(fn);
			} else if (kind == "inout" || kind == "collector") {
				// A connection-free in-out / collector: a {TABLE}-param table function on the streaming-exchange
				// (in-out) or Sink+Source (collector) operator, with handle = 0 so the bind resolves the binding
				// against the C# global registry by name (mirrors GetOrCreateCustomInOut/CollectorFunction).
				bool is_collector = kind == "collector";
				TableFunction tf(fn_name, {}, nullptr,
				                  is_collector ? FabricatorCollectorBind : FabricatorExchangeBind,
				                  is_collector ? FabricatorCollectorInitGlobal : FabricatorExchangeInitGlobal,
				                  is_collector ? FabricatorCollectorInitLocal : FabricatorExchangeInitLocal);
				tf.in_out_function = is_collector ? FabricatorCollectorFunction : FabricatorExchangeFunction;
				auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
				fn_info->handle = nullptr; // global marker
				fn_info->schema = "";
				fn_info->func = fn_name;
				fn_info->is_proc = false;
				try {
					// Signature from the declared styles — the table input is a declared parameter, and constant
					// "cost" args are NAMED so they coexist with it (SQLNULL sentinel => ANY).
					vector<string> arg_names;
					vector<LogicalType> arg_types;
					vector<FabricatorParamStyle> arg_styles;
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types, &arg_styles);
					FabricatorBuildInOutSignature(arg_names, arg_types, arg_styles, tf,
					                              /*named_any_for_null=*/true);
					fn_info->arg_names = std::move(arg_names);
					fn_info->arg_types = std::move(arg_types);
					fn_info->arg_styles = std::move(arg_styles);
				} catch (std::exception &) {
					// no cost args
				}
				if (tf.arguments.empty()) {
					tf.arguments.push_back(LogicalType::TABLE);
				}
				tf.function_info = std::move(fn_info);
				loader.RegisterFunction(tf);
			} else if (kind == "lateral") {
				// A connection-free ROW-MAPPED function: its POSITIONAL parameters become real value-typed
				// ARGUMENTS (which is what lets `f(i.a, i.b)` bind against an outer relation), with handle = 0
				// so lateral_bind resolves the binding against the C# global registry by name. Mirrors
				// GetOrCreateLateralFunction — both go through FabricatorMakeLateralFunction.
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				vector<FabricatorParamStyle> arg_styles;
				try {
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types, &arg_styles);
				} catch (std::exception &) {
					continue; // skip a global whose signature can't be resolved
				}
				if (arg_names.empty()) {
					continue; // a lateral function's arguments ARE its input; a zero-arg one is uncallable
				}
				try {
					loader.RegisterFunction(FabricatorMakeLateralFunction(nullptr, "", fn_name, std::move(arg_names),
					                                                     std::move(arg_types),
					                                                     std::move(arg_styles)));
				} catch (std::exception &) {
					continue; // a bad declaration (e.g. a table-input parameter) must not fail extension load
				}
			} else if (kind == "aggregate" || kind == "aggregate_spill") {
				// A connection-free aggregate (UDAF): same state-vectorized callbacks as a catalog aggregate,
				// handle = 0 so agg_open resolves the session against the C# global registry by name. Usable in
				// GROUP BY / OVER / parallel. Mirrors GetOrCreateAggregateFunction.
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				vector<FabricatorParamStyle> arg_styles;
				LogicalType return_type;
				try {
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types, &arg_styles);
					return_type = FetchFunctionReturnType(context, nullptr, "", fn_name);
				} catch (std::exception &) {
					continue;
				}
				FabricatorRefuseVarArgs(fn_name, "aggregate", arg_names, arg_styles);
				AggregateFunction fn = BuildFabricatorAggregateFunction(nullptr, "", fn_name, arg_types, arg_names,
				                                                      return_type, kind == "aggregate_spill");
				loader.RegisterFunction(fn);
			} else if (kind == "table") {
				// A connection-free table function: positional args + the v29 table-session bind/scan, with
				// handle = 0 so tablefn_bind resolves the binding against the C# global registry by name. Output
				// schema is arg-dependent (resolved per-call at tablefn_bind). Mirrors GetOrCreateTableFunction's
				// non-proc branch (projection + best-effort filter pushdown; the binding decides honoring).
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				vector<FabricatorParamStyle> arg_styles;
				try {
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types, &arg_styles);
				} catch (std::exception &) {
					continue;
				}
				// Positional args only in the signature; a parameter tagged `fabricator.param_style=named` becomes a
				// DuckDB named parameter instead, which is how a global function expresses an OPTIONAL
				// argument (positional table arguments have no defaults). Same split as the catalog path.
				auto varargs_index = FabricatorVarArgsIndex(fn_name, arg_names, arg_styles);
				vector<LogicalType> positional;
				for (idx_t k = 0; k < arg_types.size(); k++) {
					auto style = k < arg_styles.size() ? arg_styles[k] : FabricatorParamStyle::POSITIONAL;
					if (style != FabricatorParamStyle::NAMED && style != FabricatorParamStyle::VARARGS) {
						positional.push_back(arg_types[k]);
					}
				}
				TableFunction tf(fn_name, positional, fabricator::ArrowStreamScan, FabricatorTableFunctionBind,
				                 fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
				// Declares batch-index support, which is what routes an order-preserving plan to the PARALLEL
				// PhysicalBufferedBatchCollector instead of the single-threaded PhysicalBufferedCollector.
				// See ArrowStreamGetPartitionData + docs/scan-concurrency.md.
				tf.get_partition_data = fabricator::ArrowStreamGetPartitionData;
				if (varargs_index != DConstants::INVALID_INDEX) {
					// The declared parameters before the tail become the MINIMUM arity; DuckDB then accepts any
					// number of further arguments, each implicitly cast to this type (ANY => no cast at all).
					tf.varargs = FabricatorVarArgsType(arg_types[varargs_index]);
				}
				tf.projection_pushdown = true;
				tf.pushdown_complex_filter = FabricatorComplexFilterPushdown;
				for (idx_t k = 0; k < arg_names.size(); k++) {
					if (k < arg_styles.size() && arg_styles[k] == FabricatorParamStyle::NAMED) {
						auto t = arg_types[k].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[k];
						tf.named_parameters[arg_names[k]] = t;
					}
				}
				auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
				fn_info->handle = nullptr; // global marker
				fn_info->schema = "";
				fn_info->func = fn_name;
				fn_info->arg_types = arg_types;
				fn_info->arg_names = arg_names;
				fn_info->arg_styles = arg_styles;
				fn_info->varargs_index = varargs_index;
				fn_info->is_proc = false;
				// A byte-ordered-string reader (e.g. Delta/Parquet) can safely push string ordering + BETWEEN.
				fn_info->string_order_pushable = string_order[i] == "1";
				tf.function_info = std::move(fn_info);
				loader.RegisterFunction(tf);
			} else if (kind == "table_sql") {
				// A connection-free SQL-GENERATING table function: no bind, no scan — only bind_replace, so
				// the call is rewritten into the generated SQL at bind time and nothing crosses at execution.
				// handle = 0 so generate_table_sql resolves the generator against the C# global registry.
				vector<string> all_names;
				vector<LogicalType> all_types;
				vector<FabricatorParamStyle> styles;
				try {
					FetchFunctionParamSchema(context, nullptr, "", fn_name, all_names, all_types, &styles);
				} catch (std::exception &) {
					continue;
				}
				// One declared schema carries both kinds: POSITIONAL fields, then NAMED ones tagged
				// fabricator.param_style="named" — split them into the DuckDB signature.
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				idx_t sqlgen_varargs_index = DConstants::INVALID_INDEX;
				TableFunction tf(fn_name, {}, nullptr, nullptr);
				FabricatorBuildSqlGenSignature(all_names, all_types, styles, tf, arg_names, arg_types,
				                               &sqlgen_varargs_index);
				tf.bind_replace = FabricatorSqlGenBindReplace;
				auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
				fn_info->varargs_index = sqlgen_varargs_index;
				fn_info->handle = nullptr; // global marker
				fn_info->schema = "";
				fn_info->func = fn_name;
				fn_info->arg_types = arg_types;
				fn_info->arg_names = arg_names;
				fn_info->is_proc = false;
				tf.function_info = std::move(fn_info);
				loader.RegisterFunction(tf);
			} else if (kind == "macro") {
				// A provider MACRO: a SQL TEMPLATE, not a marshaled function — the provider ships the complete
				// CREATE MACRO statement and DuckDB's OWN parser owns the grammar (so named-parameter defaults,
				// overload sets and `AS TABLE` all work, and the parsed statement carries the scalar/table kind).
				// Registered into the SYSTEM catalog, like a built-in: bare fn(...) in every database, no ATTACH.
				// Nothing crosses back at runtime — the binder expands it. See docs/macros-and-sqlgen-functions.md.
				try {
					Parser parser(context.GetParserOptions());
					parser.ParseQuery(bodies[i]);
					if (parser.statements.size() != 1 ||
					    parser.statements[0]->type != StatementType::CREATE_STATEMENT) {
						throw ParserException("expected a single CREATE MACRO statement");
					}
					auto &create = parser.statements[0]->Cast<CreateStatement>();
					if (create.info->type != CatalogType::MACRO_ENTRY &&
					    create.info->type != CatalogType::TABLE_MACRO_ENTRY) {
						throw ParserException("expected CREATE MACRO (scalar) or CREATE MACRO ... AS TABLE");
					}
					auto info = unique_ptr_cast<CreateInfo, CreateMacroInfo>(std::move(create.info));
					// Provider namespacing belongs in the NAME (fabricator_*), not a schema/catalog: these land
					// in the system catalog's main schema. Reject a foreign qualification rather than ignore it.
					if (!info->catalog.empty() ||
					    !(info->schema.empty() || StringUtil::CIEquals(info->schema, DEFAULT_SCHEMA))) {
						throw ParserException(
						    "a provider macro must be unqualified (or main-qualified) — got catalog '%s' schema '%s'",
						    info->catalog, info->schema);
					}
					info->catalog = INVALID_CATALOG;
					info->schema = DEFAULT_SCHEMA;
					info->internal = true; // a provider-shipped entry, like a built-in (BuiltinFunctions parity)
					info->on_conflict = OnCreateConflict::ERROR_ON_CONFLICT; // a clash must be visible, not silent
					loader.RegisterFunction(*info);
				} catch (std::exception &ex) {
					// Best-effort, like every other load-time registration: a broken provider macro must never
					// block the extension. Surfaces as a DuckDB WARNING (shell + duckdb_logs).
					DUCKDB_LOG_WARNING(context, StringUtil::Format("fabricator: global macro '%s' skipped: %s",
					                                               fn_name, ex.what()));
				}
			}
		}
	} catch (std::exception &) {
		// Bridge unavailable at load — skip global-function registration (graceful degradation).
	}
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateTableFunction(ClientContext &context,
                                                                         const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = table_function_entries_.find(func_name);
	if (cached != table_function_entries_.end()) {
		return cached->second.get();
	}
	auto kind_it = table_functions_.find(func_name);
	if (kind_it == table_functions_.end()) {
		// A provider-authored SQL-GENERATING function (v68) is registered under the bare name: no bind/scan,
		// only bind_replace (the call becomes the generated SQL).
		if (sql_table_functions_.find(func_name) != sql_table_functions_.end()) {
			return GetOrCreateSqlTableFunction(context, func_name);
		}
		// A provider-authored custom table-in-out (4g) is registered under the bare name.
		if (custom_inout_functions_.find(func_name) != custom_inout_functions_.end()) {
			return GetOrCreateCustomInOutFunction(context, func_name);
		}
		// A provider-authored custom COLLECTOR (pipeline breaker) is also registered under the bare name.
		if (custom_collector_functions_.find(func_name) != custom_collector_functions_.end()) {
			return GetOrCreateCustomCollectorFunction(context, func_name);
		}
		// A provider-authored ROW-MAPPED (correlated LATERAL) function, likewise under the bare name.
		if (custom_lateral_functions_.find(func_name) != custom_lateral_functions_.end()) {
			return GetOrCreateLateralFunction(context, func_name);
		}
		return nullptr;
	}
	bool is_proc = kind_it->second;

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	vector<FabricatorParamStyle> arg_styles;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types, &arg_styles);
	} catch (std::exception &) {
		// Stale discovery (dropped out-of-band) — treat as not-found.
		table_functions_.erase(func_name);
		RetireErase(table_function_entries_, func_name, retired_entries_);
		return nullptr;
	}

	// TVFs take positional arguments (called positionally in a FROM clause); stored procs
	// take DuckDB named parameters (EXEC @name=val), so the caller supplies a subset and
	// omitted optional params fall back to the proc's own DEFAULT.
	//
	// A provider-authored function may ALSO declare named parameters (tagged `fabricator.param_style`) alongside
	// its positional ones — that is how an OPTIONAL argument is expressed, since DuckDB positional table
	// arguments have no defaults. A discovered TVF tags nothing, so it stays fully positional.
	auto varargs_index = is_proc ? DConstants::INVALID_INDEX
	                             : FabricatorVarArgsIndex(func_name, arg_names, arg_styles);
	vector<LogicalType> positional;
	if (!is_proc) {
		for (idx_t i = 0; i < arg_types.size(); i++) {
			auto style = i < arg_styles.size() ? arg_styles[i] : FabricatorParamStyle::POSITIONAL;
			if (style != FabricatorParamStyle::NAMED && style != FabricatorParamStyle::VARARGS) {
				positional.push_back(arg_types[i]);
			}
		}
	}
	TableFunction tf(func_name, positional, fabricator::ArrowStreamScan, FabricatorTableFunctionBind,
	                 fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	// Declares batch-index support, which is what routes an order-preserving plan to the PARALLEL
	// PhysicalBufferedBatchCollector instead of the single-threaded PhysicalBufferedCollector.
	// See ArrowStreamGetPartitionData + docs/scan-concurrency.md.
	tf.get_partition_data = fabricator::ArrowStreamGetPartitionData;
	if (varargs_index != DConstants::INVALID_INDEX) {
		// The declared parameters before the tail become the MINIMUM arity; DuckDB then accepts any number of
		// further arguments, each implicitly cast to this type (ANY => no cast at all).
		tf.varargs = FabricatorVarArgsType(arg_types[varargs_index]);
	}
	tf.projection_pushdown = true;
	if (is_proc) {
		for (idx_t i = 0; i < arg_names.size(); i++) {
			// A provider declares an "accept any value" named parameter (e.g. a struct/JSON parameter bag)
			// as a SQLNULL-typed field — there is no Arrow type for ANY, so SQLNULL is the agreed marker.
			// Register it as ANY so DuckDB passes any literal (a STRUCT, a VARCHAR, …) through UNCAST.
			auto t = arg_types[i].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[i];
			tf.named_parameters[arg_names[i]] = t;
		}
	} else {
		// A provider-declared NAMED parameter on a non-proc function: register it so `fn(x, flag := true)`
		// binds. Same SQLNULL=>ANY marker rule as the proc branch above.
		for (idx_t i = 0; i < arg_names.size(); i++) {
			if (i < arg_styles.size() && arg_styles[i] == FabricatorParamStyle::NAMED) {
				auto t = arg_types[i].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[i];
				tf.named_parameters[arg_names[i]] = t;
			}
		}
		// Best-effort filter pushdown into the TVF (reuses the table scan's serializer; the
		// predicates are left in the plan so DuckDB re-applies them — an over-approximation
		// is safe). `SELECT <cols> FROM tvf(@args) WHERE <filter>` is emitted by C#. Procs
		// are not inline-wrappable, so they get no filter pushdown (DuckDB filters locally).
		tf.pushdown_complex_filter = FabricatorComplexFilterPushdown;
	}
	auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->arg_styles = arg_styles;
	fn_info->varargs_index = varargs_index;
	fn_info->is_proc = is_proc;
	// ABI v81: this is the ONE registration path whose scans go through tablefn_execute, so it is the one
	// that can be told "my execution performed DDL". A GLOBAL function belongs to no catalog and leaves this
	// null; the sqlgen and in-out paths do not use tablefn_execute at all.
	fn_info->catalog = &catalog.Cast<FabricatorCatalog>();
	tf.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(tf));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

// Build the catalog entry for a provider-authored SQL-GENERATING table function (v68): a table function with
// NO bind and NO scan, only `bind_replace`, so `db.schema.fn(args)` is REPLACED at bind time by the SQL the
// provider generates from its constant args. The entry carries this catalog's ATTACH ALIAS (catalog.GetName())
// so the generator can emit qualified references back into it — C# has no other way to learn the alias.
// Caller holds entry_lock_. See docs/macros-and-sqlgen-functions.md §2.
optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateSqlTableFunction(ClientContext &context,
                                                                           const string &func_name) {
	vector<string> all_names;
	vector<LogicalType> all_types;
	vector<FabricatorParamStyle> styles;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, all_names, all_types, &styles);
	} catch (std::exception &) {
		// Stale discovery (the provider no longer declares it) — treat as not-found, like the other paths.
		sql_table_functions_.erase(func_name);
		RetireErase(table_function_entries_, func_name, retired_entries_);
		return nullptr;
	}

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	idx_t sqlgen_varargs_index = DConstants::INVALID_INDEX;
	TableFunction tf(func_name, {}, nullptr, nullptr);
	FabricatorBuildSqlGenSignature(all_names, all_types, styles, tf, arg_names, arg_types,
	                               &sqlgen_varargs_index);
	tf.bind_replace = FabricatorSqlGenBindReplace;
	auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
	fn_info->varargs_index = sqlgen_varargs_index;
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->is_proc = false;
	fn_info->catalog_name = catalog.GetName(); // the ATTACH alias — only known here
	tf.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(tf));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateCustomInOutFunction(ClientContext &context,
                                                                              const string &func_name) {
	// Phase 6: custom C# in-out runs on the streaming exchange operator (gate + two pull streams, no
	// per-chunk materialization). Discovered-TVF `_each` + procs stay on the push model for now.
	TableFunction inout(func_name, {}, nullptr, FabricatorExchangeBind, FabricatorExchangeInitGlobal,
	                    FabricatorExchangeInitLocal);
	inout.in_out_function = FabricatorExchangeFunction;
	auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->is_proc = false;
	// The signature comes from the DECLARED parameter styles: the table input is one of them now, so it is no
	// longer hardcoded here. Constant "cost" args (e.g. the DAX expression for daxevaltable / daxeach) are
	// declared NAMED so they coexist with the TABLE argument (a positional scalar arg would too, per the
	// binder, but the single-overload rule still applies — bind_table_function.cpp).
	try {
		vector<string> arg_names;
		vector<LogicalType> arg_types;
		vector<FabricatorParamStyle> arg_styles;
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types, &arg_styles);
		FabricatorBuildInOutSignature(arg_names, arg_types, arg_styles, inout);
		fn_info->arg_names = std::move(arg_names);
		fn_info->arg_types = std::move(arg_types);
		fn_info->arg_styles = std::move(arg_styles);
	} catch (std::exception &) {
		// Unresolvable param schema: fall back to the bare table-input form so the function stays callable.
	}
	if (inout.arguments.empty()) {
		inout.arguments.push_back(LogicalType::TABLE);
	}
	inout.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(inout));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

// Build the catalog entry for a provider-authored ROW-MAPPED (correlated LATERAL) function. Everything about
// the TableFunction is built by FabricatorMakeLateralFunction (shared verbatim with the load-time GLOBAL
// registrar, so the two spellings cannot drift); this only resolves the declared signature and caches the
// entry. Caller holds entry_lock_.
optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateLateralFunction(ClientContext &context,
                                                                            const string &func_name) {
	vector<string> arg_names;
	vector<LogicalType> arg_types;
	vector<FabricatorParamStyle> arg_styles;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types, &arg_styles);
	} catch (std::exception &) {
		// Stale declaration (a cache refresh dropped it) — treat as not-found rather than registering a
		// function with no arguments, which for a lateral function is uncallable by construction.
		custom_lateral_functions_.erase(func_name);
		RetireErase(table_function_entries_, func_name, retired_entries_);
		return nullptr;
	}
	if (arg_names.empty()) {
		custom_lateral_functions_.erase(func_name);
		return nullptr;
	}
	auto fn = FabricatorMakeLateralFunction(handle_, name, func_name, std::move(arg_names), std::move(arg_types),
	                                        std::move(arg_styles));
	CreateTableFunctionInfo info(std::move(fn));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

// Build the catalog entry for a provider-authored custom COLLECTOR (pipeline breaker) — a
// `{LogicalType::TABLE}`-parameter table function under the bare name, routed to the Sink+Source collector
// operator (FabricatorCollectorFunction): it buffers ALL input, then emits the C# output once. Mirrors
// GetOrCreateCustomInOutFunction (same cost-arg named-parameter handling); only the operator callbacks differ.
// Caller holds entry_lock_.
optional_ptr<CatalogEntry> FabricatorSchemaEntry::GetOrCreateCustomCollectorFunction(ClientContext &context,
                                                                                  const string &func_name) {
	TableFunction collector(func_name, {}, nullptr, FabricatorCollectorBind,
	                        FabricatorCollectorInitGlobal, FabricatorCollectorInitLocal);
	collector.in_out_function = FabricatorCollectorFunction;
	auto fn_info = make_shared_ptr<FabricatorTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->is_proc = false;
	// Signature from the declared styles (see FabricatorBuildInOutSignature): the table input is a declared
	// parameter now, and constant "cost" args are NAMED so they coexist with it.
	try {
		vector<string> arg_names;
		vector<LogicalType> arg_types;
		vector<FabricatorParamStyle> arg_styles;
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types, &arg_styles);
		FabricatorBuildInOutSignature(arg_names, arg_types, arg_styles, collector, /*named_any_for_null=*/true);
		fn_info->arg_names = std::move(arg_names);
		fn_info->arg_types = std::move(arg_types);
		fn_info->arg_styles = std::move(arg_styles);
	} catch (std::exception &) {
		// Unresolvable param schema: fall back to the bare table-input form so the function stays callable.
	}
	if (collector.arguments.empty()) {
		collector.arguments.push_back(LogicalType::TABLE);
	}
	collector.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(collector));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

// Refuses a name the provider declared as a VIEW while also discovering it as a TABLE. Both would resolve
// through the same TABLE_ENTRY lookup, so serving either one silently hands somebody the object they did not
// ask for. Erroring is the only outcome that cannot be a wrong ANSWER, and it is recoverable by the party
// that caused it (the provider renames one). Deliberately NOT refused at ATTACH: one bad declaration must not
// destroy an otherwise working catalog — the same "skip the item, keep the rest" rule the macro path and the
// plugin scan both follow.
void FabricatorSchemaEntry::RefuseViewTableCollision(const string &entry_name) {
	lock_guard<mutex> lock(entry_lock_);
	if (view_collisions_.find(entry_name) == view_collisions_.end()) {
		return;
	}
	throw CatalogException(
	    "fabricator: '%s.%s' is declared as a VIEW by the provider and also discovered as a table — refusing to "
	    "resolve it, because either answer would silently be the wrong object. The provider must rename one.",
	    name, entry_name);
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::LookupEntry(CatalogTransaction transaction,
                                                            const EntryLookupInfo &lookup_info) {
	if (!transaction.context) {
		return nullptr;
	}
	auto type = lookup_info.GetCatalogType();
	if (type == CatalogType::TABLE_ENTRY || type == CatalogType::VIEW_ENTRY) {
		// A provider-declared VIEW resolves through the TABLE_ENTRY lookup, not a separate one:
		// Binder::Bind(BaseTableRef&) asks for TABLE_ENTRY and then switches on the entry's ACTUAL type
		// (bind_basetableref.cpp — VIEW_ENTRY takes the view branch). VIEW_ENTRY is accepted too for the
		// paths that ask for the concrete type.
		//
		// The AT clause is deliberately NOT consulted for a view: DuckDB PROPAGATES it through the view onto
		// the body's own references (`view_binder->entry_retriever.SetAtClause(entry_at_clause)`), which is
		// the right semantics — `FROM v AT (VERSION => n)` time-travels what the view READS. Consuming it
		// here would instead time-travel the DECLARATION, which has no versions.
		RefuseViewTableCollision(lookup_info.GetEntryName());
		auto view = GetOrCreateView(*transaction.context, lookup_info.GetEntryName());
		if (view) {
			return view;
		}
		if (type == CatalogType::VIEW_ENTRY) {
			return nullptr; // a discovered table is not a view, whatever the provider calls it
		}
		// The AT clause rides the lookup: a time-travel reference needs an entry whose ColumnList is the
		// schema AS OF that version, because `SELECT *` expands from the ENTRY, not from the scan.
		return GetOrCreateEntry(*transaction.context, lookup_info.GetEntryName(), lookup_info.GetAtClause());
	}
	if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		// DuckDB stores scalar/aggregate/macro functions in one namespace and resolves a function call by
		// looking up SCALAR_FUNCTION_ENTRY, then dispatching on the returned entry's actual type (see
		// bind_function_expression.cpp: MACRO_ENTRY -> BindMacro, default -> BindAggregate). So a scalar lookup
		// must also surface our custom aggregates AND our catalog-bound scalar macros — that one namespace is
		// the entire reason a macro entry handed out here gets expanded correctly.
		auto scalar = GetOrCreateScalarFunction(*transaction.context, lookup_info.GetEntryName());
		if (scalar) {
			return scalar;
		}
		auto aggregate = GetOrCreateAggregateFunction(*transaction.context, lookup_info.GetEntryName());
		if (aggregate) {
			return aggregate;
		}
		return GetOrCreateMacro(*transaction.context, lookup_info.GetEntryName(), /*want_table=*/false);
	}
	if (type == CatalogType::AGGREGATE_FUNCTION_ENTRY) {
		return GetOrCreateAggregateFunction(*transaction.context, lookup_info.GetEntryName());
	}
	if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		// Same shape on the table side: Binder::Bind(TableFunctionRef&) looks up TABLE_FUNCTION_ENTRY and then
		// checks for TABLE_MACRO_ENTRY -> BindTableMacro (bind_table_function.cpp), so a `... AS TABLE` macro
		// resolves through this lookup.
		auto table_function = GetOrCreateTableFunction(*transaction.context, lookup_info.GetEntryName());
		if (table_function) {
			return table_function;
		}
		return GetOrCreateMacro(*transaction.context, lookup_info.GetEntryName(), /*want_table=*/true);
	}
	// Direct lookups by macro type (enumeration, and DuckDB paths that ask for the concrete type).
	if (type == CatalogType::MACRO_ENTRY) {
		return GetOrCreateMacro(*transaction.context, lookup_info.GetEntryName(), /*want_table=*/false);
	}
	if (type == CatalogType::TABLE_MACRO_ENTRY) {
		return GetOrCreateMacro(*transaction.context, lookup_info.GetEntryName(), /*want_table=*/true);
	}
	return nullptr;
}

// Materializes and reports every declared view. Snapshots the names first: GetOrCreateView takes
// entry_lock_ and may DROP a broken declaration, which would invalidate an iterator over views_.
//
// ⚠ Materialization is a pure PARSE and does not bind, so listing views costs nothing per referenced table —
// which matters on a Delta/OneLake catalog where resolving one would be a _delta_log read.
// duckdb_columns()/DESCRIBE DO bind (duckdb_columns.cpp:164), but from the CALLBACK, i.e. outside this lock,
// and they swallow a bind failure into a placeholder column rather than failing the listing.
void FabricatorSchemaEntry::ScanDeclaredViews(ClientContext &context,
                                              const std::function<void(CatalogEntry &)> &callback) {
	vector<string> view_names;
	{
		lock_guard<mutex> lock(entry_lock_);
		for (auto &v : views_) {
			view_names.push_back(v.first);
		}
	}
	for (auto &v : view_names) {
		auto catalog_entry = GetOrCreateView(context, v);
		if (catalog_entry) {
			callback(*catalog_entry);
		}
	}
}

void FabricatorSchemaEntry::Scan(ClientContext &context, CatalogType type,
                               const std::function<void(CatalogEntry &)> &callback) {
	if (type == CatalogType::VIEW_ENTRY) {
		// duckdb_views() scans VIEW_ENTRY. Deliberately views ONLY, where the TABLE_ENTRY scan above reports
		// both: DuckDB's shared set makes its own VIEW_ENTRY scan yield tables too, but every consumer of
		// this type filters them out, so answering with the narrower truth loses nothing and cannot mislead
		// a future consumer that does not filter.
		ScanDeclaredViews(context, callback);
		return;
	}
	if (type == CatalogType::TABLE_ENTRY) {
		// ⚠ DECLARED VIEWS ARE REPORTED HERE TOO, and getting this backwards is a silent hole: DuckDB's own
		// DuckSchemaEntry keeps tables and views in ONE CatalogSet (duck_schema_entry.cpp:386-388), so a
		// TABLE_ENTRY scan yields both and every consumer filters by the entry's ACTUAL type —
		// duckdb_tables() skips anything that is not TABLE_ENTRY, duckdb_views() anything that is not
		// VIEW_ENTRY, and duckdb_columns() (which scans TABLE_ENTRY ALONE, duckdb_columns.cpp:91) handles
		// both. So omitting views here does not "keep them out of duckdb_tables()" — that filter is the
		// consumer's job either way — it only makes them INVISIBLE to duckdb_columns(),
		// information_schema.columns and everything built on them. MEASURED: with views omitted here,
		// duckdb_columns() reported the catalog's tables and none of its views.
		for (auto &entry : table_types_) {
			auto catalog_entry = GetOrCreateEntry(context, entry.first);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		ScanDeclaredViews(context, callback);
		return;
	}
	if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		// Snapshot the names: GetOrCreateScalarFunction locks entry_lock_ and may evict
		// a stale entry, which would invalidate an iterator over scalar_functions_.
		vector<string> names;
		vector<string> macro_names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : scalar_functions_) {
				names.push_back(fn);
			}
			// Catalog-bound macros are reported by the SCALAR / TABLE_FUNCTION scans, NOT by MACRO_ENTRY ones:
			// duckdb_functions() only ever scans SCALAR_FUNCTION_ENTRY / TABLE_FUNCTION_ENTRY /
			// PRAGMA_FUNCTION_ENTRY and then switches on each entry's ACTUAL type (duckdb_functions.cpp:101-105
			// and 806-826, which handle MACRO_ENTRY + TABLE_MACRO_ENTRY). Emitting them anywhere else would
			// resolve by name but never appear in duckdb_functions() / SHOW FUNCTIONS.
			for (auto &m : macros_) {
				macro_names.push_back(m.first);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateScalarFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		for (auto &m : macro_names) {
			// want_table=false filters to the SCALAR macros; a table macro yields nullptr here and is reported
			// by the TABLE_FUNCTION_ENTRY scan instead.
			auto catalog_entry = GetOrCreateMacro(context, m, /*want_table=*/false);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::AGGREGATE_FUNCTION_ENTRY) {
		vector<string> names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : aggregate_functions_) {
				names.push_back(fn.first);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateAggregateFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		vector<string> names;
		vector<string> macro_names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &m : macros_) {
				macro_names.push_back(m.first); // filtered to TABLE macros below — see the scalar scan's note
			}
			for (auto &fn : table_functions_) {
				names.push_back(fn.first);
			}
			// Provider-declared in-out functions (incl. a provider's per-row `<routine>_each` form) are
			// catalog functions too.
			for (auto &fn : custom_inout_functions_) {
				names.push_back(fn);
			}
			for (auto &fn : custom_collector_functions_) {
				names.push_back(fn);
			}
			for (auto &fn : custom_lateral_functions_) {
				names.push_back(fn);
			}
			// SQL-generating (bind_replace) functions are catalog table functions too — without this they
			// resolve by name but never appear in duckdb_functions() / SHOW FUNCTIONS.
			for (auto &fn : sql_table_functions_) {
				names.push_back(fn);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateTableFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		for (auto &m : macro_names) {
			auto catalog_entry = GetOrCreateMacro(context, m, /*want_table=*/true);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
	}
}

void FabricatorSchemaEntry::Scan(CatalogType type, const std::function<void(CatalogEntry &)> &callback) {
	// No context available: only report already-materialized entries.
	lock_guard<mutex> lock(entry_lock_);
	// macro_entries_ holds both kinds (the parse decides which), so reporting is filtered by entry type. Note
	// scalar macros are reported under SCALAR_FUNCTION_ENTRY and table macros under TABLE_FUNCTION_ENTRY, since
	// those are the only types duckdb_functions() asks for — see the context-taking overload. ONE if-chain, so
	// no type can be reported twice.
	auto report_macros = [&](CatalogType macro_type) {
		for (auto &entry : macro_entries_) {
			if (entry.second->type == macro_type) {
				callback(*entry.second);
			}
		}
	};
	if (type == CatalogType::TABLE_ENTRY) {
		for (auto &entry : entries_) {
			callback(*entry.second);
		}
		// Views too — same reason as the context-taking overload (duckdb_columns() scans TABLE_ENTRY alone).
		for (auto &entry : view_entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::VIEW_ENTRY) {
		for (auto &entry : view_entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		for (auto &entry : function_entries_) {
			callback(*entry.second);
		}
		report_macros(CatalogType::MACRO_ENTRY);
	} else if (type == CatalogType::AGGREGATE_FUNCTION_ENTRY) {
		for (auto &entry : aggregate_function_entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		for (auto &entry : table_function_entries_) {
			callback(*entry.second);
		}
		report_macros(CatalogType::TABLE_MACRO_ENTRY);
	} else if (type == CatalogType::MACRO_ENTRY) {
		report_macros(CatalogType::MACRO_ENTRY);
	} else if (type == CatalogType::TABLE_MACRO_ENTRY) {
		report_macros(CatalogType::TABLE_MACRO_ENTRY);
	}
}

[[noreturn]] static void ReadOnly(const char *op) {
	throw NotImplementedException("fabricator: %s is not supported (read-only catalog in Phase 1)", op);
}

optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateTable(CatalogTransaction transaction, BoundCreateTableInfo &info) {
	if (!transaction.context) {
		throw InternalException("fabricator: CREATE TABLE requires a client context");
	}
	auto &context = *transaction.context;
	auto &base = info.Base();
	FabricatorSetActiveTxn(handle_, context); // CREATE (+ optional DROP for REPLACE) joins this txn's connection

	// Column names + types, and per-column nullability (NOT NULL constraints). A DuckDB GENERATED column
	// (`col type AS (expr)`) is (mis)used as an IDENTITY marker — DuckDB has no IDENTITY concept — so its name
	// is collected for the identity arg; it is otherwise sent as a normal column (the C# SQL Server provider
	// turns it into an IDENTITY column). The generated-ness exists only here at create time.
	vector<string> names;
	vector<LogicalType> types;
	string identity_arg;
	for (auto &col : base.columns.Logical()) {
		names.push_back(col.Name());
		types.push_back(col.Type());
		if (col.Generated()) {
			if (!identity_arg.empty()) {
				identity_arg += ",";
			}
			identity_arg += col.Name();
		}
	}
	vector<bool> nullable(names.size(), true);
	for (auto &constraint : base.constraints) {
		if (constraint->type == ConstraintType::NOT_NULL) {
			auto &nn = constraint->Cast<NotNullConstraint>();
			if (nn.index.index < nullable.size()) {
				nullable[nn.index.index] = false;
			}
		}
	}

	// Key constraints, carried to the backend as 0-based column-index groups:
	// the PRIMARY KEY as a single comma-separated group, each UNIQUE as its own.
	vector<idx_t> pk_indices;
	vector<vector<idx_t>> unique_groups;
	for (auto &constraint : base.constraints) {
		if (constraint->type != ConstraintType::UNIQUE) {
			continue;
		}
		auto &uc = constraint->Cast<UniqueConstraint>();
		vector<idx_t> group;
		for (auto &logical : uc.GetLogicalIndexes(base.columns)) {
			group.push_back(logical.index);
		}
		if (uc.IsPrimaryKey()) {
			pk_indices = group;
		} else {
			unique_groups.push_back(std::move(group));
		}
	}
	// PRIMARY KEY columns must be NOT NULL in SQL Server.
	for (auto idx : pk_indices) {
		if (idx < nullable.size()) {
			nullable[idx] = false;
		}
	}

	auto join_indices = [](const vector<idx_t> &idxs) {
		string out;
		for (idx_t i = 0; i < idxs.size(); i++) {
			if (i > 0) {
				out += ",";
			}
			out += std::to_string(idxs[i]);
		}
		return out;
	};
	string pk_arg = join_indices(pk_indices);
	string unique_arg;
	for (auto &group : unique_groups) {
		if (!unique_arg.empty()) {
			unique_arg += ";";
		}
		unique_arg += join_indices(group);
	}

	// Literal column DEFAULTs: "<index> <payload>" pairs, payload = base64(value
	// text) or "-" for DEFAULT NULL. Non-literal defaults (expressions) are skipped.
	string defaults_arg;
	for (idx_t i = 0; i < names.size(); i++) {
		auto &col = base.columns.GetColumn(LogicalIndex(i));
		if (!col.HasDefaultValue()) {
			continue;
		}
		// Unwrap one CAST level (e.g. boolean literals parse as CAST(... AS BOOLEAN)).
		const ParsedExpression *expr = &col.DefaultValue();
		if (expr->type == ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<CastExpression>().child.get();
		}
		if (!expr || expr->type != ExpressionType::VALUE_CONSTANT) {
			continue; // literals only
		}
		auto &val = expr->Cast<ConstantExpression>().value;
		if (!defaults_arg.empty()) {
			defaults_arg += " ";
		}
		defaults_arg += std::to_string(i) + " ";
		if (val.IsNull()) {
			defaults_arg += "-";
		} else {
			string text = val.ToString();
			defaults_arg += Blob::ToBase64(string_t(text.c_str(), (uint32_t)text.size()));
		}
	}

	bool replace = base.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT;
	bool if_not_exists = base.on_conflict == OnCreateConflict::IGNORE_ON_CONFLICT;

	// A plain CREATE TABLE must REFUSE an existing name. DuckDB delegates that decision to the catalog, so
	// with this check absent the create reached the provider as an ordinary create and each provider
	// answered in its own way — SQL Server raised its own 2714 (loud, but a provider error rather than a
	// catalog one), while the Delta provider's OpenOrCreateAsync simply OPENED the existing table, making
	//     CREATE TABLE t AS SELECT …        -- no rows written, exit 0, the OLD data kept
	//     CREATE TABLE t (a INT, b VARCHAR) -- no error, the DECLARED SCHEMA silently ignored
	// succeed while doing nothing.
	//
	// Note WHY a CTAS reaches this function at all, because it is not obvious and it explains the missing
	// rows: PhysicalPlanGenerator::CreatePlan(LogicalCreateTable &) probes for an existing entry and, on a
	// hit with a non-REPLACE conflict action, plans a bare PhysicalCreateTable and DISCARDS THE CHILD PLAN
	// (the SELECT). So "no rows written" was DuckDB's plan downgrade, not the provider swallowing a write —
	// the write was never planned, and the begin_bulk path (where mode = Overwrite lives) is never entered
	// in this shape. Only "no error" was ours. That same downgrade is why ONE check here covers both
	// spellings. REPLACE (drops first) and IGNORE (forwarded to the provider) keep their own handling
	// below, so only ERROR_ON_CONFLICT is decided here.
	//
	// GetOrCreateEntry is the existence oracle rather than a bare table_types_ lookup, because a table can
	// exist without being in the discovered name list: an ATTACH `table_filter` bounds ENUMERATION only, and
	// that path fetches by name. It costs nothing extra either way — the CreatePlan probe above resolves
	// through this same function, so on the conflict path the entry is already cached, and a non-conflicting
	// create pays only a table_types_ miss.
	if (base.on_conflict == OnCreateConflict::ERROR_ON_CONFLICT && GetOrCreateEntry(context, base.table)) {
		throw CatalogException::EntryAlreadyExists(CatalogType::TABLE_ENTRY, base.table);
	}

	if (replace) {
		fabricator::DropTable(handle_, name, base.table, /*if_exists=*/true);
	}

	// Native CREATE TABLE ... PARTITIONED BY (cols): the column names (comma-separated) go to the provider
	// (the Delta provider records them as partition columns; SQL Server / DAX ignore the arg).
	string partition_arg = fabricator::PartitionColumnsArg(base.partition_keys);
	// Native CREATE TABLE ... SORTED BY (cols): the SQL Server provider maps these to a Fabric Warehouse
	// WITH (CLUSTER BY (cols)) layout (Delta / DAX ignore the arg).
	string sort_arg = fabricator::PartitionColumnsArg(base.sort_keys);
	// CREATE TABLE ... WITH (key='value', ...): a flat JSON object of provider options (v67) — the provider
	// parses the keys it knows (Delta: per-table properties/write tuning) and REJECTS unknown ones.
	string options_arg = fabricator::TableOptionsArg(base.options);

	// A schema-only Arrow stream carries the column definitions to the backend. The text-column SQL type
	// (mssql_ctas_text_type / mssql_default_varchar_length) is read C#-side from the provider settings store
	// (see docs/settings-architecture.md), not passed here.
	fabricator::ArrowProducer producer(types, names, fabricator::BoundaryClientProperties(context));
	producer.SetNullability(nullable);
	producer.Finish();
	fabricator::CreateTable(handle_, name, base.table, *producer.Stream(), if_not_exists, pk_arg, unique_arg,
	                      defaults_arg, partition_arg, sort_arg, identity_arg, options_arg);

	// Register the new table (also invalidates any cached entry) and return it.
	AddTable(base.table, "BASE TABLE");
	return GetOrCreateEntry(context, base.table);
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateFunction(CatalogTransaction, CreateFunctionInfo &) {
	ReadOnly("CREATE FUNCTION");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateIndex(CatalogTransaction, CreateIndexInfo &,
                                                            TableCatalogEntry &) {
	ReadOnly("CREATE INDEX");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateView(CatalogTransaction, CreateViewInfo &) {
	ReadOnly("CREATE VIEW");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateSequence(CatalogTransaction, CreateSequenceInfo &) {
	ReadOnly("CREATE SEQUENCE");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateTableFunction(CatalogTransaction, CreateTableFunctionInfo &) {
	ReadOnly("CREATE TABLE FUNCTION");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateCopyFunction(CatalogTransaction, CreateCopyFunctionInfo &) {
	ReadOnly("CREATE COPY FUNCTION");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreatePragmaFunction(CatalogTransaction, CreatePragmaFunctionInfo &) {
	ReadOnly("CREATE PRAGMA FUNCTION");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateCollation(CatalogTransaction, CreateCollationInfo &) {
	ReadOnly("CREATE COLLATION");
}
optional_ptr<CatalogEntry> FabricatorSchemaEntry::CreateType(CatalogTransaction, CreateTypeInfo &) {
	ReadOnly("CREATE TYPE");
}
void FabricatorSchemaEntry::DropEntry(ClientContext &context, DropInfo &info) {
	if (info.type != CatalogType::TABLE_ENTRY) {
		throw NotImplementedException("fabricator: only DROP TABLE is supported yet (not %s)",
		                              CatalogTypeToString(info.type));
	}
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	FabricatorSetActiveTxn(handle_, context);
	fabricator::DropTable(handle_, name, info.name, if_exists);

	lock_guard<mutex> lock(entry_lock_);
	table_types_.erase(info.name);
	RetireErase(entries_, info.name, retired_entries_);
	RetireAtEntriesFor(at_entries_, info.name, retired_entries_);
}
// (The hand-rolled JsonPathArray helper died with ABI v74: FabricatorRenderAlterJson owns every string in
//  the request now. It escaped only `"` and `\`, so a legal DuckDB identifier carrying a control character
//  — `"a<TAB>b"` — produced invalid JSON and the ALTER failed inside the managed parser, naming a byte
//  position rather than the column. Field paths are still ARRAYS of segments, for the reason it gave: a
//  segment name may contain dots.)

void FabricatorSchemaEntry::Alter(CatalogTransaction transaction, AlterInfo &info) {
	if (info.type != AlterType::ALTER_TABLE) {
		throw NotImplementedException("fabricator: only ALTER TABLE is supported");
	}
	if (!transaction.context) {
		throw InternalException("fabricator: ALTER TABLE requires a client context");
	}
	auto &context = *transaction.context;
	FabricatorSetActiveTxn(handle_, context); // every ALTER below joins this txn's connection
	fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), fabricator::SessionKeyFor(&context)); // host-FS opener for a Delta-catalog ALTER
	auto &table_info = info.Cast<AlterTableInfo>();
	const string &table = table_info.name;

	// Refresh the cached entry AFTER an ALTER. We don't just evict-and-wait-for-lazy-refetch: the lazy
	// re-fetch would be triggered by whatever binds the table next — and under dbt that is a SEPARATE
	// (introspection) transaction with no pinned connection, so its `SELECT * FROM t WHERE 1=0` runs on a
	// POOLED connection and BLOCKS on this ALTER's still-uncommitted Sch-M lock (held on THIS transaction's
	// connection) until the command timeout → catalog eviction → "table does not exist" (the concurrent
	// schema-evolution deadlock, docs/dbt-incremental.md). Instead we re-fetch EAGERLY here, on THIS
	// transaction's connection (the ambient txn was set at the top of Alter): that connection owns the Sch-M
	// lock, so a read-your-writes probe on the same session sees the new schema with NO lock wait, and the
	// fresh entry is cached — so the next bind (even in a different transaction) finds it and never issues the
	// blocking pooled re-fetch. A transaction that later ROLLS BACK leaves this entry stale (it reflects the
	// uncommitted ALTER) → RollbackTransaction invalidates the cache (fabricator_transaction.cpp).
	auto refresh = [&](const string &t) {
		{
			lock_guard<mutex> lock(entry_lock_);
			RetireErase(entries_, t, retired_entries_);
			RetireAtEntriesFor(at_entries_, t, retired_entries_);
		}
		try {
			GetOrCreateEntry(context, t); // eager re-fetch on this txn's connection (no Sch-M self-block)
		} catch (...) {
			// Best-effort: on any failure leave the entry evicted (falls back to lazy re-fetch).
		}
	};

	// The switch below only DESCRIBES the request (ABI v74) — it issues nothing. That is what the typed doc
	// bought: with alter_kind + arg1 + arg2 + flags each meaning something different per kind, every branch
	// had to spell its own crossing, so the fourteen calls were fourteen chances to mis-order a carrier.
	// Now there is ONE crossing and ONE piece of cache bookkeeping, both stated once, below.
	FabricatorAlterRequest request;
	// The TYPE CHANNEL for the three kinds that carry a new column/field type. Function-scoped because the
	// stream must stay alive until the crossing; nullptr for every other kind.
	unique_ptr<fabricator::ArrowProducer> column_type;
	auto carry_type = [&](const LogicalType &type, const string &column_name) {
		vector<LogicalType> types {type};
		vector<string> names {column_name};
		column_type = make_uniq<fabricator::ArrowProducer>(types, names, fabricator::BoundaryClientProperties(context));
		column_type->Finish();
	};

	switch (table_info.alter_table_type) {
	case AlterTableType::RENAME_TABLE: {
		auto &rt = table_info.Cast<RenameTableInfo>();
		request.kind = "rename_table";
		request.new_name = rt.new_table_name;
		break;
	}
	case AlterTableType::RENAME_COLUMN: {
		auto &rc = table_info.Cast<RenameColumnInfo>();
		request.kind = "rename_column";
		request.column = rc.old_name;
		request.new_name = rc.new_name;
		break;
	}
	case AlterTableType::ADD_COLUMN: {
		auto &ac = table_info.Cast<AddColumnInfo>();
		request.kind = "add_column";
		request.column = ac.new_column.Name();
		request.guard = ac.if_column_not_exists;
		carry_type(ac.new_column.Type(), ac.new_column.Name());
		break;
	}
	case AlterTableType::REMOVE_COLUMN: {
		auto &rc = table_info.Cast<RemoveColumnInfo>();
		request.kind = "drop_column";
		request.column = rc.removed_column;
		request.guard = rc.if_column_exists;
		break;
	}
	case AlterTableType::ALTER_COLUMN_TYPE: {
		auto &ct = table_info.Cast<ChangeColumnTypeInfo>();
		request.kind = "column_type";
		request.column = ct.column_name;
		carry_type(ct.target_type, ct.column_name);
		break;
	}
	case AlterTableType::ADD_FIELD: {
		// `ALTER TABLE t ADD COLUMN s.f <type>` — add a field INSIDE a nested struct. `path` is the
		// CONTAINING struct's; the new field's name + type ride the type channel, exactly like ADD_COLUMN.
		auto &af = table_info.Cast<AddFieldInfo>();
		request.kind = "add_field";
		request.path = af.column_path;
		request.guard = af.if_field_not_exists;
		carry_type(af.new_field.Type(), af.new_field.Name());
		break;
	}
	case AlterTableType::REMOVE_FIELD: {
		auto &rf = table_info.Cast<RemoveFieldInfo>();
		request.kind = "drop_field";
		request.path = rf.column_path;
		request.guard = rf.if_column_exists;
		break;
	}
	case AlterTableType::RENAME_FIELD: {
		auto &rf = table_info.Cast<RenameFieldInfo>();
		request.kind = "rename_field";
		request.path = rf.column_path;
		request.new_name = rf.new_name;
		break;
	}
	case AlterTableType::SET_NOT_NULL: {
		auto &sn = table_info.Cast<SetNotNullInfo>();
		request.kind = "set_not_null";
		request.column = sn.column_name;
		break;
	}
	case AlterTableType::DROP_NOT_NULL: {
		auto &dn = table_info.Cast<DropNotNullInfo>();
		request.kind = "drop_not_null";
		request.column = dn.column_name;
		break;
	}
	case AlterTableType::SET_DEFAULT: {
		auto &sd = table_info.Cast<SetDefaultInfo>();
		request.column = sd.column_name;
		if (!sd.expression) {
			request.kind = "drop_default"; // DROP DEFAULT is SET DEFAULT with no expression
			break;
		}
		request.kind = "set_default";
		// Only literal defaults: unwrap one CAST (booleans parse as CAST(... AS BOOLEAN)).
		const ParsedExpression *expr = sd.expression.get();
		if (expr->type == ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<CastExpression>().child.get();
		}
		if (!expr || expr->type != ExpressionType::VALUE_CONSTANT) {
			throw NotImplementedException("fabricator: only literal column DEFAULTs are supported");
		}
		auto &val = expr->Cast<ConstantExpression>().value;
		// The "default" key carries the literal's TEXT, JSON null for DEFAULT NULL. The old arg2 spelled
		// those two states "-" and "b"+base64(text) — the base64 existed ONLY so an empty-string literal
		// stayed distinguishable from an absent C string, which a JSON string does natively.
		request.has_default = true;
		request.default_is_null = val.IsNull();
		if (!request.default_is_null) {
			request.default_literal = val.ToString();
		}
		break;
	}
	case AlterTableType::SET_SORTED_BY: {
		// ALTER TABLE t SET SORTED BY (a, b) / RESET SORTED BY (empty orders). Clustering has no sort
		// direction — only plain (implicitly ascending) column names are accepted.
		auto &ss = table_info.Cast<SetSortedByInfo>();
		request.kind = "set_sorted_by";
		request.has_columns = true; // an EMPTY list is the RESET spelling, not an absent one
		for (auto &order : ss.orders) {
			if (order.type == OrderType::DESCENDING) {
				throw NotImplementedException(
				    "fabricator: SET SORTED BY has no sort direction — use plain column names");
			}
			if (!order.expression || order.expression->type != ExpressionType::COLUMN_REF) {
				throw NotImplementedException("fabricator: SET SORTED BY accepts plain column names only");
			}
			request.columns.push_back(order.expression->Cast<ColumnRefExpression>().GetColumnName());
		}
		break;
	}
	case AlterTableType::SET_PARTITIONED_BY: {
		// Crossed so the provider errors meaningfully (Delta: repartitioning needs a full rewrite —
		// COPY ... MODE 'overwrite' + PARTITION_COLUMNS; SQL Server/DAX: unsupported).
		auto &sp = table_info.Cast<SetPartitionedByInfo>();
		request.kind = "set_partitioned_by";
		request.has_columns = true;
		for (auto &key : sp.partition_keys) {
			if (!key || key->type != ExpressionType::COLUMN_REF) {
				throw NotImplementedException("fabricator: SET PARTITIONED BY accepts plain column names only");
			}
			request.columns.push_back(key->Cast<ColumnRefExpression>().GetColumnName());
		}
		break;
	}
	default:
		throw NotImplementedException("fabricator: this ALTER TABLE variant is not supported yet");
	}

	// The table-session handle replaces the (schema, table) pair the old alter_table carried. Resolving it
	// here costs NO extra crossing: Catalog::Alter looked this very entry up moments ago to decide whether
	// to dispatch at all (catalog.cpp — it returns early when the lookup misses), so this is a cache hit on
	// the entry DuckDB just materialized. Resolved AFTER the switch so a variant that throws never
	// materializes anything.
	auto alter_entry = GetOrCreateEntry(context, table);
	if (!alter_entry) {
		throw CatalogException("fabricator: ALTER TABLE: table \"%s\" does not exist", table);
	}
	fabricator::TableAlter(alter_entry->Cast<FabricatorTableEntry>().TableHandle(),
	                       FabricatorRenderAlterJson(request), column_type ? column_type->Stream() : nullptr);

	if (table_info.alter_table_type == AlterTableType::RENAME_TABLE) {
		// A rename MOVES the entry rather than refreshing it: the name list is re-keyed and both names are
		// evicted (the old one is gone, the new one may shadow a stale entry).
		auto &rt = table_info.Cast<RenameTableInfo>();
		lock_guard<mutex> lock(entry_lock_);
		auto it = table_types_.find(table);
		string type = it != table_types_.end() ? it->second : string("BASE TABLE");
		table_types_.erase(table);
		RetireErase(entries_, table, retired_entries_);
		RetireAtEntriesFor(at_entries_, table, retired_entries_);
		table_types_[rt.new_table_name] = type;
		RetireErase(entries_, rt.new_table_name, retired_entries_);
		RetireAtEntriesFor(at_entries_, rt.new_table_name, retired_entries_);
	} else {
		refresh(table);
	}
}

} // namespace duckdb
