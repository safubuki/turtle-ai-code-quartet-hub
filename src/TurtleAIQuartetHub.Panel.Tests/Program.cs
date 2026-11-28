using TurtleAIQuartetHub.Panel.Services;

var failures = new List<string>();

await VerifyAsync(
    "既に閉じている HWND は成功し、close を送らない",
    initiallyLive: false,
    requestAccepted: true,
    pollsUntilClosed: null,
    ManagedWindowCloseStatus.AlreadyClosed,
    expectedCloseRequests: 0);

await VerifyAsync(
    "WM_CLOSE 拒否時は管理解除しない結果を返す",
    initiallyLive: true,
    requestAccepted: false,
    pollsUntilClosed: null,
    ManagedWindowCloseStatus.RequestFailed,
    expectedCloseRequests: 1);

await VerifyAsync(
    "遅れて閉じるウィンドウは HWND 消滅後にだけ成功する",
    initiallyLive: true,
    requestAccepted: true,
    pollsUntilClosed: 3,
    ManagedWindowCloseStatus.Closed,
    expectedCloseRequests: 1);

await VerifyAsync(
    "待機期限ちょうどに閉じた HWND も終了済みと判定する",
    initiallyLive: true,
    requestAccepted: true,
    pollsUntilClosed: 4,
    ManagedWindowCloseStatus.Closed,
    expectedCloseRequests: 1);

await VerifyAsync(
    "閉じずに残るウィンドウはタイムアウトになり成功扱いしない",
    initiallyLive: true,
    requestAccepted: true,
    pollsUntilClosed: null,
    ManagedWindowCloseStatus.TimedOut,
    expectedCloseRequests: 1);

