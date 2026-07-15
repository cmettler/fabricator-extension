//===----------------------------------------------------------------------===//
//                         fabricator — schema catalog entry
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
#include "catalog/fabricator_table_entry.hpp"
#include "duckdb/catalog/catalog_entry/aggregate_function_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/scalar_function_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/schema_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/table_function_catalog_entry.hpp"
#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/mutex.hpp"

namespace duckdb {

class DBConfig;
class ExtensionLoader;

//! Registers the table-in-out OperatorFinalize optimizer extension (4g): an OptimizerExtension that wraps
//! each discovered table-in-out LogicalGet in a pass-through LogicalExtensionOperator whose OperatorFinalize
//! signals the managed session that all input is consumed ("in-out finished" — a reliable resource-cleanup
//! hook, and a clean commit of a read-only TVF's snapshot transaction). NOT the proc commit (DuckDB's
//! transaction drives that). Call once at extension load.
void RegisterFabricatorInOutFinalizer(DBConfig &config);

//! Registers load-time GLOBAL (connection-free) functions: enumerates the provider-union via the bridge and
//! registers each as a bare `fn(...)` (no ATTACH). Best-effort — skipped if the bridge can't boot at load.
//! Currently scalar only (table/in-out kinds are a later slice). See docs/global-functions.md.
void RegisterFabricatorGlobalFunctions(ExtensionLoader &loader);

class FabricatorSchemaEntry : public SchemaCatalogEntry {
public:
	FabricatorSchemaEntry(Catalog &catalog, CreateSchemaInfo &info, FabricatorHandle handle);

	//! Registers a discovered table/view name (called at attach time).
	void AddTable(const string &table_name, const string &table_type);

	//! Registers a discovered scalar UDF name (called at attach time). Exposed as a
	//! DuckDB scalar function so `db.schema.func(args)` resolves; arg/return types and
	//! the body are resolved lazily on first lookup.
	void AddScalarFunction(const string &func_name);

	//! Registers a discovered table-returning routine (called at attach time): a TVF
	//! (`is_proc=false`) or a stored procedure (`is_proc=true`). Both are exposed as a
	//! DuckDB table function so `SELECT * FROM db.schema.fn(args)` resolves; arg + output
	//! schemas are resolved lazily on first lookup (procs use sp_describe + EXEC).
	void AddTableFunction(const string &func_name, bool is_proc);

	//! Registers a provider-authored custom table-in-out function (4g, `kind='inout'`): a
	//! `{LogicalType::TABLE}`-parameter table function under the bare name (no scalar-arg
	//! scan form, no `_each` alias). Resolved as `SELECT * FROM db.schema.fn(<input table>)`;
	//! its output schema is the function's full declared schema (no input echo).
	void AddInOutFunction(const string &func_name);

	//! Registers a provider-authored custom COLLECTOR table-in-out function (`kind='collector'`): like
	//! AddInOutFunction, a `{LogicalType::TABLE}`-parameter table function under the bare name, but routed
	//! to the Sink+Source pipeline-breaker operator (buffers all input, then emits) instead of the streaming
	//! exchange. For whole-table transforms whose output depends on the entire input. See
	//! docs/inout-collector-mode.md.
	void AddCollectorFunction(const string &func_name);

	//! Registers a provider-authored custom aggregate function (4h): a UDAF exposed as a DuckDB
	//! AggregateFunctionCatalogEntry so `db.schema.fn(args)` resolves wherever DuckDB allows an aggregate
	//! (GROUP BY / parallel / OVER). Arg + return types are resolved lazily on first lookup. `spillable`
	//! (`kind='aggregate_spill'`) selects the bytes-in-blob mode (state serialized into DuckDB's blob so
	//! external GROUP BY can spill it); otherwise the fast in-memory id-based mode (state lives in C#).
	void AddAggregateFunction(const string &func_name, bool spillable);

	//! Drops all cached table + function names and materialized entries (cache refresh).
	void ClearTables();

	//! Drops only the MATERIALIZED entries (table columns/rowid + function entries), keeping the discovered
	//! NAME lists — so they lazily re-fetch their details on next access without a full re-discovery (no
	//! ClientContext needed). Used on transaction ROLLBACK to discard any entry that an ALTER's eager
	//! re-fetch cached from the now-undone (uncommitted) schema. See Alter() / fabricator_transaction.cpp.
	void InvalidateEntryCache();

	void Scan(ClientContext &context, CatalogType type, const std::function<void(CatalogEntry &)> &callback) override;
	void Scan(CatalogType type, const std::function<void(CatalogEntry &)> &callback) override;
	optional_ptr<CatalogEntry> LookupEntry(CatalogTransaction transaction,
	                                       const EntryLookupInfo &lookup_info) override;

