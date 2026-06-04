# Turtle AI Code Quartet Hub 実装パターン・注意点

更新日: 2026-06-04

## 1. AI 状態監視を戻さない
- AI 状態検出、UI Automation のチャット走査、拡張ログ解析、VS Code 外枠オーバーレイは削除済み。
- `StatusStore.RefreshWindowStatusesAsync` は HWND、タイトル、ワークスペース表示更新の軽量処理として維持する。
- AI 状態に由来するカード色、縮小ボタン色、点滅、Jump List 文言を復活させない。

## 2. スロット状態と保存
- `slots.json` は visible slot と stored panel を同じドキュメントで保持する。
- `WindowSlot` は管理対象ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- パネルカードを A-D 間でドラッグ移動するときは、表示位置の `Name` と VS Code user-data / workspaceStorage / code.lock 用の `RuntimeSlotName` を分けて扱う。実行中ウィンドウを B から D に移した場合、D は B の runtime profile を持ち続け、空いた B は D の runtime profile で新規起動することで、同じ user-data を二重に再利用しない。
- VS Code / Antigravity の workspaceStorage 読み取りに失敗しても `SavedWorkspacePath` は消さない。強制終了後や中途半端な user-data 状態でも、次回起動に使える保存済みパスを残す。
- 起動確認または periodic refresh で正しいワークスペースが読めた場合は、`Path` / `SavedWorkspacePath` / `SavedWorkspaceConfirmed` と自動タイトルを保存する。
- 控え Quartet から表示スロットへ復帰するときは、旧ウィンドウへ close を送っただけで次の起動へ進めない。置換用の close 待ち処理を通し、VS Code の再接続では起動予定の `SavedWorkspacePath` / `Path` とタイトルが一致しない既存 slot-owned window を採用しない。旧 workspace のウィンドウを新しいパネル表示へ誤接続すると、パネル名と実ワークスペースが永続的にずれる。

## 3. 複数アプリ起動
- 既定のスロットアプリは VS Code (`vscode`)。一括起動はスロットごとの `ApplicationId` に従う。
- VS Code は `VscodeLauncher` に残し、専用 user-data-dir、remote URI、workspaceStorage 読み取りの既存挙動を壊さない。
- Antigravity のワークスペース推定は VS Code 互換の `workspaceStorage` 形式を使い、`%APPDATA%/Antigravity/User/workspaceStorage` などの実アプリデータ候補を新しい順に見て、ウィンドウタイトルにワークスペース名が含まれるパスだけを採用する。
- Antigravity など VS Code 以外の workspace IDE は `ApplicationLauncher` の汎用起動で扱う。起動プロセスと表示ウィンドウのプロセス ID がずれるため、新規ウィンドウハンドルで割り当てる。
- Antigravity IDE は `%LOCALAPPDATA%/Programs/Antigravity IDE/Antigravity IDE.exe` 相当を優先検出する。過去設定の bare `antigravity` は新しい Antigravity Windows アプリと衝突しやすいため、既定検出へ戻して IDE 側を開く。
- Antigravity や terminal が起動完了後に中央へ戻ることがあるため、起動確認直後の配置に加えて短い遅延再配置を複数回行う。標準またはやや低性能な PC に備えて 8 秒、12 秒後の再配置も行い、ユーザーが集中表示に入った後は再配置しない。
- Codex / Claude / GitHub Copilot / Grok Build / Gemini は `WorkspaceCli`。対象スロットの保存済みワークスペースを working directory にした `cmd.exe /k` で CLI を起動し、terminal 系の新規ウィンドウをスロットに割り当てる。
- 一括起動では CLI 種別同士を並列捕捉しない。VS Code / Antigravity などの非 CLI は並列のまま、CLI グループだけ順次起動して terminal 系プロセス名の競合を避ける。
- Workspace CLI は同一 CLI 種別の複数スロットでも、まず各 `cmd.exe` を先に起動し、`Turtle {slot} - {shortName}` のタイトルで後から捕捉する。タイトルが Windows Terminal 側で反映されない場合は、新規 terminal ウィンドウを起動順で割り当てる。
- Workspace CLI はコマンド検出で可用性を判断する。PATH に加えて npm / pnpm / Volta の一般的な shim 置き場、Claude Code の公式インストーラが使うユーザー単位の `~\.local\bin`、Grok Build の Git Bash インストーラが使う `~\.grok\bin` も探索する。terminal プロセスが起動済みであることや Windows アプリ検出だけで CLI インストール済み扱いにしない。
- GitHub Copilot Chat 拡張が作る `globalStorage\github.copilot-chat\copilotCli\copilot*` は、実体が未インストール時のインストール案内ブートストラップなので、GitHub Copilot CLI インストール済み判定には使わない。
- Workspace CLI の実行コマンドは `ResolvedCommand` を優先し、短い `claude` などの設定コマンドだけに依存しない。起動した `cmd.exe` には検出済みコマンドのフォルダと一般的な shim 置き場を PATH 先頭へ一時追加する。
- GitHub Copilot CLI の既定起動は、対象ワークスペースへ `cd /d` して `copilot` だけを実行する。`workspacePath` は暗黙の引数として渡さない。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は CLI とは別に `codex-app` / `chatgpt-app` / `claude-app` / `antigravity-app` の `SingleWindowAgent` として補助ボタン行に残す。Antigravity2 は `%LOCALAPPDATA%/Programs/Antigravity/Antigravity.exe` 相当を優先検出し、Claude の右側に表示する。
- スロット内で別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウへ close を送り、短く待ってからスロット状態をクリアし、押されたアプリを同じスロット位置へ起動する。

