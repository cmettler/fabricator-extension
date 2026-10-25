//===----------------------------------------------------------------------===//
// fabricator_remote_select.cpp
//
// DuckDB 2.0's remote pushdown optimizer, SELECT only, for a provider that declares `remote_select`
// (SQL Server with the `remote_pushdown` ATTACH option). The optimizer runs BEFORE binding and hands us a
// parsed SelectNode; we either run it on the server as one T-SQL statement or hand it back to run locally.
//
// THE RULE IS THE ONE THE DML SHORTCUT FOLLOWS (docs/duckdb-2.0-remote-execution.md §2.1): render EXACTLY or
// do not push. Nothing re-checks what the server returns, so a shape whose T-SQL meaning can differ from
// DuckDB's — collation, padding, integer division, float summation order, NULL ordering, result types — is
// refused, never approximated. What is admitted (§4.1–§4.3):
//   - FROM: one base table of this catalog, or INNER / LEFT joins of them with an ON condition; no AT clause;
//   - values: columns, integer `+ - *` over SMALLINT / INTEGER / BIGINT columns and constants, and
//     count(*), count([DISTINCT] col), min / max / integer-or-decimal sum over a column or such arithmetic;
//   - WHERE, join ON and HAVING: =, <>, <, <=, >, >= between a value (or, in HAVING, an aggregate) and a
//     constant, or between two values of the same family, AND / OR / NOT, IS [NOT] NULL. A constant must
//     convert EXACTLY to the other side's type;
//   - strings: = and <> only, GROUP BY / DISTINCT and count(DISTINCT) — never <, >, min, max or ORDER BY;
//   - GROUP BY plain columns, DISTINCT (rendered as GROUP BY); a plain projection only with a join or DISTINCT;
//   - ORDER BY positions, select aliases and values of exact types; LIMIT / OFFSET constants;
//   - no QUALIFY, window, DISTINCT ON, CTE, subquery, SEMI/ANTI/FULL/ASOF/NATURAL/USING join.
//
// ⚠ TWO HALVES, because the optimizer's veto hooks get no ClientContext. SupportsPushdown(QueryNode) checks
// the SHAPE; RemoteExecute (which has a context) resolves the tables, types every value and constant, and
// renders. A shape whose TYPES do not qualify is DECLINED there by returning the node itself, re-qualified
// with this catalog's name, so it binds and runs locally exactly as written.
//
// ⚠ TYPES AND NAMES MUST BE DUCKDB'S. T-SQL's COUNT is INT and an integer SUM keeps the column's type and
// overflows; the result of the server statement is therefore wrapped in a projection that CASTs every column
// to the type DuckDB's own binder would give it and names it the way DuckDB would.
//
// ⚠ ORDER IS RE-APPLIED LOCALLY. The server applies ORDER BY with LIMIT/OFFSET (so only the needed rows
// travel), but a pushed result reaches the user through a subquery, which carries no ordering guarantee. So
// the sort keys come back as hidden columns and the wrapper sorts by them again — over at most LIMIT rows.
//
// ⚠ STRINGS ARE COMPARED AS `(s + N'|') COLLATE Latin1_General_100_BIN2`. The forced binary collation removes
// case and accent folding whatever the column's own collation is; the appended sentinel defeats T-SQL's ANSI
// padding, under which 'a' = 'a ' even in a binary collation. Two strings are then equal on the server exactly
// when they are equal in DuckDB. ORDERING cannot be made exact this way — padding still decides 'a' against
// 'a' + CHAR(1), and UTF-16 code-unit order is not code-point order beyond U+FFFF — so strings never sort,
// compare with < / > or feed min / max here. A grouped string comes back WITH the sentinel (T-SQL must select
// the GROUP BY expression itself) and the wrapper strips it.
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_catalog.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/common/printer.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/execution/expression_executor.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/expression/comparison_expression.hpp"
#include "duckdb/parser/expression/conjunction_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/expression/function_expression.hpp"
#include "duckdb/parser/expression/operator_expression.hpp"
#include "duckdb/parser/query_node/select_node.hpp"
#include "duckdb/parser/result_modifier.hpp"
#include "duckdb/parser/statement/select_statement.hpp"
#include "duckdb/parser/tableref/basetableref.hpp"
#include "duckdb/parser/tableref/joinref.hpp"
#include "duckdb/parser/tableref/subqueryref.hpp"
#include "duckdb/parser/tableref/table_function_ref.hpp"
#include "duckdb/planner/binder.hpp"
#include "duckdb/planner/expression_binder/constant_binder.hpp"
#include <cstdlib>

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

string Bracket(const string &name) {
	return "[" + StringUtil::Replace(name, "]", "]]") + "]";
}

bool IsComparison(ExpressionType type) {
	switch (type) {
	case ExpressionType::COMPARE_EQUAL:
	case ExpressionType::COMPARE_NOTEQUAL:
	case ExpressionType::COMPARE_LESSTHAN:
	case ExpressionType::COMPARE_LESSTHANOREQUALTO:
	case ExpressionType::COMPARE_GREATERTHAN:
	case ExpressionType::COMPARE_GREATERTHANOREQUALTO:
		return true;
	default:
		return false;
	}
}

string ComparisonSql(ExpressionType type) {
	switch (type) {
	case ExpressionType::COMPARE_EQUAL:
		return "=";
	case ExpressionType::COMPARE_NOTEQUAL:
		return "<>";
	case ExpressionType::COMPARE_LESSTHAN:
		return "<";
	case ExpressionType::COMPARE_LESSTHANOREQUALTO:
		return "<=";
	case ExpressionType::COMPARE_GREATERTHAN:
		return ">";
	default:
		return ">=";
	}
}

// The types whose comparisons, grouping, ordering and min/max mean the same thing on both engines. Floats are
// out (NaN, summation order), as are timestamps (precision and time zones differ by column type) and booleans
// (BIT). Strings have their own, narrower rules (see the file header).
bool ExactType(const LogicalType &type) {
	switch (type.id()) {
	case LogicalTypeId::TINYINT:
	case LogicalTypeId::UTINYINT:
	case LogicalTypeId::SMALLINT:
	case LogicalTypeId::INTEGER:
	case LogicalTypeId::BIGINT:
	case LogicalTypeId::DECIMAL:
	case LogicalTypeId::DATE:
		return true;
	default:
		return false;
	}
}

