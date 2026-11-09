// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         Fabricator — Arrow stream ingestion (impl)
//===----------------------------------------------------------------------===//

#include "duckdb/common/enum_util.hpp"
#include "duckdb/common/printer.hpp"
#include "fabricator/arrow_ingest.hpp"

#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "fabricator/scan_wait.hpp"
#include "duckdb/common/allocator.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/types/data_chunk.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/database.hpp" // DatabaseInstance::NumberOfThreads — the scan's MaxThreads
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp" // the schema-change error names the table
#include "duckdb/catalog/catalog.hpp"
#include "duckdb/planner/expression/bound_columnref_expression.hpp"
#include "duckdb/common/vector/struct_vector.hpp" // 2.0 split vector.hpp
#include "duckdb/planner/table_filter.hpp"
#include "duckdb/execution/expression_executor.hpp"
// 2.0 split these out of table_filter.hpp; without them DynamicFilterData / TableFilterSet are
// incomplete types here (the old header only forward-declares them now).
#include "duckdb/planner/table_filter_set.hpp"
#include "duckdb/planner/filter/table_filter_functions.hpp"
#include "duckdb/common/vector_operations/vector_operations.hpp" // the bloom filter's key hash
#include "duckdb/planner/expression_iterator.hpp"
#include "duckdb/planner/expression/bound_conjunction_expression.hpp"
#include "duckdb/planner/expression/bound_function_expression.hpp"
#include "duckdb/planner/filter/optional_filter.hpp"
#include "duckdb/planner/filter/dynamic_filter.hpp"
#include "duckdb/planner/filter/conjunction_filter.hpp"
#include "duckdb/planner/filter/constant_filter.hpp"
#include "duckdb/planner/filter/in_filter.hpp"
#include "duckdb/planner/filter/struct_filter.hpp"
#include <unordered_set>
#include <yyjson.h>
#include "duckdb/planner/filter/expression_filter.hpp"
#include "duckdb/planner/expression/bound_constant_expression.hpp"
#include "duckdb/planner/expression/bound_operator_expression.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"
#include "duckdb/function/scalar/struct_functions.hpp" // struct_extract / struct_extract_at names
#include "duckdb/function/scalar/tablefilter_functions.hpp" // the prefix-range filter's name
#include "duckdb/transaction/meta_transaction.hpp"

#include <algorithm>
#include <mutex>
#include <unordered_map>

namespace fabricator {

using namespace duckdb;

static void UnregisterJoinFilters(const vector<idx_t> &ids);

//! A STRUCT field DuckDB pushed down into the scan (2.0 "pushdown extract", which the scan opts into through
//! ArrowStreamSupportsPushdownExtract): the scan emits the FIELD in that output position, not the column. The
//! provider still returns the whole column; the ingest takes the field out of it. Why it matters: a join on
//! `t.s.a` only gets DuckDB's join filters when `s.a` is a scan column of its own (the join filter pushdown
//! takes plain columns only), so without this a join on a struct field read the whole probe table.
struct ExtractPath {
	//! the path below the column: child index and name per level, and whether that level's struct is unnamed
	vector<idx_t> indexes;
	vector<string> names;
	vector<bool> unnamed;
	//! the field's own type, and the type the scan must emit: the field's, or a CAST DuckDB folded on top of it
	LogicalType field_type;
	LogicalType scan_type;

	bool Active() const {
		return !indexes.empty();
	}
	bool HasCast() const {
		return scan_type != field_type;
	}
};

//! Reads a pushed-down field's path off its ColumnIndex; false for a plain column (or a shape the scan never
//! declared, such as a VARIANT field, which travels by name rather than by child index).
static bool ReadExtractPath(const ColumnIndex &index, const LogicalType &column_type, ExtractPath &out) {
	if (!index.IsPushdownExtract()) {
		return false;
	}
	reference<const ColumnIndex> cur(index);
	LogicalType type = column_type;
	while (cur.get().IsPushdownExtract()) {
		if (type.id() != LogicalTypeId::STRUCT || cur.get().ChildIndexCount() != 1) {
			return false;
		}
		auto &child = cur.get().GetChildIndex(0);
		if (!child.HasPrimaryIndex()) {
			return false;
		}
		auto idx = child.GetPrimaryIndex();
		auto &children = StructType::GetChildTypes(type);
		if (idx >= children.size()) {
			return false;
		}
		out.indexes.push_back(idx);
		out.names.push_back(children[idx].first.GetIdentifierName());
		out.unnamed.push_back(StructType::IsUnnamed(type));
		auto next = children[idx].second;
		type = next;
		cur = child;
	}
	out.field_type = type;
	out.scan_type = cur.get().HasType() ? cur.get().GetType() : type;
	return true;
}

//! The field as a DuckDB expression over the quoted column, for the inner query's WHERE (struct_extract by name,
//! never `"s"."a"`, which the binder could read as table s, column a).
static string ExtractPathSql(const string &qcol, const ExtractPath &path) {
	string sql = qcol;
	for (idx_t i = 0; i < path.indexes.size(); i++) {
		if (path.unnamed[i]) {
			sql = "struct_extract_at(" + sql + ", " + to_string(path.indexes[i] + 1) + ")";
		} else {
			string quoted = "'";
			for (char c : path.names[i]) {
				quoted += c;
				if (c == '\'') {
					quoted += '\'';
				}
			}
			quoted += "'";
			sql = "struct_extract(" + sql + ", " + quoted + ")";
		}
	}
	if (path.HasCast()) {
		sql = "CAST(" + sql + " AS " + path.scan_type.ToString() + ")";
	}
	return sql;
}

namespace {

struct ArrowStreamGlobalState : public GlobalTableFunctionState {
	//! The scan's live table filters as ONE predicate over its output columns, re-applied to every chunk when the
	//! provider's rendering of them is a superset (ArrowStreamBindData::reapply_filters); null otherwise.
	unique_ptr<Expression> reapply_filter;
	//! The live data stream for this scan (owned; released in the destructor).
	ArrowArrayStream stream {};
	bool stream_initialized = false;
	bool done = false;
	//! The ClientContext this scan executes for — valid for the whole query. Re-established as the host-FS
	//! opener before every pull, because a pull is NOT otherwise a crossing: it runs on a worker thread whose
	//! ambient opener holds whatever the last crossing ON THAT THREAD left there, and after a nested host query
	//! that is a destroyed context (measured: every Delta scan after ducklake_delta_sync failed in
	//! fs_open_read). See docs/ducklake-catalog.md §11.6.
	ClientContext *context = nullptr;
	mutex main_mutex;
	idx_t batch_index = 0;

	//! The ACTUAL result schema of this scan (= the projected subset when projection
	//! was pushed). Declared before scan_arrow_table so the converters (which point
	//! into this schema) are destroyed first.
	ArrowSchemaWrapper scan_schema_root;
	//! Per-scan Arrow column converters built from scan_schema_root.
	ArrowTableSchema scan_arrow_table;
	//! DuckDB types of the result columns, in result order.
	vector<LogicalType> scan_types;

	//! The columns DuckDB requested for the output (real indices + ROW_ID).
	vector<column_t> output_column_ids;
	//! For each output column: its position in the (subset) result, or -1 for ROW_ID.
	vector<int64_t> output_source_pos;
	//! For each output column: the pushed-down struct field it emits (inactive = the column itself).
	vector<ExtractPath> output_extract;
	//! Positions (in the result) of the rowid source columns.
	vector<idx_t> rowid_source_pos;

	//! The filter-constants producer whose Arrow stream was handed to the provider for THIS execution (null
	//! when nothing was pushed). Owned here because the managed consumer may release that stream LAZILY: a C#
	//! binding written as an async iterator disposes it on its first MoveNext, i.e. inside get_next — long
	//! after InitGlobal returned. A producer scoped to the factory call is therefore released after it dies
	//! (ArrowProducer::Stream() hands out a pointer INTO the object, and Release() locks its mutex ⇒
	//! use-after-free). macOS is the only platform that faults on it: Apple's pthread_mutex_lock validates the
	//! signature and returns EINVAL, which std::mutex::lock turns into a throw → abort; glibc and Windows lock
	//! a destroyed mutex silently. Destroyed AFTER the destructor body below releases `stream` (a destructor
	//! body runs before member destructors), so a dispose triggered BY that release still sees a live producer.
	unique_ptr<fabricator::ArrowProducer> filter_value_producer;

	//! Shared with every AsyncTask parked on this scan, because one may OUTLIVE this state: the query can
	//! tear down (a satisfied LIMIT) while a wait is in flight. Shutdown() below wakes whoever is left.
	shared_ptr<fabricator::ScanWaitState> wait_state = make_shared_ptr<fabricator::ScanWaitState>();

	~ArrowStreamGlobalState() override {
		wait_state->Shutdown();
		if (stream_initialized && stream.release) {
			stream.release(&stream);
			stream.release = nullptr;
		}
		// After the release: no inner query can still evaluate them.
		UnregisterJoinFilters(join_filter_ids);
	}

	//! The hash-join filters this scan's inner query evaluates through fabricator_join_filter (registered while
	//! the live filters were rendered).
	vector<idx_t> join_filter_ids;

	//! Threads this scan may use, captured at InitGlobal (MaxThreads() gets no context). = the database's
	//! thread count, i.e. whatever `SET threads` says — a GLOBAL-only setting ("option \"threads\" cannot be
	//! set locally"), so the host query's fresh connection shares this scheduler and nothing needs
	//! propagating to it.
	idx_t scan_max_threads = 1;

	idx_t MaxThreads() const override {
		// The old answer was a hardcoded 1, justified as "a single Arrow C stream is consumed serially" —
		// TRUE of get_next, but too strong a conclusion. DuckDB's OWN arrow scan declares NumberOfThreads()
		// and serializes only the PULL: ArrowScanParallelStateNext takes the global mutex, calls
		// GetNextChunk(), releases, and each thread converts ITS batch (arrow.cpp:112/116). GetNextBatch
		// above is already that shape, so the RecordBatch is the morsel and only this return value forced
		// serial execution.
		//
		// What 1 cost was not the conversion but the PIPELINE: a source declaring one thread makes every
		// operator above the scan single-threaded, which is why splitting a source into UNION ALL branches
		// was observed to improve core usage. MEASURED on a 6M-row CPU-bound GROUP BY: 0.864s -> 0.640s
		// (median of 3) from this alone, and 0.592s combined with row-group-sized batches.
		//
		// ⚠ AND THIS IS ONLY CONSULTED WHEN THE PIPELINE'S SINK IS PARALLEL. Pipeline::ScheduleParallel tests
		// `!sink->ParallelSink()` FIRST and falls back to ScheduleSequentialTask (ONE task), so for a plan
		// that streams straight to the client — where the sink IS the result collector — this value used to
		// be dead: MEASURED 2694ms at threads=1 and 2633ms at threads=4, flat. Both numbers above were taken
		// with an AGGREGATE above the scan, whose sink is parallel, which is what hid it. Fixed by declaring
		// batch-index support (ArrowStreamGetPartitionData below) so the plan gets the PARALLEL batch
		// collector; same shape then measures 2729 -> 1197ms. See docs/scan-concurrency.md §5.
		return scan_max_threads;
	}
};

struct ArrowStreamLocalState : public ArrowScanLocalState {
	ArrowStreamLocalState(unique_ptr<ArrowArrayWrapper> current_chunk, ClientContext &context)
	    : ArrowScanLocalState(std::move(current_chunk), context) {
	}

	//! Evaluates the global state's reapply_filter (a copy per thread: an executor is not shared).
	unique_ptr<Expression> filter_expression;
	unique_ptr<ExpressionExecutor> filter_executor;
	SelectionVector filter_sel {STANDARD_VECTOR_SIZE};
	//! How long this thread waits before asking again, when it finds another thread mid-pull. Resets on
	//! every batch it gets, so a fast pull is never waited on and a slow one costs a bounded number of
	//! wake-ups. See src/include/fabricator/scan_wait.hpp.
	fabricator::ScanWaitBackoff backoff;
};

enum class BatchPull : uint8_t {
	//! A batch is in the local state.
	BATCH,
	//! The stream is exhausted.
	END_OF_STREAM,
	//! Another thread is mid-pull and this call was not allowed to park on the lock.
	WOULD_BLOCK
};

//! Releases the pull lock and then announces progress, on EVERY exit including a throw. Announcing is what
//! wakes the threads that handed their workers back rather than parking on this lock — and it must happen
//! after the unlock, or they wake into a lock they still cannot take.
struct AnnounceProgressOnExit {
	ArrowStreamGlobalState &gstate;
	std::unique_lock<mutex> &lock;

	~AnnounceProgressOnExit() {
		if (lock.owns_lock()) {
			lock.unlock();
		}
		gstate.wait_state->Advance();
	}
};

// Pull the next Arrow array batch into the local state.
//
// ⚠ The pull is SERIALIZED (one Arrow C stream cannot be pulled from two threads) and BLOCKING (it crosses
// into managed code, which may be waiting on a network read). `park_on_lock` is what keeps those two facts from
// costing every DuckDB worker: with it false, a thread that loses the lock returns WOULD_BLOCK so the caller
// can hand its worker back instead of parking here. docs/scan-concurrency.md §5f.
BatchPull GetNextBatch(ArrowStreamGlobalState &gstate, ArrowStreamLocalState &lstate, bool park_on_lock) {
	std::unique_lock<mutex> lock(gstate.main_mutex, std::defer_lock);
	if (park_on_lock) {
		lock.lock();
	} else if (!lock.try_lock()) {
		return BatchPull::WOULD_BLOCK;
	}
	AnnounceProgressOnExit announce {gstate, lock};
	if (gstate.done) {
		return BatchPull::END_OF_STREAM;
	}

	auto chunk = make_uniq<ArrowArrayWrapper>();
	if (gstate.context) {
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(gstate.context), fabricator::SessionKeyFor(gstate.context));
	}
	int ret = gstate.stream.get_next(&gstate.stream, &chunk->arrow_array);
	if (ret != 0) {
		const char *msg = gstate.stream.get_last_error ? gstate.stream.get_last_error(&gstate.stream) : nullptr;
		throw IOException(string("Fabricator: failed to read next batch from stream") +
		                  (msg ? string(": ") + msg : string()));
	}
	if (!chunk->arrow_array.release) {
		// End of stream.
		gstate.done = true;
		return BatchPull::END_OF_STREAM;
	}

	lstate.chunk = shared_ptr<ArrowArrayWrapper>(chunk.release());
	lstate.chunk_offset = 0;
	lstate.Reset();
	// Assigned HERE, under the pull lock, so the batch index stays a single sequence in pull order — which is
	// what ArrowStreamGetPartitionData promises DuckDB and what the batch collector uses to restore order.
	lstate.batch_index = gstate.batch_index++;
	lstate.backoff.Reset();
	return BatchPull::BATCH;
}

} // namespace

// Reads an Arrow schema's columns into DuckDB return types + names (via the per-column converters), and
// optionally per-column nullability (ARROW_FLAG_NULLABLE — a NOT NULL SQL column is exported non-nullable,
// letting ORDER BY pushdown ignore NULL order for NOT NULL keys). Shared by PopulateReturnSchema (the scan
// path, reading a stream's schema) and ReadArrowSchema (the schema-only function-metadata path).
static void ReadSchemaColumns(ClientContext &context, ArrowSchema &arrow_schema, ArrowTableSchema &arrow_table,
                              vector<LogicalType> &return_types, vector<string> &names,
                              vector<bool> *column_nullable) {
	ArrowTableFunction::PopulateArrowTableSchema(context, arrow_table, arrow_schema);
	if (column_nullable) {
		column_nullable->clear();
	}
	for (int64_t i = 0; i < arrow_schema.n_children; i++) {
		auto &child = *arrow_schema.children[i];
		names.push_back(child.name ? string(child.name) : "column" + to_string(i));
		return_types.push_back(arrow_table.GetColumns().at((idx_t)i)->GetDuckType());
		if (column_nullable) {
			column_nullable->push_back((child.flags & ARROW_FLAG_NULLABLE) != 0);
		}
	}
}

