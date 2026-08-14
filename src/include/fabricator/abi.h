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

// -----------------------------------------------------------------------------
// Metadata kinds requested via FabricatorVTable::get_metadata. The managed side
// owns all provider SQL; the result is always an Arrow stream. For SCHEMAS /
// TABLES / ROWID the columns are UTF-8 strings (read with ReadStringTable); for
// COLUMNS the stream carries zero rows and its *schema* describes the table's
// columns (so DuckDB's Arrow->LogicalType inference is reused, no C++ mapping).
// -----------------------------------------------------------------------------
typedef enum {
	FABRICATOR_META_SCHEMAS = 0, // one column: user schema names
	FABRICATOR_META_TABLES = 1,  // three columns: schema, table, type ("BASE TABLE"|"VIEW")
	FABRICATOR_META_COLUMNS = 2, // zero rows; schema = the table's column layout
	FABRICATOR_META_ROWID = 3,   // one column: row-identity column names, in key order
	FABRICATOR_META_ROWCOUNT = 4, // one column, one row: approximate table row count (as text)
	FABRICATOR_META_COLUMN_NDV = 5, // two columns: column name, distinct-value estimate (NDV, as text)
	FABRICATOR_META_FUNCTIONS = 6,  // discovered routines: schema, name, kind, param_count, return_type
	FABRICATOR_META_SERVER_INFO = 7, // two columns: property, value — the detected server capability profile
	// Kinds 8-11 and 13-14 (SNAPSHOTS / CHANGES / TXN_VERSION / SET_TXN_VERSION / TBLPROPERTIES /
	// SET_TBLPROPERTIES) are DELETED (ABI v70): Delta features that wore C++-registered function fronts
	// (fabricator_delta_*) with string-packed payloads. They are catalog-bound functions in the `delta`
	// schema now — cat.delta.snapshots('s.t') etc., declared by the Delta providers with TYPED args
	// (Fabricator.Bridge/DeltaFunctions.cs). The gaps stay unassigned so a stale peer's kind cannot
	// silently alias a new one.
	FABRICATOR_META_VIRTUAL_COLUMNS = 12, // provider-declared VIRTUAL columns for a table (arg1 = schema,
	                                    // arg2 = table): two string columns (name, type-text). The host
	                                    // registers them as queryable-by-name virtual columns (not in
	                                    // SELECT *). Delta: __delta_row_id / __delta_row_commit_version
	                                    // (stable row tracking) under native_read + enableRowTracking;
	                                    // others: empty. Additive, no ABI bump; fetch is best-effort.
	FABRICATOR_META_CATALOG_MACROS = 15, // provider-declared CATALOG-BOUND DuckDB macros: three string columns
	                                    // (schema, name, create_sql) where create_sql is one complete CREATE
	                                    // MACRO statement, parsed by DuckDB's OWN parser host-side. Bound into
	                                    // the ATTACHed catalog's schema, so they resolve as db.schema.m(...).
	                                    // Deliberately its own KIND rather than a column on _FUNCTIONS: that
	                                    // stream is built as provider SQL and executed on the server (see
	                                    // SqlServerCatalog.FunctionsMetadataSql), and a macro body is a purely
	                                    // LOCAL declaration — it must not be embedded in a T-SQL literal, sent
	                                    // to the server and read back, nor vanish when the server is
	                                    // unreachable. Adding a kind is additive => no ABI bump. Fetch is
	                                    // best-effort: a provider that does not serve it registers no macros.
} FabricatorMetadataKind;

// -----------------------------------------------------------------------------
// ALTER TABLE variants passed to FabricatorVTable::alter_table. The managed side
// generates the provider DDL. `arg1`/`arg2` carry names; for ADD_COLUMN /
// COLUMN_TYPE the new column's type travels as a single-field zero-row Arrow
// schema in the `column` stream. `flags` bit 0 is the if-(not-)exists guard.
// -----------------------------------------------------------------------------
typedef enum {
	FABRICATOR_ALTER_RENAME_TABLE = 0,  // arg1 = new table name
	FABRICATOR_ALTER_RENAME_COLUMN = 1, // arg1 = old column name, arg2 = new column name
	FABRICATOR_ALTER_ADD_COLUMN = 2,    // arg1 = column name; `column` carries its type; flag0 = if_not_exists
	FABRICATOR_ALTER_DROP_COLUMN = 3,   // arg1 = column name; flag0 = if_exists
	FABRICATOR_ALTER_COLUMN_TYPE = 4,   // arg1 = column name; `column` carries the new type
	FABRICATOR_ALTER_SET_NOT_NULL = 5,  // arg1 = column name (managed side restates the current type)
	FABRICATOR_ALTER_DROP_NOT_NULL = 6, // arg1 = column name
	FABRICATOR_ALTER_SET_DEFAULT = 7,   // arg1 = column name; arg2 = "-" (DEFAULT NULL) or "b"+base64(literal)
	FABRICATOR_ALTER_DROP_DEFAULT = 8,  // arg1 = column name
	// Nested STRUCT-field evolution (DuckDB `ALTER TABLE t ADD/DROP/RENAME COLUMN s.f ...`). Field paths
	// cross as a JSON array of segments (names may contain dots). Additive enum values — no ABI bump.
	FABRICATOR_ALTER_ADD_FIELD = 9,     // arg1 = JSON path of the CONTAINING struct; `column` carries the new
	                                  // field (name + type); flag0 = if_not_exists
	FABRICATOR_ALTER_DROP_FIELD = 10,   // arg1 = JSON full path of the field; flag0 = if_exists
	FABRICATOR_ALTER_RENAME_FIELD = 11, // arg1 = JSON full path of the field; arg2 = new field name

	// ALTER TABLE t SET SORTED BY (a, b) / RESET SORTED BY — declares/re-keys/removes the table's
	// clustering (Delta: the delta.clustering domain + the fabricator.sortedBy ordered-write property).
	// SET/RESET PARTITIONED BY crosses too so each provider errors meaningfully (none supports it yet).
	FABRICATOR_ALTER_SET_SORTED_BY = 12,      // arg1 = JSON array of column names ([] = RESET)
	FABRICATOR_ALTER_SET_PARTITIONED_BY = 13, // arg1 = JSON array of column names ([] = RESET)
} FabricatorAlterKind;

