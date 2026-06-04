# 現在の既知課題と QA 方針

更新日: 2026-06-04

## 重点 QA
- 既定状態で 4 つの VS Code を起動し、2x2 に配置できること。
- 低速または標準スペック端末で VS Code の起動が遅れ、ウィンドウが中央付近に出た場合でも、専用 `user-data-dir` の既存 VS Code ウィンドウとして再接続され、C=左下など対象スロットへ戻ること。
- 標準表示の丸いステータス LED は、停止中=赤、起動中=黄色、起動済み=緑で表示されること。
- 各スロットで VS Code / Antigravity / Codex CLI / Claude CLI / GitHub Copilot CLI / Grok Build CLI / Gemini CLI を選択できること。
- スロット内アプリ選択が `IDE` 枠と `CLI` 枠に分かれ、IDE は縦並び、CLI は4つを同じ枠内に表示していること。
- `IDE` と `CLI` のボタン高さ、上下左右の隙間、上端位置がそろっていること。
- 選択中アプリのボタンがベタ塗りではなく暗めの緑で表示され、未検出アプリは選択中でもグレーアウトすること。
- GitHub Copilot CLI は対象ワークスペースで `copilot` だけを実行し、ワークスペースパスを引数として渡さないこと。
- GitHub Copilot Chat 拡張の `globalStorage\github.copilot-chat\copilotCli\copilot*` だけが存在する環境では、GitHub Copilot CLI を未検出として扱うこと。
- VS Code から CLI、CLI から VS Code、CLI から別 CLI へ、現在のスロットウィンドウを閉じてから押したアプリへ切り替えられること。
- 未起動スロットで IDE / CLI ボタンを押しても自動起動せず、起動対象の選択だけが変わること。
- 一括起動で Codex / Gemini など複数 CLI 種別が混在しても、それぞれの terminal が対象スロットの象限へ配置されること。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版ボタンが、`Windows` ラベル付きで控え Quartet と同じ行の右端に表示され、Antigravity2 が Claude の右側にあること。
- Antigravity は A=左上、B=右上、C=左下、D=右下へ配置され、起動後に中央へ戻っても短い遅延再配置で対象象限へ戻ること。
- Antigravity IDE は `%LOCALAPPDATA%\Programs\Antigravity IDE\Antigravity IDE.exe` 相当から、Antigravity2 は `%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe` 相当から、Windows 10 / Windows 11 のユーザープロファイル差に依存せず検出できること。
- 未検出アプリはグレーアウトし、理由がツールチップまたはメッセージで分かること。
- VS Code / Antigravity の workspaceStorage 読み取りに失敗しても、保存済みワークスペースパスが消えないこと。
- Antigravity でウィンドウ起動後にアプリ内から対象フォルダを開いた場合も、`%APPDATA%/Antigravity/User/workspaceStorage` とウィンドウタイトルから最新ワークスペースパスを保存できること。
- 正しく開けたワークスペースは、タイトルと保存済みパスに反映されること。
- 各スロット右上のゴミ箱アイコンで visible slot の保存情報を削除でき、起動中の IDE / CLI ウィンドウも閉じること。
- 各スロット右上のゴミ箱アイコンは、削除確認ダイアログで `削除する` を押すまで削除しないこと。
- 通常表示の各スロット左下にフォルダアイコンボタンが表示され、ローカルワークスペースのフォルダまたは `.code-workspace` の親フォルダを Explorer で開けること。
- `vscode-remote://ssh-remote+...` や `ssh://...` など SSH / remote ワークスペースでは、フォルダアイコンボタンがグレーアウトし、Explorer 起動を行わないこと。
- 実行中スロットのアクションボタンが `閉じる` と表示されること。
- タイトルバーの `?` ヘルプに CLI インストールコマンドと承認確認を減らす起動オプション例が表示されること。Claude Code は公式インストーラの PowerShell / CMD コマンドと npm コマンドを表示し、Grok Build CLI は Git Bash/WSL と PowerShell + Git Bash のインストールコマンド、および `grok --always-approve` を表示すること。
- `?` ヘルプの各セクションに枠があり、説明文とコマンドを選択コピーできること。
- タイトルバーの `?` 左に歯車設定があり、VS Code / Antigravity / Codex / Claude / Copilot / Grok / Gemini / Codex Windows / ChatGPT Windows / Claude Windows / Antigravity2 Windows の起動コマンドを確認・編集・保存・再検出できること。
- 歯車設定で、表の Quartet と控え Quartet の保存済みタイトル、パス、アプリ ID を一覧確認・編集・空化できること。
- 歯車設定の不整合修復で、不完全な控えと重複控えを削除し、同じワークスペースを再登録できる状態に戻せること。
- Claude / Grok などの CLI が PATH に出ていない環境でも、npm / pnpm / Volta の一般的な shim 置き場、Claude Code インストーラが使う `~\.local\bin`、Grok Build インストーラが使う `~\.grok\bin` から検出できること。
- Claude / Grok など PATH がアプリ本体へ反映されていない CLI でも、起動した terminal 内で初回コマンドと手入力コマンドの両方が認識されること。
- 標準表示の下部にある `Launch Quartet` ボタンが見切れないこと。
- スロットカード下から控え Quartet までの黒い余白が不要に広がらないこと。
- `Launch Quartet` ボタン下に不要な空白が残らないこと。
- 縮小表示に切り替えた直後から C/D パネル行と `Windows` 補助アプリボタンが見えること。
- 縮小表示でも極小表示と同じように、4 つのパネルボタン中央へ丸い `非` / `表` ボタンが表示され、管理中ウィンドウの非表示/表示を切り替えられること。
- 縮小表示の Windows 補助アプリ行で、`Windows` ラベルが左端で見切れず、ChatGPT / Codex / Claude / Antigravity2 の 4 ボタンが同じ幅で収まること。
- 一括起動では A-D の対象スロットが 1 つずつ順番に開き、すべての起動・再接続確認後に 2x2 配置されること。
- VS Code の起動が遅れた場合でも、前スロットの遅延ウィンドウを次スロットとして誤捕捉せず、専用 `user-data-dir` 有効時は `code.lock` PID に基づいて対象スロットへ割り当てられること。
- 一括起動後の即時補正・遅延補正は、位置ずれがある場合だけ静かに実行され、topmost/notopmost の連続切替やパネル前面化の繰り返しで見た目がちらつかないこと。
- 一括起動後 20-30 秒程度経って VS Code が保存済み位置へ戻そうとしても、対象スロットの期待位置から大きく外れた場合だけ再配置されること。既に正しい位置の高速端末では余計な移動が起きないこと。
- ディスプレイ切替ボタンで別ディスプレイへ移動したとき、移動先の DPI や解像度・アスペクト比が異なっても、移動先の作業領域に合わせて各ウィンドウが正しいセルサイズへ再補正され、2x2 がくずれないこと。移動直後の小さく出てから直る一瞬の補正ラグが、目立たない範囲に収まること。
- 2x2 配置の見た目の隙間（上端/下端/左右の外周マージン、中央の縦・横）が均等であること。Windows の不可視リサイズ枠（DWM 拡張フレーム）を打ち消して可視枠をセルにそろえるため、上だけ詰まる/中央や下が空くといった偏りが出ないこと。既定 `gap`=6 で密着しすぎず詰まって表示されること。
- 同じスロットボタンは、CLI / Antigravity / VS Code のいずれでも 1 面フォーカス表示と 4 面表示を押すたびに切り替えること。
- 4 面表示または 1 面表示中にパネルカードやアプリケーションウィンドウをドラッグしても、フォーカス再適用や再配置が割り込んでウィンドウが一瞬乱れないこと。
- D などのスロットを削除して空にした後、実行中の B パネルを空スロットへドラッグ移動し、空いた B で新規 VS Code を起動しても、B と D が同じ VS Code user-data / code.lock を掴まず、フォーカスや操作対象が入れ替わらないこと。
- 控え Quartet への退避、復帰、入れ替えができること。
- 控え Kame と D の Mura を入れ替え、続けて控えに戻った Mura を同じ D へ入れ替えても、パネル表示と実際に開くワークスペースが一致すること。旧 D の VS Code が閉じ切る前でも、Mura のウィンドウを Kame として再接続しないこと。
- タスクバー Jump List のスロット切替と表示モード切替が動くこと。
- `scripts/Build-Panel.ps1` と `scripts/Test-StoreReadiness.ps1` が通ること。
- XAML の共通 DataTemplate / Style を追加・移動した後は、`dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` またはビルド済み EXE の短時間起動で、`StaticResource` 解決失敗による起動直後終了がないこと。

## 残リスク
- VS Code / Antigravity のウィンドウタイトルや workspaceStorage の変更により、ワークスペース表示がずれる可能性がある。
- VS Code の `code.lock` の PID と実ウィンドウ PID が将来の Electron 実装変更で一致しなくなると、既存ウィンドウ再接続の追加調整が必要になる可能性がある。
- terminal ホストや CLI 実体が環境で異なる場合、`applications[].detection.processNames` の追加設定が必要になる可能性がある。
- Antigravity の起動ラッパーや初期化タイミングが変わると、遅延再配置の回数や待ち時間の調整が必要になる可能性がある。
- 実行中 EXE ロックにより通常の `dotnet build` が失敗する場合があるため、反復確認では `scripts/Build-Panel.ps1` を使う。
