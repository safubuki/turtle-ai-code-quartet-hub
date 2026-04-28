# Turtle AI Code Quartet Hub 実装パターン・注意点リファレンス

本プロジェクトで壊しやすい実装パターン、ワークアラウンド、既知の制約をまとめる。
新機能追加や不具合修正の前に必ず確認すること。

## 1. 設定探索と状態永続化

### 1-1. 設定ファイルはワークスペース親階層まで遡って探索する

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Models/AppConfig.cs`
- **問題**: 開発時の実行ディレクトリや配布物の配置が変わると、設定ファイルが見つからなくなる。
- **対策**:
  - `%LOCALAPPDATA%` の config を最優先にする。
  - その後、`Environment.CurrentDirectory` と `AppContext.BaseDirectory` の親階層を順に遡って探索する。
  - `example.json` までフォールバックする。
- **注意**: 単純に実行 EXE 直下だけを見る実装へ戻すと、開発時の設定解決が壊れる。

### 1-2. `slots.json` は visible slot と stored panel を持つオブジェクト形式で保持する

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs`, `src/TurtleAIQuartetHub.Panel/Models/SavedPanelStateDocument.cs`
- **問題**: 表スロットと裏保存を同時に永続化する必要がある。
- **対策**:
  - `VisibleSlots` と `StoredPanels` を持つドキュメント形式で保存する。
  - 旧配列形式の legacy 読み込み互換も残す。
- **注意**: 保存形式を変える場合は legacy 読み込みを壊さないこと。

### 1-3. 保存済みワークスペースは live のタイトル確認付きで扱う

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeWorkspaceState.cs`, `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs`
- **問題**: workspaceStorage の最新情報だけでは、今その VS Code が本当にそのワークスペースを開いているとは限らない。
- **対策**:
  - workspaceStorage から候補を読む。
  - その候補が現在のウィンドウタイトルに現れているときだけ確定扱いにする。
  - `CurrentWorkspacePath` を UI 表示の優先値にする。
- **注意**: `SavedWorkspacePath` を常に優先すると、起動直後や別ワークスペース表示時に誤表示になる。

## 2. ウィンドウ起動と 4 分割配置

### 2-1. VS Code は既知ウィンドウ集合との差分で 1 スロットずつ割り当てる

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs`
- **問題**: VS Code は single-instance やリモート接続の都合で、新規 HWND の出現タイミングが不安定。
- **対策**:
  - 起動前後の既知 HWND 集合を比較する。
  - スロットごとに順番に起動して、新しいウィンドウを 1 つ見つけてから次へ進む。
  - WinEvent hook とポーリングの両方で新規ウィンドウを待つ。
- **注意**: 4 つ同時起動に寄せると、どの新規ウィンドウがどのスロットか分からなくなりやすい。

### 2-2. ゾンビ VS Code プロセスを除去してから dedicated user-data を準備する

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs`, `src/TurtleAIQuartetHub.Panel/Services/SlotUserDataPaths.cs`
- **問題**: code.lock を保持したウィンドウなしプロセスが残ると、新規ウィンドウが作られないことがある。
- **対策**:
  - スロット起動前にロック保持プロセスを確認する。
  - 必要に応じて entire process tree を終了する。
  - その後、スロット別 user-data-dir を整える。
- **注意**: dedicated user-data を使うときはログ・workspaceStorage・globalStorage の配置が判定ロジックに直結する。

### 2-3. 配置処理は `SWP_NOACTIVATE` 前提で行う

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`
- **問題**: 配置中に managed VS Code が勝手に前面化すると、パネル UI の操作感が崩れる。
- **対策**:
  - `SetWindowPos` は `SWP_NOACTIVATE` を含むフラグで呼ぶ。
  - 配置後に必要ならパネルだけ前面へ戻す。
- **注意**: 配置と前面化を同時に扱うと race が起きやすい。責務を分けること。

## 3. 前面/背面制御と集中表示

