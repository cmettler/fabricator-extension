//===----------------------------------------------------------------------===//
//                         Fabricator — Arrow stream ingestion (impl)
//===----------------------------------------------------------------------===//

#include "fabricator/arrow_ingest.hpp"

#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "duckdb/common/allocator.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/types/data_chunk.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/planner/table_filter.hpp"
#include "duckdb/planner/filter/optional_filter.hpp"
#include "duckdb/planner/filter/dynamic_filter.hpp"
#include "duckdb/planner/filter/conjunction_filter.hpp"
#include "duckdb/planner/filter/constant_filter.hpp"
#include "duckdb/planner/filter/in_filter.hpp"
#include "duckdb/planner/filter/struct_filter.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

#include <algorithm>
#include <mutex>
#include <unordered_map>

namespace fabricator {

using namespace duckdb;

namespace {

struct ArrowStreamGlobalState : public GlobalTableFunctionState {
	//! The live data stream for this scan (owned; released in the destructor).
	ArrowArrayStream stream {};
	bool stream_initialized = false;
	bool done = false;
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

	~ArrowStreamGlobalState() override {
		if (stream_initialized && stream.release) {
			stream.release(&stream);
			stream.release = nullptr;
		}
	}

	idx_t MaxThreads() const override {
		// A single Arrow C stream is consumed serially.
		return 1;
	}
};

struct ArrowStreamLocalState : public ArrowScanLocalState {
	ArrowStreamLocalState(unique_ptr<ArrowArrayWrapper> current_chunk, ClientContext &context)
	    : ArrowScanLocalState(std::move(current_chunk), context) {
	}
};

// Pull the next Arrow array batch into the local state. Returns false at EOS.
bool GetNextBatch(ArrowStreamGlobalState &gstate, ArrowStreamLocalState &lstate) {
	lock_guard<mutex> lock(gstate.main_mutex);
	if (gstate.done) {
		return false;
	}

	auto chunk = make_uniq<ArrowArrayWrapper>();
	int ret = gstate.stream.get_next(&gstate.stream, &chunk->arrow_array);
	if (ret != 0) {
		const char *msg = gstate.stream.get_last_error ? gstate.stream.get_last_error(&gstate.stream) : nullptr;
		throw IOException(string("Fabricator: failed to read next batch from stream") +
		                  (msg ? string(": ") + msg : string()));
	}
	if (!chunk->arrow_array.release) {
		// End of stream.
		gstate.done = true;
		return false;
	}

	lstate.chunk = shared_ptr<ArrowArrayWrapper>(chunk.release());
	lstate.chunk_offset = 0;
	lstate.Reset();
	lstate.batch_index = gstate.batch_index++;
	return true;
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
	copy->at_unit = at_unit;
	copy->at_value = at_value;
	copy->rowid_source_columns = rowid_source_columns;
	copy->rowid_type = rowid_type;
	copy->virtual_rowid_columns = virtual_rowid_columns;
	copy->provider_virtual_columns = provider_virtual_columns;
	copy->row_count = row_count;
	copy->table = table;
	return std::move(copy);
}

void PopulateReturnSchema(ClientContext &context, ArrowStreamBindData &bind_data,
                          vector<LogicalType> &return_types, vector<string> &names) {
	// Produce a throwaway stream solely to read the schema, then release it. A bare
	// request (no projection/filter) => the provider reports the full column set.
	// Set the active host-FS opener (this context) so a global host-FS table function (a lakehouse reader)
	// can resolve DuckDB secrets while reading its schema; SQL/compute factories ignore it.
	SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
	ArrowArrayStream schema_stream {};
	if (bind_data.schema_factory) {
		// The bound object can describe itself — ask it instead of executing it. See `schema_factory`.
		bind_data.schema_factory(schema_stream);
	} else {
		bind_data.factory(ArrowScanRequest {}, schema_stream);
	}

	int ret = schema_stream.get_schema(&schema_stream, &bind_data.schema_root.arrow_schema);
	if (ret != 0) {
		const char *msg =
		    schema_stream.get_last_error ? schema_stream.get_last_error(&schema_stream) : nullptr;
		if (schema_stream.release) {
			schema_stream.release(&schema_stream);
		}
		throw IOException(string("Fabricator: failed to read schema from stream") +
		                  (msg ? string(": ") + msg : string()));
	}
	if (schema_stream.release) {
		schema_stream.release(&schema_stream);
	}

	// Build DuckDB's per-column Arrow converters, then derive names + DuckDB types + nullability.
	ReadSchemaColumns(context, bind_data.schema_root.arrow_schema, bind_data.arrow_table, return_types, names,
	                  &bind_data.column_nullable);
	if (return_types.empty()) {
		throw IOException("Fabricator: result schema has no columns");
	}
	bind_data.return_types = return_types;
	bind_data.names = names; // remember for projection/filter column-name mapping
}

BindInfo ArrowStreamGetBindInfo(const optional_ptr<FunctionData> bind_data) {
	auto &data = bind_data->Cast<ArrowStreamBindData>();
	if (data.table) {
		return BindInfo(const_cast<TableCatalogEntry &>(*data.table));
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

// Render one live TableFilter to a DuckDB SQL predicate on `qcol` (an already-quoted identifier). Returns
// true + appends to `out` iff rendered. The native target IS DuckDB (read_parquet), so TableFilter::ToString
// yields EXACT SQL for the mandatory kinds (constant/IN/null/conjunction/expression). Only OPTIONAL / DYNAMIC
// / BLOOM are handled specially — and all three are "not required for correctness" (DuckDB never relies on
// the scan to apply them: the join/topn re-applies), so returning false for them is always safe.
static bool RenderTableFilter(const TableFilter &f, const string &qcol, string &out) {
	switch (f.filter_type) {
	case TableFilterType::OPTIONAL_FILTER: {
		auto &opt = f.Cast<OptionalFilter>();
		if (!opt.child_filter) {
			return false;
		}
		return RenderTableFilter(*opt.child_filter, qcol, out); // optional: fine to skip if child unrenderable
	}
	case TableFilterType::DYNAMIC_FILTER: {
		// A runtime join/topn bound: DynamicFilter::ToString is a debug string, so reach the resolved inner
		// ConstantFilter under the shared lock. Not yet materialized => skip (best-effort pruning).
		auto &dyn = f.Cast<DynamicFilter>();
		if (!dyn.filter_data) {
			return false;
		}
		lock_guard<mutex> lock(dyn.filter_data->lock);
		if (!dyn.filter_data->initialized || !dyn.filter_data->filter) {
			return false;
		}
		out += dyn.filter_data->filter->ToString(qcol);
		return true;
	}
	case TableFilterType::BLOOM_FILTER:
		return false; // probabilistic; not exact SQL — always OptionalFilter-wrapped, so skip-safe
	case TableFilterType::STRUCT_EXTRACT: {
		// A struct-member filter (`WHERE (s).a = 5`, erased into the set under exact mode): rebase the
		// child filter onto an explicit struct_extract — exact DuckDB SQL and robust to member names the
		// bare ToString dot form (`"s".a`) would mis-quote. NOTE: on a COLUMN-MAPPED table this SQL still
		// references the LOGICAL member name, which the native (read_parquet) path cannot bind (nested
		// children carry physical names inside the files) — that pre-existing combination fails loudly at
		// the per-file bind; the fix belongs in the C# native reader (rebuild mapped structs logically).
		// The codec exact path is fine: its per-batch filter runs over logically-renamed batches.
		auto &sf = f.Cast<StructFilter>();
		string member;
		if (!sf.child_name.empty()) {
			member = "struct_extract(" + qcol + ", '";
			for (char c : sf.child_name) {
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
	case TableFilterType::CONJUNCTION_AND:
	case TableFilterType::CONJUNCTION_OR: {
		// Recurse per child (do NOT use ToString: a child may be an OptionalFilter/DynamicFilter whose
		// ToString is not plain SQL — "optional: …" — and would leak into the predicate).
		bool is_and = f.filter_type == TableFilterType::CONJUNCTION_AND;
		// ConjunctionFilter (the base holding child_filters) has no ::TYPE, so Cast<> the concrete subtype.
		const ConjunctionFilter &conj =
		    is_and ? static_cast<const ConjunctionFilter &>(f.Cast<ConjunctionAndFilter>())
		           : static_cast<const ConjunctionFilter &>(f.Cast<ConjunctionOrFilter>());
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
	default:
		// Constant / IN / IS [NOT] NULL / expression: DuckDB's own render == exact SQL for the
		// read_parquet (DuckDB) target. These are the MANDATORY (erased-from-plan) filters we must apply.
		out += f.ToString(qcol);
		return true;
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

// Serialize one live TableFilter on the column identified by `path` (leaf type `type`) into a FilterNode
// JSON object, appending its constants to `constants`. Returns false = not convertible (skip-safe for the
// pruning consumer). String ordering comparisons are gated like the bind-time serializer.
static bool LiveFilterNode(const TableFilter &f, vector<string> &path, const LogicalType &type,
                           vector<Value> &constants, bool string_order_pushable, string &out) {
	switch (f.filter_type) {
	case TableFilterType::OPTIONAL_FILTER: {
		auto &opt = f.Cast<OptionalFilter>();
		return opt.child_filter && LiveFilterNode(*opt.child_filter, path, type, constants, string_order_pushable, out);
	}
	case TableFilterType::DYNAMIC_FILTER: {
		auto &dyn = f.Cast<DynamicFilter>();
		if (!dyn.filter_data) {
			return false;
		}
		lock_guard<mutex> lock(dyn.filter_data->lock);
		if (!dyn.filter_data->initialized || !dyn.filter_data->filter) {
			return false; // not yet materialized: nothing to prune with
		}
		return LiveFilterNode(*dyn.filter_data->filter, path, type, constants, string_order_pushable, out);
	}
	case TableFilterType::CONSTANT_COMPARISON: {
		auto &cf = f.Cast<ConstantFilter>();
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
	case TableFilterType::IS_NULL:
	case TableFilterType::IS_NOT_NULL:
		out += "{\"op\":\"";
		out += f.filter_type == TableFilterType::IS_NULL ? "is_null" : "is_not_null";
		out += "\",";
		LiveJsonColumnRef(path, out);
		out += "}";
		return true;
	case TableFilterType::IN_FILTER: {
		auto &in = f.Cast<InFilter>();
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
	case TableFilterType::STRUCT_EXTRACT: {
		auto &sf = f.Cast<StructFilter>();
		if (type.id() != LogicalTypeId::STRUCT) {
			return false;
		}
		auto &members = StructType::GetChildTypes(type);
		if (sf.child_idx >= members.size()) {
			return false;
		}
		// Member name from the struct type at the bound index (exact case — matches parquet/stats keys).
		path.push_back(members[sf.child_idx].first);
		bool ok = sf.child_filter &&
		          LiveFilterNode(*sf.child_filter, path, members[sf.child_idx].second, constants,
		                         string_order_pushable, out);
		path.pop_back();
		return ok;
	}
	case TableFilterType::CONJUNCTION_AND:
	case TableFilterType::CONJUNCTION_OR: {
		bool is_and = f.filter_type == TableFilterType::CONJUNCTION_AND;
		const ConjunctionFilter &conj =
		    is_and ? static_cast<const ConjunctionFilter &>(f.Cast<ConjunctionAndFilter>())
		           : static_cast<const ConjunctionFilter &>(f.Cast<ConjunctionOrFilter>());
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
	default:
		return false; // EXPRESSION_FILTER / BLOOM_FILTER: not convertible (bloom is pruning-only anyway)
	}
}

// Serialize the scan's live TableFilterSet into one FilterNode JSON tree (implicit AND across columns),
// appending its constants to the per-execution `constants` vector. Empty unless the scan advertised
// filter_pushdown (the exact-mode catalog) and DuckDB delivered filters.
static string SerializeLiveFilters(const ArrowStreamBindData &bind_data, TableFunctionInitInput &input,
                                   vector<Value> &constants) {
	if (!input.filters) {
		return string();
	}
	vector<string> parts;
	for (auto &entry : input.filters->filters) {
		idx_t key = entry.first;
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
		const LogicalType &type = col_type;
		string node;
		if (LiveFilterNode(*entry.second, path, type, constants, bind_data.string_order_pushable, node) &&
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
static string RenderLiveFilters(const ArrowStreamBindData &bind_data, TableFunctionInitInput &input) {
	if (!input.filters) {
		return string();
	}
	vector<string> conds;
	for (auto &entry : input.filters->filters) {
		idx_t key = entry.first;
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
		string rendered;
		if (RenderTableFilter(*entry.second, qcol, rendered) && !rendered.empty()) {
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

static string BuildScanSpec(const ArrowStreamBindData &bind_data, const vector<column_t> &output_column_ids,
                            const string &live_filter_sql, const string &live_filter_json) {
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
	// superset, so limiting before exact (DuckDB) filtering could drop valid rows.
	if (bind_data.top_n >= 0 && bind_data.filter_json.empty() && live_filter_json.empty()) {
		json += ",\"top\":" + to_string(bind_data.top_n);
	}
	if (!bind_data.order_by_json.empty() && bind_data.filter_json.empty() && live_filter_json.empty()) {
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
// CONSUMER's use of the stream, which outlasts the scan_table/table_execute call: the managed side may hold
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

	// Catalog scans push the projected column list (and superset-safe filters) to the
	// provider; raw queries fetch the full result (the SQL is user-supplied).
	ArrowScanRequest request;
	if (bind_data.push_projection) {
		// Render the live runtime filters (static-erased + dynamic/join) for an exact-filter scan
		// (filter_pushdown=true); empty for SQL/DAX/non-exact (input.filters null). This runs per execution,
		// so a hash-join dynamic filter materialized before the probe scan is captured here — twice: as the
		// exact SQL the provider APPLIES (native_filter) and as the structured FilterNode tree it PRUNES
		// with (file/row-group skipping; the constants extend a PER-EXECUTION copy of the bind constants —
		// bind data is shared across executions and must not be mutated).
		string live_filter_sql = RenderLiveFilters(bind_data, input);
		vector<Value> filter_constants = bind_data.filter_constants;
		string live_filter_json = SerializeLiveFilters(bind_data, input, filter_constants);
		request.spec_json = BuildScanSpec(bind_data, input.column_ids, live_filter_sql, live_filter_json);
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
	SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
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
		entries[i]->Reference(all_columns.data[sources[i]]);
	}
}

void ArrowStreamScan(ClientContext &context, TableFunctionInput &data, DataChunk &output) {
	auto &gstate = data.global_state->Cast<ArrowStreamGlobalState>();
	auto &lstate = data.local_state->Cast<ArrowStreamLocalState>();

	while (!lstate.chunk || !lstate.chunk->arrow_array.release ||
	       lstate.chunk_offset >= (idx_t)lstate.chunk->arrow_array.length) {
		if (!GetNextBatch(gstate, lstate)) {
			output.SetCardinality(0);
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
		} else {
			output.data[k].Reference(lstate.all_columns.data[(idx_t)gstate.output_source_pos[k]]);
		}
	}

	lstate.chunk_offset += output_size;
	output.Verify();
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
