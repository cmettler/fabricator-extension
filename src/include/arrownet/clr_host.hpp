//===----------------------------------------------------------------------===//
//                         ArrowNet — CoreCLR host
//
// clr_host.hpp
//
// Bootstraps an in-process .NET runtime (self-contained, shipped beside the
// extension) via hostfxr, loads the managed ArrowNet.Bridge assembly, and
// returns the C-ABI vtable it fills. Loading is idempotent and thread-safe.
//===----------------------------------------------------------------------===//

#pragma once

// Pull DuckDB's Arrow C struct definitions first (they are richer than abi.h's
// and share the same include guards), so the whole project uses one definition.
#include "duckdb/common/arrow/arrow.hpp"

#include "arrownet/abi.h"

#include <string>

namespace arrownet {

// Loads the managed bridge on first call (idempotent) and returns the vtable it
// populated. Throws duckdb::IOException with a descriptive message on failure.
//
// The managed assemblies + bundled runtime are located, in order of preference:
//   1. the ARROWNET_MANAGED_DIR environment variable, if set;
//   2. an "arrownet" subdirectory next to the loaded extension binary.
const ArrowNetVTable &GetBridge();

// Returns the resolved managed directory (for diagnostics).
const std::string &GetManagedDirectory();

// -----------------------------------------------------------------------------
// Convenience wrappers over the vtable. Each throws duckdb::IOException carrying
// the managed error message (and releases it via free_error) on failure.
// -----------------------------------------------------------------------------

// Open a backend catalog/connection. `provider` selects which registered backend
// handles it (case-insensitive name/alias, e.g. "sqlserver"/"mssql"); empty => the
// default backend. Returns an opaque handle to close later.
ArrowNetHandle OpenCatalog(const std::string &connection_string, const std::string &provider = "");

// Build a provider connection string from a secret's fields (`fields_json` = a flat
// JSON object of the secret's key/values). `provider` selects the backend whose
// connstr format applies (empty => default). Keeps all provider connection-string /
// auth formatting in the managed backend. Returns the assembled connection string.
std::string BuildConnectionString(const std::string &provider, const std::string &fields_json);

// Close a handle previously returned by OpenCatalog. Safe with nullptr.
void CloseCatalog(ArrowNetHandle handle);

// Execute a query and populate `out` with the resulting Arrow stream.
void ExecuteQuery(ArrowNetHandle handle, const std::string &sql, ArrowArrayStream &out);

// Execute a non-query statement (DML/DDL); returns the number of rows affected.
// `schema_may_change` (out, nullable): set true if the statement may have changed
// schema (DDL heuristic decided in C#), so the caller can invalidate its cache.
int64_t ExecuteDml(ArrowNetHandle handle, const std::string &sql, bool *schema_may_change = nullptr);

// Bulk-load an Arrow stream into a table; the managed side consumes/releases
// `in`. Returns rows written. (Generic: provider does type mapping + DDL + copy.)
int64_t BulkInsert(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool create_table,
                   bool replace, ArrowArrayStream &in);

// rowid-based DELETE: `keys` carries the key column values (Arrow). The managed
// side generates + runs the provider DELETE. Consumes/releases `keys`.
int64_t ExecuteDelete(ArrowNetHandle handle, const std::string &schema, const std::string &table,
                      ArrowArrayStream &keys);

// rowid-based UPDATE: `data` carries [set values..., key values...] (Arrow);
// the first `set_count` columns are SET values. Consumes/releases `data`.
int64_t ExecuteUpdate(ArrowNetHandle handle, const std::string &schema, const std::string &table, int32_t set_count,
                      ArrowArrayStream &data);

// Discover provider metadata. `kind` is an ArrowNetMetadataKind; `arg1`/`arg2`
// are the schema/table name when the kind needs them (empty otherwise). Fills
// `out` with the resulting Arrow stream. All provider catalog SQL lives in C#.
void GetMetadata(ArrowNetHandle handle, int32_t kind, const std::string &arg1, const std::string &arg2,
                 ArrowArrayStream &out);

// Scan a table; fills `out` with the rows as an Arrow stream. `spec_json` (empty
// => none) carries projection + filter pushdown; `filter_values` (nullable) is a
// one-batch Arrow stream of the typed constants the filter tree references.
void ScanTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, const std::string &spec_json,
               ArrowArrayStream *filter_values, ArrowArrayStream &out);

