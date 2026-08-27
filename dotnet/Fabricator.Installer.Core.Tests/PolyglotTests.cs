// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.Text;

namespace Fabricator.Installer.Tests;

public sealed class PolyglotTests
{
    /// <summary>
    /// The footer size is NOT a stable contract (534 bytes today, 512 parsed by DuckDB), so the
    /// reader must find the index regardless. These are the sizes that would break a
    /// fixed-offset-from-EOF implementation.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(534)]
    [InlineData(768)]
    [InlineData(4096)]
    public void Open_FindsThePayload_ForAnyFooterSize(int footerSize)
    {
        using var temp = new TempDirectory();
        string artifact = BuildArtifact(temp, out PayloadManifest manifest, footerSize: footerSize);

        PolyglotPackage package = PolyglotPackage.Open(artifact);

        Assert.Equal(manifest.PayloadSha256, package.Manifest.PayloadSha256);
        Assert.Equal(manifest.PayloadLength, package.PayloadLength);
        Assert.Equal("v1.5.5", package.Manifest.TargetDuckDbVersion);
        Assert.Equal(4096, package.PayloadOffset);
    }

    [Fact]
    public void OpenPayload_YieldsAnExtractableArchive()
    {
        using var temp = new TempDirectory();
        string artifact = BuildArtifact(temp, out _);
        PolyglotPackage package = PolyglotPackage.Open(artifact);

        string destination = temp.CreateSubdirectory("out");
        using (Stream payload = package.OpenPayload())
        {
            Assert.Equal(3, PayloadExtractor.Extract(payload, destination));
        }

        Assert.Equal(
            TestArtifact.CoreContentDefault,
            File.ReadAllText(Path.Combine(destination, FabricatorPayloadNames.CoreFile)));
        Assert.True(File.Exists(Path.Combine(destination, FabricatorPayloadNames.ManagedDirectory, "nested", "runtime.json")));
    }

    /// <summary>The payload window must behave like a standalone file, including seeks.</summary>
    [Fact]
    public void OpenPayload_IsASeekableWindowOverJustThePayload()
    {
        using var temp = new TempDirectory();
        string artifact = BuildArtifact(temp, out PayloadManifest manifest);
        PolyglotPackage package = PolyglotPackage.Open(artifact);

        using Stream payload = package.OpenPayload();
        Assert.Equal(manifest.PayloadLength, payload.Length);

        // A zip's local file header begins "PK\x03\x04".
        byte[] head = new byte[4];
        payload.ReadExactly(head);
        Assert.Equal("PK"u8.ToArray(), head[..2]);

        payload.Seek(-1, SeekOrigin.End);
        Assert.Equal(manifest.PayloadLength - 1, payload.Position);
        Assert.Equal(1, payload.Read(new byte[8], 0, 8));
        Assert.Equal(0, payload.Read(new byte[8], 0, 8));
    }

    /// <summary>
    /// The magic is located by scanning, so a chance occurrence of those eight bytes inside the
    /// library image must not shadow the real index.
    /// </summary>
    [Fact]
    public void Open_IgnoresTheMagicAppearingInsideTheLibraryImage()
    {
        using var temp = new TempDirectory();
        byte[] image = TestArtifact.FakeLibraryImage();
        Encoding.ASCII.GetBytes(PolyglotIndex.MagicString).CopyTo(image, 1024);

        string artifact = BuildArtifact(temp, out PayloadManifest manifest, libraryImage: image);
        PolyglotPackage package = PolyglotPackage.Open(artifact);

        Assert.Equal(manifest.PayloadSha256, package.Manifest.PayloadSha256);
    }

    /// <summary>...and neither must one inside the metadata footer, which the scan reaches FIRST.</summary>
    [Fact]
    public void Open_SkipsABogusMagicInsideTheFooter()
    {
        using var temp = new TempDirectory();
        byte[] footer = new byte[534];
        Encoding.ASCII.GetBytes(PolyglotIndex.MagicString).CopyTo(footer, 100);
        for (int i = 108; i < footer.Length; i++)
        {
            footer[i] = 0xAB; // garbage where a real index would carry format version 1
        }

        string artifact = BuildArtifact(temp, out PayloadManifest manifest, footerBytes: footer);
        PolyglotPackage package = PolyglotPackage.Open(artifact);

        Assert.Equal(manifest.PayloadSha256, package.Manifest.PayloadSha256);
    }

