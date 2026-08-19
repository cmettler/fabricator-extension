//===----------------------------------------------------------------------===//
//                         fabricator — catalog metadata helpers (impl)
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_metadata.hpp"

#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "duckdb/common/arrow/schema_metadata.hpp"
#include "duckdb/common/exception.hpp"

// yyjson (vcpkg) — OUR OWN copy, deliberately not DuckDB's vendored `duckdb_yyjson` (namespaced,
// not DUCKDB_API-exported, so a loadable extension could not resolve it). See CMakeLists.txt.
#include <yyjson.h>

#include <cstdint>
#include <cstdlib> // free() — yyjson_mut_write's buffer, allocated by the default allocator
#include <cstring>

namespace duckdb {

// Reads a UTF-8 value from one column array of an Arrow record batch.
static string GetUtf8(const ArrowArray &column, int64_t row) {
	int64_t i = column.offset + row;
	// Null check (validity bitmap is buffers[0]; may be null meaning all-valid).
	if (column.buffers[0]) {
		auto validity = reinterpret_cast<const uint8_t *>(column.buffers[0]);
		if (!(validity[i / 8] & (1u << (i % 8)))) {
			return string();
		}
	}
	auto offsets = reinterpret_cast<const int32_t *>(column.buffers[1]);
	auto data = reinterpret_cast<const char *>(column.buffers[2]);
	int32_t start = offsets[i];
	int32_t end = offsets[i + 1];
	return string(data + start, static_cast<size_t>(end - start));
}

vector<vector<string>> ReadStringTable(ArrowArrayStream &stream, idx_t expected_cols) {
	vector<vector<string>> result;
	for (idx_t c = 0; c < expected_cols; c++) {
		result.emplace_back();
	}

	// Schema is read once (and released); we rely on column ordering.
	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	if (stream.get_schema(&stream, &schema) != 0) {
		if (stream.release) {
			stream.release(&stream);
		}
		throw IOException("fabricator: failed to read metadata schema");
	}
	if (schema.release) {
		schema.release(&schema);
	}

	for (;;) {
		ArrowArray batch;
		std::memset(&batch, 0, sizeof(batch));
		if (stream.get_next(&stream, &batch) != 0) {
			if (stream.release) {
				stream.release(&stream);
			}
			throw IOException("fabricator: failed to read metadata batch");
		}
		if (!batch.release) {
			break; // end of stream
		}
		// The column count is CHECKED before dereferencing children[], not assumed — a mis-shaped provider
		// batch must fail as a message, not as an out-of-bounds read. `>=` rather than `==` because several
		// streams are deliberately WIDER than the host reads (catalog_functions carries 5 columns and
		// DiscoverFunctions reads 3; list_global_functions carries 6 and its caller reads 4). Checked per
		// BATCH rather than on the schema on purpose: a zero-row batch carries nothing to read, so its width
		// is immaterial. Since ABI v72 nothing in-tree exercises that leniency (every list entry has ONE
		// declared shape — the unknown-kind fallback arms this guard was written against are deleted), but it
		// stays: it costs nothing, and it keeps a plugin backend that answers an optional surface (macros)
		// with a minimal empty stream attachable instead of failing over rows that do not exist.
		if (batch.length > 0 && batch.n_children < static_cast<int64_t>(expected_cols)) {
			batch.release(&batch);
			if (stream.release) {
				stream.release(&stream);
			}
			throw IOException("fabricator: metadata batch has %lld columns, expected at least %llu",
			                  static_cast<long long>(batch.n_children),
			                  static_cast<unsigned long long>(expected_cols));
		}
		for (int64_t row = 0; row < batch.length; row++) {
			for (idx_t c = 0; c < expected_cols; c++) {
				result[c].push_back(GetUtf8(*batch.children[c], row));
			}
		}
		batch.release(&batch);
	}
	if (stream.release) {
		stream.release(&stream);
	}
	return result;
}

vector<string> DiscoverSchemas(FabricatorHandle handle) {
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::CatalogSchemas(handle, stream);
	auto rows = ReadStringTable(stream, 1);
	return rows[0];
}

// RAII over a parsed yyjson document (yyjson_doc_free tolerates null).
namespace {
struct YyjsonDocGuard {
	yyjson_doc *doc;
	explicit YyjsonDocGuard(const string &json) : doc(yyjson_read(json.data(), json.size(), 0)) {
	}
	~YyjsonDocGuard() {
		yyjson_doc_free(doc);
	}
	//! The document root when it is an OBJECT, else null.
	yyjson_val *Root() const {
		auto root = doc ? yyjson_doc_get_root(doc) : nullptr;
		return root && yyjson_is_obj(root) ? root : nullptr;
	}
};
} // namespace

FabricatorCapabilities FetchCapabilities(FabricatorHandle handle) {
	// ONE typed crossing (ABI v71) replaces the old pattern of grepping the diagnostic kind-7
	// (property, value) stream twice — see abi.h `get_capabilities` for the contract and for why this is
	// deliberately NOT part of open_catalog's result (open_catalog must stay connection-free; a provider
	// may need a connection to answer, and here the txn/opener ambients are already established).
	//
	// Parsed with yyjson since v73 — this used to be a string-find shortcut (`ReadCapabilityFlag`) that was
	// safe only by a producer-side argument (every value a bare boolean, so key text could not appear inside
	// a value); a real parser retires the caveat class, not just the instance. Absent key / non-boolean /
	// malformed doc all read as false — the safe direction for every capability.
	auto json = fabricator::GetCapabilities(handle);
	YyjsonDocGuard guard(json);
	FabricatorCapabilities caps;
	if (auto *root = guard.Root()) {
		caps.string_order_pushable = yyjson_get_bool(yyjson_obj_get(root, "is_binary_collation"));
		caps.exact_filter_pushdown = yyjson_get_bool(yyjson_obj_get(root, "exact_filter_pushdown"));
		caps.null_order_expressible = yyjson_get_bool(yyjson_obj_get(root, "null_order_expressible"));
	}
	return caps;
}

vector<FabricatorTableInfo> DiscoverTables(FabricatorHandle handle) {
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::CatalogTables(handle, stream);
	auto rows = ReadStringTable(stream, 3);

	vector<FabricatorTableInfo> tables;
	for (idx_t i = 0; i < rows[0].size(); i++) {
		tables.push_back({rows[0][i], rows[1][i], rows[2][i]});
	}
	return tables;
}

vector<FabricatorFunctionInfo> DiscoverFunctions(FabricatorHandle handle) {
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::CatalogFunctions(handle, stream);
	// Columns: schema_name, name, kind, [param_count (int), return_type]. We read only
	// the first three string columns here; the trailing columns are ignored.
	auto rows = ReadStringTable(stream, 3);
	vector<FabricatorFunctionInfo> funcs;
	for (idx_t i = 0; i < rows[0].size(); i++) {
		funcs.push_back({rows[0][i], rows[1][i], rows[2][i]});
	}
	return funcs;
}

vector<FabricatorMacroInfo> DiscoverCatalogMacros(FabricatorHandle handle) {
	// Best-effort BY CONTRACT (see the header): declaring catalog macros is optional. Swallowing here keeps
	// every caller free of its own guard and matches how the GLOBAL macro registration degrades — a macro
	// problem must never block an ATTACH. (Since ABI v72 every provider implements the dedicated entry with
	// a declared 3-column shape, so the old unknown-kind fallback shapes are gone; the guard stays for a
	// genuinely failing provider.)
	vector<FabricatorMacroInfo> macros;
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		fabricator::CatalogMacros(handle, stream);
		// Columns: schema, name, create_sql.
		auto rows = ReadStringTable(stream, 3);
		for (idx_t i = 0; i < rows[0].size(); i++) {
			if (rows[1][i].empty() || rows[2][i].empty()) {
				continue; // a nameless or bodiless declaration cannot be bound to anything
			}
			macros.push_back({rows[0][i], rows[1][i], rows[2][i]});
		}
	} catch (std::exception &) {
		macros.clear(); // partial reads are discarded — an all-or-nothing declaration set is easier to reason about
	}
	return macros;
}

