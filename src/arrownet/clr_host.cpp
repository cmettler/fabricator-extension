//===----------------------------------------------------------------------===//
//                         ArrowNet — CoreCLR host (impl)
//===----------------------------------------------------------------------===//

#include "arrownet/clr_host.hpp"

#include "duckdb/common/exception.hpp"

#include <cstdint>
#include <cstring>
#include <fstream>
#include <mutex>
#include <string>

#if defined(_WIN32)
#include <windows.h>
typedef wchar_t host_char_t;
#define ANET_CDECL __cdecl
#define ANET_STDCALL __stdcall
#else
#include <dlfcn.h>
#include <climits>
typedef char host_char_t;
#define ANET_CDECL
#define ANET_STDCALL
#endif

namespace arrownet {

// -----------------------------------------------------------------------------
// hostfxr / coreclr delegate signatures (trimmed from the .NET hosting headers)
// -----------------------------------------------------------------------------
// Self-contained deployments must be initialized via the command-line entry;
// hostfxr_initialize_for_runtime_config rejects self-contained components with
// "Initialization for self-contained components is not supported".
typedef int32_t(ANET_CDECL *hostfxr_initialize_for_dotnet_command_line_fn)(int argc, const host_char_t **argv,
                                                                           const void *parameters,
                                                                           void **host_context_handle);
typedef int32_t(ANET_CDECL *hostfxr_get_runtime_delegate_fn)(void *host_context_handle, int32_t type,
                                                             void **delegate);
typedef int32_t(ANET_CDECL *hostfxr_close_fn)(void *host_context_handle);

typedef int(ANET_STDCALL *load_assembly_and_get_function_pointer_fn)(const host_char_t *assembly_path,
                                                                     const host_char_t *type_name,
                                                                     const host_char_t *method_name,
                                                                     const host_char_t *delegate_type_name,
                                                                     void *reserved, void **out_fn);

// The managed Bootstrap.Initialize, exported [UnmanagedCallersOnly(Cdecl)].
typedef int32_t(ANET_CDECL *bootstrap_fn)(ArrowNetVTable *vtable, int32_t size);

static constexpr int kHdtLoadAssemblyAndGetFunctionPointer = 5;
static const host_char_t *const kUnmanagedCallersOnly = reinterpret_cast<const host_char_t *>(-1);

namespace {

// ---- string + path helpers ----
std::basic_string<host_char_t> ToHostString(const std::string &utf8) {
#if defined(_WIN32)
	if (utf8.empty()) {
		return std::wstring();
	}
	int len = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), nullptr, 0);
	std::wstring out((size_t)len, L'\0');
	// &out[0] (non-const since C++11) — std::wstring::data() is const before C++17,
	// and DuckDB compiles extensions with an older standard.
	MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), &out[0], len);
	return out;
#else
	return utf8;
#endif
}

std::string PathJoin(const std::string &dir, const char *leaf) {
#if defined(_WIN32)
	const char sep = '\\';
#else
	const char sep = '/';
#endif
	if (dir.empty()) {
		return leaf;
	}
	char last = dir.back();
	if (last == '/' || last == '\\') {
		return dir + leaf;
	}
	return dir + sep + leaf;
}

std::string DirName(const std::string &path) {
	size_t pos = path.find_last_of("/\\");
	return pos == std::string::npos ? std::string() : path.substr(0, pos);
}

// Directory of the currently-loaded extension binary (where the "arrownet"
// publish folder lives next to it). Uses the address of a local symbol.
std::string ThisModuleDirectory() {
#if defined(_WIN32)
	HMODULE hmod = nullptr;
	if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       reinterpret_cast<LPCWSTR>(&ThisModuleDirectory), &hmod) &&
	    hmod) {
		wchar_t buf[MAX_PATH];
		DWORD n = GetModuleFileNameW(hmod, buf, MAX_PATH);
		if (n > 0 && n < MAX_PATH) {
			int len = WideCharToMultiByte(CP_UTF8, 0, buf, (int)n, nullptr, 0, nullptr, nullptr);
			std::string path((size_t)len, '\0');
			WideCharToMultiByte(CP_UTF8, 0, buf, (int)n, &path[0], len, nullptr, nullptr);
			return DirName(path);
		}
	}
	return std::string();
