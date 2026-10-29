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
#include "catalog/fabricator_txn_util.hpp"
#include "duckdb/common/error_data.hpp"
#include "duckdb/common/printer.hpp"
#include "duckdb/execution/column_binding_resolver.hpp"
#include "duckdb/execution/operator/scan/physical_table_scan.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/optimizer/filter_pushdown.hpp"
#include "duckdb/optimizer/optimizer.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/expression/star_expression.hpp"
#include "duckdb/parser/expression/function_expression.hpp"
#include "duckdb/parser/expression/subquery_expression.hpp"
#include "duckdb/parser/parsed_expression_iterator.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/parser/query_node/delete_query_node.hpp"
#include "duckdb/parser/query_node/insert_query_node.hpp"
#include "duckdb/parser/query_node/update_query_node.hpp"
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
#include <yyjson.h>

namespace duckdb {

namespace {

void RemoteTrace(const string &message) {
	static const bool enabled = [] {
		auto v = std::getenv("FABRICATOR_DUMP_REMOTE");
		return v && *v && string(v) != "0";
	}();
	if (enabled) {
		Printer::Print("[fabricator remote] " + message);
	}
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
//! (INSERT: the named columns; UPDATE: the assigned ones; DELETE: none). Empty, with `reason`, on failure.
string WrapInWrite(const string &plan_bytes, substrait::WriteRel::WriteOp op, const string &schema,
                   const string &table, const vector<string> &columns, string &reason) {
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
	write->set_output(substrait::WriteRel::OutputMode::WriteRel_OutputMode_OUTPUT_MODE_NO_OUTPUT);
	write->mutable_named_table()->add_names(schema);
	write->mutable_named_table()->add_names(table);
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
		reason = ErrorData(ex).RawMessage();
		return false;
	}
	return true;
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

	string json;
	try {
		// The provider decides with the transaction in view: a query it would have to run beside this
		// transaction's own uncommitted writes is one it must decline (verify_mars_dynamic §3).
		FabricatorSetActiveTxn(catalog.GetHandle(), context);
		json = fabricator::RemoteTranslate(catalog.GetHandle(), plan_bytes);
	} catch (std::exception &ex) {
		reason = ErrorData(ex).RawMessage();
		return nullptr;
	}
	auto doc = yyjson_read(json.data(), json.size(), 0);
	if (!doc) {
		reason = "remote_translate returned malformed JSON";
		return nullptr;
	}
	auto root = yyjson_doc_get_root(doc);
	auto sql_value = yyjson_obj_get(root, "sql");
	auto columns_value = yyjson_obj_get(root, "columns");
	auto order_value = yyjson_obj_get(root, "order");
	auto reason_value = yyjson_obj_get(root, "reason");
	auto writes_value = yyjson_obj_get(root, "changes_rows");
	string sql = yyjson_is_str(sql_value) ? yyjson_get_str(sql_value) : "";
	string declined = yyjson_is_str(reason_value) ? yyjson_get_str(reason_value) : "the provider declined the plan";
	int64_t columns = yyjson_is_int(columns_value) ? yyjson_get_sint(columns_value) : -1;
	bool changes_rows = yyjson_is_bool(writes_value) && yyjson_get_bool(writes_value);
	vector<bool> descending;
	if (yyjson_is_arr(order_value)) {
		size_t index, count;
		yyjson_val *item;
		yyjson_arr_foreach(order_value, index, count, item) {
			descending.push_back(yyjson_get_bool(item));
		}
	}
	yyjson_doc_free(doc);
	if (sql.empty()) {
		reason = declined;
		return nullptr;
	}
	if (columns != static_cast<int64_t>(types.size())) {
		reason = "the provider's query has " + std::to_string(columns) + " columns, the plan " +
		         std::to_string(types.size());
		return nullptr;
	}
	RemoteTrace("remote: " + sql);
	if (!changes_rows) {
		catalog.RememberRemotePlan(sql, plan_bytes);
	}

	if (changes_rows) {
		return RemoteDmlRef(catalog, sql);
	}

	// ⚠ Every reference to the server's columns is QUALIFIED with the function's alias, so a user alias can never
	// shadow one (a select list sees earlier items' aliases, ORDER BY binds a select alias first).
	auto server_column = [](const string &name) {
		return make_uniq<ColumnRefExpression>(vector<Identifier> {Identifier("__fab_remote"), Identifier(name)});
	};
	vector<unique_ptr<ParsedExpression>> args;
	args.push_back(ConstantExpression::String(catalog.GetName().GetIdentifierName()));
	args.push_back(ConstantExpression::String(sql));
	auto function = make_uniq<TableFunctionRef>();
	function->function = make_uniq<FunctionExpression>(Identifier("fabricator_query"), std::move(args));
	function->alias = Identifier("__fab_remote");
	auto wrapper = make_uniq<SelectNode>();
	for (idx_t i = 0; i < types.size(); i++) {
		auto cast = make_uniq<CastExpression>(types[i], server_column("c" + std::to_string(i)));
		cast->SetAlias(names[i]);
		wrapper->select_list.push_back(std::move(cast));
	}
	if (!descending.empty()) {
		// a pushed result reaches the user through a subquery, which keeps no order: sort again, by the keys
		// the server sorted by, with nulls as the lowest values — SQL Server's order
		auto order = make_uniq<OrderModifier>();
		for (idx_t i = 0; i < descending.size(); i++) {
			order->orders.emplace_back(descending[i] ? OrderType::DESCENDING : OrderType::ASCENDING,
			                           descending[i] ? OrderByNullType::NULLS_LAST : OrderByNullType::NULLS_FIRST,
			                           server_column("k" + std::to_string(i)));
		}
		wrapper->modifiers.push_back(std::move(order));
	}
	wrapper->from_table = std::move(function);
	auto statement = make_uniq<SelectStatement>();
	statement->node = std::move(wrapper);
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
	RemoteTrace("remote: " + sql);
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
	RemoteTrace("remote ctas: " + sql);
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

bool FabricatorCatalog::SupportsPushdown(const TableRef &ref) {
	// A plain table or a join of them. A catalog-bound table function (a TVF, a stored procedure) has a path of
	// its own; an AT clause is the catalog's time travel.
	if (ref.type == TableReferenceType::JOIN) {
		return true;
	}
	return ref.type == TableReferenceType::BASE_TABLE && !ref.Cast<BaseTableRef>().at_clause;
}

bool FabricatorCatalog::SupportsPushdown(const SQLStatement &statement) {
	return false; // DDL keeps the host's own path
}

bool FabricatorCatalog::SupportsPushdown(const QueryNode &node) {
	// SELECT and set operations; RemoteExecute binds them and declines what does not translate. MERGE keeps the
	// rowid path (Substrait has no MERGE).
	if (node.type == QueryNodeType::SELECT_NODE || node.type == QueryNodeType::SET_OPERATION_NODE) {
		return true;
	}
	// DELETE / UPDATE as one statement (SubstraitDml). RETURNING keeps the rowid path, which returns the rows from
	// the statement's own OUTPUT; UPDATE … FROM keeps it too — a target row the join matches twice would be updated
	// with whichever match SQL Server takes. A declined one still gets the plan-less shortcut (ABI v93/v94).
	if (node.type == QueryNodeType::DELETE_QUERY_NODE) {
		return node.Cast<DeleteQueryNode>().returning_list.empty();
	}
	if (node.type == QueryNodeType::UPDATE_QUERY_NODE) {
		auto &update = node.Cast<UpdateQueryNode>();
		return update.returning_list.empty() && !update.from_table;
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
	const bool is_dml = node->type == QueryNodeType::DELETE_QUERY_NODE || node->type == QueryNodeType::UPDATE_QUERY_NODE;
	Value dml_enabled;
	if ((is_insert || is_dml) && context.TryGetCurrentSetting("fabricator_dml_pushdown", dml_enabled) &&
	    !dml_enabled.IsNull() && !dml_enabled.GetValue<bool>()) {
		// the switch exists for the case where streaming the rows through DuckDB is FASTER (a parallel plan the
		// server would not choose for an INSERT … SELECT) — and it is the reference oracle of the differential gate
		reason = "fabricator_dml_pushdown = false";
	} else if (is_dml) {
		if (auto pushed = SubstraitDml(context, *this, *node, default_schema, reason)) {
			return pushed;
		}
	} else if (auto pushed = SubstraitPushdown(context, *this, *node, default_schema, reason)) {
		return pushed;
	}
	// DECLINE: the statement as written, this catalog's name restored on every table (the optimizer stripped it),
	// so it binds and runs locally
	RemoteTrace("local: " + reason);
	RestoreCatalog(*node, GetName(), default_schema);
	if (is_insert) {
		// the rows go through the host's own insert, but the query that produces them can still run on the server
		auto &insert = node->Cast<InsertQueryNode>();
		string select_reason;
		if (auto pushed = SubstraitPushdown(context, *this, *insert.select_statement->node, default_schema,
		                                    select_reason)) {
			auto select = make_uniq<SelectNode>();
			select->select_list.push_back(make_uniq<StarExpression>());
			select->from_table = std::move(pushed);
			insert.select_statement->node = std::move(select);
		} else {
			RemoteTrace("local select: " + select_reason);
		}
	}
	auto declined = make_uniq<SelectStatement>();
	declined->node = std::move(node);
	return make_uniq<SubqueryRef>(std::move(declined));
}

} // namespace duckdb
