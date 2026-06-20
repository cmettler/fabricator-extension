//===----------------------------------------------------------------------===//
//                         mssql_net — storage extension (impl)
//===----------------------------------------------------------------------===//

#include "mssql_net_storage.hpp"

#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "catalog/arrownet_transaction.hpp"
#include "mssql_net_secret.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/main/attached_database.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/parser/parsed_data/attach_info.hpp"
#include "duckdb/storage/storage_extension.hpp"

#include <cstring>

namespace duckdb {

// Validates the TCP port embedded in a Server=/Data Source= value ("host,port"),
// so an out-of-range port fails fast at ATTACH (parity with the C++ mssql ext).
static void ValidateConnectionPort(const string &conn) {
	string lower = StringUtil::Lower(conn);
	for (const char *key : {"server=", "data source=", "addr=", "address=", "network address="}) {
		size_t pos = lower.find(key);
		if (pos == string::npos) {
			continue;
		}
		size_t value_start = pos + strlen(key);
		size_t value_end = conn.find(';', value_start);
		if (value_end == string::npos) {
			value_end = conn.size();
		}
		string value = conn.substr(value_start, value_end - value_start);
		size_t comma = value.rfind(','); // host,port — port follows the last comma
		if (comma == string::npos) {
			continue;
		}
		string port_str = value.substr(comma + 1);
		StringUtil::Trim(port_str);
		if (port_str.empty()) {
			continue;
		}
		for (char c : port_str) {
			if (!StringUtil::CharacterIsDigit(c)) {
				return; // not a plain numeric port (e.g. instance name) — leave it to the driver
			}
		}
		// > 5 digits cannot be a valid 1..65535 port; avoids stoll overflow on huge inputs.
		if (port_str.size() > 5 || std::stoll(port_str) < 1 || std::stoll(port_str) > 65535) {
			throw InvalidInputException("mssql_net: Port must be between 1 and 65535 (got '%s')", port_str);
		}
	}
}

// Masks the Password=/Pwd= value so it is not exposed via GetDBPath().
static string RedactConnectionString(const string &conn) {
	string result = conn;
	string lower = StringUtil::Lower(conn);
	for (const char *key : {"password=", "pwd="}) {
		size_t pos = lower.find(key);
		while (pos != string::npos) {
			size_t value_start = pos + strlen(key);
			size_t value_end = result.find(';', value_start);
			if (value_end == string::npos) {
				value_end = result.size();
			}
			result.replace(value_start, value_end - value_start, "***");
			lower = StringUtil::Lower(result);
			pos = lower.find(key, value_start);
		}
	}
	return result;
}

static unique_ptr<Catalog> MssqlNetAttach(optional_ptr<StorageExtensionInfo> storage_info, ClientContext &context,
                                          AttachedDatabase &db, const string &name, AttachInfo &info,
                                          AttachOptions &options) {
	// A SECRET option supplies the connection from a stored mssql_net secret;
	// otherwise the first ATTACH argument is the connection string. schema_filter /
	// table_filter restrict catalog discovery. Recognized options are erased so
	// DuckDB's StorageOptions doesn't reject them as unrecognized.
	string secret_name;
	string schema_filter;
	string table_filter;
	for (auto it = options.options.begin(); it != options.options.end();) {
		auto lower = StringUtil::Lower(it->first);
		if (lower == "secret") {
			secret_name = it->second.ToString();
		} else if (lower == "schema_filter") {
			schema_filter = it->second.ToString();
			it = options.options.erase(it);
			continue;
		} else if (lower == "table_filter") {
			table_filter = it->second.ToString();
			it = options.options.erase(it);
			continue;
		}
		++it;
	}

	string connection_string;
	if (!secret_name.empty()) {
		connection_string = BuildConnectionStringFromSecret(context, secret_name);
	} else {
		connection_string = info.path;
	}
	if (connection_string.empty()) {
		throw BinderException("mssql_net: ATTACH requires a connection string or a SECRET, e.g. "
		                      "ATTACH 'Server=...;Database=...;User Id=...;Password=...' AS db (TYPE mssql_net) "
		                      "or ATTACH '' AS db (TYPE mssql_net, SECRET my_secret)");
	}

	// Validate the filter regexes up front so a bad pattern reports a clean "Invalid
	// regex" error rather than being wrapped as a connection failure below.
	ArrowNetCatalog::ValidateCatalogFilters(schema_filter, table_filter);

	ValidateConnectionPort(connection_string);

	// ATTACH must validate the connection up front and create NO catalog on failure;
	// wrap the underlying driver/network error so the cause is clear (and so a later
	// catalog query is never the first place a bad connection surfaces).
	try {
		auto handle = arrownet::OpenCatalog(connection_string);
		auto catalog = make_uniq<ArrowNetCatalog>(db, name, handle, RedactConnectionString(connection_string));
		catalog->SetCatalogFilters(schema_filter, table_filter);
		catalog->LoadCatalog(context); // discover schemas + tables (also validates the connection)
		return std::move(catalog);
	} catch (const std::exception &ex) {
		throw IOException("MSSQL connection validation failed: %s", ex.what());
	}
}

static unique_ptr<TransactionManager>
MssqlNetCreateTransactionManager(optional_ptr<StorageExtensionInfo> storage_info, AttachedDatabase &db,
                                 Catalog &catalog) {
	auto handle = catalog.Cast<ArrowNetCatalog>().GetHandle();
	return make_uniq<ArrowNetTransactionManager>(db, handle);
}

void RegisterMssqlNetStorageExtension(ExtensionLoader &loader) {
	auto &config = DBConfig::GetConfig(loader.GetDatabaseInstance());
	auto storage_extension = make_shared_ptr<StorageExtension>();
	storage_extension->attach = MssqlNetAttach;
	storage_extension->create_transaction_manager = MssqlNetCreateTransactionManager;
	StorageExtension::Register(config, "mssql_net", std::move(storage_extension));
}

} // namespace duckdb
