// The FILE SCAN: a catalog table read by DuckDB's own parquet reader over the data files its provider lists
// (ABI v101 table_file_plan). See fabricator_file_scan.hpp and docs/delta-read-path.md.
//
// Why: a provider scan hands every row to the host through ONE streaming result, and that serial handoff — not the
// Delta layer, not the Arrow conversion — is what costs. MEASURED 2026-11-09 on TPC-H SF10 (RustFS), Q1 warm:
// read_parquet in the plan 0.36 s, the same SQL through a host-query stream 2.0-3.4 s, nine per-file streams as
// UNION ALL 1.5-1.9 s. With the files in the plan, DuckDB parallelises the read itself, prunes row groups with its
// own filters and applies hash-join filters natively.
//
// The shape is duckdb-iceberg's (iceberg_scan = a copy of parquet_scan with its own MultiFileReader, bound from the
// catalog entry's GetScanFunction), and the removed ABI v75 spike's (docs/multifile-delta.md). The reader here knows
// nothing about Delta: the provider decides which files and which rows, so an Iceberg plan from engineered-wood can
// feed the same reader.

#include "fabricator/fabricator_file_scan.hpp"

#include "fabricator/clr_host.hpp"

#include "duckdb/catalog/catalog.hpp"
#include "duckdb/catalog/catalog_entry/schema_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/catalog/catalog_entry/table_function_catalog_entry.hpp"
#include "duckdb/common/multi_file/base_file_reader.hpp"
#include "duckdb/common/multi_file/multi_file_data.hpp"
#include "duckdb/common/multi_file/multi_file_list.hpp"
#include "duckdb/common/multi_file/multi_file_reader.hpp"
#include "duckdb/common/multi_file/multi_file_states.hpp"
#include "duckdb/common/arrow/arrow_wrapper.hpp"
#include "duckdb/common/vector/flat_vector.hpp"
#include "duckdb/common/vector/list_vector.hpp"
#include "duckdb/execution/expression_executor.hpp"
#include "duckdb/function/table/arrow.hpp"
#include "duckdb/planner/expression/bound_conjunction_expression.hpp"
#include "duckdb/common/open_file_info.hpp"
#include "duckdb/common/printer.hpp"
#include "duckdb/common/types/selection_vector.hpp"
#include "duckdb/function/function_binder.hpp"
#include "duckdb/main/database.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/tableref/table_function_ref.hpp"
#include "duckdb/planner/expression/bound_constant_expression.hpp"
#include "duckdb/planner/expression/bound_operator_expression.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"

#include <yyjson.h>

#include <algorithm>
#include <cstdlib>

namespace fabricator {

using namespace duckdb;

namespace {

//! The key on each file's extended info: its index in the plan (deleted rows, constants, virtual values).
constexpr const char *FILE_KEY = "fabricator_file";

//! How a virtual column is computed (the plan's "virtual"): the file's own column where it stores one and the row's
//! value there is not NULL, else the file's value — plus file_row_number when `add_row_number` — else NULL.
struct VirtualColumnSpec {
	string stored;
	bool add_row_number = false;
};

//! The plan, owned by the scan function (function_info) for the scan's life. Nothing in it is format-specific: the
//! provider states files, deleted rows, constants, identifiers and virtual values; this reader only applies them.
struct FileScanPlan : public TableFunctionInfo {
	vector<OpenFileInfo> files;
	//! per file (by FILE_KEY), the deleted row positions, sorted; empty = none
	vector<vector<int64_t>> deleted;
	//! per file, column constants as the plan states them (text; NULL = the column is NULL for the whole file)
	vector<case_insensitive_map_t<Value>> constant_text;
	//! per file, the constants cast to their columns' types, by column index (filled at bind)
	vector<unordered_map<idx_t, Value>> constants;
	//! per file, each virtual column's value (BIGINT; NULL = none)
	vector<case_insensitive_map_t<Value>> virtual_values;
	//! the virtual columns the plan computes, by name
	case_insensitive_map_t<VirtualColumnSpec> virtual_specs;
	//! the table's columns, in order — the scan's schema; files are mapped onto it BY NAME (identifier = stored name)
	//! or, when the plan gives field ids, BY FIELD ID (identifier = the id)
	vector<MultiFileColumnDefinition> columns;
	bool by_field_id = false;
	optional_ptr<TableCatalogEntry> table;
	//! the table's virtual columns plus the reader's own (filename, file_row_number)
	virtual_column_map_t virtual_columns;
	//! the live row count the provider reported (-1 = unknown)
	int64_t row_count = -1;
	//! the AT clause the scan was bound with ("" = latest)
	string at_unit;
	string at_value;
	//! per file, the provider's token when the PROVIDER reads the part (a stream part); empty = DuckDB reads the file
	vector<string> stream_parts;
	//! the table session the stream parts are asked from
	FabricatorHandle table_handle = 0;
};

//! Excludes a file's deleted rows from DuckDB's parquet read (called per row range; returns the KEPT rows).
class DeletedRowsFilter : public DeleteFilter {
public:
	explicit DeletedRowsFilter(const vector<int64_t> &deleted_p) : deleted(deleted_p) {
	}
	idx_t Filter(row_t start_row_index, idx_t count, SelectionVector &result_sel) override {
		if (count == 0) {
			return 0;
		}
		// the parquet reader hands an uninitialised selection (Initialize(nullptr)); allocate before writing
		result_sel.Initialize(STANDARD_VECTOR_SIZE);
		auto it = std::lower_bound(deleted.begin(), deleted.end(), static_cast<int64_t>(start_row_index));
		idx_t kept = 0;
		for (idx_t i = 0; i < count; i++) {
			auto row = static_cast<int64_t>(start_row_index) + static_cast<int64_t>(i);
			while (it != deleted.end() && *it < row) {
				++it;
			}
			if (it != deleted.end() && *it == row) {
				continue;
			}
			result_sel.set_index(kept++, i);
		}
		return kept;
	}

private:
	const vector<int64_t> &deleted; // owned by the plan, which outlives every reader of the scan
};

idx_t FileIndex(const OpenFileInfo &file, const FileScanPlan &plan) {
	if (file.extended_info) {
		auto entry = file.extended_info->options.find(FILE_KEY);
		if (entry != file.extended_info->options.end()) {
			auto index = entry->second.GetValue<uint64_t>();
			if (index < plan.files.size()) {
				return index;
			}
		}
	}
	throw InternalException("fabricator file scan: a file without its plan entry");
}

unique_ptr<Expression> AddRowNumber(ClientContext &context, Value value, const LogicalType &type, idx_t ref) {
	vector<unique_ptr<Expression>> children;
	children.push_back(make_uniq<BoundConstantExpression>(std::move(value)));
	children.push_back(make_uniq<BoundReferenceExpression>(type, ref));
	FunctionBinder binder(context);
	ErrorData error;
	auto sum = binder.BindScalarFunction(Identifier::DefaultSchema(), "+", std::move(children), error, true, nullptr);
	if (error.HasError()) {
		error.Throw();
	}
	return sum;
}

//! Reads an Arrow stream as DataChunks of at most STANDARD_VECTOR_SIZE rows, in the stream's own types. Owns the
//! stream from construction. Shared by the plan's parts and by a stream part's rows.
class ArrowChunkReader {
public:
	ArrowChunkReader(ClientContext &context, ArrowArrayStream &source) : state(nullptr, context) {
		stream.arrow_array_stream = source;
		source.release = nullptr; // ours now
		ArrowSchemaWrapper schema;
		stream.GetSchema(schema);
		ArrowTableFunction::PopulateArrowTableSchema(context, table, schema.arrow_schema);
		for (int64_t i = 0; i < schema.arrow_schema.n_children; i++) {
			names.push_back(schema.arrow_schema.children[i]->name ? schema.arrow_schema.children[i]->name : "");
		}
	}

