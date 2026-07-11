//===----------------------------------------------------------------------===//
//                         DuckDB mssql_net Extension (entry)
//===----------------------------------------------------------------------===//

#include "mssql_net_extension.hpp"

// arrow_ingest pulls DuckDB's Arrow C headers first, then abi.h — keep it ahead
// of clr_host so the project agrees on one ArrowSchema/ArrowArrayStream layout.
#include "arrownet/arrow_ingest.hpp"

#include "arrownet/clr_host.hpp"
#include "arrownet/arrownet_onelake_fs.hpp"
#include "arrownet/arrownet_delta_mfr.hpp"
#include "arrownet/arrownet_variant.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "catalog/arrownet_schema_entry.hpp"
#include "copy/mssql_net_copy.hpp"
#include "arrownet_optimizer.hpp"
#include "arrownet_fs_spike.hpp"
#include "arrownet_host_query.hpp"
#include "mssql_net_secret.hpp"
#include "mssql_net_storage.hpp"
#include "duckdb/function/function_set.hpp"
#include "duckdb/function/scalar_function.hpp"
#include "duckdb/main/attached_database.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/transaction/meta_transaction.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/database_manager.hpp"
#include "duckdb/main/extension/extension_loader.hpp"

#include <array>
#include <cstring>
#include <utility>

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
		if (ArrowNetCatalog::Is(catalog)) {
			// Ensure our catalog's transaction is started for this context, so a
			// write via mssql_net_exec/query joins the active DuckDB transaction
			// (mssql_net_exec grabs the handle directly, bypassing the binder).
			catalog.GetCatalogTransaction(context);
			owns = false;
			return catalog.Cast<ArrowNetCatalog>().GetHandle();
		}
	}
	owns = true;
	// A bare name may also be a provider secret — build a connection string from it.
	if (IsKnownSecret(context, conn_or_name)) {
		// No ATTACH target here (a raw query function), so a foreign auth-only secret (e.g. azure) errors
		// clearly inside BuildConnectionStringFromSecret; our own mssql_net secret is a full connstr.
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

// -----------------------------------------------------------------------------
// Provider-declared settings (see docs/settings-architecture.md). Each provider declares its settings in C#
// (IBackend.Settings); we register them here as DuckDB extension options and push value changes back into
// the managed ProviderSettingsStore. DuckDB's set-callback carries no setting name, so we bind one callback
// per slot (a compile-time trampoline array). The provider-agnostic core thus knows no setting names.
// -----------------------------------------------------------------------------

// Max settings registrable across all providers (trampoline array size; generous).
static constexpr size_t ARROWNET_MAX_SETTINGS = 128;

struct SettingSlot {
	string provider;
	string name;
	bool has_min = false;
	int64_t min_value = 0;
};
static SettingSlot g_setting_slots[ARROWNET_MAX_SETTINGS];
static size_t g_setting_count = 0;

// One set-callback per slot: validates an optional minimum (parity with the former RequireAtLeastOne), then
// best-effort pushes the new value to the managed store. DuckDB has already cast `value` to the option type.
template <size_t I>
static void SettingTrampoline(ClientContext &, SetScope, Value &value) {
	const SettingSlot &slot = g_setting_slots[I];
	if (slot.has_min && !value.IsNull() && value.GetValue<int64_t>() < slot.min_value) {
		throw InvalidInputException("mssql_net: %s must be >= %lld", slot.name, (long long)slot.min_value);
	}
	try {
		if (value.IsNull()) {
			arrownet::SetSetting(slot.provider, slot.name, nullptr);
		} else {
			string rendered = value.ToString();
			arrownet::SetSetting(slot.provider, slot.name, rendered.c_str());
		}
	} catch (...) {
		// Best-effort: a managed-store hiccup must not fail the user's SET.
	}
}

// Hand-rolled compile-time index sequence: the build is -std=c++11 (std::make_index_sequence is C++14;
// MSVC's STL provides it even in C++11 mode, but a strict libstdc++ -std=c++11 build does not).
template <size_t...>
struct IndexSeq {};
template <size_t N, size_t... Is>
struct BuildIndexSeq : BuildIndexSeq<N - 1, N - 1, Is...> {};
template <size_t... Is>
struct BuildIndexSeq<0, Is...> {
	using type = IndexSeq<Is...>;
};

template <size_t... I>
static std::array<set_option_callback_t, sizeof...(I)> MakeSettingTrampolines(IndexSeq<I...>) {
	return {{&SettingTrampoline<I>...}};
}
static const std::array<set_option_callback_t, ARROWNET_MAX_SETTINGS> g_setting_trampolines =
    MakeSettingTrampolines(BuildIndexSeq<ARROWNET_MAX_SETTINGS>::type {});

// Registers every provider's declared settings (queried from the managed bridge) as DuckDB extension
// options. Best-effort: if the bridge can't boot at load (e.g. the managed dir is missing), registration is
// skipped and the extension still loads (SET of provider settings would then error as "unknown setting";
// the bridge boots lazily on first use as before).
static void RegisterProviderSettings(ExtensionLoader &loader) {
	DBConfig &config = DBConfig::GetConfig(loader.GetDatabaseInstance());
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		arrownet::ListSettings(stream);
		// Columns: provider, name, type, default, description, min (empty string => null/none).
		auto rows = ReadStringTable(stream, 6);
		size_t n = rows[0].size();
		for (size_t i = 0; i < n && g_setting_count < ARROWNET_MAX_SETTINGS; i++) {
			const string &provider = rows[0][i];
			const string &name = rows[1][i];
			const string &type = rows[2][i];
			const string &def = rows[3][i];
			const string &desc = rows[4][i];
			const string &min = rows[5][i];

			LogicalType lt = type == "bool" ? LogicalType::BOOLEAN
			               : type == "long" ? LogicalType::BIGINT
			                                : LogicalType::VARCHAR;
			Value default_value; // NULL => unset
			if (!def.empty()) {
				if (type == "bool") {
					default_value = Value::BOOLEAN(def == "true" || def == "1");
				} else if (type == "long") {
					default_value = Value::BIGINT(std::stoll(def));
				} else {
					default_value = Value(def);
				}
			}

			size_t slot = g_setting_count++;
			g_setting_slots[slot].provider = provider;
			g_setting_slots[slot].name = name;
			g_setting_slots[slot].has_min = !min.empty();
			g_setting_slots[slot].min_value = min.empty() ? 0 : std::stoll(min);

			config.AddExtensionOption(name, desc, lt, default_value, g_setting_trampolines[slot]);

			// Seed the managed store with the default so reads see it before any SET.
			if (!def.empty()) {
				try {
					arrownet::SetSetting(provider, name, def.c_str());
				} catch (...) {
				}
			}
		}
	} catch (std::exception &) {
		// Bridge unavailable at load — skip provider-setting registration (graceful degradation).
	}
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

// --- mssql_net_functions(connection VARCHAR) ---------------------------------
// Lists the routines (user scalar/table functions + procedures) discovered in the
// attached catalog / connection: (schema_name, name, kind, param_count, return_type).
// Diagnostic / introspection; the discovery SQL lives entirely in the C# backend.
struct MssqlNetFunctionsBindData : public arrownet::ArrowStreamBindData {
	ArrowNetHandle handle = nullptr;
	bool owns_handle = true; // false when borrowed from an attached catalog
	~MssqlNetFunctionsBindData() override {
		if (owns_handle) {
			arrownet::CloseCatalog(handle);
		}
	}
};

static unique_ptr<FunctionData> FunctionsBind(ClientContext &context, TableFunctionBindInput &input,
                                              vector<LogicalType> &return_types, vector<string> &names) {
	auto connection = input.inputs[0].GetValue<string>();

	auto bind_data = make_uniq<MssqlNetFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, connection, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_FUNCTIONS, "", "", out);
	};

	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- mssql_server_info(connection VARCHAR) -----------------------------------
