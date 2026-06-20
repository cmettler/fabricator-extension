//===----------------------------------------------------------------------===//
//                         DuckDB mssql_net Extension (entry)
//===----------------------------------------------------------------------===//

#include "mssql_net_extension.hpp"

// arrow_ingest pulls DuckDB's Arrow C headers first, then abi.h — keep it ahead
// of clr_host so the project agrees on one ArrowSchema/ArrowArrayStream layout.
#include "arrownet/arrow_ingest.hpp"

#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "copy/mssql_net_copy.hpp"
#include "arrownet_optimizer.hpp"
#include "mssql_net_secret.hpp"
#include "mssql_net_storage.hpp"
#include "duckdb/function/function_set.hpp"
#include "duckdb/function/scalar_function.hpp"
#include "duckdb/main/attached_database.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/database_manager.hpp"
#include "duckdb/main/extension/extension_loader.hpp"

namespace duckdb {

// Resolves the first argument of mssql_net_query / mssql_net_exec to a backend
// connection. If it names an attached `mssql_net` catalog, the caller BORROWS
// that catalog's handle (owns=false, must not close it); otherwise the argument
// is treated as a connection string and a fresh handle is opened (owns=true).
// This mirrors the C++ mssql extension's mssql_scan/mssql_exec ergonomics.
static ArrowNetHandle ResolveConnection(ClientContext &context, const string &conn_or_name, bool &owns) {
	auto db = DatabaseManager::Get(context).GetDatabase(context, conn_or_name);
	if (db) {
		auto &catalog = db->GetCatalog();
		if (catalog.GetCatalogType() == "mssql_net") {
			// Ensure our catalog's transaction is started for this context, so a
			// write via mssql_net_exec/query joins the active DuckDB transaction
			// (mssql_net_exec grabs the handle directly, bypassing the binder).
			catalog.GetCatalogTransaction(context);
			owns = false;
			return catalog.Cast<ArrowNetCatalog>().GetHandle();
		}
	}
	owns = true;
	// A bare name may also be an mssql_net secret — build a connection string from it.
	if (IsMssqlNetSecret(context, conn_or_name)) {
		return arrownet::OpenCatalog(BuildConnectionStringFromSecret(context, conn_or_name));
	}
	// A value with no connection-string markers ('=' key/value pairs or a 'scheme://'
	// URI) was meant as a context NAME — an attached catalog or a secret — which we
	// just failed to resolve. Fail with a clear error instead of handing a bare token
	// to the driver (which would surface an opaque network error).
	if (conn_or_name.find('=') == string::npos && conn_or_name.find("://") == string::npos) {
		throw BinderException("Unknown context '%s' (not an attached mssql_net catalog or secret)", conn_or_name);
	}
	return arrownet::OpenCatalog(conn_or_name);
}

static const char *GetExtensionVersion() {
#ifdef MSSQL_NET_VERSION
	return MSSQL_NET_VERSION;
#else
	return "0.0.1-dev";
#endif
}

// --- arrownet_version() / mssql_version() ------------------------------------
static void ArrowNetVersionFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	result.SetVectorType(VectorType::CONSTANT_VECTOR);
	ConstantVector::GetData<string_t>(result)[0] = StringVector::AddString(result, GetExtensionVersion());
}

// SET callback that rejects values < 1 (matches the C++ mssql extension's
// "must be >= 1" validation on batch-size / SQL-byte knobs).
static void RequireAtLeastOne(ClientContext &, SetScope, Value &parameter) {
	if (!parameter.IsNull() && parameter.GetValue<int64_t>() < 1) {
		throw InvalidInputException("mssql_net: value must be >= 1");
	}
}

