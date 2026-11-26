# Turtle AI Code Quartet Hub 実装パターン・注意点

更新日: 2026-09-17

## 1. AI 状態は「フックの明示通知」だけで表示する（推定は戻さない）
- **方針変更（2026-11-11）**: 以前の AI 状態検出（UI Automation のチャット走査、拡張ログ解析）は不安定なため削除した。現在は、Claude Code / Codex / GitHub Copilot / Grok の**フック**が送る明示的な通知（指示を受けた・応答が終わった・許可を待っている）だけで、スロットの作業中・完了を表示する。色はツールごとに分けない。
- **引き続き戻さないもの**: UI Automation、拡張ログ、OpenTelemetry、ターミナル出力からの推定と、VS Code 外枠オーバーレイ。Jump List の文言も AI 状態で変えない。`StatusStore.RefreshWindowStatusesAsync` は HWND、タイトル、ワークスペース表示更新の軽量処理として維持する。
- **ファイル**: `Services/AiActivityTracker.cs`（通知の解釈・スロット単位の状態・cwd からのスロット特定）、`Models/AiActivityState.cs`、`Services/AiNotifyHookInstaller.cs`（フックの設定・解除と通知スクリプト）、`MainWindow.AiActivity.cs`、`Models/WindowSlot.cs`（`AiTracker` / `AiActivity`）、`TurtleAIQuartetHub.Panel.Tests/AiActivityTests.cs`
- **流れ**: フック → `~/.turtle-ai-quartet-hub/ai-notify.ps1` → 既存の名前付きパイプ `TurtleAIQuartetHub.Commands` に `["--ai-event", tool, event, cwd, session, notification_type]` → `ExecuteExternalCommand` の先頭で振り分けて `HandleAiHookEvent`。**この経路では `ActivatePanelWindow` を呼ばない**（通知のたびにハブが前面に出ないように）。ハブ未起動時に `--ai-event` 付きで起動されても UI を開かずに終了する。
- **スロット特定**: フックの `cwd` が、ウィンドウを持つスロットのワークスペース（`CurrentWorkspacePath` / `DisplayPath` / `EffectivePath` など）と同じか配下なら対象。最も深く一致したスロットを選ぶ。フォルダ名の前方一致（`turtle` と `turtle-ai…`）を同一視しない。Git Bash 形式（`/c/...`）も扱う。リモート URI は対象外。
- **状態**: スロットごとに「作業中のセッション（tool:session）」と「未確認の完了」を持つ。開始で作業中、応答終了で「完了」（フォーカス表示中でも光らせる）。許可や質問の確認（`permission_prompt` / `elicitation_dialog` / `PermissionRequest`）は作業の途中なので表示を変えず、枠を回る緑の光を続ける（2026-11-12 に、完了と同じ青にする扱いを取りやめ）。Claude / Grok の `idle_prompt`（応答後の入力待ち）は無視する（消した光が戻るため）。Grok の `task_complete` と `PermissionDenied` も無視する。Grok の `StopFailure` / `StopCancelled` は完了と同じ光（中断や失敗のあと緑が残らないようにする）。Codex の `Interrupt` は光らせずに戻す。完了通知が来ないセッションは 60 分で片付ける。ツールごとに状態の色を分けない。
- **解除**: スロットの `IsFocused` が切り替わったとき（`Slot_PropertyChanged`。パネルでの 1 面フォーカス⇔4 面表示のほか、ウィンドウの直接最大化・復元によるフォーカス同期も含む）に光を消す。ウィンドウが前面に来ただけでは消さない（フォーカス表示中に終わっても気づけるように、2026-11-11 利用者要望）。カードの入れ替え中（`StatusStore.IsSwappingSlotContents`）は IsFocused がウィンドウと一緒に移るだけなので消さない。周期更新では、完了通知が来ないまま 60 分たった作業中だけを片付ける。ウィンドウが閉じたら（`WindowSlot.ClearWindow`）消す。スロット入れ替えでは `WindowSlot.SwapAiActivity` でワークスペースと一緒に移す。
- **見た目**: 作業中は `AiCardOrbitGlow` が、`DrawingVisual` 上に直線と厳密円弧（`ArcTo`）の 28 段グラデーション・トレイル（外周オーラ `#45D483` 〜ボディ `#7FF3B4` 18 段＋LED 芯 `#98FFC8` 〜 `#C8FFE2` 10 段、カード短辺に応じた適応幅）を描き、`BorderThickness="1"` の中心線（`inset=0.5`, 角丸半径 `R - 0.5`）上を緑の LED の一筋の光として周回する。標準約 3.60 秒・縮小約 2.77 秒・極小約 2.45 秒の落ち着いた周期で、前半〜中盤をふわっとゆっくり巡り、終盤（0.80 付近）で速度比約 2.0 倍へ少しだけ心地よく加速し、始点・終点で連続接続する（`Shape` や巨大 `BlurEffect` は使わず、クリップ角の露出を防ぐ。詳説は §10、§6/§8/§9 は過去実装）。完了は `AiDoneFirefly` により、カード地（ベース `#0C2135`＋重ね `#1D5B94`）・枠（`#3EA3EC`）・外側の青い光（`#228BE0`）と、標準・縮小の A〜D アイコン（ベース `#103457`＋重ね `#2371C2`・外枠 `#74CBFF`＋青光背）および極小表示タイル（ベース `#103457`＋重ね `#2577CB`）を、全ロール `UIElement.Opacity` 統一・モニタリフレッシュレート同期・水平接線 2 区間曲線（3.0 秒周期）で滑らかにホタル点滅させる（詳説は §11。ボタンと IDE / CLI の枠内は塗らない）。目が痛くならないことを優先し、急激なチカチカ点滅や光の境界線を出さない。
- **フック設定**: 歯車設定の「AI の作業通知」から設定・解除する。`~/.claude/settings.json`（`UserPromptSubmit` / `Stop` / `Notification`）、`~/.codex/hooks.json`（`UserPromptSubmit` / `Stop` / `PermissionRequest` / `Interrupt`、初回は CLI の `/hooks` で承認が必要）、`~/.copilot/hooks/turtle-ai-quartet-hub.json`（`userPromptSubmitted` / `agentStop`）、`~/.grok/hooks/turtle-ai-quartet-hub.json`（`UserPromptSubmit` / `Stop` / `StopFailure` / `StopCancelled` / `Notification`。設定後はセッションを開き直す）。既存の設定や利用者のフックは残し、通知スクリプトのパスを含む項目だけを足し引きする（2 回設定しても重複しない）。書き換え前の内容は `.turtle-hub-backup` に残す。ツールのフォルダが無ければ触らない。Codex の `config.toml` の `notify` と Grok の `config.toml` は触らない。
- **通知スクリプトの注意**: Windows PowerShell 5.1 は BOM なし UTF-8 を ANSI として読むため ASCII だけで書く。`$Event` は自動変数なので引数名は `HookEvent`。AI の動作を変えないよう、必ず `{}` を出して終了コード 0 で終わる（Stop / UserPromptSubmit の出力は継続や拒否として解釈されうる。Grok でも同じ）。標準入力の詳細は `notification_type` と `notificationType` の両方を読む。ハブ未起動時はパイプの有無を見て即終了する。スクリプトは AppData ではなくホーム直下に置く（MSIX 版では AppData がアプリ専用の場所へ振り替えられ、AI ツールから見えないことがあるため）。

## 2. スロット状態と保存
- `slots.json` は visible slot と stored panel を同じドキュメントで保持する。
- `WindowSlot` は管理対象ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- パネルカードを A-D 間でドラッグ移動するときは、表示位置の `Name` と VS Code user-data / workspaceStorage / code.lock 用の `RuntimeSlotName` を分けて扱う。実行中ウィンドウを B から D に移した場合、D は B の runtime profile を持ち続け、空いた B は D の runtime profile で新規起動することで、同じ user-data を二重に再利用しない。
- VS Code / Antigravity の workspaceStorage 読み取りに失敗しても `SavedWorkspacePath` は消さない。強制終了後や中途半端な user-data 状態でも、次回起動に使える保存済みパスを残す。
- 起動確認または periodic refresh で正しいワークスペースが読めた場合は、`Path` / `SavedWorkspacePath` / `SavedWorkspaceConfirmed` と自動タイトルを保存する。タイトル照合はフォルダ名の部分文字列ではなく、VS Code タイトルのワークスペース段を使う。ファイル名に `2025` などが含まれても `2025` フォルダへ保存をすり替えてはならない。
- 控え Quartet から表示スロットへ復帰するときは、旧ウィンドウへ close を送っただけで次の起動へ進めない。置換用の close 待ち処理を通し、VS Code の再接続では起動予定の `SavedWorkspacePath` / `Path` とタイトルが一致しない既存 slot-owned window を採用しない。旧 workspace のウィンドウを新しいパネル表示へ誤接続すると、パネル名と実ワークスペースが永続的にずれる。

## 3. 複数アプリ起動
- 既定のスロットアプリは VS Code (`vscode`)。一括起動はスロットごとの `ApplicationId` に従う。
- VS Code は `VscodeLauncher` に残し、remote URI と workspaceStorage 読み取りの既存挙動を壊さない。既定は標準プロファイル共有（`useDedicatedUserDataDirs=false`）。専用 user-data-dir は任意で、有効時だけ `code.lock` 再接続を使う。
- VS Code の workspaceStorage は `GetEffectiveUserDataDirectory` を読む。共有プロファイル時にスロット別フォルダを見ると、保存済みワークスペースやレイアウトが空に見える。
- Antigravity のワークスペース推定は VS Code 互換の `workspaceStorage` 形式を使い、`%APPDATA%/Antigravity IDE/User/workspaceStorage` を含む実アプリデータ候補を見て、ウィンドウタイトルにワークスペース名が含まれるパスだけを採用する。`Antigravity IDE` は workspace IDE の実際のデータフォルダ名で、`Antigravity` は別の Windows アプリにも使われるため、IDE 名の候補を優先しつつ旧候補を残す。
- Antigravity など VS Code 以外の workspace IDE は `ApplicationLauncher` の汎用起動で扱う。起動プロセスと表示ウィンドウのプロセス ID がずれるため、新規ウィンドウハンドルで割り当てる。
- Antigravity IDE は `%LOCALAPPDATA%/Programs/Antigravity IDE/Antigravity IDE.exe` 相当を優先検出する。過去設定の bare `antigravity` は新しい Antigravity Windows アプリと衝突しやすいため、既定検出へ戻して IDE 側を開く。
- Antigravity や terminal が起動完了後に中央へ戻ることがあるため、起動確認直後の配置に加えて短い遅延再配置を複数回行う。標準またはやや低性能な PC に備えて 8 秒、12 秒後の再配置も行い、ユーザーが集中表示に入った後は再配置しない。
- Codex / Claude / GitHub Copilot / Grok Build / Antigravity CLI は `WorkspaceCli`。対象スロットの保存済みワークスペースを working directory にした `cmd.exe /k` で CLI を起動し、terminal 系の新規ウィンドウをスロットに割り当てる。
- 一括起動では CLI 種別同士を並列捕捉しない。VS Code / Antigravity などの非 CLI は並列のまま、CLI グループだけ順次起動して terminal 系プロセス名の競合を避ける。
- Workspace CLI は同一 CLI 種別の複数スロットでも、まず各 `cmd.exe` を先に起動し、`Turtle {slot} - {shortName}` のタイトルで後から捕捉する。タイトルが Windows Terminal 側で反映されない場合は、新規 terminal ウィンドウを起動順で割り当てる。
- Workspace CLI はコマンド検出で可用性を判断する。PATH に加えて npm / pnpm / Volta の一般的な shim 置き場、Claude Code の公式インストーラが使うユーザー単位の `~\.local\bin`、Grok Build の Git Bash インストーラが使う `~\.grok\bin` も探索する。terminal プロセスが起動済みであることや Windows アプリ検出だけで CLI インストール済み扱いにしない。
- GitHub Copilot Chat 拡張が作る `globalStorage\github.copilot-chat\copilotCli\copilot*` は、実体が未インストール時のインストール案内ブートストラップなので、GitHub Copilot CLI インストール済み判定には使わない。
- Workspace CLI の実行コマンドは `ResolvedCommand` を優先し、短い `claude` などの設定コマンドだけに依存しない。起動した `cmd.exe` には検出済みコマンドのフォルダと一般的な shim 置き場を PATH 先頭へ一時追加する。
- Antigravity CLI は `antigravity-cli` ID と `agy` コマンドで定義する。公式 Windows インストーラーが使う `%LOCALAPPDATA%\agy\bin` をコマンド検出と起動 terminal の PATH の両方へ追加する。旧 `gemini` ID は保存済みスロットを壊さないため `antigravity-cli` へ正規化するが、旧 Gemini CLI のコマンド・表示名・検出設定は新定義へ引き継がない。
- GitHub Copilot CLI の既定起動は、対象ワークスペースへ `cd /d` して `copilot` だけを実行する。`workspacePath` は暗黙の引数として渡さない。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は CLI とは別に `codex-app` / `chatgpt-app` / `claude-app` / `antigravity-app` の `SingleWindowAgent` として補助ボタン行に残す。Antigravity2 は `%LOCALAPPDATA%/Programs/Antigravity/Antigravity.exe` 相当を優先検出し、Claude の右側に表示する。
- スロット内で別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウへ close を送り、短く待ってからスロット状態をクリアし、押されたアプリを同じスロット位置へ起動する。

## 4. UI とフォーカス
- UI は各スロット内に `IDE` 枠と `CLI` 枠を持つ。IDE 枠は VS Code / Antigravity を縦並び、CLI 枠は上段 `Codex` / `Claude`、下段 `Copilot` / `Grok` / `Antigravity` で表示する。
- CLI 選択中のボタンはベタ塗りではなく、暗めの半透明に近い緑で表示する。VS Code 選択中はロゴに近い青、Antigravity IDE 選択中は多色グラデーションの枠を使う。ただし未検出アプリは選択中でも `IsAvailable=false` のグレーアウト表示を優先し、インストール済みと誤認させない。
- 未起動スロットの IDE / CLI 選択ボタンは起動対象を変更するだけで、アプリを自動起動しない。起動は個別スロットの起動ボタンか `Launch Quartet` のみで行う。
- メインパネルのクリアアイコンは右上のゴミ箱アイコンで表示する。押下時は削除確認ダイアログを出し、確認後に対象スロットの保存済みタイトル、パス、選択アプリ、ウィンドウ割り当てを削除する。起動中の IDE / CLI ウィンドウがある場合は close を送ってからパネル情報を削除する。
- 通常表示のスロット左下にはフォルダアイコンボタンを置き、`WindowSlot.DisplayPath` から Explorer で開けるローカルフォルダを解決する。既存ディレクトリはそのまま、`.code-workspace` など既存ファイルは親フォルダを開く。`vscode-remote://...` や `ssh://...` など non-file URI は Explorer 対象にせず、ボタンをグレーアウトする。
- スロットの実行中アクションボタン文言は `閉じる` にする。未起動時は `起動` / `新規`。
- タイトルバー右上は縮小表示、`?` ヘルプ、歯車設定、最小化、閉じるの順に置く。ヘルプは Codex / GitHub Copilot / Antigravity CLI / Claude Code / Grok Build の CLI 別カードで構成し、各カード内を `インストール` と `自律実行の起動オプション` に分ける。方式名とコマンドを別表示にしてコマンドだけを正確にコピーできるようにし、自律実行オプションは警告色と共通の注意書きで通常の導入手順から区別する。IDE / Windows アプリは公式サイト参照とし、Antigravity CLI は公式 Windows PowerShell / CMD インストーラーと `agy --dangerously-skip-permissions`、Claude Code は公式インストーラの PowerShell / CMD コマンドと npm コマンドを書く。本文とコマンドは選択コピーできるよう `TextBox IsReadOnly=True` で表示し、実行操作は持たせない。
- CLI の導入方法は公式の最新情報を確認して更新し、README と `?` ヘルプで同じ選択肢を示す。Grok Build CLI の Windows 向け手順は Git Bash を経由させず、`irm https://x.ai/cli/install.ps1 | iex` を表示する。Git Bash / WSL 向けには `curl -fsSL https://x.ai/cli/install.sh | bash` を残す。
- 歯車設定画面では IDE / CLI / Windows アプリの起動コマンドを編集し、`%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json` へ保存する。VS Code の設定は `CodeCommand` と `applications[].command` を同期させる。同じ画面の「VS Code 共通ユーザー設定」から、オープンネットワーク / プロキシ環境をハブ設定へ保存できる。専用プロファイルがある場合だけその `User/settings.json` を更新し、標準の `%APPDATA%/Code/User/settings.json` は書き換えない。
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
- CLI ボタン配列は上段 `Codex` / `Claude`、下段 `Copilot` / `Grok` / `Antigravity`。下段は Copilot と Antigravity を同じ可変幅、Grok を短い固定幅にし、5 ボタン化してもカード内に収める。
- `SlotCardTemplate` 内から `StaticResource` で参照する DataTemplate / Style は、必ず `SlotCardTemplate` より前に定義する。後方定義にするとビルドは通っても起動時に `XamlParseException` で終了する。
- 控え Quartet Expander のコンテンツ上マージンは `"0,12,0,0"`。右上の `Windows` 補助アプリボタン行がオーバーレイ配置のため、閉じた状態の隙間に寄せつつ、開いた控えカードと重ならないだけの隙間を確保する。
- 縮小表示は C/D 行と `Windows` 補助アプリ行が初期状態で隠れない高さを最低値にする。
- 縮小表示の 4 パネルボタン中央には、極小表示と同じ表示/非表示の円形ボタンを重ねる。4 パネルの空間メタファーを崩さないよう、右側の `前` / `背` 操作列には重ねない。
- 終了確認は本体内の緑基調オーバーレイを維持する。縮小・極小表示では本体の表示領域が確認UIより小さいため、確認中だけ本体を必要な最小サイズへ一時展開し、キャンセル時に元の位置・サイズ・最小寸法へ復元する。固定幅の確認UIを小さい本体内で単純にクリップさせない。
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

## 8. ディスプレイ移動（全移動＋単独移動, 2026-06-08 追加）
- 配置先は「ベースディスプレイ（全体）」＋「各スロットの単独 override（`WindowSlot.MonitorOverride`, 非永続）」で表す。実効ディスプレイ＝`override ?? ベース`。フォーカスやレイヤーと同じランタイム状態として扱い、`slots.json` には保存しない。`WindowSlot.ClearWindow` で override も解除する。
- フッタの「全ディスプレイ移動」（旧「ディスプレイ移動」）はベース（`_activeMonitorIndex`）だけを次の面へ進める。進めた直後に `CollapseMonitorOverridesMatchingBase` で「override == 新ベース」のスロットの override を解除し、群れへ合流させる。これで「2 に単独移動 → 全移動でベースが 1→2 → 単独スロットは 2 に留まりつつ合流」が成立する。単独移動を 2→1 へ押し戻すスワップは行わない（リベースの不変条件）。
- 各スロットカードの「単独ディスプレイ移動」ボタン（モニタアイコン）は、そのスロットの実効ディスプレイを次へ巡回する。次がベースに一致したら override=null（ベースに戻る）。2 枚運用では「ベース ⇄ もう一方」のトグル。管理中ウィンドウが無いスロットでは無効。
- `WindowArranger.BuildPlacements` は象限セル（index→列/行）を固定したまま、作業領域だけスロットごとの実効ディスプレイから取る。同じ面へ複数スロットを単独移動しても各々の象限へタイルする。`Arrange` / `ArrangeExcept` / `NeedsArrange` の `monitorIndex` 引数は「ベース」を意味し、内部で per-slot に解決する。
- フォーカス（1 面）は実効ディスプレイで最大化する（`FocusMaximizedOnMonitor` / `MaximizeOnMonitor`）。`EnsureWindowOnMonitor` は対象が既にその面なら何もしないため、未移動スロットは従来どおりベース面で最大化する。単独移動したパネルをフォーカス・フォーカス解除しても、他ディスプレイのパネルは触らない。
- モニタ構成が変わったとき（単独移動先のディスプレイを抜く等）は `NormalizeMonitorOverrides` が override を健全化する。正規化後にベースと一致するものは解除、範囲外は丸める。1 枚運用ではすべてベースへ収束し単独移動は自然解消する。
- カードの `DisplayBadgeText`（例 "D2"）は、複数モニタかついずれかのスロットが単独移動中のときだけ表示する。全パネルが同一面に揃っているときは雑音を出さない。`RefreshAuxiliaryUi` から `UpdateDisplayBadges` を呼んで常時最新化する。

