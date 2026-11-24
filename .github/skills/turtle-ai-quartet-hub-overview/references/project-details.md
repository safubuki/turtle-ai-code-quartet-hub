# Turtle AI Code Quartet Hub プロジェクト詳細

更新日: 2026-09-17

## 概要
- 4 つの開発用ウィンドウを A-D スロットとして管理する Windows 向け WPF アプリ。
- 2x2 配置、集中表示、非表示/再表示、控え Quartet、タスクバー Jump List 操作を提供する。
- 既定のスロット起動対象は VS Code。
- VS Code / Antigravity はワークスペース IDE として、Codex / Claude / GitHub Copilot / Grok Build / Antigravity CLI はワークスペース CLI として各スロットで起動できる。
- Codex / ChatGPT / Claude / Antigravity2 の Windows アプリ版は CLI とは別に補助ボタン行から起動できる。
- 通常表示の各スロットにはフォルダボタンがあり、保存済みまたは検出済みのローカルワークスペースを Explorer で開ける。SSH / remote URI のワークスペースではボタンを無効化する。
- 画面・ログからの AI 状態推定と VS Code 外枠フレームは削除済み。AI の作業中・完了は、Claude Code / Codex / GitHub Copilot / Grok / Antigravity のフックからの明示的な通知だけでスロットに表示する（歯車設定の「AI の作業通知」でフックを設定）。作業中はカードの枠線（角丸含む）の中心線上を緑の LED の一筋の光がふわっとゆっくり一周し、最後の方で少しだけ加速して滑らかに繰り返す（許可の確認中も同じ）。完了したときだけカード（枠・地・光背）と A〜D アイコン（極小表示はカード自体）が少し早めの 3.0 秒ホタル点滅で青く光る（ボタンと IDE / CLI の枠内は塗らない）。色はツールごとに変えない。
- `?` ヘルプと README は、Codex / Copilot / Claude / Grok の Windows ネイティブインストーラーを優先して表示し、npm または Bash 系の方法を代替手段として併記する。

## 技術スタック
- アプリ本体: C#, .NET 10, WPF
- パッケージ: MSIX / Windows Application Packaging Project
- 補助スクリプト: PowerShell
- 主な API: Win32 P/Invoke, System.Text.Json

## 開発環境
- Windows 10 / Windows 11 では .NET 10 SDK が入っていれば、追加 workload なしで `dotnet build` と `dotnet run` を実行できる。
- 未導入端末では PowerShell から `winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements` で導入できる。
- SDK 導入後は新しい PowerShell または VS Code ターミナルを開き、`dotnet --list-sdks` と `dotnet --info` で確認する。

## 主なファイル
- `TurtleAIQuartetHub.sln`: WPF 本体ソリューション。
- `src/TurtleAIQuartetHub.Panel/MainWindow.xaml`: 標準表示/縮小表示 UI。
- `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs`: UI イベント、起動、配置、集中表示、前面/背面、控え保存。
- `src/TurtleAIQuartetHub.Panel/Models/AppConfig.cs`: 設定読み込み、既定アプリ定義、正規化。
- `src/TurtleAIQuartetHub.Panel/Models/ToolApplicationConfig.cs`: 起動対象アプリの設定モデル。
- `src/TurtleAIQuartetHub.Panel/Models/LauncherApplication.cs`: 検出済みアプリのランタイム状態。
- `src/TurtleAIQuartetHub.Panel/Models/ApplicationPathSetting.cs`: 設定画面で編集するアプリ起動コマンドの表示モデル。
- `src/TurtleAIQuartetHub.Panel/Models/WindowSlot.cs`: A-D スロット状態モデル。
- `src/TurtleAIQuartetHub.Panel/Models/SlotApplicationOption.cs`: スロット内の IDE / CLI 選択ボタン用モデル。
- `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs`: スロット状態、保存復元、控え保存管理。
- `src/TurtleAIQuartetHub.Panel/Services/ApplicationDetectionService.cs`: PATH、App Paths、スタートメニュー、WindowsApps、一般的なインストール先からアプリを検出。
- `src/TurtleAIQuartetHub.Panel/Services/ApplicationLauncher.cs`: VS Code 以外の workspace IDE / workspace CLI 起動、補助アプリ起動。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs`: VS Code 起動、HWND 割り当て、専用 user-data 準備、取り逃がした既存スロットウィンドウの再接続。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeUserSettings.cs`: ハブ管理の VS Code ネットワーク設定。`User/settings.json` は通常起動の `%APPDATA%/Code/User/settings.json`（Roaming）と同じ実体にする。ハブは Roaming の内容を書き換えない。`storage.json` / Cache / サインイン状態は共有しない。
- `src/TurtleAIQuartetHub.Panel/Services/WindowEnumerator.cs`: アプリごとの管理対象ウィンドウ列挙。
- `src/TurtleAIQuartetHub.Panel/Services/ManagedWindowCloseService.cs`: `WM_CLOSE` 送信後の HWND 消滅確認。未終了ウィンドウを誤って管理解除しない。
- `src/TurtleAIQuartetHub.Panel/Services/WindowArranger.cs`: Win32 ベースの配置、配置ずれ確認、最大化、復元、前面/背面制御。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeWorkspaceState.cs`: VS Code / Antigravity の workspaceStorage とウィンドウタイトルからワークスペースを推定。タイトルはフォルダ名の部分文字列ではなく、ワークスペース段として照合する。
- `src/TurtleAIQuartetHub.Panel/Services/VscodeLayoutState.cs`: VS Code storage.json からレイアウト保存/復元。
- `src/TurtleAIQuartetHub.Panel/Services/TaskbarJumpListService.cs`: Jump List 更新。
- `src/TurtleAIQuartetHub.Panel/Models/SystemMonitorModels.cs`: システムモニタの表示モデル（`SystemResourceSnapshot`、`DriveUsageItem`、`FolderUsageItem`）。
- `src/TurtleAIQuartetHub.Panel/Services/SystemMonitorService.cs`: メモリ使用量（`GlobalMemoryStatusEx`）、CPU使用率（`GetSystemTimes` 差分計測）、各ドライブ使用状況（`DriveInfo`）、各フォルダ（A〜Dワークスペース、控えワークスペース、Hubデータ、VS Code / Antigravity IDEデータ）の使用量計測サービス。

## 実行時データ
- `%LOCALAPPDATA%/TurtleAIQuartetHub/slots.json`: visible slot と stored panel の保存状態。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/user-data/{A|B|C|D}/...`: `useDedicatedUserDataDirs=true` のときだけ使うスロット別 VS Code user-data-dir。既定は標準プロファイル共有のためこのフォルダは作らず、起動時に残存分を回収する。
- `%LOCALAPPDATA%/TurtleAIQuartetHub/config/turtle-ai-quartet-hub.json`: 任意のユーザー設定。
- タイトルバーの歯車設定から、IDE / CLI / Windows アプリの起動コマンドと、VS Code 共通のネットワーク設定（オープンネットワーク / プロキシ）をこのユーザー設定へ保存できる。

