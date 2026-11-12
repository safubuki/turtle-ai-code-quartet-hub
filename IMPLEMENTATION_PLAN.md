# Turtle AI Code Quartet Hub 実装計画書

更新日: 2026-05-13

## 1. 目的

Windows デスクトップ上で複数の VS Code ウィンドウを扱いやすくする小型パネルアプリを作る。

主な目的は次の通り。

- ワンクリックで VS Code を 4 つ起動する。
- 4 つの VS Code ウィンドウを 2x2 の square 配置で並べる。
- パネル上のスロットから対応する VS Code ウィンドウを前面化する。
- 集中表示、非表示/再表示、控え Quartet、タスクバー Jump List を使ってウィンドウ管理を軽くする。
- AI 実行状態の推定や拡張機能ごとの監視に依存しない、安定した操作体験に集中する。

## 2. 現行方針

画面やログから AI 状態を「推定」する機能は削除する。

削除対象:

- VS Code ウィンドウ外周を囲むフレーム表示。
- VS Code UI Automation 走査、拡張ログ解析、AI 状態推定。
- AI 状態検証用の smoke tool と関連ドキュメント。

方針変更（2026-11-11、許可の確認は 2026-11-12 に見直し）: AI の作業中・完了は、Claude Code / Codex / GitHub Copilot / Grok の
フック機能が送る明示的な通知だけを使って、スロットのバッジに表示する（作業中は枠がゆっくり緑に明滅、
完了だけが薄い青でぼんやり光り、パネルでフォーカス表示を切り替えると消える。許可や質問の確認中は緑の明滅のまま）。推定はしないため、以前の不安定さは持ち込まない。色はツールごとに変えない。

残す対象:

- スロット選択中の枠表示。
- VS Code 起動、配置、前面化、背面化、集中表示。
- ウィンドウタイトル、存在状態、ワークスペース名の表示。
- パネル表示モード、保存復元、Jump List。

## 3. 技術スタック

- C# / .NET 10
- WPF
- Win32 API P/Invoke
- System.Text.Json
- PowerShell 補助スクリプト

## 4. アーキテクチャ

```text
+----------------------------------+
| Turtle AI Code Quartet Hub Panel |
| WPF / .NET                       |
|                                  |
| - Launch 4 VS Code               |
| - Arrange 2x2 windows            |
| - Focus selected window          |
| - Store/restore panel state      |
| - Update jump list               |
+----------------+-----------------+
                 |
                 | Win32 API
                 v
+--------------------------+
| Windows Desktop          |
| - HWND enumeration       |
| - monitor work area      |
| - window activation      |
+------------+-------------+
             |
             | title / hwnd / workspace state only
             v
+--------------------------+
| VS Code windows x4       |
+--------------------------+
```

## 5. 実装ルール

- AI 状態は AI ツールのフックからの明示的な通知（`--ai-event`）だけで決める。検出・推定サービスは追加しない。
- VS Code のチャット UI、拡張ログ、OpenTelemetry、ターミナル出力を AI 状態推定目的で読まない。
- AI 状態の表現はスロット名バッジ（極小表示はカード）の枠のゆっくりした緑の明滅と、完了時だけの薄い青のぼんやりした光に限る。許可の確認中は緑のまま。速い点滅や、カード全体の色替え・状態ピルは使わない。ツールごとに色や点滅を変えない。
- AI の通知でパネルを前面に出さない。
- 選択中/フォーカス中など操作上必要な視覚フィードバックは維持する。
- 定期更新は HWND、タイトル、ワークスペース表示の更新に限定する。

## 6. 主要ファイル

```text
turtle-ai-quartet-hub/
  IMPLEMENTATION_PLAN.md
  README.md
  src/
    TurtleAIQuartetHub.Panel/
      TurtleAIQuartetHub.Panel.csproj
      App.xaml
      MainWindow.xaml
      MainWindow.xaml.cs
      Models/
        AppConfig.cs
        SlotConfig.cs
        WindowSlot.cs
      Services/
        StatusStore.cs
        TaskbarJumpListService.cs
        VscodeLauncher.cs
        VscodeLayoutState.cs
        VscodeWorkspaceState.cs
        WindowArranger.cs
        WindowEnumerator.cs
  config/
    turtle-ai-quartet-hub.example.json
  docs/
    index.md
    guides/
    design/
    development/
    investigations/
    release/
      store-readiness.md
      release-notes-draft.md
      store-listing-draft.md
    assets/
```

## 7. QA 方針

- `dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj --artifacts-path <temp>` でビルドする。
- `.\scripts\Test-StoreReadiness.ps1` でストア向けの基本ファイルと設定を確認する。
- `rg` で `AiStatus`、AI 状態検出（推定）サービス、外周オーバーレイ、smoke tool の参照が戻っていないことを確認する。
- UI 確認では、スロット選択枠が残り、AI 状態ピルやカード全体の色変えが出ないこと、AI の作業通知がバッジの明滅・光だけで表れることを見る。

## 8. 今後の改善候補

- 起動/停止ボタンの応答性改善。
- 縮小表示時の操作密度と視認性の調整。
- ウィンドウ復元時の待ち時間とリトライの最適化。
- 複数モニタ/DPI 環境での配置確認強化。
- 保存済み Quartet の名前付けと切り替え UX 改善。

## 9. 参考資料

- VS Code Command Line Interface: https://code.visualstudio.com/docs/configure/command-line
- VS Code API Reference: https://code.visualstudio.com/api/references/vscode-api
- Microsoft .NET lifecycle: https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core
