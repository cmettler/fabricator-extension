#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the managed Fabricator bridge next to the native DuckDB extension
    so the CoreCLR host can load it at runtime.

.DESCRIPTION
    Two deployment modes:
      SelfContained (default) — runtime + assemblies (~250 MB); runs on machines
        with NO .NET installed. net10.0.
      Framework — assemblies only (~tens of MB); the native clr_host resolves a
        PROVIDED .NET install at load (FABRICATOR_DOTNET_ROOT > DOTNET_ROOT >
        global). Targets net8.0 with rollForward=LatestMajor, so ONE payload
        runs on .NET 8 (e.g. the Fabric-notebook preinstalled runtime) AND 10+.
    The native clr_host detects the layout by the presence of hostfxr in the
    managed dir (self-contained carries it; framework-dependent doesn't).

.PARAMETER ExtensionDir
    Directory containing the built .duckdb_extension. The managed bridge is
    published into "<ExtensionDir>/fabricator".

.PARAMETER Rid
    .NET runtime identifier (defaults to the host: win-x64 / linux-x64 / osx-arm64).

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Mode
    "SelfContained" (default) or "Framework" (framework-dependent).

.PARAMETER Framework
    Target framework of the publish. Defaults: net10.0 for SelfContained,
    net8.0 for Framework (lowest supported runtime; rolls forward).
#>
[CmdletBinding()]
param(
    [string]$ExtensionDir = "$PSScriptRoot/../build/release/extension/fabricator",
    [string]$Rid = "",
    [string]$Configuration = "Release",
    [ValidateSet("SelfContained", "Framework")]
    [string]$Mode = "SelfContained",
    [string]$Framework = "",
    # Opt-in: also publish the delta-rs provider (Fabricator.DeltaRs + delta-dotnet's ~240 MB native
    # delta_rs_bridge.dll / delta_kernel_ffi.dll). Off by default so the normal publish stays lean and
    # doesn't require the delta-dotnet sibling repo + its Rust build. Without it, BackendRegistry simply
    # skips the (absent) Fabricator.DeltaRs assembly.
    [switch]$IncludeDeltaRs,
    # Wipe the output dir first. Needed after MOVING a PackageReference between assemblies that publish into
    # it — see the clean block below for the measurement; the failure is silent and only the service tier
    # catches it.
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

if (-not $Rid) {
    if ($IsWindows -or $env:OS -eq "Windows_NT") { $Rid = "win-x64" }
    elseif ($IsMacOS) { $Rid = "osx-arm64" }
    else { $Rid = "linux-x64" }
}
$selfContained = $Mode -eq "SelfContained"
if (-not $Framework) { $Framework = if ($selfContained) { "net10.0" } else { "net8.0" } }
$scFlag = if ($selfContained) { "true" } else { "false" }

$managedOut = Join-Path $ExtensionDir "fabricator"
New-Item -ItemType Directory -Force -Path $ExtensionDir | Out-Null

# A leftover self-contained runtime in the output dir would make a framework-dependent publish
# indistinguishable from self-contained (clr_host detects the layout by hostfxr's presence) — and vice
# versa a stale FDD runtimeconfig would confuse an SC layout. Clean the output dir on a MODE change.
#
# ⚠⚠ MODE IS NOT THE ONLY THING THAT NEEDS A CLEAN, AND THE OTHER TRIGGER IS SILENT. Several projects
# publish into this ONE directory, and `dotnet publish` removes what ITS OWN previous publish wrote and its
# closure no longer contains. So MOVING A PackageReference BETWEEN ASSEMBLIES deletes those DLLs from an
# already-populated payload, even though the package is still referenced by another project publishing into
# the same dir. MEASURED 2026-08-18, dropping the unused Microsoft.Data.SqlClient reference from
# Fabricator.Bridge: incremental publish left ZERO SqlClient DLLs (Fabricator.AnalysisServices' publish
# removed the five it had written transitively), while a publish into a FRESH dir kept all five — the
# reference still lives on Fabricator.SqlServer, which publishes here too.
#
# It is invisible where it matters: nothing fails to compile, and the hermetic tier never touches SQL Server,
# so only the SERVICE tier catches it. Pass -Clean after moving a dependency between assemblies. The clean is
# not unconditional because a self-contained payload is ~250 MB and the common loop is a C#-only edit.
$hostfxrLeaf = if ($Rid -like "win-*") { "hostfxr.dll" } elseif ($Rid -like "osx-*") { "libhostfxr.dylib" } else { "libhostfxr.so" }
if (Test-Path $managedOut) {
    $hasHostfxr = Test-Path (Join-Path $managedOut $hostfxrLeaf)
    if ($Clean) {
        Write-Host "-Clean — removing $managedOut before publishing"
        Remove-Item -Recurse -Force $managedOut
    } elseif ($hasHostfxr -ne $selfContained) {
        Write-Host "Publish mode changed ($(if ($hasHostfxr) {'self-contained'} else {'framework-dependent'}) -> $Mode) — cleaning $managedOut"
        Remove-Item -Recurse -Force $managedOut
    }
}

function Publish-Project([string]$proj, [string]$label) {
    Write-Host "Publishing $label ($Rid, $Framework, $Mode, $Configuration) -> $managedOut"
    dotnet publish $proj `
        -c $Configuration `
        -f $Framework `
        -r $Rid `
        --self-contained $scFlag `
        -o $managedOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($label) failed ($LASTEXITCODE)" }
}

# Publish the composition root (Fabricator.SqlServer) — it pulls in Fabricator.Bridge
# and Microsoft.Data.SqlClient and carries the runtimeconfig the host initializes against.
$appProj = Join-Path $PSScriptRoot "../dotnet/Fabricator.SqlServer/Fabricator.SqlServer.csproj" | Resolve-Path
Publish-Project $appProj "Fabricator.SqlServer"

# Second provider: Fabricator.AnalysisServices (DAX/ADOMD) — published into the SAME fabricator/ dir so the
# bridge discovers it by assembly name (FABRICATOR_BACKEND_ASSEMBLY defaults to both). Adds its own dll +
# Microsoft.AnalysisServices.AdomdClient (+ deps) alongside the shared Bridge files.
$daxProj = Join-Path $PSScriptRoot "../dotnet/Fabricator.AnalysisServices/Fabricator.AnalysisServices.csproj" | Resolve-Path
Publish-Project $daxProj "Fabricator.AnalysisServices"

# Optional third provider: Fabricator.DeltaRs (delta-rs via delta-dotnet). Published into the SAME fabricator/
# dir so the bridge discovers it by assembly name. Brings DeltaLake.dll + the two native Rust DLLs
# (delta_rs_bridge.dll / delta_kernel_ffi.dll, ~240 MB) — hence opt-in via -IncludeDeltaRs.
if ($IncludeDeltaRs) {
    $deltaProj = Join-Path $PSScriptRoot "../dotnet/Fabricator.DeltaRs/Fabricator.DeltaRs.csproj"
    if (Test-Path $deltaProj) {
        $deltaProj = Resolve-Path $deltaProj
        Publish-Project $deltaProj "Fabricator.DeltaRs"
        # Ensure the native Rust DLLs landed (they are Content in DeltaLake.csproj; publish copies them, but
        # verify + copy from the DeltaLake build output as a backstop so p/invoke resolves them at runtime).
        foreach ($dll in @("delta_rs_bridge.dll", "delta_kernel_ffi.dll")) {
            if (-not (Test-Path (Join-Path $managedOut $dll))) {
                $src = Get-ChildItem -Path (Join-Path $PSScriptRoot "../../delta-dotnet/src/DeltaLake") `
                    -Recurse -Filter $dll -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($src) { Copy-Item $src.FullName (Join-Path $managedOut $dll) -Force }
                else { Write-Warning "native $dll not found; Fabricator.DeltaRs will fail to load at runtime" }
            }
        }
    } else {
        Write-Warning "Fabricator.DeltaRs project not found; skipping (-IncludeDeltaRs)."
    }
}

Write-Host "Managed bridge published to $managedOut ($Mode, $Framework, $Rid)"
