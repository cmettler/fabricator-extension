using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// Entra token auth for Fabric / Azure Analysis Services XMLA endpoints. ADOMD has no interactive auth in
/// the CoreCLR host ("interactive authentication is not supported … an external access-token is required"),
/// so we acquire a Power BI-scoped token from the SAME Azure principal the SqlServer warehouse uses and set
/// it as <c>AdomdConnection.AccessToken</c> (mirrors how <c>SqlServerCatalog</c> sets
/// <c>SqlConnection.AccessToken</c>). SqlClient and ADOMD are separate stacks with separate token caches, so
/// they can't share a connection — but they can share the underlying credential.
///
/// <para>The credential is built from a reused <c>azure</c> secret (the same one the warehouse ATTACH uses):
/// service_principal → <see cref="ClientSecretCredential"/>, managed_identity → <see cref="ManagedIdentityCredential"/>,
/// credential_chain / default → <see cref="DefaultAzureCredential"/>. The secret fields are carried from
/// <c>BuildConnectionString</c> (where the host hands us the secret) to the catalog via a connection-string
/// marker, since only the connection string reaches <c>OpenCatalog</c>.</para>
/// </summary>
internal static class DaxTokenAuth
{
    // The Power BI / AAS XMLA token audience. (Fabric + Power BI semantic models use the Power BI resource;
    // AAS-only endpoints accept the same.) Acquired as an Entra v2 scope.
    public const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";

    // Trailing connstr marker carrying the base64(JSON) azure-secret fields from BuildConnectionString to the
    // catalog. ADOMD would reject an unknown keyword, so the catalog strips it before opening the connection.
    public const string CredMarker = ";ArrowNetDaxCred=";

    /// <summary>Encodes the azure-secret fields into the connstr marker (appended to the ATTACH target).</summary>
    public static string AppendCredMarker(string baseConnString, IReadOnlyDictionary<string, string> fields)
    {
        var json = JsonSerializer.Serialize(fields);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return baseConnString + CredMarker + b64;
    }

    /// <summary>Splits a connstr into (clean connstr, credential) — the credential is null when no marker is
    /// present (e.g. local Power BI Desktop, or an explicit connstr that already carries its own auth).</summary>
    public static (string ConnectionString, TokenCredential? Credential) Extract(string connectionString)
    {
        int idx = connectionString.IndexOf(CredMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return (connectionString, null);
        }
        var clean = connectionString.Substring(0, idx);
        var b64 = connectionString.Substring(idx + CredMarker.Length);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        return (clean, BuildCredential(fields));
    }

    private static TokenCredential BuildCredential(IReadOnlyDictionary<string, string> fields)
    {
        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? "" : "";
        var provider = F("provider").ToLowerInvariant();
        var tenantId = F("tenant_id");
        var clientId = F("client_id");
        var clientSecret = F("client_secret");

        // Service principal (client_id + client_secret + tenant_id) — the truest "same principal" as the
        // warehouse's Active Directory Service Principal auth.
        if (clientSecret.Length > 0 && clientId.Length > 0 && tenantId.Length > 0)
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }
        // Managed identity (user-assigned when a client id is present, else system-assigned).
        if (provider == "managed_identity")
        {
            return clientId.Length > 0
                ? new ManagedIdentityCredential(clientId)
                : new ManagedIdentityCredential();
        }
        // credential_chain / default / anything else → the same ambient chain SqlClient's
        // "Active Directory Default" runs (env → managed identity → VS → VS Code → Azure CLI → …).
        return new DefaultAzureCredential();
    }

    /// <summary>
    /// "Active Directory Default"-style fallback when no secret is supplied: a remote XMLA endpoint
    /// (a <c>scheme://</c> Data Source — Fabric / Power BI / AAS) with no inline credential gets a
    /// <see cref="DefaultAzureCredential"/>, which runs the SAME ambient chain SqlClient's "Active Directory
    /// Default" uses (env vars incl. AZURE_TENANT_ID/CLIENT_ID/CLIENT_SECRET → managed identity → Visual
    /// Studio → VS Code → Azure CLI → interactive). Local Power BI Desktop (a <c>localhost</c> Data Source)
    /// and an explicit connstr carrying its own <c>Password=</c>/<c>User ID=</c> get no token (null).
    /// </summary>
    public static TokenCredential? DefaultCredentialForTarget(string connectionString)
    {
        var lower = connectionString.ToLowerInvariant();
        bool remote = lower.Contains("://"); // cloud XMLA uses a scheme:// Data Source; on-prem / localhost don't
        bool hasInlineAuth = lower.Contains("password=") || lower.Contains("pwd=") || lower.Contains("user id=");
        return remote && !hasInlineAuth ? new DefaultAzureCredential() : null;
    }

    /// <summary>Acquires a Power BI-scoped access token from the credential (Azure.Identity caches + refreshes
    /// internally, so per-connection calls are cheap and always valid).</summary>
    public static AccessToken GetToken(TokenCredential credential)
        => credential.GetToken(new TokenRequestContext(new[] { PowerBiScope }), default);
}
