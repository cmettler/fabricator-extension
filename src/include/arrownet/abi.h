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
	int32_t (*open_catalog)(const char *provider, const char *conn, ArrowNetHandle *out_handle, char **err);

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
	// `text_type` (nullable/empty => NVARCHAR(MAX)) overrides the SQL type used for
	// text (VARCHAR) columns — the `mssql_ctas_text_type` compatibility setting.
	int32_t (*create_table)(ArrowNetHandle handle, const char *schema, const char *table,
	                        struct ArrowArrayStream *columns, int32_t if_not_exists, const char *pk_columns,
	                        const char *unique_columns, const char *defaults, const char *text_type, char **err);

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
	int32_t (*begin_bulk)(ArrowNetHandle handle, const char *schema, const char *table, int32_t create_table,
	                      int32_t replace, int32_t check_constraints, struct ArrowSchema *schema_in,
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

	// Build a provider connection string from a secret's fields. The host reads the
	// secret's key/values (DuckDB SecretManager) and passes them as a flat JSON
	// object {"key":"value",...}; `provider` selects the backend whose connstr
	// format applies (empty => default). On success *out_connstr receives an owned
	// UTF-8 connection string (free it via free_error). This keeps all provider
	// connection-string/auth formatting in the managed backend (the C++ side has no
	// SqlClient knowledge); the result is then handed to open_catalog as usual.
	int32_t (*build_connection_string)(const char *provider, const char *fields_json, char **out_connstr,
	                                   char **err);

	// -------------------------------------------------------------------------
	// Custom scalar functions (Phase 3). Discovered SQL Server scalar UDFs are
	// registered as DuckDB catalog scalar functions; these resolve their argument
	// + return types and execute them, all over Arrow.
	// -------------------------------------------------------------------------

	// Zero-row Arrow stream whose schema = the function's input parameters (one
	// field per param, in order); used to register the DuckDB function's arg types.
	int32_t (*get_function_param_schema)(ArrowNetHandle handle, const char *schema, const char *func,
	                                     struct ArrowArrayStream *out, char **err);

	// Zero-row Arrow stream whose single field = the scalar function's return type.
	int32_t (*get_function_return_schema)(ArrowNetHandle handle, const char *schema, const char *func,
	                                      struct ArrowArrayStream *out, char **err);

	// Execute a scalar function over an input batch: `args` is an N-row stream whose
	// columns are the argument values (in param order); *out receives an N-row stream
	// with a single column = the per-row results (typed as the function's return).
	// The managed side consumes `args`.
	int32_t (*execute_scalar)(ArrowNetHandle handle, const char *schema, const char *func,
	                          struct ArrowArrayStream *args, struct ArrowArrayStream *out, char **err);

	// Zero-row Arrow stream whose schema = a table-valued function's output columns
	// (the result set, fixed/known from metadata). Used to bind the catalog table function.
	int32_t (*get_function_output_schema)(ArrowNetHandle handle, const char *schema, const char *func,
	                                      struct ArrowArrayStream *out, char **err);

	// Execute a table-valued function over its constant arguments: `args` is a 1-row
	// stream of the argument values (in param order; consumed by the managed side).
	// `spec_json` (nullable/empty => SELECT *) + `filter_values` (nullable) carry
	// projection + best-effort filter pushdown into the TVF, exactly like scan_table:
	// the managed side emits `SELECT <cols> FROM schema.func(@args) WHERE <filter>`.
	// *out receives the result rows.
	int32_t (*execute_table)(ArrowNetHandle handle, const char *schema, const char *func,
	                         struct ArrowArrayStream *args, const char *spec_json,
	                         struct ArrowArrayStream *filter_values, struct ArrowArrayStream *out, char **err);

	// Execute a stored procedure over its constant arguments: `args` is a 1-row stream
	// of the argument values (positional, in param order; consumed by the managed side);
	// *out receives the procedure's first result set. No projection/filter pushdown — a
	// proc's EXEC is not inline-wrappable, so DuckDB applies projection + filters locally.
	int32_t (*execute_proc)(ArrowNetHandle handle, const char *schema, const char *func,
	                        struct ArrowArrayStream *args, struct ArrowArrayStream *out, char **err);

	// -------------------------------------------------------------------------
	// Table-in-out (Phase 4). A session streams a TABLE in + a TABLE out, used to
	// apply a function once per input row (e.g. CROSS APPLY a TVF over a parameter
	// table). Parallel input branches feed ONE session; the host's injected
	// OperatorFinalize calls inout_finish once after all input is exhausted
	// (in_out_function_final fires per-branch, so it can't be the single signal);
	// the operator-state destructor calls inout_abort (error/cancel/LIMIT backstop).
	// -------------------------------------------------------------------------

	// Open a session. `input_schema` = the Arrow schema of the input table (its columns
	// are the function's positional parameters; the managed side consumes/releases it).
	// Returns an opaque session handle in *out_session.
	int32_t (*inout_open)(ArrowNetHandle handle, const char *schema, const char *func,
	                      struct ArrowSchema *input_schema, ArrowNetHandle *out_session, char **err);

	// Push one input chunk (consumed/released by the managed side); fill *out with the
	// output rows available so far (may be empty). Backpressured.
	int32_t (*inout_push)(ArrowNetHandle session, struct ArrowArray *in_chunk, struct ArrowArrayStream *out,
	                      char **err);

	// Signal input exhausted: drain + return all remaining output in *out. Idempotent.
	int32_t (*inout_finish)(ArrowNetHandle session, struct ArrowArrayStream *out, char **err);

	// Release the session (error/cancel/LIMIT backstop). Idempotent. Safe with nullptr.
	int32_t (*inout_abort)(ArrowNetHandle session, char **err);
} ArrowNetVTable;

#define ARROWNET_ABI_VERSION 23

// Signature of the managed bootstrap entry point loaded via hostfxr.
// Returns 0 on success; fills *vtable. `size` is sizeof(ArrowNetVTable) as seen
// by the C++ caller, allowing the managed side to guard against truncation.
typedef int32_t (*arrownet_bootstrap_fn)(ArrowNetVTable *vtable, int32_t size);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ARROWNET_ABI_H
