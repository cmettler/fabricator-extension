using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Semantic models: list them, refresh one, and read its refresh history.
/// </summary>
/// <remarks>
/// <para><b>Why this completes the picture.</b> After a Delta write there are TWO consumers to make current
/// and they need different calls: <c>fabric_refresh_sql_endpoint</c> makes the table visible to <b>T-SQL</b>,
/// and refreshing the semantic model makes the data visible to <b>Power BI</b>. A dbt flow ending in a report
/// wants both.</para>
///
/// <para><b>Refresh is NOT in the Fabric API</b> — probed with a zero control in the pinned SDK 2.14.0:
/// <c>RefreshSemanticModel</c>/<c>EnhancedRefresh</c>/<c>RefreshSchedule</c> are all absent, and
/// <c>SemanticModel.ItemsClient</c> offers only CRUD + definition + <c>BindSemanticModelConnection</c>. It
/// lives in the <b>Power BI REST API</b> instead. That is a different HOST but the SAME audience we already
/// mint — <see cref="FabricCredentialResolver.PowerBiScope"/>, the scope the DAX provider uses — so no new
/// credential path, and (measured on this tenant) no admin change either.</para>
///
/// <para><b>Both a Lakehouse and a Warehouse have a default semantic model</b>, resolved here BY NAME because
/// the API exposes no "default model for item X" link: the default carries the item's own name (measured —
/// lakehouse <c>LH</c> has a model named <c>LH</c>). A name or GUID can always be passed explicitly.</para>
///
/// <para><b>Scope boundary, deliberate:</b> this is "refresh this model, tell me when it is done". Per-table /
/// per-partition sequencing belongs in XMLA/TMSL through the DAX provider (<c>dax_*</c>), which already holds
/// an ADOMD connection on the same token — the REST API cannot express it, and growing this function toward
/// it would duplicate a better-placed surface.</para>
/// </remarks>
internal static class FabricSemanticModelFunctions
{
    internal static void Register(List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricSemanticModelsFunction(api));
        tables.Add(new FabricRefreshSemanticModelFunction(api));
        tables.Add(new FabricSemanticModelRefreshesFunction(api));
    }
}

/// <summary>
/// <c>fabric_semantic_models([workspace := …])</c> — the workspace's semantic models, including each
/// lakehouse's and warehouse's default one.
/// </summary>
internal sealed class FabricSemanticModelsFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricSemanticModelsFunction(FabricApiClient api) => _api = api;

    public string SchemaName => CatalogFunctionSet.AllSchemas;
    public string Name => "fabric_semantic_models";

    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    public Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, FabricArgs.Str(args, 0));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("id"),
            FabricApiFunctions.Str("name"),
            new Field("is_refreshable", BooleanType.Default, nullable: true),
            FabricApiFunctions.Str("web_url"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _workspace;

        internal Binding(FabricApiClient api, string? workspace)
        {
            _api = api;
            _workspace = workspace;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var ws = _api.ResolveWorkspace(_workspace);
            // The POWER BI listing, not the Fabric one: it is the surface that also reports isRefreshable,
            // and it is the same surface the refresh below acts on — so a model listed here is one that can
            // be passed straight to fabric_refresh_semantic_model.
            var models = await _api.ListDatasetsAsync(ws, ct).ConfigureAwait(false);
            var ids = new StringArray.Builder();
            var names = new StringArray.Builder();
            var refreshable = new BooleanArray.Builder();
            var urls = new StringArray.Builder();
            foreach (var m in models)
            {
                ids.Append(m.Id);
                names.Append(m.Name);
                if (m.IsRefreshable is { } r) { refreshable.Append(r); } else { refreshable.AppendNull(); }
                urls.Append(m.WebUrl);
            }
            yield return new RecordBatch(Columns, new IArrowArray[]
            {
                ids.Build(), names.Build(), refreshable.Build(), urls.Build(),
            }, models.Count);
        }
    }
}

