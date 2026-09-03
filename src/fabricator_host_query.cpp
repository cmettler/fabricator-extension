// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

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
#include "duckdb/common/arrow/arrow_wrapper.hpp" // ArrowArrayStreamWrapper (our arrow_scan factory)
#include "duckdb/function/table/arrow.hpp"       // ArrowStreamParameters + the factory typedefs
#include "duckdb/main/relation.hpp"              // Relation::CreateView(name, replace, TEMPORARY)
#include "duckdb/function/replacement_scan.hpp"
#include "duckdb/catalog/catalog_search_path.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/planner/expression/bound_parameter_data.hpp" // named parameter binding for host_query
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
// ⚠ This is load-bearing, not tidiness. The arrow scan stores the RAW POINTER it is given inside the view
// it creates (`Value::POINTER((uintptr_t)input)`) and the query below is STREAMING — `SendQuery` returns
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
	//! SHARED, not owned outright (ABI v84): with a PINNED connection several streams and the caller's own
	//! handle reference one Connection, so the last of them destroys it. A fresh-connection call still ends
	//! up with a refcount of one, i.e. the previous behaviour with no extra lifetime.
	shared_ptr<Connection> conn;
	//! Non-null when this stream runs on a PINNED connection: it keeps the pin alive (so the caller may
	//! close the handle first) and its `open_streams` slot is released in HostQueryRelease.
	shared_ptr<HostConnection> pin;
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

//! A SCHEMA-ONLY stream: it reports a result schema and yields no rows, so a bind can learn the output
//! columns WITHOUT running the statement. `HostQueryBind` fills it from a PREPARED statement.
//!
//! ⚠ Its schema must match what the executing stream produces, which is why both derive it the same way —
//! `ArrowConverter::ToArrowSchema` over the plan's types/names with `BoundaryClientProperties`. The types
//! come from `PreparedStatement`, the executing ones from `QueryResult`; both are the bound plan's.
//! Applies a captured caller session to a fresh connection.
//!
//! ⚠ SHARED BY THE DESCRIBE AND THE EXECUTE ON PURPOSE. `HostQueryBind` prepares the statement on its own
//! fresh connection to learn the output schema; if that connection resolved names or times differently from
//! the one that later runs it, the declared schema and the delivered batches could disagree — and the scan
//! reads the batches through converters built from the DECLARED schema. One function, no drift.
void ApplyHostQuerySession(Connection &conn, const HostQuerySession *session) {
	if (!session) {
		return;
	}
	if (!session->search_path.empty()) {
		// ⚠⚠ THROUGH SQL, and BOTH of the obvious API routes are measured wrong.
		//
		// `Set(paths, SET_DIRECTLY)` refuses any entry whose catalog OR schema is empty
		// (`InternalException: SET_WITHOUT_VERIFICATION requires a fully qualified set path`), and a
		// captured entry legitimately HAS an empty catalog: read from duckdb's catalog_search_path.cpp, a
		// plain `SET search_path='myschema'` stores `path.catalog = GetDefault().catalog`, GetDefault()
		// returns paths[1], and with nothing previously set that is `{INVALID_CATALOG, DEFAULT_SCHEMA}` —
		// where `#define INVALID_CATALOG ""`. So the stored catalog is the empty string. MEASURED as an
		// INTERNAL error, i.e. the assertion-failure class, on `SET search_path='myschema'` followed by any
		// fabricator_host_query.
		//
		// `Set(paths, SET_SCHEMAS)` resolves the empty catalog — and calls Catalog::GetSchema, which needs
		// an ACTIVE TRANSACTION. On a fresh, idle connection there is none: MEASURED as
		// `InternalException: TransactionContext::ActiveTransaction called without active transaction`.
		// One internal error traded for another.
		//
		// So it goes through the statement, which begins its own transaction and resolves the entry exactly
		// as the caller's own `SET search_path` did. Two layers of quoting, both DuckDB's own:
		// ListToString quotes an identifier containing `.`, `,` or `"`, and ToSQLString escapes the string
		// literal — so neither a hostile schema name nor a hostile catalog name can break out.
		auto path_sql = "SET search_path=" +
		                Value(CatalogSearchEntry::ListToString(session->search_path)).ToSQLString();
		auto sp_result = conn.Query(path_sql);
		if (sp_result->HasError()) {
			// ⚠ FATAL, unlike the TimeZone fallback below, and the asymmetry is deliberate: a search path
			// that will not apply means the caller's unqualified names resolve differently here, so the
			// statement fails later as "table not found" — an error naming a table that plainly exists.
			// Saying which session could not be applied is the more useful failure.
			throw IOException("fabricator host_query: could not apply the caller's search path (%s): %s",
			                  path_sql, sp_result->GetError());
		}
	}
	if (!session->time_zone.empty()) {
		// TimeZone is an ICU-registered EXTENSION option (icu_extension.cpp AddExtensionOption), so there is
		// no core set_local to call — it goes through the normal SET path. Value::ToSQLString() quotes and
		// escapes the literal, so a hostile zone string cannot break out.
		auto tz_result = conn.Query("SET TimeZone=" + Value(session->time_zone).ToSQLString());
		if (tz_result->HasError()) {
			// Non-fatal by design: the caller's query is what matters, and a build without ICU has no
			// TimeZone option to set. Falling back to the fresh connection's default beats refusing to run.
			tz_result.reset();
		}
	}
}

