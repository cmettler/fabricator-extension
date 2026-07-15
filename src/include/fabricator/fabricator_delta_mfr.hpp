//===----------------------------------------------------------------------===//
// fabricator/fabricator_delta_mfr.hpp
//
// Delta native-read via DuckDB's MultiFileReader (docs/multifile-delta.md Phase A). Registers
// `fabricator_delta_mfr_scan(path)` — a clone of parquet_scan whose MultiFileReader gets the EXACT active data
// files from the managed side (engineered-wood, over delta_list_files) and lets DuckDB's native parquet reader
// read them (cached over onelake:// for OneLake). Slice 1a: file list only (no DV / partition / pushdown yet).
//===----------------------------------------------------------------------===//
#pragma once

namespace duckdb {
class ExtensionLoader;
}

namespace fabricator {

// Register the fabricator_delta_mfr_scan table function (best-effort; requires the parquet extension, which is
// statically linked / autoloaded).
void RegisterDeltaMultiFileScan(duckdb::ExtensionLoader &loader);

} // namespace fabricator