### 3-1. focused slot は `IsFocused` と実ウィンドウ最大化の両方で管理する

- **ファイル**: `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`, `src/TurtleAIQuartetHub.Panel/Models/WindowSlot.cs`
- **問題**: UI 上の focused 表示だけでは、実ウィンドウの z-order と一致しない。
- **対策**:
  - focused slot を `StatusStore.SetFocusedSlot` で保持する。
  - 対象 VS Code は `FocusMaximized` で最大化・前面化する。
  - 解除時は 4 分割へ戻し、focused フラグを外す。
- **注意**: focused 状態の再現は `IsFocused` だけでは不十分。前面復帰時の再適用が必要な場面がある。

### 3-2. managed VS Code の層制御は `BringToFrontOnce` と `SetBackmost` を明示的に使い分ける

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`, `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`
- **問題**: 常時 topmost にし過ぎると他アプリ操作を邪魔し、notopmost に戻し過ぎると focused slot が埋もれる。
- **対策**:
  - 一括最前面は `BringToFrontOnce` を使い、topmost に上げてから notopmost に戻す。
  - 一括最背面は `SetBackmost` を使う。
  - パネル自体は最後に前へ戻す。
- **実装メモ 2026-04-28**:
  - focused mode は `SendOtherSlotsToBack` と `FocusMaximized` で z-order を専有するため、グローバルな `最前面` / `最背面` 操作の前には必ず focused を解除して 4 分割へ戻す。
- **注意**: `BringToFrontOnce` は race に弱い。最小化や再アクティブ化と競合させないこと。

### 3-3. パネル前面復帰は遅延タスクで行われる

- **ファイル**: `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`
- **問題**: 配置や focused 切替の直後に即時前面復帰すると、ちらつきや OS 側の z-order 競合が起きる。
- **対策**:
  - `SchedulePanelToFront` で遅延実行する。
  - 直前の予約はキャンセルして最新だけ生かす。
- **実装メモ 2026-04-28**:
  - `MainWindow.MinimizeButton_Click` で pending な前面復帰をキャンセルしてから最小化する。
  - `SchedulePanelToFront` と `BringPanelToFrontImmediate` は `WindowState == Minimized` の間は何もしない。
  - `StateChanged` / `Activated` で focused slot の前面状態を再適用する。
- **注意**: 最小化遷移中までこの遅延前面復帰を走らせると、ハングや最小化失敗の原因になり得る。

### 3-4. 標準表示と縮小表示の切替は右上基準で往復させる

- **ファイル**: `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`
- **問題**: 標準表示と縮小表示の切替を左上基準で行うと、右側に寄せて使っているときにボタン操作のたびにパネルが左へ伸び縮みして見える。
- **対策**:
  - 縮小時は縮小前の standard bounds の right/top を保持して compact bounds を計算する。
  - 標準復帰時も compact bounds の right/top を保持して復元する。
  - compact bounds は再利用せず、切替のたびに現在の panel 右上から target bounds を再計算する。
  - panel の bounds 反映は `Left` / `Top` / `Width` / `Height` の逐次更新ではなく、Win32 の一括 move/resize で適用する。
- **実装メモ 2026-04-28**:
  - Win32 で panel を移動した後は WPF の `Left` / `Top` / `Width` / `Height` が stale になることがあるため、right/top 復元は HWND 実測 bounds を使う。
  - compact 高さは現在の ActualHeight を使い回さず、target compact width を前提に measure して決める。
  - compact 高さは `RootLayoutGrid` 全体ではなく、表示中の title row と `CompactBarPanel` だけを元に計算する。
- **注意**: 実ウィンドウへ先に `Width` / `Height` を当ててから位置を直すと top-right 固定でもボタンが動いて見えるため、目標 bounds を先に作って一括反映すること。

## 4. オーバーレイと標準表示 UI の二重表現

### 4-1. VS Code 外周枠は別ウィンドウの overlay で描く

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/WindowFrameOverlayManager.cs`
- **問題**: VS Code 本体の外周に状態色を載せたいが、対象アプリの描画は直接変更できない。
- **対策**:
  - 透明 WPF ウィンドウを作り、対象 VS Code の bounds より少し大きい矩形に重ねる。
  - `PositionOverlayAbove` で対象 HWND の直上へ配置する。
  - `Running`, `Completed`, `WaitingForConfirmation` のときだけ表示する。