	const vector<string> &Names() const {
		return names;
	}
	vector<LogicalType> &Types() {
		return table.GetTypes();
	}

	//! The next rows into `chunk`, whose columns are the stream's (or none, when only the row count matters);
	//! false at the end of the stream.
	bool Next(DataChunk &chunk) {
		while (!state.chunk || state.chunk_offset >= idx_t(state.chunk->arrow_array.length)) {
			auto next = stream.GetNextChunk();
			if (!next || !next->arrow_array.release) {
				return false;
			}
			state.chunk = std::move(next);
			state.Reset();
		}
		auto count =
		    MinValue<idx_t>(STANDARD_VECTOR_SIZE, idx_t(state.chunk->arrow_array.length) - state.chunk_offset);
		chunk.Reset();
		chunk.SetCardinality(count);
		ArrowTableFunction::ArrowToDuckDB(state, table.GetColumns(), chunk);
		state.chunk_offset += count;
		return true;
	}

private:
	ArrowArrayStreamWrapper stream;
	ArrowTableSchema table;
	ArrowScanLocalState state;
	vector<string> names;
};

//! A part whose rows the PROVIDER produces (the plan's "stream"): an Arrow stream asked for with the columns the scan
//! needs, read by one thread. Everything that maps rows onto the table — deleted rows aside, which the provider
//! applies itself — stays with the multi-file reader: constants, stored names, NULL defaults, virtual columns.
class StreamPartReader : public BaseFileReader {
public:
	StreamPartReader(const OpenFileInfo &file, shared_ptr<FileScanPlan> plan_p, idx_t index_p)
	    : BaseFileReader(file), plan(std::move(plan_p)), index(index_p) {
		// the part's own columns: the table's, under their stored names (what the mapper matches), minus the ones the
		// plan gives this part as constants (the provider does not produce them)
		auto &constants = plan->constants[index];
		for (idx_t i = 0; i < plan->columns.size(); i++) {
			if (constants.find(i) != constants.end()) {
				continue;
			}
			columns.push_back(LocalColumn(plan->columns[i]));
			names.push_back(plan->columns[i].name.GetIdentifierName());
		}
	}

	string GetReaderType() const override {
		return "fabricator_stream";
	}

	//! The mapper has just appended a virtual column to `columns`; the only one a stream part produces is the row's
	//! position in the part (file_row_number), which the provider sends when asked.
	void AddVirtualColumn(column_t virtual_column_id) override {
		if (virtual_column_id != MultiFileReader::COLUMN_IDENTIFIER_FILE_ROW_NUMBER) {
			throw NotImplementedException("fabricator file scan: a stream part cannot produce virtual column %d",
			                              virtual_column_id);
		}
		row_number_column = columns.size() - 1;
		names.push_back("file_row_number");
	}

	bool TryInitializeScan(ClientContext &, GlobalTableFunctionState &, LocalTableFunctionState &) override {
		// one thread drains a stream: the first to ask takes it
		return !claimed.exchange(true);
	}

