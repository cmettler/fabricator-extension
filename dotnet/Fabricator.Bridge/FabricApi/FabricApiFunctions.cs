// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.Fabric.Api.Core.Models;

namespace Fabricator.Bridge;

/// <summary>
/// Registers the Fabric REST API functions onto a catalog. Public (with a primitive signature) because the set
/// is hosted by more than one provider: a OneLake Delta attach, which takes the workspace/item defaults from its
/// root, and a Fabric SQL attach, which takes them from ATTACH options in another assembly. See
/// <c>DeltaCatalog.BuildFunctionSet</c>, <c>SqlServerCatalog.BuildFunctionSet</c> and
/// docs/fabric-api-functions.md §9h.
/// </summary>
public static class FabricApiFunctions
{
    /// <summary>
    /// The schema every function in this set lives in: <c>&lt;catalog&gt;.fabric.&lt;name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>A dedicated schema rather than the <c>__all__</c> sentinel</b>, which is what these used to use.
    /// Three reasons, in order of how much they matter:</para>
    /// <list type="number">
    ///   <item>The sentinel advertises every function once PER DISCOVERED SCHEMA, so on a schema-enabled
    ///   lakehouse with <c>dbo</c> and <c>dbt</c> each of these appeared TWICE in <c>duckdb_functions()</c> —
    ///   ~50 functions rendered as ~100 entries. One schema means one entry each.</item>
    ///   <item><c>fabric.sessions()</c> says what it is; the old <c>dbo.fabric&#95;sessions()</c> encoded the
    ///   grouping in a NAME PREFIX because there was nowhere else to put it, and put API surface in the schema
    ///   the user's own TABLES live in.</item>
    ///   <item>It separates namespaces that were only ever conflated by accident: <c>dbo</c> is a DATA schema
    ///   the provider discovered from storage, <c>fabric</c> is a function namespace the provider declares.</item>
    /// </list>
    /// <para><b>⚠ The name must be declared PROVIDER-SIDE as a schema, or every function here silently
    /// vanishes.</b> <c>FabricatorCatalog::LoadCatalog</c> skips a declared function whose schema it did not
    /// discover (<c>if (sit == schemas_.end()) continue;</c>) — deliberately, because that is how the ATTACH
    /// <c>schema_filter</c> reaches functions. So each hosting catalog must add this name to the schema
    /// metadata it answers, and ONLY when this set is actually registered. See
    /// <c>DeltaCatalog.CatalogSchemaNames</c> and <c>SqlServerBackend.SchemasMetadata</c>.</para>
    /// <para><b>⚠ It must NOT join the <c>__all__</c> expansion list.</b> That list means "every DATA schema",
    /// and feeding this name into it would declare the provider's <c>__all__</c> macros and
    /// <c>fab_delta_info</c> inside <c>fabric</c> too — the duplication this change exists to remove.</para>
    /// </remarks>
    public const string SchemaName = "fabric";

    /// <param name="workspace">Default workspace (display name or GUID); null/empty ⇒ callers must pass
    /// <c>workspace :=</c>.</param>
    /// <param name="item">Default item (display name, <c>name.Type</c>, or GUID); null/empty ⇒ callers must
    /// pass <c>item :=</c>.</param>
    /// <param name="credential">Entra credential from the ATTACH secret; null ⇒ the ambient chain.</param>
    public static void Register(
        List<ICatalogScalarFunction> scalars, List<ICatalogTableFunction> tables,
        string? workspace, string? item, Azure.Core.TokenCredential? credential)
        => Register(scalars, tables, new FabricApiContext(workspace, item, credential));