- **注意**: overlay は別ウィンドウなので、最小化・focused・非表示状態との同期を必ず考慮すること。

### 4-2. 標準表示 / 縮小表示のカード側アニメーションは XAML で独立管理している

- **ファイル**: `src/TurtleAIQuartetHub.Panel/MainWindow.xaml`
- **問題**: 外周 overlay だけに頼ると、パネル内の状態視認性が不足する。
- **対策**:
  - `AiPill` と compact badge に Running / Completed / WaitingForConfirmation の pulse animation を持たせる。
  - focused カードにも別の pulse animation を持たせる。
- **注意**: focused カードの pulse と AI 状態の pulse は別概念。カード全体の AI 色付けを戻す場合、focused スタイルとの優先順位を設計すること。

### 4-3. focused 中は overlay を全面非表示にする設計になっている

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/WindowFrameOverlayManager.cs`
- **問題**: 集中表示時まで全スロット overlay を出すと視覚ノイズが増える。
- **対策**:
  - どれか 1 スロットでも `IsFocused` なら `HideAll` する。
- **注意**: ユーザー要求が「focused 中でも対象枠は維持」に変わる場合、この条件式の見直しが必要。

## 5. AI 状態検出

### 5-1. 判定優先順位は `UI Automation > ログ > 短時間キャッシュ` が基本

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`, `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`
- **問題**: ログは遅延し、UI は履歴に引っ張られ、どちらも単独では誤判定がある。
- **対策**:
  - まずログから明確な Running / Completed / Error / NeedsAttention / WaitingForConfirmation を探す。
  - UI Automation で running や confirmation を補う。
  - 直前観測を hold window で短時間保持する。
  - Confirmation は Running より優先して返す。
- **注意**: source priority を崩すと、「実行していないのに running/completed を往復する」症状が再発しやすい。

### 5-2. Codex と Copilot は内部では別ログソースとして扱う

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`
- **問題**: Codex と Copilot Chat ではログ名、running シグナル、完了シグナル、confirmation シグナルが異なる。
- **対策**:
  - `Codex.log` と `GitHub Copilot Chat.log` を別 source として扱う。
  - Codex は `ephemeral-generation`, `thread-stream-state-changed`, `commandExecution/requestApproval` を利用する。
  - Copilot は `ccreq:` や success/cancel 系文字列を利用する。
- **実装メモ 2026-04-28**:
  - source ごとの evidence を一度分離してから、最新イベント近傍だけを集約対象にすることで stale な高優先度判定を抑える。
  - Copilot の `GitHub Copilot Chat.log` 自体は取得できていても、`ccreq:... | markdown` の背景行や `[title]` / `[copilotLanguageModelWrapper]` の success を実行本体として扱うと false Running / false Completed が出やすい。
  - Copilot は background markdown を running 根拠から外し、metadata completion も completed 根拠から除外する。
  - VS Code の新しい chat UI では live running 文言が `Optimizing tool selection...` のような独立要素として出ることがあり、親要素の文脈は英語 `chat` ではなく日本語 `チャット` や `interactive-session-status*` class になる場合がある。UI running 検出はこの文脈差分を吸収すること。
  - Copilot の `| success |` / `request done:` / `message 0 returned` / `Stop hook result:` はユーザー表示がまだ `Optimizing tool selection...` などの Running 中でも先に出る。Copilot ではこれらを即 Completed 根拠に使わず、Running は UI を主、Completed は Running 消失後の遷移で扱うこと。
- **注意**: 内部 source を統合 UI へそのまま潰すと、原因調査が難しくなる。将来の改善では per-engine evidence を残すこと。

### 5-3. Codex には quiet window による完了推定がある

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`
- **問題**: Codex は明確な完了イベントを常に出すとは限らず、UI が過去履歴に反応して running 誤判定する。
- **対策**:
  - 最後の activity signal から一定時間経過したら completed 扱いにする。
  - UI が running を示していても、ログ側の stream 停止が長ければ completed を優先する。
