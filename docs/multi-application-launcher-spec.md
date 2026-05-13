# 複数アプリケーション起動対応 仕様書

更新日: 2026-05-13

## 概要

Turtle AI Code Quartet Hub を、VS Code 専用ランチャーから、複数の開発支援アプリケーションを起動できるランチャーへ拡張する。

初期対応アプリケーションは次の 4 つとする。

- Visual Studio Code
- Google Antigravity
- OpenAI Codex Windows アプリケーション
- Claude Windows アプリケーション

既定は各スロット VS Code とし、`Launch Quartet（一括起動）` は各スロットに保存された VS Code / Antigravity の選択状態に従って起動する。設定次第で他アプリケーションも起動できるようにする。AI 実行状態の監視は再導入しない。

## 実装状況

- 2026-05-13: 初期実装完了。
- VS Code / Antigravity の workspace IDE 選択、Codex / Claude の補助アプリ起動、未検出時の無効表示、設定モデル、Jump List 連携を追加済み。
- Claude / Codex の Windows Store 版を検出できるよう、AppModel Repository から AppUserModelID を解決し、`shell:AppsFolder` 経由で起動する。起動済みプロセスと `PackageRootFolder` も補助的に見る。
- 各スロット内に VS Code / Antigravity の切替ボタンを追加する。未起動スロットでは選択状態だけ保存し、起動中スロットで別アプリを選んだ場合は現在のウィンドウを閉じて同じスロット内容を選択アプリで開き直す。
- グローバルな VS Code / Antigravity 選択ボタンは置かない。一括起動はスロット単位の選択状態を使う。
- Codex / Claude ボタンは控え Quartet と同じ行の右端に配置し、起動専用ボタンとして扱う。
- VS Code の既存起動経路、専用 user-data-dir、リモート URI フォールバックは維持。

## 背景・課題

AI 状態監視機能を削除したことで、パネルは拡張機能ごとの仕様変更や誤検知に追従する必要がなくなった。一方で、ユーザーの実作業では VS Code 以外にも IDE 型アプリ、Codex、Claude などを並行して使う。

ただし、4 つの対象アプリは使い方が同じではない。

- VS Code と Google Antigravity は、基本的に 1 ウィンドウ 1 ワークスペースとして使うため、A-D スロットで複数同時起動する価値が高い。
- OpenAI Codex Windows アプリケーションと Claude Windows アプリケーションは、アプリ内で複数ワークスペースや会話を扱う想定が強いため、同じアプリを 4 ウィンドウ並べる優先度は低い。

そのため、4 アプリを同列の大きなボタンとして並べるより、用途に応じて UI 上の優先度を変える。

## UX 方針

### 推奨案: 2 層ランチャー

1. **ワークスペース IDE 層**
   - VS Code と Google Antigravity を主役にする。
   - IDE 選択は各スロットカード内の VS Code / Antigravity ボタンで行う。
   - 既定選択は各スロット VS Code。
   - `Launch Quartet（一括起動）` は、A-D それぞれの保存済み選択状態でワークスペースを起動する。
   - スロットを控えへ移動して戻しても、選択したアプリ情報を保持する。

2. **補助アプリランチャー層**
   - OpenAI Codex と Claude は、控え Quartet の同じ行右端にコンパクトなボタンとして置く。
   - 基本動作は「起動コマンドを送信する」。閉じる/切り替えるトグル動作はしない。
   - 4 分割配置の主対象にはしない。
   - 将来必要になった場合のみ、設定でスロット管理対象に昇格できるようにする。

### ボタン配置の優先度

優先順位は次の通り。

1. VS Code
2. Google Antigravity
3. OpenAI Codex
4. Claude

標準表示では、VS Code / Antigravity を各スロットカード内に置き、Codex / Claude は控え Quartet 見出し行の右端に置く。

縮小表示では、表示面積が限られるため次の扱いにする。

- VS Code / Antigravity の切り替えは、標準表示のスロットカードを主導線にする。
- Codex / Claude はオーバーフロー、Jump List、またはコンパクトなアイコンボタンに逃がす。
- 状態表示は `未検出`、`起動可`、`起動中` 程度に留め、AI の実行状態とは混同しない。

### インストール状態の見せ方

アプリが検出できた場合:

