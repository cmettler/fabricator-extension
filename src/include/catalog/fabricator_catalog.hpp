// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — catalog
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
#include <atomic>
#include "duckdb/catalog/catalog.hpp"
#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/mutex.hpp"

namespace duckdb {

class FabricatorSchemaEntry;

//! Read-only catalog backed by a SQL Server connection (via the C# bridge).
//! Owns the bridge catalog handle for its lifetime.
class FabricatorCatalog : public Catalog {
public:
	FabricatorCatalog(AttachedDatabase &db, string internal_name, FabricatorHandle handle, string db_path);
	~FabricatorCatalog() override;

	//! Discovers schemas + tables from SQL Server (called once at attach time).
	//! schema_filter / table_filter (ATTACH options) are applied provider-side now (catalog discovery returns
	//! only matches — docs/provider-extensibility.md §3), so this registers everything it discovers.
	void LoadCatalog(ClientContext &context);

	//! Re-discovers schemas + tables and drops cached entries (so out-of-band DDL
	//! via fabricator_exec becomes visible). Backs fabricator_refresh_cache().
	void RefreshCache(ClientContext &context);

	//! Drops every schema's MATERIALIZED entries (keeping the discovered name lists), so they lazily
	//! re-fetch from the committed server state. Context-free (callable from RollbackTransaction, which has
	//! none). Used on transaction ROLLBACK to discard entries an ALTER's eager re-fetch cached from the
	//! now-undone uncommitted schema. See FabricatorSchemaEntry::Alter / fabricator_transaction.cpp.
	void InvalidateAllEntries();

	//! Scoped invalidation: drop materialized entries whose name matches the icase regex `pattern`, keeping
	//! the discovered name lists (lazy re-fetch on next access). UNBOUNDED by the ATTACH object filter — a
	//! targeted refresh reaches any object by name (the filter bounds enumeration, not targeted access; see
	//! HasObjectFilter). Backs fabricator_invalidate_cache(catalog, name_regex). Throws on a bad regex.
	void InvalidateMatching(const string &pattern);

