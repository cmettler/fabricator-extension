//===----------------------------------------------------------------------===//
//                         mssql_net — catalog metadata helpers (impl)
//===----------------------------------------------------------------------===//

#include "catalog/mssql_net_metadata.hpp"

#include "arrownet/clr_host.hpp"
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
		throw IOException("mssql_net: failed to read metadata schema");
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
			throw IOException("mssql_net: failed to read metadata batch");
		}
		if (!batch.release) {
			break; // end of stream
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

vector<string> DiscoverSchemas(ArrowNetHandle handle) {
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	arrownet::GetMetadata(handle, ARROWNET_META_SCHEMAS, "", "", stream);
	auto rows = ReadStringTable(stream, 1);
	return rows[0];
}

vector<MssqlNetTableInfo> DiscoverTables(ArrowNetHandle handle) {
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	arrownet::GetMetadata(handle, ARROWNET_META_TABLES, "", "", stream);
	auto rows = ReadStringTable(stream, 3);

	vector<MssqlNetTableInfo> tables;
	for (idx_t i = 0; i < rows[0].size(); i++) {
		tables.push_back({rows[0][i], rows[1][i], rows[2][i]});
	}
	return tables;
}

vector<string> FetchRowIdColumns(ArrowNetHandle handle, const string &schema_name, const string &table_name) {
	// The managed side picks the PK (else the smallest unique index) and returns
	// its columns in key order.
	ArrowArrayStream stream;
	std::memset(&stream, 0, sizeof(stream));
	arrownet::GetMetadata(handle, ARROWNET_META_ROWID, schema_name, table_name, stream);
	auto rows = ReadStringTable(stream, 1);
	return rows[0];
}

void FetchTableColumns(ClientContext &context, ArrowNetHandle handle, const string &schema_name,
                       const string &table_name, vector<string> &names, vector<LogicalType> &types) {
	// The COLUMNS metadata stream carries zero rows; its Arrow schema describes
	// the table's columns, from which DuckDB infers the LogicalTypes.
	arrownet::ArrowStreamBindData bind_data;
	bind_data.factory = [handle, schema_name, table_name](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		arrownet::GetMetadata(handle, ARROWNET_META_COLUMNS, schema_name, table_name, out);
	};
	arrownet::PopulateReturnSchema(context, bind_data, types, names);
}

} // namespace duckdb
