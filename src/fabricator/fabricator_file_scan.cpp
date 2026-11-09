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
	vector<MultiFileColumnDefinition> columns;
	optional_ptr<TableCatalogEntry> table;
	//! the table's virtual columns plus the reader's own (filename, file_row_number)
	virtual_column_map_t virtual_columns;
	//! the live row count the provider reported (-1 = unknown)
	int64_t row_count = -1;
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
	auto members = PlanObject(spec, "members", where);
	if (!members) {
		return;
	}
	for (auto &child : column.children) {
		ApplyStoredNames(child, yyjson_obj_get(members, child.name.GetIdentifierName().c_str()));
	}
}

//! A member a file lacks (added to a struct later) reads NULL, at every depth.
void SetNullDefaults(MultiFileColumnDefinition &column) {
	column.default_expression = ConstantExpression::FromValue(Value(column.type));
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

//! Reads the plan (see abi.h table_file_plan) into `plan`; false (with the reason traced) when the provider
//! declined or listed no files.
bool ParsePlan(const YyjsonDoc &doc, FileScanPlan &plan) {
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
	auto files = yyjson_obj_get(root, "files");
	if (!yyjson_is_arr(files)) {
		PlanError("files must be an array");
	}
	size_t idx, max;
	yyjson_val *file;
	yyjson_arr_foreach(files, idx, max, file) {
		auto where = "files[" + std::to_string(idx) + "]";
		if (!yyjson_is_obj(file)) {
			PlanError(where + " must be an object");
		}
		auto path = PlanOptionalString(file, "path", where);
		if (!path || !*path) {
			PlanError(where + " has no path");
		}
		OpenFileInfo info(path);
		info.extended_info = make_shared_ptr<ExtendedOpenFileInfo>();
		auto &options = info.extended_info->options;
		// a planned file is immutable (Delta and Iceberg never rewrite one in place): its size from the plan,
		// no validation of a cached copy
		auto size = yyjson_obj_get(file, "size");
		if (size) {
			options["file_size"] = Value::UBIGINT(uint64_t(PlanInteger(size, where + ".size", true)));
		}
		options["validate_external_file_cache"] = Value::BOOLEAN(false);
		options["etag"] = Value("");
		options["last_modified"] = Value::TIMESTAMP(timestamp_t(0));
		options[FILE_KEY] = Value::UBIGINT(plan.files.size());
		vector<int64_t> deleted;
		auto dv = yyjson_obj_get(file, "dv");
		if (dv && !yyjson_is_arr(dv)) {
			PlanError(where + ".dv must be an array");
		}
		size_t di, dmax;
		yyjson_val *pos;
		yyjson_arr_foreach(dv, di, dmax, pos) {
			deleted.push_back(PlanInteger(pos, where + ".dv", true));
		}
		std::sort(deleted.begin(), deleted.end());
		case_insensitive_map_t<Value> constants;
		size_t ci, cmax;
		yyjson_val *ckey, *cval;
		yyjson_obj_foreach(PlanObject(file, "constants", where), ci, cmax, ckey, cval) {
			if (!yyjson_is_null(cval) && !yyjson_is_str(cval)) {
				PlanError(where + ".constants." + yyjson_get_str(ckey) + " must be a string or null");
			}
			constants[yyjson_get_str(ckey)] = yyjson_is_null(cval) ? Value() : Value(yyjson_get_str(cval));
		}
		case_insensitive_map_t<Value> virtual_values;
		size_t fi, fmax;
		yyjson_val *fkey, *fval;
		yyjson_obj_foreach(PlanObject(file, "virtual", where), fi, fmax, fkey, fval) {
			virtual_values[yyjson_get_str(fkey)] =
			    yyjson_is_null(fval) ? Value()
			                         : Value::BIGINT(PlanInteger(fval, where + ".virtual." + yyjson_get_str(fkey), false));
		}
		plan.files.push_back(std::move(info));
		plan.deleted.push_back(std::move(deleted));
		plan.constant_text.push_back(std::move(constants));
		plan.virtual_values.push_back(std::move(virtual_values));
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
	YyjsonDoc doc(TableFilePlan(table_handle, at_unit, at_value));
	if (!ParsePlan(doc, *plan)) {
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
	auto column_specs = PlanObject(doc.Root(), "columns", "plan");
	for (auto &column : table.GetColumns().Logical()) {
		auto definition = MultiFileColumnDefinition::CreateFromNameAndType(column.Name(), column.Type());
		SetNullDefaults(definition);
		if (column_specs) {
			ApplyStoredNames(definition, yyjson_obj_get(column_specs, column.Name().GetIdentifierName().c_str()));
		}
		plan->columns.push_back(std::move(definition));
	}
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
