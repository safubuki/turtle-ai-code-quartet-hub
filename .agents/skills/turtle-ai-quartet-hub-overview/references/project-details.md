# Turtle AI Code Quartet Hub プロジェクト詳細

更新日: 2026-09-17

## 概要
- 4 つの開発用ウィンドウを A-D スロットとして管理する Windows 向け WPF アプリ。
- 2x2 配置、集中表示、非表示/再表示、控え Quartet、タスクバー Jump List 操作を提供する。
- 既定のスロット起動対象は VS Code。
- VS Code / Antigravity はワークスペース IDE として、Codex / Claude / GitHub Copilot / Grok Build / Antigravity CLI はワークスペース CLI として各スロットで起動できる。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は CLI とは別に補助ボタン行から起動できる。
- 通常表示の各スロットにはフォルダボタンがあり、保存済みまたは検出済みのローカルワークスペースを Explorer で開ける。SSH / remote URI のワークスペースではボタンを無効化する。
- AI 状態表示、AI 状態監視、VS Code 外枠フレーム、AI 状態連動の点滅や色変更は削除済み。
- `?` ヘルプと README は、Codex / Copilot / Claude / Grok の Windows ネイティブインストーラーを優先して表示し、npm または Bash 系の方法を代替手段として併記する。

## 技術スタック
- アプリ本体: C#, .NET 10, WPF
- パッケージ: MSIX / Windows Application Packaging Project
- 補助スクリプト: PowerShell
- 主な API: Win32 P/Invoke, System.Text.Json

## 開発環境
- Windows 10 / Windows 11 では .NET 10 SDK が入っていれば、追加 workload なしで `dotnet build` と `dotnet run` を実行できる。
- 未導入端末では PowerShell から `winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements` で導入できる。
- SDK 導入後は新しい PowerShell または VS Code ターミナルを開き、`dotnet --list-sdks` と `dotnet --info` で確認する。

## 主なファイル
- `TurtleAIQuartetHub.sln`: WPF 本体ソリューション。
- `src/TurtleAIQuartetHub.Panel/MainWindow.xaml`: 標準表示/縮小表示 UI。
- `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`: UI イベント、起動、配置、集中表示、前面/背面、控え保存。
- `src/TurtleAIQuartetHub.Panel/Models/AppConfig.cs`: 設定読み込み、既定アプリ定義、正規化。
- `src/TurtleAIQuartetHub.Panel/Models/ToolApplicationConfig.cs`: 起動対象アプリの設定モデル。
- `src/TurtleAIQuartetHub.Panel/Models/LauncherApplication.cs`: 検出済みアプリのランタイム状態。
- `src/TurtleAIQuartetHub.Panel/Models/ApplicationPathSetting.cs`: 設定画面で編集するアプリ起動コマンドの表示モデル。
- `src/TurtleAIQuartetHub.Panel/Models/WindowSlot.cs`: A-D スロット状態モデル。
- `src/TurtleAIQuartetHub.Panel/Models/SlotApplicationOption.cs`: スロット内の IDE / CLI 選択ボタン用モデル。
- `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs`: スロット状態、保存復元、控え保存管理。
- `src/TurtleAIQuartetHub.Panel/Services/ApplicationDetectionService.cs`: PATH、App Paths、スタートメニュー、WindowsApps、一般的なインストール先からアプリを検出。
- `src/TurtleAIQuartetHub.Panel/Services/ApplicationLauncher.cs`: VS Code 以外の workspace IDE / workspace CLI 起動、補助アプリ起動。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs`: VS Code 起動、HWND 割り当て、専用 user-data 準備、取り逃がした既存スロットウィンドウの再接続。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeUserSettings.cs`: ハブ管理の VS Code ネットワーク設定。`User/settings.json` は通常起動の `%APPDATA%/Code/User/settings.json`（Roaming）と同じ実体にする。ハブは Roaming の内容を書き換えない。`storage.json` / Cache / サインイン状態は共有しない。
- `src/TurtleAIQuartetHub.Panel/Services/WindowEnumerator.cs`: アプリごとの管理対象ウィンドウ列挙。
- `src/TurtleAIQuartetHub.Panel/Services/ManagedWindowCloseService.cs`: `WM_CLOSE` 送信後の HWND 消滅確認。未終了ウィンドウを誤って管理解除しない。
- `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`: Win32 ベースの配置、配置ずれ確認、最大化、復元、前面/背面制御。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeWorkspaceState.cs`: VS Code / Antigravity の workspaceStorage とウィンドウタイトルからワークスペースを推定。タイトルはフォルダ名の部分文字列ではなく、ワークスペース段として照合する。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLayoutState.cs`: VS Code storage.json からレイアウト保存/復元。
- `src/TurtleAIQuartetHub.Panel/Services/TaskbarJumpListService.cs`: Jump List 更新。

## 実行時データ
- `%LOCALAPPDATA%/TurtleAIQuartetHub/slots.json`: visible slot と stored panel の保存状態。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data/{A|B|C|D}/...`: `useDedicatedUserDataDirs=true` のときだけ使うスロット別 VS Code user-data-dir。既定は標準プロファイル共有のためこのフォルダは作らず、起動時に残存分を回収する。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json`: 任意のユーザー設定。
- タイトルバーの歯車設定から、IDE / CLI / Windows アプリの起動コマンドと、VS Code 共通のネットワーク設定（オープンネットワーク / プロキシ）をこのユーザー設定へ保存できる。