struct HostQuerySchemaStream {
	//! ⚠ THE CONNECTION IS OWNED HERE, AND THAT IS A CORRECTNESS REQUIREMENT RATHER THAN CONVENIENCE.
	//! `ClientProperties` holds an `optional_ptr<ClientContext>` which `ToArrowSchema` dereferences, so props
	//! captured from a connection that has since been destroyed are a use-after-free — and a DEFAULT-
	//! constructed ClientProperties fails outright ("Attempting to dereference an optional pointer that is not
	//! set"), which is how this was found. Owning the connection makes the context provably alive for exactly
	//! as long as the stream, which is the only window get_schema is called in.
	unique_ptr<Connection> conn;
	duckdb::vector<LogicalType> types;
	duckdb::vector<string> names;
	string last_error;
	ArrowArrayStream stream {};
};

int HostQuerySchemaGetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *st = static_cast<HostQuerySchemaStream *>(stream->private_data);
	try {
		// Rebuilt per call rather than moved out of the struct, so a second get_schema is well-defined.
		auto props = fabricator::BoundaryClientProperties(*st->conn->context);
		ArrowConverter::ToArrowSchema(out, st->types, st->names, props);
		return 0;
	} catch (std::exception &e) {
		st->last_error = e.what();
		return 1;
	}
}

int HostQuerySchemaGetNext(ArrowArrayStream *stream, ArrowArray *out) {
	std::memset(out, 0, sizeof(*out)); // a zeroed (released) ArrowArray is the end marker
	return 0;
}

const char *HostQuerySchemaGetLastError(ArrowArrayStream *stream) {
	auto *st = static_cast<HostQuerySchemaStream *>(stream->private_data);
	return st->last_error.empty() ? nullptr : st->last_error.c_str();
}

void HostQuerySchemaRelease(ArrowArrayStream *stream) {
	delete static_cast<HostQuerySchemaStream *>(stream->private_data);
	stream->release = nullptr;
}

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
	auto *st = static_cast<HostQueryStream *>(stream->private_data);
	// ⚠ ORDER IS LOAD-BEARING on a PINNED connection: the whole point of the open_streams guard is that no
	// query runs while a streaming result of ours is alive, so the result must be GONE before the slot is
	// released. Member destruction would do it in the right order too, but only by accident of declaration
	// order — doing it explicitly is what keeps that true after the next edit to the struct.
	if (st && st->pin) {
		st->result.reset();
		st->prepared.reset();
		st->pin->open_streams.fetch_sub(1);
	}
	delete st;
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

	// ⚠⚠ DESCRIBE WITHOUT EXECUTING. Without the schema_factory below, PopulateReturnSchema runs `factory`
	// to learn the output columns and the scan then runs it AGAIN for the data — so ONE call executed the
	// statement TWICE. Harmless-looking for a SELECT (wasted work); a silent duplicate for anything with a
	// side effect: measured, one `fabricator_host_query('INSERT INTO audit VALUES (1)')` left TWO rows, and
	// DDL failed its second run with "already exists" from a statement issued once.
	//
	// This is the same defect that was fixed for `fabricator_query` in 0acd679, and the comment that used to
	// sit here appealed to that very function ("like the other fabricator table functions") — a
	// justification by analogy that aged the moment the analogue was fixed.
	//
	// The fix is cheaper here than it was there: `fabricator_query` needed the provider to describe remote
	// SQL (sp_describe_first_result_set), whereas DuckDB describes its OWN statements natively — a PREPARED
	// statement carries the bound plan's result types and names, and preparing binds without running.
	{
		auto describe_conn = make_uniq<Connection>(*db);
		ApplyHostQuerySession(*describe_conn, &session); // resolve names/times as the execution will
		auto prepared = describe_conn->Prepare(sql);
		// ⚠ FALLING BACK RATHER THAN FAILING, deliberately: `SendQuery` accepts shapes `Prepare` refuses —
		// several statements in one string, most obviously ("Cannot prepare multiple statements at once").
		// Those keep working exactly as before, double execution included; what would be worse is turning a
		// working call into a bind error. A genuinely invalid statement also lands here and then fails in
		// the factory, i.e. where it failed before. See docs/host-query.md.
		if (!prepared->HasError() && !prepared->GetTypes().empty()) {
			auto types = prepared->GetTypes();
			auto col_names = prepared->GetNames();
			// ⚠ The factory opens and CONFIGURES its own connection rather than capturing one: the props
			// derived from it must match the execute path's, and a TimeZone difference would change a
			// TIMESTAMPTZ column's Arrow type — so the same session is applied here as there.
			bind_data->schema_factory = [db, session, types, col_names](ArrowArrayStream &out) {
				auto *st = new HostQuerySchemaStream();
				st->conn = make_uniq<Connection>(*db);
				ApplyHostQuerySession(*st->conn, &session);
				st->types = types;
				st->names = col_names;
				st->stream.get_schema = HostQuerySchemaGetSchema;
				st->stream.get_next = HostQuerySchemaGetNext;
				st->stream.get_last_error = HostQuerySchemaGetLastError;
				st->stream.release = HostQuerySchemaRelease;
				st->stream.private_data = st;
				out = st->stream;
			};
		}
	}
	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// ============================================================================================================
