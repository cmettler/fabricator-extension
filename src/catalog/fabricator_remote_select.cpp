//===----------------------------------------------------------------------===//
// fabricator_remote_select.cpp
//
// DuckDB 2.0's remote pushdown optimizer, for a provider that declares `remote_select` (SQL Server with the
// `remote_pushdown` ATTACH option). The optimizer runs BEFORE binding and hands us a parsed query node; we bind a
// COPY with DuckDB's own binder, serialize the plan as Substrait (the vendored to_substrait,
// third_party/duckdb-substrait), and the provider translates it into its own query (ABI v96 remote_translate) —
// or the statement runs locally. docs/duckdb-2.0-remote-execution.md §3.2–§3.12.
//
// THE RULE IS THE ONE THE DML SHORTCUT FOLLOWS (§2.1): translate EXACTLY or do not push. Nothing re-checks what the
// server returns, so every failure on the way — bind, producer, provider — runs the statement locally, and the
// provider refuses whatever it cannot reproduce exactly (§3.12 has the measured list).
//
// ⚠ TYPES AND NAMES ARE DUCKDB'S: the server's columns c0… are CAST to the bound types and named with the bound
// names. ⚠ ORDER IS RE-APPLIED LOCALLY: a pushed result reaches the user through a subquery, which keeps no
// order, so the server's ORDER BY keys come back as k0… and the wrapper sorts by them again (nulls lowest, as
// SQL Server sorts them).
//
// Replaced the hand-written renderer of §4.1–§4.3 on 2026-10-25.
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_lateral.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "fabricator_optimizer.hpp"
#include "fabricator_trace.hpp"
#include "duckdb/common/error_data.hpp"
#include "duckdb/common/printer.hpp"
#include "duckdb/execution/column_binding_resolver.hpp"
#include "duckdb/execution/operator/scan/physical_table_scan.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/optimizer/filter_pushdown.hpp"
#include "duckdb/optimizer/optimizer.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/optimizer/column_binding_replacer.hpp"
#include "duckdb/planner/expression_iterator.hpp"
#include "duckdb/planner/logical_operator_visitor.hpp"
#include "duckdb/planner/expression/bound_aggregate_expression.hpp"
#include "duckdb/planner/expression/bound_function_expression.hpp"
#include "duckdb/planner/expression/bound_window_expression.hpp"
#include "duckdb/planner/expression/bound_columnref_expression.hpp"
#include "duckdb/planner/expression/bound_conjunction_expression.hpp"
#include "duckdb/planner/operator/logical_create_table.hpp"
#include "duckdb/planner/operator/logical_cteref.hpp"
#include "duckdb/planner/operator/logical_materialized_cte.hpp"
#include "duckdb/planner/operator/logical_projection.hpp"
#include "duckdb/planner/operator/logical_delete.hpp"
#include "duckdb/planner/operator/logical_insert.hpp"
#include "duckdb/planner/operator/logical_merge_into.hpp"
#include "duckdb/planner/operator/logical_update.hpp"
#include "duckdb/main/config.hpp"
#include "catalog/fabricator_table_entry.hpp"
#include "duckdb/parser/expression/case_expression.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/comparison_expression.hpp"
#include "duckdb/parser/expression/conjunction_expression.hpp"
#include "duckdb/parser/expression/operator_expression.hpp"
#include "duckdb/parser/query_node/merge_query_node.hpp"
#include "duckdb/parser/statement/merge_into_statement.hpp"
#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/expression/star_expression.hpp"
#include "duckdb/parser/expression/function_expression.hpp"
#include "duckdb/parser/expression/window_expression.hpp"
#include "duckdb/parser/expression/subquery_expression.hpp"
#include "duckdb/parser/parsed_expression_iterator.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/parser/query_node/delete_query_node.hpp"
#include "duckdb/parser/query_node/insert_query_node.hpp"
#include "duckdb/parser/query_node/update_query_node.hpp"
#include "duckdb/parser/statement/insert_statement.hpp"
#include "duckdb/parser/statement/update_statement.hpp"
#include "duckdb/parser/query_node/select_node.hpp"
#include "duckdb/parser/query_node/set_operation_node.hpp"
#include "duckdb/parser/result_modifier.hpp"
#include "duckdb/parser/statement/select_statement.hpp"
#include "duckdb/parser/tableref/basetableref.hpp"
#include "duckdb/parser/tableref/joinref.hpp"
#include "duckdb/parser/tableref/subqueryref.hpp"
#include "duckdb/parser/tableref/table_function_ref.hpp"
#include "duckdb/planner/binder.hpp"
#include "duckdb/planner/expression/bound_cast_expression.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"
#include "duckdb/planner/operator/logical_order.hpp"
#include "duckdb/planner/logical_operator.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
#include "duckdb/planner/subquery/flatten_dependent_join.hpp"
#include "duckdb/planner/subquery/recursive_dependent_join_planner.hpp"
#include "fabricator/arrow_ingest.hpp"
#include "fabricator/clr_host.hpp"
#include "to_substrait.hpp"
#include "substrait/plan.pb.h"
#include <cstdlib>
#include <fstream>
#include <functional>
#include <map>
#include <set>
#include <yyjson.h>

