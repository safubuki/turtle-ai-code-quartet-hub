using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// 単体起動・アプリ切替・控え復帰・Launch Quartet 一括起動、および
// 一括閉じる・個別閉じるのすべてで共通利用する演出パイプライン。
//
// 起動: 対象象限へ完全不透明のダークガラスベール＋中央 HUD をふぁっと被せ、
//       裏側で順次起動・定位置先行配置・安定化を完了してから、一拍の間を置いて
//       A→B→C→D へ 500ms 間隔で本当に 1 つ 1 つ LED ランプが灯るように開く。
//       （他プロセスへの DWMWA_CLOAK が OS 制限で拒否される環境でも、Topmost 帯ベール＋
//       assignments 基準の制御により 100% 確実に安定化＆順番点灯ウェーブが実行される）
// 終了: 開いている全対象へ完全不透明（alpha=255）のベールを一斉に被せ、裏での終了タイミング差を
//       一切見せずに全員の終了を待ってから、全象限ベールと中央 HUD を同一クロック（520ms）で一斉に消す。
public partial class MainWindow
{
    private static readonly TimeSpan[] PreRevealSettleDelays =
    [
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(190),
        TimeSpan.FromMilliseconds(260)
    ];
    private static readonly TimeSpan PostCorrectionConfirmDelay = TimeSpan.FromMilliseconds(190);
    private const int MaxExtraSettlePasses = 2;

    // 通常の終了（0.3〜2.5 秒）は全員揃うまで完全不透明ベールの裏で待ち、
    // 本当に未保存確認ダイアログ等で止まった場合のみ 4.5 秒後にその象限のベールを救済解除する。
    private static readonly TimeSpan CloseVeilEarlyReleaseDelay = TimeSpan.FromMilliseconds(4500);

    private LaunchWindowCloakSession? _activeLaunchCloakSession;

