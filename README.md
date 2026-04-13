# VS Code Square

VS Code Square は、4つの VS Code ウィンドウをスロット A-D として起動し、2x2 に配置する Windows 向けの小さな WPF パネルです。

## 機能

- `Square 4` で未起動スロットの VS Code を起動し、4分割に配置
- 各カードの `フォーカス` で対象ウィンドウを最大化し、再度押すと4分割へ戻す
- 各カードのタイトルをその場で編集して保持
- 各カードの `閉じる` で対象 VS Code を閉じる
- `全て閉じる` で管理中の VS Code をまとめて閉じる
- `設定保存` / `設定読み込み` でカードタイトル、最後に開いたワークスペース、ウィンドウ割り当てを保存・復元
- スロット別の VS Code user-data-dir を使い、スロットごとに最近開いたワークスペースを分けて管理

AI 状態取得は今後のフェーズです。現時点の `AI 未取得` は検出結果ではなくプレースホルダーです。

## 必要環境

- Windows
- VS Code
- .NET 10 SDK
- VS Code コマンドラインランチャー `code`

## 環境構築

.NET SDK を winget で入れる場合:

```powershell
winget install Microsoft.DotNet.SDK.10
```

インストール後、別の PowerShell を開き直して確認します。

```powershell
dotnet --version
```

VS Code の `code` コマンドも確認します。

```powershell
code --version
```

`code` が見つからない場合は、VS Code のコマンドパレットから `Shell Command: Install 'code' command in PATH` 相当の設定を有効にするか、`config\vscode-square.json` の `codeCommand` に VS Code の `code.cmd` / `Code.exe` パスを設定してください。

## 開発環境での起動

ビルド:

```powershell
dotnet build .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj
```

開発実行:

```powershell
dotnet run --project .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj
```

ビルド済み exe を直接起動:

```powershell
.\src\VscodeSquare.Panel\bin\Debug\net10.0-windows\VscodeSquare.Panel.exe
```

## 設定

設定ファイルを作る場合:

```powershell
Copy-Item .\config\vscode-square.example.json .\config\vscode-square.json
```

アプリは次の順で設定を探します。

1. `config\vscode-square.json`
2. `config\vscode-square.example.json`
3. アプリ内の既定値

`config\vscode-square.json` は、スロット名、初期ワークスペースパス、起動タイムアウト、スロット別 user-data-dir の有無など、配布時にも固定したい設定を置く場所です。

スロットの `path` を空にすると、初回起動では VS Code のようこそ画面やフォルダ未選択状態になります。その後、VS Code でフォルダやワークスペースを開き、パネルの `設定保存` または `閉じる` / `全て閉じる` を押すと、最後に検出したワークスペースパスが `%LOCALAPPDATA%\VscodeSquare\slots.json` に保存されます。次回 `Square 4` を押すと、この保存済み設定を読み込んで VS Code を起動します。

実行時状態の既定保存先:

```text
%LOCALAPPDATA%\VscodeSquare\
```

ここには、スロット別 user-data-dir、`slots.json`、ログ、将来の補助拡張ステータスファイルなど、PCごとの実行時データを保存します。

## デプロイ用 exe の作成

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\vscode-square
```

出力先:

```text
dist/
  vscode-square/
    VscodeSquare.Panel.exe
    config/
      vscode-square.example.json
```

配布時に固定設定を同梱する場合は、同じ階層に `config\vscode-square.json` を置きます。

```powershell
Copy-Item .\config\vscode-square.example.json .\dist\vscode-square\config\vscode-square.json
```

軽量なフレームワーク依存版でよい場合は、実行先PCに .NET 10 Desktop Runtime が必要です。

```powershell
dotnet publish .\src\VscodeSquare.Panel\VscodeSquare.Panel.csproj -c Release -o .\dist\vscode-square-framework
```

## AI・タブ・拡張機能情報

この WPF パネル単体で安定して取れるのは、Win32 で見える VS Code ウィンドウのハンドル、タイトル、プロセス情報までです。

VS Code の開いているタブ、ワークスペース、インストール済み拡張機能、拡張機能の状態を正確に取るには、VS Code 補助拡張を作り、VS Code API から取得した情報をローカルファイルなどへ書き出す構成が適しています。候補 API は `vscode.window.tabGroups.all`、`vscode.workspace.workspaceFolders`、`vscode.extensions.all` です。
