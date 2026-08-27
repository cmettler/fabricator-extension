// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;

namespace Fabricator.Installer;

/// <summary>
/// Reads a distribution artifact's trailer: the payload index and the manifest. Opening is cheap
/// (one tail read plus one small manifest read) and touches neither the payload nor the disk beyond
/// the artifact itself, so the load-time compatibility gate can run before anything is extracted.
/// </summary>
public sealed class PolyglotPackage
{
    /// <summary>
    /// How far back from EOF to look for the index. The DuckDB metadata footer that follows it is
    /// 534 bytes today, but its size is NOT a stable contract — <c>append_extension_metadata.py</c>
    /// writes a 22-byte WASM custom-section header plus 8x32 fields plus 256 signature bytes, while
    /// DuckDB itself parses only the trailing 512. Scanning a generous window for the magic keeps
    /// us independent of that number entirely.
    /// </summary>
    public const int DefaultTailScanWindow = 64 * 1024;

    private PolyglotPackage(string filePath, PayloadManifest manifest, long payloadOffset, long payloadLength, long indexOffset)
    {
        FilePath = filePath;
        Manifest = manifest;
        PayloadOffset = payloadOffset;
        PayloadLength = payloadLength;
        IndexOffset = indexOffset;
    }

    /// <summary>The artifact this package was read from.</summary>
    public string FilePath { get; }

    /// <summary>The payload manifest.</summary>
    public PayloadManifest Manifest { get; }

    /// <summary>Absolute offset of the payload archive within the artifact.</summary>
    public long PayloadOffset { get; }

    /// <summary>Length of the payload archive in bytes.</summary>
    public long PayloadLength { get; }

    /// <summary>Absolute offset of the index; diagnostic.</summary>
    public long IndexOffset { get; }

    /// <summary>Opens the artifact's trailer, throwing <see cref="InstallerException"/> if it carries no valid payload.</summary>
    public static PolyglotPackage Open(string path, int tailScanWindow = DefaultTailScanWindow)
    {
        if (!TryOpen(path, tailScanWindow, out PolyglotPackage? package, out string? error))
        {
            throw new InstallerException(error);
        }

        return package;
    }

    /// <summary>Non-throwing <see cref="Open"/>.</summary>
    public static bool TryOpen(
        string path,
        int tailScanWindow,
        [NotNullWhen(true)] out PolyglotPackage? package,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(tailScanWindow, PolyglotIndex.Size);

        package = null;
        error = null;

        using FileStream file = File.OpenRead(path);
        long fileLength = file.Length;
        if (fileLength < PolyglotIndex.Size)
        {
            error = $"'{path}' is too small to be a fabricator distribution artifact.";
            return false;
        }

        int windowLength = (int)Math.Min(tailScanWindow, fileLength);
        byte[] tail = new byte[windowLength];
        file.Position = fileLength - windowLength;
        file.ReadExactly(tail, 0, windowLength);
        long windowStart = fileLength - windowLength;

        bool sawMagic = false;

        // Scan backward: our index is the last occurrence of the magic (only the metadata footer
        // follows it). Candidates that fail validation are skipped rather than fatal, so a magic
        // byte sequence occurring by chance inside the library image or the footer cannot mask the
        // real index.
        for (int i = windowLength - PolyglotIndex.Size; i >= 0; i--)
        {
            if (!tail.AsSpan(i, PolyglotIndex.Size).StartsWith(PolyglotIndex.Magic))
            {
                continue;
            }

            sawMagic = true;

            if (!PolyglotIndex.TryRead(tail.AsSpan(i, PolyglotIndex.Size), out PolyglotIndex index))
            {
                continue;
            }

            long indexOffset = windowStart + i;
            long manifestOffset = indexOffset - index.ManifestLength;
            long payloadOffset = manifestOffset - index.PayloadLength;
            if (payloadOffset < 0)
            {
                continue;
            }

            // The manifest may start before the scan window, so read it from the file.
            byte[] manifestBytes = new byte[index.ManifestLength];
            file.Position = manifestOffset;
            file.ReadExactly(manifestBytes, 0, manifestBytes.Length);

            PayloadManifest manifest;
            try
            {
                manifest = PayloadManifest.FromJsonUtf8(manifestBytes);
            }
            catch (InstallerException)
            {
                continue;
            }

            // Cross-check: index and manifest independently record the payload length. Requiring
            // agreement makes a false positive effectively impossible and catches truncation.
            if (manifest.PayloadLength != index.PayloadLength)
            {
                continue;
            }

            package = new PolyglotPackage(Path.GetFullPath(path), manifest, payloadOffset, index.PayloadLength, indexOffset);
            return true;
        }

        error = sawMagic
            ? $"'{path}' contains a fabricator payload index that is corrupt or truncated."
            : $"'{path}' does not contain a fabricator payload (no '{PolyglotIndex.MagicString}' index in the last {windowLength} bytes). " +
              "This is not a fabricator distribution artifact.";
        return false;
    }

    /// <summary>
    /// Opens an independent, seekable stream over just the payload archive. The caller owns it.
    /// </summary>
    public Stream OpenPayload()
    {
        FileStream file = File.OpenRead(FilePath);
        try
        {
            return new WindowStream(file, PayloadOffset, PayloadLength, ownsInner: true);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}