//! An exact type, or an aggregate result type (an integer sum is HUGEINT) — what may be compared and sorted.
bool ComparableType(const LogicalType &type) {
	return ExactType(type) || type.id() == LogicalTypeId::HUGEINT;
}

bool IntegerType(const LogicalType &type) {
	switch (type.id()) {
	case LogicalTypeId::TINYINT:
	case LogicalTypeId::UTINYINT:
	case LogicalTypeId::SMALLINT:
	case LogicalTypeId::INTEGER:
	case LogicalTypeId::BIGINT:
		return true;
	default:
		return false;
	}
}

//! The operands integer arithmetic accepts, and its width order. DuckDB keeps the WIDEST column operand's type
//! (INTEGER + INTEGER is INTEGER, SMALLINT + 1 is SMALLINT — measured); unsigned and TINYINT are left out.
int ArithmeticRank(const LogicalType &type) {
	switch (type.id()) {
	case LogicalTypeId::SMALLINT:
		return 1;
	case LogicalTypeId::INTEGER:
		return 2;
	case LogicalTypeId::BIGINT:
		return 3;
	default:
		return 0;
	}
}

//! A plain VARCHAR — one carrying a DuckDB collation compares differently and stays local.
bool PlainStringType(const LogicalType &type) {
	return type.id() == LogicalTypeId::VARCHAR && StringType::GetCollation(type).empty();
}

// count(col) counts non-NULL values, which no collation or precision rule can change — but a few legacy
// SQL Server types cannot be aggregated at all, so only plain scalar types are admitted.
bool CountableType(const LogicalType &type) {
	return !type.IsNested() && type.id() != LogicalTypeId::BLOB;
}

string AggregateName(const FunctionExpression &func) {
	return StringUtil::Lower(func.FunctionName().GetIdentifierName());
}

bool IsAggregate(const ParsedExpression &expr) {
	if (expr.GetExpressionClass() != ExpressionClass::FUNCTION) {
		return false;
	}
	auto name = AggregateName(expr.Cast<FunctionExpression>());
	return name == "count_star" || name == "count" || name == "min" || name == "max" || name == "sum";
}

bool IsArithmetic(const ParsedExpression &expr) {
	if (expr.GetExpressionClass() != ExpressionClass::FUNCTION) {
		return false;
	}
	auto &func = expr.Cast<FunctionExpression>();
	auto name = func.FunctionName().GetIdentifierName();
	auto args = func.GetArguments().size();
	return (name == "+" || name == "*" || name == "-") && (args == 2 || (args == 1 && name == "-"));
}

//! One table of the FROM clause.
struct Scope {
	//! How the query refers to it: its alias, else its table name.
	Identifier name;
	TableCatalogEntry *table = nullptr;
	//! Its T-SQL alias, [t0], [t1], …
	string sql;
};

//! A column of a scope.
struct ColumnId {
	idx_t scope;
	idx_t column;
	bool operator==(const ColumnId &o) const {
		return scope == o.scope && column == o.column;
	}
};

//! A rendered value: its T-SQL and the type DuckDB would give it. `keyed` = the T-SQL is already a string's
//! comparison key, sentinel included (a grouped string), so an output of it must have the sentinel stripped.
struct Rendered {
	string sql;
	LogicalType type;
	bool keyed = false;
};

//! A sort key: rendered, with the direction and NULL placement DuckDB resolves for it.
struct SortKey {
	Rendered value;
	OrderType order;
	OrderByNullType nulls;
};

struct Output {
	Identifier name;
	LogicalType type;
	bool keyed;
};

//! What RemoteExecute builds from a successful render.
struct RemotePlan {
	string sql;
	vector<Output> outputs;
	vector<SortKey> keys;
};

class RemoteSelect {
public:
	explicit RemoteSelect(const SelectNode &node) : node(node) {
	}

	//! The SHAPE check — no types, no catalog lookups (the veto hook has no context).
	bool Structural(string &reason) const {
		if (!node.cte_map.map.empty()) {
			return Fail(reason, "a CTE");
		}
		if (node.qualify || node.sample) {
			return Fail(reason, "QUALIFY / SAMPLE");
		}
		if (node.aggregate_handling != AggregateHandling::STANDARD_HANDLING) {
			return Fail(reason, "GROUP BY ALL");
		}
		if (node.groups.grouping_sets.size() > 1) {
			return Fail(reason, "GROUPING SETS / ROLLUP / CUBE");
		}
		bool has_join = false;
		if (!node.from_table || !FromShape(*node.from_table, has_join)) {
			return Fail(reason, "a FROM clause that is not base tables of this catalog joined INNER / LEFT ON …");
		}
		bool distinct = false;
		bool seen_order = false, seen_limit = false;
		for (auto &modifier : node.modifiers) {
			switch (modifier->type) {
			case ResultModifierType::DISTINCT_MODIFIER:
				if (distinct || !modifier->Cast<DistinctModifier>().distinct_on_targets.empty()) {
					return Fail(reason, "DISTINCT ON");
				}
				distinct = true;
				break;
			case ResultModifierType::ORDER_MODIFIER: {
				if (seen_order) {
					return Fail(reason, "two ORDER BY clauses");
				}
				seen_order = true;
				for (auto &order : modifier->Cast<OrderModifier>().orders) {
					auto &expr = *order.expression;
					if (!ValueShape(expr, true) && !IsConstantShape(expr)) {
						return Fail(reason, "an ORDER BY expression");
					}
				}
				break;
			}
			case ResultModifierType::LIMIT_MODIFIER: {
				if (seen_limit) {
					return Fail(reason, "two LIMIT clauses");
				}
				seen_limit = true;
				auto &limit = modifier->Cast<LimitModifier>();
				if (limit.limit_type != LimitValueType::ROW_COUNT || (limit.limit && !IsConstantShape(*limit.limit)) ||
				    (limit.offset && !IsConstantShape(*limit.offset))) {
					return Fail(reason, "a LIMIT that is not a constant row count");
				}
				break;
			}
			default:
				return Fail(reason, "a result modifier");
			}
		}
		bool any_aggregate = false;
		for (auto &item : node.select_list) {
			if (!ValueShape(*item, true)) {
				return Fail(reason, "a select item that is not a column, integer arithmetic or an aggregate");
			}
			any_aggregate = any_aggregate || IsAggregate(*item);
			if (distinct && !IsColumn(*item)) {
				return Fail(reason, "DISTINCT over something other than columns");
			}
		}
		if (distinct && (!node.groups.group_expressions.empty() || node.having)) {
			return Fail(reason, "DISTINCT together with GROUP BY / HAVING");
		}
		for (auto &group : node.groups.group_expressions) {
			if (!IsColumn(*group)) {
				return Fail(reason, "a GROUP BY expression");
			}
		}
		if (!any_aggregate && node.groups.group_expressions.empty() && !node.having && !has_join && !distinct) {
			// Over one table a plain projection gains nothing: the scan already pushes the filter.
			return Fail(reason, "a plain projection over one table");
		}
		if (node.where_clause && !PredicateShape(*node.where_clause, false)) {
			return Fail(reason, "a WHERE shape");
		}
		if (node.having && !PredicateShape(*node.having, true)) {
			return Fail(reason, "a HAVING shape");
		}
		return true;
	}

