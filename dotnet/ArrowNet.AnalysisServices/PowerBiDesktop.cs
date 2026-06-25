using System.Runtime.InteropServices;
using System.Text;

namespace ArrowNet.AnalysisServices;

/// <summary>
/// Discovers a locally-running Power BI Desktop analysis-services instance and its TCP port so ADOMD can
/// connect to <c>localhost:&lt;port&gt;</c>. Power BI Desktop is <b>Windows-only</b>, so every entry point
/// is guarded by <see cref="RuntimeInformation.IsOSPlatform"/> — on any other OS detection throws a clear
/// error (SSAS / Fabric semantic models are reached by an explicit connection string and need no detection).
///
/// We read the per-workspace <c>msmdsrv.port.txt</c> file under
/// <c>%LOCALAPPDATA%\Microsoft\Power BI Desktop\AnalysisServicesWorkspaces\*\Data\</c> (UTF-16) — simpler
/// and cross-process-reliable vs the old TCP-table enumeration (iphlpapi P/Invoke + WMI parent-process
/// checks). The newest-written port file wins (the active workspace); a stale file just fails to connect.
/// </summary>
internal static class PowerBiDesktop
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Resolves the local Power BI Desktop port (newest workspace first). Throws if not on Windows or no
    /// running instance is found.
    /// </summary>
    public static int ResolvePort()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Local Power BI Desktop auto-detection is only supported on Windows. " +
                "For SSAS / Fabric / AAS, pass an explicit connection string (Data Source=…).");
        }

        var ports = EnumeratePortFiles();
        if (ports.Count == 0)
        {
            throw new InvalidOperationException(
                "No running Power BI Desktop instance found (no msmdsrv.port.txt under " +
                "%LOCALAPPDATA%\\Microsoft\\Power BI Desktop\\AnalysisServicesWorkspaces). " +
                "Open a .pbix in Power BI Desktop, or pass an explicit Data Source.");
        }
        return ports[0].Port; // newest workspace
    }

    /// <summary>All discovered (Port, LastWriteUtc) pairs, newest first. Empty off-Windows or when none run.</summary>
    public static IReadOnlyList<(int Port, DateTime LastWriteUtc)> EnumeratePortFiles()
    {
        var result = new List<(int Port, DateTime LastWriteUtc)>();
        if (!IsSupported)
        {
            return result;
        }
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        if (!Directory.Exists(root))
        {
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(root, "msmdsrv.port.txt", SearchOption.AllDirectories))
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
        result.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc)); // newest first
        return result;
    }
}
