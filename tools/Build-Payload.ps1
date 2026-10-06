<#
.SYNOPSIS
    Assembles src/DLSS5Tool/payload.zip from the pinned sources in src/DLSS5Tool/components.json.

.DESCRIPTION
    Downloads every source archive into tools/.cache, verifies it against the pinned SHA-256, extracts the listed
    files, verifies each of them, and zips them as <component id>/<path>. By default only components whose
    "distribution" is "bundled" (freely redistributable) are included; -Full embeds everything, for private builds.
    Idempotent: cached archives are reused when their hash matches.

.PARAMETER Full
    Also embed the on-demand components (LumeniteFX, RenoDX DLSS 5, NVIDIA runtimes). Do not publish such a build:
    LumeniteFX's license forbids mirroring and the NVIDIA runtimes are not licensed for standalone redistribution.
#>
[CmdletBinding()]
param(
    [switch] $Full,
    [string] $Output = (Join-Path $PSScriptRoot '..\src\DLSS5Tool\payload.zip')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$catalogPath = Join-Path $PSScriptRoot '..\src\DLSS5Tool\components.json'
$cache = Join-Path $PSScriptRoot '.cache'
$stage = Join-Path $PSScriptRoot '.stage'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Get-Sha256([string] $path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

function Get-Verified([string] $url, [string] $sha256, [string] $dest) {
    if ((Test-Path -LiteralPath $dest) -and ((Get-Sha256 $dest) -eq $sha256)) { Write-Host "  cached   $(Split-Path $dest -Leaf)"; return }
    Write-Host "  download $url"
    $tmp = "$dest.part"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -Headers @{ 'User-Agent' = 'DLSS5Tool-build' }
    $actual = Get-Sha256 $tmp
    if ($actual -ne $sha256) { Remove-Item $tmp -Force; throw "SHA-256 mismatch for $url`n  expected $sha256`n  got      $actual" }
    Move-Item -LiteralPath $tmp -Destination $dest -Force
}

function Open-SourceZip([string] $path, [string] $format) {
    if ($format -eq 'reshade-setup') {
        # ReShade's setup is a .NET exe with a plain zip appended; find the first PK header that opens as a zip containing the DLLs.
        $bytes = [IO.File]::ReadAllBytes($path)
        for ($i = 0; $i -lt $bytes.Length - 30; $i++) {
            if ($bytes[$i] -ne 0x50 -or $bytes[$i + 1] -ne 0x4B -or $bytes[$i + 2] -ne 0x03 -or $bytes[$i + 3] -ne 0x04) { continue }
            $ms = New-Object IO.MemoryStream(, $bytes[$i..($bytes.Length - 1)])
            try {
                $z = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Read, $false)
                if ($z.GetEntry('ReShade64.dll')) { return $z }
                $z.Dispose()
            }
            catch { $ms.Dispose() }
        }
        throw "No embedded archive found in $path"
    }
    return [IO.Compression.ZipFile]::OpenRead($path)
}

$catalog = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json
$manifest = [ordered]@{ builtUtc = (Get-Date).ToUniversalTime().ToString('o'); full = [bool]$Full; components = @() }

foreach ($c in $catalog.components) {
    $bundle = ($c.distribution -eq 'bundled') -or $Full
    if (-not $bundle) { Write-Host "skip     $($c.id) ($($c.distribution))"; continue }
    Write-Host "component $($c.id) $($c.version)"
    $destRoot = Join-Path $stage $c.id
    New-Item -ItemType Directory -Force -Path $destRoot | Out-Null

    if ($c.PSObject.Properties['source'] -and $c.source) {
        $name = [IO.Path]::GetFileName(([Uri]$c.source.url).LocalPath)
        $archive = Join-Path $cache ("$($c.id)-$($c.version -replace '[^A-Za-z0-9.\-]', '_')-$name")
        Get-Verified $c.source.url $c.source.sha256 $archive
        $zip = Open-SourceZip $archive $c.source.format
        try {
            $prefix = if ($c.source.PSObject.Properties['stripPrefix']) { $c.source.stripPrefix } else { '' }
            $byName = @{}
            foreach ($e in $zip.Entries) {
                $n = $e.FullName -replace '\\', '/'
                if ($n.EndsWith('/')) { continue }
                if ($prefix -and $n.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $n = $n.Substring($prefix.Length) }
                $byName[$n.ToLowerInvariant()] = $e
            }
            foreach ($f in $c.files) {
                $e = $byName[($f.path -replace '\\', '/').ToLowerInvariant()]
                if (-not $e) { throw "$archive has no entry $($f.path)" }
                $dest = Join-Path $destRoot $f.path
                New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true)
                $actual = Get-Sha256 $dest
                if ($actual -ne $f.sha256) { throw "SHA-256 mismatch for $($c.id)/$($f.path): expected $($f.sha256), got $actual" }
                Write-Host "  ok       $($f.path)"
            }
        }
        finally { $zip.Dispose() }
    }
    else {
        foreach ($f in $c.files) {
            $dest = Join-Path $destRoot $f.path
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            Get-Verified $f.url $f.sha256 $dest
            Write-Host "  ok       $($f.path)"
        }
    }
    $manifest.components += [ordered]@{ id = $c.id; version = $c.version; files = @($c.files | ForEach-Object { $_.path }) }
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
# Entry names must use forward slashes so the tool finds "<id>/<path>" on every runtime.
$out = [IO.Compression.ZipFile]::Open($Output, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1) -replace '\\', '/'
        $level = if ($_.Extension -in @('.zip', '.png', '.dll', '.exe')) { [IO.Compression.CompressionLevel]::Optimal } else { [IO.Compression.CompressionLevel]::Optimal }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($out, $_.FullName, $rel, $level) | Out-Null
    }
}
finally { $out.Dispose() }
Remove-Item -Recurse -Force $stage
$size = (Get-Item -LiteralPath $Output).Length
Write-Host ("payload.zip written: {0:N1} MB, full={1}" -f ($size / 1MB), [bool]$Full)