// Surfaces the detected server capability profile (engine edition, product version,
// collation + the derived flags driving connection mode + type mapping) as
// (property, value) rows. Diagnostic; the profile is detected in the C# backend.
static unique_ptr<FunctionData> ServerInfoBind(ClientContext &context, TableFunctionBindInput &input,
                                               vector<LogicalType> &return_types, vector<string> &names) {
	auto connection = input.inputs[0].GetValue<string>();

	auto bind_data = make_uniq<MssqlNetFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, connection, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_SERVER_INFO, "", "", out);
	};

	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- arrownet_delta_snapshots(catalog VARCHAR, 'schema.table' VARCHAR) --------
// The commit history (snapshots/versions view) of a Delta table in an ATTACH'd Delta-provider catalog:
// (version, timestamp, operation, operation_parameters). First arg = the attached catalog NAME (resolved to its
// handle — no abfss path needed); second = the table, schema-qualified ('schema.table'). Schema is mandatory on
// a schema-enabled lakehouse, defaults to "main" on a flat catalog (resolved managed-side). Delta only; a
// non-Delta catalog yields no snapshot rows.
static unique_ptr<FunctionData> SnapshotsBind(ClientContext &context, TableFunctionBindInput &input,
                                              vector<LogicalType> &return_types, vector<string> &names) {
	auto catalog_name = input.inputs[0].GetValue<string>();
	auto table_ref = input.inputs[1].GetValue<string>();
	// Split 'schema.table' on the first dot → (schema, table); a bare name → ("", table) (managed side defaults
	// it to "main" on a flat catalog, or errors on a schema-enabled one).
	string schema;
	string table = table_ref;
	auto dot = table_ref.find('.');
	if (dot != string::npos) {
		schema = table_ref.substr(0, dot);
		table = table_ref.substr(dot + 1);
	}

	auto bind_data = make_uniq<MssqlNetFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, schema, table](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_SNAPSHOTS, schema, table, out);
	};

	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- arrownet_delta_changes(catalog, 'schema.table', from_version[, to_version]) ----------------------------
