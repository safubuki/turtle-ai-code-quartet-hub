# Turtle AI Code Quartet Hub 実装パターン・注意点

更新日: 2026-05-13

## 1. AI 状態監視を戻さない
- AI 状態検出、UI Automation のチャット走査、拡張ログ解析、VS Code 外枠オーバーレイは削除済み。
- `StatusStore.RefreshWindowStatusesAsync` は HWND、タイトル、ワークスペース表示更新の軽量処理として維持する。
- AI 状態に由来するカード色、縮小ボタン色、点滅、Jump List 文言を復活させない。

## 2. スロット状態と保存
- `slots.json` は visible slot と stored panel を同じドキュメントで保持する。
- `WindowSlot` は管理対象ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- VS Code の workspaceStorage 読み取りに失敗しても `SavedWorkspacePath` は消さない。強制終了後や中途半端な user-data 状態でも、次回起動に使える保存済みパスを残す。
- 起動確認または periodic refresh で正しいワークスペースが読めた場合は、`Path` / `SavedWorkspacePath` / `SavedWorkspaceConfirmed` と自動タイトルを保存する。

## 3. 複数アプリ起動
- 既定のスロットアプリは VS Code (`vscode`)。一括起動はスロットごとの `ApplicationId` に従う。
- VS Code は `VscodeLauncher` に残し、専用 user-data-dir、remote URI、workspaceStorage 読み取りの既存挙動を壊さない。
- Antigravity など VS Code 以外の workspace IDE は `ApplicationLauncher` の汎用起動で扱う。起動プロセスと表示ウィンドウのプロセス ID がずれるため、新規ウィンドウハンドルで割り当てる。
- Antigravity や terminal が起動完了後に中央へ戻ることがあるため、起動確認直後の配置に加えて短い遅延再配置を複数回行う。ユーザーが集中表示に入った後は再配置しない。
- Codex / GitHub Copilot / Gemini / Claude は `WorkspaceCli`。対象スロットの保存済みワークスペースを working directory にした `cmd.exe /k` で CLI を起動し、terminal 系の新規ウィンドウをスロットに割り当てる。
- Workspace CLI は PATH のコマンド検出のみで可用性を判断する。terminal プロセスが起動済みであることや Windows アプリ検出だけで CLI インストール済み扱いにしない。
- GitHub Copilot CLI の既定起動は、対象ワークスペースへ `cd /d` して `copilot` だけを実行する。`workspacePath` は暗黙の引数として渡さない。
- Codex / Claude の Windows アプリ版は CLI とは別に `codex-app` / `claude-app` の `SingleWindowAgent` として補助ボタン行に残す。
- スロット内で別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウへ close を送り、短く待ってからスロット状態をクリアし、押されたアプリを同じスロット位置へ起動する。

## 4. UI とフォーカス
- UI は各スロット内に `IDE` 枠と `CLI` 枠を持つ。IDE 枠は VS Code / Antigravity を縦並び、CLI 枠は Codex / Claude / Gemini / Copilot をまとめて表示する。
- 選択中アプリのボタンはベタ塗りではなく、暗めの半透明に近い緑で表示する。未検出アプリは既存の `IsAvailable` 判定でグレーアウトする。
- 未起動スロットに保存済みワークスペースがあれば、アプリ選択ボタンでそのまま起動する。
- メインパネルのクリアアイコンは右上のゴミ箱アイコンで表示し、対象スロットの保存済みタイトル、パス、ウィンドウ割り当てを削除する。起動中ウィンドウがあっても閉じずに管理対象から外す。
- Codex / Claude の Windows アプリ版は、補助ボタン行の左に `Windows` ラベルを置いて CLI と区別する。
- 標準表示ではスロット領域をスクロール可能にし、下部の `Launch Quartet` ボタンが見切れないようにする。
- 集中表示中に同じスロットボタンを押したとき、対象ウィンドウの上に他アプリの可視ウィンドウが重なっている場合は 4 面表示へ戻さず、集中表示を維持して前面復帰する。
- 重なりがない場合は従来通り、同じボタンで集中表示と 4 面表示を切り替える。

## 5. ビルド
- 実行中の本体が既定の `bin\Debug` 出力をロックする場合がある。
- 反復確認では `scripts/Build-Panel.ps1` または `--artifacts-path` を使う。