// DDL: create a table whose columns are described by the (zero-row) `columns`
// Arrow stream — a non-nullable field becomes NOT NULL. `pk_columns` /
// `unique_columns` carry key constraints as 0-based field indices: `pk_columns`
// is one comma-separated group ("0,1", empty if none); `unique_columns` is
// ';'-separated groups of comma-separated indices ("2;3,4"). The managed side
// maps Arrow types to provider types and runs CREATE TABLE. `defaults` carries
// literal column DEFAULTs as space-separated "<index> <payload>" pairs (payload
// = base64(value-text) or "-" for DEFAULT NULL). Consumes `columns`.
void CreateTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &columns,
                 bool if_not_exists, const std::string &pk_columns, const std::string &unique_columns,
                 const std::string &defaults, const std::string &text_type);

// DDL: drop a table (`if_exists` suppresses the missing-table error).
void DropTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool if_exists);

// DDL: create a schema (`if_not_exists` guards creation).
void CreateSchema(ArrowNetHandle handle, const std::string &schema, bool if_not_exists);

// DDL: drop a schema (`if_exists` suppresses the missing-schema error).
void DropSchema(ArrowNetHandle handle, const std::string &schema, bool if_exists);

// DDL: alter a table. `alter_kind` is an ArrowNetAlterKind; `arg1`/`arg2` carry
// names per kind (empty when unused). For ADD_COLUMN / COLUMN_TYPE pass the new
// column's type as a zero-row Arrow stream in `column` (nullptr otherwise; the
// managed side consumes it when present). `flags` bit 0 is the if-(not-)exists guard.
void AlterTable(ArrowNetHandle handle, const std::string &schema, const std::string &table, int32_t alter_kind,
                const std::string &arg1, const std::string &arg2, ArrowArrayStream *column, int32_t flags);

// Transaction boundaries (see abi.h). Begin enters transaction mode; the managed
// side pins a connection + provider transaction on the first write. Commit/Rollback
// finish it. All throw duckdb::IOException with the managed message on failure.
void BeginTransaction(ArrowNetHandle handle);
void CommitTransaction(ArrowNetHandle handle);
void RollbackTransaction(ArrowNetHandle handle);

// INSERT ... RETURNING: `in` carries the rows to insert (Arrow, field names = the
// target columns; consumed by the managed side). Fills `out` with the inserted
// rows (all table columns) from OUTPUT INSERTED.*.
void InsertReturning(ArrowNetHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &in,
                     ArrowArrayStream &out);

// -----------------------------------------------------------------------------
// Streaming bulk-load (begin/push/complete). Unlike BulkInsert (which hands over a
// whole stream), this streams batches so the host never buffers the full dataset;
// the managed side drains them on a background task with bounded-channel
// backpressure. See abi.h.
// -----------------------------------------------------------------------------

// Begin a streaming bulk-load session. `schema_in` describes the columns (the
// pushed batches must match it); the managed side consumes it (imports + releases).
// `check_constraints` validates CHECK/FOREIGN KEY constraints during load (INSERT
// semantics; SqlBulkCopy skips them by default — pass false for COPY/CTAS bulk speed).
// Returns an opaque session handle to push batches into and complete later.
ArrowNetHandle BeginBulk(ArrowNetHandle handle, const std::string &schema, const std::string &table, bool create_table,
                         bool replace, bool check_constraints, ArrowSchema &schema_in);

// Push one record batch into the session; the managed side imports + releases it
// (the caller never releases it). Blocks while the channel is full (backpressure).
void PushBatch(ArrowNetHandle session, ArrowArray &batch);

// Finish the session: wait for the background load to drain and return rows
// written. `abort` cancels the load (errors swallowed) for cleanup on a failed
// query. Frees the session; the handle is invalid afterwards.
int64_t CompleteBulk(ArrowNetHandle session, bool abort);

// -----------------------------------------------------------------------------
// Custom scalar functions. Discovered SQL Server scalar UDFs are exposed as DuckDB
// catalog scalar functions; these resolve their arg/return types and run them.
// -----------------------------------------------------------------------------

// Zero-row Arrow stream whose schema = the function's input parameters (one field
// per param, in order). Used to register the DuckDB function's argument types.
void GetFunctionParamSchema(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                            ArrowArrayStream &out);

