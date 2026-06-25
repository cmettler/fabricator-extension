using ArrowNet.Bridge;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// The Analysis Services / DAX backend — the second provider behind the one arrownet binary (provider name
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

    // Secret type + token auth (Fabric/AAS) is a later slice; none declared yet.
    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString)
        => baseConnString; // no provider secret yet → the ATTACH target is the connection string

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson)
        => new DaxCatalog(ResolveConnectionString(connectionString));

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