- ボタンを有効化する。
- アイコンまたは短いラベルを通常色で表示する。
- ツールチップに検出した実行ファイル、コマンド、またはショートカット名を表示する。

アプリが検出できない場合:

- ボタンをグレーアウトする。
- ラベルまたはツールチップに `未検出` と表示する。
- 設定画面または設定ファイルで実行ファイルパスを指定できるようにする。
- 未検出のアプリを起動しようとした場合は、短いエラー表示と設定導線を出す。

## 要件一覧

| # | 要件 | 優先度 | 説明 |
|---|---|---|---|
| R1 | VS Code を既定アプリにする | 必須 | 既存の利用体験を壊さず、設定がない場合は現行通り VS Code を起動する。 |
| R1-1 | 一括起動はスロット単位の選択状態に従う | 必須 | 既定は各スロット VS Code。ユーザーがスロットで Antigravity を選ぶと、そのスロットだけ Antigravity で起動する。 |
| R2 | Google Antigravity をワークスペース IDE として起動できる | 必須 | VS Code と同じく A-D スロットのワークスペースを開ける対象にする。 |
| R3 | OpenAI Codex Windows アプリを起動できる | 必須 | 複数ウィンドウ前提ではなく、起動専用の補助ボタンとして扱う。 |
| R4 | Claude Windows アプリを起動できる | 必須 | Codex と同じく起動専用の補助アプリランチャーとして扱う。 |
| R5 | インストール検出を行う | 必須 | PATH、App Execution Alias、Start Menu ショートカット、レジストリ、明示パスを候補にする。 |
| R6 | 未検出アプリをグレーアウトする | 必須 | 起動不能なボタンを押せないようにし、理由を表示する。 |
| R7 | アプリごとの起動引数を設定できる | 必須 | ワークスペースパス、new window 指定、user-data-dir 指定などをテンプレート化する。 |
| R8 | AI 状態監視を再導入しない | 必須 | 起動状態やウィンドウ存在確認は行ってよいが、AI 実行状態は読まない。 |
| R9 | スロット単位のアプリ選択を可能にする | 推奨 | A は VS Code、B は Antigravity のような混在運用を可能にする。 |
| R10 | 設定移行を提供する | 必須 | 既存の `codeCommand` は VS Code アプリ定義へ移行または互換利用する。 |

## アプリ種別

### WorkspaceIde

1 ウィンドウ 1 ワークスペースで使うアプリ。

対象:

- VS Code
- Google Antigravity

挙動:

- A-D スロットに割り当て可能。
- `Launch Quartet（一括起動）` の対象になる。
- ワークスペースパスを起動引数に渡す。
- 可能ならウィンドウを A-D に割り当て、2x2 配置する。

### SingleWindowAgent

アプリ内で複数ワークスペース、会話、タスクを扱うアプリ。

対象:

- OpenAI Codex Windows アプリケーション
- Claude Windows アプリケーション

挙動:

- 控え Quartet 見出し行の右端ボタンから起動する。
- 既に起動していても閉じる/切り替えるトグル動作はしない。アプリ側が単一インスタンス制御を行う場合はそれに委ねる。
- 既定では A-D の 2x2 配置対象にはしない。
- 将来の拡張として、設定でスロット対象にできる余地は残す。

## 設定案

既存設定との互換性を保ちながら、アプリ定義を追加する。

```json
{
  "defaultWorkspaceApplicationId": "vscode",
  "applications": [
    {
      "id": "vscode",
      "displayName": "VS Code",
      "kind": "WorkspaceIde",
      "command": "code",
      "arguments": ["--new-window", "{workspacePath}"],
      "supportsMultipleWindows": true,
      "detection": {
        "commands": ["code"],
        "processNames": ["Code"],
        "startMenuNames": ["Visual Studio Code"]
      }
    },
    {
      "id": "antigravity",
      "displayName": "Antigravity",
      "kind": "WorkspaceIde",
      "command": "",
      "arguments": ["--new-window", "{workspacePath}"],
      "supportsMultipleWindows": true,
      "detection": {
        "commands": ["antigravity"],
        "processNames": ["Antigravity"],
        "startMenuNames": ["Antigravity"]
      }
    },
    {
      "id": "codex",
      "displayName": "Codex",
      "kind": "SingleWindowAgent",
      "command": "",
      "arguments": [],
      "supportsMultipleWindows": false,
      "detection": {
        "processNames": ["Codex"],
        "startMenuNames": ["Codex"]
      }
    },
    {
      "id": "claude",
      "displayName": "Claude",
      "kind": "SingleWindowAgent",
      "command": "",
      "arguments": [],
      "supportsMultipleWindows": false,
      "detection": {
        "processNames": ["Claude"],
        "startMenuNames": ["Claude"]
      }
    }
  ],
  "slots": [
    {
      "name": "A",
      "path": "",
      "applicationId": "vscode"
    }
  ]
}
```

