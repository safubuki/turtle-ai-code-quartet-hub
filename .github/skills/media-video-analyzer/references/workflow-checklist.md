# ワークフローチェックリスト

## 1. 依頼内容の確認

- [ ] ユーザーが動画解析を明示的に依頼している
- [ ] 入力動画のパスが分かっている
- [ ] 解析目的（何を検証するか）が明確

## 2. 実行経路とツール確認

- [ ] 実行経路を決定した（A: helper scripts / B: portable venv）
- [ ] 経路 A の場合: `scripts/dev/setup-media-analysis-env.ps1` がある
- [ ] 経路 A の場合: `scripts/dev/run-media-analysis.ps1` がある
- [ ] 経路 A の場合: `scripts/dev/analyze-video.py` がある
- [ ] セリフ抽出が必要な場合: `scripts/dev/requirements-media-analysis-stt.txt` がある
- [ ] 経路 B の場合: Python 3.11+ が使える
- [ ] `scripts/dev` がある（無ければ作成する）
- [ ] `.venv-media-analysis` がある（または作成予定）

## 3. 環境セットアップ（必要な場合のみ）

- [ ] ユーザー承認を取得済み
- [ ] 経路 A: `npm run dev:media:setup` を実行
- [ ] 経路 A: セリフ抽出が必要なら `npm run dev:media:setup:stt` を実行
- [ ] `small` / `tiny` の初回利用時は `npm run dev:media:setup:stt:models` でモデルを事前取得
- [ ] 経路 B: venv を作成し `imageio imageio-ffmpeg numpy` を導入
- [ ] 経路 B: セリフ抽出が必要なら Whisper 系依存を venv に導入
- [ ] 失敗時は原因と次アクションを提示

## 4. ベースライン解析

### summary

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode summary
```

```powershell
.\.venv-media-analysis\Scripts\python.exe .\scripts\dev\analyze-video.py --input "C:\path\capture.mp4" --mode summary
```

### black-segments

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode black-segments -Scope tail -TailSeconds 2
```

### freeze-segments

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode freeze-segments -Scope full -FreezeThreshold 0.8
```

### transcribe（セリフ抽出）

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode transcribe -SttModel small -SttLanguage ja
```

### JSON 出力

```powershell
npm run dev:media:analyze -- -InputPath "C:\path\capture.mp4" -Mode summary -OutputPath ".media-analysis-output\latest.json"
```

## 5. 不足時の拡張

- [ ] 既存モードで不足する理由を説明した
- [ ] `scripts/dev` 配下にワークスペース限定の拡張案を提示した
- [ ] スクリプトを CLI 引数で再利用可能にした

## 6. レポート

- [ ] 時刻/フレーム根拠を記載
- [ ] 観測結果と推論を分離
- [ ] 最小影響の対応案を提示
- [ ] 再検証手順を定義
- [ ] 不要時のクリーンアップ手順（`dev:media:cleanup` / `dev:media:cleanup:keep-json`）を案内
