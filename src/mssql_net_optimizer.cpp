//===----------------------------------------------------------------------===//
//                         mssql_net — optimizer extension (impl)
//===----------------------------------------------------------------------===//

#include "mssql_net_optimizer.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/planner/bound_result_modifier.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
#include "duckdb/planner/operator/logical_limit.hpp"
#include "duckdb/planner/operator/logical_projection.hpp"

namespace duckdb {

namespace {

// Finds an mssql_net scan Get directly under `node` (through at most one
// Projection). Deliberately does NOT descend through a LogicalFilter: a scan with
// a residual filter must not get a TOP (a best-effort filter returns a superset, so
// limiting before exact filtering could drop rows).
LogicalGet *FindMssqlScanGet(LogicalOperator &node) {
	if (node.children.size() != 1) {
		return nullptr;
	}
	auto *child = node.children[0].get();
	LogicalGet *get = nullptr;
	if (child->type == LogicalOperatorType::LOGICAL_GET) {
		get = &child->Cast<LogicalGet>();
	} else if (child->type == LogicalOperatorType::LOGICAL_PROJECTION && child->children.size() == 1 &&
	           child->children[0]->type == LogicalOperatorType::LOGICAL_GET) {
		get = &child->children[0]->Cast<LogicalGet>();
	}
	if (get && get->function.name == "mssql_net_scan") {
		return get;
	}
	return nullptr;
}

// LIMIT n (constant, no/zero offset) over an mssql_net scan -> record `n` so the
// scan issues `SELECT TOP (n)`. The LogicalLimit is left in place; DuckDB still
// applies the limit, so this only trims rows on the wire.
void TryPushLimit(LogicalOperator &op) {
	if (op.type != LogicalOperatorType::LOGICAL_LIMIT) {
		return;
	}
	auto &limit = op.Cast<LogicalLimit>();
	if (limit.limit_val.Type() != LimitNodeType::CONSTANT_VALUE) {
		return; // percentage / expression limit: not a TOP (n)
	}
	auto offset_type = limit.offset_val.Type();
	if (offset_type == LimitNodeType::CONSTANT_VALUE) {
		if (limit.offset_val.GetConstantValue() > 0) {
			return; // TOP has no OFFSET; let DuckDB handle it
		}
	} else if (offset_type != LimitNodeType::UNSET) {
		return; // expression / percentage offset
	}
	auto *get = FindMssqlScanGet(op);
	if (!get) {
		return;
	}
	auto &bind_data = get->bind_data->Cast<arrownet::ArrowStreamBindData>();
	bind_data.top_n = static_cast<int64_t>(limit.limit_val.GetConstantValue());
}

void OptimizeNode(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	TryPushLimit(*plan);
	for (auto &child : plan->children) {
		OptimizeNode(input, child);
	}
}

void MssqlNetOptimize(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	OptimizeNode(input, plan);
}

} // namespace

void RegisterMssqlNetOptimizer(DBConfig &config) {
	OptimizerExtension extension;
	extension.optimize_function = MssqlNetOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

} // namespace duckdb
