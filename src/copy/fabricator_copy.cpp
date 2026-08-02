//===----------------------------------------------------------------------===//
//                         fabricator — COPY TO (impl)
//===----------------------------------------------------------------------===//

#include "copy/fabricator_copy.hpp"

#include "fabricator/arrow_produce.hpp"
#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_schema_entry.hpp"
#include "fabricator_secret.hpp"
#include "duckdb/catalog/catalog.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/value.hpp"
#include "duckdb/function/copy_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/transaction/meta_transaction.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"

#include <algorithm>
#include <cstring>

namespace duckdb {

struct FabricatorCopyBindData : public FunctionData {
	FabricatorHandle handle = nullptr;
	string catalog_name;
	string schema_name;
	string table_name;
	vector<string> column_names;
	vector<LogicalType> column_types;
	bool create_table = true;
	bool replace = false;
	string schema_mode; // SCHEMA_MODE COPY option: "" | "merge" | "overwrite" (Delta provider)
	// PARTITION_OVERWRITE COPY option: dynamic partition overwrite — the partitions present in the input are
	// atomically replaced (one Delta commit), untouched partitions kept. Requires CREATE_TABLE false (append
	// shape) + a partitioned target; validated provider-side. Delta-only (other providers reject it when set).
	bool partition_overwrite = false;
	// FORMAT delta (path-targeted): the COPY opens its OWN transient Delta catalog rooted at the target's
	// parent directory (no ATTACH needed) — `catalog_name` holds the ROOT PATH, `handle` stays null until
	// init (the transient handle is per-execution, owned by the global state).
	bool transient_delta = false;
	string options_json;       // provider ATTACH-style options forwarded to the transient open_catalog
	string partition_columns;  // PARTITION_COLUMNS COPY option (create-time Hive partitioning; Delta)
	string sort_columns;       // SORTED_COLUMNS COPY option (ordered/clustered write; declarative — Delta
	                           // re-keys an existing table whose persisted spec differs)