	//! The base tables of the FROM clause, in the order RenderFrom names them.
	static void CollectTables(TableRef &ref, vector<reference<BaseTableRef>> &tables) {
		if (ref.type == TableReferenceType::BASE_TABLE) {
			tables.push_back(ref.Cast<BaseTableRef>());
			return;
		}
		auto &join = ref.Cast<JoinRef>();
		CollectTables(*join.left, tables);
		CollectTables(*join.right, tables);
	}

	//! Types everything against the tables and renders. False = run it locally (`reason` says why).
	bool Render(ClientContext &context, vector<Scope> scopes, RemotePlan &plan, string &reason) {
		this->context = &context;
		this->scopes = std::move(scopes);
		for (idx_t i = 0; i < this->scopes.size(); i++) {
			for (idx_t j = 0; j < i; j++) {
				if (this->scopes[i].name == this->scopes[j].name) {
					return Fail(reason, "two FROM items with the same name"); // DuckDB reports it
				}
			}
		}
		// DuckDB compares strings byte for byte unless a collation is in force; with one, so would the server have
		// to be told, and it is not.
		Value collation;
		strings_ok = !context.TryGetCurrentSetting("default_collation", collation) || collation.IsNull() ||
		             collation.ToString().empty();

		bool distinct = false;
		for (auto &modifier : node.modifiers) {
			distinct = distinct || modifier->type == ResultModifierType::DISTINCT_MODIFIER;
		}
		// DISTINCT is GROUP BY every select item — rendered that way so strings take the same exact key.
		vector<reference<const ParsedExpression>> group_exprs;
		if (distinct) {
			for (auto &item : node.select_list) {
				group_exprs.push_back(*item);
			}
		} else {
			for (auto &group : node.groups.group_expressions) {
				group_exprs.push_back(*group);
			}
		}
		for (auto &group : group_exprs) {
			ColumnId id;
			if (!ResolveColumn(group.get().Cast<ColumnRefExpression>(), id)) {
				return Fail(reason, "a GROUP BY column that is unknown or ambiguous");
			}
			auto &type = Column(id).Type();
			if (!ExactType(type) && !(strings_ok && PlainStringType(type))) {
				return Fail(reason, "a GROUP BY column of a type that does not group the same on the server");
			}
			if (std::find(groups.begin(), groups.end(), id) == groups.end()) {
				groups.push_back(id);
			}
		}
		grouped = !groups.empty() || node.having;
		for (auto &item : node.select_list) {
			grouped = grouped || IsAggregate(*item);
		}

		string select;
		vector<Rendered> items;
		for (idx_t i = 0; i < node.select_list.size(); i++) {
			auto &item = *node.select_list[i];
			Rendered value;
			if (!RenderValue(item, value, reason)) {
				return false;
			}
			if (!grouped && !ExactType(value.type) && !PlainStringType(value.type)) {
				return Fail(reason, "a projected column of a type that is not passed through exactly");
			}
			select += (i ? ", " : "") + value.sql + " AS [c" + std::to_string(i) + "]";
			plan.outputs.push_back(Output {item.GetName(), value.type, value.keyed});
			items.push_back(std::move(value));
		}

		string from;
		idx_t next_scope = 0;
		if (!RenderFrom(*node.from_table, next_scope, from, reason)) {
			return false;
		}
		string where, having;
		if (node.where_clause && !RenderPredicate(*node.where_clause, false, where, reason)) {
			return false;
		}
		if (node.having && !RenderPredicate(*node.having, true, having, reason)) {
			return false;
		}

		optional_idx limit, offset;
		for (auto &modifier : node.modifiers) {
			if (modifier->type == ResultModifierType::ORDER_MODIFIER) {
				for (auto &order : modifier->Cast<OrderModifier>().orders) {
					SortKey key;
					if (!RenderSortKey(order, items, key, reason)) {
						return false;
					}
					plan.keys.push_back(std::move(key));
				}
			} else if (modifier->type == ResultModifierType::LIMIT_MODIFIER) {
				auto &limit_mod = modifier->Cast<LimitModifier>();
				if (limit_mod.limit && !RenderCount(*limit_mod.limit, limit, reason)) {
					return false;
				}
				if (limit_mod.offset && !RenderCount(*limit_mod.offset, offset, reason)) {
					return false;
				}
			}
		}

		// The sort keys travel as hidden columns [k0], [k1], … so the wrapper can sort again.
		string order_by;
		for (idx_t i = 0; i < plan.keys.size(); i++) {
			auto &key = plan.keys[i];
			select += ", " + key.value.sql + " AS [k" + std::to_string(i) + "]";
			// T-SQL has no NULLS FIRST/LAST and puts NULLs first for ASC, last for DESC — so every key carries an
			// explicit NULL rank, whatever DuckDB resolved.
			bool nulls_first = key.nulls == OrderByNullType::NULLS_FIRST;
			order_by += (i ? ", " : "") + string("CASE WHEN ") + key.value.sql + " IS NULL THEN " +
			            (nulls_first ? "0 ELSE 1" : "1 ELSE 0") + " END, " + key.value.sql +
			            (key.order == OrderType::DESCENDING ? " DESC" : " ASC");
		}

		string top;
		string paging;
		if (!plan.keys.empty() || offset.IsValid()) {
			if (plan.keys.empty()) {
				order_by = "(SELECT NULL)"; // OFFSET requires an ORDER BY; with none, both engines pick any rows
			}
			if (limit.IsValid() || offset.IsValid()) {
				paging = " OFFSET " + std::to_string(offset.IsValid() ? offset.GetIndex() : 0) + " ROWS";
				if (limit.IsValid()) {
					paging += " FETCH NEXT " + std::to_string(limit.GetIndex()) + " ROWS ONLY";
				}
			}
		} else if (limit.IsValid()) {
			top = "TOP (" + std::to_string(limit.GetIndex()) + ") ";
		}

		plan.sql = "SELECT " + top + select + " FROM " + from;
		if (!where.empty()) {
			plan.sql += " WHERE " + where;
		}
		for (idx_t i = 0; i < groups.size(); i++) {
			plan.sql += (i ? ", " : " GROUP BY ") + GroupSql(groups[i]);
		}
		if (!having.empty()) {
			plan.sql += " HAVING " + having;
		}
		if (!order_by.empty()) {
			plan.sql += " ORDER BY " + order_by + paging;
		}
		return true;
	}

private:
	static bool Fail(string &reason, const string &why) {
		reason = why;
		return false;
	}

