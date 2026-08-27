// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer.Tests;

public sealed class CompatibilityGateTests
{
    private static PayloadManifest Manifest(string version = "v1.5.5", string platform = "windows_amd64") =>
        new() { TargetDuckDbVersion = version, Platform = platform, PayloadSha256 = "abc" };

    private static DuckDbEnvironment Environment(string version = "v1.5.5", string platform = "windows_amd64") =>
        new() { LibraryVersion = version, Platform = platform };

    [Fact]
    public void Check_PassesOnAnExactMatch() =>
        Assert.Null(CompatibilityGate.Check(Manifest(), Environment()));

    /// <summary>A manifest written as "1.5.5" must not be treated as a mismatch against "v1.5.5".</summary>
    [Fact]
    public void Check_NormalizesTheVersionTagOnBothSides()
    {
        Assert.Null(CompatibilityGate.Check(Manifest(version: "1.5.5"), Environment(version: "v1.5.5")));
        Assert.Null(CompatibilityGate.Check(Manifest(version: "v1.5.5"), Environment(version: "1.5.5")));
    }

    [Fact]
    public void Check_ReportsAVersionMismatchWithBothVersionsAndTheFix()
    {
        string? message = CompatibilityGate.Check(Manifest(version: "v1.5.5"), Environment(version: "v1.6.0"));

        Assert.NotNull(message);
        Assert.Contains("v1.5.5", message);
        Assert.Contains("v1.6.0", message);
        Assert.Contains("built for v1.6.0/windows_amd64", message);
        Assert.DoesNotContain("platform windows_amd64, but", message);
    }

    /// <summary>A dev build of the same version is still a different ABI.</summary>
    [Fact]
    public void Check_RejectsADevBuildOfTheTargetedVersion() =>
        Assert.NotNull(CompatibilityGate.Check(Manifest(version: "v1.5.5"), Environment(version: "v1.5.5-dev42")));

    [Fact]
    public void Check_ReportsAPlatformMismatch()
    {
        string? message = CompatibilityGate.Check(Manifest(platform: "windows_amd64"), Environment(platform: "linux_amd64"));

        Assert.NotNull(message);
        Assert.Contains("targets platform windows_amd64", message);
        Assert.Contains("this process reports linux_amd64", message);
    }

    [Fact]
    public void Check_ReportsBothWhenBothDiffer()
    {
        string? message = CompatibilityGate.Check(
            Manifest(version: "v1.5.5", platform: "windows_amd64"),
            Environment(version: "v1.6.0", platform: "osx_arm64"));

        Assert.NotNull(message);
        Assert.Contains("v1.5.5 on windows_amd64", message);
        Assert.Contains("v1.6.0 on osx_arm64", message);
    }

    /// <summary>
    /// An empty field is a mismatch, never a wildcard: a manifest that forgot to record its target
    /// must not silently load into any DuckDB.
    /// </summary>
    [Theory]
    [InlineData("", "windows_amd64")]
    [InlineData("v1.5.5", "")]
    public void Check_TreatsAMissingManifestFieldAsAMismatch(string version, string platform)
    {
        string? message = CompatibilityGate.Check(Manifest(version, platform), Environment());

        Assert.NotNull(message);
        Assert.Contains("<unknown>", message);
    }

    [Fact]
    public void Check_DescribesAnUnknownRunningVersion()
    {
        string? message = CompatibilityGate.Check(Manifest(), Environment(version: "", platform: ""));

        Assert.NotNull(message);
        Assert.Contains("<unknown>", message);
    }
}