## 9. 複数ディスプレイの同時フォーカス（2026-06-09 追加）
- フォーカス（1 面・最大化）は「全体で 1 つ」ではなく「各ディスプレイで 1 つ」。3 画面なら最大 3 フォーカスを許容する。`WindowSlot.IsFocused` は複数同時 true を取り得る（ただし同一実効ディスプレイには 1 つ）。
- 土台は `WindowArranger.BuildPlacements` が `IsFocused` のスロットを必ずスキップすること。これで全 Arrange は各ディスプレイの最大化ウィンドウを保ったまま非フォーカスだけ象限へ並べる。フォーカス操作は基本「フラグを更新 → Arrange → `ReassertAllFocusedSlots`（各面で最大化・前面化）」で表現する。
- フォーカスの集合操作はディスプレイ単位のヘルパで行う: `GetSlotMonitorIndex` / `GetFocusedSlotOnMonitor` / `SetFocusedSlotForDisplay`（同面の旧フォーカスだけ解除）/ `ClearFocusedSlotForDisplay` / `SendOtherSlotsToBackOnSameDisplay`。z-order は「同じ実効ディスプレイの他スロットだけ背面へ」。別ディスプレイのフォーカスや前後は触らない。
- リアサート（`ReassertFocusedSlotIfNeeded`）と手動最小化整合（`ReconcileExternallyMinimizedFocusedSlot`）、非表示復帰（`_hiddenFocusedSlots` リスト）、`CaptureFocusedLayout`、`ApplyLayerPreservingFocusMode`、`ArrangeSlotsAfterPanelStateChange` はすべて全フォーカスをループ処理する。1 つの面を手動最小化したらその面のフォーカスだけ解除し、他面は維持する。
- スロットクリックのトグルは「そのディスプレイだけ」を 1 面/4 面で切り替える。他ディスプレイのフォーカスは保持。
- 全ディスプレイ移動を押したら、ベースに追従するスロット（override なし）のフォーカスだけ解除して 4 面に戻す。単独移動中（override あり）のスロットはディスプレイが変わらないため、その面のフォーカス（1 面表示）を維持し、配置後に `ReassertAllFocusedSlots` で立て直す。リベース（override==新ベースの解除）は従来どおり行い、解除されたスロットはベース追従としてフォーカスも解除対象になる。
- 単独ディスプレイ移動とフォーカスの干渉は「移動先優先」: フォーカス中パネルを、既にフォーカスを持つディスプレイへ移すと、移動先のフォーカスを優先し、移動したパネルはフォーカス解除（移動先で非フォーカス＝最大化の背面）になる。移動元のフォーカスはそのパネルが去るので自然に解ける。移動先にフォーカスが無ければフォーカスを引き継いで移動先で最大化する。
- ディスプレイ色: ベース（作業ベース）は常に緑（`AccentBrush`）、非ベースは番号の小さい順に 青 `DisplayAccent2Brush` → 紫 `DisplayAccent3Brush` → 金 `DisplayAccent4Brush`（`GetDisplayBrushForMonitor`）。全ディスプレイ移動でベースが動いても「緑＝ベース」を維持する（番号固定ではない）。
- 色は `WindowSlot.DisplayBrush`（実効ディスプレイの色、常時更新）に集約し、バッジ文字色・枠線色、フォーカス枠 `FocusFrameBrush`（フォーカス中だけ DisplayBrush、それ以外は透明）に使う。`FocusFrameBrush` は標準カード `SlotCardFocusFrame`、縮小 `CompactFocusFrame`、極小 `MicroFocusFrame` の `BorderBrush` に直接バインドする（`Setter.Value` はバインド不可のためトリガではなく直接バインド。トリガ側の固定色 `BorderBrush` 設定は撤去）。グロー(DropShadow)と LED ドットは Setter 内 Freezable で DataContext が効かないため緑のまま。
- 色解決 `GetDisplayBrushForMonitor` は `TryFindResource`（見つからなければ緑フォールバック）で必ず例外を出さない。`UpdateDisplayBadges` は毎ティックの `RefreshAuxiliaryUi` から呼ばれるため、ここで例外を投げるとアプリ全体が落ちる。
- リアサート（`ReassertFocusedSlotIfNeeded` / `ReassertAllFocusedSlots`）は `SetForegroundWindow` を呼ばない（`MaximizeOnMonitor` + `BringToFrontOnce` + 同面背面のみ）。複数ディスプレイのフォーカスへ毎回フォアグラウンドを奪うと、アクティベーション争奪でちらつき・ハングになる。前面化はパネル（`SchedulePanelToFront`）に任せる。初回フォーカス（`ToggleSlotFocus`）だけは従来どおり `FocusMaximizedOnMonitor` で一度だけ前面化する。
- 単独移動は settle 付き再配置の await を挟む前に `ReassertAllFocusedSlots` で移動先の最大化を先行確定し、「4 面のまま少し残ってから 1 面へ遷移」する見た目のラグを抑える。
- 最前面/最背面（`ApplyLayerPreservingFocusMode`）は、非フォーカスの全スロットへレイヤーを適用してから、フォーカスを持つ各ディスプレイで同面の前後関係を保ち直す。フォーカスの無いディスプレイのウィンドウも確実に前面/背面化される。
- 致命傷対策: `App.OnStartup` で `DispatcherUnhandledException`（`e.Handled=true`）/ `TaskScheduler.UnobservedTaskException`（`SetObserved`）/ `AppDomain.UnhandledException` をログ付きで握りつぶす。外部ウィンドウや P/Invoke の一過性失敗でランチャー本体が勝手に終了する事故を防ぐ。
- 周期更新 `RefreshTimer_Tick` は async void のため内部でも try/catch して `DiagnosticLog` へ記録する（`DispatcherUnhandledException` の手前で握る多重防御）。
- D&D 入替（`SwapSlotContents`）は `MonitorOverride` もワークスペース側と一緒に交換する。入替はカード上の象限位置だけを交換し、各ワークスペースは元のディスプレイ・フォーカス状態・4 面/1 面の見え方を維持する（入替でウィンドウはディスプレイをまたがない）。
- 入替・単独移動・全体移動後の DPI/解像度差の補正は、フォーカスがあっても走る `SettleArrangementPreservingFocusAsync`（`NeedsArrange` はフォーカス中スロットをスキップ）で行う。`ArrangeSlotsOnActiveMonitorWithSettlingAsync` の settling は `CanReapplyPostLaunchArrangement` によりフォーカス中は走らないため、フォーカスを保つ操作では使わない。
- ズームアウト演出とちらつき対策: 配置（`ArrangeCore`）は最大化/最小化中のウィンドウの復元先 `rcNormalPosition` を目的セルへ事前設定（`SetWindowPlacement`。`WPF_RESTORETOMAXIMIZED` も解除）してから `SW_RESTORE` する。DWM の復元アニメは復元先矩形へ向かって再生されるため、ズーム解除はアニメーション付きで目的セルへ直接着地し、「旧位置へ戻ってからセルへジャンプ」する二段移動（ちらつき）は出ない。rcNormalPosition はワークスペース座標（プライマリ作業領域原点基準）なので原点ぶん補正し、残差は直後の `SetWindowPos`（画面座標）が吸収する。
- アニメを出さない配置: フォーカスイン随伴の背面整列（`ArrangeSlotsExceptOnActiveMonitor`）と settling 補正（`ArrangeSlotsOnActiveMonitorQuietly`）は `animateRestore=false` で、復元が必要なウィンドウにだけ `DWMWA_TRANSITIONS_FORCEDISABLED` を対で適用し、`SW_SHOWNOACTIVATE` で非アクティブのまま通常サイズへ戻す。`SW_RESTORE` は非表示アニメを止めてもウィンドウをアクティブ化して z-order を変えるため、旧フォーカスが新フォーカスの前へ一瞬割り込む。A フォーカス中に B を押す即切替は、B を通常Z順の先頭へ非アクティブで準備し、`SW_MAXIMIZE` 開始後にフォアグラウンドを渡す。先にアクティブ化すると通常サイズのElectronサーフェスが白く再描画される。A の解除は遅延後に B の背面で非アクティブ・アニメ無しで済ませる。ディスプレイまたぎのフォーカス移動（`EnsureWindowOnMonitor`〜最大化）も遷移アニメを止める。
- フォーカスイン時の透け対策: 他スロットの背面送り（`SendOtherSlotsToBackOnSameDisplay`）は最大化アニメ完了後（`FocusSwitchArrangeDelay` 経過後）に行う。アニメ開始と同時に HWND_BOTTOM へ送ると、最大化が画面を覆い切るまでタイルの位置に管理外ウィンドウ（ブラウザ等）が透けて見える。
- フォーカス切替の背景フラッシュ対策（2026-07-05/07/11 追加調整）: `MainWindow.PrepareFocusTransitionBackdrop` は同じ面に旧フォーカスがある間、その旧1面と他タイルの z-order を動かさず、旧1面を全面の覆いとして維持する。新フォーカスだけを `BringToFrontWithoutTopmost` で非アクティブのまま旧1面の上へ準備し、`FocusMaximizedOnMonitorAsync` は `SW_MAXIMIZE` の反映後に `SetForegroundWindow` する。4 面へ戻すときは先に非フォーカスを静かにセルへ整えて背後を埋め、フォーカス解除後のレイヤー再適用を `FocusSwitchArrangeDelay` 後まで遅らせる。後片付け中に `HWND_BOTTOM`、`SW_RESTORE`、フォーカス対象の反復前面化を使うと、旧窓と新窓の双方で白背景・再描画・z-order の跳ねが発生するため禁止する。
- 2026-07-23 のズーム面白化対策: `WindowArranger` の `ShowWindow` はハング防止のため実体が `ShowWindowAsync` であり、呼び出しから戻っただけでは `SW_MAXIMIZE` が対象スレッドで処理済みとは限らない。直後に `SetForegroundWindow` すると通常サイズの Electron を先にアクティブ化し、白いサーフェスがDWMの拡大アニメーション素材になる。初回フォーカスは `FocusMaximizedOnMonitorAsync` で最大化要求後に `IsZoomed` を8ms間隔・最大160msで確認し、反映後に一度だけ前景化する。タイムアウトを持たせ、応答しない管理窓でパネルを停止させない。
- 2026-07-23 の即時切替補強: A の1面から B の1面へ切り替えるとき、`SW_SHOWNOACTIVATE` でAを復元しても、復元要求より前にZ順が確定していないとAの縮小がBより前へ一瞬出る環境がある。Bの最大化完了待ち後、`ArrangeExcept` へBのハンドルを `keepAboveHandle` として渡し、旧AをBの直後へ相対配置してからアニメ無しで4面セルへ戻す。`SetWindowPos(SWP_ASYNCWINDOWPOS)` と既存の `ShowWindowAsync` を同じ対象スレッドの非同期キューへ順番に積み、復元中もBを覆いとして維持する。DWMクロークは管理スロットの下地を消して白背景を露出させるため、フォーカス切替では使わない。
- 不可視枠（DWM 拡張フレームと GetWindowRect の差）は「通常状態」のときにだけ計測し、ハンドルごとにキャッシュする（`GetFrameInsetCached`）。最大化中は枠のはみ出し方が異なり、最小化中は座標が無効なため、そのまま測ると 4 面セルより大きい/ずれた配置になる。復元前の事前補正（rcNormalPosition）はキャッシュ値、最終配置（SetWindowPos）は復元後の実測で行う。
- パネル UI の描画は GPU 既定（`RenderMode.SoftwareOnly` 強制は撤去）。特定環境で描画乱れが出る場合のみ SoftwareOnly へ戻す。
- 1 面フォーカス中の z-order は `EnsureFocusedSlotAboveTiles` に集約し、常に `パネル本体 > 1 面フォーカス > 同面の 4 面スロット` とする。パネル本体だけを topmost 帯に置き、1 面と 4 面は通常帯のまま扱う。最大化開始直後に1面を topmost 帯へ移すと DWM のズームアニメーションが乱れ、白いちらつきや表示のごちゃつきが発生するため禁止する。アニメーション完了後に各4面を `SetWindowPos(tile, focusHandle, ...)` で1面の直後へ相対配置する。4 面は `HWND_BOTTOM` へ送らず、タイル同士の画面の覆いを保つ。
- 2026-07-22 の回帰修正: フォーカス中の `ArrangeCore` で座標・サイズ変更と Z 順変更を同じ `SetWindowPos` 要求へ統合してはならない。Electron / VS Code のサーフェス再描画がズーム中に表へ出て白ちらつきが増える。配置は従来どおり `SWP_NOZORDER` で行い、`PrepareFocusTransitionBackdrop` による旧1面の覆いを維持する。
- 非同期配置の後勝ち対策は、遷移完了後の読み取り検査で行う。`WindowArranger.IsAbove` で同面の4面が1面より上にあるか確認し、実際に違反した窓だけを `PlaceDirectlyBehind` で非アクティブのまま戻す。正常時は `SetWindowPos` を呼ばず、短い遅延検査と周期更新の両方で `パネル本体 > 1面 > 4面` を回復する。フォーカス窓の反復前面化や topmost 化は行わない。
- パネル本体の終了は確認ダイアログを通し、既定選択を「いいえ」にする。タイトルバーの X や Alt+F4 の誤操作を即終了にしない。OS のシャットダウン/サインアウト時だけ確認を省略し、終了要求・キャンセル・確定を `panel.log` に残す。
- パネル終了確認を含むユーザー向け確認 UI に WPF 標準 `MessageBox` を使わない。OS 標準の灰色ダイアログは本体の緑基調デザインと統一できないため、`MainWindow.xaml` 内の既存オーバーレイパターン（暗い半透明背景、`SurfaceBrush`、`AccentBrush`）で表示する。
- 新規VS Codeの起動前は、AIチャット等を表示する右側の `auxiliaryBarWidth` を基準画面幅の約28%（既定538px、420～620pxの範囲）まで必要時だけ広げる。既に広いユーザー設定は縮めない。`auxiliarySideBarWidth` が既に存在する形式では同じ下限を適用するが、未使用環境へ新しい値を勝手に追加しない。フォーカス直前やVS Code以外のウィンドウには適用しない。
- 新規VS Codeの起動時は、WinEventで新しいHWNDを捕捉した時点で `DWMWA_CLOAK` を設定し、2x2配置と160msの初回描画猶予が完了してから解除する。これによりElectronの白い初期サーフェスや中央の初期位置を見せない。再接続した既存ウィンドウはクロークしない。配置処理が失敗しても12秒後に必ず解除するフェイルセーフを持つ。右側 `auxiliaryBarWidth` の拡張も新規ウィンドウ起動前に済ませ、フォーカス直前に `storage.json` を変更して4面を再描画させない。

## 10. VS Code プロファイルとディスク消費（2026-08-21 追加）
- **問題**: スロット別に `--user-data-dir` を切ると、設定だけでなく Cache / WebStorage / CachedExtensionVSIXs / agent-host まで 4 コピーになり数 GB を消費する。逆に標準プロファイルを共有するとディスクは 1 倍だが、`storage.json` を稼働中に書き換えるとチャットモデル選択やサインイン状態が巻き戻る。
- **対策**: 既定は `UseDedicatedUserDataDirs=false` と `InheritMainUserState=false`。ウィンドウ別レイアウトは `slots.json` の `PreferredLayout` に保存する。`storage.json` は対象キーだけを触り、共有プロファイルでは VS Code が生きている間は書かない。専用プロファイルでも稼働中スロット自身の `storage.json` は書かない（白飛びと巻き戻し防止）。
- **ファイル**: `AppConfig.cs`, `SlotUserDataPaths.cs`, `VscodeLayoutState.cs`, `VscodeWorkspaceState.cs`
- **inherit**: 専用プロファイルへ引き継ぐのは settings / keybindings / snippets / prompts / mcp.json のみ。`globalStorage` と Chromium ストレージはコピーしない。
- **回収**: 共有プロファイル運用では `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data` の残存専用プロファイルを起動時に削除する。専用運用でも各スロット起動前に消すのは再生成キャッシュだけ。
- **必ず残すもの**: `User`（settings / keybindings / globalStorage / workspaceStorage）、`WebStorage`、`Partitions`、`Local Storage`、`Session Storage`。サインイン状態とチャット履歴は消さない。毎回の再ログインを発生させない。
- **消してよいもの**: `Crashpad`（実行中でも可）、Cache / CachedData / CachedExtensionVSIXs / logs / agent-host / GPUCache など再生成できるキャッシュのみ。VS Code プロセスが一つも無いときだけ重いキャッシュを削除する。初回起動はキャッシュ再生成のため少し遅くなることがある。
- **注意**: 専用 user-data-dir を再有効化するとディスクが増える。ウィンドウ識別のため必要でも、キャッシュのコピーや `globalStorage` 丸ごとコピーは戻さない。フォーカス切替中に `storage.json` を書かない。VS Code 実行中は共有プロファイルのキャッシュを触らない。

## 11. 管理ウィンドウの終了確認とメモリ安全性（2026-08-25 追加）
- `PostMessage(WM_CLOSE)` の成功は終了完了ではない。個別閉じる、一括閉じる、アプリ切替、控え移動、パネル情報削除は `ManagedWindowCloseService.CloseAndWaitAsync` を通し、HWND の消滅確認後だけ `StatusStore.ClearWindow` を行う。
- 外部 IDE が未保存確認や終了処理で8秒以内に閉じなかった場合は、対象 HWND を管理したまま操作を中止する。旧 Antigravity が残った状態で VS Code を追加起動したり、パネルだけ未起動表示へ変えたりしない。
- 一括閉じるは全対象へ並列に close 要求と終了確認を行う。終了確認できたスロットだけをクリアし、未終了スロットは管理を継続する。
- メモリ削減目的で外部 IDE へ `EmptyWorkingSet`、`--disable-gpu`、`--disable-extensions`、プロセス強制 kill を既定適用しない。一時的な working set 減少より、ページフォルト増加、描画回帰、AI拡張停止、未保存データ消失のリスクが大きい。
- 4ウィンドウ時の安全な基本策は、不要な管理ウィンドウを確実に閉じることと、既定の `UseDedicatedUserDataDirs=false` を維持してプロファイルを共有すること。後者は主にディスク重複を防ぐもので、Electron renderer / extension host の実メモリは各IDEの利用内容に依存する。

## 12. ワークスペースタイトル照合（2026-08-27 追加）
- **ファイル**: `VscodeWorkspaceState.cs`, `TurtleAIQuartetHub.Panel.Tests/VscodeWorkspaceStateTests.cs`
- **問題**: 共有プロファイルの `workspaceStorage` 候補を `windowTitle.Contains(フォルダ名)` で採用していた。`turtle-20250502-gem.md - zenn-contents - Visual Studio Code` のようなタイトルだと、ファイル名中の `2025` が別フォルダ `2025` に先にマッチし、開いている `zenn-contents` ではなく `2025` が `SavedWorkspacePath` へ保存された。再起動すると間違ったフォルダが開く。
- **対策**: タイトルからアプリ名接尾辞を除き、` - ` 区切りの段と照合する。完全一致、SSH などの ` [..]` / ` (..)` 接尾辞、パス末尾を優先し、部分文字列ではマッチさせない。複数候補がある場合はより具体的なフォルダ名を選ぶ。`.code-workspace` は二重拡張子として名前から取り除く。
- **注意**: `Contains` によるタイトル照合を戻さない。短いフォルダ名（`hub`, `Code`, `2025`）は長いワークスペース名やファイル名の一部に現れやすい。回帰テストは `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj` に含める。

## 13. VS Code ユーザー設定の共有（2026-08-31 追加）
- **ファイル**: `VscodeUserSettings.cs`, `SlotUserDataPaths.cs`, `AppConfig.cs`, `MainWindow.xaml`
- **経緯**: 以前はスロット別 `--user-data-dir` が既定で、ウィンドウ識別（`code.lock`）と `window.restoreWindows=none`（余分な窓の復元防止）のためにプロファイルを分けていた。副作用として `User/settings.json` もパネルごとに分かれ、プロキシ切替を 4 回する必要があった。2026-08-21 にディスク消費対策で共有プロファイルが既定になったが、専用プロファイルを再有効化すると設定が再分裂する。
- **今回の判断**: ユーザー向け設定（プロキシ、テーマ等の `settings.json`）は一つにまとめる。過去問題の再現を避けるため、次は分けたままにする。
  - ウィンドウ別レイアウト（サイドバー幅）は `slots.json` の `PreferredLayout`。`storage.json` を稼働中に書かない。
  - サインイン / チャット履歴は `globalStorage` と WebStorage をコピーしない、消さない。
  - Cache / CachedExtensionVSIXs の 4 複製は戻さない。専用 user-data の既定も `false` のまま。
  - `window.restoreWindows=none` は専用プロファイルにだけ書く。共有の `%APPDATA%/Code/User/settings.json` には書かない（通常 VS Code の窓復元を壊すため）。
- **対策**: 歯車設定の「全パネルへ適用」はハブ設定へ保存する。専用プロファイルがある場合だけ、その `http.proxy` / `http.proxySupport` / `http.noProxy` を更新する。オープンネットワークは専用プロファイル側で `http.proxy=""` と `http.proxySupport=off`。プロキシ環境は指定 URL と `override`。標準の `%APPDATA%/Code/User/settings.json` は読んでも書かない。起動時は `ManageVsCodeUserSettings` が true のときだけ VS Code プロセスへ `HTTP_PROXY` / `NO_PROXY` を渡す。
- **注意**: `storage.json` の一括上書き、Cache のコピー、共有プロファイルへの `restoreWindows=none` 強制は戻さない。既に開いている VS Code ではプロキシ反映にウィンドウ再起動が必要な場合がある。ワークスペースの `.vscode/settings.json` は触らない。標準の VS Code `settings.json` をハブから書き換えない。2026-09-03 以降、専用プロファイルでも `settings.json` だけを Roaming へハードリンクする。これは「ユーザー設定は一つにまとめる」判断に沿う。`User` フォルダ全体・`storage.json`・`globalStorage`・Cache の共有やコピーは戻さない。

## 14. SSH 接続名を含むワークスペース照合（2026-09-01 追加）
- **ファイル**: `VscodeWorkspaceState.cs`, `TurtleAIQuartetHub.Panel.Tests/VscodeWorkspaceStateTests.cs`
- **問題**: 異なる SSH 接続先で同名フォルダを開くと、フォルダ名の一致スコアが同点になり、`workspaceStorage` で先に並んだ古い `vscode-remote://ssh-remote+...` URI を現在値として保存することがあった。次回起動でも古い SSH 接続名を再利用するため、廃止済み接続先では接続に失敗する。
- **対策**: VS Code タイトルに `[SSH: 接続名]` が見える場合は、URI authority の `ssh-remote+接続名` と一致しない候補を除外し、一致する候補へ最優先スコアを与える。選択された現在 URI は既存の `StatusStore` 経路で `Path` / `SavedWorkspacePath` / `slots.json` へ保存する。
- **注意**: 2026-08-27 のフォルダ名・ファイル名誤判定防止を維持し、単純な `Contains` へ戻さない。カスタム `window.title` で `[SSH: ...]` が出ない場合はフォルダ名照合へフォールバックし、候補を根拠なく破棄しない。Remote-SSH の履歴や認証情報、`workspaceStorage` 自体は削除しない。

## 15. パネル別 settings.json の保持（2026-09-03 追加）
- **ファイル**: `VscodeUserSettings.cs`, `SlotUserDataPaths.cs`, `VscodeLauncher.cs`, `TurtleAIQuartetHub.Panel.Tests/VscodeUserSettingsTests.cs`
- **問題**: 専用 user-data の各パネルで Ctrl+Shift+P から `settings.json` を編集して保存しても、次回起動がファイル全体を JSON として読み直して書き戻していた。社内プロキシ切替用のコメント、`remote.SSH.httpsProxy`、`remote.SSH.remotePlatform` が消えて「設定が戻った」ように見える。SSH 接続中の別 PC（例: `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data/A/User/settings.json`）で再現する。
- **対策**: 専用プロファイルでも `User/settings.json` は `%APPDATA%/Code/User/settings.json`（Roaming）と同じ実体へハードリンクする。通常起動の VS Code で保存したプロキシが、ハブ起動窓でもそのまま使われる。リンクできた場合は Roaming へ `window.restoreWindows` を書かない。リンク前の専用ファイルは `settings.json.quartet-bak` に退避する。リンクできないときだけ専用ファイルを JSONC パッチし、コメントと `remote.SSH.*` を残す。
- **注意**: ハードリンクは Roaming の内容を書き換えない。JsonNode で読み書きするとコメントが落ちるため、専用プロファイルの既存ファイルには使わない。ワークスペースの `.vscode/settings.json` は触らない。

## 16. 縮小表示の IDE 切り替えと控え（2026-09-27 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **IDE 切り替え**: 縮小スロットの単一トグルは `WindowSlot.CompactIdeToggleLabel` で選択中の V / A を表示し、`CompactIdeToggleTargetApplicationId` で反対側を選ぶ。IDE 以外の選択中は中立記号を示し、利用可能な IDE を選ぶ。通常表示と同じ `SelectSlotWorkspaceApplicationAsync` を通し、未起動時は選択だけ変更、起動中は `CloseSlotWindowForReplacementAsync` の終了確認後に起動する。内側のボタンの Click は `Handled` にして、外側カードのフォーカス切り替えへ伝播させない。
- **控え**: 縮小表示では通常表示と同じ `StoredPanelPages` / `SelectedStoredPanelPage` を使い、控えの選択・登録・削除・A-D への表示・整列・ドラッグ移動・タブ名編集を小型の 2x2 カードで提供する。開閉は縮小表示の幅を変えず、高さだけを `GetCompactModeHeight` で再計算する。通常表示の Expander 状態と標準サイズの記憶は分けて維持する。
- **注意**: Windows 補助アプリボタン行は縮小表示の幅に合わせている。ボタンやタブの固定幅を増やす場合は、縮小表示の最小幅 430 DIP での収まりと DPI 変換後の高さを確認する。
- **選択表示と状態表示**: 以前の二つの V / A ボタンは Style 継承により同時点灯した。現在は単一トグルと状態ボタンが寸法と細枠の共通 Style だけを継承し、IDE 選択色と起／停の `WindowStatus` 色を独立させる。`Ready` は管理対象 HWND を確認した緑の「起」、`Missing` は赤の「停」、起動遷移中は「…」とする。状態ボタンの終了／起動操作は維持し、ツールチップに明示する。
- **控えトグルの幅**: 最小幅 430 DIP ではフレームと行の余白を引いた補助アプリ行が数 DIP しか余らない。控えボタンは 60 DIP の透明なヒット領域に丸い矢印アイコンと「控え」だけを置き、閉じる／開くの長文を `Content` に再設定しない。負の左マージンと増やした右マージンでボタンだけを外枠側へ 6 DIP 寄せ、後続の Windows アプリの位置は維持する。開閉は矢印の向き、ツールチップ、Automation 名で示す。
- **IDE 選択色**: `MainWindow.xaml` の `VsCodeSelected*Brush` / `AntigravitySelected*Brush` を標準表示の IDE 専用スタイルと縮小表示の単一トグルで共有する。VS Code は暗い青背景＋ロゴに近い青の枠（`#102D3F` / `#168FD6`）と明るい青文字、Antigravity は暗い紫背景＋暖色→緑→水色→青紫の中間的な明度の `LinearGradientBrush` 枠と少し落ち着かせた文字色を使う。縮小 A の枠は 1.2 DIP にして多色の変化を視認できるようにする。CLI は従来の緑を維持する。標準 IDE 専用テンプレートはホバー中も選択色を保つ。未検出時の灰色を優先し、ディスプレイ位置を示すフォーカス枠色とは独立させる。