    [Fact]
    public void Open_ReportsAMissingPayload_ForAPlainLibrary()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("bare.duckdb_extension");
        File.WriteAllBytes(path, TestArtifact.FakeLibraryImage());

        var ex = Assert.Throws<InstallerException>(() => PolyglotPackage.Open(path));
        Assert.Contains("does not contain a fabricator payload", ex.Message);
        Assert.Contains("not a fabricator distribution artifact", ex.Message);
    }

    [Fact]
    public void Open_ReportsCorruption_WhenTheIndexIsTruncated()
    {
        using var temp = new TempDirectory();
        string artifact = BuildArtifact(temp, out _, footerSize: 0);

        // Keep the index and manifest, drop the payload: the declared payload cannot fit before them.
        byte[] bytes = File.ReadAllBytes(artifact);
        string corrupt = temp.Combine("corrupt.duckdb_extension");
        File.WriteAllBytes(corrupt, bytes[^(PolyglotIndex.Size + 400)..]);

        var ex = Assert.Throws<InstallerException>(() => PolyglotPackage.Open(corrupt));
        Assert.Contains("corrupt or truncated", ex.Message);
    }

    /// <summary>
    /// Index and manifest independently record the payload length; disagreement means the artifact
    /// was tampered with or spliced, and must not be extracted.
    /// </summary>
    [Fact]
    public void Open_RejectsALengthDisagreementBetweenIndexAndManifest()
    {
        using var temp = new TempDirectory();
        string artifact = BuildArtifact(temp, out _, footerSize: 0);

        byte[] bytes = File.ReadAllBytes(artifact);
        // Rewrite the index's payloadLength field (offset 16 within the 32-byte index) to a value
        // that is still structurally plausible, so only the manifest cross-check can catch it.
        int indexStart = bytes.Length - PolyglotIndex.Size;
        BitConverter.GetBytes(100L).CopyTo(bytes, indexStart + 16);
        string spliced = temp.Combine("spliced.duckdb_extension");
        File.WriteAllBytes(spliced, bytes);

        var ex = Assert.Throws<InstallerException>(() => PolyglotPackage.Open(spliced));
        Assert.Contains("corrupt or truncated", ex.Message);
    }

    [Fact]
    public void Open_ReportsMissingPayload_WhenTheIndexIsBeyondTheScanWindow()
    {
        using var temp = new TempDirectory();
        string artifact = BuildArtifact(temp, out _, footerSize: 8192);

        var ex = Assert.Throws<InstallerException>(() => PolyglotPackage.Open(artifact, tailScanWindow: 1024));
        Assert.Contains("last 1024 bytes", ex.Message);
    }

    [Fact]
    public void TryOpen_ReportsFailureWithoutThrowing()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("bare.duckdb_extension");
        File.WriteAllBytes(path, TestArtifact.FakeLibraryImage());

        Assert.False(PolyglotPackage.TryOpen(path, PolyglotPackage.DefaultTailScanWindow, out PolyglotPackage? package, out string? error));
        Assert.Null(package);
        Assert.NotNull(error);
    }

    [Fact]
    public void Write_RejectsAManifestWhoseLengthDisagreesWithThePayload()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));
        string payloadPath = temp.Combine("payload.zip");
        PayloadManifest manifest = TestArtifact.Pack(entries, payloadPath);

        using var output = new MemoryStream();
        using var library = new MemoryStream(TestArtifact.FakeLibraryImage());
        using FileStream payload = File.OpenRead(payloadPath);

        var ex = Assert.Throws<InstallerException>(() => PolyglotWriter.Write(
            library, payload, new PayloadManifest { PayloadLength = manifest.PayloadLength + 1, PayloadSha256 = "x" }, output));
        Assert.Contains("does not match", ex.Message);
    }

    private static string BuildArtifact(
        TempDirectory temp,
        out PayloadManifest manifest,
        int footerSize = 534,
        byte[]? libraryImage = null,
        byte[]? footerBytes = null)
    {
        IReadOnlyList<PayloadEntry> entries = TestArtifact.CreateSource(temp.Combine("src"));
        string payloadPath = temp.Combine("payload.zip");
        manifest = TestArtifact.Pack(entries, payloadPath);
        string artifact = temp.Combine("fabricator.duckdb_extension");
        return TestArtifact.WriteArtifact(artifact, payloadPath, manifest, footerSize, libraryImage, footerBytes);
    }
}
