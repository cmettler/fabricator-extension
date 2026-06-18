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
    [string]$Configuration = "Release"
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

Write-Host "Managed bridge published to $managedOut"
