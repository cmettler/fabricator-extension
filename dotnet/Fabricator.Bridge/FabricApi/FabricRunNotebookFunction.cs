using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// <c>db.&lt;schema&gt;.fabric_run_notebook(notebook [, params_json := …] [, config_json := …]
/// [, wait_seconds := …])</c> — runs a Fabric notebook with parameters and, by default, BLOCKS until it
/// finishes, returning one row of final state.
/// </summary>
/// <remarks>
/// <para>Blocking is the default because a dbt hook must not return before the work is done;
/// <c>wait_seconds := 0</c> submits and returns immediately (status <c>NotStarted</c>/<c>InProgress</c>).</para>
///
/// <para><b>Parameters ride <c>executionData.parameters</c>, which is the shape LIVE-VERIFIED to work</b> — the
/// generic top-level <c>parameters</c> array is accepted with 202 and then SILENTLY IGNORED for notebooks
/// (docs/fabric-api-functions.md §9d). The notebook needs a cell tagged <c>parameters</c>; Fabric injects the
/// overrides after it (papermill convention).</para>
///
/// <para>Raw HTTP rather than the SDK, of necessity: the SDK's <c>RunOnDemandItemJob</c> returns a bare
/// <c>Response</c> (the instance id is only in the <c>Location</c> header) and its <c>ItemJobInstance</c> model
/// has no <c>exitValue</c> in 2.14.0 or 2.18.0 — and the exit value lives at <c>properties.exitValue</c> on the
/// NOTEBOOK-scoped instance GET, which the SDK does not project at all.</para>
/// </remarks>
internal sealed class FabricRunNotebookFunction : ICatalogTableFunction
{
    private readonly FabricApiClient _api;

    internal FabricRunNotebookFunction(FabricApiClient api) => _api = api;

    public string SchemaName => CatalogFunctionSet.AllSchemas;
    public string Name => "fabric_run_notebook";

    /// <summary>The notebook is the only required argument.</summary>
    public Schema Parameters { get; } = new Schema(new[] { FabricApiFunctions.Str("notebook") }, null);

