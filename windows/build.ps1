#Requires -Version 5.1
<#
.SYNOPSIS
    Disk Monitor Windows Build Script — EXE (portable) + MSIX (Microsoft Store)

.EXAMPLE
    .\build.ps1              # EXE + MSIX (signed) — full build
    .\build.ps1 -Exe         # EXE only
    .\build.ps1 -Clean       # Clean build output first
    .\build.ps1 -Noverup     # Skip version.txt bump
#>
param(
    [switch]$Exe,
    [switch]$Clean,
    [switch]$Noverup
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:TOMIPPE_BUILD_COMMON_ROOT -and (Test-Path -LiteralPath (Join-Path $env:TOMIPPE_BUILD_COMMON_ROOT 'windows-build-bootstrap.ps1'))) {
    . (Join-Path $env:TOMIPPE_BUILD_COMMON_ROOT 'windows-build-bootstrap.ps1')
} else {
    . (Join-Path $PSScriptRoot '..\..\build-common\windows-build-bootstrap.ps1')
}
$buildCommonRoot = Resolve-BuildCommonRoot -WindowsScriptRoot $PSScriptRoot
. (Join-Path $buildCommonRoot 'windows-unc-build.ps1')
if ($env:TOMIPPE_WIN_BUILD_STAGED -ne '1') {
    $projRootForStage = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    if (Invoke-WindowsBuildViaLocalStageIfNeeded `
            -ProjectRoot $projRootForStage `
            -StageName 'disk-monitor-win-build' `
            -ArtifactRelativePaths @('windows\build', 'windows\version.txt') `
            -BuildScriptRelative 'windows\build.ps1' `
            -BoundParameters $PSBoundParameters) {
        exit 0
    }
}

$rootDir = $PSScriptRoot
$projectRoot = Resolve-TomippeProjectRoot -WindowsScriptRoot $rootDir

. (Join-Path $rootDir "scripts\_msstore-env.ps1")
. (Join-Path $buildCommonRoot "helpers.ps1")
. (Join-Path $buildCommonRoot "version.ps1")
. (Join-Path $buildCommonRoot "ftp-upload.ps1")

$DIST_DIR = Join-Path $projectRoot "..\apps.tomippe.jp\disk-monitor"
$projectDir = Join-Path $rootDir "DiskMonitor"
$buildDir = Join-Path $rootDir "build"
$csproj = Join-Path $projectDir "DiskMonitor.csproj"
$assetsDir = Join-Path $projectDir "Assets"
$versionFile = Join-Path $rootDir "version.txt"
$rids = @("win-x64", "win-arm64")

function Find-MakeAppx {
    $sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) { return $null }

    $found = Get-ChildItem $sdkRoot -Recurse -Filter "makeappx.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*\x64" } |
        Sort-Object { $_.DirectoryName } -Descending |
        Select-Object -First 1

    if ($found) { return $found.FullName }
    return $null
}

Write-Host "Disk Monitor Windows Build Script" -ForegroundColor White

$dotnetVersion = & dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Error ".NET 8 SDK is not installed."
    exit 1
}
Write-Host ".NET SDK: $dotnetVersion" -ForegroundColor Gray

$version = Read-AppVersion -VersionFile $versionFile
Write-Ok "v$version (from version.txt)"

$csprojContent = Get-Content $csproj -Raw
$csprojContent = $csprojContent -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
Set-Content -Path $csproj -Value $csprojContent -NoNewline

if ($Clean) {
    Write-Step "Clean"
    Remove-Item -Recurse -Force (Join-Path $projectDir "bin"), (Join-Path $projectDir "obj"), $buildDir -ErrorAction SilentlyContinue
    Write-Ok "Cleaned"
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

$procs = Get-Process -Name "DiskMonitor" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Stopping running DiskMonitor..." -ForegroundColor Yellow
    $procs | Stop-Process -Force
    Start-Sleep -Seconds 1
}

$storeLogo = Join-Path $assetsDir "StoreLogo.png"
if (-not (Test-Path $storeLogo)) {
    Write-Host "Generating Store assets..." -ForegroundColor Gray
    & (Join-Path $rootDir "generate-assets.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Error "generate-assets.ps1 failed"
        exit 1
    }
}

Write-Host "Building EXE (single-file)..." -ForegroundColor Gray

foreach ($rid in $rids) {
    $arch = $rid -replace "win-", ""
    $outDir = Join-Path $buildDir "exe\$arch"

    & dotnet publish $csproj `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $outDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "EXE build failed for $rid"
        exit 1
    }

    $exePath = Join-Path $outDir "DiskMonitor.exe"
    if (Test-Path $exePath) {
        $size = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
        Write-Host "OK: $exePath ($size MB)" -ForegroundColor Green
    }
}

