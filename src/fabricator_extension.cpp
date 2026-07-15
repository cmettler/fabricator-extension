//===----------------------------------------------------------------------===//
//                         DuckDB fabricator Extension (entry)
//===----------------------------------------------------------------------===//

#include "fabricator_extension.hpp"

// arrow_ingest pulls DuckDB's Arrow C headers first, then abi.h — keep it ahead
// of clr_host so the project agrees on one ArrowSchema/ArrowArrayStream layout.
#include "fabricator/arrow_ingest.hpp"

#include "fabricator/clr_host.hpp"
#include "fabricator/fabricator_onelake_fs.hpp"
#include "fabricator/fabricator_delta_mfr.hpp"
#include "fabricator/fabricator_variant.hpp"
#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_metadata.hpp"
#include "catalog/fabricator_schema_entry.hpp"
#include "copy/fabricator_copy.hpp"
#include "fabricator_optimizer.hpp"
#include "fabricator_fs_spike.hpp"
#include "fabricator_host_query.hpp"
#include "fabricator_secret.hpp"
#include "fabricator_storage.hpp"
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

// Resolves the first argument of fabricator_query / fabricator_exec to a backend
// connection. If it names an attached `fabricator` catalog, the caller BORROWS
// that catalog's handle (owns=false, must not close it); otherwise the argument
// is treated as a connection string and a fresh handle is opened (owns=true).
// This mirrors the C++ mssql extension's mssql_scan/mssql_exec ergonomics.
static FabricatorHandle ResolveConnection(ClientContext &context, const string &conn_or_name, bool &owns) {
	auto db = DatabaseManager::Get(context).GetDatabase(context, conn_or_name);
	if (db) {
		auto &catalog = db->GetCatalog();
		if (FabricatorCatalog::Is(catalog)) {
			// Ensure our catalog's transaction is started for this context, so a
			// write via fabricator_exec/query joins the active DuckDB transaction
			// (fabricator_exec grabs the handle directly, bypassing the binder).
			catalog.GetCatalogTransaction(context);
			owns = false;
			return catalog.Cast<FabricatorCatalog>().GetHandle();
		}
	}
	owns = true;
	// A bare name may also be a provider secret — build a connection string from it.
	if (IsKnownSecret(context, conn_or_name)) {
		// No ATTACH target here (a raw query function), so a foreign auth-only secret (e.g. azure) errors
		// clearly inside BuildConnectionStringFromSecret; our own mssql secret is a full connstr.
		return fabricator::OpenCatalog(BuildConnectionStringFromSecret(context, conn_or_name));
	}
	// A value with no connection-string markers ('=' key/value pairs or a 'scheme://'
	// URI) was meant as a context NAME — an attached catalog or a secret — which we
	// just failed to resolve. Fail with a clear error instead of handing a bare token
	// to the driver (which would surface an opaque network error).
	if (conn_or_name.find('=') == string::npos && conn_or_name.find("://") == string::npos) {
		throw BinderException("Unknown context '%s' (not an attached fabricator catalog or secret)", conn_or_name);
	}
	return fabricator::OpenCatalog(conn_or_name);
}

static const char *GetExtensionVersion() {
#ifdef FABRICATOR_VERSION
	return FABRICATOR_VERSION;
#else
	return "0.0.1-dev";
#endif
}

