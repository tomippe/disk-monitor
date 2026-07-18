#Requires -Version 5.1
<#
.SYNOPSIS
    Partner Center の Export listings CSV に、store-listings.json の文言を流し込む。

.DESCRIPTION
    Dev Center submission API に書いた listings が Partner Center UI に出ないことがある。
    その場合は UI の Export → このスクリプト → Import で画面側に反映する。

.EXAMPLE
    .\scripts\fill-partner-center-listings-csv.ps1 -InputCsv .\scripts\exported-listings.csv
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$InputCsv,
    [string]$OutputCsv = "",
    [string]$ListingsJson = (Join-Path $PSScriptRoot "store-listings.json")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $OutputCsv) {
    $OutputCsv = Join-Path (Split-Path -Parent $InputCsv) "disk-monitor-listings-import.csv"
}

$meta = Get-Content -LiteralPath $ListingsJson -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = Import-Csv -LiteralPath $InputCsv

# Ensure language columns exist
$langs = @("en-us", "ja-jp", "zh-hans")
$sample = $rows[0]
$cols = @($sample.PSObject.Properties.Name)
foreach ($lang in $langs) {
    if ($cols -notcontains $lang) {
        foreach ($row in $rows) {
            $row | Add-Member -NotePropertyName $lang -NotePropertyValue "" -Force
        }
    }
}

function Set-FieldValue {
    param($Rows, [string]$FieldName, [string]$Lang, [string]$Value)
    $row = $Rows | Where-Object { $_.Field -eq $FieldName } | Select-Object -First 1
    if (-not $row) {
        Write-Warning "Field not in CSV (skipped): $FieldName"
        return
    }
    $row.$Lang = $Value
}

foreach ($lang in $langs) {
    $L = $meta.listings.$lang
    if (-not $L) { throw "Missing listings.$lang in $ListingsJson" }

    Set-FieldValue $rows "Title" $lang ([string]$L.title)
    Set-FieldValue $rows "Description" $lang ([string]$L.description)
    Set-FieldValue $rows "ShortDescription" $lang ([string]$L.shortDescription)
    Set-FieldValue $rows "ReleaseNotes" $lang ([string]$L.releaseNotes)

    $i = 1
    foreach ($f in @($L.features)) {
        Set-FieldValue $rows ("Feature{0}" -f $i) $lang ([string]$f)
        $i++
    }

    $i = 1
    foreach ($k in @($L.keywords)) {
        # Export templates use Keyword1.. or Keywords1.. depending on product type
        Set-FieldValue $rows ("Keyword{0}" -f $i) $lang ([string]$k)
        Set-FieldValue $rows ("Keywords{0}" -f $i) $lang ([string]$k)
        $i++
    }

    Set-FieldValue $rows "CopyrightAndTrademarkInfo" $lang "Copyright (c) 2026 tomippe. All rights reserved."
    Set-FieldValue $rows "Copyright" $lang "Copyright (c) 2026 tomippe. All rights reserved."
}

# Prefer UTF-8 with BOM for Excel / Partner Center
$utf8Bom = New-Object System.Text.UTF8Encoding $true
$sb = New-Object System.Text.StringBuilder
$header = @($rows[0].PSObject.Properties.Name)
[void]$sb.AppendLine(($header -join ","))
foreach ($row in $rows) {
    $vals = foreach ($h in $header) {
        $v = [string]$row.$h
        if ($null -eq $v) { $v = "" }
        $v = $v.Replace("`r`n", "`n").Replace("`r", "`n")
        '"' + $v.Replace('"', '""') + '"'
    }
    [void]$sb.AppendLine(($vals -join ","))
}
$outFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputCsv)
[System.IO.File]::WriteAllText($outFull, $sb.ToString(), $utf8Bom)
Write-Host "Wrote: $outFull" -ForegroundColor Green
Write-Host "Partner Center -> Store listings -> Import listings -> Import .csv" -ForegroundColor Yellow