//===----------------------------------------------------------------------===//
//                         fabricator — catalog metadata helpers (impl)
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_metadata.hpp"

#include "fabricator/clr_host.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "duckdb/common/arrow/schema_metadata.hpp"
#include "duckdb/common/exception.hpp"

#include <cstdint>
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
		// The column count is CHECKED before dereferencing children[], not assumed. A provider answering a
		// metadata kind it does not implement returns some other shape — the Delta/DAX catalogs' `_ =>` arm is a
		// ONE-column empty table, which is what a Delta catalog gives for FUNCTIONS — so reading children[1..]
		// of that would be out of bounds. Checked per BATCH rather than on the schema on purpose: a zero-row
		// stream carries nothing to read, so its width is immaterial, and several callers legitimately rely on
		// that (DiscoverFunctions asks for 3 columns and a Delta catalog answers with 1 and no rows). Only a
		// batch that actually HAS rows must be wide enough.
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

// True iff `json` (a flat JSON object OUR bridge serialized — values are bare booleans, so the key text
// cannot appear inside a value) carries `"key": true`. Absent key / anything else => false, the safe
// direction for every capability. Deliberately not a JSON parser: the producer is Bootstrap's own
// System.Text.Json serialization of a Dictionary<string, bool>, so the token after the colon is exactly
// `true` or `false`.
static bool ReadCapabilityFlag(const string &json, const char *key) {
	string needle = string("\"") + key + "\"";
	size_t pos = json.find(needle);
	if (pos == string::npos) {
		return false;
	}
	pos += needle.size();
	while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\n' || json[pos] == '\r')) {
		pos++;
	}
	if (pos >= json.size() || json[pos] != ':') {
		return false;
	}
	pos++;
	while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\n' || json[pos] == '\r')) {
		pos++;
	}
	return json.compare(pos, 4, "true") == 0;
}

FabricatorCapabilities FetchCapabilities(FabricatorHandle handle) {
	// ONE typed crossing (ABI v71) replaces the old pattern of grepping the diagnostic kind-7
	// (property, value) stream twice — see abi.h `get_capabilities` for the contract and for why this is
	// deliberately NOT part of open_catalog's result (open_catalog must stay connection-free; a provider
	// may need a connection to answer, and here the txn/opener ambients are already established).
	auto json = fabricator::GetCapabilities(handle);
	FabricatorCapabilities caps;
	caps.string_order_pushable = ReadCapabilityFlag(json, "is_binary_collation");
	caps.exact_filter_pushdown = ReadCapabilityFlag(json, "exact_filter_pushdown");
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
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::TableInfo(table_handle, stream);
	auto rows = ReadStringTable(stream, 3); // role, name, type
	FabricatorTableRowIdentity result;
	for (idx_t i = 0; i < rows[0].size(); i++) {
		if (rows[0][i] == "rowid") {
			result.rowid_columns.push_back(rows[1][i]);
		} else if (rows[0][i] == "virtual") {
			result.virtual_columns.emplace_back(rows[1][i], rows[2][i]);
		}
		// An unknown role is skipped rather than refused: additive roles must stay additive (the same
		// forward-compat rule as an unknown ATTACH option).
	}
	return result;
}

// Reads an int64 value from one column array of an Arrow record batch (validity bitmap honoured; a NULL
// reads as `fallback`).
static int64_t GetInt64(const ArrowArray &column, int64_t row, int64_t fallback) {
	int64_t i = column.offset + row;
	if (column.buffers[0]) {
		auto validity = reinterpret_cast<const uint8_t *>(column.buffers[0]);
		if (!(validity[i / 8] & (1u << (i % 8)))) {
			return fallback;
		}
	}
	return reinterpret_cast<const int64_t *>(column.buffers[1])[i];
}

FabricatorTableStats FetchTableStats(FabricatorHandle table_handle) {
	// (stat, column, value:int64) — the value column is TYPED (the old kinds 4/5 crossed numbers as text),
	// so this needs its own reader beside the all-UTF-8 ReadStringTable.
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::TableStats(table_handle, stream);

	FabricatorTableStats result;
	ArrowSchema schema;
	std::memset(&schema, 0, sizeof(schema));
	if (stream.get_schema(&stream, &schema) != 0) {
		if (stream.release) {
			stream.release(&stream);
		}
		throw IOException("fabricator: failed to read table_stats schema");
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
			throw IOException("fabricator: failed to read table_stats batch");
		}
		if (!batch.release) {
			break; // end of stream
		}
		if (batch.length > 0 && batch.n_children < 3) {
			batch.release(&batch);
			if (stream.release) {
				stream.release(&stream);
			}
			throw IOException("fabricator: table_stats batch has %lld columns, expected 3",
			                  static_cast<long long>(batch.n_children));
		}
		for (int64_t row = 0; row < batch.length; row++) {
			auto stat = GetUtf8(*batch.children[0], row);
			if (stat == "row_count") {
				result.row_count = GetInt64(*batch.children[2], row, -1);
			} else if (stat == "ndv") {
				result.column_ndv[GetUtf8(*batch.children[1], row)] = GetInt64(*batch.children[2], row, -1);
			}
			// Unknown stat names are skipped — additive stats must stay additive.
		}
		batch.release(&batch);
	}
	if (stream.release) {
		stream.release(&stream);
	}
	return result;
}

} // namespace duckdb
