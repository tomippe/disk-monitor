#Requires -Version 5.1
<#
.SYNOPSIS
    Disk Monitor Windows Build Script
    Reads version from version.txt, builds self-contained EXE (x64 + arm64).

.EXAMPLE
    .\build.ps1              # Build EXE
    .\build.ps1 -Clean       # Clean first, then full build
#>
param(
    [switch]$Clean,
    [switch]$Noverup
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootDir    = $PSScriptRoot
$projectDir = Join-Path $rootDir "DiskMonitor"
$publishDir = Join-Path $rootDir "publish"
$csproj     = Join-Path $projectDir "DiskMonitor.csproj"

$rids = @("win-x64", "win-arm64")

. "$rootDir\..\..\build-common\helpers.ps1"
. "$rootDir\..\..\build-common\version.ps1"

Write-Host "Disk Monitor Windows Build Script" -ForegroundColor White

$dotnetVersion = & dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Error ".NET SDK is not installed. Please install .NET 8 SDK."
    exit 1
}
Write-Host ".NET SDK: $dotnetVersion" -ForegroundColor Gray

$procs = Get-Process -Name "DiskMonitor" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Stopping running DiskMonitor..." -ForegroundColor Yellow
    $procs | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Write-Step "Version"
$versionFile = Join-Path $rootDir "version.txt"
$version = Read-AppVersion -VersionFile $versionFile
Write-Ok "v$version (from version.txt)"

# Sync Version into csproj
$csprojText = Get-Content $csproj -Raw
$csprojText = $csprojText -replace '<Version>.*?</Version>', "<Version>$version</Version>"
Set-Content -Path $csproj -Value $csprojText -NoNewline

if ($Clean) {
    Write-Step "Clean"
    Remove-Item -Recurse -Force (Join-Path $projectDir "bin"), (Join-Path $projectDir "obj"), $publishDir -ErrorAction SilentlyContinue
    Write-Ok "Cleaned"
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

foreach ($rid in $rids) {
    Write-Step "Publish $rid"
    $out = Join-Path $publishDir $rid
    & dotnet publish $csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    Write-Ok "$out\DiskMonitor.exe"
}

if (-not $Noverup) {
    Write-Step "Bump version"
    $parts = $version.Split('.')
    $parts[-1] = [string]([int]$parts[-1] + 1)
    $next = $parts -join '.'
    Set-Content -Path $versionFile -Value $next -NoNewline
    Write-Ok "Next version: $next"
}

Write-Host ""
Write-Host "Done. Run on Windows:" -ForegroundColor Green
Write-Host "  $($publishDir)\win-x64\DiskMonitor.exe"
