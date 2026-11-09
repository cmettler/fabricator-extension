#pragma once

#include "duckdb.hpp"
#include "duckdb/function/table_function.hpp"
#include "fabricator/abi.h"

namespace duckdb {
class TableCatalogEntry;
}

namespace fabricator {

//! The FILE SCAN (ABI v101 table_file_plan): a catalog table whose provider can list the data files its scan would
//! read is scanned by DuckDB's own parquet reader over exactly those files — a copy of `parquet_scan` driven by our
//! MultiFileReader — instead of the provider's Arrow stream. The provider answers the plan (files, deleted
//! positions, the row id of each file's first row); no row crosses into managed code.
//!
//! Returns false when the provider declines (or the plan cannot be used); the caller then builds the ordinary scan.
//! The caller must have established the call's ambients (FabricatorSetActiveTxn) — the plan reads the transaction's
//! pinned snapshot. Off unless FABRICATOR_FILE_SCAN=1 (an experiment, docs/delta-read-path.md).
bool TryBuildFileScan(duckdb::ClientContext &context, duckdb::TableCatalogEntry &table, FabricatorHandle table_handle,
                      const std::string &at_unit, const std::string &at_value,
                      duckdb::unique_ptr<duckdb::FunctionData> &bind_data, duckdb::TableFunction &result);

//! Whether the file scan is switched on (FABRICATOR_FILE_SCAN=1).
bool FileScanEnabled();

//! The table a file scan's bind data reads, and the AT clause it was bound with — what a plan copy needs to rebuild
//! the scan (FabricatorTableEntry's file-scan serialize/deserialize).
duckdb::TableCatalogEntry &FileScanTable(const duckdb::FunctionData &bind_data, std::string &at_unit,
                                         std::string &at_value);

} // namespace fabricator
