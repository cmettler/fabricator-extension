//===----------------------------------------------------------------------===//
//                         arrownet — schema catalog entry (impl)
//===----------------------------------------------------------------------===//

#include "catalog/arrownet_schema_entry.hpp"

#include "arrownet/arrow_ingest.hpp"
#include "arrownet/arrow_produce.hpp"
#include "arrownet/clr_host.hpp"
#include "catalog/arrownet_catalog.hpp"
#include "catalog/arrownet_metadata.hpp"
#include "catalog/arrownet_txn_util.hpp"
#include "duckdb/common/arrow/arrow_appender.hpp"
#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/common/enums/operator_result_type.hpp"
#include "duckdb/common/exception.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/common/types/blob.hpp"
#include "duckdb/common/vector_operations/vector_operations.hpp"
#include "duckdb/catalog/catalog_entry/aggregate_function_catalog_entry.hpp"
#include "duckdb/execution/execution_context.hpp"
#include "duckdb/execution/expression_executor_state.hpp"
#include "duckdb/execution/physical_operator.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/function/aggregate_function.hpp"
#include "duckdb/function/aggregate_state.hpp"
#include "duckdb/function/function_set.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/function/table_function.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/connection.hpp"
#include "duckdb/main/extension/extension_loader.hpp"
#include "duckdb/optimizer/optimizer_extension.hpp"
#include "duckdb/planner/operator/logical_extension_operator.hpp"
#include "duckdb/planner/operator/logical_get.hpp"
#include "duckdb/parser/constraints/not_null_constraint.hpp"
#include "duckdb/parser/constraints/unique_constraint.hpp"
#include "duckdb/parser/expression/cast_expression.hpp"
#include "duckdb/parser/expression/constant_expression.hpp"
#include "duckdb/parser/parsed_data/alter_table_info.hpp"
#include "duckdb/parser/parsed_data/create_aggregate_function_info.hpp"
#include "duckdb/parser/parsed_data/create_scalar_function_info.hpp"
#include "duckdb/parser/parsed_data/create_table_function_info.hpp"
#include "duckdb/parser/parsed_data/create_table_info.hpp"
#include "duckdb/parser/parsed_data/drop_info.hpp"
#include "duckdb/planner/parsed_data/bound_create_table_info.hpp"

#include <atomic>
#include <cstring>
#include <unordered_map>