	FabricatorHandle GetHandle() const {
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

	//! Whether a pushed ORDER BY key may carry ANY resolved NULL placement, because the provider renders it
	//! (`NULLS FIRST` / `NULLS LAST`) rather than owning a fixed convention. False keeps
	//! `fabricator_optimizer.cpp`'s `NullOrderCompatible` gate, which encodes SQL Server's built-in order
	//! (NULLs first for ASC, last for DESC) — T-SQL cannot spell the other one. True for the Delta reader,
	//! whose ORDER BY is executed BY DuckDB. Without it the COMMON shape does not push at all: DuckDB's
	//! default null order is NULLS LAST for ASC, so `ORDER BY x LIMIT n` on a NULLABLE column is declined.
	bool NullOrderExpressible() const {
		return null_order_expressible_;
	}

	//! Whether an ATTACH object filter (schema_filter / table_filter / function_filter) is active. When set,
	//! the discovered name list (per schema) is a FILTERED SUBSET, so a targeted lookup miss is ambiguous —
	//! it may be a real object the filter merely excluded from ENUMERATION. The filter bounds enumeration
	//! (SHOW TABLES / full refresh), NOT targeted-by-name access: FabricatorSchemaEntry::GetOrCreateEntry
	//! lazily fetches an out-of-enumeration table by name when this is true (cached in entries_, never added
	//! to the enumerated set). With no filter, the discovery list is authoritative so a miss is genuinely
	//! absent (no wasted round-trip). Set at ATTACH from the presence of a filter option.
	void SetObjectFilter(bool has_filter) {
		has_object_filter_ = has_filter;
	}
	bool HasObjectFilter() const {
		return has_object_filter_;
	}

	//! Records that a provider function's EXECUTION performed DDL (ABI v81's `tablefn_execute`
	//! schema_may_change out-flag), so this catalog's metadata cache must be rebuilt before anything binds
	//! against it again.
	//!
	//! ⚠⚠ IT IS DELIBERATELY NOT ACTED ON WHERE IT IS SET. The flag is raised during a SCAN, and
	//! RefreshCache() calls ClearTables() on every schema — which would RETIRE the very entry the running
	//! statement is scanning. The graveyard makes that non-fatal rather than a use-after-free, but it is
	//! needless risk plus a burst of discovery IO in the middle of someone's query. So the refresh happens at
	//! the next FabricatorTransactionManager::StartTransaction, which is provably outside any bind or scan:
	//! DuckDB resolves the CatalogTransaction before LookupSchema takes schema_lock_, so nothing this thread
	//! holds can deadlock against it.
	//!
	//! ⚠ Why a deferred rebuild is needed at all rather than nothing: with no ATTACH object filter, a name
	//! missing from the discovered list is treated as GENUINELY ABSENT (FabricatorSchemaEntry's lookup gate),
	//! so an object a function just created is not merely un-enumerated — it is UNREACHABLE, with no by-name
	//! fallback. MEASURED: `cdc.enable` then reading the change table it created gives "Table with name
	//! dbo_x_CT does not exist!" in the same session.
	void MarkSchemaMayChange() {
		schema_may_change_ = true;
	}

	//! Reads and clears the flag. Atomic exchange because a scan on a DuckDB worker thread may set it while
	//! another thread starts a transaction; at worst the refresh lands one statement later, never twice.
	bool ConsumeSchemaMayChange() {
		return schema_may_change_.exchange(false);
	}

	//! The catalog-type string identifying an attached catalog as ours (the provider
	//! identity — becomes generic in the multi-provider rename). Centralized so the
	//! "is this our catalog?" checks don't repeat the literal.
	static constexpr const char *CATALOG_TYPE = "fabricator";

	//! True if `catalog` is one of our attached catalogs.
	static bool Is(Catalog &catalog) {
		return catalog.GetCatalogType() == CATALOG_TYPE;
	}

	void Initialize(bool load_builtin) override;
	string GetCatalogType() override;

	//! Time travel: SQL Server temporal (system-versioned) tables support FOR SYSTEM_TIME AS OF, which the
	//! table scan maps the DuckDB `AT (...)` clause to. Without this the binder rejects `FROM t AT (...)`
	//! with "Catalog type does not support time travel" before reaching FabricatorTableEntry::GetScanFunction.
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

	//! Tell every fabricator scan in `plan` that reads THIS catalog WHICH SINK it shares the plan with,
	//! so the PROVIDER can decide whether that costs anything. Same-catalog only — a scan of a DIFFERENT
	//! catalog cannot collide with this sink's connection and keeps its pipelining.
	//! ⚠ The host states a FACT ("your catalog is also this plan's sink, and the sink is <s>.<t>"), never
	//! a verdict: whether the scan must be drained depends on how the backend will WRITE, which only the
	//! backend knows. See ArrowStreamBindData::sink_table for the measurement that forced the split, and
	//! for why marking at plan time is safe.
	void MarkSinkOnOwnScans(PhysicalOperator &plan, const string &sink_schema, const string &sink_table,
	                        const string &sink_kind);

	PhysicalOperator &PlanCreateTableAs(ClientContext &context, PhysicalPlanGenerator &planner,
	                                    LogicalCreateTable &op, PhysicalOperator &plan) override;
	PhysicalOperator &PlanInsert(ClientContext &context, PhysicalPlanGenerator &planner, LogicalInsert &op,
	                             optional_ptr<PhysicalOperator> plan) override;
	PhysicalOperator &PlanDelete(ClientContext &context, PhysicalPlanGenerator &planner, LogicalDelete &op,
	                             PhysicalOperator &plan) override;
	PhysicalOperator &PlanUpdate(ClientContext &context, PhysicalPlanGenerator &planner, LogicalUpdate &op,
	                             PhysicalOperator &plan) override;
	//! MERGE INTO — and, since the binder rewrites `INSERT ... ON CONFLICT` into a MERGE, that too. The base
	//! Catalog's body is the "does not support MERGE INTO or ON CONFLICT" throw, so overriding it is the
	//! whole gate. See src/catalog/fabricator_merge_into.cpp.
	PhysicalOperator &PlanMergeInto(ClientContext &context, PhysicalPlanGenerator &planner, LogicalMergeInto &op,
	                                PhysicalOperator &plan) override;

	DatabaseSize GetDatabaseSize(ClientContext &context) override;
	bool InMemory() override;
	string GetDBPath() override;
	void DropSchema(ClientContext &context, DropInfo &info) override;

private:
	FabricatorHandle handle_;
	string db_path_;
	//! Whether the database collation is binary (detected at LoadCatalog) => string ORDER BY is pushable.
	bool has_object_filter_ = false;
	//! Set by MarkSchemaMayChange (a provider function did DDL); consumed at the next transaction start.
	std::atomic<bool> schema_may_change_ {false};
	bool string_order_pushable_ = false;
	bool null_order_expressible_ = false;
	//! Whether the provider applies pushed filters exactly (detected at LoadCatalog) => filter_pushdown=true
	//! is safe on the scan (currently: Delta native_read only). See ExactFilterPushdown().
	bool exact_filter_pushdown_ = false;
	mutex schema_lock_;
	case_insensitive_map_t<unique_ptr<FabricatorSchemaEntry>> schemas_;
	// GRAVEYARD: evicted schema entries are RETIRED, never destroyed mid-session — binders hold raw
	// pointers across the lock, AND every (possibly retired) table entry's ParentSchema() is a reference
	// into its FabricatorSchemaEntry, so schemas must outlive their entries. Freed at catalog teardown.
	// Guarded by schema_lock_. (Same use-after-free class as the schema-entry graveyard; see
	// fabricator_schema_entry.hpp.)
	vector<unique_ptr<FabricatorSchemaEntry>> retired_schemas_;
};

} // namespace duckdb
