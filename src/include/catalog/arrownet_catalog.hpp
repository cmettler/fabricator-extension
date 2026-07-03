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

	//! Discovers schemas + tables from SQL Server (called once at attach time).
	//! schema_filter / table_filter (ATTACH options) are applied provider-side now (get_metadata returns
	//! only matches — docs/provider-extensibility.md §3), so this registers everything it discovers.
	void LoadCatalog(ClientContext &context);

	//! Re-discovers schemas + tables and drops cached entries (so out-of-band DDL
	//! via mssql_net_exec becomes visible). Backs mssql_refresh_cache().
	void RefreshCache(ClientContext &context);

	//! Drops every schema's MATERIALIZED entries (keeping the discovered name lists), so they lazily
	//! re-fetch from the committed server state. Context-free (callable from RollbackTransaction, which has
	//! none). Used on transaction ROLLBACK to discard entries an ALTER's eager re-fetch cached from the
	//! now-undone uncommitted schema. See ArrowNetSchemaEntry::Alter / arrownet_transaction.cpp.
	void InvalidateAllEntries();

	ArrowNetHandle GetHandle() const {
		return handle_;
	}

	//! Whether string-keyed ORDER BY+LIMIT can be pushed to SQL Server: true only when the database
	//! collation is binary (_BIN/_BIN2), so the server's byte-order string sort matches DuckDB. Detected
	//! once at LoadCatalog from the server profile; read at scan bind onto the scan's bind_data.
	bool StringOrderPushable() const {
		return string_order_pushable_;
	}

	//! Whether the host may set `filter_pushdown = true` on this catalog's table scan — i.e. whether the
	//! provider applies pushed table filters EXACTLY (not merely as a superset prune). True only for the
	//! Delta `native_read` catalog, where every scan routes through DuckDB's own `read_parquet` and the
	//! pushed WHERE is 1:1 (same engine). Enables receiving DuckDB's runtime dynamic (join) filters, whose
	//! delivery is gated on `filter_pushdown`. Detected once at LoadCatalog from the provider profile;
	//! default false keeps the safe superset-and-DuckDB-re-applies model for SQL Server / DAX / non-native
	//! Delta. See docs/multifile-delta.md §"Batch 2 slice 2".
	bool ExactFilterPushdown() const {
		return exact_filter_pushdown_;
	}

	//! The catalog-type string identifying an attached catalog as ours (the provider
	//! identity — becomes generic in the multi-provider rename). Centralized so the
	//! "is this our catalog?" checks don't repeat the literal.
	static constexpr const char *CATALOG_TYPE = "mssql_net";

	//! True if `catalog` is one of our attached catalogs.
	static bool Is(Catalog &catalog) {
		return catalog.GetCatalogType() == CATALOG_TYPE;
	}

	void Initialize(bool load_builtin) override;
	string GetCatalogType() override;

	//! Time travel: SQL Server temporal (system-versioned) tables support FOR SYSTEM_TIME AS OF, which the
	//! table scan maps the DuckDB `AT (...)` clause to. Without this the binder rejects `FROM t AT (...)`
	//! with "Catalog type does not support time travel" before reaching ArrowNetTableEntry::GetScanFunction.
	bool SupportsTimeTravel() const override {
		return true;
	}

	//! Allow CREATE TABLE [AS] ... PARTITIONED BY (cols): the base Catalog rejects any partition_keys, so we
	//! override to permit them (a partitioning provider — Delta — lays out the data by partition; SQL Server /
	//! DAX ignore the columns). SORTED BY and the WITH-options clause stay unsupported.
	ErrorData SupportsCreateTable(BoundCreateTableInfo &info) override;

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
	//! Whether the database collation is binary (detected at LoadCatalog) => string ORDER BY is pushable.
	bool string_order_pushable_ = false;
	//! Whether the provider applies pushed filters exactly (detected at LoadCatalog) => filter_pushdown=true
	//! is safe on the scan (currently: Delta native_read only). See ExactFilterPushdown().
	bool exact_filter_pushdown_ = false;
	mutex schema_lock_;
	case_insensitive_map_t<unique_ptr<ArrowNetSchemaEntry>> schemas_;
};

} // namespace duckdb
