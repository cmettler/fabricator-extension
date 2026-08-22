//===----------------------------------------------------------------------===//
//                  fabricator — DELETE / UPDATE physical operators (impl)
//
// Provider-agnostic: the operators serialize the key columns (DELETE) or the
// SET values + key columns (UPDATE) to Arrow and hand them to the bridge. The
// C# provider generates the parameterized DELETE/UPDATE. No T-SQL here.
//===----------------------------------------------------------------------===//

#include "dml/fabricator_modify.hpp"

#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/types/vector.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"

namespace duckdb {

// Shared sink state: an Arrow producer over the columns we send to the bridge —
// [set columns...] (UPDATE only) followed by [key columns...].
class FabricatorModifyGlobalState : public GlobalSinkState {
public:
	FabricatorModifyGlobalState(ClientContext &context, const FabricatorModifyTarget &target, bool is_update) {
		if (is_update) {
			for (idx_t i = 0; i < target.set_columns.size(); i++) {
				names.push_back(target.set_columns[i]);
				types.push_back(target.set_types[i]);
			}
			set_count = target.set_columns.size();
			set_child_indices = target.set_child_indices;
		}
		for (idx_t i = 0; i < target.rowid_columns.size(); i++) {
			names.push_back(target.rowid_columns[i]);
			types.push_back(target.rowid_types[i]);
		}
		key_count = target.rowid_columns.size();
		rowid_child_index = target.rowid_child_index;
		properties = fabricator::BoundaryClientProperties(context);
		extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, types);
		producer = make_uniq<fabricator::ArrowProducer>(types, names, properties);
	}

	vector<LogicalType> types;
	vector<string> names;
	idx_t set_count = 0;
	idx_t key_count = 0;
	//! UPDATE: the child-chunk position of each SET value (see FabricatorModifyTarget). Empty on the
	//! DELETE path.
	vector<idx_t> set_child_indices;
	//! The rowid's position in the child chunk (INVALID_INDEX = last column — a plain UPDATE's
	//! binder-built projection puts the rowid last).
	idx_t rowid_child_index = DConstants::INVALID_INDEX;
	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<fabricator::ArrowProducer> producer;
	idx_t total = 0;
	bool returned = false;
	mutable mutex lock;
};

class FabricatorModifyLocalState : public LocalSinkState {};

// A MERGE with >=2 mutating actions must run BUFFERED even in autocommit, and this is a CORRECTNESS
// requirement rather than an atomicity nicety. Every action consumes rowids captured from the merge's ONE
// join scan; if the actions commit separately, a copy-on-write DELETE removes a row and RENUMBERS every
// later row, so a subsequent action's captured (fileOrdinal, position) names a DIFFERENT row. Measured
// before this: two conditional deletes on a one-file table deleted a row that should have survived and
// left the one that should have gone. Marking the transaction buffered stages every action against ONE
// pinned snapshot, so no action can renumber another's targets, and fuses them into one commit.
//
// Called from GetGlobalSinkState — i.e. at EXECUTION time, and BEFORE any action does provider work.
// PhysicalMergeInto builds every action's global sink state up front, so whichever action runs first sets
// the flag and the rest observe it (the INSERT's own begin_bulk therefore sees it and buffers too).
// Deliberately NOT done at plan time: a prepared statement's physical plan is reused across transactions.
static void FabricatorForceBufferedTxn(FabricatorHandle handle, ClientContext &context) {
	FabricatorSetActiveTxn(handle, context);
	fabricator::BeginTransaction(handle, /*is_explicit=*/true);
}


// References the rowid column's key vector(s) into `out` starting at out_offset.
static void ReferenceKeyColumns(DataChunk &out, idx_t out_offset, DataChunk &src, idx_t rowid_col, idx_t key_count) {
	if (key_count == 1) {
		out.data[out_offset].Reference(src.data[rowid_col]);
	} else {
		auto &entries = StructVector::GetEntries(src.data[rowid_col]);
		for (idx_t i = 0; i < key_count; i++) {
			out.data[out_offset + i].Reference(*entries[i]);
		}
	}
}