// The Change Data Feed of a Delta table between two versions: the table's columns plus _change_type,
// _commit_version, _commit_timestamp. Catalog NAME (resolved to its handle) + schema-qualified table; the
// version range is packed into arg2 as "from:to" (to omitted => latest). Requires the table to have
// delta.enableChangeDataFeed (else the managed side errors). Delta only.
static unique_ptr<FunctionData> ChangesBind(ClientContext &context, TableFunctionBindInput &input,
                                            vector<LogicalType> &return_types, vector<string> &names) {
	auto catalog_name = input.inputs[0].GetValue<string>();
	auto table_ref = input.inputs[1].GetValue<string>();
	int64_t from_version = input.inputs[2].GetValue<int64_t>();
	int64_t to_version = (input.inputs.size() > 3 && !input.inputs[3].IsNull())
	                         ? input.inputs[3].GetValue<int64_t>()
	                         : -1; // -1 => latest
	string range = std::to_string(from_version) + ":" + (to_version < 0 ? string() : std::to_string(to_version));

	auto bind_data = make_uniq<MssqlNetFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, range](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_CHANGES, table_ref, range, out);
	};

	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- arrownet_delta_get/set_transaction_version — Delta idempotent appends (the `txn` action) ---------------
// get(catalog, 'schema.table', app_id) -> (app_id, version|NULL): the app's committed high-water mark.
// set(catalog, 'schema.table', app_id, version [, expected_previous]) -> the echoed row: PARKS the version on
// the CURRENT explicit transaction; at COMMIT it is compared-and-swapped against the latest snapshot and the
// `txn` action commits ATOMICALLY with the transaction's fused commit — a retried batch whose first attempt
// landed fails the CAS instead of duplicating data (duckdb-delta / Spark txnAppId parity). Both schemas are
// FIXED and set here directly — deliberately NO PopulateReturnSchema probe, so the (side-effecting) factory
// runs only at EXECUTION, where the ambient transaction id is established by ArrowStreamInitGlobal.
static void TxnVersionSchema(vector<LogicalType> &return_types, vector<string> &names) {
	return_types = {LogicalType::VARCHAR, LogicalType::BIGINT};
	names = {"app_id", "version"};
}

static unique_ptr<FunctionData> GetTxnVersionBind(ClientContext &context, TableFunctionBindInput &input,
                                                  vector<LogicalType> &return_types, vector<string> &names) {
	auto catalog_name = input.inputs[0].GetValue<string>();
	auto table_ref = input.inputs[1].GetValue<string>();
	auto app_id = input.inputs[2].GetValue<string>();

	auto bind_data = make_uniq<MssqlNetFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, app_id](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_TXN_VERSION, table_ref, app_id, out);
	};
	TxnVersionSchema(return_types, names);
	return std::move(bind_data);
}