## 複数アプリ起動
- `defaultWorkspaceApplicationId` がスロットの既定アプリ。未設定時は `vscode`。
- `applications` で VS Code、Antigravity IDE、Codex CLI、Claude CLI、GitHub Copilot CLI、Grok Build CLI、Antigravity CLI、Codex / ChatGPT / Claude / Antigravity2 Windows アプリの起動コマンド、引数、検出候補を定義する。
- `slots[].applicationId` と `slots.json` の `ApplicationId` で、スロット/控えごとの起動対象を保持する。
- VS Code の既定は標準 user-data の共有。専用 `user-data-dir` は任意設定で、有効時は remote URI フォールバックと `code.lock` 再接続を維持する。
- Antigravity は汎用 workspace IDE として `%LOCALAPPDATA%/Programs/Antigravity IDE/Antigravity IDE.exe` 相当を優先検出し、ワークスペースパスを渡して起動し、新規ウィンドウを A-D の象限へ配置する。アプリ内でフォルダを開いた場合も `%APPDATA%/Antigravity IDE/User/workspaceStorage` から最新パスを保存する。旧名称やインストール形態に対応するため、`Antigravity`、`Google/Antigravity IDE`、`Google/Antigravity` と Local 側の候補も引き続き探索する。
- Codex / Claude / GitHub Copilot / Grok Build / Antigravity CLI は、対象スロットの保存済みワークスペースをカレントディレクトリにした `cmd.exe` ウィンドウで起動する。Antigravity CLI は公式コマンド `agy` を使い、公式 Windows インストーラーの `%LOCALAPPDATA%\agy\bin` も検出・起動 PATH に含める。
- 旧 `gemini` アプリ ID は読み込み時に `antigravity-cli` へ移行し、旧 Gemini のコマンドや表示名は引き継がず Antigravity CLI の既定定義へ置き換える。
- GitHub Copilot CLI の既定は `copilot` コマンドのみ。ワークスペースパスを暗黙引数として渡さない。
- スロット内 UI は `IDE` 枠と `CLI` 枠に分ける。別の IDE/CLI ボタンを押した場合は、現在のスロットウィンドウを閉じてから押したアプリを同じ象限へ開く。
- Codex / ChatGPT / Claude / Antigravity2 Windows アプリは `Windows` ラベル付きの補助ボタンとして表示し、Antigravity2 は Claude の右側に置く。
- 縮小表示の各スロットには 1 個の IDE トグルを置き、選択中の VS Code / Antigravity IDE を V / A で示してもう一方へ切り替える。補助アプリ行の左側には丸い矢印と「控え」を置き、同じ幅のまま小型の控えカードを 2x2 で表示する。
- 起動確認または periodic refresh でワークスペースを確認できたスロットは `SavedWorkspacePath` とタイトルを自動保存し、ワークスペース読み取りに失敗しても保存済みパスを消さない。
- 歯車設定では、表の Quartet と控え Quartet のタイトル、パス、保存済みパス、アプリ ID を一覧で確認・編集・空化できる。不完全な控えや重複控えは修復ボタンで整理できる。
- 歯車設定の「VS Code 共通ユーザー設定」はプロキシをハブ設定へ保存する。`User/settings.json`（プロキシ・テーマ・SSH プロキシ）は通常起動とハブ起動で同じ Roaming ファイルを使う。サイドバー幅、サインイン、チャット履歴、Cache は対象外。

