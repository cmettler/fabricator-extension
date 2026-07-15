//===----------------------------------------------------------------------===//
//                         fabricator — INSERT physical operator
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
#include "duckdb/common/mutex.hpp"
#include "duckdb/execution/physical_operator.hpp"

namespace duckdb {

//! Target of an INSERT: qualified table + insert columns (names + types, in the
//! INSERT statement's source order).
struct FabricatorInsertTarget {
	string schema_name;
	string table_name;
	vector<string> columns;
	vector<LogicalType> column_types;
	//! INSERT ... RETURNING: the operator emits the inserted rows (all table
	//! columns) via OUTPUT INSERTED.* instead of a single rows-affected count.
	bool returning = false;
};

//! Physical INSERT into SQL Server. Streams input chunks to the bridge as Arrow
//! (generic) and the provider bulk-loads into the existing table via the same
//! path as CTAS/COPY (SqlBulkCopy). No T-SQL is generated in C++.
class FabricatorPhysicalInsert : public PhysicalOperator {
public:
	static constexpr const PhysicalOperatorType TYPE = PhysicalOperatorType::EXTENSION;

	FabricatorPhysicalInsert(PhysicalPlan &plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                       FabricatorInsertTarget target, FabricatorHandle handle);

	string GetName() const override {
		return "MSSQL_NET_INSERT";
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

	const FabricatorInsertTarget &GetTarget() const {
		return target_;
	}
	FabricatorHandle GetHandle() const {
		return handle_;
	}

private:
	FabricatorInsertTarget target_;
	FabricatorHandle handle_;
};

} // namespace duckdb