// Registers the SET mssql_* knobs the C++ mssql extension exposes so that
// `SET mssql_... = ...` is accepted (no error). Behavioral knobs we actually
// honor are read where relevant; the rest are accepted no-ops for compatibility.
static void RegisterCompatSettings(DBConfig &config) {
	auto add = [&](const char *name, const LogicalType &type) {
		config.AddExtensionOption(name, "mssql_net compatibility setting", type);
	};
	for (const char *b : {"mssql_connection_cache", "mssql_order_pushdown", "mssql_copy_tablock",
	                      "mssql_ctas_use_bcp", "mssql_convert_varchar_max"}) {
		add(b, LogicalType::BOOLEAN);
	}
	for (const char *i : {"mssql_connection_limit", "mssql_connection_timeout", "mssql_acquire_timeout",
	                      "mssql_attach_validation_timeout", "mssql_catalog_cache_ttl", "mssql_copy_flush_rows",
	                      "mssql_idle_timeout", "mssql_min_connections"}) {
		add(i, LogicalType::BIGINT);
	}
	add("mssql_ctas_text_type", LogicalType::VARCHAR);

	// The INSERT knobs carry real defaults (so current_setting() reads them) and the
	// numeric ones validate `>= 1` on SET — parity with the C++ mssql extension.
	config.AddExtensionOption("mssql_insert_batch_size", "mssql_net: max rows per INSERT statement",
	                          LogicalType::BIGINT, Value::BIGINT(2000), RequireAtLeastOne);
	config.AddExtensionOption("mssql_insert_max_rows_per_statement", "mssql_net: hard cap on rows per statement",
	                          LogicalType::BIGINT, Value::BIGINT(2000), RequireAtLeastOne);
	config.AddExtensionOption("mssql_insert_max_sql_bytes", "mssql_net: max SQL statement size in bytes",
	                          LogicalType::BIGINT, Value::BIGINT(8388608), RequireAtLeastOne);
	config.AddExtensionOption("mssql_insert_use_returning_output", "mssql_net: use OUTPUT INSERTED for RETURNING",
	                          LogicalType::BOOLEAN, Value::BOOLEAN(true));

	// Auto-invalidate the catalog cache after DDL run via mssql_net_exec(). Defaults
	// to FALSE (Postgres-scanner parity): by default invalidate manually with
	// mssql_invalidate_cache()/mssql_refresh_cache(); set true to auto-invalidate.
	config.AddExtensionOption("mssql_exec_invalidate_cache",
	                          "mssql_net: invalidate the catalog cache after DDL run via mssql_net_exec()",
	                          LogicalType::BOOLEAN, Value::BOOLEAN(false));
}

// --- arrownet_managed_dir() --------------------------------------------------
// Diagnostic: forces the bridge to load and reports the resolved managed dir.
static void ArrowNetManagedDirFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	arrownet::GetBridge(); // throws with a descriptive message if loading fails
	result.SetVectorType(VectorType::CONSTANT_VECTOR);
	ConstantVector::GetData<string_t>(result)[0] =
	    StringVector::AddString(result, arrownet::GetManagedDirectory());
}

// --- arrownet_test_scan(sql VARCHAR) -----------------------------------------
// Phase 0 round-trip: routes a query string through the bridge and ingests the
// returned Arrow stream as a DuckDB table. The stub backend echoes the query.
struct MssqlNetTestScanBindData : public arrownet::ArrowStreamBindData {
	ArrowNetHandle handle = nullptr;
	~MssqlNetTestScanBindData() override {
		arrownet::CloseCatalog(handle);
	}
};

