//===----------------------------------------------------------------------===//
//                         fabricator — DELETE / UPDATE physical operators
//===----------------------------------------------------------------------===//

#pragma once

#include "fabricator/abi.h"
#include "duckdb/common/mutex.hpp"
#include "duckdb/common/index_vector.hpp"
#include "duckdb/execution/physical_operator.hpp"

namespace duckdb {

class TableCatalogEntry;
struct FabricatorModifyTarget;

// ⚠ These two live in `duckdb`, NOT in a nested `duckdb::fabricator`. Declaring a `fabricator` namespace
// inside `duckdb` would SHADOW the extension's global `::fabricator` core namespace for every translation
// unit that includes this header: unqualified `fabricator::X` inside `namespace duckdb` would then resolve
// to `duckdb::fabricator` and stop, so every existing `fabricator::PartitionColumnsArg` /
// `BoundaryClientProperties` / `ExecuteDelete` call would fail to compile with "is not a member of
// duckdb::fabricator". They take duckdb types anyway, so `duckdb` is where they belong.

//! Resolves a table's row identity into the target's key columns. Throws a BinderException naming the
//! table when it has none (no PK / unique index / IDENTITY, and no provider virtual rowid).
FabricatorModifyTarget FabricatorBuildModifyTarget(TableCatalogEntry &table);

//! Fills the SET half of an UPDATE target from the bound assignment expressions. Shared by a plain
//! UPDATE and a MERGE's WHEN MATCHED THEN UPDATE so the two cannot drift on where a value comes from.
void FabricatorFillUpdateSetColumns(TableCatalogEntry &table, const vector<PhysicalIndex> &columns,
                                    const vector<unique_ptr<Expression>> &expressions,
                                    FabricatorModifyTarget &target);

//! Shared target metadata for rowid-based DELETE/UPDATE.
struct FabricatorModifyTarget {
	string schema_name;
	string table_name;
	//! rowid/PK column names + types, in key order (1 = scalar rowid, >1 = compound).
	vector<string> rowid_columns;
	vector<LogicalType> rowid_types;
	//! For UPDATE: the columns being SET (names + types), in `LogicalUpdate::columns` order.
	vector<string> set_columns;
	vector<LogicalType> set_types;
	//! For UPDATE: where each SET value lives in the child chunk, from LogicalUpdate::expressions
	//! (each a BOUND_REF into the binder's projection — upstream PhysicalUpdate reads them the same
	//! way). NOT positional: `SET x = DEFAULT` contributes NO projection column, so every later SET
	//! value shifts; and a MERGE's WHEN MATCHED THEN UPDATE shares one projection with every other
	//! action, so its SET values sit at arbitrary positions.
	vector<idx_t> set_child_indices;
	//! The rowid column's position in the child chunk. DELETE: from LogicalDelete::expressions[0]
	//! (the bound row-identifier reference — DuckDB's own PhysicalDelete reads it the same way).
	//! MERGE: LogicalMergeInto::row_id_start. INVALID_INDEX = fall back to the last column (a plain
	//! UPDATE keeps that: the binder builds its child projection with the rowid last, matching
	//! upstream PhysicalUpdate's assumption). A mark-join DELETE plan (WHERE x [NOT] IN (subquery))
	//! has NO projection between FILTER and DELETE, so the child chunk ends with the BOOLEAN mark —
	//! "last column" would reference the mark as the rowid.
	idx_t rowid_child_index = DConstants::INVALID_INDEX;
	//! MERGE with >=2 mutating actions: mark the statement's transaction BUFFERED at execution time, even in
	//! autocommit. Load-bearing for CORRECTNESS, not just atomicity — every action of a merge consumes rowids
	//! captured from ONE join scan, so if the actions commit separately a copy-on-write DELETE renumbers the
	//! rows a later action already addressed and that action hits the WRONG ROW (measured: two conditional
	//! deletes destroyed a row that should have survived). Buffering stages every action against one pinned
	//! snapshot, so positions cannot shift, and fuses them into one commit.
	//! Set at EXECUTION time (GetGlobalSinkState), never at plan time: a prepared statement's physical plan is
	//! reused across transactions, so a plan-time mark would apply to the first transaction only.
	bool force_buffered = false;
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