	static bool IsColumn(const ParsedExpression &expr) {
		if (expr.GetExpressionClass() != ExpressionClass::COLUMN_REF) {
			return false;
		}
		// `col` or `t.col`; a longer path names something else (a struct member, another catalog).
		return expr.Cast<ColumnRefExpression>().ColumnNames().size() <= 2;
	}

	static bool IsConstantShape(const ParsedExpression &expr) {
		// After the optimizer's folding a constant is a literal, or a CAST of one (ConstantExpression::FromValue).
		// Whether it IS constant is checked when it is evaluated.
		return expr.GetExpressionClass() == ExpressionClass::CONSTANT ||
		       expr.GetExpressionClass() == ExpressionClass::CAST;
	}

	//! Integer arithmetic over columns and constants, at least one column somewhere in it.
	static bool ArithmeticShape(const ParsedExpression &expr, bool &has_column) {
		if (IsColumn(expr)) {
			has_column = true;
			return true;
		}
		if (IsConstantShape(expr)) {
			return true;
		}
		if (!IsArithmetic(expr)) {
			return false;
		}
		for (auto &arg : expr.Cast<FunctionExpression>().GetArguments()) {
			if (!arg.GetName().empty() || !ArithmeticShape(arg.GetExpression(), has_column)) {
				return false;
			}
		}
		return true;
	}

	static bool IsArithmeticValue(const ParsedExpression &expr) {
		bool has_column = false;
		return IsArithmetic(expr) && ArithmeticShape(expr, has_column) && has_column;
	}

	static bool AggregateShape(const FunctionExpression &func) {
		if (func.Filter() || (func.OrderBy() && !func.OrderBy()->orders.empty()) || func.ExportState()) {
			return false;
		}
		auto name = AggregateName(func);
		auto &args = func.GetArguments();
		if (name == "count_star") {
			return args.empty() && !func.Distinct();
		}
		if (args.size() != 1 || !args[0].GetName().empty()) {
			return false;
		}
		auto &arg = args[0].GetExpression();
		if (name == "count") {
			return IsColumn(arg); // count(DISTINCT col) included
		}
		return !func.Distinct() && (IsColumn(arg) || IsArithmeticValue(arg));
	}

	//! A column, integer arithmetic, or (where allowed) an aggregate.
	static bool ValueShape(const ParsedExpression &expr, bool allow_aggregates) {
		if (IsColumn(expr) || IsArithmeticValue(expr)) {
			return true;
		}
		return allow_aggregates && IsAggregate(expr) && AggregateShape(expr.Cast<FunctionExpression>());
	}

	static bool FromShape(const TableRef &ref, bool &has_join) {
		if (ref.type == TableReferenceType::BASE_TABLE) {
			return !ref.Cast<BaseTableRef>().at_clause;
		}
		if (ref.type != TableReferenceType::JOIN) {
			return false;
		}
		auto &join = ref.Cast<JoinRef>();
		if (join.ref_type != JoinRefType::REGULAR || (join.type != JoinType::INNER && join.type != JoinType::LEFT) ||
		    !join.condition || !join.using_columns.empty() || join.is_implicit ||
		    !join.duplicate_eliminated_columns.empty()) {
			return false;
		}
		has_join = true;
		return FromShape(*join.left, has_join) && FromShape(*join.right, has_join) &&
		       PredicateShape(*join.condition, false);
	}

	static bool PredicateShape(const ParsedExpression &expr, bool allow_aggregates) {
		switch (expr.GetExpressionClass()) {
		case ExpressionClass::COMPARISON: {
			if (!IsComparison(expr.GetExpressionType())) {
				return false;
			}
			auto &cmp = expr.Cast<ComparisonExpression>();
			bool left = ValueShape(cmp.Left(), allow_aggregates), right = ValueShape(cmp.Right(), allow_aggregates);
			return (left && IsConstantShape(cmp.Right())) || (IsConstantShape(cmp.Left()) && right) ||
			       (ValueShape(cmp.Left(), false) && ValueShape(cmp.Right(), false));
		}
		case ExpressionClass::CONJUNCTION: {
			for (auto &child : expr.Cast<ConjunctionExpression>().GetChildren()) {
				if (!PredicateShape(*child, allow_aggregates)) {
					return false;
				}
			}
			return true;
		}
		case ExpressionClass::OPERATOR: {
			auto &children = expr.Cast<OperatorExpression>().GetChildren();
			switch (expr.GetExpressionType()) {
			case ExpressionType::OPERATOR_NOT:
				return children.size() == 1 && PredicateShape(*children[0], allow_aggregates);
			case ExpressionType::OPERATOR_IS_NULL:
			case ExpressionType::OPERATOR_IS_NOT_NULL:
				return children.size() == 1 && IsColumn(*children[0]);
			default:
				return false;
			}
		}
		default:
			return false;
		}
	}

	const ColumnDefinition &Column(const ColumnId &id) const {
		return scopes[id.scope].table->GetColumns().GetColumn(LogicalIndex(id.column));
	}

	string ColumnSql(const ColumnId &id) const {
		return scopes[id.scope].sql + "." + Bracket(Column(id).Name().GetIdentifierName());
	}