## 4. UI とフォーカス
- UI は各スロット内に `IDE` 枠と `CLI` 枠を持つ。IDE 枠は VS Code / Antigravity を縦並び、CLI 枠は上段 `Codex` / `Claude`、下段 `Copilot` / `Grok` / `Gemini` で表示する。
- 選択中アプリのボタンはベタ塗りではなく、暗めの半透明に近い緑で表示する。ただし未検出アプリは選択中でも `IsAvailable=false` のグレーアウト表示を優先し、インストール済みと誤認させない。
- 未起動スロットの IDE / CLI 選択ボタンは起動対象を変更するだけで、アプリを自動起動しない。起動は個別スロットの起動ボタンか `Launch Quartet` のみで行う。
- メインパネルのクリアアイコンは右上のゴミ箱アイコンで表示する。押下時は削除確認ダイアログを出し、確認後に対象スロットの保存済みタイトル、パス、選択アプリ、ウィンドウ割り当てを削除する。起動中の IDE / CLI ウィンドウがある場合は close を送ってからパネル情報を削除する。
- 通常表示のスロット左下にはフォルダアイコンボタンを置き、`WindowSlot.DisplayPath` から Explorer で開けるローカルフォルダを解決する。既存ディレクトリはそのまま、`.code-workspace` など既存ファイルは親フォルダを開く。`vscode-remote://...` や `ssh://...` など non-file URI は Explorer 対象にせず、ボタンをグレーアウトする。
- スロットの実行中アクションボタン文言は `閉じる` にする。未起動時は `起動` / `新規`。
- タイトルバー右上は縮小表示、`?` ヘルプ、歯車設定、最小化、閉じるの順に置く。ヘルプには枠付きセクションで CLI インストールコマンド、IDE / Windows アプリは公式サイト参照、承認確認を減らす起動オプション例と注意書きを表示する。Claude Code は公式インストーラの PowerShell / CMD コマンドと npm コマンドを書く。本文とコマンドは選択コピーできるよう `TextBox IsReadOnly=True` で表示する。
- 歯車設定画面では IDE / CLI / Windows アプリの起動コマンドを編集し、`%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json` へ保存する。VS Code の設定は `CodeCommand` と `applications[].command` を同期させる。
- 設定画面には表の Quartet と控え Quartet の保存状態を一覧表示する。表は `PanelTitle` / `Path` / `SavedWorkspacePath` / `SavedWorkspaceConfirmed` / `ApplicationId` を編集可能にし、控えは `PanelTitle` / `WorkspacePath` / `ApplicationId` を編集可能にする。空化ボタンと不整合修復ボタンを用意し、過去の重複控えや不完全な控えで再登録できない状態を解消できるようにする。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は、補助ボタン行の左に `Windows` ラベルを置いて CLI と区別する。Antigravity2 の文言が収まる固定幅にそろえ、縮小表示でも行全体が隠れない幅を確保する。
- 標準表示ではスロット領域をカード実寸の高さに詰め、控え Quartet までの黒い余白を作らない。下部の `Launch Quartet` ボタンも見切れないようにする。
- 集中表示中に同じスロットボタンを押したとき、対象ウィンドウの上に他アプリの可視ウィンドウが重なっている場合は 4 面表示へ戻さず、集中表示を維持して前面復帰する。
- 2026-05-14 時点では、CLI / Antigravity でも操作を予測しやすくするため、同じスロットボタンは常に 1 面フォーカス表示と 4 面表示のトグルとして扱う。上に他アプリが重なっているかどうかでは分岐しない。
- パネルカードや外部ウィンドウのドラッグ操作中は、フォーカス中スロットの再前面化を抑制する。カードのドラッグアンドドロップ中と直後はスロットクリックによる 1 面フォーカストグルも一時抑止し、ドラッグ中のマウス移動やボタン解放が集中表示へ化けないようにする。1 面表示中にカード入れ替えや状態クリアが発生した場合は、全スロットを 4 面再配置せず、フォーカス対象以外だけを背面へ整える。

