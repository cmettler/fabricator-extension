// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         Fabricator — C ABI contract
//
// abi.h
//
// Shared C header describing the function-pointer vtable that the managed
// (C#) bridge fills at startup, plus the Arrow C Stream Interface structs used
// to move record batches across the C++ <-> C# boundary.
//
// Both the C++ host (clr_host.cpp) and — conceptually — the C# bridge agree on
// this layout. All tabular results flow through a caller-allocated
// ArrowArrayStream; errors are reported as a status code plus an owned UTF-8
// message string that must be released via FabricatorVTable::free_error.
//===----------------------------------------------------------------------===//

#ifndef FABRICATOR_ABI_H
#define FABRICATOR_ABI_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// -----------------------------------------------------------------------------
// Arrow C Data / Stream Interface
//
// These are the canonical definitions from the Apache Arrow specification. We
// use the exact same include guards Arrow / DuckDB / nanoarrow use, so when this
// header is included alongside DuckDB's arrow headers only one definition wins.
// -----------------------------------------------------------------------------
#ifndef ARROW_C_DATA_INTERFACE
#define ARROW_C_DATA_INTERFACE

#define ARROW_FLAG_DICTIONARY_ORDERED 1
#define ARROW_FLAG_NULLABLE 2
#define ARROW_FLAG_MAP_KEYS_SORTED 4

struct ArrowSchema {
	const char *format;
	const char *name;
	const char *metadata;
	int64_t flags;
	int64_t n_children;
	struct ArrowSchema **children;
	struct ArrowSchema *dictionary;
	void (*release)(struct ArrowSchema *);
	void *private_data;
};

struct ArrowArray {
	int64_t length;
	int64_t null_count;
	int64_t offset;
	int64_t n_buffers;
	int64_t n_children;
	const void **buffers;
	struct ArrowArray **children;
	struct ArrowArray *dictionary;
	void (*release)(struct ArrowArray *);
	void *private_data;
};

#endif // ARROW_C_DATA_INTERFACE

#ifndef ARROW_C_STREAM_INTERFACE
#define ARROW_C_STREAM_INTERFACE

struct ArrowArrayStream {
	int (*get_schema)(struct ArrowArrayStream *, struct ArrowSchema *out);
	int (*get_next)(struct ArrowArrayStream *, struct ArrowArray *out);
	const char *(*get_last_error)(struct ArrowArrayStream *);
	void (*release)(struct ArrowArrayStream *);
	void *private_data;
};

#endif // ARROW_C_STREAM_INTERFACE

// -----------------------------------------------------------------------------
// Status codes returned by vtable entries. 0 == success.
// -----------------------------------------------------------------------------
typedef enum {
	FABRICATOR_OK = 0,
	FABRICATOR_ERROR = 1,           // generic failure; see *err
	FABRICATOR_INVALID_ARGUMENT = 2,
	FABRICATOR_NOT_FOUND = 3,
	FABRICATOR_ALREADY_EXISTS = 4,  // fs_open_write(exclusive): the target already exists (a commit conflict)
} FabricatorStatus;

// Opaque handle to a managed catalog/connection (a GCHandle id on the C# side).
typedef void *FabricatorHandle;

// (FabricatorMetadataKind and its get_metadata entry were DELETED at ABI v72. Catalog discovery has
// dedicated typed entries — catalog_schemas / catalog_tables / catalog_functions / catalog_macros /
// catalog_server_info, each keeping the column layout its kind carried, minus the kind int and the
// per-provider unknown-kind fallback shapes (the 1-column empty table behind the ReadStringTable OOB
// hazard). The per-table kinds (COLUMNS/ROWID/ROWCOUNT/COLUMN_NDV/VIRTUAL_COLUMNS) live on the table_*
// session at the end of the vtable, over the managed ITable object model. Kind history, incl. the v70
// deletion of 8-11/13-14, is in docs/abi-history.md.)

// (FabricatorAlterKind, FABRICATOR_ALTER_FLAG_IF_EXISTS and the alter_table entry were DELETED at ABI
// v74. The kind int plus its four overloaded carriers — arg1/arg2/flags, each meaning something different
// per kind — became ONE typed JSON doc on the table_alter session entry at the end of this struct; the
// `column` Arrow stream stayed, because it is the TYPE CHANNEL and a doc cannot carry an Arrow extension
// type. The kind names survive as the doc's "kind" strings, listed on table_alter.)

