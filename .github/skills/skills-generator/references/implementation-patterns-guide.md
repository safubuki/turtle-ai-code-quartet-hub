# 実装パターン・注意点リファレンス作成ガイド

プロジェクトオーバービュースキルに含める **実装パターン・注意点リファレンス** の調査方法・作成手順・出力フォーマットをまとめたガイドです。

---

## 目的

プロジェクトには「なぜこのコードがこうなっているのか」が分からないと壊してしまうパターンが多数存在します。
それらを `references/implementation-patterns.md` として体系的にドキュメント化し、AI がコード変更時に既存パターンを壊さず、設計思想に沿った実装を行えるようにします。

---

## Step 1: コードベースの調査

以下の **10カテゴリ** を調査軸として、プロジェクトのコードを網羅的に検索します。
すべてのカテゴリが当てはまるわけではありません。プロジェクトに該当するものだけを採用してください。

### 調査カテゴリ一覧

| # | カテゴリ | 検索キーワード・パターン | 調査対象 |
|---|---------|----------------------|---------|
| 1 | **誤操作防止** | `touchstart`, `touchmove`, `preventDefault`, `overflow: hidden`, `position: fixed`, `scrollTo`, `swipe`, `slider`, `drag` | hooks/, components/ |
| 2 | **表示復帰・ブラックアウト対策** | `visibilitychange`, `visibilityState`, `requestAnimationFrame`, `display: none`, `opacity`, `load()`, Canvas再描画 | components/, hooks/ |
| 3 | **ブラウザAPI制約・ワークアラウンド** | `AudioContext`, `webkitAudioContext`, `resume()`, `suspended`, `playsInline`, `autoplay`, `createMediaElementSource` | hooks/, utils/ |
| 4 | **メモリ管理** | `revokeObjectURL`, `createObjectURL`, `AbortController`, `close()`, `cancel()`, `useEffect return`, `try-catch` クリーンアップ | utils/, stores/, hooks/ |
| 5 | **エラーハンドリング** | `ErrorBoundary`, `getDerivedStateFromError`, `componentDidCatch`, `window.addEventListener('error'`, `unhandledrejection`, `.catch` | components/, main.tsx |
| 6 | **パフォーマンス最適化** | `React.memo`, `useMemo`, `useCallback`, `IntersectionObserver`, `debounce`, `throttle`, `requestAnimationFrame`, FPS制限 | components/, hooks/ |
| 7 | **モバイル・レスポンシブ** | `orientation`, `lock`, `playsInline`, `UserAgent`, `touch`, `viewport`, `@media` | hooks/, components/ |
| 8 | **データ永続化** | `IndexedDB`, `localStorage`, `sessionStorage`, `ArrayBuffer`, `serialize`, `beforeunload`, `auto-save` | utils/, stores/, hooks/ |
| 9 | **メディア・動画処理** | `MediaRecorder`, `VideoEncoder`, `AudioEncoder`, `captureStream`, `VideoFrame`, `canvas`, `drawImage`, `readyState` | hooks/, utils/ |
| 10 | **状態管理** | `create(`, `devtools`, `getState`, `useRef`, `useState`, `restoreFromSave`, ストア間の `import` | stores/, hooks/, components/ |

### 調査方法

1. 各カテゴリのキーワードで `grep` / `semantic_search` を実行
2. 見つかったパターンについて、**何の問題を解決しているか** を特定
3. 関連ファイルの実装を読み、**具体的なテクニック** と **注意点** を記録
4. プロジェクトに該当しないカテゴリはスキップ

---

## Step 2: リファレンスの作成

### 出力フォーマット

各パターンを以下のフォーマットで記述します：

```markdown
## N. カテゴリ名

### N-1. パターン名

- **ファイル**: `src/path/to/file.ts`
- **問題**: [このパターンが解決する問題を1-2文で]
- **対策**:
  - [具体的なテクニック1]
  - [具体的なテクニック2]
  - [具体的なテクニック3]
- **注意**: [変更時に壊しやすいポイント、やってはいけないこと]
```

### 重要な記述ルール

1. **「なぜ」を必ず書く**: コードの「何」だけでなく「なぜそうなっているか」を説明する
   - 悪い例: `opacity: 0.001` で隠している
   - 良い例: `display: none` にするとブラウザがビデオのデコードを停止するため、`opacity: 0.001` で視覚的に隠しつつレンダリング対象として維持

2. **禁止事項を明記する**: やってはいけない操作を明示する
   - 例: 「`display: none` や `visibility: hidden` は使わない」
   - 例: 「`createMediaElementSource()` を同じ要素に2回呼ばない」

3. **ファイルパスを必ず記載**: どのファイルに実装されているかを明記する

4. **閾値・定数を記載**: マジックナンバーの意味と値を記録する
   - 例: `minMovement=15px`, `minTouchDuration=200ms`, `DUPLICATE_SUPPRESS_MS=10000`

---

## Step 3: 末尾に横断サマリーを追加

リファレンスの末尾に、全カテゴリの注意点を一覧にまとめた **横断的な注意点まとめ** を追加します：

