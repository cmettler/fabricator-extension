//===----------------------------------------------------------------------===//
//                         ArrowNet — Arrow stream ingestion (impl)
//===----------------------------------------------------------------------===//

#include "arrownet/arrow_ingest.hpp"

#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "duckdb/common/allocator.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/types/data_chunk.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

#include <algorithm>
#include <mutex>
#include <unordered_map>

namespace arrownet {

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
		throw IOException(string("ArrowNet: failed to read next batch from stream") +
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

void PopulateReturnSchema(ClientContext &context, ArrowStreamBindData &bind_data,
                          vector<LogicalType> &return_types, vector<string> &names) {
	// Produce a throwaway stream solely to read the schema, then release it. A bare
	// request (no projection/filter) => the provider reports the full column set.
	// Set the active host-FS opener (this context) so a global host-FS table function (a lakehouse reader)
	// can resolve DuckDB secrets while reading its schema; SQL/compute factories ignore it.
	SetActiveOpener(reinterpret_cast<ArrowNetHandle>(&context));
	ArrowArrayStream schema_stream {};
	bind_data.factory(ArrowScanRequest {}, schema_stream);

	int ret = schema_stream.get_schema(&schema_stream, &bind_data.schema_root.arrow_schema);
	if (ret != 0) {
		const char *msg =
		    schema_stream.get_last_error ? schema_stream.get_last_error(&schema_stream) : nullptr;
		if (schema_stream.release) {
			schema_stream.release(&schema_stream);
		}
		throw IOException(string("ArrowNet: failed to read schema from stream") +
		                  (msg ? string(": ") + msg : string()));
	}
	if (schema_stream.release) {
		schema_stream.release(&schema_stream);
	}

	// Build DuckDB's per-column Arrow converters, then derive names + DuckDB types + nullability.
	ReadSchemaColumns(context, bind_data.schema_root.arrow_schema, bind_data.arrow_table, return_types, names,
	                  &bind_data.column_nullable);
	if (return_types.empty()) {
		throw IOException("ArrowNet: result schema has no columns");
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
static string BuildScanSpec(const ArrowStreamBindData &bind_data, const vector<column_t> &output_column_ids) {
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
	for (auto col_id : output_column_ids) {
		if (col_id == COLUMN_IDENTIFIER_ROW_ID) {
			need_rowid = true;
		} else {
			add((idx_t)col_id);
		}
	}
	if (need_rowid) {
		for (auto src : bind_data.rowid_source_columns) {
			add(src);
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
	if (!bind_data.filter_json.empty()) {
		json += ",\"filter\":";
		json += bind_data.filter_json; // already a JSON object
	}
	// TOP (n) is only safe with no pushed filter: a best-effort filter returns a
	// superset, so limiting before exact (DuckDB) filtering could drop valid rows.
	if (bind_data.top_n >= 0 && bind_data.filter_json.empty()) {
		json += ",\"top\":" + to_string(bind_data.top_n);
	}
	if (!bind_data.order_by_json.empty() && bind_data.filter_json.empty()) {
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
	json += "}";
	return json;
}

// Materializes the filter constants as a one-row Arrow batch (column i == value i),
// so the provider can build a parameterized WHERE with exact types. The returned
// producer must outlive the scan_table call that consumes the stream.
static unique_ptr<arrownet::ArrowProducer> BuildFilterValues(ClientContext &context, const vector<Value> &consts) {
	vector<LogicalType> types;
	vector<string> names;
	for (idx_t i = 0; i < consts.size(); i++) {
		types.push_back(consts[i].type());
		names.push_back("v" + to_string(i));
	}
	auto props = arrownet::BoundaryClientProperties(context);
	auto producer = make_uniq<arrownet::ArrowProducer>(types, names, props);

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
	for (auto col_id : gstate.output_column_ids) {
		gstate.output_source_pos.push_back(col_id == COLUMN_IDENTIFIER_ROW_ID ? -1 : (int64_t)resolve((idx_t)col_id));
	}
	gstate.rowid_source_pos.clear();
	for (auto src : bind_data.rowid_source_columns) {
		gstate.rowid_source_pos.push_back(resolve(src));
	}
}

unique_ptr<GlobalTableFunctionState> ArrowStreamInitGlobal(ClientContext &context,
                                                           TableFunctionInitInput &input) {
	auto &bind_data = input.bind_data->Cast<ArrowStreamBindData>();
	auto gstate = make_uniq<ArrowStreamGlobalState>();
	gstate->output_column_ids = input.column_ids;

	// Catalog scans push the projected column list (and superset-safe filters) to the
	// provider; raw queries fetch the full result (the SQL is user-supplied).
	ArrowScanRequest request;
	unique_ptr<arrownet::ArrowProducer> value_producer; // must outlive the factory() call
	if (bind_data.push_projection) {
		request.spec_json = BuildScanSpec(bind_data, gstate->output_column_ids);
		if (!bind_data.filter_constants.empty()) {
			value_producer = BuildFilterValues(context, bind_data.filter_constants);
			request.filter_values = value_producer->Stream();
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
	SetActiveOpener(reinterpret_cast<ArrowNetHandle>(&context));
	bind_data.factory(request, gstate->stream);
	gstate->stream_initialized = true;

	// Read the ACTUAL result schema (a subset when projection was pushed) and build
	// per-scan converters + DuckDB types from it.
	if (gstate->stream.get_schema(&gstate->stream, &gstate->scan_schema_root.arrow_schema) != 0) {
		const char *msg = gstate->stream.get_last_error ? gstate->stream.get_last_error(&gstate->stream) : nullptr;
		throw IOException(string("ArrowNet: failed to read scan schema") + (msg ? string(": ") + msg : string()));
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
		throw IOException(string("ArrowNet: failed to read RETURNING schema") + (msg ? string(": ") + msg : string()));
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
			throw IOException(string("ArrowNet: failed to read RETURNING batch") +
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
		throw IOException(string("ArrowNet: failed to read exchange batch") + (msg ? string(": ") + msg : string()));
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

} // namespace arrownet
