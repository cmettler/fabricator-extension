#include "arrownet/arrownet_onelake_fs.hpp"

#include "arrownet/clr_host.hpp"
#include "duckdb/catalog/catalog_transaction.hpp"
#include "duckdb/common/file_opener.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/database.hpp"
#include "duckdb/main/secret/secret.hpp"
#include "duckdb/main/secret/secret_manager.hpp"

namespace arrownet {

using namespace duckdb;

namespace {

const char *const kScheme = "onelake://";

// Minimal JSON-string escaping (matches the managed side's expectations for secret field values).
std::string EscapeJson(const std::string &s) {
	std::string out;
	out.reserve(s.size() + 8);
	for (char c : s) {
		switch (c) {
		case '"':
			out += "\\\"";
			break;
		case '\\':
			out += "\\\\";
			break;
		case '\n':
			out += "\\n";
			break;
		case '\r':
			out += "\\r";
			break;
		case '\t':
			out += "\\t";
			break;
		default:
			out += c;
			break;
		}
	}
	return out;
}

// Resolve the azure secret matching `path` from the opener → its fields as a JSON object. "{}" if there is no
// context or no matching azure secret (the managed side then uses DefaultAzureCredential — Fabric managed /
// workspace identity, or a local az-login). Best-effort: never throws (a resolution failure must not abort IO).
std::string ResolveCredJson(const std::string &path, optional_ptr<FileOpener> opener) {
	auto context = FileOpener::TryGetClientContext(opener);
	if (!context) {
		return "{}";
	}
	try {
		auto &secret_manager = SecretManager::Get(*context);
		auto transaction = CatalogTransaction::GetSystemCatalogTransaction(*context);
		// Scope-match an azure secret to the path. `onelake://` won't match an azure secret's default scopes
		// (azure://, abfss://, …), so fall back to ANY registered azure secret — a Fabric workspace normally has
		// one SP, and this mirrors how the Delta catalog reuses a plain `azure` secret for OneLake.
		auto match = secret_manager.LookupSecret(transaction, path, "azure");
		optional_ptr<const BaseSecret> secret;
		unique_ptr<SecretEntry> fallback; // keeps the fallback secret alive for the field reads below
		if (match.HasMatch()) {
			secret = match.GetSecret();
		} else {
			for (auto &entry : secret_manager.AllSecrets(transaction)) {
				if (entry.secret && entry.secret->GetType() == "azure") {
					fallback = make_uniq<SecretEntry>(entry);
					secret = fallback->secret.get();
					break;
				}
			}
		}
		if (!secret) {
			return "{}";
		}
		const auto &kv = static_cast<const KeyValueSecret &>(*secret);
		std::string json = "{";
		bool first = true;
		for (auto &field : kv.secret_map) {
			if (field.second.IsNull()) {
				continue;
			}
			if (!first) {
				json += ",";
			}
			first = false;
			json += "\"" + EscapeJson(field.first) + "\":\"" + EscapeJson(field.second.ToString()) + "\"";
		}
		json += "}";
		return json;
	} catch (...) {
		return "{}";
	}
}

// Extract string/int values for a key from our own well-formed glob JSON object substring.
// (A minimal, shape-specific scan — the JSON is produced by OneLakeForwardFs.Glob, not arbitrary input.)
bool ExtractJsonString(const std::string &obj, const std::string &key, std::string &out) {
	auto k = "\"" + key + "\":\"";
	auto pos = obj.find(k);
	if (pos == std::string::npos) {
		return false;
	}
	pos += k.size();
	std::string value;
	for (; pos < obj.size(); pos++) {
		char c = obj[pos];
		if (c == '\\' && pos + 1 < obj.size()) {
			value += obj[++pos];
			continue;
		}
		if (c == '"') {
			break;
		}
		value += c;
	}
	out = value;
	return true;
}

int64_t ExtractJsonInt(const std::string &obj, const std::string &key) {
	auto k = "\"" + key + "\":";
	auto pos = obj.find(k);
	if (pos == std::string::npos) {
		return -1;
	}
	pos += k.size();
	try {
		return std::stoll(obj.substr(pos));
	} catch (...) {
		return -1;
	}
}

class ArrowNetOneLakeFileHandle : public FileHandle {
public:
	ArrowNetOneLakeFileHandle(FileSystem &fs, const string &path, FileOpenFlags flags, ArrowNetHandle handle,
	                          int64_t size, bool is_write)
	    : FileHandle(fs, path, flags), managed_handle(handle), file_size(size), position(0), write_mode(is_write) {
	}
	~ArrowNetOneLakeFileHandle() override {
		Close();
	}
	void Close() override {
		if (managed_handle) {
			if (write_mode) {
				OneLakeCloseWrite(managed_handle); // flush + commit at the final length
			} else {
				OneLakeClose(managed_handle);
			}
			managed_handle = nullptr;
		}
	}

