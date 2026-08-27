// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using DuckDB.ExtensionKit;
using DuckDB.ExtensionKit.Native;

namespace Fabricator.Installer;

/// <summary>
/// Asks the loading connection for everything <see cref="ExtensionDirectoryResolver"/> and
/// <see cref="CompatibilityGate"/> need. Two queries, all VARCHAR.
/// </summary>
internal static class DuckDbEnvironmentReader
{
    /// <summary>
    /// Version, platform and the directory settings in one row. <c>pragma_version()</c> and
    /// <c>pragma_platform()</c> are the table-function forms, so they compose in a single SELECT.
    /// </summary>
    private const string ScalarsSql =
        "SELECT v.library_version, v.source_id, p.platform, " +
        "current_setting('extension_directory'), current_setting('home_directory') " +
        "FROM pragma_version() v, pragma_platform() p";

    /// <summary>
    /// The plural setting is a LIST. Unnesting it avoids parsing DuckDB's list rendering, which would
    /// be ambiguous for paths containing commas or brackets.
    /// </summary>
    private const string DirectoriesSql = "SELECT unnest(current_setting('extension_directories'))";

    internal static DuckDbEnvironment Read(DuckDBConnection connection)
    {
        string?[] scalars = DuckDbSql.QueryFirstRow(connection, ScalarsSql, 5);

        return new DuckDbEnvironment
        {
            LibraryVersion = scalars[0] ?? "",
            SourceId = scalars[1] ?? "",
            Platform = scalars[2] ?? "",
            ExtensionDirectorySetting = scalars[3] ?? "",
            HomeDirectory = ResolveHomeDirectory(scalars[4] ?? ""),
            ExtensionDirectoriesSetting = DuckDbSql.QueryColumn(connection, DirectoriesSql),
        };
    }

    /// <summary>
    /// Mirrors <c>FileSystem::GetHomeDirectory</c> (file_system.cpp:333-349): the
    /// <c>home_directory</c> setting if non-empty, else the <c>USERPROFILE</c>/<c>HOME</c> environment
    /// variable. Deliberately the raw env var rather than a .NET special-folder lookup, so that a
    /// <c>~</c> resolves to the same place DuckDB itself would resolve it.
    /// </summary>
    private static string ResolveHomeDirectory(string homeDirectorySetting)
    {
        if (!string.IsNullOrEmpty(homeDirectorySetting))
        {
            return homeDirectorySetting;
        }

        return Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? "";
    }
}
