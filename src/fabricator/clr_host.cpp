//===----------------------------------------------------------------------===//
//                         Fabricator — CoreCLR host (impl)
//===----------------------------------------------------------------------===//

#include "fabricator/clr_host.hpp"

#include "fabricator/arrow_produce.hpp"

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
#include <dirent.h>
#include <sys/stat.h>
typedef char host_char_t;
#define ANET_CDECL
#define ANET_STDCALL
#endif

#include <vector>

namespace fabricator {

// -----------------------------------------------------------------------------
// hostfxr / coreclr delegate signatures (trimmed from the .NET hosting headers)
// -----------------------------------------------------------------------------
// Self-contained deployments must be initialized via the command-line entry;
// hostfxr_initialize_for_runtime_config rejects self-contained components with
// "Initialization for self-contained components is not supported".
// Optional init parameters: `dotnet_root` selects WHICH .NET install resolves the frameworks — the hook the
// framework-dependent deployment uses (no process-env mutation needed).
struct hostfxr_initialize_parameters {
	size_t size;
	const host_char_t *host_path;
	const host_char_t *dotnet_root;
};

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

// The managed Bootstrap.Initialize, exported [UnmanagedCallersOnly(Cdecl)]. `host` carries the host-services
// callbacks (reverse direction) the managed side caches; may be a zeroed block if none were registered.
typedef int32_t(ANET_CDECL *bootstrap_fn)(FabricatorVTable *vtable, int32_t size, const FabricatorHostServices *host);

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

// Directory of the currently-loaded extension binary (where the "fabricator"
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

// ---- framework-dependent hosting: locate a PROVIDED .NET install ----

bool DirExists(const std::string &path) {
#if defined(_WIN32)
	DWORD attrs = GetFileAttributesW(ToHostString(path).c_str());
	return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY);
#else
	struct stat st;
	return stat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode);
#endif
}

std::vector<std::string> ListSubdirs(const std::string &dir) {
	std::vector<std::string> names;
#if defined(_WIN32)
	WIN32_FIND_DATAW fd;
	HANDLE h = FindFirstFileW((ToHostString(dir) + L"\\*").c_str(), &fd);
	if (h != INVALID_HANDLE_VALUE) {
		do {
			if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && fd.cFileName[0] != L'.') {
				int len = WideCharToMultiByte(CP_UTF8, 0, fd.cFileName, -1, nullptr, 0, nullptr, nullptr);
				std::string name((size_t)len, '\0');
				WideCharToMultiByte(CP_UTF8, 0, fd.cFileName, -1, &name[0], len, nullptr, nullptr);
				while (!name.empty() && name.back() == '\0') {
					name.pop_back();
				}
				names.push_back(name);
			}
		} while (FindNextFileW(h, &fd));
		FindClose(h);
	}
#else
	DIR *d = opendir(dir.c_str());
	if (d) {
		while (struct dirent *e = readdir(d)) {
			if (e->d_name[0] == '.') {
				continue;
			}
			if (DirExists(PathJoin(dir, e->d_name))) {
				names.push_back(e->d_name);
			}
		}
		closedir(d);
	}
#endif
	return names;
}

// Numeric dotted-version compare ("10.0.7" > "8.0.26"); a prerelease suffix ("-preview…") stops the parse
// of that segment, which is good enough for picking the newest hostfxr.
bool VersionLess(const std::string &a, const std::string &b) {
	size_t ia = 0, ib = 0;
	while (ia < a.size() || ib < b.size()) {
		long va = 0, vb = 0;
		while (ia < a.size() && a[ia] >= '0' && a[ia] <= '9') {
			va = va * 10 + (a[ia++] - '0');
		}
		while (ib < b.size() && b[ib] >= '0' && b[ib] <= '9') {
			vb = vb * 10 + (b[ib++] - '0');
		}
		if (va != vb) {
			return va < vb;
		}
		while (ia < a.size() && a[ia] != '.') {
			ia++;
		}
		while (ib < b.size() && b[ib] != '.') {
			ib++;
		}
		if (ia < a.size()) {
			ia++;
		}
		if (ib < b.size()) {
			ib++;
		}
	}
	return false;
}

// The hostfxr of a .NET install: <root>/host/fxr/<highest version>/<hostfxr lib>. Empty when absent.
std::string FindHostFxrInRoot(const std::string &root) {
	std::string fxr_dir = PathJoin(PathJoin(root, "host"), "fxr");
	if (!DirExists(fxr_dir)) {
		return std::string();
	}
	std::string best;
	for (auto &name : ListSubdirs(fxr_dir)) {
		if (best.empty() || VersionLess(best, name)) {
			best = name;
		}
	}
	if (best.empty()) {
		return std::string();
	}
	std::string candidate = PathJoin(PathJoin(fxr_dir, best.c_str()), HostFxrLeaf());
	return FileExists(candidate) ? candidate : std::string();
}

// Resolve the .NET install a framework-dependent payload should run on:
// FABRICATOR_DOTNET_ROOT (explicit override — e.g. a private .NET 10 next to a global .NET 8) >
// DOTNET_ROOT (the standard env) > the platform's global install locations. `probed` collects what was
// tried, for the error message.
std::string ResolveDotnetRoot(std::string &probed) {
	const char *env_names[] = {"FABRICATOR_DOTNET_ROOT", "DOTNET_ROOT"};
	for (auto *name : env_names) {
		std::string root = EnvOrEmpty(name);
		if (!root.empty()) {
			probed += std::string(name) + "=" + root + "; ";
			if (DirExists(root)) {
				return root;
			}
		}
	}
#if defined(_WIN32)
	std::string pf = EnvOrEmpty("ProgramFiles");
	if (!pf.empty()) {
		std::string root = PathJoin(pf, "dotnet");
		probed += root + "; ";
		if (DirExists(root)) {
			return root;
		}
	}
#else
	// /etc/dotnet/install_location holds the registered install dir (one path per line; optional
	// arch-suffixed variants exist — the plain file is the common case, incl. Azure Linux/Mariner).
	{
		std::ifstream f("/etc/dotnet/install_location");
		std::string line;
		if (f.good() && std::getline(f, line)) {
			while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' ')) {
				line.pop_back();
			}
			probed += "install_location=" + line + "; ";
			if (!line.empty() && DirExists(line)) {
				return line;
			}
		}
	}
	for (auto *root : {"/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet"}) {
		probed += std::string(root) + "; ";
		if (DirExists(root)) {
			return root;
		}
	}
