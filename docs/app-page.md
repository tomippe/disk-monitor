# Disk Monitor 紹介ページ設定

## 公開ステータス

**公開済み**（`status: publish`）。

## URL

- 紹介ページ: https://apps.tomippe.jp/disk-monitor/
- プライバシーポリシー: https://apps.tomippe.jp/disk-monitor/policy/

## WordPress 投稿 ID

| 用途 | ID |
|------|-----|
| 紹介ページ（app） | **2274** |
| プライバシーポリシー（app・子ページ） | **2408** |

## キャッチフレーズ（app-cp）

メニューバーに空き容量
ボリュームとゴミ箱をひと目で

## プラットフォーム

- **platform**: ["mac", "win"]
- **app-macdesc**: macOS 11+, DMG<br>日本語,English,中文
- **app-winurl**: https://apps.microsoft.com/detail/9P47CBVHQ797?hl=ja-JP&gl=JP
- **app-windesc**: Windows 10+, Store<br>日本語,English,中文

MAS 公開後は **app-iosurl**（Mac App Store の URL）と **app-macdesc**（`macOS 11+, App Store<br>日本語,English,中文` 形式）を更新すること。

## メディア（WordPress）

| フィールド | メディア ID | 備考 |
|------------|-------------|------|
| app-icon | **2273** | ビルド済み `AppIcon.icns` から 512px PNG を生成してアップロード（後で差し替え可） |
| app-kvbg | **2278** | `big-data-cybier-security-database-abstract-concept.jpg` をアップロード（旧 KV メディア 2276 は未使用） |
| app-ss01 | **2275** | `CleanShot 2026-05-12 at 01.43.50.png` をアップロード |
| app-ss01width | **1600** | 元幅 1912px。ACF 上限に合わせて 1600 |

## KV背景・キー色

- **app-keycolor**: #c4b81e（ゴールド系）
- **app-kvbgaddcss**: `no-repeat` / `center` / `cover` に加え、`background-color: rgba(196, 184, 30, 0.28)`（#c4b81e 相当）と **`background-blend-mode: luminosity`**

## 本文（content）

紹介ページ本文は WordPress（投稿 ID 2274）で設定済み。要点:

- Mac はメニューバー、Windows はタスクバー直上の帯に空き容量を表示
- ボリューム一覧・取り出し、ゴミ箱、ディスクユーティリティ（Mac）
- ローカル情報のみ・外部送信なし
- **多言語 UI**（日本語・English・简体中文、OS の表示言語に追従）

## バージョン履歴（app-versions）

**アプリ本体のリリース履歴のみ**（紹介ページ・KV・スクショの変更は書かない）。**初回だけ**必ず 1 行入れる（例: 初版リリース）。それ以降はユーザーから指示があったときだけ行を追加する。

- 2026.07.18 / v1.1.9 / Windows 版を Microsoft Store で公開しました。
- 2026.05.12 / v1.0.4 / 初版リリース

## ローカル連携

- プロジェクト直下に `.env` を置く（`.env.example` をコピー）。Git には含めない（`.gitignore`）。
- REST API 例: `source ~/.wp-env && source .env` のあと `.env` の投稿 ID を使って取得・更新。
