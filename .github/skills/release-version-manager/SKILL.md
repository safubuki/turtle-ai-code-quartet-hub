---
name: release-version-manager
description: バージョン更新とリリース要約を支援するスキル。gitタグ取得、変更差分収集、AI要約、version.json 更新、ローカルタグ作成、必要時のみリモート push を確認付きで進める。「バージョン更新」「リリース準備」「version.json 更新」「タグ振って」「release」「version up」などで発火。
---

# Release Version Manager

## スキル読み込み通知

このスキルが読み込まれたら、必ず以下の通知をユーザーに表示してください：

> 💡 **Release Version Manager スキルを読み込みました**  
> バージョン更新、履歴要約、タグ運用を安全な確認付きで進めます。

## When to Use

- バージョンを更新したいとき
- `version.json` の `version` や `history` を更新したいとき
- リリース前に、現在タグからの変更点をAIに要約させたいとき
- ローカルタグ作成やリモート push まで含めたリリース作業を整理したいとき
- 「リリース準備して」「タグ振って」「version up」などを依頼されたとき

## 概要

このスキルは、現在のタグ・コミット・差分・`version.json` を材料に、次バージョン候補と変更概要を整理し、確認付きで更新作業を進めるためのスキルです。  
`version.json` には全履歴ではなく「前回タグから今回バージョンまでの概要」だけを保持する前提で運用します。

## 手順

### Step 1: 現状収集

以下を確認する。

1. `version.json`
2. `scripts/collect-release-context.ps1`
3. 必要に応じて `git log` / `git diff`

基本コマンド:

```bash
powershell -ExecutionPolicy Bypass -File .github/skills/release-version-manager/scripts/collect-release-context.ps1 -Repo .
```

- 最新タグが取得できる場合は、そのタグ以降のコミットと変更ファイルを収集する
- タグが無い、または履歴が分かりにくい場合は、変更ファイルと主要 diff の読解を優先する
- ワークツリーが dirty の場合は、その状態もユーザーへ明示する

### Step 2: バージョン候補と要約案の作成

収集結果をもとに、以下を AI が整理する。

1. 次バージョン候補
2. `history.summary`
3. `history.highlights`

出力フォーマットは以下を参照:

📄 **[assets/release-summary-template.md](assets/release-summary-template.md)**

バージョンが未指定なら、ユーザーへ簡潔に確認する。  
選択式UIが使える環境ではそれを使ってよいが、plain text の確認でもよい。

### Step 3: 確認付きで `version.json` を更新

ユーザーが version / summary / highlights を確認したら、`version.json` を更新する。

更新補助スクリプト:

```bash
node .github/skills/release-version-manager/scripts/update-version-json.mjs --target version.json --version 4.1.0 --previous 4.0.0 --summary "..." --highlight "タイトル::説明" --highlight "タイトル::説明"
```

- 既定は dry-run
- 実際に書き込むときだけ `--write` を付ける
- AI が `apply_patch` で直接更新してもよいが、同じ形式を維持すること

### Step 4: 検証

`version.json` 更新後は、少なくとも以下を確認する。

1. 履歴表示が想定どおりか
2. `npm run test:run`
3. 必要なら `npm run build`

### Step 5: Git 操作

以下は段階的に扱う。

1. コミット
2. ローカルタグ作成
3. リモート push

## Safety Rules

- `git push` と `git push --tags` は、ユーザーの明示確認なしに実行しない
- ローカルタグ作成も、version 更新と検証完了後に確認を挟む
- 履歴要約はコミットログだけに依存せず、差分と変更ファイルも読む
- `version.json` には最新差分だけを保持し、全履歴の肥大化を避ける
- 履歴が曖昧なときは、推測で断定せず「要確認」として提案する

## 参照ドキュメント

- [assets/release-summary-template.md](assets/release-summary-template.md) — ユーザー確認用の要約テンプレート
- [references/release-policy.md](references/release-policy.md) — 段階的リリース運用ポリシー
- [references/version-json-schema.md](references/version-json-schema.md) — `version.json` の推奨スキーマ
- [scripts/collect-release-context.ps1](scripts/collect-release-context.ps1) — タグ・コミット・差分収集スクリプト
- [scripts/update-version-json.mjs](scripts/update-version-json.mjs) — `version.json` 更新補助スクリプト
