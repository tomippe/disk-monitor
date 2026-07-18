# generate-assets.ps1
# Generates store asset PNGs from DiskMonitor\Assets\app-icon.png

Add-Type -AssemblyName System.Drawing

$rootDir   = $PSScriptRoot
$sourceImg = Join-Path $rootDir "DiskMonitor\Assets\app-icon.png"
$assetsDir = Join-Path $rootDir "DiskMonitor\Assets"

if (-not (Test-Path $sourceImg)) {
    Write-Error "app-icon.png not found at $sourceImg"
    exit 1
}

if (-not (Test-Path $assetsDir)) {
    New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
}

$original = [System.Drawing.Image]::FromFile($sourceImg)
Write-Host "Source: $sourceImg ($($original.Width)x$($original.Height))"

function Resize-Image {
    param(
        [System.Drawing.Image]$source,
        [int]$width,
        [int]$height
    )
    $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $width, $height)
    $g.Dispose()
    return $bmp
}

function Save-Png {
    param(
        [System.Drawing.Image]$source,
        [string]$path,
        [int]$width,
        [int]$height
    )
    $bmp = Resize-Image -source $source -width $width -height $height
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  Created: $path ($width x $height)"
}

Write-Host "Generating Store assets..."

Save-Png -source $original -path (Join-Path $assetsDir "StoreLogo.png")         -width 50  -height 50
Save-Png -source $original -path (Join-Path $assetsDir "Square44x44Logo.png")   -width 44  -height 44
Save-Png -source $original -path (Join-Path $assetsDir "Square71x71Logo.png")   -width 71  -height 71
Save-Png -source $original -path (Join-Path $assetsDir "Square150x150Logo.png") -width 150 -height 150
Save-Png -source $original -path (Join-Path $assetsDir "Square310x310Logo.png") -width 310 -height 310
Save-Png -source $original -path (Join-Path $assetsDir "SplashScreen.png")      -width 620 -height 300

$wideWidth = 310; $wideHeight = 150
$wideBmp = New-Object System.Drawing.Bitmap($wideWidth, $wideHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$wideG = [System.Drawing.Graphics]::FromImage($wideBmp)
$wideG.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$wideG.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$wideG.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$wideG.Clear([System.Drawing.Color]::Transparent)
$iconSize = [Math]::Min($wideWidth, $wideHeight) - 20
$offsetX = ($wideWidth - $iconSize) / 2
$offsetY = ($wideHeight - $iconSize) / 2
$wideG.DrawImage($original, $offsetX, $offsetY, $iconSize, $iconSize)
$wideG.Dispose()
$wideBmp.Save((Join-Path $assetsDir "Wide310x150Logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$wideBmp.Dispose()
Write-Host "  Created: $(Join-Path $assetsDir "Wide310x150Logo.png") (310 x 150)"

Save-Png -source $original -path (Join-Path $assetsDir "Square44x44Logo.scale-200.png")   -width 88  -height 88
Save-Png -source $original -path (Join-Path $assetsDir "Square44x44Logo.targetsize-24_altform-unplated.png") -width 24 -height 24
Save-Png -source $original -path (Join-Path $assetsDir "Square150x150Logo.scale-200.png") -width 300 -height 300
Save-Png -source $original -path (Join-Path $assetsDir "StoreLogo.scale-200.png")         -width 100 -height 100

$wideWidth2 = 620; $wideHeight2 = 300
$wideBmp2 = New-Object System.Drawing.Bitmap($wideWidth2, $wideHeight2, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$wideG2 = [System.Drawing.Graphics]::FromImage($wideBmp2)
$wideG2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$wideG2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$wideG2.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$wideG2.Clear([System.Drawing.Color]::Transparent)
$iconSize2 = [Math]::Min($wideWidth2, $wideHeight2) - 40
$offsetX2 = ($wideWidth2 - $iconSize2) / 2
$offsetY2 = ($wideHeight2 - $iconSize2) / 2
$wideG2.DrawImage($original, $offsetX2, $offsetY2, $iconSize2, $iconSize2)
$wideG2.Dispose()
$wideBmp2.Save((Join-Path $assetsDir "Wide310x150Logo.scale-200.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$wideBmp2.Dispose()
Write-Host "  Created: $(Join-Path $assetsDir "Wide310x150Logo.scale-200.png") (620 x 300)"

$original.Dispose()

Write-Host ""
Write-Host "Store assets generated." -ForegroundColor Green
