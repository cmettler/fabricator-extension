using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// Jobs on Fabric items: table maintenance (the V-Order optimize our own engine cannot do), the generic
/// on-demand job runner, and job status/history/cancel.
/// </summary>
internal static class FabricJobFunctions
{
    internal static void Register(
        List<ICatalogScalarFunction> scalars, List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricTableMaintenanceFunction(api));
        tables.Add(new FabricRunJobFunction(api));
        tables.Add(new FabricJobStatusFunction(api));
        tables.Add(new FabricJobInstancesFunction(api));
        tables.Add(new FabricResetShortcutCacheFunction(api));
        tables.Add(new FabricOperationStatusFunction(api));
        tables.Add(new FabricLakehouseTablesFunction(api));
        scalars.Add(new FabricCancelJobFunction(api));
    }

    /// <summary>The columns every job-returning function reports, so they read alike.</summary>
    internal static Schema JobColumns { get; } = new(new[]
    {
        FabricApiFunctions.Str("job_instance_id"),
        FabricApiFunctions.Str("status"),
        FabricApiFunctions.Ts("start_time"),
        FabricApiFunctions.Ts("end_time"),
        FabricApiFunctions.Str("error_code"),
        FabricApiFunctions.Str("error_message"),
    }, null);

    /// <summary>Emits one <see cref="JobColumns"/> row from a polled job state.</summary>
    internal static RecordBatch JobRow(string instanceId, FabricApiClient.JobRunState state)
    {
        var id = new StringArray.Builder();
        var status = new StringArray.Builder();
        var start = FabricApiFunctions.TsBuilder();
        var end = FabricApiFunctions.TsBuilder();
        var code = new StringArray.Builder();
        var message = new StringArray.Builder();
        id.Append(instanceId);
        status.Append(state.Status);
        AppendTs(start, state.StartTimeUtc);
        AppendTs(end, state.EndTimeUtc);
        code.Append(state.ErrorCode);
        message.Append(state.ErrorMessage);
        return new RecordBatch(JobColumns, new IArrowArray[]
        {
            id.Build(), status.Build(), start.Build(), end.Build(), code.Build(), message.Build(),
        }, 1);
    }

    /// <summary>The job models type these as STRINGS, and a job that never started has none.</summary>
    internal static void AppendTs(TimestampArray.Builder b, string? iso)
    {
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

    /// <summary>Default blocking cap: a Spark-backed job's session start alone is minutes.</summary>
    internal const long DefaultWaitSeconds = 3600;
}

