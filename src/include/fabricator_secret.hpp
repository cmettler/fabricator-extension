// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — secret type
//
// Registers a DuckDB secret type `fabricator` (SQL auth: host/port/database/user/
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

//! Looks up a stored secret by name (our own type OR a foreign one reused for auth, e.g. azure) and assembles
//! a connection string from its fields — the owning backend validates + formats it, interpreting the fields
//! per the secret's type. `base_connstr` is the ATTACH target (Server=…;Database=… / mssql:// URI), used when
//! a foreign secret carries only auth (empty otherwise). `provider` selects the consuming backend for a
//! foreign secret (empty => default; our own secret type routes to its declared backend). Throws if the
//! secret is missing. See docs/provider-extensibility.md §2.
string BuildConnectionStringFromSecret(ClientContext &context, const string &secret_name,
                                       const string &base_connstr = "", const string &provider = "");

//! True if a secret with this name exists (any type — our own or a foreign one reused for auth).
bool IsKnownSecret(ClientContext &context, const string &secret_name);

//! Same as BuildConnectionStringFromSecret, but finds the secret by SCOPE MATCH against `path` instead of by
//! name, and returns `path` UNCHANGED when nothing matches (a missing credential is not an error here — the
//! caller can still work through the host filesystem).
//!
//! This exists because naming a secret is not always possible. `COPY … TO '<path>' (FORMAT delta)` has no
//! SECRET clause at all, yet it opens a real Delta catalog and needs the same credential an ATTACH would get
//! — without one it silently drops to the host filesystem, which on abfss cannot commit atomically. Scope
//! matching is the right resolution rule for that: a DuckDB secret's scope IS a path prefix, so a secret that
//! matches was declared for this location. `azure` secrets scope to `abfss://` by default, so the common
//! case resolves with no user action.
//!
//! Deliberately NO "any secret of this type" fallback (unlike the onelake:// FileSystem, which needs one
//! because that scheme matches no azure secret's default scope): guessing among several accounts is how a
//! write lands somewhere the user did not intend.
string BuildConnectionStringFromScopedSecret(ClientContext &context, const string &path,
                                             const string &secret_type, const string &provider);

} // namespace duckdb