// --- fabricator_version() / fabricator_version() ------------------------------------
static void FabricatorVersionFunction(DataChunk &args, ExpressionState &state, Vector &result) {
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
static constexpr size_t FABRICATOR_MAX_SETTINGS = 128;

struct SettingSlot {
	string provider;
	string name;
	bool has_min = false;
	int64_t min_value = 0;
};
static SettingSlot g_setting_slots[FABRICATOR_MAX_SETTINGS];
static size_t g_setting_count = 0;

// One set-callback per slot: validates an optional minimum (parity with the former RequireAtLeastOne), then
// best-effort pushes the new value to the managed store. DuckDB has already cast `value` to the option type.
template <size_t I>
static void SettingTrampoline(ClientContext &, SetScope, Value &value) {
	const SettingSlot &slot = g_setting_slots[I];
	if (slot.has_min && !value.IsNull() && value.GetValue<int64_t>() < slot.min_value) {
		throw InvalidInputException("fabricator: %s must be >= %lld", slot.name, (long long)slot.min_value);
	}
	try {
		if (value.IsNull()) {
			fabricator::SetSetting(slot.provider, slot.name, nullptr);
		} else {
			string rendered = value.ToString();
			fabricator::SetSetting(slot.provider, slot.name, rendered.c_str());
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
static const std::array<set_option_callback_t, FABRICATOR_MAX_SETTINGS> g_setting_trampolines =
    MakeSettingTrampolines(BuildIndexSeq<FABRICATOR_MAX_SETTINGS>::type {});

// Registers every provider's declared settings (queried from the managed bridge) as DuckDB extension
// options. Best-effort: if the bridge can't boot at load (e.g. the managed dir is missing), registration is
// skipped and the extension still loads (SET of provider settings would then error as "unknown setting";
// the bridge boots lazily on first use as before).
static void RegisterProviderSettings(ExtensionLoader &loader) {
	DBConfig &config = DBConfig::GetConfig(loader.GetDatabaseInstance());
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		fabricator::ListSettings(stream);
		// Columns: provider, name, type, default, description, min (empty string => null/none).
		auto rows = ReadStringTable(stream, 6);
		size_t n = rows[0].size();
		for (size_t i = 0; i < n && g_setting_count < FABRICATOR_MAX_SETTINGS; i++) {
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
					fabricator::SetSetting(provider, name, def.c_str());
				} catch (...) {
				}
			}
		}
	} catch (std::exception &) {
		// Bridge unavailable at load — skip provider-setting registration (graceful degradation).
	}
}


// --- fabricator_managed_dir() --------------------------------------------------
// Diagnostic: forces the bridge to load and reports the resolved managed dir.
static void FabricatorManagedDirFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	fabricator::GetBridge(); // throws with a descriptive message if loading fails
	result.SetVectorType(VectorType::CONSTANT_VECTOR);
	ConstantVector::GetData<string_t>(result)[0] =
	    StringVector::AddString(result, fabricator::GetManagedDirectory());
}

// --- fabricator_test_scan(sql VARCHAR) -----------------------------------------
// Phase 0 round-trip: routes a query string through the bridge and ingests the
// returned Arrow stream as a DuckDB table. The stub backend echoes the query.
struct FabricatorTestScanBindData : public fabricator::ArrowStreamBindData {
	FabricatorHandle handle = nullptr;
	~FabricatorTestScanBindData() override {
		fabricator::CloseCatalog(handle);
	}
};

static unique_ptr<FunctionData> TestScanBind(ClientContext &context, TableFunctionBindInput &input,
                                             vector<LogicalType> &return_types, vector<string> &names) {
	auto sql = input.inputs[0].GetValue<string>();

	auto bind_data = make_uniq<FabricatorTestScanBindData>();
	bind_data->handle = fabricator::OpenCatalog(""); // stub backend ignores the connection string
	auto handle = bind_data->handle;
	bind_data->factory = [handle, sql](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::ExecuteQuery(handle, sql, out);
	};

	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- fabricator_query(connection_string VARCHAR, sql VARCHAR) -----------------
// Runs arbitrary T-SQL against SQL Server and streams the result into DuckDB as
// Arrow. The connection/catalog handle lives as long as the bind data.
struct FabricatorQueryBindData : public fabricator::ArrowStreamBindData {
	FabricatorHandle handle = nullptr;
	bool owns_handle = true; // false when borrowed from an attached catalog
	~FabricatorQueryBindData() override {
		if (owns_handle) {
			fabricator::CloseCatalog(handle);
		}
	}
};

static unique_ptr<FunctionData> QueryBind(ClientContext &context, TableFunctionBindInput &input,
                                          vector<LogicalType> &return_types, vector<string> &names) {
	auto connection_string = input.inputs[0].GetValue<string>();
	auto sql = input.inputs[1].GetValue<string>();

	auto bind_data = make_uniq<FabricatorQueryBindData>();
	bind_data->handle = ResolveConnection(context, connection_string, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, sql](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::ExecuteQuery(handle, sql, out);
	};

	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- fabricator_functions(connection VARCHAR) ---------------------------------
// Lists the routines (user scalar/table functions + procedures) discovered in the
// attached catalog / connection: (schema_name, name, kind, param_count, return_type).
// Diagnostic / introspection; the discovery SQL lives entirely in the C# backend.
struct FabricatorFunctionsBindData : public fabricator::ArrowStreamBindData {
	FabricatorHandle handle = nullptr;
	bool owns_handle = true; // false when borrowed from an attached catalog
	~FabricatorFunctionsBindData() override {
		if (owns_handle) {
			fabricator::CloseCatalog(handle);
		}
	}
};

static unique_ptr<FunctionData> FunctionsBind(ClientContext &context, TableFunctionBindInput &input,
                                              vector<LogicalType> &return_types, vector<string> &names) {
	auto connection = input.inputs[0].GetValue<string>();

	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, connection, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_FUNCTIONS, "", "", out);
	};

	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- fabricator_server_info(connection VARCHAR) -----------------------------------
// Surfaces the detected server capability profile (engine edition, product version,
// collation + the derived flags driving connection mode + type mapping) as
// (property, value) rows. Diagnostic; the profile is detected in the C# backend.
static unique_ptr<FunctionData> ServerInfoBind(ClientContext &context, TableFunctionBindInput &input,
                                               vector<LogicalType> &return_types, vector<string> &names) {
	auto connection = input.inputs[0].GetValue<string>();

	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, connection, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_SERVER_INFO, "", "", out);
	};

	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- fabricator_delta_snapshots(catalog VARCHAR, 'schema.table' VARCHAR) --------
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

	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, schema, table](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_SNAPSHOTS, schema, table, out);
	};

	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- fabricator_delta_changes(catalog, 'schema.table', from_version[, to_version]) ----------------------------
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

	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, range](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_CHANGES, table_ref, range, out);
	};

	fabricator::PopulateReturnSchema(context, *bind_data, return_types, names);
	return std::move(bind_data);
}