// Builds an ArrowArray from a column layout that references the source chunk,
// and enqueues it on the producer.
static void AppendModifyBatch(FabricatorModifyGlobalState &gstate, DataChunk &chunk, bool is_update) {
	// DELETE carries the rowid's actual child-chunk position (a mark-join plan's chunk ends with the
	// BOOLEAN mark, not the rowid); a plain UPDATE keeps the last-column contract (binder projection).
	idx_t rowid_col = gstate.rowid_child_index != DConstants::INVALID_INDEX ? gstate.rowid_child_index
	                                                                        : chunk.ColumnCount() - 1;
	DataChunk produce;
	produce.InitializeEmpty(gstate.types);
	produce.SetCardinality(chunk.size());
	if (is_update) {
		// Each SET value is read from the position the BOUND_REF named, NOT from `j`: a `SET x = DEFAULT`
		// puts no column in the projection (so everything after it shifts), and a MERGE's UPDATE action
		// shares one projection with every other action.
		D_ASSERT(gstate.set_child_indices.size() == gstate.set_count);
		for (idx_t j = 0; j < gstate.set_count; j++) {
			produce.data[j].Reference(chunk.data[gstate.set_child_indices[j]]);
		}
	}
	ReferenceKeyColumns(produce, gstate.set_count, chunk, rowid_col, gstate.key_count);

	ArrowAppender appender(gstate.types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(produce, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();

	// ⚠ THIS LOCK IS NOW LOAD-BEARING RATHER THAN DEFENSIVE. Both modify operators declare ParallelSink()
	// true when the plan carries no explicit ordering, so several DuckDB tasks reach this line at once; the
	// appender above is per-call and the producer is the only shared thing touched. It was already written
	// this way, which is why the flag was a one-line change — but do not "simplify" it back.
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->AddBatch(array);
}

static SourceResultType EmitCount(GlobalSinkState &sink_state, DataChunk &chunk) {
	auto &gstate = sink_state.Cast<FabricatorModifyGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	if (gstate.returned) {
		return SourceResultType::FINISHED;
	}
	gstate.returned = true;
	chunk.SetCardinality(1);
	chunk.SetValue(0, 0, Value::BIGINT((int64_t)gstate.total));
	return SourceResultType::FINISHED;
}

//===----------------------------------------------------------------------===//
// DELETE
//===----------------------------------------------------------------------===//
FabricatorPhysicalDelete::FabricatorPhysicalDelete(PhysicalPlan &plan, vector<LogicalType> types,
                                               idx_t estimated_cardinality, FabricatorModifyTarget target,
                                               FabricatorHandle handle)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      target_(std::move(target)), handle_(handle) {
}

unique_ptr<GlobalSinkState> FabricatorPhysicalDelete::GetGlobalSinkState(ClientContext &context) const {
	if (target_.force_buffered) {
		FabricatorForceBufferedTxn(handle_, context);
	}
	return make_uniq<FabricatorModifyGlobalState>(context, target_, /*is_update=*/false);
}
unique_ptr<LocalSinkState> FabricatorPhysicalDelete::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<FabricatorModifyLocalState>();
}
SinkResultType FabricatorPhysicalDelete::Sink(ExecutionContext &context, DataChunk &chunk,
                                            OperatorSinkInput &input) const {
	if (chunk.size() > 0) {
		AppendModifyBatch(input.global_state.Cast<FabricatorModifyGlobalState>(), chunk, /*is_update=*/false);
	}
	return SinkResultType::NEED_MORE_INPUT;
}
SinkCombineResultType FabricatorPhysicalDelete::Combine(ExecutionContext &context,
                                                      OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}
SinkFinalizeType FabricatorPhysicalDelete::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                  OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<FabricatorModifyGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	FabricatorSetActiveTxn(handle_, context);
	gstate.total = (idx_t)fabricator::ExecuteDelete(handle_, target_.schema_name, target_.table_name,
	                                              *gstate.producer->Stream());
	return SinkFinalizeType::READY;
}
SourceResultType FabricatorPhysicalDelete::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                         OperatorSourceInput &input) const {
	return EmitCount(*sink_state, chunk);
}

//===----------------------------------------------------------------------===//
// UPDATE
//===----------------------------------------------------------------------===//
FabricatorPhysicalUpdate::FabricatorPhysicalUpdate(PhysicalPlan &plan, vector<LogicalType> types,
                                               idx_t estimated_cardinality, FabricatorModifyTarget target,
                                               FabricatorHandle handle)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      target_(std::move(target)), handle_(handle) {
}

unique_ptr<GlobalSinkState> FabricatorPhysicalUpdate::GetGlobalSinkState(ClientContext &context) const {
	if (target_.force_buffered) {
		FabricatorForceBufferedTxn(handle_, context);
	}
	return make_uniq<FabricatorModifyGlobalState>(context, target_, /*is_update=*/true);
}
unique_ptr<LocalSinkState> FabricatorPhysicalUpdate::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<FabricatorModifyLocalState>();
}
SinkResultType FabricatorPhysicalUpdate::Sink(ExecutionContext &context, DataChunk &chunk,
                                            OperatorSinkInput &input) const {
	if (chunk.size() > 0) {
		AppendModifyBatch(input.global_state.Cast<FabricatorModifyGlobalState>(), chunk, /*is_update=*/true);
	}
	return SinkResultType::NEED_MORE_INPUT;
}
SinkCombineResultType FabricatorPhysicalUpdate::Combine(ExecutionContext &context,
                                                      OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}
SinkFinalizeType FabricatorPhysicalUpdate::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                  OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<FabricatorModifyGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	FabricatorSetActiveTxn(handle_, context);
	gstate.total = (idx_t)fabricator::ExecuteUpdate(handle_, target_.schema_name, target_.table_name,
	                                              (int32_t)gstate.set_count, *gstate.producer->Stream());
	return SinkFinalizeType::READY;
}
SourceResultType FabricatorPhysicalUpdate::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                         OperatorSourceInput &input) const {
	return EmitCount(*sink_state, chunk);
}

} // namespace duckdb
