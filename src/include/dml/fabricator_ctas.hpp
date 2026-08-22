//===----------------------------------------------------------------------===//
//                         fabricator — CREATE TABLE AS (CTAS) operator
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
#include "duckdb/common/optional_ptr.hpp"
#include "duckdb/execution/physical_operator.hpp"

namespace duckdb {

class FabricatorSchemaEntry;

struct FabricatorCtasInfo {
	string schema_name;
	string table_name;
	vector<string> column_names;
	vector<LogicalType> column_types;
	bool replace = false;
	//! Comma-separated column names from a native PARTITIONED BY clause (empty if none). Passed to begin_bulk so
	//! a partitioning provider (Delta) lays out the CTAS result by partition; SQL Server / DAX ignore it.
	string partition_columns;
	//! Comma-separated column names from a native SORTED BY clause (empty if none). The SQL Server provider maps
	//! them to a Fabric Warehouse WITH (CLUSTER BY (cols)) layout on the created table; Delta / DAX ignore it.
	string sort_columns;
	//! The CREATE TABLE AS ... WITH (key='value', ...) options clause as a flat JSON object (empty if none).
	//! Passed to begin_bulk (v67); the provider parses the keys it knows and rejects unknown ones.
	string options_json;
	FabricatorHandle handle = nullptr;
	//! Schema entry to register the new table in (so it appears in the catalog).
	optional_ptr<FabricatorSchemaEntry> schema_entry;
	//! Whether the sink may run on SEVERAL tasks at once — see FabricatorInsertTarget::parallel for why this
	//! governs the whole pipeline rather than only the sink. Decided in FabricatorCatalog::PlanCreateTableAs.
	bool parallel = false;
};

//! CREATE TABLE [schema].[table] AS SELECT ... — streams the SELECT result as
//! Arrow to the bridge, which creates the table (mapping Arrow types) and bulk
//! loads it. Returns the inserted row count.
class FabricatorPhysicalCreateTableAs : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	FabricatorPhysicalCreateTableAs(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                              FabricatorCtasInfo info);

	string GetName() const override {
		return "MSSQL_NET_CREATE_TABLE_AS";
	}
	bool IsSink() const override {
		return true;
	}
	bool IsSource() const override {
		return true;
	}
	bool ParallelSink() const override {
		return info_.parallel;
	}

	SinkResultType Sink(ExecutionContext &context, DataChunk &chunk, OperatorSinkInput &input) const override;
	SinkCombineResultType Combine(ExecutionContext &context, OperatorSinkCombineInput &input) const override;
	SinkFinalizeType Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
	                          OperatorSinkFinalizeInput &input) const override;
	unique_ptr<GlobalSinkState> GetGlobalSinkState(ClientContext &context) const override;
	unique_ptr<LocalSinkState> GetLocalSinkState(ExecutionContext &context) const override;
	SourceResultType GetDataInternal(ExecutionContext &context, DataChunk &chunk,
	                                 OperatorSourceInput &input) const override;

private:
	FabricatorCtasInfo info_;
};

} // namespace duckdb
