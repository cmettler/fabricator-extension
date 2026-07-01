//===----------------------------------------------------------------------===//
//                         mssql_net — COPY TO (impl)
//===----------------------------------------------------------------------===//

#include "copy/mssql_net_copy.hpp"

#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "catalog/arrownet_schema_entry.hpp"
#include "duckdb/catalog/catalog.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/value.hpp"
#include "duckdb/function/copy_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/transaction/meta_transaction.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"

#include <cstring>

namespace duckdb {

struct ArrowNetCopyBindData : public FunctionData {
	ArrowNetHandle handle = nullptr;
	string catalog_name;
	string schema_name;
	string table_name;
	vector<string> column_names;
	vector<LogicalType> column_types;
	bool create_table = true;
	bool replace = false;
	string schema_mode; // SCHEMA_MODE COPY option: "" | "merge" | "overwrite" (Delta provider)

	unique_ptr<FunctionData> Copy() const override {
		auto result = make_uniq<ArrowNetCopyBindData>();
		*result = *this;
		return std::move(result);
	}
	bool Equals(const FunctionData &other_p) const override {
		auto &other = other_p.Cast<ArrowNetCopyBindData>();
		return handle == other.handle && schema_name == other.schema_name && table_name == other.table_name;
	}
};

struct ArrowNetCopyGlobalState : public GlobalFunctionData {
	ArrowNetCopyGlobalState(ClientContext &context, const ArrowNetCopyBindData &bind_data)
	    : properties(arrownet::BoundaryClientProperties(context)),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, bind_data.column_types)) {
		// The producer also builds the Arrow schema handed to begin_bulk.
		producer = make_uniq<arrownet::ArrowProducer>(bind_data.column_types, bind_data.column_names, properties);
	}
	~ArrowNetCopyGlobalState() override {
		// Cancel the background load if the query failed before Finalize (best-effort).
		if (bulk_session && !bulk_completed) {
			try {
				arrownet::CompleteBulk(bulk_session, /*abort=*/true);
			} catch (...) {
			}
		}
	}
	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<arrownet::ArrowProducer> producer;
	//! Streaming bulk-load session.
	ArrowNetHandle bulk_session = nullptr;
	bool bulk_completed = false;
	mutex lock;
};

struct ArrowNetCopyLocalState : public LocalFunctionData {};

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

// A string COPY option (e.g. SCHEMA_MODE 'merge'|'overwrite'); empty when absent.
static string GetStringOption(const case_insensitive_map_t<vector<Value>> &options, const string &key) {
	auto it = options.find(key);
	if (it == options.end() || it->second.empty() || it->second[0].IsNull()) {
		return string();
	}
	return it->second[0].ToString();
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
	auto bind_data = make_uniq<ArrowNetCopyBindData>();
	ParseTarget(input.info.file_path, bind_data->catalog_name, bind_data->schema_name, bind_data->table_name);

	auto &catalog = Catalog::GetCatalog(context, bind_data->catalog_name);
	if (!ArrowNetCatalog::Is(catalog)) {
		throw BinderException("mssql_net COPY: catalog '%s' is not an mssql_net catalog", bind_data->catalog_name);
	}
	bind_data->handle = catalog.Cast<ArrowNetCatalog>().GetHandle();
	bind_data->column_names = names;
	bind_data->column_types = sql_types;
	bind_data->create_table = GetBoolOption(input.info.options, "CREATE_TABLE", true);
	bind_data->replace = GetBoolOption(input.info.options, "REPLACE", false);
	bind_data->schema_mode = GetStringOption(input.info.options, "SCHEMA_MODE");
	return std::move(bind_data);
}

static unique_ptr<GlobalFunctionData> CopyToInitGlobal(ClientContext &context, FunctionData &bind_data_p,
                                                       const string &file_path) {
	auto &bind_data = bind_data_p.Cast<ArrowNetCopyBindData>();
	auto gstate = make_uniq<ArrowNetCopyGlobalState>(context, bind_data);
	// Stream rows to the provider as they are sinked (bounded memory). The target is
	// created at begin per CREATE_TABLE/REPLACE, then rows stream in.
	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	auto *stream = gstate->producer->Stream();
	stream->get_schema(stream, &schema);
	arrownet::SetActiveOpener(reinterpret_cast<ArrowNetHandle>(&context)); // host-FS opener for a Delta-catalog COPY
	gstate->bulk_session = arrownet::BeginBulk(bind_data.handle, bind_data.schema_name, bind_data.table_name,
	                                           bind_data.create_table, bind_data.replace, /*check_constraints=*/false,
	                                           (int64_t)context.ActiveTransaction().global_transaction_id, schema,
	                                           /*partition_columns=*/"", /*sort_columns=*/"", bind_data.schema_mode);
	return std::move(gstate);
}

static unique_ptr<LocalFunctionData> CopyToInitLocal(ExecutionContext &context, FunctionData &bind_data) {
	return make_uniq<ArrowNetCopyLocalState>();
}

static void CopyToSink(ExecutionContext &context, FunctionData &bind_data_p, GlobalFunctionData &gstate_p,
                       LocalFunctionData &lstate_p, DataChunk &input) {
	auto &bind_data = bind_data_p.Cast<ArrowNetCopyBindData>();
	auto &gstate = gstate_p.Cast<ArrowNetCopyGlobalState>();
	if (input.size() == 0) {
		return;
	}
	ArrowAppender appender(bind_data.column_types, input.size(), gstate.properties, gstate.extension_types);
	appender.Append(input, 0, input.size(), input.size());
	ArrowArray array = appender.Finalize();
	// Stream the batch to the provider; PushBatch blocks for backpressure when the
	// channel is full. COPY runs serially, so no lock is needed.
	arrownet::PushBatch(gstate.bulk_session, array);
}

static void CopyToCombine(ExecutionContext &context, FunctionData &bind_data, GlobalFunctionData &gstate,
                          LocalFunctionData &lstate) {
}

static void CopyToFinalize(ClientContext &context, FunctionData &bind_data_p, GlobalFunctionData &gstate_p) {
	auto &bind_data = bind_data_p.Cast<ArrowNetCopyBindData>();
	auto &gstate = gstate_p.Cast<ArrowNetCopyGlobalState>();
	// Signal end-of-stream and wait for the background load to drain.
	arrownet::CompleteBulk(gstate.bulk_session, /*abort=*/false);
	gstate.bulk_completed = true;
	gstate.bulk_session = nullptr;

	// Register the target in the attached catalog so it's queryable immediately
	// (also invalidates any stale cached entry, e.g. for CREATE_TABLE/REPLACE).
	auto &catalog = Catalog::GetCatalog(context, bind_data.catalog_name);
	if (ArrowNetCatalog::Is(catalog)) {
		auto schema = catalog.GetSchema(context, bind_data.schema_name, OnEntryNotFound::RETURN_NULL);
		if (schema) {
			schema->Cast<ArrowNetSchemaEntry>().AddTable(bind_data.table_name, "BASE TABLE");
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
