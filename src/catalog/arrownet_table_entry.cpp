//===----------------------------------------------------------------------===//
//                         arrownet — table catalog entry (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_table_entry.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "duckdb/catalog/entry_lookup_info.hpp"
#include "duckdb/common/column_index.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/logging/logger.hpp"
#include "duckdb/planner/tableref/bound_at_clause.hpp"
#include "duckdb/planner/expression/bound_between_expression.hpp"
#include "duckdb/planner/expression/bound_columnref_expression.hpp"
#include "duckdb/planner/expression/bound_comparison_expression.hpp"
#include "duckdb/planner/expression/bound_conjunction_expression.hpp"
#include "duckdb/planner/expression/bound_constant_expression.hpp"
#include "duckdb/planner/expression/bound_function_expression.hpp"
#include "duckdb/planner/expression/bound_operator_expression.hpp"
#include "duckdb/function/scalar/struct_utils.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
#include "duckdb/storage/statistics/base_statistics.hpp"
#include "duckdb/storage/statistics/node_statistics.hpp"
#include "duckdb/storage/table_storage_info.hpp"

namespace duckdb {

namespace {

// Serializes the superset-safe subset of a DuckDB filter expression tree into the
// pushdown predicate JSON (FilterNode), collecting constants into `constants` (the
// JSON references them by index). "Superset-safe" = a row that truly matches always
// passes the emitted SQL; pushdown is best-effort (DuckDB re-applies every filter),
// so over-approximation is correct, under-approximation is not.
class FilterSerializer {
public:
	// `string_order_pushable` = the source orders strings the same way DuckDB does for these scans (a binary
	// `_BIN/_BIN2` SQL collation, or a byte-ordered source like Parquet/Delta statistics). When true, string
	// ordering comparisons (`<` `<=` `>` `>=` `<>` `is_distinct`) and string BETWEEN are superset-safe to push;
	// otherwise only string equality (`=`/`is_not_distinct`, superset-safe under any collation) is pushed.
	FilterSerializer(LogicalGet &get, vector<Value> &constants, bool string_order_pushable)
	    : get_(get), constants_(constants), string_order_pushable_(string_order_pushable) {
	}

	// Returns true and fills `out` (a JSON object) + `sql` (an equivalent DuckDB SQL predicate, literals
	// inlined) iff `e` was fully serialized. `sql` is 1:1 with `out` and only consumed by the native
	// (read_parquet-on-DuckDB) path; both are always produced together so they cannot diverge.
	bool Serialize(const Expression &e, string &out, string &sql) {
		switch (e.GetExpressionClass()) {
		case ExpressionClass::BOUND_COMPARISON:
			return Comparison(e.Cast<BoundComparisonExpression>(), out, sql);
		case ExpressionClass::BOUND_OPERATOR:
			return Operator(e.Cast<BoundOperatorExpression>(), out, sql);
		case ExpressionClass::BOUND_CONJUNCTION:
			return Conjunction(e.Cast<BoundConjunctionExpression>(), out, sql);
		case ExpressionClass::BOUND_BETWEEN:
			return Between(e.Cast<BoundBetweenExpression>(), out, sql);
		default:
			return false;
		}
	}

private:
	static void JsonStr(const string &s, string &out) {
		out += '"';
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
				out += c;
			}
		}
		out += '"';
	}

	// A DuckDB double-quoted identifier ("col", with any embedded " doubled) for the native SQL WHERE.
	static void SqlIdent(const string &s, string &out) {
		out += '"';
		for (char c : s) {
			if (c == '"') {
				out += "\"\"";
			} else {
				out += c;
			}
		}
		out += '"';
	}

	// The comparison operator as it appears in the native SQL predicate (between column and literal).
	// nullptr for a token we don't emit as SQL.
	static const char *CmpSqlToken(const char *json_tok) {
		string t(json_tok);
		if (t == "is_distinct") {
			return "IS DISTINCT FROM";
		}
		if (t == "is_not_distinct") {
			return "IS NOT DISTINCT FROM";
		}
		return json_tok; // = <> < > <= >= are identical in DuckDB SQL
	}