#endif
	return std::string();
}

// ---- loaded state ----
std::once_flag g_once;
FabricatorVTable g_vtable {};
// Host-services callbacks the managed side calls (reverse direction). Populated by SetHostServices BEFORE the
// bridge boots; passed to Bootstrap.Initialize. Zeroed if no host registered any (the managed side then
// treats host services as unavailable).
FabricatorHostServices g_host_services {};
std::string g_managed_dir;
std::string g_load_error;

std::string ResolveManagedDir() {
	std::string env = EnvOrEmpty("FABRICATOR_MANAGED_DIR");
	if (!env.empty()) {
		return env;
	}
	return PathJoin(ThisModuleDirectory(), "fabricator");
}

void LoadOnce() {
	using duckdb::IOException;
	std::memset(&g_vtable, 0, sizeof(g_vtable));

	g_managed_dir = ResolveManagedDir();
	if (g_managed_dir.empty()) {
		g_load_error = "Fabricator: could not determine managed directory";
		return;
	}

	// Deployment-mode detection: a SELF-CONTAINED publish carries its own hostfxr next to the assemblies;
	// a FRAMEWORK-DEPENDENT publish doesn't — then a PROVIDED .NET install is resolved
	// (FABRICATOR_DOTNET_ROOT > DOTNET_ROOT > the global install) and ITS hostfxr is used, with the install
	// passed as `dotnet_root` so the runtimeconfig (net8.0, rollForward=LatestMajor) resolves against it.
	std::string hostfxr_path = PathJoin(g_managed_dir, HostFxrLeaf());
	std::string dotnet_root; // non-empty => framework-dependent
	if (!FileExists(hostfxr_path)) {
		std::string probed;
		dotnet_root = ResolveDotnetRoot(probed);
#if defined(_WIN32)
		// CoreCLR rejects a dotnet_root containing FORWARD slashes with E_INVALIDARG at CreateCoreCLR
		// (framework RESOLUTION tolerates them — the failure is late and cryptic). Normalize.
		for (auto &c : dotnet_root) {
			if (c == '/') {
				c = '\\';
			}
		}
#endif
		if (dotnet_root.empty()) {
			g_load_error = "Fabricator: framework-dependent layout (no " + std::string(HostFxrLeaf()) + " in " +
			               g_managed_dir + ") but no .NET install found — set FABRICATOR_DOTNET_ROOT (probed: " +
			               probed + ")";
			return;
		}
		hostfxr_path = FindHostFxrInRoot(dotnet_root);
		if (hostfxr_path.empty()) {
			g_load_error = "Fabricator: no hostfxr under " + dotnet_root +
			               "/host/fxr — is this a .NET runtime install? (set FABRICATOR_DOTNET_ROOT to one)";
			return;
		}
	}
	void *hostfxr = LoadLib(hostfxr_path);
	if (!hostfxr) {
		g_load_error = "Fabricator: failed to load hostfxr from " + hostfxr_path;
		return;
	}

	auto init_fn = reinterpret_cast<hostfxr_initialize_for_dotnet_command_line_fn>(
	    LoadSym(hostfxr, "hostfxr_initialize_for_dotnet_command_line"));
	auto get_delegate_fn =
	    reinterpret_cast<hostfxr_get_runtime_delegate_fn>(LoadSym(hostfxr, "hostfxr_get_runtime_delegate"));
	auto close_fn = reinterpret_cast<hostfxr_close_fn>(LoadSym(hostfxr, "hostfxr_close"));
	if (!init_fn || !get_delegate_fn || !close_fn) {
		g_load_error = "Fabricator: hostfxr is missing required exports";
		return;
	}

	// The runtime is initialized against the published "app" assembly (the one
	// carrying the .runtimeconfig.json). For the fabricator product that is the
	// composition assembly Fabricator.SqlServer; the bridge's Bootstrap type is
	// still resolved from Fabricator.Bridge below. Falls back to the bridge when
	// the composition assembly is absent (e.g. a bridge-only publish).
	std::string app_name = EnvOrEmpty("FABRICATOR_APP_DLL");
	if (app_name.empty()) {
		app_name = "Fabricator.SqlServer.dll";
	}
	std::string app_dll = PathJoin(g_managed_dir, app_name.c_str());
	if (!FileExists(app_dll)) {
		app_dll = PathJoin(g_managed_dir, "Fabricator.Bridge.dll");
	}
	std::string bridge_dll = PathJoin(g_managed_dir, "Fabricator.Bridge.dll");
	auto app_dll_h = ToHostString(app_dll);
	auto bridge_dll_h = ToHostString(bridge_dll);

	void *ctx = nullptr;
	const host_char_t *argv[1] = {app_dll_h.c_str()};
	// Framework-dependent: pass the resolved install as dotnet_root so hostfxr resolves the shared
	// framework THERE (an explicit FABRICATOR_DOTNET_ROOT wins over any global install — e.g. a private
	// .NET 10 beside a machine-wide .NET 8). Self-contained: no parameters, exactly as before.
	auto dotnet_root_h = ToHostString(dotnet_root);
	hostfxr_initialize_parameters params {};
	params.size = sizeof(params);
	// host_path = the NATIVE host executable; null lets hostfxr use the current process (we are a library
	// loaded into duckdb/python — the managed dll is NOT a valid host_path and CoreCLR rejects it).
	params.host_path = nullptr;
	params.dotnet_root = dotnet_root_h.c_str();
	int32_t rc = init_fn(1, argv, dotnet_root.empty() ? nullptr : &params, &ctx);
	// Negative codes are failures; small positive codes are success variants.
	if (rc < 0 || ctx == nullptr) {
		g_load_error = "Fabricator: hostfxr_initialize_for_dotnet_command_line failed (0x" +
		               std::to_string((uint32_t)rc) + ") for " + app_dll +
		               (dotnet_root.empty() ? "" : " (dotnet_root=" + dotnet_root + ")");
		return;
	}

	void *load_assembly_fn_ptr = nullptr;
	rc = get_delegate_fn(ctx, kHdtLoadAssemblyAndGetFunctionPointer, &load_assembly_fn_ptr);
	if (rc != 0 || load_assembly_fn_ptr == nullptr) {
		close_fn(ctx);
		g_load_error = "Fabricator: hostfxr_get_runtime_delegate failed (0x" + std::to_string((uint32_t)rc) + ")";
		return;
	}
	auto load_assembly = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(load_assembly_fn_ptr);

	auto type_h = ToHostString("Fabricator.Bridge.Bootstrap, Fabricator.Bridge");
	auto method_h = ToHostString("Initialize");

	void *bootstrap_ptr = nullptr;
	rc = load_assembly(bridge_dll_h.c_str(), type_h.c_str(), method_h.c_str(), kUnmanagedCallersOnly, nullptr,
	                   &bootstrap_ptr);
	if (rc != 0 || bootstrap_ptr == nullptr) {
		close_fn(ctx);
		g_load_error = "Fabricator: failed to load Bootstrap.Initialize (0x" + std::to_string((uint32_t)rc) +
		               ") from " + bridge_dll;
		return;
	}

	auto bootstrap = reinterpret_cast<bootstrap_fn>(bootstrap_ptr);
	int32_t brc = bootstrap(&g_vtable, (int32_t)sizeof(FabricatorVTable), &g_host_services);
	if (brc != 0) {
		close_fn(ctx);
		g_load_error = "Fabricator: Bootstrap.Initialize returned " + std::to_string(brc);
		return;
	}
	if (g_vtable.abi_version != FABRICATOR_ABI_VERSION) {
		close_fn(ctx);
		g_load_error = "Fabricator: ABI version mismatch (host=" + std::to_string(FABRICATOR_ABI_VERSION) +
		               ", bridge=" + std::to_string(g_vtable.abi_version) + ")";
		return;
	}

	// Intentionally keep `ctx` open for the process lifetime; the runtime stays
	// resident and the vtable function pointers remain valid.
}

} // namespace

