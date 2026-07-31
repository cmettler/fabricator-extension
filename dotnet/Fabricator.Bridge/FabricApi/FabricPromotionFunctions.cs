using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Apache.Arrow;
using Microsoft.Fabric.Api.Core.Models;

namespace Fabricator.Bridge;

/// <summary>
/// The two PROMOTION surfaces: workspace git integration and deployment pipelines.
/// </summary>
/// <remarks>
/// <para><b>Why these are here at all, given the standing rule.</b> Exclusion rule 2 in
/// docs/fabric-api-functions.md §10 keeps authoring and deployment surfaces with the tooling that owns them
/// (fabric-cicd, the portal, CI), and that rule still holds for item DEFINITIONS — a base64 payload in a SQL
/// string is hostile. Git and pipelines were filed as "P3 demand-driven … would serve promotion flows if users
/// ask", and they were asked for. What makes them defensible where definition-writing is not: they move
/// ALREADY-AUTHORED content between environments by reference, so nothing about the content is expressed in SQL,
/// and a dbt project whose models write Delta plausibly wants to promote the workspace in the same run.</para>
/// <para><b>What is still excluded here:</b> <c>Connect</c>/<c>Disconnect</c> and the git CREDENTIAL calls (rule
/// 1 — they carry a PAT or a connection secret), pipeline/stage CRUD and role assignments, and workspace-to-stage
/// assignment. Reading and advancing an existing configuration is in; establishing or re-pointing one is not.</para>
/// <para><b>All four git calls and the deploy are LONG-RUNNING</b> — the SDK blocks on them (its
/// <c>timeoutInMinutes</c> parameter, default 60), so there is no submit-and-poll shape and no request id. A
/// commit of a large workspace is a minutes-scale statement.</para>
/// </remarks>
internal static class FabricPromotionFunctions
{
    internal static void Register(List<ICatalogTableFunction> tables, FabricApiClient api)
    {
        tables.Add(new FabricGitStatusFunction(api));
        tables.Add(new FabricGitConnectionFunction(api));
        tables.Add(new FabricGitCommitFunction(api));
        tables.Add(new FabricGitUpdateFunction(api));
        tables.Add(new FabricDeploymentPipelinesFunction(api));
        tables.Add(new FabricDeploymentStagesFunction(api));
        tables.Add(new FabricDeploymentStageItemsFunction(api));
        tables.Add(new FabricDeployFunction(api));
        tables.Add(new FabricDeploymentOperationsFunction(api));
    }

    /// <summary>
    /// Converts a <c>wait_seconds</c> argument into the SDK's <c>timeoutInMinutes</c>.
    /// </summary>
    /// <remarks>
    /// The vocabulary is <c>wait_seconds</c> for consistency with every other blocking function here, but these
    /// APIs only accept MINUTES — so the value is rounded UP (a caller asking for 90 s gets 2 min rather than 1)
    /// and floored at 1, because 0 would mean "give up immediately" rather than "do not wait", which these
    /// endpoints cannot express at all.
    /// </remarks>
    internal static int TimeoutMinutes(string? waitSeconds, int fallback = 60)
    {
        if (!long.TryParse(waitSeconds, out var seconds) || seconds <= 0)
        {
            return fallback;
        }
        return (int)Math.Max(1, Math.Min(int.MaxValue, (seconds + 59) / 60));
    }
}

