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
