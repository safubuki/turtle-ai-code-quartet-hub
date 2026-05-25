# 複数アプリケーション起動対応 仕様書

更新日: 2026-05-24

## 概要

Turtle AI Code Quartet Hub は、A-D の 4 スロットそれぞれで起動対象アプリを選べるランチャーです。既定は VS Code ですが、Google Antigravity を workspace IDE として、Codex / GitHub Copilot / Gemini / Claude を workspace CLI として起動できます。

Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は CLI とは別扱いです。これらはワークスペース単位の 2x2 管理対象ではなく、補助アプリボタンとして控え Quartet と同じ行の右端に表示します。

## アプリ種別

### WorkspaceIde

1 ウィンドウ 1 ワークスペースで使う IDE です。

対象:
- VS Code
- Google Antigravity IDE

挙動:
- A-D スロットに割り当て可能。
- `Launch Quartet（一括起動）` の対象。
- 保存済みワークスペースパスを起動引数に渡す。
- 起動したウィンドウを A-D の象限へ 2x2 配置する。
- VS Code は専用 `VscodeLauncher` で、既存の user-data-dir、remote URI、workspaceStorage 読み取りを維持する。
- Antigravity は汎用 `ApplicationLauncher` で起動し、起動プロセス ID ではなく新規ウィンドウハンドルで割り当てる。
- Antigravity の既定検出は `%LOCALAPPDATA%\Programs\Antigravity IDE\Antigravity IDE.exe` 相当を優先し、Windows 10 / Windows 11 のユーザープロファイル差に依存しない。

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
- ChatGPT Windows アプリ (`chatgpt-app`)
- Claude Windows アプリ (`claude-app`)
- Antigravity2 Windows アプリ (`antigravity-app`)
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
- `IDE` と `CLI` のボタン高さ、隙間、上端位置は同じ基準でそろえる。
- 選択中アプリは暗めの緑で表示し、ベタ塗りの強いアクセント色にはしない。
- 未検出アプリはグレーアウトし、ツールチップで理由を表示する。
- 未起動スロットでアプリ選択ボタンを押しても、そのアプリを起動しない。選択状態だけを保存し、個別スロットの起動ボタンまたは `Launch Quartet（一括起動）` で起動する。
- 起動中スロットで別の IDE/CLI ボタンを押すと、現在のウィンドウを閉じてから押したアプリを同じスロット位置へ起動する。
- スロット右上のゴミ箱アイコンを押すと削除確認ダイアログを表示する。`削除する` で visible slot の保存済みタイトル、パス、選択アプリ、ウィンドウ割り当てを削除する。起動中ウィンドウは閉じずに管理対象から外す。
- 実行中スロットのアクションボタンは `閉じる` と表示する。
- タイトルバーの右上ボタンは、縮小表示、`?` ヘルプ、設定、最小化、閉じるの順に並べる。ヘルプは枠付きセクションで CLI インストールコマンド、IDE / Windows アプリは公式サイト参照、承認確認を減らす起動オプション例と注意書きを表示する。Claude Code は公式インストーラの curl コマンドと npm コマンドの両方を表示する。本文とコマンドは選択コピーできるようにする。
- 補助アプリボタンは `Windows` ラベル付きで、控え Quartet と同じ行の右端に表示する。表示順は ChatGPT / Codex / Claude / Antigravity2 とし、Antigravity2 は Claude の右側に置く。
- 標準表示ではスロット領域をカード実寸に詰め、控え Quartet までの黒い余白を作らない。下部の `Launch Quartet` ボタンも見切れないようにする。

### 縮小表示

- A-D のスロット操作を優先する。
- 初期表示時から C/D のスロット行と `Windows` 補助アプリボタンが見える高さを確保する。
- アプリ切り替えは標準表示のスロットカードで行う。
- AI 状態に由来する色変更、点滅、状態ピルは表示しない。

### 集中表示

- スロットボタンを押すと対象ウィンドウを全画面相当で前面表示する。
- 同じスロットボタンを押すたびに、対象ウィンドウの 1 面フォーカス表示と 4 面表示を切り替える。
- このトグルは VS Code / Antigravity / CLI のいずれでも同じ挙動にする。
- スロットカードまたは控えカードをドラッグアンドドロップ中は、このトグルを一時抑止する。

## 起動と配置

- `launchTimeoutSeconds` の既定値は 20 秒。起動待ちはウィンドウ検出時に早期終了しつつ、標準的またはやや低性能な PC の起動遅延を吸収する。設定値は 3-60 秒に丸める。
- `remoteReconnectTimeoutSeconds` の既定値は 5 秒。設定値は 1 秒から `launchTimeoutSeconds` または 8 秒の小さい方までに丸める。
- Antigravity や terminal は起動直後に自分で中央へ戻ることがあるため、起動確認後に即時配置し、さらに 1.5 秒、3 秒、5 秒、8 秒、12 秒後にも再配置する。
- 一括起動では非 CLI グループを並列起動し、CLI グループは terminal 系プロセス名の捕捉競合を避けるため順次起動する。
- Workspace CLI は対象スロットごとの terminal を先に開き、`Turtle {slot} - {shortName}` のタイトルで後から捕捉する。タイトルが反映されない場合は、新規 terminal ウィンドウを起動順で割り当てる。
- Workspace CLI の検出は PATH に加えて npm / pnpm / Volta の一般的な shim 置き場、Claude Code 公式インストーラが使うユーザー単位の `~\.local\bin` も探索する。
- Workspace CLI 起動時は、検出済みコマンドの実体パスを短いコマンド名より優先する。また起動した `cmd.exe` の PATH 先頭に検出済みコマンドのフォルダと一般的な shim 置き場を一時追加する。
- VS Code は起動プロセス ID と実際のウィンドウプロセス ID がずれる場合があるため、新規 VS Code ウィンドウの HWND を優先して捕捉する。
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
    },
    {
      "id": "chatgpt-app",
      "displayName": "ChatGPT",
      "shortName": "ChatGPT",
      "kind": "SingleWindowAgent",
      "command": "",
      "arguments": [],
      "supportsMultipleWindows": false,
      "detection": {
        "commands": [],
        "processNames": ["ChatGPT", "OpenAI ChatGPT"],
        "startMenuNames": ["ChatGPT", "OpenAI ChatGPT"],
        "appPathNames": ["ChatGPT.exe", "OpenAI ChatGPT.exe"]
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
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版ボタンが `Windows` ラベル付きで補助ボタン行に表示され、Antigravity2 が Claude の右側に表示される。
- 未検出アプリは起動できず、理由が表示される。
- 右上のゴミ箱アイコンから visible slot の保存情報をクリアできる。
- 右上ボタンが縮小表示、ヘルプ、設定、最小化、閉じるの順で表示される。
- `Launch Quartet` ボタンが標準表示の下部で見切れない。
- 集中表示中に他アプリが上に重なった場合、同じスロットボタンで集中表示を維持したまま前面復帰する。
- ドラッグアンドドロップでカードを移動中に、マウスカーソルがスロット上へ移動しても集中表示へ切り替わらない。
- `.\scripts\Build-Panel.ps1` と `.\scripts\Test-StoreReadiness.ps1` が成功する。
