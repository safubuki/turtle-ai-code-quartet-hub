# Microsoft Store 公開 TODO / 現状把握

このドキュメントは、Turtle AI Code Quartet Hub を Microsoft Store に公開するために、
**いま何ができていて、何が残っているか**を一覧で把握するためのものです。

- **ソフトウェア面**（リポジトリ内のコード・パッケージ・ドキュメントで完結する作業）
- **リリース面**（Partner Center・公開URL・審査など、リポジトリ外で発生する作業）

の2軸で整理します。具体的な手続きの手順そのものは
[WindowsAppsStoreリリース手順.md](WindowsAppsStoreリリース手順.md) に詳細があります。
本ドキュメントは「進捗の管理表」として使ってください。

最終更新: 2026-06-20
対象バージョン: 1.0.0（初回公開候補）

---

## 0. 重要な前提（2026年時点の最新情報）

Microsoft の公開フローは 2025〜2026 年に大きく変わりました。手順書の一部に古い金額前提が
残っている場合は、こちらを正としてください。

| 項目 | 2026年6月時点の事実 | 出典 |
|---|---|---|
| 個人開発者の登録費用 | **無料**（旧 $19 は新フローで免除） | Microsoft Learn |
| 法人開発者の登録費用 | **無料**（2026年5月に旧 $99 を撤廃） | Windows Developer Blog |
| 登録の入口 | **必ず https://storedeveloper.microsoft.com から**開始する。Partner Center / Xbox / Visual Studio から直接入ると旧フローになる | Microsoft Learn |
| 本人確認 | 個人は**政府発行の身分証＋セルフィー**が必須（スマホで撮影） | Microsoft Learn |
| コード署名証明書 | **CA 署名証明書は不要**。Store が認定後に Microsoft 証明書で再署名する。ローカル確認用の自己署名で十分 | Microsoft Learn |
| 提出パッケージ形式 | `.msixupload` / `.appxupload`（推奨）。MSIX が推奨形式だが EXE/MSI も受理される | Microsoft Learn |

> このアプリは GPL-3.0 です。Store 配布でも問題ありませんが、ソースコード提供・ライセンス同梱・
> 著作権表示の義務を満たす必要があります（後述「リリース面 R8」）。

---

## 1. サマリ（ざっくり現在地）

| フェーズ | 状態 | ひとことメモ |
|---|---|---|
| ソフトウェア準備（コード・設定・データ保存先） | ✅ ほぼ完了 | manifest、保存先、ライセンス同梱まで対応済み |
| MSIX パッケージング基盤 | ✅ ほぼ完了 | Packaging Project・スクリプト・ローカルWACK PASS実績あり |
| 公開用ドキュメント（草案） | 🟡 草案あり | プライバシー/サポート/掲載文は「草案」のまま。正式化と内容の最新化が必要 |
| 公開用アセット（スクショ） | ❌ 未着手 | `assets/store/` に PNG が1枚もない |
| 公開URL（プライバシー/サポート） | ❌ 未着手 | HTTPS で公開された URL がまだない |
| Partner Center 側 | ❌ 未着手 | アカウント登録・アプリ名予約・Identity 取得が未 |
| 提出パッケージ（`.msixupload`） | ❌ 未着手 | Identity 確定後に Visual Studio で生成 |
| 申請・審査・公開 | ❌ 未着手 | 上記が揃ってから |

**残りの本質はだいたい3つだけ:**

1. **掲載情報の最新化と正式化**（草案→正式、かつ「4面VS Code」前提の古い文言をマルチアプリ実態に更新）
2. **公開アセット・公開URLの用意**（スクショ4枚＋プライバシー/サポートのHTTPS URL）
3. **Partner Center 登録〜Identity 反映〜提出**（アカウント本人確認が最初の関門）

---

## 2. ソフトウェア面 TODO

リポジトリ内で完結する作業です。多くは完了済みですが、**ドキュメントの内容が古い**問題が残っています。

### 2-A. 完了している項目 ✅

