// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Apache.Arrow;
using Fabricator.Installer;

namespace Fabricator.Bridge;

/// <summary>What one uninstall did to ONE installed version. A column set of
/// <c>fabricator_uninstall_plugin()</c>.</summary>
/// <param name="Removed">False when the directory could not even be moved aside — the only outcome that
/// leaves the plugin discoverable, and therefore the one a caller has to look at.</param>
internal sealed record PluginUninstallResult(
    string Name, string Version, string Path, bool Removed, bool Purged, string Detail);

/// <summary>What one install did. Every field is a column of <c>fabricator_install_plugin()</c>.</summary>
internal sealed record PluginInstallResult(
    string Name, string Version, string Platform, string Destination,
    int Files, string Providers, bool Activated, string Detail);

/// <summary>
/// Installs a plugin archive into a plugin root and makes its PROVIDER usable in the same session.
/// </summary>
/// <remarks>
/// <para>The decidable half — manifest, layout, destination — lives in <see cref="PluginPackage"/> so tier 0
/// can pin it offline. What is here is the file I/O and the registry invalidation, i.e. the parts that need a
/// real filesystem and a real load context.</para>
/// <para><b>THE WRITE IS STAGE-THEN-MOVE, and that is what makes it safe rather than merely tidy.</b> Files
/// are extracted into <c>&lt;root&gt;/.staging/&lt;guid&gt;</c> and the finished directory is
/// <see cref="Directory.Move"/>d onto <c>&lt;root&gt;/&lt;name&gt;/&lt;version&gt;</c>. The move is atomic
/// (same volume, by construction) and a scan running concurrently in another process therefore never sees a
/// half-extracted plugin — the staging path is additionally hidden from
/// <see cref="PluginPaths.EnumerateCandidates"/> by its leading dot.</para>
/// <para>⚠ THE PUT-IF-ABSENT IS EXACT ON WINDOWS AND CONDITIONAL ON UNIX, so state it precisely rather
/// than claiming atomicity outright. Windows' <c>MoveFileEx</c> without <c>MOVEFILE_REPLACE_EXISTING</c>
/// fails whenever the destination exists; POSIX <c>rename</c> fails with <c>ENOTEMPTY</c> only when the
/// destination directory is NON-EMPTY, and silently replaces an empty one. It holds here because the
/// destination is only ever created by this same stage-then-move — i.e. fully populated, in one rename — so
/// a competing installer's directory is never empty for us to replace. It would NOT hold against something
/// that creates the directory first and fills it afterwards.</para>
/// <para>⚠ AN UPGRADE NEVER OVERWRITES A FILE. <c>LoadFromAssemblyPath</c> maps the assembly, which LOCKS it
/// on Windows, and the bridge's load context is created by hostfxr and is not collectible — so a loaded
/// assembly can never be replaced in-process at all. The version-stamped layout means an upgrade writes
/// BESIDE the running one and takes effect at next start; <c>replace</c> exists only for re-installing the
/// SAME version, and it MOVES the old directory aside rather than deleting in place, because moving a locked
/// file is permitted where deleting it is not.</para>
/// </remarks>
internal static class PluginInstall
{
    private const string StagingDirectory = ".staging";
    private const string TrashDirectory = ".trash";