/// <summary>
/// <c>fabric.table_maintenance(table [, schema := …] [, v_order := …] [, z_order_by := …]
/// [, vacuum_retention := …] [, purge_deletion_vectors := …] [, wait_seconds := …] [, workspace := …]
/// [, item := …])</c> — runs Fabric's lakehouse table-maintenance job and blocks until it finishes.
/// </summary>
/// <remarks>
/// <para>This is COMPLEMENTARY to our own OPTIMIZE, not a duplicate of it: <b>V-Order</b> is Microsoft's
/// proprietary parquet layout optimization and we cannot produce it, so a table that Power BI / the SQL
/// endpoint will read hot is worth passing through here. It also offers Z-order, VACUUM and
/// <c>REORG … APPLY (PURGE)</c> for deletion vectors.</para>
/// <para>Omitting <c>v_order</c>/<c>z_order_by</c> entirely skips optimization, and omitting
/// <c>vacuum_retention</c> skips vacuum — that is the API's own convention (an absent settings object means
/// "do not do this part"), so the defaults here do nothing rather than something surprising.</para>
/// <para><b>Preview API.</b> Its shape may change; <c>execution_data_json</c> on
/// <c>run_job</c> is the escape hatch if it does.</para>
/// </remarks>
internal sealed class FabricTableMaintenanceFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricTableMaintenanceFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "table_maintenance";

    public Schema Parameters { get; } = new Schema(new[] { FabricApiFunctions.Str("table") }, null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("schema"),
        new Field("v_order", BooleanType.Default, nullable: true),
        FabricApiFunctions.Str("z_order_by"),               // comma-separated column list
        FabricApiFunctions.Str("vacuum_retention"),          // d:hh:mm:ss
        new Field("purge_deletion_vectors", BooleanType.Default, nullable: true),
        new Field("wait_seconds", Int64Type.Default, nullable: true),
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        private readonly FabricApiClient _api;
        private readonly string? _table;
        private readonly string? _schema;
        private readonly bool? _vOrder;
        private readonly string? _zOrderBy;
        private readonly string? _vacuumRetention;
        private readonly bool? _purgeDvs;
        private readonly long _waitSeconds;
        private readonly string? _workspace;
        private readonly string? _item;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            _api = api;
            _table = FabricArgs.Str(args, 0);
            _schema = FabricArgs.Str(args, 1);
            _vOrder = FabricArgs.Bool(args, 2);
            _zOrderBy = FabricArgs.Str(args, 3);
            _vacuumRetention = FabricArgs.Str(args, 4);
            _purgeDvs = FabricArgs.Bool(args, 5);
            _waitSeconds = FabricArgs.Int(args, 6) ?? FabricJobFunctions.DefaultWaitSeconds;
            _workspace = FabricArgs.Str(args, 7);
            _item = FabricArgs.Str(args, 8);
        }

        public override Schema OutputSchema => FabricJobFunctions.JobColumns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_table))
            {
                throw new NotSupportedException("table_maintenance: 'table' must not be NULL.");
            }
            var ws = _api.ResolveWorkspace(_workspace);
            var lh = _api.ResolveItem(_item, "Lakehouse", ws);

            var instanceId = await _api
                .SubmitItemJobAsync(ws, lh, "TableMaintenance", BuildBody(), ct).ConfigureAwait(false);
            var state = await _api.PollItemJobAsync(ws, lh, instanceId, _waitSeconds, ct).ConfigureAwait(false);
            yield return FabricJobFunctions.JobRow(instanceId, state);
        }

        private string BuildBody()
        {
            var execution = new Dictionary<string, object?> { ["tableName"] = _table };
            if (!string.IsNullOrWhiteSpace(_schema))
            {
                // Applies only to a SCHEMA-ENABLED lakehouse; the service rejects it otherwise.
                execution["schemaName"] = _schema;
            }
            // An ABSENT settings object means "skip this part" — the API's own convention, so only emit one
            // when the caller actually asked for it.
            if (_vOrder is not null || !string.IsNullOrWhiteSpace(_zOrderBy))
            {
                var optimize = new Dictionary<string, object?>();
                if (_vOrder is not null) { optimize["vOrder"] = _vOrder; }
                if (!string.IsNullOrWhiteSpace(_zOrderBy))
                {
                    optimize["zOrderBy"] = _zOrderBy!.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                                 | StringSplitOptions.TrimEntries);
                }
                execution["optimizeSettings"] = optimize;
            }
            if (!string.IsNullOrWhiteSpace(_vacuumRetention))
            {
                execution["vacuumSettings"] = new Dictionary<string, object?>
                {
                    ["retentionPeriod"] = _vacuumRetention,
                };
            }
            if (_purgeDvs is not null)
            {
                execution["purgeDeletionVectors"] = _purgeDvs;
            }
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["executionData"] = execution });
        }
    }
}

/// <summary>
/// <c>fabric.run_job(item, job_type [, execution_data_json := …] [, wait_seconds := …] [, workspace := …])</c>
/// — the generic on-demand job runner behind the specialized ones.
/// </summary>
/// <remarks>
/// Reach for this when a job type has no dedicated function (a Data Pipeline's <c>Pipeline</c>, a Spark job
/// definition, a preview job whose shape changed): <c>execution_data_json</c> is passed through verbatim as
/// the request's <c>executionData</c>, so it can express whatever the API accepts today.
/// </remarks>
internal sealed class FabricRunJobFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricRunJobFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "run_job";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("item"),
        FabricApiFunctions.Str("job_type"),
    }, null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("execution_data_json"),
        new Field("wait_seconds", Int64Type.Default, nullable: true),
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item_type"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        private readonly FabricApiClient _api;
        private readonly string? _item;
        private readonly string? _jobType;
        private readonly string? _executionDataJson;
        private readonly long _waitSeconds;
        private readonly string? _workspace;
        private readonly string? _itemType;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            _api = api;
            _item = FabricArgs.Str(args, 0);
            _jobType = FabricArgs.Str(args, 1);
            _executionDataJson = FabricArgs.Str(args, 2);
            _waitSeconds = FabricArgs.Int(args, 3) ?? FabricJobFunctions.DefaultWaitSeconds;
            _workspace = FabricArgs.Str(args, 4);
            _itemType = FabricArgs.Str(args, 5);
        }

        public override Schema OutputSchema => FabricJobFunctions.JobColumns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_item) || string.IsNullOrWhiteSpace(_jobType))
            {
                throw new NotSupportedException("run_job: 'item' and 'job_type' must not be NULL.");
            }
            var ws = _api.ResolveWorkspace(_workspace);
            // item_type narrows the name lookup; a GUID needs none, which is why it is optional.
            var id = _api.ResolveItem(_item, _itemType ?? "Lakehouse", ws, requireType: _itemType is not null);
            var body = string.IsNullOrWhiteSpace(_executionDataJson)
                ? null
                : JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["executionData"] = JsonSerializer.Deserialize<JsonElement>(_executionDataJson!),
                });
            var instanceId = await _api.SubmitItemJobAsync(ws, id, _jobType!, body, ct).ConfigureAwait(false);
            var state = await _api.PollItemJobAsync(ws, id, instanceId, _waitSeconds, ct).ConfigureAwait(false);
            yield return FabricJobFunctions.JobRow(instanceId, state);
        }
    }
}