// --- fabricator_delta_get/set_transaction_version — Delta idempotent appends (the `txn` action) ---------------
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

	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, app_id](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_TXN_VERSION, table_ref, app_id, out);
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
		throw BinderException("fabricator_delta_set_transaction_version: app_id must not contain a newline");
	}
	int64_t version = input.inputs[3].GetValue<int64_t>();
	// expected_previous omitted or NULL => "must not exist yet" (first batch of the app).
	string expected = (input.inputs.size() > 4 && !input.inputs[4].IsNull())
	                      ? std::to_string(input.inputs[4].GetValue<int64_t>())
	                      : string();
	string payload = app_id + "\n" + std::to_string(version) + "\n" + expected;

	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, payload](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_SET_TXN_VERSION, table_ref, payload, out);
	};
	TxnVersionSchema(return_types, names);
	return std::move(bind_data);
}

// fabricator_delta_tblproperties(catalog, 'schema.table') / _set_tblproperties(catalog, 'schema.table',
// properties): read / SET the Delta table's delta.* properties. Both return (property, value) VARCHAR rows.
static void TblPropertiesSchema(vector<LogicalType> &return_types, vector<string> &names) {
	return_types = {LogicalType::VARCHAR, LogicalType::VARCHAR};
	names = {"property", "value"};
}

static unique_ptr<FunctionData> TblPropertiesBind(ClientContext &context, TableFunctionBindInput &input,
                                                  vector<LogicalType> &return_types, vector<string> &names) {
	auto catalog_name = input.inputs[0].GetValue<string>();
	auto table_ref = input.inputs[1].GetValue<string>();
	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_TBLPROPERTIES, table_ref, "", out);
	};
	TblPropertiesSchema(return_types, names);
	return std::move(bind_data);
}

static unique_ptr<FunctionData> SetTblPropertiesBind(ClientContext &context, TableFunctionBindInput &input,
                                                     vector<LogicalType> &return_types, vector<string> &names) {
	auto catalog_name = input.inputs[0].GetValue<string>();
	auto table_ref = input.inputs[1].GetValue<string>();
	auto properties = input.inputs[2].GetValue<string>(); // JSON object property->value (null = unset)
	auto bind_data = make_uniq<FabricatorFunctionsBindData>();
	bind_data->handle = ResolveConnection(context, catalog_name, bind_data->owns_handle);
	auto handle = bind_data->handle;
	bind_data->factory = [handle, table_ref, properties](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_SET_TBLPROPERTIES, table_ref, properties, out);
	};
	TblPropertiesSchema(return_types, names);
	return std::move(bind_data);
}

