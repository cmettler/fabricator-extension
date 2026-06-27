//===----------------------------------------------------------------------===//
//                         arrownet — host query (reuse the host DuckDB engine, over Arrow)
//===----------------------------------------------------------------------===//
//
// Runs a DuckDB query on a FRESH host connection (own ClientContext/transaction — never the in-flight one,
// which is non-reentrant and would corrupt the outer query's state) and exposes the result as Arrow through
// the existing arrow_ingest scan path. Slice 1 surfaces it as the table function `arrownet_host_query(sql)`;
// the C#-callable `host_query` host service + parameter binding + named Arrow inputs + the replacement-scan
// layer build on the same `MakeHostQueryStream` core. See docs/host-query.md.
//
#pragma once

#include "duckdb.hpp"
#include "duckdb/main/extension/extension_loader.hpp"

struct ArrowArrayStream;

namespace duckdb {

// One named Arrow input to register as a connection-scoped view before the query runs (data-in).
struct HostQueryInput {
	string name;
	ArrowArrayStream *stream; // not owned here — consumed (+ released) by DuckDB's arrow_scan during the query
};

// Runs `sql` on a fresh Connection over `db` and fills `out` with a self-owning ArrowArrayStream over the
// result (the Connection + result live until `out` is released). When `params` is non-null it is a 1-row
// Arrow stream whose columns bind positionally to the statement's parameters (via a prepared statement;
// ownership is consumed here). Each `inputs` entry is registered as a connection-scoped view (so the SQL can
// reference it by name) before the query. Throws on a query error.
void MakeHostQueryStream(DatabaseInstance &db, const string &sql, ArrowArrayStream *params,
                         const vector<HostQueryInput> &inputs, ArrowArrayStream &out);

// Registers the `arrownet_host_query(VARCHAR)` table function.
void RegisterHostQuery(ExtensionLoader &loader);

} // namespace duckdb
