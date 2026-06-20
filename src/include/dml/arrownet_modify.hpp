//===----------------------------------------------------------------------===//
//                         arrownet — DELETE / UPDATE physical operators
//===----------------------------------------------------------------------===//

#pragma once

#include "arrownet/abi.h"
#include "duckdb/common/mutex.hpp"
#include "duckdb/execution/physical_operator.hpp"

namespace duckdb {

//! Shared target metadata for rowid-based DELETE/UPDATE.
struct ArrowNetModifyTarget {
	string schema_name;
	string table_name;
	//! rowid/PK column names + types, in key order (1 = scalar rowid, >1 = compound).
	vector<string> rowid_columns;
	vector<LogicalType> rowid_types;
	//! For UPDATE: the columns being SET (names + types), in input-chunk order
	//! (the SET values precede the trailing rowid column).
	vector<string> set_columns;
	vector<LogicalType> set_types;
};

//! DELETE FROM [schema].[table] WHERE <rowid predicates>, batched.
class ArrowNetPhysicalDelete : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	ArrowNetPhysicalDelete(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                       ArrowNetModifyTarget target, ArrowNetHandle handle);

	string GetName() const override {
		return "MSSQL_NET_DELETE";
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
	ArrowNetModifyTarget target_;
	ArrowNetHandle handle_;
};

//! UPDATE [schema].[table] SET <cols> = <vals> WHERE <rowid predicate>, per-row batched.
class ArrowNetPhysicalUpdate : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	ArrowNetPhysicalUpdate(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                       ArrowNetModifyTarget target, ArrowNetHandle handle);

	string GetName() const override {
		return "MSSQL_NET_UPDATE";
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
	ArrowNetModifyTarget target_;
	ArrowNetHandle handle_;
};

} // namespace duckdb