// Zero-row Arrow stream whose single field = the scalar function's return type.
void GetFunctionReturnSchema(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                             ArrowArrayStream &out);

// Execute a scalar function over an input batch: `args` is an N-row stream of the
// argument columns (in param order; consumed by the managed side); fills `out` with
// an N-row, single-column stream of the per-row results.
void ExecuteScalar(ArrowNetHandle handle, const std::string &schema, const std::string &func, ArrowArrayStream &args,
                   ArrowArrayStream &out);

// Zero-row Arrow stream whose schema = a table-returning function's output columns. `args` (nullable) is a
// 1-row Arrow stream of the constant call arguments; a custom table function's output schema may depend on it
// (consumed by the managed side when non-null). Pass nullptr when there are no constant args (in-out base).
void GetFunctionOutputSchema(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                             ArrowArrayStream *args, ArrowArrayStream &out);

// Execute a table-valued function over its constant arguments: `args` is a 1-row stream
// of the argument values (in param order; consumed by the managed side). `spec_json`
// (empty => none) + `filter_values` (nullable) carry projection + best-effort filter
// pushdown into the TVF (like ScanTable). Fills `out` with the function's result rows.
void ExecuteTable(ArrowNetHandle handle, const std::string &schema, const std::string &func, ArrowArrayStream &args,
                  const std::string &spec_json, ArrowArrayStream *filter_values, ArrowArrayStream &out);

// Execute a stored procedure over its constant arguments: `args` is a 1-row stream of
// the positional argument values (consumed by the managed side); fills `out` with the
// procedure's first result set. No pushdown (EXEC is not inline-wrappable).
void ExecuteProc(ArrowNetHandle handle, const std::string &schema, const std::string &func, ArrowArrayStream &args,
                 ArrowArrayStream &out);

// -----------------------------------------------------------------------------
// Table-in-out (Phase 4). A session streams a TABLE in + a TABLE out (apply a
// function once per input row). See abi.h / docs §11.1.
// -----------------------------------------------------------------------------

// Open a session for `schema.func` over an input table described by `input_schema`
// (its columns are the function's positional params; consumed by the managed side).
// `isolation` (empty => provider default) sets the SQL transaction isolation level for the
// session's pinned connection. Returns an opaque session handle to push into / finish / abort.
ArrowNetHandle InOutOpen(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                         ArrowSchema &input_schema, const std::string &isolation);

// Push one input chunk (the managed side imports + releases it); fills `out` with the
// output rows available so far (may be empty). Blocks for backpressure.
void InOutPush(ArrowNetHandle session, ArrowArray &in_chunk, ArrowArrayStream &out);

// Signal input exhausted: drain + fill `out` with all remaining output. Idempotent.
void InOutFinish(ArrowNetHandle session, ArrowArrayStream &out);

// Release the session (error/cancel/LIMIT backstop). Idempotent; safe with nullptr.
void InOutAbort(ArrowNetHandle session);

// -----------------------------------------------------------------------------
// Custom aggregate functions (Phase 4h, C#-authored UDAF). The C++ aggregate
// callbacks keep each DuckDB state blob as an int64 id; the real accumulator lives
// in C# behind that id. One session per bound aggregate. See abi.h.
// -----------------------------------------------------------------------------

// Open a managed aggregate session for `schema.func`. Returns an opaque session handle.
ArrowNetHandle AggOpen(ArrowNetHandle handle, const std::string &schema, const std::string &func);

// Update: `batch` = [int64 state_id ++ argument columns], N rows (consumed/released by
// the managed side, which groups by id and folds each group into its accumulator).
void AggUpdate(ArrowNetHandle session, ArrowArray &batch);

// Combine: `batch` = [int64 target_id, int64 source_id], N rows (consumed/released);
// the managed side merges each source accumulator into its target.
void AggCombine(ArrowNetHandle session, ArrowArray &batch);

// Finalize: `ids` = a single int64 state_id column, N rows (consumed/released); fills
// `out` with one column of N results, in the SAME ORDER as `ids`.
void AggFinalize(ArrowNetHandle session, ArrowArray &ids, ArrowArrayStream &out);