// --- fabricator_exec(connection_string VARCHAR, sql VARCHAR) -> BIGINT --------
// Executes arbitrary T-SQL (DDL/DML/EXEC) against SQL Server and returns the
// number of rows affected. Volatile (always executed, never constant-folded).
static void FabricatorExecFunction(DataChunk &args, ExpressionState &state, Vector &result) {
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
			// fabricator_exec is a raw passthrough. In JOIN-ONLY mode it runs on the active transaction's pinned
			// connection IFF a DuckDB-managed write is already in flight in this transaction (e.g. a dbt model's
			// CTAS, with the exec in a post-hook) — then it is atomic with the transaction and sees its
			// uncommitted writes. Otherwise it autocommits on its own connection (a raw exec's string-arg target
			// never triggers the catalog's transaction lifecycle, so nothing would ever commit a pinned
			// connection). See docs/dbt-hooks.md. handle is unused by set_active_txn (the ambient is per-thread).
			fabricator::SetActiveTxn(handle, (int64_t)MetaTransaction::Get(context).global_transaction_id,
			                       /*join_only=*/true);
			// Host-FS opener for a raw exec against a host-FS provider (the Delta catalog's OPTIMIZE/VACUUM read +
			// write the _delta_log/data through DuckDB's FileSystem). No-op for SQL Server / delta-rs (they ignore it).
			fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
			result_data[i] = fabricator::ExecuteDml(handle, StringValue::Get(sql_value), &schema_may_change);
		} catch (...) {
			if (owns) {
				fabricator::CloseCatalog(handle);
			}
			throw;
		}
		if (owns) {
			fabricator::CloseCatalog(handle);
		}

		// owns == false => the arg named an attached fabricator catalog whose cache we own.
		if (invalidate_on_ddl && schema_may_change && !owns) {
			auto db = DatabaseManager::Get(context).GetDatabase(context, conn_name);
			if (db && FabricatorCatalog::Is(db->GetCatalog())) {
				db->GetCatalog().Cast<FabricatorCatalog>().RefreshCache(context);
			}
		}
	}
}

// --- fabricator_refresh_cache(catalog_name VARCHAR) -> BOOLEAN -------------------
// Re-discovers the attached catalog's schemas/tables so out-of-band DDL (e.g.
// via fabricator_exec) becomes visible. Compatible with the C++ mssql extension.
static void FabricatorRefreshCacheFunction(DataChunk &args, ExpressionState &state, Vector &result) {
	auto &context = state.GetContext();
	auto count = args.size();
	result.SetVectorType(VectorType::FLAT_VECTOR);
	auto result_data = FlatVector::GetData<bool>(result);
	for (idx_t i = 0; i < count; i++) {
		auto value = args.GetValue(0, i);
		if (value.IsNull()) {
			throw InvalidInputException("fabricator_refresh_cache: catalog name is required (got NULL)");
		}
		auto name = StringValue::Get(value);
		auto &catalog = Catalog::GetCatalog(context, name);
		if (!FabricatorCatalog::Is(catalog)) {
			throw BinderException("fabricator_refresh_cache: catalog '%s' is not a fabricator catalog (type: %s)", name,
			                      catalog.GetCatalogType());
		}
		// fabricator_invalidate_cache(catalog, name_regex): a non-empty 2nd arg is a name pattern — SCOPED
		// invalidation of only the matching objects (drop their materialized entries; they re-fetch / self-heal
		// on next access), UNBOUNDED by the ATTACH filter, leaving the rest of the cache warm. Refresh only what
		// you touched via fabricator_exec. No pattern (arity 1, or an empty/NULL 2nd arg) => full re-discovery
		// of the whole catalog within its filtered enumeration baseline. The optional 3rd arg is legacy, ignored.
		string pattern;
		if (args.ColumnCount() >= 2) {
			auto pat_value = args.GetValue(1, i);
			if (!pat_value.IsNull()) {
				pattern = StringValue::Get(pat_value);
			}
		}
		if (!pattern.empty()) {
			catalog.Cast<FabricatorCatalog>().InvalidateMatching(pattern);
		} else {
			catalog.Cast<FabricatorCatalog>().RefreshCache(context);
		}
		result_data[i] = true;
	}
}

