# ナビゲーションとスクロール設計

シングルページやランディングページで「リンクを押したらヌルッと移動し、いつでも戻れる」体験を作るための実装パターン集。ここに書かれたパターンは `scripts/ux_audit.py audit` が機械的に検査する。

---

## 1. スムーズスクロールは「無条件で有効にしない」

### 結論

`scroll-behavior: smooth` を `html` に直接書くのは**アクセシビリティ上の問題**になる。必ず `prefers-reduced-motion` でガードする。

### 根拠

前庭障害（vestibular disorder）を持つ人にとって、大きな画面移動アニメーションは吐き気・めまい・頭痛を引き起こす。OS の「視差効果を減らす / アニメーションを減らす」設定は、その申告そのものであり、尊重が必要（WCAG 2.2 SC 2.3.3 Animation from Interactions, AAA）。

### 実装

```css
/* ✅ 正しい: 減モーション希望者には従来どおり即時ジャンプ */
@media (prefers-reduced-motion: no-preference) {
  html {
    scroll-behavior: smooth;
  }
}
```

```css
/* 🚫 誤り: 全ユーザーに強制する */
html { scroll-behavior: smooth; }
```

JavaScript で制御する場合も同じ判断を通す：

```js
const prefersReduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
target.scrollIntoView({
  behavior: prefersReduced ? "auto" : "smooth",
  block: "start",
});
```

> 💡 `scroll-behavior: smooth` はモダンブラウザで広くサポートされている（Chrome 61+, Firefox 36+, Edge 79+, Safari 15.4+）。ライブラリは不要。

---

## 2. スティッキーヘッダーに隠れない着地点（最重要の落とし穴）

### 問題

スティッキーヘッダーがある状態で `#section` へジャンプすると、**見出しがヘッダーの裏に潜り込む**。スムーズスクロールを入れた実装でほぼ必ず発生する。

### 解決

CSS だけで解決する。JavaScript のオフセット計算は不要。

```css
/* 着地点にヘッダー高さぶんの余白を確保する */
:where(h2, h3, section, [id]) {
  scroll-margin-top: calc(var(--header-height) + 0.5rem);
}
```

Tailwind の場合：

```html
<section id="work" class="scroll-mt-20">
```

> `scroll-margin-top` は「その要素へスクロールしたときだけ」効く余白で、通常のレイアウトには影響しない。`padding-top` で代用してはいけない。

---

## 3. スティッキーヘッダーは「小さく」「賢く」

### 根拠と限界

NN/g のスクロール調査では**閲覧時間の約74%が最初の2画面分**に集中する。スティッキーヘッダーはナビゲーションへの復帰導線として有効だが、**常時表示は縦方向の可読領域を奪う**。とくに記事・ブログのような「読むことが主タスク」のページでは、固定ヘッダーは支援より妨害になりやすい。

### 判断基準

| ページ種別 | 推奨 |
|-----------|------|
| LP / ポートフォリオ / 製品ページ（回遊が主タスク） | スティッキー推奨。高さは 56–64px 程度に抑える |
| 記事 / ドキュメント（読むことが主タスク） | 部分スティッキー（上スクロール時のみ出現）を推奨 |
| モバイル全般 | 高さを最小化。あるいは下部ナビ + 上スクロール時出現 |

### 部分スティッキー（Hide-on-scroll-down）

下スクロール時は隠して読書領域を返し、上スクロール時に即座に戻す。「戻りたい」という意図の表明が上スクロールなので、意図に一致する。

```css
.site-header {
  position: sticky;
  top: 0;
  transition: transform 200ms ease;
}
.site-header[data-hidden="true"] {
  transform: translateY(-100%); /* transform のみ = 60fps を維持 */
}
```

```css
@media (prefers-reduced-motion: reduce) {
  .site-header { transition: none; }
}
```

---

## 4. 現在地を示す（スクロールスパイ）

移動した後に「自分がどこにいるか」が分からないと、目次に戻る動機すら失われる。`IntersectionObserver` で現在のセクションをナビゲーションに反映する。スクロールイベントでの実装は毎フレーム発火してジャンクの原因になるため避ける。