/// <summary>
/// <c>fabric_git_status()</c> — the workspace's uncommitted/unpulled changes against its connected git branch.
/// </summary>
/// <remarks>
/// The two heads (<c>workspace_head</c>, <c>remote_commit_hash</c>) are repeated on every change row rather than
/// wrapped in a struct, per the D4 output rule. A CLEAN workspace still returns ONE row — with the change columns
/// NULL — because the heads are the useful answer in that case, and emitting nothing would make "in sync" and
/// "not connected" indistinguishable (they differ: the latter throws).
/// </remarks>
internal sealed class FabricGitStatusFunction : FabricRowsFunction
{
    internal FabricGitStatusFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_git_status";

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("workspace_head"),
        FabricApiFunctions.Str("remote_commit_hash"),
        FabricApiFunctions.Str("item_id"),
        FabricApiFunctions.Str("logical_id"),
        FabricApiFunctions.Str("item_type"),
        FabricApiFunctions.Str("display_name"),
        FabricApiFunctions.Str("remote_change"),
        FabricApiFunctions.Str("workspace_change"),
        FabricApiFunctions.Str("conflict_type"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        var status = FabricApiClient.Wrap("git_status",
            () => Api.Client.Core.Git.GetStatus(ws, cancellationToken: ct).Value);
        var changes = status.Changes ?? (IReadOnlyList<ItemChange>)System.Array.Empty<ItemChange>();
        if (changes.Count == 0)
        {
            row.Str(0, status.WorkspaceHead).Str(1, status.RemoteCommitHash);
            for (int i = 2; i < Columns.FieldsList.Count; i++)
            {
                row.Str(i, null);
            }
            row.EndRow();
            return;
        }
        foreach (var change in changes)
        {
            var md = change.ItemMetadata;
            row.Str(0, status.WorkspaceHead)
               .Str(1, status.RemoteCommitHash)
               .Str(2, md?.ItemIdentifier?.ObjectId?.ToString())
               .Str(3, md?.ItemIdentifier?.LogicalId?.ToString())
               .Str(4, md?.ItemType.ToString())
               .Str(5, md?.DisplayName)
               .Str(6, change.RemoteChange?.ToString())
               .Str(7, change.WorkspaceChange?.ToString())
               .Str(8, change.ConflictType.ToString())
               .EndRow();
        }
    }
}

/// <summary><c>fabric_git_connection()</c> — which repository/branch the workspace is connected to, and when it
/// last synced.</summary>
internal sealed class FabricGitConnectionFunction : FabricRowsFunction
{
    internal FabricGitConnectionFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_git_connection";

    public override Schema NamedParameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("workspace") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("state"),
        FabricApiFunctions.Str("repository_name"),
        FabricApiFunctions.Str("branch_name"),
        FabricApiFunctions.Str("directory_name"),
        FabricApiFunctions.Str("head"),
        FabricApiFunctions.Ts("last_sync_time"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[0]);
        var conn = FabricApiClient.Wrap("git_connection",
            () => Api.Client.Core.Git.GetConnection(ws, cancellationToken: ct).Value);
        var provider = conn.GitProviderDetails;
        var sync = conn.GitSyncDetails;
        row.Str(0, conn.GitConnectionState?.ToString())
           .Str(1, provider?.RepositoryName)
           .Str(2, provider?.BranchName)
           .Str(3, provider?.DirectoryName)
           .Str(4, sync?.Head)
           // LastSyncTime is a non-nullable DateTimeOffset on a NULLABLE parent, so the null check is on `sync`.
           .Ts(5, sync is null ? null : sync.LastSyncTime)
           .EndRow();
    }
}

/// <summary>
/// <c>fabric_git_commit([mode := 'All'] [, comment := …] [, items_json := …] [, workspace_head := …])</c> —
/// commits the workspace's changes to the connected branch.
/// </summary>
/// <remarks>
/// <c>workspace_head</c> is the API's OPTIMISTIC CONCURRENCY token: supplying the head you read from
/// <c>fabric_git_status()</c> makes the commit fail rather than overwrite if someone else committed in between.
/// Omitting it commits unconditionally, which is why it is worth exposing at all.
/// </remarks>
internal sealed class FabricGitCommitFunction : FabricRowsFunction
{
    internal FabricGitCommitFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_git_commit";