- **実装メモ 2026-04-28**:
  - quiet completion は 10 秒へ揃えた。
  - Completed は短時間観測としてのみ扱い、Codex の最終 activity から 45 秒を超えた completion は stale とみなして Idle へ落とす。
  - UI Automation が Running を示していても、根拠になる Codex activity が 45 秒より古い場合は履歴表示とみなして無視する。
- **注意**: quiet window の秒数は拡張更新で再調整が必要になり得るため、調整時は smoke 結果で再確認すること。

### 5-8. AI 状態の検証は temp build の smoke を基準にする

- **ファイル**: `tools/AiStatusSmoke/Program.cs`, `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`
- **問題**: 通常の `dotnet run` は実行中の本体 EXE ロックに巻き込まれやすく、`--no-build` は古いバイナリを使って誤った結論を出しやすい。
- **対策**:
  - `dotnet build .\tools\AiStatusSmoke\AiStatusSmoke.csproj -p:BaseOutputPath=.\.build-tmp\smoke\` で temp 出力へビルドする。
  - 生成された `AiStatusSmoke.dll` を直接実行して現在の判定を確認する。
  - stale completion 修正後は「何も実行していない状態で全スロット Idle」を smoke の基準値にする。
- **注意**: `SlotName` や `EventTime` ではなく、実際の出力プロパティ `Slot` と `EventAt` を読むこと。

### 5-9. smoke のウィンドウ解決では workspaceStorage を確定状態より優先しない

- **ファイル**: `tools/AiStatusSmoke/Program.cs`
- **問題**: slot に保存済みの確定 workspace があるのに `workspaceStorage` の最新履歴を高優先度で使うと、A/B のように別スロットの VS Code ウィンドウへ誤マッチして AI 状態まで入れ替わる。
- **対策**:
  - `SavedWorkspaceConfirmed` が true のときは saved workspace / assigned path を優先し、`workspaceStorage` は未確定時の補助に落とす。
- **注意**: smoke で window title と assigned path が食い違う場合、AI 検出を疑う前に window 解決の cross-match を先に疑うこと。

### 5-10. 検証用 PowerShell スクリプトは ASCII 安全と temp-built DLL 前提にする

- **ファイル**: `scripts/Invoke-AiStatusSmoke.ps1`
- **問題**: Windows PowerShell 5.1 では日本語文字列を含むスクリプトが文字化けして parse error になりやすく、さらに `dotnet run` は本体 EXE ロックや build 出力混入で JSON パースを壊しやすい。
- **対策**:
  - helper script の監視キーワードとエラーメッセージは ASCII 安全に寄せる。
  - `dotnet run` ではなく temp-built `AiStatusSmoke.dll` を直接呼ぶ。
- **注意**: PowerShell 側で JSON を扱うときは stdout に build メッセージが混ざっていないことを必ず確認すること。

## 7. ビルドと検証

### 7-1. 実行中のパネルは既定の `bin\Debug` 出力をロックする

- **ファイル**: `README.md`, `scripts/Build-Panel.ps1`
- **問題**: `dotnet build` / `dotnet run` は既定で `src/TurtleAIQuartetHub.Panel/bin/Debug/net10.0-windows/TurtleAIQuartetHub.exe` を使うため、実行中の本体が同じ exe を掴んでいると `MSB3026` のコピー失敗が続く。
- **対策**:
  - 反復ビルドや検証は `dotnet build --artifacts-path ...` か `scripts/Build-Panel.ps1` を使い、毎回別 artifacts path へ出力する。
  - 起動中の本体を残したまま検証するときは、既定の `bin/Debug` を上書きしない。
- **注意**: 開発中に既定の `bin/Debug` を直接起動すると次の通常ビルドで再びロックに当たる。ロックを避けたまま再ビルドしたいときは script 側の出力物から起動する。

### 7-2. temp build と調査用ファイルは git ノイズへ混ぜない

- **ファイル**: `.gitignore`
- **問題**: `.build-tmp` 配下や `inspect_ui*.ps1`、`smoke*.json` などの調査残骸が残ると、必要な本体修正より変更数の見た目が大きくなり、レビューと切り分けを誤りやすい。
- **対策**:
  - `**/.build-tmp/` を ignore する。
  - 調査専用の一時ファイルは root や `scripts/` に残さず削除する。
- **注意**: temp build を運用に組み込んだなら、ignore もセットで維持しないと毎回同じノイズが再発する。

### 5-5. UI Automation は履歴表示を Running と誤認しないよう Completed に寄せる

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`
- **問題**: `Ran 3 commands` や `実行済みコマンド` のような履歴表示を Running と誤認すると、何もしていないのに Running / Completed を往復しやすい。
- **対策**:
  - Running 用の prefix と Completed 用の prefix を分離する。
  - `Ran` 接頭辞、`実行済みコマンド`、`編集済みファイル` などは Completed として扱う。