const FabricatorVTable &GetBridge() {
	std::call_once(g_once, LoadOnce);
	if (!g_load_error.empty()) {
		throw duckdb::IOException(g_load_error);
	}
	return g_vtable;
}

const std::string &GetManagedDirectory() {
	return g_managed_dir;
}

void SetHostServices(const FabricatorHostServices &services) {
	g_host_services = services; // must be called before the first GetBridge() (bridge boot)
}

void SetHostQueryService(HostQueryFn fn, HostQueryInterruptFn interrupt_fn, HostQueryInterruptFn free_fn) {
	// Patches the host_query trio on the shared host-services block (the fs services set the rest).
	// Both happen at extension load, before the bridge boots — order-independent.
	g_host_services.host_query = fn;
	g_host_services.host_query_interrupt = interrupt_fn;
	g_host_services.host_query_interrupt_free = free_fn;
}

void SetHostLog(HostLogFn fn) {
	// Patches the host_log field (DuckDB internal-logging forward), like SetHostQueryService. At extension load,
	// before the bridge boots.
	g_host_services.host_log = fn;
}

// -----------------------------------------------------------------------------
// vtable convenience wrappers
// -----------------------------------------------------------------------------
namespace {

// Consume a managed error string (if any), free it, and throw.
[[noreturn]] void ThrowManagedError(const FabricatorVTable &vt, char *err, const std::string &context) {
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

FabricatorHandle OpenCatalog(const std::string &connection_string, const std::string &provider,
                           const std::string &options_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.open_catalog) {
		throw duckdb::IOException("Fabricator: bridge does not provide open_catalog");
	}
	FabricatorHandle handle = nullptr;
	char *err = nullptr;
	int32_t rc = vt.open_catalog(provider.empty() ? nullptr : provider.c_str(), connection_string.c_str(),
	                             options_json.empty() ? nullptr : options_json.c_str(), &handle, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: open_catalog failed");
	}
	return handle;
}

std::string BuildConnectionString(const std::string &provider, const std::string &secret_type,
                                  const std::string &fields_json, const std::string &base_connstr) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.build_connection_string) {
		throw duckdb::IOException("Fabricator: bridge does not provide build_connection_string");
	}
	char *out_connstr = nullptr;
	char *err = nullptr;
	int32_t rc = vt.build_connection_string(provider.empty() ? nullptr : provider.c_str(),
	                                        secret_type.empty() ? nullptr : secret_type.c_str(), fields_json.c_str(),
	                                        base_connstr.empty() ? nullptr : base_connstr.c_str(), &out_connstr, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: build_connection_string failed");
	}
	std::string result = out_connstr ? out_connstr : "";
	if (out_connstr && vt.free_error) {
		vt.free_error(out_connstr); // owned UTF-8, freed like an error string
	}
	return result;
}