## 5. ビルド
- 実行中の本体が既定の `bin\Debug` 出力をロックする場合がある。
- 反復確認では `scripts/Build-Panel.ps1` または `--artifacts-path` を使う。

## 6. UI レイアウト細部（2026-05-14 修正済み）
- `ApplicationGroupLabelStyle` の Margin は `"8,-5,0,0"` で境界線上に浮かせる（負のtopマージンで境界線に重ねる）。正にするとボタンと被る。
- IDE / CLI ボタンはどちらも高さ 24、`ItemContainerStyle Margin="2,0,2,4"` で統一する。IDE StackPanel と CLI UniformGrid の上端・隙間をそろえる。
- CLI ボタン配列は上段 `Codex` / `Claude`、下段 `Copilot` / `Grok` / `Gemini`。下段は Copilot と Gemini を同じ可変幅、Grok を短い固定幅にし、5 ボタン化してもカード内に収める。
- `SlotCardTemplate` 内から `StaticResource` で参照する DataTemplate / Style は、必ず `SlotCardTemplate` より前に定義する。後方定義にするとビルドは通っても起動時に `XamlParseException` で終了する。
- 控え Quartet Expander のコンテンツ上マージンは `"0,12,0,0"`。右上の `Windows` 補助アプリボタン行がオーバーレイ配置のため、閉じた状態の隙間に寄せつつ、開いた控えカードと重ならないだけの隙間を確保する。
- 縮小表示は C/D 行と `Windows` 補助アプリ行が初期状態で隠れない高さを最低値にする。
- 縮小表示の 4 パネルボタン中央には、極小表示と同じ表示/非表示の円形ボタンを重ねる。4 パネルの空間メタファーを崩さないよう、右側の `前` / `背` 操作列には重ねない。
- 標準表示の既定高さは、`Launch Quartet` ボタン下に空白が残らない値にする。下端余白が見える場合はウィンドウ既定高さを先に調整する。
- Windows 補助アプリが 4 つに増えたため、縮小表示の最低幅は Antigravity2 ボタンまで収まる値にする。
- Windows 補助アプリボタンは 4 つすべて同じ幅を保ちつつ、左の `Windows` ラベルが `indows` のように見切れない幅に収める。

