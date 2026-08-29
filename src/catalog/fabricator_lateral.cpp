// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//         fabricator — row-mapped (correlated LATERAL) table functions (impl)
//===----------------------------------------------------------------------===//
//
// See catalog/fabricator_lateral.hpp for the shape and abi.h for the wire contract. The one thing worth
// restating here: PROVENANCE is what separates this from the table-in-out exchange. An in-out sends input and
// takes back rows; it never has to say which input row an output row came from, because either exactly one is
// in flight (nothing to disambiguate) or there are no correlated columns to stamp. A lateral function batched
// over N input rows must answer that question per output row, or 1->N and 1->0 are inexpressible and the
// correlated columns cannot be stamped at all.
//
// DELIBERATELY NOT ADVERTISED: projection pushdown. With `projection_pushdown` false DuckDB's
// remove-unused-columns pass leaves the get's column list alone, so the callee's batch positions always match
// the bind-time output schema. Advertising it would narrow the get and require the callee-original column
// indices to be captured at rewrite time and threaded through as the wire projection — where an off-by-one
// reads a callee column into a correlated column's slot: wrong data, no error. DuckDB projects above the
// operator instead, which costs a projection and cannot be wrong.
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_lateral.hpp"

#include "fabricator/arrow_ingest.hpp"
#include "fabricator/arrow_produce.hpp"
#include "catalog/fabricator_txn_util.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/common/enums/operator_result_type.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/vector_operations/vector_operations.hpp"
#include "duckdb/execution/execution_context.hpp"
#include "duckdb/execution/physical_operator.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/function/table/arrow.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/planner/operator/logical_extension_operator.hpp"
#include "duckdb/planner/operator/logical_get.hpp"

#include <cstring>

namespace duckdb {

const char *const FabricatorBatchedLateralSetting = "fabricator_batched_lateral";

namespace {

//! Static info attached to the registered TableFunction (the provider identity + the declared signature).
struct LateralFunctionInfo : public TableFunctionInfo {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	vector<string> arg_names;
	vector<LogicalType> arg_types;
	vector<FabricatorParamStyle> arg_styles;
};

//! Refcounted owner of the managed BINDING. Shared by the bind data, so the binding is freed exactly once at
//! plan teardown even though a prepared statement re-executes.
struct LateralBindingHolder {
	FabricatorHandle binding = nullptr;
	~LateralBindingHolder() {
		fabricator::LateralBindClose(binding); // best-effort; swallows errors
		binding = nullptr;
	}
};

struct LateralBindData : public TableFunctionData {
	FabricatorHandle handle = nullptr;
	string schema;
	string func;
	//! The per-row INPUT columns — the function's positional parameters as DuckDB resolved them from the
	//! argument expressions (input_table_types), MINUS any bind-time CONSTANT slots (those never reach the
	//! callee's rows). This is what the managed bind sees as its input schema and what the wire carries.
	vector<LogicalType> input_types;
	vector<string> input_names;
	//! For each entry of input_types, the CHILD-CHUNK column index it comes from. Identity while no CONSTANT
	//! slot is declared; with one, the wire skips that slot while the child chunk still carries it.
	vector<idx_t> wire_slots;
	//! The TOTAL positional width in the child chunk (CONSTANT slots included) — the correlated operator's
	//! [callee inputs | correlated columns] split point, which input_types.size() no longer is.
	idx_t arg_width = 0;
	//! The callee's OWN output columns (what the function returns; the correlated columns are the host's).
	vector<LogicalType> output_types;
	//! TRUE for the literal-argument shape (`f(1,2)`): no child, so DuckDB plans a PhysicalTableScan that
	//! re-invokes the callback with the SAME 1-row constant chunk and decides flow purely on chunk.size().
	//! That needs a call-once-then-EOS latch where the operator shape needs one call per input chunk.
	bool source_shape = false;
	shared_ptr<LateralBindingHolder> holder;
};

//===----------------------------------------------------------------------===//
// The managed session + the result of the call in flight.
//
// One per THREAD, never shared: on the row-by-row path it lives in the in-out LOCAL state, on the batched
// path in the OperatorState. lateral_open permits several open at once precisely so that holds.
//===----------------------------------------------------------------------===//
struct LateralSession {
	LateralSession(FabricatorHandle binding, const LateralBindData &bind) : bind_(bind) {
		handle_ = fabricator::LateralOpen(binding);
	}
	~LateralSession() {
		reader_.reset(); // release the in-flight result stream BEFORE the session that produced it
		fabricator::LateralClose(handle_);
	}

