---
name: readme-generator
description: プロジェクトのREADME.mdを作成・更新するスキル。プロジェクト名、概要、目次、機能一覧、環境構築、実行方法、技術スタック、ファイル構成、ライセンス、変更履歴を簡潔にまとめたREADMEを生成する。「READMEを作って」「README更新」「README生成」「ドキュメント作成」「プロジェクト説明を書いて」「ドキュメント整備」「プロジェクト紹介」「使い方を書いて」「READMEを最新にして」「readme」「documentation」「docs」などで発火。
---

# README Generator Skill

## スキル読み込み通知

このスキルが読み込まれたら、必ず以下の通知をユーザーに表示してください：

> 💡 **README Generator スキルを読み込みました**  
> プロジェクトのREADME.mdを作成・更新します。

## When to Use

- プロジェクトの README.md を新規作成したいとき
- README.md を最新の実装に合わせて更新したいとき
- プロジェクトの公開に向けて README を整備したいとき
- バージョンアップ後に変更履歴を反映したいとき
- 「このプロジェクトの説明を書いてほしい」とき
- 既存の README が古くなって実態と乖離しているとき

## README 作成ガイドライン

### 簡潔さの原則

README は**ユーザーが参照したらすぐに理解できて、簡潔にまとまっている**ことが最も重要です。以下を心がけてください：

- 環境構築・実行方法はコマンド数行で完結させる
- 長文の説明より箇条書きと表を使う
- 「とりあえず動かす」ための最短パスを最初に示す
- 詳細は別ドキュメントへのリンクで誘導する

## README テンプレート

以下のテンプレートに沿って README を生成してください：

📄 **[assets/readme-template.md](assets/readme-template.md)**

各セクションは実際のプロジェクトから情報を収集して埋めます。

## 情報収集の手順

README を生成する際の参照先、収集すべき情報、注意事項は以下を参照してください：

📄 **[references/collection-guide.md](references/collection-guide.md)**
