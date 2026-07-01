#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the managed ArrowNet bridge (self-contained) next to the native
    DuckDB extension so the CoreCLR host can load it at runtime.

.DESCRIPTION
    Produces a self-contained .NET deployment (runtime + Apache.Arrow +
    ArrowNet.Bridge.dll + runtimeconfig.json) in <ExtensionDir>/arrownet. The
    native clr_host looks for this folder beside the extension binary, or via
    the ARROWNET_MANAGED_DIR environment variable.

.PARAMETER ExtensionDir
    Directory containing the built .duckdb_extension. The managed bridge is
    published into "<ExtensionDir>/arrownet".

.PARAMETER Rid
    .NET runtime identifier (defaults to the host: win-x64 / linux-x64 / osx-arm64).

.PARAMETER Configuration
    Build configuration (default: Release).
#>
[CmdletBinding()]
param(
    [string]$ExtensionDir = "$PSScriptRoot/../build/release/extension/mssql_net",
    [string]$Rid = "",
    [string]$Configuration = "Release",
    # Opt-in: also publish the delta-rs provider (ArrowNet.DeltaRs + delta-dotnet's ~240 MB native
    # delta_rs_bridge.dll / delta_kernel_ffi.dll). Off by default so the normal publish stays lean and
    # doesn't require the delta-dotnet sibling repo + its Rust build. Without it, BackendRegistry simply
    # skips the (absent) ArrowNet.DeltaRs assembly.
    [switch]$IncludeDeltaRs
)

$ErrorActionPreference = "Stop"

if (-not $Rid) {
    if ($IsWindows -or $env:OS -eq "Windows_NT") { $Rid = "win-x64" }
    elseif ($IsMacOS) { $Rid = "osx-arm64" }
    else { $Rid = "linux-x64" }
}

# Publish the composition root (ArrowNet.SqlServer) — it pulls in ArrowNet.Bridge
# and Microsoft.Data.SqlClient and carries the runtimeconfig the host initializes against.
$appProj = Join-Path $PSScriptRoot "../dotnet/ArrowNet.SqlServer/ArrowNet.SqlServer.csproj" | Resolve-Path
$managedOut = Join-Path $ExtensionDir "arrownet"

New-Item -ItemType Directory -Force -Path $ExtensionDir | Out-Null

Write-Host "Publishing ArrowNet.SqlServer ($Rid, $Configuration) -> $managedOut"
dotnet publish $appProj `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -o $managedOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Second provider: ArrowNet.AnalysisServices (DAX/ADOMD) — published into the SAME arrownet/ dir so the
# bridge discovers it by assembly name (ARROWNET_BACKEND_ASSEMBLY defaults to both). Adds its own dll +
# Microsoft.AnalysisServices.AdomdClient (+ deps) alongside the shared Bridge/runtime files.
$daxProj = Join-Path $PSScriptRoot "../dotnet/ArrowNet.AnalysisServices/ArrowNet.AnalysisServices.csproj" | Resolve-Path
Write-Host "Publishing ArrowNet.AnalysisServices ($Rid, $Configuration) -> $managedOut"
dotnet publish $daxProj `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -o $managedOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (AnalysisServices) failed ($LASTEXITCODE)" }

# Optional third provider: ArrowNet.DeltaRs (delta-rs via delta-dotnet). Published into the SAME arrownet/
# dir so the bridge discovers it by assembly name. Brings DeltaLake.dll + the two native Rust DLLs
# (delta_rs_bridge.dll / delta_kernel_ffi.dll, ~240 MB) — hence opt-in via -IncludeDeltaRs.
if ($IncludeDeltaRs) {
    $deltaProj = Join-Path $PSScriptRoot "../dotnet/ArrowNet.DeltaRs/ArrowNet.DeltaRs.csproj"
    if (Test-Path $deltaProj) {
        $deltaProj = Resolve-Path $deltaProj
        Write-Host "Publishing ArrowNet.DeltaRs ($Rid, $Configuration) -> $managedOut"
        dotnet publish $deltaProj `
            -c $Configuration `
            -r $Rid `
            --self-contained true `
            -o $managedOut
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (DeltaRs) failed ($LASTEXITCODE)" }
        # Ensure the native Rust DLLs landed (they are Content in DeltaLake.csproj; publish copies them, but
        # verify + copy from the DeltaLake build output as a backstop so p/invoke resolves them at runtime).
        foreach ($dll in @("delta_rs_bridge.dll", "delta_kernel_ffi.dll")) {
            if (-not (Test-Path (Join-Path $managedOut $dll))) {
                $src = Get-ChildItem -Path (Join-Path $PSScriptRoot "../../delta-dotnet/src/DeltaLake") `
                    -Recurse -Filter $dll -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($src) { Copy-Item $src.FullName (Join-Path $managedOut $dll) -Force }
                else { Write-Warning "native $dll not found; ArrowNet.DeltaRs will fail to load at runtime" }
            }
        }
    } else {
        Write-Warning "ArrowNet.DeltaRs project not found; skipping (-IncludeDeltaRs)."
    }
}

Write-Host "Managed bridge published to $managedOut"
