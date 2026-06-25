//===----------------------------------------------------------------------===//
//                         arrownet — filesystem reverse-callback SPIKE (impl)
//===----------------------------------------------------------------------===//

#include "arrownet_fs_spike.hpp"

#include "arrownet/clr_host.hpp"
#include "duckdb/common/file_system.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/main/client_context.hpp"

#include <cstdlib>
#include <cstring>

namespace duckdb {

namespace {

// -----------------------------------------------------------------------------
// Host FileSystem callbacks — the reverse direction (the managed side calls these to reach DuckDB's
// FileSystem). Secret resolution rides the FileOpener built from the calling operator's ClientContext, so an
// az:// / s3:// path resolves its DuckDB secret exactly as a native read would.
// -----------------------------------------------------------------------------

// Duplicate a message into a malloc'd C string for the managed side (freed via HostFreeStr).
char *DupErr(const std::string &msg) {
	char *out = static_cast<char *>(malloc(msg.size() + 1));
	if (out) {
		memcpy(out, msg.c_str(), msg.size() + 1);
	}
	return out;
}

int32_t HostFsOpenRead(ArrowNetHandle opener, const char *path, ArrowNetHandle *out_file, char **err) {
	try {
		// `opener` is the calling operator's ClientContext (alive for the duration of the managed call).
		// FileSystem::GetFileSystem(context) returns an OpenerFileSystem that AUTO-pushes the context's
		// FileOpener (secret resolution for az://, s3://, …) — so we must NOT pass an explicit opener.
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		auto handle = fs.OpenFile(path, FileOpenFlags::FILE_FLAGS_READ);
		*out_file = reinterpret_cast<ArrowNetHandle>(handle.release()); // closed via HostFsClose
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsSize(ArrowNetHandle file, int64_t *out_size, char **err) {
	try {
		*out_size = static_cast<int64_t>(reinterpret_cast<FileHandle *>(file)->GetFileSize());
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsRead(ArrowNetHandle file, void *buffer, int64_t nr_bytes, int64_t location, char **err) {
	try {
		reinterpret_cast<FileHandle *>(file)->Read(buffer, static_cast<idx_t>(nr_bytes), static_cast<idx_t>(location));
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

void HostFsClose(ArrowNetHandle file) {
	delete reinterpret_cast<FileHandle *>(file); // FileHandle dtor closes
}

void HostFreeStr(char *str) {
	free(str);
}

// Escape a path for a JSON string value (backslash + quote; control chars are not expected in paths).
std::string JsonEscape(const std::string &s) {
	std::string out;
	out.reserve(s.size() + 8);
	for (char c : s) {
		if (c == '\\' || c == '"') {
			out.push_back('\\');
		}
		out.push_back(c);
	}
	return out;
}

int32_t HostFsGlob(ArrowNetHandle opener, const char *pattern, char **out_json, char **err) {
	try {
		// Opener auto-pushed (OpenerFileSystem) — secrets resolve as a native read would.
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		auto files = fs.Glob(pattern);
		std::string json = "[";
		for (idx_t i = 0; i < files.size(); i++) {
			const std::string &p = files[i].path;
			int64_t size = -1;
			try {
				auto h = fs.OpenFile(p, FileOpenFlags::FILE_FLAGS_READ);
				size = static_cast<int64_t>(h->GetFileSize());
			} catch (...) {
				size = -1; // best-effort; the managed side falls back to a size query if it needs one
			}
			if (i) {
				json += ",";
			}
			json += "{\"path\":\"" + JsonEscape(p) + "\",\"size\":" + std::to_string(size) + "}";
		}
		json += "]";
		*out_json = DupErr(json);
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

void InstallHostFsServices() {
	ArrowNetHostServices services {};
	services.abi_version = ARROWNET_ABI_VERSION;
	services.fs_open_read = HostFsOpenRead;
	services.fs_size = HostFsSize;
	services.fs_read = HostFsRead;
	services.fs_close = HostFsClose;
	services.free_str = HostFreeStr;
	services.fs_glob = HostFsGlob;
	arrownet::SetHostServices(services);
}

// -----------------------------------------------------------------------------
// arrownet_fs_spike(path) table function — a ClientContext-bearing trigger that asks the managed side to do
// the read (proving the C# -> host FileSystem path end-to-end).
// -----------------------------------------------------------------------------

struct FsSpikeBindData : public TableFunctionData {
	string path;
};

unique_ptr<FunctionData> FsSpikeBind(ClientContext &, TableFunctionBindInput &input, vector<LogicalType> &return_types,
                                     vector<string> &names) {
	auto bind_data = make_uniq<FsSpikeBindData>();
	bind_data->path = input.inputs[0].GetValue<string>();
	return_types.push_back(LogicalType::VARCHAR);
	names.emplace_back("result");
	return std::move(bind_data);
}

struct FsSpikeGlobalState : public GlobalTableFunctionState {
	bool done = false;
	idx_t MaxThreads() const override {
		return 1;
	}
};

unique_ptr<GlobalTableFunctionState> FsSpikeInit(ClientContext &, TableFunctionInitInput &) {
	return make_uniq<FsSpikeGlobalState>();
}

void FsSpikeFunc(ClientContext &context, TableFunctionInput &data, DataChunk &output) {
	auto &gstate = data.global_state->Cast<FsSpikeGlobalState>();
	if (gstate.done) {
		output.SetCardinality(0);
		return;
	}
	gstate.done = true;
	auto &bind_data = data.bind_data->Cast<FsSpikeBindData>();
	// The ClientContext is alive for this synchronous call; pass it as the opaque opener handle so the host
	// FileSystem callbacks can build a FileOpener from it (secret resolution).
	string result = arrownet::FsSpike(reinterpret_cast<ArrowNetHandle>(&context), bind_data.path);
	output.SetCardinality(1);
	output.SetValue(0, 0, Value(result));
}

} // namespace

void RegisterFsSpike(ExtensionLoader &loader) {
	InstallHostFsServices(); // must precede the bridge's first boot so the callbacks are passed to Initialize
	TableFunction fn("arrownet_fs_spike", {LogicalType::VARCHAR}, FsSpikeFunc, FsSpikeBind, FsSpikeInit);
	loader.RegisterFunction(fn);
}

} // namespace duckdb
