//===----------------------------------------------------------------------===//
//                         mssql_net — secret type (impl)
//===----------------------------------------------------------------------===//

#include "mssql_net_secret.hpp"

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
static constexpr const char *kAccessToken = "access_token";       // BYO Azure Entra JWT
static constexpr const char *kAzureTenantId = "azure_tenant_id";  // tenant hint
// `authentication` is our addition: selects the Microsoft.Data.SqlClient Entra
// method (the C++ extension infers it from azure_secret/access_token instead).
static constexpr const char *kAuthentication = "authentication";
// Accepted for cross-compat (stored; not all are wired to the SqlClient path).
static constexpr const char *kCatalog = "catalog";
static constexpr const char *kAzureSecret = "azure_secret";
static constexpr const char *kSchemaFilter = "schema_filter";
static constexpr const char *kTableFilter = "table_filter";
static constexpr const char *kAuthenticator = "authenticator";
static constexpr const char *kApplicationName = "application_name";

// Non-standard connection-string segment carrying an Azure access token. The
// managed backend strips it and sets SqlConnection.AccessToken (the token is not
// a valid SqlClient connection-string keyword). Mirrored in C# (SqlServerCatalog).
static constexpr const char *kAccessTokenKeyword = "ArrowNetAccessToken";

// -----------------------------------------------------------------------------
// Authentication mapping
// -----------------------------------------------------------------------------
// Classifies how an `authentication` value relates to user/password requirements.
enum class AuthClass { SqlAuth, EntraUserPass, EntraTokenless };

static string NormalizeAuth(const string &raw) {
	string k;
	for (char c : StringUtil::Lower(raw)) {
		if (c != ' ' && c != '_' && c != '-') {
			k += c;
		}
	}
	return k;
}

// Maps a friendly/explicit `authentication` value to the SqlClient `Authentication`
// keyword. Returns "" for plain SQL auth. Throws on an unknown value.
static string MapAuthentication(const string &raw, AuthClass &cls) {
	auto k = NormalizeAuth(raw);
	if (k.empty() || k == "sql" || k == "sqlpassword") {
		cls = AuthClass::SqlAuth;
		return "";
	}
	if (k == "serviceprincipal" || k == "spn" || k == "activedirectoryserviceprincipal") {
		cls = AuthClass::EntraUserPass;
		return "Active Directory Service Principal";
	}
	if (k == "password" || k == "entrapassword" || k == "activedirectorypassword") {
		cls = AuthClass::EntraUserPass;
		return "Active Directory Password";
	}
	cls = AuthClass::EntraTokenless;
	if (k == "managedidentity" || k == "msi" || k == "activedirectorymanagedidentity") {
		return "Active Directory Managed Identity";
	}
	if (k == "default" || k == "activedirectorydefault") {
		return "Active Directory Default";
	}
	if (k == "interactive" || k == "activedirectoryinteractive") {
		return "Active Directory Interactive";
	}
	if (k == "devicecode" || k == "devicecodeflow" || k == "activedirectorydevicecodeflow") {
		return "Active Directory Device Code Flow";
	}
	if (k == "workloadidentity" || k == "activedirectoryworkloadidentity") {
		return "Active Directory Workload Identity";
	}
	if (k == "integrated" || k == "activedirectoryintegrated") {
		return "Active Directory Integrated";
	}
	throw BinderException("mssql_net secret: unsupported authentication '%s'", raw);
}

// -----------------------------------------------------------------------------
// Creation + validation
// -----------------------------------------------------------------------------
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

	bool has_token = !get(kAccessToken).empty();
	bool requires_user_pass = false;
	if (!has_token) {
		AuthClass cls;
		MapAuthentication(get(kAuthentication), cls); // also validates the value
		requires_user_pass = cls != AuthClass::EntraTokenless;
	}
	if (requires_user_pass) {
		for (auto field : {kUser, kPassword}) {
			if (get(field).empty()) {
				return StringUtil::Format("Missing required field '%s'", field);
			}
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
// Connection-string assembly
// -----------------------------------------------------------------------------
// Quotes a connection-string value if it contains a delimiter/quote, per the
// Microsoft.Data.SqlClient rules (double quotes around ; = ', single quotes when
// the value itself contains a double quote).
static string QuoteConnValue(const string &v) {
	bool needs = v.empty() || v.find_first_of(";=\"'") != string::npos || v.front() == ' ' || v.back() == ' ';
	if (!needs) {
		return v;
	}
	if (v.find('"') != string::npos) {
		string out = "'";
		for (char c : v) {
			out += (c == '\'') ? "''" : string(1, c);
		}
		out += "'";
		return out;
	}
	return "\"" + v + "\"";
}

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
	auto field = [&](const char *key) -> string {
		auto value = kv.TryGetValue(key);
		return value.IsNull() ? string() : value.ToString();
	};

	auto host = field(kHost);
	auto port_val = kv.TryGetValue(kPort);
	int64_t port = port_val.IsNull() ? 1433 : port_val.GetValue<int64_t>();
	auto encrypt_val = kv.TryGetValue(kUseEncrypt);
	bool encrypt = encrypt_val.IsNull() ? true : encrypt_val.GetValue<bool>();

	string cs = "Server=" + host + "," + std::to_string(port) + ";Database=" + QuoteConnValue(field(kDatabase)) +
	            ";Encrypt=" + (encrypt ? "True" : "False") + ";TrustServerCertificate=True";

	auto access_token = field(kAccessToken);
	if (!access_token.empty()) {
		// Token auth: the backend strips this and sets SqlConnection.AccessToken.
		// Must be the LAST segment so the managed side can read it verbatim.
		return cs + ";" + kAccessTokenKeyword + "=" + access_token;
	}

	AuthClass cls;
	string auth_kw = MapAuthentication(field(kAuthentication), cls);
	auto user = field(kUser);
	auto password = field(kPassword);
	if (!auth_kw.empty()) {
		cs += ";Authentication=" + auth_kw;
		if (!user.empty()) {
			cs += ";User Id=" + QuoteConnValue(user); // service principal: client id; MI: client id
		}
		if (!password.empty()) {
			cs += ";Password=" + QuoteConnValue(password); // service principal: client secret
		}
	} else {
		cs += ";User Id=" + QuoteConnValue(user) + ";Password=" + QuoteConnValue(password);
	}
	return cs;
}

} // namespace duckdb
