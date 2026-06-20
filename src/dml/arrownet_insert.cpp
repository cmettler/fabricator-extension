//===----------------------------------------------------------------------===//
//                         arrownet — INSERT physical operator (impl)
//
// INSERT streams the input chunks to the bridge as Arrow and the provider bulk
// loads them (SqlBulkCopy). The C++ side is provider-agnostic: it only produces
// Arrow + the target table identity; all SQL/type specifics live in C#.
//===----------------------------------------------------------------------===//

#include "dml/arrownet_insert.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"

#include <cstring>

namespace duckdb {

class ArrowNetInsertGlobalSinkState : public GlobalSinkState {
public:
	ArrowNetInsertGlobalSinkState(ClientContext &context, const ArrowNetInsertTarget &target)
	    : properties(context.GetClientProperties()),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, target.column_types)) {
		// The Arrow schema field names = the insert column names, so the provider
		// can map columns by name (SqlBulkCopy column mappings). The producer also
		// builds the Arrow schema handed to the streaming bulk session at begin.
		producer = make_uniq<arrownet::ArrowProducer>(target.column_types, target.columns, properties);
	}

	~ArrowNetInsertGlobalSinkState() override {
		// If the query failed before Finalize, cancel the background bulk load so its
		// task + connection are released (best-effort; never throw from a destructor).
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
	//! Streaming bulk-load session (non-RETURNING path). RETURNING still uses
	//! `producer` since the inserted rows are read back at Finalize.
	ArrowNetHandle bulk_session = nullptr;
	bool bulk_completed = false;
	idx_t total = 0;
	bool returned = false;
	//! For INSERT ... RETURNING: reader over the OUTPUT INSERTED.* rows.
	unique_ptr<arrownet::ArrowStreamReader> returning_reader;
	mutable mutex lock;
};

class ArrowNetInsertLocalSinkState : public LocalSinkState {};

ArrowNetPhysicalInsert::ArrowNetPhysicalInsert(PhysicalPlan &plan, vector<LogicalType> types,
                                               idx_t estimated_cardinality, ArrowNetInsertTarget target,
                                               ArrowNetHandle handle)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      target_(std::move(target)), handle_(handle) {
}

unique_ptr<GlobalSinkState> ArrowNetPhysicalInsert::GetGlobalSinkState(ClientContext &context) const {
	auto gstate = make_uniq<ArrowNetInsertGlobalSinkState>(context, target_);
	if (!target_.returning) {
		// Stream rows to the provider as they are sinked (bounded memory) rather than
		// buffering the whole input. Build the Arrow schema from the column types
		// (reusing the producer's schema export) and start the background bulk load.
		ArrowSchema schema;
		std::memset(&schema, 0, sizeof(schema));
		auto *stream = gstate->producer->Stream();
		stream->get_schema(stream, &schema);
		gstate->bulk_session = arrownet::BeginBulk(handle_, target_.schema_name, target_.table_name,
		                                           /*create_table=*/false, /*replace=*/false,
		                                           /*check_constraints=*/true, schema);
	}
	return std::move(gstate);
}

unique_ptr<LocalSinkState> ArrowNetPhysicalInsert::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<ArrowNetInsertLocalSinkState>();
}

SinkResultType ArrowNetPhysicalInsert::Sink(ExecutionContext &context, DataChunk &chunk,
                                            OperatorSinkInput &input) const {
	auto &gstate = input.global_state.Cast<ArrowNetInsertGlobalSinkState>();
	if (chunk.size() == 0) {
		return SinkResultType::NEED_MORE_INPUT;
	}
	ArrowAppender appender(target_.column_types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(chunk, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();
	if (target_.returning) {
		// RETURNING accumulates the rows; the OUTPUT result is read back at Finalize.
		lock_guard<mutex> guard(gstate.lock);
		gstate.producer->AddBatch(array);
	} else {
		// Stream the batch to the provider. PushBatch blocks for backpressure while
		// the channel is full. The sink is serial (ParallelSink defaults to false),
		// so no lock is needed and blocking here cannot starve another sink thread.
		arrownet::PushBatch(gstate.bulk_session, array);
	}
	return SinkResultType::NEED_MORE_INPUT;
}

SinkCombineResultType ArrowNetPhysicalInsert::Combine(ExecutionContext &context,
                                                      OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}

SinkFinalizeType ArrowNetPhysicalInsert::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                  OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<ArrowNetInsertGlobalSinkState>();
	if (target_.returning) {
		// INSERT ... OUTPUT INSERTED.* -> Arrow; ingested by GetData as the source.
		lock_guard<mutex> guard(gstate.lock);
		gstate.producer->Finish();
		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		arrownet::InsertReturning(handle_, target_.schema_name, target_.table_name, *gstate.producer->Stream(), out);
		gstate.returning_reader = make_uniq<arrownet::ArrowStreamReader>(context, out);
	} else {
		// Signal end-of-stream and wait for the background bulk load to drain.
		gstate.total = (idx_t)arrownet::CompleteBulk(gstate.bulk_session, /*abort=*/false);
		gstate.bulk_completed = true;
		gstate.bulk_session = nullptr;
	}
	return SinkFinalizeType::READY;
}

SourceResultType ArrowNetPhysicalInsert::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                         OperatorSourceInput &input) const {
	auto &gstate = sink_state->Cast<ArrowNetInsertGlobalSinkState>();
	lock_guard<mutex> guard(gstate.lock);

	if (target_.returning) {
		// Stream the inserted (OUTPUT) rows back as this operator's output.
		gstate.returning_reader->Read(chunk);
		return chunk.size() == 0 ? SourceResultType::FINISHED : SourceResultType::HAVE_MORE_OUTPUT;
	}

	if (gstate.returned) {
		return SourceResultType::FINISHED;
	}
	gstate.returned = true;
	chunk.SetCardinality(1);
	chunk.SetValue(0, 0, Value::BIGINT((int64_t)gstate.total));
	return SourceResultType::FINISHED;
}

} // namespace duckdb