std::string GetCapabilities(FabricatorHandle handle) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.get_capabilities) {
		throw duckdb::IOException("Fabricator: bridge does not provide get_capabilities");
	}
	char *out_json = nullptr;
	char *err = nullptr;
	int32_t rc = vt.get_capabilities(handle, &out_json, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: get_capabilities failed");
	}
	std::string result = out_json ? out_json : "";
	if (out_json && vt.free_error) {
		vt.free_error(out_json); // owned UTF-8, freed like an error string
	}
	return result;
}

void CloseCatalog(FabricatorHandle handle) {
	if (!handle) {
		return;
	}
	const FabricatorVTable &vt = GetBridge();
	if (vt.close_catalog) {
		vt.close_catalog(handle);
	}
}

void ExecuteQuery(FabricatorHandle handle, const std::string &sql, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.execute_query) {
		throw duckdb::IOException("Fabricator: bridge does not provide execute_query");
	}
	char *err = nullptr;
	int32_t rc = vt.execute_query(handle, sql.c_str(), &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: execute_query failed");
	}
}

int64_t ExecuteDml(FabricatorHandle handle, const std::string &sql, bool *schema_may_change) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.execute_dml) {
		throw duckdb::IOException("Fabricator: bridge does not provide execute_dml");
	}
	int64_t affected = 0;
	int32_t schema_changed = 0;
	char *err = nullptr;
	int32_t rc = vt.execute_dml(handle, sql.c_str(), &affected, &schema_changed, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: execute_dml failed");
	}
	if (schema_may_change) {
		*schema_may_change = schema_changed != 0;
	}
	return affected;
}

int64_t BulkInsert(FabricatorHandle handle, const std::string &schema, const std::string &table, bool create_table,
                   bool replace, ArrowArrayStream &in) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.bulk_insert) {
		throw duckdb::IOException("Fabricator: bridge does not provide bulk_insert");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.bulk_insert(handle, schema.c_str(), table.c_str(), create_table ? 1 : 0, replace ? 1 : 0, &in,
	                            &affected, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: bulk_insert failed");
	}
	return affected;
}

int64_t ExecuteDelete(FabricatorHandle handle, const std::string &schema, const std::string &table,
                      ArrowArrayStream &keys) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.execute_delete) {
		throw duckdb::IOException("Fabricator: bridge does not provide execute_delete");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.execute_delete(handle, schema.c_str(), table.c_str(), &keys, &affected, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: execute_delete failed");
	}
	return affected;
}

int64_t ExecuteUpdate(FabricatorHandle handle, const std::string &schema, const std::string &table, int32_t set_count,
                      ArrowArrayStream &data) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.execute_update) {
		throw duckdb::IOException("Fabricator: bridge does not provide execute_update");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.execute_update(handle, schema.c_str(), table.c_str(), set_count, &data, &affected, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: execute_update failed");
	}
	return affected;
}

void GetMetadata(FabricatorHandle handle, int32_t kind, const std::string &arg1, const std::string &arg2,
                 ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.get_metadata) {
		throw duckdb::IOException("Fabricator: bridge does not provide get_metadata");
	}
	char *err = nullptr;
	int32_t rc = vt.get_metadata(handle, kind, arg1.empty() ? nullptr : arg1.c_str(),
	                             arg2.empty() ? nullptr : arg2.c_str(), &out, &err);
	if (rc == FABRICATOR_NOT_FOUND) {
		// ABSENCE, kept distinct from failure all the way to the catalog. The status has existed in abi.h
		// since the beginning and was never produced or consumed; wiring it is what lets the entry
		// materialization stop treating every unreadable table as a deleted one.
		std::string message = "Fabricator: get_metadata failed";
		if (err) {
			message += ": ";
			message += err;
			if (vt.free_error) {
				vt.free_error(err);
			}
		}
		throw ObjectNotFoundException(message);
	}
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: get_metadata failed");
	}
}

void ListSettings(ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.list_settings) {
		throw duckdb::IOException("Fabricator: bridge does not provide list_settings");
	}
	char *err = nullptr;
	int32_t rc = vt.list_settings(&out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: list_settings failed");
	}
}

void ListGlobalFunctions(ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.list_global_functions) {
		throw duckdb::IOException("Fabricator: bridge does not provide list_global_functions");
	}
	char *err = nullptr;
	int32_t rc = vt.list_global_functions(&out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: list_global_functions failed");
	}
}

void OpenNamedInput(const std::string &name, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.open_named_input) {
		throw duckdb::IOException("Fabricator: bridge does not provide open_named_input");
	}
	char *err = nullptr;
	int32_t rc = vt.open_named_input(name.c_str(), &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: open_named_input failed");
	}
}

bool NamedInputExists(const std::string &name) {
	// Called from the replacement scan for EVERY unresolved table name, so it must NEVER throw (else a normal
	// "table does not exist" would become an error) and must tolerate an unavailable/unbootable bridge.
	try {
		const FabricatorVTable &vt = GetBridge();
		if (!vt.named_input_exists) {
			return false;
		}
		char *err = nullptr;
		int32_t exists = 0;
		int32_t rc = vt.named_input_exists(name.c_str(), &exists, &err);
		if (rc != FABRICATOR_OK) {
			if (err && vt.free_error) {
				vt.free_error(err);
			}
			return false;
		}
		return exists != 0;
	} catch (...) {
		return false;
	}
}

