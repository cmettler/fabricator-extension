// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer;

/// <summary>
/// The DuckDB state the installer needs, all of it obtainable over SQL from the loading connection
/// (<c>PRAGMA version</c>, <c>PRAGMA platform</c>, <c>current_setting(...)</c>).
/// </summary>
public sealed class DuckDbEnvironment
{
    /// <summary><c>PRAGMA version</c> → <c>library_version</c>, e.g. <c>v1.5.5</c> or <c>v1.6.0-dev123</c>.</summary>
    public string LibraryVersion { get; init; } = "";

    /// <summary><c>PRAGMA version</c> → <c>source_id</c>; the version-directory name for dev builds.</summary>
    public string SourceId { get; init; } = "";

    /// <summary><c>PRAGMA platform</c>, e.g. <c>windows_amd64</c>.</summary>
    public string Platform { get; init; } = "";

    /// <summary><c>current_setting('extension_directory')</c>; empty when unset.</summary>
    public string ExtensionDirectorySetting { get; init; } = "";

    /// <summary><c>current_setting('extension_directories')</c>; the plural LIST setting, empty when unset.</summary>
    public IReadOnlyList<string> ExtensionDirectoriesSetting { get; init; } = [];

    /// <summary>
    /// <c>current_setting('home_directory')</c> if set, else the process home. Only consulted when
    /// a path needs <c>~</c> expansion or the default base applies.
    /// </summary>
    public string HomeDirectory { get; init; } = "";
}

/// <summary>
/// Reimplements DuckDB's extension-directory resolution as a pure function, so the installer writes
/// the core exactly where a later bare <c>LOAD fabricator_core</c> will look for it.
/// </summary>
/// <remarks>
/// Mirrors <c>ExtensionHelper::GetExtensionDirectoryPath</c> (extension_install.cpp:93-136) and was
/// verified empirically against DuckDB 1.5.5 by observing where <c>INSTALL '&lt;file&gt;'</c> lands
/// under a custom <c>extension_directory</c>, a custom <c>extension_directories</c> list, and an
/// overridden <c>home_directory</c>.
/// <para>
/// The subtlety that empirical check caught: the <c>extensions</c> path component is NOT appended by
/// DuckDB. It only appears in the DEFAULT case because the default base string is literally
/// <c>~/.duckdb/extensions</c>. With a custom <c>extension_directory</c> of <c>D:\ext</c> the result
/// is <c>D:\ext\v1.5.5\windows_amd64</c> — no <c>extensions</c> segment. Appending one
/// unconditionally would have quietly extracted the payload into a directory DuckDB never searches.
/// </para>
/// </remarks>
public static class ExtensionDirectoryResolver
{
    /// <summary>The default base when neither directory setting is set (DUCKDB_EXTENSION_DIRECTORIES).</summary>
    private const string DefaultBase = "~/.duckdb/extensions";

    /// <summary>DuckDB treats any version tag without <c>-dev</c> as a release (extension_install.cpp:31-33).</summary>
    public static bool IsRelease(string libraryVersion) =>
        !libraryVersion.Contains("-dev", StringComparison.Ordinal);

    /// <summary>Prepends <c>v</c> when missing (extension_install.cpp:24-29).</summary>
    public static string NormalizeVersionTag(string versionTag) =>
        versionTag.Length > 0 && versionTag[0] != 'v' ? "v" + versionTag : versionTag;

    /// <summary>
    /// The version component of the extension path: the normalized tag for releases, the source id
    /// for dev builds (extension_install.cpp:35-43).
    /// </summary>
    public static string VersionDirectoryName(string libraryVersion, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryVersion);

        if (IsRelease(libraryVersion))
        {
            return NormalizeVersionTag(libraryVersion);
        }

        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new InstallerException(
                $"DuckDB reports the development version '{libraryVersion}' but no source id, so its extension " +
                "directory cannot be determined.");
        }

        return sourceId;
    }

    /// <summary>Resolves the primary extension directory: <c>&lt;base&gt;/&lt;version&gt;/&lt;platform&gt;</c>.</summary>
    public static string Resolve(DuckDbEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(environment.Platform))
        {
            throw new InstallerException("DuckDB reported an empty platform, so its extension directory cannot be determined.");
        }

        string baseDirectory = SelectBaseDirectory(environment);
        baseDirectory = ConvertSeparators(baseDirectory);
        baseDirectory = ExpandPath(baseDirectory, environment.HomeDirectory);

        return Path.Combine(
            baseDirectory,
            VersionDirectoryName(environment.LibraryVersion, environment.SourceId),
            environment.Platform);
    }

    /// <summary>
    /// Picks the base the same way DuckDB picks its PRIMARY directory: the singular setting wins,
    /// then the first entry of the plural list, then the default. DuckDB uses <c>[0]</c> of the same
    /// ordered list for installs, so this is the one directory a name-based LOAD will find.
    /// </summary>
    private static string SelectBaseDirectory(DuckDbEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.ExtensionDirectorySetting))
        {
            return environment.ExtensionDirectorySetting;
        }

        foreach (string directory in environment.ExtensionDirectoriesSetting)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return DefaultBase;
    }

    /// <summary>
    /// <c>FileSystem::ConvertSeparators</c> (file_system.cpp:291-300): Windows accepts both
    /// separators and normalizes <c>/</c> to <c>\</c>; POSIX accepts only <c>/</c>, where a
    /// backslash is a legal filename character and must NOT be rewritten.
    /// </summary>
    private static string ConvertSeparators(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    /// <summary>
    /// <c>FileSystem::ExpandPath</c> (file_system.cpp:392-405): a leading <c>~</c> is replaced by
    /// the home directory. Note it is a bare prefix replacement — no separator is required after
    /// the tilde.
    /// </summary>
    private static string ExpandPath(string path, string homeDirectory)
    {
        if (path.Length == 0 || path[0] != '~')
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new InstallerException(
                $"The extension directory '{path}' needs a home directory, but DuckDB reported none. " +
                "Set one with SET home_directory='/path/to/dir', or set extension_directory to an absolute path.");
        }

        return homeDirectory + path[1..];
    }
}