    public override Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("mode"),
        FabricApiFunctions.Str("comment"),
        FabricApiFunctions.Str("items_json"),
        FabricApiFunctions.Str("workspace_head"),
        FabricApiFunctions.Str("wait_seconds"),
        FabricApiFunctions.Str("workspace"),
    }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("status"),
        FabricApiFunctions.Str("mode"),
        FabricApiFunctions.Str("items"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var ws = Api.ResolveWorkspace(args[5]);
        var mode = ParseMode(args[0]);
        var request = new CommitToGitRequest(mode);
        if (!string.IsNullOrWhiteSpace(args[1]))
        {
            request.Comment = args[1];
        }
        if (!string.IsNullOrWhiteSpace(args[3]))
        {
            request.WorkspaceHead = args[3];
        }
        var ids = ParseItemIds(args[2]);
        foreach (var id in ids)
        {
            request.Items.Add(id);
        }
        if (mode == CommitMode.Selective && ids.Count == 0)
        {
            throw new NotSupportedException(
                "fabric_git_commit: mode := 'Selective' needs items_json, e.g. "
                + "items_json := '[\"<object-id>\", …]' (read the ids from fabric_git_status()).");
        }
        FabricApiClient.Wrap("git_commit", () => Api.Client.Core.Git.CommitToGit(
            ws, request, cancellationToken: ct,
            timeoutInMinutes: FabricPromotionFunctions.TimeoutMinutes(args[4])));
        row.Str(0, "Committed")
           .Str(1, mode.ToString())
           .Str(2, ids.Count == 0 ? "all" : ids.Count.ToString())
           .EndRow();
    }

    // An extensible enum accepts ANY string, so an unrecognized mode would reach the service as-is and come back
    // as an opaque parse error. Reject it here, naming the two legal values.
    private static CommitMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return CommitMode.All;
        }
        if (string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
        {
            return CommitMode.All;
        }
        if (string.Equals(mode, "Selective", StringComparison.OrdinalIgnoreCase))
        {
            return CommitMode.Selective;
        }
        throw new NotSupportedException($"fabric_git_commit: mode must be 'All' or 'Selective', not '{mode}'.");
    }

    /// <summary>
    /// Parses <c>items_json</c>: an array of item object-ids, or of
    /// <c>{"object_id": …}</c> / <c>{"logical_id": …}</c> objects when the logical id is what you hold.
    /// </summary>
    private static List<ItemIdentifier> ParseItemIds(string? json)
    {
        var result = new List<ItemIdentifier>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        using var doc = JsonDocument.Parse(json!);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new NotSupportedException("fabric_git_commit: items_json must be a JSON array.");
        }
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = new ItemIdentifier();
            if (e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var objectId))
            {
                id.ObjectId = objectId;
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("object_id", out var o) && Guid.TryParse(o.GetString(), out var oid))
                {
                    id.ObjectId = oid;
                }
                if (e.TryGetProperty("logical_id", out var l) && Guid.TryParse(l.GetString(), out var lid))
                {
                    id.LogicalId = lid;
                }
            }
            if (id.ObjectId is null && id.LogicalId is null)
            {
                throw new NotSupportedException(
                    "fabric_git_commit: each items_json element must be an item GUID, or an object with "
                    + "\"object_id\" or \"logical_id\".");
            }
            result.Add(id);
        }
        return result;
    }
}

/// <summary>
/// <c>fabric_git_update(remote_commit_hash [, conflict_resolution := 'PreferRemote'] [, allow_override := …])</c>
/// — pulls the branch's content into the workspace.
/// </summary>
/// <remarks>
/// The hash is REQUIRED and positional on purpose: updating to "whatever is on the branch now" is how a promotion
/// flow silently deploys a commit nobody reviewed. Read it from <c>fabric_git_status()</c> in the same script.
/// </remarks>
internal sealed class FabricGitUpdateFunction : FabricRowsFunction
{
    internal FabricGitUpdateFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_git_update";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("remote_commit_hash") }, null);

    public override Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("conflict_resolution"),
        FabricApiFunctions.Str("allow_override"),
        FabricApiFunctions.Str("workspace_head"),
        FabricApiFunctions.Str("wait_seconds"),
        FabricApiFunctions.Str("workspace"),
    }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("status"),
        FabricApiFunctions.Str("remote_commit_hash"),
        FabricApiFunctions.Str("conflict_resolution"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args[0]))
        {
            throw new NotSupportedException(
                "fabric_git_update: pass the remote commit hash to update to "
                + "(read it from fabric_git_status().remote_commit_hash).");
        }
        var ws = Api.ResolveWorkspace(args[5]);
        var request = new UpdateFromGitRequest(args[0]!);
        if (!string.IsNullOrWhiteSpace(args[3]))
        {
            request.WorkspaceHead = args[3];
        }
        var policy = ParsePolicy(args[1]);
        request.ConflictResolution = new WorkspaceConflictResolution(ConflictResolutionType.Workspace, policy);
        if (string.Equals(args[2], "true", StringComparison.OrdinalIgnoreCase))
        {
            request.Options = new UpdateOptions { AllowOverrideItems = true };
        }
        FabricApiClient.Wrap("git_update", () => Api.Client.Core.Git.UpdateFromGit(
            ws, request, cancellationToken: ct,
            timeoutInMinutes: FabricPromotionFunctions.TimeoutMinutes(args[4])));
        row.Str(0, "Updated").Str(1, args[0]).Str(2, policy.ToString()).EndRow();
    }

    // PreferRemote is the default because that is what "pull this reviewed commit into the workspace" means; the
    // alternative silently keeps local edits and makes the update a no-op for those items.
    private static ConflictResolutionPolicy ParsePolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "PreferRemote", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResolutionPolicy.PreferRemote;
        }
        if (string.Equals(value, "PreferWorkspace", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResolutionPolicy.PreferWorkspace;
        }
        throw new NotSupportedException(
            $"fabric_git_update: conflict_resolution must be 'PreferRemote' or 'PreferWorkspace', not '{value}'.");
    }
}

