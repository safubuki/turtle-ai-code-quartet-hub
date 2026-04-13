---
name: media-video-analyzer
description: 動画ファイルを根拠に挙動確認・不具合解析を行う汎用スキル。動画解析の明示依頼時のみ発火し、依存導入前に必ず確認し、まず summary で観測して必要時のみ scripts/dev を生成・拡張する。
---

# Media Video Analyzer

## スキル読み込み時の挙動

このスキルが読み込まれたら、以下の手順を順番に実行してください。

> 重要: 依存インストールや venv 構築の前に、必ずユーザー確認を取ること。

## 利用条件

- ユーザーが動画解析を明示的に依頼した
- 動画ファイルを根拠に不具合の再現確認や切り分けを行いたい
- 時系列の現象（表示崩れ、ノイズ、停止、カクつき等）を検証したい

このスキルを使わないケース:

- 動画解析の依頼がない
- 動画が不要で、コード変更のみで完結する

## 手順

### 手順1: 依頼の成立確認

1. 依頼が「動画解析の明示依頼」か確認する
2. 解析対象ファイルパスと目的を確認する
3. 不足情報がある場合は最小限だけ確認する

### 手順2: 環境・ツール確認

まず、どちらの経路で実行するか判定する。

経路 A（workspace helper がある場合）:

1. 以下の存在を確認する
   - `scripts/dev/setup-media-analysis-env.ps1`
   - `scripts/dev/run-media-analysis.ps1`
   - `scripts/dev/analyze-video.py`
   - `scripts/dev/requirements-media-analysis-stt.txt`（セリフ抽出を行う場合）
2. `.venv-media-analysis` が利用可能か確認する
3. 環境未構築なら、必ずユーザー確認後に `npm run dev:media:setup` を実行する
4. セリフ抽出（STT）が必要な場合は、必ずユーザー確認後に `npm run dev:media:setup:stt` を実行する
5. `small` / `tiny` を確実に使う場合は、必ずユーザー確認後に `npm run dev:media:setup:stt:models`（または `-PrefetchSttModels -SttModels ...`）で事前取得する

経路 B（helper がない場合のポータブル手順）:

1. Python 3.11+ を確認する
2. ワークスペース直下に `.venv-media-analysis` を作成する
3. 必要依存 (`imageio`, `imageio-ffmpeg`, `numpy`) を venv にだけ入れる
4. `scripts/dev` が無ければ作成し、解析スクリプトを `scripts/dev` に配置する
5. 解析スクリプトは引数で動く汎用形にする（固定パス・固定閾値を避ける）
6. 依存インストール前に必ずユーザー確認を取る
7. セリフ抽出を行う場合は Whisper 系依存（例: `faster-whisper`）を venv にのみ導入する

### 手順3: ベースライン解析

まずは必ず `summary` で観測する:

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode summary
```

経路 B の例:

```powershell
.\.venv-media-analysis\Scripts\python.exe .\scripts\dev\analyze-video.py --input "C:\path\capture.mp4" --mode summary
```

必要に応じて目的別モードを使う:

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode black-segments -Scope tail
```

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode freeze-segments -Scope full
```

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode transcribe -SttModel small -SttLanguage ja
```

### 手順4: 拡張ルール（ワークスペース別）

既存モードで不足する場合のみ、`scripts/dev` に追加スクリプトを作成・拡張する。

- 追加前にユーザーへ「何を追加するか」を説明
- `scripts/dev` が無い場合は先にディレクトリを作成してから進める
- 影響範囲を `scripts/dev` に限定
- 汎用利用可能な形（入力引数化、固定値の排除）で実装
- 既存ファイルがある場合は破壊的変更を避け、最小差分で更新する

### 手順5: 報告と次アクション

1. 時刻/フレーム根拠付きで結果を整理
2. 必要なら最小影響の修正案を提示
3. 修正後は同一手順で再検証する
4. 解析生成物が不要になったら `npm run dev:media:cleanup`（または `npm run dev:media:cleanup:keep-json`）を案内する

## 参照ガイド

- [references/activation-policy.md](references/activation-policy.md)
- [references/workflow-checklist.md](references/workflow-checklist.md)
- [assets/analysis-report-template.md](assets/analysis-report-template.md)