#else
	Dl_info info;
	if (dladdr(reinterpret_cast<void *>(&ThisModuleDirectory), &info) && info.dli_fname) {
		return DirName(info.dli_fname);
	}
	return std::string();
#endif
}

std::string EnvOrEmpty(const char *name) {
#if defined(_WIN32)
	char *val = nullptr;
	size_t len = 0;
	if (_dupenv_s(&val, &len, name) == 0 && val) {
		std::string out(val);
		free(val);
		return out;
	}
	return std::string();
#else
	const char *val = std::getenv(name);
	return val ? std::string(val) : std::string();
#endif
}

bool FileExists(const std::string &path) {
	std::ifstream f(path.c_str());
	return f.good();
}

void *LoadLib(const std::string &path) {
#if defined(_WIN32)
	return reinterpret_cast<void *>(LoadLibraryW(ToHostString(path).c_str()));
#else
	return dlopen(path.c_str(), RTLD_LAZY | RTLD_LOCAL);
#endif
}

void *LoadSym(void *lib, const char *name) {
#if defined(_WIN32)
	return reinterpret_cast<void *>(GetProcAddress(reinterpret_cast<HMODULE>(lib), name));
#else
	return dlsym(lib, name);
#endif
}

const char *HostFxrLeaf() {
#if defined(_WIN32)
	return "hostfxr.dll";
#elif defined(__APPLE__)
	return "libhostfxr.dylib";
#else
	return "libhostfxr.so";
#endif
}

// ---- loaded state ----
std::once_flag g_once;
ArrowNetVTable g_vtable {};
std::string g_managed_dir;
std::string g_load_error;

std::string ResolveManagedDir() {
	std::string env = EnvOrEmpty("ARROWNET_MANAGED_DIR");
	if (!env.empty()) {
		return env;
	}
	return PathJoin(ThisModuleDirectory(), "arrownet");
}

