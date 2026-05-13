# 現在の既知課題と QA 方針

更新日: 2026-05-13

## 重点 QA
- 既定状態で 4 つの VS Code を起動し、2x2 に配置できること。
- 各スロットで VS Code / Antigravity / Codex CLI / GitHub Copilot CLI / Gemini CLI / Claude CLI を選択できること。
- スロット内アプリ選択が `IDE` 枠と `CLI` 枠に分かれ、IDE は縦並び、CLI は4つを同じ枠内に表示していること。
- 選択中アプリのボタンがベタ塗りではなく暗めの緑で表示され、未検出アプリはグレーアウトすること。
- GitHub Copilot CLI は対象ワークスペースで `copilot` だけを実行し、ワークスペースパスを引数として渡さないこと。
- VS Code から CLI、CLI から VS Code、CLI から別 CLI へ、現在のスロットウィンドウを閉じてから押したアプリへ切り替えられること。
- Codex / Claude の Windows アプリ版ボタンが、`Windows` ラベル付きで控え Quartet と同じ行の右端に表示されること。
- Antigravity は A=左上、B=右上、C=左下、D=右下へ配置され、起動後に中央へ戻っても短い遅延再配置で対象象限へ戻ること。
- 未検出アプリはグレーアウトし、理由がツールチップまたはメッセージで分かること。
- VS Code の workspaceStorage 読み取りに失敗しても、保存済みワークスペースパスが消えないこと。
- 正しく開けたワークスペースは、タイトルと保存済みパスに反映されること。
- 各スロット右上のゴミ箱アイコンで visible slot の保存情報を削除でき、起動中ウィンドウは閉じずに管理対象から外れること。
- 標準表示の下部にある `Launch Quartet` ボタンが見切れないこと。
- 集中表示中、対象ウィンドウの上に他アプリが重なっている場合は、同じスロットボタンで 4 面表示へ戻さず前面復帰すること。
- 重なりがない場合は、同じスロットボタンで従来通り 4 面表示へ戻ること。
- 控え Quartet への退避、復帰、入れ替えができること。
- タスクバー Jump List のスロット切替と表示モード切替が動くこと。
- `scripts/Build-Panel.ps1` と `scripts/Test-StoreReadiness.ps1` が通ること。

## 残リスク
- VS Code のウィンドウタイトルや workspaceStorage の変更により、ワークスペース表示がずれる可能性がある。
- terminal ホストや CLI 実体が環境で異なる場合、`applications[].detection.processNames` の追加設定が必要になる可能性がある。
- Antigravity の起動ラッパーや初期化タイミングが変わると、遅延再配置の回数や待ち時間の調整が必要になる可能性がある。
- 実行中 EXE ロックにより通常の `dotnet build` が失敗する場合があるため、反復確認では `scripts/Build-Panel.ps1` を使う。
