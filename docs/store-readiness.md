# Microsoft Store 公開準備

この文書は、Turtle AI Code Quartet Hub を Microsoft Store へ公開する前に、このリポジトリ内で確認・改善する内容を管理するチェックリストです。

## 配布方針

Microsoft は Windows アプリのインストール、更新、Store 配布の形式として MSIX を案内しています。WPF デスクトップアプリは、Visual Studio の Windows Application Packaging Project または MSIX パッケージング手順で Store 申請用パッケージを作成できます。

参考:
- Publish your first Windows app: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app
- Packaging overview: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/
- Package desktop apps in Visual Studio: https://learn.microsoft.com/en-us/windows/msix/desktop/vs-package-overview
- Microsoft Store Policies: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies

## このリポジトリで対応済み

- アプリ表示名とアセンブリメタデータを `Turtle AI Code Quartet Hub` に統一。
- Windows アプリケーションマニフェストで `asInvoker` を明示し、管理者権限を要求しない構成にした。
- 実行時状態の保存先を `%LOCALAPPDATA%\TurtleAIQuartetHub\` に統一。
- `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json` からユーザー設定を優先読み込みできる。
- `TurtleAIQuartetHub.sln` を追加し、WPF 本体をソリューションから開ける。
- ビルド / publish 出力へ `LICENSE.txt` を同梱する。
- [msix-packaging-guide.md](msix-packaging-guide.md) に MSIX パッケージング手順を追加。
- `src/TurtleAIQuartetHub.Package/Package.appxmanifest` と開発用 Packaging Project を追加。
- `src/TurtleAIQuartetHub.Package/Assets/` にパッケージ用ロゴ画像を追加。
- [../scripts/New-LocalMsixPackage.ps1](../scripts/New-LocalMsixPackage.ps1) にローカル確認用 MSIX 生成スクリプトを追加。
- [../.github/workflows/windows-build.yml](../.github/workflows/windows-build.yml) に Windows ビルドと公開準備チェックの CI を追加。
- [release-notes-draft.md](release-notes-draft.md) にリリースノート草案を追加。
- [../SUPPORT.md](../SUPPORT.md) にサポート案内草案を追加。
- [../assets/store/README.md](../assets/store/README.md) に Store 画像素材の要件と撮影前チェックを追加。
- [../scripts/Test-StoreReadiness.ps1](../scripts/Test-StoreReadiness.ps1) にローカル公開準備チェックを追加。
- [../PRIVACY.md](../PRIVACY.md) にプライバシーポリシー草案を追加。
- [store-listing-draft.md](store-listing-draft.md) に Store 掲載文案の草案を追加。

## 現在の状態

| 項目 | 状態 | メモ |
|---|---|---|
| アプリ名/表示名 | 済み | `Turtle AI Code Quartet Hub` に統一済み。 |
| 管理者権限 | 済み | `asInvoker`。 |
| ユーザー設定保存先 | 済み | `%LOCALAPPDATA%` 優先。 |
| ライセンス同梱 | 済み | ビルド出力へ `LICENSE.txt` をコピー。 |
| CI | 済み | Windows 上でビルドと公開準備チェックを実行。 |
| MSIX Packaging Project | 開発用追加済み | Store 提出前に Partner Center の Identity / Publisher へ差し替える。 |
| `.msixupload` / `.appxupload` | 未着手 | Partner Center 連携後に生成する。 |
| WACK 実行 | 開発用 MSIX で PASS | 最終 Store 提出パッケージで再実行する。 |
| Store 画像 | 未着手 | `assets/store/` に公開用 PNG を配置する。 |
| サポート文書 | 草案 | 連絡先とサポート URL は正式情報へ更新する。 |
| プライバシーポリシー | 草案 | 連絡先、公開者名、公開 URL は正式情報へ更新する。 |
| Store 掲載文案 | 草案 | 提出前に文言と画像を最終確認する。 |

## Store 申請時に明記すること

Partner Center の説明、プライバシーポリシー、サポート文書では次を明記する。

- Visual Studio Code は既定のスロット起動対象として別途インストールが必要。
- Antigravity、Codex CLI、GitHub Copilot CLI、Gemini CLI、Claude CLI を使う場合も、それぞれ別途インストールが必要。
- Codex / ChatGPT / Claude の Windows アプリ版は補助ボタンから起動できるが、このアプリに同梱されない。
- このアプリは独立したユーティリティであり、Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic、Google の公式アプリ、提携アプリ、承認済みアプリではない。
- Win32 ウィンドウ API を使って管理対象ウィンドウを配置する。
- 管理対象ウィンドウの配置と復元のために、ローカルのウィンドウタイトルと保存済み設定を参照する。
- アプリ独自のテレメトリ、プロンプト、ソースコード、ワークスペース情報、ログを公開者へ送信しない。
- VS Code 内で使用する remote workspace、AI サービス、拡張機能は、それぞれのツールやサービスの仕様とプライバシーポリシーに従う。

## 一般公開前に残っている作業

- Partner Center の Identity / Publisher を `Package.appxmanifest` に反映する。
- Visual Studio または同等の MSIX 手順で `.msixupload` または `.appxupload` を生成する。
- Partner Center で最終アプリ名を予約する。
- プライバシーポリシーを公開 HTTPS URL でホストし、`PRIVACY.md` の連絡先を正式情報へ更新する。
- 個人のワークスペース名や機密情報が写らないスクリーンショットを用意する。
- サポート URL と公開者連絡先を用意する。
- 最終 MSIX / `.msixupload` に対して Windows App Certification Kit を再実行する。
- クリーンな Windows ユーザープロファイルで、インストール、初回起動、アップグレード、アンインストールを確認する。
- VS Code 未インストール時、または `code` コマンドがない場合の表示と案内を確認する。
- GPL-3.0 での Store 配布時に、ソースコード提供、ライセンス文書同梱、著作権表示などの義務を満たす。

## ローカル確認コマンド

公開準備チェック:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

自己完結 publish まで含める場合:

```powershell
.\scripts\Test-StoreReadiness.ps1 -Publish
```

ローカル確認用 MSIX を生成する場合:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

署名と WACK まで含める場合:

```powershell
.\scripts\New-LocalMsixPackage.ps1 -Sign -RunWack
```

起動中アプリを上書きせずにビルド確認する場合:

```powershell
.\scripts\Build-Panel.ps1
```
