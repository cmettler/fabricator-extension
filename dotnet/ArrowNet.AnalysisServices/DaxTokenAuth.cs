using System.Text;
using System.Text.Json;
using ArrowNet.Bridge;
using Azure.Core;

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
    // AAS-only endpoints accept the same.) Aliases the shared resolver's constant.
    public const string PowerBiScope = FabricCredentialResolver.PowerBiScope;

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
        return (clean, FabricCredentialResolver.Resolve(fields));
    }

    /// <summary>
    /// "Active Directory Default"-style fallback when no secret is supplied: a remote XMLA endpoint
    /// (a <c>scheme://</c> Data Source — Fabric / Power BI / AAS) with no inline credential gets a
    /// <see cref="Azure.Identity.DefaultAzureCredential"/>; local Power BI Desktop (a <c>localhost</c> Data
    /// Source) and an explicit connstr carrying its own <c>Password=</c>/<c>User ID=</c> get no token (null).
    /// Delegates to the shared <see cref="FabricCredentialResolver.ResolveForRemoteTarget"/>.
    /// </summary>
    public static TokenCredential? DefaultCredentialForTarget(string connectionString)
        => FabricCredentialResolver.ResolveForRemoteTarget(connectionString);

    /// <summary>Acquires a Power BI-scoped access token from the credential (Azure.Identity caches + refreshes
    /// internally, so per-connection calls are cheap and always valid).</summary>
    public static AccessToken GetToken(TokenCredential credential)
        => FabricCredentialResolver.GetToken(credential, FabricCredentialResolver.PowerBiScope);
}