| # | 項目 | 確認場所 |
|---|---|---|
| S1 | アプリ表示名を `Turtle AI Code Quartet Hub` に統一 | `TurtleAIQuartetHub.Panel.csproj` |
| S2 | 管理者権限を要求しない（`asInvoker`） | `src/TurtleAIQuartetHub.Panel/app.manifest` |
| S3 | 実行時データを `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存 | アプリ実装・PRIVACY.md |
| S4 | ユーザー設定を `%LOCALAPPDATA%` 側から優先読み込み | README「設定」節 |
| S5 | `LICENSE.txt` をビルド/publish 出力へ同梱 | `TurtleAIQuartetHub.Panel.csproj` の `Content` |
| S6 | 設定例 `turtle-ai-quartet-hub.example.json` を同梱 | 同上 |
| S7 | アプリアイコン `app.ico` を設定 | `TurtleAIQuartetHub.Panel.csproj` |
| S8 | ソリューション `TurtleAIQuartetHub.sln` を整備 | ルート |
| S9 | MSIX Packaging Project を追加 | `src/TurtleAIQuartetHub.Package/TurtleAIQuartetHub.Package.wapproj` |
| S10 | 開発用 `Package.appxmanifest` を追加 | `src/TurtleAIQuartetHub.Package/Package.appxmanifest` |
| S11 | パッケージ用ロゴ6種を配置（44/71/150/310/Wide/Store） | `src/TurtleAIQuartetHub.Package/Assets/` |
| S12 | ローカルMSIX生成スクリプト | `scripts/New-LocalMsixPackage.ps1` |
| S13 | Store準備チェックスクリプト | `scripts/Test-StoreReadiness.ps1` |
| S14 | Windows ビルド＋準備チェックのCI | `.github/workflows/windows-build.yml` |
| S15 | 削除済み機能（AI状態検出・外周フレーム・点滅）がコードから除去済み | `src/` に痕跡なしを確認済み |

### 2-B. 残っている項目 ❌🟡

| # | 優先 | 項目 | 内容・理由 |
|---|---|---|---|
| S16 | **高** | 掲載文・手順書の「4面VS Code」前提を実態に更新 | 現在のアプリは VS Code だけでなく **Antigravity / Codex / Copilot / Gemini / Claude CLI など複数アプリ**を各スロットに割り当てられる（README はマルチアプリ前提）。一方 `store-listing-draft.md`・`release-notes-draft.md`・`WindowsAppsStoreリリース手順.md` は「4つの VS Code ウィンドウ」固定の旧表現が残っている。掲載文・審査メモ・スクショ計画を実態に合わせる |
| S17 | 中 | スクショ計画ファイル名の更新 | `assets/store/README.md` と手順書に残る `desktop-03-status-frame.png`（削除済みの外周フレーム機能名）を `desktop-03-focus.png` 等へ修正 |
| S18 | 中 | csproj にバージョン番号を明示 | 現状 `Version` 未指定で既定 `1.0.0`。manifest の `Identity Version` と一致させるため `<Version>1.0.0</Version>` 等を `TurtleAIQuartetHub.Panel.csproj` に明示しておくと更新管理が楽 |
| S19 | 中 | クリーン環境での動作確認手順を整備 | 新規Windowsユーザーで「インストール→初回起動→更新→アンインストール」、および **VS Code 未導入時 / `code` コマンド無し時の案内**が出るかを確認するチェックリスト。審査の動作確認と直結する |
| S20 | 低 | ローカルMSIX＋WACK の再実行（実態確認） | `dist/` は .gitignore 対象で現在ローカルに無い。`store-readiness.md` は「開発用MSIXでWACK PASS」と記すが手元に成果物が無いので、提出前に `New-LocalMsixPackage.ps1 -Sign -RunWack` を流して PASS を再取得する |
| S21 | 低 | GPL-3.0 のソース提供導線をアプリ内/掲載文に明記 | GitHub リポジトリURL を「ソースコード入手先」として掲載文・SUPPORT に明記（R8 と対）|

> S16 と S17 は「公開してから審査で指摘される / ユーザーに誤解される」リスクに直結するため、
> ソフト面で最優先に潰しておきたい項目です。

---

## 3. リリース面 TODO

Partner Center・公開URL・審査など、**リポジトリの外**で発生する作業です。ほぼ全部これからです。

| # | 優先 | 項目 | 内容 | 完了条件 |
|---|---|---|---|---|
| R1 | **高** | Store 開発者アカウント登録 | https://storedeveloper.microsoft.com から「Get started for free」。個人 or 法人を選ぶ（**個人→法人の変更は不可**なので最初に決める）| Apps & Games が見える |
| R2 | **高** | 本人確認（個人の場合） | 政府発行の身分証＋セルフィーをスマホで撮影。発行元表示名（例: `Turtle Village`）を決める | 確認完了・Publisher確定 |
| R3 | **高** | アプリ名の予約 | Apps and games →「New product」→「MSIX or PWA app」→ `Turtle AI Code Quartet Hub` を予約 | 名前予約済み |
| R4 | **高** | Identity / Publisher を manifest へ反映 | 予約後に得た `Package/Identity Name`・`Publisher`・`PublisherDisplayName` を `Package.appxmanifest` の開発用値（`TurtleAIQuartetHub.Dev` / `CN=TurtleAIQuartetHubDev`）から差し替え | manifest が正式値 |
| R5 | **高** | プライバシーポリシーを正式化＋HTTPS公開 | `PRIVACY.md` の「草案」「置き換えてください」を消し、公開者名・連絡先・公開日を記入。GitHub Pages 等で HTTPS URL として公開 | 有効な公開URL |
| R6 | **高** | サポートページを正式化＋HTTPS公開 | `SUPPORT.md` を正式な問い合わせ先・対応範囲へ。Partner Center に入れる HTTPS URL（または連絡先）を用意 | 有効な公開URL/連絡先 |
| R7 | **高** | Store スクリーンショット作成 | `assets/store/` に PNG。**最低1枚・推奨4枚以上**、1366×768px 以上、50MB以下。ダミーWS で撮影し個人情報・パス・APIキー・未公開コードを写さない | PNG が配置済み |
| R8 | 中 | GPL-3.0 配布義務の充足 | ソースコード提供URL（GitHub）・ライセンス同梱・著作権表示を満たす。掲載文/サポートにソース入手先を明記 | 義務を満たす状態 |
| R9 | 中 | 提出パッケージ `.msixupload` 生成 | Visual Studio で `TurtleAIQuartetHub.Package.wapproj` を右クリック →「Publish」→「Create App Packages」→ Microsoft Store 用。Release 構成 | `.msixupload` ができる |
| R10 | 中 | （任意）最終MSIXでWACK再実行 | 提出用パッケージに対し `appcert.exe test`。Storeはサーバ側でも認定するが事前確認推奨 | WACK PASS |
| R11 | 中 | Partner Center で提出情報を入力 | Pricing/Availability（市場・価格・公開日）、Properties（カテゴリ＝Developer tools 等・プライバシーURL・サポート情報）、Age ratings（年齢区分質問票）、Packages（`.msixupload`）、Store listings（説明・短い説明・機能・スクショ）| 全セクション入力済み |
| R12 | 中 | 審査向けメモ（Submission notes）を記入 | `runFullTrust` の説明＋動作確認手順（VS Code を入れて Launch Quartet 等）。手順書 §9 に英文テンプレあり | Notes 記入済み |
| R13 | 低 | 認定へ提出・結果確認 | `Submit for certification`。不合格なら認定レポートに沿って修正・再提出 | 認定通過 |
| R14 | 低 | 公開・公開後確認 | `Publish now` または日時指定。公開後に Store ページ・インストール・初回起動を実機確認 | 公開済み |
| R15 | 低 | 公開後運用の準備 | Action Center・レビュー・クラッシュ確認の運用、更新フロー（`Identity Version` を上げる）を把握 | 運用体制あり |

### 依存関係（着手順の目安）

```
R1 本人確認 ──► R3 アプリ名予約 ──► R4 Identity反映 ──► R9 .msixupload生成 ──┐
                                                                            ├─► R11 提出情報入力 ──► R13 提出 ──► R14 公開
