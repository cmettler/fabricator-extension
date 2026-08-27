// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer;

/// <summary>
/// Validation for paths inside the payload archive. Shared by the packer and the extractor so that
/// what we refuse to write is exactly what we refuse to read — the extractor's guard is what stops
/// a tampered artifact from writing outside the extension directory ("zip slip").
/// </summary>
internal static class ArchivePath
{
    /// <summary>
    /// Returns the canonical <c>a/b/c</c> form, or throws if the path is absolute, escapes its
    /// root, or contains empty/relative segments.
    /// </summary>
    internal static string Normalize(string path)
    {
        if (!TryNormalize(path, out string? normalized, out string? error))
        {
            throw new InstallerException($"Invalid payload entry path '{path}': {error}");
        }

        return normalized;
    }

    internal static bool TryNormalize(string? path, out string normalized, out string? error)
    {
        normalized = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "the path is empty.";
            return false;
        }

        string candidate = path.Replace('\\', '/');

        if (candidate.StartsWith('/'))
        {
            error = "absolute paths are not allowed.";
            return false;
        }

        // A Windows drive or ADS spec ("C:/x", "x:stream"): rejected everywhere, not just on
        // Windows, so an artifact behaves identically on every platform.
        if (candidate.Contains(':'))
        {
            error = "the path must not contain ':'.";
            return false;
        }

        string[] segments = candidate.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                error = "the path contains an empty segment.";
                return false;
            }

            if (segment is "." or "..")
            {
                error = "relative segments ('.' or '..') are not allowed.";
                return false;
            }
        }

        normalized = string.Join('/', segments);
        error = null;
        return true;
    }
}
