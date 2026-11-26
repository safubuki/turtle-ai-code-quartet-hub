---
name: media-video-analyzer
description: 動画解析の明示依頼時に scripts/analyze_video.py でメタデータ、輝度、黒区間、フリーズ区間、任意の文字起こしを定型計測し、AIが時系列証拠の解釈、原因仮説、修正案を判断する汎用スキル。
---

# Media Video Analyzer

## スキル読み込み通知

このスキルを使用するときは、ユーザーへ次を通知する：

> **Media Video Analyzer スキルを読み込みました** — 動画の定型計測をスキル内スクリプトで行い、観測結果の意味をAIが判断します。

## 発火条件

[references/activation-policy.md](references/activation-policy.md) に従い、ユーザーが動画解析を明示し、動画ファイルがある場合だけ使用する。コードだけで完結する依頼には使用しない。

## 役割分担

| 領域 | 担当 |
|---|---|
| 解析目的、見るべき区間、閾値、追加モードの判断 | AI |
| 動画読込、サンプリング、メタデータ・輝度・黒区間・フリーズ区間の計測、JSON整形 | `scripts/analyze_video.py` |
| 観測と推論の分離、原因仮説、確信度、修正・再検証方針 | AI |
| 音声文字起こし | スクリプトの任意 `transcribe` モード（明示承認した依存のみ） |

## 実行環境と依存

Python 3.10 以上を確認する：

```bash
python --version
python3 --version
```

`summary`、`black-segments`、`freeze-segments` には `imageio`, `imageio-ffmpeg`, `numpy` が必要。`transcribe` には追加で `faster-whisper` が必要。依存やモデルを勝手に導入・取得せず、未導入なら用途、保存先、代替案を示してユーザーの明示承認を得る。導入する場合はワークスペースの venv に限定する。

スクリプト自体はスキルの `scripts/` にあり、対象ワークスペースへ helper を生成しない。

## 手順

### Step 1: 入力と目的を確認する

動画パス、確認したい現象、対象区間を確認する。入力が不明なら解析を始めない。

### Step 2: ヘルプとドライランを確認する

```bash
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --help
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode summary --dry-run
```

`.github/skills` または `.claude/skills` から読み込んだ場合はスクリプトパスを読み替える。

### Step 3: まず summary を実行する

```bash
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode summary
```

AIは duration、fps、sample_count、luminance、フレーム差分を確認し、追加解析が必要か判断する。

### Step 4: 必要なモードだけ実行する

```bash
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode black-segments --scope tail --tail-seconds 3
```

```bash
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode freeze-segments --scope full --freeze-threshold 0.8
```

```bash
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode transcribe --stt-model small --stt-language ja
```

結果を保存する場合は、先にドライランし、本実行する：

```bash
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode summary --output .media-analysis-output/latest.json --dry-run
python .agents/skills/media-video-analyzer/scripts/analyze_video.py --input "C:\path\capture.mp4" --mode summary --output .media-analysis-output/latest.json
```

既存出力の置換が必要なときだけ `--force` を使う。

### Step 5: AIが解釈し報告する

1. 時刻、サンプル数、閾値を添えて観測結果を記載する。
2. サンプリングであることを踏まえ、検出なしを「異常なし」と断定しない。
3. 観測事実、原因仮説、確信度、未確認事項を分ける。
4. 修正後は同じ引数・閾値で再実行し、変更前後を比較する。
5. [assets/analysis-report-template.md](assets/analysis-report-template.md) で報告する。

## 入出力と失敗時の扱い

- 入力: ローカル動画、解析モード、scope、サンプリング間隔、各閾値
- 出力: UTF-8 JSON。配列は時刻順。標準出力が既定
- 終了コード: `0` 成功、`1` 入力・依存・解析・出力エラー、`2` CLI 引数エラー
- 外部通信: スクリプト自身はアップロードしない。STTモデルの初回取得は依存側で起こり得るため事前承認が必須
- 解析は入力動画を変更しない。`--output` 以外へ書き込まない

詳細は [references/script-contract.md](references/script-contract.md) と [references/workflow-checklist.md](references/workflow-checklist.md) を参照する。

## スクリプト更新条件

- 新しい異常種別を同じ手順で2回以上手作業解析した
- decoder、依存API、出力スキーマが変わった
- 閾値やscopeを引数化できず固定値で補った
- 同一入力で非決定的な順序や再現可能な誤検出が見つかった

## 参照

- [scripts/analyze_video.py](scripts/analyze_video.py) — 定型動画解析
- [references/script-contract.md](references/script-contract.md) — 入出力・計測契約
- [references/activation-policy.md](references/activation-policy.md) — 発火条件
- [references/workflow-checklist.md](references/workflow-checklist.md) — 実行確認
- [assets/analysis-report-template.md](assets/analysis-report-template.md) — AIレポート
