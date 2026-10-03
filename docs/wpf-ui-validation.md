# WPF UI 検証ツールの実行方法

一時的な WPF 描画・操作検証ツールは、`tools/WpfCheckGuard.cs` をコンパイル対象に追加し、STA スレッドの入口を次の形にする。

```csharp
[STAThread]
private static int Main(string[] args)
{
    return TurtleAIQuartetHub.Tools.WpfCheckGuard.Run(() => RunChecks(args));
}
```

リポジトリ直下の `.build-tmp/<検証名>/` にプロジェクトを置く場合の参照例:

```xml
<Compile Include="../../tools/WpfCheckGuard.cs" Link="WpfCheckGuard.cs" />
```

- 検証の失敗は標準エラーに記録し、終了コード 1 で返す。成功扱いにしたり、確認ダイアログで待機したりしない。
- 通常の例外に加え、Dispatcher・バックグラウンドスレッド・未観測 Task の例外を検証プロセス内で処理する。エラーダイアログの抑制もその検証プロセス内だけに適用する。
- ユーザーの Windows 全体のエラー報告設定や、本体アプリの例外処理は変更しない。
- 本体を表示せずに描画する検証では、周期更新を停止し、保存先を隔離してからモデルを変更する。`Show()` や実ウィンドウの操作を呼ぶ経路は実行しない。
- 子プロセスを `Start-Process` で起動する場合は `-WindowStyle Hidden` と標準出力・標準エラーのファイルへの転送を指定する。タイムアウト時は起動した PID だけを停止する。
- 検証後は終了コードとプロセスの終了を確認してから、同じ出力先の再ビルドを行う。

2026-10-03 に `TitleHoverUi.exe` の検証失敗が未処理例外となり、ユーザーのデスクトップへ Windows のアプリケーションエラーを表示したため、この共通処理を導入した。`TitleHoverUi`、`TitleOrderUi`、`CompactUiPreview` の一時ツールに適用済み。通常例外・Dispatcher 例外・別スレッド例外の意図的な失敗を使い、各プロセスが標準エラーを出力して終了コード 1 で終了することを確認した。