static unique_ptr<FunctionData> SetTxnVersionBind(ClientContext &context, TableFunctionBindInput &input,
                                                  vector<LogicalType> &return_types, vector<string> &names) {
	auto catalog_name = input.inputs[0].GetValue<string>();
	auto table_ref = input.inputs[1].GetValue<string>();
	auto app_id = input.inputs[2].GetValue<string>();
	if (app_id.find('\n') != string::npos) {
		throw BinderException("arrownet_delta_set_transaction_version: app_id must not contain a newline");
	}
	int64_t version = input.inputs[3].GetValue<int64_t>();
	// expected_previous omitted or NULL => "must not exist yet" (first batch of the app).
	string expected = (input.inputs.size() > 4 && !input.inputs[4].IsNull())
	                      ? std::to_string(input.inputs[4].GetValue<int64_t>())
	                      : string();
	string payload = app_id + "\n" + std::to_string(version) + "\n" + expected;

	auto bind_data = make_uniq<MssqlNetFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, payload](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_SET_TXN_VERSION, table_ref, payload, out);
	};
	TxnVersionSchema(return_types, names);
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
			// mssql_net_exec is a raw passthrough. In JOIN-ONLY mode it runs on the active transaction's pinned
			// connection IFF a DuckDB-managed write is already in flight in this transaction (e.g. a dbt model's
			// CTAS, with the exec in a post-hook) — then it is atomic with the transaction and sees its
			// uncommitted writes. Otherwise it autocommits on its own connection (a raw exec's string-arg target
			// never triggers the catalog's transaction lifecycle, so nothing would ever commit a pinned
			// connection). See docs/dbt-hooks.md. handle is unused by set_active_txn (the ambient is per-thread).
			arrownet::SetActiveTxn(handle, (int64_t)MetaTransaction::Get(context).global_transaction_id,
			                       /*join_only=*/true);
			// Host-FS opener for a raw exec against a host-FS provider (the Delta catalog's OPTIMIZE/VACUUM read +
			// write the _delta_log/data through DuckDB's FileSystem). No-op for SQL Server / delta-rs (they ignore it).
			arrownet::SetActiveOpener(reinterpret_cast<ArrowNetHandle>(&context));
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
			if (db && ArrowNetCatalog::Is(db->GetCatalog())) {
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
		if (!ArrowNetCatalog::Is(catalog)) {
			throw BinderException("mssql_refresh_cache: catalog '%s' is not an mssql_net catalog (type: %s)", name,
			                      catalog.GetCatalogType());
		}
		catalog.Cast<ArrowNetCatalog>().RefreshCache(context);
		result_data[i] = true;
	}
}