vector<FabricatorViewInfo> DiscoverCatalogViews(FabricatorHandle handle) {
	// Best-effort, exactly as DiscoverCatalogMacros: declaring catalog views is optional, and a provider
	// problem here must never block an ATTACH. Note this only covers the CROSSING — a view whose BODY is
	// broken is not detectable here at all (nothing is parsed until first use), so the two failure modes
	// are handled in different places on purpose.
	vector<FabricatorViewInfo> views;
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		fabricator::CatalogViews(handle, stream);
		// Columns: schema, name, create_sql.
		auto rows = ReadStringTable(stream, 3);
		for (idx_t i = 0; i < rows[0].size(); i++) {
			if (rows[1][i].empty() || rows[2][i].empty()) {
				continue; // a nameless or bodiless declaration cannot be bound to anything
			}
			views.push_back({rows[0][i], rows[1][i], rows[2][i]});
		}
	} catch (std::exception &) {
		views.clear(); // partial reads discarded — all-or-nothing, like the macro set
	}
	return views;
}

void FetchFunctionParamSchema(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                              const string &func_name, vector<string> &names, vector<LogicalType> &types,
                              vector<FabricatorParamStyle> *out_styles) {
	ArrowSchema schema {};
	fabricator::GetFunctionParamSchema(handle, schema_name, func_name, schema);
	if (out_styles) {
		// A parameter's STYLE rides its FIELD metadata (the same C-ABI channel as the volatility signal
		// above): fabricator.param_style = "named" | "table". Absent => positional. ONE schema per function
		// carries all three kinds, so there is no second schema to align this against. Read BEFORE
		// ReadArrowSchema consumes the struct.
		out_styles->clear();
		for (int64_t c = 0; c < schema.n_children; c++) {
			auto style = FabricatorParamStyle::POSITIONAL;
			if (schema.children[c] && schema.children[c]->metadata) {
				ArrowSchemaMetadata field_metadata(schema.children[c]->metadata);
				auto value = field_metadata.GetOption("fabricator.param_style");
				if (value == "named") {
					style = FabricatorParamStyle::NAMED;
				} else if (value == "table") {
					style = FabricatorParamStyle::TABLE_INPUT;
				}
			}
			out_styles->push_back(style);
		}
	}
	fabricator::ReadArrowSchema(context, schema, types, names);
}