    internal static void Register(
        List<ICatalogScalarFunction> scalars, List<ICatalogTableFunction> tables, FabricApiContext context)
    {
        // ONE client per catalog, shared by every function: it caches name→GUID resolutions, and those listings
        // are throttled per principal.
        var api = new FabricApiClient(context);

        // ONE registration per table function: its options are DuckDB NAMED parameters, so
        // `refresh_sql_endpoint()` and `refresh_sql_endpoint(recreate := true)` are the same
        // function. (This replaced an `_ex` sibling per function, which existed only because positional table
        // arguments have no defaults — see the named-parameter support in fabricator_schema_entry.cpp.)
        tables.Add(new FabricRefreshSqlEndpointFunction(api));
        tables.Add(new FabricListShortcutsFunction(api));
        scalars.Add(new FabricCreateShortcutFunction(api, "create_shortcut", ShortcutMode.Create));
        scalars.Add(new FabricCreateShortcutFunction(api, "alter_shortcut", ShortcutMode.Alter));
        // `_ex` adds the conflict policy. Without it a OneLake-target caller could not reach
        // CreateOrOverwrite / GenerateUniqueName at all (only the JSON variant took a policy), and
        // CreateOrOverwrite is the right shape for an IDEMPOTENT script: Fabric's shortcut metadata is
        // eventually consistent, so drop-then-create can transiently 409 on the name it just removed.
        scalars.Add(new FabricCreateShortcutFunction(api, "create_shortcut_ex", ShortcutMode.Create,
                                                     withPolicy: true));
        scalars.Add(new FabricCreateShortcutJsonFunction(api));
        scalars.Add(new FabricDropShortcutFunction(api));
        // Parameterized notebook runs: parameters ride executionData.parameters, the shape live-verified to
        // be honoured (the generic top-level array is accepted and silently ignored — docs §9d).
        tables.Add(new FabricRunNotebookFunction(api));
        // Jobs: table maintenance (V-Order, which our own OPTIMIZE cannot produce), the generic runner, and
        // status/history/cancel. They share one submit+poll path.
        FabricJobFunctions.Register(scalars, tables, api);
        // Semantic models: list + enhanced refresh + refresh history. On the POWER BI REST surface (the
        // Fabric SDK cannot refresh one), but on the SAME token — so this needs no extra credential.
        FabricSemanticModelFunctions.Register(tables, api);
        // Read-only introspection: the identifiers the write functions above need (a connection GUID for an
        // external shortcut target, an endpoint connection string for a T-SQL ATTACH, a workspace/item name).
        FabricInspectFunctions.Register(tables, api);
        // P3: the promotion surfaces (git + deployment pipelines) and the remaining platform reads. Both were
        // filed "demand-driven" in §10 and are here because they were asked for; their WRITE halves that carry
        // credentials or item definitions stay excluded. See each file's remarks.
        FabricPromotionFunctions.Register(tables, api);
        FabricPlatformFunctions.Register(tables, api);
        // Variable libraries: per-environment configuration. An ItemReference variable holds exactly the
        // {workspaceId, itemId} pair the `workspace :=` / `item :=` overrides above consume, so a project can
        // read its target from the library instead of hardcoding it. Reading VALUES means reading the item
        // DEFINITION (an LRO) — there is no effective-value API.
        FabricVariableFunctions.Register(scalars, tables, api);
        // Spark/Livy session monitoring. Complements the job functions rather than overlapping them: this is
        // the SPARK-level detail (queued vs running time, runtime version, attempt) that a job instance does
        // not carry, and job instances cover item kinds that never produce a Livy session. Workspace-scoped, so
        // one request — no fan-out, and no item argument. (The two DO join on job_instance_id — measured; see
        // FabricSessionFunctions for the two claims the live data falsified.)
        FabricSessionFunctions.Register(tables, api);
    }

    internal enum ShortcutMode
    {
        /// <summary>Default conflict policy (Abort) — verified to 409 on an existing name, so "create" for free.</summary>
        Create,

        /// <summary><c>OverwriteOnly</c> — fails when absent, which is the SQL-ish ALTER semantic.</summary>
        Alter,
    }

    // Shared Arrow field shapes. A shortcut path/name is user text; nullability is uniform "true" because
    // Arrow-side nullability is not a constraint we enforce, and the host maps by name.
    internal static Field Str(string name) => new(name, StringType.Default, nullable: true);