if ($Exe) {
    if (-not $Noverup) {
        Write-Step "Bump version"
        Save-NextAppVersion -Version $version -VersionFile $versionFile
    }
    Write-Host ""
    Write-Host "Build complete (EXE only): v$version" -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Building MSIX (Store package)..." -ForegroundColor Gray

Load-MsStoreEnv -ProjectRoot $projectRoot

$signingPfx = Resolve-MsStoreSigningPfx -WindowsRoot $rootDir -ProjectRoot $projectRoot
if ($signingPfx) {
    $env:MS_STORE_SIGNING_PFX = $signingPfx
    Write-Host "  Signing PFX: $signingPfx" -ForegroundColor Gray
} else {
    Write-Error "MS_STORE_SIGNING_PFX not found. Configure %USERPROFILE%\.msstore-env (see build-common/msstore-env.example)."
    exit 1
}

Remove-Item -Recurse -Force (Join-Path $buildDir "msix"), (Join-Path $buildDir "msix-content") -ErrorAction SilentlyContinue

$makeappx = Find-MakeAppx
if (-not $makeappx) {
    Write-Error "makeappx.exe not found. Install Windows 10/11 SDK: https://developer.microsoft.com/windows/downloads/windows-sdk/"
    exit 1
}
Write-Host "  makeappx.exe: $makeappx" -ForegroundColor Gray

$msixOutputDir = Join-Path $buildDir "msix"
New-Item -ItemType Directory -Path $msixOutputDir -Force | Out-Null

$msixFiles = @()
$bundlePath = $null
$msixVersion = "$version.0"

foreach ($rid in $rids) {
    $arch = $rid -replace "win-", ""
    $contentDir = Join-Path $buildDir "msix-content\$arch"

    Write-Host "  Publishing $rid (multi-file for MSIX)..." -ForegroundColor Gray

    & dotnet publish $csproj `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $contentDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "MSIX publish failed for $rid"
        exit 1
    }

    $contentAssetsDir = Join-Path $contentDir "Assets"
    if (-not (Test-Path $contentAssetsDir)) {
        New-Item -ItemType Directory -Path $contentAssetsDir -Force | Out-Null
    }
    Copy-Item (Join-Path $assetsDir "*.png") $contentAssetsDir -Force

    $stringsSrc = Join-Path $projectDir "Strings"
    if (Test-Path $stringsSrc) {
        Copy-Item -Recurse $stringsSrc (Join-Path $contentDir "Strings") -Force
    }

    $manifestSrc = Join-Path $projectDir "Package.appxmanifest"
    $manifestDst = Join-Path $contentDir "AppxManifest.xml"
    $manifestContent = Get-Content $manifestSrc -Raw -Encoding UTF8

    $manifestContent = $manifestContent -replace '(<Identity[^>]*?)Version="[^"]*"', "`$1Version=`"$msixVersion`""

    if ($manifestContent -match '<Identity[^>]*ProcessorArchitecture=') {
        $manifestContent = $manifestContent -replace '(<Identity[^>]*?)ProcessorArchitecture="[^"]*"', "`$1ProcessorArchitecture=`"$arch`""
    } else {
        $manifestContent = $manifestContent -replace '(<Identity[^>]*?)(/>|\s*>)', "`$1`n            ProcessorArchitecture=`"$arch`"`$2"
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($manifestDst, $manifestContent, $utf8NoBom)

    $makepri = $makeappx -replace "makeappx\.exe", "makepri.exe"
    if (Test-Path $makepri) {
        Write-Host "  Creating resources.pri for $rid ..." -ForegroundColor Gray
        $priConfigFile = Join-Path $contentDir "priconfig.xml"
        & $makepri createconfig /cf $priConfigFile /dq en-US /o 2>$null
        if (Test-Path $priConfigFile) {
            $priOut = Join-Path $contentDir "resources.pri"
            $mnFile = Join-Path $contentDir "AppxManifest.xml"
            & $makepri new /pr $contentDir /cf $priConfigFile /mn $mnFile /of $priOut /o 2>$null
            Remove-Item $priConfigFile -Force -ErrorAction SilentlyContinue
        }
    }

    $msixPath = Join-Path $msixOutputDir "DiskMonitor_$arch.msix"
    Write-Host "  Packaging $msixPath ..." -ForegroundColor Gray

    & $makeappx pack /d $contentDir /p $msixPath /o

    if ($LASTEXITCODE -ne 0) {
        Write-Error "makeappx pack failed for $rid"
        exit 1
    }

    Sign-StorePackage -PackagePath $msixPath -PfxPath $signingPfx

    $msixSize = [math]::Round((Get-Item $msixPath).Length / 1MB, 1)
    Write-Host "OK: $msixPath ($msixSize MB, signed)" -ForegroundColor Green
    $msixFiles += $msixPath
}

if ($msixFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "Creating MSIX Bundle..." -ForegroundColor Gray

    $bundlePath = Join-Path $msixOutputDir "DiskMonitor.msixbundle"
    $bundleContentDir = Join-Path $buildDir "msix-bundle-content"

    if (Test-Path $bundleContentDir) { Remove-Item -Recurse -Force $bundleContentDir }
    New-Item -ItemType Directory -Path $bundleContentDir -Force | Out-Null

    foreach ($msix in $msixFiles) { Copy-Item $msix $bundleContentDir }

    # /bv 必須: 未指定だと bundle Identity が日時版になり Partner Center が分かりにくい
    & $makeappx bundle /d $bundleContentDir /p $bundlePath /bv $msixVersion /o

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Bundle creation failed. Individual MSIX files are still available."
    } else {
        Sign-StorePackage -PackagePath $bundlePath -PfxPath $signingPfx

        $bundleSize = [math]::Round((Get-Item $bundlePath).Length / 1MB, 1)
        Write-Host "OK: $bundlePath ($bundleSize MB, bundle v$msixVersion, signed)" -ForegroundColor Green

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $bundleZip = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
        try {
            $bundleManifest = $bundleZip.GetEntry("AppxMetadata/AppxBundleManifest.xml")
            if ($bundleManifest) {
                $reader = New-Object System.IO.StreamReader($bundleManifest.Open())
                $xml = $reader.ReadToEnd()
                $reader.Close()
                if ($xml -notmatch "Version=`"$([regex]::Escape($msixVersion))`"") {
                    Write-Warning "Bundle manifest version mismatch (expected $msixVersion). Check AppxBundleManifest.xml."
                }
            }
        } finally {
            $bundleZip.Dispose()
        }
    }

    Remove-Item -Recurse -Force $bundleContentDir -ErrorAction SilentlyContinue
}

