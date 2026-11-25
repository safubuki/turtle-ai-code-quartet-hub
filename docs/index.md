# ドキュメント一覧

Turtle AI Code Quartet Hub の資料を、目的別にまとめています。各ファイルの内容は下の一覧から確認できます。

整理日: 2026-11-12

## まず読みたい資料

| 知りたいこと | 読む資料 |
|---|---|
| AI のフックを設定し、完了通知が届く仕組みを知りたい | [AI の作業通知：フック設定と通知の流れ](guides/ai-notification-hooks.md) — 基本から実装までのベクター図付き |
| UI アニメーションの設計・使い分け・他アプリへの応用を知りたい | [UI アニメーション設計・実装ガイド](guides/ui-animation-guide.md) — シーン別一覧・5 層構造・フェイルセーフのベクター図付き |
| データ容量が増えた原因や診断コードを調べたい | [容量の解析・診断コード一覧](guides/data-folder-diagnostics.md) |
| IDE・CLI の起動やスロット管理の設計を知りたい | [複数アプリケーション起動の仕様](design/multi-application-launcher-spec.md) |
| 画面を安全に検証したい | [WPF UI の検証手順](development/wpf-ui-validation.md) |
| Store 公開・パッケージ作成の資料を探したい | 下の「リリース・Store」の一覧 |

## フォルダの役割

```text
docs/
├── index.md          ← 資料を探す入口（このページ）
├── guides/           ← 利用方法・機能の仕組み・診断コード
├── design/           ← 設計仕様・実装計画・検証記録
├── development/      ← 開発・画面検証の手順
├── release/          ← MSIX・Store 公開手順・提出用の草案
├── investigations/   ← 不具合の原因調査・修正の経緯
└── assets/           ← ドキュメントから参照する SVG・画像
```

## 利用ガイド — `guides/`

| ファイル | この資料の内容 |
|---|---|
| [ai-notification-hooks.md](guides/ai-notification-hooks.md) | 現在の AI フック設定、設定ファイルの場所、通知がカルテットハブへ届く経路、バッジの見方、通知が届かないときの確認方法。 |
| [ui-animation-guide.md](guides/ui-animation-guide.md) | 本体起動・終了・最小化（タスクバーのボタンへ吸い込み）と復元（ボタンから広がる）、モード切替、ワークスペース起動／終了（5 層遮蔽・1 つずつランプ点灯・完了ワンクッション）、AI 通知のアニメーション設計と WPF 実装原則・5 重フェイルセーフ。 |
| [data-folder-diagnostics.md](guides/data-folder-diagnostics.md) | システムモニタの容量解析で表示する診断コード、容量増加の原因、対処方法と開発側の改善案。 |

## 設計・実装計画 — `design/`

| ファイル | この資料の内容・位置付け |
|---|---|
| [multi-application-launcher-spec.md](design/multi-application-launcher-spec.md) | IDE・CLI・補助アプリの分類、起動・配置・保存、画面構成の設計仕様。過去の画面仕様や後からの追記も含む設計資料。 |
| [micro-mode-ui-concepts.md](design/micro-mode-ui-concepts.md) | 極小表示（Micro モード）の UI/UX 改善比較（案 A〜C）、採用した円形オービット・ダイヤル（案 B）の仕様、および将来再利用できる超極小宝石展開（案 C）の設計メモ。 |
| [system-monitor-recovery-plan.md](design/system-monitor-recovery-plan.md) | フォルダ分類の折りたたみと、VS Code の終了・復旧機能の仕様・実装計画・検証記録。記載された実装フェーズは完了済み。 |

## 開発・検証 — `development/`

| ファイル | この資料の内容 |
|---|---|
| [wpf-ui-validation.md](development/wpf-ui-validation.md) | 実ウィンドウや利用者の保存状態へ影響を与えず、WPF 画面を非表示で描画・検証する手順。 |

## リリース・Store — `release/`

公開手続き・準備状況・提出用の文章を分けて管理しています。草案やチェックリストの完了状況は記録時点のもので、現在の公開状況を示すものではありません。

| ファイル | この資料の内容・位置付け |
|---|---|
| [msix-packaging-guide.md](release/msix-packaging-guide.md) | このリポジトリの MSIX 構成、パッケージの生成、署名、Windows App Certification Kit による確認の手順。 |
| [WindowsAppsStoreリリース手順.md](release/WindowsAppsStoreリリース手順.md) | アカウント登録から Partner Center への提出・認定・公開後の更新までをまとめた実務手順書。2026-04-26 時点の記録を含む。 |
| [store-readiness.md](release/store-readiness.md) | Store 公開前に、リポジトリ内で準備・確認する項目のチェックリスト。 |
| [store-release-todo.md](release/store-release-todo.md) | 初回公開候補 1.0.0 のソフトウェア準備と公開手続きを整理した進捗表。2026-06-20 時点の記録で、解消済みの指摘も残る。 |
| [store-listing-draft.md](release/store-listing-draft.md) | Store 掲載用のアプリ説明、依存関係、プライバシー・サポート表記の草案。 |
| [release-notes-draft.md](release/release-notes-draft.md) | Microsoft Store や GitHub Releases に掲載する、初回公開リリースノートの草案。 |