/// <summary>
/// <c>fabric_refresh_semantic_model(model [, type := …] [, objects_json := …] [, commit_mode := …]
/// [, max_parallelism := …] [, retry_count := …] [, timeout := …] [, wait_seconds := …]
/// [, workspace := …])</c> — triggers an ENHANCED refresh and blocks until it settles.
/// </summary>
/// <remarks>
/// <para>Blocking by default for the same reason as the rest: a dbt hook must not return before the work is
/// done. <c>wait_seconds := 0</c> submits and returns the request id for later polling with
/// <c>fabric_semantic_model_refreshes</c>.</para>
/// <para><b>An enhanced refresh is requested by sending a body at all</b> — the API treats a request whose
/// only field is <c>notifyOption</c> as a plain refresh. It also rejects <c>notifyOption</c> for a
/// service-principal caller. Those two rules interact, so this NEVER sends <c>notifyOption</c> and always
/// sends at least <c>type</c> (defaulting to <c>Full</c>), which is the only combination valid for both
/// identity kinds.</para>
/// <para>Enhanced refresh requires Fabric/Premium capacity; on shared capacity the API accepts only
/// <c>notifyOption</c> and caps refreshes at 8/day, so the failure there is the service's, reported as-is.</para>
/// </remarks>
internal sealed class FabricRefreshSemanticModelFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricRefreshSemanticModelFunction(FabricApiClient api) => _api = api;

    public string SchemaName => CatalogFunctionSet.AllSchemas;
    public string Name => "fabric_refresh_semantic_model";

    public Schema Parameters { get; } = new Schema(new[] { FabricApiFunctions.Str("model") }, null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("type"),               // Full | ClearValues | Calculate | DataOnly | Automatic | Defragment
        FabricApiFunctions.Str("objects_json"),        // [{"table":"T","partition":"P"}, …]
        FabricApiFunctions.Str("commit_mode"),          // Transactional | PartialBatch
        new Field("max_parallelism", Int64Type.Default, nullable: true),
        new Field("retry_count", Int64Type.Default, nullable: true),
        FabricApiFunctions.Str("timeout"),              // hh:mm:ss
        new Field("wait_seconds", Int64Type.Default, nullable: true),
        FabricApiFunctions.Str("workspace"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("request_id"),
            FabricApiFunctions.Str("status"),
            FabricApiFunctions.Str("refresh_type"),
            FabricApiFunctions.Ts("start_time"),
            FabricApiFunctions.Ts("end_time"),
            FabricApiFunctions.Str("error_message"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _model;
        private readonly string? _type;
        private readonly string? _objectsJson;
        private readonly string? _commitMode;
        private readonly long? _maxParallelism;
        private readonly long? _retryCount;
        private readonly string? _timeout;
        private readonly long _waitSeconds;
        private readonly string? _workspace;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            _api = api;
            _model = FabricArgs.Str(args, 0);
            _type = FabricArgs.Str(args, 1);
            _objectsJson = FabricArgs.Str(args, 2);
            _commitMode = FabricArgs.Str(args, 3);
            _maxParallelism = FabricArgs.Int(args, 4);
            _retryCount = FabricArgs.Int(args, 5);
            _timeout = FabricArgs.Str(args, 6);
            _waitSeconds = FabricArgs.Int(args, 7) ?? FabricJobFunctions.DefaultWaitSeconds;
            _workspace = FabricArgs.Str(args, 8);
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_model))
            {
                throw new NotSupportedException(
                    "fabric_refresh_semantic_model: pass the model name or id "
                    + "(list them with fabric_semantic_models()).");
            }
            var ws = _api.ResolveWorkspace(_workspace);
            var modelId = await _api.ResolveDatasetAsync(ws, _model!, ct).ConfigureAwait(false);
            var requestId = await _api.StartDatasetRefreshAsync(ws, modelId, BuildBody(), ct)
                .ConfigureAwait(false);
            var state = await _api.PollDatasetRefreshAsync(ws, modelId, requestId, _waitSeconds, ct)
                .ConfigureAwait(false);

            var id = new StringArray.Builder();
            var status = new StringArray.Builder();
            var type = new StringArray.Builder();
            var start = FabricApiFunctions.TsBuilder();
            var end = FabricApiFunctions.TsBuilder();
            var error = new StringArray.Builder();
            id.Append(state.RequestId ?? requestId);
            status.Append(state.Status);
            type.Append(state.RefreshType);
            FabricJobFunctions.AppendTs(start, state.StartTime);
            FabricJobFunctions.AppendTs(end, state.EndTime);
            error.Append(state.ErrorMessage);
            yield return new RecordBatch(Columns, new IArrowArray[]
            {
                id.Build(), status.Build(), type.Build(), start.Build(), end.Build(), error.Build(),
            }, 1);
        }

        private string BuildBody()
        {
            // ALWAYS a non-empty body, and NEVER notifyOption: a body of only notifyOption means a PLAIN
            // refresh, and notifyOption is rejected outright for a service principal. Sending `type` covers
            // both rules at once.
            var body = new Dictionary<string, object?> { ["type"] = _type ?? "Full" };
            if (!string.IsNullOrWhiteSpace(_objectsJson))
            {
                var objects = JsonSerializer.Deserialize<JsonElement>(_objectsJson!);
                if (objects.ValueKind != JsonValueKind.Array)
                {
                    throw new NotSupportedException(
                        "fabric_refresh_semantic_model: objects_json must be a JSON ARRAY, e.g. "
                        + "'[{\"table\":\"Sales\",\"partition\":\"2026\"}]'.");
                }
                body["objects"] = objects;
            }
            if (!string.IsNullOrWhiteSpace(_commitMode)) { body["commitMode"] = _commitMode; }
            if (_maxParallelism is not null) { body["maxParallelism"] = _maxParallelism; }
            if (_retryCount is not null) { body["retryCount"] = _retryCount; }
            if (!string.IsNullOrWhiteSpace(_timeout)) { body["timeout"] = _timeout; }
            return JsonSerializer.Serialize(body);
        }
    }
}

