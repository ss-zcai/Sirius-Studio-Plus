<#
.SYNOPSIS
    Packs the custom node SDK: the Sirius.Plugins package, the guide, and a
    sample library that builds against the package.

.DESCRIPTION
    What a team writing custom nodes is handed. Unzip it anywhere, and

        dotnet build SampleNodes

    builds a working library from the package alone, with nothing from this
    repository - the nuget.config beside it points NuGet at the package
    folder inside the zip.

    That is also what this script checks before it zips anything: the sample
    is built in a temporary folder outside the repository, where neither
    Directory.Build.props nor central package management can quietly supply
    something the package forgot. A package that only builds inside the
    repository it came from is not a package.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/pack-sdk.ps1
#>
[CmdletBinding()]
param(
    # Where the zip is written.
    [string] $OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- version ---------------------------------------------------------------

# The contract's own version, not the application's.
[xml] $project = Get-Content 'src/Sirius.Plugins/Sirius.Plugins.csproj'
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not $version) { throw 'No <Version> in Sirius.Plugins.csproj.' }

Write-Host "Packing the custom node SDK $version" -ForegroundColor Cyan

# ---- staging, outside the repository ----------------------------------------

$staging = Join-Path ([IO.Path]::GetTempPath()) "sirius-sdk-$([Guid]::NewGuid().ToString('N'))"
$packages = Join-Path $staging 'packages'
$sample = Join-Path $staging 'SampleNodes'

New-Item -ItemType Directory -Force $packages, $sample | Out-Null

try {
    # ---- the package ---------------------------------------------------------

    Write-Host 'Packing Sirius.Plugins...'
    dotnet pack src/Sirius.Plugins/Sirius.Plugins.csproj -c Release --nologo --verbosity quiet -o $packages
    if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }

    # ---- the sample, as a team outside this repository would write it --------

    Copy-Item 'samples/SampleNodes/*.cs' $sample

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- Sirius Studio Plus is 64-bit only. -->
    <PlatformTarget>x64</PlatformTarget>

    <RootNamespace>Sirius.Samples</RootNamespace>
    <AssemblyName>Sirius.SampleNodes</AssemblyName>
    <Version>1.0.0</Version>

    <!-- Writes this library's own dependencies and a .deps.json beside it. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <!--
      ExcludeAssets="runtime": the application supplies Sirius.Plugins when
      it loads the library, so no copy is needed beside it.
    -->
    <PackageReference Include="Sirius.Plugins" Version="$version" ExcludeAssets="runtime" />
  </ItemGroup>

</Project>
"@
    Set-Content -Path (Join-Path $sample 'SampleNodes.csproj') -Value $csproj -Encoding UTF8

    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <!-- The Sirius.Plugins package, from the folder beside this file. -->
    <add key="Sirius SDK" value="packages" />
  </packageSources>
</configuration>
"@
    Set-Content -Path (Join-Path $staging 'nuget.config') -Value $nugetConfig -Encoding UTF8

    Copy-Item 'docs/custom-nodes.md' $staging

    # ---- prove it builds from the package alone -----------------------------

    Write-Host 'Building the sample against the package, outside the repository...'

    # A private package cache. NuGet never fetches a version it already
    # holds, so a check against the machine-wide cache would pass against
    # whatever 1.0.0 was packed first - not the package just built.
    $cache = Join-Path ([IO.Path]::GetTempPath()) "sirius-sdk-cache-$([Guid]::NewGuid().ToString('N'))"

    try {
        dotnet build $sample -c Release --nologo --verbosity quiet "-p:RestorePackagesPath=$cache"
        if ($LASTEXITCODE -ne 0) { throw 'The sample does not build against the package; nothing was zipped.' }
    }
    finally {
        Remove-Item -Recurse -Force $cache -ErrorAction SilentlyContinue
    }

    $built = Get-ChildItem -Path $sample -Recurse -Filter 'Sirius.SampleNodes.dll' |
        Where-Object { $_.FullName -match '\\bin\\' } |
        Select-Object -First 1

    if (-not $built) { throw 'The sample built, but Sirius.SampleNodes.dll is not in its output.' }

    # The check above leaves bin/ and obj/ behind; the zip should hold
    # sources only, as a team would check them out.
    Remove-Item -Recurse -Force (Join-Path $sample 'bin'), (Join-Path $sample 'obj')

    # ---- zip --------------------------------------------------------------------

    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $zip = Join-Path (Resolve-Path $OutputDirectory) "Sirius.Plugins-SDK-$version.zip"

    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "Wrote $zip" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