	//! Issue ONE call over the leading input_types.size() columns of `input`. Replaces any previous result.
	void Call(ClientContext &context, DataChunk &input) {
		reader_.reset();
		auto props = fabricator::BoundaryClientProperties(context);
		auto ext = ArrowTypeExtensionData::GetExtensionTypes(context, bind_.input_types);
		// A narrow VIEW of the leading input columns. Required, not tidy: ArrowAppender::Append iterates
		// `input.ColumnCount()` against its own per-type root_data, so handing it the child chunk (whose
		// trailing slots hold the correlated columns) walks off the end of that array in a release build.
		//
		// ⚠ CAST-AT-SEAM, not a blind Reference: for an in-out function DuckDB RELABELS input_table_types[i]
		// to the DECLARED parameter type without inserting a cast into the child subquery
		// (bind_table_function.cpp:448-457 — ANY slots exempt), so the chunk can legitimately arrive in the
		// EXPRESSION's own type: `plug_lat_slow(t.bigint_col, …)` against a declared INTEGER delivers BIGINT
		// here while the bind promised INTEGER. Vector::Reference on that mismatch is an INTERNAL error that
		// INVALIDATES the whole database. Deliver the type the bind REPORTED instead — the binder already
		// judged the pair implicitly castable when it matched the overload.
		DataChunk view;
		view.Initialize(Allocator::DefaultAllocator(), bind_.input_types);
		for (idx_t i = 0; i < bind_.input_types.size(); i++) {
			// wire_slots skips bind-time CONSTANT slots — the child chunk still carries them, the callee
			// never sees them (their value went to the managed bind through the args batch).
			auto &src = input.data[bind_.wire_slots[i]];
			if (src.GetType() == bind_.input_types[i]) {
				view.data[i].Reference(src);
			} else {
				VectorOperations::Cast(context, src, view.data[i], input.size());
			}
		}
		view.SetCardinality(input.size());
		ArrowAppender appender(bind_.input_types, input.size(), props, ext);
		appender.Append(view, 0, input.size(), input.size());
		ArrowArray array = appender.Finalize();

		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		fabricator::LateralCall(handle_, array, out); // consumes `array`
		reader_ = make_uniq<fabricator::ArrowStreamReader>(context, out);

		// The wire is the trust boundary: verify its shape ONCE per call rather than trusting that the schema
		// the bind advertised and the schema this batch carries agree. A silent disagreement is a vector of
		// one type read as another — the read-past-the-end class, not merely a wrong answer.
		auto &types = reader_->Types();
		if (types.size() != bind_.output_types.size() + 1) {
			throw IOException("Fabricator: lateral function '%s' returned %llu wire columns, expected %llu (its "
			                  "output columns plus one trailing INTEGER provenance column)",
			                  bind_.func, (uint64_t)types.size(), (uint64_t)(bind_.output_types.size() + 1));
		}
		for (idx_t c = 0; c < bind_.output_types.size(); c++) {
			if (types[c] != bind_.output_types[c]) {
				throw IOException("Fabricator: lateral function '%s' column %llu came back as %s but was bound "
				                  "as %s",
				                  bind_.func, (uint64_t)c, types[c].ToString(), bind_.output_types[c].ToString());
			}
		}
		if (types.back() != LogicalType::INTEGER) {
			throw IOException("Fabricator: lateral function '%s' provenance column is %s, expected INTEGER",
			                  bind_.func, types.back().ToString());
		}
		Advance();
	}

	//! True while the current call still has rows to hand out.
	bool HasRows() const {
		return reader_ && reader_->HasPending();
	}

	//! Drain the next <= STANDARD_VECTOR_SIZE rows into a FRESHLY ALLOCATED chunk of the wire types.
	//!
	//! Fresh, not a reused member, and that is the Invariant-2 indirection: the caller REFERENCES these
	//! vectors into a wider output chunk, so the buffers must not be rewritten by the next drain. Reference
	//! keeps them alive by refcount after this chunk goes out of scope; a reused chunk's Reset() restores the
	//! SAME cached buffers and the next drain would overwrite rows already handed downstream.
	unique_ptr<DataChunk> DrainOwned(ClientContext &context) {
		auto chunk = make_uniq<DataChunk>();
		chunk->Initialize(Allocator::Get(context), reader_->Types());
		reader_->Drain(*chunk);
		if (!reader_->HasPending()) {
			Advance(); // a call may return several batches; step to the next one (or to end-of-call)
		}
		return chunk;
	}

private:
	//! Pull until a batch with rows is pending, or the call's stream is exhausted.
	void Advance() {
		while (!reader_->HasPending()) {
			auto pr = reader_->Pull();
			if (pr == fabricator::ArrowStreamReader::PullResult::END) {
				return; // no rows left for THIS call — not end of stream; a map answers once per request
			}
			// A length-0 batch is the in-out exchange's per-input SENTINEL and has no meaning here (this is a
			// request/response entry), so it is skipped rather than read as end-of-call.
		}
	}

