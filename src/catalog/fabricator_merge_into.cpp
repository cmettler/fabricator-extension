//===----------------------------------------------------------------------===//
//                    fabricator — MERGE INTO physical planning
//
// `Catalog::PlanMergeInto` is a virtual whose default body throws
// `Database type "%s" does not support MERGE INTO or ON CONFLICT`. Overriding it is the whole gate: DuckDB
// has already lowered the statement into a form we can serve, and `PhysicalMergeInto` (upstream, reused
// verbatim) does the routing.
//
// WHAT DUCKDB HANDS US. The child plan is one projection laid out as
//
//     [ ...action expressions..., (source_marker)?, rowid ]
//
// where `row_id_start` indexes the rowid and `source_marker` — present only when the statement has a
// WHEN NOT MATCHED BY SOURCE clause — distinguishes "no source row" from "no target row"
// (`bind_merge_into.cpp`). `PhysicalMergeInto::ComputeMatches` classifies each row by whether those two
// are NULL, slices the chunk three ways, and then drives each action's operator as a MANUAL SINK — it
// calls `GetGlobalSinkState`/`GetLocalSinkState`/`Sink`/`Combine`/`Finalize` directly rather than running
// it as a pipeline. Our DML operators are already self-contained sinks, so they slot in unchanged; the
// only thing that had to move was WHERE an UPDATE reads its SET values from (see FabricatorFillUpdateSetColumns).
//
// SO THIS IS A LOWERING WE DO NOT WRITE. Each action becomes the same Logical{Update,Delete,Insert} the
// standalone statement would produce, routed through our OWN PlanUpdate/PlanDelete/PlanInsert. That is
// deliberate — it is what DuckLake does (`src/storage/ducklake_merge_into.cpp`), and it means MERGE
// inherits every property of our rowid DML instead of re-deriving them: the provider dispatch, the
// buffered-transaction fusion (a Delta MERGE lands as ONE commit), the change feed, the identity handling.
//
// ON CONFLICT COMES ALONG FOR FREE, and not by coincidence: since 1.5.x the binder REWRITES
// `INSERT ... ON CONFLICT` into a MERGE statement (`Binder::Bind(InsertStatement&)` ->
// `GenerateMergeInto`), which is why one error message covered both features and why one override lifts
// both. DO NOTHING is a MERGE_DO_NOTHING action, DO UPDATE a MERGE_UPDATE.
//===----------------------------------------------------------------------===//

#include "catalog/fabricator_catalog.hpp"
#include "catalog/fabricator_table_entry.hpp"
#include "dml/fabricator_insert.hpp"
#include "dml/fabricator_modify.hpp"

#include "duckdb/catalog/catalog_entry/table_catalog_entry.hpp"
#include "duckdb/execution/operator/persistent/physical_merge_into.hpp"
#include "duckdb/execution/physical_plan_generator.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"
#include "duckdb/planner/operator/logical_merge_into.hpp"