// Reads a bare Arrow schema (e.g. from get_function_*_schema) into DuckDB return types + names. Releases the
// caller-owned ArrowSchema via an ArrowSchemaWrapper. Used by the function-metadata fetches (schema-only).
void ReadArrowSchema(ClientContext &context, ArrowSchema &arrow_schema, vector<LogicalType> &return_types,
                     vector<string> &names) {
	ArrowSchemaWrapper schema_root;
	schema_root.arrow_schema = arrow_schema;
	arrow_schema.release = nullptr; // ownership moved into schema_root (auto-released)
	ArrowTableSchema arrow_table;
	ReadSchemaColumns(context, schema_root.arrow_schema, arrow_table, return_types, names, nullptr);
}

// See the header note: schema_root/arrow_table are bind-time-only, so the copy shares everything else
// and leaves them empty. Used by the late-materialization rewrite's fetch-side get (CreateLHSGet).
unique_ptr<FunctionData> ArrowStreamBindData::Copy() const {
	auto copy = make_uniq<ArrowStreamBindData>();
	copy->column_ids = column_ids; // TableFunctionData base
	copy->return_types = return_types;
	copy->names = names;
	copy->column_nullable = column_nullable;
	copy->column_ndv = column_ndv;
	copy->push_projection = push_projection;
	copy->sink_schema = sink_schema;
	copy->sink_table = sink_table;
	copy->sink_kind = sink_kind;
	copy->factory = factory;
	copy->schema_factory = schema_factory;
	copy->filter_json = filter_json;
	copy->filter_constants = filter_constants;
	copy->native_filter_sql = native_filter_sql;
	copy->top_n = top_n;
	copy->order_by_json = order_by_json;
	copy->string_order_pushable = string_order_pushable;
	copy->reapply_filters = reapply_filters;
	copy->null_order_expressible = null_order_expressible;
	copy->at_unit = at_unit;
	copy->at_value = at_value;
	copy->rowid_source_columns = rowid_source_columns;
	copy->rowid_type = rowid_type;
	copy->virtual_rowid_columns = virtual_rowid_columns;
	copy->provider_virtual_columns = provider_virtual_columns;
	copy->row_count = row_count;
	copy->table = table;
	copy->query_sql = query_sql;
	copy->query_handle = query_handle;
	return std::move(copy);
}

void PopulateReturnSchema(ClientContext &context, ArrowStreamBindData &bind_data,
                          vector<LogicalType> &return_types, vector<Identifier> &names) {
	// Produce a throwaway stream solely to read the schema, then release it. A bare
	// request (no projection/filter) => the provider reports the full column set.
	// Set the active host-FS opener (this context) so a global host-FS table function (a lakehouse reader)
	// can resolve DuckDB secrets while reading its schema; SQL/compute factories ignore it.
	SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), SessionKeyFor(&context));
	ArrowArrayStream schema_stream {};
	if (bind_data.schema_factory) {
		// The bound object can describe itself — ask it instead of executing it. See `schema_factory`.
		bind_data.schema_factory(schema_stream);
	} else {
		bind_data.factory(ArrowScanRequest {}, schema_stream);
	}

	int ret = schema_stream.get_schema(&schema_stream, &bind_data.schema_root.arrow_schema);
	if (ret != 0) {
		// Copy the error BEFORE release: get_last_error's pointer lives in the stream's
		// private data, which release frees.
		string msg;
		if (schema_stream.get_last_error) {
			if (const char *err = schema_stream.get_last_error(&schema_stream)) {
				msg = err;
			}
		}
		if (schema_stream.release) {
			schema_stream.release(&schema_stream);
		}
		throw IOException(string("Fabricator: failed to read schema from stream") +
		                  (msg.empty() ? string() : ": " + msg));
	}
	if (schema_stream.release) {
		schema_stream.release(&schema_stream);
	}

	// Build DuckDB's per-column Arrow converters, then derive names + DuckDB types + nullability.
	// ReadSchemaColumns speaks strings (its other caller is the function-metadata path); a 2.0 bind
	// reports Identifiers, so convert here rather than typing the helper to one of its two callers.
	vector<string> schema_names;
	ReadSchemaColumns(context, bind_data.schema_root.arrow_schema, bind_data.arrow_table, return_types,
	                  schema_names, &bind_data.column_nullable);
	// DuckDB refuses a table function whose columns repeat a name ("duplicate column name"), and a raw query
	// can return one: `SELECT 41 AS a, 42 AS a`, or a join selecting `t.id, s.id`. Repeats become `a_1`, `a_2`,
	// … (duckdb-postgres does the same), compared case-insensitively because DuckDB's names are. Safe for the
	// mapping: a path that maps BY NAME (push_projection — catalog scans, TVFs) reads a table or function,
	// whose columns SQL Server already keeps unique, so it never reaches the rename; the raw-query paths map
	// by position.
	case_insensitive_set_t seen_names;
	for (auto &n : schema_names) {
		if (seen_names.insert(n).second) {
			continue;
		}
		for (idx_t suffix = 1;; suffix++) {
			auto candidate = n + "_" + to_string(suffix);
			if (seen_names.insert(candidate).second) {
				n = candidate;
				break;
			}
		}
	}
	names.clear();
	for (auto &n : schema_names) {
		names.push_back(Identifier(n));
	}
	if (return_types.empty()) {
		throw IOException("Fabricator: result schema has no columns");
	}
	bind_data.return_types = return_types;
	// our bind data keeps plain strings; DuckDB reports Identifiers.
	bind_data.names.clear();
	for (auto &n : names) {
		bind_data.names.push_back(n.GetIdentifierName());
	}
}

// ⚠⚠ 2.0: `LogicalGet::GetTable()` ASKS THE FUNCTION — `if (!function.get_table_entry) return nullptr;` —
// where 1.5 derived the entry from the bind data itself. Without this callback every catalog scan looks like
// a non-table to the binder, and UPDATE/DELETE are refused with *"Can only delete from base table"* even
// though the plan is a perfectly ordinary scan of one. The entry is already on our bind data.
optional_ptr<TableCatalogEntry> ArrowStreamGetTableEntry(optional_ptr<const FunctionData> bind_data) {
	if (!bind_data) {
		return nullptr;
	}
	return bind_data->Cast<ArrowStreamBindData>().table;
}

// 2.0 pushdown extract (see ExtractPath): DuckDB may hand this scan `s.a` instead of `s` when we say yes here.
// STRUCT columns only — a VARIANT field travels by name and needs variant_extract, not built. A scan that
// re-applies its filters (reapply_filters) types a filter on such a column by the FIELD it emits (ArrowStreamInitGlobal).
bool ArrowStreamSupportsPushdownExtract(const FunctionData &bind_data_p, const LogicalIndex &column_index) {
	auto &bind_data = bind_data_p.Cast<ArrowStreamBindData>();
	if (getenv("FABRICATOR_NO_PUSHDOWN_EXTRACT")) {
		return false;
	}
	auto idx = column_index.index;
	return idx < bind_data.return_types.size() && bind_data.return_types[idx].id() == LogicalTypeId::STRUCT;
}

BindInfo ArrowStreamGetBindInfo(TableFunctionGetBindInfoInput &input) { // 2.0: takes an input struct
	auto bind_data = input.bind_data;
	auto &data = bind_data->Cast<ArrowStreamBindData>();
	if (data.table) {
		// 2.0 dropped BindInfo's TableCatalogEntry member, so the classification is all that is left to
		// report; nothing in the engine reads the entry back out of a BindInfo any more.
		return BindInfo(ScanType::TABLE);
	}
	return BindInfo(ScanType::EXTERNAL);
}

// Minimal JSON string escaping for column names embedded in the scan spec.
static void JsonEscape(const string &s, string &out) {
	out += '"';
	for (char c : s) {
		switch (c) {
		case '"':
			out += "\\\"";
			break;
		case '\\':
			out += "\\\\";
			break;
		case '\n':
			out += "\\n";
			break;
		case '\r':
			out += "\\r";
			break;
		case '\t':
			out += "\\t";
			break;
		default:
			out += c;
		}
	}
	out += '"';
}

// Builds the projection spec `{"columns":["a","b"]}` for a catalog scan: the set of
// provider columns to fetch = requested real columns + (rowid source columns, if a
// rowid was requested). Empty result => fetch the first column (COUNT(*)-style).
// A DuckDB double-quoted identifier ("col", with any embedded " doubled) for the native SQL WHERE.
static void SqlIdentIngest(const string &s, string &out) {
	out += '"';
	for (char c : s) {
		if (c == '"') {
			out += "\"\"";
		} else {
			out += c;
		}
	}
	out += '"';
}

// ---- Internal table-filter functions must never reach a host query ----------------------------------
// ⚠⚠ DuckDB 2.0 renders OPTIONAL / BLOOM / DYNAMIC / PREFIX-RANGE / SELECTIVITY filters as calls to
// INTERNAL scalar functions (`__internal_tablefilter_optional`, …) whose bind THROWS
// *"Table filter functions are for internal use only!"*. They are reachable from here because 2.0's
// optimizer hands us an EXPRESSION_FILTER whose tree embeds them, where 1.5 handed the legacy wrapper
// shapes this file strips by TYPE. Measured on a join: the rendered predicate was
// `(("id" >= 2) AND (__internal_tablefilter_optional("id") AND __internal_tablefilter_optional("id")))`,
// which binds nowhere.
// All five are "not required for query correctness" (DuckDB re-applies every predicate above the scan),
// so DROPPING them is superset-safe — the same rule the legacy branches already apply by returning false.
// Whether a pushed filter only narrows what DuckDB discards anyway: one of the internal wrappers above (a TopN's
// moving bound, a join's min/max, a bloom or prefix-range filter), or an AND of only such. Such a filter does not
// make a server-side TOP unsafe: re-applied after it, it drops rows that could not be in the answer.
static bool IsOptionalFilterExpression(const Expression &e) {
	if (e.GetExpressionClass() == ExpressionClass::BOUND_FUNCTION &&
	    TableFilterFunctions::IsTableFilterFunction(e.Cast<BoundFunctionExpression>().Function())) {
		return true;
	}
	if (e.GetExpressionType() == ExpressionType::CONJUNCTION_AND) {
		for (auto &child : e.Cast<BoundConjunctionExpression>().GetChildren()) {
			if (!IsOptionalFilterExpression(*child)) {
				return false;
			}
		}
		return true;
	}
	return false;
}

static bool IsMandatoryFilter(const TableFilter &filter) {
	return filter.filter_type != TableFilterType::EXPRESSION_FILTER ||
	       !IsOptionalFilterExpression(*filter.Cast<ExpressionFilter>().expr);
}

static bool ContainsTableFilterFunction(const Expression &e) {
	if (e.GetExpressionClass() == ExpressionClass::BOUND_FUNCTION &&
	    TableFilterFunctions::IsTableFilterFunction(e.Cast<BoundFunctionExpression>().Function())) {
		return true;
	}
	bool found = false;
	ExpressionIterator::EnumerateChildren(e, [&](const Expression &child) {
		if (!found && ContainsTableFilterFunction(child)) {
			found = true;
		}
	});
	return found;
}

//! ⚠⚠ RECOVER WHAT THE WRAPPER HIDES BEFORE DROPPING IT. An internal filter function is not always a dead
//! end: the OPTIONAL wrappers carry the real child predicate in their bind data, and the DYNAMIC one carries
//! the live join bound. On 1.5 those arrived as their own TableFilter TYPES and this file rendered them; on
//! 2.0 they arrive INSIDE an EXPRESSION_FILTER, so pruning alone silently gives up the pushdown —
//! MEASURED: a join over a Delta table rendered NOTHING (`rendered=no`, the whole expression being one
//! dynamic-filter call) where 1.5 spliced the join bound into the per-file `read_parquet` WHERE.
//! Returns the recovered predicate, or nullptr when there is genuinely nothing to render.
//! ⚠⚠ A RECOVERED CHILD IS BOUND TO `#0`, NOT TO OUR COLUMN. The wrappers build their child against a
//! `BoundReferenceExpression(type, 0)` (CreateOptionalFilterExpression), so rendering it verbatim emits
//! `#0` and the generated SQL does not PARSE — measured as a Parser error from the host query. Every bound
//! reference in the recovered tree is therefore rebound onto the scan's column expression.
static void RebindReferencesToColumn(Expression &e, const Expression &column) {
	ExpressionIterator::EnumerateChildren(e, [&](unique_ptr<Expression> &child) {
		if (child && child->GetExpressionClass() == ExpressionClass::BOUND_REF) {
			child = column.Copy();
		} else if (child) {
			RebindReferencesToColumn(*child, column);
		}
	});
}

static unique_ptr<Expression> PrefixRangeAsExpression(const BoundFunctionExpression &f, const Expression &column);
static unique_ptr<Expression> BloomAsExpression(const BoundFunctionExpression &f, const Expression &column);

static unique_ptr<Expression> RecoverFilterFunction(const BoundFunctionExpression &f, const Expression &column) {
	const auto name = f.Function().GetName();
	if (name == DynamicFilterScalarFun::NAME) {
		if (!f.BindInfo()) {
			return nullptr;
		}
		auto &data = f.BindInfo()->Cast<DynamicFilterFunctionData>();
		// `initialized` is atomic; not yet materialized => nothing to push, which is always safe for a
		// dynamic filter. ToExpression takes its OWN shared lock (table_filter_dynamic_function.cpp), so
		// none is taken here.
		if (!data.filter_data || !data.filter_data->initialized || f.GetChildren().empty()) {
			return nullptr;
		}
		return data.filter_data->ToExpression(*f.GetChildren()[0]);
	}
	if (name == OptionalFilterScalarFun::NAME) {
		if (!f.BindInfo()) {
			return nullptr;
		}
		auto &data = f.BindInfo()->Cast<OptionalFilterFunctionData>();
		if (!data.child_filter_expr) {
			return nullptr;
		}
		auto child = data.child_filter_expr->Copy();
		RebindReferencesToColumn(*child, column);
		return child;
	}
	if (name == SelectivityOptionalFilterScalarFun::NAME) {
		if (!f.BindInfo()) {
			return nullptr;
		}
		auto &data = f.BindInfo()->Cast<SelectivityOptionalFilterFunctionData>();
		// ⚠ On 2.0 a HASH JOIN's bound arrives here as a PREFIX-RANGE child once the build side has more keys than
		// dynamic_or_filter_threshold (50). Its keys CAN be read (LookupRange/LookupKeys) — PrefixRangeAsExpression,
		// one level down. The port first dropped it as unreadable, which left Delta scans without any join filter
		// on every larger join (TPC-H); the keys were read for SQL Server only until 2026-11-08.
		if (!data.child_filter_expr) {
			return nullptr;
		}
		auto child = data.child_filter_expr->Copy();
		RebindReferencesToColumn(*child, column);
		return child;
	}
	if (name == TableFilterPrefixRangeFun::Name) {
		return PrefixRangeAsExpression(f, column);
	}
	if (name == TableFilterBloomFilterFun::Name) {
		return BloomAsExpression(f, column); // no SQL form: only through the registry, else dropped
	}
	return nullptr;
}

