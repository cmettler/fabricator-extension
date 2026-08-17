using System.Runtime.InteropServices;
using Apache.Arrow.C;

namespace Fabricator.Bridge;

/// <summary>
/// Managed mirror of <c>FabricatorVTable</c> in <c>src/include/fabricator/abi.h</c>.
/// Layout MUST stay in lockstep with the C struct (sequential, natural padding).
/// Filled in place by <see cref="Bootstrap.Initialize"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FabricatorVTable
{
    public int AbiVersion;

    // int32 open_catalog(const char* provider, const char* conn, const char* options_json, void** out_handle, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, nint*, byte**, int> OpenCatalog;

    // void close_catalog(void* handle)
    public delegate* unmanaged[Cdecl]<nint, void> CloseCatalog;

    // int32 execute_query(void* handle, const char* sql, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, CArrowArrayStream*, byte**, int> ExecuteQuery;

    // void free_error(char* err)
    public delegate* unmanaged[Cdecl]<byte*, void> FreeError;

    // int32 execute_dml(void* handle, const char* sql, int64* affected, int32* schema_may_change, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, long*, int*, byte**, int> ExecuteDml;

    // int32 bulk_insert(void* handle, const char* schema, const char* table,
    //                   int32 create_table, int32 replace, ArrowArrayStream* in, int64* affected, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, CArrowArrayStream*, long*, byte**, int> BulkInsert;

    // int32 execute_delete(void* handle, const char* schema, const char* table, ArrowArrayStream* keys, int64* affected, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, long*, byte**, int> ExecuteDelete;

    // int32 execute_update(void* handle, const char* schema, const char* table, int32 set_count, ArrowArrayStream* data, int64* affected, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, CArrowArrayStream*, long*, byte**, int> ExecuteUpdate;

    // (get_metadata / scan_table were removed at ABI v72 — the 16-kind multiplexer and the name-pair scan
    //  are replaced by the dedicated catalog_* discovery entries and the table_* session at the end of this
    //  struct. Removing mid-struct slots shifts every later field, which the abi_version check makes loud —
    //  the v30/v31/v47 precedent.)

    // int32 create_table(void* handle, const char* schema, const char* table, ArrowArrayStream* columns,
    //                    int32 if_not_exists, const char* pk_columns, const char* unique_columns,
    //                    const char* defaults, const char* partition_columns, const char* sort_columns,
    //                    const char* identity_columns, const char* options_json, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, int, byte*, byte*, byte*, byte*, byte*, byte*, byte*, byte**, int> CreateTable;

    // int32 drop_table(void* handle, const char* schema, const char* table, int32 if_exists, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, byte**, int> DropTable;

    // int32 create_schema(void* handle, const char* schema, int32 if_not_exists, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, int, byte**, int> CreateSchema;

    // int32 drop_schema(void* handle, const char* schema, int32 if_exists, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, int, byte**, int> DropSchema;

    // (alter_table was removed at ABI v74 — it is TableAlter on the table session, at the end of this struct.)

    // int32 begin/commit/rollback_transaction(void* handle, char** err)
    public delegate* unmanaged[Cdecl]<nint, int, byte**, int> BeginTransaction;
    public delegate* unmanaged[Cdecl]<nint, byte**, int> CommitTransaction;
    public delegate* unmanaged[Cdecl]<nint, byte**, int> RollbackTransaction;

    // int32 insert_returning(void* handle, const char* schema, const char* table, ArrowArrayStream* in, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> InsertReturning;

    // int32 begin_bulk(void* handle, const char* schema, const char* table, int32 create_table, int32 replace,
    //                  int32 check_constraints, int64 txn_id, ArrowSchema* schema_in, const char* partition_columns,
    //                  const char* sort_columns, const char* schema_mode, int32 partition_overwrite,
    //                  const char* options_json, void** out_session, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, int, long, CArrowSchema*, byte*, byte*, byte*, int, byte*, nint*, byte**, int> BeginBulk;

    // int32 push_batch(void* session, ArrowArray* batch, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, byte**, int> PushBatch;

    // int32 complete_bulk(void* session, int32 abort, int64* affected, char** err)
    public delegate* unmanaged[Cdecl]<nint, int, long*, byte**, int> CompleteBulk;

    // int32 build_connection_string(const char* provider, const char* fields_json, char** out_connstr, char** err)
    // int32 build_connection_string(const char* provider, const char* secret_type, const char* fields_json,
    //                               const char* base_connstr, char** out_connstr, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, byte**, byte**, int> BuildConnectionString;

    // int32 get_function_param_schema(void* handle, const char* schema, const char* func, ArrowSchema* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowSchema*, byte**, int> GetFunctionParamSchema;

    // int32 get_function_return_schema(void* handle, const char* schema, const char* func, ArrowSchema* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowSchema*, byte**, int> GetFunctionReturnSchema;

    // int32 execute_scalar(void* handle, const char* schema, const char* func, ArrowArrayStream* args, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> ExecuteScalar;

    // int32 get_function_output_schema(void* handle, const char* schema, const char* func, ArrowArrayStream* args, ArrowSchema* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowSchema*, byte**, int> GetFunctionOutputSchema;

    // (execute_table / execute_proc were removed at ABI v30 — superseded by the table-function session
    //  tablefn_bind / tablefn_execute / tablefn_close below.)

    // (inout_open / inout_push / inout_finish / inout_abort were removed at ABI v31 — every `_each` form now
    //  runs on the streaming exchange: inout_bind / inout_exchange_open / inout_bind_close below.)

    // int32 agg_open(void* handle, const char* schema, const char* func, void** out_session, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, nint*, byte**, int> AggOpen;

    // int32 agg_update(void* session, ArrowArray* batch, char** err)  -- batch = [int64 id ++ params]
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, byte**, int> AggUpdate;

    // int32 agg_combine(void* session, ArrowArray* batch, char** err) -- batch = [int64 target, int64 source]
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, byte**, int> AggCombine;

    // int32 agg_finalize(void* session, ArrowArray* ids, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, CArrowArrayStream*, byte**, int> AggFinalize;

    // int32 agg_destroy(void* session, ArrowArray* ids, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, byte**, int> AggDestroy;

    // int32 agg_close(void* session, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte**, int> AggClose;

    // int32 agg_update_spill(void* session, ArrowArray* group_states, ArrowArray* batch, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, CArrowArray*, CArrowArrayStream*, byte**, int> AggUpdateSpill;

    // int32 agg_combine_spill(void* session, ArrowArray* target_states, ArrowArray* source_states, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, CArrowArray*, CArrowArrayStream*, byte**, int> AggCombineSpill;

    // int32 agg_finalize_spill(void* session, ArrowArray* states, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, CArrowArrayStream*, byte**, int> AggFinalizeSpill;

    // int32 inout_bind(void* handle, const char* schema, const char* func, ArrowArrayStream* args /*nullable*/,
    //                  ArrowSchema* input_schema, ArrowArrayStream* out_schema, void** out_binding, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowSchema*, CArrowArrayStream*, nint*, byte**, int> InOutBind;

    // int32 inout_exchange_open(void* binding, ArrowArrayStream* input, ArrowArrayStream* output, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, CArrowArrayStream*, byte**, int> InOutExchangeOpen;

    // int32 inout_bind_close(void* binding, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte**, int> InOutBindClose;

    // int32 tablefn_bind(void* handle, const char* schema, const char* func, ArrowArrayStream* args /*nullable*/,
    //                  ArrowArrayStream* out_schema, int* supports_pushdown, void** out_binding, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, int*, nint*, byte**, int> TableFnBind;

    // int32 tablefn_execute(void* binding, const char* spec_json, ArrowArrayStream* filter_values, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> TableFnExecute;

    // int32 tablefn_close(void* binding, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte**, int> TableFnClose;

    // int32 list_settings(ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<CArrowArrayStream*, byte**, int> ListSettings;

    // int32 set_setting(int64 session, const char* provider, const char* name, const char* value, char** err)
    public delegate* unmanaged[Cdecl]<long, byte*, byte*, byte*, byte**, int> SetSetting;

    // int32 set_active_txn(void* handle, int64 txn_id, int32 join_only, char** err)
    public delegate* unmanaged[Cdecl]<nint, long, int, byte**, int> SetActiveTxn;

    // int32 list_secret_fields(ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<CArrowArrayStream*, byte**, int> ListSecretFields;

    // SPIKE: int32 fs_spike(void* opener, const char* path, char** out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte**, byte**, int> FsSpike;

    // (delta_schema / delta_scan removed at ABI v47 — the Delta reader is a connection-free GLOBAL host-FS
    //  table function dispatched through the table-session path; see SetActiveOpener below.)

    // int32 open_named_input(const char* name, ArrowArrayStream* out, char** err) — fresh stream for a
    // registered ambient source; int32 named_input_exists(const char* name, int32* out_exists, char** err).
    public delegate* unmanaged[Cdecl]<byte*, CArrowArrayStream*, byte**, int> OpenNamedInput;
    public delegate* unmanaged[Cdecl]<byte*, int*, byte**, int> NamedInputExists;

    // int32 list_global_functions(ArrowArrayStream* out, char** err) — the provider-union of connection-free
    // global functions (metadata: name/kind/param_count/return_type), enumerated once at extension load.
    public delegate* unmanaged[Cdecl]<CArrowArrayStream*, byte**, int> ListGlobalFunctions;

    // int32 set_active_opener(void* opener, int64 session, char** err) — record the calling operator's
    // ClientContext as the active host-FS opener (per-thread ambient) so a global host-FS table function (a
    // lakehouse reader) resolves DuckDB secrets when reading through the host FileSystem. NULL clears it.
    // Mirrors SetActiveTxn. `session` (ABI v69) is the DuckDB connection whose session-scoped provider
    // settings apply — NOT always the opener: the commit flush and rollback pass their own short-lived
    // connection as the opener while keeping the USER's connection as the session.
    public delegate* unmanaged[Cdecl]<nint, long, byte**, int> SetActiveOpener;

    // onelake:// FileSystem forward callbacks (Phase-3): the C++ onelake FS subsystem forwards read ops here to
    // the managed Azure DataLake SDK. cred_json = the azure secret fields the host resolved from the opener.
    // int32 onelake_open(char* path, char* cred_json, void** out_file, int64* out_size, char** err)
    // int32 onelake_open(char* path, char* cred_json, int64 known_size, void** out_file, int64* out_size,
    //                    char** out_etag, int64* out_modified_ms, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, long, nint*, long*, byte**, long*, byte**, int> OneLakeOpen;
    // int32 onelake_read(void* file, void* buffer, int64 nr_bytes, int64 location, char** err)
    public delegate* unmanaged[Cdecl]<nint, void*, long, long, byte**, int> OneLakeRead;
    // void onelake_close(void* file)
    public delegate* unmanaged[Cdecl]<nint, void> OneLakeClose;
    // int32 onelake_glob(char* pattern, char* cred_json, char** out_json, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte**, byte**, int> OneLakeGlob;
    // int32 onelake_exists(char* path, char* cred_json, int32* out_exists, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, int*, byte**, int> OneLakeExists;
    // onelake:// WRITE (slice 2): create/overwrite a plain OneLake file (COPY … TO 'onelake://…').
    // `exclusive` != 0 => put-if-absent (ADLS conditional create) — EXCLUSIVE_CREATE semantics (v61).
    // int32 onelake_open_write(char* path, char* cred_json, int32 exclusive, void** out_file, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, int, nint*, byte**, int> OneLakeOpenWrite;
    // int32 onelake_write(void* file, void* buffer, int64 nr_bytes, char** err)
    public delegate* unmanaged[Cdecl]<nint, void*, long, byte**, int> OneLakeWrite;
    // int32 onelake_close_write(void* file, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte**, int> OneLakeCloseWrite;
    // Delta native-read (MultiFileList): the active files of the Delta table at `path` as a JSON array
    // [{"path":"<uri>", ...}]. `push_json` = pushed filters (empty ⇒ none). See abi.h delta_list_files.
    // int32 delta_list_files(char* path, char* push_json, char** out_json, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte**, byte**, int> DeltaListFiles;
    // Delete a single onelake:// file (DataLakeFileClient.DeleteIfExists — idempotent). Appended at v61 so
    // the onelake:// FileSystem supports RemoveFile (engineered-wood's rename emulation deletes its source).
    // int32 onelake_remove(char* path, char* cred_json, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte**, int> OneLakeRemove;

    // Atomic onelake:// file rename via the DFS native rename (overwrites the destination — MoveFile
    // semantics; backs DuckDB's COPY tmp-file staging on onelake://). Appended at v64.
    // int32 onelake_move(char* src, char* dest, char* cred_json, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte**, int> OneLakeMove;

    // SQL-GENERATING table function (v68): generate the replacement SQL for one call — the managed side of
    // DuckDB's bind_replace. handle == 0 => the global registry (schema/catalog_name empty); non-zero => the
    // catalog's, with catalog_name = the ATTACH alias. `args` (nullable) = the 1-row constant-arg batch.
    // int32 generate_table_sql(void* handle, char* schema, char* func, char* catalog_name,
    //                          ArrowArrayStream* args, char** out_sql, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*, CArrowArrayStream*, byte**, byte**, int>
        GenerateTableSql;

    // int32 clear_session_settings(int64 session, char** err) (v69) — drop a closed DuckDB connection's
    // session-scoped settings. Appended at the vtable end so no earlier slot shifts.
    public delegate* unmanaged[Cdecl]<long, byte**, int> ClearSessionSettings;

    // int32 get_capabilities(void* handle, char** out_json, char** err) (v71) — ONE flat JSON object of
    // the catalog's host-consumed capability booleans (absent key = false), read once at ATTACH. The typed
    // replacement for the host grepping the diagnostic kind-7 (property, value) stream. Appended at the
    // vtable end so no earlier slot shifts.
    public delegate* unmanaged[Cdecl]<nint, byte**, byte**, int> GetCapabilities;

    // ---- catalog discovery (ABI v72) — the dedicated typed LIST entries that replaced get_metadata's
    // kind multiplexer. Arrow streams stay the carrier (the right tool for lists); what died is the kind
    // int, the per-provider `_ =>` fallback shapes, and the name-pair-per-call transport for tables.
    // int32 catalog_schemas(void* handle, ArrowArrayStream* out, char** err) — 1 utf8 col: schema_name
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, byte**, int> CatalogSchemas;
    // int32 catalog_tables(void* handle, ArrowArrayStream* out, char** err) — 3 utf8: schema, table, type
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, byte**, int> CatalogTables;
    // int32 catalog_functions(void* handle, ArrowArrayStream* out, char** err) — 5: schema, name, kind,
    // param_count, return_type (the host reads the first three)
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, byte**, int> CatalogFunctions;
    // int32 catalog_macros(void* handle, ArrowArrayStream* out, char** err) — 3 utf8: schema, name, create_sql
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, byte**, int> CatalogMacros;
    // int32 catalog_server_info(void* handle, ArrowArrayStream* out, char** err) — 2 utf8: property, value.
    // DIAGNOSTIC only (fabricator_server_info()); the host consumes get_capabilities (v71) instead.
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, byte**, int> CatalogServerInfo;

    // ---- the table session (ABI v72) — mirrors tablefn_*; see abi.h for the full contract.
    // int32 table_open(void* handle, const char* schema, const char* table, const char* at_unit,
    //                  const char* at_value, void** out_table, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*, byte*, nint*, byte**, int> TableOpen;
    // int32 table_schema(void* table, ArrowArrayStream* out, char** err) — zero-row stream whose SCHEMA is
    // the table's column layout; NOT_FOUND status = established absence.
    public delegate* unmanaged[Cdecl]<nint, CArrowArrayStream*, byte**, int> TableSchema;
    // int32 table_info(void* table, char** out_json, char** err) (v73) — ONE typed JSON doc:
    // {"rowid":[...], "virtual":[{"name":..,"type":..}, ...]}; owned UTF-8, host frees via free_error.
    public delegate* unmanaged[Cdecl]<nint, byte**, byte**, int> TableInfo;
    // int32 table_stats(void* table, char** out_json, char** err) (v73) — ONE typed JSON doc:
    // {"row_count":N, "ndv":{"col":N, ...}}; row_count absent = unknown. Lazy by design (never called
    // during enumeration). Owned UTF-8, host frees via free_error.
    public delegate* unmanaged[Cdecl]<nint, byte**, byte**, int> TableStats;
    // int32 table_scan(void* table, const char* spec_json, ArrowArrayStream* filter_values,
    //                  ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> TableScan;
    // int32 table_alter(void* table, const char* alter_json, ArrowArrayStream* column, char** err) (v74) —
    // ONE typed doc naming its variant (see AlterTableSpec / abi.h), plus the `column` TYPE CHANNEL, which
    // stays an Arrow field because a VARIANT rides field METADATA no type name could carry.
    public delegate* unmanaged[Cdecl]<nint, byte*, CArrowArrayStream*, byte**, int> TableAlter;
    // void table_close(void* table)
    public delegate* unmanaged[Cdecl]<nint, void> TableClose;
}

