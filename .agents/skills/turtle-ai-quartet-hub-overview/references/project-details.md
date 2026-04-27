# Turtle AI Code Quartet Hub プロジェクト詳細

更新日: 2026-04-28

## 1. プロジェクト概要

- 4 つの VS Code ウィンドウを A-D スロットとして管理する Windows 向け WPF アプリ。
- 2x2 配置、集中表示、非表示/再表示、裏保存 Quartet、AI 状態表示、タスクバー Jump List 操作を提供する。
- AI 状態は外部送信ではなく、ローカル UI Automation とローカル拡張ログから推定する。

## 2. 技術スタック

- アプリ本体: C#, .NET 10, WPF
- パッケージ: MSIX / Windows Application Packaging Project
- 補助ツール: C# コンソールツール、PowerShell スクリプト
- 主な API: Win32 P/Invoke, UI Automation, System.Text.Json

## 3. ワークスペース構成

### 3-1. ルート主要ファイル

- `TurtleAIQuartetHub.sln`: ソリューション
- `README.md`: 利用方法、ビルド、設定、確認コマンド
- `IMPLEMENTATION_PLAN.md`: 初期設計方針、アーキテクチャ、MVP 段階整理
- `PRIVACY.md`: プライバシー文書草案
- `SUPPORT.md`: サポート案内草案
- `LICENSE.txt`: GPL-3.0
- `debug.log`: ローカル実行ログ

### 3-2. 主要ディレクトリ

- `.github/`: GitHub 運用用の workflow、template、skills
- `.agents/`: エージェント運用用の mirrored skills / workflows
- `.vscode/`: ワークスペース設定
- `.turtle-ai-quartet-hub/user-data/`: ローカルな VS Code 用 user-data 置き場
- `assets/store/`: Store 素材関連メモ
- `config/`: 設定 JSON の example / 実運用ファイル
- `docs/`: Store 公開、テレメトリ、リリースノートなどの文書
- `scripts/`: Store readiness、MSIX 生成、AI 状態スモーク用 PowerShell
- `src/`: 本体アプリとパッケージプロジェクト
- `tools/`: AI 状態診断ツール
- `dist/`, `bin/`, `obj/`, `.dotnet_home/`: 生成物やローカル環境用。通常の保守対象外

### 3-3. docs 配下

- `docs/telemetry-notes.md`: AI 状態検出の考え方、ローカルデータの扱い
- `docs/store-readiness.md`: Store 公開前チェック
- `docs/store-listing-draft.md`: Store 掲載文案
- `docs/release-notes-draft.md`: リリースノート草案
- `docs/msix-packaging-guide.md`: MSIX パッケージング手順
- `docs/WindowsAppsStoreリリース手順.md`: Store リリースの日本語手順

### 3-4. scripts 配下

- `scripts/Test-StoreReadiness.ps1`: Store readiness チェック
- `scripts/New-LocalMsixPackage.ps1`: ローカル確認用 MSIX 生成
- `scripts/Invoke-AiStatusSmoke.ps1`: スロットへ入力して AI 状態遷移を確認

## 4. プロジェクト別責務

### 4-1. src/TurtleAIQuartetHub.Panel

本体 WPF アプリ。主要ファイルは次のとおり。

- `App.xaml`, `App.xaml.cs`: アプリ起動、単一起動調停の起点
- `MainWindow.xaml`: 標準表示 / 縮小表示 UI、状態ピル、カード、アニメーション
- `MainWindow.xaml.cs`: UI イベント、起動、配置、集中表示、前面/背面、裏保存、オーバーレイ更新
- `Models/`:
  - `WindowSlot.cs`: A-D スロットの状態モデル
  - `AiStatus.cs`: AI 状態 enum
  - `AppConfig.cs`: 設定読み込みと正規化
  - `SavedPanelStateDocument.cs`, `SavedSlotState.cs`, `StoredPanelSlot.cs`: 永続化モデル
  - `VscodeLayoutPreference.cs`: サイドバー等のレイアウト保持
