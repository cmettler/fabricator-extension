//===----------------------------------------------------------------------===//
//                  arrownet — DELETE / UPDATE physical operators (impl)
//
// Provider-agnostic: the operators serialize the key columns (DELETE) or the
// SET values + key columns (UPDATE) to Arrow and hand them to the bridge. The
// C# provider generates the parameterized DELETE/UPDATE. No T-SQL here.
//===----------------------------------------------------------------------===//

#include "dml/arrownet_modify.hpp"

#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/types/vector.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"

namespace duckdb {

// Shared sink state: an Arrow producer over the columns we send to the bridge —
// [set columns...] (UPDATE only) followed by [key columns...].
class ArrowNetModifyGlobalState : public GlobalSinkState {
public:
	ArrowNetModifyGlobalState(ClientContext &context, const ArrowNetModifyTarget &target, bool is_update) {
		if (is_update) {
			for (idx_t i = 0; i < target.set_columns.size(); i++) {
				names.push_back(target.set_columns[i]);
				types.push_back(target.set_types[i]);
			}
			set_count = target.set_columns.size();
		}
		for (idx_t i = 0; i < target.rowid_columns.size(); i++) {
			names.push_back(target.rowid_columns[i]);
			types.push_back(target.rowid_types[i]);
		}
		key_count = target.rowid_columns.size();
		properties = context.GetClientProperties();
		extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, types);
		producer = make_uniq<arrownet::ArrowProducer>(types, names, properties);
	}

	vector<LogicalType> types;
	vector<string> names;
	idx_t set_count = 0;
	idx_t key_count = 0;
	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<arrownet::ArrowProducer> producer;
	idx_t total = 0;
	bool returned = false;
	mutable mutex lock;
};

class ArrowNetModifyLocalState : public LocalSinkState {};

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
static void AppendModifyBatch(ArrowNetModifyGlobalState &gstate, DataChunk &chunk, bool is_update) {
	idx_t rowid_col = chunk.ColumnCount() - 1; // rowid is the last child column
	DataChunk produce;
	produce.InitializeEmpty(gstate.types);
	produce.SetCardinality(chunk.size());
	if (is_update) {
		for (idx_t j = 0; j < gstate.set_count; j++) {
			produce.data[j].Reference(chunk.data[j]);
		}
	}
	ReferenceKeyColumns(produce, gstate.set_count, chunk, rowid_col, gstate.key_count);

	ArrowAppender appender(gstate.types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(produce, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();

	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->AddBatch(array);
}

static SourceResultType EmitCount(GlobalSinkState &sink_state, DataChunk &chunk) {
	auto &gstate = sink_state.Cast<ArrowNetModifyGlobalState>();
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
ArrowNetPhysicalDelete::ArrowNetPhysicalDelete(PhysicalPlan &plan, vector<LogicalType> types,
                                               idx_t estimated_cardinality, ArrowNetModifyTarget target,
                                               ArrowNetHandle handle)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      target_(std::move(target)), handle_(handle) {
}

unique_ptr<GlobalSinkState> ArrowNetPhysicalDelete::GetGlobalSinkState(ClientContext &context) const {
	return make_uniq<ArrowNetModifyGlobalState>(context, target_, /*is_update=*/false);
}
unique_ptr<LocalSinkState> ArrowNetPhysicalDelete::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<ArrowNetModifyLocalState>();
}
SinkResultType ArrowNetPhysicalDelete::Sink(ExecutionContext &context, DataChunk &chunk,
                                            OperatorSinkInput &input) const {
	if (chunk.size() > 0) {
		AppendModifyBatch(input.global_state.Cast<ArrowNetModifyGlobalState>(), chunk, /*is_update=*/false);
	}
	return SinkResultType::NEED_MORE_INPUT;
}
SinkCombineResultType ArrowNetPhysicalDelete::Combine(ExecutionContext &context,
                                                      OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}
SinkFinalizeType ArrowNetPhysicalDelete::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                  OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<ArrowNetModifyGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	gstate.total = (idx_t)arrownet::ExecuteDelete(handle_, target_.schema_name, target_.table_name,
	                                              *gstate.producer->Stream());
	return SinkFinalizeType::READY;
}
SourceResultType ArrowNetPhysicalDelete::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                         OperatorSourceInput &input) const {
	return EmitCount(*sink_state, chunk);
}

//===----------------------------------------------------------------------===//
// UPDATE
//===----------------------------------------------------------------------===//
ArrowNetPhysicalUpdate::ArrowNetPhysicalUpdate(PhysicalPlan &plan, vector<LogicalType> types,
                                               idx_t estimated_cardinality, ArrowNetModifyTarget target,
                                               ArrowNetHandle handle)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      target_(std::move(target)), handle_(handle) {
}

unique_ptr<GlobalSinkState> ArrowNetPhysicalUpdate::GetGlobalSinkState(ClientContext &context) const {
	return make_uniq<ArrowNetModifyGlobalState>(context, target_, /*is_update=*/true);
}
unique_ptr<LocalSinkState> ArrowNetPhysicalUpdate::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<ArrowNetModifyLocalState>();
}
SinkResultType ArrowNetPhysicalUpdate::Sink(ExecutionContext &context, DataChunk &chunk,
                                            OperatorSinkInput &input) const {
	if (chunk.size() > 0) {
		AppendModifyBatch(input.global_state.Cast<ArrowNetModifyGlobalState>(), chunk, /*is_update=*/true);
	}
	return SinkResultType::NEED_MORE_INPUT;
}
SinkCombineResultType ArrowNetPhysicalUpdate::Combine(ExecutionContext &context,
                                                      OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}
SinkFinalizeType ArrowNetPhysicalUpdate::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                  OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<ArrowNetModifyGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	gstate.total = (idx_t)arrownet::ExecuteUpdate(handle_, target_.schema_name, target_.table_name,
	                                              (int32_t)gstate.set_count, *gstate.producer->Stream());
	return SinkFinalizeType::READY;
}
SourceResultType ArrowNetPhysicalUpdate::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                         OperatorSourceInput &input) const {
	return EmitCount(*sink_state, chunk);
}

} // namespace duckdb