/// <summary><c>fabric.job_status(item, job_instance_id [, workspace := …] [, item_type := …])</c>.</summary>
internal sealed class FabricJobStatusFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricJobStatusFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "job_status";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("item"),
        FabricApiFunctions.Str("job_instance_id"),
    }, null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item_type"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        private readonly FabricApiClient _api;
        private readonly string? _item;
        private readonly string? _instanceId;
        private readonly string? _workspace;
        private readonly string? _itemType;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            _api = api;
            _item = FabricArgs.Str(args, 0);
            _instanceId = FabricArgs.Str(args, 1);
            _workspace = FabricArgs.Str(args, 2);
            _itemType = FabricArgs.Str(args, 3);
        }

        public override Schema OutputSchema => FabricJobFunctions.JobColumns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct) => Run(ct);

        private async IAsyncEnumerable<RecordBatch> Run(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_item) || string.IsNullOrWhiteSpace(_instanceId))
            {
                throw new NotSupportedException(
                    "job_status: 'item' and 'job_instance_id' must not be NULL.");
            }
            var ws = _api.ResolveWorkspace(_workspace);
            var id = _api.ResolveItem(_item, _itemType ?? "Lakehouse", ws, requireType: _itemType is not null);
            var state = await _api.GetItemJobAsync(ws, id, _instanceId!, ct).ConfigureAwait(false);
            yield return FabricJobFunctions.JobRow(_instanceId!, state);
        }
    }
}