    public static async Task<PluginInstallResult> InstallAsync(string archivePath, string? rootOverride, bool replace)
    {
        if (!HostSettings.AllowPluginInstall)
        {
            throw new InvalidOperationException(
                $"fabricator_install_plugin is disabled. It loads and runs arbitrary .NET code in this " +
                $"process from a path chosen in SQL, so it is opt-in: SET {HostSettings.AllowPluginInstallName} = true.");
        }
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("fabricator_install_plugin: the archive path is empty.");
        }
        // ⚠ LOCAL PATHS ONLY, deliberately, and refused rather than silently attempted: fetching an archive
        // from a URL would let one SQL statement pull executable code from the network into this process. The
        // caller downloads it first, where the usual controls apply.
        if (archivePath.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"fabricator_install_plugin: '{archivePath}' looks like a URL. Only local paths are accepted; " +
                "download the archive first.");
        }
        string archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException($"fabricator_install_plugin: no such archive '{archive}'.", archive);
        }

        string root = ResolveRoot(rootOverride);
        string platform = await ResolvePlatformAsync().ConfigureAwait(false);

        Directory.CreateDirectory(root);
        // Reclaim whatever a previous uninstall (or a replace) could not delete while it was loaded. Here
        // rather than on any read path: this is a moment the user is already managing the root.
        SweepTrash(root);
        string staging = Path.Combine(root, StagingDirectory, Guid.NewGuid().ToString("n"));
        PluginManifest manifest;
        int written;
        try
        {
            using (var zip = OpenArchive(archive))
            {
                manifest = ReadManifest(zip, archive);
                var files = PluginPackage.SelectFiles(zip.Entries.Select(e => e.FullName), platform, out var layoutError);
                if (layoutError != null)
                {
                    throw new InvalidOperationException($"fabricator_install_plugin: '{archive}': {layoutError}");
                }
                Directory.CreateDirectory(staging);
                written = Extract(zip, files, staging, archive);
            }
            // The archive can install cleanly and contain no plugin at all — the exact "install succeeded,
            // nothing happened" failure the scan report exists to remove. Checked BEFORE the move, so a
            // package that cannot work never becomes a directory somebody has to find and delete.
            string entry = Path.Combine(staging, manifest.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entry))
            {
                throw new InvalidOperationException(
                    $"fabricator_install_plugin: '{archive}' declares entryAssembly '{manifest.EntryAssembly}', " +
                    $"which is not present after merging 'any/' and '{platform}/'.");
            }
            Publish(root, staging, PluginPackage.DestinationFor(root, manifest), replace);
        }
        finally
        {
            TryDelete(staging);
        }

        string destination = PluginPackage.DestinationFor(root, manifest);
        var (providers, detail) = Activate(destination);
        return new PluginInstallResult(manifest.Name, manifest.Version, platform, destination, written,
                                       providers, providers.Length > 0,
                                       detail + PluginPackage.ContractSkew(manifest.AbstractionsVersion,
                                                                          FabricatorVersion.Contract));
    }

    private static string ResolveRoot(string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            return Path.GetFullPath(rootOverride);
        }
        // The FIRST configured root, which is the one a plain FABRICATOR_PLUGIN_DIR names and, unset, the
        // per-user default. Installing into the first rather than "the default" keeps a rig that redirects
        // the search self-consistent: what it installs is what it then scans.
        var roots = PluginPaths.ResolveRoots();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException(
                "fabricator_install_plugin: no plugin root is configured and the user's home directory could " +
                "not be resolved. Pass root := '<directory>' or set FABRICATOR_PLUGIN_DIR.");
        }
        return roots[0];
    }

    /// <summary>
    /// DuckDB's OWN platform string, asked of the running engine rather than derived from
    /// <c>RuntimeInformation</c>. The spelling (<c>osx_arm64</c>, and the <c>_gcc4</c> variants) is DuckDB's,
    /// so deriving it would be a second implementation free to drift from the one an archive is built against.
    /// </summary>
    private static async Task<string> ResolvePlatformAsync()
    {
        if (!Host.CanQuery)
        {
            throw new InvalidOperationException(
                "fabricator_install_plugin: the host did not register host_query, so DuckDB's platform string " +
                "cannot be read. This function is only callable from SQL, where it always is.");
        }
        using var stream = Host.Query("SELECT p.platform FROM pragma_platform() p");
        using var batch = await stream.ReadNextRecordBatchAsync().ConfigureAwait(false);
        if (batch is null || batch.Length == 0 || batch.Column(0) is not StringArray platforms)
        {
            throw new InvalidOperationException("fabricator_install_plugin: pragma_platform() returned nothing.");
        }
        return platforms.GetString(0) ?? string.Empty;
    }

    private static ZipArchive OpenArchive(string archive)
    {
        try
        {
            return ZipFile.OpenRead(archive);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                $"fabricator_install_plugin: '{archive}' is not a readable zip archive: {ex.Message}", ex);
        }
    }

    private static PluginManifest ReadManifest(ZipArchive zip, string archive)
    {
        var entry = zip.GetEntry(PluginPackage.ManifestFileName)
            ?? throw new InvalidOperationException(
                $"fabricator_install_plugin: '{archive}' has no '{PluginPackage.ManifestFileName}' at its root.");
        byte[] bytes;
        using (var s = entry.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            bytes = ms.ToArray();
        }
        if (!PluginPackage.TryParseManifest(bytes, out var manifest, out var error) || manifest is null)
        {
            throw new InvalidOperationException($"fabricator_install_plugin: '{archive}': {error}");
        }
        return manifest;
    }

    /// <summary>
    /// Writes the selected entries under <paramref name="staging"/>, validating each destination TWICE — the
    /// shared archive-path guard, then a containment check on the resolved absolute path. Identical to what
    /// <c>PayloadExtractor</c> does for the distribution payload, and the same guard object, so what the two
    /// refuse to write cannot drift apart.
    /// </summary>
    private static int Extract(ZipArchive zip, IReadOnlyList<PluginFileSelection> files, string staging, string archive)
    {
        string root = Path.GetFullPath(staging);
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        int written = 0;
        foreach (var file in files)
        {
            if (!ArchivePath.TryNormalize(file.Relative, out string relative, out string? error))
            {
                throw new InvalidOperationException(
                    $"fabricator_install_plugin: '{archive}' contains an unsafe entry '{file.Entry}': {error}");
            }
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"fabricator_install_plugin: '{archive}' entry '{file.Entry}' resolves outside the " +
                    "extraction directory.");
            }
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            var entry = zip.GetEntry(file.Entry)
                ?? throw new InvalidOperationException(
                    $"fabricator_install_plugin: '{archive}' entry '{file.Entry}' vanished while reading.");
            entry.ExtractToFile(target, overwrite: true);
            written++;
        }
        return written;
    }

    private static void Publish(string root, string staging, string destination, bool replace)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (Directory.Exists(destination))
        {
            if (!replace)
            {
                throw new InvalidOperationException(
                    $"fabricator_install_plugin: '{destination}' already exists. Install a new version, or " +
                    "pass replace := true to re-install this one.");
            }
            // MOVED, not deleted: an assembly already loaded from here is LOCKED on Windows and cannot be
            // deleted, but it can be renamed out of the way. The old copy stays loaded in this process (it
            // cannot be unloaded either) — the point is only that the next scan, in this process or the next,
            // sees the new files.
            string trash = Path.Combine(root, TrashDirectory, Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path.Combine(root, TrashDirectory));
            Directory.Move(destination, trash);
            TryDelete(trash);
        }
        try
        {
            Directory.Move(staging, destination);
        }
        catch (IOException ex)
        {
            // The put-if-absent losing: another process published this exact version between the check above
            // and this move. Reported as the collision it is rather than as a filesystem error.
            throw new InvalidOperationException(
                $"fabricator_install_plugin: could not publish to '{destination}' — another process may have " +
                $"installed it concurrently: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Drops the memoized provider map and FORCES the re-scan, then reports what the new scan made of the
    /// directory just written.
    /// </summary>
    /// <remarks>
    /// Forcing the scan here rather than leaving it to the next ATTACH is what lets this function answer
    /// honestly: <c>activated</c> is read out of the scan report, so "installed" and "installed and usable"
    /// are distinguishable in the same row — and a plugin that installs but cannot load says so immediately,
    /// with its exception, instead of at whatever later statement first needed it.
    /// </remarks>
    private static (string Providers, string Detail) Activate(string destination)
    {
        BackendRegistry.Invalidate();
        _ = BackendRegistry.All().Count(); // forces Discover(), i.e. a fresh plugin scan
        string prefix = destination.EndsWith(Path.DirectorySeparatorChar)
            ? destination
            : destination + Path.DirectorySeparatorChar;
        var mine = PluginPaths.Report()
            .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var providers = mine.Where(e => e.Status == PluginScanStatus.Loaded)
                            .SelectMany(e => e.Provider.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
        if (providers.Length > 0)
        {
            return (string.Join(",", providers),
                    "provider(s) registered; global functions declared by this plugin appear at next start");
        }
        var rejected = mine.FirstOrDefault(e => e.Status == PluginScanStatus.Rejected);
        if (rejected.Path is { Length: > 0 })
        {
            return (string.Empty, $"no provider registered: {rejected.Detail}");
        }
        // An install into a root NOBODY SCANS is legitimate — provisioning another machine, or a root a
        // later FABRICATOR_PLUGIN_DIR will name — but it must not be reported with the same words as a
        // plugin that was scanned and declared nothing. The two look identical from the report (no rows
        // either way) and mean completely different things.
        bool scanned = PluginPaths.ResolveRoots().Any(
            r => destination.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (!scanned)
        {
            return (string.Empty,
                    "installed outside every configured plugin root, so nothing scanned it — it will load " +
                    "when a root containing it is configured (FABRICATOR_PLUGIN_DIR)");
        }
        return (string.Empty,
                "installed, but the scan registered no provider from it — SELECT * FROM fabricator_plugins() " +
                "for the per-file reason");
    }

    /// <summary>
    /// Uninstalls every installed version of <paramref name="name"/>, or one named version.
    /// </summary>
    /// <remarks>
    /// <para><b>IT IS A MOVE, NOT A DELETE, AND THAT IS THE WHOLE MECHANISM.</b> An assembly loaded from a
    /// file is LOCKED on Windows and the bridge's load context is not collectible, so a plugin that has been
    /// used in this process CANNOT be deleted. It CAN be renamed. Moving the version directory into
    /// <c>&lt;root&gt;/.trash/&lt;guid&gt;</c> takes it out of the scan immediately — the trash path is hidden
    /// from <see cref="PluginPaths.EnumerateCandidates"/> by its leading dot — which is the mark-for-deletion
    /// the design called for, arrived at without inventing a marker file.</para>
    /// <para>The bytes then go whenever they can: a best-effort delete right away, and a sweep of the whole
    /// trash on the next install or uninstall, by which time a restart has usually released the lock. So the
    /// row reports TWO things and they are not the same question — <c>Removed</c> (is it out of the scan?)
    /// and <c>Purged</c> (are the bytes gone?). Only the first is a failure when false.</para>
    /// <para>⚠ The PROVIDER disappears from this session, but the ASSEMBLY does not: it stays loaded, because
    /// nothing can unload it. What the re-scan does is stop finding a candidate for it, so it is not
    /// registered into the fresh map — an uninstalled provider stops resolving at ATTACH even though its code
    /// is still in memory. That is the strongest guarantee available here, and it is worth stating rather
    /// than implying the code is gone.</para>
    /// </remarks>
    public static IReadOnlyList<PluginUninstallResult> Uninstall(string name, string? version, string? rootOverride)
    {
        if (!HostSettings.AllowPluginInstall)
        {
            throw new InvalidOperationException(
                $"fabricator_uninstall_plugin is disabled. It removes files under a plugin root from a path " +
                $"chosen in SQL, so it rides the same opt-in as installing: " +
                $"SET {HostSettings.AllowPluginInstallName} = true.");
        }
        if (string.IsNullOrWhiteSpace(name) || !PluginPackage.IsSafeSegment(name))
        {
            throw new ArgumentException(
                $"fabricator_uninstall_plugin: '{name}' is not a usable plugin name.");
        }
        if (version != null && !PluginPackage.IsSafeSegment(version))
        {
            throw new ArgumentException(
                $"fabricator_uninstall_plugin: '{version}' is not a usable version.");
        }
        string root = ResolveRoot(rootOverride);
        SweepTrash(root);
        string pluginDir = Path.Combine(root, name);
        if (!Directory.Exists(pluginDir))
        {
            throw new InvalidOperationException(
                $"fabricator_uninstall_plugin: '{name}' is not installed under '{root}'.");
        }
        var versions = version is null
            ? Directory.GetDirectories(pluginDir).Select(Path.GetFileName).Where(v => v is { Length: > 0 })
                       .OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray()!
            : new[] { version };
        var results = new List<PluginUninstallResult>();
        foreach (var v in versions)
        {
            string dir = Path.Combine(pluginDir, v!);
            if (!Directory.Exists(dir))
            {
                throw new InvalidOperationException(
                    $"fabricator_uninstall_plugin: '{name}' version '{v}' is not installed under '{root}'.");
            }
            string trash = Path.Combine(root, TrashDirectory, Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path.Combine(root, TrashDirectory));
            try
            {
                Directory.Move(dir, trash);
            }
            catch (Exception ex)
            {
                // The ONE outcome that leaves the plugin discoverable. Reported per version rather than
                // thrown, so uninstalling several versions does not stop at the first locked one.
                results.Add(new PluginUninstallResult(name, v!, dir, Removed: false, Purged: false,
                                                      $"could not move it out of the scan: {ex.Message}"));
                continue;
            }
            bool purged = TryDelete(trash);
            results.Add(new PluginUninstallResult(
                name, v!, dir, Removed: true, purged,
                purged
                    ? "removed"
                    : "removed from the scan; the files are held by this process (a loaded assembly cannot be " +
                      "deleted) and are swept on a later install or uninstall"));
        }
        // Leave no empty <root>/<name>/ behind — it would read as an installed plugin with no versions.
        try
        {
            if (Directory.Exists(pluginDir) && Directory.GetFileSystemEntries(pluginDir).Length == 0)
            {
                Directory.Delete(pluginDir);
            }
        }
        catch
        {
        }
        BackendRegistry.Invalidate();
        _ = BackendRegistry.All().Count(); // force the re-scan, so the provider stops resolving NOW
        return results;
    }

    /// <summary>
    /// Best-effort deletion of everything parked in <c>&lt;root&gt;/.trash</c>.
    /// </summary>
    /// <remarks>
    /// Called at the START of an install and an uninstall — i.e. at the moments a user is already managing
    /// this root, and the moments most likely to follow a restart that released whatever lock kept the bytes
    /// alive. Never on a read path: sweeping during a scan would put filesystem writes on the ATTACH path.
    /// Failures are silent BY DESIGN; anything left is inert, since the trash is hidden from the scan.
    /// </remarks>
    private static void SweepTrash(string root)
    {
        string trash = Path.Combine(root, TrashDirectory);
        if (!Directory.Exists(trash))
        {
            return;
        }
        try
        {
            foreach (var dir in Directory.GetDirectories(trash))
            {
                TryDelete(dir);
            }
        }
        catch
        {
        }
    }

    /// <summary>Best-effort recursive delete. Returns whether the directory is gone — the uninstall path
    /// needs that answer, because "out of the scan" and "bytes reclaimed" are different questions and a
    /// loaded assembly makes the second one frequently No.</summary>
    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            return true;
        }
        catch
        {
            // Best effort. A staging or trash directory we cannot remove is inert: both are hidden from the
            // scan by their leading dot, so leaving one costs disk and nothing else.
            return false;
        }
    }
}