namespace duckdb {

namespace {

void RemoteTrace(ClientContext &context, const string &message) {
	FabricatorRoutingTrace(context, FABRICATOR_LOG_REMOTE, "FABRICATOR_DUMP_REMOTE", message);
}

//! The reason of a refused translation. to_substrait's strict mode wraps every refusal in the same preamble, which
//! would lead every logged reason.
string DeclineReason(const std::exception &ex) {
	auto message = ErrorData(ex).RawMessage();
	static const string preamble = "Strict Mode is set to true, and the following warnings/errors happened.";
	if (StringUtil::StartsWith(message, preamble)) {
		message = message.substr(preamble.size());
		StringUtil::Trim(message);
		message = "no Substrait translation: " + message;
	}
	return message;
}

//! FABRICATOR_DUMP_SUBSTRAIT_PLAN=<file>: the last plan sent to the provider, for the harness that measures the
//! translation against DuckDB (docs/duckdb-2.0-remote-execution.md §3.2).
void DumpPlan(const string &bytes) {
	auto file = std::getenv("FABRICATOR_DUMP_SUBSTRAIT_PLAN");
	if (file && *file) {
		std::ofstream out(file, std::ios::binary);
		out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
	}
}

void CollectCteNames(QueryNode &node, case_insensitive_set_t &names);

void CollectCteNamesInExpression(ParsedExpression &expr, case_insensitive_set_t &names) {
	if (expr.GetExpressionClass() == ExpressionClass::SUBQUERY) {
		CollectCteNames(*expr.Cast<SubqueryExpression>().SubqueryMutable()->node, names);
	}
	ParsedExpressionIterator::EnumerateChildren(expr,
	                                            [&](ParsedExpression &child) { CollectCteNamesInExpression(child, names); });
}

//! Every CTE name defined anywhere in the node — over-approximated on purpose: a name in the set is never qualified.
void CollectCteNames(QueryNode &node, case_insensitive_set_t &names) {
	for (auto &kv : node.cte_map.map) {
		names.insert(kv.first.GetIdentifierName());
		if (kv.second->query_node) {
			CollectCteNames(*kv.second->query_node, names);
		}
	}
	if (node.type == QueryNodeType::SET_OPERATION_NODE) {
		for (auto &child : node.Cast<SetOperationNode>().children) {
			CollectCteNames(*child, names);
		}
	}
	ParsedExpressionIterator::EnumerateQueryNodeChildren(
	    node,
	    [&](unique_ptr<ParsedExpression> &child) {
		    if (child) {
			    CollectCteNamesInExpression(*child, names);
		    }
	    },
	    [&](TableRef &ref) {
		    if (ref.type == TableReferenceType::SUBQUERY) {
			    CollectCteNames(*ref.Cast<SubqueryRef>().subquery->node, names);
		    }
	    });
}

void RestoreCatalogInExpression(ParsedExpression &expr, const Identifier &catalog, const Identifier &default_schema,
                                const case_insensitive_set_t &ctes);

void RestoreCatalog(QueryNode &node, const Identifier &catalog, const Identifier &default_schema,
                    const case_insensitive_set_t &ctes) {
	ParsedExpressionIterator::EnumerateQueryNodeChildren(
	    node,
	    [&](unique_ptr<ParsedExpression> &child) {
		    if (child) {
			    RestoreCatalogInExpression(*child, catalog, default_schema, ctes);
		    }
	    },
	    [&](TableRef &ref) {
		    if (ref.type != TableReferenceType::BASE_TABLE) {
			    return;
		    }
		    auto &base = ref.Cast<BaseTableRef>();
		    auto &name = base.GetQualifiedName();
		    if (!name.Catalog().empty()) {
			    return;
		    }
		    // a CTE reference is a base table too, and must stay unqualified
		    if (name.Schema().empty() && ctes.count(name.Name().GetIdentifierName())) {
			    return;
		    }
		    Identifier schema = name.Schema().empty() ? default_schema : name.Schema();
		    Identifier table = name.Name();
		    base.SetQualifiedName(catalog, schema, table);
	    });
}

void RestoreCatalogInExpression(ParsedExpression &expr, const Identifier &catalog, const Identifier &default_schema,
                                const case_insensitive_set_t &ctes) {
	if (expr.GetExpressionClass() == ExpressionClass::SUBQUERY) {
		RestoreCatalog(*expr.Cast<SubqueryExpression>().SubqueryMutable()->node, catalog, default_schema, ctes);
	}
	ParsedExpressionIterator::EnumerateChildren(
	    expr, [&](ParsedExpression &child) { RestoreCatalogInExpression(child, catalog, default_schema, ctes); });
}

//! The optimizer stripped this catalog's name from every table it pushed; put it back — the INSERT target too,
//! which is a name on the node rather than a table reference.
void RestoreCatalog(QueryNode &node, const Identifier &catalog, const Identifier &default_schema) {
	case_insensitive_set_t ctes;
	CollectCteNames(node, ctes);
	RestoreCatalog(node, catalog, default_schema, ctes);
	if (node.type == QueryNodeType::INSERT_QUERY_NODE) {
		auto &insert = node.Cast<InsertQueryNode>();
		auto &name = insert.qualified_name;
		if (name.Catalog().empty()) {
			insert.SetQualifiedName(catalog, name.Schema().empty() ? default_schema : name.Schema(), name.Name());
		}
	}
}

//! Whether a query reads a table at all (a CTE name counts too — over-approximated, harmless: a false yes only
//! means the plan is bound and then declined).
bool ReadsTable(QueryNode &node) {
	bool found = false;
	ParsedExpressionIterator::EnumerateQueryNodeChildren(
	    node, [&](unique_ptr<ParsedExpression> &) {},
	    [&](TableRef &ref) {
		    if (ref.type == TableReferenceType::BASE_TABLE) {
			    found = true;
		    }
	    });
	return found;
}

//! Whether a RETURNING list names only columns — a plain `*` or column references — which T-SQL's OUTPUT DELETED.<c>
//! can write.
bool ReturnsColumns(const vector<unique_ptr<ParsedExpression>> &returning) {
	for (auto &expr : returning) {
		if (expr->GetExpressionClass() == ExpressionClass::STAR) {
			auto &star = expr->Cast<StarExpression>();
			if (!star.RelationName().empty() || !star.ExcludeList().empty() || !star.ReplaceList().empty() ||
			    !star.RenameList().empty() || star.Expression() || star.IsColumns()) {
				return false;
			}
		} else if (expr->GetExpressionClass() != ExpressionClass::COLUMN_REF) {
			return false;
		}
	}
	return !returning.empty();
}

//! The data-modifying CTE of `node` (DuckDB 2.0: a CTE whose body is a DELETE / UPDATE / INSERT / MERGE), if any.
const CommonTableExpressionInfo *DmlCte(const QueryNode &node) {
	for (auto &kv : node.cte_map.map) {
		auto &body = kv.second->query_node;
		if (body && (body->type == QueryNodeType::DELETE_QUERY_NODE || body->type == QueryNodeType::UPDATE_QUERY_NODE ||
		             body->type == QueryNodeType::INSERT_QUERY_NODE || body->type == QueryNodeType::MERGE_QUERY_NODE)) {
			return kv.second.get();
		}
	}
	return nullptr;
}

//! The moved-rows shape SubstraitMoved writes as one statement: an INSERT whose only CTE is a DELETE … RETURNING of
//! columns. Checked before binding; SubstraitMoved checks the rest and declines.
bool IsMovedRowsInsert(const QueryNode &node) {
	if (node.type != QueryNodeType::INSERT_QUERY_NODE || node.cte_map.map.size() != 1) {
		return false;
	}
	auto cte = DmlCte(node);
	return cte && cte->query_node->type == QueryNodeType::DELETE_QUERY_NODE &&
	       ReturnsColumns(cte->query_node->Cast<DeleteQueryNode>().returning_list);
}

//! A write the provider translated: fabricator_remote_dml runs it on this transaction's own connection when the
//! statement EXECUTES and yields its changed-row count as DuckDB's own DML does (one BIGINT column, CHANGED_ROWS).
unique_ptr<TableRef> RemoteDmlRef(FabricatorCatalog &catalog, const string &sql) {
	vector<unique_ptr<ParsedExpression>> args;
	args.push_back(ConstantExpression::String(catalog.GetName().GetIdentifierName()));
	args.push_back(ConstantExpression::String(sql));
	auto dml = make_uniq<TableFunctionRef>();
	dml->function = make_uniq<FunctionExpression>(Identifier("fabricator_remote_dml"), std::move(args));
	return std::move(dml);
}

//! A pushed query's plan wrapped in a WriteRel on schema.table: `columns` are the table columns the input writes
//! (INSERT: the named columns; UPDATE: the assigned ones; DELETE: none, or — `modified_records` — the deleted columns
//! it outputs). Empty, with `reason`, on failure.
string WrapInWrite(const string &plan_bytes, substrait::WriteRel::WriteOp op, const string &schema,
                   const string &table, const vector<string> &columns, string &reason, bool modified_records = false,
                   const string &enhancement = "", const string &enhancement_value = "") {
	substrait::Plan plan;
	if (!plan.ParseFromString(plan_bytes)) {
		reason = "the pushed plan does not parse";
		return "";
	}
	substrait::RelRoot *root = nullptr;
	for (auto &rel : *plan.mutable_relations()) {
		if (rel.has_root()) {
			root = rel.mutable_root();
		}
	}
	if (!root) {
		reason = "the pushed plan has no root";
		return "";
	}
	auto write_rel = make_uniq<substrait::Rel>();
	auto write = write_rel->mutable_write();
	write->set_op(op);
	write->set_output(modified_records
	                      ? substrait::WriteRel::OutputMode::WriteRel_OutputMode_OUTPUT_MODE_MODIFIED_RECORDS
	                      : substrait::WriteRel::OutputMode::WriteRel_OutputMode_OUTPUT_MODE_NO_OUTPUT);
	write->mutable_named_table()->add_names(schema);
	write->mutable_named_table()->add_names(table);
	if (!enhancement.empty()) {
		// Substrait's way to say "this rel means something the spec does not define": the consumer must understand
		// the enhancement or refuse the plan
		auto any = write->mutable_advanced_extension()->mutable_enhancement();
		any->set_type_url(enhancement);
		any->set_value(enhancement_value);
	}
	auto table_schema = write->mutable_table_schema();
	for (auto &column : columns) {
		table_schema->add_names(column);
	}
	write->set_allocated_input(root->release_input());
	root->set_allocated_input(write_rel.release());
	root->clear_names();
	return plan.SerializeAsString();
}

//! The provider's statement for a write plan (WrapInWrite) — empty, with `reason`, when it declines.
string TranslateWrite(ClientContext &context, FabricatorCatalog &catalog, const string &plan_bytes, string &reason) {
	DumpPlan(plan_bytes);
	string json;
	try {
		FabricatorSetActiveTxn(catalog.GetHandle(), context);
		json = fabricator::RemoteTranslate(catalog.GetHandle(), plan_bytes);
	} catch (std::exception &ex) {
		reason = ErrorData(ex).RawMessage();
		return "";
	}
	auto doc = yyjson_read(json.data(), json.size(), 0);
	if (!doc) {
		reason = "remote_translate returned malformed JSON";
		return "";
	}
	auto obj = yyjson_doc_get_root(doc);
	auto sql_value = yyjson_obj_get(obj, "sql");
	auto writes_value = yyjson_obj_get(obj, "changes_rows");
	auto reason_value = yyjson_obj_get(obj, "reason");
	string sql = yyjson_is_str(sql_value) ? yyjson_get_str(sql_value) : "";
	bool writes = yyjson_is_bool(writes_value) && yyjson_get_bool(writes_value);
	reason = yyjson_is_str(reason_value) ? yyjson_get_str(reason_value) : "the provider declined the write";
	yyjson_doc_free(doc);
	if (sql.empty() || !writes) {
		return "";
	}
	return sql;
}

//! Whether `expr` itself (not its children) gives the same value for the same arguments. now(), current_date or
//! random() would run with the server's clock, time zone and generator — a different answer, and nothing would fail.
//! The T-SQL emitter knows none of them today; this keeps it so when it learns one. `why` receives the reason.
bool ConsistentNode(const Expression &expr, string *why) {
	auto check = [&](FunctionStability stability, const Identifier &name) {
		if (stability == FunctionStability::CONSISTENT) {
			return true;
		}
		if (why) {
			*why = "it calls " + name.GetIdentifierName() + "(), whose value is not fixed by its arguments";
		}
		return false;
	};
	switch (expr.GetExpressionClass()) {
	case ExpressionClass::BOUND_FUNCTION: {
		auto &function = expr.Cast<BoundFunctionExpression>().Function();
		auto catalog = function.GetCatalogName().GetIdentifierName();
		if (!catalog.empty() && !StringUtil::CIEquals(catalog, "system")) {
			return true; // the provider's own function: run by the provider either way, so pushed or not, same answer
		}
		return check(function.GetStability(), function.GetName());
	}
	case ExpressionClass::BOUND_AGGREGATE: {
		auto &function = expr.Cast<BoundAggregateExpression>().Function();
		return check(function.GetStability(), function.GetName());
	}
	case ExpressionClass::BOUND_WINDOW: {
		auto &window = expr.Cast<BoundWindowExpression>();
		if (window.AggregateFunction() &&
		    !check(window.AggregateFunction()->GetStability(), window.AggregateFunction()->GetName())) {
			return false;
		}
		if (window.WindowFunction() &&
		    !check(window.WindowFunction()->GetStability(), window.WindowFunction()->GetName())) {
			return false;
		}
		return true;
	}
	default:
		return true;
	}
}

//! ConsistentNode over every expression of a plan.
bool ConsistentPlan(LogicalOperator &op, string &why) {
	bool consistent = true;
	std::function<void(const Expression &)> visit = [&](const Expression &expr) {
		if (!consistent) {
			return;
		}
		if (!ConsistentNode(expr, &why)) {
			consistent = false;
			return;
		}
		ExpressionIterator::EnumerateChildren(expr, visit);
	};
	LogicalOperatorVisitor::EnumerateExpressions(op, [&](unique_ptr<Expression> *expr) {
		if (*expr) {
			visit(**expr);
		}
	});
	for (auto &child : op.children) {
		if (consistent && !ConsistentPlan(*child, why)) {
			consistent = false;
		}
	}
	return consistent;
}

//! Binds a copy of `node` (this catalog's name restored on its tables) and serializes the plan as Substrait, the
//! root's columns named c0…; false, with `reason`, when anything on the way declines. `require_worth`: decline a plan
//! of scans, filters, projections and sorts only — the table scan already does that much.
bool BindToSubstrait(ClientContext &context, FabricatorCatalog &catalog, const QueryNode &node,
                     const Identifier &default_schema, bool require_worth, string &plan_bytes,
                     vector<LogicalType> &types, vector<Identifier> &names, string &reason) {
	// binding the copy runs the binder, never the remote optimizer, but guard anyway: a nested call declines
	static thread_local int depth = 0;
	if (depth > 0) {
		reason = "re-entered while binding";
		return false;
	}
	auto copy = node.Copy();
	RestoreCatalog(*copy, catalog.GetName(), default_schema);
	depth++;
	try {
		auto binder = Binder::CreateBinder(context);
		auto bound = binder->Bind(*copy);
		auto plan = std::move(bound.plan);
		// what Planner::CreatePlan does after binding (§3.3)
		RecursiveDependentJoinPlanner::Plan(*binder, plan);
		plan = FlattenDependentJoins::DecorrelateIndependent(*binder, std::move(plan));
		// ONE optimizer pass (§3.4): filter pushdown turns a MARK join filtered on its marker (IN / EXISTS) into
		// a SEMI / ANTI join. It adds no table filters: a SQL Server scan declares filter_pushdown = false.
		Optimizer optimizer(*binder, context);
		FilterPushdown filter_pushdown(optimizer);
		plan = filter_pushdown.Rewrite(std::move(plan));
		// A plan of scans, filters, projections and sorts only gains nothing: the table scan already pushes its filters
		// and projection, and keeps the catalog's statistics and types. Push what reduces what travels —
		// aggregates, joins, windows, DISTINCT, LIMIT, set operations.
		std::function<bool(LogicalOperator &)> worth_pushing = [&](LogicalOperator &op) {
			// (a sort alone neither: the result is sorted again locally anyway)
			if (op.type != LogicalOperatorType::LOGICAL_PROJECTION && op.type != LogicalOperatorType::LOGICAL_FILTER &&
			    op.type != LogicalOperatorType::LOGICAL_GET && op.type != LogicalOperatorType::LOGICAL_ORDER_BY) {
				return true;
			}
			for (auto &child : op.children) {
				if (worth_pushing(*child)) {
					return true;
				}
			}
			return false;
		};
		if (require_worth && !worth_pushing(*plan)) {
			depth--;
			reason = "a plain scan keeps the table scan";
			return false;
		}
		if (!ConsistentPlan(*plan, reason)) {
			depth--;
			return false;
		}
		plan->ResolveOperatorTypes();
		ColumnBindingResolver resolver; // column refs to positional refs, which to_substrait reads
		resolver.VisitOperator(*plan);
		plan->ResolveOperatorTypes();
		vector<string> root_names;
		for (idx_t i = 0; i < bound.types.size(); i++) {
			root_names.push_back("c" + std::to_string(i));
		}
		DuckDBToSubstrait to_substrait(context, *plan, true, root_names);
		plan_bytes = to_substrait.SerializeToString();
		DumpPlan(plan_bytes);
		types = bound.types;
		names = bound.names;
		depth--;
	} catch (std::exception &ex) {
		depth--;
		reason = DeclineReason(ex);
		return false;
	}
	return true;
}

//! A query the provider translated: its text, the direction of each ORDER BY key it returns as k0…, and whether it
//! writes (an INSERT … SELECT: fabricator_remote_dml runs it).
struct RemoteQuery {
	string sql;
	vector<bool> descending;
	bool changes_rows = false;
};

//! The provider's query for a plan whose root has `column_count` columns (c0…); false, with `reason`, when it
//! declines.
bool TranslateQuery(ClientContext &context, FabricatorCatalog &catalog, const string &plan_bytes, idx_t column_count,
                    RemoteQuery &query, string &reason) {
	string json;
	try {
		// The provider decides with the transaction in view: a query it would have to run beside this
		// transaction's own uncommitted writes is one it must decline (verify_mars_dynamic §3).
		FabricatorSetActiveTxn(catalog.GetHandle(), context);
		json = fabricator::RemoteTranslate(catalog.GetHandle(), plan_bytes);
	} catch (std::exception &ex) {
		reason = ErrorData(ex).RawMessage();
		return false;
	}
	auto doc = yyjson_read(json.data(), json.size(), 0);
	if (!doc) {
		reason = "remote_translate returned malformed JSON";
		return false;
	}
	auto root = yyjson_doc_get_root(doc);
	auto sql_value = yyjson_obj_get(root, "sql");
	auto columns_value = yyjson_obj_get(root, "columns");
	auto order_value = yyjson_obj_get(root, "order");
	auto reason_value = yyjson_obj_get(root, "reason");
	auto writes_value = yyjson_obj_get(root, "changes_rows");
	query.sql = yyjson_is_str(sql_value) ? yyjson_get_str(sql_value) : "";
	string declined = yyjson_is_str(reason_value) ? yyjson_get_str(reason_value) : "the provider declined the plan";
	int64_t columns = yyjson_is_int(columns_value) ? yyjson_get_sint(columns_value) : -1;
	query.changes_rows = yyjson_is_bool(writes_value) && yyjson_get_bool(writes_value);
	query.descending.clear();
	if (yyjson_is_arr(order_value)) {
		size_t index, count;
		yyjson_val *item;
		yyjson_arr_foreach(order_value, index, count, item) {
			query.descending.push_back(yyjson_get_bool(item));
		}
	}
	yyjson_doc_free(doc);
	if (query.sql.empty()) {
		reason = declined;
		return false;
	}
	if (columns != static_cast<int64_t>(column_count)) {
		reason = "the provider's query has " + std::to_string(columns) + " columns, the plan " +
		         std::to_string(column_count);
		return false;
	}
	if (!query.changes_rows) {
		catalog.RememberRemotePlan(query.sql, plan_bytes);
	}
	return true;
}

//!   SELECT CAST(c0 AS <type>) AS <name>, … FROM fabricator_query('<catalog>', '<query>') __fab_remote
//!     ORDER BY __fab_remote.k0 <dir> <nulls lowest>, …
//! — a translated query read back with DuckDB's types and names.
unique_ptr<SelectNode> RemoteWrapper(FabricatorCatalog &catalog, const RemoteQuery &query,
                                     const vector<LogicalType> &types, const vector<Identifier> &names) {
	// ⚠ Every reference to the server's columns is QUALIFIED with the function's alias, so a user alias can never
	// shadow one (a select list sees earlier items' aliases, ORDER BY binds a select alias first).
	auto server_column = [](const string &name) {
		return make_uniq<ColumnRefExpression>(vector<Identifier> {Identifier("__fab_remote"), Identifier(name)});
	};
	vector<unique_ptr<ParsedExpression>> args;
	args.push_back(ConstantExpression::String(catalog.GetName().GetIdentifierName()));
	args.push_back(ConstantExpression::String(query.sql));
	auto function = make_uniq<TableFunctionRef>();
	function->function = make_uniq<FunctionExpression>(Identifier("fabricator_query"), std::move(args));
	function->alias = Identifier("__fab_remote");
	auto wrapper = make_uniq<SelectNode>();
	for (idx_t i = 0; i < types.size(); i++) {
		auto cast = make_uniq<CastExpression>(types[i], server_column("c" + std::to_string(i)));
		cast->SetAlias(names[i]);
		wrapper->select_list.push_back(std::move(cast));
	}
	if (!query.descending.empty()) {
		// a pushed result reaches the user through a subquery, which keeps no order: sort again, by the keys
		// the server sorted by, with nulls as the lowest values — SQL Server's order
		auto order = make_uniq<OrderModifier>();
		for (idx_t i = 0; i < query.descending.size(); i++) {
			auto desc = query.descending[i];
			order->orders.emplace_back(desc ? OrderType::DESCENDING : OrderType::ASCENDING,
			                           desc ? OrderByNullType::NULLS_LAST : OrderByNullType::NULLS_FIRST,
			                           server_column("k" + std::to_string(i)));
		}
		wrapper->modifiers.push_back(std::move(order));
	}
	wrapper->from_table = std::move(function);
	return wrapper;
}

//! The pushed query as the provider's own, through Substrait (ABI v96; docs/duckdb-2.0-remote-execution.md §3.12):
//!   SELECT CAST(c0 AS <bound type>) AS <bound name>, … FROM fabricator_query('<catalog>', '<query>') __fab_remote
//!     ORDER BY __fab_remote.k0 <dir> <nulls lowest>, …
//! — or, for a write (INSERT … SELECT), fabricator_remote_dml. Null, with `reason`, when anything declines.
unique_ptr<TableRef> SubstraitPushdown(ClientContext &context, FabricatorCatalog &catalog, const QueryNode &node,
                                       const Identifier &default_schema, string &reason) {
	vector<LogicalType> types;
	vector<Identifier> names;
	string plan_bytes;
	if (!BindToSubstrait(context, catalog, node, default_schema, /*require_worth=*/true, plan_bytes, types, names,
	                     reason)) {
		return nullptr;
	}
	// ⚠ DuckDB re-wraps a pushed node as `SELECT * FROM <our ref>` (RemotePushdownOptimizer::FinishPushdown),
	// and a binding cannot hold two columns of one name, so `SELECT a.id, b.id` would come back as id, id_1
	// where DuckDB answers id, id. Such a query runs locally.
	case_insensitive_set_t seen;
	for (auto &name : names) {
		if (!seen.insert(name.GetIdentifierName()).second) {
			reason = "two result columns are named " + name.GetIdentifierName();
			return nullptr;
		}
	}

	RemoteQuery query;
	if (!TranslateQuery(context, catalog, plan_bytes, types.size(), query, reason)) {
		return nullptr;
	}
	RemoteTrace(context, "remote: " + query.sql);
	if (query.changes_rows) {
		return RemoteDmlRef(catalog, query.sql);
	}
	auto statement = make_uniq<SelectStatement>();
	statement->node = RemoteWrapper(catalog, query, types, names);
	return make_uniq<SubqueryRef>(std::move(statement));
}

//! DELETE / UPDATE as ONE provider statement. The statement is rewritten as the SELECT over its target that DuckDB
//! would evaluate — DELETE: `SELECT 1 FROM <target> [, <using>…] WHERE <condition>`; UPDATE: `SELECT CAST(<value> AS
//! <column type>), … FROM <target> WHERE <condition>` (the cast is the conversion DuckDB applies when it assigns) —
//! bound and serialized like any pushed query, and wrapped in a WriteRel DELETE / UPDATE on the target. The provider
//! writes it against the target's own row in the query, so every exactness rule of a pushed SELECT holds for the
//! condition and the new values. Null, with `reason`, when anything declines; the statement then runs locally.
unique_ptr<TableRef> SubstraitDml(ClientContext &context, FabricatorCatalog &catalog, const QueryNode &node,
                                  const Identifier &default_schema, string &reason) {
	if ((node.type == QueryNodeType::DELETE_QUERY_NODE && !node.Cast<DeleteQueryNode>().returning_list.empty()) ||
	    (node.type == QueryNodeType::UPDATE_QUERY_NODE && !node.Cast<UpdateQueryNode>().returning_list.empty())) {
		// the statement would lose its rows: RETURNING keeps the rowid path, which returns them from its own OUTPUT
		reason = "RETURNING keeps the rowid path";
		return nullptr;
	}
	auto copy = node.Copy();
	RestoreCatalog(*copy, catalog.GetName(), default_schema);
	auto select = make_uniq<SelectNode>();
	unique_ptr<TableRef> target;
	vector<string> columns;
	auto op = substrait::WriteRel::WriteOp::WriteRel_WriteOp_WRITE_OP_DELETE;
	if (copy->type == QueryNodeType::DELETE_QUERY_NODE) {
		auto &del = copy->Cast<DeleteQueryNode>();
		target = std::move(del.table);
		select->select_list.push_back(ConstantExpression::Integer(1));
		unique_ptr<TableRef> from = target->Copy();
		for (auto &using_clause : del.using_clauses) {
			auto join = make_uniq<JoinRef>(JoinRefType::CROSS);
			join->left = std::move(from);
			join->right = std::move(using_clause);
			from = std::move(join);
		}
		select->from_table = std::move(from);
		select->where_clause = std::move(del.condition);
	} else {
		auto &upd = copy->Cast<UpdateQueryNode>();
		op = substrait::WriteRel::WriteOp::WriteRel_WriteOp_WRITE_OP_UPDATE;
		target = std::move(upd.table);
		if (!target || target->type != TableReferenceType::BASE_TABLE || !upd.set_info) {
			reason = "the UPDATE target is not a table";
			return nullptr;
		}
		auto &name = target->Cast<BaseTableRef>().GetQualifiedName();
		auto entry = catalog.GetEntry(context, CatalogType::TABLE_ENTRY, name.Schema(), name.Name(),
		                              OnEntryNotFound::RETURN_NULL);
		if (!entry) {
			reason = "the UPDATE target is not found";
			return nullptr;
		}
		auto &table = entry->Cast<TableCatalogEntry>();
		auto &set_info = *upd.set_info;
		for (idx_t i = 0; i < set_info.columns.size(); i++) {
			auto &expr = set_info.expressions[i];
			if (expr->GetExpressionClass() == ExpressionClass::DEFAULT) {
				reason = "SET … = DEFAULT";
				return nullptr;
			}
			if (!table.ColumnExists(set_info.columns[i])) {
				reason = "SET names a column the table does not have"; // the binder reports it
				return nullptr;
			}
			auto &column = table.GetColumn(set_info.columns[i]);
			columns.push_back(column.Name().GetIdentifierName());
			select->select_list.push_back(make_uniq<CastExpression>(column.Type(), std::move(expr)));
		}
		select->from_table = target->Copy();
		select->where_clause = std::move(set_info.condition);
	}
	if (!target || target->type != TableReferenceType::BASE_TABLE) {
		reason = "the target is not a table";
		return nullptr;
	}
	auto &target_name = target->Cast<BaseTableRef>().GetQualifiedName();
	string plan_bytes;
	vector<LogicalType> types;
	vector<Identifier> names;
	if (!BindToSubstrait(context, catalog, *select, default_schema, /*require_worth=*/false, plan_bytes, types, names,
	                     reason)) {
		return nullptr;
	}
	auto bytes = WrapInWrite(plan_bytes, op, target_name.Schema().GetIdentifierName(),
	                         target_name.Name().GetIdentifierName(), columns, reason);
	if (bytes.empty()) {
		return nullptr;
	}
	auto sql = TranslateWrite(context, catalog, bytes, reason);
	if (sql.empty()) {
		return nullptr;
	}
	RemoteTrace(context, "remote: " + sql);
	return RemoteDmlRef(catalog, sql);
}

//! DuckDB 2.0's data-modifying CTE that MOVES rows within this catalog —
//!   WITH moved AS (DELETE FROM src … RETURNING *) INSERT INTO dst SELECT * FROM moved
//! — as ONE provider statement, `DELETE … OUTPUT DELETED.<c…> INTO dst (<d…>) …` on SQL Server: no row leaves the
//! server and the move is atomic in itself. The DELETE is rewritten as SubstraitDml does; its WriteRel outputs the
//! deleted columns the INSERT writes (MODIFIED_RECORDS), wrapped in the INSERT's WriteRel. Only the plain shape: the
//! outer SELECT picks columns of the CTE by position (no filter, grouping or modifier), and every moved column has
//! exactly the type of the column it lands in — OUTPUT INTO converts the T-SQL way, the local path the DuckDB way.
//! Null, with `reason`, when anything declines; the statement then runs locally.
unique_ptr<TableRef> SubstraitMoved(ClientContext &context, FabricatorCatalog &catalog, const QueryNode &node,
                                    const Identifier &default_schema, string &reason) {
	if (!IsMovedRowsInsert(node)) {
		reason = "not the moved-rows shape";
		return nullptr;
	}
	auto copy = node.Copy();
	RestoreCatalog(*copy, catalog.GetName(), default_schema);
	auto &insert = copy->Cast<InsertQueryNode>();
	auto &cte_entry = *insert.cte_map.map.begin();
	auto &cte_name = cte_entry.first.GetIdentifierName();
	if (!cte_entry.second->key_targets.empty()) {
		reason = "a recursive moved CTE";
		return nullptr;
	}
	auto &del = cte_entry.second->query_node->Cast<DeleteQueryNode>();
	if (insert.column_order != InsertColumnOrder::INSERT_BY_POSITION || !insert.select_statement ||
	    insert.on_conflict_info || insert.default_values || !insert.returning_list.empty() || !del.cte_map.map.empty()) {
		reason = "the INSERT is not a plain INSERT … SELECT";
		return nullptr;
	}
	auto &outer_node = *insert.select_statement->node;
	if (outer_node.type != QueryNodeType::SELECT_NODE || !outer_node.modifiers.empty() ||
	    !outer_node.cte_map.map.empty()) {
		reason = "the INSERT's SELECT is not a plain SELECT";
		return nullptr;
	}
	auto &outer = outer_node.Cast<SelectNode>();
	if (outer.where_clause || !outer.groups.group_expressions.empty() || !outer.groups.grouping_sets.empty() ||
	    outer.having || outer.qualify || outer.sample || !outer.from_table ||
	    outer.from_table->type != TableReferenceType::BASE_TABLE) {
		reason = "the INSERT's SELECT does more than pick columns";
		return nullptr;
	}
	auto &from_name = outer.from_table->Cast<BaseTableRef>().GetQualifiedName();
	if (!from_name.Catalog().empty() || !from_name.Schema().empty() ||
	    !StringUtil::CIEquals(from_name.Name().GetIdentifierName(), cte_name)) {
		reason = "the INSERT's SELECT does not read the moved rows alone";
		return nullptr;
	}
	if (!del.table || del.table->type != TableReferenceType::BASE_TABLE) {
		reason = "the DELETE target is not a table";
		return nullptr;
	}
	auto &src_name = del.table->Cast<BaseTableRef>().GetQualifiedName();
	auto &dst_name = insert.qualified_name;
	auto src_entry = catalog.GetEntry(context, CatalogType::TABLE_ENTRY, src_name.Schema(), src_name.Name(),
	                                  OnEntryNotFound::RETURN_NULL);
	auto dst_entry = catalog.GetEntry(context, CatalogType::TABLE_ENTRY, dst_name.Schema(), dst_name.Name(),
	                                  OnEntryNotFound::RETURN_NULL);
	if (!src_entry || !dst_entry) {
		reason = "a table of the move is not found"; // the binder reports it
		return nullptr;
	}
	auto &src = src_entry->Cast<TableCatalogEntry>();
	auto &dst = dst_entry->Cast<TableCatalogEntry>();
	// the CTE's columns: the name it exposes, the source column behind it
	vector<pair<string, const ColumnDefinition *>> moved;
	for (auto &expr : del.returning_list) {
		if (expr->GetExpressionClass() == ExpressionClass::STAR) {
			for (auto &column : src.GetColumns().Logical()) {
				if (column.Generated()) {
					reason = "the moved table has a generated column";
					return nullptr;
				}
				moved.emplace_back(column.Name().GetIdentifierName(), &column);
			}
			continue;
		}
		auto &ref = expr->Cast<ColumnRefExpression>();
		auto &name = ref.GetColumnName();
		if (!src.ColumnExists(name)) {
			reason = "RETURNING names a column the table does not have"; // the binder reports it
			return nullptr;
		}
		auto &column = src.GetColumn(name);
		auto alias = expr->GetAlias().GetIdentifierName();
		moved.emplace_back(alias.empty() ? column.Name().GetIdentifierName() : alias, &column);
	}
	// the outer SELECT picks them by position
	vector<const ColumnDefinition *> picked;
	for (auto &expr : outer.select_list) {
		if (expr->GetExpressionClass() == ExpressionClass::STAR) {
			auto &star = expr->Cast<StarExpression>();
			if (!star.ExcludeList().empty() || !star.ReplaceList().empty() || !star.RenameList().empty() ||
			    star.Expression() || star.IsColumns()) {
				reason = "the INSERT's SELECT transforms the moved columns";
				return nullptr;
			}
			for (auto &m : moved) {
				picked.push_back(m.second);
			}
			continue;
		}
		if (expr->GetExpressionClass() != ExpressionClass::COLUMN_REF) {
			reason = "the INSERT's SELECT computes a value";
			return nullptr;
		}
		auto &name = expr->Cast<ColumnRefExpression>().GetColumnName().GetIdentifierName();
		const ColumnDefinition *found = nullptr;
		for (auto &m : moved) {
			if (StringUtil::CIEquals(m.first, name)) {
				if (found) {
					reason = "the moved rows have two columns named " + name; // the binder reports it
					return nullptr;
				}
				found = m.second;
			}
		}
		if (!found) {
			reason = "the INSERT's SELECT names a column the moved rows do not have";
			return nullptr;
		}
		picked.push_back(found);
	}
	// the columns they land in
	vector<const ColumnDefinition *> landing;
	if (insert.columns.empty()) {
		for (auto &column : dst.GetColumns().Logical()) {
			if (column.Generated()) {
				reason = "the target table has a generated column";
				return nullptr;
			}
			landing.push_back(&column);
		}
	} else {
		for (auto &name : insert.columns) {
			if (!dst.ColumnExists(name)) {
				reason = "the INSERT names a column the table does not have";
				return nullptr;
			}
			landing.push_back(&dst.GetColumn(name));
		}
	}
	if (picked.size() != landing.size() || picked.empty()) {
		reason = "the INSERT writes a different number of columns than it selects";
		return nullptr;
	}
	vector<string> src_columns, dst_columns;
	for (idx_t i = 0; i < picked.size(); i++) {
		if (picked[i]->Type() != landing[i]->Type()) {
			reason = "a moved column changes its type (" + picked[i]->Type().ToString() + " -> " +
			         landing[i]->Type().ToString() + ")";
			return nullptr;
		}
		src_columns.push_back(picked[i]->Name().GetIdentifierName());
		dst_columns.push_back(landing[i]->Name().GetIdentifierName());
	}
	// the DELETE's own query, as SubstraitDml builds it
	auto select = make_uniq<SelectNode>();
	select->select_list.push_back(ConstantExpression::Integer(1));
	unique_ptr<TableRef> from = del.table->Copy();
	for (auto &using_clause : del.using_clauses) {
		auto join = make_uniq<JoinRef>(JoinRefType::CROSS);
		join->left = std::move(from);
		join->right = std::move(using_clause);
		from = std::move(join);
	}
	select->from_table = std::move(from);
	select->where_clause = std::move(del.condition);
	string plan_bytes;
	vector<LogicalType> types;
	vector<Identifier> names;
	if (!BindToSubstrait(context, catalog, *select, default_schema, /*require_worth=*/false, plan_bytes, types, names,
	                     reason)) {
		return nullptr;
	}
	auto bytes = WrapInWrite(plan_bytes, substrait::WriteRel::WriteOp::WriteRel_WriteOp_WRITE_OP_DELETE,
	                         src_name.Schema().GetIdentifierName(), src_name.Name().GetIdentifierName(), src_columns,
	                         reason, /*modified_records=*/true);
	if (bytes.empty()) {
		return nullptr;
	}
	bytes = WrapInWrite(bytes, substrait::WriteRel::WriteOp::WriteRel_WriteOp_WRITE_OP_INSERT,
	                    dst_name.Schema().GetIdentifierName(), dst_name.Name().GetIdentifierName(), dst_columns, reason);
	if (bytes.empty()) {
		return nullptr;
	}
	auto sql = TranslateWrite(context, catalog, bytes, reason);
	if (sql.empty()) {
		return nullptr;
	}
	RemoteTrace(context, "remote: " + sql);
	return RemoteDmlRef(catalog, sql);
}

//! MERGE INTO as ONE provider statement. Substrait has no MERGE, and T-SQL's MERGE takes at most one UPDATE and one
//! DELETE per match class while DuckDB takes any number of clauses, first true one wins. So DuckDB's whole decision
//! is computed in ONE query over target and source, which is translated like any pushed query:
//!   SELECT <target key>, tp, op, u_<col>…, i_<col>…
//!     FROM (SELECT *, 1 AS __fab_t FROM target) t <FULL | RIGHT | INNER> JOIN (SELECT *, 1 AS __fab_s FROM source) s
//!       ON <condition>
//!     WHERE op IS NOT NULL
//! where op is 1 (UPDATE) / 2 (DELETE) / 3 (INSERT) from the first clause whose class and condition hold (NULL for DO NOTHING or none),
//! u_<col> the new value of a column some UPDATE assigns (CAST to the column type, as DuckDB assigns; the old value
//! when this row's clause does not assign it) and i_<col> the value an INSERT writes. The provider wraps it in a
//! fixed-shape MERGE that matches by the target's KEY: `WHEN MATCHED AND op = 1 THEN UPDATE …`, `… = 2 THEN DELETE`,
//! `WHEN NOT MATCHED AND op = 3 THEN INSERT …` — DuckDB's NOT MATCHED BY SOURCE rows are matched rows there (they
//! carry their key). The WriteRel says so with the enhancement `fabricator.merge`; its table_schema names the columns'
//! roles (`k:<key column>`, `tp`, `op`, `u:<column>`, `i:<column>`). Null, with `reason`, when anything declines.
unique_ptr<TableRef> SubstraitMerge(ClientContext &context, FabricatorCatalog &catalog, const QueryNode &node,
                                    const Identifier &default_schema, string &reason) {
	auto copy = node.Copy();
	RestoreCatalog(*copy, catalog.GetName(), default_schema);
	auto &merge = copy->Cast<MergeQueryNode>();
	if (!merge.returning_list.empty() || !merge.cte_map.map.empty()) {
		reason = "MERGE … RETURNING / WITH keeps the rowid path";
		return nullptr;
	}
	if (!merge.target || merge.target->type != TableReferenceType::BASE_TABLE || !merge.source) {
		reason = "the MERGE target is not a table";
		return nullptr;
	}
	auto &target_ref = merge.target->Cast<BaseTableRef>();
	auto &target_name = target_ref.GetQualifiedName();
	auto entry = catalog.GetEntry(context, CatalogType::TABLE_ENTRY, target_name.Schema(), target_name.Name(),
	                              OnEntryNotFound::RETURN_NULL);
	if (!entry) {
		reason = "the MERGE target is not found"; // the binder reports it
		return nullptr;
	}
	auto &table = entry->Cast<FabricatorTableEntry>();
	if (!table.HasRowId() || table.HasVirtualRowId()) {
		reason = "the MERGE target has no key to match by";
		return nullptr;
	}
	string t_alias = target_ref.alias.empty() ? target_name.Name().GetIdentifierName() : target_ref.alias.GetIdentifierName();
	string s_alias;
	if (!merge.source->alias.empty()) {
		s_alias = merge.source->alias.GetIdentifierName();
	} else if (merge.source->type == TableReferenceType::BASE_TABLE) {
		s_alias = merge.source->Cast<BaseTableRef>().GetQualifiedName().Name().GetIdentifierName();
	} else {
		reason = "the MERGE source has no name";
		return nullptr;
	}
	if (StringUtil::CIEquals(t_alias, s_alias)) {
		reason = "target and source share a name";
		return nullptr;
	}
	auto column = [](const string &alias, const string &name) {
		return make_uniq<ColumnRefExpression>(Identifier(name), Identifier(alias));
	};
	auto is_null = [](unique_ptr<ParsedExpression> e, bool null) -> unique_ptr<ParsedExpression> {
		return make_uniq<OperatorExpression>(null ? ExpressionType::OPERATOR_IS_NULL : ExpressionType::OPERATOR_IS_NOT_NULL,
		                                     std::move(e));
	};
	auto both = [](unique_ptr<ParsedExpression> a, unique_ptr<ParsedExpression> b) -> unique_ptr<ParsedExpression> {
		if (!a) {
			return b;
		}
		return make_uniq<ConjunctionExpression>(ExpressionType::CONJUNCTION_AND, std::move(a), std::move(b));
	};
	auto class_guard = [&](MergeActionCondition condition) -> unique_ptr<ParsedExpression> {
		switch (condition) {
		case MergeActionCondition::WHEN_MATCHED:
			return both(is_null(column(t_alias, "__fab_t"), false), is_null(column(s_alias, "__fab_s"), false));
		case MergeActionCondition::WHEN_NOT_MATCHED_BY_TARGET:
			return is_null(column(t_alias, "__fab_t"), true);
		default:
			return is_null(column(s_alias, "__fab_s"), true);
		}
	};
	// the clauses in DuckDB's order of evaluation; the match classes are disjoint, so their order does not matter
	struct Clause {
		MergeActionCondition condition;
		const MergeIntoAction *action;
	};
	vector<Clause> clauses;
	bool by_target = false, by_source = false, matched_delete = false;
	for (auto &kv : merge.actions) {
		for (auto &action : kv.second) {
			if (action->action_type == MergeActionType::MERGE_ERROR) {
				reason = "a THEN ERROR action";
				return nullptr;
			}
			clauses.push_back({kv.first, action.get()});
			if (action->action_type == MergeActionType::MERGE_DO_NOTHING) {
				continue;
			}
			by_target |= kv.first == MergeActionCondition::WHEN_NOT_MATCHED_BY_TARGET;
			by_source |= kv.first == MergeActionCondition::WHEN_NOT_MATCHED_BY_SOURCE;
			matched_delete |= kv.first == MergeActionCondition::WHEN_MATCHED &&
			                  action->action_type == MergeActionType::MERGE_DELETE;
		}
	}
	auto guard = [&](const Clause &clause) {
		auto g = class_guard(clause.condition);
		if (clause.action->condition) {
			g = both(std::move(g), clause.action->condition->Copy());
		}
		return g;
	};
	// op
	auto op = make_uniq<CaseExpression>();
	for (auto &clause : clauses) {
		CaseCheck check;
		check.when_expr = guard(clause);
		switch (clause.action->action_type) {
		case MergeActionType::MERGE_UPDATE:
			check.then_expr = ConstantExpression::Integer(1);
			break;
		case MergeActionType::MERGE_DELETE:
			check.then_expr = ConstantExpression::Integer(2);
			break;
		case MergeActionType::MERGE_INSERT:
			check.then_expr = ConstantExpression::Integer(3);
			break;
		default:
			check.then_expr = ConstantExpression::Null(); // DO NOTHING ends the evaluation too
			break;
		}
		op->CaseChecksMutable().push_back(std::move(check));
	}
	op->ElseMutable() = ConstantExpression::Null();
	// the columns some UPDATE assigns, and the columns the INSERT writes
	vector<const ColumnDefinition *> updated;
	vector<const ColumnDefinition *> inserted;
	bool have_insert = false;
	for (auto &clause : clauses) {
		auto &action = *clause.action;
		if (action.action_type == MergeActionType::MERGE_UPDATE) {
			if (!action.update_info || action.update_info->condition || action.update_info->columns.empty()) {
				reason = "an UPDATE without SET (UPDATE SET *)";
				return nullptr;
			}
			for (idx_t i = 0; i < action.update_info->columns.size(); i++) {
				auto &name = action.update_info->columns[i];
				if (!table.ColumnExists(name)) {
					reason = "SET names a column the table does not have"; // the binder reports it
					return nullptr;
				}
				if (action.update_info->expressions[i]->GetExpressionClass() == ExpressionClass::DEFAULT) {
					reason = "SET … = DEFAULT";
					return nullptr;
				}
				auto &col = table.GetColumn(name);
				if (std::find(updated.begin(), updated.end(), &col) == updated.end()) {
					updated.push_back(&col);
				}
			}
		} else if (action.action_type == MergeActionType::MERGE_INSERT) {
			if (action.default_values || action.column_order != InsertColumnOrder::INSERT_BY_POSITION ||
			    !action.exclude_columns.empty() || action.expressions.empty()) {
				reason = "an INSERT that is not INSERT (columns) VALUES (…)";
				return nullptr;
			}
			vector<const ColumnDefinition *> columns;
			if (action.insert_columns.empty()) {
				for (auto &col : table.GetColumns().Logical()) {
					if (col.Generated()) {
						reason = "the MERGE target has a generated column";
						return nullptr;
					}
					columns.push_back(&col);
				}
			} else {
				for (auto &name : action.insert_columns) {
					if (!table.ColumnExists(name)) {
						reason = "INSERT names a column the table does not have";
						return nullptr;
					}
					columns.push_back(&table.GetColumn(name));
				}
			}
			if (columns.size() != action.expressions.size()) {
				reason = "INSERT writes a different number of columns than values"; // the binder reports it
				return nullptr;
			}
			for (auto &expr : action.expressions) {
				if (expr->GetExpressionClass() == ExpressionClass::DEFAULT) {
					reason = "INSERT … DEFAULT";
					return nullptr;
				}
			}
			if (have_insert && columns != inserted) {
				// one T-SQL INSERT clause: a column one insert names and another omits would get NULL, not its default
				reason = "two INSERT actions write different columns";
				return nullptr;
			}
			inserted = columns;
			have_insert = true;
		}
	}
	auto select = make_uniq<SelectNode>();
	vector<string> roles;
	auto names = table.GetColumns().GetColumnNames();
	for (auto idx : table.RowIdColumnIndices()) {
		select->select_list.push_back(column(t_alias, names[idx]));
		roles.push_back("k:" + names[idx]);
	}
	select->select_list.push_back(column(t_alias, "__fab_t"));
	roles.push_back("tp");
	select->select_list.push_back(op->Copy());
	roles.push_back("op");
	for (auto col : updated) {
		auto value = make_uniq<CaseExpression>();
		for (auto &clause : clauses) {
			CaseCheck check;
			check.when_expr = guard(clause);
			check.then_expr = column(t_alias, col->Name().GetIdentifierName());
			if (clause.action->action_type == MergeActionType::MERGE_UPDATE) {
				auto &info = *clause.action->update_info;
				for (idx_t i = 0; i < info.columns.size(); i++) {
					if (&table.GetColumn(info.columns[i]) == col) {
						check.then_expr = make_uniq<CastExpression>(col->Type(), info.expressions[i]->Copy());
					}
				}
			}
			value->CaseChecksMutable().push_back(std::move(check));
		}
		value->ElseMutable() = column(t_alias, col->Name().GetIdentifierName());
		select->select_list.push_back(std::move(value));
		roles.push_back("u:" + col->Name().GetIdentifierName());
	}
	for (idx_t j = 0; j < inserted.size(); j++) {
		// read only where op = 3, so the last INSERT's value is the ELSE: no untyped NULL, which would reach the
		// server as a cast the translation refuses
		vector<unique_ptr<ParsedExpression>> guards, values;
		for (auto &clause : clauses) {
			if (clause.action->action_type == MergeActionType::MERGE_INSERT) {
				guards.push_back(guard(clause));
				values.push_back(make_uniq<CastExpression>(inserted[j]->Type(), clause.action->expressions[j]->Copy()));
			}
		}
		unique_ptr<ParsedExpression> value = std::move(values.back());
		if (values.size() > 1) {
			auto chosen = make_uniq<CaseExpression>();
			for (idx_t k = 0; k + 1 < values.size(); k++) {
				CaseCheck check;
				check.when_expr = std::move(guards[k]);
				check.then_expr = std::move(values[k]);
				chosen->CaseChecksMutable().push_back(std::move(check));
			}
			chosen->ElseMutable() = std::move(value);
			value = std::move(chosen);
		}
		select->select_list.push_back(std::move(value));
		roles.push_back("i:" + inserted[j]->Name().GetIdentifierName());
	}
	// FROM: target and source, each with a marker column that says the row exists on that side
	auto marked = [](unique_ptr<TableRef> ref, const string &marker, const string &alias) {
		auto inner = make_uniq<SelectNode>();
		inner->select_list.push_back(make_uniq<StarExpression>());
		auto one = ConstantExpression::Integer(1);
		one->SetAlias(Identifier(marker));
		inner->select_list.push_back(std::move(one));
		inner->from_table = std::move(ref);
		auto statement = make_uniq<SelectStatement>();
		statement->node = std::move(inner);
		return make_uniq<SubqueryRef>(std::move(statement), Identifier(alias));
	};
	auto join = make_uniq<JoinRef>(JoinRefType::REGULAR);
	join->type = by_source ? JoinType::OUTER : by_target ? JoinType::RIGHT : JoinType::INNER;
	join->left = marked(std::move(merge.target), "__fab_t", t_alias);
	join->right = marked(std::move(merge.source), "__fab_s", s_alias);
	if (merge.join_condition) {
		join->condition = std::move(merge.join_condition);
	} else if (!merge.using_columns.empty()) {
		unique_ptr<ParsedExpression> condition;
		for (auto &name : merge.using_columns) {
			condition = both(std::move(condition),
			                 make_uniq<ComparisonExpression>(ExpressionType::COMPARE_EQUAL,
			                                                 column(t_alias, name.GetIdentifierName()),
			                                                 column(s_alias, name.GetIdentifierName())));
		}
		join->condition = std::move(condition);
	} else {
		reason = "a MERGE without a join condition";
		return nullptr;
	}
	select->from_table = std::move(join);
	select->where_clause = is_null(std::move(op), false);
	string plan_bytes;
	vector<LogicalType> types;
	vector<Identifier> bound_names;
	if (!BindToSubstrait(context, catalog, *select, default_schema, /*require_worth=*/false, plan_bytes, types,
	                     bound_names, reason)) {
		return nullptr;
	}
	auto bytes = WrapInWrite(plan_bytes, substrait::WriteRel::WriteOp::WriteRel_WriteOp_WRITE_OP_UNSPECIFIED,
	                         target_name.Schema().GetIdentifierName(), target_name.Name().GetIdentifierName(), roles,
	                         reason, false, "fabricator.merge", matched_delete ? "check_duplicates" : "");
	if (bytes.empty()) {
		return nullptr;
	}
	auto sql = TranslateWrite(context, catalog, bytes, reason);
	if (sql.empty()) {
		return nullptr;
	}
	RemoteTrace(context, "remote: " + sql);
	return RemoteDmlRef(catalog, sql);
}

//! Follows each output column of `op` down to the column of the pushed query it is, `c<j>` -> j, through the
//! projections and the ORDER BY the pushdown wraps a query in. False for anything else in between — a filter,
//! a computed expression — and for a scan that is not fabricator_query. ⚠ Called from physical planning, where
//! the column references are already POSITIONS into the child (BoundReferenceExpression), not bindings.
bool MapToRemoteColumns(LogicalOperator &op, vector<int64_t> &out, optional_ptr<LogicalGet> &get_out) {
	auto from_child = [&](const vector<idx_t> &positions) {
		vector<int64_t> child_map;
		if (op.children.size() != 1 || !MapToRemoteColumns(*op.children[0], child_map, get_out)) {
			return false;
		}
		for (auto position : positions) {
			if (position >= child_map.size()) {
				return false;
			}
			out.push_back(child_map[position]);
		}
		return true;
	};
	switch (op.type) {
	case LogicalOperatorType::LOGICAL_GET: {
		auto &get = op.Cast<LogicalGet>();
		if (get.function.GetName() != "fabricator_query") {
			return false;
		}
		auto &ids = get.GetColumnIds();
		auto projected = get.projection_ids.size();
		auto count = projected ? projected : ids.size();
		for (idx_t p = 0; p < count; p++) {
			auto &id = ids[projected ? get.projection_ids[p].GetIndex() : p];
			if (id.IsVirtualColumn()) {
				return false;
			}
			auto name = get.names[id.GetPrimaryIndex()].GetIdentifierName();
			int64_t j = -1;
			if (name.size() > 1 && name[0] == 'c' && name.find_first_not_of("0123456789", 1) == string::npos) {
				j = std::stoll(name.substr(1));
			}
			out.push_back(j);
		}
		get_out = &get;
		return true;
	}
	case LogicalOperatorType::LOGICAL_PROJECTION: {
		vector<idx_t> positions;
		for (auto &expr : op.expressions) {
			const Expression *e = expr.get();
			while (BoundCastExpression::IsCast(*e)) {
				e = &BoundCastExpression::Child(e->Cast<BoundFunctionExpression>());
			}
			if (e->GetExpressionClass() != ExpressionClass::BOUND_REF) {
				return false;
			}
			positions.push_back(e->Cast<BoundReferenceExpression>().Index());
		}
		return from_child(positions);
	}
	case LogicalOperatorType::LOGICAL_ORDER_BY: {
		auto &order = op.Cast<LogicalOrder>();
		vector<idx_t> positions;
		if (order.projection_map.empty()) {
			for (idx_t i = 0; i < op.children[0]->types.size(); i++) {
				positions.push_back(i);
			}
		} else {
			for (auto &index : order.projection_map) {
				positions.push_back(index);
			}
		}
		return from_child(positions);
	}
	default:
		return false;
	}
}

//===--- Subtree pushdown (docs/duckdb-2.0-remote-execution.md §4.10) ---===//
// DuckDB's remote optimizer pushes whole statements only. A part of a statement that reads this catalog alone — a
// subquery or CTE aggregating a SQL Server table, joined with a local one — runs locally over a full scan. Here every
// maximal subtree of the logical plan that reads one such catalog and nothing else, and does more than scan / filter /
// project / sort, is sent to the server as one query and replaced by its result: the same translation the
// whole-statement pushdown uses (to_substrait + remote_translate), read back with fabricator_query and cast to the
// types DuckDB bound. Twice: before DuckDB's optimizers and after them (see the two functions at the end). Anything on
// the way that declines keeps the subtree as it was.

//! The catalog a table scan reads, when the scan may be part of a pushed subtree: a plain fabricator_scan of a catalog
//! with remote_select — no time travel, no table filters, no row id or other virtual column.
//! The fabricator catalog `owner` is, when it takes pushed queries.
optional_ptr<FabricatorCatalog> RemoteCatalog(optional_ptr<Catalog> owner) {
	if (!owner || !FabricatorCatalog::Is(*owner)) {
		return nullptr;
	}
	auto &catalog = owner->Cast<FabricatorCatalog>();
	if (!catalog.Supports(RemoteCapability::EXECUTE_QUERY_NODE)) {
		return nullptr;
	}
	return &catalog;
}

optional_ptr<FabricatorCatalog> PushableScanCatalog(LogicalGet &get) {
	// A scan that takes table filters (filter_pushdown, a SQL Server catalog with dynamic filters) may be a target of a
	// join's runtime filter. Replacing it is safe: the join holds only a shared_ptr to the DynamicTableFilterSet, never
	// to the scan (join_filter_pushdown.hpp), so it fills a set nobody reads. Its STATIC filters are translated back
	// into a FilterRel (to_substrait TransformGet); a runtime filter in the copy declines the part.
	if (!get.children.empty() || !get.bind_data) {
		return nullptr;
	}
	optional_ptr<Catalog> owner;
	if (get.function.GetName() == "fabricator_scan") {
		auto table = get.GetTable();
		if (table) {
			owner = &table->ParentCatalog();
		}
	} else if (get.function.function == ::fabricator::ArrowStreamScan) {
		// a table function of the catalog called with constants (see ResolveTableFunction)
		auto data = dynamic_cast<::fabricator::ArrowStreamBindData *>(get.bind_data.get());
		if (data) {
			owner = data->table_function_catalog;
		}
	}
	auto catalog = RemoteCatalog(owner);
	if (!catalog) {
		return nullptr;
	}
	if (!get.bind_data->Cast<::fabricator::ArrowStreamBindData>().at_unit.empty()) {
		return nullptr;
	}
	for (auto &id : get.GetColumnIds()) {
		if (id.IsRowIdColumn() || id.IsVirtualColumn()) {
			return nullptr;
		}
	}
	return catalog;
}

//! Whether an expression may run on the server: no parameter (a prepared statement binds its values per EXECUTE, after
//! the plan was translated), no subquery, and no aggregate or window function of an attached catalog. A SCALAR function
//! of an attached catalog (a SQL Server UDF) is allowed and recorded in `catalog_functions` as (catalog, name): the
//! subtree must read that same catalog, and the provider decides at translation whether it runs there (a C#-authored
//! function does not). `why`, when given, receives the reason of a refusal.
bool PushableExpression(const Expression &expr, string *why = nullptr,
                        vector<pair<string, string>> *catalog_functions = nullptr) {
	auto builtin = [&](const Identifier &function, const Identifier &catalog) {
		auto name = catalog.GetIdentifierName();
		if (name.empty() || StringUtil::CIEquals(name, "system")) {
			return true;
		}
		if (why) {
			*why = "it calls " + function.GetIdentifierName() + "() of catalog " + name;
		}
		return false;
	};
	switch (expr.GetExpressionClass()) {
	case ExpressionClass::BOUND_PARAMETER:
		if (why) {
			*why = "it uses a prepared-statement parameter";
		}
		return false;
	case ExpressionClass::BOUND_SUBQUERY:
		if (why) {
			*why = "it contains a subquery expression";
		}
		return false;
	case ExpressionClass::BOUND_FUNCTION: {
		auto &function = expr.Cast<BoundFunctionExpression>().Function();
		auto catalog = function.GetCatalogName().GetIdentifierName();
		if (!catalog.empty() && !StringUtil::CIEquals(catalog, "system")) {
			if (!catalog_functions) {
				return builtin(function.GetName(), function.GetCatalogName());
			}
			catalog_functions->emplace_back(catalog, function.GetName().GetIdentifierName());
		}
		break;
	}
	case ExpressionClass::BOUND_AGGREGATE: {
		auto &function = expr.Cast<BoundAggregateExpression>().Function();
		if (!builtin(function.GetName(), function.GetCatalogName())) {
			return false;
		}
		break;
	}
	case ExpressionClass::BOUND_WINDOW: {
		auto &aggregate = expr.Cast<BoundWindowExpression>().AggregateFunction();
		if (aggregate && !builtin(aggregate->GetName(), aggregate->GetCatalogName())) {
			return false;
		}
		break;
	}
	default:
		break;
	}
	// after the catalog check: a function of the catalog is refused for that, whatever its declared stability
	if (!ConsistentNode(expr, why)) {
		return false;
	}
	bool pushable = true;
	ExpressionIterator::EnumerateChildren(expr, [&](const Expression &child) {
		if (pushable && !PushableExpression(child, why, catalog_functions)) {
			pushable = false;
		}
	});
	return pushable;
}

//! Whether an operator may be part of a pushed subtree; `worth`: whether it reduces what travels — a subtree of scans,
//! filters, projections and sorts only is what the table scan already does (it pushes its filter and projection).
bool PushableOperator(LogicalOperator &op, bool &worth, string &why, vector<pair<string, string>> &catalog_functions) {
	switch (op.type) {
	case LogicalOperatorType::LOGICAL_PROJECTION:
	case LogicalOperatorType::LOGICAL_FILTER:
	case LogicalOperatorType::LOGICAL_ORDER_BY:
		worth = false;
		break;
	case LogicalOperatorType::LOGICAL_AGGREGATE_AND_GROUP_BY:
	case LogicalOperatorType::LOGICAL_COMPARISON_JOIN:
	case LogicalOperatorType::LOGICAL_CROSS_PRODUCT:
	case LogicalOperatorType::LOGICAL_WINDOW:
	case LogicalOperatorType::LOGICAL_DISTINCT:
	case LogicalOperatorType::LOGICAL_LIMIT:
	case LogicalOperatorType::LOGICAL_TOP_N:
	case LogicalOperatorType::LOGICAL_UNION:
	case LogicalOperatorType::LOGICAL_EXCEPT:
	case LogicalOperatorType::LOGICAL_INTERSECT:
		worth = true;
		break;
	default:
		return false;
	}
	if (op.children.empty()) {
		return false;
	}
	bool pushable = true;
	LogicalOperatorVisitor::EnumerateExpressions(op, [&](unique_ptr<Expression> *expr) {
		if (pushable && *expr && !PushableExpression(**expr, &why, &catalog_functions)) {
			pushable = false;
		}
	});
	return pushable;
}

//! Whether a plan writes a table of an attached fabricator catalog. Such a plan keeps its reads on the catalog's own
//! scans: the write and the reads beside it are routed together (the same-catalog read+write mark, the pinned
//! connection), which a pushed subtree's fabricator_query would bypass.
bool WritesFabricatorCatalog(LogicalOperator &op) {
	optional_ptr<Catalog> target;
	switch (op.type) {
	case LogicalOperatorType::LOGICAL_INSERT:
		target = op.Cast<LogicalInsert>().table.ParentCatalog();
		break;
	case LogicalOperatorType::LOGICAL_DELETE:
		target = op.Cast<LogicalDelete>().table.ParentCatalog();
		break;
	case LogicalOperatorType::LOGICAL_UPDATE:
		target = op.Cast<LogicalUpdate>().table.ParentCatalog();
		break;
	case LogicalOperatorType::LOGICAL_MERGE_INTO:
		target = op.Cast<LogicalMergeInto>().table.ParentCatalog();
		break;
	case LogicalOperatorType::LOGICAL_CREATE_TABLE:
		target = op.Cast<LogicalCreateTable>().schema.ParentCatalog();
		break;
	default:
		break;
	}
	if (target && FabricatorCatalog::Is(*target)) {
		return true;
	}
	for (auto &child : op.children) {
		if (WritesFabricatorCatalog(*child)) {
			return true;
		}
	}
	return false;
}

struct SubtreeReads {
	optional_ptr<FabricatorCatalog> catalog;
	bool pure = false;
	bool worth = false;
	//! CTEs this subtree reads whose definition is not inside it: sent with a copy of that definition
	std::set<TableIndex> cte_refs;
};

//! A CTE seen by the analysis: how its definition reads, and the definition itself (still in the plan, above).
struct CteDefinition {
	SubtreeReads reads;
	LogicalOperator *plan = nullptr;
};

struct SubtreePushdown {
	ClientContext &context;
	Optimizer &optimizer;
	//! before DuckDB's optimizers: the subtree is translated as the whole-statement pushdown translates a statement
	bool pre;
	//! every replaced subtree's output bindings, and its replacement's, by position
	vector<ColumnBinding> old_bindings;
	vector<ColumnBinding> new_bindings;
	//! each replaced subtree, with the slot it was taken from: put back if the plan cannot take the replacements
	vector<pair<unique_ptr<LogicalOperator> *, unique_ptr<LogicalOperator>>> originals;
	//! (before the optimizers) the tables each conjunct of a WHERE reads — see MayReceiveFilter
	vector<std::set<TableIndex>> filter_conjuncts;
	//! each CTE definition seen so far, by CTE index
	std::map<TableIndex, CteDefinition> cte_definitions;

