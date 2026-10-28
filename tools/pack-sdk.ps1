<#
.SYNOPSIS
    Packs the custom node SDK: the Sirius.Plugins package, the guide, and a
    sample library that builds against the package.

.DESCRIPTION
    What a team writing custom nodes is handed. Unzip it anywhere, and

        dotnet build SampleNodes.sln
        dotnet test  SampleNodes.sln

    builds a working library and runs its tests from the package alone, with
    nothing from this repository - the nuget.config beside it points NuGet
    at the package folder inside the zip.

    That is also what this script checks before it zips anything: the sample
    and its tests are built and run in a temporary folder outside the
    repository, where neither Directory.Build.props nor central package
    management can quietly supply something the package forgot. A package
    that only builds inside the repository it came from is not a package.

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
$tests = Join-Path $staging 'SampleNodes.Tests'

New-Item -ItemType Directory -Force $packages, $sample, $tests | Out-Null

# The test packages at the versions this repository tests with.
[xml] $central = Get-Content 'Directory.Packages.props'
function Get-PackageVersion([string] $id) {
    $entry = $central.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq $id }
    if (-not $entry) { throw "No version for $id in Directory.Packages.props." }
    return $entry.Version
}
$testSdk = Get-PackageVersion 'Microsoft.NET.Test.Sdk'
$xunit = Get-PackageVersion 'xunit'
$xunitRunner = Get-PackageVersion 'xunit.runner.visualstudio'

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

    # ---- and its tests --------------------------------------------------------

    Copy-Item 'samples/SampleNodes.Tests/*.cs' $tests

    $testsProject = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
    <RootNamespace>Sirius.Samples.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="$testSdk" />
    <PackageReference Include="xunit" Version="$xunit" />
    <PackageReference Include="xunit.runner.visualstudio" Version="$xunitRunner" />

    <!--
      Unlike the library, its tests run without the application, so they
      need Sirius.Plugins at run time - no ExcludeAssets here.
    -->
    <PackageReference Include="Sirius.Plugins" Version="$version" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SampleNodes\SampleNodes.csproj" />
  </ItemGroup>

</Project>
"@
    Set-Content -Path (Join-Path $tests 'SampleNodes.Tests.csproj') -Value $testsProject -Encoding UTF8

    # ---- around them ------------------------------------------------------------

    Push-Location $staging
    try {
        dotnet new sln --name SampleNodes --format sln --output . | Out-Null
        dotnet sln SampleNodes.sln add SampleNodes/SampleNodes.csproj SampleNodes.Tests/SampleNodes.Tests.csproj | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the sample solution.' }
    }
    finally {
        Pop-Location
    }

    $gitignore = @"
bin/
obj/
.vs/
*.user
"@
    Set-Content -Path (Join-Path $staging '.gitignore') -Value $gitignore -Encoding UTF8

    Copy-Item 'docs/sdk-readme.md' (Join-Path $staging 'README.md')
    Copy-Item 'docs/custom-nodes-api.md' $staging

    # For coding agents. Codex and most others read AGENTS.md; Claude Code
    # reads CLAUDE.md, which imports it, so both follow the same rules.
    Copy-Item 'docs/sdk-agent-guide.md' (Join-Path $staging 'AGENTS.md')

    $claude = @"
# CLAUDE.md

The instructions for coding agents in this folder are in AGENTS.md, shared
with Codex and other tools so every agent follows the same rules.

@AGENTS.md
"@
    Set-Content -Path (Join-Path $staging 'CLAUDE.md') -Value $claude -Encoding UTF8

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

    Write-Host 'Building and testing the sample against the package, outside the repository...'

    # A private package cache. NuGet never fetches a version it already
    # holds, so a check against the machine-wide cache would pass against
    # whatever 1.0.0 was packed first - not the package just built.
    $cache = Join-Path ([IO.Path]::GetTempPath()) "sirius-sdk-cache-$([Guid]::NewGuid().ToString('N'))"

    try {
        $solution = Join-Path $staging 'SampleNodes.sln'

        dotnet build $solution -c Release --nologo --verbosity quiet "-p:RestorePackagesPath=$cache"
        if ($LASTEXITCODE -ne 0) { throw 'The sample does not build against the package; nothing was zipped.' }

        dotnet test $solution -c Release --no-build --nologo --verbosity quiet "-p:RestorePackagesPath=$cache"
        if ($LASTEXITCODE -ne 0) { throw 'The sample tests fail against the package; nothing was zipped.' }
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
    foreach ($folder in $sample, $tests) {
        Remove-Item -Recurse -Force (Join-Path $folder 'bin'), (Join-Path $folder 'obj') -ErrorAction SilentlyContinue
    }
    Get-ChildItem $staging -Directory -Filter 'TestResults' | Remove-Item -Recurse -Force

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
