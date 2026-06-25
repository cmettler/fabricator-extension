//===----------------------------------------------------------------------===//
//                         arrownet — Delta lakehouse scan (engineered-wood)
//
// arrownet_delta.hpp
//
// Registers the `arrownet_delta_scan(path)` table function: a managed (C#)
// Delta Lake reader (Curt Hagenlocher's engineered-wood) whose IO goes through
// DuckDB's FileSystem via the host reverse-callbacks (so az://, s3://, https://
// and DuckDB secrets all work), returning the table as Arrow → DuckDB.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb.hpp"

namespace duckdb {

void RegisterDeltaScan(ExtensionLoader &loader);

} // namespace duckdb
