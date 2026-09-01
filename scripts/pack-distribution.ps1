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

    # Override where the core loadable and the managed directory come from. Needed when packaging for
    # another OS: NativeAOT and the C++ core must be BUILT on their target platform, but packing is
    # platform-neutral, so a Windows machine can assemble a linux artifact from linux inputs.
    [string]$CorePath,
    [string]$ManagedPath,

    [switch]$SkipCore,
    [switch]$SkipManaged,
    [switch]$SkipShell,

    # Also emit the two artifacts test/distribution/smoke_distribution.py uses to check the failure
    # paths: one whose manifest targets a different DuckDB version, and one with no payload at all.
    # They are per-platform (DuckDB checks the footer's platform first), hence siblings of the real one.
    [switch]$WithNegatives,

    # Keep the intermediate payload archive next to the artifact. It is NOT needed at runtime — the
    # artifact carries its own copy of those bytes — so it is deleted by default rather than doubling
    # the output size. Useful when auditing what went into a build: its SHA-256 is the value recorded
    # in the artifact's manifest.
    [switch]$KeepPayload,

    # Skip building + staging the bundled plugins (step 2b). The artifact then ships WITHOUT them, so
    # fabricator_render does not resolve for anyone using it -- a deliberate choice, not a default.
    [switch]$SkipPlugins
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

if (-not $CorePath) { $CorePath = Join-Path $repo 'build/release/extension/fabricator/fabricator.duckdb_extension' }
if (-not $ManagedPath) { $ManagedPath = Join-Path $repo 'build/release/extension/fabricator/fabricator' }
$corePath = $CorePath
$managedPath = $ManagedPath

$shellProject = Join-Path $repo 'dotnet/Fabricator.Installer'
$shellName = if ($Rid -like 'win-*') { 'Fabricator.Installer.dll' }
             elseif ($Rid -like 'osx-*') { 'Fabricator.Installer.dylib' }
             else { 'Fabricator.Installer.so' }