FabricatorHandle OneLakeOpen(const std::string &path, const std::string &cred_json, int64_t &out_size,
                           int64_t known_size, std::string *out_etag, int64_t *out_modified_ms) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_open) {
		throw duckdb::IOException("Fabricator: bridge does not provide onelake_open");
	}
	char *err = nullptr;
	char *etag = nullptr;
	int64_t modified_ms = -1;
	FabricatorHandle file = nullptr;
	int32_t rc = vt.onelake_open(path.c_str(), cred_json.c_str(), known_size, &file, &out_size, &etag,
	                             &modified_ms, &err);
	if (etag) {
		if (out_etag) {
			*out_etag = etag;
		}
		vt.free_error(etag);
	}
	if (out_modified_ms) {
		*out_modified_ms = modified_ms;
	}
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_open failed");
	}
	return file;
}

void OneLakeRead(FabricatorHandle file, void *buffer, int64_t nr_bytes, int64_t location) {
	const FabricatorVTable &vt = GetBridge();
	char *err = nullptr;
	int32_t rc = vt.onelake_read(file, buffer, nr_bytes, location, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_read failed");
	}
}

void OneLakeClose(FabricatorHandle file) {
	const FabricatorVTable &vt = GetBridge();
	if (vt.onelake_close && file) {
		vt.onelake_close(file);
	}
}

std::string OneLakeGlob(const std::string &pattern, const std::string &cred_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_glob) {
		throw duckdb::IOException("Fabricator: bridge does not provide onelake_glob");
	}
	char *err = nullptr;
	char *out_json = nullptr;
	int32_t rc = vt.onelake_glob(pattern.c_str(), cred_json.c_str(), &out_json, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_glob failed");
	}
	std::string result = out_json ? out_json : "[]";
	if (out_json && vt.free_error) {
		vt.free_error(out_json); // allocated by StringToCoTaskMemUTF8; freed like an error string
	}
	return result;
}

bool OneLakeExists(const std::string &path, const std::string &cred_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_exists) {
		throw duckdb::IOException("Fabricator: bridge does not provide onelake_exists");
	}
	char *err = nullptr;
	int32_t exists = 0;
	int32_t rc = vt.onelake_exists(path.c_str(), cred_json.c_str(), &exists, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_exists failed");
	}
	return exists != 0;
}

FabricatorHandle OneLakeOpenWrite(const std::string &path, const std::string &cred_json, bool exclusive) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_open_write) {
		throw duckdb::IOException("Fabricator: bridge does not provide onelake_open_write");
	}
	char *err = nullptr;
	FabricatorHandle file = nullptr;
	int32_t rc = vt.onelake_open_write(path.c_str(), cred_json.c_str(), exclusive ? 1 : 0, &file, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_open_write failed");
	}
	return file;
}

void OneLakeRemove(const std::string &path, const std::string &cred_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_remove) {
		throw duckdb::IOException("Fabricator: bridge does not provide onelake_remove");
	}
	char *err = nullptr;
	int32_t rc = vt.onelake_remove(path.c_str(), cred_json.c_str(), &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_remove failed");
	}
}

void OneLakeMove(const std::string &src, const std::string &dest, const std::string &cred_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_move) {
		throw duckdb::IOException("Fabricator: bridge does not provide onelake_move");
	}
	char *err = nullptr;
	int32_t rc = vt.onelake_move(src.c_str(), dest.c_str(), cred_json.c_str(), &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_move failed");
	}
}

void OneLakeWrite(FabricatorHandle file, const void *buffer, int64_t nr_bytes) {
	const FabricatorVTable &vt = GetBridge();
	char *err = nullptr;
	int32_t rc = vt.onelake_write(file, buffer, nr_bytes, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_write failed");
	}
}

void OneLakeCloseWrite(FabricatorHandle file) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.onelake_close_write || !file) {
		return;
	}
	char *err = nullptr;
	int32_t rc = vt.onelake_close_write(file, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: onelake_close_write failed");
	}
}

std::string DeltaListFiles(const std::string &path, const std::string &push_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.delta_list_files) {
		throw duckdb::IOException("Fabricator: bridge does not provide delta_list_files");
	}
	char *err = nullptr;
	char *out_json = nullptr;
	int32_t rc = vt.delta_list_files(path.c_str(), push_json.c_str(), &out_json, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: delta_list_files failed");
	}
	std::string result = out_json ? out_json : "[]";
	if (out_json && vt.free_error) {
		vt.free_error(out_json);
	}
	return result;
}

std::string GenerateTableSql(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             const std::string &catalog_name, ArrowArrayStream *args) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.generate_table_sql) {
		throw duckdb::IOException("Fabricator: bridge does not provide generate_table_sql");
	}
	char *err = nullptr;
	char *out_sql = nullptr;
	int32_t rc = vt.generate_table_sql(handle, schema.c_str(), func.c_str(), catalog_name.c_str(), args,
	                                   &out_sql, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: generate_table_sql failed");
	}
	std::string result = out_sql ? out_sql : "";
	if (out_sql && vt.free_error) {
		vt.free_error(out_sql);
	}
	if (result.empty()) {
		throw duckdb::IOException("Fabricator: generate_table_sql returned no SQL for function '%s'", func);
	}
	return result;
}

void ListSecretFields(ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.list_secret_fields) {
		throw duckdb::IOException("Fabricator: bridge does not provide list_secret_fields");
	}
	char *err = nullptr;
	int32_t rc = vt.list_secret_fields(&out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: list_secret_fields failed");
	}
}

// `value` is the rendered setting value (nullptr => unset/reset). Pushes into the managed
// ProviderSettingsStore so providers read it in C#, at `session` scope (0 => the global layer).
void SetSetting(const std::string &provider, const std::string &name, const char *value, int64_t session) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.set_setting) {
		throw duckdb::IOException("Fabricator: bridge does not provide set_setting");
	}
	char *err = nullptr;
	int32_t rc = vt.set_setting(session, provider.c_str(), name.c_str(), value, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: set_setting failed");
	}
}

