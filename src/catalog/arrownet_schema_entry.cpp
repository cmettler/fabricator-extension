//===----------------------------------------------------------------------===//
//                         arrownet — schema catalog entry (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_schema_entry.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/common/enums/operator_result_type.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/blob.hpp"
#include "duckdb/common/vector_operations/vector_operations.hpp"
#include "duckdb/execution/execution_context.hpp"
#include "duckdb/execution/expression_executor_state.hpp"
#include "duckdb/execution/physical_operator.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/function/function_set.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/planner/operator/logical_extension_operator.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
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
	// A discovered TVF or stored proc also gets a synthetic table-in-out alias `<name>_each` that applies
	// it once per input row (4g): a TVF via SQL-Server CROSS APPLY, a proc via per-row EXEC (the managed
	// side picks by object kind). Both echo the input columns + the function's output columns; a proc's
	// per-row EXECs run in DuckDB's transaction (commit/rollback driven by DuckDB).
	string each = func_name + "_each";
	inout_functions_[each] = func_name;
	table_function_entries_.erase(each);
}

void ArrowNetSchemaEntry::AddInOutFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	custom_inout_functions_.insert(func_name);
	table_function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::ClearTables() {
	lock_guard<mutex> lock(entry_lock_);
	table_types_.clear();
	entries_.clear();
	scalar_functions_.clear();
	function_entries_.clear();
	table_functions_.clear();
	inout_functions_.clear();
	custom_inout_functions_.clear();
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
	bool is_proc = false;    // stored procedure (EXEC, no pushdown) vs TVF (FROM, pushdown)
	string attach_isolation; // ATTACH isolation_level default for this catalog (in-out only; empty => none)
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

// -----------------------------------------------------------------------------
// Table-in-out (4g): a discovered TVF's `{LogicalType::TABLE}` overload — applies the
// function once per input row via SQL-Server CROSS APPLY (the T-SQL is generated +
// executed in C#; see SqlServerCatalog.InOutSessionImpl). Output = the input parameter
// columns ++ the TVF's output columns. Read-only (a SQL Server TVF can't modify data).
//
// OUTPUT IS SYNCHRONOUS PER CHUNK: each in_out_function call pushes one input chunk and the
// managed side runs THAT chunk's CROSS APPLY to completion, returning its full output — so
// there is no lagging "tail" produced after the last input. This is what makes the design
// robust: emitting rows never depends on detecting which parallel input branch finishes
// last (`in_out_function_final` fires per branch, and the union branch pipelines may even run
// sequentially — see PhysicalUnion::BuildPipelines — so a per-branch "last" detector is
// unreliable). With no tail there is nothing to emit at the end, so there is no
// `in_out_function_final` at all.
//
// Session lifecycle lives in a refcounted InOutSessionHolder carried on the bind data, so the
// injected OperatorFinalize (Phase: per-row procs) can reach the same session to signal a clean
// finish/COMMIT. The holder's destructor calls inout_abort on every teardown path (frees the
// managed handle + rolls back/releases) — the reliable RAII backstop for normal/LIMIT/error/cancel.
struct InOutSessionHolder {
	ArrowNetHandle session = nullptr;
	bool finished = false; // a clean finish (OperatorFinalize) was signalled
	mutex lock;

	// Clean finish / commit signal (no rows in the synchronous model). Idempotent. Used by the
	// injected OperatorFinalize (built with per-row stored procs); unused for read-only TVFs.
	void Finish() {
		lock_guard<mutex> guard(lock);
		if (!session || finished) {
			return;
		}
		finished = true;
		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		arrownet::InOutFinish(session, out);
		if (out.release) {
			out.release(&out); // synchronous model: finish carries no tail rows
		}
	}

	~InOutSessionHolder() {
		// inout_abort releases the managed session AND frees the GCHandle (inout_finish does NOT
		// free it), so call it on every path. Idempotent + best-effort (swallows errors): after a
		// clean Finish it is a no-op release that just frees the handle.
		arrownet::InOutAbort(session);
	}
};

struct ArrowNetInOutBindData : public TableFunctionData {
	ArrowNetHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> input_types; // input parameter-table columns (= the TVF's positional params)
	vector<string> input_names;
	//! Effective SQL transaction isolation level for the session (SET mssql_isolation_level ?? the ATTACH
	//! isolation_level default ?? empty). Resolved at bind; passed to inout_open. Gives a consistent view
	//! across the per-chunk queries of one in-out call.
	string isolation;
	//! Per-execution managed session, opened in init_global, pushed by in_out_function, finished by the
	//! injected OperatorFinalize / released by the holder destructor. shared_ptr so the injected operator
	//! can hold the same session.
	shared_ptr<InOutSessionHolder> session_holder;
};

// Resolves the effective isolation level for an in-out session: the `mssql_isolation_level` session
// setting if set, else the catalog's ATTACH `isolation_level` default, else empty (provider default).
string ResolveInOutIsolation(ClientContext &context, const string &attach_isolation) {
	Value setting;
	if (context.TryGetCurrentSetting("mssql_isolation_level", setting) && !setting.IsNull()) {
		string s = setting.ToString();
		if (!s.empty()) {
			return s;
		}
	}
	return attach_isolation;
}

struct ArrowNetInOutGlobalState : public GlobalTableFunctionState {
	idx_t MaxThreads() const override {
		return 1; // the operator consumes one Arrow C output stream at a time
	}
};

struct ArrowNetInOutLocalState : public LocalTableFunctionState {
	bool pushed_current = false;                    // pushed the current input chunk yet?
	unique_ptr<arrownet::ArrowStreamReader> reader; // output reader for the current input chunk
};

// Append the function's output columns (read from a zero-row get_function_output_schema stream) to
// return_types/names — shared by the discovered-TVF `_each` bind and the custom-in-out bind.
void AppendInOutOutputSchema(ClientContext &context, ArrowNetHandle handle, const string &schema, const string &func,
                            vector<LogicalType> &return_types, vector<string> &names) {
	ArrowArrayStream out_schema;
	std::memset(&out_schema, 0, sizeof(out_schema));
	arrownet::GetFunctionOutputSchema(handle, schema, func, out_schema);
	ArrowSchemaWrapper schema_root;
	if (out_schema.get_schema(&out_schema, &schema_root.arrow_schema) != 0) {
		const char *msg = out_schema.get_last_error ? out_schema.get_last_error(&out_schema) : nullptr;
		if (out_schema.release) {
			out_schema.release(&out_schema);
		}
		throw IOException(string("mssql_net: failed to read table-in-out output schema") +
		                  (msg ? string(": ") + msg : string()));
	}
	if (out_schema.release) {
		out_schema.release(&out_schema);
	}
	ArrowTableSchema arrow_table;
	ArrowTableFunction::PopulateArrowTableSchema(context, arrow_table, schema_root.arrow_schema);
	for (int64_t i = 0; i < schema_root.arrow_schema.n_children; i++) {
		auto &child = *schema_root.arrow_schema.children[i];
		names.push_back(child.name ? string(child.name) : "column" + to_string(i));
		return_types.push_back(arrow_table.GetColumns().at((idx_t)i)->GetDuckType());
	}
}

// Bind the discovered-TVF `_each` overload: output schema = input parameter-table columns ++ the TVF's
// output columns (resolved from metadata, without executing the function).
unique_ptr<FunctionData> ArrowNetInOutBind(ClientContext &context, TableFunctionBindInput &input,
                                           vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<ArrowNetTableFunctionInfo>();
	if (input.input_table_types.size() != info.arg_types.size()) {
		throw BinderException(
		    "mssql_net: table-in-out function \"%s\" takes %llu parameter(s) but the input table has %llu column(s)",
		    info.func, (idx_t)info.arg_types.size(), (idx_t)input.input_table_types.size());
	}

	auto bind_data = make_uniq<ArrowNetInOutBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->isolation = ResolveInOutIsolation(context, info.attach_isolation);
	bind_data->session_holder = make_shared_ptr<InOutSessionHolder>();

	// 1) The input parameter columns lead the output (p.* in the CROSS APPLY). C# casts the
	//    VALUES to the TVF's parameter types, so the echoed columns come back typed as the
	//    PARAMETERS (info.arg_types) — not necessarily the input-table types. bind_data->input_*
	//    keep the actual input-table types: that's what we marshal/push (and pass to inout_open).
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		return_types.push_back(info.arg_types[i]);
		names.push_back(input.input_table_names[i]);
		bind_data->input_types.push_back(input.input_table_types[i]);
		bind_data->input_names.push_back(input.input_table_names[i]);
	}

	// 2) Then the TVF's own output columns (f.*).
	AppendInOutOutputSchema(context, info.handle, info.schema, info.func, return_types, names);
	return std::move(bind_data);
}