/// <summary>
/// <c>fabric.job_instances([item := …] [, workspace := …] [, item_type := …])</c> — job history for one item,
/// or FANNED OUT across every item of a type.
/// </summary>
/// <remarks>
/// <para><b>⚠ <c>item</c> moved from POSITIONAL to NAMED (2026-08-03, breaking).</b>
/// <c>job_instances('nb')</c> is now <c>job_instances(item := 'nb')</c>. It had to: DuckDB arity is fixed, so a
/// positional parameter cannot be omitted, and omitting it is exactly what the fan-out needs. Shipped in the same
/// breaking window as the <c>fabric</c> schema move so callers migrate once rather than twice.</para>
/// <para><b>Why fan-out belongs here and not in <c>fabric.sessions()</c>.</b> Sessions are already
/// workspace-scoped in ONE request, but they only cover Spark. Job instances cover every item kind — Pipeline,
/// Dataflow, TableMaintenance, notebook runs — and the API is per-item, so the only way to ask "what has run in
/// this workspace" is to enumerate items and ask each. That is O(items) requests against a per-principal
/// throttle, which is why it is opt-in behind <c>item_type</c> and never the default.</para>
/// <para><b>It is deliberately NOT capped.</b> A default `max_items` would under-report while looking complete —
/// the silent-truncation failure mode. If a type has 500 items, the caller pays for 500 items.</para>
/// </remarks>
internal sealed class FabricJobInstancesFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricJobInstancesFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "job_instances";

    /// <summary>No positionals: <c>item</c> must be omittable for the fan-out, and only a NAMED parameter can be.</summary>
    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("item"),
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item_type"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

    private sealed class Binding : FabricTableBinding
    {
        // `item_name`/`item_id` are APPENDED, not prepended: the D4 rule is that adding a column stays additive
        // for `SELECT *`, which prepending would break for anything reading by position. They are required for
        // the fan-out to be usable at all — without them the rows of 40 notebooks are indistinguishable.
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("job_instance_id"),
            FabricApiFunctions.Str("job_type"),
            FabricApiFunctions.Str("invoke_type"),
            FabricApiFunctions.Str("status"),
            FabricApiFunctions.Ts("start_time"),
            FabricApiFunctions.Ts("end_time"),
            FabricApiFunctions.Str("error_code"),
            FabricApiFunctions.Str("error_message"),
            FabricApiFunctions.Str("item_name"),
            FabricApiFunctions.Str("item_id"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _item;
        private readonly string? _workspace;
        private readonly string? _itemType;

        internal Binding(FabricApiClient api, RecordBatch args)
        {
            _api = api;
            _item = FabricArgs.Str(args, 0);
            _workspace = FabricArgs.Str(args, 1);
            _itemType = FabricArgs.Str(args, 2);
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var ws = _api.ResolveWorkspace(_workspace);
            var row = new FabricRowBuilder(Columns);
            if (!string.IsNullOrWhiteSpace(_item))
            {
                // Single item — the original behaviour. `item_name` echoes what the CALLER passed (which may be
                // a GUID): resolving the display name back would cost an extra listing for a column the caller
                // already knows. In fan-out mode below it is the service's own DisplayName.
                var id = _api.ResolveItem(_item, _itemType ?? "Lakehouse", ws,
                                          requireType: _itemType is not null);
                Append(row, ws, id, _item!, ct);
                return One(row.Build());
            }
            if (string.IsNullOrWhiteSpace(_itemType))
            {
                throw new NotSupportedException(
                    "job_instances: pass item := '<name or id>' for one item, or item_type := '<type>' to fan "
                    + "out across every item of that type (e.g. 'Notebook', 'DataPipeline'). Omitting BOTH is "
                    + "refused on purpose: it would issue one API call per item in the workspace, unbounded and "
                    + "throttled per principal. List the types with fabric.items().");
            }
            // FAN-OUT: one listing to enumerate the items, then ONE job-instance call PER ITEM. That is O(items)
            // requests against a per-principal throttle, so it is opt-in via item_type rather than the default,
            // and it is not capped — a silent cap would under-report while looking complete.
            foreach (var (id, name) in ItemsOfType(ws, _itemType!, ct))
            {
                Append(row, ws, id, name, ct);
            }
            return One(row.Build());
        }

        /// <summary>The (id, display name) pairs of every item of one type in the workspace.</summary>
        /// <remarks>
        /// Materialized before the per-item calls rather than streamed: <c>PageableResponse&lt;T&gt;</c> is lazy,
        /// so enumerating it while also issuing the inner request per row would interleave two paged reads on one
        /// client. WrapList already materializes inside the error guard, which is what makes that safe here.
        /// </remarks>
        private List<(Guid Id, string Name)> ItemsOfType(Guid ws, string itemType, CancellationToken ct)
        {
            var items = new List<(Guid, string)>();
            foreach (var i in FabricApiClient.WrapList("items",
                         () => _api.Client.Core.Items.ListItems(ws, type: itemType, cancellationToken: ct)))
            {
                if (i.Id is { } id)
                {
                    items.Add((id, i.DisplayName?.Trim() ?? id.ToString()));
                }
            }
            return items;
        }

        private void Append(FabricRowBuilder row, Guid ws, Guid itemId, string itemName, CancellationToken ct)
        {
            foreach (var j in FabricApiClient.WrapList("job_instances",
                         () => _api.Client.Core.JobScheduler.ListItemJobInstances(ws, itemId, cancellationToken: ct)))
            {
                row.Str(0, j.Id?.ToString())
                   .Str(1, j.JobType)
                   .Str(2, j.InvokeType?.ToString())
                   // ⚠ `Completed`, not `Succeeded` — fabric.sessions() reports the SAME work with the other
                   // vocabulary. See FabricSessionFunctions.
                   .Str(3, j.Status?.ToString())
                   // Iso, not Ts: the service reports these as ISO STRINGS on a job instance (unlike a Livy
                   // session, where they are DateTimeOffset). An absent one is a legitimate NULL — a job that
                   // never started has no start time.
                   .Iso(4, j.StartTimeUtc)
                   .Iso(5, j.EndTimeUtc)
                   .Str(6, j.FailureReason?.ErrorCode)
                   .Str(7, j.FailureReason?.Message)
                   .Str(8, itemName)
                   .Str(9, itemId.ToString())
                   .EndRow();
            }
        }
    }
}

