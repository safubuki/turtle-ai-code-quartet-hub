# Turtle AI Code Quartet Hub

Turtle AI Code Quartet Hub は、4つの開発ワークスペースを A-D のスロットとしてまとめて起動し、画面上へ 2x2 に整列表示する Windows 向けランチャーアプリです。

VS Code だけを4面で開くためのツールではなく、スロットごとに VS Code / Google Antigravity IDE / Codex CLI / GitHub Copilot CLI / Antigravity CLI / Grok CLI / Claude CLI を選び、同じワークスペースを IDE と CLI のどちらでもすばやく開き直せることを重視しています。複数案件、複数AIエージェント、複数ターミナルを行き来する作業を、ひとつの小さな操作パネルに寄せるためのアプリです。

## 特徴

- **4面ワークスペース起動**: `Launch Quartet（一括起動）` で A-D の各スロットに選択済みアプリを起動し、2x2 に配置します。
- **IDE / CLI の切り替え**: 各スロットで VS Code / Antigravity IDE と、Codex / Copilot / Antigravity / Grok / Claude CLI を選択できます。
- **同じ位置でアプリを差し替え**: 起動中スロットで別の IDE / CLI を押すと、現在のウィンドウを閉じて同じ象限へ開き直します。
- **ワークスペースを覚える**: スロットのタイトル、ワークスペースパス、選択アプリ、控え Quartet を保存します。
- **CLI をワークスペース直下で起動**: CLI は対象フォルダをカレントディレクトリにした terminal として開きます。
- **AI アプリも起動**: CLI とは別に、右揃えの `GPT/Codex` / Claude / Antigravity2 ボタンから各 Windows アプリを開けます。
- **フォーカス表示と4面表示**: スロットボタンで1面フォーカス表示と4面表示を切り替えます。
- **縮小モード**: 小さな操作バーとして常駐し、各スロットの 1 個の V / A ボタンで VS Code と Antigravity IDE を切り替えられます。V は VS Code ロゴに近い青、A は多色グラデーションの枠で表示します。ウィンドウ状態（緑の「起」／赤の「停」）、左端の控え、右端の AI アプリ起動も使えます。状態ボタンを押すと、起動中のウィンドウは終了、停止中は起動します。
- **極小モード**: 116 DIP 四方の 2x2 パネルです。上部の拡大アイコンで標準表示、タイルアイコンで縮小表示へ直接切り替えられます。
- **タスクバー連携**: Jump List からスロット切替、表示モード切替、前面/背面操作を実行できます。

## 必要環境

- Windows 10 / Windows 11
- .NET 10 SDK
- VS Code
- VS Code の `code` コマンド

任意で使用するツール:

- Google Antigravity IDE
- Codex CLI
- GitHub Copilot CLI
- Antigravity CLI
- Grok CLI
- Claude CLI / Claude Code
- Codex / Claude / Antigravity2 の Windows アプリ版

`code` コマンドが使えない場合は、設定ファイルの `codeCommand` に `Code.exe` のパスを指定してください。

## .NET 環境構築

PowerShell で .NET 10 SDK を導入します。Windows 10 / Windows 11 のどちらでも同じ手順です。

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
```

導入後に新しい PowerShell または VS Code ターミナルを開き、SDK が見えていることを確認してください。

```powershell
dotnet --list-sdks
dotnet --info
```

`dotnet` コマンドが見つからない場合は、端末を開き直して PATH を再読み込みしてください。

## 起動方法

開発中は、実行中の本体が既定の `bin\Debug` をロックしていても通る `Build-Panel.ps1` を使うのがおすすめです。

```powershell
.\scripts\Build-Panel.ps1
```

ビルドしてそのまま起動する場合:

```powershell
.\scripts\Build-Panel.ps1 -Run
```

通常ビルド:

```powershell
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

通常の開発実行:

```powershell
dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

`dotnet build` と `dotnet run` は既定の `src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows` を使うため、その場所の exe が起動中だとロックで失敗することがあります。反復確認では `Build-Panel.ps1` か `--artifacts-path` を使ってください。

## 基本操作

1. A-D の各スロットにワークスペースを登録します。
2. スロット内の `IDE` または `CLI` から起動したいアプリを選びます。
3. `Launch Quartet（一括起動）` を押すと、未起動スロットがまとめて開きます。
4. 起動中スロットの別アプリボタンを押すと、同じ位置でアプリを切り替えます。
5. スロットボタンを押すと、1面フォーカス表示と4面表示を切り替えます。
6. 右上のゴミ箱アイコンで、保存済みのスロット情報を確認付きで削除できます。

## CLI インストール例

アプリ内の `?` ヘルプにも同じ内容を表示します。IDE と Windows アプリは各製品の公式サイトを参照してください。

Codex CLI（Windows PowerShell・推奨）:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
```