// Bind a custom (provider-authored) table-in-out: output schema = the function's FULL declared output
// (no input echo, unlike the `_each` alias above). The input table's columns are marshalled as-is and
// validated against the function's expected input in C#.
unique_ptr<FunctionData> ArrowNetCustomInOutBind(ClientContext &context, TableFunctionBindInput &input,
                                                 vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<ArrowNetTableFunctionInfo>();
	auto bind_data = make_uniq<ArrowNetInOutBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->isolation = ResolveInOutIsolation(context, info.attach_isolation);
	bind_data->session_holder = make_shared_ptr<InOutSessionHolder>();
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		bind_data->input_types.push_back(input.input_table_types[i]);
		bind_data->input_names.push_back(input.input_table_names[i]);
	}
	AppendInOutOutputSchema(context, info.handle, info.schema, info.func, return_types, names);
	return std::move(bind_data);
}

unique_ptr<GlobalTableFunctionState> ArrowNetInOutInitGlobal(ClientContext &context, TableFunctionInitInput &input) {
	auto &bind = input.bind_data->Cast<ArrowNetInOutBindData>();
	auto &holder = *bind.session_holder;
	// Build the input table's Arrow schema (its columns are the TVF's positional params) and open the
	// managed session into the holder (one per execution). C# consumes/releases the schema struct.
	ArrowSchema input_schema;
	std::memset(&input_schema, 0, sizeof(input_schema));
	auto props = context.GetClientProperties();
	ArrowConverter::ToArrowSchema(&input_schema, bind.input_types, bind.input_names, props);
	lock_guard<mutex> guard(holder.lock);
	if (holder.session) {
		// Re-execution of a cached plan: release the previous session before opening a new one.
		arrownet::InOutAbort(holder.session);
		holder.session = nullptr;
	}
	holder.finished = false;
	holder.session = arrownet::InOutOpen(bind.handle, bind.schema, bind.func, input_schema, bind.isolation);
	return make_uniq<ArrowNetInOutGlobalState>();
}

