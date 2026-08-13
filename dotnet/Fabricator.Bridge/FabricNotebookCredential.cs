using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace Fabricator.Bridge;

/// <summary>
/// Ambient credential for Microsoft Fabric notebook / Spark compute: mints Entra tokens for the session's
/// executing identity (the interactive user, or the submitting SP / workspace identity for pipeline and
/// on-demand-job runs) from the Fabric token service — the same local service
/// <c>notebookutils.credentials.getToken</c> fronts. This is what makes ambient auth work where
/// <c>DefaultAzureCredential</c> cannot: Fabric compute has NO IMDS endpoint and no <c>AZURE_*</c> env
/// credentials (every chain link fails — validated live 2026-07-14).
///
/// Protocol (captured from <c>notebookutils.common.token_utils</c>/<c>configs</c> on the Fabric runtime):
/// <code>
///   GET {AZURE_FABRIC_TOKEN_SERVICE_URL}?resource={resource-without-/.default}
///   x-ms-partner-token:       sessionToken   (/opt/token-service/tokenservice.config.json)
///   x-ms-cluster-identifier:  AZURE_FABRIC_CLUSTER_IDENTIFIER / config clusterName
///   x-ms-proxy-host:          https://&lt;host of trident.lakehouse.tokenservice.endpoint&gt;
///   x-ms-client-tenant-id:    tid claim of the session token
///   → response body = the raw AAD access token
/// </code>
/// Tokens are cached per resource and re-minted 5 minutes before expiry, so callers get REFRESHING ambient
/// auth (unlike a raw pasted access token). This is an UNDOCUMENTED internal protocol — engaged only when
/// <see cref="IsAvailable"/> (the env vars/config exist solely on Fabric compute), and consumers fall back
/// to <c>DefaultAzureCredential</c> everywhere else (see <c>FabricCredentialResolver.AmbientChain</c>).
/// </summary>
public sealed class FabricNotebookCredential : TokenCredential
{
    private const string TokenServiceUrlEnv = "AZURE_FABRIC_TOKEN_SERVICE_URL";
    private const string ClusterIdEnv = "AZURE_FABRIC_CLUSTER_IDENTIFIER";
    private const string ConfigPath = "/opt/token-service/tokenservice.config.json";
    private const string TridentContextPath = "/home/trusted-service-user/.trident-context";
    // notebookutils' configs.get(key) mirrors spark-conf keys into MSNOTEBOOKUTILS_<KEY with . -> _> env vars.
    private const string WorkloadEndpointEnv = "MSNOTEBOOKUTILS_TRIDENT_LAKEHOUSE_TOKENSERVICE_ENDPOINT";
    private const string SessionTokenEnv = "MSNOTEBOOKUTILS_TRIDENT_SESSION_TOKEN";
    // The spark conf key the env var above MIRRORS. Present in .trident-context on Spark compute, where
    // the env var is not — see IsAvailable.
    private const string SessionTokenConfKey = "trident.session.token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AccessToken> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True on Fabric notebook/Spark compute — we can name the token service AND hold a session
    /// token to authenticate to it.
    /// <para>⚠ THIS USED TO TEST TWO ENV VARS AND THAT MADE IT PYTHON-NOTEBOOK-ONLY. Measured live
    /// 2026-08-13 on a Fabric PySpark session (Spark 4.1.1.5.5, kernel confirmed): <b>all four
    /// <c>AZURE_FABRIC_*</c> / <c>MSNOTEBOOKUTILS_*</c> variables are MISSING</b>, and
    /// <c>notebookutils</c> does not even expose <c>configs</c> there — so `IsAvailable` was false, the
    /// caller fell through to <c>DefaultAzureCredential</c>, and a secretless `abfss://` ATTACH died with
    /// its whole "no credential source" chain. The env vars are a PYTHON-runtime mirror, not the
    /// platform's own contract.</para>
    /// <para>Everything needed is present on Spark, in the two files this class ALREADY reads: the same
    /// <c>trident.session.token</c> the env var mirrors lives in <c>.trident-context</c>, and the token
    /// service endpoint is in <c>tokenservice.config.json</c>. So this asks the QUESTION (can I name the
    /// service, do I hold a session token?) instead of testing one accidental spelling of the answer.</para>
    /// <para>⚠ The config file's OWN <c>sessionToken</c> is deliberately NOT accepted here, only in
    /// <see cref="ReadSessionToken"/> as a last resort. That is MEASURED, not deference to the older
    /// comment that claimed it: minting with it against the live service returns <b>401
    /// SignedPayloadValidationException</b> while the <c>.trident-context</c> token returns 200, in the
    /// same session, seconds apart. Letting it satisfy availability would flip the selection and then fail
    /// at MINT time, trading a clear "no credential" error for a confusing one.</para></summary>
    public static bool IsAvailable =>
        !string.IsNullOrEmpty(TokenServiceUrl()) &&
        (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SessionTokenEnv)) ||
         !string.IsNullOrEmpty(TridentConfValue(SessionTokenConfKey)));

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var resource = ResourceFromScopes(requestContext.Scopes);
        if (_cache.TryGetValue(resource, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow + RefreshBuffer)
        {
            return cached;
        }
        var token = await MintAsync(resource, cancellationToken).ConfigureAwait(false);
        _cache[resource] = token;
        return token;
    }

    private static string ResourceFromScopes(string[] scopes)
    {
        // Token Management takes a bare resource, not a scope: strip the /.default suffix (per
        // notebookutils' augment_resource_param).
        var scope = scopes is { Length: > 0 } ? scopes[0] : FabricCredentialResolver.StorageScope;
        const string suffix = "/.default";
        return scope.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? scope.Substring(0, scope.Length - suffix.Length)
            : scope;
    }

    private static async Task<AccessToken> MintAsync(string resource, CancellationToken ct)
    {
        var baseUrl = TokenServiceUrl()
            ?? throw new InvalidOperationException(
                "FabricNotebookCredential: no token service endpoint (neither AZURE_FABRIC_TOKEN_SERVICE_URL "
                + "nor tokenServiceEndpoint in " + ConfigPath + ")");
        var sessionToken = ReadSessionToken()
            ?? throw new InvalidOperationException("FabricNotebookCredential: no Fabric session token found");

        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "?resource=" + Uri.EscapeDataString(resource));
        req.Headers.TryAddWithoutValidation("x-ms-client-request-id", Guid.NewGuid().ToString());
        req.Headers.TryAddWithoutValidation("x-ms-customer-correlation-id", Guid.NewGuid().ToString());
        req.Headers.TryAddWithoutValidation("x-ms-partner-token", sessionToken);
        var clusterId = Environment.GetEnvironmentVariable(ClusterIdEnv) ?? ReadConfigValue("clusterName");
        if (!string.IsNullOrEmpty(clusterId))
        {
            req.Headers.TryAddWithoutValidation("x-ms-cluster-identifier", clusterId);
        }
        var workloadHost = WorkloadHost();
        if (!string.IsNullOrEmpty(workloadHost))
        {
            req.Headers.TryAddWithoutValidation("x-ms-proxy-host", "https://" + workloadHost);
        }
        var tenantId = JwtClaim(sessionToken, "tid");
        if (!string.IsNullOrEmpty(tenantId))
        {
            req.Headers.TryAddWithoutValidation("x-ms-client-tenant-id", tenantId);
        }
        var moniker = TridentConfValue("trident.artifact.id");
        if (!string.IsNullOrEmpty(moniker))
        {
            req.Headers.TryAddWithoutValidation("x-ms-workload-resource-moniker", moniker);
        }
        req.Headers.TryAddWithoutValidation("User-Agent", "Fabricator");

        // ALWAYS the async transport: the sync HttpClient path hangs under the hostfxr-hosted CLR.
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                $"FabricNotebookCredential: token service returned {(int)resp.StatusCode} for resource '{resource}': " +
                body.Substring(0, Math.Min(body.Length, 400)));
        }
        var expiry = JwtClaim(body, "exp") is { } exp && long.TryParse(exp, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow.AddMinutes(50);
        return new AccessToken(body.Trim(), expiry);
    }

    // The spark-conf session token (trident.session.token, mirrored env) is the one the token service
    // validates; the tokenservice.config.json sessionToken is a DIFFERENT (cluster-level) token that fails
    // with SignedPayloadValidationException — proven by request ablation on the live runtime (2026-07-14).
    /// <summary>The session token presented to the token service as <c>x-ms-partner-token</c>.
    /// Python compute mirrors it into an env var; SPARK compute does not, and carries the same value under
    /// its spark-conf key in <c>.trident-context</c> (measured — see <see cref="IsAvailable"/>). The
    /// config file's own token is a LAST resort and does not satisfy availability.</summary>
    private static string? ReadSessionToken()
        => Environment.GetEnvironmentVariable(SessionTokenEnv)
           ?? TridentConfValue(SessionTokenConfKey)
           ?? ReadConfigValue("sessionToken");

    /// <summary>Where to mint tokens. The env var exists on PYTHON compute only; on Spark it is composed
    /// from the two files — see <see cref="FabricTokenServiceUrl"/> for why the config file's
    /// <c>tokenServiceEndpoint</c> is NOT the endpoint (it is the bare origin, and using it directly 404s).
    /// MEASURED live 2026-08-13 on PySpark: the composed URL is byte-identical to the env var a Python
    /// kernel on the same capacity carries, and mints 200 for the storage, Fabric and SQL audiences.</summary>
    private static string? TokenServiceUrl()
        => Environment.GetEnvironmentVariable(TokenServiceUrlEnv)
           ?? FabricTokenServiceUrl.Compose(
                  ReadConfigValue("tokenServiceEndpoint"),
                  Environment.GetEnvironmentVariable(WorkloadEndpointEnv)
                      ?? TridentConfValue("trident.lakehouse.tokenservice.endpoint"));

    private static string? ReadConfigValue(string property)
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // The MWC/workload host for x-ms-proxy-host: hostname of the trident.lakehouse.tokenservice.endpoint
    // spark conf (e.g. <capacity>.pbidedicated.windows.net) — from the mirrored env var, else the
    // .trident-context spark-conf file.
    private static string? WorkloadHost()
    {
        var endpoint = Environment.GetEnvironmentVariable(WorkloadEndpointEnv)
                       ?? TridentConfValue("trident.lakehouse.tokenservice.endpoint");
        if (string.IsNullOrEmpty(endpoint))
        {
            return null;
        }
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    // .trident-context is a spark-conf-style properties file ("key value" / "key=value" lines).
    private static string? TridentConfValue(string key)
    {
        try
        {
            if (!File.Exists(TridentContextPath))
            {
                return null;
            }
            foreach (var line in File.ReadLines(TridentContextPath))
            {
                var t = line.Trim();
                if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var rest = t.Substring(key.Length).TrimStart('=', ' ', '\t');
                if (rest.Length > 0)
                {
                    return rest;
                }
            }
        }
        catch
        {
            // best-effort — header omitted
        }
        return null;
    }

    private static string? JwtClaim(string token, string claim)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty(claim, out var v)
                ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
