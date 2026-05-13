# 複数アプリケーション起動対応 仕様書

更新日: 2026-05-13

## 概要

Turtle AI Code Quartet Hub は、A-D の 4 スロットそれぞれで起動対象アプリを選べるランチャーです。既定は VS Code ですが、Google Antigravity を workspace IDE として、Codex / GitHub Copilot / Gemini / Claude を workspace CLI として起動できます。

Codex / Claude の Windows アプリ版は CLI とは別扱いです。これらはワークスペース単位の 2x2 管理対象ではなく、補助アプリボタンとして控え Quartet と同じ行の右端に表示します。

## アプリ種別

### WorkspaceIde

1 ウィンドウ 1 ワークスペースで使う IDE です。

対象:
- VS Code
- Google Antigravity

挙動:
- A-D スロットに割り当て可能。
- `Launch Quartet（一括起動）` の対象。
- 保存済みワークスペースパスを起動引数に渡す。
- 起動したウィンドウを A-D の象限へ 2x2 配置する。
- VS Code は専用 `VscodeLauncher` で、既存の user-data-dir、remote URI、workspaceStorage 読み取りを維持する。
- Antigravity は汎用 `ApplicationLauncher` で起動し、起動プロセス ID ではなく新規ウィンドウハンドルで割り当てる。

### WorkspaceCli

対象ワークスペースをカレントディレクトリにして起動する CLI です。

対象:
- Codex CLI
- GitHub Copilot CLI
- Gemini CLI
- Claude CLI

挙動:
- A-D スロットに割り当て可能。
- `Launch Quartet（一括起動）` の対象。
- `cmd.exe /k` を開き、保存済みワークスペースへ `cd /d` してから CLI コマンドを実行する。
- 既定の GitHub Copilot CLI は `copilot` だけを実行する。ワークスペースパスを暗黙の引数として渡さない。
- `arguments` が明示されている場合だけ追加引数を渡す。
- 新規 terminal ウィンドウを検出し、A-D の象限へ配置する。
- CLI の可用性は PATH 上のコマンド検出で判断し、Windows アプリや既存 terminal プロセスの検出ではインストール済み扱いにしない。

### SingleWindowAgent

ワークスペース単位ではなく単体で開く補助アプリです。

対象:
- Codex Windows アプリ (`codex-app`)
- Claude Windows アプリ (`claude-app`)
- ユーザー設定で追加された単一ウィンドウ型アプリ

挙動:
- 補助アプリボタンから起動する。
- 既定では A-D の 2x2 配置対象にしない。
- 既存ウィンドウの状態監視や AI 状態監視は行わない。
- WindowsApps、App Paths、スタートメニュー、実行中プロセスなどから検出する。

## 画面仕様

### 標準表示

- 各スロットカード内に workspace app / CLI 選択ボタンを並べる。
- スロット内の選択ボタンは `IDE` 枠と `CLI` 枠に分ける。
- `IDE` 枠は VS Code / Antigravity を縦に並べる。
- `CLI` 枠は Codex CLI / Claude CLI / Gemini CLI / Copilot CLI を同じ枠内にまとめる。
- 選択中アプリは暗めの緑で表示し、ベタ塗りの強いアクセント色にはしない。
- 未検出アプリはグレーアウトし、ツールチップで理由を表示する。
- 未起動スロットに保存済みワークスペースがある場合、アプリ選択ボタンを押すとそのアプリで即時起動する。
- 起動中スロットで別の IDE/CLI ボタンを押すと、現在のウィンドウを閉じてから押したアプリを同じスロット位置へ起動する。
- スロット右上のゴミ箱アイコンで、visible slot の保存済みタイトル、パス、ウィンドウ割り当てを削除できる。起動中ウィンドウは閉じずに管理対象から外す。
- 補助アプリボタンは `Windows` ラベル付きで、控え Quartet と同じ行の右端に表示する。
- 標準表示ではスロット領域をスクロール可能にし、下部の `Launch Quartet` ボタンが見切れないようにする。