unique_ptr<LocalTableFunctionState> ArrowNetInOutInitLocal(ExecutionContext &, TableFunctionInitInput &,
                                                           GlobalTableFunctionState *) {
	return make_uniq<ArrowNetInOutLocalState>();
}

// Per input chunk: push it to the session — the managed side runs THAT chunk's CROSS APPLY to
// completion and returns its full output (synchronous, no lagging tail). HAVE_MORE_OUTPUT drains the
// chunk's output across re-calls (same input); NEED_MORE_INPUT advances to the next chunk. Parallel
// branches push into the one session concurrently; the managed Push serializes them.
OperatorResultType ArrowNetInOutFunction(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                         DataChunk &output) {
	auto &bind = data.bind_data->Cast<ArrowNetInOutBindData>();
	auto &l = data.local_state->Cast<ArrowNetInOutLocalState>();

	if (!l.pushed_current) {
		if (input.size() == 0) {
			output.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		// Marshal the input chunk -> a one-batch Arrow array (columns in param order); the
		// managed side imports + releases it, so we never release `array` ourselves.
		auto props = context.client.GetClientProperties();
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context.client, bind.input_types);
		ArrowAppender appender(bind.input_types, input.size(), props, extension_types);
		appender.Append(input, 0, input.size(), input.size());
		ArrowArray array = appender.Finalize();

		ArrowArrayStream ready;
		std::memset(&ready, 0, sizeof(ready));
		arrownet::InOutPush(bind.session_holder->session, array, ready);
		l.reader = make_uniq<arrownet::ArrowStreamReader>(context.client, ready);
		l.pushed_current = true;
	}

	l.reader->Read(output);
	if (output.size() == 0) {
		// This chunk's output is exhausted — fetch the next input chunk.
		l.reader.reset();
		l.pushed_current = false;
		return OperatorResultType::NEED_MORE_INPUT;
	}
	return OperatorResultType::HAVE_MORE_OUTPUT;
}

