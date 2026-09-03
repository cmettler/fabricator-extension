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
#include "duckdb/common/atomic.hpp"               // atomic<int32_t>, for HostConnection
#include "duckdb/main/extension/extension_loader.hpp"

struct ArrowArrayStream;

namespace duckdb {

// One named Arrow input to register as a TEMPORARY (connection-scoped) view before the query runs
// (data-in) — see RegisterArrowInputView for why it must not be a catalog view.
struct HostQueryInput {
	string name;
	ArrowArrayStream *stream; // not owned here — ADOPTED by OwnedArrowInputs, which releases it with the stream
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

// A PINNED host connection (ABI v84): several host_query calls run on ONE DuckDB Connection, so its
// TEMPORARY catalog, its session settings and its transaction context persist across them. Handed to the
// managed side as an opaque handle owning a shared_ptr to this struct.
//
// ⚠ `open_streams` is what makes the pin SAFE rather than merely convenient. Every DuckDB query path calls
// ClientContext::InitialCleanup, which CLOSES the connection's active streaming result — and MEASURED, it
// does so SILENTLY (the abandoned stream then reports end-of-stream, so the first query's remaining rows are
// LOST with no error). A second query on a pinned connection with a live result stream is therefore REFUSED.
// A result stream also holds a shared_ptr to this struct, so closing the handle first is safe: the
// Connection dies with the last stream, not before it.
struct HostConnection {
	shared_ptr<Connection> conn;
	atomic<int32_t> open_streams {0};
};

// Runs `sql` on a fresh Connection over `db` and fills `out` with a self-owning ArrowArrayStream over the
// result (the Connection + result live until `out` is released). When `params` is non-null it is a 1-row
// Arrow stream whose columns bind positionally to the statement's parameters (via a prepared statement;
// ownership is consumed here). Each `inputs` entry is registered as a connection-scoped view (so the SQL can
// reference it by name) before the query. `out_context` (nullable) receives the fresh connection's
// ClientContext for out-of-band interruption (the v66 host_query cancellation). `session` (nullable) copies
// the caller's search path + TimeZone onto the fresh connection — passed by the table function, NOT by the C#
// host service. Throws on a query error.
// `pinned` (nullable) runs the statement on an EXISTING connection instead of a fresh one — see
// HostConnection. The stream then holds a shared reference to it (so the pin may be closed first) and
// releases its `open_streams` slot on release. Named `inputs` are REFUSED with a pinned connection —
// ⚠ on a reason that was MEASURED FALSE on 2026-09-03 (the view neither collided, `replace: true`, nor was
// connection-scoped). The refusal is now LIFTABLE but is NOT a one-line deletion: an input view is a TEMP
// view scoped to the connection, so on a pin it would outlive the RESULT STREAM that owns the input's
// storage. Move the ownership first. docs/host-query.md + docs/fluid-templating.md §17.10.
void MakeHostQueryStream(DatabaseInstance &db, const string &sql, ArrowArrayStream *params,
                         const vector<HostQueryInput> &inputs, ArrowArrayStream &out,
                         shared_ptr<ClientContext> *out_context = nullptr,
                         const HostQuerySession *session = nullptr,
                         shared_ptr<HostConnection> pinned = nullptr);

// Registers the `fabricator_host_query(VARCHAR)` table function.
void RegisterHostQuery(ExtensionLoader &loader);

} // namespace duckdb
