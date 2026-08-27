// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer;

/// <summary>What <see cref="PayloadInstaller.Ensure"/> had to do.</summary>
public enum InstallOutcome
{
    /// <summary>The payload was already extracted and current; no lock was taken. The steady state.</summary>
    AlreadyCurrent,

    /// <summary>This call extracted the payload.</summary>
    Extracted,

    /// <summary>Another process extracted it while this call waited for the lock.</summary>
    ExtractedByAnotherProcess,
}

/// <summary>Inputs for <see cref="PayloadInstaller.Ensure"/>.</summary>
public sealed class InstallRequest
{
    /// <summary>DuckDB's extension directory (see <see cref="ExtensionDirectoryResolver"/>).</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>The artifact's manifest.</summary>
    public required PayloadManifest Manifest { get; init; }

    /// <summary>Opens a fresh, seekable stream over the payload archive (see <see cref="PolyglotPackage.OpenPayload"/>).</summary>
    public required Func<Stream> OpenPayload { get; init; }

    /// <summary>How long to wait for a concurrent extraction to finish.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Verify the payload SHA-256 before extracting. Slow path only; on by default.</summary>
    public bool VerifyPayloadHash { get; init; } = true;
}

/// <summary>Where the payload ended up.</summary>
public sealed record InstallResult(InstallOutcome Outcome, string CorePath, string ManagedDirectory);