/// <summary>
/// Host services — function pointers the C++ host provides TO the managed side (reverse of the vtable), so a
/// managed component can reach DuckDB's FileSystem (secret-backed remote IO via DuckDB). Mirrors
/// <c>FabricatorHostServices</c> in abi.h; the host fills it and passes it to <c>Bootstrap.Initialize</c>.
/// SPIKE surface (the foundation for a future C# lakehouse reader).
/// </summary>
public unsafe struct FabricatorHostServices
{
    public int AbiVersion;
    // int32 fs_open_read(void* opener, const char* path, void** out_file, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, nint*, byte**, int> FsOpenRead;
    // int32 fs_size(void* file, int64* out_size, char** err)
    public delegate* unmanaged[Cdecl]<nint, long*, byte**, int> FsSize;
    // int32 fs_read(void* file, void* buffer, int64 nr_bytes, int64 location, char** err)
    public delegate* unmanaged[Cdecl]<nint, void*, long, long, byte**, int> FsRead;
    // void fs_close(void* file)
    public delegate* unmanaged[Cdecl]<nint, void> FsClose;
    // void free_str(char* str)
    public delegate* unmanaged[Cdecl]<byte*, void> FreeStr;
    // int32 fs_glob(void* opener, const char* pattern, char** out_json, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte**, byte**, int> FsGlob;
    // int32 host_query(const char* sql, ArrowArrayStream* params, FabricatorHostInputs* inputs,
    //                  ArrowArrayStream* out, void** out_interrupt, char** err) — run sql on a fresh host
    // connection (binding a 1-row params batch positionally + registering named Arrow inputs as views first);
    // result as Arrow (the managed caller imports + releases the stream). out_interrupt (nullable) receives an
    // opaque cancellation handle for the query's fresh ClientContext (v66). See docs/host-query.md.
    public delegate* unmanaged[Cdecl]<byte*, CArrowArrayStream*, FabricatorHostInputs*, CArrowArrayStream*, void**, byte**, int> HostQuery;