## 17. 配布用 exe の発行（2026-09-27 追加）
- **ファイル**: `publish.bat`, `README.md`, `TurtleAIQuartetHub.Panel.csproj`
- **手順**: リポジトリ直下の `publish.bat` は自身の場所へ移動してから `dotnet publish` を実行する。Release / win-x64 / self-contained / single-file を指定し、`dist/turtle-ai-quartet-hub/` に `TurtleAIQuartetHub.exe` を生成する。ダブルクリック時は結果を読めるように待機し、自動確認には `--no-pause` を使う。
- **配布**: exe に加えて `LICENSE.txt` と `config/turtle-ai-quartet-hub.example.json` を出力フォルダごと渡す。単一ファイル publish を繰り返すと内容ファイルが配布先から消えるため、プロジェクトの `AfterTargets="Publish"` で 2 ファイルを物理ファイルとして再コピーする。バッチでも publish 後に毎回コピーして存在を確認する。`CopyToPublishDirectory=IfDifferent` / `Always` だけでは連続発行時の欠落を防げなかった。
- **NuGet 復元**: `publish.bat` は `win-x64` / `SelfContained=true` / `PublishSingleFile=true` の条件で明示的に restore してから `publish --no-restore` を行う。通常の設定で復元できなければ公式 `https://api.nuget.org/v3/index.json` を指定して一度再試行する。`NuGetAudit=false` で発行時の監査用通信を省き、通常ソースの `RestoreIgnoreFailedSources=true` で利用できないソースを警告に留める。自己完結型の .NET ランタイムパックを取得できない `NU1100` などは失敗を維持し、ソース一覧と接続確認先を表示する。TLS 検証は無効化しない。

## 18. Smart App Control 向け配布署名（2026-09-27 追加）
- **現象と原因**: `dist/turtle-ai-quartet-hub/TurtleAIQuartetHub.exe` は通常の `dotnet publish` では未署名となり、この PC の CodeIntegrity Operational ログに ID 3033 / 3077 の署名要件違反として記録された。Zone.Identifier はなくても Smart App Control はブロックする。開発用 `AIUsageChecker Dev` 証明書は自己署名で、配布の信頼には使わない。
- **発生時期の調査**: Smart App Control の強制ポリシーは少なくとも 2026-08-25 に別の未署名バイナリをブロックしていた。今回の `dist` の単一ファイル exe は 2026-09-27 16:13 に更新され、同じパスがブロックされた最初の記録は 16:28。ユーザーは以前も同じ `dist` のパスから起動できていたため、パスの違いは原因ではない。旧ファイルは上書き済みでハッシュを比較できないが、Smart App Control はクラウド判定で未署名 exe を許可する場合があり、新しいビルドの評価が変わった可能性がある。
- **手順**: `publish.bat` の通常発行では `scripts/Sign-Release.ps1 -Mode Inspect` が署名状態を表示する。認証局発行のコード署名証明書がある場合だけ、`TURTLE_CODE_SIGN_THUMBPRINT` と `TURTLE_CODE_SIGN_TIMESTAMP_URL` を指定し `publish.bat --sign` を実行する。発行前に証明書の EKU・秘密鍵・有効期限・チェーン・失効を検査し、発行後に SignTool の署名・検証と Authenticode の状態確認を行う。
- **通常発行の確認**: 署名状態の表示に失敗しても発行済みファイルは成功として扱い、配布前の手動確認を促す警告を出す。`--sign` では署名・検証の失敗をエラーとして維持する。
- **注意**: 自己署名や Windows 全体の保護解除を解決手段として扱わない。CA 署名でも実機上の Smart App Control 判定を確認する。Store の MSIX 配布は Store 側の署名が必要で、ローカル MSIX の自己署名とは別物。

## 19. 極小表示からの直接切り替え（2026-09-27 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **操作**: 極小表示の上部には標準表示アイコンと縮小表示アイコンを別々に置く。それぞれ `SetDisplayMode` を通して切り替え、フォーカス再前面化の抑制とパネルのアクティブ化を従来の切替ボタンとそろえる。
- **寸法**: 極小表示は 116×116 DIP のまま。左右 8 DIP の内側余白と両側 1.75 DIP の緑枠を引くとタイトル行の有効幅は 96.5 DIP。表示切替・最小化・閉じるの 4 ボタンを各 23 DIP、合計 92 DIP にして収める。強調枠 2.25 DIP のときも収まる。標準・縮小表示のボタン幅と左右マージンは従来値に戻す。

## 20. Windows 補助アプリの表示選別（2026-09-28 追加）
- **ファイル**: `Models/LauncherApplication.cs`, `Models/AppConfig.cs`, `Services/StatusStore.cs`
- **対策**: `ShowInAuxiliaryLauncher` で `chatgpt-app` だけを表示対象から外す。`StatusStore` の初期生成時と設定再読み込み時の両方に同じ条件を使い、標準・縮小表示と Jump List を同期する。旧 ChatGPT 設定は破棄しない。
- **Codex 表記**: `codex-app` の既定 `ShortName` は `GPT/Codex`。旧設定の `ShortName=Codex` は正規化時に移行し、ID・検出条件・起動処理は維持する。
- **注意**: 2026-09-28 より前の「4 ボタン」の記述は変更前の仕様。縮小表示の幅 430 DIP で新しい 3 ボタン行が収まることを確認する。

## 21. 控えと AI アプリボタンの左右配置（2026-09-28 追加）
- **ファイル**: `MainWindow.xaml`
- **対策**: 縮小表示の `CompactStoredPanelsToggleButton` を右側 StackPanel から独立させ、`StoredPanelsRowGrid` の左端へ配置する。標準表示の控え Expander は既存どおり左に置く。共用の `AIアプリ` ラベルと起動ボタンは右揃えにする。
- **寸法**: 3 ボタンは `AuxiliaryApplicationButtonStyle` で幅 86 DIP・高さ 28 DIP・文字 11.5 DIP に統一し、`AIアプリ` ラベルは文字 11 DIP にする。最小幅 430 DIP の縮小表示で左右の操作が重ならないことを確認する。

## 22. 縮小表示の AI アプリボタン寸法（2026-09-28 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **対策**: `AuxiliaryApplicationPanel.Tag` に表示モード名を渡し、共通ボタンスタイルの `DataTrigger` で `Compact` のときだけ幅 78 DIP・高さ 25 DIP・文字 10.5 DIP にする。標準表示は 86×28 DIP・文字 11.5 DIP のまま。
- **注意**: 幅と `MinWidth` を同時に変更しないと、継承した最小幅で縮まない。モード切替時は高さの測定前に `Tag` を更新する。

## 23. 縮小表示の控えカードの割り当てボタン（2026-09-29 追加）
- **ファイル**: `MainWindow.xaml`
- **配置**: `CompactStoredPanelTemplate` の A-D ボタンは `UniformGrid Columns="2"` で上段 A/B、下段 C/D とする。カード幅を変えず、各ボタンの横幅を確保する。
- **高さ**: ボタン行は 46 DIP、各ボタンは高さ 21 DIP とし、縮小表示の控えを開いたときは既存の `GetCompactModeHeight` による実測でウィンドウを縦に広げる。
- **文字**: 控えトグルとカード番号は 11 DIP、タイトルは 11 DIP、フォルダ名は 10.5 DIP。タイトルは番号の横で縦中央に揃え、利用可能な幅に応じて省略表示する。標準表示の控えカードと割り当て処理は変更しない。

## 24. 縮小・極小表示のスロットカード入替（2026-09-29 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **操作**: `CompactSlotTemplate` と `MicroSlotTemplate` の外側 Button を標準カードと同じ `SlotCard_*` ドラッグ処理へ接続する。ドロップ後は `SwapSlotContents` と `ArrangeSlotsAfterPanelStateChangeWithSettlingAsync` を共用し、カードと管理ウィンドウの位置を同期する。
- **誤操作防止**: 外側 Button はドラッグ開始対象、縮小カード内側の IDE / 起動ボタンは対象外。押し始めた要素を記憶し、内側ボタンからポインタが外れても入替を開始しない。ドラッグ終了直後の外側 Button の Click はフォーカス切替に使わない。
- **表示**: ドロップ先の光彩は DragLeave / Drop で解除する。標準カードの `FocusFrameBrush` Binding を維持する。

## 25. 標準表示の通知メッセージ（2026-09-30 追加）
- **ファイル**: `MainWindow.xaml`
- **表示**: `FooterControlsGrid` の `Message` は文字サイズ 14 DIP、上下中央揃え、`TextWrapping="Wrap"` で表示する。幅に収まらない場合は複数行にし、親の Auto 行で高さを確保する。固定高さや省略表示を追加しない。

## 26. 標準表示の通知文字サイズを復元（2026-10-01 追加）
- **ファイル**: `MainWindow.xaml`
- **問題**: 通知文字を 14 DIP に拡大したことで、長い設定パスの折り返しが増え、通知枠が高くなり画面バランスが崩れた。
- **対策**: `FooterControlsGrid` の `Message` を元の 10 DIP に戻す。上下中央揃え、`TextWrapping="Wrap"`、既存の余白と Auto 行による高さ調整は維持する。
- **注意**: ユーザーのサイズ復元依頼により、セクション 25 の 14 DIP 指定はこの項目で置き換える。枠を詰めるために固定高さや省略表示を追加せず、文字サイズで調整する。

## 27. 縮小表示の前後ボタンと AI アプリ寸法の復元（2026-10-02 更新）
- **ファイル**: `MainWindow.xaml`
- **前後操作**: 「前」「背」は幅・高さを 24 DIP に固定し、`MinWidth` / `MinHeight` を 0、Padding を 0、縦位置を Center にする。縮小カードの外側 Button は 28 DIP 高だが、上下各 2 DIP の透明フォーカス枠を除いた可視枠は 24 DIP 高である。
- **AI アプリ**: ユーザーの撤回指示により、縮小表示の追加一括操作ボタン・専用確認パネル・対応コードを取り除き、補助アプリを第22節の元の 78×25 DIP・文字 10.5 DIP に復元した。68 DIP 幅・文字 9.5 DIP へ縮める変更は採用しない。
- **注意**: 縮小ウィンドウ寸法・倍率・高さ算出は変えない。標準表示の一括操作・終了確認も従来どおり。コードビハインドに今回の変更を残さず、前後ボタンの寸法と説明属性だけを変更する。

## 28. 縮小表示の余白と高さ測定（2026-10-02 更新）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **原因**: WPF の DesiredSize は Margin を含むが、GetCompactModeHeight は行の上下マージンを再加算し、測定幅からも左右マージンを二重に引いていた。下端に余分な空白が残り、控え表示の高さも過大になっていた。
- **対策**: 各行を本体内の共通の有効幅で測定し、DesiredSize の高さをそのまま使う。RootLayoutGrid の Margin と通常の外周枠厚だけを別途加える。通常枠厚 1.75 DIP は CompactPanelBorderThickness で描画と測定に共用する。
- **配置**: CompactBarPanel の上マージンを 0 DIP、RootLayoutGrid の下マージンを縮小時だけ 6 DIP にする。モードの高さ測定前に UpdateDisplayModeChrome で余白を確定し、標準・極小では元の Margin=8 に戻す。
- **注意**: 第27節の寸法維持は今回の余白調整前の仕様。ユーザーの追加指示により閉じた縮小表示の高さは 156 DIP から 146 DIP へ詰めた。幅、前/背の 24 DIP 正方形、AI アプリの 78×25 DIP・文字 10.5 DIP は維持する。控えを開いた高さも実測で求め、手動の補正値や Margin の再加算を戻さない。

## 29. 縮小表示の AI アプリボタンの高さ（2026-10-02 更新）
- **ファイル**: `MainWindow.xaml`
- **対策**: ユーザーの高さ調整指示により、AuxiliaryApplicationButtonStyle の Compact トリガだけを高さ 25→22 DIP にした。継承した BarButton の最小高さ 24 DIP に阻まれないよう MinHeight も 22 DIP にする。
- **維持**: 横幅 78 DIP・文字サイズ 10.5 DIP・文字スタイル・配色はそのまま。標準表示の 86×28 DIP・文字 11.5 DIP と本体の余白設定は変更しない。第27・28節の縮小 AI アプリ高さ 25 DIP はこの調整で置き換える。
- **確認**: 本体ビルド成功。430×146 DIP の縮小表示で GPT/Codex・Claude・Antigravity2 が各 78×22 DIP・文字 10.5 DIP となり、文字・ボタンの見切れがないことを描画と実測で確認した。
- **追加調整**: ユーザーの「まだ目立つ」という指示を受け、高さと MinHeight を 22→18 DIP にした。横幅 78 DIP・文字サイズ 10.5 DIP・本体サイズ 430×146 DIP は維持する。本体ビルド成功、100% / 125% の描画で文字とボタンの見切れがないことを確認した。
- **デザイン調整**: 18 DIP では薄すぎて上に寄るという指摘を受け、高さと MinHeight を 20 DIP に戻した。縮小時の StoredPanelsRowGrid.MinHeight を 30 DIP とし、控えの上マージンを外して縦中央、AuxiliaryApplicationsRow も上マージンを外して縦中央へ揃える。標準表示では補助アプリ行の元の上揃え・マージンを維持する。幅 78 DIP・文字 10.5 DIP・本体 430×146 DIP を維持し、左右の操作と AI ラベル・3 ボタンの中心が同じ高さになること、控え表示の収まり、標準表示の描画一致、100% / 125% の見切れなしを確認した。
- **上下余白と厚みの再調整**: ユーザーの「もう少し太く、上下のスペースを半分」という指示により、縮小 AI ボタンの Height / MinHeight を 20→22 DIP にした。StoredPanelsRowGrid.MinHeight は 26 DIP、上下 Margin は各 2 DIP、RootLayoutGrid の下 Margin は 2 DIP とし、CompactWindowMinHeight を 136 DIP に下げて余分な高さを残さない。100% 描画でボタン行の見える上余白は 7→4 DIP、下余白は 16→8 DIP、本体は 430×137 DIP になる。控え・AI ラベル・3 ボタンは縦中央を共通の位置に揃え、幅 78 DIP・文字 10.5 DIP・左右余白を維持する。直前の本体高さ 146 DIP と余白設定はこの項目で置き換える。ビルド成功、100% / 125% の見切れなし、控えを開いた表示の収まり、標準表示の描画一致を確認した。

## 30. 縮小表示の控え開閉とボタン位置の固定（2026-10-02 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **問題**: ユーザー環境で控えを開くとボタンが少し上へ動くと報告された。下端の余白も追加するよう依頼された。
- **対策**: `CompactBarPanel` は上揃えにし、縮小時の `StoredPanelsRowGrid` は Height / MinHeight を 26 DIP に固定して上揃えにする。行内の控えと AI アプリは従来どおり縦中央へ置く。`RootLayoutGrid` の縮小時下 Margin は 2→6 DIP とし、下端に 4 DIP の余白を追加する。100% の閉じた表示は 430×141 DIP、控え表示は現在の保存内容で 430×384 DIP。
- **維持**: AI アプリの 78×22 DIP・文字 10.5 DIP、前/背の 24 DIP 正方形、横幅と左右余白を維持する。標準表示へ戻したときは操作行の Height を Auto、MinHeight を 0、縦配置を Stretch に戻す。第29節の下 Margin=2 DIP と本体高さ 137 DIP は今回の依頼で置き換える。
- **確認**: 100% で実際の控え開閉ハンドラを3往復実行し、既存23ボタンの座標・寸法変化と見切れが0件。WPF 自体の DPI を125%にした開閉描画でも同じ23ボタンの座標・寸法が一致する。標準・極小の125%描画は修正前と同一。画面下端付近のウィンドウ全体の作業領域補正は既存の挙動を維持する。

## 31. タイトル編集と設定一覧の挿入移動（2026-10-02 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`, `Services/StatusStore.cs`, `TurtleAIQuartetHub.Panel.Tests/PanelOrderTests.cs`
- **タイトル**: `InlineTitleTextBox` は読み取り時に `DisplayTitle`、編集時に `PanelTitle` を表示する。Binding は `UpdateSourceTrigger=Explicit` とし、確定時だけ保存する。Esc は UpdateTarget で元の値を戻す。タイトルの最初のクリックも Handled にして外側カードの起動・フォーカス切替を防ぐ。設定用の通常TextBoxへインライン編集の終了処理を適用しない。
- **並び替え**: メインカードの既存D&Dは交換のまま維持する。設定一覧の上下操作とD&Dは `MoveSlotContents` / `MoveStoredPanelContents` を通す挿入移動とし、移動区間の他カードの順序を保つ。位置番号・コレクション・控えページの参照は固定し、内容の隣接交換中は保存を抑制、完成後に一度だけ保存する。
- **実行情報**: `SwapSlotContentsCore` はタイトル・パス・アプリだけでなくHWND、`RuntimeSlotName`、フォーカス、非表示、ディスプレイ割当、レイヤー、レイアウト、ワークスペース読取時刻を一緒に移す。設定の移動後は非表示からのフォーカス復帰リストも RuntimeSlotName で追従させ、既存のフォーカスを保つ配置処理を共用する。
- **UI**: 設定のドラッグ開始は専用つまみだけに限定し、入力欄の文字選択・上下キーを妨げない。移動後は移動先のつまみへフォーカスして連続キー移動を可能にする。表と控えの間のドロップは受け付けない。控え一覧の端でドラッグ中にスクロールする。設定ダイアログの内側Gridを本体高さから78 DIP引いた範囲に制限し、下部操作が見切れないようにする。
- **配置**: 標準の控え見出し領域は28 DIP。AIアプリ行の上Marginを0にして見出しと同じラインにする。AIアプリは標準86×28 DIP・縮小78×22 DIPのまま。

## 32. 設定一覧の並び替えUIの意匠・操作性刷新（2026-10-02 追加）
- **ファイル**: `MainWindow.xaml`
- **問題**: 設定一覧の並び替えボタン（テキストの「↕」「↑」「↓」が上下に並ぶ構成）がアプリ全体のダークグリーン・ネオングリーンのサイバーなテイストから浮いており、バランス・視認性・操作感に課題があった。
- **対策**:
  - 幅32 DIPの一体型縦スプリットステッパーコンテナ（`SettingsOrderContainerStyle`）へ刷新。
  - 上ボタン: Segoe MDL2 Assets の `&#xE70E;` (ChevronUp)。
  - 中央つまみ: モダンな6ドットグリッパー（2列×3行）。ドラッグ移動に対応し、選択時はアクセント枠とネオングリーン発光で ↑/↓ キーによる並び替えが直感的に行える。
  - 下ボタン: Segoe MDL2 Assets の `&#xE70D;` (ChevronDown)。
  - ホバー・押下・フォーカス時のカラーはすべてアプリの `SurfaceBrush`, `AccentBrush` (`#45D483`), `#1F3427` と調和。
  - 行内の左端列幅を 58→44 DIP に最適化し、入力欄（名前・パス等）の横幅を拡大。
- **維持**: `SettingsMovePanelButton_Click`、`SettingsPanelOrder_PreviewKeyDown`、`FocusSettingsPanelGrip` のイベントハンドラ・タグ参照・フォーカス探索仕様（`Cursor=SizeAll`、`Tag={Binding}`）を完全互換のまま維持。

## 33. ウィンドウの直接操作とフォーカス同期（2026-10-12 追加）
- **ファイル**: `MainWindow.xaml.cs`, `Services/WindowFocusSynchronizer.cs`, `Services/WindowArranger.cs`, `TurtleAIQuartetHub.Panel.Tests/WindowFocusSynchronizerTests.cs`
- **問題**: 直接最大化が `IsFocused` へ反映されず、旧フォーカスの Z 順補正や起動後の配置リトライが手動最大化を取り消していた。直接復元も再最大化され得た。
- **対策**: HWND ごとの最大化への変化を自動補正より先に取り込み、同じディスプレイの旧フォーカスだけを解除する。420msの遷移猶予後に `ArrangeOnly` で対象だけを非アクティブ・アニメーションなしで象限へ戻す。最小化窓は復元しない。直接復元は自分の象限へ戻し、直接最小化は従来どおりフォーカスだけ解除する。
- **非同期制御**: 手動切替は古い遅延整列・再適用・起動後リトライをキャンセルする。未完了の復元HWNDを引き継ぎ、すでに最大化している旧窓を再採用しない。パネルからの最大化要求中は同期を保留し、ディスプレイ移動中の一時的な通常状態は入力抑制の猶予後に判定する。
- **注意**: 同期は既存の状態更新間隔（既定約1秒）で行う。フォーカス変更を取り込む前のZ順補正・全窓の再配置は禁止。通常Z帯と `SWP_NOZORDER` の配置、Win32要求の非同期化を維持する。直接操作に伴う整列で別ディスプレイや最小化窓を復元しない。過去の「手動最大化を取り消す」要望は確認したGit履歴・資料には見つからず、2026-06-07の手動最小化尊重を最大化・復元にも広げる判断。
- **確認**: 回帰チェック74件（既存62＋追加12）と、表示しないWPF本体・模擬状態による統合チェック12件が合格。本体ビルドは警告・エラー0。実IDEのアニメーション・表示順の目視確認は未実施。経緯と実機手順は `docs/investigations/manual-window-focus-analysis-2026-10-12.md` を参照。

## 34. メイン4カードのタイトル視認性向上とシステムモニタ（2026-10-19 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`, `Models/SystemMonitorModels.cs`, `Services/SystemMonitorService.cs`, `TurtleAIQuartetHub.Panel.Tests/SystemMonitorAndCardTitleTests.cs`
- **カードタイトル視認性向上**:
  - `MainWindow.xaml.cs` の `IsInlineTitleTextBox` は `ReferenceEquals(style, TryFindResource("InlineTitleTextBox") as Style)` で参照同一性を判定しているため、`SlotCardTemplate` 側のタイトル `TextBox` でも `Style="{StaticResource InlineTitleTextBox}"` はそのまま保持し、派生スタイルへ差し替えない。
  - `InlineTitleTextBox` の `ControlTemplate` 内の `TitleDisplay`（読み取り表示用 `TextBlock`）に `FontSize="{TemplateBinding FontSize}"`、`FontWeight="{TemplateBinding FontWeight}"`、`Foreground="{TemplateBinding Foreground}"` をバインドし、`SlotCardTemplate` のタイトル `TextBox` に `FontSize="15.5"`、`FontWeight="Bold"`、`Foreground="#F8FCF9"`、`Padding="2,0"`、`MinHeight="22"` を指定した。
  - `ShortPath` の Margin を `0,2,0,0` から `2,0,0,0` に詰め、ヘッダ `StackPanel` の高さを従来の 40 DIP 以内（約 39 DIP）に収めることで、カード高さ（169 DIP）やアプリ全体のウィンドウサイズ（650×535 DIP）を一切圧迫・拡大せずにタイトル文字のみを大きく太字化した。