R5 プライバシーURL ────────────────────────────────────────────────────────┤
R6 サポートURL ────────────────────────────────────────────────────────────┤
R7 スクリーンショット ─────────────────────────────────────────────────────┘
```

- **R1/R2（アカウント＋本人確認）が全ての前提**。ここで詰まる人が多いので最初に着手。
- **R5/R6/R7（公開URL・スクショ）は R1 と並行して進められる**。Partner Center を待つ必要がない。
- **R4（Identity 反映）は R3 の後でないと正式値が分からない**ので、`.msixupload` 生成（R9）はそれ以降。

---

## 4. 「これだけやれば出せる」最短ルート

1. **R1 → R2**: storedeveloper.microsoft.com で登録・本人確認（個人/法人を決定）
2. 並行して **S16/S17**: 掲載文・スクショ計画をマルチアプリ実態に更新
3. 並行して **R5/R6**: PRIVACY.md / SUPPORT.md を正式化し GitHub Pages 等で HTTPS 公開
4. 並行して **R7**: スクショ4枚をダミーWSで撮影し `assets/store/` へ
5. **R3 → R4**: アプリ名予約 → Identity を `Package.appxmanifest` へ反映
6. **S20/R9**: `New-LocalMsixPackage.ps1 -Sign -RunWack` で最終確認 → VS で `.msixupload` 生成
7. **R11 → R12 → R13**: Partner Center で提出情報入力・審査メモ記入・提出
8. **R14**: 認定通過後に公開

---

## 5. ローカル確認コマンド早見

```powershell
# 公開準備の自動チェック（基本ファイル・MSIX関連・スクショ寸法・ビルド・WACK結果を判定）
.\scripts\Test-StoreReadiness.ps1