// fabricator_host_exec(sql) — DDL / DML on a fresh host connection, reporting the affected-row count.
//
// THE SIBLING OF fabricator_host_query, and it exists because that one has to DESCRIBE. A table function must
// declare its output columns at BIND, and for arbitrary SQL that means asking DuckDB to prepare the statement
// — which cannot be done for several statements in one string, and cannot be done at all when a later
// statement's schema depends on an earlier one's effects (`CREATE TABLE t …; SELECT * FROM t`). Those fall
// back to describing by EXECUTING, so they run twice.
//
// exec sidesteps the whole question: its output schema is FIXED — one BIGINT — so there is nothing to
// describe, nothing to prepare, and the statement runs EXACTLY ONCE whatever it is. That is the point of the
// function, not a side benefit.
//
// ⚠ THE RESULT IS NORMALISED, NOT PASSED THROUGH. Whatever the statement returns is discarded; the single
// row is the engine's own affected-row count when it reports one and 0 otherwise. That is what makes the
// fixed schema honest: without it a `SELECT` here would declare one BIGINT and deliver something else, and
// the scan reads batches through converters built from the DECLARED schema.
// ============================================================================================================

struct HostExecStream {
	unique_ptr<Connection> conn; // kept alive: BoundaryClientProperties and the appender need its context
	duckdb::vector<LogicalType> types;
	duckdb::vector<string> names;
	int64_t affected = 0;
	bool emitted = false;
	string last_error;
	ArrowArrayStream stream {};
};

int HostExecGetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *st = static_cast<HostExecStream *>(stream->private_data);
	try {
		auto props = fabricator::BoundaryClientProperties(*st->conn->context);
		ArrowConverter::ToArrowSchema(out, st->types, st->names, props);
		return 0;
	} catch (std::exception &e) {
		st->last_error = e.what();
		return 1;
	}
}

int HostExecGetNext(ArrowArrayStream *stream, ArrowArray *out) {
	auto *st = static_cast<HostExecStream *>(stream->private_data);
	std::memset(out, 0, sizeof(*out));
	try {
		if (st->emitted) {
			return 0; // one row, once — a zeroed (released) ArrowArray is the end marker
		}
		DataChunk chunk;
		chunk.Initialize(Allocator::Get(*st->conn->context), st->types);
		chunk.SetValue(0, 0, Value::BIGINT(st->affected));
		chunk.SetCardinality(1);
		auto props = fabricator::BoundaryClientProperties(*st->conn->context);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(*st->conn->context, st->types);
		ArrowAppender appender(st->types, 1, props, extension_types);
		appender.Append(chunk, 0, chunk.size(), chunk.size());
		*out = appender.Finalize();
		st->emitted = true;
		return 0;
	} catch (std::exception &e) {
		st->last_error = e.what();
		return 1;
	}
}

const char *HostExecGetLastError(ArrowArrayStream *stream) {
	auto *st = static_cast<HostExecStream *>(stream->private_data);
	return st->last_error.empty() ? nullptr : st->last_error.c_str();
}

void HostExecRelease(ArrowArrayStream *stream) {
	delete static_cast<HostExecStream *>(stream->private_data);
	stream->release = nullptr;
}

