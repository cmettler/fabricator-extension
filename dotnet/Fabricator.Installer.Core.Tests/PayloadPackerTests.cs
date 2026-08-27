// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer.Tests;

public sealed class PayloadPackerTests
{
    /// <summary>
    /// The payload SHA doubles as the idempotence marker, so a rebuild of identical inputs must
    /// produce identical bytes — otherwise every rebuild would force every installed machine to
    /// re-extract.
    /// </summary>
    [Fact]
    public void Pack_IsByteIdentical_ForIdenticalInputs()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        PayloadPackResult a = PayloadPacker.Pack(entries, first);
        PayloadPackResult b = PayloadPacker.Pack(entries, second);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(3, a.EntryCount);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void Pack_IsIndependentOfInputOrder()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));

        using var forward = new MemoryStream();
        using var reversed = new MemoryStream();
        PayloadPacker.Pack(entries, forward);
        PayloadPacker.Pack(entries.Reverse(), reversed);

        Assert.Equal(forward.ToArray(), reversed.ToArray());
    }

    /// <summary>
    /// Determinism must hold across BUILD MACHINES, not just across runs on one machine, so the
    /// stored timestamp must not depend on the local timezone.
    /// </summary>
    /// <remarks>
    /// A zip stores an MS-DOS timestamp, which carries no timezone. .NET encodes the
    /// <see cref="DateTimeOffset"/>'s wall-clock component verbatim (so a UTC-offset constant is
    /// machine-independent — the point of <see cref="PayloadPacker.FixedTimestamp"/>) but on read
    /// reattaches the LOCAL offset, which is why the round-tripped value compares equal only on its
    /// <see cref="DateTimeOffset.DateTime"/> part. Asserting the header bytes pins the property that
    /// actually matters.
    /// </remarks>
    [Fact]
    public void Pack_StampsTheFixedTimestamp_IndependentOfTheMachineTimezone()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));

        using var payload = new MemoryStream();
        PayloadPacker.Pack(entries, payload);
        byte[] bytes = payload.ToArray();

        payload.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(payload, System.IO.Compression.ZipArchiveMode.Read);
        Assert.All(archive.Entries, e => Assert.Equal(PayloadPacker.FixedTimestamp.DateTime, e.LastWriteTime.DateTime));

        // Local file header: bytes 10-11 = DOS time, 12-13 = DOS date. 1980-01-01 00:00:00 encodes
        // as time 0x0000 and date 0x0021 (year 0 << 9 | month 1 << 5 | day 1), little-endian.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x21, 0x00 }, bytes[10..14]);
    }

    [Fact]
    public void Pack_SortsEntriesOrdinally()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));

        using var payload = new MemoryStream();
        PayloadPacker.Pack(entries, payload);

        payload.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(payload, System.IO.Compression.ZipArchiveMode.Read);
        string[] names = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
    }

    [Theory]
    [InlineData("/absolute.dll")]
    [InlineData("..\\escape.dll")]
    [InlineData("a/../../escape.dll")]
    [InlineData("C:/drive.dll")]
    [InlineData("nested//empty.dll")]
    [InlineData("./here.dll")]
    [InlineData("   ")]
    public void Pack_RejectsUnsafeRelativePaths(string relativePath)
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("payload.bin");
        File.WriteAllText(file, "x");

        using var destination = new MemoryStream();
        var ex = Assert.Throws<InstallerException>(
            () => PayloadPacker.Pack([new PayloadEntry(relativePath, file)], destination));
        Assert.Contains("Invalid payload entry path", ex.Message);
    }

    /// <summary>A case-only collision would overwrite on Windows and duplicate on Linux.</summary>
    [Fact]
    public void Pack_RejectsCaseInsensitiveDuplicates()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("payload.bin");
        File.WriteAllText(file, "x");

        using var destination = new MemoryStream();
        var ex = Assert.Throws<InstallerException>(() => PayloadPacker.Pack(
            [new PayloadEntry("fabricator/Bridge.dll", file), new PayloadEntry("fabricator/BRIDGE.DLL", file)],
            destination));
        Assert.Contains("Duplicate payload entry", ex.Message);
    }

    /// <summary>
    /// Zip local-header offsets are stream-absolute, so an archive written at a non-zero position is
    /// unreadable through the polyglot's payload window. Guard, don't debug later.
    /// </summary>
    [Fact]
    public void Pack_RequiresStreamPositionZero()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));

        using var destination = new MemoryStream();
        destination.Write(new byte[16]);

        var ex = Assert.Throws<ArgumentException>(() => PayloadPacker.Pack(entries, destination));
        Assert.Contains("position 0", ex.Message);
    }

    [Fact]
    public void Pack_LeavesTheDestinationPositionedAtTheEnd()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));

        using var destination = new MemoryStream();
        PayloadPackResult result = PayloadPacker.Pack(entries, destination);

        Assert.Equal(result.Length, destination.Position);
        Assert.Equal(result.Length, destination.Length);
    }

    [Fact]
    public void EnumerateDirectory_ProducesForwardSlashPathsUnderThePrefix()
    {
        using var temp = new TempDirectory();
        string managed = temp.Combine("managed");
        Directory.CreateDirectory(Path.Combine(managed, "nested"));
        File.WriteAllText(Path.Combine(managed, "a.dll"), "a");
        File.WriteAllText(Path.Combine(managed, "nested", "b.dll"), "b");

        IReadOnlyList<PayloadEntry> entries = PayloadPacker.EnumerateDirectory(managed, "fabricator");

        Assert.Equal(
            ["fabricator/a.dll", "fabricator/nested/b.dll"],
            entries.Select(e => e.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void EnumerateDirectory_WithEmptyPrefix_KeepsPathsAtTheRoot()
    {
        using var temp = new TempDirectory();
        string root = temp.CreateSubdirectory("root");
        File.WriteAllText(Path.Combine(root, "core.bin"), "c");

        IReadOnlyList<PayloadEntry> entries = PayloadPacker.EnumerateDirectory(root, "");

        Assert.Equal("core.bin", Assert.Single(entries).RelativePath);
    }

    [Fact]
    public void EnumerateDirectory_ThrowsWhenMissing()
    {
        using var temp = new TempDirectory();
        var ex = Assert.Throws<InstallerException>(() => PayloadPacker.EnumerateDirectory(temp.Combine("nope"), "x"));
        Assert.Contains("does not exist", ex.Message);
    }
}
