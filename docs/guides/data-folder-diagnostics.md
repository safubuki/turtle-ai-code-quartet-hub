# 容量の解析 診断コード一覧

[ドキュメント一覧へ](../index.md)

システムモニタの「アプリのデータ（ハブ・IDE）」の各行にある解析ボタン（虫眼鏡）で、フォルダの容量の内訳と増えた原因を判定する。
結果は診断コード（H1・V3 など）と大きさで示す。利用者がコードと大きさを伝えれば、この一覧から原因と改善策を特定できる。

- 実装: `src/TurtleAIQuartetHub.Panel/Services/DataFolderDiagnosticsService.cs`（判定）、`MainWindow.DataFolderAnalysis.cs`（画面）
- 回帰テスト: `src/TurtleAIQuartetHub.Panel.Tests/DataFolderDiagnosticsTests.cs`
- 解析は読み取りだけで、ファイルを削除・変更しない。結果のキーワードは診断ログにも 1 行残る（`Data folder analysis (...)`）。

## 伝え方の例

画面の「伝えるときのキーワード」をそのまま伝える。

```text
Hub: H1（4.2 GB）／合計 4.3 GB
VS Code: V3（3.8 GB）・V10（1.9 GB）／合計 6.0 GB
```

問題がなければ `問題なし／合計 1.7 MB` と表示される。
コードのほかに「容量の多い場所（上位）」の 1〜2 行目（例: `user-data\A\User\workspaceStorage 2.1 GB`）も伝えてもらうと、原因を絞りやすい。

## 段階

| 段階 | 色 | 意味 |
|---|---|---|
| 注意 | 黄 | しきい値（注意）以上。すぐ困るほどではないが増え続けている |
| 要対処 | 赤 | しきい値（要対処）以上。ディスクを大きく圧迫している |

## Hub データ（H）

対象: `%LOCALAPPDATA%\TurtleAIQuartetHub`（設定の `stateDirectory`）。通常は数 MB で、数 GB になるのは `user-data`（スロット別の VS Code データ）がある場合だけ。

| コード | 現象 | 判定（注意 / 要対処） | 想定原因 | 利用者の対処 | 開発側の改善案 |
|---|---|---|---|---|---|
| H1 | 使われていない `user-data` が残っている | `useDedicatedUserDataDirs=false` で `user-data` が 50 MB / 1 GB 以上 | 以前スロット別プロファイルを使っていたときのデータ。起動時の自動削除（`SlotUserDataPaths.ReclaimUnusedUserData`）が、VS Code の窓によるロックで消し残った | VS Code をすべて閉じてからハブを再起動 | スロットの窓が閉じたタイミングで回収を再試行する。ディスク整理の候補に「安全」で追加する。消し残ったファイル名をログに出す |
| H2 | スロット別のキャッシュが大きい | 専用プロファイル有効で、各スロットの再生成キャッシュ合計が 500 MB / 2 GB 以上 | VS Code のプロセスが一つでも残っている間は回収を見送る | VS Code をすべて終了してハブを再起動。不要なら `useDedicatedUserDataDirs=false` | 終了後の回収再試行やディスク整理の候補への追加 |
| H3 | スロット別のワークスペースデータが大きい | 各スロットの `User\workspaceStorage` 合計が 1 GB / 3 GB 以上 | Copilot のチャット履歴などがスロットごとに溜まる。同じフォルダを別スロットで開くと重複する | 消えたフォルダの分を整理。大きいものはチャット履歴なので確認してから削除 | ワークスペース別の内訳表示。消えたフォルダの分をディスク整理の候補（注意）に追加する |
| H4 | スロット別の拡張機能データが大きい | 各スロットの `User\globalStorage` 合計が 1 GB / 3 GB 以上（最大の拡張機能名を表示） | 拡張機能のインデックスやキャッシュがスロットごとに作られる | 該当拡張機能のキャッシュ削除・アンインストール | 拡張機能別の内訳表示。よく大きくなる拡張機能の個別案内を追加する |
| H5 | スロット別のブラウザ保存領域が大きい | 各スロットの WebStorage 等の合計が 1 GB / 3 GB 以上（CacheStorage の内訳付き） | Web ビューのキャッシュと保存データ。VS Code のプロセス残存中は回収されない | VS Code をすべて終了してハブを再起動すると CacheStorage を自動回収。WebStorage 全体は削除しない | CacheStorage 限定の回収・内訳表示を実装済み（2026-11-07） |
| H6 | 今のスロット名に対応しないデータがある | `user-data` 内の A〜D 以外のフォルダが 50 MB / 1 GB 以上 | 以前のスロット名で作られたデータ。ハブは使わず自動削除もしない | VS Code を閉じて該当フォルダを削除 | 起動時の回収対象に加える。ディスク整理の候補に「安全」で追加する |
| H7 | ハブのログが大きい | `panel*.log` の合計が 10 MB 以上（注意のみ） | 切り詰めは起動時だけ（2 MB 超で 1 MB に）。長期間再起動していないか、切り詰めに失敗 | ハブを再起動。改善しなければ `panel.log` を削除 | 実行中にも定期的に切り詰める。切り詰め失敗をログに残す |
| H8 | ハブが作らないデータがある | 既知以外（config・user-data・slots.json・update-badges.json・*.log 以外）が 200 MB / 2 GB 以上（名前を表示） | `stateDirectory` が他の用途のフォルダを指しているか、別のツールが同じ場所を使っている | 中身を確認し、ハブには専用の空フォルダを指定 | 起動時に `stateDirectory` の中身を検証して警告する |

