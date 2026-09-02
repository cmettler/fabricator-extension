// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — per-transaction connection routing
//===----------------------------------------------------------------------===//
//
// Helper to tell the managed backend which DuckDB transaction a connection-using call belongs to, so it
// keys its per-transaction provider connection by it (concurrent DuckDB transactions — e.g. dbt --threads N
// — each get their own connection instead of colliding on one shared, non-thread-safe connection; see
// docs/transaction-concurrency.md). Call it IMMEDIATELY before each connection-using bridge call, on the
// same thread (the bridge calls are synchronous), so the id rides the managed per-thread ambient.

#pragma once

#include "fabricator/clr_host.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/transaction/meta_transaction.hpp"

namespace duckdb {

// Set the active DuckDB transaction id (global_transaction_id) from the client context. Also set the active
// host-FS opener (this context) so a host-FS C# binding running on this call — e.g. a global collector/in-out
// that writes a lakehouse table through DuckDB's FileSystem — can resolve DuckDB secrets. Harmless for
// SQL/DAX bindings (only host-FS bindings read the opener ambient). Mirrors the pair set in arrow_ingest's
// scan init; this helper covers the other connection-using callsites (DML/DDL/exchange/collector).
inline void FabricatorSetActiveTxn(FabricatorHandle handle, ClientContext &context) {
	fabricator::SetActiveTxn(handle, (int64_t)MetaTransaction::Get(context).global_transaction_id);
	fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context), fabricator::SessionKeyFor(&context));
}

// The caller's context, for a crossing that RESTORES the ambients rather than merely overwriting them
// (fabricator::CallContext / CallScope.cs). Use this — never FabricatorSetActiveTxn — wherever the crossing
// can happen inside somebody else's statement: a global SCALAR is the case it was built for, since it is
// evaluated wherever it is CALLED, including inside a nested host query an OUTER operation is running.
inline fabricator::CallContext MakeCallContext(ClientContext &context) {
	fabricator::CallContext call;
	call.opener = reinterpret_cast<FabricatorHandle>(&context);
	call.session = fabricator::SessionKeyFor(&context);
	call.txn_id = (int64_t)MetaTransaction::Get(context).global_transaction_id;
	return call;
}

} // namespace duckdb
