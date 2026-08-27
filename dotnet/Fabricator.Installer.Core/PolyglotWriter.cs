// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer;

/// <summary>
/// Concatenates the AOT library image, the payload archive, the manifest and the index into the
/// "combined" artifact. The DuckDB metadata footer is appended AFTERWARDS (by
/// <c>append_extension_metadata.py</c>) because DuckDB requires it to be the trailing bytes.
/// </summary>
/// <remarks>
/// Nothing here is platform-specific: OS loaders map only the segments the library's own headers
/// declare, so everything we append is inert to <c>dlopen</c>/<c>LoadLibrary</c> — which is exactly
/// why DuckDB's own footer works, and why no per-platform resource embedding is needed.
/// </remarks>
public static class PolyglotWriter
{
    /// <summary>Writes the combined artifact (without the DuckDB footer) to <paramref name="outputPath"/>.</summary>
    public static void Write(string libraryPath, string payloadPath, PayloadManifest manifest, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);
        ArgumentException.ThrowIfNullOrEmpty(payloadPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(manifest);

        using FileStream library = File.OpenRead(libraryPath);
        using FileStream payload = File.OpenRead(payloadPath);
        using FileStream output = File.Create(outputPath);
        Write(library, payload, manifest, output);
    }

    /// <summary>Writes the combined artifact to <paramref name="output"/>.</summary>
    public static void Write(Stream library, Stream payload, PayloadManifest manifest, Stream output)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(output);

        byte[] manifestBytes = manifest.ToJsonUtf8();
        if (manifestBytes.Length > PolyglotIndex.MaxManifestLength)
        {
            throw new InstallerException(
                $"The payload manifest is {manifestBytes.Length} bytes, above the {PolyglotIndex.MaxManifestLength}-byte limit.");
        }

        library.CopyTo(output);

        long payloadStart = output.CanSeek ? output.Position : -1;
        payload.CopyTo(output);
        long payloadLength = output.CanSeek ? output.Position - payloadStart : payload.Length;

        if (manifest.PayloadLength != payloadLength)
        {
            // The manifest's length is what the reader cross-checks the index against; a mismatch
            // here would ship an artifact that refuses to open.
            throw new InstallerException(
                $"Manifest payload length {manifest.PayloadLength} does not match the {payloadLength} bytes written.");
        }

        output.Write(manifestBytes);
        output.Write(new PolyglotIndex(PolyglotIndex.CurrentFormatVersion, payloadLength, manifestBytes.Length).ToArray());
    }
}