Codex CLI（npm）:

```powershell
npm install -g @openai/codex
```

GitHub Copilot CLI（Windows・推奨）:

```powershell
winget install GitHub.Copilot
```

GitHub Copilot CLI（npm）:

```powershell
npm install -g @github/copilot
```

Antigravity CLI（Windows PowerShell・推奨）:

```powershell
irm https://antigravity.google/cli/install.ps1 | iex
```

Antigravity CLI（Windows CMD）:

```cmd
curl -fsSL https://antigravity.google/cli/install.cmd -o install.cmd && install.cmd && del install.cmd
```

Claude Code（PowerShell・推奨）:

```powershell
irm https://claude.ai/install.ps1 | iex
```

Claude Code（CMD）:

```cmd
curl -fsSL https://claude.ai/install.cmd -o install.cmd && install.cmd && del install.cmd
```

Claude Code（npm）:

```powershell
npm install -g @anthropic-ai/claude-code
```

Grok Build CLI（PowerShell）:

```powershell
irm https://x.ai/cli/install.ps1 | iex
```

Grok Build CLI（Git Bash／WSL）:

```bash
curl -fsSL https://x.ai/cli/install.sh | bash
```

自律実行向けの起動オプション例:

```powershell
codex --ask-for-approval never --sandbox workspace-write
copilot --allow-all
agy --dangerously-skip-permissions
claude --permission-mode bypassPermissions
```

これらは承認確認を減らす、または権限を広げるための例です。信頼できるワークスペースでのみ使ってください。

## 設定

ユーザー設定は `%LOCALAPPDATA%` 側に置くのがおすすめです。

```powershell
$configDir = Join-Path $env:LOCALAPPDATA 'TurtleAIQuartetHub\config'
New-Item -ItemType Directory -Force $configDir
Copy-Item .\config\turtle-ai-quartet-hub.example.json (Join-Path $configDir 'turtle-ai-quartet-hub.json')
```

設定ファイルは次の順で読み込まれます。

1. `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json`
2. `config\turtle-ai-quartet-hub.json`
3. `config\turtle-ai-quartet-hub.example.json`
4. アプリ内の既定値

主な設定項目:

- `codeCommand`: VS Code の起動コマンドまたは `Code.exe` のパス
- `launchTimeoutSeconds`: VS Code / Antigravity / CLI 起動待ち時間
- `remoteReconnectTimeoutSeconds`: SSH / Remote 接続の再接続待ち時間
- `statusRefreshIntervalMilliseconds`: 管理中ウィンドウ状態とワークスペース表示の更新間隔
- `useDedicatedUserDataDirs`: スロット別に VS Code user-data-dir を切るか。既定は `false`（標準プロファイル共有）。`true` にするとウィンドウ識別は容易になるが、キャッシュがスロット数だけ増える。専用時も `User/settings.json` は `%APPDATA%/Code/User/settings.json`（Roaming）と同じ実体へリンクし、通常起動のプロキシ設定を共有する。Roaming の内容はハブから書き換えない
- `inheritMainUserState`: 専用 user-data-dir 利用時に、通常 VS Code の設定やスニペットをスロットへ引き継ぐか。`settings.json` は未作成のときだけ種としてコピーし、既存のパネル設定は上書きしない。`globalStorage` や Chromium キャッシュはコピーしない
- `manageVsCodeUserSettings` / `vsCodeUseHttpProxy` / `vsCodeHttpProxy` / `vsCodeHttpNoProxy`: 歯車設定の「VS Code 共通ユーザー設定」。プロキシはハブ設定へ保存し、VS Code 起動時にプロセス環境変数として渡す。専用プロファイルがある場合だけその `settings.json` のプロキシキーを更新する。標準の `%APPDATA%/Code/User/settings.json` は変更しない
- `defaultWorkspaceApplicationId`: スロットの既定アプリ。未設定時は `vscode`
- `applications`: VS Code、Antigravity IDE、Codex CLI、GitHub Copilot CLI、Antigravity CLI、Claude CLI、Codex / ChatGPT / Claude Windows アプリなどの起動定義と検出候補
- `slots[].applicationId`: スロットごとの起動対象アプリ

`applications[].command` には、実行ファイルのフルパスまたはコマンド名を指定できます。未指定または検出できない場合は、PATH、App Paths、スタートメニュー、WindowsApps、一般的なインストール先から検出します。CLI については、npm / pnpm / Volta の shim 置き場、`%LOCALAPPDATA%\agy\bin`、`~\.local\bin` も探索します。旧設定の `gemini` アプリ ID は読み込み時に `antigravity-cli` へ移行されます。