- **システムモニタ表示（アイコン＆モーダル）**:
  - タイトルバーの `SettingsButton`（歯車アイコン）のすぐ左に `SystemMonitorButton`（`Content="&#xE9D9;"`、`Segoe MDL2 Assets`）を配置し、標準表示時のみ表示する。
  - 既存のウィンドウ内モーダルパターンに従い、`RootLayoutGrid` 内に `SystemMonitorOverlay`（`Grid.RowSpan="5"`、`Panel.ZIndex="23"`、`Esc` キー閉鎖対応）を実装。
  - メモリ使用量（Win32 `GlobalMemoryStatusEx`）、CPU使用率（Win32 `GetSystemTimes` による前回サンプルとの差分計算、起動時プライミング済み）、および各ドライブ使用状況（`DriveInfo.GetDrives()`）は、モーダル表示中のみ動作する 1.5 秒周期の `DispatcherTimer`（`_systemMonitorTimer`）でリアルタイム更新し、モーダル閉鎖時はタイマーを停止してバックグラウンド負荷をゼロにする。
  - 各フォルダ使用量（スロット A〜D、重複を除いた控えパネル、Hub の `StateDirectory`、VS Code データフォルダ、Antigravity IDE データフォルダ）は `Task.Run` と `CancellationTokenSource` で非同期に走査（`EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }`）し、完了したフォルダから順次 UI スレッドでサイズ・ファイル数・相対比率バーを更新する。モーダル閉鎖時や「再計測」押下時は即座にキャンセルする。
- **確認**: 本体ビルド警告・エラー0、回帰テスト全91件（既存74＋新規17）合格。

## 35. 巨大フォルダによる使用量計測の停滞防止（2026-10-20 追加）
- **ファイル**: `Services/SystemMonitorService.cs`, `Models/SystemMonitorModels.cs`, `MainWindow.xaml.cs`, `TurtleAIQuartetHub.Panel.Tests/FolderUsageCalculationTests.cs`
- **原因**: フォルダを逐次走査していたため、控えの `C:\` 全体の列挙が後続の計測を止めていた。項目単位の例外処理もなく、読み取り失敗で残りが「計測中」のままになった。
- **対策**: ドライブ直下は `DriveInfo.TotalSize - TotalFreeSpace` を取得して「ドライブ使用量」と表示し、ファイル数を数えない。ドライブのバーは容量に対する使用率とし、通常フォルダの相対バーと合計から除外する。通常フォルダは最大2件を並行計測し、1件3秒で「スキップ (時間超過)」、取得失敗は「計測不可」として後続を継続する。完了表示でも未計測件数を明示する。
- **中断**: `WaitAsync` で待機期限を設け、期限到達・閉鎖・再計測時は走査側のトークンもキャンセルする。OSの同期I/Oそのものは即時中断できないため、遅れて終了した結果をUIへ反映しない。通常走査はディレクトリ/項目ごとにキャンセルを確認し、ジャンクション等の再解析ポイントを辿らない。取得できない子ディレクトリは従来どおり除外するが、対象ルートの失敗を0 Bと誤表示しない。
- **注意**: 第34節の再帰列挙・逐次処理は本節で置き換える。UI更新はDispatcher上で行い、閉鎖・再計測後のキャンセルトークンと一覧の参照一致を確認する。単にタイムアウトしたTaskを放置して走査を続けたり、部分集計を確定値として表示したりしない。
- **確認**: 本体とテストのビルド警告・エラー0。回帰テスト102件（既存91＋追加11）が合格。実ドライブの容量取得と後続フォルダ、空/消失フォルダ、遅い先頭項目、並行枠2件の時間超過、読み取り例外、閉鎖/再計測に相当するキャンセルと遅延結果の破棄を確認。

## 36. VS Code CacheStorage の停止中回収（2026-11-07 追加）
- **ファイル**: `Services/SlotUserDataPaths.cs`, `Services/VscodeCacheStorage.cs`, `Services/FileSystemCleanup.cs`, `Services/VscodeLauncher.cs`, `Services/DataFolderDiagnosticsService.cs`
- **問題**: H5 のブラウザ保存領域内にある CacheStorage が直下キャッシュの回収対象外で、専用スロットごとに蓄積する。窓を閉じてもプロセスが残る環境がある。
- **対策**: 既存の起動時 Task.Run から標準・専用プロファイルの `WebStorage/<bucket>/CacheStorage`、`Service Worker/CacheStorage` と `Partitions/<partition>` 内の同じ配置を回収する。プロセスが存在する/確認不能なら見送る。清掃中も 200 ms 間隔で確認し、一度起動を検出したらその回収は中止する。ハブの VS Code 起動と清掃は `UserDataMaintenanceGate` で排他し、起動側は非同期待機する。
- **保護**: 既知の相対配置だけを許可し、ルート外と祖先の reparse point を拒否する。内部リンクを辿らず、ロック中のファイルは次回へ回す。User・Cookies・IndexedDB・未保存データ・Service Worker 登録情報は触らない。第10節の WebStorage 保持に対する例外は CacheStorage のみ。専用無効時の旧 user-data 全体回収は従来仕様だが、プロセス停止条件を追加した。
- **診断**: H5/V10/G10 に CacheStorage の内訳を表示。G10 は自動回収対象外。既存コード番号・しきい値は維持し、索引を7階層に拡張して専用 Partition 内も数える。H3 の履歴は削除対象にしない。
- **注意**: 外部起動との完全な排他はできない。強制終了や任意の CacheStorage 名の再帰検索は追加しない。別 PC 固有の増加元の拡張機能は未特定。原因・確認手順は `docs/guides/data-folder-diagnostics.md` を参照。

## 37. メモリ警告を物理メモリのバーへ統一（2026-11-07 追加）
- **ファイル**: `Services/SystemMonitorService.cs`, `MainWindow.xaml.cs`, `MainWindow.xaml`
- **原因**: メモリバーは物理使用率、ページファイルの注意文とアイコン色はコミット率を使い、47.5%使用・空き25.1 GBでも警告が出ていた。過去のページファイル拡張を最優先にしていたため、空き回復後も警告が残った。10月27日/31日の既存実装で、直前のCacheStorage対応では変更していない。
- **対策**: `AssessPagefile` にその回の `MemoryUsagePercent` を渡し、バーと同じ75%/90%で判定する。緑では警告文を空にし、ページファイル拡張・無効設定・高いコミット率だけで色を上げない。開いているモニタと背景評価の両方へ適用する。
- **注意**: ページファイルの容量と増加量は通常色で維持し、コミット値はツールチップに残す。ユーザー指定の表示方針として、コミット上限だけの接近は警告にしない。CPU・固定ドライブの警告は抑制しない。調査根拠は `docs/investigations/memory-warning-analysis-2026-11-07.md`。

## システムモニタの折りたたみと確認付き VS Code 終了（2026-11-12 追加）
- **UI**: `GroupItem` 内の `Expander` と専用スタイルで、見出し・件数・「開く / 閉じる」を表示。`_expandedSystemMonitorFolderGroups` はモニタを開くときにクリアし、分類の Loaded で復元する。Expanded / Collapsed の処理は分類自身のイベントだけを受け取る。計測の世代管理・期限・キャンセルは既存どおり。
- **確認**: `VscodeProcessRecoveryConfirmOverlay` で対象の PID・タイトル・実行パスを表示する。初期フォーカスはキャンセル、Escape で閉じて対象を破棄。一覧取得だけでは終了しない。取得中のキャンセルと次の取得の競合は CTS の参照一致とキャンセル状態で成功・例外両経路を保護する。
- **対象保護**: `Code.exe` / `Code - Insiders.exe` だけを対象とし、ユーザー SID・Windows セッション・実行パス・開始時刻・PID を終了直前に再照合する。限定照会ハンドルを保持して PID 再利用を防ぎ、子プロセスツリーは終了しない。`Process.Kill` は使わない（`PROCESS_ALL_ACCESS` を要求し、システム版や管理者起動でアクセス拒否になる。終了権限の開き方は末尾の「VS Code 終了のアクセス拒否」）。取得不能・別ユーザー・別セッション・別 IDE は対象外。
- **結果**: 終了待機は5秒で打ち切り、再検出結果を表示する。確認後の新規 PID は自動終了しない。アクセス拒否・タイムアウト・取得失敗を「プロセスなし」と区別する。実行前に保存し、実行後は管理窓を再検出して、消えた窓の非表示状態と表示ボタンを更新する。保存済みワークスペースは保持する。
- **注意**: 自動回収・通常の一括終了へ強制終了を組み込まない。強制終了は本機能の明示確認時だけで、キャッシュやユーザーデータの削除は行わない。第36節の自動キャッシュ回収における強制終了禁止は維持する。
- **検証**: フェイクによる終了対象・PID再利用・権限拒否・自然終了・残留の安全性14件を含む回帰259件。本体ビルド警告/エラー0。隔離した非表示WPFで初期全閉・開閉・一覧再生成時の復元・長文確認画面・キャンセルを確認。実ユーザーのプロセス停止は行わない。
## AI フック設定の場所一覧と読み取り専用プレビュー（2026-11-12 追加）
- **ファイル**: `MainWindow.AiActivity.cs`, `MainWindow.AiHookSettings.cs`, `MainWindow.xaml`, `Services/AiNotifyHookInstaller.cs`, `Models/AiNotifyHookLocation.cs`, `TurtleAIQuartetHub.Panel.Tests/AiHookLocationTests.cs`。
- **場所**: Installerの既存パスを `GetSettingsLocations` で一覧化し、Claudeの `~/.claude/settings.json`、Codexの `~/.codex/hooks.json`、Copilotの `~/.copilot/hooks/turtle-ai-quartet-hub.json`、Grokの `~/.grok/hooks/turtle-ai-quartet-hub.json` を示す。状態は短い語に固定し、エラー詳細はツールチップへ移す。インストール/解除後は同じ一覧を再取得する。
- **閲覧**: `ReadSettingsPreview` は既知の4ファイルだけをFileAccess.Readで取得し、原文を保ったまま最大256Ki文字に制限する。BOMを判定し、上限のサロゲートペアを壊さない。未作成・消失・読取拒否を表示用の結果で返し、JSONの整形/保存やフォルダの暗黙作成は行わない。
- **UI**: `AiHookSettingsPreviewOverlay` は設定画面の上に表示し、パスと内容を読み取り専用TextBoxで示す。縦横スクロール・選択コピーに対応。Escapeはプレビューを先に閉じ、設定画面を残す。閉鎖時は内容を消し、CTSの参照一致で古い読取結果・例外の反映を防ぐ。ファイル内容はログへ出さない。
- **維持する仕様**: 通知イベントの解釈、バッジ表示、フック追加/解除、バックアップ、Codex初回承認の案内は変更しない。実ホームへの設定変更をUI検証から呼ばない。
- **実装検証（2026-11-12）**: 場所・閲覧の追加13件を含め回帰272件が合格。本体ビルドは警告・エラー0。650/580 DIP幅の通常・エラー・補足・プレビュー・スクロール・空ファイル・読取失敗・省略・閉じる・Escapeを合成データと非表示WPFで確認。読み取り専用の編集抑止、選択、閉じる時の内容消去も確認。実ホームの設定変更やExplorer起動は検証から呼ばない。
## フォルダ分類の見出しと説明の余白（2026-11-12 調整）
- `MainWindow.xaml` の分類説明 `GroupDescriptionText` に上10 DIP・下10 DIPの余白を設け、展開見出し・説明文・フォルダカードの間を分離する。共通Expanderテンプレートの間隔には影響させない。
- 折りたたみ時の高さや開閉・計測の処理は維持する。余白だけの変更のため、本体ビルドと合成データによる非表示WPF描画で確認する。
## 半角Xだったモーダルの閉じるボタン（2026-11-12 対象範囲・中央揃えを修正）
- **対象範囲の訂正**: 変更対象は元がASCIIの `X` だった設定・ヘルプ・ログ・システムモニタ・ディスク整理・容量解析の6モーダルに限定する。元からアイコンだった本体タイトルバーと控え一覧を統一対象へ広げない。
- **既存表示を保持**: 本体はE8BB / Segoe MDL2 Assets / 文字10 / `TitleBarChromeButton`、控え一覧はE711 / 同フォント / 文字10 / 26×24 / `ChromeButton`。本体の極小幅23・通常幅32の既存制御も維持する。汎用CloseIconButtonStyleや本体専用TitleBarCloseButtonStyleは使用しない。
- **サイズと中央揃え**: `ModalCloseButtonStyle` は「再計測」の高さに合わせた30×30 DIP。対称な12×12のPath・線幅1.4で十字を描き、ボタンだけでなく記号も比例して縮小する。文字の行高・ベースラインに依存させず、描画中心をボタン中心に合わせる。既存Click/Escape/終了確認・通常フッターは維持する。新規のフックガイドの×も同じモーダル用部品を使う。
- **確認観点**: 再計測と高さが一致し、幅も同じ正方形であること。十字の描画境界の中心がボタン中心（15,15）に一致することを確認する。依頼対象外の既存アイコン・寸法・スタイルを元の値と照合する。
- **サイズ調整後の確認**: 6モーダルすべて30×30・描画中心（15,15）を確認し、既存Click結線と端寄りのヒット領域を保持。本体/控え一覧の元の値との照合、本体ビルド警告/エラー0、システムモニタと設定画面の非表示描画でバランスを確認した。
## docs 整理時の参照と図版（2026-11-12 追加）
- `README.md` と `docs/index.md` を入口にし、移動した各資料にもindexへの戻りリンクを置く。既存16資料を5分類へ移動し、新しいフックガイドを追加した。調査時点の検証結果やStore草案を、現行状態の保証として扱わない。
- ファイル移動時は、Markdownの相対リンクだけでなく、`scripts/Test-StoreReadiness.ps1` の必須ファイル4件、README/IMPLEMENTATION_PLANの案内、診断サービス/画面のコメント、overview内のパスも更新する。本文のコマンド例はリポジトリルート起点を維持する。
- フック図は自己完結SVG（外部画像・スクリプトなし）。図中の日本語と線をブラウザでレンダリングし、キャンバス外にはみ出す文字がないことを確認する。元SVGは `docs/assets/`、一時PNG/検証レポートは `.build-tmp/docs-organization/`。
- 確認: docs18 Markdown（indexを含む）とREADME/IMPLEMENTATION_PLANのローカルリンク93件で欠落0、全資料がindexに掲載、必須Store文書4件の存在とスクリプト構文に問題なし。SVG2枚はXMLと表示を確認し、外部参照/文字はみ出し0。

## 設定枠内のフックガイドとページ復帰（2026-11-12 追加）
- **ファイル**: `MainWindow.AiHookGuide.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `Views/AiHookGuideView.xaml(.cs)`。
- **ページ構成**: SettingsOverlayの同じ枠に `SettingsMainContentGrid` と `AiHookGuidePanel` を置き、Visibilityを切り替える。元の設定モデル/入力コントロールを再生成しない。3つの切替は選択状態を持つRadioButtonで、選択した本文だけを表示し、本文だけをスクロールする。
- **復帰**: Show時にSettingsScrollViewerの位置と起点ボタンを保持する。戻る/×/EscapeではLoaded優先度のDispatcherで元の位置とフォーカスを復帰する。navigationVersion、親の表示、guideの表示を照合し、閉鎖や再オープン後の古いcallbackを無効化する。設定画面そのものの開閉では `HideAiHookGuide(false)` を使う。
- **リソース**: View内の図はPath/Border/TextBlockのベクター。自己完結のStaticResourceと、親から受け取るボタン用DynamicResourceを使い分け、コンストラクター時の親リソース未解決を防ぐ。既存の共通×スタイルも再利用する。
- **説明**: 既存フック保持、Claude/Codexバックアップの更新、Copilot専用JSONの上書き/削除とバックアップなし、設定済みと疎通の違い、応答終了と成功判定の違い、未起動時再送なしを説明する。画面の閲覧からReset/Save/Install/Uninstallを呼ばない。
- **検証**: 本体ビルド警告/エラー0、既存回帰272件合格。隔離した非表示WPFで650/580幅の3頁、ラベルや図の見切れ、切替時の先頭復帰、戻る/×/Escapeで設定枠を保持、元の180DIPスクロール位置への復帰を確認。実キーボードフォーカス自体はShowしない制約のため、戻り先参照と遅延復帰経路を確認する。実フックの設定/解除・本番保存・実ウィンドウ操作は行わない。

## AI 作業通知の完了色（2026-11-12 追加）
- **ファイル**: `MainWindow.xaml`, `Views/AiHookGuideView.xaml`, `Models/AiActivityState.cs`, `Services/AiActivityTracker.cs`
- **問題**: 完了の光が作業中と同じ緑系統で、終わったことが見分けにくかった。ツール別に黄色の入力待ちを出す案は、Copilot に対応イベントが無く表示が揃わないため採用しない。
- **対策**: 作業中の緑の明滅は維持する。完了と許可待ちは同じ薄い青（塗り `#3E7EAD`、枠 `#D7EEFB`、光 `#B6DFF6`、ぼかし 16）で点滅せずに光らせる。`AiGlowColor` は作業中の緑のまま分ける。
- **注意**: 黄色の入力待ち状態は残さない。`idle_prompt` は無視したまま。画面やログから状態を推定しない。
- **確認**: `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj` が終了コード 0。回帰278件（AI 作業通知27件）が合格。青の光の目視は起動中の画面では未実施。