## 確認コマンド
- 配布用の自己完結・単一ファイル win-x64 exe: ルートの `publish.bat` を実行する。出力は `dist/turtle-ai-quartet-hub/`。
- 通常ビルド: `dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- 終了確認、ワークスペースタイトル照合、VS Code 共通ユーザー設定の回帰テスト: `dotnet run --project .\src\TurtleAIQuartetHub.Panel.Tests\TurtleAIQuartetHub.Panel.Tests.csproj`
- 通常実行: `dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj`
- ビルド: `.\scripts\Build-Panel.ps1`
- 開発実行: `.\scripts\Build-Panel.ps1 -Run`
- Store readiness: `.\scripts\Test-StoreReadiness.ps1`
- ローカル MSIX: `.\scripts\New-LocalMsixPackage.ps1`

## Windows 補助アプリボタン（2026-09-28 更新）
- 標準表示と縮小表示では同じ `AuxiliaryApplications` を使用する。表示する既定ボタンは `GPT/Codex`、Claude、Antigravity2 の順で、ChatGPT Classic の `chatgpt-app` ボタンは表示しない。
- `chatgpt-app` の設定定義は既存ユーザー設定との互換性のため維持する。Codex Windows アプリの ID と検出条件は変えず、旧 `ShortName=Codex` を読み込んだときは `GPT/Codex` に移行する。
- 2026-09-28 のレイアウト調整では、縮小表示の「控え」を左端へ分離し、共用行の `AIアプリ` ラベルと 3 ボタンを右揃えにした。ボタンは同寸の 86×28 DIP、文字 11.5 DIP。
- その後の縮小表示調整では、標準表示の寸法を維持したまま、縮小表示の 3 ボタンだけを 78×25 DIP・文字 10.5 DIP にした。

## 縮小表示の前後ボタン調整（2026-10-02 更新）
- 「前」「背」はカードの可視枠と同じ高さの 24×24 DIP の正方形にし、各行の縦中央へ置く。
- ユーザーの撤回指示により、縮小表示へ追加した一括起動・一括終了ボタンは取り除いた。AI アプリ 3 ボタンは元の 78×25 DIP・文字 10.5 DIP に戻し、配色・枠・文字スタイルも維持する。
- 縮小ウィンドウの寸法とサイズ算出は変更しない。標準表示の一括操作と終了確認は既存どおり。

## 縮小表示の上下余白（2026-10-02 更新）
- ユーザーのバランス調整依頼により、タイトル下のカード枠の上マージンを 3 DIP から 0 DIP、縮小時の本体下マージンを 8 DIP から 6 DIP にした。
- 行の DesiredSize に含まれる Margin の二重加算を解消し、外周枠を含めて高さを算出する。閉じた縮小表示は幅 430 DIP・高さ 146 DIP。横幅とボタン・文字のサイズは維持する。

## カードのタイトル編集と設定画面の並び替え（2026-10-02 追加）
- 標準・縮小表示の表カードと控えカードはタイトルのダブルクリックで編集できる。Enter・欄外クリック・フォーカス移動で確定し、Escで取消。表示時は空き・既定タイトルなどの DisplayTitle を保つ。
- 標準表示のAIアプリ行は控えの見出しと同じ高さへ揃える。縮小表示のボタン寸法と開閉時の位置固定は維持する。
- 設定の表・控え一覧に上下ボタンとドラッグつまみを置く。つまみ選択中は上下キーでも移動でき、カードの並びと保存状態へ即時反映する。控えはページをまたいで移動できる。
- 2026-10-02 UI改修: 並び替えボタンをアプリのダークグリーン＆ネオングリーンに調和した縦型スプリットステッパーへ刷新。上部に ChevronUp、中央にモダンな6ドットグリッパー（ドラッグ＋キー操作対応、フォーカス時アクセント発光）、下部に ChevronDown を一体型コンテナ内に配置し、左端列幅を44 DIPに最適化して入力領域を拡大。

## メイン4カードのタイトル視認性向上とシステムモニタ（2026-10-19 追加）
- 高解像度PC画面での視認性向上のため、標準表示のメイン4カード（`SlotCardTemplate`）のタイトル文字を太字（`FontWeight="Bold"`）・`FontSize="15.5"`・高コントラスト色（`#F8FCF9`）へ拡大。カード高さ（169 DIP）やアプリ全体のウィンドウサイズ（650×535 DIP）を一切圧迫しないよう、ヘッダ内の `ShortPath` 上マージン調整と `InlineTitleTextBox` テンプレートの `TemplateBinding` 対応により既存の40 DIP高ヘッダ枠内に収めた。
- タイトルバーの歯車設定ボタン（`SettingsButton`）のすぐ左にシステムモニタボタン（`SystemMonitorButton`、アイコン `&#xE9D9;`）を追加。
- クリックするとウィンドウ内モーダル（`SystemMonitorOverlay`）が開き、物理メモリ使用量、CPU使用率、各ドライブの使用状況（1.5秒周期の自動更新＋エクスプローラー起動ボタン付き）、および各フォルダ（スロットA〜D、控えパネル、Hubデータ、VS Code / Antigravity IDEデータ）の使用量（バックグラウンド非同期計測・再計測ボタン・エクスプローラー起動ボタン付き）を表示する。