	// Resolve a column-ref expression to its provider column name.
	bool ColumnName(const Expression &e, string &name, LogicalType &type) {
		if (e.GetExpressionClass() != ExpressionClass::BOUND_COLUMN_REF) {
			return false;
		}
		auto &ref = e.Cast<BoundColumnRefExpression>();
		auto &cols = get_.GetColumnIds();
		if (ref.binding.column_index >= cols.size()) {
			return false;
		}
		auto &ci = cols[ref.binding.column_index];
		if (!ci.HasPrimaryIndex() || ci.HasChildren()) {
			return false; // struct sub-field / non-numeric ref: not pushed
		}
		auto table_col = ci.GetPrimaryIndex();
		if (table_col >= get_.names.size()) {
			return false;
		}
		name = get_.names[table_col];
		type = ref.return_type;
		return true;
	}

	// Resolve a column ref OR a struct_extract(...) chain over one to its provider column PATH
	// (path[0] = the top-level column name, then the member names down to the leaf) + the LEAF type.
	// `WHERE (s).a = 5` arrives here as struct_extract(s, 'a') = 5 (bind_columnref/CreateStructExtract);
	// nested access nests the calls. Member names are resolved from the child struct type via the bound
	// extract index (StructExtractBindData) — exact case, matching parquet/stats keys — which also covers
	// struct_extract_at's positional form.
	bool ColumnPath(const Expression &e, vector<string> &path, LogicalType &type) {
		if (e.GetExpressionClass() == ExpressionClass::BOUND_FUNCTION) {
			auto &func = e.Cast<BoundFunctionExpression>();
			if ((func.function.name != "struct_extract" && func.function.name != "struct_extract_at") ||
			    func.children.size() != 2 || !func.bind_info) {
				return false;
			}
			if (!ColumnPath(*func.children[0], path, type)) {
				return false;
			}
			auto &parent_type = func.children[0]->return_type;
			if (parent_type.id() != LogicalTypeId::STRUCT) {
				return false; // VARIANT member access etc.: not a plain struct path
			}
			auto member = func.bind_info->Cast<StructExtractBindData>().index;
			auto &members = StructType::GetChildTypes(parent_type);
			if (member >= members.size()) {
				return false;
			}
			path.push_back(members[member].first);
			type = e.return_type;
			return true;
		}
		string name;
		if (!ColumnName(e, name, type)) {
			return false;
		}
		path.clear();
		path.push_back(std::move(name));
		return true;
	}

	// Emit the column reference into the JSON object body: a plain column as `"col":"name"`, a
	// struct-member path as `"path":["s","a"]` with NO "col" — a renderer without path support then
	// throws/skips (falls back to no pushdown) instead of mis-rendering the top-level column.
	static void JsonColumnRef(const vector<string> &path, string &out) {
		if (path.size() == 1) {
			out += "\"col\":";
			JsonStr(path[0], out);
			return;
		}
		out += "\"path\":[";
		for (idx_t i = 0; i < path.size(); i++) {
			if (i) {
				out += ',';
			}
			JsonStr(path[i], out);
		}
		out += ']';
	}

	// Comparison operator -> JSON token; "" if not a pushable comparison.
	static const char *CmpToken(ExpressionType t) {
		switch (t) {
		case ExpressionType::COMPARE_EQUAL:
			return "=";
		case ExpressionType::COMPARE_NOTEQUAL:
			return "<>";
		case ExpressionType::COMPARE_LESSTHAN:
			return "<";
		case ExpressionType::COMPARE_GREATERTHAN:
			return ">";
		case ExpressionType::COMPARE_LESSTHANOREQUALTO:
			return "<=";
		case ExpressionType::COMPARE_GREATERTHANOREQUALTO:
			return ">=";
		case ExpressionType::COMPARE_DISTINCT_FROM:
			return "is_distinct";
		case ExpressionType::COMPARE_NOT_DISTINCT_FROM:
			return "is_not_distinct";
		default:
			return nullptr;
		}
	}

	static ExpressionType Flip(ExpressionType t) {
		switch (t) {
		case ExpressionType::COMPARE_LESSTHAN:
			return ExpressionType::COMPARE_GREATERTHAN;
		case ExpressionType::COMPARE_GREATERTHAN:
			return ExpressionType::COMPARE_LESSTHAN;
		case ExpressionType::COMPARE_LESSTHANOREQUALTO:
			return ExpressionType::COMPARE_GREATERTHANOREQUALTO;
		case ExpressionType::COMPARE_GREATERTHANOREQUALTO:
			return ExpressionType::COMPARE_LESSTHANOREQUALTO;
		default:
			return t; // equal / not-equal / (not-)distinct are symmetric
		}
	}

