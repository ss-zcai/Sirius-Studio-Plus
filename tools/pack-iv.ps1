<#
.SYNOPSIS
    Publishes Sirius IV on its own, and zips it and makes its installer,
    for testing the image viewer without the rest of the suite.

.DESCRIPTION
    Self-contained, as the full package is: a tester needs nothing
    installed. Sirius IV is built on Studio's viewer and theme, so OpenCV
    comes with it; the camera drivers and PLC library come too, unused.

    The version is Sirius IV's own (src/Apps/Sirius.IV/Sirius.IV.csproj), so
    the package name and its About box cannot disagree. The full package,
    tools/pack.ps1, carries the same SiriusIV.exe beside the other programs.

    The installer, SiriusIV-<version>-Setup.exe, is made from the same
    published folder by Inno Setup 6 (tools/installer/SiriusIV.iss); its
    compiler is looked for where Inno Setup installs, or given with
    -InnoSetup.

.EXAMPLE
    pwsh tools/pack-iv.ps1
#>
[CmdletBinding()]
param(
    # Where the zip is written.
    [string] $OutputDirectory = 'dist',

    # Skip the tests. For a quick local package only.
    [switch] $SkipTests,

    # The zip only, without the installer.
    [switch] $NoInstaller,

    # Inno Setup's compiler, ISCC.exe, when not where it installs.
    [string] $InnoSetup
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- version ---------------------------------------------------------------

[xml] $project = Get-Content 'src/Apps/Sirius.IV/Sirius.IV.csproj'
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not $version) { throw 'No <Version> in src/Apps/Sirius.IV/Sirius.IV.csproj.' }

Write-Host "Packing Sirius IV $version" -ForegroundColor Cyan

# ---- tests -----------------------------------------------------------------

if (-not $SkipTests) {
    Write-Host 'Running tests...'
    dotnet test --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was packed.' }
}

# ---- publish ---------------------------------------------------------------

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "sirius-iv-pack-$version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

Write-Host 'Publishing Sirius IV...'
dotnet publish src/Apps/Sirius.IV/Sirius.IV.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    --nologo `
    --verbosity quiet `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Sirius IV did not publish.' }

# ---- what to read first ------------------------------------------------------

# What it is, how a station's pictures and their .sann files are laid out,
# and what changed: the overview as the README, as in the full package.
New-Item -ItemType Directory -Force (Join-Path $staging 'docs') | Out-Null
Copy-Item 'docs/overview.md' (Join-Path $staging 'README.md')
Copy-Item 'docs/overview.md', 'docs/design/annotation-format.md', 'docs/project/release-notes.md' (Join-Path $staging 'docs')

# ---- check -----------------------------------------------------------------

$required = @(
    'SiriusIV.exe'
    'Sirius.UI.dll'
    'THIRD-PARTY-NOTICES.txt'
    'LICENSE-phosphor.txt'
    'LICENSE-apache-2.0.txt'
    'LICENSE-avalondock-mspl.txt'
    'README.md'
    'docs/overview.md'
    'docs/annotation-format.md'
    'docs/release-notes.md'
    'cvextern.dll'
)

$missing = $required | Where-Object { -not (Test-Path (Join-Path $staging $_)) }

if ($missing) {
    throw "The published output is missing: $($missing -join ', ')"
}

$stamped = (Get-Item (Join-Path $staging 'SiriusIV.exe')).VersionInfo.ProductVersion

if (-not $stamped.StartsWith($version)) {
    throw "SiriusIV.exe reports $stamped but the package is $version."
}

Write-Host "SiriusIV.exe is $stamped"

# ---- zip -------------------------------------------------------------------

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory $OutputDirectory | Out-Null
}

$zip = Join-Path $OutputDirectory "SiriusIV-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host 'Compressing...'
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Wrote $zip ($size MB)" -ForegroundColor Green

# ---- installer ---------------------------------------------------------------

if (-not $NoInstaller) {
    $iscc = @(
        $InnoSetup
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $iscc) {
        Remove-Item $staging -Recurse -Force
        throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup), or give -InnoSetup; -NoInstaller for the zip alone.'
    }

    $output = (Resolve-Path $OutputDirectory).Path
    $setup = Join-Path $output "SiriusIV-$version-Setup.exe"
    if (Test-Path $setup) { Remove-Item $setup -Force }

    Write-Host 'Making the installer...'
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$staging" "/DOutputDir=$output" 'tools/installer/SiriusIV.iss'
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $staging -Recurse -Force
        throw 'The installer was not made.'
    }

    $size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "Wrote $setup ($size MB)" -ForegroundColor Green
}

Remove-Item $staging -Recurse -Force