	//! A string's exact comparison key (see the file header): binary collation, sentinel appended.
	static string KeyOf(const Rendered &value) {
		return value.keyed ? value.sql : "(" + value.sql + " + N'|') COLLATE Latin1_General_100_BIN2";
	}

	string GroupSql(const ColumnId &id) const {
		Rendered column {ColumnSql(id), Column(id).Type()};
		return PlainStringType(column.type) ? KeyOf(column) : column.sql;
	}

	//! `t.col` names one scope; a bare `col` must exist in EXACTLY one — DuckDB reports an ambiguity itself, so
	//! a name two tables share stays local rather than letting the server pick one.
	bool ResolveColumn(const ColumnRefExpression &ref, ColumnId &id) const {
		auto &names = ref.ColumnNames();
		auto &name = names.back();
		idx_t matches = 0;
		for (idx_t s = 0; s < scopes.size(); s++) {
			if (names.size() == 2 && !(names[0] == scopes[s].name)) {
				continue;
			}
			auto &columns = scopes[s].table->GetColumns();
			if (!columns.ColumnExists(name)) {
				continue;
			}
			auto &column = columns.GetColumn(name);
			if (column.Generated()) {
				return false;
			}
			id = ColumnId {s, column.Logical().index};
			matches++;
		}
		return matches == 1;
	}

	bool RenderFrom(const TableRef &ref, idx_t &next_scope, string &sql, string &reason) const {
		if (ref.type == TableReferenceType::BASE_TABLE) {
			auto &scope = scopes[next_scope++];
			sql = Bracket(scope.table->ParentSchema().name.GetIdentifierName()) + "." +
			      Bracket(scope.table->name.GetIdentifierName()) + " AS " + scope.sql;
			return true;
		}
		auto &join = ref.Cast<JoinRef>();
		string left, right, on;
		if (!RenderFrom(*join.left, next_scope, left, reason) || !RenderFrom(*join.right, next_scope, right, reason) ||
		    !RenderPredicate(*join.condition, false, on, reason)) {
			return false;
		}
		if (join.right->type == TableReferenceType::JOIN) {
			right = "(" + right + ")";
		}
		sql = left + (join.type == JoinType::LEFT ? " LEFT JOIN " : " INNER JOIN ") + right + " ON " + on;
		return true;
	}

	//! A column, integer arithmetic or an aggregate. Under grouping, a column in the select list, HAVING or
	//! ORDER BY must be a GROUP BY column, as DuckDB requires; a ROW-LEVEL value (WHERE, join ON, an aggregate's
	//! argument) is evaluated before grouping and may name any column.
	bool RenderValue(const ParsedExpression &expr, Rendered &out, string &reason, bool row_level = false) const {
		if (IsAggregate(expr)) {
			return RenderAggregate(expr.Cast<FunctionExpression>(), out, reason);
		}
		if (IsArithmetic(expr)) {
			return RenderArithmetic(expr, out, reason, row_level);
		}
		ColumnId id;
		if (!ResolveColumn(expr.Cast<ColumnRefExpression>(), id)) {
			return Fail(reason, "a column that is unknown or ambiguous");
		}
		bool grouped_here = grouped && !row_level;
		if (grouped_here && std::find(groups.begin(), groups.end(), id) == groups.end()) {
			return Fail(reason, "a column that is not grouped");
		}
		out.sql = ColumnSql(id);
		out.type = Column(id).Type();
		if (grouped_here && PlainStringType(out.type)) {
			// T-SQL must select the GROUP BY expression itself, so a grouped string travels as its key.
			out.sql = KeyOf(out);
			out.keyed = true;
		}
		return true;
	}

	//! Integer `+ - *`, computed on the server in BIGINT and typed as DuckDB types it: the widest column operand
	//! (measured). A constant must fit that type, or DuckDB would have widened it. A final value that overflows
	//! DuckDB's type fails in the wrapper's CAST; one that overflows only INTERMEDIATELY is an error in DuckDB
	//! and an answer here — the same error-versus-answer divergence the DML shortcut accepts, never two answers.
	bool RenderArithmetic(const ParsedExpression &expr, Rendered &out, string &reason, bool row_level) const {
		vector<Value> constants;
		int rank = 0;
		if (!RenderArithmeticNode(expr, out.sql, rank, constants, reason, row_level)) {
			return false;
		}
		out.type = rank == 3 ? LogicalType::BIGINT : rank == 2 ? LogicalType::INTEGER : LogicalType::SMALLINT;
		for (auto &constant : constants) {
			string error;
			auto cast = constant.DefaultTryCastAs(out.type, &error, true);
			if (!cast.has_value() || cast->IsNull()) {
				return Fail(reason, "an arithmetic constant wider than its columns");
			}
			auto back = cast->DefaultTryCastAs(constant.type(), &error, true);
			if (!back.has_value() || *back != constant) {
				return Fail(reason, "an arithmetic constant wider than its columns");
			}
		}
		return true;
	}

	bool RenderArithmeticNode(const ParsedExpression &expr, string &sql, int &rank, vector<Value> &constants,
	                          string &reason, bool row_level) const {
		if (IsColumn(expr)) {
			Rendered column;
			if (!RenderValue(expr, column, reason, row_level)) {
				return false;
			}
			auto column_rank = ArithmeticRank(column.type);
			if (!column_rank) {
				return Fail(reason, "arithmetic over a column that is not SMALLINT / INTEGER / BIGINT");
			}
			rank = MaxValue(rank, column_rank);
			sql = "CAST(" + column.sql + " AS BIGINT)";
			return true;
		}
		if (IsConstantShape(expr)) {
			Value value;
			if (!Evaluate(expr, value, reason) || value.IsNull() || !value.type().IsIntegral()) {
				return Fail(reason, "an arithmetic constant that is not an integer");
			}
			constants.push_back(value);
			sql = value.ToString();
			return true;
		}
		auto &func = expr.Cast<FunctionExpression>();
		auto op = func.FunctionName().GetIdentifierName();
		auto &args = func.GetArguments();
		string left;
		if (!RenderArithmeticNode(args[0].GetExpression(), left, rank, constants, reason, row_level)) {
			return false;
		}
		if (args.size() == 1) {
			sql = "(-" + left + ")";
			return true;
		}
		string right;
		if (!RenderArithmeticNode(args[1].GetExpression(), right, rank, constants, reason, row_level)) {
			return false;
		}
		sql = "(" + left + " " + op + " " + right + ")";
		return true;
	}

