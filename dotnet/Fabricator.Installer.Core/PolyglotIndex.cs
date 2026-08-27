// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Fabricator.Installer;

/// <summary>
/// The fixed-size trailer that locates the payload and manifest inside the artifact:
/// <code>
/// [library image][payload archive][manifest json][INDEX][DuckDB metadata footer]
/// </code>
/// Layout (little-endian, 32 bytes):
/// <code>
///  0..7   magic "FABPKG01"
///  8..11  int32  format version
/// 12..15  int32  flags (reserved, 0)
/// 16..23  int64  payload archive length
/// 24..27  int32  manifest length
/// 28..31  int32  reserved (0)
/// </code>
/// </summary>
public readonly struct PolyglotIndex
{
    /// <summary>Serialized size in bytes.</summary>
    public const int Size = 32;

    /// <summary>Current format version written by <see cref="PolyglotWriter"/>.</summary>
    public const int CurrentFormatVersion = 1;

    private const int MagicLength = 8;
    private const string MagicText = "FABPKG01";

    /// <summary>Upper bound on a plausible manifest, used to reject a false magic match.</summary>
    internal const int MaxManifestLength = 1 << 20;

    public PolyglotIndex(int formatVersion, long payloadLength, int manifestLength)
    {
        FormatVersion = formatVersion;
        PayloadLength = payloadLength;
        ManifestLength = manifestLength;
    }

    public int FormatVersion { get; }

    public long PayloadLength { get; }

    public int ManifestLength { get; }

    internal static ReadOnlySpan<byte> Magic => "FABPKG01"u8;

    /// <summary>The magic as text, for diagnostics.</summary>
    public static string MagicString => MagicText;

    internal void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Index buffer too small.", nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], PayloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], ManifestLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], 0);
    }

    internal byte[] ToArray()
    {
        byte[] buffer = new byte[Size];
        Write(buffer);
        return buffer;
    }

    /// <summary>
    /// Parses a candidate index. Returns false for anything implausible — the reader locates the
    /// index by scanning for the magic, so a byte sequence that merely happens to contain
    /// "FABPKG01" (e.g. inside the library image or the metadata footer) must be rejected here
    /// rather than trusted.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> source, out PolyglotIndex index)
    {
        index = default;

        if (source.Length < Size || !source[..MagicLength].SequenceEqual(Magic))
        {
            return false;
        }

        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
        int flags = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(source[16..]);
        int manifestLength = BinaryPrimitives.ReadInt32LittleEndian(source[24..]);
        int reserved = BinaryPrimitives.ReadInt32LittleEndian(source[28..]);

        if (formatVersion != CurrentFormatVersion || flags != 0 || reserved != 0)
        {
            return false;
        }

        if (payloadLength <= 0 || manifestLength <= 0 || manifestLength > MaxManifestLength)
        {
            return false;
        }

        index = new PolyglotIndex(formatVersion, payloadLength, manifestLength);
        return true;
    }
}