static void LoadInternal(ExtensionLoader &loader) {
	// SPIKE: install the host FileSystem callbacks FIRST (before any bridge boot) + register
	// fabricator_fs_spike(path). Foundation for a managed lakehouse reader doing secret-backed remote IO.
	RegisterFsSpike(loader);
	// fabricator_delta_scan(path) is now a connection-free GLOBAL host-FS table function (registered by
	// RegisterFabricatorGlobalFunctions below, dispatched to the managed engineered-wood reader) — no bespoke
	// C++ registration needed. See docs/global-functions.md §host-FS.
	// fabricator_host_query(sql) — run a query on a fresh host connection, result as Arrow (reuse DuckDB).
	RegisterHostQuery(loader);
	// CREATE SECRET ... (TYPE mssql, host '...', ...) — secret type(s) + fields declared in C#
	RegisterProviderSecrets(loader);
	// ATTACH '<connstr>' AS db (TYPE fabricator) — or ATTACH '' (TYPE fabricator, SECRET name)
	RegisterFabricatorStorageExtension(loader);
	// COPY ... TO 'mssql://...' (FORMAT mssql)
	RegisterFabricatorCopyFunction(loader);

	loader.RegisterFunction(ScalarFunction("fabricator_version", {}, LogicalType::VARCHAR, FabricatorVersionFunction));
	loader.RegisterFunction(
	    ScalarFunction("fabricator_managed_dir", {}, LogicalType::VARCHAR, FabricatorManagedDirFunction));

	RegisterProviderSettings(loader);
	fabricator::RegisterOneLakeFileSystem(loader.GetDatabaseInstance()); // onelake:// VFS subsystem (docs/filesystem-bridge.md §3)
	fabricator::RegisterDeltaMultiFileScan(loader); // fabricator_delta_mfr_scan — native Delta read (docs/multifile-delta.md Phase A)
	RegisterFabricatorGlobalFunctions(loader); // connection-free global functions (docs/global-functions.md)
	RegisterFabricatorOptimizer(DBConfig::GetConfig(loader.GetDatabaseInstance()));
	RegisterFabricatorInOutFinalizer(DBConfig::GetConfig(loader.GetDatabaseInstance()));
	// VARIANT over the Arrow C boundary: registers the arrow.parquet.variant type extension so VARIANT
	// crosses every export/import path as the tagged transport struct (see fabricator_variant.hpp).
	fabricator::RegisterFabricatorVariantExtension(loader.GetDatabaseInstance());

	TableFunction test_scan("fabricator_test_scan", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan, TestScanBind,
	                        fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	test_scan.projection_pushdown = true;
	loader.RegisterFunction(test_scan);

	// Provider-agnostic user-facing surface (the binary hosts several providers); the first arg names an
	// attached catalog / connstr / secret of any provider.
	TableFunction query_fn("fabricator_query", {LogicalType::VARCHAR, LogicalType::VARCHAR},
	                       fabricator::ArrowStreamScan, QueryBind, fabricator::ArrowStreamInitGlobal,
	                       fabricator::ArrowStreamInitLocal);
	query_fn.projection_pushdown = true;
	loader.RegisterFunction(query_fn);

	// fabricator_functions(catalog|connstr) — lists discovered routines (diagnostic).
	TableFunction functions_fn("fabricator_functions", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan,
	                           FunctionsBind, fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	functions_fn.projection_pushdown = true; // arrow_ingest maps requested columns; required (see fabricator_query)
	loader.RegisterFunction(functions_fn);

	// fabricator_server_info(catalog|connstr) — the detected server capability profile (diagnostic).
	TableFunction server_info_fn("fabricator_server_info", {LogicalType::VARCHAR}, fabricator::ArrowStreamScan,
	                             ServerInfoBind, fabricator::ArrowStreamInitGlobal, fabricator::ArrowStreamInitLocal);
	server_info_fn.projection_pushdown = true;
	loader.RegisterFunction(server_info_fn);

	// fabricator_delta_snapshots(catalog, 'schema.table') — a Delta table's commit-history / snapshots view.
	TableFunction snapshots_fn("fabricator_delta_snapshots", {LogicalType::VARCHAR, LogicalType::VARCHAR},
	                           fabricator::ArrowStreamScan, SnapshotsBind, fabricator::ArrowStreamInitGlobal,
	                           fabricator::ArrowStreamInitLocal);
	snapshots_fn.projection_pushdown = true;
	loader.RegisterFunction(snapshots_fn);

	// fabricator_delta_changes(catalog, 'schema.table', from_version[, to_version]) — the Change Data Feed.
	// Two overloads: with an explicit end version, or without (=> latest).
	TableFunction changes_fn("fabricator_delta_changes",
	                         {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::BIGINT, LogicalType::BIGINT},
	                         fabricator::ArrowStreamScan, ChangesBind, fabricator::ArrowStreamInitGlobal,
	                         fabricator::ArrowStreamInitLocal);
	changes_fn.projection_pushdown = true;
	loader.RegisterFunction(changes_fn);
	changes_fn.arguments = {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::BIGINT};
	loader.RegisterFunction(changes_fn);

	// fabricator_delta_get/set_transaction_version — Delta application-transaction versions (idempotent
	// appends): get the committed high-water mark / park a CAS'd version on the current explicit txn.
	TableFunction get_txn_fn("fabricator_delta_get_transaction_version",
	                         {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR},
	                         fabricator::ArrowStreamScan, GetTxnVersionBind, fabricator::ArrowStreamInitGlobal,
	                         fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(get_txn_fn);
	TableFunction set_txn_fn("fabricator_delta_set_transaction_version",
	                         {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR,
	                          LogicalType::BIGINT, LogicalType::BIGINT},
	                         fabricator::ArrowStreamScan, SetTxnVersionBind, fabricator::ArrowStreamInitGlobal,
	                         fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(set_txn_fn);
	set_txn_fn.arguments = {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR,
	                        LogicalType::BIGINT}; // expected omitted => must-not-exist (first batch)
	loader.RegisterFunction(set_txn_fn);

	// fabricator_delta_tblproperties(catalog, 'schema.table') — read a Delta table's delta.* properties;
	// fabricator_delta_set_tblproperties(catalog, 'schema.table', properties) — SET/UNSET them (metaData commit).
	TableFunction tblprops_fn("fabricator_delta_tblproperties", {LogicalType::VARCHAR, LogicalType::VARCHAR},
	                          fabricator::ArrowStreamScan, TblPropertiesBind, fabricator::ArrowStreamInitGlobal,
	                          fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(tblprops_fn);
	TableFunction set_tblprops_fn("fabricator_delta_set_tblproperties",
	                              {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR},
	                              fabricator::ArrowStreamScan, SetTblPropertiesBind, fabricator::ArrowStreamInitGlobal,
	                              fabricator::ArrowStreamInitLocal);
	loader.RegisterFunction(set_tblprops_fn);

	ScalarFunction exec_fn("fabricator_exec", {LogicalType::VARCHAR, LogicalType::VARCHAR}, LogicalType::BIGINT,
	                       FabricatorExecFunction);
	exec_fn.stability = FunctionStability::VOLATILE;
	loader.RegisterFunction(exec_fn);

	// fabricator_refresh_cache(catalog) re-discovers the attached catalog's metadata.
	{
		ScalarFunction refresh_fn("fabricator_refresh_cache", {LogicalType::VARCHAR}, LogicalType::BOOLEAN,
		                          FabricatorRefreshCacheFunction);
		refresh_fn.stability = FunctionStability::VOLATILE;
		loader.RegisterFunction(refresh_fn);
	}

	// fabricator_invalidate_cache(catalog [, schema [, table]]). Our cache is catalog-granular, so every
	// arity re-discovers the whole catalog (a valid superset of point invalidation); the schema/table args
	// are accepted but coarsened. Only arg0 (the catalog) is read.
	{
		ScalarFunctionSet set("fabricator_invalidate_cache");
		const vector<vector<LogicalType>> signatures = {
		    {LogicalType::VARCHAR},
		    {LogicalType::VARCHAR, LogicalType::VARCHAR},
		    {LogicalType::VARCHAR, LogicalType::VARCHAR, LogicalType::VARCHAR}};
		for (auto &arg_types : signatures) {
			ScalarFunction fn("fabricator_invalidate_cache", arg_types, LogicalType::BOOLEAN,
			                  FabricatorRefreshCacheFunction);
			fn.stability = FunctionStability::VOLATILE;
			set.AddFunction(fn);
		}
		loader.RegisterFunction(set);
	}
}

void FabricatorExtension::Load(ExtensionLoader &loader) {
	LoadInternal(loader);
}

std::string FabricatorExtension::Name() {
	return "fabricator";
}

std::string FabricatorExtension::Version() const {
	return GetExtensionVersion();
}

} // namespace duckdb

extern "C" {

DUCKDB_CPP_EXTENSION_ENTRY(fabricator, loader) {
	duckdb::LoadInternal(loader);
}
}