//! Returns a copy of `e` with the internal filter functions pruned, or nullptr when nothing
//! correctness-bearing survives. Pruning happens ONLY under an AND — dropping a branch of an OR would
//! NARROW the result (lose rows), and an argument of a comparison has no meaning without it.
static unique_ptr<Expression> PruneTableFilterFunctions(const Expression &e, const Expression &column) {
	if (!ContainsTableFilterFunction(e)) {
		return e.Copy();
	}
	if (e.GetExpressionClass() == ExpressionClass::BOUND_FUNCTION &&
	    TableFilterFunctions::IsTableFilterFunction(e.Cast<BoundFunctionExpression>().Function())) {
		auto recovered = RecoverFilterFunction(e.Cast<BoundFunctionExpression>(), column);
		if (!recovered) {
			return nullptr;
		}
		// The recovered child may itself be wrapped (an optional filter around a dynamic one).
		return PruneTableFilterFunctions(*recovered, column);
	}
	if (e.GetExpressionType() == ExpressionType::CONJUNCTION_AND) {
		vector<unique_ptr<Expression>> kept;
		for (auto &child : e.Cast<BoundConjunctionExpression>().GetChildren()) {
			auto pruned = PruneTableFilterFunctions(*child, column);
			if (pruned) {
				kept.push_back(std::move(pruned));
			}
		}
		if (kept.empty()) {
			return nullptr;
		}
		if (kept.size() == 1) {
			return std::move(kept[0]);
		}
		auto conj = make_uniq<BoundConjunctionExpression>(ExpressionType::CONJUNCTION_AND);
		for (auto &k : kept) {
			conj->GetChildrenMutable().push_back(std::move(k));
		}
		return std::move(conj);
	}
	return nullptr;
}

// Render one live TableFilter to a DuckDB SQL predicate on `qcol` (an already-quoted identifier). Returns
// true + appends to `out` iff rendered. The native target IS DuckDB (read_parquet), so TableFilter::ToString
// yields EXACT SQL for the mandatory kinds (constant/IN/null/conjunction/expression). Only OPTIONAL / DYNAMIC
// / BLOOM are handled specially — and all three are "not required for correctness" (DuckDB never relies on
// the scan to apply them: the join/topn re-applies), so returning false for them is always safe.
static bool RenderTableFilter(const TableFilter &f, const string &qcol, string &out) {
	switch (f.filter_type) {
	case TableFilterType::LEGACY_OPTIONAL_FILTER: {
		auto &opt = f.Cast<LegacyOptionalFilter>();
		if (!opt.child_filter) {
			return false;
		}
		return RenderTableFilter(*opt.child_filter, qcol, out); // optional: fine to skip if child unrenderable
	}
	case TableFilterType::LEGACY_DYNAMIC_FILTER: {
		// A runtime join/topn bound. ⚠ 2.0 reshaped DynamicFilterData: there is no inner `filter` to reach
		// any more — it holds comparison_type + constant and builds the expression itself, TAKING ITS OWN
		// SHARED LOCK inside ToExpression (verified in table_filter_dynamic_function.cpp). So we must NOT
		// lock here, which also removes the manual lock_guard this used to need.
		// `initialized` is atomic, so reading it unlocked is fine; not yet materialized => skip, which is
		// always safe for a dynamic filter (nothing relies on the scan applying it).
		auto &dyn = f.Cast<LegacyDynamicFilter>();
		if (!dyn.filter_data || !dyn.filter_data->initialized) {
			return false;
		}
		BoundColumnRefExpression dyn_col(Identifier(qcol), LogicalType::ANY, ColumnBinding());
		auto dyn_expr = dyn.filter_data->ToExpression(dyn_col);
		if (!dyn_expr) {
			return false;
		}
		out += dyn_expr->ToString();
		return true;
	}
	case TableFilterType::LEGACY_BLOOM_FILTER:
		return false; // probabilistic; not exact SQL — always LegacyOptionalFilter-wrapped, so skip-safe
	case TableFilterType::LEGACY_STRUCT_EXTRACT: {
		// A struct-member filter (`WHERE (s).a = 5`, erased into the set under exact mode): rebase the
		// child filter onto an explicit struct_extract — exact DuckDB SQL and robust to member names the
		// bare ToString dot form (`"s".a`) would mis-quote. NOTE: on a COLUMN-MAPPED table this SQL still
		// references the LOGICAL member name, which the native (read_parquet) path cannot bind (nested
		// children carry physical names inside the files) — that pre-existing combination fails loudly at
		// the per-file bind; the fix belongs in the C# native reader (rebuild mapped structs logically).
		// The codec exact path is fine: its per-batch filter runs over logically-renamed batches.
		auto &sf = f.Cast<LegacyStructFilter>();
		string member;
		// 2.0: child_name is an Identifier; iterate its raw characters.
		auto sf_child = sf.child_name.GetIdentifierName();
		if (!sf_child.empty()) {
			member = "struct_extract(" + qcol + ", '";
			for (char c : sf_child) {
				member += c;
				if (c == '\'') {
					member += '\''; // SQL string-literal escape
				}
			}
			member += "')";
		} else {
			member = "struct_extract_at(" + qcol + ", " + to_string(sf.child_idx + 1) + ")";
		}
		return RenderTableFilter(*sf.child_filter, member, out);
	}
	case TableFilterType::LEGACY_CONJUNCTION_AND:
	case TableFilterType::LEGACY_CONJUNCTION_OR: {
		// Recurse per child (do NOT use ToString: a child may be an LegacyOptionalFilter/LegacyDynamicFilter whose
		// ToString is not plain SQL — "optional: …" — and would leak into the predicate).
		bool is_and = f.filter_type == TableFilterType::LEGACY_CONJUNCTION_AND;
		// LegacyConjunctionFilter (the base holding child_filters) has no ::TYPE, so Cast<> the concrete subtype.
		const LegacyConjunctionFilter &conj =
		    is_and ? static_cast<const LegacyConjunctionFilter &>(f.Cast<LegacyConjunctionAndFilter>())
		           : static_cast<const LegacyConjunctionFilter &>(f.Cast<LegacyConjunctionOrFilter>());
		vector<string> parts;
		for (auto &child : conj.child_filters) {
			string cs;
			if (RenderTableFilter(*child, qcol, cs) && !cs.empty()) {
				parts.push_back(std::move(cs));
			} else if (!is_and) {
				return false; // OR: dropping a branch narrows the result (a subset) — unsafe, skip the whole OR
			}
			// AND: dropping a child only widens (superset). Its children here are optional (join) or renderable
			// constants; a mandatory constant always renders, so nothing correctness-bearing is dropped.
		}
		if (parts.empty()) {
			return false;
		}
		out += '(';
		for (idx_t i = 0; i < parts.size(); i++) {
			if (i) {
				out += is_and ? " AND " : " OR ";
			}
			out += parts[i];
		}
		out += ')';
		return true;
	}
	default: {
		// Constant / IN / IS [NOT] NULL / expression: DuckDB's own render == exact SQL for the
		// read_parquet (DuckDB) target. These are the MANDATORY (erased-from-plan) filters we must apply.
		// ⚠ 2.0 REMOVED TableFilter::ToString — a filter now only knows how to build an EXPRESSION
		// (ToExpression), so the render goes through that: a BoundColumnRefExpression whose ALIAS is the
		// quoted column renders as exactly that name (BoundColumnRefExpression::ToString returns the alias),
		// and the resulting expression renders itself. Still DuckDB's own rendering, just one step later.
		// ⚠ The TEXT may differ from 1.5's in spacing/parenthesisation; it must stay valid DuckDB SQL for
		// read_parquet, which is what the filter-pushdown suites check.
		BoundColumnRefExpression col(Identifier(qcol), LogicalType::ANY, ColumnBinding());
		auto expr = f.ToExpression(col);
		if (!expr) {
			return false;
		}
		// ⚠ An EXPRESSION_FILTER's tree may embed internal table-filter functions — see
		// PruneTableFilterFunctions. Unpruned they render SQL that binds NOWHERE.
		auto pruned = PruneTableFilterFunctions(*expr, col);
		if (!pruned) {
			return false;
		}
		out += pruned->ToString();
		return true;
	}
	}
}

// ---- Live TableFilterSet -> FilterNode JSON (structured, for provider PRUNING) ------------------------
// The SQL rendering above (RenderTableFilter) is the APPLICATION channel; this is the PRUNING channel:
// the same live filters (erased statics + materialized dynamic/join filters) serialized as the FilterNode
// tree the provider's stats-based skipping consumes (Delta file pruning + parquet row-group/bloom pruning)
// — a dynamic join filter is a true predicate of the query, so pruning with it is superset-correct, and it
// is exactly the filter the bind-time pushdown_complex_filter channel can never carry (it materializes at
// run time). Constants are appended to a PER-EXECUTION copy of the constants vector (never the shared bind
// data). Best-effort: an unconvertible node is dropped (AND) or drops its whole disjunction (OR).

static const char *LiveCmpToken(ExpressionType t) {
	switch (t) {
	case ExpressionType::COMPARE_EQUAL:
		return "=";
	case ExpressionType::COMPARE_NOTEQUAL:
		return "<>";
	case ExpressionType::COMPARE_LESSTHAN:
		return "<";
	case ExpressionType::COMPARE_GREATERTHAN:
		return ">";
	case ExpressionType::COMPARE_LESSTHANOREQUALTO:
		return "<=";
	case ExpressionType::COMPARE_GREATERTHANOREQUALTO:
		return ">=";
	default:
		return nullptr; // (not-)distinct etc.: not consumed by the pruning evaluators
	}
}

// Emit the column reference body: `"col":"name"` for a plain column, `"path":["s","a"]` for a struct
// member (mirrors the bind-time FilterSerializer contract in fabricator_table_entry.cpp).
static void LiveJsonColumnRef(const vector<string> &path, string &out) {
	if (path.size() == 1) {
		out += "\"col\":";
		JsonEscape(path[0], out);
		return;
	}
	out += "\"path\":[";
	for (idx_t i = 0; i < path.size(); i++) {
		if (i) {
			out += ',';
		}
		JsonEscape(path[i], out);
	}
	out += ']';
}

// ---- EXPRESSION_FILTER -> FilterNode JSON -------------------------------------------------------------
// ⚠⚠ ON 2.0 THIS IS THE ONLY LIVE SHAPE, so without it the structured PRUNING channel is DEAD: the legacy
// cases below never fire and the `default: return false` beneath them swallowed every filter. MEASURED
// before: `verify_delta_late_materialization` rendered the SQL predicate correctly
// (`("_metadata.row_id" = 1099511627781)`) while the Delta pruner's `rowid prune files 3 -> 1` line was
// ABSENT — results stay correct (DuckDB re-applies every predicate above the scan), so the only symptom is
// work not being skipped. On 1.5 the comment "EXPRESSION_FILTER: not convertible" was true, because the
// type never arrived.
// This mirrors the SQL renderer's walk one level lower, over the EXPRESSION rather than the TableFilter:
// comparisons via BoundComparisonExpression, AND/OR via BoundConjunctionExpression, IS [NOT] NULL and IN via
// BoundOperatorExpression, struct members via struct_extract(_at), and the internal filter wrappers via the
// same RecoverFilterFunction the SQL channel uses. Anything else is DROPPED, which only ever widens.

//! Resolve `e` as the filter's COLUMN SUBJECT, appending any struct member names it traverses to `path` and
//! reporting the leaf type. An ExpressionFilter binds its column as `BoundReferenceExpression(type, 0)`
//! (ExpressionFilter::CreateComparisonFilter), and a single-column filter carries NO `column_indexes`
//! (TableFilterSet::PushFilter refuses one that does) — so index 0 is this entry's own column.
//! ⚠ The CALLER restores `path`. Members are pushed on the way back OUT of the recursion, so they land
//! outermost-first (`s`, `a`, `b`) exactly as the legacy LEGACY_STRUCT_EXTRACT branch builds them.
//! Anything computed (a cast, an arithmetic expression) returns false: there is no stats key to prune on.
static bool LiveExprSubject(const Expression &e, const LogicalType &type, vector<string> &path,
                            LogicalType &leaf) {
	if (e.GetExpressionClass() == ExpressionClass::BOUND_REF) {
		if (e.Cast<BoundReferenceExpression>().Index() != 0) {
			return false;
		}
		leaf = type;
		return true;
	}
	if (e.GetExpressionClass() != ExpressionClass::BOUND_FUNCTION) {
		return false;
	}
	auto &fn = e.Cast<BoundFunctionExpression>();
	const auto name = fn.Function().GetName();
	const bool by_name = name == StructExtractFun::Name;
	if ((!by_name && name != StructExtractAtFun::Name) || fn.GetChildren().size() != 2) {
		return false;
	}
	auto &key = *fn.GetChildren()[1];
	if (key.GetExpressionClass() != ExpressionClass::BOUND_CONSTANT) {
		return false;
	}
	const auto &key_val = key.Cast<BoundConstantExpression>().GetValue();
	if (key_val.IsNull()) {
		return false;
	}
	LogicalType parent;
	if (!LiveExprSubject(*fn.GetChildren()[0], type, path, parent)) {
		return false;
	}
	if (parent.id() != LogicalTypeId::STRUCT) {
		return false;
	}
	auto &members = StructType::GetChildTypes(parent);
	idx_t member_idx = members.size();
	if (by_name) {
		// ⚠ Push the STRUCT TYPE's spelling, never the key literal's: the member name IS the parquet/stats
		// key, so a case-insensitive match must still emit the name the provider can look up.
		const auto wanted = StringUtil::Lower(key_val.ToString());
		for (idx_t i = 0; i < members.size(); i++) {
			if (StringUtil::Lower(members[i].first.GetIdentifierName()) == wanted) {
				member_idx = i;
				break;
			}
		}
	} else {
		// struct_extract_at is 1-BASED (its own doc says so).
		// 2.0: DefaultTryCastAs RETURNS an optional<Value> (the out-param overload now takes an error string).
		auto pos_val = key_val.DefaultTryCastAs(LogicalType::BIGINT);
		if (!pos_val.has_value()) {
			return false;
		}
		const auto pos = pos_val->GetValue<int64_t>();
		if (pos >= 1 && (idx_t)pos <= members.size()) {
			member_idx = (idx_t)(pos - 1);
		}
	}
	if (member_idx >= members.size()) {
		return false;
	}
	path.push_back(members[member_idx].first.GetIdentifierName());
	leaf = members[member_idx].second;
	return true;
}

// ---- A hash join's prefix-range filter as the build side's keys ---------------------------------------------
// Whether the live filters being serialized go to a provider that renders them as a SUPERSET and re-applies them
// (ArrowStreamBindData::reapply_filters — SQL Server): only then is a hash join's prefix-range filter turned into a
// key list. Set by SerializeLiveFilters for the duration of one call; the node serializers below take it from here
// rather than through every signature.
static thread_local bool g_expand_prefix_ranges = false;

struct PrefixRangeScope {
	explicit PrefixRangeScope(bool on) : saved(g_expand_prefix_ranges) {
		g_expand_prefix_ranges = on;
	}
	~PrefixRangeScope() {
		g_expand_prefix_ranges = saved;
	}
	bool saved;
};

//! The values a hash join's prefix-range filter admits, as an IN list (docs/duckdb-2.0-remote-execution.md §5.5).
//! DuckDB passes a probe-side scan the build side's keys as an IN list only up to dynamic_or_filter_threshold
//! (default 50); above it a bitmap over [min, max] — exact when the span is small, one bit per value — that has no
//! SQL form, so the server got nothing and returned the table nearly whole (TPC-H q17 9.5 s with lineitem remote). The
//! bitmap answers `LookupRange` / `LookupKeys`, so its keys can be read back: its bounds by binary search, then every
//! value in between in vectors. A bucket of a coarser bitmap admits values the build side does not have — a superset,
//! which the scan's re-applied filters and the join remove. Integral keys only.
//! Returns false when there is nothing to send; `keys` empty with true: too many keys, send `[lo, hi]` instead.
static constexpr idx_t PREFIX_RANGE_MAX_KEYS = 100000;
static constexpr int64_t PREFIX_RANGE_MAX_SPAN = int64_t(1) << 24;