## システムモニタの折りたたみ・VS Code 終了復旧（2026-11-12 追加）
- フォルダ分類（表・控え・アプリ・AI エージェントと一時フォルダ）はそれぞれアコーディオン。モニタを開くたび全閉で、同じモニタ内の再計測時は開閉状態を保つ。閉じた分類でも従来の非同期計測は継続する。
- 「VS Code の終了・復旧」から現在のユーザー・同じ Windows セッションの通常版 / Insiders を列挙し、対象一覧と未保存編集・ターミナル・AI 処理中断の警告を確認した後に強制終了できる。ハブ外の VS Code も含む。
- 確認済みプロセスだけを終了し、後から起動したものは自動終了しない。終了・既に終了・失敗・残留を結果画面に表示する。VS Code の再起動・更新そのものは行わない。
- ユーザー版のように同じ整合性レベルなら終了権限だけで停止する。システム版や管理者として起動した VS Code でアクセスが拒否された場合だけ、拒否された対象に限り1回の管理者確認で終了する。キャンセル時は残す。
- 実装: `MainWindow.VscodeProcessRecovery.cs`, `Models/VscodeProcessRecoveryModels.cs`, `Services/VscodeProcessRecoveryService.cs`, `Services/VscodeProcessRecoveryElevation.cs`。詳細仕様は `docs/design/system-monitor-recovery-plan.md`。
## AI 作業通知の設定場所・内容確認（2026-11-12 追加）
- 長い説明を短い概要と補足の折りたたみに整理し、Claude Code / Codex / GitHub Copilot を箇条書きで表示する。各行に設定状態とホーム省略パスを表示し、完全なパスと詳しい状態はツールチップで確認できる。
- 各行の「フォルダ」から既存の設定フォルダをExplorerで開き、「設定を見る」からアプリ内の読み取り専用画面で実際の設定ファイルを確認できる。ファイル・フォルダの未作成時は対応する操作を無効化する。
- 読み取り専用画面は原文・改行を保持し、選択・コピーと縦横スクロールが可能。表示上限は256Ki文字で、省略時は明記する。閲覧では設定・フォルダ・バックアップを作成しない。閉じると表示内容を破棄する。
## ドキュメントの入口と配置（2026-11-12 整理）
- 入口は `docs/index.md`。各ファイルの目的と、資料の年代・草案/調査記録としての位置付けを一覧化する。
- 利用ガイド `docs/guides/`、設計・実装計画 `docs/design/`、開発・検証 `docs/development/`、公開・Store `docs/release/`、不具合調査 `docs/investigations/` に分類。ドキュメント用のSVG・画像は `docs/assets/` に置く。
- AIフックの設定・通知経路・状態表示は `docs/guides/ai-notification-hooks.md`。通知経路図と状態図のSVGを本文から相対パスで参照する。設定済みと通知の疎通、応答終了と作業成功、Claude/Codexのバックアップ、Copilot専用JSON、Grok専用JSONの扱いを区別して説明する。
- 旧docs直下のファイル名は維持してカテゴリ内へ移動した。古いパスを見かけた場合もindex内の同じ名前から移動先を確認できる。
## 設定内のフック図解ガイド（2026-11-12 追加）
- 「AI の作業通知」の「仕組みを見る」で、同じ設定枠のガイドページに切り替える。「通知の流れ」「設定で変わること」「表示の見方」を選び、短い説明とWPFベクター図で確認できる。
- 「設定に戻る」・ガイド右上の×・Escapeで設定画面へ戻り、編集状態とスクロール位置を保持する。ガイドの閲覧から設定保存やフック設定/解除は呼ばない。
- `Views/AiHookGuideView.xaml` は外部画像・SVGライブラリ・ブラウザに依存しないため、配布版でもオフラインで閲覧できる。本文の詳細は `docs/guides/ai-notification-hooks.md`。
## AI 作業通知の完了色（2026-11-12 追加）
- 黄色の入力待ちは採用しない。Claude / Codex / GitHub Copilot で作業中は緑の明滅、完了と許可待ちは同じ薄い青の光。
- 応答後の `idle_prompt` は使わない。許可を知らせるイベントが無いツールでも、完了時の色は同じ。
## AI 作業通知へ Grok を追加（2026-11-12 追加）
- Grok も同じ緑の明滅と、同じ薄い青の光。黄色や Grok だけの色は付けない。
- 保存先は `~/.grok/hooks/turtle-ai-quartet-hub.json`。`UserPromptSubmit` で作業中、`Stop` / `StopFailure` / `StopCancelled` で完了、`Notification` の `permission_prompt` と `elicitation_dialog` で許可待ち。`idle_prompt`、`task_complete`、`PermissionDenied` は無視する。
- `~/.grok` が無いときは未インストールとして何も作らない。`config.toml` とほかのフックファイルは変更しない。解除でハブの項目だけになった専用ファイルは削除し、`.turtle-hub-backup` は残す。
- すでに開いている Grok は、設定のあとセッションを開き直すまで新しいファイルを読まない。未信頼の作業フォルダでは `/hooks-trust` が必要。ホーム直下は信頼済み。
- 詳細は `docs/guides/ai-notification-hooks.md`。回帰284件（AI 作業通知32件、保存先・閲覧14件）が合格。実ホームのフックと実行中セッションは変更していない。光の目視は未実施。
## フックガイドの初心者向け図（2026-11-12 追加）
- `docs/guides/ai-notification-hooks.md` は、フックの基本、AI の状態と光の対応、アプリへの届き方、今の実装の順で説明する。
- 図は `docs/assets/ai-notification-hooks-basics.svg`（状態とフック）、`ai-notification-to-app.svg`（届き方）、既存の状態図と実装経路図。実装経路図の完了バッジも同じ薄い青。チャットの読み取りや、ツールごとの別色は描かない。
- アプリ内の「仕組みを見る」は3項目のまま。詳しい基本説明はドキュメント側に置く。
## 許可の確認中は緑のまま（2026-11-12 見直し）
- 利用者の指摘により、許可や質問の確認では青く光らせない。作業が始まったあとの続きなので、緑の明滅が続く。青い光は応答が終わったときだけ。
- それ以前の「完了と許可待ちは同じ薄い青」は現在の仕様ではない。黄色や、ツールごとの別色は使わない。
- `permission_prompt`、`elicitation_dialog`、Codex の `PermissionRequest` は届いても表示を変えない。`idle_prompt` も無視したまま。
- 状態図の戻り矢印は、青い箱の下辺から作業中または待機の下辺へつなぐ。説明の帯で線を隠さない。
- フックガイドの先頭は、AI が小さなプログラムを起動し、開いているハブが受け取って自分でバッジの光を変える、の順で書く。AI はバッジを塗らない。この5段階は `docs/assets/ai-notification-who-moves.svg`。主体は、1と2が AI ツール、3が AI の起動したプログラムからハブへの通信、4と5がハブ。3番は同じパソコンの名前付きパイプ `TurtleAIQuartetHub.Commands` で、一方的に1回書く。
## アプリ内「通知の流れ」に主体と通信を記載（2026-11-13 追加）
- 利用者の指摘により、設定内「仕組みを見る」の「通知の流れ」へ、ドキュメントと同じ主体と通信の説明を入れた。3枚の短いカードでは、誰が動き、何で渡しているかが分からなかった。
- 主体は AIツール、フックのプログラム、ハブ。通信は同じパソコンの名前付きパイプ `TurtleAIQuartetHub.Commands` で、一方的に1回書く。色を変えるのはハブ。開始は緑の明滅（許可や質問の確認中も緑）、応答が終わったときだけ薄い青。
- タブは「通知の流れ」「設定で変わること」「表示の見方」の3つのまま。図は WPF のベクターで、外部 SVG は埋め込まない。狭い設定枠では横5列にせず、上に3主体、下に1〜5の縦の流れを置く。
- それ以前の「詳しい基本説明はドキュメント側だけ」は、この流れタブには当てはまらない。ツールごとの出来事の表は、引き続き `docs/guides/ai-notification-hooks.md` に置く。挙動、フック登録、実ホームは変えていない。
- 図と説明の主体は「ハブ」ではなく「このアプリ（カルテットハブ）」と書く。Turtle AI Code Quartet Hub のことである。4と5の主体表示も「このアプリ」。
## 図と説明は「この」「その」を使わず名前で書く（2026-11-13 追加）
- 利用者の指摘により、フックガイド、`docs/assets/ai-notification-*.svg`、設定内「通知の流れ」では、このアプリ、そのプログラム、このフォルダ、そのバッジ、そのスロット、この緑、ここでは、と書かない。
- 名前は、AIツール、フックのプログラム、カルテットハブ。フォルダは作業フォルダ。光は緑の明滅。バッジは選んだスロットのバッジ、または光っているスロット。
- それ以前の「このアプリ（カルテットハブ）」と「主体：このアプリ」は現在の書き方ではない。裸の「ハブ」にも戻さない。カルテットハブは Turtle AI Code Quartet Hub のことである。
- 挙動、フック登録、実ホームは変えていない。光の実機目視は未実施。
## 標準表示フッターのボタン配置（2026-11-13 追加）
- 標準表示の右下は2段。上段は「全ディスプレイ移動」「最前面」「最背面」を同じ幅（1/3）。下段は「非表示」が1/3、「すべて閉じる」が2/3。
- 「設定を保存」「設定を読み込み」は歯車の設定画面へ移した。スクロールの先頭カード「スロットと控え」から、タイトル、ワークスペース、選択アプリを保存ファイルへ書き込む。起動コマンドのパスは、設定画面下の「パス設定を保存」。
- 「すべて閉じる」は高さ 24 DIP のまま、設定の「クリアする」と同じ赤系（`DangerBarButton`）。ホバーも赤のまま。
- 縮小表示の「前」「背」と各カードの非表示は変えない。保存と読み込みの中身も変えない。
- 650×535 DIP の非表示描画で、上段の実幅は 109 / 110 / 109 DIP、下段は 111 / 221 DIP、高さは 24 DIP。「全ディスプレイ移動」の希望幅 95 DIP は実幅に収まる。設定を開くと先頭カードが見え、「設定を保存」の実幅は 124 DIP、「設定を読み込み」の実幅は 140 DIP。実行中のハブは起動していない。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告 0、エラー 0。
## 「すべて閉じる」の色はカードのクリアに合わせる（2026-11-13 追加）
- 利用者の指摘により、「すべて閉じる」の色はカード右上のクリア（`SlotClearInfoButtonStyle`）と同じにする。地 `#261816`、枠 `#6A3832`、文字 `#F2B7AF`。ホバーは地 `#33201D`、枠 `#A65348`、文字 `#FFE7E2`。
- それ以前の「設定の『クリアする』と同じ赤系（`#6F2D27` / `#B3574E`）」は現在の色ではない。確認ダイアログの「クリアする」の色は変えない。高さ 24 DIP と 2/3 幅は維持する。
## 作業中と完了の光はカード枠と A〜D アイコン（2026-11-13 更新）
- 作業中はカードと A〜D アイコンの枠（`AiBadgeWorkingRingStyle`）が 5.6 秒周期のホタル点滅でゆっくり緑に明滅し、枠の外が境界のないぼかした光（`AiCardHaloStyle`、`BlurEffect Radius="15"`）になる。フォーカス表示時は太い 2px フォーカス枠を固定のまま、外側の光背と A〜D アイコン枠を明滅させる。
- 完了は白っぽさを抑えたアプリ調和型のブルーへ刷新し、点滅せずに点灯する。カードは地 `#112D46`・枠 `#3EA3EC`・外側の光 `#228BE0`（`Opacity="0.74"`）、標準・縮小の A〜D アイコンは少し濃いめの `#174F85`・枠 `#68C1FF`・文字 `#F0F8FF`（`AiDoneBadgeGlowEffect`）、極小表示のボタン自体も `#174F85`・文字 `#F0F8FF` で点灯する。
- ボタン、IDE / CLI の枠の中（地 `#121816`）は塗らない。許可の確認中は緑の明滅のまま。フォーカス表示の切り替えで消える仕組みは変えない。
- 設定の「仕組みを見る」とフックの図もカード枠＋ A〜D アイコン点灯の新配色に合わせて更新した。