    // ---- WRITE surface (Delta write-back foundation; see docs/delta-catalog.md) ----
    // int32 fs_open_write(void* opener, const char* path, int32 exclusive, void** out_file, char** err)
    // exclusive=1 => EXCLUSIVE_CREATE (put-if-absent: fails if the file exists); 0 => create-or-truncate.
    public delegate* unmanaged[Cdecl]<nint, byte*, int, nint*, byte**, int> FsOpenWrite;
    // int32 fs_write(void* file, const void* buffer, int64 nr_bytes, char** err) — sequential append.
    public delegate* unmanaged[Cdecl]<nint, void*, long, byte**, int> FsWrite;
    // int32 fs_close_write(void* file, char** err) — flush + close + free the write handle.
    public delegate* unmanaged[Cdecl]<nint, byte**, int> FsCloseWrite;
    // int32 fs_remove(void* opener, const char* path, char** err) — delete (no error if missing).
    public delegate* unmanaged[Cdecl]<nint, byte*, byte**, int> FsRemove;
    // int32 fs_create_dir(void* opener, const char* path, char** err) — idempotent mkdir.
    public delegate* unmanaged[Cdecl]<nint, byte*, byte**, int> FsCreateDir;
    // int32 fs_remove_dir(void* opener, const char* path, char** err) — recursive directory delete (idempotent).
    public delegate* unmanaged[Cdecl]<nint, byte*, byte**, int> FsRemoveDir;
    // int32 fs_move_dir(void* opener, const char* src, const char* dest, char** err) — directory rename/move
    // (FileSystem::MoveFile; atomic on local, unimplemented on object stores).
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte**, int> FsMoveDir;
    // void host_log(int32 level, const char* log_type, const char* message) — forward an ILogger event into
    // DuckDB's internal logging (duckdb_logs). Best-effort; no error out. Additive (ABI v58).
    public delegate* unmanaged[Cdecl]<int, byte*, byte*, void> HostLog;
    // int32 is_interrupted(void* opener) — read the calling operator's ClientContext::interrupted (Ctrl+C /
    // query timeout). Returns 1 if interrupted else 0 (0 for a null opener). Polled by InterruptScope to cancel
    // long-running C# I/O. Additive (ABI v65). See docs/cancellation.md.
    public delegate* unmanaged[Cdecl]<nint, int> IsInterrupted;
    // void host_query_interrupt(void* interrupt_handle) — trip the fresh ClientContext behind a host_query
    // result (thread-safe; callable any time, a no-op after the query ended). Additive (ABI v66).
    public delegate* unmanaged[Cdecl]<void*, void> HostQueryInterrupt;
    // void host_query_interrupt_free(void* interrupt_handle) — free the handle, exactly once, after any
    // in-flight interrupt callback has been waited out (registration disposed first). Additive (ABI v66).
    public delegate* unmanaged[Cdecl]<void*, void> HostQueryInterruptFree;
}