	void PrepareScan(ClientContext &context, GlobalTableFunctionState &, LocalTableFunctionState &) override {
		// a filter on a COMPUTED column (the row id: base + position) comes with the expression to evaluate first; its
		// inputs are the consecutive output columns from its own position on, and its result replaces that column
		for (auto &entry : expression_map) {
			Converted converted;
			converted.position = MultiFileLocalIndex(entry.first).GetIndex();
			converted.inputs = entry.second.column_indexes.size();
			converted.expression = entry.second.expression->Copy();
			converted.executor = make_uniq<ExpressionExecutor>(context, *converted.expression);
			converted_columns.push_back(std::move(converted));
		}
		// the columns asked for, in the order of the output chunk (column_ids are positions in `columns`); null asks for
		// the row's position in the part (file_row_number, what the row id is built from)
		string request = "{\"part\":" + Quote(plan->stream_parts[index]) + ",\"columns\":[";
		for (idx_t i = 0; i < column_ids.size(); i++) {
			auto local = column_ids[MultiFileLocalIndex(i)].GetId();
			if (IsRowNumber(local)) {
				request += i ? ",null" : "null";
				continue;
			}
			request += (i ? "," : "") + Quote(names[local]);
		}
		request += "]}";
		ArrowArrayStream raw;
		raw.release = nullptr;
		TablePartScan(plan->table_handle, request, raw);
		rows = make_uniq<ArrowChunkReader>(context, raw);
		auto &types = rows->Types();
		// no columns asked (a COUNT(*)): the provider sends a placeholder column only to carry the row count
		if (!column_ids.empty() && types.size() != column_ids.size()) {
			throw IOException("fabricator file scan: the provider's stream for '%s' has %d columns, the scan asked "
			                  "for %d", file.path, types.size(), column_ids.size());
		}
		for (idx_t i = 0; i < column_ids.size(); i++) {
			auto local = column_ids[MultiFileLocalIndex(i)].GetId();
			auto &expected = columns[local].type;
			if (types[i] != expected) {
				throw IOException("fabricator file scan: the provider's stream for '%s' gives column '%s' as %s, the "
				                  "table declares %s",
				                  file.path, names[local], types[i].ToString(), expected.ToString());
			}
		}
		if (filters && filters->HasFilters()) {
			vector<unique_ptr<Expression>> conjuncts;
			for (auto &entry : *filters) {
				MultiFileLocalIndex position(entry.GetIndex());
				BoundReferenceExpression column(columns[column_ids[position].GetId()].type, position.GetIndex());
				conjuncts.push_back(entry.Filter().ToExpression(column));
			}
			if (conjuncts.size() == 1) {
				filter = std::move(conjuncts[0]);
			} else {
				auto conjunction = make_uniq<BoundConjunctionExpression>(ExpressionType::CONJUNCTION_AND);
				for (auto &conjunct : conjuncts) {
					conjunction->GetChildrenMutable().push_back(std::move(conjunct));
				}
				filter = std::move(conjunction);
			}
			executor = make_uniq<ExpressionExecutor>(context, *filter);
		}
	}

	AsyncResult Scan(ClientContext &context, GlobalTableFunctionState &, LocalTableFunctionState &,
	                 DataChunk &chunk) override {
		while (true) {
			if (context.IsInterrupted()) {
				throw InterruptException();
			}
			if (!rows->Next(chunk)) {
				// the end of the part: release the stream here, on the thread that drained it (see FinishFile)
				rows.reset();
				chunk.SetCardinality(0);
				return SourceResultType::FINISHED;
			}
			for (auto &converted : converted_columns) {
				Convert(converted, chunk);
			}
			if (!executor) {
				return SourceResultType::HAVE_MORE_OUTPUT;
			}
			auto kept = executor->SelectExpression(chunk, selection);
			if (kept == 0) {
				continue; // no row passed; an empty chunk would read as the end of the part
			}
			if (kept < chunk.size()) {
				chunk.Slice(selection, kept);
			}
			return SourceResultType::HAVE_MORE_OUTPUT;
		}
	}

	//! ⚠ NOT where the stream is released: DuckDB calls FinishFile from the thread that found no more work in this file,
	//! which may be another thread while the owner is still draining it (a parquet reader's FinishFile only resets a
	//! counter). Releasing here was a null dereference mid-scan; the owner releases at the end of the part instead.
	void FinishFile(ClientContext &, GlobalTableFunctionState &) override {
	}

private:
	struct Converted {
		idx_t position;
		idx_t inputs;
		unique_ptr<Expression> expression;
		unique_ptr<ExpressionExecutor> executor;
	};

	static void Convert(Converted &converted, DataChunk &chunk) {
		vector<LogicalType> input_types;
		for (idx_t i = 0; i < converted.inputs; i++) {
			input_types.push_back(chunk.data[converted.position + i].GetType());
		}
		DataChunk input;
		input.InitializeEmpty(input_types);
		for (idx_t i = 0; i < converted.inputs; i++) {
			input.data[i].Reference(chunk.data[converted.position + i]);
		}
		input.SetCardinality(chunk.size());
		auto &type = converted.expression->GetReturnType();
		if (type != chunk.data[converted.position].GetType()) {
			throw NotImplementedException("fabricator file scan: a stream part cannot convert a filtered column from %s "
			                              "to %s yet",
			                              chunk.data[converted.position].GetType().ToString(), type.ToString());
		}
		Vector result(type, chunk.size());
		converted.executor->ExecuteExpression(input, result);
		chunk.data[converted.position].Reference(result);
	}

	bool IsRowNumber(idx_t local) const {
		return row_number_column.IsValid() && local == row_number_column.GetIndex();
	}

	//! The part's own column for a table column: what the mapper matches — the stored name, or the field id the part
	//! stores it under (then the part keeps the table's name).
	static MultiFileColumnDefinition LocalColumn(const MultiFileColumnDefinition &global) {
		bool by_id = !global.identifier.IsNull() && global.identifier.type().id() == LogicalTypeId::INTEGER;
		MultiFileColumnDefinition local(by_id ? global.name.GetIdentifierName() : global.GetIdentifierName(), global.type);
		if (by_id) {
			local.identifier = global.identifier;
		}
		if (global.type.id() == LogicalTypeId::MAP) {
			// a part's map, like a file's, has its key and value under a "key_value" node
			MultiFileColumnDefinition key_value(Identifier("key_value"), ListType::GetChildType(global.type));
			for (auto &child : global.children) {
				key_value.children.push_back(LocalColumn(child));
			}
			local.children.push_back(std::move(key_value));
			return local;
		}
		for (auto &child : global.children) {
			local.children.push_back(LocalColumn(child));
		}
		return local;
	}
	static string Quote(const string &text) {
		string out = "\"";
		for (auto c : text) {
			if (c == '"' || c == '\\') {
				out += '\\';
				out += c;
			} else if (static_cast<unsigned char>(c) < 0x20) {
				char buf[8];
				snprintf(buf, sizeof(buf), "\\u%04x", c);
				out += buf;
			} else {
				out += c;
			}
		}
		return out + "\"";
	}