	bool RenderAggregate(const FunctionExpression &func, Rendered &out, string &reason) const {
		auto name = AggregateName(func);
		if (name == "count_star") {
			out.sql = "COUNT_BIG(*)";
			out.type = LogicalType::BIGINT;
			return true;
		}
		Rendered arg;
		if (!RenderValue(func.GetArguments()[0].GetExpression(), arg, reason, true)) {
			return false;
		}
		if (name == "count") {
			out.type = LogicalType::BIGINT;
			if (!func.Distinct()) {
				if (!CountableType(arg.type)) {
					return Fail(reason, "count over a type the server cannot aggregate");
				}
				out.sql = "COUNT_BIG(" + arg.sql + ")";
				return true;
			}
			if (ExactType(arg.type)) {
				out.sql = "COUNT_BIG(DISTINCT " + arg.sql + ")";
				return true;
			}
			if (strings_ok && PlainStringType(arg.type)) {
				out.sql = "COUNT_BIG(DISTINCT " + KeyOf(arg) + ")";
				return true;
			}
			return Fail(reason, "count(DISTINCT) over a type that does not compare the same on the server");
		}
		if (!ExactType(arg.type)) {
			return Fail(reason, name + " over a type that is not exact");
		}
		if (name == "min" || name == "max") {
			out.sql = StringUtil::Upper(name) + "(" + arg.sql + ")";
			out.type = arg.type;
			return true;
		}
		// sum: DuckDB widens an integer sum to HUGEINT and a decimal sum to DECIMAL(38, scale). SQL Server keeps
		// an INT sum an INT (overflowing past 2^31), so the integer case is summed as DECIMAL(38,0) — exact up
		// to 10^38, where HUGEINT goes to 1.7 x 10^38: between the two an answer on one side is an overflow
		// error on the other, never a different number.
		if (IntegerType(arg.type)) {
			out.sql = "SUM(CAST(" + arg.sql + " AS DECIMAL(38,0)))";
			out.type = LogicalType::HUGEINT;
			return true;
		}
		if (arg.type.id() == LogicalTypeId::DECIMAL) {
			out.sql = "SUM(" + arg.sql + ")";
			out.type = LogicalType::DECIMAL(38, DecimalType::GetScale(arg.type));
			return true;
		}
		return Fail(reason, "sum over a date");
	}

	static bool SameFamily(const LogicalType &a, const LogicalType &b) {
		// Both engines compare two numbers in the wider of their types; a date only compares with a date.
		return (a.id() == LogicalTypeId::DATE) == (b.id() == LogicalTypeId::DATE);
	}

	bool RenderPredicate(const ParsedExpression &expr, bool allow_aggregates, string &sql, string &reason) const {
		switch (expr.GetExpressionClass()) {
		case ExpressionClass::COMPARISON: {
			auto &cmp = expr.Cast<ComparisonExpression>();
			auto type = expr.GetExpressionType();
			auto op = ComparisonSql(type);
			bool equality = type == ExpressionType::COMPARE_EQUAL || type == ExpressionType::COMPARE_NOTEQUAL;
			if (!IsConstantShape(cmp.Left()) && !IsConstantShape(cmp.Right())) {
				Rendered left, right;
				if (!RenderValue(cmp.Left(), left, reason, !allow_aggregates) ||
				    !RenderValue(cmp.Right(), right, reason, !allow_aggregates)) {
					return false;
				}
				if (PlainStringType(left.type) && PlainStringType(right.type)) {
					if (!strings_ok || !equality) {
						return Fail(reason, "a string comparison other than = / <>");
					}
					auto eq = "(" + KeyOf(left) + " = " + KeyOf(right) + ")";
					sql = type == ExpressionType::COMPARE_EQUAL ? eq : "(NOT " + eq + ")";
					return true;
				}
				if (!ComparableType(left.type) || !ComparableType(right.type) || !SameFamily(left.type, right.type)) {
					return Fail(reason, "a comparison across types that do not compare exactly");
				}
				sql = "(" + left.sql + " " + op + " " + right.sql + ")";
				return true;
			}
			bool value_left = !IsConstantShape(cmp.Left());
			auto &value_expr = value_left ? cmp.Left() : cmp.Right();
			auto &constant_expr = value_left ? cmp.Right() : cmp.Left();
			Rendered value;
			if (!RenderValue(value_expr, value, reason, !allow_aggregates)) {
				return false;
			}
			if (PlainStringType(value.type)) {
				if (!strings_ok || !equality) {
					return Fail(reason, "a string comparison other than = / <>");
				}
				string literal;
				if (!RenderStringKey(constant_expr, literal, reason)) {
					return false;
				}
				auto eq = "(" + KeyOf(value) + " = " + literal + ")";
				sql = type == ExpressionType::COMPARE_EQUAL ? eq : "(NOT " + eq + ")";
				return true;
			}
			if (!ComparableType(value.type)) {
				return Fail(reason, "a comparison over a type that is not exact");
			}
			string literal;
			if (!RenderConstant(constant_expr, value.type, literal, reason)) {
				return false;
			}
			sql = "(" + (value_left ? value.sql + " " + op + " " + literal : literal + " " + op + " " + value.sql) + ")";
			return true;
		}
		case ExpressionClass::CONJUNCTION: {
			auto joiner = expr.GetExpressionType() == ExpressionType::CONJUNCTION_AND ? " AND " : " OR ";
			auto &children = expr.Cast<ConjunctionExpression>().GetChildren();
			string out;
			for (idx_t i = 0; i < children.size(); i++) {
				string child;
				if (!RenderPredicate(*children[i], allow_aggregates, child, reason)) {
					return false;
				}
				out += (i ? joiner : "") + child;
			}
			sql = "(" + out + ")";
			return true;
		}
		default: {
			auto &children = expr.Cast<OperatorExpression>().GetChildren();
			if (expr.GetExpressionType() == ExpressionType::OPERATOR_NOT) {
				string child;
				if (!RenderPredicate(*children[0], allow_aggregates, child, reason)) {
					return false;
				}
				sql = "(NOT " + child + ")";
				return true;
			}
			// IS [NOT] NULL means the same for every type, so no exact-type gate here.
			Rendered value;
			if (!RenderValue(*children[0], value, reason, !allow_aggregates)) {
				return false;
			}
			sql = "(" + value.sql +
			      (expr.GetExpressionType() == ExpressionType::OPERATOR_IS_NULL ? " IS NULL)" : " IS NOT NULL)");
			return true;
		}
		}
	}