## 不具合調査・修正記録 — `investigations/`

調査当時の現象・原因・修正内容・検証範囲を残した資料です。記載された環境固有のパス、テスト件数、未検証事項は、その調査時点の記録として読んでください。

| ファイル | この資料の内容 |
|---|---|
| [antigravity-close-memory-analysis-2026-08-24.md](investigations/antigravity-close-memory-analysis-2026-08-24.md) | 終了操作後にウィンドウが残る問題とメモリ使用を調べ、終了確認の修正を記録した報告。 |
| [antigravity-cli-detection-analysis-2026-10-03.md](investigations/antigravity-cli-detection-analysis-2026-10-03.md) | Antigravity CLI の検出漏れ、初回判断の訂正、探索先の追加を記録した報告。 |
| [vscode-proxy-persistence-analysis-2026-10-07.md](investigations/vscode-proxy-persistence-analysis-2026-10-07.md) | 専用プロファイルのプロキシ設定が起動時に戻る原因と、設定保持の修正記録。 |
| [manual-window-focus-analysis-2026-10-12.md](investigations/manual-window-focus-analysis-2026-10-12.md) | IDE の直接最大化・復元と、ハブのフォーカス状態が競合する問題の調査・修正記録。 |
| [window-layer-buttons-analysis-2026-10-14.md](investigations/window-layer-buttons-analysis-2026-10-14.md) | 最前面・最背面操作で起きるウィンドウの前後関係の競合と、その修正・検証記録。 |
| [memory-warning-analysis-2026-11-07.md](investigations/memory-warning-analysis-2026-11-07.md) | メモリバーと警告文・アイコン色の不一致を調べ、判定基準を統一した修正記録。 |

## 図・画像 — `assets/`

| ファイル | 使用する資料・内容 |
|---|---|
| [display-modes.svg](assets/display-modes.svg) / [display-modes.png](assets/display-modes.png) | 標準・縮小・極小の 3 つの表示モードを並べた図（README で使用）。実際の画面を見本データで描画したものを SVG に埋め込み、PNG は SVG から書き出したもの。 |
| [ai-notification-who-moves.svg](assets/ai-notification-who-moves.svg) | 色が変わるまでに動く5つ。AIツールの判断、フックのプログラムの起動、カルテットハブへの短い知らせ、スロットの判断、バッジの色。 |
| [ai-notification-flow.svg](assets/ai-notification-flow.svg) | AI フックの設定ファイルから、通知スクリプト、カルテットハブ、対象スロットへ届く全体の流れ。 |
| [ai-notification-states.svg](assets/ai-notification-states.svg) | AI の作業中・完了・確認中・中断と、バッジ表示の関係。青は完了だけ。 |
| [animation-scenes-map.svg](assets/animation-scenes-map.svg) | 本体起動・終了、モード切替、ワークスペース起動／終了、AI 通知の 5 シーン別アニメーション一覧マップ。 |
| [animation-five-layer-architecture.svg](assets/animation-five-layer-architecture.svg) | 中央 HUD〜黒い全画面シールド〜裏の実ウィンドウの 5 層 Z 順構造と、起動・終了タイムラインの違い。 |
| [animation-wpf-pitfalls-and-failsafe.svg](assets/animation-wpf-pitfalls-and-failsafe.svg) | WPF で必ず避けるべき 5 大落とし穴と、黒画面残留・操作不能を防ぐ 5 重フェイルセーフ構造。 |

SVG は [フック設定の解説](guides/ai-notification-hooks.md) から画像として参照しています。ブラウザで開いたり拡大したりしても、文字や線が鮮明に表示されます。Store 掲載用の画像は、別の [リポジトリ直下の assets/store](../assets/store/README.md) で管理しています。

## 読むとき・追加するときの目安

- 資料内のコマンドは、特記がなければリポジトリのルートから実行します。`src/...` や `scripts/...` と書かれたコード上のパスも、ルートを基準にしています。
- 2026-11-12 に、直下の既存16文書を上記のカテゴリへ移動しました。ファイル名は維持しているので、旧パスを見かけた場合はこの一覧から同じ名前の移動先を探せます。
- 新しい文書は目的に合うフォルダへ置き、この一覧に用途を1行追加してください。説明図・画像は `assets/` に置き、本文から相対パスで参照します。
