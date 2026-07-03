//===----------------------------------------------------------------------===//
//                         ArrowNet — C ABI contract
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
// message string that must be released via ArrowNetVTable::free_error.
//===----------------------------------------------------------------------===//

#ifndef ARROWNET_ABI_H
#define ARROWNET_ABI_H

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
	ARROWNET_OK = 0,
	ARROWNET_ERROR = 1,           // generic failure; see *err
	ARROWNET_INVALID_ARGUMENT = 2,
	ARROWNET_NOT_FOUND = 3,
	ARROWNET_ALREADY_EXISTS = 4,  // fs_open_write(exclusive): the target already exists (a commit conflict)
} ArrowNetStatus;

// Opaque handle to a managed catalog/connection (a GCHandle id on the C# side).
typedef void *ArrowNetHandle;

// -----------------------------------------------------------------------------
// Metadata kinds requested via ArrowNetVTable::get_metadata. The managed side
// owns all provider SQL; the result is always an Arrow stream. For SCHEMAS /
// TABLES / ROWID the columns are UTF-8 strings (read with ReadStringTable); for
// COLUMNS the stream carries zero rows and its *schema* describes the table's
// columns (so DuckDB's Arrow->LogicalType inference is reused, no C++ mapping).
// -----------------------------------------------------------------------------
typedef enum {
	ARROWNET_META_SCHEMAS = 0, // one column: user schema names
	ARROWNET_META_TABLES = 1,  // three columns: schema, table, type ("BASE TABLE"|"VIEW")
	ARROWNET_META_COLUMNS = 2, // zero rows; schema = the table's column layout
	ARROWNET_META_ROWID = 3,   // one column: row-identity column names, in key order
	ARROWNET_META_ROWCOUNT = 4, // one column, one row: approximate table row count (as text)
	ARROWNET_META_COLUMN_NDV = 5, // two columns: column name, distinct-value estimate (NDV, as text)
	ARROWNET_META_FUNCTIONS = 6,  // discovered routines: schema, name, kind, param_count, return_type
	ARROWNET_META_SERVER_INFO = 7, // two columns: property, value — the detected server capability profile
	ARROWNET_META_SNAPSHOTS = 8,   // Delta only: a table's commit history (version, timestamp, operation,
	                               // operation_parameters); arg1 = schema, arg2 = table
	ARROWNET_META_CHANGES = 9,     // Delta only: the Change Data Feed; arg1 = 'schema.table' ref,
	                               // arg2 = "from:to" version range (to empty => latest)
} ArrowNetMetadataKind;

// -----------------------------------------------------------------------------
// ALTER TABLE variants passed to ArrowNetVTable::alter_table. The managed side
// generates the provider DDL. `arg1`/`arg2` carry names; for ADD_COLUMN /
// COLUMN_TYPE the new column's type travels as a single-field zero-row Arrow
// schema in the `column` stream. `flags` bit 0 is the if-(not-)exists guard.
// -----------------------------------------------------------------------------
typedef enum {
	ARROWNET_ALTER_RENAME_TABLE = 0,  // arg1 = new table name
	ARROWNET_ALTER_RENAME_COLUMN = 1, // arg1 = old column name, arg2 = new column name
	ARROWNET_ALTER_ADD_COLUMN = 2,    // arg1 = column name; `column` carries its type; flag0 = if_not_exists
	ARROWNET_ALTER_DROP_COLUMN = 3,   // arg1 = column name; flag0 = if_exists
	ARROWNET_ALTER_COLUMN_TYPE = 4,   // arg1 = column name; `column` carries the new type
	ARROWNET_ALTER_SET_NOT_NULL = 5,  // arg1 = column name (managed side restates the current type)
	ARROWNET_ALTER_DROP_NOT_NULL = 6, // arg1 = column name
	ARROWNET_ALTER_SET_DEFAULT = 7,   // arg1 = column name; arg2 = "-" (DEFAULT NULL) or "b"+base64(literal)
	ARROWNET_ALTER_DROP_DEFAULT = 8,  // arg1 = column name
} ArrowNetAlterKind;

#define ARROWNET_ALTER_FLAG_IF_EXISTS 1