static unique_ptr<FunctionData> TestScanBind(ClientContext &context, TableFunctionBindInput &input,
                                             vector<LogicalType> &return_types, vector<string> &names) {
	auto sql = input.inputs[0].GetValue<string>();

	auto bind_data = make_uniq<MssqlNetTestScanBindData>();
	bind_data->handle = arrownet::OpenCatalog(""); // stub backend ignores the connection string
	auto handle = bind_data->handle;
	bind_data->factory = [handle, sql](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::ExecuteQuery(handle, sql, out);
	};

	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- mssql_net_query(connection_string VARCHAR, sql VARCHAR) -----------------
// Runs arbitrary T-SQL against SQL Server and streams the result into DuckDB as
// Arrow. The connection/catalog handle lives as long as the bind data.
struct MssqlNetQueryBindData : public arrownet::ArrowStreamBindData {
	ArrowNetHandle handle = nullptr;
	bool owns_handle = true; // false when borrowed from an attached catalog
	~MssqlNetQueryBindData() override {
		if (owns_handle) {
			arrownet::CloseCatalog(handle);
		}
	}
};

static unique_ptr<FunctionData> QueryBind(ClientContext &context, TableFunctionBindInput &input,
                                          vector<LogicalType> &return_types, vector<string> &names) {
	auto connection_string = input.inputs[0].GetValue<string>();
	auto sql = input.inputs[1].GetValue<string>();

	auto bind_data = make_uniq<MssqlNetQueryBindData>();
	bind_data->handle = ResolveConnection(context, connection_string, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, sql](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::ExecuteQuery(handle, sql, out);
	};

	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- mssql_net_exec(connection_string VARCHAR, sql VARCHAR) -> BIGINT --------
// Executes arbitrary T-SQL (DDL/DML/EXEC) against SQL Server and returns the
// number of rows affected. Volatile (always executed, never constant-folded).
static void MssqlNetExecFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	auto &context = state.GetContext();
	auto count = args.size();
	result.SetVectorType(VectorType::FLAT_VECTOR);
	auto result_data = FlatVector::GetData<int64_t>(result);
	auto &validity = FlatVector::Validity(result);

	// Opt-in: invalidate the attached catalog's cache after a DDL statement so a later
	// read / CREATE IF NOT EXISTS sees the real server-side state. Whether a statement
	// is DDL is decided in C# (see SqlDdl.MayChangeSchema); here we only act on the flag.
	bool invalidate_on_ddl = false;
	Value invalidate_value;
	if (context.TryGetCurrentSetting("mssql_exec_invalidate_cache", invalidate_value) && !invalidate_value.IsNull()) {
		invalidate_on_ddl = invalidate_value.GetValue<bool>();
	}

	for (idx_t i = 0; i < count; i++) {
		auto conn_value = args.GetValue(0, i);
		auto sql_value = args.GetValue(1, i);
		if (conn_value.IsNull() || sql_value.IsNull()) {
			validity.SetInvalid(i);
			continue;
		}
		auto conn_name = StringValue::Get(conn_value);
		bool owns = true;
		auto handle = ResolveConnection(context, conn_name, owns);
		bool schema_may_change = false;
		try {
			result_data[i] = arrownet::ExecuteDml(handle, StringValue::Get(sql_value), &schema_may_change);
		} catch (...) {
			if (owns) {
				arrownet::CloseCatalog(handle);
			}
			throw;
		}
		if (owns) {
			arrownet::CloseCatalog(handle);
		}

		// owns == false => the arg named an attached mssql_net catalog whose cache we own.
		if (invalidate_on_ddl && schema_may_change && !owns) {
			auto db = DatabaseManager::Get(context).GetDatabase(context, conn_name);
			if (db && db->GetCatalog().GetCatalogType() == "mssql_net") {
				db->GetCatalog().Cast<ArrowNetCatalog>().RefreshCache(context);
			}
		}
	}
}

// --- mssql_refresh_cache(catalog_name VARCHAR) -> BOOLEAN -------------------
// Re-discovers the attached catalog's schemas/tables so out-of-band DDL (e.g.
// via mssql_net_exec) becomes visible. Compatible with the C++ mssql extension.
static void MssqlRefreshCacheFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	auto &context = state.GetContext();
	auto count = args.size();
	result.SetVectorType(VectorType::FLAT_VECTOR);
	auto result_data = FlatVector::GetData<bool>(result);
	for (idx_t i = 0; i < count; i++) {
		auto value = args.GetValue(0, i);
		if (value.IsNull()) {
			throw InvalidInputException("mssql_refresh_cache: catalog name is required (got NULL)");
		}
		auto name = StringValue::Get(value);
		auto &catalog = Catalog::GetCatalog(context, name);
		if (catalog.GetCatalogType() != "mssql_net") {
			throw BinderException("mssql_refresh_cache: catalog '%s' is not an mssql_net catalog (type: %s)", name,
			                      catalog.GetCatalogType());
		}
		catalog.Cast<ArrowNetCatalog>().RefreshCache(context);
		result_data[i] = true;
	}
}

