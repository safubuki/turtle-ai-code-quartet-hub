# Turtle AI Code Quartet Hub

4つの開発用ウィンドウを A-D のスロットとして起動し、2x2 に並べる Windows 向け WPF パネルです。
既定は各スロット VS Code の一括起動です。設定により Google Antigravity をワークスペース IDE として選択でき、Codex / GitHub Copilot / Gemini / Claude は各スロットのワークスペース CLI として起動できます。Codex / Claude の Windows アプリ版は、CLI とは別に補助ボタン行から起動できます。

## できること

- `Launch Quartet（一括起動）` で、各スロットに選択された VS Code / Antigravity / CLI を起動し、画面に2x2で配置
- 各スロットで IDE 枠の VS Code / Antigravity、CLI 枠の Codex / Claude / Gemini / Copilot を切り替え、同じワークスペースを開き直し
- 起動中スロットでも別の IDE/CLI ボタンを押すだけで、現在のウィンドウを閉じて選択アプリへ切り替え
- CLI は保存済みワークスペースをカレントディレクトリにした terminal ウィンドウとして起動。既定の GitHub Copilot CLI は対象ワークスペースで `copilot` だけを実行
- Codex / Claude の Windows アプリ版は、`Windows` ラベル付きの補助ボタンから起動
- メインパネルからスロットの保存情報をクリア
- 未検出のアプリケーションはグレーアウトし、設定で実行ファイルやコマンドを指定可能
- 縮小モードで小さな操作バーとして常時表示
- スロット A-D のタイトル、ワークスペース、控え Quartet を保存
- `ディスプレイ移動` で4面表示を別ディスプレイへまとめて移動
- タスクバー右クリックからスロット切替、表示モード切替、前面/背面操作を実行
- スロット別の VS Code user-data-dir で最近使ったワークスペースを分離

## 必要環境

- Windows
- VS Code
- .NET 10 SDK
- VS Code の `code` コマンド

`code` コマンドが使えない場合は、設定ファイルの `codeCommand` に `Code.exe` のパスを指定してください。

## 起動方法

開発ビルド: 実行中の本体が既定の `bin\Debug` をロックしていても通るよう、毎回別の artifacts path へ出力します。

```powershell
.\scripts\Build-Panel.ps1
```

ロックを避けたままビルドして、そのまま起動する場合:

```powershell
.\scripts\Build-Panel.ps1 -Run
```

通常ビルド: 実行中の `TurtleAIQuartetHub.exe` を停止しているときだけ使います。

```powershell
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

通常の開発実行:

```powershell
dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

`dotnet build` と `dotnet run` は既定の `src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows` を使うため、その場所の exe が起動中だとロックで失敗します。検証や反復ビルドは `Build-Panel.ps1` か `--artifacts-path` を使ってください。

ビルド済み exe を直接起動:

```powershell
.\src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows\TurtleAIQuartetHub.exe
```

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
- `inheritMainUserState`: 通常 VS Code の設定やスニペットをスロットへ引き継ぐか
- `defaultWorkspaceApplicationId`: スロットの既定アプリ。未設定時は `vscode`
- `applications`: VS Code、Antigravity、Codex CLI、GitHub Copilot CLI、Gemini CLI、Claude CLI、Codex / Claude Windows アプリなどの起動定義と検出候補
- `slots[].applicationId`: スロットごとの起動対象アプリ

`applications[].command` に実行ファイルのフルパスまたはコマンド名を指定できます。未指定または検出できない場合は、PATH、App Paths、スタートメニュー、一般的なインストール先から検出します。

実行時データは `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。

## 確認用コマンド

Store公開準備の確認:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

ローカル確認用 MSIX の生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

## 配布ビルド

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```

配布物には `LICENSE.txt` も同梱してください。

## 関連ドキュメント

- [PRIVACY.md](PRIVACY.md): プライバシーポリシー草案
- [docs/store-readiness.md](docs/store-readiness.md): Microsoft Store 公開前チェック
- [docs/store-listing-draft.md](docs/store-listing-draft.md): Store 掲載文案
- [docs/msix-packaging-guide.md](docs/msix-packaging-guide.md): MSIX パッケージング手順
- [docs/release-notes-draft.md](docs/release-notes-draft.md): リリースノート草案
- [docs/multi-application-launcher-spec.md](docs/multi-application-launcher-spec.md): 複数アプリケーション起動対応 仕様書
- [SUPPORT.md](SUPPORT.md): サポート案内草案
- [assets/store/README.md](assets/store/README.md): Store 画像素材チェック

## ライセンス

GNU General Public License v3.0 です。詳細は [LICENSE.txt](LICENSE.txt) を参照してください。
