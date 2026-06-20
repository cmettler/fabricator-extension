//===----------------------------------------------------------------------===//
//                         arrownet — schema catalog entry (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_schema_entry.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/blob.hpp"
#include "duckdb/common/vector_operations/vector_operations.hpp"
#include "duckdb/execution/expression_executor_state.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/parser/constraints/not_null_constraint.hpp"
#include "duckdb/parser/constraints/unique_constraint.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/parsed_data/alter_table_info.hpp"
#include "duckdb/parser/parsed_data/create_scalar_function_info.hpp"
#include "duckdb/parser/parsed_data/create_table_function_info.hpp"
#include "duckdb/parser/parsed_data/create_table_info.hpp"
#include "duckdb/parser/parsed_data/drop_info.hpp"
#include "duckdb/planner/parsed_data/bound_create_table_info.hpp"

namespace duckdb {

ArrowNetSchemaEntry::ArrowNetSchemaEntry(Catalog &catalog, CreateSchemaInfo &info, ArrowNetHandle handle)
    : SchemaCatalogEntry(catalog, info), handle_(handle) {
}

void ArrowNetSchemaEntry::AddTable(const string &table_name, const string &table_type) {
	lock_guard<mutex> lock(entry_lock_);
	table_types_[table_name] = table_type;
	// Drop any cached entry so the schema is re-fetched (e.g. after CREATE OR REPLACE).
	entries_.erase(table_name);
}

void ArrowNetSchemaEntry::AddScalarFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	scalar_functions_.insert(func_name);
	// Drop any cached entry so the signature is re-fetched (e.g. after CREATE OR ALTER).
	function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::AddTableFunction(const string &func_name, bool is_proc) {
	lock_guard<mutex> lock(entry_lock_);
	table_functions_[func_name] = is_proc;
	table_function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::ClearTables() {
	lock_guard<mutex> lock(entry_lock_);
	table_types_.clear();
	entries_.clear();
	scalar_functions_.clear();
	function_entries_.clear();
	table_functions_.clear();
	table_function_entries_.clear();
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateEntry(ClientContext &context, const string &table_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = entries_.find(table_name);
	if (cached != entries_.end()) {
		return cached->second.get();
	}
	auto type_it = table_types_.find(table_name);
	if (type_it == table_types_.end()) {
		return nullptr;
	}

	vector<string> names;
	vector<LogicalType> types;
	try {
		FetchTableColumns(context, handle_, name, table_name, names, types);
	} catch (std::exception &) {
		// The discovered name is stale — the table no longer exists on the server
		// (e.g. dropped out-of-band via mssql_net_exec). Treat it as not-found so
		// CREATE TABLE IF NOT EXISTS / OR REPLACE see "absent" instead of an error.
		table_types_.erase(table_name);
		entries_.erase(table_name);
		return nullptr;
	}

	CreateTableInfo info(catalog.GetName(), name, table_name);
	for (idx_t i = 0; i < names.size(); i++) {
		info.columns.AddColumn(ColumnDefinition(names[i], types[i]));
	}

	// Resolve row-identity columns (PK / smallest unique index) to column indices.
	auto rowid_names = FetchRowIdColumns(handle_, name, table_name);
	vector<idx_t> rowid_indices;
	for (auto &rowid_name : rowid_names) {
		for (idx_t i = 0; i < names.size(); i++) {
			if (StringUtil::CIEquals(names[i], rowid_name)) {
				rowid_indices.push_back(i);
				break;
			}
		}
	}
	if (rowid_indices.size() != rowid_names.size()) {
		rowid_indices.clear(); // unresolved column — disable rowid rather than risk a bad key
	}

	LogicalType rowid_type = LogicalType::BIGINT;
	if (rowid_indices.size() == 1) {
		rowid_type = types[rowid_indices[0]];
	} else if (rowid_indices.size() > 1) {
		child_list_t<LogicalType> children;
		for (auto idx : rowid_indices) {
			children.push_back(make_pair(names[idx], types[idx]));
		}
		rowid_type = LogicalType::STRUCT(std::move(children));
	}

	auto entry = make_uniq<ArrowNetTableEntry>(catalog, *this, info, handle_, std::move(rowid_indices),
	                                           std::move(rowid_type));
	auto &ref = *entry;
	entries_[table_name] = std::move(entry);
	return &ref;
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateScalarFunction(ClientContext &context,
                                                                          const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = function_entries_.find(func_name);
	if (cached != function_entries_.end()) {
		return cached->second.get();
	}
	if (scalar_functions_.find(func_name) == scalar_functions_.end()) {
		return nullptr;
	}

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	LogicalType return_type;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
		return_type = FetchFunctionReturnType(context, handle_, name, func_name);
	} catch (std::exception &) {
		// The discovered name is stale — the function no longer exists on the server
		// (e.g. dropped out-of-band). Treat it as not-found rather than erroring.
		scalar_functions_.erase(func_name);
		function_entries_.erase(func_name);
		return nullptr;
	}

	// Capture the identity for the per-call execution. The callback marshals the
	// argument chunk to Arrow, runs the UDF on the backend, and ingests the result.
	ArrowNetHandle handle = handle_;
	string schema_name = name;
	string fn_name = func_name;
	scalar_function_t exec = [handle, schema_name, fn_name, arg_types, arg_names](
	                             DataChunk &args, ExpressionState &state, Vector &result) {
		auto &ctx = state.GetContext();
		idx_t row_count = args.size();

		// Argument chunk -> a one-batch Arrow stream (in parameter order).
		auto properties = ctx.GetClientProperties();
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(ctx, arg_types);
		ArrowAppender appender(arg_types, row_count, properties, extension_types);
		appender.Append(args, 0, row_count, row_count);
		ArrowArray array = appender.Finalize();

		arrownet::ArrowProducer producer(arg_types, arg_names, properties);
		producer.AddBatch(array);
		producer.Finish();

		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		arrownet::ExecuteScalar(handle, schema_name, fn_name, *producer.Stream(), out);

		// Single-column, row_count-row result -> the output vector (matching offsets).
		arrownet::ArrowStreamReader reader(ctx, out);
		DataChunk chunk;
		chunk.Initialize(Allocator::Get(ctx), reader.Types());
		idx_t offset = 0;
		while (offset < row_count) {
			chunk.Reset();
			reader.Read(chunk);
			idx_t got = chunk.size();
			if (got == 0) {
				break; // defensive: backend returned fewer rows than requested
			}
			VectorOperations::Copy(chunk.data[0], result, got, 0, offset);
			offset += got;
		}
	};

	ScalarFunction fn(arg_types, return_type, exec);
	fn.name = func_name;
	// A remote UDF may be non-deterministic / side-effecting (VOLATILE => never folded),
	// and may return non-NULL for NULL inputs, so it must see NULL args (SPECIAL_HANDLING)
	// rather than DuckDB short-circuiting the row to NULL.
	fn.SetStability(FunctionStability::VOLATILE);
	fn.SetNullHandling(FunctionNullHandling::SPECIAL_HANDLING);

	CreateScalarFunctionInfo info(std::move(fn));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<ScalarFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	function_entries_[func_name] = std::move(entry);
	return &ref;
}

namespace {

// Carried on the registered TableFunction so its (static) bind can recover the catalog
// identity + signature of a discovered TVF (table_function_bind_t is a raw fn pointer,
// so it can't capture — unlike the scalar callback's std::function).
struct ArrowNetTableFunctionInfo : public TableFunctionInfo {
	ArrowNetHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	bool is_proc = false; // stored procedure (EXEC, no pushdown) vs TVF (FROM, pushdown)
};

// Bind a catalog-bound TVF: resolve the (fixed) output schema for the return types, then
// install a scan factory that marshals the constant call args into a 1-row Arrow batch
// and runs execute_table (which streams the result rows).
unique_ptr<FunctionData> ArrowNetTableFunctionBind(ClientContext &context, TableFunctionBindInput &input,
                                                   vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<ArrowNetTableFunctionInfo>();
	ArrowNetHandle handle = info.handle;
	string schema_name = info.schema;
	string func_name = info.func;
	bool is_proc = info.is_proc;

	// Resolve the values to marshal into the 1-row args batch (the field NAMES become the
	// proc parameter names that C# uses to build `EXEC @name=@p`). TVFs: all params,
	// positional, in order (`input.inputs`). Procs: only the SUPPLIED named parameters
	// (`input.named_parameters`), each cast to its declared type — omitted params are absent.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	if (is_proc) {
		for (auto &kv : input.named_parameters) {
			LogicalType declared = kv.second.type();
			for (idx_t i = 0; i < info.arg_names.size(); i++) {
				if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
					declared = info.arg_types[i];
					break;
				}
			}
			arg_names.push_back(kv.first);
			arg_types.push_back(declared);
			arg_values.push_back(kv.second);
		}
	} else {
		arg_types = info.arg_types;
		arg_names = info.arg_names;
		arg_values = input.inputs;
	}

	auto bind_data = make_uniq<arrownet::ArrowStreamBindData>();

	// 1) Output schema (fixed, from metadata) -> return types/names + column converters.
	bind_data->factory = [handle, schema_name, func_name](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetFunctionOutputSchema(handle, schema_name, func_name, out);
	};
	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);

	// 2) Scan factory: constant args -> 1-row Arrow batch -> execute_table (streams rows).
	// The request carries projection + best-effort filter pushdown (spec_json/filter_values),
	// built by the scan machinery from the projected column ids + the pushed filter tree.
	auto properties = context.GetClientProperties();
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
	bind_data->factory = [handle, schema_name, func_name, arg_types, arg_names, arg_values, properties, extension_types,
	                      is_proc](const arrownet::ArrowScanRequest &req, ArrowArrayStream &out) {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		ArrowAppender appender(arg_types, 1, properties, extension_types);
		appender.Append(chunk, 0, 1, 1);
		ArrowArray array = appender.Finalize();
		arrownet::ArrowProducer producer(arg_types, arg_names, properties);
		producer.AddBatch(array);
		producer.Finish();
		if (is_proc) {
			// Procs run via EXEC (not inline-wrappable) → no projection/filter pushdown.
			arrownet::ExecuteProc(handle, schema_name, func_name, *producer.Stream(), out);
		} else {
			arrownet::ExecuteTable(handle, schema_name, func_name, *producer.Stream(), req.spec_json,
			                       req.filter_values, out);
		}
	};
	// TVFs push the projected column list (by name) + filters to SQL Server (inline TVFs
	// get inlined → genuine pushdown). Procs can't, so DuckDB projects/filters locally.
	bind_data->push_projection = !is_proc;
	return std::move(bind_data);
}

} // namespace

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateTableFunction(ClientContext &context,
                                                                         const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = table_function_entries_.find(func_name);
	if (cached != table_function_entries_.end()) {
		return cached->second.get();
	}
	auto kind_it = table_functions_.find(func_name);
	if (kind_it == table_functions_.end()) {
		return nullptr;
	}
	bool is_proc = kind_it->second;

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
	} catch (std::exception &) {
		// Stale discovery (dropped out-of-band) — treat as not-found.
		table_functions_.erase(func_name);
		table_function_entries_.erase(func_name);
		return nullptr;
	}

	// TVFs take positional arguments (called positionally in a FROM clause); stored procs
	// take DuckDB named parameters (EXEC @name=val), so the caller supplies a subset and
	// omitted optional params fall back to the proc's own DEFAULT.
	vector<LogicalType> positional = is_proc ? vector<LogicalType>() : arg_types;
	TableFunction tf(func_name, positional, arrownet::ArrowStreamScan, ArrowNetTableFunctionBind,
	                 arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	tf.projection_pushdown = true;
	if (is_proc) {
		for (idx_t i = 0; i < arg_names.size(); i++) {
			tf.named_parameters[arg_names[i]] = arg_types[i];
		}
	} else {
		// Best-effort filter pushdown into the TVF (reuses the table scan's serializer; the
		// predicates are left in the plan so DuckDB re-applies them — an over-approximation
		// is safe). `SELECT <cols> FROM tvf(@args) WHERE <filter>` is emitted by C#. Procs
		// are not inline-wrappable, so they get no filter pushdown (DuckDB filters locally).
		tf.pushdown_complex_filter = ArrowNetComplexFilterPushdown;
	}
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->is_proc = is_proc;
	tf.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(tf));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::LookupEntry(CatalogTransaction transaction,
                                                            const EntryLookupInfo &lookup_info) {
	if (!transaction.context) {
		return nullptr;
	}
	auto type = lookup_info.GetCatalogType();
	if (type == CatalogType::TABLE_ENTRY) {
		return GetOrCreateEntry(*transaction.context, lookup_info.GetEntryName());
	}
	if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		return GetOrCreateScalarFunction(*transaction.context, lookup_info.GetEntryName());
	}
	if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		return GetOrCreateTableFunction(*transaction.context, lookup_info.GetEntryName());
	}
	return nullptr;
}