## Antigravity の AI 作業通知フック対応（2026-11-13 追加）
- 「AI の作業通知」の明示フック対象に **Antigravity**（CLI `agy` / Antigravity IDE / Antigravity2 共通の `~/.gemini/config/hooks.json`）を追加（合計 5 ツール: Claude Code / Codex / GitHub Copilot / Grok / Antigravity）。
- `turtle-ai-quartet-hub` という名前付きフックとして `enabled: true`、`PreInvocation`（開始 → 緑の明滅）、`Stop`（完了 → 薄い青。ただし `terminationReason == "USER_CANCELED"` は `Interrupt` として緑の明滅だけを止め、偽の青を残さない）を平坦なハンドラ配列で登録する。
- 別製品 `@google/gemini-cli` の `~/.gemini/settings.json` だけが存在する環境を誤認しないよう、`~/.gemini/config`、`antigravity`、`antigravity-cli`、`antigravity-ide` のいずれかがある場合のみインストール済みと判定する。


## Antigravity の AI 作業通知フック対応（2026-11-13 追加）
- 「AI の作業通知」の明示フック対象に **Antigravity**（CLI `agy` / Antigravity IDE / Antigravity2 共通の `~/.gemini/config/hooks.json`）を追加（合計 5 ツール: Claude Code / Codex / GitHub Copilot / Grok / Antigravity）。
- `turtle-ai-quartet-hub` という名前付きフックとして `enabled: true`、`PreInvocation`（開始 → 緑の明滅）、`Stop`（完了 → 薄い青。ただし `terminationReason == "USER_CANCELED"` は `Interrupt` として緑の明滅だけを止め、偽の青を残さない）を平坦なハンドラ配列で登録する。
- 別製品 `@google/gemini-cli` の `~/.gemini/settings.json` だけが存在する環境を誤認しないよう、`~/.gemini/config`、`antigravity`、`antigravity-cli`、`antigravity-ide` のいずれかがある場合のみインストール済みと判定する。