	const LateralBindData &bind_;
	FabricatorHandle handle_ = nullptr;
	unique_ptr<fabricator::ArrowStreamReader> reader_;
};

//! Validate the wire's trailing provenance column and, when `sel` is given, turn it into a selection vector
//! over the INPUT rows.
//!
//! Adversarial by assumption: these values are used directly as an index into the input chunk, so range is
//! checked before the first use. Validated on BOTH paths even though the row-by-row path does not USE it —
//! so the two paths agree on what is an error, which is what makes the reference-oracle test meaningful.
void ReadOriginColumn(const string &func, DataChunk &wire, idx_t base_idx, idx_t input_rows,
                      SelectionVector *sel) {
	idx_t rows = wire.size();
	auto &origin = wire.data[base_idx];
	UnifiedVectorFormat fmt;
	origin.ToUnifiedFormat(rows, fmt);
	auto data = UnifiedVectorFormat::GetData<int32_t>(fmt);
	for (idx_t r = 0; r < rows; r++) {
		auto idx = fmt.sel->get_index(r);
		if (!fmt.validity.RowIsValid(idx)) {
			throw IOException("Fabricator: lateral function '%s' returned a NULL provenance index for output "
			                  "row %llu",
			                  func, (uint64_t)r);
		}
		int32_t v = data[idx];
		if (v < 0 || (idx_t)v >= input_rows) {
			throw IOException("Fabricator: lateral function '%s' returned provenance index %d for output row "
			                  "%llu, outside the input range [0, %llu)",
			                  func, (int)v, (uint64_t)r, (uint64_t)input_rows);
		}
		if (sel) {
			sel->set_index(r, (idx_t)v);
		}
	}
}

//! Copy the callee columns of one drained wire chunk into the (wider) output chunk.
//!
//! `output` carries base_idx callee columns plus, on the correlated shape, the projected passthrough columns
//! AFTER them. Write ONLY [0, base_idx) and never Reset() the output: on the row-by-row path DuckDB has
//! already installed the correlated columns as constant vectors by the time we are called, and a reset would
//! clear them.
void EmitCalleeColumns(DataChunk &wire, DataChunk &output, idx_t base_idx) {
	for (idx_t c = 0; c < base_idx; c++) {
		output.data[c].Reference(wire.data[c]);
	}
	output.SetCardinality(wire.size());
}

//===----------------------------------------------------------------------===//
// Bind — shared by both paths (the batched rewrite happens entirely after binding)
//===----------------------------------------------------------------------===//

unique_ptr<FunctionData> LateralBind(ClientContext &context, TableFunctionBindInput &input,
                                    vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<LateralFunctionInfo>();
	auto bind_data = make_uniq<LateralBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->holder = make_shared_ptr<LateralBindingHolder>();

	// THE ASYMMETRY (abi.h): the positional arguments do NOT arrive in input.inputs — they ARE the per-row
	// input columns, and DuckDB reports them as the synthesized input relation's schema. That holds for the
	// literal shape too (bind_table_function pushes the parameter types with EMPTY names), which is exactly
	// how the source shape is detected.
	bool all_names_empty = true;
	// The DECLARED positional slots (bind-time CONSTANT ones included — they occupy positional slots), in
	// slot order: the one contract that is stable across the two call shapes (see the normalization below).
	vector<LogicalType> declared_positional;
	vector<bool> declared_constant;
	vector<string> declared_pos_names;
	for (idx_t i = 0; i < info.arg_types.size(); i++) {
		auto style = i < info.arg_styles.size() ? info.arg_styles[i] : FabricatorParamStyle::POSITIONAL;
		if (style == FabricatorParamStyle::NAMED) {
			continue;
		}
		declared_positional.push_back(info.arg_types[i]);
		declared_constant.push_back(style == FabricatorParamStyle::CONSTANT);
		declared_pos_names.push_back(i < info.arg_names.size() ? info.arg_names[i] : "arg" + to_string(i));
	}
	// Bind-time CONSTANT slots, collected here and marshaled into the args batch below: the folded VALUE
	// where the binder evaluated the arguments (the literal shape, input.inputs), else a NULL of the BOUND
	// type — for a `const_arg(...)` wrapper that type is the capture struct whose member name keys the
	// managed registry, and for anything else the managed side refuses with the wrap-it message.
	vector<string> const_names;
	vector<Value> const_values;
	bind_data->arg_width = input.input_table_types.size();
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		auto slot_name = i < input.input_table_names.size() ? input.input_table_names[i] : string();
		if (!slot_name.empty()) {
			all_names_empty = false;
		}
		if (i < declared_constant.size() && declared_constant[i]) {
			Value v = (!input.inputs.empty() && i < input.inputs.size()) ? input.inputs[i]
			                                                            : Value(input.input_table_types[i]);
			const_names.push_back(declared_pos_names[i]);
			const_values.push_back(std::move(v));
			continue; // never a wire column: the callee's rows do not carry it
		}
		auto col_type = input.input_table_types[i];
		// ⚠ NORMALIZE TO THE DECLARATION — DuckDB's two call shapes DISAGREE about this type, in OPPOSITE
		// directions (bind_table_function.cpp:425-457): the CORRELATED shape RELABELS input_table_types[i]
		// to the declared parameter type while the child still delivers the expression's own type (no cast
		// is inserted), and the LITERAL shape reports the PRE-cast expression type while delivering the
		// POST-cast value (input_table_types is filled from parameters[i].type() BEFORE the cast loop runs).
		// So neither reading can be trusted as "what arrives at execute". The DECLARATION is the one stable
		// contract: a concrete declared type WINS here — the input schema handed to the managed bind then
		// shows the author's own declaration, and LateralSession::Call casts each runtime chunk to it. An
		// ANY-declared slot (Arrow null type => SQLNULL/ANY) keeps the BOUND type: carrying a per-call-site
		// type through untouched is what ANY is for.
		if (i < declared_positional.size() && declared_positional[i].id() != LogicalTypeId::SQLNULL &&
		    declared_positional[i].id() != LogicalTypeId::ANY) {
			col_type = declared_positional[i];
		}
		bind_data->input_types.push_back(col_type);
		bind_data->input_names.push_back(slot_name.empty() ? "col" + to_string(i) : slot_name);
		bind_data->wire_slots.push_back(i);
	}
	if (bind_data->input_types.empty()) {
		throw BinderException("Fabricator: lateral function \"%s\" needs at least one PER-ROW argument — its "
		                      "non-constant positional parameters ARE its per-row input columns",
		                      info.func);
	}
	bind_data->source_shape = all_names_empty;

