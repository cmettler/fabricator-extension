//===----------------------------------------------------------------------===//
//                         arrownet — per-transaction connection routing
//===----------------------------------------------------------------------===//
//
// Helper to tell the managed backend which DuckDB transaction a connection-using call belongs to, so it
// keys its per-transaction provider connection by it (concurrent DuckDB transactions — e.g. dbt --threads N
// — each get their own connection instead of colliding on one shared, non-thread-safe connection; see
// docs/transaction-concurrency.md). Call it IMMEDIATELY before each connection-using bridge call, on the
// same thread (the bridge calls are synchronous), so the id rides the managed per-thread ambient.

#pragma once

#include "arrownet/clr_host.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

namespace duckdb {

// Set the active DuckDB transaction id (global_transaction_id) from the client context.
inline void ArrowNetSetActiveTxn(ArrowNetHandle handle, ClientContext &context) {
	arrownet::SetActiveTxn(handle, (int64_t)MetaTransaction::Get(context).global_transaction_id);
}

} // namespace duckdb