## VS Code 終了のアクセス拒否（2026-11-12 追加）
- **ファイル**: `Services/VscodeProcessRecoveryService.cs`, `Services/VscodeProcessRecoveryElevation.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, `MainWindow.VscodeProcessRecovery.cs`
- **問題**: 「VS Code の終了・復旧」で「アクセスが拒否された」となる。ユーザー版（`%LOCALAPPDATA%\Programs`）はハブと同じ整合性レベルなら終了できる。システム版（`C:\Program Files\Microsoft VS Code`）や管理者として起動した VS Code は高い整合性レベルになり、通常権限のハブから `PROCESS_TERMINATE` が Win32 エラー 5 で拒否される。加えて `Process.Kill` は内部で `PROCESS_ALL_ACCESS` のハンドルを保持して再利用するため、終了権限だけでは足りる相手でも拒否される。
- **対策**: `Process.Kill` と `Process.SafeHandle` を使わない。`PROCESS_TERMINATE | SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION` を開き、エラー 5 のときだけ `TERMINATE | QUERY_LIMITED`、さらに `TERMINATE` のみへ落とす。直接終了できた対象は昇格しない。拒否された対象だけを `%LOCALAPPDATA%\TurtleAIQuartetHub\recovery\` の確認情報として渡し、`--vscode-recovery-elevated` で一度だけ管理者確認を出す。昇格側は OS から PID・開始時刻・パス・SID・セッションを読み直し、`Code.exe` / `Code - Insiders.exe` 以外、別ユーザー、別セッションは終了しない。キャンセル（Win32 1223）は対象を残し、「管理者の確認がキャンセルされたため終了できませんでした。」と返す。
- **注意**: アプリの manifest は `asInvoker` のまま。昇格プロセスは単一インスタンス mutex（`Local\TurtleAIQuartetHub.SingleInstance`）へ入る前に処理して終了する。既存インスタンスへ転送すると、権限のない側が終了を担当して再び拒否される。メモリ回収・キャッシュ回収・通常のウィンドウ終了へ強制終了を広げない。子プロセスツリーは終了しない。第11節の既定強制終了禁止は維持する。
- **確認**: `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj` が終了コード 0。回帰278件（終了・復旧20件）が合格。同じユーザーの犠牲プロセス（`ping.exe`）だけを終了権限で停止し、実行中の VS Code は停止していない。

## AI 作業通知へ Grok を追加（2026-11-12 追加）
- **ファイル**: `Services/AiNotifyHookInstaller.cs`, `Services/AiActivityTracker.cs`, `Models/WindowSlot.cs`, `Views/AiHookGuideView.xaml`, `MainWindow.xaml`, `TurtleAIQuartetHub.Panel.Tests/AiActivityTests.cs`, `TurtleAIQuartetHub.Panel.Tests/AiHookLocationTests.cs`
- **問題**: Grok には Claude と同じフックがあるが、ハブは登録していなかった。
- **対策**: 既存の名前付きパイプと状態機械をそのまま使う。専用ファイル `~/.grok/hooks/turtle-ai-quartet-hub.json` に `UserPromptSubmit` / `Stop` / `StopFailure` / `StopCancelled` / `Notification` を書く。失敗と中断も完了と同じ薄い青。許可待ちは `permission_prompt` と `elicitation_dialog` だけ。作業中の緑は変えない。
- **注意**: `~/.grok` が無いときは未インストールとし、フォルダを作らない。`config.toml` とほかの `~/.grok/hooks/*.json` は触らない。解除で専用ファイルが空オブジェクトになったときだけ削除し、`.turtle-hub-backup` は残す。利用者のキーが残っていればファイルも残す。スクリプトは `{}` と終了コード 0 のまま（`Stop` と `UserPromptSubmit` を止めない）。`idle_prompt` は無視したまま。Grok は Claude 互換で `~/.claude/settings.json` も実行することがあり、その通知はツールチップが Claude Code になる。色は同じ。`config.toml` では止めない。`StopCancelled` は次の開始より遅れて届くことがあり、そのとき完了の光が付く。設定後のセッションは開き直すまで新しいファイルを読まない。未信頼の作業フォルダでは `/hooks-trust` が必要。ホーム直下は信頼済み。
- **確認**: `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj` が終了コード 0。回帰284件（AI 作業通知32件、保存先・閲覧14件）が合格。一時ホームだけで確認し、実ホームのフックと実行中のセッションは変更していない。光の目視は未実施。

## フックガイドの図は分類と揃える（2026-11-12 追加）
- **ファイル**: `docs/guides/ai-notification-hooks.md`, `docs/assets/ai-notification-hooks-basics.svg`, `docs/assets/ai-notification-to-app.svg`, `docs/assets/ai-notification-flow.svg`, `docs/index.md`
- **問題**: ガイドが設定手順から始まっており、どの瞬間にフックが動き、その知らせがバッジへどう届くかが先に分からなかった。実装経路図の完了バッジも、古い緑の光のままだった。
- **対策**: 基本、AIの状態とフック、アプリへの届き方、画面の見え方、使い方、今の実装の順に書き直した。状態とフック、届き方のSVGを追加し、実装経路図の完了バッジを同じ薄い青にした。
- **注意**: 図は分類器と一致させる。作業中と許可の確認中は緑、青い光は応答終了だけ。`idle_prompt` / `task_complete` / `PermissionDenied` は光らせない。チャット本文、黄色、ツールごとの別色は描かない。アプリ内「仕組みを見る」は3項目のまま。実ホームのフックと実行中セッションは変更しない。状態図の戻り矢印は、説明ラベルで線を隠さず、箱の辺から箱の辺へつなぐ。
- **確認**: 4つのSVGをUTF-8のXMLとして解析し、幅1200で描画して文字の見切れと重なりを確認した。アプリの挙動は変えていないため、回帰284件は再実行していない。

## 許可の確認中は緑のまま（2026-11-12 見直し）
- **ファイル**: `Services/AiActivityTracker.cs`, `Models/AiActivityState.cs`, `Models/WindowSlot.cs`, `MainWindow.AiActivity.cs`, `MainWindow.xaml`, `Views/AiHookGuideView.xaml`, `docs/guides/ai-notification-hooks.md`, `docs/assets/ai-notification-states.svg`, `docs/assets/ai-notification-hooks-basics.svg`
- **問題**: 許可や質問の確認を完了と同じ薄い青にしていた。確認は作業の途中なので、緑の明滅が正しい。状態図の戻り矢印は説明の帯に隠れ、箱の辺まで届いていなかった。
- **対策**: `permission_prompt`、`elicitation_dialog`、`PermissionRequest` は表示を変えない。開始済みなら緑の明滅が続く。青い光は `Stop` / `agentStop` / `StopFailure` / `StopCancelled` だけ。`Attention` と「操作を待っています」のツールチップは外した。状態図の戻り矢印は、青い箱の下辺から出て、作業中または待機の下辺で終わる。
- **注意**: フックの登録自体は残す。知らせは届くが、バッジは変えない。黄色やツールごとの別色には戻さない。`idle_prompt` は無視したまま。過去の「完了と許可待ちは同じ青」の記録は履歴として残し、現在の仕様はこちら。
- **確認**: `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj` が終了コード 0。回帰284件（AI 作業通知32件、保存先・閲覧14件）が合格。状態図は幅1200で描画し、進む矢印と戻る矢印が箱の辺でつながっていることを確認した。実ホームのフックと実行中の VS Code は変更していない。光の実機目視は未実施。

## アプリ内「通知の流れ」の主体と通信（2026-11-13 追加）
- **ファイル**: `Views/AiHookGuideView.xaml`, `docs/guides/ai-notification-hooks.md`
- **問題**: 設定内の「通知の流れ」が3枚の短いカードで、誰が動き、名前付きパイプで何を渡すのかが分からなかった。
- **対策**: 同じ3タブのまま、流れタブへ主体（AIツール / フックのプログラム / ハブ）と 1〜5 の順を WPF ベクターで入れた。3番は `TurtleAIQuartetHub.Commands` への一方通行。開始は緑、確認中も緑、終わったときだけ薄い青。外部 SVG は埋め込まない。
- **注意**: それ以前の「アプリ内は3項目の短い要約のまま」は、この流れタブには当てはまらない。タブ数は3つのまま。設定タブと表示タブの文言は、緑の継続と薄い青の完了と矛盾させない。挙動・フック登録・実ホームは変えない。横5列の図は設定枠（最大610 DIP）に入らない。主体は裸の「ハブ」と書かず、「このアプリ（カルテットハブ）」と書く。
- **確認**: ガイド文言の回帰テストは無い。挙動を変えていないため、回帰284件は再実行しない。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告・エラー 0。隔離した非表示描画で幅 540 と 578 を確認し、主体の3列、下向き矢印、本文、バッジ例は折り返しの中に収まった。実行中のハブと VS Code は起動・終了していない。光の実機目視は未実施。

## 図と説明の「この」「その」を名前にする（2026-11-13 追加）
- **ファイル**: `Views/AiHookGuideView.xaml`, `docs/guides/ai-notification-hooks.md`, `docs/index.md`, `docs/assets/ai-notification-who-moves.svg`, `docs/assets/ai-notification-to-app.svg`, `docs/assets/ai-notification-hooks-basics.svg`, `docs/assets/ai-notification-flow.svg`, `docs/assets/ai-notification-states.svg`
- **問題**: 「このアプリ」「そのプログラム」「このフォルダ」「ここでは」では、誰が動き、何を渡しているのかが分からなかった。
- **対策**: 主体は AIツール、フックのプログラム、カルテットハブと書く。フォルダは作業フォルダ、作業中の光は緑の明滅、消す対象は光っているスロットと書く。裸の「ハブ」と「このアプリ」には戻さない。
- **注意**: それ以前の「主体は『このアプリ（カルテットハブ）』と書く」は現在の書き方ではない。タブ数は3つのまま。設定枠の横5列にはしない。挙動・フック登録・実ホームは変えない。`MainWindow.xaml` の「ハブ設定へ保存」と「ハブ外で開いている VS Code」は、このガイドの主体名とは別なので変えない。
- **確認**: ガイド文言の回帰テストは無い。挙動を変えていないため、回帰284件は再実行しない。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告 0、エラー 0。5つの SVG は UTF-8 の XML として幅 1200 で解析し、Edge の headless（device scale 1）で描画した。who-moves の「主体：フックのプログラム」「主体：カルテットハブ」「カルテットハブが塗る。」、to-app の「カルテットハブへ渡す」「カルテットハブが判断」「カルテットハブが変える」、flow の「カルテットハブが受け取る」、basics のフッターは、枠の中に収まっていた。states の戻り矢印は箱の辺でつながったまま。隔離した非表示描画で幅 540 の流れ・設定・表示の本文を確認し、折り返しの中に収まった。実行中のハブと VS Code は起動・終了していない。光の実機目視は未実施。

## 標準表示フッターから設定の保存・読み込みを移す（2026-11-13 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`
- **問題**: 標準表示の下段にあった「設定保存」「設定読み込み」は使われていなかった。空いた場所へ「非表示」を移し、「すべて閉じる」を広く、設定の「クリアする」と同じ赤系にしたい。保存と読み込みは歯車の設定から行う。
- **対策**: `FooterControlsGrid` の右クラスタ幅は 336 DIP。上段は `*` / 4 / `*` / 4 / `*` で「全ディスプレイ移動」「最前面」「最背面」。下段は `*` / 4 / `2*` で「非表示」と「すべて閉じる」。間隔はボタンの右 Margin ではなく 4 DIP の列にする。`DangerBarButton` は `BarButton` の高さ 24 DIP を維持し、色は「クリアする」と同じ前景 `#FFF4F2`、背景 `#6F2D27`、枠 `#B3574E`。`BaseButton` のホバーは緑になるため、独自テンプレートでホバー背景 `#8A3A32`、枠 `#D07A72`、押下 `#5A241F` にする。`DangerButtonStyle` の `MinHeight` 32 はフッターへ使わない。
- **設定画面**: `SaveSettingsButton` と `LoadSettingsButton` の名前と Click は維持し、`SettingsScrollViewer` の先頭カード「スロットと控え」へ置く。`SaveCurrentSettings` は `slots.json`（表示中スロット、控え、タイトル、ワークスペース、アプリ ID）。起動コマンドは「パス設定を保存」（`SaveApplicationPathSettings`）。設定のモーダルがステータスバーを覆うため、結果は `PanelStateActionStatusText` に出す。歯車を開くときにその文言は消す。
- **注意**: メッセージ欄は 10 DIP のまま。コンパクト表示は `FooterControlsGrid` を隠したまま。`SetBusyState` は既存のボタン名で有効と無効を切り替える。ボタンの `DesiredSize` は Margin を含む。保存ボタンの Desired 132 は実幅 124 に右余白 8 を足した値で、文字切れではない。縮小表示の「前」「背」、光、フックは変えない。前面・背面の動作記録は位置が変わっただけで維持する。
- **確認**: `MainWindow` を生成し、`Loaded` の前に `DispatcherTimer` を止めて、`Show` せず 650×535 DIP で Measure/Arrange した。上段の実幅は 109 / 110 / 109 DIP、下段は 111 / 221 DIP、高さは 24 DIP。「全ディスプレイ移動」の希望幅 95 DIP は実幅に収まる。設定オーバーレイを Visible にして再配置し、先頭カードとフッター（不整合を修復、再検出、ログを表示、パス設定を保存、閉じる）が見えることを確認した。`Loaded` は走らせていないので、管理ウィンドウの再配置はしていない。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告 0、エラー 0。レイアウトだけの変更のため、回帰284件は再実行していない。

## 「すべて閉じる」はカードのクリア色（2026-11-13 追加）
- **ファイル**: `MainWindow.xaml` の `DangerBarButton`
- **問題**: 最初の赤は確認ダイアログ「クリアする」（`DangerButtonStyle`、地 `#6F2D27`、枠 `#B3574E`）だった。利用者が示したクリアは、カード右上の消しゴムボタンである。
- **対策**: `SlotClearInfoButtonStyle` と同じ地 `#261816`、枠 `#6A3832`、文字 `#F2B7AF`。ホバーも同じ地 `#33201D`、枠 `#A65348`、文字 `#FFE7E2`。押下は地 `#1C1210`。`BaseButton` のホバーが緑に戻らないよう、独自テンプレートは維持する。
- **注意**: 確認ダイアログの「クリアする」は `#6F2D27` のまま。フッターの高さ 24 DIP と下段 1/3・2/3 は変えない。
- **確認**: `Show` せず 650×535 DIP で描画し、「すべて閉じる」とカードのクリアのブラシがどちらも地 `#FF261816`、文字 `#FFF2B7AF`、枠 `#FF6A3832` であることを確認した。実行中のハブは起動していない。

## 作業中と完了の光をカードの枠へ移す（2026-11-13 追加）
- **ファイル**: `MainWindow.xaml`, `Views/AiHookGuideView.xaml`, `MainWindow.AiActivity.cs`
- **問題**: 作業中と完了の光が A〜D の小さな文字にあった。利用者は、カードの細い枠が明滅し、枠の外だけが LED のようにぼんやり光る見え方を求めた。枠の線は太くしない。完了時は今の薄い青の枠と、カードの地だけの薄い青。ボタンと IDE / CLI の中は塗らない。
- **対策（後述 §6 で改良）**: `AiCardPulseStyle` が太さ 1 の枠を緑で明滅し、完了時は `#D7EEFB` で止める。初期実装では `AiCardHaloStyle` を太さ 8 の光源＋`BlurEffect` Radius 14 で置いていたが、クリップ端と外張りで境界線が見えたため §6 の面塗り＋`Margin="0"` 方式へ見直した。完了の地は `AiDoneCardBrush` `#203440`。IDE / CLI の地 `#121816` と各ボタンの地は変えない。`ApplicationGroupLabelStyle` の穴あき背景だけ完了時に地へ合わせる。A〜D の文字色とバッジの光は外した。状態機械、フック、フォーカスで消す処理は変えない。
- **注意**: それ以前の「バッジだけを光らせ、カード全体は塗らない」は現在の見え方ではない。塗るのは完了時の地だけで、ボタンと IDE / CLI の中は含めない。点滅方式は §6 の 5.6 秒ホタル点滅に更新済み。
- **確認**: `Show` せず 650×535 DIP で描画した。スロット A を作業中、B を完了にした。A の文字は通常の緑のまま、細い緑の枠と外側のぼかしが出た。B は細い薄い青の枠と地の薄い青で、ボタンと IDE / CLI の中は暗いまま、B の文字は青くなっていない。C と D は通常の枠。実行中のハブは起動していない。`Loaded` は走らせていない。

## 5. Antigravity 作業通知フックの実装パターン（2026-11-13 追加）
- **設定形式（名前付きフック + 平坦な配列）**:
  - `AiNotifyHookInstaller.AntigravityHooksPath`（`~/.gemini/config/hooks.json`）では `{ "hooks": { ... } }` や `matcher` ラッパーを使わず、トップレベルに名前付きフック `"turtle-ai-quartet-hub": { "enabled": true, "PreInvocation": [ { "type": "command", "command": ..., "timeout": 10 } ], "Stop": [ ... ] }` を書き込む（Antigravity のパーサー仕様に合わせ、`hooks` ラッパーや `matcher` を入れない）。
  - 既存の名前付きフック（例: `lint-checker`）はそのまま残し、更新前には `.turtle-hub-backup` を保存する。解除時は `RemoveAntigravityHubEntries` で自前コマンドのみを除去し、`hooks.json` が空 `{}` になった場合は `DeleteIfEmptyJsonObject` で削除する。
- **Windows の `-File` 引用符制約への対応**:
  - Antigravity のフック実行系は Windows で `-File "..."` の二重引用符を不正パス文字として拒否する。
  - `AiNotifyHookInstaller.BuildCommand("antigravity", hookEvent)` では引用符を使わず、スクリプトパスに空白がない場合は無引用の `/` 区切り絶対パス、空白を含む場合は Antigravity が `hooks.json` の親フォルダ（`~/.gemini/config`）をカレントディレクトリとしてフックを起動する仕様を利用し、`../../.turtle-ai-quartet-hub/ai-notify.ps1` を出力する。
- **ペイロード拡張（`NotifyScript` は ASCII のみ維持）**:
  - `ai-notify.ps1` は `cwd` が空の場合に `workspacePaths` の先頭要素を参照し、セッション識別子として `session_id` / `sessionId` に加えて `conversationId` / `conversation_id` を、詳細として `notification_type` / `notificationType` に加えて `terminationReason` を読み取る。
  - `AiHookEventParser.Classify` は `PreInvocation` を `Start`、`Stop` のうち `detail == "USER_CANCELED"`（大文字小文字不問）を `Interrupt`、それ以外の `Stop`（`NO_TOOL_CALL` / `ERROR` / 空など）を `Stop` と判定する。

## 6. 作業中カードのホタル点滅・境界のない発光（2026-11-13 追加）
- **ホタル点滅のアニメーション**: `MainWindow.xaml` の `AiCardHaloStyle` / `AiCardPulseStyle` は、5.6 秒周期・非対称の `DoubleAnimationUsingKeyFrames`（30fps、0.7 秒の穏やかな待機 `0.08→0.10` / `0.18→0.20` → 1.7 秒で柔らかく点灯 `0.82` / `0.90` → 0.4 秒の頂点余韻 `0.77` / `0.86` → 2.8 秒の長い余韻減衰）へ統一した。
- **光の外側境界を消しつつ外へ広げる構造**: `AiCardHaloStyle` は `Margin="-1"`（既定、`IsFocused == True` 時は `-2` へ広げて 2px フォーカス枠の外縁と一致させる）・塗りつぶし `Background`・`BorderThickness="0"`・`BlurEffect Radius="15" RenderingBias="Quality"` とし、手前のカードで内側を隠しながら外周方向へ大きく柔らかな減衰だけを残した（テンプレート側のローカル `Margin` は外して `Style.Triggers` を効かせる）。
- **フォーカス表示との両立（枠線は点滅させない）**: フォーカス表示中の太い 2px 枠（`SlotCardFocusFrame` / `CompactFocusFrame` / `MicroFocusFrame`）自体は固定のまま点滅させず（`MultiDataTrigger` で `IsFocused && Working` 時は内側の `AiCardPulseStyle` も `Collapsed` にして枠線の明滅を完全に止める）、カード側の静的な緑 `DropShadowEffect` だけを `{x:Null}` にすることで、固定フォーカス枠の外周に広がる `AiCardHaloStyle` の明滅をはっきり見せる。
- **クリップ余白の相殺**: `StandardSlotsGrid` を `Margin="-8,-7,-4,-7"` / `Padding="0"`、内側の `UniformGrid` を `Margin="8,7,8,7"` とし、カード位置・幅・高さ・スクロールバー位置を変えずにフォーカス枠外縁から上下 12 DIP・左右 13 DIP（カード本体からは上下 14 / 左右 15 DIP）の余白を確保し、光が境界の手前で 0 付近まで減衰するようにした。`AiCardHaloStyle` / `AiCardPulseStyle` は `CompactSlotButtonStyle` より前に配置して静的リソースの前方参照も防いでいる。

## 7. 完了の青配色見直し・A〜D アイコン点灯の併用（2026-11-13 追加）
- **完了時のアプリ調和型ブルー**: 白っぽかった `#203440` / `#D7EEFB` / `#B6DFF6` を廃止し、アプリ内の VS Code / AI ボタンと調和する深めのブルーへ統一した。カード地 `AiDoneCardBrush = #112D46`、1px 枠 `AiDoneBorderBrush = #3EA3EC`、外側のぼかし光 `AiDoneGlowColor = #228BE0`（`Opacity="0.74"`）。
- **A〜D アイコンの点灯・点滅併用**: カード枠＋光背だけでは視認しづらいため、`AiBadgeWorkingRingStyle`（`AiCardPulseStyle` / `AiCardHaloStyle` と同期した 5.6 秒ホタル点滅、フォーカス中も維持）を標準 `SlotNameBadgeStyle`・縮小 `CompactSlotBadgeStyle` の上に重ねて作業中に枠を光らせる。完了時は `AiDoneBadgeBackgroundBrush = #174F85`（カード地より少し濃いめの青）・`AiDoneBadgeBorderBrush = #68C1FF`・`AiDoneBadgeForegroundBrush = #F0F8FF`・`AiDoneBadgeGlowEffect`（`Color="#2E96F0"`, `BlurRadius="9"`）で点灯させ、極小 `MicroSlotButtonStyle` も同様に `#174F85` 地＋ `#F0F8FF` 文字で点灯させる（`CompactSlotBadgeStyle` と `MicroSlotButtonStyle` の完了トリガは `Missing` / `Launching` より後ろに配置して表示優先順位を確保）。

## 8. 作業中は枠を一周する光の筋（2026-11-16 追加）
- **ファイル**: `AiCardOrbitGlow.cs`, `MainWindow.xaml`, `Views/AiHookGuideView.xaml`
- **問題**: 5.6 秒で枠全体がゆっくり明滅する見え方は、作業中だと分かりにくかった。利用者は、枠の上を光の筋が一周し、少し止まってからもう一周する動きを求めた。
- **対策**: `AiCardOrbitGlow` を標準（角丸 8）・縮小（7）・極小（6）のカード枠の上に置く。作業中だけ、角丸矩形の破線を一周分ずらし、1.15 秒で一周、0.5 秒停止、を繰り返す。破線は一区間だけで、残りは隙間。芯は `#F4FFF8`、太さ 2.8。`DropShadowEffect`（`#45D483`、ぼかし 12、深さ 0）は筋だけに付く。常設の 1px 枠は太くしない。
- **止めたもの**: 作業中の `AiCardHaloStyle` と `AiCardPulseStyle` の透明度アニメーション、フォーカス中に内側の枠を畳む `MultiDataTrigger`、`AiBadgeWorkingRingStyle`。完了時の青い枠と外側の青い光、A〜D の青点灯は残す。太いフォーカス枠は点滅させない。
- **注意**: `StrokeDashArray` と `StrokeDashOffset` の単位は線の太さ。一周は周長÷太さ。停止位置をちょうど一周分にすると、次の周の開始と見た目が同じになり、飛びがない。状態機械とフックは変えない。§6 のホタル点滅と §7 の作業中アイコン点滅は、作業中の現在の表示ではない。
- **確認**: `MainWindow` は生成したが `Show` せず、`Loaded` の前に `DispatcherTimer` を止めた。スロット 0 は作業中、1 は完了。標準カードの軌道は 4 件で、見えている作業中は 1 件、破線は `35.71,289.81`（一区間）。完了と待機の軌道は畳んだ。縮小 210×28 と極小 52×44 も破線 2 個で組んだ。オフセット 8.55 の光は左上、163.83 の光は右下で、中心の距離は 285。明るい画素は枠の周の約 2%。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告 0、エラー 0。見た目だけの変更のため、回帰 284 件は再実行していない。実行中のハブは起動していない。

## 9. 作業中の光はぼんやりした緑で加速する（2026-11-16 追加）
- **ファイル**: `AiCardOrbitGlow.cs`, `MainWindow.xaml`, `Views/AiHookGuideView.xaml`
- **問題**: 枠を一周する動きは合っていた。利用者は、白い芯のくっきりした線と一定の速度をやめ、旧来のホタル点滅のようにテーマの緑がふわっとぼやけた光が回り、最初はゆっくりで終わりにかけてグッと速くなる見え方を求めた。
- **対策**: 作業中の `AiCardOrbitGlow` は白い線と `DropShadowEffect` を出さない。外側は `#45D483`（不透明度 0.70、太さ 16、ぼかし 22）、内側はホタルの光背と同じ `#67E7A4`（不透明度 0.38、太さ 7、ぼかし 16）。光の長さは周長の 16% を 58〜92 DIP に収め、一塊のぼやけとして進む。約 1.78 秒で一周し、0.40 秒で 3.5%、1.10 秒で 20%、1.68 秒で 74%、1.78 秒で一周。約 0.44 秒止まってから同じ向きに繰り返す。標準・縮小・極小とも同じ。常設の 1px 枠は太くしない。
- **維持**: 完了の深い青、許可の確認中もこの緑の周回、状態の判定、フック、フォーカス表示の切り替えで消す処理。§8 の白い芯と一定速度は、作業中の現在の表示ではない。
- **確認**: `MainWindow` は起動していない。300×170 の軌道を余白付きで描き、0.40 秒の光は左上 (45, 44)、1.10 秒は下 (49, 184)、1.68 秒は上の右寄り (225, 33)。中心の移動は 181。前半の速度は 0.24 周/秒、後半は 0.93 周/秒。いちばん明るい画素は緑（55,146,96 と 53,138,90）で、枠の周に占める緑は約 2%。待機は畳んだ。角丸の枠に入れた描画でも、同じ緑のぼかしが枠の上を移動した。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告 0、エラー 0。見た目だけの変更のため、回帰 284 件は再実行していない。

## 10. 作業中の光は枠線・角丸の中心線上を走る滑らかな LED トレイル（2026-11-16 刷新）
- **ファイル**: `AiCardOrbitGlow.cs`, `MainWindow.xaml`, `MainWindow.AiActivity.cs`, `Views/AiHookGuideView.xaml`, `TurtleAIQuartetHub.Panel.Tests/AiOrbitGlowTests.cs`
- **問題（§9 までの課題）**:
  1. `Shape.Path` + `StrokeDashArray` に太さ 16 / ぼかし 22 の `BlurEffect` を組み合わせた結果、WPF の `Shape.GetLayoutClip` と余白不足により、カード周囲（特に縮小・極小・標準左上）に 90° の四角いクリップ角が露出していた。
  2. `inset=1` かつ太さが角丸半径（5〜7 DIP）を上回り、さらに `StrokeDashArray` が 2 層で先端位置・丸キャップ形状がズレるため、角丸の枠上を綺麗に追従せず太い霧がぼんやり動く見た目になっていた。
  3. 1 周 1.78 秒かつ最後 0.10 秒で 26% を飛ばす急加速が視覚的に落ち着かず目障りだった。
- **対策**:
  - `Shape` と `BlurEffect` を撤去し、`DrawingVisual` へ `StreamGeometry`（`LineTo` + `ArcTo` の厳密円弧）で 28 段（外周オーラ〜ボディ 18 段＋LED 芯 10 段）の高密度グラデーション・トレイルを描画（先端ほど太く明るく、尾に向かって細く透明へ段差なく減衰）。各スライス `Pen` はサイズ変更時のみ生成・`Freeze()` して毎フレームのアロケーションを抑える。
  - 中心線オフセットを `BorderThickness="1"` に一致する `inset = 0.5`（角丸半径 `R - 0.5`）へ補正し、カード短辺に応じて線の太さをスケールする（外縁張り出しは縮小/極小で最大 1.7 DIP、標準で 3.2 DIP 以内）。
  - モーションは 256 分割の周期ガウス加速テーブル（`u = 0.80` 付近ピーク、速度比約 2.0 倍）を用い、周期を周長に応じて標準 ~3.60s / 縮小 ~2.77s / 極小 ~2.45s に設定。終端と始端が速度・位置とも連続に繋がる。
  - `IsVisibleChanged` / `Unloaded` で非表示モード・閉鎖中のアニメーション時計を止め、負荷を抑える。テスト用に `SetStaticPhase` を備える。
- **注意**: `AiCardOrbitGlow` 内に `Shape` や大きな `BlurEffect` を戻すと再び `Shape.GetLayoutClip` 等による四角い境界角が露出するため禁止。§8 / §9 の仕様は本節で置き換える。完了時の青い光背（`AiCardHaloStyle`）と `StandardSlotsGrid` の負マージン相殺は維持する。

## 11. 完了時青表示の早めホタル点滅と IDE/CLI 見出し枠線切り抜き（2026-11-16 追加）
- **ファイル**: `AiDoneFirefly.cs`, `GroupHeaderCutout.cs`, `MainWindow.xaml`, `MainWindow.AiActivity.cs`, `Models/AiActivityState.cs`, `Views/AiHookGuideView.xaml`, `TurtleAIQuartetHub.Panel.Tests/AiDoneFireflyTests.cs`
- **問題**:
  1. 完了時（`AiActivityState.Done`）の青表示が静的点灯のままだったため、少し早めのホタル点滅へ変更する要望があった。
  2. 標準カードの `IDE` / `CLI` ラベル（`ApplicationGroupLabelStyle`）が文字サイズ相当の背景塗り（`AiDoneCardBrush`）を持っていたため、完了時に枠線の内側へ四角い青が食い込んで見えていた。
- **対策**:
  - `AiDoneFirefly` 添付ビヘイビア（`CycleDurationSeconds = 3.0`、ピーク 1.15s）を導入し、`Halo`（`0.12↔0.90`）・`CardEdge`（`0.28↔0.98`）・`CardSurface`（`0.00↔0.62`）・`MicroSurface`（`0.00↔0.68`）・`BadgeSurface`（`0.00↔0.66`）・`BadgeGlow`（`0.16↔0.96`）の全 6 ロールを **`UIElement.OpacityProperty` のみのアニメーションへ統一**した。`DesiredFrameRate` の 30fps 制限を排してモニタのネイティブリフレッシュレートへ追従させ、`KeySpline(0.55, 0.0, 0.22, 1.0)` → `KeySpline(0.30, 0.0, 0.55, 1.0)` の水平接線（速度 0 接続）2 区間スプラインと `Stopwatch` 共有位相オフセットにより、8bit RGB 階段バンディング・折れ角・周期境界のカクつきをなくした。
  - XAML 側は待機用ベーススタイルと完了用オーバーレイ（`AiCardDoneWashStyle`・`AiMicroDoneWashStyle`・`AiBadgeDoneWashStyle`・`AiBadgeDoneGlowStyle`：`Visibility="Collapsed"`、完了時のみ `Visible`）を分離し、`DropShadowEffect` 自体を静止凍結したまま外側の `Border.Opacity` ごとフェードさせる。これにより `Style` 内の Frozen ブラシ例外や `TemplateBinding Background` 剥がれを回避し、非完了時は負荷ゼロで通常スタイルへ戻る。
  - `IDE` / `CLI` グループは背景専用層（`ApplicationGroupFillStyle`: `#121816`, `Margin="0.5,1,0.5,0.5"`, `CornerRadius="6"`）、1px 枠線専用層（`ApplicationGroupBorderStyle` + `GroupHeaderCutout.Header` で上辺の見出し占有幅だけを `EvenOdd` クリップ除外）、文字ラベル層（`Background="Transparent"`）の 3 層へ分離した。これにより待機時も完了点滅時も枠内へカード地が一切食い込まず、枠外・線隙間の外側だけがカード背景と一致する。
- **注意**:
  - `TemplateBinding Background` を持つテンプレート内 `Border` 自体へ `ClearValue(BackgroundProperty)` を呼ぶとバインディングが剥がれるため、`CardSurface`/`MicroSurface` 用の専用オーバーレイ `<Border>` を維持すること。
  - 完了時アニメーションへ `ColorAnimation` や `DropShadowEffect.Opacity` アニメ、あるいは `DesiredFrameRate=30` を戻すと再び暗色青域で 8bit 色階段やコマ落ちが目立つため、`Opacity` 統一とハードウェア合成設計を維持すること。
  - 完了用 `AiDoneFirefly` に `IsVisible` のみを起動条件にすると、ウィンドウ未接続（`PresentationSource == null`）のオフスクリーン検査で開始できないため、通常時は `element.IsVisible` を尊重して省負荷を保ち、検査専用の `FreezeAtPhaseForTesting` 内では祖先可視性（`AreAncestorsVisible`）を許可して静止位相のレンダリングを可能にしている。

## 12. AI 作業状態スタック（取りこぼし・誤残存・隠れカード解除漏れ）の構造対策（2026-11-17 追加）
- **ファイル**: `Services/SingleInstanceCoordinator.cs`, `App.xaml.cs`, `Services/AiNotifyHookInstaller.cs`, `Services/AiActivityTracker.cs`, `MainWindow.xaml.cs`, `Services/WindowArranger.cs`, `TurtleAIQuartetHub.Panel.Tests/AiActivityTests.cs`
- **発生していたルート**:
  1. **名前付きパイプの 1 本制限と閉鎖空白**: `SingleInstanceCoordinator` が `maxNumberOfServerInstances: 1` の同期読取ループだったため、接続確立〜読取中の同時通知や再生成隙間で `ai-notify.ps1` の単発 `Connect(800)` / `Directory.GetFiles` 判定が空振りし、`Start` 取りこぼし（`Done` のまま）や `Stop`/`Interrupt` 取りこぼし（`Working` 残存）が起き得た。
  2. **終了・失敗・キャンセル系のフック不足と分類漏れ**: Claude Code の `StopFailure` / `SessionEnd`、Codex の `SessionEnd`、GitHub Copilot CLI の `errorOccurred` / `sessionEnd`（および `agentStop` の `reason` = `abort` / `user_exit` 等）が未登録・未分類で、Antigravity の `termination_reason` や `cancel`/`cancelled`/`aborted` 表記、Grok の `workspaceRoot` も未救済だった。
  3. **孤立セッションキーの 60 分残存**: 同一ツールで前のターンがフック無送信のまま中断（例: Claude の `Esc`/`Ctrl+C` 等）または `SessionId` 有無が食い違って別セッションが完了・`Acknowledge()` された際、`_activeSessions` に残った同ツールの古いキーが即座に `Working` へ引き戻していた（「カードを開いても完了にならず」「動いていないのに実行表示」の主因）。
  4. **隠れフォーカスカード再クリックと OS フォーカス制限**: 既に 1 面フォーカス中のスロットが管理外ウィンドウ（ブラウザ等）に覆われているときにカードをクリックすると、`ToggleSlotFocus` が前面化だけで `IsFocused` を変えず early return するため `AcknowledgeAiActivityOnFocusChange` が呼ばれず `Done` が消えなかった。また `FocusMaximizedOnMonitorAsync` が `SetForegroundWindow` の OS 制限で `false` を返した際も、`IsZoomed` 済みでも `IsFocused` 更新・消灯がスキップされていた。
- **対策と維持すべき不変条件**:
  - `SingleInstanceCoordinator` は `MaxAllowedServerInstances` を許可し、クライアント接続後すぐ次インスタンスを張って `HandleClientConnectionAsync` へ読取を委譲する（テスト隔離用の `internal SingleInstanceCoordinator(string mutexName, string pipeName)` を提供）。`App.xaml.cs` では `--ai-event` のみを `DispatcherPriority.Normal` で即時処理する。
  - `ai-notify.ps1`（ASCII 専用維持）は `ConvertFrom-Json` 失敗でも `(Get-Location).Path` フォールバックへ進み、`workspaceRoot` / `workspace_root` / `termination_reason` / `reason` / `recoverable` を吸い上げ、パイプ接続を 1 回だけ短間隔リトライ（400ms×2 + 30ms）する。
  - `AiActivityTracker.Apply` は同一ツールの新規 `Start` で同ツールの古いセッションキーを置き換え（**異なるツール間の並行動作は維持**）、`Stop` / `Interrupt` でも空 `SessionId`・無キー開始分を取り残さない。
  - **例外: `SessionEnd`（2026-11-19 追加）**: `SessionId` 付きの `SessionEnd` は、そのセッションのキー（と無キー開始のツール名キー）だけを外し、同ツールの別セッションの作業中は残す。VS Code 版 Codex は次の会話用の空の予備スレッド（Codex ログ上 `prewarmed-thread`）を先に作り、未使用のまま後で `SessionEnd`（`reason` = `other`）で終了するため、ツール単位で消すと同じウィンドウで動いている本来のスレッドのぐるぐるまで消えていた（実例: 03:19 開始 → 約 1 分後に予備スレッドの終了で D:Idle）。`SessionId` が空の `SessionEnd`、Esc の `Interrupt`、`Stop` は従来どおり同ツールをまとめて止める。診断ログの `AI event:` 行にはセッション ID の末尾 8 文字を `[…xxxxxxxx]` で出す（Codex の ID は UUIDv7 で先頭が時刻のため末尾で見分ける）。
  - `ToggleSlotFocus` の外部遮蔽再前面化パスへ `AcknowledgeAiActivityOnFocusChange(slot)` を追加し、`FocusMaximizedOnMonitorAsync` は `SetForegroundWindow(windowHandle) || (IsWindow(windowHandle) && IsZoomed(windowHandle))` を成功とする。なお 4 面でウィンドウを直接クリックしただけでは消さない既存仕様と、Claude CLI の `Esc`/`Ctrl+C` 自体は CLI 側がフックを発行しない外部仕様は維持・明記する。

## 13. タイトルバー「ダウンロードフォルダを開く」と設定内「AI 状態をリセット」（2026-11-17 追加）
- **ファイル**: `Services/DownloadsFolderResolver.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `Services/StatusStore.cs`, `MainWindow.AiActivity.cs`, `TurtleAIQuartetHub.Panel.Tests/SystemMonitorAndCardTitleTests.cs`, `TitleBarIndicatorTests.cs`, `AiActivityTests.cs`
- **対策**:
  1. **ダウンロードフォルダボタン**: `TitleBarToolButtonsPanel`（標準表示のみ可視）の `SystemMonitorButton` と `PullRequestInboxButton` の間（＝`PullRequestInboxButton` の左側）に `DownloadsFolderButton`（`Content="&#xE896;"`、`Segoe MDL2 Assets`、26 DIP 幅、`Margin="2,0,0,0"`）を配置した。`DownloadsFolderResolver` は Win32 `SHGetKnownFolderPath(FOLDERID_Downloads)` によりユーザーが別ドライブ等へ移動したダウンロードフォルダを優先解決し、未取得・未存在時のみ `%USERPROFILE%\Downloads` へフォールバックする。Explorer 起動ロジックは `MainWindow.xaml.cs` の `TryOpenFolderInExplorer` へ集約し、他のフォルダボタンとの重複も解消した。
  2. **AI 状態リセットボタン**: Swarm 等の複数エージェント運用や外部要因で表示状態がずれたとき手動で即座に待機へ戻せるよう、「AI の作業通知」カードヘッダー右端に `ResetAiActivityButton`（「AI 状態をリセット」）を追加し、`StatusStore.ResetAllAiActivity()` を呼ぶようにした（`UninstallAiNotifyHooksButton_Click` も同じメソッドを共有）。設定ファイル・通知スクリプト・実行中プロセスには触れず、結果を `AiNotifyActionStatusText`（設定を開き直すたびに文言・可視状態とも初期化）とステータスバーへ表示する。
- **注意**: `TitleBarButtonsPanel` 上の並び順（`SystemMonitorButton` → `DownloadsFolderButton` → `PullRequestInboxButton` → `SettingsButton`）は `SystemMonitorAndCardTitleTests` で検証しているため、アイコン追加時はテストも整合させること。また `TitleBarToolButtonsPanel` 内のため縮小・極小表示では非表示となる（既存仕様どおり）。

## 14. 控え Quartet の廃止と控え一覧への一本化（2026-11-18 追加）
- **ファイル**: `MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.StoredAssign.cs`（旧 `MainWindow.CompactStoredAssign.cs`）, `MainWindow.StoredPanelMenu.cs`, `Services/StatusStore.cs`, `TurtleAIQuartetHub.Panel.Tests/SystemMonitorAndCardTitleTests.cs`
- **ユーザー指示**: 標準・縮小の控え Quartet（タブ＋4 枚のカード）と控え一覧上部の A〜D パッドは使わないので削除。控えは数を増やしたい。配置は縮小表示と同じく「カードを選ぶ → スロットの『ここに表示』」またはドラッグ。AI アプリは左寄せ、控え一覧の開閉ボタンは右端に矢印付きで置く。一覧の高さはメインパネルに揃えなくてよい。
- **構成**:
  - `StoredPanelsRowGrid` は列 0 に `AuxiliaryApplicationsRow`（左寄せ）、列 1 に `StoredOverviewToggleButton`。標準は「⊞ 控え一覧 ›」28 DIP、縮小は「控え ›」23 DIP（`UpdateDisplayModeChrome` で切り替え）。極小表示では行ごと隠す。矢印 `StoredOverviewToggleChevron` は閉じている間は一覧が出る側、開いている間はしまう側を向く（`UpdateStoredOverviewToggleButton`）。
  - 一覧の開閉は `_isStoredOverviewWanted` で持つ。最小化・表示モード切替・終了確認で Popup を閉じても、`IsStoredPanelsAreaOpen` が真なら `ScheduleStoredOverviewAutoOpen` で開き直す。`StoredPanelsExpander` / `CompactStoredPanelsArea` / `_isCompactStoredPanelsOpen` / `UpdateWindowHeightForStoredPanels` は削除済みで、標準表示のウィンドウ高さは控えの開閉で変わらない。
  - 控えは `StoredPanelsPerPage = 4` × `StoredPanelPageCount = 10`（40 件）。保存は `Index` 単位なので段を増やしても既存の slots.json はそのまま読める。段を減らすと末尾の控えが読めなくなるので減らさない。
  - 一覧の高さは中身に合わせる（自然高 約 800 DIP）。`UpdateStoredOverviewMaxHeight` で作業領域の高さを上限にし、超える分は一覧をスクロール。上端はパネルに揃え、作業領域の下にはみ出すときだけ `GetStoredOverviewVerticalShift` で上へずらす。Popup のカスタム配置の引数は DPI によって物理ピクセルになるため、`targetSize.Width / PanelFrameBorder.ActualWidth` で換算してから使う。
  - スロットへの配置は `IsStoredAssignArmed`（極小以外で一覧のカードを選択中）。標準表示は `StandardSlotsGrid`、縮小表示は `CompactBarPanel` に `SlotAssignTarget.IsArmed` を付け、標準カードは全面の `SlotAssignOverlay`（▶ X に表示／いまの「…」は控えへ移ります）を出してクリックで `ShowSelectedStoredPanelInSlot`。オーバーレイ表示中はカード内のボタンを押せない。Esc で取消。
  - 一覧のタイルは高さ 50 DIP、名前は `InlineTitleTextBox`（最大 2 行折り返し）でダブルクリック編集。単クリックはタイトル上でも選択の切り替えとして扱う（`InlineTitleTextBox_PreviewMouseLeftButtonUp` で `StoredPanelSlot` を分岐）。`InlineTitleTextBox` の表示用 TextBlock は `TextWrapping` と `VerticalContentAlignment` をテンプレートバインドするようにしたが、既定値は従来どおり（NoWrap・Center）。
  - 段見出し（控え1 など）は `StoredOverviewPageHeaderButtonStyle` のボタンで、ダブルクリックで名前変更（兄弟の `PageHeaderEditBox`）、カードのドロップでその段へ移動。`整列` は一覧の見出し行へ移した。
- **追加調整（同日）**: ユーザー指示で、段見出し（控え1〜10）と `整列` を廃止し、右クリックで引く区切り線に置き換えた。
  - `StoredPanelPage.HasDivider`（`SavedStoredPanelPageState.HasDivider` で保存）が真の行だけ、その行の上に区切り線（`DividerRow`）を出す。名前は既存の `CustomHeader` を流用し、空なら線だけ。名前を付けると `RenameStoredPanelPage` が区切り線も引く。区切り線を消すと名前も消す（`SetStoredPanelPageDivider`）。旧データで名前の付いていた段は、読み込み時にその名前の区切り線になる。
  - カードの右クリックに「この行の上に区切り線を引く／名前付きで引く／名前を変更／消す」、区切り線の右クリックに「名前を変更／名前だけ消す／区切り線を消す」。区切り線のダブルクリックで名前を編集（`BeginStoredPanelPageHeaderEdit`）。カードを区切り線にドロップするとその行へ移す。
  - 区切り線でグループを作るため、ページをまたいで詰める `整列`（`CompactStoredPanelsButton_Click`）は削除。ドロップでの移動は移動元の行の中だけを詰める（`TryMoveStoredPanelInOverview`）ので、グループをまたいでずれない。
  - 一覧の上端はパネルの上端に揃える（`GetStoredOverviewVerticalShift`）。一度縦中央揃えにしたが、区切り線を足すたびに一覧全体が上へずれてカードの位置を覚えにくいため、ユーザーと相談して上揃えに戻した。作業領域の下→上の順に収まる位置へ補正する。区切り線の追加・削除で高さが変わったら `RepositionStoredOverview` で合わせ直す。
  - 開閉ボタンの左のアイコンは削除し、文字と矢印だけにした。標準表示では幅を下の「最背面」（`BackmostAllButton`）の ActualWidth にバインドし右端もそろえる（右 Margin 0）。縮小表示はフッターが無いのでバインドを外して中身の幅にする（`UpdateDisplayModeChrome`）。高さは標準 28 / 縮小 23 のまま。
  - 開閉ボタンは `StoredOverviewToggleButtonStyle` の独自テンプレートで、緑みの地 #14261C・枠 #3B7A55・文字 #D6F5E2・矢印 AccentBrush。ホバーは #1D3527 / #5FC98A、開いている間（Tag=Active）は #24402F / AccentBrush。BarButton の既定トリガーで灰色に戻らないよう、DangerBarButton と同じく専用テンプレートにしている。
  - 区切り線は高さ 15・線は上下 7 の位置（SnapsToDevicePixels）で、前の行の下余白 5 と下マージン 5 を合わせて上下のカードとの距離をどちらも 12 にそろえる（100/125/150% で実測一致）。
  - タイルの格子は負の余白で右端の間隔を打ち消さない（丸めで最後の列が表示域から 1px はみ出し右枠が切れた）。各カードの右 5 と区切り線・見出し行の右 5 をそろえ、一覧の右 Padding を 5 に減らして見た目の左右余白を保つ。タイル下段は DockPanel ではなく Auto/*/Auto の Grid にし、アプリ名に MaxWidth 52 と省略を付けて、狭い環境でもカードが欄の幅以上を要求しないようにする（要求すると UniformGrid ごと膨らみ右の列が切れる）。
  - 設定の「控えの区切り線の名前」は行番号の範囲（`RowRangeText`、例: 5〜8）ごとに名前を編集する。