//! The smallest and largest value the filter admits, by binary search over LookupRange. False: an empty build side.
template <class T>
static bool PrefixRangeBounds(const PrefixRangeFilter &filter, const LogicalType &type, int64_t &lo, int64_t &hi) {
	const int64_t domain_lo = static_cast<int64_t>(NumericLimits<T>::Minimum());
	const int64_t domain_hi = static_cast<int64_t>(NumericLimits<T>::Maximum());
	auto any = [&](int64_t a, int64_t b) {
		return filter.LookupRange(Value::Numeric(type, a), Value::Numeric(type, b)) !=
		       FilterPropagateResult::FILTER_ALWAYS_FALSE;
	};
	if (!any(domain_lo, domain_hi)) {
		return false; // an empty build side
	}
	// the smallest value with a set bucket: the largest a with nothing in [domain_lo, a - 1]
	int64_t a = domain_lo, b = domain_hi;
	while (a < b) {
		int64_t mid = a + static_cast<int64_t>((static_cast<uint64_t>(b) - static_cast<uint64_t>(a)) / 2);
		if (any(domain_lo, mid)) {
			b = mid;
		} else {
			a = mid + 1;
		}
	}
	lo = a;
	a = lo, b = domain_hi;
	while (a < b) {
		int64_t mid = a + static_cast<int64_t>((static_cast<uint64_t>(b) - static_cast<uint64_t>(a) + 1) / 2);
		if (any(mid, domain_hi)) {
			a = mid;
		} else {
			b = mid - 1;
		}
	}
	hi = a;
	return true;
}

template <class T>
static bool ReadPrefixRangeKeys(const PrefixRangeFilter &filter, const LogicalType &type, vector<Value> &keys,
                                int64_t &lo, int64_t &hi) {
	if (!PrefixRangeBounds<T>(filter, type, lo, hi)) {
		return false;
	}
	if (static_cast<uint64_t>(hi) - static_cast<uint64_t>(lo) >= static_cast<uint64_t>(PREFIX_RANGE_MAX_SPAN)) {
		return true; // too wide to read value by value: the range only
	}
	Vector candidates(type, STANDARD_VECTOR_SIZE);
	SelectionVector found(STANDARD_VECTOR_SIZE);
	auto data = FlatVector::GetDataMutable<T>(candidates);
	for (int64_t start = lo; start <= hi;) {
		idx_t count = 0;
		for (; count < STANDARD_VECTOR_SIZE && start + static_cast<int64_t>(count) <= hi; count++) {
			data[count] = static_cast<T>(start + static_cast<int64_t>(count));
		}
		idx_t hits = filter.LookupKeys(candidates, found, count);
		if (keys.size() + hits > PREFIX_RANGE_MAX_KEYS) {
			keys.clear();
			return true; // too many keys for a list: the range only
		}
		for (idx_t i = 0; i < hits; i++) {
			keys.push_back(Value::Numeric(type, static_cast<int64_t>(data[found.get_index(i)])));
		}
		if (start + static_cast<int64_t>(count) > hi) {
			break;
		}
		start += static_cast<int64_t>(count);
	}
	return true;
}

static bool ReadPrefixRangeBounds(const PrefixRangeFunctionData &data, int64_t &lo, int64_t &hi) {
	if (!data.filter || !data.filter->IsInitialized()) {
		return false;
	}
	switch (data.key_type.id()) {
	case LogicalTypeId::TINYINT:
		return PrefixRangeBounds<int8_t>(*data.filter, data.key_type, lo, hi);
	case LogicalTypeId::SMALLINT:
		return PrefixRangeBounds<int16_t>(*data.filter, data.key_type, lo, hi);
	case LogicalTypeId::INTEGER:
		return PrefixRangeBounds<int32_t>(*data.filter, data.key_type, lo, hi);
	case LogicalTypeId::BIGINT:
		return PrefixRangeBounds<int64_t>(*data.filter, data.key_type, lo, hi);
	default:
		return false;
	}
}

static bool ReadPrefixRangeKeys(const PrefixRangeFunctionData &data, vector<Value> &keys, int64_t &lo, int64_t &hi) {
	if (!data.filter || !data.filter->IsInitialized()) {
		return false;
	}
	switch (data.key_type.id()) {
	case LogicalTypeId::TINYINT:
		return ReadPrefixRangeKeys<int8_t>(*data.filter, data.key_type, keys, lo, hi);
	case LogicalTypeId::SMALLINT:
		return ReadPrefixRangeKeys<int16_t>(*data.filter, data.key_type, keys, lo, hi);
	case LogicalTypeId::INTEGER:
		return ReadPrefixRangeKeys<int32_t>(*data.filter, data.key_type, keys, lo, hi);
	case LogicalTypeId::BIGINT:
		return ReadPrefixRangeKeys<int64_t>(*data.filter, data.key_type, keys, lo, hi);
	default:
		return false;
	}
}

//! The SQL channel's form of a hash join's prefix-range filter (RenderLiveFilters, the WHERE of a Delta scan's
//! inner read_parquet query): `col IN (keys)`, or `col >= lo AND col <= hi` when the keys are too many to list.
//! Without it a probe-side Delta scan got NO join filter once the build side passed dynamic_or_filter_threshold
//! (50 keys — every TPC-H join), so the whole table crossed into the plan and the join discarded it (SF10 q8:
//! 60M lineitem rows out of the scan where Iceberg's returned 24k). A bucket of a coarse bitmap admits keys the
//! build side does not have: a superset, which the join removes. Only while g_expand_prefix_ranges is set.
// ---- A hash join's prefix-range filter, evaluated by the scan's inner query --------------------------------
// A Delta scan's rows come from an inner DuckDB query (read_parquet) in THIS process. Rather than copying the build
// side's keys into its SQL text (an IN list costs ~0.3 s per 13k keys to parse and probe, and a key set past
// 100k or a span past 2^24 can only be sent as its range, which on TPC-H covers the whole table), the inner query
// calls fabricator_join_filter(<id>, <column>), which looks the filter object up by id and asks it — LookupKeys, the
// probe DuckDB's own parquet scan runs. The filter is owned by the join's hash table, which lives as long as the
// probe pipeline this scan belongs to; the scan's global state registers it and removes it after releasing the
// inner stream (so no inner query can still be running).
// A join may also hand out a BLOOM filter instead (a key type the prefix-range filter does not support, e.g.
// DOUBLE or VARCHAR, or a join DuckDB builds no prefix-range filter for): the entry then carries that, and the
// function hashes the keys the way DuckDB's own bloom lookup does (VectorOperations::Hash of the key-typed value).
struct JoinFilterEntry {
	optional_ptr<PrefixRangeFilter> filter;
	//! raw: the shared_ptr that keeps it alive sits in g_join_bloom_owners, so a per-thread cache entry holds no
	//! reference (a bloom filter can be large) and is only ever read for an id that is still registered
	const BloomFilter *bloom = nullptr;
	LogicalType key_type;
	bool filters_null_values = false;
};
static mutex g_join_filters_lock;
static unordered_map<idx_t, JoinFilterEntry> g_join_filters;
static unordered_map<idx_t, shared_ptr<const BloomFilter>> g_join_bloom_owners;
static atomic<idx_t> g_join_filter_next {1};
//! Set while RenderLiveFilters runs: where the ids it registers are collected for the scan to remove.
static thread_local vector<idx_t> *g_join_filter_ids = nullptr;

static const char *const JOIN_FILTER_FUNCTION = "fabricator_join_filter";

//! What DuckDB's own pushed filter answers for `count` keys, into out[]: true where a key may have a partner in the
//! join. No filter (already removed), an unusable one, or a key of another type: every row true — a join filter may
//! only ever widen, the join itself checks the keys.
static void JoinFilterMatch(const JoinFilterEntry *entry_p, Vector &keys, idx_t count, bool *out) {
	const bool usable = entry_p && keys.GetType() == entry_p->key_type &&
	                    ((entry_p->filter && entry_p->filter->IsInitialized()) ||
	                     (entry_p->bloom && entry_p->bloom->IsSealed()));
	if (!usable) {
		for (idx_t i = 0; i < count; i++) {
			out[i] = true;
		}
		return;
	}
	auto &entry = *entry_p;
	keys.Flatten(count);
	SelectionVector found(STANDARD_VECTOR_SIZE);
	idx_t hits;
	if (entry.bloom) {
		// DuckDB's own lookup (SelectBloomFilter): hash the key-typed values, then test the hashes. A NULL row's
		// hash is tested too and overridden below, as there.
		Vector hashes(LogicalType::HASH, count);
		VectorOperations::Hash(keys, hashes, count);
		hashes.Flatten(count);
		hits = entry.bloom->LookupHashes(hashes, found, count);
	} else {
		hits = entry.filter->LookupKeys(keys, found, count);
	}
	for (idx_t i = 0; i < count; i++) {
		out[i] = false;
	}
	for (idx_t i = 0; i < hits; i++) {
		out[found.get_index(i)] = true;
	}
	// a NULL key: what DuckDB's own pushed filter does with it
	auto &validity = FlatVector::Validity(keys);
	if (!validity.AllValid()) {
		for (idx_t i = 0; i < count; i++) {
			if (!validity.RowIsValid(i)) {
				out[i] = !entry.filters_null_values;
			}
		}
	}
}

static void JoinFilterFunctionImpl(DataChunk &args, ExpressionState &state, Vector &result) {
	const idx_t count = args.size();
	result.SetVectorType(VectorType::FLAT_VECTOR);
	auto out = FlatVector::GetDataMutable<bool>(result);
	// The lookup is cached per thread: the function runs once per chunk on every thread of the inner query, and a
	// shared lock per chunk made a 60M-row probe SLOWER than no filter at all (TPC-H SF10 q8, two filters). Ids are
	// never reused, so a cached entry cannot name another filter; it is only consulted for the id it was found for,
	// which no query uses once the scan that registered it has removed it.
	// Several entries: one query may probe several filters in turn (q8: l_orderkey and l_partkey).
	static constexpr idx_t CACHE_SIZE = 4;
	static thread_local idx_t cached_ids[CACHE_SIZE] = {0, 0, 0, 0};
	static thread_local JoinFilterEntry cached_entries[CACHE_SIZE];
	static thread_local idx_t cache_next = 0;
	JoinFilterEntry entry;
	bool found_entry = false;
	auto &id_vector = args.data[0];
	if (id_vector.GetVectorType() == VectorType::CONSTANT_VECTOR && !ConstantVector::IsNull(id_vector)) {
		idx_t id = ConstantVector::GetData<uint64_t>(id_vector)[0];
		for (idx_t c = 0; c < CACHE_SIZE && !found_entry; c++) {
			if (cached_ids[c] == id) {
				entry = cached_entries[c];
				found_entry = true;
			}
		}
		if (!found_entry) {
			lock_guard<mutex> guard(g_join_filters_lock);
			auto it = g_join_filters.find(id);
			if (it != g_join_filters.end()) {
				entry = it->second;
				found_entry = true;
				cached_ids[cache_next] = id;
				cached_entries[cache_next] = entry;
				cache_next = (cache_next + 1) % CACHE_SIZE;
			}
		}
	}
	JoinFilterMatch(found_entry ? &entry : nullptr, args.data[1], count, out);
}

ScalarFunction JoinFilterFunction() {
	ScalarFunction fn(JOIN_FILTER_FUNCTION, {LogicalType::UBIGINT, LogicalType::ANY}, LogicalType::BOOLEAN,
	                  JoinFilterFunctionImpl);
	fn.SetNullHandling(FunctionNullHandling::SPECIAL_HANDLING);
	return fn;
}

static void UnregisterJoinFilters(const vector<idx_t> &ids) {
	if (ids.empty()) {
		return;
	}
	lock_guard<mutex> guard(g_join_filters_lock);
	for (auto id : ids) {
		g_join_filters.erase(id);
		g_join_bloom_owners.erase(id);
	}
}

//! Registers a join filter for the scan being rendered (its ids go to g_join_filter_ids, removed when the scan
//! ends) and returns the id. `owner` keeps a bloom filter alive; a prefix-range filter belongs to the join's hash
//! table, which outlives the probe pipeline this scan is part of.
static idx_t RegisterJoinFilter(const JoinFilterEntry &entry, shared_ptr<const BloomFilter> owner) {
	D_ASSERT(g_join_filter_ids);
	idx_t id = g_join_filter_next++;
	{
		lock_guard<mutex> guard(g_join_filters_lock);
		g_join_filters[id] = entry;
		if (owner) {
			g_join_bloom_owners[id] = std::move(owner);
		}
	}
	g_join_filter_ids->push_back(id);
	return id;
}

//! A hash join's BLOOM filter, as fabricator_join_filter(<id>, col) — only inside the SQL channel's registry (there
//! is no SQL form of a bloom filter), and only when its argument is the scan column itself: DuckDB hashes the
//! argument's value, so a filter whose argument is a cast of the column (a join on a column of another type)
//! would need that cast reproduced bit for bit, or it would DROP rows. Such a filter is left out, which only widens.
static unique_ptr<Expression> BloomAsExpression(const BoundFunctionExpression &f, const Expression &column) {
	if (!g_expand_prefix_ranges || !g_join_filter_ids || !f.BindInfo() || f.GetChildren().size() != 1) {
		return nullptr;
	}
	auto &data = f.BindInfo()->Cast<BloomFilterFunctionData>();
	auto &arg = *f.GetChildren()[0];
	// the wrapper's recovery has already rebound the argument onto the scan column, so a filter on the column itself
	// arrives as that column (or still as the bare reference); anything else is an expression over it
	const bool on_column = arg.GetExpressionClass() == ExpressionClass::BOUND_REF || arg.Equals(column);
	// the type DuckDB hashes is the bloom function's argument type (the end of the join's cast chain); it must be the
	// key type itself, i.e. no cast between the column and the hash. The scan column expression may carry no
	// concrete type here (ANY); the inner query's actual column type is checked per chunk in the function.
	auto &declared = f.Function().GetArguments();
	const LogicalType input_type = declared.size() == 1 ? declared[0] : data.key_type;
	if (!data.filter || !data.filter->IsSealed() || !on_column || input_type != data.key_type) {
		return nullptr;
	}
	JoinFilterEntry entry;
	entry.bloom = data.filter.get();
	entry.key_type = input_type;
	entry.filters_null_values = data.filters_null_values;
	idx_t id = RegisterJoinFilter(entry, data.filter);
	vector<unique_ptr<Expression>> args;
	args.push_back(make_uniq<BoundConstantExpression>(Value::UBIGINT(id)));
	args.push_back(column.Copy());
	return make_uniq<BoundFunctionExpression>(BoundScalarFunction(JoinFilterFunction()), std::move(args), nullptr);
}

