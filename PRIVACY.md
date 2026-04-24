# プライバシーポリシー草案

Turtle AI Code Quartet Hub は、最大 4 つの Visual Studio Code ウィンドウを配置・監視するためのローカル Windows デスクトップユーティリティです。

この文書は、公開前レビューと Microsoft Store 公開準備のためのリポジトリ内草案です。公開時には、連絡先、公開者名、サポート URL、正式な公開日を実際の情報に置き換えてください。

## 参照する情報

このアプリは、ユーザーの PC 上にあるローカル情報のみを参照します。

- Win32 API で取得できる VS Code ウィンドウのハンドル、タイトル、プロセス ID、位置とサイズ。
- AI 状態推定に必要な VS Code の UI Automation 情報。例: 表示中の状態テキスト、ボタン状態。
- スロット別 VS Code user-data-dir にあるローカル拡張ログの直近行。現在は Codex と GitHub Copilot Chat のログを対象にします。
- ワークスペースパス、remote workspace URI、パネルタイトル、スロット割り当て、控えパネル、レイアウト設定。
- `inheritMainUserState` が有効な場合に、スロット別 user-data-dir へコピーされる軽量な VS Code ユーザー状態。例: 設定、スニペット、prompts、一部の globalStorage。

## 保存する情報

アプリの実行時データは、既定で次のローカルフォルダに保存します。

```text
%LOCALAPPDATA%\TurtleAIQuartetHub\
```

保存される可能性がある情報は次のとおりです。

- `slots.json` とパネル保存状態。
- スロット別 VS Code user-data-dir。
- 最後に確認できたワークスペースパスまたは remote workspace URI。
- 例外や遅い状態検出を記録するアプリ診断ログ。
- 任意のユーザー設定ファイル `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json`。

## ネットワーク利用

Turtle AI Code Quartet Hub は、アプリ独自のテレメトリ、ワークスペース情報、プロンプト、ソースコード、VS Code ログ、利用状況分析を公開者へ送信しません。

このアプリは、ローカルパスまたは remote workspace URI を指定して VS Code を起動する場合があります。VS Code、VS Code 拡張機能、SSH、GitHub Copilot、Codex、その他のツールが行うネットワーク通信は、それぞれの製品・サービスの仕様とプライバシーポリシーに従います。

## 第三者提供

このアプリは、ユーザーデータを販売、共有、アップロード、第三者提供しません。

## ユーザーによる管理

アプリの実行時データを削除するには、次のフォルダを削除してください。

```text
%LOCALAPPDATA%\TurtleAIQuartetHub\
```

軽量な VS Code ユーザー状態のコピーを無効にするには、設定ファイルで次のように指定します。

```json
{
  "inheritMainUserState": false
}
```

## 連絡先

正式公開前に、この項目を公開者のサポート連絡先またはサポート URL に置き換えてください。