	// DuckDB string comparison is exact (case+accent sensitive); SQL Server uses the
	// column collation, which for `=`/IN is typically LOOSER (=> superset, safe) but
	// for ordering / `<>` / IS DISTINCT can be a strict subset (=> drops rows). So for
	// VARCHAR push only the positive-equality shapes.
	bool SafeForType(const char *cmp, const LogicalType &type) const {
		if (type.id() != LogicalTypeId::VARCHAR) {
			return true; // numeric / temporal / bool / blob / uuid: exact
		}
		// Equality is superset-safe under ANY string collation (a case-insensitive source returns a superset;
		// DuckDB re-applies). Ordering / not-equal match DuckDB only when the source is byte/binary-ordered.
		string c(cmp);
		if (c == "=" || c == "is_not_distinct") {
			return true;
		}
		return string_order_pushable_;
	}

	idx_t AddConstant(const Value &v) {
		idx_t idx = constants_.size();
		constants_.push_back(v);
		return idx;
	}

	bool Comparison(const BoundComparisonExpression &cmp, string &out, string &sql) {
		auto type = cmp.GetExpressionType();
		const Expression *col = nullptr;
		const Expression *constant = nullptr;
		bool flipped = false;
		auto lc = cmp.left->GetExpressionClass();
		auto rc = cmp.right->GetExpressionClass();
		// Column side = a plain column ref OR a struct_extract chain (ColumnPath validates the shape).
		auto is_col_side = [](ExpressionClass c) {
			return c == ExpressionClass::BOUND_COLUMN_REF || c == ExpressionClass::BOUND_FUNCTION;
		};
		if (is_col_side(lc) && rc == ExpressionClass::BOUND_CONSTANT) {
			col = cmp.left.get();
			constant = cmp.right.get();
		} else if (is_col_side(rc) && lc == ExpressionClass::BOUND_CONSTANT) {
			col = cmp.right.get();
			constant = cmp.left.get();
			flipped = true;
		} else {
			return false;
		}
		vector<string> path;
		LogicalType coltype;
		if (!ColumnPath(*col, path, coltype)) {
			return false;
		}
		if (flipped) {
			type = Flip(type);
		}
		const char *tok = CmpToken(type);
		if (!tok || !SafeForType(tok, coltype)) {
			return false;
		}
		auto &value = constant->Cast<BoundConstantExpression>().value;
		if (value.IsNull()) {
			return false; // `col <op> NULL` is unknown; leave it to DuckDB
		}
		idx_t idx = AddConstant(value);
		out = "{\"op\":\"compare\",\"cmp\":\"";
		out += tok;
		out += "\",";
		JsonColumnRef(path, out);
		out += ",\"val\":" + to_string(idx) + "}";
		sql.clear();
		// Struct-member predicates get NO native-SQL twin: the native read runs over the provider's storage
		// layout, where a column-mapped table's nested children carry PHYSICAL names — a rendered logical
		// member access would mis-bind the per-file query. The JSON path (stats pruning) is layout-translated
		// by the provider; applying fewer predicates as SQL is always superset-safe.
		if (path.size() == 1) {
			SqlIdent(path[0], sql);
			sql += ' ';
			sql += CmpSqlToken(tok);
			sql += ' ';
			sql += value.ToSQLString();
		}
		return true;
	}