namespace duckdb {

ArrowNetSchemaEntry::ArrowNetSchemaEntry(Catalog &catalog, CreateSchemaInfo &info, ArrowNetHandle handle)
    : SchemaCatalogEntry(catalog, info), handle_(handle) {
}

void ArrowNetSchemaEntry::AddTable(const string &table_name, const string &table_type) {
	lock_guard<mutex> lock(entry_lock_);
	table_types_[table_name] = table_type;
	// Drop any cached entry so the schema is re-fetched (e.g. after CREATE OR REPLACE).
	entries_.erase(table_name);
}

void ArrowNetSchemaEntry::AddScalarFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	scalar_functions_.insert(func_name);
	// Drop any cached entry so the signature is re-fetched (e.g. after CREATE OR ALTER).
	function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::AddTableFunction(const string &func_name, bool is_proc) {
	lock_guard<mutex> lock(entry_lock_);
	table_functions_[func_name] = is_proc;
	table_function_entries_.erase(func_name);
	// A discovered TVF or stored proc also gets a synthetic table-in-out alias `<name>_each` that applies
	// it once per input row (4g): a TVF via SQL-Server CROSS APPLY, a proc via per-row EXEC (the managed
	// side picks by object kind). Both echo the input columns + the function's output columns; a proc's
	// per-row EXECs run in DuckDB's transaction (commit/rollback driven by DuckDB).
	string each = func_name + "_each";
	inout_functions_[each] = func_name;
	table_function_entries_.erase(each);
}

void ArrowNetSchemaEntry::AddInOutFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	custom_inout_functions_.insert(func_name);
	table_function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::AddCollectorFunction(const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	custom_collector_functions_.insert(func_name);
	table_function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::AddAggregateFunction(const string &func_name, bool spillable) {
	lock_guard<mutex> lock(entry_lock_);
	aggregate_functions_[func_name] = spillable;
	// Drop any cached entry so the signature is re-fetched (e.g. after a cache refresh).
	aggregate_function_entries_.erase(func_name);
}

void ArrowNetSchemaEntry::ClearTables() {
	lock_guard<mutex> lock(entry_lock_);
	table_types_.clear();
	entries_.clear();
	scalar_functions_.clear();
	function_entries_.clear();
	table_functions_.clear();
	inout_functions_.clear();
	custom_inout_functions_.clear();
	custom_collector_functions_.clear();
	aggregate_functions_.clear();
	table_function_entries_.clear();
	aggregate_function_entries_.clear();
}

void ArrowNetSchemaEntry::InvalidateEntryCache() {
	// Keep the discovered NAME lists (table_types_, scalar_functions_, …); drop only the materialized
	// entries so the next access re-fetches columns/rowid/return types from the (now committed) server state.
	lock_guard<mutex> lock(entry_lock_);
	entries_.clear();
	function_entries_.clear();
	table_function_entries_.clear();
	aggregate_function_entries_.clear();
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateEntry(ClientContext &context, const string &table_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = entries_.find(table_name);
	if (cached != entries_.end()) {
		return cached->second.get();
	}
	auto type_it = table_types_.find(table_name);
	if (type_it == table_types_.end()) {
		return nullptr;
	}

	vector<string> names;
	vector<LogicalType> types;
	try {
		FetchTableColumns(context, handle_, name, table_name, names, types);
	} catch (std::exception &) {
		// The discovered name is stale — the table no longer exists on the server
		// (e.g. dropped out-of-band via mssql_net_exec). Treat it as not-found so
		// CREATE TABLE IF NOT EXISTS / OR REPLACE see "absent" instead of an error.
		table_types_.erase(table_name);
		entries_.erase(table_name);
		return nullptr;
	}

	CreateTableInfo info(catalog.GetName(), name, table_name);
	for (idx_t i = 0; i < names.size(); i++) {
		info.columns.AddColumn(ColumnDefinition(names[i], types[i]));
	}

	// Resolve row-identity columns (PK / smallest unique index) to column indices.
	auto rowid_names = FetchRowIdColumns(handle_, name, table_name);
	vector<idx_t> rowid_indices;
	for (auto &rowid_name : rowid_names) {
		for (idx_t i = 0; i < names.size(); i++) {
			if (StringUtil::CIEquals(names[i], rowid_name)) {
				rowid_indices.push_back(i);
				break;
			}
		}
	}

	// A rowid name that resolves to no user column is a VIRTUAL rowid: a provider-supplied row identity that
	// is not part of the visible schema (the Delta catalog's stable `_metadata.row_id`). SQL Server's rowid is
	// always real PK/unique columns, so they all resolve and this branch never triggers there.
	vector<string> virtual_rowid_columns;
	if (!rowid_names.empty() && rowid_indices.empty()) {
		virtual_rowid_columns = rowid_names; // none resolved as a user column => treat all as virtual
	} else if (rowid_indices.size() != rowid_names.size()) {
		rowid_indices.clear(); // a partial/mixed resolution is a bad key — disable rowid rather than risk it
	}

	LogicalType rowid_type = LogicalType::BIGINT;
	if (rowid_indices.size() == 1) {
		rowid_type = types[rowid_indices[0]];
	} else if (rowid_indices.size() > 1) {
		child_list_t<LogicalType> children;
		for (auto idx : rowid_indices) {
			children.push_back(make_pair(names[idx], types[idx]));
		}
		rowid_type = LogicalType::STRUCT(std::move(children));
	} else if (virtual_rowid_columns.size() > 1) {
		// Compound virtual key (not used by the Delta provider, which has a single BIGINT row id) — STRUCT
		// of BIGINTs keyed by the virtual names.
		child_list_t<LogicalType> children;
		for (auto &vn : virtual_rowid_columns) {
			children.push_back(make_pair(vn, LogicalType::BIGINT));
		}
		rowid_type = LogicalType::STRUCT(std::move(children));
	}

	auto entry = make_uniq<ArrowNetTableEntry>(catalog, *this, info, handle_, std::move(rowid_indices),
	                                           std::move(rowid_type), std::move(virtual_rowid_columns));
	auto &ref = *entry;
	entries_[table_name] = std::move(entry);
	return &ref;
}

// Builds a ScalarFunction whose callback marshals the arg chunk to Arrow, runs the UDF over the bridge
// (ExecuteScalar with `handle` — 0 for a connection-free GLOBAL scalar, where the C# side resolves by name
// against the global registry), and ingests the single-column result. Shared by catalog-bound scalar UDFs
// (GetOrCreateScalarFunction) and load-time global scalars (RegisterArrowNetGlobalFunctions).
static ScalarFunction BuildArrowNetScalarFunction(ArrowNetHandle handle, const string &schema_name,
                                                  const string &fn_name, vector<LogicalType> arg_types,
                                                  vector<string> arg_names, LogicalType return_type) {
	scalar_function_t exec = [handle, schema_name, fn_name, arg_names](
	                             DataChunk &args, ExpressionState &state, Vector &result) {
		auto &ctx = state.GetContext();
		idx_t row_count = args.size();

		// Marshal the arg chunk -> a one-batch Arrow stream using the chunk's ACTUAL column types (not the
		// declared signature): for a SQLNULL-sentinel ("accept any value") param declared as ANY, DuckDB passes
		// the value UNCAST, so the runtime type (a STRUCT, a VARCHAR, …) is what must be appended. For a
		// concrete-typed param DuckDB has already cast to the declared type, so this equals the signature.
		auto actual_types = args.GetTypes();
		auto properties = arrownet::BoundaryClientProperties(ctx);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(ctx, actual_types);
		ArrowAppender appender(actual_types, row_count, properties, extension_types);
		appender.Append(args, 0, row_count, row_count);
		ArrowArray array = appender.Finalize();

		arrownet::ArrowProducer producer(actual_types, arg_names, properties);
		producer.AddBatch(array);
		producer.Finish();

		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		arrownet::ExecuteScalar(handle, schema_name, fn_name, *producer.Stream(), out);

		// Single-column, row_count-row result -> the output vector (matching offsets).
		arrownet::ArrowStreamReader reader(ctx, out);
		DataChunk chunk;
		chunk.Initialize(Allocator::Get(ctx), reader.Types());
		idx_t offset = 0;
		while (offset < row_count) {
			chunk.Reset();
			reader.Read(chunk);
			idx_t got = chunk.size();
			if (got == 0) {
				break; // defensive: backend returned fewer rows than requested
			}
			VectorOperations::Copy(chunk.data[0], result, got, 0, offset);
			offset += got;
		}
	};

	// Signature: a SQLNULL-typed param is the "accept any value" sentinel (no Arrow type for ANY) → register it
	// as LogicalType::ANY so DuckDB passes any literal (a STRUCT, a VARCHAR, …) UNCAST; the exec marshals the
	// runtime type. Same marker the table/proc named-param path uses (e.g. daxeval's params bag).
	vector<LogicalType> sig_types = arg_types;
	for (auto &t : sig_types) {
		if (t.id() == LogicalTypeId::SQLNULL) {
			t = LogicalType::ANY;
		}
	}
	ScalarFunction fn(sig_types, return_type, exec);
	fn.name = fn_name;
	// A remote UDF may be non-deterministic / side-effecting (VOLATILE => never folded), and may return
	// non-NULL for NULL inputs, so it must see NULL args (SPECIAL_HANDLING) rather than being short-circuited.
	fn.SetStability(FunctionStability::VOLATILE);
	fn.SetNullHandling(FunctionNullHandling::SPECIAL_HANDLING);
	return fn;
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateScalarFunction(ClientContext &context,
                                                                          const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = function_entries_.find(func_name);
	if (cached != function_entries_.end()) {
		return cached->second.get();
	}
	if (scalar_functions_.find(func_name) == scalar_functions_.end()) {
		return nullptr;
	}

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	LogicalType return_type;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
		return_type = FetchFunctionReturnType(context, handle_, name, func_name);
	} catch (std::exception &) {
		// The discovered name is stale — the function no longer exists on the server
		// (e.g. dropped out-of-band). Treat it as not-found rather than erroring.
		scalar_functions_.erase(func_name);
		function_entries_.erase(func_name);
		return nullptr;
	}

	// The per-call execution callback (shared with load-time global scalars).
	ScalarFunction fn = BuildArrowNetScalarFunction(handle_, name, func_name, arg_types, arg_names, return_type);

	CreateScalarFunctionInfo info(std::move(fn));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<ScalarFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	function_entries_[func_name] = std::move(entry);
	return &ref;
}

// -----------------------------------------------------------------------------
// Custom aggregate functions (4h). DuckDB owns a contiguous array of fixed-size state blobs and drives the
// reduction through initialize/update/simple_update/combine/finalize/destructor callbacks. We keep each blob
// as just an int64 id; the real per-group accumulator lives in C# behind that id (a Dictionary keyed by id
// on a per-bound-aggregate session). The callbacks below marshal the id(s) + argument columns over the
// agg_* ABI. Window (OVER) needs no custom `window` callback — DuckDB drives windowing through these same
// update/combine/finalize via WindowSegmentTree, which is far cheaper for a marshaled bridge than one
// boundary crossing per output row; the destructor (wired) bounds the C# map for the window paths that
// churn transient states.
namespace {

// Identity + a monotonic id counter, carried on the AggregateFunction's function_info (aggregate callbacks
// are raw fn pointers — they can't capture). Reachable from initialize (function.function_info) and bind.
// Monotonic ids are never reused, so they never collide across threads or prepared-statement re-executions.
struct ArrowNetAggregateFunctionInfo : public AggregateFunctionInfo {
	ArrowNetHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	std::atomic<int64_t> counter {0};
	bool spillable = false; // bytes-in-blob mode (state serialized into DuckDB's blob → external spill)
};

// Spillable-mode state blob: [uint32 len][byte data[ARROWNET_AGG_SPILL_CAP]] — fixed-size + pointer-free so
// DuckDB's external GROUP BY spills it as raw bytes. A len of this sentinel = fresh/uninitialized (so
// `initialize` needs no C# call).
static constexpr uint32_t AGG_SPILL_SENTINEL = 0xFFFFFFFFu;

// Refcounted holder for the managed aggregate session, carried on the bind data. Its destructor calls
// agg_close (frees the managed id->accumulator map + GCHandle) on plan teardown — best-effort, idempotent.
struct AggSessionHolder {
	ArrowNetHandle session = nullptr;
	~AggSessionHolder() {
		arrownet::AggClose(session);
	}
};

// Per-bound-aggregate state (FunctionData). bind runs once per bound plan; update/combine/finalize/destructor
// reach it via AggregateInputData.bind_data. Carries the managed session + the marshaling context (the
// aggregate callbacks are not handed a ClientContext, unlike scalar/table execution — so we capture what we
// need at bind: client properties, the update-batch extension types, and the connection's stable context).
struct ArrowNetAggregateBindData : public FunctionData {
	shared_ptr<AggSessionHolder> holder;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	ClientProperties properties;
	optional_ptr<ClientContext> context; // connection context (stable across the bound plan); Arrow marshaling
	bool spillable = false;              // bytes-in-blob mode (see ArrowNetAggregateFunctionInfo)

	unique_ptr<FunctionData> Copy() const override {
		auto c = make_uniq<ArrowNetAggregateBindData>();
		c->holder = holder;
		c->arg_types = arg_types;
		c->arg_names = arg_names;
		c->properties = properties;
		c->context = context;
		c->spillable = spillable;
		return std::move(c);
	}
	bool Equals(const FunctionData &other_p) const override {
		return holder == other_p.Cast<ArrowNetAggregateBindData>().holder;
	}
};

// Reads `count` state ids out of a state-pointer Vector via UnifiedVectorFormat — handles FLAT *and*
// CONSTANT (the ungrouped path passes a CONSTANT state vector to finalize/simple_update). The callback
// already receives a pointer to our {int64 id} blob, so Load<int64_t> reads the id directly.
void ReadStateIds(Vector &state, idx_t count, int64_t *out) {
	UnifiedVectorFormat sdata;
	state.ToUnifiedFormat(count, sdata);
	auto ptrs = UnifiedVectorFormat::GetData<data_ptr_t>(sdata);
	for (idx_t i = 0; i < count; i++) {
		out[i] = Load<int64_t>(ptrs[sdata.sel->get_index(i)]);
	}
}

// Marshal a single-column int64 Arrow batch from `ids` (no extension types — BIGINT has none).
ArrowArray BuildIdBatch(const ClientProperties &props, const int64_t *ids, idx_t count) {
	Vector id_vec(LogicalType::BIGINT);
	auto data = FlatVector::GetData<int64_t>(id_vec);
	for (idx_t i = 0; i < count; i++) {
		data[i] = ids[i];
	}
	vector<LogicalType> types {LogicalType::BIGINT};
	DataChunk batch;
	batch.InitializeEmpty(types);
	batch.data[0].Reference(id_vec);
	batch.SetCardinality(count);
	ArrowAppender appender(types, count, props, {});
	appender.Append(batch, 0, count, count);
	return appender.Finalize();
}

// Marshal a `[BIGINT key ++ inputs]` batch (key = state id in fast mode, dense group slot in spill mode).
ArrowArray BuildUpdateBatch(ArrowNetAggregateBindData &bind, Vector &key_vec, Vector inputs[], idx_t input_count,
                            idx_t count) {
	vector<LogicalType> types;
	types.reserve(input_count + 1);
	types.push_back(LogicalType::BIGINT);
	for (idx_t i = 0; i < input_count; i++) {
		types.push_back(bind.arg_types[i]);
	}
	DataChunk batch;
	batch.InitializeEmpty(types);
	batch.data[0].Reference(key_vec);
	for (idx_t i = 0; i < input_count; i++) {
		batch.data[1 + i].Reference(inputs[i]);
	}
	batch.SetCardinality(count);
	// Extension types are recomputed here (the aggregate callbacks get no ClientContext, so we use the
	// connection context captured at bind) — mirrors the scalar path's GetExtensionTypes usage.
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(*bind.context, types);
	ArrowAppender appender(types, count, bind.properties, extension_types);
	appender.Append(batch, 0, count, count);
	return appender.Finalize();
}

// Fast mode: marshal [id ++ inputs] and send to agg_update.
void MarshalAggUpdate(ArrowNetAggregateBindData &bind, Vector &id_vec, Vector inputs[], idx_t input_count,
                      idx_t count) {
	ArrowArray array = BuildUpdateBatch(bind, id_vec, inputs, input_count, count);
	arrownet::AggUpdate(bind.holder->session, array);
}

// ---- Spillable mode (bytes-in-blob): state travels as an Arrow BLOB column, one row per group; a NULL row
// = a fresh/empty group. The state blob is `[uint32 len][byte data]`. ----

// Fill a BLOB Vector with the current serialized state of each group blob (sentinel => NULL).
void FillBlobVector(Vector &vec, const data_ptr_t *blobs, idx_t count) {
	auto data = FlatVector::GetData<string_t>(vec);
	for (idx_t i = 0; i < count; i++) {
		uint32_t len = Load<uint32_t>(blobs[i]);
		if (len == AGG_SPILL_SENTINEL) {
			FlatVector::SetNull(vec, i, true);
		} else {
			data[i] = StringVector::AddStringOrBlob(
			    vec, string_t(reinterpret_cast<const char *>(blobs[i] + sizeof(uint32_t)), len));
		}
	}
}

// Build a single-column BLOB Arrow array holding the current serialized state of each group blob.
ArrowArray BuildSpillStateColumn(const ClientProperties &props, const data_ptr_t *blobs, idx_t count) {
	Vector vec(LogicalType::BLOB);
	FillBlobVector(vec, blobs, count);
	vector<LogicalType> types {LogicalType::BLOB};
	DataChunk chunk;
	chunk.InitializeEmpty(types);
	chunk.data[0].Reference(vec);
	chunk.SetCardinality(count);
	ArrowAppender appender(types, count == 0 ? 1 : count, props, {});
	appender.Append(chunk, 0, count, count);
	return appender.Finalize();
}

// Read a BLOB stream of new per-group state back into the group blobs (len-prefixed, capped).
void WriteBackSpillStates(ClientContext &context, ArrowArrayStream &out, const data_ptr_t *blobs, idx_t count) {
	arrownet::ArrowStreamReader reader(context, out);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(context), reader.Types());
	idx_t produced = 0;
	while (produced < count) {
		chunk.Reset();
		reader.Read(chunk);
		idx_t got = chunk.size();
		if (got == 0) {
			break;
		}
		UnifiedVectorFormat ovf;
		chunk.data[0].ToUnifiedFormat(got, ovf);
		auto strs = UnifiedVectorFormat::GetData<string_t>(ovf);
		for (idx_t j = 0; j < got; j++) {
			auto sv = strs[ovf.sel->get_index(j)];
			idx_t len = sv.GetSize();
			if (len > ARROWNET_AGG_SPILL_CAP) {
				throw InvalidInputException(
				    "mssql_net: spillable aggregate state is %llu bytes, exceeding the %d-byte cap", (idx_t)len,
				    ARROWNET_AGG_SPILL_CAP);
			}
			data_ptr_t blob = blobs[produced + j];
			Store<uint32_t>(static_cast<uint32_t>(len), blob);
			std::memcpy(blob + sizeof(uint32_t), sv.GetData(), len);
		}
		produced += got;
	}
}

// Spillable update: group rows by state-blob pointer (dense slots), read each group's current state, run the
// chunk's rows for each group in C#, and write the new serialized state back into the blobs.
void AggUpdateSpillImpl(ArrowNetAggregateBindData &bind, Vector inputs[], idx_t input_count, Vector &state,
                        idx_t count) {
	UnifiedVectorFormat sdata;
	state.ToUnifiedFormat(count, sdata);
	auto ptrs = UnifiedVectorFormat::GetData<data_ptr_t>(sdata);
	std::unordered_map<data_ptr_t, idx_t> slot_of;
	vector<data_ptr_t> group_blobs;
	Vector slot_vec(LogicalType::BIGINT);
	auto slot_data = FlatVector::GetData<int64_t>(slot_vec);
	for (idx_t i = 0; i < count; i++) {
		data_ptr_t blob = ptrs[sdata.sel->get_index(i)];
		auto it = slot_of.find(blob);
		idx_t slot;
		if (it == slot_of.end()) {
			slot = group_blobs.size();
			slot_of.emplace(blob, slot);
			group_blobs.push_back(blob);
		} else {
			slot = it->second;
		}
		slot_data[i] = static_cast<int64_t>(slot);
	}
	idx_t g = group_blobs.size();
	ArrowArray group_states = BuildSpillStateColumn(bind.properties, group_blobs.data(), g);
	ArrowArray batch = BuildUpdateBatch(bind, slot_vec, inputs, input_count, count);
	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	arrownet::AggUpdateSpill(bind.holder->session, group_states, batch, out);
	WriteBackSpillStates(*bind.context, out, group_blobs.data(), g);
}

// Spillable combine: assign dense slots to the distinct TARGET blobs (a target may repeat in one combine
// batch — the window segment-tree merges several source nodes into one frame target), build a `[slot, source]`
// batch, merge in C#, and write each distinct target's merged state back.
void AggCombineSpillImpl(ArrowNetAggregateBindData &bind, Vector &source, Vector &target, idx_t count) {
	UnifiedVectorFormat sf, tf;
	source.ToUnifiedFormat(count, sf);
	target.ToUnifiedFormat(count, tf);
	auto sptr = UnifiedVectorFormat::GetData<data_ptr_t>(sf);
	auto tptr = UnifiedVectorFormat::GetData<data_ptr_t>(tf);

	std::unordered_map<data_ptr_t, idx_t> slot_of;
	vector<data_ptr_t> target_blobs;
	vector<data_ptr_t> source_blobs(count);
	Vector slot_vec(LogicalType::BIGINT);
	auto slot_data = FlatVector::GetData<int64_t>(slot_vec);
	for (idx_t i = 0; i < count; i++) {
		data_ptr_t tgt = tptr[tf.sel->get_index(i)];
		auto it = slot_of.find(tgt);
		idx_t slot;
		if (it == slot_of.end()) {
			slot = target_blobs.size();
			slot_of.emplace(tgt, slot);
			target_blobs.push_back(tgt);
		} else {
			slot = it->second;
		}
		slot_data[i] = static_cast<int64_t>(slot);
		source_blobs[i] = sptr[sf.sel->get_index(i)];
	}
	idx_t g = target_blobs.size();

	ArrowArray target_states = BuildSpillStateColumn(bind.properties, target_blobs.data(), g);
	// batch = [int64 slot, BLOB source]
	Vector src_vec(LogicalType::BLOB);
	FillBlobVector(src_vec, source_blobs.data(), count);
	vector<LogicalType> types {LogicalType::BIGINT, LogicalType::BLOB};
	DataChunk batch_chunk;
	batch_chunk.InitializeEmpty(types);
	batch_chunk.data[0].Reference(slot_vec);
	batch_chunk.data[1].Reference(src_vec);
	batch_chunk.SetCardinality(count);
	ArrowAppender appender(types, count, bind.properties, {});
	appender.Append(batch_chunk, 0, count, count);
	ArrowArray batch = appender.Finalize();

	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	arrownet::AggCombineSpill(bind.holder->session, target_states, batch, out);
	WriteBackSpillStates(*bind.context, out, target_blobs.data(), g);
}

// Spillable finalize: read each group's serialized state, finalize in C#, copy the result column out.
void AggFinalizeSpillImpl(ArrowNetAggregateBindData &bind, Vector &state, Vector &result, idx_t count,
                          idx_t offset) {
	UnifiedVectorFormat sf;
	state.ToUnifiedFormat(count, sf);
	auto ptr = UnifiedVectorFormat::GetData<data_ptr_t>(sf);
	vector<data_ptr_t> blobs(count);
	for (idx_t i = 0; i < count; i++) {
		blobs[i] = ptr[sf.sel->get_index(i)];
	}
	ArrowArray states = BuildSpillStateColumn(bind.properties, blobs.data(), count);
	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	arrownet::AggFinalizeSpill(bind.holder->session, states, out);
	arrownet::ArrowStreamReader reader(*bind.context, out);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(*bind.context), reader.Types());
	idx_t produced = 0;
	while (produced < count) {
		chunk.Reset();
		reader.Read(chunk);
		idx_t got = chunk.size();
		if (got == 0) {
			break;
		}
		VectorOperations::Copy(chunk.data[0], result, got, 0, offset + produced);
		produced += got;
	}
}

idx_t ArrowNetAggregateStateSize(const AggregateFunction &function) {
	auto &info = function.function_info->Cast<ArrowNetAggregateFunctionInfo>();
	// Spillable: a fixed, pointer-free serialized-state blob ([uint32 len][data]); else an int64 id.
	return info.spillable ? (sizeof(uint32_t) + ARROWNET_AGG_SPILL_CAP) : sizeof(int64_t);
}

void ArrowNetAggregateInit(const AggregateFunction &function, data_ptr_t state) {
	auto &info = function.function_info->Cast<ArrowNetAggregateFunctionInfo>();
	if (info.spillable) {
		Store<uint32_t>(AGG_SPILL_SENTINEL, state); // fresh/uninitialized; no C# call needed at init
	} else {
		Store<int64_t>(info.counter.fetch_add(1, std::memory_order_relaxed), state);
	}
}

unique_ptr<FunctionData> ArrowNetAggregateBind(ClientContext &context, AggregateFunction &function,
                                               vector<unique_ptr<Expression>> &) {
	auto &info = function.function_info->Cast<ArrowNetAggregateFunctionInfo>();
	auto bind_data = make_uniq<ArrowNetAggregateBindData>();
	bind_data->holder = make_shared_ptr<AggSessionHolder>();
	bind_data->holder->session = arrownet::AggOpen(info.handle, info.schema, info.func);
	bind_data->arg_types = info.arg_types;
	bind_data->arg_names = info.arg_names;
	bind_data->properties = arrownet::BoundaryClientProperties(context);
	bind_data->context = &context;
	bind_data->spillable = info.spillable;
	return std::move(bind_data);
}

// Grouped GROUP BY: a FLAT vector of one state pointer per row (rows belong to different groups).
void ArrowNetAggregateUpdate(Vector inputs[], AggregateInputData &aggr_input_data, idx_t input_count, Vector &state,
                             idx_t count) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<ArrowNetAggregateBindData>();
	if (bind.spillable) {
		AggUpdateSpillImpl(bind, inputs, input_count, state, count);
		return;
	}
	Vector id_vec(LogicalType::BIGINT);
	ReadStateIds(state, count, FlatVector::GetData<int64_t>(id_vec));
	MarshalAggUpdate(bind, id_vec, inputs, input_count, count);
}

// Ungrouped fast path: all `count` rows fold into one state (no per-row state vector).
void ArrowNetAggregateSimpleUpdate(Vector inputs[], AggregateInputData &aggr_input_data, idx_t input_count,
                                   data_ptr_t state, idx_t count) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<ArrowNetAggregateBindData>();
	Vector key_vec(LogicalType::BIGINT);
	auto data = FlatVector::GetData<int64_t>(key_vec);
	if (bind.spillable) {
		// All rows fold into the single state `state`: one group (slot 0).
		for (idx_t i = 0; i < count; i++) {
			data[i] = 0;
		}
		ArrowArray group_states = BuildSpillStateColumn(bind.properties, &state, 1);
		ArrowArray batch = BuildUpdateBatch(bind, key_vec, inputs, input_count, count);
		ArrowArrayStream out;
		std::memset(&out, 0, sizeof(out));
		arrownet::AggUpdateSpill(bind.holder->session, group_states, batch, out);
		WriteBackSpillStates(*bind.context, out, &state, 1);
		return;
	}
	int64_t id = Load<int64_t>(state);
	for (idx_t i = 0; i < count; i++) {
		data[i] = id;
	}
	MarshalAggUpdate(bind, key_vec, inputs, input_count, count);
}

