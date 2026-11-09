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

#include "fabricator/arrow_ingest.hpp" // ProviderVirtualBase
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

//! Keys on each file's extended info: the index of its deleted positions in the plan, and its first row's row id.
constexpr const char *FILE_KEY = "fabricator_file";
constexpr const char *ROWID_BASE_KEY = "fabricator_rowid_base";
//! Row tracking per file: the first row's stable row id, and the default commit version (absent = unknown).
constexpr const char *BASE_ROW_ID_KEY = "fabricator_base_row_id";
constexpr const char *COMMIT_VERSION_KEY = "fabricator_commit_version";
//! The provider's row-tracking virtual columns, by the names the table entry declares them under.
constexpr const char *ROW_ID_VIRTUAL = "__delta_row_id";
constexpr const char *ROW_VERSION_VIRTUAL = "__delta_row_commit_version";

//! The plan, owned by the scan function (function_info) for the scan's life.
struct FileScanPlan : public TableFunctionInfo {
	vector<OpenFileInfo> files;
	//! per file (by FILE_KEY), the deleted row positions, sorted; empty = none
	vector<vector<int64_t>> deleted;
	//! per file (by FILE_KEY), its partition values as the log states them (text; NULL = a null partition)
	vector<case_insensitive_map_t<Value>> partition_text;
	//! per file, the partition values cast to their columns' types, by column index (filled at bind)
	vector<unordered_map<idx_t, Value>> partition_values;
	//! the table's columns, in order — the scan's schema; files are mapped onto it BY NAME
	vector<MultiFileColumnDefinition> columns;
	optional_ptr<TableCatalogEntry> table;
	//! the table's virtual columns (rowid, provider columns) plus the reader's own (filename, file_row_number)
	virtual_column_map_t virtual_columns;
	//! the live row count the provider reported (-1 = unknown)
	int64_t row_count = -1;
	//! the stored names of the materialized row-tracking columns (a file's value wins where non-NULL)
	string row_id_column;
	string row_version_column;
	//! the AT clause the scan was bound with ("" = latest)
	string at_unit;
	string at_value;
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

optional_idx ExtendedIndex(const OpenFileInfo &file, const char *key) {
	if (!file.extended_info) {
		return optional_idx();
	}
	auto entry = file.extended_info->options.find(key);
	if (entry == file.extended_info->options.end()) {
		return optional_idx();
	}
	return optional_idx(entry->second.GetValue<uint64_t>());
}

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
		bind_data.mapping = MultiFileColumnMappingMode::BY_NAME;
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
		auto index = ExtendedIndex(reader_data.reader->file, FILE_KEY);
		if (!index.IsValid() || index.GetIndex() >= plan->deleted.size()) {
			throw InternalException("fabricator file scan: a file without its plan entry");
		}
		auto &deleted = plan->deleted[index.GetIndex()];
		if (!deleted.empty()) {
			reader_data.reader->deletion_filter = make_uniq<DeletedRowsFilter>(deleted);
		}
		// partition columns are not in the files: the file's value from the log, as a constant (a NULL partition
		// adds nothing — the column is absent from the file, so it reads its NULL default)
		auto &partition = plan->partition_values[index.GetIndex()];
		if (!partition.empty()) {
			for (idx_t i = 0; i < global_column_ids.size(); i++) {
				auto &id = global_column_ids[i];
				if (id.IsVirtualColumn()) {
					continue;
				}
				auto value = partition.find(id.GetPrimaryIndex());
				if (value != partition.end() && !value->second.IsNull()) {
					reader_data.constant_map.Add(MultiFileGlobalIndex(i), value->second);
				}
			}
		}
	}

	MultiFileReaderVirtualColumnBinding GetVirtualColumnExpression(ClientContext &context,
	                                                               MultiFileReaderData &reader_data,
	                                                               const vector<MultiFileColumnDefinition> &local_columns,
	                                                               const idx_t column_id, const LogicalType &type,
	                                                               MultiFileLocalIndex local_idx) override {
		if (column_id == COLUMN_IDENTIFIER_ROW_ID) {
			// the provider's row id: the file's first row id + the row's position in the file
			auto base = ExtendedIndex(reader_data.file_to_be_opened, ROWID_BASE_KEY);
			if (!base.IsValid()) {
				throw InternalException("fabricator file scan: a file without its row id base");
			}
			vector<unique_ptr<Expression>> children;
			children.push_back(make_uniq<BoundConstantExpression>(Value::BIGINT(int64_t(base.GetIndex()))));
			children.push_back(make_uniq<BoundReferenceExpression>(type, local_idx.GetIndex()));
			FunctionBinder binder(context);
			ErrorData error;
			auto sum = binder.BindScalarFunction(Identifier::DefaultSchema(), "+", std::move(children), error, true,
			                                     nullptr);
			if (error.HasError()) {
				error.Throw();
			}
			vector<idx_t> column_ids {COLUMN_IDENTIFIER_FILE_ROW_NUMBER};
			return MultiFileReaderVirtualColumnBinding(std::move(sum), std::move(column_ids));
		}
		auto virtual_column = plan->virtual_columns.find(column_id);
		if (column_id >= ProviderVirtualBase() && virtual_column != plan->virtual_columns.end()) {
			return RowTrackingBinding(context, reader_data, local_columns,
			                          virtual_column->second.name.GetIdentifierName(), type, local_idx);
		}
		return MultiFileReader::GetVirtualColumnExpression(context, reader_data, local_columns, column_id, type,
		                                                   local_idx);
	}

	//! A row-tracking column, as DeltaNativeReader.RowTrackingExpr: COALESCE(the materialized column, the derived
	//! value) where the file stores one, else the derived value — base_row_id + file_row_number for the id, the
	//! file's default commit version for the version — else NULL.
	MultiFileReaderVirtualColumnBinding RowTrackingBinding(ClientContext &context, MultiFileReaderData &reader_data,
	                                                      const vector<MultiFileColumnDefinition> &local_columns,
	                                                      const string &name, const LogicalType &type,
	                                                      MultiFileLocalIndex local_idx) {
		const bool is_id = name == ROW_ID_VIRTUAL;
		if (!is_id && name != ROW_VERSION_VIRTUAL) {
			throw NotImplementedException("fabricator file scan: the virtual column '%s' is not read by the file scan",
			                              name);
		}
		auto &file = reader_data.file_to_be_opened;
		auto derived_value = ExtendedIndex(file, is_id ? BASE_ROW_ID_KEY : COMMIT_VERSION_KEY);
		const string &stored = is_id ? plan->row_id_column : plan->row_version_column;
		optional_idx materialized;
		for (idx_t i = 0; i < local_columns.size(); i++) {
			if (local_columns[i].name.GetIdentifierName() == stored) {
				materialized = i;
				break;
			}
		}
		// the derived expression; `ref` = where file_row_number lands among the binding's columns
		auto derived = [&](idx_t ref) -> unique_ptr<Expression> {
			auto value = make_uniq<BoundConstantExpression>(Value::BIGINT(int64_t(derived_value.GetIndex())));
			if (!is_id) {
				return std::move(value);
			}
			vector<unique_ptr<Expression>> children;
			children.push_back(std::move(value));
			children.push_back(make_uniq<BoundReferenceExpression>(type, ref));
			FunctionBinder binder(context);
			ErrorData error;
			auto sum = binder.BindScalarFunction(Identifier::DefaultSchema(), "+", std::move(children), error, true,
			                                     nullptr);
			if (error.HasError()) {
				error.Throw();
			}
			return sum;
		};
		if (materialized.IsValid()) {
			vector<idx_t> column_ids {materialized.GetIndex()};
			if (!derived_value.IsValid()) {
				return MultiFileReaderVirtualColumnBinding(
				    make_uniq<BoundReferenceExpression>(type, local_idx.GetIndex()), std::move(column_ids));
			}
			auto coalesce = make_uniq<BoundOperatorExpression>(ExpressionType::OPERATOR_COALESCE, type);
			coalesce->GetChildrenMutable().push_back(make_uniq<BoundReferenceExpression>(type, local_idx.GetIndex()));
			if (is_id) {
				column_ids.push_back(COLUMN_IDENTIFIER_FILE_ROW_NUMBER);
			}
			coalesce->GetChildrenMutable().push_back(derived(local_idx.GetIndex() + 1));
			return MultiFileReaderVirtualColumnBinding(std::move(coalesce), std::move(column_ids));
		}
		if (!derived_value.IsValid()) {
			return MultiFileReaderVirtualColumnBinding(Value(type));
		}
		if (!is_id) {
			return MultiFileReaderVirtualColumnBinding(Value::BIGINT(int64_t(derived_value.GetIndex())));
		}
		vector<idx_t> column_ids {COLUMN_IDENTIFIER_FILE_ROW_NUMBER};
		return MultiFileReaderVirtualColumnBinding(derived(local_idx.GetIndex()), std::move(column_ids));
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

//! Parses the plan JSON; false (with the reason traced) when the provider declined or the plan is empty.
//! `physical` receives the name each column is stored under, where it differs (Delta column mapping 'name').
bool ParsePlan(const string &json, FileScanPlan &plan, case_insensitive_map_t<string> &physical) {
	auto doc = yyjson_read(json.data(), json.size(), 0);
	if (!doc) {
		throw IOException("fabricator file scan: the provider's plan is not JSON");
	}
	auto root = yyjson_doc_get_root(doc);
	auto declined = yyjson_obj_get(root, "declined");
	if (declined) {
		Trace(string("declined: ") + (yyjson_get_str(declined) ? yyjson_get_str(declined) : "?"));
		yyjson_doc_free(doc);
		return false;
	}
	auto row_id_column = yyjson_get_str(yyjson_obj_get(root, "row_id_column"));
	auto row_version_column = yyjson_get_str(yyjson_obj_get(root, "row_version_column"));
	plan.row_id_column = row_id_column ? row_id_column : "";
	plan.row_version_column = row_version_column ? row_version_column : "";
	auto rows = yyjson_obj_get(root, "row_count");
	if (rows) {
		plan.row_count = yyjson_get_sint(rows);
	}
	auto mapping = yyjson_obj_get(root, "physical");
	size_t mi, mmax;
	yyjson_val *key, *val;
	yyjson_obj_foreach(mapping, mi, mmax, key, val) {
		physical[yyjson_get_str(key)] = yyjson_get_str(val);
	}
	auto files = yyjson_obj_get(root, "files");
	size_t idx, max;
	yyjson_val *file;
	yyjson_arr_foreach(files, idx, max, file) {
		auto path = yyjson_get_str(yyjson_obj_get(file, "path"));
		auto base = yyjson_obj_get(file, "rowid_base");
		if (!path || !base) {
			yyjson_doc_free(doc);
			throw IOException("fabricator file scan: a plan entry without path or rowid_base");
		}
		OpenFileInfo info(path);
		info.extended_info = make_shared_ptr<ExtendedOpenFileInfo>();
		auto &options = info.extended_info->options;
		// data files are immutable (Delta never rewrites one in place): the size from the log, no validation
		auto size = yyjson_obj_get(file, "size");
		if (size) {
			options["file_size"] = Value::UBIGINT(yyjson_get_uint(size));
		}
		options["validate_external_file_cache"] = Value::BOOLEAN(false);
		options["etag"] = Value("");
		options["last_modified"] = Value::TIMESTAMP(timestamp_t(0));
		options[FILE_KEY] = Value::UBIGINT(plan.files.size());
		options[ROWID_BASE_KEY] = Value::UBIGINT(yyjson_get_uint(base));
		auto base_row_id = yyjson_obj_get(file, "base_row_id");
		if (base_row_id) {
			options[BASE_ROW_ID_KEY] = Value::UBIGINT(yyjson_get_uint(base_row_id));
		}
		auto commit_version = yyjson_obj_get(file, "commit_version");
		if (commit_version) {
			options[COMMIT_VERSION_KEY] = Value::UBIGINT(yyjson_get_uint(commit_version));
		}
		vector<int64_t> deleted;
		auto dv = yyjson_obj_get(file, "dv");
		size_t di, dmax;
		yyjson_val *pos;
		yyjson_arr_foreach(dv, di, dmax, pos) {
			deleted.push_back(yyjson_get_sint(pos));
		}
		std::sort(deleted.begin(), deleted.end());
		case_insensitive_map_t<Value> partition;
		auto values = yyjson_obj_get(file, "partition");
		size_t pi, pmax;
		yyjson_val *pkey, *pval;
		yyjson_obj_foreach(values, pi, pmax, pkey, pval) {
			partition[yyjson_get_str(pkey)] = yyjson_is_null(pval) ? Value() : Value(yyjson_get_str(pval));
		}
		plan.files.push_back(std::move(info));
		plan.deleted.push_back(std::move(deleted));
		plan.partition_text.push_back(std::move(partition));
	}
	yyjson_doc_free(doc);
	if (plan.files.empty()) {
		Trace("declined: no files");
		return false;
	}
	return true;
}

//! A member a file lacks (added to a struct later) reads NULL, at every depth.
void SetNullDefaults(MultiFileColumnDefinition &column) {
	column.default_expression = ConstantExpression::FromValue(Value(column.type));
	for (auto &child : column.children) {
		SetNullDefaults(child);
	}
}

} // namespace

