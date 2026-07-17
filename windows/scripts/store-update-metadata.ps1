#Requires -Version 5.1
<#
.SYNOPSIS
    Disk Monitor — Store draft listing metadata only (no package upload).

.EXAMPLE
    .\scripts\store-update-metadata.ps1
#>
param(
    [string]$AppId = "",
    [string]$ListingsJson = (Join-Path $PSScriptRoot "store-listings.json")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$global:SBDisableTelemetry = $true
$global:SBSuppressTelemetryReminder = $true

. (Join-Path $PSScriptRoot "_msstore-env.ps1")

$windowsRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectRoot = (Resolve-Path (Join-Path $windowsRoot "..")).Path

function Import-MsStoreEnvFileTwoPass {
    param([string]$File)
    if (-not (Test-Path -LiteralPath $File)) { return }
    $pairs = @{}
    foreach ($raw in [System.IO.File]::ReadAllLines($File)) {
        $line = $raw.Trim()
        if ($line -match '^\s*#' -or [string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^\s*export\s+(.+)$') { $line = $Matches[1].Trim() }
        if ($line -notmatch '=') { continue }
        $name, $value = $line -split '=', 2
        $pairs[$name.Trim()] = $value.Trim().Trim('"').Trim("'")
    }
    foreach ($k in @($pairs.Keys)) {
        if ($pairs[$k] -match '^\$\{([A-Z_][A-Z0-9_]*)\}$') {
            $ref = $Matches[1]
            if ($pairs.ContainsKey($ref)) { $pairs[$k] = $pairs[$ref] }
        }
    }
    foreach ($k in $pairs.Keys) {
        if ($k -and $pairs[$k]) { Set-Item -Path "Env:$k" -Value $pairs[$k] }
    }
}

Import-MsStoreEnvFile (Join-Path $env:USERPROFILE '.msstore-env')
Import-MsStoreEnvFileTwoPass 'Y:\.msstore-env'
Import-MsStoreEnvFile (Join-Path $projectRoot '.env')
Sync-MsStoreAuthAliases

if (-not $AppId) {
    $AppId = $env:MS_STORE_PRODUCT_ID
    if (-not $AppId) { $AppId = $env:MS_STORE_APP_ID }
}
if (-not $AppId) { $AppId = '9P47CBVHQ797' }

$TenantId = $env:SB_TENANT_ID
$ClientId = $env:SB_CLIENT_ID
$ClientSecret = $env:SB_CLIENT_SECRET
if (-not $TenantId -or -not $ClientId -or -not $ClientSecret) {
    Write-Error "Set MSSTORE_* / SB_* in Y:\.msstore-env (or %USERPROFILE%\.msstore-env)"
    exit 1
}
if (-not (Test-Path -LiteralPath $ListingsJson)) {
    Write-Error "Listings JSON not found: $ListingsJson"
    exit 1
}

$localSb = Join-Path $env:USERPROFILE "StoreBroker\StoreBroker"
if (Test-Path (Join-Path $localSb "StoreBroker.psd1")) {
    Import-Module $localSb -Force
} else {
    $sb = Get-Module -ListAvailable -Name StoreBroker
    if (-not $sb) {
        Install-Module -Name StoreBroker -Scope CurrentUser -Force -AllowClobber
    }
    Import-Module -Name StoreBroker -Force
}

$cred = New-Object PSCredential $ClientId, (ConvertTo-SecureString $ClientSecret -AsPlainText -Force)
Set-StoreBrokerAuthentication -TenantId $TenantId -Credential $cred | Out-Null

$meta = Get-Content -LiteralPath $ListingsJson -Raw -Encoding UTF8 | ConvertFrom-Json

function New-BaseListingFromMeta {
    param($LangMeta)
    return [PSCustomObject]@{
        copyrightAndTrademarkInfo = "Copyright (c) 2026 tomippe. All rights reserved."
        keywords                  = @($LangMeta.keywords)
        licenseTerms              = ""
        privacyPolicy             = ""
        supportContact            = "https://apps.tomippe.jp/disk-monitor/"
        websiteUrl                = "https://apps.tomippe.jp/disk-monitor/"
        description               = [string]$LangMeta.description
        features                  = @($LangMeta.features)
        releaseNotes              = [string]$LangMeta.releaseNotes
        images                    = @()
        recommendedHardware       = @()
        minimumHardware           = @()
        title                     = [string]$LangMeta.title
        shortDescription          = [string]$LangMeta.shortDescription
        shortTitle                = ""
        voiceTitle                = ""
    }
}

$listingMap = [ordered]@{}
foreach ($lang in @("en-us", "ja-jp", "zh-hans")) {
    $langMeta = $meta.listings.$lang
    if (-not $langMeta) { Write-Error "Missing listings.$lang in $ListingsJson"; exit 1 }
    $listingMap[$lang] = [PSCustomObject]@{
        baseListing       = (New-BaseListingFromMeta $langMeta)
        platformOverrides = [PSCustomObject]@{}
    }
}

Write-Host "Getting or creating submission for $AppId ..." -ForegroundColor Cyan
$app = Get-Application -AppId $AppId -NoStatus
$sub = $null
if ($app.pendingApplicationSubmission.id) {
    $sub = Get-ApplicationSubmission -AppId $AppId -SubmissionId $app.pendingApplicationSubmission.id -NoStatus
    Write-Host "Using pending submission $($sub.id)" -ForegroundColor Gray
} else {
    $sub = New-ApplicationSubmission -AppId $AppId
    Write-Host "Created submission $($sub.id)" -ForegroundColor Gray
}

# Pricing / availability / properties-like fields (MSIX submission API)
$sub.applicationCategory = [string]$meta.applicationCategory
$sub.visibility = [string]$meta.visibility
$sub.targetPublishMode = [string]$meta.targetPublishMode
$sub.automaticBackupEnabled = [bool]$meta.automaticBackupEnabled
$sub.canInstallOnRemovableMedia = [bool]$meta.canInstallOnRemovableMedia
$sub.meetAccessibilityGuidelines = [bool]$meta.meetAccessibilityGuidelines
$sub.enterpriseLicensing = [string]$meta.enterpriseLicensing
if ($null -ne $meta.hardwarePreferences) {
    $sub.hardwarePreferences = @($meta.hardwarePreferences)
}

$sub.pricing = [PSCustomObject]@{
    trialPeriod              = [string]$meta.pricing.trialPeriod
    marketSpecificPricings   = [PSCustomObject]@{}
    sales                    = @()
    priceId                  = [string]$meta.pricing.priceId
    isAdvancedPricingModel   = [bool]$meta.pricing.isAdvancedPricingModel
}

# Partner Center requires Desktop/Mobile/Xbox/Holographic (Team not allowed for this account)
$sub.allowTargetFutureDeviceFamilies = [PSCustomObject]@{
    Desktop     = $true
    Mobile      = $false
    Xbox        = $false
    Holographic = $false
}

$sub.listings = [PSCustomObject]$listingMap
$sub.notesForCertification = [string]$meta.notesForCertification

Write-Host "Updating submission metadata (no package upload)..." -ForegroundColor Cyan
Set-ApplicationSubmission -AppId $AppId -UpdatedSubmission $sub | Out-Null

$verify = Get-ApplicationSubmission -AppId $AppId -SubmissionId $sub.id -NoStatus
$langs = @($verify.listings.PSObject.Properties.Name)
Write-Host "Done. SubmissionId=$($verify.id)" -ForegroundColor Green
Write-Host "  category=$($verify.applicationCategory)" -ForegroundColor Green
Write-Host "  visibility=$($verify.visibility) priceId=$($verify.pricing.priceId) publish=$($verify.targetPublishMode)" -ForegroundColor Green
Write-Host "  backup=$($verify.automaticBackupEnabled) removable=$($verify.canInstallOnRemovableMedia) enterprise=$($verify.enterpriseLicensing)" -ForegroundColor Green
Write-Host "  listings=$($langs -join ', ')" -ForegroundColor Green
foreach ($lang in @("en-us", "ja-jp", "zh-hans")) {
    $bl = $verify.listings.$lang.baseListing
    $shortLen = 0; if ($bl.shortDescription) { $shortLen = $bl.shortDescription.Length }
    $descLen = 0; if ($bl.description) { $descLen = $bl.description.Length }
    $featCount = 0; if ($bl.features) { $featCount = @($bl.features).Count }
    $relLen = 0; if ($bl.releaseNotes) { $relLen = $bl.releaseNotes.Length }
    Write-Host ("  [{0}] title={1} short={2} desc={3} features={4} releaseNotes={5}" -f `
        $lang, $bl.title, $shortLen, $descLen, $featCount, $relLen)
}
$notesLen = 0
if ($verify.notesForCertification) { $notesLen = $verify.notesForCertification.Length }
Write-Host ("  notesForCertification length={0}" -f $notesLen) -ForegroundColor Green
Write-Host ""
Write-Host "Still MANUAL in Partner Center (API cannot set):" -ForegroundColor Yellow
Write-Host "  - Age ratings (IARC questionnaire)" -ForegroundColor Yellow
Write-Host "  - Properties: Privacy policy URL / Support contact / Website" -ForegroundColor Yellow
Write-Host "  - Packages upload + Screenshots" -ForegroundColor Yellow
Write-Host "  - Submit for certification (commit)" -ForegroundColor Yellow
