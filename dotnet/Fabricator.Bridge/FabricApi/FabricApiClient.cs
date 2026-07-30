using System;
using System.Collections.Concurrent;
using System.Threading;
using Azure;
using Azure.Core;
using Microsoft.Fabric.Api;

namespace Fabricator.Bridge;

/// <summary>
/// What a catalog-bound Fabric-API function inherits from its ATTACH: the OneLake root it was attached at, and
/// the credential resolved from the ATTACH secret (null ⇒ fall back to the ambient chain, which is how a
/// credential-free ATTACH works on Fabric compute).
/// </summary>
/// <remarks>
/// This is the whole reason these functions are catalog-bound rather than global: dbt runs OFF Fabric compute,
/// where the ambient chain finds nothing, and a GLOBAL function has no route to a DuckDB secret (secrets are
/// resolved host-side, and the host-FS opener covers storage only — not REST). The attach already carries the
/// credential, so <c>lake.dbo.fabric_refresh_sql_endpoint()</c> needs no arguments at all.
/// </remarks>
internal sealed record FabricApiContext(string Root, TokenCredential? Credential);

/// <summary>
/// Thin wrapper over <see cref="FabricClient"/>: credential handling, name-or-GUID resolution with a cache, and
/// error normalization. Everything below it is the SDK (<c>Microsoft.Fabric.Api</c>, already a Bridge dependency
/// — see docs/fabric-api-functions.md §2).
/// </summary>
/// <remarks>
/// <para>ONE call is deliberately raw HTTP rather than SDK: a notebook run's <c>exitValue</c>. It is absent from
/// <c>ItemJobInstance</c> in 2.14.0 AND 2.18.0 (both byte-probed), so no package bump avoids it.</para>
/// <para>Resolution results are cached per instance, and the instance lives on the catalog: workspace/item
/// listing is throttled per principal, and every function call would otherwise re-list.</para>
/// </remarks>
internal sealed class FabricApiClient
{
    private readonly FabricApiContext _context;
    private readonly Lazy<FabricClient> _client;
    private readonly ConcurrentDictionary<string, Guid> _idCache = new(StringComparer.OrdinalIgnoreCase);

