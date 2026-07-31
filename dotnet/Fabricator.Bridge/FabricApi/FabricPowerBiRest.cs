using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Azure.Core;

namespace Fabricator.Bridge;

/// <summary>
/// The Power BI REST half of <see cref="FabricApiClient"/>: semantic-model listing and enhanced refresh.
/// </summary>
/// <remarks>
/// <para><b>Why a second surface at all.</b> The Fabric SDK cannot refresh a semantic model — probed with a
/// zero control in the pinned 2.14.0 (<c>RefreshSemanticModel</c>, <c>EnhancedRefresh</c>,
/// <c>RefreshSchedule</c> all absent). Refresh only exists on <c>api.powerbi.com</c>.</para>
/// <para><b>What it does NOT need.</b> A different audience: the Power BI API wants a token for
/// <c>analysis.windows.net/powerbi/api</c>, which <see cref="FabricCredentialResolver.PowerBiScope"/> already
/// is — the DAX provider mints exactly this. So the same ATTACH secret, the same ambient notebook token, and
/// (measured on this tenant) the same permissions carry over; only the base URL differs.</para>
/// <para>Raw HTTP because there is no SDK here at all, and because the enhanced-refresh contract is
/// header-driven: the POST answers 202 with the request id in <c>x-ms-request-id</c> (and a <c>Location</c>),
/// and status comes from a separate history read.</para>
/// </remarks>
internal sealed partial class FabricApiClient
{
    private const string PowerBiBase = "https://api.powerbi.com/v1.0/myorg";

    /// <summary>A semantic model as the Power BI surface reports it.</summary>
    internal sealed record DatasetInfo(string? Id, string? Name, bool? IsRefreshable, string? WebUrl);

    /// <summary>One entry of a model's refresh history (also the shape a poll returns).</summary>
    internal sealed record DatasetRefreshInfo(
        string? RequestId, string? RefreshType, string Status, string? ExtendedStatus, string? StartTime,
        string? EndTime, string? ErrorMessage);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _datasetCache =
        new(StringComparer.OrdinalIgnoreCase);

    private async System.Threading.Tasks.Task<string> PowerBiTokenAsync(CancellationToken ct)
    {
        var cred = _context.Credential ?? FabricCredentialResolver.AmbientChain();
        // The SAME scope constant the DAX provider uses. Async only — the sync token path deadlocks under
        // the hostfxr-hosted CLR.
        var token = await cred.GetTokenAsync(
            new TokenRequestContext(new[] { FabricCredentialResolver.PowerBiScope }), ct).ConfigureAwait(false);
        return token.Token;
    }

