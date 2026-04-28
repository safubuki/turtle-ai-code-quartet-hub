# 現在の既知課題と QA 方針

更新日: 2026-04-28

## 1. 確認済み QA

### Q1. 本体パネルを最小化したとき、管理中 VS Code はどうあるべきか

- **A**: パネルのみ最小化し、管理中 VS Code とその枠表示は維持する。

### Q2. 完了状態はどう表示すべきか

- **A**: 青で点滅させる。

### Q3. Copilot と Codex の状態はどう扱うべきか

- **A**: 内部判定は分離し、最終 UI 表示は 1 つのスロット状態へ集約する。

### Q4. 一画面フォーカス中に背面化してから前面復帰したとき、何を最優先で再現すべきか

- **A**: 最後にフォーカスしたスロットを維持する。

### Q5. 主な検証環境は何か

- **A**: Stable + GitHub Copilot Chat + Codex。

## 2. 課題一覧と対応方針

### 2-1. 最小化時に枠表示中だと本体が最小化できずハングする

- **現象**:
  - VS Code 外周枠が表示されている状態で本体パネルを最小化すると、最小化が完了せずハングアップする。
- **関連実装**:
  - `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`
  - `src/TurtleAIQuartetHub.Panel/Services/WindowFrameOverlayManager.cs`
  - `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`
- **現時点の読み**:
  - 最小化ボタンは `WindowState = Minimized` だけで、最小化遷移専用の抑止処理がない。
  - パネル前面復帰の遅延処理と overlay 別ウィンドウが、最小化中も競合している可能性が高い。
- **対応方針**:
  1. パネル最小化前に pending な `SchedulePanelToFront` を必ずキャンセルする。
  2. 最小化中は overlay を全面停止する。
  3. 復元時のみ overlay を再表示し、VS Code 側の z-order は崩さない。
  4. 「パネルのみ最小化、VS Code と枠表示維持」という QA に沿って、最小化中の overlay の見せ方を明文化する。
- **2026-04-28 実装反映**:
  - pending な panel front restore を最小化前にキャンセルするよう修正。
  - panel が最小化中は新たな front restore を抑止するよう修正。
- **回帰リスク**:
  - focused 復帰ロジックや常時 TopMost の制御を壊しやすい。
  - 復元時にパネルが勝手にアクティブ化し過ぎると使い勝手が悪化する。
- **最低限の確認**:
  - Running / Completed / Confirmation 各状態で最小化→復元できるか。
  - focused 中と 4 分割中の両方で再現しないか。

### 2-2. 標準表示中も VS Code とパネルカードの両方で状態枠を維持したい

- **現象**:
  - ユーザー期待として、標準表示中も VS Code 本体に枠を出しつつ、パネルカード側でも緑 / 黄 / 青の点滅点灯が必要。
- **関連実装**:
  - `src/TurtleAIQuartetHub.Panel/MainWindow.xaml`
  - `src/TurtleAIQuartetHub.Panel/Services/WindowFrameOverlayManager.cs`
- **現時点の読み**:
  - 現在の XAML には AI pill と compact badge の pulse がある。
  - ただしカード全体の border は focused 状態しか pulse しておらず、AI 状態色との直結はない。
  - overlay は focused 中を除き Running / Completed / WaitingForConfirmation で出る設計。
- **対応方針**:
  1. 既存の AI pill pulse は維持する。
  2. 標準表示カードの外周にも AI 状態連動の border 表現を戻すか追加する。
  3. focused 表現と AI 状態表現が競合しない優先順位を定義する。
  4. completed は QA に従い青で点滅とする。
- **2026-04-28 実装反映**:
  - 標準表示カードの border に Running / Completed / WaitingForConfirmation の pulse を追加。
  - focused slot は既存の focused 表現を優先するようにした。
- **回帰リスク**:
  - focused 色と completed/running 色が競合して視認性が落ちる。
  - XAML Resource 名を崩すと起動時 `XamlParseException` になる履歴がある。
- **最低限の確認**:
  - Standard / Compact の両モードで Running, Completed, WaitingForConfirmation の視認性を確認する。
  - focused slot にも意図した表示優先順位が適用されるか確認する。

### 2-3. Copilot / Codex の AI 状態検出が不正確で、開始・確認中・完了が混線する

- **現象**:
  - 何も実行していないのに Running と Completed を繰り返す。
  - 起動直後に確認中へ入ることがある。
  - GitHub Copilot と Codex の状態が取得しづらく、現状ロジックが正しく分離できていない可能性がある。
- **関連実装**:
  - `src/TurtleAIQuartetHub.Panel/Services/AiStatusDetector.cs`
  - `src/TurtleAIQuartetHub.Panel/Services/VscodeChatUiStatusReader.cs`
  - `docs/telemetry-notes.md`
  - `tools/AiStatusSmoke/Program.cs`
- **現時点の読み**:
  - 内部では Codex と Copilot の log source は分かれているが、最終スロット状態は単一の `AiStatusSnapshot` へ潰している。
  - UI テキストマッチは locale と VS Code バージョン差に弱い。
  - Codex の quiet completion 秒数について、repo memory と現コードに差分がある。
