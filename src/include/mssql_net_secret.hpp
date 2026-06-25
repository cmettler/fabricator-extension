//===----------------------------------------------------------------------===//
//                         mssql_net — secret type
//
// Registers a DuckDB secret type `mssql_net` (SQL auth: host/port/database/user/
// password/use_encrypt) and builds a Microsoft.Data.SqlClient connection string
// from its stored parts. ATTACH and the query/exec functions can then reference a
// secret by name instead of repeating a connection string.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/main/client_context.hpp"
#include "duckdb/main/extension/extension_loader.hpp"

namespace duckdb {

//! Registers every provider's secret type(s) + their `config` creation functions, declared in C#
//! (IBackend.SecretType / SecretFields) and queried via the list_secret_fields ABI at load — so the
//! provider-agnostic core names no secret type or field. See docs/provider-extensibility.md §2.
void RegisterProviderSecrets(ExtensionLoader &loader);

//! Looks up a stored provider secret by name and assembles a connection string from its fields (the owning
//! backend validates + formats it). Throws if the secret is missing or is not a provider secret type.
string BuildConnectionStringFromSecret(ClientContext &context, const string &secret_name);

//! True if a secret with this name exists and is one of the registered provider secret types.
bool IsProviderSecret(ClientContext &context, const string &secret_name);

} // namespace duckdb