### 縮小表示

- A-D のスロット操作を優先する。
- アプリ切り替えは標準表示のスロットカードで行う。
- AI 状態に由来する色変更、点滅、状態ピルは表示しない。

### 集中表示

- スロットボタンを押すと対象ウィンドウを全画面相当で前面表示する。
- 集中表示中に同じスロットボタンを押した場合、対象ウィンドウの上に他アプリの可視ウィンドウが重なっていれば 4 面表示へ戻さず、集中表示を維持したまま対象ウィンドウを前面へ戻す。
- 重なりがない場合は、従来通り 4 面表示へ戻す。

## 起動と配置

- `launchTimeoutSeconds` の既定値は 12 秒。起動待ちが長く固まる体験を避けるため、設定値は 3-30 秒に丸める。
- `remoteReconnectTimeoutSeconds` の既定値は 5 秒。設定値は 1 秒から `launchTimeoutSeconds` または 8 秒の小さい方までに丸める。
- Antigravity や terminal は起動直後に自分で中央へ戻ることがあるため、起動確認後に即時配置し、さらに 1.5 秒、3 秒、5 秒後にも再配置する。
- ユーザーが集中表示に入った後は、遅延再配置で 4 面表示へ戻さない。

## 保存と復元

- `slots.json` に visible slot と stored panel の状態を保存する。
- 保存対象はタイトル、ワークスペースパス、アプリ ID、保存済みワークスペース、確認済みフラグ、ウィンドウハンドル、VS Code レイアウト設定。
- VS Code の workspaceStorage 読み取りに失敗しても、保存済みワークスペースパスは消さない。
- 起動確認または periodic refresh で正しいワークスペースが読めたら、`Path`、`SavedWorkspacePath`、`SavedWorkspaceConfirmed`、自動タイトルを保存する。

## 設定例

```json
{
  "defaultWorkspaceApplicationId": "vscode",
  "applications": [
    {
      "id": "github-copilot",
      "displayName": "GitHub Copilot CLI",
      "shortName": "Copilot",
      "kind": "WorkspaceCli",
      "command": "copilot",
      "arguments": [],
      "supportsMultipleWindows": false,
      "detection": {
        "commands": ["copilot"],
        "processNames": ["cmd", "WindowsTerminal", "OpenConsole", "powershell", "pwsh"]
      }
    },
    {
      "id": "codex-app",
      "displayName": "Codex",
      "shortName": "Codex",
      "kind": "SingleWindowAgent",
      "command": "",
      "arguments": [],
      "supportsMultipleWindows": false,
      "detection": {
        "commands": [],
        "processNames": ["Codex"],
        "startMenuNames": ["Codex", "OpenAI Codex"],
        "appPathNames": ["Codex.exe"]
      }
    }
  ]
}
```

## 受け入れ条件

- 既定設定で VS Code 4 面起動が従来通り動く。
- スロットごとに VS Code / Antigravity / Codex CLI / Copilot CLI / Gemini CLI / Claude CLI を選べる。
- IDE/CLI の枠分け、選択中ボタンの暗めの緑表示、未検出アプリのグレーアウトが機能する。
- 別アプリボタンを押したとき、現在のスロットウィンドウを閉じてから選択アプリへ切り替わる。
- GitHub Copilot CLI は対象ワークスペースで `copilot` だけを実行する。
- Antigravity は対象スロットの象限へ配置され、起動直後に中央へ戻っても遅延再配置で戻る。
- Codex / Claude の Windows アプリ版ボタンが `Windows` ラベル付きで補助ボタン行に表示される。
- 未検出アプリは起動できず、理由が表示される。
- 右上のゴミ箱アイコンから visible slot の保存情報をクリアできる。
- `Launch Quartet` ボタンが標準表示の下部で見切れない。
- 集中表示中に他アプリが上に重なった場合、同じスロットボタンで集中表示を維持したまま前面復帰する。
- `.\scripts\Build-Panel.ps1` と `.\scripts\Test-StoreReadiness.ps1` が成功する。