static unique_ptr<Expression> PrefixRangeAsExpression(const BoundFunctionExpression &f, const Expression &column) {
	if (!g_expand_prefix_ranges || !f.BindInfo()) {
		return nullptr;
	}
	auto &data = f.BindInfo()->Cast<PrefixRangeFunctionData>();
	if (g_join_filter_ids && data.filter && data.filter->IsInitialized()) {
		JoinFilterEntry entry; // not brace-initialized: a struct with member initializers is no aggregate in C++11
		entry.filter = data.filter;
		entry.key_type = data.key_type;
		entry.filters_null_values = data.filters_null_values;
		idx_t id = RegisterJoinFilter(entry, nullptr);
		vector<unique_ptr<Expression>> args;
		args.push_back(make_uniq<BoundConstantExpression>(Value::UBIGINT(id)));
		args.push_back(column.Copy());
		unique_ptr<Expression> call =
		    make_uniq<BoundFunctionExpression>(BoundScalarFunction(JoinFilterFunction()), std::move(args), nullptr);
		// The keys' range beside it, as plain comparisons: those the parquet reader checks against each row group's
		// min/max and skips the row group, which it cannot do for the call (DuckDB's own scan asks the filter's
		// LookupRange per row group; for our function it has no statistics). Nothing on TPC-H, whose keys are
		// spread over every row group; whole row groups on data clustered by the key. Integer keys only.
		int64_t lo = 0, hi = 0;
		if (ReadPrefixRangeBounds(data, lo, hi)) {
			auto ge = BoundComparisonExpression::Create(ExpressionType::COMPARE_GREATERTHANOREQUALTO, column.Copy(),
			                                            make_uniq<BoundConstantExpression>(Value::Numeric(data.key_type, lo)));
			auto le = BoundComparisonExpression::Create(ExpressionType::COMPARE_LESSTHANOREQUALTO, column.Copy(),
			                                            make_uniq<BoundConstantExpression>(Value::Numeric(data.key_type, hi)));
			auto range = make_uniq<BoundConjunctionExpression>(ExpressionType::CONJUNCTION_AND, std::move(ge), std::move(le));
			return make_uniq<BoundConjunctionExpression>(ExpressionType::CONJUNCTION_AND, std::move(range), std::move(call));
		}
		return call;
	}
	// Outside the SQL channel's registry: the keys themselves, or their range.
	vector<Value> keys;
	int64_t lo = 0, hi = 0;
	if (!ReadPrefixRangeKeys(data, keys, lo, hi)) {
		return nullptr;
	}
	if (!keys.empty() && static_cast<uint64_t>(keys.size()) == static_cast<uint64_t>(hi) - static_cast<uint64_t>(lo) + 1) {
		keys.clear(); // every value of the range: the range says as much
	}
	if (keys.empty()) {
		auto ge = BoundComparisonExpression::Create(ExpressionType::COMPARE_GREATERTHANOREQUALTO, column.Copy(),
		                                            make_uniq<BoundConstantExpression>(Value::Numeric(data.key_type, lo)));
		auto le = BoundComparisonExpression::Create(ExpressionType::COMPARE_LESSTHANOREQUALTO, column.Copy(),
		                                            make_uniq<BoundConstantExpression>(Value::Numeric(data.key_type, hi)));
		return make_uniq<BoundConjunctionExpression>(ExpressionType::CONJUNCTION_AND, std::move(ge), std::move(le));
	}
	auto in = make_uniq<BoundOperatorExpression>(ExpressionType::COMPARE_IN, LogicalType::BOOLEAN);
	in->GetChildrenMutable().push_back(column.Copy());
	for (auto &k : keys) {
		in->GetChildrenMutable().push_back(make_uniq<BoundConstantExpression>(k));
	}
	return std::move(in);
}


//! Serialize one EXPRESSION_FILTER expression node into a FilterNode JSON object. Same contract as
//! LiveFilterNode: false = not convertible, which is skip-safe for the pruning consumer.
static bool LiveExprNode(const Expression &e, vector<string> &path, const LogicalType &type,
                         vector<Value> &constants, bool string_order_pushable, string &out) {
	// The internal filter wrappers first: recover the predicate they hide (the SQL channel's
	// RecoverFilterFunction verbatim, rebinding onto the wrapper's OWN argument so a struct subject
	// survives), else drop. Dropping only widens under an AND; an OR below refuses outright.
	if (e.GetExpressionClass() == ExpressionClass::BOUND_FUNCTION &&
	    TableFilterFunctions::IsTableFilterFunction(e.Cast<BoundFunctionExpression>().Function())) {
		auto &wrapper = e.Cast<BoundFunctionExpression>();
		BoundReferenceExpression fallback(type, 0ULL);
		const Expression &subject =
		    wrapper.GetChildren().empty() ? static_cast<const Expression &>(fallback) : *wrapper.GetChildren()[0];
		if (g_expand_prefix_ranges && wrapper.Function().GetName() == TableFilterPrefixRangeFun::Name &&
		    wrapper.BindInfo()) {
			// a hash join's keys as an IN list, or their range when there are too many (§5.5)
			auto &data = wrapper.BindInfo()->Cast<PrefixRangeFunctionData>();
			vector<Value> keys;
			int64_t lo = 0, hi = 0;
			LogicalType leaf;
			const idx_t base = path.size();
			if (data.key_type != type || !ReadPrefixRangeKeys(data, keys, lo, hi) ||
			    !LiveExprSubject(subject, type, path, leaf)) {
				path.resize(base);
				return false;
			}
			if (!keys.empty() && static_cast<uint64_t>(keys.size()) == static_cast<uint64_t>(hi) - static_cast<uint64_t>(lo) + 1) {
				keys.clear(); // every value of the range: the range says as much, and costs no list
			}
			if (keys.empty()) {
				const idx_t lo_idx = constants.size();
				constants.push_back(Value::Numeric(type, lo));
				constants.push_back(Value::Numeric(type, hi));
				out += "{\"op\":\"and\",\"children\":[{\"op\":\"compare\",\"cmp\":\">=\",";
				LiveJsonColumnRef(path, out);
				out += ",\"val\":" + to_string(lo_idx) + "},{\"op\":\"compare\",\"cmp\":\"<=\",";
				LiveJsonColumnRef(path, out);
				out += ",\"val\":" + to_string(lo_idx + 1) + "}]}";
			} else {
				out += "{\"op\":\"in\",";
				LiveJsonColumnRef(path, out);
				out += ",\"vals\":[";
				for (idx_t i = 0; i < keys.size(); i++) {
					if (i) {
						out += ',';
					}
					out += to_string(constants.size());
					constants.push_back(std::move(keys[i]));
				}
				out += "]}";
			}
			path.resize(base);
			return true;
		}
		auto recovered = RecoverFilterFunction(wrapper, subject);
		if (!recovered) {
			return false;
		}
		// A recovered child may itself be wrapped (an optional filter around a dynamic one).
		return LiveExprNode(*recovered, path, type, constants, string_order_pushable, out);
	}
	const idx_t base = path.size();
	switch (e.GetExpressionClass()) {
	case ExpressionClass::BOUND_CONJUNCTION: {
		const bool is_and = e.GetExpressionType() == ExpressionType::CONJUNCTION_AND;
		if (!is_and && e.GetExpressionType() != ExpressionType::CONJUNCTION_OR) {
			return false;
		}
		vector<string> parts;
		// Constants appended by a dropped OR branch must not leak — stage into a scratch vector first.
		vector<Value> scratch = constants;
		for (auto &child : e.Cast<BoundConjunctionExpression>().GetChildren()) {
			string cs;
			if (LiveExprNode(*child, path, type, scratch, string_order_pushable, cs) && !cs.empty()) {
				parts.push_back(std::move(cs));
			} else if (!is_and) {
				return false; // OR: dropping a branch would narrow (unsafe for pruning)
			}
			// AND: dropping a child only widens (superset) — fine.
		}
		if (parts.empty()) {
			return false;
		}
		constants = std::move(scratch);
		if (parts.size() == 1) {
			out += parts[0];
			return true;
		}
		out += "{\"op\":\"";
		out += is_and ? "and" : "or";
		out += "\",\"children\":[";
		for (idx_t i = 0; i < parts.size(); i++) {
			if (i) {
				out += ',';
			}
			out += parts[i];
		}
		out += "]}";
		return true;
	}
	case ExpressionClass::BOUND_OPERATOR: {
		auto &op = e.Cast<BoundOperatorExpression>();
		auto &children = op.GetChildren();
		if (children.empty()) {
			return false;
		}
		LogicalType leaf;
		if (!LiveExprSubject(*children[0], type, path, leaf)) {
			path.resize(base);
			return false;
		}
		bool ok = false;
		switch (e.GetExpressionType()) {
		case ExpressionType::OPERATOR_IS_NULL:
		case ExpressionType::OPERATOR_IS_NOT_NULL:
			if (children.size() == 1) {
				out += "{\"op\":\"";
				out += e.GetExpressionType() == ExpressionType::OPERATOR_IS_NULL ? "is_null" : "is_not_null";
				out += "\",";
				LiveJsonColumnRef(path, out);
				out += "}";
				ok = true;
			}
			break;
		case ExpressionType::COMPARE_IN: {
			// ⚠ COMPARE_NOT_IN deliberately falls to the default: refusing is always superset-safe.
			vector<Value> vals;
			for (idx_t i = 1; i < children.size(); i++) {
				if (children[i]->GetExpressionClass() != ExpressionClass::BOUND_CONSTANT) {
					vals.clear();
					break;
				}
				const auto &v = children[i]->Cast<BoundConstantExpression>().GetValue();
				if (v.IsNull()) {
					vals.clear();
					break;
				}
				vals.push_back(v);
			}
			if (vals.empty()) {
				break;
			}
			out += "{\"op\":\"in\",";
			LiveJsonColumnRef(path, out);
			out += ",\"vals\":[";
			for (idx_t i = 0; i < vals.size(); i++) {
				if (i) {
					out += ',';
				}
				out += to_string(constants.size());
				constants.push_back(vals[i]);
			}
			out += "]}";
			ok = true;
			break;
		}
		default:
			break;
		}
		path.resize(base);
		return ok;
	}
	case ExpressionClass::BOUND_FUNCTION: {
		// ⚠ A COMPARISON IS A FUNCTION EXPRESSION ON 2.0 — IsComparison covers exactly the eight binary
		// comparison types (BETWEEN is excluded; it arrives as COMPARE_BETWEEN, which we do not handle).
		if (!BoundComparisonExpression::IsComparison(e)) {
			return false;
		}
		auto &cmp_expr = e.Cast<BoundFunctionExpression>();
		auto &left = BoundComparisonExpression::Left(cmp_expr);
		auto &right = BoundComparisonExpression::Right(cmp_expr);
		auto cmp = e.GetExpressionType();
		optional_ptr<const Expression> subject;
		optional_ptr<const BoundConstantExpression> konst;
		if (right.GetExpressionClass() == ExpressionClass::BOUND_CONSTANT) {
			subject = left;
			konst = right.Cast<BoundConstantExpression>();
		} else if (left.GetExpressionClass() == ExpressionClass::BOUND_CONSTANT) {
			subject = right;
			konst = left.Cast<BoundConstantExpression>();
			cmp = FlipComparisonExpression(cmp); // the constant was on the LEFT
		} else {
			return false; // column-to-column: no constant to prune against
		}
		const char *tok = LiveCmpToken(cmp);
		if (!tok || konst->GetValue().IsNull()) {
			return false;
		}
		LogicalType leaf;
		if (!LiveExprSubject(*subject, type, path, leaf)) {
			path.resize(base);
			return false;
		}
		bool ok = true;
		if (leaf.id() == LogicalTypeId::VARCHAR && !string_order_pushable && string(tok) != "=") {
			ok = false; // string ordering: only pushable for a byte-ordered source
		}
		if (ok) {
			idx_t idx = constants.size();
			constants.push_back(konst->GetValue());
			out += "{\"op\":\"compare\",\"cmp\":\"";
			out += tok;
			out += "\",";
			LiveJsonColumnRef(path, out);
			out += ",\"val\":" + to_string(idx) + "}";
		}
		path.resize(base);
		return ok;
	}
	default:
		return false;
	}
}

// Serialize one live TableFilter on the column identified by `path` (leaf type `type`) into a FilterNode
// JSON object, appending its constants to `constants`. Returns false = not convertible (skip-safe for the
// pruning consumer). String ordering comparisons are gated like the bind-time serializer.
static bool LiveFilterNode(const TableFilter &f, vector<string> &path, const LogicalType &type,
                           vector<Value> &constants, bool string_order_pushable, string &out) {
	switch (f.filter_type) {
	case TableFilterType::LEGACY_OPTIONAL_FILTER: {
		auto &opt = f.Cast<LegacyOptionalFilter>();
		return opt.child_filter && LiveFilterNode(*opt.child_filter, path, type, constants, string_order_pushable, out);
	}
	case TableFilterType::LEGACY_DYNAMIC_FILTER: {
		auto &dyn = f.Cast<LegacyDynamicFilter>();
		if (!dyn.filter_data) {
			return false;
		}
		// ⚠ 2.0 reshaped DynamicFilterData: there is no inner `filter` to forward any more, it holds
		// comparison_type + constant directly. Rebuilding the equivalent LegacyConstantFilter keeps this
		// pruning path alive rather than dropping it (dropping would be SAFE — nothing relies on a dynamic
		// filter — but it would silently lose the pruning this exists for).
		// ⚠ `constant` is DUCKDB_GUARDED_BY(lock), so it is read under the SHARED lock — the same one
		// DynamicFilterData::ToExpression takes internally. `initialized` is atomic and needs none.
		if (!dyn.filter_data->initialized) {
			return false; // not yet materialized: nothing to prune with
		}
		annotated_shared_lock<annotated_shared_mutex> dyn_guard(dyn.filter_data->lock);
		LegacyConstantFilter resolved(dyn.filter_data->comparison_type, dyn.filter_data->constant);
		return LiveFilterNode(resolved, path, type, constants, string_order_pushable, out);
	}
	case TableFilterType::LEGACY_CONSTANT_COMPARISON: {
		auto &cf = f.Cast<LegacyConstantFilter>();
		const char *tok = LiveCmpToken(cf.comparison_type);
		if (!tok || cf.constant.IsNull()) {
			return false;
		}
		if (type.id() == LogicalTypeId::VARCHAR && !string_order_pushable && string(tok) != "=") {
			return false; // string ordering: only pushable for a byte-ordered source
		}
		idx_t idx = constants.size();
		constants.push_back(cf.constant);
		out += "{\"op\":\"compare\",\"cmp\":\"";
		out += tok;
		out += "\",";
		LiveJsonColumnRef(path, out);
		out += ",\"val\":" + to_string(idx) + "}";
		return true;
	}
	case TableFilterType::LEGACY_IS_NULL:
	case TableFilterType::LEGACY_IS_NOT_NULL:
		out += "{\"op\":\"";
		out += f.filter_type == TableFilterType::LEGACY_IS_NULL ? "is_null" : "is_not_null";
		out += "\",";
		LiveJsonColumnRef(path, out);
		out += "}";
		return true;
	case TableFilterType::LEGACY_IN_FILTER: {
		auto &in = f.Cast<LegacyInFilter>();
		if (in.values.empty()) {
			return false;
		}
		for (auto &v : in.values) {
			if (v.IsNull()) {
				return false;
			}
		}
		out += "{\"op\":\"in\",";
		LiveJsonColumnRef(path, out);
		out += ",\"vals\":[";
		for (idx_t i = 0; i < in.values.size(); i++) {
			if (i) {
				out += ',';
			}
			out += to_string(constants.size());
			constants.push_back(in.values[i]);
		}
		out += "]}";
		return true;
	}
	case TableFilterType::LEGACY_STRUCT_EXTRACT: {
		auto &sf = f.Cast<LegacyStructFilter>();
		if (type.id() != LogicalTypeId::STRUCT) {
			return false;
		}
		auto &members = StructType::GetChildTypes(type);
		if (sf.child_idx >= members.size()) {
			return false;
		}
		// Member name from the struct type at the bound index (exact case — matches parquet/stats keys).
		path.push_back(members[sf.child_idx].first.GetIdentifierName());
		bool ok = sf.child_filter &&
		          LiveFilterNode(*sf.child_filter, path, members[sf.child_idx].second, constants,
		                         string_order_pushable, out);
		path.pop_back();
		return ok;
	}
	case TableFilterType::LEGACY_CONJUNCTION_AND:
	case TableFilterType::LEGACY_CONJUNCTION_OR: {
		bool is_and = f.filter_type == TableFilterType::LEGACY_CONJUNCTION_AND;
		const LegacyConjunctionFilter &conj =
		    is_and ? static_cast<const LegacyConjunctionFilter &>(f.Cast<LegacyConjunctionAndFilter>())
		           : static_cast<const LegacyConjunctionFilter &>(f.Cast<LegacyConjunctionOrFilter>());
		vector<string> parts;
		// Constants appended by a dropped OR branch must not leak — stage into a scratch vector first.
		vector<Value> scratch = constants;
		for (auto &child : conj.child_filters) {
			string cs;
			if (LiveFilterNode(*child, path, type, scratch, string_order_pushable, cs)) {
				parts.push_back(std::move(cs));
			} else if (!is_and) {
				return false; // OR: dropping a branch would narrow (unsafe for pruning)
			}
			// AND: dropping a child only widens (superset) — fine.
		}
		if (parts.empty()) {
			return false;
		}
		constants = std::move(scratch);
		if (parts.size() == 1) {
			out += parts[0];
			return true;
		}
		out += "{\"op\":\"";
		out += is_and ? "and" : "or";
		out += "\",\"children\":[";
		for (idx_t i = 0; i < parts.size(); i++) {
			if (i) {
				out += ',';
			}
			out += parts[i];
		}
		out += "]}";
		return true;
	}
	case TableFilterType::EXPRESSION_FILTER: {
		// 2.0's optimizer hands us THIS and nothing else; the block above says why the old
		// "not convertible" default was silently fatal to pruning.
		auto &ef = f.Cast<ExpressionFilter>();
		// ⚠ A single-column filter carries no `column_indexes` (TableFilterSet::PushFilter refuses one
		// that does) and a multi-column filter lives in TableFilterSet::multi_column_filters, which this
		// iteration never reaches. A non-empty one here would mean the subject is NOT this entry's column,
		// so refuse rather than prune on the wrong one.
		if (!ef.expr || !ef.column_indexes.empty()) {
			return false;
		}
		return LiveExprNode(*ef.expr, path, type, constants, string_order_pushable, out);
	}
	default:
		return false; // BLOOM_FILTER / anything new: not convertible (bloom is probabilistic anyway)
	}
}