	shared_ptr<FileScanPlan> plan;
	idx_t index;
	//! the table's names of `columns` (what the provider is asked for), parallel to `columns`
	vector<string> names;
	//! the position in `columns` of the row's position in the part, when the scan needs it (AddVirtualColumn)
	optional_idx row_number_column;
	std::atomic<bool> claimed {false};
	unique_ptr<ArrowChunkReader> rows;
	unique_ptr<Expression> filter;
	unique_ptr<ExpressionExecutor> executor;
	SelectionVector selection {STANDARD_VECTOR_SIZE};
	vector<Converted> converted_columns;
};

struct FabricatorFileScanReader : public MultiFileReader {
	explicit FabricatorFileScanReader(shared_ptr<TableFunctionInfo> info)
	    : plan(shared_ptr_cast<TableFunctionInfo, FileScanPlan>(std::move(info))) {
		if (!plan) {
			throw InternalException("fabricator file scan: no plan on the scan function");
		}
	}

	static unique_ptr<MultiFileReader> CreateInstance(const BoundTableFunction &function) {
		return make_uniq<FabricatorFileScanReader>(function.function_info);
	}

	shared_ptr<MultiFileList> CreateFileList(ClientContext &, const vector<string> &, const FileGlobInput &) override {
		return make_shared_ptr<SimpleMultiFileList>(plan->files);
	}

	//! The table's own schema, files mapped onto it by name; a column a file lacks (added later) reads NULL.
	bool Bind(MultiFileOptions &, MultiFileList &, vector<LogicalType> &return_types, vector<Identifier> &names,
	          MultiFileReaderBindData &bind_data) override {
		for (auto &column : plan->columns) {
			names.push_back(column.name);
			return_types.push_back(column.type);
			bind_data.schema.push_back(column);
		}
		bind_data.mapping = plan->by_field_id ? MultiFileColumnMappingMode::BY_FIELD_ID : MultiFileColumnMappingMode::BY_NAME;
		return true;
	}

	void BindOptions(MultiFileOptions &options, MultiFileList &files, vector<LogicalType> &return_types,
	                 vector<Identifier> &names, MultiFileReaderBindData &bind_data) override {
		// a data file's path says nothing about its rows: never read partition values or unions from it
		options.auto_detect_hive_partitioning = false;
		options.hive_partitioning = false;
		options.union_by_name = false;
		MultiFileReader::BindOptions(options, files, return_types, names, bind_data);
	}

	void FinalizeBind(MultiFileReaderData &reader_data, const MultiFileOptions &file_options,
	                  const MultiFileReaderBindData &options, const vector<MultiFileColumnDefinition> &global_columns,
	                  const vector<ColumnIndex> &global_column_ids, ClientContext &context,
	                  optional_ptr<MultiFileReaderGlobalState> global_state) override {
		MultiFileReader::FinalizeBind(reader_data, file_options, options, global_columns, global_column_ids, context,
		                              global_state);
		if (!reader_data.reader) {
			return;
		}
		auto index = FileIndex(reader_data.reader->file, *plan);
		RefuseZoneDependentCasts(*reader_data.reader, global_columns);
		auto &deleted = plan->deleted[index];
		if (!deleted.empty()) {
			reader_data.reader->deletion_filter = make_uniq<DeletedRowsFilter>(deleted);
		}
		// a column the plan states per file (a partition value): a constant of this file's read. A NULL constant adds
		// nothing — the column is absent from the file, so it reads its NULL default.
		auto &constants = plan->constants[index];
		if (!constants.empty()) {
			for (idx_t i = 0; i < global_column_ids.size(); i++) {
				auto &id = global_column_ids[i];
				if (id.IsVirtualColumn()) {
					continue;
				}
				auto value = constants.find(id.GetPrimaryIndex());
				if (value != constants.end() && !value->second.IsNull()) {
					reader_data.constant_map.Add(MultiFileGlobalIndex(i), value->second);
				}
			}
		}
	}

	//! A file whose timestamp column is naive where the table's is UTC-adjusted (or the reverse) — a DuckLake 'utc'
	//! mirror, Spark INT96 — would be cast by the reader in the SESSION's time zone, and its filters too, before this
	//! reader can intervene: a shifted value with no error. Until a plan can say how to convert, such a file is
	//! refused loudly.
	static void RefuseZoneDependentCasts(const BaseFileReader &reader,
	                                     const vector<MultiFileColumnDefinition> &global_columns) {
		auto is_timestamp = [](const LogicalType &type) {
			switch (type.id()) {
			case LogicalTypeId::TIMESTAMP:
			case LogicalTypeId::TIMESTAMP_SEC:
			case LogicalTypeId::TIMESTAMP_MS:
			case LogicalTypeId::TIMESTAMP_NS:
			case LogicalTypeId::TIMESTAMP_TZ:
				return true;
			default:
				return false;
			}
		};
		for (auto &global : global_columns) {
			if (!is_timestamp(global.type)) {
				continue;
			}
			auto name = global.GetIdentifierName();
			for (auto &local : reader.columns) {
				if (!StringUtil::CIEquals(local.name.GetIdentifierName(), name) || !is_timestamp(local.type)) {
					continue;
				}
				bool global_zoned = global.type.id() == LogicalTypeId::TIMESTAMP_TZ;
				bool local_zoned = local.type.id() == LogicalTypeId::TIMESTAMP_TZ;
				if (global_zoned != local_zoned) {
					throw IOException("fabricator file scan: '%s' stores column '%s' as %s where the table declares %s; "
					                  "the file scan cannot convert it independently of the session time zone yet "
					                  "(SET fabricator_file_scan = false)",
					                  reader.file.path, name, local.type.ToString(), global.type.ToString());
				}
			}
		}
	}