/// <summary>
/// <c>fabric_semantic_model_refreshes(model [, top := …] [, workspace := …])</c> — that model's refresh
/// history, newest first.
/// </summary>
/// <remarks>
/// The way to assert in a hook that the LAST refresh actually succeeded, and the follow-up for a
/// <c>wait_seconds := 0</c> submission. <c>extended_status</c> carries the detail Power BI adds beyond
/// <c>status</c> (it is where a DirectLake fallback or a partition-level failure shows up).
/// </remarks>
internal sealed class FabricSemanticModelRefreshesFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricSemanticModelRefreshesFunction(FabricApiClient api) => _api = api;

    public string SchemaName => CatalogFunctionSet.AllSchemas;
    public string Name => "fabric_semantic_model_refreshes";

    public Schema Parameters { get; } = new Schema(new[] { FabricApiFunctions.Str("model") }, null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        new Field("top", Int64Type.Default, nullable: true),
        FabricApiFunctions.Str("workspace"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) =>
        new Binding(_api, FabricArgs.Str(args, 0), FabricArgs.Int(args, 1), FabricArgs.Str(args, 2));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("request_id"),
            FabricApiFunctions.Str("refresh_type"),
            FabricApiFunctions.Str("status"),
            FabricApiFunctions.Str("extended_status"),
            FabricApiFunctions.Ts("start_time"),
            FabricApiFunctions.Ts("end_time"),
            FabricApiFunctions.Str("error_message"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _model;
        private readonly long? _top;
        private readonly string? _workspace;

        internal Binding(FabricApiClient api, string? model, long? top, string? workspace)
        {
            _api = api;
            _model = model;
            _top = top;
            _workspace = workspace;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_model))
            {
                throw new NotSupportedException("fabric_semantic_model_refreshes: 'model' must not be NULL.");
            }
            var ws = _api.ResolveWorkspace(_workspace);
            var modelId = await _api.ResolveDatasetAsync(ws, _model!, ct).ConfigureAwait(false);
            var history = await _api.ListDatasetRefreshesAsync(ws, modelId, _top, ct).ConfigureAwait(false);

            var ids = new StringArray.Builder();
            var types = new StringArray.Builder();
            var status = new StringArray.Builder();
            var extended = new StringArray.Builder();
            var start = FabricApiFunctions.TsBuilder();
            var end = FabricApiFunctions.TsBuilder();
            var error = new StringArray.Builder();
            foreach (var r in history)
            {
                ids.Append(r.RequestId);
                types.Append(r.RefreshType);
                status.Append(r.Status);
                extended.Append(r.ExtendedStatus);
                FabricJobFunctions.AppendTs(start, r.StartTime);
                FabricJobFunctions.AppendTs(end, r.EndTime);
                error.Append(r.ErrorMessage);
            }
            yield return new RecordBatch(Columns, new IArrowArray[]
            {
                ids.Build(), types.Build(), status.Build(), extended.Build(), start.Build(), end.Build(),
                error.Build(),
            }, history.Count);
        }
    }
}