//! Runs `sql` for effect on a FRESH connection and returns the affected-row count.
//!
//! ⚠ THE ONE EXECUTION PATH, shared by both surfaces — the table function's stream and the scalar. Two
//! copies would be two chances for the count, the session handling or the error prefix to drift apart, and
//! nothing would notice until someone compared them.
int64_t RunHostExec(DatabaseInstance &db, const string &sql, const HostQuerySession *session,
                    unique_ptr<Connection> *out_conn) {
	auto conn = make_uniq<Connection>(db); // FRESH connection: own context/transaction, as host_query does
	ApplyHostQuerySession(*conn, session);

	// SendQuery rather than Query: several statements in one string all run (which is the case exec exists
	// for), and a caller who passes a SELECT anyway streams rather than materialising the whole thing.
	auto result = conn->SendQuery(sql);
	if (result->HasError()) {
		throw IOException("fabricator_host_exec: " + result->GetError());
	}
	int64_t affected = 0;
	// ⚠ ASKED OF THE STATEMENT, not inferred from its column types. `StatementReturnType::CHANGED_ROWS` is
	// DuckDB's own "this result IS a row count", so a DML reports its count and a SELECT that happens to
	// return one BIGINT column does not get its first value mistaken for one.
	// ⚠ For several statements, SendQuery returns the LAST one's result — so the count is the LAST
	// statement's, which is the only one that could be meant.
	if (result->properties.return_type == StatementReturnType::CHANGED_ROWS) {
		auto chunk = result->Fetch();
		if (chunk && chunk->size() > 0) {
			auto v = chunk->GetValue(0, 0);
			if (!v.IsNull()) {
				affected = v.GetValue<int64_t>();
			}
		}
	}
	if (out_conn) {
		*out_conn = std::move(conn); // the stream needs a live context to build its Arrow batch from
	}
	return affected;
}

//! Runs `sql` for effect and yields ONE BIGINT row: the affected-row count.
void MakeHostExecStream(DatabaseInstance &db, const string &sql, ArrowArrayStream &out,
                        const HostQuerySession *session) {
	unique_ptr<Connection> conn;
	auto affected = RunHostExec(db, sql, session, &conn);
	auto *st = new HostExecStream();
	st->conn = std::move(conn);
	st->types = {LogicalType::BIGINT};
	st->names = {"affected"};
	st->affected = affected;
	st->stream.get_schema = HostExecGetSchema;
	st->stream.get_next = HostExecGetNext;
	st->stream.get_last_error = HostExecGetLastError;
	st->stream.release = HostExecRelease;
	st->stream.private_data = st;
	out = st->stream;
}

//! Captures the caller's session at bind/execute time — shared by both exec surfaces.
HostQuerySession CaptureSession(ClientContext &context) {
	HostQuerySession session;
	session.search_path = ClientData::Get(context).catalog_search_path->GetSetPaths();
	Value tz_value;
	if (context.TryGetCurrentSetting("TimeZone", tz_value) && !tz_value.IsNull()) {
		session.time_zone = tz_value.ToString();
	}
	return session;
}

// --- fabricator_host_exec(sql) -> BIGINT, the SCALAR spelling -------------------------------------------
//
// The same function as the table form, for symmetry with fabricator_exec, which is also a VOLATILE scalar.
//
// ⚠⚠ VOLATILE IS LOAD-BEARING AND IT IS STILL NOT THE SAFER SHAPE. Volatile stops DuckDB constant-folding
// the call at PLAN time — without it a `SELECT fabricator_host_exec('CREATE …')` would run during binding,
// firing on an EXPLAIN that executes nothing (measured for bind-time host queries generally; see
// docs/fluid-templating.md §8.3). What volatile does NOT stop is PER-ROW evaluation: in a row context the
// statement runs once per row, so `SELECT fabricator_host_exec('…') FROM range(1000)` executes it a
// thousand times. The TABLE form runs once per scan and is the one to reach for with DDL; this exists
// because `SELECT f('…')` is what people write and fabricator_exec set that precedent.
static void HostExecScalarFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	auto &context = state.GetContext();
	auto session = CaptureSession(context);
	auto count = args.size();
	result.SetVectorType(VectorType::FLAT_VECTOR);
	auto result_data = FlatVector::GetData<int64_t>(result);
	auto &validity = FlatVector::Validity(result);
	for (idx_t i = 0; i < count; i++) {
		auto sql_value = args.GetValue(0, i);
		if (sql_value.IsNull()) {
			validity.SetInvalid(i); // a NULL statement is NULL, not zero rows affected
			continue;
		}
		result_data[i] = RunHostExec(*context.db, StringValue::Get(sql_value), &session, nullptr);
	}
}

