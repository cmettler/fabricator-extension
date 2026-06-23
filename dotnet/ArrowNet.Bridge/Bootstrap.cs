using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.C;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace ArrowNet.Bridge;

/// <summary>
/// Native entry point of the managed bridge. The C++ host loads this assembly
/// via hostfxr and calls <see cref="Initialize"/> to populate the
/// <c>ArrowNetVTable</c> with function pointers to the static methods below.
/// All boundary methods are <c>[UnmanagedCallersOnly]</c> (cdecl) and never let
/// exceptions cross the ABI — they translate failures into a status code plus an
/// owned UTF-8 error string.
/// </summary>
public static unsafe class Bootstrap
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Initialize(ArrowNetVTable* vtable, int size)
    {
        // Guard against a host built against a newer/larger struct than we know.
        if (vtable is null || size < sizeof(ArrowNetVTable))
        {
            return ArrowNetStatus.InvalidArgument;
        }

        vtable->AbiVersion = 31;
        vtable->OpenCatalog = &OpenCatalog;
        vtable->CloseCatalog = &CloseCatalog;
        vtable->ExecuteQuery = &ExecuteQuery;
        vtable->FreeError = &FreeError;
        vtable->ExecuteDml = &ExecuteDml;
        vtable->BulkInsert = &BulkInsert;
        vtable->ExecuteDelete = &ExecuteDelete;
        vtable->ExecuteUpdate = &ExecuteUpdate;
        vtable->GetMetadata = &GetMetadata;
        vtable->ScanTable = &ScanTable;
        vtable->CreateTable = &CreateTable;
        vtable->DropTable = &DropTable;
        vtable->CreateSchema = &CreateSchema;
        vtable->DropSchema = &DropSchema;
        vtable->AlterTable = &AlterTable;
        vtable->BeginTransaction = &BeginTransaction;
        vtable->CommitTransaction = &CommitTransaction;
        vtable->RollbackTransaction = &RollbackTransaction;
        vtable->InsertReturning = &InsertReturning;
        vtable->BeginBulk = &BeginBulk;
        vtable->PushBatch = &PushBatch;
        vtable->CompleteBulk = &CompleteBulk;
        vtable->BuildConnectionString = &BuildConnectionString;
        vtable->GetFunctionParamSchema = &GetFunctionParamSchema;
        vtable->GetFunctionReturnSchema = &GetFunctionReturnSchema;
        vtable->ExecuteScalar = &ExecuteScalar;
        vtable->GetFunctionOutputSchema = &GetFunctionOutputSchema;
        vtable->AggOpen = &AggOpen;
        vtable->AggUpdate = &AggUpdate;
        vtable->AggCombine = &AggCombine;
        vtable->AggFinalize = &AggFinalize;
        vtable->AggDestroy = &AggDestroy;
        vtable->AggClose = &AggClose;
        vtable->AggUpdateSpill = &AggUpdateSpill;
        vtable->AggCombineSpill = &AggCombineSpill;
        vtable->AggFinalizeSpill = &AggFinalizeSpill;
        vtable->InOutBind = &InOutBind;
        vtable->InOutExchangeOpen = &InOutExchangeOpen;
        vtable->InOutBindClose = &InOutBindClose;
        vtable->TableBind = &TableBind;
        vtable->TableExecute = &TableExecute;
        vtable->TableClose = &TableClose;
        return ArrowNetStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OpenCatalog(byte* provider, byte* conn, nint* outHandle, byte** err)
    {
        try
        {
            if (outHandle is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var providerName = Marshal.PtrToStringUTF8((nint)provider); // null/empty => default backend
            var connStr = Marshal.PtrToStringUTF8((nint)conn) ?? string.Empty;
            var catalog = BackendRegistry.Resolve(providerName).OpenCatalog(connStr);
            *outHandle = Handles.Alloc(catalog);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void CloseCatalog(nint handle) => Handles.Free(handle);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteQuery(nint handle, byte* sql, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var query = Marshal.PtrToStringUTF8((nint)sql) ?? string.Empty;

            IArrowArrayStream stream = catalog.ExecuteQuery(query);
            CArrowArrayStreamExporter.ExportArrayStream(stream, outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteDml(nint handle, byte* sql, long* affected, int* schemaMayChange, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var statement = Marshal.PtrToStringUTF8((nint)sql) ?? string.Empty;
            // DDL detection lives here (C#); the host invalidates its catalog cache
            // when this is set (and the mssql_exec_invalidate_cache setting is on).
            if (schemaMayChange is not null)
            {
                *schemaMayChange = SqlDdl.MayChangeSchema(statement) ? 1 : 0;
            }
            long rows = catalog.ExecuteNonQuery(statement);
            if (affected is not null)
            {
                *affected = rows;
            }
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BulkInsert(nint handle, byte* schema, byte* table, int createTable, int replace,
                                  CArrowArrayStream* input, long* affected, byte** err)
    {
        try
        {
            if (input is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;

            // We take ownership of the C stream (consume + release on dispose).
            var stream = CArrowArrayStreamImporter.ImportArrayStream(input);
            long rows = catalog.BulkInsert(schemaName, tableName, stream, createTable != 0, replace != 0,
                                           checkConstraints: false);
            if (affected is not null)
            {
                *affected = rows;
            }
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteDelete(nint handle, byte* schema, byte* table, CArrowArrayStream* keys, long* affected,
                                     byte** err)
    {
        try
        {
            if (keys is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var stream = CArrowArrayStreamImporter.ImportArrayStream(keys);
            long rows = catalog.ExecuteDelete(schemaName, tableName, stream);
            if (affected is not null)
            {
                *affected = rows;
            }
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteUpdate(nint handle, byte* schema, byte* table, int setCount, CArrowArrayStream* data,
                                     long* affected, byte** err)
    {
        try
        {
            if (data is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var stream = CArrowArrayStreamImporter.ImportArrayStream(data);
            long rows = catalog.ExecuteUpdate(schemaName, tableName, setCount, stream);
            if (affected is not null)
            {
                *affected = rows;
            }
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetMetadata(nint handle, int kind, byte* arg1, byte* arg2, CArrowArrayStream* outStream,
                                   byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var a1 = Marshal.PtrToStringUTF8((nint)arg1);
            var a2 = Marshal.PtrToStringUTF8((nint)arg2);

            IArrowArrayStream stream = catalog.GetMetadata(kind, a1, a2);
            CArrowArrayStreamExporter.ExportArrayStream(stream, outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ScanTable(nint handle, byte* schema, byte* table, byte* specJson,
                                 CArrowArrayStream* filterValues, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var spec = Marshal.PtrToStringUTF8((nint)specJson); // null => full SELECT *

            // Import the typed constant values (if any) the filter tree references.
            IArrowArrayStream? values = filterValues is null
                ? null
                : CArrowArrayStreamImporter.ImportArrayStream(filterValues);

            IArrowArrayStream stream = catalog.ScanTable(schemaName, tableName, spec, values);
            CArrowArrayStreamExporter.ExportArrayStream(stream, outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CreateTable(nint handle, byte* schema, byte* table, CArrowArrayStream* columns, int ifNotExists,
                                   byte* pkColumns, byte* uniqueColumns, byte* defaults, byte* textType, byte** err)
    {
        try
        {
            if (columns is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var pk = Marshal.PtrToStringUTF8((nint)pkColumns);
            var uniques = Marshal.PtrToStringUTF8((nint)uniqueColumns);
            var defaultSpec = Marshal.PtrToStringUTF8((nint)defaults);
            var textTypeName = Marshal.PtrToStringUTF8((nint)textType);

            // We own the C stream; read its schema (the column layout) and release it.
            using var stream = CArrowArrayStreamImporter.ImportArrayStream(columns);
            catalog.CreateTable(schemaName, tableName, stream.Schema, ifNotExists != 0, pk, uniques, defaultSpec,
                                textTypeName);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DropTable(nint handle, byte* schema, byte* table, int ifExists, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            catalog.DropTable(schemaName, tableName, ifExists != 0);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CreateSchema(nint handle, byte* schema, int ifNotExists, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            catalog.CreateSchema(schemaName, ifNotExists != 0);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DropSchema(nint handle, byte* schema, int ifExists, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            catalog.DropSchema(schemaName, ifExists != 0);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AlterTable(nint handle, byte* schema, byte* table, int alterKind, byte* arg1, byte* arg2,
                                  CArrowArrayStream* column, int flags, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var a1 = Marshal.PtrToStringUTF8((nint)arg1);
            var a2 = Marshal.PtrToStringUTF8((nint)arg2);

            // ADD_COLUMN / COLUMN_TYPE carry the new column's type as a one-field schema.
            Field? columnField = null;
            if (column is not null)
            {
                using var stream = CArrowArrayStreamImporter.ImportArrayStream(column);
                columnField = stream.Schema.FieldsList.Count > 0 ? stream.Schema.FieldsList[0] : null;
            }
            catalog.AlterTable(alterKind, schemaName, tableName, a1, a2, columnField, flags);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InsertReturning(nint handle, byte* schema, byte* table, CArrowArrayStream* input,
                                       CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (input is null || outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;

            var rows = CArrowArrayStreamImporter.ImportArrayStream(input);
            IArrowArrayStream returned = catalog.InsertReturning(schemaName, tableName, rows);
            CArrowArrayStreamExporter.ExportArrayStream(returned, outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BeginBulk(nint handle, byte* schema, byte* table, int createTable, int replace,
                                 int checkConstraints, CArrowSchema* schemaIn, nint* outSession, byte** err)
    {
        try
        {
            if (schemaIn is null || outSession is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            // Take ownership of the C schema (materialized into a managed Schema; the
            // C struct is released by the importer).
            var arrowSchema = CArrowSchemaImporter.ImportSchema(schemaIn);
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;

            var session = new BulkSession(catalog, schemaName, tableName, arrowSchema, createTable != 0, replace != 0,
                                          checkConstraints != 0);
            *outSession = Handles.Alloc(session);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int PushBatch(nint session, CArrowArray* batch, byte** err)
    {
        if (batch is null)
        {
            return ArrowNetStatus.InvalidArgument;
        }
        RecordBatch? imported = null;
        try
        {
            var s = Handles.Resolve<BulkSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            // Take ownership of the C array (zero-copy; released when the batch is disposed).
            imported = CArrowArrayImporter.ImportRecordBatch(batch, s.Schema);
            s.Push(imported); // ownership moves into the channel (or disposed if the consumer is gone)
            imported = null;
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            imported?.Dispose();
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CompleteBulk(nint session, int abort, long* affected, byte** err)
    {
        try
        {
            var s = Handles.Resolve<BulkSession>(session);
            long rows = s?.Complete(abort != 0) ?? 0;
            Handles.Free(session);
            if (affected is not null)
            {
                *affected = rows;
            }
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            // Free the handle even on failure (the background task has been observed).
            Handles.Free(session);
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BeginTransaction(nint handle, byte** err) => RunTransactionOp(handle, c => c.BeginTransaction(), err);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CommitTransaction(nint handle, byte** err) => RunTransactionOp(handle, c => c.CommitTransaction(), err);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RollbackTransaction(nint handle, byte** err) => RunTransactionOp(handle, c => c.RollbackTransaction(), err);

    private static int RunTransactionOp(nint handle, Action<IBackendCatalog> op, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            op(catalog);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BuildConnectionString(byte* provider, byte* fieldsJson, byte** outConnStr, byte** err)
    {
        try
        {
            if (outConnStr is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var providerName = Marshal.PtrToStringUTF8((nint)provider); // null/empty => default backend
            var json = Marshal.PtrToStringUTF8((nint)fieldsJson) ?? "{}";
            var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                         ?? new Dictionary<string, string>();
            var connStr = BackendRegistry.Resolve(providerName).BuildConnectionString(fields);
            *outConnStr = (byte*)Marshal.StringToCoTaskMemUTF8(connStr);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFunctionParamSchema(nint handle, byte* schema, byte* func, CArrowArrayStream* outStream,
                                              byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            CArrowArrayStreamExporter.ExportArrayStream(catalog.GetFunctionParamSchema(s, f), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFunctionReturnSchema(nint handle, byte* schema, byte* func, CArrowArrayStream* outStream,
                                               byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            CArrowArrayStreamExporter.ExportArrayStream(catalog.GetFunctionReturnSchema(s, f), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteScalar(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                     CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (args is null || outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
            CArrowArrayStreamExporter.ExportArrayStream(catalog.ExecuteScalar(s, f, argStream), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFunctionOutputSchema(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                               CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            // `args` (nullable) is a 1-row stream of the constant call args — a custom table function's output
            // schema may depend on them. Discovered SQL functions ignore it.
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            CArrowArrayStreamExporter.ExportArrayStream(catalog.GetFunctionOutputSchema(s, f, argsBatch), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    // (ExecuteTable / ExecuteProc handlers were removed at ABI v30 — superseded by the table-function
    //  session TableBind / TableExecute / TableClose.)

    // (The 4g table-in-out push handlers InOutOpen/InOutPush/InOutFinish/InOutAbort were removed at ABI v31 —
    //  every `_each` form now runs on the streaming exchange: InOutBind / InOutExchangeOpen / InOutBindClose.)

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InOutBind(nint handle, byte* schema, byte* func, CArrowArrayStream* args, CArrowSchema* inputSchema,
                                 CArrowArrayStream* outSchema, nint* outBinding, byte** err)
    {
        try
        {
            if (inputSchema is null || outSchema is null || outBinding is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var inSchema = CArrowSchemaImporter.ImportSchema(inputSchema); // takes ownership of the C schema
            // `args` (nullable) is a 1-row stream of the constant cost args (read synchronously below).
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            var binding = catalog.InOutBind(s, f, argsBatch, inSchema);
            // Export the binding's full output schema as a zero-row stream so the host can read return types.
            CArrowArrayStreamExporter.ExportArrayStream(
                new InMemoryArrayStream(binding.OutputSchema, System.Array.Empty<RecordBatch>()), outSchema);
            *outBinding = Handles.Alloc(binding);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InOutExchangeOpen(nint binding, CArrowArrayStream* input, byte* isolation,
                                         CArrowArrayStream* output, byte** err)
    {
        try
        {
            if (input is null || output is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var b = Handles.Resolve<IArrowInOutBinding>(binding);
            if (b is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            // Take ownership of the host's input stream; the pump pulls it (one chunk per gate tenure) + releases it.
            var inputStream = CArrowArrayStreamImporter.ImportArrayStream(input);
            var iso = Marshal.PtrToStringUTF8((nint)isolation) ?? string.Empty;
            CArrowArrayStreamExporter.ExportArrayStream(new InOutExchangeStream(b, inputStream, iso), output);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InOutBindClose(nint binding, byte** err)
    {
        try
        {
            Handles.Resolve<IArrowInOutBinding>(binding)?.Dispose(); // idempotent
            Handles.Free(binding);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableBind(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                 CArrowArrayStream* outSchema, int* supportsPushdown, nint* outBinding, byte** err)
    {
        try
        {
            if (outSchema is null || supportsPushdown is null || outBinding is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            // `args` (nullable) is a 1-row stream of the constant call args (read synchronously below).
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            var bound = catalog.TableBind(s, f, argsBatch);
            // Export the binding's output schema as a zero-row stream so the host can read return types.
            CArrowArrayStreamExporter.ExportArrayStream(
                new InMemoryArrayStream(bound.OutputSchema, System.Array.Empty<RecordBatch>()), outSchema);
            *supportsPushdown = bound.SupportsPushdown ? 1 : 0;
            *outBinding = Handles.Alloc(bound);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableExecute(nint binding, byte* specJson, CArrowArrayStream* filterValues,
                                    CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var bound = Handles.Resolve<IBoundTable>(binding);
            if (bound is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var spec = Marshal.PtrToStringUTF8((nint)specJson); // null => SELECT *
            IArrowArrayStream? filters =
                filterValues is null ? null : CArrowArrayStreamImporter.ImportArrayStream(filterValues);
            CArrowArrayStreamExporter.ExportArrayStream(bound.Execute(spec, filters), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableClose(nint binding, byte** err)
    {
        try
        {
            Handles.Resolve<IBoundTable>(binding)?.Dispose(); // idempotent
            Handles.Free(binding);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    // Function-independent import schemas for the aggregate ABI batches: combine carries two int64 id
    // columns, finalize/destroy a single int64 id column. (Update's schema is per-function — the session
    // exposes it as IAggregateSession.UpdateSchema. Field names are cosmetic on import — the session reads
    // columns by position.)
    private static readonly Schema AggCombineSchema =
        new(new[] { new Field("target_id", Int64Type.Default, false), new Field("source_id", Int64Type.Default, false) },
            null);
    private static readonly Schema AggIdsSchema =
        new(new[] { new Field("state_id", Int64Type.Default, false) }, null);
    // Spillable-mode serialized per-group state column (NULL = fresh/empty group).
    private static readonly Schema AggStateSchema =
        new(new[] { new Field("state", BinaryType.Default, true) }, null);
    // Spillable combine batch: a target-slot index + the source state to merge into that target.
    private static readonly Schema AggCombineBatchSchema =
        new(new[] { new Field("slot", Int64Type.Default, false), new Field("source", BinaryType.Default, true) }, null);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggOpen(nint handle, byte* schema, byte* func, nint* outSession, byte** err)
    {
        try
        {
            if (outSession is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            *outSession = Handles.Alloc(catalog.AggOpen(s, f));
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggUpdate(nint session, CArrowArray* batch, byte** err)
    {
        try
        {
            if (batch is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(batch, s.UpdateSchema); // takes ownership
            s.Update(rb);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggCombine(nint session, CArrowArray* batch, byte** err)
    {
        try
        {
            if (batch is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(batch, AggCombineSchema); // takes ownership
            s.Combine(rb);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggFinalize(nint session, CArrowArray* ids, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (ids is null || outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(ids, AggIdsSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.Finalize(rb), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggDestroy(nint session, CArrowArray* ids, byte** err)
    {
        try
        {
            if (ids is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(ids, AggIdsSchema); // takes ownership (must release)
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                rb.Dispose();
                return ArrowNetStatus.Ok; // session already closed — nothing to drop
            }
            s.Destroy(rb);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggClose(nint session, byte** err)
    {
        try
        {
            Handles.Resolve<IAggregateSession>(session)?.Close(); // idempotent
            Handles.Free(session);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggUpdateSpill(nint session, CArrowArray* groupStates, CArrowArray* batch,
                                      CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (groupStates is null || batch is null || outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var states = CArrowArrayImporter.ImportRecordBatch(groupStates, AggStateSchema); // takes ownership
            var rows = CArrowArrayImporter.ImportRecordBatch(batch, s.UpdateSchema);          // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.UpdateSpill(states, rows), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggCombineSpill(nint session, CArrowArray* targetStates, CArrowArray* combineBatch,
                                       CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (targetStates is null || combineBatch is null || outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var target = CArrowArrayImporter.ImportRecordBatch(targetStates, AggStateSchema);       // takes ownership
            var batch = CArrowArrayImporter.ImportRecordBatch(combineBatch, AggCombineBatchSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.CombineSpill(target, batch), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggFinalizeSpill(nint session, CArrowArray* states, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (states is null || outStream is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return ArrowNetStatus.InvalidArgument;
            }
            var batch = CArrowArrayImporter.ImportRecordBatch(states, AggStateSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.FinalizeSpill(batch), outStream);
            return ArrowNetStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return ArrowNetStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void FreeError(byte* err)
    {
        if (err is not null)
        {
            Marshal.FreeCoTaskMem((nint)err);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetError(byte** err, Exception ex)
    {
        if (err is not null)
        {
            *err = (byte*)Marshal.StringToCoTaskMemUTF8(FormatError(ex));
        }
    }

    /// <summary>
    /// Surfaces a provider error code (e.g. SqlException.Number = 2627 for a PK
    /// violation) ahead of the message, so error-code assertions match the way the
    /// native mssql extension reports TDS errors. The bridge stays provider-agnostic,
    /// so we duck-type a public <c>int Number</c> property (SqlException has one)
    /// rather than reference Microsoft.Data.SqlClient.
    /// </summary>
    private static string FormatError(Exception ex)
    {
        try
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                var prop = e.GetType().GetProperty("Number");
                if (prop?.PropertyType == typeof(int) && prop.GetValue(e) is int number && number != 0)
                {
                    return $"{number}: {ex.Message}";
                }
            }
        }
        catch
        {
            // Reflection failed — fall back to the plain message.
        }
        return ex.Message;
    }
}
