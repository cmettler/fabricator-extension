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
	//! MERGE with >=2 mutating actions: mark the statement's transaction BUFFERED at execution time, even in
	//! autocommit. Load-bearing for CORRECTNESS, not just atomicity — every action of a merge consumes rowids
	//! captured from ONE join scan, so if the actions commit separately a copy-on-write DELETE renumbers the
	//! rows a later action already addressed and that action hits the WRONG ROW (measured: two conditional
	//! deletes destroyed a row that should have survived). Buffering stages every action against one pinned
	//! snapshot, so positions cannot shift, and fuses them into one commit.
	//! Set at EXECUTION time (GetGlobalSinkState), never at plan time: a prepared statement's physical plan is
	//! reused across transactions, so a plan-time mark would apply to the first transaction only.
	bool force_buffered = false;
	//! Whether the sink may run on SEVERAL tasks at once. Decided at PLAN time (FabricatorCatalog::PlanInsert)
	//! and read by ParallelSink(), which DuckDB consults in Pipeline::ScheduleParallel BEFORE it looks at the
	//! source or MaxThreads() — so false serializes the WHOLE pipeline (the scan, every projection, all of it)
	//! onto one task, which is what made every write into a fabricator table flat in `SET threads`.
	//! Default FALSE so a construction site that has not thought about it keeps today's behaviour; the MERGE
	//! path builds this struct directly and deliberately keeps it (PhysicalMergeInto drives our operators
	//! manually and shares ONE global sink state across its actions).
	bool parallel = false;
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
	//! See FabricatorInsertTarget::parallel. RETURNING is never parallel (the OUTPUT rows would come back in
	//! an arbitrary order, and its accumulating producer is shared).
	bool ParallelSink() const override {
		return target_.parallel;
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
