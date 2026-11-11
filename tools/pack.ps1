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

# ---- command-line runner ---------------------------------------------------

# Into the same folder as the application, whose runtime and libraries it
# shares: one copy of OpenCV, not two.
Write-Host 'Publishing the command-line runner...'
dotnet publish src/Sirius.Runner/Sirius.Runner.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    --nologo `
    --verbosity quiet `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'The command-line runner did not publish.' }

# ---- Sirius Builder and Sirius Edge ------------------------------------------

# Builder builds projects - stations, screens, line settings - from Studio's
# recipes; Edge runs them on the machine, full screen (decision 71). Into the
# same folder, sharing Studio's libraries: both are built on its screens.
Write-Host 'Publishing Sirius Builder...'
dotnet publish src/Sirius.Builder/Sirius.Builder.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    --nologo `
    --verbosity quiet `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Sirius Builder did not publish.' }

Write-Host 'Publishing Sirius Edge...'
dotnet publish src/Sirius.Edge/Sirius.Edge.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    --nologo `
    --verbosity quiet `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Sirius Edge did not publish.' }
# ---- sample custom node library --------------------------------------------

# Beside the application rather than in its plugins folder, so a tester
# adds it by hand - which is the part of the feature worth testing.
Write-Host 'Building the sample custom node library...'
$sampleOut = Join-Path $staging 'samples/SampleNodes'
dotnet build samples/SampleNodes/SampleNodes.csproj `
    --configuration Release `
    --output $sampleOut `
    --nologo `
    --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'The sample custom node library did not build.' }

# The application supplies these; a copy beside the library would only be
# ignored, and would make a tester wonder which one is in use.
Get-ChildItem $sampleOut -File |
    Where-Object { $_.Name -match '^(Sirius\.Plugins|Sirius\.Core|CommunityToolkit\.Mvvm)\.' } |
    Remove-Item -Force

# ---- sample Python node library ---------------------------------------------

# Beside the C# one, for the same reason, with the guide to go with it. Not
# sirius.json: that names a Python on the machine it was written on.
Write-Host 'Adding the sample Python node library...'
$pythonSample = Join-Path $staging 'samples/PythonNodes'
New-Item -ItemType Directory -Force $pythonSample | Out-Null
Copy-Item 'samples/PythonNodes/*.py' $pythonSample
Copy-Item 'samples/PythonNodes/sirius.json.example' $pythonSample
Copy-Item 'docs/python-nodes.md' (Join-Path $staging 'docs')

# ---- sample C++ node library -------------------------------------------------

# Its source, and the DLL built from it against the headers this package
# carries - so a tester can add a C++ node by hand without a compiler, and
# so a package whose headers do not compile never leaves here.
Write-Host 'Building the sample C++ node library...'
$cppSample = Join-Path $staging 'samples/CppNodes'
New-Item -ItemType Directory -Force $cppSample | Out-Null
Copy-Item 'samples/CppNodes/sample_nodes.cpp', 'samples/CppNodes/build.cmd', 'samples/CppNodes/CMakeLists.txt' $cppSample
Copy-Item 'docs/cpp-nodes.md' (Join-Path $staging 'docs')

& cmd /c "`"$(Join-Path $cppSample 'build.cmd')`" `"$(Join-Path $staging 'cpp')`""
if ($LASTEXITCODE -ne 0) {
    throw 'The sample C++ node library did not build. It needs Visual Studio with "Desktop development with C++".'
}

# The DLL and its .pdb stay; the rest is what the compiler left behind.
Get-ChildItem $cppSample -File |
    Where-Object { $_.Extension -in '.obj', '.exp', '.lib', '.ilk' -or $_.Name -like 'vc*.pdb' } |
    Remove-Item -Force

# ---- check -----------------------------------------------------------------

$required = @(
    'SiriusStudioPlus.exe'
    'SiriusStudioPlus.dll'
    'sirius-run.exe'
    'SiriusBuilder.exe'
    'SiriusEdge.exe'
    'THIRD-PARTY-NOTICES.txt'
    'LICENSE-phosphor.txt'
    'LICENSE-apache-2.0.txt'
    'LICENSE-avalondock-mspl.txt'
    'docs/node-reference.md'
    'docs/release-notes.md'
    'cvextern.dll'
    'Clipper2Lib.dll'
    'tessdata/eng.traineddata'
    'Sirius.Plugins.dll'
    'samples/SampleNodes/Sirius.SampleNodes.dll'
    'python/sirius_nodes.py'
    'python/sirius_worker.py'
    'samples/PythonNodes/dark_spots.py'
    'docs/python-nodes.md'
    'cpp/sirius_node.h'
    'cpp/sirius_node.hpp'
    'samples/CppNodes/sample_nodes.cpp'
    'samples/CppNodes/SampleCppNodes.dll'
    'docs/cpp-nodes.md'

    # The camera makers' .NET APIs. Without them the drivers build empty and
    # the package lists no Basler or Hikrobot cameras, which nothing else
    # would notice: pack from a shell that has PYLON_DEV_DIR and
    # MVCAM_COMMON_RUNENV - one opened after pylon and MVS were installed.
    'Basler.Pylon.dll'
    'MvCameraControl.Net.dll'
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