VscodeWorkspaceStateTests.Run(failures);
VscodeUserSettingsTests.Run(failures);
AppConfigTests.Run(failures);
WindowSlotCompactIdeToggleTests.Run(failures);
PanelOrderTests.Run(failures);
GitHubRepositoryResolverTests.Run(failures);
WindowFocusSynchronizerTests.Run(failures);
SystemMonitorAndCardTitleTests.Run(failures);
await FolderUsageCalculationTests.RunAsync(failures);
await VscodeProcessRecoveryTests.RunAsync(failures);
UserDataReclaimTests.Run(failures);
await CacheStorageReclaimTests.RunAsync(failures);
DiskCleanupTests.Run(failures);
MemoryWarningTests.Run(failures);
TitleBarIndicatorTests.Run(failures);
DataFolderDiagnosticsTests.Run(failures);
await AiActivityTests.RunAsync(failures);
AiHookLocationTests.Run(failures);
AiOrbitGlowTests.Run(failures);
AiDoneFireflyTests.Run(failures);
await PublishStopPanelTests.RunAsync(failures);
await LaunchPresentationTests.RunAsync(failures);
MinimizeAnimationTests.Run(failures);
RestoreAnimationTests.Run(failures);
MicroDialTests.Run(failures);
await FocusSwitchBackgroundRestoreTests.RunAsync(failures);
await ForegroundActivationTests.RunAsync(failures);

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("Managed window close regression checks passed (5/5).");
Console.WriteLine("VS Code workspace title matching regression checks passed (21/21).");
Console.WriteLine("VS Code 設定保持の回帰チェックに合格しました (22/22)。");
Console.WriteLine("Application configuration migration regression checks passed (4/4).");
Console.WriteLine("Compact IDE toggle regression checks passed (4/4).");
Console.WriteLine("Panel ordering and persistence regression checks passed (10/10).");
Console.WriteLine($"GitHub URL 解決の回帰チェックに合格しました ({GitHubRepositoryResolverTests.Count}/{GitHubRepositoryResolverTests.Count})。");
Console.WriteLine("ウィンドウ直接操作のフォーカス同期チェックに合格しました (12/12)。");
Console.WriteLine($"メインカードタイトル視認性＆システムモニタ回帰チェックに合格しました ({SystemMonitorAndCardTitleTests.Count}/{SystemMonitorAndCardTitleTests.Count})。");
Console.WriteLine("巨大フォルダ・ドライブの使用量計測チェックに合格しました (11/11)。");
Console.WriteLine($"VS Code 終了・復旧の安全性チェックに合格しました ({VscodeProcessRecoveryTests.Count}/{VscodeProcessRecoveryTests.Count})。");
Console.WriteLine("VS Code user-data 回収のリンク保護チェックに合格しました (3/3)。");
Console.WriteLine($"CacheStorage の安全な自動回収チェックに合格しました ({CacheStorageReclaimTests.Count}/{CacheStorageReclaimTests.Count})。");
Console.WriteLine("ディスク整理・ページファイル表示のチェックに合格しました (26/26)。");
Console.WriteLine($"メモリ警告・アイコン色の整合チェックに合格しました ({MemoryWarningTests.Count}/{MemoryWarningTests.Count})。");
Console.WriteLine($"タイトルバーの状態表示・控えのフォルダ変更のチェックに合格しました ({TitleBarIndicatorTests.Count}/{TitleBarIndicatorTests.Count})。");
Console.WriteLine($"容量の解析（診断コード）のチェックに合格しました ({DataFolderDiagnosticsTests.Count}/{DataFolderDiagnosticsTests.Count})。");
Console.WriteLine($"AI の作業通知（状態・スロット特定・フック設定）のチェックに合格しました ({AiActivityTests.Count}/{AiActivityTests.Count})。");
Console.WriteLine($"AI フックの保存先・閲覧チェックに合格しました ({AiHookLocationTests.Count}/{AiHookLocationTests.Count})。");
Console.WriteLine($"AI 作業中 LED 周回エフェクトのチェックに合格しました ({AiOrbitGlowTests.Count}/{AiOrbitGlowTests.Count})。");
Console.WriteLine($"AI 完了ホタル点滅・IDE/CLI 枠切り抜きのチェックに合格しました ({AiDoneFireflyTests.Count}/{AiDoneFireflyTests.Count})。");
Console.WriteLine($"配布ビルド前のアプリ自動終了チェックに合格しました ({PublishStopPanelTests.Count}/{PublishStopPanelTests.Count})。");
Console.WriteLine($"起動演出・ステルス起動・一斉表示のチェックに合格しました ({LaunchPresentationTests.Count}/{LaunchPresentationTests.Count})。");
Console.WriteLine($"最小化のタスクバー吸い込み演出のチェックに合格しました ({MinimizeAnimationTests.Count}/{MinimizeAnimationTests.Count})。");
Console.WriteLine($"最小化からの復元演出のチェックに合格しました ({RestoreAnimationTests.Count}/{RestoreAnimationTests.Count})。");
Console.WriteLine($"極小表示（円形オービット・ダイヤル）のチェックに合格しました ({MicroDialTests.Count}/{MicroDialTests.Count})。");
Console.WriteLine($"フォーカス切替時の背面復元・Z順維持チェックに合格しました ({FocusSwitchBackgroundRestoreTests.Count}/{FocusSwitchBackgroundRestoreTests.Count})。");
Console.WriteLine($"フォアグラウンド前面化・ロック突破チェックに合格しました ({ForegroundActivationTests.Count}/{ForegroundActivationTests.Count})。");
return 0;

async Task VerifyAsync(
    string name,
    bool initiallyLive,
    bool requestAccepted,
    int? pollsUntilClosed,
    ManagedWindowCloseStatus expectedStatus,
    int expectedCloseRequests)
{
    var live = initiallyLive;
    var polls = 0;
    var closeRequests = 0;
    var service = new ManagedWindowCloseService(
        _ => live,
        _ =>
        {
            closeRequests++;
            return requestAccepted;
        },
        (_, _) =>
        {
            polls++;
            if (pollsUntilClosed.HasValue && polls >= pollsUntilClosed.Value)
            {
                live = false;
            }

            return Task.CompletedTask;
        },
        timeout: TimeSpan.FromMilliseconds(400),
        pollInterval: TimeSpan.FromMilliseconds(100));

    var result = await service.CloseAndWaitAsync(new IntPtr(42));
    if (result.Status != expectedStatus)
    {
        failures.Add($"{name}: expected={expectedStatus}, actual={result.Status}");
    }

    if (closeRequests != expectedCloseRequests)
    {
        failures.Add($"{name}: close requests expected={expectedCloseRequests}, actual={closeRequests}");
    }
}