```markdown
## 横断的な注意点まとめ

| カテゴリ | 注意点 |
|---------|--------|
| **AudioContext** | `suspended` → `resume()` はユーザージェスチャーが必要。必ず `catch` する |
| **ObjectURL** | 作成したら必ず `revokeObjectURL` で解放。特にリストア時の古い URL に注意 |
| ... | ... |
```

この表は **AI が変更前のクイックチェックリスト** として使えるようにします。

---

## 典型的なカテゴリ別の調査パターン

### Web アプリ共通（ほぼ全てのプロジェクトに該当）

- エラーハンドリング戦略
- パフォーマンス最適化
- 状態管理パターン
- データ永続化

### メディア系アプリ（動画・音声を扱う場合）

- AudioContext / Web Audio API の制約
- Canvas 描画パイプライン
- ブラックアウト対策（`visibilitychange`）
- メモリ管理（ObjectURL, VideoFrame）
- エクスポート時のストリーム管理

### モバイル対応アプリ

- 誤操作防止（スワイプ保護、デバウンス）
- 画面向き固定
- `playsInline` / Autoplay Policy
- ボディスクロールロック

### データ駆動アプリ

- シリアライズ / デシリアライズ
- 変更検知・自動保存
- 大容量データの容量管理
- ページ離脱防止

---

## 出力の完全な例

以下は、ブラウザベースの動画編集アプリの場合の出力例です：

```markdown
# [プロジェクト名] 実装パターン・注意点リファレンス

本プロジェクトに組み込まれている実装パターン・ワークアラウンド・注意すべきポイントを
網羅的にまとめたドキュメントです。新機能の追加や既存コードの変更時に必ず確認してください。

---

## 1. スクロール/スワイプ誤操作防止

### 1-1. モーダル表示時のボディスクロールロック

- **ファイル**: `src/hooks/useDisableBodyScroll.ts`
- **問題**: モーダル表示中に背景がスクロールする（特にモバイル）
- **対策**:
  - `body.style.overflow = 'hidden'` + `position: fixed` + `top: -scrollY`
  - クリーンアップ時に `window.scrollTo({ behavior: 'instant' })` で元の位置に復帰
- **注意**: `position: fixed` にすると元のスクロール位置がリセットされるため、
  `top: -scrollY` で視覚的な位置を保持する必要がある

### 1-2. スライダーのスワイプ保護（モバイル誤操作防止）

- **ファイル**: `src/hooks/useSwipeProtectedValue.ts`, `src/components/SwipeProtectedSlider.tsx`
- **問題**: モバイルで縦スクロール中にスライダーに指が触れて値が変わる
- **対策**:
  - `onTouchStart` → `onTouchMove` で `deltaX` vs `deltaY` を比較し方向を判定
  - 縦移動 > 横移動 → 縦スクロールと判断しスライダーの値をリセット
  - 80ms 未満のタッチは「通りすがりタップ」としてリセット
- **注意**: 方向判定は一度決めたら変更されない（`directionDecidedRef`）。
  閾値は `minMovement=15px`, `minTouchDuration=200ms`

---

## 2. ブラックアウト対策・表示復帰

### 2-1. タブ復帰時の Canvas 自動リフレッシュ

- **ファイル**: `src/components/TurtleVideo.tsx`
- **問題**: タブ切り替え後に Canvas が黒画面のまま
- **対策**:
  - `visibilitychange` イベントで Page Visibility API を監視
  - `document.visibilityState === 'visible'` で `requestAnimationFrame(() => renderFrame(...))` を実行
  - `readyState < 2` のメディア要素には `element.load()` で再読み込み
- **注意**: `visibilitychange` リスナーのクリーンアップを必ず行う

### 2-2. メディアリソースの可視配置（display: none 回避）

- **ファイル**: `src/components/media/MediaResourceLoader.tsx`
- **問題**: `display: none` にするとブラウザがビデオのデコードを停止する
- **対策**: `opacity: 0.001`, `position: fixed`, `zIndex: -100` で視覚的に隠しつつ、
  ブラウザにはレンダリング対象として認識させる
- **禁止**: `display: none` や `visibility: hidden` は使わない

---

## 横断的な注意点まとめ

| カテゴリ | 注意点 |
|---------|--------|
| **スワイプ** | モバイルのスライダーには誤操作防止が必須 |
| **Canvas** | `display: none` の video からは描画不可 |
| **AudioContext** | `resume()` はユーザージェスチャーが必要 |
| ... | ... |
```

> 上記は **抜粋** です。実際のリファレンスでは該当する全カテゴリ・全パターンを網羅してください。

---

## チェックリスト

リファレンス作成後、以下を確認してください：

- [ ] 各パターンに「問題」「対策」「注意」の3要素が記載されている
- [ ] ファイルパスが実在する
- [ ] 「なぜそうなっているか」が説明されている
- [ ] 禁止事項が明記されている
- [ ] 閾値・定数の値と意味が記載されている
- [ ] 末尾に横断サマリーがある
- [ ] プロジェクトに該当しないカテゴリはスキップされている
