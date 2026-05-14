# Turtle AI Code Quartet Hub プロジェクト詳細

更新日: 2026-05-13

## 概要
- 4 つの開発用ウィンドウを A-D スロットとして管理する Windows 向け WPF アプリ。
- 2x2 配置、集中表示、非表示/再表示、控え Quartet、タスクバー Jump List 操作を提供する。
- 既定のスロット起動対象は VS Code。
- VS Code / Antigravity はワークスペース IDE として、Codex / GitHub Copilot / Gemini / Claude はワークスペース CLI として各スロットで起動できる。
- Codex / Claude の Windows アプリ版は CLI とは別に補助ボタン行から起動できる。
- AI 状態表示、AI 状態監視、VS Code 外枠フレーム、AI 状態連動の点滅や色変更は削除済み。

## 技術スタック
- アプリ本体: C#, .NET 10, WPF
- パッケージ: MSIX / Windows Application Packaging Project
- 補助スクリプト: PowerShell
- 主な API: Win32 P/Invoke, System.Text.Json

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
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs`: VS Code 起動、HWND 割り当て、専用 user-data 準備。
- `src/TurtleAIQuartetHub.Panel/Services/WindowEnumerator.cs`: アプリごとの管理対象ウィンドウ列挙。
- `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`: Win32 ベースの配置、最大化、復元、前面/背面制御。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeWorkspaceState.cs`: VS Code / Antigravity の workspaceStorage とウィンドウタイトルからワークスペースを推定。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLayoutState.cs`: VS Code storage.json からレイアウト保存/復元。
- `src/TurtleAIQuartetHub.Panel/Services/TaskbarJumpListService.cs`: Jump List 更新。

## 実行時データ
- `%LOCALAPPDATA%/TurtleAIQuartetHub/slots.json`: visible slot と stored panel の保存状態。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data/{A|B|C|D}/...`: スロット別 VS Code user-data-dir。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json`: 任意のユーザー設定。
- タイトルバーの歯車設定から、IDE / CLI / Windows アプリの起動コマンドをこのユーザー設定へ保存できる。

## 複数アプリ起動
- `defaultWorkspaceApplicationId` がスロットの既定アプリ。未設定時は `vscode`。
- `applications` で VS Code、Antigravity、Codex CLI、GitHub Copilot CLI、Gemini CLI、Claude CLI、Codex / Claude Windows アプリの起動コマンド、引数、検出候補を定義する。
- `slots[].applicationId` と `slots.json` の `ApplicationId` で、スロット/控えごとの起動対象を保持する。
- VS Code は専用 user-data-dir と remote URI フォールバックを維持する。
- Antigravity は汎用 workspace IDE としてワークスペースパスを渡して起動し、新規ウィンドウを A-D の象限へ配置する。アプリ内でフォルダを開いた場合も `%APPDATA%/Antigravity/User/workspaceStorage` から最新パスを保存する。
- Codex / GitHub Copilot / Gemini / Claude CLI は、対象スロットの保存済みワークスペースをカレントディレクトリにした `cmd.exe` ウィンドウで起動する。
- GitHub Copilot CLI の既定は `copilot` コマンドのみ。ワークスペースパスを暗黙引数として渡さない。
- スロット内 UI は `IDE` 枠と `CLI` 枠に分ける。別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウを閉じてから押したアプリを同じ象限へ開く。
- Codex / Claude Windows アプリは `Windows` ラベル付きの補助ボタンとして表示する。
- 起動確認または periodic refresh でワークスペースを確認できたスロットは `SavedWorkspacePath` とタイトルを自動保存し、ワークスペース読み取りに失敗しても保存済みパスを消さない。
- 歯車設定では、表の Quartet と控え Quartet のタイトル、パス、保存済みパス、アプリ ID を一覧で確認・編集・空化できる。不完全な控えや重複控えは修復ボタンで整理できる。

## 確認コマンド
- ビルド: `.\scripts\Build-Panel.ps1`
- 開発実行: `.\scripts\Build-Panel.ps1 -Run`
- Store readiness: `.\scripts\Test-StoreReadiness.ps1`
- ローカル MSIX: `.\scripts\New-LocalMsixPackage.ps1`
