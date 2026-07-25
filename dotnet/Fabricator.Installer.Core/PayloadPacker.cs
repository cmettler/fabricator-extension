using System.IO.Compression;

namespace Fabricator.Installer;

/// <summary>One file to place into the payload archive.</summary>
/// <param name="RelativePath">Path inside the archive, <c>/</c>-separated, relative and non-escaping.</param>
/// <param name="SourcePath">Absolute or working-directory-relative path of the file to read.</param>
public sealed record PayloadEntry(string RelativePath, string SourcePath);

/// <summary>Result of packing: what to record in the manifest.</summary>
public sealed record PayloadPackResult(long Length, string Sha256, int EntryCount);

/// <summary>
/// Builds the payload archive deterministically, so the same inputs yield the same bytes and
/// therefore the same SHA-256. That reproducibility is what makes the sha usable as the
/// idempotence marker: a rebuilt-but-identical payload must not trigger a re-extraction.
/// </summary>
public static class PayloadPacker
{
    /// <summary>
    /// The timestamp stamped on every entry. Wall-clock times would make the archive differ on
    /// every build. 1980-01-01 is the earliest value the DOS timestamp in a zip can represent.
    /// </summary>
    /// <remarks>
    /// A zip's DOS timestamp has no timezone, and .NET encodes the wall-clock component of the
    /// <see cref="DateTimeOffset"/> verbatim — so this UTC-offset constant produces identical bytes
    /// on build machines in different timezones, which is what makes the payload SHA reproducible
    /// across them. Do not "normalize" it to local time. (On read, .NET reattaches the reader's local
    /// offset, so the round-tripped value only compares equal on its <c>DateTime</c> part.)
    /// </remarks>
    public static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Writes a deflate zip of <paramref name="entries"/> to <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="destination"/> must be seekable and positioned at 0. A zip's central
    /// directory records local-header offsets relative to the START OF THE STREAM, so an archive
    /// written at a non-zero position is only readable through a view with that same origin —
    /// which the polyglot reader (whose payload view starts exactly at the archive) is not. Packing
    /// standalone and concatenating afterwards keeps the archive 0-based and self-contained.
    /// </remarks>
    public static PayloadPackResult Pack(IEnumerable<PayloadEntry> entries, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanSeek || !destination.CanWrite)
        {
            throw new ArgumentException("The payload destination must be writable and seekable.", nameof(destination));
        }

        if (destination.Position != 0)
        {
            throw new ArgumentException(
                "The payload archive must be written at stream position 0 — zip offsets are relative to the " +
                "start of the stream, so an archive written at an offset cannot be read back through a window.",
                nameof(destination));
        }

        List<PayloadEntry> ordered = Normalize(entries);

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (PayloadEntry entry in ordered)
            {
                ZipArchiveEntry zipEntry = archive.CreateEntry(entry.RelativePath, CompressionLevel.Optimal);
                zipEntry.LastWriteTime = FixedTimestamp;
                using Stream target = zipEntry.Open();
                using FileStream source = File.OpenRead(entry.SourcePath);
                source.CopyTo(target);
            }
        }

        long length = destination.Position;
        destination.Position = 0;
        string sha = Hashing.Sha256Hex(destination);
        destination.Position = length;

        return new PayloadPackResult(length, sha, ordered.Count);
    }

    /// <summary>
    /// Enumerates every file under <paramref name="directory"/> as archive entries rooted at
    /// <paramref name="archivePrefix"/>. Empty directories are not represented — extraction
    /// recreates parents, and the payload has no meaningful empty directories.
    /// </summary>
    public static IReadOnlyList<PayloadEntry> EnumerateDirectory(string directory, string archivePrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(archivePrefix);

        if (!Directory.Exists(directory))
        {
            throw new InstallerException($"Cannot pack '{directory}': the directory does not exist.");
        }

        string root = Path.GetFullPath(directory);
        string prefix = archivePrefix.Trim('/');
        var result = new List<PayloadEntry>();

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            result.Add(new PayloadEntry(prefix.Length == 0 ? relative : prefix + "/" + relative, file));
        }

        return result;
    }

    private static List<PayloadEntry> Normalize(IEnumerable<PayloadEntry> entries)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<PayloadEntry>();

        foreach (PayloadEntry entry in entries)
        {
            string path = ArchivePath.Normalize(entry.RelativePath);
            if (seen.TryGetValue(path, out string? existing))
            {
                // Case-insensitive, because a case-only collision would silently overwrite on
                // Windows while producing two files on Linux.
                throw new InstallerException(
                    $"Duplicate payload entry '{path}' (from '{existing}' and '{entry.SourcePath}').");
            }

            seen.Add(path, entry.SourcePath);
            normalized.Add(entry with { RelativePath = path });
        }

        normalized.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return normalized;
    }
}