// Runs from a ClientContextState DESTRUCTOR, so it must never throw and must tolerate a bridge that is gone
// or was never booted. Leaving the entry behind is the failure it guards against, but a throw here would be
// worse: it would terminate the process during connection teardown.
void ClearSessionSettings(int64_t session) noexcept {
	try {
		const FabricatorVTable &vt = GetBridge();
		if (!vt.clear_session_settings) {
			return; // older/partial bridge
		}
		char *err = nullptr;
		if (vt.clear_session_settings(session, &err) != FABRICATOR_OK && err && vt.free_error) {
			vt.free_error(err);
		}
	} catch (...) {
	}
}

void GetFunctionParamSchema(FabricatorHandle handle, const std::string &schema, const std::string &func,
                            ArrowSchema &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.get_function_param_schema) {
		throw duckdb::IOException("Fabricator: bridge does not provide get_function_param_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.get_function_param_schema(handle, schema.c_str(), func.c_str(), &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: get_function_param_schema failed");
	}
}

void GetFunctionReturnSchema(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             ArrowSchema &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.get_function_return_schema) {
		throw duckdb::IOException("Fabricator: bridge does not provide get_function_return_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.get_function_return_schema(handle, schema.c_str(), func.c_str(), &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: get_function_return_schema failed");
	}
}

void ExecuteScalar(FabricatorHandle handle, const std::string &schema, const std::string &func, ArrowArrayStream &args,
                   ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.execute_scalar) {
		throw duckdb::IOException("Fabricator: bridge does not provide execute_scalar");
	}
	char *err = nullptr;
	int32_t rc = vt.execute_scalar(handle, schema.c_str(), func.c_str(), &args, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: execute_scalar failed");
	}
}

void GetFunctionOutputSchema(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             ArrowArrayStream *args, ArrowSchema &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.get_function_output_schema) {
		throw duckdb::IOException("Fabricator: bridge does not provide get_function_output_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.get_function_output_schema(handle, schema.c_str(), func.c_str(), args, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: get_function_output_schema failed");
	}
}

// (ExecuteTable / ExecuteProc were removed at ABI v30 — superseded by the table-function session
//  TableFnBind / TableFnExecute / TableFnClose below.)

// (The 4g table-in-out push wrappers InOutOpen/InOutPush/InOutFinish/InOutAbort were removed at ABI v31 —
//  every `_each` form now runs on the streaming exchange: InOutBind/InOutExchangeOpen/InOutBindClose below.)

FabricatorHandle InOutBind(FabricatorHandle handle, const std::string &schema, const std::string &func,
                         ArrowArrayStream *args, ArrowSchema &input_schema, ArrowArrayStream &out_schema) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.inout_bind) {
		throw duckdb::IOException("Fabricator: bridge does not provide inout_bind");
	}
	FabricatorHandle binding = nullptr;
	char *err = nullptr;
	int32_t rc =
	    vt.inout_bind(handle, schema.c_str(), func.c_str(), args, &input_schema, &out_schema, &binding, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: inout_bind failed");
	}
	return binding;
}

void InOutExchangeOpen(FabricatorHandle binding, ArrowArrayStream &input, ArrowArrayStream &output) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.inout_exchange_open) {
		throw duckdb::IOException("Fabricator: bridge does not provide inout_exchange_open");
	}
	char *err = nullptr;
	int32_t rc = vt.inout_exchange_open(binding, &input, &output, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: inout_exchange_open failed");
	}
}

void InOutBindClose(FabricatorHandle binding) {
	if (!binding) {
		return;
	}
	const FabricatorVTable &vt = GetBridge();
	if (!vt.inout_bind_close) {
		return;
	}
	char *err = nullptr;
	int32_t rc = vt.inout_bind_close(binding, &err);
	if (rc != FABRICATOR_OK) {
		// Best-effort cleanup; swallow + free the managed error message.
		if (err && vt.free_error) {
			vt.free_error(err);
		}
	}
}

FabricatorHandle TableFnBind(FabricatorHandle handle, const std::string &schema, const std::string &func,
                         ArrowArrayStream *args, ArrowArrayStream &out_schema, bool &supports_pushdown) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.tablefn_bind) {
		throw duckdb::IOException("Fabricator: bridge does not provide tablefn_bind");
	}
	FabricatorHandle binding = nullptr;
	int32_t pushdown = 0;
	char *err = nullptr;
	int32_t rc = vt.tablefn_bind(handle, schema.c_str(), func.c_str(), args, &out_schema, &pushdown, &binding, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: tablefn_bind failed");
	}
	supports_pushdown = pushdown != 0;
	return binding;
}

void TableFnExecute(FabricatorHandle binding, const std::string &spec_json, ArrowArrayStream *filter_values,
                  ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.tablefn_execute) {
		throw duckdb::IOException("Fabricator: bridge does not provide tablefn_execute");
	}
	char *err = nullptr;
	const char *spec = spec_json.empty() ? nullptr : spec_json.c_str();
	int32_t rc = vt.tablefn_execute(binding, spec, filter_values, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: tablefn_execute failed");
	}
}

void TableFnClose(FabricatorHandle binding) {
	if (!binding) {
		return;
	}
	const FabricatorVTable &vt = GetBridge();
	if (!vt.tablefn_close) {
		return;
	}
	char *err = nullptr;
	int32_t rc = vt.tablefn_close(binding, &err);
	if (rc != FABRICATOR_OK) {
		// Best-effort cleanup; swallow + free the managed error message.
		if (err && vt.free_error) {
			vt.free_error(err);
		}
	}
}

FabricatorHandle AggOpen(FabricatorHandle handle, const std::string &schema, const std::string &func) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_open) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_open");
	}
	FabricatorHandle session = nullptr;
	char *err = nullptr;
	int32_t rc = vt.agg_open(handle, schema.c_str(), func.c_str(), &session, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_open failed");
	}
	return session;
}