/// <summary><c>fabric_deployment_pipelines()</c> — the pipelines this identity can see.</summary>
internal sealed class FabricDeploymentPipelinesFunction : FabricRowsFunction
{
    internal FabricDeploymentPipelinesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_deployment_pipelines";

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("description"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        foreach (var p in FabricApiClient.WrapList("deployment_pipelines",
                     () => Api.Client.Core.DeploymentPipelines.ListDeploymentPipelines(cancellationToken: ct)))
        {
            row.Str(0, p.Id.ToString()).Str(1, p.DisplayName).Str(2, p.Description).EndRow();
        }
    }
}

/// <summary><c>fabric_deployment_pipeline_stages(pipeline)</c> — a pipeline's stages, in order, with the
/// workspace assigned to each.</summary>
internal sealed class FabricDeploymentStagesFunction : FabricRowsFunction
{
    internal FabricDeploymentStagesFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_deployment_pipeline_stages";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("pipeline") }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("id"),
        FabricApiFunctions.Int32("stage_order"),
        FabricApiFunctions.Str("name"),
        FabricApiFunctions.Str("description"),
        FabricApiFunctions.Str("workspace_id"),
        FabricApiFunctions.Str("workspace_name"),
        FabricApiFunctions.Bool("is_public"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var pipeline = Api.ResolvePipeline(args[0], ct);
        foreach (var s in Api.ListStages(pipeline, ct))
        {
            row.Str(0, s.Id.ToString())
               .Int(1, s.Order)
               .Str(2, s.DisplayName)
               .Str(3, s.Description)
               .Str(4, s.WorkspaceId?.ToString())
               .Str(5, s.WorkspaceName)
               .Bool(6, s.IsPublic)
               .EndRow();
        }
    }
}

/// <summary><c>fabric_deployment_pipeline_items(pipeline, stage)</c> — what a stage contains, and when each item
/// was last deployed.</summary>
internal sealed class FabricDeploymentStageItemsFunction : FabricRowsFunction
{
    internal FabricDeploymentStageItemsFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_deployment_pipeline_items";

    public override Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("pipeline"),
        FabricApiFunctions.Str("stage"),
    }, null);

    protected override Schema Columns { get; } = new(new[]
    {
        FabricApiFunctions.Str("item_id"),
        FabricApiFunctions.Str("item_type"),
        FabricApiFunctions.Str("item_name"),
        FabricApiFunctions.Str("source_item_id"),
        FabricApiFunctions.Str("target_item_id"),
        FabricApiFunctions.Ts("last_deployment_time"),
    }, null);

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var pipeline = Api.ResolvePipeline(args[0], ct);
        var stage = Api.ResolveStage(pipeline, args[1], ct);
        foreach (var i in FabricApiClient.WrapList("deployment_stage_items",
                     () => Api.Client.Core.DeploymentPipelines.ListDeploymentPipelineStageItems(
                         pipeline, stage, cancellationToken: ct)))
        {
            row.Str(0, i.ItemId.ToString())
               .Str(1, i.ItemType.ToString())
               .Str(2, i.ItemDisplayName)
               .Str(3, i.SourceItemId?.ToString())
               .Str(4, i.TargetItemId?.ToString())
               .Ts(5, i.LastDeploymentTime)
               .EndRow();
        }
    }
}

