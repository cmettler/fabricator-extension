using System;
using System.Text.Json;
using Microsoft.Fabric.Api.Core.Models;

namespace Fabricator.Bridge;

/// <summary>
/// Maps a shortcut target between the REST JSON union and the SDK model, in both directions:
/// JSON → <see cref="CreatableShortcutTarget"/> for <c>create_shortcut_json</c>, and
/// <see cref="Target"/> → flat columns + raw JSON for <c>list_shortcuts</c>.
/// </summary>
/// <remarks>
/// Hand-mapped rather than fed to <c>JsonSerializer</c>: the SDK models are Azure.Core-generated with
/// non-public deserializers, and their ctors take <c>Uri</c> + a REQUIRED <c>Guid connectionId</c> — so a
/// missing or malformed <c>connectionId</c> has to become a clear error rather than a
/// <c>Guid.Empty</c> the service rejects opaquely.
/// </remarks>
internal static class FabricShortcutTarget
{
    /// <summary>The subset of target fields that is common enough across union members to be worth a column.</summary>
    internal readonly record struct Flat(string? Location, string? Subpath, string? ConnectionId);

    /// <summary>
    /// Parses one REST <c>target</c> object, e.g.
    /// <c>{"adlsGen2":{"location":"https://a.dfs.core.windows.net","subpath":"/c/d","connectionId":"…"}}</c>.
    /// Exactly one member must be present — the service enforces that too, but failing here names the problem.
    /// </summary>
    internal static CreatableShortcutTarget FromJson(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new NotSupportedException($"fabric shortcut: target_json is not valid JSON — {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new NotSupportedException("fabric shortcut: target_json must be a JSON object.");
        }
        // Tolerate a wrapper: both the bare target object and {"target": {...}} read naturally in SQL.
        if (root.TryGetProperty("target", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            root = inner;
        }

        var target = new CreatableShortcutTarget();
        int found = 0;
        foreach (var member in root.EnumerateObject())
        {
            // "type" is echoed by the service on reads; ignore it on writes rather than rejecting a
            // round-tripped object.
            if (string.Equals(member.Name, "type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var v = member.Value;
            switch (member.Name.ToLowerInvariant())
            {
                case "onelake":
                    target.OneLake = new OneLake(
                        itemId: Guid(v, "itemId"), workspaceId: Guid(v, "workspaceId"), path: Str(v, "path")!);
                    break;
                case "adlsgen2":
                    target.AdlsGen2 = new AdlsGen2(Uri(v, "location"), Str(v, "subpath")!, Guid(v, "connectionId"));
                    break;
                case "amazons3":
                    target.AmazonS3 = new AmazonS3(Uri(v, "location"), Guid(v, "connectionId"))
                    {
                        Subpath = Str(v, "subpath"),
                    };
                    break;
                case "azureblobstorage":
                    target.AzureBlobStorage = new AzureBlobStorage(
                        Uri(v, "location"), Str(v, "subpath")!, Guid(v, "connectionId"));
                    break;
                case "googlecloudstorage":
                    target.GoogleCloudStorage = new GoogleCloudStorage(
                        Uri(v, "location"), Str(v, "subpath")!, Guid(v, "connectionId"));
                    break;
                case "s3compatible":
                    target.S3Compatible = new S3Compatible(
                        Uri(v, "location"), Str(v, "subpath")!, Str(v, "bucket")!, Guid(v, "connectionId"));
                    break;
                case "dataverse":
                    target.Dataverse = new Dataverse(
                        Uri(v, "environmentDomain"), Guid(v, "connectionId"), Str(v, "deltaLakeFolder")!,
                        Str(v, "tableName")!);
                    break;
                case "onedrivesharepoint":
                    target.OneDriveSharePoint = new OneDriveSharePoint(
                        Uri(v, "location"), Str(v, "subpath")!, Guid(v, "connectionId"));
                    break;
                default:
                    throw new NotSupportedException(
                        $"fabric shortcut: unknown target type '{member.Name}'. Supported: oneLake, adlsGen2, "
                        + "amazonS3, azureBlobStorage, googleCloudStorage, s3Compatible, dataverse, oneDriveSharePoint.");
            }
            found++;
        }
        if (found != 1)
        {
            throw new NotSupportedException(
                $"fabric shortcut: target_json must name exactly one target type (found {found}).");
        }
        return target;
    }

    /// <summary>The flattened common fields of a returned target (all null for a OneLake target).</summary>
    internal static Flat Flatten(Target? t)
    {
        if (t is null)
        {
            return default;
        }
        if (t.AdlsGen2 is { } a) { return new Flat(a.Location?.ToString(), a.Subpath, a.ConnectionId.ToString()); }
        if (t.AmazonS3 is { } s3) { return new Flat(s3.Location?.ToString(), s3.Subpath, s3.ConnectionId.ToString()); }
        if (t.AzureBlobStorage is { } b) { return new Flat(b.Location?.ToString(), b.Subpath, b.ConnectionId.ToString()); }
        if (t.GoogleCloudStorage is { } g) { return new Flat(g.Location?.ToString(), g.Subpath, g.ConnectionId.ToString()); }
        if (t.S3Compatible is { } sc) { return new Flat(sc.Location?.ToString(), sc.Subpath, sc.ConnectionId.ToString()); }
        if (t.Dataverse is { } d) { return new Flat(d.EnvironmentDomain?.ToString(), d.DeltaLakeFolder, d.ConnectionId.ToString()); }
        if (t.OneDriveSharePoint is { } o) { return new Flat(o.Location?.ToString(), o.Subpath, o.ConnectionId.ToString()); }
        if (t.ExternalDataShare is { } e) { return new Flat(null, null, e.ConnectionId.ToString()); }
        if (t.OneLake is { } ol) { return new Flat(null, ol.Path, ol.ConnectionId?.ToString()); }
        return default;
    }

    /// <summary>
    /// The target re-serialized as the REST-shaped JSON object — the full-fidelity escape hatch for whatever the
    /// flat columns do not cover (including target types added after this code was written).
    /// </summary>
    internal static string? ToJson(Target? t)
    {
        if (t is null)
        {
            return null;
        }
        var sw = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(sw))
        {
            w.WriteStartObject();
            var type = t.Type.ToString();
            if (!string.IsNullOrEmpty(type)) { w.WriteString("type", type); }
            if (t.OneLake is { } ol)
            {
                w.WriteStartObject("oneLake");
                w.WriteString("workspaceId", ol.WorkspaceId);
                w.WriteString("itemId", ol.ItemId);
                if (ol.Path is not null) { w.WriteString("path", ol.Path); }
                if (ol.ConnectionId is { } c) { w.WriteString("connectionId", c); }
                w.WriteEndObject();
            }
            WriteExternal(w, "adlsGen2", t.AdlsGen2?.Location, t.AdlsGen2?.Subpath, t.AdlsGen2?.ConnectionId);
            WriteExternal(w, "amazonS3", t.AmazonS3?.Location, t.AmazonS3?.Subpath, t.AmazonS3?.ConnectionId);
            WriteExternal(w, "azureBlobStorage", t.AzureBlobStorage?.Location, t.AzureBlobStorage?.Subpath,
                          t.AzureBlobStorage?.ConnectionId);
            WriteExternal(w, "googleCloudStorage", t.GoogleCloudStorage?.Location, t.GoogleCloudStorage?.Subpath,
                          t.GoogleCloudStorage?.ConnectionId);
            WriteExternal(w, "s3Compatible", t.S3Compatible?.Location, t.S3Compatible?.Subpath,
                          t.S3Compatible?.ConnectionId, ("bucket", t.S3Compatible?.Bucket));
            WriteExternal(w, "oneDriveSharePoint", t.OneDriveSharePoint?.Location, t.OneDriveSharePoint?.Subpath,
                          t.OneDriveSharePoint?.ConnectionId);
            if (t.Dataverse is { } dv)
            {
                w.WriteStartObject("dataverse");
                if (dv.EnvironmentDomain is not null) { w.WriteString("environmentDomain", dv.EnvironmentDomain.ToString()); }
                if (dv.DeltaLakeFolder is not null) { w.WriteString("deltaLakeFolder", dv.DeltaLakeFolder); }
                if (dv.TableName is not null) { w.WriteString("tableName", dv.TableName); }
                w.WriteString("connectionId", dv.ConnectionId);
                w.WriteEndObject();
            }
            if (t.ExternalDataShare is { } eds)
            {
                w.WriteStartObject("externalDataShare");
                w.WriteString("connectionId", eds.ConnectionId);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(sw.ToArray());
    }

    private static void WriteExternal(
        Utf8JsonWriter w, string member, Uri? location, string? subpath, Guid? connectionId,
        (string Name, string? Value) extra = default)
    {
        if (location is null && subpath is null && connectionId is null)
        {
            return;
        }
        w.WriteStartObject(member);
        if (location is not null) { w.WriteString("location", location.ToString()); }
        if (extra.Name is not null && extra.Value is not null) { w.WriteString(extra.Name, extra.Value); }
        if (subpath is not null) { w.WriteString("subpath", subpath); }
        if (connectionId is { } c) { w.WriteString("connectionId", c); }
        w.WriteEndObject();
    }

    // ---- field readers that fail with the FIELD NAME, not a generic cast error ----------------------

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Uri Uri(JsonElement o, string name)
    {
        var s = Str(o, name) ?? throw new NotSupportedException($"fabric shortcut: target is missing '{name}'.");
        return System.Uri.TryCreate(s, UriKind.Absolute, out var u)
            ? u
            : throw new NotSupportedException($"fabric shortcut: target '{name}' is not an absolute URI: {s}");
    }

    private static Guid Guid(JsonElement o, string name)
    {
        var s = Str(o, name) ?? throw new NotSupportedException(
            $"fabric shortcut: target is missing '{name}'"
            + (name == "connectionId"
                ? " — an external target needs a pre-provisioned cloud connection; list them with connections()."
                : "."));
        return System.Guid.TryParse(s, out var g)
            ? g
            : throw new NotSupportedException($"fabric shortcut: target '{name}' is not a GUID: {s}");
    }
}

/// <summary>Shortcut path normalization + conflict-policy parsing.</summary>
internal static class FabricShortcutPath
{
    /// <summary>
    /// Drops surrounding slashes. Needed because the service RETURNS <c>/Files/staging</c> but ACCEPTS
    /// <c>Files</c> — verified live — so without this, piping <c>list_shortcuts</c> into
    /// <c>drop_shortcut</c> would 404.
    /// </summary>
    internal static string Strip(string? path) => (path ?? string.Empty).Trim('/');

    internal static string Join(string? path, string? name)
    {
        var p = Strip(path);
        return string.IsNullOrEmpty(p) ? (name ?? string.Empty) : $"{p}/{name}";
    }

    internal static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Parses the four documented conflict policies, case-insensitively. Unknown values throw with the list —
    /// silently defaulting would turn a typo into an unintended overwrite.
    /// </summary>
    internal static ShortcutConflictPolicy? ParsePolicy(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        return s.Trim().ToLowerInvariant() switch
        {
            "abort" => ShortcutConflictPolicy.Abort,
            "generateuniquename" => ShortcutConflictPolicy.GenerateUniqueName,
            "createoroverwrite" => ShortcutConflictPolicy.CreateOrOverwrite,
            "overwriteonly" => ShortcutConflictPolicy.OverwriteOnly,
            _ => throw new NotSupportedException(
                $"fabric shortcut: unknown conflict_policy '{s}'. Use Abort, GenerateUniqueName, "
                + "CreateOrOverwrite or OverwriteOnly."),
        };
    }
}