	auto props = fabricator::BoundaryClientProperties(context);
	ArrowSchema input_schema;
	std::memset(&input_schema, 0, sizeof(input_schema));
	ArrowConverter::ToArrowSchema(&input_schema, bind_data->input_types, bind_data->input_names, props);

	// The bind-time arguments: NAMED parameters (an omitted one crosses as a typed NULL — the same
	// "omitted == explicit NULL" equivalence every other function kind uses) plus the CONSTANT slots
	// collected above. An ordinary positional slot is runtime data and has nothing to marshal.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	for (idx_t i = 0; i < info.arg_names.size(); i++) {
		auto style = i < info.arg_styles.size() ? info.arg_styles[i] : FabricatorParamStyle::POSITIONAL;
		if (style != FabricatorParamStyle::NAMED) {
			continue;
		}
		Value v(info.arg_types[i]);
		for (auto &kv : input.named_parameters) {
			if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
				v = kv.second;
				break;
			}
		}
		arg_names.push_back(info.arg_names[i]);
		arg_types.push_back(info.arg_types[i].id() == LogicalTypeId::SQLNULL ? v.type() : info.arg_types[i]);
		arg_values.push_back(std::move(v));
	}
	for (idx_t k = 0; k < const_names.size(); k++) {
		arg_names.push_back(const_names[k]);
		arg_types.push_back(const_values[k].type());
		arg_values.push_back(std::move(const_values[k]));
	}
	fabricator::ArrowProducer arg_producer(arg_types, arg_names, props);
	ArrowArrayStream *args_ptr = nullptr;
	if (!arg_values.empty()) {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		auto ext = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
		ArrowAppender appender(arg_types, 1, props, ext);
		appender.Append(chunk, 0, 1, 1);
		arg_producer.AddBatch(appender.Finalize());
		arg_producer.Finish();
		args_ptr = arg_producer.Stream();
	}

	FabricatorSetActiveTxn(info.handle, context);
	ArrowArrayStream out_schema;
	std::memset(&out_schema, 0, sizeof(out_schema));
	bind_data->holder->binding =
	    fabricator::LateralBind(info.handle, info.schema, info.func, args_ptr, input_schema, out_schema);

	ArrowSchemaWrapper schema_root;
	if (out_schema.get_schema(&out_schema, &schema_root.arrow_schema) != 0) {
		// Copy the error BEFORE release: get_last_error's pointer lives in the stream's
		// private data, which release frees.
		string msg;
		if (out_schema.get_last_error) {
			if (const char *err = out_schema.get_last_error(&out_schema)) {
				msg = err;
			}
		}
		if (out_schema.release) {
			out_schema.release(&out_schema);
		}
		throw IOException(string("Fabricator: failed to read lateral output schema") +
		                  (msg.empty() ? string() : ": " + msg));
	}
	if (out_schema.release) {
		out_schema.release(&out_schema);
	}
	ArrowTableSchema arrow_table;
	ArrowTableFunction::PopulateArrowTableSchema(context, arrow_table, schema_root.arrow_schema);
	for (int64_t i = 0; i < schema_root.arrow_schema.n_children; i++) {
		auto &child = *schema_root.arrow_schema.children[i];
		names.push_back(child.name ? string(child.name) : "column" + to_string(i));
		return_types.push_back(arrow_table.GetColumns().at((idx_t)i)->GetDuckType());
	}
	if (return_types.empty()) {
		throw BinderException("Fabricator: lateral function \"%s\" declared no output columns", info.func);
	}
	bind_data->output_types = return_types;
	return std::move(bind_data);
}

