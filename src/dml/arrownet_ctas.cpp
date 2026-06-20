//===----------------------------------------------------------------------===//
//                         arrownet — CTAS operator (impl)
//===----------------------------------------------------------------------===//

#include "dml/arrownet_ctas.hpp"

#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_schema_entry.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"

#include <cstring>

namespace duckdb {

class ArrowNetCtasGlobalState : public GlobalSinkState {
public:
	ArrowNetCtasGlobalState(ClientContext &context, const ArrowNetCtasInfo &info)
	    : properties(context.GetClientProperties()),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, info.column_types)) {
		// The producer also builds the Arrow schema handed to begin_bulk.
		producer = make_uniq<arrownet::ArrowProducer>(info.column_types, info.column_names, properties);
	}

	~ArrowNetCtasGlobalState() override {
		// Cancel the background load if the query failed before Finalize (best-effort).
		if (bulk_session && !bulk_completed) {
			try {
				arrownet::CompleteBulk(bulk_session, /*abort=*/true);
			} catch (...) {
			}
		}
	}

	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<arrownet::ArrowProducer> producer;
	//! Streaming bulk-load session (table created at begin from the column schema).
	ArrowNetHandle bulk_session = nullptr;
	bool bulk_completed = false;
	idx_t total = 0;
	bool returned = false;
	mutable mutex lock;
};

class ArrowNetCtasLocalState : public LocalSinkState {};

ArrowNetPhysicalCreateTableAs::ArrowNetPhysicalCreateTableAs(PhysicalPlan &plan, vector<LogicalType> types,
                                                             idx_t estimated_cardinality, ArrowNetCtasInfo info)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      info_(std::move(info)) {
}

unique_ptr<GlobalSinkState> ArrowNetPhysicalCreateTableAs::GetGlobalSinkState(ClientContext &context) const {
	auto gstate = make_uniq<ArrowNetCtasGlobalState>(context, info_);
	// Stream rows to the provider as they are sinked (bounded memory). The table is
	// created from the column schema at begin (create_table=true), then rows stream
	// in. Reuses the same managed bulk path as bulk_insert(create=true).
	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	auto *stream = gstate->producer->Stream();
	stream->get_schema(stream, &schema);
	gstate->bulk_session = arrownet::BeginBulk(info_.handle, info_.schema_name, info_.table_name,
	                                           /*create_table=*/true, info_.replace,
	                                           /*check_constraints=*/false, schema);
	return std::move(gstate);
}

unique_ptr<LocalSinkState> ArrowNetPhysicalCreateTableAs::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<ArrowNetCtasLocalState>();
}

SinkResultType ArrowNetPhysicalCreateTableAs::Sink(ExecutionContext &context, DataChunk &chunk,
                                                   OperatorSinkInput &input) const {
	auto &gstate = input.global_state.Cast<ArrowNetCtasGlobalState>();
	if (chunk.size() == 0) {
		return SinkResultType::NEED_MORE_INPUT;
	}
	// Convert the chunk to an Arrow array and stream it to the provider. PushBatch
	// blocks for backpressure while the channel is full; the sink is serial so no
	// lock is needed.
	ArrowAppender appender(info_.column_types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(chunk, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();
	arrownet::PushBatch(gstate.bulk_session, array);
	return SinkResultType::NEED_MORE_INPUT;
}

SinkCombineResultType ArrowNetPhysicalCreateTableAs::Combine(ExecutionContext &context,
                                                             OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}

SinkFinalizeType ArrowNetPhysicalCreateTableAs::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                         OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<ArrowNetCtasGlobalState>();
	// Signal end-of-stream and wait for the background load (and the CREATE TABLE) to finish.
	gstate.total = (idx_t)arrownet::CompleteBulk(gstate.bulk_session, /*abort=*/false);
	gstate.bulk_completed = true;
	gstate.bulk_session = nullptr;
	// Make the new table visible in the attached catalog for this session.
	if (info_.schema_entry) {
		const_cast<ArrowNetSchemaEntry &>(*info_.schema_entry).AddTable(info_.table_name, "BASE TABLE");
	}
	return SinkFinalizeType::READY;
}

SourceResultType ArrowNetPhysicalCreateTableAs::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                                OperatorSourceInput &input) const {
	auto &gstate = sink_state->Cast<ArrowNetCtasGlobalState>();
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
