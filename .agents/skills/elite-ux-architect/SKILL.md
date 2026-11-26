---
name: elite-ux-architect
description: UI を作る・直すときに、assets/ のデザイントークン（tokens.css）・部品（base.css）・完成画面のお手本（LP、ダッシュボード、設定フォーム、記事、スマホアプリ、AI で作る・直すツール、AI エージェント、部品カタログ）を写して組み、scripts/capture.mjs でスクリーンショットを撮って目で確かめて直し、scripts/ux_audit.py でコントラストやフォーカスを機械検査するスキル。形容詞ではなく、お手本と数値ルールと目視で品質を出す。「UIを作って」「画面を作成」「デザインして」「LP」「ランディングページ」「ダッシュボード」「管理画面」「設定画面」「フォーム」「UI改善」「見た目を良く」「デザインを整えて」「配色」「ダークモード」「スマホ対応」「UIレビュー」「AI機能の画面」「チャットUI」「エージェントの画面」などで発火。
---

# Elite UX Architect

## スキル読み込み通知

読み込んだら 1 行だけ通知する：

> 🎨 **Elite UX Architect を読み込みました** — お手本を写して組み、スクリーンショットで確かめながら仕上げます。

## 考え方

指示が無いと LLM は学習データで多数派の無難な見た目（定番フォント、紫のグラデーション、飾りのアイコン、中身のない大見出し）に寄る。「美しく」と指示しても変わらないので、このスキルは具体的な見本と値で上書きする（根拠と確度は [references/evidence-ledger.md](references/evidence-ledger.md) G〜J 章）。お手本は検証済みの正解ではなく、規範・研究・実務の慣習から組んだ出発点。3 つの道具で品質を出す。

| 道具 | 中身 | 使い方 |
|---|---|---|
| **お手本**（assets/） | トークン、部品 CSS、完成した 8 画面 | 作る画面に近いものを開いて、構成・余白・部品を**そのまま写す** |
| **ルール**（references/） | 数値ルール、パターンの判断表、避けるパターン | 迷ったら値と判断表で決める。新しい値を発明しない |
| **確認**（scripts/） | スクリーンショット撮影、機械検査 | **描画して画像を見る。** コードを読んだだけで完成にしない |

## 新しく作るとき

### Step 1: お手本を選んで開く

[references/pattern-catalog.md](references/pattern-catalog.md) の表から、作る画面にいちばん近いお手本を選び、HTML を読む。

| 作るもの | お手本 |
|---|---|
| 製品紹介・LP・ポートフォリオ | [assets/exemplars/landing.html](assets/exemplars/landing.html) |
| 管理画面・分析・一覧 | [assets/exemplars/dashboard.html](assets/exemplars/dashboard.html) |
| 設定・登録・編集フォーム | [assets/exemplars/settings-form.html](assets/exemplars/settings-form.html) |
| ブログ・ドキュメント | [assets/exemplars/article.html](assets/exemplars/article.html) |
| スマホ中心のアプリ・PWA | [assets/exemplars/mobile-app.html](assets/exemplars/mobile-app.html) |
| AI で作る・直すツール（文章・画像などの生成） | [assets/exemplars/ai-assistant.html](assets/exemplars/ai-assistant.html) |
| AI に複数の手順を任せる（エージェント・自動化） | [assets/exemplars/ai-agent.html](assets/exemplars/ai-agent.html) |
| 部品単体・状態の作り方 | [assets/exemplars/components.html](assets/exemplars/components.html) |

**AI を使う機能は、先に [references/ai-ui-patterns.md](references/ai-ui-patterns.md) の判断表で型を決める。** チャット欄は最後の選択肢（条件を選ばせる・その場で補助する・裏で動かす、のほうが合うことが多い）。

### Step 2: 土台を決める

- **既存プロジェクトにトークンや部品があれば、それを優先する。** 対応付けは [references/framework-mapping.md](references/framework-mapping.md)（素の CSS / CSS Modules / Tailwind v4・v3 / shadcn/ui）
- 無ければ [assets/tokens.css](assets/tokens.css) と [assets/base.css](assets/base.css) をコピーする
- ブランド色は `--brand-h`（色相）と `--brand-c`（彩度 0.17 以下）だけ変えるか、`data-accent` のプリセット（teal / green / orange / rose / violet / graphite）を使う
- 色を変えたら検証する：`python scripts/ux_audit.py tokens --file <tokens.css>`

### Step 3: 写して組む

お手本の構成（セクションの順番、グリッド、余白、部品）をそのまま写し、文言とデータを実際のものに差し替える。値は [references/design-rules.md](references/design-rules.md) にあるものだけを使う。迷う判断（ボタンの置き場所、表かカードか、ダイアログかトーストか）は pattern-catalog.md の判断表で決める。

### Step 4: 状態を作る

データがある画面と同じだけ、空（0 件）・読み込み中・エラー・長いデータの見た目を作る（components.html に全部ある）。

### Step 5: 撮って、見て、直す（必須）

```bash
node scripts/capture.mjs <ページの HTML か URL> --theme both
node scripts/capture.mjs <ページ> --viewports desktop --full
```

撮った画像を開き、[references/visual-review.md](references/visual-review.md) の順に確認する。崩れ・詰まり・重なりを見つけたら直して撮り直す。**最低 1 回は「直して撮り直す」を回す。** 仕上げに [references/anti-patterns.md](references/anti-patterns.md) の「AI が作った感じ」の症状が残っていないか見る。

### Step 6: 機械検査

