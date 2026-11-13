<#
.SYNOPSIS
    Packs the PLC kit: Sirius Builder, the PLC driver SDK, a sample driver
    with its tests, and the guides - what the PLC team is handed.

.DESCRIPTION
    Builder is published self-contained, so it runs on a PC with nothing
    installed. The SDK is Sirius.Plc and Sirius.Core, with Sirius.Plc's
    documentation comments for IntelliSense, and a sample driver solution
    that builds against them alone.

    Before anything is zipped, the sample is built and its tests run in a
    folder outside the repository, from the kit's own files - as the team
    will build it. A kit whose sample only builds inside the repository is
    not a kit. The sample driver so built is then installed in the kit's
    Builder\plc-drivers\Sample\, which is the proof that a driver built
    from the kit loads.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/pack-plc-kit.ps1
#>
[CmdletBinding()]
param(
    # Where the zip is written.
    [string] $OutputDirectory = 'dist',

    # Skip the PLC and Builder tests in the repository first.
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- version ---------------------------------------------------------------

# The driver interface's own version: what a driver built from the kit is checked against.
[xml] $plc = Get-Content 'src/Sirius.Plc/Sirius.Plc.csproj'
$version = ($plc.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Sirius.Plc.csproj.' }

[xml] $builder = Get-Content 'src/Sirius.Builder/Sirius.Builder.csproj'
$builderVersion = ($builder.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

Write-Host "Packing the PLC kit $version (Sirius Builder $builderVersion)" -ForegroundColor Cyan

# ---- tests -----------------------------------------------------------------

if (-not $SkipTests) {
    Write-Host 'Running the PLC and Builder tests...'
    dotnet test tests/Sirius.Core.Tests --nologo --verbosity quiet `
        --filter 'FullyQualifiedName~Plc|FullyQualifiedName~BuilderController|FullyQualifiedName~BuilderTests'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was packed.' }
}

# ---- staging, outside the repository ----------------------------------------

$staging = Join-Path ([IO.Path]::GetTempPath()) "sirius-plc-kit-$([Guid]::NewGuid().ToString('N'))"
$builderOut = Join-Path $staging 'Builder'
$sdk = Join-Path $staging 'sdk'
$lib = Join-Path $sdk 'lib'
$docs = Join-Path $staging 'docs'

New-Item -ItemType Directory $staging, $lib, $docs | Out-Null

try {
    # ---- Sirius Builder ------------------------------------------------------------

    Write-Host 'Publishing Sirius Builder...'
    dotnet publish src/Sirius.Builder/Sirius.Builder.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $builderOut `
        --nologo `
        --verbosity quiet `
        -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw 'Sirius Builder did not publish.' }

    New-Item -ItemType Directory (Join-Path $builderOut 'plc-drivers') -Force | Out-Null

    # ---- the SDK --------------------------------------------------------------------

    # The very DLLs Builder runs, so a driver is built against what loads it.
    foreach ($file in 'Sirius.Plc.dll', 'Sirius.Core.dll', 'CommunityToolkit.Mvvm.dll') {
        Copy-Item (Join-Path $builderOut $file) $lib
    }

    $comments = 'src/Sirius.Plc/bin/Release/net8.0/Sirius.Plc.xml'
    if (-not (Test-Path $comments)) { throw "No $comments - is GenerateDocumentationFile on in Sirius.Plc.csproj?" }
    Copy-Item $comments $lib

    # The sample, without the repository's guard files or anything built.
    foreach ($item in 'SampleDriver', 'SampleDriver.Tests', 'SampleDriver.sln') {
        Copy-Item (Join-Path 'samples/PlcDrivers' $item) $sdk -Recurse
    }

    Get-ChildItem $sdk -Directory -Recurse -Include 'bin', 'obj' | Remove-Item -Recurse -Force

    # ---- the sample, built and tested as the team will -----------------------------------

    $check = Join-Path ([IO.Path]::GetTempPath()) "sirius-plc-kit-check-$([Guid]::NewGuid().ToString('N'))"
    Copy-Item $sdk $check -Recurse

    try {
        Write-Host 'Testing the sample driver from the kit alone...'
        dotnet test (Join-Path $check 'SampleDriver.sln') --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "The kit's sample driver failed its tests." }

        Write-Host 'Building the sample driver for Builder...'
        dotnet build (Join-Path $check 'SampleDriver/SampleDriver.csproj') --configuration Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "The kit's sample driver did not build." }

        # Installed as a driver is: its build output, in a folder of its own.
        $installed = Join-Path $builderOut 'plc-drivers/Sample'
        New-Item -ItemType Directory $installed | Out-Null
        Copy-Item (Join-Path $check 'SampleDriver/bin/Release/net8.0/*') $installed -Recurse
    }
    finally {
        Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ---- the guides ------------------------------------------------------------------

    Copy-Item 'docs/plc-drivers.md', 'docs/plc-workflows.md' $docs
    Copy-Item 'docs/plc-kit-readme.md' (Join-Path $staging 'README.md')

    # ---- check -----------------------------------------------------------------------

    $required = @(
        'README.md'
        'Builder/SiriusBuilder.exe'
        'Builder/Sirius.Plc.dll'
        'Builder/THIRD-PARTY-NOTICES.txt'
        'Builder/plc-drivers/Sample/Sample.Drivers.dll'
        'Builder/plc-drivers/Sample/Sample.Drivers.deps.json'
        'sdk/lib/Sirius.Plc.dll'
        'sdk/lib/Sirius.Plc.xml'
        'sdk/lib/Sirius.Core.dll'
        'sdk/SampleDriver.sln'
        'sdk/SampleDriver/RegisterDriver.cs'
        'sdk/SampleDriver.Tests/ConformanceTests.cs'
        'docs/plc-drivers.md'
        'docs/plc-workflows.md'
    )

    $missing = $required | Where-Object { -not (Test-Path (Join-Path $staging $_)) }
    if ($missing) { throw "The kit is missing: $($missing -join ', ')" }

    # The program's own copies only: a Sirius.Plc beside the sample driver would be ignored, but should not be there.
    if (Test-Path (Join-Path $builderOut 'plc-drivers/Sample/Sirius.Plc.dll')) {
        throw 'Sirius.Plc.dll was copied beside the sample driver: its reference must be Private=false.'
    }

    $stamped = (Get-Item (Join-Path $builderOut 'SiriusBuilder.exe')).VersionInfo.ProductVersion
    if (-not $stamped.StartsWith($builderVersion)) {
        throw "SiriusBuilder.exe reports $stamped but Sirius.Builder.csproj says $builderVersion."
    }

    # ---- zip -------------------------------------------------------------------------

    if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory $OutputDirectory | Out-Null }

    $zip = Join-Path $OutputDirectory "Sirius-PLC-Kit-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Host 'Compressing...'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Wrote $zip ($size MB)" -ForegroundColor Green
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
