//===----------------------------------------------------------------------===//
//                         fabricator — host HTTP (reuse DuckDB's HTTP stack from managed code)
//===----------------------------------------------------------------------===//
//
// Performs an HTTP request through DuckDB's OWN HTTP layer (`HTTPUtil`, which httpfs replaces with its
// curl/httplib client when loaded) on behalf of the managed side, so a C# component — above all a PLUGIN
// calling a REST API — inherits DuckDB's secrets, TLS trust, proxy and retry configuration instead of
// carrying its own. Registered as the `http_request` host service (the reverse direction of the vtable);
// the managed `DuckDbHttpHandler` wraps it as an ordinary .NET `HttpMessageHandler`.
//
// See docs/http-transport.md.
//
#pragma once

#include "duckdb.hpp"
#include "duckdb/main/extension/extension_loader.hpp"

namespace duckdb {

// Installs the `http_request` host service onto the shared host-services block. Must run at extension load,
// BEFORE the bridge boots (like SetHostQueryService / SetHostLog) — order-independent with the others.
void RegisterHostHttp(ExtensionLoader &loader);

} // namespace duckdb