- **注意**: UI 表記は VS Code / 拡張更新で変わり得るため、marketplace の更新や release notes で語彙差分を追うこと。

### 5-6. 汎用的な確認ボタンは追加コンテキスト必須にする

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`
- **問題**: `Yes`, `Run`, `Approve` のような汎用ラベルは、チャット以外や履歴上の UI でも現れ得る。
- **対策**:
  - `Continue`, `Allow`, `続行`, `許可` はそのまま confirmation 候補にする。
  - `Yes`, `Run`, `Approve`, `実行`, `承認` などは、周辺に approval / command / terminal / 承認 / 許可 等の文脈があるときだけ confirmation とみなす。
- **注意**: generic action 名を無条件で confirmation に戻すと、起動直後の誤判定が再発しやすい。

### 5-7. 外部 change history を踏まえて UI 文字列は可変とみなす

- **ファイル**: `docs/telemetry-notes.md`, external release notes / marketplace pages
- **問題**: VS Code 1.117 では chat rendering や background terminal notifications が更新され、Copilot Chat は VS Code と lockstep、Codex 拡張も継続更新されている。UI 文言や DOM/Automation 構造が固定とは限らない。
- **対策**:
  - release notes と marketplace 更新日を前提に、固定フレーズ依存を減らし、文脈付き判定を優先する。
  - generic なボタン名や履歴文言は単独で running / confirmation に使わない。
- **注意**: 拡張更新後はログシグナルだけでなく UI Automation の検出語彙も再棚卸しすること。

### 5-4. confirmation 検出はチャット文脈付きボタンのみを使う

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`
- **問題**: VS Code 全体に存在する一般的なボタンや stop アイコンを拾うと誤判定する。
- **対策**:
  - `chat`, `copilot`, `codex`, `agent` の文脈断片を含む要素に限定する。
  - `Continue`, `続行`, `Allow`, `許可` などの確認ボタンを優先する。
- **注意**: UI テキスト検出はロケールや VS Code バージョン差に弱い。Stable + GitHub Copilot Chat + Codex を基準に検証すること。

## 6. スロット交換と裏保存

