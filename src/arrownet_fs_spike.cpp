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
#include <functional>
#include <vector>

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
		// A glob over a non-existent prefix returns empty on a local FS but THROWS 404 on object stores
		// (Azure/S3). Normalize "path does not exist" to an empty result — a glob of a missing dir = no files
		// (so a brand-new Delta table, whose `_delta_log/` doesn't exist yet, is treated as version -1 = create,
		// not an error). Genuine failures (auth 401/403, etc.) still propagate.
		std::string msg = e.what();
		auto contains = [&](const char *needle) { return msg.find(needle) != std::string::npos; };
		if (contains("does not exist") || contains("404") || contains("NoSuchKey") || contains("BlobNotFound") ||
		    contains("PathNotFound")) {
			*out_json = DupErr("[]");
			return ARROWNET_OK;
		}
		if (err) {
			*err = DupErr(msg);
		}
		return 1;
	}
}

// ---- WRITE surface (Delta write-back foundation) ----

int32_t HostFsOpenWrite(ArrowNetHandle opener, const char *path, int32_t exclusive, ArrowNetHandle *out_file,
                        char **err) {
	try {
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		// exclusive => put-if-absent (O_CREAT|O_EXCL on POSIX / honored on ADLS); else create-or-truncate.
		idx_t flags = exclusive ? (FileOpenFlags::FILE_FLAGS_WRITE | FileOpenFlags::FILE_FLAGS_FILE_CREATE |
		                           FileOpenFlags::FILE_FLAGS_EXCLUSIVE_CREATE)
		                        : (FileOpenFlags::FILE_FLAGS_WRITE | FileOpenFlags::FILE_FLAGS_FILE_CREATE_NEW);
		auto handle = fs.OpenFile(path, FileOpenFlags(flags));
		*out_file = reinterpret_cast<ArrowNetHandle>(handle.release()); // closed via HostFsCloseWrite
		return ARROWNET_OK;
	} catch (std::exception &e) {
		// For an exclusive (put-if-absent) open, distinguish "already exists" (a commit conflict) from a real
		// error by probing existence — robust across backends (no fragile message matching).
		if (exclusive) {
			try {
				auto *ctx = reinterpret_cast<ClientContext *>(opener);
				auto &fs = FileSystem::GetFileSystem(*ctx);
				if (fs.FileExists(path)) {
					return ARROWNET_ALREADY_EXISTS; // no *err — the caller treats this as a conflict, not a failure
				}
			} catch (...) {
				// fall through to the generic error below
			}
		}
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsWrite(ArrowNetHandle file, const void *buffer, int64_t nr_bytes, char **err) {
	try {
		auto *h = reinterpret_cast<FileHandle *>(file);
		// Sequential append (no location) — the only mode Azure DFS supports beyond location 0.
		h->Write(const_cast<void *>(buffer), nr_bytes);
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsCloseWrite(ArrowNetHandle file, char **err) {
	auto *h = reinterpret_cast<FileHandle *>(file);
	try {
		h->Close(); // flush — surfaces write/commit errors that the dtor would swallow
		delete h;
		return ARROWNET_OK;
	} catch (std::exception &e) {
		delete h; // still free the handle on a flush error
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsRemove(ArrowNetHandle opener, const char *path, char **err) {
	try {
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		fs.TryRemoveFile(path); // no error if missing
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

// Recursive mkdir -p: DuckDB's CreateDirectory is single-level, so create the parent chain. Recurses up until
// an existing ancestor (drive root / scheme authority), then creates downward. Idempotent; object stores treat
// directories as implicit (CreateDirectory is a no-op/marker), so the recursion is harmless there.
void CreateDirRecursive(FileSystem &fs, const std::string &path) {
	if (path.empty() || fs.DirectoryExists(path)) {
		return;
	}
	auto slash = path.find_last_of('/');
	if (slash != std::string::npos && slash > 0) {
		std::string parent = path.substr(0, slash);
		// Stop at a scheme authority ("abfss://c@host") — has no '/' after "://".
		if (!parent.empty() && parent.find("://") != parent.size() - 3 && !fs.DirectoryExists(parent)) {
			CreateDirRecursive(fs, parent);
		}
	}
	try {
		fs.CreateDirectory(path);
	} catch (...) {
		if (!fs.DirectoryExists(path)) {
			throw; // a real failure (not a lost create/exists race)
		}
	}
}

int32_t HostFsCreateDir(ArrowNetHandle opener, const char *path, char **err) {
	try {
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		CreateDirRecursive(fs, path);
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsRemoveDir(ArrowNetHandle opener, const char *path, char **err) {
	try {
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		if (fs.DirectoryExists(path)) {
			fs.RemoveDirectory(path); // recursive; idempotent (skip if absent)
		}
		return ARROWNET_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupErr(e.what());
		}
		return 1;
	}
}

int32_t HostFsMoveDir(ArrowNetHandle opener, const char *src, const char *dest, char **err) {
	try {
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		auto &fs = FileSystem::GetFileSystem(*ctx);
		fs.MoveFile(src, dest); // atomic directory rename on local; object stores throw "not implemented"
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
	services.fs_open_write = HostFsOpenWrite;
	services.fs_write = HostFsWrite;
	services.fs_close_write = HostFsCloseWrite;
	services.fs_remove = HostFsRemove;
	services.fs_create_dir = HostFsCreateDir;
	services.fs_remove_dir = HostFsRemoveDir;
	services.fs_move_dir = HostFsMoveDir;
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

// -----------------------------------------------------------------------------
// arrownet_fs_write_probe(base_path) — capability probe for the WRITE side of DuckDB's FileSystem (the
// foundation for a Delta WRITE-back path through the host-FS bridge). It exercises DuckDB's FileSystem
// directly (the managed reverse-callbacks would forward to these exact calls, so this faithfully answers
// "is DuckDB's FileSystem capable?", including the opener/secret path when `base_path` is az:// / s3://).
// Returns one row per check: (step, ok, detail). The headline checks are the Delta COMMIT primitive —
// put-if-absent via EXCLUSIVE_CREATE (O_CREAT|O_EXCL) — and MoveFile's overwrite-vs-fail behavior.
// -----------------------------------------------------------------------------

struct FsWriteProbeBindData : public TableFunctionData {
	string base;
};

unique_ptr<FunctionData> FsWriteProbeBind(ClientContext &, TableFunctionBindInput &input,
                                          vector<LogicalType> &return_types, vector<string> &names) {
	auto bind_data = make_uniq<FsWriteProbeBindData>();
	bind_data->base = input.inputs[0].GetValue<string>();
	return_types.push_back(LogicalType::VARCHAR);
	names.emplace_back("step");
	return_types.push_back(LogicalType::BOOLEAN);
	names.emplace_back("ok");
	return_types.push_back(LogicalType::VARCHAR);
	names.emplace_back("detail");
	return std::move(bind_data);
}

struct FsWriteProbeGlobalState : public GlobalTableFunctionState {
	bool done = false;
	idx_t MaxThreads() const override {
		return 1;
	}
};

unique_ptr<GlobalTableFunctionState> FsWriteProbeInit(ClientContext &, TableFunctionInitInput &) {
	return make_uniq<FsWriteProbeGlobalState>();
}

void FsWriteProbeFunc(ClientContext &context, TableFunctionInput &data, DataChunk &output) {
	auto &gstate = data.global_state->Cast<FsWriteProbeGlobalState>();
	if (gstate.done) {
		output.SetCardinality(0);
		return;
	}
	gstate.done = true;
	auto &bind_data = data.bind_data->Cast<FsWriteProbeBindData>();

	// Opener auto-pushed (OpenerFileSystem) — az:// / s3:// resolve their DuckDB secret as a native read would.
	auto &fs = FileSystem::GetFileSystem(context);
	const string dir = bind_data.base + "/arrownet_write_probe";
	const string f1 = dir + "/f1.txt";
	const string f2 = dir + "/f2.txt";
	const string f3 = dir + "/f3.txt";

	vector<string> steps;
	vector<bool> oks;
	vector<string> details;
	auto record = [&](const string &step, bool ok, const string &detail) {
		steps.push_back(step);
		oks.push_back(ok);
		details.push_back(detail);
	};
	auto run = [&](const string &step, const std::function<string()> &fn) {
		try {
			string detail = fn();
			record(step, true, detail);
		} catch (std::exception &e) {
			record(step, false, string("threw: ") + e.what());
		}
	};

	const idx_t WRITE_CREATE = FileOpenFlags::FILE_FLAGS_WRITE | FileOpenFlags::FILE_FLAGS_FILE_CREATE;
	const idx_t WRITE_EXCL =
	    FileOpenFlags::FILE_FLAGS_WRITE | FileOpenFlags::FILE_FLAGS_FILE_CREATE | FileOpenFlags::FILE_FLAGS_EXCLUSIVE_CREATE;
	const string content = "hello-arrownet";

	auto write_file = [&](const string &p, idx_t flags, const string &data_str) {
		auto h = fs.OpenFile(p, FileOpenFlags(flags));
		if (!data_str.empty()) {
			fs.Write(*h, const_cast<char *>(data_str.data()), static_cast<int64_t>(data_str.size()));
		}
		h->Close();
	};
	auto read_file = [&](const string &p) -> string {
		auto h = fs.OpenFile(p, FileOpenFlags::FILE_FLAGS_READ);
		auto size = h->GetFileSize();
		string buf(static_cast<size_t>(size), '\0');
		if (size > 0) {
			fs.Read(*h, &buf[0], static_cast<int64_t>(size), 0);
		}
		return buf;
	};

	// Best-effort clean slate (ignore errors).
	try { fs.CreateDirectory(dir); } catch (...) {}
	for (auto &p : {f1, f2, f3}) {
		try { fs.TryRemoveFile(p); } catch (...) {}
	}

	// 1) Create + write a file.
	run("create_directory", [&]() -> string {
		return fs.DirectoryExists(dir) ? "directory exists/created" : "CreateDirectory did not create the directory";
	});
	run("write_create", [&]() -> string {
		write_file(f1, WRITE_CREATE, content);
		return "wrote " + std::to_string(content.size()) + " bytes (WRITE|FILE_CREATE)";
	});
	// 2) Read it back.
	run("read_back", [&]() -> string {
		string got = read_file(f1);
		if (got != content) {
			throw IOException("read-back mismatch: got '" + got + "'");
		}
		return "round-trip ok: '" + got + "'";
	});
	run("file_exists", [&]() -> string {
		return fs.FileExists(f1) ? "FileExists=true" : "FileExists=false (unexpected)";
	});
	// 3) THE COMMIT PRIMITIVE: exclusive create on an EXISTING file must FAIL (put-if-absent). We invert the
	// sense: success here = it threw. If OpenFile unexpectedly succeeds, the store has no put-if-absent guard.
	{
		bool threw = false;
		string detail;
		try {
			write_file(f1, WRITE_EXCL, "");
			detail = "EXCLUSIVE_CREATE on an existing file SUCCEEDED — NO put-if-absent guard (unsafe for commits)";
		} catch (std::exception &e) {
			threw = true;
			detail = string("EXCLUSIVE_CREATE on an existing file threw (put-if-absent works): ") + e.what();
		}
		record("exclusive_create_existing_fails", threw, detail);
	}
	// 4) Exclusive create on a NEW path must succeed.
	run("exclusive_create_new", [&]() -> string {
		write_file(f2, WRITE_EXCL, content);
		return "EXCLUSIVE_CREATE on a new path ok";
	});
	// 5) MoveFile to a new target.
	run("move_to_new", [&]() -> string {
		fs.MoveFile(f2, f3);
		if (!fs.FileExists(f3)) {
			throw IOException("MoveFile did not create the target");
		}
		return "MoveFile to a new target ok";
	});
	// 6) MoveFile onto an EXISTING target — does it overwrite (atomic replace) or fail? This determines whether
	// MoveFile is usable as a commit (it is NOT if it silently overwrites). Both outcomes are recorded as ok=true
	// (we're characterizing behavior, not asserting); the detail says which.
	run("move_overwrite_behavior", [&]() -> string {
		write_file(f1, WRITE_CREATE, "TARGET-BEFORE-MOVE"); // f1 currently holds `content`; overwrite with a marker
		try {
			fs.MoveFile(f3, f1); // f1 exists
			string after = read_file(f1);
			return "MoveFile OVERWROTE the existing target (after='" + after +
			       "') => NOT a put-if-absent commit primitive; use EXCLUSIVE_CREATE for commits";
		} catch (std::exception &e) {
			return string("MoveFile onto an existing target THREW (fail-if-exists): ") + e.what();
		}
	});
	// 7) Delete.
	run("remove_file", [&]() -> string {
		fs.RemoveFile(f1);
		return fs.FileExists(f1) ? "RemoveFile left the file (unexpected)" : "RemoveFile ok";
	});
	run("try_remove_missing", [&]() -> string {
		bool removed = fs.TryRemoveFile(dir + "/does_not_exist.txt");
		return removed ? "TryRemoveFile returned true for a missing file (unexpected)"
		               : "TryRemoveFile returned false for a missing file (ok)";
	});

	// Best-effort cleanup.
	for (auto &p : {f1, f2, f3}) {
		try { fs.TryRemoveFile(p); } catch (...) {}
	}
	try { fs.RemoveDirectory(dir); } catch (...) {}

	idx_t n = steps.size();
	output.SetCardinality(n);
	for (idx_t i = 0; i < n; i++) {
		output.SetValue(0, i, Value(steps[i]));
		output.SetValue(1, i, Value::BOOLEAN(oks[i]));
		output.SetValue(2, i, Value(details[i]));
	}
}

} // namespace

void RegisterFsSpike(ExtensionLoader &loader) {
	InstallHostFsServices(); // must precede the bridge's first boot so the callbacks are passed to Initialize
	TableFunction fn("arrownet_fs_spike", {LogicalType::VARCHAR}, FsSpikeFunc, FsSpikeBind, FsSpikeInit);
	loader.RegisterFunction(fn);
	TableFunction probe("arrownet_fs_write_probe", {LogicalType::VARCHAR}, FsWriteProbeFunc, FsWriteProbeBind,
	                    FsWriteProbeInit);
	loader.RegisterFunction(probe);
}

} // namespace duckdb