## 作業中の光は枠を一周する筋（2026-11-16 追加）
- 利用者の指摘により、作業中の 5.6 秒ホタル点滅（枠全体と外側の光背、A〜D アイコン枠）は現在の見え方ではない。
- 作業中は `AiCardOrbitGlow` が、カードの枠の上を一本の緑の光の筋で約 1.15 秒一周し、約 0.5 秒止まってから同じ向きでもう一周する。光は筋だけに付き、LED のようににじむ（芯 `#F4FFF8`、影 `#45D483`）。標準・縮小・極小とも同じ。許可の確認中もこの周回のまま。
- 完了の深い青（カード地 `#112D46`、枠 `#3EA3EC`、光背 `#228BE0`、A〜D は `#174F85`）は変えない。状態の判定、フック、フォーカス表示の切り替えで消す処理も変えない。
- それ以前の「枠全体がゆっくり明滅する」「作業中に A〜D の枠が点滅する」は、作業中の現在の表示ではない。

## 作業中の光はぼんやりした緑で加速する（2026-11-16 追加）
- 利用者の指摘により、白い芯のくっきりした筋と一定速度の周回は現在の見え方ではない。回り方（枠を一周して少し休む）は残す。
- 作業中は `AiCardOrbitGlow` がテーマの緑をぼかして枠の上を一周する。外側は `#45D483`（不透明度 0.70、太さ 16、ぼかし 22）、内側は `#67E7A4`（不透明度 0.38、太さ 7、ぼかし 16）。白い線は出さない。約 1.78 秒で一周し、最初は遅く終わりにかけて速くなる。約 0.44 秒止まってから同じ向きに繰り返す。標準・縮小・極小とも同じ。許可の確認中もこの周回のまま。
- 完了の深い青と状態の判定、フック、フォーカス表示の切り替えで消す処理は変えない。
- 確認は `MainWindow` を起動せず、300×170 の軌道を描いた。0.40 秒は左上、1.68 秒は上の右寄りで、中心の距離は 181。前半 0.24 周/秒、後半 0.93 周/秒。明るい画素は緑で、枠の周の約 2%。`dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj` は警告 0、エラー 0。回帰 284 件は再実行していない。