TableCatalogEntry &FileScanTable(const FunctionData &bind_data, std::string &at_unit, std::string &at_value) {
	auto &plan = *bind_data.Cast<MultiFileBindData>().multi_file_reader->Cast<FabricatorFileScanReader>().plan;
	at_unit = plan.at_unit;
	at_value = plan.at_value;
	return *plan.table;
}

bool FileScanEnabled() {
	auto env = getenv("FABRICATOR_FILE_SCAN");
	return env && string(env) == "1";
}

bool TryBuildFileScan(ClientContext &context, TableCatalogEntry &table, FabricatorHandle table_handle,
                      const std::string &at_unit, const std::string &at_value, unique_ptr<FunctionData> &bind_data,
                      TableFunction &result) {
	auto plan = make_shared_ptr<FileScanPlan>();
	case_insensitive_map_t<string> physical;
	if (!ParsePlan(TableFilePlan(table_handle, at_unit, at_value), *plan, physical)) {
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
	plan->at_unit = at_unit;
	plan->at_value = at_value;
	for (auto &column : table.GetColumns().Logical()) {
		auto definition = MultiFileColumnDefinition::CreateFromNameAndType(column.Name(), column.Type());
		SetNullDefaults(definition);
		// stored under another name: the reader matches the identifier, the scan still outputs the logical name
		auto stored = physical.find(column.Name().GetIdentifierName());
		if (stored != physical.end()) {
			definition.identifier = Value(stored->second);
		}
		plan->columns.push_back(std::move(definition));
	}
	// partition values, typed: the log's text cast to the column type, as the native reader's literals are
	auto &logical_columns = table.GetColumns();
	for (auto &partition : plan->partition_text) {
		unordered_map<idx_t, Value> typed;
		for (auto &entry : partition) {
			if (!logical_columns.ColumnExists(Identifier(entry.first))) {
				throw IOException("fabricator file scan: a partition value for an unknown column '%s'", entry.first);
			}
			auto &column = logical_columns.GetColumn(Identifier(entry.first));
			typed[column.Logical().index] =
			    entry.second.IsNull() ? Value(column.Type()) : entry.second.DefaultCastAs(column.Type());
		}
		plan->partition_values.push_back(std::move(typed));
	}
	plan->virtual_columns = table.GetVirtualColumns();
	plan->virtual_columns.emplace(MultiFileReader::COLUMN_IDENTIFIER_FILENAME,
	                              TableColumn("filename", LogicalType::VARCHAR));
	plan->virtual_columns.emplace(MultiFileReader::COLUMN_IDENTIFIER_FILE_ROW_NUMBER,
	                              TableColumn("file_row_number", LogicalType::BIGINT));

	TableFunction function = *parquet;
	function.get_multi_file_reader = FabricatorFileScanReader::CreateInstance;
	function.function_info = plan;
	// a copy of this plan cannot be rebuilt from parquet_scan's serialized form (it would read the placeholder path)
	function.serialize = nullptr;
	function.deserialize = nullptr;
	// parquet's own statistics and partition stats read the files' footers, which know nothing of deleted rows
	function.statistics = nullptr;
	function.statistics_extended = nullptr;
	function.get_partition_stats = nullptr;
	function.late_materialization = false;
	function.cardinality = FileScanCardinality;
	// ⚠ No struct-field pushdown extract: with it, a path into a member old files lack (a nested ADD COLUMN) raised
	// INTERNAL "result vector of type INTEGER does not match expression type STRUCT(q INTEGER)" —
	// verify_delta_catalog_nested_alter:239, 3 of 3 with it, 0 without. Costs join filters on struct fields.
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