// Merge partial states (parallel/windowed aggregation): source[i] merged into target[i].
void ArrowNetAggregateCombine(Vector &source, Vector &target, AggregateInputData &aggr_input_data, idx_t count) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<ArrowNetAggregateBindData>();
	if (bind.spillable) {
		AggCombineSpillImpl(bind, source, target, count);
		return;
	}
	vector<int64_t> tgt(count), src(count);
	ReadStateIds(target, count, tgt.data());
	ReadStateIds(source, count, src.data());
	Vector tgt_vec(LogicalType::BIGINT);
	Vector src_vec(LogicalType::BIGINT);
	auto tgt_data = FlatVector::GetData<int64_t>(tgt_vec);
	auto src_data = FlatVector::GetData<int64_t>(src_vec);
	for (idx_t i = 0; i < count; i++) {
		tgt_data[i] = tgt[i];
		src_data[i] = src[i];
	}
	vector<LogicalType> types {LogicalType::BIGINT, LogicalType::BIGINT};
	DataChunk batch;
	batch.InitializeEmpty(types);
	batch.data[0].Reference(tgt_vec); // column 0 = target_id, column 1 = source_id (C# merges source -> target)
	batch.data[1].Reference(src_vec);
	batch.SetCardinality(count);
	ArrowAppender appender(types, count, bind.properties, {});
	appender.Append(batch, 0, count, count);
	ArrowArray array = appender.Finalize();
	arrownet::AggCombine(bind.holder->session, array);
}

// Produce each group's result. `state` may be CONSTANT (ungrouped) or FLAT (grouped). The managed side
// returns one column of `count` results in id order (an absent id => a fresh accumulator => empty value).
void ArrowNetAggregateFinalize(Vector &state, AggregateInputData &aggr_input_data, Vector &result, idx_t count,
                               idx_t offset) {
	if (count == 0) {
		return;
	}
	auto &bind = aggr_input_data.bind_data->Cast<ArrowNetAggregateBindData>();
	if (bind.spillable) {
		AggFinalizeSpillImpl(bind, state, result, count, offset);
		return;
	}
	vector<int64_t> ids(count);
	ReadStateIds(state, count, ids.data());
	ArrowArray array = BuildIdBatch(bind.properties, ids.data(), count);
	ArrowArrayStream out;
	std::memset(&out, 0, sizeof(out));
	arrownet::AggFinalize(bind.holder->session, array, out);
	if (!bind.context) {
		if (out.release) {
			out.release(&out);
		}
		throw InternalException("mssql_net: aggregate finalize is missing its client context");
	}
	arrownet::ArrowStreamReader reader(*bind.context, out);
	DataChunk chunk;
	chunk.Initialize(Allocator::Get(*bind.context), reader.Types());
	idx_t produced = 0;
	while (produced < count) {
		chunk.Reset();
		reader.Read(chunk);
		idx_t got = chunk.size();
		if (got == 0) {
			break; // defensive: backend returned fewer rows than requested
		}
		VectorOperations::Copy(chunk.data[0], result, got, 0, offset + produced);
		produced += got;
	}
}

// Free the managed accumulators for these states. Wired so the window paths (which churn many transient
// states) don't accumulate unbounded; the bind-data destructor's agg_close is the backstop. Must not throw.
void ArrowNetAggregateDestroy(Vector &state, AggregateInputData &aggr_input_data, idx_t count) {
	if (count == 0) {
		return;
	}
	try {
		auto &bind = aggr_input_data.bind_data->Cast<ArrowNetAggregateBindData>();
		if (bind.spillable) {
			return; // spillable state lives inline in the blob — nothing in C# to free
		}
		vector<int64_t> ids(count);
		ReadStateIds(state, count, ids.data());
		ArrowArray array = BuildIdBatch(bind.properties, ids.data(), count);
		arrownet::AggDestroy(bind.holder->session, array);
	} catch (...) {
		// A destructor must not throw — memory cleanup is best-effort (the session close frees the rest).
	}
}

} // namespace

// Builds an AggregateFunction whose state-vectorized callbacks marshal per-group int64 ids + Arrow batches to
// the C# session over the agg_* ABI (ExecuteScalar's aggregate analog). `handle` = 0 for a connection-free
// GLOBAL aggregate (C# resolves by name); `spillable` selects the bytes-in-blob mode (the callbacks branch on
// the flag). Shared by catalog-bound aggregates (GetOrCreateAggregateFunction) + load-time global aggregates.
static AggregateFunction BuildArrowNetAggregateFunction(ArrowNetHandle handle, const string &schema_name,
                                                        const string &func_name, vector<LogicalType> arg_types,
                                                        vector<string> arg_names, LogicalType return_type,
                                                        bool spillable) {
	AggregateFunction fn(func_name, arg_types, return_type, ArrowNetAggregateStateSize, ArrowNetAggregateInit,
	                     ArrowNetAggregateUpdate, ArrowNetAggregateCombine, ArrowNetAggregateFinalize,
	                     FunctionNullHandling::DEFAULT_NULL_HANDLING, ArrowNetAggregateSimpleUpdate,
	                     ArrowNetAggregateBind, ArrowNetAggregateDestroy);
	auto fn_info = make_shared_ptr<ArrowNetAggregateFunctionInfo>();
	fn_info->handle = handle;
	fn_info->schema = schema_name;
	fn_info->func = func_name;
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->spillable = spillable;
	fn.function_info = std::move(fn_info);
	return fn;
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateAggregateFunction(ClientContext &context,
                                                                             const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = aggregate_function_entries_.find(func_name);
	if (cached != aggregate_function_entries_.end()) {
		return cached->second.get();
	}
	auto spill_it = aggregate_functions_.find(func_name);
	if (spill_it == aggregate_functions_.end()) {
		return nullptr;
	}
	bool spillable = spill_it->second;

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	LogicalType return_type;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
		return_type = FetchFunctionReturnType(context, handle_, name, func_name);
	} catch (std::exception &) {
		// Stale discovery (the function no longer exists) — treat as not-found, like the scalar path.
		aggregate_functions_.erase(func_name);
		aggregate_function_entries_.erase(func_name);
		return nullptr;
	}

	AggregateFunction fn =
	    BuildArrowNetAggregateFunction(handle_, name, func_name, arg_types, arg_names, return_type, spillable);

	AggregateFunctionSet set(func_name);
	set.AddFunction(fn);
	CreateAggregateFunctionInfo info(std::move(set));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<AggregateFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	aggregate_function_entries_[func_name] = std::move(entry);
	return &ref;
}

