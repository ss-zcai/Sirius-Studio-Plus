<#
.SYNOPSIS
    Zips the kit for sending on.

.DESCRIPTION
    Runs the tests, then writes into dist\:

      NodeGraphKit-<version>-src.zip    the whole kit as source - what a team builds on
      NodeGraphKit-Demo-<version>.zip   with -WithDemo: the demo built, to try without
                                        building (needs the .NET 8 Desktop Runtime)

    The version is <Version> in Directory.Build.props.

.EXAMPLE
    .\pack.ps1
    .\pack.ps1 -WithDemo
#>
param(
    [switch]$WithDemo,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root 'dist'
$staging = Join-Path $dist 'staging'

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'NodeGraphKit.sln') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was packed.' }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

# ---- source ------------------------------------------------------------------

$skip = '\\(bin|obj|dist|\.vs|__pycache__)(\\|$)'
$source = Join-Path $staging "NodeGraphKit-$version"

Get-ChildItem $root -Recurse -File |
    Where-Object { $_.FullName.Substring($root.Length) -notmatch $skip -and $_.Extension -ne '.user' } |
    ForEach-Object {
        $target = Join-Path $source $_.FullName.Substring($root.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $_.FullName $target
    }

$sourceZip = Join-Path $dist "NodeGraphKit-$version-src.zip"
if (Test-Path $sourceZip) { Remove-Item $sourceZip }
Compress-Archive -Path $source -DestinationPath $sourceZip
"wrote $sourceZip"

# ---- built demo ----------------------------------------------------------------

if ($WithDemo) {
    $demo = Join-Path $staging "NodeGraphKit-Demo-$version"

    dotnet publish (Join-Path $root 'src\NodeGraphKit.Demo\NodeGraphKit.Demo.csproj') -c Release -o $demo -nologo -v q --self-contained false
    if ($LASTEXITCODE -ne 0) { throw 'The demo did not build.' }

    Get-ChildItem $demo -Filter *.pdb | Remove-Item
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt'), (Join-Path $root 'LICENSE-avalondock-mspl.txt') $demo
    Copy-Item (Join-Path $root 'res\icons\LICENSE-phosphor.txt') $demo

    $demoZip = Join-Path $dist "NodeGraphKit-Demo-$version.zip"
    if (Test-Path $demoZip) { Remove-Item $demoZip }
    Compress-Archive -Path $demo -DestinationPath $demoZip
    "wrote $demoZip  (run NodeGraphDemo.exe; needs the .NET 8 Desktop Runtime)"
}

Remove-Item $staging -Recurse -Force