    /// <summary>
    /// Runs a scalar function body, attaching the stack trace to any UNEXPECTED exception.
    /// </summary>
    /// <remarks>
    /// The ABI carries only a string to the host, and the bridge's warning sink renders just type + message — so
    /// a bare framework message like "Value cannot be null. (Parameter 'value')" arrives with no indication of
    /// which of a dozen marshaling steps produced it. Errors we raise ourselves already name their cause and are
    /// passed through untouched.
    /// </remarks>
    internal static IArrowArray Guarded(string what, Func<IArrowArray> body)
    {
        try
        {
            return body();
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NotSupportedException($"fabric {what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
        }
    }

    // UTC microsecond timestamps: what DuckDB's TIMESTAMP maps to, so no lossy conversion at the boundary.
    /// <summary>The ONE timestamp type these functions use. Shared by the field declaration and the array
    /// builder so the two cannot disagree — see <see cref="TsBuilder"/>.</summary>
    internal static readonly TimestampType TsType = new(Apache.Arrow.Types.TimeUnit.Microsecond, "UTC");

    internal static Field Ts(string name) => new(name, TsType, nullable: true);

    /// <summary>
    /// A builder for a <see cref="Ts"/> column. Use this, never the parameterless
    /// <c>TimestampArray.Builder</c> constructor.
    /// </summary>
    /// <remarks>
    /// ⚠ The parameterless <c>TimestampArray.Builder()</c> defaults to <b>MILLISECOND</b> while these columns
    /// are declared MICROSECOND, and nothing anywhere reports the mismatch: the array is built with millisecond
    /// values, the schema says microseconds, and the host faithfully reads the number it was given. Every
    /// timestamp then lands in <b>January 1970</b> — 1000× too small. That shipped on every hand-rolled
    /// function here (refresh, jobs, notebook runs, semantic-model refreshes) and survived live validation of
    /// all of them, because each run was checked for its status and ids and nobody looked at the times.
    /// Functions built on <c>FabricRowBuilder</c> were never affected — it creates each builder FROM the
    /// declared field, which is exactly the property this helper restores for the rest.
    /// </remarks>
    internal static TimestampArray.Builder TsBuilder() => new(TsType);

    /// <summary>A BIGINT column (byte/row counts, durations).</summary>
    internal static Field Int64(string name) => new(name, Int64Type.Default, nullable: true);

    /// <summary>An INTEGER column — used where the service itself models the value as 32-bit (a stage order).</summary>
    internal static Field Int32(string name) => new(name, Int32Type.Default, nullable: true);

    internal static Field Bool(string name) => new(name, BooleanType.Default, nullable: true);

    /// <summary>A DOUBLE column — where the service reports a real-valued measure (a duration in seconds).</summary>
    internal static Field Dbl(string name) => new(name, DoubleType.Default, nullable: true);
}

/// <summary>
/// Base for a Fabric table function's binding: fixed output schema, no pushdown, rows produced eagerly by an
/// override.
/// </summary>
/// <remarks>
/// <c>Execute</c> is a PLAIN method that disposes the pushed filter values BEFORE delegating to the async
/// iterator. That is not stylistic: an async-iterator body does not begin until the first <c>MoveNextAsync</c>,
/// which happens inside the host's <c>get_next</c> — long after <c>InitGlobal</c> returned — and the filter-value
/// producer is owned by the scan's global state. Disposing inside the iterator is the documented late-release
/// use-after-free (it aborts on macOS, silently corrupts elsewhere). Same contract as
/// <see cref="StaticTableFunction"/>.
/// </remarks>
internal abstract class FabricTableBinding : ITableFunctionBinding
{
    public abstract Schema OutputSchema { get; }

    public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

    public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
    {
        scan.FilterValues?.Dispose();
        return Rows(ct);
    }

    protected abstract IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct);

    public virtual void Dispose()
    {
    }

    /// <summary>Yields one batch, or nothing when the result is empty (an empty batch is not a valid row block).</summary>
    protected static async IAsyncEnumerable<RecordBatch> One(Schema schema, IArrowArray[] columns, int rows)
    {
        await System.Threading.Tasks.Task.CompletedTask;
        if (rows > 0)
        {
            yield return new RecordBatch(schema, columns, rows);
        }
    }