    private async Task<IReadOnlyList<WindowAssignment>> ExecuteStagedLaunchAsync(
        IReadOnlyList<(WindowSlot Slot, LauncherApplication Application)> launchTargets,
        bool clearFocusAfterArrange,
        bool preserveExistingFocus,
        bool suppressStagingOverlay = false)
    {
        if (launchTargets.Count == 0)
        {
            return [];
        }

        var config = _statusStore.Config;
        var activeMonitor = GetActiveMonitorIndex();
        var isBatch = launchTargets.Count > 1;
        var failsafeSeconds = Math.Clamp(launchTargets.Count * config.LaunchTimeoutSeconds + 15, 25, 110);

        using var cloakSession = new LaunchWindowCloakSession(TimeSpan.FromSeconds(failsafeSeconds));
        _activeLaunchCloakSession = cloakSession;

        var showOverlay = !suppressStagingOverlay && IsVisible && WindowState != WindowState.Minimized;
        var hudMonitorIndex = isBatch
            ? activeMonitor
            : GetSlotMonitorIndex(launchTargets[0].Slot);
        var panelHandle = new WindowInteropHelper(this).Handle;
        var headline = isBatch
            ? "QUARTET LAUNCHING"
            : $"LAUNCHING SLOT {launchTargets[0].Slot.Name}";
        var initialMessage = isBatch
            ? $"4面のワークスペースを裏で起動・整列しています... ({BuildLaunchPlanSummary(launchTargets)})"
            : $"スロット{launchTargets[0].Slot.Name}の{launchTargets[0].Application.DisplayName}を準備しています...";

        var assignments = new List<WindowAssignment>();
        var revealed = false;

        try
        {
            if (showOverlay)
            {
                var stageEntries = BuildLaunchStageEntries(launchTargets);
                await _launchStagingOverlay.BeginForLaunchAsync(
                    stageEntries,
                    _statusStore.Slots,
                    config.Gap,
                    activeMonitor,
                    hudMonitorIndex,
                    panelHandle,
                    headline,
                    initialMessage,
                    animate: SystemParameters.ClientAreaAnimation,
                    failsafeTimeout: TimeSpan.FromSeconds(failsafeSeconds + 15));
            }

            for (var index = 0; index < launchTargets.Count; index++)
            {
                var (slot, application) = launchTargets[index];
                var stepMessage = isBatch
                    ? $"スロット{slot.Name}の{application.DisplayName}を起動しています... ({index + 1}/{launchTargets.Count})"
                    : $"スロット{slot.Name}の{application.DisplayName}を起動しています...";

                _statusStore.Message = stepMessage;
                _launchStagingOverlay.UpdateSlotState(slot.Name, LaunchStageSlotState.Launching, stepMessage);
                DiagnosticLog.Write(
                    $"Staged launch starting slot {slot.Name} with {application.DisplayName} ({index + 1}/{launchTargets.Count}).");

                // 他スロットに既に割り当て済みの HWND は共有プロファイル再接続の対象から除外する。
                var assignedOtherHandles = _statusStore.Slots
                    .Where(other => !ReferenceEquals(other, slot) && other.WindowHandle != IntPtr.Zero)
                    .Select(other => other.WindowHandle)
                    .ToList();

                var slotAssignments = await _applicationLauncher.LaunchMissingAsync(
                    new[] { slot },
                    config,
                    application,
                    CancellationToken.None,
                    cloakSession,
                    assignedOtherHandles);

                if (slotAssignments.Count == 0)
                {
                    _launchStagingOverlay.UpdateSlotState(
                        slot.Name,
                        LaunchStageSlotState.Failed,
                        $"スロット{slot.Name}の{application.DisplayName}を確認できませんでした。");
                }
                else
                {
                    foreach (var assignment in slotAssignments)
                    {
                        _statusStore.AssignWindow(assignment.Slot, assignment.Window);
                        assignments.Add(assignment);

                        // 捕捉直後にベールの真下の定位置へ先行配置しておくことで、後続スロットの起動中にも
                        // 正しい 2x2 サイズで初回描画と位置安定を進められる。
                        if (!suppressStagingOverlay)
                        {
                            _ = _windowArranger.ArrangeOnly(
                                _statusStore.Slots,
                                new[] { assignment.Window.Handle },
                                config.Gap,
                                activeMonitor);
                        }
                    }

                    var readyMsg = isBatch
                        ? $"スロット{slot.Name}の準備が完了しました ({index + 1}/{launchTargets.Count})"
                        : $"スロット{slot.Name}の配置を整えています...";
                    _launchStagingOverlay.UpdateSlotState(slot.Name, LaunchStageSlotState.Ready, readyMsg);
                }

                if (index < launchTargets.Count - 1)
                {
                    await Task.Delay(LaunchAllSlotPaceDelay);
                }
            }

            await RefreshSlotsAsync(allowDuringBusy: true);

            if (suppressStagingOverlay)
            {
                cloakSession.ReleaseAllAdopted();
                revealed = true;
                return assignments;
            }

            if (clearFocusAfterArrange)
            {
                _statusStore.ClearFocusedSlot();
            }

            // 全ウィンドウをベールの裏側でアニメーション無しに正確な 2x2 セルへ整列する。
            if (preserveExistingFocus && FocusedSlots().Any())
            {
                _ = _windowArranger.Arrange(_statusStore.Slots, config.Gap, activeMonitor, animateRestore: false);
                ReassertAllFocusedSlots();
            }
            else
            {
                _ = _windowArranger.Arrange(_statusStore.Slots, config.Gap, activeMonitor, animateRestore: false);
            }

            // ベール解除の瞬間に管理外アプリ（ブラウザ等）の裏へ隠れないよう、
            // 起動した全ウィンドウを通常帯の最上位（Topmost 帯ベールの真下）へ整える。
            foreach (var assignment in assignments)
            {
                var slotMonitor = GetSlotMonitorIndex(assignment.Slot);
                var focusedOnSameDisplay = preserveExistingFocus
                    ? GetFocusedSlotOnMonitor(slotMonitor)
                    : null;
                if (focusedOnSameDisplay is not null && !ReferenceEquals(focusedOnSameDisplay, assignment.Slot))
                {
                    _ = _windowArranger.PlaceDirectlyBehind(assignment.Window.Handle, focusedOnSameDisplay.WindowHandle);
                }
                else
                {
                    _ = _windowArranger.BringToFrontWithoutTopmost(assignment.Window.Handle);
                }
            }

            // 他プロセスへの DWMWA_CLOAK が OS 仕様で効かない場合も、assignments（全起動成功窓）を
            // 基準に必ず裏での配置安定化と「ふぁ〜っ…」LED 順番点灯ウェーブを実行する。
            var launchedHandles = assignments
                .Select(item => item.Window.Handle)
                .Where(handle => handle != IntPtr.Zero)
                .Distinct()
                .ToList();

            if (launchedHandles.Count > 0)
            {
                _launchStagingOverlay.UpdateStatusMessage(
                    isBatch
                        ? "全ウィンドウの配置と描画を整えています..."
                        : $"スロット{launchTargets[0].Slot.Name}の配置を整えています...");

                await WaitForLaunchedWindowsToSettleAsync(launchedHandles, config.Gap, activeMonitor);

                var slotHandleByName = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                var orderedRevealSlots = new List<string>();
                foreach (var assignment in assignments)
                {
                    slotHandleByName[assignment.Slot.Name] = assignment.Window.Handle;
                    orderedRevealSlots.Add(assignment.Slot.Name);
                }

                var readyHeadline = isBatch ? "QUARTET READY" : $"SLOT {launchTargets[0].Slot.Name} READY";
                var readyMessage = isBatch
                    ? $"{orderedRevealSlots.Count}個のワークスペースを1つずつ点灯表示します"
                    : $"スロット{launchTargets[0].Slot.Name}を表示します";

                await _launchStagingOverlay.RevealSequentiallyAndCloseAsync(
                    orderedRevealSlots,
                    slotName =>
                    {
                        if (slotHandleByName.TryGetValue(slotName, out var handle))
                        {
                            _ = cloakSession.ReleaseAdopted(handle);
                        }
                    },
                    () =>
                    {
                        cloakSession.ReleaseAllAdopted();
                        revealed = true;
                    },
                    readyHeadline,
                    readyMessage);
            }
            else
            {
                cloakSession.ReleaseAllAdopted();
                revealed = true;
                await _launchStagingOverlay.DismissAsync();
            }

            // ベールが晴れたあとで、通常のレイヤー設定（最前面／最背面）と遅延再配置保険（8s/12s/30s）を適用する。
            if (preserveExistingFocus && FocusedSlots().Any())
            {
                ApplyManagedWindowLayers(bringPanelAfterChange: true);
                ReassertAllFocusedSlots();
                _ = SettleArrangementPreservingFocusAsync();
            }
            else
            {
                ApplyManagedWindowLayers(bringPanelAfterChange: true);
                SchedulePostLaunchArrangeRetries(bringPanelAfterArrange: true);
            }

            RefreshAuxiliaryUi();
            return assignments;
        }
        finally
        {
            if (!revealed)
            {
                cloakSession.ReleaseAllAdopted();
            }

            _launchStagingOverlay.DismissImmediate();
            if (ReferenceEquals(_activeLaunchCloakSession, cloakSession))
            {
                _activeLaunchCloakSession = null;
            }
        }
    }