	ArrowNetHandle managed_handle;
	int64_t file_size;
	idx_t position;
	bool write_mode;
};

class ArrowNetOneLakeFileSystem : public FileSystem {
public:
	unique_ptr<FileHandle> OpenFile(const string &path, FileOpenFlags flags,
	                                optional_ptr<FileOpener> opener) override {
		return OpenInternal(path, flags, opener, /*known_size=*/-1);
	}

	// Extended open: a glob-fed open (read_parquet('onelake://…/*.parquet'), the Delta native reader's
	// file list) carries the listing's extended_info — a known file_size lets the managed open SKIP its
	// per-file GetProperties round trip (constructing the DataLake client does no IO).
	bool SupportsOpenFileExtended() const override {
		return true;
	}
	unique_ptr<FileHandle> OpenFileExtended(const OpenFileInfo &file, FileOpenFlags flags,
	                                        optional_ptr<FileOpener> opener) override {
		int64_t known_size = -1;
		if (file.extended_info && !flags.OpenForWriting()) {
			auto it = file.extended_info->options.find("file_size");
			if (it != file.extended_info->options.end()) {
				try {
					known_size = it->second.GetValue<int64_t>();
				} catch (...) {
					known_size = -1;
				}
			}
		}
		return OpenInternal(file.path, flags, opener, known_size);
	}

private:
	unique_ptr<FileHandle> OpenInternal(const string &path, FileOpenFlags flags, optional_ptr<FileOpener> opener,
	                                    int64_t known_size) {
		auto cred = ResolveCredJson(path, opener);
		if (flags.OpenForWriting()) {
			// Plain sequential file write (COPY … TO 'onelake://…') — create/overwrite, appends follow.
			// EXCLUSIVE_CREATE => put-if-absent (ADLS conditional create) — the atomic-commit primitive
			// engineered-wood's rename emulation rides on (an existing target fails the open).
			auto handle = OneLakeOpenWrite(path, cred, flags.ExclusiveCreate());
			return make_uniq<ArrowNetOneLakeFileHandle>(*this, path, flags, handle, 0, /*is_write=*/true);
		}
		int64_t size = 0;
		auto handle = OneLakeOpen(path, cred, size, known_size);
		return make_uniq<ArrowNetOneLakeFileHandle>(*this, path, flags, handle, size, /*is_write=*/false);
	}

public:

	void Read(FileHandle &handle, void *buffer, int64_t nr_bytes, idx_t location) override {
		auto &h = handle.Cast<ArrowNetOneLakeFileHandle>();
		OneLakeRead(h.managed_handle, buffer, nr_bytes, static_cast<int64_t>(location));
	}

	int64_t Read(FileHandle &handle, void *buffer, int64_t nr_bytes) override {
		auto &h = handle.Cast<ArrowNetOneLakeFileHandle>();
		int64_t remaining = h.file_size - static_cast<int64_t>(h.position);
		int64_t to_read = nr_bytes < remaining ? nr_bytes : remaining;
		if (to_read <= 0) {
			return 0;
		}
		OneLakeRead(h.managed_handle, buffer, to_read, static_cast<int64_t>(h.position));
		h.position += static_cast<idx_t>(to_read);
		return to_read;
	}

	int64_t GetFileSize(FileHandle &handle) override {
		return handle.Cast<ArrowNetOneLakeFileHandle>().file_size;
	}

	// The parquet reader / ExternalFileCache asks for the mtime as a cache version tag. Delta data files are
	// immutable (a new commit writes a new file), so a constant is correct — and avoids an extra round-trip.
	timestamp_t GetLastModifiedTime(FileHandle &handle) override {
		return timestamp_t(0);
	}

	void Seek(FileHandle &handle, idx_t location) override {
		handle.Cast<ArrowNetOneLakeFileHandle>().position = location;
	}
	idx_t SeekPosition(FileHandle &handle) override {
		return handle.Cast<ArrowNetOneLakeFileHandle>().position;
	}
	void Reset(FileHandle &handle) override {
		handle.Cast<ArrowNetOneLakeFileHandle>().position = 0;
	}
	bool CanSeek() override {
		return true;
	}
	bool OnDiskFile(FileHandle &handle) override {
		return false;
	}
	bool IsManuallySet() override {
		return true;
	}
	bool CanHandleFile(const string &fpath) override {
		return StringUtil::StartsWith(fpath, kScheme);
	}

	bool FileExists(const string &filename, optional_ptr<FileOpener> opener) override {
		return OneLakeExists(filename, ResolveCredJson(filename, opener));
	}