namespace {

// Carried on the registered TableFunction so its (static) bind can recover the catalog
// identity + signature of a discovered TVF (table_function_bind_t is a raw fn pointer,
// so it can't capture — unlike the scalar callback's std::function).
struct ArrowNetTableFunctionInfo : public TableFunctionInfo {
	ArrowNetHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	bool is_proc = false;    // stored procedure (EXEC, no pushdown) vs TVF (FROM, pushdown)
	// The function's source orders strings the way DuckDB does (byte/binary), so string ordering comparisons +
	// BETWEEN are superset-safe to push (e.g. a Delta/Parquet reader — byte-ordered stats). Default false:
	// discovered SQL TVFs run on SQL Server under its (possibly case-insensitive) collation, so only string
	// equality is pushed for them. Set true for a byte-ordered global host-FS reader (declared in C#). Copied
	// onto the scan bind data so ArrowNetComplexFilterPushdown's FilterSerializer honors it.
	bool string_order_pushable = false;
};

// Per-plan binding handle for the session-model table functions (table_bind / table_execute / table_close).
// Held (refcounted) on the bind data's scan factory; its destructor frees the managed binding at plan
// teardown. The per-execution provider connection lives in table_execute's result stream (released by the
// arrow scan at teardown), so the binding itself holds no connection — table_close is metadata cleanup.
struct TableBindState {
	ArrowNetHandle binding = nullptr;
	bool supports_pushdown = false;
	~TableBindState() {
		arrownet::TableClose(binding);
	}
};

// Bind a catalog-bound TVF / proc / custom table function (Phase 5 session model): table_bind resolves the
// output schema (return types) + pushdown + an opaque binding, then a scan factory runs table_execute over
// that binding per execution (which streams the result rows). See TableBindState + abi.h.
unique_ptr<FunctionData> ArrowNetTableFunctionBind(ClientContext &context, TableFunctionBindInput &input,
                                                   vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<ArrowNetTableFunctionInfo>();
	ArrowNetHandle handle = info.handle;
	string schema_name = info.schema;
	string func_name = info.func;
	bool is_proc = info.is_proc;

	// Resolve the values to marshal into the 1-row args batch (the field NAMES become the
	// proc parameter names that C# uses to build `EXEC @name=@p`). TVFs: all params,
	// positional, in order (`input.inputs`). Procs: only the SUPPLIED named parameters
	// (`input.named_parameters`), each cast to its declared type — omitted params are absent.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	if (is_proc) {
		for (auto &kv : input.named_parameters) {
			LogicalType declared = kv.second.type();
			for (idx_t i = 0; i < info.arg_names.size(); i++) {
				if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
					// A SQLNULL-declared param is the "accept any value" marker (registered as ANY):
					// keep the supplied value's RUNTIME type so a STRUCT bag (or a VARCHAR, …) marshals
					// across as its real Arrow type, not coerced to the declared type.
					if (info.arg_types[i].id() != LogicalTypeId::SQLNULL) {
						declared = info.arg_types[i];
					}
					break;
				}
			}
			arg_names.push_back(kv.first);
			arg_types.push_back(declared);
			arg_values.push_back(kv.second);
		}
	} else {
		arg_types = info.arg_types;
		arg_names = info.arg_names;
		arg_values = input.inputs;
	}

	auto bind_data = make_uniq<arrownet::ArrowStreamBindData>();
	// A byte-ordered source (e.g. a Delta/Parquet global reader) can safely push string ordering + BETWEEN;
	// discovered SQL TVFs leave this false (collation-dependent). Read by the shared FilterSerializer.
	bind_data->string_order_pushable = info.string_order_pushable;

	auto properties = arrownet::BoundaryClientProperties(context);
	auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);

	// Marshal the constant call args into a 1-row Arrow array (shared by the output-schema resolution and the
	// scan): a custom table function's output schema MAY depend on the args, so they cross at bind time too.
	auto marshal_args = [arg_types, arg_values, properties, extension_types]() -> ArrowArray {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		ArrowAppender appender(arg_types, 1, properties, extension_types);
		appender.Append(chunk, 0, 1, 1);
		return appender.Finalize();
	};

	// 1) Bind the call (Phase 5 session model): table_bind resolves the output schema (-> return types),
	//    whether the host should push the projection, and an opaque binding handle reused by every execution.
	//    The managed side classifies the function (TVF / proc / custom), so the host no longer branches on
	//    is_proc here (is_proc above is only the named-vs-positional arg marshaling). The binding is freed at
	//    plan teardown via the refcounted TableBindState captured on the scan factory.
	auto bind_state = make_shared_ptr<TableBindState>();
	bind_data->factory = [handle, schema_name, func_name, arg_types, arg_names, properties, marshal_args,
	                      bind_state](const arrownet::ArrowScanRequest &, ArrowArrayStream &out) {
		ArrowArray array = marshal_args();
		arrownet::ArrowProducer producer(arg_types, arg_names, properties);
		producer.AddBatch(array);
		producer.Finish();
		bind_state->binding = arrownet::TableBind(handle, schema_name, func_name, producer.Stream(), out,
		                                          bind_state->supports_pushdown);
	};
	arrownet::PopulateReturnSchema(context, *bind_data, return_types, names);

	// 2) Scan factory: table_execute over the bound binding (per execution). spec_json/filter_values push
	//    projection + filter into the SELECT when the binding supports it (a discovered TVF); else ignored.
	bind_data->factory = [bind_state](const arrownet::ArrowScanRequest &req, ArrowArrayStream &out) {
		arrownet::TableExecute(bind_state->binding, req.spec_json, req.filter_values, out);
	};
	bind_data->push_projection = bind_state->supports_pushdown;
	return std::move(bind_data);
}


// =============================================================================
// Phase 6 streaming table-in-out EXCHANGE operator (read-only). Replaces the push/materialize model
// above for custom C# in-out (and, in 6.2, discovered TVFs): two pull-based Arrow streams coordinated by
// a C++ "gate" mutex, no per-chunk materialization. The host exports the INPUT stream (its get_next hands
// the current gate-holder's one input chunk to C#); C# exports the OUTPUT stream, which the host pulls —
// a non-empty batch = HAVE_MORE_OUTPUT, a length-0 batch = NEED_MORE_INPUT (the per-input sentinel C#
// yields), a released array = FINISHED. One binding (bound once) runs one exchange per execution. EOF is
// the injected OperatorFinalize (sets input_eof + drains), not a producer counter. See abi.h / the plan.
// =============================================================================

struct ArrowNetExchangeGlobalState;

// Refcounted, on the bind data (survives prepared re-executions). Owns the bound binding handle (freed
// once via inout_bind_close) and points at the CURRENT execution's global state for the EOF signal.
struct ExchangeHolder {
	mutex lock;
	ArrowNetHandle binding = nullptr;
	ArrowNetExchangeGlobalState *active = nullptr; // set at init_global; cleared at its dtor
	~ExchangeHolder();
	void Finish(ClientContext &context); // forwards to active->FinishEof (single all-input-done signal)
};

struct ArrowNetExchangeGlobalState : public GlobalTableFunctionState {
	mutex gate;                  // serializes one input chunk's full cycle across parallel branch pipelines
	ArrowArray slot {};          // the single input handoff (set by the gate-holder, moved out by input get_next)
	bool slot_full = false;
	bool input_eof = false;
	bool finished = false;
	duckdb::unique_ptr<arrownet::ArrowStreamReader> reader; // the C# output stream (sentinel-aware pull)
	// Captured for the host-side input stream's get_schema/get_next callbacks.
	vector<LogicalType> input_types;
	vector<string> input_names;
	ClientProperties props;
	string input_error;
	ExchangeHolder *holder = nullptr;

	idx_t MaxThreads() const override {
		return 1; // intra-pipeline cap; parallel UNION branches are separate pipelines (serialized by the gate)
	}

	// Single "all input consumed" action (idempotent): mark EOF and drain the output to its terminal null so
	// the managed DoExchange exits its loop and disposes (commit / connection close). Read-only => no tail rows.
	void FinishEof(ClientContext &context) {
		lock_guard<mutex> guard(gate);
		if (finished) {
			return;
		}
		finished = true;
		input_eof = true;
		if (!reader) {
			return;
		}
		DataChunk scratch;
		scratch.Initialize(Allocator::Get(context), reader->Types());
		while (true) {
			auto pr = reader->Pull();
			if (pr == arrownet::ArrowStreamReader::PullResult::END) {
				break;
			}
			if (pr == arrownet::ArrowStreamReader::PullResult::DATA) {
				while (reader->HasPending()) {
					reader->Drain(scratch);
				}
			}
		}
	}

	~ArrowNetExchangeGlobalState() override {
		// Release the output stream first (the managed dispose tears down the exchange + the imported input
		// stream, so input get_next won't fire again), THEN free the slot.
		reader.reset();
		if (slot_full && slot.release) {
			slot.release(&slot);
		}
		if (holder) {
			lock_guard<mutex> guard(holder->lock);
			if (holder->active == this) {
				holder->active = nullptr;
			}
		}
	}
};

ExchangeHolder::~ExchangeHolder() {
	if (binding) {
		arrownet::InOutBindClose(binding); // best-effort; swallows errors
		binding = nullptr;
	}
}

void ExchangeHolder::Finish(ClientContext &context) {
	lock_guard<mutex> guard(lock);
	if (active) {
		active->FinishEof(context);
	}
}

// Bind data (reused across prepared re-executions; the holder is shared, the binding bound once).
struct ArrowNetExchangeBindData : public TableFunctionData {
	ArrowNetHandle handle = nullptr;
	string schema;
	string func;
	vector<LogicalType> input_types;
	vector<string> input_names;
	shared_ptr<ExchangeHolder> holder;
};

struct ArrowNetExchangeLocalState : public LocalTableFunctionState {
	bool owns_gate = false; // this thread holds the gate for the current input chunk's cycle
};

// Host-side INPUT stream callbacks. private_data == the global state. Only the current gate-holder sets the
// slot, and C# pulls exactly once per tenure (after the sentinel), so a "slot empty, not EOF" pull means the
// transform read ahead of the gate (a missing sentinel) — surfaced as a stream error, never silent.
int ExchangeInputGetSchema(ArrowArrayStream *stream, ArrowSchema *out) {
	auto *g = reinterpret_cast<ArrowNetExchangeGlobalState *>(stream->private_data);
	ArrowConverter::ToArrowSchema(out, g->input_types, g->input_names, g->props);
	return 0;
}
int ExchangeInputGetNext(ArrowArrayStream *stream, ArrowArray *out) {
	auto *g = reinterpret_cast<ArrowNetExchangeGlobalState *>(stream->private_data);
	if (g->slot_full) {
		*out = g->slot; // move the gate-holder's chunk to C#
		g->slot = ArrowArray {};
		g->slot_full = false;
		return 0;
	}
	if (g->input_eof) {
		out->release = nullptr; // EOF
		return 0;
	}
	g->input_error = "ArrowNet: in-out transform requested input before yielding a sentinel (single-slot gate)";
	return 1;
}
const char *ExchangeInputGetLastError(ArrowArrayStream *stream) {
	auto *g = reinterpret_cast<ArrowNetExchangeGlobalState *>(stream->private_data);
	return g->input_error.empty() ? nullptr : g->input_error.c_str();
}
void ExchangeInputRelease(ArrowArrayStream *stream) {
	stream->release = nullptr; // the global state (private_data) is not owned by the stream
}

// Bind: resolve the binding + its full output schema via inout_bind (no cost args for custom in-out yet).
unique_ptr<FunctionData> ArrowNetExchangeBind(ClientContext &context, TableFunctionBindInput &input,
                                              vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<ArrowNetTableFunctionInfo>();
	auto bind_data = make_uniq<ArrowNetExchangeBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->holder = make_shared_ptr<ExchangeHolder>();
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		bind_data->input_types.push_back(input.input_table_types[i]);
		bind_data->input_names.push_back(input.input_table_names[i]);
	}

	auto props = arrownet::BoundaryClientProperties(context);
	ArrowSchema input_schema;
	std::memset(&input_schema, 0, sizeof(input_schema));
	ArrowConverter::ToArrowSchema(&input_schema, bind_data->input_types, bind_data->input_names, props);

	// Marshal the SUPPLIED constant args (named parameters) into a 1-row Arrow stream for inout_bind — e.g.
	// daxevaltable(<input>, expression := 'EVALUATE …'). A custom in-out with no declared args, and every
	// discovered `_each` (which declares no named parameters — its per-row arg values come from input
	// columns), supplies none => args stays null (unchanged behavior). Mirrors the table-function bind.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	for (auto &kv : input.named_parameters) {
		LogicalType declared = kv.second.type();
		for (idx_t i = 0; i < info.arg_names.size(); i++) {
			if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
				declared = info.arg_types[i];
				break;
			}
		}
		arg_names.push_back(kv.first);
		arg_types.push_back(declared);
		arg_values.push_back(kv.second);
	}
	arrownet::ArrowProducer arg_producer(arg_types, arg_names, props);
	ArrowArrayStream *args_ptr = nullptr;
	if (!arg_values.empty()) {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
		ArrowAppender appender(arg_types, 1, props, extension_types);
		appender.Append(chunk, 0, 1, 1);
		arg_producer.AddBatch(appender.Finalize());
		arg_producer.Finish();
		args_ptr = arg_producer.Stream();
	}

	ArrowArrayStream out_schema;
	std::memset(&out_schema, 0, sizeof(out_schema));
	bind_data->holder->binding =
	    arrownet::InOutBind(info.handle, info.schema, info.func, args_ptr, input_schema, out_schema);

	ArrowSchemaWrapper schema_root;
	if (out_schema.get_schema(&out_schema, &schema_root.arrow_schema) != 0) {
		const char *msg = out_schema.get_last_error ? out_schema.get_last_error(&out_schema) : nullptr;
		if (out_schema.release) {
			out_schema.release(&out_schema);
		}
		throw IOException(string("mssql_net: failed to read in-out exchange output schema") +
		                  (msg ? string(": ") + msg : string()));
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
	return std::move(bind_data);
}

unique_ptr<GlobalTableFunctionState> ArrowNetExchangeInitGlobal(ClientContext &context,
                                                               TableFunctionInitInput &input) {
	auto &bind = input.bind_data->Cast<ArrowNetExchangeBindData>();
	auto gstate = make_uniq<ArrowNetExchangeGlobalState>();
	gstate->holder = bind.holder.get();
	gstate->input_types = bind.input_types;
	gstate->input_names = bind.input_names;
	gstate->props = arrownet::BoundaryClientProperties(context);

	ArrowArrayStream input_stream;
	std::memset(&input_stream, 0, sizeof(input_stream));
	input_stream.private_data = gstate.get();
	input_stream.get_schema = ExchangeInputGetSchema;
	input_stream.get_next = ExchangeInputGetNext;
	input_stream.get_last_error = ExchangeInputGetLastError;
	input_stream.release = ExchangeInputRelease;

	ArrowArrayStream output_stream;
	std::memset(&output_stream, 0, sizeof(output_stream));
	// A proc `_each` runs its per-row EXEC on DuckDB's pinned write connection (BeginWrite), so the binding
	// must see this transaction's id when it opens that connection (read-your-writes + commit/rollback with
	// DuckDB's transaction). The id rides the per-thread ambient; also re-set in the Execute function (the
	// connection is opened lazily on the first output pull there). See docs/transaction-concurrency.md.
	ArrowNetSetActiveTxn(nullptr, context);
	arrownet::InOutExchangeOpen(bind.holder->binding, input_stream, output_stream);
	gstate->reader = make_uniq<arrownet::ArrowStreamReader>(context, output_stream);

	lock_guard<mutex> guard(bind.holder->lock);
	bind.holder->active = gstate.get();
	return std::move(gstate);
}

