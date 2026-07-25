namespace Fabricator.Installer;

/// <summary>
/// The on-disk names the installer writes into DuckDB's extension directory. These are a
/// contract with the C++ side, not free choices:
/// <list type="bullet">
/// <item><see cref="CoreFile"/> — DuckDB derives an extension's entry symbol from the FILE NAME
/// (<c>&lt;filebase&gt;_duckdb_cpp_init</c>, extension_load.cpp:633), so the core loadable must be
/// named exactly this and must export <c>fabricator_core_duckdb_cpp_init</c>.</item>
/// <item><see cref="ManagedDirectory"/> — clr_host's zero-configuration lookup is a folder
/// literally named <c>fabricator</c> beside the loaded module (clr_host.cpp:341-345). Renaming it
/// would force <c>FABRICATOR_MANAGED_DIR</c> back into the happy path.</item>
/// </list>
/// </summary>
public static class FabricatorPayloadNames
{
    /// <summary>The extracted C++ loadable (CPP ABI, exact-DuckDB-version-locked).</summary>
    public const string CoreFile = "fabricator_core.duckdb_extension";

    /// <summary>The extracted managed directory (bridge assemblies, optionally a .NET runtime).</summary>
    public const string ManagedDirectory = "fabricator";

    /// <summary>
    /// Idempotence marker: holds the payload SHA-256 of the currently extracted payload. Written
    /// LAST so its presence implies a complete extraction.
    /// </summary>
    public const string MarkerFile = "fabricator_core.payload.sha";

    /// <summary>Cross-process extraction lock. Left in place (never deleted) to avoid the
    /// unlink-while-locked race that would let two processes both believe they hold it.</summary>
    public const string LockFile = ".fabricator.lock";

    /// <summary>Prefix of a transient extraction directory; swept on the next slow path.</summary>
    public const string StagingPrefix = ".fabricator.staging.";

    /// <summary>Prefix of a directory holding files renamed aside during an upgrade.</summary>
    public const string SupersededPrefix = ".fabricator.old.";
}
