# Turtle AI Code Quartet Hub 実装パターン・注意点

更新日: 2026-05-13

## 1. AI状態監視を再導入しない

- AI状態検出、UI Automation のチャット走査、拡張ログ解析、VS Code外周オーバーレイは削除済み。
- `StatusStore.RefreshWindowStatusesAsync` は HWND/タイトル/ワークスペース更新の軽量処理として維持する。
- AI状態のためにパネルカード色、縮小ボタン色、点滅アニメーション、Jump List 文言を変えない。
- focused/selected の枠表示は AI 状態と無関係な操作フィードバックなので維持する。

## 2. スロット状態と保存

- `slots.json` は visible slot と stored panel を同じドキュメントで保持する。
- legacy 読み込み互換は壊さない。
- `WindowSlot` は管理対象ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- AI状態、最終AIイベント時刻、AI状態詳細は持たない。

## 3. 定期更新

- 更新対象は管理対象ウィンドウの存在、タイトル、ワークスペース表示のみ。ワークスペース読み取りは VS Code スロットに限定する。
- ワークスペース取得は `WorkspaceRefreshInterval` を使い、毎 tick で重い読み取りをしない。
- 操作中のレイアウト変更では `_suppressPeriodicRefreshUntil` で短時間抑止し、UI操作を優先する。

## 4. フォーカス/集中表示

- focused slot は `StatusStore.SetFocusedSlot` で UI 状態を保持する。
- 対象 VS Code は `FocusMaximized`、他スロットは `ArrangeExcept` と `SendOtherSlotsToBack` で制御する。
- パネルクリック直後に `SetForegroundWindow` が走るとクリックがキャンセルされるため、遅延 reassert と入力抑止を維持する。

## 5. UI 表現

- 標準カードと縮小ボタンは常に安定したサイズを持つ。
- 選択中スロットの外枠は残す。
- AI状態に由来する色変え、発光、点滅、状態ピルは追加しない。
- テキストがはみ出る場合は `TextTrimming` と固定寸法で吸収する。

## 6. ビルド

- 実行中の本体が既定の `bin\Debug` 出力をロックする場合がある。
- 反復確認では `scripts/Build-Panel.ps1` または `--artifacts-path` を使う。

## 7. 複数アプリ起動

- 各スロットの既定アプリは VS Code (`vscode`) とし、一括起動はスロットごとの `ApplicationId` に従う。
- VS Code は `VscodeLauncher` に残し、専用 user-data-dir、リモート URI、workspaceStorage 読み取りの既存挙動を壊さない。
- Antigravity など VS Code 以外の workspace IDE は `ApplicationLauncher` の汎用起動で扱う。
- Codex / Claude は `SingleWindowAgent` として、既存ウィンドウ探索やウィンドウ検出待ちをせず起動コマンドを送信する。閉じる/切り替えるトグル動作にしない。
- Claude / Codex の Windows Store 版は通常の PATH やスタートメニュー `.lnk` で検出できない場合があるため、AppModel Repository から AppUserModelID を解決し、`shell:AppsFolder` 経由で起動する。起動済みプロセスと `PackageRootFolder` も補助的に見る。
- `WindowEnumerator` はスロットの `ApplicationId` に対応する process names で確認する。VS Code 以外に `VscodeWorkspaceState` を適用しない。
- 未検出アプリはボタンを無効化し、`ToolTip` とメッセージで設定パス/コマンド確認へ誘導する。
- UI は VS Code / Antigravity を各スロット内の等幅ボタン、Codex / Claude を控え Quartet 行の右端補助ボタンとして配置する。Launch 直上にグローバル IDE 選択ボタンは置かない。AI状態の色変えや点滅は追加しない。
- 各スロットにも VS Code / Antigravity 切替を置く。未起動スロットでは選択状態だけ保存し、起動中スロットで別アプリを選んだ場合は現在のウィンドウを閉じて同じスロット内容を選択アプリで開き直す。

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
- 全ディスプレイ移動を押したら全フォーカスを解除して全員 4 面に戻す（`ClearFocusedSlot` で全解除 → 4 面 Arrange）。リベース（override==新ベースの解除）も従来どおり行う。
- 単独ディスプレイ移動とフォーカスの干渉は「移動先優先」: フォーカス中パネルを、既にフォーカスを持つディスプレイへ移すと、移動先のフォーカスを優先し、移動したパネルはフォーカス解除（移動先で非フォーカス＝最大化の背面）になる。移動元のフォーカスはそのパネルが去るので自然に解ける。移動先にフォーカスが無ければフォーカスを引き継いで移動先で最大化する。
- ディスプレイ色: ベース（作業ベース）は常に緑（`AccentBrush`）、非ベースは番号の小さい順に 青 `DisplayAccent2Brush` → 紫 `DisplayAccent3Brush` → 金 `DisplayAccent4Brush`（`GetDisplayBrushForMonitor`）。全ディスプレイ移動でベースが動いても「緑＝ベース」を維持する（番号固定ではない）。
- 色は `WindowSlot.DisplayBrush`（実効ディスプレイの色、常時更新）に集約し、バッジ文字色・枠線色、フォーカス枠 `FocusFrameBrush`（フォーカス中だけ DisplayBrush、それ以外は透明）に使う。`FocusFrameBrush` はカードの外枠 `SlotCardFocusFrame` の `BorderBrush` に直接バインドする（`Setter.Value` はバインド不可のためトリガではなく直接バインド）。縮小/極小ビューのフォーカス枠はバッジ非対応のため緑のまま。
