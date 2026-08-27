// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.IO.Compression;

namespace Fabricator.Installer.Tests;

public sealed class PayloadInstallerTests
{
    [Fact]
    public void Ensure_FirstRun_ExtractsCoreManagedDirAndMarker()
    {
        using var fixture = new InstallFixture();

        InstallResult result = fixture.Install();

        Assert.Equal(InstallOutcome.Extracted, result.Outcome);
        Assert.Equal(TestArtifact.CoreContentDefault, File.ReadAllText(result.CorePath));
        Assert.True(File.Exists(Path.Combine(result.ManagedDirectory, "Fabricator.Bridge.dll")));
        Assert.True(File.Exists(Path.Combine(result.ManagedDirectory, "nested", "runtime.json")));
        Assert.Equal(fixture.Manifest.PayloadSha256, File.ReadAllText(fixture.MarkerPath));
    }

    /// <summary>
    /// The core must land in the extension directory under its own name, because DuckDB derives an
    /// extension's entry symbol from the file name.
    /// </summary>
    [Fact]
    public void Ensure_PlacesTheCoreWhereGetCorePathPromises()
    {
        using var fixture = new InstallFixture();

        InstallResult result = fixture.Install();

        Assert.Equal(PayloadInstaller.GetCorePath(fixture.TargetDirectory, fixture.Manifest), result.CorePath);
        Assert.Equal(FabricatorPayloadNames.CoreFile, Path.GetFileName(result.CorePath));
        Assert.Equal(FabricatorPayloadNames.ManagedDirectory, Path.GetFileName(result.ManagedDirectory));
    }

    [Fact]
    public void Ensure_SecondRun_TakesTheFastPathAndTouchesNothing()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();
        DateTime coreWritten = File.GetLastWriteTimeUtc(first.CorePath);

        InstallResult second = fixture.Install();

