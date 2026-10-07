# Antigravity CLI 無効表示の調査

調査日: 2026-10-03 / 訂正・修正確認: 2026-10-04

## 結論

Antigravity CLI は `C:\Users\kamep\.gemini\bin\agy.exe` に導入済みだった。ハブがこの配置先を探索せず、PATH にも登録されていなかったため、CLI ボタンが無効になっていた。別ツール `ai-usage-checker` はこの配置先も探索しており、検出範囲の差があった。

初回の「未導入」という結論は、`~\.gemini\bin` の探索漏れによる誤判定だった。2026-10-04 に既存ファイルを確認して訂正した。追加インストールは不要。

| 対象 | 確認結果 | 状態 |
| --- | --- | --- |
| Antigravity IDE | `%LOCALAPPDATA%\Programs\Antigravity IDE\Antigravity IDE.exe` が存在し、起動中 | 導入済み |
| Antigravity CLI | `~\.gemini\bin\agy.exe` が存在。自動更新を無効にして `--version` を実行し `1.2.16` を確認 | 導入済み |
| CLI 設定 | 初回は `Command=agy`。起動中のハブには既存ファイルのフルパスを保存 | 認識済み |
| 自動検出 | 検出・起動 PATH の両方に `~\.gemini\bin` を追加 | 修正済み |

## 根拠

- sandbox の実行ユーザーは `CodexSandboxOffline` で、その .NET UserProfile は実ユーザーと異なる。`~\.gemini` の検出結果は実ユーザー `kamep` で確認した。
- `%LOCALAPPDATA%`、`~\.local`、`~\.antigravity`、`%APPDATA%\npm` だけでは CLI 本体を発見できなかった。別ツールの探索候補との照合で `~\.gemini\bin` の漏れが判明した。
- 現在の PATH 上には `antigravity-ide.cmd` がある。このコマンドは IDE の起動用であり、ターミナルで動く Antigravity CLI の `agy` とは別の実体。
- 起動中のハブは `dist\turtle-ai-quartet-hub\TurtleAIQuartetHub.exe`。ユーザー設定の CLI コマンドに欠落したフルパスの指定はなかった。
- ハブの設定読み込み・検出ソースを使う独立診断で、修正前は CLI=`NotFound`、修正後は CLI=`Installed` / `ResolvedCommand=C:\Users\kamep\.gemini\bin\agy.EXE` になった。後者はフルパス設定を保存する前に、`Command=agy` と PATH 未登録の状態で確認した。診断は設定やログを書き込まず、終了コード0、ビルド警告・エラー0。

## コードの確認

- `Models/AppConfig.cs`: CLI の既定コマンドと検出候補は `agy`。
- `Services/ApplicationDetectionService.cs`: PATH と公式標準配置先に加えて、既存環境の `~\.gemini\bin` も探索するよう修正した。
- `Services/ApplicationLauncher.cs`: 検出済みフルパスの優先は維持し、起動ターミナルの PATH 候補にも `~\.gemini\bin` を追加した。
- `Models/LauncherApplication.cs`: `Installed` 以外は `IsAvailable=false` となり、選択ボタンを無効化する。
- `Services/StatusStore.cs` と `MainWindow.xaml.cs`: 初回起動・設定保存・設定画面の「再検出」で可用性を更新する。導入後は「再検出」またはハブの再起動が必要。

`antigravity` や `antigravity-ide` を CLI の検出候補へ追加すると、GUI 用コマンドを CLI と誤認するため、この対応は行わない。

## 対応と確認結果

- [x] 検出・起動 PATH に `~\.gemini\bin` を追加し、README の探索先説明を更新
- [x] 実ユーザーで PATH 未登録・`Command=agy` のまま、自動検出が `Installed` になることを確認
- [x] 本体ビルド成功、警告・エラー0
- [x] 既存回帰テスト55件合格
- [x] 起動中の旧バイナリにも設定画面から既存 CLI のフルパスを保存し、4スロットの CLI ボタンが有効になることを確認
- [x] overview に原因・修正・検証結果を記録

現環境のフルパス設定はハブの再起動後も保持される。ソース修正は隔離出力へビルドして確認した。実行中の配布 EXE は置換していないため、自動検出の変更は次回ビルド・配布時に反映される。

CLI 本体の追加インストールと OS の PATH 変更は行っていない。前日のインストーラー実行は自動承認審査で拒否されており、配置先判明後は不要になった。画面操作中にユーザーの物理 Esc が通知されたため、それ以降の Computer Use 入力は停止した。通知前に設定保存と4スロットの有効化を確認済み。

実際のプロジェクトで CLI を対話起動し、自動作業を開始する確認は行っていない。

## 別ツールで利用量を取得できる場合（2026-10-04 追記）

ユーザーから別ツールで取得できる理由の質問があった。今回の新規画像は確認できていないため、保存済みスロットにある `C:\git_home\ai-usage-checker` を候補として、コードだけを読み取り確認した。

- `Services/UsageFetcherService.cs:257` の `FetchGeminiQuotaAsync` は、まず CLI の `agy` に問い合わせ、有効な利用量を取得できなければ IDE の言語サーバーへフォールバックする。
- `Services/AgyQuotaClient.cs:74` は、CLI 本体が見つからなければ結果なしを返す。
- `Services/LanguageServerQuotaClient.cs:52` 以降は稼働中の言語サーバーを探し、ローカル接続の `RetrieveUserQuotaSummary` API を使用する。したがって、IDE が稼働していれば CLI がなくても利用量を取得できる経路がある。
- この環境では別ツールが探索する `~\.gemini\bin\agy.exe` が存在するため、CLI 経由で取得できる条件も満たしている。ただし画像と実行ログを確認していないため、画像の取得経路が CLI か IDE かは確定していない。

画像の別ツールがこの候補と一致するかは未確認だが、探索先の差がハブの無効表示を引き起こすことは実環境で再現・修正確認した。別ツールの外部 API の呼び出し、認証情報の閲覧、別リポジトリの変更は行っていない。