namespace duckdb {

// One merge action -> one of our DML operators (or none, for the two that PhysicalMergeInto handles itself).
static unique_ptr<MergeIntoOperator> PlanFabricatorMergeAction(FabricatorCatalog &catalog, ClientContext &context,
                                                              PhysicalPlanGenerator &planner, LogicalMergeInto &op,
                                                              BoundMergeIntoAction &action, bool force_buffered) {
	auto result = make_uniq<MergeIntoOperator>();
	result->action_type = action.action_type;
	result->condition = std::move(action.condition);

	switch (action.action_type) {
	case MergeActionType::MERGE_UPDATE: {
		// `result->expressions` stays EMPTY so PhysicalMergeInto passes the raw sliced chunk through: our
		// UPDATE operator does its own projection out of it, reading each SET value at the position the
		// bound expression names and the key at `row_id_start`. Leaving the expressions here instead would
		// have PhysicalMergeInto pre-project them, and the rowid would no longer be in the chunk at all.
		auto target = FabricatorBuildModifyTarget(op.table);
		FabricatorFillUpdateSetColumns(op.table, action.columns, action.expressions, target);
		target.rowid_child_index = op.row_id_start;
		target.force_buffered = force_buffered;
		vector<LogicalType> result_types {LogicalType::BIGINT};
		auto &upd = planner.Make<FabricatorPhysicalUpdate>(std::move(result_types), op.EstimateCardinality(context),
		                                                   std::move(target), catalog.GetHandle());
		result->op = upd;
		break;
	}
	case MergeActionType::MERGE_DELETE: {
		auto target = FabricatorBuildModifyTarget(op.table);
		target.rowid_child_index = op.row_id_start;
		target.force_buffered = force_buffered;
		vector<LogicalType> result_types {LogicalType::BIGINT};
		auto &del = planner.Make<FabricatorPhysicalDelete>(std::move(result_types), op.EstimateCardinality(context),
		                                                   std::move(target), catalog.GetHandle());
		result->op = del;
		break;
	}
	case MergeActionType::MERGE_INSERT: {
		// Unlike UPDATE/DELETE this action DOES carry expressions: PhysicalMergeInto executes them into a
		// fresh chunk and sinks that, so our INSERT operator receives exactly the table's physical columns
		// in table order — never the raw child chunk. Hence the remap below produces one expression per
		// physical column, substituting the column's bound default where the statement named no value
		// (the same remap DuckCatalog and DuckLake perform).
		if (!action.column_index_map.empty()) {
			vector<unique_ptr<Expression>> new_expressions;
			for (auto &col : op.table.GetColumns().Physical()) {
				auto mapped_index = action.column_index_map[col.Physical()];
				if (mapped_index == DConstants::INVALID_INDEX) {
					new_expressions.push_back(op.bound_defaults[col.StorageOid()]->Copy());
				} else {
					new_expressions.push_back(std::move(action.expressions[mapped_index]));
				}
			}
			action.expressions = std::move(new_expressions);
		}
		result->expressions = std::move(action.expressions);

		FabricatorInsertTarget target;
		target.returning = false; // MERGE ... RETURNING is refused below.
		target.schema_name = op.table.schema.name;
		target.table_name = op.table.name;
		target.columns = op.table.GetColumns().GetColumnNames();
		for (auto &col : op.table.GetColumns().Logical()) {
			target.column_types.push_back(col.Type());
		}
		target.force_buffered = force_buffered;
		vector<LogicalType> result_types {LogicalType::BIGINT};
		auto &ins = planner.Make<FabricatorPhysicalInsert>(std::move(result_types), op.EstimateCardinality(context),
		                                                   std::move(target), catalog.GetHandle());
		result->op = ins;
		break;
	}
	case MergeActionType::MERGE_ERROR:
		// No operator: PhysicalMergeInto raises the error itself, using these expressions (if any) as the
		// user-supplied message.
		result->expressions = std::move(action.expressions);
		break;
	case MergeActionType::MERGE_DO_NOTHING:
		break;
	default:
		throw InternalException("fabricator: unsupported merge action");
	}
	return result;
}

PhysicalOperator &FabricatorCatalog::PlanMergeInto(ClientContext &context, PhysicalPlanGenerator &planner,
                                                   LogicalMergeInto &op, PhysicalOperator &plan) {
	if (op.return_chunk) {
		throw NotImplementedException("fabricator: MERGE INTO ... RETURNING is not supported yet");
	}
	// Row identity is required by EVERY merge, including one whose only action is an INSERT: DuckDB decides
	// matched-vs-not by testing the rowid column for NULL, so without one there is nothing to test.
	// `ComputeMatches` reads `chunk.data[row_id_index]` unconditionally, and with no rowid column the binder
	// leaves `row_id_start` == the chunk's width — an out-of-bounds read at execution rather than an error.
	// So refuse here, where it can still be a message. (An UPDATE/DELETE action would additionally hit
	// FabricatorBuildModifyTarget's own check, but an insert-only merge never reaches it.)
	auto &entry = op.table.Cast<FabricatorTableEntry>();
	if (!entry.HasRowId()) {
		throw BinderException("fabricator: MERGE INTO requires a table with a primary key or unique index for row "
		                      "identity. Table '%s' has neither",
		                      entry.name);
	}

	// ⚠ COUNT THE ROW-ADDRESSING ACTIONS FIRST — a merge with TWO OR MORE of them must run BUFFERED, and
	// that is a CORRECTNESS requirement, not an atomicity nicety.
	//
	// Every UPDATE/DELETE action consumes rowids captured from the merge's ONE join scan. If two of them
	// commit separately, a copy-on-write DELETE removes a row and RENUMBERS every later row in its file — so
	// the other action's captured (fileOrdinal, position) then names a DIFFERENT row. Measured before this
	// guard, on a one-file `deletion_vectors=false` table (1,10)(2,20)(3,30)(4,40) with conditional deletes of
	// id1 and id3: the survivors were 2 and 3 — id3 was NOT deleted and **id4 WAS DESTROYED**, exit 0, no
	// warning. The update variant silently lost the update instead. It is strictly positional (corrupt iff the
	// deleted row precedes the other action's target), which is why every test that put the rows in SEPARATE
	// FILES passed: the rewrite never renumbered another action's target.
	//
	// Buffering fixes it at the root: both actions stage against ONE pinned snapshot and fuse into a single
	// commit, so neither can renumber the other's targets. On a table whose buffered DML is impossible (no
	// deletion vectors) the provider refuses, naming them — a clean error in place of destroyed data.
	//
	// ⚠ COUNT ONLY UPDATE AND DELETE. An INSERT addresses no existing rows, so it can neither renumber another
	// action's targets nor hold targets of its own — and it commits LAST regardless (it is the one action that
	// always routes through the transaction buffer). Counting it too was measured to REFUSE the single most
	// common merge shape, `WHEN MATCHED THEN UPDATE` + `WHEN NOT MATCHED THEN INSERT`, on a non-DV table where
	// it had always been correct. That is the boundary DuckLake documents as well ("MERGE INTO with DuckLake
	// only supports a single UPDATE/DELETE action currently") — except that where DuckLake REFUSES two such
	// actions outright, we SERVE them by fusing, and only refuse when the table cannot buffer at all.
	//
	// One row-addressing action needs none of this: there is nothing for it to collide with, so it keeps the
	// direct per-statement path and a non-DV table loses no capability.
	idx_t rowid_actions = 0;
	for (auto &condition_entry : op.actions) {
		for (auto &action : condition_entry.second) {
			if (action->action_type == MergeActionType::MERGE_UPDATE ||
			    action->action_type == MergeActionType::MERGE_DELETE) {
				rowid_actions++;
			}
		}
	}
	// ⚠ AND ONLY WHERE ROW IDENTITY IS POSITIONAL. The hazard is that an action RENUMBERS rows another has
	// already addressed, which can only happen when a rowid is a TRANSIENT (file, position) address — i.e. a
	// provider VIRTUAL rowid, as Delta's `_metadata.row_id` is. Where the rowid is real KEY COLUMNS (SQL
	// Server's PK / unique index / IDENTITY) it is a VALUE, stable under any rewrite, so nothing can be
	// renumbered and forcing buys nothing. MEASURED both ways: the two corrupting shapes are correct on SQL
	// Server, and forcing there COST a capability — a 2-action merge into a SQL Server EXTERNAL table was
	// refused by the pre-existing "storage-side DML cannot roll back with an explicit transaction" guard,
	// which the forced mark trips. Gating on the identity KIND removes that without naming any provider.
	const bool force_buffered = rowid_actions >= 2 && entry.HasVirtualRowId();

	map<MergeActionCondition, vector<unique_ptr<MergeIntoOperator>>> actions;
	for (auto &condition_entry : op.actions) {
		vector<unique_ptr<MergeIntoOperator>> planned_actions;
		for (auto &action : condition_entry.second) {
			planned_actions.push_back(PlanFabricatorMergeAction(*this, context, planner, op, *action, force_buffered));
		}
		actions.emplace(condition_entry.first, std::move(planned_actions));
	}

	// parallel = FALSE, and this is load-bearing rather than conservative — but ⚠ ONE OF ITS TWO REASONS HAS
	// EXPIRED and the comment must not keep citing it. The dead one: "PushBatch takes no lock, safe only
	// because ParallelSink() is false". Our INSERT sink is now parallel-capable (the managed channel is
	// multi-writer and the session handle is read-only), so a concurrent push no longer corrupts the stream.
	// The reason that SURVIVES on its own: every action of a merge shares ONE global sink state, and the merge
	// operator drives our sub-operators MANUALLY (GetGlobalSinkState/Sink/Combine/Finalize called directly on
	// sliced chunks) rather than as pipelines — so their own ParallelSink() is never consulted here, and the
	// actions' shared state is what would have to be made safe. Note the INSERT action builds
	// FabricatorInsertTarget directly and leaves `parallel` at its false default, which is why nothing here
	// changed. Whether a merge could then go parallel is a SEPARATE decision, not a consequence of this one.
	// DuckDB's own DuckCatalog reaches false for a third reason (it disables parallelism as soon as there are
	// two appends); DuckLake passes true because its per-action operators are parallel-safe.
	auto &result = planner.Make<PhysicalMergeInto>(op.types, std::move(actions), op.row_id_start, op.source_marker,
	                                               /*parallel=*/false, op.return_chunk);
	result.children.push_back(plan);
	return result;
}

} // namespace duckdb