void LoadOnce() {
	using duckdb::IOException;
	std::memset(&g_vtable, 0, sizeof(g_vtable));

	g_managed_dir = ResolveManagedDir();
	if (g_managed_dir.empty()) {
		g_load_error = "ArrowNet: could not determine managed directory";
		return;
	}

	std::string hostfxr_path = PathJoin(g_managed_dir, HostFxrLeaf());
	void *hostfxr = LoadLib(hostfxr_path);
	if (!hostfxr) {
		g_load_error = "ArrowNet: failed to load hostfxr from " + hostfxr_path;
		return;
	}

	auto init_fn = reinterpret_cast<hostfxr_initialize_for_dotnet_command_line_fn>(
	    LoadSym(hostfxr, "hostfxr_initialize_for_dotnet_command_line"));
	auto get_delegate_fn =
	    reinterpret_cast<hostfxr_get_runtime_delegate_fn>(LoadSym(hostfxr, "hostfxr_get_runtime_delegate"));
	auto close_fn = reinterpret_cast<hostfxr_close_fn>(LoadSym(hostfxr, "hostfxr_close"));
	if (!init_fn || !get_delegate_fn || !close_fn) {
		g_load_error = "ArrowNet: hostfxr is missing required exports";
		return;
	}

	// The runtime is initialized against the published "app" assembly (the one
	// carrying the .runtimeconfig.json). For the mssql_net product that is the
	// composition assembly ArrowNet.SqlServer; the bridge's Bootstrap type is
	// still resolved from ArrowNet.Bridge below. Falls back to the bridge when
	// the composition assembly is absent (e.g. a bridge-only publish).
	std::string app_name = EnvOrEmpty("ARROWNET_APP_DLL");
	if (app_name.empty()) {
		app_name = "ArrowNet.SqlServer.dll";
	}
	std::string app_dll = PathJoin(g_managed_dir, app_name.c_str());
	if (!FileExists(app_dll)) {
		app_dll = PathJoin(g_managed_dir, "ArrowNet.Bridge.dll");
	}
	std::string bridge_dll = PathJoin(g_managed_dir, "ArrowNet.Bridge.dll");
	auto app_dll_h = ToHostString(app_dll);
	auto bridge_dll_h = ToHostString(bridge_dll);

	void *ctx = nullptr;
	const host_char_t *argv[1] = {app_dll_h.c_str()};
	int32_t rc = init_fn(1, argv, nullptr, &ctx);
	// Negative codes are failures; small positive codes are success variants.
	if (rc < 0 || ctx == nullptr) {
		g_load_error = "ArrowNet: hostfxr_initialize_for_dotnet_command_line failed (0x" +
		               std::to_string((uint32_t)rc) + ") for " + app_dll;
		return;
	}

	void *load_assembly_fn_ptr = nullptr;
	rc = get_delegate_fn(ctx, kHdtLoadAssemblyAndGetFunctionPointer, &load_assembly_fn_ptr);
	if (rc != 0 || load_assembly_fn_ptr == nullptr) {
		close_fn(ctx);
		g_load_error = "ArrowNet: hostfxr_get_runtime_delegate failed (0x" + std::to_string((uint32_t)rc) + ")";
		return;
	}
	auto load_assembly = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(load_assembly_fn_ptr);

	auto type_h = ToHostString("ArrowNet.Bridge.Bootstrap, ArrowNet.Bridge");
	auto method_h = ToHostString("Initialize");

	void *bootstrap_ptr = nullptr;
	rc = load_assembly(bridge_dll_h.c_str(), type_h.c_str(), method_h.c_str(), kUnmanagedCallersOnly, nullptr,
	                   &bootstrap_ptr);
	if (rc != 0 || bootstrap_ptr == nullptr) {
		close_fn(ctx);
		g_load_error = "ArrowNet: failed to load Bootstrap.Initialize (0x" + std::to_string((uint32_t)rc) +
		               ") from " + bridge_dll;
		return;
	}

	auto bootstrap = reinterpret_cast<bootstrap_fn>(bootstrap_ptr);
	int32_t brc = bootstrap(&g_vtable, (int32_t)sizeof(ArrowNetVTable));
	if (brc != 0) {
		close_fn(ctx);
		g_load_error = "ArrowNet: Bootstrap.Initialize returned " + std::to_string(brc);
		return;
	}
	if (g_vtable.abi_version != ARROWNET_ABI_VERSION) {
		close_fn(ctx);
		g_load_error = "ArrowNet: ABI version mismatch (host=" + std::to_string(ARROWNET_ABI_VERSION) +
		               ", bridge=" + std::to_string(g_vtable.abi_version) + ")";
		return;
	}

	// Intentionally keep `ctx` open for the process lifetime; the runtime stays
	// resident and the vtable function pointers remain valid.
}

} // namespace

const ArrowNetVTable &GetBridge() {
	std::call_once(g_once, LoadOnce);
	if (!g_load_error.empty()) {
		throw duckdb::IOException(g_load_error);
	}
	return g_vtable;
}

const std::string &GetManagedDirectory() {
	return g_managed_dir;
}

// -----------------------------------------------------------------------------
// vtable convenience wrappers
// -----------------------------------------------------------------------------
namespace {

// Consume a managed error string (if any), free it, and throw.
[[noreturn]] void ThrowManagedError(const ArrowNetVTable &vt, char *err, const std::string &context) {
	std::string message = context;
	if (err) {
		message += ": ";
		message += err;
		if (vt.free_error) {
			vt.free_error(err);
		}
	}
	throw duckdb::IOException(message);
}

} // namespace

ArrowNetHandle OpenCatalog(const std::string &connection_string) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.open_catalog) {
		throw duckdb::IOException("ArrowNet: bridge does not provide open_catalog");
	}
	ArrowNetHandle handle = nullptr;
	char *err = nullptr;
	int32_t rc = vt.open_catalog(connection_string.c_str(), &handle, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: open_catalog failed");
	}
	return handle;
}

void CloseCatalog(ArrowNetHandle handle) {
	if (!handle) {
		return;
	}
	const ArrowNetVTable &vt = GetBridge();
	if (vt.close_catalog) {
		vt.close_catalog(handle);
	}
}

