<#
.SYNOPSIS
    Builds ONE distributable fabricator.duckdb_extension: the NativeAOT installer shell with the
    core loadable + managed directory appended as a payload.

.DESCRIPTION
    Orchestration only — every packing decision lives in Fabricator.Installer.Core (and is unit
    tested there); this script just sequences the pieces:

      1. cmake  : the C++ core loadable          (skip with -SkipCore)
      2. publish-managed.ps1                     (skip with -SkipManaged)
      3. dotnet publish -p:PublishAot=true       (the installer shell; skip with -SkipShell)
      4. Fabricator.Installer.Pack               (deterministic payload + polyglot artifact)
      5. append_extension_metadata.py            (DuckDB's footer — must be the trailing bytes)

    The AOT shell only needs rebuilding when installer code changes; steps 1/2/4/5 are what a new
    core build or a SKU switch requires.

.NOTES
    Steps 1 and 3 need a Visual Studio developer environment on Windows (the C++ toolchain, and ilc's
    linker). Run this from a vcvars64 prompt, or pass -SkipCore/-SkipShell to reuse existing output.
    AOT cannot cross-compile between operating systems, so each platform is built on its own machine.
#>
[CmdletBinding()]
param(
    [ValidateSet('Standard', 'Standalone')]
    [string]$Sku = 'Standalone',

    [string]$Rid = 'win-x64',

    # DuckDB platform string (PRAGMA platform) recorded in the manifest and the metadata footer.
    [string]$Platform,

    # DuckDB version the CORE is built against. This is an exact requirement at load time.
    [string]$DuckDbVersion = 'v1.5.5',

    [string]$FabricatorVersion = '0.0.1',

    # C API version for the installer's C_STRUCT footer (checked as major==1 && minor <= host).
    [string]$CApiVersion = 'v1.2.0',

    [string]$OutputDirectory,

    [switch]$SkipCore,
    [switch]$SkipManaged,
    [switch]$SkipShell
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Platform) {
    $Platform = switch ($Rid) {
        'win-x64' { 'windows_amd64' }
        'win-arm64' { 'windows_arm64' }
        'linux-x64' { 'linux_amd64' }
        'linux-arm64' { 'linux_arm64' }
        'osx-x64' { 'osx_amd64' }
        'osx-arm64' { 'osx_arm64' }
        default { throw "Unknown RID '$Rid' — pass -Platform explicitly." }
    }
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "build/distribution/$Platform" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$corePath = Join-Path $repo 'build/release/extension/fabricator/fabricator.duckdb_extension'
$managedPath = Join-Path $repo 'build/release/extension/fabricator/fabricator'
$shellProject = Join-Path $repo 'dotnet/Fabricator.Installer'
$shellLibrary = Join-Path $shellProject "bin/x64/Release/net10.0/$Rid/publish/Fabricator.Installer.dll"
if ($Rid -notlike 'win-*') {
    $shellLibrary = Join-Path $shellProject "bin/x64/Release/net10.0/$Rid/publish/Fabricator.Installer.so"
}

$artifact = Join-Path $OutputDirectory 'fabricator.duckdb_extension'
$combined = Join-Path $OutputDirectory 'fabricator.combined'
$payload = Join-Path $OutputDirectory 'payload.zip'

function Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

# --- 1. the C++ core -------------------------------------------------------------------------
if (-not $SkipCore) {
    Step 'Building the core loadable'
    & cmake --build (Join-Path $repo 'build/release') --target fabricator_loadable_extension
    if ($LASTEXITCODE -ne 0) { throw 'cmake build failed (needs a vcvars64 environment on Windows)' }
}
if (-not (Test-Path $corePath)) { throw "Core loadable not found: $corePath" }

# --- 2. the managed payload ------------------------------------------------------------------
if (-not $SkipManaged) {
    Step "Publishing the managed bridge ($Sku)"
    $mode = if ($Sku -eq 'Standalone') { 'SelfContained' } else { 'Framework' }
    & (Join-Path $repo 'scripts/publish-managed.ps1') -Mode $mode
    if ($LASTEXITCODE -ne 0) { throw 'publish-managed.ps1 failed' }
}
if (-not (Test-Path $managedPath)) { throw "Managed directory not found: $managedPath" }

# --- 3. the NativeAOT installer shell --------------------------------------------------------
if (-not $SkipShell) {
    Step "Publishing the installer shell ($Rid, NativeAOT)"
    # IlcUseEnvironmentalTools: without it ilc runs its own vswhere probe for the linker and splices
    # the probe's FAILURE TEXT into the link command line. With it, the ambient vcvars tools are used.
    & dotnet publish $shellProject -c Release -r $Rid -p:IlcUseEnvironmentalTools=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (AOT) failed' }
}
if (-not (Test-Path $shellLibrary)) { throw "Installer shell not found: $shellLibrary" }

# --- 4. payload + polyglot artifact ----------------------------------------------------------
Step 'Packing the payload'
# NOTE: no --nologo here. `dotnet run` does not accept it and forwards it to the app, which then
# sees it as the first argument and rejects the whole command line.
& dotnet run --project (Join-Path $repo 'dotnet/Fabricator.Installer.Pack') -c Release -- `
    --core $corePath `
    --managed $managedPath `
    --library $shellLibrary `
    --output $combined `
    --payload $payload `
    --duckdb-version $DuckDbVersion `
    --platform $Platform `
    --fabricator-version $FabricatorVersion `
    --sku $Sku.ToLowerInvariant()
if ($LASTEXITCODE -ne 0) { throw 'packing failed' }

# --- 5. DuckDB metadata footer (must be last in the file) ------------------------------------
Step 'Appending the DuckDB metadata footer'
$metadataScript = Join-Path $repo 'extension-ci-tools/scripts/append_extension_metadata.py'
if (-not (Test-Path $metadataScript)) { throw "Not found: $metadataScript (clone extension-ci-tools)" }

& python $metadataScript -l $combined -o $artifact -n fabricator -p $Platform `
    -dv $CApiVersion -ev "v$($FabricatorVersion.TrimStart('v'))" --abi-type C_STRUCT
if ($LASTEXITCODE -ne 0) { throw 'append_extension_metadata.py failed' }

Remove-Item $combined -Force
$size = (Get-Item $artifact).Length

Write-Host ''
Write-Host "artifact : $artifact" -ForegroundColor Green
Write-Host ("size     : {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Host "targets  : DuckDB $DuckDbVersion / $Platform, sku $Sku"
Write-Host ''
Write-Host "Load with:  LOAD '$artifact';   (requires allow_unsigned_extensions)"
