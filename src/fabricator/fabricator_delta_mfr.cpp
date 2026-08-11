#include "fabricator/fabricator_delta_mfr.hpp"

#include "fabricator/clr_host.hpp"
#include "duckdb/catalog/catalog_entry/table_function_catalog_entry.hpp"
#include "duckdb/common/multi_file/base_file_reader.hpp"
#include "duckdb/common/multi_file/multi_file_data.hpp"
#include "duckdb/common/multi_file/multi_file_list.hpp"
#include "duckdb/common/multi_file/multi_file_reader.hpp"
#include "duckdb/common/multi_file/multi_file_states.hpp"
#include "duckdb/common/open_file_info.hpp"
#include "duckdb/common/types/selection_vector.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/main/extension_helper.hpp"

#include <algorithm>

namespace fabricator {

using namespace duckdb;

namespace {

// One active Delta data file: its URI + (slice 1b) the sorted DELETED row positions (empty = no DV).
struct DeltaFileEntry {
	string path;
	vector<int64_t> deleted; // sorted, file-relative row positions
};

// Parse the managed JSON array [{"path":"<uri>"[, "dv":[p0,p1,...]]}, ...] (our own well-formed output).
vector<DeltaFileEntry> ParseFileList(const std::string &json) {
	vector<DeltaFileEntry> out;
	size_t pos = 0;
	while (true) {
		auto open = json.find('{', pos);
		if (open == string::npos) {
			break;
		}
		auto close = json.find('}', open);
		if (close == string::npos) {
			break;
		}
		auto obj = json.substr(open, close - open + 1);
		DeltaFileEntry e;
		// path (string)
		const std::string pkey = "\"path\":\"";
		auto pp = obj.find(pkey);
		if (pp != string::npos) {
			pp += pkey.size();
			for (; pp < obj.size(); pp++) {
				char c = obj[pp];
				if (c == '\\' && pp + 1 < obj.size()) {
					e.path += obj[++pp];
					continue;
				}
				if (c == '"') {
					break;
				}
				e.path += c;
			}
		}
		// dv (array of ints) — the DV positions never contain '{' or '}', so the object's '}' is unambiguous.
		auto dvp = obj.find("\"dv\":[");
		if (dvp != string::npos) {
			dvp += 6;
			auto endb = obj.find(']', dvp);
			std::string nums = obj.substr(dvp, endb == string::npos ? string::npos : endb - dvp);
			size_t i = 0;
			while (i < nums.size()) {
				while (i < nums.size() && (nums[i] == ',' || nums[i] == ' ')) {
					i++;
				}
				size_t start = i;
				while (i < nums.size() && nums[i] != ',') {
					i++;
				}
				if (i > start) {
					try {
						e.deleted.push_back(std::stoll(nums.substr(start, i - start)));
					} catch (...) {
					}
				}
			}
		}
		if (!e.path.empty()) {
			out.push_back(std::move(e));
		}
		pos = close + 1;
	}
	return out;
}

// A per-file DeleteFilter over sorted deleted positions — the parquet reader calls it per row range and we
// return the KEPT rows, so DuckDB's native read excludes the DV-deleted rows (correctness for DV tables).
class FabricatorDeltaDeleteFilter : public DeleteFilter {
public:
	explicit FabricatorDeltaDeleteFilter(const vector<int64_t> &deleted_p) : deleted(deleted_p) {
	}
	idx_t Filter(row_t start_row_index, idx_t count, SelectionVector &result_sel) override {
		if (count == 0) {
			return 0;
		}
		// The caller passes an uninitialized selection (Initialize(nullptr) → null sel_vector); the DeleteFilter
		// owns allocating it before writing (matches DuckDB's delta extension).
		result_sel.Initialize(STANDARD_VECTOR_SIZE);
		idx_t sel_count = 0;
		for (idx_t i = 0; i < count; i++) {
			row_t row_id = start_row_index + static_cast<row_t>(i);
			if (!std::binary_search(deleted.begin(), deleted.end(), row_id)) {
				result_sel.set_index(sel_count++, i);
			}
		}
		return sel_count;
	}

private:
	const vector<int64_t> deleted; // sorted
};

// A MultiFileList carrying, per file, the deleted positions (parallel to the file list). The MultiFileReader
// reads them back in FinalizeBind (via the reader's file_list_idx) to attach the DeleteFilter.
class FabricatorDeltaMultiFileList : public SimpleMultiFileList {
public:
	FabricatorDeltaMultiFileList(vector<OpenFileInfo> files, vector<vector<int64_t>> dvs)
	    : SimpleMultiFileList(std::move(files)), file_dvs(std::move(dvs)) {
	}
	const vector<int64_t> *GetDv(idx_t idx) const {
		if (idx < file_dvs.size() && !file_dvs[idx].empty()) {
			return &file_dvs[idx];
		}
		return nullptr;
	}

private:
	vector<vector<int64_t>> file_dvs;
};

// A MultiFileReader that, instead of globbing, gets the EXACT active Delta files (+ per-file deletion vectors)
// from the managed side and hands them to DuckDB's native parquet reader. Slice 1a: file list; slice 1b: DV.
// Partition-value constants + filter pushdown (Complex/DynamicFilterPushdown) are later slices.
class FabricatorDeltaMultiFileReader : public MultiFileReader {
public:
	static unique_ptr<MultiFileReader> CreateInstance(const TableFunction &) {
		return make_uniq<FabricatorDeltaMultiFileReader>();
	}