実際の実行ファイル名やコマンド名は環境差があるため、初期値は候補として扱う。検出できない場合はユーザー設定で `command` または実行ファイルパスを指定する。

## インストール検出仕様

検出順は次の通り。

1. ユーザー設定の明示パス。
2. ユーザー設定のコマンド名を `PATH` から解決。
3. Windows の App Execution Alias。
4. Start Menu の `.lnk`。
5. `HKCU` / `HKLM` の `App Paths`。
6. `HKCU` / `HKLM` の Uninstall 情報。
7. MSIX / WindowsApps のパッケージ情報。

検出結果は次の状態で保持する。

| 状態 | UI | 起動 |
|---|---|---|
| Installed | 通常表示 | 可能 |
| NotFound | グレーアウト | 不可 |
| ConfiguredButMissing | 警告付きグレーアウト | 不可 |
| Unknown | 控えめな警告 | 起動時に再検出 |

## 画面仕様

### 標準表示

- 既存の A-D スロットカードは維持する。
- 各スロットカードに `VS Code / Antigravity` の等幅ボタンを追加する。
- 各スロットカードに、現在の起動対象アプリを示す小さなラベルを追加する。
- `Launch Quartet（一括起動）` の直上にはグローバル IDE 選択ボタンを置かない。
- Codex / Claude は控え Quartet 見出し行の右端に小さな起動ボタンとして追加する。
- 未検出アプリはグレーアウトし、ツールチップで理由を出す。

### 縮小表示

- A-D のスロット操作を優先する。
- VS Code / Antigravity の切り替えは短い表示にする。
- Codex / Claude はアイコンボタンまたはオーバーフローメニューに置く。
- 点滅や発光は使わず、静的な色・枠・透明度で状態を示す。

### Jump List

- `Launch Quartet（一括起動）`
- `Codex を開く`
- `Claude を開く`
- 未検出アプリは Jump List に出さない、または `未設定` の説明付きで無効相当の表示にする。

## 影響を受けるファイル

| ファイル | 変更内容 |
|---|---|
| `src/TurtleAIQuartetHub.Panel/Models/AppConfig.cs` | アプリ定義、既定アプリ、スロット別アプリ ID を追加する。 |
| `src/TurtleAIQuartetHub.Panel/Models/SlotConfig.cs` | `applicationId` を追加する。 |
| `src/TurtleAIQuartetHub.Panel/Models/WindowSlot.cs` | 表示用のアプリ名/種別/検出状態を持つ。AI 状態は持たない。 |
| `src/TurtleAIQuartetHub.Panel/Services/VscodeLauncher.cs` | 汎用ランチャーへ分離または委譲する。 |
| `src/TurtleAIQuartetHub.Panel/Services/WindowEnumerator.cs` | アプリごとの process/window matching に対応する。 |
| `src/TurtleAIQuartetHub.Panel/Services/StatusStore.cs` | スロットとアプリ定義の対応を管理する。 |
| `src/TurtleAIQuartetHub.Panel/Services/TaskbarJumpListService.cs` | アプリ別起動項目を追加する。 |
| `src/TurtleAIQuartetHub.Panel/MainWindow.xaml` | スロット別 IDE 選択ボタン、補助アプリボタン、未検出表示を追加する。 |
| `src/TurtleAIQuartetHub.Panel/MainWindow.xaml.cs` | 起動ボタン、アプリ選択、設定反映のイベント処理を追加する。 |
| `config/turtle-ai-quartet-hub.example.json` | 複数アプリ定義例を追加する。 |
| `README.md` | 複数アプリ起動と未検出時の設定方法を追記する。 |

