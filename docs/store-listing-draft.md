# Store 掲載文案

Microsoft Store 申請前の掲載文案です。提出前に、ポリシー、商標表現、スクリーンショット、サポートURL、プライバシーポリシーURLを確認してください。

## アプリ名

Turtle AI Code Quartet Hub

## 短い説明

4つの VS Code ウィンドウを小さな Windows パネルから起動・配置・切替できます。

## 長い説明

Turtle AI Code Quartet Hub は、複数の Visual Studio Code セッションを扱う開発者向けのローカル Windows ユーティリティです。

最大4つの VS Code ウィンドウを起動して2x2に配置し、スロットごとのフォーカス切替、タイトル管理、ワークスペース状態の保存、控え Quartet への退避と復帰を行えます。縮小モードでは、常に最前面の小さなパネルから各スロットへ素早くアクセスできます。

AIの実行状態を推定する機能、VS Code外周フレーム、状態連動の点滅表示は搭載していません。VS Code や拡張機能の仕様変更に左右されにくい、軽量なウィンドウ管理に集中しています。

Visual Studio Code は別途インストールが必要です。Remote workspace や AI サービス、VS Code 拡張機能は、それぞれのツール・サービスにより提供されます。

## 主な機能

- 最大4つの VS Code ウィンドウを起動・配置。
- A-D スロット、パネルタイトル、ワークスペースパス、控えパネルを保持。
- 小さな常時最前面パネルとして使える縮小モード。
- ディスプレイ移動、最前面/最背面、非表示/再表示をまとめて操作。
- 複数セッションを分けやすいスロット別 VS Code user-data-dir。

## 依存関係

- Windows。
- Visual Studio Code。
- VS Code コマンドラインランチャー `code`、または `Code.exe` への設定済みパス。
- 自己完結版では .NET Desktop Runtime の別途インストールは不要。開発環境では .NET SDK が必要。

## プライバシー要約

このアプリは、VS Code ウィンドウの起動・配置・保存復元に必要なローカル情報を扱います。設定と状態は `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。アプリ独自のテレメトリ、プロンプト、ソースコード、ワークスペース情報、ログを公開者へ送信しません。

プライバシーポリシー草案: [PRIVACY.md](../PRIVACY.md)

## サポート注記

- このアプリは Microsoft、Visual Studio Code、GitHub、OpenAI、Anthropic の公式アプリ、提携アプリ、承認済みアプリではありません。
- VS Code が未インストール、または `code` コマンドが使えない場合は、`codeCommand` に `Code.exe` のパスを設定してください。
- Remote SSH や AI サービスの挙動は、VS Code とインストール済み拡張機能側で管理されます。

サポート案内草案: [SUPPORT.md](../SUPPORT.md)
リリースノート草案: [release-notes-draft.md](release-notes-draft.md)

## 画像素材メモ

公開用スクリーンショットは [assets/store/README.md](../assets/store/README.md) のチェックに沿って用意してください。
最低1枚、推奨4枚以上です。個人情報、ローカルパス、未公開コード、APIキー、社内情報が写らないダミーワークスペースで撮影します。

パッケージ用ロゴ画像は `src/TurtleAIQuartetHub.Package/Assets/` に配置済みです。Store のスクリーンショットとは別物として扱ってください。