	//! Whether a subtree can be sent: it reads one catalog alone, gains something, and defines every CTE it reads.
	//! A reference to a CTE defined ABOVE it would make the server read rows DuckDB reads too, in another statement —
	//! two reads no isolation level reconciles. SinkCtes moves a CTE down to its references first, so the common
	//! case (DuckDB hangs a decorrelated subquery's CTE at the top of the statement) does define it inside.
	bool Replaceable(const SubtreeReads &reads) const {
		return reads.pure && reads.worth && reads.catalog && reads.cte_refs.empty();
	}

	//! A decision of this pass. Both passes see most plans, so each message says which one decided.
	void Trace(const string &decision, const string &detail) {
		RemoteTrace(context, decision + (pre ? " (before the optimizers): " : " (after the optimizers): ") + detail);
	}
};

//! What a subtree reads: pure = one catalog and nothing else.

//! The tables each conjunct of every filter in the plan reads.
void CollectFilterConjuncts(LogicalOperator &op, vector<std::set<TableIndex>> &conjuncts) {
	if (op.type == LogicalOperatorType::LOGICAL_FILTER) {
		std::function<void(const Expression &)> split = [&](const Expression &expr) {
			if (expr.GetExpressionType() == ExpressionType::CONJUNCTION_AND) {
				for (auto &child : expr.Cast<BoundConjunctionExpression>().GetChildren()) {
					split(*child);
				}
				return;
			}
			std::set<TableIndex> tables;
			ExpressionIterator::VisitExpression<BoundColumnRefExpression>(
			    expr, [&](const BoundColumnRefExpression &ref) { tables.insert(ref.Binding().table_index); });
			if (!tables.empty()) {
				conjuncts.push_back(std::move(tables));
			}
		};
		for (auto &expr : op.expressions) {
			split(*expr);
		}
	}
	for (auto &child : op.children) {
		CollectFilterConjuncts(*child, conjuncts);
	}
}

//! Before the optimizers, whether a filter above the subtree reads its output alone: DuckDB's filter pushdown would
//! move that condition INTO the subtree (a WHERE on a group key down to the aggregate's scan). Replaced now, the
//! condition stays outside the pushed query — measured: the whole table aggregated on the server, 0.73 s, where the
//! scan with the condition took 0.08 s. Such a subtree is left to the pass after the optimizers, which sees it with
//! the condition inside.
bool MayReceiveFilter(const SubtreePushdown &state, const vector<ColumnBinding> &outputs) {
	std::set<TableIndex> produced;
	for (auto &binding : outputs) {
		produced.insert(binding.table_index);
	}
	for (auto &conjunct : state.filter_conjuncts) {
		bool inside = true;
		for (auto &table : conjunct) {
			if (produced.find(table) == produced.end()) {
				inside = false;
				break;
			}
		}
		if (inside) {
			return true;
		}
	}
	return false;
}

//! Replaces every CTE of the subtree by copies of its definition, one per reference — Substrait has no CTE. A
//! correlated subquery reaches the optimizers as one (DuckDB 2.0 decorrelates into a `__duckdb_delim_<n>` CTE: the
//! outer rows, their distinct correlation keys and plain joins), and so does a WITH … AS MATERIALIZED. The server
//! reads the definition once per reference. A copy keeps the definition's table indices: each one sits under a
//! projection carrying the reference's own index, which is all the operators above it can see.
unique_ptr<LogicalOperator> InlineCtes(ClientContext &context, unique_ptr<LogicalOperator> op,
                                       std::map<TableIndex, LogicalOperator *> &definitions,
                                       std::map<TableIndex, idx_t> &copies) {
	if (op->type == LogicalOperatorType::LOGICAL_MATERIALIZED_CTE) {
		auto cte_index = op->Cast<LogicalMaterializedCTE>().table_index;
		op->children[0] = InlineCtes(context, std::move(op->children[0]), definitions, copies);
		definitions[cte_index] = op->children[0].get();
		auto main = InlineCtes(context, std::move(op->children[1]), definitions, copies);
		definitions.erase(cte_index);
		return main;
	}
	if (op->type == LogicalOperatorType::LOGICAL_CTE_REF) {
		auto &ref = op->Cast<LogicalCTERef>();
		auto definition = definitions.find(ref.cte_index);
		if (definition == definitions.end()) {
			throw NotImplementedException("a CTE read outside its definition");
		}
		copies[ref.cte_index]++;
		auto copy = definition->second->Copy(context);
		copy->ResolveOperatorTypes();
		auto bindings = copy->GetColumnBindings();
		if (bindings.size() != ref.chunk_types.size()) {
			throw NotImplementedException("a CTE reference with another column count than its definition");
		}
		vector<unique_ptr<Expression>> columns;
		for (idx_t i = 0; i < bindings.size(); i++) {
			columns.push_back(make_uniq<BoundColumnRefExpression>(copy->types[i], bindings[i]));
		}
		auto projection = make_uniq<LogicalProjection>(ref.table_index, std::move(columns));
		projection->children.push_back(std::move(copy));
		return std::move(projection);
	}
	for (auto &child : op->children) {
		child = InlineCtes(context, std::move(child), definitions, copies);
	}
	return op;
}

//! How many references to CTE `index` the subtree holds.
idx_t CountCteRefs(LogicalOperator &op, TableIndex index) {
	idx_t count = op.type == LogicalOperatorType::LOGICAL_CTE_REF && op.Cast<LogicalCTERef>().cte_index == index ? 1 : 0;
	for (auto &child : op.children) {
		count += CountCteRefs(*child, index);
	}
	return count;
}

//! Whether every leaf of the subtree is a scan of `catalog` the pushdown could send.
bool ReadsCatalogOnly(LogicalOperator &op, optional_ptr<FabricatorCatalog> &catalog) {
	if (op.children.empty()) {
		if (op.type != LogicalOperatorType::LOGICAL_GET) {
			return false;
		}
		auto scan = PushableScanCatalog(op.Cast<LogicalGet>());
		if (!scan || (catalog && catalog.get() != scan.get())) {
			return false;
		}
		catalog = scan;
		return true;
	}
	for (auto &child : op.children) {
		if (!ReadsCatalogOnly(*child, catalog)) {
			return false;
		}
	}
	return true;
}

//! Moves a CTE whose definition reads one catalog alone down to the smallest subtree holding all of its references.
//! DuckDB hangs the CTE of a decorrelated subquery at the TOP of the statement, above any local data the statement
//! joins; left there, the part reading it could only be sent with a second read of rows DuckDB reads itself.
//! The CTE node's columns are its main side's, so wrapping that subtree keeps every binding above it.
void SinkCtes(unique_ptr<LogicalOperator> &op) {
	for (auto &child : op->children) {
		SinkCtes(child);
	}
	if (op->type != LogicalOperatorType::LOGICAL_MATERIALIZED_CTE) {
		return;
	}
	auto index = op->Cast<LogicalMaterializedCTE>().table_index;
	optional_ptr<FabricatorCatalog> catalog;
	if (!ReadsCatalogOnly(*op->children[0], catalog) || CountCteRefs(*op->children[0], index) != 0) {
		return;
	}
	idx_t total = CountCteRefs(*op->children[1], index);
	if (total == 0) {
		return;
	}
	// descend while one child holds every reference
	auto *target = &op->children[1];
	while (true) {
		unique_ptr<LogicalOperator> *next = nullptr;
		for (auto &child : (*target)->children) {
			if (CountCteRefs(*child, index) == total) {
				next = &child;
				break;
			}
		}
		if (!next) {
			break;
		}
		target = next;
	}
	if (target == &op->children[1]) {
		return; // already as low as it goes
	}
	auto cte = std::move(op);
	auto sunk = std::move(*target);
	auto main = std::move(cte->children[1]);
	cte->children[1] = std::move(sunk);
	*target = std::move(cte);
	op = std::move(main);
}

//! Sends `op` to `catalog`'s server and puts the result in its place; leaves it alone when anything declines.
//! DuckDB's ColumnBindingResolver does not descend below a get, so the input relation of a table function called per
//! row (an APPLY) keeps its column references; it is self-contained, so it is resolved on its own.
void ResolveGetInputs(LogicalOperator &op) {
	for (auto &child : op.children) {
		ResolveGetInputs(*child);
	}
	if (op.type == LogicalOperatorType::LOGICAL_GET) {
		for (auto &child : op.children) {
			ColumnBindingResolver resolver;
			resolver.VisitOperator(*child);
		}
	}
}

void ReplaceSubtree(SubtreePushdown &state, unique_ptr<LogicalOperator> &op, FabricatorCatalog &catalog) {
	op->ResolveOperatorTypes();
	auto old_bindings = op->GetColumnBindings();
	auto types = op->types;
	if (old_bindings.size() != types.size()) {
		state.Trace("local subtree", "its bindings and types disagree");
		return;
	}
	if (state.pre && MayReceiveFilter(state, old_bindings)) {
		state.Trace("local subtree", "a filter above it reads its columns; left for the pass after the optimizers");
		return;
	}
	string plan_bytes;
	bool repeated_reads = false;
	try {
		// translated from a copy: positional references are what to_substrait reads, and the plan must stay intact
		// when the provider declines
		auto copy = op->Copy(state.context);
		if (state.pre) {
			// what BindToSubstrait runs: filter pushdown turns a MARK join filtered on its marker into a SEMI / ANTI
			// join and moves predicates to the scans of the subtree
			FilterPushdown filter_pushdown(state.optimizer);
			copy = filter_pushdown.Rewrite(std::move(copy));
		}
		std::map<TableIndex, LogicalOperator *> definitions;
		std::map<TableIndex, idx_t> copies;
		copy = InlineCtes(state.context, std::move(copy), definitions, copies);
		for (auto &count : copies) {
			repeated_reads = repeated_reads || count.second > 1; // the server reads that definition more than once
		}
		copy->ResolveOperatorTypes();
		ColumnBindingResolver resolver;
		resolver.VisitOperator(*copy);
		ResolveGetInputs(*copy);
		copy->ResolveOperatorTypes();
		vector<string> root_names;
		for (idx_t i = 0; i < types.size(); i++) {
			root_names.push_back("c" + std::to_string(i));
		}
		DuckDBToSubstrait to_substrait(state.context, *copy, true, root_names);
		if (repeated_reads) {
			// DuckDB read the CTE once; the server reads it once per copy, so within one statement the copies may see
			// different rows unless the server reads consistently — the provider decides (`fabricator.repeated_reads`)
			to_substrait.AddPlanOptimization("fabricator.repeated_reads");
		}
		plan_bytes = to_substrait.SerializeToString();
		DumpPlan(plan_bytes);
	} catch (std::exception &ex) {
		state.Trace("local subtree", DeclineReason(ex));
		return;
	}
	string reason;
	RemoteQuery query;
	if (!TranslateQuery(state.context, catalog, plan_bytes, types.size(), query, reason) || query.changes_rows) {
		state.Trace("local subtree", reason);
		return;
	}
	unique_ptr<LogicalOperator> replacement;
	try {
		vector<Identifier> names;
		for (idx_t i = 0; i < types.size(); i++) {
			names.emplace_back("c" + std::to_string(i));
		}
		// bound by a child of the statement's binder, so its table indices are new to the plan
		auto binder = Binder::CreateBinder(state.context, &state.optimizer.binder);
		auto wrapper = RemoteWrapper(catalog, query, types, names);
		auto bound = binder->Bind(*wrapper);
		replacement = std::move(bound.plan);
		replacement->ResolveOperatorTypes();
	} catch (std::exception &ex) {
		state.Trace("local subtree", ErrorData(ex).RawMessage());
		return;
	}
	auto new_bindings = replacement->GetColumnBindings();
	if (new_bindings.size() != old_bindings.size() || replacement->types != types) {
		state.Trace("local subtree", "the pushed query's columns do not match");
		return;
	}
	state.Trace("remote subtree", query.sql);
	state.old_bindings.insert(state.old_bindings.end(), old_bindings.begin(), old_bindings.end());
	state.new_bindings.insert(state.new_bindings.end(), new_bindings.begin(), new_bindings.end());
	state.originals.emplace_back(&op, std::move(op));
	op = std::move(replacement);
}

SubtreeReads AnalyzeSubtree(SubtreePushdown &state, unique_ptr<LogicalOperator> &op) {
	vector<SubtreeReads> children;
	for (auto &child : op->children) {
		children.push_back(AnalyzeSubtree(state, child));
		if (op->type == LogicalOperatorType::LOGICAL_MATERIALIZED_CTE && children.size() == 1) {
			// the definition is analyzed before the part that reads it: a reference is pure when its definition is
			auto &definition = state.cte_definitions[op->Cast<LogicalMaterializedCTE>().table_index];
			definition.reads = children[0];
			definition.plan = op->children[0].get();
		}
	}
	SubtreeReads result;
	if (op->type == LogicalOperatorType::LOGICAL_GET && !op->children.empty()) {
		// a server TVF called per row of a relation of its own catalog: one CROSS APPLY on the server
		vector<string> path;
		optional_ptr<Catalog> owner;
		idx_t arg_count = 0;
		auto catalog = FabricatorLateralApply(op->Cast<LogicalGet>(), path, owner, arg_count) ? RemoteCatalog(owner)
		                                                                                       : nullptr;
		auto &input = children[0];
		if (catalog && input.pure && (!input.catalog || input.catalog.get() == catalog.get())) {
			result.catalog = catalog;
			result.pure = true;
			result.worth = true; // one statement instead of one call per chunk of arguments
			result.cte_refs = input.cte_refs;
			return result;
		}
		if (state.Replaceable(input)) {
			ReplaceSubtree(state, op->children[0], *input.catalog);
		}
		return SubtreeReads();
	}
	if (op->type == LogicalOperatorType::LOGICAL_GET) {
		result.catalog = PushableScanCatalog(op->Cast<LogicalGet>());
		result.pure = result.catalog != nullptr;
		return result;
	}
	if (op->type == LogicalOperatorType::LOGICAL_EMPTY_RESULT || op->type == LogicalOperatorType::LOGICAL_DUMMY_SCAN) {
		result.pure = true; // reads nothing: pure, and of no catalog
		return result;
	}
	if (op->type == LogicalOperatorType::LOGICAL_CTE_REF) {
		auto &ref = op->Cast<LogicalCTERef>();
		auto definition = state.cte_definitions.find(ref.cte_index);
		if (definition != state.cte_definitions.end() && definition->second.reads.pure && !ref.is_recurring) {
			result.catalog = definition->second.reads.catalog;
			result.pure = true;
			result.cte_refs.insert(ref.cte_index);
		}
		return result;
	}
	if (op->type == LogicalOperatorType::LOGICAL_MATERIALIZED_CTE) {
		auto cte_index = op->Cast<LogicalMaterializedCTE>().table_index;
		auto &definition = children[0];
		auto &main = children[1];
		if (definition.pure && main.pure &&
		    (!definition.catalog || !main.catalog || definition.catalog.get() == main.catalog.get())) {
			result.catalog = definition.catalog ? definition.catalog : main.catalog;
			result.pure = true;
			result.worth = definition.worth || main.worth;
			result.cte_refs = definition.cte_refs;
			result.cte_refs.insert(main.cte_refs.begin(), main.cte_refs.end());
			result.cte_refs.erase(cte_index);
			return result;
		}
		for (idx_t i = 0; i < op->children.size(); i++) {
			if (state.Replaceable(children[i])) {
				ReplaceSubtree(state, op->children[i], *children[i].catalog);
			}
		}
		return SubtreeReads();
	}
	bool worth = false;
	string why;
	vector<pair<string, string>> catalog_functions;
	bool operator_ok = PushableOperator(*op, worth, why, catalog_functions);
	// whether everything below reads one catalog alone
	bool children_remote = !children.empty();
	for (auto &child : children) {
		// a child without a catalog (an empty result, a dummy scan) reads nothing and constrains nothing
		if (!child.pure || (result.catalog && child.catalog && result.catalog.get() != child.catalog.get())) {
			children_remote = false;
			break;
		}
		if (child.catalog) {
			result.catalog = child.catalog;
		}
		worth = worth || child.worth;
		result.cte_refs.insert(child.cte_refs.begin(), child.cte_refs.end());
	}
	// A function of the catalog the subtree reads runs on its server (the provider decides at translation); calling it
	// there instead of once per batch is worth it on its own. A function of ANOTHER catalog keeps the operator here.
	for (auto &function : catalog_functions) {
		if (!operator_ok) {
			break;
		}
		if (children_remote && result.catalog &&
		    StringUtil::CIEquals(function.first, result.catalog->GetName().GetIdentifierName())) {
			worth = true;
		} else {
			operator_ok = false;
			why = "it calls " + function.second + "() of catalog " + function.first;
		}
	}
	bool pure = operator_ok && children_remote;
	if (!operator_ok && children_remote && !why.empty()) {
		// the one refusal a reader would not guess: the subtree reads only the server, and still stays here
		state.Trace("local subtree", LogicalOperatorToString(op->type) + " stays local because " + why);
	}
	if (pure) {
		result.pure = true;
		result.worth = worth;
		return result;
	}
	// this operator stays local: each child that reads one catalog alone, and is worth it, goes to its server
	for (idx_t i = 0; i < op->children.size(); i++) {
		if (state.Replaceable(children[i])) {
			ReplaceSubtree(state, op->children[i], *children[i].catalog);
		}
	}
	return SubtreeReads();
}

//! Whether the plan scans a fabricator table at all — every other plan leaves this pass untouched.
bool ReadsFabricatorTable(LogicalOperator &op) {
	if (op.type == LogicalOperatorType::LOGICAL_GET) {
		auto &get = op.Cast<LogicalGet>();
		if (get.function.GetName() == "fabricator_scan" || get.function.function == ::fabricator::ArrowStreamScan) {
			return true;
		}
	}
	for (auto &child : op.children) {
		if (ReadsFabricatorTable(*child)) {
			return true;
		}
	}
	return false;
}

void SubtreePushdownPass(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan, bool pre) {
	if (!ReadsFabricatorTable(*plan)) {
		return;
	}
	Value enabled;
	if (input.context.TryGetCurrentSetting("fabricator_subtree_pushdown", enabled) && !enabled.IsNull() &&
	    !enabled.GetValue<bool>()) {
		return; // the reference oracle of the differential gate
	}
	if (WritesFabricatorCatalog(*plan)) {
		if (pre) {
			RemoteTrace(input.context, "local subtree: the statement writes a table of a fabricator catalog, so its "
			                           "reads keep the catalog's own scans");
		}
		return;
	}
	SubtreePushdown state {input.context, input.optimizer, pre, {}, {}, {}, {}, {}};
	if (pre) {
		SinkCtes(plan);
		CollectFilterConjuncts(*plan, state.filter_conjuncts);
	}
	auto root = AnalyzeSubtree(state, plan);
	if (state.Replaceable(root)) {
		// a whole statement the remote optimizer did not push (a CTE, a name it cannot repeat, …)
		ReplaceSubtree(state, plan, *root.catalog);
	}
	if (state.old_bindings.empty()) {
		return;
	}
	try {
		ColumnBindingReplacer replacer;
		replacer.AddReplacements(state.old_bindings, state.new_bindings);
		replacer.VisitOperator(*plan);
		plan->ResolveOperatorTypes();
	} catch (std::exception &ex) {
		// the plan did not take the replacements: put every subtree back and point the references at it again, so the
		// statement runs as DuckDB planned it rather than failing
		for (auto it = state.originals.rbegin(); it != state.originals.rend(); ++it) {
			*it->first = std::move(it->second);
		}
		ColumnBindingReplacer restore;
		restore.AddReplacements(state.new_bindings, state.old_bindings);
		restore.VisitOperator(*plan);
		plan->ResolveOperatorTypes();
		state.Trace("local subtree", "the plan did not take the replacement (" + ErrorData(ex).RawMessage() +
		                                 "); every subtree runs locally");
	}
}

//! BEFORE DuckDB's optimizers, which may rewrite a subtree into a form that has no translation (an aggregate pushed
//! through a join as EXPORT_STATE / combine, a filtered row_number() as arg_min + UNNEST). A replaced subtree is a leaf
//! to the join order optimizer, so it also stays together.
void SubtreePushdownPreOptimize(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	SubtreePushdownPass(input, plan, true);
}

//! AFTER them: a second chance for a subtree the join order produced, and for one that declined before with its
//! predicates still above it.
void SubtreePushdownOptimize(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	SubtreePushdownPass(input, plan, false);
}

} // namespace

void FabricatorCatalog::RememberRemotePlan(const string &sql, const string &plan) {
	lock_guard<mutex> guard(remote_plans_lock_);
	if (remote_plans_.size() >= 256) {
		remote_plans_.clear();
	}
	remote_plans_[sql] = plan;
}

bool FabricatorCatalog::FindRemotePlan(const string &sql, string &plan) {
	lock_guard<mutex> guard(remote_plans_lock_);
	auto it = remote_plans_.find(sql);
	if (it == remote_plans_.end()) {
		return false;
	}
	plan = it->second;
	return true;
}

string FabricatorCatalog::RemoteInsertForCtas(ClientContext &context, LogicalOperator &source,
                                              PhysicalOperator &planned, const string &schema, const string &table,
                                              const vector<string> &columns, string &reason) {
	// the source must be exactly a query this catalog pushed, its columns in order
	vector<int64_t> map;
	optional_ptr<LogicalGet> get;
	if (!MapToRemoteColumns(source, map, get) || !get) {
		reason = "the source is not a pushed query";
		return "";
	}
	if (map.size() != columns.size()) {
		reason = "the source has another column count";
		return "";
	}
	for (idx_t i = 0; i < map.size(); i++) {
		if (map[i] != static_cast<int64_t>(i)) {
			reason = "the source reorders the pushed query's columns";
			return "";
		}
	}
	// fabricator_query on THIS catalog with no options, as the pushdown writes it. 2.0's LogicalGet keeps no
	// inputs, and by now its bind data has MOVED into the physical scan (the child is planned before the CTAS),
	// so the query is read from there — the one table scan at the bottom of the planned chain.
	optional_ptr<PhysicalOperator> leaf = &planned;
	while (leaf->children.size() == 1) {
		leaf = &leaf->children[0].get();
	}
	if (!leaf->children.empty() || leaf->type != PhysicalOperatorType::TABLE_SCAN) {
		reason = "the planned source is not one scan";
		return "";
	}
	auto &scan = leaf->Cast<PhysicalTableScan>();
	if (scan.function.GetName() != "fabricator_query" || !scan.bind_data) {
		reason = "the planned source is not fabricator_query";
		return "";
	}
	auto &query = scan.bind_data->Cast<::fabricator::ArrowStreamBindData>();
	if (query.query_handle != GetHandle() || query.query_sql.empty()) {
		reason = "the source query is not this catalog's";
		return "";
	}
	string plan_bytes;
	if (!FindRemotePlan(query.query_sql, plan_bytes)) {
		reason = "the source query was not pushed by this catalog";
		return "";
	}
	// its plan, wrapped in a WriteRel INSERT into the new table — the same translation an INSERT … SELECT gets
	auto bytes = WrapInWrite(plan_bytes, substrait::WriteRel::WriteOp::WriteRel_WriteOp_WRITE_OP_INSERT, schema, table,
	                         columns, reason);
	if (bytes.empty()) {
		return "";
	}
	auto sql = TranslateWrite(context, *this, bytes, reason);
	if (sql.empty()) {
		return "";
	}
	RemoteTrace(context, "remote ctas: " + sql);
	return sql;
}

bool FabricatorCatalog::Supports(RemoteCapability capability) const {
	switch (capability) {
	case RemoteCapability::IS_REMOTE:
	case RemoteCapability::EXECUTE_QUERY_NODE:
		return remote_select_;
	default:
		return false;
	}
}

bool FabricatorCatalog::SupportsPushdown(const ParsedExpression &expression) {
	// A call of one of THIS catalog's functions (a SQL Server UDF or aggregate, a catalog-bound macro) keeps the
	// statement local. It does not translate anyway — and the optimizer strips the catalog from the call before
	// RemoteExecute sees it (`db.dbo.f(x)` arrives as `dbo.f(x)`), which no decline can restore with certainty: a
	// bare `f` may as well be a built-in. Measured: `SELECT db.dbo.f(id) FROM db.dbo.t` failed with "Scalar Function
	// with name f does not exist" under remote_pushdown before this veto.
	QualifiedName name;
	if (expression.GetExpressionClass() == ExpressionClass::FUNCTION) {
		name = expression.Cast<FunctionExpression>().GetQualifiedName();
	} else if (expression.GetExpressionClass() == ExpressionClass::WINDOW) {
		name = expression.Cast<WindowExpression>().GetQualifiedName();
	} else {
		return true;
	}
	auto ours = GetName().GetIdentifierName();
	return !(StringUtil::CIEquals(name.Catalog().GetIdentifierName(), ours) ||
	         (name.Catalog().empty() && StringUtil::CIEquals(name.Schema().GetIdentifierName(), ours)));
}

bool FabricatorCatalog::SupportsPushdown(const TableRef &ref) {
	// A plain table, a join of them, or a derived table over them (`FROM (SELECT …) AS x`, the usual MERGE
	// source): whatever it contains is bound and translated with the statement, which declines what does not
	// translate exactly. A catalog-bound table function (a TVF, a stored procedure) has a path of its own; an AT
	// clause is the catalog's time travel.
	// So are the constructs that read nothing — a SELECT without FROM, a VALUES list — when they appear beside this
	// catalog's tables (alone they are never pushed: no catalog is referenced).
	if (ref.type == TableReferenceType::JOIN || ref.type == TableReferenceType::SUBQUERY ||
	    ref.type == TableReferenceType::EMPTY_FROM || ref.type == TableReferenceType::EXPRESSION_LIST) {
		return true;
	}
	return ref.type == TableReferenceType::BASE_TABLE && !ref.Cast<BaseTableRef>().at_clause;
}

bool FabricatorCatalog::SupportsPushdown(const SQLStatement &statement) {
	return false; // DDL keeps the host's own path
}

bool FabricatorCatalog::SupportsPushdown(const QueryNode &node) {
	// SELECT and set operations; RemoteExecute binds them and declines what does not translate.
	// A data-modifying CTE (DuckDB 2.0): only the rows MOVED within this catalog are one statement (SubstraitMoved);
	// every other shape runs locally, where DuckDB evaluates the CTE and the rowid path writes.
	if (DmlCte(node)) {
		return IsMovedRowsInsert(node);
	}
	if (node.type == QueryNodeType::SELECT_NODE || node.type == QueryNodeType::SET_OPERATION_NODE) {
		return true;
	}
	// DELETE / UPDATE as one statement (SubstraitDml). RETURNING keeps the rowid path, which returns the rows from
	// the statement's own OUTPUT; UPDATE … FROM keeps it too — a target row the join matches twice would be updated
	// with whichever match SQL Server takes. A declined one still gets the plan-less shortcut (ABI v93/v94).
	// A DELETE … RETURNING of columns says yes all the same: as the body of a moved-rows CTE it must report this
	// catalog for the INSERT around it to be pushed. Standing alone it reaches RemoteExecute, which declines it.
	if (node.type == QueryNodeType::DELETE_QUERY_NODE) {
		auto &del = node.Cast<DeleteQueryNode>();
		return del.returning_list.empty() || ReturnsColumns(del.returning_list);
	}
	if (node.type == QueryNodeType::UPDATE_QUERY_NODE) {
		auto &update = node.Cast<UpdateQueryNode>();
		return update.returning_list.empty() && !update.from_table;
	}
	// MERGE as one statement (SubstraitMerge); RETURNING keeps the rowid path, which returns each action's rows
	if (node.type == QueryNodeType::MERGE_QUERY_NODE) {
		return node.Cast<MergeQueryNode>().returning_list.empty();
	}
	// INSERT … SELECT reading a table of this catalog: one INSERT … SELECT on the server, no rows travel. Only that
	// shape — an INSERT … VALUES gains nothing and keeps the host's own insert untouched, and RETURNING / ON
	// CONFLICT / DEFAULT VALUES are not translated.
	if (node.type == QueryNodeType::INSERT_QUERY_NODE) {
		auto &insert = node.Cast<InsertQueryNode>();
		return insert.select_statement && insert.returning_list.empty() && !insert.on_conflict_info &&
		       !insert.default_values && ReadsTable(*insert.select_statement->node);
	}
	return false;
}

unique_ptr<TableRef> FabricatorCatalog::RemoteExecute(ClientContext &context, unique_ptr<QueryNode> node) {
	Identifier default_schema(GetDefaultSchema().value_or(Identifier("dbo")));
	string reason;
	const bool is_insert = node->type == QueryNodeType::INSERT_QUERY_NODE;
	const bool is_merge = node->type == QueryNodeType::MERGE_QUERY_NODE;
	const bool is_dml = node->type == QueryNodeType::DELETE_QUERY_NODE || node->type == QueryNodeType::UPDATE_QUERY_NODE;
	Value dml_enabled;
	if ((is_insert || is_dml || is_merge) && context.TryGetCurrentSetting("fabricator_dml_pushdown", dml_enabled) &&
	    !dml_enabled.IsNull() && !dml_enabled.GetValue<bool>()) {
		// the switch exists for the case where streaming the rows through DuckDB is FASTER (a parallel plan the
		// server would not choose for an INSERT … SELECT) — and it is the reference oracle of the differential gate
		reason = "fabricator_dml_pushdown = false";
	} else if (is_insert && DmlCte(*node)) {
		if (auto pushed = SubstraitMoved(context, *this, *node, default_schema, reason)) {
			return pushed;
		}
	} else if (is_dml) {
		if (auto pushed = SubstraitDml(context, *this, *node, default_schema, reason)) {
			return pushed;
		}
	} else if (is_merge) {
		if (auto pushed = SubstraitMerge(context, *this, *node, default_schema, reason)) {
			return pushed;
		}
	} else if (auto pushed = SubstraitPushdown(context, *this, *node, default_schema, reason)) {
		return pushed;
	}
	// DECLINE: the statement as written, this catalog's name restored on every table (the optimizer stripped it),
	// so it binds and runs locally
	RemoteTrace(context, "local: " + reason);
	RestoreCatalog(*node, GetName(), default_schema);
	if (is_insert && node->cte_map.map.empty()) {
		// the rows go through the host's own insert, but the query that produces them can still run on the server.
		// Not when the INSERT defines CTEs: its SELECT alone would bind a CTE name as a table of this catalog.
		auto &insert = node->Cast<InsertQueryNode>();
		string select_reason;
		if (auto pushed = SubstraitPushdown(context, *this, *insert.select_statement->node, default_schema,
		                                    select_reason)) {
			auto select = make_uniq<SelectNode>();
			select->select_list.push_back(make_uniq<StarExpression>());
			select->from_table = std::move(pushed);
			insert.select_statement->node = std::move(select);
		} else {
			RemoteTrace(context, "local select: " + select_reason);
		}
	}
	// A data-modifying CTE binds only at the top level, and the declined statement now sits in a subquery. That rule
	// guards a CTE that a nested query would run more than once or not at all; this subquery IS the statement, run
	// exactly once — so the CTE is marked as the binder marks the ones it generates itself (trigger expansion).
	for (auto &cte : node->cte_map.map) {
		cte.second->is_trigger_generated = true;
	}
	auto declined = make_uniq<SelectStatement>();
	declined->node = std::move(node);
	return make_uniq<SubqueryRef>(std::move(declined));
}

//! A table function of an attached catalog called with constants, for to_substrait (its table_function_resolver).
static bool ResolveTableFunction(LogicalGet &get, vector<string> &path, vector<Value> &args) {
	if (get.function.function != ::fabricator::ArrowStreamScan || !get.bind_data || !get.children.empty()) {
		return false;
	}
	auto data = dynamic_cast<::fabricator::ArrowStreamBindData *>(get.bind_data.get());
	if (!data || data->table_function_path.empty()) {
		return false;
	}
	path = data->table_function_path;
	args = data->table_function_args;
	return true;
}

void RegisterFabricatorSubtreePushdown(DBConfig &config) {
	DuckDBToSubstrait::table_function_resolver = ResolveTableFunction;
	DuckDBToSubstrait::apply_resolver = [](LogicalGet &get, vector<string> &path, idx_t &arg_count) {
		optional_ptr<Catalog> catalog;
		return FabricatorLateralApply(get, path, catalog, arg_count);
	};
	OptimizerExtension extension;
	extension.pre_optimize_function = SubtreePushdownPreOptimize;
	extension.optimize_function = SubtreePushdownOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

} // namespace duckdb