## VS Code（V）・Antigravity IDE（G）のユーザーデータ

対象: VS Code は `%APPDATA%\Code`、Antigravity IDE は `%APPDATA%\Antigravity IDE`。番号の意味は V と G で共通。
ハブが起動時にキャッシュを回収するのは VS Code だけ（VS Code が 1 つも起動していないときのみ）で、Antigravity IDE は回収していない。原因の説明はこの違いで変わる。

| コード | 現象 | 判定（注意 / 要対処） | 想定原因 | 利用者の対処 | 開発側の改善案 |
|---|---|---|---|---|---|
| V1 / G1 | 再生成できるキャッシュが大きい | `Cache`・`Code Cache`・`GPUCache`・`CachedData` 等の合計が 1 GB / 3 GB 以上 | V: 回収は VS Code 停止中の起動時だけ。G: ハブは回収していない | V: VS Code を閉じてハブを再起動。G: Antigravity を閉じて手動削除 | VS Code の停止を検知したタイミングで回収する。G1 は Antigravity も回収対象に追加する。ディスク整理の候補に追加する |
| V2 / G2 | 拡張機能の古いインストーラーが大きい | `CachedExtensionVSIXs` が 300 MB / 1 GB 以上 | 拡張機能を更新するたびに古い .vsix が残る | V1 / G1 と同じ | V1 / G1 と同じ |
| V3 / G3 | ワークスペースごとのデータが大きい | `User\workspaceStorage` が 2 GB / 5 GB 以上（件数・最大のフォルダ名・消えたフォルダの分を表示） | フォルダを開くたびにチャット履歴や拡張機能のデータが作られ、閉じても残る | 使わないフォルダの分を削除（チャット履歴に注意） | ワークスペース別の一覧（最終更新日付き）を表示し、古いものをディスク整理の候補にする |
| V4 / G4 | 消えたフォルダのワークスペースデータが残っている | `workspace.json` の参照先が存在しない分の合計が 300 MB / 1 GB 以上 | フォルダを削除・移動しても IDE は自動で消さない | IDE を閉じて該当フォルダを削除 | ディスク整理の候補（注意・チャット履歴を含む）に追加する |
| V5 / G5 | 拡張機能のデータが大きい | `User\globalStorage` が 1 GB / 3 GB 以上（最大の拡張機能名を表示） | Copilot のインデックスや言語サーバーのキャッシュなど | 該当拡張機能のキャッシュ削除・アンインストール | 拡張機能別の内訳表示。よく大きくなる拡張機能の個別案内を追加する |
| V6 / G6 | ログが大きい | `logs` が 300 MB / 1 GB 以上 | 起動のたびにログが残る。詳細ログを出す拡張機能があると急増する | IDE を閉じて `logs` を削除 | 古いセッションのログだけ回収する |
| V7 / G7 | クラッシュダンプが大きい | `Crashpad` が 200 MB / 1 GB 以上 | IDE や拡張機能の異常終了が繰り返されている可能性 | 削除して問題なし。頻発なら原因調査 | VS Code は起動時に回収済みのため、増える頻度（落ちる原因）を調べる。G7 は Antigravity も回収対象に追加する |
| V8 / G8 | 未保存ファイルの退避が大きい | `Backups` が 300 MB / 1 GB 以上 | 大きいファイルを未保存のまま開いている | 未保存のファイルを保存するか閉じる | 案内のみ |
| V9 / G9 | ローカル履歴が大きい | `User\History` が 500 MB / 2 GB 以上 | 保存のたびに履歴が残る。大きいファイルの頻繁な保存で増える | `workbench.localHistory.maxFileSize` / `maxFileEntries` を下げる | 設定の推奨値を案内・適用する機能 |
| V10 / G10 | ブラウザ保存領域が大きい | `WebStorage`・`Service Worker`・`IndexedDB` 等の合計が 1 GB / 3 GB 以上（CacheStorage の内訳付き） | Web ビューのキャッシュと保存データ | V: VS Code をすべて終了してハブを再起動すると CacheStorage を自動回収。G: 自動回収対象外。いずれも保存領域全体は削除しない | V10 は CacheStorage 限定の回収、V10/G10 は内訳表示を実装済み（2026-11-07） |
| V11 / G11 | 分類できない大きなデータがある | 上記以外の直下フォルダ、または `User` 直下のフォルダで 1 GB 以上のもの（場所を表示） | 未知 | フォルダ名を伝える | 伝えられた名前を調べ、この一覧と判定に分類を追加する |

