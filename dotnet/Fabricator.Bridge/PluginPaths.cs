using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Fabricator.Bridge;

/// <summary>What one candidate file (or one root) contributed to the plugin scan. Purely descriptive: the
/// scan records these as it goes and <c>fabricator_plugins()</c> reads them back, because the scan runs ONCE
/// per process (behind <see cref="BackendRegistry"/>'s memoized map) and cannot be replayed on demand.</summary>
/// <remarks>
/// ⚠ THIS EXISTS BECAUSE THE SCAN USED TO FAIL SILENTLY, which is the single worst property of a plugin
/// system: <c>ScanPluginDirectories</c> ends every candidate in a <c>catch</c>, so a plugin built against a
/// different Apache.Arrow major, or missing a private dependency, was skipped with NO signal at all — and
/// this repo already records that a failing <c>verify_plugin</c> is indistinguishable from "the plugin
/// loaded and chose to register nothing". A plugin INSTALLER makes that the normal failure mode ("install
/// succeeded, nothing happened"), so the report is a prerequisite for one rather than a nicety.
/// </remarks>
internal readonly record struct PluginScanEntry(
    string Root, string Path, string Status, string Provider, string Detail);

/// <summary>Status values used by <see cref="PluginScanEntry.Status"/>. Strings rather than an enum because
/// they cross into SQL as a column and are the user-facing vocabulary of a diagnostic.</summary>
internal static class PluginScanStatus
{
    /// <summary>A root that exists and was searched. <c>Detail</c> carries the candidate count.</summary>
    public const string Root = "root";

    /// <summary>A configured root that does not exist. The most common real cause of "my plugin is not
    /// found", and previously invisible: the scan silently filtered non-existent roots away.</summary>
    public const string RootMissing = "root_missing";

    /// <summary>Loaded and contributed at least one provider. <c>Provider</c> lists their names.</summary>
    public const string Loaded = "loaded";

    /// <summary>Loaded, but declared no <c>IBackend</c>. Usually a private dependency of a plugin rather
    /// than a plugin — benign, and worth showing so it is not mistaken for a failure.</summary>
    public const string NoBackend = "no_backend";

    /// <summary>Skipped because the host context already has an assembly of that simple name (the shared
    /// set — Fabricator.Bridge, Apache.Arrow, the built-in providers). Deliberate, not a failure.</summary>
    public const string Shared = "shared";

    /// <summary>Could not be loaded or reflected. <c>Detail</c> carries the exception message — this is the
    /// row that turns "install succeeded, nothing happened" into an answer.</summary>
    public const string Rejected = "rejected";
}

/// <summary>
/// Where plugin assemblies are looked for, and what the last scan found. BCL-only on purpose so tier 0 can
/// link it (see the admission rule in <c>Fabricator.Bridge.Tests.csproj</c>) — the path precedence is the
/// part most likely to be got wrong and the cheapest to pin.
/// </summary>
internal static class PluginPaths
{
    private static readonly object Gate = new();
    private static List<PluginScanEntry> _report = new();

    /// <summary>The per-user plugin root, relative to the user's home: <c>.duckdb/fabricator/plugins</c>.</summary>
    /// <remarks>
    /// ⚠ DELIBERATELY NOT INSIDE THE MANAGED DIRECTORY, and this is a MEASURED hazard rather than taste.
    /// Several projects publish into the managed dir and <c>dotnet publish</c> DELETES files its own previous
    /// publish wrote whose closure no longer contains them — that is what silently removed five
    /// Microsoft.Data.SqlClient DLLs from a populated payload on 2026-08-18. A plugin installed under the
    /// managed dir would be wiped by an ordinary <c>publish-managed.ps1</c> run, with no error.
    /// <para><c>~/.duckdb</c> is DuckDB's own per-user directory (it is where <c>INSTALL</c> puts
    /// extensions), it is writable without admin, and it is stable while the managed dir is not: that one
    /// moves between a build tree, <c>~/.duckdb/extensions/&lt;version&gt;/&lt;platform&gt;/</c> and the
    /// single-file distribution's cache, and can be pointed anywhere by FABRICATOR_MANAGED_DIR.</para>
    /// </remarks>
    public const string DefaultRelativeRoot = ".duckdb/fabricator/plugins";