#define FABRICATOR_ALTER_FLAG_IF_EXISTS 1

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
	// schema_filter/table_filter in get_metadata and stores isolation_level for table-in-out sessions). See
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

	// Discover provider metadata. `kind` is an FabricatorMetadataKind; `arg1`/`arg2`
	// carry the schema/table name when the kind needs them (NULL otherwise). The
	// result is exported into *out as an Arrow stream (see FabricatorMetadataKind).
	// Keeps all provider catalog SQL (sys.*, PK/unique-index discovery) in C#.
	int32_t (*get_metadata)(FabricatorHandle handle, int32_t kind, const char *arg1, const char *arg2,
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
	int32_t (*scan_table)(FabricatorHandle handle, const char *schema, const char *table, const char *spec_json,
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

	// DDL: alter a table. `alter_kind` is an FabricatorAlterKind; `arg1`/`arg2` are
	// names (per kind). For ADD_COLUMN / COLUMN_TYPE the new column's type travels
	// as a single-field zero-row Arrow schema in `column` (NULL otherwise; the
	// managed side consumes/releases it when present). `flags` bit 0 is the
	// if-(not-)exists guard. The managed side generates the provider ALTER.
	int32_t (*alter_table)(FabricatorHandle handle, const char *schema, const char *table, int32_t alter_kind,
	                       const char *arg1, const char *arg2, struct ArrowArrayStream *column, int32_t flags,
	                       char **err);

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

	// *out receives the Arrow schema whose single field = the scalar function's return type (a bare ArrowSchema).
	int32_t (*get_function_return_schema)(FabricatorHandle handle, const char *schema, const char *func,
	                                      struct ArrowSchema *out, char **err);

	// Execute a scalar function over an input batch: `args` is an N-row stream whose
	// columns are the argument values (in param order); *out receives an N-row stream
	// with a single column = the per-row results (typed as the function's return).
	// The managed side consumes `args`.
	int32_t (*execute_scalar)(FabricatorHandle handle, const char *schema, const char *func,
	                          struct ArrowArrayStream *args, struct ArrowArrayStream *out, char **err);

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
	// calls it IBoundTableFunction.MapResultByName, which is what it has always meant (renamed there
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
	int32_t (*tablefn_execute)(FabricatorHandle binding, const char *spec_json,
	                         struct ArrowArrayStream *filter_values, struct ArrowArrayStream *out, char **err);

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
	// routes a 0 handle to the global registry by name), and dispatches execution via execute_scalar with
	// handle = 0. So global SCALAR functions add NO execution/schema ABI — only this enumeration entry.
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
	// Delta native-read via DuckDB's MultiFileReader (docs/multifile-delta.md Phase A). The host's
	// FabricatorDeltaMultiFileReader (a DuckDB MultiFileReader that clones parquet_scan) asks the managed side for
	// the EXACT active data files of the Delta table at `path` — the snapshot `add` set, NOT a glob — as a JSON
	// array of objects: [{"path":"<uri>"[, "partitionValues":{..}, "deletionVector":"<base64>", "recordCount":N]}].
	// Paths are absolute URIs DuckDB's native reader can open (onelake:// for OneLake → native + ExternalFileCache);
	// the managed side uses the active host-FS opener (set_active_opener) to read the `_delta_log`. DuckDB's parquet
	// reader then reads the files; partition values + deletion vectors (later slices) attach per file. The `push`
	// arg carries pushed-down filters as JSON (empty ⇒ none) so the managed side prunes files by the Delta log
	// stats (static + dynamic filter pushdown); an empty result column of files is valid (everything pruned).
	int32_t (*delta_list_files)(const char *path, const char *push_json, char **out_json, char **err);

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
// host registers each as a connection-scoped view (duckdb_arrow_scan) BEFORE running the query, so the SQL
// can reference them by name (`SELECT … FROM <name>`). The host consumes the streams during the query (which
// materializes), so they're done by the time host_query returns. count==0 / null => no inputs.
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
	int32_t (*host_query)(const char *sql, struct ArrowArrayStream *params, struct FabricatorHostInputs *inputs,
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
} FabricatorHostServices;

// Max serialized size of a spillable aggregate's per-group state (the inline, pointer-free
// state blob is this many bytes + a 4-byte length prefix). Serialize() must fit within it.
#define FABRICATOR_AGG_SPILL_CAP 1024

#define FABRICATOR_ABI_VERSION 71

// Signature of the managed bootstrap entry point loaded via hostfxr.
// Returns 0 on success; fills *vtable. `size` is sizeof(FabricatorVTable) as seen
// by the C++ caller, allowing the managed side to guard against truncation.
typedef int32_t (*fabricator_bootstrap_fn)(FabricatorVTable *vtable, int32_t size);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // FABRICATOR_ABI_H