void AggUpdate(FabricatorHandle session, ArrowArray &batch) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_update) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_update");
	}
	char *err = nullptr;
	int32_t rc = vt.agg_update(session, &batch, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_update failed");
	}
}

void AggCombine(FabricatorHandle session, ArrowArray &batch) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_combine) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_combine");
	}
	char *err = nullptr;
	int32_t rc = vt.agg_combine(session, &batch, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_combine failed");
	}
}

void AggFinalize(FabricatorHandle session, ArrowArray &ids, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_finalize) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_finalize");
	}
	char *err = nullptr;
	int32_t rc = vt.agg_finalize(session, &ids, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_finalize failed");
	}
}

void AggDestroy(FabricatorHandle session, ArrowArray &ids) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_destroy) {
		// No destroy entry: release the array we were handed so it doesn't leak.
		if (ids.release) {
			ids.release(&ids);
		}
		return;
	}
	char *err = nullptr;
	int32_t rc = vt.agg_destroy(session, &ids, &err);
	if (rc != FABRICATOR_OK) {
		// Destroy is best-effort (an aggregate destructor must not throw); swallow + free the message.
		if (err && vt.free_error) {
			vt.free_error(err);
		}
	}
}

void AggClose(FabricatorHandle session) {
	if (!session) {
		return;
	}
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_close) {
		return;
	}
	char *err = nullptr;
	int32_t rc = vt.agg_close(session, &err);
	if (rc != FABRICATOR_OK) {
		// Close is best-effort cleanup; swallow + free the managed error message.
		if (err && vt.free_error) {
			vt.free_error(err);
		}
	}
}

void AggUpdateSpill(FabricatorHandle session, ArrowArray &group_states, ArrowArray &batch, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_update_spill) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_update_spill");
	}
	char *err = nullptr;
	int32_t rc = vt.agg_update_spill(session, &group_states, &batch, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_update_spill failed");
	}
}

void AggCombineSpill(FabricatorHandle session, ArrowArray &target_states, ArrowArray &batch, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_combine_spill) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_combine_spill");
	}
	char *err = nullptr;
	int32_t rc = vt.agg_combine_spill(session, &target_states, &batch, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_combine_spill failed");
	}
}

void AggFinalizeSpill(FabricatorHandle session, ArrowArray &states, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.agg_finalize_spill) {
		throw duckdb::IOException("Fabricator: bridge does not provide agg_finalize_spill");
	}
	char *err = nullptr;
	int32_t rc = vt.agg_finalize_spill(session, &states, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: agg_finalize_spill failed");
	}
}

void ScanTable(FabricatorHandle handle, const std::string &schema, const std::string &table, const std::string &spec_json,
               ArrowArrayStream *filter_values, ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.scan_table) {
		throw duckdb::IOException("Fabricator: bridge does not provide scan_table");
	}
	char *err = nullptr;
	const char *spec = spec_json.empty() ? nullptr : spec_json.c_str();
	int32_t rc = vt.scan_table(handle, schema.c_str(), table.c_str(), spec, filter_values, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: scan_table failed");
	}
}

void CreateTable(FabricatorHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &columns,
                 bool if_not_exists, const std::string &pk_columns, const std::string &unique_columns,
                 const std::string &defaults, const std::string &partition_columns, const std::string &sort_columns,
                 const std::string &identity_columns, const std::string &options_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.create_table) {
		throw duckdb::IOException("Fabricator: bridge does not provide create_table");
	}
	char *err = nullptr;
	int32_t rc = vt.create_table(handle, schema.c_str(), table.c_str(), &columns, if_not_exists ? 1 : 0,
	                             pk_columns.empty() ? nullptr : pk_columns.c_str(),
	                             unique_columns.empty() ? nullptr : unique_columns.c_str(),
	                             defaults.empty() ? nullptr : defaults.c_str(),
	                             partition_columns.empty() ? nullptr : partition_columns.c_str(),
	                             sort_columns.empty() ? nullptr : sort_columns.c_str(),
	                             identity_columns.empty() ? nullptr : identity_columns.c_str(),
	                             options_json.empty() ? nullptr : options_json.c_str(), &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: create_table failed");
	}
}

void DropTable(FabricatorHandle handle, const std::string &schema, const std::string &table, bool if_exists) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.drop_table) {
		throw duckdb::IOException("Fabricator: bridge does not provide drop_table");
	}
	char *err = nullptr;
	int32_t rc = vt.drop_table(handle, schema.c_str(), table.c_str(), if_exists ? 1 : 0, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: drop_table failed");
	}
}

void CreateSchema(FabricatorHandle handle, const std::string &schema, bool if_not_exists) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.create_schema) {
		throw duckdb::IOException("Fabricator: bridge does not provide create_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.create_schema(handle, schema.c_str(), if_not_exists ? 1 : 0, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: create_schema failed");
	}
}

void DropSchema(FabricatorHandle handle, const std::string &schema, bool if_exists) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.drop_schema) {
		throw duckdb::IOException("Fabricator: bridge does not provide drop_schema");
	}
	char *err = nullptr;
	int32_t rc = vt.drop_schema(handle, schema.c_str(), if_exists ? 1 : 0, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: drop_schema failed");
	}
}

void AlterTable(FabricatorHandle handle, const std::string &schema, const std::string &table, int32_t alter_kind,
                const std::string &arg1, const std::string &arg2, ArrowArrayStream *column, int32_t flags) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.alter_table) {
		throw duckdb::IOException("Fabricator: bridge does not provide alter_table");
	}
	char *err = nullptr;
	int32_t rc = vt.alter_table(handle, schema.c_str(), table.c_str(), alter_kind,
	                            arg1.empty() ? nullptr : arg1.c_str(), arg2.empty() ? nullptr : arg2.c_str(), column,
	                            flags, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: alter_table failed");
	}
}

