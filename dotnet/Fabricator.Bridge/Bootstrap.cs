using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Apache.Arrow;
using Apache.Arrow.C;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Native entry point of the managed bridge. The C++ host loads this assembly
/// via hostfxr and calls <see cref="Initialize"/> to populate the
/// <c>FabricatorVTable</c> with function pointers to the static methods below.
/// All boundary methods are <c>[UnmanagedCallersOnly]</c> (cdecl) and never let
/// exceptions cross the ABI — they translate failures into a status code plus an
/// owned UTF-8 error string.
/// </summary>
public static unsafe class Bootstrap
{
    // Traces bridge-boundary activity — every ABI crossing that FAILS is logged here centrally (see SetError:
    // the CallerMemberName is the ABI op), plus control-path crossings (open/close/metadata/bind). The data
    // path is traced in the providers (Fabricator.Sql / Fabricator.Delta). Off by default (FABRICATOR_LOG_LEVEL).
    private static readonly ILogger BridgeLog = FabricatorLog.CreateLogger("Fabricator.Bridge");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Initialize(FabricatorVTable* vtable, int size, FabricatorHostServices* host)
    {
        // Guard against a host built against a newer/larger struct than we know.
        if (vtable is null || size < sizeof(FabricatorVTable))
        {
            return FabricatorStatus.InvalidArgument;
        }

        // Cache the host-services callbacks (reverse direction) so managed components can reach DuckDB's
        // FileSystem (secret-backed remote IO). May be a zeroed block if the host registered none. SPIKE.
        if (host is not null)
        {
            HostFs.Set(*host);
            // Fill in the contract assembly's HTTP transport seam (ABI v76), so DuckDbHttpHandler works for
            // anything referencing Fabricator.Abstractions alone — a plugin above all. ⚠ The ambient opener
            // is read HERE, per call, not captured: a catalog is database-scoped and outlives the connection
            // that attached it, so a held ClientContext* would dangle.
            HostHttpTransport.Send = (method, url, headers, body) =>
                HostFs.HttpRequest(AmbientOpener.Current, method, url, headers, body);
            // Forward ILogger output into DuckDB's internal logging (duckdb_logs) when the host provides host_log.
            // The file sink (FABRICATOR_LOG_LEVEL/_FILE) stays independent; this adds the engine-log route.
            if (HostFs.CanLog)
            {
                FabricatorLog.EnableHostForwarding((level, category, message) => HostFs.Log(level, category, message));
            }
        }

        // A built-in demo named source (data-in by name): query it as `fabricator_scan('fabricator_demo_numbers')`
        // or, with the replacement scan, bare `FROM fabricator_demo_numbers`. Harmless; proves the registry.
        Host.RegisterSource("fabricator_demo_numbers", () =>
        {
            var schema = new Apache.Arrow.Schema(
                new[] { new Apache.Arrow.Field("value", Apache.Arrow.Types.Int64Type.Default, nullable: false) }, null);
            var col = new Apache.Arrow.Int64Array.Builder().Append(10).Append(20).Append(30).Build();
            var batch = new Apache.Arrow.RecordBatch(schema, new Apache.Arrow.IArrowArray[] { col }, 3);
            return new InMemoryArrayStream(schema, new[] { batch });
        });

        vtable->AbiVersion = 81;
        vtable->OpenCatalog = &OpenCatalog;
        vtable->CloseCatalog = &CloseCatalog;
        vtable->ExecuteQuery = &ExecuteQuery;
        vtable->FreeError = &FreeError;
        vtable->ExecuteDml = &ExecuteDml;
        vtable->BulkInsert = &BulkInsert;
        vtable->ExecuteDelete = &ExecuteDelete;
        vtable->ExecuteUpdate = &ExecuteUpdate;
        vtable->CreateTable = &CreateTable;
        vtable->DropTable = &DropTable;
        vtable->CreateSchema = &CreateSchema;
        vtable->DropSchema = &DropSchema;
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
        vtable->ScalarFnBind = &ScalarFnBind;
        vtable->ScalarFnExecute = &ScalarFnExecute;
        vtable->ScalarFnClose = &ScalarFnClose;
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
        vtable->TableFnBind = &TableFnBind;
        vtable->TableFnExecute = &TableFnExecute;
        vtable->TableFnClose = &TableFnClose;
        vtable->ListSettings = &ListSettings;
        vtable->SetSetting = &SetSetting;
        vtable->SetActiveTxn = &SetActiveTxn;
        vtable->ListSecretFields = &ListSecretFields;
        vtable->FsSpike = &FsSpike;
        vtable->OpenNamedInput = &OpenNamedInput;
        vtable->NamedInputExists = &NamedInputExists;
        vtable->ListGlobalFunctions = &ListGlobalFunctions;
        vtable->SetActiveOpener = &SetActiveOpener;
        vtable->OneLakeOpen = &OneLakeOpen;
        vtable->OneLakeRead = &OneLakeRead;
        vtable->OneLakeClose = &OneLakeClose;
        vtable->OneLakeGlob = &OneLakeGlob;
        vtable->OneLakeExists = &OneLakeExists;
        vtable->OneLakeOpenWrite = &OneLakeOpenWrite;
        vtable->OneLakeWrite = &OneLakeWrite;
        vtable->OneLakeCloseWrite = &OneLakeCloseWrite;
        vtable->OneLakeRemove = &OneLakeRemove;
        vtable->OneLakeMove = &OneLakeMove;
        vtable->GenerateTableSql = &GenerateTableSql;
        vtable->ClearSessionSettings = &ClearSessionSettings;
        vtable->GetCapabilities = &GetCapabilities;
        vtable->CatalogInit = &CatalogInit;
        vtable->CatalogSchemas = &CatalogSchemas;
        vtable->CatalogTables = &CatalogTables;
        vtable->CatalogFunctions = &CatalogFunctions;
        vtable->CatalogMacros = &CatalogMacros;
        vtable->CatalogViews = &CatalogViews;
        vtable->CatalogServerInfo = &CatalogServerInfo;
        vtable->TableOpen = &TableOpen;
        vtable->TableSchema = &TableSchema;
        vtable->TableInfo = &TableInfo;
        vtable->TableStats = &TableStats;
        vtable->TableScan = &TableScan;
        vtable->TableAlter = &TableAlter;
        vtable->TableClose = &TableClose;
        vtable->LateralBind = &LateralBind;
        vtable->LateralOpen = &LateralOpen;
        vtable->LateralCall = &LateralCall;
        vtable->LateralClose = &LateralClose;
        vtable->LateralBindClose = &LateralBindClose;
        return FabricatorStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OpenCatalog(byte* provider, byte* conn, byte* optionsJson, nint* outHandle, byte** err)
    {
        try
        {
            if (outHandle is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var providerName = Marshal.PtrToStringUTF8((nint)provider); // null/empty => default backend
            var connStr = Marshal.PtrToStringUTF8((nint)conn) ?? string.Empty;
            var options = Marshal.PtrToStringUTF8((nint)optionsJson) ?? string.Empty; // ATTACH options (JSON), provider-owned
            // NOTE: connStr may carry a password — never log it. Provider + options (schema_filter/… ) are safe.
            BridgeLog.LogDebug("abi open_catalog: provider={Provider} options={Options}",
                string.IsNullOrEmpty(providerName) ? "(default)" : providerName, options);
            // The REQUESTED name is forwarded, not just used to resolve the backend: for the Delta backend the
            // name selects a default PROFILE ('delta' = native hybrid, 'engineeredwooddelta' = pure EW), so
            // dropping it here is what used to make that distinction unexpressible.
            var catalog = BackendRegistry.Resolve(providerName)
                .OpenCatalog(connStr, options, providerName ?? string.Empty);
            *outHandle = Handles.Alloc(catalog);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void CloseCatalog(nint handle) => Handles.Free(handle);

    // v71: the catalog's capability doc — one flat JSON object of booleans (absent key = false), read once
    // at ATTACH from LoadCatalog. The provider answers via IBackendCatalog.CapabilitiesJson (DIM "{}"), so a
    // provider with nothing to assert declares nothing. Replaces the host's grep of the diagnostic kind-7
    // (property, value) stream; that stream stays, as the fabricator_server_info() diagnostic only.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetCapabilities(nint handle, byte** outJson, byte** err)
    {
        try
        {
            if (outJson is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? throw new InvalidOperationException("get_capabilities: invalid catalog handle");
            *outJson = (byte*)Marshal.StringToCoTaskMemUTF8(catalog.CapabilitiesJson); // host frees via free_error
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteQuery(nint handle, byte* sql, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var query = Marshal.PtrToStringUTF8((nint)sql) ?? string.Empty;

            // ⚠ DESCRIBE-THEN-EXECUTE, and it is a FIX rather than an optimisation. The host's bind-time
            // schema probe (arrow_ingest's PopulateReturnSchema) calls this entry, reads get_schema and
            // releases — then the scan calls it again for rows. Handing back an eagerly-executed stream
            // therefore ran the caller's SQL TWICE: MEASURED, an INSERT through fabricator_query inserted two
            // rows where the same statement through fabricator_exec inserted one.
            //
            // DescribedArrowStream defers: get_schema asks the provider to DESCRIBE (no execution), and only
            // a row pull executes. A provider that cannot describe the statement returns null and the stream
            // executes to answer the schema, keeping that stream for the rows — i.e. the unsupported case is
            // exactly the old behaviour rather than a failure.
            //
            // ⚠ Scoped to THIS entry on purpose. The provider's own internal reads call catalog.ExecuteQuery
            // directly in C# and want rows immediately; wrapping those would add a describe round trip that
            // nothing consumes.
            //
            // ⚠⚠ CAPTURE THE AMBIENTS HERE AND RE-ESTABLISH THEM IN BOTH LAMBDAS — this is not defensive, it
            // is what makes the deferral legal, and OMITTING IT WAS A REGRESSION THE SUITES CAUGHT.
            // AmbientTransaction / CurrentSession / AmbientOpener are AsyncLocal PER CROSSING. Deferring the
            // execution from this crossing to the first get_next moves it to a crossing where nothing has set
            // them, so catalog.ExecuteQuery saw txn 0, found no pinned connection, and ran the caller's SQL on
            // a POOLED one. MEASURED: verify_session_tag's "a different call in the same transaction sees the
            // tag" assertion returned NULL — and the read-your-writes consequence is bigger than the tag,
            // since a fabricator_query inside a transaction would silently read the COMMITTED state.
            //
            // ⚠ It fails by returning the WRONG ANSWER, not by erroring, and it is the standing rule this repo
            // already paid for once (fabricator_install_plugin read a session-scoped opt-in inside an async
            // iterator and non-deterministically saw session 0). The rule: A LAZY BODY MUST BE HANDED THE
            // AMBIENTS ITS CROSSING CAPTURED — never read them where it runs.
            long txnId = AmbientTransaction.Current;
            long settingsSession = ProviderSettingsStore.CurrentSession;
            nint opener = AmbientOpener.Current;

            void Restore()
            {
                AmbientTransaction.Current = txnId;
                ProviderSettingsStore.CurrentSession = settingsSession;
                AmbientOpener.Current = opener;
            }

            // Both halves need them: the describe resolves its command timeout from the session settings, and
            // the execution needs the transaction to find the pinned connection.
            IArrowArrayStream stream = new DescribedArrowStream(
                () => { Restore(); return catalog.DescribeQuery(query); },
                () => { Restore(); return catalog.ExecuteQuery(query); });
            CArrowArrayStreamExporter.ExportArrayStream(stream, outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ExecuteDml(nint handle, byte* sql, long* affected, int* schemaMayChange, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
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
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;

            // We take ownership of the C stream (consume + release on dispose).
            var stream = CArrowArrayStreamImporter.ImportArrayStream(input);
            long rows = catalog.BulkInsert(schemaName, tableName, stream, createTable != 0, replace != 0,
                                           checkConstraints: false, txnId: AmbientTransaction.Current,
                                           partitionColumns: null, sortColumns: null, schemaMode: null,
                                           partitionOverwrite: false, optionsJson: null);
            if (affected is not null)
            {
                *affected = rows;
            }
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var stream = CArrowArrayStreamImporter.ImportArrayStream(keys);
            long rows = catalog.ExecuteDelete(schemaName, tableName, stream);
            if (affected is not null)
            {
                *affected = rows;
            }
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var stream = CArrowArrayStreamImporter.ImportArrayStream(data);
            long rows = catalog.ExecuteUpdate(schemaName, tableName, setCount, stream);
            if (affected is not null)
            {
                *affected = rows;
            }
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // ---- catalog discovery + the table session (ABI v72 — get_metadata/scan_table's replacement) --------

    /// <summary>Shared body of the five catalog_* discovery exports: resolve the catalog, export the
    /// member's stream.</summary>
    private static int CatalogList(nint handle, CArrowArrayStream* outStream, byte** err, string what,
                                   Func<IBackendCatalog, IArrowArrayStream> member)
    {
        try
        {
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            BridgeLog.LogDebug("abi {What}", what);
            CArrowArrayStreamExporter.ExportArrayStream(member(catalog), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogSchemas(nint handle, CArrowArrayStream* outStream, byte** err) =>
        CatalogList(handle, outStream, err, "catalog_schemas", c => c.GetSchemas());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogTables(nint handle, CArrowArrayStream* outStream, byte** err) =>
        CatalogList(handle, outStream, err, "catalog_tables", c => c.GetTables());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogFunctions(nint handle, CArrowArrayStream* outStream, byte** err) =>
        CatalogList(handle, outStream, err, "catalog_functions", c => c.GetFunctions());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogMacros(nint handle, CArrowArrayStream* outStream, byte** err) =>
        CatalogList(handle, outStream, err, "catalog_macros", c => c.GetMacros());

    // The provider init hook (v78). Unlike the CatalogList entries this returns no stream — it exists purely
    // so a provider can do context-requiring setup at a DEFINED point, and its failure is reported so the
    // host can fail the ATTACH.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogInit(nint handle, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle);
            if (catalog is null)
            {
                // ⚠ Deliberately NOT the CatalogList fallback of opening a fresh catalog: initialising a
                // DIFFERENT catalog than the host asked about is worse than refusing, and unlike a discovery
                // read there is no result to approximate. An unresolvable handle here is a host bug.
                return FabricatorStatus.InvalidArgument;
            }
            // Logged on the MANAGED side, matching the `abi <entry>` convention of the discovery crossings —
            // and it is what makes the gate meaningful: the host's own line proves only that it CALLED, while
            // this one proves the crossing ARRIVED and the provider's Initialize ran.
            BridgeLog.LogDebug("abi catalog_init");
            catalog.Initialize();
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogViews(nint handle, CArrowArrayStream* outStream, byte** err) =>
        CatalogList(handle, outStream, err, "catalog_views", c => c.GetViews());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CatalogServerInfo(nint handle, CArrowArrayStream* outStream, byte** err) =>
        CatalogList(handle, outStream, err, "catalog_server_info", c => c.GetServerInfo());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableOpen(nint handle, byte* schema, byte* table, byte* atUnit, byte* atValue,
                                 nint* outTable, byte** err)
    {
        try
        {
            if (outTable is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var unit = Marshal.PtrToStringUTF8((nint)atUnit);
            var value = Marshal.PtrToStringUTF8((nint)atValue);
            TableAt? at = string.IsNullOrEmpty(unit) ? null : new TableAt(unit!, value ?? string.Empty);
            BridgeLog.LogDebug("abi table_open: {Schema}.{Table} at={At}", schemaName, tableName, unit);

            // No IO and no absence probe here: the handle wraps the DEFINITION (+ the reference's AT), and
            // absence is established by table_schema — the first actual read — exactly where the old kind-2
            // classified it. Opening cheap is load-bearing: enumeration materializes every table.
            var session = new TableSession(catalog, catalog.GetTable(schemaName, tableName), at);
            *outTable = Handles.Alloc(session);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableSchema(nint table, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null || Handles.Resolve<TableSession>(table) is not { } session)
            {
                return FabricatorStatus.InvalidArgument;
            }
            CArrowArrayStreamExporter.ExportArrayStream(session.SchemaStream(), outStream);
            return FabricatorStatus.Ok;
        }
        catch (ObjectNotFoundException ex)
        {
            // ABSENCE, distinguished from failure. The host drops the catalog entry AND removes the name
            // from enumeration on this status — right for a table dropped out-of-band, catastrophic for a
            // table that merely could not be read (its data is intact and it would silently vanish). Only a
            // provider that has ESTABLISHED absence throws this; everything else falls through below.
            SetError(err, ex);
            return FabricatorStatus.NotFound;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableInfo(nint table, byte** outJson, byte** err)
    {
        try
        {
            if (outJson is null || Handles.Resolve<TableSession>(table) is not { } session)
            {
                return FabricatorStatus.InvalidArgument;
            }
            *outJson = (byte*)Marshal.StringToCoTaskMemUTF8(session.InfoJson()); // host frees via free_error
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableStats(nint table, byte** outJson, byte** err)
    {
        try
        {
            if (outJson is null || Handles.Resolve<TableSession>(table) is not { } session)
            {
                return FabricatorStatus.InvalidArgument;
            }
            *outJson = (byte*)Marshal.StringToCoTaskMemUTF8(session.StatsJson()); // host frees via free_error
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableScan(nint table, byte* specJson, CArrowArrayStream* filterValues,
                                 CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null || Handles.Resolve<TableSession>(table) is not { } session)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var spec = Marshal.PtrToStringUTF8((nint)specJson); // null => full SELECT *

            // Import the typed constant values (if any) the filter tree references.
            IArrowArrayStream? values = filterValues is null
                ? null
                : CArrowArrayStreamImporter.ImportArrayStream(filterValues);

            CArrowArrayStreamExporter.ExportArrayStream(session.Scan(spec, values), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableAlter(nint table, byte* alterJson, CArrowArrayStream* column, byte** err)
    {
        try
        {
            if (alterJson is null || Handles.Resolve<TableSession>(table) is not { } session)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var spec = AlterTableSpec.Parse(Marshal.PtrToStringUTF8((nint)alterJson)!);

            // The TYPE CHANNEL: add_column / column_type / add_field carry the new column's or field's type
            // as a one-field zero-row schema. It stays an Arrow field rather than folding into the doc
            // because a VARIANT is identified by field METADATA, which no type name could carry.
            Field? columnField = null;
            if (column is not null)
            {
                using var stream = CArrowArrayStreamImporter.ImportArrayStream(column);
                columnField = stream.Schema.FieldsList.Count > 0 ? stream.Schema.FieldsList[0] : null;
            }
            session.Alter(spec, columnField);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void TableClose(nint table)
    {
        // A definition handle holds no provider resources — this frees the GCHandle. Best-effort by
        // contract (the C++ entry destructor calls it during catalog teardown).
        Handles.Free(table);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CreateTable(nint handle, byte* schema, byte* table, CArrowArrayStream* columns, int ifNotExists,
                                   byte* pkColumns, byte* uniqueColumns, byte* defaults, byte* partitionColumns,
                                   byte* sortColumns, byte* identityColumns, byte* optionsJson, byte** err)
    {
        try
        {
            if (columns is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var pk = Marshal.PtrToStringUTF8((nint)pkColumns);
            var uniques = Marshal.PtrToStringUTF8((nint)uniqueColumns);
            var defaultSpec = Marshal.PtrToStringUTF8((nint)defaults);
            var partition = SplitColumnList(Marshal.PtrToStringUTF8((nint)partitionColumns));
            var sort = SplitColumnList(Marshal.PtrToStringUTF8((nint)sortColumns));
            var identity = SplitColumnList(Marshal.PtrToStringUTF8((nint)identityColumns));
            var options = Marshal.PtrToStringUTF8((nint)optionsJson); // WITH (key='value', ...) as flat JSON (v67)

            // We own the C stream; read its schema (the column layout) and release it. The text-column SQL
            // type (mssql_ctas_text_type / mssql_default_varchar_length) is read from the settings store in C#.
            using var stream = CArrowArrayStreamImporter.ImportArrayStream(columns);
            catalog.CreateTable(schemaName, tableName, stream.Schema, ifNotExists != 0, pk, uniques, defaultSpec,
                                partition, sort, identity, options);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DropTable(nint handle, byte* schema, byte* table, int ifExists, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            catalog.DropTable(schemaName, tableName, ifExists != 0);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CreateSchema(nint handle, byte* schema, int ifNotExists, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            catalog.CreateSchema(schemaName, ifNotExists != 0);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int DropSchema(nint handle, byte* schema, int ifExists, byte** err)
    {
        try
        {
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            catalog.DropSchema(schemaName, ifExists != 0);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;

            var rows = CArrowArrayStreamImporter.ImportArrayStream(input);
            IArrowArrayStream returned = catalog.InsertReturning(schemaName, tableName, rows);
            CArrowArrayStreamExporter.ExportArrayStream(returned, outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // Set the DuckDB transaction id (global_transaction_id) in effect on THIS thread, so the subsequent
    // connection-using call on the same thread keys its per-transaction provider connection by it. The host
    // calls this immediately before each such call. 0 => no specific transaction (fresh/pooled connection).
    // handle is unused (the ambient is per-thread + global; each catalog keys its own state dictionary by it).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetActiveTxn(nint handle, long txnId, int joinOnly, byte** err)
    {
        AmbientTransaction.Current = txnId;
        AmbientTransaction.JoinOnly = joinOnly != 0;
        return FabricatorStatus.Ok;
    }

    // SPIKE: open `path` via the host FileSystem callbacks (using `opener` for secret resolution) and return
    // head/tail bytes + size. Proves a managed component can do secret-backed remote IO through DuckDB.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FsSpike(nint opener, byte* path, byte** outResult, byte** err)
    {
        try
        {
            if (outResult is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var p = Marshal.PtrToStringUTF8((nint)path) ?? string.Empty;
            var result = HostFileSystemSpike.Run(opener, p);
            *outResult = (byte*)Marshal.StringToCoTaskMemUTF8(result); // host frees via free_error
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // Record the calling operator's ClientContext as the active host-FS opener (per-thread ambient), so a
    // connection-free GLOBAL host-FS table function (a lakehouse reader like fabricator_delta_scan) can resolve
    // DuckDB secrets while reading through the host FileSystem callbacks. The host calls this immediately
    // before each table-function bind + execution, on the same thread. 0 clears it. Mirrors SetActiveTxn.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetActiveOpener(nint opener, long session, byte** err)
    {
        AmbientOpener.Current = opener;
        // The settings session rides this entry because the two are set at the same moments — but it is a
        // SEPARATE value, not `opener` reinterpreted: the commit flush and the rollback pass their own
        // short-lived connection as the opener while the settings that govern them were SET on the user's.
        ProviderSettingsStore.CurrentSession = session;
        return FabricatorStatus.Ok;
    }

    // ---- onelake:// FileSystem forward callbacks (Phase-3): the C++ onelake FS forwards read ops here to the
    //      managed Azure DataLake SDK (see OneLakeForwardFs). cred_json = the azure secret fields the host
    //      resolved from the opener ("{}"/empty ⇒ DefaultAzureCredential).

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeOpen(byte* path, byte* credJson, long knownSize, nint* outFile, long* outSize,
                                   byte** outEtag, long* outModifiedMs, byte** err)
    {
        try
        {
            var p = Marshal.PtrToStringUTF8((nint)path) ?? string.Empty;
            var cj = Marshal.PtrToStringUTF8((nint)credJson);
            var (handle, size, etag, modifiedMs) = OneLakeForwardFs.Open(p, cj, knownSize);
            *outFile = Handles.Alloc(handle);
            *outSize = size;
            *outEtag = etag is null ? null : (byte*)Marshal.StringToCoTaskMemUTF8(etag); // host frees via free_error
            *outModifiedMs = modifiedMs;
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeRead(nint file, void* buffer, long nrBytes, long location, byte** err)
    {
        try
        {
            var h = Handles.Resolve<OneLakeForwardFs.Handle>(file)
                    ?? throw new InvalidOperationException("onelake_read: invalid file handle");
            OneLakeForwardFs.Read(h, new Span<byte>(buffer, checked((int)nrBytes)), location);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OneLakeClose(nint file)
    {
        if (file != 0)
        {
            Handles.Free(file);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeGlob(byte* pattern, byte* credJson, byte** outJson, byte** err)
    {
        try
        {
            var pat = Marshal.PtrToStringUTF8((nint)pattern) ?? string.Empty;
            var cj = Marshal.PtrToStringUTF8((nint)credJson);
            var json = OneLakeForwardFs.Glob(pat, cj);
            *outJson = (byte*)Marshal.StringToCoTaskMemUTF8(json); // host frees via free_error
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeExists(byte* path, byte* credJson, int* outExists, byte** err)
    {
        try
        {
            var p = Marshal.PtrToStringUTF8((nint)path) ?? string.Empty;
            var cj = Marshal.PtrToStringUTF8((nint)credJson);
            *outExists = OneLakeForwardFs.Exists(p, cj) ? 1 : 0;
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeOpenWrite(byte* path, byte* credJson, int exclusive, nint* outFile, byte** err)
    {
        try
        {
            var p = Marshal.PtrToStringUTF8((nint)path) ?? string.Empty;
            var cj = Marshal.PtrToStringUTF8((nint)credJson);
            var handle = OneLakeForwardFs.OpenWrite(p, cj, exclusive != 0);
            *outFile = Handles.Alloc(handle);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeRemove(byte* path, byte* credJson, byte** err)
    {
        try
        {
            var p = Marshal.PtrToStringUTF8((nint)path) ?? string.Empty;
            var cj = Marshal.PtrToStringUTF8((nint)credJson);
            OneLakeForwardFs.Remove(p, cj);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeMove(byte* src, byte* dest, byte* credJson, byte** err)
    {
        try
        {
            var s2 = Marshal.PtrToStringUTF8((nint)src) ?? string.Empty;
            var d = Marshal.PtrToStringUTF8((nint)dest) ?? string.Empty;
            var cj = Marshal.PtrToStringUTF8((nint)credJson);
            OneLakeForwardFs.Move(s2, d, cj);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeWrite(nint file, void* buffer, long nrBytes, byte** err)
    {
        try
        {
            var h = Handles.Resolve<OneLakeForwardFs.WriteHandle>(file)
                    ?? throw new InvalidOperationException("onelake_write: invalid file handle");
            OneLakeForwardFs.Write(h, new ReadOnlySpan<byte>(buffer, checked((int)nrBytes)));
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OneLakeCloseWrite(nint file, byte** err)
    {
        try
        {
            var h = Handles.Resolve<OneLakeForwardFs.WriteHandle>(file);
            if (h is not null)
            {
                OneLakeForwardFs.CloseWrite(h);
                Handles.Free(file);
            }
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // SQL-GENERATING table function (v68): hand the constant call args to the function's generator and return
    // the replacement SQL — the managed side of DuckDB's bind_replace (docs/macros-and-sqlgen-functions.md §2).
    // BIND-time only; no data path. handle == 0 => the global registry; non-zero => the catalog's, with
    // catalogName = the DuckDB ATTACH alias so a catalog-bound generator can qualify references back into it.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GenerateTableSql(nint handle, byte* schema, byte* func, byte* catalogName,
                                       CArrowArrayStream* args, byte** outSql, byte** err)
    {
        try
        {
            if (outSql is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            string sql = handle == 0
                ? GlobalFunctions.GenerateTableSql(f, argsBatch)
                : (Handles.Resolve<IBackendCatalog>(handle)
                   ?? throw new InvalidOperationException(
                       $"fabricator: generate_table_sql got a stale catalog handle for '{f}'"))
                    .GenerateTableSql(Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty, f,
                                      Marshal.PtrToStringUTF8((nint)catalogName) ?? string.Empty, argsBatch);
            *outSql = (byte*)Marshal.StringToCoTaskMemUTF8(sql); // host frees via free_error
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // Ambient named-source registry (data-in by name) — see Host.RegisterSource. open_named_input exports a
    // fresh stream for the registered source (errors if none); named_input_exists reports registration.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OpenNamedInput(byte* name, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var n = Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
            var stream = Host.OpenSource(n)
                         ?? throw new InvalidOperationException($"fabricator: no named source registered as '{n}'");
            CArrowArrayStreamExporter.ExportArrayStream(stream, outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NamedInputExists(byte* name, int* outExists, byte** err)
    {
        try
        {
            var n = Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
            if (outExists != null)
            {
                *outExists = Host.SourceExists(n) ? 1 : 0;
            }
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BeginBulk(nint handle, byte* schema, byte* table, int createTable, int replace,
                                 int checkConstraints, long txnId, CArrowSchema* schemaIn, byte* partitionColumns,
                                 byte* sortColumns, byte* schemaMode, int partitionOverwrite, byte* optionsJson,
                                 nint* outSession, byte** err)
    {
        try
        {
            if (schemaIn is null || outSession is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            // Take ownership of the C schema (materialized into a managed Schema; the
            // C struct is released by the importer).
            var arrowSchema = CArrowSchemaImporter.ImportSchema(schemaIn);
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var schemaName = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            var tableName = Marshal.PtrToStringUTF8((nint)table) ?? string.Empty;
            var partition = SplitColumnList(Marshal.PtrToStringUTF8((nint)partitionColumns));
            var sort = SplitColumnList(Marshal.PtrToStringUTF8((nint)sortColumns));
            var schemaModeStr = Marshal.PtrToStringUTF8((nint)schemaMode);
            var options = Marshal.PtrToStringUTF8((nint)optionsJson); // CTAS WITH (key='value', ...) as flat JSON (v67)

            // Capture the host-FS opener now (set by the C++ sink before begin_bulk, on this thread) so the
            // background bulk consumer can re-establish it — a host-FS provider (the Delta catalog) writes
            // through DuckDB's FileSystem on the consumer thread. The ClientContext stays valid for the
            // statement (complete_bulk blocks until the consumer finishes), so the opener is live at write time.
            var opener = AmbientOpener.Current;
            var session = new BulkSession(catalog, schemaName, tableName, arrowSchema, createTable != 0, replace != 0,
                                          checkConstraints != 0, txnId, opener, partition, sort, schemaModeStr,
                                          partitionOverwrite != 0, options);
            *outSession = Handles.Alloc(session);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int PushBatch(nint session, CArrowArray* batch, byte** err)
    {
        if (batch is null)
        {
            return FabricatorStatus.InvalidArgument;
        }
        RecordBatch? imported = null;
        try
        {
            var s = Handles.Resolve<BulkSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            // Take ownership of the C array (zero-copy; released when the batch is disposed).
            imported = CArrowArrayImporter.ImportRecordBatch(batch, s.Schema);
            s.Push(imported); // ownership moves into the channel (or disposed if the consumer is gone)
            imported = null;
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            imported?.Dispose();
            SetError(err, ex);
            return FabricatorStatus.Error;
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
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            // Free the handle even on failure (the background task has been observed).
            Handles.Free(session);
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BeginTransaction(nint handle, int isExplicit, byte** err) =>
        RunTransactionOp(handle, c => c.BeginTransaction(isExplicit != 0), err);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CommitTransaction(nint handle, byte** err) => RunTransactionOp(handle, c => c.CommitTransaction(), err);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RollbackTransaction(nint handle, byte** err) => RunTransactionOp(handle, c => c.RollbackTransaction(), err);

    private static int RunTransactionOp(nint handle, Action<IBackendCatalog> op, byte** err)
    {
        try
        {
            // No fallback here: a transaction op on an unresolvable handle means the host holds a STALE catalog
            // handle (GCHandle slots are reused after a DETACH frees them, so the old value may resolve to an
            // arbitrary unrelated object) — surface it as the diagnostic it is instead of opening a nonsense
            // default catalog with an empty connection string.
            var catalog = Handles.Resolve<IBackendCatalog>(handle)
                          ?? throw new InvalidOperationException(
                              $"Fabricator: transaction op on a stale/unknown catalog handle 0x{handle:x} "
                              + $"(resolves to: {Handles.Resolve<object>(handle)?.GetType().FullName ?? "<freed>"})");
            op(catalog);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int BuildConnectionString(byte* provider, byte* secretType, byte* fieldsJson, byte* baseConnStr,
                                             byte** outConnStr, byte** err)
    {
        try
        {
            if (outConnStr is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var providerName = Marshal.PtrToStringUTF8((nint)provider); // null/empty => default backend
            var type = Marshal.PtrToStringUTF8((nint)secretType) ?? string.Empty; // the DuckDB secret type
            var baseConn = Marshal.PtrToStringUTF8((nint)baseConnStr) ?? string.Empty; // ATTACH target (may be empty)
            var json = Marshal.PtrToStringUTF8((nint)fieldsJson) ?? "{}";
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                         ?? new Dictionary<string, string>();
            // Case-insensitive: secret field names may be stored lower-cased (ours) or differ by provider (azure).
            var fields = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            var connStr = BackendRegistry.Resolve(providerName).BuildConnectionString(type, fields, baseConn);
            *outConnStr = (byte*)Marshal.StringToCoTaskMemUTF8(connStr);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFunctionParamSchema(nint handle, byte* schema, byte* func, CArrowSchema* outSchema,
                                              byte** err)
    {
        try
        {
            if (outSchema is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            // ArrowSchemaExport, not CArrowSchemaExporter: a function taking NO arguments has a zero-field
            // parameter schema, which Apache.Arrow cannot export at all (it throws ArgumentNullException on
            // 'fields'). The host treats any failure here as "the function is stale" and silently drops it, so
            // using the raw exporter makes every zero-argument function invisible. See ArrowSchemaExport.
            if (handle == 0) // global (connection-free) function — resolve by name (any kind), no catalog
            {
                ArrowSchemaExport.Export(GlobalFunctions.ParamSchema(f), outSchema);
                return FabricatorStatus.Ok;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            ArrowSchemaExport.Export(catalog.GetFunctionParamSchema(s, f), outSchema);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFunctionReturnSchema(nint handle, byte* schema, byte* func, CArrowSchema* outSchema,
                                               byte** err)
    {
        try
        {
            if (outSchema is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            if (handle == 0) // global (connection-free) function — scalar or aggregate return type
            {
                CArrowSchemaExporter.ExportSchema(new Schema(new[] { GlobalFunctions.ReturnField(f) }, null), outSchema);
                return FabricatorStatus.Ok;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
            var s = Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty;
            CArrowSchemaExporter.ExportSchema(catalog.GetFunctionReturnSchema(s, f), outSchema);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // -------------------------------------------------------------------------
    // Scalar-function session (ABI v80) — the successor to the removed stateless execute_scalar. Bind resolves
    // a per-CALL-SITE binding (result field + any bind state); execute reuses it per chunk; close frees it.
    // Mirrors TableFnBind / TableFnExecute / TableFnClose.
    // -------------------------------------------------------------------------
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ScalarFnBind(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                    byte* argConstant, CArrowSchema* outSchema, nint* outBinding, byte** err)
    {
        try
        {
            if (outSchema is null || outBinding is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            // `args` (nullable) is a 1-row stream of the call's arguments, read synchronously below. The values
            // are PARTIAL: `argConstant` is a mask, one char per argument, '1' = a folded constant whose value
            // is real, '0' = a runtime expression whose slot holds a NULL placeholder.
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var mask = Marshal.PtrToStringUTF8((nint)argConstant) ?? string.Empty;
            var constant = new bool[argsBatch?.ColumnCount ?? 0];
            for (int i = 0; i < constant.Length && i < mask.Length; i++)
            {
                constant[i] = mask[i] == '1';
            }
            var bindArgs = new ScalarBindArgs(argsBatch, constant);
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            // handle == 0 => a connection-free GLOBAL scalar: resolve from the global registry by name.
            var bound = handle == 0
                ? GlobalFunctions.BindScalar(f, bindArgs)
                : (Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty))
                    .ScalarFnBind(Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty, f, bindArgs);
            // Export the resolved result as a BARE ArrowSchema — the carrier get_function_return_schema uses.
            // ⚠ NOT tablefn_bind's zero-row stream: reading a stream's schema host-side goes through
            // PopulateReturnSchema, which clobbers the ambient host-FS opener, and a scalar binds wherever it is
            // called — including underneath a statement already doing host-FS IO. A null-typed field here is the
            // UNRESOLVED sentinel, "the declared type stands"; the host then keeps what it registered.
            var resultField = bound.ResolvedResult ?? new Field("result", NullType.Default, nullable: true);
            CArrowSchemaExporter.ExportSchema(new Schema(new[] { resultField }, null), outSchema);
            *outBinding = Handles.Alloc(bound);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ScalarFnExecute(nint binding, CArrowArrayStream* args, CArrowArrayStream* outStream,
                                       byte** err)
    {
        try
        {
            if (args is null || outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var bound = Handles.Resolve<ScalarBindingHandle>(binding);
            if (bound is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
            CArrowArrayStreamExporter.ExportArrayStream(ScalarBindingRunner.Execute(bound, argStream), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ScalarFnClose(nint binding, byte** err)
    {
        try
        {
            Handles.Resolve<ScalarBindingHandle>(binding)?.Dispose(); // idempotent
            Handles.Free(binding);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFunctionOutputSchema(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                               CArrowSchema* outSchema, byte** err)
    {
        try
        {
            if (outSchema is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var catalog = Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty);
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
            CArrowSchemaExporter.ExportSchema(catalog.GetFunctionOutputSchema(s, f, argsBatch), outSchema);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // (ExecuteTable / ExecuteProc handlers were removed at ABI v30 — superseded by the table-function
    //  session TableFnBind / TableFnExecute / TableFnClose.)

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
                return FabricatorStatus.InvalidArgument;
            }
            var inSchema = CArrowSchemaImporter.ImportSchema(inputSchema); // takes ownership of the C schema
            // `args` (nullable) is a 1-row stream of the constant cost args (read synchronously below).
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            // handle == 0 => a connection-free GLOBAL in-out / collector: resolve from the global registry by
            // name (a collector is wrapped as an IInOutBinding). Else the catalog path.
            var binding = handle == 0
                ? GlobalFunctions.ResolveInOut(f, argsBatch, inSchema)
                : (Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty))
                    .InOutBind(Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty, f, argsBatch, inSchema);
            // Export the binding's full output schema as a zero-row stream so the host can read return types.
            CArrowArrayStreamExporter.ExportArrayStream(
                new InMemoryArrayStream(binding.OutputSchema, System.Array.Empty<RecordBatch>()), outSchema);
            *outBinding = Handles.Alloc(binding);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InOutExchangeOpen(nint binding, CArrowArrayStream* input,
                                         CArrowArrayStream* output, byte** err)
    {
        try
        {
            if (input is null || output is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var b = Handles.Resolve<IInOutBinding>(binding);
            if (b is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            // Take ownership of the host's input stream; the pump pulls it (one chunk per gate tenure) + releases it.
            // The SQL isolation was resolved + set on the binding at inout_bind (C#), so it is not passed here.
            var inputStream = CArrowArrayStreamImporter.ImportArrayStream(input);
            CArrowArrayStreamExporter.ExportArrayStream(new InOutExchangeStream(b, inputStream), output);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InOutBindClose(nint binding, byte** err)
    {
        try
        {
            Handles.Resolve<IInOutBinding>(binding)?.Dispose(); // idempotent
            Handles.Free(binding);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // --- row-mapped (correlated LATERAL) functions, ABI v79. See ILateralTableFunction + LateralExchange. ---

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int LateralBind(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                   CArrowSchema* inputSchema, CArrowArrayStream* outSchema, nint* outBinding,
                                   byte** err)
    {
        try
        {
            if (inputSchema is null || outSchema is null || outBinding is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var inSchema = CArrowSchemaImporter.ImportSchema(inputSchema); // takes ownership of the C schema
            // `args` (nullable) carries the constant NAMED args only — a lateral function's positional
            // parameters ARE its per-row input columns and have no bind-time value (see abi.h).
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            var binding = handle == 0
                ? GlobalFunctions.ResolveLateral(f, argsBatch, inSchema)
                : (Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty))
                    .LateralBind(Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty, f, argsBatch, inSchema);
            var bound = new LateralBindingHandle(binding, f, inSchema);
            // The host binds its RETURN TYPES from this: the function's own columns, WITHOUT the provenance
            // column (which is transport, not a result column).
            CArrowArrayStreamExporter.ExportArrayStream(
                new InMemoryArrayStream(bound.OutputSchema, System.Array.Empty<RecordBatch>()), outSchema);
            *outBinding = Handles.Alloc(bound);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int LateralOpen(nint binding, nint* outSession, byte** err)
    {
        try
        {
            if (outSession is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var b = Handles.Resolve<LateralBindingHandle>(binding);
            if (b is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            // SEVERAL sessions may be open on one binding at once — the batched operator is parallel, so each
            // pipeline thread opens its own and nothing is shared.
            *outSession = Handles.Alloc(b.Open());
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int LateralCall(nint session, CArrowArray* input, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (input is null || outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<LateralSessionRunner>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var rows = CArrowArrayImporter.ImportRecordBatch(input, s.InputSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.Call(rows), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int LateralClose(nint session, byte** err)
    {
        try
        {
            Handles.Resolve<LateralSessionRunner>(session)?.Dispose(); // idempotent
            Handles.Free(session);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int LateralBindClose(nint binding, byte** err)
    {
        try
        {
            Handles.Resolve<LateralBindingHandle>(binding)?.Dispose(); // idempotent
            Handles.Free(binding);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableFnBind(nint handle, byte* schema, byte* func, CArrowArrayStream* args,
                                 CArrowArrayStream* outSchema, int* supportsPushdown, nint* outBinding, byte** err)
    {
        try
        {
            if (outSchema is null || supportsPushdown is null || outBinding is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            // `args` (nullable) is a 1-row stream of the constant call args (read synchronously below).
            RecordBatch? argsBatch = null;
            if (args is not null)
            {
                using var argStream = CArrowArrayStreamImporter.ImportArrayStream(args); // we own it
                argsBatch = argStream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            // handle == 0 => a connection-free GLOBAL table function: resolve from the global registry by name.
            var bound = handle == 0
                ? GlobalFunctions.ResolveTable(f, argsBatch)
                : (Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty))
                    .TableFnBind(Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty, f, argsBatch);
            // Export the binding's output schema as a zero-row stream so the host can read return types.
            CArrowArrayStreamExporter.ExportArrayStream(
                new InMemoryArrayStream(bound.OutputSchema, System.Array.Empty<RecordBatch>()), outSchema);
            *supportsPushdown = bound.MapResultByName ? 1 : 0;
            *outBinding = Handles.Alloc(bound);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableFnExecute(nint binding, byte* specJson, CArrowArrayStream* filterValues,
                                    CArrowArrayStream* outStream, int* schemaMayChange, byte** err)
    {
        try
        {
            if (schemaMayChange is not null)
            {
                *schemaMayChange = 0;
            }
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var bound = Handles.Resolve<IBoundTableFunction>(binding);
            if (bound is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var spec = Marshal.PtrToStringUTF8((nint)specJson); // null => SELECT *
            IArrowArrayStream? filters =
                filterValues is null ? null : CArrowArrayStreamImporter.ImportArrayStream(filterValues);
            var rows = bound.Execute(spec, filters);
            // ⚠ READ THE FLAG AFTER Execute() RETURNS AND BEFORE THE STREAM IS DRAINED — that ordering is the
            // whole contract (abi.h §tablefn_execute). A binding whose DDL sits in an async-iterator body has
            // not run it yet at this point, because an iterator does not begin until the first batch PULL, a
            // different crossing entirely. So a function reporting through this flag must do its work in the
            // EAGER part of Execute(), and the host reads what that part decided.
            if (schemaMayChange is not null && bound.SchemaMayChange)
            {
                *schemaMayChange = 1;
            }
            CArrowArrayStreamExporter.ExportArrayStream(rows, outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int TableFnClose(nint binding, byte** err)
    {
        try
        {
            Handles.Resolve<IBoundTableFunction>(binding)?.Dispose(); // idempotent
            Handles.Free(binding);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // -------------------------------------------------------------------------
    // Provider-declared settings (see docs/settings-architecture.md). list_settings returns ALL registered
    // providers' declared settings (not catalog-scoped); set_setting pushes a value into the process-wide
    // ProviderSettingsStore. Six string columns: provider, name, type, default, description, min.
    // -------------------------------------------------------------------------
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ListSettings(CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var provider = new StringArray.Builder();
            var name = new StringArray.Builder();
            var type = new StringArray.Builder();
            var def = new StringArray.Builder();
            var desc = new StringArray.Builder();
            var min = new StringArray.Builder();
            int rows = 0;
            void Emit(string providerName, ProviderSetting s)
            {
                provider.Append(providerName);
                name.Append(s.Name);
                type.Append(s.Type switch
                {
                    ProviderSettingType.Bool => "bool",
                    ProviderSettingType.Long => "long",
                    _ => "varchar",
                });
                var rendered = RenderSettingValue(s.Default);
                if (rendered is null) { def.AppendNull(); } else { def.Append(rendered); }
                desc.Append(s.Description ?? string.Empty);
                if (s.Min is long m) { min.Append(m.ToString()); } else { min.AppendNull(); }
                rows++;
            }
            // Host settings FIRST, mirroring HostGlobalFunctions: a setting governing the plugin machinery
            // has to exist when NO provider has loaded, which is exactly the case it is most needed in. The
            // `provider` column is opaque to the host, so the pseudo-provider name round-trips through
            // set_setting with no C++ change.
            foreach (var s in HostSettings.Settings)
            {
                Emit(HostSettings.Provider, s);
            }
            foreach (var backend in BackendRegistry.All())
            {
                foreach (var s in backend.Settings)
                {
                    Emit(backend.Name, s);
                }
            }
            var schema = new Schema(new[]
            {
                new Field("provider", StringType.Default, nullable: false),
                new Field("name", StringType.Default, nullable: false),
                new Field("type", StringType.Default, nullable: false),
                new Field("default", StringType.Default, nullable: true),
                new Field("description", StringType.Default, nullable: true),
                new Field("min", StringType.Default, nullable: true),
            }, metadata: null);
            var batch = new RecordBatch(schema, new IArrowArray[]
            {
                provider.Build(), name.Build(), type.Build(), def.Build(), desc.Build(), min.Build(),
            }, rows);
            CArrowArrayStreamExporter.ExportArrayStream(new InMemoryArrayStream(schema, new[] { batch }), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // Load-time GLOBAL functions (see docs/global-functions.md). Returns the provider-union of connection-free
    // global functions so the host registers each as a bare fn(...) at extension load. Columns: name, kind,
    // param_count, return_type (return_type meaningful for kind='scalar'). Per-function schemas + execution
    // reuse the scalar entries with handle=0. Currently scalar only; table/in-out kinds slot in here later.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ListGlobalFunctions(CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var name = new StringArray.Builder();
            var kind = new StringArray.Builder();
            // "1" iff the function's source orders strings byte/binary (string ordering + BETWEEN safe to push);
            // only meaningful for table functions, "0" for the other kinds. Read by the C++ load-time registrar.
            var stringOrder = new StringArray.Builder();
            // kind='macro' only: the provider's complete CREATE MACRO statement, parsed + registered by the
            // C++ load-time registrar (DuckDB's own parser owns the grammar). Empty for every other kind.
            var body = new StringArray.Builder();
            var paramCount = new Int32Array.Builder();
            var returnType = new StringArray.Builder();
            int rows = 0;
            foreach (var fn in GlobalFunctions.AllScalars())
            {
                name.Append(fn.Name);
                kind.Append("scalar");
                stringOrder.Append("0");
                body.Append(string.Empty);
                paramCount.Append(fn.Parameters.FieldsList.Count);
                // "ANY" when the function declares no fixed return type (resolved per call site at
                // scalarfn_bind). ⚠ Dereferencing fn.Result here without the ?. drops EVERY managed global
                // function, silently: one throw inside this enumeration fails the whole list_global_functions
                // crossing, and the host's registrar then has nothing to register.
                returnType.Append(fn.Result?.DataType.Name ?? "ANY");
                rows++;
            }
            foreach (var fn in GlobalFunctions.AllInOut())
            {
                name.Append(fn.Name);
                kind.Append("inout");
                stringOrder.Append("0");
                body.Append(string.Empty);
                paramCount.Append(Params.DeclaredCount(fn.Parameters));
                returnType.Append(string.Empty);
                rows++;
            }
            foreach (var fn in GlobalFunctions.AllLaterals())
            {
                name.Append(fn.Name);
                kind.Append("lateral");
                stringOrder.Append("0");
                body.Append(string.Empty);
                // Positional + named: for a lateral function the positional half IS the per-row input, and
                // each of those occupies a DuckDB argument slot, so the count is its full arity.
                paramCount.Append(Params.DeclaredCount(fn.Parameters));
                returnType.Append(string.Empty);
                rows++;
            }
            foreach (var fn in GlobalFunctions.AllCollectors())
            {
                name.Append(fn.Name);
                kind.Append("collector");
                stringOrder.Append("0");
                body.Append(string.Empty);
                paramCount.Append(Params.DeclaredCount(fn.Parameters));
                returnType.Append(string.Empty);
                rows++;
            }
            foreach (var fn in GlobalFunctions.AllTables())
            {
                name.Append(fn.Name);
                kind.Append("table");
                stringOrder.Append(fn.StringOrderPushable ? "1" : "0");
                body.Append(string.Empty);
                paramCount.Append(fn.Parameters.FieldsList.Count);
                returnType.Append(string.Empty);
                rows++;
            }
            foreach (var fn in GlobalFunctions.AllAggregates())
            {
                name.Append(fn.Name);
                kind.Append(fn.SupportsSpill ? "aggregate_spill" : "aggregate");
                stringOrder.Append("0");
                body.Append(string.Empty);
                paramCount.Append(fn.Parameters.FieldsList.Count);
                returnType.Append(fn.Result.DataType.Name);
                rows++;
            }
            // SQL-GENERATING table functions (v68): registered with bind_replace only — the call is rewritten
            // into generated SQL at bind time. param_count is POSITIONAL + NAMED (the host splits them by the
            // per-field fabricator.param_style tag on the param schema); no return type (the plan decides it).
            foreach (var fn in GlobalFunctions.AllSqlTables())
            {
                name.Append(fn.Name);
                kind.Append("table_sql");
                stringOrder.Append("0");
                body.Append(string.Empty);
                paramCount.Append(Params.DeclaredCount(fn.Parameters));
                returnType.Append(string.Empty);
                rows++;
            }
            // MACROs: SQL templates registered into DuckDB's system catalog at load. No param/return metadata
            // crosses — the parsed CREATE MACRO statement carries the signature AND the scalar/table kind.
            foreach (var macro in GlobalFunctions.AllMacros())
            {
                name.Append(macro.Name);
                kind.Append("macro");
                stringOrder.Append("0");
                body.Append(macro.CreateSql);
                paramCount.Append(0);
                returnType.Append(string.Empty);
                rows++;
            }
            var schema = new Schema(new[]
            {
                new Field("name", StringType.Default, nullable: false),
                new Field("kind", StringType.Default, nullable: false),
                new Field("string_order", StringType.Default, nullable: false),
                new Field("body", StringType.Default, nullable: false),
                new Field("param_count", Int32Type.Default, nullable: false),
                new Field("return_type", StringType.Default, nullable: true),
            }, metadata: null);
            var batch = new RecordBatch(schema, new IArrowArray[]
            {
                name.Build(), kind.Build(), stringOrder.Build(), body.Build(), paramCount.Build(), returnType.Build(),
            }, rows);
            CArrowArrayStreamExporter.ExportArrayStream(new InMemoryArrayStream(schema, new[] { batch }), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // Provider-declared secret fields (see docs/provider-extensibility.md §2). Returns ALL registered
    // providers' secret types + fields so the host registers each secret type + its CREATE SECRET named
    // parameters generically. Five columns: provider, secret_type, name, type ("varchar"|"integer"|
    // "boolean"), redact ("1"|"0"). A provider with an empty SecretType contributes no rows.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ListSecretFields(CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var provider = new StringArray.Builder();
            var secretType = new StringArray.Builder();
            var name = new StringArray.Builder();
            var type = new StringArray.Builder();
            var redact = new StringArray.Builder();
            int rows = 0;
            foreach (var backend in BackendRegistry.All())
            {
                if (string.IsNullOrEmpty(backend.SecretType))
                {
                    continue;
                }
                foreach (var f in backend.SecretFields)
                {
                    provider.Append(backend.Name);
                    secretType.Append(backend.SecretType);
                    name.Append(f.Name);
                    type.Append(f.Type switch
                    {
                        SecretFieldType.Integer => "integer",
                        SecretFieldType.Boolean => "boolean",
                        _ => "varchar",
                    });
                    redact.Append(f.Redact ? "1" : "0");
                    rows++;
                }
            }
            var schema = new Schema(new[]
            {
                new Field("provider", StringType.Default, nullable: false),
                new Field("secret_type", StringType.Default, nullable: false),
                new Field("name", StringType.Default, nullable: false),
                new Field("type", StringType.Default, nullable: false),
                new Field("redact", StringType.Default, nullable: false),
            }, metadata: null);
            var batch = new RecordBatch(schema, new IArrowArray[]
            {
                provider.Build(), secretType.Build(), name.Build(), type.Build(), redact.Build(),
            }, rows);
            CArrowArrayStreamExporter.ExportArrayStream(new InMemoryArrayStream(schema, new[] { batch }), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // Renders a setting default/value to the string transport form (bool -> true/false, long/int -> digits,
    // anything else -> ToString); null => unset (the caller appends a null).
    private static string? RenderSettingValue(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        long l => l.ToString(),
        int i => i.ToString(),
        _ => value.ToString(),
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetSetting(long session, byte* provider, byte* name, byte* value, byte** err)
    {
        try
        {
            var p = Marshal.PtrToStringUTF8((nint)provider) ?? string.Empty;
            var n = Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
            var v = Marshal.PtrToStringUTF8((nint)value); // null => unset / reset
            // `session` honours DuckDB's SetScope: 0 (a SET GLOBAL, or a registration default) writes the
            // process-wide layer, anything else this DuckDB connection's own. SetForSession routes 0 to the
            // global layer itself, so there is no branch here to get wrong.
            ProviderSettingsStore.Instance.SetForSession(session, p, n, v);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    // The owning DuckDB connection closed — drop its session-scoped settings so a later connection landing on
    // the same ClientContext ADDRESS cannot inherit them. Called from a C++ destructor, so it must not throw
    // across the boundary; the catch below is what guarantees that.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ClearSessionSettings(long session, byte** err)
    {
        try
        {
            ProviderSettingsStore.Instance.ClearSession(session);
            // Off by default. The C++ half of this — a ClientContextState destructor firing on connection
            // close — is invisible from SQL and has no other observable, so this line is how it gets
            // verified at all; leaving it in makes "did the connection's settings get reclaimed?" a grep
            // rather than a rebuild.
            BridgeLog.LogDebug("settings: cleared session {Session} ({Remaining} session(s) still held)",
                               session, ProviderSettingsStore.Instance.SessionCount);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var f = Marshal.PtrToStringUTF8((nint)func) ?? string.Empty;
            // handle == 0 => a connection-free GLOBAL aggregate: open a session from the global registry by name.
            var session = handle == 0
                ? GlobalFunctions.ResolveAggregate(f)
                : (Handles.Resolve<IBackendCatalog>(handle) ?? BackendRegistry.Active.OpenCatalog(string.Empty, string.Empty))
                    .AggOpen(Marshal.PtrToStringUTF8((nint)schema) ?? string.Empty, f);
            *outSession = Handles.Alloc(session);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggUpdate(nint session, CArrowArray* batch, byte** err)
    {
        try
        {
            if (batch is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(batch, s.UpdateSchema); // takes ownership
            s.Update(rb);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggCombine(nint session, CArrowArray* batch, byte** err)
    {
        try
        {
            if (batch is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(batch, AggCombineSchema); // takes ownership
            s.Combine(rb);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggFinalize(nint session, CArrowArray* ids, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (ids is null || outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(ids, AggIdsSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.Finalize(rb), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggDestroy(nint session, CArrowArray* ids, byte** err)
    {
        try
        {
            if (ids is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var rb = CArrowArrayImporter.ImportRecordBatch(ids, AggIdsSchema); // takes ownership (must release)
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                rb.Dispose();
                return FabricatorStatus.Ok; // session already closed — nothing to drop
            }
            s.Destroy(rb);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggClose(nint session, byte** err)
    {
        try
        {
            Handles.Resolve<IAggregateSession>(session)?.Close(); // idempotent
            Handles.Free(session);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var states = CArrowArrayImporter.ImportRecordBatch(groupStates, AggStateSchema); // takes ownership
            var rows = CArrowArrayImporter.ImportRecordBatch(batch, s.UpdateSchema);          // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.UpdateSpill(states, rows), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var target = CArrowArrayImporter.ImportRecordBatch(targetStates, AggStateSchema);       // takes ownership
            var batch = CArrowArrayImporter.ImportRecordBatch(combineBatch, AggCombineBatchSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.CombineSpill(target, batch), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AggFinalizeSpill(nint session, CArrowArray* states, CArrowArrayStream* outStream, byte** err)
    {
        try
        {
            if (states is null || outStream is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var s = Handles.Resolve<IAggregateSession>(session);
            if (s is null)
            {
                return FabricatorStatus.InvalidArgument;
            }
            var batch = CArrowArrayImporter.ImportRecordBatch(states, AggStateSchema); // takes ownership
            CArrowArrayStreamExporter.ExportArrayStream(s.FinalizeSpill(batch), outStream);
            return FabricatorStatus.Ok;
        }
        catch (Exception ex)
        {
            SetError(err, ex);
            return FabricatorStatus.Error;
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
    // `op` binds to the calling ABI handler's method name via [CallerMemberName], so EVERY failed bridge
    // crossing is logged centrally with its operation and exception — no per-handler logging needed.
    private static void SetError(byte** err, Exception ex, [CallerMemberName] string op = "")
    {
        BridgeLog.LogWarning(ex, "abi {Op} failed: {Message}", op, ex.Message);
        if (err is not null)
        {
            *err = (byte*)Marshal.StringToCoTaskMemUTF8(FormatError(ex));
        }
    }

    /// <summary>Splits a comma-separated column list (from a native PARTITIONED BY clause, marshaled by C++) into a
    /// trimmed non-empty list, or null when absent/empty. Providers that don't partition ignore the argument.</summary>
    private static IReadOnlyList<string>? SplitColumnList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }
        var list = new List<string>();
        foreach (var part in csv.Split(','))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                list.Add(part.Trim());
            }
        }
        return list.Count > 0 ? list : null;
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