// Serialize the scan's live TableFilterSet into one FilterNode JSON tree (implicit AND across columns),
// appending its constants to the per-execution `constants` vector. Empty unless the scan advertised
// filter_pushdown (the exact-mode catalog) and DuckDB delivered filters.
//! The AND-leaves of a FilterNode tree as compact JSON: an "and" node is flattened, anything else is one leaf.
static void FilterLeaves(yyjson_val *node, vector<string> &out) {
	auto op = yyjson_get_str(yyjson_obj_get(node, "op"));
	if (op && string(op) == "and") {
		size_t idx, max;
		yyjson_val *child;
		yyjson_arr_foreach(yyjson_obj_get(node, "children"), idx, max, child) {
			FilterLeaves(child, out);
		}
		return;
	}
	size_t len = 0;
	char *text = yyjson_val_write(node, 0, &len);
	if (text) {
		out.emplace_back(text, len);
		free(text);
	}
}

static vector<string> FilterLeaves(const string &json) {
	vector<string> out;
	auto doc = yyjson_read(json.c_str(), json.size(), 0);
	if (!doc) {
		out.push_back(json);
		return out;
	}
	FilterLeaves(yyjson_doc_get_root(doc), out);
	yyjson_doc_free(doc);
	return out;
}

//! A filter leaf with each constant reference ("val": i, "vals": [i, …]) replaced by the constant's type and SQL
//! text, so two renders of one predicate compare equal whichever constants list they index.
static string CanonicalFilterLeaf(const string &leaf, const vector<Value> &constants) {
	string out;
	auto constant = [&](idx_t index) {
		string text = index < constants.size()
		                  ? constants[index].type().ToString() + "|" + constants[index].ToSQLString()
		                  : "?" + to_string(index);
		JsonEscape(text, out);
	};
	auto digits = [&](idx_t &i, idx_t &index) {
		idx_t start = i;
		index = 0;
		while (i < leaf.size() && leaf[i] >= '0' && leaf[i] <= '9') {
			index = index * 10 + idx_t(leaf[i] - '0');
			i++;
		}
		return i > start;
	};
	for (idx_t i = 0; i < leaf.size();) {
		if (leaf.compare(i, 6, "\"val\":") == 0) {
			out += "\"val\":";
			i += 6;
			idx_t index;
			if (digits(i, index)) {
				constant(index);
			}
		} else if (leaf.compare(i, 8, "\"vals\":[") == 0) {
			out += "\"vals\":[";
			i += 8;
			idx_t index;
			while (digits(i, index)) {
				constant(index);
				if (i < leaf.size() && leaf[i] == ',') {
					out += ',';
					i++;
				}
			}
		} else {
			out += leaf[i++];
		}
	}
	return out;
}

//! The live filter without the leaves the bind-time filter already holds. DuckDB both hands a pushed predicate to
//! pushdown_complex_filter and erases it into the scan's table filters, so the two halves of the spec repeat it — it
//! reached SQL Server as `([yr] = @p0 AND [yr] = @p1)`. Constants stay where they are: an unreferenced one is unused.
static string WithoutBindDuplicates(const string &bind_json, const string &live_json, const vector<Value> &constants) {
	if (bind_json.empty() || live_json.empty()) {
		return live_json;
	}
	std::unordered_set<string> bound;
	for (auto &leaf : FilterLeaves(bind_json)) {
		bound.insert(CanonicalFilterLeaf(leaf, constants));
	}
	vector<string> kept;
	for (auto &leaf : FilterLeaves(live_json)) {
		if (!bound.count(CanonicalFilterLeaf(leaf, constants))) {
			kept.push_back(std::move(leaf));
		}
	}
	if (kept.empty()) {
		return string();
	}
	if (kept.size() == 1) {
		return kept[0];
	}
	string json = "{\"op\":\"and\",\"children\":[";
	for (idx_t i = 0; i < kept.size(); i++) {
		if (i) {
			json += ',';
		}
		json += kept[i];
	}
	return json + "]}";
}

//! While a scan renders its live filters: where the join filters it registers are collected (null = no registry,
//! a join filter is then rendered as its keys or dropped).
struct JoinFilterIdsScope {
	explicit JoinFilterIdsScope(vector<idx_t> *ids) : saved(g_join_filter_ids) {
		g_join_filter_ids = ids;
	}
	~JoinFilterIdsScope() {
		g_join_filter_ids = saved;
	}
	vector<idx_t> *saved;
};

static string SerializeLiveFilters(const ArrowStreamBindData &bind_data, TableFunctionInitInput &input,
                                   vector<Value> &constants, bool join_key_lists) {
	if (!input.filters) {
		return string();
	}
	// no join filter registry in the tree: a hash join's filter goes as its keys or their range, never as a call
	JoinFilterIdsScope ids_scope(nullptr);
	// a hash join's prefix-range filter becomes its keys (or their range). Safe for every provider: it is a join
	// filter, and the join checks its keys itself, so a superset never changes a result. It used to be limited to
	// providers that re-apply what they are sent (SQL Server), which left Delta without join filters.
	PrefixRangeScope expand_scope(join_key_lists);
	vector<string> parts;
	for (auto entry : *input.filters) {
		idx_t key = entry.GetIndex();
		if (key >= input.column_ids.size()) {
			continue;
		}
		auto col_id = input.column_ids[key];
		string col_name;
		LogicalType col_type;
		if (col_id == COLUMN_IDENTIFIER_ROW_ID) {
			// A filter on the ROWID column (late-materialization semi join / WHERE rowid = ...): resolvable
			// only for a single VIRTUAL rowid (Delta's `_metadata.row_id`, a named provider column). Exact
			// mode is native_read-only, whose rowid is always that single virtual BIGINT — so a mandatory
			// erased rowid filter is never silently dropped here.
			if (bind_data.virtual_rowid_columns.size() != 1) {
				continue;
			}
			col_name = bind_data.virtual_rowid_columns[0];
			col_type = bind_data.rowid_type;
		} else if (col_id >= ProviderVirtualBase() &&
		           col_id < ProviderVirtualBase() + bind_data.provider_virtual_columns.size()) {
			// Provider virtual column (e.g. Delta's stable __delta_row_id): filter by declared name/type.
			auto &vc = bind_data.provider_virtual_columns[(idx_t)(col_id - ProviderVirtualBase())];
			col_name = vc.first;
			col_type = vc.second;
		} else if ((idx_t)col_id >= bind_data.names.size()) {
			continue; // out-of-range/virtual: no filter pushed on those
		} else {
			col_name = bind_data.names[(idx_t)col_id];
			col_type = (idx_t)col_id < bind_data.return_types.size() ? bind_data.return_types[(idx_t)col_id]
			                                                         : LogicalType(LogicalTypeId::ANY);
		}
		vector<string> path {col_name};
		LogicalType type = col_type;
		// a filter on a pushed-down struct field: the field's path below the column. Not for a field under a
		// CAST (the provider would prune the cast value against the field's statistics) or an unnamed struct.
		ExtractPath extract;
		if (key < input.column_indexes.size() && ReadExtractPath(input.column_indexes[key], col_type, extract)) {
			bool has_unnamed = false;
			for (auto u : extract.unnamed) {
				has_unnamed = has_unnamed || u;
			}
			if (extract.HasCast() || has_unnamed) {
				continue;
			}
			for (auto &n : extract.names) {
				path.push_back(n);
			}
			type = extract.field_type;
		}
		string node;
		if (LiveFilterNode(entry.Filter(), path, type, constants, bind_data.string_order_pushable, node) &&
		    !node.empty()) {
			parts.push_back(std::move(node));
		}
	}
	if (parts.empty()) {
		return string();
	}
	if (parts.size() == 1) {
		return parts[0];
	}
	string json = "{\"op\":\"and\",\"children\":[";
	for (idx_t i = 0; i < parts.size(); i++) {
		if (i) {
			json += ',';
		}
		json += parts[i];
	}
	json += "]}";
	return json;
}

// Render the scan's LIVE TableFilterSet (static WHERE constants that DuckDB erased from the plan under
// filter_pushdown=true, PLUS any runtime dynamic/join filters) into one DuckDB SQL predicate for the native
// (read_parquet) path. Column keys map through the scanned column list to provider names (exactly as
// PhysicalTableScan::GetFilterInfo does). Empty unless the scan advertised filter_pushdown (the exact-filter
// native catalog) and DuckDB delivered filters. See docs/multifile-delta.md §"Batch 2 slice 2".
static string RenderLiveFilters(const ArrowStreamBindData &bind_data, TableFunctionInitInput &input,
                                bool join_key_lists, vector<idx_t> &join_filter_ids) {
	if (!input.filters) {
		return string();
	}
	// a hash join's prefix-range filter as fabricator_join_filter(<id>, col) (PrefixRangeAsExpression); the ids
	// it registers go to the scan's global state, which removes them when the scan ends
	PrefixRangeScope expand_scope(join_key_lists);
	JoinFilterIdsScope ids_scope(&join_filter_ids);
	vector<string> conds;
	for (auto entry : *input.filters) {
		idx_t key = entry.GetIndex();
		if (key >= input.column_ids.size()) {
			continue;
		}
		auto col_id = input.column_ids[key];
		string col_name;
		if (col_id == COLUMN_IDENTIFIER_ROW_ID) {
			// Rowid filter (late-materialization semi join / WHERE rowid = ...): render against the virtual
			// rowid's provider name — the native reader's per-file SELECT aliases the rowid expression to
			// exactly that name, and DuckDB permits SELECT-alias references in WHERE.
			if (bind_data.virtual_rowid_columns.size() != 1) {
				continue;
			}
			col_name = bind_data.virtual_rowid_columns[0];
		} else if (col_id >= ProviderVirtualBase() &&
		           col_id < ProviderVirtualBase() + bind_data.provider_virtual_columns.size()) {
			// Provider virtual column: the per-file SELECT aliases it to its declared name too.
			col_name = bind_data.provider_virtual_columns[(idx_t)(col_id - ProviderVirtualBase())].first;
		} else if ((idx_t)col_id >= bind_data.names.size()) {
			continue; // out-of-range/virtual: no filter pushed on those
		} else {
			col_name = bind_data.names[(idx_t)col_id];
		}
		string qcol;
		SqlIdentIngest(col_name, qcol);
		// a filter on a pushed-down struct field: the field (and a cast DuckDB folded on top) stands in for the
		// column, so the inner query evaluates it on the same value the scan emits
		if ((idx_t)col_id < bind_data.return_types.size() && key < input.column_indexes.size()) {
			ExtractPath extract;
			if (ReadExtractPath(input.column_indexes[key], bind_data.return_types[(idx_t)col_id], extract)) {
				qcol = ExtractPathSql(qcol, extract);
			}
		}
		string rendered;
		bool ok = RenderTableFilter(entry.Filter(), qcol, rendered);
		// ⚠ SHIPPED, env-gated, like the Fabricator.Memory marks and the Arrow liveness registry: what a
		// pushed filter RENDERS TO is invisible from SQL (a predicate that silently fails to render is a
		// performance regression with no message, and one that renders WRONG binds nowhere). This print is
		// what found the EXPRESSION_FILTER/internal-function defect in minutes after four passes of reading
		// had not — and it is the instrument the EXPRESSION_FILTER migration will need.
		if (getenv("FABRICATOR_DUMP_FILTERS")) {
			// The UNPRUNED expression too: "rendered=no" alone cannot say whether the filter was a wrapper
			// we dropped, a shape we cannot render, or absent — and those want different fixes.
			string raw;
			try {
				BoundColumnRefExpression probe(Identifier(qcol), LogicalType::ANY, ColumnBinding());
				auto e = entry.Filter().ToExpression(probe);
				raw = e ? e->ToString() : "<null>";
			} catch (...) {
				raw = "<throws>";
			}
			Printer::Print("FILTER type=" + EnumUtil::ToString(entry.Filter().filter_type) +
			               " rendered=" + (ok ? "yes" : "no") + " sql=[" + rendered + "] raw=[" + raw + "]");
		}
		if (ok && !rendered.empty()) {
			conds.push_back(std::move(rendered));
		}
	}
	if (conds.empty()) {
		return string();
	}
	string sql;
	for (idx_t i = 0; i < conds.size(); i++) {
		if (i) {
			sql += " AND ";
		}
		sql += conds[i];
	}
	return sql;
}

string BuildSchemaOnlySpec(const string &at_unit, const string &at_value) {
	string json = "{\"schema_only\":true";
	// Mirrors the "at" encoding BuildScanSpec emits below, EXACTLY — same key, same escaping. The describe
	// and the scan must name the same version or the plan is built against a schema the scan will not return.
	if (!at_unit.empty()) {
		json += ",\"at\":{\"unit\":";
		JsonEscape(at_unit, json);
		json += ",\"value\":";
		JsonEscape(at_value, json);
		json += "}";
	}
	json += "}";
	return json;
}