    private async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> PowerBiSendAsync(
        System.Net.Http.HttpMethod method, string url, string? json, CancellationToken ct)
    {
        using var req = new System.Net.Http.HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await PowerBiTokenAsync(ct).ConfigureAwait(false));
        if (json is not null)
        {
            req.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task<JsonDocument> PowerBiReadAsync(
        System.Net.Http.HttpResponseMessage resp, string what, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // The Power BI surface nests its message under error.{code,message}, unlike Fabric's flat
            // errorCode/message — so lead with whichever is present rather than dumping the body.
            string detail = Trim(text);
            try
            {
                using var err = JsonDocument.Parse(text);
                if (err.RootElement.TryGetProperty("error", out var e))
                {
                    var code = e.TryGetProperty("code", out var c) ? c.GetString() : null;
                    var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message))
                    {
                        detail = $"{code}: {message}";
                    }
                }
            }
            catch
            {
                // Not JSON — the status line plus the trimmed body is the best available.
            }
            throw new NotSupportedException($"powerbi {what}: HTTP {(int)resp.StatusCode}: {detail}");
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>The workspace's semantic models.</summary>
    internal async System.Threading.Tasks.Task<List<DatasetInfo>> ListDatasetsAsync(
        Guid workspaceId, CancellationToken ct)
    {
        using var resp = await PowerBiSendAsync(
            System.Net.Http.HttpMethod.Get, $"{PowerBiBase}/groups/{workspaceId}/datasets", null, ct)
            .ConfigureAwait(false);
        using var doc = await PowerBiReadAsync(resp, "datasets", ct).ConfigureAwait(false);
        var rows = new List<DatasetInfo>();
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in value.EnumerateArray())
            {
                rows.Add(new DatasetInfo(
                    JsonStr(d, "id"), JsonStr(d, "name"),
                    d.TryGetProperty("isRefreshable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? r.GetBoolean() : null,
                    JsonStr(d, "webUrl")));
            }
        }
        return rows;
    }

    /// <summary>
    /// Resolves a semantic model by GUID or NAME. Name resolution is how a lakehouse's or warehouse's DEFAULT
    /// model is reached: it carries the item's own name, and the API exposes no "default model for item X"
    /// link, so the convention is the only handle.
    /// </summary>
    internal async System.Threading.Tasks.Task<Guid> ResolveDatasetAsync(
        Guid workspaceId, string nameOrId, CancellationToken ct)
    {
        if (Guid.TryParse(nameOrId, out var direct))
        {
            return direct;
        }
        var key = $"ds:{workspaceId}:{nameOrId}";
        if (_datasetCache.TryGetValue(key, out var cached))
        {
            return Guid.Parse(cached);
        }
        foreach (var d in await ListDatasetsAsync(workspaceId, ct).ConfigureAwait(false))
        {
            if (string.Equals(d.Name?.Trim(), nameOrId, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(d.Id, out var found))
            {
                _datasetCache[key] = found.ToString();
                return found;
            }
        }
        throw new NotSupportedException(
            $"powerbi: semantic model '{nameOrId}' not found in workspace {workspaceId} "
            + "(list them with fabric_semantic_models()).");
    }

    /// <summary>
    /// Starts an enhanced refresh and returns its request id, which arrives in <c>x-ms-request-id</c> — the
    /// 202 has no body.
    /// </summary>
    internal async System.Threading.Tasks.Task<string> StartDatasetRefreshAsync(
        Guid workspaceId, Guid datasetId, string body, CancellationToken ct)
    {
        using var resp = await PowerBiSendAsync(
            System.Net.Http.HttpMethod.Post, $"{PowerBiBase}/groups/{workspaceId}/datasets/{datasetId}/refreshes",
            body, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            using var _ = await PowerBiReadAsync(resp, "refresh", ct).ConfigureAwait(false); // throws
        }
        if (resp.Headers.TryGetValues("x-ms-request-id", out var ids))
        {
            foreach (var id in ids)
            {
                return id;
            }
        }
        // A Location tail is the documented fallback shape; without either there is nothing to poll on, and
        // saying so beats returning a blank id that fails confusingly later.
        var location = resp.Headers.Location?.ToString();
        if (!string.IsNullOrEmpty(location))
        {
            return location!.Split('/')[^1].Split('?')[0];
        }
        throw new NotSupportedException(
            "powerbi refresh: accepted but reported no request id (no x-ms-request-id, no Location).");
    }

    /// <summary>
    /// Polls the refresh history for <paramref name="requestId"/> until it leaves <c>Unknown</c> (Power BI's
    /// "in progress") or <paramref name="waitSeconds"/> elapses.
    /// </summary>
    internal async System.Threading.Tasks.Task<DatasetRefreshInfo> PollDatasetRefreshAsync(
        Guid workspaceId, Guid datasetId, string requestId, long waitSeconds, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds <= 0 ? 0 : waitSeconds);
        DatasetRefreshInfo? found = null;
        while (true)
        {
            foreach (var r in await ListDatasetRefreshesAsync(workspaceId, datasetId, top: 20, ct)
                         .ConfigureAwait(false))
            {
                if (string.Equals(r.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
                {
                    found = r;
                    break;
                }
            }
            // "Unknown" is Power BI's IN-PROGRESS status, not an error — a refresh reports Completed or Failed
            // when it settles. A request not yet in the history is also still in flight.
            bool running = found is null || string.Equals(found.Status, "Unknown", StringComparison.OrdinalIgnoreCase);
            if (!running || waitSeconds <= 0 || DateTimeOffset.UtcNow >= deadline)
            {
                return found ?? new DatasetRefreshInfo(requestId, null, "Unknown", null, null, null, null);
            }
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>A model's refresh history, newest first.</summary>
    internal async System.Threading.Tasks.Task<List<DatasetRefreshInfo>> ListDatasetRefreshesAsync(
        Guid workspaceId, Guid datasetId, long? top, CancellationToken ct)
    {
        var url = $"{PowerBiBase}/groups/{workspaceId}/datasets/{datasetId}/refreshes";
        if (top is > 0)
        {
            url += $"?$top={top}";
        }
        using var resp = await PowerBiSendAsync(System.Net.Http.HttpMethod.Get, url, null, ct)
            .ConfigureAwait(false);
        using var doc = await PowerBiReadAsync(resp, "refresh_history", ct).ConfigureAwait(false);
        var rows = new List<DatasetRefreshInfo>();
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in value.EnumerateArray())
            {
                string? error = JsonStr(r, "serviceExceptionJson");
                rows.Add(new DatasetRefreshInfo(
                    JsonStr(r, "requestId"), JsonStr(r, "refreshType"), JsonStr(r, "status") ?? "Unknown",
                    JsonStr(r, "extendedStatus"), JsonStr(r, "startTime"), JsonStr(r, "endTime"), error));
            }
        }
        return rows;
    }
}
