# Disk Monitor — Windows ビルド

## 概要

- **UI**: タスクバー直上の横帯 AppBar（`ABE_BOTTOM`）
- **表示**: システムドライブの空き容量
- **クリック**: ドライブ一覧メニュー（エクスプローラーで開く / 更新 / 終了）
- **トレイ**: 終了・更新用の NotifyIcon

## 前提

- Windows 10/11
- .NET 8 SDK

## ビルド

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

| オプション | 内容 |
|---|---|
| （なし） | self-contained EXE（x64 / arm64） |
| `-Clean` | `bin` / `obj` / `publish` を消してからビルド |
| `-Noverup` | `version.txt` のパッチ上げをスキップ |

成果物:

- `windows/publish/win-x64/DiskMonitor.exe`
- `windows/publish/win-arm64/DiskMonitor.exe`

## 開発実行

```powershell
cd windows
dotnet run --project DiskMonitor
```