/// <summary><c>fabric.cancel_job(item, job_instance_id)</c> → true when the cancel was accepted.</summary>
/// <remarks>
/// A scalar, so positional-only (DuckDB scalar functions have no named parameters) and it always acts on the
/// ATTACHED workspace. Cancelling is a request, not a guarantee: a job already finishing may still complete.
/// </remarks>
internal sealed class FabricCancelJobFunction : ICatalogScalarFunction
{
    private readonly FabricApiClient _api;

    internal FabricCancelJobFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "cancel_job";

    public Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("item"),
        FabricApiFunctions.Str("job_instance_id"),
    }, null);

    public Field Result { get; } = new("cancelled", BooleanType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args) => FabricApiFunctions.Guarded(Name, () =>
    {
        var b = new BooleanArray.Builder();
        var ws = _api.WorkspaceId;
        for (int row = 0; row < args.Length; row++)
        {
            string item = FabricArgs.Str(args, 0, row)
                ?? throw new NotSupportedException("cancel_job: 'item' must not be NULL.");
            string instance = FabricArgs.Str(args, 1, row)
                ?? throw new NotSupportedException("cancel_job: 'job_instance_id' must not be NULL.");
            if (!Guid.TryParse(instance, out var instanceId))
            {
                throw new NotSupportedException($"cancel_job: '{instance}' is not a job-instance GUID.");
            }
            var id = _api.ResolveItem(item, "Lakehouse", ws, requireType: false);
            FabricApiClient.Wrap("cancel_job",
                () => _api.Client.Core.JobScheduler.CancelItemJobInstance(ws, id, instanceId));
            b.Append(true);
        }
        return b.Build();
    });
}

/// <summary>
/// <c>fabric.reset_shortcut_cache([workspace := …])</c> — clears the workspace's OneLake shortcut cache.
/// </summary>
/// <remarks>
/// <para><b>NOT TESTED, and knowingly so.</b> This tenant's SERVICE PRINCIPAL is refused with
/// <c>400 PrincipalTypeNotSupported</c> (measured — docs/fabric-api-functions.md §9b), despite the API being
/// documented as SP-supported, so it could not be exercised the way every other function here was. It is
/// expected to work under a USER identity, and in particular under a Fabric notebook's AMBIENT token, which
/// is a user-delegated identity rather than an SP.</para>
/// <para>It is worth having anyway because it is the remedy for the eventual consistency measured in §9c:
/// a re-created shortcut name can transiently conflict, and shortcut metadata can lag a mutation. Blocking
/// LRO, so it returns when the reset is done.</para>
/// <para>A TABLE function rather than a scalar for two reasons: a zero-argument SCALAR is impossible (a
/// scalar's argument batch is how row count crosses), and returning a row lets it report which workspace it
/// acted on.</para>
/// </remarks>
internal sealed class FabricResetShortcutCacheFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricResetShortcutCacheFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "reset_shortcut_cache";

    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    public Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, FabricArgs.Str(args, 0));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("workspace_id"),
            new Field("reset", BooleanType.Default, nullable: true),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _workspace;

        internal Binding(FabricApiClient api, string? workspace)
        {
            _api = api;
            _workspace = workspace;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var ws = _api.ResolveWorkspace(_workspace);
            FabricApiClient.Wrap("reset_shortcut_cache",
                () => _api.Client.Core.OneLakeShortcuts.ResetShortcutCache(ws, cancellationToken: ct));
            var id = new StringArray.Builder();
            var ok = new BooleanArray.Builder();
            id.Append(ws.ToString());
            ok.Append(true);
            return One(Columns, new IArrowArray[] { id.Build(), ok.Build() }, 1);
        }
    }
}

/// <summary>
/// <c>fabric.operation_status(operation_id)</c> — the generic long-running-operation peek, for a call that
/// was submitted without waiting.
/// </summary>
internal sealed class FabricOperationStatusFunction : ICatalogTableFunction
{
    private readonly FabricApiClient _api;

    internal FabricOperationStatusFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "operation_status";

