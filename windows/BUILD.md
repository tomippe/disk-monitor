# Disk Monitor — Windows ビルド

作業ディレクトリは常に **`windows` フォルダ**（この README と同じ階層）を起点にする。

## 前提

- **.NET 8 SDK**（`dotnet --version` で確認）
- **フルビルド（MSIX）**: Windows 10/11 SDK（`makeappx.exe` / `signtool.exe`）
- **Store 署名**: `%USERPROFILE%\.msstore-env`（`build-common/msstore-env.example` 参照）
  - `MS_STORE_SIGNING_PFX` / `MS_STORE_SIGNING_PFX_PASSWORD`
  - PFX 未設定時は `..\pouches\native\windows\signing\StudioTomippe-MSIX.pfx` を自動参照

## バージョンの正

- **`version.txt`** の値（例: `1.0.2`）
- MSIX Identity.Version は **`$version.0`**（例: `1.0.2.0`）。第 4 桁は常に 0

## コマンド

| 目的 | コマンド |
|------|----------|
| **フルビルド**（EXE x64/arm64 + 署名済み msixbundle） | `.\build.ps1` |
| `publish` を消してからフルビルド | `.\build.ps1 -Clean` |
| EXE のみ（MSIX なし） | `.\build.ps1 -Exe` |
| 版上げスキップ | `.\build.ps1 -Noverup` |
| Store 用タイル PNG の再生成 | `.\generate-assets.ps1` |

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## 成果物

- **ポータブル EXE**: `publish\exe\x64\DiskMonitor.exe` / `publish\exe\arm64\DiskMonitor.exe`
- **直接配布**（フルビルド時）: `../apps.tomippe.jp/disk-monitor/DiskMonitor-win-{x64,arm64}.exe` と `manifest.json` の `win_version` / `win_url_*`（FTP 同期・任意）
- **MSIX バンドル**（Partner Center 用）: `publish\msix\DiskMonitor.msixbundle`（更新は Store 側。アプリ内アップデート確認は無し）
- 個別: `publish\msix\DiskMonitor_x64.msix` / `DiskMonitor_arm64.msix`

Store タイル PNG が無い場合、ビルド時に `generate-assets.ps1` が `DiskMonitor\Assets\app-icon.png` から生成する。

## 開発実行

```powershell
dotnet run --project DiskMonitor
```
