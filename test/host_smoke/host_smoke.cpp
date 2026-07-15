//===----------------------------------------------------------------------===//
//                         Fabricator — host smoke test
//
// Standalone validation of the runtime spine WITHOUT DuckDB: boots CoreCLR via
// hostfxr, loads Fabricator.Bridge, fills the vtable, opens a (stub) catalog,
// executes a query, and reads the exported Arrow C stream — proving the
// C++ <-> C# Arrow interop end to end. The DuckDB ingestion step
// (ArrowTableFunction::ArrowToDuckDB) is exercised separately by the full
// extension build.
//
// Requires FABRICATOR_MANAGED_DIR to point at a self-contained publish of
// Fabricator.Bridge (containing hostfxr + Fabricator.Bridge.dll + runtimeconfig).
//===----------------------------------------------------------------------===//

#include "fabricator/abi.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#if defined(_WIN32)
#include <windows.h>
typedef wchar_t host_char_t;
#define ANET_CDECL __cdecl
#define ANET_STDCALL __stdcall
#else
#include <dlfcn.h>
typedef char host_char_t;
#define ANET_CDECL
#define ANET_STDCALL
#endif

typedef int32_t(ANET_CDECL *init_cmdline_fn)(int, const host_char_t **, const void *, void **);
typedef int32_t(ANET_CDECL *get_runtime_delegate_fn)(void *, int32_t, void **);
typedef int(ANET_STDCALL *load_assembly_and_get_function_pointer_fn)(const host_char_t *, const host_char_t *,
                                                                     const host_char_t *, const host_char_t *,
                                                                     void *, void **);
typedef int32_t(ANET_CDECL *bootstrap_fn)(FabricatorVTable *, int32_t);

static const host_char_t *const kUnmanagedCallersOnly = reinterpret_cast<const host_char_t *>(-1);
static constexpr int kHdtLoadAssembly = 5;

static std::basic_string<host_char_t> H(const std::string &s) {
#if defined(_WIN32)
	int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
	std::wstring w((size_t)len, L'\0');
	MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), len);
	return w;
#else
	return s;
#endif
}

static void *LoadLib(const std::string &p) {
#if defined(_WIN32)
	return (void *)LoadLibraryW(H(p).c_str());
#else
	return dlopen(p.c_str(), RTLD_LAZY | RTLD_LOCAL);
#endif
}
static void *Sym(void *lib, const char *n) {
#if defined(_WIN32)
	return (void *)GetProcAddress((HMODULE)lib, n);
#else
	return dlsym(lib, n);
#endif
}

#define CHECK(cond, msg)                                                                                          \
	do {                                                                                                          \
		if (!(cond)) {                                                                                            \
			std::fprintf(stderr, "FAIL: %s\n", msg);                                                              \
			return 1;                                                                                             \
		}                                                                                                         \
	} while (0)

