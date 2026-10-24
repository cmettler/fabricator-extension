//===----------------------------------------------------------------------===//
// fabricator_remote_select.cpp
//
// DuckDB 2.0's remote pushdown optimizer, SELECT only, for a provider that declares `remote_select`
// (SQL Server with the `remote_pushdown` ATTACH option). The optimizer runs BEFORE binding and hands us a
// parsed SelectNode; we either run it on the server as one T-SQL statement or hand it back to run locally.
//
// THE RULE IS THE ONE THE DML SHORTCUT FOLLOWS (docs/duckdb-2.0-remote-execution.md §2.1): render EXACTLY or
// do not push. Nothing re-checks what the server returns, so a shape whose T-SQL meaning can differ from
// DuckDB's — collation, padding, integer division, float summation order, result types — is refused, never
// approximated. What is admitted (the spike, §4.1):
//   - ONE base table of this catalog, no AT clause, no sample;
//   - WHERE: =, <>, <, <=, >, >= between a column and a constant, AND / OR / NOT, IS [NOT] NULL — over
//     integer, decimal and date columns, with every constant converting EXACTLY to the column's type;
//   - GROUP BY plain columns of those types; select items that are group columns or
//     count(*), count(col), min/max(col) and integer/decimal sum(col);
//   - no HAVING, QUALIFY, window, DISTINCT, ORDER BY, LIMIT, CTE, join or subquery.
//
// ⚠ TWO HALVES, because the optimizer's veto hooks get no ClientContext. SupportsPushdown(QueryNode) checks
// the SHAPE; RemoteExecute (which has a context) resolves the table, types every column and constant, and
// renders. A shape whose TYPES do not qualify is DECLINED there by returning the node itself, re-qualified
// with this catalog's name, so it binds and runs locally exactly as written.
//
// ⚠ TYPES AND NAMES MUST BE DUCKDB'S. T-SQL's COUNT is INT and an integer SUM keeps the column's type and
// overflows; the result of the server statement is therefore wrapped in a projection that CASTs every column
// to the type DuckDB's own binder would give it and names it the way DuckDB would.
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_catalog.hpp"
#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/common/printer.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/execution/expression_executor.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/columnref_expression.hpp"
#include "duckdb/parser/expression/comparison_expression.hpp"
#include "duckdb/parser/expression/conjunction_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/expression/function_expression.hpp"
#include "duckdb/parser/expression/operator_expression.hpp"
#include "duckdb/parser/query_node/select_node.hpp"
#include "duckdb/parser/statement/select_statement.hpp"
#include "duckdb/parser/tableref/basetableref.hpp"
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

// The types whose comparisons, grouping and min/max mean the same thing on both engines. Strings are out
// (collation, and T-SQL ignores trailing spaces in '='), as are floats (NaN, summation order), timestamps
// (precision and time zones differ by column type) and booleans (BIT) in this first slice.
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

class RemoteSelect {
public:
	RemoteSelect(const SelectNode &node) : node(node) {
	}

	//! The SHAPE check — no types, no catalog lookups (the veto hook has no context).
	bool Structural(string &reason) const {
		if (!node.cte_map.map.empty()) {
			return Fail(reason, "a CTE");
		}
		if (!node.modifiers.empty()) {
			return Fail(reason, "ORDER BY / LIMIT / DISTINCT");
		}
		if (node.having || node.qualify || node.sample) {
			return Fail(reason, "HAVING / QUALIFY / SAMPLE");
		}
		if (node.aggregate_handling != AggregateHandling::STANDARD_HANDLING) {
			return Fail(reason, "GROUP BY ALL");
		}
		if (node.groups.grouping_sets.size() > 1) {
			return Fail(reason, "GROUPING SETS / ROLLUP / CUBE");
		}
		if (!node.from_table || node.from_table->type != TableReferenceType::BASE_TABLE) {
			return Fail(reason, "not a single base table");
		}
		auto &table = node.from_table->Cast<BaseTableRef>();
		if (table.at_clause) {
			return Fail(reason, "an AT clause");
		}
		bool any_aggregate = false;
		for (auto &item : node.select_list) {
			if (IsAggregate(*item)) {
				auto &func = item->Cast<FunctionExpression>();
				if (func.Distinct() || func.Filter() || (func.OrderBy() && !func.OrderBy()->orders.empty()) ||
				    func.ExportState()) {
					return Fail(reason, "an aggregate modifier");
				}
				auto name = AggregateName(func);
				auto &args = func.GetArguments();
				if (name == "count_star" ? !args.empty() : args.size() != 1) {
					return Fail(reason, "an aggregate's arguments");
				}
				if (!args.empty() && (!args[0].GetName().empty() || !IsColumn(args[0].GetExpression()))) {
					return Fail(reason, "an aggregate over an expression");
				}
				any_aggregate = true;
			} else if (!IsColumn(*item)) {
				return Fail(reason, "a select item that is neither a column nor an aggregate");
			}
		}
		for (auto &group : node.groups.group_expressions) {
			if (!IsColumn(*group)) {
				return Fail(reason, "a GROUP BY expression");
			}
		}
		if (!any_aggregate && node.groups.group_expressions.empty()) {
			// A plain projection gains nothing: the scan already pushes the filter.
			return Fail(reason, "no aggregate and no GROUP BY");
		}
		if (node.where_clause && !PredicateShape(*node.where_clause)) {
			return Fail(reason, "a WHERE shape");
		}
		return true;
	}

