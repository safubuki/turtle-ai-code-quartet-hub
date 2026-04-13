# `version.json` 推奨スキーマ

```json
{
  "version": "4.1.0",
  "history": {
    "previousVersion": "4.0.0",
    "summary": "前回タグからの概要説明",
    "highlights": [
      {
        "title": "変更タイトル",
        "description": "ユーザー向けの短い説明"
      }
    ]
  }
}
```

## ルール

- `version` は現在バージョン
- `history.previousVersion` は差分起点のタグ
- `history.summary` は 1〜2 文の概要
- `history.highlights` は 2〜4 件程度に抑える
- 詳細な技術ログや全履歴は持たない
