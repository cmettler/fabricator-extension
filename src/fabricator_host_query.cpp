//===----------------------------------------------------------------------===//
//                         fabricator — host query (impl)
//===----------------------------------------------------------------------===//

#include "fabricator_host_query.hpp"

#include "fabricator/arrow_ingest.hpp"
#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb.h" // C API: duckdb_arrow_scan + duckdb_connection (data-in via connection-scoped views)
#include "duckdb/function/replacement_scan.hpp"
#include "duckdb/catalog/catalog_search_path.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/client_data.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/connection.hpp"
#include "duckdb/logging/logger.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/expression/function_expression.hpp"
#include "duckdb/parser/tableref/table_function_ref.hpp"

#include <cstdlib>
#include <cstring>

namespace duckdb {

namespace {

// A self-owning ArrowArrayStream over a fresh-connection query result. Unlike fabricator::ArrowProducer (which
// is for synchronous hand-off within a call), this is drained ASYNCHRONOUSLY by the consuming scan, so it
// owns its Connection + result and frees them on release. The fresh Connection has its own ClientContext /
// transaction — the in-flight context is non-reentrant, so reusing it would corrupt the outer query.
// Owns MOVED-IN copies of the caller's input ArrowArrayStreams.
//
// ⚠ This is load-bearing, not tidiness. `duckdb_arrow_scan` stores the RAW POINTER it is given inside the
// view it creates (`Value::POINTER((uintptr_t)input)`) and the query below is STREAMING — `SendQuery` returns
// as soon as the first chunk is ready, so the arrow scan is generally NOT consumed by the time this function
// returns. The caller's struct is a managed allocation whose cleanup runs the moment the ABI call returns, so
// leaving the view pointed at it is a use-after-free: the scan later dereferences freed memory and reads
// garbage string offsets (observed as `INTERNAL Error: Information loss on integer cast: value 4294967296`,
// i.e. a ~4 GB string length, and as an outright SEGFAULT).
//
// Adopting also performs the C-data-interface MOVE — the source struct is zeroed, so its `release` is null and
// the caller's own cleanup becomes a plain deallocation that cannot double-release the exporter.
struct OwnedArrowInputs {
	vector<unique_ptr<ArrowArrayStream>> streams;

	ArrowArrayStream *Adopt(ArrowArrayStream *src) {
		auto owned = make_uniq<ArrowArrayStream>();
		*owned = *src;                     // copy callbacks + private_data
		std::memset(src, 0, sizeof(*src)); // ...and take ownership: the caller must not release it
		auto *raw = owned.get();
		streams.push_back(std::move(owned));
		return raw;
	}