- `Services/`:
  - `StatusStore.cs`: スロット状態、AI 状態反映、保存復元、裏保存管理
  - `VscodeLauncher.cs`: VS Code 起動、既知 HWND 差分検出、ゾンビ除去、専用 user-data 準備
  - `WindowEnumerator.cs`: VS Code ウィンドウ列挙
  - `WindowArranger.cs`: Win32 ベースの配置、最小化、復元、前面/背面、オーバーレイ位置制御
  - `WindowFrameOverlayManager.cs`: VS Code 外周の枠オーバーレイ描画
  - `AiStatusDetector.cs`: ログ + UI Automation による AI 状態判定
  - `VscodeChatUiStatusReader.cs`: VS Code UI Automation から Running / Confirmation シグナル抽出
  - `VscodeWorkspaceState.cs`: workspaceStorage とウィンドウタイトルからワークスペース推定
  - `VscodeLayoutState.cs`: VS Code storage.json からレイアウト保存/復元
  - `SlotUserDataPaths.cs`: スロット別 user-data-dir の解決と準備
  - `TaskbarJumpListService.cs`: Jump List 更新
  - `SingleInstanceCoordinator.cs`: 単一起動制御
  - `DiagnosticLog.cs`: ローカル診断ログ

### 4-2. src/TurtleAIQuartetHub.Package

- `TurtleAIQuartetHub.Package.wapproj`: MSIX 用パッケージプロジェクト
- `Package.appxmanifest`: アプリマニフェスト
- `Assets/`: パッケージ用画像資産

### 4-3. tools/AiStatusSmoke

- `Program.cs`: `slots.json` と現在の VS Code ウィンドウを突き合わせ、AiStatusDetector の結果を CLI で確認する診断ツール

## 5. 実行時データと設定

### 5-1. 設定ファイル

- 既定の example: `config/turtle-ai-quartet-hub.example.json`
- 探索順:
  1. `%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json`
  2. ワークスペース親階層を遡った `config/turtle-ai-quartet-hub.json`
  3. ワークスペース親階層を遡った `config/turtle-ai-quartet-hub.example.json`
  4. アプリ既定値

主な項目:

- `codeCommand`
- `monitor`
- `gap`
- `useDedicatedUserDataDirs`
- `inheritMainUserState`
- `reopenLastWorkspace`
- `launchTimeoutSeconds`
- `remoteReconnectTimeoutSeconds`
- `statusRefreshIntervalMilliseconds`
- `slots[A-D]`

### 5-2. 実行時状態

- `%LOCALAPPDATA%/TurtleAIQuartetHub/slots.json`: visible slot と stored panel の保存状態
- `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data/{A|B|C|D}/...`: スロット別 VS Code user-data-dir
- 各 user-data-dir の `User/workspaceStorage`: ワークスペース推定に使用
- 各 user-data-dir の `User/globalStorage/storage.json`: レイアウト保持に使用
- 各 user-data-dir の `logs/`: Codex / Copilot 系ログ読取元

## 6. 主要機能メモ

- 4 スロット管理: A-D 固定
- 裏保存 Quartet: 1 ページ 4 件 x 3 ページ = 最大 12 件
- 標準表示と縮小表示を切替可能
- 集中表示では 1 スロットを最大化し、カード側でも focused 状態を保持
- 非表示モードでは VS Code ウィンドウを隠すが、管理状態や AI 状態監視は継続する設計
- AI 状態は `Idle`, `Running`, `Completed`, `Error`, `NeedsAttention`, `WaitingForConfirmation` を扱う

## 7. 実行・確認コマンド

- ビルド:
  - `dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- 開発実行:
  - `dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- AI 状態 CLI 確認:
  - `dotnet run --project .\tools\AiStatusSmoke\AiStatusSmoke.csproj -- --json`
- Store readiness:
  - `.\scripts\Test-StoreReadiness.ps1`
- ローカル MSIX:
  - `.\scripts\New-LocalMsixPackage.ps1`

## 8. この overview を使うときの注意

- `.github/skills/` と `.agents/skills/` は mirrored 構成になっている。overview も両方へ同内容を置くこと。
- `bin/`, `obj/`, `dist/` は生成物を含むため、通常の実装修正対象から除外して考える。
- AI 状態検出は UI Automation とログヒューリスティックに依存するため、VS Code / 拡張更新で壊れやすい。変更時は必ずスモーク確認を行う。