LogicalType FetchFunctionReturnType(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                                    const string &func_name, bool *out_volatile) {
	ArrowSchema schema {};
	fabricator::GetFunctionReturnSchema(handle, schema_name, func_name, schema);
	if (out_volatile) {
		// The volatility signal rides the result FIELD's metadata (the same C-ABI channel as extension-type
		// markers): fabricator.volatile = "0" => CONSISTENT (pure, constant-foldable). ABSENT => VOLATILE —
		// the historical default, so old bridges/plugins keep their behavior. Read BEFORE ReadArrowSchema
		// consumes the struct.
		*out_volatile = true;
		if (schema.n_children > 0 && schema.children[0] && schema.children[0]->metadata) {
			ArrowSchemaMetadata field_metadata(schema.children[0]->metadata);
			if (field_metadata.GetOption("fabricator.volatile") == "0") {
				*out_volatile = false;
			}
		}
	}
	vector<string> names;
	vector<LogicalType> types;
	fabricator::ReadArrowSchema(context, schema, types, names);
	if (types.empty()) {
		throw InvalidInputException("fabricator: function '%s.%s' has no scalar return type", schema_name, func_name);
	}
	return types[0];
}

void FetchFunctionOutputSchema(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                               const string &func_name, vector<string> &names, vector<LogicalType> &types) {
	ArrowSchema schema {};
	fabricator::GetFunctionOutputSchema(handle, schema_name, func_name, nullptr, schema);
	fabricator::ReadArrowSchema(context, schema, types, names);
}