実行時データは `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。

## データとプライバシー

- スロット、控え Quartet、ワークスペース履歴、VS Code スロット別 user-data-dir はローカルに保存されます。
- アプリ独自の利用状況送信は行いません。

## 確認用コマンド

Store 公開準備の確認:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

ローカル確認用 MSIX の生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

## 配布ビルド

Windows で自己完結型の win-x64 exe を作る場合は、リポジトリ直下の `publish.bat` をダブルクリックします。コマンドからも実行できます。

```powershell
.\publish.bat
```

生成先は `dist\turtle-ai-quartet-hub\TurtleAIQuartetHub.exe` です。配布するときは、同じフォルダの `LICENSE.txt` と設定例を含め、フォルダの中身をまとめて渡してください。

発行する PC には .NET 10 SDK が必要です。`publish.bat` は発行時の NuGet 脆弱性監査を省き、利用できないパッケージソースは、必要なパッケージがほかのソースやローカルキャッシュにあれば警告に留めます。通常発行時の署名状態の表示も、確認に失敗しても発行を止めません。`--sign` を指定した場合の署名失敗はエラーになります。

自己完結型 exe に必要な .NET ランタイムパックがその PC にない場合は、NuGet からの取得が必要です。`NU1100` などで依存関係の復元に失敗した場合は、`dotnet nuget list source` でソース設定を確認し、必要なパッケージを取得できる接続を用意してください。監査を省いても、必要なパッケージ自体が取得できなければ発行できません。

### Smart App Control とコード署名

通常の `publish.bat` が作る exe は未署名です。Windows の Smart App Control はクラウドで安全と判断できた exe は通しますが、判断できない未署名 exe はブロックします。コードや発行内容が変わって exe のハッシュが変わると、以前の exe が起動できても新しい exe の起動は保証されません。バッチは署名状態を確認して警告します。ローカル MSIX 用の開発用自己署名証明書も、この問題の解決には使えません。

配布用 exe を署名するには、Microsoft Trusted Root Program に含まれる認証局が発行したコード署名証明書を Windows の `CurrentUser/My` または `LocalMachine/My` に用意し、証明書の秘密鍵へアクセスできる状態にします。証明書の拇印と、発行元が指定する RFC 3161 タイムスタンプ URL を環境変数に設定して発行します。秘密鍵や証明書のパスワードはリポジトリに保存しません。

```powershell
$env:TURTLE_CODE_SIGN_THUMBPRINT = '<証明書の40文字の拇印>'
$env:TURTLE_CODE_SIGN_TIMESTAMP_URL = '<発行元指定のRFC 3161タイムスタンプURL>'
.\publish.bat --sign
Get-AuthenticodeSignature .\dist\turtle-ai-quartet-hub\TurtleAIQuartetHub.exe
```

`--sign` は証明書の用途、有効期限、信頼チェーン、失効状態を確認し、SHA-256 で署名・タイムスタンプ後に署名を検証します。証明書がない場合や署名に失敗した場合は配布成功と表示しません。署名後に exe を変更すると署名が無効になるため、ファイルの加工は署名前に済ませてください。署名済みでも別のセキュリティ判定が行われる可能性があるため、配布前には Smart App Control が有効な端末で起動を確認します。

Microsoft Store の MSIX として公開する場合は Store 側で署名されます。Store 用の準備は [MSIX パッケージング手順](docs/msix-packaging-guide.md) を参照してください。

手動で発行する場合は次のコマンドも使えます。

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true -o .\dist\turtle-ai-quartet-hub
```

## 関連ドキュメント

- [PRIVACY.md](PRIVACY.md): プライバシーポリシー草案
- [SUPPORT.md](SUPPORT.md): サポート案内草案
- [docs/multi-application-launcher-spec.md](docs/multi-application-launcher-spec.md): 複数アプリケーション起動対応 仕様書
- [docs/store-release-todo.md](docs/store-release-todo.md): Store 公開 TODO / 現状把握（ソフト面・リリース面の進捗一覧）
- [docs/store-readiness.md](docs/store-readiness.md): Microsoft Store 公開前チェック
- [docs/store-listing-draft.md](docs/store-listing-draft.md): Store 掲載文案
- [docs/msix-packaging-guide.md](docs/msix-packaging-guide.md): MSIX パッケージング手順
- [docs/release-notes-draft.md](docs/release-notes-draft.md): リリースノート草案
- [assets/store/README.md](assets/store/README.md): Store 画像素材チェック

## ライセンス

GNU General Public License v3.0 です。詳細は [LICENSE.txt](LICENSE.txt) を参照してください。
