//===----------------------------------------------------------------------===//
//                         mssql_net — COPY TO (impl)
//===----------------------------------------------------------------------===//

#include "copy/mssql_net_copy.hpp"

#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/mssql_net_catalog.hpp"
#include "catalog/mssql_net_schema_entry.hpp"
#include "duckdb/catalog/catalog.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/value.hpp"
#include "duckdb/function/copy_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"

namespace duckdb {

struct MssqlNetCopyBindData : public FunctionData {
	ArrowNetHandle handle = nullptr;
	string catalog_name;
	string schema_name;
	string table_name;
	vector<string> column_names;
	vector<LogicalType> column_types;
	bool create_table = true;
	bool replace = false;

	unique_ptr<FunctionData> Copy() const override {
		auto result = make_uniq<MssqlNetCopyBindData>();
		*result = *this;
		return std::move(result);
	}
	bool Equals(const FunctionData &other_p) const override {
		auto &other = other_p.Cast<MssqlNetCopyBindData>();
		return handle == other.handle && schema_name == other.schema_name && table_name == other.table_name;
	}
};

struct MssqlNetCopyGlobalState : public GlobalFunctionData {
	MssqlNetCopyGlobalState(ClientContext &context, const MssqlNetCopyBindData &bind_data)
	    : properties(context.GetClientProperties()),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, bind_data.column_types)) {
		producer = make_uniq<arrownet::ArrowProducer>(bind_data.column_types, bind_data.column_names, properties);
	}
	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<arrownet::ArrowProducer> producer;
	mutex lock;
};

struct MssqlNetCopyLocalState : public LocalFunctionData {};

static bool GetBoolOption(const case_insensitive_map_t<vector<Value>> &options, const string &key, bool fallback) {
	auto it = options.find(key);
	if (it == options.end() || it->second.empty()) {
		return fallback;
	}
	auto &value = it->second[0];
	if (value.type().id() == LogicalTypeId::BOOLEAN) {
		return value.GetValue<bool>();
	}
	auto text = StringUtil::Lower(value.ToString());
	return text == "true" || text == "1" || text == "yes";
}

// Parses 'mssql://catalog/schema/table' or 'catalog.schema.table'.
static void ParseTarget(const string &path, string &catalog, string &schema, string &table) {
	string body = path;
	if (StringUtil::StartsWith(StringUtil::Lower(body), "mssql://")) {
		body = body.substr(8);
		auto parts = StringUtil::Split(body, "/");
		if (parts.size() == 3) {
			catalog = parts[0];
			schema = parts[1];
			table = parts[2];
			return;
		}
	} else {
		auto parts = StringUtil::Split(body, ".");
		if (parts.size() == 3) {
			catalog = parts[0];
			schema = parts[1];
			table = parts[2];
			return;
		}
	}
	throw BinderException("mssql_net COPY: target must be 'mssql://catalog/schema/table' or "
	                      "'catalog.schema.table' (got '%s')",
	                      path);
}

static unique_ptr<FunctionData> CopyToBind(ClientContext &context, CopyFunctionBindInput &input,
                                           const vector<string> &names, const vector<LogicalType> &sql_types) {
	auto bind_data = make_uniq<MssqlNetCopyBindData>();
	ParseTarget(input.info.file_path, bind_data->catalog_name, bind_data->schema_name, bind_data->table_name);

	auto &catalog = Catalog::GetCatalog(context, bind_data->catalog_name);
	if (catalog.GetCatalogType() != "mssql_net") {
		throw BinderException("mssql_net COPY: catalog '%s' is not an mssql_net catalog", bind_data->catalog_name);
	}
	bind_data->handle = catalog.Cast<MssqlNetCatalog>().GetHandle();
	bind_data->column_names = names;
	bind_data->column_types = sql_types;
	bind_data->create_table = GetBoolOption(input.info.options, "CREATE_TABLE", true);
	bind_data->replace = GetBoolOption(input.info.options, "REPLACE", false);
	return std::move(bind_data);
}

static unique_ptr<GlobalFunctionData> CopyToInitGlobal(ClientContext &context, FunctionData &bind_data,
                                                       const string &file_path) {
	return make_uniq<MssqlNetCopyGlobalState>(context, bind_data.Cast<MssqlNetCopyBindData>());
}

static unique_ptr<LocalFunctionData> CopyToInitLocal(ExecutionContext &context, FunctionData &bind_data) {
	return make_uniq<MssqlNetCopyLocalState>();
}

static void CopyToSink(ExecutionContext &context, FunctionData &bind_data_p, GlobalFunctionData &gstate_p,
                       LocalFunctionData &lstate_p, DataChunk &input) {
	auto &bind_data = bind_data_p.Cast<MssqlNetCopyBindData>();
	auto &gstate = gstate_p.Cast<MssqlNetCopyGlobalState>();
	if (input.size() == 0) {
		return;
	}
	ArrowAppender appender(bind_data.column_types, input.size(), gstate.properties, gstate.extension_types);
	appender.Append(input, 0, input.size(), input.size());
	ArrowArray array = appender.Finalize();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->AddBatch(array);
}

static void CopyToCombine(ExecutionContext &context, FunctionData &bind_data, GlobalFunctionData &gstate,
                          LocalFunctionData &lstate) {
}

static void CopyToFinalize(ClientContext &context, FunctionData &bind_data_p, GlobalFunctionData &gstate_p) {
	auto &bind_data = bind_data_p.Cast<MssqlNetCopyBindData>();
	auto &gstate = gstate_p.Cast<MssqlNetCopyGlobalState>();
	lock_guard<mutex> guard(gstate.lock);
	gstate.producer->Finish();
	arrownet::BulkInsert(bind_data.handle, bind_data.schema_name, bind_data.table_name, bind_data.create_table,
	                     bind_data.replace, *gstate.producer->Stream());

	// Register the target in the attached catalog so it's queryable immediately
	// (also invalidates any stale cached entry, e.g. for CREATE_TABLE/REPLACE).
	auto &catalog = Catalog::GetCatalog(context, bind_data.catalog_name);
	if (catalog.GetCatalogType() == "mssql_net") {
		auto schema = catalog.GetSchema(context, bind_data.schema_name, OnEntryNotFound::RETURN_NULL);
		if (schema) {
			schema->Cast<MssqlNetSchemaEntry>().AddTable(bind_data.table_name, "BASE TABLE");
		}
	}
}

void RegisterMssqlNetCopyFunction(ExtensionLoader &loader) {
	// Register under both names: "mssql_net" (native) and "bcp" (compatibility
	// with the C++ mssql extension's `COPY ... (FORMAT 'bcp')`). Both route to the
	// same Arrow -> SqlBulkCopy bulk path.
	for (const char *fmt : {"mssql_net", "bcp"}) {
		CopyFunction function(fmt);
		function.copy_to_bind = CopyToBind;
		function.copy_to_initialize_global = CopyToInitGlobal;
		function.copy_to_initialize_local = CopyToInitLocal;
		function.copy_to_sink = CopyToSink;
		function.copy_to_combine = CopyToCombine;
		function.copy_to_finalize = CopyToFinalize;
		function.extension = "mssql_net";
		loader.RegisterFunction(function);
	}
}

} // namespace duckdb