	bool Operator(const BoundOperatorExpression &op, string &out, string &sql) {
		auto type = op.GetExpressionType();
		if (type == ExpressionType::OPERATOR_IS_NULL || type == ExpressionType::OPERATOR_IS_NOT_NULL) {
			if (op.children.size() != 1) {
				return false;
			}
			vector<string> path;
			LogicalType coltype;
			if (!ColumnPath(*op.children[0], path, coltype)) {
				return false;
			}
			out = "{\"op\":\"";
			out += type == ExpressionType::OPERATOR_IS_NULL ? "is_null" : "is_not_null";
			out += "\",";
			JsonColumnRef(path, out);
			out += "}";
			sql.clear();
			if (path.size() == 1) { // struct-member: JSON only (see Comparison)
				SqlIdent(path[0], sql);
				sql += type == ExpressionType::OPERATOR_IS_NULL ? " IS NULL" : " IS NOT NULL";
			}
			return true;
		}
		if (type == ExpressionType::COMPARE_IN) {
			// children[0] = column, children[1..] = constants (IN-rewriting runs after
			// filter pushdown, so IN is still a single operator here). IN is positive
			// equality => superset-safe for all types (incl VARCHAR).
			if (op.children.size() < 2) {
				return false;
			}
			vector<string> path;
			LogicalType coltype;
			if (!ColumnPath(*op.children[0], path, coltype)) {
				return false;
			}
			vector<Value> values;
			for (idx_t i = 1; i < op.children.size(); i++) {
				if (op.children[i]->GetExpressionClass() != ExpressionClass::BOUND_CONSTANT) {
					return false;
				}
				auto &v = op.children[i]->Cast<BoundConstantExpression>().value;
				if (v.IsNull()) {
					return false;
				}
				values.push_back(v);
			}
			bool emit_sql = path.size() == 1; // struct-member: JSON only (see Comparison)
			out = "{\"op\":\"in\",";
			JsonColumnRef(path, out);
			out += ",\"vals\":[";
			sql.clear();
			if (emit_sql) {
				SqlIdent(path[0], sql);
				sql += " IN (";
			}
			for (idx_t i = 0; i < values.size(); i++) {
				if (i) {
					out += ',';
					if (emit_sql) {
						sql += ", ";
					}
				}
				out += to_string(AddConstant(values[i]));
				if (emit_sql) {
					sql += values[i].ToSQLString();
				}
			}
			out += "]}";
			if (emit_sql) {
				sql += ')';
			}
			return true;
		}
		return false; // OPERATOR_NOT etc.: leave to DuckDB
	}

	bool Conjunction(const BoundConjunctionExpression &conj, string &out, string &sql) {
		auto type = conj.GetExpressionType();
		bool is_and = type == ExpressionType::CONJUNCTION_AND;
		bool is_or = type == ExpressionType::CONJUNCTION_OR;
		if (!is_and && !is_or) {
			return false;
		}
		vector<string> parts;
		vector<string> sql_parts;
		for (auto &child : conj.children) {
			string js, cs;
			if (Serialize(*child, js, cs)) {
				parts.push_back(std::move(js));
				sql_parts.push_back(std::move(cs));
			} else if (is_or) {
				return false; // OR is all-or-nothing (dropping a branch would be a subset)
			}
			// AND: dropping a branch only widens the result (superset), so partial is OK.
		}
		if (parts.empty()) {
			return false;
		}
		if (parts.size() == 1) {
			out = parts[0];
			sql = sql_parts[0];
			return true;
		}
		out = "{\"op\":\"";
		out += is_and ? "and" : "or";
		out += "\",\"children\":[";
		for (idx_t i = 0; i < parts.size(); i++) {
			if (i) {
				out += ',';
			}
			out += parts[i];
		}
		out += "]}";
		// The SQL twin per child may be EMPTY (struct-member predicates are JSON-only): AND may skip an
		// empty child (fewer predicates = superset); OR must drop the whole disjunction (a narrowed branch
		// would be a subset). The JSON above is unaffected.
		sql.clear();
		vector<string> sqls;
		for (auto &cs : sql_parts) {
			if (!cs.empty()) {
				sqls.push_back(cs);
			} else if (is_or) {
				return true; // JSON emitted; no SQL twin for the OR
			}
		}
		if (sqls.empty()) {
			return true;
		}
		if (sqls.size() == 1) {
			sql = sqls[0];
			return true;
		}
		const char *joiner = is_and ? " AND " : " OR ";
		sql = "(";
		for (idx_t i = 0; i < sqls.size(); i++) {
			if (i) {
				sql += joiner;
			}
			sql += sqls[i];
		}
		sql += ')';
		return true;
	}

