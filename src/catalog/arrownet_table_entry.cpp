//===----------------------------------------------------------------------===//
//                         arrownet — table catalog entry (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_table_entry.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "duckdb/common/column_index.hpp"
#include "duckdb/planner/expression/bound_between_expression.hpp"
#include "duckdb/planner/expression/bound_columnref_expression.hpp"
#include "duckdb/planner/expression/bound_comparison_expression.hpp"
#include "duckdb/planner/expression/bound_conjunction_expression.hpp"
#include "duckdb/planner/expression/bound_constant_expression.hpp"
#include "duckdb/planner/expression/bound_operator_expression.hpp"
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
	FilterSerializer(LogicalGet &get, vector<Value> &constants) : get_(get), constants_(constants) {
	}

	// Returns true and fills `out` (a JSON object) iff `e` was fully serialized.
	bool Serialize(const Expression &e, string &out) {
		switch (e.GetExpressionClass()) {
		case ExpressionClass::BOUND_COMPARISON:
			return Comparison(e.Cast<BoundComparisonExpression>(), out);
		case ExpressionClass::BOUND_OPERATOR:
			return Operator(e.Cast<BoundOperatorExpression>(), out);
		case ExpressionClass::BOUND_CONJUNCTION:
			return Conjunction(e.Cast<BoundConjunctionExpression>(), out);
		case ExpressionClass::BOUND_BETWEEN:
			return Between(e.Cast<BoundBetweenExpression>(), out);
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
	static bool SafeForType(const char *cmp, const LogicalType &type) {
		if (type.id() != LogicalTypeId::VARCHAR) {
			return true; // numeric / temporal / bool / blob / uuid: exact
		}
		return string(cmp) == "=" || string(cmp) == "is_not_distinct";
	}

	idx_t AddConstant(const Value &v) {
		idx_t idx = constants_.size();
		constants_.push_back(v);
		return idx;
	}

	bool Comparison(const BoundComparisonExpression &cmp, string &out) {
		auto type = cmp.GetExpressionType();
		const Expression *col = nullptr;
		const Expression *constant = nullptr;
		bool flipped = false;
		auto lc = cmp.left->GetExpressionClass();
		auto rc = cmp.right->GetExpressionClass();
		if (lc == ExpressionClass::BOUND_COLUMN_REF && rc == ExpressionClass::BOUND_CONSTANT) {
			col = cmp.left.get();
			constant = cmp.right.get();
		} else if (rc == ExpressionClass::BOUND_COLUMN_REF && lc == ExpressionClass::BOUND_CONSTANT) {
			col = cmp.right.get();
			constant = cmp.left.get();
			flipped = true;
		} else {
			return false;
		}
		string name;
		LogicalType coltype;
		if (!ColumnName(*col, name, coltype)) {
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
		out += "\",\"col\":";
		JsonStr(name, out);
		out += ",\"val\":" + to_string(idx) + "}";
		return true;
	}

	bool Operator(const BoundOperatorExpression &op, string &out) {
		auto type = op.GetExpressionType();
		if (type == ExpressionType::OPERATOR_IS_NULL || type == ExpressionType::OPERATOR_IS_NOT_NULL) {
			if (op.children.size() != 1) {
				return false;
			}
			string name;
			LogicalType coltype;
			if (!ColumnName(*op.children[0], name, coltype)) {
				return false;
			}
			out = "{\"op\":\"";
			out += type == ExpressionType::OPERATOR_IS_NULL ? "is_null" : "is_not_null";
			out += "\",\"col\":";
			JsonStr(name, out);
			out += "}";
			return true;
		}
		if (type == ExpressionType::COMPARE_IN) {
			// children[0] = column, children[1..] = constants (IN-rewriting runs after
			// filter pushdown, so IN is still a single operator here). IN is positive
			// equality => superset-safe for all types (incl VARCHAR).
			if (op.children.size() < 2 || op.children[0]->GetExpressionClass() != ExpressionClass::BOUND_COLUMN_REF) {
				return false;
			}
			string name;
			LogicalType coltype;
			if (!ColumnName(*op.children[0], name, coltype)) {
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
			out = "{\"op\":\"in\",\"col\":";
			JsonStr(name, out);
			out += ",\"vals\":[";
			for (idx_t i = 0; i < values.size(); i++) {
				if (i) {
					out += ',';
				}
				out += to_string(AddConstant(values[i]));
			}
			out += "]}";
			return true;
		}
		return false; // OPERATOR_NOT etc.: leave to DuckDB
	}

	bool Conjunction(const BoundConjunctionExpression &conj, string &out) {
		auto type = conj.GetExpressionType();
		bool is_and = type == ExpressionType::CONJUNCTION_AND;
		bool is_or = type == ExpressionType::CONJUNCTION_OR;
		if (!is_and && !is_or) {
			return false;
		}
		vector<string> parts;
		for (auto &child : conj.children) {
			string js;
			if (Serialize(*child, js)) {
				parts.push_back(std::move(js));
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
		return true;
	}

	bool Between(const BoundBetweenExpression &b, string &out) {
		string name;
		LogicalType coltype;
		if (!ColumnName(*b.input, name, coltype)) {
			return false;
		}
		if (coltype.id() == LogicalTypeId::VARCHAR) {
			return false; // string range: collation-dependent ordering, not superset-safe
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
		out += "\",\"col\":";
		JsonStr(name, out);
		out += ",\"val\":" + to_string(lo_idx) + "},{\"op\":\"compare\",\"cmp\":\"";
		out += hi_op;
		out += "\",\"col\":";
		JsonStr(name, out);
		out += ",\"val\":" + to_string(hi_idx) + "}]}";
		return true;
	}

	LogicalGet &get_;
	vector<Value> &constants_;
};

} // namespace

// pushdown_complex_filter: serialize the superset-safe predicates into bind_data and
// LEAVE every expression in `filters` (best-effort) so DuckDB still applies them all.
// Shared with the table-function scan (arrownet_schema_entry.cpp); declared in
// arrownet_table_entry.hpp.
void ArrowNetComplexFilterPushdown(ClientContext &, LogicalGet &get, FunctionData *bind_data_p,
                                   vector<unique_ptr<Expression>> &filters) {
	auto &bind_data = bind_data_p->Cast<arrownet::ArrowStreamBindData>();
	bind_data.filter_json.clear();
	bind_data.filter_constants.clear();
	if (filters.empty()) {
		return;
	}
	FilterSerializer ser(get, bind_data.filter_constants);
	vector<string> parts;
	for (auto &f : filters) { // do NOT erase — DuckDB re-applies them
		string js;
		if (ser.Serialize(*f, js)) {
			parts.push_back(std::move(js));
		}
	}
	if (parts.empty()) {
		bind_data.filter_constants.clear();
		return;
	}
	// The filters vector is an implicit AND.
	if (parts.size() == 1) {
		bind_data.filter_json = parts[0];
		return;
	}
	string json = "{\"op\":\"and\",\"children\":[";
	for (idx_t i = 0; i < parts.size(); i++) {
		if (i) {
			json += ',';
		}
		json += parts[i];
	}
	json += "]}";
	bind_data.filter_json = std::move(json);
}

ArrowNetTableEntry::ArrowNetTableEntry(Catalog &catalog, SchemaCatalogEntry &schema, CreateTableInfo &info,
                                       ArrowNetHandle handle, vector<idx_t> rowid_columns, LogicalType rowid_type)
    : TableCatalogEntry(catalog, schema, info), handle_(handle), rowid_columns_(std::move(rowid_columns)),
      rowid_type_(std::move(rowid_type)) {
}

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

TableFunction ArrowNetTableEntry::GetScanFunction(ClientContext &context, unique_ptr<FunctionData> &bind_data) {
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
	data->rowid_type = rowid_type_;
	data->table = this; // lets LogicalGet::GetTable() resolve (UPDATE/DELETE)

	bind_data = std::move(data);

	TableFunction function("arrownet_scan", {}, arrownet::ArrowStreamScan, nullptr, arrownet::ArrowStreamInitGlobal,
	                       arrownet::ArrowStreamInitLocal);
	function.projection_pushdown = true;
	// Best-effort filter pushdown: the callback serializes superset-safe predicates
	// and leaves them in place, so DuckDB still applies every filter (correctness).
	// filter_pushdown stays false (its TableFilterSet path removes filters from the
	// plan, which would be unsafe for partial/approximate pushdown).
	function.pushdown_complex_filter = ArrowNetComplexFilterPushdown;
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
	if (!rowid_columns_.empty()) {
		// Expose a rowid backed by the PK / unique-index columns.
		result.insert(make_pair(COLUMN_IDENTIFIER_ROW_ID, TableColumn("rowid", rowid_type_)));
	}
	// Otherwise no virtual columns (no DuckDB rowid) — scans then don't require
	// projection pushdown for the virtual column.
	return result;
}

vector<column_t> ArrowNetTableEntry::GetRowIdColumns() const {
	vector<column_t> result;
	if (!rowid_columns_.empty()) {
		result.push_back(COLUMN_IDENTIFIER_ROW_ID);
	}
	return result;
}

} // namespace duckdb