	vector<OpenFileInfo> Glob(const string &path, FileOpener *opener) override {
		vector<OpenFileInfo> result;
		auto cred = ResolveCredJson(path, opener);
		auto json = OneLakeGlob(path, cred);
		// Parse our own array of {"path":"onelake://...","size":N}. Split on object boundaries.
		size_t pos = 0;
		while (true) {
			auto open = json.find('{', pos);
			if (open == std::string::npos) {
				break;
			}
			auto close = json.find('}', open);
			if (close == std::string::npos) {
				break;
			}
			auto obj = json.substr(open, close - open + 1);
			std::string p;
			if (ExtractJsonString(obj, "path", p) && !p.empty()) {
				OpenFileInfo info(p);
				// Everything the DataLake listing carries for FREE rides along under the same
				// extended_info keys httpfs fills (file_size / last_modified / etag) — so consumers
				// (HostFsGlob, the parquet metadata probe, OpenFileExtended below) never need a
				// per-file properties round trip for them.
				int64_t size = ExtractJsonInt(obj, "size");
				int64_t modified_ms = ExtractJsonInt(obj, "last_modified");
				std::string etag;
				bool has_etag = ExtractJsonString(obj, "etag", etag) && !etag.empty();
				if (size >= 0 || modified_ms >= 0 || has_etag) {
					info.extended_info = make_shared_ptr<ExtendedOpenFileInfo>();
					if (size >= 0) {
						info.extended_info->options["file_size"] = Value::UBIGINT((uint64_t)size);
					}
					if (modified_ms >= 0) {
						info.extended_info->options["last_modified"] =
						    Value::TIMESTAMP(timestamp_t(modified_ms * 1000)); // epoch ms -> micros
					}
					if (has_etag) {
						info.extended_info->options["etag"] = Value(etag);
					}
				}
				result.push_back(std::move(info));
			}
			pos = close + 1;
		}
		return result;
	}

	// --- write: sequential append only (Azure DFS + COPY are sequential) ---
	void Write(FileHandle &handle, void *buffer, int64_t nr_bytes, idx_t location) override {
		auto &h = handle.Cast<ArrowNetOneLakeFileHandle>();
		if (static_cast<int64_t>(location) != static_cast<int64_t>(h.position)) {
			throw NotImplementedException("onelake:// FileSystem supports sequential writes only (got a write at "
			                              "offset %lld, expected %lld)",
			                              static_cast<long long>(location), static_cast<long long>(h.position));
		}
		OneLakeWrite(h.managed_handle, buffer, nr_bytes);
		h.position += static_cast<idx_t>(nr_bytes);
	}
	int64_t Write(FileHandle &handle, void *buffer, int64_t nr_bytes) override {
		auto &h = handle.Cast<ArrowNetOneLakeFileHandle>();
		OneLakeWrite(h.managed_handle, buffer, nr_bytes);
		h.position += static_cast<idx_t>(nr_bytes);
		return nr_bytes;
	}
	void FileSync(FileHandle &) override {
		// The managed side flushes + commits on close (OneLakeCloseWrite); nothing to do mid-stream.
	}
	// Single-file delete (idempotent) — engineered-wood's commit rename is emulated as
	// exclusive-create-copy + delete-source, so a Delta write over onelake:// needs RemoveFile (v61).
	void RemoveFile(const string &path, optional_ptr<FileOpener> opener) override {
		OneLakeRemove(path, ResolveCredJson(path, opener));
	}
	// --- other mutate ops: not supported ---
	void MoveFile(const string &, const string &, optional_ptr<FileOpener>) override {
		throw NotImplementedException("onelake:// FileSystem: MoveFile is not supported");
	}
	// Directory ops: OneLake / ADLS Gen2 directories are IMPLICIT (a blob write at `<dir>/<file>`
	// materializes the whole hierarchy). DuckDB's partitioned COPY (PARTITION_BY) checks the target directory
	// during setup — support those checks so a partitioned COPY to onelake:// works (the Hive col=val/ files
	// are then written by OpenFile-for-writing, exactly as in the single-file path).
	bool DirectoryExists(const string &, optional_ptr<FileOpener>) override {
		// Report the target as not-yet-a-directory: DuckDB then calls CreateDirectory (a no-op here) and
		// proceeds to write. Correct because dirs are implicit — no explicit dir need pre-exist.
		return false;
	}
	void CreateDirectory(const string &, optional_ptr<FileOpener>) override {
		// No-op: an ADLS Gen2 / OneLake directory is created implicitly by the first blob written under it
		// (OneLakeOpenWrite → DataLakeFileClient.CreateAsync materializes the parent hierarchy).
	}
	void RemoveDirectory(const string &, optional_ptr<FileOpener>) override {
		throw NotImplementedException("onelake:// FileSystem: RemoveDirectory is not supported");
	}

	std::string GetName() const override {
		return "ArrowNetOneLakeFileSystem";
	}
};

} // namespace

void RegisterOneLakeFileSystem(DatabaseInstance &db) {
	auto &fs = db.GetFileSystem();
	fs.RegisterSubSystem(make_uniq<ArrowNetOneLakeFileSystem>());
}

} // namespace arrownet