- **対応方針**:
  1. 内部 evidence モデルを engine ごとに分ける。
  2. UI 表示は最終的に 1 つへ集約するが、内部ログでは Copilot / Codex を分けて保持する。
  3. Stable + GitHub Copilot Chat + Codex を基準に running / confirmation / completed シグナルを再棚卸しする。
  4. `AiStatusSmoke` と実ログ確認を前提に、quiet window と hold window を再調整する。
  5. VS Code 拡張画面、変更履歴、ログ文字列、完了メッセージの差分を収集し、ソースごとの誤検知条件を整理する。
- **2026-04-28 実装反映**:
  - source ごとの log evidence は最新イベント近傍だけを集約対象にするよう修正。
  - UI Automation で `Ran ...` 系の履歴表示を Completed 扱いに分離。
  - `Yes` / `Run` / `Approve` 系は追加コンテキスト必須へ変更。
  - Codex quiet completion は 10 秒へ調整。
  - `_completedAtBySlot` と log completion は 45 秒を超えたら stale とみなして Idle へ落とすよう修正。
  - Codex の最終 activity が古い場合、UI Automation の Running 表示は履歴由来として無視するよう修正。
  - temp build した `AiStatusSmoke` で再検証し、2026-04-28 01:13 時点の 4 スロットはすべて Idle を確認。
  - Copilot については `GitHub Copilot Chat.log` の取得失敗ではなく、背景 `ccreq:... | markdown` 行と `[title]` / `[copilotLanguageModelWrapper]` metadata success が false Running / false Completed を作っていたため、これらを集計対象から除外するよう修正。
  - smoke の window 解決では `workspaceStorage` 優先が強すぎて A/B の監視対象が入れ替わるケースがあったため、confirmed workspace 優先へ修正。
  - Copilot の live running 文言 `Optimizing tool selection...` は日本語 `チャット` / `interactive-session-status*` 文脈配下に出ることがあるため、UI Automation の chat context 判定を拡張。
  - `Invoke-AiStatusSmoke.ps1` は文字化けと `dotnet run` 混入出力で検証を誤らせていたため、temp-built DLL 直実行と ASCII 安全な script へ修正。
  - 実行中 panel プロセスが修正前ビルドのまま残っていると表示は更新されないため、最新 build への再起動が必要。
  - 追加調査で、Copilot の `| success |` / `request done:` / `message 0 returned` / `Stop hook result:` は UI の Running 表示終了前に出ることを確認したため、Copilot の Completed はこれらのログ行へ直結させず、UI Running の消失ベースへ寄せた。
  - `.build-tmp` と調査一時ファイルを `.gitignore` と削除で整理し、変更数の見た目ノイズを除去した。
- **外部 change history メモ**:
  - VS Code 1.117 では chat rendering / agent UI / background terminal notifications が更新されている。
  - Copilot Chat は VS Code と lockstep で更新される。
  - OpenAI Codex 拡張 marketplace 更新日は 2026-04-25。
- **回帰リスク**:
  - aggressive な running 判定は false positive を増やす。
  - completed 優先に寄せ過ぎると running の取りこぼしが増える。
  - confirmation を Running より後ろに置くと、承認待ちが埋もれる。
- **最低限の確認**:
  - 起動直後の idle 安定性
  - 実行開始直後の running 反映
  - 承認待ちダイアログ時の waiting for confirmation
  - 完了後の completed 持続と idle への戻り
  - Copilot 実行と Codex 実行を別々に試した結果の比較
  - Copilot が未実行の状態で background markdown / title 更新だけでは Completed に張り付かないこと
  - temp build smoke で stale な Completed / Running が残っていないこと

### 2-4. focused slot を前面復帰したいのに、背面化後に D スロットが最前面へ出る

- **現象**:
  - あるパネルを focused 表示にした後、アプリを背面へ移動し、再度前面へ出すと focused slot ではなく右下の D が前へ来ることがある。
- **関連実装**:
  - `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`
  - `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`
- **現時点の読み**:
  - focused 状態は `IsFocused` にあるが、再アクティブ化時に focused slot を再適用する専用フックは見当たらない。
  - layer 一括適用や arrange の流れで、最後に処理されたスロットが z-order 的に勝つ可能性がある。
- **対応方針**:
  1. 最後に focused だった slot を明示的に保持する。
  2. パネル再アクティブ化 / 復元時に、その slot の最大化・前面状態を再適用する。
  3. focused 中は全スロットへの layer 再適用を避ける。
  4. 4 分割復帰はユーザーが focused を解除したときだけ行う。
- **2026-04-28 実装反映**:
  - focused slot に入る際は他スロットを背面へ送るよう修正。
  - `Activated` / `StateChanged` で focused slot の前面状態を再適用するよう修正。
  - focused 中に `最前面` / `最背面` を押した場合は、先に focused を解除して 4 分割へ戻してから全 managed window の layer を変更するよう修正。