	// --- read-only: all mutating operations are unsupported ---
	optional_ptr<CatalogEntry> CreateTable(CatalogTransaction transaction, BoundCreateTableInfo &info) override;
	optional_ptr<CatalogEntry> CreateFunction(CatalogTransaction transaction, CreateFunctionInfo &info) override;
	optional_ptr<CatalogEntry> CreateIndex(CatalogTransaction transaction, CreateIndexInfo &info,
	                                       TableCatalogEntry &table) override;
	optional_ptr<CatalogEntry> CreateView(CatalogTransaction transaction, CreateViewInfo &info) override;
	optional_ptr<CatalogEntry> CreateSequence(CatalogTransaction transaction, CreateSequenceInfo &info) override;
	optional_ptr<CatalogEntry> CreateTableFunction(CatalogTransaction transaction,
	                                               CreateTableFunctionInfo &info) override;
	optional_ptr<CatalogEntry> CreateCopyFunction(CatalogTransaction transaction,
	                                              CreateCopyFunctionInfo &info) override;
	optional_ptr<CatalogEntry> CreatePragmaFunction(CatalogTransaction transaction,
	                                                CreatePragmaFunctionInfo &info) override;
	optional_ptr<CatalogEntry> CreateCollation(CatalogTransaction transaction, CreateCollationInfo &info) override;
	optional_ptr<CatalogEntry> CreateType(CatalogTransaction transaction, CreateTypeInfo &info) override;
	void DropEntry(ClientContext &context, DropInfo &info) override;
	void Alter(CatalogTransaction transaction, AlterInfo &info) override;

private:
	optional_ptr<CatalogEntry> GetOrCreateEntry(ClientContext &context, const string &table_name);
	optional_ptr<CatalogEntry> GetOrCreateScalarFunction(ClientContext &context, const string &func_name);
	optional_ptr<CatalogEntry> GetOrCreateTableFunction(ClientContext &context, const string &func_name);
	//! Materializes the synthetic `<base>_each` table-in-out alias (4g): a TABLE-parameter table
	//! function applying the discovered TVF `base_func` once per input row via CROSS APPLY.
	optional_ptr<CatalogEntry> GetOrCreateInOutFunction(ClientContext &context, const string &each_name,
	                                                    const string &base_func);
	//! Materializes a provider-authored custom table-in-out function (4g): a TABLE-parameter table
	//! function whose output schema is the function's full declared schema (dispatched in C#).
	optional_ptr<CatalogEntry> GetOrCreateCustomInOutFunction(ClientContext &context, const string &func_name);
	//! Materializes a provider-authored custom COLLECTOR table-in-out function: a TABLE-parameter table
	//! function routed to the Sink+Source pipeline-breaker operator (buffers all input, then emits).
	optional_ptr<CatalogEntry> GetOrCreateCustomCollectorFunction(ClientContext &context, const string &func_name);
	//! Materializes a custom aggregate (4h) as an AggregateFunctionCatalogEntry whose callbacks marshal
	//! per-group int64 state ids + Arrow batches to the C# accumulator over the agg_* ABI.
	optional_ptr<CatalogEntry> GetOrCreateAggregateFunction(ClientContext &context, const string &func_name);

	FabricatorHandle handle_;
	case_insensitive_map_t<string> table_types_; // table name -> "BASE TABLE" | "VIEW"
	case_insensitive_set_t scalar_functions_;    // discovered scalar UDF names
	case_insensitive_map_t<bool> table_functions_; // table-returning routine name -> is_proc (TVF=false)
	case_insensitive_map_t<string> inout_functions_; // synthetic `<base>_each` alias -> base TVF name (4g)
	case_insensitive_set_t custom_inout_functions_;  // provider-authored custom table-in-out names (4g)
	case_insensitive_set_t custom_collector_functions_; // provider-authored custom collector (pipeline-breaker) names
	case_insensitive_map_t<bool> aggregate_functions_; // custom aggregate (UDAF) name -> spillable (4h)
	mutex entry_lock_;
	case_insensitive_map_t<unique_ptr<FabricatorTableEntry>> entries_;
	case_insensitive_map_t<unique_ptr<ScalarFunctionCatalogEntry>> function_entries_;
	case_insensitive_map_t<unique_ptr<TableFunctionCatalogEntry>> table_function_entries_;
	case_insensitive_map_t<unique_ptr<AggregateFunctionCatalogEntry>> aggregate_function_entries_;
};

} // namespace duckdb
