#Requires -Version 5.1
# Studio Tomippe 共通 — ~/.msstore-env + プロジェクト .env を読み込む
# Mac 側は export KEY=value 形式、Windows 側は KEY=value も可

function Import-MsStoreEnvFile {
    param([string]$File)

    if (-not (Test-Path -LiteralPath $File)) { return }

    [System.IO.File]::ReadAllLines($File) | ForEach-Object {
        $line = $_.Trim()
        if ($line -match '^\s*#' -or [string]::IsNullOrWhiteSpace($line)) { return }
        if ($line -match '^\s*export\s+(.+)$') { $line = $Matches[1].Trim() }
        if ($line -notmatch '=') { return }

        $name, $value = $line -split '=', 2
        $name = $name.Trim()
        $value = $value.Trim().Trim('"').Trim("'")

        if ($value -match '^\$\{([A-Z_][A-Z0-9_]*)\}$') {
            $ref = $Matches[1]
            $resolved = (Get-Item -Path "Env:$ref" -ErrorAction SilentlyContinue).Value
            if (-not $resolved) { return }
            $value = $resolved
        }

        if ($name -and $value) {
            Set-Item -Path "Env:$name" -Value $value
        }
    }
}

function Sync-MsStoreAuthAliases {
    if (-not $env:SB_TENANT_ID -and $env:MSSTORE_TENANT_ID) {
        $env:SB_TENANT_ID = $env:MSSTORE_TENANT_ID
    }
    if (-not $env:SB_CLIENT_ID -and $env:MSSTORE_CLIENT_ID) {
        $env:SB_CLIENT_ID = $env:MSSTORE_CLIENT_ID
    }
    if (-not $env:SB_CLIENT_SECRET -and $env:MSSTORE_CLIENT_SECRET) {
        $env:SB_CLIENT_SECRET = $env:MSSTORE_CLIENT_SECRET
    }
    if (-not $env:MSSTORE_TENANT_ID -and $env:SB_TENANT_ID) {
        $env:MSSTORE_TENANT_ID = $env:SB_TENANT_ID
    }
    if (-not $env:MSSTORE_CLIENT_ID -and $env:SB_CLIENT_ID) {
        $env:MSSTORE_CLIENT_ID = $env:SB_CLIENT_ID
    }
    if (-not $env:MSSTORE_CLIENT_SECRET -and $env:SB_CLIENT_SECRET) {
        $env:MSSTORE_CLIENT_SECRET = $env:SB_CLIENT_SECRET
    }
}

function Load-MsStoreEnv {
    param([string]$ProjectRoot = "")

    Import-MsStoreEnvFile (Join-Path $env:USERPROFILE '.msstore-env')
    # Y: 共有ドライブ上の正本（Windows PC で USERPROFILE 側が署名のみのとき）
    Import-MsStoreEnvFile 'Y:\.msstore-env'
    if ($ProjectRoot) {
        Import-MsStoreEnvFile (Join-Path $ProjectRoot '.env')
    }
    Sync-MsStoreAuthAliases

    if (-not $env:SB_TENANT_ID -and -not $env:MSSTORE_TENANT_ID) {
        try {
            $info = msstore info 2>$null | Out-String
            if ($info -match 'Tenant Id\s*\|\s*([0-9a-f-]{36})') {
                $env:SB_TENANT_ID = $Matches[1]
                $env:MSSTORE_TENANT_ID = $Matches[1]
            }
        } catch {
            # msstore CLI optional
        }
    }
}

function Resolve-MsStoreSigningPfx {
    param(
        [string]$WindowsRoot,
        [string]$ProjectRoot
    )

    $candidates = @()
    if ($env:MS_STORE_SIGNING_PFX) {
        $candidates += $env:MS_STORE_SIGNING_PFX
    }
    if ($ProjectRoot) {
        $candidates += (Join-Path $ProjectRoot '..\pouches\native\windows\signing\StudioTomippe-MSIX.pfx')
    }
    if ($WindowsRoot) {
        $candidates += (Join-Path $WindowsRoot 'signing\StudioTomippe-MSIX.pfx')
    }

    foreach ($path in $candidates) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    return $null
}

function Find-SdkSignTool {
    $candidates = @()
    foreach ($root in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $candidates += Get-ChildItem -LiteralPath $root -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like '*\x64*' }
    }

    if ($candidates.Count -eq 0) {
        throw 'signtool.exe not found. Install Windows 10/11 SDK.'
    }

    return ($candidates | Sort-Object {
            $ver = $null
            [void][version]::TryParse($_.Directory.Parent.Name, [ref]$ver)
            if ($ver) { $ver } else { [version]'0.0.0.0' }
        } -Descending | Select-Object -First 1).FullName
}

function Sign-StorePackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [string]$PfxPath = "",
        [string]$PfxPassword = ""
    )

    if (-not (Test-Path -LiteralPath $PackagePath)) {
        throw "Package not found: $PackagePath"
    }

    if (-not $PfxPath) { $PfxPath = $env:MS_STORE_SIGNING_PFX }
    if (-not $PfxPassword) { $PfxPassword = $env:MS_STORE_SIGNING_PFX_PASSWORD }

    if (-not $PfxPath -or -not (Test-Path -LiteralPath $PfxPath)) {
        throw "PFX not found. Set MS_STORE_SIGNING_PFX in %USERPROFILE%\.msstore-env"
    }
    if (-not $PfxPassword) {
        throw 'PFX password required. Set MS_STORE_SIGNING_PFX_PASSWORD in %USERPROFILE%\.msstore-env'
    }

    $signTool = Find-SdkSignTool
    Write-Host "  Signing: $PackagePath" -ForegroundColor Gray
    Write-Host "  SignTool: $signTool" -ForegroundColor Gray

    & $signTool sign /fd SHA256 /f $PfxPath /p $PfxPassword /a $PackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $PackagePath (exit $LASTEXITCODE)"
    }
}
