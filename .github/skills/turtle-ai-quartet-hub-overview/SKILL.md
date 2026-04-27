---
name: turtle-ai-quartet-hub-overview
description: Turtle AI Code Quartet Hub のプロジェクト概要・実装パターン・既知課題・QA 方針を参照する overview スキル。「overview」「プロジェクト概要」「このリポジトリを把握」「アーキテクチャ」「既知課題」「実装パターン」「回帰防止」「workspace summary」などで発火。
---

# Turtle AI Quartet Hub Overview

## スキル読み込み通知

このスキルが読み込まれたら、必ず以下の通知をユーザーに表示してください：

> 💡 **Turtle AI Quartet Hub Overview スキルを読み込みました**  
> このワークスペースの構成、実装パターン、既知課題、QA 方針を参照して安全に作業します。

## When to Use

- このリポジトリの全体像を把握したいとき
- バグ修正や機能追加の前に、既存の実装パターンと注意点を確認したいとき
- AI 状態検出、VS Code ウィンドウ制御、保存復元まわりの責務分担を知りたいとき
- 既知課題と現在の QA 方針を確認しながら、デグレを避けて対応したいとき

## 概要

Turtle AI Code Quartet Hub は、4 つの VS Code ウィンドウを WPF パネルで管理し、2x2 配置、集中表示、裏保存、AI 状態の可視化を行う Windows 向けツールです。
この overview スキルは、変更前に読むべきプロジェクト詳細、壊しやすい実装パターン、現在進行中の課題と QA 方針を一か所に集約します。

## 手順

### Step 1: まずリファレンスを読む

最初に以下を順番に読むこと。

1. `references/project-details.md`
2. `references/implementation-patterns.md`
3. `references/current-issues-qa.md`

### Step 2: 依頼内容を関連箇所へ結び付ける

依頼が来たら、次を明確にすること。

1. どの責務に属する変更か
2. どのファイル群が直接その責務を担っているか
3. 既知課題や既存ワークアラウンドと競合しないか
4. 変更後に最低限どの確認を行うべきか

### Step 3: 対応後に overview を更新する

新しい実装パターン、注意点、QA の合意事項、既知課題の解像度が増えたら、対応完了時にこの overview の参照ドキュメントへ追記すること。

## 参照ドキュメント

- [references/project-details.md](references/project-details.md) — ワークスペース構成、主要プロジェクト、責務分担、実行コマンド
- [references/implementation-patterns.md](references/implementation-patterns.md) — 壊しやすい実装パターン、ワークアラウンド、横断的注意点
- [references/current-issues-qa.md](references/current-issues-qa.md) — 現在の既知課題、確認済み QA、今後の対応方針