## 作業中の光を枠線上の滑らかな LED 周回へ改善（2026-11-16 刷新）
- 前節の「太さ 16 / ぼかし 22 の `Path` + `StrokeDashArray` + `1.78s` 急加速周回」では、`Shape.GetLayoutClip` 等によりカード周囲に四角いクリップ角が露出し、角丸カーブにも沿わず、終盤の速度が速すぎて目障りになっていたため刷新した。
- 作業中は `AiCardOrbitGlow`（`DrawingVisual` + 直線・厳密 `ArcTo` による 28 段グラデーション・トレイル）が、`BorderThickness="1"` の中心線（`inset=0.5`, 角丸 `R-0.5`）上を、尾を引きながら消える一筋の緑の LED 光（標準約 3.60 秒 / 縮小約 2.77 秒 / 極小約 2.45 秒）としてふわっとゆっくり巡り、終盤に約 2.0 倍程度へ心地よく少しだけ加速してシームレスに繰り返す。
- 完了の深い青、状態の判定、フック、フォーカス表示切り替えで消す処理は変えない。`AiOrbitGlowTests`（8件）を回帰テストへ追加。

## 完了時青表示の早めホタル点滅と IDE/CLI 枠内非塗りつぶし（2026-11-16 追加）
- 完了（`AiActivityState.Done`）の青表示を、従来の静的青点灯から、少し早めの 3.0 秒周期ホタル点滅（`AiDoneFirefly` 添付ビヘイビア）へ変更した。カード地・1px 青枠・外周光背・A〜D アイコン（極小はカードタイル自体）が溜めと余韻を持つ非対称カーブで同位相同期して明滅する。
- 標準表示カードの `IDE` / `CLI` グループ見出しは、`ApplicationGroupLabelStyle` の背景塗りを廃して完全透明化し、`GroupHeaderCutout` により 1px 枠線の上辺見出し部分だけを切り抜くことで、枠内（`#121816`）へカード背景色が食い込まないようにした。
- 詳細・注意点・QA は `implementation-patterns.md` §11 と `current-issues-qa.md` に記録。

