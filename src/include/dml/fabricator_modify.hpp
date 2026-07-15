//===----------------------------------------------------------------------===//
//                         fabricator — DELETE / UPDATE physical operators
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
#include "duckdb/common/mutex.hpp"
#include "duckdb/execution/physical_operator.hpp"

namespace duckdb {

//! Shared target metadata for rowid-based DELETE/UPDATE.
struct FabricatorModifyTarget {
	string schema_name;
	string table_name;
	//! rowid/PK column names + types, in key order (1 = scalar rowid, >1 = compound).
	vector<string> rowid_columns;
	vector<LogicalType> rowid_types;
	//! For UPDATE: the columns being SET (names + types), in input-chunk order
	//! (the SET values precede the trailing rowid column).
	vector<string> set_columns;
	vector<LogicalType> set_types;
	//! DELETE: the rowid column's position in the child chunk, from LogicalDelete::expressions[0]
	//! (the bound row-identifier reference — DuckDB's own PhysicalDelete reads it the same way).
	//! INVALID_INDEX = fall back to the last column (UPDATE keeps that: the binder builds its child
	//! projection with the rowid last, matching upstream PhysicalUpdate's assumption). A mark-join
	//! DELETE plan (WHERE x [NOT] IN (subquery)) has NO projection between FILTER and DELETE, so the
	//! child chunk ends with the BOOLEAN mark — "last column" would reference the mark as the rowid.
	idx_t rowid_child_index = DConstants::INVALID_INDEX;
};

//! DELETE FROM [schema].[table] WHERE <rowid predicates>, batched.
class FabricatorPhysicalDelete : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	FabricatorPhysicalDelete(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                       FabricatorModifyTarget target, FabricatorHandle handle);

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
	FabricatorModifyTarget target_;
	FabricatorHandle handle_;
};

//! UPDATE [schema].[table] SET <cols> = <vals> WHERE <rowid predicate>, per-row batched.
class FabricatorPhysicalUpdate : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	FabricatorPhysicalUpdate(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                       FabricatorModifyTarget target, FabricatorHandle handle);

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
	FabricatorModifyTarget target_;
	FabricatorHandle handle_;
};

} // namespace duckdb