static string BuildScanSpec(const ArrowStreamBindData &bind_data, const vector<column_t> &output_column_ids,
                            const string &live_filter_sql, const string &live_filter_json, bool has_live_filters) {
	vector<string> cols;
	auto add = [&](idx_t table_idx) {
		if (table_idx >= bind_data.names.size()) {
			return;
		}
		const string &n = bind_data.names[table_idx];
		if (std::find(cols.begin(), cols.end(), n) == cols.end()) {
			cols.push_back(n);
		}
	};
	bool need_rowid = false;
	auto virtual_base = ProviderVirtualBase();
	for (auto col_id : output_column_ids) {
		if (col_id == COLUMN_IDENTIFIER_ROW_ID) {
			need_rowid = true;
		} else if (col_id >= virtual_base && col_id < virtual_base + bind_data.provider_virtual_columns.size()) {
			// Provider virtual column (e.g. Delta's stable __delta_row_id): fetch by its declared name.
			const string &vn = bind_data.provider_virtual_columns[(idx_t)(col_id - virtual_base)].first;
			if (std::find(cols.begin(), cols.end(), vn) == cols.end()) {
				cols.push_back(vn);
			}
		} else {
			add((idx_t)col_id);
		}
	}
	if (need_rowid) {
		for (auto src : bind_data.rowid_source_columns) {
			add(src);
		}
		// Virtual rowid columns are names not present in the user schema (e.g. Delta's `_metadata.row_id`);
		// add them to the fetch list directly so the provider supplies them on this scan.
		for (auto &vname : bind_data.virtual_rowid_columns) {
			if (std::find(cols.begin(), cols.end(), vname) == cols.end()) {
				cols.push_back(vname);
			}
		}
	}
	if (cols.empty() && !bind_data.names.empty()) {
		cols.push_back(bind_data.names[0]);
	}

	string json = "{\"columns\":[";
	for (idx_t i = 0; i < cols.size(); i++) {
		if (i) {
			json += ',';
		}
		JsonEscape(cols[i], json);
	}
	json += "]";
	// The structured predicate for provider PRUNING: the bind-time serialization (incl. cross-column
	// disjunction shapes the per-column TableFilterSet can't represent) AND-merged with the live
	// TableFilterSet render (which adds the runtime dynamic/join filters). Both are true predicates of
	// the query, so the conjunction is superset-correct; duplicated statics are idempotent for pruning.
	if (!bind_data.filter_json.empty() || !live_filter_json.empty()) {
		json += ",\"filter\":";
		if (!bind_data.filter_json.empty() && !live_filter_json.empty()) {
			json += "{\"op\":\"and\",\"children\":[";
			json += bind_data.filter_json;
			json += ',';
			json += live_filter_json;
			json += "]}";
		} else {
			json += bind_data.filter_json.empty() ? live_filter_json : bind_data.filter_json;
		}
	}
	// A 1:1 SQL rendering of the same predicates (literals inlined) — consumed only by a provider whose scan
	// target is DuckDB itself (native Delta read_parquet); foreign-engine providers ignore it. Two sources:
	//   • `live_filter_sql` (slice 2): the execute-time TableFilterSet (DuckDB-erased static filters + dynamic
	//     join filters; filter_pushdown=true only). Holds the MANDATORY filters and is per-column complete.
	//   • `bind_data.native_filter_sql` (slice 1): the bind-time pushdown_complex_filter render.
	// When live filters fired they SUBSUME the bind render for per-column predicates (and push more — e.g. the
	// comparisons the superset-safe bind subset drops), so we emit ONLY the live filter to avoid the
	// double-render of the same predicate. Fall back to the bind render only when no live filters fired.
	// Correctness-neutral: the sole thing not emitted when live filters win is read_parquet row-group pruning
	// for cross-column disjunctions (which the per-column TableFilterSet can't represent) — file-level pruning
	// still uses filter_json, and DuckDB re-applies the complex filter above the scan. See docs/multifile-delta.md.
	const string &native_filter = !live_filter_sql.empty() ? live_filter_sql : bind_data.native_filter_sql;
	if (!native_filter.empty()) {
		json += ",\"native_filter\":";
		JsonEscape(native_filter, json);
	}
	// TOP (n) is only safe with no pushed filter: a best-effort filter returns a
	// superset, so limiting before exact (DuckDB) filtering could drop valid rows. ⚠ That includes a live filter
	// that did not serialize: the scan re-applies it (reapply_filters) after the server's TOP.
	if (bind_data.top_n >= 0 && bind_data.filter_json.empty() && live_filter_json.empty() && !has_live_filters) {
		json += ",\"top\":" + to_string(bind_data.top_n);
	}
	if (!bind_data.order_by_json.empty() && bind_data.filter_json.empty() && live_filter_json.empty() &&
	    !has_live_filters) {
		json += ",\"order_by\":" + bind_data.order_by_json; // already a JSON array
	}
	// Time travel (AT clause): orthogonal to projection/filter/order — always emitted when set so the
	// provider applies it as a table-level temporal qualifier (SQL Server: FOR SYSTEM_TIME AS OF).
	if (!bind_data.at_unit.empty()) {
		json += ",\"at\":{\"unit\":";
		JsonEscape(bind_data.at_unit, json);
		json += ",\"value\":";
		JsonEscape(bind_data.at_value, json);
		json += "}";
	}
	// Same-catalog read+write in one plan: NAME THE SINK and let the provider decide whether that costs
	// anything (see ArrowStreamBindData::sink_table). Its presence alone carries what the old
	// `"materialize":true` said — "a sink of this catalog is in this plan" — so a provider that only wants
	// the boolean reads `sink != null`. Emitted only when set, so an ordinary scan's spec is byte-identical
	// to before.
	if (bind_data.HasSink()) {
		json += ",\"sink\":{\"schema\":";
		JsonEscape(bind_data.sink_schema, json);
		json += ",\"table\":";
		JsonEscape(bind_data.sink_table, json);
		json += ",\"kind\":";
		JsonEscape(bind_data.sink_kind, json);
		json += "}";
	}
	json += "}";
	return json;
}

// Materializes the filter constants as a one-row Arrow batch (column i == value i),
// so the provider can build a parameterized WHERE with exact types. The returned producer must outlive the
// CONSUMER's use of the stream, which outlasts the scan_table/tablefn_execute call: the managed side may hold
// the imported stream and release it lazily (see ArrowStreamGlobalState::filter_value_producer). Callers must
// therefore keep it for the whole scan — never as a local scoped to the factory call.
static unique_ptr<fabricator::ArrowProducer> BuildFilterValues(ClientContext &context, const vector<Value> &consts) {
	vector<LogicalType> types;
	vector<string> names;
	for (idx_t i = 0; i < consts.size(); i++) {
		types.push_back(consts[i].type());
		names.push_back("v" + to_string(i));
	}
	auto props = fabricator::BoundaryClientProperties(context);
	auto producer = make_uniq<fabricator::ArrowProducer>(types, names, props);

	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, types);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(context), types, 1);
	chunk.SetCardinality(1);
	for (idx_t i = 0; i < consts.size(); i++) {
		chunk.SetValue(i, 0, consts[i]);
	}
	ArrowAppender appender(types, 1, props, extension_types);
	appender.Append(chunk, 0, 1, 1);
	ArrowArray array = appender.Finalize();
	producer->AddBatch(array);
	producer->Finish();
	return producer;
}

unique_ptr<fabricator::ArrowProducer> FilterValueProducer(ClientContext &context, const vector<Value> &consts) {
	return BuildFilterValues(context, consts);
}

// Resolves each requested output column (and each rowid source) to a position in the
// actual result. By NAME when projection was pushed (the result is a subset), else by
// positional identity (the full result is returned in table order).
static void BuildProjectionMapping(const ArrowStreamBindData &bind_data, const vector<string> &scan_names,
                                   ArrowStreamGlobalState &gstate) {
	std::unordered_map<string, idx_t> pos_by_name;
	for (idx_t i = 0; i < scan_names.size(); i++) {
		pos_by_name[scan_names[i]] = i;
	}
	auto resolve = [&](idx_t table_idx) -> idx_t {
		if (!bind_data.push_projection) {
			return table_idx; // full result, positional
		}
		if (table_idx < bind_data.names.size()) {
			auto it = pos_by_name.find(bind_data.names[table_idx]);
			if (it != pos_by_name.end()) {
				return it->second;
			}
		}
		return 0;
	};

	gstate.output_source_pos.clear();
	auto virtual_base = fabricator::ProviderVirtualBase();
	for (auto col_id : gstate.output_column_ids) {
		if (col_id == COLUMN_IDENTIFIER_ROW_ID) {
			gstate.output_source_pos.push_back(-1);
		} else if (col_id >= virtual_base &&
		           col_id < virtual_base + bind_data.provider_virtual_columns.size()) {
			// Provider virtual column: a plain 1:1 result column, resolved BY NAME (it has no table index).
			auto it = pos_by_name.find(bind_data.provider_virtual_columns[(idx_t)(col_id - virtual_base)].first);
			gstate.output_source_pos.push_back(it != pos_by_name.end() ? (int64_t)it->second : 0);
		} else {
			gstate.output_source_pos.push_back((int64_t)resolve((idx_t)col_id));
		}
	}
	gstate.rowid_source_pos.clear();
	for (auto src : bind_data.rowid_source_columns) {
		gstate.rowid_source_pos.push_back(resolve(src));
	}
	// Virtual rowid columns (e.g. Delta `_metadata.row_id`): resolved BY NAME in the result (they have no
	// table index). The provider always returns them in the result when requested, so they're in scan_names.
	for (auto &vname : bind_data.virtual_rowid_columns) {
		auto it = pos_by_name.find(vname);
		gstate.rowid_source_pos.push_back(it != pos_by_name.end() ? it->second : 0);
	}
}

unique_ptr<GlobalTableFunctionState> ArrowStreamInitGlobal(ClientContext &context,
                                                           TableFunctionInitInput &input) {
	auto &bind_data = input.bind_data->Cast<ArrowStreamBindData>();
	auto gstate = make_uniq<ArrowStreamGlobalState>();
	// Parallelism = the database's thread count (`SET threads`). See MaxThreads() on the global state.
	gstate->scan_max_threads = MaxValue<idx_t>(context.db->NumberOfThreads(), 1);
	// The scan's OUTPUT columns. projection_ids (indexes into column_ids; empty = all) is DuckDB's
	// filter_prune contract — an optimizer-pruned output subset of the scanned columns. Our functions
	// don't set filter_prune today (see the struct-filter NOTE in fabricator_table_entry.cpp), so this is
	// currently always the empty=identity case; honoring it keeps the scan correct if that ever changes.
	// The provider FETCH below stays the full column_ids so filter-referenced columns remain available
	// to the native WHERE / the provider's exact per-batch filter.
	if (input.projection_ids.empty()) {
		gstate->output_column_ids = input.column_ids;
	} else {
		for (auto proj : input.projection_ids) {
			gstate->output_column_ids.push_back(input.column_ids[proj]);
		}
	}
	// a pushed-down struct field in an output position: the scan fetches the whole column (BuildScanSpec asks for
	// it by name) and emits the field (ArrowStreamScanChunk)
	for (idx_t k = 0; k < gstate->output_column_ids.size(); k++) {
		idx_t in_idx = input.projection_ids.empty() ? k : input.projection_ids[k];
		ExtractPath extract;
		auto col_id = gstate->output_column_ids[k];
		if (in_idx < input.column_indexes.size() && col_id < bind_data.return_types.size()) {
			if (input.column_indexes[in_idx].IsPushdownExtract() &&
			    !ReadExtractPath(input.column_indexes[in_idx], bind_data.return_types[col_id], extract)) {
				// the scan only declares struct fields; anything else here is a contract it cannot keep
				throw InternalException("fabricator: a pushed-down extract the scan does not support");
			}
		}
		gstate->output_extract.push_back(std::move(extract));
	}
	if (bind_data.reapply_filters && input.filters) {
		vector<unique_ptr<Expression>> conjuncts;
		for (auto entry : *input.filters) {
			idx_t key = entry.GetIndex(); // an index into column_ids
			idx_t out = key;
			if (!input.projection_ids.empty()) {
				out = DConstants::INVALID_INDEX;
				for (idx_t k = 0; k < input.projection_ids.size(); k++) {
					if (input.projection_ids[k] == key) {
						out = k;
					}
				}
			}
			if (out == DConstants::INVALID_INDEX || out >= gstate->output_column_ids.size()) {
				// filter_prune is not set, so a filtered column is always an output column; were it not, the filter
				// could not be re-applied and the server's superset would stand — refuse instead
				throw InternalException("fabricator: a pushed filter on a column the scan does not return");
			}
			auto col_id = input.column_ids[key];
			LogicalType col_type;
			if (gstate->output_extract[out].Active()) {
				// a pushed-down struct field: the filter is on the field the scan emits (after a folded CAST), not
				// on the struct column
				col_type = gstate->output_extract[out].scan_type;
			} else if (col_id < bind_data.return_types.size()) {
				col_type = bind_data.return_types[col_id];
			} else if (col_id == COLUMN_IDENTIFIER_ROW_ID && bind_data.rowid_type.id() != LogicalTypeId::INVALID) {
				col_type = bind_data.rowid_type; // `WHERE rowid > …`: the scan returns the row id, so it re-checks it
			} else if (col_id >= ProviderVirtualBase() &&
			           col_id < ProviderVirtualBase() + bind_data.provider_virtual_columns.size()) {
				col_type = bind_data.provider_virtual_columns[(idx_t)(col_id - ProviderVirtualBase())].second;
			} else {
				throw InternalException("fabricator: a pushed filter on a virtual column");
			}
			BoundReferenceExpression column(col_type, out);
			conjuncts.push_back(entry.Filter().ToExpression(column));
		}
		if (conjuncts.size() == 1) {
			gstate->reapply_filter = std::move(conjuncts[0]);
		} else if (!conjuncts.empty()) {
			auto conjunction = make_uniq<BoundConjunctionExpression>(ExpressionType::CONJUNCTION_AND);
			for (auto &conjunct : conjuncts) {
				conjunction->GetChildrenMutable().push_back(std::move(conjunct));
			}
			gstate->reapply_filter = std::move(conjunction);
		}
	}

	// Catalog scans push the projected column list (and superset-safe filters) to the
	// provider; raw queries fetch the full result (the SQL is user-supplied).
	ArrowScanRequest request;
	// a pushed query that takes table filters (reapply_filters) sends them too, under a spec over its own columns
	if (bind_data.push_projection || bind_data.reapply_filters) {
		// Render the live runtime filters (static-erased + dynamic/join) for an exact-filter scan
		// (filter_pushdown=true); empty for SQL/DAX/non-exact (input.filters null). This runs per execution,
		// so a hash-join dynamic filter materialized before the probe scan is captured here — twice: as the
		// exact SQL the provider APPLIES (native_filter) and as the structured FilterNode tree it PRUNES
		// with (file/row-group skipping; the constants extend a PER-EXECUTION copy of the bind constants —
		// bind data is shared across executions and must not be mutated).
		// the switch for reading a hash join's prefix-range filter back as keys (§5.5): DuckDB's own structure, so
		// the way out should DuckDB ever get it wrong
		Value join_key_lists;
		bool read_key_lists = !(context.TryGetCurrentSetting("fabricator_join_key_lists", join_key_lists) &&
		                        !join_key_lists.IsNull() && !join_key_lists.GetValue<bool>());
		string live_filter_sql = RenderLiveFilters(bind_data, input, read_key_lists, gstate->join_filter_ids);
		vector<Value> filter_constants = bind_data.filter_constants;
		string live_filter_json = WithoutBindDuplicates(
		    bind_data.filter_json, SerializeLiveFilters(bind_data, input, filter_constants, read_key_lists),
		    filter_constants);
		if (getenv("FABRICATOR_DUMP_FILTERS")) {
			// the pruning channel too: what the provider receives as its filter TREE
			Printer::Print("FILTER JSON=" + (live_filter_json.empty() ? string("<none>") : live_filter_json));
		}
		// a filter the scan re-applies after the server's TOP must not be able to remove one of its rows
		bool has_live_filters = false;
		if (input.filters) {
			for (auto entry : *input.filters) {
				has_live_filters = has_live_filters || IsMandatoryFilter(entry.Filter());
			}
		}
		request.spec_json = BuildScanSpec(bind_data, input.column_ids, live_filter_sql, live_filter_json,
		                                  has_live_filters);
		if (!filter_constants.empty()) {
			// Owned by the scan's global state, NOT this frame — the consumer may release it lazily.
			gstate->filter_value_producer = BuildFilterValues(context, filter_constants);
			request.filter_values = gstate->filter_value_producer->Stream();
		}
	}
	// Key this scan's connection to the active DuckDB transaction so a read inside an explicit transaction
	// sees the transaction's own uncommitted writes (read-your-writes); the factory's scan_table/execute_query
	// runs synchronously on this thread, so the managed per-thread ambient set here governs which connection
	// it borrows. (handle is unused by set_active_txn — the ambient is global per-thread.) See
	// docs/transaction-concurrency.md.
	SetActiveTxn(nullptr, (int64_t)MetaTransaction::Get(context).global_transaction_id);
	// Set the active host-FS opener (this execution's context) so a global host-FS table function (a
	// lakehouse reader) resolves DuckDB secrets while reading its data through the host FileSystem; SQL
	// scans ignore it. The factory runs synchronously on this thread, so the per-thread ambient governs it.
	SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), SessionKeyFor(&context));
	gstate->context = &context;
	bind_data.factory(request, gstate->stream);
	gstate->stream_initialized = true;

	// Read the ACTUAL result schema (a subset when projection was pushed) and build
	// per-scan converters + DuckDB types from it.
	if (gstate->stream.get_schema(&gstate->stream, &gstate->scan_schema_root.arrow_schema) != 0) {
		const char *msg = gstate->stream.get_last_error ? gstate->stream.get_last_error(&gstate->stream) : nullptr;
		throw IOException(string("Fabricator: failed to read scan schema") + (msg ? string(": ") + msg : string()));
	}
	ArrowTableFunction::PopulateArrowTableSchema(context, gstate->scan_arrow_table,
	                                             gstate->scan_schema_root.arrow_schema);
	auto &sch = gstate->scan_schema_root.arrow_schema;
	vector<string> scan_names;
	for (int64_t i = 0; i < sch.n_children; i++) {
		scan_names.push_back(sch.children[i]->name ? string(sch.children[i]->name) : "column" + to_string(i));
		gstate->scan_types.push_back(gstate->scan_arrow_table.GetColumns().at((idx_t)i)->GetDuckType());
	}

	BuildProjectionMapping(bind_data, scan_names, *gstate);
	return std::move(gstate);
}

