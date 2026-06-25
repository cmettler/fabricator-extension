//===----------------------------------------------------------------------===//
//                         mssql_net — secret type (impl)
//
// Registration + basic validation live here (DuckDB API); the provider-specific
// connection-string + auth formatting lives in the managed backend
// (build_connection_string), so the C++ side has no SqlClient knowledge.
//===----------------------------------------------------------------------===//

#include "mssql_net_secret.hpp"

#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "duckdb/catalog/catalog_transaction.hpp"
#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/main/secret/secret.hpp"
#include "duckdb/main/secret/secret_manager.hpp"

#include <cstring>

namespace duckdb {

// -----------------------------------------------------------------------------
// Provider-declared secret types (see docs/provider-extensibility.md §2). Each provider declares its secret
// type + fields in C# (IBackend.SecretType / SecretFields); we register them here generically from the
// list_secret_fields ABI, so the provider-agnostic core names no secret type or field. Field validation +
// connection-string assembly live in the managed backend (build_connection_string).
// -----------------------------------------------------------------------------
namespace {

struct SecretFieldDecl {
	string name;
	bool redact = false;
	LogicalType type = LogicalType::VARCHAR;
};
struct SecretTypeDecl {
	string provider; // the backend that owns this secret type (passed to build_connection_string)
	vector<SecretFieldDecl> fields;
};
// secret_type -> declaration. Populated once at extension load (RegisterProviderSecrets), read by the generic
// creation function + IsKnownSecret + BuildConnectionStringFromSecret. Registration is single-threaded at
// load; reads thereafter never mutate it.
case_insensitive_map_t<SecretTypeDecl> g_secret_types;

} // namespace

// Generic secret-creation function shared by every registered provider secret type (keyed by input.type):
// stores the declared fields' supplied values (redacting the marked ones). Validation is deferred to the
// managed build_connection_string (provider-specific), so a missing/invalid field surfaces at connect time.
static unique_ptr<BaseSecret> CreateProviderSecret(ClientContext &context, CreateSecretInput &input) {
	auto it = g_secret_types.find(input.type);
	if (it == g_secret_types.end()) {
		throw InvalidInputException("mssql_net: unknown secret type '%s'", input.type);
	}
	auto result = make_uniq<KeyValueSecret>(input.scope, input.type, input.provider, input.name);
	for (auto &field : it->second.fields) {
		result->TrySetValue(field.name, input); // no-op when the key was not supplied
		if (field.redact) {
			result->redact_keys.insert(field.name);
		}
	}
	return std::move(result);
}

void RegisterProviderSecrets(ExtensionLoader &loader) {
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		arrownet::ListSecretFields(stream);
		// Columns: provider, secret_type, name, type ("varchar"|"integer"|"boolean"), redact ("1"|"0").
		auto rows = ReadStringTable(stream, 5);
		size_t n = rows[0].size();
		for (size_t i = 0; i < n; i++) {
			const string &provider = rows[0][i];
			const string &secret_type = rows[1][i];
			const string &name = rows[2][i];
			const string &type = rows[3][i];
			const string &redact = rows[4][i];
			LogicalType lt = type == "integer" ? LogicalType::INTEGER
			               : type == "boolean" ? LogicalType::BOOLEAN
			                                    : LogicalType::VARCHAR;
			auto &decl = g_secret_types[secret_type];
			decl.provider = provider;
			SecretFieldDecl field;
			field.name = name;
			field.redact = (redact == "1");
			field.type = lt;
			decl.fields.push_back(std::move(field));
		}
		// Register one DuckDB secret type (+ its config creation function) per distinct declared secret_type.
		for (auto &entry : g_secret_types) {
			SecretType type;
			type.name = entry.first;
			type.deserializer = KeyValueSecret::Deserialize<KeyValueSecret>;
			type.default_provider = "config";
			loader.RegisterSecretType(type);

			CreateSecretFunction create_func;
			create_func.secret_type = entry.first;
			create_func.provider = "config";
			create_func.function = CreateProviderSecret;
			for (auto &field : entry.second.fields) {
				create_func.named_parameters[field.name] = field.type;
			}
			loader.RegisterFunction(std::move(create_func));
		}
	} catch (std::exception &) {
		// Best-effort: if the bridge can't boot at load (e.g. the managed dir is missing), no secret type is
		// registered (CREATE SECRET would then error "unknown secret type"); the extension still loads.
		// Mirrors RegisterProviderSettings.
	}
}

// -----------------------------------------------------------------------------
// Connection-string assembly (delegated to the managed backend)
// -----------------------------------------------------------------------------
static unique_ptr<SecretEntry> GetSecretEntry(ClientContext &context, const string &secret_name) {
	auto &secret_manager = SecretManager::Get(context);
	auto transaction = CatalogTransaction::GetSystemCatalogTransaction(context);
	return secret_manager.GetSecretByName(transaction, secret_name);
}

bool IsKnownSecret(ClientContext &context, const string &secret_name) {
	if (secret_name.empty()) {
		return false;
	}
	// ANY existing secret (our own mssql_net type OR a foreign one reused for auth, e.g. azure). A foreign
	// secret that turns out to be unusable surfaces a clear error when BuildConnectionStringFromSecret runs.
	auto entry = GetSecretEntry(context, secret_name);
	return entry && entry->secret != nullptr;
}

// Minimal JSON-string escaping for secret values. Handles the JSON-special chars;
// non-ASCII UTF-8 passes through unchanged (valid JSON).
static string EscapeJson(const string &s) {
	string out;
	out.reserve(s.size() + 8);
	for (char c : s) {
		switch (c) {
		case '"':
			out += "\\\"";
			break;
		case '\\':
			out += "\\\\";
			break;
		case '\n':
			out += "\\n";
			break;
		case '\r':
			out += "\\r";
			break;
		case '\t':
			out += "\\t";
			break;
		default:
			out += c;
			break;
		}
	}
	return out;
}

string BuildConnectionStringFromSecret(ClientContext &context, const string &secret_name,
                                       const string &base_connstr, const string &provider) {
	auto entry = GetSecretEntry(context, secret_name); // owns the secret; keep alive for the reads below
	if (!entry || !entry->secret) {
		throw BinderException("mssql_net: secret '%s' not found. Create it with: CREATE SECRET %s "
		                      "(TYPE mssql_net, host '...', database '...', user '...', password '...')",
		                      secret_name, secret_name);
	}
	auto &secret = *entry->secret;
	const string secret_type = secret.GetType();
	auto &kv = static_cast<const KeyValueSecret &>(secret);

	// Hand all of the secret's fields to the owning backend, which owns the connection-string format AND
	// validates them (build_connection_string), interpreting them per `secret_type` (our own mssql_net secret
	// => full connstr; a foreign secret, e.g. azure, => auth merged onto base_connstr). See
	// docs/provider-extensibility.md §2.
	string json = "{";
	bool first = true;
	for (auto &field : kv.secret_map) {
		if (field.second.IsNull()) {
			continue;
		}
		if (!first) {
			json += ",";
		}
		first = false;
		json += "\"" + EscapeJson(field.first) + "\":\"" + EscapeJson(field.second.ToString()) + "\"";
	}
	json += "}";

	// Our own secret type routes to its declared backend; a foreign secret routes to the ATTACHing provider
	// (the one consuming it), default backend when unspecified.
	auto our = g_secret_types.find(secret_type);
	const string backend_provider = our != g_secret_types.end() ? our->second.provider : provider;
	return arrownet::BuildConnectionString(backend_provider, secret_type, json, base_connstr);
}

} // namespace duckdb
