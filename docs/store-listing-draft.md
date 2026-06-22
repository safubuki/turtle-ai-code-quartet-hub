# Store 掲載文案

Microsoft Store 申請前の掲載文案です。提出前に、ポリシー、商標表現、スクリーンショット、サポートURL、プライバシーポリシーURLを確認してください。

## アプリ名

Turtle AI Code Quartet Hub

## 短い説明

4つの開発ワークスペースを A-D スロットとして一括起動し、IDE と AI CLI をすばやく切り替えられる Windows ランチャーです。

## 長い説明

Turtle AI Code Quartet Hub は、複数の案件・複数の AI エージェント・複数のターミナルを行き来する開発者向けのローカル Windows ランチャーです。

4つの開発ワークスペースを A-D のスロットとしてまとめて起動し、画面上に 2x2 で整列表示します。各スロットには VS Code / Google Antigravity などの IDE か、Codex / GitHub Copilot / Gemini / Grok / Claude などの AI CLI を割り当てられ、同じワークスペースを IDE と CLI のどちらでもすばやく開き直せます。起動中スロットで別のアプリを選ぶと、同じ象限でウィンドウを差し替えます。

スロットのタイトル、ワークスペースパス、選択アプリ、控え Quartet を保存し、スロットボタンで1面フォーカス表示と4面表示を切り替えられます。縮小モードでは、常に最前面の小さな操作バーから各スロット操作と Windows 補助アプリの起動をすぐに行えます。

VS Code、Antigravity、各 AI CLI、Codex / ChatGPT / Claude の Windows アプリ版は、いずれもこのアプリには同梱されず、使う場合はそれぞれ別途インストールが必要です。Remote workspace や AI サービス、VS Code 拡張機能は、それぞれのツール・サービスにより提供されます。

## 主な機能

- 4つの開発ワークスペースを A-D スロットとして一括起動し、2x2 に配置。
- 各スロットで IDE（VS Code / Antigravity）と AI CLI（Codex / GitHub Copilot / Gemini / Grok / Claude）を選択。
- 起動中スロットで別アプリを押すと、同じ象限でウィンドウを差し替え。
- スロット名、ワークスペースパス、選択アプリ、控え Quartet をローカル保存。
- スロットボタンで1面フォーカス表示と4面表示を切り替え。
- 小さな常時最前面の操作バーとして使える縮小モード。
- Codex / ChatGPT / Claude などの Windows アプリ版を補助ボタンから起動。
- Jump List からスロット切替・表示モード切替・前面/背面操作。
- 複数セッションを分けやすいスロット別 VS Code user-data-dir。

## 依存関係

- Windows 10 / Windows 11。
- 起動対象のアプリ（このアプリには同梱されません）:
  - VS Code（既定のスロット起動対象。`code` コマンド、または `Code.exe` への設定済みパスが必要）。
  - 任意で Google Antigravity、Codex CLI、GitHub Copilot CLI、Gemini CLI、Grok CLI、Claude CLI。
  - 任意で Codex / ChatGPT / Claude の Windows アプリ版。
- 自己完結版では .NET Desktop Runtime の別途インストールは不要。開発環境では .NET SDK が必要。

## プライバシー要約

このアプリは、管理対象ウィンドウの起動・配置・保存復元に必要なローカル情報を扱います。設定と状態は `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。アプリ独自のテレメトリ、プロンプト、ソースコード、ワークスペース情報、ログを公開者へ送信しません。

プライバシーポリシー草案: [PRIVACY.md](../PRIVACY.md)

## サポート注記

- このアプリは Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic、Google、xAI の公式アプリ、提携アプリ、承認済みアプリではありません。
- VS Code が未インストール、または `code` コマンドが使えない場合は、`codeCommand` に `Code.exe` のパスを設定してください。
- Antigravity、各 AI CLI、Codex / ChatGPT / Claude の Windows アプリ版を使う場合は、それぞれ別途インストールが必要です。
- Remote SSH や AI サービスの挙動は、VS Code とインストール済み拡張機能、各ツール側で管理されます。

サポート案内草案: [SUPPORT.md](../SUPPORT.md)
リリースノート草案: [release-notes-draft.md](release-notes-draft.md)

## 画像素材メモ

公開用スクリーンショットは [assets/store/README.md](../assets/store/README.md) のチェックに沿って用意してください。
最低1枚、推奨4枚以上です。個人情報、ローカルパス、未公開コード、APIキー、社内情報が写らないダミーワークスペースで撮影します。

パッケージ用ロゴ画像は `src/TurtleAIQuartetHub.Package/Assets/` に配置済みです。Store のスクリーンショットとは別物として扱ってください。