	unique_ptr<FunctionData> Copy() const override {
		auto result = make_uniq<FabricatorCopyBindData>();
		*result = *this;
		return std::move(result);
	}
	bool Equals(const FunctionData &other_p) const override {
		auto &other = other_p.Cast<FabricatorCopyBindData>();
		return handle == other.handle && schema_name == other.schema_name && table_name == other.table_name &&
		       catalog_name == other.catalog_name && transient_delta == other.transient_delta;
	}
};

struct FabricatorCopyGlobalState : public GlobalFunctionData {
	FabricatorCopyGlobalState(ClientContext &context, const FabricatorCopyBindData &bind_data)
	    : properties(fabricator::BoundaryClientProperties(context)),
	      extension_types(ArrowTypeExtensionData::GetExtensionTypes(context, bind_data.column_types)) {
		// The producer also builds the Arrow schema handed to begin_bulk.
		producer = make_uniq<fabricator::ArrowProducer>(bind_data.column_types, bind_data.column_names, properties);
	}
	~FabricatorCopyGlobalState() override {
		// Cancel the background load if the query failed before Finalize (best-effort).
		if (bulk_session && !bulk_completed) {
			try {
				fabricator::CompleteBulk(bulk_session, /*abort=*/true);
			} catch (...) {
			}
		}
		// FORMAT delta: on a failure path, discard any parked append buffer and release the transient catalog
		// (a rollback is discard-only — no opener needed). Best-effort; never throw out of a destructor.
		if (owned_catalog) {
			try {
				fabricator::SetActiveTxn(owned_catalog, txn_id);
				fabricator::RollbackTransaction(owned_catalog);
			} catch (...) {
			}
			try {
				fabricator::CloseCatalog(owned_catalog);
			} catch (...) {
			}
			owned_catalog = nullptr;
		}
	}
	ClientProperties properties;
	unordered_map<idx_t, const shared_ptr<ArrowTypeExtensionData>> extension_types;
	unique_ptr<fabricator::ArrowProducer> producer;
	//! Streaming bulk-load session.
	FabricatorHandle bulk_session = nullptr;
	bool bulk_completed = false;
	//! FORMAT delta: the transient per-execution catalog handle (owned; closed at finalize/teardown) + the
	//! DuckDB transaction id the bulk was keyed with (the provider parks appends per txn — the finalize
	//! commit_transaction must present the SAME id to flush them).
	FabricatorHandle owned_catalog = nullptr;
	int64_t txn_id = 0;
	mutex lock;
};

struct FabricatorCopyLocalState : public LocalFunctionData {};

static bool GetBoolOption(const case_insensitive_map_t<vector<Value>> &options, const string &key, bool fallback) {
	auto it = options.find(key);
	if (it == options.end() || it->second.empty()) {
		return fallback;
	}
	auto &value = it->second[0];
	if (value.type().id() == LogicalTypeId::BOOLEAN) {
		return value.GetValue<bool>();
	}
	auto text = StringUtil::Lower(value.ToString());
	return text == "true" || text == "1" || text == "yes";
}

// A string COPY option (e.g. SCHEMA_MODE 'merge'|'overwrite'); empty when absent.
static string GetStringOption(const case_insensitive_map_t<vector<Value>> &options, const string &key) {
	auto it = options.find(key);
	if (it == options.end() || it->second.empty() || it->second[0].IsNull()) {
		return string();
	}
	return it->second[0].ToString();
}

// Parses 'mssql://catalog/schema/table' or 'catalog.schema.table'.
static void ParseTarget(const string &path, string &catalog, string &schema, string &table) {
	string body = path;
	if (StringUtil::StartsWith(StringUtil::Lower(body), "mssql://")) {
		body = body.substr(8);
		auto parts = StringUtil::Split(body, "/");
		if (parts.size() == 3) {
			catalog = parts[0];
			schema = parts[1];
			table = parts[2];
			return;
		}
	} else {
		auto parts = StringUtil::Split(body, ".");
		if (parts.size() == 3) {
			catalog = parts[0];
			schema = parts[1];
			table = parts[2];
			return;
		}
	}
	throw BinderException("mssql COPY: target must be 'mssql://catalog/schema/table' or "
	                      "'catalog.schema.table' (got '%s')",
	                      path);
}

// The COPY options shared by both formats (CREATE_TABLE / REPLACE / SCHEMA_MODE / PARTITION_OVERWRITE).
static void BindCommonCopyOptions(const case_insensitive_map_t<vector<Value>> &options,
                                  FabricatorCopyBindData &bind_data, const char *fmt) {
	bind_data.create_table = GetBoolOption(options, "CREATE_TABLE", true);
	bind_data.replace = GetBoolOption(options, "REPLACE", false);
	bind_data.schema_mode = GetStringOption(options, "SCHEMA_MODE");
	bind_data.partition_overwrite = GetBoolOption(options, "PARTITION_OVERWRITE", false);
	if (bind_data.partition_overwrite && (bind_data.create_table || bind_data.replace)) {
		throw BinderException("%s COPY: PARTITION_OVERWRITE requires CREATE_TABLE false (it appends into an "
		                      "existing partitioned table, atomically replacing the partitions present in the input)",
		                      fmt);
	}
}

static unique_ptr<FunctionData> CopyToBind(ClientContext &context, CopyFunctionBindInput &input,
                                           const vector<string> &names, const vector<LogicalType> &sql_types) {
	auto bind_data = make_uniq<FabricatorCopyBindData>();
	ParseTarget(input.info.file_path, bind_data->catalog_name, bind_data->schema_name, bind_data->table_name);

	auto &catalog = Catalog::GetCatalog(context, bind_data->catalog_name);
	if (!FabricatorCatalog::Is(catalog)) {
		throw BinderException("mssql COPY: catalog '%s' is not a fabricator catalog", bind_data->catalog_name);
	}
	bind_data->handle = catalog.Cast<FabricatorCatalog>().GetHandle();
	bind_data->column_names = names;
	bind_data->column_types = sql_types;
	BindCommonCopyOptions(input.info.options, *bind_data, "mssql");
	return std::move(bind_data);
}

// --- COPY … TO '<path>/<table>' (FORMAT delta, …) — path-targeted Delta write, NO ATTACH needed ---
//
// The target is a raw filesystem path (local / s3:// / abfss:// …); the COPY opens a TRANSIENT
// engineered-wood Delta catalog rooted at the parent directory (flat layout — the last path segment is the
// table folder) and streams through the exact same bulk machinery as the catalog COPY, so CREATE_TABLE /
// REPLACE / SCHEMA_MODE / PARTITION_OVERWRITE all work identically. Provider ATTACH-style options pass
// through (DELETION_VECTORS / COLUMN_MAPPING / ROW_TRACKING / … below); NATIVE_WRITE defaults TRUE here
// (bounded-memory streaming via DuckDB's own parquet writer — there is no attached catalog whose default
// could disagree). PARTITION_COLUMNS gives create-time Hive partitioning (deliberately NOT the generic
// PARTITION_BY option, which DuckDB's planner intercepts for file-based copies).
//
// Transactionality: the COPY is its OWN atomic Delta commit at finalize — like COPY TO a parquet file, it
// does NOT roll back with a surrounding DuckDB BEGIN/ROLLBACK (the transient catalog has no transaction
// manager). A failure discards the parked buffer (nothing committed).
static unique_ptr<FunctionData> DeltaCopyToBind(ClientContext &context, CopyFunctionBindInput &input,
                                                const vector<string> &names, const vector<LogicalType> &sql_types) {
	auto bind_data = make_uniq<FabricatorCopyBindData>();
	bind_data->transient_delta = true;

	// Split '<root>/<table>' — the table is the last path segment, the root becomes the transient catalog.
	string path = input.info.file_path;
	std::replace(path.begin(), path.end(), '\\', '/');
	while (!path.empty() && path.back() == '/') {
		path.pop_back();
	}
	auto scheme = path.find("://");
	auto first_seg = scheme == string::npos ? 0 : path.find('/', scheme + 3);
	auto slash = path.find_last_of('/');
	if (slash == string::npos || first_seg == string::npos || slash <= first_seg || slash + 1 >= path.size()) {
		throw BinderException("delta COPY: target must be a path '<root>/<table>' (local or remote, e.g. "
		                      "'s3://bucket/lake/mytable') — got '%s'",
		                      input.info.file_path);
	}
	bind_data->catalog_name = path.substr(0, slash); // the transient catalog ROOT
	bind_data->schema_name = "main";                 // flat layout
	bind_data->table_name = path.substr(slash + 1);

	bind_data->column_names = names;
	bind_data->column_types = sql_types;

	// MODE — the Spark/delta-rs write-disposition vocabulary:
	//   'overwrite' (default)      create or fully replace
	//   'append'                   create if missing, append if exists (Spark mode=append)
	//   'error' | 'errorifexists'  create only — fail if the target exists (Spark's default mode)
	//   'ignore'                   create if missing, silent no-op if exists
	//   'error_if_not_exists'      strict append — fail if the target does NOT exist (the inverse of
	//                              'error'; no Spark equivalent)
	//   'overwrite_partitions'     dynamic partition overwrite (Spark: overwrite + partitionOverwriteMode=
	//                              dynamic); with PARTITION_COLUMNS a missing target is created partitioned
	//                              (idempotent first run), without them a missing target is rejected up front
	// PARTITION_COLUMNS is allowed with EVERY mode and applies whenever the write actually creates the
	// table (explicit or implicit) — an existing table keeps its declared partitioning.
	// The legacy CREATE_TABLE/REPLACE/PARTITION_OVERWRITE flags (shared with FORMAT mssql) still work,
	// but mixing them with MODE is rejected — one vocabulary per statement.
	auto mode = StringUtil::Lower(GetStringOption(input.info.options, "MODE"));
	string disposition; // 'error'/'ignore' → checked provider-side (rides the transient catalog's options)
	if (!mode.empty()) {
		for (auto &legacy : {"CREATE_TABLE", "REPLACE", "PARTITION_OVERWRITE"}) {
			if (input.info.options.find(legacy) != input.info.options.end()) {
				throw BinderException("delta COPY: MODE cannot be combined with the legacy %s flag — use MODE "
				                      "'overwrite'|'append'|'error'|'ignore'|'overwrite_partitions' alone",
				                      legacy);
			}
		}
		if (mode == "overwrite") {
			bind_data->create_table = true;
			bind_data->replace = true;
		} else if (mode == "append") {
			bind_data->create_table = false;
		} else if (mode == "error" || mode == "errorifexists") {
			bind_data->create_table = true;
			disposition = "error";
		} else if (mode == "ignore") {
			bind_data->create_table = true;
			disposition = "ignore";
		} else if (mode == "error_if_not_exists") {
			bind_data->create_table = false;
			disposition = "error_if_not_exists";
		} else if (mode == "overwrite_partitions") {
			bind_data->create_table = false;
			bind_data->partition_overwrite = true;
		} else {
			throw BinderException("delta COPY: unknown MODE '%s' — expected 'overwrite'|'append'|'error'|"
			                      "'ignore'|'error_if_not_exists'|'overwrite_partitions'",
			                      mode);
		}
		bind_data->schema_mode = GetStringOption(input.info.options, "SCHEMA_MODE");
	} else {
		BindCommonCopyOptions(input.info.options, *bind_data, "delta");
	}
	bind_data->partition_columns = GetStringOption(input.info.options, "PARTITION_COLUMNS");
	// SORTED_COLUMNS: the SORTED BY analog for the COPY surface (deliberately NOT ORDER_BY, which the
	// planner intercepts). Orders THIS write's stream; on Delta it is DECLARATIVE — persisted at create
	// (fabricator.sortedBy + the delta.clustering domain, unpartitioned) and an EXISTING table whose
	// persisted spec differs is RE-KEYED first, so repeated runs converge (dbt-style). Removal is DDL:
	// ALTER TABLE ... RESET SORTED BY (an empty option value cannot cross the column-list ABI).
	bind_data->sort_columns = GetStringOption(input.info.options, "SORTED_COLUMNS");

	// Provider options → the transient open_catalog's ATTACH-options JSON. NATIVE_WRITE defaults true.
	string json = "{\"native_write\":\"" +
	              string(GetBoolOption(input.info.options, "NATIVE_WRITE", true) ? "true" : "false") + "\"";
	if (!disposition.empty()) {
		// The transient catalog serves exactly this one COPY, so the per-statement disposition can ride
		// the catalog options; DeltaCatalog checks target existence at begin_bulk.
		json += ",\"copy_disposition\":\"" + disposition + "\"";
	}
	for (auto &key : {"DELETION_VECTORS", "COLUMN_MAPPING", "ROW_TRACKING", "MATERIALIZE_ROW_TRACKING",
	                  "CHANGE_DATA_FEED", "IN_COMMIT_TIMESTAMPS", "COMPRESSION", "ROW_GROUP_SIZE",
	                  "BLOOM_FILTER_COLUMNS"}) {
		auto value = GetStringOption(input.info.options, key);
		if (!value.empty()) {
			// The values are option words (true/false/name/none/snappy/…), not arbitrary text — but escape
			// quotes/backslashes anyway so a malformed value fails provider-side, not as broken JSON.
			string escaped;
			for (char c : value) {
				if (c == '"' || c == '\\') {
					escaped += '\\';
				}
				escaped += c;
			}
			json += ",\"" + StringUtil::Lower(key) + "\":\"" + escaped + "\"";
		}
	}
	json += "}";
	bind_data->options_json = std::move(json);
	return std::move(bind_data);
}

static unique_ptr<GlobalFunctionData> CopyToInitGlobal(ClientContext &context, FunctionData &bind_data_p,
                                                       const string &file_path) {
	auto &bind_data = bind_data_p.Cast<FabricatorCopyBindData>();
	auto gstate = make_uniq<FabricatorCopyGlobalState>(context, bind_data);
	auto handle = bind_data.handle;
	gstate->txn_id = (int64_t)context.ActiveTransaction().global_transaction_id;
	if (bind_data.transient_delta) {
		// FORMAT delta: open the per-execution transient catalog rooted at the target's parent directory.
		// Owned by the gstate (closed at finalize; rolled back + closed by the destructor on failure).
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
		// A COPY has no SECRET clause, but it opens a REAL catalog and needs the same credential an ATTACH
		// would carry: without one the provider silently falls back to the host filesystem, which on
		// abfss:// cannot commit atomically and cannot rename or remove a directory at all. Resolve the
		// azure secret whose SCOPE covers the target (azure secrets scope to abfss:// by default, so this
		// normally needs no user action) and hand its fields over on the connection string exactly as the
		// named-secret ATTACH path does. No match => unchanged => the previous host-FS behaviour.
		string root = bind_data.catalog_name;
		if (StringUtil::StartsWith(StringUtil::Lower(root), "abfss://")) {
			root = BuildConnectionStringFromScopedSecret(context, root, "azure", "delta");
		}
		handle = fabricator::OpenCatalog(root, "delta", bind_data.options_json);
		gstate->owned_catalog = handle;
		fabricator::SetActiveTxn(handle, gstate->txn_id);
	}
	// Stream rows to the provider as they are sinked (bounded memory). The target is
	// created at begin per CREATE_TABLE/REPLACE, then rows stream in.
	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	auto *stream = gstate->producer->Stream();
	stream->get_schema(stream, &schema);
	fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context)); // host-FS opener for a Delta-catalog COPY
	gstate->bulk_session = fabricator::BeginBulk(handle, bind_data.schema_name, bind_data.table_name,
	                                           bind_data.create_table, bind_data.replace, /*check_constraints=*/false,
	                                           gstate->txn_id, schema, bind_data.partition_columns,
	                                           bind_data.sort_columns, bind_data.schema_mode,
	                                           bind_data.partition_overwrite);
	return std::move(gstate);
}