// Bind: the output schema is FIXED, so it is declared outright — no prepare, no execution, nothing to get
// wrong. This is why exec runs its statement exactly once where query cannot always.
unique_ptr<FunctionData> HostExecBind(ClientContext &context, TableFunctionBindInput &input,
                                      vector<LogicalType> &return_types, vector<string> &names) {
	auto sql = input.inputs[0].GetValue<string>();
	auto db = context.db;
	auto bind_data = make_uniq<fabricator::ArrowStreamBindData>();
	auto session = CaptureSession(context);
	bind_data->factory = [db, sql, session](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		MakeHostExecStream(*db, sql, out, &session);
	};
	// The fixed schema, handed over without running anything. PopulateReturnSchema still builds the Arrow
	// converters from it, so the declared and delivered schemas are the same object by construction.
	bind_data->schema_factory = [db](ArrowArrayStream &out) {
		auto *st = new HostQuerySchemaStream();
		// No session applied: one BIGINT has no timezone or encoding sensitivity, so any live context
		// describes it identically. The connection exists only to give ToArrowSchema a context to hold.
		st->conn = make_uniq<Connection>(*db);
		st->types = {LogicalType::BIGINT};
		st->names = {"affected"};
		st->stream.get_schema = HostQuerySchemaGetSchema;
		st->stream.get_next = HostQuerySchemaGetNext;
		st->stream.get_last_error = HostQuerySchemaGetLastError;
		st->stream.release = HostQuerySchemaRelease;
		st->stream.private_data = st;
		out = st->stream;
	};
	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}


// ── Registering a named Arrow input as a TEMPORARY view ────────────────────────────────────────────────
//
// This is `duckdb_arrow_scan` with ONE argument changed. That C-API entry ends in
// `CreateView(name, /*replace=*/true, /*temporary=*/FALSE)` (duckdb/src/main/capi/arrow-c.cpp:425), so the
// view it makes is an ordinary CATALOG view: visible to every other connection on the database, and
// OUTLIVING the connection that made it — including the stream whose raw pointer it stores.
//
// ⚠⚠ THAT IS NOT A TIDINESS POINT, IT IS A CRASH — MEASURED 2026-09-03 with a positive control. On a codec
// Delta scan under exact pushdown (`Fabricator.Delta` Debug: `mode=Exact native_filter="g"=3`, which is what
// proves the path ran), three filtered SELECTs left THREE views behind in the user's own `memory.main` —
// `__fabricator_scan_batch_1..3`, `temporary = false`, one per statement and unbounded — and
// `SELECT * FROM __fabricator_scan_batch_1` afterwards SEGFAULTED the process, because the stream that view
// points at was released when its own query finished. A TEMPORARY view is scoped to the connection that
// made it, so the whole class goes away by construction: nothing reaches the shared catalog, nothing
// outlives the stream, and nothing has to be cleaned up afterwards.
//
// ⚠ The factory pair below is duplicated from that same upstream file, where it lives in an ANONYMOUS
// namespace and is therefore unlinkable from an extension. `EmptyStreamRelease` is not laziness: ownership
// of the input stays with OwnedArrowInputs, which releases it when the result stream is released, so DuckDB
// must not release it on our behalf.
void FabricatorEmptyStreamRelease(ArrowArrayStream *stream) {
	stream->release = nullptr;
}

void FabricatorEmptySchemaRelease(ArrowSchema *schema) {
	schema->release = nullptr;
}

unique_ptr<ArrowArrayStreamWrapper> FabricatorArrowStreamProduce(uintptr_t stream_factory_ptr,
                                                                 ArrowStreamParameters &) {
	auto *stream = reinterpret_cast<ArrowArrayStream *>(stream_factory_ptr);
	auto ret = make_uniq<ArrowArrayStreamWrapper>();
	ret->arrow_array_stream = *stream;
	ret->arrow_array_stream.release = FabricatorEmptyStreamRelease;
	return ret;
}

void FabricatorArrowStreamGetSchema(ArrowArrayStream *stream, ArrowSchema &schema) {
	stream->get_schema(stream, &schema);
	// Nulling the ROOT release stops ArrowSchemaWrapper's destructor freeing a schema the caller owns; the
	// children are covered by the backup dance below. (Upstream does exactly this, for the same reason.)
	schema.release = nullptr;
}

