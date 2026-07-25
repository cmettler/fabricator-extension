using Fabricator.Installer;

namespace Fabricator.Installer.Pack;

/// <summary>
/// Packs a fabricator distribution artifact (everything except DuckDB's metadata footer, which
/// <c>append_extension_metadata.py</c> must append last).
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage: Fabricator.Installer.Pack --core <file> --managed <dir> --library <file> --output <file>
                                         --duckdb-version <vX.Y.Z> --platform <duckdb_platform>
                                         [--fabricator-version <v>] [--sku standard|standalone]
                                         [--core-name <file name>] [--payload <file>]

          --core             the built C++ loadable (fabricator.duckdb_extension)
          --managed          the published managed directory (becomes 'fabricator/' in the payload)
          --library          the NativeAOT installer library to prepend
          --output           artifact to write (WITHOUT the DuckDB footer)
          --duckdb-version   DuckDB version the core was built against, e.g. v1.5.5
          --platform         DuckDB platform string, e.g. windows_amd64
          --core-name        name the core is extracted under (default fabricator_core.duckdb_extension)
          --payload          keep the intermediate payload archive here (default <output>.payload.zip)
        """;

    private static int Main(string[] args)
    {
        try
        {
            Arguments arguments = Arguments.Parse(args);
            Run(arguments);
            return 0;
        }
        catch (Exception ex) when (ex is InstallerException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }
    }

    private static void Run(Arguments arguments)
    {
        string coreName = arguments.CoreName ?? FabricatorPayloadNames.CoreFile;
        string payloadPath = arguments.PayloadPath ?? arguments.Output + ".payload.zip";

        List<PayloadEntry> entries = [new PayloadEntry(coreName, arguments.Core)];
        entries.AddRange(PayloadPacker.EnumerateDirectory(arguments.Managed, FabricatorPayloadNames.ManagedDirectory));

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(payloadPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        PayloadPackResult packed;
        using (FileStream payload = File.Create(payloadPath))
        {
            packed = PayloadPacker.Pack(entries, payload);
        }

        // hostfxr in the managed directory is how clr_host distinguishes a self-contained payload from
        // a framework-dependent one, so it is also the honest default for the SKU label.
        string sku = arguments.Sku
            ?? (File.Exists(Path.Combine(arguments.Managed, "hostfxr.dll"))
                || File.Exists(Path.Combine(arguments.Managed, "libhostfxr.so"))
                || File.Exists(Path.Combine(arguments.Managed, "libhostfxr.dylib"))
                ? "standalone"
                : "standard");

        var manifest = new PayloadManifest
        {
            FabricatorVersion = arguments.FabricatorVersion,
            TargetDuckDbVersion = ExtensionDirectoryResolver.NormalizeVersionTag(arguments.DuckDbVersion),
            Platform = arguments.Platform,
            Sku = sku,
            CoreFileName = coreName,
            PayloadSha256 = packed.Sha256,
            PayloadLength = packed.Length,
            EntryCount = packed.EntryCount,
        };

        PolyglotWriter.Write(arguments.Library, payloadPath, manifest, arguments.Output);

        long library = new FileInfo(arguments.Library).Length;
        long artifact = new FileInfo(arguments.Output).Length;

        Console.WriteLine($"core           : {arguments.Core}");
        Console.WriteLine($"managed dir    : {arguments.Managed}");
        Console.WriteLine($"entries        : {packed.EntryCount}");
        Console.WriteLine($"target duckdb  : {manifest.TargetDuckDbVersion} / {manifest.Platform} (sku {manifest.Sku})");
        Console.WriteLine($"payload sha256 : {packed.Sha256}");
        Console.WriteLine($"library        : {library:N0} bytes");
        Console.WriteLine($"payload        : {packed.Length:N0} bytes");
        Console.WriteLine($"artifact       : {artifact:N0} bytes -> {arguments.Output}");
        Console.WriteLine("NOTE: the DuckDB metadata footer must still be appended (append_extension_metadata.py).");
    }

    private sealed record Arguments(
        string Core,
        string Managed,
        string Library,
        string Output,
        string DuckDbVersion,
        string Platform,
        string FabricatorVersion,
        string? Sku,
        string? CoreName,
        string? PayloadPath)
    {
        internal static Arguments Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Unexpected argument '{args[i]}'.");
                }

                values[args[i]] = args[i + 1];
            }

            return new Arguments(
                Required(values, "--core"),
                Required(values, "--managed"),
                Required(values, "--library"),
                Required(values, "--output"),
                Required(values, "--duckdb-version"),
                Required(values, "--platform"),
                values.GetValueOrDefault("--fabricator-version", "0.0.1"),
                values.GetValueOrDefault("--sku"),
                values.GetValueOrDefault("--core-name"),
                values.GetValueOrDefault("--payload"));
        }

        private static string Required(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out string? value) && value.Length > 0
                ? value
                : throw new ArgumentException($"Missing required argument {name}.");
    }
}