	bool Between(const BoundBetweenExpression &b, string &out, string &sql) {
		vector<string> path;
		LogicalType coltype;
		if (!ColumnPath(*b.input, path, coltype)) {
			return false;
		}
		if (coltype.id() == LogicalTypeId::VARCHAR && !string_order_pushable_) {
			return false; // string range: collation-dependent ordering, not superset-safe (unless byte-ordered)
		}
		if (b.lower->GetExpressionClass() != ExpressionClass::BOUND_CONSTANT ||
		    b.upper->GetExpressionClass() != ExpressionClass::BOUND_CONSTANT) {
			return false;
		}
		auto &lo = b.lower->Cast<BoundConstantExpression>().value;
		auto &hi = b.upper->Cast<BoundConstantExpression>().value;
		if (lo.IsNull() || hi.IsNull()) {
			return false;
		}
		idx_t lo_idx = AddConstant(lo);
		idx_t hi_idx = AddConstant(hi);
		const char *lo_op = b.lower_inclusive ? ">=" : ">";
		const char *hi_op = b.upper_inclusive ? "<=" : "<";
		out = "{\"op\":\"and\",\"children\":[{\"op\":\"compare\",\"cmp\":\"";
		out += lo_op;
		out += "\",";
		JsonColumnRef(path, out);
		out += ",\"val\":" + to_string(lo_idx) + "},{\"op\":\"compare\",\"cmp\":\"";
		out += hi_op;
		out += "\",";
		JsonColumnRef(path, out);
		out += ",\"val\":" + to_string(hi_idx) + "}]}";
		sql.clear();
		if (path.size() == 1) { // struct-member: JSON only (see Comparison)
			sql = "(";
			SqlIdent(path[0], sql);
			sql += ' ';
			sql += lo_op;
			sql += ' ';
			sql += lo.ToSQLString();
			sql += " AND ";
			SqlIdent(path[0], sql);
			sql += ' ';
			sql += hi_op;
			sql += ' ';
			sql += hi.ToSQLString();
			sql += ')';
		}
		return true;
	}

	LogicalGet &get_;
	vector<Value> &constants_;
	bool string_order_pushable_;
};

} // namespace

// pushdown_complex_filter: serialize the superset-safe predicates into bind_data and
// LEAVE every expression in `filters` (best-effort) so DuckDB still applies them all.
// Shared with the table-function scan (arrownet_schema_entry.cpp); declared in
// arrownet_table_entry.hpp.
void ArrowNetComplexFilterPushdown(ClientContext &context, LogicalGet &get, FunctionData *bind_data_p,
                                   vector<unique_ptr<Expression>> &filters) {
	auto &bind_data = bind_data_p->Cast<arrownet::ArrowStreamBindData>();

	// Diagnostic (duckdb_logs, type 'ArrowNet.Pushdown'): DuckDB may call this callback MORE THAN ONCE per
	// plan (e.g. once with the static predicates, again as dynamic/join filters materialize), and we
	// clear+rebuild each time — so this logs the incoming expression list + what we serialized, to make the
	// call pattern (count, static vs later, replace-vs-merge) observable. Gated by ShouldLog so it's free when
	// logging is off. See docs/multifile-delta.md §"Batch 2".
	auto log_result = [&](idx_t serialized) {
		auto &logger = Logger::Get(context);
		if (!logger.ShouldLog("ArrowNet.Pushdown", LogLevel::LOG_INFO)) {
			return;
		}
		string exprs;
		for (auto &f : filters) {
			if (!exprs.empty()) {
				exprs += " ; ";
			}
			exprs += f->ToString();
		}
		logger.WriteLog("ArrowNet.Pushdown", LogLevel::LOG_INFO,
		                StringUtil::Format("pushdown_complex_filter: %llu expr in [%s] -> %llu pushed; native_filter=[%s]",
		                                   (unsigned long long)filters.size(), exprs.c_str(),
		                                   (unsigned long long)serialized, bind_data.native_filter_sql.c_str()));
	};

	if (filters.empty()) {
		// A later optimizer round can re-invoke this callback with an EMPTY list — under exact mode
		// (filter_pushdown=true) the first round's predicates were ERASED into the TableFilterSet, so
		// they no longer appear here. KEEP the previous serialization: those predicates are still the
		// query's own (and still applied via the erased set / re-applied by DuckDB), so pruning with
		// them stays superset-correct — clearing would silently forfeit file/row-group pruning on every
		// exact-mode scan.
		log_result(0);
		return;
	}
	bind_data.filter_json.clear();
	bind_data.filter_constants.clear();
	bind_data.native_filter_sql.clear();
	FilterSerializer ser(get, bind_data.filter_constants, bind_data.string_order_pushable);
	vector<string> parts;
	vector<string> sql_parts;
	for (auto &f : filters) { // do NOT erase — DuckDB re-applies them
		string js, cs;
		if (ser.Serialize(*f, js, cs)) {
			parts.push_back(std::move(js));
			sql_parts.push_back(std::move(cs));
		}
	}
	if (parts.empty()) {
		bind_data.filter_constants.clear();
		log_result(0);
		return;
	}
	// The filters vector is an implicit AND (both the JSON tree and the native SQL WHERE). A part's SQL
	// twin may be EMPTY (struct-member predicates are JSON-only) — an AND may simply skip it (fewer
	// predicates applied = superset; DuckDB re-applies everything).
	if (parts.size() == 1) {
		bind_data.filter_json = parts[0];
		bind_data.native_filter_sql = sql_parts[0];
		log_result(1);
		return;
	}
	string json = "{\"op\":\"and\",\"children\":[";
	string sql;
	for (idx_t i = 0; i < parts.size(); i++) {
		if (i) {
			json += ',';
		}
		json += parts[i];
		if (!sql_parts[i].empty()) {
			if (!sql.empty()) {
				sql += " AND ";
			}
			sql += sql_parts[i];
		}
	}
	json += "]}";
	bind_data.filter_json = std::move(json);
	bind_data.native_filter_sql = std::move(sql);
	log_result(parts.size());
}