    /// <summary>
    /// Resolves the roots to search, in order. <paramref name="env"/> is FABRICATOR_PLUGIN_DIR (a
    /// comma-separated list); when it names anything at all it wins OUTRIGHT — the default is not appended.
    /// </summary>
    /// <remarks>
    /// ⚠ THE OVERRIDE REPLACES RATHER THAN EXTENDS, which is the same shape as the variable's existing
    /// meaning and the safer of the two: a test rig or a CI job that narrows the search must actually get a
    /// narrow search, or it is not testing what it says. It is also why the default root is REPORTED — with
    /// an override in force the default is silently not searched, and that should be visible rather than
    /// inferred.
    /// </remarks>
    public static IReadOnlyList<string> ResolveRoots(string? env, string? userHome)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var part in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(roots, part);
            }
            return new ReadOnlyCollection<string>(roots);
        }
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            Add(roots, Path.Combine(userHome, DefaultRelativeRoot.Replace('/', Path.DirectorySeparatorChar)));
        }
        return new ReadOnlyCollection<string>(roots);
    }

    /// <summary><see cref="ResolveRoots(string?, string?)"/> against the live environment.</summary>
    public static IReadOnlyList<string> ResolveRoots() => ResolveRoots(
        Environment.GetEnvironmentVariable("FABRICATOR_PLUGIN_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static void Add(List<string> roots, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return; // an unusable path string is not a root; the caller records nothing for it
        }
        if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(full);
        }
    }

    /// <summary>
    /// The candidate assemblies under one root, RECURSIVELY and in a stable order.
    /// </summary>
    /// <remarks>
    /// ⚠ RECURSION IS THE POINT: the scan searched only a root's top level, so a plugin laid out the way an
    /// installer would write one — <c>&lt;root&gt;/&lt;name&gt;/&lt;version&gt;/&lt;platform&gt;/*.dll</c> —
    /// was never seen. Ordering is by path so the scan is deterministic, which matters because the FIRST
    /// provider registered under a name wins and <c>Directory</c> enumeration order is filesystem-dependent.
    /// <para>No cap on the count, deliberately: a self-contained plugin can carry hundreds of DLLs and most
    /// will be rejected, but a silent truncation would read as "covered everything" — every one of them gets
    /// a row in the report instead.</para>
    /// <para>⚠ A PATH SEGMENT BEGINNING WITH '.' IS NOT SEARCHED, and this is load-bearing rather than
    /// tidiness: the installer stages an extraction under <c>&lt;root&gt;/.staging/&lt;guid&gt;</c> and parks
    /// a replaced version under <c>&lt;root&gt;/.trash/&lt;guid&gt;</c>, both INSIDE the root so the final
    /// <c>Directory.Move</c> stays on one volume and is therefore atomic. Without this rule a scan running in
    /// another process could load a half-extracted plugin, or reload one that has just been replaced. It is
    /// the same hidden-name convention Delta uses for <c>_</c>/<c>.</c> prefixes, applied per segment.</para>
    /// </remarks>
    public static IReadOnlyList<string> EnumerateCandidates(string root)
    {
        try
        {
            var files = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
                                 .Where(f => !HasHiddenSegment(root, f))
                                 .ToArray();
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return new ReadOnlyCollection<string>(files);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>True when any path segment BELOW <paramref name="root"/> starts with '.'. The root itself is
    /// exempt — a user may legitimately put their plugins under a dotted directory (the DEFAULT root is
    /// <c>~/.duckdb/...</c>, which is precisely such a path), so only what the installer creates below it is
    /// hidden.</summary>
    private static bool HasHiddenSegment(string root, string file)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, file);
        }
        catch
        {
            return false;
        }
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length > 0 && segment[0] == '.')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Replaces the recorded report. Called once, by the scan.</summary>
    public static void SetReport(IEnumerable<PluginScanEntry> entries)
    {
        var list = entries.ToList();
        lock (Gate)
        {
            _report = list;
        }
    }

    /// <summary>The recorded report. Empty until the scan has run — which, in practice, it always has by the
    /// time this is reachable from SQL: registering <c>fabricator_plugins()</c> itself enumerates the global
    /// functions, and that walks the backend registry, which is what triggers the scan.</summary>
    public static IReadOnlyList<PluginScanEntry> Report()
    {
        lock (Gate)
        {
            return new ReadOnlyCollection<PluginScanEntry>(_report.ToList());
        }
    }
}