- **注意**: 一覧は Popup（別 HWND）なので、タイル名・区切り線の名前の入力欄へのキーボード入力は実機で確認する（オフスクリーン描画では検証できない）。
- **縮小表示の追加調整（2026-11-18）**:
  - 開閉ボタンは標準と同じ「控え一覧 ›」。行の列定義を Auto（AI アプリ）/ *（開閉ボタン）にし、縮小表示では残り幅いっぱい（HorizontalAlignment=Stretch、左 Margin 8 が Antigravity2 との隙間）、標準表示は右寄せで「最背面」と同じ幅。
  - 縮小表示の一覧は最初 `CompactOverviewVisibleRows`（5 行＝1〜20）だけを出し、一覧下の `StoredOverviewMoreButton`（「21〜40 を表示（登録 n 件）」／「21〜40 をたたむ」）で後半を下に伸ばす。タブ切替ではなく下に伸ばす方式にしたのは、区切り線のまとまりがタブの境目で分断されず、前半のカードの位置も動かないため。行の表示は `UpdateStoredOverviewRows` でページのコンテナの Visibility を切り替える（Popup の Opened で適用、展開状態はセッション中だけ覚える）。件数は `RefreshAuxiliaryUi` でも更新する。
  - **2026-11-27 追記（縮小・極小表示の控え一覧ホイールスクロール＆スクロールバー非表示）**: 縮小・極小表示で折りたたんだ状態（領域を拡大しない 5 行分サイズ）のまま 21〜40 を確認・操作できるよう、後半行を `Visibility.Collapsed` で消さずに全 10 行（1〜40）を常に `Visibility.Visible` で保持し、`StoredOverviewScrollViewer.MaxHeight` を先頭 5 行分の実測高さ（`ComputeCompactOverviewViewportHeight`、未計測時フォールバック `275 DIP`）に制限する方式へ変更した。縮小・極小表示では `VerticalScrollBarVisibility = ScrollBarVisibility.Hidden` でスクロールバーを完全非表示にし、`StoredOverviewRoot.PreviewMouseWheel`（`StoredOverviewScrollViewer_PreviewMouseWheel`）で 1 ノッチあたり 1 行分（`55 DIP`）ずつ上下スクロールできるようにした。下部の `StoredOverviewMoreButton` を押したときは従来どおり `MaxHeight` を解除して領域自体を拡大／折りたたみする。折りたたみ時のスタガー出現アニメーション（`AnimateStoredOverviewItemsIn`）は前半 5 行のみに限定し、6 行目以降は最初から `Opacity=1` にして即スクロール時の非表示を防ぐ。
  - タイトルバーのツール群は縮小表示でも出し、ダウンロードフォルダと PR 受信箱だけを表示する（システムモニタ・設定・ヘルプはモーダルなので標準表示のみ）。