/// <summary>Mirrors <c>FabricatorHostInputs</c> in abi.h — named Arrow streams handed to host_query as data-in
/// (registered as connection-scoped views the SQL references by name).</summary>
public unsafe struct FabricatorHostInputs
{
    public int Count;
    public byte** Names;                // Count UTF-8 view names
    public CArrowArrayStream** Streams; // Count Arrow streams (parallel to Names)
}

// (AlterKind was deleted at ABI v74 with alter_table itself. Its fourteen ints are now the "kind" strings
//  of the table_alter doc, and the flags bit is the doc's own if_not_exists / if_exists key — parsed once
//  into the typed AlterTableSpec (Fabricator.Abstractions) instead of read positionally by each provider.)

// (MetadataKind was deleted at ABI v72 together with get_metadata itself: catalog discovery has dedicated
//  typed entries (CatalogSchemas/Tables/Functions/Macros/ServerInfo — the shapes those kinds carried keep
//  their column layouts, minus the kind int), and the per-table kinds live on the table_* session over the
//  ITableBinding object model. Kind history, incl. the v70 deletion of 8-11/13-14, is in docs/abi-history.md.)

internal static class FabricatorStatus
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int InvalidArgument = 2;
    public const int NotFound = 3;
    public const int AlreadyExists = 4; // fs_open_write(exclusive): target already exists (commit conflict)
}
