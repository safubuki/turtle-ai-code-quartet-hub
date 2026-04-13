---
name: skills-sync-guard
description: "`.agent/skills` `.agents/skills` `.github/skills` を安全同期するスキル。各フォルダの更新状態を調査し、競合を検知してから最新戦略またはベース戦略で同期を実施する。「skillsを同期」「スキル同期」「sync skills」「sync-skills」「skillsフォルダを揃える」「.agent と .github を同期」などで発火。"
---

# Skills Sync Guard

## スキル読み込み通知

このスキルが読み込まれたら、必ず以下の通知をユーザーに表示してください：

> 💡 **Skills Sync Guard スキルを読み込みました**  
> 3つの skills ディレクトリを調査し、競合を監査して安全に同期します。

## When to Use

- `.agent/skills` と `.agents/skills` と `.github/skills` の内容がズレているとき
- どのフォルダをベースに同期すべきか判断したいとき
- 同期時の上書き事故を避けながら安全に反映したいとき
- CI/開発環境で skills 配布先を統一したいとき

## 概要

このスキルは、3つの skills ディレクトリの状態を監査し、競合リスクを判定した上で安全な同期を実行する。  
`scripts/sync-skills.mjs` を直接実行する前に、`scripts/safe-sync-skills.mjs` で事前調査と戦略決定を行う。

## 手順

### Step 1: 事前監査（必須）

まず dry-run で状態を確認する。

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --verbose
```

確認ポイント:

- 各ディレクトリの `files` 数と `newest` タイムスタンプ
- `base` 候補（ファイル単位の最新採用数が最も多く、かつ新しい更新を持つディレクトリ）
- `conflicts` 件数（`base-missing` / `base-outdated`）

### Step 2: 同期戦略の決定

`safe-sync-skills.mjs` のデフォルト戦略は `auto`:

- 競合なし: `base` 戦略（最新採用数と更新時刻を加味したベース候補を展開）
- 競合あり: `latest` 戦略（ファイル単位で最新を選ぶ）

ベース固定したい場合:

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --strategy base --base github --verbose
```

### Step 3: 同期の適用

適用時は必ず `--apply` を付ける。バックアップはデフォルト有効。

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --apply --verbose
```

競合がある状態で `base` を強制する場合は、ユーザー明示承認のうえでのみ `--no-strict` を使用する。

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --apply --strategy base --base agents --no-strict --verbose
```

### Step 4: 同期後検証

1. 再度 dry-run して差分が実質解消されたことを確認する。

```powershell
node scripts/sync-skills.mjs --dry-run --verbose
```

2. Git 差分を確認し、想定外の削除・上書きがないことを確認する。

```powershell
git status --short .agent/skills .agents/skills .github/skills
```

## 使い方

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs [options]
```

主要オプション:

- `--apply`: dry-run ではなく実際に同期する
- `--strategy <auto|base|latest>`: 同期戦略を指定する（既定: `auto`）
- `--base <auto|agents|agent|github>`: ベース候補を指定する（既定: `auto`）
- `--no-strict`: 競合があっても `base` 戦略を許可する
- `--no-backup`: 同期前バックアップを無効化する（非推奨）
- `--backup-dir <path>`: バックアップ保存先を変更する
- `--no-delete`: ミラーモードではなくオーバーレイ同期にする
- `--verbose`: 監査情報を詳細表示する
- `--json`: 監査結果をJSONで出力する

## 参照ドキュメント

- [references/conflict-policy.md](references/conflict-policy.md)
- [scripts/safe-sync-skills.mjs](scripts/safe-sync-skills.mjs)
