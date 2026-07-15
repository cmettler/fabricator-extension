//===----------------------------------------------------------------------===//
//                         fabricator — PARTITIONED BY helper
//===----------------------------------------------------------------------===//
// Extracts the column names from a native CREATE TABLE [AS] ... PARTITIONED BY (cols) clause
// (CreateTableInfo::partition_keys) into a comma-separated list for the create_table / begin_bulk ABI.
// The Delta provider records them as the table's partition columns; SQL Server / DAX ignore the arg.
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/parsed_expression.hpp"

#include <string>

namespace fabricator {

// Joins the PARTITIONED BY column names (comma-separated; empty if none). Only plain column references are
// emitted — any non-column expression is skipped (the providers partition by column name). A leading/trailing
// comma is never produced, so an all-skipped list yields "".
inline std::string PartitionColumnsArg(
    const duckdb::vector<duckdb::unique_ptr<duckdb::ParsedExpression>> &keys) {
	std::string out;
	for (auto &k : keys) {
		if (!k) {
			continue;
		}
		std::string col;
		if (k->type == duckdb::ExpressionType::COLUMN_REF) {
			col = k->Cast<duckdb::ColumnRefExpression>().GetColumnName();
		} else {
			col = k->GetName();
		}
		if (col.empty()) {
			continue;
		}
		if (!out.empty()) {
			out += ",";
		}
		out += col;
	}
	return out;
}

} // namespace fabricator
