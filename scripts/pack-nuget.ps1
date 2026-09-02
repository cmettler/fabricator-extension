# Packs the two assemblies a fabricator PLUGIN may reference into a local NuGet folder feed.
#
# WHY THERE IS A SCRIPT rather than two `dotnet pack` lines in a doc: it is the one place that says WHICH
# projects are packable. `Directory.Build.props` sets IsPackable=false for the whole folder and the two
# contract assemblies opt back in, so `dotnet pack` on a wrong project silently produces nothing and looks
# like it worked - naming them here makes the set reviewable, and gives the release-asset step somewhere to
# land later.
#
# The version comes from ./VERSION, the SAME file CMakeLists.txt and extension_config.cmake read, so a
# package can never claim a version the extension does not.
#
# Consume it from a plugin repo with a nuget.config beside its csproj:
#
#   <configuration>
#     <packageSources>
#       <add key="fabricator-local" value="D:\repos\fabricator-extension\artifacts" />
#     </packageSources>
#   </configuration>
#
# then <PackageReference Include="Fabricator.Abstractions" Version="0.0.13" ExcludeAssets="runtime" />.
#
# /!\ ExcludeAssets="runtime" is REQUIRED, and MEASURED rather than assumed (section 11.8): a plugin sets
# CopyLocalLockFileAssemblies=true to get its OWN dependencies copied, and that is what makes the contract
# package copy too - a second copy of it beside a plugin makes every type it hands the host a different,
# non-assignable one. On a plain library neither form copies, so the attribute matters only for the plugin
# shape. The good news is that ONE attribute on the top-level package suppresses the whole transitive
# closure (Abstractions + Apache.Arrow + Apache.Arrow.Scalars), unlike the ProjectReference form, where
# Apache.Arrow.Scalars has to be named separately. docs/plugin-services.md section 11.8.
param(
    [string]$Output = "$PSScriptRoot/../artifacts",
    [string]$Configuration = "Release",
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path "$PSScriptRoot/.."
$version = (Get-Content "$root/VERSION" -Raw).Trim()
$projects = @(
    'dotnet/Fabricator.Abstractions/Fabricator.Abstractions.csproj',
    'dotnet/Fabricator.Common/Fabricator.Common.csproj'
)

if ($Clean -and (Test-Path $Output)) {
    Remove-Item -Recurse -Force $Output
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

foreach ($proj in $projects) {
    Write-Host "packing $proj ($version)"
    dotnet pack (Join-Path $root $proj) -c $Configuration -o $Output --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $proj" }
}

# Assert what was produced rather than trusting the exit code: `dotnet pack` on a project whose IsPackable
# is false succeeds and emits NOTHING, which is exactly the silent failure this check exists to catch.
foreach ($proj in $projects) {
    $id = [System.IO.Path]::GetFileNameWithoutExtension($proj)
    $pkg = Join-Path $Output "$id.$version.nupkg"
    if (-not (Test-Path $pkg)) { throw "expected $pkg - pack produced no package for $id" }
}
Write-Host "packed $($projects.Count) package(s) at $version into $Output"