void ArrowNetSchemaEntry::Scan(ClientContext &context, CatalogType type,
                               const std::function<void(CatalogEntry &)> &callback) {
	if (type == CatalogType::TABLE_ENTRY) {
		for (auto &entry : table_types_) {
			auto catalog_entry = GetOrCreateEntry(context, entry.first);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		// Snapshot the names: GetOrCreateScalarFunction locks entry_lock_ and may evict
		// a stale entry, which would invalidate an iterator over scalar_functions_.
		vector<string> names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : scalar_functions_) {
				names.push_back(fn);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateScalarFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		vector<string> names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : table_functions_) {
				names.push_back(fn.first);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateTableFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
	}
}

void ArrowNetSchemaEntry::Scan(CatalogType type, const std::function<void(CatalogEntry &)> &callback) {
	// No context available: only report already-materialized entries.
	lock_guard<mutex> lock(entry_lock_);
	if (type == CatalogType::TABLE_ENTRY) {
		for (auto &entry : entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		for (auto &entry : function_entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		for (auto &entry : table_function_entries_) {
			callback(*entry.second);
		}
	}
}

[[noreturn]] static void ReadOnly(const char *op) {
	throw NotImplementedException("mssql_net: %s is not supported (read-only catalog in Phase 1)", op);
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateTable(CatalogTransaction transaction, BoundCreateTableInfo &info) {
	if (!transaction.context) {
		throw InternalException("mssql_net: CREATE TABLE requires a client context");
	}
	auto &context = *transaction.context;
	auto &base = info.Base();

	// Column names + types, and per-column nullability (NOT NULL constraints).
	vector<string> names;
	vector<LogicalType> types;
	for (auto &col : base.columns.Logical()) {
		names.push_back(col.Name());
		types.push_back(col.Type());
	}
	vector<bool> nullable(names.size(), true);
	for (auto &constraint : base.constraints) {
		if (constraint->type == ConstraintType::NOT_NULL) {
			auto &nn = constraint->Cast<NotNullConstraint>();
			if (nn.index.index < nullable.size()) {
				nullable[nn.index.index] = false;
			}
		}
	}

	// Key constraints, carried to the backend as 0-based column-index groups:
	// the PRIMARY KEY as a single comma-separated group, each UNIQUE as its own.
	vector<idx_t> pk_indices;
	vector<vector<idx_t>> unique_groups;
	for (auto &constraint : base.constraints) {
		if (constraint->type != ConstraintType::UNIQUE) {
			continue;
		}
		auto &uc = constraint->Cast<UniqueConstraint>();
		vector<idx_t> group;
		for (auto &logical : uc.GetLogicalIndexes(base.columns)) {
			group.push_back(logical.index);
		}
		if (uc.IsPrimaryKey()) {
			pk_indices = group;
		} else {
			unique_groups.push_back(std::move(group));
		}
	}
	// PRIMARY KEY columns must be NOT NULL in SQL Server.
	for (auto idx : pk_indices) {
		if (idx < nullable.size()) {
			nullable[idx] = false;
		}
	}

	auto join_indices = [](const vector<idx_t> &idxs) {
		string out;
		for (idx_t i = 0; i < idxs.size(); i++) {
			if (i > 0) {
				out += ",";
			}
			out += std::to_string(idxs[i]);
		}
		return out;
	};
	string pk_arg = join_indices(pk_indices);
	string unique_arg;
	for (auto &group : unique_groups) {
		if (!unique_arg.empty()) {
			unique_arg += ";";
		}
		unique_arg += join_indices(group);
	}

	// Literal column DEFAULTs: "<index> <payload>" pairs, payload = base64(value
	// text) or "-" for DEFAULT NULL. Non-literal defaults (expressions) are skipped.
	string defaults_arg;
	for (idx_t i = 0; i < names.size(); i++) {
		auto &col = base.columns.GetColumn(LogicalIndex(i));
		if (!col.HasDefaultValue()) {
			continue;
		}
		// Unwrap one CAST level (e.g. boolean literals parse as CAST(... AS BOOLEAN)).
		const ParsedExpression *expr = &col.DefaultValue();
		if (expr->type == ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<CastExpression>().child.get();
		}
		if (!expr || expr->type != ExpressionType::VALUE_CONSTANT) {
			continue; // literals only
		}
		auto &val = expr->Cast<ConstantExpression>().value;
		if (!defaults_arg.empty()) {
			defaults_arg += " ";
		}
		defaults_arg += std::to_string(i) + " ";
		if (val.IsNull()) {
			defaults_arg += "-";
		} else {
			string text = val.ToString();
			defaults_arg += Blob::ToBase64(string_t(text.c_str(), (uint32_t)text.size()));
		}
	}

	bool replace = base.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT;
	bool if_not_exists = base.on_conflict == OnCreateConflict::IGNORE_ON_CONFLICT;
	if (replace) {
		arrownet::DropTable(handle_, name, base.table, /*if_exists=*/true);
	}

	// The `mssql_ctas_text_type` setting overrides the SQL type for text columns
	// (default NVARCHAR(MAX)) — useful for indexable string keys.
	string text_type;
	Value text_type_value;
	if (context.TryGetCurrentSetting("mssql_ctas_text_type", text_type_value) && !text_type_value.IsNull()) {
		text_type = text_type_value.ToString();
	}

	// A schema-only Arrow stream carries the column definitions to the backend.
	arrownet::ArrowProducer producer(types, names, context.GetClientProperties());
	producer.SetNullability(nullable);
	producer.Finish();
	arrownet::CreateTable(handle_, name, base.table, *producer.Stream(), if_not_exists, pk_arg, unique_arg,
	                      defaults_arg, text_type);

	// Register the new table (also invalidates any cached entry) and return it.
	AddTable(base.table, "BASE TABLE");
	return GetOrCreateEntry(context, base.table);
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateFunction(CatalogTransaction, CreateFunctionInfo &) {
	ReadOnly("CREATE FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateIndex(CatalogTransaction, CreateIndexInfo &,
                                                            TableCatalogEntry &) {
	ReadOnly("CREATE INDEX");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateView(CatalogTransaction, CreateViewInfo &) {
	ReadOnly("CREATE VIEW");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateSequence(CatalogTransaction, CreateSequenceInfo &) {
	ReadOnly("CREATE SEQUENCE");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateTableFunction(CatalogTransaction, CreateTableFunctionInfo &) {
	ReadOnly("CREATE TABLE FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateCopyFunction(CatalogTransaction, CreateCopyFunctionInfo &) {
	ReadOnly("CREATE COPY FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreatePragmaFunction(CatalogTransaction, CreatePragmaFunctionInfo &) {
	ReadOnly("CREATE PRAGMA FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateCollation(CatalogTransaction, CreateCollationInfo &) {
	ReadOnly("CREATE COLLATION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateType(CatalogTransaction, CreateTypeInfo &) {
	ReadOnly("CREATE TYPE");
}
void ArrowNetSchemaEntry::DropEntry(ClientContext &context, DropInfo &info) {
	if (info.type != CatalogType::TABLE_ENTRY) {
		throw NotImplementedException("mssql_net: only DROP TABLE is supported yet (not %s)",
		                              CatalogTypeToString(info.type));
	}
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	arrownet::DropTable(handle_, name, info.name, if_exists);

	lock_guard<mutex> lock(entry_lock_);
	table_types_.erase(info.name);
	entries_.erase(info.name);
}
void ArrowNetSchemaEntry::Alter(CatalogTransaction transaction, AlterInfo &info) {
	if (info.type != AlterType::ALTER_TABLE) {
		throw NotImplementedException("mssql_net: only ALTER TABLE is supported");
	}
	if (!transaction.context) {
		throw InternalException("mssql_net: ALTER TABLE requires a client context");
	}
	auto &context = *transaction.context;
	auto &table_info = info.Cast<AlterTableInfo>();
	const string &table = table_info.name;

	// Drops the cached entry so the next lookup re-fetches columns/rowid.
	auto invalidate = [&](const string &t) {
		lock_guard<mutex> lock(entry_lock_);
		entries_.erase(t);
	};

	switch (table_info.alter_table_type) {
	case AlterTableType::RENAME_TABLE: {
		auto &rt = table_info.Cast<RenameTableInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_RENAME_TABLE, rt.new_table_name, "", nullptr, 0);
		lock_guard<mutex> lock(entry_lock_);
		auto it = table_types_.find(table);
		string type = it != table_types_.end() ? it->second : string("BASE TABLE");
		table_types_.erase(table);
		entries_.erase(table);
		table_types_[rt.new_table_name] = type;
		entries_.erase(rt.new_table_name);
		break;
	}
	case AlterTableType::RENAME_COLUMN: {
		auto &rc = table_info.Cast<RenameColumnInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_RENAME_COLUMN, rc.old_name, rc.new_name, nullptr, 0);
		invalidate(table);
		break;
	}
	case AlterTableType::ADD_COLUMN: {
		auto &ac = table_info.Cast<AddColumnInfo>();
		int32_t flags = ac.if_column_not_exists ? ARROWNET_ALTER_FLAG_IF_EXISTS : 0;
		// Carry the new column's type as a single-field zero-row Arrow stream.
		vector<LogicalType> types {ac.new_column.Type()};
		vector<string> names {ac.new_column.Name()};
		arrownet::ArrowProducer producer(types, names, context.GetClientProperties());
		producer.Finish();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_ADD_COLUMN, ac.new_column.Name(), "",
		                     producer.Stream(), flags);
		invalidate(table);
		break;
	}
	case AlterTableType::REMOVE_COLUMN: {
		auto &rc = table_info.Cast<RemoveColumnInfo>();
		int32_t flags = rc.if_column_exists ? ARROWNET_ALTER_FLAG_IF_EXISTS : 0;
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_DROP_COLUMN, rc.removed_column, "", nullptr, flags);
		invalidate(table);
		break;
	}
	case AlterTableType::ALTER_COLUMN_TYPE: {
		auto &ct = table_info.Cast<ChangeColumnTypeInfo>();
		vector<LogicalType> types {ct.target_type};
		vector<string> names {ct.column_name};
		arrownet::ArrowProducer producer(types, names, context.GetClientProperties());
		producer.Finish();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_COLUMN_TYPE, ct.column_name, "", producer.Stream(),
		                     0);
		invalidate(table);
		break;
	}
	case AlterTableType::SET_NOT_NULL: {
		auto &sn = table_info.Cast<SetNotNullInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_SET_NOT_NULL, sn.column_name, "", nullptr, 0);
		invalidate(table);
		break;
	}
	case AlterTableType::DROP_NOT_NULL: {
		auto &dn = table_info.Cast<DropNotNullInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_DROP_NOT_NULL, dn.column_name, "", nullptr, 0);
		invalidate(table);
		break;
	}
	case AlterTableType::SET_DEFAULT: {
		auto &sd = table_info.Cast<SetDefaultInfo>();
		if (!sd.expression) {
			// DROP DEFAULT (no expression).
			arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_DROP_DEFAULT, sd.column_name, "", nullptr, 0);
			invalidate(table);
			break;
		}
		// Only literal defaults: unwrap one CAST (booleans parse as CAST(... AS BOOLEAN)).
		const ParsedExpression *expr = sd.expression.get();
		if (expr->type == ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<CastExpression>().child.get();
		}
		if (!expr || expr->type != ExpressionType::VALUE_CONSTANT) {
			throw NotImplementedException("mssql_net: only literal column DEFAULTs are supported");
		}
		auto &val = expr->Cast<ConstantExpression>().value;
		// arg2: "-" for DEFAULT NULL, else "b"+base64(value-text) (the "b" keeps
		// it non-empty so empty-string literals survive the ABI).
		string arg2;
		if (val.IsNull()) {
			arg2 = "-";
		} else {
			string text = val.ToString();
			arg2 = "b" + Blob::ToBase64(string_t(text.c_str(), (uint32_t)text.size()));
		}
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_SET_DEFAULT, sd.column_name, arg2, nullptr, 0);
		invalidate(table);
		break;
	}
	default:
		throw NotImplementedException("mssql_net: this ALTER TABLE variant is not supported yet");
	}
}

} // namespace duckdb