// -----------------------------------------------------------------------------
// Table-in-out OperatorFinalize (4g): a reliable single "all input consumed" signal to the managed
// session, for resource cleanup (and a clean commit of a read-only TVF's snapshot transaction — NOT the
// per-row-proc commit, which DuckDB's transaction manager drives). DuckDB exposes no row-less finalize on
// the in-out TableFunction itself, so an OptimizerExtension wraps the in-out's LogicalGet in this
// pass-through LogicalExtensionOperator; its PhysicalOperator forwards rows unchanged and calls
// holder->Finish() in OperatorFinalize, which fires once (sink-level) after every input branch is drained.
class ArrowNetInOutFinalizePhysical : public PhysicalOperator {
public:
	ArrowNetInOutFinalizePhysical(PhysicalPlan &physical_plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                              shared_ptr<InOutSessionHolder> holder)
	    : PhysicalOperator(physical_plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
	      holder(std::move(holder)) {
	}

	shared_ptr<InOutSessionHolder> holder;

	string GetName() const override {
		return "ARROWNET_INOUT_FINALIZE";
	}

	// Pass-through: forward the child's chunk unchanged.
	OperatorResultType Execute(ExecutionContext &, DataChunk &input, DataChunk &chunk, GlobalOperatorState &,
	                           OperatorState &) const override {
		chunk.Reference(input);
		return OperatorResultType::NEED_MORE_INPUT;
	}

	bool ParallelOperator() const override {
		return true;
	}

	bool RequiresOperatorFinalize() const override {
		return true;
	}

	OperatorFinalResultType OperatorFinalize(Pipeline &, Event &, ClientContext &,
	                                         OperatorFinalizeInput &) const override {
		holder->Finish(); // single all-input-done signal (idempotent); resource cleanup / read-only commit
		return OperatorFinalResultType::FINISHED;
	}
};

struct ArrowNetInOutFinalizeOperator : public LogicalExtensionOperator {
	explicit ArrowNetInOutFinalizeOperator(unique_ptr<LogicalOperator> child, shared_ptr<InOutSessionHolder> holder)
	    : holder(std::move(holder)) {
		children.push_back(std::move(child));
	}

	shared_ptr<InOutSessionHolder> holder;

	PhysicalOperator &CreatePlan(ClientContext &, PhysicalPlanGenerator &planner) override {
		auto &child_plan = planner.CreatePlan(*children[0]);
		auto &op = planner.Make<ArrowNetInOutFinalizePhysical>(children[0]->types, children[0]->estimated_cardinality,
		                                                       holder);
		op.children.push_back(child_plan);
		return op;
	}

	vector<ColumnBinding> GetColumnBindings() override {
		return children[0]->GetColumnBindings(); // pass-through
	}

	void ResolveTypes() override {
		types = children[0]->types;
	}

	string GetExtensionName() const override {
		return "arrownet_inout_finalize";
	}
};

// Recursively wrap every ArrowNet table-in-out LogicalGet in a finalize operator.
void WrapArrowNetInOutNodes(unique_ptr<LogicalOperator> &op) {
	for (auto &child : op->children) {
		WrapArrowNetInOutNodes(child);
	}
	if (op->type != LogicalOperatorType::LOGICAL_GET) {
		return;
	}
	auto &get = op->Cast<LogicalGet>();
	// A table-in-out is a LogicalGet with input children + our in_out_function; identify it by the
	// function pointer (no RTTI), then recover the session holder from its bind data.
	if (get.children.empty() || get.function.in_out_function != ArrowNetInOutFunction || !get.bind_data) {
		return;
	}
	auto holder = get.bind_data->Cast<ArrowNetInOutBindData>().session_holder;
	if (!holder) {
		return;
	}
	op = make_uniq<ArrowNetInOutFinalizeOperator>(std::move(op), std::move(holder));
}

void ArrowNetInOutOptimize(OptimizerExtensionInput &, unique_ptr<LogicalOperator> &plan) {
	WrapArrowNetInOutNodes(plan);
}

} // namespace