unique_ptr<LocalTableFunctionState> ArrowNetExchangeInitLocal(ExecutionContext &, TableFunctionInitInput &,
                                                              GlobalTableFunctionState *) {
	return make_uniq<ArrowNetExchangeLocalState>();
}

// The gate-based operator. The gate is held across the whole chunk cycle (multiple Execute calls during
// HAVE_MORE_OUTPUT) — ownership in the per-thread local state — and released on the sentinel/EOF or on a
// thrown managed error (so the gate never leaks).
OperatorResultType ArrowNetExchangeFunction(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                            DataChunk &output) {
	auto &bind = data.bind_data->Cast<ArrowNetExchangeBindData>();
	auto &g = data.global_state->Cast<ArrowNetExchangeGlobalState>();
	auto &l = data.local_state->Cast<ArrowNetExchangeLocalState>();
	// A proc `_each` opens its pinned write connection (BeginWrite) lazily on the first output pull below,
	// which runs on THIS thread — so set the active transaction id here so it joins DuckDB's transaction
	// (read-your-writes + commit/rollback with DuckDB). Harmless for a TVF `_each` (its own read connection).
	ArrowNetSetActiveTxn(nullptr, context.client);
	try {
		if (!l.owns_gate) {
			if (input.size() == 0) {
				output.SetCardinality(0);
				return OperatorResultType::NEED_MORE_INPUT;
			}
			g.gate.lock();
			l.owns_gate = true;
			// Export this input chunk into the single slot for the C# input pull.
			auto props = arrownet::BoundaryClientProperties(context.client);
			auto ext = ArrowTypeExtensionData::GetExtensionTypes(context.client, bind.input_types);
			ArrowAppender appender(bind.input_types, input.size(), props, ext);
			appender.Append(input, 0, input.size(), input.size());
			g.slot = appender.Finalize();
			g.slot_full = true;
		}
		// Drain a pending output array (one C# batch may exceed STANDARD_VECTOR_SIZE).
		if (g.reader->HasPending()) {
			g.reader->Drain(output);
			return OperatorResultType::HAVE_MORE_OUTPUT;
		}
		auto pr = g.reader->Pull();
		if (pr == arrownet::ArrowStreamReader::PullResult::SENTINEL) {
			l.owns_gate = false;
			g.gate.unlock(); // hand the gate to the next branch
			output.SetCardinality(0);
			return OperatorResultType::NEED_MORE_INPUT;
		}
		if (pr == arrownet::ArrowStreamReader::PullResult::END) {
			l.owns_gate = false;
			g.gate.unlock();
			output.SetCardinality(0);
			return OperatorResultType::FINISHED;
		}
		g.reader->Drain(output);
		return OperatorResultType::HAVE_MORE_OUTPUT;
	} catch (...) {
		if (l.owns_gate) {
			l.owns_gate = false;
			g.gate.unlock(); // never leak the gate on a managed error
		}
		throw;
	}
}

// -----------------------------------------------------------------------------

// The exchange analog: forwards rows 1:1 and drives the exchange EOF (set input_eof + drain the output to
// terminal-null so the managed DoExchange finishes + disposes) once, sink-level, after all branches.
class ArrowNetExchangeFinalizePhysical : public PhysicalOperator {
public:
	ArrowNetExchangeFinalizePhysical(PhysicalPlan &physical_plan, vector<LogicalType> types,
	                                 idx_t estimated_cardinality, shared_ptr<ExchangeHolder> holder)
	    : PhysicalOperator(physical_plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
	      holder(std::move(holder)) {
	}

	shared_ptr<ExchangeHolder> holder;

	string GetName() const override {
		return "ARROWNET_INOUT_EXCHANGE_FINALIZE";
	}

	OperatorResultType Execute(ExecutionContext &, DataChunk &input, DataChunk &chunk, GlobalOperatorState &,
	                           OperatorState &) const override {
		chunk.Reference(input);
		return OperatorResultType::NEED_MORE_INPUT;
	}

	bool ParallelOperator() const override {
		return true;
	}

	bool RequiresOperatorFinalize() const override {
		return true;
	}

	OperatorFinalResultType OperatorFinalize(Pipeline &, Event &, ClientContext &context,
	                                         OperatorFinalizeInput &) const override {
		holder->Finish(context); // single all-input-done signal (idempotent): EOF + drain
		return OperatorFinalResultType::FINISHED;
	}
};

struct ArrowNetExchangeFinalizeOperator : public LogicalExtensionOperator {
	explicit ArrowNetExchangeFinalizeOperator(unique_ptr<LogicalOperator> child, shared_ptr<ExchangeHolder> holder)
	    : holder(std::move(holder)) {
		children.push_back(std::move(child));
	}

	shared_ptr<ExchangeHolder> holder;

	PhysicalOperator &CreatePlan(ClientContext &, PhysicalPlanGenerator &planner) override {
		auto &child_plan = planner.CreatePlan(*children[0]);
		auto &op = planner.Make<ArrowNetExchangeFinalizePhysical>(children[0]->types,
		                                                          children[0]->estimated_cardinality, holder);
		op.children.push_back(child_plan);
		return op;
	}

	vector<ColumnBinding> GetColumnBindings() override {
		return children[0]->GetColumnBindings();
	}

	void ResolveTypes() override {
		types = children[0]->types;
	}

