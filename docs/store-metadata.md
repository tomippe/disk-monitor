# Microsoft Store 申請用メタデータ — Disk Monitor

Partner Center / Store submission API 用。コピペ用のため引用符・表罫線は使わず、見出しの直下に本文のみ置く。

---

## 基本情報

プライバシーポリシー URL
https://apps.tomippe.jp/disk-monitor/policy/

カテゴリ
ユーティリティとツール > ファイル マネージャー / Utilities & tools > File managers

著作権
Copyright © 2026 tomippe. All rights reserved.

データ収集（個人データ）
いいえ（収集しない）

---

## Product identity（manifest と一致させる）

正本: `windows/DiskMonitor/Package.appxmanifest`

Package/Identity/Name
StudioTomippe.DiskMonitor

Package/Identity/Publisher
CN=7AA9757D-D72F-4DE2-9980-F9A659207C27

Package/Properties/DisplayName
Disk Monitor

Package/Properties/PublisherDisplayName
Studio Tomippe

Store URL
https://apps.microsoft.com/detail/9P47CBVHQ797

Store ID
9P47CBVHQ797

Identity Version の第 4 桁は常に 0（例: version.txt が 1.0.4 なら manifest は 1.0.4.0）

提出パッケージ
windows/publish/msix/DiskMonitor.msixbundle

---

## Restricted capabilities（Partner Center 申告）

| 種別 | 名前 | 申告 |
| --- | --- | --- |
| Restricted | runFullTrust | 要 |
| Restricted | unvirtualizedResources | 要 |
| 通常 | internetClient | 申告不要 |

### runFullTrust — 理由（英語）

The app is a full-trust desktop utility (EntryPoint: Windows.FullTrustApplication). It registers a bottom AppBar above the taskbar, shows per-volume free space, opens Explorer folders, manages the Recycle Bin, and ejects removable volumes. These shell and AppBar APIs are not available to sandboxed Store apps.

### unvirtualizedResources — 理由（英語・500文字以内）

Disk Monitor stores favorites, top-bar pin state, and local preferences under the real user profile. Virtualized paths would lose settings across updates. Scope: LOCALAPPDATA\DiskMonitor and user-selected folder favorites only. No other apps’ data is modified. Policy: https://apps.tomippe.jp/disk-monitor/policy/

---

## Partner Center リスト登録（3言語）

### アプリ名（Product name）

日本語
Disk Monitor

英語
Disk Monitor

簡体中国語
Disk Monitor

### 短い説明（Short description）

日本語
タスクバー直上に空き容量を常時表示。ボリューム・お気に入り・ごみ箱をひと目で確認できる Windows 用ユーティリティです。

英語
Always-on free space above the taskbar. See volumes, favorites, and the Recycle Bin at a glance on Windows.

簡体中国语
在任务栏上方常驻显示可用空间。一目了然查看卷、收藏夹和回收站的 Windows 工具。

### 説明文（Full description）

日本語
Disk Monitor は、Windows のタスクバー直上に横帯（AppBar）を置き、ドライブごとの空き容量を常時表示するユーティリティです。

チップを開くとボリューム一覧、お気に入りフォルダ、ごみ箱の容量確認や空にする操作、取り出し、ディスクの管理へのショートカットが使えます。トップバーに表示するドライブやお気に入りは自分で選べます。

個人を特定するデータは収集せず、設定と表示は端末内だけで完結します。UI は日本語・英語・簡体中国語に対応し、Windows の表示言語に追従します。

英語
Disk Monitor is a Windows utility that pins a slim AppBar just above the taskbar and always shows free space for each drive.

Open a chip to browse volumes, favorite folders, check or empty the Recycle Bin, eject removable media, and jump to Disk Management. Choose which drives and favorites appear on the top bar.

The app does not collect personally identifiable data; settings and display stay on your device. The UI supports Japanese, English, and Simplified Chinese and follows the Windows display language.

簡体中国语
Disk Monitor 是一款 Windows 工具，在任务栏上方固定一条横条（AppBar），常驻显示各驱动器的可用空间。

点击芯片可浏览卷、收藏文件夹，查看或清空回收站，弹出可移动介质，并打开磁盘管理。可自行选择在顶栏显示的驱动器和收藏。

不收集可识别个人的数据，设置与显示均在本机完成。界面支持日语、英语和简体中文，并跟随 Windows 显示语言。

### What's new in this version

日本語
初期リリースです。タスクバー直上の AppBar に空き容量を表示し、ボリューム・お気に入り・ごみ箱を操作できます。

英語
Initial release. Shows free space on an AppBar above the taskbar, with volume, favorites, and Recycle Bin actions.

簡体中国语
首次发布。在任务栏上方的 AppBar 显示可用空间，并支持卷、收藏夹和回收站操作。

### Product features

日本語
タスクバー直上に空き容量を常時表示
ボリュームごとの容量確認と取り出し
お気に入りフォルダをトップバー／メニューから開ける
ごみ箱の容量表示と空にする操作
ディスクの管理へのショートカット
日本語・English・简体中文、個人データ収集なし

英語
Always-on free space above the taskbar
Per-volume capacity and eject
Open favorite folders from the top bar or menu
Recycle Bin size and empty action
Shortcut to Disk Management
Japanese, English, Simplified Chinese; no personal data collection

簡体中国语
在任务栏上方常驻显示可用空间
按卷查看容量并弹出介质
从顶栏或菜单打开收藏文件夹
回收站容量显示与清空
打开磁盘管理的快捷方式
日语、英语、简体中文；不收集个人数据

---

## Notes for Certification（審査員向け・英語）

How to verify the app:

1. Launch Disk Monitor. A slim horizontal bar appears just above the Windows taskbar (AppBar). Confirm drive free-space chips are visible.
2. Click a volume chip. A menu opens with capacity info, open-in-Explorer, pin/unpin on the top bar, and eject (for removable volumes when available).
3. From the bar or overflow menu, open Favorites (add a folder if needed), Recycle Bin (size / empty), and Open Disk Management.
4. Right-click the notification-area icon (if shown) or use the app menu to quit. Expected: the AppBar disappears; no data is sent to developer servers.

Technical notes:

- Full-trust WPF desktop app (Windows.FullTrustApplication) with bottom AppBar registration.
- Restricted capabilities: runFullTrust (AppBar / shell), unvirtualizedResources (local favorites and preferences under the real user profile).
- internetClient may be used for optional update checks only; no analytics SDK; no personal data collection.
- Languages: en, ja, zh-Hans.

---

## 申請前チェックリスト

- [ ] version.txt / Package.appxmanifest Identity Version / MSIX 内バージョンが一致（第 4 桁 0）
- [ ] プライバシーポリシー URL を Partner Center のプロパティに設定
- [ ] Restricted capabilities（runFullTrust / unvirtualizedResources）を申告
- [ ] スクリーンショットを Partner Center に手動アップロード（API パッケージ提出はタイムアウトのため）
- [ ] MSIX パッケージを Partner Center に手動アップロード
- [ ] docs/store-metadata.md の文言が Partner Center 入力と一致