Remove-Item -Recurse -Force (Join-Path $buildDir "msix-content") -ErrorAction SilentlyContinue

if (Test-Path (Split-Path $DIST_DIR -Parent)) {
    Write-Step "Direct download (Sparkle-like for Windows)"
    New-Item -ItemType Directory -Path $DIST_DIR -Force | Out-Null

    $exeX64Src = Join-Path $buildDir "exe\x64\DiskMonitor.exe"
    $exeArmSrc = Join-Path $buildDir "exe\arm64\DiskMonitor.exe"
    $exeX64Name = "DiskMonitor-win-x64.exe"
    $exeArmName = "DiskMonitor-win-arm64.exe"
    $exeX64Dst = Join-Path $DIST_DIR $exeX64Name
    $exeArmDst = Join-Path $DIST_DIR $exeArmName

    if (Test-Path $exeX64Src) {
        Copy-Item $exeX64Src $exeX64Dst -Force
        Write-Ok $exeX64Dst
    }
    if (Test-Path $exeArmSrc) {
        Copy-Item $exeArmSrc $exeArmDst -Force
        Write-Ok $exeArmDst
    }

    Write-Step "manifest.json"
    $manifestPath = Join-Path $DIST_DIR "manifest.json"
    $manifest = @{}
    if (Test-Path $manifestPath) {
        $existing = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $existing.PSObject.Properties | ForEach-Object { $manifest[$_.Name] = $_.Value }
    }
    $manifest['name'] = 'Disk Monitor'
    $manifest['win_version'] = $version
    # Keep mac_version / version (Mac Sparkle) if already present; don't overwrite Mac's version with win.
    if (-not $manifest.ContainsKey('mac_version') -and $manifest.ContainsKey('version')) {
        $manifest['mac_version'] = $manifest['version']
    }
    $manifest['win_url_x64'] = "https://apps.tomippe.jp/disk-monitor/$exeX64Name"
    $manifest['win_url_arm64'] = "https://apps.tomippe.jp/disk-monitor/$exeArmName"
    $manifest | ConvertTo-Json -Compress | Set-Content -Path $manifestPath -Encoding UTF8 -NoNewline
    Write-Ok "manifest.json updated (win_version: $version)"

    try {
        if (Test-Path $exeX64Dst) {
            Send-FtpFile -LocalFile $exeX64Dst -RemotePath "disk-monitor/$exeX64Name"
        }
        if (Test-Path $exeArmDst) {
            Send-FtpFile -LocalFile $exeArmDst -RemotePath "disk-monitor/$exeArmName"
        }
        Send-FtpFile -LocalFile $manifestPath -RemotePath "disk-monitor/manifest.json"
        Write-Ok "FTP upload complete"
    } catch {
        Write-Warning "FTP upload skipped/failed: $($_.Exception.Message)"
    }
}

