---
name: issue-specialist
description: GitHub Issue運用の専門スキル。Issueテンプレートの設計・日本語化・導入、gh CLIの環境確認、概要からの構造化Issue登録、ラベル/優先度/状態管理まで一貫して実施する。`Issueを追加` `Issueテンプレートを作成` `gh issue create` `バグ/改善/ドキュメント/メンテIssueを整備` `Issue運用を標準化` などの依頼で使用する。
---

# Issue Specialist

## スキル読み込み時の挙動

このスキルが読み込まれたら、以下の順序で実行する。

1. 依頼を Issue 種別（バグ / 改善 / ドキュメント / メンテ）へ分類する
2. `gh` CLI の導入状況と認証状態を確認する
3. 必要なら Issue テンプレートと作成CLIを導入する
4. ユーザー概要から構造化本文を生成し、Issueを登録する
5. 以後の運用（一覧、更新、クローズ、再オープン、コメント）を実施する

## 手順

### 手順1: 依頼の整理

最初に次を確認する。

- 対象リポジトリ（現在のリポジトリか、別リポジトリか）
- Issue種別（バグ / 改善 / ドキュメント / メンテ）
- 目的（新規登録 / テンプレート整備 / 運用改善）

### 手順2: 環境確認

以下を確認する。

```powershell
gh --version
gh auth status
git remote -v
```

`gh` が無い場合は、導入方法を案内する。

- 公式: <https://cli.github.com/>

### 手順3: テンプレートとCLIの導入

Issue運用を未整備のリポジトリには、次を実行する。

```powershell
node .github/skills/issue-specialist/scripts/setup-issue-specialist.mjs --target . --with-cli
```

別リポジトリへ適用する場合:

```powershell
node .github/skills/issue-specialist/scripts/setup-issue-specialist.mjs --target "C:\path\repo" --with-cli
```

このセットアップは次を作成・更新する。

- `.github/ISSUE_TEMPLATE/*.yml`（日本語フォーム）
- `.github/ISSUE_TEMPLATE/config.yml`
- `scripts/create-github-issue.mjs`（構造化Issue作成CLI）
- `package.json` の `scripts.issue:create`（存在時のみ）

### 手順4: Issue登録

セットアップ済みリポジトリでは、以下で登録する。

対話式:

```powershell
npm run issue:create
```

非対話:

```powershell
npm run issue:create -- --type バグ --summary "iOSで書き出しに失敗する"
```

内容確認のみ:

```powershell
npm run issue:create -- --type 改善 --summary "タイムライン操作性を改善する" --dry-run
```

### 手順5: 運用管理

日常運用は `gh issue` サブコマンドで管理する。
具体例は `references/gh-issue-commands.md` を参照する。

### 手順6: 品質基準の適用

Issueの品質は `references/issue-best-practices.md` の基準に従って評価・補正する。
不十分なIssueは、背景・受け入れ条件・検証観点を補ってから登録する。

## 参照

- [references/issue-best-practices.md](references/issue-best-practices.md)
- [references/gh-issue-commands.md](references/gh-issue-commands.md)
- [assets/issue-templates/](assets/issue-templates/)
- [assets/scripts/create-github-issue.mjs](assets/scripts/create-github-issue.mjs)
- [scripts/setup-issue-specialist.mjs](scripts/setup-issue-specialist.mjs)