// Destroy: `ids` = a single int64 state_id column (consumed/released); the managed side
// drops those accumulators. Best-effort (a destructor must not throw) — swallows errors.
void AggDestroy(ArrowNetHandle session, ArrowArray &ids);

// Release the session (frees the managed map). Idempotent; safe with nullptr.
// Best-effort — swallows errors (teardown must not throw).
void AggClose(ArrowNetHandle session);

// Spillable-mode aggregate steps (IArrowAggregateFunction.SupportsSpill): state travels as Arrow BLOB
// columns (one row per group; null = fresh). All input arrays are consumed/released by the managed side.

// `group_states` = BLOB[G] (current state per group), `batch` = [int64 slot ++ params]; fills `out` with
// BLOB[G] of the new state per group (same order as group_states).
void AggUpdateSpill(ArrowNetHandle session, ArrowArray &group_states, ArrowArray &batch, ArrowArrayStream &out);

// `target_states` = BLOB[G] (distinct targets), `batch` = [int64 slot, BLOB source]; fills `out` with
// BLOB[G] of the merged target state per target.
void AggCombineSpill(ArrowNetHandle session, ArrowArray &target_states, ArrowArray &batch, ArrowArrayStream &out);

// `states` = BLOB[N]; fills `out` with one result column of N rows.
void AggFinalizeSpill(ArrowNetHandle session, ArrowArray &states, ArrowArrayStream &out);

// -----------------------------------------------------------------------------
// Streaming table-in-out exchange (Phase 6, read-only). Two pull-based Arrow
// streams + a C++ gate replace the push/materialize model for discovered TVFs +
// custom C# in-out functions. See abi.h.
// -----------------------------------------------------------------------------

// Bind one in-out call. `args` (nullable) = a 1-row stream of the constant cost args (consumed by
// the managed side when present); `input_schema` = the input table's Arrow schema (consumed). Fills
// `out_schema` with a zero-row stream whose schema = the binding's full output columns. Returns an
// opaque binding handle (reused by InOutExchangeOpen; freed via InOutBindClose).
ArrowNetHandle InOutBind(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                         ArrowArrayStream *args, ArrowSchema &input_schema, ArrowArrayStream &out_schema);

// Open one execution exchange on a bound binding. `input` = a host-populated stream the managed side
// imports + pulls (one input chunk per gate tenure; released/null array = end). `isolation` (empty =>
// provider default) sets the SQL transaction isolation. Fills `output` with the managed output stream
// (the host pulls it: non-empty = HAVE_MORE_OUTPUT, length-0 = NEED_MORE_INPUT, null = FINISHED).
void InOutExchangeOpen(ArrowNetHandle binding, ArrowArrayStream &input, const std::string &isolation,
                       ArrowArrayStream &output);

// Release a binding handle from InOutBind. Idempotent; safe with nullptr. Best-effort (swallows errors).
void InOutBindClose(ArrowNetHandle binding);

// -----------------------------------------------------------------------------
// Table-function session (Phase 5). The session-handle successor to ExecuteTable /
// ExecuteProc: TableBind resolves a per-plan binding (output schema + whether it
// accepts pushdown); TableExecute runs it (per execution); TableClose frees it. The
// managed side classifies the function (TVF / proc / custom). See abi.h.
// -----------------------------------------------------------------------------

// Bind one table-function call. `args` (nullable) = a 1-row stream of the constant call args (consumed
// by the managed side). Fills `out_schema` with a zero-row stream = the function's output columns; sets
// `supports_pushdown` (true = the binding accepts projection/filter pushdown — a discovered TVF).
// Returns an opaque binding handle (reused by TableExecute across executions; freed via TableClose).
ArrowNetHandle TableBind(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                         ArrowArrayStream *args, ArrowArrayStream &out_schema, bool &supports_pushdown);

// Execute a bound table function. `spec_json` (empty => SELECT *) + `filter_values` (nullable) carry
// projection + best-effort filter pushdown (honored only when the binding supports it). Fills `out`
// with the result rows (its stream owns the provider connection, released by the host at scan teardown).
void TableExecute(ArrowNetHandle binding, const std::string &spec_json, ArrowArrayStream *filter_values,
                  ArrowArrayStream &out);

// Release a binding handle from TableBind. Idempotent; safe with nullptr. Best-effort (swallows errors).
void TableClose(ArrowNetHandle binding);

} // namespace arrownet