# The platform segment ('x64') is present or absent depending on how the build was invoked, so probe
# rather than assume: a Windows publish lands under bin/x64/Release, a WSL one under bin/Release.
# MUST be called AFTER the publish below, never before: on a clean machine the file does not exist yet,
# so an up-front probe yields $null and the "not found" guard fires even though the publish succeeded.
# (That is precisely how this passed for months on developer boxes — a PREVIOUS publish had left the
# file there — and failed on the first clean CI runner.)
function Resolve-ShellLibrary {
    @(
        (Join-Path $shellProject "bin/x64/Release/net10.0/$Rid/publish/$shellName"),
        (Join-Path $shellProject "bin/Release/net10.0/$Rid/publish/$shellName")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
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

# --- 2b. BUNDLED PLUGINS ----------------------------------------------------------------------
# The payload IS the core loadable plus the managed directory, so a plugin ships only by living inside the
# managed directory. It goes under `<managed>/plugins/<name>/`, which PluginPaths.BundledRelativeRoot makes
# a DEFAULT search root (searched first, so a user-installed plugin of the same name still overrides it).
#
# /!\ ORDER IS LOAD-BEARING: this MUST run AFTER publish-managed.ps1 and BEFORE the pack. `dotnet publish`
# deletes files under the managed directory that a previous publish wrote and the current closure no longer
# contains -- the same mechanism that silently removed five SqlClient DLLs on 2026-08-18 -- so a plugin
# copied in BEFORE step 2 would simply not be in the artifact, with no error anywhere.
#
# /!\ And this is why it re-copies rather than checking for existence: the copy is CHEAP and the failure it
# prevents is SILENT.
if (-not $SkipPlugins) {
    Step 'Building and staging the bundled plugins'
    $pluginsRoot = Join-Path $managedPath 'plugins'
    # Each entry: the project to build, and the directory its build lands in (its csproj fixes OutputPath,
    # so no TFM and no RID appear here).
    $bundledPlugins = @(
        @{ Name = 'fluid'; Project = 'dotnet/Fabricator.FluidPlugin'; Output = 'build/plugins/fluid' }
    )
    foreach ($plugin in $bundledPlugins) {
        & dotnet build (Join-Path $repo $plugin.Project) -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "building the bundled plugin '$($plugin.Name)' failed" }
        $source = Join-Path $repo $plugin.Output
        if (-not (Test-Path $source)) { throw "bundled plugin '$($plugin.Name)' not found at $source" }

        # /!\ ASSERT THE PRIVATE NuGet CLOSURE, not merely the entry assembly. A library does not copy its
        # package closure to the output directory unless CopyLocalLockFileAssemblies is set; without it the
        # plugin LOADS and then throws FileNotFoundException at its first call. Shipping that would put the
        # failure in the user's session rather than in this build.
        $dlls = @(Get-ChildItem -Path $source -Filter *.dll -File)
        if ($dlls.Count -lt 2) {
            throw "bundled plugin '$($plugin.Name)' has $($dlls.Count) assembly/assemblies in $source - " +
                  'expected its dependency closure too (is CopyLocalLockFileAssemblies set in its csproj?)'
        }

        $destination = Join-Path $pluginsRoot $plugin.Name
        if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        # .dll ONLY: the build output also holds a .pdb, a .deps.json and the .plugin.zip archive, none of
        # which the scan needs -- and the ARCHIVE especially must not ride along, since the search is
        # RECURSIVE and a zip inside a plugin root is dead weight in every artifact.
        Copy-Item -Path (Join-Path $source '*.dll') -Destination $destination -Force
        Write-Host "    $($plugin.Name): $($dlls.Count) assemblies -> $destination"
    }
}

# --- 3. the NativeAOT installer shell --------------------------------------------------------
if (-not $SkipShell) {
    Step "Publishing the installer shell ($Rid, NativeAOT)"
    # IlcUseEnvironmentalTools: without it ilc runs its own vswhere probe for the linker and splices
    # the probe's FAILURE TEXT into the link command line. With it, the ambient vcvars tools are used.
    & dotnet publish $shellProject -c Release -r $Rid -p:IlcUseEnvironmentalTools=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (AOT) failed' }
}
$shellLibrary = Resolve-ShellLibrary   # resolve AFTER publishing — see the note on the function
if (-not $shellLibrary) {
    throw "Installer shell ($shellName for $Rid) not found — publish it on a $Rid machine first: " +
          "dotnet publish dotnet/Fabricator.Installer -c Release -r $Rid"
}

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
if (-not $KeepPayload) { Remove-Item $payload -Force -ErrorAction SilentlyContinue }
$size = (Get-Item $artifact).Length

# --- 6. optional negative artifacts for the smoke harness -------------------------------------
if ($WithNegatives) {
    Step 'Building the negative artifacts'

    $wrongDirectory = Join-Path $OutputDirectory '_negative'
    New-Item -ItemType Directory -Force -Path $wrongDirectory | Out-Null
    $wrongCombined = Join-Path $wrongDirectory 'fabricator.combined'
    # Same payload, a manifest that claims a different DuckDB version: exercises the gate.
    & dotnet run --project (Join-Path $repo 'dotnet/Fabricator.Installer.Pack') -c Release -- `
        --core $corePath --managed $managedPath --library $shellLibrary `
        --output $wrongCombined --payload (Join-Path $wrongDirectory 'payload.zip') `
        --duckdb-version 'v0.0.0' --platform $Platform --fabricator-version $FabricatorVersion | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'packing the version-mismatch artifact failed' }
    & python $metadataScript -l $wrongCombined -o (Join-Path $wrongDirectory 'fabricator.duckdb_extension') `
        -n fabricator -p $Platform -dv $CApiVersion -ev "v$($FabricatorVersion.TrimStart('v'))" `
        --abi-type C_STRUCT | Out-Null
    Remove-Item $wrongCombined, (Join-Path $wrongDirectory 'payload.zip') -Force -ErrorAction SilentlyContinue

    # The bare shell with a footer and nothing appended: exercises "this carries no payload".
    $bareDirectory = Join-Path $OutputDirectory '_nopayload'
    New-Item -ItemType Directory -Force -Path $bareDirectory | Out-Null
    & python $metadataScript -l $shellLibrary -o (Join-Path $bareDirectory 'fabricator.duckdb_extension') `
        -n fabricator -p $Platform -dv $CApiVersion -ev "v$($FabricatorVersion.TrimStart('v'))" `
        --abi-type C_STRUCT | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'packing the payload-less artifact failed' }

    Write-Host "negatives: $wrongDirectory, $bareDirectory"
}

Write-Host ''
Write-Host "artifact : $artifact" -ForegroundColor Green
Write-Host ("size     : {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Host "targets  : DuckDB $DuckDbVersion / $Platform, sku $Sku"
Write-Host ''
Write-Host "Load with:  LOAD '$artifact';   (requires allow_unsigned_extensions)"
