# Skills Sync Conflict Policy

このドキュメントは `.agent/skills` / `.agents/skills` / `.github/skills` の同期時に、  
上書き事故を避けるための判定基準を定義する。

## 競合種別

`safe-sync-skills.mjs` は以下を競合として扱う。

| 種別 | 条件 | リスク |
|------|------|--------|
| `base-missing` | ベース候補に存在しないファイルが他フォルダに存在する | `base` 同期でファイルが消える |
| `base-outdated` | 同一相対パスで、ベース候補より新しい別内容のファイルが他フォルダに存在する | `base` 同期で新しい変更が失われる |

## 同期戦略の選択基準

| 条件 | 推奨戦略 | 理由 |
|------|----------|------|
| 競合なし | `base` | ファイル単位の最新採用数と更新時刻を加味したベース候補を正としてミラー展開できる |
| 競合あり | `latest` | ファイル単位で最新を採用し、消失リスクを下げる |
| 競合あり + ベース固定が必須 | `base` + `--no-strict` | 明示的にリスクを受容する場合のみ許可 |

## 標準運用フロー

1. 監査（dry-run）

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --verbose
```

2. 自動戦略で適用（推奨）

```powershell
node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --apply --verbose
```

3. 同期後確認

```powershell
node scripts/sync-skills.mjs --dry-run --verbose
git status --short .agent/skills .agents/skills .github/skills
```

## 非推奨運用

- `--no-backup` を常用する
- 競合検知を無視して `--strategy base` を強制する
- 監査を省略して即時適用する
