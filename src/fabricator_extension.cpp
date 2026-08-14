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

// The scope our options are REGISTERED with (AddExtensionOption's `default_scope`), and therefore the scope
// an unqualified `SET fabricator_x = …` resolves to. It is DuckDB's own default for extension options; it is
// named here because the trampoline below has to resolve SetScope::AUTOMATIC the same way DuckDB does, and
// the two must not drift.
static constexpr SetScope FABRICATOR_SETTING_DEFAULT_SCOPE = SetScope::SESSION;

// The lifetime hook for a session's settings. The SESSION KEY is a ClientContext ADDRESS, so leaving entries
// behind is not merely a leak: a later connection allocated at the same address would INHERIT a dead one's
// settings — a silent wrong answer, and one that would show up only under connection churn (a dbt run), i.e.
// exactly where it is hardest to attribute.
//
// A `ClientContextState` registered on the context is held by it for its whole life, so this object's
// DESTRUCTOR is the connection-close signal — there is no explicit close callback to hook. It is registered
// LAZILY, on the first session-scoped SET, so a connection that never sets anything costs nothing.
class FabricatorSessionSettingsState : public ClientContextState {
public:
	explicit FabricatorSessionSettingsState(int64_t session) : session_(session) {
	}
	~FabricatorSessionSettingsState() override {
		try {
			fabricator::ClearSessionSettings(session_);
		} catch (...) {
			// A destructor must not throw, and a store that can no longer be reached has nothing to clear.
		}
	}

private:
	int64_t session_;
};

// Key under which the state is registered on the ClientContext. Namespaced so it cannot collide with
// DuckDB's own or another extension's.
static constexpr const char *FABRICATOR_SESSION_STATE_KEY = "fabricator_session_settings";

// One set-callback per slot: validates an optional minimum (parity with the former RequireAtLeastOne), then
// best-effort pushes the new value to the managed store. DuckDB has already cast `value` to the option type.
//
// ⚠ THE SCOPE IS LOAD-BEARING AND WAS DISCARDED UNTIL ABI v69, WHICH WAS A DATA BUG RATHER THAN A MISSING
// FEATURE. MEASURED: `SET mssql_mars='false'` in DuckDB connection A made a same-catalog CTAS in connection
// B — which set nothing — return 10 rows instead of 15, the control (same script, no SET) returning 15. So a
// setting applied in one connection changed the DATA another connection saw. DuckDB stores the value
// per-connection on ITS side already (default_scope = SESSION, `client_config.user_settings`); only this push
// was process-wide. The practical consequence: configuring ONE dbt model via a pre-hook could not work — the
// value leaked to models running concurrently on other threads.
//
// ⚠ SET AND RESET HAND US THE SCOPE DIFFERENTLY, and only one of them can be AUTOMATIC.
// `PhysicalSet::SetExtensionVariable` calls us with the RAW scope and resolves AUTOMATIC afterwards, while
// `PhysicalReset::ResetExtensionVariable` resolves it BEFORE calling us. So we must resolve AUTOMATIC
// ourselves, and must resolve it to the same thing DuckDB will.
template <size_t I>
static void SettingTrampoline(ClientContext &context, SetScope scope, Value &value) {
	const SettingSlot &slot = g_setting_slots[I];
	if (slot.has_min && !value.IsNull() && value.GetValue<int64_t>() < slot.min_value) {
		throw InvalidInputException("fabricator: %s must be >= %lld", slot.name, (long long)slot.min_value);
	}
	if (scope == SetScope::AUTOMATIC) {
		scope = FABRICATOR_SETTING_DEFAULT_SCOPE;
	}
	// GLOBAL writes the process-wide layer (key 0); anything else is this connection's own.
	// ⚠ A RESET at session scope pushes the option's DEFAULT into the session layer rather than deleting the
	// session entry — which shadows a `SET GLOBAL` value instead of falling back to it. That looks wrong and
	// is exactly what DuckDB does one line later (`client_config.user_settings.SetUserSetting(idx, default)`),
	// so matching it keeps our view and DuckDB's `duckdb_settings()` view of the same setting in agreement.
	int64_t session = scope == SetScope::GLOBAL ? 0 : fabricator::SessionKeyFor(&context);
	if (session != 0 && context.registered_state) {
		// Arm the cleanup BEFORE the write, so a session entry can never exist without the state that
		// reclaims it. GetOrCreate is keyed and locked, so repeated SETs on one connection register exactly
		// one — the cost is per connection, not per setting.
		context.registered_state->GetOrCreate<FabricatorSessionSettingsState>(FABRICATOR_SESSION_STATE_KEY,
		                                                                      session);
	}
	try {
		if (value.IsNull()) {
			fabricator::SetSetting(slot.provider, slot.name, nullptr, session);
		} else {
			string rendered = value.ToString();
			fabricator::SetSetting(slot.provider, slot.name, rendered.c_str(), session);
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

			// The scope is passed EXPLICITLY although it is also DuckDB's default for extension options, so
			// that it and the trampoline's AUTOMATIC resolution read from one constant and cannot drift.
			config.AddExtensionOption(name, desc, lt, default_value, g_setting_trampolines[slot],
			                          FABRICATOR_SETTING_DEFAULT_SCOPE);

			// Seed the managed store with the default so reads see it before any SET. Session 0: a
			// registration default belongs to every connection, not to whichever one happened to load the
			// extension — and load-time here has no user connection to attribute it to anyway.
			if (!def.empty()) {
				try {
					fabricator::SetSetting(provider, name, def.c_str(), 0);
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

// The fabricator_delta_* table functions (snapshots / changes / get+set_transaction_version /
// tblproperties / set_tblproperties) were DELETED here (ABI v70): provider-specific surface does not
// belong in C++. They are catalog-bound functions in the `delta` schema now — cat.delta.snapshots('s.t')
// etc., declared by the Delta providers themselves (Fabricator.Bridge/DeltaFunctions.cs) with typed args.

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
			fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context),
			                            fabricator::SessionKeyFor(&context));
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

	// The eight fabricator_delta_* registrations were DELETED here (ABI v70) — see the note above
	// FabricatorExecFunction. Their replacements live in the `delta` schema of every attached Delta catalog.

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

// Second entry point for the SAME binary under the file name fabricator_core.duckdb_extension.
//
// DuckDB derives an extension's entry symbol AND its identity from the file name
// (ExtensionHelper::GetExtensionName -> the first dot-segment; extension_load.cpp:593-607,633), and
// ExtensionManager::BeginLoad takes a lock per extension NAME. The single-file distribution
// (docs/distribution-installer.md) has an installer extension named "fabricator" chain-load the
// extracted core during its own load, so the core MUST answer to a different name — a nested load of
// "fabricator" from inside "fabricator" would block on its own load lock. Exporting both spellings
// keeps one artifact serving both: the direct dev flow (LOAD 'fabricator.duckdb_extension') and the
// distributed flow (extracted as fabricator_core.duckdb_extension).
DUCKDB_CPP_EXTENSION_ENTRY(fabricator_core, loader) {
	duckdb::LoadInternal(loader);
}
}