// Registers `stream` as a TEMPORARY view named `name` on `conn`. Returns false on any failure, which is the
// same signal `duckdb_arrow_scan`'s DuckDBError gave the caller.
bool RegisterArrowInputView(Connection &conn, const string &name, ArrowArrayStream *stream) {
	// Back up and neutralise the IMMEDIATE children's release callbacks so DuckDB cannot release the
	// caller's schema children on its behalf. Arrow releases are not recursive, so immediate children are
	// the whole job — upstream's comment, and its dance, reproduced verbatim.
	ArrowSchema schema;
	if (stream->get_schema(stream, &schema) != 0) {
		return false;
	}
	const idx_t n_children = schema.n_children > 0 ? static_cast<idx_t>(schema.n_children) : 0;
	vector<void (*)(ArrowSchema *)> release_fns(n_children);
	for (idx_t i = 0; i < n_children; i++) {
		release_fns[i] = schema.children[i]->release;
		schema.children[i]->release = FabricatorEmptySchemaRelease;
	}
	bool ok = true;
	try {
		conn.TableFunction("arrow_scan",
		                   {Value::POINTER(reinterpret_cast<uintptr_t>(stream)),
		                    Value::POINTER(reinterpret_cast<uintptr_t>(FabricatorArrowStreamProduce)),
		                    Value::POINTER(reinterpret_cast<uintptr_t>(FabricatorArrowStreamGetSchema))})
		    ->CreateView(name, /*replace=*/true, /*temporary=*/true);
	} catch (...) {
		ok = false;
	}
	for (idx_t i = 0; i < n_children; i++) {
		schema.children[i]->release = release_fns[i];
	}
	return ok;
}

} // namespace


