using Fabricator.Bridge;

namespace Fabricator.AnalysisServices;

/// <summary>
/// The Analysis Services / DAX backend — the second provider behind the one fabricator binary (provider name
/// <c>"dax"</c>, aliases <c>"adomd"</c>/<c>"powerbi"</c>/<c>"ssas"</c>/<c>"fabric"</c>). Connects to a
/// semantic model via <c>Microsoft.AnalysisServices.AdomdClient</c>; one connection mode is chosen from the
/// ATTACH target:
/// <list type="bullet">
/// <item><b>Local Power BI Desktop</b> — empty target or a <c>pbidesktop://</c> marker → auto-detect the
/// running msmdsrv port (Windows-only; see <see cref="PowerBiDesktop"/>) → <c>Data Source=localhost:&lt;port&gt;</c>.</item>
/// <item><b>Explicit endpoint</b> (SSAS / Fabric / AAS) — any other target is treated as an ADOMD connection
/// string (e.g. <c>Data Source=powerbi://api.powerbi.com/v1.0/myorg/Workspace;…</c>). Token/Entra auth via a
/// secret is a later slice.</item>
/// </list>
/// See docs/dax-provider.md.
/// </summary>
public sealed class DaxBackend : IBackend
{
    public string Name => "dax";

    public IEnumerable<string> Aliases => new[] { "adomd", "powerbi", "ssas", "fabric" };

    /// <summary>
    /// Resolves a secret into the connection string. The DAX provider declares no secret type of its own, but
    /// it CONSUMES a foreign <c>azure</c> secret (the same one a Fabric Warehouse ATTACH uses) to authenticate
    /// to a Fabric/AAS XMLA endpoint — so a model and its underlying warehouse share one principal. The secret
    /// fields are carried to the catalog via a connstr marker (see <see cref="DaxTokenAuth"/>), where they
    /// build a credential that mints a Power BI-scoped token set as <c>AdomdConnection.AccessToken</c>.
    /// </summary>
    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString)
    {
        if (secretType.Equals("azure", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(baseConnString))
            {
                throw new ArgumentException(
                    "dax: an azure secret supplies only auth — give the XMLA endpoint in the ATTACH target, e.g. " +
                    "ATTACH 'Data Source=powerbi://api.powerbi.com/v1.0/myorg/WS;Initial Catalog=Model' AS m " +
                    "(TYPE mssql, PROVIDER 'dax', SECRET <azure_sp>)");
            }
            return DaxTokenAuth.AppendCredMarker(baseConnString, fields);
        }
        // Our own (fabricator) secret or none → the ATTACH target is already the connection string.
        return baseConnString;
    }

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson)
    {
        // Split off any azure-secret credential marker (Fabric/AAS token auth) before resolving the target.
        var (connStr, credential) = DaxTokenAuth.Extract(connectionString);
        var resolved = ResolveConnectionString(connStr);
        // No secret but a remote XMLA endpoint → "Active Directory Default"-style ambient credential.
        credential ??= DaxTokenAuth.DefaultCredentialForTarget(resolved);
        return new DaxCatalog(resolved, credential);
    }

    /// <summary>
    /// Turns the ATTACH target into an ADOMD connection string. Empty or a <c>pbidesktop[://]</c> marker →
    /// auto-detect a local Power BI Desktop instance; anything else is passed through verbatim.
    /// </summary>
    internal static string ResolveConnectionString(string attachTarget)
    {
        var target = (attachTarget ?? string.Empty).Trim();
        if (target.Length == 0 || IsLocalDesktopMarker(target))
        {
            int port = PowerBiDesktop.ResolvePort();
            return $"Data Source=localhost:{port}";
        }
        return target;
    }

    private static bool IsLocalDesktopMarker(string target)
    {
        return target.Equals("pbidesktop", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("pbidesktop:", StringComparison.OrdinalIgnoreCase)
            || target.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
