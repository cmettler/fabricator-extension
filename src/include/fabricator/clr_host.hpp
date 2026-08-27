// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         Fabricator — CoreCLR host
//
// clr_host.hpp
//
// Bootstraps an in-process .NET runtime (self-contained, shipped beside the
// extension) via hostfxr, loads the managed Fabricator.Bridge assembly, and
// returns the C-ABI vtable it fills. Loading is idempotent and thread-safe.
//===----------------------------------------------------------------------===//

#pragma once

// Pull DuckDB's Arrow C struct definitions first (they are richer than abi.h's
// and share the same include guards), so the whole project uses one definition.
#include "duckdb/common/arrow/arrow.hpp"
// ObjectNotFoundException derives from IOException so an UNCAUGHT one reads exactly as every other bridge
// failure did before it existed — the type only adds a handle for the one caller that must tell absence
// from unreadability. The header already depends on DuckDB (above), so this adds no new layering.
#include "duckdb/common/exception.hpp"

#include "fabricator/abi.h"

#include <string>

namespace fabricator {

// Loads the managed bridge on first call (idempotent) and returns the vtable it
// populated. Throws duckdb::IOException with a descriptive message on failure.
//
// The managed assemblies + bundled runtime are located, in order of preference:
//   1. the FABRICATOR_MANAGED_DIR environment variable, if set;
//   2. an "fabricator" subdirectory next to the loaded extension binary.
const FabricatorVTable &GetBridge();

// Returns the resolved managed directory (for diagnostics).
const std::string &GetManagedDirectory();

// Registers the host-services callbacks (the reverse direction: functions the managed side calls to reach
// DuckDB's FileSystem). MUST be called before the bridge first boots (GetBridge) so the services are passed
// to Bootstrap.Initialize. See FabricatorHostServices in abi.h. (Filesystem reverse-callback foundation.)
void SetHostServices(const FabricatorHostServices &services);

// Installs the host_query host-service callback onto the shared host-services block (called at extension
// load by the host-query module, after SetHostServices set the rest; both run before the bridge boots, so
// order-independent). See FabricatorHostServices::host_query in abi.h.
using HostQueryFn = int32_t (*)(const char *sql, struct ArrowArrayStream *params,
                                struct FabricatorHostInputs *inputs, struct ArrowArrayStream *out,
                                void **out_interrupt, char **err);
using HostQueryInterruptFn = void (*)(void *interrupt_handle);
void SetHostQueryService(HostQueryFn fn, HostQueryInterruptFn interrupt_fn, HostQueryInterruptFn free_fn);

// Register the host_log callback (DuckDB internal-logging forward). Patched onto the shared host-services block
// at load, like SetHostQueryService. See FabricatorHostServices::host_log in abi.h.
using HostLogFn = void (*)(int32_t level, const char *log_type, const char *message);
void SetHostLog(HostLogFn fn);

// Register the http_request callback (perform an HTTP request through DuckDB's own HTTP stack, so a managed
// caller inherits its secrets / TLS trust / proxy / retry). Patched onto the shared host-services block at
// load, like SetHostQueryService. See FabricatorHostServices::http_request in abi.h + docs/http-transport.md.
using HostHttpFn = int32_t (*)(FabricatorHandle opener, const char *method, const char *url,
                               const char *headers_json, const void *body, int64_t body_length,
                               char **out_response_json, void **out_body, int64_t *out_body_length, char **err);
void SetHostHttpService(HostHttpFn fn);

// SPIKE: ask the managed side to open `path` via the host FileSystem callbacks (using `opener` for secret
// resolution) and return a short human-readable result (head/tail bytes + size). Proves C#->host FS reads.
std::string FsSpike(FabricatorHandle opener, const std::string &path);

// Record the calling operator's ClientContext as the active host-FS opener (a per-thread ambient on the
// managed side), so a connection-free GLOBAL host-FS table function (a lakehouse reader: Delta/Iceberg/…)
// can resolve DuckDB secrets when reading through the host FileSystem callbacks. The host calls this
// IMMEDIATELY before each table-function bind + execution (same thread, synchronous); `opener` is valid only
// for the call it precedes. Best-effort (mirrors SetActiveTxn). See docs/global-functions.md §host-FS.
//
// `session` records which DuckDB connection's SESSION-SCOPED provider settings apply (see SessionKeyFor).
// It has NO default on purpose: every call site must answer "whose settings?", because the two sites where
// the answer differs from the opener — the commit flush and the rollback, which open their own connection —
// are exactly the ones that would otherwise resolve a write against a connection that has set nothing.
void SetActiveOpener(FabricatorHandle opener, int64_t session);

// The SESSION KEY for provider settings: the address of a DuckDB ClientContext, i.e. ONE KEY PER DuckDB
// CONNECTION (a Connection owns its context for its whole life, so the key is stable across that
// connection's statements and unique while it lives). 0 means "no session" and resolves to the GLOBAL layer.
//
// ⚠ It is an ADDRESS, so it is only meaningful while that connection is alive — which is why the managed
// store must be told when one closes, or a later connection landing on the same address would inherit a dead
// one's settings.
inline int64_t SessionKeyFor(const void *client_context) {
	return static_cast<int64_t>(reinterpret_cast<uintptr_t>(client_context));
}

// Ambient named-source registry (data-in by name). OpenNamedInput fills `out` with a fresh Arrow stream for
// the registered source `name` (throws if none); NamedInputExists reports whether a source is registered
// (used by the replacement scan; false if the bridge predates the entry). See fabricator_host_query.cpp.
void OpenNamedInput(const std::string &name, ArrowArrayStream &out);
bool NamedInputExists(const std::string &name);

// onelake:// FileSystem forward calls (Phase-3): the C++ onelake FS subsystem (registered in DuckDB's VFS)
// forwards its read ops to the managed Azure DataLake SDK. `cred_json` = the azure secret fields the host
// resolved from the calling opener (empty/"{}" => DefaultAzureCredential). Read-only for now. OneLakeOpen
// returns an opaque managed handle (close via OneLakeClose) + the file length in `out_size`.
// `known_size` >= 0 (from a listing) skips the per-file properties round trip (v62); -1 = fetch. When the
// managed side fetches properties (v63) it also returns the cache-validation identity: `out_etag` /
// `out_modified_ms` (epoch ms, -1 unknown) — untouched on the skip path (the caller uses listing values).
FabricatorHandle OneLakeOpen(const std::string &path, const std::string &cred_json, int64_t &out_size,
                           int64_t known_size = -1, std::string *out_etag = nullptr,
                           int64_t *out_modified_ms = nullptr);
void OneLakeRead(FabricatorHandle file, void *buffer, int64_t nr_bytes, int64_t location);
void OneLakeClose(FabricatorHandle file);
std::string OneLakeGlob(const std::string &pattern, const std::string &cred_json);
bool OneLakeExists(const std::string &path, const std::string &cred_json);

// onelake:// WRITE (slice 2): a plain sequential file write (COPY … TO 'onelake://…'). OneLakeOpenWrite
// creates/overwrites the file (`exclusive` => put-if-absent, ADLS conditional create — EXCLUSIVE_CREATE
// semantics, v61) and returns a managed write handle; OneLakeWrite appends; OneLakeCloseWrite flushes +
// frees the handle. OneLakeRemove deletes a single file (idempotent, v61).
FabricatorHandle OneLakeOpenWrite(const std::string &path, const std::string &cred_json, bool exclusive);
void OneLakeWrite(FabricatorHandle file, const void *buffer, int64_t nr_bytes);
void OneLakeCloseWrite(FabricatorHandle file);
void OneLakeRemove(const std::string &path, const std::string &cred_json);
// Atomic onelake:// single-file rename via the DFS native rename (overwrites the destination —
// MoveFile semantics; backs DuckDB's COPY tmp-file staging, v64). Same-workspace only.
void OneLakeMove(const std::string &src, const std::string &dest, const std::string &cred_json);

// SQL-generating table function (v68): generate the replacement SQL for one call. `handle` = 0 + empty
// `schema`/`catalog_name` => the GLOBAL registry (resolve `func` by name); non-zero => the catalog's
// registry, with `catalog_name` = the DuckDB ATTACH alias. `args` (nullable — no arguments) is the 1-row
// constant-argument batch and is CONSUMED by the callee. Called at BIND time only. Throws
// duckdb::IOException carrying the managed message on failure.
std::string GenerateTableSql(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             const std::string &catalog_name, ArrowArrayStream *args);

// -----------------------------------------------------------------------------
// Convenience wrappers over the vtable. Each throws duckdb::IOException carrying
// the managed error message (and releases it via free_error) on failure.
// -----------------------------------------------------------------------------

// Open a backend catalog/connection. `provider` selects which registered backend
// handles it (case-insensitive name/alias, e.g. "sqlserver"/"mssql"); empty => the
// default backend. `options_json` carries the provider-owned ATTACH options as a flat
// JSON object of strings (empty => none); the managed side parses the keys it knows.
// Returns an opaque handle to close later.
FabricatorHandle OpenCatalog(const std::string &connection_string, const std::string &provider = "",
                           const std::string &options_json = "");

// Build a provider connection string from a secret's fields (`fields_json` = a flat JSON object of the
// secret's key/values). `provider` selects the backend (empty => default). `secret_type` is the DuckDB
// secret type the fields came from (so the backend interprets them per type, e.g. an azure secret mapped to
// Entra auth). `base_connstr` (empty => none) is the ATTACH target, used when a foreign secret carries only
// auth. Keeps all provider connstr/auth formatting in the managed backend. Returns the assembled connstr.
std::string BuildConnectionString(const std::string &provider, const std::string &secret_type,
                                  const std::string &fields_json, const std::string &base_connstr = "");

// The catalog's capability doc (ABI v71): one flat JSON object of boolean capability flags — an absent
// key means false. Read once at ATTACH (LoadCatalog). See abi.h `get_capabilities` for the contract and
// why this is not part of open_catalog's result.
std::string GetCapabilities(FabricatorHandle handle);

// Close a handle previously returned by OpenCatalog. Safe with nullptr.
void CloseCatalog(FabricatorHandle handle);

// Execute a query and populate `out` with the resulting Arrow stream.
void ExecuteQuery(FabricatorHandle handle, const std::string &sql, ArrowArrayStream &out);

// Execute a non-query statement (DML/DDL); returns the number of rows affected.
// `schema_may_change` (out, nullable): set true if the statement may have changed
// schema (DDL heuristic decided in C#), so the caller can invalidate its cache.
int64_t ExecuteDml(FabricatorHandle handle, const std::string &sql, bool *schema_may_change = nullptr);

// Bulk-load an Arrow stream into a table; the managed side consumes/releases
// `in`. Returns rows written. (Generic: provider does type mapping + DDL + copy.)
int64_t BulkInsert(FabricatorHandle handle, const std::string &schema, const std::string &table, bool create_table,
                   bool replace, ArrowArrayStream &in);

// rowid-based DELETE: `keys` carries the key column values (Arrow). The managed
// side generates + runs the provider DELETE. Consumes/releases `keys`.
int64_t ExecuteDelete(FabricatorHandle handle, const std::string &schema, const std::string &table,
                      ArrowArrayStream &keys);

// rowid-based UPDATE: `data` carries [set values..., key values...] (Arrow);
// the first `set_count` columns are SET values. Consumes/releases `data`.
int64_t ExecuteUpdate(FabricatorHandle handle, const std::string &schema, const std::string &table, int32_t set_count,
                      ArrowArrayStream &data);

// The provider reported FABRICATOR_NOT_FOUND: the object GENUINELY DOES NOT EXIST, as opposed to existing
// and being unreadable. Derives from IOException so an uncaught one reads exactly as it did before.
//
// Catch it ONLY where absence has a distinct meaning — chiefly the catalog's entry materialization, which
// turns absence into "this table is gone" (dropping the entry AND removing the name from enumeration) so
// CREATE TABLE IF NOT EXISTS / OR REPLACE work after an out-of-band DROP. Catching every failure there
// instead made a table with intact data vanish whenever its columns merely could not be READ — a holed
// log, an expired credential, a brief outage — and reported it as "Table with name t does not exist!".
class ObjectNotFoundException : public duckdb::IOException {
public:
	explicit ObjectNotFoundException(const std::string &msg) : duckdb::IOException(msg) {
	}
};

// Catalog discovery (ABI v72 — get_metadata's replacement; see abi.h for each entry's column layout).
// All provider catalog SQL lives in C#.
void CatalogSchemas(FabricatorHandle handle, ArrowArrayStream &out);
void CatalogTables(FabricatorHandle handle, ArrowArrayStream &out);
void CatalogFunctions(FabricatorHandle handle, ArrowArrayStream &out);
// Give the provider its ONE chance to initialise with a live client context (ABI v78) — called from
// LoadCatalog after the ambients are established and BEFORE any discovery crossing. Optional provider-side
// (a no-op DIM), but THROWS when the provider fails: init is where "I cannot serve this catalog" belongs,
// which is why open_catalog (no ambients, construction only) could never be that place.
void CatalogInit(FabricatorHandle handle);

void CatalogMacros(FabricatorHandle handle, ArrowArrayStream &out);
void CatalogViews(FabricatorHandle handle, ArrowArrayStream &out);
void CatalogServerInfo(FabricatorHandle handle, ArrowArrayStream &out);

// The table session (ABI v72/v73; see abi.h for the full contract). TableOpen resolves (schema, table[,
// AT]) to a session handle — no IO, no absence probe. TableSchema fills a ZERO-ROW stream whose Arrow
// schema is the column layout and throws ObjectNotFoundException when the provider reports the table
// absent (the old kind-2 contract, one entry over). TableInfo/TableStats return the JSON docs (v73 —
// {"rowid":[...],"virtual":[...]} / {"row_count":N,"ndv":{...}}), parsed by the caller with yyjson.
// TableScan = the old ScanTable minus the name pair. TableAlter (v74) replaces AlterTable: one typed JSON
// doc (rendered by FabricatorRenderAlterJson) plus the optional `column` type-channel stream, which the
// managed side consumes when present. TableClose is best-effort and never throws (it runs from the entry
// destructor at teardown).
FabricatorHandle TableOpen(FabricatorHandle handle, const std::string &schema, const std::string &table,
                           const std::string &at_unit = "", const std::string &at_value = "");
void TableSchema(FabricatorHandle table, ArrowArrayStream &out);
std::string TableInfo(FabricatorHandle table);
std::string TableStats(FabricatorHandle table);
void TableScan(FabricatorHandle table, const std::string &spec_json, ArrowArrayStream *filter_values,
               ArrowArrayStream &out);
void TableAlter(FabricatorHandle table, const std::string &alter_json, ArrowArrayStream *column);
void TableClose(FabricatorHandle table) noexcept;

// Provider-declared settings (see docs/settings-architecture.md). ListSettings fills `out` with ALL
// registered providers' declared settings (six string columns: provider, name, type, default, description,
// min); the host registers each as a DuckDB extension option at load. SetSetting pushes a setting's value
// (nullptr => unset) into the managed ProviderSettingsStore, at `session` scope (0 = global; see
// SessionKeyFor).
void ListSettings(ArrowArrayStream &out);
void SetSetting(const std::string &provider, const std::string &name, const char *value, int64_t session);

// Drops every session-scoped setting for `session` — called when the owning DuckDB connection closes, so a
// later connection reusing that ADDRESS cannot inherit a dead one's settings. Best-effort and never throws:
// it runs from a destructor.
void ClearSessionSettings(int64_t session) noexcept;

// Load-time global (connection-free) functions (see docs/global-functions.md). Fills `out` with the
// provider-union of global functions (metadata columns: name, kind, param_count, return_type); the host
// registers each as a bare `fn(...)` at extension load. Per-function param/return schemas + execution reuse
// the scalar entries with handle = 0 (the global marker).
void ListGlobalFunctions(ArrowArrayStream &out);

// Provider-declared secret fields (see docs/provider-extensibility.md §2). Fills `out` with ALL registered
// providers' secret types + fields (five string columns: provider, secret_type, name, type, redact); the
// host registers one DuckDB secret type per distinct secret_type at load.
void ListSecretFields(ArrowArrayStream &out);

// DDL: create a table whose columns are described by the (zero-row) `columns`
// Arrow stream — a non-nullable field becomes NOT NULL. `pk_columns` /
// `unique_columns` carry key constraints as 0-based field indices: `pk_columns`
// is one comma-separated group ("0,1", empty if none); `unique_columns` is
// ';'-separated groups of comma-separated indices ("2;3,4"). The managed side
// maps Arrow types to provider types and runs CREATE TABLE. `defaults` carries
// literal column DEFAULTs as space-separated "<index> <payload>" pairs (payload
// = base64(value-text) or "-" for DEFAULT NULL). Consumes `columns`.
void CreateTable(FabricatorHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &columns,
                 bool if_not_exists, const std::string &pk_columns, const std::string &unique_columns,
                 const std::string &defaults, const std::string &partition_columns = "",
                 const std::string &sort_columns = "", const std::string &identity_columns = "",
                 const std::string &options_json = "");

// DDL: drop a table (`if_exists` suppresses the missing-table error).
void DropTable(FabricatorHandle handle, const std::string &schema, const std::string &table, bool if_exists);

// DDL: create a schema (`if_not_exists` guards creation).
void CreateSchema(FabricatorHandle handle, const std::string &schema, bool if_not_exists);

// DDL: drop a schema (`if_exists` suppresses the missing-schema error).
void DropSchema(FabricatorHandle handle, const std::string &schema, bool if_exists);

// (AlterTable was replaced at ABI v74 by TableAlter, declared with the other table-session wrappers.)

// Transaction boundaries (see abi.h). Begin enters transaction mode; the managed
// side pins a connection + provider transaction on the first write. Commit/Rollback
// finish it. All throw duckdb::IOException with the managed message on failure.
void BeginTransaction(FabricatorHandle handle, bool is_explicit);
void CommitTransaction(FabricatorHandle handle);
void RollbackTransaction(FabricatorHandle handle);

// INSERT ... RETURNING: `in` carries the rows to insert (Arrow, field names = the
// target columns; consumed by the managed side). Fills `out` with the inserted
// rows (all table columns) from OUTPUT INSERTED.*.
void InsertReturning(FabricatorHandle handle, const std::string &schema, const std::string &table, ArrowArrayStream &in,
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
FabricatorHandle BeginBulk(FabricatorHandle handle, const std::string &schema, const std::string &table, bool create_table,
                         bool replace, bool check_constraints, int64_t txn_id, ArrowSchema &schema_in,
                         const std::string &partition_columns = "", const std::string &sort_columns = "",
                         const std::string &schema_mode = "", bool partition_overwrite = false,
                         const std::string &options_json = "");

// Record the DuckDB transaction id in effect on this thread (global_transaction_id), so the next
// connection-using bridge call keys its per-transaction provider connection by it. Call immediately before
// each such call, on the same thread. txn_id 0 => no specific transaction. `join_only` (raw fabricator_exec
// only) joins the active transaction's connection iff one exists, else autocommits without pinning a
// connection — see abi.h / docs/dbt-hooks.md. Best-effort (never throws).
void SetActiveTxn(FabricatorHandle handle, int64_t txn_id, bool join_only = false);

// Push one record batch into the session; the managed side imports + releases it
// (the caller never releases it). Blocks while the channel is full (backpressure).
void PushBatch(FabricatorHandle session, ArrowArray &batch);

// Finish the session: wait for the background load to drain and return rows
// written. `abort` cancels the load (errors swallowed) for cleanup on a failed
// query. Frees the session; the handle is invalid afterwards.
int64_t CompleteBulk(FabricatorHandle session, bool abort);

// -----------------------------------------------------------------------------
// Custom scalar functions. Discovered SQL Server scalar UDFs are exposed as DuckDB
// catalog scalar functions; these resolve their arg/return types and run them.
// -----------------------------------------------------------------------------

// Fills `out` with the Arrow schema of the function's input parameters (one field per param, in order),
// used to register the DuckDB function's argument types. A bare ArrowSchema (the caller reads it then
// releases it via ArrowSchemaWrapper).
void GetFunctionParamSchema(FabricatorHandle handle, const std::string &schema, const std::string &func,
                            ArrowSchema &out);

// Fills `out` with the Arrow schema whose single field = the scalar function's DECLARED return type. A field
// of Arrow `null` type is the UNRESOLVED sentinel — scalarfn_bind supplies the type per call site.
void GetFunctionReturnSchema(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             ArrowSchema &out);

// ---------------------------------------------------------------------------
// Scalar-function session (ABI v80) — the successor to the removed stateless ExecuteScalar. ScalarFnBind
// resolves a per-CALL-SITE binding (result field + bind state); ScalarFnExecute reuses it per chunk;
// ScalarFnClose frees it. Mirrors TableFnBind / TableFnExecute / TableFnClose.
// ---------------------------------------------------------------------------

// Bind one scalar call site. `args` (nullable) is a 1-row stream of the call's arguments, PARTIAL and
// PRE-CAST: the host folds only the constant ones and marks those fields `fabricator.arg_constant`="1" (an
// unmarked field's value is a meaningless placeholder). Fills `out_schema` (a BARE ArrowSchema, like
// GetFunctionReturnSchema) with the single resolved result field — Arrow `null` type meaning "the DECLARED
// type stands". Returns an opaque binding handle (reused by ScalarFnExecute for every chunk; freed via
// ScalarFnClose).
FabricatorHandle ScalarFnBind(FabricatorHandle handle, const std::string &schema, const std::string &func,
                              ArrowArrayStream *args, const std::string &arg_constant,
                              ArrowSchema &out_schema);

// Execute a bound scalar function over one chunk: `args` is an N-row stream of the argument columns (in
// param order, post-cast; consumed by the managed side); fills `out` with an N-row, single-column stream of
// the per-row results.
void ScalarFnExecute(FabricatorHandle binding, ArrowArrayStream &args, ArrowArrayStream &out);

// Release a binding handle from ScalarFnBind. Idempotent; safe with nullptr. Best-effort (swallows errors).
void ScalarFnClose(FabricatorHandle binding);

// Fills `out` with the Arrow schema of a table-returning function's output columns. `args` (nullable) is a
// 1-row Arrow stream of the constant call arguments; a custom table function's output schema may depend on it
// (consumed by the managed side when non-null). Pass nullptr when there are no constant args (in-out base).
void GetFunctionOutputSchema(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             ArrowArrayStream *args, ArrowSchema &out);

// (ExecuteTable / ExecuteProc were removed at ABI v30 — superseded by the table-function session
//  TableFnBind / TableFnExecute / TableFnClose below.)

// (The 4g table-in-out push wrappers InOutOpen/InOutPush/InOutFinish/InOutAbort were removed at ABI v31 —
//  every `_each` form now runs on the streaming exchange: InOutBind/InOutExchangeOpen/InOutBindClose below.)

// -----------------------------------------------------------------------------
// Custom aggregate functions (Phase 4h, C#-authored UDAF). The C++ aggregate
// callbacks keep each DuckDB state blob as an int64 id; the real accumulator lives
// in C# behind that id. One session per bound aggregate. See abi.h.
// -----------------------------------------------------------------------------

// Open a managed aggregate session for `schema.func`. Returns an opaque session handle.
FabricatorHandle AggOpen(FabricatorHandle handle, const std::string &schema, const std::string &func);

// Update: `batch` = [int64 state_id ++ argument columns], N rows (consumed/released by
// the managed side, which groups by id and folds each group into its accumulator).
void AggUpdate(FabricatorHandle session, ArrowArray &batch);

// Combine: `batch` = [int64 target_id, int64 source_id], N rows (consumed/released);
// the managed side merges each source accumulator into its target.
void AggCombine(FabricatorHandle session, ArrowArray &batch);

// Finalize: `ids` = a single int64 state_id column, N rows (consumed/released); fills
// `out` with one column of N results, in the SAME ORDER as `ids`.
void AggFinalize(FabricatorHandle session, ArrowArray &ids, ArrowArrayStream &out);

// Destroy: `ids` = a single int64 state_id column (consumed/released); the managed side
// drops those accumulators. Best-effort (a destructor must not throw) — swallows errors.
void AggDestroy(FabricatorHandle session, ArrowArray &ids);

// Release the session (frees the managed map). Idempotent; safe with nullptr.
// Best-effort — swallows errors (teardown must not throw).
void AggClose(FabricatorHandle session);

// Spillable-mode aggregate steps (IArrowAggregateFunction.SupportsSpill): state travels as Arrow BLOB
// columns (one row per group; null = fresh). All input arrays are consumed/released by the managed side.

// `group_states` = BLOB[G] (current state per group), `batch` = [int64 slot ++ params]; fills `out` with
// BLOB[G] of the new state per group (same order as group_states).
void AggUpdateSpill(FabricatorHandle session, ArrowArray &group_states, ArrowArray &batch, ArrowArrayStream &out);

// `target_states` = BLOB[G] (distinct targets), `batch` = [int64 slot, BLOB source]; fills `out` with
// BLOB[G] of the merged target state per target.
void AggCombineSpill(FabricatorHandle session, ArrowArray &target_states, ArrowArray &batch, ArrowArrayStream &out);

// `states` = BLOB[N]; fills `out` with one result column of N rows.
void AggFinalizeSpill(FabricatorHandle session, ArrowArray &states, ArrowArrayStream &out);

// -----------------------------------------------------------------------------
// Streaming table-in-out exchange (Phase 6, read-only). Two pull-based Arrow
// streams + a C++ gate replace the push/materialize model for discovered TVFs +
// custom C# in-out functions. See abi.h.
// -----------------------------------------------------------------------------

// Bind one in-out call. `args` (nullable) = a 1-row stream of the constant cost args (consumed by
// the managed side when present); `input_schema` = the input table's Arrow schema (consumed). Fills
// `out_schema` with a zero-row stream whose schema = the binding's full output columns. Returns an
// opaque binding handle (reused by InOutExchangeOpen; freed via InOutBindClose).
FabricatorHandle InOutBind(FabricatorHandle handle, const std::string &schema, const std::string &func,
                         ArrowArrayStream *args, ArrowSchema &input_schema, ArrowArrayStream &out_schema);

// Open one execution exchange on a bound binding. `input` = a host-populated stream the managed side
// imports + pulls (one input chunk per gate tenure; released/null array = end). Fills `output` with the
// managed output stream (the host pulls it: non-empty = HAVE_MORE_OUTPUT, length-0 = NEED_MORE_INPUT,
// null = FINISHED). The SQL isolation is resolved + set on the binding in C# at inout_bind, not passed here.
void InOutExchangeOpen(FabricatorHandle binding, ArrowArrayStream &input, ArrowArrayStream &output);

// Release a binding handle from InOutBind. Idempotent; safe with nullptr. Best-effort (swallows errors).
void InOutBindClose(FabricatorHandle binding);

// -----------------------------------------------------------------------------
// Row-mapped (correlated LATERAL) table functions (ABI v79). See abi.h for the shape and why provenance
// is what makes the batched path sound.
// -----------------------------------------------------------------------------

// Bind one lateral call. `args` (nullable) = a 1-row stream of the constant NAMED cost args; `input_schema`
// = the per-row input columns (consumed). Fills `out_schema` with a zero-row stream whose schema = the
// function's OWN output columns. Returns an opaque binding handle (freed via LateralBindClose).
FabricatorHandle LateralBind(FabricatorHandle handle, const std::string &schema, const std::string &func,
                             ArrowArrayStream *args, ArrowSchema &input_schema, ArrowArrayStream &out_schema);

// Open one per-thread session on a bound binding (several may be open at once).
FabricatorHandle LateralOpen(FabricatorHandle binding);

// One batched call: `input` = an N-row array of the input columns (consumed). Fills `out` with the result
// stream, whose batches carry the output columns + a TRAILING int32 provenance column.
void LateralCall(FabricatorHandle session, ArrowArray &input, ArrowArrayStream &out);

// Release a session / a binding. Both idempotent, nullptr-safe, best-effort (swallow errors).
void LateralClose(FabricatorHandle session);
void LateralBindClose(FabricatorHandle binding);

// -----------------------------------------------------------------------------
// Table-function session (Phase 5). The session-handle successor to ExecuteTable /
// ExecuteProc: TableFnBind resolves a per-plan binding (output schema + whether it
// accepts pushdown); TableFnExecute runs it (per execution); TableFnClose frees it. The
// managed side classifies the function (TVF / proc / custom). See abi.h.
// -----------------------------------------------------------------------------

// Bind one table-function call. `args` (nullable) = a 1-row stream of the constant call args (consumed
// by the managed side). Fills `out_schema` with a zero-row stream = the function's output columns; sets
// `supports_pushdown` (true = the binding accepts projection/filter pushdown — a discovered TVF).
// Returns an opaque binding handle (reused by TableFnExecute across executions; freed via TableFnClose).
FabricatorHandle TableFnBind(FabricatorHandle handle, const std::string &schema, const std::string &func,
                         ArrowArrayStream *args, ArrowArrayStream &out_schema, bool &supports_pushdown);

// Execute a bound table function. `spec_json` (empty => SELECT *) + `filter_values` (nullable) carry
// projection + best-effort filter pushdown (honored only when the binding supports it). Fills `out`
// with the result rows (its stream owns the provider connection, released by the host at scan teardown).
void TableFnExecute(FabricatorHandle binding, const std::string &spec_json, ArrowArrayStream *filter_values,
                  ArrowArrayStream &out, bool *schema_may_change = nullptr);

// Release a binding handle from TableFnBind. Idempotent; safe with nullptr. Best-effort (swallows errors).
void TableFnClose(FabricatorHandle binding);

} // namespace fabricator
