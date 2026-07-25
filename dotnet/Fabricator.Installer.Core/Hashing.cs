using System.Security.Cryptography;

namespace Fabricator.Installer;

internal static class Hashing
{
    /// <summary>
    /// Lowercase hex SHA-256 of the stream from its current position to the end.
    /// Lowercase hex (not base64) because the value is also the content of the marker file, which
    /// a human may well diff against a release note.
    /// </summary>
    internal static string Sha256Hex(Stream stream)
    {
        byte[] hash = SHA256.HashData(stream);
        // Convert.ToHexStringLower is .NET 9+; net8.0 is our floor.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
