// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — CREATE TABLE clause helpers
//===----------------------------------------------------------------------===//
// Extracts the column names from a native CREATE TABLE [AS] ... PARTITIONED BY (cols) clause
// (CreateTableInfo::partition_keys) into a comma-separated list for the create_table / begin_bulk ABI.
// The Delta provider records them as the table's partition columns; SQL Server / DAX ignore the arg.
// Also extracts the WITH (key='value', ...) options clause (CreateTableInfo::options) into a flat JSON
// object for the create_table / begin_bulk `options_json` arg (ABI v67) — the provider parses the keys
// it knows and REJECTS unknown ones (never silently ignored).
//===----------------------------------------------------------------------===//

#pragma once

#include "duckdb/common/case_insensitive_map.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/exception/binder_exception.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
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

// Minimal JSON string escape for the WITH-options JSON (keys/values are option names + constant text).
inline std::string EscapeJsonString(const std::string &s) {
	std::string out;
	out.reserve(s.size() + 2);
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
			if (static_cast<unsigned char>(c) < 0x20) {
				char buf[8];
				snprintf(buf, sizeof(buf), "\\u%04x", (unsigned)(unsigned char)c);
				out += buf;
			} else {
				out += c;
			}
		}
	}
	return out;
}

// Serializes the CREATE TABLE [AS] ... WITH (key='value', ...) options clause as a flat JSON object
// (all values rendered as their constant TEXT — the managed parsers take strings). Only CONSTANT values
// are accepted (one CAST level unwrapped — boolean literals parse as CAST(... AS BOOLEAN)); anything
// else throws. Empty options => "".
inline std::string TableOptionsArg(
    const duckdb::case_insensitive_map_t<duckdb::unique_ptr<duckdb::ParsedExpression>> &options) {
	if (options.empty()) {
		return "";
	}
	std::string out = "{";
	bool first = true;
	for (auto &kv : options) {
		const duckdb::ParsedExpression *expr = kv.second.get();
		if (expr && expr->type == duckdb::ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<duckdb::CastExpression>().child.get();
		}
		if (!expr || expr->type != duckdb::ExpressionType::VALUE_CONSTANT) {
			throw duckdb::BinderException("CREATE TABLE WITH option \"%s\" must be a constant value", kv.first);
		}
		auto &val = expr->Cast<duckdb::ConstantExpression>().value;
		if (val.IsNull()) {
			throw duckdb::BinderException("CREATE TABLE WITH option \"%s\" must not be NULL", kv.first);
		}
		if (!first) {
			out += ",";
		}
		first = false;
		out += "\"" + EscapeJsonString(kv.first) + "\":\"" + EscapeJsonString(val.ToString()) + "\"";
	}
	out += "}";
	return out;
}

} // namespace fabricator