	~OwnedArrowInputs() {
		// Releases only what the query did not consume (a consumed stream nulls its own release).
		for (auto &s : streams) {
			if (s && s->release) {
				s->release(s.get());
			}
		}
	}
};

struct HostQueryStream {
	// FIRST member on purpose: members are destroyed in reverse declaration order, so the adopted input
	// streams outlive the result/connection that scans them.
	unique_ptr<OwnedArrowInputs> inputs;
	unique_ptr<Connection> conn;
	unique_ptr<PreparedStatement> prepared; // kept alive for the param path (the streaming result references it)
	unique_ptr<QueryResult> result;         // a StreamQueryResult — fetched lazily (bounded memory)
	vector<LogicalType> types;
	vector<string> names;
	string last_error;
	//! The stream has seen its EOF. Load-bearing ONLY when batches accumulate several chunks: a Fetch that
	//! returns null also CLOSES the streaming result, so a later Fetch throws ("closed pending query result")
	//! rather than returning null again. In one-chunk mode that EOF is the value get_next returns, so the
	//! consumer never calls back; accumulating swallows it mid-batch and the consumer legitimately calls once
	//! more. DuckDB's own multi-chunk batching guards the same way (ChunkScanState::Finished /
	//! QueryResultChunkScanState::InternalLoad's `if (!stream_result.IsOpen()) return true;`).
	bool finished = false;
	ArrowArrayStream stream {};
};

int HostQueryGetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *st = static_cast<HostQueryStream *>(stream->private_data);
	auto props = fabricator::BoundaryClientProperties(*st->conn->context);
	ArrowConverter::ToArrowSchema(out, st->types, st->names, props);
	return 0;
}

int HostQueryGetNext(ArrowArrayStream *stream, ArrowArray *out) {
	auto *st = static_cast<HostQueryStream *>(stream->private_data);
	std::memset(out, 0, sizeof(*out));
	try {
		if (st->finished) {
			return 0; // EOF already seen (mid-batch, while accumulating) — never re-Fetch a closed result
		}
		auto chunk = st->result->Fetch(); // next DataChunk, lazily; null at end. A streaming result can
		                                   // surface a RUNTIME error here (vs at SendQuery) — caught below.
		if (!chunk || chunk->size() == 0) {
			st->finished = true;
			return 0; // EOF — a zeroed (released) ArrowArray is the end marker
		}
		auto props = fabricator::BoundaryClientProperties(*st->conn->context);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(*st->conn->context, st->types);
		// A batch accumulates chunks up to DuckDB's own MORSEL size, because the exported RecordBatch IS the
		// morsel of a parallel Arrow scan (arrow_ingest's GetNextBatch hands one batch to one thread, exactly
		// as DuckDB's ArrowScanParallelStateNext does). One DataChunk per batch made that morsel
		// STANDARD_VECTOR_SIZE: measured on a 6M-row scan, 2929 batches of exactly 2048 rows plus a 1408
		// tail, against 49 row groups of 122880 for the same data read natively — 60x more morsels, each
		// paying a mutex acquisition, an Arrow import and converter setup.
		//
		// MEASURED (6M rows, CPU-bound GROUP BY, median of 3): 0.864s at one chunk / 1 thread, 0.689s from
		// batching alone, 0.640s from threads alone, 0.592s from both. The two overlap rather than add —
		// both attack the same per-batch cost. Row-group-sized batches also run far more PREDICTABLY (a 1.7%
		// spread across reps vs 10% at one chunk), there being much less lock contention to jitter.
		//
		// ⚠⚠ DEFAULT IS ONE CHUNK, AND THE REASON IS NOT PERFORMANCE — A BATCH IS ALSO A FILE.
		// engineered-wood writes ONE PARQUET FILE PER INPUT BATCH, and this stream feeds WRITERS as well as
		// scans (the OPTIMIZE recluster's ORDER BY, sorted-by writes). Defaulting to a row group therefore
		// silently changed physical file LAYOUT: verify_delta_clustered_optimize's OPTIMIZE collapsed 80000
		// rows into ONE file where the suite asserts several, so `delta.targetFileSize` stopped being
		// honoured. MEASURED — default(122880): 1 failed; 0 (one chunk): 147 passed.
		//
		// So the batch target must be scoped to the CONSUMER, which this entry cannot see: a scan wants big
		// morsels, a writer wants its own file granularity. Plumbing it needs a parameter on host_query (an
		// ABI change), so the win is deferred rather than taken here. What it is worth, measured on a 6M-row
		// CPU-bound GROUP BY (median of 3): 0.864s -> 0.689s single-threaded from batching alone.
		// The env var stays as the experiment hook that produced those numbers.
		static const idx_t target_rows = []() -> idx_t {
			const char *env = std::getenv("FABRICATOR_HOST_QUERY_BATCH_ROWS");
			if (!env || !*env) {
				return 0; // one chunk per batch — never change this without scoping it per consumer
			}
			auto n = std::atoll(env);
			return n > 0 ? (idx_t)n : 0;
		}();
		ArrowAppender appender(st->types, target_rows > 0 ? target_rows : chunk->size(), props, extension_types);
		idx_t appended = 0;
		while (true) {
			appender.Append(*chunk, 0, chunk->size(), chunk->size());
			appended += chunk->size();
			if (target_rows == 0 || appended >= target_rows) {
				break; // one-chunk mode, or the target is met
			}
			chunk = st->result->Fetch(); // EOF leaves nothing unappended — every fetched chunk is appended above
			if (!chunk || chunk->size() == 0) {
				st->finished = true; // this batch is short; the NEXT get_next must not Fetch the closed result
				break;
			}
		}
		*out = appender.Finalize();
		return 0;
	} catch (std::exception &e) {
		st->last_error = string("fabricator_host_query: ") + e.what();
		return 1; // the consumer reads get_last_error
	}
}

const char *HostQueryGetLastError(ArrowArrayStream *stream) {
	auto *st = static_cast<HostQueryStream *>(stream->private_data);
	return st->last_error.empty() ? nullptr : st->last_error.c_str();
}

void HostQueryRelease(ArrowArrayStream *stream) {
	delete static_cast<HostQueryStream *>(stream->private_data);
	stream->release = nullptr;
}

// Table function bind: stash a factory that (re)runs the query on a fresh connection + produces the result
// stream, then read the output schema from it (PopulateReturnSchema runs the factory once for the schema;
// the scan runs it again for the data — like the other fabricator table functions).
unique_ptr<FunctionData> HostQueryBind(ClientContext &context, TableFunctionBindInput &input,
                                       vector<LogicalType> &return_types, vector<string> &names) {
	auto sql = input.inputs[0].GetValue<string>();
	auto db = context.db; // shared_ptr<DatabaseInstance>; the fresh connection is opened on it per run
	auto bind_data = make_uniq<fabricator::ArrowStreamBindData>();
	// Capture the caller's session state BY VALUE here, at bind, while `context` is definitely alive. The
	// factory below runs later (and again per execution), so capturing `&context` would be a dangling pointer —
	// the same bug class as the host-FS opener that commit 142b350 moved to per-call resolution.
	HostQuerySession session;
	session.search_path = ClientData::Get(context).catalog_search_path->GetSetPaths();
	Value tz_value;
	if (context.TryGetCurrentSetting("TimeZone", tz_value) && !tz_value.IsNull()) {
		session.time_zone = tz_value.ToString();
	}
	bind_data->factory = [db, sql, session](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		// the table-function form takes no params/inputs; it DOES inherit the caller's session (unlike the
		// C#-callable host_query service — see docs/host-query.md).
		MakeHostQueryStream(*db, sql, nullptr, {}, out, nullptr, &session);
	};
	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

} // namespace

void MakeHostQueryStream(DatabaseInstance &db, const string &sql, ArrowArrayStream *params,
                         const vector<HostQueryInput> &inputs, ArrowArrayStream &out,
                         shared_ptr<ClientContext> *out_context, const HostQuerySession *session) {
	auto conn = make_uniq<Connection>(db); // FRESH connection: own context/transaction (see header)
	if (out_context) {
		*out_context = conn->context; // for out-of-band interruption (host_query_interrupt, ABI v66)
	}
	// Adopt the caller's session state, when one was supplied (the table function supplies it; the C# host
	// service deliberately does not — provider-generated SQL must not depend on what the user last USEd).
	if (session) {
		if (!session->search_path.empty()) {
			// SET_DIRECTLY: install exactly the captured entries. Copying the resolved values avoids emitting
			// `USE <ident>` text, which would need identifier quoting to be safe.
			ClientData::Get(*conn->context)
			    .catalog_search_path->Set(session->search_path, CatalogSetPathType::SET_DIRECTLY);
		}
		if (!session->time_zone.empty()) {
			// TimeZone is an ICU-registered EXTENSION option (icu_extension.cpp AddExtensionOption), so there is
			// no core set_local to call — it goes through the normal SET path. Value::ToSQLString() quotes and
			// escapes the literal, so a hostile zone string cannot break out.
			auto tz_result = conn->Query("SET TimeZone=" + Value(session->time_zone).ToSQLString());
			if (tz_result->HasError()) {
				// Non-fatal by design: the caller's query is what matters, and a build without ICU has no
				// TimeZone option to set. Falling back to the fresh connection's default beats refusing to run.
				tz_result.reset();
			}
		}
	}
	// Register each named Arrow input as a connection-scoped view (data-in). duckdb_arrow_scan reinterprets
	// the opaque handle back to ArrowArrayStream* and creates a view over it. We ADOPT each stream first (see
	// OwnedArrowInputs): the view keeps the RAW POINTER and the query below is LAZY, so the caller's own
	// allocation must not be what it points at. On any throw below, the local holder releases them.
	auto owned_inputs = make_uniq<OwnedArrowInputs>();
	for (auto &in : inputs) {
		auto rc = duckdb_arrow_scan(reinterpret_cast<duckdb_connection>(conn.get()), in.name.c_str(),
		                            reinterpret_cast<duckdb_arrow_stream>(owned_inputs->Adopt(in.stream)));
		if (rc != DuckDBSuccess) {
			throw IOException("fabricator_host_query: failed to register input view '" + in.name + "'");
		}
	}
	unique_ptr<QueryResult> result;
	unique_ptr<PreparedStatement> prepared;
	if (params) {
		// Read the 1-row Arrow params batch into values, bind positionally via a prepared statement.
		fabricator::ArrowStreamReader reader(*conn->context, *params); // consumes + releases the params stream
		DataChunk chunk;
		chunk.Initialize(Allocator::Get(*conn->context), reader.Types());
		reader.Read(chunk);
		vector<Value> values;
		for (idx_t c = 0; c < chunk.ColumnCount(); c++) {
			values.push_back(chunk.size() > 0 ? chunk.GetValue(c, 0) : Value());
		}
		prepared = conn->Prepare(sql);
		if (prepared->HasError()) {
			throw IOException("fabricator_host_query: " + prepared->GetError());
		}
		// Streaming result: it references the prepared statement, so the holder keeps `prepared` alive.
		result = prepared->Execute(values, /*allow_stream_result=*/true);
	} else {
		result = conn->SendQuery(sql); // streaming (lazy Fetch) — bounded memory for large results
	}
	if (result->HasError()) { // bind/plan errors surface here; runtime errors surface during Fetch (get_next)
		throw IOException("fabricator_host_query: " + result->GetError());
	}
	auto *st = new HostQueryStream();
	st->inputs = std::move(owned_inputs); // the views reference these; they outlive `result` (see the struct)
	st->conn = std::move(conn);
	st->prepared = std::move(prepared);
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
// result as a self-owning ArrowArrayStream (C# imports + releases it). `out_interrupt` (nullable) receives
// a heap shared_ptr<ClientContext> to the fresh context so the managed InterruptScope can cancel an
// in-flight Fetch out-of-band (the fresh connection is invisible to the USER query's Ctrl+C). See abi.h /
// docs/host-query.md / docs/cancellation.md.
int32_t HostQueryService(const char *sql, ArrowArrayStream *params, FabricatorHostInputs *inputs,
                         ArrowArrayStream *out, void **out_interrupt, char **err) {
	try {
		if (!g_host_db) {
			throw IOException("fabricator host_query: host database not available");
		}
		vector<HostQueryInput> in;
		if (inputs && inputs->count > 0) {
			for (int32_t i = 0; i < inputs->count; i++) {
				in.push_back({string(inputs->names[i]), inputs->streams[i]});
			}
		}
		shared_ptr<ClientContext> ctx;
		MakeHostQueryStream(*g_host_db, sql ? string(sql) : string(), params, in, *out,
		                    out_interrupt ? &ctx : nullptr);
		if (out_interrupt) {
			// The handle owns a shared_ptr copy: an interrupt AFTER the result stream is released still
			// dereferences a live (idle) context — a harmless no-op instead of a use-after-free.
			*out_interrupt = new shared_ptr<ClientContext>(std::move(ctx));
		}
		return FABRICATOR_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

// host_query_interrupt — trip the fresh context's interrupted flag (thread-safe atomic); an in-flight
// Fetch aborts at DuckDB's next between-tasks check and surfaces as a get_next error. Best-effort.
void HostQueryInterruptService(void *interrupt_handle) {
	if (!interrupt_handle) {
		return;
	}
	try {
		(*static_cast<shared_ptr<ClientContext> *>(interrupt_handle))->Interrupt();
	} catch (...) { // never fault the extension from a cancellation signal
	}
}

// host_query_interrupt_free — release the handle (the managed wrapper calls this exactly once, after any
// in-flight interrupt callback has been waited out).
void HostQueryInterruptFreeService(void *interrupt_handle) {
	delete static_cast<shared_ptr<ClientContext> *>(interrupt_handle);
}

// fabricator_scan(name) — scan an ambient named source (registered in C# via Host.RegisterSource). The factory
// asks the managed registry for a fresh stream by name (OpenNamedInput); reuses the arrow_ingest scan path.
static unique_ptr<FunctionData> NamedScanBind(ClientContext &context, TableFunctionBindInput &input,
                                              vector<LogicalType> &return_types, vector<string> &names) {
	auto name = input.inputs[0].GetValue<string>();
	auto bind_data = make_uniq<fabricator::ArrowStreamBindData>();
	bind_data->factory = [name](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::OpenNamedInput(name, out);
	};
	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// Replacement scan: an unresolved bare table name that matches a registered ambient source is rewritten to
// `fabricator_scan('<name>')`. Fires only for names DuckDB couldn't resolve; NamedInputExists is non-throwing
// + tolerates an unavailable bridge, so a genuine "table does not exist" is left to DuckDB.
static unique_ptr<TableRef> NamedSourceReplacement(ClientContext &, ReplacementScanInput &input,
                                                   optional_ptr<ReplacementScanData>) {
	if (!fabricator::NamedInputExists(input.table_name)) {
		return nullptr;
	}
	auto table_function = make_uniq<TableFunctionRef>();
	vector<unique_ptr<ParsedExpression>> children;
	children.push_back(make_uniq<ConstantExpression>(Value(input.table_name)));
	table_function->function = make_uniq<FunctionExpression>("fabricator_scan", std::move(children));
	table_function->alias = input.table_name;
	return std::move(table_function);
}

// host_log — forward a managed ILogger event into DuckDB's internal logging (duckdb_logs). Best-effort: a
// no-op until the DB is known, and any logging failure is swallowed (logging must never fault the extension).
// GATED on ShouldLog (enabled + level + type filters) — Logger::WriteLog itself writes UNCONDITIONALLY, and
// the shell's default log storage prints to the console, so ungated forwarding spammed every interactive
// session with the bridge's Debug chatter. With the gate, semantics are DuckDB-native: silence until
// `CALL enable_logging(...)`; the .test duckdb_logs pins on Debug messages enable with `level := 'debug'`.
static void HostLogService(int32_t level, const char *log_type, const char *message) {
	if (!g_host_db) {
		return;
	}
	LogLevel lvl;
	switch (level) {
	case 0:
		lvl = LogLevel::LOG_TRACE;
		break;
	case 1:
		lvl = LogLevel::LOG_DEBUG;
		break;
	case 3:
		lvl = LogLevel::LOG_WARNING;
		break;
	case 4:
		lvl = LogLevel::LOG_ERROR;
		break;
	case 5:
		lvl = LogLevel::LOG_FATAL;
		break;
	default:
		lvl = LogLevel::LOG_INFO;
		break;
	}
	try {
		auto &logger = Logger::Get(*g_host_db);
		const char *type = log_type ? log_type : "Fabricator";
		if (!logger.ShouldLog(type, lvl)) {
			return;
		}
		logger.WriteLog(type, lvl, message ? message : "");
	} catch (...) {
	}
}

void RegisterHostQuery(ExtensionLoader &loader) {
	g_host_db = &loader.GetDatabaseInstance();
	// Make host_query (+ its v66 interrupt pair) callable from C# (patched onto the host-services block).
	fabricator::SetHostQueryService(HostQueryService, HostQueryInterruptService, HostQueryInterruptFreeService);
	fabricator::SetHostLog(HostLogService);            // forward ILogger events into DuckDB's internal logging
	DBConfig::GetConfig(loader.GetDatabaseInstance()).replacement_scans.emplace_back(NamedSourceReplacement);
	TableFunction fn("fabricator_host_query", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan, HostQueryBind,
	                 fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(fn);

	TableFunction scan("fabricator_scan", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan, NamedScanBind,
	                   fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(scan);
}

} // namespace duckdb
