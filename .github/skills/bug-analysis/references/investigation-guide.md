# 本プロジェクト固有の調査リファレンス

## よくある不具合の原因パターン

| カテゴリ | よくある原因 | 調査対象 |
|---------|-------------|---------|
| 再生関連 | AudioContext の状態管理 | `usePlayback.ts`, `useAudioContext.ts` |
| メディア | URL.revokeObjectURL の不適切な呼び出し | `useMediaItems.ts`, `mediaStore.ts` |
| 非同期 | 再生/停止の競合状態 | `usePlayback.ts` |
| ストア | Zustand の状態更新タイミング | `src/stores/` 配下 |
| エクスポート | MediaRecorder / Canvas の問題 | `useExport.ts`, `canvas.ts` |
| AI機能 | API呼び出しエラー、レスポンス解析 | `useAiNarration.ts` |
| 保存/読込 | IndexedDB の操作エラー | `indexedDB.ts`, `projectStore.ts` |
| UI | スワイプ誤操作、モーダル制御 | `SwipeProtectedSlider.tsx`, `uiStore.ts` |

## 深刻度の判断基準

| 深刻度 | 基準 |
|--------|------|
| 🔴 Critical | アプリが使用不能、データ損失、セキュリティリスク |
| 🟠 Major | 主要機能が動作しない、ユーザー体験に重大な影響 |
| 🟡 Minor | 軽微なUI崩れ、エッジケースの問題、改善レベル |

## ログの活用

本プロジェクトには `logStore` によるログ機能があります。
設定モーダル → ログタブ からエラー・警告・情報ログを確認できます。

## 過去の不具合修正の実例

- [preview-blackout-fix-v1.4.0.md](../../../../Docs/preview-blackout-fix-v1.4.0.md): プレビュー黒画面修正
- [preview-pause-fix-v1.4.1.md](../../../../Docs/preview-pause-fix-v1.4.1.md): プレビュー一時停止修正
