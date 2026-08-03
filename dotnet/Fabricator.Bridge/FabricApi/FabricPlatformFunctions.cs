using System;
using System.Collections.Generic;
using System.Threading;
using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// The remaining P3 reads: capacities, Spark environments, OneLake data-access roles, and mirrored-database
/// status.
/// </summary>
/// <remarks>
/// <para>All READ-only, and that is the whole distinction that admitted them. Each area's WRITE half stays out
/// for a recorded reason: assigning a workspace to a capacity is infrastructure (and the capacity list only
/// existed to feed it); publishing an environment is meaningless without the library-definition writes that rule
/// 2 excludes; a data-access role WRITE is folder security policy from a SQL string (rule 1); and
/// starting/stopping mirroring reconfigures someone else's ingestion pipeline, which is not something a
/// transformation should do as a side effect.</para>
/// <para>Mirroring is the one area §10 filed as "skip, but WATCH", naming <c>mirroring_status</c> as what
/// would justify revisiting it — a mirrored table is only trustworthy once its replication has caught up, so
/// checking that before reading it is exactly a data-path concern.</para>
/// </remarks>
internal static class FabricPlatformFunctions
{
    internal static void Register(List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricCapacitiesFunction(api));
        tables.Add(new FabricEnvironmentsFunction(api));
        tables.Add(new FabricDataAccessRolesFunction(api));
        tables.Add(new FabricMirroredDatabasesFunction(api));
        tables.Add(new FabricMirroringStatusFunction(api));
        tables.Add(new FabricMirroredTablesFunction(api));
    }
}

/// <summary><c>fabric.capacities()</c> — the capacities this identity can see, with SKU and region.</summary>
/// <remarks>
/// Useful on its own for answering "is the workspace I am writing to on a Fabric capacity" — which decides
/// whether an enhanced semantic-model refresh is even permitted (shared capacity cannot run one).
/// </remarks>
internal sealed class FabricCapacitiesFunction : FabricRowsFunction
{
    internal FabricCapacitiesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "capacities";

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("sku"),
        FabricApiFunctions.Str("region"),
        FabricApiFunctions.Str("state"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        foreach (var c in FabricApiClient.WrapList("capacities",
                     () => Api.Client.Core.Capacities.ListCapacities(cancellationToken: ct)))
        {
            row.Str(0, c.Id.ToString())
               .Str(1, c.DisplayName)
               .Str(2, c.Sku)
               .Str(3, c.Region)
               .Str(4, c.State.ToString())
               .EndRow();
        }
    }
}

/// <summary>
/// <c>fabric.environments()</c> — the workspace's Spark environments and their publish state.
/// </summary>
/// <remarks>
/// This is the name→id helper §10 anticipated: <c>run_notebook</c>'s <c>config_json</c> can pin an
/// environment, and the id is otherwise only visible in the portal URL. <c>publish_state</c> matters because a
/// notebook run against an environment whose publish is still <c>Running</c> does not get the libraries it
/// expects.
/// </remarks>
internal sealed class FabricEnvironmentsFunction : FabricRowsFunction
{
    internal FabricEnvironmentsFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "environments";

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("description"),
        FabricApiFunctions.Str("publish_state"),
        FabricApiFunctions.Str("target_version"),
        FabricApiFunctions.Ts("publish_start_time"),
        FabricApiFunctions.Ts("publish_end_time"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        foreach (var e in FabricApiClient.WrapList("environments",
                     () => Api.Client.Environment.Items.ListEnvironments(ws, cancellationToken: ct)))
        {
            var publish = e.Properties?.PublishDetails;
            row.Str(0, e.Id?.ToString())
               .Str(1, e.DisplayName)
               .Str(2, e.Description)
               .Str(3, publish?.State?.ToString())
               .Str(4, publish?.TargetVersion?.ToString())
               .Ts(5, publish?.StartTime)
               .Ts(6, publish?.EndTime)
               .EndRow();
        }
    }
}

/// <summary>
/// <c>fabric.data_access_roles([item := …])</c> — the OneLake data-access roles defined on an item.
/// </summary>
/// <remarks>
/// <para>READ only (rule 1). Reading matters for a very practical reason: OneLake role scoping is a common cause
/// of "the table is there but my identity sees no rows", and this is the only way to see from SQL whether such a
/// role exists at all.</para>
/// <para>The rule CONSTRAINTS (the path/column/row restrictions inside each decision rule) are summarized as
/// counts rather than projected. They are a nested polymorphic tree whose faithful flattening would be several
/// more functions, and a wrong flattening of a SECURITY policy is worse than not showing it — the portal is the
/// right place to audit one in detail.</para>
/// </remarks>
internal sealed class FabricDataAccessRolesFunction : FabricRowsFunction
{
    internal FabricDataAccessRolesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "data_access_roles";

