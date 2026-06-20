//===----------------------------------------------------------------------===//
//                         arrownet — catalog
//===----------------------------------------------------------------------===//

#pragma once

#include "arrownet/abi.h"
#include "duckdb/catalog/catalog.hpp"
#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/mutex.hpp"

namespace duckdb {

class ArrowNetSchemaEntry;

//! Read-only catalog backed by a SQL Server connection (via the C# bridge).
//! Owns the bridge catalog handle for its lifetime.
class ArrowNetCatalog : public Catalog {
public:
	ArrowNetCatalog(AttachedDatabase &db, string internal_name, ArrowNetHandle handle, string db_path);
	~ArrowNetCatalog() override;

	//! Restricts catalog discovery to schemas/tables matching these (icase regex,
	//! substring) patterns. Empty => no filter. Validates the patterns (throws on a
	//! bad regex). Must be called before LoadCatalog. Mirrors the C++ mssql extension's
	//! schema_filter / table_filter ATTACH options.
	void SetCatalogFilters(const string &schema_filter, const string &table_filter);

	//! Validates the filter regex patterns (throws "Invalid regex …" on a bad one).
	//! Static so ATTACH can validate before opening the connection.
	static void ValidateCatalogFilters(const string &schema_filter, const string &table_filter);

	//! Discovers schemas + tables from SQL Server (called once at attach time).
	void LoadCatalog(ClientContext &context);

	//! Re-discovers schemas + tables and drops cached entries (so out-of-band DDL
	//! via mssql_net_exec becomes visible). Backs mssql_refresh_cache().
	void RefreshCache(ClientContext &context);

	ArrowNetHandle GetHandle() const {
		return handle_;
	}

	void Initialize(bool load_builtin) override;
	string GetCatalogType() override;

	optional_ptr<CatalogEntry> CreateSchema(CatalogTransaction transaction, CreateSchemaInfo &info) override;
	optional_ptr<SchemaCatalogEntry> LookupSchema(CatalogTransaction transaction,
	                                              const EntryLookupInfo &schema_lookup,
	                                              OnEntryNotFound if_not_found) override;
	void ScanSchemas(ClientContext &context, std::function<void(SchemaCatalogEntry &)> callback) override;

	PhysicalOperator &PlanCreateTableAs(ClientContext &context, PhysicalPlanGenerator &planner,
	                                    LogicalCreateTable &op, PhysicalOperator &plan) override;
	PhysicalOperator &PlanInsert(ClientContext &context, PhysicalPlanGenerator &planner, LogicalInsert &op,
	                             optional_ptr<PhysicalOperator> plan) override;
	PhysicalOperator &PlanDelete(ClientContext &context, PhysicalPlanGenerator &planner, LogicalDelete &op,
	                             PhysicalOperator &plan) override;
	PhysicalOperator &PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner, LogicalUpdate &op,
	                             PhysicalOperator &plan) override;

	DatabaseSize GetDatabaseSize(ClientContext &context) override;
	bool InMemory() override;
	string GetDBPath() override;
	void DropSchema(ClientContext &context, DropInfo &info) override;

private:
	ArrowNetHandle handle_;
	string db_path_;
	//! Catalog visibility filters (icase regex, substring match); empty => match all.
	string schema_filter_;
	string table_filter_;
	mutex schema_lock_;
	case_insensitive_map_t<unique_ptr<ArrowNetSchemaEntry>> schemas_;
};

} // namespace duckdb
