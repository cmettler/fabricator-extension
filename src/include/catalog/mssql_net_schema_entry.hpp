//===----------------------------------------------------------------------===//
//                         mssql_net — schema catalog entry
//===----------------------------------------------------------------------===//

#pragma once

#include "arrownet/abi.h"
#include "catalog/mssql_net_table_entry.hpp"
#include "duckdb/catalog/catalog_entry/schema_catalog_entry.hpp"
#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/mutex.hpp"

namespace duckdb {

class MssqlNetSchemaEntry : public SchemaCatalogEntry {
public:
	MssqlNetSchemaEntry(Catalog &catalog, CreateSchemaInfo &info, ArrowNetHandle handle);

	//! Registers a discovered table/view name (called at attach time).
	void AddTable(const string &table_name, const string &table_type);

	//! Drops all cached table names + materialized entries (for cache refresh).
	void ClearTables();

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

	ArrowNetHandle handle_;
	case_insensitive_map_t<string> table_types_; // table name -> "BASE TABLE" | "VIEW"
	mutex entry_lock_;
	case_insensitive_map_t<unique_ptr<MssqlNetTableEntry>> entries_;
};

} // namespace duckdb