    // 「すべて閉じる」および個別スロット「閉じる」の共通演出パイプライン。
    // 開いているすべての対象ウィンドウへ完全不透明（alpha=255）のベールを一斉に被せ、
    // 裏での個別終了タイミング差を一切見せずに全終了を待ってから、全ベールと HUD を一斉にバッと消す。
    private async Task<IReadOnlyList<(WindowSlot Slot, IntPtr Handle, ManagedWindowCloseResult Result)>> ExecuteStagedCloseAsync(
        IReadOnlyList<WindowSlot> targetSlots,
        bool saveSettingsBeforeClose,
        bool clearAllFocus)
    {
        var closeTargets = targetSlots
            .Where(slot => slot.WindowHandle != IntPtr.Zero)
            .Select(slot => (Slot: slot, Handle: slot.WindowHandle))
            .ToList();
        if (closeTargets.Count == 0)
        {
            return [];
        }

        var config = _statusStore.Config;
        var activeMonitor = GetActiveMonitorIndex();
        var isBatch = closeTargets.Count > 1;
        var panelHandle = new WindowInteropHelper(this).Handle;

        var veilTargets = BuildCloseVeilTargets(closeTargets, config.Gap, activeMonitor);
        var showOverlay = !_areWindowsHidden
            && veilTargets.Count > 0
            && IsVisible
            && WindowState != WindowState.Minimized;

        var headline = isBatch
            ? "QUARTET CLOSING"
            : $"CLOSING SLOT {closeTargets[0].Slot.Name}";
        var initialMessage = isBatch
            ? $"{closeTargets.Count}個のワークスペースを裏で終了しています..."
            : $"スロット{closeTargets[0].Slot.Name}の{closeTargets[0].Slot.ApplicationDisplayName}を閉じています...";

        var transitionsDisabledHandles = new List<IntPtr>(closeTargets.Count);
        var closedCount = 0;
        var results = new List<(WindowSlot Slot, IntPtr Handle, ManagedWindowCloseResult Result)>();

        try
        {
            if (showOverlay)
            {
                var stageEntries = BuildCloseStageEntries(closeTargets);
                var hudMonitor = isBatch ? activeMonitor : GetSlotMonitorIndex(closeTargets[0].Slot);
                await _launchStagingOverlay.BeginCoverForCloseAsync(
                    stageEntries,
                    veilTargets,
                    hudMonitor,
                    panelHandle,
                    headline,
                    initialMessage,
                    animate: SystemParameters.ClientAreaAnimation,
                    failsafeTimeout: TimeSpan.FromSeconds(30));
            }

            if (saveSettingsBeforeClose)
            {
                _statusStore.SaveCurrentSettings();
            }
            else
            {
                foreach (var target in closeTargets)
                {
                    _statusStore.CaptureWorkspacePath(target.Slot);
                }
            }

            if (clearAllFocus)
            {
                _statusStore.ClearFocusedSlot();
            }
            else
            {
                foreach (var target in closeTargets)
                {
                    ClearFocusedSlotForDisplay(GetSlotMonitorIndex(target.Slot));
                }
            }

            foreach (var target in closeTargets)
            {
                _windowArranger.ReleaseTopmost(target.Handle);
                if (showOverlay && WindowArranger.SetTransitionsDisabled(target.Handle, true))
                {
                    transitionsDisabledHandles.Add(target.Handle);
                }
            }

            var tasks = closeTargets.Select(async target =>
            {
                using var earlyReleaseCts = new CancellationTokenSource();
                if (showOverlay)
                {
                    _ = ReleaseCloseVeilIfSlowAsync(target.Slot.Name, earlyReleaseCts.Token);
                }

                var result = await _managedWindowCloseService.CloseAndWaitAsync(target.Handle);
                earlyReleaseCts.Cancel();

                if (result.Succeeded)
                {
                    if (target.Slot.WindowHandle == target.Handle)
                    {
                        target.Slot.IsHidden = false;
                        _statusStore.ClearWindow(target.Slot);
                    }

                    var doneSoFar = Interlocked.Increment(ref closedCount);
                    if (showOverlay)
                    {
                        _launchStagingOverlay.UpdateSlotState(
                            target.Slot.Name,
                            LaunchStageSlotState.Closed,
                            isBatch
                                ? $"終了処理を完了しています... ({doneSoFar}/{closeTargets.Count})"
                                : $"スロット{target.Slot.Name}を終了しました");
                    }
                }
                else
                {
                    DiagnosticLog.Write(
                        LogLevel.Warn,
                        $"Managed window close did not complete for slot {target.Slot.Name}: "
                        + $"handle=0x{target.Handle.ToInt64():X}, status={result.Status}.");
                    _ = WindowArranger.SetTransitionsDisabled(target.Handle, false);
                    if (showOverlay)
                    {
                        _launchStagingOverlay.ReleaseSlotVeilEarly(target.Slot.Name);
                        _launchStagingOverlay.UpdateSlotState(
                            target.Slot.Name,
                            LaunchStageSlotState.Failed,
                            $"スロット{target.Slot.Name}は確認待ちのため終了できませんでした");
                    }
                }

                return (target.Slot, target.Handle, result);
            }).ToList();

            results.AddRange(await Task.WhenAll(tasks));

            RefreshHiddenWindowsState();
            UpdateVisibilityButtonVisual();

            if (showOverlay)
            {
                var allSucceeded = results.All(item => item.Result.Succeeded);
                if (allSucceeded)
                {
                    var doneHeadline = isBatch ? "QUARTET CLOSED" : $"SLOT {closeTargets[0].Slot.Name} CLOSED";
                    var doneMessage = isBatch
                        ? $"{results.Count}個のワークスペースをすべて終了しました"
                        : $"スロット{closeTargets[0].Slot.Name}を閉じました";
                    // 全員の終了確認が揃ってから、全象限ベールを一斉にバッと消す。
                    await _launchStagingOverlay.VanishAllAndCloseAsync(doneHeadline, doneMessage);
                }
                else
                {
                    await _launchStagingOverlay.DismissAsync();
                }
            }

            return results;
        }
        finally
        {
            foreach (var handle in transitionsDisabledHandles)
            {
                _ = WindowArranger.SetTransitionsDisabled(handle, false);
            }

            _launchStagingOverlay.DismissImmediate();
        }
    }