## 複数アプリ起動
- `defaultWorkspaceApplicationId` がスロットの既定アプリ。未設定時は `vscode`。
- `applications` で VS Code、Antigravity IDE、Codex CLI、Claude CLI、GitHub Copilot CLI、Grok Build CLI、Antigravity CLI、Codex / ChatGPT / Claude / Antigravity2 Windows アプリの起動コマンド、引数、検出候補を定義する。
- `slots[].applicationId` と `slots.json` の `ApplicationId` で、スロット/控えごとの起動対象を保持する。
- VS Code の既定は標準 user-data の共有。専用 `user-data-dir` は任意設定で、有効時は remote URI フォールバックと `code.lock` 再接続を維持する。
- Antigravity は汎用 workspace IDE として `%LOCALAPPDATA%/Programs/Antigravity IDE/Antigravity IDE.exe` 相当を優先検出し、ワークスペースパスを渡して起動し、新規ウィンドウを A-D の象限へ配置する。アプリ内でフォルダを開いた場合も `%APPDATA%/Antigravity IDE/User/workspaceStorage` から最新パスを保存する。旧名称やインストール形態に対応するため、`Antigravity`、`Google/Antigravity IDE`、`Google/Antigravity` と Local 側の候補も引き続き探索する。
- Codex / Claude / GitHub Copilot / Grok Build / Antigravity CLI は、対象スロットの保存済みワークスペースをカレントディレクトリにした `cmd.exe` ウィンドウで起動する。Antigravity CLI は公式コマンド `agy` を使い、公式 Windows インストーラーの `%LOCALAPPDATA%\agy\bin` も検出・起動 PATH に含める。
- 旧 `gemini` アプリ ID は読み込み時に `antigravity-cli` へ移行し、旧 Gemini のコマンドや表示名は引き継がず Antigravity CLI の既定定義へ置き換える。
- GitHub Copilot CLI の既定は `copilot` コマンドのみ。ワークスペースパスを暗黙引数として渡さない。
- スロット内 UI は `IDE` 枠と `CLI` 枠に分ける。別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウを閉じてから押したアプリを同じ象限へ開く。
- Codex / ChatGPT / Claude / Antigravity2 Windows アプリは `Windows` ラベル付きの補助ボタンとして表示し、Antigravity2 は Claude の右側に置く。
- 縮小表示の各スロットには 1 個の IDE トグルを置き、選択中の VS Code / Antigravity IDE を V / A で示してもう一方へ切り替える。補助アプリ行の左側には丸い矢印と「控え」を置き、同じ幅のまま小型の控えカードを 2x2 で表示する。
- 起動確認または periodic refresh でワークスペースを確認できたスロットは `SavedWorkspacePath` とタイトルを自動保存し、ワークスペース読み取りに失敗しても保存済みパスを消さない。
- 歯車設定では、表の Quartet と控え Quartet のタイトル、パス、保存済みパス、アプリ ID を一覧で確認・編集・空化できる。不完全な控えや重複控えは修復ボタンで整理できる。
- 歯車設定の「VS Code 共通ユーザー設定」はプロキシをハブ設定へ保存する。`User/settings.json`（プロキシ・テーマ・SSH プロキシ）は通常起動とハブ起動で同じ Roaming ファイルを使う。サイドバー幅、サインイン、チャット履歴、Cache は対象外。

## 確認コマンド
- 配布用の自己完結・単一ファイル win-x64 exe: ルートの `publish.bat` を実行する。出力は `dist/turtle-ai-quartet-hub/`。
- 通常ビルド: `dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- 終了確認、ワークスペースタイトル照合、VS Code 共通ユーザー設定の回帰テスト: `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj`
- 通常実行: `dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- ビルド: `.\scripts\Build-Panel.ps1`
- 開発実行: `.\scripts\Build-Panel.ps1 -Run`
- Store readiness: `.\scripts\Test-StoreReadiness.ps1`
- ローカル MSIX: `.\scripts\New-LocalMsixPackage.ps1`

## Windows 補助アプリボタン（2026-09-28 更新）
- 標準表示と縮小表示では同じ `AuxiliaryApplications` を使用する。表示する既定ボタンは `GPT/Codex`、Claude、Antigravity2 の順で、ChatGPT Classic の `chatgpt-app` ボタンは表示しない。
- `chatgpt-app` の設定定義は既存ユーザー設定との互換性のため維持する。Codex Windows アプリの ID と検出条件は変えず、旧 `ShortName=Codex` を読み込んだときは `GPT/Codex` に移行する。
- 2026-09-28 のレイアウト調整では、縮小表示の「控え」を左端へ分離し、共用行の `AIアプリ` ラベルと 3 ボタンを右揃えにした。ボタンは同寸の 86×28 DIP、文字 11.5 DIP。
- その後の縮小表示調整では、標準表示の寸法を維持したまま、縮小表示の 3 ボタンだけを 78×25 DIP・文字 10.5 DIP にした。