ArrowNetTableEntry::ArrowNetTableEntry(Catalog &catalog, SchemaCatalogEntry &schema, CreateTableInfo &info,
                                       ArrowNetHandle handle, vector<idx_t> rowid_columns, LogicalType rowid_type,
                                       vector<string> virtual_rowid_columns,
                                       vector<std::pair<string, LogicalType>> provider_virtual_columns)
    : TableCatalogEntry(catalog, schema, info), handle_(handle), rowid_columns_(std::move(rowid_columns)),
      virtual_rowid_columns_(std::move(virtual_rowid_columns)),
      provider_virtual_columns_(std::move(provider_virtual_columns)), rowid_type_(std::move(rowid_type)) {
}

// NOTE on struct filters under exact mode (filter_pushdown=true): a `WHERE (s).a = 5` becomes an
// erased StructFilter in the TableFilterSet, which the scan MUST apply — RenderTableFilter
// (arrow_ingest.cpp) renders it as struct_extract SQL. DuckDB's `supports_pushdown_type` veto (pull
// struct filters back out of the scan) was tried and REJECTED: it requires `filter_prune`-maintained
// projection_ids, and DuckDB's veto path (plan_get.cpp) corrupts rowid DML plans whose projection_ids
// are empty (RemoveUnusedColumns early-outs on everything_referenced) and crashes on rowid entries
// (function-level virtual_columns.at) — upstream only pairs the veto with the DML-less arrow scan.

// Cardinality callback: hands the optimizer the table's approximate row count so
// join ordering has a real estimate. Unknown (-1) => no statistics reported.
static unique_ptr<NodeStatistics> ArrowNetScanCardinality(ClientContext &context, const FunctionData *bind_data_p) {
	auto &bind_data = bind_data_p->Cast<arrownet::ArrowStreamBindData>();
	if (bind_data.row_count < 0) {
		return nullptr;
	}
	return make_uniq<NodeStatistics>(static_cast<idx_t>(bind_data.row_count));
}

// Per-column statistics callback: reports ONLY the distinct-value estimate (NDV) for
// the optimizer's selectivity. min/max is deliberately left UNKNOWN (CreateUnknown):
// DuckDB prunes filters on min/max (FILTER_ALWAYS_FALSE), and SQL Server's sampled,
// possibly-stale stats are not exact bounds on a live table — so reporting them could
// drop rows. NDV only affects cardinality estimation, never correctness.
static unique_ptr<BaseStatistics> ArrowNetScanStatistics(ClientContext &context, const FunctionData *bind_data_p,
                                                         column_t column_index) {
	auto &bind_data = bind_data_p->Cast<arrownet::ArrowStreamBindData>();
	if (column_index >= bind_data.column_ndv.size() || bind_data.column_ndv[column_index] <= 0 ||
	    column_index >= bind_data.return_types.size()) {
		return nullptr;
	}
	auto stats = BaseStatistics::CreateUnknown(bind_data.return_types[column_index]);
	stats.SetDistinctCount(static_cast<idx_t>(bind_data.column_ndv[column_index]));
	return make_uniq<BaseStatistics>(std::move(stats));
}