# 自己完結 publish まで含めて確認
.\scripts\Test-StoreReadiness.ps1 -Publish

# ローカル確認用 MSIX を生成
.\scripts\New-LocalMsixPackage.ps1

# 署名と WACK まで実行（dist\msix-local\ に msix と wack-report.xml）
.\scripts\New-LocalMsixPackage.ps1 -Sign -RunWack
```

---

## 6. 関連ドキュメント

| 文書 | 役割 |
|---|---|
| [WindowsAppsStoreリリース手順.md](WindowsAppsStoreリリース手順.md) | **詳細な手順書**（アカウント登録・Partner Center・提出の各ステップ、英文テンプレ付き） |
| [store-readiness.md](store-readiness.md) | 公開前チェックリスト（リポジトリ内対応状況） |
| [msix-packaging-guide.md](msix-packaging-guide.md) | MSIX パッケージング手順 |
| [store-listing-draft.md](store-listing-draft.md) | Store 掲載文案（S16 で要更新） |
| [release-notes-draft.md](release-notes-draft.md) | リリースノート草案 |
| [../PRIVACY.md](../PRIVACY.md) | プライバシーポリシー草案（R5 で正式化） |
| [../SUPPORT.md](../SUPPORT.md) | サポート案内草案（R6 で正式化） |
| [../assets/store/README.md](../assets/store/README.md) | Store 画像素材チェック（R7 / S17） |

## 7. 公式参考リンク（2026年版で確認したもの）

- 個人開発者の無料登録: https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-individual-developer
- 開発者アカウント作成: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account
- アプリ提出 FAQ: https://learn.microsoft.com/en-us/windows/apps/publish/faq/submit-your-app
- MSIX パッケージ要件: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
- スクリーンショット/画像: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images
- プライバシー/サポート情報: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info
- Microsoft Store Policies: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
- Windows App Certification Kit: https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit
- コード署名オプション: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
