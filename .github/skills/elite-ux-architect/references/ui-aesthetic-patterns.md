# UIデザインパターン集 — 至高の美的デザイン

## 1. カラーシステム

### ダークモード対応パレット設計

```
プライマリ:   Tailwind blue-500 / blue-400 (dark)
セカンダリ:   Tailwind slate-600 / slate-400 (dark)
アクセント:   Tailwind amber-500 / amber-400 (dark)
サクセス:     Tailwind emerald-500 / emerald-400 (dark)
ワーニング:   Tailwind amber-500 / amber-400 (dark)
エラー:       Tailwind red-500 / red-400 (dark)

背景:         white / slate-900
サーフェス:   slate-50 / slate-800
ボーダー:     slate-200 / slate-700
テキスト:     slate-900 / slate-100
```

### 色の使い方ルール

| 用途 | 使い方 |
|------|--------|
| プライマリアクション | プライマリカラーのみで表現。画面に1〜2箇所が理想 |
| セカンダリアクション | ボーダーのみ or ゴーストスタイル |
| 破壊的アクション | 赤系。確認ダイアログ必須 |
| 無効状態 | 彩度を落とし、opacity: 0.5 |
| ホバー | 明度を1段階変化（例: blue-500 → blue-600） |

---

## 2. タイポグラフィシステム

### スケール（Major Third: 1.25 ratio）

```
xs:    12px / 0.75rem — キャプション、メタ情報
sm:    14px / 0.875rem — 補助テキスト、ラベル
base:  16px / 1rem — 本文
lg:    18px / 1.125rem — サブヘッド
xl:    20px / 1.25rem — セクションタイトル
2xl:   24px / 1.5rem — ページタイトル
3xl:   30px / 1.875rem — ヒーローテキスト
4xl:   36px / 2.25rem — 大見出し
```

### Font Weight の使い分け

| Weight | 用途 |
|--------|------|
| 400 (normal) | 本文・説明文 |
| 500 (medium) | ラベル・ナビゲーション |
| 600 (semibold) | サブヘッド・強調テキスト |
| 700 (bold) | タイトル・見出し |

---

## 3. シャドウとエレベーション

### エレベーションシステム

| レベル | Shadow | 用途 |
|--------|--------|------|
| 0 | none | 埋め込みコンテンツ |
| 1 | `shadow-sm` | カード・入力フィールド |
| 2 | `shadow-md` | ドロップダウン・ポップオーバー |
| 3 | `shadow-lg` | モーダル・ダイアログ |
| 4 | `shadow-xl` | フローティングアクションボタン |
| 5 | `shadow-2xl` | 最前面のオーバーレイ |

### ダークモードのシャドウ

ダークモードではシャドウの視認性が低下するため、代わりに**ボーダー + 背景色の差分**でエレベーションを表現する。

```tsx
// ✅ ダークモード対応のカード
<div className="bg-white dark:bg-slate-800 shadow-md dark:shadow-none dark:border dark:border-slate-700 rounded-lg">
```

---

## 4. アニメーション & トランジション

### 基本ルール

| 種類 | Duration | Easing | 用途 |
|------|----------|--------|------|
| マイクロ | 100-150ms | ease-out | ホバー・フォーカス・トグル |
| 標準 | 200-300ms | ease-in-out | パネル展開・アコーディオン |
| ページ | 300-500ms | ease-out | ページ遷移・モーダル表示 |
| 強調 | 500-800ms | cubic-bezier(0.34, 1.56, 0.64, 1) | 成功演出・バウンス |

### アニメーション可能なプロパティ（パフォーマンス安全）

```css
/* ✅ GPU アクセラレーションされるプロパティ */
transform: translateX(), translateY(), scale(), rotate();
opacity: 0 → 1;

/* ❌ レイアウト再計算が発生するプロパティ */
width, height, top, left, margin, padding;
```

### 実装パターン

```tsx
// フェードイン + スライドアップ
const fadeInUp = "transition-all duration-300 ease-out";
const hidden = "opacity-0 translate-y-2";
const visible = "opacity-100 translate-y-0";

// スケールバウンス（ボタン押下）
const buttonPress = "active:scale-95 transition-transform duration-100";

// スムーズなアコーディオン（grid-rows trick）
const accordion = "grid transition-[grid-template-rows] duration-300";
const collapsed = "grid-rows-[0fr]";
const expanded = "grid-rows-[1fr]";
```

### prefers-reduced-motion 対応

```tsx
// ✅ 必ず対応する
<div className="motion-safe:transition-all motion-safe:duration-300 motion-reduce:transition-none">
```

---

## 5. コンポーネントパターン

### ボタン