        Assert.Equal(InstallOutcome.AlreadyCurrent, second.Outcome);
        Assert.Equal(coreWritten, File.GetLastWriteTimeUtc(second.CorePath));
    }

    /// <summary>The fast path must not even create the lock file — it is meant to be nearly free.</summary>
    [Fact]
    public void Ensure_FastPath_DoesNotTakeTheLock()
    {
        using var fixture = new InstallFixture();
        fixture.Install();
        File.Delete(Path.Combine(fixture.TargetDirectory, FabricatorPayloadNames.LockFile));

        Assert.Equal(InstallOutcome.AlreadyCurrent, fixture.Install().Outcome);
        Assert.False(File.Exists(Path.Combine(fixture.TargetDirectory, FabricatorPayloadNames.LockFile)));
    }

    [Fact]
    public void Ensure_DifferentPayload_UpgradesInPlace()
    {
        using var fixture = new InstallFixture();
        fixture.Install();

        InstallResult upgraded = fixture.InstallVariant("fake core loadable v2");

        Assert.Equal(InstallOutcome.Extracted, upgraded.Outcome);
        Assert.Equal("fake core loadable v2", File.ReadAllText(upgraded.CorePath));
    }

    /// <summary>Stale files from the previous payload must not survive the upgrade.</summary>
    [Fact]
    public void Ensure_Upgrade_ReplacesTheManagedDirectoryRatherThanMergingIt()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();
        string stale = Path.Combine(first.ManagedDirectory, "StaleFromV1.dll");
        File.WriteAllText(stale, "obsolete");

        fixture.InstallVariant("fake core loadable v2");

        Assert.False(File.Exists(stale));
    }

    /// <summary>A marker that vouches for files someone deleted by hand must not be trusted.</summary>
    [Fact]
    public void Ensure_RepairsAMissingCoreFile()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();
        File.Delete(first.CorePath);

        Assert.Equal(InstallOutcome.Extracted, fixture.Install().Outcome);
        Assert.True(File.Exists(first.CorePath));
    }

    [Fact]
    public void Ensure_RepairsAnEmptyCoreFile()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();
        File.WriteAllBytes(first.CorePath, []);

        Assert.Equal(InstallOutcome.Extracted, fixture.Install().Outcome);
        Assert.Equal(TestArtifact.CoreContentDefault, File.ReadAllText(first.CorePath));
    }

    [Fact]
    public void Ensure_RepairsAMissingManagedDirectory()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();
        Directory.Delete(first.ManagedDirectory, recursive: true);

        Assert.Equal(InstallOutcome.Extracted, fixture.Install().Outcome);
        Assert.True(File.Exists(Path.Combine(first.ManagedDirectory, "Fabricator.Bridge.dll")));
    }

    [Fact]
    public void Ensure_RepairsAnEmptyManagedDirectory()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();
        Directory.Delete(first.ManagedDirectory, recursive: true);
        Directory.CreateDirectory(first.ManagedDirectory);

        Assert.Equal(InstallOutcome.Extracted, fixture.Install().Outcome);
    }

    [Fact]
    public void Ensure_RepairsACorruptMarker()
    {
        using var fixture = new InstallFixture();
        fixture.Install();
        File.WriteAllText(fixture.MarkerPath, "not-a-sha");

        Assert.Equal(InstallOutcome.Extracted, fixture.Install().Outcome);
        Assert.Equal(fixture.Manifest.PayloadSha256, File.ReadAllText(fixture.MarkerPath));
    }

    /// <summary>The marker is compared case-insensitively so hex casing can never cause a re-extract.</summary>
    [Fact]
    public void Ensure_AcceptsAnUppercaseMarker()
    {
        using var fixture = new InstallFixture();
        fixture.Install();
        File.WriteAllText(fixture.MarkerPath, fixture.Manifest.PayloadSha256.ToUpperInvariant() + "\n");

        Assert.Equal(InstallOutcome.AlreadyCurrent, fixture.Install().Outcome);
    }

    /// <summary>An interrupted run leaves a staging directory; the next slow path must clear it.</summary>
    [Fact]
    public void Ensure_SweepsLeftoverStagingAndSupersededDirectories()
    {
        using var fixture = new InstallFixture();
        string staleStaging = Path.Combine(fixture.TargetDirectory, FabricatorPayloadNames.StagingPrefix + "interrupted");
        string staleAside = Path.Combine(fixture.TargetDirectory, FabricatorPayloadNames.SupersededPrefix + "upgrade");
        Directory.CreateDirectory(staleStaging);
        Directory.CreateDirectory(staleAside);
        File.WriteAllText(Path.Combine(staleStaging, "partial.dll"), "half-written");

        fixture.Install();

        Assert.False(Directory.Exists(staleStaging));
        Assert.False(Directory.Exists(staleAside));
    }

    [Fact]
    public void Ensure_LeavesNoStagingDirectoryBehindOnSuccess()
    {
        using var fixture = new InstallFixture();
        fixture.Install();

        Assert.Empty(Directory.EnumerateDirectories(fixture.TargetDirectory, FabricatorPayloadNames.StagingPrefix + "*"));
        Assert.Empty(Directory.EnumerateDirectories(fixture.TargetDirectory, FabricatorPayloadNames.SupersededPrefix + "*"));
    }

    [Fact]
    public void Ensure_RejectsAPayloadWhoseHashDoesNotMatchTheManifest()
    {
        using var fixture = new InstallFixture();

        var ex = Assert.Throws<InstallerException>(() => fixture.Install(
            manifest: new PayloadManifest { PayloadSha256 = new string('0', 64), PayloadLength = fixture.Manifest.PayloadLength }));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains("Re-download", ex.Message);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public void Ensure_RejectsAPayloadWhoseLengthDoesNotMatchTheManifest()
    {
        using var fixture = new InstallFixture();

        var ex = Assert.Throws<InstallerException>(() => fixture.Install(
            manifest: new PayloadManifest { PayloadSha256 = fixture.Manifest.PayloadSha256, PayloadLength = 7 }));

        Assert.Contains("truncated or damaged", ex.Message);
    }

    /// <summary>A failed extraction must not leave a marker claiming success.</summary>
    [Fact]
    public void Ensure_WritesNoMarkerWhenThePayloadIsUnusable()
    {
        using var fixture = new InstallFixture();
        string emptyZip = fixture.Temp.Combine("empty.zip");
        using (FileStream stream = File.Create(emptyZip))
        {
            PayloadPacker.Pack([], stream);
        }

        var ex = Assert.Throws<InstallerException>(() => PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = fixture.TargetDirectory,
            Manifest = new PayloadManifest { PayloadSha256 = "ignored", PayloadLength = 0 },
            OpenPayload = () => File.OpenRead(emptyZip),
            VerifyPayloadHash = false,
        }));

        Assert.Contains(FabricatorPayloadNames.CoreFile, ex.Message);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public void Ensure_RejectsAPayloadWithoutAManagedDirectory()
    {
        using var fixture = new InstallFixture();
        string coreOnly = fixture.Temp.Combine("core-only.zip");
        string core = fixture.Temp.Combine("core.bin");
        File.WriteAllText(core, "core");
        using (FileStream stream = File.Create(coreOnly))
        {
            PayloadPacker.Pack([new PayloadEntry(FabricatorPayloadNames.CoreFile, core)], stream);
        }

        var ex = Assert.Throws<InstallerException>(() => PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = fixture.TargetDirectory,
            Manifest = new PayloadManifest { PayloadSha256 = "ignored" },
            OpenPayload = () => File.OpenRead(coreOnly),
            VerifyPayloadHash = false,
        }));

        Assert.Contains($"non-empty '{FabricatorPayloadNames.ManagedDirectory}'", ex.Message);
    }

    /// <summary>
    /// Zip slip: a crafted artifact must not write outside the extension directory. The manifest and
    /// the archive are equally untrusted input.
    /// </summary>
    [Fact]
    public void Ensure_RejectsAnArchiveEntryThatEscapesTheDestination()
    {
        using var fixture = new InstallFixture();
        string evilZip = fixture.Temp.Combine("evil.zip");
        using (FileStream stream = File.Create(evilZip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry("../escaped.dll").Open());
            writer.Write("pwned");
        }

        var ex = Assert.Throws<InstallerException>(() => PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = fixture.TargetDirectory,
            Manifest = new PayloadManifest { PayloadSha256 = "ignored" },
            OpenPayload = () => File.OpenRead(evilZip),
            VerifyPayloadHash = false,
        }));

        Assert.Contains("unsafe entry", ex.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Temp.Path, "escaped.dll")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("sub/core.duckdb_extension")]
    [InlineData("../core.duckdb_extension")]
    public void Ensure_RejectsAManifestNameThatIsNotAPlainFileName(string coreFileName)
    {
        using var fixture = new InstallFixture();

        var ex = Assert.Throws<InstallerException>(() => fixture.Install(manifest: new PayloadManifest
        {
            CoreFileName = coreFileName,
            PayloadSha256 = fixture.Manifest.PayloadSha256,
        }));

        Assert.Contains("invalid CoreFileName", ex.Message);
    }

    [Fact]
    public void Ensure_RejectsAManifestWithNoPayloadHash()
    {
        using var fixture = new InstallFixture();

        var ex = Assert.Throws<InstallerException>(() => fixture.Install(manifest: new PayloadManifest()));

        Assert.Contains("no payload SHA-256", ex.Message);
    }

    [Fact]
    public void Ensure_CreatesTheTargetDirectory()
    {
        using var fixture = new InstallFixture(createTargetDirectory: false);

        Assert.Equal(InstallOutcome.Extracted, fixture.Install().Outcome);
        Assert.True(Directory.Exists(fixture.TargetDirectory));
    }

    /// <summary>Concurrent DuckDB processes (a CI matrix, dbt --threads N) must not corrupt each other.</summary>
    [Fact]
    public void Ensure_ConcurrentCallers_ExtractOnceAndAllSucceed()
    {
        using var fixture = new InstallFixture();
        const int callers = 4;
        var ready = new Barrier(callers);
        var outcomes = new InstallOutcome[callers];

        Parallel.For(0, callers, i =>
        {
            ready.SignalAndWait();
            outcomes[i] = fixture.Install().Outcome;
        });

        Assert.Equal(1, outcomes.Count(o => o == InstallOutcome.Extracted));
        Assert.All(outcomes, o => Assert.True(
            o is InstallOutcome.Extracted or InstallOutcome.ExtractedByAnotherProcess or InstallOutcome.AlreadyCurrent,
            $"unexpected outcome {o}"));
        Assert.Equal(TestArtifact.CoreContentDefault, File.ReadAllText(fixture.CorePath));
    }

    [Fact]
    public void Ensure_TimesOutWhenAnotherProcessHoldsTheLock()
    {
        using var fixture = new InstallFixture();
        string lockPath = Path.Combine(fixture.TargetDirectory, FabricatorPayloadNames.LockFile);

        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var ex = Assert.Throws<InstallerException>(() => fixture.Install(lockTimeout: TimeSpan.FromMilliseconds(150)));

        Assert.Contains("Timed out", ex.Message);
        Assert.Contains(FabricatorPayloadNames.LockFile, ex.Message);
    }

    /// <summary>
    /// The Windows upgrade-in-use case. A loaded library is opened by the loader with
    /// FILE_SHARE_DELETE, which forbids deleting it but PERMITS renaming it — so displacing the old
    /// core by rename lets an upgrade succeed while another session still has it loaded.
    /// </summary>
    [WindowsOnlyFact("POSIX renames unconditionally, so there is nothing to distinguish.")]
    public void Ensure_Upgrade_SucceedsWhileTheOldCoreIsStillOpenLoaderStyle()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();

        using (new FileStream(first.CorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            InstallResult upgraded = fixture.InstallVariant("fake core loadable v2");
            Assert.Equal(InstallOutcome.Extracted, upgraded.Outcome);
            Assert.Equal("fake core loadable v2", File.ReadAllText(upgraded.CorePath));
        }
    }

    /// <summary>...and when even a rename is refused, the error must say what to do.</summary>
    [WindowsOnlyFact("POSIX cannot refuse the rename, so the error path is unreachable.")]
    public void Ensure_Upgrade_ReportsAnInUseFileWhenItCannotBeDisplaced()
    {
        using var fixture = new InstallFixture();
        InstallResult first = fixture.Install();

        using (new FileStream(first.CorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<InstallerException>(() => fixture.InstallVariant("fake core loadable v2"));
            Assert.Contains("in use by another process", ex.Message);
            Assert.Contains("Close other DuckDB sessions", ex.Message);
        }
    }

    [Fact]
    public void Ensure_SkipsHashVerificationWhenAsked()
    {
        using var fixture = new InstallFixture();

        InstallResult result = PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = fixture.TargetDirectory,
            // A wrong-but-well-formed sha still installs when verification is off, and becomes the marker.
            Manifest = new PayloadManifest { PayloadSha256 = new string('a', 64) },
            OpenPayload = fixture.OpenPayload,
            VerifyPayloadHash = false,
        });

        Assert.Equal(InstallOutcome.Extracted, result.Outcome);
        Assert.Equal(new string('a', 64), File.ReadAllText(fixture.MarkerPath));
    }

    private sealed class InstallFixture : IDisposable
    {
        internal InstallFixture(bool createTargetDirectory = true)
        {
            Temp = new TempDirectory();
            TargetDirectory = Temp.Combine("extensions", "v1.5.5", "windows_amd64");
            if (createTargetDirectory)
            {
                Directory.CreateDirectory(TargetDirectory);
            }

            PayloadPath = Temp.Combine("payload.zip");
            Manifest = TestArtifact.Pack(TestArtifact.CreateSource(Temp.Combine("src")), PayloadPath);
        }

        internal TempDirectory Temp { get; }

        internal string TargetDirectory { get; }

        internal string PayloadPath { get; }

        internal PayloadManifest Manifest { get; }

        internal string MarkerPath => Path.Combine(TargetDirectory, FabricatorPayloadNames.MarkerFile);

        internal string CorePath => Path.Combine(TargetDirectory, FabricatorPayloadNames.CoreFile);

        internal Stream OpenPayload() => File.OpenRead(PayloadPath);

        internal InstallResult Install(PayloadManifest? manifest = null, TimeSpan? lockTimeout = null) =>
            PayloadInstaller.Ensure(new InstallRequest
            {
                TargetDirectory = TargetDirectory,
                Manifest = manifest ?? Manifest,
                OpenPayload = OpenPayload,
                LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30),
            });

        /// <summary>Packs a second, different payload — an upgrade.</summary>
        internal InstallResult InstallVariant(string coreContent)
        {
            string root = Temp.Combine("src-" + Guid.NewGuid().ToString("N")[..8]);
            string payload = Temp.Combine("payload-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
            PayloadManifest manifest = TestArtifact.Pack(TestArtifact.CreateSource(root, coreContent), payload);

            return PayloadInstaller.Ensure(new InstallRequest
            {
                TargetDirectory = TargetDirectory,
                Manifest = manifest,
                OpenPayload = () => File.OpenRead(payload),
                LockTimeout = TimeSpan.FromSeconds(30),
            });
        }

        public void Dispose() => Temp.Dispose();
    }
}