	//! Types everything against the table and renders. False = run it locally (`reason` says why).
	bool Render(ClientContext &context, TableCatalogEntry &table, string &sql,
	            vector<std::pair<Identifier, LogicalType>> &outputs, string &reason) {
		this->context = &context;
		this->table = &table;
		auto &ref = node.from_table->Cast<BaseTableRef>();
		alias = ref.alias;

		// Group columns, resolved — a plain select column must be one of them, as DuckDB requires.
		vector<idx_t> group_columns;
		for (auto &group : node.groups.group_expressions) {
			idx_t index;
			if (!ResolveColumn(group->Cast<ColumnRefExpression>(), index) ||
			    !ExactType(table.GetColumns().GetColumn(LogicalIndex(index)).Type())) {
				return Fail(reason, "a GROUP BY column that is unknown or not of an exact type");
			}
			group_columns.push_back(index);
		}

		string select;
		for (idx_t i = 0; i < node.select_list.size(); i++) {
			auto &item = *node.select_list[i];
			string rendered;
			LogicalType type;
			if (IsAggregate(item)) {
				if (!RenderAggregate(item.Cast<FunctionExpression>(), rendered, type, reason)) {
					return false;
				}
			} else {
				idx_t index;
				if (!ResolveColumn(item.Cast<ColumnRefExpression>(), index)) {
					return Fail(reason, "an unknown select column");
				}
				if (std::find(group_columns.begin(), group_columns.end(), index) == group_columns.end()) {
					return Fail(reason, "a select column that is not grouped");
				}
				auto &column = table.GetColumns().GetColumn(LogicalIndex(index));
				rendered = Bracket(column.Name().GetIdentifierName());
				type = column.Type();
			}
			select += (i ? ", " : "") + rendered + " AS [c" + std::to_string(i) + "]";
			outputs.emplace_back(item.GetName(), type);
		}

		string where;
		if (node.where_clause && !RenderPredicate(*node.where_clause, where, reason)) {
			return false;
		}
		sql = "SELECT " + select + " FROM " + Bracket(table.ParentSchema().name.GetIdentifierName()) + "." +
		      Bracket(table.name.GetIdentifierName());
		if (!where.empty()) {
			sql += " WHERE " + where;
		}
		for (idx_t i = 0; i < group_columns.size(); i++) {
			sql += (i ? ", " : " GROUP BY ") +
			       Bracket(table.GetColumns().GetColumn(LogicalIndex(group_columns[i])).Name().GetIdentifierName());
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

	static bool PredicateShape(const ParsedExpression &expr) {
		switch (expr.GetExpressionClass()) {
		case ExpressionClass::COMPARISON: {
			if (!IsComparison(expr.GetExpressionType())) {
				return false;
			}
			auto &cmp = expr.Cast<ComparisonExpression>();
			return (IsColumn(cmp.Left()) && IsConstantShape(cmp.Right())) ||
			       (IsConstantShape(cmp.Left()) && IsColumn(cmp.Right()));
		}
		case ExpressionClass::CONJUNCTION: {
			for (auto &child : expr.Cast<ConjunctionExpression>().GetChildren()) {
				if (!PredicateShape(*child)) {
					return false;
				}
			}
			return true;
		}
		case ExpressionClass::OPERATOR: {
			auto &op = expr.Cast<OperatorExpression>();
			auto &children = op.GetChildren();
			switch (expr.GetExpressionType()) {
			case ExpressionType::OPERATOR_NOT:
				return children.size() == 1 && PredicateShape(*children[0]);
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

	bool ResolveColumn(const ColumnRefExpression &ref, idx_t &index) const {
		auto &names = ref.ColumnNames();
		if (names.size() == 2) {
			auto &qualifier = names[0];
			bool matches = alias.empty() ? qualifier == table->name : qualifier == alias;
			if (!matches) {
				return false;
			}
		}
		auto &columns = table->GetColumns();
		auto &name = names.back();
		if (!columns.ColumnExists(name)) {
			return false;
		}
		auto &column = columns.GetColumn(name);
		if (column.Generated()) {
			return false;
		}
		index = column.Logical().index;
		return true;
	}

	bool RenderAggregate(const FunctionExpression &func, string &sql, LogicalType &type, string &reason) const {
		auto name = AggregateName(func);
		if (name == "count_star") {
			sql = "COUNT_BIG(*)";
			type = LogicalType::BIGINT;
			return true;
		}
		idx_t index;
		if (!ResolveColumn(func.GetArguments()[0].GetExpression().Cast<ColumnRefExpression>(), index)) {
			return Fail(reason, "an aggregate over an unknown column");
		}
		auto &column = table->GetColumns().GetColumn(LogicalIndex(index));
		auto col = Bracket(column.Name().GetIdentifierName());
		auto &col_type = column.Type();
		if (name == "count") {
			if (!CountableType(col_type)) {
				return Fail(reason, "count over a type the server cannot aggregate");
			}
			sql = "COUNT_BIG(" + col + ")";
			type = LogicalType::BIGINT;
			return true;
		}
		if (!ExactType(col_type)) {
			return Fail(reason, name + " over a type that is not exact");
		}
		if (name == "min" || name == "max") {
			sql = StringUtil::Upper(name) + "(" + col + ")";
			type = col_type;
			return true;
		}
		// sum: DuckDB widens an integer sum to HUGEINT and a decimal sum to DECIMAL(38, scale). SQL Server keeps
		// an INT sum an INT (overflowing past 2^31), so the integer case is summed as DECIMAL(38,0) — exact up
		// to 10^38, where HUGEINT goes to 1.7 x 10^38: between the two an answer on one side is an overflow
		// error on the other, never a different number.
		if (IntegerType(col_type)) {
			sql = "SUM(CAST(" + col + " AS DECIMAL(38,0)))";
			type = LogicalType::HUGEINT;
			return true;
		}
		if (col_type.id() == LogicalTypeId::DECIMAL) {
			sql = "SUM(" + col + ")";
			type = LogicalType::DECIMAL(38, DecimalType::GetScale(col_type));
			return true;
		}
		return Fail(reason, "sum over a date");
	}

	bool RenderPredicate(const ParsedExpression &expr, string &sql, string &reason) const {
		switch (expr.GetExpressionClass()) {
		case ExpressionClass::COMPARISON: {
			auto &cmp = expr.Cast<ComparisonExpression>();
			bool column_left = IsColumn(cmp.Left());
			auto &column_expr = (column_left ? cmp.Left() : cmp.Right()).Cast<ColumnRefExpression>();
			auto &constant_expr = column_left ? cmp.Right() : cmp.Left();
			idx_t index;
			if (!ResolveColumn(column_expr, index)) {
				return Fail(reason, "a WHERE column that is unknown");
			}
			auto &column = table->GetColumns().GetColumn(LogicalIndex(index));
			if (!ExactType(column.Type())) {
				return Fail(reason, "a WHERE column of a type that is not exact");
			}
			string literal;
			if (!RenderConstant(constant_expr, column.Type(), literal, reason)) {
				return false;
			}
			auto col = Bracket(column.Name().GetIdentifierName());
			auto op = ComparisonSql(expr.GetExpressionType());
			sql = "(" + (column_left ? col + " " + op + " " + literal : literal + " " + op + " " + col) + ")";
			return true;
		}
		case ExpressionClass::CONJUNCTION: {
			auto joiner = expr.GetExpressionType() == ExpressionType::CONJUNCTION_AND ? " AND " : " OR ";
			auto &children = expr.Cast<ConjunctionExpression>().GetChildren();
			string out;
			for (idx_t i = 0; i < children.size(); i++) {
				string child;
				if (!RenderPredicate(*children[i], child, reason)) {
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
				if (!RenderPredicate(*children[0], child, reason)) {
					return false;
				}
				sql = "(NOT " + child + ")";
				return true;
			}
			idx_t index;
			if (!ResolveColumn(children[0]->Cast<ColumnRefExpression>(), index)) {
				return Fail(reason, "an IS NULL column that is unknown");
			}
			// IS [NOT] NULL means the same for every type, so no exact-type gate here.
			auto col = Bracket(table->GetColumns().GetColumn(LogicalIndex(index)).Name().GetIdentifierName());
			sql = "(" + col +
			      (expr.GetExpressionType() == ExpressionType::OPERATOR_IS_NULL ? " IS NULL)" : " IS NOT NULL)");
			return true;
		}
		}
	}

	//! Evaluates the constant with DuckDB's own binder, converts it EXACTLY to the column's type, and renders a
	//! T-SQL literal of that type. A constant that does not convert exactly (1.5 against an INTEGER column, 300
	//! against a TINYINT) refuses: DuckDB would compare in a wider type, and the narrow literal would not.
	bool RenderConstant(const ParsedExpression &expr, const LogicalType &type, string &literal,
	                    string &reason) const {
		Value value;
		try {
			auto copy = expr.Copy();
			auto binder = Binder::CreateBinder(*context);
			ConstantBinder constant_binder(*binder, *context, "fabricator remote select");
			auto bound = constant_binder.Bind(copy);
			if (!bound || !bound->IsFoldable() || !ExpressionExecutor::TryEvaluateScalar(*context, *bound, value)) {
				return Fail(reason, "a WHERE value that is not a constant");
			}
		} catch (std::exception &) {
			return Fail(reason, "a WHERE value that does not bind as a constant");
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
			return Fail(reason, "a WHERE value that does not convert to the column type");
		}
		auto back = cast->DefaultTryCastAs(value.type(), &error, true);
		if (!back.has_value() || *back != value) {
			return Fail(reason, "a WHERE value that does not convert EXACTLY to the column type");
		}
		auto text = cast->ToString();
		switch (type.id()) {
		case LogicalTypeId::DECIMAL:
			literal = "CAST('" + text + "' AS DECIMAL(" + std::to_string(DecimalType::GetWidth(type)) + "," +
			          std::to_string(DecimalType::GetScale(type)) + "))";
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
	TableCatalogEntry *table = nullptr;
	Identifier alias;
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
	// Only a plain table: a catalog-bound table function (a TVF, a stored procedure) would otherwise be pushed
	// on its own, and it already has a path of its own.
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
	auto &ref = select.from_table->Cast<BaseTableRef>();
	auto &qualified = ref.GetQualifiedName();
	auto schema = qualified.Schema().empty() ? Identifier(GetDefaultSchema().value_or(Identifier("dbo")))
	                                         : qualified.Schema();

	string sql;
	vector<std::pair<Identifier, LogicalType>> outputs;
	string reason;
	optional_ptr<CatalogEntry> entry;
	try {
		entry = GetEntry(context, CatalogType::TABLE_ENTRY, schema, ref.Table(), OnEntryNotFound::RETURN_NULL);
	} catch (std::exception &) {
		entry = nullptr;
	}
	bool pushed = entry && entry->type == CatalogType::TABLE_ENTRY &&
	              RemoteSelect(select).Render(context, entry->Cast<TableCatalogEntry>(), sql, outputs, reason);
	if (!entry || entry->type != CatalogType::TABLE_ENTRY) {
		reason = "the table did not resolve";
	}

	auto statement = make_uniq<SelectStatement>();
	if (!pushed) {
		// DECLINE: hand the statement back with this catalog's name restored (the optimizer stripped it), so it
		// binds and runs locally exactly as the user wrote it.
		RemoteTrace("local: " + reason);
		ref.SetQualifiedName(GetName(), schema, ref.Table());
		statement->node = std::move(node);
		return make_uniq<SubqueryRef>(std::move(statement));
	}
	RemoteTrace("remote: " + sql);

	// SELECT CAST(c0 AS <duckdb type>) AS <duckdb name>, … FROM fabricator_query('<catalog>', '<t-sql>')
	vector<unique_ptr<ParsedExpression>> args;
	args.push_back(ConstantExpression::String(GetName().GetIdentifierName()));
	args.push_back(ConstantExpression::String(sql));
	auto function = make_uniq<TableFunctionRef>();
	function->function = make_uniq<FunctionExpression>(Identifier("fabricator_query"), std::move(args));
	auto wrapper = make_uniq<SelectNode>();
	for (idx_t i = 0; i < outputs.size(); i++) {
		auto column = make_uniq<ColumnRefExpression>(Identifier("c" + std::to_string(i)));
		auto cast = make_uniq<CastExpression>(outputs[i].second, std::move(column));
		cast->SetAlias(outputs[i].first);
		wrapper->select_list.push_back(std::move(cast));
	}
	wrapper->from_table = std::move(function);
	statement->node = std::move(wrapper);
	return make_uniq<SubqueryRef>(std::move(statement));
}

} // namespace duckdb