/// <summary>
/// <c>fabric_deploy(pipeline, source_stage, target_stage [, note := …] [, wait_seconds := …])</c> — deploys one
/// stage's content into the next.
/// </summary>
/// <remarks>
/// Deploys the WHOLE stage. A selective deployment names items by <c>(sourceItemId, itemType)</c> pairs, which is
/// a shape better expressed by the tooling that already knows the item inventory — and getting it wrong here
/// means deploying the wrong subset silently, whereas a whole-stage deploy is exactly what the portal button
/// does.
/// </remarks>
internal sealed class FabricDeployFunction : FabricRowsFunction
{
    internal FabricDeployFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_deploy";

    public override Schema Parameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("pipeline"),
        FabricApiFunctions.Str("source_stage"),
        FabricApiFunctions.Str("target_stage"),
    }, null);

    public override Schema NamedParameters { get; } = new Schema(new[]
    {
        FabricApiFunctions.Str("note"),
        FabricApiFunctions.Str("wait_seconds"),
    }, null);

    protected override Schema Columns => FabricDeploymentOperationsFunction.OperationColumns;

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var pipeline = Api.ResolvePipeline(args[0], ct);
        var source = Api.ResolveStage(pipeline, args[1], ct);
        var target = Api.ResolveStage(pipeline, args[2], ct);
        var request = new DeployRequest(source, target);
        if (!string.IsNullOrWhiteSpace(args[3]))
        {
            request.Note = args[3];
        }
        var op = FabricApiClient.Wrap("deploy",
            () => Api.Client.Core.DeploymentPipelines.DeployStageContent(
                pipeline, request, cancellationToken: ct,
                timeoutInMinutes: FabricPromotionFunctions.TimeoutMinutes(args[4])).Value);
        FabricDeploymentOperationsFunction.OperationRow(
            row, op.Id, op.Type.ToString(), op.Status.ToString(), op.ExecutionStartTime, op.ExecutionEndTime,
            op.SourceStageId, op.TargetStageId, op.PerformedBy?.DisplayName);
    }
}

/// <summary><c>fabric_deployment_pipeline_operations(pipeline)</c> — deployment history, newest first, for
/// asserting that the last deploy actually succeeded.</summary>
internal sealed class FabricDeploymentOperationsFunction : FabricRowsFunction
{
    internal FabricDeploymentOperationsFunction(FabricApiClient api) : base(api)
    {
    }

    public override string Name => "fabric_deployment_pipeline_operations";

    public override Schema Parameters { get; } =
        new Schema(new[] { FabricApiFunctions.Str("pipeline") }, null);

    /// <summary>Shared with <c>fabric_deploy</c> so a submitted deploy and a history row read identically.</summary>
    internal static Schema OperationColumns { get; } = new(new[]
    {
        FabricApiFunctions.Str("operation_id"),
        FabricApiFunctions.Str("type"),
        FabricApiFunctions.Str("status"),
        FabricApiFunctions.Ts("execution_start_time"),
        FabricApiFunctions.Ts("execution_end_time"),
        FabricApiFunctions.Str("source_stage_id"),
        FabricApiFunctions.Str("target_stage_id"),
        FabricApiFunctions.Str("performed_by"),
    }, null);

    internal static void OperationRow(
        FabricRowBuilder row, Guid id, string? type, string? status, DateTimeOffset? start, DateTimeOffset? end,
        Guid? sourceStage, Guid? targetStage, string? performedBy)
    {
        row.Str(0, id.ToString())
           .Str(1, type)
           .Str(2, status)
           .Ts(3, start)
           .Ts(4, end)
           .Str(5, sourceStage?.ToString())
           .Str(6, targetStage?.ToString())
           .Str(7, performedBy)
           .EndRow();
    }

    protected override Schema Columns => OperationColumns;

    protected override void Fill(FabricRowBuilder row, string?[] args, CancellationToken ct)
    {
        var pipeline = Api.ResolvePipeline(args[0], ct);
        foreach (var op in FabricApiClient.WrapList("deployment_operations",
                     () => Api.Client.Core.DeploymentPipelines.ListDeploymentPipelineOperations(
                         pipeline, cancellationToken: ct)))
        {
            OperationRow(row, op.Id, op.Type.ToString(), op.Status.ToString(), op.ExecutionStartTime,
                         op.ExecutionEndTime, op.SourceStageId, op.TargetStageId, op.PerformedBy?.DisplayName);
        }
    }
}