// -----------------------------------------------------------------------------
// The vtable. The managed Bootstrap.Initialize fills this struct in place. New
// entries are appended (never reordered) so the C++ side can negotiate by size.
// -----------------------------------------------------------------------------
typedef struct FabricatorVTable {
	// ABI/struct version. Bumped when the layout changes.
	int32_t abi_version;

	// Open a backend catalog/connection for a connection string. `provider`
	// selects which registered backend handles it (case-insensitive name/alias,
	// e.g. "sqlserver"/"mssql"); NULL/empty => the default backend (single-provider
	// behaviour). On success *out_handle receives an opaque handle (thereafter every
	// call on it dispatches to that backend). On failure returns non-zero and *err
	// points to an owned UTF-8 message.
	// `options_json` carries the ATTACH options the provider owns, as a flat JSON object of strings
	// {"schema_filter":"…","table_filter":"…","isolation_level":"…", …} (NULL/empty => none). The host
	// passes every ATTACH option EXCEPT the two it must handle itself before the provider is resolved —
	// PROVIDER (selects the backend) and SECRET (resolved to a connstr) — so the provider-agnostic core
	// names no provider-specific option. The managed side parses the keys it knows (e.g. SQL Server applies
	// schema_filter/table_filter in catalog discovery and stores isolation_level for table-in-out sessions). See
	// docs/provider-extensibility.md §3.
	int32_t (*open_catalog)(const char *provider, const char *conn, const char *options_json,
	                        FabricatorHandle *out_handle, char **err);

	// Close a handle previously returned by open_catalog. Safe with NULL.
	void (*close_catalog)(FabricatorHandle handle);

	// Execute a query and export the result as an Arrow stream into *out.
	// `handle` may be NULL in Phase 0 stub mode.
	int32_t (*execute_query)(FabricatorHandle handle, const char *sql,
	                         struct ArrowArrayStream *out, char **err);

	// Release an error string previously returned through a char** out param.
	void (*free_error)(char *err);

	// Execute a non-query statement (DML/DDL); *affected receives rows affected.
	// `schema_may_change` (out, nullable): set to 1 if the statement may have changed
	// schema/catalog metadata (DDL heuristic, decided in C#) so the host can invalidate
	// its catalog cache; 0 otherwise.
	int32_t (*execute_dml)(FabricatorHandle handle, const char *sql, int64_t *affected, int32_t *schema_may_change,
	                       char **err);

	// Bulk-load an Arrow stream (produced by the host) into a table. Generic: the
	// managed side maps the Arrow schema to provider types, optionally creates the
	// table (create_table / replace), and bulk-copies. *affected = rows written.
	// The managed side takes ownership of `in` (consumes + releases it).
	int32_t (*bulk_insert)(FabricatorHandle handle, const char *schema, const char *table, int32_t create_table,
	                       int32_t replace, struct ArrowArrayStream *in, int64_t *affected, char **err);

	// rowid-based DELETE. `keys` is an Arrow stream whose columns (named by their
	// Arrow field names) are the key column values to delete. The managed side
	// generates the provider DELETE (parameterized). Takes ownership of `keys`.
	int32_t (*execute_delete)(FabricatorHandle handle, const char *schema, const char *table,
	                          struct ArrowArrayStream *keys, int64_t *affected, char **err);

	// rowid-based UPDATE. `data` is an Arrow stream with the first `set_count`
	// columns being the SET values and the remaining columns the key values
	// (all named by Arrow field name). Managed side generates the provider
	// UPDATE (parameterized). Takes ownership of `data`.
	int32_t (*execute_update)(FabricatorHandle handle, const char *schema, const char *table, int32_t set_count,
	                          struct ArrowArrayStream *data, int64_t *affected, char **err);

	// (get_metadata / scan_table were removed at ABI v72 — replaced by the dedicated catalog_* discovery
	//  entries and the table_* session at the end of this struct. Removing mid-struct slots shifts every
	//  later field, which the abi_version check makes loud — the v30/v31/v47 precedent.)

	// DDL: create a table. `columns` is a zero-row Arrow stream whose schema
	// describes the columns; a non-nullable field => NOT NULL. `if_not_exists`
	// guards creation. `pk_columns` / `unique_columns` carry key constraints as
	// 0-based field indices into `columns`: `pk_columns` is one comma-separated
	// group (e.g. "0,1", NULL/empty if none); `unique_columns` is one or more
	// ';'-separated groups of comma-separated indices (e.g. "2;3,4"). `defaults`
	// carries literal column DEFAULTs as space-separated "<index> <payload>"
	// pairs, where payload is base64(value-text) or "-" for DEFAULT NULL (NULL/
	// empty if none). The managed side maps Arrow->provider types, quotes the
	// default by column type, and runs the provider CREATE TABLE. Consumes `columns`.
	// The text-column SQL type (mssql_ctas_text_type override / mssql_default_varchar_length) is read from
	// the managed provider settings store (see docs/settings-architecture.md), not passed here.
	// `partition_columns` (nullable): comma-separated column names from a native CREATE TABLE ... PARTITIONED BY
	// clause (empty/NULL => none). Providers that don't partition (SQL Server / DAX) ignore it; the Delta provider
	// records them as the table's partition columns (data files then laid out by partition on write).
	// `sort_columns` (nullable): comma-separated column names from a native CREATE TABLE ... SORTED BY clause. The
	// SQL Server provider maps them to a Fabric Warehouse / Synapse WITH (CLUSTER BY (cols)) layout (ignored on box
	// SQL Server and by the Delta / DAX providers).
	// `identity_columns` (nullable): comma-separated column names the host detected as DuckDB GENERATED columns
	// (used as an IDENTITY marker — DuckDB has no IDENTITY concept). The SQL Server provider emits them as
	// IDENTITY (box: IDENTITY(1,1); Fabric Warehouse: bare IDENTITY, BIGINT only); Delta / DAX ignore them.
	// `options_json` (nullable, v67): the CREATE TABLE ... WITH (key='value', ...) options clause as a flat
	// JSON object of string values ({"parquet_compression":"zstd", ...}). The PROVIDER parses the keys it
	// knows (Delta: per-table write tuning + create-flag overrides + delta.*/fabricator.* properties) and
	// REJECTS unknown keys — a WITH option is never silently ignored.
	int32_t (*create_table)(FabricatorHandle handle, const char *schema, const char *table,
	                        struct ArrowArrayStream *columns, int32_t if_not_exists, const char *pk_columns,
	                        const char *unique_columns, const char *defaults, const char *partition_columns,
	                        const char *sort_columns, const char *identity_columns, const char *options_json,
	                        char **err);

	// DDL: drop a table. `if_exists` suppresses the error when it is absent.
	int32_t (*drop_table)(FabricatorHandle handle, const char *schema, const char *table, int32_t if_exists, char **err);

	// DDL: create a schema. `if_not_exists` guards creation.
	int32_t (*create_schema)(FabricatorHandle handle, const char *schema, int32_t if_not_exists, char **err);

	// DDL: drop a schema. `if_exists` suppresses the error when it is absent.
	int32_t (*drop_schema)(FabricatorHandle handle, const char *schema, int32_t if_exists, char **err);

	// (alter_table was removed at ABI v74 — replaced by table_alter at the end of this struct, over the
	//  table session's handle and one typed JSON doc.)

	// Transaction boundaries for a catalog handle. begin_transaction enters
	// transaction mode (the managed side pins a connection + provider transaction
	// lazily on the first write); commit/rollback finish it. While in transaction
	// mode all DML (execute_dml/bulk_insert/execute_delete/execute_update) runs on
	// the pinned connection so commit/rollback are atomic. Reads stay on their own
	// connections. begin on an already-open transaction is a no-op.
	// `is_explicit` (v60): 1 when the DuckDB transaction is a user BEGIN..COMMIT,
	// 0 for the implicit per-statement autocommit wrapper — a provider that buffers
	// transactional DML (the Delta provider) changes statement-visible semantics
	// only for explicit transactions; autocommit keeps the direct per-statement paths.
	int32_t (*begin_transaction)(FabricatorHandle handle, int32_t is_explicit, char **err);
	int32_t (*commit_transaction)(FabricatorHandle handle, char **err);
	int32_t (*rollback_transaction)(FabricatorHandle handle, char **err);

	// INSERT ... RETURNING. `in` is an Arrow stream of the rows to insert (its
	// field names are the target column list); the managed side runs
	// INSERT ... OUTPUT INSERTED.* and exports the inserted rows (all table
	// columns, in table order) into *out. Consumes/releases `in`.
	int32_t (*insert_returning)(FabricatorHandle handle, const char *schema, const char *table,
	                            struct ArrowArrayStream *in, struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// Streaming bulk-load (begin_bulk / push_batch / complete_bulk).
	//
	// Unlike bulk_insert (which takes a whole Arrow stream and buffers it), this
	// streams record batches one at a time so the host never materializes the
	// full dataset. The managed side runs the bulk-copy on a background task that
	// reads batches from a bounded channel; the host pushes batches as it sinks
	// chunks and backpressure blocks push_batch when the channel is full. This
	// keeps peak memory bounded for warehouse-scale writes.
	//
	// begin_bulk starts the session: `schema_in` describes the columns (the
	// pushed arrays must match it); the managed side optionally creates the table
	// (create_table / replace), maps Arrow->provider types, and starts the
	// background load. On success *out_session receives an opaque session handle.
	// The managed side CONSUMES `schema_in` (imports + releases it).
	// `check_constraints` (1/0): when set, the bulk-copy validates CHECK / FOREIGN
	// KEY constraints during load (INSERT semantics); when 0 they are skipped for
	// bulk-load speed (COPY/CTAS). SqlBulkCopy skips constraints by default.
	// `txn_id` is the DuckDB transaction id (global_transaction_id) the load belongs to, sourced at the sink
	// from `context.ActiveTransaction().global_transaction_id`. The bulk-copy runs on a background task (its
	// own thread), so unlike other calls the id can't ride the per-thread ambient (set_active_txn) — the host
	// passes it here and the managed consumer re-establishes it on its thread. It keys the per-transaction
	// provider connection, so concurrent writes (e.g. dbt --threads N building several models at once) each
	// use their OWN connection instead of colliding on one non-thread-safe SqlConnection (0 => no specific
	// transaction => a fresh connection). See docs/transaction-concurrency.md.
	// `partition_columns` (nullable): comma-separated column names from a native CREATE TABLE AS ... PARTITIONED BY
	// clause (empty/NULL => none), honored only when `create_table`/`replace` (an Append into an existing table keeps
	// its declared partitioning). Providers that don't partition ignore it; the Delta provider partitions the data.
	// `sort_columns` (nullable): comma-separated column names from a native CREATE TABLE AS ... SORTED BY clause,
	// honored only when `create_table`/`replace`. The SQL Server provider maps them to a Fabric Warehouse / Synapse
	// WITH (CLUSTER BY (cols)) layout (ignored on box SQL Server and by Delta / DAX).
	// `schema_mode` (nullable): a COPY SCHEMA_MODE option — "merge" (append + union new source columns) or
	// "overwrite" (replace data + adopt the incoming source schema). Delta-provider concept; ignored elsewhere.
	// `partition_overwrite` (1/0): the COPY PARTITION_OVERWRITE option — DYNAMIC partition overwrite (Spark's
	// partitionOverwriteMode=dynamic): the partitions PRESENT IN THE INPUT are atomically replaced (ONE Delta
	// commit removes their currently-active files + adds the new ones — a log-level swap, no physical delete, so
	// time travel keeps working and it is cloud-safe, unlike DuckDB COPY's local-only OVERWRITE); partitions the
	// input does not touch are unaffected. Append-shaped only (rejected with create_table/replace — a full
	// replace contradicts a partition-scoped one) and requires a partitioned target. Delta-provider concept;
	// providers without partition semantics REJECT it when set (silently ignoring an overwrite flag would be a
	// correctness surprise, unlike the advisory schema/sort options above).
	// `options_json` (nullable, v67): the CREATE TABLE AS ... WITH (key='value', ...) options clause as a flat
	// JSON object of string values, present only on a CTAS (a plain INSERT/COPY bulk passes NULL). Same
	// provider-parses/rejects-unknown contract as create_table's options_json.
	int32_t (*begin_bulk)(FabricatorHandle handle, const char *schema, const char *table, int32_t create_table,
	                      int32_t replace, int32_t check_constraints, int64_t txn_id, struct ArrowSchema *schema_in,
	                      const char *partition_columns, const char *sort_columns, const char *schema_mode,
	                      int32_t partition_overwrite, const char *options_json, FabricatorHandle *out_session,
	                      char **err);

	// push_batch enqueues one record batch into the session. The managed side
	// imports `batch` (taking ownership and releasing it); the caller never
	// releases it. Blocks (backpressure) while the channel is full; returns once
	// enqueued.
	int32_t (*push_batch)(FabricatorHandle session, struct ArrowArray *batch, char **err);

	// complete_bulk finishes the session: signals end-of-stream, waits for the
	// background load to drain, returns rows written in *affected, and frees the
	// session. If `abort` is non-zero the load is cancelled (errors swallowed),
	// for cleanup on a failed/cancelled query. The session handle is invalid
	// after this call.
	int32_t (*complete_bulk)(FabricatorHandle session, int32_t abort, int64_t *affected, char **err);

	// Build a provider connection string from a secret's fields. The host reads the secret's key/values
	// (DuckDB SecretManager) and passes them as a flat JSON object {"key":"value",...}; `provider` selects the
	// backend whose connstr format applies (empty => default). `secret_type` is the DuckDB type of the secret
	// the fields came from (e.g. "fabricator" = this provider's own full secret; "azure" = a FOREIGN secret
	// reused for auth) so the backend interprets the fields per type. `base_connstr` (nullable/empty) is the
	// ATTACH connection target (Server=…;Database=… or a mssql:// URI) — used when a foreign secret carries
	// only auth and the server/database must come from the ATTACH target; ignored for the provider's own full
	// secret. On success *out_connstr receives an owned UTF-8 connection string (free it via free_error). This
	// keeps all provider connstr/auth formatting in the managed backend; the result is handed to open_catalog.
	int32_t (*build_connection_string)(const char *provider, const char *secret_type, const char *fields_json,
	                                   const char *base_connstr, char **out_connstr, char **err);

	// -------------------------------------------------------------------------
	// Custom scalar functions (Phase 3). Discovered SQL Server scalar UDFs are
	// registered as DuckDB catalog scalar functions; these resolve their argument
	// + return types and execute them, all over Arrow.
	// -------------------------------------------------------------------------

	// *out receives the Arrow schema of the function's input parameters (one field per param, in order),
	// used to register the DuckDB function's arg types. A bare ArrowSchema (the managed side exports it; the
	// caller reads it then calls its release callback).
	int32_t (*get_function_param_schema)(FabricatorHandle handle, const char *schema, const char *func,
	                                     struct ArrowSchema *out, char **err);

	// *out receives the Arrow schema whose single field = the scalar function's DECLARED return type (a bare
	// ArrowSchema), read once per function when its catalog entry is materialized. A field of Arrow `null`
	// type is the UNRESOLVED sentinel: the function declares no fixed return type and scalarfn_bind must
	// supply one per call site. (Same sentinel/meaning as a SQLNULL *parameter*, which registers as ANY.)
	// The volatility signal rides this field's metadata (fabricator.volatile = "0" => CONSISTENT).
	int32_t (*get_function_return_schema)(FabricatorHandle handle, const char *schema, const char *func,
	                                      struct ArrowSchema *out, char **err);

	// -------------------------------------------------------------------------
	// Scalar-function session (ABI v80). The session-handle successor to the
	// stateless execute_scalar (removed at v80): scalarfn_bind resolves a per-CALL-SITE
	// binding (result field + any bind state) once; scalarfn_execute reuses that binding
	// for every chunk. Mirrors tablefn_bind / tablefn_execute / tablefn_close, and exists
	// for the same two reasons: a result type that depends on the call's constant
	// arguments, and somewhere to park work done once at bind instead of per chunk.
	// -------------------------------------------------------------------------

	// Bind one scalar-function call site. `args` (nullable) is a 1-row Arrow stream of the call's arguments,
	// in param order, consumed by the managed side.
	//
	// ⚠ UNLIKE tablefn_bind, THE VALUES ARE PARTIAL. A table function's arguments must be constant; a
	// scalar's need not be (`f(t.col)` is legal), and DuckDB hands the bind callback argument EXPRESSIONS
	// rather than values. `arg_constant` is a MASK — one char per argument, '1' = a folded constant whose
	// value is real, '0' = a runtime expression whose slot holds a NULL PLACEHOLDER (nullable/empty => treat
	// every argument as runtime). A '0' placeholder is NOT the same as a '1' slot holding an explicit NULL
	// literal, and a provider that reads a value without consulting the mask will read a placeholder as data.
	// (The mask is a separate parameter rather than field metadata on `args` deliberately: metadata would
	// have to out-live the exported schema, which is the Arrow-lifetime hazard this codebase has already
	// paid for once, and a field may already carry an extension-type marker there.)
	//
	// ⚠ THE VALUES ARE ALSO PRE-CAST. DuckDB runs CastToFunctionArguments AFTER the bind callback, so a
	// literal 1.0 bound to a declared INTEGER parameter arrives here as DOUBLE and arrives at
	// scalarfn_execute as INTEGER. Bind values are for DECIDING; the execute batch is the authoritative
	// typed view for COMPUTING.
	//
	// *out_schema receives a bare ArrowSchema whose single field is the resolved result — the same carrier
	// get_function_return_schema uses (NOT tablefn_bind's zero-row stream: a scalar needs one field, and the
	// stream form would drag in PopulateReturnSchema, which SETS THE AMBIENT HOST-FS OPENER as a side effect
	// and so cannot run inside a statement that is already doing host-FS IO — measured, see docs/abi-history.md
	// §v80). A field of Arrow `null` type here is the SAME UNRESOLVED SENTINEL as on the declared side, read
	// in the other direction: "my result IS the declared type", which the host already holds, so a
	// fixed-return function costs no work at bind. If BOTH are unresolved the call is refused by name at bind.
	// *out_binding receives an opaque binding handle, reused by scalarfn_execute for every chunk and freed
	// via scalarfn_close.
	//
	// ⚠⚠ `opener` / `session` / `txn_id` (ABI v82) are the CALLER's context, and they are parameters rather
	// than a preceding set_active_opener BECAUSE THE MANAGED SIDE MUST RESTORE THEM. A scalar is evaluated
	// wherever it is CALLED — including inside a nested host query an OUTER operation is running while that
	// operation holds the ambient — so assigning without restoring leaves the outer operation resolving a
	// ClientContext that is gone (measured as a SIGSEGV at OPTIMIZE, docs/abi-history.md §v80). The ambients
	// are AsyncLocals the host can only overwrite, so only the managed handler can put back what it found;
	// see CallScope. `opener` may be 0 where the host has no context to give.
	int32_t (*scalarfn_bind)(FabricatorHandle handle, const char *schema, const char *func,
	                         struct ArrowArrayStream *args, const char *arg_constant,
	                         FabricatorHandle opener, int64_t session, int64_t txn_id,
	                         struct ArrowSchema *out_schema, FabricatorHandle *out_binding, char **err);

	// Execute a bound scalar function over one chunk: `args` is an N-row stream whose columns are the
	// argument values (in param order, post-cast); *out receives an N-row stream with a single column = the
	// per-row results, typed as the field the binding reported. The managed side consumes `args`.
	//
	// The constant arguments are deliberately REPEATED here rather than being read off the binding: which
	// arguments are constant is a property of the CALL SITE, so omitting them would make column i stop
	// meaning parameter i, differently per call site. Uniformity keeps the param schema the single
	// positional contract. (Cost: a constant column is materialized as N values per chunk — Arrow has no
	// constant encoding.)
	//
	// ⚠ `opener` / `session` / `txn_id` are the caller's context for THIS chunk, on the same terms as
	// scalarfn_bind above: repeated per call rather than captured on the binding, because a binding is
	// reused across chunks and — for a prepared statement — across transactions, so the context it was bound
	// under need not be the context it is executed under.
	int32_t (*scalarfn_execute)(FabricatorHandle binding, struct ArrowArrayStream *args,
	                            FabricatorHandle opener, int64_t session, int64_t txn_id,
	                            struct ArrowArrayStream *out, char **err);

	// Release a binding handle from scalarfn_bind. Idempotent; safe with nullptr. Best-effort
	// (bind-data teardown must not throw).
	int32_t (*scalarfn_close)(FabricatorHandle binding, char **err);

	// *out receives the Arrow schema of a table-returning function's output columns (a bare ArrowSchema).
	// `args` (nullable) is a 1-row Arrow STREAM of the constant call arguments (in param order; consumed by
	// the managed side when present) — a custom table function's output schema MAY depend on them (the managed
	// side binds the call and returns the bound output schema); discovered SQL TVFs/procs read it from metadata
	// and ignore `args`. Pass NULL for `args` when there are none (e.g. the in-out `_each` base-schema lookup).
	int32_t (*get_function_output_schema)(FabricatorHandle handle, const char *schema, const char *func,
	                                      struct ArrowArrayStream *args, struct ArrowSchema *out, char **err);

	// (execute_table / execute_proc were removed at ABI v30 — superseded by the table-function session
	//  tablefn_bind / tablefn_execute / tablefn_close at the end of this struct.)

	// (The 4g table-in-out PUSH entries inout_open/inout_push/inout_finish/inout_abort were removed at ABI
	//  v31 — every `_each` form now runs on the streaming exchange below: inout_bind/inout_exchange_open/
	//  inout_bind_close.)

	// -------------------------------------------------------------------------
	// Custom aggregate functions (Phase 4h, C#-authored UDAF). DuckDB owns a
	// contiguous array of fixed-size state blobs (each blob = an int64 id); the
	// real per-group accumulator lives in C# behind that id. One managed session
	// per bound aggregate (a Dictionary<id, accumulator>); the C++ aggregate
	// callbacks (initialize/update/simple_update/combine/finalize/destructor)
	// marshal the id(s) + input columns over these entries. Argument + return
	// schemas reuse get_function_param_schema / get_function_return_schema (the
	// custom registry routes them).
	// -------------------------------------------------------------------------

	// Open a managed aggregate session for (schema, func). On success *out_session
	// receives an opaque handle (a fresh Dictionary<id, accumulator>). Closed via
	// agg_close when the bound plan is torn down.
	int32_t (*agg_open)(FabricatorHandle handle, const char *schema, const char *func, FabricatorHandle *out_session,
	                    char **err);

	// Update: `batch` is an N-row Arrow array whose column 0 is an int64 "state_id"
	// and columns 1.. are the argument values (in param order). The managed side
	// groups rows by id, get-or-creates each accumulator, and applies the per-group
	// rows. Consumes/releases `batch`. (simple_update reuses this with a constant id.)
	int32_t (*agg_update)(FabricatorHandle session, struct ArrowArray *batch, char **err);

	// Combine: `batch` is an N-row Arrow array of two int64 columns
	// [target_id, source_id]; the managed side merges each source accumulator into
	// its target (absent source => empty, skipped). Consumes/releases `batch`.
	int32_t (*agg_combine)(FabricatorHandle session, struct ArrowArray *batch, char **err);

	// Finalize: `ids` is an N-row Arrow array of a single int64 "state_id" column;
	// *out receives an N-row stream with one column = each group's result, in the
	// SAME ORDER as `ids` (an absent id => a fresh accumulator => the empty-group
	// value). Consumes/releases `ids`.
	int32_t (*agg_finalize)(FabricatorHandle session, struct ArrowArray *ids, struct ArrowArrayStream *out,
	                        char **err);

	// Destroy: `ids` is an N-row Arrow array of a single int64 "state_id" column;
	// the managed side drops those accumulators (bounds memory for the window paths
	// that churn transient states). Best-effort (a destructor must not throw).
	// Consumes/releases `ids`.
	int32_t (*agg_destroy)(FabricatorHandle session, struct ArrowArray *ids, char **err);

	// Release the session (frees the dictionary + GCHandle). Idempotent. Safe with
	// nullptr. Best-effort (teardown must not throw).
	int32_t (*agg_close)(FabricatorHandle session, char **err);

	// -------------------------------------------------------------------------
	// Spillable aggregates (Phase 4h opt-in, `IArrowAggregateFunction.SupportsSpill`).
	// For these the per-group accumulator is serialized into the fixed-size,
	// pointer-free state blob (`[uint32 len][byte data[FABRICATOR_AGG_SPILL_CAP]]`),
	// so DuckDB's external GROUP BY can spill it to disk. Each call round-trips the
	// state bytes <-> the C# accumulator (no persistent C# state). State is carried
	// as an Arrow BLOB column; a NULL row = a fresh/empty group. Serialized state
	// must fit FABRICATOR_AGG_SPILL_CAP bytes.
	// -------------------------------------------------------------------------

	// Update: `group_states` = a BLOB column, one row per distinct group in this chunk
	// (its current serialized state; NULL = fresh); `batch` = `[int64 slot ++ params]`,
	// N rows (slot indexes into group_states). *out = a BLOB column of the new serialized
	// state per group, SAME ORDER as group_states. Consumes both input arrays.
	int32_t (*agg_update_spill)(FabricatorHandle session, struct ArrowArray *group_states, struct ArrowArray *batch,
	                            struct ArrowArrayStream *out, char **err);

	// Combine: `target_states` = a BLOB column, one row per distinct TARGET group (NULL = fresh); `batch` =
	// `[int64 slot, BLOB source]`, N rows (slot indexes into target_states; source = the partial state to
	// merge in — a target may repeat across rows, e.g. the window segment-tree merges several nodes into one
	// frame state). *out = a BLOB column of the merged state per target, SAME ORDER as target_states.
	// Consumes both input arrays.
	int32_t (*agg_combine_spill)(FabricatorHandle session, struct ArrowArray *target_states, struct ArrowArray *batch,
	                             struct ArrowArrayStream *out, char **err);

	// Finalize: `states` = a BLOB column, N rows (NULL = fresh/empty). *out = one result
	// column, N rows, SAME ORDER. Consumes `states`.
	int32_t (*agg_finalize_spill)(FabricatorHandle session, struct ArrowArray *states, struct ArrowArrayStream *out,
	                              char **err);

	// -------------------------------------------------------------------------
	// Streaming table-in-out exchange (Phase 6). The in-out path for EVERY `_each` form —
	// discovered TVFs (CROSS APPLY), stored procs (per-row EXEC on DuckDB's pinned write txn),
	// and custom C#-authored in-out functions (the 4g push entries it replaced were removed at
	// v31). Output streams via two pull-based Arrow streams coordinated by a C++ "gate" mutex —
	// at most one input
	// array + one output array in flight (no per-chunk materialization). One SQL connection +
	// transaction for the whole call (consistent snapshot); the host's injected
	// OperatorFinalize is the single EOF signal.
	// -------------------------------------------------------------------------

	// Bind one in-out call. `args` (nullable) is a 1-row Arrow stream of the constant "cost"
	// arguments (consumed by the managed side); `input_schema` is the Arrow schema of the input
	// table (consumed/released). *out_schema receives a zero-row Arrow stream whose schema = the
	// binding's FULL output columns — the input-column echo (typed as the function's parameters)
	// ++ the function's own output columns, computed in C# from the args + input schema — read it
	// for the DuckDB return types, then release it. *out_binding receives an opaque binding handle
	// (reused by inout_exchange_open across prepared re-executions; freed via inout_bind_close).
	int32_t (*inout_bind)(FabricatorHandle handle, const char *schema, const char *func,
	                      struct ArrowArrayStream *args, struct ArrowSchema *input_schema,
	                      struct ArrowArrayStream *out_schema, FabricatorHandle *out_binding, char **err);

	// Open one execution exchange on a bound binding. `input` is an Arrow stream the HOST has
	// populated (host exports; its get_next yields one input chunk per gate tenure, a released/null
	// array at end) — the managed side IMPORTS it (takes ownership, pulls, releases). *output receives the
	// managed OUTPUT stream (the managed side exports into it; the host pulls it — non-empty batch =
	// HAVE_MORE_OUTPUT, length-0 batch = NEED_MORE_INPUT, released/null array = FINISHED — and releases it).
	// The connection opens lazily on first input pull. One binding may open at most one exchange at a time.
	// The SQL isolation for the read transaction is resolved + set on the binding in C# at inout_bind (SET
	// mssql_isolation_level ?? the catalog's ATTACH isolation_level — both C#-owned), so it is not passed here.
	int32_t (*inout_exchange_open)(FabricatorHandle binding, struct ArrowArrayStream *input,
	                               struct ArrowArrayStream *output, char **err);

	// Release a binding handle from inout_bind. Idempotent; safe with nullptr. Best-effort
	// (bind-data teardown must not throw).
	int32_t (*inout_bind_close)(FabricatorHandle binding, char **err);

	// -------------------------------------------------------------------------
	// Table-function session (Phase 5). The session-handle successor to the
	// stateless execute_table / execute_proc (removed at v30): tablefn_bind resolves
	// a per-PLAN binding (output schema + whether it accepts pushdown) once; that
	// binding is reused by tablefn_execute for each execution (the result stream owns
	// its own provider connection, released by the host's arrow scan at teardown —
	// no separate close). Unifies discovered TVFs (pushdown), stored procs (no
	// pushdown) and custom C# table functions behind one path; the managed side
	// classifies the function (so the host no longer needs the is_proc distinction
	// at bind).
	// -------------------------------------------------------------------------

	// Bind one table-function call. `args` (nullable) is a 1-row Arrow stream of the
	// constant call arguments (consumed by the managed side). *out_schema receives a
	// zero-row Arrow stream whose schema = the function's output columns (a custom
	// function's MAY depend on `args`) — read it for the DuckDB return types, then
	// release it. *supports_pushdown drives the host's projection mapping — the managed side
	// calls it ITableFunctionSession.MapResultByName, which is what it has always meant (renamed there
	// 2026-08-13; the ABI parameter keeps its name so no rebuild is forced for a spelling).
	// 1 = map result
	// columns by NAME — a discovered TVF (pushes the projection + filter into the SELECT)
	// or a custom function (returns its full result, mapped by name); 0 = a stored proc
	// (full result, projected positionally + filtered above the scan). *out_binding
	// receives an opaque binding handle, reused by tablefn_execute across (prepared)
	// re-executions and freed via tablefn_close.
	int32_t (*tablefn_bind)(FabricatorHandle handle, const char *schema, const char *func,
	                      struct ArrowArrayStream *args, struct ArrowArrayStream *out_schema,
	                      int32_t *supports_pushdown, FabricatorHandle *out_binding, char **err);

	// Execute a bound table function. `spec_json` (nullable/empty => SELECT *) +
	// `filter_values` (nullable) carry projection + best-effort filter pushdown,
	// honored only when the binding reported supports_pushdown (else ignored — DuckDB
	// re-applies above the scan). *out receives the result rows (its stream owns the
	// provider connection, released by the host at scan teardown). Called once per
	// execution; the binding may be executed repeatedly.
	// *schema_may_change (nullable) reports whether this EXECUTION changed the provider's catalog — a
	// provider-authored function that performs DDL (the db.cdc.* enable/disable pair) sets it, and the host
	// then rebuilds its metadata cache. Mirrors execute_dml's out-param of the same name; before ABI v81 a
	// table function had no way to say this, so its own new objects stayed unreachable for the session.
	//
	// ⚠⚠ THE FLAG IS READ WHEN THIS CALL RETURNS, NOT WHEN THE STREAM IS DRAINED. A managed binding whose
	// side effect lives in an async-iterator body has not run it yet at that moment — an iterator does not
	// begin until the first batch PULL, a different crossing. So a function that reports through this flag
	// MUST do its work in the eager part of Execute(). Same rule, and the same failure mode, as the
	// ambient-capture bug recorded for global table functions.
	//
	// ⚠ The host must NOT act on it synchronously here: it is set during a SCAN, and rebuilding the catalog
	// at that moment retires the very entry the running statement is scanning. The host records it and
	// refreshes at the next transaction start instead (fabricator_transaction.cpp).
	int32_t (*tablefn_execute)(FabricatorHandle binding, const char *spec_json,
	                         struct ArrowArrayStream *filter_values, struct ArrowArrayStream *out,
	                         int32_t *schema_may_change, char **err);

	// Release a binding handle from tablefn_bind. Idempotent; safe with nullptr.
	// Best-effort (bind-data teardown must not throw).
	int32_t (*tablefn_close)(FabricatorHandle binding, char **err);

	// -------------------------------------------------------------------------
	// Provider-declared settings (Phase: settings refactor; see docs/settings-architecture.md).
	// Appended at the vtable end so no earlier slot shifts.
	// -------------------------------------------------------------------------
	// list_settings: the managed side returns ALL registered providers' declared settings as an Arrow
	// stream with six UTF-8 columns: provider, name, type ("bool"|"long"|"varchar"), default (rendered;
	// empty => unset), description, min (rendered int64 for long settings; empty => none). The host
	// registers each as a DuckDB extension option at extension load.
	int32_t (*list_settings)(struct ArrowArrayStream *out, char **err);

	// set_setting: push a setting's new value (rendered UTF-8; NULL/empty => unset/reset) into the managed
	// ProviderSettingsStore. Called from each option's set-callback when the value is SET, and once per
	// setting at registration for its default.
	//
	// `session` scopes the write, honouring DuckDB's SetScope (ABI v69): 0 = the GLOBAL layer (a `SET GLOBAL`,
	// and every registration default), non-zero = the SESSION layer, keyed by the setting connection's
	// ClientContext address (fabricator::SessionKeyFor). A read resolves session-then-global, so a
	// session-scoped value shadows the global one for that DuckDB connection ALONE.
	//
	// ⚠ Before v69 there was no session key and every SET was process-wide. That was not a missing nicety:
	// MEASURED, `SET mssql_mars='false'` in one connection made a same-catalog CTAS in ANOTHER connection —
	// which set nothing — return 10 rows instead of 15. A setting applied in one connection changed the DATA
	// another connection saw. DuckDB registers our options with default_scope = SESSION and already stores
	// the value per-connection on its side; only this push was global.
	int32_t (*set_setting)(int64_t session, const char *provider, const char *name, const char *value, char **err);

	// -------------------------------------------------------------------------
	// Per-transaction connection routing (write-concurrency fix; see
	// docs/transaction-concurrency.md). Appended at the vtable end.
	// -------------------------------------------------------------------------
	// set_active_txn: record the DuckDB transaction id (`global_transaction_id`) currently in effect, so the
	// NEXT connection-using call keys its per-transaction provider connection by it. The managed side stores
	// it in a per-thread ambient; the host calls this IMMEDIATELY before each connection-using call, on the
	// SAME thread (the calls are synchronous). `txn_id` 0 => no specific transaction (a fresh/pooled
	// connection). This makes concurrent DuckDB transactions (e.g. dbt --threads N) each use their OWN
	// provider connection instead of colliding on one shared, non-thread-safe connection. `handle` is unused
	// (the ambient is per-thread + global; each catalog keys its own connection-state map by the id).
	//
	// `join_only` (1/0): set ONLY by the raw `fabricator_exec` passthrough. When 1, the following write JOINS
	// the active transaction's pinned connection if one already exists (a DuckDB-managed write is in flight in
	// this transaction) — so the exec is atomic with the transaction and sees its uncommitted writes — else it
	// autocommits on a fresh connection WITHOUT creating persistent transaction state (a raw exec's target
	// never triggers DuckDB's transaction lifecycle, so nothing would ever commit a pinned connection). Normal
	// DuckDB-managed writes pass 0 (they create + own the per-transaction connection). See docs/dbt-hooks.md.
	int32_t (*set_active_txn)(FabricatorHandle handle, int64_t txn_id, int32_t join_only, char **err);

	// -------------------------------------------------------------------------
	// Provider-declared secret fields (see docs/provider-extensibility.md §2). Appended at the vtable end.
	// -------------------------------------------------------------------------
	// list_secret_fields: the managed side returns ALL registered providers' secret types + fields as an Arrow
	// stream with five UTF-8 columns: provider, secret_type, name, type ("varchar"|"integer"|"boolean"),
	// redact ("1"|"0"). The host registers one DuckDB secret type per distinct secret_type at extension load,
	// with the listed fields as the CREATE SECRET named parameters (redacting the marked ones) — so the
	// provider-agnostic core names no secret type or field. A provider with no secret type contributes no rows.
	int32_t (*list_secret_fields)(struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// SPIKE — filesystem reverse-callback foundation (a managed lakehouse reader doing secret-backed remote
	// IO via DuckDB's FileSystem). `opener` is the opaque host FileOpener handle for the calling operator's
	// ClientContext (carries secret resolution); the managed side opens `path` via the FabricatorHostServices
	// callbacks, reads its head + tail bytes, and returns a short human-readable result in *out (owned UTF-8,
	// freed via free_error). Proves C#->host FileSystem reads + opener/secret threading work end-to-end.
	// -------------------------------------------------------------------------
	int32_t (*fs_spike)(FabricatorHandle opener, const char *path, char **out, char **err);

	// (delta_schema / delta_scan were removed at ABI v47 — the Delta reader is now a connection-free GLOBAL
	//  host-FS table function (kind='table' enumerated by list_global_functions), dispatched through the v29
	//  table-session path (tablefn_bind / tablefn_execute) with the active host-FS opener set via set_active_opener
	//  below. So a managed lakehouse reader needs NO bespoke C++/ABI — see docs/global-functions.md §host-FS.)

	// -------------------------------------------------------------------------
	// Ambient named-source registry (data-in by name). A managed component registers `name -> a fresh Arrow
	// stream factory`; fabricator_scan(name) + the replacement scan resolve a referenced name to that stream.
	// -------------------------------------------------------------------------
	// open_named_input: fill *out with a FRESH Arrow stream for the registered source `name`. Errors (non-zero
	// + *err) if no source is registered under that name. Each call produces a fresh stream (the registry
	// holds a factory), so bind + execute can each open one.
	int32_t (*open_named_input)(const char *name, struct ArrowArrayStream *out, char **err);
	// named_input_exists: set *out_exists to 1 if a source is registered under `name`, else 0 (no stream
	// produced). Used by the replacement scan to decide whether to rewrite a bare table name.
	int32_t (*named_input_exists)(const char *name, int32_t *out_exists, char **err);

	// -------------------------------------------------------------------------
	// Load-time GLOBAL functions (connection-free; no ATTACH). The host calls this ONCE at extension load to
	// enumerate the provider-union of global functions, then registers each as a bare `fn(...)` via
	// loader.RegisterFunction. Metadata rows: {name VARCHAR, kind VARCHAR, param_count INT, return_type VARCHAR}
	// (same shape as the catalog functions metadata; return_type is meaningful for kind='scalar', empty
	// otherwise). For each, the host fetches the precise Arrow param/return schema via the existing
	// get_function_param_schema / get_function_return_schema entries with HANDLE = 0 (the global marker; C#
	// routes a 0 handle to the global registry by name), and binds/dispatches execution via scalarfn_bind
	// with handle = 0. So global SCALAR functions add NO execution/schema ABI — only this enumeration entry.
	int32_t (*list_global_functions)(struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// Active host-FS opener (for connection-free GLOBAL host-FS table functions — lakehouse readers like
	// Delta/Iceberg). A global host-FS table reader does its IO through DuckDB's FileSystem (the
	// FabricatorHostServices fs_* callbacks), which needs the calling operator's FileOpener/ClientContext to
	// resolve DuckDB secrets (az://, s3://, …). That context isn't an argument of the generic tablefn_bind /
	// tablefn_execute path, so — mirroring set_active_txn — the host records it in a per-thread ambient
	// IMMEDIATELY before each table-function bind + execution (same thread, synchronous), and the managed
	// host-FS binding reads it. `opener` is the operator's ClientContext (reinterpret_cast to a handle), valid
	// only for the duration of the call it precedes; NULL clears it. SQL/compute table functions ignore it.
	// Best-effort (a failure to set the ambient must not abort the statement). See docs/global-functions.md.
	//
	// `session` (ABI v69) records, in a SECOND ambient, which DuckDB connection's session-scoped provider
	// settings apply — see set_setting. It rides this entry because the two are set at the same moments and
	// setting them together is what stops them drifting.
	//
	// ⚠ THEY ARE NOT THE SAME VALUE, and the one place they differ is the reason this is a parameter rather
	// than something the managed side derives from `opener`. The commit flush and the rollback path
	// deliberately open their OWN short-lived connection and pass ITS context as the opener (the user's
	// transaction is ending), while the settings that govern that flush — delta_write_options,
	// copy_into_staging, the parquet tuning — were SET on the USER's connection. Deriving the session from
	// the opener there would resolve against a connection that has set nothing, silently writing at the
	// engine defaults.
	int32_t (*set_active_opener)(FabricatorHandle opener, int64_t session, char **err);

	// -------------------------------------------------------------------------
	// onelake:// FileSystem forward callbacks (Phase-3 filesystem subsystem). A C++ FileSystem registered in
	// DuckDB's VFS for the `onelake://` scheme forwards its read ops HERE, to the managed Azure DataLake SDK
	// (bypassing duckdb-azure's OneLake gaps). This is the FORWARD direction of the fs_* host services (which
	// go managed→host); here the host's onelake FS goes host→managed. The credential rides as `cred_json` — the
	// fields of the azure secret the host resolved from the calling opener (empty/"{}" ⇒ DefaultAzureCredential);
	// the managed side builds a TokenCredential via FabricCredentialResolver. Because the onelake FS is behind
	// the VFS, DuckDB's native readers + ExternalFileCache use it uniformly (see docs/filesystem-bridge.md §3).
	// Read-only surface for now (write ops on the C++ FS throw NotImplemented). *out_file = an opaque managed
	// file handle (close via onelake_close); *out_size = the file length (read at open, cached C++-side).
	// `known_size` >= 0 (from a listing's extended info) skips the per-file GetProperties round trip (v62);
	// -1 = fetch the length at open. When the managed side DOES fetch properties (v63), it also returns the
	// file's `out_etag` (owned UTF-8, freed via free_error; null when unknown) and `out_modified_ms`
	// (epoch ms; -1 unknown) — the cache-validation identity the host surfaces via GetVersionTag /
	// GetLastModifiedTime so DuckDB's external file cache detects in-place overwrites (VALIDATE_ALL default).
	int32_t (*onelake_open)(const char *path, const char *cred_json, int64_t known_size, FabricatorHandle *out_file,
	                        int64_t *out_size, char **out_etag, int64_t *out_modified_ms,
	                        char **err);
	// Read nr_bytes at absolute offset `location` into buffer (host-owned). The managed side does the range GET.
	int32_t (*onelake_read)(FabricatorHandle file, void *buffer, int64_t nr_bytes, int64_t location, char **err);
	// Close a handle from onelake_open. Safe with NULL/0.
	void (*onelake_close)(FabricatorHandle file);
	// Glob `pattern` (an onelake:// path, possibly with wildcards) → JSON array of {path,size}. cred_json as above.
	int32_t (*onelake_glob)(const char *pattern, const char *cred_json, char **out_json, char **err);
	// FileExists(`path`). *out_exists = 0/1. cred_json as above.
	int32_t (*onelake_exists)(const char *path, const char *cred_json, int32_t *out_exists, char **err);

	// onelake:// WRITE (Phase-3 slice 2): a plain file write to OneLake through any DuckDB writer
	// (COPY … TO 'onelake://…', etc.) — NOT a Delta commit. Sequential (append-only), which is what
	// COPY and Azure DFS both do. onelake_open_write creates/overwrites the file; onelake_write appends;
	// onelake_close_write flushes + commits at the final length. cred_json as above.
	// `exclusive` != 0 => put-if-absent (ADLS conditional create, If-None-Match:*) — the atomic commit
	// primitive EXCLUSIVE_CREATE maps to (v61); an existing target fails the create (the host probes
	// existence to classify the conflict).
	int32_t (*onelake_open_write)(const char *path, const char *cred_json, int32_t exclusive,
	                              FabricatorHandle *out_file, char **err);
	int32_t (*onelake_write)(FabricatorHandle file, const void *buffer, int64_t nr_bytes, char **err);
	int32_t (*onelake_close_write)(FabricatorHandle file, char **err);

	// -------------------------------------------------------------------------
	// `delta_list_files` lived here from v57 until it was DELETED at v75 together with
	// `fabricator_delta_mfr_scan`, its only caller (a C++ MultiFileReader spike that shipped registered but
	// undocumented; the production Delta read path is the managed DeltaNativeReader, which builds its own
	// read_parquet SQL and never crossed here). Removing a MID-STRUCT slot shifts every later field — the
	// v30/v31/v47/v72 precedent — so the version bump is what makes a mismatched pair loud at boot.

	// Delete a single onelake:// FILE (DataLakeFileClient.DeleteIfExists — idempotent). Appended at v61 so the
	// onelake:// FileSystem supports RemoveFile: engineered-wood's commit rename is emulated as
	// exclusive-create-copy + DELETE-SOURCE, so a Delta write over onelake:// (fabricator_delta_write) needs it.
	// cred_json as above.
	int32_t (*onelake_remove)(const char *path, const char *cred_json, char **err);
	// Atomic single-file rename via the ADLS Gen2 DFS native rename (a metadata op, not a copy;
	// overwrites an existing destination — MoveFile semantics). Appended at v64 so DuckDB's COPY
	// tmp-file staging (`<file>.tmp` -> target, taken because onelake:// classifies as LOCAL in the
	// hardcoded remote-prefix list) works on onelake://. dest is a full onelake:// path in the SAME
	// workspace filesystem; cred_json as above.
	int32_t (*onelake_move)(const char *src, const char *dest, const char *cred_json, char **err);

	// -------------------------------------------------------------------------
	// SQL-GENERATING table functions (ABI v68; docs/macros-and-sqlgen-functions.md §2). Generate the
	// replacement SQL for one call of a `table_sql` function — the managed side of DuckDB's bind_replace
	// mechanism (what query_table() uses): the host parses the returned statement and SUBSTITUTES it for the
	// function call in the plan, so NO data crosses this ABI at execution and the SQL binds as a native plan
	// (keeping full pushdown into whatever it references, including this extension's own catalog scans).
	//   handle == 0  => resolve `func` against the GLOBAL registry (`schema` empty, `catalog_name` empty);
	//   handle != 0  => the catalog's registry (`schema`.`func`), with `catalog_name` = the DuckDB ATTACH
	//                   alias so the generator can emit qualified references back into its own catalog.
	// `args` is a 1-row batch of the CONSTANT call arguments — positional first (declared order), then the
	// SUPPLIED named parameters identified by field name; nullable when the function takes none (consumed
	// either way). *out_sql = an owned UTF-8 statement, freed by the host via free_error. Called at BIND
	// time only, possibly repeatedly (EXPLAIN / DESCRIBE / a view re-bind), never during execution.
	int32_t (*generate_table_sql)(FabricatorHandle handle, const char *schema, const char *func,
	                              const char *catalog_name, struct ArrowArrayStream *args, char **out_sql,
	                              char **err);

	// -------------------------------------------------------------------------
	// Session-scoped provider settings (ABI v69; see set_setting). Appended at the vtable end so no earlier
	// slot shifts.
	// -------------------------------------------------------------------------
	// clear_session_settings: drop every session-scoped value for `session`. The host calls this when the
	// owning DuckDB connection closes (from a ClientContextState destructor, so it must never throw).
	//
	// ⚠ NOT HOUSEKEEPING — the session key is a ClientContext ADDRESS, so an entry left behind can be
	// INHERITED by a later connection the allocator happens to place at the same address. That is a silent
	// wrong answer, and it surfaces only under connection churn (a dbt run), where it is hardest to
	// attribute. Must be idempotent and cheap for a session that never set anything: the host arms the
	// cleanup once per connection, not once per setting.
	int32_t (*clear_session_settings)(int64_t session, char **err);

	// -------------------------------------------------------------------------
	// Catalog capability doc (ABI v71). Appended at the vtable end so no earlier slot shifts.
	// -------------------------------------------------------------------------
	// get_capabilities: ONE flat JSON object of the capability flags the HOST consumes for this catalog,
	// read once at ATTACH (from LoadCatalog, with the txn/opener ambient already established). It replaces
	// the old pattern of grepping the diagnostic get_metadata kind-7 (property, value) stream for
	// "exact_filter_pushdown" / "is_binary_collation" — a display surface string-matched on both sides.
	// Kind 7 STAYS, but as a diagnostic only (the fabricator_server_info() table function); the host never
	// reads it again.
	//
	// ⚠ Deliberately NOT part of open_catalog's result, although the design doc first sketched it there:
	// open_catalog must stay connection-free (measured — see the mutant note in fabricator_storage.cpp),
	// while a provider may need a CONNECTION to answer (SQL Server detects the database collation). At
	// LoadCatalog time the session/opener ambients are established and the first connection was always paid
	// on this path anyway (the old FetchBinaryCollation triggered profile detection).
	//
	// Contract: a flat JSON object whose values are booleans; an ABSENT key means false (each provider
	// emits only the flags it can assert). Keys the host reads today:
	//   "exact_filter_pushdown"  — pushed table filters are applied EXACTLY (never a superset) => the scan
	//                              may advertise filter_pushdown=true so DuckDB delivers dynamic/join
	//                              filters (the Delta catalog in Exact pushdown mode).
	//   "is_binary_collation"    — the database collation sorts strings by byte value == DuckDB's order =>
	//                              string-keyed TopN (ORDER BY+LIMIT) may be pushed (SQL Server _BIN/_BIN2).
	// *out_json is an owned UTF-8 string freed via free_error (the build_connection_string convention).
	// Best-effort on the host side: any failure leaves every capability off (the safe defaults).
	int32_t (*get_capabilities)(FabricatorHandle handle, char **out_json, char **err);

	// -------------------------------------------------------------------------
	// catalog_init (ABI v78) — the provider's ONE chance to initialise with a live client context, called
	// from LoadCatalog immediately after the ambients are established and BEFORE any discovery crossing.
	// -------------------------------------------------------------------------
	// Optional (a DIM no-op on IProviderCatalog): a provider that needs nothing implements nothing.
	//
	// ⚠ WHY IT EXISTS. open_catalog runs with NO ambients — no opener, no settings session — because it only
	// CONSTRUCTS (that invariant is what makes the missing ambient safe; see fabricator_storage.cpp). So a
	// provider with context-requiring setup had nowhere to put it and had to hang it off whichever discovery
	// crossing happened to run first — and the ORDER of those is not part of the contract. In practice
	// get_capabilities became the de-facto init hook by accident of being first, which is how SQL Server's
	// first CONNECT ended up inside a call documented as reading a doc of booleans.
	//
	// ⚠ DELIBERATELY NOT wrapped in a swallowing catch, unlike get_capabilities below it: an init failure is
	// the provider saying it cannot serve this catalog, so it must fail the ATTACH with its own message
	// rather than degrade to defaults.
	int32_t (*catalog_init)(FabricatorHandle handle, char **err);

	// -------------------------------------------------------------------------
	// Catalog discovery (ABI v72) — the dedicated typed LIST entries that replaced get_metadata's 16-kind
	// multiplexer (docs/catalog-table-abstraction.md §2.4). Arrow streams stay the carrier — the right tool
	// for lists — with each entry keeping the column layout its old kind carried; what died is the kind int,
	// the per-provider unknown-kind fallback shapes, and (below) the name-pair-per-call table transport.
	// All UTF-8 columns, read host-side with ReadStringTable.
	// -------------------------------------------------------------------------
	// One column: the user schema names (schema_filter applied provider-side; a Delta/Fabric catalog also
	// advertises its function namespaces here — `delta`, `fabric`).
	int32_t (*catalog_schemas)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);
	// Three columns: schema, table, type ("BASE TABLE"|"VIEW"). schema_filter/table_filter applied
	// provider-side (they bound ENUMERATION only, never targeted access).
	int32_t (*catalog_tables)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);
	// Five columns: schema, name, kind, param_count, return_type (the host reads the first three).
	int32_t (*catalog_functions)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);
	// Three columns: schema, name, create_sql — provider-declared CATALOG-BOUND DuckDB macros, each
	// create_sql one complete CREATE MACRO statement parsed by DuckDB's OWN parser host-side and bound into
	// the ATTACHed catalog's schema (db.schema.m(...)). A purely LOCAL declaration — never embedded in
	// provider SQL, never dependent on server reachability. Fetch is best-effort host-side.
	int32_t (*catalog_macros)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);
	// Three columns: schema, name, create_sql — provider-declared CATALOG-BOUND DuckDB VIEWS, each
	// create_sql one complete CREATE VIEW statement parsed by DuckDB's OWN parser host-side and bound into
	// the ATTACHed catalog's schema, resolving as an ordinary relation `db.schema.v`.
	//
	// ⚠ A VIEW, unlike a macro, binds its body against ITS OWN catalog + schema (DuckDB's view binder
	// re-points the search path — bind_basetableref.cpp), so an unqualified reference inside the body
	// resolves against the catalog the view belongs to rather than the caller's. That is the whole reason
	// this entry exists beside catalog_macros: it is the only declaration form whose body can name the
	// provider's own tables without knowing the ATTACH alias.
	//
	// Same local-declaration contract as macros: never embedded in provider SQL, never dependent on server
	// reachability, fetch best-effort host-side. See docs/macros-and-sqlgen-functions.md §5.
	int32_t (*catalog_views)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);
	// Two columns: property, value — the detected capability profile, DIAGNOSTIC ONLY (the
	// fabricator_server_info() table function). The host consumes get_capabilities (v71) instead; nothing
	// greps these rows any more.
	int32_t (*catalog_server_info)(FabricatorHandle handle, struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// The TABLE session (ABI v72) — mirrors tablefn_* (session entries = <noun>_<verb>), replacing the
	// per-table metadata kinds (COLUMNS/ROWID/ROWCOUNT/COLUMN_NDV/VIRTUAL_COLUMNS) and scan_table.
	//
	// The handle wraps the table DEFINITION (+ the reference's AT clause), deliberately NOT a binding: the
	// C++ catalog entry is shared across transactions while per-(table × txn) state lives on the managed
	// bound table, so EVERY call below re-binds the definition against the CURRENT ambient transaction
	// (set_active_txn stays the transport — the §6 lazy-bind default). A definition holds no state, which is
	// what makes the handle's lifetime trivial: the entry keeps it for its whole life (incl. the
	// retire-don't-destroy graveyard) and it cannot go stale — staleness is governed by the binding layer,
	// which the per-transaction invalidation already owns. table_close in the entry DESTRUCTOR (teardown),
	// best-effort: it frees a GCHandle, nothing more.
	// -------------------------------------------------------------------------
	// Resolve (schema, table) to a table-session handle. `at_unit`/`at_value` (NULL/empty => none) carry the
	// reference's AT clause — part of the handle's IDENTITY, matching the C++ side where AT entries live in
	// their own map (time travel is a property of a reference). NO IO and NO absence probe: absence is
	// established by table_schema, the first actual read — same contract as the old kind-2, one entry over.
	// Cheap opening is load-bearing: catalog enumeration materializes every table.
	int32_t (*table_open)(FabricatorHandle handle, const char *schema, const char *table, const char *at_unit,
	                      const char *at_value, FabricatorHandle *out_table, char **err);
	// The table's column layout: a ZERO-ROW stream whose Arrow schema is the answer — the kind-2 carrier
	// kept deliberately (PopulateReturnSchema is the proven import path incl. VARIANT extension types; a
	// bare ArrowSchema would fork the type conversion for zero gain). Binds against the ambient txn, so a
	// buffered CREATE/ALTER's pending shape wins over storage (read-your-writes — the caller sets the
	// ambient first, exactly as the old FetchTableColumns did). Returns FABRICATOR_NOT_FOUND for ESTABLISHED
	// absence (Delta: no commit in the log; SQL Server: error 208) — never for a table that merely could not
	// be read.
	int32_t (*table_schema)(FabricatorHandle table, struct ArrowArrayStream *out, char **err);
	// Row identity + provider virtual columns, ONE crossing (was kinds 3 + 12), as ONE typed JSON doc
	// (v73; the v72 intermediate carried an Arrow stream):
	//   {"rowid":["a","b",...], "virtual":[{"name":"...","type":"<DuckDB type text>"}, ...]}
	// Both arrays always present (empty ok); rowid names in key order. *out_json is owned UTF-8, freed via
	// free_error (the get_capabilities convention). Parsed host-side with yyjson — OUR OWN vcpkg copy, not
	// DuckDB's vendored one, whose `duckdb_yyjson`-namespaced symbols are not DUCKDB_API-exported and so
	// cannot be resolved by a loadable extension (see CMakeLists.txt).
	int32_t (*table_info)(FabricatorHandle table, char **out_json, char **err);
	// Optimizer statistics, ONE crossing (was kinds 4 + 5, numbers as text), as ONE typed JSON doc (v73):
	//   {"row_count":N, "ndv":{"<column>":N, ...}}
	// row_count ABSENT = unknown; ndv always an object (may be empty). LAZY BY CONTRACT: called at first
	// scan, never at entry materialization, and deliberately NOT folded into table_info — bundling would put
	// the stats queries on the enumeration path. The warehouse never-issue-a-swallowable-statement rule
	// lives inside the providers (null/empty answers, no probe). *out_json owned UTF-8, freed via free_error.
	int32_t (*table_stats)(FabricatorHandle table, char **out_json, char **err);
	// Scan the table — scan_table minus the name pair; `spec_json`/`filter_values` exactly as before
	// (projection, filter tree + typed constants, TOP/ORDER, schema_only, and the AT clause, which still
	// rides the spec for the scan itself — the handle's AT selects the SCHEMA answer above).
	int32_t (*table_scan)(FabricatorHandle table, const char *spec_json, struct ArrowArrayStream *filter_values,
	                      struct ArrowArrayStream *out, char **err);
	// DDL: ALTER TABLE (ABI v74 — the old alter_table's kind int + arg1 + arg2 + flags, each overloaded per
	// kind, collapsed into ONE typed doc). The name pair is gone because the handle carries the identity:
	// sound HERE and not for create_table / begin_bulk, since an ALTER always targets an EXISTING table, so
	// there is no "the object does not exist yet" asymmetry to work around. Like every other session entry
	// this re-binds against the AMBIENT transaction, which is what lets a provider BUFFER the alter into an
	// open transaction (Delta's schema-evolution kinds) rather than committing it on its own.
	//
	// `alter_json` names its variant and carries ONLY that variant's fields — the v73 table_info/table_stats
	// pattern with the direction reversed (the HOST writes it, with yyjson's mutable API; the managed side
	// parses it with System.Text.Json). Absent optional key => false/none:
	//   {"kind":"rename_table",       "new_name":"t2"}
	//   {"kind":"rename_column",      "column":"a", "new_name":"b"}
	//   {"kind":"add_column",         "column":"c" [,"if_not_exists":true]}          + `column`
	//   {"kind":"drop_column",        "column":"c" [,"if_exists":true]}
	//   {"kind":"column_type",        "column":"c"}                                  + `column`
	//   {"kind":"set_not_null",       "column":"c"}
	//   {"kind":"drop_not_null",      "column":"c"}
	//   {"kind":"set_default",        "column":"c", "default":<string|null>}
	//   {"kind":"drop_default",       "column":"c"}
	//   {"kind":"add_field",          "path":["s","inner"] [,"if_not_exists":true]}  + `column`
	//   {"kind":"drop_field",         "path":["s","inner","f"] [,"if_exists":true]}
	//   {"kind":"rename_field",       "path":["s","inner","f"], "new_name":"g"}
	//   {"kind":"set_sorted_by",      "columns":["a","b"]}   ([] = RESET)
	//   {"kind":"set_partitioned_by", "columns":["a","b"]}   ([] = RESET)
	// `path` is an ARRAY of segments rather than a joined string because a field name may contain dots.
	// "default" is REQUIRED by set_default and carries the literal's TEXT, with JSON null for DEFAULT NULL —
	// the two states the old arg2 encoded as "-" / "b"+base64(literal), a hack that existed only because a
	// C string cannot distinguish empty from absent.
	//
	// ⚠ `column` STAYS a separate Arrow stream and must NOT fold into the doc: it is the TYPE CHANNEL for
	// the ADD_COLUMN / COLUMN_TYPE / ADD_FIELD kinds, and a VARIANT column rides an Arrow field-metadata
	// marker (VariantMarker / "ew.variant_transport") that a DuckDB type NAME cannot carry — rendering types
	// as text would silently regress exactly the extension-type shapes. NULL for every other kind; the
	// managed side consumes/releases it when present.
	int32_t (*table_alter)(FabricatorHandle table, const char *alter_json, struct ArrowArrayStream *column,
	                       char **err);
	// Release a handle from table_open. Idempotent; safe with NULL. Best-effort (entry teardown must not
	// throw) — frees the managed GCHandle, nothing more.
	void (*table_close)(FabricatorHandle table);

	// -------------------------------------------------------------------------
	// ROW-MAPPED (correlated LATERAL) table functions — `ILateralFunction`, ABI v79.
	//
	// The shape a table-in-out CANNOT express. An in-out declares its input as a {TABLE} parameter, so it is
	// called `f(<relation>)` and the relation must be nameable; a lateral function declares its positional
	// parameters as REAL VALUE TYPES and no TABLE marker, so DuckDB's binder synthesises the input relation
	// from whichever arguments are EXPRESSIONS. That is what makes the idiomatic correlated spelling work:
	//
	//     SELECT * FROM inputs i, f(i.a, i.b);            -- implicitly correlated (LATERAL)
	//     SELECT * FROM f(1, 2);                          -- the same bind, literal args
	//
	// TWO execution paths share ONE bind and ONE managed contract, which is what makes the batched path
	// checkable (a kill switch flips between them and the results must be identical):
	//   * ROW-BY-ROW — DuckDB's own PhysicalTableInOutFunction, which slices the child chunk to cardinality 1
	//     and stamps the correlated columns itself. One lateral_call per OUTER ROW.
	//   * BATCHED — our own PhysicalOperator (installed by an OptimizerExtension over the correlated shape),
	//     which hands the WHOLE input chunk over in one lateral_call and stamps the correlated columns from
	//     the provenance the callee returns. N calls become ceil(N / 2048).
	//
	// ⚠ PROVENANCE IS WHAT MAKES BATCHING SOUND, and it is why this is not the in-out exchange with a
	// different name. When N input rows produce M output rows the host must know, per output row, WHICH input
	// row produced it — otherwise it cannot stamp the correlated columns, and 1->N / 1->0 are inexpressible.
	// It rides the wire as a TRAILING int32 column on every result batch (see lateral_call), so there is one
	// wire format for both paths; the row-by-row path ignores it (DuckDB stamps) but still validates it.
	// -------------------------------------------------------------------------

	// Bind one lateral call. `args` (nullable) is a 1-row Arrow stream of the constant NAMED "cost" args
	// (consumed by the managed side) — a lateral function's POSITIONAL parameters are its per-row input
	// columns and carry no bind-time value, so they are NOT in `args`; they arrive as `input_schema`, the
	// Arrow schema of the per-row input (consumed/released). *out_schema receives a zero-row Arrow stream
	// whose schema = the function's OWN output columns — NOT the input echo an in-out returns, because the
	// correlated passthrough columns are the HOST's business (DuckDB's projected_input on the row-by-row
	// path, our own stamping on the batched one). *out_binding receives an opaque binding handle, reused
	// across executions and per-thread sessions; freed via lateral_bind_close.
	int32_t (*lateral_bind)(FabricatorHandle handle, const char *schema, const char *func,
	                        struct ArrowArrayStream *args, struct ArrowSchema *input_schema,
	                        struct ArrowArrayStream *out_schema, FabricatorHandle *out_binding, char **err);

	// Open one session on a bound binding. SEVERAL sessions may be open at once and that is the point: the
	// batched operator declares ParallelOperator(), so every pipeline thread gets its own OperatorState and
	// therefore its own session — no shared mutable state, no gate. (Contrast the in-out exchange, which
	// permits ONE exchange per binding and serialises parallel branches behind a mutex.)
	int32_t (*lateral_open)(FabricatorHandle binding, FabricatorHandle *out_session, char **err);

	// ONE batched call. `input` is an N-row Arrow array of the input columns (consumed/released by the
	// managed side); *out receives a stream of the result, where every batch carries the binding's output
	// columns PLUS one TRAILING int32 column giving, per output row, the 0-based index of the `input` row
	// that produced it. M may be 0 (every row filtered), less than N, or far more than N (fan-out) — a
	// batch bigger than a DuckDB vector is sliced by the host across HAVE_MORE_OUTPUT calls.
	//
	// The managed marshaling layer synthesises IDENTITY provenance when the author returned none, and
	// REFUSES the absent case when M != N (a fan-out or filtering map must say which parent each row has).
	// The host re-validates the range because it INDEXES with it.
	//
	// ⚠ A map owes exactly one response per request: an EMPTY stream means "0 output rows", never
	// "end of stream" — this is a request/response entry, not a long-lived exchange.
	int32_t (*lateral_call)(FabricatorHandle session, struct ArrowArray *input, struct ArrowArrayStream *out,
	                        char **err);

	// Release a session from lateral_open. Idempotent; safe with NULL. Best-effort (per-thread state teardown
	// must not throw).
	int32_t (*lateral_close)(FabricatorHandle session, char **err);

	// Release a binding from lateral_bind. Idempotent; safe with NULL. Best-effort (bind-data teardown must
	// not throw).
	int32_t (*lateral_bind_close)(FabricatorHandle binding, char **err);
} FabricatorVTable;