void MakeHostQueryStream(DatabaseInstance &db, const string &sql, ArrowArrayStream *params,
                         const vector<HostQueryInput> &inputs, ArrowArrayStream &out,
                         shared_ptr<ClientContext> *out_context, const HostQuerySession *session,
                         shared_ptr<HostConnection> pinned) {
	// ⚠⚠ A PINNED connection is REFUSED a named input — and the reason this shipped with (v84: "a
	// connection-scoped view … would collide") was MEASURED FALSE on 2026-09-03 on BOTH counts: the view
	// was created with `replace: true`, so a re-registration REPLACES, and it was a CATALOG view, not a
	// connection-scoped one. Inputs are TEMPORARY views now (see RegisterArrowInputView), which makes a pin
	// the RIGHT scope rather than the wrong one, so this refusal is LIFTABLE.
	// ⚠ It is NOT a one-line deletion, which is why it still stands: `OwnedArrowInputs` is owned by the
	// RESULT STREAM, and on a pinned connection the view would outlive that stream — the same
	// use-after-free one layer over. Move the ownership onto the pin first; docs/fluid-templating.md §17.10.
	// Checked BEFORE anything is adopted, so nothing leaks on the refusal.
	if (pinned && !inputs.empty()) {
		throw IOException("fabricator host_query: named Arrow inputs are not supported on a pinned host "
		                  "connection (an arrow_scan view is connection-scoped, so it would outlive this "
		                  "call). Use a fresh-connection query for inputs.");
	}
	// ⚠⚠ ONE STREAMING RESULT AT A TIME. DuckDB's every query path calls InitialCleanup, which closes the
	// connection's active streaming result SILENTLY — the abandoned stream reports end-of-stream, so the
	// earlier query's remaining rows are LOST with no error. Refusing is the only honest answer; a fresh
	// connection is unaffected because it has one of its own. Not a race guard (a Connection is
	// single-threaded by contract) — it catches a caller HOLDING a stream, which is a sequencing mistake.
	if (pinned && pinned->open_streams.load() > 0) {
		throw IOException("fabricator host_query: this pinned host connection still has an open result "
		                  "stream; release (or fully read) it before running another statement on the same "
		                  "connection. DuckDB closes a connection's active streaming result when the next "
		                  "statement starts, which would silently truncate it.");
	}
	// FRESH connection (own context/transaction, see header) unless the caller pinned one (ABI v84), in
	// which case its TEMP catalog + session settings are exactly what it is here for.
	shared_ptr<Connection> conn = pinned ? pinned->conn : shared_ptr<Connection>(new Connection(db));
	if (out_context) {
		*out_context = conn->context; // for out-of-band interruption (host_query_interrupt, ABI v66)
	}
	// Adopt the caller's session state, when one was supplied (the table function supplies it; the C# host
	// service deliberately does not — provider-generated SQL must not depend on what the user last USEd).
	// ⚠ Skipped for a pinned connection: it was applied ONCE at open, and re-applying per query would undo
	// a `SET` the caller performed THROUGH the pin, which is one of the things pinning is for.
	if (!pinned) {
		ApplyHostQuerySession(*conn, session);
	}
	// Register each named Arrow input as a TEMPORARY, connection-scoped view (data-in) — see
	// RegisterArrowInputView, which is duckdb_arrow_scan with `temporary` flipped and says why. We ADOPT each
	// stream first (see OwnedArrowInputs): the view keeps the RAW POINTER and the query below is LAZY, so the
	// caller's own allocation must not be what it points at. On any throw below, the local holder releases
	// them.
	auto owned_inputs = make_uniq<OwnedArrowInputs>();
	for (auto &in : inputs) {
		if (!RegisterArrowInputView(*conn, in.name, owned_inputs->Adopt(in.stream))) {
			throw IOException("fabricator_host_query: failed to register input view '" + in.name + "'");
		}
	}
	unique_ptr<QueryResult> result;
	unique_ptr<PreparedStatement> prepared;
	if (params) {
		// Read the 1-row Arrow params batch into values and bind them via a prepared statement — POSITIONALLY
		// by column order, or BY NAME when the batch names its columns (see below).
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
		// ⚠⚠ THE STATEMENT DECIDES, NOT THE BATCH — bind by name only when the batch's column names are
		// EXACTLY the statement's own parameter names. The names were always crossing in the Arrow schema;
		// nothing read them until named binding landed, which is why this overload was positional-only for
		// its whole life.
		// ⚠ The first rule tried here was "every column has a non-empty name", and it was WRONG in a way a
		// local run did not show: an Arrow field practically always HAS a name, so that test is nearly
		// always true and it silently switched EVERY existing caller to name-binding. `cf_host_param` sends
		// columns p0/p1 against positional `?` placeholders and broke with "Values were not provided for
		// the following prepared statement parameters: 1, 2" — caught by the service tier, not by review.
		// Matching against `named_param_map` cannot make that mistake: a `?` statement names its parameters
		// "1", "2", … so p0/p1 do not match and it stays positional.
		// ⚠ Duplicate names are NOT silently collapsed: case_insensitive_map_t would keep the LAST value and
		// bind a parameter the caller never intended, so they are refused by name.
		const auto &names = reader.Names();
		const auto &declared = prepared->named_param_map;
		// ⚠ A SUBSET is enough, deliberately: requiring declared.size() == values.size() would send a call
		// that supplies only some of the statement's parameters down the POSITIONAL path, where DuckDB then
		// reports every parameter as missing ("… parameters: a, b") including the one that WAS supplied.
		// Binding by name and letting DuckDB name only what is genuinely absent is the better error.
		bool by_name = !values.empty() && names.size() == values.size();
		for (idx_t c = 0; by_name && c < names.size(); c++) {
			by_name = !names[c].empty() && declared.find(names[c]) != declared.end();
		}
		if (by_name) {
			case_insensitive_map_t<BoundParameterData> named;
			for (idx_t c = 0; c < values.size(); c++) {
				if (named.find(names[c]) != named.end()) {
					throw IOException("fabricator_host_query: duplicate parameter name '" + names[c] +
					                  "' in the bound parameters");
				}
				named.emplace(names[c], BoundParameterData(values[c]));
			}
			// Streaming result: it references the prepared statement, so the holder keeps `prepared` alive.
			result = prepared->Execute(named, /*allow_stream_result=*/true);
		} else {
			result = prepared->Execute(values, /*allow_stream_result=*/true);
		}
	} else {
		result = conn->SendQuery(sql); // streaming (lazy Fetch) — bounded memory for large results
	}
	if (result->HasError()) { // bind/plan errors surface here; runtime errors surface during Fetch (get_next)
		throw IOException("fabricator_host_query: " + result->GetError());
	}
	auto *st = new HostQueryStream();
	st->inputs = std::move(owned_inputs); // the views reference these; they outlive `result` (see the struct)
	st->pin = std::move(pinned); // null unless PINNED; keeps the pin alive for this stream's life
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
	if (st->pin) {
		// ⚠ TAKEN LAST, and both halves of that matter. Only now has the statement SUCCEEDED — counting
		// earlier would wedge the connection on the first failed query, refusing every later one. And
		// only now is there nothing left that can throw: an allocation failing between the increment and
		// the handover below would leave a slot taken by a stream the caller never receives, i.e. a
		// permanently unusable connection, where leaking `st` (which the raw `new` above already risks)
		// costs nothing extra.
		st->pin->open_streams.fetch_add(1);
	}
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
                         FabricatorHandle client_context, FabricatorHandle connection, ArrowArrayStream *out,
                         void **out_interrupt, char **err) {
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
		// ⚠ The caller's session, when it gave us one (ABI v83). Captured HERE, on the caller's LIVE
		// context — the handle is valid only for the duration of this synchronous call — and applied to
		// the fresh connection by MakeHostQueryStream, which is the same pair the SQL surface uses, so
		// `fabricator_host_query` and a plugin's Host.Query cannot resolve names or times differently. A 0
		// handle means a clean session, which is what every caller got before v83.
		HostQuerySession session;
		const HostQuerySession *session_ptr = nullptr;
		if (client_context) {
			session = CaptureSession(*reinterpret_cast<ClientContext *>(client_context));
			session_ptr = &session;
		}
		// The PINNED connection, when the caller opened one (ABI v84). A shared_ptr COPY, so the
		// statement's result stream keeps it alive even if the managed side closes the handle first.
		shared_ptr<HostConnection> pinned;
		if (connection) {
			pinned = *reinterpret_cast<shared_ptr<HostConnection> *>(connection);
		}
		shared_ptr<ClientContext> ctx;
		MakeHostQueryStream(*g_host_db, sql ? string(sql) : string(), params, in, *out,
		                    out_interrupt ? &ctx : nullptr, session_ptr, std::move(pinned));
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

// host_connection_open — hand C# a connection that OUTLIVES a single host_query call (ABI v84), so a
// caller can CREATE TEMP TABLE in one statement and read it in the next. The handle owns a
// shared_ptr<HostConnection>; result streams opened on it hold their own copies, so closing the handle
// while a stream is outstanding destroys the Connection only once the last of them is released.
//
// ⚠ The caller's session is applied HERE, once, rather than per query — a `SET` the caller then performs
// THROUGH the pin sticks for the connection's life, which is one of the reasons to pin at all.
int32_t HostConnectionOpenService(FabricatorHandle client_context, FabricatorHandle *out_connection, char **err) {
	try {
		if (!g_host_db) {
			throw IOException("fabricator host_connection_open: host database not available");
		}
		if (!out_connection) {
			throw IOException("fabricator host_connection_open: out_connection is null");
		}
		auto pin = make_shared_ptr<HostConnection>();
		pin->conn = shared_ptr<Connection>(new Connection(*g_host_db));
		if (client_context) {
			auto session = CaptureSession(*reinterpret_cast<ClientContext *>(client_context));
			ApplyHostQuerySession(*pin->conn, &session);
		}
		*out_connection = reinterpret_cast<FabricatorHandle>(new shared_ptr<HostConnection>(std::move(pin)));
		return FABRICATOR_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

// host_connection_close — release the caller's reference. The Connection (and with it the TEMPORARY catalog)
// dies here unless a result stream opened on it is still alive, which is exactly why the handle owns a
// shared_ptr rather than the object.
void HostConnectionCloseService(FabricatorHandle connection) {
	if (!connection) {
		return;
	}
	delete reinterpret_cast<shared_ptr<HostConnection> *>(connection);
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
	// The v84 pinned-connection pair: several host_query calls on ONE connection (TEMP tables, settings).
	fabricator::SetHostConnectionService(HostConnectionOpenService, HostConnectionCloseService);
	fabricator::SetHostLog(HostLogService);            // forward ILogger events into DuckDB's internal logging
	DBConfig::GetConfig(loader.GetDatabaseInstance()).replacement_scans.emplace_back(NamedSourceReplacement);
	TableFunction fn("fabricator_host_query", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan, HostQueryBind,
	                 fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	// Declares batch-index support, which is what routes an order-preserving plan to the PARALLEL
	// PhysicalBufferedBatchCollector instead of the single-threaded PhysicalBufferedCollector.
	// See ArrowStreamGetPartitionData + docs/scan-concurrency.md.
	fn.get_partition_data = fabricator::ArrowStreamGetPartitionData;
	loader.RegisterFunction(fn);

	// fabricator_host_exec(sql) — the DDL/DML sibling. Fixed one-BIGINT output, so no describe is needed and
	// the statement runs EXACTLY ONCE, including several statements in one string (see docs/host-query.md).
	TableFunction exec_fn("fabricator_host_exec", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan,
	                      HostExecBind, fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(exec_fn);

	// The SCALAR spelling of the same function, for symmetry with fabricator_exec (also a VOLATILE scalar).
	// ⚠ VOLATILE stops constant folding at PLAN time — without it a `SELECT fabricator_host_exec('CREATE …')`
	// would run during binding and fire on an EXPLAIN. It does NOT stop per-ROW evaluation; the TABLE form is
	// the one to reach for with DDL. See docs/host-query.md.
	ScalarFunction exec_scalar("fabricator_host_exec", {LogicalType::VARCHAR}, LogicalType::BIGINT,
	                           HostExecScalarFunction);
	exec_scalar.stability = FunctionStability::VOLATILE;
	loader.RegisterFunction(exec_scalar);

	TableFunction scan("fabricator_scan", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan, NamedScanBind,
	                   fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	// Declares batch-index support, which is what routes an order-preserving plan to the PARALLEL
	// PhysicalBufferedBatchCollector instead of the single-threaded PhysicalBufferedCollector.
	// See ArrowStreamGetPartitionData + docs/scan-concurrency.md.
	scan.get_partition_data = fabricator::ArrowStreamGetPartitionData;
	loader.RegisterFunction(scan);
}

} // namespace duckdb