Write-Step "Partner Center listing CSV"
$listingCsvScript = Join-Path $rootDir "scripts\generate-listing-csv.py"
$listingCsvOut = Join-Path $buildDir "msix\listingData-9P47CBVHQ797.csv"
if (-not (Test-Path -LiteralPath $listingCsvScript)) {
    Write-Error "Missing $listingCsvScript"
    exit 1
}
python $listingCsvScript -o $listingCsvOut
if ($LASTEXITCODE -ne 0) {
    Write-Error "generate-listing-csv.py failed"
    exit 1
}
if (-not (Test-Path -LiteralPath $listingCsvOut)) {
    Write-Error "listing CSV missing after generate: $listingCsvOut"
    exit 1
}
Write-Ok $listingCsvOut

Write-Step "Yoink (Partner Center submit artifacts)"
$sendYoink = Join-Path $buildCommonRoot 'send-mspc-artifacts-to-yoink.ps1'
$yoinkArtifacts = @()
if ($bundlePath -and (Test-Path -LiteralPath $bundlePath)) { $yoinkArtifacts += $bundlePath }
if (Test-Path -LiteralPath $listingCsvOut) { $yoinkArtifacts += $listingCsvOut }
if ($yoinkArtifacts.Count -gt 0) {
    & $sendYoink -ArtifactPath $yoinkArtifacts
    if ($LASTEXITCODE -ne 0) { Write-Warn "Yoink send failed (artifacts remain on disk)" }
}

if (-not $Noverup) {
    Write-Step "Bump version"
    Save-NextAppVersion -Version $version -VersionFile $versionFile
}

Write-Host ""
Write-Host "Build complete: v$version" -ForegroundColor Green
Write-Host "  EXE: build\exe\x64, build\exe\arm64" -ForegroundColor Gray
Write-Host "  MSIX: build\msix\DiskMonitor.msixbundle (signed)" -ForegroundColor Gray
Write-Host "  Listing CSV (Partner Center import): build\msix\listingData-9P47CBVHQ797.csv" -ForegroundColor Gray