    /// <summary>Yields <paramref name="batch"/> when it has rows; a null batch (no rows) yields nothing.</summary>
    protected static async IAsyncEnumerable<RecordBatch> One(RecordBatch? batch)
    {
        await System.Threading.Tasks.Task.CompletedTask;
        if (batch is not null)
        {
            yield return batch;
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// refresh_sql_endpoint() — THE dbt unblocker.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>db.fabric.refresh_sql_endpoint([recreate [, timeout_seconds]])</c> — forces the lakehouse's
/// SQL analytics endpoint to sync its metadata NOW, returning one row per table with its sync status.
/// </summary>
/// <remarks>
/// <para>A table created through this provider is invisible to the endpoint until Fabric's asynchronous detection
/// notices it, which is a race for any dbt DAG whose next model reads it over T-SQL. This call is the documented
/// escape hatch, and the SDK's method is already a BLOCKING long-running-operation helper — so the function
/// simply returns when the sync is done, which is what a hook needs.</para>
/// <para><b>The dbt trap</b>: our Delta writes commit the log at DuckDB COMMIT, so an in-transaction post-hook
/// would refresh BEFORE the table exists. Use <c>transaction: false</c> or an <c>on-run-end</c> hook.</para>
/// </remarks>
internal sealed class FabricRefreshSqlEndpointFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricRefreshSqlEndpointFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "refresh_sql_endpoint";

    /// <summary>No positional arguments — everything comes from the ATTACH.</summary>
    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    /// <summary>
    /// All optional: <c>fabric.refresh_sql_endpoint(recreate := true)</c>, and
    /// <c>fabric.refresh_sql_endpoint(item := 'OtherLH')</c> to refresh a DIFFERENT lakehouse's endpoint than
    /// the one this catalog is attached to — a dbt project commonly writes to several.
    /// </summary>
    public Schema NamedParameters { get; } = new Schema(new[]
    {
        new Field("recreate", BooleanType.Default, nullable: true),
        new Field("timeout_seconds", Int64Type.Default, nullable: true),
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item"),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("table_name"),
            FabricApiFunctions.Str("status"),
            FabricApiFunctions.Ts("start_time"),
            FabricApiFunctions.Ts("end_time"),
            FabricApiFunctions.Ts("last_successful_sync"),
            FabricApiFunctions.Str("error_code"),
            FabricApiFunctions.Str("error_message"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly bool? _recreate;
        private readonly long? _timeoutSeconds;
        private readonly string? _workspace;
        private readonly string? _item;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            _api = api;
            _recreate = FabricArgs.Bool(args, 0);
            _timeoutSeconds = FabricArgs.Int(args, 1);
            _workspace = FabricArgs.Str(args, 2);
            _item = FabricArgs.Str(args, 3);
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            // NULL falls back to the ATTACH's own workspace/lakehouse — the zero-argument case.
            var ws = _api.ResolveWorkspace(_workspace);
            var lh = _api.ResolveItem(_item, "Lakehouse", ws);
            var ep = _api.ResolveSqlEndpointId(ws, lh);

            Microsoft.Fabric.Api.SQLEndpoint.Models.SqlEndpointRefreshMetadataRequest? request = null;
            if (_recreate is not null || _timeoutSeconds is not null)
            {
                request = new Microsoft.Fabric.Api.SQLEndpoint.Models.SqlEndpointRefreshMetadataRequest();
                if (_recreate is not null) { request.RecreateTables = _recreate; }
                if (_timeoutSeconds is not null)
                {
                    request.Timeout = new Microsoft.Fabric.Api.SQLEndpoint.Models.Duration(
                        _timeoutSeconds.Value, Microsoft.Fabric.Api.SQLEndpoint.Models.TimeUnit.Seconds);
                }
            }

            var statuses = FabricApiClient.Wrap("refresh_sql_endpoint", () =>
                _api.Client.SQLEndpoint.Items.RefreshSqlEndpointMetadata(ws, ep, request!, ct).Value);

            var names = new StringArray.Builder();
            var status = new StringArray.Builder();
            var start = FabricApiFunctions.TsBuilder();
            var end = FabricApiFunctions.TsBuilder();
            var lastOk = FabricApiFunctions.TsBuilder();
            var errCode = new StringArray.Builder();
            var errMsg = new StringArray.Builder();
            int n = 0;
            foreach (var t in statuses.Value ?? (IReadOnlyList<Microsoft.Fabric.Api.SQLEndpoint.Models.TableSyncStatus>)
                         System.Array.Empty<Microsoft.Fabric.Api.SQLEndpoint.Models.TableSyncStatus>())
            {
                names.Append(t.TableName);
                status.Append(t.Status.ToString());
                start.Append(t.StartDateTime);
                end.Append(t.EndDateTime);
                lastOk.Append(t.LastSuccessfulSyncDateTime);
                errCode.Append(t.Error?.ErrorCode);
                errMsg.Append(t.Error?.Message);
                n++;
            }
            return One(Columns, new IArrowArray[]
            {
                names.Build(), status.Build(), start.Build(), end.Build(), lastOk.Build(),
                errCode.Build(), errMsg.Build(),
            }, n);
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// Shortcuts.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// <c>fabric.create_shortcut(path, name, target_workspace, target_item, target_path)</c> and its
/// <c>alter_shortcut</c> twin — a OneLake-internal shortcut, the case that needs no pre-provisioned
/// connection. External targets go through <see cref="FabricCreateShortcutJsonFunction"/>.
/// </summary>
/// <remarks>
/// Returns the created shortcut's full path. <c>target_workspace</c>/<c>target_item</c> accept a display name or
/// a GUID, and NULL means "this catalog's own workspace / lakehouse" — so a same-lakehouse shortcut is
/// <c>fabric.create_shortcut('Tables', 'ref', NULL, NULL, 'Tables/orders')</c>.
/// </remarks>
internal sealed class FabricCreateShortcutFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;
    private readonly FabricApiFunctions.ShortcutMode _mode;
    private readonly bool _withPolicy;

    internal FabricCreateShortcutFunction(
        FabricApiClient api, string name, FabricApiFunctions.ShortcutMode mode, bool withPolicy = false)
    {
        _api = api;
        Name = name;
        _mode = mode;
        _withPolicy = withPolicy;
        var fields = new List<Field>
        {
            FabricApiFunctions.Str("path"),
            FabricApiFunctions.Str("name"),
            FabricApiFunctions.Str("target_workspace"),
            FabricApiFunctions.Str("target_item"),
            FabricApiFunctions.Str("target_path"),
        };
        if (withPolicy)
        {
            fields.Add(FabricApiFunctions.Str("conflict_policy"));
        }
        Parameters = new Schema(fields, null);
    }

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name { get; }

    public Schema Parameters { get; }

    public Field Result { get; } = FabricApiFunctions.Str("shortcut_path");

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new StringArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            string path = FabricArgs.Str(args, 0, row) ?? throw Missing("path");
            string name = FabricArgs.Str(args, 1, row) ?? throw Missing("name");
            string targetPath = FabricArgs.Str(args, 4, row) ?? throw Missing("target_path");
            var ws = _api.ResolveWorkspace(FabricArgs.Str(args, 2, row));
            var item = _api.ResolveItem(FabricArgs.Str(args, 3, row), "Lakehouse");

            // NOTE: OneLake's ctor is (itemId, workspaceId, path) — ITEM FIRST. The intuitive order compiles
            // (both Guid) and silently points the shortcut at the wrong object, so these stay named.
            var target = new CreatableShortcutTarget
            {
                OneLake = new OneLake(itemId: item, workspaceId: ws, path: targetPath),
            };
            var policy = _withPolicy ? FabricShortcutPath.ParsePolicy(FabricArgs.Str(args, 5, row)) : null;
            b.Append(Apply(_api, _mode, path, name, target, policy));
        }
        return b.Build();
    });

    /// <summary>Shared by the JSON-target variant: same call, same policy mapping, different target construction.</summary>
    internal static string Apply(
        FabricApiClient api, FabricApiFunctions.ShortcutMode mode, string path, string name,
        CreatableShortcutTarget target, ShortcutConflictPolicy? explicitPolicy = null)
    {
        // Default (no policy) = Abort, verified live: a duplicate name 409s EntityConflict /
        // ShorcutsOperationNotAllowed (Microsoft's typo). OverwriteOnly fails when ABSENT, which is why it is
        // the ALTER semantic rather than CreateOrOverwrite.
        //
        // The (ShortcutConflictPolicy?) cast is LOAD-BEARING, not tidying. ShortcutConflictPolicy is an
        // Azure-style extensible enum with an IMPLICIT CONVERSION FROM STRING, so `cond ? OverwriteOnly : null`
        // infers `string` for the ternary (null is a valid string) and then converts it back — calling
        // op_Implicit(null), which throws ArgumentNullException("value") from inside the SDK. It compiles
        // cleanly and fails at run time with a message naming nothing recognizable.
        ShortcutConflictPolicy? policy = explicitPolicy
            ?? (mode == FabricApiFunctions.ShortcutMode.Alter
                    ? ShortcutConflictPolicy.OverwriteOnly
                    : (ShortcutConflictPolicy?)null);
        var ws = api.WorkspaceId;
        var item = api.ItemId;
        var request = new CreateShortcutRequest(path, name, target);
        var created = FabricApiClient.Wrap(mode == FabricApiFunctions.ShortcutMode.Alter ? "alter_shortcut" : "create_shortcut",
            () => api.Client.Core.OneLakeShortcuts.CreateShortcut(ws, item, request, policy).Value);
        return FabricShortcutPath.Join(created.Path, created.Name);
    }

    private static NotSupportedException Missing(string arg) =>
        new($"fabric shortcut: '{arg}' must not be NULL.");
}

/// <summary>
/// <c>fabric.create_shortcut_json(path, name, target_json [, conflict_policy])</c> — the full target union as the
/// REST <c>target</c> object verbatim, e.g.
/// <c>'{"adlsGen2":{"location":"https://acct.dfs.core.windows.net","subpath":"/c/d","connectionId":"…"}}'</c>.
/// </summary>
/// <remarks>
/// Deliberately a JSON passthrough rather than eight flattened sibling functions: every external target needs a
/// pre-provisioned <c>connectionId</c> anyway (see <c>connections</c>), and Microsoft documents that
/// target types get ADDED over time — a passthrough survives that, a flattened signature per member does not.
/// </remarks>
internal sealed class FabricCreateShortcutJsonFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricCreateShortcutJsonFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "create_shortcut_json";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("path"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("target_json"),
        FabricApiFunctions.Str("conflict_policy"),
    }, null);

