using System.Runtime.InteropServices;
using Apache.Arrow.C;

namespace ArrowNet.Bridge;

/// <summary>
/// Managed mirror of <c>ArrowNetVTable</c> in <c>src/include/arrownet/abi.h</c>.
/// Layout MUST stay in lockstep with the C struct (sequential, natural padding).
/// Filled in place by <see cref="Bootstrap.Initialize"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ArrowNetVTable
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

    // int32 get_metadata(void* handle, int32 kind, const char* arg1, const char* arg2, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, CArrowArrayStream*, byte**, int> GetMetadata;

    // int32 scan_table(void* handle, const char* schema, const char* table, const char* spec_json,
    //                  ArrowArrayStream* filter_values, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> ScanTable;

    // int32 create_table(void* handle, const char* schema, const char* table, ArrowArrayStream* columns,
    //                    int32 if_not_exists, const char* pk_columns, const char* unique_columns,
    //                    const char* defaults, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, int, byte*, byte*, byte*, byte**, int> CreateTable;

    // int32 drop_table(void* handle, const char* schema, const char* table, int32 if_exists, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, byte**, int> DropTable;

    // int32 create_schema(void* handle, const char* schema, int32 if_not_exists, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, int, byte**, int> CreateSchema;

    // int32 drop_schema(void* handle, const char* schema, int32 if_exists, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, int, byte**, int> DropSchema;

    // int32 alter_table(void* handle, const char* schema, const char* table, int32 alter_kind,
    //                   const char* arg1, const char* arg2, ArrowArrayStream* column, int32 flags, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, byte*, byte*, CArrowArrayStream*, int, byte**, int> AlterTable;

    // int32 begin/commit/rollback_transaction(void* handle, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte**, int> BeginTransaction;
    public delegate* unmanaged[Cdecl]<nint, byte**, int> CommitTransaction;
    public delegate* unmanaged[Cdecl]<nint, byte**, int> RollbackTransaction;

    // int32 insert_returning(void* handle, const char* schema, const char* table, ArrowArrayStream* in, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> InsertReturning;

    // int32 begin_bulk(void* handle, const char* schema, const char* table, int32 create_table, int32 replace,
    //                  int32 check_constraints, int64 txn_id, ArrowSchema* schema_in, void** out_session, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, int, long, CArrowSchema*, nint*, byte**, int> BeginBulk;

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
    //  table_bind / table_execute / table_close below.)

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

    // int32 table_bind(void* handle, const char* schema, const char* func, ArrowArrayStream* args /*nullable*/,
    //                  ArrowArrayStream* out_schema, int* supports_pushdown, void** out_binding, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, int*, nint*, byte**, int> TableBind;

    // int32 table_execute(void* binding, const char* spec_json, ArrowArrayStream* filter_values, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> TableExecute;

    // int32 table_close(void* binding, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte**, int> TableClose;

    // int32 list_settings(ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<CArrowArrayStream*, byte**, int> ListSettings;

    // int32 set_setting(const char* provider, const char* name, const char* value, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte**, int> SetSetting;

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

    // int32 set_active_opener(void* opener, char** err) — record the calling operator's ClientContext as the
    // active host-FS opener (per-thread ambient) so a global host-FS table function (a lakehouse reader)
    // resolves DuckDB secrets when reading through the host FileSystem. NULL clears it. Mirrors SetActiveTxn.
    public delegate* unmanaged[Cdecl]<nint, byte**, int> SetActiveOpener;
}

/// <summary>
/// Host services — function pointers the C++ host provides TO the managed side (reverse of the vtable), so a
/// managed component can reach DuckDB's FileSystem (secret-backed remote IO via DuckDB). Mirrors
/// <c>ArrowNetHostServices</c> in abi.h; the host fills it and passes it to <c>Bootstrap.Initialize</c>.
/// SPIKE surface (the foundation for a future C# lakehouse reader).
/// </summary>
public unsafe struct ArrowNetHostServices
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
    // int32 host_query(const char* sql, ArrowArrayStream* params, ArrowNetHostInputs* inputs,
    //                  ArrowArrayStream* out, char** err) — run sql on a fresh host connection (binding a 1-row
    // params batch positionally + registering named Arrow inputs as views first); result as Arrow (the managed
    // caller imports + releases the stream). See docs/host-query.md.
    public delegate* unmanaged[Cdecl]<byte*, CArrowArrayStream*, ArrowNetHostInputs*, CArrowArrayStream*, byte**, int> HostQuery;

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
}

/// <summary>Mirrors <c>ArrowNetHostInputs</c> in abi.h — named Arrow streams handed to host_query as data-in
/// (registered as connection-scoped views the SQL references by name).</summary>
public unsafe struct ArrowNetHostInputs
{
    public int Count;
    public byte** Names;                // Count UTF-8 view names
    public CArrowArrayStream** Streams; // Count Arrow streams (parallel to Names)
}

/// <summary>Mirrors <c>ArrowNetAlterKind</c> in abi.h.</summary>
public static class AlterKind
{
    public const int RenameTable = 0;
    public const int RenameColumn = 1;
    public const int AddColumn = 2;
    public const int DropColumn = 3;
    public const int ColumnType = 4;
    public const int SetNotNull = 5;
    public const int DropNotNull = 6;
    public const int SetDefault = 7;
    public const int DropDefault = 8;

    /// <summary>flags bit 0: the if-(not-)exists guard.</summary>
    public const int FlagIfExists = 1;
}

/// <summary>
/// Mirrors <c>ArrowNetMetadataKind</c> in abi.h: the kind of catalog metadata
/// requested through <c>get_metadata</c>.
/// </summary>
public static class MetadataKind
{
    public const int Schemas = 0;
    public const int Tables = 1;
    public const int Columns = 2;
    public const int RowId = 3;
    public const int RowCount = 4;
    public const int ColumnNdv = 5;
    public const int Functions = 6;
    public const int ServerInfo = 7;
    // Delta only: a table's commit history (version, timestamp, operation, operation_parameters). arg1=schema,
    // arg2=table. Surfaced by the arrownet_delta_snapshots(catalog, 'schema.table') table function.
    public const int Snapshots = 8;
}

internal static class ArrowNetStatus
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int InvalidArgument = 2;
    public const int NotFound = 3;
    public const int AlreadyExists = 4; // fs_open_write(exclusive): target already exists (commit conflict)
}