```js
const observer = new IntersectionObserver(
  (entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const id = entry.target.id;
      document.querySelectorAll("[data-navlink]").forEach((link) => {
        const active = link.getAttribute("href") === `#${id}`;
        link.classList.toggle("is-active", active);
        // 支援技術にも現在地を伝える
        if (active) link.setAttribute("aria-current", "location");
        else link.removeAttribute("aria-current");
      });
    }
  },
  // 画面の上寄り 1/3 に入った時点で「現在地」とみなす
  { rootMargin: "-20% 0px -70% 0px", threshold: 0 }
);
document.querySelectorAll("section[id]").forEach((el) => observer.observe(el));
```

> 色だけで現在地を示さない。太字・左ボーダー・下線など**形状の差**を併用する（WCAG 2.2 SC 1.4.1 色の使用）。

---

## 5. 常に戻れる導線（Back to Top）

### 出現ルール

- 1画面分（100vh）以上スクロールしたら出現させる。最初から出さない
- 画面右下固定。モバイルではサムゾーン（親指の届く範囲）を意識する
- 最小 44×44px。アイコンのみなら `aria-label` 必須

```html
<button
  type="button"
  class="back-to-top h-11 w-11"
  aria-label="ページ先頭へ戻る"
  onclick="scrollToTop()"
>↑</button>
```

```js
function scrollToTop() {
  const prefersReduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  window.scrollTo({ top: 0, behavior: prefersReduced ? "auto" : "smooth" });
}
```

### フォーカスも一緒に戻す（見落とされがちな点）

スクロールだけ戻してもキーボードフォーカスは元の位置に残る。キーボードユーザーは Tab を押した瞬間にページ下部へ引き戻される。

```js
// ページ先頭の要素へフォーカスも移す
const anchor = document.querySelector("#top-anchor");
anchor.setAttribute("tabindex", "-1");
anchor.focus({ preventScroll: true });
```

---

## 6. スキップリンク

キーボードユーザーが繰り返しナビゲーションを飛ばして本文へ行けるようにする（WCAG 2.2 SC 2.4.1 Bypass Blocks, A）。**フォーカス時のみ可視**にするのが定石で、視覚的なデザインを損なわない。

```css
.skip-to-content {
  position: absolute;
  left: -9999px;
}
.skip-to-content:focus {
  left: 1rem;
  top: 1rem;
  z-index: 100;
}
```

---

## 7. スクロールジャッキングは原則禁止

`scroll-jacking`（ホイール操作を奪って独自のアニメーション量に置き換える手法）は NN/g が明確に否定している。ユーザーの想定量と実際の移動量がズレ、制御感を失わせる。とくに以下を壊す：

- スクロールバーによる位置把握
- 検索（Ctrl+F）での到達
- キーボードの PageDown / Home / End
- ブラウザバックでのスクロール位置復元

> 「ヌルッと動く」の実現は `scroll-behavior: smooth`（＝ブラウザネイティブ）で十分。ホイール量を奪う実装で作らない。

---

## 8. スクロール連動アニメーションの安全な使い方

要素が視界に入ったときのフェードインは有効だが、以下を守る：

- `transform` と `opacity` のみをアニメートする（レイアウトを起こさない）
- 減モーション時は**アニメーションを消し、最終状態で即表示**する。要素を消したままにしない
- 初期状態を `opacity: 0` にする場合、JS が失敗するとコンテンツが永久に見えなくなる。`no-js` フォールバックを用意する

```css
@media (prefers-reduced-motion: no-preference) {
  .reveal {
    opacity: 0;
    transform: translateY(12px);
    transition: opacity 400ms ease-out, transform 400ms ease-out;
  }
  .reveal.is-visible {
    opacity: 1;
    transform: none;
  }
}
/* 減モーション時は最初から見えている状態が既定 */
```

---

## 9. チェックリスト

`scripts/ux_audit.py audit` が自動判定する項目：

- [ ] `scroll-behavior: smooth` が `prefers-reduced-motion` でガードされている
- [ ] スムーズスクロール時に `scroll-margin-top` が設定されている
- [ ] `#anchor` リンクの飛び先 `id` が実在する
- [ ] 長いページにスティッキーヘッダーか Back to Top がある
- [ ] スクロールスパイ（現在地表示）がある
- [ ] スキップリンクがある
- [ ] アニメーションに減モーション対応がある
- [ ] レイアウトを起こすプロパティをアニメートしていない

AI が目視で判断する項目：

- [ ] スティッキーヘッダーの高さがコンテンツを圧迫していないか
- [ ] 現在地表示が色だけに依存していないか
- [ ] Back to Top の出現タイミングが早すぎないか
- [ ] スクロール量とアニメーション時間が体感的に自然か