void FetchTableSchema(ClientContext &context, FabricatorHandle catalog_handle, FabricatorHandle table_handle,
                      vector<string> &names, vector<LogicalType> &types) {
	// The table_schema stream carries zero rows; its Arrow schema describes the table's columns, from which
	// DuckDB infers the LogicalTypes (PopulateReturnSchema — the one import path, incl. extension types).
	fabricator::ArrowStreamBindData bind_data;
	bind_data.factory = [table_handle](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::TableSchema(table_handle, out);
	};
	// Read-your-writes: re-fetching a just-created table's columns (to build its catalog entry) inside an
	// explicit transaction must see the uncommitted CREATE, so key the read to the active txn — the session
	// binds against this ambient (a table opened WITH an AT clause then answers the AS-OF layout).
	FabricatorSetActiveTxn(catalog_handle, context);
	fabricator::PopulateReturnSchema(context, bind_data, types, names);
}

FabricatorTableRowIdentity FetchTableInfo(FabricatorHandle table_handle) {
	// {"rowid":["a",...], "virtual":[{"name":"...","type":"..."}, ...]} — see abi.h table_info. The
	// producer is TableSession.InfoJson (Utf8JsonWriter, proper escaping), so a malformed doc is a BUG,
	// not an input condition — refused loudly rather than read as an empty answer (a silently-empty rowid
	// would quietly disable UPDATE/DELETE on the table). Unknown keys are skipped: additive fields must
	// stay additive (the same forward-compat rule as an unknown ATTACH option).
	auto json = fabricator::TableInfo(table_handle);
	YyjsonDocGuard guard(json);
	auto *root = guard.Root();
	if (!root) {
		throw IOException("fabricator: table_info returned malformed JSON: %s", json);
	}
	FabricatorTableRowIdentity result;
	size_t idx, max;
	yyjson_val *item;
	// Hoisted out of the foreach macros, which evaluate their container argument more than once.
	auto *rowid_arr = yyjson_obj_get(root, "rowid");
	auto *virtual_arr = yyjson_obj_get(root, "virtual");
	yyjson_arr_foreach(rowid_arr, idx, max, item) {
		if (yyjson_is_str(item)) {
			result.rowid_columns.emplace_back(yyjson_get_str(item), yyjson_get_len(item));
		}
	}
	yyjson_arr_foreach(virtual_arr, idx, max, item) {
		auto *name = yyjson_obj_get(item, "name");
		auto *type = yyjson_obj_get(item, "type");
		if (yyjson_is_str(name) && yyjson_is_str(type)) {
			result.virtual_columns.emplace_back(string(yyjson_get_str(name), yyjson_get_len(name)),
			                                    string(yyjson_get_str(type), yyjson_get_len(type)));
		}
	}
	return result;
}

FabricatorTableStats FetchTableStats(FabricatorHandle table_handle) {
	// {"row_count":N, "ndv":{"<column>":N, ...}} — see abi.h table_stats. row_count ABSENT = unknown
	// (stays -1); the values are TYPED JSON numbers (the old kinds 4/5 crossed them as text).
	auto json = fabricator::TableStats(table_handle);
	YyjsonDocGuard guard(json);
	auto *root = guard.Root();
	if (!root) {
		throw IOException("fabricator: table_stats returned malformed JSON: %s", json);
	}
	FabricatorTableStats result;
	auto *row_count = yyjson_obj_get(root, "row_count");
	if (yyjson_is_int(row_count)) {
		result.row_count = yyjson_get_sint(row_count);
	}
	size_t idx, max;
	yyjson_val *key, *val;
	auto *ndv_obj = yyjson_obj_get(root, "ndv"); // hoisted: the foreach macro multi-evaluates its argument
	yyjson_obj_foreach(ndv_obj, idx, max, key, val) {
		if (yyjson_is_int(val)) {
			result.column_ndv[string(yyjson_get_str(key), yyjson_get_len(key))] = yyjson_get_sint(val);
		}
	}
	return result;
}