    private async Task ReleaseCloseVeilIfSlowAsync(string slotName, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CloseVeilEarlyReleaseDelay, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested && _launchStagingOverlay.IsActive)
                {
                    _launchStagingOverlay.ReleaseSlotVeilEarly(slotName);
                    _launchStagingOverlay.UpdateStatusMessage($"スロット{slotName}の確認ダイアログを待機しています...");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Normal close finished within the grace window.
        }
    }

    private IReadOnlyList<StagingVeilTarget> BuildCloseVeilTargets(
        IReadOnlyList<(WindowSlot Slot, IntPtr Handle)> closeTargets,
        int gap,
        int activeMonitor)
    {
        var targets = new List<StagingVeilTarget>();
        var fullScreenCoveredMonitors = new HashSet<int>();

        foreach (var (slot, handle) in closeTargets)
        {
            if (slot.IsHidden || _windowArranger.IsMinimized(handle))
            {
                continue;
            }

            var isMaximized = slot.IsFocused || _windowArranger.GetWindowState(handle) == ManagedWindowState.Maximized;
            if (!isMaximized)
            {
                continue;
            }

            var monitorIndex = GetSlotMonitorIndex(slot);
            if (fullScreenCoveredMonitors.Add(monitorIndex)
                && _windowArranger.TryGetMonitorWorkArea(monitorIndex, out var workArea))
            {
                targets.Add(new StagingVeilTarget(
                    slot.Name,
                    slot.DisplayTitle,
                    slot.ApplicationDisplayName,
                    workArea,
                    LaunchStageSlotState.Closing,
                    monitorIndex));
            }
        }

        foreach (var (slot, handle) in closeTargets)
        {
            if (slot.IsHidden || _windowArranger.IsMinimized(handle))
            {
                continue;
            }

            var monitorIndex = GetSlotMonitorIndex(slot);
            if (fullScreenCoveredMonitors.Contains(monitorIndex))
            {
                continue;
            }

            var slotIndex = _statusStore.Slots.IndexOf(slot);
            WindowArranger.WindowBounds bounds;
            if (slotIndex >= 0
                && _windowArranger.TryGetSlotCellBounds(slot, slotIndex, gap, activeMonitor, out var cellBounds))
            {
                bounds = cellBounds;
            }
            else if (_windowArranger.TryGetWindowBounds(handle, out var windowBounds))
            {
                bounds = windowBounds;
            }
            else
            {
                continue;
            }

            targets.Add(new StagingVeilTarget(
                slot.Name,
                slot.DisplayTitle,
                slot.ApplicationDisplayName,
                bounds,
                LaunchStageSlotState.Closing,
                monitorIndex));
        }

        return targets;
    }

    private IReadOnlyList<LaunchStageSlotEntry> BuildCloseStageEntries(
        IReadOnlyList<(WindowSlot Slot, IntPtr Handle)> closeTargets)
    {
        var targetNames = new HashSet<string>(closeTargets.Select(item => item.Slot.Name), StringComparer.OrdinalIgnoreCase);
        var entries = new List<LaunchStageSlotEntry>(_statusStore.Slots.Count);

        for (var index = 0; index < _statusStore.Slots.Count; index++)
        {
            var slot = _statusStore.Slots[index];
            var isTarget = targetNames.Contains(slot.Name);
            var appName = _statusStore.FindApplication(slot.ApplicationId)?.ShortName ?? slot.ApplicationDisplayName;
            var state = isTarget
                ? LaunchStageSlotState.Closing
                : slot.WindowStatus == SlotWindowStatus.Ready
                    ? LaunchStageSlotState.AlreadyRunning
                    : LaunchStageSlotState.Closed;

            entries.Add(new LaunchStageSlotEntry(
                slot.Name,
                index,
                slot.DisplayTitle,
                appName,
                state,
                isTarget));
        }

        return entries;
    }

    private IReadOnlyList<LaunchStageSlotEntry> BuildLaunchStageEntries(
        IReadOnlyList<(WindowSlot Slot, LauncherApplication Application)> launchTargets)
    {
        var targetMap = launchTargets.ToDictionary(item => item.Slot.Name, item => item.Application, StringComparer.OrdinalIgnoreCase);
        var entries = new List<LaunchStageSlotEntry>(_statusStore.Slots.Count);

        for (var index = 0; index < _statusStore.Slots.Count; index++)
        {
            var slot = _statusStore.Slots[index];
            var isTarget = targetMap.TryGetValue(slot.Name, out var targetApp);
            var appName = targetApp?.ShortName
                ?? _statusStore.FindApplication(slot.ApplicationId)?.ShortName
                ?? slot.ApplicationDisplayName;
            var state = isTarget
                ? LaunchStageSlotState.Pending
                : slot.WindowStatus == SlotWindowStatus.Ready
                    ? LaunchStageSlotState.AlreadyRunning
                    : LaunchStageSlotState.Failed;

            entries.Add(new LaunchStageSlotEntry(
                slot.Name,
                index,
                slot.DisplayTitle,
                appName,
                state,
                isTarget));
        }

        return entries;
    }

    // ベールの裏で起動直後のウィンドウが「前回位置への自己復元」でズレていないか確認し、
    // ズレていれば見えない裏側のまま静かに押し戻して安定させてからウェーブ点灯へ進む。
    private async Task WaitForLaunchedWindowsToSettleAsync(
        IReadOnlyCollection<IntPtr> targetHandles,
        int gap,
        int activeMonitor)
    {
        if (targetHandles.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var correctedDuringSettle = false;

        foreach (var delay in PreRevealSettleDelays)
        {
            await Task.Delay(delay);
            if (_areWindowsHidden || WindowState == WindowState.Minimized)
            {
                return;
            }

            if (_windowArranger.NeedsArrangeOnly(_statusStore.Slots, targetHandles, gap, activeMonitor, tolerance: 20))
            {
                _ = _windowArranger.ArrangeOnly(_statusStore.Slots, targetHandles, gap, activeMonitor);
                correctedDuringSettle = true;
            }
        }

        for (var extraPass = 0; extraPass < MaxExtraSettlePasses && correctedDuringSettle; extraPass++)
        {
            correctedDuringSettle = false;
            await Task.Delay(PostCorrectionConfirmDelay);
            if (_areWindowsHidden || WindowState == WindowState.Minimized)
            {
                return;
            }

            if (_windowArranger.NeedsArrangeOnly(_statusStore.Slots, targetHandles, gap, activeMonitor, tolerance: 20))
            {
                _ = _windowArranger.ArrangeOnly(_statusStore.Slots, targetHandles, gap, activeMonitor);
                correctedDuringSettle = true;
            }
        }

        DiagnosticLog.Write(
            $"Staged launch settle completed for {targetHandles.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms.");
    }

    private void CleanupLaunchPresentationOnClose()
    {
        CompleteStartupRevealImmediately();
        try
        {
            _activeLaunchCloakSession?.ReleaseAllAdopted();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        _launchStagingOverlay.Dispose();
    }
}
