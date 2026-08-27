// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer.Tests;

/// <summary>
/// Packs and installs the REAL fabricator payload — the built core loadable plus the published
/// managed directory — so the packer/polyglot/installer chain is exercised at production scale and
/// shape, not just against synthetic fixtures.
/// </summary>
/// <remarks>
/// Skipped unless both env vars are set (the repo's <c>require-env</c> convention):
/// <list type="bullet">
/// <item><c>FABRICATOR_E2E_CORE</c> — path to <c>fabricator.duckdb_extension</c></item>
/// <item><c>FABRICATOR_E2E_MANAGED</c> — path to the published <c>fabricator/</c> directory</item>
/// </list>
/// Set <c>FABRICATOR_E2E_OUT</c> to keep the produced artifact and install tree for manual loading;
/// otherwise both are written to a temp directory and deleted.
/// <para>
/// <c>CoreFileName</c> is taken from the environment so this can produce a tree that today's core
/// loads: the built binary exports <c>fabricator_duckdb_cpp_init</c> only, and DuckDB derives the
/// entry symbol from the file name, so the extracted core is loadable under the name
/// <c>fabricator.duckdb_extension</c> and will only be loadable as <c>fabricator_core</c> once the
/// forwarding export is added.
/// </para>
/// </remarks>
public sealed class RealPayloadEndToEndTests
{
    private static string? Core => Environment.GetEnvironmentVariable("FABRICATOR_E2E_CORE");

    private static string? Managed => Environment.GetEnvironmentVariable("FABRICATOR_E2E_MANAGED");

    private static bool Enabled =>
        !string.IsNullOrEmpty(Core) && File.Exists(Core) &&
        !string.IsNullOrEmpty(Managed) && Directory.Exists(Managed);

    [Fact]
    public void PackInstallAndVerify_TheRealPayload()
    {
        if (!Enabled)
        {
            return;
        }

        string coreFileName = Environment.GetEnvironmentVariable("FABRICATOR_E2E_CORE_NAME")
            ?? FabricatorPayloadNames.CoreFile;

        using var temp = new TempDirectory();
        string workspace = Environment.GetEnvironmentVariable("FABRICATOR_E2E_OUT") ?? temp.Path;
        Directory.CreateDirectory(workspace);

        string payloadPath = Path.Combine(workspace, "payload.zip");
        string artifactPath = Path.Combine(workspace, "fabricator.duckdb_extension");
        string extensionDirectory = Path.Combine(workspace, "extensions", "v1.5.5", "windows_amd64");

        // 1. Pack exactly what pack-distribution will: the core under its extracted name, plus the
        //    managed directory under the name clr_host probes for next to the loaded module.
        List<PayloadEntry> entries = [new PayloadEntry(coreFileName, Core!)];
        entries.AddRange(PayloadPacker.EnumerateDirectory(Managed!, FabricatorPayloadNames.ManagedDirectory));

        PayloadPackResult packed;
        using (FileStream output = File.Create(payloadPath))
        {
            packed = PayloadPacker.Pack(entries, output);
        }

        var manifest = new PayloadManifest
        {
            FabricatorVersion = "0.0.1",
            TargetDuckDbVersion = "v1.5.5",
            Platform = "windows_amd64",
            Sku = Directory.Exists(Path.Combine(Managed!, "..")) && File.Exists(Path.Combine(Managed!, "hostfxr.dll"))
                ? "standalone"
                : "standard",
            CoreFileName = coreFileName,
            PayloadSha256 = packed.Sha256,
            PayloadLength = packed.Length,
            EntryCount = packed.EntryCount,
        };

        // 2. Wrap it polyglot-style. A real AOT image is not needed to exercise the format; the AOT
        //    shell is validated separately.
        TestArtifact.WriteArtifact(artifactPath, payloadPath, manifest);

        // 3. Read the trailer back out of the artifact.
        PolyglotPackage package = PolyglotPackage.Open(artifactPath);
        Assert.Equal(packed.Sha256, package.Manifest.PayloadSha256);
        Assert.Equal(coreFileName, package.Manifest.CoreFileName);
        Assert.Null(CompatibilityGate.Check(package.Manifest, new DuckDbEnvironment
        {
            LibraryVersion = "v1.5.5",
            Platform = "windows_amd64",
        }));

        // 4. Install, verifying the payload hash on the way in.
        InstallResult result = PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = extensionDirectory,
            Manifest = package.Manifest,
            OpenPayload = package.OpenPayload,
        });

        Assert.Equal(InstallOutcome.Extracted, result.Outcome);
        Assert.Equal(new FileInfo(Core!).Length, new FileInfo(result.CorePath).Length);
        Assert.Equal(
            Directory.EnumerateFiles(Managed!, "*", SearchOption.AllDirectories).Count(),
            Directory.EnumerateFiles(result.ManagedDirectory, "*", SearchOption.AllDirectories).Count());

        // 5. And the steady state is the cheap path.
        Assert.Equal(InstallOutcome.AlreadyCurrent, PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = extensionDirectory,
            Manifest = package.Manifest,
            OpenPayload = package.OpenPayload,
        }).Outcome);
    }
}