unique_ptr<LocalTableFunctionState> ArrowStreamInitLocal(ExecutionContext &context,
                                                         TableFunctionInitInput &input,
                                                         GlobalTableFunctionState *global_state) {
	auto &gstate = global_state->Cast<ArrowStreamGlobalState>();
	auto current_chunk = make_uniq<ArrowArrayWrapper>();
	auto lstate = make_uniq<ArrowStreamLocalState>(std::move(current_chunk), context.client);

	// We ingest every result column into `all_columns` (identity), then project /
	// synthesize the requested output columns (mapping computed in InitGlobal). So
	// clear ArrowToDuckDB's column_ids (=> identity). `all_columns` is sized to the
	// ACTUAL result schema (the projected subset when projection was pushed).
	lstate->column_ids.clear();
	lstate->all_columns.Initialize(context.client, gstate.scan_types);
	return std::move(lstate);
}

// Builds the rowid output vector from the PK/unique source columns of `all_columns`
// (positions in the result, computed in InitGlobal).
static void BuildRowId(const vector<idx_t> &sources, DataChunk &all_columns, Vector &out, idx_t count) {
	if (sources.size() == 1) {
		// Scalar rowid == the single key column's value.
		out.Reference(all_columns.data[sources[0]]);
		return;
	}
	// Compound key: rowid is a STRUCT of the key columns.
	out.SetVectorType(VectorType::FLAT_VECTOR);
	auto &entries = StructVector::GetEntries(out);
	for (idx_t i = 0; i < sources.size(); i++) {
		entries[i].Reference(all_columns.data[sources[i]]);
	}
}

// The provider's stream carries a different type than the plan was bound with. It happens when the table was
// changed OUTSIDE this catalog after its entry was built (another process altered a column's type): the entry,
// and the plan built from it, still hold the old type. Vector::Reference would refuse the pair with an
// INTERNAL error, which invalidates the whole database; a clean error does not, and the failed statement's
// rollback drops every catalog entry, so the next statement binds against the new schema.
[[noreturn]] static void ThrowSchemaChanged(const ArrowStreamBindData &bind_data, column_t col_id,
                                            const LogicalType &actual, const LogicalType &expected) {
	string column = col_id < bind_data.names.size() ? bind_data.names[(idx_t)col_id] : "column " + to_string(col_id);
	string table = bind_data.table ? bind_data.table->name.GetIdentifierName() : string("the table");
	string catalog = bind_data.table ? bind_data.table->ParentCatalog().GetName().GetIdentifierName() : string();
	throw IOException("Fabricator: column '%s' of '%s' is %s on the provider but %s in the catalog — the table was "
	                  "changed outside this session. The next statement sees the new schema; to refresh explicitly, "
	                  "run SELECT fabricator_refresh_cache('%s').",
	                  column, table, actual.ToString(), expected.ToString(), catalog);
}

static void ArrowStreamScanChunk(ClientContext &context, TableFunctionInput &data, DataChunk &output);

void ArrowStreamScan(ClientContext &context, TableFunctionInput &data, DataChunk &output) {
	auto &gstate = data.global_state->Cast<ArrowStreamGlobalState>();
	auto &lstate = data.local_state->Cast<ArrowStreamLocalState>();
	while (true) {
		ArrowStreamScanChunk(context, data, output);
		// end of the scan, a pull handed back (BLOCKED, no rows), or nothing to re-apply
		if (!gstate.reapply_filter || output.size() == 0) {
			return;
		}
		// the provider's WHERE was a superset: keep exactly the rows DuckDB's own filters keep
		if (!lstate.filter_executor) {
			lstate.filter_expression = gstate.reapply_filter->Copy();
			lstate.filter_executor = make_uniq<ExpressionExecutor>(context, *lstate.filter_expression);
		}
		idx_t kept = lstate.filter_executor->SelectExpression(output, lstate.filter_sel);
		if (kept == output.size()) {
			return;
		}
		if (kept > 0) {
			output.Slice(lstate.filter_sel, kept);
			return;
		}
		output.Reset(); // no row of this chunk passed; 0 rows would read as the end of the scan
	}
}

static void ArrowStreamScanChunk(ClientContext &context, TableFunctionInput &data, DataChunk &output) {
	auto &gstate = data.global_state->Cast<ArrowStreamGlobalState>();
	auto &lstate = data.local_state->Cast<ArrowStreamLocalState>();

	// Whether a thread that finds the pull busy must PARK on the lock. True only under the SYNCHRONOUS
	// table-scan execution strategy, where returning BLOCKED is an InternalException — everywhere else it
	// hands the worker back instead.
	const bool park_on_lock = !fabricator::CanReturnBlocked(data);

	while (!lstate.chunk || !lstate.chunk->arrow_array.release ||
	       lstate.chunk_offset >= (idx_t)lstate.chunk->arrow_array.length) {
		// Read the progress counter BEFORE discovering the pull is busy: a puller that finishes in between
		// then makes this thread's wait predicate already true, so a wakeup cannot be lost.
		auto seen = gstate.wait_state->Generation();
		auto pulled = GetNextBatch(gstate, lstate, park_on_lock);
		if (pulled == BatchPull::END_OF_STREAM) {
			output.SetCardinality(0);
			return;
		}
		if (pulled == BatchPull::WOULD_BLOCK) {
			// Reachable only when park_on_lock is false, i.e. when a BLOCKED result IS permitted — so the
			// install below cannot fail. Emit nothing: DuckDB throws on a BLOCKED result carrying rows.
			output.SetCardinality(0);
			auto blocked = fabricator::BlockUntilProgress(data, gstate.wait_state, seen, lstate.backoff);
			D_ASSERT(blocked);
			(void)blocked;
			return;
		}
	}

	idx_t output_size =
	    MinValue<idx_t>(STANDARD_VECTOR_SIZE, (idx_t)lstate.chunk->arrow_array.length - lstate.chunk_offset);

	// Ingest every result column into all_columns (identity mapping).
	lstate.all_columns.Reset();
	lstate.all_columns.SetCardinality(output_size);
	ArrowTableFunction::ArrowToDuckDB(lstate, gstate.scan_arrow_table.GetColumns(), lstate.all_columns);

	// Project / synthesize the requested output columns (positions resolved in InitGlobal).
	output.SetCardinality(output_size);
	for (idx_t k = 0; k < gstate.output_column_ids.size(); k++) {
		if (gstate.output_source_pos[k] < 0) {
			BuildRowId(gstate.rowid_source_pos, lstate.all_columns, output.data[k], output_size);
		} else if (k < gstate.output_extract.size() && gstate.output_extract[k].Active()) {
			// a pushed-down struct field: walk from the column to the field. The Arrow import already marks a field
			// NULL wherever its struct is NULL, so the field vector is the extracted value as it stands.
			auto &extract = gstate.output_extract[k];
			auto &bind_data = data.bind_data->Cast<ArrowStreamBindData>();
			auto col_id = gstate.output_column_ids[k];
			reference<Vector> field(lstate.all_columns.data[(idx_t)gstate.output_source_pos[k]]);
			if (field.get().GetType() != bind_data.return_types[col_id]) {
				ThrowSchemaChanged(bind_data, col_id, field.get().GetType(), bind_data.return_types[col_id]);
			}
			for (auto idx : extract.indexes) {
				if (field.get().GetVectorType() != VectorType::FLAT_VECTOR) {
					field.get().Flatten(output_size);
				}
				field = StructVector::GetEntries(field.get())[idx];
			}
			if (extract.HasCast()) {
				// the CAST DuckDB folded into the extract: the plain cast it would have run above the scan
				VectorOperations::Cast(context, field.get(), output.data[k], output_size);
			} else {
				output.data[k].Reference(field.get());
			}
		} else {
			auto &source = lstate.all_columns.data[(idx_t)gstate.output_source_pos[k]];
			if (source.GetType() != output.data[k].GetType()) {
				ThrowSchemaChanged(data.bind_data->Cast<ArrowStreamBindData>(), gstate.output_column_ids[k],
				                   source.GetType(), output.data[k].GetType());
			}
			output.data[k].Reference(source);
		}
	}

	lstate.chunk_offset += output_size;
	output.Verify();
}

OperatorPartitionData ArrowStreamGetPartitionData(ClientContext &context, TableFunctionGetPartitionInput &input) {
	// Batch index ONLY. Partition COLUMNS would mean handing back the values of a partitioning key, which this
	// scan has no notion of; DuckDB's own arrow scan refuses the same way.
	if (input.partition_info.RequiresPartitionColumns()) {
		throw InternalException("fabricator: GetPartitionData does not support partition columns");
	}
	auto &state = input.local_state->Cast<ArrowStreamLocalState>();
	return OperatorPartitionData(state.batch_index);
}

// -----------------------------------------------------------------------------
// ArrowStreamReader
// -----------------------------------------------------------------------------
ArrowStreamReader::ArrowStreamReader(ClientContext &context, ArrowArrayStream stream)
    : context_(context), stream_(stream) {
	if (stream_.get_schema(&stream_, &schema_root_.arrow_schema) != 0) {
		const char *msg = stream_.get_last_error ? stream_.get_last_error(&stream_) : nullptr;
		throw IOException(string("Fabricator: failed to read RETURNING schema") + (msg ? string(": ") + msg : string()));
	}
	ArrowTableFunction::PopulateArrowTableSchema(context_, arrow_table_, schema_root_.arrow_schema);
	auto &arrow_schema = schema_root_.arrow_schema;
	for (int64_t i = 0; i < arrow_schema.n_children; i++) {
		types_.push_back(arrow_table_.GetColumns().at((idx_t)i)->GetDuckType());
		// The Arrow field name, captured beside the type. It always arrived here; nothing read it until
		// host_query grew named parameter binding, which is why that path bound positionally.
		auto *child = arrow_schema.children[i];
		names_.push_back(child && child->name ? string(child->name) : string());
	}
	lstate_ = make_uniq<ArrowScanLocalState>(make_uniq<ArrowArrayWrapper>(), context_);
	lstate_->column_ids.clear(); // identity mapping
}

ArrowStreamReader::~ArrowStreamReader() {
	if (stream_.release) {
		stream_.release(&stream_);
		stream_.release = nullptr;
	}
}

void ArrowStreamReader::Read(DataChunk &output) {
	output.Reset();
	if (done_) {
		output.SetCardinality(0);
		return;
	}
	while (!lstate_->chunk || !lstate_->chunk->arrow_array.release ||
	       lstate_->chunk_offset >= (idx_t)lstate_->chunk->arrow_array.length) {
		auto chunk = make_uniq<ArrowArrayWrapper>();
		int ret = stream_.get_next(&stream_, &chunk->arrow_array);
		if (ret != 0) {
			const char *msg = stream_.get_last_error ? stream_.get_last_error(&stream_) : nullptr;
			throw IOException(string("Fabricator: failed to read RETURNING batch") +
			                  (msg ? string(": ") + msg : string()));
		}
		if (!chunk->arrow_array.release) {
			done_ = true;
			output.SetCardinality(0);
			return;
		}
		lstate_->chunk = shared_ptr<ArrowArrayWrapper>(chunk.release());
		lstate_->chunk_offset = 0;
		lstate_->Reset();
	}

	idx_t output_size =
	    MinValue<idx_t>(STANDARD_VECTOR_SIZE, (idx_t)lstate_->chunk->arrow_array.length - lstate_->chunk_offset);
	output.SetCardinality(output_size);
	ArrowTableFunction::ArrowToDuckDB(*lstate_, arrow_table_.GetColumns(), output);
	lstate_->chunk_offset += output_size;
	output.Verify();
}

ArrowStreamReader::PullResult ArrowStreamReader::Pull() {
	auto chunk = make_uniq<ArrowArrayWrapper>();
	int ret = stream_.get_next(&stream_, &chunk->arrow_array);
	if (ret != 0) {
		const char *msg = stream_.get_last_error ? stream_.get_last_error(&stream_) : nullptr;
		throw IOException(string("Fabricator: failed to read exchange batch") + (msg ? string(": ") + msg : string()));
	}
	if (!chunk->arrow_array.release) {
		return PullResult::END; // released/null array == FINISHED
	}
	if (chunk->arrow_array.length == 0) {
		chunk->arrow_array.release(&chunk->arrow_array); // length-0 == per-input sentinel (NEED_MORE_INPUT)
		return PullResult::SENTINEL;
	}
	lstate_->chunk = shared_ptr<ArrowArrayWrapper>(chunk.release());
	lstate_->chunk_offset = 0;
	lstate_->Reset();
	return PullResult::DATA;
}

bool ArrowStreamReader::HasPending() const {
	return lstate_->chunk && lstate_->chunk->arrow_array.release &&
	       lstate_->chunk_offset < (idx_t)lstate_->chunk->arrow_array.length;
}

void ArrowStreamReader::Drain(DataChunk &output) {
	output.Reset();
	idx_t output_size =
	    MinValue<idx_t>(STANDARD_VECTOR_SIZE, (idx_t)lstate_->chunk->arrow_array.length - lstate_->chunk_offset);
	output.SetCardinality(output_size);
	ArrowTableFunction::ArrowToDuckDB(*lstate_, arrow_table_.GetColumns(), output);
	lstate_->chunk_offset += output_size;
	output.Verify();
}

} // namespace fabricator
