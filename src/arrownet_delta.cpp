//===----------------------------------------------------------------------===//
//                         arrownet — Delta lakehouse scan (impl)
//===----------------------------------------------------------------------===//

#include "arrownet_delta.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/clr_host.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/main/client_context.hpp"

namespace duckdb {

namespace {

struct DeltaScanBindData : public TableFunctionData {
	string path;
};

// Bind: resolve the Delta table's Arrow schema via the managed reader (no data read). The bind context is
// the opener (secret resolution + the FileSystem the managed DuckDbTableFileSystem reads through).
unique_ptr<FunctionData> DeltaScanBind(ClientContext &context, TableFunctionBindInput &input,
                                       vector<LogicalType> &return_types, vector<string> &names) {
	auto bind_data = make_uniq<DeltaScanBindData>();
	bind_data->path = input.inputs[0].GetValue<string>();

	ArrowSchema schema {};
	arrownet::DeltaSchema(reinterpret_cast<ArrowNetHandle>(&context), bind_data->path, schema);
	arrownet::ReadArrowSchema(context, schema, return_types, names); // consumes/releases `schema`
	return std::move(bind_data);
}

// Global state holds the materialized Arrow stream reader for the whole table (read once at init_global).
struct DeltaScanGlobalState : public GlobalTableFunctionState {
	explicit DeltaScanGlobalState(unique_ptr<arrownet::ArrowStreamReader> reader_p) : reader(std::move(reader_p)) {
	}
	unique_ptr<arrownet::ArrowStreamReader> reader;
	idx_t MaxThreads() const override {
		return 1;
	}
};

unique_ptr<GlobalTableFunctionState> DeltaScanInit(ClientContext &context, TableFunctionInitInput &input) {
	auto &bind_data = input.bind_data->Cast<DeltaScanBindData>();
	// All Delta IO happens here, synchronously, with this execution's ClientContext as the opener — so the
	// opener need only stay valid until DeltaScan returns (the result is materialized in managed memory).
	ArrowArrayStream stream {};
	arrownet::DeltaScan(reinterpret_cast<ArrowNetHandle>(&context), bind_data.path, stream);
	auto reader = make_uniq<arrownet::ArrowStreamReader>(context, stream); // takes ownership
	return make_uniq<DeltaScanGlobalState>(std::move(reader));
}

void DeltaScanFunc(ClientContext &, TableFunctionInput &data, DataChunk &output) {
	auto &gstate = data.global_state->Cast<DeltaScanGlobalState>();
	gstate.reader->Read(output); // sets cardinality 0 at end of stream
}

} // namespace

void RegisterDeltaScan(ExtensionLoader &loader) {
	TableFunction fn("arrownet_delta_scan", {LogicalType::VARCHAR}, DeltaScanFunc, DeltaScanBind, DeltaScanInit);
	loader.RegisterFunction(fn);
}

} // namespace duckdb