- **ヘルプのはみ出し（2026-11-18）**: 控え Quartet を外して本体が 535 DIP に低くなり、ヘルプの「閉じる」が下で切れた。オーバーレイは Auto 行にまたがるため高さ無制限で測られ、`MaxHeight=430` のスクロールだけでは収まらず行ごと伸びていた。ダイアログ Border の MaxHeight を設定画面と同じく `Window.Height - 44`（`SubtractConverter`）にし、行を Auto/*/Auto にしてスクロール部分が縮むようにした。650×535 で閉じるボタン下端 496 DIP（100%・125%）。

## 15. 表示モード切替の基準位置を中心に変更（2026-11-18 追加）
- **ファイル**: `MainWindow.xaml.cs`（`GetCenterAnchoredBounds`）
- **ユーザー指示**: 標準⇔縮小の切り替えは、押したボタンの位置を保つため右上基準にしていたが、大きさが変わるとウィンドウの位置が大きくずれて使いにくい。中心を保って拡大縮小する一般的な動きにする。極小表示は常に画面中央。
- **対策**: `GetCompactModeBounds` / `GetStandardModeRestoreBounds`（右端・上端を保つ）を廃止し、切り替え前の物理 px の境界の中心を保つ `GetCenterAnchoredBounds` に統一。作業領域からはみ出す分は `ClampToWorkArea` で押し戻す。極小表示は従来どおり `GetMicroModeCenteredBounds`（作業領域の中央）。極小から戻るときは極小の中心＝画面中央を基準に広がる。
- **README の図**: `docs/assets/display-modes.svg`（見本データで描画した 3 モードの PNG を data URI で埋め込んだ自己完結 SVG）と、そこから Edge headless（`--force-device-scale-factor=2 --window-size=1200,700 --default-background-color=00000000`）で書き出した `display-modes.png`（2400×1400）。個人のスロット名・パスを載せないよう、描画時はメモリ上だけ見本のタイトル・パスに差し替え、保存は抑止する。

## 16. 表示モード切替のアニメーション（2026-11-18 追加、2026-11-19 作り直し）
- **ファイル**: `MainWindow.ModeTransition.cs`（`SetDisplayMode` / `ApplyModeWhileCloaked` / `ModeMorphGhostWindow`）、`MainWindow.xaml.cs`（本体は `SetDisplayModeCore`）
- **ユーザー指示**: 切り替えをふわっと滑らかにしたい。最初の実装（本物のウィンドウを毎フレーム `SetWindowPos` で変形）は「移動しながらガタガタする」と指摘された。
- **原因**: 最上位ウィンドウの大きさを毎フレーム変えると、そのたびに DWM の再描画と WPF の再レイアウトが走り、整数 px の段階的な動きとコマ落ちになる。
- **方式**: 本物のウィンドウはアニメーション中に動かさない。
  1. 今のパネル（`PanelFrameBorder`）を `RenderTargetBitmap` で撮り、始めと見込みの終わりを含む範囲に、透明・クリック透過・非アクティブ・最前面の影武者ウィンドウを一度だけ置いて同じ位置に表示する。
  2. 影武者の `ContentRendered` 後に、本物を DWM の cloak（`DWMWA_CLOAK`=13）で見えなくする（開いたまま・アクティブのまま）。その間に `SetDisplayModeCore` を実行して目標の境界を受け取り、同期の `SetWindowPos` で一度に移動し、新しいパネルを撮る。
  3. 影武者の中で角丸の枠（Canvas 上の Grid の Left/Top/Width/Height）を 340ms の ease-in-out cubic で変形させる。画像は伸縮させず中央に置いて（`ImageBrush Stretch=None`）枠で切り抜くので、文字がにじまない。古い画像は 200ms で 0.97 倍に縮みながら消え、新しい画像は 110ms 遅れて 0.97→1 倍で現れる。`UseLayoutRounding=false` で小数点単位の位置にする。
  4. 最後のコマは本物と同じ位置・見た目なので、cloak を外してから 60ms 後に影武者を閉じる（すき間のコマを作らない）。
- **注意**:
  - 影武者ウィンドウは途中で動かさない（動かすとガタつく）。範囲は `EstimateModeTargetRect`（中心基準・極小は画面中央。縮小の高さは 200 DIP で多めに見込む）と開始範囲の和を 48 DIP 広げる。実際の終わりが範囲外なら変形せずに即時表示へ切り替える。
  - 透明ウィンドウは面積に比例して重くなるので、画面全体を覆う影武者にしない。
  - cloak に失敗する環境、「アニメーションを表示する」オフ、未表示・最小化中は即時に切り替える。切り替え中の再指示は最後の 1 件だけを終了後に反映する。控え一覧の自動オープンも切り替え後まで待つ。
  - **操作不能になった不具合（2026-11-19）**: 縮小表示へ切り替えた直後から一切操作できず、タスクマネージャーで終了するしかなくなった。原因は、影武者の枠のアニメーションの `Completed` を `BeginAnimation` の後に登録していたこと。WPF は BeginAnimation の時点で時計を作り、その時点の Completed の登録だけを引き継ぐため、完了が呼ばれなかった。その結果、本物は cloak されたまま（見えず入力も受けない）、影武者の最後のコマだけが残った。画面外の 1×1 の描画先でアニメーションを実際に進めて、後から付けると呼ばれず、前に付けると呼ばれることを確認した。
  - **対策**: Completed は必ず BeginAnimation より前に付ける。後始末は `SetDisplayMode` 内の `Finish`（1 回だけ実行）に集め、cloak を必ず外し、未適用ならモードを適用し、影武者を閉じる。アニメーション時間＋1.5 秒の watchdog が切れたら、どの段階でも `Finish` を強制する（cloak を外さないまま終わる経路を残さない）。

## 17. 控えの名前入力を本体パネルへ移し、カードのどこにでもドロップできるようにした（2026-11-19 追加）
- **ファイル**: `MainWindow.StoredRename.cs`（`StoredRenameOverlay`）、`MainWindow.xaml`、`MainWindow.xaml.cs`、`MainWindow.StoredPanelMenu.cs`
- **報告**: AI が動いているときなどに控えのカード名を変更できないことがある。入力すると IME の変換欄が入力欄の外に出る。カードのタイトルの上にドロップすると禁止マークになる。
- **原因**:
  - 控え一覧は WPF の Popup（`AllowsTransparency=True` の別 HWND で、自分ではアクティブにならない）。本体以外（AI が動いている VS Code など）が前面にあると、キー入力が一覧の入力欄に届かない。透過ポップアップの中の入力欄では、IME の変換欄の位置もずれる。
  - タイル名（`InlineTitleTextBox`）とスロットカードのタイトルは TextBox。読み取り専用の TextBox でも、ドラッグ中のデータが文字列でないと自分で `DragDropEffects.None` を返し、親カードのバブルの DragOver より先に禁止マークにしていた。
- **対策**:
  - 一覧のタイル名は TextBlock にし、ダブルクリックや右クリックの「名前を編集」で、本体パネルの `StoredRenameOverlay`（1 行の入力欄・変更・取消。縮小表示 430×145 にも収まる）を開く。開くときに `ActivatePanelWindow` で本体を前面に出し、入力欄にキーボードフォーカスを置く。Enter で確定（IME 確定の Enter は `Key.ImeProcessed` なので誤確定しない）、Esc・外側のクリックで取消、空欄ならフォルダ名の表示に戻す。区切り線の名前（ダブルクリック・右クリック・「名前付きの区切り線を引く」）も同じダイアログで、取消のときは区切り線を作らない。一覧の中の入力欄（`PageHeaderEditBox` など）とインライン編集の処理は削除した。
  - タイル・スロットカード（標準・縮小・極小）のドラッグ処理は `PreviewDragEnter/Over/Leave/Drop` で受け、子の TextBox より先に受け付ける。Preview の Leave は子要素の間を移るときにも来るため、`IsDragPointInside` で本当に外へ出たときだけ光を消す。
- **注意**: 一覧の中に入力欄を戻さない（キー入力・IME の問題が再発する）。

## 18. 配布ビルド（publish.bat）前の起動中アプリ自動終了（2026-11-20 追加）
- **ファイル**: `publish.bat`, `scripts/Stop-RunningPanel.ps1`, `src/TurtleAIQuartetHub.Panel/Services/SingleInstanceCoordinator.cs`, `src/TurtleAIQuartetHub.Panel/App.xaml.cs`, `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`, `src/TurtleAIQuartetHub.Panel.Tests/PublishStopPanelTests.cs`, `README.md`
- **問題**: `dist/turtle-ai-quartet-hub/TurtleAIQuartetHub.exe` や Release 出力の `TurtleAIQuartetHub.exe` が起動中のまま `publish.bat` を実行すると、実行ファイルが OS にロックされて `dotnet publish` が失敗していた。さらに `MainWindow.OnClosing` は通常操作の誤終了を防ぐため `WM_CLOSE` で終了確認ダイアログを表示してキャンセルするため、単純な `WM_CLOSE`（`/F` なしの `taskkill` 等）では閉じない。
- **対策**:
  - アプリ側（`SingleInstanceCoordinator` / `App.xaml.cs` / `MainWindow.xaml.cs`）に外部終了コマンド `--exit` を追加。未起動時に `--exit` 付きで起動された場合は UI を開かず即 `Shutdown()` し、起動中インスタンスへ届いた場合は `DispatcherPriority.Normal` で `RequestExternalExit()` を呼び出して確認ダイアログを出さずに `OnClosing`（非表示ウィンドウ復元）・`OnClosed`・`App.OnExit`（JumpList 無効化、正常終了ログ記録、Mutex/Pipe 解放）を通って正常終了させる。
  - `publish.bat` は Preflight 後かつ `dotnet restore` 前に `scripts/Stop-RunningPanel.ps1` を実行する（PowerShell 5.1 互換の英語 ASCII スクリプト）。同スクリプトは現在セッション内の `TurtleAIQuartetHub` プロセスを検出し、まず名前付きパイプ `TurtleAIQuartetHub.Commands` へ `["--exit"]` を送って最大 3 秒待機し、未応答（`--exit` 未実装の旧バイナリやハング時）の場合は `Stop-Process -Force` でアプリ単体プロセスのみをフォールバック終了した上で、`dist` と `bin\Release` の `TurtleAIQuartetHub.exe` の排他オープン（ロック解除）を確認してから `dotnet restore` / `dotnet publish` へ進む。
- **注意**: VS Code やターミナル等の管理対象・子プロセスを巻き込まないよう、`taskkill /T` のようなプロセスツリー終了は絶対に使わない。`publish.bat` は英語 ASCII のまま維持し（CP932 環境でのバッチ構文崩れ防止）、`Stop-RunningPanel.ps1` 内で配列を返す関数は PowerShell StrictMode で空配列が 1 要素配列へ化けないよう単項カンマ `return ,$arr` を使わない。テスト時は本番 `dist` を触らないよう他プロセス名指定時や `-SkipDefaultLockCheck` 指定時にデフォルトのロック検査をスキップする。

## 19. アプリ本体の超軽量影武者起動／終了、正式透過 1 つずつランプ点灯ウェーブ、完全同期終了、共有プロファイル再接続（2026-11-25 刷新）
- **ファイル**: `MainWindow.StartupAnimation.cs`, `MainWindow.LaunchPresentation.cs`, `MainWindow.ModeTransition.cs`, `MainWindow.xaml.cs`, `Services/LaunchWindowCloakSession.cs`, `Services/LaunchStagingOverlay.cs`, `Services/VscodeLauncher.cs`, `Services/ApplicationLauncher.cs`, `Services/WindowArranger.cs`, `TurtleAIQuartetHub.Panel.Tests/LaunchPresentationTests.cs`
- **判明した重要事実と従来の課題（実機・Win32 実験で 100% 実証）**:
  1. **WPF による `WS_EX_LAYERED` 強制剥ぎ取り仕様（超重要）**: WPF の `AllowsTransparency=False` ウィンドウに対して後から Win32 `SetWindowLong(GWL_EXSTYLE, WS_EX_LAYERED)` を呼んでも、WPF 内部フック（`WM_STYLECHANGING`）が `WS_EX_LAYERED` ビットを自動剥ぎ取りするため、`SetLayeredWindowAttributes` が `ERROR_INVALID_PARAMETER (87)` で必ず失敗する（`WindowChrome` 有無に関わらず発生）。これにより `AllowsTransparency=False` の `MainWindow` や `QuadrantVeilWindow` ではフェードが一切動作しなかった。
  2. **旧影武者（`AllowsTransparency=True`）のカクつき真因**: `ModeMorphGhostWindow` は同じ `AllowsTransparency=True` でも超滑らかだったのに対し、旧 `StartupRevealGhostWindow` は ①`BitmapCache` 付き要素への毎フレーム `ScaleTransform`（キャッシュ再生成）、②`RenderOptions.BitmapScalingMode.HighQuality`（激重 Fant 補間）、③12 層の多重レイヤー、④コンストラクタでの `_refreshTimer.Start()` 競合と待機不足、⑤終了時の確認ダイアログ消去直後の未安定フレーム撮影が重なってコマ飛びしていた。
  3. **他プロセスへの `DWMWA_CLOAK` 制限**: Windows DWM は自プロセス（`MainWindow`）への `DWMWA_CLOAK` は許可するが、他プロセス（VS Code 等）への `DWMWA_CLOAK` は `E_ACCESSDENIED` で拒否する環境がある。
  4. **一括起動の過密テンポと準備中点滅による「世話しない」印象**: `WaveStaggerDelay=210ms` は 4 面が同時並行で重なり、準備中の 850ms 呼吸点滅や `Ready` 時の二重フラッシュ・`"ONLINE"` 文字切替も慌ただしかった。
  5. **共有プロファイル時の既存一致ウィンドウ再接続欠落（スロット D 起動不能）**: `UseDedicatedUserDataDirs=false` の既定環境で、同じフォルダを保持したまま閉じ残った VS Code ウィンドウ／残存プロセスが存在すると、`Code.exe --new-window <path>` が新規ウィンドウを作らず 20 秒タイムアウトを繰り返す状態に陥っていた。
- **対策**:
  1. **本体起動（`920ms`）・終了（`680ms`）の超軽量影武者方式（`MainWindow.StartupAnimation.cs`, `MainWindow.xaml.cs`）**: 縮小・拡大（`ModeMorphGhostWindow`）と同じ `DWMWA_CLOAK`（自プロセス）＋`AllowsTransparency=True` 影武者＋WPF レンダースレッド独立クロックの `DoubleAnimation` へ統一。`BitmapCache`・`HighQuality` フィルタ・多重 `BlurEffect` / リングを全撤去して最小 3 レイヤー（プレート＋パネル画像＋1.5px エメラルド縁、余白 `22 DIP`）へ徹底簡素化。`_refreshTimer.Start()` は `Loaded` の `await WaitForStartupRevealIfActiveAsync()` 後へ移動。起動時は `120ms` 待機後に影武者を出し、`ContentRendered` 後さらに `70ms` 安定させてから `920ms` の S 字カーブ（`Opacity 0→1`, `Scale 0.965→1.0`, `TranslateY +12→0`）で浮かび上がらせる。終了時も確認オーバーレイ消去後 `DispatcherPriority.Render`＋`70ms` 待って撮影し、影武者で隙間なく引き継いで `60ms` 安定後に `680ms` で消散する。
  2. **象限ベールの正式 `AllowsTransparency=True` 化＋複数一括時の背面黒全画面シールド＋`BeginTime` 一括 GPU タイムライン（`Services/LaunchStagingOverlay.cs`, `MainWindow.LaunchPresentation.cs`）**: `QuadrantVeilWindow` を `AllowsTransparency=True`＋`Border.Opacity` の GPU `DoubleAnimation` へ刷新し、複数スロット同時の一括起動・一括終了（`ShouldUseFullScreenBackdrop`: `veilTargetCount > 1`）に限り、全象限ベールを `Panel > VeilA > VeilB > VeilC > VeilD` へチェーン配置した直後の背面へ角丸なしの漆黒全面シールド `MonitorBackdropWindow`（`#070A08`, `AllowsTransparency=True`, モニタ作業領域全面）を挟む 5 層構造（`HUD > Panel > Veil×N > Backdrop > 実窓`）で十字隙間・角丸隙間・全画面初期チラつきを完全遮蔽する（単体の起動・閉じる・クリア時は他画面まで暗転させないよう `MonitorBackdropWindow` は挟まず対象象限ベールと中央 HUD のみを表示）。準備中の呼吸は `1800ms`（HUD `1600ms`）へ抑制し、`Ready` は控えめな待機灯に留める。全員の配置安定化が完了したら一拍（`260ms`／単体 `120ms`）の間に背面黒シールドだけを先に `220ms` で静かに引き（裏の窓はすでに完全静止＋4 枚ベールは 100% 不透明のためチラつき 0）、直後に `BeginTime` 一括 GPU タイムラインにより `480ms` 間隔（`BuildRevealWaveTimings`）で HUD ランプ＆象限ベールの LED 光漏れ枠が「ぽっ…ふぁ〜っ…」と灯り、`240ms` 後に `580ms` の S 字カーブで開く。
  3. **一括終了の完全不透明ベール＆背面黒全画面シールド＋完了知覚ワンクッション＋「フッ」と同時消散（`MainWindow.LaunchPresentation.cs`, `Services/LaunchStagingOverlay.cs`）**: 複数終了時（`ShouldUseFullScreenBackdrop`）は `MonitorBackdropWindow`＋全象限ベールを一斉に被せて十字隙間のチラつきを完全遮断（未保存確認ダイアログ救済の 4.5 秒早期解除 `ReleaseSlotVeilEarly` では該当モニタの背面黒シールドも同時解放）。全員終了後に HUD の LED ドット点滅を止めて静止完了点灯・進捗バー 100%・各象限「✓ 終了完了」へ整えた状態で、人間が完了を知覚できるワンクッション（`GetCloseDoneHoldDuration`: 一括 `720ms`／単体 `460ms`）を置き、直後に背面黒シールド・全象限ベール・中央 HUD へ同じ瞬間（`BeginTime=0`）に `420ms` の `CubicEase EaseOut` フェードアウトを一斉投入して「フッ」と軽やかに消す。
  4. **共有プロファイル既存一致ウィンドウ再接続＆ゾンビ診断案内（`Services/VscodeLauncher.cs`, `Services/ApplicationLauncher.cs`）**: `UseDedicatedUserDataDirs=false` でも、他スロット未割り当てで `IsWorkspaceVisibleInWindowTitle` が一致する既存 VS Code ウィンドウがあれば `SelectMatchingSharedProfileWindow` で安全に再接続（念のためクローク・遷移無効化も解除）。残存 VS Code プロセスにより新規起動がタイムアウトした場合は `LastLaunchDiagnosticHint` と診断ログでシステムモニタの「VS Code の終了・復旧」を案内する。
  5. **黒画面・マスク残留を構造的にゼロにする 5 重フェイルセーフ（`MainWindow.LaunchPresentation.cs`, `Services/LaunchStagingOverlay.cs`, `MainWindow.xaml.cs`）**: ① `BeginForLaunchAsync` / `BeginCoverForCloseAsync` 自体を `try/finally` 内へ収めて例外経路の取りこぼしを排除、② `LaunchStagingOverlay` 自身の内蔵上限タイマー（起動既定 120 秒・終了 30 秒）で自動強制解除、③ 4.5 秒確認ダイアログ救済（最大化全面カバー裏の別スロット停止時も全黒シールド・全面カバーを安全解放）、④ `_sessionGeneration` 世代トークンで連打・再入時の古い遅延復帰を無効化、⑤ `OnClosing` / `OnClosed` / パネルフォーカス時 `Escape` で即時 `DismissImmediate()`。また他アプリ応用向けの設計ガイド `docs/guides/ui-animation-guide.md` と自己完結 SVG 図 3 枚（`docs/assets/animation-*.svg`）を整備。
- **注意**:
  - **禁止**: WPF の `AllowsTransparency=False` ウィンドウへ `SetWindowLong(WS_EX_LAYERED)` + `SetLayeredWindowAttributes` でフェードを試みないこと（WPF が `WS_EX_LAYERED` を剥ぎ取りエラー 87 で完全無動作になる）。透過フェードが必要なウィンドウ（`StartupRevealGhostWindow` / `QuadrantVeilWindow` / `CenterHudWindow`）は必ず `AllowsTransparency=True` を維持する（テストで検証済み）。
  - 影武者ウィンドウへ `BitmapCache` の毎フレーム拡大縮小・`HighQuality` フィルタ・`BlurEffect`・過剰な多重レイヤーを戻さないこと。`_refreshTimer.Start()` をコンストラクタへ戻さないこと。
  - 他プロセスへの `DWMWA_CLOAK` が失敗する前提で、起動安定化・ウェーブ点灯・終了一斉消散のいずれも Topmost 帯の不透明ベールと `assignments` を主軸に維持すること。
  - §16 と同じく、全 `DoubleAnimation` の `Completed` は必ず `BeginAnimation` より前に登録し、ウォッチドッグ・`try/finally` でクローク残留やオーバーレイ残留の経路を作らないこと。
  - **WPF `Timeline.BeginTime` とローカル値保持の鉄則**: `BeginTime` に `null` を設定すると WPF 仕様で「永遠に開始しない」扱いになるため、0 秒開始は必ず `TimeSpan.Zero`（`NormalizeBeginTime`）を渡すこと。また、`BeginTime > 0` の次アニメーションを掛ける際は古い `HoldEnd` 時計が破棄されて初期ローカル値（`0.0`）へ一時巻き戻るため、必ず開始前にローカル `Opacity` 値を現在値（`1.0`）へ確定してから `BeginAnimation` すること。
  - `SelectMatchingSharedProfileWindow` は必ず他スロット割り当て済み HWND を除外し、`launchPath` が空のときは再接続しないこと。

## 20. 極小表示（Micro モード）の円形オービット・ダイヤル（2026-11-26 追加）
- **ファイル**: `Services/MicroDialWindow.cs`, `MainWindow.MicroDial.cs`, `Models/WindowSlot.cs`, `Models/AppConfig.cs`, `MainWindow.ModeTransition.cs`, `MainWindow.MinimizeAnimation.cs`, `MainWindow.RestoreAnimation.cs`, `MainWindow.StartupAnimation.cs`, `MainWindow.StoredRename.cs`, `MainWindow.LaunchPresentation.cs`, `MainWindow.xaml.cs`, `TurtleAIQuartetHub.Panel.Tests/MicroDialTests.cs`, `docs/design/micro-mode-ui-concepts.md`
- **問題**: 従来の `116×116` 四角形極小表示は ①先頭 2 文字の `MicroLabel` が共通接頭辞（`AI-` 等）で重複し識別不能、②上部 4 ボタンが狭く面積を圧迫、③不透明な四角い角が中央の十字で背後 VS Code の歯車・サイドバーに干渉、④極小から控え一覧・前後・補助 AI へ届かない、という課題があった。また `MainWindow` 自体は `AllowsTransparency=False`（`WindowChrome` 付き）のため直接円形透過や半透明ゴースト化ができない。
- **対策**:
  1. **専用透過ウィンドウ＋本物 Cloak 待機構造**: 極小モード中のみ `MainWindow` を `DWMWA_CLOAK = 1`（タスクバーアイコン・Alt+Tab・既存 `MicroSlotTemplate` 互換を維持）で待機させ、専用の `AllowsTransparency=True` ウィンドウ `MicroDialWindow`（全体ステージ `228×228 DIP`・完全透明背景で非展開時は直径 `116 DIP` 真円コア以外 100% クリックスルー）を表示する。Cloak 失敗時や生成例外時は従来 `MicroPanel` へ自動フォールバックし、終了確認（`AppExitConfirmOverlay`）や控え名前変更（`StoredRenameOverlay`）表示時だけ一時的に `MicroDialWindow` を隠して本物 Cloak を解除し、閉じたら即復帰する。
  2. **スマート識別ラベル（`WindowSlot.BuildMicroSmartLabels`）**: 4 スロット間で先頭トークンまたは先頭 2 文字が重複する場合に共通接頭辞を自動スキップして特徴語を抽出（全角 4 文字・ASCII 6 文字上限）。既存 `MicroLabel` と `MicroSlotTemplate` は既存回帰テスト互換のため維持。
  3. **ゴースト透過（10% 刻み調整）・ホバー開花・内側アーク点滅・Popup トグル・左弧ツール・フォーカス表示回避・スロット右クリック統一**: 非ホバー時は `AppConfig.MicroGhostOpacityPercent`（既定 `30`＝`Opacity=0.30`、`10〜90` の 10% 刻みへ正規化）で半透明ゴースト表示し、右クリックメニュー「普段は透過する（ゴースト表示）」のサブメニュー（`10%〜90%`＋`透過しない（100%）`、右端 `InputGestureText` に現在値 `30%` / `OFF` 表示）から即時切替・永続化できる。AI 作業中／完了中は `Math.Max(idleOpacity, 0.86)` へ自動ブーストし、90% 設定時に 0.86 へ下がらないよう下限保証する。マウス進入で `1.0` へ実体化＋外周リングへ上弧 4（標準/縮小/最小化/閉じる）・左弧 2（ダウンロードフォルダ `-166.5°` / PR 受信箱 `+166.5°`・`UpdateBadge` 赤丸連動）・下弧 4（標準・縮小と同じ「AI アプリ＝左・控え＝右」原則で `✦ AIアプリ` / `前` / `背` / `≡ 控え`ピル。控えピルはカプセル左半円から中央円外周までの最短距離が他ボタンと完全一致の `9.0 DIP` になる幾何計算配置）が開花（退出は `240ms` 猶予）。補助 AI アプリ一覧（`✦`）は `ContextMenu` ではなく「控え一覧」と同じ `Popup(AllowsTransparency=True, StaysOpen=True)` で実装し、再クリックで確実に閉じるトグル動作を保証する。極小表示中に `FocusNameOverlay.Show` を呼ぶ際は `GetExpandedDialScreenRect()` を回避矩形として渡し、同じディスプレイ上で極小表示と重ならないよう真下（下に空きが無ければ上側）へ配置する。扇本体は縮小・標準表示と統一した色言語（起動済み＝緑 LED・緑発光 `A〜D`、起動中＝黄、停止中＝ローズ赤 LED・非発光グレー `A〜D`、非表示＝黄土）とし、1 面フォーカス中の太枠（`2.0px`）とホバー時の控えめ細枠（`0.9px`）を明確分離。A〜D ブロックは上下完全対称（`±25.2`）、ステータスドットは中央リング寄りの左右ミラー対称（左列 `A ●`・`C ●`／右列 `● B`・`● D`）、短縮名は `Viewbox(StretchDirection="DownOnly", MaxWidth=33.5)` ではみ出しを防止。AI 作業中は画面中央での目の疲れと背後アプリへの干渉を抑えるため、中央「非」リング側の内側円弧（3 層ソフトアーク）だけを `4.8 秒` 周期（`Opacity 0.16 ⇄ 1.00`）で静かにホタル点滅させる。未起動スロットの左クリック／中クリックでは起動せず、右クリックメニュー（`MainWindow.SlotContextMenu.cs` の `PopulateSlotContextMenuItems` を共通利用：先頭「起動する／閉じる」＋縮小同等スロット操作一式＋末尾「普段は透過する（サブメニュー）」「画面の中央へ戻す」、控え一覧／標準／縮小は削除）から明示的に起動・終了する。`ClearSlotInfoOverlay`・`DeleteStoredPanelOverlay` 表示時は `SuspendMicroDialForMainOverlay(expandForModal: true)` で一時退避・拡張して操作不能を防ぐ。
  4. **四角 ⇄ 真円の GPU 角丸モーフィング＆真円最小化/復元**: `ModeMorphGhostWindow` のプレートと画像層を `Rectangle` 化して `RadiusX/Y` を `8 ⇄ 58` へ同時補間し、`StartupRevealGhostWindow` へも `cornerRadius` 引数を追加して最小化吸い込み／復元まで一貫した真円表現にした。
  5. **3 モード絶対シルエット・ベクターアイコン統一（2026-11-26 追加）**: 3 状態（標準・縮小・極小）で従来の斜め拡大・縮小矢印（`\uE740` / `\uE73F`）が遷移先を誤認させるため、`MainWindow.MicroDial.cs` に 13×13 `Canvas` の絶対シルエット生成メソッド 3 種（`CreateStandardModeIcon`＝フル 4 タイル `12.0×12.0`、`CreateCompactModeIcon`＝極細横長バー `12.4×6.0`＋中央縦仕切り、`CreateMicroDialModeIcon`＝真円ダイヤル `12.0×12.0`）を揃え、親 `Button.Foreground` への `RelativeSource(FindAncestor, typeof(Button), 1)` バインディングで色を一括連動させた。`MainWindow.xaml`・`UpdateDisplayModeChrome`・`MicroDialWindow` 上弧 2 ボタンのすべてをこのベクターアイコンへ統一。
  6. **縮小・極小モードの単独終了確認カードウィンドウと真円消散演出（2026-11-26 修正）**: 従来 `ShowAppExitConfirmDialog()` は縮小（`430×146`）・極小（`116×116`）でも `EnsureExitConfirmViewport()` で本体 `MainWindow` を `430×250` へ引き伸ばし、極小では裏の `MainWindow` の Cloak まで外していたため、背後に縮小・極小画面と巨大な黒余白が露出し、終了確定時も復元せず引き伸ばされたままフェードアウトしていた。修正後は、`!IsStandardMode` のときは専用の透過角丸カードウィンドウ `Services/AppExitConfirmWindow.cs`（カード単体のみ・背景露出ゼロ・アンカー中央＆作業領域クランプ・既定フォーカスはキャンセル・`Esc` 対応）を表示し、本体のリサイズ・Cloak 解除を一切行わない。終了確定時（`ClosePanelWithVanishAnimationAsync`）も、縮小は本来のスリム寸法のまま、極小は `_microDialWindow.CaptureCoreCircleImage()`＋`cornerRadius: MicroDialWindow.CoreRadiusDip` で真円のまま消散する（標準やフォールバック経路でも `ConfirmAppExitAndCloseAsync` で `RestoreBoundsAfterExitConfirm()` を呼ぶ二重防御）。
  7. **極小モード実機フィードバック第 2 弾（真円キャプチャ上下枠切れ解消・非アクティブ右クリックメニュー確実実行＆自動閉鎖・中央ボタンからのドラッグ移動・内側ホタル点滅視認性向上・非ホバー透過方針の見直し、2026-11-26 追加）**:
     - **終了時などの真円キャプチャ上下枠切れ解消**: `_coreHost` の子要素として配置していた下方向へ 3 DIP 偏ったソフト影 2 枚（`_coreShadowOuter` / `_coreShadowInner`）を `_coreHost` の外側（`_dialRoot` の `_coreHost` 背面）へ分離し、`_coreHost` 自体を純粋な `116×116` 真円コアのみとした。さらに `CaptureCoreCircleImage()` に真円 `EllipseGeometry` クリップを加え、`StartupRevealGhostWindow` の真円消散時も `Stretch.Fill`・極小ウィンドウ基準の DPI を使用して上下端 0px 誤差の真円消散を保証した。
     - **非アクティブ `WS_EX_NOACTIVATE` 右クリックメニューからの GitHub 等の確実ジャンプ・自動閉鎖**: `MicroDialWindow` の `ContextMenu` は非アクティブ（`IsMenuMode=false`）のため、項目クリック後も `menu.IsOpen` が自動で閉じず、サブメニューがホバー展開されなかった。`AttachNonActivatingMenuBehaviors` を追加して全 `MenuItem`（サブメニュー内含む）へ ①リーフ項目クリック後の `CloseActiveContextMenu()` 自動閉鎖、②サブメニューヘッダーの `MouseEnter` 自動展開＆兄弟サブメニュー閉鎖、を実装（WPF 標準の `MenuItem.Click` 自体は非アクティブ下でも 1 回正常に発火するため、`PreviewMouseLeftButtonUp` からの手動 `RaiseEvent` 再発火は二重実行・GitHub タブ 2 枚開きの原因となるため禁止）。あわせて `TryAddElementScreenRect` で `PointToScreen` とネイティブ `GetWindowRect` の両方を包含し、引数なし `new RoutedEventArgs()` を全廃して有効な `RoutedEvent` 付きへ統一、メニュー構築前の `WindowSlot.RefreshWorkspaceLinks()` と外部ジャンプ各ハンドラへの `SuppressFocusedSlotReassertForPanelInput()` を追加した。
     - **中央「非/表」丸ボタン上からのドラッグ移動両立**: `CoreHost_PreviewMouseLeftButtonDown` で中央ボタン上の押下もドラッグ待機対象にし、`DragStartThresholdDip`（`7.0 DIP`）超でウィンドウ移動へ移行しつつ直後のボタン `Click` を `_suppressNextCenterButtonClick` と `e.Handled = true` で確実に抑止。閾値未満の単クリック時は従来どおり「非 ⇄ 表」トグルが動作する。
     - **実行中ホタル点滅（中央内側アーク）の視認性向上**: `BuildInnerWorkingArcLayer` の 3 層ソフトアークを両端 `3.0°` 内側（`78°`）で少し明るく・ほんの少し太め（外層 `5.4` / `#6452E896`、中層 `3.2` / `#DE82F5BA`、内層 `1.65` / `#FFE8FFF2`）へ調整し、ホタル点滅の下限不透明度を `0.16 → 0.26`（`0.26 ⇄ 1.00`, `4.8 秒` 周期）へ引き上げた。
     - **非ホバー透過方針（フォーカス中・実行中も薄く、完了・再ホバー時のみ濃く）**: `ComputeIdleOpacity` の `IdleAiAlertOpacity (0.86)` 自動ブースト対象を `AiActivityState.Done`（完了）のみに限定し、1 面フォーカス中および AI 実行中（`Working`）でもマウスオーバーが外れていれば設定透過率（既定 `30%`）で薄く表示する。さらに `RefreshVisuals` で `IsMouseOver == false` 時のホバー残留を取りこぼし補正する。
     - **4 扇の放射状対称ラベル配置（アルファベット中央寄り・タイトル外周寄り）**: 4 扇すべてでブロック中心を完全対称（`dx = ±22.0, dy = ±25.0`）に揃え、上段（A・B）は「タイトル上／アルファベット＋ドット下」、下段（C・D）は「アルファベット＋ドット上／タイトル下」の放射状対称とした（タイトル幅は `Viewbox(MaxWidth=31.5)` で全角 4 文字でも外周円弧へ干渉しない）。
- **注意**:
  - **禁止（WPF `RoutedEventArgs` の罠）**: コードビハインドからボタンイベントハンドラ（`OpenSlotWorkspaceFolderButton_Click` / `OpenSlotGitHubButton_Click` / `MoveSlotToNextDisplayButton_Click` 等）を直接呼ぶ際に、引数なしの `new RoutedEventArgs()` を渡さないこと。`RoutedEvent == null` の `RoutedEventArgs` に対して `e.Handled = true;` を代入すると WPF が `InvalidOperationException`（「すべての RoutedEventArgs に、それぞれに関連付けられた NULL 以外の RoutedEvent が必要です。」）を投げて操作が無反応になる。必ず `new RoutedEventArgs(MenuItem.ClickEvent, source)` や `new RoutedEventArgs(Button.ClickEvent, source)` のように有効な `RoutedEvent` を渡し、ハンドラ側でも `if (e.RoutedEvent is not null) { e.Handled = true; }` と二重防御すること（テストで `Panel` 全体に引数なし `new RoutedEventArgs()` が残っていないことを機械検証済み）。
  - `MicroDialWindow` の `_coreHost` の子要素へ `116×116` を超える装飾（ソフト影など）を追加しないこと（`VisualBrush(_coreHost)` の自動境界がズレて終了消散・最小化・モード切替キャプチャで円の上下枠が切れる）。影等の外周装飾は必ず `_dialRoot` 側（`_coreHost` の背面）へ配置する。
  - `MicroDialWindow` の本体円背面のソフト影 2 枚（`_coreShadowOuter` / `_coreShadowInner`）へ縦方向の `Margin` オフセット（ドロップシャドウ風のずらし）を付けないこと。影がずれると暗い縁取りが「上は細く下は太い」状態になり、円が中心からずれて見える。外側 +8 DIP・内側 +4 DIP の完全同心とし、`TestMicroDialCoreShadowConcentricWithCore` で上下左右の均一性を機械検証している。
  - `MainWindow.xaml` の `MicroSlotTemplate` / `AiMicroDoneWashStyle` は `AiDoneFireflyTests` が参照しフォールバックにも使うため削除しないこと。
  - **非アクティブ透過窓（`WS_EX_NOACTIVATE`）上の `ContextMenu` 外クリック・Esc 閉鎖（`SetForegroundWindow` 厳禁）**: `MicroDialWindow` は背後アプリのフォーカスを奪わないため `WS_EX_NOACTIVATE` を付与しており、そのままでは他アプリやデスクトップをクリックしても WPF `ContextMenu` のマウスキャプチャへ通知が届かず閉じない。一方で、**`ContextMenu` の内部 `Popup`（自体も `WS_EX_NOACTIVATE` 付き `0x08080088`）に対して `menu.Opened` で `SetForegroundWindow` を呼ぶと、実機で右クリック直後のフォアグラウンド権限下で WPF キャプチャが崩壊し開いた瞬間にメニューが閉じてしまうため厳禁**。解決策として、`SetForegroundWindow` は一切使わず、`ContextMenu` 表示中のみ軽量な `WH_MOUSE_LL`（ボタン押下限定・`WM_MOUSEMOVE` 無視）と `WH_KEYBOARD_LL`（`VK_ESCAPE` 押下限定）を張り、親 `ContextMenu` と展開中の全サブメニュー（`PART_Popup`）の物理画面矩形外クリックまたは `Esc` 押下を検知したら `Dispatcher.BeginInvoke` で確実に閉じる。フックデリゲートは GC 回収防止のためインスタンスフィールドへ保持し、閉鎖・`HideWithPopups`・`OnWindowClosedCleanup` で必ず `UnhookWindowsHookEx` すること。
  - 同一のベクター `Canvas` インスタンスを複数の `Button.Content` で共有しないこと（WPF の単一論理親制約で例外になる）。`DisplayModeButton` 用キャッシュ（`_standardModeButtonIcon` / `_compactModeButtonIcon` / `_microDialModeButtonIcon`）と他ボタン（`StandardJumpButton` / `CompactJumpButton` / `MicroJumpButton` / `MicroDialWindow` 上弧ボタン）は必ず別インスタンスを生成する。また `MicroDialWindow.AddCircularSatelliteWithContent` の `Button` には明示的に `Foreground = TextPrimaryBrush` を設定し、暗背景上で `Foreground` バインド線が黒く沈まないよう保つこと。
  - `ModeMorphGhostWindow` と `RestoreAnimation` の `Finish` 経路では、極小モードかつ `MicroDialWindow` 稼働中なら `MainWindow` の Cloak を外さず `_microDialWindow.ShowTopmost()` を呼び、失敗時や他モード時は必ず `SetCloaked(handle, false)` を呼ぶこと（本物 Cloak 残留防止）。
  - 控え一覧（`StoredOverviewPopup`）は極小モード時も `MicroStoredOverviewExtraGapDip` を足して外周ダイヤルと重ならない位置へ開く。代替案（案 C: 超極小 56px 宝石 → ミニダッシュボード変形）の設計は `docs/design/micro-mode-ui-concepts.md` に保管済み。

## 20. アプリ本体の最小化：タスクバーのボタンへ縮みながら吸い込まれる演出（2026-11-25 追加・同日作り直し）
- **ファイル**: `MainWindow.MinimizeAnimation.cs`, `MainWindow.StartupAnimation.cs`（`StartupRevealGhostWindow.PlayMinimizeSink` / `SetSoftenedImages`）, `Services/TaskbarButtonLocator.cs`, `Services/PanelImageSoftener.cs`, `MainWindow.xaml.cs`, `Services/WindowArranger.cs`（`TryGetMonitorAreasForWindow`）, `TurtleAIQuartetHub.Panel.Tests/MinimizeAnimationTests.cs`
- **課題**: `WindowStyle=None` のパネルは OS 既定の最小化アニメーションが付かず、最小化ボタンを押すと「カチッ」と消えていた。最初の版（タスクバーの縁へ沈ませ、縁で切り取る方式）は縮みが弱く（`0.56`〜`0.7`）、「四角いまま下へ落下するだけ」に見えて吸い込まれる印象にならなかった。
- **対策**:
  1. **着地点＝タスクバーのこのアプリのボタン（`Services/TaskbarButtonLocator.cs`）**: UI オートメーションで、パネルと同じディスプレイのタスクバー（`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`）から `AutomationId = "Appid: <Environment.ProcessPath>"`（大文字小文字無視）のボタンを探し、無ければボタンの名前（タイトル、または「タイトル - …」）で探す。問い合わせはスレッドプールで 1 本だけ走らせ、最後に見つかった位置を覚える。起動 `3 秒` 後に一度温め、最小化ボタンの `MouseEnter` で先読みし、押した時点では `3 秒` 以内の結果をそのまま使う（古ければ最大 `60ms` だけ待ち、間に合わなければ最後に分かった位置）。
  2. **代わりの着地点（`ResolveMinimizeTargetRect`）**: ボタンが見つからない・別のディスプレイにある・画面外（自動的に隠す設定）・大きさが不正なときは、ディスプレイ全体と作業領域の差がいちばん広い縁（＝タスクバー、差が無ければ下）の帯の上で、パネルの中心にいちばん近い位置へ `44 DIP` 角の着地点を置く。
  3. **縮みながら吸い寄せる（`BuildMinimizeSinkPlan` / `PlayMinimizeSink`、`340ms`。表示モード切替と同じ長さ）**: 起動・終了と同じ最小 3 レイヤーの影武者を中央基準で、最後の倍率＝ボタンに収まる大きさ（`0.035`〜`0.3` に制限、通常約 `0.07`）まで縮めながら、パネル中心→ボタン中心へ動かす。影武者の範囲はパネルと着地点の両方を含み、タスクバーの上にも重ねて描く。曲線は `SplineDoubleKeyFrame` の `KeySpline`（CSS の cubic-bezier と同じ）で、移動と縦の縮みは `(0.40, 0, 0.65, 0.80)`、横の縮みは少し先行する `(0.35, 0, 0.60, 0.85)`、薄れ方は `(0.55, 0, 0.90, 0.45)`。エメラルドの縁取りは輪郭を強調してしまうため、最小化では灯さない。
  6. **輪郭ごとふわっと空気に溶かす（`Services/PanelImageSoftener.cs`、2026-11-25 追加）**: 撮影したパネル画像を `1/4` に縮小し、周囲に透明な余白を足してから CPU で箱型ぼかし 3 回（半径 `2`＝元の大きさで約 `10px`、半径 `5`＝約 `22px`）をかけた「軽いぼかし」「強いぼかし」画像を作る（乗算済みアルファのまま処理、150% 表示の標準サイズでも数 ms）。影武者の中で奥から「強いぼかし → 軽いぼかし → くっきり（枠＋画像）」の順に重ね、ぼかし層は余白の分だけ外側へ広げて置く（輪郭が外へにじむ）。全体時間に対する割合で、くっきりは `0.08〜0.50` で引き、軽いぼかしは `0.02〜0.24` で現れて `0.38〜0.66` で引き、強いぼかしは `0.24〜0.52` で現れる。全体の不透明度は `0〜0.86` の区間を `KeySpline (0.50, 0, 0.75, 1.0)` で 0 にし、移動が道のりの約 9 割に達した時点（ボタンへ届く手前）で消えきって演出を終える。にじみを切らないよう、最小化の影武者の余白は `40 DIP`（起動・終了は `22 DIP`）。透過影武者へ毎フレームの `BlurEffect` は掛けない（§19 の禁止事項どおり、画像の入れ替えだけでぼかしを表現する）。
  4. **本物は待たせない**: 影武者の `ContentRendered` で本物を `SetTransitionsDisabled(true)` のうえ即座に `WindowState=Minimized` にする。本物を `DWMWA_CLOAK` で隠し続けないため、「本物が見えないまま残る」経路そのものが無い。
  5. **フェイルセーフ**: 後始末は `Finish` 1 か所（何度呼ばれても 1 回）。影武者が `400ms` 以内に描かれなければ即座に最小化し、描かれた後は全体期限（`20ms`＋`340ms`＋`1000ms`）で強制完了する。演出中の復元（`StateChanged`）と `OnClosed` では `CompleteMinimizeAnimationImmediately` で影武者をすぐ閉じる。`SC_MINIMIZE` はウィンドウプロシージャ内で撮影せず `Dispatcher.BeginInvoke` で始める。
- **注意**:
  - `SystemParameters.ClientAreaAnimation` と `SystemParameters.MinimizeAnimation` のどちらかがオフ、表示モード切替中（`_isModeTransitionRunning`）、終了演出中（`_isExitAnimationRunning`）、`WindowState != Normal` のときは従来どおり一瞬で最小化すること。
  - 本物の最小化を演出の後ろへ遅らせないこと（操作がもたつき、演出中の復元・タスクバーのサムネイルが不自然になる）。UI オートメーションの問い合わせを UI スレッドで同期実行しないこと（エクスプローラーが重いと UI が固まる）。
  - 共有する `KeySpline` は別スレッド（テストの STA スレッド）でも使うため必ず `Freeze()` しておくこと。
  - ぼかしを `BlurEffect` に置き換えないこと。最小化の影武者でも、くっきりした層の輪郭（1px 枠・エメラルドの縁取り）を最後まで残さないこと（「輪郭が残る」と指摘された）。

## 21. 最小化からの復元：タスクバーのボタンから広がって元の位置に収まる演出と、タスクバーのアイコンのクリックでの最小化⇄復元（2026-11-25 追加）
- **ファイル**: `MainWindow.RestoreAnimation.cs`, `MainWindow.StartupAnimation.cs`（`StartupRevealGhostWindow.PlayRestoreReveal`）, `MainWindow.xaml`（`ResizeMode="CanMinimize"`）, `MainWindow.xaml.cs`（`PanelWindowProcHook` / `MainWindow_StateChanged` / `ActivatePanelWindow`）, `TurtleAIQuartetHub.Panel.Tests/RestoreAnimationTests.cs`
- **課題**: 最小化から戻すとパネルが「パッ」と出ていた。また `ResizeMode="NoResize"` のため `WS_MINIMIZEBOX` が無く、他のアプリと違ってタスクバーのアイコンを押しても最小化されなかった（Windows は最小化ボックスの無いウィンドウをタスクバーのクリックで最小化しない）。
- **実機で確かめた事実（使い捨ての WPF ウィンドウで `WM_WINDOWPOSCHANGING` を記録）**:
  1. `ResizeMode=CanMinimize` にすると `WindowStyle=None` のままでも `WS_MINIMIZEBOX`（`0x00020000`）が付く（`NoResize`: `0x06080000` → `CanMinimize`: `0x060A0000`）。
  2. `SC_RESTORE`（タスクバーのクリックと同じ経路）や外部からの `ShowWindow(SW_RESTORE)` では、復元の `WM_WINDOWPOSCHANGING`（例: `x=20, y=20, flags=0x8120`＝`SWP_STATECHANGED|SWP_NOCOPYBITS|SWP_FRAMECHANGED`）の時点で WPF の `WindowState` はまだ `Minimized`。ただし `IsIconic` はすでに `false` なので判定に使えない。
  3. アプリ自身の `WindowState = Normal` では、WPF が先に値を変えるため `WM_WINDOWPOSCHANGING` の時点で `WindowState` はもう `Normal`。最小化時の退避は `x=y=-32000, flags=0x8170`。
- **対策**:
  1. **タスクバーのアイコンで最小化⇄復元**: `MainWindow.xaml` を `ResizeMode="CanMinimize"` にした（大きさは変えられないまま、`WindowChrome` の `ResizeBorderThickness=0` も維持）。前面にあるときにアイコンを押すと `SC_MINIMIZE` が届き、§20 の吸い込み演出に置き換わる。最小化中に押すと復元され、下の復元演出になる。
  2. **表示される前に隠す**: `PanelWindowProcHook` の `WM_WINDOWPOSCHANGING` で、`WindowState == Minimized` かつ `IsRestoreWindowPos`（移動あり・重なり順だけではない・退避座標 `-30000` 以下ではない）なら本物を `DWMWA_CLOAK` で隠して「復元待ち」にする。アプリ自身が戻す `ActivatePanelWindow` では、`WindowState = Normal` の直前に `ArmRestoreRevealBeforeProgrammaticRestore` で同じ準備をする。準備と同時にタスクバーのボタンの位置を調べ直す。
  3. **逆再生（`PlayRestoreReveal`、`340ms`。最小化・表示モード切替と同じ長さ）**: `StateChanged`（Normal）で、DPI 補正（Render 優先度）とレイアウトが終わるのを `DispatcherPriority.Loaded` で待ってから撮影し（cloak 中でも撮れる）、ボタンの位置・ボタン大の倍率・強いぼかしのもやの状態から、最小化と同じ `BuildMinimizeSinkPlan` を逆にたどって元の位置へ広げる。曲線は最小化の `KeySpline` を時間について裏返した `ReverseSpline`（`1 − p(1 − t)`：ボタンから素早く離れ、元の位置へふわっと減速して収まる）。重ね替えは最小化の時刻表（`MinimizeCrispBlend` など）を `MirrorBlendPoints` で裏返す（つなぐ S 字が左右対称なのでそっくり逆になる）。押した直後から待たせないよう、最小化の「消えきったあとの区間」は逆再生せず、はじめからボタン付近で現れ始める。最後のコマは本物と同じ見た目（くっきりした層だけが不透明）なので、cloak を外してから `70ms` 後に影武者を閉じる。
  4. **フェイルセーフ**: 復元待ちにしたのに最小化のままなら、`DispatcherPriority.Background` の後確認ですぐ cloak を外す。復元待ちは `800ms`、再生は全体期限（`60ms`＋`16ms`＋`340ms`＋`1200ms`）で強制的に cloak を外す。後始末は `Finish` 1 か所。再び最小化された（`StateChanged` の Minimized）、表示モードの切替（`SetDisplayMode`）、終了演出（`ClosePanelWithVanishAnimationAsync`）、`OnClosed` では `CompleteRestoreRevealImmediately` で復元待ち・再生をすぐ終わらせる。復元中は控え一覧の自動オープンを演出の後へ回す（本物が隠れている間に一覧だけが出ないように）。復元待ち・再生中は最小化の演出を始めない（`CanAnimateMinimize`）。
- **注意**:
  - 復元の判定に `IsIconic` を使わないこと（復元の `WM_WINDOWPOSCHANGING` の時点ですでに `false`）。アプリ自身が最小化から戻すコードを足すときは、`WindowState = Normal` の直前に `ArmRestoreRevealBeforeProgrammaticRestore()` を呼ぶこと（呼ばないと演出なしで「パッ」と出るだけで、隠れたままにはならない）。
  - `ResizeMode` を `NoResize` へ戻さないこと（タスクバーのアイコンで最小化できなくなる）。
