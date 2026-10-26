<#
.SYNOPSIS
    Publishes Sirius Studio Plus and zips it for distribution.

.DESCRIPTION
    Self-contained, so a tester needs nothing installed: a machine without
    the .NET Desktop Runtime would otherwise greet them with an installer
    prompt instead of the application. It costs about 70 MB on top of the
    native OpenCV binaries, which is the right trade for internal testing.

    The version comes from Directory.Build.props, so the package name and
    the About box can never disagree.

    After zipping, the contents are checked against a list of files that
    must be present. A package missing its licence notices, or its node
    reference, is a package that should not leave the building - and both
    have gone missing before, silently, because nothing looked.

.EXAMPLE
    pwsh tools/pack.ps1
#>
[CmdletBinding()]
param(
    # Where the zip is written.
    [string] $OutputDirectory = 'dist',

    # Skip the test run. For a quick local package only.
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- version ---------------------------------------------------------------

[xml] $props = Get-Content 'Directory.Build.props'
$version = $props.Project.PropertyGroup.Version

if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

Write-Host "Packing Sirius Studio Plus $version" -ForegroundColor Cyan

# ---- tests -----------------------------------------------------------------

if (-not $SkipTests) {
    Write-Host 'Running tests...'
    dotnet test --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was packed.' }
}

# ---- node reference --------------------------------------------------------

# Regenerated rather than trusted: it ships inside the package, and a stale
# copy is worse than none because nothing about it looks stale.
Write-Host 'Generating the node reference...'
dotnet run --project tools/NodeDocGen --verbosity quiet -- docs/node-reference.md
if ($LASTEXITCODE -ne 0) { throw 'Could not generate the node reference.' }

# ---- publish ---------------------------------------------------------------

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "sirius-pack-$version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

Write-Host 'Publishing...'
dotnet publish src/Sirius.Studio.App/Sirius.Studio.App.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    --nologo `
    --verbosity quiet `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# ---- check -----------------------------------------------------------------

$required = @(
    'SiriusStudioPlus.exe'
    'SiriusStudioPlus.dll'
    'THIRD-PARTY-NOTICES.txt'
    'LICENSE-phosphor.txt'
    'docs/node-reference.md'
    'docs/release-notes.md'
    'cvextern.dll'
)

$missing = $required | Where-Object { -not (Test-Path (Join-Path $staging $_)) }

if ($missing) {
    throw "The published output is missing: $($missing -join ', ')"
}

$stamped = (Get-Item (Join-Path $staging 'SiriusStudioPlus.exe')).VersionInfo.ProductVersion

if (-not $stamped.StartsWith($version)) {
    throw "The executable reports $stamped but the package is $version."
}

# ---- zip -------------------------------------------------------------------

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory $OutputDirectory | Out-Null
}

$zip = Join-Path $OutputDirectory "SiriusStudioPlus-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host 'Compressing...'
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item $staging -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Wrote $zip ($size MB)" -ForegroundColor Green