- **回帰リスク**:
  - panel 側の TopMost 維持と focused VS Code の再前面化が競合する。
  - hidden モードや最背面モードとの組合せで順序が崩れる。
- **最低限の確認**:
  - A-D の各スロットで focused → 背面化 → 前面復帰を試す。
  - タスクバー経由、Alt+Tab 経由の両方を確認する。

### 2-5. AI 実行中の標準/縮小切替や配置変更でパネルが固まる

- **現象**:
  - AI 実行中に標準表示/縮小表示の切替、表示/非表示、モニター移動などを行うと、パネルが固まることがある。
- **原因**:
  - `VscodeChatUiStatusReader` が VS Code の UI Automation RawView を最大 6000 要素まで走査していた。
  - 750ms の status refresh ごとに全スロットが UIA 対象になり、`panel.log` では 1 回の refresh が 2.5〜5.4 秒へ伸びていた。
  - AI 状態 overlay 更新や Win32 ウィンドウ操作と UIA 走査が重なり、リサイズ/ズーム系操作時に固まりやすくなっていた。
- **2026-04-28 実装反映**:
  - UIA probe は 1 refresh 最大 1 スロットへ制限し、A-D をラウンドロビンで回すよう変更。
  - UIA の Running / WaitingForConfirmation は短時間キャッシュし、probe を間引いても表示が途切れにくいよう変更。
  - RawView 走査は最大 1500 要素、約 220ms、Running 検出後 240 要素までに制限。Confirmation は見つけた時点で即返すよう変更。
- **最低限の確認**:
  - AI 実行中に標準/縮小を連続切替しても固まらないこと。
  - `panel.log` の `Status refresh took` が常時 1 秒を大きく超え続けないこと。
  - Running / WaitingForConfirmation の検出が完全に失われていないこと。

### 2-6. focused 1面表示中に panel のボタンが効かない

- **現象**:
  - focused 1面表示中だけ、縮小、最小化、閉じる、ディスプレイ移動、最前面、最背面、非表示などの panel ボタンが反応しなくなる。
- **原因**:
  - panel がマウスクリックでアクティブ化された直後、`MainWindow_Activated` が同期的に `ReassertFocusedSlotIfNeeded` を呼んでいた。
  - `ReassertFocusedSlotIfNeeded` は `FocusMaximized(SetForegroundWindow)` で focused VS Code を前面化するため、Button の MouseUp / Click 前に VS Code が foreground を奪い、クリックがキャンセルされていた。
- **2026-04-28 実装反映**:
  - `Activated` / `StateChanged` からの focused 再適用を即時実行から遅延実行へ変更。
  - panel の `PreviewMouseDown` で pending reassert をキャンセルし、短時間 focused 再適用を抑止するよう変更。
  - 最小化、終了、最小化状態、busy、非表示、マウス押下中は `FocusMaximized` を呼ばないよう変更。
- **最低限の確認**:
  - focused 1面表示中に縮小、最小化、閉じる、ディスプレイ移動、最前面、最背面、非表示がそれぞれ反応すること。
  - Alt+Tab やタスクバーから panel へ戻したとき、クリック操作なしなら focused slot の最大化状態が維持されること。
  - 最背面ボタン後に遅延 reassert が走って focused VS Code を勝手に前面へ戻さないこと。

## 3. 対応優先順

1. 最小化ハング
2. focused 復帰の誤前面化
3. AI 状態検出の分離と精度改善
4. 標準表示カード枠の AI 状態表現調整

## 4. 追加実装メモ

- 2026-04-28: 標準表示と縮小表示の切替は左上基準ではなく右上基準へ変更した。compact bounds は再利用せず、毎回現在の panel 右上から再計算するよう変更した。
- 2026-04-28: 右上基準でも見た目のボタン位置がずれないよう、panel の move/resize は Win32 で一括反映するよう変更した。
- 2026-04-28: 2回目以降の drift と異常な compact サイズ変動を防ぐため、compact/standard の right/top 計算は WPF プロパティではなく HWND 実測 bounds を使い、compact 高さも target width ベースで measure するよう変更した。
- 2026-04-28: 縮小時の下側余白を減らすため、compact 高さは `RootLayoutGrid` 全体ではなく title row と `CompactBarPanel` のみから算出するよう変更した。
- 2026-04-28: AI 状態 UIA 走査が過剰で status refresh が数秒化していたため、1 refresh 最大 1 スロットのラウンドロビン、短時間キャッシュ、RawView 走査予算を導入した。
- 2026-04-28: focused 1面表示中に panel クリックが `Activated` 直後の `FocusMaximized` で奪われていたため、focused reassert を遅延化し、panel mouse input 中は短時間抑止するよう変更した。

## 5. 以後の更新ルール

- QA が増えたらこのファイルの `確認済み QA` へ追記する。
- 不具合を修正したら、該当セクションへ `対応済み / 未対応 / 要再検証` の状態を追記する。
- 実ログや smoke 結果で定数を見直した場合は、`implementation-patterns.md` 側の注意点も合わせて更新する。