static unique_ptr<LocalFunctionData> CopyToInitLocal(ExecutionContext &context, FunctionData &bind_data) {
	return make_uniq<FabricatorCopyLocalState>();
}

static void CopyToSink(ExecutionContext &context, FunctionData &bind_data_p, GlobalFunctionData &gstate_p,
                       LocalFunctionData &lstate_p, DataChunk &input) {
	auto &bind_data = bind_data_p.Cast<FabricatorCopyBindData>();
	auto &gstate = gstate_p.Cast<FabricatorCopyGlobalState>();
	if (input.size() == 0) {
		return;
	}
	ArrowAppender appender(bind_data.column_types, input.size(), gstate.properties, gstate.extension_types);
	appender.Append(input, 0, input.size(), input.size());
	ArrowArray array = appender.Finalize();
	// Stream the batch to the provider; PushBatch blocks for backpressure when the
	// channel is full. COPY runs serially, so no lock is needed.
	fabricator::PushBatch(gstate.bulk_session, array);
}

static void CopyToCombine(ExecutionContext &context, FunctionData &bind_data, GlobalFunctionData &gstate,
                          LocalFunctionData &lstate) {
}

static void CopyToFinalize(ClientContext &context, FunctionData &bind_data_p, GlobalFunctionData &gstate_p) {
	auto &bind_data = bind_data_p.Cast<FabricatorCopyBindData>();
	auto &gstate = gstate_p.Cast<FabricatorCopyGlobalState>();
	// Signal end-of-stream and wait for the background load to drain. complete_bulk CONSUMES the session
	// (the managed side frees the handle even when the call returns an error), so mark it consumed BEFORE the
	// call — if it throws with the flag still false, the gstate destructor would CompleteBulk(abort) AGAIN on
	// the freed value, and GCHandle slots are recycled: the double-free kills an unrelated live handle
	// (surfaced as intermittent "stale catalog handle" commit failures after an erroring COPY).
	auto session = gstate.bulk_session;
	gstate.bulk_completed = true;
	gstate.bulk_session = nullptr;
	fabricator::CompleteBulk(session, /*abort=*/false);

	if (bind_data.transient_delta) {
		// FORMAT delta: the provider PARKS an append (CREATE_TABLE false) in its per-transaction buffer and
		// flushes at commit_transaction — the transient catalog has no transaction manager, so the COPY
		// drives the commit itself (a no-op for CREATE/REPLACE, which committed in complete_bulk). The
		// finalize context's transaction is still active, so the opener resolves secrets directly.
		auto handle = gstate.owned_catalog;
		fabricator::SetActiveOpener(reinterpret_cast<FabricatorHandle>(&context));
		fabricator::SetActiveTxn(handle, gstate.txn_id);
		fabricator::CommitTransaction(handle);
		gstate.owned_catalog = nullptr;
		fabricator::CloseCatalog(handle);
		return;
	}

	// Register the target in the attached catalog so it's queryable immediately
	// (also invalidates any stale cached entry, e.g. for CREATE_TABLE/REPLACE).
	auto &catalog = Catalog::GetCatalog(context, bind_data.catalog_name);
	if (FabricatorCatalog::Is(catalog)) {
		auto schema = catalog.GetSchema(context, bind_data.schema_name, OnEntryNotFound::RETURN_NULL);
		if (schema) {
			schema->Cast<FabricatorSchemaEntry>().AddTable(bind_data.table_name, "BASE TABLE");
		}
	}
}