	MultiFileReaderVirtualColumnBinding GetVirtualColumnExpression(ClientContext &context,
	                                                               MultiFileReaderData &reader_data,
	                                                               const vector<MultiFileColumnDefinition> &local_columns,
	                                                               const idx_t column_id, const LogicalType &type,
	                                                               MultiFileLocalIndex local_idx) override {
		auto column = plan->virtual_columns.find(column_id);
		if (column != plan->virtual_columns.end()) {
			auto name = column->second.name.GetIdentifierName();
			auto spec = plan->virtual_specs.find(name);
			if (spec != plan->virtual_specs.end()) {
				return VirtualBinding(context, reader_data, local_columns, name, spec->second, type, local_idx);
			}
			if (column_id != COLUMN_IDENTIFIER_FILENAME && column_id != COLUMN_IDENTIFIER_FILE_ROW_NUMBER) {
				throw NotImplementedException("fabricator file scan: the plan does not compute the virtual column '%s'",
				                              name);
			}
		}
		return MultiFileReader::GetVirtualColumnExpression(context, reader_data, local_columns, column_id, type,
		                                                   local_idx);
	}

	//! COALESCE(the file's own column `stored`, the file's value [+ file_row_number]), the parts that exist; NULL
	//! when neither does.
	MultiFileReaderVirtualColumnBinding VirtualBinding(ClientContext &context, MultiFileReaderData &reader_data,
	                                                  const vector<MultiFileColumnDefinition> &local_columns,
	                                                  const string &name, const VirtualColumnSpec &spec,
	                                                  const LogicalType &type, MultiFileLocalIndex local_idx) {
		auto index = FileIndex(reader_data.file_to_be_opened, *plan);
		Value value;
		auto entry = plan->virtual_values[index].find(name);
		if (entry != plan->virtual_values[index].end()) {
			value = entry->second.DefaultCastAs(type);
		}
		optional_idx stored;
		if (!spec.stored.empty()) {
			for (idx_t i = 0; i < local_columns.size(); i++) {
				if (local_columns[i].name.GetIdentifierName() == spec.stored) {
					stored = i;
					break;
				}
			}
		}
		// the file's value; `ref` = where file_row_number lands among the binding's columns
		auto derived = [&](idx_t ref) -> unique_ptr<Expression> {
			if (spec.add_row_number) {
				return AddRowNumber(context, value, type, ref);
			}
			return make_uniq<BoundConstantExpression>(value);
		};
		if (stored.IsValid()) {
			vector<idx_t> column_ids {stored.GetIndex()};
			if (value.IsNull()) {
				return MultiFileReaderVirtualColumnBinding(
				    make_uniq<BoundReferenceExpression>(type, local_idx.GetIndex()), std::move(column_ids));
			}
			auto coalesce = make_uniq<BoundOperatorExpression>(ExpressionType::OPERATOR_COALESCE, type);
			coalesce->GetChildrenMutable().push_back(make_uniq<BoundReferenceExpression>(type, local_idx.GetIndex()));
			if (spec.add_row_number) {
				column_ids.push_back(COLUMN_IDENTIFIER_FILE_ROW_NUMBER);
			}
			coalesce->GetChildrenMutable().push_back(derived(local_idx.GetIndex() + 1));
			return MultiFileReaderVirtualColumnBinding(std::move(coalesce), std::move(column_ids));
		}
		if (value.IsNull() || !spec.add_row_number) {
			return MultiFileReaderVirtualColumnBinding(value.IsNull() ? Value(type) : value);
		}
		vector<idx_t> column_ids {COLUMN_IDENTIFIER_FILE_ROW_NUMBER};
		return MultiFileReaderVirtualColumnBinding(derived(local_idx.GetIndex()), std::move(column_ids));
	}

	//! A stream part is read by the provider; every other part by DuckDB's own reader for its format.
	shared_ptr<BaseFileReader> CreateReader(ClientContext &context, GlobalTableFunctionState &gstate,
	                                        const OpenFileInfo &file, idx_t file_idx,
	                                        const MultiFileBindData &bind_data) override {
		auto index = FileIndex(file, *plan);
		if (!plan->stream_parts[index].empty()) {
			return make_shared_ptr<StreamPartReader>(file, plan, index);
		}
		return MultiFileReader::CreateReader(context, gstate, file, file_idx, bind_data);
	}

	unique_ptr<MultiFileReader> Copy() const override {
		return make_uniq<FabricatorFileScanReader>(plan);
	}

