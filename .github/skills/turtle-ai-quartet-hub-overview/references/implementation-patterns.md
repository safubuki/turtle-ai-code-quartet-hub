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
- `WindowSlot` は VS Code ウィンドウ、タイトル、ワークスペース、フォーカス、表示/非表示、レイヤー状態だけを持つ。
- AI状態、最終AIイベント時刻、AI状態詳細は持たない。

## 3. 定期更新

- 更新対象は VS Code ウィンドウの存在、タイトル、ワークスペース表示のみ。
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