//===----------------------------------------------------------------------===//
// The row-by-row path — DuckDB's own PhysicalTableInOutFunction / PhysicalTableScan
//===----------------------------------------------------------------------===//

struct LateralLocalState : public LocalTableFunctionState {
	unique_ptr<LateralSession> session;
	//! Operator shape: a call has been issued for the input chunk currently in hand.
	bool called = false;
	//! Source shape: the one call's rows are exhausted, so the next invocation must emit 0 rows — which is how
	//! PhysicalTableScan learns the scan is over (it keys on chunk.size(), not on our return value).
	bool done = false;
};

unique_ptr<LocalTableFunctionState> LateralInitLocal(ExecutionContext &, TableFunctionInitInput &,
                                                    GlobalTableFunctionState *) {
	return make_uniq<LateralLocalState>();
}

OperatorResultType LateralInOutFunction(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                        DataChunk &output) {
	auto &bind = data.bind_data->Cast<LateralBindData>();
	auto &l = data.local_state->Cast<LateralLocalState>();
	idx_t base_idx = bind.output_types.size();
	// Every ambient this crossing needs must be read HERE, in the crossing that sets them: the callee may
	// open a provider connection or reach the host filesystem on its first call.
	FabricatorSetActiveTxn(bind.handle, context.client);

	if (bind.source_shape) {
		// Literal args: the SAME 1-row constant chunk comes back every time, and 0 rows means FINISHED.
		if (l.done) {
			output.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		if (!l.session) {
			l.session = make_uniq<LateralSession>(bind.holder->binding, bind);
		}
		if (!l.called) {
			l.session->Call(context.client, input);
			l.called = true;
		}
		if (!l.session->HasRows()) {
			l.done = true;
			output.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		auto wire = l.session->DrainOwned(context.client);
		ReadOriginColumn(bind.func, *wire, base_idx, input.size(), nullptr);
		EmitCalleeColumns(*wire, output, base_idx);
		if (!l.session->HasRows()) {
			l.done = true; // the next invocation emits 0 rows, which ends the scan
		}
		return OperatorResultType::HAVE_MORE_OUTPUT;
	}

	// Operator shape. `input` is ONE outer row under the correlated plan (the driver slices it and stamps the
	// correlated columns for us) and the WHOLE chunk when there are no correlated columns to project.
	if (!l.called) {
		if (input.size() == 0) {
			output.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		if (!l.session) {
			l.session = make_uniq<LateralSession>(bind.holder->binding, bind);
		}
		l.session->Call(context.client, input);
		l.called = true;
	}
	if (!l.session->HasRows()) {
		l.called = false;
		output.SetCardinality(0);
		// NEVER FINISHED: a map is done when its child is done, and FINISHED would tell DuckDB to stop
		// feeding us — truncating the result at the first input row that produced nothing.
		return OperatorResultType::NEED_MORE_INPUT;
	}
	auto wire = l.session->DrainOwned(context.client);
	ReadOriginColumn(bind.func, *wire, base_idx, input.size(), nullptr);
	EmitCalleeColumns(*wire, output, base_idx);
	if (l.session->HasRows()) {
		return OperatorResultType::HAVE_MORE_OUTPUT;
	}
	l.called = false;
	return OperatorResultType::NEED_MORE_INPUT;
}

//===----------------------------------------------------------------------===//
// The BATCHED path — our own operator, installed over the correlated shape
//===----------------------------------------------------------------------===//

//! Per-thread state. `ParallelOperator()` is true, so each pipeline thread gets one of these and therefore
//! its own managed session: no shared mutable state, no gate.
class LateralBatchedState : public OperatorState {
public:
	unique_ptr<LateralSession> session;
	//! The input chunk's size when the call in flight was issued. DuckDB re-passes the SAME input chunk while
	//! we return HAVE_MORE_OUTPUT — which is what makes the drain branch work — so a provenance array paired
	//! with a DIFFERENT input chunk would index out of bounds. This assert should be dead code; it stays
	//! because what it prevents is silent memory corruption rather than a crash.
	idx_t input_size_at_call = 0;
};

class LateralBatchedPhysical : public PhysicalOperator {
public:
	LateralBatchedPhysical(PhysicalPlan &physical_plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                       unique_ptr<FunctionData> bind_data_p, vector<column_t> projected_input_p, idx_t base_idx_p)
	    : PhysicalOperator(physical_plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
	      bind_data(std::move(bind_data_p)), projected_input(std::move(projected_input_p)), base_idx(base_idx_p) {
	}

	unique_ptr<FunctionData> bind_data;
	vector<column_t> projected_input;
	//! Where the correlated passthrough columns begin in the OUTPUT chunk == the callee's column count.
	idx_t base_idx;

	string GetName() const override {
		return "FABRICATOR_LATERAL_BATCHED";
	}

	bool ParallelOperator() const override {
		return true;
	}

	unique_ptr<OperatorState> GetOperatorState(ExecutionContext &) const override {
		return make_uniq<LateralBatchedState>();
	}

	InsertionOrderPreservingMap<string> ParamsToString() const override {
		// Captured BEFORE execution, so static facts only — a runtime call counter cannot live here.
		InsertionOrderPreservingMap<string> result;
		result["Name"] = bind_data->Cast<LateralBindData>().func;
		result["Correlated Columns"] = to_string(projected_input.size());
		SetEstimatedCardinality(result, estimated_cardinality);
		return result;
	}

	//! Emit one drained wire chunk: the callee columns by reference, the correlated columns GATHERED through
	//! the provenance selection.
	void Emit(ClientContext &context, LateralBatchedState &s, DataChunk &input, DataChunk &chunk) const {
		auto &bind = bind_data->Cast<LateralBindData>();
		auto wire = s.session->DrainOwned(context);
		idx_t rows = wire->size();
		SelectionVector sel(rows);
		ReadOriginColumn(bind.func, *wire, base_idx, input.size(), &sel);
		for (idx_t c = 0; c < base_idx; c++) {
			chunk.data[c].Reference(wire->data[c]);
		}
		// A gather, not a copy loop: the selection replicates a source row for fan-out AND severs the emitted
		// chunk's dependency on the input chunk's buffers, which the child owns and will recycle.
		for (idx_t k = 0; k < projected_input.size(); k++) {
			VectorOperations::Copy(input.data[projected_input[k]], chunk.data[base_idx + k], sel, rows, 0, 0);
		}
		chunk.SetCardinality(rows);
	}

	OperatorResultType Execute(ExecutionContext &context, DataChunk &input, DataChunk &chunk,
	                           GlobalOperatorState &, OperatorState &state_p) const override {
		auto &s = state_p.Cast<LateralBatchedState>();
		auto &bind = bind_data->Cast<LateralBindData>();
		FabricatorSetActiveTxn(bind.handle, context.client);

		// (A) A drain in progress comes FIRST — DuckDB calls us again with the same input while we have
		//     output pending, so testing the input before the buffer would re-issue the call.
		if (s.session && s.session->HasRows()) {
			if (input.size() != s.input_size_at_call) {
				throw IOException("Fabricator: lateral function '%s' input chunk resized mid-drain (%llu -> %llu)",
				                  bind.func, (uint64_t)s.input_size_at_call, (uint64_t)input.size());
			}
			Emit(context.client, s, input, chunk);
			return s.session->HasRows() ? OperatorResultType::HAVE_MORE_OUTPUT
			                            : OperatorResultType::NEED_MORE_INPUT;
		}
		// (B) Nothing to do — and NEVER FINISHED: a map is done when its child is done, and FINISHED would
		//     stop DuckDB feeding us, truncating the result.
		if (input.size() == 0) {
			chunk.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		// (C) A fresh input chunk: ONE batched call, which is the whole point of this operator.
		if (!s.session) {
			s.session = make_uniq<LateralSession>(bind.holder->binding, bind);
		}
		s.session->Call(context.client, input);
		s.input_size_at_call = input.size();
		if (!s.session->HasRows()) {
			chunk.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT; // the whole chunk was filtered out (1->0)
		}
		Emit(context.client, s, input, chunk);
		return s.session->HasRows() ? OperatorResultType::HAVE_MORE_OUTPUT : OperatorResultType::NEED_MORE_INPUT;
	}
};

//! The logical node. Its ENTIRE job is to be indistinguishable from the LogicalGet it replaced: a DELIM_JOIN
//! sits above it and resolves column bindings by (table_index, column_index). Get that wrong and it is a
//! binder error at best, the wrong columns at worst.
struct LateralBatchedLogical : public LogicalExtensionOperator {
	idx_t table_index = 0;
	vector<column_t> projected_input;
	vector<LogicalType> callee_types;
	vector<ColumnBinding> callee_bindings;
	unique_ptr<FunctionData> bind_data;

	vector<idx_t> GetTableIndex() const override {
		return vector<idx_t> {table_index};
	}

	vector<ColumnBinding> GetColumnBindings() override {
		// Exactly LogicalGet's shape: the callee columns, then the child's binding for each projected_input
		// entry, in that order. The child part is RECOMPUTED rather than snapshotted because the binding
		// resolver visits the child first and may rewrite its bindings.
		auto result = callee_bindings;
		auto child_bindings = children[0]->GetColumnBindings();
		for (auto entry : projected_input) {
			if (entry >= child_bindings.size()) {
				throw InternalException("fabricator lateral: projected_input out of range");
			}
			result.push_back(child_bindings[entry]);
		}
		return result;
	}

	void ResolveTypes() override {
		types = callee_types;
		for (auto entry : projected_input) {
			types.push_back(children[0]->types[entry]);
		}
	}

	string GetName() const override {
		return "FABRICATOR_LATERAL_BATCHED";
	}

	string GetExtensionName() const override {
		return "fabricator_lateral_batched";
	}

	PhysicalOperator &CreatePlan(ClientContext &context, PhysicalPlanGenerator &planner) override {
		if (!bind_data) {
			throw InternalException("fabricator lateral: CreatePlan called twice (bind data already moved)");
		}
		auto &child_plan = planner.CreatePlan(*children[0]);
		auto &op = planner.Make<LateralBatchedPhysical>(types, EstimateCardinality(context), std::move(bind_data),
		                                               projected_input, callee_types.size());
		op.children.push_back(child_plan);
		return op;
	}
};

//! Every clause is a guard against a shape this operator was not designed for; the load-bearing one is
//! `!projected_input.empty()`, which IS the correlated-LATERAL signal (empty means the plain uncorrelated
//! shape, where DuckDB's own driver already passes the whole chunk and is therefore already batched).
bool LateralIsEligible(LogicalGet &get) {
	if (!get.bind_data || get.function.in_out_function != LateralInOutFunction) {
		return false; // not one of ours
	}
	if (get.function.in_out_function_final) {
		return false; // DuckDB refuses finalize + projected_input anyway; defensive
	}
	if (get.projected_input.empty() || get.children.size() != 1) {
		return false;
	}
	if (!get.projection_ids.empty()) {
		return false; // we do not advertise projection pushdown; if something ever narrows the get, bail out
	}
	auto &bind = get.bind_data->Cast<LateralBindData>();
	if (bind.source_shape || !bind.holder || !bind.holder->binding) {
		return false;
	}
	auto child_width = get.children[0]->types.size();
	if (child_width < get.projected_input.size()) {
		return false;
	}
	// The child chunk is [ callee input columns | correlated columns ], and the input half must be exactly
	// the bind's POSITIONAL width (arg_width, bind-time CONSTANT slots included — they sit in the child chunk
	// even though the wire skips them) — otherwise the wire_slots view would index the wrong columns.
	idx_t input_width = child_width - get.projected_input.size();
	if (input_width != bind.arg_width) {
		return false;
	}
	for (auto entry : get.projected_input) {
		if (entry < input_width || entry >= child_width) {
			return false;
		}
	}
	if (get.types.size() != bind.output_types.size() + get.projected_input.size()) {
		return false;
	}
	return true;
}

//! Depth-first: recurse into children, then test this node, replacing in place through the unique_ptr& so the
//! parent's child slot is updated.
void RewriteLateralNodes(unique_ptr<LogicalOperator> &op) {
	for (auto &child : op->children) {
		RewriteLateralNodes(child);
	}
	if (op->type != LogicalOperatorType::LOGICAL_GET) {
		return;
	}
	auto &get = op->Cast<LogicalGet>();
	if (!LateralIsEligible(get)) {
		return;
	}
	auto &bind = get.bind_data->Cast<LateralBindData>();
	idx_t base = bind.output_types.size();

	auto node = make_uniq<LateralBatchedLogical>();
	node->table_index = get.table_index;
	// Built directly rather than sliced out of get.GetColumnBindings(): with projection_ids empty (asserted in
	// the eligibility check) LogicalGet produces exactly (table_index, i), and constructing them here avoids
	// depending on call order relative to the moves below.
	for (idx_t i = 0; i < base; i++) {
		node->callee_bindings.emplace_back(get.table_index, i);
		node->callee_types.push_back(get.types[i]);
	}
	node->projected_input = std::move(get.projected_input);
	node->bind_data = std::move(get.bind_data);
	node->has_estimated_cardinality = get.has_estimated_cardinality;
	node->estimated_cardinality = get.estimated_cardinality;
	node->children.push_back(std::move(get.children[0]));
	node->ResolveOperatorTypes();
	op = std::move(node);
}

void LateralOptimize(OptimizerExtensionInput &input, unique_ptr<LogicalOperator> &plan) {
	// The kill switch. Absent (the bridge never booted, so there are no lateral functions either) => batched,
	// which is the shipped default.
	Value enabled;
	if (input.context.TryGetCurrentSetting(FabricatorBatchedLateralSetting, enabled) && !enabled.IsNull() &&
	    !enabled.GetValue<bool>()) {
		return; // stay on DuckDB's row-by-row driver — the reference oracle
	}
	RewriteLateralNodes(plan);
}

} // namespace

TableFunction FabricatorMakeLateralFunction(FabricatorHandle handle, const string &schema_name,
                                           const string &func_name, vector<string> arg_names,
                                           vector<LogicalType> arg_types, vector<FabricatorParamStyle> arg_styles) {
	TableFunction tf(func_name, {}, nullptr, LateralBind, nullptr, LateralInitLocal);
	tf.in_out_function = LateralInOutFunction;
	auto info = make_shared_ptr<LateralFunctionInfo>();
	// POSITIONAL parameters become real ARGUMENT TYPES — that is what makes `f(i.a)` bind at all, and what
	// lets overloads work (the TABLE-parameter overload restriction does not apply). NAMED ones become DuckDB
	// named parameters — bind-time configuration the LITERAL call shape can spell. A CONSTANT parameter is
	// the bind-time configuration BOTH shapes can spell: it registers as an ordinary ANY positional slot
	// (falling into the else below — Params.Constant declares the Arrow null type), and LateralBind resolves
	// its VALUE into the managed args batch instead of the wire (see the capture registry in
	// Fabricator.Bridge/CapturedConstants.cs).
	for (idx_t i = 0; i < arg_names.size(); i++) {
		auto style = i < arg_styles.size() ? arg_styles[i] : FabricatorParamStyle::POSITIONAL;
		auto type = arg_types[i].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[i];
		if (style == FabricatorParamStyle::NAMED) {
			tf.named_parameters[arg_names[i]] = type;
		} else if (style == FabricatorParamStyle::TABLE_INPUT) {
			// A lateral function has no input TABLE — its arguments ARE the input. Declaring one would build a
			// {TABLE} signature the correlated spelling cannot bind against, so refuse at registration rather
			// than ship a function nobody can call correlated.
			throw InvalidInputException("Fabricator: lateral function \"%s\" declared a table-input parameter "
			                            "(\"%s\"); its POSITIONAL parameters are its per-row input columns. "
			                            "Declare an in-out function instead if it needs a table argument.",
			                            func_name, arg_names[i]);
		} else {
			tf.arguments.push_back(type);
		}
	}
	info->handle = handle;
	info->schema = schema_name;
	info->func = func_name;
	info->arg_names = std::move(arg_names);
	info->arg_types = std::move(arg_types);
	info->arg_styles = std::move(arg_styles);
	tf.function_info = std::move(info);
	return tf;
}

void RegisterFabricatorLateralOptimizer(DBConfig &config) {
	OptimizerExtension extension;
	// optimize_function, NOT pre_optimize_function: `projected_input` — the entire eligibility signal — is
	// produced by the DECORRELATOR, so in the pre-optimize phase it is not populated yet and this would match
	// nothing at all.
	extension.optimize_function = LateralOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

} // namespace duckdb