### 6-1. visible slot を入れ替えるときは detector session も交換する

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs`
- **問題**: 画面上の A-D を入れ替えても detector 側の直前 running/confirmation キャッシュが元スロット名に残ると、状態が混線する。
- **対策**:
  - `SwapSlotContents` で見た目のプロパティだけでなく `_aiStatusDetector.SwapSlotSessions` を呼ぶ。
  - workspace refresh timestamp も swap する。
- **注意**: スロット入替時に detector cache を移さない修正は厳禁。

### 6-2. hidden は missing と別状態として扱う

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Models/WindowSlot.cs`, `src/TurtleAIQuartetHub.Panel/Services/WindowEnumerator.cs`, repo memory
- **問題**: 一時的に非表示にした VS Code を Missing と誤判定すると、状態監視・復帰操作が壊れる。
- **対策**:
  - hidden フラグを別に持ち、WindowStatus は Ready のまま保つ。
  - UI 文言だけ `非表示` に寄せる。
- **注意**: `IsWindowVisible` だけで Missing 判定してはいけない。

### 5-11. AI 状態検出の条件強化は検出感度を殺すリスクがある

- **ファイル**: `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`, `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`
- **問題**: 誤検知防止のために検出条件を強化した結果、AI 状態が全く取得できなくなる事故が 2026-04-28 に発生。
- **原因**: 以下の 5 つの変更が複合して検出感度を殺した。
  1. `VscodeChatUiStatusReader` の Running 検出に `HasChatContext`（親要10階層遍歴）を必須化 → VS Code の UI 構造次第で親に chat 文脈がない場合に Running を見落とす
  2. Stopボタン（codicon-stop、中断、Cancel）による Running 検出を完全削除 → もう一つの Running 検出経路が消失
  3. Confirmation 検出に `IsActionLikeElement` + `HasChatContext` を追加 → 検出感度激減
  4. Copilot の `CompletionSignals` を空配列に変更 → ログからの Copilot 完了検出が不可能に
  5. `TryCarryForwardCodexFromCurrentSession` を削除 → 初回起動時の Codex 実行状態引き継ぎが不可能に
- **対策**: 上記 5 点をすべて旧版にロールバックして復旧。
- **教訓**:
  - 誤検知防止のための条件強化と、検出感度の維持はトレードオフの関係にある。強化するときは必ず smoke テストで「実行中に Running を検出できるか」を確認すること。
  - `CompletionSignals` を空にすると、そのエンジンの完了検出が不可能になる。除外したいシグナルがある場合は空にせず `IgnoredRunningSignals` のように除外リストで対応すること。
  - UI Automation の検出経路を削除する際は、代替経路が十分に機能することを確認してから削除すること。
- **注意**: **このパターンの再発防止が最優先。** AI 状態検出の条件を変更するときは、「実行中に Running を検出できるか」「確認待ちに Confirmation を検出できるか」「完了後に Completed を検出できるか」の 3 点を smoke で必ず検証すること。

## 横断的な注意点まとめ

| カテゴリ | 注意点 |
| -------- | ------ |
| 設定探索 | 実行 EXE 直下固定に戻さず、親階層遡りと `%LOCALAPPDATA%` 優先を維持する |
| 保存復元 | `slots.json` の object 形式と legacy 互換を壊さない |
| ワークスペース推定 | workspaceStorage だけで確定せず、現在タイトルとの一致を必ず見る |
| 起動割当 | 4 窓同時起動でなく、スロット単位の直列割当を維持する |
| 前面復帰 | `SchedulePanelToFront` は便利だが、最小化や再アクティブ化と競合させない |
| overlay | overlay は別ウィンドウ。最小化・focused・hidden と必ず同期させる |
| UI 表現 | focused 表現と AI 状態表現は別概念なので、優先順位を明示して実装する |
| AI 検出 | Confirmation を Running より優先し、source priority を崩さない |
| Codex quiet window | 既定は 15 秒。調整時は smoke で検証する |
| スロット交換 | detector session と timestamp を一緒に swap しないと状態が混線する |
| **AI 検出条件変更** | **Running/Confirmation/Completed の検出経路を削除・制限する場合は必ず smoke で検出可能性を確認する。誤検知防止と検出感度はトレードオフ** |