    public override Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("item"),
        FabricApiFunctions.Str("workspace"),
    }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("kind"),
        FabricApiFunctions.Str("etag"),
        FabricApiFunctions.Int32("decision_rule_count"),
        FabricApiFunctions.Int32("entra_member_count"),
        FabricApiFunctions.Int32("item_member_count"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[1]);
        var item = Api.ResolveItem(args[0], "Lakehouse", ws);
        var roles = FabricApiClient.Wrap("data_access_roles",
            () => Api.Client.Core.OneLakeDataAccessSecurity.ListDataAccessRoles(
                ws, item, cancellationToken: ct).Value);
        foreach (var r in roles.Value ?? (IReadOnlyList<Microsoft.Fabric.Api.Core.Models.DataAccessRoleListItem>)
                     System.Array.Empty<Microsoft.Fabric.Api.Core.Models.DataAccessRoleListItem>())
        {
            row.Str(0, r.Id?.ToString())
               .Str(1, r.Name)
               .Str(2, r.Kind?.ToString())
               .Str(3, r.ETag)
               .Int(4, r.DecisionRules?.Count ?? 0)
               .Int(5, r.Members?.MicrosoftEntraMembers?.Count ?? 0)
               .Int(6, r.Members?.FabricItemMembers?.Count ?? 0)
               .EndRow();
        }
    }
}

/// <summary><c>fabric.mirrored_databases()</c> — the workspace's mirrored databases and where their Delta tables
/// land in OneLake.</summary>
/// <remarks>
/// <c>onelake_tables_path</c> is the actionable column: it is the path a Delta <c>ATTACH</c> can point at to read
/// the mirrored data directly, instead of going through the SQL endpoint.
/// </remarks>
internal sealed class FabricMirroredDatabasesFunction : FabricRowsFunction
{
    internal FabricMirroredDatabasesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "mirrored_databases";

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("description"),
        FabricApiFunctions.Str("onelake_tables_path"),
        FabricApiFunctions.Str("default_schema"),
        FabricApiFunctions.Str("sql_endpoint_id"),
        FabricApiFunctions.Str("sql_endpoint_connection_string"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        foreach (var db in FabricApiClient.WrapList("mirrored_databases",
                     () => Api.Client.MirroredDatabase.Items.ListMirroredDatabases(ws, cancellationToken: ct)))
        {
            var p = db.Properties;
            row.Str(0, db.Id?.ToString())
               .Str(1, db.DisplayName)
               .Str(2, db.Description)
               .Str(3, p?.OneLakeTablesPath)
               .Str(4, p?.DefaultSchema)
               .Str(5, p?.SqlEndpointProperties?.Id)
               .Str(6, p?.SqlEndpointProperties?.ConnectionString)
               .EndRow();
        }
    }
}

/// <summary><c>fabric.mirroring_status(database)</c> — whether a mirrored database is actually replicating.</summary>
internal sealed class FabricMirroringStatusFunction : FabricRowsFunction
{
    internal FabricMirroringStatusFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "mirroring_status";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("database") }, null);

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("status"),
        FabricApiFunctions.Str("error_code"),
        FabricApiFunctions.Str("error_message"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[1]);
        var db = Api.ResolveItem(args[0], "MirroredDatabase", ws);
        var status = FabricApiClient.Wrap("mirroring_status",
            () => Api.Client.MirroredDatabase.Mirroring.GetMirroringStatus(ws, db, cancellationToken: ct).Value);
        row.Str(0, status.Status.ToString())
           .Str(1, status.Error?.ErrorCode)
           .Str(2, status.Error?.Message)
           .EndRow();
    }
}

/// <summary>
/// <c>fabric.mirrored_tables(database)</c> — per-table replication state and freshness.
/// </summary>
/// <remarks>
/// <c>last_sync_time</c> and <c>last_sync_latency_seconds</c> are why this is worth having in SQL: they let a
/// model assert its source is caught up before it reads, rather than silently transforming stale data.
/// </remarks>
internal sealed class FabricMirroredTablesFunction : FabricRowsFunction
{
    internal FabricMirroredTablesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "mirrored_tables";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("database") }, null);

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("source_schema_name"),
        FabricApiFunctions.Str("source_table_name"),
        FabricApiFunctions.Str("source_object_type"),
        FabricApiFunctions.Str("status"),
        FabricApiFunctions.Int64("processed_rows"),
        FabricApiFunctions.Int64("processed_bytes"),
        FabricApiFunctions.Ts("last_sync_time"),
        FabricApiFunctions.Int32("last_sync_latency_seconds"),
        FabricApiFunctions.Str("error_code"),
        FabricApiFunctions.Str("error_message"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[1]);
        var db = Api.ResolveItem(args[0], "MirroredDatabase", ws);
        foreach (var t in FabricApiClient.WrapList("mirrored_tables",
                     () => Api.Client.MirroredDatabase.Mirroring.GetTablesMirroringStatus(
                         ws, db, cancellationToken: ct)))
        {
            var m = t.Metrics;
            row.Str(0, t.SourceSchemaName)
               .Str(1, t.SourceTableName)
               .Str(2, t.SourceObjectType.ToString())
               .Str(3, t.Status.ToString())
               .Int(4, m?.ProcessedRows)
               .Int(5, m?.ProcessedBytes)
               // LastSyncDateTime is non-nullable on a nullable parent, so the null test is on `m`.
               .Ts(6, m is null ? null : m.LastSyncDateTime)
               .Int(7, m?.LastSyncLatencyInSeconds)
               .Str(8, t.Error?.ErrorCode)
               .Str(9, t.Error?.Message)
               .EndRow();
        }
    }
}