static void LoadInternal(ExtensionLoader &loader) {
	// CREATE SECRET ... (TYPE mssql_net, host '...', ...)
	RegisterMssqlNetSecretType(loader);
	// ATTACH '<connstr>' AS db (TYPE mssql_net) — or ATTACH '' (TYPE mssql_net, SECRET name)
	RegisterMssqlNetStorageExtension(loader);
	// COPY ... TO 'mssql://...' (FORMAT mssql_net)
	RegisterMssqlNetCopyFunction(loader);

	loader.RegisterFunction(ScalarFunction("arrownet_version", {}, LogicalType::VARCHAR, ArrowNetVersionFunction));
	// mssql_version(): compatibility alias used as a preamble by many mssql tests.
	loader.RegisterFunction(ScalarFunction("mssql_version", {}, LogicalType::VARCHAR, ArrowNetVersionFunction));
	loader.RegisterFunction(
	    ScalarFunction("arrownet_managed_dir", {}, LogicalType::VARCHAR, ArrowNetManagedDirFunction));

	RegisterCompatSettings(DBConfig::GetConfig(loader.GetDatabaseInstance()));
	RegisterArrowNetOptimizer(DBConfig::GetConfig(loader.GetDatabaseInstance()));

	TableFunction test_scan("arrownet_test_scan", {LogicalType::VARCHAR}, arrownet::ArrowStreamScan, TestScanBind,
	                        arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	test_scan.projection_pushdown = true;
	loader.RegisterFunction(test_scan);

	TableFunction query_fn("mssql_net_query", {LogicalType::VARCHAR, LogicalType::VARCHAR},
	                       arrownet::ArrowStreamScan, QueryBind, arrownet::ArrowStreamInitGlobal,
	                       arrownet::ArrowStreamInitLocal);
	query_fn.projection_pushdown = true;
	loader.RegisterFunction(query_fn);

	ScalarFunction exec_fn("mssql_net_exec", {LogicalType::VARCHAR, LogicalType::VARCHAR}, LogicalType::BIGINT,
	                       MssqlNetExecFunction);
	exec_fn.stability = FunctionStability::VOLATILE;
	loader.RegisterFunction(exec_fn);

	// mssql_refresh_cache(catalog) re-discovers the attached catalog's metadata.
	for (const char *fn_name : {"mssql_refresh_cache", "mssql_net_refresh_cache"}) {
		ScalarFunction refresh_fn(fn_name, {LogicalType::VARCHAR}, LogicalType::BOOLEAN, MssqlRefreshCacheFunction);
		refresh_fn.stability = FunctionStability::VOLATILE;
		loader.RegisterFunction(refresh_fn);
	}

	// mssql_invalidate_cache(catalog [, schema [, table]]) — compatibility alias.
	// Our cache is catalog-granular, so every arity re-discovers the whole catalog
	// (a valid superset of point invalidation); the schema/table args are accepted
	// but coarsened. Only arg0 (the catalog) is read.
	for (const char *fn_name : {"mssql_invalidate_cache", "mssql_net_invalidate_cache"}) {
		ScalarFunctionSet set(fn_name);
		const vector<vector<LogicalType>> signatures = {
		    {LogicalType::VARCHAR},
		    {LogicalType::VARCHAR, LogicalType::VARCHAR},
		    {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR}};
		for (auto &arg_types : signatures) {
			ScalarFunction fn(fn_name, arg_types, LogicalType::BOOLEAN, MssqlRefreshCacheFunction);
			fn.stability = FunctionStability::VOLATILE;
			set.AddFunction(fn);
		}
		loader.RegisterFunction(set);
	}
}

void MssqlNetExtension::Load(ExtensionLoader &loader) {
	LoadInternal(loader);
}

std::string MssqlNetExtension::Name() {
	return "mssql_net";
}

std::string MssqlNetExtension::Version() const {
	return GetExtensionVersion();
}

} // namespace duckdb

extern "C" {

DUCKDB_CPP_EXTENSION_ENTRY(mssql_net, loader) {
	duckdb::LoadInternal(loader);
}
}