static void LoadInternal(ExtensionLoader &loader) {
	// SPIKE: install the host FileSystem callbacks FIRST (before any bridge boot) + register
	// arrownet_fs_spike(path). Foundation for a managed lakehouse reader doing secret-backed remote IO.
	RegisterFsSpike(loader);
	// arrownet_delta_scan(path) is now a connection-free GLOBAL host-FS table function (registered by
	// RegisterArrowNetGlobalFunctions below, dispatched to the managed engineered-wood reader) — no bespoke
	// C++ registration needed. See docs/global-functions.md §host-FS.
	// arrownet_host_query(sql) — run a query on a fresh host connection, result as Arrow (reuse DuckDB).
	RegisterHostQuery(loader);
	// CREATE SECRET ... (TYPE mssql_net, host '...', ...) — secret type(s) + fields declared in C#
	RegisterProviderSecrets(loader);
	// ATTACH '<connstr>' AS db (TYPE mssql_net) — or ATTACH '' (TYPE mssql_net, SECRET name)
	RegisterMssqlNetStorageExtension(loader);
	// COPY ... TO 'mssql://...' (FORMAT mssql_net)
	RegisterMssqlNetCopyFunction(loader);

	loader.RegisterFunction(ScalarFunction("arrownet_version", {}, LogicalType::VARCHAR, ArrowNetVersionFunction));
	// mssql_version(): compatibility alias used as a preamble by many mssql tests.
	loader.RegisterFunction(ScalarFunction("mssql_version", {}, LogicalType::VARCHAR, ArrowNetVersionFunction));
	loader.RegisterFunction(
	    ScalarFunction("arrownet_managed_dir", {}, LogicalType::VARCHAR, ArrowNetManagedDirFunction));

	RegisterProviderSettings(loader);
	arrownet::RegisterOneLakeFileSystem(loader.GetDatabaseInstance()); // onelake:// VFS subsystem (docs/filesystem-bridge.md §3)
	arrownet::RegisterDeltaMultiFileScan(loader); // arrownet_delta_mfr_scan — native Delta read (docs/multifile-delta.md Phase A)
	RegisterArrowNetGlobalFunctions(loader); // connection-free global functions (docs/global-functions.md)
	RegisterArrowNetOptimizer(DBConfig::GetConfig(loader.GetDatabaseInstance()));
	RegisterArrowNetInOutFinalizer(DBConfig::GetConfig(loader.GetDatabaseInstance()));
	// VARIANT over the Arrow C boundary: registers the arrow.parquet.variant type extension so VARIANT
	// crosses every export/import path as the tagged transport struct (see arrownet_variant.hpp).
	arrownet::RegisterArrowNetVariantExtension(loader.GetDatabaseInstance());

	TableFunction test_scan("arrownet_test_scan", {LogicalType::VARCHAR}, arrownet::ArrowStreamScan, TestScanBind,
	                        arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	test_scan.projection_pushdown = true;
	loader.RegisterFunction(test_scan);

	// Provider-agnostic names are the going-forward surface (the binary hosts several providers). They are
	// registered as ALIASES of the mssql_net_* names — additive, so existing usage + the compat corpus keep
	// working; the eventual removal of the mssql_net_* names is the separate breaking rename.
	TableFunction query_fn("mssql_net_query", {LogicalType::VARCHAR, LogicalType::VARCHAR},
	                       arrownet::ArrowStreamScan, QueryBind, arrownet::ArrowStreamInitGlobal,
	                       arrownet::ArrowStreamInitLocal);
	query_fn.projection_pushdown = true;
	loader.RegisterFunction(query_fn);
	query_fn.name = "arrownet_query";
	loader.RegisterFunction(query_fn);

	// mssql_net_functions(catalog|connstr) — lists discovered routines (diagnostic).
	TableFunction functions_fn("mssql_net_functions", {LogicalType::VARCHAR}, arrownet::ArrowStreamScan,
	                           FunctionsBind, arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	functions_fn.projection_pushdown = true; // arrow_ingest maps requested columns; required (see mssql_net_query)
	loader.RegisterFunction(functions_fn);
	functions_fn.name = "arrownet_functions";
	loader.RegisterFunction(functions_fn);

	// mssql_server_info(catalog|connstr) — the detected server capability profile (diagnostic).
	TableFunction server_info_fn("mssql_server_info", {LogicalType::VARCHAR}, arrownet::ArrowStreamScan,
	                             ServerInfoBind, arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	server_info_fn.projection_pushdown = true;
	loader.RegisterFunction(server_info_fn);
	server_info_fn.name = "arrownet_server_info";
	loader.RegisterFunction(server_info_fn);

	// arrownet_delta_snapshots(catalog, 'schema.table') — a Delta table's commit-history / snapshots view.
	TableFunction snapshots_fn("arrownet_delta_snapshots", {LogicalType::VARCHAR, LogicalType::VARCHAR},
	                           arrownet::ArrowStreamScan, SnapshotsBind, arrownet::ArrowStreamInitGlobal,
	                           arrownet::ArrowStreamInitLocal);
	snapshots_fn.projection_pushdown = true;
	loader.RegisterFunction(snapshots_fn);

	// arrownet_delta_changes(catalog, 'schema.table', from_version[, to_version]) — the Change Data Feed.
	// Two overloads: with an explicit end version, or without (=> latest).
	TableFunction changes_fn("arrownet_delta_changes",
	                         {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::BIGINT, LogicalType::BIGINT},
	                         arrownet::ArrowStreamScan, ChangesBind, arrownet::ArrowStreamInitGlobal,
	                         arrownet::ArrowStreamInitLocal);
	changes_fn.projection_pushdown = true;
	loader.RegisterFunction(changes_fn);
	changes_fn.arguments = {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::BIGINT};
	loader.RegisterFunction(changes_fn);

	// arrownet_delta_get/set_transaction_version — Delta application-transaction versions (idempotent
	// appends): get the committed high-water mark / park a CAS'd version on the current explicit txn.
	TableFunction get_txn_fn("arrownet_delta_get_transaction_version",
	                         {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR},
	                         arrownet::ArrowStreamScan, GetTxnVersionBind, arrownet::ArrowStreamInitGlobal,
	                         arrownet::ArrowStreamInitLocal);
	loader.RegisterFunction(get_txn_fn);
	TableFunction set_txn_fn("arrownet_delta_set_transaction_version",
	                         {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR,
	                          LogicalType::BIGINT, LogicalType::BIGINT},
	                         arrownet::ArrowStreamScan, SetTxnVersionBind, arrownet::ArrowStreamInitGlobal,
	                         arrownet::ArrowStreamInitLocal);
	loader.RegisterFunction(set_txn_fn);
	set_txn_fn.arguments = {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR,
	                        LogicalType::BIGINT}; // expected omitted => must-not-exist (first batch)
	loader.RegisterFunction(set_txn_fn);

	ScalarFunction exec_fn("mssql_net_exec", {LogicalType::VARCHAR, LogicalType::VARCHAR}, LogicalType::BIGINT,
	                       MssqlNetExecFunction);
	exec_fn.stability = FunctionStability::VOLATILE;
	loader.RegisterFunction(exec_fn);
	exec_fn.name = "arrownet_exec";
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