## AI 実行状態のスタック・解除漏れ・誤点灯ルート修正（2026-11-17 追加）
- パイプの単発・1 インスタンス制限による取りこぼし、Claude（`StopFailure` / `SessionEnd`）・Codex（`SessionEnd`）・Copilot（`errorOccurred` / `sessionEnd` / `agentStop.reason`）および Antigravity cancel 表記漏れ、同ツール孤立セッションキーの残存による `Done` 解除後の `Working` 逆戻り、外部ウィンドウ遮蔽下フォーカス再クリック時の未解除、`SetForegroundWindow` 失敗時の最大化未反映をすべて修正した。
- 詳細と不変条件は `implementation-patterns.md` §12 と `current-issues-qa.md` に記録。既存のフック設定済みの実環境ホームでは、新イベント・強化版 `ai-notify.ps1` を反映するため歯車設定から「フックを設定」の再実行を案内する（自動書換は行わない）。

## ダウンロードフォルダボタン・AI 状態リセット機能追加（2026-11-17 追加）
- タイトルバーのプルリクエスト受信箱ボタン（`PullRequestInboxButton`）のすぐ左側に、`DownloadsFolderButton`（`&#xE896;`）を追加し、`SHGetKnownFolderPath(FOLDERID_Downloads)` 優先＋フォールバックでダウンロードフォルダを Explorer で開けるようにした（標準表示時のみ表示）。
- 設定画面の「AI の作業通知」セクション右上に `ResetAiActivityButton`（「AI 状態をリセット」）を追加し、Swarm 利用時などの表示ズレ時に設定ファイルへ触れず全スロット（A〜D）の `AiTracker` 表示状態を待機へ即時リセットできるようにした。詳細は `implementation-patterns.md` §13 と `current-issues-qa.md` に記録。

## アプリ起動演出と単体／一括ステルス一斉表示（2026-11-25 追加・刷新）
- アプリ本体起動・終了演出（`MainWindow.StartupAnimation.cs`）では、`ModeMorphGhostWindow` と同じ実績ある「本物を `DWMWA_CLOAK` で隠しつつ、`AllowsTransparency=True` の超シンプル影武者ウィンドウ上で WPF GPU 独立クロックの `DoubleAnimation` を走らせる」方式を採用した（`AllowsTransparency=False` への `SetWindowLong(WS_EX_LAYERED)` は WPF 内部で自動剥ぎ取りされ `SetLayeredWindowAttributes` がエラー 87 で失敗するため禁止）。旧版のカクつき原因（`BitmapCache` への毎フレーム拡大縮小・`HighQuality` フィルタ・多重 `BlurEffect` / リング・`_refreshTimer` 競合）を完全排除して最小 3 レイヤー構成とし、起動時は `120ms`＋影武者安定 `70ms` の余裕待機を経て `920ms` の S 字カーブでふわっと浮かび上がり、終了時も確認オーバーレイ消去の確定（`70ms`）と影武者安定（`60ms`）のあと `680ms` で静かに消え失せる（OS シャットダウン・`--exit`・OS アニメーションオフ時・オフスクリーン検証時は即処理）。
- 単体起動・アプリ切替・控え復帰・`Launch Quartet` 一括起動、および「すべて閉じる」・個別スロット「閉じる」を `MainWindow.LaunchPresentation.cs`・`Services/LaunchWindowCloakSession.cs`・`Services/LaunchStagingOverlay.cs` の共通パイプラインへ統一した。
- 象限ベール（`QuadrantVeilWindow`）も正式な `AllowsTransparency=True` + GPU `DoubleAnimation` へ統一し、確実に滑らかなフェードイン／フェードアウトが描画されるようにした。起動時は裏で準備している間の激しい点滅を抑え、全員が揃ったら一拍（`260ms`）置いて、`BeginTime` 一括 GPU タイムラインにより A→B→C→D へ `480ms` 間隔で本当に 1 つ 1 つ LED ランプが「ぽっ…ふぁ〜っ…」と灯ってから `580ms` の S 字カーブで開く（他プロセスへの `DWMWA_CLOAK` が OS 制限で拒否される環境でもベール基準で確実に動作）。
- 終了時は開いている全対象ウィンドウ（最大化中ならモニタ作業領域全面、4 面なら各セル）へ完全不透明（`Opacity=1.0`）のベールと中央 HUD を一斉に被せ、裏での個別終了タイミング差を一切見せずに全員の終了を待ってから（4.5 秒超の未保存確認ダイアログ待ちのみ個別自動解放）、人間が「すべて完了した」と知覚できるワンクッション（一括 `720ms`／単体 `460ms`・HUD 完了静止点灯＋各象限「✓ 終了完了」）を置いたあと、全象限ベールと中央 HUD へ同時に `420ms` の軽やかなフェードアウト（`CubicEase EaseOut`）を一斉投入して「フッ」と消す。
- 標準共有プロファイル（`UseDedicatedUserDataDirs=false`）でも、他スロット未割り当てでタイトルがワークスペースと一致する既存 VS Code ウィンドウがあれば安全に再接続し、ゾンビ残存プロセスで新規起動がタイムアウトした場合はシステムモニタの「VS Code の終了・復旧」を案内する。詳細は `implementation-patterns.md` §19 と `current-issues-qa.md` に記録。
