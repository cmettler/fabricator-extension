// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — host query (reuse the host DuckDB engine, over Arrow)
//===----------------------------------------------------------------------===//
//
// Runs a DuckDB query on a FRESH host connection (own ClientContext/transaction — never the in-flight one,
// which is non-reentrant and would corrupt the outer query's state) and exposes the result as Arrow through
// the existing arrow_ingest scan path. Slice 1 surfaces it as the table function `fabricator_host_query(sql)`;
// the C#-callable `host_query` host service + parameter binding + named Arrow inputs + the replacement-scan
// layer build on the same `MakeHostQueryStream` core. See docs/host-query.md.
//
#pragma once

#include "duckdb.hpp"
#include "duckdb/catalog/catalog_search_path.hpp" // CatalogSearchEntry, for HostQuerySession
#include "duckdb/main/extension/extension_loader.hpp"

struct ArrowArrayStream;

namespace duckdb {

// One named Arrow input to register as a connection-scoped view before the query runs (data-in).
struct HostQueryInput {
	string name;
	ArrowArrayStream *stream; // not owned here — consumed (+ released) by DuckDB's arrow_scan during the query
};

// SESSION state copied from the CALLING context onto the fresh connection, so `fabricator_host_query('…')`
// resolves names and renders timestamps the way the surrounding session does. Without it the fresh connection
// starts at DuckDB's defaults, and `USE lake.main; SELECT * FROM fabricator_host_query('SELECT * FROM t')`
// fails while the same SQL works one line earlier — the search path was `memory.main`.
//
// Only what a session actually owns is here: GLOBAL settings (threads, …) live on the DatabaseInstance, so the
// fresh connection already sees them. Deliberately captured BY VALUE at bind rather than by holding the
// calling `ClientContext *`: the factory that opens the connection runs later (and can re-run per execution),
// so a stored context pointer is a dangling-pointer bug of exactly the kind commit 142b350 removed from the
// host-FS opener.
//
// Applied ONLY to the user-facing table function. The C#-callable `host_query` service deliberately does NOT
// inherit — see docs/host-query.md ("Session state").
struct HostQuerySession {
	//! The caller's explicitly-set search path (`USE x` / `SET search_path`). Empty => leave the default.
	vector<CatalogSearchEntry> search_path;
	//! The caller's `TimeZone`. Empty => leave the default (also what an ICU-less build yields).
	string time_zone;
};

// Runs `sql` on a fresh Connection over `db` and fills `out` with a self-owning ArrowArrayStream over the
// result (the Connection + result live until `out` is released). When `params` is non-null it is a 1-row
// Arrow stream whose columns bind positionally to the statement's parameters (via a prepared statement;
// ownership is consumed here). Each `inputs` entry is registered as a connection-scoped view (so the SQL can
// reference it by name) before the query. `out_context` (nullable) receives the fresh connection's
// ClientContext for out-of-band interruption (the v66 host_query cancellation). `session` (nullable) copies
// the caller's search path + TimeZone onto the fresh connection — passed by the table function, NOT by the C#
// host service. Throws on a query error.
void MakeHostQueryStream(DatabaseInstance &db, const string &sql, ArrowArrayStream *params,
                         const vector<HostQueryInput> &inputs, ArrowArrayStream &out,
                         shared_ptr<ClientContext> *out_context = nullptr,
                         const HostQuerySession *session = nullptr);

// Registers the `fabricator_host_query(VARCHAR)` table function.
void RegisterHostQuery(ExtensionLoader &loader);

} // namespace duckdb
