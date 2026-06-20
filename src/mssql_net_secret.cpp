//===----------------------------------------------------------------------===//
//                         mssql_net — secret type (impl)
//
// Registration + basic validation live here (DuckDB API); the provider-specific
// connection-string + auth formatting lives in the managed backend
// (build_connection_string), so the C++ side has no SqlClient knowledge.
//===----------------------------------------------------------------------===//

#include "mssql_net_secret.hpp"

#include "arrownet/clr_host.hpp"
#include "duckdb/catalog/catalog_transaction.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/main/secret/secret.hpp"
#include "duckdb/main/secret/secret_manager.hpp"

namespace duckdb {

// Field names — kept in lock-step with the C++ `mssql` extension's secret so a
// `CREATE SECRET (TYPE mssql_net, ...)` accepts the same parameters (cross-compat).
static constexpr const char *kHost = "host";
static constexpr const char *kPort = "port";
static constexpr const char *kDatabase = "database";
static constexpr const char *kUser = "user";
static constexpr const char *kPassword = "password";
static constexpr const char *kUseEncrypt = "use_encrypt";
static constexpr const char *kAccessToken = "access_token";      // BYO Azure Entra JWT
static constexpr const char *kAzureTenantId = "azure_tenant_id"; // tenant hint
// `authentication` selects the Microsoft.Data.SqlClient Entra method (mapped in the
// managed backend); the C++ extension infers it from azure_secret/access_token instead.
static constexpr const char *kAuthentication = "authentication";
// Accepted for cross-compat (stored; not all are wired to the SqlClient path).
static constexpr const char *kCatalog = "catalog";
static constexpr const char *kAzureSecret = "azure_secret";
static constexpr const char *kSchemaFilter = "schema_filter";
static constexpr const char *kTableFilter = "table_filter";
static constexpr const char *kAuthenticator = "authenticator";
static constexpr const char *kApplicationName = "application_name";

// -----------------------------------------------------------------------------
// Creation + validation
// -----------------------------------------------------------------------------
// Basic, provider-agnostic validation. Connection-string assembly and auth-value
// validation now live in the managed backend (build_connection_string), so a secret
// missing user/password (SQL auth) or carrying an unknown `authentication` value
// surfaces at connect time rather than here.
static string ValidateFields(const CreateSecretInput &input) {
	auto get = [&](const char *key) -> string {
		auto it = input.options.find(key);
		return it == input.options.end() ? string() : it->second.ToString();
	};

	for (auto field : {kHost, kDatabase}) {
		if (get(field).empty()) {
			return StringUtil::Format("Missing required field '%s'", field);
		}
	}

	auto port_it = input.options.find(kPort);
	if (port_it != input.options.end() && !port_it->second.IsNull()) {
		int64_t port_value;
		try {
			port_value = port_it->second.GetValue<int64_t>();
		} catch (...) {
			return StringUtil::Format("Port must be a valid integer. Got: %s", port_it->second.ToString());
		}
		if (port_value < 1 || port_value > 65535) {
			return StringUtil::Format("Port must be between 1 and 65535. Got: %lld", (long long)port_value);
		}
	}
	return "";
}

static unique_ptr<BaseSecret> CreateMssqlNetSecret(ClientContext &context, CreateSecretInput &input) {
	auto error = ValidateFields(input);
	if (!error.empty()) {
		throw InvalidInputException("mssql_net secret: %s", error);
	}
	auto result = make_uniq<KeyValueSecret>(input.scope, input.type, input.provider, input.name);
	// TrySetValue is a no-op when the key was not supplied.
	for (auto field : {kHost, kPort, kDatabase, kUser, kPassword, kUseEncrypt, kAccessToken, kAzureTenantId,
	                   kAuthentication, kCatalog, kAzureSecret, kSchemaFilter, kTableFilter, kAuthenticator,
	                   kApplicationName}) {
		result->TrySetValue(field, input);
	}
	if (input.options.find(kUseEncrypt) == input.options.end()) {
		result->secret_map[kUseEncrypt] = Value::BOOLEAN(true); // TLS on by default
	}
	result->redact_keys.insert(kPassword);
	result->redact_keys.insert(kAccessToken);
	return std::move(result);
}

void RegisterMssqlNetSecretType(ExtensionLoader &loader) {
	SecretType type;
	type.name = "mssql_net";
	type.deserializer = KeyValueSecret::Deserialize<KeyValueSecret>;
	type.default_provider = "config";
	loader.RegisterSecretType(type);

	CreateSecretFunction create_func;
	create_func.secret_type = "mssql_net";
	create_func.provider = "config";
	create_func.function = CreateMssqlNetSecret;
	create_func.named_parameters[kHost] = LogicalType::VARCHAR;
	create_func.named_parameters[kPort] = LogicalType::INTEGER;
	create_func.named_parameters[kDatabase] = LogicalType::VARCHAR;
	create_func.named_parameters[kUser] = LogicalType::VARCHAR;
	create_func.named_parameters[kPassword] = LogicalType::VARCHAR;
	create_func.named_parameters[kUseEncrypt] = LogicalType::BOOLEAN;
	create_func.named_parameters[kAuthentication] = LogicalType::VARCHAR;
	create_func.named_parameters[kAccessToken] = LogicalType::VARCHAR;
	create_func.named_parameters[kAzureTenantId] = LogicalType::VARCHAR;
	create_func.named_parameters[kCatalog] = LogicalType::BOOLEAN;
	create_func.named_parameters[kAzureSecret] = LogicalType::VARCHAR;
	create_func.named_parameters[kSchemaFilter] = LogicalType::VARCHAR;
	create_func.named_parameters[kTableFilter] = LogicalType::VARCHAR;
	create_func.named_parameters[kAuthenticator] = LogicalType::VARCHAR;
	create_func.named_parameters[kApplicationName] = LogicalType::VARCHAR;
	loader.RegisterFunction(std::move(create_func));
}

// -----------------------------------------------------------------------------
// Connection-string assembly (delegated to the managed backend)
// -----------------------------------------------------------------------------
static unique_ptr<SecretEntry> GetSecretEntry(ClientContext &context, const string &secret_name) {
	auto &secret_manager = SecretManager::Get(context);
	auto transaction = CatalogTransaction::GetSystemCatalogTransaction(context);
	return secret_manager.GetSecretByName(transaction, secret_name);
}

bool IsMssqlNetSecret(ClientContext &context, const string &secret_name) {
	if (secret_name.empty()) {
		return false;
	}
	auto entry = GetSecretEntry(context, secret_name);
	return entry && entry->secret && entry->secret->GetType() == "mssql_net";
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

string BuildConnectionStringFromSecret(ClientContext &context, const string &secret_name) {
	auto entry = GetSecretEntry(context, secret_name); // owns the secret; keep alive for the reads below
	if (!entry) {
		throw BinderException("mssql_net: secret '%s' not found. Create it with: CREATE SECRET %s "
		                      "(TYPE mssql_net, host '...', database '...', user '...', password '...')",
		                      secret_name, secret_name);
	}
	auto &secret = *entry->secret;
	if (secret.GetType() != "mssql_net") {
		throw BinderException("mssql_net: secret '%s' is not TYPE mssql_net (got '%s')", secret_name,
		                      secret.GetType());
	}
	auto &kv = static_cast<const KeyValueSecret &>(secret);

	// Hand all of the secret's fields to the managed backend, which owns the provider
	// connection-string format (Server=/Database=/Encrypt=/auth/access-token, escaping).
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

	return arrownet::BuildConnectionString("", json);
}

} // namespace duckdb