// Function-level rowid hook for DuckDB's late-materialization rewrite (distinct from the entry-level
// GetRowIdColumns, which serves DML planning): declares that this scan's row identity is the standard
// rowid virtual column. Only installed (with function.late_materialization) for scans whose rowid the
// provider can filter FAST — see BuildScanFunction.
static vector<column_t> ArrowNetScanRowIdColumns(ClientContext &context, optional_ptr<FunctionData> bind_data_p) {
	vector<column_t> result;
	result.emplace_back(COLUMN_IDENTIFIER_ROW_ID);
	return result;
}

TableFunction ArrowNetTableEntry::GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data) {
	return BuildScanFunction(context, bind_data, nullptr);
}

// Time-travel overload: DuckDB binds `FROM t AT (...)` and hands us the bound clause via the lookup info.
TableFunction ArrowNetTableEntry::GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data,
                                                  const EntryLookupInfo &lookup_info) {
	return BuildScanFunction(context, bind_data, lookup_info.GetAtClause());
}

TableFunction ArrowNetTableEntry::BuildScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data,
                                                    optional_ptr<BoundAtClause> at_clause) {
	auto data = make_uniq<arrownet::ArrowStreamBindData>();
	auto handle = handle_;
	// The managed side builds the provider SELECT for the whole table.
	string schema_name = schema.name;
	string table_name = name;

	// Approximate row count for the optimizer (fetched once, cached on the entry).
	// A stats failure (e.g. missing VIEW DATABASE STATE) must not break the scan.
	if (row_count_ == -2) {
		try {
			row_count_ = FetchRowCount(handle_, schema_name, table_name);
		} catch (...) {
			row_count_ = -1;
		}
	}
	data->row_count = row_count_;

	// Per-column NDV for selectivity (fetched once, cached). Best-effort: a stats
	// failure leaves all columns unknown. Aligned to the column order by name.
	if (!ndv_fetched_) {
		ndv_fetched_ = true;
		try {
			column_ndv_ = FetchColumnNdv(handle_, schema_name, table_name);
		} catch (...) {
			column_ndv_.clear();
		}
	}
	data->factory = [handle, schema_name, table_name](const arrownet::ArrowScanRequest &req, ArrowArrayStream &out) {
		arrownet::ScanTable(handle, schema_name, table_name, req.spec_json, req.filter_values, out);
	};
	data->push_projection = true; // push the projected column list (and later, filters) to SQL
	// String-keyed ORDER BY may be pushed only under a binary database collation (byte-order sort ==
	// DuckDB); the optimizer's TopN pushdown reads this. Detected once at LoadCatalog.
	data->string_order_pushable = ParentCatalog().Cast<ArrowNetCatalog>().StringOrderPushable();

	// Build the Arrow column converters + verify the result schema.
	vector<LogicalType> return_types;
	vector<string> names;
	arrownet::PopulateReturnSchema(context, *data, return_types, names);

	// Align the cached NDV map to the column order (parallel to names; -1 = unknown).
	data->column_ndv.assign(data->names.size(), -1);
	for (idx_t i = 0; i < data->names.size(); i++) {
		auto it = column_ndv_.find(data->names[i]);
		if (it != column_ndv_.end()) {
			data->column_ndv[i] = it->second;
		}
	}

	// Propagate rowid info so the scan can synthesize the rowid column.
	data->rowid_source_columns = rowid_columns_;
	data->virtual_rowid_columns = virtual_rowid_columns_; // Delta `_metadata.row_id` (not a user column)
	data->provider_virtual_columns = provider_virtual_columns_; // stable __delta_row_id / _commit_version
	data->rowid_type = rowid_type_;
	data->table = this; // lets LogicalGet::GetTable() resolve (UPDATE/DELETE)

	// Time travel: record `FROM t AT (...)` (a bind-time constant) so the scan spec carries it to the
	// provider (SQL Server: FOR SYSTEM_TIME AS OF for "timestamp"; "version" is rejected managed-side).
	if (at_clause) {
		data->at_unit = at_clause->Unit();
		data->at_value = at_clause->GetValue().ToString();
	}

	bind_data = std::move(data);

	TableFunction function("arrownet_scan", {}, arrownet::ArrowStreamScan, nullptr, arrownet::ArrowStreamInitGlobal,
	                       arrownet::ArrowStreamInitLocal);
	function.projection_pushdown = true;
	// Best-effort filter pushdown: the callback serializes superset-safe predicates
	// and leaves them in place, so DuckDB still applies every filter (correctness).
	function.pushdown_complex_filter = ArrowNetComplexFilterPushdown;
	// filter_pushdown is normally FALSE — DuckDB's TableFilterSet path REMOVES the pushed filters from the
	// plan (trusts the scan to apply them), which is unsafe for our best-effort/superset providers. Enable it
	// ONLY when the provider applies pushed filters EXACTLY (the Delta native_read catalog: every scan reads
	// via read_parquet on the host DuckDB, 1:1). That also makes DuckDB deliver runtime dynamic (join) filters
	// to the scan (arrow_ingest renders the live TableFilterSet into the native WHERE). SQL Server / DAX /
	// non-native Delta keep it false (unchanged). See docs/multifile-delta.md §"Batch 2 slice 2".
	if (ParentCatalog().Cast<ArrowNetCatalog>().ExactFilterPushdown()) {
		function.filter_pushdown = true;
		// Late materialization (ORDER BY ... LIMIT n → TopN on a narrow scan + SEMI-join back on rowid):
		// profitable ONLY here — the join's dynamic rowid filter decodes in the native reader to exact
		// (fileOrdinal → file selection, position → file_row_number row-group skip), so the fetch side is
		// O(matched files), not a second full scan. Requires the single virtual BIGINT rowid (Delta
		// `_metadata.row_id`); SQL Server / DAX stay off (their TopN pushdown is superior, and a join-back
		// would re-scan the server). NOTE: the rewrite clones the bind data (ArrowStreamBindData::Copy).
		if (HasVirtualRowId() && rowid_type_.id() == LogicalTypeId::BIGINT) {
			function.late_materialization = true;
			function.get_row_id_columns = ArrowNetScanRowIdColumns;
		}
	}
	function.cardinality = ArrowNetScanCardinality;
	function.statistics = ArrowNetScanStatistics;
	function.get_bind_info = arrownet::ArrowStreamGetBindInfo;
	return function;
}