// -----------------------------------------------------------------------------
// The vtable. The managed Bootstrap.Initialize fills this struct in place. New
// entries are appended (never reordered) so the C++ side can negotiate by size.
// -----------------------------------------------------------------------------
typedef struct ArrowNetVTable {
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
	// schema_filter/table_filter in get_metadata and stores isolation_level for table-in-out sessions). See
	// docs/provider-extensibility.md §3.
	int32_t (*open_catalog)(const char *provider, const char *conn, const char *options_json,
	                        ArrowNetHandle *out_handle, char **err);

	// Close a handle previously returned by open_catalog. Safe with NULL.
	void (*close_catalog)(ArrowNetHandle handle);

	// Execute a query and export the result as an Arrow stream into *out.
	// `handle` may be NULL in Phase 0 stub mode.
	int32_t (*execute_query)(ArrowNetHandle handle, const char *sql,
	                         struct ArrowArrayStream *out, char **err);

	// Release an error string previously returned through a char** out param.
	void (*free_error)(char *err);

	// Execute a non-query statement (DML/DDL); *affected receives rows affected.
	// `schema_may_change` (out, nullable): set to 1 if the statement may have changed
	// schema/catalog metadata (DDL heuristic, decided in C#) so the host can invalidate
	// its catalog cache; 0 otherwise.
	int32_t (*execute_dml)(ArrowNetHandle handle, const char *sql, int64_t *affected, int32_t *schema_may_change,
	                       char **err);

	// Bulk-load an Arrow stream (produced by the host) into a table. Generic: the
	// managed side maps the Arrow schema to provider types, optionally creates the
	// table (create_table / replace), and bulk-copies. *affected = rows written.
	// The managed side takes ownership of `in` (consumes + releases it).
	int32_t (*bulk_insert)(ArrowNetHandle handle, const char *schema, const char *table, int32_t create_table,
	                       int32_t replace, struct ArrowArrayStream *in, int64_t *affected, char **err);

	// rowid-based DELETE. `keys` is an Arrow stream whose columns (named by their
	// Arrow field names) are the key column values to delete. The managed side
	// generates the provider DELETE (parameterized). Takes ownership of `keys`.
	int32_t (*execute_delete)(ArrowNetHandle handle, const char *schema, const char *table,
	                          struct ArrowArrayStream *keys, int64_t *affected, char **err);

	// rowid-based UPDATE. `data` is an Arrow stream with the first `set_count`
	// columns being the SET values and the remaining columns the key values
	// (all named by Arrow field name). Managed side generates the provider
	// UPDATE (parameterized). Takes ownership of `data`.
	int32_t (*execute_update)(ArrowNetHandle handle, const char *schema, const char *table, int32_t set_count,
	                          struct ArrowArrayStream *data, int64_t *affected, char **err);

	// Discover provider metadata. `kind` is an ArrowNetMetadataKind; `arg1`/`arg2`
	// carry the schema/table name when the kind needs them (NULL otherwise). The
	// result is exported into *out as an Arrow stream (see ArrowNetMetadataKind).
	// Keeps all provider catalog SQL (sys.*, PK/unique-index discovery) in C#.
	int32_t (*get_metadata)(ArrowNetHandle handle, int32_t kind, const char *arg1, const char *arg2,
	                        struct ArrowArrayStream *out, char **err);

	// Scan a table: the managed side builds the provider SELECT and exports the
	// rows into *out as an Arrow stream. Keeps the read-path SQL in C#.
	//
	// `spec_json` (nullable) carries pushdown info as a small JSON document:
	//   { "columns": ["a","b"],          // projection; absent/empty => SELECT *
	//     "filter":  <predicate-tree> }   // WHERE; absent/null => no filter
	// Predicate-tree nodes reference constants by index into `filter_values`.
	// `filter_values` (nullable) is a one-batch Arrow stream whose columns are the
	// typed constant values the filter tree refers to (column i == value index i).
	// Both null/empty => a plain full-table scan (back-compat).
	int32_t (*scan_table)(ArrowNetHandle handle, const char *schema, const char *table, const char *spec_json,
	                      struct ArrowArrayStream *filter_values, struct ArrowArrayStream *out, char **err);

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
	int32_t (*create_table)(ArrowNetHandle handle, const char *schema, const char *table,
	                        struct ArrowArrayStream *columns, int32_t if_not_exists, const char *pk_columns,
	                        const char *unique_columns, const char *defaults, const char *partition_columns,
	                        const char *sort_columns, const char *identity_columns, char **err);

	// DDL: drop a table. `if_exists` suppresses the error when it is absent.
	int32_t (*drop_table)(ArrowNetHandle handle, const char *schema, const char *table, int32_t if_exists, char **err);

	// DDL: create a schema. `if_not_exists` guards creation.
	int32_t (*create_schema)(ArrowNetHandle handle, const char *schema, int32_t if_not_exists, char **err);

	// DDL: drop a schema. `if_exists` suppresses the error when it is absent.
	int32_t (*drop_schema)(ArrowNetHandle handle, const char *schema, int32_t if_exists, char **err);

	// DDL: alter a table. `alter_kind` is an ArrowNetAlterKind; `arg1`/`arg2` are
	// names (per kind). For ADD_COLUMN / COLUMN_TYPE the new column's type travels
	// as a single-field zero-row Arrow schema in `column` (NULL otherwise; the
	// managed side consumes/releases it when present). `flags` bit 0 is the
	// if-(not-)exists guard. The managed side generates the provider ALTER.
	int32_t (*alter_table)(ArrowNetHandle handle, const char *schema, const char *table, int32_t alter_kind,
	                       const char *arg1, const char *arg2, struct ArrowArrayStream *column, int32_t flags,
	                       char **err);

	// Transaction boundaries for a catalog handle. begin_transaction enters
	// transaction mode (the managed side pins a connection + provider transaction
	// lazily on the first write); commit/rollback finish it. While in transaction
	// mode all DML (execute_dml/bulk_insert/execute_delete/execute_update) runs on
	// the pinned connection so commit/rollback are atomic. Reads stay on their own
	// connections. begin on an already-open transaction is a no-op.
	int32_t (*begin_transaction)(ArrowNetHandle handle, char **err);
	int32_t (*commit_transaction)(ArrowNetHandle handle, char **err);
	int32_t (*rollback_transaction)(ArrowNetHandle handle, char **err);

	// INSERT ... RETURNING. `in` is an Arrow stream of the rows to insert (its
	// field names are the target column list); the managed side runs
	// INSERT ... OUTPUT INSERTED.* and exports the inserted rows (all table
	// columns, in table order) into *out. Consumes/releases `in`.
	int32_t (*insert_returning)(ArrowNetHandle handle, const char *schema, const char *table,
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
	int32_t (*begin_bulk)(ArrowNetHandle handle, const char *schema, const char *table, int32_t create_table,
	                      int32_t replace, int32_t check_constraints, int64_t txn_id, struct ArrowSchema *schema_in,
	                      const char *partition_columns, const char *sort_columns, const char *schema_mode,
	                      ArrowNetHandle *out_session, char **err);

	// push_batch enqueues one record batch into the session. The managed side
	// imports `batch` (taking ownership and releasing it); the caller never
	// releases it. Blocks (backpressure) while the channel is full; returns once
	// enqueued.
	int32_t (*push_batch)(ArrowNetHandle session, struct ArrowArray *batch, char **err);

	// complete_bulk finishes the session: signals end-of-stream, waits for the
	// background load to drain, returns rows written in *affected, and frees the
	// session. If `abort` is non-zero the load is cancelled (errors swallowed),
	// for cleanup on a failed/cancelled query. The session handle is invalid
	// after this call.
	int32_t (*complete_bulk)(ArrowNetHandle session, int32_t abort, int64_t *affected, char **err);

	// Build a provider connection string from a secret's fields. The host reads the secret's key/values
	// (DuckDB SecretManager) and passes them as a flat JSON object {"key":"value",...}; `provider` selects the
	// backend whose connstr format applies (empty => default). `secret_type` is the DuckDB type of the secret
	// the fields came from (e.g. "mssql_net" = this provider's own full secret; "azure" = a FOREIGN secret
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
	int32_t (*get_function_param_schema)(ArrowNetHandle handle, const char *schema, const char *func,
	                                     struct ArrowSchema *out, char **err);

	// *out receives the Arrow schema whose single field = the scalar function's return type (a bare ArrowSchema).
	int32_t (*get_function_return_schema)(ArrowNetHandle handle, const char *schema, const char *func,
	                                      struct ArrowSchema *out, char **err);

	// Execute a scalar function over an input batch: `args` is an N-row stream whose
	// columns are the argument values (in param order); *out receives an N-row stream
	// with a single column = the per-row results (typed as the function's return).
	// The managed side consumes `args`.
	int32_t (*execute_scalar)(ArrowNetHandle handle, const char *schema, const char *func,
	                          struct ArrowArrayStream *args, struct ArrowArrayStream *out, char **err);

	// *out receives the Arrow schema of a table-returning function's output columns (a bare ArrowSchema).
	// `args` (nullable) is a 1-row Arrow STREAM of the constant call arguments (in param order; consumed by
	// the managed side when present) — a custom table function's output schema MAY depend on them (the managed
	// side binds the call and returns the bound output schema); discovered SQL TVFs/procs read it from metadata
	// and ignore `args`. Pass NULL for `args` when there are none (e.g. the in-out `_each` base-schema lookup).
	int32_t (*get_function_output_schema)(ArrowNetHandle handle, const char *schema, const char *func,
	                                      struct ArrowArrayStream *args, struct ArrowSchema *out, char **err);

	// (execute_table / execute_proc were removed at ABI v30 — superseded by the table-function session
	//  table_bind / table_execute / table_close at the end of this struct.)

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
	int32_t (*agg_open)(ArrowNetHandle handle, const char *schema, const char *func, ArrowNetHandle *out_session,
	                    char **err);

	// Update: `batch` is an N-row Arrow array whose column 0 is an int64 "state_id"
	// and columns 1.. are the argument values (in param order). The managed side
	// groups rows by id, get-or-creates each accumulator, and applies the per-group
	// rows. Consumes/releases `batch`. (simple_update reuses this with a constant id.)
	int32_t (*agg_update)(ArrowNetHandle session, struct ArrowArray *batch, char **err);

	// Combine: `batch` is an N-row Arrow array of two int64 columns
	// [target_id, source_id]; the managed side merges each source accumulator into
	// its target (absent source => empty, skipped). Consumes/releases `batch`.
	int32_t (*agg_combine)(ArrowNetHandle session, struct ArrowArray *batch, char **err);

	// Finalize: `ids` is an N-row Arrow array of a single int64 "state_id" column;
	// *out receives an N-row stream with one column = each group's result, in the
	// SAME ORDER as `ids` (an absent id => a fresh accumulator => the empty-group
	// value). Consumes/releases `ids`.
	int32_t (*agg_finalize)(ArrowNetHandle session, struct ArrowArray *ids, struct ArrowArrayStream *out,
	                        char **err);

	// Destroy: `ids` is an N-row Arrow array of a single int64 "state_id" column;
	// the managed side drops those accumulators (bounds memory for the window paths
	// that churn transient states). Best-effort (a destructor must not throw).
	// Consumes/releases `ids`.
	int32_t (*agg_destroy)(ArrowNetHandle session, struct ArrowArray *ids, char **err);

	// Release the session (frees the dictionary + GCHandle). Idempotent. Safe with
	// nullptr. Best-effort (teardown must not throw).
	int32_t (*agg_close)(ArrowNetHandle session, char **err);

	// -------------------------------------------------------------------------
	// Spillable aggregates (Phase 4h opt-in, `IArrowAggregateFunction.SupportsSpill`).
	// For these the per-group accumulator is serialized into the fixed-size,
	// pointer-free state blob (`[uint32 len][byte data[ARROWNET_AGG_SPILL_CAP]]`),
	// so DuckDB's external GROUP BY can spill it to disk. Each call round-trips the
	// state bytes <-> the C# accumulator (no persistent C# state). State is carried
	// as an Arrow BLOB column; a NULL row = a fresh/empty group. Serialized state
	// must fit ARROWNET_AGG_SPILL_CAP bytes.
	// -------------------------------------------------------------------------

	// Update: `group_states` = a BLOB column, one row per distinct group in this chunk
	// (its current serialized state; NULL = fresh); `batch` = `[int64 slot ++ params]`,
	// N rows (slot indexes into group_states). *out = a BLOB column of the new serialized
	// state per group, SAME ORDER as group_states. Consumes both input arrays.
	int32_t (*agg_update_spill)(ArrowNetHandle session, struct ArrowArray *group_states, struct ArrowArray *batch,
	                            struct ArrowArrayStream *out, char **err);

	// Combine: `target_states` = a BLOB column, one row per distinct TARGET group (NULL = fresh); `batch` =
	// `[int64 slot, BLOB source]`, N rows (slot indexes into target_states; source = the partial state to
	// merge in — a target may repeat across rows, e.g. the window segment-tree merges several nodes into one
	// frame state). *out = a BLOB column of the merged state per target, SAME ORDER as target_states.
	// Consumes both input arrays.
	int32_t (*agg_combine_spill)(ArrowNetHandle session, struct ArrowArray *target_states, struct ArrowArray *batch,
	                             struct ArrowArrayStream *out, char **err);

	// Finalize: `states` = a BLOB column, N rows (NULL = fresh/empty). *out = one result
	// column, N rows, SAME ORDER. Consumes `states`.
	int32_t (*agg_finalize_spill)(ArrowNetHandle session, struct ArrowArray *states, struct ArrowArrayStream *out,
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
	int32_t (*inout_bind)(ArrowNetHandle handle, const char *schema, const char *func,
	                      struct ArrowArrayStream *args, struct ArrowSchema *input_schema,
	                      struct ArrowArrayStream *out_schema, ArrowNetHandle *out_binding, char **err);

	// Open one execution exchange on a bound binding. `input` is an Arrow stream the HOST has
	// populated (host exports; its get_next yields one input chunk per gate tenure, a released/null
	// array at end) — the managed side IMPORTS it (takes ownership, pulls, releases). *output receives the
	// managed OUTPUT stream (the managed side exports into it; the host pulls it — non-empty batch =
	// HAVE_MORE_OUTPUT, length-0 batch = NEED_MORE_INPUT, released/null array = FINISHED — and releases it).
	// The connection opens lazily on first input pull. One binding may open at most one exchange at a time.
	// The SQL isolation for the read transaction is resolved + set on the binding in C# at inout_bind (SET
	// mssql_isolation_level ?? the catalog's ATTACH isolation_level — both C#-owned), so it is not passed here.
	int32_t (*inout_exchange_open)(ArrowNetHandle binding, struct ArrowArrayStream *input,
	                               struct ArrowArrayStream *output, char **err);

	// Release a binding handle from inout_bind. Idempotent; safe with nullptr. Best-effort
	// (bind-data teardown must not throw).
	int32_t (*inout_bind_close)(ArrowNetHandle binding, char **err);

	// -------------------------------------------------------------------------
	// Table-function session (Phase 5). The session-handle successor to the
	// stateless execute_table / execute_proc (removed at v30): table_bind resolves
	// a per-PLAN binding (output schema + whether it accepts pushdown) once; that
	// binding is reused by table_execute for each execution (the result stream owns
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
	// release it. *supports_pushdown drives the host's projection mapping: 1 = map result
	// columns by NAME — a discovered TVF (pushes the projection + filter into the SELECT)
	// or a custom function (returns its full result, mapped by name); 0 = a stored proc
	// (full result, projected positionally + filtered above the scan). *out_binding
	// receives an opaque binding handle, reused by table_execute across (prepared)
	// re-executions and freed via table_close.
	int32_t (*table_bind)(ArrowNetHandle handle, const char *schema, const char *func,
	                      struct ArrowArrayStream *args, struct ArrowArrayStream *out_schema,
	                      int32_t *supports_pushdown, ArrowNetHandle *out_binding, char **err);

	// Execute a bound table function. `spec_json` (nullable/empty => SELECT *) +
	// `filter_values` (nullable) carry projection + best-effort filter pushdown,
	// honored only when the binding reported supports_pushdown (else ignored — DuckDB
	// re-applies above the scan). *out receives the result rows (its stream owns the
	// provider connection, released by the host at scan teardown). Called once per
	// execution; the binding may be executed repeatedly.
	int32_t (*table_execute)(ArrowNetHandle binding, const char *spec_json,
	                         struct ArrowArrayStream *filter_values, struct ArrowArrayStream *out, char **err);

	// Release a binding handle from table_bind. Idempotent; safe with nullptr.
	// Best-effort (bind-data teardown must not throw).
	int32_t (*table_close)(ArrowNetHandle binding, char **err);

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
	int32_t (*set_setting)(const char *provider, const char *name, const char *value, char **err);

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
	// `join_only` (1/0): set ONLY by the raw `mssql_net_exec` passthrough. When 1, the following write JOINS
	// the active transaction's pinned connection if one already exists (a DuckDB-managed write is in flight in
	// this transaction) — so the exec is atomic with the transaction and sees its uncommitted writes — else it
	// autocommits on a fresh connection WITHOUT creating persistent transaction state (a raw exec's target
	// never triggers DuckDB's transaction lifecycle, so nothing would ever commit a pinned connection). Normal
	// DuckDB-managed writes pass 0 (they create + own the per-transaction connection). See docs/dbt-hooks.md.
	int32_t (*set_active_txn)(ArrowNetHandle handle, int64_t txn_id, int32_t join_only, char **err);

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
	// ClientContext (carries secret resolution); the managed side opens `path` via the ArrowNetHostServices
	// callbacks, reads its head + tail bytes, and returns a short human-readable result in *out (owned UTF-8,
	// freed via free_error). Proves C#->host FileSystem reads + opener/secret threading work end-to-end.
	// -------------------------------------------------------------------------
	int32_t (*fs_spike)(ArrowNetHandle opener, const char *path, char **out, char **err);

	// (delta_schema / delta_scan were removed at ABI v47 — the Delta reader is now a connection-free GLOBAL
	//  host-FS table function (kind='table' enumerated by list_global_functions), dispatched through the v29
	//  table-session path (table_bind / table_execute) with the active host-FS opener set via set_active_opener
	//  below. So a managed lakehouse reader needs NO bespoke C++/ABI — see docs/global-functions.md §host-FS.)

	// -------------------------------------------------------------------------
	// Ambient named-source registry (data-in by name). A managed component registers `name -> a fresh Arrow
	// stream factory`; arrownet_scan(name) + the replacement scan resolve a referenced name to that stream.
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
	// routes a 0 handle to the global registry by name), and dispatches execution via execute_scalar with
	// handle = 0. So global SCALAR functions add NO execution/schema ABI — only this enumeration entry.
	int32_t (*list_global_functions)(struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// Active host-FS opener (for connection-free GLOBAL host-FS table functions — lakehouse readers like
	// Delta/Iceberg). A global host-FS table reader does its IO through DuckDB's FileSystem (the
	// ArrowNetHostServices fs_* callbacks), which needs the calling operator's FileOpener/ClientContext to
	// resolve DuckDB secrets (az://, s3://, …). That context isn't an argument of the generic table_bind /
	// table_execute path, so — mirroring set_active_txn — the host records it in a per-thread ambient
	// IMMEDIATELY before each table-function bind + execution (same thread, synchronous), and the managed
	// host-FS binding reads it. `opener` is the operator's ClientContext (reinterpret_cast to a handle), valid
	// only for the duration of the call it precedes; NULL clears it. SQL/compute table functions ignore it.
	// Best-effort (a failure to set the ambient must not abort the statement). See docs/global-functions.md.
	int32_t (*set_active_opener)(ArrowNetHandle opener, char **err);

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
	int32_t (*onelake_open)(const char *path, const char *cred_json, ArrowNetHandle *out_file, int64_t *out_size,
	                        char **err);
	// Read nr_bytes at absolute offset `location` into buffer (host-owned). The managed side does the range GET.
	int32_t (*onelake_read)(ArrowNetHandle file, void *buffer, int64_t nr_bytes, int64_t location, char **err);
	// Close a handle from onelake_open. Safe with NULL/0.
	void (*onelake_close)(ArrowNetHandle file);
	// Glob `pattern` (an onelake:// path, possibly with wildcards) → JSON array of {path,size}. cred_json as above.
	int32_t (*onelake_glob)(const char *pattern, const char *cred_json, char **out_json, char **err);
	// FileExists(`path`). *out_exists = 0/1. cred_json as above.
	int32_t (*onelake_exists)(const char *path, const char *cred_json, int32_t *out_exists, char **err);

	// onelake:// WRITE (Phase-3 slice 2): a plain file write to OneLake through any DuckDB writer
	// (COPY … TO 'onelake://…', etc.) — NOT a Delta commit. Sequential (append-only), which is what
	// COPY and Azure DFS both do. onelake_open_write creates/overwrites the file; onelake_write appends;
	// onelake_close_write flushes + commits at the final length. cred_json as above.
	int32_t (*onelake_open_write)(const char *path, const char *cred_json, ArrowNetHandle *out_file, char **err);
	int32_t (*onelake_write)(ArrowNetHandle file, const void *buffer, int64_t nr_bytes, char **err);
	int32_t (*onelake_close_write)(ArrowNetHandle file, char **err);

	// -------------------------------------------------------------------------
	// Delta native-read via DuckDB's MultiFileReader (docs/multifile-delta.md Phase A). The host's
	// ArrowNetDeltaMultiFileReader (a DuckDB MultiFileReader that clones parquet_scan) asks the managed side for
	// the EXACT active data files of the Delta table at `path` — the snapshot `add` set, NOT a glob — as a JSON
	// array of objects: [{"path":"<uri>"[, "partitionValues":{..}, "deletionVector":"<base64>", "recordCount":N]}].
	// Paths are absolute URIs DuckDB's native reader can open (onelake:// for OneLake → native + ExternalFileCache);
	// the managed side uses the active host-FS opener (set_active_opener) to read the `_delta_log`. DuckDB's parquet
	// reader then reads the files; partition values + deletion vectors (later slices) attach per file. The `push`
	// arg carries pushed-down filters as JSON (empty ⇒ none) so the managed side prunes files by the Delta log
	// stats (static + dynamic filter pushdown); an empty result column of files is valid (everything pruned).
	int32_t (*delta_list_files)(const char *path, const char *push_json, char **out_json, char **err);
} ArrowNetVTable;

// -----------------------------------------------------------------------------
// Host services — function pointers the HOST provides TO the managed side (the reverse direction of the
// vtable). They let a managed component reach DuckDB's FileSystem so it can do secret-backed remote IO via
// DuckDB (one auth config — DuckDB secrets — shared with native reads). The host fills this struct and passes
// it to Bootstrap.Initialize; the managed side caches the pointers. SPIKE surface (open/size/read/close) —
// the foundation for a future C# lakehouse provider. A failing call returns non-zero and, when `err` is
// provided, sets *err to an owned UTF-8 message the managed side frees via `free_str`.
// -----------------------------------------------------------------------------
// Named Arrow inputs handed to host_query: the managed caller exports N Arrow streams + their names; the
// host registers each as a connection-scoped view (duckdb_arrow_scan) BEFORE running the query, so the SQL
// can reference them by name (`SELECT … FROM <name>`). The host consumes the streams during the query (which
// materializes), so they're done by the time host_query returns. count==0 / null => no inputs.
typedef struct ArrowNetHostInputs {
	int32_t count;
	const char **names;                  // count UTF-8 view names
	struct ArrowArrayStream **streams;   // count Arrow streams (parallel to names)
} ArrowNetHostInputs;

typedef struct ArrowNetHostServices {
	// Mirrors ARROWNET_ABI_VERSION so the managed side can reject a mismatched host services block.
	int32_t abi_version;
	// Open `path` for reading via DuckDB's FileSystem. `opener` is the opaque host FileOpener handle (its
	// ClientContext resolves secrets for az://, s3://, … ); valid only for the duration of the managed call
	// that received it. *out_file receives an opaque file handle (close via fs_close).
	int32_t (*fs_open_read)(ArrowNetHandle opener, const char *path, ArrowNetHandle *out_file, char **err);
	// File size in bytes.
	int32_t (*fs_size)(ArrowNetHandle file, int64_t *out_size, char **err);
	// Read `nr_bytes` at byte offset `location` into `buffer` (caller-allocated, in managed memory).
	int32_t (*fs_read)(ArrowNetHandle file, void *buffer, int64_t nr_bytes, int64_t location, char **err);
	// Close a file handle from fs_open_read. Safe with NULL.
	void (*fs_close)(ArrowNetHandle file);
	// Free an error string returned by the fs_* callbacks above.
	void (*free_str)(char *str);
	// Glob `pattern` (DuckDB glob, e.g. "<root>/_delta_log/*") via DuckDB's FileSystem (opener resolves
	// secrets). *out_json receives an owned UTF-8 JSON array of {"path":<string>,"size":<int64>} (freed via
	// free_str). Used by the managed lakehouse filesystem's directory listing.
	int32_t (*fs_glob)(ArrowNetHandle opener, const char *pattern, char **out_json, char **err);

	// Host query — run `sql` on a FRESH host DuckDB connection (its own ClientContext/transaction; never the
	// in-flight one, which is non-reentrant) and return the result as an ArrowArrayStream in *out. Lets a
	// managed component reuse the host engine (functions, readers, the catalog) over Arrow. Separate
	// transaction => committed-reads semantics. The result stream (and its connection) is owned by the
	// managed caller, which releases it when done. `params` (nullable) is a 1-row Arrow stream whose columns
	// bind POSITIONALLY to the statement's parameters (?, $1, …) via a prepared statement. `inputs` (nullable)
	// registers named Arrow sources as connection-scoped views before the query (data-in). See docs/host-query.md.
	int32_t (*host_query)(const char *sql, struct ArrowArrayStream *params, struct ArrowNetHostInputs *inputs,
	                      struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// WRITE surface (foundation for a Delta WRITE-back through the host FileSystem; see docs/delta-catalog.md).
	// `opener` is the calling operator's ClientContext (secret resolution), valid for the duration of the call.
	// -------------------------------------------------------------------------
	// Open `path` for sequential writing. `exclusive` (1/0): when 1, opens with EXCLUSIVE_CREATE
	// (WRITE|FILE_CREATE|EXCLUSIVE_CREATE) — the put-if-absent primitive: FAILS (non-zero + *err) if the file
	// already exists, which is honored on OneLake/ADLS and POSIX (and is how a Delta commit detects a conflict).
	// When 0, opens create-or-truncate (WRITE|FILE_CREATE_NEW). *out_file receives a write handle (close via
	// fs_close_write). NOTE: Azure DFS allows only sequential writes (or location 0).
	int32_t (*fs_open_write)(ArrowNetHandle opener, const char *path, int32_t exclusive, ArrowNetHandle *out_file,
	                         char **err);
	// Append `nr_bytes` from `buffer` to a write handle (sequential; the position advances).
	int32_t (*fs_write)(ArrowNetHandle file, const void *buffer, int64_t nr_bytes, char **err);
	// Flush + close a write handle from fs_open_write (surfaces flush errors, unlike fs_close). Frees the handle.
	int32_t (*fs_close_write)(ArrowNetHandle file, char **err);
	// Remove `path`. Does NOT error if it does not exist (TryRemoveFile semantics).
	int32_t (*fs_remove)(ArrowNetHandle opener, const char *path, char **err);
	// Create directory `path` (idempotent — ok if it already exists). On object stores directories are implicit;
	// on a local filesystem this materializes the parent (e.g. `_delta_log/`) before a write.
	int32_t (*fs_create_dir)(ArrowNetHandle opener, const char *path, char **err);
	// Remove directory `path` RECURSIVELY (all files + subdirectories). Idempotent — no error if it does not
	// exist. Maps to DuckDB's FileSystem::RemoveDirectory (recursive on local; on object stores it deletes every
	// object under the prefix). Used to DROP a Delta catalog table (its whole `<table>/` folder).
	int32_t (*fs_remove_dir)(ArrowNetHandle opener, const char *path, char **err);
	// Rename/move directory `src` to `dest`. Maps to DuckDB's FileSystem::MoveFile — atomic on a local
	// filesystem (a directory rename); object stores (S3/Azure DFS) generally do NOT implement it and throw.
	// Used to RENAME a Delta catalog table (move its whole `<table>/` folder; OneLake renames via the DFS SDK
	// directly instead, since Azure MoveFile is unimplemented).
	int32_t (*fs_move_dir)(ArrowNetHandle opener, const char *src, const char *dest, char **err);

	// Forward a managed .NET-logging event into DuckDB's internal logging (duckdb_logs), so the ILogger trace
	// (queries, filters, files) is visible in the engine's own log alongside the optional file sink. `level` is
	// the stable code 0 Trace / 1 Debug / 2 Info / 3 Warning / 4 Error / 5 Critical (ArrowNetLog.LevelCode);
	// `log_type` = the logger category, `message` = the formatted line. Best-effort (no error out); a no-op if
	// the host has no database/logger. Additive host-service entry (ABI v58).
	void (*host_log)(int32_t level, const char *log_type, const char *message);
} ArrowNetHostServices;

// Max serialized size of a spillable aggregate's per-group state (the inline, pointer-free
// state blob is this many bytes + a 4-byte length prefix). Serialize() must fit within it.
#define ARROWNET_AGG_SPILL_CAP 1024

#define ARROWNET_ABI_VERSION 58

// Signature of the managed bootstrap entry point loaded via hostfxr.
// Returns 0 on success; fills *vtable. `size` is sizeof(ArrowNetVTable) as seen
// by the C++ caller, allowing the managed side to guard against truncation.
typedef int32_t (*arrownet_bootstrap_fn)(ArrowNetVTable *vtable, int32_t size);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ARROWNET_ABI_H