```
┌─────────────────────────────────────────────────────────┐
│ Primary   │ Secondary  │ Ghost     │ Danger    │ Icon    │
│ [塗り]    │ [枠線]     │ [透明]    │ [赤塗り]  │ [丸]    │
│ bg-blue   │ border     │ hover:bg  │ bg-red    │ p-2     │
│ text-white│ text-blue  │ text-gray │ text-white│ rounded │
└─────────────────────────────────────────────────────────┘

サイズ:
  sm: px-3 py-1.5 text-sm
  md: px-4 py-2 text-base
  lg: px-6 py-3 text-lg
```

### カード

```
┌────────────────────────┐
│  ┌──────────────────┐  │ ← padding: 16-24px
│  │  Header          │  │ ← font-weight: 600
│  │  ─────────────── │  │ ← divider (optional)
│  │  Content         │  │ ← font-weight: 400
│  │                  │  │
│  │  [Action] [Sec]  │  │ ← footer actions
│  └──────────────────┘  │
└────────────────────────┘

rounded-lg + shadow-sm + border
hover: shadow-md (if clickable)
```

### モーダル

```
┌──────────── Overlay (bg-black/50) ─────────────┐
│                                                  │
│    ┌──────────────────────────────────┐         │
│    │  ✕                     Title    │         │ ← Header
│    │  ──────────────────────────────  │         │
│    │                                  │         │
│    │  Content                        │         │ ← Body (scrollable)
│    │                                  │         │
│    │  ──────────────────────────────  │         │
│    │            [Cancel] [Confirm]   │         │ ← Footer
│    └──────────────────────────────────┘         │
│                                                  │
└──────────────────────────────────────────────────┘

アニメーション: overlay fadeIn + modal scaleIn (0.95→1.0 + fadeIn)
閉じ方: ✕ボタン / オーバーレイクリック / Escape
フォーカストラップ: 必須
```

### トースト通知

```
┌────────────────────────────────────┐
│  ●  メッセージテキスト          ✕  │
│     サブテキスト（オプション）      │
└────────────────────────────────────┘

位置: 右上 or 上部中央
アニメーション: slideInRight + fadeIn → autoHide (3-5秒) → slideOutRight + fadeOut
種類: success(緑) / error(赤) / warning(黄) / info(青)
スタック: 複数表示可能、上から順に積む
```

---

## 6. レスポンシブパターン

### コンテナ幅

```
モバイル:  100% (padding: 16px)
タブレット: max-w-2xl (672px)
デスクトップ: max-w-4xl (896px) or max-w-6xl (1152px)
```

### レイアウト切り替え

```
モバイル:     1カラム（縦積み）
タブレット:   2カラム or サイドバー collapse
デスクトップ: 3カラム or フルサイドバー
```

### ナビゲーション変換

```
デスクトップ: 水平ナビバー
タブレット:   コンパクトナビ + アイコン
モバイル:     ハンバーガーメニュー or ボトムナビゲーション
```

---

## 7. 状態表現パターン

### ローディング

| パターン | 用途 |
|---------|------|
| スケルトンスクリーン | コンテンツの初回読み込み |
| インラインスピナー | ボタン内・小要素の読み込み |
| プログレスバー | 進捗が分かる処理 |
| パルスアニメーション | プレースホルダー要素 |

### 空状態（Empty State）

```
┌────────────────────────────────────┐
│                                    │
│            🎬                      │
│                                    │
│    まだクリップがありません          │
│    「追加」ボタンから始めましょう    │
│                                    │
│         [+ クリップを追加]          │
│                                    │
└────────────────────────────────────┘
```

- イラスト or アイコン + メッセージ + アクションボタン
- 前向きで励ますトーン

### 無効状態

- `opacity-50` + `cursor-not-allowed` + `pointer-events-none`
- ツールチップで理由を説明する

---

## 8. Glassmorphism / Frosted Glass（モダンパターン）

```tsx
// ✅ 控えめに使うと高級感が出る
<div className="bg-white/80 dark:bg-slate-900/80 backdrop-blur-xl border border-white/20 rounded-2xl shadow-xl">
```

**使用ルール**:
- ヘッダー・ツールバーなど固定要素に限定する
- 背景にコンテンツがある（ぼかし効果が意味を持つ）場合のみ
- モバイルではパフォーマンスに注意（`backdrop-blur` は重い）

---

## 9. ダークモード設計原則

| 原則 | 説明 |
|------|------|
| 純黒を避ける | `#000000` ではなく `slate-900` (`#0f172a`) を背景にする |
| エレベーションで明度を上げる | 上のレイヤーほど明るい背景色にする |
| 彩度を調整する | ダークモードでは彩度をやや下げる（目の疲れ軽減） |
| シャドウよりボーダー | 暗い背景ではシャドウが見えにくいためボーダーで区切る |
| アクセントカラーを際立たせる | 暗い背景でアクセントカラーが映える配色にする |
