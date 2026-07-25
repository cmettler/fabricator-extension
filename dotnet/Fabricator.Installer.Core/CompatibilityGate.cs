namespace Fabricator.Installer;

/// <summary>
/// Checks an artifact against the running DuckDB BEFORE anything is extracted.
/// </summary>
/// <remarks>
/// The inner core uses the CPP ABI, which DuckDB version-checks exactly (extension.cpp:51-58) —
/// unavoidable, since the core builds on catalog/storage/optimizer internals. Without this gate the
/// user meets DuckDB's generic footer-mismatch error after a pointless multi-megabyte extraction;
/// with it they get told which artifact to download, and the disk is never touched.
/// </remarks>
public static class CompatibilityGate
{
    /// <summary>
    /// Returns null when the artifact matches, otherwise a complete user-facing message.
    /// </summary>
    public static string? Check(PayloadManifest manifest, DuckDbEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(environment);

        string expectedVersion = ExtensionDirectoryResolver.NormalizeVersionTag(manifest.TargetDuckDbVersion);
        string actualVersion = ExtensionDirectoryResolver.NormalizeVersionTag(environment.LibraryVersion);

        bool versionMatches = expectedVersion.Length > 0
            && string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal);
        bool platformMatches = manifest.Platform.Length > 0
            && string.Equals(manifest.Platform, environment.Platform, StringComparison.Ordinal);

        if (versionMatches && platformMatches)
        {
            return null;
        }

        string mismatch = (versionMatches, platformMatches) switch
        {
            (false, true) => $"targets DuckDB {Describe(expectedVersion)}, but this process is DuckDB {Describe(actualVersion)}",
            (true, false) => $"targets platform {Describe(manifest.Platform)}, but this process reports {Describe(environment.Platform)}",
            _ => $"targets DuckDB {Describe(expectedVersion)} on {Describe(manifest.Platform)}, " +
                 $"but this process is DuckDB {Describe(actualVersion)} on {Describe(environment.Platform)}",
        };

        return $"This fabricator distribution {mismatch}. The extension core is built against DuckDB internals and " +
               $"cannot load into a different version, so install the fabricator artifact built for " +
               $"{Describe(actualVersion)}/{Describe(environment.Platform)}.";
    }

    private static string Describe(string value) => value.Length == 0 ? "<unknown>" : value;
}
