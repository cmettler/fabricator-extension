// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using DuckDB.ExtensionKit;
using DuckDB.ExtensionKit.Native;

namespace Fabricator.Installer;

/// <summary>
/// The <c>fabricator</c> extension as users see it: one file to <c>LOAD</c>. It registers no SQL
/// surface of its own — every function, <c>ATTACH … (TYPE fabricator)</c>, secret and setting comes
/// from the core it loads.
/// </summary>
/// <remarks>
/// Flow at every load (docs/distribution-installer.md §6): find our own file → read the appended
/// payload's manifest → gate on DuckDB version/platform BEFORE touching disk → resolve DuckDB's
/// extension directory → extract if not already current → <c>LOAD</c> the extracted core.
/// <para>
/// Any exception's message becomes DuckDB's error for the <c>LOAD</c> (the kit's generated entry
/// routes it to <c>set_error</c>), which is why <see cref="InstallerException"/> messages are written
/// to be read by users rather than developers.
/// </para>
/// <para>
/// The core is extracted under a DIFFERENT extension name (<c>fabricator_core</c>) on purpose.
/// DuckDB derives an extension's identity from the file name and takes a load lock per name
/// (extension_manager.cpp:73-110), so chain-loading a file that resolves to "fabricator" from inside
/// "fabricator"'s own load would block on our own lock. The core exports a forwarding
/// <c>fabricator_core_duckdb_cpp_init</c> entry for exactly this reason.
/// </para>
/// </remarks>
[DuckDBExtension]
public static unsafe partial class FabricatorInstallerExtension
{
    private static void RegisterFunctions(DuckDBConnection connection)
    {
        string self = ModulePath.OfThisLibrary()
            ?? throw new InstallerException(
                "The fabricator installer could not determine its own file path, so it cannot find its " +
                "payload. Load it from a real file (not a memory image), or install the two-piece " +
                "distribution and LOAD the core directly.");

        PolyglotPackage package = PolyglotPackage.Open(self);
        PayloadManifest manifest = package.Manifest;

        DuckDbEnvironment environment = DuckDbEnvironmentReader.Read(connection);

        // Before any disk work: the core is CPP-ABI and version-locked, so a mismatch can only fail.
        // Failing here turns DuckDB's generic footer error into an actionable one.
        string? mismatch = CompatibilityGate.Check(manifest, environment);
        if (mismatch is not null)
        {
            throw new InstallerException(mismatch);
        }

        string extensionDirectory = ExtensionDirectoryResolver.Resolve(environment);

        InstallResult install = PayloadInstaller.Ensure(new InstallRequest
        {
            TargetDirectory = extensionDirectory,
            Manifest = manifest,
            OpenPayload = package.OpenPayload,
        });

        // Full path: unambiguous regardless of how extension search paths are configured.
        DuckDbSql.Execute(connection, $"LOAD '{DuckDbSql.Literal(install.CorePath)}'");
    }
}
