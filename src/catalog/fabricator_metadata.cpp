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
	fabricator::GetMetadata(handle, FABRICATOR_META_SCHEMAS, "", "", stream);
	auto rows = ReadStringTable(stream, 1);
	return rows[0];
}

bool FetchBinaryCollation(FabricatorHandle handle) {
	// Read the detected server profile (property, value rows) and look for the binary-collation flag.
	// A binary (_BIN/_BIN2) database collation sorts strings by byte value — identical to DuckDB — so a
	// pushed SQL TOP+ORDER BY on a string column matches DuckDB's ordering. Best-effort: any failure or a
	// missing row => false (string ORDER BY pushdown stays off, the safe default).
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::GetMetadata(handle, FABRICATOR_META_SERVER_INFO, "", "", stream);
	auto rows = ReadStringTable(stream, 2); // property, value
	for (idx_t i = 0; i < rows[0].size(); i++) {
		if (rows[0][i] == "is_binary_collation") {
			return rows[1][i] == "true";
		}
	}
	return false;
}

bool FetchExactFilterPushdown(FabricatorHandle handle) {
	// Read the provider profile and look for the `exact_filter_pushdown` flag: TRUE => the provider applies
	// pushed table filters exactly (currently the Delta native_read catalog, which reads via read_parquet on
	// the host DuckDB), so the host may set filter_pushdown=true. Best-effort: any failure / missing row =>
	// false (filter_pushdown stays off — the safe superset-and-DuckDB-re-applies default).
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::GetMetadata(handle, FABRICATOR_META_SERVER_INFO, "", "", stream);
	auto rows = ReadStringTable(stream, 2); // property, value
	for (idx_t i = 0; i < rows[0].size(); i++) {
		if (rows[0][i] == "exact_filter_pushdown") {
			return rows[1][i] == "true";
		}
	}
	return false;
}

vector<FabricatorTableInfo> DiscoverTables(FabricatorHandle handle) {
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::GetMetadata(handle, FABRICATOR_META_TABLES, "", "", stream);
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
	fabricator::GetMetadata(handle, FABRICATOR_META_FUNCTIONS, "", "", stream);
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
	// Best-effort BY CONTRACT (see the header): declaring catalog macros is optional, and a provider that does
	// not serve the kind answers with an error (SqlServerCatalog's default arm throws) or an unrelated empty
	// table (the Delta/DAX catalogs' `_ =>` fallback). Swallowing here keeps every caller free of its own guard
	// and matches how the GLOBAL macro registration degrades — a macro problem must never block an ATTACH.
	vector<FabricatorMacroInfo> macros;
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		fabricator::GetMetadata(handle, FABRICATOR_META_CATALOG_MACROS, "", "", stream);
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

vector<string> FetchRowIdColumns(FabricatorHandle handle, const string &schema_name, const string &table_name) {
	// The managed side picks the PK (else the smallest unique index) and returns
	// its columns in key order.
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::GetMetadata(handle, FABRICATOR_META_ROWID, schema_name, table_name, stream);
	auto rows = ReadStringTable(stream, 1);
	return rows[0];
}

vector<std::pair<string, string>> FetchVirtualColumns(FabricatorHandle handle, const string &schema_name,
                                                      const string &table_name) {
	// Best-effort: a provider that doesn't implement the kind (or returns an unexpected shape) simply
	// contributes no virtual columns — never fail entry materialization over this.
	vector<std::pair<string, string>> result;
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		fabricator::GetMetadata(handle, FABRICATOR_META_VIRTUAL_COLUMNS, schema_name, table_name, stream);
		auto rows = ReadStringTable(stream, 2); // name, type-text
		for (idx_t i = 0; i < rows[0].size(); i++) {
			result.emplace_back(rows[0][i], rows[1][i]);
		}
	} catch (...) {
		result.clear();
	}
	return result;
}

int64_t FetchRowCount(FabricatorHandle handle, const string &schema_name, const string &table_name) {
	// Approximate row count from partition stats (cheap metadata read); -1 if unknown.
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::GetMetadata(handle, FABRICATOR_META_ROWCOUNT, schema_name, table_name, stream);
	auto rows = ReadStringTable(stream, 1);
	if (rows.empty() || rows[0].empty()) {
		return -1;
	}
	try {
		return std::stoll(rows[0][0]);
	} catch (...) {
		return -1;
	}
}

std::unordered_map<string, int64_t> FetchColumnNdv(FabricatorHandle handle, const string &schema_name,
                                                   const string &table_name) {
	// Two columns: column name, NDV (as text). Columns without a leading-key stat are
	// simply absent (=> unknown). Errors (e.g. permissions) bubble up; the caller may
	// swallow them — NDV is an optimizer hint, not required for correctness.
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	fabricator::GetMetadata(handle, FABRICATOR_META_COLUMN_NDV, schema_name, table_name, stream);
	auto rows = ReadStringTable(stream, 2);
	std::unordered_map<string, int64_t> result;
	if (rows.size() < 2) {
		return result;
	}
	for (idx_t i = 0; i < rows[0].size() && i < rows[1].size(); i++) {
		try {
			result[rows[0][i]] = std::stoll(rows[1][i]);
		} catch (...) {
			// skip unparseable rows
		}
	}
	return result;
}

void FetchTableColumns(ClientContext &context, FabricatorHandle handle, const string &schema_name,
                       const string &table_name, vector<string> &names, vector<LogicalType> &types) {
	// The COLUMNS metadata stream carries zero rows; its Arrow schema describes
	// the table's columns, from which DuckDB infers the LogicalTypes.
	fabricator::ArrowStreamBindData bind_data;
	bind_data.factory = [handle, schema_name, table_name](const fabricator::ArrowScanRequest &, ArrowArrayStream &out) {
		fabricator::GetMetadata(handle, FABRICATOR_META_COLUMNS, schema_name, table_name, out);
	};
	// Read-your-writes: re-fetching a just-created table's columns (to build its catalog entry) inside an
	// explicit transaction must see the uncommitted CREATE, so key the metadata read to the active txn.
	FabricatorSetActiveTxn(handle, context);
	fabricator::PopulateReturnSchema(context, bind_data, types, names);
}

} // namespace duckdb
