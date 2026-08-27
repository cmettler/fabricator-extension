// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — optimizer extension (impl)
//===----------------------------------------------------------------------===//

#include "fabricator_optimizer.hpp"

#include "fabricator/arrow_ingest.hpp"
#include "duckdb/common/enums/order_type.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/planner/bound_result_modifier.hpp"
#include "duckdb/planner/expression/bound_columnref_expression.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
#include "duckdb/planner/operator/logical_limit.hpp"
#include "duckdb/planner/operator/logical_projection.hpp"
#include "duckdb/planner/operator/logical_top_n.hpp"

namespace duckdb {

namespace {

// An fabricator scan Get found directly under a node, through at most one Projection.
// We deliberately do NOT descend through a LogicalFilter: a scan with a residual
// filter must not get a TOP/ORDER BY (a best-effort filter returns a superset, so
// limiting/ordering before exact filtering could drop or mis-rank rows).
struct ScanMatch {
	LogicalGet *get = nullptr;
	LogicalProjection *proj = nullptr;
};

ScanMatch FindScan(LogicalOperator &node) {
	ScanMatch m;
	if (node.children.size() != 1) {
		return m;
	}
	auto *child = node.children[0].get();
	if (child->type == LogicalOperatorType::LOGICAL_GET) {
		auto &g = child->Cast<LogicalGet>();
		if (g.function.name == "fabricator_scan") {
			m.get = &g;
		}
	} else if (child->type == LogicalOperatorType::LOGICAL_PROJECTION && child->children.size() == 1 &&
	           child->children[0]->type == LogicalOperatorType::LOGICAL_GET) {
		auto &g = child->children[0]->Cast<LogicalGet>();
		if (g.function.name == "fabricator_scan") {
			m.get = &g;
			m.proj = &child->Cast<LogicalProjection>();
		}
	}
	return m;
}

void JsonStr(const string &s, string &out) {
	out += '"';
	for (char c : s) {
		if (c == '"' || c == '\\') {
			out += '\\';
		}
		out += c;
	}
	out += '"';
}

// Resolves an ORDER BY expression to a plain provider column (name + nullability +
// whether it is a VARCHAR). Handles a Projection between the ordering node and the
// Get. Returns false for anything that is not a direct column reference.
bool ResolveOrderColumn(const Expression &expr, LogicalGet &get, LogicalProjection *proj,
                        fabricator::ArrowStreamBindData &bind_data, string &name, bool &nullable, bool &is_string) {
	const Expression *e = &expr;
	if (proj) {
		if (e->GetExpressionClass() == ExpressionClass::BOUND_REF) {
			auto idx = e->Cast<BoundReferenceExpression>().index;
			if (idx >= proj->expressions.size()) {
				return false;
			}
			e = proj->expressions[idx].get();
		} else if (e->GetExpressionClass() == ExpressionClass::BOUND_COLUMN_REF) {
			auto &cr = e->Cast<BoundColumnRefExpression>();
			if (cr.binding.table_index == proj->table_index && cr.binding.column_index < proj->expressions.size()) {
				e = proj->expressions[cr.binding.column_index].get();
			}
		}
	}

	idx_t col_ids_index;
	if (e->GetExpressionClass() == ExpressionClass::BOUND_COLUMN_REF) {
		auto &cr = e->Cast<BoundColumnRefExpression>();
		if (cr.binding.table_index != get.table_index) {
			return false;
		}
		col_ids_index = cr.binding.column_index;
	} else if (e->GetExpressionClass() == ExpressionClass::BOUND_REF) {
		col_ids_index = e->Cast<BoundReferenceExpression>().index;
	} else {
		return false;
	}

	auto &col_ids = get.GetColumnIds();
	if (col_ids_index >= col_ids.size()) {
		return false;
	}
	auto &ci = col_ids[col_ids_index];
	if (!ci.HasPrimaryIndex() || ci.HasChildren()) {
		return false;
	}
	auto table_col = ci.GetPrimaryIndex();
	if (table_col >= bind_data.names.size()) {
		return false;
	}
	name = bind_data.names[table_col];
	nullable = table_col >= bind_data.column_nullable.size() || bind_data.column_nullable[table_col];
	is_string = table_col < bind_data.return_types.size() &&
	            bind_data.return_types[table_col].id() == LogicalTypeId::VARCHAR;
	return true;
}

// The RESOLVED null placement of an order key: a bare `ORDER BY x` carries no modifier, so the answer
// comes from `default_null_order` (DuckDB's default is NULLS LAST for ASC). Every consumer below wants the
// resolved value, never the parsed one — describing the key by its modifier would push an order DuckDB's
// own TopN does not apply, and the pushed LIMIT would then trim the wrong rows.
bool ResolvedNullsFirst(ClientContext &context, OrderType type, OrderByNullType null_order) {
	return DBConfig::GetConfig(context).ResolveNullOrder(context, type, null_order) == OrderByNullType::NULLS_FIRST;
}

// SQL Server orders NULLs FIRST for ASC and LAST for DESC, and T-SQL cannot spell the other one — so for a
// provider with that fixed convention we may only push a key whose effective NULL ordering already matches
// (or whose column is NOT NULL). A provider that RENDERS the placement instead (`null_order_expressible` —
// the Delta reader, whose ORDER BY is executed by DuckDB) is handed the resolved value and needs no gate.
//
// ⚠ The gate is not a corner case: DuckDB's default is NULLS LAST for ASC, so under it EVERY bare
// `ORDER BY x LIMIT n` on a NULLABLE column is declined. That is why a provider must declare the capability
// for TopN pushdown to fire on the shape people actually write.
bool NullOrderCompatible(bool nulls_first, OrderType type, bool nullable) {
	if (!nullable) {
		return true;
	}
	return type == OrderType::ASCENDING ? nulls_first : !nulls_first;
}

// LIMIT n (constant, no/zero offset) over a fabricator scan -> SELECT TOP (n).
void TryPushLimit(LogicalOperator &op) {
	if (op.type != LogicalOperatorType::LOGICAL_LIMIT) {
		return;
	}
	auto &limit = op.Cast<LogicalLimit>();
	if (limit.limit_val.Type() != LimitNodeType::CONSTANT_VALUE) {
		return;
	}
	auto offset_type = limit.offset_val.Type();
	if (offset_type == LimitNodeType::CONSTANT_VALUE) {
		if (limit.offset_val.GetConstantValue() > 0) {
			return;
		}
	} else if (offset_type != LimitNodeType::UNSET) {
		return;
	}
	auto match = FindScan(op);
	if (!match.get) {
		return;
	}
	match.get->bind_data->Cast<fabricator::ArrowStreamBindData>().top_n =
	    static_cast<int64_t>(limit.limit_val.GetConstantValue());
}

// TopN (ORDER BY + LIMIT, fused by the built-in TOP_N optimizer) over a fabricator scan -> the provider's
// own top-n (`SELECT TOP (n) … ORDER BY …` on SQL Server; `ORDER BY … LIMIT n` in the Delta reader's
// generated SQL) — but only when there is no pushed filter and EVERY key is a plain column whose ordering
// the provider reproduces exactly. The LogicalTopN is KEPT, so DuckDB re-sorts and re-limits; pushing only
// trims wire rows. That is what makes an over-broad push merely wasteful and a WRONG one silently lossy:
// rows the provider trimmed never arrive, and the kept TopN cannot re-select what it never saw.
//
// ⚠ `ResolveOrderColumn`'s "must be a plain column reference" test does more work than it looks, and it is
// load-bearing for exactly this: DuckDB pushes a COLLATION onto every ORDER BY key at bind
// (bind_select_node.cpp -> ExpressionBinder::PushCollation), replacing the expression with a function call
// whenever the key's comparison is not the naive one — an explicit `COLLATE`, a session `default_collation`
// other than binary/c/posix, and also TIME_TZ and INTERVAL keys (`timetz_byte_comparable`,
// `normalized_interval`). All of those therefore arrive as BOUND_FUNCTION and are declined here, without
// this file having to enumerate them.
void TryPushTopN(ClientContext &context, LogicalOperator &op) {
	if (op.type != LogicalOperatorType::LOGICAL_TOP_N) {
		return;
	}
	auto &topn = op.Cast<LogicalTopN>();
	if (topn.offset > 0 || topn.orders.empty()) {
		return; // TOP has no OFFSET
	}
	auto match = FindScan(op);
	if (!match.get) {
		return;
	}
	auto &bind_data = match.get->bind_data->Cast<fabricator::ArrowStreamBindData>();
	if (!bind_data.filter_json.empty()) {
		return; // filter present: TOP/ORDER before exact filtering would be unsafe
	}

	string order_json = "[";
	for (idx_t i = 0; i < topn.orders.size(); i++) {
		auto &o = topn.orders[i];
		string name;
		bool nullable = true;
		bool is_string = true;
		// Require EVERY key to resolve — a prefix-only push plus a limit would let the provider pick the
		// wrong top-n (it would order by fewer keys than DuckDB and trim on that).
		if (!ResolveOrderColumn(*o.expression, *match.get, match.proj, bind_data, name, nullable, is_string)) {
			return;
		}
		if (is_string && !bind_data.string_order_pushable) {
			// String ordering is collation-dependent, so a pushed top-n could trim the wrong rows unless the
			// source orders strings as DuckDB does. Declared per catalog at LoadCatalog: SQL Server asserts
			// it under a binary database collation (byte-order sort == DuckDB); the Delta reader asserts it
			// unconditionally, because its ORDER BY is executed BY DuckDB.
			return;
		}
		bool nulls_first = ResolvedNullsFirst(context, o.type, o.null_order);
		if (!bind_data.null_order_expressible && !NullOrderCompatible(nulls_first, o.type, nullable)) {
			return;
		}
		if (i) {
			order_json += ',';
		}
		order_json += "{\"col\":";
		JsonStr(name, order_json);
		order_json += ",\"desc\":";
		order_json += o.type == OrderType::DESCENDING ? "true" : "false";
		order_json += ",\"nulls_first\":";
		order_json += nulls_first ? "true" : "false";
		order_json += "}";
	}
	order_json += "]";
	bind_data.order_by_json = std::move(order_json);
	bind_data.top_n = static_cast<int64_t>(topn.limit);
}

void OptimizeNode(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	TryPushLimit(*plan);
	TryPushTopN(input.context, *plan);
	for (auto &child : plan->children) {
		OptimizeNode(input, child);
	}
}

void FabricatorOptimize(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	OptimizeNode(input, plan);
}

} // namespace

void RegisterFabricatorOptimizer(DBConfig &config) {
	OptimizerExtension extension;
	extension.optimize_function = FabricatorOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

} // namespace duckdb
