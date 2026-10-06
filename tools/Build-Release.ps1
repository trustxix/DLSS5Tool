<#
.SYNOPSIS
    Builds the distributable DLSS5Tool.exe: payload + tests + single-file self-contained publish + checksums.

.PARAMETER Version
    Semantic version to stamp (e.g. 1.0.0). Defaults to the latest "v*" git tag, else 0.1.0.

.PARAMETER Full
    Embed every component (private builds only; see Build-Payload.ps1).

.PARAMETER SkipTests
    Do not run the test suite first.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $Full,
    [switch] $SkipTests,
    [string] $OutDir = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$proj = Join-Path $repo 'src\DLSS5Tool\DLSS5Tool.csproj'

if (-not $Version) {
    $tag = (git -C $repo describe --tags --abbrev=0 --match 'v*' 2>$null)
    $Version = if ($tag) { $tag.TrimStart('v') } else { '0.1.0' }
}
Write-Host "Version $Version (full=$([bool]$Full))"

& (Join-Path $PSScriptRoot 'Build-Payload.ps1') -Full:$Full
if ($LASTEXITCODE) { throw 'payload build failed' }

if (-not $SkipTests) {
    dotnet test (Join-Path $repo 'src\DLSS5Tool.Tests\DLSS5Tool.Tests.csproj') -c Release --nologo -v minimal
    if ($LASTEXITCODE) { throw 'tests failed' }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Remove-Item (Join-Path $OutDir '*') -Force -ErrorAction SilentlyContinue
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:Publishing=true -p:Version=$Version -p:DebugType=none -o $OutDir --nologo -v minimal
if ($LASTEXITCODE) { throw 'publish failed' }

Get-ChildItem $OutDir -Exclude 'DLSS5Tool.exe' | Remove-Item -Force -Recurse
$exe = Join-Path $OutDir 'DLSS5Tool.exe'
if ($Full) { Rename-Item $exe 'DLSS5Tool-full.exe'; $exe = Join-Path $OutDir 'DLSS5Tool-full.exe' }
$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
"$hash  $(Split-Path $exe -Leaf)" | Set-Content (Join-Path $OutDir 'SHA256SUMS.txt') -Encoding ascii
Write-Host ("built {0} ({1:N1} MB) sha256 {2}" -f (Split-Path $exe -Leaf), ((Get-Item $exe).Length / 1MB), $hash)