/// <summary>
/// Makes DuckDB's extension directory contain the current payload — the core loadable plus the
/// managed directory beside it — and nothing else changes on disk.
/// </summary>
/// <remarks>
/// Ordering is what makes this safe to run concurrently and to interrupt at any point:
/// extract into a private staging directory, move into place, and write the marker LAST. The marker
/// therefore means "a complete payload with this SHA is present", never "an extraction was started".
/// </remarks>
public static class PayloadInstaller
{
    /// <summary>Path the core loadable will occupy; what the shell passes to <c>LOAD</c>.</summary>
    public static string GetCorePath(string targetDirectory, PayloadManifest manifest)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        return Path.Combine(Path.GetFullPath(targetDirectory), ValidateName(manifest.CoreFileName, nameof(manifest.CoreFileName)));
    }

    /// <summary>Extracts the payload if it is not already present and current.</summary>
    public static InstallResult Ensure(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.TargetDirectory);
        ArgumentNullException.ThrowIfNull(request.Manifest);
        ArgumentNullException.ThrowIfNull(request.OpenPayload);

        PayloadManifest manifest = request.Manifest;
        string coreName = ValidateName(manifest.CoreFileName, nameof(manifest.CoreFileName));
        string managedName = ValidateName(manifest.ManagedDirectoryName, nameof(manifest.ManagedDirectoryName));

        if (string.IsNullOrWhiteSpace(manifest.PayloadSha256))
        {
            throw new InstallerException("The fabricator payload manifest carries no payload SHA-256.");
        }

        string directory = Path.GetFullPath(request.TargetDirectory);
        string corePath = Path.Combine(directory, coreName);
        string managedPath = Path.Combine(directory, managedName);
        string markerPath = Path.Combine(directory, FabricatorPayloadNames.MarkerFile);

        Directory.CreateDirectory(directory);

        if (IsCurrent(markerPath, corePath, managedPath, manifest))
        {
            return new InstallResult(InstallOutcome.AlreadyCurrent, corePath, managedPath);
        }

        using CrossProcessLock installLock = CrossProcessLock.Acquire(
            Path.Combine(directory, FabricatorPayloadNames.LockFile), request.LockTimeout);

        // Re-check under the lock: whoever we queued behind may have installed exactly this payload.
        if (IsCurrent(markerPath, corePath, managedPath, manifest))
        {
            return new InstallResult(InstallOutcome.ExtractedByAnotherProcess, corePath, managedPath);
        }

        SweepTransients(directory);

        string staging = Path.Combine(directory, FabricatorPayloadNames.StagingPrefix + Token());
        try
        {
            Directory.CreateDirectory(staging);

            using (Stream payload = request.OpenPayload())
            {
                if (request.VerifyPayloadHash)
                {
                    VerifyPayload(payload, manifest);
                }

                PayloadExtractor.Extract(payload, staging);
            }

            ValidateStaged(staging, coreName, managedName);
            Promote(directory, staging, coreName, managedName);
            WriteMarker(markerPath, manifest.PayloadSha256);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        return new InstallResult(InstallOutcome.Extracted, corePath, managedPath);
    }

    /// <summary>
    /// The fast path. Requires the marker to match AND the files it vouches for to be present, so a
    /// payload someone half-deleted by hand is repaired instead of trusted.
    /// </summary>
    private static bool IsCurrent(string markerPath, string corePath, string managedPath, PayloadManifest manifest)
    {
        string? marker = ReadMarker(markerPath);
        if (marker is null || !marker.Equals(manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var core = new FileInfo(corePath);
        if (!core.Exists || core.Length == 0)
        {
            return false;
        }

        return Directory.Exists(managedPath) && Directory.EnumerateFiles(managedPath).Any();
    }

    private static string? ReadMarker(string markerPath)
    {
        try
        {
            return File.ReadAllText(markerPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string markerPath, string sha)
    {
        string temporary = markerPath + ".tmp";
        File.WriteAllText(temporary, sha);
        File.Move(temporary, markerPath, overwrite: true);
    }

    private static void VerifyPayload(Stream payload, PayloadManifest manifest)
    {
        payload.Position = 0;

        if (payload.CanSeek && manifest.PayloadLength > 0 && payload.Length != manifest.PayloadLength)
        {
            throw new InstallerException(
                $"The fabricator payload is {payload.Length} bytes but the manifest declares {manifest.PayloadLength} — " +
                "the artifact is truncated or damaged. Re-download it.");
        }

        string actual = Hashing.Sha256Hex(payload);
        if (!actual.Equals(manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException(
                $"The fabricator payload SHA-256 is {actual} but the manifest declares {manifest.PayloadSha256} — " +
                "the artifact is corrupt. Re-download it.");
        }

        payload.Position = 0;
    }

    private static void ValidateStaged(string staging, string coreName, string managedName)
    {
        var core = new FileInfo(Path.Combine(staging, coreName));
        if (!core.Exists || core.Length == 0)
        {
            throw new InstallerException($"The fabricator payload does not contain '{coreName}'.");
        }

        string managed = Path.Combine(staging, managedName);
        if (!Directory.Exists(managed) || !Directory.EnumerateFiles(managed, "*", SearchOption.AllDirectories).Any())
        {
            throw new InstallerException($"The fabricator payload does not contain a non-empty '{managedName}' directory.");
        }
    }

    /// <summary>
    /// Moves the staged payload into place, displacing anything already there.
    /// </summary>
    /// <remarks>
    /// Old files are RENAMED aside rather than deleted. Windows refuses to delete a library that
    /// another process still has mapped, but it does permit renaming it — the loader opens image
    /// files with <c>FILE_SHARE_DELETE</c> — so an upgrade while another DuckDB session holds the
    /// previous core loaded succeeds, and the displaced file is swept on the next slow path. Renaming
    /// first also means the target is never briefly absent.
    /// </remarks>
    private static void Promote(string directory, string staging, string coreName, string managedName)
    {
        string aside = Path.Combine(directory, FabricatorPayloadNames.SupersededPrefix + Token());
        bool asideUsed = false;

        MoveAside(Path.Combine(directory, coreName), aside, ref asideUsed);
        MoveAside(Path.Combine(directory, managedName), aside, ref asideUsed);

        File.Move(Path.Combine(staging, coreName), Path.Combine(directory, coreName));
        Directory.Move(Path.Combine(staging, managedName), Path.Combine(directory, managedName));

        if (asideUsed)
        {
            TryDeleteDirectory(aside);
        }
    }

    private static void MoveAside(string path, string asideDirectory, ref bool asideUsed)
    {
        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(asideDirectory);
        asideUsed = true;
        string destination = Path.Combine(asideDirectory, Path.GetFileName(path));

        try
        {
            if (isDirectory)
            {
                Directory.Move(path, destination);
            }
            else
            {
                File.Move(path, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallerException(
                $"Cannot replace '{path}': it is in use by another process. Close other DuckDB sessions that have " +
                $"fabricator loaded and retry. ({ex.Message})",
                ex);
        }
    }

    /// <summary>Removes leftovers from interrupted runs and from upgrades whose displaced files were still mapped.</summary>
    private static void SweepTransients(string directory)
    {
        foreach (string prefix in new[] { FabricatorPayloadNames.StagingPrefix, FabricatorPayloadNames.SupersededPrefix })
        {
            IEnumerable<string> leftovers;
            try
            {
                leftovers = Directory.EnumerateDirectories(directory, prefix + "*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string leftover in leftovers)
            {
                TryDeleteDirectory(leftover);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a still-mapped file keeps its directory alive. Swept on a later run.
        }
    }

    /// <summary>
    /// Rejects a manifest name that is anything but a plain file/directory name. The manifest is
    /// attacker-controlled input in the same sense the archive is, and these names are joined onto
    /// the extension directory.
    /// </summary>
    private static string ValidateName(string name, string field)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/') || name.Contains('\\')
            || name is "." or ".."
            || name != Path.GetFileName(name))
        {
            throw new InstallerException($"The fabricator payload manifest has an invalid {field} '{name}'.");
        }

        return name;
    }

    private static string Token() => Guid.NewGuid().ToString("N");
}