void ExecuteQuery(ArrowNetHandle handle, const std::string &sql, ArrowArrayStream &out) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.execute_query) {
		throw duckdb::IOException("ArrowNet: bridge does not provide execute_query");
	}
	char *err = nullptr;
	int32_t rc = vt.execute_query(handle, sql.c_str(), &out, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: execute_query failed");
	}
}

int64_t ExecuteDml(ArrowNetHandle handle, const std::string &sql, bool *schema_may_change) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.execute_dml) {
		throw duckdb::IOException("ArrowNet: bridge does not provide execute_dml");
	}
	int64_t affected = 0;
	int32_t schema_changed = 0;
	char *err = nullptr;
	int32_t rc = vt.execute_dml(handle, sql.c_str(), &affected, &schema_changed, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: execute_dml failed");
	}
	if (schema_may_change) {
		*schema_may_change = schema_changed != 0;
	}
	return affected;
}

int64_t BulkInsert(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool create_table,
                   bool replace, ArrowArrayStream &in) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.bulk_insert) {
		throw duckdb::IOException("ArrowNet: bridge does not provide bulk_insert");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.bulk_insert(handle, schema.c_str(), table.c_str(), create_table ? 1 : 0, replace ? 1 : 0, &in,
	                            &affected, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: bulk_insert failed");
	}
	return affected;
}

int64_t ExecuteDelete(ArrowNetHandle handle, const std::string &schema, const std::string &table,
                      ArrowArrayStream &keys) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.execute_delete) {
		throw duckdb::IOException("ArrowNet: bridge does not provide execute_delete");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.execute_delete(handle, schema.c_str(), table.c_str(), &keys, &affected, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: execute_delete failed");
	}
	return affected;
}

int64_t ExecuteUpdate(ArrowNetHandle handle, const std::string &schema, const std::string &table, int32_t set_count,
                      ArrowArrayStream &data) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.execute_update) {
		throw duckdb::IOException("ArrowNet: bridge does not provide execute_update");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.execute_update(handle, schema.c_str(), table.c_str(), set_count, &data, &affected, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: execute_update failed");
	}
	return affected;
}

void GetMetadata(ArrowNetHandle handle, int32_t kind, const std::string &arg1, const std::string &arg2,
                 ArrowArrayStream &out) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.get_metadata) {
		throw duckdb::IOException("ArrowNet: bridge does not provide get_metadata");
	}
	char *err = nullptr;
	int32_t rc = vt.get_metadata(handle, kind, arg1.empty() ? nullptr : arg1.c_str(),
	                             arg2.empty() ? nullptr : arg2.c_str(), &out, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: get_metadata failed");
	}
}

void ScanTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, const std::string &spec_json,
               ArrowArrayStream *filter_values, ArrowArrayStream &out) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.scan_table) {
		throw duckdb::IOException("ArrowNet: bridge does not provide scan_table");
	}
	char *err = nullptr;
	const char *spec = spec_json.empty() ? nullptr : spec_json.c_str();
	int32_t rc = vt.scan_table(handle, schema.c_str(), table.c_str(), spec, filter_values, &out, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: scan_table failed");
	}
}

void CreateTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &columns,
                 bool if_not_exists, const std::string &pk_columns, const std::string &unique_columns,
                 const std::string &defaults, const std::string &text_type) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.create_table) {
		throw duckdb::IOException("ArrowNet: bridge does not provide create_table");
	}
	char *err = nullptr;
	int32_t rc = vt.create_table(handle, schema.c_str(), table.c_str(), &columns, if_not_exists ? 1 : 0,
	                             pk_columns.empty() ? nullptr : pk_columns.c_str(),
	                             unique_columns.empty() ? nullptr : unique_columns.c_str(),
	                             defaults.empty() ? nullptr : defaults.c_str(),
	                             text_type.empty() ? nullptr : text_type.c_str(), &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: create_table failed");
	}
}

void DropTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool if_exists) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.drop_table) {
		throw duckdb::IOException("ArrowNet: bridge does not provide drop_table");
	}
	char *err = nullptr;
	int32_t rc = vt.drop_table(handle, schema.c_str(), table.c_str(), if_exists ? 1 : 0, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: drop_table failed");
	}
}

