//===----------------------------------------------------------------------===//
//                         mssql_net — CTAS operator (impl)
//===----------------------------------------------------------------------===//

#include "dml/mssql_net_ctas.hpp"

#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/mssql_net_schema_entry.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"

namespace duckdb {

class MssqlNetCtasGlobalState : public GlobalSinkState {
public:
	MssqlNetCtasGlobalState(ClientContext &context, const MssqlNetCtasInfo &info)
	    : properties(context.GetClientProperties()),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, info.column_types)) {
		producer = make_uniq<arrownet::ArrowProducer>(info.column_types, info.column_names, properties);
	}

	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<arrownet::ArrowProducer> producer;
	idx_t total = 0;
	bool returned = false;
	mutable mutex lock;
};

class MssqlNetCtasLocalState : public LocalSinkState {};

MssqlNetPhysicalCreateTableAs::MssqlNetPhysicalCreateTableAs(PhysicalPlan &plan, vector<LogicalType> types,
                                                             idx_t estimated_cardinality, MssqlNetCtasInfo info)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      info_(std::move(info)) {
}

unique_ptr<GlobalSinkState> MssqlNetPhysicalCreateTableAs::GetGlobalSinkState(ClientContext &context) const {
	return make_uniq<MssqlNetCtasGlobalState>(context, info_);
}

unique_ptr<LocalSinkState> MssqlNetPhysicalCreateTableAs::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<MssqlNetCtasLocalState>();
}

SinkResultType MssqlNetPhysicalCreateTableAs::Sink(ExecutionContext &context, DataChunk &chunk,
                                                   OperatorSinkInput &input) const {
	auto &gstate = input.global_state.Cast<MssqlNetCtasGlobalState>();
	if (chunk.size() == 0) {
		return SinkResultType::NEED_MORE_INPUT;
	}
	// Convert the chunk to an Arrow array and enqueue it on the producer stream.
	ArrowAppender appender(info_.column_types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(chunk, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();

	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->AddBatch(array);
	return SinkResultType::NEED_MORE_INPUT;
}

SinkCombineResultType MssqlNetPhysicalCreateTableAs::Combine(ExecutionContext &context,
                                                             OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}

SinkFinalizeType MssqlNetPhysicalCreateTableAs::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                         OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<MssqlNetCtasGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	gstate.total = (idx_t)arrownet::BulkInsert(info_.handle, info_.schema_name, info_.table_name,
	                                           /*create_table=*/true, info_.replace, *gstate.producer->Stream());
	// Make the new table visible in the attached catalog for this session.
	if (info_.schema_entry) {
		const_cast<MssqlNetSchemaEntry &>(*info_.schema_entry).AddTable(info_.table_name, "BASE TABLE");
	}
	return SinkFinalizeType::READY;
}

SourceResultType MssqlNetPhysicalCreateTableAs::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                                OperatorSourceInput &input) const {
	auto &gstate = sink_state->Cast<MssqlNetCtasGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	if (gstate.returned) {
		return SourceResultType::FINISHED;
	}
	gstate.returned = true;
	chunk.SetCardinality(1);
	chunk.SetValue(0, 0, Value::BIGINT((int64_t)gstate.total));
	return SourceResultType::FINISHED;
}

} // namespace duckdb
