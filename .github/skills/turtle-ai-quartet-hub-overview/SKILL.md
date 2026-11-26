---
name: turtle-ai-quartet-hub-overview
description: Turtle AI Code Quartet Hub（4 つの VS Code ウィンドウを WPF パネルで管理する Windows ツール）のプロジェクト概要・実装パターン・既知課題・QA 方針を参照する overview スキル。Quartet Hub のコードを修正・機能追加する前に、関係する節だけを読んでデグレを防ぐ。「Quartet Hub の概要」「このリポジトリを把握」「既知課題」「実装パターン」「回帰防止」などで発火。
---

# Turtle AI Quartet Hub Overview

## スキル読み込み通知

読み込んだら 1 行だけ通知する：

> 💡 **Turtle AI Quartet Hub Overview を読み込みました** — 関係する節だけ参照して安全に作業します。

## When to Use

- このリポジトリの全体像を把握したいとき
- バグ修正や機能追加の前に、既存の実装パターンと注意点を確認したいとき
- VS Code ウィンドウ制御、保存復元、AI 作業通知（フックのみ・推定はしない）の責務分担を知りたいとき
- 既知課題と現在の QA 方針を確認しながら、デグレを避けて対応したいとき

## 概要

Turtle AI Code Quartet Hub は、4 つの VS Code ウィンドウを WPF パネルで管理し、2x2 配置、集中表示、裏保存を行う Windows 向けツールです。画面やログからの AI 状態推定と VS Code 外周フレームは削除済みで、AI の作業中・完了は各 AI ツールのフックからの明示的な通知だけで表示します。
この overview スキルは、変更前に読むべきプロジェクト詳細、壊しやすい実装パターン、現在進行中の課題と QA 方針を一か所に集約します。

## 手順

### Step 1: 関係する節だけを読む

リファレンスは合計で数百 KB あり、追記で増え続けるので**丸ごと読まない**。見出しの一覧を先に見て、依頼に関係する節だけを読む。

```bash
grep -n "^## " references/implementation-patterns.md references/current-issues-qa.md
```

| ファイル | 大きさ | 読み方 |
|---|---|---|
| `references/project-details.md` | 数十 KB | 構成・責務分担を知りたいときに該当節を読む |
| `references/implementation-patterns.md` | いちばん大きい（節が多い） | 見出しから変更対象の責務に当たる節だけ読む |
| `references/current-issues-qa.md` | 数十 KB | 既知課題・QA 方針の該当節だけ読む |

### Step 2: 依頼内容を関連箇所へ結び付ける

依頼が来たら、次を明確にすること。

1. どの責務に属する変更か
2. どのファイル群が直接その責務を担っているか
3. 既知課題や既存ワークアラウンドと競合しないか
4. 変更後に最低限どの確認を行うべきか

### Step 3: 対応後に overview を更新する

新しい実装パターン、注意点、QA の合意事項、既知課題の解像度が増えたら、対応完了時に参照ドキュメントの該当する節へ追記する。積み上がった記録は、過去にやったことを繰り返さないための資産なので、既存の記録は削除・要約しない。似た節があれば新しい節を立てずにその節へ追記し、あとから見出しで探せるようにする。

## 参照ドキュメント

- [references/project-details.md](references/project-details.md) — ワークスペース構成、主要プロジェクト、責務分担、実行コマンド
- [references/implementation-patterns.md](references/implementation-patterns.md) — 壊しやすい実装パターン、ワークアラウンド、横断的注意点
- [references/current-issues-qa.md](references/current-issues-qa.md) — 現在の既知課題、確認済み QA、今後の対応方針