void RegisterFabricatorCopyFunction(ExtensionLoader &loader) {
	// Register under both names: "mssql" (native) and "bcp" (compatibility
	// with the C++ mssql extension's `COPY ... (FORMAT 'bcp')`). Both route to the
	// same Arrow -> SqlBulkCopy bulk path.
	for (const char *fmt : {"mssql", "bcp"}) {
		CopyFunction function(fmt);
		function.copy_to_bind = CopyToBind;
		function.copy_to_initialize_global = CopyToInitGlobal;
		function.copy_to_initialize_local = CopyToInitLocal;
		function.copy_to_sink = CopyToSink;
		function.copy_to_combine = CopyToCombine;
		function.copy_to_finalize = CopyToFinalize;
		function.extension = "fabricator";
		loader.RegisterFunction(function);
	}
	// COPY … TO '<path>/<table>' (FORMAT delta, …) — path-targeted Delta write, no ATTACH. Same sink
	// machinery, transient per-execution catalog (see DeltaCopyToBind). The official duckdb-delta
	// extension registers no copy function, so the name is free.
	{
		CopyFunction function("delta");
		function.copy_to_bind = DeltaCopyToBind;
		function.copy_to_initialize_global = CopyToInitGlobal;
		function.copy_to_initialize_local = CopyToInitLocal;
		function.copy_to_sink = CopyToSink;
		function.copy_to_combine = CopyToCombine;
		function.copy_to_finalize = CopyToFinalize;
		function.extension = "fabricator";
		loader.RegisterFunction(function);
	}
}

} // namespace duckdb