```bash
python scripts/ux_audit.py audit --path <実装ディレクトリ> --fail-on error
```

`error` は必ず直す。`warning` は直すか、見送る理由を報告に書く。

### Step 7: 報告

ベースにしたお手本、お手本から変えた点、スクリーンショットで見つけて直した点、機械検査の結果、残っている課題を短くまとめる。

## 既存の UI を改善するとき

1. **今の状態を撮る**（desktop / mobile / dark）+ `ux_audit.py audit`
2. visual-review.md と anti-patterns.md で問題を挙げ、影響の大きい順に 3〜5 件を提案する。根拠は撮った画像の該当箇所で示す
3. ユーザーが選んだものを直し、**同じ条件で撮り直して前後を比べる**
4. 既存のトークン・部品・技術スタックを尊重する。全面的な作り直しは提案に留める

## ユーザーの前提が根拠と食い違うとき

指示の前提が [references/evidence-ledger.md](references/evidence-ledger.md) と食い違う場合は、黙って従わず、黙って無視もしない。

1. 指示の意図（実現したい体験）をくむ
2. 前提の食い違いを 1〜2 文で伝え、出典を示す
3. 意図を満たす、より確かな代わりの案を出す
4. それでも同じ指示なら、その判断に従って作る

例：「全ページにスムーズスクロールを入れて」→ 意図（気持ちよく移動したい）は妥当。ただし前庭障害のある人には負担になりうるので、`prefers-reduced-motion` で止める形で入れ、理由を伝える。

## 守ること

- 値はトークンから取る。生の色コード・中間の文字サイズ・4px 刻み以外の余白を足さない
- 1 画面に主ボタンは 1 つ。アクセント色は面積 1 割以下
- 本文は左揃え・40rem まで・行間 1.7 以上。日本語の見出しは `word-break: auto-phrase`
- 状態は色だけで伝えない（文言かアイコンを添える）
- 動かすのは `transform` と `opacity` だけ。`prefers-reduced-motion` を尊重する
- フォーカスリングを消さない。アイコンだけのボタンには `aria-label`
- 絵文字をアイコンに使わない。ストック画像やぼかし円で埋めず、製品の画面や実データを見せる
- 根拠のない通説（「44px 必須」「APCA が新基準」など）を言わない。根拠は [references/evidence-ledger.md](references/evidence-ledger.md)

## ファイル

| パス | 内容 |
|---|---|
| [assets/tokens.css](assets/tokens.css) | 色（ライト/ダーク、OKLCH）、文字、余白、角丸、影、動き、レイアウトのトークン |
| [assets/base.css](assets/base.css) | リセット、文字組み、レイアウト、ボタン・入力・カード・バッジ・表・タブ・ダイアログなどの部品 |
| [assets/exemplars/](assets/exemplars/index.html) | 完成画面のお手本 8 つと一覧（`?theme=dark`、`?accent=teal` で切り替えて見られる。AI の 2 つは `?state=` で状態も切り替わる） |
| [references/design-rules.md](references/design-rules.md) | 数値ルール（色・文字・余白・角丸・影・レイアウト・動き・状態・文言） |
| [references/pattern-catalog.md](references/pattern-catalog.md) | 画面ごとの構成と、迷ったときの判断表 |
| [references/anti-patterns.md](references/anti-patterns.md) | 「AI が作った感じ」の症状と直し方 |
| [references/ai-ui-patterns.md](references/ai-ui-patterns.md) | AI 機能の画面の 7 つの型、選び方、作る状態、Microsoft HAX の 18 指針 |
| [references/visual-review.md](references/visual-review.md) | スクリーンショットの撮り方と見る順番 |
| [references/framework-mapping.md](references/framework-mapping.md) | React / Tailwind / shadcn/ui への持ち込み方 |
| [references/color-science.md](references/color-science.md) | コントラストと OKLCH の根拠 |
| [references/navigation-and-scroll-design.md](references/navigation-and-scroll-design.md) | スティッキーヘッダー、ページ内リンク、戻る導線、スクロールスパイ |
| [references/evidence-ledger.md](references/evidence-ledger.md) | 原則の出典と確度、よくある誤った通説 |
| [scripts/capture.mjs](scripts/capture.mjs) | スクリーンショット撮影（desktop / laptop / tablet / mobile、light / dark、ページ全体） |
| [scripts/ux_audit.py](scripts/ux_audit.py) | 機械検査：`audit`（ソース一式）、`tokens`（トークンファイル）、`palette` / `contrast`（色の組） |

## 実行環境

- `capture.mjs`：Node.js 22 以上と、Chrome / Edge / Chromium のどれか。npm パッケージは不要。見つからなければ `--browser <パス>` を指定する
- `ux_audit.py`：Python 3.9 以上、標準ライブラリのみ
- どちらも勝手にインストールしない。無い場合は「インストールする」「今回は目視・手動で確認する」をユーザーに選んでもらう
- スクリプトのパスは、スキルを置いた場所に合わせて読み替える（例：`.agents/skills/elite-ux-architect/scripts/capture.mjs`）

## スクリプトを更新すべきとき

- 同じ見落とし（崩れ・コントラスト不足など）を 2 回以上スクリーンショットで見つけた → `ux_audit.py audit` に検査を足す
- 新しい画面の種類を 2 回以上作った → `assets/exemplars/` にお手本を足し、pattern-catalog.md に行を足す
- トークンを変えた → `ux_audit.py tokens` の全プリセットと色相 0〜360 で検証する
