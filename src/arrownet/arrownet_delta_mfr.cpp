#include "arrownet/arrownet_delta_mfr.hpp"

#include "arrownet/clr_host.hpp"
#include "duckdb/catalog/catalog_entry/table_function_catalog_entry.hpp"
#include "duckdb/common/multi_file/multi_file_list.hpp"
#include "duckdb/common/multi_file/multi_file_reader.hpp"
#include "duckdb/common/open_file_info.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/main/extension_helper.hpp"

namespace arrownet {

using namespace duckdb;

namespace {

// Extract the "path" values from the managed JSON array [{"path":"<uri>", ...}, ...] (our own well-formed
// output — a minimal, shape-specific scan, not a general JSON parser).
vector<OpenFileInfo> ParseFileList(const std::string &json) {
	vector<OpenFileInfo> files;
	const std::string key = "\"path\":\"";
	size_t pos = 0;
	while (true) {
		auto k = json.find(key, pos);
		if (k == std::string::npos) {
			break;
		}
		k += key.size();
		std::string value;
		for (; k < json.size(); k++) {
			char c = json[k];
			if (c == '\\' && k + 1 < json.size()) {
				value += json[++k];
				continue;
			}
			if (c == '"') {
				break;
			}
			value += c;
		}
		files.emplace_back(value);
		pos = k;
	}
	return files;
}

// A MultiFileReader that, instead of globbing, gets the EXACT active Delta files from the managed side and hands
// them to DuckDB's native parquet reader. Slice 1a: file list only. DV (a per-file DeleteFilter in FinalizeBind),
// partition-value constants, and filter pushdown (Complex/DynamicFilterPushdown → re-prune via the managed side)
// are later slices — see docs/multifile-delta.md.
class ArrowNetDeltaMultiFileReader : public MultiFileReader {
public:
	static unique_ptr<MultiFileReader> CreateInstance(const TableFunction &) {
		return make_uniq<ArrowNetDeltaMultiFileReader>();
	}

	shared_ptr<MultiFileList> CreateFileList(ClientContext &context, const vector<string> &paths,
	                                         const FileGlobInput &glob_input) override {
		if (paths.empty()) {
			throw InvalidInputException("arrownet_delta_mfr_scan: a Delta table path is required");
		}
		// The managed side reads the _delta_log through the host FileSystem; give it the calling operator's
		// ClientContext as the opener (secret resolution for OneLake), mirroring the global host-FS readers.
		SetActiveOpener(reinterpret_cast<ArrowNetHandle>(&context));
		auto json = DeltaListFiles(paths[0], ""); // "" push filter (pruning is a later slice)
		auto files = ParseFileList(json);
		return make_shared_ptr<SimpleMultiFileList>(std::move(files));
	}
};

} // namespace

void RegisterDeltaMultiFileScan(ExtensionLoader &loader) {
	// The native read needs the parquet reader (statically linked here; autoload is a best-effort safety net).
	try {
		ExtensionHelper::AutoLoadExtension(loader.GetDatabaseInstance(), "parquet");
	} catch (...) {
		// parquet is linked into this build; a failed autoload is non-fatal.
	}

	// Clone parquet_scan and swap in our MultiFileReader (the duckdb-delta pattern): DuckDB's native parquet
	// read machinery, driven by our managed file list.
	auto &parquet_entry = loader.GetTableFunction("parquet_scan");
	auto function_set = parquet_entry.functions;
	for (auto &function : function_set.functions) {
		function.get_multi_file_reader = ArrowNetDeltaMultiFileReader::CreateInstance;
		// Disable machinery that assumes the parquet bind data / would not round-trip our file list.
		function.serialize = nullptr;
		function.deserialize = nullptr;
		function.statistics = nullptr;
		function.table_scan_progress = nullptr;
		function.get_bind_info = nullptr;
		function.named_parameters.erase("schema");
		function.name = "arrownet_delta_mfr_scan";
	}
	function_set.name = "arrownet_delta_mfr_scan";
	loader.RegisterFunction(std::move(function_set));
}

} // namespace arrownet