	shared_ptr<MultiFileList> CreateFileList(ClientContext &context, const vector<string> &paths,
	                                         const FileGlobInput &glob_input) override {
		if (paths.empty()) {
			throw InvalidInputException("fabricator_delta_mfr_scan: a Delta table path is required");
		}
		// The managed side reads the _delta_log through the host FileSystem; give it the calling operator's
		// ClientContext as the opener (secret resolution for OneLake), mirroring the global host-FS readers.
		SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), SessionKeyFor(&context));
		auto json = DeltaListFiles(paths[0], ""); // "" push filter (file pruning is a later slice)
		auto entries = ParseFileList(json);
		vector<OpenFileInfo> files;
		vector<vector<int64_t>> dvs;
		files.reserve(entries.size());
		dvs.reserve(entries.size());
		for (auto &e : entries) {
			files.emplace_back(e.path);
			dvs.push_back(std::move(e.deleted));
		}
		return make_shared_ptr<FabricatorDeltaMultiFileList>(std::move(files), std::move(dvs));
	}

	// The base returns nullptr (no global state). We need one carrying the file_list so FinalizeBind can read
	// back each file's deletion vector (via the reader's file_list_idx) and attach the DeleteFilter.
	unique_ptr<MultiFileReaderGlobalState>
	InitializeGlobalState(ClientContext &context, const MultiFileOptions &file_options,
	                      const MultiFileReaderBindData &bind_data, const MultiFileList &file_list,
	                      const vector<MultiFileColumnDefinition> &global_columns,
	                      const vector<ColumnIndex> &global_column_ids) override {
		return make_uniq<MultiFileReaderGlobalState>(vector<LogicalType>(), &file_list);
	}

	void FinalizeBind(MultiFileReaderData &reader_data, const MultiFileOptions &file_options,
	                  const MultiFileReaderBindData &options,
	                  const vector<MultiFileColumnDefinition> &global_columns,
	                  const vector<ColumnIndex> &global_column_ids, ClientContext &context,
	                  optional_ptr<MultiFileReaderGlobalState> global_state) override {
		MultiFileReader::FinalizeBind(reader_data, file_options, options, global_columns, global_column_ids,
		                              context, global_state);
		if (!global_state || !global_state->file_list || !reader_data.reader) {
			return;
		}
		auto delta_list = dynamic_cast<const FabricatorDeltaMultiFileList *>(global_state->file_list.get());
		if (!delta_list || !reader_data.reader->file_list_idx.IsValid()) {
			return;
		}
		auto dv = delta_list->GetDv(reader_data.reader->file_list_idx.GetIndex());
		if (dv) {
			// Push the DV into the parquet scan as a per-file row filter (excludes the deleted rows).
			reader_data.reader->deletion_filter = make_uniq<FabricatorDeltaDeleteFilter>(*dv);
		}
	}
};

} // namespace

void RegisterDeltaMultiFileScan(ExtensionLoader &loader) {
	// The native read needs the parquet reader. Try to bring it in, but note what this call actually
	// does: ExtensionHelper::AutoLoadExtension autoinstalls and then LoadExternalExtension()s — it
	// resolves an extension from DISK and never consults the statically linked set. So on a build where
	// parquet is linked-but-not-yet-loaded it succeeds only if a parquet loadable happens to be
	// installed in the extension directory.
	try {
		ExtensionHelper::AutoLoadExtension(loader.GetDatabaseInstance(), "parquet");
	} catch (...) {
		// Non-fatal — see the TryGetTableFunction below.
	}

	// Clone parquet_scan and swap in our MultiFileReader (the duckdb-delta pattern): DuckDB's native parquet
	// read machinery, driven by our managed file list.
	//
	// TOLERATE ITS ABSENCE. This used to be an unconditional GetTableFunction, which THROWS — and because
	// this runs during Load, one unavailable parquet took down the whole extension: every unrelated
	// function (fabricator_query, the global scalars, the Delta catalog) became unusable with
	// "Function with name \"parquet_scan\" not found". Skipping one secondary surface is the proportionate
	// response. Official DuckDB builds have parquet built in, so this branch is not reachable there; it is
	// reachable in a build where parquet is a separately-linked extension that nothing has loaded yet,
	// which is exactly our own test binary (a suite must `require parquet` BEFORE `require fabricator` to
	// exercise fabricator_delta_mfr_scan).
	auto parquet_lookup = loader.TryGetTableFunction("parquet_scan");
	if (!parquet_lookup) {
		return;
	}
	auto &parquet_entry = parquet_lookup->Cast<TableFunctionCatalogEntry>();
	auto function_set = parquet_entry.functions;
	for (auto &function : function_set.functions) {
		function.get_multi_file_reader = FabricatorDeltaMultiFileReader::CreateInstance;
		// Disable machinery that assumes the parquet bind data / would not round-trip our file list.
		function.serialize = nullptr;
		function.deserialize = nullptr;
		function.statistics = nullptr;
		function.table_scan_progress = nullptr;
		function.get_bind_info = nullptr;
		function.named_parameters.erase("schema");
		function.name = "fabricator_delta_mfr_scan";
	}
	function_set.name = "fabricator_delta_mfr_scan";
	loader.RegisterFunction(std::move(function_set));
}

} // namespace fabricator
