# gh Issue Commands

## 1. 新規作成

```powershell
gh issue create --title "[バグ] iOSで書き出し失敗" --body "本文" --label bug
```

## 2. 一覧

```powershell
gh issue list
gh issue list --label bug
gh issue list --state all
```

## 3. 詳細確認

```powershell
gh issue view 123
gh issue view 123 --comments
```

## 4. 編集

```powershell
gh issue edit 123 --title "[改善] タイトル更新"
gh issue edit 123 --add-label "priority:high"
gh issue edit 123 --remove-label "needs-triage"
```

## 5. コメント

```powershell
gh issue comment 123 --body "検証結果: 再現せず"
```

## 6. 状態変更

```powershell
gh issue close 123 --comment "PR #456 で対応済み"
gh issue reopen 123 --comment "再発を確認したため再オープン"
```

## 7. 検索

```powershell
gh issue list --search "is:open label:bug export"
```