// -----------------------------------------------------------------------------
// Host services — function pointers the HOST provides TO the managed side (the reverse direction of the
// vtable). They let a managed component reach DuckDB's FileSystem so it can do secret-backed remote IO via
// DuckDB (one auth config — DuckDB secrets — shared with native reads). The host fills this struct and passes
// it to Bootstrap.Initialize; the managed side caches the pointers. SPIKE surface (open/size/read/close) —
// the foundation for a future C# lakehouse provider. A failing call returns non-zero and, when `err` is
// provided, sets *err to an owned UTF-8 message the managed side frees via `free_str`.
// -----------------------------------------------------------------------------
// Named Arrow inputs handed to host_query: the managed caller exports N Arrow streams + their names; the
// host registers each as a TEMPORARY (connection-scoped) view BEFORE running the query, so the SQL can
// reference them by name (`SELECT … FROM <name>`) and nothing survives the connection.
// ⚠⚠ THE HOST TAKES OWNERSHIP OF EACH STREAM AT ONCE AND THE CALLER MUST NOT RELEASE IT. This used to read
// "the host consumes the streams during the query (which materializes), so they're done by the time
// host_query returns" — which contradicted the implementation's own comment and was the WRONG half: the
// query is STREAMING (`SendQuery` returns as soon as the first chunk is ready), so the arrow scan is
// generally NOT consumed by then. That is exactly why `OwnedArrowInputs` ADOPTS each stream (C-data-interface
// move, source zeroed): the view holds a RAW POINTER and would otherwise outlive the caller's allocation.
// ⚠ Each input is scanned ONCE — the view is re-queryable but the stream is a cursor, so a second reference
// sees end-of-stream and returns ZERO ROWS silently (MEASURED 2026-09-03). count==0 / null => no inputs.
typedef struct FabricatorHostInputs {
	int32_t count;
	const char **names;                  // count UTF-8 view names
	struct ArrowArrayStream **streams;   // count Arrow streams (parallel to names)
} FabricatorHostInputs;