string FabricatorRenderAlterJson(const FabricatorAlterRequest &request) {
	// The ONE place the host WRITES an ABI JSON doc (table_info/table_stats/get_capabilities all flow the
	// other way), so it is also the one place that needs yyjson's mutable API. Every string below is a
	// user-controlled identifier or literal, which is exactly why this is a real writer and not
	// concatenation — see the header note on what the hand-rolled predecessor got wrong.
	//
	// ⚠ yyjson_mut_strncpy, NOT yyjson_mut_strn: the plain form does NOT copy (its own doc says so) and
	// would leave every value pointing into `request`. That happens to be safe today — the doc dies at this
	// function's end, `request` is the caller's — but it is a lifetime coupling nothing states at the call
	// site, so a later render-from-a-temporary would be a use-after-free rather than a compile error. The
	// copies are a handful of identifiers. KEYS are string literals (static storage), so the non-copying
	// yyjson_mut_obj_add_* key handling is correct for them.
	struct MutDocGuard {
		yyjson_mut_doc *doc;
		MutDocGuard() : doc(yyjson_mut_doc_new(nullptr)) {
			if (!doc) {
				throw IOException("fabricator: could not allocate the ALTER TABLE request document");
			}
		}
		~MutDocGuard() {
			yyjson_mut_doc_free(doc);
		}
	} guard;
	auto *doc = guard.doc;
	auto *root = yyjson_mut_obj(doc);
	yyjson_mut_doc_set_root(doc, root);

	auto add_str = [&](const char *key, const string &value) {
		yyjson_mut_obj_add_val(doc, root, key, yyjson_mut_strncpy(doc, value.c_str(), value.size()));
	};
	auto add_arr = [&](const char *key, const vector<string> &values) {
		auto *arr = yyjson_mut_arr(doc);
		for (auto &value : values) {
			yyjson_mut_arr_append(arr, yyjson_mut_strncpy(doc, value.c_str(), value.size()));
		}
		yyjson_mut_obj_add_val(doc, root, key, arr);
	};

	add_str("kind", request.kind);
	if (!request.column.empty()) {
		add_str("column", request.column);
	}
	if (!request.new_name.empty()) {
		add_str("new_name", request.new_name);
	}
	if (!request.path.empty()) {
		add_arr("path", request.path);
	}
	if (request.has_columns) {
		add_arr("columns", request.columns);
	}
	if (request.guard) {
		// The kind decides the SPELLING: the ADD kinds guard on absence, the DROP kinds on presence. Two
		// honest keys rather than one overloaded flag bit — a doc that says "if_exists" on an ADD would be
		// the very ambiguity this crossing exists to remove.
		bool adds = request.kind == "add_column" || request.kind == "add_field";
		yyjson_mut_obj_add_bool(doc, root, adds ? "if_not_exists" : "if_exists", true);
	}
	if (request.has_default) {
		if (request.default_is_null) {
			yyjson_mut_obj_add_null(doc, root, "default");
		} else {
			add_str("default", request.default_literal);
		}
	}

	char *rendered = yyjson_mut_write(doc, 0, nullptr);
	if (!rendered) {
		throw IOException("fabricator: could not render the ALTER TABLE request for kind '%s'", request.kind);
	}
	string json(rendered);
	free(rendered); // yyjson_mut_write allocates with the default allocator
	return json;
}

} // namespace duckdb
