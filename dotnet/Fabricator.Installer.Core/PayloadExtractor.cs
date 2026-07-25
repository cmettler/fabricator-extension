using System.IO.Compression;

namespace Fabricator.Installer;

/// <summary>Unpacks the payload archive into a directory.</summary>
public static class PayloadExtractor
{
    /// <summary>
    /// Extracts every entry of <paramref name="payload"/> beneath <paramref name="destinationDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Entry paths are validated twice — syntactically via <see cref="ArchivePath"/>, then by
    /// checking the resolved absolute path is still inside the destination. Anything that escapes is
    /// rejected rather than sanitized: a crafted artifact must fail loudly, not write somewhere
    /// almost-right.
    /// </remarks>
    public static int Extract(Stream payload, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        string root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        int extracted = 0;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InstallerException(
                "The fabricator payload archive is corrupt or truncated: " + ex.Message, ex);
        }

        using (archive)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                bool isDirectoryEntry = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                string rawName = isDirectoryEntry ? entry.FullName[..^1] : entry.FullName;

                if (!ArchivePath.TryNormalize(rawName, out string relative, out string? error))
                {
                    throw new InstallerException($"The fabricator payload contains an unsafe entry '{entry.FullName}': {error}");
                }

                string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                {
                    throw new InstallerException(
                        $"The fabricator payload entry '{entry.FullName}' resolves outside the extraction directory.");
                }

                if (isDirectoryEntry)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }

        return extracted;
    }
}
