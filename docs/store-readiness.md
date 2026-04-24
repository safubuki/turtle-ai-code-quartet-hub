# Microsoft Store 公開準備

この文書は、Turtle AI Code Quartet Hub を Microsoft Store へ公開する前に、このリポジトリ内で確認・改善する内容を管理するためのチェックリストです。

## 配布方針

Microsoft は Windows アプリのインストール、更新、Store 配布の形式として MSIX を案内しています。WPF デスクトップアプリは、Visual Studio の Windows Application Packaging Project または MSIX パッケージング手順で Store 申請用パッケージを作成できます。

参照先:

- Publish your first Windows app: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app
- Packaging overview: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/
- Package desktop apps in Visual Studio: https://learn.microsoft.com/en-us/windows/msix/desktop/vs-package-overview
- Microsoft Store Policies: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies

## このリポジトリで対応済み

- アプリ表示名とアセンブリメタデータを `Turtle AI Code Quartet Hub` に統一。
- Windows アプリケーションマニフェストで `asInvoker` を明示し、管理者権限を要求しない構成にした。
- 実行時状態の保存先を `%LOCALAPPDATA%\TurtleAIQuartetHub\` に統一。
- `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json` からユーザー設定を優先読み込みできるようにした。Store / MSIX 配布でアプリ配置先が読み取り専用になっても、ユーザー設定を安全に扱える。
- [PRIVACY.md](../PRIVACY.md) にプライバシーポリシー草案を追加。
- [store-listing-draft.md](store-listing-draft.md) に Store 掲載文案の草案を追加。
- README から公開準備・プライバシー関連文書へ誘導する導線を追加。

## Store 申請時に明記すること

Partner Center の説明、プライバシーポリシー、サポート文書では次を明示してください。

- Visual Studio Code は別途インストールが必要。
- このアプリは独立したユーティリティであり、Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic の公式アプリ、提携アプリ、承認済みアプリではない。
- Win32 ウィンドウ API を使って VS Code ウィンドウを配置する。
- 状態表示のために、ローカルの VS Code ウィンドウタイトル、UI Automation 状態、一部の VS Code 拡張ログを読む。
- アプリ独自のテレメトリ、プロンプト、ソースコード、ワークスペース情報、ログを公開者へ送信しない。
- VS Code 内で使用する remote workspace、AI サービス、拡張機能は、それぞれのツール・サービスの仕様とプライバシーポリシーに従う。

## 一般公開前に残っている作業

- Windows Application Packaging Project を追加し、MSIX 生成手順をリポジトリに取り込む。
- Visual Studio または同等の MSIX 手順で `.msixupload` または `.appxupload` を生成する。
- Partner Center で最終アプリ名を予約する。
- プライバシーポリシーを公開 HTTPS URL でホストし、`PRIVACY.md` の連絡先を正式情報へ更新する。
- 個人のワークスペース名や機密情報が写らないスクリーンショットを用意する。
- サポート URL と公開者連絡先を用意する。
- MSIX パッケージに対して Windows App Certification Kit をローカル実行する。
- クリーンな Windows ユーザープロファイルで、インストール、初回起動、アップグレード、アンインストールを確認する。
- VS Code 未インストール時、または `code` コマンドがない場合の表示と案内を確認する。
- GPL-3.0 での Store 配布時に、ソースコード提供、ライセンス文書同梱、著作権表示などの義務を満たす。

## 推奨するパッケージング方針

最初は手作業で package manifest を組むより、Visual Studio の Windows Application Packaging Project を使う方針を推奨します。WPF デスクトップアプリの MSIX 生成と Partner Center 申請用パッケージ作成の流れに合わせやすいためです。

パッケージには次を含めます。

- WPF アプリ本体。
- `config\turtle-ai-quartet-hub.example.json`。
- `LICENSE.txt`。
- Partner Center で構成したパッケージ ID と publisher 情報。

ユーザー設定はインストール先へ書き込まず、`%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json` を使います。

## ローカル確認コマンド

起動中アプリを上書きせずにビルド確認する場合:

```powershell
$out = Join-Path $env:TEMP 'vscode-square-build-check'
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -o $out
```

フレームワーク依存版の publish:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -o .\dist\turtle-ai-quartet-hub-framework
```

自己完結版の publish:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```

