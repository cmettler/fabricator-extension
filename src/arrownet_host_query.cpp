//===----------------------------------------------------------------------===//
//                         arrownet — host query (impl)
//===----------------------------------------------------------------------===//

#include "arrownet_host_query.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb.h" // C API: duckdb_arrow_scan + duckdb_connection (data-in via connection-scoped views)
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/connection.hpp"

#include <cstdlib>
#include <cstring>

namespace duckdb {

namespace {

// A self-owning ArrowArrayStream over a fresh-connection query result. Unlike arrownet::ArrowProducer (which
// is for synchronous hand-off within a call), this is drained ASYNCHRONOUSLY by the consuming scan, so it
// owns its Connection + result and frees them on release. The fresh Connection has its own ClientContext /
// transaction — the in-flight context is non-reentrant, so reusing it would corrupt the outer query.
struct HostQueryStream {
	unique_ptr<Connection> conn;
	unique_ptr<QueryResult> result;
	vector<LogicalType> types;
	vector<string> names;
	ArrowArrayStream stream {};
};

int HostQueryGetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *st = static_cast<HostQueryStream *>(stream->private_data);
	auto props = arrownet::BoundaryClientProperties(*st->conn->context);
	ArrowConverter::ToArrowSchema(out, st->types, st->names, props);
	return 0;
}

int HostQueryGetNext(ArrowArrayStream *stream, ArrowArray *out) {
	auto *st = static_cast<HostQueryStream *>(stream->private_data);
	std::memset(out, 0, sizeof(*out));
	auto chunk = st->result->Fetch(); // next DataChunk, or null at end-of-result
	if (!chunk || chunk->size() == 0) {
		return 0; // EOF — a zeroed (released) ArrowArray is the end marker
	}
	auto props = arrownet::BoundaryClientProperties(*st->conn->context);
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(*st->conn->context, st->types);
	ArrowAppender appender(st->types, chunk->size(), props, extension_types);
	appender.Append(*chunk, 0, chunk->size(), chunk->size());
	*out = appender.Finalize();
	return 0;
}

const char *HostQueryGetLastError(ArrowArrayStream *) {
	return nullptr;
}

void HostQueryRelease(ArrowArrayStream *stream) {
	delete static_cast<HostQueryStream *>(stream->private_data);
	stream->release = nullptr;
}

// Table function bind: stash a factory that (re)runs the query on a fresh connection + produces the result
// stream, then read the output schema from it (PopulateReturnSchema runs the factory once for the schema;
// the scan runs it again for the data — like the other arrownet table functions).
unique_ptr<FunctionData> HostQueryBind(ClientContext &context, TableFunctionBindInput &input,
                                       vector<LogicalType> &return_types, vector<string> &names) {
	auto sql = input.inputs[0].GetValue<string>();
	auto db = context.db; // shared_ptr<DatabaseInstance>; the fresh connection is opened on it per run
	auto bind_data = make_uniq<arrownet::ArrowStreamBindData>();
	bind_data->factory = [db, sql](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		MakeHostQueryStream(*db, sql, nullptr, {}, out); // the table-function form takes no params/inputs
	};
	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

} // namespace

void MakeHostQueryStream(DatabaseInstance &db, const string &sql, ArrowArrayStream *params,
                         const vector<HostQueryInput> &inputs, ArrowArrayStream &out) {
	auto conn = make_uniq<Connection>(db); // FRESH connection: own context/transaction (see header)
	// Register each named Arrow input as a connection-scoped view (data-in). duckdb_arrow_scan reinterprets
	// the opaque handle back to ArrowArrayStream* and creates a temp view; the stream is consumed + released
	// by DuckDB during the (materializing) query below.
	for (auto &in : inputs) {
		auto rc = duckdb_arrow_scan(reinterpret_cast<duckdb_connection>(conn.get()), in.name.c_str(),
		                            reinterpret_cast<duckdb_arrow_stream>(in.stream));
		if (rc != DuckDBSuccess) {
			throw IOException("arrownet_host_query: failed to register input view '" + in.name + "'");
		}
	}
	unique_ptr<QueryResult> result;
	if (params) {
		// Read the 1-row Arrow params batch into values, bind positionally via a prepared statement.
		arrownet::ArrowStreamReader reader(*conn->context, *params); // consumes + releases the params stream
		DataChunk chunk;
		chunk.Initialize(Allocator::Get(*conn->context), reader.Types());
		reader.Read(chunk);
		vector<Value> values;
		for (idx_t c = 0; c < chunk.ColumnCount(); c++) {
			values.push_back(chunk.size() > 0 ? chunk.GetValue(c, 0) : Value());
		}
		auto prepared = conn->Prepare(sql);
		if (prepared->HasError()) {
			throw IOException("arrownet_host_query: " + prepared->GetError());
		}
		// Materialize (allow_stream_result=false): the result must NOT reference the prepared statement, which
		// is destroyed when this function returns (the result stream outlives it). Matches the conn.Query path.
		result = prepared->Execute(values, false);
	} else {
		result = conn->Query(sql);
	}
	if (result->HasError()) {
		throw IOException("arrownet_host_query: " + result->GetError());
	}
	auto *st = new HostQueryStream();
	st->conn = std::move(conn);
	st->types = result->types;
	st->names = result->names;
	st->result = std::move(result);
	st->stream.get_schema = HostQueryGetSchema;
	st->stream.get_next = HostQueryGetNext;
	st->stream.get_last_error = HostQueryGetLastError;
	st->stream.release = HostQueryRelease;
	st->stream.private_data = st;
	out = st->stream; // copy the stream struct; ownership of `st` rides private_data (freed in HostQueryRelease)
}

// The host DatabaseInstance the C#-callable `host_query` opens a fresh connection on (captured at load —
// the host service has no per-call context, unlike the fs callbacks). Valid for the extension's lifetime.
static DatabaseInstance *g_host_db = nullptr;

// Duplicate a message into a malloc'd C string for the managed side (freed via the host-services free_str).
static char *DupErr(const string &msg) {
	char *out = static_cast<char *>(malloc(msg.size() + 1));
	if (out) {
		memcpy(out, msg.c_str(), msg.size() + 1);
	}
	return out;
}

// The `host_query` host-service callback (C# -> host). Runs `sql` on a fresh connection + hands C# the
// result as a self-owning ArrowArrayStream (C# imports + releases it). See abi.h / docs/host-query.md.
int32_t HostQueryService(const char *sql, ArrowArrayStream *params, ArrowNetHostInputs *inputs,
                         ArrowArrayStream *out, char **err) {
	try {
		if (!g_host_db) {
			throw IOException("arrownet host_query: host database not available");
		}
		vector<HostQueryInput> in;
		if (inputs && inputs->count > 0) {
			for (int32_t i = 0; i < inputs->count; i++) {
				in.push_back({string(inputs->names[i]), inputs->streams[i]});
			}
		}
		MakeHostQueryStream(*g_host_db, sql ? string(sql) : string(), params, in, *out);
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

void RegisterHostQuery(ExtensionLoader &loader) {
	g_host_db = &loader.GetDatabaseInstance();
	arrownet::SetHostQueryService(HostQueryService); // make host_query callable from C# (added to the host block)
	TableFunction fn("arrownet_host_query", {LogicalType::VARCHAR}, arrownet::ArrowStreamScan, HostQueryBind,
	                 arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	loader.RegisterFunction(fn);
}

} // namespace duckdb