void BeginTransaction(FabricatorHandle handle, bool is_explicit) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.begin_transaction) {
		throw duckdb::IOException("Fabricator: bridge does not provide begin_transaction");
	}
	char *err = nullptr;
	if (vt.begin_transaction(handle, is_explicit ? 1 : 0, &err) != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: begin_transaction failed");
	}
}

void CommitTransaction(FabricatorHandle handle) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.commit_transaction) {
		throw duckdb::IOException("Fabricator: bridge does not provide commit_transaction");
	}
	char *err = nullptr;
	if (vt.commit_transaction(handle, &err) != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: commit_transaction failed");
	}
}

void RollbackTransaction(FabricatorHandle handle) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.rollback_transaction) {
		throw duckdb::IOException("Fabricator: bridge does not provide rollback_transaction");
	}
	char *err = nullptr;
	if (vt.rollback_transaction(handle, &err) != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: rollback_transaction failed");
	}
}

void InsertReturning(FabricatorHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &in,
                     ArrowArrayStream &out) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.insert_returning) {
		throw duckdb::IOException("Fabricator: bridge does not provide insert_returning");
	}
	char *err = nullptr;
	int32_t rc = vt.insert_returning(handle, schema.c_str(), table.c_str(), &in, &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: insert_returning failed");
	}
}

FabricatorHandle BeginBulk(FabricatorHandle handle, const std::string &schema, const std::string &table, bool create_table,
                         bool replace, bool check_constraints, int64_t txn_id, ArrowSchema &schema_in,
                         const std::string &partition_columns, const std::string &sort_columns,
                         const std::string &schema_mode, bool partition_overwrite, const std::string &options_json) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.begin_bulk) {
		throw duckdb::IOException("Fabricator: bridge does not provide begin_bulk");
	}
	FabricatorHandle session = nullptr;
	char *err = nullptr;
	int32_t rc = vt.begin_bulk(handle, schema.c_str(), table.c_str(), create_table ? 1 : 0, replace ? 1 : 0,
	                           check_constraints ? 1 : 0, txn_id, &schema_in,
	                           partition_columns.empty() ? nullptr : partition_columns.c_str(),
	                           sort_columns.empty() ? nullptr : sort_columns.c_str(),
	                           schema_mode.empty() ? nullptr : schema_mode.c_str(),
	                           partition_overwrite ? 1 : 0,
	                           options_json.empty() ? nullptr : options_json.c_str(), &session, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: begin_bulk failed");
	}
	return session;
}

void SetActiveTxn(FabricatorHandle handle, int64_t txn_id, bool join_only) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.set_active_txn) {
		return; // older/partial bridge: per-transaction routing simply not active
	}
	char *err = nullptr;
	int32_t rc = vt.set_active_txn(handle, txn_id, join_only ? 1 : 0, &err);
	if (rc != FABRICATOR_OK && err) {
		// Best-effort: a failure to set the ambient must not abort the statement; free the message.
		if (vt.free_error) {
			vt.free_error(err);
		}
	}
}

void PushBatch(FabricatorHandle session, ArrowArray &batch) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.push_batch) {
		throw duckdb::IOException("Fabricator: bridge does not provide push_batch");
	}
	// The batch is about to become the MANAGED side's property (the ABI contract: the managed side
	// consumes/releases every ArrowArray passed in). Record the handover so any later free can be
	// attributed — this is the path that feeds DeltaWriter.Materialize, whose IPC copy was justified by
	// "the source batches may be freed after consumption". No-op unless FABRICATOR_ARROW_LIVENESS is set.
	ArrowLiveness::Track(batch, "PushBatch");
	char *err = nullptr;
	int32_t rc = vt.push_batch(session, &batch, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: push_batch failed");
	}
}

int64_t CompleteBulk(FabricatorHandle session, bool abort) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.complete_bulk) {
		throw duckdb::IOException("Fabricator: bridge does not provide complete_bulk");
	}
	int64_t affected = 0;
	char *err = nullptr;
	int32_t rc = vt.complete_bulk(session, abort ? 1 : 0, &affected, &err);
	if (rc != FABRICATOR_OK) {
		if (abort) {
			// Cleanup path: don't mask the original failure with the abort's error.
			if (err && vt.free_error) {
				vt.free_error(err);
			}
			return 0;
		}
		ThrowManagedError(vt, err, "Fabricator: complete_bulk failed");
	}
	return affected;
}

std::string FsSpike(FabricatorHandle opener, const std::string &path) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.fs_spike) {
		throw duckdb::IOException("Fabricator: bridge does not provide fs_spike");
	}
	char *out = nullptr;
	char *err = nullptr;
	int32_t rc = vt.fs_spike(opener, path.c_str(), &out, &err);
	if (rc != FABRICATOR_OK) {
		ThrowManagedError(vt, err, "Fabricator: fs_spike failed");
	}
	std::string result = out ? out : "";
	if (out && vt.free_error) {
		vt.free_error(out); // owned UTF-8, freed like an error string
	}
	return result;
}

void SetActiveOpener(FabricatorHandle opener, int64_t session) {
	const FabricatorVTable &vt = GetBridge();
	if (!vt.set_active_opener) {
		return; // older/partial bridge: host-FS opener routing simply not active
	}
	char *err = nullptr;
	int32_t rc = vt.set_active_opener(opener, session, &err);
	if (rc != FABRICATOR_OK && err) {
		// Best-effort: a failure to set the ambient must not abort the statement; free the message.
		if (vt.free_error) {
			vt.free_error(err);
		}
	}
}

} // namespace fabricator
