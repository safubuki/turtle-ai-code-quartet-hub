# 既存プロジェクトへの持ち込み方

お手本は素の HTML/CSS だが、ルールと値はそのまま React・Tailwind に移せる。基本方針：

1. **プロジェクトに既存のトークン（CSS 変数・Tailwind の theme）があれば、それを優先する。** お手本は構成・比率・状態の参考にし、値は既存の名前に対応付ける
2. 無ければ `assets/tokens.css` をプロジェクトにコピーし、色以外はそのまま使う。ブランド色は `--brand-h` / `--brand-c` だけ変える
3. 持ち込んだあとは `python scripts/ux_audit.py tokens --file <コピー先>` と、スクリーンショット確認をする

## 素の CSS / CSS Modules

`tokens.css` と `base.css` をグローバルに読み込み、部品はクラス名をそのまま使う。CSS Modules ではページ固有の部分だけを書き、値は必ず `var(--…)` で参照する。

```tsx
// main.tsx
import './styles/tokens.css';
import './styles/base.css';
```

```tsx
// Button.tsx — base.css のクラスに型を付けるだけ
type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';
type Size = 'sm' | 'md' | 'lg';

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  loading?: boolean;
}

export function Button({ variant = 'secondary', size = 'md', loading = false, className, children, ...rest }: ButtonProps) {
  const classes = ['btn', `btn--${variant}`, size !== 'md' && `btn--${size}`, className].filter(Boolean).join(' ');
  return (
    <button className={classes} aria-busy={loading || undefined} {...rest}>
      {loading && <span className="spinner" aria-hidden="true" />}
      {children}
    </button>
  );
}
```

## Tailwind CSS v4

`tokens.css` を読み込み、**色は役割名で `@theme inline` に登録**する。これで `bg-surface`、`text-fg-muted`、`border-input` のようなクラスが使え、ライト/ダークは `tokens.css` 側の `light-dark()` で自動的に切り替わる。

```css
/* src/index.css */
@import "tailwindcss";
@import "./styles/tokens.css";

@theme inline {
  --color-bg: var(--bg);
  --color-surface: var(--surface);
  --color-surface-2: var(--surface-2);
  --color-surface-3: var(--surface-3);
  --color-line: var(--border);
  --color-line-strong: var(--border-strong);
  --color-input: var(--border-input);
  --color-fg: var(--text);
  --color-fg-muted: var(--text-muted);
  --color-fg-faint: var(--text-faint);
  --color-accent: var(--accent);
  --color-accent-hover: var(--accent-hover);
  --color-on-accent: var(--on-accent);
  --color-accent-soft: var(--accent-soft);
  --color-accent-fg: var(--accent-text);
  --color-success: var(--success);
  --color-success-fg: var(--success-text);
  --color-success-soft: var(--success-soft);
  --color-warning: var(--warning);
  --color-warning-fg: var(--warning-text);
  --color-warning-soft: var(--warning-soft);
  --color-danger: var(--danger);
  --color-danger-fg: var(--danger-text);
  --color-danger-soft: var(--danger-soft);
}

@theme {
  /* 色以外は tokens.css と同じ値。rounded-md / text-base などが一致する */
  --radius-sm: 6px;
  --radius-md: 8px;
  --radius-lg: 12px;
  --radius-xl: 16px;
  --radius-2xl: 24px;
  --text-base--line-height: 1.7; /* 日本語の本文 */
}
```

- 余白は Tailwind 既定の `--spacing: 0.25rem`（4px 刻み）がそのまま `--space-*` と一致する（`p-4` = 16px = `--space-4`）
- 文字サイズのクラス名（`text-xs`〜`text-5xl`）と値も一致する。中間値（`text-[15px]` など）は使わない
- 色の名前は Tailwind の `text-*`（文字サイズ）と衝突しないよう `fg` を使う
- 反映されたかは、ビルドして生成クラスを確認し、スクリーンショットで見る

```tsx
// Tailwind 版の Button
const variants = {
  primary: 'bg-accent text-on-accent hover:bg-accent-hover',
  secondary: 'bg-surface text-fg border border-line-strong shadow-xs hover:bg-surface-2',
  ghost: 'text-fg hover:bg-surface-3',
  danger: 'bg-danger text-white hover:brightness-95',
} as const;
const sizes = { sm: 'h-8 px-3 text-sm', md: 'h-10 px-4 text-sm', lg: 'h-12 px-6 text-base' } as const;

export function Button({ variant = 'secondary', size = 'md', className = '', ...rest }: ButtonProps) {
  return (
    <button
      className={`inline-flex items-center justify-center gap-2 rounded-md font-semibold transition-colors
        focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent
        disabled:opacity-50 pointer-coarse:min-h-11 ${variants[variant]} ${sizes[size]} ${className}`}
      {...rest}
    />
  );
}
```

## Tailwind CSS v3

`tailwind.config.js` の `theme.extend` で CSS 変数を参照する（v3 では `bg-accent/50` のような不透明度指定は効かない）。

```js
// tailwind.config.js
export default {
  theme: {
    extend: {
      colors: {
        bg: 'var(--bg)', surface: { DEFAULT: 'var(--surface)', 2: 'var(--surface-2)', 3: 'var(--surface-3)' },
        line: { DEFAULT: 'var(--border)', strong: 'var(--border-strong)' }, input: 'var(--border-input)',
        fg: { DEFAULT: 'var(--text)', muted: 'var(--text-muted)', faint: 'var(--text-faint)' },
        accent: { DEFAULT: 'var(--accent)', hover: 'var(--accent-hover)', soft: 'var(--accent-soft)', fg: 'var(--accent-text)' },
        'on-accent': 'var(--on-accent)',
        danger: { DEFAULT: 'var(--danger)', fg: 'var(--danger-text)', soft: 'var(--danger-soft)' },
      },
      borderRadius: { sm: '6px', md: '8px', lg: '12px', xl: '16px', '2xl': '24px' },
      fontSize: { base: ['1rem', { lineHeight: '1.7' }] },
    },
  },
};
```

## shadcn/ui を使っているプロジェクト

shadcn/ui は独自の変数名を使う。**`--accent` の意味が違う**（shadcn では hover の背景、こちらではブランド色）ので、`tokens.css` をそのまま足すと衝突する。shadcn の変数へ値を写す：

| shadcn/ui | 写す値 |
|---|---|
| `--background` / `--foreground` | `--bg` / `--text` |
| `--card` / `--popover` | `--surface` |
| `--primary` / `--primary-foreground` | ブランド色（`--accent`）/ `--on-accent` |
| `--secondary` / `--accent`（hover 背景） | `--surface-3` |
| `--muted` / `--muted-foreground` | `--surface-2` / `--text-muted` |
| `--destructive` | `--danger` |
| `--border` / `--input` / `--ring` | `--border` / `--border-input` / `--focus-ring` |
| `--radius` | `8px` |

このときは `tokens.css` の変数名に接頭辞を付けて（`--ux-accent` など）から写すか、値だけを shadcn の変数に直接書く。

## 既存のデザインシステムがある場合

- 既存のトークン名を残し、`design-rules.md` の**数値ルール**（余白の段・文字の段・角丸・影の段）に合わない値だけを提案する
- 既存の色は `ux_audit.py palette --surfaces … --foregrounds …` でコントラストを検証してから使う
- 部品は既存のものを優先し、足りない状態（空・読み込み・エラー）を `components.html` から補う