	//! ORDER BY binds the way DuckDB binds it: an integer is a 1-based output POSITION, a bare name that is a
	//! select ALIAS is that item (an alias wins over a column of the same name), anything else is a value.
	//! Direction and NULL placement come from DuckDB's own resolution of the session defaults.
	bool RenderSortKey(const OrderByNode &order, const vector<Rendered> &items, SortKey &key, string &reason) const {
		auto &expr = *order.expression;
		bool resolved = false;
		if (IsConstantShape(expr)) {
			Value value;
			if (!Evaluate(expr, value, reason) || !value.type().IsIntegral() || value.IsNull()) {
				return Fail(reason, "an ORDER BY constant that is not a position");
			}
			auto position = value.GetValue<int64_t>();
			if (position < 1 || position > int64_t(items.size())) {
				return Fail(reason, "an ORDER BY position out of range"); // DuckDB reports it
			}
			key.value = items[position - 1];
			resolved = true;
		} else if (IsColumn(expr) && expr.Cast<ColumnRefExpression>().ColumnNames().size() == 1) {
			auto &name = expr.Cast<ColumnRefExpression>().ColumnNames()[0];
			for (idx_t i = 0; i < node.select_list.size(); i++) {
				if (node.select_list[i]->HasAlias() && node.select_list[i]->GetAlias() == name) {
					if (resolved) {
						return Fail(reason, "an ORDER BY name two select aliases share");
					}
					key.value = items[i];
					resolved = true;
				}
			}
		}
		if (!resolved && !RenderValue(expr, key.value, reason)) {
			return false;
		}
		if (!ComparableType(key.value.type)) {
			return Fail(reason, "an ORDER BY key of a type that does not sort the same on the server");
		}
		auto &config = DBConfig::GetConfig(*context);
		key.order = config.ResolveOrder(*context, order.type);
		key.nulls = config.ResolveNullOrder(*context, key.order, order.null_order);
		return true;
	}

	bool RenderCount(const ParsedExpression &expr, optional_idx &out, string &reason) const {
		Value value;
		if (!Evaluate(expr, value, reason) || value.IsNull() || !value.type().IsIntegral()) {
			return Fail(reason, "a LIMIT / OFFSET that is not an integer constant");
		}
		auto count = value.GetValue<int64_t>();
		if (count < 0) {
			return Fail(reason, "a negative LIMIT / OFFSET"); // DuckDB reports it
		}
		out = optional_idx(idx_t(count));
		return true;
	}

	bool Evaluate(const ParsedExpression &expr, Value &value, string &reason) const {
		try {
			auto copy = expr.Copy();
			auto binder = Binder::CreateBinder(*context);
			ConstantBinder constant_binder(*binder, *context, "fabricator remote select");
			auto bound = constant_binder.Bind(copy);
			if (!bound || !bound->IsFoldable() || !ExpressionExecutor::TryEvaluateScalar(*context, *bound, value)) {
				return Fail(reason, "a value that is not a constant");
			}
			return true;
		} catch (std::exception &) {
			return Fail(reason, "a value that does not bind as a constant");
		}
	}

	//! A string constant as its comparison key: N'<text>|' under the binary collation. Only a VARCHAR constant
	//! qualifies (DuckDB compares a string with a string), and one holding U+0000 stays local.
	bool RenderStringKey(const ParsedExpression &expr, string &literal, string &reason) const {
		Value value;
		if (!Evaluate(expr, value, reason)) {
			return false;
		}
		if (value.IsNull() || value.type().id() != LogicalTypeId::VARCHAR) {
			return Fail(reason, "a string comparison with something other than a string constant");
		}
		auto text = value.GetValue<string>();
		if (text.find('\0') != string::npos) {
			return Fail(reason, "a string constant holding U+0000");
		}
		literal = "N'" + StringUtil::Replace(text, "'", "''") + "|' COLLATE Latin1_General_100_BIN2";
		return true;
	}

	//! Evaluates the constant with DuckDB's own binder, converts it EXACTLY to the other side's type, and renders
	//! a T-SQL literal of that type. A constant that does not convert exactly (1.5 against an INTEGER column, 300
	//! against a TINYINT) refuses: DuckDB would compare in a wider type, and the narrow literal would not.
	bool RenderConstant(const ParsedExpression &expr, const LogicalType &type, string &literal,
	                    string &reason) const {
		Value value;
		if (!Evaluate(expr, value, reason)) {
			return false;
		}
		if (value.IsNull()) {
			return Fail(reason, "a comparison with NULL");
		}
		// ⚠ "strict" is NOT exactness: a strict DECIMAL -> INTEGER cast ROUNDS (strict governs string parsing
		// only). MEASURED as a wrong answer: `id > 1.5` was pushed as `[id] > 2` and counted 2 rows where 3 are
		// right. So the converted value must survive the trip BACK to the constant's own type unchanged.
		string error;
		auto cast = value.DefaultTryCastAs(type, &error, true);
		if (!cast.has_value() || cast->IsNull()) {
			return Fail(reason, "a value that does not convert to the other side's type");
		}
		auto back = cast->DefaultTryCastAs(value.type(), &error, true);
		if (!back.has_value() || *back != value) {
			return Fail(reason, "a value that does not convert EXACTLY to the other side's type");
		}
		auto text = cast->ToString();
		switch (type.id()) {
		case LogicalTypeId::DECIMAL:
			literal = "CAST('" + text + "' AS DECIMAL(" + std::to_string(DecimalType::GetWidth(type)) + "," +
			          std::to_string(DecimalType::GetScale(type)) + "))";
			return true;
		case LogicalTypeId::HUGEINT:
			// an integer sum, which the server computes as DECIMAL(38,0)
			literal = "CAST('" + text + "' AS DECIMAL(38,0))";
			return true;
		case LogicalTypeId::DATE:
			// SQL Server's DATE spans 0001-01-01 .. 9999-12-31; DuckDB's is wider (and prints BC dates with a
			// suffix). Anything that is not the plain ten-character form stays local.
			if (text.size() != 10) {
				return Fail(reason, "a date outside SQL Server's range");
			}
			literal = "CAST('" + text + "' AS DATE)";
			return true;
		default:
			literal = text; // integers
			return true;
		}
	}

