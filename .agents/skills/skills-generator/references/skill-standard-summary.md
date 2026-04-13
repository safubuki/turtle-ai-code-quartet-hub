# Agent Skills Standard — 仕様サマリー

Agent Skills は Anthropic が提唱するオープンスタンダードで、AIエージェントに専門知識と手続き的な知識を与えるための仕組みです。

## 仕様バージョン

- **Standard**: [anthropics/agent-skills-standard](https://github.com/anthropics/agent-skills-standard)
- **公式スキル集**: [anthropics/skills](https://github.com/anthropics/skills)
- **GitHub Copilot 対応**: [github/awesome-copilot/skills](https://github.com/github/awesome-copilot/tree/main/skills)

## SKILL.md フロントマター仕様

| フィールド | 必須 | 制約 | 説明 |
|-----------|------|------|------|
| `name` | ✅ | 最大64文字。`[a-z0-9-]` のみ。先頭・末尾ハイフン不可 | スキルの一意な識別子 |
| `description` | ✅ | 最大1024文字。空欄不可 | スキルの内容と使用タイミングの説明 |
| `license` | ❌ | — | ライセンス名またはファイル参照 |
| `compatibility` | ❌ | 最大500文字 | 想定環境・必要条件 |
| `metadata` | ❌ | — | 任意のキーバリューマッピング |
| `allowed-tools` | ❌ | — | 事前承認ツールのスペース区切りリスト（実験的） |

## ディレクトリ構成

```
.github/skills/{skill-name}/
├── SKILL.md              # 必須
├── scripts/              # オプション: ヘルパースクリプト
├── references/           # オプション: 参照ドキュメント・仕様
├── examples/             # オプション: 実装例・サンプル
└── assets/               # オプション: テンプレート・図表
```

## 配置場所

| 種類 | パス | 用途 |
|------|------|------|
| プロジェクトスキル | `.github/skills/` | 単一リポジトリ専用 |
| 個人スキル | `~/.copilot/skills/` | 複数プロジェクト共有 |
| 旧形式（互換） | `.claude/skills/` | Claude Code 互換 |

## ロードの仕組み

Copilot は以下の手順でスキルを遅延ロードします：

1. スキルディレクトリを自動スキャンし、各スキルの `name` と `description` を収集
2. システムプロンプトに `<skills>` セクションとして注入
3. ユーザーの入力と `description` を照合し、関連性があると判断したら `read_file` で SKILL.md を読み込み
4. 読み込んだ指示に従って作業を実行

```xml
<skills>
Here is a list of skills that contain domain specific knowledge...
<skill>
  <name>skill-name</name>
  <description>...</description>
  <file>path/to/SKILL.md</file>
</skill>
</skills>
```

## 有効化

VS Code の settings.json に以下を追加：

```json
{
  "chat.useAgentSkills": true
}
```

## 注意事項

- `description` はスキルのロード判定に直接使われるため、最も重要なフィールド
- スキルの内容はユーザーの入力があって初めてロードされる（遅延ロード）
- SKILL.md 本文はコンテキストウィンドウを消費するため、スリムに保つことが推奨
- `allowed-tools` は実験的機能のため、GitHub Copilot では削除が推奨