    public Field Result { get; } = FabricApiFunctions.Str("shortcut_path");

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new StringArray.Builder();
        for (int row = 0; row < args.Length; row++)
        {
            string path = FabricArgs.Str(args, 0, row) ?? throw new NotSupportedException("fabric shortcut: 'path' must not be NULL.");
            string name = FabricArgs.Str(args, 1, row) ?? throw new NotSupportedException("fabric shortcut: 'name' must not be NULL.");
            string json = FabricArgs.Str(args, 2, row) ?? throw new NotSupportedException("fabric shortcut: 'target_json' must not be NULL.");
            var policy = FabricShortcutPath.ParsePolicy(FabricArgs.Str(args, 3, row));
            b.Append(FabricCreateShortcutFunction.Apply(
                _api, FabricApiFunctions.ShortcutMode.Create, path, name, FabricShortcutTarget.FromJson(json), policy));
        }
        return b.Build();
    });
}

/// <summary><c>fabric.drop_shortcut(path, name [, if_exists])</c> → true when it was removed.</summary>
/// <remarks>
/// Fabric 404s (<c>EntityNotFound</c>/<c>ShortcutNotFound</c>) on a missing shortcut — verified live — so the
/// default is to fail loudly, and <c>if_exists := true</c> turns that single case into <c>false</c>. Any other
/// error still throws: "it might not exist" must not swallow "you lack permission".
/// </remarks>
internal sealed class FabricDropShortcutFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricDropShortcutFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "drop_shortcut";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("path"),
        FabricApiFunctions.Str("name"),
        new Field("if_exists", BooleanType.Default, nullable: true),
    }, null);

    public Field Result { get; } = new("dropped", BooleanType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new BooleanArray.Builder();
        var ws = _api.WorkspaceId;
        var item = _api.ItemId;
        for (int row = 0; row < args.Length; row++)
        {
            string path = FabricArgs.Str(args, 0, row) ?? throw new NotSupportedException("fabric shortcut: 'path' must not be NULL.");
            string name = FabricArgs.Str(args, 1, row) ?? throw new NotSupportedException("fabric shortcut: 'name' must not be NULL.");
            bool ifExists = FabricArgs.Bool(args, 2, row) ?? false;
            try
            {
                FabricApiClient.Wrap("drop_shortcut",
                    () => _api.Client.Core.OneLakeShortcuts.DeleteShortcut(ws, item, FabricShortcutPath.Strip(path), name));
                b.Append(true);
            }
            catch (NotSupportedException ex) when (ifExists && ex.Message.Contains("EntityNotFound", StringComparison.Ordinal))
            {
                b.Append(false);
            }
        }
        return b.Build();
    });
}

