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

// SQL Server orders NULLs FIRST for ASC and LAST for DESC. We may only push an order
// key whose effective NULL ordering matches that (or whose column is NOT NULL).
bool NullOrderCompatible(ClientContext &context, OrderType type, OrderByNullType null_order, bool nullable) {
	if (!nullable) {
		return true;
	}
	auto resolved = DBConfig::GetConfig(context).ResolveNullOrder(context, type, null_order);
	return type == OrderType::ASCENDING ? resolved == OrderByNullType::NULLS_FIRST
	                                     : resolved == OrderByNullType::NULLS_LAST;
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

// TopN (ORDER BY + LIMIT, fused by the built-in TOP_N optimizer) over a fabricator
// scan -> SELECT TOP (n) ... ORDER BY ... — but only when ALL order keys are plain
// non-string columns with compatible NULL ordering and there is no pushed filter.
// The LogicalTopN is kept, so DuckDB re-sorts/limits; pushing only trims wire rows.
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
		// Require EVERY key to be a plain, non-string, NULL-order-compatible column —
		// a prefix-only push + TOP would let SQL pick the wrong top-n.
		if (!ResolveOrderColumn(*o.expression, *match.get, match.proj, bind_data, name, nullable, is_string)) {
			return;
		}
		if (is_string && !bind_data.string_order_pushable) {
			// String ordering is collation-dependent: SQL Server's sort may differ from DuckDB's, so a
			// pushed TOP+ORDER BY could trim the wrong rows. Push it only under a binary database
			// collation (byte-order sort == DuckDB), detected at LoadCatalog. Otherwise keep it off.
			return;
		}
		if (!NullOrderCompatible(context, o.type, o.null_order, nullable)) {
			return;
		}
		if (i) {
			order_json += ',';
		}
		order_json += "{\"col\":";
		JsonStr(name, order_json);
		order_json += ",\"desc\":";
		order_json += o.type == OrderType::DESCENDING ? "true" : "false";
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