    public Schema Parameters { get; } = new Schema(new[] { FabricApiFunctions.Str("operation_id") }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, FabricArgs.Str(args, 0));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("operation_id"),
            FabricApiFunctions.Str("status"),
            new Field("percent_complete", Int32Type.Default, nullable: true),
            FabricApiFunctions.Ts("created_time"),
            FabricApiFunctions.Ts("last_updated_time"),
            FabricApiFunctions.Str("error_code"),
            FabricApiFunctions.Str("error_message"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _operationId;

        internal Binding(FabricApiClient api, string? operationId)
        {
            _api = api;
            _operationId = operationId;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            if (!Guid.TryParse(_operationId, out var opId))
            {
                throw new NotSupportedException(
                    $"operation_status: '{_operationId}' is not an operation GUID.");
            }
            var state = FabricApiClient.Wrap("operation_status",
                () => _api.Client.Core.LongRunning.GetOperationState(opId, cancellationToken: ct).Value);

            var id = new StringArray.Builder();
            var status = new StringArray.Builder();
            var pct = new Int32Array.Builder();
            var created = FabricApiFunctions.TsBuilder();
            var updated = FabricApiFunctions.TsBuilder();
            var code = new StringArray.Builder();
            var message = new StringArray.Builder();
            id.Append(opId.ToString());
            status.Append(state.Status.ToString());
            if (state.PercentComplete is { } p) { pct.Append(p); } else { pct.AppendNull(); }
            created.Append(state.CreatedTimeUtc);
            updated.Append(state.LastUpdatedTimeUtc);
            code.Append(state.Error?.ErrorCode);
            message.Append(state.Error?.Message);
            return One(Columns, new IArrowArray[]
            {
                id.Build(), status.Build(), pct.Build(), created.Build(), updated.Build(), code.Build(),
                message.Build(),
            }, 1);
        }
    }
}

/// <summary>
/// <c>fabric.lakehouse_tables([workspace := …] [, item := …])</c> — the lakehouse's tables as FABRIC sees
/// them (name, type, format, OneLake location).
/// </summary>
/// <remarks>
/// Overlaps our own catalog discovery for the attached lakehouse, but answers a different question and for a
/// different item: what Fabric has registered (so a shortcut-backed or non-Delta table shows up), and for any
/// lakehouse in reach rather than the attached one.
/// </remarks>
internal sealed class FabricLakehouseTablesFunction : ICatalogTableFunction
{
    // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
    // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
    Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
        Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

    private readonly FabricApiClient _api;

    internal FabricLakehouseTablesFunction(FabricApiClient api) => _api = api;

    public string SchemaName => FabricApiFunctions.SchemaName;
    public string Name => "lakehouse_tables";

    public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

    public Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("workspace"),
        FabricApiFunctions.Str("item"),
    }, null);

    public IArrowTableFunctionBinding Bind(RecordBatch args) =>
        new Binding(_api, FabricArgs.Str(args, 0), FabricArgs.Str(args, 1));

    private sealed class Binding : FabricTableBinding
    {
        private static readonly Schema Columns = new(new[]
        {
            FabricApiFunctions.Str("name"),
            FabricApiFunctions.Str("type"),
            FabricApiFunctions.Str("format"),
            FabricApiFunctions.Str("location"),
        }, null);

        private readonly FabricApiClient _api;
        private readonly string? _workspace;
        private readonly string? _item;

        internal Binding(FabricApiClient api, string? workspace, string? item)
        {
            _api = api;
            _workspace = workspace;
            _item = item;
        }

        public override Schema OutputSchema => Columns;

        protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
        {
            var ws = _api.ResolveWorkspace(_workspace);
            var lh = _api.ResolveItem(_item, "Lakehouse", ws);
            var names = new StringArray.Builder();
            var types = new StringArray.Builder();
            var formats = new StringArray.Builder();
            var locations = new StringArray.Builder();
            int n = 0;
            foreach (var t in FabricApiClient.WrapList("lakehouse_tables",
                         () => _api.Client.Lakehouse.Tables.ListTables(ws, lh, cancellationToken: ct)))
            {
                names.Append(t.Name);
                types.Append(t.Type.ToString());
                formats.Append(t.Format);
                locations.Append(t.Location);
                n++;
            }
            return One(Columns, new IArrowArray[]
            {
                names.Build(), types.Build(), formats.Build(), locations.Build(),
            }, n);
        }
    }
}