typedef struct FabricatorHostServices {
	// Mirrors FABRICATOR_ABI_VERSION so the managed side can reject a mismatched host services block.
	int32_t abi_version;
	// Open `path` for reading via DuckDB's FileSystem. `opener` is the opaque host FileOpener handle (its
	// ClientContext resolves secrets for az://, s3://, … ); valid only for the duration of the managed call
	// that received it. *out_file receives an opaque file handle (close via fs_close).
	int32_t (*fs_open_read)(FabricatorHandle opener, const char *path, FabricatorHandle *out_file, char **err);
	// File size in bytes.
	int32_t (*fs_size)(FabricatorHandle file, int64_t *out_size, char **err);
	// Read `nr_bytes` at byte offset `location` into `buffer` (caller-allocated, in managed memory).
	int32_t (*fs_read)(FabricatorHandle file, void *buffer, int64_t nr_bytes, int64_t location, char **err);
	// Close a file handle from fs_open_read. Safe with NULL.
	void (*fs_close)(FabricatorHandle file);
	// Free an error string returned by the fs_* callbacks above.
	void (*free_str)(char *str);
	// Glob `pattern` (DuckDB glob, e.g. "<root>/_delta_log/*") via DuckDB's FileSystem (opener resolves
	// secrets). *out_json receives an owned UTF-8 JSON array of {"path":<string>,"size":<int64>} (freed via
	// free_str). Used by the managed lakehouse filesystem's directory listing.
	int32_t (*fs_glob)(FabricatorHandle opener, const char *pattern, char **out_json, char **err);

	// Host query — run `sql` on a FRESH host DuckDB connection (its own ClientContext/transaction; never the
	// in-flight one, which is non-reentrant) and return the result as an ArrowArrayStream in *out. Lets a
	// managed component reuse the host engine (functions, readers, the catalog) over Arrow. Separate
	// transaction => committed-reads semantics. The result stream (and its connection) is owned by the
	// managed caller, which releases it when done. `params` (nullable) is a 1-row Arrow stream whose columns
	// bind POSITIONALLY to the statement's parameters (?, $1, …) via a prepared statement. `inputs` (nullable)
	// registers named Arrow sources as connection-scoped views before the query (data-in). `out_interrupt`
	// (nullable): receives an opaque cancellation handle for THIS query's fresh ClientContext — trip it via
	// host_query_interrupt (thread-safe, any time) and free it via host_query_interrupt_free once the result
	// stream is released (the handle owns a shared_ptr, so a late interrupt is a harmless no-op on a dead
	// query). The fresh connection is invisible to the USER query's Ctrl+C, so without this a long host-side
	// fetch (the native_write rewrite's read_parquet JOIN, a big COPY) was uncancellable (ABI v66). See
	// docs/host-query.md + docs/cancellation.md.
	//
	// ⚠⚠ `client_context` (ABI v83) is OPTIONAL and it is what lets a managed caller run "as my caller
	// would". 0 = a clean session, which is what every caller got before v83; non-zero = the calling
	// operator's ClientContext, whose TimeZone and catalog SEARCH PATH are copied onto the fresh
	// connection. The search path covers current_catalog() and current_schema() too — all three read the
	// same CatalogSearchPath object — so it is one thing to copy, not three. It goes through the SAME
	// CaptureSession/ApplyHostQuerySession pair the fabricator_host_query SQL surface has always used, so
	// the two surfaces cannot drift.
	//
	// ⚠ A per-call ARGUMENT rather than an ambient the service reads for itself, because inheriting is a
	// CHOICE: a template rendering the caller's statement wants it; a provider doing its own internal
	// bookkeeping does not, and a clean session is the safer default for anything that must not depend on
	// who called it. The managed Host.Query exposes it the same way (`clientSession`, default 0).
	//
	// ⚠ It is NOT general inheritance: only those settings are copied, and "copy the session" has no
	// principled boundary. What is copied is what has been needed; adding one is a deliberate act.
	// ⚠⚠ `connection` (ABI v84) is OPTIONAL: 0 = a FRESH connection per call, which is what every caller
	// got before v84; non-zero = a PINNED connection from host_connection_open, so several calls share one
	// DuckDB Connection and therefore one TEMPORARY catalog, one set of session settings and one
	// transaction context. That is what lets a caller CREATE TEMP TABLE in one call and read it in the
	// next — the read-your-writes a fresh connection structurally cannot give (see host_connection_open).
	int32_t (*host_query)(const char *sql, struct ArrowArrayStream *params, struct FabricatorHostInputs *inputs,
	                      FabricatorHandle client_context, FabricatorHandle connection,
	                      struct ArrowArrayStream *out, void **out_interrupt, char **err);

	// -------------------------------------------------------------------------
	// WRITE surface (foundation for a Delta WRITE-back through the host FileSystem; see docs/delta-catalog.md).
	// `opener` is the calling operator's ClientContext (secret resolution), valid for the duration of the call.
	// -------------------------------------------------------------------------
	// Open `path` for sequential writing. `exclusive` (1/0): when 1, opens with EXCLUSIVE_CREATE
	// (WRITE|FILE_CREATE|EXCLUSIVE_CREATE) — the put-if-absent primitive: FAILS (non-zero + *err) if the file
	// already exists, which is honored on OneLake/ADLS and POSIX (and is how a Delta commit detects a conflict).
	// When 0, opens create-or-truncate (WRITE|FILE_CREATE_NEW). *out_file receives a write handle (close via
	// fs_close_write). NOTE: Azure DFS allows only sequential writes (or location 0).
	int32_t (*fs_open_write)(FabricatorHandle opener, const char *path, int32_t exclusive, FabricatorHandle *out_file,
	                         char **err);
	// Append `nr_bytes` from `buffer` to a write handle (sequential; the position advances).
	int32_t (*fs_write)(FabricatorHandle file, const void *buffer, int64_t nr_bytes, char **err);
	// Flush + close a write handle from fs_open_write (surfaces flush errors, unlike fs_close). Frees the handle.
	int32_t (*fs_close_write)(FabricatorHandle file, char **err);
	// Remove `path`. Does NOT error if it does not exist (TryRemoveFile semantics).
	int32_t (*fs_remove)(FabricatorHandle opener, const char *path, char **err);
	// Create directory `path` (idempotent — ok if it already exists). On object stores directories are implicit;
	// on a local filesystem this materializes the parent (e.g. `_delta_log/`) before a write.
	int32_t (*fs_create_dir)(FabricatorHandle opener, const char *path, char **err);
	// Remove directory `path` RECURSIVELY (all files + subdirectories). Idempotent — no error if it does not
	// exist. Maps to DuckDB's FileSystem::RemoveDirectory (recursive on local; on object stores it deletes every
	// object under the prefix). Used to DROP a Delta catalog table (its whole `<table>/` folder).
	int32_t (*fs_remove_dir)(FabricatorHandle opener, const char *path, char **err);
	// Rename/move directory `src` to `dest`. Maps to DuckDB's FileSystem::MoveFile — atomic on a local
	// filesystem (a directory rename); object stores (S3/Azure DFS) generally do NOT implement it and throw.
	// Used to RENAME a Delta catalog table (move its whole `<table>/` folder; OneLake renames via the DFS SDK
	// directly instead, since Azure MoveFile is unimplemented).
	int32_t (*fs_move_dir)(FabricatorHandle opener, const char *src, const char *dest, char **err);

	// Forward a managed .NET-logging event into DuckDB's internal logging (duckdb_logs), so the ILogger trace
	// (queries, filters, files) is visible in the engine's own log alongside the optional file sink. `level` is
	// the stable code 0 Trace / 1 Debug / 2 Info / 3 Warning / 4 Error / 5 Critical (FabricatorLog.LevelCode);
	// `log_type` = the logger category, `message` = the formatted line. Best-effort (no error out); a no-op if
	// the host has no database/logger. Additive host-service entry (ABI v58).
	void (*host_log)(int32_t level, const char *log_type, const char *message);

	// Read the calling operator's interrupt flag (ClientContext::interrupted — set by Ctrl+C via
	// Connection::Interrupt() or by a query timeout). `opener` is the same host FileOpener handle (a
	// ClientContext*) the fs_* callbacks receive; returns 1 when the query is interrupted, else 0 (0 also for
	// a null opener). Lets a managed poller (InterruptScope) trip a CancellationToken so long-running C# I/O
	// (a blocking OneLake/S3/SQL read) is cancelled when a Ctrl+C would otherwise hang the shell — DuckDB only
	// checks interruption BETWEEN operator calls, so a single blocking get_next is invisible to it. Additive
	// host-service entry (ABI v65). See docs/cancellation.md.
	int32_t (*is_interrupted)(FabricatorHandle opener);

	// Interrupt the fresh ClientContext behind a host_query result (handle from host_query's out_interrupt):
	// sets its interrupted flag so an in-flight Fetch aborts at DuckDB's next check. Thread-safe, callable any
	// time (incl. after the result stream is released — the handle keeps the context alive, the interrupt is
	// then a no-op). Best-effort, never errors. Additive host-service entries (ABI v66).
	void (*host_query_interrupt)(void *interrupt_handle);
	// Free the interrupt handle (exactly once, after any in-flight host_query_interrupt has returned —
	// the managed wrapper orders registration-dispose before this).
	void (*host_query_interrupt_free)(void *interrupt_handle);

	// Perform an HTTP request through DuckDB's OWN HTTP layer (ABI v76). The point is not the socket — it is
	// that the request inherits DuckDB's configuration: the `TYPE http` SECRET whose SCOPE matches this URL
	// (bearer_token / extra_http_headers / proxy / verify_ssl), `ca_cert_file`, `http_proxy*`, `http_timeout`,
	// `http_retries`/`http_retry_backoff`. A managed component — above all a PLUGIN calling a REST API — thus
	// stops carrying its own TLS trust, proxy and retry policy. The managed `DuckDbHttpHandler` wraps this as
	// an ordinary .NET HttpMessageHandler. See docs/http-transport.md.
	//
	// `opener` is the calling operator's ClientContext (valid for the duration of the call) — it selects the
	// HTTPUtil and resolves the secret+settings. `method` is GET|PUT|HEAD|DELETE|POST; DuckDB's RequestType
	// has no others, so PATCH/OPTIONS/TRACE are REFUSED by name rather than silently mapped onto POST.
	// `headers_json` is a JSON object {"Name":"value", …} or null.
	//
	// ⚠ ONE VALUE PER HEADER NAME, in BOTH directions, and that is DuckDB's model: HTTPHeaders is a
	// case-insensitive MAP, so a repeated header (Set-Cookie) cannot be carried at all.
	//
	// `body`/`body_length` are the request body (nullable; meaningful for PUT/POST). On success:
	//   *out_response_json = {"status":N,"reason":..,"url":..,"success":bool,"error":..,"headers":{..}}
	//   *out_body / *out_body_length = the response body as raw bytes (null when empty)
	// Both are owned by the caller and freed via free_str (which is plain free()). A non-empty "error" is a
	// TRANSPORT failure (DNS/connect/TLS); an HTTP status the server actually returned — 404, 500 — arrives
	// as a normal response with a status, never as a non-zero return. A non-zero return means the request
	// could not be ATTEMPTED (bad method, no context, malformed headers), with *err set.
	//
	// ⚠ The response body is FULLY BUFFERED. DuckDB's own HTTPResponse::body is a std::string, so there is
	// no streaming to inherit; a paging REST reader must page, not stream.
	int32_t (*http_request)(FabricatorHandle opener, const char *method, const char *url, const char *headers_json,
	                        const void *body, int64_t body_length, char **out_response_json, void **out_body,
	                        int64_t *out_body_length, char **err);
	// -------------------------------------------------------------------------
	// PINNED host connection (ABI v84) — several host_query calls on ONE DuckDB Connection.
	// -------------------------------------------------------------------------
	// Open a host connection that OUTLIVES a single host_query call and hand back an owned handle. Pass it
	// as host_query's `connection` and the statements share one Connection, hence one TEMPORARY catalog,
	// one set of session settings and one transaction context.
	//
	// ⚠⚠ WHY IT EXISTS: a fresh-per-call connection cannot see what an earlier call wrote in the same
	// logical unit of work. `exec('CREATE TEMP TABLE t …')` then `query('SELECT … FROM t')` is the shape,
	// and on separate connections the second call fails — a TEMP table is scoped to the ClientContext that
	// created it (MEASURED: invisible from any other connection, and dropped by that connection's own
	// ROLLBACK). A pinned connection makes such a pair a working scratch space that needs no name in the
	// shared catalog and no cleanup, because closing the handle destroys the temporary catalog with it.
	//
	// `client_context` is the v83 optional session source, applied ONCE at open: 0 = a clean session,
	// non-zero = the calling operator's ClientContext, whose TimeZone and catalog SEARCH PATH are copied.
	// ⚠ Applied at OPEN, not per query, which is the point of pinning — a `SET` performed through the
	// pinned connection then STICKS for its life (measured), so the caller can configure its own session.
	//
	// ⚠⚠ ONE STREAMING RESULT AT A TIME, AND THE HOST ENFORCES IT. DuckDB's every query path calls
	// ClientContext::InitialCleanup, which CLOSES the connection's active streaming result — and MEASURED,
	// it does so SILENTLY: the abandoned stream reports end-of-stream, so the first query's remaining rows
	// are LOST with no error anywhere. A second host_query on a pinned connection whose previous result
	// stream is still open is therefore REFUSED with a message naming the cause, rather than allowed to
	// truncate it. Release the stream (or drain it) before the next call. A FRESH-connection call is
	// unaffected — it has a connection of its own.
	//
	// ⚠ NOT THREAD-SAFE, like any DuckDB Connection: one call at a time per handle. A caller that renders
	// on several threads opens one connection per thread (which is what the Fluid engine does — one per
	// render, created lazily on the first query/exec).
	//
	// ⚠ NAMED ARROW INPUTS ARE REFUSED on a pinned connection — and the reason shipped with v84 ("a
	// connection-scoped view … would collide") was MEASURED FALSE on 2026-09-03: `replace: true` replaces,
	// and the view was a CATALOG view. Inputs are TEMPORARY views now, so a pin is the RIGHT scope and the
	// refusal is LIFTABLE — but not by deleting it: the view would then outlive the RESULT STREAM that owns
	// the input's storage, which is the same defect one layer over. docs/fluid-templating.md §17.10.
	int32_t (*host_connection_open)(FabricatorHandle client_context, FabricatorHandle *out_connection, char **err);
	// Close a handle from host_connection_open. Safe with 0, and idempotent from the caller's point of view
	// only in the sense that the handle must not be reused afterwards. ⚠ Result streams opened on this
	// connection keep the underlying Connection ALIVE (they hold a shared reference), so closing the handle
	// while a stream is outstanding is safe rather than a use-after-free — the Connection dies with the
	// last of them. The TEMPORARY catalog goes with it.
	void (*host_connection_close)(FabricatorHandle connection);
} FabricatorHostServices;

// Max serialized size of a spillable aggregate's per-group state (the inline, pointer-free
// state blob is this many bytes + a 4-byte length prefix). Serialize() must fit within it.
#define FABRICATOR_AGG_SPILL_CAP 1024

#define FABRICATOR_ABI_VERSION 84

// Signature of the managed bootstrap entry point loaded via hostfxr.
// Returns 0 on success; fills *vtable. `size` is sizeof(FabricatorVTable) as seen
// by the C++ caller, allowing the managed side to guard against truncation.
typedef int32_t (*fabricator_bootstrap_fn)(FabricatorVTable *vtable, int32_t size);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // FABRICATOR_ABI_H