	string GetExtensionName() const override {
		return "arrownet_inout_exchange_finalize";
	}
};

// =============================================================================
// COLLECTOR table-in-out (pipeline breaker). The second in-out execution shape (alongside the streaming
// exchange above): a Sink+Source operator that buffers ALL input, then (once, after all branches) opens the
// exchange and STREAMS the output (the Source pulls the C# output one vector-slice at a time — no output
// materialization). Right for whole-table transforms (output depends on all input). Reuses the inout_bind /
// inout_exchange_open ABI verbatim — only the OPERATOR differs (no gate, no per-chunk sentinel; the C# input
// stream is a plain buffered producer, so the binding reads all input before yielding). Input is fully
// buffered (inherent); output is streamed. See docs/inout-collector-mode.md.
// =============================================================================

// Refcounted, on the bind data. Survives prepared re-executions AND the sink->source boundary, so it owns
// everything the Source needs: the bound binding handle (freed via inout_bind_close), the per-execution input
// buffer (ALL input, as Arrow — the in-out Execute appends to it during the sink phase), and the C# output
// stream reader (opened at Finalize, pulled LAZILY by the Source). The input producer lives HERE (not in the
// in-out global state) because C# reads it only on the first Source pull — by which point the in-out's global
// state may already be gone; the holder outlives both phases.
struct CollectorHolder {
	mutex lock;
	ArrowNetHandle binding = nullptr;
	vector<LogicalType> input_types; // input table columns (set at bind; for the producer + Execute appender)
	vector<string> input_names;
	ClientProperties props;          // boundary props for the input producer (set at init_global)
	// per-execution (reset at init_global):
	duckdb::unique_ptr<arrownet::ArrowProducer> input_producer; // ALL input buffered here; drained by C# lazily
	bool opened = false;                                        // exchange opened (Finalize ran)
	duckdb::unique_ptr<arrownet::ArrowStreamReader> reader;     // C# output stream; Source pulls vector-slices
	bool source_done = false;
	~CollectorHolder();
	void OpenExchange(ClientContext &context); // single all-input-done: Finish input + open exchange (keep reader)
};

// Trivial per-execution state. Its init (ArrowNetCollectorInitGlobal) resets the holder's per-execution buffer.
struct ArrowNetCollectorGlobalState : public GlobalTableFunctionState {
	idx_t MaxThreads() const override {
		return 1;
	}
};

// Bind data (reused across prepared re-executions; the holder is shared, the binding bound once).
struct ArrowNetCollectorBindData : public TableFunctionData {
	ArrowNetHandle handle = nullptr;
	string schema;
	string func;
	shared_ptr<CollectorHolder> holder;
};

struct ArrowNetCollectorLocalState : public LocalTableFunctionState {};

CollectorHolder::~CollectorHolder() {
	// Release the output reader FIRST (its C# dispose releases the imported input stream, which points at the
	// producer), THEN the producer, THEN the binding.
	reader.reset();
	input_producer.reset();
	if (binding) {
		arrownet::InOutBindClose(binding); // best-effort; swallows errors
		binding = nullptr;
	}
}

void CollectorHolder::OpenExchange(ClientContext &context) {
	lock_guard<mutex> guard(lock);
	if (opened) {
		return;
	}
	opened = true;
	// Read-only / custom collectors don't touch DuckDB's write transaction; set a null active txn for
	// consistency (a future SQL-backed collector opening a connection in Collect would pick it up).
	ArrowNetSetActiveTxn(nullptr, context);
	if (!input_producer) {
		return; // input pipeline never ran (empty plan) — the Source sees no reader => FINISHED (no rows)
	}
	input_producer->Finish(); // no more input batches
	ArrowArrayStream out_stream;
	std::memset(&out_stream, 0, sizeof(out_stream));
	// Open the exchange: C# Collect is wired up but LAZY — it doesn't read the (now fully buffered) input until
	// the Source pulls the first output batch. The Source then drains `reader` one vector-slice at a time, so
	// the output is STREAMED (never materialized). The reader holds `context` by reference (the query context,
	// valid through the source phase).
	arrownet::InOutExchangeOpen(binding, *input_producer->Stream(), out_stream);
	reader = make_uniq<arrownet::ArrowStreamReader>(context, out_stream);
}

// Bind: resolve the binding + its full output schema via inout_bind (identical to the streaming exchange bind,
// but stores a CollectorHolder + the output types for the Source). Cost args (named parameters) marshaled the
// same way so a collector can take constant args (e.g. a future daxevaltable).
unique_ptr<FunctionData> ArrowNetCollectorBind(ClientContext &context, TableFunctionBindInput &input,
                                               vector<LogicalType> &return_types, vector<string> &names) {
	auto &info = input.info->Cast<ArrowNetTableFunctionInfo>();
	auto bind_data = make_uniq<ArrowNetCollectorBindData>();
	bind_data->handle = info.handle;
	bind_data->schema = info.schema;
	bind_data->func = info.func;
	bind_data->holder = make_shared_ptr<CollectorHolder>();
	auto &holder = *bind_data->holder;
	for (idx_t i = 0; i < input.input_table_types.size(); i++) {
		holder.input_types.push_back(input.input_table_types[i]);
		holder.input_names.push_back(input.input_table_names[i]);
	}

	auto props = arrownet::BoundaryClientProperties(context);
	ArrowSchema input_schema;
	std::memset(&input_schema, 0, sizeof(input_schema));
	ArrowConverter::ToArrowSchema(&input_schema, holder.input_types, holder.input_names, props);

	// Marshal supplied constant args (named parameters) into a 1-row Arrow stream (else null). Same as the
	// streaming exchange bind.
	vector<LogicalType> arg_types;
	vector<string> arg_names;
	vector<Value> arg_values;
	for (auto &kv : input.named_parameters) {
		LogicalType declared = kv.second.type();
		for (idx_t i = 0; i < info.arg_names.size(); i++) {
			if (StringUtil::CIEquals(info.arg_names[i], kv.first)) {
				declared = info.arg_types[i];
				break;
			}
		}
		arg_names.push_back(kv.first);
		arg_types.push_back(declared);
		arg_values.push_back(kv.second);
	}
	arrownet::ArrowProducer arg_producer(arg_types, arg_names, props);
	ArrowArrayStream *args_ptr = nullptr;
	if (!arg_values.empty()) {
		DataChunk chunk;
		chunk.Initialize(Allocator::DefaultAllocator(), arg_types);
		for (idx_t c = 0; c < arg_values.size(); c++) {
			chunk.SetValue(c, 0, arg_values[c].DefaultCastAs(arg_types[c]));
		}
		chunk.SetCardinality(1);
		auto extension_types = ArrowTypeExtensionData::GetExtensionTypes(context, arg_types);
		ArrowAppender appender(arg_types, 1, props, extension_types);
		appender.Append(chunk, 0, 1, 1);
		arg_producer.AddBatch(appender.Finalize());
		arg_producer.Finish();
		args_ptr = arg_producer.Stream();
	}

	ArrowArrayStream out_schema;
	std::memset(&out_schema, 0, sizeof(out_schema));
	bind_data->holder->binding =
	    arrownet::InOutBind(info.handle, info.schema, info.func, args_ptr, input_schema, out_schema);

	ArrowSchemaWrapper schema_root;
	if (out_schema.get_schema(&out_schema, &schema_root.arrow_schema) != 0) {
		const char *msg = out_schema.get_last_error ? out_schema.get_last_error(&out_schema) : nullptr;
		if (out_schema.release) {
			out_schema.release(&out_schema);
		}
		throw IOException(string("mssql_net: failed to read collector output schema") +
		                  (msg ? string(": ") + msg : string()));
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
	return std::move(bind_data);
}

unique_ptr<GlobalTableFunctionState> ArrowNetCollectorInitGlobal(ClientContext &context,
                                                                TableFunctionInitInput &input) {
	auto &bind = input.bind_data->Cast<ArrowNetCollectorBindData>();
	auto &holder = *bind.holder;
	// Reset per-execution holder state (a prepared statement may re-execute on the shared holder). Release the
	// prior reader FIRST (its C# dispose releases the prior producer's exported stream) before replacing the
	// producer with a fresh one for this execution.
	lock_guard<mutex> guard(holder.lock);
	holder.reader.reset();
	holder.opened = false;
	holder.source_done = false;
	holder.props = arrownet::BoundaryClientProperties(context);
	holder.input_producer = make_uniq<arrownet::ArrowProducer>(holder.input_types, holder.input_names, holder.props);
	return make_uniq<ArrowNetCollectorGlobalState>();
}

unique_ptr<LocalTableFunctionState> ArrowNetCollectorInitLocal(ExecutionContext &, TableFunctionInitInput &,
                                                               GlobalTableFunctionState *) {
	return make_uniq<ArrowNetCollectorLocalState>();
}

// The in-out operator function: buffer each input chunk (as Arrow), emit NO rows. The actual output is emitted
// by the injected Sink+Source wrapper after ALL input is collected.
OperatorResultType ArrowNetCollectorFunction(ExecutionContext &context, TableFunctionInput &data, DataChunk &input,
                                             DataChunk &output) {
	auto &holder = *data.bind_data->Cast<ArrowNetCollectorBindData>().holder;
	if (input.size() > 0) {
		// holder.input_producer + holder.props are set at init_global (before any Execute) and are stable for
		// the execution; AddBatch is internally mutex-guarded, so parallel-branch appends are safe lock-free.
		auto ext = ArrowTypeExtensionData::GetExtensionTypes(context.client, holder.input_types);
		ArrowAppender appender(holder.input_types, input.size(), holder.props, ext);
		appender.Append(input, 0, input.size(), input.size());
		holder.input_producer->AddBatch(appender.Finalize());
	}
	output.SetCardinality(0);
	return OperatorResultType::NEED_MORE_INPUT;
}

// Source state — single-threaded (base MaxThreads()==1); the lock guards the lazy reader pull.
class ArrowNetCollectorSourceState : public GlobalSourceState {
public:
	mutex lock;
};

// The pipeline-breaker operator: Sink consumes the (empty) in-out output (just to get the single
// all-branches-done Finalize); Finalize opens the exchange; the Source STREAMS the C# output (pulls the reader
// one vector-slice at a time — no materialization).
class ArrowNetCollectorPhysical : public PhysicalOperator {
public:
	ArrowNetCollectorPhysical(PhysicalPlan &physical_plan, vector<LogicalType> types, idx_t estimated_cardinality,
	                          shared_ptr<CollectorHolder> holder)
	    : PhysicalOperator(physical_plan, PhysicalOperatorType::EXTENSION, std::move(types), estimated_cardinality),
	      holder(std::move(holder)) {
	}

	shared_ptr<CollectorHolder> holder;

	string GetName() const override {
		return "ARROWNET_COLLECTOR";
	}

	// ---- Sink (collect all input) ----
	bool IsSink() const override {
		return true;
	}
	SinkResultType Sink(ExecutionContext &, DataChunk &, OperatorSinkInput &) const override {
		// The child in-out emits 0 rows; the actual input buffering happens in its Execute. Nothing to do here
		// except participate in the pipeline so Finalize fires once after all branches.
		return SinkResultType::NEED_MORE_INPUT;
	}
	SinkFinalizeType Finalize(Pipeline &, Event &, ClientContext &context, OperatorSinkFinalizeInput &) const override {
		holder->OpenExchange(context); // single, all-branches-done: open the exchange (the Source streams it)
		return SinkFinalizeType::READY;
	}

	// ---- Source (STREAM the C# output — pull the reader one vector-slice at a time) ----
	bool IsSource() const override {
		return true;
	}
	unique_ptr<GlobalSourceState> GetGlobalSourceState(ClientContext &) const override {
		return make_uniq<ArrowNetCollectorSourceState>();
	}
	SourceResultType GetDataInternal(ExecutionContext &context, DataChunk &chunk, OperatorSourceInput &input) const override {
		auto &gstate = input.global_state.Cast<ArrowNetCollectorSourceState>();
		lock_guard<mutex> guard(gstate.lock); // single-stream reader; MaxThreads()==1 but guard anyway
		if (!holder->reader || holder->source_done) {
			return SourceResultType::FINISHED;
		}
		// C# Collect runs lazily on THIS pull (sync-over-async on this thread), so (re)set the active txn +
		// host-FS opener here — not just in Finalize/OpenExchange, which may run on a different thread (the
		// per-thread ambient would otherwise be unset here → a host-FS collector like arrownet_delta_write
		// would see a null opener).
		ArrowNetSetActiveTxn(nullptr, context.client);
		// Drain a pending array first (one C# output batch may exceed STANDARD_VECTOR_SIZE).
		if (holder->reader->HasPending()) {
			holder->reader->Drain(chunk);
			return SourceResultType::HAVE_MORE_OUTPUT;
		}
		while (true) {
			auto pr = holder->reader->Pull();
			if (pr == arrownet::ArrowStreamReader::PullResult::END) {
				holder->source_done = true;
				return SourceResultType::FINISHED;
			}
			if (pr == arrownet::ArrowStreamReader::PullResult::SENTINEL) {
				continue; // a collector yields no sentinels, but tolerate (skip empty) for robustness
			}
			holder->reader->Drain(chunk);
			return SourceResultType::HAVE_MORE_OUTPUT;
		}
	}
};

struct ArrowNetCollectorFinalizeOperator : public LogicalExtensionOperator {
	explicit ArrowNetCollectorFinalizeOperator(unique_ptr<LogicalOperator> child, shared_ptr<CollectorHolder> holder)
	    : holder(std::move(holder)) {
		children.push_back(std::move(child));
	}

	shared_ptr<CollectorHolder> holder;

	PhysicalOperator &CreatePlan(ClientContext &, PhysicalPlanGenerator &planner) override {
		auto &child_plan = planner.CreatePlan(*children[0]);
		auto &op = planner.Make<ArrowNetCollectorPhysical>(children[0]->types, children[0]->estimated_cardinality,
		                                                   holder);
		op.children.push_back(child_plan);
		return op;
	}

	vector<ColumnBinding> GetColumnBindings() override {
		return children[0]->GetColumnBindings();
	}

	void ResolveTypes() override {
		types = children[0]->types;
	}

	string GetExtensionName() const override {
		return "arrownet_collector";
	}
};

// -----------------------------------------------------------------------------

// Recursively wrap every ArrowNet table-in-out LogicalGet in a finalize operator.
void WrapArrowNetInOutNodes(unique_ptr<LogicalOperator> &op) {
	for (auto &child : op->children) {
		WrapArrowNetInOutNodes(child);
	}
	if (op->type != LogicalOperatorType::LOGICAL_GET) {
		return;
	}
	auto &get = op->Cast<LogicalGet>();
	// A table-in-out is a LogicalGet with input children + the exchange in_out_function; identify by the
	// function pointer (no RTTI), then recover the holder from its bind data to inject the EOF/finalize.
	if (get.children.empty() || !get.bind_data) {
		return;
	}
	if (get.function.in_out_function == ArrowNetExchangeFunction) {
		auto holder = get.bind_data->Cast<ArrowNetExchangeBindData>().holder;
		if (holder) {
			op = make_uniq<ArrowNetExchangeFinalizeOperator>(std::move(op), std::move(holder));
		}
		return;
	}
	// A COLLECTOR (pipeline breaker) in-out is wrapped in its Sink+Source finalize operator instead — it
	// alone emits the output (the in-out itself emits 0 rows; it only buffers input).
	if (get.function.in_out_function == ArrowNetCollectorFunction) {
		auto holder = get.bind_data->Cast<ArrowNetCollectorBindData>().holder;
		if (holder) {
			op = make_uniq<ArrowNetCollectorFinalizeOperator>(std::move(op), std::move(holder));
		}
	}
}

void ArrowNetInOutOptimize(OptimizerExtensionInput &, unique_ptr<LogicalOperator> &plan) {
	WrapArrowNetInOutNodes(plan);
}

} // namespace

void RegisterArrowNetInOutFinalizer(DBConfig &config) {
	OptimizerExtension extension;
	extension.optimize_function = ArrowNetInOutOptimize;
	OptimizerExtension::Register(config, std::move(extension));
}

void RegisterArrowNetGlobalFunctions(ExtensionLoader &loader) {
	// Load-time global (connection-free) functions: enumerate the provider-union via the bridge, then register
	// each as a bare fn(...). Best-effort — if the bridge can't boot (no managed dir) this is skipped, exactly
	// like provider settings/secrets. See docs/global-functions.md.
	try {
		ArrowArrayStream stream;
		std::memset(&stream, 0, sizeof(stream));
		arrownet::ListGlobalFunctions(stream);
		// Columns: name, kind, string_order, param_count(int), return_type. We read the three leading string
		// columns (name, kind, string_order); the precise arg/return types come from the per-function fetch
		// below (handle = 0 = global). string_order ("1"/"0") marks a byte-ordered-string table reader.
		auto rows = ReadStringTable(stream, 3);
		const auto &names = rows[0];
		const auto &kinds = rows[1];
		const auto &string_order = rows[2];
		if (names.empty()) {
			return; // no global functions declared
		}
		// FetchFunctionParamSchema/ReturnType need a ClientContext to turn the Arrow schema into DuckDB types;
		// a fresh connection on the loading database provides one. (The DB instance exists by Extension::Load.)
		Connection conn(loader.GetDatabaseInstance());
		auto &context = *conn.context;
		for (idx_t i = 0; i < names.size(); i++) {
			const string &fn_name = names[i];
			const string &kind = kinds[i];
			if (kind == "scalar") {
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				LogicalType return_type;
				try {
					// handle = 0 + empty schema = the global marker; C# resolves the function by name.
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types);
					return_type = FetchFunctionReturnType(context, nullptr, "", fn_name);
				} catch (std::exception &) {
					continue; // skip a global whose schema can't be resolved
				}
				ScalarFunction fn =
				    BuildArrowNetScalarFunction(nullptr, "", fn_name, arg_types, arg_names, return_type);
				loader.RegisterFunction(fn);
			} else if (kind == "inout" || kind == "collector") {
				// A connection-free in-out / collector: a {TABLE}-param table function on the streaming-exchange
				// (in-out) or Sink+Source (collector) operator, with handle = 0 so the bind resolves the binding
				// against the C# global registry by name (mirrors GetOrCreateCustomInOut/CollectorFunction).
				bool is_collector = kind == "collector";
				TableFunction tf(fn_name, {LogicalType::TABLE}, nullptr,
				                  is_collector ? ArrowNetCollectorBind : ArrowNetExchangeBind,
				                  is_collector ? ArrowNetCollectorInitGlobal : ArrowNetExchangeInitGlobal,
				                  is_collector ? ArrowNetCollectorInitLocal : ArrowNetExchangeInitLocal);
				tf.in_out_function = is_collector ? ArrowNetCollectorFunction : ArrowNetExchangeFunction;
				auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
				fn_info->handle = nullptr; // global marker
				fn_info->schema = "";
				fn_info->func = fn_name;
				fn_info->is_proc = false;
				try {
					// Constant "cost" args as NAMED parameters (coexist with the single {TABLE} overload); none
					// for a no-arg in-out/collector. SQLNULL sentinel => ANY (an "accept any value" param bag).
					vector<string> arg_names;
					vector<LogicalType> arg_types;
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types);
					for (idx_t k = 0; k < arg_names.size(); k++) {
						auto t = arg_types[k].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[k];
						tf.named_parameters[arg_names[k]] = t;
					}
					fn_info->arg_names = std::move(arg_names);
					fn_info->arg_types = std::move(arg_types);
				} catch (std::exception &) {
					// no cost args
				}
				tf.function_info = std::move(fn_info);
				loader.RegisterFunction(tf);
			} else if (kind == "aggregate" || kind == "aggregate_spill") {
				// A connection-free aggregate (UDAF): same state-vectorized callbacks as a catalog aggregate,
				// handle = 0 so agg_open resolves the session against the C# global registry by name. Usable in
				// GROUP BY / OVER / parallel. Mirrors GetOrCreateAggregateFunction.
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				LogicalType return_type;
				try {
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types);
					return_type = FetchFunctionReturnType(context, nullptr, "", fn_name);
				} catch (std::exception &) {
					continue;
				}
				AggregateFunction fn = BuildArrowNetAggregateFunction(nullptr, "", fn_name, arg_types, arg_names,
				                                                      return_type, kind == "aggregate_spill");
				loader.RegisterFunction(fn);
			} else if (kind == "table") {
				// A connection-free table function: positional args + the v29 table-session bind/scan, with
				// handle = 0 so table_bind resolves the binding against the C# global registry by name. Output
				// schema is arg-dependent (resolved per-call at table_bind). Mirrors GetOrCreateTableFunction's
				// non-proc branch (projection + best-effort filter pushdown; the binding decides honoring).
				vector<string> arg_names;
				vector<LogicalType> arg_types;
				try {
					FetchFunctionParamSchema(context, nullptr, "", fn_name, arg_names, arg_types);
				} catch (std::exception &) {
					continue;
				}
				TableFunction tf(fn_name, arg_types, arrownet::ArrowStreamScan, ArrowNetTableFunctionBind,
				                 arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
				tf.projection_pushdown = true;
				tf.pushdown_complex_filter = ArrowNetComplexFilterPushdown;
				auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
				fn_info->handle = nullptr; // global marker
				fn_info->schema = "";
				fn_info->func = fn_name;
				fn_info->arg_types = arg_types;
				fn_info->arg_names = arg_names;
				fn_info->is_proc = false;
				// A byte-ordered-string reader (e.g. Delta/Parquet) can safely push string ordering + BETWEEN.
				fn_info->string_order_pushable = string_order[i] == "1";
				tf.function_info = std::move(fn_info);
				loader.RegisterFunction(tf);
			}
		}
	} catch (std::exception &) {
		// Bridge unavailable at load — skip global-function registration (graceful degradation).
	}
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateTableFunction(ClientContext &context,
                                                                         const string &func_name) {
	lock_guard<mutex> lock(entry_lock_);
	auto cached = table_function_entries_.find(func_name);
	if (cached != table_function_entries_.end()) {
		return cached->second.get();
	}
	auto kind_it = table_functions_.find(func_name);
	if (kind_it == table_functions_.end()) {
		// A provider-authored custom table-in-out (4g) is registered under the bare name.
		if (custom_inout_functions_.find(func_name) != custom_inout_functions_.end()) {
			return GetOrCreateCustomInOutFunction(context, func_name);
		}
		// A provider-authored custom COLLECTOR (pipeline breaker) is also registered under the bare name.
		if (custom_collector_functions_.find(func_name) != custom_collector_functions_.end()) {
			return GetOrCreateCustomCollectorFunction(context, func_name);
		}
		// Else maybe the synthetic in-out alias `<base>_each` (a real same-named function would
		// have matched above, so it wins over the alias).
		auto each_it = inout_functions_.find(func_name);
		if (each_it != inout_functions_.end()) {
			return GetOrCreateInOutFunction(context, func_name, each_it->second);
		}
		return nullptr;
	}
	bool is_proc = kind_it->second;

	vector<string> arg_names;
	vector<LogicalType> arg_types;
	try {
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
	} catch (std::exception &) {
		// Stale discovery (dropped out-of-band) — treat as not-found.
		table_functions_.erase(func_name);
		table_function_entries_.erase(func_name);
		return nullptr;
	}

	// TVFs take positional arguments (called positionally in a FROM clause); stored procs
	// take DuckDB named parameters (EXEC @name=val), so the caller supplies a subset and
	// omitted optional params fall back to the proc's own DEFAULT.
	vector<LogicalType> positional = is_proc ? vector<LogicalType>() : arg_types;
	TableFunction tf(func_name, positional, arrownet::ArrowStreamScan, ArrowNetTableFunctionBind,
	                 arrownet::ArrowStreamInitGlobal, arrownet::ArrowStreamInitLocal);
	tf.projection_pushdown = true;
	if (is_proc) {
		for (idx_t i = 0; i < arg_names.size(); i++) {
			// A provider declares an "accept any value" named parameter (e.g. a struct/JSON parameter bag)
			// as a SQLNULL-typed field — there is no Arrow type for ANY, so SQLNULL is the agreed marker.
			// Register it as ANY so DuckDB passes any literal (a STRUCT, a VARCHAR, …) through UNCAST.
			auto t = arg_types[i].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[i];
			tf.named_parameters[arg_names[i]] = t;
		}
	} else {
		// Best-effort filter pushdown into the TVF (reuses the table scan's serializer; the
		// predicates are left in the plan so DuckDB re-applies them — an over-approximation
		// is safe). `SELECT <cols> FROM tvf(@args) WHERE <filter>` is emitted by C#. Procs
		// are not inline-wrappable, so they get no filter pushdown (DuckDB filters locally).
		tf.pushdown_complex_filter = ArrowNetComplexFilterPushdown;
	}
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->is_proc = is_proc;
	tf.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(tf));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