/// <summary>
/// <c>fabric.list_shortcuts([parent_path])</c> — the shortcuts of this catalog's lakehouse.
/// </summary>
/// <remarks>
/// The output shape is the design rule from docs §D4 in miniature: stable fields as TYPED columns (flattened one
/// level with a <c>target_</c> prefix), plus one <c>target_json</c> carrying the original target object for the
/// polymorphic remainder. No STRUCT wrapping — adding a column is additive for <c>SELECT *</c> consumers, whereas
/// adding a struct FIELD changes a column's type and breaks bound views.
/// </remarks>
internal sealed class FabricListShortcutsFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricListShortcutsFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "list_shortcuts";

    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    /// <summary>
    /// <c>fabric.list_shortcuts(parent_path := 'Files')</c> — unset lists all; <c>workspace</c>/<c>item</c>
    /// read a different item than the attached one.
    /// </summary>
    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("parent_path"),
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item"),
    }, null);

    public ITableFunctionBinding Bind(RecordBatch args) =>
        new Binding(_api, FabricArgs.Str(args, 0), FabricArgs.Str(args, 1), FabricArgs.Str(args, 2));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("path"),
            FabricApiFunctions.Str("name"),
            FabricApiFunctions.Str("target_type"),
            FabricApiFunctions.Str("target_workspace_id"),
            FabricApiFunctions.Str("target_item_id"),
            FabricApiFunctions.Str("target_path"),
            FabricApiFunctions.Str("target_location"),
            FabricApiFunctions.Str("target_subpath"),
            FabricApiFunctions.Str("target_connection_id"),
            FabricApiFunctions.Str("target_json"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _parentPath;
        private readonly string? _workspace;
        private readonly string? _item;

        internal Binding(FabricApiClient api, string? parentPath, string? workspace, string? item)
        {
            _api = api;
            _parentPath = parentPath;
            _workspace = workspace;
            _item = item;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var ws = _api.ResolveWorkspace(_workspace);
            var item = _api.ResolveItem(_item, "Lakehouse", ws);
            var paths = new StringArray.Builder();
            var names = new StringArray.Builder();
            var types = new StringArray.Builder();
            var tws = new StringArray.Builder();
            var titem = new StringArray.Builder();
            var tpath = new StringArray.Builder();
            var tloc = new StringArray.Builder();
            var tsub = new StringArray.Builder();
            var tconn = new StringArray.Builder();
            var tjson = new StringArray.Builder();
            int n = 0;

            var listed = FabricApiClient.Wrap("list_shortcuts", () =>
            {
                var rows = new List<ShortcutTransformFlagged>();
                foreach (var s in _api.Client.Core.OneLakeShortcuts.ListShortcuts(
                             ws, item, parentPath: FabricShortcutPath.NullIfBlank(_parentPath),
                             cancellationToken: ct))
                {
                    rows.Add(s);
                }
                return rows;
            });

            foreach (var s in listed)
            {
                var t = s.Target;
                // Returned paths carry a LEADING SLASH ("/Files/staging") while CreateShortcut takes "Files";
                // normalize so list → drop round-trips.
                paths.Append(FabricShortcutPath.Strip(s.Path));
                names.Append(s.Name);
                types.Append(t?.Type.ToString());
                tws.Append(t?.OneLake?.WorkspaceId.ToString());
                titem.Append(t?.OneLake?.ItemId.ToString());
                var flat = FabricShortcutTarget.Flatten(t);
                tpath.Append(t?.OneLake?.Path);
                tloc.Append(flat.Location);
                tsub.Append(flat.Subpath);
                tconn.Append(flat.ConnectionId);
                tjson.Append(FabricShortcutTarget.ToJson(t));
                n++;
            }
            return One(Columns, new IArrowArray[]
            {
                paths.Build(), names.Build(), types.Build(), tws.Build(), titem.Build(), tpath.Build(),
                tloc.Build(), tsub.Build(), tconn.Build(), tjson.Build(),
            }, n);
        }
    }
}