    internal FabricApiClient(FabricApiContext context)
    {
        _context = context;
        // Lazy: constructing the client is cheap but resolving the ambient credential chain is not, and a
        // catalog that never calls a Fabric function must not pay for it at ATTACH.
        _client = new Lazy<FabricClient>(
            () => new FabricClient(_context.Credential ?? FabricCredentialResolver.AmbientChain()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal FabricClient Client => _client.Value;

    /// <summary>The workspace named by the ATTACH root, as a GUID.</summary>
    internal Guid WorkspaceId => ResolveWorkspace(null);

    /// <summary>The lakehouse item named by the ATTACH root, as a GUID.</summary>
    internal Guid ItemId => ResolveItem(null, "Lakehouse");

    /// <summary>
    /// Resolves a workspace by GUID or display name; <paramref name="nameOrId"/> null/empty ⇒ the one the ATTACH
    /// root names (parsed by <see cref="FabricLakehouse.ParseOneLake"/>, which yields either a name or a GUID
    /// depending on how the user wrote the abfss URI).
    /// </summary>
    internal Guid ResolveWorkspace(string? nameOrId)
    {
        var wanted = Blank(nameOrId) ? FabricLakehouse.ParseOneLake(_context.Root).Workspace : nameOrId!;
        if (Blank(wanted))
        {
            throw new NotSupportedException(
                "fabric: could not determine the workspace from the ATTACH root — pass it explicitly.");
        }
        if (Guid.TryParse(wanted, out var direct))
        {
            return direct;
        }
        return _idCache.GetOrAdd("ws:" + wanted, _ =>
        {
            foreach (var w in Client.Core.Workspaces.ListWorkspaces())
            {
                // Trailing spaces in portal-entered display names are real (CLAUDE.md records one), so trim.
                if (string.Equals(w.DisplayName?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return w.Id;
                }
            }
            throw new NotSupportedException($"fabric: workspace '{wanted}' not found (or not visible to this identity).");
        });
    }

    /// <summary>
    /// Resolves an item by GUID or display name within the ATTACH's workspace; null/empty ⇒ the item the root
    /// names. <paramref name="itemType"/> is the Fabric item type to filter by (e.g. <c>Lakehouse</c>,
    /// <c>Notebook</c>).
    /// </summary>
    internal Guid ResolveItem(string? nameOrId, string itemType)
    {
        var wanted = nameOrId;
        if (Blank(wanted))
        {
            // The root's item segment is "<name>.Lakehouse" or a bare GUID — ResolveLakehouseId strips the suffix.
            wanted = FabricLakehouse.ParseOneLake(_context.Root).Lakehouse;
        }
        if (Blank(wanted))
        {
            throw new NotSupportedException(
                $"fabric: could not determine the {itemType} from the ATTACH root — pass it explicitly.");
        }
        var trimmed = wanted!.EndsWith("." + itemType, StringComparison.OrdinalIgnoreCase)
            ? wanted[..^(itemType.Length + 1)]
            : wanted;
        if (Guid.TryParse(trimmed, out var direct))
        {
            return direct;
        }
        var ws = ResolveWorkspace(null);
        return _idCache.GetOrAdd($"item:{ws}:{itemType}:{trimmed}", _ =>
        {
            foreach (var i in Client.Core.Items.ListItems(ws, type: itemType))
            {
                if (string.Equals(i.DisplayName?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return i.Id ?? Guid.Empty;
                }
            }
            throw new NotSupportedException($"fabric: {itemType} '{trimmed}' not found in workspace {ws}.");
        });
    }

    /// <summary>
    /// The lakehouse's SQL analytics endpoint id. Note the SDK models this as a <b>string</b> while
    /// <c>RefreshSqlEndpointMetadata</c> takes a <b>Guid</b>, so it must be parsed — <c>?? Guid.Empty</c> does
    /// not even compile against it.
    /// </summary>
    internal Guid ResolveSqlEndpointId(Guid workspaceId, Guid lakehouseId)
    {
        return _idCache.GetOrAdd($"sqlep:{workspaceId}:{lakehouseId}", _ =>
        {
            var props = Client.Lakehouse.Items.GetLakehouse(workspaceId, lakehouseId).Value.Properties;
            var ep = props?.SqlEndpointProperties;
            if (ep is null || !Guid.TryParse(ep.Id, out var parsed))
            {
                throw new NotSupportedException(
                    "fabric: this lakehouse reports no SQL analytics endpoint id "
                    + $"(provisioning status: {ep?.ProvisioningStatus.ToString() ?? "unknown"}).");
            }
            return parsed;
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/>, converting an Azure <see cref="RequestFailedException"/> into a message that
    /// leads with Fabric's own error code — the same reading experience as the provider-error-number prefixing the
    /// SQL backend does. The service's codes are the actionable part (<c>PrincipalTypeNotSupported</c>,
    /// <c>EntityConflict</c>, <c>FeatureNotAvailable</c>), and the raw exception buries them in a wall of text.
    /// </summary>
    internal static T Wrap<T>(string what, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (RequestFailedException ex)
        {
            throw new NotSupportedException($"fabric {what}: {Describe(ex)}", ex);
        }
        catch (NotSupportedException)
        {
            // Already ours (an argument-validation message) — do not re-wrap it into noise.
            throw;
        }
        catch (Exception ex)
        {
            // An exception that is NOT a service error came from our own marshaling or the SDK's client-side
            // validation, and its bare message is usually unlocatable ("Value cannot be null. (Parameter
            // 'value')"). The ABI only carries a string to the host, so the frame list has to ride in it or it
            // is lost entirely.
            throw new NotSupportedException(
                $"fabric {what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
        }
    }

    internal static void Wrap(string what, Action body) => Wrap(what, () => { body(); return 0; });

    private static string Describe(RequestFailedException ex)
    {
        string code = ex.ErrorCode ?? "";
        string message = "";
        string requestId = "";
        try
        {
            var content = ex.GetRawResponse()?.Content?.ToString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (Blank(code) && root.TryGetProperty("errorCode", out var c)) { code = c.GetString() ?? ""; }
                if (root.TryGetProperty("message", out var m)) { message = m.GetString() ?? ""; }
                if (root.TryGetProperty("requestId", out var r)) { requestId = r.GetString() ?? ""; }
                // moreDetails carries the specific reason; the top-level message is often generic
                // ("The request could not be completed due to a conflict with an existing resource").
                if (root.TryGetProperty("moreDetails", out var more)
                    && more.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var d in more.EnumerateArray())
                    {
                        if (d.TryGetProperty("message", out var dm) && !Blank(dm.GetString()))
                        {
                            message = $"{message} — {dm.GetString()}";
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Diagnostics must never mask the original failure; fall through to the status line.
        }
        if (Blank(message)) { message = ex.Message?.Split('\n')[0] ?? "request failed"; }
        var prefix = Blank(code) ? $"HTTP {ex.Status}" : code;
        return Blank(requestId) ? $"{prefix}: {message}" : $"{prefix}: {message} (requestId {requestId})";
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