unique_ptr<BaseStatistics> ArrowNetTableEntry::GetStatistics(ClientContext &context, column_t column_id) {
	return nullptr;
}

TableStorageInfo ArrowNetTableEntry::GetStorageInfo(ClientContext &context) {
	return TableStorageInfo();
}

virtual_column_map_t ArrowNetTableEntry::GetVirtualColumns() const {
	virtual_column_map_t result;
	if (HasRowId()) {
		// Expose a rowid backed by the PK / unique-index columns (SQL Server) or a virtual
		// provider column (Delta `_metadata.row_id`).
		result.insert(make_pair(COLUMN_IDENTIFIER_ROW_ID, TableColumn("rowid", rowid_type_)));
	}
	// Provider-declared virtual columns (queryable by name, excluded from SELECT *) — e.g. the Delta
	// catalog's stable __delta_row_id / __delta_row_commit_version. A REAL column with the same name
	// shadows the virtual one (TableBinding only maps a virtual name that isn't already taken).
	for (idx_t i = 0; i < provider_virtual_columns_.size(); i++) {
		result.insert(make_pair(arrownet::ProviderVirtualBase() + i,
		                        TableColumn(provider_virtual_columns_[i].first, provider_virtual_columns_[i].second)));
	}
	// Otherwise no virtual columns (no DuckDB rowid) — scans then don't require
	// projection pushdown for the virtual column.
	return result;
}

vector<column_t> ArrowNetTableEntry::GetRowIdColumns() const {
	vector<column_t> result;
	if (HasRowId()) {
		result.push_back(COLUMN_IDENTIFIER_ROW_ID);
	}
	return result;
}

} // namespace duckdb
