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
#include "duckdb/parser/expression/function_expression.hpp"
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
		if (k->GetExpressionType() == duckdb::ExpressionType::COLUMN_REF) {
			col = k->Cast<duckdb::ColumnRefExpression>().GetColumnName().GetIdentifierName();
		} else {
			col = k->GetName().GetIdentifierName();
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

// The TEXT of ONE table-option value, as both `CREATE TABLE ... WITH (...)` and `ALTER TABLE ... SET (...)`
// render it. Constants only (one CAST level unwrapped — boolean literals parse as CAST(... AS BOOLEAN)); a
// LIST literal is joined with commas, which is exactly the spelling the comma-separated options
// (bloom_filter_columns, …) already take. Anything else throws.
//
// ⚠ ONE function for both statements ON PURPOSE. The option VOCABULARY lives in the provider, so the host's
// only job is to turn an expression into text — and two renderers would be two chances for `SET` and `WITH`
// to disagree about what `['a','b']` or a cast boolean means.
//
// ⚠⚠ `.text`, NEVER `Literal::ToString()`. 2.0 replaced `ConstantExpression::value` (a `Value`) with a
// `Literal`, whose ToString RENDERS SQL THAT PARSES BACK — i.e. a string literal comes out WITH ITS QUOTES.
// The managed side quotes for itself, so ToString double-quotes: measured as *"unknown mode ''id''"* and
// *"unknown codec ''zstd''"*. `text` is the DECODED content (quotes stripped, '' collapsed).
//
// ⚠ A column name containing a comma cannot be expressed in a list either way; that limitation belongs to
// the comma-separated form and is inherited here rather than introduced.
inline std::string TableOptionValueText(const std::string &key, const duckdb::ParsedExpression *expr) {
	if (expr && expr->GetExpressionType() == duckdb::ExpressionType::OPERATOR_CAST) {
		expr = &expr->Cast<duckdb::CastExpression>().Child();
	}
	if (expr && expr->GetExpressionType() == duckdb::ExpressionType::FUNCTION) {
		auto &fn = expr->Cast<duckdb::FunctionExpression>();
		// DuckDB parses `['a','b']` as a `list_value(...)` call, not as a constant, so without this branch
		// the DuckLake-style `SET (skip_stats_columns = ['payload'])` is refused as "not a constant".
		// 2.0 encapsulated both: the name is an Identifier behind FunctionName() and the arguments are
		// FunctionArgument wrappers behind GetArguments(), not bare child expressions.
		if (fn.FunctionName().GetIdentifierName() == "list_value") {
			std::string joined;
			for (auto &arg : fn.GetArguments()) {
				const duckdb::ParsedExpression *item = &arg.GetExpression();
				if (item && item->GetExpressionType() == duckdb::ExpressionType::OPERATOR_CAST) {
					item = &item->Cast<duckdb::CastExpression>().Child();
				}
				if (!item || item->GetExpressionType() != duckdb::ExpressionType::VALUE_CONSTANT) {
					throw duckdb::BinderException("table option \"%s\": every list element must be a constant",
					                              key);
				}
				if (!joined.empty()) {
					joined += ",";
				}
				joined += item->Cast<duckdb::ConstantExpression>().GetLiteral().text;
			}
			return joined;
		}
	}
	if (!expr || expr->GetExpressionType() != duckdb::ExpressionType::VALUE_CONSTANT) {
		throw duckdb::BinderException("table option \"%s\" must be a constant value", key);
	}
	auto &val = expr->Cast<duckdb::ConstantExpression>().GetLiteral();
	if (val.IsNull()) {
		throw duckdb::BinderException("table option \"%s\" must not be NULL", key);
	}
	return val.text;
}

// Serializes a table-options clause as a flat JSON object of TEXT values (the managed parsers take
// strings). Empty options => "".
inline std::string TableOptionsArg(
    const duckdb::case_insensitive_map_t<duckdb::unique_ptr<duckdb::ParsedExpression>> &options) {
	if (options.empty()) {
		return "";
	}
	std::string out = "{";
	bool first = true;
	for (auto &kv : options) {
		if (!first) {
			out += ",";
		}
		first = false;
		out += "\"" + EscapeJsonString(kv.first) + "\":\"" +
		       EscapeJsonString(TableOptionValueText(kv.first, kv.second.get())) + "\"";
	}
	out += "}";
	return out;
}

} // namespace fabricator
