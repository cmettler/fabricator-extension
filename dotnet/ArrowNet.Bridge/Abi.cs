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

    // int32 open_catalog(const char* provider, const char* conn, void** out_handle, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, nint*, byte**, int> OpenCatalog;

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
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, int, byte*, byte*, byte*, byte*, byte**, int> CreateTable;

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
    //                  int32 check_constraints, ArrowSchema* schema_in, void** out_session, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, int, CArrowSchema*, nint*, byte**, int> BeginBulk;

    // int32 push_batch(void* session, ArrowArray* batch, char** err)
    public delegate* unmanaged[Cdecl]<nint, CArrowArray*, byte**, int> PushBatch;

    // int32 complete_bulk(void* session, int32 abort, int64* affected, char** err)
    public delegate* unmanaged[Cdecl]<nint, int, long*, byte**, int> CompleteBulk;

    // int32 build_connection_string(const char* provider, const char* fields_json, char** out_connstr, char** err)
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte**, byte**, int> BuildConnectionString;

    // int32 get_function_param_schema(void* handle, const char* schema, const char* func, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, byte**, int> GetFunctionParamSchema;

    // int32 get_function_return_schema(void* handle, const char* schema, const char* func, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, byte**, int> GetFunctionReturnSchema;

    // int32 execute_scalar(void* handle, const char* schema, const char* func, ArrowArrayStream* args, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> ExecuteScalar;

    // int32 get_function_output_schema(void* handle, const char* schema, const char* func, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, byte**, int> GetFunctionOutputSchema;

    // int32 execute_table(void* handle, const char* schema, const char* func, ArrowArrayStream* args,
    //                     const char* spec_json, ArrowArrayStream* filter_values, ArrowArrayStream* out, char** err)
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, CArrowArrayStream*, byte*, CArrowArrayStream*, CArrowArrayStream*, byte**, int> ExecuteTable;
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
}

internal static class ArrowNetStatus
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int InvalidArgument = 2;
    public const int NotFound = 3;
}