int main() {
	const char *managed = std::getenv("FABRICATOR_MANAGED_DIR");
	CHECK(managed && *managed, "FABRICATOR_MANAGED_DIR not set");
	std::string dir = managed;
	char sep = dir.back() == '/' || dir.back() == '\\' ? '\0' : '/';
	auto join = [&](const char *leaf) { return sep ? dir + sep + leaf : dir + leaf; };

#if defined(_WIN32)
	std::string hostfxr_leaf = "hostfxr.dll";
#elif defined(__APPLE__)
	std::string hostfxr_leaf = "libhostfxr.dylib";
#else
	std::string hostfxr_leaf = "libhostfxr.so";
#endif

	void *hostfxr = LoadLib(join(hostfxr_leaf.c_str()));
	CHECK(hostfxr, "could not load hostfxr");

	// Self-contained deployments must be initialized via the command-line entry
	// (initialize_for_runtime_config rejects self-contained components).
	auto init_fn = (init_cmdline_fn)Sym(hostfxr, "hostfxr_initialize_for_dotnet_command_line");
	auto get_delegate = (get_runtime_delegate_fn)Sym(hostfxr, "hostfxr_get_runtime_delegate");
	CHECK(init_fn && get_delegate, "hostfxr exports missing");

	void *ctx = nullptr;
	auto asm_w = H(join("Fabricator.Bridge.dll"));
	const host_char_t *argv[1] = {asm_w.c_str()};
	int32_t rc = init_fn(1, argv, nullptr, &ctx);
	CHECK(rc >= 0 && ctx, "hostfxr_initialize_for_dotnet_command_line failed");

	void *load_ptr = nullptr;
	rc = get_delegate(ctx, kHdtLoadAssembly, &load_ptr);
	CHECK(rc == 0 && load_ptr, "get_runtime_delegate failed");
	auto load_assembly = (load_assembly_and_get_function_pointer_fn)load_ptr;

	void *boot_ptr = nullptr;
	rc = load_assembly(H(join("Fabricator.Bridge.dll")).c_str(), H("Fabricator.Bridge.Bootstrap, Fabricator.Bridge").c_str(),
	                   H("Initialize").c_str(), kUnmanagedCallersOnly, nullptr, &boot_ptr);
	CHECK(rc == 0 && boot_ptr, "load Bootstrap.Initialize failed");

	FabricatorVTable vt;
	std::memset(&vt, 0, sizeof(vt));
	rc = ((bootstrap_fn)boot_ptr)(&vt, (int32_t)sizeof(vt));
	CHECK(rc == 0, "Bootstrap.Initialize returned non-zero");
	CHECK(vt.abi_version == FABRICATOR_ABI_VERSION, "ABI version mismatch");
	CHECK(vt.open_catalog && vt.execute_query && vt.close_catalog && vt.free_error, "vtable not fully populated");
	std::printf("vtable populated (abi_version=%d)\n", vt.abi_version);

	FabricatorHandle handle = nullptr;
	char *err = nullptr;
	rc = vt.open_catalog("", &handle, &err);
	CHECK(rc == 0, err ? err : "open_catalog failed");
	std::printf("open_catalog ok (handle=%p)\n", handle);

	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	rc = vt.execute_query(handle, "SELECT 42 AS answer", &stream, &err);
	CHECK(rc == 0, err ? err : "execute_query failed");

	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	rc = stream.get_schema(&stream, &schema);
	CHECK(rc == 0, "get_schema failed");
	std::printf("schema: %lld columns:", (long long)schema.n_children);
	for (int64_t i = 0; i < schema.n_children; i++) {
		std::printf(" %s(%s)", schema.children[i]->name, schema.children[i]->format);
	}
	std::printf("\n");
	CHECK(schema.n_children == 3, "expected 3 columns");

	int64_t total_rows = 0;
	int batches = 0;
	for (;;) {
		ArrowArray array;
		std::memset(&array, 0, sizeof(array));
		rc = stream.get_next(&stream, &array);
		CHECK(rc == 0, "get_next failed");
		if (!array.release) {
			break; // end of stream
		}
		batches++;
		total_rows += array.length;
		// Decode the first (int32) column as a sanity check.
		if (array.length > 0 && array.n_children >= 1 && array.children[0]->n_buffers >= 2) {
			const int32_t *ids = (const int32_t *)array.children[0]->buffers[1];
			std::printf("batch %d: %lld rows, id[0]=%d\n", batches, (long long)array.length, ids ? ids[0] : -1);
		}
		array.release(&array);
	}
	std::printf("total: %lld rows in %d batch(es)\n", (long long)total_rows, batches);
	CHECK(total_rows == 3, "expected 3 rows from stub backend");

	if (schema.release) {
		schema.release(&schema);
	}
	if (stream.release) {
		stream.release(&stream);
	}
	vt.close_catalog(handle);
	std::printf("SMOKE TEST PASSED\n");
	return 0;
}