## 7. 一括起動の逐次化（2026-06-04 修正済み）
- `LaunchAllMissingAsync` では A-D の対象スロットを 1 つずつ順番に起動する。非 CLI も `Task.WhenAll` で並列起動しない。VS Code / Antigravity / terminal の新規ウィンドウ捕捉が競合し、4 つ起動しきれない、または別スロットの遅延ウィンドウを拾って 2x2 配置がずれる問題を避けるため。
- 各スロットの起動結果は捕捉できた時点で `StatusStore.AssignWindow` へ反映し、短い間隔を置いて次スロットへ進む。最後に `RefreshSlotsAsync` と `ArrangeSlotsOnActiveMonitorWithSettlingAsync` を実行し、遅れて再接続されたウィンドウも 2x2 配置対象に含める。
- 起動スロット間の待ちは短めに保ち、テンポよく進める。起動後の 260ms / 720ms 付近の即時補正と 30 秒までの遅延補正は、`WindowArranger.NeedsArrange` で位置ずれがある場合だけ実行する。追加補正ではレイヤー再適用やパネル前面化を繰り返さず、`ArrangeSlotsOnActiveMonitorQuietly` で静かに座標だけ戻す。
- VS Code の新規ウィンドウ捕捉では、専用 `user-data-dir` 有効時は `code.lock` PID に一致するウィンドウを採用する。専用 user-data を使わない設定では対象ワークスペース名がタイトルに出たウィンドウを補助的に採用する。単に「最初に見えた VS Code ウィンドウ」を採用すると、直前スロットの遅延表示を次スロットへ誤割り当てするため避ける。
- CLI (cmd.exe) は `UseShellExecute = true` で起動する。`WindowStyle = Normal` が適用されウィンドウが即時表示される。`UseShellExecute = false` + `CreateNoWindow = false` の組み合わせより表示が速い。
- VS Code など IDE の同一アプリタイプ内スロットは引き続きシーケンシャル起動。Workspace CLI も一括起動ではスロット単位で順次起動し、terminal 系プロセス名の競合を避ける。
- VS Code は起動プロセス ID と実際のウィンドウプロセス ID がずれる場合があるため、新規 VS Code ウィンドウの HWND を優先して捕捉する。
- VS Code が低速端末で新規 HWND 捕捉前に既存スロット用ウィンドウとして残った場合は、専用 `user-data-dir` の `code.lock` に記録された PID と VS Code のトップレベル HWND を照合して再接続する。これにより、ウィンドウ自体は開いているがスロットが赤表示のままになる状態を次回起動・再起動時にも回復する。
- 起動後の遅延再配置は 30 秒まで確認するが、`WindowArranger.NeedsArrange` で現在位置が期待 2x2 配置から大きく外れている場合だけ静かに再配置する。高速端末で既に正しい位置にあるウィンドウは触らない。
- ディスプレイ切替（`ToggleMonitorButton_Click`）と非表示からの復帰（`ToggleVisibilityButton_Click`）も単発の `ArrangeSlotsOnActiveMonitor` ではなく `ArrangeSlotsOnActiveMonitorWithSettlingAsync` を使う。DPI/解像度が異なるディスプレイへ移すと、移動直後に対象ウィンドウへ `WM_DPICHANGED` が届いてこちらの指定サイズを上書きするため、遅延付きの `NeedsArrange` 補正で移動先の作業領域に合わせて再補正する。
- 移動直後の「小さく出てから直る」フラッシュを抑えるため、即時補正 `ImmediateArrangeSettleDelays` は前倒しの密な間隔（40/110/220/420/720ms）にする。`WM_DPICHANGED` のサイズ上書きはクロスプロセスで非同期に届くため、単発の同期再適用では防げない。短い間隔で `NeedsArrange` を見て静かに補正することで、上書き後のずれを素早く戻す。
- 2x2 配置はウィンドウの不可視リサイズ枠（`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` と `GetWindowRect` の差）を打ち消して配置する。`WindowArranger.BuildPlacements` のセルは可視枠の目標矩形とし、`CompensateForFrame` で各辺の不可視枠ぶん外側へ広げて `SetWindowPos` する。これをしないと上端は不可視枠 0、左右下は約 7px(DPI で増減) のぶん見た目の隙間が偏る。`NeedsArrange` の現在値比較も可視枠（`TryGetVisibleBounds`）で行う。
- セル計算（`BuildPlacements`）は `gap` を内側の隙間と外周マージンの両方に使う（縦横とも 3 枠ぶん）。不可視枠を補正済みなので `gap` がそのまま見た目の均等な隙間になる。隙間を詰めるときは `gap` を下げる。既定は `6`（密着させず均等に詰める値）。
