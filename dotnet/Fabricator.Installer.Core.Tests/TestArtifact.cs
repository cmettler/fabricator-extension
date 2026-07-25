namespace Fabricator.Installer.Tests;

/// <summary>A temp directory that deletes itself.</summary>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fabinst_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    internal string CreateSubdirectory(string name)
    {
        string path = Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open by a test leaves the temp dir behind; harmless.
        }
    }
}

/// <summary>
/// Builds realistically-shaped payloads and artifacts: a core loadable plus a managed directory with
/// a nested subdirectory, packed and wrapped exactly as <c>pack-distribution</c> will.
/// </summary>
internal static class TestArtifact
{
    internal const string CoreContentDefault = "fake core loadable v1";

    /// <summary>Lays out a payload source tree under <paramref name="root"/> and returns its entries.</summary>
    internal static IReadOnlyList<PayloadEntry> CreateSource(string root, string coreContent = CoreContentDefault)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, FabricatorPayloadNames.ManagedDirectory, "nested"));

        string core = Path.Combine(root, FabricatorPayloadNames.CoreFile);
        File.WriteAllText(core, coreContent);

        string bridge = Path.Combine(root, FabricatorPayloadNames.ManagedDirectory, "Fabricator.Bridge.dll");
        File.WriteAllText(bridge, "fake bridge assembly");

        string nested = Path.Combine(root, FabricatorPayloadNames.ManagedDirectory, "nested", "runtime.json");
        File.WriteAllText(nested, "{\"fake\":true}");

        return
        [
            new PayloadEntry(FabricatorPayloadNames.CoreFile, core),
            new PayloadEntry($"{FabricatorPayloadNames.ManagedDirectory}/Fabricator.Bridge.dll", bridge),
            new PayloadEntry($"{FabricatorPayloadNames.ManagedDirectory}/nested/runtime.json", nested),
        ];
    }

    /// <summary>Packs a payload archive and returns the matching manifest.</summary>
    internal static PayloadManifest Pack(
        IEnumerable<PayloadEntry> entries,
        string payloadPath,
        string targetDuckDbVersion = "v1.5.5",
        string platform = "windows_amd64")
    {
        PayloadPackResult result;
        using (FileStream output = File.Create(payloadPath))
        {
            result = PayloadPacker.Pack(entries, output);
        }

        return new PayloadManifest
        {
            FabricatorVersion = "0.0.1",
            TargetDuckDbVersion = targetDuckDbVersion,
            Platform = platform,
            Sku = "standard",
            PayloadSha256 = result.Sha256,
            PayloadLength = result.Length,
            EntryCount = result.EntryCount,
        };
    }

    /// <summary>
    /// Writes a complete artifact: a fake library image, the payload, the manifest, the index, and a
    /// <paramref name="footerSize"/>-byte stand-in for DuckDB's metadata footer.
    /// </summary>
    internal static string WriteArtifact(
        string artifactPath,
        string payloadPath,
        PayloadManifest manifest,
        int footerSize = 534,
        byte[]? libraryImage = null,
        byte[]? footerBytes = null)
    {
        libraryImage ??= FakeLibraryImage();

        using (FileStream output = File.Create(artifactPath))
        using (var library = new MemoryStream(libraryImage))
        using (FileStream payload = File.OpenRead(payloadPath))
        {
            PolyglotWriter.Write(library, payload, manifest, output);

            if (footerBytes is not null)
            {
                output.Write(footerBytes);
            }
            else if (footerSize > 0)
            {
                byte[] footer = new byte[footerSize];
                for (int i = 0; i < footer.Length; i++)
                {
                    footer[i] = (byte)(i % 251);
                }

                output.Write(footer);
            }
        }

        return artifactPath;
    }

    /// <summary>Deterministic stand-in for the AOT library image.</summary>
    internal static byte[] FakeLibraryImage(int length = 4096)
    {
        byte[] image = new byte[length];
        for (int i = 0; i < image.Length; i++)
        {
            image[i] = (byte)(i * 7 % 256);
        }

        return image;
    }
}
