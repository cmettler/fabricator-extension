//===----------------------------------------------------------------------===//
//                         fabricator — CTAS operator (impl)
//===----------------------------------------------------------------------===//

#include "dml/fabricator_ctas.hpp"

#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_schema_entry.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

#include <cstring>

namespace duckdb {

class FabricatorCtasGlobalState : public GlobalSinkState {
public:
	FabricatorCtasGlobalState(ClientContext &context, const FabricatorCtasInfo &info)
	    : properties(fabricator::BoundaryClientProperties(context)),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, info.column_types)) {
		// The producer also builds the Arrow schema handed to begin_bulk.
		producer = make_uniq<fabricator::ArrowProducer>(info.column_types, info.column_names, properties);
	}

	~FabricatorCtasGlobalState() override {
		// Cancel the background load if the query failed before Finalize (best-effort).
		if (bulk_session && !bulk_completed) {
			try {
				fabricator::CompleteBulk(bulk_session, /*abort=*/true);
			} catch (...) {
			}
		}
	}

	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<fabricator::ArrowProducer> producer;
	//! Streaming bulk-load session (table created at begin from the column schema).
	FabricatorHandle bulk_session = nullptr;
	bool bulk_completed = false;
	idx_t total = 0;
	bool returned = false;
	mutable mutex lock;
};

class FabricatorCtasLocalState : public LocalSinkState {};

FabricatorPhysicalCreateTableAs::FabricatorPhysicalCreateTableAs(PhysicalPlan &plan, vector<LogicalType> types,
                                                             idx_t estimated_cardinality, FabricatorCtasInfo info)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      info_(std::move(info)) {
}

unique_ptr<GlobalSinkState> FabricatorPhysicalCreateTableAs::GetGlobalSinkState(ClientContext &context) const {
	auto gstate = make_uniq<FabricatorCtasGlobalState>(context, info_);
	// Stream rows to the provider as they are sinked (bounded memory). The table is
	// created from the column schema at begin (create_table=true), then rows stream
	// in. Reuses the same managed bulk path as bulk_insert(create=true).
	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	auto *stream = gstate->producer->Stream();
	stream->get_schema(stream, &schema);
	fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), fabricator::SessionKeyFor(&context)); // host-FS opener for a Delta-catalog CTAS
	gstate->bulk_session = fabricator::BeginBulk(info_.handle, info_.schema_name, info_.table_name,
	                                           /*create_table=*/true, info_.replace, /*check_constraints=*/false,
	                                           (int64_t)context.ActiveTransaction().global_transaction_id, schema,
	                                           info_.partition_columns, info_.sort_columns, /*schema_mode=*/"",
	                                           /*partition_overwrite=*/false, info_.options_json);
	return std::move(gstate);
}

unique_ptr<LocalSinkState> FabricatorPhysicalCreateTableAs::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<FabricatorCtasLocalState>();
}

SinkResultType FabricatorPhysicalCreateTableAs::Sink(ExecutionContext &context, DataChunk &chunk,
                                                   OperatorSinkInput &input) const {
	auto &gstate = input.global_state.Cast<FabricatorCtasGlobalState>();
	if (chunk.size() == 0) {
		return SinkResultType::NEED_MORE_INPUT;
	}
	// Convert the chunk to an Arrow array and stream it to the provider. PushBatch
	// blocks for backpressure while the channel is full; the sink is serial so no
	// lock is needed.
	ArrowAppender appender(info_.column_types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(chunk, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();
	fabricator::PushBatch(gstate.bulk_session, array);
	return SinkResultType::NEED_MORE_INPUT;
}

SinkCombineResultType FabricatorPhysicalCreateTableAs::Combine(ExecutionContext &context,
                                                             OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}

SinkFinalizeType FabricatorPhysicalCreateTableAs::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                         OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<FabricatorCtasGlobalState>();
	// Signal end-of-stream and wait for the background load (and the CREATE TABLE) to finish. complete_bulk
	// CONSUMES the session even on error — mark it consumed BEFORE the call so a thrown provider error can't
	// lead the destructor to double-complete a freed (and possibly recycled) handle.
	auto session = gstate.bulk_session;
	gstate.bulk_completed = true;
	gstate.bulk_session = nullptr;
	gstate.total = (idx_t)fabricator::CompleteBulk(session, /*abort=*/false);
	// Make the new table visible in the attached catalog for this session.
	if (info_.schema_entry) {
		const_cast<FabricatorSchemaEntry &>(*info_.schema_entry).AddTable(info_.table_name, "BASE TABLE");
	}
	return SinkFinalizeType::READY;
}

SourceResultType FabricatorPhysicalCreateTableAs::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                                OperatorSourceInput &input) const {
	auto &gstate = sink_state->Cast<FabricatorCtasGlobalState>();
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
