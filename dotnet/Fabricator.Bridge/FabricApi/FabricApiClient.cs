using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Resolves an item by GUID or display name; null/empty ⇒ the item the ATTACH root names.
    /// <paramref name="itemType"/> is the Fabric item type to filter by (e.g. <c>Lakehouse</c>,
    /// <c>Notebook</c>), and <paramref name="workspaceId"/> the workspace to look in — pass it when the caller
    /// overrode the workspace, or a cross-workspace lookup would search the ATTACH's own.
    /// </summary>
    internal Guid ResolveItem(string? nameOrId, string itemType, Guid? workspaceId = null,
                              bool requireType = true)
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
        var ws = workspaceId ?? ResolveWorkspace(null);
        // requireType=false searches EVERY item type: the generic job functions take an item of any kind, and
        // filtering by a guessed type would hide the item the caller actually named.
        var typeFilter = requireType ? itemType : null;
        return _idCache.GetOrAdd($"item:{ws}:{typeFilter ?? "*"}:{trimmed}", _ =>
        {
            foreach (var i in Client.Core.Items.ListItems(ws, type: typeFilter))
            {
                if (string.Equals(i.DisplayName?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return i.Id ?? Guid.Empty;
                }
            }
            throw new NotSupportedException(
                $"fabric: {(requireType ? itemType : "item")} '{trimmed}' not found in workspace {ws}.");
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

    /// <summary>
    /// Runs a PAGED read and MATERIALIZES it inside the guard.
    /// </summary>
    /// <remarks>
    /// <c>Wrap</c> alone is not enough for a pageable response: the SDK's <c>PageableResponse&lt;T&gt;</c> is
    /// lazy, so the request happens during enumeration — OUTSIDE the try — and its exception escapes
    /// unformatted, reaching the user as a raw Azure dump complete with a header list. Materializing here also
    /// bounds the request to the guard's lifetime rather than the consumer's. Measured: a
    /// schema-enabled-lakehouse ListTables refusal surfaced that way before this existed.
    /// </remarks>
    internal static List<T> WrapList<T>(string what, Func<IEnumerable<T>> body) =>
        Wrap(what, () =>
        {
            var rows = new List<T>();
            foreach (var row in body())
            {
                rows.Add(row);
            }
            return rows;
        });

    // ---- notebook runs: raw HTTP, of necessity -------------------------------------------------------
    //
    // The SDK's RunOnDemandItemJob returns a bare Response (the instance id is only in the Location
    // header) and its ItemJobInstance model carries no exitValue in 2.14.0 OR 2.18.0. The exit value and
    // the monitoring links live at `properties.*` on the NOTEBOOK-scoped instance GET, which the SDK does
    // not project — so this one flow is hand-rolled. Everything else goes through the SDK.

    /// <summary>
    /// What a finished (or still-running) item job reports. <c>ExitValue</c>/<c>Compute</c>/<c>SnapshotUrl</c>
    /// are notebook-only (they come from the notebook-scoped instance's <c>properties</c>) and stay null for
    /// every other job type.
    /// </summary>
    internal sealed record JobRunState(
        string Status, string? StartTimeUtc, string? EndTimeUtc, string? ExitValue, string? Compute,
        string? SnapshotUrl, string? ErrorCode, string? ErrorMessage);

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private async System.Threading.Tasks.Task<string> TokenAsync(CancellationToken ct)
    {
        var cred = _context.Credential ?? FabricCredentialResolver.AmbientChain();
        // Always the ASYNC token path: the sync one deadlocks under the hostfxr-hosted CLR (the same rule
        // FabricNotebookCredential documents for its own transport).
        var token = await cred.GetTokenAsync(
            new TokenRequestContext(new[] { "https://api.fabric.microsoft.com/.default" }), ct)
            .ConfigureAwait(false);
        return token.Token;
    }

    private async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpMethod method, string url, string? json, CancellationToken ct)
    {
        using var req = new System.Net.Http.HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await TokenAsync(ct).ConfigureAwait(false));
        if (json is not null)
        {
            req.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits an on-demand item job of <paramref name="jobType"/> and returns its instance id, which is only
    /// available in the <c>Location</c> header (the 202 has no body).
    /// </summary>
    internal async System.Threading.Tasks.Task<string> SubmitItemJobAsync(
        Guid workspaceId, Guid itemId, string jobType, string? body, CancellationToken ct)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}"
                  + $"/jobs/{jobType}/instances";
        using var resp = await SendAsync(System.Net.Http.HttpMethod.Post, url, body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)resp.StatusCode != 202)
        {
            throw new NotSupportedException($"fabric run_job({jobType}): HTTP {(int)resp.StatusCode}: {Trim(text)}");
        }
        var location = resp.Headers.Location?.ToString() ?? string.Empty;
        var id = location.Split('/')[^1].Split('?')[0];
        if (!Guid.TryParse(id, out _))
        {
            throw new NotSupportedException(
                $"fabric run_job({jobType}): accepted but no job-instance id in the Location header ('{location}').");
        }
        return id;
    }

    /// <summary>
    /// Polls until the job reaches a terminal state or <paramref name="waitSeconds"/> elapses (0 = return
    /// immediately after submission). Reads the NOTEBOOK-scoped instance URL, the only one that returns the
    /// <c>properties</c> object holding exitValue + the monitoring links.
    /// </summary>
    internal async System.Threading.Tasks.Task<JobRunState> PollNotebookRunAsync(
        Guid workspaceId, Guid notebookId, string instanceId, long waitSeconds, CancellationToken ct)
    {
        var polled = await PollItemJobAsync(workspaceId, notebookId, instanceId, waitSeconds, ct)
            .ConfigureAwait(false);
        var notebookUrl = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/notebooks/{notebookId}"
                          + $"/jobs/execute/instances/{instanceId}";
        // Best-effort enrichment: never fail a completed run because the extended record is not there yet.
        return await EnrichAsync(polled, notebookUrl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls any item job's ITEMS-scoped instance until terminal or <paramref name="waitSeconds"/> elapses
    /// (0 = read once and return).
    /// </summary>
    internal async System.Threading.Tasks.Task<JobRunState> PollItemJobAsync(
        Guid workspaceId, Guid itemId, string instanceId, long waitSeconds, CancellationToken ct)
    {
        // The ITEMS-scoped instance is what we poll because it exists as soon as the job is ACCEPTED. The
        // notebook-scoped variant (the only source of `properties` — exitValue + monitoring links) is
        // populated LATER: reading it straight after submission 404s with ItemNotFound / "No notebook
        // execution state found in database for the runId", a timing artefact rather than a missing item.
        var itemsUrl = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}"
                       + $"/jobs/instances/{instanceId}";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds <= 0 ? 0 : waitSeconds);
        JobRunState state;
        while (true)
        {
            state = await ReadInstanceAsync(itemsUrl, ct).ConfigureAwait(false);
            bool running = state.Status is "NotStarted" or "InProgress";
            if (!running || waitSeconds <= 0 || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }
            // 5 s: a notebook run is minutes-scale (a cold Spark session alone is minutes), so polling
            // faster only burns request quota.
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        return state;
    }

    /// <summary>Reads one item-job instance without polling.</summary>
    internal System.Threading.Tasks.Task<JobRunState> GetItemJobAsync(
        Guid workspaceId, Guid itemId, string instanceId, CancellationToken ct) =>
        ReadInstanceAsync(
            $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}"
            + $"/jobs/instances/{instanceId}", ct);

    private async System.Threading.Tasks.Task<JobRunState> ReadInstanceAsync(
        string url, CancellationToken ct)
    {
        using var resp = await SendAsync(System.Net.Http.HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new NotSupportedException($"fabric run_notebook status: HTTP {(int)resp.StatusCode}: {Trim(text)}");
        }
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? errCode = null, errMsg = null;
        if (root.TryGetProperty("failureReason", out var fr)
            && fr.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            errCode = JsonStr(fr, "errorCode");
            errMsg = JsonStr(fr, "message");
        }
        return new JobRunState(
            JsonStr(root, "status") ?? "Unknown", JsonStr(root, "startTimeUtc"), JsonStr(root, "endTimeUtc"),
            ExitValue: null, Compute: null, SnapshotUrl: null, errCode, errMsg);
    }

    /// <summary>
    /// Adds <c>properties.*</c> (exitValue, compute, snapshot link) from the notebook-scoped instance when it
    /// is available. Failure is SWALLOWED on purpose: these are diagnostics, and the extended record is
    /// populated asynchronously — losing them must never turn a successful run into an error.
    /// </summary>
    private async System.Threading.Tasks.Task<JobRunState> EnrichAsync(
        JobRunState state, string notebookUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(System.Net.Http.HttpMethod.Get, notebookUrl, null, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return state;
            }
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("properties", out var props)
                || props.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return state;
            }
            string? snapshot = null;
            if (props.TryGetProperty("computeDetails", out var cd)
                && cd.TryGetProperty("monitoringInfo", out var mi))
            {
                snapshot = JsonStr(mi, "executionSnapshotUrl");
            }
            return state with
            {
                ExitValue = JsonStr(props, "exitValue"),
                Compute = JsonStr(props, "compute"),
                SnapshotUrl = snapshot,
            };
        }
        catch
        {
            return state;
        }
    }

    private static string? JsonStr(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() : null;

    private static string Trim(string s) =>
        s.Replace('\n', ' ').Length > 400 ? s.Replace('\n', ' ')[..400] : s.Replace('\n', ' ');

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