	const SelectNode &node;
	ClientContext *context = nullptr;
	vector<Scope> scopes;
	vector<ColumnId> groups;
	bool grouped = false;
	bool strings_ok = false;
};

} // namespace

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
	// A plain table or a join of them; the query-node check decides which joins. A catalog-bound table function
	// (a TVF, a stored procedure) would otherwise be pushed on its own, and it already has a path of its own.
	if (ref.type == TableReferenceType::JOIN) {
		return true;
	}
	return ref.type == TableReferenceType::BASE_TABLE && !ref.Cast<BaseTableRef>().at_clause;
}

bool FabricatorCatalog::SupportsPushdown(const SQLStatement &statement) {
	return false; // DDL keeps the host's own path
}

bool FabricatorCatalog::SupportsPushdown(const QueryNode &node) {
	// SELECT only. DELETE and UPDATE keep the plan-less shortcut (ABI v93/v94), which keeps DuckDB's binding
	// and its affected-row count; INSERT and MERGE keep the rowid path.
	if (node.type != QueryNodeType::SELECT_NODE) {
		return false;
	}
	string reason;
	if (!RemoteSelect(node.Cast<SelectNode>()).Structural(reason)) {
		RemoteTrace("local: " + reason);
		return false;
	}
	return true;
}

unique_ptr<TableRef> FabricatorCatalog::RemoteExecute(ClientContext &context, unique_ptr<QueryNode> node) {
	auto &select = node->Cast<SelectNode>();
	vector<reference<BaseTableRef>> tables;
	RemoteSelect::CollectTables(*select.from_table, tables);

	// Resolve every table (the optimizer stripped the catalog name; a missing schema is the default one).
	vector<Scope> scopes;
	vector<Identifier> schemas;
	bool resolved = true;
	for (idx_t i = 0; i < tables.size(); i++) {
		auto &ref = tables[i].get();
		auto &qualified = ref.GetQualifiedName();
		auto schema = qualified.Schema().empty() ? Identifier(GetDefaultSchema().value_or(Identifier("dbo")))
		                                         : qualified.Schema();
		schemas.push_back(schema);
		optional_ptr<CatalogEntry> entry;
		try {
			entry = GetEntry(context, CatalogType::TABLE_ENTRY, schema, ref.Table(), OnEntryNotFound::RETURN_NULL);
		} catch (std::exception &) {
			entry = nullptr;
		}
		if (!entry || entry->type != CatalogType::TABLE_ENTRY) {
			resolved = false;
			continue;
		}
		Scope scope;
		scope.name = ref.alias.empty() ? ref.Table() : ref.alias;
		scope.table = &entry->Cast<TableCatalogEntry>();
		scope.sql = "[t" + std::to_string(i) + "]";
		scopes.push_back(std::move(scope));
	}

	RemotePlan plan;
	string reason = "a table did not resolve";
	bool pushed = resolved && RemoteSelect(select).Render(context, std::move(scopes), plan, reason);

	auto statement = make_uniq<SelectStatement>();
	if (!pushed) {
		// DECLINE: hand the statement back with this catalog's name restored on every table (the optimizer
		// stripped it), so it binds and runs locally exactly as the user wrote it.
		RemoteTrace("local: " + reason);
		for (idx_t i = 0; i < tables.size(); i++) {
			tables[i].get().SetQualifiedName(GetName(), schemas[i], tables[i].get().Table());
		}
		statement->node = std::move(node);
		return make_uniq<SubqueryRef>(std::move(statement));
	}
	RemoteTrace("remote: " + plan.sql);

	// SELECT CAST(c0 AS <duckdb type>) AS <duckdb name>, … FROM fabricator_query('<catalog>', '<t-sql>') __fab_remote
	//   ORDER BY CAST(k0 AS <type>) <dir> <nulls>, …
	// ⚠ Every reference to the server's columns is QUALIFIED with the function's alias, so a user alias can
	// never shadow one: the wrapper's ORDER BY binds a select alias before a column of the same name, and a
	// select list can see an earlier item's alias (lateral aliases).
	auto server_column = [](const string &name) {
		return make_uniq<ColumnRefExpression>(vector<Identifier> {Identifier("__fab_remote"), Identifier(name)});
	};
	vector<unique_ptr<ParsedExpression>> args;
	args.push_back(ConstantExpression::String(GetName().GetIdentifierName()));
	args.push_back(ConstantExpression::String(plan.sql));
	auto function = make_uniq<TableFunctionRef>();
	function->function = make_uniq<FunctionExpression>(Identifier("fabricator_query"), std::move(args));
	auto wrapper = make_uniq<SelectNode>();
	for (idx_t i = 0; i < plan.outputs.size(); i++) {
		unique_ptr<ParsedExpression> column = server_column("c" + std::to_string(i));
		if (plan.outputs[i].keyed) {
			// a grouped string arrives with its sentinel; left(x, -1) drops exactly that one character
			vector<unique_ptr<ParsedExpression>> strip;
			strip.push_back(std::move(column));
			strip.push_back(ConstantExpression::Integer(-1));
			column = make_uniq<FunctionExpression>(Identifier("left"), std::move(strip));
		}
		auto cast = make_uniq<CastExpression>(plan.outputs[i].type, std::move(column));
		cast->SetAlias(plan.outputs[i].name);
		wrapper->select_list.push_back(std::move(cast));
	}
	if (!plan.keys.empty()) {
		auto order = make_uniq<OrderModifier>();
		for (idx_t i = 0; i < plan.keys.size(); i++) {
			auto &key = plan.keys[i];
			auto cast = make_uniq<CastExpression>(key.value.type, server_column("k" + std::to_string(i)));
			order->orders.emplace_back(key.order, key.nulls, std::move(cast));
		}
		wrapper->modifiers.push_back(std::move(order));
	}
	function->alias = Identifier("__fab_remote");
	wrapper->from_table = std::move(function);
	statement->node = std::move(wrapper);
	return make_uniq<SubqueryRef>(std::move(statement));
}

} // namespace duckdb