// Build the in-out catalog entry for the synthetic alias `<base>_each` — a `{LogicalType::TABLE}`
// table function that applies the discovered TVF `base_func` once per input row (4g). DuckDB
// forbids a TABLE-parameter overload from coexisting with the scalar-arg scan form under one name
// (bind_table_function.cpp), so the in-out form is exposed as a sibling entry under its own name;
// the scan form keeps the bare TVF name. Caller holds entry_lock_.
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateInOutFunction(ClientContext &context,
                                                                         const string &each_name,
                                                                         const string &base_func) {
	vector<string> arg_names;
	vector<LogicalType> arg_types;
	try {
		FetchFunctionParamSchema(context, handle_, name, base_func, arg_names, arg_types);
	} catch (std::exception &) {
		// Stale discovery (base TVF dropped out-of-band) — treat as not-found, evicting both
		// the alias and the base so the next lookup re-discovers.
		inout_functions_.erase(each_name);
		table_function_entries_.erase(each_name);
		table_functions_.erase(base_func);
		table_function_entries_.erase(base_func);
		return nullptr;
	}
	if (arg_types.empty()) {
		return nullptr; // a no-arg TVF has nothing to apply per input row
	}

	// Every `_each` form (discovered TVF AND stored proc) streams on the Phase 6 exchange operator (gate + two
	// pull streams, no per-chunk materialization). The managed InOutBind classifies the base object: a TVF
	// CROSS APPLYs on a read-only connection (SqlServerTvfEach); a proc EXECs per input row on DuckDB's pinned
	// write transaction (SqlServerProcEach). The retired 4g push operator (ArrowNetInOut*) is now unused.
	TableFunction inout(each_name, {LogicalType::TABLE}, nullptr, ArrowNetExchangeBind, ArrowNetExchangeInitGlobal,
	                    ArrowNetExchangeInitLocal);
	inout.in_out_function = ArrowNetExchangeFunction;
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = base_func; // the CROSS APPLY target is the real SQL Server TVF, not the alias
	fn_info->arg_types = arg_types;
	fn_info->arg_names = arg_names;
	fn_info->is_proc = false;
	inout.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(inout));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[each_name] = std::move(entry);
	return &ref;
}

// Build the catalog entry for a provider-authored custom table-in-out (4g) — a `{LogicalType::TABLE}`
// table function under the bare name, dispatched to C# (no SQL object, no scalar-arg scan form). Reuses
// the same operator callbacks as the `_each` path; only the bind differs (full output schema, no input
// echo). Caller holds entry_lock_.
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateCustomInOutFunction(ClientContext &context,
                                                                              const string &func_name) {
	// Phase 6: custom C# in-out runs on the streaming exchange operator (gate + two pull streams, no
	// per-chunk materialization). Discovered-TVF `_each` + procs stay on the push model for now.
	TableFunction inout(func_name, {LogicalType::TABLE}, nullptr, ArrowNetExchangeBind, ArrowNetExchangeInitGlobal,
	                    ArrowNetExchangeInitLocal);
	inout.in_out_function = ArrowNetExchangeFunction;
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->is_proc = false;
	// Constant "cost" args (e.g. the DAX expression for daxevaltable / daxeach) are declared as NAMED
	// parameters so they can coexist with the single {LogicalType::TABLE} overload (a scalar arg can't —
	// bind_table_function.cpp). Best-effort: a custom in-out with no args (a pure-C# in-out like cf_tag, or
	// any provider returning an empty param schema) declares none, preserving existing behavior.
	try {
		vector<string> arg_names;
		vector<LogicalType> arg_types;
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
		for (idx_t i = 0; i < arg_names.size(); i++) {
			inout.named_parameters[arg_names[i]] = arg_types[i];
		}
		fn_info->arg_names = std::move(arg_names);
		fn_info->arg_types = std::move(arg_types);
	} catch (std::exception &) {
		// no cost args for this in-out function
	}
	inout.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(inout));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

// Build the catalog entry for a provider-authored custom COLLECTOR (pipeline breaker) — a
// `{LogicalType::TABLE}`-parameter table function under the bare name, routed to the Sink+Source collector
// operator (ArrowNetCollectorFunction): it buffers ALL input, then emits the C# output once. Mirrors
// GetOrCreateCustomInOutFunction (same cost-arg named-parameter handling); only the operator callbacks differ.
// Caller holds entry_lock_.
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::GetOrCreateCustomCollectorFunction(ClientContext &context,
                                                                                  const string &func_name) {
	TableFunction collector(func_name, {LogicalType::TABLE}, nullptr, ArrowNetCollectorBind,
	                        ArrowNetCollectorInitGlobal, ArrowNetCollectorInitLocal);
	collector.in_out_function = ArrowNetCollectorFunction;
	auto fn_info = make_shared_ptr<ArrowNetTableFunctionInfo>();
	fn_info->handle = handle_;
	fn_info->schema = name;
	fn_info->func = func_name;
	fn_info->is_proc = false;
	// Constant "cost" args declared as NAMED parameters (coexist with the single {TABLE} overload). A collector
	// with no args (the cf_collect demo) declares none.
	try {
		vector<string> arg_names;
		vector<LogicalType> arg_types;
		FetchFunctionParamSchema(context, handle_, name, func_name, arg_names, arg_types);
		for (idx_t i = 0; i < arg_names.size(); i++) {
			auto t = arg_types[i].id() == LogicalTypeId::SQLNULL ? LogicalType::ANY : arg_types[i];
			collector.named_parameters[arg_names[i]] = t;
		}
		fn_info->arg_names = std::move(arg_names);
		fn_info->arg_types = std::move(arg_types);
	} catch (std::exception &) {
		// no cost args for this collector
	}
	collector.function_info = std::move(fn_info);

	CreateTableFunctionInfo info(std::move(collector));
	info.catalog = catalog.GetName();
	info.schema = name;
	auto entry = make_uniq<TableFunctionCatalogEntry>(catalog, *this, info);
	auto &ref = *entry;
	table_function_entries_[func_name] = std::move(entry);
	return &ref;
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::LookupEntry(CatalogTransaction transaction,
                                                            const EntryLookupInfo &lookup_info) {
	if (!transaction.context) {
		return nullptr;
	}
	auto type = lookup_info.GetCatalogType();
	if (type == CatalogType::TABLE_ENTRY) {
		return GetOrCreateEntry(*transaction.context, lookup_info.GetEntryName());
	}
	if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		// DuckDB stores scalar/aggregate/macro functions in one namespace and resolves a function call by
		// looking up SCALAR_FUNCTION_ENTRY, then dispatching on the returned entry's actual type (see
		// bind_function_expression.cpp). So a scalar lookup must also surface our custom aggregates.
		auto scalar = GetOrCreateScalarFunction(*transaction.context, lookup_info.GetEntryName());
		if (scalar) {
			return scalar;
		}
		return GetOrCreateAggregateFunction(*transaction.context, lookup_info.GetEntryName());
	}
	if (type == CatalogType::AGGREGATE_FUNCTION_ENTRY) {
		return GetOrCreateAggregateFunction(*transaction.context, lookup_info.GetEntryName());
	}
	if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		return GetOrCreateTableFunction(*transaction.context, lookup_info.GetEntryName());
	}
	return nullptr;
}

void ArrowNetSchemaEntry::Scan(ClientContext &context, CatalogType type,
                               const std::function<void(CatalogEntry &)> &callback) {
	if (type == CatalogType::TABLE_ENTRY) {
		for (auto &entry : table_types_) {
			auto catalog_entry = GetOrCreateEntry(context, entry.first);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		// Snapshot the names: GetOrCreateScalarFunction locks entry_lock_ and may evict
		// a stale entry, which would invalidate an iterator over scalar_functions_.
		vector<string> names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : scalar_functions_) {
				names.push_back(fn);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateScalarFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::AGGREGATE_FUNCTION_ENTRY) {
		vector<string> names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : aggregate_functions_) {
				names.push_back(fn.first);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateAggregateFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
		return;
	}
	if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		vector<string> names;
		{
			lock_guard<mutex> lock(entry_lock_);
			for (auto &fn : table_functions_) {
				names.push_back(fn.first);
			}
			// The synthetic `<name>_each` table-in-out aliases + custom in-out functions are catalog
			// functions too.
			for (auto &fn : inout_functions_) {
				names.push_back(fn.first);
			}
			for (auto &fn : custom_inout_functions_) {
				names.push_back(fn);
			}
			for (auto &fn : custom_collector_functions_) {
				names.push_back(fn);
			}
		}
		for (auto &fn : names) {
			auto catalog_entry = GetOrCreateTableFunction(context, fn);
			if (catalog_entry) {
				callback(*catalog_entry);
			}
		}
	}
}

