using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// Discovers a locally-running Power BI Desktop analysis-services instance and its TCP port so ADOMD can
/// connect to <c>localhost:&lt;port&gt;</c>. Power BI Desktop is <b>Windows-only</b>, so every entry point
/// is guarded by <see cref="RuntimeInformation.IsOSPlatform"/> — on any other OS detection throws a clear
/// error (SSAS / Fabric semantic models are reached by an explicit connection string and need no detection).
///
/// We read the per-workspace <c>msmdsrv.port.txt</c> file (UTF-16) under each edition's workspace root:
/// the classic install (<c>…\Power BI Desktop\…</c>), the Report Server edition (<c>…\Power BI Desktop
/// SSRS\…</c>), and the Store/MSIX edition (under <c>%LOCALAPPDATA%\Packages\Microsoft.MicrosoftPowerBIDesktop*</c>).
/// A closed instance can leave a stale port file behind, so we prefer the <b>newest port that is actually
/// listening</b> (a quick loopback TCP connect), falling back to the newest port file if none verifies.
/// (Simpler + cross-platform-safe vs the old TCP-table enumeration: iphlpapi P/Invoke + WMI parent checks.)
/// </summary>
internal static class PowerBiDesktop
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Resolves the local Power BI Desktop port: the newest workspace whose port is actually listening
    /// (across all editions). Throws if not on Windows or no running instance is found.
    /// </summary>
    public static int ResolvePort()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Local Power BI Desktop auto-detection is only supported on Windows. " +
                "For SSAS / Fabric / AAS, pass an explicit connection string (Data Source=…).");
        }

        var candidates = EnumeratePortFiles(); // newest first, all editions
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No running Power BI Desktop instance found (no msmdsrv.port.txt under any Power BI Desktop " +
                "workspace — classic, Report Server, or Store edition). Open a .pbix in Power BI Desktop, or " +
                "pass an explicit Data Source.");
        }
        foreach (var c in candidates)
        {
            if (IsListening(c.Port))
            {
                return c.Port; // newest that is actually accepting connections (skips stale port files)
            }
        }
        return candidates[0].Port; // none verified listening — best-effort newest
    }

    /// <summary>All discovered (Port, LastWriteUtc) pairs across every edition's workspace root, newest first.
    /// Empty off-Windows or when none are found.</summary>
    public static IReadOnlyList<(int Port, DateTime LastWriteUtc)> EnumeratePortFiles()
    {
        var result = new List<(int Port, DateTime LastWriteUtc)>();
        if (!IsSupported)
        {
            return result;
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in WorkspaceRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "msmdsrv.port.txt", options);
            }
            catch
            {
                continue; // unreadable root — skip
            }
            foreach (var file in files)
            {
                try
                {
                    // The file is UTF-16; be lenient and keep only digits.
                    var raw = File.ReadAllText(file, Encoding.Unicode);
                    var digits = new string(raw.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var port) && port > 0)
                    {
                        result.Add((port, File.GetLastWriteTimeUtc(file)));
                    }
                }
                catch
                {
                    // Unreadable/locked workspace file — skip it.
                }
            }
        }
        result.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc)); // newest first
        return result;
    }

    /// <summary>The workspace roots to scan for <c>msmdsrv.port.txt</c>, one per Power BI Desktop edition.</summary>
    private static IEnumerable<string> WorkspaceRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Classic (MSI) install.
        yield return Path.Combine(local, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        // Power BI Report Server edition.
        yield return Path.Combine(local, "Microsoft", "Power BI Desktop SSRS", "AnalysisServicesWorkspaces");
        // Store / MSIX editions: each package keeps its workspaces somewhere under the package dir — recurse it.
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            IEnumerable<string> pkgs;
            try
            {
                pkgs = Directory.EnumerateDirectories(packages, "Microsoft.MicrosoftPowerBIDesktop*");
            }
            catch
            {
                pkgs = System.Array.Empty<string>();
            }
            foreach (var pkg in pkgs)
            {
                yield return pkg;
            }
        }
    }

    /// <summary>Quick check that something is listening on <c>localhost:port</c> (250 ms timeout), so a stale
    /// workspace port file from a closed instance is skipped.</summary>
    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
            {
                return false;
            }
            client.EndConnect(connect);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
