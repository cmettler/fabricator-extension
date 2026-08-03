using System;
using System.Collections.Generic;
using System.Threading;
using Apache.Arrow;
using Microsoft.Fabric.Api.Spark.Models;

// ⚠ `TimeUnit` exists in BOTH Apache.Arrow.Types (microsecond/millisecond, used for the timestamp columns)
// and Microsoft.Fabric.Api.Spark.Models (seconds/minutes/hours/days, the unit on a Duration). Neither
// namespace is imported unqualified here for exactly that reason; the Spark one gets an alias so the
// duration conversion below reads unambiguously and cannot silently bind to the wrong type.
using SparkTimeUnit = Microsoft.Fabric.Api.Spark.Models.TimeUnit;

namespace Fabricator.Bridge;

/// <summary>
/// Spark/Livy session monitoring: <c>fabric.sessions([workspace := …])</c> — what is running right now, and
/// what recently ran, on the workspace's Spark compute.
/// </summary>
/// <remarks>
/// <para><b>What this adds over <c>job_instances</c> — measured, not assumed (2026-08-03, 115 sessions).</b>
/// It is the SPARK-level view of the same work: QUEUED time separated from RUNNING time, the runtime version,
/// the attempt number, high concurrency, and the <c>spark_application_id</c>. None of that exists on a job
/// instance. It is also WORKSPACE-scoped in ONE request, where job instances need one call per item.
/// (The per-executor resource shape is NOT among them — the list endpoint does not populate it; see the
/// column-list remarks below, which record how that was established.)</para>
/// <para><b>⚠ The two surfaces DO join, and an earlier version of this comment claimed they largely would not
/// — that was wrong.</b> All 115 sessions carried a <c>job_instance_id</c>, and a spot-checked JupyterSession's
/// id was present in that notebook's <c>job_instances</c> history. So treat <c>job_instance_id</c> as a
/// real join key, not a sometimes-key.</para>
/// <para><b>⚠ <c>JupyterSession</c> does NOT mean "interactive".</b> Every JupyterSession observed was created
/// by the RunNotebook JOB api, with nobody clicking anything — the value names the session KIND (a Jupyter
/// kernel), not its trigger. Whether a genuinely portal-driven session lacks a job instance is UNVERIFIED here,
/// because no such session existed in the data; do not restate it as fact either way.</para>
/// <para><b>⚠ The same work is labelled DIFFERENTLY in each surface — in two columns, not one.</b> Measured on
/// one identical <c>job_instance_id</c>:</para>
/// <list type="table">
///   <item><term>as a session</term><description><c>job_type = 'JupyterSession'</c>, <c>state = 'Succeeded'</c></description></item>
///   <item><term>as a job instance</term><description><c>job_type = 'RunNotebook'</c>, <c>status = 'Completed'</c></description></item>
/// </list>
/// <para>So a predicate carried across from one to the other silently matches NOTHING. Values pass through
/// VERBATIM rather than being normalised: a synthesised shared vocabulary would hide which API answered, and
/// both are extensible enums whose members can grow.</para>
/// <para><b>⚠ THE SQL-VISIBLE VALUES ARE NOT THE ENUM MEMBER NAMES — DO NOT DERIVE PREDICATES FROM THE SDK.</b>
/// The .NET member is <c>NotStarted</c>; the value that arrives in the column is <c>Not Started</c>, WITH A
/// SPACE (captured live on a submitted-but-not-yet-running session). Meanwhile <c>InProgress</c> has no space.
/// So spacing is per-member, not a convention. Observed values so far: <c>Not Started</c>, <c>InProgress</c>,
/// <c>Succeeded</c>; the SDK additionally declares <c>Failed</c>, <c>Cancelled</c>, <c>Unknown</c> — spelling
/// UNCONFIRMED for those three, so match them defensively. There is no <c>Deduped</c>, which job instances
/// have.</para>
/// <para><b>⚠ CASING IS INCONSISTENT ACROSS COLUMNS OF THE SAME ROW.</b> <c>item_type</c> comes back
/// lower-case (<c>notebook</c>, <c>lakehouse</c>) while <c>job_type</c> and <c>state</c> are PascalCase
/// (<c>SparkSession</c>, <c>Succeeded</c>) — verified on every row. <c>WHERE item_type = 'Notebook'</c>
/// therefore returns nothing while the neighbouring <c>WHERE state = 'Succeeded'</c> works. These are the
/// service's own spellings and are not rewritten here; use <c>lower(…)</c> if you want uniformity.</para>
/// <para><b>A friendlier grouping than <c>job_type</c>:</b> <c>operation_name</c> is a human label the service
/// supplies — observed <c>Session Livy Run</c>, <c>Notebook Scheduled Run</c>,
/// <c>Jupyter Notebook Scheduled Run</c>, <c>Lakehouse Operations</c>, <c>Lakehouse Table Maintenance</c>.</para>
/// <para><b>⚠ <c>submitter</c> (display name) was EMPTY on all 115 rows while <c>submitter_id</c> was
/// populated on all 115.</b> The principal is identified, just not named — so filter and group by
/// <c>submitter_id</c>. Both are kept: the name is what a human wants, and its absence may be specific to
/// service-principal submissions.</para>
/// <para><b>Scope.</b> The call is WORKSPACE-scoped, so one request covers every Spark item — no fan-out, no
/// O(items) throttling risk. The SDK also exposes item-scoped overloads (notebook / lakehouse / Spark job
/// definition); they are deliberately not wired, because <c>WHERE item_name = …</c> over one cheap request is
/// both simpler and strictly more expressive than a parameter that would force the caller to name an item.</para>
/// <para><b>⚠ Live cell OUTPUT is not available and cannot be added here.</b> The model carries
/// <c>spark_application_id</c> and <c>resource_uri</c>, which are POINTERS to where logs live; the entire
/// Fabric SDK assembly contains no method that fetches driver or executor logs. Diagnosis therefore still ends
/// in the portal — this function gets you to the right session, not inside it.</para>
/// </remarks>
internal static class FabricSessionFunctions
{
    internal static void Register(List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricSessionsFunction(api));
    }

    /// <summary>
    /// A Fabric <see cref="Duration"/> normalised to SECONDS.
    /// </summary>
    /// <remarks>
    /// ⚠ Fabric reports these as a <c>{value, unit}</c> PAIR, not in a fixed unit, and <c>TimeUnit</c> is an
    /// Azure EXTENSIBLE enum — so an unrecognised unit is a real possibility rather than a defensive nicety
    /// (and <see cref="Duration"/> is a CLASS, so the whole pair can be absent). An unknown unit yields NULL,
    /// deliberately, instead of the raw number: a column silently mixing seconds with minutes makes every
    /// <c>ORDER BY</c> and every threshold comparison wrong, and wrong is worse than absent for a monitoring
    /// column. Compared against the typed members, not <c>ToString()</c>, so a member rename is a compile error.
    /// </remarks>
    private static double? Seconds(Duration? d)
    {
        if (d is null)
        {
            return null;
        }
        var unit = d.TimeUnit;
        if (unit == SparkTimeUnit.Seconds) { return d.Value; }
        if (unit == SparkTimeUnit.Minutes) { return d.Value * 60d; }
        if (unit == SparkTimeUnit.Hours) { return d.Value * 3600d; }
        if (unit == SparkTimeUnit.Days) { return d.Value * 86400d; }
        return null;
    }

    /// <summary>
    /// <c>fabric.sessions([workspace := …])</c> — one row per Livy session on the workspace's Spark compute.
    /// </summary>
    private sealed class FabricSessionsFunction : ICatalogTableFunction
    {
        // The canonical signature: ONE schema, each field flagged with its style. Explicit so this class
        // may keep declaring the two halves separately (a local shorthand); consumers see the combination.
        Apache.Arrow.Schema Fabricator.Bridge.ITableFunction.Parameters =>
            Fabricator.Bridge.Params.Combine(Parameters, NamedParameters);

        private readonly FabricApiClient _api;

        internal FabricSessionsFunction(FabricApiClient api) => _api = api;

        public string SchemaName => FabricApiFunctions.SchemaName;
        public string Name => "sessions";

        /// <summary>No positional parameters: the zero-argument call is the useful one.</summary>
        public Schema Parameters { get; } = new Schema(System.Array.Empty<Field>(), null);

        public Schema NamedParameters { get; } = new Schema(new[]
        {
            FabricApiFunctions.Str("workspace"),
        }, null);

        public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_api, args);

        private sealed class Binding : FabricTableBinding
        {
            // Column order is chosen for `SELECT *` at a terminal: WHAT and in WHAT STATE first, then WHEN and
            // for HOW LONG, then the Spark ALLOCATION, then the identity/join keys you only want once something
            // looks wrong.
            //
            // The whole model is exposed — every field the service offers is a column here.
            //
            // ⚠ THE SEVEN ALLOCATION COLUMNS (driver_*/executor_*/num_executors/dynamic_allocation/
            // max_executors) WERE NULL IN EVERY SESSION OBSERVED ON THE TEST TENANT, AND THAT IS ABOUT THE
            // WORKLOAD, NOT ABOUT THIS FUNCTION. Those 116 sessions were Python/Jupyter notebook runs
            // (`runtime_version = jupyter1.0`) plus system-managed lakehouse jobs — none of which has a Spark
            // executor allocation to report. A PySpark session is expected to populate them; that has NOT been
            // observed here, so treat it as unverified rather than promised.
            //
            // An earlier revision DROPPED these seven, reasoning that the list endpoint "never populates" them.
            // That was wrong, and the error is worth keeping visible: the evidence was NULL across all finished
            // sessions PLUS NULL on one session caught mid-run, which does rule out "only reported once
            // finished" — but it controls for session LIFECYCLE and says nothing about session KIND, the
            // variable that actually differed. One controlled variable does not license a structural claim.
            private static readonly Schema Columns = new(new[]
            {
                FabricApiFunctions.Str("item_name"),
                FabricApiFunctions.Str("item_type"),
                FabricApiFunctions.Str("job_type"),
                FabricApiFunctions.Str("state"),
                FabricApiFunctions.Str("operation_name"),
                FabricApiFunctions.Str("submitter"),
                FabricApiFunctions.Ts("submitted_time"),
                FabricApiFunctions.Ts("start_time"),
                FabricApiFunctions.Ts("end_time"),
                FabricApiFunctions.Dbl("queued_seconds"),
                FabricApiFunctions.Dbl("running_seconds"),
                FabricApiFunctions.Dbl("total_seconds"),
                FabricApiFunctions.Str("origin"),
                FabricApiFunctions.Int32("attempt_number"),
                FabricApiFunctions.Int32("max_attempts"),
                FabricApiFunctions.Str("runtime_version"),
                FabricApiFunctions.Bool("high_concurrency"),
                // The Spark allocation. Populated for a session that HAS one — see the remarks above.
                FabricApiFunctions.Int32("driver_cores"),
                FabricApiFunctions.Int32("driver_memory"),
                // ⚠ VARCHAR, not INTEGER: the SDK types ExecutorCores as `object` — a spec union leaking
                // through the generator. Rendered rather than cast, since a numeric cast would throw on
                // whatever other shape the service is entitled to send.
                FabricApiFunctions.Str("executor_cores"),
                FabricApiFunctions.Int32("executor_memory"),
                FabricApiFunctions.Int32("num_executors"),
                FabricApiFunctions.Bool("dynamic_allocation"),
                FabricApiFunctions.Int32("max_executors"),
                FabricApiFunctions.Str("cancellation_reason"),
                // Identity + join keys last: `job_instance_id` is the join to job_instances, and the
                // uri/app-id columns are the only route onward to the portal.
                FabricApiFunctions.Str("livy_id"),
                FabricApiFunctions.Str("livy_name"),
                FabricApiFunctions.Str("item_id"),
                FabricApiFunctions.Str("creator_item_id"),
                FabricApiFunctions.Str("job_instance_id"),
                FabricApiFunctions.Str("spark_application_id"),
                FabricApiFunctions.Str("capacity_id"),
                FabricApiFunctions.Str("submitter_id"),
                FabricApiFunctions.Str("resource_uri"),
            }, null);

            private readonly FabricApiClient _api;
            private readonly string? _workspace;

            internal Binding(FabricApiClient api, RecordBatch args)
            {
                _api = api;
                _workspace = FabricArgs.Str(args, 0);
            }

            public override Schema OutputSchema => Columns;

            protected override IAsyncEnumerable<RecordBatch> Rows(CancellationToken ct)
            {
                var ws = _api.ResolveWorkspace(_workspace);
                var row = new FabricRowBuilder(Columns);
                // WrapList, not a bare foreach: PageableResponse<T> is LAZY, so the request happens during
                // enumeration — outside any try around the call itself. See FabricApiClient.WrapList.
                foreach (var s in FabricApiClient.WrapList("sessions",
                             () => _api.Client.Spark.LivySessions.ListLivySessions(ws, cancellationToken: ct)))
                {
                    row.Str(0, s.ItemName)
                       .Str(1, s.ItemType?.ToString())
                       .Str(2, s.JobType?.ToString())
                       // Verbatim — `Succeeded` here vs `Completed` on the SAME work as a job instance, and
                       // PascalCase where item_type above is lower-case. See the class remarks.
                       .Str(3, s.State?.ToString())
                       .Str(4, s.OperationName)
                       // ⚠ Submitter is a nullable REFERENCE whose own Id is a non-nullable Guid, so the null
                       // test belongs on the PARENT. Written the other way this reports Guid.Empty as a real
                       // principal — the same shape as the LastSyncTime/.NET-epoch trap in the git functions.
                       // Measured EMPTY on every row while submitter_id was populated on every row.
                       .Str(5, s.Submitter?.DisplayName)
                       .Ts(6, s.SubmittedDateTime)
                       .Ts(7, s.StartDateTime)
                       .Ts(8, s.EndDateTime)
                       .Dbl(9, Seconds(s.QueuedDuration))
                       .Dbl(10, Seconds(s.RunningDuration))
                       .Dbl(11, Seconds(s.TotalDuration))
                       .Str(12, s.Origin?.ToString())
                       .Int(13, s.AttemptNumber)
                       .Int(14, s.MaxNumberOfAttempts)
                       .Str(15, s.RuntimeVersion)
                       .Bool(16, s.IsHighConcurrency)
                       .Int(17, s.DriverCores)
                       .Int(18, s.DriverMemory)
                       // ⚠ ExecutorCores is `object` in the SDK, so it is rendered as text — see the column list.
                       .Str(19, s.ExecutorCores?.ToString())
                       .Int(20, s.ExecutorMemory)
                       .Int(21, s.NumExecutors)
                       .Bool(22, s.IsDynamicAllocationEnabled)
                       .Int(23, s.DynamicAllocationMaxExecutors)
                       .Str(24, s.CancellationReason)
                       .Str(25, s.LivyId?.ToString())
                       .Str(26, s.LivyName)
                       // ⚠ Item / CreatorItem are nullable references holding NON-nullable Guids: same
                       // parent-null rule as Submitter above.
                       .Str(27, s.Item?.ItemId.ToString())
                       .Str(28, s.CreatorItem?.ItemId.ToString())
                       .Str(29, s.JobInstanceId?.ToString())
                       .Str(30, s.SparkApplicationId)
                       .Str(31, s.CapacityId?.ToString())
                       .Str(32, s.Submitter?.Id.ToString())
                       .Str(33, s.LivySessionItemResourceUri)
                       .EndRow();
                }
                return One(row.Build());
            }
        }
    }
}