void RegisterArrowNetInOutFinalizer(DBConfig &config) {
	OptimizerExtension extension;
	extension.optimize_function = ArrowNetInOutOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateTableFunction(ClientContext &context,
                                                                         const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = table_function_entries_.find(func_name);
	if (cached != table_function_entries_.end()) {
		return cached->second.get();
	}
	auto kind_it = table_functions_.find(func_name);
	if (kind_it == table_functions_.end()) {
		// A provider-authored custom table-in-out (4g) is registered under the bare name.
		if (custom_inout_functions_.find(func_name) != custom_inout_functions_.end()) {
			return GetOrCreateCustomInOutFunction(context, func_name);
		}
		// Else maybe the synthetic in-out alias `<base>_each` (a real same-named function would
		// have matched above, so it wins over the alias).
		auto each_it = inout_functions_.find(func_name);
		if (each_it != inout_functions_.end()) {
			return GetOrCreateInOutFunction(context, func_name, each_it->second);
		}
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

// Build the in-out catalog entry for the synthetic alias `<base>_each` — a `{LogicalType::TABLE}`
// table function that applies the discovered TVF `base_func` once per input row (4g). DuckDB
// forbids a TABLE-parameter overload from coexisting with the scalar-arg scan form under one name
// (bind_table_function.cpp), so the in-out form is exposed as a sibling entry under its own name;
// the scan form keeps the bare TVF name. Caller holds entry_lock_.
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateInOutFunction(ClientContext &context,
                                                                         const string &each_name,
                                                                         const string &base_func) {
	vector<string> arg_names;
	vector<LogicalType> arg_types;
	try {
		FetchFunctionParamSchema(context, handle_, name, base_func, arg_names, arg_types);
	} catch (std::exception &) {
		// Stale discovery (base TVF dropped out-of-band) — treat as not-found, evicting both
		// the alias and the base so the next lookup re-discovers.
		inout_functions_.erase(each_name);
		table_function_entries_.erase(each_name);
		table_functions_.erase(base_func);
		table_function_entries_.erase(base_func);
		return nullptr;
	}
	if (arg_types.empty()) {
		return nullptr; // a no-arg TVF has nothing to apply per input row
	}

	TableFunction inout(each_name, {LogicalType::TABLE}, nullptr, ArrowNetInOutBind, ArrowNetInOutInitGlobal,
	                    ArrowNetInOutInitLocal);
	inout.in_out_function = ArrowNetInOutFunction;
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = base_func; // the CROSS APPLY target is the real SQL Server TVF, not the alias
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->is_proc = false;
	fn_info->attach_isolation = catalog.Cast<ArrowNetCatalog>().GetIsolationLevel();
	inout.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(inout));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[each_name] = std::move(entry);
	return &ref;
}

// Build the catalog entry for a provider-authored custom table-in-out (4g) — a `{LogicalType::TABLE}`
// table function under the bare name, dispatched to C# (no SQL object, no scalar-arg scan form). Reuses
// the same operator callbacks as the `_each` path; only the bind differs (full output schema, no input
// echo). Caller holds entry_lock_.
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateCustomInOutFunction(ClientContext &context,
                                                                              const string &func_name) {
	TableFunction inout(func_name, {LogicalType::TABLE}, nullptr, ArrowNetCustomInOutBind, ArrowNetInOutInitGlobal,
	                    ArrowNetInOutInitLocal);
	inout.in_out_function = ArrowNetInOutFunction;
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->is_proc = false;
	fn_info->attach_isolation = catalog.Cast<ArrowNetCatalog>().GetIsolationLevel();
	inout.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(inout));
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
			// The synthetic `<name>_each` table-in-out aliases + custom in-out functions are catalog
			// functions too.
			for (auto &fn : inout_functions_) {
				names.push_back(fn.first);
			}
			for (auto &fn : custom_inout_functions_) {
				names.push_back(fn);
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