## H5 / V10 の CacheStorage 回収（2026-11-07）

別 PC で H5 が約 5.1 GB、H3 が約 2.1 GB となり、CacheStorage の清掃で H5 が解消したという報告への対応。
別 PC のファイル実体や拡張機能は未確認のため、増加させた拡張機能までは特定していない。
確認できた実装上の原因は、従来の回収がプロファイル直下のキャッシュだけを列挙し、ブラウザ保存領域内の CacheStorage を対象外としていたこと。

- ハブ起動時の既存の背景処理で、標準プロファイルと専用スロットプロファイルを回収する。
- 対象は `WebStorage\<bucket>\CacheStorage` と `Service Worker\CacheStorage`、および `Partitions\<partition>` 配下の同じ配置だけ。
- 窓の有無ではなく VS Code / Insiders / VSCodium / Codium のプロセスの有無で判定する。ひとつでも残っている場合、または確認に失敗した場合は見送る。背景プロセスを強制終了しない。
- 清掃とハブからの VS Code 起動は排他制御する。外部からの起動も清掃中に約 200 ms 間隔で再確認し、検出後は残りを削除しない（外部プロセスの起動そのものとの原子的な排他はできない）。
- プロファイル・保存領域・bucket などの祖先がジャンクション等の場合は対象外。CacheStorage 内部のリンク先も辿らない。ロック中のファイルは残し、次回の回収へ回す。
- `User`（設定・workspaceStorage・globalStorage・履歴）、Cookies、IndexedDB、Local/Session Storage、未保存の Backups、Service Worker の登録情報、QuotaManager はこのキャッシュ回収では削除しない。専用プロファイルを無効化した場合の旧 `user-data` 全体の回収（H1）は既存仕様として別に維持し、全 VS Code 停止中に限定した。
- 回収したパスとバイト数、プロセス残存による見送りは `panel.log` に残す。次の Web ビュー利用時にはキャッシュの再生成が発生する。
- H5 / V10 のしきい値とコードは維持する。CacheStorage を削除しても他の保存データがしきい値以上なら警告は残る。H3 の履歴はこの対応で減らない。

配置の根拠は [Chromium の CacheStorage 実装](https://chromium.googlesource.com/chromium/src/+/d55e865d17f642ebfd55f629211b78e5cc400aae/content/browser/cache_storage/cache_storage_manager.cc)、Web ビュー資源のキャッシュ用途は [VS Code の Service Worker](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/webview/browser/pre/service-worker.js) を参照。

検証: `CacheStorageReclaimTests` で、模擬プロファイルの回収・保存データの保持・プロセス残存/確認失敗・リンク保護・ファイルロック・途中停止・起動との排他を確認。`DataFolderDiagnosticsTests` で H5/V10/G10 の案内と内訳、H3 の独立性、回収後の残存警告を確認する。
別 PC では更新版をビルドし、VS Code をすべて終了してハブを再起動した後に容量を再解析する。ログの回収量と H5 の変化、設定・サインイン・チャット履歴の保持を確認する。

## 判定を追加・変更するとき

1. `DataFolderDiagnosticsService` の `EvaluateHub` / `EvaluateIde` に判定を追加する（コード番号は既存を変えず、末尾に足す）。
2. `DataFolderDiagnosticsTests` に、その現象を合成データで再現するテストを足す。
3. この一覧に行を追加する。
