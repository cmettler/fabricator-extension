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

//! Registers the `mssql_net` secret type + its `config` creation function.
void RegisterMssqlNetSecretType(ExtensionLoader &loader);

//! Looks up a stored `mssql_net` secret by name and assembles a SqlClient
//! connection string from its fields. Throws if the secret is missing or is not
//! an mssql_net secret.
string BuildConnectionStringFromSecret(ClientContext &context, const string &secret_name);

//! True if a secret with this name exists and is of type `mssql_net`.
bool IsMssqlNetSecret(ClientContext &context, const string &secret_name);

} // namespace duckdb