    /// <summary>
    /// Everything else is optional:
    /// <c>fabric_run_notebook('nb', params_json := '{…}', wait_seconds := 0)</c>.
    /// </summary>
    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("params_json"),
        FabricApiFunctions.Str("config_json"),
        new Field("wait_seconds", Int64Type.Default, nullable: true),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("job_instance_id"),
            FabricApiFunctions.Str("status"),
            FabricApiFunctions.Ts("start_time"),
            FabricApiFunctions.Ts("end_time"),
            FabricApiFunctions.Str("exit_value"),
            FabricApiFunctions.Str("compute"),
            FabricApiFunctions.Str("snapshot_url"),
            FabricApiFunctions.Str("error_code"),
            FabricApiFunctions.Str("error_message"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _notebook;
        private readonly string? _paramsJson;
        private readonly string? _configJson;
        private readonly long _waitSeconds;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            // Positions are Parameters ++ NamedParameters in declared order; an omitted named argument
            // arrives as NULL, which is why every read below is null-tolerant.
            _api = api;
            _notebook = FabricArgs.Str(args, 0);
            _paramsJson = FabricArgs.Str(args, 1);
            _configJson = FabricArgs.Str(args, 2);
            // Default cap: a cold Spark session alone can take minutes, so a short default would time out
            // on the very flows this exists for. 0 = fire and return.
            _waitSeconds = FabricArgs.Int(args, 3) ?? 3600;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_notebook))
            {
                throw new NotSupportedException(
                    "fabric_run_notebook: pass the notebook name or id (list them with fabric_items(item_type := 'Notebook')).");
            }
            var ws = _api.WorkspaceId;
            var nb = _api.ResolveItem(_notebook, "Notebook");

            var instanceId = await _api.SubmitNotebookRunAsync(ws, nb, BuildBody(), ct).ConfigureAwait(false);
            var state = await _api.PollNotebookRunAsync(ws, nb, instanceId, _waitSeconds, ct).ConfigureAwait(false);

            var id = new StringArray.Builder();
            var status = new StringArray.Builder();
            var start = new TimestampArray.Builder();
            var end = new TimestampArray.Builder();
            var exit = new StringArray.Builder();
            var compute = new StringArray.Builder();
            var snapshot = new StringArray.Builder();
            var errCode = new StringArray.Builder();
            var errMsg = new StringArray.Builder();

            id.Append(instanceId);
            status.Append(state.Status);
            AppendTs(start, state.StartTimeUtc);
            AppendTs(end, state.EndTimeUtc);
            exit.Append(state.ExitValue);
            compute.Append(state.Compute);
            snapshot.Append(state.SnapshotUrl);
            errCode.Append(state.ErrorCode);
            errMsg.Append(state.ErrorMessage);

            yield return new RecordBatch(Columns, new IArrowArray[]
            {
                id.Build(), status.Build(), start.Build(), end.Build(), exit.Build(), compute.Build(),
                snapshot.Build(), errCode.Build(), errMsg.Build(),
            }, 1);
        }

        private static void AppendTs(TimestampArray.Builder b, string? iso)
        {
            // The instance model types these as STRINGS, and a run that never started has none.
            if (DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto))
            {
                b.Append(dto);
            }
            else
            {
                b.AppendNull();
            }
        }

        /// <summary>
        /// Builds the request body. Parameters go in <c>executionData.parameters</c> as
        /// <c>{name: {value, type}}</c> — the live-verified shape.
        /// </summary>
        private string BuildBody()
        {
            var executionData = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(_paramsJson))
            {
                executionData["parameters"] = ParseParameters(_paramsJson!);
            }
            if (!string.IsNullOrWhiteSpace(_configJson))
            {
                using var doc = ParseObject(_configJson!, "config_json");
                executionData["configuration"] = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["executionData"] = executionData });
        }

        /// <summary>
        /// Turns a plain JSON object into Fabric's <c>{name: {value, type}}</c> map, inferring the type from
        /// the JSON value. A member already written in the verbose form is passed through, so a caller who
        /// needs an explicit type (a float that happens to be integral) can say so.
        /// </summary>
        private static Dictionary<string, object> ParseParameters(string json)
        {
            using var doc = ParseObject(json, "params_json");
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var m in doc.RootElement.EnumerateObject())
            {
                if (m.Value.ValueKind == JsonValueKind.Object
                    && m.Value.TryGetProperty("value", out _) && m.Value.TryGetProperty("type", out _))
                {
                    map[m.Name] = JsonSerializer.Deserialize<JsonElement>(m.Value.GetRawText());
                    continue;
                }
                map[m.Name] = m.Value.ValueKind switch
                {
                    JsonValueKind.String => Wrap(m.Value.GetString(), "string"),
                    JsonValueKind.True or JsonValueKind.False => Wrap(m.Value.GetBoolean(), "bool"),
                    JsonValueKind.Number when m.Value.TryGetInt64(out var l) => Wrap(l, "int"),
                    JsonValueKind.Number => Wrap(m.Value.GetDouble(), "float"),
                    _ => throw new NotSupportedException(
                        $"fabric_run_notebook: parameter '{m.Name}' must be a string, number, boolean, or a "
                        + "{\"value\":…,\"type\":…} object (Fabric notebook parameters have no array/object type)."),
                };
            }
            return map;
        }

        private static Dictionary<string, object?> Wrap(object? value, string type) =>
            new() { ["value"] = value, ["type"] = type };

        private static JsonDocument ParseObject(string json, string argName)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new NotSupportedException($"fabric_run_notebook: {argName} is not valid JSON — {ex.Message}");
            }
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new NotSupportedException($"fabric_run_notebook: {argName} must be a JSON object.");
            }
            return doc;
        }
    }
}