	shared_ptr<FileScanPlan> plan;
};

optional_ptr<TableCatalogEntry> FileScanTableEntry(optional_ptr<const FunctionData> bind_data) {
	auto &data = bind_data->Cast<MultiFileBindData>();
	auto &reader = data.multi_file_reader->Cast<FabricatorFileScanReader>();
	return reader.plan->table;
}

unique_ptr<NodeStatistics> FileScanCardinality(ClientContext &, const FunctionData *bind_data) {
	auto &data = bind_data->Cast<MultiFileBindData>();
	auto rows = data.multi_file_reader->Cast<FabricatorFileScanReader>().plan->row_count;
	if (rows < 0) {
		return make_uniq<NodeStatistics>();
	}
	return make_uniq<NodeStatistics>(idx_t(rows), idx_t(rows));
}

BindInfo FileScanBindInfo(TableFunctionGetBindInfoInput &) {
	return BindInfo(ScanType::TABLE);
}

virtual_column_map_t FileScanVirtualColumns(ClientContext &, optional_ptr<FunctionData> bind_data) {
	auto &data = bind_data->Cast<MultiFileBindData>();
	return data.multi_file_reader->Cast<FabricatorFileScanReader>().plan->virtual_columns;
}

void Trace(const string &message) {
	if (getenv("FABRICATOR_DUMP_FILTERS")) {
		Printer::Print("FILE SCAN " + message);
	}
}

// ---- reading the plan: a malformed doc is an error that names what is wrong, never a crash ------------------------

[[noreturn]] void PlanError(const string &what) {
	throw IOException("fabricator file scan: malformed plan from the provider: %s", what);
}

//! The member if present, checked to be an object; null when absent.
yyjson_val *PlanObject(yyjson_val *parent, const char *key, const string &where) {
	auto value = yyjson_obj_get(parent, key);
	if (value && !yyjson_is_obj(value)) {
		PlanError(where + "." + key + " must be an object");
	}
	return value;
}

//! A string member, or null when absent; any other type is an error.
const char *PlanOptionalString(yyjson_val *parent, const char *key, const string &where) {
	auto value = yyjson_obj_get(parent, key);
	if (!value || yyjson_is_null(value)) {
		return nullptr;
	}
	if (!yyjson_is_str(value)) {
		PlanError(where + "." + key + " must be a string");
	}
	return yyjson_get_str(value);
}

//! An integer value (non-negative when `non_negative`); any other type is an error.
int64_t PlanInteger(yyjson_val *value, const string &what, bool non_negative) {
	if (!yyjson_is_int(value)) {
		PlanError(what + " must be an integer");
	}
	if (yyjson_is_uint(value) && yyjson_get_uint(value) > uint64_t(NumericLimits<int64_t>::Maximum())) {
		PlanError(what + " is out of range");
	}
	auto result = yyjson_get_sint(value);
	if (non_negative && result < 0) {
		PlanError(what + " must not be negative");
	}
	return result;
}

//! Sets a column's stored name (the plan's {"stored": …, "members": {…}}) as its identifier, recursively through
//! struct members: the reader matches the identifier, the scan still outputs the table's names.
void ApplyStoredNames(MultiFileColumnDefinition &column, yyjson_val *spec) {
	if (!spec) {
		return;
	}
	auto where = "columns." + column.name.GetIdentifierName();
	if (!yyjson_is_obj(spec)) {
		PlanError(where + " must be an object");
	}
	auto stored = PlanOptionalString(spec, "stored", where);
	if (stored) {
		column.identifier = Value(stored);
	}
	auto field_id = yyjson_obj_get(spec, "field_id");
	if (field_id) {
		auto id = PlanInteger(field_id, where + ".field_id", true);
		if (id > NumericLimits<int32_t>::Maximum()) {
			PlanError(where + ".field_id is out of range");
		}
		column.identifier = Value::INTEGER(int32_t(id)); // matched by field id (the plan says so for every column)
	}
	auto members = PlanObject(spec, "members", where);
	if (!members) {
		return;
	}
	// the plan names a list's elements "element" and a map's "key" / "value"; the table column's nodes are "list" and
	// "key" / "value" (TableColumnDefinition)
	switch (column.type.id()) {
	case LogicalTypeId::LIST:
	case LogicalTypeId::ARRAY:
		ApplyStoredNames(column.children[0], yyjson_obj_get(members, "element"));
		break;
	case LogicalTypeId::MAP:
		ApplyStoredNames(column.children[0], yyjson_obj_get(members, "key"));
		ApplyStoredNames(column.children[1], yyjson_obj_get(members, "value"));
		break;
	default:
		for (auto &child : column.children) {
			ApplyStoredNames(child, yyjson_obj_get(members, child.name.GetIdentifierName().c_str()));
		}
	}
}

//! A table column as the mapper matches files onto it: a child per struct member, the element of a list ("list"), and
//! the key and the value of a map — directly, where a FILE's map has a "key_value" node between (MapColumnMap).
MultiFileColumnDefinition TableColumnDefinition(const Identifier &name, const LogicalType &type) {
	MultiFileColumnDefinition result(name, type);
	switch (type.id()) {
	case LogicalTypeId::STRUCT:
		for (auto &child : StructType::GetChildTypes(type)) {
			result.children.push_back(TableColumnDefinition(child.first, child.second));
		}
		break;
	case LogicalTypeId::LIST:
		result.children.push_back(TableColumnDefinition(Identifier("list"), ListType::GetChildType(type)));
		break;
	case LogicalTypeId::ARRAY:
		result.children.push_back(TableColumnDefinition(Identifier("list"), ArrayType::GetChildType(type)));
		break;
	case LogicalTypeId::MAP:
		result.children.push_back(TableColumnDefinition(Identifier("key"), MapType::KeyType(type)));
		result.children.push_back(TableColumnDefinition(Identifier("value"), MapType::ValueType(type)));
		break;
	default:
		break;
	}
	return result;
}

//! A column or struct member a file lacks (added later) reads NULL, at every depth. By name the mapper would supply a
//! member's NULL itself; by field id it refuses a member without a default ("Field … is missing from the file schema
//! and has no default expression").
void SetNullDefaults(MultiFileColumnDefinition &column) {
	column.default_expression = ConstantExpression::FromValue(Value(column.type));
	if (column.type.id() != LogicalTypeId::STRUCT) {
		return; // a list's element or a map's entry is not a member that can be missing
	}
	for (auto &child : column.children) {
		SetNullDefaults(child);
	}
}

//! Frees a yyjson document at scope exit.
struct YyjsonDoc {
	explicit YyjsonDoc(const string &json) : doc(yyjson_read(json.data(), json.size(), 0)) {
		if (!doc) {
			throw IOException("fabricator file scan: the provider's plan is not JSON");
		}
	}
	~YyjsonDoc() {
		yyjson_doc_free(doc);
	}
	yyjson_val *Root() const {
		return yyjson_doc_get_root(doc);
	}
	yyjson_doc *doc;
};

//! Reads the plan (see abi.h table_file_plan) into `plan`: the table's half from the JSON doc, the parts from the
//! Arrow batch. False (with the reason traced) when the provider declined or listed no parts.
bool ParsePlan(ClientContext &context, const YyjsonDoc &doc, ArrowArrayStream &parts, FileScanPlan &plan) {
	auto root = doc.Root();
	if (!yyjson_is_obj(root)) {
		PlanError("the plan must be an object");
	}
	if (yyjson_obj_get(root, "declined")) {
		auto reason = PlanOptionalString(root, "declined", "plan");
		Trace(string("declined: ") + (reason ? reason : "?"));
		return false;
	}
	auto rows = yyjson_obj_get(root, "row_count");
	if (rows) {
		plan.row_count = PlanInteger(rows, "row_count", true);
	}
	size_t vi, vmax;
	yyjson_val *vkey, *vval;
	yyjson_obj_foreach(PlanObject(root, "virtual", "plan"), vi, vmax, vkey, vval) {
		auto where = string("virtual.") + yyjson_get_str(vkey);
		if (!yyjson_is_obj(vval)) {
			PlanError(where + " must be an object");
		}
		VirtualColumnSpec spec;
		auto stored = PlanOptionalString(vval, "stored", where);
		spec.stored = stored ? stored : "";
		auto add = yyjson_obj_get(vval, "add_row_number");
		if (add && !yyjson_is_bool(add)) {
			PlanError(where + ".add_row_number must be a boolean");
		}
		spec.add_row_number = add && yyjson_get_bool(add);
		plan.virtual_specs[yyjson_get_str(vkey)] = std::move(spec);
	}
	if (!parts.release) {
		PlanError("no parts");
	}

	// the parts: a row each; the column names say what each column is (abi.h)
	ArrowChunkReader reader(context, parts);
	auto &names = reader.Names();
	auto &types = reader.Types();
	optional_idx path_col, stream_col, size_col, deleted_col;
	vector<pair<string, idx_t>> constant_cols, virtual_cols;
	for (idx_t c = 0; c < names.size(); c++) {
		auto &name = names[c];
		auto expect = [&](const LogicalType &type) {
			if (types[c] != type) {
				PlanError("parts column '" + name + "' must be " + type.ToString() + ", not " + types[c].ToString());
			}
		};
		if (name == "path") {
			expect(LogicalType::VARCHAR);
			path_col = c;
		} else if (name == "stream") {
			expect(LogicalType::BOOLEAN);
			stream_col = c;
		} else if (name == "size") {
			expect(LogicalType::BIGINT);
			size_col = c;
		} else if (name == "deleted") {
			expect(LogicalType::LIST(LogicalType::BIGINT));
			deleted_col = c;
		} else if (StringUtil::StartsWith(name, "constant:")) {
			expect(LogicalType::VARCHAR);
			constant_cols.emplace_back(name.substr(9), c);
		} else if (StringUtil::StartsWith(name, "virtual:")) {
			expect(LogicalType::BIGINT);
			virtual_cols.emplace_back(name.substr(8), c);
		} else {
			PlanError("unknown parts column '" + name + "'");
		}
	}
	if (!path_col.IsValid()) {
		PlanError("the parts have no 'path' column");
	}
	DataChunk chunk;
	chunk.Initialize(Allocator::DefaultAllocator(), types);
	while (reader.Next(chunk)) {
		for (auto &vector : chunk.data) {
			vector.Flatten();
		}
		const list_entry_t *entries = nullptr;
		const int64_t *positions = nullptr;
		if (deleted_col.IsValid()) {
			auto &list = chunk.data[deleted_col.GetIndex()];
			entries = FlatVector::GetData<list_entry_t>(list);
			auto &child = ListVector::GetChildMutable(list);
			child.Flatten();
			positions = FlatVector::GetData<int64_t>(child);
		}
		for (idx_t row = 0; row < chunk.size(); row++) {
			auto where = "parts[" + std::to_string(plan.files.size()) + "]";
			auto path_value = chunk.GetValue(path_col.GetIndex(), row);
			if (path_value.IsNull() || StringValue::Get(path_value).empty()) {
				PlanError(where + " has no path");
			}
			auto path = StringValue::Get(path_value);
			OpenFileInfo info(path);
			info.extended_info = make_shared_ptr<ExtendedOpenFileInfo>();
			auto &options = info.extended_info->options;
			if (size_col.IsValid()) {
				auto size = chunk.GetValue(size_col.GetIndex(), row);
				if (!size.IsNull()) {
					if (size.GetValue<int64_t>() < 0) {
						PlanError(where + ".size must not be negative");
					}
					options["file_size"] = Value::UBIGINT(uint64_t(size.GetValue<int64_t>()));
				}
			}
			// a planned file is immutable (Delta and Iceberg never rewrite one in place): its size from the plan,
			// no validation of a cached copy
			options["validate_external_file_cache"] = Value::BOOLEAN(false);
			options["etag"] = Value("");
			options["last_modified"] = Value::TIMESTAMP(timestamp_t(0));
			options[FILE_KEY] = Value::UBIGINT(plan.files.size());
			vector<int64_t> deleted;
			if (entries && FlatVector::Validity(chunk.data[deleted_col.GetIndex()]).RowIsValid(row)) {
				auto &entry = entries[row];
				deleted.reserve(entry.length);
				for (idx_t i = 0; i < entry.length; i++) {
					auto position = positions[entry.offset + i];
					if (position < 0) {
						PlanError(where + ".deleted must not be negative");
					}
					deleted.push_back(position);
				}
				std::sort(deleted.begin(), deleted.end());
			}
			bool stream = stream_col.IsValid() && chunk.GetValue(stream_col.GetIndex(), row).GetValue<bool>();
			if (stream && !deleted.empty()) {
				PlanError(where + " is a stream part with deleted rows: the provider applies its own deletes");
			}
			case_insensitive_map_t<Value> constants;
			for (auto &constant : constant_cols) {
				constants[constant.first] = chunk.GetValue(constant.second, row);
			}
			case_insensitive_map_t<Value> virtual_values;
			for (auto &value : virtual_cols) {
				virtual_values[value.first] = chunk.GetValue(value.second, row);
			}
			plan.stream_parts.push_back(stream ? path : "");
			plan.files.push_back(std::move(info));
			plan.deleted.push_back(std::move(deleted));
			plan.constant_text.push_back(std::move(constants));
			plan.virtual_values.push_back(std::move(virtual_values));
		}
	}
	if (plan.files.empty()) {
		Trace("declined: no files");
		return false;
	}
	return true;
}

} // namespace

TableCatalogEntry &FileScanTable(const FunctionData &bind_data, std::string &at_unit, std::string &at_value) {
	auto &plan = *bind_data.Cast<MultiFileBindData>().multi_file_reader->Cast<FabricatorFileScanReader>().plan;
	at_unit = plan.at_unit;
	at_value = plan.at_value;
	return *plan.table;
}

bool FileScanEnabled(ClientContext &context) {
	Value enabled;
	if (context.TryGetCurrentSetting("fabricator_file_scan", enabled) && !enabled.IsNull() &&
	    enabled.GetValue<bool>()) {
		return true;
	}
	auto env = getenv("FABRICATOR_FILE_SCAN");
	return env && string(env) == "1";
}

bool TryBuildFileScan(ClientContext &context, TableCatalogEntry &table, FabricatorHandle table_handle,
                      const std::string &at_unit, const std::string &at_value, unique_ptr<FunctionData> &bind_data,
                      TableFunction &result) {
	auto plan = make_shared_ptr<FileScanPlan>();
	ArrowArrayStream parts;
	auto json = TableFilePlan(table_handle, at_unit, at_value, parts);
	// the parts stream is released whatever happens below (a declined plan never set it)
	ArrowArrayStreamWrapper parts_owner;
	parts_owner.arrow_array_stream = parts;
	parts.release = nullptr;
	YyjsonDoc doc(json);
	if (!ParsePlan(context, doc, parts_owner.arrow_array_stream, *plan)) {
		return false;
	}

	// DuckDB's own parquet scan (the system catalog's), with our reader
	auto &db = DatabaseInstance::GetDatabase(context);
	auto system_transaction = CatalogTransaction::GetSystemTransaction(db);
	auto &system_schema = Catalog::GetSystemCatalog(db).GetSchema(system_transaction, Identifier::DefaultSchema());
	auto entry = system_schema.GetEntry(system_transaction, CatalogType::TABLE_FUNCTION_ENTRY, "parquet_scan");
	if (!entry) {
		Trace("declined: parquet_scan is not loaded");
		return false;
	}
	auto parquet = entry->Cast<TableFunctionCatalogEntry>().functions.GetFunctionByArguments(context,
	                                                                                          {LogicalType::VARCHAR});
	if (!parquet) {
		return false;
	}

	plan->table = &table;
	plan->table_handle = table_handle;
	plan->at_unit = at_unit;
	plan->at_value = at_value;
	auto column_specs = PlanObject(doc.Root(), "columns", "plan");
	for (auto &column : table.GetColumns().Logical()) {
		auto definition = TableColumnDefinition(column.Name(), column.Type());
		SetNullDefaults(definition);
		if (column_specs) {
			ApplyStoredNames(definition, yyjson_obj_get(column_specs, column.Name().GetIdentifierName().c_str()));
		}
		plan->columns.push_back(std::move(definition));
	}
	// field ids or names, never a mix: a column without an id would match nothing by id and read NULL
	idx_t with_id = 0;
	for (auto &column : plan->columns) {
		with_id += !column.identifier.IsNull() && column.identifier.type().id() == LogicalTypeId::INTEGER;
	}
	if (with_id > 0 && with_id < plan->columns.size()) {
		PlanError("some columns have a field id and some do not");
	}
	plan->by_field_id = with_id > 0;
	// the per-file constants, typed: the plan's text cast to the column type
	auto &logical_columns = table.GetColumns();
	for (auto &file_constants : plan->constant_text) {
		unordered_map<idx_t, Value> typed;
		for (auto &constant : file_constants) {
			if (!logical_columns.ColumnExists(Identifier(constant.first))) {
				throw IOException("fabricator file scan: a constant for an unknown column '%s'", constant.first);
			}
			auto &column = logical_columns.GetColumn(Identifier(constant.first));
			Value value(column.Type());
			if (!constant.second.IsNull()) {
				string error;
				auto cast = constant.second.DefaultTryCastAs(column.Type(), &error);
				if (!cast) {
					PlanError("the constant '" + constant.second.ToString() + "' for column '" + constant.first +
					          "' is not a " + column.Type().ToString() + (error.empty() ? "" : ": " + error));
				}
				value = std::move(*cast);
			}
			typed[column.Logical().index] = std::move(value);
		}
		plan->constants.push_back(std::move(typed));
	}
	plan->virtual_columns = table.GetVirtualColumns();
	plan->virtual_columns.emplace(MultiFileReader::COLUMN_IDENTIFIER_FILENAME,
	                              TableColumn("filename", LogicalType::VARCHAR));
	plan->virtual_columns.emplace(MultiFileReader::COLUMN_IDENTIFIER_FILE_ROW_NUMBER,
	                              TableColumn("file_row_number", LogicalType::BIGINT));

	TableFunction function = *parquet;
	function.get_multi_file_reader = FabricatorFileScanReader::CreateInstance;
	function.function_info = plan;
	// a copy of this plan cannot be rebuilt from parquet_scan's serialized form (it would read the placeholder path);
	// the caller installs its own pair, which rebuilds through the catalog entry
	function.serialize = nullptr;
	function.deserialize = nullptr;
	// parquet's own statistics and partition stats read the files' footers, which know nothing of deleted rows
	function.statistics = nullptr;
	function.statistics_extended = nullptr;
	function.get_partition_stats = nullptr;
	function.late_materialization = false;
	function.cardinality = FileScanCardinality;
	// ⚠ No struct-field pushdown extract: a path into a member some files lack raises an INTERNAL error in DuckDB
	// 2.0.0 itself (docs/duckdb-upstream-issues.md §17, verify_delta_catalog_nested_alter). Costs join filters on
	// struct fields.
	function.supports_pushdown_extract = nullptr;
	function.get_bind_info = FileScanBindInfo;
	function.get_table_entry = FileScanTableEntry;
	function.get_virtual_columns = FileScanVirtualColumns;
	function.SetName("fabricator_file_scan");

	vector<Value> inputs {Value("fabricator-file-plan")};
	named_argument_map_t named;
	vector<LogicalType> input_types;
	vector<Identifier> input_names;
	TableFunctionRef ref;
	BoundTableFunction bound(function);
	TableFunctionBindInput input(inputs, named, input_types, input_names, nullptr, nullptr, bound, ref);
	vector<LogicalType> return_types;
	vector<Identifier> names;
	bind_data = function.bind(context, input, return_types, names);
	bind_data->Cast<MultiFileBindData>().virtual_columns = plan->virtual_columns;
	Trace("on: " + std::to_string(plan->files.size()) + " files");
	result = std::move(function);
	return true;
}

} // namespace fabricator
