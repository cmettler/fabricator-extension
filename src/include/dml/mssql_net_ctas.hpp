//===----------------------------------------------------------------------===//
//                         mssql_net — CREATE TABLE AS (CTAS) operator
//===----------------------------------------------------------------------===//

#pragma once

#include "arrownet/abi.h"
#include "duckdb/common/optional_ptr.hpp"
#include "duckdb/execution/physical_operator.hpp"

namespace duckdb {

class MssqlNetSchemaEntry;

struct MssqlNetCtasInfo {
	string schema_name;
	string table_name;
	vector<string> column_names;
	vector<LogicalType> column_types;
	bool replace = false;
	ArrowNetHandle handle = nullptr;
	//! Schema entry to register the new table in (so it appears in the catalog).
	optional_ptr<MssqlNetSchemaEntry> schema_entry;
};

//! CREATE TABLE [schema].[table] AS SELECT ... — streams the SELECT result as
//! Arrow to the bridge, which creates the table (mapping Arrow types) and bulk
//! loads it. Returns the inserted row count.
class MssqlNetPhysicalCreateTableAs : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	MssqlNetPhysicalCreateTableAs(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                              MssqlNetCtasInfo info);

	string GetName() const override {
		return "MSSQL_NET_CREATE_TABLE_AS";
	}
	bool IsSink() const override {
		return true;
	}
	bool IsSource() const override {
		return true;
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
	MssqlNetCtasInfo info_;
};

} // namespace duckdb