void CreateSchema(ArrowNetHandle handle, const std::string &schema, bool if_not_exists) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.create_schema) {
		throw duckdb::IOException("ArrowNet: bridge does not provide create_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.create_schema(handle, schema.c_str(), if_not_exists ? 1 : 0, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: create_schema failed");
	}
}

void DropSchema(ArrowNetHandle handle, const std::string &schema, bool if_exists) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.drop_schema) {
		throw duckdb::IOException("ArrowNet: bridge does not provide drop_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.drop_schema(handle, schema.c_str(), if_exists ? 1 : 0, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: drop_schema failed");
	}
}

void AlterTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, int32_t alter_kind,
                const std::string &arg1, const std::string &arg2, ArrowArrayStream *column, int32_t flags) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.alter_table) {
		throw duckdb::IOException("ArrowNet: bridge does not provide alter_table");
	}
	char *err = nullptr;
	int32_t rc = vt.alter_table(handle, schema.c_str(), table.c_str(), alter_kind,
	                            arg1.empty() ? nullptr : arg1.c_str(), arg2.empty() ? nullptr : arg2.c_str(), column,
	                            flags, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: alter_table failed");
	}
}

void BeginTransaction(ArrowNetHandle handle) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.begin_transaction) {
		throw duckdb::IOException("ArrowNet: bridge does not provide begin_transaction");
	}
	char *err = nullptr;
	if (vt.begin_transaction(handle, &err) != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: begin_transaction failed");
	}
}

void CommitTransaction(ArrowNetHandle handle) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.commit_transaction) {
		throw duckdb::IOException("ArrowNet: bridge does not provide commit_transaction");
	}
	char *err = nullptr;
	if (vt.commit_transaction(handle, &err) != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: commit_transaction failed");
	}
}

void RollbackTransaction(ArrowNetHandle handle) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.rollback_transaction) {
		throw duckdb::IOException("ArrowNet: bridge does not provide rollback_transaction");
	}
	char *err = nullptr;
	if (vt.rollback_transaction(handle, &err) != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: rollback_transaction failed");
	}
}

void InsertReturning(ArrowNetHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &in,
                     ArrowArrayStream &out) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.insert_returning) {
		throw duckdb::IOException("ArrowNet: bridge does not provide insert_returning");
	}
	char *err = nullptr;
	int32_t rc = vt.insert_returning(handle, schema.c_str(), table.c_str(), &in, &out, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: insert_returning failed");
	}
}

ArrowNetHandle BeginBulk(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool create_table,
                         bool replace, bool check_constraints, ArrowSchema &schema_in) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.begin_bulk) {
		throw duckdb::IOException("ArrowNet: bridge does not provide begin_bulk");
	}
	ArrowNetHandle session = nullptr;
	char *err = nullptr;
	int32_t rc = vt.begin_bulk(handle, schema.c_str(), table.c_str(), create_table ? 1 : 0, replace ? 1 : 0,
	                           check_constraints ? 1 : 0, &schema_in, &session, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: begin_bulk failed");
	}
	return session;
}

void PushBatch(ArrowNetHandle session, ArrowArray &batch) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.push_batch) {
		throw duckdb::IOException("ArrowNet: bridge does not provide push_batch");
	}
	char *err = nullptr;
	int32_t rc = vt.push_batch(session, &batch, &err);
	if (rc != ARROWNET_OK) {
		ThrowManagedError(vt, err, "ArrowNet: push_batch failed");
	}
}

int64_t CompleteBulk(ArrowNetHandle session, bool abort) {
	const ArrowNetVTable &vt = GetBridge();
	if (!vt.complete_bulk) {
		throw duckdb::IOException("ArrowNet: bridge does not provide complete_bulk");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.complete_bulk(session, abort ? 1 : 0, &affected, &err);
	if (rc != ARROWNET_OK) {
		if (abort) {
			// Cleanup path: don't mask the original failure with the abort's error.
			if (err && vt.free_error) {
				vt.free_error(err);
			}
			return 0;
		}
		ThrowManagedError(vt, err, "ArrowNet: complete_bulk failed");
	}
	return affected;
}

} // namespace arrownet