## 実装計画

### Phase 0: 調査と命名整理

**目標**: 実行ファイル名、コマンド名、ショートカット名の候補を整理する。

タスク:

- [ ] VS Code の既存起動経路を整理する。
- [ ] Antigravity / Codex / Claude の検出候補名をローカルで確認する。
- [ ] アプリ種別 `WorkspaceIde` / `SingleWindowAgent` の責務を確定する。

完了条件:

- [ ] 設定例に入れる初期候補名が決まっている。
- [ ] 既存 VS Code 起動の互換方針が決まっている。

### Phase 1: 設定モデルと検出サービス

**目標**: 複数アプリ定義を読み込み、インストール状態を判定できるようにする。

タスク:

- [ ] `ApplicationConfig` / `ApplicationKind` / `ApplicationAvailability` を追加する。
- [ ] `codeCommand` 互換の移行ロジックを実装する。
- [ ] 明示パス、PATH、Start Menu、レジストリの検出を実装する。
- [ ] 未検出理由を UI に渡せる形で保持する。

完了条件:

- [ ] VS Code 未設定でも既存通り起動できる。
- [ ] 未検出アプリが `NotFound` として扱われる。
- [ ] ビルドが成功する。

### Phase 2: 汎用ランチャー

**目標**: VS Code 固有の起動処理を、アプリ種別に応じた起動処理へ広げる。

タスク:

- [ ] `VscodeLauncher` の責務を整理し、汎用 `ApplicationLauncher` を追加する。
- [ ] `WorkspaceIde` はワークスペースパスを渡して起動する。
- [x] `SingleWindowAgent` は起動コマンド送信のみ行い、ウィンドウ検出待ちで固まらないようにする。
- [ ] 起動失敗時のエラーメッセージを整える。

完了条件:

- [ ] VS Code の A-D 起動が現行通り動く。
- [x] Codex / Claude は起動コマンドを送信できる。
- [ ] 未検出アプリの起動操作は安全にブロックされる。

### Phase 3: UI 追加

**目標**: 標準表示と縮小表示に、アプリ選択と補助アプリ起動を追加する。

タスク:

- [x] 各スロットに VS Code / Antigravity の選択ボタンを追加する。
- [x] Codex / Claude のコンパクトな起動ボタンを控え Quartet 見出し行の右端に追加する。
- [ ] 未検出状態のグレーアウトとツールチップを追加する。
- [ ] スロット別アプリ選択メニューを追加するか、初期版では全体選択のみとする。

完了条件:

- [ ] インストール済みアプリだけが押せる。
- [ ] 未検出理由が分かる。
- [ ] AI 状態表示と誤解される色変えや点滅がない。

### Phase 4: Jump List とドキュメント

**目標**: タスクバー導線と利用説明を整える。

タスク:

- [ ] Jump List にアプリ別起動項目を追加する。
- [ ] README に設定例と未検出時の対応を追記する。
- [ ] Store 向け説明で、対象アプリは別途インストールが必要であることを明記する。
- [ ] 回帰確認を実施する。

完了条件:

- [ ] `dotnet build` が成功する。
- [ ] `scripts/Test-StoreReadiness.ps1` が既存基準で通る。
- [ ] VS Code 既存利用の動線が劣化していない。

## 受け入れ条件

- 設定なしの初回起動では、VS Code が既定アプリとして扱われる。
- VS Code と Antigravity は、ワークスペース IDE として A-D スロット起動の対象にできる。
- Codex と Claude は、補助アプリとして起動できる。
- 未インストールまたは未検出のアプリはグレーアウトされ、理由が分かる。
- ユーザーは設定で実行ファイルパスまたはコマンド名を上書きできる。
- AI 実行状態の監視、状態ピル、状態連動の色変え、外周フレームは復活しない。

## 未決事項

- Antigravity / Codex / Claude の標準インストール時の実行ファイル名、App Execution Alias、Start Menu 名。
- Codex / Claude を将来的に A-D スロットへ割り当て可能にするか。
- アプリ設定 UI を初期実装に含めるか、まずは JSON 設定のみで開始するか。
- 各アプリの公式アイコンを使うか、テキストラベル中心にするか。
