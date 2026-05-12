# Turtle AI Code Quartet Hub

4つの VS Code ウィンドウを A-D のスロットとして起動し、2x2 に並べる Windows 向け WPF パネルです。
スロット単位の起動、配置、集中表示、非表示、控え保存を軽量に扱えます。

## できること

- `Launch Quartet` で4つの VS Code を起動し、画面に2x2で配置
- 縮小モードで小さな操作バーとして常時表示
- スロット A-D のタイトル、ワークスペース、控え Quartet を保存
- `ディスプレイ移動` で4面表示を別ディスプレイへまとめて移動
- タスクバー右クリックからスロット切替、表示モード切替、前面/背面操作を実行
- スロット別の VS Code user-data-dir で最近使ったワークスペースを分離

## 必要環境

- Windows
- VS Code
- .NET 10 SDK
- VS Code の `code` コマンド

`code` コマンドが使えない場合は、設定ファイルの `codeCommand` に `Code.exe` のパスを指定してください。

## 起動方法

開発ビルド: 実行中の本体が既定の `bin\Debug` をロックしていても通るよう、毎回別の artifacts path へ出力します。

```powershell
.\scripts\Build-Panel.ps1
```

ロックを避けたままビルドして、そのまま起動する場合:

```powershell
.\scripts\Build-Panel.ps1 -Run
```

通常ビルド: 実行中の `TurtleAIQuartetHub.exe` を停止しているときだけ使います。

```powershell
dotnet build .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

通常の開発実行:

```powershell
dotnet run --project .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj
```

`dotnet build` と `dotnet run` は既定の `src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows` を使うため、その場所の exe が起動中だとロックで失敗します。検証や反復ビルドは `Build-Panel.ps1` か `--artifacts-path` を使ってください。

ビルド済み exe を直接起動:

```powershell
.\src\TurtleAIQuartetHub.Panel\bin\Debug\net10.0-windows\TurtleAIQuartetHub.exe
```

## 設定

ユーザー設定は `%LOCALAPPDATA%` 側に置くのがおすすめです。

```powershell
$configDir = Join-Path $env:LOCALAPPDATA 'TurtleAIQuartetHub\config'
New-Item -ItemType Directory -Force $configDir
Copy-Item .\config\turtle-ai-quartet-hub.example.json (Join-Path $configDir 'turtle-ai-quartet-hub.json')
```

設定ファイルは次の順で読み込まれます。

1. `%LOCALAPPDATA%\TurtleAIQuartetHub\config\turtle-ai-quartet-hub.json`
2. `config\turtle-ai-quartet-hub.json`
3. `config\turtle-ai-quartet-hub.example.json`
4. アプリ内の既定値

主な設定項目:

- `codeCommand`: VS Code の起動コマンドまたは `Code.exe` のパス
- `launchTimeoutSeconds`: VS Code 起動待ち時間
- `remoteReconnectTimeoutSeconds`: SSH / Remote 接続の再接続待ち時間
- `statusRefreshIntervalMilliseconds`: VS Code ウィンドウ状態とワークスペース表示の更新間隔
- `inheritMainUserState`: 通常 VS Code の設定やスニペットをスロットへ引き継ぐか

実行時データは `%LOCALAPPDATA%\TurtleAIQuartetHub\` に保存されます。

## 確認用コマンド

Store公開準備の確認:

```powershell
.\scripts\Test-StoreReadiness.ps1
```

ローカル確認用 MSIX の生成:

```powershell
.\scripts\New-LocalMsixPackage.ps1
```

## 配布ビルド

自己完結型の win-x64 exe を作る場合:

```powershell
dotnet publish .\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\turtle-ai-quartet-hub
```

配布物には `LICENSE.txt` も同梱してください。

## 関連ドキュメント

- [PRIVACY.md](PRIVACY.md): プライバシーポリシー草案
- [docs/store-readiness.md](docs/store-readiness.md): Microsoft Store 公開前チェック
- [docs/store-listing-draft.md](docs/store-listing-draft.md): Store 掲載文案
- [docs/msix-packaging-guide.md](docs/msix-packaging-guide.md): MSIX パッケージング手順
- [docs/release-notes-draft.md](docs/release-notes-draft.md): リリースノート草案
- [SUPPORT.md](SUPPORT.md): サポート案内草案
- [assets/store/README.md](assets/store/README.md): Store 画像素材チェック

## ライセンス

GNU General Public License v3.0 です。詳細は [LICENSE.txt](LICENSE.txt) を参照してください。
