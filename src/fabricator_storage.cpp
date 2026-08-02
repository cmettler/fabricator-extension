//===----------------------------------------------------------------------===//
//                         fabricator — storage extension (impl)
//===----------------------------------------------------------------------===//

#include "fabricator_storage.hpp"

#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_transaction.hpp"
#include "fabricator_secret.hpp"
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
			throw InvalidInputException("fabricator: Port must be between 1 and 65535 (got '%s')", port_str);
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

// Minimal JSON-string escaping for ATTACH option keys/values (mirrors fabricator_secret.cpp's EscapeJson).
static string EscapeJsonOption(const string &s) {
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

static unique_ptr<Catalog> FabricatorAttach(optional_ptr<StorageExtensionInfo> storage_info, ClientContext &context,
                                          AttachedDatabase &db, const string &name, AttachInfo &info,
                                          AttachOptions &options) {
	// Only the two META options the core must handle itself are parsed here: SECRET (resolved to a connstr
	// before the provider opens) and PROVIDER (selects which backend handles this ATTACH). EVERY other option
	// is provider-owned — collected into a flat JSON object and forwarded to the managed side via open_catalog
	// (e.g. SQL Server reads schema_filter / table_filter / isolation_level). The provider-agnostic core thus
	// names no provider-specific option. Forwarded options are erased so DuckDB's StorageOptions doesn't
	// reject them as unrecognized. See docs/provider-extensibility.md §3.
	string secret_name;
	string provider; // which registered backend handles this catalog (empty => default)
	string options_body;
	for (auto it = options.options.begin(); it != options.options.end();) {
		auto lower = StringUtil::Lower(it->first);
		if (lower == "secret") {
			secret_name = it->second.ToString();
			++it; // left in the options map (handled by DuckDB's ATTACH machinery), as before
			continue;
		}
		if (lower == "provider") {
			provider = StringUtil::Lower(it->second.ToString());
			it = options.options.erase(it);
			continue;
		}
		// Provider-owned option: forward to C# as a JSON string field (lowercased key for a stable contract).
		if (!options_body.empty()) {
			options_body += ",";
		}
		options_body += "\"" + EscapeJsonOption(lower) + "\":\"" + EscapeJsonOption(it->second.ToString()) + "\"";
		it = options.options.erase(it);
	}
	// SYNTHETIC option, not one the user wrote: DuckDB's READ_ONLY / READ_WRITE is an ATTACH KEYWORD, so it
	// never appears in options.options and a provider could not see it. Forwarded because "am I allowed to
	// write here?" is a legitimate provider question — the Delta catalog uses it to warn, at attach time,
	// about an s3:// root opened READ_WRITE with no named SECRET, which silently loses concurrent commits
	// (docs/delta-transactions.md §8.3). Emitted as the enum's lowercase name so the contract is stable;
	// AUTOMATIC is passed through as itself rather than resolved, since what it RESOLVES to is DuckDB's
	// business and a provider must not second-guess it.
	if (!options_body.empty()) {
		options_body += ",";
	}
	switch (options.access_mode) {
	case AccessMode::READ_ONLY:
		options_body += "\"access_mode\":\"read_only\"";
		break;
	case AccessMode::READ_WRITE:
		options_body += "\"access_mode\":\"read_write\"";
		break;
	case AccessMode::AUTOMATIC:
		options_body += "\"access_mode\":\"automatic\"";
		break;
	default:
		options_body += "\"access_mode\":\"undefined\"";
		break;
	}
	string options_json = "{" + options_body + "}";

	string connection_string;
	if (!secret_name.empty()) {
		// info.path is the ATTACH target (Server=…;Database=… / mssql:// URI). For our own mssql secret it
		// is ignored (the secret is a full connstr); for a foreign secret (e.g. azure) reused for auth, the
		// server/database come from it. provider is the resolved PROVIDER option (which backend interprets it).
		connection_string = BuildConnectionStringFromSecret(context, secret_name, info.path, provider);
	} else {
		// NOTE: an abfss:// ATTACH with no SECRET named is deliberately NOT auto-resolved here, even though
		// the COPY path does exactly that (fabricator_copy.cpp). The difference is that COPY knows its
		// provider is 'delta'; here `provider` may be EMPTY (no PROVIDER option, inferred later from the
		// scheme), and an empty provider resolves to the DEFAULT backend, whose azure branch merges the
		// fields into a SQL Server connection string — it would turn the abfss path into a mangled connstr
		// and break an attach that currently works. Auto-resolution here needs provider dispatch settled
		// first; until then the remedy is the explicit SECRET, and the attach warns when it is missing.
		connection_string = info.path;
	}
	if (connection_string.empty()) {
		throw BinderException("fabricator: ATTACH requires a connection string or a SECRET, e.g. "
		                      "ATTACH 'Server=...;Database=...;User Id=...;Password=...' AS db (TYPE fabricator) "
		                      "or ATTACH '' AS db (TYPE fabricator, SECRET my_secret)");
	}

	ValidateConnectionPort(connection_string);

	// No explicit PROVIDER option? Infer it from a "scheme://" connection string
	// (e.g. mssql://… -> "mssql"); otherwise the default backend handles it.
	if (provider.empty()) {
		size_t scheme_end = connection_string.find("://");
		if (scheme_end != string::npos && scheme_end > 0) {
			bool all_alpha = true;
			for (size_t i = 0; i < scheme_end; i++) {
				char c = connection_string[i];
				if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) {
					all_alpha = false;
					break;
				}
			}
			if (all_alpha) {
				provider = StringUtil::Lower(connection_string.substr(0, scheme_end));
			}
		}
	}

	// ATTACH must validate the connection up front and create NO catalog on failure;
	// wrap the underlying driver/network error so the cause is clear (and so a later
	// catalog query is never the first place a bad connection surfaces).
	try {
		// The provider-owned options (schema_filter / table_filter / isolation_level / …) ride options_json
		// into the managed side; a bad filter regex now surfaces here as the managed open error.
		auto handle = fabricator::OpenCatalog(connection_string, provider, options_json);
		auto catalog = make_uniq<FabricatorCatalog>(db, name, handle, RedactConnectionString(connection_string));
		// An object filter (schema_filter/table_filter/function_filter) makes the discovered name list a
		// FILTERED SUBSET — so a targeted lookup miss may be a real table the filter merely excluded from
		// enumeration. Flag it so GetOrCreateEntry lazily fetches such a table by name (the filter bounds
		// enumeration, not targeted access). The keys ride options_json lowercased+quoted.
		catalog->SetObjectFilter(options_json.find("\"schema_filter\"") != string::npos ||
		                         options_json.find("\"table_filter\"") != string::npos ||
		                         options_json.find("\"function_filter\"") != string::npos);
		catalog->LoadCatalog(context); // discover schemas + tables (also validates the connection)
		return std::move(catalog);
	} catch (const std::exception &ex) {
		throw IOException("MSSQL connection validation failed: %s", ex.what());
	}
}

static unique_ptr<TransactionManager>
FabricatorCreateTransactionManager(optional_ptr<StorageExtensionInfo> storage_info, AttachedDatabase &db,
                                 Catalog &catalog) {
	auto handle = catalog.Cast<FabricatorCatalog>().GetHandle();
	return make_uniq<FabricatorTransactionManager>(db, handle);
}

void RegisterFabricatorStorageExtension(ExtensionLoader &loader) {
	auto &config = DBConfig::GetConfig(loader.GetDatabaseInstance());
	auto storage_extension = make_shared_ptr<StorageExtension>();
	storage_extension->attach = FabricatorAttach;
	storage_extension->create_transaction_manager = FabricatorCreateTransactionManager;
	// ATTACH '…' (TYPE fabricator) — the single provider-agnostic storage extension keyword.
	StorageExtension::Register(config, "fabricator", std::move(storage_extension));
}

} // namespace duckdb