void ArrowNetSchemaEntry::Scan(CatalogType type, const std::function<void(CatalogEntry &)> &callback) {
	// No context available: only report already-materialized entries.
	lock_guard<mutex> lock(entry_lock_);
	if (type == CatalogType::TABLE_ENTRY) {
		for (auto &entry : entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::SCALAR_FUNCTION_ENTRY) {
		for (auto &entry : function_entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::AGGREGATE_FUNCTION_ENTRY) {
		for (auto &entry : aggregate_function_entries_) {
			callback(*entry.second);
		}
	} else if (type == CatalogType::TABLE_FUNCTION_ENTRY) {
		for (auto &entry : table_function_entries_) {
			callback(*entry.second);
		}
	}
}

[[noreturn]] static void ReadOnly(const char *op) {
	throw NotImplementedException("mssql_net: %s is not supported (read-only catalog in Phase 1)", op);
}

optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateTable(CatalogTransaction transaction, BoundCreateTableInfo &info) {
	if (!transaction.context) {
		throw InternalException("mssql_net: CREATE TABLE requires a client context");
	}
	auto &context = *transaction.context;
	auto &base = info.Base();
	ArrowNetSetActiveTxn(handle_, context); // CREATE (+ optional DROP for REPLACE) joins this txn's connection

	// Column names + types, and per-column nullability (NOT NULL constraints).
	vector<string> names;
	vector<LogicalType> types;
	for (auto &col : base.columns.Logical()) {
		names.push_back(col.Name());
		types.push_back(col.Type());
	}
	vector<bool> nullable(names.size(), true);
	for (auto &constraint : base.constraints) {
		if (constraint->type == ConstraintType::NOT_NULL) {
			auto &nn = constraint->Cast<NotNullConstraint>();
			if (nn.index.index < nullable.size()) {
				nullable[nn.index.index] = false;
			}
		}
	}

	// Key constraints, carried to the backend as 0-based column-index groups:
	// the PRIMARY KEY as a single comma-separated group, each UNIQUE as its own.
	vector<idx_t> pk_indices;
	vector<vector<idx_t>> unique_groups;
	for (auto &constraint : base.constraints) {
		if (constraint->type != ConstraintType::UNIQUE) {
			continue;
		}
		auto &uc = constraint->Cast<UniqueConstraint>();
		vector<idx_t> group;
		for (auto &logical : uc.GetLogicalIndexes(base.columns)) {
			group.push_back(logical.index);
		}
		if (uc.IsPrimaryKey()) {
			pk_indices = group;
		} else {
			unique_groups.push_back(std::move(group));
		}
	}
	// PRIMARY KEY columns must be NOT NULL in SQL Server.
	for (auto idx : pk_indices) {
		if (idx < nullable.size()) {
			nullable[idx] = false;
		}
	}

	auto join_indices = [](const vector<idx_t> &idxs) {
		string out;
		for (idx_t i = 0; i < idxs.size(); i++) {
			if (i > 0) {
				out += ",";
			}
			out += std::to_string(idxs[i]);
		}
		return out;
	};
	string pk_arg = join_indices(pk_indices);
	string unique_arg;
	for (auto &group : unique_groups) {
		if (!unique_arg.empty()) {
			unique_arg += ";";
		}
		unique_arg += join_indices(group);
	}

	// Literal column DEFAULTs: "<index> <payload>" pairs, payload = base64(value
	// text) or "-" for DEFAULT NULL. Non-literal defaults (expressions) are skipped.
	string defaults_arg;
	for (idx_t i = 0; i < names.size(); i++) {
		auto &col = base.columns.GetColumn(LogicalIndex(i));
		if (!col.HasDefaultValue()) {
			continue;
		}
		// Unwrap one CAST level (e.g. boolean literals parse as CAST(... AS BOOLEAN)).
		const ParsedExpression *expr = &col.DefaultValue();
		if (expr->type == ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<CastExpression>().child.get();
		}
		if (!expr || expr->type != ExpressionType::VALUE_CONSTANT) {
			continue; // literals only
		}
		auto &val = expr->Cast<ConstantExpression>().value;
		if (!defaults_arg.empty()) {
			defaults_arg += " ";
		}
		defaults_arg += std::to_string(i) + " ";
		if (val.IsNull()) {
			defaults_arg += "-";
		} else {
			string text = val.ToString();
			defaults_arg += Blob::ToBase64(string_t(text.c_str(), (uint32_t)text.size()));
		}
	}

	bool replace = base.on_conflict == OnCreateConflict::REPLACE_ON_CONFLICT;
	bool if_not_exists = base.on_conflict == OnCreateConflict::IGNORE_ON_CONFLICT;
	if (replace) {
		arrownet::DropTable(handle_, name, base.table, /*if_exists=*/true);
	}

	// A schema-only Arrow stream carries the column definitions to the backend. The text-column SQL type
	// (mssql_ctas_text_type / mssql_default_varchar_length) is read C#-side from the provider settings store
	// (see docs/settings-architecture.md), not passed here.
	arrownet::ArrowProducer producer(types, names, arrownet::BoundaryClientProperties(context));
	producer.SetNullability(nullable);
	producer.Finish();
	arrownet::CreateTable(handle_, name, base.table, *producer.Stream(), if_not_exists, pk_arg, unique_arg,
	                      defaults_arg);

	// Register the new table (also invalidates any cached entry) and return it.
	AddTable(base.table, "BASE TABLE");
	return GetOrCreateEntry(context, base.table);
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateFunction(CatalogTransaction, CreateFunctionInfo &) {
	ReadOnly("CREATE FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateIndex(CatalogTransaction, CreateIndexInfo &,
                                                            TableCatalogEntry &) {
	ReadOnly("CREATE INDEX");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateView(CatalogTransaction, CreateViewInfo &) {
	ReadOnly("CREATE VIEW");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateSequence(CatalogTransaction, CreateSequenceInfo &) {
	ReadOnly("CREATE SEQUENCE");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateTableFunction(CatalogTransaction, CreateTableFunctionInfo &) {
	ReadOnly("CREATE TABLE FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateCopyFunction(CatalogTransaction, CreateCopyFunctionInfo &) {
	ReadOnly("CREATE COPY FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreatePragmaFunction(CatalogTransaction, CreatePragmaFunctionInfo &) {
	ReadOnly("CREATE PRAGMA FUNCTION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateCollation(CatalogTransaction, CreateCollationInfo &) {
	ReadOnly("CREATE COLLATION");
}
optional_ptr<CatalogEntry> ArrowNetSchemaEntry::CreateType(CatalogTransaction, CreateTypeInfo &) {
	ReadOnly("CREATE TYPE");
}
void ArrowNetSchemaEntry::DropEntry(ClientContext &context, DropInfo &info) {
	if (info.type != CatalogType::TABLE_ENTRY) {
		throw NotImplementedException("mssql_net: only DROP TABLE is supported yet (not %s)",
		                              CatalogTypeToString(info.type));
	}
	bool if_exists = info.if_not_found == OnEntryNotFound::RETURN_NULL;
	ArrowNetSetActiveTxn(handle_, context);
	arrownet::DropTable(handle_, name, info.name, if_exists);

	lock_guard<mutex> lock(entry_lock_);
	table_types_.erase(info.name);
	entries_.erase(info.name);
}
void ArrowNetSchemaEntry::Alter(CatalogTransaction transaction, AlterInfo &info) {
	if (info.type != AlterType::ALTER_TABLE) {
		throw NotImplementedException("mssql_net: only ALTER TABLE is supported");
	}
	if (!transaction.context) {
		throw InternalException("mssql_net: ALTER TABLE requires a client context");
	}
	auto &context = *transaction.context;
	ArrowNetSetActiveTxn(handle_, context); // every ALTER below joins this txn's connection
	auto &table_info = info.Cast<AlterTableInfo>();
	const string &table = table_info.name;

	// Refresh the cached entry AFTER an ALTER. We don't just evict-and-wait-for-lazy-refetch: the lazy
	// re-fetch would be triggered by whatever binds the table next — and under dbt that is a SEPARATE
	// (introspection) transaction with no pinned connection, so its `SELECT * FROM t WHERE 1=0` runs on a
	// POOLED connection and BLOCKS on this ALTER's still-uncommitted Sch-M lock (held on THIS transaction's
	// connection) until the command timeout → catalog eviction → "table does not exist" (the concurrent
	// schema-evolution deadlock, docs/dbt-incremental.md). Instead we re-fetch EAGERLY here, on THIS
	// transaction's connection (the ambient txn was set at the top of Alter): that connection owns the Sch-M
	// lock, so a read-your-writes probe on the same session sees the new schema with NO lock wait, and the
	// fresh entry is cached — so the next bind (even in a different transaction) finds it and never issues the
	// blocking pooled re-fetch. A transaction that later ROLLS BACK leaves this entry stale (it reflects the
	// uncommitted ALTER) → RollbackTransaction invalidates the cache (arrownet_transaction.cpp).
	auto refresh = [&](const string &t) {
		{
			lock_guard<mutex> lock(entry_lock_);
			entries_.erase(t);
		}
		try {
			GetOrCreateEntry(context, t); // eager re-fetch on this txn's connection (no Sch-M self-block)
		} catch (...) {
			// Best-effort: on any failure leave the entry evicted (falls back to lazy re-fetch).
		}
	};

	switch (table_info.alter_table_type) {
	case AlterTableType::RENAME_TABLE: {
		auto &rt = table_info.Cast<RenameTableInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_RENAME_TABLE, rt.new_table_name, "", nullptr, 0);
		lock_guard<mutex> lock(entry_lock_);
		auto it = table_types_.find(table);
		string type = it != table_types_.end() ? it->second : string("BASE TABLE");
		table_types_.erase(table);
		entries_.erase(table);
		table_types_[rt.new_table_name] = type;
		entries_.erase(rt.new_table_name);
		break;
	}
	case AlterTableType::RENAME_COLUMN: {
		auto &rc = table_info.Cast<RenameColumnInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_RENAME_COLUMN, rc.old_name, rc.new_name, nullptr, 0);
		refresh(table);
		break;
	}
	case AlterTableType::ADD_COLUMN: {
		auto &ac = table_info.Cast<AddColumnInfo>();
		int32_t flags = ac.if_column_not_exists ? ARROWNET_ALTER_FLAG_IF_EXISTS : 0;
		// Carry the new column's type as a single-field zero-row Arrow stream.
		vector<LogicalType> types {ac.new_column.Type()};
		vector<string> names {ac.new_column.Name()};
		arrownet::ArrowProducer producer(types, names, arrownet::BoundaryClientProperties(context));
		producer.Finish();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_ADD_COLUMN, ac.new_column.Name(), "",
		                     producer.Stream(), flags);
		refresh(table);
		break;
	}
	case AlterTableType::REMOVE_COLUMN: {
		auto &rc = table_info.Cast<RemoveColumnInfo>();
		int32_t flags = rc.if_column_exists ? ARROWNET_ALTER_FLAG_IF_EXISTS : 0;
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_DROP_COLUMN, rc.removed_column, "", nullptr, flags);
		refresh(table);
		break;
	}
	case AlterTableType::ALTER_COLUMN_TYPE: {
		auto &ct = table_info.Cast<ChangeColumnTypeInfo>();
		vector<LogicalType> types {ct.target_type};
		vector<string> names {ct.column_name};
		arrownet::ArrowProducer producer(types, names, arrownet::BoundaryClientProperties(context));
		producer.Finish();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_COLUMN_TYPE, ct.column_name, "", producer.Stream(),
		                     0);
		refresh(table);
		break;
	}
	case AlterTableType::SET_NOT_NULL: {
		auto &sn = table_info.Cast<SetNotNullInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_SET_NOT_NULL, sn.column_name, "", nullptr, 0);
		refresh(table);
		break;
	}
	case AlterTableType::DROP_NOT_NULL: {
		auto &dn = table_info.Cast<DropNotNullInfo>();
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_DROP_NOT_NULL, dn.column_name, "", nullptr, 0);
		refresh(table);
		break;
	}
	case AlterTableType::SET_DEFAULT: {
		auto &sd = table_info.Cast<SetDefaultInfo>();
		if (!sd.expression) {
			// DROP DEFAULT (no expression).
			arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_DROP_DEFAULT, sd.column_name, "", nullptr, 0);
			refresh(table);
			break;
		}
		// Only literal defaults: unwrap one CAST (booleans parse as CAST(... AS BOOLEAN)).
		const ParsedExpression *expr = sd.expression.get();
		if (expr->type == ExpressionType::OPERATOR_CAST) {
			expr = expr->Cast<CastExpression>().child.get();
		}
		if (!expr || expr->type != ExpressionType::VALUE_CONSTANT) {
			throw NotImplementedException("mssql_net: only literal column DEFAULTs are supported");
		}
		auto &val = expr->Cast<ConstantExpression>().value;
		// arg2: "-" for DEFAULT NULL, else "b"+base64(value-text) (the "b" keeps
		// it non-empty so empty-string literals survive the ABI).
		string arg2;
		if (val.IsNull()) {
			arg2 = "-";
		} else {
			string text = val.ToString();
			arg2 = "b" + Blob::ToBase64(string_t(text.c_str(), (uint32_t)text.size()));
		}
		arrownet::AlterTable(handle_, name, table, ARROWNET_ALTER_SET_DEFAULT, sd.column_name, arg2, nullptr, 0);
		refresh(table);
		break;
	}
	default:
		throw NotImplementedException("mssql_net: this ALTER TABLE variant is not supported yet");
	}
}

} // namespace duckdb
