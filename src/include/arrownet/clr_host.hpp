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

// Registers the host-services callbacks (the reverse direction: functions the managed side calls to reach
// DuckDB's FileSystem). MUST be called before the bridge first boots (GetBridge) so the services are passed
// to Bootstrap.Initialize. See ArrowNetHostServices in abi.h. (Filesystem reverse-callback foundation.)
void SetHostServices(const ArrowNetHostServices &services);

// Installs the host_query host-service callback onto the shared host-services block (called at extension
// load by the host-query module, after SetHostServices set the rest; both run before the bridge boots, so
// order-independent). See ArrowNetHostServices::host_query in abi.h.
using HostQueryFn = int32_t (*)(const char *sql, struct ArrowArrayStream *params,
                                struct ArrowNetHostInputs *inputs, struct ArrowArrayStream *out, char **err);
void SetHostQueryService(HostQueryFn fn);

// SPIKE: ask the managed side to open `path` via the host FileSystem callbacks (using `opener` for secret
// resolution) and return a short human-readable result (head/tail bytes + size). Proves C#->host FS reads.
std::string FsSpike(ArrowNetHandle opener, const std::string &path);

// Delta lakehouse reads (engineered-wood, IO via the host FileSystem callbacks). `opener` = the calling
// operator's ClientContext (secret resolution + FileSystem). DeltaSchema fills `out` with the table's Arrow
// schema only; DeltaScan reads the whole table into `out` (materialized during the call). Both throw on error.
void DeltaSchema(ArrowNetHandle opener, const std::string &path, ArrowSchema &out);
void DeltaScan(ArrowNetHandle opener, const std::string &path, ArrowArrayStream &out);

// Ambient named-source registry (data-in by name). OpenNamedInput fills `out` with a fresh Arrow stream for
// the registered source `name` (throws if none); NamedInputExists reports whether a source is registered
// (used by the replacement scan; false if the bridge predates the entry). See arrownet_host_query.cpp.
void OpenNamedInput(const std::string &name, ArrowArrayStream &out);
bool NamedInputExists(const std::string &name);

// -----------------------------------------------------------------------------
// Convenience wrappers over the vtable. Each throws duckdb::IOException carrying
// the managed error message (and releases it via free_error) on failure.
// -----------------------------------------------------------------------------

// Open a backend catalog/connection. `provider` selects which registered backend
// handles it (case-insensitive name/alias, e.g. "sqlserver"/"mssql"); empty => the
// default backend. `options_json` carries the provider-owned ATTACH options as a flat
// JSON object of strings (empty => none); the managed side parses the keys it knows.
// Returns an opaque handle to close later.
ArrowNetHandle OpenCatalog(const std::string &connection_string, const std::string &provider = "",
                           const std::string &options_json = "");

// Build a provider connection string from a secret's fields (`fields_json` = a flat JSON object of the
// secret's key/values). `provider` selects the backend (empty => default). `secret_type` is the DuckDB
// secret type the fields came from (so the backend interprets them per type, e.g. an azure secret mapped to
// Entra auth). `base_connstr` (empty => none) is the ATTACH target, used when a foreign secret carries only
// auth. Keeps all provider connstr/auth formatting in the managed backend. Returns the assembled connstr.
std::string BuildConnectionString(const std::string &provider, const std::string &secret_type,
                                  const std::string &fields_json, const std::string &base_connstr = "");

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

// Provider-declared settings (see docs/settings-architecture.md). ListSettings fills `out` with ALL
// registered providers' declared settings (six string columns: provider, name, type, default, description,
// min); the host registers each as a DuckDB extension option at load. SetSetting pushes a setting's value
// (nullptr => unset) into the managed ProviderSettingsStore.
void ListSettings(ArrowArrayStream &out);
void SetSetting(const std::string &provider, const std::string &name, const char *value);

// Provider-declared secret fields (see docs/provider-extensibility.md §2). Fills `out` with ALL registered
// providers' secret types + fields (five string columns: provider, secret_type, name, type, redact); the
// host registers one DuckDB secret type per distinct secret_type at load.
void ListSecretFields(ArrowArrayStream &out);

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
                 const std::string &defaults);

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
                         bool replace, bool check_constraints, int64_t txn_id, ArrowSchema &schema_in);

// Record the DuckDB transaction id in effect on this thread (global_transaction_id), so the next
// connection-using bridge call keys its per-transaction provider connection by it. Call immediately before
// each such call, on the same thread. txn_id 0 => no specific transaction. `join_only` (raw mssql_net_exec
// only) joins the active transaction's connection iff one exists, else autocommits without pinning a
// connection — see abi.h / docs/dbt-hooks.md. Best-effort (never throws).
void SetActiveTxn(ArrowNetHandle handle, int64_t txn_id, bool join_only = false);

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

// Fills `out` with the Arrow schema of the function's input parameters (one field per param, in order),
// used to register the DuckDB function's argument types. A bare ArrowSchema (the caller reads it then
// releases it via ArrowSchemaWrapper).
void GetFunctionParamSchema(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                            ArrowSchema &out);

// Fills `out` with the Arrow schema whose single field = the scalar function's return type.
void GetFunctionReturnSchema(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                             ArrowSchema &out);

// Execute a scalar function over an input batch: `args` is an N-row stream of the
// argument columns (in param order; consumed by the managed side); fills `out` with
// an N-row, single-column stream of the per-row results.
void ExecuteScalar(ArrowNetHandle handle, const std::string &schema, const std::string &func, ArrowArrayStream &args,
                   ArrowArrayStream &out);

// Fills `out` with the Arrow schema of a table-returning function's output columns. `args` (nullable) is a
// 1-row Arrow stream of the constant call arguments; a custom table function's output schema may depend on it
// (consumed by the managed side when non-null). Pass nullptr when there are no constant args (in-out base).
void GetFunctionOutputSchema(ArrowNetHandle handle, const std::string &schema, const std::string &func,
                             ArrowArrayStream *args, ArrowSchema &out);

// (ExecuteTable / ExecuteProc were removed at ABI v30 — superseded by the table-function session
//  TableBind / TableExecute / TableClose below.)

// (The 4g table-in-out push wrappers InOutOpen/InOutPush/InOutFinish/InOutAbort were removed at ABI v31 —
//  every `_each` form now runs on the streaming exchange: InOutBind/InOutExchangeOpen/InOutBindClose below.)

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
// imports + pulls (one input chunk per gate tenure; released/null array = end). Fills `output` with the
// managed output stream (the host pulls it: non-empty = HAVE_MORE_OUTPUT, length-0 = NEED_MORE_INPUT,
// null = FINISHED). The SQL isolation is resolved + set on the binding in C# at inout_bind, not passed here.
void InOutExchangeOpen(ArrowNetHandle binding, ArrowArrayStream &input, ArrowArrayStream &output);

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
