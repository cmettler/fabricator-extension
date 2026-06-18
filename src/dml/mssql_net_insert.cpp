//===----------------------------------------------------------------------===//
//                         mssql_net — INSERT physical operator (impl)
//
// INSERT streams the input chunks to the bridge as Arrow and the provider bulk
// loads them (SqlBulkCopy). The C++ side is provider-agnostic: it only produces
// Arrow + the target table identity; all SQL/type specifics live in C#.
//===----------------------------------------------------------------------===//

#include "dml/mssql_net_insert.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"

#include <cstring>

namespace duckdb {

class MssqlNetInsertGlobalSinkState : public GlobalSinkState {
public:
	MssqlNetInsertGlobalSinkState(ClientContext &context, const MssqlNetInsertTarget &target)
	    : properties(context.GetClientProperties()),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, target.column_types)) {
		// The Arrow schema field names = the insert column names, so the provider
		// can map columns by name (SqlBulkCopy column mappings).
		producer = make_uniq<arrownet::ArrowProducer>(target.column_types, target.columns, properties);
	}

	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<arrownet::ArrowProducer> producer;
	idx_t total = 0;
	bool returned = false;
	//! For INSERT ... RETURNING: reader over the OUTPUT INSERTED.* rows.
	unique_ptr<arrownet::ArrowStreamReader> returning_reader;
	mutable mutex lock;
};

class MssqlNetInsertLocalSinkState : public LocalSinkState {};

MssqlNetPhysicalInsert::MssqlNetPhysicalInsert(PhysicalPlan &plan, vector<LogicalType> types,
                                               idx_t estimated_cardinality, MssqlNetInsertTarget target,
                                               ArrowNetHandle handle)
    : PhysicalOperator(plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
      target_(std::move(target)), handle_(handle) {
}

unique_ptr<GlobalSinkState> MssqlNetPhysicalInsert::GetGlobalSinkState(ClientContext &context) const {
	return make_uniq<MssqlNetInsertGlobalSinkState>(context, target_);
}

unique_ptr<LocalSinkState> MssqlNetPhysicalInsert::GetLocalSinkState(ExecutionContext &context) const {
	return make_uniq<MssqlNetInsertLocalSinkState>();
}

SinkResultType MssqlNetPhysicalInsert::Sink(ExecutionContext &context, DataChunk &chunk,
                                            OperatorSinkInput &input) const {
	auto &gstate = input.global_state.Cast<MssqlNetInsertGlobalSinkState>();
	if (chunk.size() == 0) {
		return SinkResultType::NEED_MORE_INPUT;
	}
	ArrowAppender appender(target_.column_types, chunk.size(), gstate.properties, gstate.extension_types);
	appender.Append(chunk, 0, chunk.size(), chunk.size());
	ArrowArray array = appender.Finalize();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->AddBatch(array);
	return SinkResultType::NEED_MORE_INPUT;
}

SinkCombineResultType MssqlNetPhysicalInsert::Combine(ExecutionContext &context,
                                                      OperatorSinkCombineInput &input) const {
	return SinkCombineResultType::FINISHED;
}

SinkFinalizeType MssqlNetPhysicalInsert::Finalize(Pipeline &pipeline, Event &event, ClientContext &context,
                                                  OperatorSinkFinalizeInput &input) const {
	auto &gstate = input.global_state.Cast<MssqlNetInsertGlobalSinkState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	if (target_.returning) {
		// INSERT ... OUTPUT INSERTED.* -> Arrow; ingested by GetData as the source.
		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		arrownet::InsertReturning(handle_, target_.schema_name, target_.table_name, *gstate.producer->Stream(), out);
		gstate.returning_reader = make_uniq<arrownet::ArrowStreamReader>(context, out);
	} else {
		gstate.total = (idx_t)arrownet::BulkInsert(handle_, target_.schema_name, target_.table_name,
		                                           /*create_table=*/false, /*replace=*/false,
		                                           *gstate.producer->Stream());
	}
	return SinkFinalizeType::READY;
}

SourceResultType MssqlNetPhysicalInsert::GetDataInternal(ExecutionContext &context, DataChunk &chunk,
                                                         OperatorSourceInput &input) const {
	auto &gstate = sink_state->Cast<MssqlNetInsertGlobalSinkState>();
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
