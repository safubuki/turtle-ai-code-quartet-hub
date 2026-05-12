# Turtle AI Code Quartet Hub プロジェクト詳細

更新日: 2026-05-13

## 概要

- 4つの VS Code ウィンドウを A-D スロットとして管理する Windows 向け WPF アプリ。
- 2x2 配置、集中表示、非表示/再表示、控え Quartet、タスクバー Jump List 操作を提供する。
- AI状態表示、AI状態監視、VS Code外周フレーム、AI状態連動の点滅/色変えは削除済み。

## 技術スタック

- アプリ本体: C#, .NET 10, WPF
- パッケージ: MSIX / Windows Application Packaging Project
- 補助スクリプト: PowerShell
- 主な API: Win32 P/Invoke, System.Text.Json

## 主なファイル

- `TurtleAIQuartetHub.sln`: WPF本体ソリューション。
- `src/TurtleAIQuartetHub.Panel/MainWindow.xaml`: 標準表示/縮小表示 UI。
- `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`: UIイベント、起動、配置、集中表示、前面/背面、控え保存。
- `src/TurtleAIQuartetHub.Panel/Models/WindowSlot.cs`: A-D スロット状態モデル。
- `src/TurtleAIQuartetHub.Panel/Models/AppConfig.cs`: 設定読み込みと正規化。
- `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs`: スロット状態、保存復元、控え保存管理。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs`: VS Code 起動、HWND 割り当て、専用 user-data 準備。
- `src/TurtleAIQuartetHub.Panel/Services/WindowEnumerator.cs`: VS Code ウィンドウ列挙。
- `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`: Win32 ベースの配置、最小化、復元、前面/背面制御。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeWorkspaceState.cs`: workspaceStorage とウィンドウタイトルからワークスペースを推定。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLayoutState.cs`: VS Code storage.json からレイアウト保存/復元。
- `src/TurtleAIQuartetHub.Panel/Services/TaskbarJumpListService.cs`: Jump List 更新。

## 削除済みAI状態関連

- `src/TurtleAIQuartetHub.Panel/Models/AiStatus.cs`
- `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`
- `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`
- `src/TurtleAIQuartetHub.Panel/Services/WindowFrameOverlayManager.cs`
- `tools/AiStatusSmoke`
- `scripts/Invoke-AiStatusSmoke.ps1`
- `docs/telemetry-notes.md`

## 実行時データ

- `%LOCALAPPDATA%/TurtleAIQuartetHub/slots.json`: visible slot と stored panel の保存状態。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data/{A|B|C|D}/...`: スロット別 VS Code user-data-dir。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json`: 任意のユーザー設定。

## 確認コマンド

- ビルド: `dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- 開発実行: `dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- Store readiness: `.\scripts\Test-StoreReadiness.ps1`
- ローカル MSIX: `.\scripts\New-LocalMsixPackage.ps1`
